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

        /// <summary>Episode cap in policy steps. 900 at 18 Hz is 50 s of game time.</summary>
        public int MaxSteps = 900;

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
    }

    private readonly Config _cfg;
    private readonly Random _rng;

    private GameWorld _world = null!;
    private NavField _field = null!;
    private MapFeatures _features = null!;
    private NavDistanceField _distance = null!;
    private CourseGenerator.Course _course = null!;
    private PolicyNetwork _dummyNet = null!;
    private NeuralBotService _service = null!;

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
        public Vector3 Target;
        public bool WeaponPermit;
        public int PermitFlipStep;
        public bool AimConstraint;
        public Vector3 AimTarget;
        public float AimWeight;
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
        _course = CourseGenerator.Generate(_cfg.Stage, _cfg.Seed * 7919 + _episodeIndex);

        _world = new GameWorld(_course.World, BuildEntityDicts()) { MapName = $"nbcourse{_episodeIndex}" };
        _world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_number", "0");          // the roster is created directly, not by fixcount
        Cvars.Set("skill", "10");
        Cvars.Set("g_balance_selfdamagepercent", "0.65");
        Cvars.Set("bot_nofire", "0");
        // Agents share a world for throughput, not to fight. Without this they acquire each other as
        // enemies, combat claims the weapon (so the policy never gets to weapon-jump), and they shoot each
        // other down in seconds — which is a deathmatch, not a locomotion curriculum.
        Cvars.Set("bot_ignore_bots", "1");

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
        _service = NeuralBotService.ForPreparedMap(_dummyNet, _field, _features, _world.MapName!);

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

        _agents.Clear();
        for (int i = 0; i < _cfg.Agents; i++)
            _agents.Add(SpawnAgent(i));

        // A few frames so the sync attaches locomotors, the bots are alive, and the first ground contact
        // has happened.
        for (int t = 0; t < 6; t++) _world.Frame(SimulationLoop.TicRate);

        for (int i = 0; i < _agents.Count; i++)
        {
            Agent a = _agents[i];
            a.Loco = a.Brain.Locomotor
                     ?? throw new InvalidOperationException(
                         "the bot population did not attach a locomotor — bot_neural or the service is unset");
            a.Loco.UseExternalAction = true;
            a.PrevPotential = Potential(a.Player.Origin);
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

    private Agent SpawnAgent(int index)
    {
        // ClientConnect's own OnBotConnected hook routes into BotPopulation.RegisterBot, so the brain
        // exists by the time this returns; that is the same path a fixcount fill takes.
        Player p = _world.Clients.ClientConnect(isBot: true, netName: $"nb{index}").Player;
        BotBrain brain = _world.Bots.BrainOf(p)
            ?? throw new InvalidOperationException("bot connected without a brain — OnBotConnected is unwired");

        var a = new Agent
        {
            Brain = brain,
            Player = p,
            Target = _course.Target,
            WeaponPermit = _rng.NextDouble() < _cfg.WeaponChance,
            PermitFlipStep = _rng.NextDouble() < _cfg.PermitFlipChance ? _rng.Next(60, Math.Max(61, _cfg.MaxSteps)) : -1,
            AimConstraint = _rng.NextDouble() < _cfg.AimConstraintChance,
        };

        if (a.AimConstraint)
        {
            Vector3 toGoal = a.Target - _course.Spawn;
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

    private MoveIntent BuildIntent(Agent a)
    {
        bool permit = a.WeaponPermit && (a.PermitFlipStep < 0 || a.Step < a.PermitFlipStep);
        return new MoveIntent
        {
            GoalPos = a.Target,
            CorridorA = a.Target,
            CorridorB = a.Target,
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
            a.Loco.LastObservation.CopyTo(observations.Slice(i * ObservationSize, ObservationSize));
        }
    }

    /// <summary>True when every agent has finished, so the trainer should call <see cref="Reset"/>.</summary>
    public bool AllDone()
    {
        for (int i = 0; i < _agents.Count; i++) if (!_agents[i].Done) return false;
        return true;
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
            float d = Potential(a.Player.Origin);
            remainSum += MathF.Min(d, 8000f);
        }
        return (arrived,
            arrived > 0 ? timeSum / arrived : 0f,
            _agents.Count > 0 ? remainSum / _agents.Count : 0f);
    }

    // =============================================================================================
    // reward
    // =============================================================================================

    /// <summary>Arrival radius. Generous, so the policy is not scored on final centimetres.</summary>
    public const float ArriveRadius = 96f;

    /// <summary>
    /// Reward per Quake unit of geodesic distance closed. At a 400 qu/s run and 4 ticks per step that is
    /// about 0.22 per step, an order above the 0.02 time cost, so progress dominates and standing still
    /// loses.
    /// </summary>
    public const float ProgressScale = 0.01f;

    private static float Vitality(Player p) => p.Health + p.GetResource(ResourceType.Armor);

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
        float potential = Potential(p.Origin);
        r += (a.PrevPotential - potential) * ProgressScale;
        a.PrevPotential = potential;

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
            r -= 5f;
            terminal = true;
            return r;
        }

        if ((p.Origin - a.Target).Length() <= ArriveRadius)
        {
            a.Arrived = true;
            a.ArrivalTime = _world.Time;
            // Scaled by the step budget left, so a faster route pays strictly more. A flat bonus makes
            // "arrive eventually" as good as "arrive fast" once the time cost is amortised.
            float budgetLeft = 1f - a.Step / (float)_cfg.MaxSteps;
            r += 10f + 20f * MathF.Max(0f, budgetLeft);
            terminal = true;
            return r;
        }

        if (p.Origin.Z < _course.World.WorldMins.Z - 512f)
        {
            r -= 5f;
            terminal = true;
            return r;
        }

        if (a.Step >= _cfg.MaxSteps)
        {
            truncatedOut = true;
            return r;
        }

        return r;
    }

    private float Potential(Vector3 at)
    {
        float d = _distance.DistanceAt(at);
        // Off-graph (mid-air over a pit, mid jump-pad arc) has no graph distance. Fall back to a padded
        // straight line so the shaping term stays finite and airborne states are not all identical.
        return d >= NavDistanceField.Unreachable ? (at - _course.Target).Length() * 1.5f : d;
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
