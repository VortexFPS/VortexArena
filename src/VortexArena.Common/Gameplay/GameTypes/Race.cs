using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Race gametype — port of <c>CLASS(Race, Gametype)</c>
/// (common/gametypes/gametype/race/race.qh + sv_race.qc, server/race.qc).
///
/// Players race a fixed sequence of <c>trigger_race_checkpoint</c> entities; crossing the final checkpoint
/// completes a lap. The match runs in one of two modes (QC <c>g_race_qualifying</c>):
///  - qualifying: every racer runs solo against the clock; ranking is by fastest single lap
///    (QC SP_RACE_FASTEST, SFL_LOWER_IS_BETTER);
///  - race: head-to-head; first to the lap limit (cvar <c>g_race_laps_limit</c>) wins, ties broken by time.
/// The match ends when a racer reaches the lap limit, OR (QC <c>WinningCondition_Race</c>) once every racer
/// has finished — at which point a sudden-death overtime is started unless all laps were raced.
///
/// Faithfully ported (the timing/scoring rule):
///  - per-player lap + checkpoint + fastest-lap state (<see cref="RaceState"/>);
///  - lap-limit win check (<see cref="LapLimit"/>, QC g_race_laps_limit / GameRules_limit_score);
///  - "everyone finished ⇒ match over" condition (QC WinningCondition_Race n == c).
///
/// Faithfully ported (objective layer):
///  - the checkpoint trigger entities + ordered crossing detection (<see cref="SpawnCheckpoint"/> /
///    <see cref="CheckpointTouch"/>, QC trigger_race_checkpoint + checkpoint_passed / race_NextCheckpoint):
///    a racer must cross checkpoints in sequence; the finish line (index 0) closes a lap;
///  - per-player lap/checkpoint timing + fastest-lap tracking (<see cref="RaceState"/>, QC race_* fields);
///  - record persistence (QC race_readTime/race_writeTime via <see cref="RaceRecords"/>): closing a lap files
///    the lap time into the per-map top-99 ranking, classified for the host's INFO_RACE_* notification;
///  - qualifying mode (QC g_race_qualifying): solo time-trial — closing a lap re-teleports the racer to start
///    (the finish kill-delay <see cref="OnFinishRetract"/>), ranking by fastest lap; in plain race mode the
///    racer keeps going until the lap limit, then is retracted.
///
/// Faithfully ported (depth):
///  - penalty zones (<see cref="SpawnPenaltyZone"/>/<see cref="PenaltyTouch"/>/<see cref="SetPenalty"/>, QC
///    trigger_race_penalty + race_ImposePenaltyTime): a racer entering one is frozen for the penalty time in a
///    plain race, or has it added to the lap time in qualifying, expired by <see cref="Tick"/>;
///  - sudden-death overtime (<see cref="SuddenDeath"/>, QC WinningCondition_Race): the lap limit alone never
///    ends the race — it runs on until everyone finishes;
///  - the qualifying==2 → race transition (<see cref="TransitionQualifyingToRace"/>, QC rc_SetLimits +
///    reset_map_global): clears qualifying, restores the saved fraglimit/leadlimit/timelimit, re-runs the
///    score rules, and resets every racer;
///  - team race (<see cref="RaceTeams"/>, QC g_race_teams): laps add up into ST_RACE_LAPS.
///
/// Deferred (NOTE — cross-boundary): the forced-keyboard movement quantization (rc PlayerPhysics — a fairness
/// tweak to the move command), and the score networking/HUD — physics-input / client concerns.
/// </summary>
[GameType]
public sealed class Race : GameType
{
    // ----- lap limit cvars + default (gametype default laplimit=7) -----
    private const string CvarLapLimit     = "g_race_laps_limit";
    private const string CvarFragLimit    = "fraglimit"; // GameRules_limit_score writes the lap limit here
    private const int    DefaultLapLimit  = 7;           // gametype_init "laplimit=7" (race.qh m_legacydefaults)
    private const int    DefaultTeamLapLimit = 15;       // gametype_init "teamlaplimit=15" (race.qh m_legacydefaults)
    private const string CvarQualifying   = "g_race_qualifying";        // 0 = race, !=0 = qualifying time-trial
    private const string CvarTeams        = "g_race_teams";             // 0 = FFA, 2..4 = team race (laps add up)
    private const string CvarSuddenDeath  = "timelimit_suddendeath";    // overtime window (minutes); unused for the latch
    private const string CvarMapName      = "mapname";

    /// <summary>QC <c>g_race_teams</c> (sv_race.qc:14): 2/3/4 → the race is a team game (members add up laps to
    /// ST_RACE_LAPS). 0/1 = FFA. Bounded to 2..4 when set.</summary>
    public int RaceTeams
    {
        get
        {
            if (!TryCvar(CvarTeams, out float v) || v < 2f) return 0;
            return System.Math.Clamp((int)v, 2, 4);
        }
    }

    /// <summary>Per-player race timing state (QC race_* client fields + SP_RACE_* scores).</summary>
    public sealed class RaceState
    {
        /// <summary>QC <c>race_checkpoint</c>: index of the next checkpoint this racer must cross (-1 = none yet).</summary>
        public int NextCheckpoint = -1;

        /// <summary>Laps fully completed (QC SP_RACE_LAPS).</summary>
        public int LapsCompleted;

        /// <summary>Sim time the current lap started (QC race_movetime baseline). 0 = not started.</summary>
        public float LapStartTime;

        /// <summary>Best single-lap time in seconds so far (QC SP_RACE_FASTEST), or 0 if no lap finished.</summary>
        public float FastestLap;

        /// <summary>Cumulative time of all completed laps in seconds (QC SP_RACE_TIME ~ time − game_starttime).</summary>
        public float TotalTime;

        /// <summary>QC <c>race_completed</c>: this racer has reached the lap limit and finished the race.</summary>
        public bool Completed;

        /// <summary>
        /// QC <c>race_penalty</c> (sv_race.qc:141): absolute sim time until which the racer is frozen by a
        /// penalty zone (velocity zeroed + MOVETYPE_NONE). 0 = no active penalty.
        /// </summary>
        public float PenaltyUntil;

        /// <summary>
        /// The racer's movement integrator captured the moment the penalty freeze began, so releasing the freeze
        /// restores it (QC just clears <c>race_penalty</c> and the next PlayerPhysics frame re-applies the racer's
        /// real movetype — Walk on land, but Swim/Fly/Noclip if they were swimming/flying/noclipping). Restoring
        /// the captured value avoids forcing a swimming/flying racer back to Walk on release.
        /// </summary>
        public MoveType PenaltyPrevMoveType = MoveType.Walk;

        /// <summary>QC <c>race_penalty_accumulator</c>: penalty seconds added to a qualifying lap time (no freeze).</summary>
        public float PenaltyAccumulator;

        // ---- penalty HUD line (QC RACE_NET_PENALTY_RACE / _QUALIFYING → race_penaltytime/eventtime/reason) ----

        /// <summary>QC <c>race_penaltytime</c> feed: the magnitude (seconds) of the most recently imposed penalty,
        /// for the on-screen "PENALTY: Ns (reason)" line. 0 = none imposed yet.</summary>
        public float LastPenaltySeconds;

        /// <summary>QC <c>race_penaltyeventtime</c> feed: the absolute sim time the most recent penalty was imposed
        /// (the HUD penalty line fades over a ~2 s window from this stamp). 0 = none.</summary>
        public float LastPenaltyEventTime;

        /// <summary>QC <c>race_penaltyreason</c> feed: the human-readable reason of the most recent penalty (QC's
        /// trigger_race_penalty <c>.race_penalty_reason</c>; the port has no per-zone reason string yet, so it
        /// defaults to "missing a checkpoint", the stock zone reason).</summary>
        public string LastPenaltyReason = "";

        /// <summary>QC <c>race_lastpenalty</c>: the penalty entity last triggered (so a zone only fires once per pass).</summary>
        public Entity? LastPenalty;

        // ---- per-checkpoint split state (QC race_SendTime client feed: server/race.qc:485) ----

        /// <summary>QC <c>race_checkpoint</c> feed: the checkpoint index this racer most recently crossed
        /// (0/finish = 255 on the wire, see <see cref="HudCheckpoint"/>). -1 = none crossed yet this lap.</summary>
        public int LastCrossedCheckpoint = -1;

        /// <summary>QC the absolute sim time of the most recent checkpoint crossing (drives the HUD split fade,
        /// <c>race_checkpointtime</c>). 0 = none.</summary>
        public float LastCheckpointTime;

        /// <summary>QC <c>race_time</c>: the racer's split (seconds since lap start) at the last checkpoint.</summary>
        public float LastSplit;

        /// <summary>QC <c>race_timespeed</c>: the racer's planar speed (qu/s) when it crossed the last checkpoint.</summary>
        public float LastCheckpointSpeed;

        /// <summary>QC <c>e.race_checkpoint_record[cp]</c>: this racer's personal-best split (seconds) per checkpoint
        /// index (sparse; 0 = none). Used for the HUD "my previous best" anticipation line.</summary>
        public readonly Dictionary<int, float> PersonalCheckpointRecords = new();

        // ---- respawn-at-last-checkpoint (QC race_respawn_checkpoint / race_respawn_spotref, server/race.qc:807-809) ----

        /// <summary>QC <c>race_respawn_checkpoint</c>: the checkpoint index this racer must respawn at (0 = start
        /// line). Advanced as the racer crosses checkpoints in order; consulted by the spawn-point selection
        /// (QC trigger_race_checkpoint_spawn_evalfunc) so a death returns the racer to their last checkpoint.</summary>
        public int RespawnCheckpoint;

        /// <summary>QC <c>race_respawn_spotref</c>: the checkpoint world entity at which this racer respawns
        /// (server/race.qc:808 — "this is not a spot but a CP, but spawnpoint selection will deal with that"). The
        /// host forces the next spawn to this entity via <see cref="Entity.SpawnPointTarg"/> (the one-shot redirect
        /// SelectSpawnPoint short-circuits to), so a respawning racer is placed back at the checkpoint they reached.
        /// Null = respawn at a normal start spawn (the racer hasn't passed any checkpoint yet).</summary>
        public Entity? RespawnSpot;

        /// <summary>QC <c>race_started</c>: this racer has crossed the start line at least once (so the lap clock is
        /// running and the respawn falls back to the last checkpoint rather than the grid start).</summary>
        public bool Started;

        // ---- QC .race_movetime per-frame lap-clock accumulator (server/race.qc, advanced in rc PlayerPhysics) ----
        // QC times a lap by a frame-accumulated value (sum of the per-physics-frame dt) rather than wall-clock `time`,
        // so the recorded lap time is frame-rate-independent / deterministic and unaffected by a pause or frame jitter
        // (the divergence race.lap.timing_scoring flagged). It is split into an integer count and a [0,1) fraction
        // (QC race_movetime_count / race_movetime_frac) to keep float precision over a long session; race_movetime =
        // count + frac. The accumulator is zeroed when the racer crosses the start line (QC checkpoint_passed start
        // branch, race.qc:818) and read as the elapsed lap time at every crossing (race.qc:813, race_SendTime).

        /// <summary>QC <c>race_movetime_frac</c>: the [0,1) fractional part of the accumulated lap clock.</summary>
        public float MoveTimeFrac;

        /// <summary>QC <c>race_movetime_count</c>: the integer-seconds part of the accumulated lap clock.</summary>
        public float MoveTimeCount;

        /// <summary>QC <c>race_movetime</c>: the frame-accumulated lap clock (count + frac), in seconds. This is the
        /// authoritative deterministic lap time the <c>race_SendTime</c> path reads at each checkpoint crossing. It
        /// counts up every physics frame from the start-line crossing; the wall clock is only the fallback for a host
        /// that drives no PlayerPhysics frames (e.g. the unit harness touching checkpoints directly).</summary>
        public float RaceMoveTime;
    }

    // ---- per-map / per-run checkpoint records (QC race_checkpoint_records[cp] et al, server/race.qc) ----

    /// <summary>QC <c>race_checkpoint_records[cp]</c>: the best split (seconds) any racer has reached checkpoint
    /// <c>cp</c> in this match (sparse; 0 = none). Drives the HUD "previous best" / anticipation deltas.</summary>
    private readonly Dictionary<int, float> _checkpointRecords = new();

    /// <summary>QC <c>race_checkpoint_recordspeeds[cp]</c>: the planar speed (qu/s) of the record-holding crossing.</summary>
    private readonly Dictionary<int, float> _checkpointRecordSpeeds = new();

    /// <summary>QC <c>race_checkpoint_recordholders[cp]</c>: the net name of the racer who set the record split.</summary>
    private readonly Dictionary<int, string> _checkpointRecordHolders = new();

    /// <summary>The best recorded split (seconds) to checkpoint <paramref name="cp"/> this match, 0 if none.</summary>
    public float CheckpointRecord(int cp) => _checkpointRecords.TryGetValue(cp, out float v) ? v : 0f;

    /// <summary>The speed (qu/s) of the record-holding crossing at checkpoint <paramref name="cp"/>, 0 if none.</summary>
    public float CheckpointRecordSpeed(int cp) => _checkpointRecordSpeeds.TryGetValue(cp, out float v) ? v : 0f;

    /// <summary>The net name of the racer holding the split record at checkpoint <paramref name="cp"/> ("" if none).</summary>
    public string CheckpointRecordHolder(int cp) => _checkpointRecordHolders.TryGetValue(cp, out string? v) ? v : "";

    private readonly Dictionary<Player, RaceState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _physicsHandler;
    // Cached delegate for SpawnSystem.SpotEvalScore so Deactivate can compare by identity (a new closure each time would fail ReferenceEquals).
    private System.Func<Entity, Player, float>? _spotEvalScore;

    /// <summary>Optional sink for the host/controller to react to a death/finish.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a racer hits the lap limit or all have finished.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The first racer to complete the race (QC winner), else null.</summary>
    public Player? Winner { get; private set; }

    /// <summary>
    /// QC sudden-death overtime latch (sv_race.qc WinningCondition_Race returns WINNING_STARTSUDDENDEATHOVERTIME):
    /// set once the score limit is reached but not everyone has finished, so the match runs on until a racer
    /// completes (rather than ending on a tie). Cleared at <see cref="Activate"/>.
    /// </summary>
    public bool SuddenDeath { get; private set; }

    /// <summary>
    /// QC <c>race_completing</c> (server/race.qc): latched the moment the FIRST racer reaches the lap limit
    /// (<see cref="StartCompleting"/>). Once set, every dead/respawning racer is auto-marked completed+abandoned
    /// (<see cref="AbandonRaceCheck"/>) — that is how a real multi-racer match drains to n == c after the leader
    /// finishes, rather than waiting for every straggler to independently cross the line. Cleared at Activate.
    /// </summary>
    public bool RaceCompleting { get; private set; }

    /// <summary>QC <c>g_race_qualifying</c>: solo time-trial mode (rank by fastest lap, re-teleport after each lap).
    /// Note this can be the value <c>2</c> ("qualifying THEN race"), which <see cref="TransitionQualifyingToRace"/>
    /// collapses to 0 (plain race) once the qualifying session ends.</summary>
    public bool Qualifying => TryCvar(CvarQualifying, out float v) && v != 0f;

    /// <summary>The current map name (QC GetMapname) — the record-DB key segment.</summary>
    public string MapName => Api.Services is null ? "" : Api.Cvars.GetString(CvarMapName);

    /// <summary>This gametype's record-DB type segment (QC RACE_RECORD).</summary>
    public string RecordType => RaceRecords.RaceRecord;

    /// <summary>
    /// The host's finish-retract hook (QC the kill-delay re-teleport in race_PreparePlayer / Race_FinalCheckpoint):
    /// after a lap is closed (qualifying) or the race is finished (race mode) the racer is sent back to the start.
    /// The simulation resets the racer's lap state (<see cref="RetractPlayer"/>); the host implements the actual
    /// teleport-to-start + optional silent kill. Invoked from <see cref="Tick"/> when the kill delay elapses.
    /// </summary>
    public System.Action<Player>? OnFinishRetract;

    /// <summary>
    /// QC <c>race_setTime</c> consent gate (server/race.qc:415): before a new record is stored the player must
    /// have <c>cl_allow_uidtracking == 1</c> AND <c>cl_allow_uid2name == 1</c> (and not be an
    /// <c>^1Unregistered Player</c>). The host wires this to its per-client replicated-cvar store
    /// (Commands.GetClientCvar). Returns true when the racer consents to having their record stored + named.
    /// Null ⇒ no host gate (the harness / a host that doesn't track consent) ⇒ treated as consenting, so the
    /// uid-only fallback (a non-empty PersistentId) still files the record as before.
    /// </summary>
    public System.Func<Player, bool>? UidConsentProvider;

    /// <summary>The result of the most recent finish-time record attempt (for the host's INFO_RACE_* notification).</summary>
    public RaceRecordResult LastRecord { get; private set; }
    public Player? LastRecordPlayer { get; private set; }

    /// <summary>Absolute sim time of the most recent finish-time record attempt (QC the race_SendStatus event
    /// time; the HUD medal flash fades over this + 5s). 0 = none yet. Lets the client raise the medal once.</summary>
    public float LastRecordTime { get; private set; }

    // Pending finish-retracts: racer -> absolute sim time at which to send them back to start (kill delay).
    private readonly Dictionary<Player, float> _retractAt = new();

    public Race()
    {
        NetName = "rc";
        DisplayName = "Race";
        TeamGame = false;
    }

    /// <summary>The checkpoint entities on the map, indexed by their <see cref="Entity.GtCheckpointIndex"/> (QC g_race_targets).</summary>
    public readonly List<Entity> Checkpoints = new();

    /// <summary>The highest checkpoint index seen (QC race_highest_checkpoint); index 0 is the finish/start line.</summary>
    public int HighestCheckpoint { get; private set; }

    /// <summary>
    /// QC <c>race_timed_checkpoint</c> (server/race.qc:169): the checkpoint index whose crossing CLOSES a lap
    /// (files the time + advances the lap counter). Default <c>0</c> ⇒ the start line is also the finish (a closed
    /// loop). On a track where start != finish, the highest checkpoint carries <c>spawnflags &amp; 8</c>
    /// (<see cref="SpawnCheckpoint"/> <c>timed:true</c>) and becomes the timed checkpoint, so the lap clock still
    /// resets at CP 0 but the lap is only scored when the racer crosses that final timed CP
    /// (QC spawnfunc(trigger_race_checkpoint): <c>if(spawnflags &amp; 8) race_timed_checkpoint = race_checkpoint;</c>).
    /// </summary>
    public int TimedCheckpoint { get; private set; }

    public override void OnInit()
    {
        // QC: checkpoints are spawned from the map's trigger_race_checkpoint entities (see SpawnCheckpoint).
        // gametype_init flags (USEPOINTS) and the qualifying/race mode are engine/server-config concerns.
        Checkpoints.Clear();
        PenaltyZones.Clear();
        _punishWrongWay.Clear();
        _useTargetsBeforeScore.Clear();
        HighestCheckpoint = 0;
        TimedCheckpoint = 0; // QC race_timed_checkpoint default 0 (closed-loop track: start line == finish)
        // QC race_ClearRecords: a fresh map has no checkpoint split records yet.
        _checkpointRecords.Clear();
        _checkpointRecordSpeeds.Clear();
        _checkpointRecordHolders.Clear();
    }

    /// <summary>
    /// QC trigger_race_checkpoint spawnfunc: register a checkpoint world entity at <paramref name="origin"/>
    /// with ordinal <paramref name="checkpointIndex"/> (0 = start/finish line). Touch dispatches the ordered
    /// crossing to <see cref="CrossCheckpoint"/>.
    /// </summary>
    public Entity? SpawnCheckpoint(Vector3 origin, int checkpointIndex, Vector3 mins = default, Vector3 maxs = default,
        bool punishWrongWay = false, bool timed = false, string target = "", string message = "",
        bool useTargetsBeforeScore = false)
    {
        if (maxs == default) { mins = new Vector3(-48f, -48f, -48f); maxs = new Vector3(48f, 48f, 48f); }
        Entity? e = GametypeEntities.SpawnObjective("trigger_race_checkpoint", origin, Teams.None, mins, maxs,
            touch: CheckpointTouch);
        if (e is not null)
        {
            e.Flags = EntFlags.None; // a checkpoint is a pure trigger volume, not an item
            e.GtCheckpointIndex = checkpointIndex;
            // QC trigger_race_checkpoint spawnflag 4: a racer who crosses this checkpoint out of order is killed
            // (DEATH_HURTTRIGGER 10000), to forbid backwards/shortcut traversal.
            if (punishWrongWay) _punishWrongWay.Add(e);
            // QC trigger_race_checkpoint .target/.message + spawnflag 2: a valid crossing fires the checkpoint's
            // targets (SUB_UseTargets) and the message is blanked during that fire (no centerprint). The map
            // target/message ride on the checkpoint edict (SpawnObjective doesn't copy them); the spawnflag-2 bit
            // selects fire-before vs fire-after the lap-time registration (CheckpointTouch).
            if (!string.IsNullOrEmpty(target)) e.Target = target;
            if (!string.IsNullOrEmpty(message)) e.Message = message;
            if (useTargetsBeforeScore) _useTargetsBeforeScore.Add(e);
            Checkpoints.Add(e);
            // QC spawnfunc(trigger_race_checkpoint) (server/race.qc:1111): the HIGHEST checkpoint determines the
            // timed (finish) line — if it carries spawnflag 8 the finish is that CP, otherwise the finish is CP 0
            // (a closed-loop track). race_timed_checkpoint follows the highest CP, so a later, lower-index CP does
            // not steal the finish from an earlier higher one.
            if (checkpointIndex > HighestCheckpoint)
            {
                HighestCheckpoint = checkpointIndex;
                TimedCheckpoint = timed ? checkpointIndex : 0;
            }
            // The finish line is the timed checkpoint (QC race_timed_checkpoint); on a closed loop that is CP 0.
            e.GtIsFinishLine = checkpointIndex == TimedCheckpoint;
        }
        return e;
    }

    /// <summary>Checkpoints with QC spawnflag 4 (kill on a wrong-order crossing). See <see cref="CheckpointTouch"/>.</summary>
    private readonly HashSet<Entity> _punishWrongWay = new();

    /// <summary>
    /// QC trigger_race_checkpoint spawnflag 2 (server/race.qc:checkpoint_passed): when SET, the checkpoint fires
    /// its targets (SUB_UseTargets) BEFORE the lap time is registered; when clear, AFTER. The checkpoints in this
    /// set fire-before; everything else fires-after. Tracked here (like <see cref="_punishWrongWay"/>) so the
    /// checkpoint world entity needs no extra field. See <see cref="CheckpointTouch"/>.
    /// </summary>
    private readonly HashSet<Entity> _useTargetsBeforeScore = new();

    /// <summary>The penalty-zone trigger entities on the map (QC trigger_race_penalty).</summary>
    public readonly List<Entity> PenaltyZones = new();

    /// <summary>
    /// QC trigger_race_penalty spawnfunc (server/race.qc:1322): a trigger volume that imposes
    /// <paramref name="penaltySeconds"/> (QC .race_penalty, default 5) on a racer who enters it. Touch
    /// dispatches to <see cref="PenaltyTouch"/>. Returns the world entity (or null headless).
    /// </summary>
    public Entity? SpawnPenaltyZone(Vector3 origin, float penaltySeconds = 5f, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-48f, -48f, -48f); maxs = new Vector3(48f, 48f, 48f); }
        Entity? e = GametypeEntities.SpawnObjective("trigger_race_penalty", origin, Teams.None, mins, maxs,
            touch: PenaltyTouchEntity);
        if (e is not null)
        {
            e.Flags = EntFlags.None;                       // a pure trigger volume
            e.GtPointAmt = penaltySeconds > 0f ? penaltySeconds : 5f; // QC this.race_penalty
            PenaltyZones.Add(e);
        }
        return e;
    }

    /// <summary>Entity touch trampoline for a penalty zone: a live racer entering it is penalized.</summary>
    private void PenaltyTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            PenaltyTouch(self, p);
    }

    /// <summary>
    /// QC checkpoint_passed: a racer touched a checkpoint. The crossing only counts if it is the racer's
    /// expected next checkpoint (QC player.race_checkpoint == this.race_checkpoint, with -1 → expect 0);
    /// then advance to the next checkpoint (wrapping the finish line) and update lap timing.
    /// </summary>
    public void CheckpointTouch(Entity self, Entity other)
    {
        if (other is not Player p || p.IsDead)
            return;
        int cpIndex = self.GtCheckpointIndex;
        RaceState st = GetState(p);

        // Expected next checkpoint: -1 means "haven't started, expect the start line (0)".
        int expected = st.NextCheckpoint < 0 ? 0 : st.NextCheckpoint;
        if (cpIndex != expected)
        {
            // QC checkpoint_passed: a wrong-order crossing of a spawnflag-4 checkpoint kills the racer
            // (Damage 10000, DEATH_HURTTRIGGER); otherwise it is simply ignored.
            if (_punishWrongWay.Contains(self) && Api.Services is not null)
                Combat.Damage(p, self, self, 10000f, DeathTypes.Void, p.Origin, Vector3.Zero);
            return;
        }

        // QC checkpoint_passed (server/race.qc:807-811): a crossing in order updates the racer's respawn anchor —
        // the checkpoint they will be returned to on death. race_respawn_spotref is only re-pointed when the racer
        // reaches a NEW checkpoint (or hasn't started yet), so a re-touch of the same CP keeps the prior spot.
        if (st.RespawnCheckpoint != cpIndex || !st.Started)
            st.RespawnSpot = self;     // QC player.race_respawn_spotref = this
        st.RespawnCheckpoint = cpIndex; // QC player.race_respawn_checkpoint = this.race_checkpoint
        st.Started = true;              // QC player.race_started = 1

        // QC checkpoint_passed (server/race.qc:729-731): "remove unauthorized equipment" — a racer crossing a
        // checkpoint has all their portals torn down (Portal_ClearAll) and porto re-firing is forbidden for the
        // next 2 server frames (porto_forbidden = 2, decremented each frame by the porto_ticker mutator) so they
        // can't portal across the checkpoint reset.
        Porto.PortalClearAll?.Invoke(p);
        p.WeaponState(new WeaponSlot(0)).PortoForbidden = 2;

        // QC race_SendTime (server/race.qc:493): a lap is CLOSED (scored) at race_timed_checkpoint, which is the
        // start line (0) on a closed loop or the highest spawnflag-8 CP on a start!=finish track. Compute from the
        // live TimedCheckpoint (spawn-order independent) rather than the cached per-entity flag.
        bool isFinish = cpIndex == TimedCheckpoint;

        // QC checkpoint_passed (server/race.qc): a valid crossing fires the checkpoint's targets (SUB_UseTargets,
        // activator = the racer). spawnflag 2 selects whether they fire BEFORE the lap time is registered or AFTER
        // (this.spawnflags & 2). The checkpoint's .message is blanked for the fire so the targets run silently (no
        // centerprint) — QC: oldmsg = this.message; this.message = ""; SUB_UseTargets(); this.message = oldmsg.
        bool fireBefore = _useTargetsBeforeScore.Contains(self);
        if (fireBefore)
            FireCheckpointTargets(self, p);
        CrossCheckpoint(p, cpIndex, isFinish);
        if (!fireBefore)
            FireCheckpointTargets(self, p);
    }

    /// <summary>
    /// QC checkpoint_passed's SUB_UseTargets fire (server/race.qc): fire the checkpoint's <c>.target</c> chain with
    /// the racer as activator, with the centerprint <c>.message</c> blanked for the duration (so a checkpoint with
    /// a target — e.g. a defrag <c>target_init</c>/relay or a map effect — triggers without spamming a banner).
    /// No-op when the checkpoint names no target (the common closed-loop case). See <see cref="CheckpointTouch"/>.
    /// </summary>
    private static void FireCheckpointTargets(Entity checkpoint, Player racer)
    {
        if (Api.Services is null || string.IsNullOrEmpty(checkpoint.Target))
            return;
        string oldMsg = checkpoint.Message;
        checkpoint.Message = "";                       // QC: this.message = "" (blank the centerprint)
        MapMover.UseTargets(checkpoint, racer, null);  // QC SUB_UseTargets(this, other=activator, NULL)
        checkpoint.Message = oldMsg;                   // QC: this.message = oldmsg (restore)
    }

    /// <summary>
    /// The lap limit in force. QC <c>g_race_laps_limit</c> (sv_race.qc:408, rc_SetLimits): the default is
    /// <c>-1</c> = "use the mapinfo laplimit" (here the gametype default <see cref="DefaultLapLimit"/>=7, as the
    /// port has no per-map laplimit table); <c>0</c> = unlimited; <c>&gt;0</c> = that many laps. The value is fed
    /// to GameRules_limit_score (the fraglimit), so an explicit fraglimit is honored only when the race cvar is
    /// unset. 0 means no lap limit (the finish check is guarded by <c>limit &gt; 0</c>).
    /// </summary>
    public int LapLimit
    {
        get
        {
            // QC race.qh m_legacydefaults "20 5 7 15 0": the mapinfo laplimit default is 7 (FFA) / 15 (team). The
            // port has no per-map mapinfo laplimit table, so a map that overrides laplimit in its .mapinfo isn't
            // honored; the gametype legacy default IS reproduced (the value any non-overriding map gets in Base).
            int mapinfoDefault = RaceTeams >= 2 ? DefaultTeamLapLimit : DefaultLapLimit;
            if (TryCvar(CvarLapLimit, out float ll))
            {
                int l = (int)ll;
                // QC rc_SetLimits: g_race_laps_limit default -1 means "consult the mapinfo laplimit" (here the legacy
                // gametype default, team-aware). 0 stays unlimited; >0 is the literal lap limit.
                if (l < 0) return mapinfoDefault;
                return l;
            }
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return mapinfoDefault;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Winner = null;
        SuddenDeath = false;
        RaceCompleting = false;
        // QC fragsleft_last reset: re-arm the "N laps left" announcer (rc's Scores_CountFragsRemaining hook, !qualifying).
        Scoring.GameScores.ResetFragsRemaining();
        // QC race_ClearRecords (server/race.qc): reset the round-best speed award; the persisted all-time best
        // (speedaward_alltimebest, kept in the DB) is re-loaded lazily so it survives map resets.
        _speedawardSpeed = 0f;
        _speedawardHolder = "";
        _speedawardUid = "";
        _speedawardLoaded = false; // force a fresh DB load for the new map/session
        // QC race_PreparePlayer: a fresh activation clears every racer's respawn anchor (re-derived as they race).
        foreach (RaceState rs in _states.Values)
        {
            rs.RespawnCheckpoint = 0;
            rs.RespawnSpot = null;
            rs.Started = false;
        }
        // QC rc_SetLimits (sv_race.qc:391): radar_showenemies = true — race reveals every racer on the radar (and,
        // server-side, sends every player's private ent_cs fields to everyone via the ServerNet privacy-mask gate),
        // since there are no enemies to hide in a time-trial. Mirror the global so both the client radar and the
        // server-side ent_cs strip read the faithful value.
        if (Api.Services is not null)
            Api.Cvars.Set("radar_showenemies", "1");

        // QC MUTATOR_HOOKFUNCTION(rc, FixClientCvars) (sv_race.qc): the server stuffs `cl_cmd settemp
        // cl_movecliptokeyboard 2` to every client so their LOCAL input-clip matches the server's force-keyboard
        // octant snap (OnPlayerPhysics slice 3) — without it a remote client would predict an un-snapped analog
        // move and rubber-band. In this in-process listen server the client input reads the cvar directly, so the
        // faithful analogue of the per-client stuffcmd is to set cl_movecliptokeyboard=2 globally at activation
        // (the same listen-server stuffcmd analogue BugrigsMutator.OnClientConnect uses for chase_active). The
        // server-side snap is already authoritative; this aligns the client's own clip so prediction agrees.
        if (Api.Services is not null)
            Api.Cvars.Set("cl_movecliptokeyboard", "2");

        // QC rc_SetLimits: g_race_teams 2..4 makes race a team game (members add up laps to ST_RACE_LAPS).
        TeamGame = RaceTeams >= 2;
        if (TeamGame)
            Scoring.GameScores.ResetTeams();
        DeclareScoreRules();
        if (TeamGame)
            Scoring.GameScores.SeedTeams(RaceTeams);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
        // QC MUTATOR_HOOKFUNCTION(rc, PlayerPhysics) (sv_race.qc:128): advance the deterministic .race_movetime lap
        // clock + force keyboard-movement quantization for record fairness + apply the penalty freeze. Registered on
        // the global PlayerPhysics chain (the same seam CTS uses) so it runs live every physics frame.
        _physicsHandler = OnPlayerPhysics;
        MutatorHooks.PlayerPhysics.Add(_physicsHandler);

        // QC race_spawns (server/race.qh:16): the global counter is incremented once per registered
        // trigger_race_checkpoint (race.qc:1241 `++race_spawns`). Spawn_Score gates on it: a spawnpoint without a
        // target is invalid on a race map (spawnpoints.qc:246 `if(race_spawns) if(!spot.target) return '-1 0 0'`).
        // In the port Checkpoints is the equivalent list; if any checkpoints were registered before Activate was
        // called (the normal map-load order), enable the race-spawns target requirement gate.
        SpawnSystem.RaceSpawnsActive = Checkpoints.Count > 0;

        // QC trigger_race_checkpoint_spawn_evalfunc (server/race.qc:1055-1083) score-rewriting branch: when NOT
        // qualifying, add SPAWN_PRIO_RACE_PREVIOUS_SPAWN=50 to a spot when it equals the player's respawn spotref
        // (so the spot the racer last crossed is slightly preferred in the weighted pick). This is a secondary
        // fallback: ApplyRespawnSpot already forces the exact checkpoint via SpawnPointTarg (the one-shot redirect
        // SelectSpawnPoint short-circuits to), so the +50 path only fires on the rare edge case where the forced
        // redirect was not applied (e.g. the racer's RespawnSpot is null / at checkpoint 0, or qualifying mode).
        _spotEvalScore ??= RaceSpotEvalScore;    // cache once so Deactivate can compare by identity
        SpawnSystem.SpotEvalScore = _spotEvalScore;
    }

    /// <summary>
    /// QC <c>trigger_race_checkpoint_spawn_evalfunc</c> score-rewriting branch (server/race.qc:1069-1071):
    /// <c>if(this == player.race_respawn_spotref || spot == player.race_respawn_spotref) current.x += 50</c>.
    /// This is called from <see cref="SpawnSystem.ScoreSpot"/> via <see cref="SpawnSystem.SpotEvalScore"/> when
    /// race checkpoints exist on the map. Returns the prio delta to add (50 when the spot matches the player's
    /// last-checkpoint respawn anchor, 0 otherwise). Only relevant on a non-qualifying race path when the spot
    /// is the racer's <see cref="RaceState.RespawnSpot"/> entity (the <c>race_respawn_spotref</c>).
    /// </summary>
    private float RaceSpotEvalScore(Entity spot, Player player)
    {
        if (Qualifying) return 0f;  // qualifying evalfunc uses place-only filtering, not a prio bias
        if (!_states.TryGetValue(player, out RaceState? st)) return 0f;
        Entity? respawnSpot = st.RespawnSpot;
        if (respawnSpot is null || respawnSpot.IsFreed) return 0f;
        // QC: if(this == player.race_respawn_spotref || spot == player.race_respawn_spotref)
        // 'this' = the checkpoint entity; 'spot' = the spawn spot entity. In the port the respawn spot IS the
        // checkpoint entity itself (set by CheckpointTouch: st.RespawnSpot = self where self is the checkpoint).
        // So this half matches when the spawn spot IS the checkpoint (rare: only if a map places an info_player_*
        // ON the checkpoint trigger). The 'this=checkpoint' half corresponds to the spawn spot's target chain —
        // when the spot targets a checkpoint that is the RespawnSpot, we also add the bias.
        if (ReferenceEquals(spot, respawnSpot))
            return SpawnPrioRacePreviousSpawn;
        // Check whether the spot's target resolves to the respawn checkpoint entity (the 'this' half in QC).
        string spotTarget = spot.Target;
        if (!string.IsNullOrEmpty(spotTarget) && !string.IsNullOrEmpty(respawnSpot.TargetName)
            && spotTarget == respawnSpot.TargetName)
            return SpawnPrioRacePreviousSpawn;
        return 0f;
    }

    /// <summary>QC <c>SPAWN_PRIO_RACE_PREVIOUS_SPAWN</c> (server/spawnpoints.qh:12): priority boost for a
    /// spot that matches the player's last-checkpoint respawn anchor in a non-qualifying race.</summary>
    public const int SpawnPrioRacePreviousSpawn = 50;

    /// <summary>
    /// QC the Race checkpoint waypoint sprites (spawnfunc(trigger_race_checkpoint) → WaypointSprite_SpawnFixed):
    /// one fixed marker per checkpoint at its world origin, shown to everyone (showto = NULL), with the def chosen
    /// from the checkpoint's role — WP_RaceStartFinish when CP 0 is also the timed finish (a closed loop),
    /// WP_RaceStart for CP 0 on a start≠finish track, WP_RaceFinish for the timed finish CP, and WP_RaceCheckpoint
    /// for every intermediate checkpoint. Rebuilt each tick like CTF's flag sprites; reached via
    /// ServerNet.SendWaypoints (GameType.CollectWaypoints). Race has no teams to hide markers from, so all are
    /// SPRITERULE_DEFAULT with team 0 (everyone-visible), matching the QC SpawnFixed showto=NULL.
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        foreach (Entity cp in Checkpoints)
        {
            if (cp.IsFreed)
                continue;
            int idx = cp.GtCheckpointIndex;
            bool isFinish = idx == TimedCheckpoint; // QC race_timed_checkpoint — the lap-closing CP
            // QC trigger_race_checkpoint: CP 0 that is ALSO the finish (closed loop) is the combined Start/Finish
            // marker; CP 0 on a start≠finish track is Start; the timed finish CP is Finish; else an intermediate.
            string sprite =
                idx == 0 && isFinish ? "RaceStartFinish"
              : idx == 0            ? "RaceStart"
              : isFinish            ? "RaceFinish"
                                    : "RaceCheckpoint";
            into.Add(new Waypoints.WaypointSprite
            {
                SpriteName = sprite,
                FixedOrigin = cp.Origin,
                Team = 0,                          // QC SpawnFixed showto=NULL → shown to everyone
                Color = new Vector3(1f, 0.5f, 0f), // WP_Race* default tint (orange)
                RadarIcon = 1,                     // RADARICON_RACE
                Health = -1f,
            });
        }
    }

    /// <summary>
    /// QC <c>race_ScoreRules</c> (sv_race.qc): GameRules_score_enabled(false) — race has no SP_SCORE — then declare
    /// the lap/time columns and pin the sort keys. In a non-team race: qualifying ranks by SP_RACE_FASTEST
    /// (PRIMARY, lower-is-better TIME); a plain race ranks by SP_RACE_LAPS (PRIMARY) then SP_RACE_TIME (SECONDARY,
    /// lower-is-better TIME), with SP_RACE_FASTEST shown as a stat. Team race ranks teams by ST_RACE_LAPS.
    /// </summary>
    private void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: TeamGame, scoreEnabled: false); // QC GameRules_score_enabled(false): no SP_SCORE
        bool qualifying = Qualifying;
        if (!TeamGame && qualifying)
        {
            // qualifying solo time-trial: fastest single lap, ascending.
            GS.DeclareColumn("RACE_FASTEST", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "fastest");
            GS.SetSortKeys(GS.Field("RACE_FASTEST")!);
        }
        else
        {
            // QC: field(SP_RACE_LAPS, "laps", PRIMARY); field(SP_RACE_TIME, "time", SECONDARY|LOWER|TIME); + the
            // fastest stat. In a TEAM race the team primary is ST_RACE_LAPS (team slot 1, "laps").
            if (TeamGame)
            {
                GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None);
                GS.SetTeamLabel(GS.TeamSlotSecondary, "laps", Scoring.ScoreFlags.SortPrioPrimary); // ST_RACE_LAPS
            }
            GS.DeclareColumn("RACE_LAPS", Scoring.ScoreFlags.None, "laps");
            GS.DeclareColumn("RACE_TIME", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "time");
            GS.DeclareColumn("RACE_FASTEST", Scoring.ScoreFlags.LowerIsBetter | Scoring.ScoreFlags.Time, "fastest");
            GS.SetSortKeys(GS.Field("RACE_LAPS")!, GS.Field("RACE_TIME"));
        }
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Race player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (_physicsHandler is not null) { MutatorHooks.PlayerPhysics.Remove(_physicsHandler); _physicsHandler = null; }
        // QC: race_spawns is a global counter that persists until the next map load; clear the port's equivalents
        // on gametype deactivation so a mode-switch doesn't leave the race spawn gates active in DM/CTF.
        SpawnSystem.RaceSpawnsActive = false;
        if (_spotEvalScore is not null && ReferenceEquals(SpawnSystem.SpotEvalScore, _spotEvalScore))
            SpawnSystem.SpotEvalScore = null;
    }

    public RaceState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out RaceState? st))
        {
            st = new RaceState();
            _states[p] = st;
        }
        return st;
    }

    public void AddPlayer(Player p) => GetState(p);
    public void RemovePlayer(Player p) => _states.Remove(p);

    /// <summary>
    /// Record a racer crossing a checkpoint (QC race_CheckpointTouch). This is the core timing rule: crossing
    /// the timed finish checkpoint (<paramref name="isFinishLine"/> ⇒ <see cref="TimedCheckpoint"/>) closes the
    /// current lap — updating the fastest lap and the lap counter — and either starts the next lap or, at the lap
    /// limit, finishes the race. This is invoked by <see cref="CheckpointTouch"/> when a checkpoint entity is
    /// crossed in order; the host may also call it directly. Advances <see cref="RaceState.NextCheckpoint"/>
    /// (QC race_NextCheckpoint, wrapping back to 0 after the highest checkpoint).
    /// </summary>
    public void CrossCheckpoint(Player p, int checkpointIndex, bool isFinishLine)
    {
        if (MatchEnded)
            return;

        RaceState st = GetState(p);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC race_SendTime (server/race.qc:485): every crossing — intermediate OR finish — files a split for the
        // HUD race timer / checkpoints panel. The split is the elapsed lap time at this checkpoint (race_time);
        // we stamp it (+ the racer's planar speed) and fold it into the per-checkpoint record store so the client
        // can render the "Checkpoint N (+/-delta vs record)" line and the anticipation delta. A valid split needs
        // a started lap (LapStartTime > 0); the very first start-line crossing only opens the clock (no split).
        RecordCheckpointSplit(p, st, checkpointIndex, now);

        // QC race_NextCheckpoint (server/race.qc:174): every valid crossing advances the next-required checkpoint,
        // wrapping back to 0 after the highest CP. On a closed loop (TimedCheckpoint==0) the finish is CP 0 and
        // this collapses to "0 → 1 → … → highest → 0"; on a start!=finish track the wrap happens at the highest
        // (timed) CP. Done here uniformly so the start-line reset (below) and the finish-line scoring agree.
        st.NextCheckpoint = checkpointIndex >= HighestCheckpoint ? 0 : checkpointIndex + 1;

        if (isFinishLine)
        {
            // Close the lap: a lap time exists only once we've crossed the line at least once before.
            if (st.LapStartTime > 0f)
            {
                // QC race_SendTime (server/race.qc:813): the lap time IS the frame-accumulated .race_movetime (zeroed
                // at the start line, advanced each PlayerPhysics frame), NOT a wall-clock delta — so it is frame-rate-
                // independent and unaffected by a pause/jitter. Use it when the physics accumulator has run (the live
                // path); fall back to the wall-clock delta only when no physics frames advanced it (e.g. a unit harness
                // touching checkpoints directly without driving OnPlayerPhysics), so the end-to-end tests still hold.
                float lapTime = st.RaceMoveTime > 0f ? st.RaceMoveTime : now - st.LapStartTime;
                if (lapTime > 0f)
                {
                    if (st.FastestLap <= 0f || lapTime < st.FastestLap)
                        st.FastestLap = lapTime; // QC SP_RACE_FASTEST (lower is better)
                    st.LapsCompleted += 1;       // QC SP_RACE_LAPS
                    st.TotalTime += lapTime;     // cumulative race time (QC SP_RACE_TIME ~ time - game_starttime)

                    // QC Race_FinalCheckpoint scoring (server/race.qc): SP_RACE_FASTEST tracks the best (lowest)
                    // lap; in a plain race SP_RACE_TIME is the cumulative time and SP_RACE_LAPS counts laps. Times
                    // are TIME_ENCODE'd to hundredths (SFL_TIME). Qualifying ranks by fastest only.
                    Scoring.ScoreField? fastest = Scoring.GameScores.Field("RACE_FASTEST");
                    if (fastest is not null)
                        Scoring.GameScores.SetBestTime(p, fastest, Scoring.GameScores.TimeEncode(lapTime));
                    if (!Qualifying)
                    {
                        AddCol(p, "RACE_LAPS", 1);
                        // QC g_race_teams (sv_race.qc:399): in a team race the members' laps add up into
                        // ST_RACE_LAPS (team slot 1), the team primary.
                        if (TeamGame && (int)p.Team != Teams.None)
                            Scoring.GameScores.AddToTeam((int)p.Team, Scoring.GameScores.TeamSlotSecondary, 1);
                        Scoring.ScoreField? rtime = Scoring.GameScores.Field("RACE_TIME");
                        if (rtime is not null)
                        {
                            // set RACE_TIME to the cumulative encoded time (delta from current value).
                            int target = Scoring.GameScores.TimeEncode(st.TotalTime);
                            int cur = Scoring.GameScores.Get(p, rtime);
                            if (target != cur) Scoring.GameScores.AddToPlayer(p, rtime, target - cur);
                        }
                    }

                    // QC race_setTime: file the lap time into the persistent per-map ranking (top-99 by UID).
                    RecordFinishTime(p, lapTime);
                }
            }

            int limit = LapLimit;
            if (limit > 0 && st.LapsCompleted >= limit)
                FinishRacer(p, st);
            else if (Qualifying)
                // QC qualifying: solo time-trial — after each lap the racer is sent back to start to run again.
                ScheduleRetract(p, now);
        }

        // QC checkpoint_passed (server/race.qc:815-818): the lap clock is (re)started at the START LINE (CP 0), NOT at
        // the timed finish CP. On a closed loop these coincide; on a start!=finish track the racer crosses the
        // finish (timed CP, scored above) and only re-arms the lap clock when they next cross CP 0. The very first
        // start-line crossing opens the clock with no prior lap to score. QC race.qc:818 also zeroes the deterministic
        // .race_movetime accumulator here (race_movetime = race_movetime_frac = race_movetime_count = 0) so the next
        // lap times from zero.
        if (checkpointIndex == 0)
        {
            st.LapStartTime = now;
            st.MoveTimeFrac = 0f;
            st.MoveTimeCount = 0f;
            st.RaceMoveTime = 0f;
        }
    }

    /// <summary>
    /// QC <c>race_SendTime</c> (server/race.qc:485) checkpoint-split half: stamp the racer's split (lap-elapsed
    /// time) + planar speed at <paramref name="checkpointIndex"/>, and fold it into both the per-match record
    /// (<see cref="_checkpointRecords"/>, QC <c>race_checkpoint_records[cp]</c>) and this racer's personal-best
    /// (<see cref="RaceState.PersonalCheckpointRecords"/>, QC <c>e.race_checkpoint_record[cp]</c>). Lower is
    /// better. The HUD race-timer / checkpoints panels read these via <see cref="GetState"/> + the record getters.
    /// A split is only meaningful once the lap clock has started (the first start-line crossing just opens it).
    /// </summary>
    private void RecordCheckpointSplit(Player p, RaceState st, int checkpointIndex, float now)
    {
        // No started lap yet ⇒ no split (the opening start-line crossing only stamps LapStartTime below).
        if (st.LapStartTime <= 0f)
            return;

        // QC race_time: lap-elapsed at this checkpoint = the deterministic .race_movetime (frame accumulator), with a
        // wall-clock fallback for hosts driving no physics frames (the unit harness), matching CrossCheckpoint above.
        float split = st.RaceMoveTime > 0f ? st.RaceMoveTime : now - st.LapStartTime;
        if (Qualifying)
            split += st.PenaltyAccumulator;         // QC race_SendTime: qualifying adds the penalty accumulator
        if (split <= 0f)
            return;

        // QC vlen(vec2(e.velocity)): the racer's PLANAR speed when crossing (qu/s).
        var v = p.Velocity;
        float speed = System.MathF.Sqrt(v.X * v.X + v.Y * v.Y);

        st.LastCrossedCheckpoint = checkpointIndex;
        st.LastCheckpointTime = now;
        st.LastSplit = split;
        st.LastCheckpointSpeed = speed;

        // QC: per-checkpoint MATCH record (race_checkpoint_records[cp]) — keep the lowest split + its speed/holder.
        float rec = CheckpointRecord(checkpointIndex);
        if (rec <= 0f || split < rec)
        {
            _checkpointRecords[checkpointIndex] = split;
            _checkpointRecordSpeeds[checkpointIndex] = speed;
            _checkpointRecordHolders[checkpointIndex] = p.NetName;
        }

        // QC: per-racer PERSONAL best (e.race_checkpoint_record[cp]).
        float prec = st.PersonalCheckpointRecords.TryGetValue(checkpointIndex, out float pv) ? pv : 0f;
        if (prec <= 0f || split < prec)
            st.PersonalCheckpointRecords[checkpointIndex] = split;
    }

    /// <summary>
    /// QC race_SendTime's lap-limit branch (server/race.qc:506-519): the racer just reached the lap limit. The
    /// FIRST racer to do so latches the global completing state (<see cref="StartCompleting"/>, which abandons
    /// every already-dead racer); thereafter every racer who crosses (or is abandoned) is marked completed and
    /// announced with INFO_RACE_FINISHED. We then re-check the win condition.
    /// </summary>
    private void FinishRacer(Player p, RaceState st)
    {
        if (st.Completed)
            return;
        Winner ??= p;        // first to finish is the race winner (qualifying ranks by FastestLap instead)

        // QC: if(l >= autocvar_fraglimit) race_StartCompleting();  — only the first finisher trips the latch.
        if (!RaceCompleting)
            StartCompleting();

        // QC: if(race_completing) { race_completed = 1; ...; INFO_RACE_FINISHED }.
        if (RaceCompleting)
        {
            st.Completed = true; // QC race_completed
            if (Api.Services is not null)
                NotificationSystem.Info("RACE_FINISHED", p.NetName); // QC INFO_RACE_FINISHED
        }

        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        ScheduleRetract(p, now); // QC: a finished racer is retracted to start (kept out of the running pack)
        CheckWinningCondition();
    }

    /// <summary>
    /// QC <c>race_StartCompleting</c> (server/race.qc:1214): the first racer reached the lap limit — latch the
    /// global completing state and immediately abandon every racer who is currently dead (<see cref="AbandonRaceCheck"/>),
    /// so a multi-racer match converges to n == c instead of waiting on stragglers.
    /// </summary>
    public void StartCompleting()
    {
        RaceCompleting = true; // QC race_completing = 1
        // QC FOREACH_CLIENT(IS_PLAYER(it) && IS_DEAD(it), { race_AbandonRaceCheck(it); });
        foreach (Player p in new List<Player>(_states.Keys))
            if (p.IsDead)
                AbandonRaceCheck(p);
    }

    /// <summary>
    /// QC <c>race_AbandonRaceCheck</c> (server/race.qc:1203): once the race is completing, a racer who is dead /
    /// has just respawned (and hasn't finished) is auto-marked completed+abandoned and announced with
    /// INFO_RACE_ABANDONED. Called from <see cref="StartCompleting"/> and on death (<see cref="OnDeath"/>).
    /// </summary>
    public void AbandonRaceCheck(Player p)
    {
        if (!RaceCompleting)
            return;
        RaceState st = GetState(p);
        if (st.Completed)
            return;
        st.Completed = true; // QC race_completed = 1 (MAKE_INDEPENDENT_PLAYER side-effect not modelled)
        if (Api.Services is not null)
            NotificationSystem.Info("RACE_ABANDONED", p.NetName); // QC INFO_RACE_ABANDONED
        CheckWinningCondition();
    }

    /// <summary>
    /// QC <c>race_setTime</c>: file a finished lap/run time into the persistent per-map ranking, stash the
    /// classified result (<see cref="LastRecord"/>), and broadcast the matching INFO_RACE_* notification
    /// (server/race.qc race_setTime: NEW_IMPROVED / NEW_SET / NEW_BROKEN / FAIL_RANKED / FAIL_UNRANKED /
    /// NEW_MISSING_UID). The notification time tokens are TIME_ENCODE'd (the race_time HUD token decodes them).
    /// </summary>
    private void RecordFinishTime(Player p, float time)
    {
        string uid = p.PersistentId;
        LastRecordPlayer = p;
        LastRecordTime = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC race_setTime (server/race.qc:407-421): once the time is confirmed to be a NEW record (would rank,
        // not a fail), there are two reasons it can still NOT be stored — and each fires its own notification
        // BEFORE the table is touched: (1) the player has no UID at all (INFO_RACE_NEW_MISSING_UID); (2) the
        // player has a UID but withheld consent — cl_allow_uidtracking != 1 || cl_allow_uid2name != 1 (or is an
        // "^1Unregistered Player") — (INFO_RACE_NEW_MISSING_NAME). A non-ranking time still just fails through
        // SetTime below, so the consent gate only matters for a would-be record.
        if (UidConsentProvider is not null && !string.IsNullOrEmpty(uid)
            && RaceRecords.WouldBeNewRecord(MapName, RecordType, time, uid)
            && !UidConsentProvider(p))
        {
            // Record-worthy but consent withheld: announce the loss and DON'T store (QC NEW_MISSING_NAME).
            LastRecord = new RaceRecordResult(RaceRecordKind.Fail, 0, 0, 0f, "");
            if (Api.Services is not null)
                NotificationSystem.Info("RACE_NEW_MISSING_NAME", p.NetName, Scoring.GameScores.TimeEncode(time));
            return;
        }

        RaceRecordResult r = RaceRecords.SetTime(MapName, RecordType, time, uid, p.NetName);
        LastRecord = r;
        SendRecordNotification(p, time, uid, r);
    }

    /// <summary>
    /// QC race_setTime's <c>showmessage</c> branch: fire the right INFO_RACE_* line for the classified result.
    /// Encoded-time tokens (<c>t</c>, <c>oldrec</c>) mirror QC's TIME_ENCODE'd notification floats.
    /// </summary>
    private static void SendRecordNotification(Player p, float time, string uid, RaceRecordResult r)
    {
        if (Api.Services is null)
            return;
        string name = p.NetName;
        int t = Scoring.GameScores.TimeEncode(time);

        if (r.Kind == RaceRecordKind.Fail)
        {
            // QC: anonymous run (uid == "") can't be ranked → NEW_MISSING_UID.
            if (string.IsNullOrEmpty(uid))
            {
                NotificationSystem.Info("RACE_NEW_MISSING_UID", name, t);
                return;
            }
            int oldrec = Scoring.GameScores.TimeEncode(r.OldRecordTime);
            // QC: a previously-ranked player who didn't improve → FAIL_RANKED (their own old rank/time);
            // an unranked time (worse than the worst ranked) → FAIL_UNRANKED (vs the last rank).
            if (r.OldPos != 0 && (r.NewPos == 0 || r.OldPos < r.NewPos))
                NotificationSystem.Info("RACE_FAIL_RANKED", name, r.OldPos, t, oldrec);
            else
                NotificationSystem.Info("RACE_FAIL_UNRANKED", name, RaceRecords.RankingsCnt, t, oldrec);
            return;
        }

        // A genuine new record.
        int prevRec = Scoring.GameScores.TimeEncode(r.OldRecordTime);
        switch (r.Kind)
        {
            case RaceRecordKind.NewImproved: // improved your own ranked time
                NotificationSystem.Info("RACE_NEW_IMPROVED", name, r.NewPos, t, prevRec);
                break;
            case RaceRecordKind.NewSet:      // a position that was previously empty
                NotificationSystem.Info("RACE_NEW_SET", name, r.NewPos, t);
                break;
            case RaceRecordKind.NewBroken:   // broke someone else's record at this rank
                NotificationSystem.Info("RACE_NEW_BROKEN", name, r.OldRecordHolder, r.NewPos, t, prevRec);
                break;
        }
    }

    /// <summary>The current persistent server record (rank 1) lap time for this map (0 if none).</summary>
    public float ServerRecord => RaceRecords.ServerRecord(MapName, RecordType);
    public string ServerRecordHolder => RaceRecords.ServerRecordHolder(MapName, RecordType);

    /// <summary>QC the finish kill-delay: schedule the racer to be retracted to start after <paramref name="now"/> + delay.</summary>
    private void ScheduleRetract(Player p, float now)
    {
        float delay = Cvar("g_race_respawn_delay", 0f); // 0 = retract on the next Tick (race_PreparePlayer is immediate)
        _retractAt[p] = now + System.MathF.Max(0f, delay);
    }

    /// <summary>
    /// QC <c>race_PreparePlayer</c> + <c>race_RetractPlayer</c>: reset a racer's lap/checkpoint state so they
    /// restart from the start line. The host's <see cref="OnFinishRetract"/> does the actual teleport (+ optional
    /// silent kill); this clears the simulation-side timing.
    /// </summary>
    public void RetractPlayer(Player p)
    {
        RaceState st = GetState(p);
        st.LapStartTime = 0f;
        st.MoveTimeFrac = 0f;       // QC race_PreparePlayer: zero the deterministic lap clock for a fresh start
        st.MoveTimeCount = 0f;
        st.RaceMoveTime = 0f;
        st.NextCheckpoint = -1; // re-expect the start line
        // QC race_PreparePlayer (server/race.qc:1220): a retract to start clears the respawn anchor so the racer
        // restarts from the grid/start line rather than their last mid-track checkpoint.
        st.RespawnCheckpoint = 0;
        st.RespawnSpot = null;
        st.Started = false;
        OnFinishRetract?.Invoke(p);
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(rc, AbortSpeedrun)</c> (sv_race.qc): the SPEEDRUN cheat (impulse 141) teleported
    /// the racer back to their personal checkpoint, so the in-progress lap is no longer a legitimate run — drop the
    /// lap clock / checkpoint progress (QC race_PreparePlayer) WITHOUT the finish-retract teleport (the speedrun
    /// cheat does its own teleport). Identical timing reset to <see cref="RetractPlayer"/> minus <see cref="OnFinishRetract"/>.
    /// </summary>
    public void AbortSpeedrun(Player p)
    {
        RaceState st = GetState(p);
        st.LapStartTime = 0f;
        st.MoveTimeFrac = 0f;
        st.MoveTimeCount = 0f;
        st.RaceMoveTime = 0f;
        st.NextCheckpoint = -1; // re-expect the start line
        st.RespawnCheckpoint = 0;
        st.RespawnSpot = null;
        st.Started = false;
    }

    /// <summary>
    /// Per-frame: expire penalty-zone freezes (QC the rc PlayerPhysics race_penalty slice) and fire any due
    /// finish-retracts (QC the deferred kill-delay re-teleport). Call once per server frame with the sim time.
    /// </summary>
    public void Tick(float now)
    {
        // QC rc PlayerPhysics (sv_race.qc:141): a racer with an active race_penalty is frozen (velocity 0,
        // MOVETYPE_NONE) until time > penalty; then it clears. We drive both the expiry and the freeze here.
        foreach (var kv in _states)
        {
            RaceState st = kv.Value;
            if (st.PenaltyUntil <= 0f)
                continue;
            if (now > st.PenaltyUntil)
            {
                st.PenaltyUntil = 0f;
                // Release the freeze: restore the movetype the racer had before the freeze (QC just clears
                // race_penalty and lets the next PlayerPhysics frame re-apply the real movetype). Only override the
                // frozen MOVETYPE_NONE — if something else re-set the movetype meanwhile, leave it.
                if (kv.Key.MoveType == MoveType.None)
                    kv.Key.MoveType = st.PenaltyPrevMoveType;
            }
            else
            {
                kv.Key.Velocity = Vector3.Zero;       // QC: player.velocity = '0 0 0'
                kv.Key.MoveType = MoveType.None;      // QC: set_movetype(player, MOVETYPE_NONE)
            }
        }

        if (_retractAt.Count == 0) return;
        List<Player>? due = null;
        foreach (var kv in _retractAt)
            if (now >= kv.Value) (due ??= new List<Player>()).Add(kv.Key);
        if (due is null) return;
        foreach (Player p in due) { _retractAt.Remove(p); RetractPlayer(p); }
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(rc, PlayerPhysics)</c> (sv_race.qc:128). Three faithful slices, run every physics
    /// frame for a live racer:
    /// <list type="number">
    ///   <item>Advance the deterministic <c>.race_movetime</c> lap clock by the physics tic dt (split into an integer
    ///     count + [0,1) fraction to preserve precision over a long session) — the frame-rate-independent lap time
    ///     <see cref="CrossCheckpoint"/> reads at the finish, instead of wall-clock <c>time</c>.</item>
    ///   <item>Apply the <c>race_penalty</c> freeze: while the penalty is active zero velocity + force MOVETYPE_NONE
    ///     (QC also sets <c>disableclientprediction = 2</c> — see the note below); expire it once the clock passes.</item>
    ///   <item>Force keyboard-movement quantization for record fairness: snap an off-axis analog wish-move to pure-X,
    ///     pure-Y, or a 45° diagonal (<c>M_SQRT1_2 * wishspeed</c>) so an analog-stick racer can't gain a sub-cardinal
    ///     edge. Only fires when both axes are non-zero AND unequal (QC's <c>x != 0 &amp;&amp; y != 0 &amp;&amp; x != y</c> guard);
    ///     pure cardinals and exact diagonals are already keyboard-legal and pass through untouched.</item>
    /// </list>
    /// NOTE (cross-boundary): QC's <c>disableclientprediction = 2</c> during the freeze has no port mechanism — the
    /// port has no per-player client-prediction-disable field (the same gap bugrigs.qc / vehicle boarding defer). The
    /// authoritative side (velocity 0 + MOVETYPE_NONE) is applied; a pure remote client may briefly mispredict through
    /// the freeze, but the server snaps it back. The penalty HUD line is fed separately (<see cref="PenaltyEventTime"/>).
    /// </summary>
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs a)
    {
        Entity player = a.Player;
        if (player is not Player p || p.IsDead || p.IsObserver)
            return false;
        RaceState st = GetState(p);

        // (1) QC: race_movetime_frac += dt; f = floor(frac); frac -= f; count += f; race_movetime = frac + count.
        float dt = a.TicRate;
        st.MoveTimeFrac += dt;
        float fl = System.MathF.Floor(st.MoveTimeFrac);
        st.MoveTimeFrac -= fl;
        st.MoveTimeCount += fl;
        st.RaceMoveTime = st.MoveTimeFrac + st.MoveTimeCount;

        // (2) QC: if(race_penalty) { if(time > race_penalty) race_penalty = 0; else { velocity=0; MOVETYPE_NONE; } }
        if (st.PenaltyUntil > 0f)
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            if (now > st.PenaltyUntil)
            {
                st.PenaltyUntil = 0f;
                if (p.MoveType == MoveType.None)
                    p.MoveType = st.PenaltyPrevMoveType; // restore Swim/Fly/Noclip rather than forcing Walk
            }
            else
            {
                p.Velocity = Vector3.Zero;
                p.MoveType = MoveType.None;
                // QC: player.disableclientprediction = 2 — no port mechanism (see the doc-comment note above).
            }
        }

        // (3) QC "force kbd movement for fairness": snap an off-axis analog wish-move to a keyboard octant.
        float mx = player.MovementForward;
        float my = player.MovementRight;
        float ax = System.MathF.Abs(mx);
        float ay = System.MathF.Abs(my);
        // QC: if(wishvel.x != 0 && wishvel.y != 0 && wishvel.x != wishvel.y) — only true off-axis analog motion.
        if (ax == 0f || ay == 0f || ax == ay)
            return false;

        float wishspeed = System.MathF.Sqrt(ax * ax + ay * ay); // QC vlen('ax ay 0')
        if (ax >= 2f * ay)
        {
            // QC: pure X motion
            player.MovementForward = mx > 0f ? wishspeed : -wishspeed;
            player.MovementRight = 0f;
        }
        else if (ay >= 2f * ax)
        {
            // QC: pure Y motion
            player.MovementForward = 0f;
            player.MovementRight = my > 0f ? wishspeed : -wishspeed;
        }
        else
        {
            // QC: diagonal — both axes get M_SQRT1_2 * wishspeed, sign-preserved.
            const float MSqrt1_2 = 0.70710678118654752440f;
            player.MovementForward = (mx > 0f ? 1f : -1f) * MSqrt1_2 * wishspeed;
            player.MovementRight   = (my > 0f ? 1f : -1f) * MSqrt1_2 * wishspeed;
        }
        return false;
    }

    // ============================================================================================
    //  Penalty zones (QC trigger_race_penalty / race_ImposePenaltyTime, sv_race.qc + server/race.qc)
    // ============================================================================================

    /// <summary>
    /// QC race_ImposePenaltyTime (server/race.qc:1275): apply a <paramref name="seconds"/> penalty to a racer.
    /// In QUALIFYING the penalty is added to the lap time (race_penalty_accumulator, no freeze); in a plain RACE
    /// the racer is FROZEN (velocity 0 + MOVETYPE_NONE) until <c>time + seconds</c> (driven by <see cref="Tick"/>).
    /// </summary>
    public void SetPenalty(Player p, float seconds, string reason = "missing a checkpoint")
    {
        if (seconds <= 0f)
            return;
        RaceState st = GetState(p);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        // QC race_ImposePenaltyTime → RACE_NET_PENALTY_*: stamp the penalty line for the HUD (magnitude + event time +
        // reason), in BOTH modes — qualifying shows the accumulated-into-the-clock penalty, race shows the freeze.
        st.LastPenaltySeconds = seconds;
        st.LastPenaltyEventTime = now;
        st.LastPenaltyReason = reason;
        if (Qualifying)
        {
            st.PenaltyAccumulator += seconds; // QC qualifying: accumulate into the lap time, no freeze
        }
        else
        {
            // Capture the racer's current movetype ONCE (a re-trigger while already frozen keeps the original, not
            // the frozen MOVETYPE_NONE) so the release restores Swim/Fly/Noclip rather than forcing Walk.
            if (st.PenaltyUntil <= 0f)
                st.PenaltyPrevMoveType = p.MoveType;
            st.PenaltyUntil = now + seconds;  // QC race: freeze until time > penalty
            p.Velocity = Vector3.Zero;
            p.MoveType = MoveType.None;
        }
    }

    /// <summary>
    /// QC penalty_touch (server/race.qc:1307): a racer entered a trigger_race_penalty zone. Each zone only fires
    /// once per pass (QC race_lastpenalty), then imposes its penalty time/reason. The zone's penalty seconds are
    /// read from the trigger entity's <see cref="Entity.GtPointAmt"/> (set by the spawnfunc, default 5).
    /// </summary>
    public void PenaltyTouch(Entity zone, Player toucher)
    {
        RaceState st = GetState(toucher);
        if (ReferenceEquals(st.LastPenalty, zone))
            return; // already penalized by this zone on this pass
        st.LastPenalty = zone;
        float seconds = zone.GtPointAmt > 0f ? zone.GtPointAmt : 5f; // QC: this.race_penalty default 5
        SetPenalty(toucher, seconds);
    }

    /// <summary>Whether a racer is currently frozen by a penalty zone (QC race_penalty &gt; 0 in a plain race).</summary>
    public bool IsPenalized(Player p)
        => _states.TryGetValue(p, out RaceState? st)
           && st.PenaltyUntil > 0f
           && (Api.Services is null ? 0f : Api.Clock.Time) <= st.PenaltyUntil;

    /// <summary>The penalty seconds accumulated onto a qualifying racer's time (QC race_penalty_accumulator).</summary>
    public float PenaltyAccumulatorOf(Player p)
        => _states.TryGetValue(p, out RaceState? st) ? st.PenaltyAccumulator : 0f;

    /// <summary>Read a float cvar with a fallback (QC autocvar with default).</summary>
    private static float Cvar(string name, float fallback)
        => TryCvar(name, out float v) ? v : fallback;

    /// <summary>The fastest single-lap time recorded for a racer (0 if none), QC SP_RACE_FASTEST.</summary>
    public float FastestLapOf(Player p) => _states.TryGetValue(p, out RaceState? st) ? st.FastestLap : 0f;

    /// <summary>
    /// QC WinningCondition_Race (sv_race.qc:77): the match ends only once EVERY tracked racer has completed the
    /// race (n == c). Otherwise, if the score limit (lap limit) has been reached by anyone, QC returns
    /// WINNING_STARTSUDDENDEATHOVERTIME — i.e. it does NOT end on the lap limit alone but runs on in sudden death
    /// until everyone finishes (no equality/tie when laps are all raced). We latch <see cref="SuddenDeath"/> in
    /// that case rather than ending the match.
    /// </summary>
    public void CheckWinningCondition()
    {
        if (_states.Count == 0)
            return;

        int total = 0, completed = 0;
        foreach (RaceState st in _states.Values)
        {
            total++;
            if (st.Completed) completed++;
        }

        // QC: if (n && n == c) return WINNING_YES — everyone finished, the match is over.
        if (total > 0 && total == completed)
        {
            MatchEnded = true;
            return;
        }

        // QC: wc = WinningCondition_Scores(fraglimit). If the lap limit was reached but not everyone finished,
        // START SUDDEN DEATH (run on) instead of ending. The lap-limit reach is the race_completing latch
        // (set by FinishRacer/StartCompleting the moment the first racer hits the limit).
        if (!SuddenDeath && Qualifying == false && RaceCompleting && completed < total)
            SuddenDeath = true;

        // QC MUTATOR_HOOKFUNCTION(rc, Scores_CountFragsRemaining) returns true when !g_race_qualifying:
        // announce "N laps left" once as the leader approaches the lap limit, but only in race mode (not
        // qualifying time-trial, where the primary key is SP_RACE_FASTEST, not laps).
        if (!Qualifying && Api.Services is not null)
        {
            float lapLimit = LapLimit;
            // Compute the top and second lap counts from SP_RACE_LAPS (the primary key in race mode).
            Scoring.ScoreField? lapsField = Scoring.GameScores.Field("RACE_LAPS");
            if (lapsField is not null)
            {
                int topLaps = 0, secondLaps = 0;
                foreach (var kv in _states)
                {
                    int laps = Scoring.GameScores.Get(kv.Key, lapsField);
                    if (laps > topLaps) { secondLaps = topLaps; topLaps = laps; }
                    else if (laps > secondLaps) secondLaps = laps;
                }
                Scoring.GameScores.CountFragsRemaining(lapLimit, 0f, topLaps, secondLaps, suddenDeathEnding: false);
            }
        }
    }

    /// <summary>
    /// QC rc_SetLimits + reset_map_global (sv_race.qc:199/394): the <c>g_race_qualifying == 2</c> "qualifying
    /// THEN race" transition — once the qualifying session ends, clear qualifying (cvar → 0), restore the saved
    /// fraglimit/leadlimit/timelimit, re-run the score rules for the plain-race schema, and reset every racer.
    /// Returns true if a transition happened (it was qualifying==2). Invoke from the host when the qualifying
    /// timelimit elapses; the shared CheckRules path is left untouched (this is a Race-local swap).
    /// </summary>
    public bool TransitionQualifyingToRace()
    {
        if (Api.Services is null)
            return false;
        // QC: if(g_race_qualifying == 2) { g_race_qualifying = 0; ... }
        if ((int)Cvar(CvarQualifying, 0f) != 2)
            return false;

        Api.Cvars.Set(CvarQualifying, "0"); // g_race_qualifying = 0
        // QC restores the saved race_fraglimit/leadlimit/timelimit; the host owns those cvars, so we restore
        // any saved values it stashed (default: leave the configured fraglimit in place).
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (_savedFragLimit >= 0f) Api.Cvars.Set("fraglimit", _savedFragLimit.ToString(ic));
        if (_savedLeadLimit >= 0f) Api.Cvars.Set("leadlimit", _savedLeadLimit.ToString(ic));
        if (_savedTimeLimit >= 0f) Api.Cvars.Set("timelimit", _savedTimeLimit.ToString(ic));

        DeclareScoreRules(); // QC race_ScoreRules() — re-declare for the now-plain race
        SuddenDeath = false;

        // QC FOREACH_CLIENT race_PreparePlayer: reset every racer to the start.
        foreach (Player p in new List<Player>(_states.Keys))
        {
            RaceState st = _states[p];
            st.LapStartTime = 0f;
            st.MoveTimeFrac = 0f;
            st.MoveTimeCount = 0f;
            st.RaceMoveTime = 0f;
            st.NextCheckpoint = -1;
            st.Completed = false;
            st.PenaltyAccumulator = 0f;
            st.PenaltyUntil = 0f;
            st.RespawnCheckpoint = 0; // QC race_PreparePlayer: reset the respawn anchor for the now-plain race
            st.RespawnSpot = null;
            st.Started = false;
        }
        return true;
    }

    /// <summary>Stash the race fraglimit/leadlimit/timelimit to restore on the qualifying→race transition (QC
    /// race_fraglimit/race_leadlimit/race_timelimit captured in rc_SetLimits). -1 = unset (don't restore).</summary>
    public void SaveRaceLimits(float fragLimit, float leadLimit, float timeLimit)
    {
        _savedFragLimit = fragLimit;
        _savedLeadLimit = leadLimit;
        _savedTimeLimit = timeLimit;
    }
    private float _savedFragLimit = -1f, _savedLeadLimit = -1f, _savedTimeLimit = -1f;

    /// <summary>QC MUTATOR_HOOKFUNCTION(rc, PlayerDies): a death just forces a respawn; no scoring in race.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;

        // Death never scores in race and never ends the race — the racer is simply sent back to respawn.
        // QC race_RetractPlayer (server/race.qc:1229): a racer is returned to their LAST passed checkpoint, not
        // the grid start. If that checkpoint is the start/finish line (0) the lap clock is cleared (a fresh lap);
        // otherwise the lap clock is kept and the racer resumes expecting the checkpoint AFTER the one they
        // respawn at (QC race_checkpoint = race_respawn_checkpoint, advanced by the next crossing).
        RaceState st = GetState(victim);
        int respawnCp = st.RespawnSpot is not null ? st.RespawnCheckpoint : 0;
        if (respawnCp == 0)
        {
            st.LapStartTime = 0f;        // QC race_ClearTime: start the next lap fresh at the start line
            st.MoveTimeFrac = 0f;        // QC: zero the deterministic lap clock for the fresh lap
            st.MoveTimeCount = 0f;
            st.RaceMoveTime = 0f;
            st.NextCheckpoint = -1;      // re-expect the start line
        }
        else
        {
            // resume from the respawn checkpoint: the next required CP is the one after it (wrap at the finish).
            st.NextCheckpoint = respawnCp >= HighestCheckpoint ? 0 : respawnCp + 1;
        }
        // QC race_AbandonRaceCheck (driven from PutClientInServer/respawn): once the race is completing, a racer
        // who dies and would respawn into a finished race is auto-abandoned rather than left running forever.
        AbandonRaceCheck(victim);
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }

    /// <summary>
    /// QC <c>trigger_race_checkpoint_spawn_evalfunc</c> + <c>race_respawn_spotref</c> (server/race.qc:1055/808):
    /// place a (re)spawning racer back at the checkpoint they last reached (the SPAWN_PRIO_RACE_PREVIOUS_SPAWN
    /// bias) rather than at a random grid start. The host calls this just before <c>SelectSpawnPoint</c>: it sets
    /// the one-shot forced-spawn redirect (<see cref="Entity.SpawnPointTarg"/>) to the racer's respawn checkpoint
    /// entity, which SelectSpawnPoint short-circuits to (and PutPlayerInServer consumes). A racer who hasn't
    /// passed any checkpoint yet (or qualifying, which always restarts from the start line) keeps the normal
    /// start-spawn selection. Returns true if a checkpoint respawn spot was applied.
    /// </summary>
    public bool ApplyRespawnSpot(Player p)
    {
        if (!_states.TryGetValue(p, out RaceState? st))
            return false;
        // QC: qualifying always respawns at the start line (race_RetractPlayer clears to checkpoint 0); only a
        // plain race returns the racer to their last checkpoint.
        if (Qualifying)
            return false;
        Entity? spot = st.RespawnSpot;
        if (spot is null || spot.IsFreed || st.RespawnCheckpoint == 0)
            return false; // no passed checkpoint (or back at the start line) — use the normal start spawn
        p.SpawnPointTarg = spot; // QC race_respawn_spotref → the forced one-shot respawn spot
        return true;
    }

    // ============================================================================================
    //  Speed award (QC server/race.qc race_SpeedAwardFrame, sv_race.qc GetPressedKeys hook)
    // ============================================================================================
    // QC tracks the best HORIZONTAL (planar) speed seen this round (speedaward_speed/holder) and the persisted
    // all-time best (speedaward_alltimebest, ServerProgsDB). Both are broadcast to clients (RACE_NET_SPEED_AWARD /
    // _BEST) and shown on the race scoreboard ("Speed award: N qu/s (holder)" / "All-time fastest: ...").
    // The port computes the same values server-side and exposes them so a listen-server scoreboard can render them.

    private float _speedawardSpeed;            // QC speedaward_speed: best planar speed this round (qu/s)
    private string _speedawardHolder = "";     // QC speedaward_holder
    private string _speedawardUid = "";        // QC speedaward_uid
    private float _speedawardAllTimeBest;      // QC speedaward_alltimebest (persisted)
    private string _speedawardAllTimeHolder = ""; // QC speedaward_alltimebest_holder
    private bool _speedawardLoaded;            // lazily pull the persisted all-time best once the map is known

    /// <summary>QC <c>speedaward_speed</c>: the best planar speed (qu/s) achieved this round, for the scoreboard
    /// "Speed award" line. 0 = nobody has moved yet.</summary>
    public float SpeedAwardSpeed => _speedawardSpeed;
    /// <summary>QC <c>speedaward_holder</c>: the net name of the round-best-speed holder ("" if none).</summary>
    public string SpeedAwardHolder => _speedawardHolder;
    /// <summary>QC <c>speedaward_alltimebest</c>: the persisted all-time best planar speed (qu/s) for this map.</summary>
    public float SpeedAwardBest { get { EnsureSpeedAwardLoaded(); return _speedawardAllTimeBest; } }
    /// <summary>QC <c>speedaward_alltimebest_holder</c>: the all-time best-speed holder name ("" if none).</summary>
    public string SpeedAwardBestHolder { get { EnsureSpeedAwardLoaded(); return _speedawardAllTimeHolder; } }

    private void EnsureSpeedAwardLoaded()
    {
        if (_speedawardLoaded || Api.Services is null)
            return;
        _speedawardLoaded = true;
        // QC race_SendAll: speedaward_alltimebest = stof(db_get(map, record_type, "speed/speed")); holder via uid2name.
        _speedawardAllTimeBest = RaceRecords.ReadSpeedAwardBest(MapName, RecordType);
        _speedawardAllTimeHolder = RaceRecords.ReadSpeedAwardBestHolder(MapName, RecordType);
    }

    /// <summary>
    /// QC <c>race_SpeedAwardFrame</c> (server/race.qc:304), driven from the race gametype's per-frame tick
    /// (sv_race.qc GetPressedKeys hook): track the round-best planar (horizontal) speed and, when it beats the
    /// persisted all-time best, update + persist that too. QC also calls <c>race_checkAndWriteName</c> to keep the
    /// uid→name DB current; the port mirrors that by recording the holder's UID→name in <see cref="RaceRecords"/>.
    /// Call once per server frame with the live player list.
    /// </summary>
    public void SpeedAwardFrame(System.Collections.Generic.IReadOnlyList<Player> players)
    {
        EnsureSpeedAwardLoaded();
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            if (p.IsObserver || p.IsDead) // QC: if (IS_OBSERVER(player)) return;
                continue;

            // QC race_checkAndWriteName: keep the uid→display-name DB current (gated on the consent cvars in QC;
            // the port models consent as a non-empty PersistentId).
            if (!string.IsNullOrEmpty(p.PersistentId))
                RaceRecords.SetName(p.PersistentId, p.NetName);

            // QC vdist(player.velocity - player.velocity_z * '0 0 1', >, speedaward_speed): planar speed only.
            System.Numerics.Vector3 v = p.Velocity;
            float planar = System.MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            if (planar > _speedawardSpeed)
            {
                _speedawardSpeed = planar;           // QC speedaward_speed
                _speedawardHolder = p.NetName;        // QC speedaward_holder
                _speedawardUid = p.PersistentId;      // QC speedaward_uid

                // QC: a new round best that also beats the all-time best (and has a UID) is persisted + becomes
                // the all-time holder.
                if (planar > _speedawardAllTimeBest && !string.IsNullOrEmpty(_speedawardUid))
                {
                    _speedawardAllTimeBest = planar;      // QC speedaward_alltimebest
                    _speedawardAllTimeHolder = p.NetName;  // QC speedaward_alltimebest_holder
                    RaceRecords.WriteSpeedAwardBest(MapName, RecordType, planar, _speedawardUid);
                }
            }
        }
    }

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }
}
