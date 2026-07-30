using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The warmup + ready-restart flow — the Godot-free essence of the QC warmup stage and the
/// <c>ReadyRestart</c> / <c>ReadyRestart_force</c> / <c>ReadyCount</c> / <c>reset_map</c> machinery
/// (server/command/vote.qc + server/world.qc). Warmup is the pre-match phase where players join, gear up,
/// and ready up (F4); once a majority is ready (or the warmup limit elapses) the match restarts into a
/// short countdown and goes live.
///
/// State tracked (the QC globals): the warmup stage flag (<see cref="WarmupStage"/>, QC <c>warmup_stage</c>),
/// the per-player ready set (QC <c>.ready</c> / <c>readycount</c>), the warmup time limit
/// (<see cref="WarmupLimit"/>, QC <c>warmup_limit</c>), and the match start time / countdown
/// (<see cref="GameStartTime"/>, QC <c>game_starttime</c>, owned by <see cref="GameWorld"/> but driven here).
///
/// The actual map reset (re-spawn players, reset map objects, clear scores) is delegated to
/// <see cref="ResetMap"/>, which <see cref="GameWorld"/> wires to its own reset (the QC <c>reset_map</c>
/// FOREACH_CLIENT PutClientInServer + the entity <c>.reset</c> callbacks).
///
/// Host-wired via callbacks (all live in <see cref="GameWorld"/>): team locking (<see cref="OnLockTeams"/>),
/// the campaign 3s countdown (<see cref="CampaignActive"/>), the minplayers/badteams countdown-abort
/// (<see cref="MapMinPlayers"/> / <see cref="BadTeams"/> / <see cref="OnCountdownStop"/>), and the timeout
/// ready-count guard (<see cref="TimeoutActive"/>). Still deferred: the spectate==2 demotion + per-player
/// requeue/timeout-reset on restart, and the Nagger network entity.
/// </summary>
public sealed class WarmupController
{
    /// <summary>QC RESTART_COUNTDOWN (vote.qh:66): seconds of countdown after warmup ends before the match goes live.</summary>
    public const float RestartCountdown = 10f;

    /// <summary>QC ReadyRestart_force: the campaign restart uses a fixed 3s countdown instead of RESTART_COUNTDOWN.</summary>
    public const float CampaignCountdown = 3f;

    private readonly HashSet<Player> _ready = new();

    /// <summary>QC <c>warmup_stage</c>: true while the match is in the pre-game warmup phase.</summary>
    public bool WarmupStage { get; private set; }

    /// <summary>
    /// QC <c>warmup_limit</c>: absolute-ish warmup duration in seconds. -1 = wait until ready; 0 = use the
    /// match timelimit; &gt;0 = that many seconds. Resolved from <c>g_warmup_limit</c> at <see cref="Begin"/>.
    /// </summary>
    public float WarmupLimit { get; private set; } = -1f;

    /// <summary>QC <c>game_starttime</c>: absolute sim time the match goes live (the GameWorld mirrors this).</summary>
    public float GameStartTime { get; set; }

    /// <summary>QC <c>readycount</c>: how many players have readied up.</summary>
    public int ReadyCount => _ready.Count;

    /// <summary>
    /// Supplies the roster the ready-majority is computed against (QC FOREACH_CLIENT(IS_PLAYER)). The host
    /// wires this to its connected players.
    /// </summary>
    public Func<IReadOnlyList<Player>> Roster { get; set; } = static () => System.Array.Empty<Player>();

    /// <summary>
    /// The map-reset action (QC <c>reset_map</c>): re-spawn players, reset map objects, and (when
    /// <c>fakeRoundStart</c> is false) clear scores. The host wires this to <see cref="GameWorld"/>'s reset.
    /// </summary>
    public Action<bool>? ResetMap { get; set; }

    /// <summary>Fired when warmup ends and the match restart begins (QC sv_hook_warmupend / the countdown).</summary>
    public Action? OnMatchRestart { get; set; }

    /// <summary>
    /// QC <c>autocvar_g_campaign</c> in the warmup path: when true there is no warmup (it's forced off at
    /// worldspawn) and the ready-restart uses the 3s campaign countdown rather than RESTART_COUNTDOWN. The host
    /// wires this to its campaign state; defaults false (a normal match).
    /// </summary>
    public bool CampaignActive { get; set; }

    /// <summary>
    /// QC <c>timeout_status</c> guard at the top of <c>ReadyCount</c>: while a timeout is active or pending the
    /// ready-count must not reset the game. The host wires this to its timeout machinery (this port defers full
    /// timeout interaction — see the class docstring); defaults false (no timeout).
    /// </summary>
    public Func<bool>? TimeoutActive { get; set; }

    /// <summary>
    /// QC <c>map_minplayers</c> / the <c>g_warmup &gt; 1</c> "minimum N players (incl. bots)" mode: the minimum
    /// player count below which warmup will NOT end (the ReadyCount minplayers gate). Resolved in <see cref="Begin"/>
    /// from <c>g_warmup</c> (&gt;1 ⇒ that value) and otherwise the host-provided map minimum. 0 = no minimum.
    /// </summary>
    public int MinPlayers { get; private set; }

    /// <summary>
    /// Host hook for the resolved map minimum-player count (QC <c>map_minplayers</c>, world.qc:697). Only consulted
    /// when <c>g_warmup &lt;= 1</c>; with <c>g_warmup &gt; 1</c> the cvar value is the minimum directly. Defaults 0.
    /// </summary>
    public Func<int>? MapMinPlayers { get; set; }

    /// <summary>
    /// QC <c>ReadyCount</c> badteams gate: <c>(teamplay &amp;&amp; total_players &amp;&amp; sv_teamnagger)
    /// ? TeamBalance_SizeDifference(NULL) &gt;= sv_teamnagger : false</c>. The team-size compute lives in the
    /// teamplay layer (this controller can't see team sizes), so the host wires this to
    /// <c>Teamplay.TeamsUnbalancedForNag</c>. Defaults false (no team imbalance held against the restart).
    /// </summary>
    public Func<bool>? BadTeams { get; set; }

    /// <summary>
    /// QC ReadyRestart_force: <c>if (autocvar_teamplay_lockonrestart &amp;&amp; teamplay) lockteams = !warmup_stage;</c>.
    /// Fired on every restart with the lock state to apply (true = lock teams now that the match is going live). The
    /// host wires this to its team-lock (Commands.TeamsLocked); no-op if unwired.
    /// </summary>
    public Action<bool>? OnLockTeams { get; set; }

    /// <summary>
    /// QC the COUNTDOWN_STOP_MINPLAYERS / COUNTDOWN_STOP_BADTEAMS notifications: fired when a ready-count during the
    /// live countdown aborts back to warmup because too few players are present (argument = the required minimum) or
    /// the teams are unbalanced (argument = -1, the QC bad-teams case). The host maps it to the broadcast.
    /// </summary>
    public Action<int>? OnCountdownStop { get; set; }

    /// <summary>
    /// T40: fired once per whole second of the pre-match (game-start) countdown — the server-side analogue of
    /// the CSQC <c>Announcer_Countdown</c> CNT_GAMESTART tick (this port has no CSQC announcer timer). The
    /// argument is the visible seconds remaining (the QC <c>countdown_rounded</c> = floor(0.5 + secondsLeft)):
    /// it counts down to 1, then fires 0 on the tick the match goes live. <see cref="GameWorld"/> maps it to the
    /// broadcasts: <c>NUM_GAMESTART_&lt;n&gt;</c> (the registry self-gates to n≤5) + <c>COUNTDOWN_GAMESTART(n)</c>
    /// for n≥1, <c>BEGIN</c> at n==0, and <c>PREPARE</c> on the first tick when the countdown exceeds 5s (QC
    /// <c>time + 5.0 &lt; startTime</c>). Fires once per second only (not every frame), so the announcer isn't
    /// spammed.
    /// </summary>
    public Action<int>? OnCountdownTick { get; set; }

    private bool _started;
    private float _warmupStartTime;

    // T40: the last whole-second value handed to OnCountdownTick, so Think() fires it once per second (not every
    // frame) and once for the terminal 0. int.MinValue = no tick emitted yet for the current countdown.
    private int _lastCountdownSecond = int.MinValue;

    /// <summary>True once <see cref="Begin"/> has run (the warmup/countdown flow is driving the start time).</summary>
    public bool IsStarted => _started;

    /// <summary>
    /// QC the warmup setup at world spawn: enter the warmup stage if <c>g_warmup</c> is set, resolve the
    /// warmup limit, and pin <see cref="GameStartTime"/> until the match is readied. Call once at boot after
    /// the cvars are registered. When warmup is disabled, the match starts immediately (countdown only).
    /// </summary>
    public void Begin()
    {
        _started = true;
        _ready.Clear();
        _lastCountdownSecond = int.MinValue; // T40: re-arm the game-start countdown announcer.

        // QC readlevelcvars (world.qc:2198-2211): campaign forces warmup off; otherwise warmup_stage = g_warmup
        // and a g_warmup value outside [0,1] (e.g. -1 "until enough players" or >1 "minimum N players") means an
        // infinite warmup_limit (-1). The plain g_warmup==1 path resolves the timed limit.
        int gWarmup = Cvars.Int("g_warmup");
        WarmupStage = !CampaignActive && gWarmup != 0;
        ResolveMinPlayers(gWarmup);

        if (WarmupStage)
        {
            if (gWarmup < 0 || gWarmup > 1)
                WarmupLimit = -1f; // don't start until there's enough players
            else
                ResolveWarmupLimit(); // g_warmup == 1: timed (or 0->timelimit, -1->until-ready) limit
            _warmupStartTime = Now;
            // QC: during warmup there's no countdown; game_starttime sits in the future until ready.
            GameStartTime = WarmupLimit < 0f ? float.MaxValue : _warmupStartTime + WarmupLimit;
        }
        else
        {
            // QC world.qc:2220-2222: !warmup && !campaign -> game_starttime = time + g_start_delay (0 listen /
            // 15 dedicated). Campaign starts immediately (g_start_delay not applied). No ready-restart countdown
            // here — Begin only arms the pre-match join window; the countdown comes from a later ReadyRestart.
            WarmupLimit = 0f;
            float startDelay = CampaignActive ? 0f : Cvars.FloatOr("g_start_delay", 0f);
            GameStartTime = Now + startDelay;
        }
    }

    // QC map_minplayers resolution (world.qc:697) collapsed: g_warmup>1 means that value is the minimum directly,
    // otherwise the host-provided map minimum. 0 = no minimum (the ReadyCount minplayers gate is inert).
    private void ResolveMinPlayers(int gWarmup)
    {
        MinPlayers = gWarmup > 1 ? gWarmup : (MapMinPlayers?.Invoke() ?? 0);
    }

    private void ResolveWarmupLimit()
    {
        // QC: warmup_limit = g_warmup_limit; if 0 -> timelimit*60; -1 stays "until ready".
        float lim = Cvars.FloatOr("g_warmup_limit", -1f);
        if (lim == 0f)
            lim = Cvars.TimeLimitMinutes * 60f;
        WarmupLimit = lim;
    }

    /// <summary>
    /// QC <c>ClientCommand_ready</c>: toggle a player's ready flag (F4). Returns the new ready state. Re-counts
    /// and, if the ready-majority threshold is met, triggers the restart (<see cref="ReadyRestart"/>). No-op
    /// outside warmup (QC only counts ready during warmup / when sv_ready_restart is enabled).
    /// </summary>
    public bool ToggleReady(Player p)
    {
        bool nowReady = !_ready.Contains(p);
        if (nowReady) _ready.Add(p);
        else _ready.Remove(p);

        ReadyCountCheck();
        return nowReady;
    }

    /// <summary>Clear a player's ready flag (e.g. on disconnect / move to spectator), then re-check.</summary>
    public void ClearReady(Player p)
    {
        if (_ready.Remove(p))
            ReadyCountCheck();
    }

    /// <summary>
    /// QC <c>ReadyCount</c> (vote.qc:553): re-count the ready players and decide whether to (a) abort a running
    /// countdown back to warmup (too few players / unbalanced teams), (b) switch an infinite warmup to timed once
    /// there are enough players, or (c) end warmup and restart when the human-ready majority is met. Called whenever
    /// the ready set changes (F4, join, spectate, disconnect) — NOT gated on warmup, because the abort path runs
    /// during the live countdown too.
    /// </summary>
    public void ReadyCountCheck()
    {
        // QC: cannot reset the game while a timeout is active or pending.
        if (TimeoutActive?.Invoke() == true)
            return;

        var roster = Roster();
        int totalPlayers = roster.Count; // QC total_players: every player (bots incl.)
        int humanPlayers = 0;
        int humansReady = 0;
        foreach (var p in roster)
        {
            if (p.IsBot) continue;
            humanPlayers++;
            if (_ready.Contains(p)) humansReady++;
        }

        // QC badteams: (teamplay && total_players && sv_teamnagger) ? TeamBalance_SizeDifference(NULL) >=
        // sv_teamnagger : false. The size-difference compute lives in the teamplay layer (this controller can't
        // see team sizes), so the host wires BadTeams to Teamplay.TeamsUnbalancedForNag. The QC total_players
        // guard is mirrored here (no players => no imbalance hold). Unwired => false (no team imbalance hold).
        bool badteams = totalPlayers > 0 && (BadTeams?.Invoke() ?? false);

        if (totalPlayers < MinPlayers || badteams)
        {
            // QC: someone bailed during the live countdown -> abort back to warmup and notify.
            if (GameStartTime > Now)
            {
                WarmupStage = !CampaignActive && Cvars.WarmupStage; // re-enter warmup (CAN change after this point)
                GameStartTime = Now;
                _lastCountdownSecond = int.MinValue; // re-arm the announcer for the next countdown
                _pendingResetAt = null; // abort cancels any deferred (after-countdown) reset
                OnCountdownStop?.Invoke(totalPlayers < MinPlayers ? MinPlayers : -1);
                // QC GiveWarmupResources re-give when reset_map already ran at countdown start is a per-player
                // loadout concern owned by SpawnSystem/GameWorld — see todos (warmup loadout re-give on abort).
            }
            WarmupLimit = -1f;
            return; // don't restart: ready players present but too few / bad teams
        }

        // QC: enough players & teams ok, but still in infinite warmup -> switch to a timed warmup.
        if (WarmupLimit <= 0f && GameStartTime <= Now)
        {
            float lim = Cvars.FloatOr("g_warmup_limit", -1f);
            if (lim == 0f)
                lim = Cvars.TimeLimitMinutes * 60f;
            WarmupLimit = lim;
            if (WarmupLimit > 0f)
            {
                _warmupStartTime = Now;       // anchor the now-timed warmup window
                GameStartTime = Now + WarmupLimit;
            }
            // implicit else (g_warmup -1 && g_warmup_limit -1): warmup continues until enough RUPs (no time limit).
        }

        // QC: humans_ready && humans_ready >= rint(human_players * bound(0.5, g_warmup_majority_factor, 1)).
        if (humansReady == 0)
            return;
        float factor = System.Math.Clamp(Cvars.FloatOr("g_warmup_majority_factor", 0.8f), 0.5f, 1f);
        int needed = (int)MathF.Round(humanPlayers * factor, MidpointRounding.ToEven); // QC rint (round-half-to-even)
        if (humansReady >= needed)
            ReadyRestart(forceWarmupEnd: true);
    }

    /// <summary>
    /// Drive warmup one frame (QC the StartFrame warmup check): when the warmup limit elapses, end warmup
    /// and restart into the match. No-op when warmup is disabled or already ended. Call once per server frame.
    /// </summary>
    public void Think()
    {
        if (!_started)
            return;

        // QC restart_timer think (ReadyRestart_force, sv_ready_restart_after_countdown==1): once the countdown
        // reaches game_starttime, run the deferred reset_map(false). Fires exactly once (cleared on dispatch).
        if (_pendingResetAt is float resetAt && Now >= resetAt)
        {
            _pendingResetAt = null;
            ResetMap?.Invoke(/*fakeRoundStart*/ false);
        }

        // T40: drive the pre-match (game-start) countdown announcer at 1 Hz (QC CSQC Announcer_Countdown,
        // CNT_GAMESTART). Only outside warmup, while time < game_starttime. Mirrors countdown_rounded going
        // count..1 then a terminal 0 (BEGIN). Fires the callback at most once per whole second + once at 0.
        if (!WarmupStage)
        {
            float remaining = GameStartTime - Now;
            if (remaining > 0f)
            {
                int cr = (int)MathF.Floor(0.5f + remaining); // QC countdown_rounded
                if (cr >= 1 && cr != _lastCountdownSecond)
                {
                    _lastCountdownSecond = cr;
                    OnCountdownTick?.Invoke(cr);
                }
            }
            else if (_lastCountdownSecond != 0)
            {
                // countdown finished (time >= game_starttime): fire BEGIN exactly once.
                _lastCountdownSecond = 0;
                OnCountdownTick?.Invoke(0);
            }
        }

        if (!WarmupStage)
            return;
        if (WarmupLimit > 0f && Now - _warmupStartTime >= WarmupLimit)
            ReadyRestart(forceWarmupEnd: true);
    }

    /// <summary>
    /// QC <c>ReadyRestart</c> → <c>ReadyRestart_force</c>: leave warmup (when forced or no warmup configured),
    /// arm the match countdown via <see cref="GameStartTime"/>, clear all ready flags, fire the restart
    /// callback, and reset the map. With <c>g_warmup</c> still enabled and not forced, this restarts back into
    /// a fresh warmup instead.
    /// </summary>
    public void ReadyRestart(bool forceWarmupEnd)
    {
        _started = true; // the warmup/countdown flow now drives the match start time

        // QC ReadyRestart (vote.qc:531-543): forceWarmupEnd or campaign ends warmup; otherwise restart back into
        // warmup if g_warmup is still enabled.
        WarmupStage = (forceWarmupEnd || CampaignActive) ? false : Cvars.WarmupStage;

        // QC ReadyRestart_force (vote.qc:455-460): warmup -> no countdown; campaign -> 3s; else RESTART_COUNTDOWN (10s).
        if (WarmupStage)
            GameStartTime = Now; // no countdown in warmup
        else if (CampaignActive)
            GameStartTime = Now + CampaignCountdown;
        else
            GameStartTime = Now + RestartCountdown;

        _ready.Clear();
        _lastCountdownSecond = int.MinValue; // T40: a restart arms a fresh game-start countdown to announce.

        if (WarmupStage)
        {
            _warmupStartTime = Now; // re-anchor the timed-warmup window
            ResolveWarmupLimit();
        }

        // QC: if (autocvar_teamplay_lockonrestart && teamplay) lockteams = !warmup_stage.
        if (Cvars.FloatOr("teamplay_lockonrestart", 0f) != 0f && Cvars.Teamplay)
            OnLockTeams?.Invoke(!WarmupStage);

        OnMatchRestart?.Invoke();

        // QC ReadyRestart_force (vote.qc:480-516): sv_ready_restart_after_countdown chooses WHEN reset_map runs.
        //   - Default (0) or in warmup: reset_map(false) runs NOW, at countdown start — players/items snap to spawn
        //     immediately and stand frozen through the countdown.
        //   - sv_ready_restart_after_countdown==1 && !warmup: a restart_timer think is armed at game_starttime; the
        //     reset is DEFERRED until the countdown finishes, so players keep moving during the countdown and only
        //     reset the instant the match goes live. Modelled here with a pending-reset time the Think loop fires
        //     (the restart_timer think's ReadyRestart_think calls reset_map(false) at game_starttime).
        if (!WarmupStage && Cvars.FloatOr("sv_ready_restart_after_countdown", 0f) != 0f)
        {
            _pendingResetAt = GameStartTime;
        }
        else
        {
            _pendingResetAt = null;
            // QC: reset_map(false) re-spawns players + resets map objects + clears scores (full restart).
            ResetMap?.Invoke(/*fakeRoundStart*/ false);
        }
    }

    // QC ReadyRestart_force restart_timer: the time at which a deferred reset_map(false) should fire, or null when
    // no deferral is pending (sv_ready_restart_after_countdown disabled / warmup). Driven by Think().
    private float? _pendingResetAt;

    /// <summary>True if the match is currently in its pre-live countdown (QC <c>time &lt; game_starttime</c> live phase).</summary>
    public bool CountdownRunning => _started && !WarmupStage && Now < GameStartTime;

    /// <summary>Seconds remaining on the countdown (for HUD), 0 once live.</summary>
    public float CountdownSecondsLeft => System.Math.Max(0f, GameStartTime - Now);

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
