using XonoticGodot.Common.Gameplay;
using XonoticGodot.Net;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The top-level packet framing for the XonoticGodot client/server link, plus the build-parity gate.
///
/// Two layers of "message id" exist:
/// <list type="bullet">
///   <item><b>This file</b> — the <see cref="NetControl"/> byte that prefixes every datagram and says what
///         KIND of packet it is (handshake, a server snapshot frame, a client input frame, a bundle of
///         server events). It is the analogue of the DP top-level <c>svc_*</c>/<c>clc_*</c> framing.</item>
///   <item><see cref="NetMessageId"/> (XonoticGodot.Net) — the per-message id INSIDE a frame, dispatched by
///         <see cref="NetDispatcher"/> (Linked entity updates, Temp events, C2S input). The QC
///         <c>LinkedEntities</c>/<c>TempEntities</c>/<c>C2S_Protocol</c> registries.</item>
/// </list>
///
/// The transport (<see cref="NetTransport"/>) frames raw bytes on two ENet channels:
/// <see cref="ReliableChannel"/> for things that must arrive in order (the handshake, entity spawns/removes,
/// notifications, scores) and <see cref="UnreliableChannel"/> for the high-rate, loss-tolerant streams
/// (per-tick snapshots, input frames, fire-and-forget effects) — exactly the reliable/unreliable split the
/// networking spec calls for.
/// </summary>
public static class NetProtocol
{
    /// <summary>
    /// The wire protocol version. Bump on ANY incompatible change to the framing/serialization that the
    /// registry content hash would not catch (e.g. a change to <see cref="InputCommand.Serialize"/> or the
    /// snapshot layout). Mixed with the registry hashes into <see cref="BuildParity"/>.
    ///
    /// v2: the SoundBundle record gained a source entity net-id + loop/stop flags (<see cref="XonoticGodot.Net.SoundWire"/>)
    /// for DP's entity+channel looping-sound model (Arc beam loop, vehicle engines).
    /// v3: added the console string-command channel (<see cref="NetControl.ClientCommand"/> /
    /// <see cref="NetControl.ServerPrint"/>) — the DP <c>clc_stringcmd</c> / <c>svc_print</c> pair that lets the
    /// in-game console run gameplay commands against a remote server and print the reply.
    /// v4: the snapshot gained the <see cref="XonoticGodot.Net.ScoreInfoBlock"/> (QC ENT_CLIENT_SCORES_INFO) — the
    /// active per-mode score label/flag set + gametype/teamplay, sent before the per-player
    /// <see cref="XonoticGodot.Net.ScoreboardBlock"/> so a remote client's networked-column layout matches the server
    /// (without it the scoreboard block's layout hash disagreed and was silently dropped). The ScoreboardBlock
    /// row also gained the entcs name+team slice.
    /// v5: the C2S <see cref="InputCommand"/> gained a one-shot <see cref="InputCommand.Impulse"/> byte (QC
    /// usercmd.impulse) so a human on the net path can switch weapons / reload — the client samples weapon binds
    /// into it and the server dispatches it through the gated <c>impulse</c> command path (<c>WeaponImpulses</c>).
    /// v6: added the minigame session snapshot channel (<see cref="NetControl.MinigameState"/>) — the S2C push
    /// of a live <c>MinigameSession</c> (board/turn/winner/players + bespoke Pong state) to its participating
    /// peer(s), the C# stand-in for QC's per-entity minigame networking (ENT_CLIENT_MINIGAME). The <c>cmd
    /// minigame …</c> command itself rides the existing <see cref="NetControl.ClientCommand"/> channel.
    /// v7 (the SINGLE wave-A3 bump — T53/T57 additions ride it too):
    /// <list type="number">
    ///   <item>the snapshot's <see cref="XonoticGodot.Net.MoveVarsBlock"/> grew from 40 to 46 entries (the full
    ///         stats.qh MOVEVARS breadth: g_movement_highspeed(+_q3_compat), sv_gameplayfix_nudgeoutofsolid,
    ///         sv_wallclip, sv_nostep, sv_slick_applygravity — appended, prefix-stable);</item>
    ///   <item>a per-peer preset-RESOLVED physics block follows the global movevars block: one bool, then —
    ///         when true — a MoveVarsBlock vector resolved through g_physics_clientselect/cl_physics
    ///         (Physics_ClientOption, player.qc:18-42). A count-0 block clears the client's override. Hash-gated
    ///         per peer; always the bare false bool when client physics selection is off (the stock default);</item>
    ///   <item>RESERVED: T53's mode-stats block — a bool(+block) slot between the scores block and the entity
    ///         section of the snapshot (see the [A3 reserved] markers in ServerNet.BroadcastSnapshots /
    ///         ClientNet.HandleSnapshot). Absent until T53's snippets land (no wire bytes — doc-only);</item>
    ///   <item>RESERVED: T57's accuracy fields — appended at the END of the owner state (after spectateeStatus,
    ///         per the append-at-END lockstep contract; see the markers in ServerNet.WriteOwnerState and the
    ///         ClientNet owner read). Absent until T57's snippets land.</item>
    /// </list>
    /// Any new top-level frame kind starts at <see cref="NetControl"/> id 20 (Waypoints=19 is the last used).
    ///
    /// v8: added the match-clock channel (<see cref="NetControl.MatchState"/>) — a global S2C push of
    /// GAMESTARTTIME / TIMELIMIT / warmup so the TIMER panel works on the play path. Additive (old clients drop
    /// the unknown frame), but the version bump keeps the parity hash honest.
    ///
    /// v9: added the waypoint-sprite channel (<see cref="NetControl.Waypoints"/>) — per-peer team/rule-filtered
    /// waypoint sprites (CTF flags + player pings + objective markers) driving the 3D in-world sprite layer + the
    /// radar icons (the C# port of QC's ENT_CLIENT_WAYPOINT entities).
    ///
    /// v10: added the items-time channel (<see cref="NetControl.ItemsTime"/>) — the C# port of QC's CSQC
    /// <c>itemstime</c> net-temp message (itemstime.qc IT_Write/NET_HANDLE). A per-peer push of the item
    /// respawn-time table (with the negative "available now" encoding) plus the live <c>STAT(ITEMSTIME)</c>
    /// tier, gated by QC's <c>Item_ItemsTime_SetTimesForAllPlayers</c> send rule
    /// (<c>warmup_stage || !IS_PLAYER || sv_itemstime==2</c>) so the ItemsTimePanel works for a pure remote
    /// client (not just the listen host). Unknown to old clients (dispatch falls through harmlessly).
    ///
    /// v11: extended the match-clock channel (<see cref="NetControl.MatchState"/>) with STAT(OVERTIMES) — a
    /// trailing 32-bit field so the TIMER panel's persistent "Overtime #N" / "Sudden Death" subtext shows on
    /// every client (the one-shot overtime center notifications were already wired). Same-build lockstep: the
    /// field is read unconditionally, so the version bump keeps the parity hash honest.
    ///
    /// v12: the C2S input button byte gained the PHYS_INPUT_BUTTON_CHAT bit (<see cref="InputButtons.Chat"/>,
    /// 1&lt;&lt;7). The client sets it whenever a text prompt (the in-game console) is open and the server decodes
    /// it into <c>MovementInput.Typing</c> → <c>player.ButtonChat</c>, so the typing exemption (camp-check
    /// g_campcheck_typecheck, type-frag, spawn-near-teammate, monster-typefrag) is finally live for real network
    /// clients. The byte size is unchanged (a previously-zero bit now carries meaning); the bump keeps the parity
    /// hash honest so a mixed-build client can't silently mis-signal typing.
    /// </summary>
    /// v13: added the end-of-match map-vote channel (<see cref="NetControl.MapVote"/>) — a per-peer S2C push of
    /// the live ballot (running/finished flags, gametype/abstain/detail, the seconds-remaining countdown, each
    /// candidate's name/votes/availability/suggester, the winner, and THIS peer's own-vote index) so a pure
    /// remote (--connect) client can see the ballot and have its number-key vote highlighted. The vote CAST
    /// itself rides the existing C2S <see cref="InputCommand.Impulse"/> byte (v5): the server's gated impulse path
    /// (<c>Commands.DispatchImpulse</c>) already routes impulse 1..N to <c>MapVoting.CastVote</c> while the vote
    /// runs, so only the S2C ballot needed a new opcode. Additive (old clients drop the unknown frame), but the
    /// version bump keeps the parity hash honest.
    ///
    /// v14 (the client-map-load remote-HUD bump — three same-build lockstep changes ride it together):
    /// <list type="number">
    ///   <item>the HandshakeAccept gained two trailing strings — the server's current map name + gametype — so a
    ///         pure --connect client can load the BSP for world render + prediction collision
    ///         (<c>NetGame.LoadClientMapFromServer</c>);</item>
    ///   <item>the owner block gained the <see cref="XonoticGodot.Net.OwnerInventory"/> tail (ammo pools, owned-
    ///         weapon bitset, unlimited-ammo, STAT_ITEMS flag bits) feeding the pure client's full HUD;</item>
    ///   <item>the snapshot entity section now INCLUDES the recipient's own entity (the old encoder excluded it
    ///         via excludeEntNum) — the client diverts it into <c>ClientNet.LocalState</c> (never interpolated),
    ///         which every "watched == self" consumer reads (viewmodel anim frame/colors, vortex glow, powerup
    ///         status timers, hitsound damage diff, own name tag). Matches Base: CSQC receives the local
    ///         player's own entity.</item>
    /// </list>
    /// All three are unconditional reads, so mixed builds would mis-parse every snapshot — the bump makes
    /// BuildParity reject them at handshake instead.
    ///
    /// v15 (the ±4096 coord-wrap fix, r16): snapshot entity Origin/Velocity switched from
    /// <c>NetPrecision.Low</c> (13-bit fixed point — EncodeCoord13's unchecked short cast wrapped past
    /// ±4096 qu, teleporting everything on implosion's blue half into the void; bolt velocities ≥4096 qu/s
    /// sign-flipped extrapolation everywhere) to full 32-bit floats — DP7's coord path, matching the owner
    /// block. Wire layout change in every entity delta ⇒ version bump.
    public const uint ProtocolVersion = 15;

    /// <summary>Ordered, reliable ENet channel — handshake, spawns/removes, notifications, scores.</summary>
    public const int ReliableChannel = 0;

    /// <summary>Unreliable ENet channel — snapshots, input frames, fire-and-forget effects/temp-entities.</summary>
    public const int UnreliableChannel = 1;

    /// <summary>Number of ENet channels the host/client allocate.</summary>
    public const int ChannelCount = 2;

    /// <summary>
    /// The largest payload (bytes) we send on the <see cref="UnreliableChannel"/>. Godot's ENet host reports an
    /// MTU of 1392; an UNRELIABLE packet above it is sent as a lossy unreliable-fragment datagram (Godot logs a
    /// "Sending N bytes unreliably which is above the MTU" warning + a higher drop rate — a lost fragment loses
    /// the WHOLE packet). A snapshot only exceeds this on its initial full-baseline frame (every static map item
    /// at once, before the client's first ack lets the server delta-compress it) or after packet loss reverts the
    /// stream to full baselines — both must arrive, so <see cref="ServerNet.BroadcastSnapshots"/> promotes an
    /// oversized snapshot to the <see cref="ReliableChannel"/> (ENet fragments reliable packets losslessly).
    /// Kept a touch under the reported MTU to leave headroom for ENet's per-packet overhead.
    /// </summary>
    public const int MaxUnreliablePayload = 1350;

    /// <summary>
    /// Build-parity gate: a single hash mixing the protocol version with the content hashes of every
    /// registry whose ORDER the client and server must agree on (effects, notifications) — the analogue of
    /// QC's registry-hash handshake (registry_net.qh) that replaced the DP <c>csprogs.dat</c> push. A client
    /// and server with mismatched gameplay content (different effect/notification tables, or protocol) hash
    /// differently and are rejected at <see cref="NetControl.HandshakeRequest"/> time.
    ///
    /// FNV-1a-style fold of the constituent hashes so adding a registry later is a one-line change.
    /// </summary>
    public static uint BuildParity()
    {
        uint h = 2166136261u;
        h = Mix(h, ProtocolVersion);
        h = Mix(h, Effects.Hash);
        h = Mix(h, Notifications.Hash);
        // NetMessageId is a hand-authored enum today (not a registry); fold its highest assigned id so a
        // renumber is caught too. When it becomes source-generated this becomes Registry<NetMsg>.ContentHash.
        h = Mix(h, (uint)NetMessageId.Temp_Explosion);
        return h;
    }

    private static uint Mix(uint h, uint value)
    {
        // fold each byte of value through FNV-1a.
        for (int i = 0; i < 4; i++)
        {
            h ^= value & 0xFF;
            h *= 16777619u;
            value >>= 8;
        }
        return h;
    }
}

/// <summary>
/// The leading byte of every datagram — the kind of packet. Distinct from the per-message
/// <see cref="NetMessageId"/> (which identifies messages WITHIN a snapshot/event frame).
/// </summary>
public enum NetControl : byte
{
    None = 0,

    // ---- handshake (reliable channel) ----
    /// <summary>Client → server on connect: protocol/build-parity hash + the client's name + its public identity key.</summary>
    HandshakeRequest = 1,
    /// <summary>Server → client: accepted; carries the assigned entity id and the server tick rate.</summary>
    HandshakeAccept = 2,
    /// <summary>Server → client: rejected (build-parity mismatch / server full / failed auth); carries a reason string.</summary>
    HandshakeReject = 3,
    /// <summary>Server → client: build-parity passed; here is a random challenge — sign it with your identity key.</summary>
    HandshakeChallenge = 4,
    /// <summary>Client → server: the signature over the challenge (proves ownership of the public key). SessionAuth.</summary>
    HandshakeAuth = 5,

    // ---- gameplay frames ----
    /// <summary>Client → server (unreliable): a redundant tail of recent <see cref="InputCommand"/>s.</summary>
    InputFrame = 10,
    /// <summary>Server → client (unreliable): a world snapshot — acked input seq, server time, entity states.</summary>
    Snapshot = 11,
    /// <summary>Server → client (unreliable): a bundle of fire-and-forget events (effects, temp-entities).</summary>
    EventBundle = 12,
    /// <summary>Server → client (reliable): a bundle of must-arrive events (notifications, spawns, scores).</summary>
    ReliableBundle = 13,

    /// <summary>Server → client (unreliable): a bundle of fire-and-forget positional sounds (DP SV_StartSound) —
    /// see <c>ServerNet.FlushSounds</c> / <c>ClientNet.HandleSoundBundle</c>.</summary>
    SoundBundle = 14,

    // ---- console string commands (DP clc_stringcmd / svc_print) ----
    /// <summary>Client → server (reliable): a console command line to run on the server on the sender's behalf
    /// (the in-game console's gameplay commands — kill/say/team/…). DP <c>clc_stringcmd</c>.</summary>
    ClientCommand = 15,
    /// <summary>Server → client (reliable): a line of console output (a command reply / server notice) to print
    /// in the client console. DP <c>svc_print</c>.</summary>
    ServerPrint = 16,

    // ---- minigames (the C# stand-in for QC's per-entity ENT_CLIENT_MINIGAME networking) ----
    /// <summary>Server → client (reliable): a full minigame-session snapshot (board/turn/winner/players +
    /// bespoke Pong state, via <see cref="MinigameNetState.Encode"/>), sent to the session's participating peers
    /// when it changes. The client decodes it and drives the minigame board overlay + menu.</summary>
    MinigameState = 17,

    // ---- match clock (the C# stand-in for QC STAT(GAMESTARTTIME)/TIMELIMIT/WARMUP) ----
    /// <summary>Server → client (reliable): the global match-clock state — game start time, time limit, warmup
    /// stage + warmup limit, intermission/overtime — so the TIMER panel can count up/down on the play path.
    /// Broadcast to every accepted peer on change and ~1×/s (covers late joiners). Decoded into
    /// <see cref="ClientNet"/>'s match fields; unknown to old clients (dispatch falls through harmlessly).</summary>
    MatchState = 18,

    // ---- waypoint sprites (the C# port of QC's networked ENT_CLIENT_WAYPOINT entities) ----
    /// <summary>Server → client: the live waypoint sprites — gametype objectives (CTF flags, DOM points, KH
    /// keys…) + player pings (HERE/DANGER/HELPME) + deployed markers. Drives BOTH the 3D in-world sprite layer
    /// (icon/text + edge-arrow + health bar) and the radar icons. Sent per-peer (team/rule filtered) each network
    /// tick on the unreliable channel (positions move with carriers). Each entry = id + origin + team + sprite
    /// name + radar icon + color + health + fade + helpme + maxdist + hideable. Unknown to old clients.</summary>
    Waypoints = 19,

    // ---- items-time (the C# port of QC's CSQC itemstime net-temp message) ----
    /// <summary>Server → client (reliable): the item respawn-time table (QC <c>itemstime</c> IT_Write /
    /// NET_HANDLE) — for each tracked timed item (Mega/Big Health+Armor, Strength/Shield, the Superweapons
    /// aggregate) its absolute scheduled respawn time, with the negative "another copy available now" encoding;
    /// plus the live <c>STAT(ITEMSTIME)</c> tier (0/1/2). Sent per-peer, gated by QC's
    /// <c>Item_ItemsTime_SetTimesForAllPlayers</c> rule (only spectators in a live round, everyone in warmup /
    /// when <c>sv_itemstime==2</c>), so the ItemsTimePanel respawn countdowns work for a pure remote client.
    /// Decoded into <see cref="ClientNet"/>'s item-time fields; unknown to old clients.</summary>
    ItemsTime = 20,

    // ---- client init constants (the C# port of QC's ENT_CLIENT_INIT / ClientInit_misc bundle) ----
    /// <summary>Server → client (reliable): the one-shot ClientInit_misc constant bundle (QC server/client.qc:907) —
    /// the per-server gameplay constants the client needs at session init: the hook + arc shot origins, the fog
    /// string, armor blockpercent, damagepush speedfactor, serverflags, g_trueaim_minrange, and the nexball meter
    /// period. Sent once right after <see cref="HandshakeAccept"/> (the welcome/accept handshake). Decoded into
    /// <see cref="ClientNet"/>'s client-init constant stores; unknown to old clients (dispatch falls through).</summary>
    ClientInit = 21,

    // ---- end-of-match map vote (the C# port of QC's networked ENT_CLIENT_MAPVOTE entity) ----
    /// <summary>Server → client (reliable): the live end-of-match map/gametype ballot (QC client/mapvoting.qc
    /// <c>MapVote_Draw</c> fed by the <c>mapvote</c> net entity). Carries the running/finished flags, the
    /// gametype/abstain/detail flags, the seconds-remaining countdown, every candidate's name + vote count +
    /// availability + suggester, the winner (1-based, 0 = not yet), and THIS peer's own-vote candidate index
    /// (QC <c>mv_ownvote</c>, stamped per-peer from <c>MapVoting.SelectionOf</c>). Sent per-peer (the own-vote
    /// field differs per client) on change + ~2×/s while a vote runs, so the MapVotePanel lights up for a pure
    /// remote client (not just the listen host). The vote CAST rides the C2S impulse byte. Unknown to old
    /// clients (dispatch falls through harmlessly).</summary>
    MapVote = 22,

    // ---- onslaught radar links (the C# port of QC's networked ENT_CLIENT_RADARLINK entities) ----
    /// <summary>Server → client (unreliable): the live Onslaught control-point/generator connection lines (QC
    /// <c>ENT_CLIENT_RADARLINK</c> + <c>draw_teamradar_link</c>, client/teamradar.qc). Each entry is one power
    /// link — the two endpoint world positions (XY) plus each end's owning team color code — so the radar can
    /// draw the per-end team-colored connection quad between two linked nodes. Sent per-peer each network tick on
    /// the unreliable channel (the graph + ownership change as points are captured); only emitted in Onslaught.
    /// Decoded into <see cref="ClientNet"/>'s <c>RadarLinks</c> list; unknown to old clients (dispatch falls
    /// through harmlessly).</summary>
    RadarLinks = 23,
}
