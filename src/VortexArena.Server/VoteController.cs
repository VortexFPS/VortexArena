using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The in-match call-vote system — a faithful Godot-free port of server/command/vote.qc
/// (<c>VoteCommand_*</c> / <c>VoteCount</c> / <c>VoteStop</c> / <c>VoteAccept</c> / <c>VoteReject</c> /
/// <c>VoteTimeout</c> + the parse/whitelist/master subsystem). A player calls a vote on a whitelisted command
/// (<c>sv_vote_commands</c>), connected clients cast yes/no/abstain, and the vote passes when the yes-majority
/// threshold is reached, is rejected when passing becomes impossible, or times out — running the parsed command
/// on a pass. A vote master (password login or a won master-vote) can run any allowed command directly and stop
/// any vote.
///
/// Distinct from <see cref="MapVoting"/> (the end-of-match map ballot). It is driven through
/// <see cref="Commands"/>'s <c>vote</c> command via <see cref="Execute"/>. The networked vote HUD (the QC
/// Nagger entity) and the announcer/centerprint wording are presentation and live in the host; this core
/// carries the events (broadcast text via <see cref="Broadcast"/>).
/// </summary>
public sealed class VoteController
{
    // ---- vote status (QC VOTE_NULL/NORMAL/MASTER) ----
    public const int StatusNull = 0, StatusNormal = 1, StatusMaster = 2;
    // ---- per-player selection (QC VOTE_SELECT_*) ----
    public const int SelectAbstain = -2, SelectReject = -1, SelectNull = 0, SelectAccept = 1;

    /// <summary>QC <c>sv_vote_timeout</c> default (commands.cfg: 24).</summary>
    public const float DefaultTimeout = 24f;

    private readonly Dictionary<Player, int> _selection = new();  // QC .vote_selection
    private readonly HashSet<Player> _masters = new();            // QC .vote_master
    private readonly Dictionary<Player, float> _waitTime = new(); // QC .vote_waittime
    private Func<int> _voterCount = static () => 0;

    /// <summary>QC <c>vote_called</c>: VOTE_NULL / VOTE_NORMAL / VOTE_MASTER.</summary>
    public int Status { get; private set; }

    /// <summary>True while a vote is open (QC <c>vote_called</c> != VOTE_NULL).</summary>
    public bool Active => Status != StatusNull;

    /// <summary>The parsed command to run on a pass (QC <c>vote_called_command</c>), or "" when idle.</summary>
    public string Command { get; private set; } = "";

    /// <summary>The display string shown to players (QC <c>vote_called_display</c>).</summary>
    public string Display { get; private set; } = "";

    /// <summary>The player who called the vote (QC <c>vote_caller</c>), or null for a console-called vote.</summary>
    public Player? Caller { get; private set; }

    /// <summary>Absolute sim time the vote auto-closes (QC <c>vote_endtime</c>); 0 when idle.</summary>
    public float EndTime { get; private set; }

    /// <summary>Yes / no / abstain tallies + the computed overall threshold (QC the vote_*_count globals).</summary>
    public int YesCount { get; private set; }
    public int NoCount { get; private set; }
    public int AbstainCount { get; private set; }
    public int NeededOverall { get; private set; }

    /// <summary>Fired with the winning command string when a vote passes (the host runs it via the command bus).</summary>
    public Action<string>? VotePassed { get; set; }

    /// <summary>Broadcast sink for the bprint lines (QC bprint). The host wires it to the chat/console broadcast.</summary>
    public Action<string>? Broadcast { get; set; }

    /// <summary>QC <c>Send_Notification(NOTIF_ALL, NULL, MSG_ANNCE, ANNCE_VOTE_CALL)</c> (vote.qc:1073): the "vote
    /// called" announcer cue, played when a vote opens AND more than one real player is on the server. Host-wires
    /// to the announcer channel.</summary>
    public Action? AnnounceVoteCall { get; set; }

    /// <summary>QC <c>Send_Notification(NOTIF_ALL, NULL, MSG_ANNCE, ANNCE_VOTE_ACCEPT)</c> (VoteAccept, vote.qc:181):
    /// the "vote passed" announcer cue. Host-wires to the announcer channel.</summary>
    public Action? AnnounceVoteAccept { get; set; }

    /// <summary>QC <c>Send_Notification(NOTIF_ALL, NULL, MSG_ANNCE, ANNCE_VOTE_FAIL)</c> (VoteReject/VoteTimeout,
    /// vote.qc:188/195): the "vote failed" announcer cue (shared by reject + timeout). Host-wires to the
    /// announcer channel.</summary>
    public Action? AnnounceVoteFail { get; set; }

    /// <summary>The connected real clients (QC FOREACH_CLIENT(IS_REAL_CLIENT)). Wired by the host for counting.</summary>
    public Func<IEnumerable<Player>>? Roster { get; set; }

    /// <summary>Resolve a player by name/#index for kick/moveto votes (host-wired). Null = unresolved.</summary>
    public Func<string, Player?>? FindPlayer { get; set; }

    /// <summary>QC IS_PLAYER predicate — is this client an in-game player (vs spectator)? Host-wired (default true).</summary>
    public Func<Player, bool> IsPlayer { get; set; } = static _ => true;

    /// <summary>QC <c>INGAME(it)</c> — a client queued/joined this round (counts toward the real-player tally even
    /// while not yet IS_PLAYER). Host-wired (default false, so the union reduces to IsPlayer until wired).</summary>
    public Func<Player, bool> InGame { get; set; } = static _ => false;

    /// <summary>QC <c>warmup_stage || intermission_running</c> — relaxes the spectator gate. Host-wired.</summary>
    public Func<bool> WarmupOrIntermission { get; set; } = static () => false;

    /// <summary>QC <c>timeout_status</c> != INACTIVE — a timeout is pending/active. While true, only a
    /// literal <c>timein</c> vote may be called (QC vote.qc:1022). Host-wires to <c>TimeoutController.Active</c>.</summary>
    public Func<bool> TimeoutActive { get; set; } = static () => false;

    /// <summary>QC <c>game_starttime</c> — absolute sim time the match begins; before it, calling a vote is
    /// rejected unless <c>sv_vote_gamestart</c> (QC vote.qc:1009). Host-wires to <c>GameWorld.GameStartTime</c>.</summary>
    public Func<float> GameStartTime { get; set; } = static () => 0f;

    /// <summary>QC <c>IS_CLIENT</c> — is this caller a fully connected client (vs. still spawning)? Host-wired
    /// (default true; the port's caller is generally already a connected client).</summary>
    public Func<Player, bool> IsClient { get; set; } = static _ => true;

    /// <summary>QC <c>warmup_stage</c> — true during warmup only. Gates the <c>allready</c> vote (rejected once
    /// the match has started, vote.qc:913). Host-wires to <c>Warmup.WarmupStage</c>.</summary>
    public Func<bool> WarmupStage { get; set; } = static () => false;

    /// <summary>QC <c>ValidateMap</c> (vote.qc): normalize + validate a map/nextmap vote argument
    /// (<c>MapInfo_FixName</c> + the <c>sv_vote_override_mostrecent</c> recent-map block + <c>MapInfo_CheckMap</c>
    /// gametype-support gate). Returns the validated map name, or null with the rejection reason in
    /// <c>error</c>. <c>fromConsole</c> (caller is server console) bypasses the recent-map block, like QC's
    /// <c>caller</c> guard. Host-wires to the map catalog/rotation; default accepts a non-empty name.</summary>
    public delegate string? ValidateMapDelegate(string map, bool fromConsole, out string error);

    /// <summary>Host-wired map validator (see <see cref="ValidateMapDelegate"/>). Default: accept any non-empty name.</summary>
    public ValidateMapDelegate ValidateMap { get; set; } =
        static (string map, bool fromConsole, out string error) =>
        { error = ""; return string.IsNullOrEmpty(map) ? null : map; };

    /// <summary>Whether a player can become master (granted via login or a won master vote).</summary>
    public bool IsMaster(Player p) => _masters.Contains(p);

    /// <summary>
    /// QC <c>this.vote_master = true</c> auto-grant (server/client.qc:1262-1264): grant vote-master status to a
    /// player directly, without a login password or a won master-vote. Used by the host on connect to honor
    /// <c>sv_vote_master_ids</c> — a connecting client whose crypto idfp is listed in that cvar is silently made
    /// master. Idempotent (a re-grant is a no-op).
    /// </summary>
    public void GrantMaster(Player p) => _masters.Add(p);

    /// <summary>QC the FOREACH_CLIENT(IS_REAL_CLIENT) voter count source — kept for back-compat.</summary>
    public void SetVoterCountSource(Func<int> voterCount) => _voterCount = voterCount;

    // =============================================================================================
    // command dispatch (QC VoteCommand + the VOTE_COMMANDS table)
    // =============================================================================================

    /// <summary>
    /// QC <c>VoteCommand</c>: dispatch a <c>vote &lt;sub&gt; ...</c> command. <paramref name="ctx"/> carries the
    /// argv (argv(0)="vote", argv(1)=sub) and the caller (null = server console). Output goes to the caller via
    /// <see cref="CommandContext.Print"/>; broadcasts go through <see cref="Broadcast"/>. Returns true (consumed).
    /// </summary>
    public bool Execute(CommandContext ctx)
    {
        string sub = ctx.Arg(1).ToLowerInvariant();
        switch (sub)
        {
            case "call": CmdCall(ctx); break;
            case "yes": CmdSelect(ctx, SelectAccept); break;
            case "no": CmdNo(ctx); break;
            case "abstain": CmdSelect(ctx, SelectAbstain); break;
            case "stop": CmdStop(ctx); break;
            case "status": CmdStatus(ctx); break;
            case "master": CmdMaster(ctx); break;
            case "help":
            case "":
                ctx.Print("vote commands: call, yes, no, abstain, stop, status, master, help");
                ctx.Print("votable: " + Cvars.String("sv_vote_commands"));
                break;
            default:
                ctx.Print($"Unknown vote command \"{ctx.Arg(1)}\". Try: vote help");
                break;
        }
        return true;
    }

    private void CmdCall(CommandContext ctx)
    {
        Player? caller = ctx.Caller;

        if (caller is not null && Bans.PlayerInList(caller, "g_voteban_list"))
        { ctx.Print("^1You are banned from calling a vote."); return; }
        if (!Cvars.Bool("sv_vote_call") && caller is not null)
        { ctx.Print("^1Vote calling is not allowed."); return; }
        // QC: !sv_vote_gamestart && time < game_starttime — no votes before the match starts (vote.qc:1009).
        if (!Cvars.Bool("sv_vote_gamestart") && Now < GameStartTime())
        { ctx.Print("^1Vote calling is not allowed before the match has started."); return; }
        if (Active)
        { ctx.Print("^1There is already a vote called."); return; }
        if (!SpectatorsAllowed && caller is not null && !IsPlayer(caller))
        { ctx.Print("^1Only players can call a vote."); return; }
        // QC: caller && !IS_CLIENT(caller) — only fully connected clients may vote (vote.qc:1018).
        if (caller is not null && !IsClient(caller))
        { ctx.Print("^1Only connected clients can vote."); return; }

        string raw = ctx.ArgTail(2);
        // QC: timeout_status && vote_command != "timein" — no other vote may be called mid-timeout (vote.qc:1022).
        if (TimeoutActive() && !string.Equals(ctx.Arg(2), "timein", StringComparison.OrdinalIgnoreCase))
        { ctx.Print("^1You can not call a vote while a timeout is active."); return; }
        if (caller is not null && Now < WaitTimeOf(caller))
        { ctx.Print($"^1You have to wait ^2{MathF.Ceiling(WaitTimeOf(caller) - Now):0}^1 seconds before you can call a vote again."); return; }

        if (!CheckNasty(raw))
        { ctx.Print("^1Syntax error in command."); return; }

        int parse = Parse(ctx, raw, Cvars.String("sv_vote_commands"), 2, out string parsedCmd, out string parsedDisplay, out string parseError);
        if (parse <= 0)
        {
            if (parse == 0)
                ctx.Print(string.IsNullOrEmpty(parsedCmd)
                    ? "usage: vote call <command>"
                    : "^1This command is not acceptable or not available.");
            else if (!string.IsNullOrEmpty(parseError))
                ctx.Print(parseError);
            return;
        }

        // open the vote (QC the success block).
        Status = StatusNormal;
        Caller = caller;
        Command = parsedCmd;
        Display = parsedDisplay;
        EndTime = Now + Cvars.FloatOr("sv_vote_timeout", DefaultTimeout);
        _selection.Clear();
        if (caller is not null)
        {
            _selection[caller] = SelectAccept;        // caller auto-votes yes
            _waitTime[caller] = Now + Cvars.Float("sv_vote_wait");
        }

        // QC: count real clients (IS_REAL_CLIENT = connected, non-bot) before announcing (vote.qc:1062).
        int tmpPlayerCount = 0;
        foreach (Player it in Roster?.Invoke() ?? System.Array.Empty<Player>())
            if (!it.IsBot) tmpPlayerCount++;

        Broadcast?.Invoke($"^2* {CallerName(caller)} calls a vote for {Display}");
        Count(firstCount: true);

        // QC vote.qc:1072 — play ANNCE_VOTE_CALL only if the vote is still open (Count may have already resolved a
        // single-player vote) and more than one real player is present (so a solo caller doesn't hear their own cue).
        if (tmpPlayerCount > 1 && Active)
            AnnounceVoteCall?.Invoke();
    }

    private void CmdMaster(CommandContext ctx)
    {
        if (!Cvars.Bool("sv_vote_master"))
        { ctx.Print("^1Master control of voting is not allowed."); return; }
        Player? caller = ctx.Caller;
        string action = ctx.Arg(2).ToLowerInvariant();

        if (action == "login")
        {
            string pw = Cvars.String("sv_vote_master_password");
            if (string.IsNullOrEmpty(pw)) { ctx.Print("^1Login to vote master is not allowed."); return; }
            if (caller is null) { ctx.Print("^1Only clients can log in as master."); return; }
            if (IsMaster(caller)) { ctx.Print("^1You are already logged in as vote master."); return; }
            if (pw != ctx.Arg(3)) { ctx.Print($"Rejected vote master login from {CallerName(caller)}"); return; }
            _masters.Add(caller);
            ctx.Print($"Accepted vote master login from {CallerName(caller)}");
            Broadcast?.Invoke($"^2* {CallerName(caller)} logged in as ^3master");
            return;
        }

        if (action == "do")
        {
            if (caller is null || !IsMaster(caller)) { ctx.Print("^1You do not have vote master privileges."); return; }
            string raw = ctx.ArgTail(3);
            if (!CheckNasty(raw)) { ctx.Print("^1Syntax error in command."); return; }
            string list = Cvars.String("sv_vote_commands") + " " + Cvars.String("sv_vote_master_commands");
            int parse = Parse(ctx, raw, list, 3, out string parsedCmd, out string parsedDisplay, out string parseError);
            if (parse <= 0)
            {
                if (parse == 0) ctx.Print("^1This command is not acceptable or not available.");
                else if (!string.IsNullOrEmpty(parseError)) ctx.Print(parseError);
                return;
            }
            ctx.Print($"Executing command '{parsedDisplay}' on server.");
            Broadcast?.Invoke($"^2* {CallerName(caller)} used their ^3master^2 status to do \"{parsedDisplay}\".");
            VotePassed?.Invoke(parsedCmd);
            return;
        }

        // no action → call a vote to become master.
        if (!Cvars.Bool("sv_vote_master_callable")) { ctx.Print("^1Vote to become vote master is not allowed."); return; }
        if (Active) { ctx.Print("^1There is already a vote called."); return; }
        if (!SpectatorsAllowed && caller is not null && !IsPlayer(caller)) { ctx.Print("^1Only players can call a vote."); return; }
        // QC: timeout_status — a master vote cannot be called mid-timeout either (vote.qc:1170).
        if (TimeoutActive()) { ctx.Print("^1You can not call a vote while a timeout is active."); return; }

        Status = StatusMaster;
        Caller = caller;
        Command = "XXX"; // placeholder; a master vote grants status, it never runs a command
        Display = "^3master";
        EndTime = Now + Cvars.FloatOr("sv_vote_timeout", DefaultTimeout);
        _selection.Clear();
        if (caller is not null)
        {
            _selection[caller] = SelectAccept;
            _waitTime[caller] = Now + Cvars.Float("sv_vote_wait");
        }
        Broadcast?.Invoke($"^2* {CallerName(caller)} calls a vote to become ^3master");
        Count(firstCount: true);
    }

    private void CmdSelect(CommandContext ctx, int selection)
    {
        Player? voter = ctx.Caller;
        if (voter is null) { ctx.Print("^1Only clients can vote."); return; }
        if (Bans.PlayerInList(voter, "g_voteban_list")) { ctx.Print("^1You are banned from voting."); return; }
        if (!Active) { ctx.Print("^1No vote called."); return; }
        if (SelectionOf(voter) != SelectNull && !Cvars.Bool("sv_vote_change")) { ctx.Print("^1You have already voted."); return; }
        _selection[voter] = selection;
        ctx.Print(selection switch
        {
            SelectAccept => "^2You accepted the vote.",
            SelectReject => "^1You rejected the vote.",
            _ => "^3You abstained from your vote.",
        });
        if (!Cvars.Bool("sv_vote_singlecount"))
            Count(firstCount: false);
    }

    private void CmdNo(CommandContext ctx)
    {
        Player? voter = ctx.Caller;
        if (voter is not null && Active && ReferenceEquals(voter, Caller) && Cvars.Bool("sv_vote_no_stops_vote"))
        { Stop(voter); ctx.Print("^1You stopped your vote."); return; }
        CmdSelect(ctx, SelectReject);
    }

    private void CmdStop(CommandContext ctx)
    {
        if (!Active) { ctx.Print("^1No vote called."); return; }
        Player? caller = ctx.Caller;
        if (caller is null || ReferenceEquals(caller, Caller) || IsMaster(caller)) { Stop(caller); }
        else ctx.Print("^1You are not allowed to stop that vote.");
    }

    private void CmdStatus(CommandContext ctx)
    {
        if (Active) ctx.Print($"^7Vote for {Display}^7 called by ^7{CallerName(Caller)}^7.");
        else ctx.Print("^1No vote called.");
    }

    // =============================================================================================
    // legacy granular API (kept for back-compat with existing callers/tests)
    // =============================================================================================

    /// <summary>Open a vote directly on a pre-parsed command (server/console path; bypasses the whitelist).</summary>
    public bool Call(Player? caller, string command, float? timeout = null)
    {
        if (Active || string.IsNullOrWhiteSpace(command)) return false;
        Status = StatusNormal;
        Caller = caller;
        Command = command.Trim();
        Display = "^1" + Command;
        EndTime = Now + (timeout ?? Cvars.FloatOr("sv_vote_timeout", DefaultTimeout));
        _selection.Clear();
        if (caller is not null) { _selection[caller] = SelectAccept; _waitTime[caller] = Now + Cvars.Float("sv_vote_wait"); }
        Count(firstCount: true);
        return Active;
    }

    /// <summary>Record (or change) a player's yes/no vote (QC the vote yes/no command).</summary>
    public bool Cast(Player? voter, bool yes)
    {
        if (!Active || voter is null) return false;
        _selection[voter] = yes ? SelectAccept : SelectReject;
        Count(firstCount: false);
        return true;
    }

    /// <summary>QC the abstain ballot.</summary>
    public bool Abstain(Player? voter)
    {
        if (!Active || voter is null) return false;
        _selection[voter] = SelectAbstain;
        Count(firstCount: false);
        return true;
    }

    /// <summary>Withdraw a voter (disconnect) so their ballot no longer counts.</summary>
    public void RemoveVoter(Player voter)
    {
        bool changed = _selection.Remove(voter);
        _masters.Remove(voter);
        _waitTime.Remove(voter);
        if (changed && Active) Count(firstCount: false);
    }

    /// <summary>QC <c>VoteStop</c>: cancel the active vote without running it.</summary>
    public void Stop() => Stop(null);

    private void Stop(Player? stopper)
    {
        if (!Active) return;
        Broadcast?.Invoke(stopper is not null && ReferenceEquals(stopper, Caller)
            ? $"^2* {CallerName(Caller)} stopped their vote"
            : $"^2* {CallerName(stopper)} stopped {CallerName(Caller)}'s vote");
        if (Caller is not null && stopper is not null && ReferenceEquals(stopper, Caller))
            _waitTime[Caller] = Now + Cvars.Float("sv_vote_stop"); // shorter penalty: lets them recall
        Reset();
    }

    // =============================================================================================
    // counting + resolution (QC VoteCount / VoteAccept / VoteReject / VoteTimeout / VoteThink)
    // =============================================================================================

    /// <summary>Drive the vote one frame (QC <c>VoteThink</c>): resolve at the timeout.</summary>
    public void Think()
    {
        if (!Active) return;
        if (EndTime > 0f && Now > EndTime)
            Count(firstCount: false);
    }

    /// <summary>
    /// QC <c>VoteCount</c>: tally real clients, apply the master playerlimit + spectator exclusion, compute the
    /// overall + of-voted thresholds, and resolve (0-player auto-accept, early accept/reject, timeout).
    /// </summary>
    public void Count(bool firstCount)
    {
        if (!Active) return;

        int playerCount = 0, accept = 0, reject = 0, abstain = 0;
        int realPlayers = 0, realAccept = 0, realReject = 0, realAbstain = 0;
        bool debug = Cvars.Bool("sv_vote_debug");

        IEnumerable<Player> roster = Roster?.Invoke() ?? System.Array.Empty<Player>();
        foreach (Player it in roster)
        {
            if (!debug && it.IsBot) continue; // QC: only real clients (bots only in debug)
            playerCount++;
            bool real = IsPlayer(it) || InGame(it); // QC: IS_PLAYER(it) || INGAME(it)
            if (real) realPlayers++;
            switch (SelectionOf(it))
            {
                case SelectAccept: accept++; if (real) realAccept++; break;
                case SelectReject: reject++; if (real) realReject++; break;
                case SelectAbstain: abstain++; if (real) realAbstain++; break;
            }
        }
        // fall back to the legacy count source if no roster is wired.
        if (Roster is null)
        {
            playerCount = System.Math.Max(_voterCount(), _selection.Count);
            accept = reject = abstain = 0;
            foreach (int sel in _selection.Values)
                switch (sel) { case SelectAccept: accept++; break; case SelectReject: reject++; break; case SelectAbstain: abstain++; break; }
        }

        // master playerlimit guard.
        if (Status == StatusMaster && Cvars.Float("sv_vote_master_playerlimit") > playerCount)
        {
            if (Caller is not null) _waitTime[Caller] = 0f;
            Broadcast?.Invoke("^1Not enough players to allow a master vote.");
            Reset();
            return;
        }

        // spectator exclusion: drop spectator ballots when not allowed and real players exist.
        if (!SpectatorsAllowed && realPlayers > 0)
        {
            playerCount = realPlayers; accept = realAccept; reject = realReject; abstain = realAbstain;
        }

        YesCount = accept; NoCount = reject; AbstainCount = abstain;
        int notvoters = playerCount - accept - reject - abstain;

        float factorOverall = System.Math.Clamp(Cvars.FloatOr("sv_vote_majority_factor", 0.5f), 0.5f, 0.999f);
        NeededOverall = (int)MathF.Floor((playerCount - abstain) * factorOverall) + 1;
        float factorVoted = System.Math.Clamp(Cvars.FloatOr("sv_vote_majority_factor_of_voted", 0.5f), 0.5f, 0.999f);
        int neededOfVoted = (int)MathF.Floor((accept + reject) * factorVoted) + 1;

        // 0-player auto-accept (console vote with nobody on the server).
        if (playerCount == 0 && firstCount) { Spam(notvoters, -1, "yes"); Accept(); return; }
        // early accept.
        if (accept >= NeededOverall) { Spam(notvoters, -1, "yes"); Accept(); return; }
        // early reject (enough No that Yes is impossible).
        if (reject > playerCount - abstain - NeededOverall) { Spam(notvoters, -1, "no"); Reject(); return; }

        // timeout resolution.
        if (Now > EndTime)
        {
            int finalNeeded = NeededOverall;
            if (Cvars.Float("sv_vote_majority_factor_of_voted") != 0f)
            {
                if (accept >= neededOfVoted) { Spam(notvoters, System.Math.Min(NeededOverall, neededOfVoted), "yes"); Accept(); return; }
                if (accept + reject > 0) { Spam(notvoters, System.Math.Min(NeededOverall, neededOfVoted), "no"); Reject(); return; }
                finalNeeded = System.Math.Min(NeededOverall, neededOfVoted);
            }
            Spam(notvoters, finalNeeded, "timeout");
            Timeout();
        }
    }

    private void Accept()
    {
        Broadcast?.Invoke($"^2* {CallerName(Caller)}'s vote for ^1{Display}^2 was accepted");
        string cmd = Command;
        int status = Status;
        Player? caller = Caller;
        if (caller is not null) _waitTime[caller] = 0f; // reward: no cooldown on a passed vote
        if (status == StatusMaster && caller is not null) _masters.Add(caller);
        Reset();
        if (status != StatusMaster)
            VotePassed?.Invoke(cmd);
        // QC VoteAccept (vote.qc:181): Send_Notification(NOTIF_ALL, NULL, MSG_ANNCE, ANNCE_VOTE_ACCEPT).
        AnnounceVoteAccept?.Invoke();
    }

    private void Reject()
    {
        Broadcast?.Invoke($"^2* {CallerName(Caller)}'s vote for ^1{Display}^2 was rejected");
        Reset();
        // QC VoteReject (vote.qc:188): Send_Notification(NOTIF_ALL, NULL, MSG_ANNCE, ANNCE_VOTE_FAIL).
        AnnounceVoteFail?.Invoke();
    }

    private void Timeout()
    {
        Broadcast?.Invoke($"^2* {CallerName(Caller)}'s vote for ^1{Display}^2 timed out");
        Reset();
        // QC VoteTimeout (vote.qc:195): Send_Notification(NOTIF_ALL, NULL, MSG_ANNCE, ANNCE_VOTE_FAIL).
        AnnounceVoteFail?.Invoke();
    }

    private void Spam(int notvoters, int mincount, string result)
    {
        string needed = mincount >= 0 ? $" (^1{mincount}^2 needed)" : "";
        // QC VoteSpam (vote.qc:205): "didn't " + (mincount >= 0 ? "" : "have to ") + "vote" — an early accept/reject
        // (mincount < 0, the vote resolved before everyone had to weigh in) reads "didn't have to vote".
        string didnt = mincount >= 0 ? "didn't vote" : "didn't have to vote";
        Broadcast?.Invoke($"^2* vote results: ^1{YesCount}^2:^1{NoCount}{needed}, ^1{AbstainCount}^2 didn't care, ^1{notvoters}^2 {didnt}");
        _ = result;
    }

    private void Reset()
    {
        Status = StatusNull;
        Command = "";
        Display = "";
        Caller = null;
        EndTime = 0f;
        YesCount = NoCount = AbstainCount = NeededOverall = 0;
        _selection.Clear();
        // NB: _masters persists (master status survives a vote, like QC's .vote_master).
    }

    /// <summary>The local/given player's current ballot (QC <c>.vote_selection</c>): +1 yes, -1 no, -2 abstain,
    /// 0 not voted. Surfaced for the client vote HUD's "you've already voted" dim + the yes/no highlight.</summary>
    public int SelectionOf(Player p) => _selection.TryGetValue(p, out int s) ? s : SelectNull;
    private float WaitTimeOf(Player p) => _waitTime.TryGetValue(p, out float t) ? t : 0f;
    private static string CallerName(Player? p) => p?.NetName ?? "server";
    private bool SpectatorsAllowed
    {
        get
        {
            int mode = Cvars.Int("sv_vote_nospectators");
            if (mode == 0) return true;
            if (mode == 1) return WarmupOrIntermission();
            return false;
        }
    }
    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;

    // =============================================================================================
    // parse / sanitize (QC VoteCommand_checknasty / checkinlist / VoteCommand_parse)
    // =============================================================================================

    /// <summary>QC <c>VoteCommand_checknasty</c>: reject command-chaining / cvar-expansion injection chars.</summary>
    public static bool CheckNasty(string command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        return command.IndexOf(';') < 0 && command.IndexOf('\n') < 0 && command.IndexOf('\r') < 0 && command.IndexOf('$') < 0;
    }

    /// <summary>
    /// QC <c>VoteCommand_checkargs</c>: apply the per-command arg restriction cvar
    /// <c>sv_vote_command_restriction_&lt;cmd&gt;</c>. The restriction is <c>"&lt;minargs&gt;[;charlist[;charlist...]]"</c>:
    /// the first field is the minimum arg count, each subsequent <c>;</c>-separated field is the allowed-character
    /// list for that positional arg (empty = any char allowed). An unset/empty restriction permits everything.
    /// Returns false (reject) when the arg count is short or an arg contains a disallowed character.
    /// </summary>
    private static bool CheckArgs(CommandContext ctx, int startpos)
    {
        string cmdrestriction = Cvars.String("sv_vote_command_restriction_" + ctx.Arg(startpos));
        if (string.IsNullOrEmpty(cmdrestriction)) return true; // unset/empty → no restriction (QC)

        int argc = ctx.ArgCount;
        startpos++; // skip the command name (QC ++startpos)

        // QC: minargs = stof(cmdrestriction); if (argc - startpos < minargs) return false.
        int firstSemi = cmdrestriction.IndexOf(';');
        string minPart = firstSemi < 0 ? cmdrestriction : cmdrestriction.Substring(0, firstSemi);
        int minargs = (int)ToFloat(minPart);
        if (argc - startpos < minargs) return false;

        int p = firstSemi; // index of the first semicolon (QC strstrofs(...,";",0))
        for (; ; )
        {
            if (startpos >= argc) break;            // all args checked → GOOD
            if (p < 0)                              // no more charlists
            {
                if (argc - startpos == minargs) break; // exactly minargs left → GOOD
                return false;
            }
            int q = cmdrestriction.IndexOf(';', p + 1); // next semicolon
            string charlist = q < 0
                ? cmdrestriction.Substring(p + 1)
                : cmdrestriction.Substring(p + 1, q - (p + 1));
            if (charlist.Length != 0)
            {
                string arg = ctx.Arg(startpos);
                for (int check = 0; check < arg.Length; check++)
                    if (charlist.IndexOf(arg[check]) < 0) return false; // disallowed char
            }
            startpos++;
            minargs--;
            p = q;
        }
        return true;
    }

    private static float ToFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>QC the map/chmap→gotomap normalization used by the whitelist check.</summary>
    private static string ApplyReplacements(string s)
        => (" " + s + " ").Replace(" map ", " gotomap ").Replace(" chmap ", " gotomap ");

    /// <summary>QC <c>VoteCommand_checkinlist</c>: is the command's first word whitelisted (whole-word match)?</summary>
    public static bool CheckInList(string command, string list)
    {
        if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(list)) return false;
        // ApplyReplacements space-pads both ends, so a padded single word matches whole words only.
        string c = ApplyReplacements(command); // e.g. " gotomap "
        string l = ApplyReplacements(list);    // e.g. " restart gotomap endmatch "
        return l.Contains(c, StringComparison.Ordinal);
    }

    /// <summary>
    /// QC <c>VoteCommand_parse</c>: validate the called command against the whitelist (console bypasses it),
    /// then build the parsed command (executed) + display (shown). Returns 1 accepted, 0 unacceptable, -1 a
    /// specific error (in <paramref name="parseError"/>). map/chmap→gotomap; restart→defer; kick→by-name.
    /// </summary>
    private int Parse(CommandContext ctx, string command, string list, int startpos,
        out string parsedCommand, out string parsedDisplay, out string parseError)
    {
        parsedCommand = ""; parsedDisplay = ""; parseError = "";
        Player? caller = ctx.Caller;

        int limit = Cvars.Int("sv_vote_limit");
        if (limit > 0 && command.Length > limit) return 0;

        string first = ctx.Arg(startpos).ToLowerInvariant();
        if (caller is not null && !CheckInList(first, list)) return 0;

        // QC VoteCommand_checkargs: per-command arg restriction (sv_vote_command_restriction_<cmd>).
        if (!CheckArgs(ctx, startpos)) return 0;

        switch (first)
        {
            case "movetoauto":
            case "movetored":
            case "movetoblue":
            case "movetoyellow":
            case "movetopink":
            case "movetospec":
            {
                // QC: GetIndexedEntity + VerifyClientEntity, then pass the verbatim command through (vote.qc:796).
                Player? victim = FindPlayer?.Invoke(ctx.Arg(startpos + 1));
                if (victim is null) { parseError = $"vcall: no player matching \"{ctx.Arg(startpos + 1)}\"."; return 0; }
                // Base has per-team server verbs (movetored/blue/…); the port consolidated them into a single
                // `moveplayer <player> <dest>` command, so translate the voted verb's team suffix into it. Use the
                // #index player form (not the name) so a NetName containing spaces can't re-tokenize on execution.
                // Without this the passed vote ran a non-existent `movetored` command and silently did nothing.
                parsedCommand = $"moveplayer #{victim.Index} {first["moveto".Length..]}";
                parsedDisplay = $"^1{first} #{victim.Index} ^7{victim.NetName}";
                return 1;
            }
            case "kick":
            case "kickban":
            {
                // QC emits `kick # <index> ...` (kickban appends g_ban_default_bantime/masksize/~) (vote.qc:820).
                Player? victim = FindPlayer?.Invoke(ctx.Arg(startpos + 1));
                if (victim is null) { parseError = $"vcall: no player matching \"{ctx.Arg(startpos + 1)}\"."; return 0; }
                string reason = ctx.ArgTail(startpos + 2);
                if (string.IsNullOrEmpty(reason)) reason = "No reason provided";
                string cmdArgs = first == "kickban"
                    ? $"{(int)Cvars.FloatOr("g_ban_default_bantime", 5400f)} {Cvars.Int("g_ban_default_masksize")} ~"
                    : reason;
                parsedCommand = $"{first} # {victim.Index} {cmdArgs}";
                parsedDisplay = $"^1{first} #{victim.Index} ^7{victim.NetName}^1 {reason}";
                return 1;
            }
            case "map":
            case "chmap":
            case "gotomap":
            {
                // QC: vote_command = ValidateMap(argv(startpos+1), caller); if (!vote_command) return -1.
                string? map = ValidateMap(ctx.Arg(startpos + 1), caller is null, out parseError);
                if (string.IsNullOrEmpty(map)) return -1;
                parsedCommand = $"gotomap {map}";
                parsedDisplay = $"^1gotomap {map}";
                return 1;
            }
            case "nextmap":
            {
                string? map = ValidateMap(ctx.Arg(startpos + 1), caller is null, out parseError);
                if (string.IsNullOrEmpty(map)) return -1;
                parsedCommand = $"nextmap {map}";
                parsedDisplay = $"^1nextmap {map}";
                return 1;
            }
            case "fraglimit":
            {
                float n = ctx.ArgFloat(startpos + 1);
                if (n < 0f || n > 999999f) { parseError = "^1Invalid fraglimit, accepted values are 0..999999."; return -1; }
                parsedCommand = $"fraglimit {(int)n}";
                parsedDisplay = $"^1fraglimit {(int)n}";
                return 1;
            }
            case "timelimit":
            {
                float n = ctx.ArgFloat(startpos + 1);
                // QC timelimit_min/max defaults (xonotic-server.cfg): 5 / 60.
                float lo = Cvars.FloatOr("timelimit_min", 5f), hi = Cvars.FloatOr("timelimit_max", 60f);
                if (n < lo || n > hi) { parseError = $"^1Invalid timelimit, accepted values are {lo:0}..{hi:0}."; return -1; }
                parsedCommand = $"timelimit {(int)n}";
                parsedDisplay = $"^1timelimit {(int)n}";
                return 1;
            }
            case "restart":
                parsedCommand = "defer 1 restart"; // QC defers so the announcer/result shows first
                parsedDisplay = "^1restart";
                return 1;
            case "allready":
                // QC: allready is only votable during warmup; once the match started use resetmatch (vote.qc:911).
                if (!WarmupStage())
                { parseError = "Game already started. Use the resetmatch command to restart the match."; return -1; }
                parsedCommand = command;
                parsedDisplay = "^1" + command;
                return 1;
            default:
                parsedCommand = command;
                parsedDisplay = "^1" + command;
                return 1;
        }
    }
}
