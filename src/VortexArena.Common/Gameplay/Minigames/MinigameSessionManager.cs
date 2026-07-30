using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

// Port of qcsrc/common/minigames/sv_minigames.qc — the SERVER-SIDE minigame session spine:
// start_minigame / join_minigame / part_minigame / end_minigame / end_minigames / minigame_addplayer /
// minigame_rmplayer / player_clear_minigame / invite_minigame / minigame_find_player / MinigameImpulse.
//
// QC keeps a global linked list of live session entities (minigame_sessions, chained via .owner/.list_next)
// and a per-client back-link (CS(player).active_minigame + player.minigame_players). The CSQC entity-network
// layer (Net_LinkEntity + minigame_SendEntity, the MSLE read/write) is NOT modelled here — the port networks
// a whole-session snapshot instead (game/net/MinigameNetState.cs), pushed by the host after each change. This
// type owns ONLY the rules-side lifecycle so it can live in XonoticGodot.Common (the test assembly sees src/, not
// game/). It drives Pong.Tick on each session each frame.
//
// QC model -> C# model
//   minigame_sessions (linked list head)        -> Sessions (List<MinigameSession>)
//   CS(player).active_minigame + .minigame_players -> _active (Player -> (session, pointer))
//   minigame_player entity (player_pointer)       -> MinigamePlayer (Slot>0 = a connected client)
//   .netname "<gameid>_<entnum>"                  -> session.NetName (unique "<gameid>_<n>" from a counter)
//   minigame_event(mg, "join"/"part"/...)         -> session.Join/Part (the descriptor virtuals)
//   GameLogEcho(":minigame:...")                  -> Log.Trace (the event log isn't wired here)

/// <summary>
/// The server-side registry of live <see cref="MinigameSession"/>s plus the player→session map — the C#
/// successor to QuakeC's <c>minigame_sessions</c> list and the per-client <c>active_minigame</c> back-link.
/// Mirrors sv_minigames.qc 1:1: create/join/part/end, the "only player → end" rmplayer branch, the
/// observer-forcing on join, the invite error-string contract, and the impulse dispatch. A <see cref="Tick"/>
/// drives the real-time games (Pong) each server frame; changed sessions are flagged <see cref="Dirty"/> so
/// the host knows to re-send a snapshot.
/// </summary>
public sealed class MinigameSessionManager
{
    /// <summary>QC <c>.netname</c> on a <c>minigame_player</c> back-links to the controlling client. Here a
    /// session player carries the controlling <see cref="Player"/> on <see cref="MinigamePlayer.Client"/>.</summary>
    public sealed record Active(MinigameSession Session, MinigamePlayer Pointer);

    private readonly List<MinigameSession> _sessions = new();
    private readonly Dictionary<Player, Active> _active = new();
    private readonly Dictionary<string, MinigameSession> _byName = new(StringComparer.Ordinal);
    private readonly HashSet<MinigameSession> _dirty = new();
    private readonly HashSet<Player> _departed = new(); // players who left/whose session ended → push a null envelope

    /// <summary>The cvar facade (QC autocvar_*). Injected so the manager stays Godot-free and unit-testable.</summary>
    private readonly Func<string, bool> _cvarBool;
    private readonly Func<string, int> _cvarInt;

    /// <summary>Optional sink the host wires to force a player into the observer/spectator state when
    /// <c>sv_minigames_observer</c> is set (QC PutObserverInServer / Player_SetForcedTeamIndex). Args: the
    /// player and the force-spectator flag (true when <c>sv_minigames_observer==2</c>). Null = no-op (P0 hosts
    /// that don't own the spectator plumbing). The owning seam is the spectator/forced-team layer (T44).</summary>
    public Action<Player, bool>? ObserverForcer { get; set; }

    /// <summary>Optional sink the host wires to restore a player to the normal walk/team state when they leave a
    /// minigame (QC player_clear_minigame: MOVETYPE_WALK + TEAM_FORCE_DEFAULT). Null = no-op.</summary>
    public Action<Player>? ClearForcer { get; set; }

    /// <summary>Optional predicate the host wires to test play-ban list membership (QC
    /// <c>PlayerInList(player, autocvar_g_playban_list)</c>). The g_playban_list lives in the server layer
    /// (Bans), unreachable from XonoticGodot.Common, so the host injects it (like <see cref="ObserverForcer"/>).
    /// Null = no membership backing → treated as "not banned" (faithful no-op: the <c>g_playban_minigames</c>
    /// cvar gate in <see cref="Invite"/> still applies, and that cvar ships 0 anyway).</summary>
    public Func<Player, bool>? PlayBanned { get; set; }

    private int _counter; // QC etof(minig): a unique entity number — here a monotonically increasing counter.

    public MinigameSessionManager(Func<string, bool> cvarBool, Func<string, int> cvarInt)
    {
        _cvarBool = cvarBool;
        _cvarInt = cvarInt;
    }

    /// <summary>The live sessions (QC the <c>minigame_sessions</c> list), most-recently-created first.</summary>
    public IReadOnlyList<MinigameSession> Sessions => _sessions;

    /// <summary>QC <c>CS(player).active_minigame</c>: the session the player is in, or null.</summary>
    public MinigameSession? ActiveSessionOf(Player player)
        => _active.TryGetValue(player, out Active? a) ? a.Session : null;

    /// <summary>QC <c>player.minigame_players</c>: the player's pointer in its active session, or null.</summary>
    public MinigamePlayer? PointerOf(Player player)
        => _active.TryGetValue(player, out Active? a) ? a.Pointer : null;

    /// <summary>Look up a session by its unique netname (QC the <c>minigame_sessions</c> scan in join_minigame).</summary>
    public MinigameSession? ByName(string netname)
        => _byName.TryGetValue(netname, out MinigameSession? s) ? s : null;

    /// <summary>
    /// The human participants of a session with their team — the C# stand-in for QC's
    /// <c>minigame_CheckSend</c> (the snapshot of an entity is only sent to clients in its
    /// <c>minigame_players</c> list). The host iterates this to push each participant their per-peer envelope
    /// (the envelope carries that player's own team so the client gates moves on the local turn). AI paddles
    /// aren't included (they have no client).
    /// </summary>
    public IEnumerable<(Player Player, int Team)> Participants(MinigameSession session)
    {
        foreach (var kv in _active)
            if (ReferenceEquals(kv.Value.Session, session))
                yield return (kv.Key, kv.Value.Pointer.Team);
    }

    // =============================================================================================
    // dirty tracking (the snapshot push: QC minigame_resend marks SendFlags; here we flag the session)
    // =============================================================================================

    /// <summary>Sessions changed since the last drain (the host re-encodes + sends their snapshot).</summary>
    public IReadOnlyCollection<MinigameSession> Dirty => _dirty;

    /// <summary>Mark a session as needing a re-send (QC <c>minigame_resend</c>).</summary>
    public void MarkDirty(MinigameSession session) => _dirty.Add(session);

    /// <summary>Take + clear the changed sessions (the host calls this after each frame to push snapshots).</summary>
    public IReadOnlyList<MinigameSession> DrainDirty()
    {
        if (_dirty.Count == 0)
            return System.Array.Empty<MinigameSession>();
        var list = new List<MinigameSession>(_dirty);
        _dirty.Clear();
        return list;
    }

    /// <summary>Take + clear the players who left a minigame (parted / their session ended) since the last drain.
    /// The host pushes each a "you have no active minigame" envelope (QC the per-entity removal → CSQC
    /// deactivate_minigame). A player who left AND is in <see cref="DrainDirty"/>'s sessions can't happen — once
    /// removed they aren't in any session's participant list.</summary>
    public IReadOnlyList<Player> DrainDeparted()
    {
        if (_departed.Count == 0)
            return System.Array.Empty<Player>();
        var list = new List<Player>(_departed);
        _departed.Clear();
        return list;
    }

    // =============================================================================================
    // start_minigame (QC sv_minigames.qc:164)
    // =============================================================================================

    /// <summary>
    /// QC <c>start_minigame(player, gameid)</c>: gate on <c>sv_minigames</c> + a real client, resolve the game
    /// descriptor, spawn a fresh session, fire its "start", seat the creator via <see cref="AddPlayer"/> (END
    /// + return null if the first join is rejected), and push it onto <see cref="Sessions"/>. Returns the new
    /// session or null.
    /// </summary>
    public MinigameSession? Create(Player player, string gameId)
    {
        if (!_cvarBool("sv_minigames") || !IsRealClient(player))
            return null;

        Minigame? descriptor = Minigames.ByName(gameId);
        if (descriptor is null)
            return null;

        // QC: new(minigame); .descriptor=e; minigame_event(minig,"start"). The port's CreateSession() both
        // allocates the session (incl. its board) AND fires Start(), matching the QC descriptor → "start" order.
        MinigameSession session = descriptor.CreateSession();
        // QC: .netname = strzone(strcat(e.netname,"_",ftos(etof(minig)))) — a unique "<gameid>_<entnum>".
        session.NetName = $"{descriptor.NetName}_{++_counter}";

        Log.Trace($":minigame:start:{session.NetName}");

        // QC: if(!minigame_addplayer(minig,player)) { end_minigame(minig); return NULL; }
        if (AddPlayer(session, player) == 0)
        {
            Log.Trace($"Minigame {session.NetName} rejected the first player join!");
            End(session);
            return null;
        }

        // QC: push onto the minigame_sessions list head.
        _sessions.Insert(0, session);
        _byName[session.NetName] = session;
        MarkDirty(session);
        return session;
    }

    // =============================================================================================
    // join_minigame (QC sv_minigames.qc:200)
    // =============================================================================================

    /// <summary>
    /// QC <c>join_minigame(player, game_id)</c>: gate on <c>sv_minigames</c> + real client, find the session by
    /// netname, then <see cref="AddPlayer"/>. Returns the session on a successful join (mgteam != 0), else null.
    /// </summary>
    public MinigameSession? JoinByName(Player player, string gameId)
    {
        if (!_cvarBool("sv_minigames") || !IsRealClient(player))
            return null;

        if (_byName.TryGetValue(gameId, out MinigameSession? session) && AddPlayer(session, player) != 0)
            return session;
        return null;
    }

    // =============================================================================================
    // minigame_addplayer (QC sv_minigames.qc:127)
    // =============================================================================================

    /// <summary>
    /// QC <c>minigame_addplayer(session, player)</c>: if the player is already in a DIFFERENT session, part it
    /// first (an identical session returns 0 — already in). Ask the game's "join" event for a team
    /// (<see cref="MinigameSession.Join"/>); on a non-zero team link the player (add to
    /// <see cref="MinigameSession.Players"/> + the <see cref="_active"/> map), force observer per
    /// <c>sv_minigames_observer</c>, and resend. Returns the assigned team (0 = rejected).
    /// </summary>
    public int AddPlayer(MinigameSession session, Player player)
    {
        if (_active.TryGetValue(player, out Active? existing))
        {
            if (ReferenceEquals(existing.Session, session))
                return 0;                       // QC: identical session → return 0
            RemovePlayer(existing.Session, player); // QC: minigame_rmplayer(active, player) before re-seating
        }

        // QC: new(minigame_player); int mgteam = minigame_event(session,"join",player,player_pointer).
        // The pointer carries the controlling client (QC player_pointer.minigame_players = player).
        var pointer = new MinigamePlayer(team: 0, slot: player.PlayerId > 0 ? player.PlayerId : 1, client: player);
        int mgteam = session.Join(pointer);

        if (mgteam != 0)
        {
            pointer.Team = mgteam;
            // QC: link player_pointer into session.minigame_players + set CS(player).active_minigame.
            session.Players.Add(pointer);
            _active[player] = new Active(session, pointer);
            _departed.Remove(player); // a re-seat (parted-then-rejoined this drain) supersedes the "you left" push

            // QC: if(!IS_OBSERVER(player) && autocvar_sv_minigames_observer) PutObserverInServer(player,...);
            //     if(autocvar_sv_minigames_observer==2) Player_SetForcedTeamIndex(player, TEAM_FORCE_SPECTATOR);
            int observerMode = _cvarInt("sv_minigames_observer");
            if (!player.IsObserver && observerMode != 0)
                ObserverForcer?.Invoke(player, observerMode == 2);

            MarkDirty(session);                 // QC minigame_resend(session)
            Log.Trace($":minigame:join:{session.NetName}:{player.PlayerId}:{player.NetName}");
        }
        else
        {
            // QC: delete(player_pointer) — nothing to clean up here (the pointer was never linked).
            Log.Trace($":minigame:joinfail:{session.NetName}:{player.PlayerId}:{player.NetName}");
        }

        return mgteam;
    }

    // =============================================================================================
    // part_minigame / minigame_rmplayer / player_clear_minigame (QC sv_minigames.qc:216/17/6)
    // =============================================================================================

    /// <summary>
    /// QC <c>part_minigame(player)</c>: if the player has an active session, <see cref="RemovePlayer"/> them
    /// from it. No-op if they aren't in a minigame.
    /// </summary>
    public void Part(Player player)
    {
        if (_active.TryGetValue(player, out Active? a))
            RemovePlayer(a.Session, player);
    }

    /// <summary>
    /// QC <c>minigame_rmplayer(session, player)</c>: remove a player's pointer from a session. SPECIAL CASE
    /// (the QC head-with-no-list_next branch): if the leaver was the session's ONLY player, <see cref="End"/>
    /// the whole session and fire NO "part" event. Otherwise fire the game's "part", unlink the pointer, and
    /// clear the player's minigame state.
    /// </summary>
    public void RemovePlayer(MinigameSession session, Player player)
    {
        if (!_active.TryGetValue(player, out Active? a) || !ReferenceEquals(a.Session, session))
            return;

        // QC: the linked-list head with list_next==NULL means this is the ONLY player → end_minigame (no part).
        // Mirror it by the count of CLIENT-controlled players (AI pointers, slot 0, aren't human leavers but the
        // QC list only ever held the human player_pointers; AI paddles live on the game state, not the list).
        if (CountHumanPlayers(session) <= 1)
        {
            End(session);                       // QC end_minigame — clears _active for every player below
            return;
        }

        // QC: minigame_event(session,"part",player); unlink + delete player_pointer; player_clear_minigame.
        session.Part(a.Pointer);
        session.Players.Remove(a.Pointer);
        _active.Remove(player);
        _departed.Add(player);        // push the leaver a "no active minigame" envelope (QC deactivate)
        ClearMinigame(player);
        Log.Trace($":minigame:part:{session.NetName}:{player.PlayerId}:{player.NetName}");
        MarkDirty(session);
    }

    /// <summary>QC <c>player_clear_minigame(player)</c>: restore the player to the normal walk/team state. The
    /// movetype/forced-team restoration is delegated to the host (<see cref="ClearForcer"/>); the active-session
    /// map clear is done by the caller (so End can batch it).</summary>
    private void ClearMinigame(Player player) => ClearForcer?.Invoke(player);

    // =============================================================================================
    // end_minigame / end_minigames (QC sv_minigames.qc:224/255)
    // =============================================================================================

    /// <summary>
    /// QC <c>end_minigame(session)</c>: unlink from <see cref="Sessions"/>, fire the game's "end" (clears the
    /// board/pieces), clear every seated player's minigame state, and drop the session. Idempotent.
    /// </summary>
    public void End(MinigameSession session)
    {
        // QC: unlink from the minigame_sessions list (whether or not it was the head).
        _sessions.Remove(session);
        if (!string.IsNullOrEmpty(session.NetName))
            _byName.Remove(session.NetName);

        // QC: minigame_event(session,"end") — delete owned pieces / bespoke state.
        session.End();
        Log.Trace($":minigame:end:{session.NetName}");

        // QC: for each minigame_player → player_clear_minigame + delete. We must drop EVERY client mapped to
        // this session (collect first; mutating _active during iteration is unsafe).
        var leavers = new List<Player>();
        foreach (var kv in _active)
            if (ReferenceEquals(kv.Value.Session, session))
                leavers.Add(kv.Key);
        foreach (Player p in leavers)
        {
            _active.Remove(p);
            _departed.Add(p);     // push each a "no active minigame" envelope (QC deactivate on the removal)
            ClearMinigame(p);
        }

        session.Players.Clear();
        _dirty.Remove(session); // no point re-sending a dead session
    }

    /// <summary>QC <c>end_minigames()</c>: end every live session (map reset / server shutdown).</summary>
    public void EndAll()
    {
        // QC: while(minigame_sessions) end_minigame(minigame_sessions). Snapshot first (End mutates the list).
        foreach (MinigameSession s in new List<MinigameSession>(_sessions))
            End(s);
    }

    // =============================================================================================
    // invite_minigame (QC sv_minigames.qc:263)
    // =============================================================================================

    /// <summary>
    /// QC <c>invite_minigame(inviter, player)</c>: returns "" on success, else a human-readable ERROR string
    /// (the exact QC strings). Validates that the inviter is in a game, the target is a valid OTHER player not
    /// play-banned and not already in the inviter's game. The actual notification send (QC Send_Notification
    /// INFO_MINIGAME_INVITE) is the host's job — this returns the resolved session netname on success via
    /// <paramref name="sessionName"/> so the caller can dispatch the notify.
    /// </summary>
    public string Invite(Player inviter, Player? player, out string sessionName)
    {
        sessionName = "";
        MinigameSession? inviterGame = ActiveSessionOf(inviter);
        if (inviterGame is null)
            return "Invalid minigame";
        if (player is null || !IsRealClient(player))
            return "Invalid player";
        if (ReferenceEquals(inviter, player))
            return "You can't invite yourself";
        if (_cvarBool("g_playban_minigames") && IsPlayBanned(player))
            return "You can't invite a banned player";
        if (ReferenceEquals(ActiveSessionOf(player), inviterGame))
            return $"{player.NetName} is already playing";

        sessionName = inviterGame.NetName;
        Log.Trace($":minigame:invite:{inviterGame.NetName}:{player.PlayerId}:{player.NetName}");
        return "";
    }

    /// <summary>QC <c>PlayerInList(player, autocvar_g_playban_list)</c>: the play-ban list membership test.
    /// Delegated to the host-provided <see cref="PlayBanned"/> predicate (the list lives in the server layer,
    /// unreachable from XonoticGodot.Common). With no predicate wired it returns false (faithful no-op); the
    /// <c>g_playban_minigames</c> cvar gate at the call site still applies and ships 0.</summary>
    private bool IsPlayBanned(Player player) => PlayBanned?.Invoke(player) ?? false;

    // =============================================================================================
    // minigame_find_player / MinigameImpulse (QC sv_minigames.qc:285/296)
    // =============================================================================================

    /// <summary>QC <c>minigame_find_player(client)</c>: the player's pointer in its active session, or null.</summary>
    public MinigamePlayer? FindPlayer(Player player) => PointerOf(player);

    /// <summary>
    /// QC <c>MinigameImpulse(this, imp)</c>: if the player is in a session, dispatch the impulse to the game as
    /// a "cmd". The port's <see cref="Minigame.Move"/> is the synchronous successor to the QC "cmd"/"impulse"
    /// event; impulses map to the per-game discrete tokens. Returns true if the game consumed it.
    /// </summary>
    public bool Impulse(Player player, int impulse)
    {
        if (impulse == 0)
            return false;
        if (!_active.TryGetValue(player, out Active? a))
            return false;
        // QC routes the raw impulse into the game's "impulse" event; the port's engines accept their discrete
        // tokens through Move. There is no impulse→token table in the P0 games (Pong drives via "move <bits>"
        // from the client), so an impulse simply re-runs the pending move if any. Marked dirty so a state change
        // is networked. Returning the Move acceptance keeps the QC "consumed?" contract.
        MoveResult r = a.Session.Move(a.Pointer, impulse.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (r != MoveResult.Invalid)
            MarkDirty(a.Session);
        return r != MoveResult.Invalid;
    }

    // =============================================================================================
    // move dispatch (the ClientCommand_minigame "cmd" forward → the game's Move)
    // =============================================================================================

    /// <summary>
    /// Forward a free-form command tail to the player's active game (QC ClientCommand_minigame's fall-through:
    /// <c>minigame_event(active_minigame,"cmd",e,arg_c,subcommand)</c>). The port's engines take the move as a
    /// single string via <see cref="Minigame.Move"/> (grid games expect the bare tile/column, e.g. "b2"/"a";
    /// Pong expects a verb like "throw"/"move 1"/"pong_aimore"). Returns whether the game accepted it; marks
    /// the session dirty on any non-invalid result so the change is networked.
    /// </summary>
    public bool ForwardMove(Player player, string move)
    {
        if (!_active.TryGetValue(player, out Active? a) || string.IsNullOrEmpty(move))
            return false;
        MoveResult r = a.Session.Move(a.Pointer, move);
        if (r != MoveResult.Invalid)
            MarkDirty(a.Session);
        return r != MoveResult.Invalid;
    }

    // =============================================================================================
    // per-frame tick (the real-time games: QC pong_ball_think / pong_paddle_think on sys_ticrate)
    // =============================================================================================

    /// <summary>
    /// Advance every live session's real-time simulation by <paramref name="dt"/> seconds (QC the per-think
    /// drivers scheduled on sys_ticrate). Pong is the only real-time P0 game; <see cref="Pong.Tick"/> no-ops on
    /// the turn-based ones. A session whose Pong state advanced (a ball moved / a goal scored) is marked dirty
    /// so the host re-sends its snapshot.
    /// </summary>
    public void Tick(float dt)
    {
        if (dt <= 0f || _sessions.Count == 0)
            return;
        // Iterate a snapshot: a tick can End a session (none of the P0 games do, but be safe) — if a future
        // real-time game called End() inside its Tick, End()'s _sessions.Remove would otherwise throw mid-enumeration.
        foreach (MinigameSession s in _sessions.ToArray())
        {
            PongState? pong = Pong.StateOf(s);
            if (pong is { Playing: true })
            {
                Pong.Tick(s, dt);
                MarkDirty(s);   // real-time: the board changes every tick while playing
            }
        }
    }

    // =============================================================================================
    // helpers
    // =============================================================================================

    /// <summary>QC <c>IS_REAL_CLIENT(player)</c>: a connected, non-bot client (FL_CLIENT, not an AI). A minigame
    /// is only startable/joinable by a human (QC the start/join gates).</summary>
    private static bool IsRealClient(Player player)
        => (player.Flags & EntFlags.Client) != 0 && !player.IsBot;

    /// <summary>The number of human (client-controlled) players seated in a session — the QC
    /// <c>minigame_players</c> list only ever held the human pointers (AI paddles live on the game state).</summary>
    private int CountHumanPlayers(MinigameSession session)
    {
        int n = 0;
        foreach (var kv in _active)
            if (ReferenceEquals(kv.Value.Session, session))
                n++;
        return n;
    }
}
