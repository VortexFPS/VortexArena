using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The player actor — the C# successor to a QuakeC client edict transmuted into <c>CLASS(Player)</c>
/// (server/client.qc <c>PutPlayerInServer</c>, common/state.qh PlayerState). In QC a "player" is just an
/// edict with <c>FL_CLIENT</c> set and a pile of flat fields; the entity-model (ADR-0007) promotes the
/// player-specific gameplay state onto this dedicated subclass while the engine-shared fields
/// (Origin/Velocity/Health/Frags/…) stay on <see cref="Entity"/>.
///
/// Scope for this phase: the fields the DM match loop needs — frag score (kept on the base
/// <see cref="Entity.Frags"/>, exposed here as <see cref="ScoreFrags"/>), the respawn timer, the owned-weapon
/// set, and the bot flag. NOTE (cross-boundary): the full PlayerState component — weapon view-entities
/// (client), input buttons (net/client input), and spectator bookkeeping (client) — is deferred to those layers.
/// </summary>
public sealed class Player : Entity
{
    /// <summary>QC FRAGS_PLAYER (common/constants.qh): a live player's <c>.frags</c> STATUS baseline (0).</summary>
    public const int FragsPlayer = 0;

    /// <summary>QC FRAGS_SPECTATOR (common/constants.qh): the sentinel <c>.frags</c> STATUS for a spectator.</summary>
    public const int FragsSpectator = -666;

    /// <summary>QC FRAGS_PLAYER_OUT_OF_GAME (-616): a player who is in-game but eliminated (LMS/CA/round wait).</summary>
    public const int FragsOutOfGame = -616;

    /// <summary>QC FRAGS_LMS_LOSER (-616 family): an LMS player out of lives.</summary>
    public const int FragsLmsLoser = -616;

    public Player()
    {
        ClassName = "player";
    }

    /// <summary>
    /// The player's match score — QC <c>GameRules_scoring_add(..., SCORE, f)</c>. Now backed by the real
    /// per-column score table (<see cref="GameScores"/>'s SP_SCORE column), NOT the engine <see cref="Entity.Frags"/>
    /// field (which reverts to its QC role as the player STATUS sentinel — see <see cref="FragsStatus"/>). The
    /// gametypes keep writing this property; it routes their ±1 into the networked scoreboard. Whole frags only.
    /// </summary>
    public int ScoreFrags
    {
        get => GameScores.Get(this, GameScores.Score);
        set => GameScores.SetPlayer(this, GameScores.Score, value);
    }

    /// <summary>
    /// QC <c>.frags</c> as the player STATUS sentinel (FRAGS_PLAYER / FRAGS_SPECTATOR / FRAGS_OUT_OF_GAME),
    /// backed by the engine <see cref="Entity.Frags"/> field. Reset to <see cref="FragsPlayer"/> on every
    /// (re)spawn (PutPlayerInServer); the running match score lives in <see cref="ScoreFrags"/>.
    /// </summary>
    public int FragsStatus
    {
        get => (int)Frags;
        set => Frags = value;
    }

    /// <summary>
    /// QC <c>.respawn_time</c> (server/client.qc): absolute sim time (<see cref="Services.IGameClock.Time"/>)
    /// at which this player becomes eligible to respawn. 0 means "not waiting to respawn" (alive, or never
    /// scheduled). Set when the player dies; consumed by the match loop. <c>new</c>: intentionally distinct
    /// from the vehicle-entity <c>Entity.RespawnTime</c> (VehicleCommon) — a player is never a vehicle.
    /// </summary>
    public new float RespawnTime;

    /// <summary>
    /// QC <c>.spawn_time</c> (server/client.qc:690) — the sim time this player most recently spawned.
    /// Used by CTS trigger_multiple to detect whether a client has respawned since they last fired the trigger
    /// (multi.qc multi_trigger: <c>this.enemy.spawn_time &lt;= triggertime</c> → "haven't respawned yet").
    /// Set to <see cref="Services.IGameClock.Time"/> each time the player is placed in-world by
    /// <see cref="SpawnSystem"/>. 0 at construction (player has never spawned).
    /// </summary>
    public float SpawnTime;

    /// <summary>
    /// QC <c>STAT(WEAPONS, this)</c> — the owned-weapon set (a WEPSET bitfield in QC). Modeled here as a set
    /// of weapon NetNames ("blaster", "machinegun", …) so it reads against <see cref="Weapons"/> without a
    /// bit-index registry. Repopulated from the starting loadout on every spawn (QC <c>start_weapons</c>).
    /// </summary>
    public readonly HashSet<string> OwnedWeapons = new(StringComparer.Ordinal);

    /// <summary>QC IS_BOT_CLIENT(this) — true for an AI-controlled player (affects spawn restriction filtering).</summary>
    public bool IsBot;

    /// <summary>The floating chat-bubble entity shown over this player while typing (QC <c>.chatbubbleentity</c>,
    /// server/client.qc <c>UpdateChatBubble</c>/<c>ChatBubbleThink</c>). Spawned, followed and freed by
    /// <c>GameWorld.UpdateChatBubble</c>. (playtest #35c)</summary>
    public Entity? ChatBubble;

    /// <summary>
    /// QC the bot's <c>skill</c> level (0..10), set when the bot is created. Feeds the skill-weighted team
    /// balance (Teamplay.SkillProvider) so a strong bot counts as "more" than a weak one. Meaningless for humans
    /// (they use a reference rating until real TrueSkill mu/variance ratings are modeled).
    /// </summary>
    public float BotSkill = 5f;

    /// <summary>
    /// QC <c>.bot_moveskill</c>: a movement-skill term added to <see cref="BotSkill"/> wherever the AI decides to
    /// bunnyhop (havocbot.qc:1315 gates on <c>skill + bot_moveskill &gt;= bot_ai_bunnyhop_skilloffset</c>). Stock
    /// non-campaign default is 0 (the READSKILL reward factor is 0; bot.qc:276). The midair mutator forces this to 0
    /// on spawn to suppress bunnyhopping WITHOUT degrading aim/reaction (which key off <see cref="BotSkill"/>).
    /// </summary>
    public float BotMoveSkill;

    /// <summary>
    /// QC <c>.bot_aggresskill</c> (server/bot/default/bot.qc:282, READSKILL column 11): a per-bot aggression
    /// modifier added to <see cref="BotSkill"/> in the fire decision (aim.qc:325-328) — it widens the
    /// close-range "always fire" band, raises the long-range fire chance, and shortens the fire-timer cooldown,
    /// so an aggressive bot shoots more eagerly at distance. Stock non-campaign default 0 (the READSKILL reward
    /// factor is 0; populated only from the bots.txt skill columns). Read by <see cref="BotAim"/>.
    /// </summary>
    public float BotAggresSkill;

    /// <summary>
    /// QC <c>.bot_aimskill</c> (server/bot/default/bot.qc:285, READSKILL column 13): a per-bot aiming modifier
    /// added to <see cref="BotSkill"/> in the max-fire-deviation formula (aim.qc:372) — a higher aim skill
    /// narrows the fire cone (tighter aim required before shooting). Stock non-campaign default 0 (READSKILL
    /// reward factor 0; populated only from the bots.txt skill columns). Read by <see cref="BotAim"/>.
    /// </summary>
    public float BotAimSkill;

    /// <summary>QC <c>.maycheat</c>: a per-player override that always permits cheats regardless of <c>sv_cheats</c>.</summary>
    public bool MayCheat;

    /// <summary>QC <c>.lip</c> (reused by server/cheats.qc): the count of corpse clones this player has spawned this
    /// life via the CLONE_MOVING/CLONE_STANDING cheats. The clone impulses are permitted (independent of
    /// <c>sv_cheats</c>) while this is below <c>sv_clones</c> — see <c>Cheats.Allowed</c>.</summary>
    public int Lip;

    /// <summary>
    /// QC <c>.speedrunning</c> (server/cheats.qc CHIMPULSE_SPEEDRUN): set when the player has teleported back to
    /// their <see cref="PersonalCheckpoint"/> via the SPEEDRUN (141) cheat impulse. A flag the speedrun timing
    /// reads to know a run was aborted/reset.
    /// </summary>
    public bool Speedrunning;

    /// <summary>
    /// QC the <c>.personal</c> (<c>personal_wp</c>) checkpoint entity (server/cheats.qc CHIMPULSE_SPEEDRUN_INIT):
    /// the player's snapshotted speedrun checkpoint — origin/view-angle/velocity plus the full resource/weapon/
    /// item/status-effect/pause-timer state taken when <c>waypoint_personal_here</c> (impulse 30) is pressed, and
    /// restored when <c>SPEEDRUN</c> (impulse 141) is pressed. Modeled as a value snapshot on the player rather
    /// than a separate entity (the QC <c>personal_wp</c> edict only ever stores state — it is never simulated).
    /// <c>null</c> until the player first deploys a personal waypoint this life.
    /// </summary>
    public PersonalCheckpoint? PersonalCheckpoint;

    /// <summary>
    /// QC <c>.winning</c>: set at match end for the player(s) who won (the scoreboard leader, or each member of
    /// the winning team). Read by the campaign win/lose check and the end-of-match flow. Cleared on (re)spawn.
    /// </summary>
    public bool Winning;

    /// <summary>
    /// QC <c>.crypto_idfp</c>: the player's stable persistent identity (the SessionAuth public-key fingerprint),
    /// set by the host on connect. "" for an anonymous/unauthenticated player — race records can't be stored for
    /// those (the ranking table is keyed by this UID). See <see cref="RaceRecords"/>.
    /// </summary>
    public string PersistentId = "";

    /// <summary>
    /// QC <c>.netaddress</c>: the client's remote address ("ip:port", or "local"/"bot" for a non-networked
    /// client), set by the host on connect. Used for IP banning, the mute/playban/voteban prefix lists, and
    /// the event log. "" / "bot" / "local" for a non-remote client. See the server ban + stats systems.
    /// </summary>
    public string NetAddress = "";

    /// <summary>
    /// QC <c>.playerid</c>: a per-match unique integer id for this client (distinct from the net id), used as
    /// the stable key in the event log and player-stats reports. Assigned by the host on connect (0 = unset).
    /// </summary>
    public int PlayerId;

    // ===== observer / join lifecycle (server/client.qc ClientConnect → TRANSMUTE(Observer) → Join) =====
    // In QC a connecting client is transmuted to an Observer edict and only becomes a live Player on Join
    // (on +jump/+attack, or a delayed autojoin). This port keeps a client as a Player subclass throughout
    // (ADR-0007) and models the Observer phase with these flags; the ClientManager drives the transition.

    /// <summary>
    /// QC the Observer phase after ClientConnect's <c>TRANSMUTE(Observer)</c>: the client is connected
    /// (FL_CLIENT) but is NOT yet a live, spawned player — it has no loadout and doesn't move/take damage/score.
    /// Cleared by <c>Join</c> (server/client.qc) when the player actually enters the match. A connecting human
    /// starts an observer; a bot auto-joins immediately so it never observes (ObserverOrSpectatorThink).
    /// </summary>
    public bool IsObserver;

    /// <summary>
    /// QC <c>.wants_join</c> (server/client.qc): the player's join intent while observing — 0 = none, &gt;0 = a
    /// chosen team index, -1 = autoselect. Pressing +jump/+attack as an observer sets this and gates
    /// <c>joinAllowed</c>; <c>Join</c> clears it. (Team selection UX is deferred; the core uses 0 = autoselect.)
    /// </summary>
    public int WantsJoin;

    /// <summary>
    /// QC <c>.clientcolors</c> (server/teamplay.qc <c>setcolor</c> / <c>SetPlayerColors</c>): the player's packed
    /// pants(low nibble)+shirt(high nibble) color, i.e. <c>16*shirt + pants</c>. In a team game it is forced to
    /// <c>16*team + team</c> (both nibbles the team color); in FFA it keeps the player's chosen shirt+pants from
    /// the <c>color</c> command. The player-model colormap derives from this. Server-authority value; the network
    /// of clientcolors to the client is deferred, but the encoding is kept faithful so team-from-color works.
    /// </summary>
    public int ClientColors;

    /// <summary>
    /// QC <c>.autojoin_checked</c> (server/client.qc): the one-shot autojoin latch. A bot sets it to 1 and joins
    /// once in ObserverOrSpectatorThink; a real client's delayed autojoin (PlayerPreThink, after MIN_SPEC_TIME)
    /// sets it to 1 after trying, or -1 to keep retrying briefly. 0 = not yet attempted.
    /// </summary>
    public int AutoJoinChecked;

    /// <summary>
    /// QC FL_JUMPRELEASED as used by ObserverOrSpectatorThink: the observer-phase edge tracker so a HELD
    /// +jump/+attack fires Join only once (true once the key is released, re-armed for the next press). Distinct
    /// from the physics <see cref="EntFlags.JumpReleased"/> bit, which PlayerPhysics owns for live players.
    /// </summary>
    public bool JoinJumpReleased = true;

    /// <summary>
    /// QC <c>CS(this).startplaytime</c> (server/client.qh:67): the absolute sim time
    /// (<see cref="Services.IGameClock.Time"/>) at which this client last switched from spectator to live
    /// player — set in <c>PutPlayerInServer</c> only on a spectator→player transition (QC client.qc:780, gated on
    /// the prior <c>killcount == FRAGS_SPECTATOR</c>), NOT on a mid-life respawn. Used as the per-client playtime
    /// denominator (e.g. the kick_teamkiller rate gate: <c>time - startplaytime</c>). 0 until the first Join.
    /// </summary>
    public float StartPlayTime;

    /// <summary>QC IS_DEAD(this): the player is dying or already dead (<see cref="Entity.DeadState"/> != No).</summary>
    public bool IsDead => DeadState != DeadFlag.No;

    /// <summary>
    /// QC <c>.death_origin</c> (server/damage.qh:67): the player's <c>.origin</c> latched at the moment of death,
    /// set in the Obituary path (<c>targ.death_origin = targ.origin</c>, server/damage.qc:238). Read by the
    /// "spawn near where you died" features — the spawn_near_teammate <c>closetodeath</c> selection
    /// (sv_spawn_near_teammate.qc:174) and the personal/here/danger waypoints (server/impulse.qc). Zero until the
    /// player's first death.
    /// </summary>
    public Vector3 DeathOrigin;

    /// <summary>
    /// The sim time (<c>Api.Clock.Time</c>) at which <see cref="DeathOrigin"/> was last latched — set alongside it
    /// in the Obituary path (DamageSystem.Killed). Backs the [R2] death-point spawn avoidance decay
    /// (<see cref="SpawnSystem"/> <c>g_spawn_avoid_death_time</c>): how long ago the player died. Zero until the
    /// player's first death (which, with a zero <see cref="DeathOrigin"/>, disables the penalty anyway).
    /// </summary>
    public float DeathTime;

    // ===== chat state (server/chat.qc Say + server/command/cmd.qc ignore-list CRUD — T46) =====
    // The flat per-client chat fields QC keeps on the clientstate (CS(this)): the three per-say-type flood
    // "next allowed time" stamps, the mute flag, and the ignore list. These must persist across ticks (a per-tick
    // reset would let a flooder past the throttle), so they live on the long-lived Player like the rest of CS().

    /// <summary>
    /// QC <c>.floodcontrol_chat</c> (server/chat.qc:156): the absolute sim time before which the next BROADCAST
    /// (public <c>say</c>) line is throttled as flooding. <see cref="Chat.Say"/> advances it by the per-line
    /// spacing on each accepted line; a message whose modeled time is below it is rejected (flood == 1). 0 = never
    /// said before (the QC field default).
    /// </summary>
    public float FloodControlChat;

    /// <summary>QC <c>.floodcontrol_chatteam</c> (server/chat.qc:172): the TEAM-chat (<c>say_team</c>) flood stamp.</summary>
    public float FloodControlChatTeam;

    /// <summary>QC <c>.floodcontrol_chattell</c> (server/chat.qc:165): the PRIVATE-chat (<c>tell</c>) flood stamp.</summary>
    public float FloodControlChatTell;

    /// <summary>
    /// QC <c>CS(this).muted</c> (server/chat.qc:255): when set, this player's chat is fake-accepted — the sender
    /// still sees their own message (the <c>say</c> echoes locally) but NO other client receives it. Set by the
    /// admin <c>mute</c> command (the chatban list), cleared by <c>unmute</c>.
    /// </summary>
    public bool Muted;

    /// <summary>
    /// QC <c>.ignore_list</c> (server/command/cmd.qc): the set of players this client has ignored — messages from
    /// a member are dropped before reaching this client's chat log. QC stored a space-separated tempstring of
    /// per-match entity ids; the port keys by the stable <see cref="PersistentId"/> so an ignore survives a
    /// reconnect within the match (and a future db-backed permanent ignore reads the same key). A player with no
    /// PersistentId still keys by it ("" — matches no real client, so an unauthenticated target can't be ignored,
    /// the same effective behavior as QC's permanent-ignore UID requirement).
    /// </summary>
    public readonly HashSet<string> IgnoreList = new(StringComparer.Ordinal);

    // ===== respawn state machine (server/client.qc PlayerThink dead branch + calculate_player_respawn_time) =====

    /// <summary>
    /// QC <c>.respawn_time_max</c>: the latest sim time the player may stay dead — once reached (and
    /// <see cref="RespawnFlag.Force"/> is set) the respawn is forced even without a button press. Set by
    /// <c>calculate_player_respawn_time</c>; 0 = unset.
    /// </summary>
    public float RespawnTimeMax;

    /// <summary>QC <c>.respawn_flags</c> (RESPAWN_FORCE/SILENT/DENY). Set on death, cleared on (re)spawn.</summary>
    public RespawnFlag RespawnFlags;

    /// <summary>QC <c>.respawn_countdown</c>: the next announcer number to count down from (-1 = no countdown).</summary>
    public int RespawnCountdown;

    /// <summary>
    /// The respawn timer the client should display (QC <c>STAT(RESPAWN_TIME)</c>): the absolute sim time the
    /// player becomes/became respawnable, NEGATED while <see cref="DeadFlag.Respawning"/> so the client can show
    /// "respawning" vs "press fire", and 0 while <see cref="RespawnFlag.Silent"/>. Networked in the owner snapshot.
    /// </summary>
    public float RespawnTimeStat;

    // ===== spectate / follow-cam (server/client.qc SetSpectatee / SpectateCopy / spectatee_status) =====

    /// <summary>
    /// QC <c>.enemy</c> while observing/spectating: the live player this observer is currently following
    /// (null = free-fly, not following anyone). Set by <c>SpectateNext</c>/<c>SpectatePrev</c>/<c>Spectate</c>.
    /// </summary>
    public Player? Spectatee;

    /// <summary>
    /// QC <c>spectatee_status</c> (server/client.qc:1904): 0 = a live player; this player's own net id =
    /// observing (free-fly, the client maps it to -1); another player's net id (&gt;0) = spectating that
    /// player (the client renders from their eyes). Networked in the owner snapshot (QC ClientData BIT(1)).
    /// </summary>
    public int SpectateeStatus;

    /// <summary>True once the player is dead and the respawn delay has elapsed (the match loop should respawn it).</summary>
    public bool IsAwaitingRespawn(float now) => IsDead && RespawnTime > 0f && now >= RespawnTime;

    /// <summary>Convenience: does this player currently own the given weapon (by NetName)?</summary>
    public bool HasWeapon(string weaponNetName) => OwnedWeapons.Contains(weaponNetName);
}
