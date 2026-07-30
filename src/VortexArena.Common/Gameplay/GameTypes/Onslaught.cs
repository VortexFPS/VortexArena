using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Onslaught gametype — port of <c>CLASS(Onslaught, Gametype)</c>
/// (common/gametypes/gametype/onslaught/onslaught.qh + sv_onslaught.qc).
///
/// A team objective mode: each team defends a generator and seizes a network of control points to extend its
/// reach toward — and ultimately destroy — the enemy generator. A generator only becomes vulnerable once the
/// attacking team controls a control point linked to it; reducing the enemy generator's health to zero
/// destroys it and wins the round for the attackers (QC: the team whose generator is destroyed loses; with
/// pointlimit=1 a single generator kill ends the match). Rounds are time-bounded (gametype default
/// timelimit=20, plus a per-round timelimit via the round handler).
///
/// Faithfully ported (the generator-destruction win rule + link graph):
///  - per-team generator health + alive flag (<see cref="GeneratorState"/>, QC g_onslaught_gen_health);
///  - the control-point + generator + link graph (<see cref="AddGenerator"/>/<see cref="AddControlPoint"/>/
///    <see cref="Link"/>, QC onslaught_generator / onslaught_controlpoint + ons_worldlinklist) with the
///    power-flow propagation that decides which nodes are shielded vs attackable
///    (<see cref="UpdateLinks"/>, QC onslaught_updatelinks);
///  - generator damage/destruction gated by the shield (<see cref="DamageGenerator"/>, QC ons_GeneratorDamage)
///    → the other team wins the round when one generator falls;
///  - the round handler + warmup (<see cref="Handler"/>, QC round_handler_Spawn) via <see cref="Tick"/>;
///  - the control-point CAPTURE-BY-BUILD combat (QC ons_ControlPoint_Touch → Icon_Spawn → BuildThink → Think /
///    Icon_Damage / Icon_Heal) + the generator damage pipeline (QC ons_GeneratorSetup / ons_GeneratorDamage),
///    both driven through the shared <see cref="Damage.DamageSystem"/> via <see cref="Entity.GtEventDamage"/>
///    (<see cref="CpCombat"/>): a point only flips once its build icon completes, and a generator only takes
///    real weapon damage once an enemy controls a linked CP;
///  - the overtime generator-decay (QC the stalemate branch of Onslaught_CheckWinner) via <see cref="Tick"/>.
///
/// Deferred (NOTE — cross-boundary): the generator/control-point/icon entity models + sprites (CSQC), the
/// proximity-decap (g_onslaught_cp_proximitydecap), vehicles/turrets, and the bot waypointing — client/AI.
/// </summary>
[GameType]
public sealed class Onslaught : GameType
{
    // ----- generator health cvar + default (QC autocvar_g_onslaught_gen_health) -----
    private const string CvarGenHealth    = "g_onslaught_gen_health";
    private const float  DefaultGenHealth = 2500f; // QC default g_onslaught_gen_health (gametypes-server.cfg:576)

    /// <summary>A team's generator (QC <c>onslaught_generator</c> entity: health + destroyed state).</summary>
    public sealed class GeneratorState
    {
        /// <summary>Remaining generator health (QC RES_HEALTH). Destroyed at 0.</summary>
        public float Health;

        /// <summary>Max/starting health (QC max_health = g_onslaught_gen_health).</summary>
        public float MaxHealth;

        /// <summary>True once health has reached 0 (QC generator dead → its team loses).</summary>
        public bool Destroyed => Health <= 0f;
    }

    /// <summary>
    /// A node in the Onslaught power graph — a generator or a control point (QC onslaught_generator /
    /// onslaught_controlpoint share the iscaptured/islinked/isshielded flags + .team). Links between nodes
    /// (QC ons_worldlinklist) carry power outward from the generators.
    /// </summary>
    public sealed class OnsNode
    {
        /// <summary>True if this node is a generator (QC classname "onslaught_generator").</summary>
        public bool IsGenerator;
        /// <summary>The team owning this node (QC .team); a CP starts neutral.</summary>
        public int Team = Teams.None;
        /// <summary>QC .iscaptured: a generator is captured while alive; a CP is captured once a team owns it.</summary>
        public bool Captured;
        /// <summary>QC .islinked: powered (reachable from its team's generator through same-team links).</summary>
        public bool Linked;
        /// <summary>QC .waslinked: the previous <see cref="Linked"/> value, tracked by the steady-state icon think so a
        /// link-state change can re-fire the CP's map targets (re-team linked spawnpoints) exactly once per flip.</summary>
        public bool WasLinked;
        /// <summary>QC .isshielded: invulnerable — true unless an enemy-owned powered neighbor exposes it.</summary>
        public bool Shielded = true;
        /// <summary>Generator state when <see cref="IsGenerator"/> (health/destroyed), else null.</summary>
        public GeneratorState? Gen;
        /// <summary>Adjacent nodes via links (QC ons_worldlinklist neighbors).</summary>
        public readonly List<OnsNode> Neighbors = new();
        /// <summary>Stable id (control-point id for the legacy CaptureControlPoint API).</summary>
        public int Id;
        /// <summary>QC .targetname: the map name an <c>onslaught_link</c> uses to reference this node (or empty).</summary>
        public string Name = string.Empty;
    }

    /// <summary>Every node in the power graph (generators + control points), QC ons_worldgeneratorlist + ons_worldcplist.</summary>
    public readonly List<OnsNode> Nodes = new();

    /// <summary>Generators keyed by owning team color code (see <see cref="Teams"/>).</summary>
    private readonly Dictionary<int, GeneratorState> _generators = new();

    /// <summary>Generator nodes keyed by team, and control-point nodes keyed by id, for fast lookup.</summary>
    private readonly Dictionary<int, OnsNode> _genNodes = new();
    private readonly Dictionary<int, OnsNode> _cpNodes = new();

    /// <summary>Nodes keyed by their QC .targetname (for resolving <c>onslaught_link</c> target/target2 → edges).</summary>
    private readonly Dictionary<string, OnsNode> _nodesByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Staged onslaught_link target/target2 name pairs, resolved into edges post-spawn (QC ons_DelayedLinkSetup).</summary>
    private readonly List<(string a, string b)> _stagedLinks = new();

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>
    /// The control-point + generator COMBAT layer (QC sv_onslaught.qc icon/generator funcs): the buildable
    /// capture icons, the generator damage entities, and the overtime decay. Owns the entity ↔ graph bridge so
    /// weapon damage routes through the shared <see cref="Damage.DamageSystem"/> via
    /// <see cref="Entity.GtEventDamage"/>. Created in the ctor (the graph is this <see cref="Onslaught"/>).
    /// (Named <c>CpCombat</c> — not <c>Combat</c> — so it doesn't shadow the static
    /// <see cref="Damage.Combat"/> bus this file uses for the death hook.)
    /// </summary>
    public OnslaughtControlPoint CpCombat { get; }

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _spawnHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-round latch: true once a generator has been destroyed.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The winning team color code (the team whose generator survived), or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public Onslaught()
    {
        NetName = "ons";
        DisplayName = "Onslaught";
        TeamGame = true;
        CpCombat = new OnslaughtControlPoint(this);
    }

    /// <summary>Absolute sim time this overtime decay last fired (one step per second, QC ons_overtime_damagedelay).</summary>
    private float _nextOvertimeDecay;

    public override void OnInit()
    {
        // QC: generators/control-points/links are built from the map's onslaught_* entities (see AddGenerator
        // / AddControlPoint / Link). gametype_init flags (TEAMPLAY) are the engine's job; OnInit clears state.
        Nodes.Clear();
        _generators.Clear();
        _genNodes.Clear();
        _cpNodes.Clear();
        _nodesByName.Clear();
        _stagedLinks.Clear();
        CpCombat.Reset();
    }

    /// <summary>Starting generator health (g_onslaught_gen_health, else 2000), QC max_health.</summary>
    public float GeneratorHealth => TryCvar(CvarGenHealth, out float v) && v > 0f ? v : DefaultGenHealth;

    /// <summary>Starting control-point health (g_onslaught_cp_health, else 1000), QC max_health (gametypes-server.cfg:578).</summary>
    public float ControlPointHealth => TryCvar("g_onslaught_cp_health", out float v) && v > 0f ? v : 1000f;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        _nextOvertimeDecay = 0f;
        CpCombat.ClearOvertime();
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC ons_ScoreRules (sv_onslaught.qc) GameRules_scoring(teams, SFL_SORT_PRIO_PRIMARY, 0, {
        // field_team(ST_ONS_GENS, "generators", PRIMARY); field(SP_ONS_CAPS, "caps", SECONDARY); field(SP_ONS_TAKES,
        // "takes", 0); }): the player primary is SP_SCORE with SP_ONS_CAPS secondary; ST_SCORE (team slot 0) has no
        // prio (stprio=0); ST_ONS_GENS (team slot 1) "generators" is the TEAM primary — credited +1 per generator
        // destroyed (round win, see BankRoundWin). Onslaught's team set is generator-derived (built later from the
        // map), so the team slots are seeded lazily by the first credit rather than SeedTeams here.
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0
        GS.SetTeamLabel(GS.TeamSlotSecondary, "generators", Scoring.ScoreFlags.SortPrioPrimary); // ST_ONS_GENS (slot 1)
        GS.DeclareColumn("ONS_CAPS", Scoring.ScoreFlags.None, "caps");   // QC field(SP_ONS_CAPS, "caps", SECONDARY)
        GS.DeclareColumn("ONS_TAKES", Scoring.ScoreFlags.None, "takes"); // QC field(SP_ONS_TAKES, "takes", 0)
        GS.SetSortKeys(GS.Score, GS.Field("ONS_CAPS"));

        // QC round_handler_Spawn(Onslaught_CheckTeams?, Onslaught_CheckWinner, ...). Onslaught rounds restart
        // when a generator falls; the handler drives warmup/countdown.
        // QC round_handler_Spawn(Onslaught_CheckPlayers, Onslaught_CheckWinner, Onslaught_RoundStart) then
        // round_handler_Init(5, g_onslaught_warmup, g_onslaught_round_timelimit) at ons_DelayedInit
        // (sv_onslaught.qc:2155-2156): the FIRST round arms with end-delay 5. Inside Onslaught_CheckWinner,
        // when a round actually ends, QC calls round_handler_Init(7, …) (sv_onslaught.qc:1236), so every
        // SUBSEQUENT round uses end-delay 7 — a 2 s longer end-delay after round 1. We reproduce that by
        // bumping EndDelay to 7 the moment CanRoundEnd first reports the round decided (see CanRoundEndOns).
        // (Onslaught_CheckPlayers — canRoundStart — is `return 1`.)
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = () => true,
            CanRoundEnd = CanRoundEndOns,
            OnRoundStart = () => { },
        };
        Handler.Init(5f, Cvar("g_onslaught_warmup", 5f), Cvar("g_onslaught_round_timelimit", 500f));

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(ons, PlayerSpawn): the overtime center-print + the spawn placement (CP-to-CP
        // teleport via the click-radar choice, or the cvar-gated spawn_at_controlpoints / spawn_at_generator
        // placement near the player's death location). See OnPlayerSpawn.
        _spawnHandler = OnPlayerSpawn;
        MutatorHooks.PlayerSpawn.Add(_spawnHandler);
    }

    public override void Deactivate()
    {
        if (_spawnHandler is not null)
        {
            MutatorHooks.PlayerSpawn.Remove(_spawnHandler);
            _spawnHandler = null;
        }
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// Drive the overtime generator-decay (QC the stalemate branch of Onslaught_CheckWinner): once the timelimit /
    /// round end-time elapses, every generator self-damages each second (scaled by the enemy-linked control-point
    /// count) until one falls. Call each tick.
    /// </summary>
    /// <remarks>
    /// The round handler itself (<see cref="Handler"/>) is NOT ticked here: the host drives the LIVE server round
    /// handler off <see cref="CanRoundStartLive"/>/<see cref="CanRoundEndLive"/> (GameWorld.EnableRounds) and mirrors
    /// its timing back into <see cref="Handler"/> each frame, so <see cref="Handler"/> is a passive mirror (matching
    /// Survival). Ticking it here too would double-invoke <see cref="CanRoundEndOns"/> → CheckWinner/BankRoundWin.
    /// </remarks>
    public void Tick()
    {
        DriveOvertime();
    }

    /// <summary>
    /// QC Onslaught_CheckWinner overtime gate (sv_onslaught.qc:1174): true once <c>timelimit</c> has elapsed
    /// since game start, or the round's end time has passed. While true, the generators decay
    /// (<see cref="OnslaughtControlPoint.OvertimeDecayTick"/>).
    /// </summary>
    public bool IsInOvertime()
    {
        if (MatchEnded || Api.Services is null)
            return false;
        float now = Api.Clock.Time;
        // QC: time > game_starttime + timelimit*60. The headless gametype runs the match from t=0 (the host's
        // game_starttime countdown is applied upstream by GameWorld); compare against the elapsed time directly.
        float timelimit = Cvar("timelimit", 0f);
        if (timelimit > 0f && now > GameStartTime + timelimit * 60f)
            return true;
        // QC: round_handler_GetEndTime() > 0 && endtime - time <= 0.
        if (Handler is { RoundEndTime: > 0f } h && h.RoundEndTime - now <= 0f && h.IsRoundStarted)
            return true;
        return false;
    }

    /// <summary>QC game_starttime — when the match begins. The host raises this for a countdown; default 0.</summary>
    public float GameStartTime { get; set; }

    /// <summary>Step the overtime decay at most once per second while in overtime (QC ons_overtime_damagedelay = time + 1).</summary>
    private void DriveOvertime()
    {
        if (!IsInOvertime())
        {
            CpCombat.ClearOvertime();
            return;
        }
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (now < _nextOvertimeDecay)
            return;
        _nextOvertimeDecay = now + 1f; // QC: ons_overtime_damagedelay = time + 1
        CpCombat.OvertimeDecayTick();
    }

    /// <summary>QC onslaught_generator spawn: register/reset a team's generator at full health (graph node).</summary>
    public GeneratorState RegisterGenerator(int team) => AddGenerator(team).Gen!;

    /// <summary>
    /// QC onslaught_generator spawn: add a generator node for <paramref name="team"/> at full health,
    /// captured + linked (a live generator is always powered). Returns the node.
    /// </summary>
    public OnsNode AddGenerator(int team) => AddGenerator(team, null);

    /// <summary>
    /// QC onslaught_generator spawn with a map <paramref name="name"/> (.targetname): same as
    /// <see cref="AddGenerator(int)"/> but also indexes the node by name so an <c>onslaught_link</c> can
    /// reference it (see <see cref="StageLink"/>/<see cref="ResolveLinks"/>).
    /// </summary>
    public OnsNode AddGenerator(int team, string? name)
    {
        float h = GeneratorHealth;
        var gen = new GeneratorState { Health = h, MaxHealth = h };
        var node = new OnsNode { IsGenerator = true, Team = team, Captured = true, Linked = true, Gen = gen };
        Nodes.Add(node);
        _generators[team] = gen;
        _genNodes[team] = node;
        RegisterNodeName(node, name);
        return node;
    }

    /// <summary>
    /// QC onslaught_controlpoint spawn: add a (neutral) control-point node with a stable
    /// <paramref name="controlPointId"/>. CPs start neutral, unlinked, shielded until power reaches them.
    /// </summary>
    public OnsNode AddControlPoint(int controlPointId) => AddControlPoint(controlPointId, null);

    /// <summary>
    /// QC onslaught_controlpoint spawn with a map <paramref name="name"/> (.targetname): same as
    /// <see cref="AddControlPoint(int)"/> but also indexes the node by name so an <c>onslaught_link</c> can
    /// reference it (see <see cref="StageLink"/>/<see cref="ResolveLinks"/>).
    /// </summary>
    public OnsNode AddControlPoint(int controlPointId, string? name)
    {
        var node = new OnsNode { IsGenerator = false, Team = Teams.None, Captured = false, Id = controlPointId };
        Nodes.Add(node);
        _cpNodes[controlPointId] = node;
        RegisterNodeName(node, name);
        return node;
    }

    /// <summary>Index a node by its QC .targetname so links can resolve it (no-op for empty names).</summary>
    private void RegisterNodeName(OnsNode node, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        node.Name = name!;
        _nodesByName[name!] = node;
    }

    /// <summary>The node with the given QC .targetname (or null), for <c>onslaught_link</c> resolution.</summary>
    public OnsNode? NodeByName(string name)
        => !string.IsNullOrEmpty(name) && _nodesByName.TryGetValue(name, out var n) ? n : null;

    /// <summary>QC ons_worldlinklist: connect two nodes with a power link (bidirectional).</summary>
    public void Link(OnsNode a, OnsNode b)
    {
        if (!a.Neighbors.Contains(b)) a.Neighbors.Add(b);
        if (!b.Neighbors.Contains(a)) b.Neighbors.Add(a);
    }

    /// <summary>
    /// QC <c>spawnfunc(onslaught_link)</c>: stage an <c>onslaught_link</c> map entity by its two referenced
    /// node names (<c>.target</c> → goalentity, <c>.target2</c> → enemy). The actual edge is created later by
    /// <see cref="ResolveLinks"/>, mirroring QC's deferred <c>ons_DelayedLinkSetup</c> (INITPRIO_FINDTARGET):
    /// links are resolved AFTER all generators/control-points have spawned, so spawn order doesn't matter.
    /// Empty names are dropped (QC: a link with target=="" or target2=="" is removed without linking).
    /// </summary>
    public void StageLink(string target, string target2)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(target2))
            return; // QC: if (this.target == "" || this.target2 == "") delete(this); return;
        _stagedLinks.Add((target, target2));
    }

    /// <summary>
    /// QC <c>ons_DelayedLinkSetup</c> (INITPRIO_FINDTARGET post-spawn pass): turn every staged
    /// <see cref="StageLink"/> name pair into a graph edge by resolving each name to its node
    /// (<c>find(targetname)</c>), then re-propagate power (<see cref="UpdateLinks"/>) so the freshly-linked
    /// generators expose their first attackable neighbors. Call once after the BSP entity lump has spawned all
    /// onslaught_* nodes. Unresolvable names are skipped (QC objerror) so a partial map still links what it can.
    /// Returns the number of edges created.
    /// </summary>
    public int ResolveLinks()
    {
        int created = 0;
        foreach ((string a, string b) in _stagedLinks)
        {
            OnsNode? na = NodeByName(a);  // QC this.goalentity = find(NULL, targetname, this.target)
            OnsNode? nb = NodeByName(b);  // QC this.enemy     = find(NULL, targetname, this.target2)
            if (na is null || nb is null || ReferenceEquals(na, nb))
                continue;                 // QC objerror "can not find target"/"target2" — skip a dangling link
            if (!na.Neighbors.Contains(nb))
            {
                Link(na, nb);
                created++;
            }
        }
        _stagedLinks.Clear();
        UpdateLinks(); // QC: ons_Link_CheckUpdate → onslaught_updatelinks once the graph edges exist
        return created;
    }

    /// <summary>The generator node owned by a team (or null).</summary>
    public OnsNode? GeneratorNode(int team) => _genNodes.TryGetValue(team, out var n) ? n : null;

    /// <summary>The control-point node with the given id (or null).</summary>
    public OnsNode? ControlPointNode(int id) => _cpNodes.TryGetValue(id, out var n) ? n : null;

    /// <summary>The generator owned by a team, or null if none registered.</summary>
    public GeneratorState? GeneratorFor(int team) => _generators.TryGetValue(team, out GeneratorState? g) ? g : null;

    /// <summary>The team's generators-destroyed score (QC teamscores(team, ST_ONS_GENS) — the round wins banked
    /// on each enemy-generator kill). Read through the unified GameScores two-slot team store (slot 1).</summary>
    public int GetTeamGenerators(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    /// <summary>
    /// QC onslaught_updatelinks: propagate power from each (live) generator through same-team links, marking
    /// reachable nodes <see cref="OnsNode.Linked"/>; then a node is <see cref="OnsNode.Shielded"/> unless a
    /// powered enemy neighbor exposes it. Call after any capture or generator change; <see cref="DamageGenerator"/>
    /// and <see cref="CaptureControlPoint"/> call it for you.
    /// </summary>
    public void UpdateLinks()
    {
        // 1. Reset: generators are linked/shielded iff captured (alive); CPs start unlinked + shielded.
        foreach (var n in Nodes)
        {
            if (n.IsGenerator)
            {
                n.Captured = n.Gen is { Destroyed: false };
                n.Linked = n.Captured;
                n.Shielded = n.Captured;
            }
            else
            {
                n.Linked = false;
                n.Shielded = true;
            }
        }

        // 2. Flow power outward: a captured node adjacent (same team) to a powered captured node becomes powered.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in Nodes)
            {
                foreach (var m in n.Neighbors)
                {
                    if (n.Captured && m.Captured && n.Team == m.Team && n.Linked && !m.Linked)
                    {
                        m.Linked = true;
                        changed = true;
                    }
                }
            }
        }

        // 3. Unshield nodes adjacent to a powered ENEMY node (those are attackable / capturable).
        foreach (var n in Nodes)
        {
            foreach (var m in n.Neighbors)
            {
                if (m.Linked && m.Captured && (n.Team != m.Team || !n.Captured))
                    n.Shielded = false;
            }
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Attackable / the unshielded flag: whether a node may currently be damaged/captured
    /// by <paramref name="byTeam"/> — it must be unshielded (an enemy-owned powered neighbor exposed it).
    /// </summary>
    public bool IsAttackable(OnsNode node, int byTeam)
        => !node.Shielded && node.Team != byTeam;

    /// <summary>
    /// QC control-point capture: a control point captured by <paramref name="team"/> (0 = neutralized). The
    /// CP must be attackable (unshielded) to flip; capturing it re-propagates power so it can in turn expose
    /// the next link toward the enemy generator.
    /// </summary>
    public void CaptureControlPoint(int controlPointId, int team) => CaptureControlPoint(controlPointId, team, null);

    /// <summary>
    /// QC control-point capture with the capturing player (ons_ControlPoint_Icon: GameRules_scoring_add(toucher,
    /// ONS_CAPS, 1) + SCORE 10). Overload of <see cref="CaptureControlPoint(int,int)"/> that also credits the
    /// scoreboard columns when a non-neutral capture by a real player succeeds. The port collapses the QC
    /// "take" (ONS_TAKES) and "fully captured" (ONS_CAPS) into this single flip, so the capturer is credited
    /// both columns with one SCORE +10 (avoids the double-credit the two separate QC events would otherwise add).
    /// </summary>
    public void CaptureControlPoint(int controlPointId, int team, Player? by)
    {
        OnsNode node = ControlPointNode(controlPointId) ?? AddControlPoint(controlPointId);
        // A CP can only be (re)captured when attackable by the capturing team (or when neutralizing).
        if (team != Teams.None && node.Captured && !IsAttackable(node, team))
            return;
        bool realCapture = team != Teams.None && node.Team != team;
        node.Team = team;
        node.Captured = team != Teams.None;
        UpdateLinks();

        if (realCapture && by is not null && (int)by.Team == team)
        {
            // QC: completing a BUILD credits only ONS_CAPS + team SCORE. ONS_TAKES is awarded for DESTROYING an
            // enemy control-point icon (ons_ControlPoint_Icon_Damage), not for a build-capture — so it is NOT added here.
            AddCol(by, "ONS_CAPS", 1);  // QC GameRules_scoring_add(toucher, ONS_CAPS, 1)
            by.ScoreFrags += 10;        // QC GameRules_scoring_add_team(toucher, SCORE, 10)
        }
    }

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for an ONS player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>The team that owns a control point (0 = neutral/unknown).</summary>
    public int ControlPointOwner(int controlPointId)
        => _cpNodes.TryGetValue(controlPointId, out var n) ? n.Team : Teams.None;

    /// <summary>
    /// Apply damage to a team's generator (QC ons_GeneratorDamage). Damage is ignored while the generator is
    /// shielded (no enemy controls a linked CP). When health reaches 0 it is destroyed, the graph is updated,
    /// and the round ends: the winner is any other team that still has a standing generator. Returns true if
    /// this damage destroyed the generator.
    /// </summary>
    public bool DamageGenerator(int team, float amount) => DamageGenerator(team, amount, null);

    /// <summary>
    /// QC ons_GeneratorDamage with the attacker (overload of <see cref="DamageGenerator(int,float)"/>): when the
    /// damage destroys the generator the attacker is awarded SCORE +100 (QC GameRules_scoring_add(attacker, SCORE,
    /// 100)). Returns true if this damage destroyed the generator.
    /// </summary>
    public bool DamageGenerator(int team, float amount, Player? by)
    {
        if (MatchEnded || amount <= 0f)
            return false;

        OnsNode? node = GeneratorNode(team);
        GeneratorState? gen = node?.Gen ?? GeneratorFor(team);
        if (gen is null || gen.Destroyed)
            return false;

        // QC: a shielded generator ignores damage entirely.
        if (node is not null && node.Shielded)
            return false;

        gen.Health -= amount;
        if (gen.Health < 0f)
            gen.Health = 0f;

        if (gen.Destroyed)
        {
            if (node is not null) { node.Captured = false; node.Linked = false; node.Shielded = false; }
            UpdateLinks();
            if (by is not null && (int)by.Team != team)
                by.ScoreFrags += 100; // QC GameRules_scoring_add(attacker, SCORE, 100): generator kill bonus
            EndRoundAfterGeneratorLoss(team);
            return true;
        }
        return false;
    }

    /// <summary>
    /// QC Onslaught_CheckWinner used as the round handler's <c>canRoundEnd</c> predicate: the round is decided
    /// once <see cref="CheckWinner"/> is non-zero. On the first transition to "decided" this also bumps the
    /// handler's end-delay from 5 → 7, mirroring the <c>round_handler_Init(7, …)</c> that QC fires inside
    /// Onslaught_CheckWinner when a winner is found (sv_onslaught.qc:1236) — so round 1 ends with a 5 s delay
    /// and every later round with 7 s.
    /// </summary>
    private bool CanRoundEndOns()
    {
        bool decided = CheckWinner() != 0;
        if (decided && Handler is not null)
            Handler.EndDelay = 7f; // QC round_handler_Init(7, …) inside Onslaught_CheckWinner on a decided round
        return decided;
    }

    /// <summary>
    /// QC <c>Onslaught_CheckPlayers</c> (round_handler canRoundStart) — <c>return 1</c>: Onslaught always allows
    /// the round to start. Public so the host (GameWorld.EnableRounds) can wire the LIVE round handler off the
    /// real predicate instead of the generic default (matching how CA/FreezeTag are now driven).
    /// </summary>
    public bool CanRoundStartLive() => true;

    /// <summary>
    /// QC <c>Onslaught_CheckWinner</c> (round_handler canRoundEnd): the round is decided once a winner exists.
    /// Public companion to <see cref="CanRoundStartLive"/> so the host can drive the live handler off the real
    /// predicate. Same body as the gametype-owned <see cref="CanRoundEndOns"/> (incl. the 5→7 end-delay bump).
    /// </summary>
    public bool CanRoundEndLive() => CanRoundEndOns();

    /// <summary>
    /// QC Onslaught_CheckWinner (Team_GetWinnerTeam_WithOwnedItems): the round is decided when at most one
    /// team still has a standing generator. Returns that team (&gt;0), -1 for a tie (none left), or 0 while
    /// two or more generators stand. A win banks ST_ONS_GENS +1 for the winner (<see cref="BankRoundWin"/>).
    /// </summary>
    public int CheckWinner()
    {
        int standing = 0, soleTeam = Teams.None;
        foreach (var kv in _generators)
            if (!kv.Value.Destroyed) { standing++; soleTeam = kv.Key; }
        if (_generators.Count < 2 || standing > 1)
            return 0; // round still live (or not enough generators set up yet)
        int winner = standing == 1 ? soleTeam : -1;
        if (winner > 0)
            BankRoundWin(winner); // QC TeamScore_AddToTeam(winner_team, ST_ONS_GENS, +1) (idempotent via MatchEnded)
        return winner;
    }

    /// <summary>The team <paramref name="losingTeam"/>'s generator fell — award the round to a surviving team
    /// (QC Onslaught round-over: TeamScore_AddToTeam(winner_team, ST_ONS_GENS, +1)).</summary>
    private void EndRoundAfterGeneratorLoss(int losingTeam)
    {
        int winner = Teams.None;
        foreach (KeyValuePair<int, GeneratorState> kv in _generators)
        {
            if (kv.Key != losingTeam && !kv.Value.Destroyed)
            {
                winner = kv.Key; // the (last) team with a standing generator wins
                break;
            }
        }
        BankRoundWin(winner);
    }

    /// <summary>
    /// QC the Onslaught round-over credit (sv_onslaught.qc): latch the round winner and bank ST_ONS_GENS +1 for
    /// it (the team slot-1 score = generators destroyed). Idempotent per round via the <see cref="MatchEnded"/>
    /// latch, so the two win paths — a generator destroyed in <see cref="DamageGenerator"/> and the round
    /// handler's <see cref="CheckWinner"/> — credit exactly once. A neutral winner just latches the end.
    /// </summary>
    private void BankRoundWin(int winner)
    {
        if (MatchEnded)
            return;
        MatchEnded = true;
        WinningTeam = winner;
        if (winner != Teams.None)
            GS.AddToTeam(winner, GS.TeamSlotSecondary, 1); // QC TeamScore_AddToTeam(winner_team, ST_ONS_GENS, +1)

        // QC Onslaught_CheckWinner round-decision cues (sv_onslaught.qc:1220-1234): the round-win/tie
        // center+info to all + the win jingle (play2all(CTF_CAPTURE(winner_team))). Emitted once per round
        // via the MatchEnded latch above, exactly like QC fires them at the moment the round is decided.
        if (winner > 0)
        {
            string suffix = TeamSuffix(winner);
            Notify(NotifBroadcast.All, MsgType.Center, $"ROUND_TEAM_WIN_{suffix}");
            Notify(NotifBroadcast.All, MsgType.Info, $"ROUND_TEAM_WIN_{suffix}");
            if (Api.Services is not null)
                SoundSystem.PlayGlobal(Sounds.ByName($"CTF_CAPTURE_{suffix}")); // QC play2all(SND(CTF_CAPTURE(winner_team)))
        }
        else if (winner == -1)
        {
            Notify(NotifBroadcast.All, MsgType.Center, "ROUND_TIED");
            Notify(NotifBroadcast.All, MsgType.Info, "ROUND_TIED");
        }
    }

    /// <summary>QC <c>APP_TEAM_NUM</c> team-code → notification/sound suffix (RED/BLUE/YELLOW/PINK).</summary>
    internal static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "NEUTRAL",
    };

    /// <summary>QC Send_Notification(NOTIF_*, NULL, MSG_*, …) — broadcast a registered notification (server only).</summary>
    internal static void Notify(NotifBroadcast broadcast, MsgType type, string name, params object[] args)
    {
        if (Api.Services is not null)
            NotificationSystem.Send(broadcast, null, type, name, args);
    }

    /// <summary>Player kills don't decide Onslaught (the generators do); the handler just notifies the host.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }

    // ============================================================================================
    //  Spawn placement + teleport (QC ons_Teleport / MUTATOR_HOOKFUNCTION(ons, PlayerSpawn))
    // ============================================================================================

    private const string CvarSpawnChoose      = "g_onslaught_spawn_choose";                  // 1
    private const string CvarTeleportRadius   = "g_onslaught_teleport_radius";               // 200
    private const string CvarTeleportWait     = "g_onslaught_teleport_wait";                 // 5
    private const string CvarSpawnAtCps        = "g_onslaught_spawn_at_controlpoints";        // 0
    private const string CvarSpawnAtCpsChance  = "g_onslaught_spawn_at_controlpoints_chance"; // 0.5
    private const string CvarSpawnAtCpsRandom  = "g_onslaught_spawn_at_controlpoints_random"; // 0
    private const string CvarSpawnAtGen        = "g_onslaught_spawn_at_generator";            // 0
    private const string CvarSpawnAtGenChance  = "g_onslaught_spawn_at_generator_chance";     // 0
    private const string CvarSpawnAtGenRandom  = "g_onslaught_spawn_at_generator_random";     // 0

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ons, PlayerSpawn) (sv_onslaught.qc:1686): when a player (re)spawns, (1) if the
    /// match is in overtime, center-print OVERTIME_CONTROLPOINT; (2) if spawn_choose is on and the player picked
    /// a control point from the click-radar (<see cref="Entity.GtOnsSpawnBy"/>), teleport them near it; (3)
    /// otherwise, if spawn_at_controlpoints / spawn_at_generator are enabled (default OFF), place them near the
    /// nearest same-team node to where they died. The click-radar selection itself (the ons_spawn client command
    /// + the clickradar HUD that SETS GtOnsSpawnBy) is a client concern — see the unresolved note.
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (Api.Services is null || args.Player is not Player player)
            return false;

        // QC: if(ons_stalemate) Send_Notification(NOTIF_ONE, player, MSG_CENTER, CENTER_OVERTIME_CONTROLPOINT).
        if (IsInOvertime())
            NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "OVERTIME_CONTROLPOINT");

        // QC: if(spawn_choose) if(player.ons_spawn_by) if(ons_Teleport(player, ons_spawn_by, teleport_radius, false))
        //         { player.ons_spawn_by = NULL; return; }
        if (Cvar(CvarSpawnChoose, 1f) != 0f && player.GtOnsSpawnBy is { } chosen)
        {
            if (OnsTeleport(player, chosen, Cvar(CvarTeleportRadius, 200f), teleEffects: false))
            {
                player.GtOnsSpawnBy = null;
                return false;
            }
        }

        // QC: if(spawn_at_controlpoints) if(random() <= chance) place near the nearest same-team control point.
        if (Cvar(CvarSpawnAtCps, 0f) != 0f && Prandom.Float() <= Cvar(CvarSpawnAtCpsChance, 0.5f))
            if (SpawnAtNode(player, "onslaught_controlpoint", Cvar(CvarSpawnAtCpsRandom, 0f) != 0f,
                    zOffset: 96f, jitter: 128f, sameTeamOnly: true))
                return false;

        // QC: if(spawn_at_generator) if(random() <= chance) place near the nearest same-team generator.
        if (Cvar(CvarSpawnAtGen, 0f) != 0f && Prandom.Float() <= Cvar(CvarSpawnAtGenChance, 0f))
            if (SpawnAtNode(player, "onslaught_generator", Cvar(CvarSpawnAtGenRandom, 0f) != 0f,
                    zOffset: 128f, jitter: 256f, sameTeamOnly: true))
                return false;

        return false;
    }

    /// <summary>
    /// QC ons_Teleport (sv_onslaught.qc:1603): teleport <paramref name="player"/> to a random clear position within
    /// <paramref name="range"/> of <paramref name="target"/>. Tries up to 16 narrowing iterations: pick a random
    /// angle + distance on the ground disc around the target, tracebox the player's bbox there (must be clear and
    /// not start-solid), then double-check a traceline from the target to the spot (no wall between). On success,
    /// setorigin + face away from the target, arm the teleport antispam, optionally play the teleport FX. Returns
    /// false if no clear spot was found.
    /// </summary>
    public bool OnsTeleport(Player player, Entity target, float range, bool teleEffects)
    {
        if (target is null || Api.Services is null)
            return false;
        Vector3 plMin = SpawnSystem.PlayerMins, plMax = SpawnSystem.PlayerMaxs;
        float iterationScale = 1f;
        for (int i = 0; i < 16; i++)
        {
            // QC: iteration_scale -= i / 16 — narrow the search disc each iteration so a spot is more likely found.
            iterationScale -= i / 16f;
            float theta = Prandom.Float() * (2f * MathF.PI);
            Vector3 loc = new(MathF.Cos(theta), MathF.Sin(theta), 0f);
            loc *= Prandom.Float() * range * iterationScale;
            loc += target.Origin + new Vector3(0f, 0f, 128f) * iterationScale;

            TraceResult box = Api.Trace.Trace(loc, plMin, plMax, loc, MoveFilter.Normal, player);
            if (box.Fraction == 1f && !box.StartSolid)
            {
                TraceResult line = Api.Trace.Trace(target.Origin, Vector3.Zero, Vector3.Zero, loc,
                    MoveFilter.NoMonsters, target);
                if (line.Fraction == 1f && !line.StartSolid)
                {
                    if (teleEffects)
                        SoundSystem.PlayOn(player, Sounds.ByName("TELEPORT"));
                    Api.Entities.SetOrigin(player, loc);
                    // QC: player.angles = '0 1 0' * (theta * RAD2DEG + 180) — face away from the target.
                    Vector3 ang = new(0f, theta * (180f / MathF.PI) + 180f, 0f);
                    player.Angles = ang;
                    player.FixAngle = true;
                    player.FixAngleAngles = ang;
                    // QC: player.teleport_antispam = time + autocvar_g_onslaught_teleport_wait.
                    player.GtTeleportAntispam = Api.Clock.Time + Cvar(CvarTeleportWait, 5f);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// QC the spawn_at_controlpoints / spawn_at_generator placement branches of the ons PlayerSpawn hook
    /// (sv_onslaught.qc:1716-1815): find the nearest same-team node of <paramref name="className"/> to where the
    /// player died (or a random one if <paramref name="random"/>), then trace up to 10 narrowing candidate spots
    /// just above it and setorigin the player at the first clear one. Returns true if the player was placed.
    /// </summary>
    private bool SpawnAtNode(Player player, string className, bool random, float zOffset, float jitter, bool sameTeamOnly)
    {
        if (Api.Services is null)
            return false;
        Vector3 deathLoc = player.DeathOrigin; // QC player.ons_deathloc
        // QC: new joining player or round reset, don't bother checking.
        if (deathLoc == Vector3.Zero)
            return false;

        int myTeam = (int)player.Team;
        Entity? chosen = null;
        float bestDist2 = float.MaxValue;
        var candidates = new List<Entity>();
        foreach (Entity e in Api.Entities.FindByClass(className))
        {
            if (sameTeamOnly && (int)e.Team != myTeam)
                continue;
            if (random)
                candidates.Add(e);
            else
            {
                float d2 = (e.Origin - deathLoc).LengthSquared();
                if (d2 < bestDist2 || chosen is null)
                {
                    bestDist2 = d2;
                    chosen = e;
                }
            }
        }
        if (random && candidates.Count > 0)
            chosen = candidates[Prandom.RangeInt(0, candidates.Count)];
        if (chosen is null)
            return false;

        Vector3 plMin = SpawnSystem.PlayerMins, plMax = SpawnSystem.PlayerMaxs;
        float iterationScale = 1f;
        for (int i = 0; i < 10; i++)
        {
            iterationScale -= i / 10f;
            // QC: loc = target.origin + '0 0 zOffset'*scale + ('0 1 0'*random())*jitter*scale.
            Vector3 loc = chosen.Origin + new Vector3(0f, 0f, zOffset) * iterationScale
                + new Vector3(0f, Prandom.Float(), 0f) * jitter * iterationScale;
            TraceResult box = Api.Trace.Trace(loc, plMin, plMax, loc, MoveFilter.Normal, player);
            if (box.Fraction == 1f && !box.StartSolid)
            {
                TraceResult line = Api.Trace.Trace(chosen.Origin, Vector3.Zero, Vector3.Zero, loc,
                    MoveFilter.NoMonsters, chosen);
                if (line.Fraction == 1f && !line.StartSolid)
                {
                    Api.Entities.SetOrigin(player, loc);
                    // QC: player.angles = normalize(loc - target.origin) * RAD2DEG (face away from the node).
                    Vector3 ang = QMath.VecToAngles(loc - chosen.Origin);
                    player.Angles = ang;
                    player.FixAngle = true;
                    player.FixAngleAngles = ang;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// QC ons_Count_SelfControlPoints (sv_onslaught.qc:1581): how many control points + generators the player's
    /// team currently owns (a captured CP, or any same-team generator). Base uses this to decide whether to offer
    /// the click-radar respawn picker on death (it offers it only when the team owns more than one node).
    /// </summary>
    public int CountSelfControlPoints(int team)
    {
        int n = 0;
        foreach (OnsNode node in Nodes)
        {
            if (node.IsGenerator)
            {
                if (node.Team == team) ++n; // QC: every same-team generator counts
            }
            else if (node.Captured && node.Team == team)
                ++n; // QC: a same-team CAPTURED control point counts
        }
        return n;
    }

    private const string CvarClickRadius = "g_onslaught_click_radius"; // 500

    /// <summary>
    /// QC ons_Nearest_ControlPoint (sv_onslaught.qc:1513): the nearest same-team node — a CAPTURED control point or
    /// a generator — to <paramref name="pos"/>, within <paramref name="maxDist"/> (≤0 = unbounded). Returns the map
    /// entity (onslaught_controlpoint / onslaught_generator) so the caller can teleport to its origin.
    /// </summary>
    public Entity? NearestControlPoint(Player player, Vector3 pos, float maxDist)
    {
        if (Api.Services is null) return null;
        int myTeam = (int)player.Team;
        Entity? closest = null;
        float maxDist2 = maxDist > 0f ? maxDist * maxDist : 0f;
        foreach (Entity cp in Api.Entities.FindByClass("onslaught_controlpoint"))
        {
            if (cp.IsFreed || (int)cp.Team != myTeam) continue;      // QC SAME_TEAM(cp, this)
            OnsNode? node = ControlPointNode(cp.GtPointId);
            if (node is null || !node.Captured) continue;            // QC cp.iscaptured
            float d2 = (cp.Origin - pos).LengthSquared();
            if (maxDist > 0f && d2 > maxDist2) continue;
            if (closest is null || d2 <= (closest.Origin - pos).LengthSquared())
                closest = cp;
        }
        foreach (Entity gen in Api.Entities.FindByClass("onslaught_generator"))
        {
            if (gen.IsFreed) continue;
            int genTeam = gen.GtHomeTeam != 0 ? gen.GtHomeTeam : (int)gen.Team;
            if (genTeam != myTeam) continue;                         // QC SAME_TEAM(gen, this)
            float d2 = (gen.Origin - pos).LengthSquared();
            if (maxDist > 0f && d2 > maxDist2) continue;
            if (closest is null || d2 <= (closest.Origin - pos).LengthSquared())
                closest = gen;
        }
        return closest;
    }

    /// <summary>
    /// QC the <c>g_radarlinks</c> set + <c>draw_teamradar_link</c> (client/teamradar.qc): collect every powered
    /// power-graph LINK as a renderable segment for the team radar — the two endpoint world origins plus each
    /// end's owning team color code. Mirrors the QC <c>ENT_CLIENT_RADARLINK</c> entities the server spawns per
    /// link (one per <c>onslaught_link</c>): the line goes between the two linked nodes, colored at each end by
    /// that node's team (a captured/owned node shows its team color; a neutral/unowned node is team 0 = white).
    /// Each undirected neighbor edge is emitted exactly once (a&lt;b id guard). Endpoints are resolved from the
    /// live world entities (onslaught_generator / onslaught_controlpoint origins), matching the existing
    /// entity-walk pattern in <see cref="NearestControlPoint"/>. Cleared + refilled into <paramref name="into"/>.
    /// </summary>
    public void CollectRadarLinks(System.Collections.Generic.List<RadarLinkSegment> into)
    {
        into.Clear();
        if (Api.Services is null || Nodes.Count == 0)
            return;

        // Resolve each graph node to its live world origin + display team. Generators key by team; control points
        // key by their stable point id (the node Id). A node with no spawned world entity (headless / not yet
        // built) is skipped — a link is only drawn when BOTH endpoints have a position, exactly like QC where the
        // RADARLINK entity carries both origins.
        Vector3 GenOrigin(int team)
        {
            foreach (Entity gen in Api.Entities.FindByClass("onslaught_generator"))
            {
                if (gen.IsFreed) continue;
                int t = gen.GtHomeTeam != 0 ? gen.GtHomeTeam : (int)gen.Team;
                if (t == team) return gen.Origin;
            }
            return new Vector3(float.NaN, float.NaN, float.NaN);
        }
        Vector3 CpOrigin(int id)
        {
            foreach (Entity cp in Api.Entities.FindByClass("onslaught_controlpoint"))
                if (!cp.IsFreed && cp.GtPointId == id) return cp.Origin;
            return new Vector3(float.NaN, float.NaN, float.NaN);
        }
        Vector3 NodeOrigin(OnsNode n) => n.IsGenerator ? GenOrigin(n.Team) : CpOrigin(n.Id);
        // QC link end color: the node's team if it is captured/owned, else neutral (0 = white). A generator is
        // always owned by its team; a control point shows its team only once captured.
        int NodeTeam(OnsNode n) => (n.IsGenerator || n.Captured) ? n.Team : Teams.None;

        foreach (OnsNode a in Nodes)
        {
            Vector3 oa = NodeOrigin(a);
            if (float.IsNaN(oa.X)) continue;
            int ta = NodeTeam(a);
            foreach (OnsNode b in a.Neighbors)
            {
                // emit each undirected edge once: order generators-before-cps then by id so the pair is stable.
                if (LinkKey(a) >= LinkKey(b))
                    continue;
                Vector3 ob = NodeOrigin(b);
                if (float.IsNaN(ob.X)) continue;
                into.Add(new RadarLinkSegment(oa, ta, ob, NodeTeam(b)));
            }
        }
    }

    /// <summary>Stable ordering key for the undirected-edge dedupe in <see cref="CollectRadarLinks"/>: generators
    /// (negated team, always &lt; any cp id) sort before control points (their point id). Distinct per node.</summary>
    private static int LinkKey(OnsNode n) => n.IsGenerator ? -1000 - n.Team : n.Id;

    /// <summary>One Onslaught radar link segment (QC ENT_CLIENT_RADARLINK / draw_teamradar_link): the two
    /// endpoint world origins plus each end's team color code (0 = neutral/white). Networked by ServerNet.</summary>
    public readonly record struct RadarLinkSegment(Vector3 A, int TeamA, Vector3 B, int TeamB);

    /// <summary>
    /// QC ons_Nearest_ControlPoint_2D (sv_onslaught.qc:1540): like <see cref="NearestControlPoint"/> but measures
    /// distance on the XY plane only (Z disregarded) — used for the click-radar pick, where the click is a 2D map
    /// position. Returns the nearest same-team captured CP / generator entity within <paramref name="maxDist"/>.
    /// </summary>
    public Entity? NearestControlPoint2D(Player player, Vector3 pos, float maxDist)
    {
        if (Api.Services is null) return null;
        int myTeam = (int)player.Team;
        Entity? closest = null;
        float smallest = 0f;
        foreach (Entity cp in Api.Entities.FindByClass("onslaught_controlpoint"))
        {
            if (cp.IsFreed || (int)cp.Team != myTeam) continue;      // QC SAME_TEAM(cp, this)
            OnsNode? node = ControlPointNode(cp.GtPointId);
            if (node is null || !node.Captured) continue;            // QC cp.iscaptured
            Vector3 delta = cp.Origin - pos; delta.Z = 0f;
            float dist = delta.Length();
            if (maxDist > 0f && dist > maxDist) continue;
            if (closest is null || dist <= smallest) { closest = cp; smallest = dist; }
        }
        foreach (Entity gen in Api.Entities.FindByClass("onslaught_generator"))
        {
            if (gen.IsFreed) continue;
            int genTeam = gen.GtHomeTeam != 0 ? gen.GtHomeTeam : (int)gen.Team;
            if (genTeam != myTeam) continue;                         // QC SAME_TEAM(gen, this)
            Vector3 delta = gen.Origin - pos; delta.Z = 0f;
            float dist = delta.Length();
            if (maxDist > 0f && dist > maxDist) continue;
            if (closest is null || dist <= smallest) { closest = gen; smallest = dist; }
        }
        return closest;
    }

    /// <summary>
    /// QC the <c>ons_spawn</c> client command (MUTATOR_HOOKFUNCTION(ons, SV_ParseClientCommand),
    /// sv_onslaught.qc:1937): a player picks a control point from the click-radar (a 2D map position
    /// <paramref name="pos"/>) and either teleports to it (if alive, between own CPs within teleport_radius) or
    /// queues a respawn AT it (if dead — sets <see cref="Entity.GtOnsSpawnBy"/> + forces respawn so the
    /// <see cref="OnPlayerSpawn"/> hook teleports them there). Returns a player-facing message (or null on a
    /// successful silent action). This is the server side of the click-radar feature — the HUD that generates
    /// the clicked <paramref name="pos"/> is a CSQC/client concern (see the spec's deferred note).
    /// </summary>
    public string? HandleOnsSpawnCommand(Player player, Vector3 pos)
    {
        if (Api.Services is null)
            return null;

        // QC: source_point = ons_Nearest_ControlPoint(player, player.origin, teleport_radius).
        Entity? sourcePoint = NearestControlPoint(player, player.Origin, Cvar(CvarTeleportRadius, 200f));

        // QC: if(!source_point && GetResource(player, RES_HEALTH) > 0) "You need to be next to a control point".
        if (sourcePoint is null && !player.IsDead)
            return "\nYou need to be next to a control point\n";

        // QC: closest_target = ons_Nearest_ControlPoint_2D(player, pos, click_radius).
        Entity? closestTarget = NearestControlPoint2D(player, pos, Cvar(CvarClickRadius, 500f));
        if (closestTarget is null)
            return "\nNo control point found\n";

        if (player.IsDead)
        {
            // QC: player.ons_spawn_by = closest_target; player.respawn_flags |= RESPAWN_FORCE.
            player.GtOnsSpawnBy = closestTarget;
            player.RespawnFlags |= RespawnFlag.Force;
            return null;
        }

        // Alive: teleport between own control points.
        if (ReferenceEquals(sourcePoint, closestTarget))
            return "\nTeleporting to the same point\n"; // QC: same source/target

        // QC: if(!ons_Teleport(player, closest_target, teleport_radius, true)) "Unable to teleport there".
        if (!OnsTeleport(player, closestTarget, Cvar(CvarTeleportRadius, 200f), teleEffects: true))
            return "\nUnable to teleport there\n";
        return null;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(ons, PlayerUseKey) (sv_onslaught.qc:1990): the +use key, while alive, not in a
    /// vehicle, and past the teleport antispam, opens the click-radar IF the player is standing next to one of
    /// their own control points. Returns true if the press is consumed (a source point was found). The HUD that
    /// the QC then stuffcmds (<c>qc_cmd_cl hud clickradar</c>) is client-side; the server-side gate is faithful.
    /// </summary>
    public bool HandleUseKey(Player player)
    {
        if (Api.Services is null || MatchEnded)
            return false;
        // QC: (time > player.teleport_antispam) && (!IS_DEAD(player)) && !player.vehicle.
        if (Api.Clock.Time <= player.GtTeleportAntispam || player.IsDead || player.Vehicle is not null)
            return false;
        Entity? source = NearestControlPoint(player, player.Origin, Cvar(CvarTeleportRadius, 200f));
        return source is not null; // QC: only consumes the press (and opens the radar) when next to an own CP
    }

    /// <summary>
    /// QC the Onslaught control-point + generator waypoint sprites (sv_onslaught.qc: ons_GeneratorSetup spawns
    /// WP_OnsGenShielded/WP_OnsGen, ons_ControlPoint_Setup + ons_ControlPoint_UpdateSprite resolve
    /// WP_OnsCP / WP_OnsCPDefend / WP_OnsCPAttack per the captured/linked/shielded state). One fixed marker per
    /// generator and per control point at its world origin, rebuilt each tick from the live power graph like CTF's
    /// flag sprites. Generators: shielded (no enemy controls a linked CP) shows WP_OnsGenShielded, exposed shows
    /// WP_OnsGen — both shown to everyone, tinted by the owning team. Control points: a neutral CP shows the plain
    /// WP_OnsCP to all; a captured CP uses the SPRITERULE_TEAMPLAY three-image swap (own team sees WP_OnsCPDefend
    /// with the per-def 0.5 blink, an enemy who can attack it sees WP_OnsCPAttack with the per-def 2.0 blink, and a
    /// spectator/observer sees the plain WP_OnsCP) — resolved per peer by the net layer's SpriteFor() like the
    /// KeyHunt/TeamKeepaway carriers. Reached via ServerNet.SendWaypoints (GameType.CollectWaypoints) per tick.
    /// </summary>
    public override void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into)
    {
        if (Api.Services is null)
            return;

        // ----- generators (WP_OnsGen / WP_OnsGenShielded), shown to everyone, tinted by the owning team -----
        foreach (Entity gen in Api.Entities.FindByClass("onslaught_generator"))
        {
            if (gen.IsFreed)
                continue;
            int team = gen.GtHomeTeam != 0 ? gen.GtHomeTeam : (int)gen.Team;
            OnsNode? node = GeneratorNode(team);
            if (node is { Gen.Destroyed: true })
                continue; // QC: a destroyed generator no longer carries a waypoint
            bool shielded = node is null || node.Shielded;
            into.Add(new Waypoints.WaypointSprite
            {
                // QC ons_GeneratorSetup: WaypointSprite_SpawnFixed(this.origin, WP_OnsGen, RADARICON_NONE, …) with
                // the shielded/exposed sprite swapped by ons_ShowGeneratorSprite — shown to all, team-tinted radar.
                SpriteName = shielded ? "OnsGenShielded" : "OnsGen",
                FixedOrigin = gen.Origin,
                Team = 0, // shown to everyone (the owning team lives in Color, not the visibility team)
                Color = team != Teams.None ? Teams.ColorRgb(team) : new Vector3(1f, 1f, 1f),
                RadarIcon = 1, // RADARICON_GENERATOR
                Health = -1f,
            });
        }

        // ----- control points (WP_OnsCP / WP_OnsCPDefend / WP_OnsCPAttack via the teamplay three-image swap) -----
        foreach (Entity cp in Api.Entities.FindByClass("onslaught_controlpoint"))
        {
            if (cp.IsFreed)
                continue;
            OnsNode? node = ControlPointNode(cp.GtPointId);
            int owner = node?.Captured == true ? node.Team : Teams.None;
            var wp = new Waypoints.WaypointSprite
            {
                FixedOrigin = cp.Origin,
                RadarIcon = 1, // RADARICON_CONTROLPOINT
                Health = -1f,
                Color = owner != Teams.None ? Teams.ColorRgb(owner) : new Vector3(1f, 0.5f, 0f), // WP_*color (orange neutral)
            };
            if (owner == Teams.None)
            {
                // QC: a neutral / uncaptured control point shows the plain WP_OnsCP to everyone.
                wp.SpriteName = "OnsCP";
                wp.Team = 0;
                wp.Rule = Waypoints.SpriteRule.Default;
            }
            else
            {
                // QC ons_ControlPoint_UpdateSprite: a captured CP networks the three-image teamplay triple —
                // model1 (enemy) = WP_OnsCPAttack, model2 (own team) = WP_OnsCPDefend, model3 (spectator) = WP_OnsCP.
                // The net layer's SpriteFor() picks the right one per peer (SPRITERULE_TEAMPLAY shows to all).
                wp.SpriteName = "OnsCPAttack";        // enemy image (per-def blink 2.0)
                wp.SpriteNameOwn = "OnsCPDefend";     // own-team image (per-def blink 0.5)
                wp.SpriteNameSpec = "OnsCP";          // spectator image
                wp.Team = owner;
                wp.Rule = Waypoints.SpriteRule.Teamplay;
            }
            into.Add(wp);
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

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;
}
