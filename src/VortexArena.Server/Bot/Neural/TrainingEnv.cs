using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// One reinforcement-learning environment: a headless <see cref="GameWorld"/>, a roster of real bots in it,
/// and the episode machinery around them.
///
/// <para><b>It drives real bots through the real server loop.</b> Not a reduced-order movement model, not a
/// hand-rolled tick loop: <see cref="GameWorld.Frame"/>, which means triggers fire, jump pads launch,
/// teleporters relocate, splash damage knocks back, and the observation comes out of the same
/// <see cref="NeuralObservation"/> builder the live server uses. Anything the policy learns to exploit here
/// exists in a match, and anything it learns to fear does too. This is affordable because the simulation is
/// Godot-free and runs at 23x real time per core with eight agents in it
/// (<c>planning/neural-bots-2026-08-07.md</c> section 2.2).</para>
///
/// <para><b>The network stays in Python.</b> The trainer samples an action and sends it; the locomotor's
/// <see cref="NeuralLocomotor.UseExternalAction"/> path builds the observation and applies that action
/// without evaluating any weights here. Keeping one copy of the policy is what stops a run training against
/// something other than the thing it exports.</para>
///
/// <para>Not thread-safe: <see cref="Api"/> is process-ambient, so one env owns it while it steps.
/// Parallelism comes from several env host PROCESSES, which is also how the trainer scales past one core.</para>
/// </summary>
public sealed class TrainingEnv
{
    /// <summary>Config the trainer sends at handshake.</summary>
    public sealed class Config
    {
        /// <summary>Agents in this one world. Batching amortises the fixed per-tick cost by about 1.7x.</summary>
        public int Agents = 8;

        /// <summary>Sim ticks per policy action. 4 ticks at 72 Hz is an 18 Hz decision rate.</summary>
        public int TicksPerStep = 4;

        /// <summary>The episode cap every stage trains at: 900 policy steps at 18 Hz is 50 s of game time.</summary>
        public const int DefaultMaxSteps = 900;

        /// <summary>Episode cap in policy steps. 900 at 18 Hz is 50 s of game time.</summary>
        public int MaxSteps = DefaultMaxSteps;

        /// <summary>Emit a <c>[stuck]</c> line characterising where each timed-out agent stopped. Diagnostic.</summary>
        public bool StuckReport;

        /// <summary>Run the course acceptance tests. Off restores the pre-filter course pool, for A/B.</summary>
        public bool CourseFilters = true;

        /// <summary>Curriculum stage, which picks the course generator.</summary>
        public CourseGenerator.Stage Stage = CourseGenerator.Stage.Flat;

        /// <summary>Base seed. Episode N uses a derived seed, so a run reproduces exactly.</summary>
        public int Seed = 1;

        /// <summary>Probability an episode grants the movement weapons at all.</summary>
        public float WeaponChance = 1f;

        /// <summary>
        /// Probability an episode revokes the weapon permit partway through. Without this the policy only
        /// ever sees one setting of the flag and treats the other as out of distribution, which is exactly
        /// the case that matters: combat claiming the weapon mid-route.
        /// </summary>
        public float PermitFlipChance = 0.35f;

        /// <summary>
        /// Probability an episode imposes a synthetic aim constraint. Cheaper than staging a real enemy and
        /// it exercises the same machinery: a required angle, a weight, and a reward penalty for missing it.
        /// </summary>
        public float AimConstraintChance = 0.4f;

        /// <summary>Spend the per-think trace fan. Off is faster; on is what the live server does.</summary>
        public bool TraceFan = true;

        /// <summary>Content root for <see cref="CourseGenerator.Stage.RealMaps"/>. Ignored by every other stage.</summary>
        public string DataRoot = "";

        /// <summary>Comma-separated map names for stage 6, or empty for "every installed map".</summary>
        public string MapList = "";
    }

    private readonly Config _cfg;
    private readonly Random _rng;

    private GameWorld _world = null!;
    private NavField _field = null!;
    private MapFeatures _features = null!;
    private NavDistanceField _distance = null!;
    private CourseGenerator.Course _course = null!;
    private MapCourseSource? _mapSource;

    /// <summary>Course-filter accept/reject tallies, or null before any real-map episode has been drawn.</summary>
    public string? CourseFilterStats => _mapSource?.FilterStats();
    private MapCourseSource.PreparedMap? _currentMap;
    private PolicyNetwork _dummyNet = null!;
    private NeuralBotService _service = null!;

    /// <summary>
    /// A trained policy to evaluate INSIDE the environment, instead of taking actions from the trainer.
    ///
    /// <para>Training keeps the network in Python, so the env normally runs on external actions. Set this
    /// and the locomotors evaluate the weight file themselves: the same code the live server runs, scored
    /// against the same courses the curriculum trains on. That answers "is this checkpoint any good on
    /// stage 4" without a Python process, and it is the only way to score a policy on the curriculum at all
    /// — the time trial measures real maps, which is a different question.</para>
    /// </summary>
    private PolicyNetwork? _evalPolicy;

    /// <summary>
    /// Run <paramref name="policy"/> inside the environment rather than accepting external actions. Pass
    /// null to go back to external actions. Takes effect at the next <see cref="Reset"/>.
    /// </summary>
    public void SetEvalPolicy(PolicyNetwork? policy) => _evalPolicy = policy;

    /// <summary>True when the env is scoring its own policy and the trainer's actions are ignored.</summary>
    public bool IsEvaluating => _evalPolicy is not null;

    private readonly List<Agent> _agents = new();
    private int _episodeIndex;

    private sealed class Agent
    {
        public BotBrain Brain = null!;
        public Player Player = null!;
        public NeuralLocomotor Loco = null!;
        public int Step;
        public float PrevPotential;
        public float PrevHealth;
        public bool Done;
        public bool Arrived;
        public float ArrivalTime;

        /// <summary>
        /// Geodesic distance from the spawn to the target, measured once at episode start.
        ///
        /// <para>Diagnostic only -- nothing in the reward reads it. It exists so an eval can report arrival
        /// rate as a function of how long the course actually was, which is the difference between "the
        /// policy cannot navigate" and "the policy is being handed courses nothing could finish".</para>
        /// </summary>
        public float StartPotential;

        /// <summary>How the episode ended: 0 running, 1 arrived, 2 died, 3 fell out of the world, 4 timed out.</summary>
        public int Outcome;
        public Vector3 Target;

        /// <summary>
        /// Closest geodesic approach to the target this episode, and the step it happened on.
        ///
        /// <para>Diagnostic only. The gap between this and the final distance separates "never got near the
        /// target" from "got near and then wedged", and <see cref="BestStep"/> against the episode cap says
        /// how long it stayed wedged -- which is what distinguishes a slow policy from a blocked one.</para>
        /// </summary>
        public float BestPotential = float.MaxValue;
        public int BestStep;

        /// <summary>
        /// A position sample refreshed every <see cref="StuckAnchorPeriod"/> steps, so a timeout can report
        /// how far the agent moved recently.
        ///
        /// <para>This is what separates the two failures that look identical in the distance trace: an agent
        /// physically wedged against geometry moves ~0 qu, while one circling in open ground moves hundreds
        /// without ever closing on the goal. They need opposite fixes, so the report has to tell them
        /// apart.</para>
        /// </summary>
        public Vector3 RecentAnchor;
        public int RecentAnchorStep;

        /// <summary>
        /// This agent's own goal-distance field, so every agent on a host can run a DIFFERENT route.
        ///
        /// <para>Geometry is per HOST -- one GameWorld, one map -- but a route is just a spawn/target pair
        /// and a Dijkstra flood, which is a few milliseconds and a float per navigation cell. Sharing one
        /// route across all sixteen agents made route diversity scale with host count when it did not have
        /// to: 20 hosts x 16 agents is 320 distinct routes, not 20.</para>
        /// </summary>
        public NavDistanceField Distance = null!;
        public bool WeaponPermit;
        public int PermitFlipStep;
        public bool AimConstraint;
        public Vector3 AimTarget;
        public float AimWeight;

        /// <summary>
        /// Corridor look-ahead, refreshed once per env step by <see cref="RefreshCorridor"/>.
        ///
        /// <para>These come from walking the distance field, which costs up to eight directions by ten
        /// outward probes per lattice step. BuildIntent is called three times per env step -- once from the
        /// brain's IntentOverride on every think, twice more from the observation builds -- so computing
        /// them inline ran the walk six times for one answer, and cost stage-1 throughput a factor of
        /// nearly four (70,589 agent-steps/s down to 18,969 on the same box).</para>
        /// </summary>
        public Vector3 CorridorNear;
        public Vector3 CorridorFar;
    }

    public TrainingEnv(Config cfg)
    {
        _cfg = cfg;
        _rng = new Random(cfg.Seed);
    }

    /// <summary>Observation length, sent to the trainer at handshake.</summary>
    public static int ObservationSize => NeuralObservation.Size;

    /// <summary>Agents in this env.</summary>
    public int AgentCount => _cfg.Agents;

    /// <summary>Sim seconds elapsed in the current episode.</summary>
    public float Time => _world?.Time ?? 0f;

    /// <summary>Episodes completed since construction, for the throughput report.</summary>
    public int EpisodeCount => _episodeIndex;

    // =============================================================================================
    // episode lifecycle
    // =============================================================================================

    /// <summary>Build a fresh course, spawn the roster, and write the opening observations.</summary>
    public void Reset(Span<float> observations)
    {
        _episodeIndex++;
        // Stage 5 is the only generated stage left. Everything else runs on shipped maps.
        //
        // The generated curriculum taught locomotion and did not transfer: 71% on generated terrain against
        // 12.5% on real arenas for the same policy. The generator's own note says why -- stairwells, tight
        // doorways, railings and multi-level loops are not in it -- so the early stages were teaching a
        // world the bot would never see, and stage 6 was where it met the real one all at once.
        //
        // Stage 5 stays generated because it is the only way to GUARANTEE the skill it teaches. It builds a
        // 560 qu gap against a ~320 qu running jump, and a 250 qu ledge against a ~105 qu jump apex, so a
        // rocket or blaster jump is the only way through. A real map's route almost always has a walkable
        // path, so a bot trained only on real maps is never forced to weapon-jump and would likely never
        // find it.
        //
        // Without a data root there are no maps to draw from, so the stage falls back to generated
        // geometry. That path exists for tests and for anyone running the env without a content install;
        // a training run always sets DataRoot, and a data root that exists but holds no usable map fails
        // loudly in ResetOnRealMap rather than quietly generating something instead.
        if (StageUsesRealMaps(_cfg.Stage) && _cfg.DataRoot.Length > 0) { ResetOnRealMap(observations); return; }
        _currentMap = null;
        _course = CourseGenerator.Generate(_cfg.Stage, _cfg.Seed * 7919 + _episodeIndex);

        // bot_neural OFF across Boot, then on again below.
        //
        // Boot reads the cvar and, when it is set, builds its own NeuralBotService and kicks off a
        // BACKGROUND parallel bake of the map. That is right for a server and wrong here: the cvar is still
        // 1 from the previous episode, so from the second Reset onward every episode spawned a detached
        // bake against a course that was already being discarded. A few hundred episodes in, the host died
        // and the trainer saw only "connection forcibly closed".
        Cvars.Set("bot_neural", "0");

        // Detach the OUTGOING world before building the next one.
        //
        // GameWorld.Shutdown removes its OnServerCvarChanged handler from the cvar store, and that store is
        // process-wide -- every episode's world subscribes to the same one. Without this, every world ever
        // built stays reachable from the store's event list and is never collected. Its own doc comment
        // says so ("a map change builds a fresh world on the same store, so it must be detached or every
        // retired world keeps re-deriving balance on every cvar change"); the listen-server host calls it
        // and this env did not.
        //
        // Measured: about 1.6 MB retained per episode, a managed heap climbing straight through gen2
        // collections, and roughly 3.4 MB/s of RSS growth in a flat-out bench. It is the cause of every
        // out-of-memory kill in this training setup -- at 16 GB, at 24 GB, and at every host count from 14
        // to 24. Host count only ever changed how long a run survived it.
        RetireWorld();

        _world = new GameWorld(_course.World, BuildEntityDicts()) { MapName = $"nbcourse{_episodeIndex}" };
        _world.Boot("dm");
        ApplyTrainingCvars();

        // Bake the field for this course. Courses are a few thousand columns, so a single-threaded bake is
        // milliseconds; doing it inline keeps the env deterministic, with no background work racing a step.
        ulong hash = NavFieldIo.GeometryHash(_course.World);
        _field = NavFieldBaker.Bake(_course.World, _world.MapName!, hash, _world.Services.EntityTable.All);
        _features = new MapFeatures();
        _features.Build(_world.Services.EntityTable.All);
        _distance = NavDistanceField.Build(_field, _course.Target);

        // The locomotor's constructor wants a network even though UseExternalAction means it never runs
        // one. An untrained net of the right shape is the cheapest way to satisfy that without a nullable
        // field on the runtime path.
        _dummyNet ??= PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size);
        _service = NeuralBotService.ForPreparedMap(_evalPolicy ?? _dummyNet, _field, _features, _world.MapName!);

        // Hand the service to the population and turn the feature on, so BotPopulation's per-frame sync
        // creates and owns the locomotors. The env then borrows them and switches them to external-action
        // mode.
        //
        // The env used to construct its own locomotors and assign brain.Locomotor directly. That worked
        // only while the sync happened to early-out; once the sync was corrected to handle bots joining
        // mid-match, it saw bot_neural off and cleared the env's locomotors every single frame. The bots
        // silently fell back to the classic steer, fought each other with the movement weapons they had
        // been granted, and died in about twelve steps — 3,364 episodes in a 40,000-step bench, mean reward
        // -0.52, throughput down from 4,200 steps/s to 174. One owner for the locomotors, not two.
        _world.Bots.Neural = _service;
        Cvars.Set("bot_neural", "1");

        SpawnRosterAndSettle(observations);
    }

    /// <summary>The cvar state every training episode runs under, shared by both reset paths.</summary>
    /// <summary>
    /// Cvars to apply after the training defaults, for the host's <c>--cvar</c> option.
    ///
    /// <para>This exists because <see cref="Cvars.Set"/> is a silent no-op until <c>Api.Services</c> is
    /// built, which is long after command-line parsing. Setting a cvar from an option handler therefore
    /// does nothing and <see cref="Cvars.FloatOr"/> later returns the fallback -- an A/B run that way
    /// compares a configuration against itself and reads exactly like a rejected hypothesis.</para>
    /// </summary>
    public static readonly Dictionary<string, string> ExtraCvars = new();

    private void ApplyTrainingCvars()
    {
        Cvars.Set("bot_join_empty", "1");
        // bot_number must MATCH the roster this env creates by hand.
        //
        // It was 0, on the reasoning that the roster is built directly so fixcount has nothing to do. What
        // fixcount actually does with a target of 0 and eight connected bots is remove them, one per frame,
        // and it starts doing that during the warm-up frames. Every agent's Player was freed a few ticks
        // after spawning; the env kept stepping stale references, so position and velocity froze at
        // whatever they held at disconnect and nothing ever moved again. The scripted hold-forward probe
        // read as "closes 24 qu/s", which looks like bad movement and was actually no movement.
        Cvars.Set("bot_number", _cfg.Agents.ToString(CultureInfo.InvariantCulture));
        Cvars.Set("skill", "10");
        Cvars.Set("g_balance_selfdamagepercent", "0.65");
        Cvars.Set("bot_nofire", "0");
        // Agents share a world for throughput, not to fight. Without this they acquire each other as
        // enemies, combat claims the weapon (so the policy never gets to weapon-jump), and they shoot each
        // other down in seconds — which is a deathmatch, not a locomotion curriculum.
        Cvars.Set("bot_ignore_bots", "1");
        // One think per env step, exactly. The policy's decision rate is part of what it learns, so the
        // trainer's step rate and the runtime's bot_neural_hz have to be the same number.
        Cvars.Set("bot_neural_hz", (72f / _cfg.TicksPerStep).ToString(CultureInfo.InvariantCulture));

        // Last, so an explicit --cvar wins over the training defaults above.
        foreach (KeyValuePair<string, string> kv in ExtraCvars)
            Cvars.Set(kv.Key, kv.Value);
    }

    /// <summary>
    /// Stage 6's reset: draw a map and an A/B pair from <see cref="MapCourseSource"/>, then build the world
    /// around it. The map's collision, entity lump and navigation field are prepared once and reused; only
    /// the goal-relative distance field is rebuilt, which is a few milliseconds.
    /// </summary>
    /// <summary>Every stage but <see cref="CourseGenerator.Stage.WeaponGaps"/> draws from shipped maps.</summary>
    public static bool StageUsesRealMaps(CourseGenerator.Stage stage) =>
        stage != CourseGenerator.Stage.WeaponGaps;

    /// <summary>
    /// Difficulty ramp for the real-map stages: which maps, and how long a route.
    ///
    /// <para>Route length is the ramp, because it is what arrival rate actually tracks. Measured on stage 6
    /// with a scripted straight-line runner: 60.9% under 1000 qu, 38.9% to 1500, 20.5% to 2500, 8.0% to
    /// 4000, 2.3% beyond. A short route on a complex map is a genuinely easier problem than a long one, and
    /// it is the same geometry the policy has to end up handling.</para>
    ///
    /// <para>Every stage now uses the WHOLE map pool. Stages 1 and 2 were restricted to four simple
    /// arenas, and it failed exactly as the note here predicted: 24 worlds held 24 routes across only 4
    /// distinct geometries, and stage 1 plateaued at 52-56% shipped against an 85% gate -- below the 59.9%
    /// a scripted straight-line runner scores on the same band -- for 20.7M steps. Four layouts is fewer
    /// than the six that scored 46.3% in the diversity A/B, against 24 that scored 95.0%.</para>
    ///
    /// <para><b>Route length is no longer the only ramp, because it was not carrying the one the stage names.</b>
    /// Stage 2 is defined as "flat, but long -- learns bunnyhop and strafe-jump chaining", with jump timing
    /// deferred to stage 3. With length as the only filter it was serving stairwells and ledges instead: of
    /// 396 stage-2 timeouts, 31.6% were pinned at the foot of a step up taller than a jump clears, on a stage
    /// that hands out no weapons and follows nothing that ever taught jumping. MaxStepUp restores the stated
    /// ordering by filtering on the vertical profile of the route the bot is actually pointed down.</para>
    ///
    /// <para>The numbers are the movement envelope, not round figures: a step is 18 qu, a standing jump apex
    /// is about 105, and a running jump clears about 320 of gap. So stage 1 and 2 stay under a standing jump,
    /// stage 3 opens up to the full jump, and stages 4 and up take whatever the map has.</para>
    /// </summary>
    public static (string[] Maps, float MinRoute, float MaxRoute, float MaxStepUp) StageProfile(
        CourseGenerator.Stage stage)
    {
        return stage switch
        {
            CourseGenerator.Stage.Flat      => (Array.Empty<string>(), 700f, 1200f, 64f),
            CourseGenerator.Stage.Corridor  => (Array.Empty<string>(), 1200f, 2500f, 64f),
            CourseGenerator.Stage.Terrain   => (Array.Empty<string>(), 700f, 1500f, 112f),
            CourseGenerator.Stage.Furniture => (Array.Empty<string>(), 1500f, 3000f, float.PositiveInfinity),
            _                               => (Array.Empty<string>(), 700f, float.PositiveInfinity,
                                                float.PositiveInfinity),
        };
    }

    private void ResetOnRealMap(Span<float> observations)
    {
        _mapSource ??= new MapCourseSource(_cfg.DataRoot, _cfg.MapList)
        {
            Log = Log,
            FiltersEnabled = _cfg.CourseFilters,
        };

        (string[] maps, float minRoute, float maxRoute, float maxStepUp) = StageProfile(_cfg.Stage);
        // An explicit --maps list from the caller wins over the stage's own subset.
        if (_cfg.MapList.Length == 0) _mapSource.Only = maps;

        var draw = _mapSource.NextEpisode(_rng, minRoute, maxRoute, maxStepUp);
        if (draw is null)
            throw new InvalidOperationException(
                "no map in the pool produced a reachable A/B pair — check the map list and the held-out set");

        (MapCourseSource.PreparedMap map, Vector3 spawn, Vector3 target, NavDistanceField dist) = draw.Value;
        _currentMap = map;

        // A spawn point exactly at the episode origin, appended to the map's own entity lump so the roster
        // starts where the route does.
        var dicts = new List<EntityDict>(map.Entities) { new("info_player_deathmatch", spawn) };

        Cvars.Set("bot_neural", "0");   // see the note in the generated-course reset

        RetireWorld();                  // see RetireWorld: detaches everything this env attached

        _world = new GameWorld(map.World, dicts) { MapName = map.Name };
        _world.BrushModels = map.Submodels;
        _world.MapBsp = map.Bsp;
        // Wire the content reader so the bot population loads the map's SHIPPED waypoint file.
        //
        // Without it, WaypointNetwork.ForMap falls back to GenerateFromEntities(autoLink: true), an O(N^2)
        // tracewalk over every item and spawn point. A fresh GameWorld is built per episode, so that ran on
        // every single reset. Measured: 1341 ms on stormkeep, 630 on catharsis, 541 on courtfun, against
        // 10-11 ms to read the file. The graph is not even used by a neural bot -- the policy navigates on
        // the baked field and the destination comes from the environment -- but the population loads it
        // lazily whichever way, so the cheap path is the one to be on.
        _world.ConfigReader = path => _mapSource!.Vfs.Exists(path) ? _mapSource.Vfs.ReadText(path) : null;
        _world.Boot("dm");
        ApplyTrainingCvars();

        _field = map.Field;
        _features = new MapFeatures();
        _features.Build(_world.Services.EntityTable.All);
        _distance = dist;

        // The generated-course record the rest of the class reads for spawn, target and world bounds.
        _course = new CourseGenerator.Course { World = map.World, Spawn = spawn, Target = target };

        _dummyNet ??= PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size);
        _service = NeuralBotService.ForPreparedMap(_evalPolicy ?? _dummyNet, _field, _features, map.Name);
        _world.Bots.Neural = _service;
        Cvars.Set("bot_neural", "1");

        SpawnRosterAndSettle(observations);
    }

    /// <summary>Console sink for the map loader; null stays silent.</summary>
    public Action<string>? Log;

    /// <summary>
    /// Weak handles on retired worlds, so the env can say how many are still reachable.
    ///
    /// <para>Diagnostic for the leak that has killed every training run: about 1.6 MB is retained per
    /// episode, the managed heap climbs straight through gen2 collections, and it happens on a single map
    /// so it is not map handling. Detaching the world's cvar-store subscription -- the one retainer the
    /// code documents -- changed nothing, which means either the world is not what leaks or something else
    /// holds it too. Counting live retired worlds separates those two cases instead of guessing at a third
    /// candidate.</para>
    /// </summary>
    private readonly List<WeakReference<GameWorld>> _retired = new();

    /// <summary>
    /// Break every reference this env attached to the outgoing world, then record it weakly.
    ///
    /// <para>Diagnostic as much as fix. Retired worlds survive a forced collect one per episode, and
    /// detaching the cvar-store subscription -- the only retainer the code documents -- changed nothing.
    /// If clearing all of these drives retiredWorldsAlive to zero, the retainer is one of them and can be
    /// bisected; if the count keeps climbing, it is held by something outside this class and the search
    /// moves to the engine.</para>
    /// </summary>
    private void RetireWorld()
    {
        if (_world is null) return;

        // Detach from the process-wide cvar store: GameWorld.Shutdown's whole purpose.
        _world.Shutdown();

        // The closure here captures THIS env, so it makes the world hold the env rather than the reverse --
        // but it also keeps the world's own graph alive through the delegate, so clear it anyway.
        _world.ConfigReader = null;
        _world.BrushModels = null;
        _world.MapBsp = null;
        _world.Bots.Neural = null;

        // Agents hold Player and BotBrain, which belong to the outgoing world.
        foreach (Agent a in _agents)
        {
            a.Brain = null!;
            a.Player = null!;
            a.Loco = null!;
            a.Distance = null!;
        }
        _agents.Clear();
        _service = null;

        _retired.Add(new WeakReference<GameWorld>(_world));
    }

    /// <summary>How many retired worlds are still reachable. 0-1 is healthy; a rising count is the leak.</summary>
    public int LiveRetiredWorlds()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int alive = 0;
        foreach (WeakReference<GameWorld> w in _retired)
            if (w.TryGetTarget(out _)) alive++;
        return alive;
    }

    /// <summary>Connect the roster, let the world settle, and write the opening observations.</summary>
    private void SpawnRosterAndSettle(Span<float> observations)
    {
        _agents.Clear();
        for (int i = 0; i < _cfg.Agents; i++)
            _agents.Add(SpawnAgent(i));

        // A few frames so the sync attaches locomotors, the bots are alive, and the first ground contact
        // has happened.
        for (int t = 0; t < 6; t++) _world.Frame(SimulationLoop.TicRate);

        for (int i = 0; i < _agents.Count; i++)
        {
            Agent a = _agents[i];
            if (a.Player.IsFreed)
                throw new InvalidOperationException(
                    "an agent was disconnected during warm-up — check bot_number against the roster size");
            a.Loco = a.Brain.Locomotor
                     ?? throw new InvalidOperationException(
                         "the bot population did not attach a locomotor — bot_neural or the service is unset");
            // Evaluating: let the locomotor run the network. Training: it builds the observation and
            // applies the action the trainer sampled.
            a.Loco.UseExternalAction = _evalPolicy is null;
            // With external actions the env owns the step boundary, so it also owns when the observation is
            // built: after the physics, not inside the think. See NeuralLocomotor.DeferObservationBuild.
            a.Loco.DeferObservationBuild = _evalPolicy is null;
            RefreshCorridor(a);
            a.PrevPotential = Potential(a, a.Player.Origin);
            a.StartPotential = a.PrevPotential;
            a.PrevHealth = Vitality(a.Player);
            // Build the opening observation through the locomotor, so it is produced by exactly the code
            // that will produce every later one.
            RunLocomotor(a);
            a.Loco.LastObservation.CopyTo(observations.Slice(i * ObservationSize, ObservationSize));
        }
    }

    private IReadOnlyList<EntityDict> BuildEntityDicts()
    {
        var dicts = new List<EntityDict> { new("worldspawn") };
        dicts.Add(new EntityDict("info_player_deathmatch", _course.Spawn));
        foreach ((string cn, Vector3 origin, Vector3 mins, Vector3 maxs, string target, string targetName) in _course.Entities)
        {
            var d = new EntityDict(cn, origin);
            if (mins != maxs)
            {
                d.Fields["mins"] = Fmt(mins);
                d.Fields["maxs"] = Fmt(maxs);
            }
            if (target.Length > 0) d.Fields["target"] = target;
            if (targetName.Length > 0) d.Fields["targetname"] = targetName;
            if (cn == "trigger_hurt") d.Fields["dmg"] = "1000";   // lethal, so falling short is a real loss
            if (cn == "trigger_push") d.Fields["height"] = "180";
            dicts.Add(d);
        }
        return dicts;
    }

    private static string Fmt(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X} {v.Y} {v.Z}");

    /// <summary>
    /// A target and its distance field for one agent. On a real map this is an independent draw; on a
    /// generated course it is the host's shared route.
    /// </summary>
    private (Vector3 Target, NavDistanceField Distance) DrawAgentRoute()
    {
        if (_currentMap is null || _mapSource is null) return (_course.Target, _distance);

        (_, float minRoute, float maxRoute, float maxStepUp) = StageProfile(_cfg.Stage);
        var draw = _mapSource.NextRouteOn(_currentMap, _rng, minRoute, maxRoute, maxStepUp);
        return draw is null ? (_course.Target, _distance) : (draw.Value.Target, draw.Value.Distance);
    }

    private Agent SpawnAgent(int index)
    {
        // ClientConnect's own OnBotConnected hook routes into BotPopulation.RegisterBot, so the brain
        // exists by the time this returns; that is the same path a fixcount fill takes.
        Player p = _world.Clients.ClientConnect(isBot: true, netName: $"nb{index}").Player;
        BotBrain brain = _world.Bots.BrainOf(p)
            ?? throw new InvalidOperationException("bot connected without a brain — OnBotConnected is unwired");

        // Each agent draws its OWN route through this host's map, so route diversity does not scale with
        // host count. Geometry cannot vary within a host -- one GameWorld, one map -- but a route is a
        // spawn/target pair plus a Dijkstra flood, which costs a few milliseconds and a float per cell.
        // Generated stages keep the shared course: there the geometry IS the course, so there is nothing
        // else to draw.
        (Vector3 agentTarget, NavDistanceField agentDist) = DrawAgentRoute();

        var a = new Agent
        {
            Brain = brain,
            Player = p,
            Target = agentTarget,
            Distance = agentDist,
            WeaponPermit = _rng.NextDouble() < _cfg.WeaponChance,
            PermitFlipStep = _rng.NextDouble() < _cfg.PermitFlipChance ? _rng.Next(60, Math.Max(61, _cfg.MaxSteps)) : -1,
            AimConstraint = _rng.NextDouble() < _cfg.AimConstraintChance,
        };

        if (a.AimConstraint)
        {
            Vector3 toGoal = a.Target - p.Origin;
            float baseYaw = toGoal.LengthSquared() > 1f ? QMath.VecToAngles(QMath.Normalize(toGoal)).Y : 0f;
            a.AimTarget = new Vector3(
                (float)(_rng.NextDouble() * 40.0 - 20.0),
                baseYaw + (float)(_rng.NextDouble() * 220.0 - 110.0),
                0f);
            a.AimWeight = 0.4f + (float)_rng.NextDouble() * 0.6f;
        }

        if (a.WeaponPermit) GrantMovementWeapons(p);

        // The tactician's goal is replaced wholesale: this episode has exactly one destination and the
        // curriculum owns the permit and the aim constraint.
        brain.IntentOverride = _ => BuildIntent(a);
        return a;
    }

    private void GrantMovementWeapons(Player p)
    {
        foreach (string name in NeuralObservation.MovementWeapons)
        {
            if (Weapons.ByName(name) is not { } w) continue;
            p.OwnedWeaponSet.Add(w);
            if (w.AmmoType != ResourceType.None)
                p.SetResource(w.AmmoType, 200f);
        }
        if (Weapons.ByName("blaster") is { } b)
            Inventory.SwitchWeapon(p, b);
    }

    /// <summary>
    /// Recompute one agent's corridor look-ahead. Called once per env step, before anything reads the
    /// intent, because the walk is the most expensive thing in BuildIntent and the answer does not change
    /// within a step.
    /// </summary>
    private void RefreshCorridor(Agent a)
    {
        a.CorridorNear = a.Distance.PointAlongRoute(a.Player.Origin, 320f);
        a.CorridorFar = a.Distance.PointAlongRoute(a.CorridorNear, 320f);
    }

    private MoveIntent BuildIntent(Agent a)
    {
        bool permit = a.WeaponPermit && (a.PermitFlipStep < 0 || a.Step < a.PermitFlipStep);

        // Corridor look-ahead, walked down the distance field once per step by RefreshCorridor.
        //
        // Both of these used to be set to the target, which made six of the 206 observation floats constant
        // for the whole of training. At runtime they carry the next two waypoint-route nodes
        // (BotBrainNeural reads Nav.RouteNode), so the policy was learning to ignore an input that then
        // started carrying information. Walking the field produces the same quantity from the same
        // geometry, and unlike the waypoint graph it exists on generated courses too.
        Vector3 near = a.CorridorNear;
        Vector3 far = a.CorridorFar;

        return new MoveIntent
        {
            GoalPos = a.Target,
            Route = a.Distance,
            CorridorA = near,
            CorridorB = far,
            Urgency = 1f,
            WeaponMovementAllowed = permit,
            AimRequired = a.AimConstraint,
            RequiredAimAngles = a.AimTarget,
            AimWeight = a.AimConstraint ? a.AimWeight : 0f,
            HullMins = a.Player.Mins,
            HullMaxs = a.Player.Maxs,
        };
    }

    /// <summary>
    /// Build this agent's observation without advancing the world, by running the locomotor with its
    /// current pending action. Used to produce the opening observation of an episode.
    /// </summary>
    private void RunLocomotor(Agent a)
    {
        if (_evalPolicy is null)
        {
            a.Loco.BuildObservationOnly(a.Player, BuildIntent(a), _field, _features,
                a.Player.ViewAngles, _world.Time, _cfg.TraceFan);
            return;
        }
        a.Loco.Think(a.Player, BuildIntent(a), _field, _features,
            a.Player.ViewAngles, _world.Time, 1f / 18f, 400f, _cfg.TraceFan);
    }

    // =============================================================================================
    // stepping
    // =============================================================================================

    /// <summary>
    /// Apply one action per agent, advance the world by <see cref="Config.TicksPerStep"/> ticks, and report
    /// the observation, reward and termination for each.
    /// </summary>
    /// <param name="actions"><see cref="ActionEncoding.Size"/> floats per agent, in agent order.</param>
    public void Step(ReadOnlySpan<float> actions, Span<float> observations, Span<float> rewards,
        Span<byte> dones, Span<byte> truncated)
    {
        for (int i = 0; i < _agents.Count; i++)
        {
            Agent a = _agents[i];
            if (a.Done) continue;
            RefreshCorridor(a);
            if (_evalPolicy is not null) continue;
            a.Loco.PendingExternalAction = ActionEncoding.Decode(actions.Slice(i * ActionEncoding.Size, ActionEncoding.Size));
        }

        // The whole server tick: bot thinks (which run the locomotor and consume the pending action),
        // physics, weapons, triggers, damage, thinks.
        for (int t = 0; t < _cfg.TicksPerStep; t++)
            _world.Frame(SimulationLoop.TicRate);

        for (int i = 0; i < _agents.Count; i++)
        {
            Agent a = _agents[i];
            if (a.Done)
            {
                rewards[i] = 0f;
                dones[i] = 1;
                truncated[i] = 0;
                observations.Slice(i * ObservationSize, ObservationSize).Clear();
                continue;
            }

            a.Step++;
            rewards[i] = Reward(a, out bool terminal, out bool cut);
            dones[i] = (byte)(terminal ? 1 : 0);
            truncated[i] = (byte)(cut ? 1 : 0);
            if (terminal || cut) a.Done = true;

            // Build the observation HERE, after the step's physics, so what the trainer sees is the state
            // its next action will act on. Reading whatever the last think happened to build put a
            // one-think lag between the two and cost 4x the arrival rate.
            if (_evalPolicy is null)
                a.Loco.BuildObservationOnly(a.Player, BuildIntent(a), _field, _features,
                    a.Player.ViewAngles, _world.Time, _cfg.TraceFan);
            a.Loco.LastObservation.CopyTo(observations.Slice(i * ObservationSize, ObservationSize));
        }
    }

    /// <summary>True when every agent has finished, so the trainer should call <see cref="Reset"/>.</summary>
    public bool AllDone()
    {
        for (int i = 0; i < _agents.Count; i++) if (!_agents[i].Done) return false;
        return true;
    }

    /// <summary>
    /// Diagnostic snapshot of agent 0: where it is, how fast, how far from its target, and whether the
    /// locomotor the env holds is still the one the brain is using. For the env host's bench only.
    /// </summary>
    public string DebugAgent0()
    {
        if (_agents.Count == 0) return "no agents";
        Agent a = _agents[0];
        bool sameLoco = ReferenceEquals(a.Loco, a.Brain.Locomotor);
        return $"act mv {a.Loco.LastAction.MoveForward:+0.0;-0.0},{a.Loco.LastAction.MoveRight:+0.0;-0.0} " +
               $"jmp {(a.Loco.LastAction.Jump ? 1 : 0)} yaw {a.Loco.LastAction.YawDelta:+00.0;-00.0} " +
               $"pos {a.Player.Origin.X:F0},{a.Player.Origin.Y:F0},{a.Player.Origin.Z:F0} " +
               $"spd {a.Player.Velocity.Length():F0} " +
               $"dist {(a.Player.Origin - a.Target).Length():F0} " +
               $"ground {a.Player.OnGround} step {a.Step} done {a.Done} " +
               $"loco-ok {sameLoco} ext {a.Brain.Locomotor?.UseExternalAction} " +
               $"move {a.Brain.LastInput.MoveValues.X:F0},{a.Brain.LastInput.MoveValues.Y:F0}";
    }

    /// <summary>Per-episode summary the trainer logs.</summary>
    public (int Arrived, float MeanArrivalTime, float MeanRemaining) EpisodeSummary()
    {
        int arrived = 0;
        float timeSum = 0f, remainSum = 0f;
        for (int i = 0; i < _agents.Count; i++)
        {
            Agent a = _agents[i];
            if (a.Arrived) { arrived++; timeSum += a.ArrivalTime; }
            float d = Potential(a, a.Player.Origin);
            remainSum += MathF.Min(d, 8000f);
        }
        return (arrived,
            arrived > 0 ? timeSum / arrived : 0f,
            _agents.Count > 0 ? remainSum / _agents.Count : 0f);
    }

    /// <summary>
    /// How the current episode ended for each agent, indexed by outcome: arrived, died, fell, timed out.
    ///
    /// <para>The reward's dense terms are net negative -- measured mean per-step reward is about -0.007 for
    /// every arm including the scripted one -- so surviving to the 900-step cap accrues roughly -6.3, while
    /// dying at step 100 costs about -0.7 of dense terms plus the -5 death penalty. If that arithmetic
    /// holds, ending the episode early is competitive with finishing it, and a policy that optimises the
    /// objective will find that out. This counts whether it has.</para>
    /// </summary>
    public (int Arrived, int Died, int Fell, int TimedOut) OutcomeCounts()
    {
        int arrived = 0, died = 0, fell = 0, timedOut = 0;
        for (int i = 0; i < _agents.Count; i++)
        {
            switch (_agents[i].Outcome)
            {
                case 1: arrived++; break;
                case 2: died++; break;
                case 3: fell++; break;
                case 4: timedOut++; break;
            }
        }
        return (arrived, died, fell, timedOut);
    }

    /// <summary>Upper edge, in qu, of each route-length bucket reported by <see cref="ArrivalByRouteLength"/>.</summary>
    public static readonly float[] RouteBuckets = { 1000f, 1500f, 2500f, 4000f, float.PositiveInfinity };

    /// <summary>
    /// Arrivals and attempts per starting-route-length bucket, for the current episode.
    ///
    /// <para>A single arrival rate cannot distinguish a policy that never learned to navigate from one that
    /// navigates well over 900 qu and is being handed 4000 qu courses. Bucketing separates them: skill that
    /// exists shows up as a rate that falls with distance, and skill that does not shows up as a flat line.</para>
    /// </summary>
    public (int[] Arrived, int[] Attempts) ArrivalByRouteLength()
    {
        int[] arrived = new int[RouteBuckets.Length];
        int[] attempts = new int[RouteBuckets.Length];
        for (int i = 0; i < _agents.Count; i++)
        {
            Agent a = _agents[i];
            int b = 0;
            while (b < RouteBuckets.Length - 1 && a.StartPotential > RouteBuckets[b]) b++;
            attempts[b]++;
            if (a.Arrived) arrived[b]++;
        }
        return (arrived, attempts);
    }

    // =============================================================================================
    // reward
    // =============================================================================================

    /// <summary>Arrival radius. Generous, so the policy is not scored on final centimetres.</summary>
    public const float ArriveRadius = 96f;

    /// <summary>
    /// Cost of dying, or of falling out of the world.
    ///
    /// <para>This has to beat the whole remaining cost of staying alive, or ending the episode early is
    /// simply the cheaper option and a policy maximising return will take it. It did not, and the policy
    /// did. The dense terms -- aim error, turn, and progress once the bot is stuck -- run about -0.007 per
    /// step for every arm including the scripted one, so surviving to the 900-step cap accrues roughly
    /// -6.3, while dying at step 100 cost about -0.7 of dense terms plus the old -5 penalty: -5.7, and
    /// therefore strictly better than living.</para>
    ///
    /// <para>Measured on stage 6 at -5, against the scripted "run at the target" baseline:</para>
    /// <code>
    ///              arrived   died    timed out   return
    ///   scripted    20.6%     6.8%     72.4%     -6.594
    ///   policy       8.6%    65.1%     26.2%     -6.672
    /// </code>
    /// <para>Ten times the death rate for less than half the arrivals, and the objective scored the two
    /// within 0.08 of each other. That is not a policy that failed to learn; it is a policy that learned
    /// exactly what it was asked. It also explains why entropy, the action frame, gamma, the learning rate,
    /// the view-delta sigma and the padding mask all moved their own diagnostics and never moved arrivals:
    /// each one only helped the search find this optimum faster.</para>
    ///
    /// <para>-20 puts death clearly below the worst survival outcome while staying within the arrival
    /// bonus's own 10-30 range, so the gradient toward arriving is not swamped by the one away from
    /// dying.</para>
    /// </summary>
    public const float DeathPenalty = 20f;

    /// <summary>
    /// Reward per Quake unit of geodesic distance closed. At a 400 qu/s run and 4 ticks per step that is
    /// about 0.22 per step, an order above the 0.02 time cost, so progress dominates and standing still
    /// loses.
    /// </summary>
    public const float ProgressScale = 0.01f;

    /// <summary>Paid for arriving at all, regardless of how long it took.</summary>
    public const float ArriveBase = 10f;

    /// <summary>
    /// Paid for arriving FAST, scaled by the fraction of the step budget still unspent.
    ///
    /// <para>Speed is the goal, so this is the term that has to dominate, and it is the only safe place to
    /// put that pressure. Arriving at step 100 of 900 pays this almost in full; arriving at step 800 pays
    /// about a ninth of it. Raising it cannot make any failure more attractive, because it is only ever paid
    /// on success.</para>
    ///
    /// <para>The tempting alternative -- a bigger per-step time penalty -- is the one to avoid. The time
    /// cost is already -0.02 x 900 = -18 over a full episode against a death penalty of -20, so doubling it
    /// would make surviving to the cap cost -36 and dying cost -20, and dying would be the cheaper option
    /// again. That is exactly the bug that had the policy killing itself in 65% of stage-6 episodes.</para>
    ///
    /// <para>At 60, arriving at step 100 is worth about 63 against 17 for arriving at step 800, and the time
    /// term adds another 14 to the gap: roughly 60 for speed against 17 for merely finishing.</para>
    /// </summary>
    public const float ArriveSpeedBonus = 60f;

    private static float Vitality(Player p) => p.Health + p.GetResource(ResourceType.Armor);

    /// <summary>Lookaheads along the route, in qu, used by <see cref="ReportStuck"/>.</summary>
    /// <remarks>
    /// Chosen against the movement envelope rather than round numbers: a standing jump clears about 105 qu
    /// of ledge and a running one about 320 qu of gap, so 64 is "inside one step", 160 is "past anything
    /// walkable", and 320 is "at the limit of a running jump". An obstacle that shows up at 64 is something
    /// the bot is standing against; one that only shows at 320 is something it would have to commit to.
    /// </remarks>
    private static readonly float[] StuckLookaheads = { 64f, 160f, 320f };

    /// <summary>How often <see cref="Agent.RecentAnchor"/> is refreshed, in policy steps (90 = 5 s at 18 Hz).</summary>
    private const int StuckAnchorPeriod = 90;

    /// <summary>
    /// Characterise where a timed-out agent actually stopped, so a plateau can be attributed to a named
    /// obstacle rather than to "the policy is bad".
    ///
    /// <para>One line per timeout. <c>left</c> against <c>best</c> separates "never got near the target"
    /// from "got near and then wedged", and <c>stalled</c> -- steps since the closest approach -- says how
    /// long it stayed wedged. <c>reach</c> is the one that matters most: 0 means the goal is no longer
    /// reachable from where it stands, so it has fallen somewhere it cannot climb out of and the episode
    /// was unwinnable from that moment, which is a course-generation problem and not a policy one.</para>
    ///
    /// <para>The trailing <c>+N=dz/clearance/content</c> triples are the floor profile along the descending
    /// route. A large positive dz is a ledge, NOFLOOR is a gap, and a Lethal/Harmful/Water content bit is a
    /// hazard the route runs straight through.</para>
    /// </summary>
    private void ReportStuck(Agent a)
    {
        if (Log is null) return;
        Vector3 at = a.Player.Origin;
        float ownFloor = _field.GroundHeight(at, at.Z);
        string F(float v) => v.ToString("F0", CultureInfo.InvariantCulture);

        string line = $"[stuck] map={_world.MapName} left={F(a.Distance.DistanceAt(at))}"
                    + $" best={F(a.BestPotential)} stalled={_cfg.MaxSteps - a.BestStep}"
                    + $" reach={(a.Distance.IsReachable(at) ? 1 : 0)} goalDZ={F(a.Target.Z - at.Z)}"
                    + $" moved={F((at - a.RecentAnchor).Length())}/{a.Step - a.RecentAnchorStep}"
                    + $" spd={F(a.Player.Velocity.Length())}";

        foreach (float look in StuckLookaheads)
        {
            Vector3 ahead = a.Distance.PointAlongRoute(at, look);
            line += $" +{F(look)}=";
            if (!_field.TrySampleBelow(ahead, out FloorSpan span)) { line += "NOFLOOR"; continue; }
            // Commas would break whitespace-delimited aggregation, so flag sets join on '|'.
            string content = ((NavContent)span.Content).ToString().Replace(", ", "|");
            line += $"{F(span.FloorZ - ownFloor)}/{span.Clearance}/{content}";
        }
        Log(line);
    }

    private float Reward(Agent a, out bool terminal, out bool truncatedOut)
    {
        terminal = false;
        truncatedOut = false;
        Player p = a.Player;
        float r = 0f;

        // --- progress along the geodesic ---
        // The plain difference d - d', NOT the discounted potential form gamma*phi(s') - phi(s).
        //
        // The discounted form is what the shaping theorem is stated in, and it is what this originally used.
        // It is also, with phi = -d, worth d*(1-gamma) per step to an agent that does not move: at gamma 0.99
        // and 1000 qu from the target that is +0.1 per step for standing still, five times the time penalty.
        // Measured with random actions it gave a mean reward of +0.057/step, so the best thing a policy could
        // learn was to stay far away and do nothing. The telescoping sum still respects the theorem; the
        // finite horizon and the time cost are what turn the drift into a local optimum.
        //
        // The difference form is zero for a stationary agent, positive only for real progress, and its total
        // over an episode is exactly the distance closed. Standard practice, and the one that survives
        // contact with a time penalty.
        float potential = Potential(a, p.Origin);
        r += (a.PrevPotential - potential) * ProgressScale;
        a.PrevPotential = potential;
        if (potential < a.BestPotential) { a.BestPotential = potential; a.BestStep = a.Step; }
        if (a.Step <= 1 || a.Step - a.RecentAnchorStep >= StuckAnchorPeriod)
        {
            a.RecentAnchor = p.Origin;
            a.RecentAnchorStep = a.Step;
        }

        // --- time ---
        // Small, constant, and the only reason bunnyhopping is worth the trouble.
        r -= 0.02f;

        // --- damage taken ---
        // Non-zero, or the policy rocket-jumps everywhere at 15 health and dies to the first stray shot in a
        // real match. Small, or it never learns to weapon-jump at all.
        float vit = Vitality(p);
        if (vit < a.PrevHealth) r -= (a.PrevHealth - vit) * 0.004f;
        a.PrevHealth = vit;

        // --- aim constraint ---
        if (a.AimConstraint)
        {
            Vector3 d = WrapPitchYaw(a.AimTarget - p.ViewAngles);
            float err = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            r -= a.AimWeight * MathF.Min(err / 90f, 2f) * 0.03f;
        }

        // --- jerk ---
        // The term that actually produces smooth motion; slewing the goal input alone yields a policy that
        // snaps the moment the smoothed signal arrives.
        NeuralAction last = a.Loco.LastAction;
        float turn = MathF.Abs(last.YawDelta) / NeuralAction.MaxYawRate
                     + MathF.Abs(last.PitchDelta) / NeuralAction.MaxPitchRate;
        r -= turn * 0.004f;

        // --- terminal states ---
        if (p.IsDead || p.Health <= 0f)
        {
            r -= DeathPenalty;
            terminal = true;
            a.Outcome = 2;
            return r;
        }

        if ((p.Origin - a.Target).Length() <= ArriveRadius)
        {
            a.Arrived = true;
            a.Outcome = 1;
            a.ArrivalTime = _world.Time;
            // Scaled by the step budget left, so a faster route pays strictly more. A flat bonus makes
            // "arrive eventually" as good as "arrive fast" once the time cost is amortised.
            float budgetLeft = 1f - a.Step / (float)_cfg.MaxSteps;
            r += ArriveBase + ArriveSpeedBonus * MathF.Max(0f, budgetLeft);
            terminal = true;
            return r;
        }

        if (p.Origin.Z < _course.World.WorldMins.Z - 512f)
        {
            r -= DeathPenalty;
            terminal = true;
            a.Outcome = 3;
            return r;
        }

        if (a.Step >= _cfg.MaxSteps)
        {
            truncatedOut = true;
            a.Outcome = 4;
            if (_cfg.StuckReport) ReportStuck(a);
            return r;
        }

        return r;
    }

    /// <summary>Geodesic distance from <paramref name="at"/> to THIS AGENT'S target.</summary>
    private static float Potential(Agent a, Vector3 at)
    {
        float d = a.Distance.DistanceAt(at);
        // Off-graph (mid-air over a pit, mid jump-pad arc) has no graph distance. Fall back to a padded
        // straight line so the shaping term stays finite and airborne states are not all identical.
        return d >= NavDistanceField.Unreachable ? (at - a.Target).Length() * 1.5f : d;
    }

    private static Vector3 WrapPitchYaw(Vector3 v)
    {
        v.Y -= MathF.Floor(v.Y / 360f) * 360f;
        if (v.Y >= 180f) v.Y -= 360f;
        while (v.X < -180f) v.X += 360f;
        while (v.X > 180f) v.X -= 360f;
        return v;
    }
}

/// <summary>
/// Wire encoding for one action as the trainer sends it: six discrete choices as indices, then the two
/// continuous view deltas in [-1,1].
///
/// <para>Deliberately distinct from <see cref="ActionSpace"/>, which decodes network LOGITS at runtime.
/// During training the Python side samples from the distribution and sends the chosen indices; at runtime
/// the C# side takes the argmax itself. Keeping the two paths separate stops a sampling change on one side
/// silently reinterpreting the other, and the shared <see cref="MoveTable"/> is the one thing that must
/// match (guarded by <c>NeuralPolicyTests.MoveTablesAgree</c>).</para>
/// </summary>
public static class ActionEncoding
{
    /// <summary>Floats per agent on the wire.</summary>
    public const int Size = 8;

    /// <summary>Nine wishmove categories: eight compass directions plus a null. Same table <see cref="ActionSpace"/> uses.</summary>
    public static readonly (float Fwd, float Right)[] MoveTable =
    {
        (0f, 0f),
        (1f, 0f), (0.7071f, 0.7071f), (0f, 1f), (-0.7071f, 0.7071f),
        (-1f, 0f), (-0.7071f, -0.7071f), (0f, -1f), (0.7071f, -0.7071f),
    };

    public static NeuralAction Decode(ReadOnlySpan<float> a)
    {
        int move = Math.Clamp((int)a[0], 0, MoveTable.Length - 1);
        (float fwd, float right) = MoveTable[move];
        return new NeuralAction
        {
            MoveForward = fwd,
            MoveRight = right,
            Jump = a[1] > 0.5f,
            Crouch = a[2] > 0.5f,
            Attack1 = a[3] > 0.5f,
            Attack2 = a[4] > 0.5f,
            WeaponSelect = (int)a[5] - 1,
            YawDelta = Math.Clamp(a[6], -1f, 1f) * NeuralAction.MaxYawRate,
            PitchDelta = Math.Clamp(a[7], -1f, 1f) * NeuralAction.MaxPitchRate,
        };
    }
}
