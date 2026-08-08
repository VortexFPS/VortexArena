using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VortexArena.Server.Bot.Neural;

namespace VortexArena.NeuralHost;

/// <summary>
/// The environment host. Listens on a localhost port, accepts one trainer, and serves
/// reset/step/observation/reward until the trainer closes.
///
/// <para>One world per process by design: <c>Api.Services</c> is process-ambient, so two worlds on threads
/// would fight over it. The trainer scales by launching N of these on N ports, which is also how the work
/// spreads across cores.</para>
///
/// <code>
/// va-neural-host --port 7801 --agents 8 --stage 1 --seed 1
/// va-neural-host --bench 2000          # no trainer: measure steps/s and exit
/// </code>
/// </summary>
public static class Program
{
    private const int ProtocolVersion = 1;

    public static int Main(string[] args)
    {
        // A managed exception on a BACKGROUND thread terminates the process without ever reaching the
        // try/catch around the message loop, so the trainer sees a bare connection reset and the reason is
        // gone. These two handlers are the only way that failure mode says anything at all.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[neural-host] UNHANDLED on a background thread: {e.ExceptionObject}");
            Console.Error.Flush();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[neural-host] unobserved task exception: {e.Exception}");
            Console.Error.Flush();
            e.SetObserved();
        };

        var opts = Options.Parse(args);
        if (opts.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        if (opts.VerifyWeights is not null)
            return VerifyWeights(opts.VerifyWeights);

        if (opts.BenchSteps > 0)
            return RunBenchmark(opts);

        return RunServer(opts);
    }

    /// <summary>
    /// Load a weight file the Python exporter wrote and report whether this build can use it.
    ///
    /// <para>The weight format is written by <c>tools/neural/va_neural/model.py</c> and read by
    /// <c>PolicyNetwork.cs</c>: two implementations of one layout, in two languages, that only ever meet at
    /// a binary file. A transposed weight matrix or a wrong activation byte produces a network that loads
    /// and runs and is simply wrong. Running this after every export is how that gets caught in a second
    /// rather than after a training run.</para>
    /// </summary>
    private static int VerifyWeights(string path)
    {
        PolicyNetwork? net = PolicyNetwork.Load(path, out string? error);
        if (net is null)
        {
            Console.Error.WriteLine($"[verify] FAILED to load {path}: {error}");
            return 1;
        }

        Console.WriteLine($"label          {net.Label}");
        Console.WriteLine($"input          {net.InputSize} (this build expects {NeuralObservation.Size})");
        Console.WriteLine($"output         {net.OutputSize} (this build expects {ActionSpace.Size})");
        Console.WriteLine($"parameters     {net.ParameterCount:N0}");
        Console.WriteLine($"widest layer   {net.MaxLayerWidth}");

        if (net.InputSize != NeuralObservation.Size || net.OutputSize != ActionSpace.Size)
        {
            Console.Error.WriteLine("[verify] FAILED: shape does not match this build's observation/action layout");
            return 1;
        }

        // A forward pass on a deterministic non-trivial input, so a NaN or a dead network shows up here.
        var obs = new float[net.InputSize];
        for (int i = 0; i < obs.Length; i++) obs[i] = MathF.Sin(i * 0.37f);
        var output = new float[net.OutputSize];
        var sw = Stopwatch.StartNew();
        const int iterations = 20000;
        var scratch = new PolicyNetwork.Scratch(net);
        for (int i = 0; i < iterations; i++) net.Evaluate(obs, scratch, output);
        sw.Stop();

        foreach (float v in output)
        {
            if (float.IsFinite(v)) continue;
            Console.Error.WriteLine("[verify] FAILED: forward pass produced a non-finite output");
            return 1;
        }

        double usPerEval = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;
        Console.WriteLine($"forward pass   {usPerEval:F1} us  ({1000.0 / usPerEval:F0}/ms)");
        // 16 bots at the 20 Hz think rate is 320 evaluations a second.
        Console.WriteLine($"16 bots @20Hz  {usPerEval * 320 / 1000.0:F3} ms/s of CPU " +
                          $"({usPerEval * 320 / 10000.0:F3}% of one core)");

        NeuralAction action = ActionSpace.Decode(output, weaponAllowed: true, stackalloc bool[] { true, true, true });
        Console.WriteLine($"sample action  move ({action.MoveForward:+0.00;-0.00}, {action.MoveRight:+0.00;-0.00}) " +
                          $"jump {action.Jump} yaw {action.YawDelta:+0.0;-0.0} pitch {action.PitchDelta:+0.0;-0.0} " +
                          $"weapon {action.WeaponSelect}");
        Console.WriteLine("[verify] OK");
        return 0;
    }

    // =============================================================================================
    // serve
    // =============================================================================================

    private static int RunServer(Options opts)
    {
        var listener = new TcpListener(IPAddress.Loopback, opts.Port);
        listener.Start();
        // The port goes to stdout before anything else so a trainer launching with --port 0 can read the
        // assigned one. Every other message goes to stderr, keeping stdout a clean machine-readable channel.
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"PORT {port}");
        Console.Out.Flush();
        Console.Error.WriteLine($"[neural-host] listening on 127.0.0.1:{port}");

        using TcpClient client = listener.AcceptTcpClient();
        client.NoDelay = true;   // a step is a request/response round trip; Nagle would add 40 ms to each
        // Big enough to hold a whole step result. At 64 agents that is 53 KB of observation, which overruns
        // the default and leaves this process blocked mid-write until the trainer gets around to reading
        // it -- and the trainer reads its hosts in order, so everyone behind the current one stalls too.
        client.SendBufferSize = 4 << 20;
        client.ReceiveBufferSize = 4 << 20;
        listener.Stop();
        Console.Error.WriteLine("[neural-host] trainer connected");

        using NetworkStream stream = client.GetStream();
        var frames = new Frames(stream);

        TrainingEnv? env = null;
        int agents = 0, obsSize = 0;
        float[] observations = Array.Empty<float>();
        float[] rewards = Array.Empty<float>();
        byte[] dones = Array.Empty<byte>();
        byte[] truncated = Array.Empty<byte>();
        float[] actions = Array.Empty<float>();
        var payload = new List<byte>(1 << 16);
        int resets = 0;
        long stepCount = 0;

        try
        {
            while (frames.TryRead(out OpCode op, out ReadOnlySpan<byte> body))
            {
                switch (op)
                {
                    case OpCode.Hello:
                    {
                        var cfg = ReadHello(body, out int version);
                        if (version != ProtocolVersion)
                        {
                            SendError(frames, $"protocol version {version}, host speaks {ProtocolVersion}");
                            return 2;
                        }
                        env = new TrainingEnv(cfg) { Log = m => Console.Error.WriteLine(m) };
                        agents = cfg.Agents;
                        obsSize = TrainingEnv.ObservationSize;
                        observations = new float[agents * obsSize];
                        rewards = new float[agents];
                        dones = new byte[agents];
                        truncated = new byte[agents];
                        actions = new float[agents * ActionEncoding.Size];

                        payload.Clear();
                        AppendI32(payload, obsSize);
                        AppendI32(payload, ActionEncoding.Size);
                        AppendI32(payload, agents);
                        AppendI32(payload, cfg.TicksPerStep);
                        frames.Write(OpCode.HelloAck, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(payload));
                        Console.Error.WriteLine(
                            $"[neural-host] {agents} agents, obs {obsSize}, action {ActionEncoding.Size}, " +
                            $"stage {cfg.Stage}, {cfg.TicksPerStep} ticks/step");
                        break;
                    }

                    case OpCode.Reset:
                    {
                        if (env is null) { SendError(frames, "reset before hello"); return 2; }
                        resets++;
                        // Always time the reset, and always complain about a slow one. A reset that takes
                        // minutes looks exactly like a hung host from the trainer's side, and the first
                        // stage-6 run spent ninety minutes in one before anyone could see why.
                        long resetStart = Stopwatch.GetTimestamp();
                        if (opts.Debug)
                            Console.Error.WriteLine($"[neural-host] reset {resets} begin, " +
                                                    $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB managed");
                        env.Reset(observations);
                        double resetMs = (Stopwatch.GetTimestamp() - resetStart) * 1000.0 / Stopwatch.Frequency;
                        if (opts.Debug)
                            Console.Error.WriteLine($"[neural-host] reset {resets} ok in {resetMs:F0} ms");
                        else if (resetMs > 3000)
                            Console.Error.WriteLine($"[neural-host] reset {resets} took {resetMs / 1000.0:F1} s " +
                                                    $"-- that is slow enough to look like a hang; run with --debug");
                        frames.Write(OpCode.Observation, AsBytes(observations));
                        break;
                    }

                    case OpCode.Step:
                    {
                        if (env is null) { SendError(frames, "step before hello"); return 2; }
                        int expect = actions.Length * sizeof(float);
                        if (body.Length != expect)
                        {
                            SendError(frames, $"step payload is {body.Length} bytes, expected {expect}");
                            return 2;
                        }
                        CopyToFloats(body, actions);
                        stepCount++;
                        if (opts.Debug && stepCount % 100 == 0)
                            Console.Error.WriteLine($"[neural-host] step {stepCount}");
                        env.Step(actions, observations, rewards, dones, truncated);

                        payload.Clear();
                        AppendBytes(payload, AsBytes(observations));
                        AppendBytes(payload, AsBytes(rewards));
                        AppendBytes(payload, dones);
                        AppendBytes(payload, truncated);

                        // Episode stats ride along in every step result rather than arriving as their own
                        // frame when an episode happens to end. A separate optional frame means the client
                        // has to guess whether one is waiting, and the obvious way to guess (peek with a
                        // short socket timeout) breaks a buffered reader the moment it fires. Thirteen
                        // bytes a step is a good trade for a protocol with no conditional framing in it.
                        bool episodeOver = env.AllDone();
                        payload.Add((byte)(episodeOver ? 1 : 0));
                        (int arrived, float meanTime, float meanRemaining) =
                            episodeOver ? env.EpisodeSummary() : (0, 0f, 0f);
                        AppendI32(payload, arrived);
                        AppendF32(payload, meanTime);
                        AppendF32(payload, meanRemaining);

                        frames.Write(OpCode.StepResult, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(payload));
                        break;
                    }

                    case OpCode.SetStage:
                    {
                        // Applied at the next Reset, because a stage change mid-episode would score a policy
                        // on a course it did not start.
                        if (body.Length < 4) { SendError(frames, "setstage payload too short"); return 2; }
                        Console.Error.WriteLine($"[neural-host] stage -> {BinaryPrimitives.ReadInt32LittleEndian(body)}");
                        break;
                    }

                    case OpCode.Close:
                        Console.Error.WriteLine("[neural-host] trainer closed");
                        return 0;

                    default:
                        SendError(frames, $"unexpected opcode {op}");
                        return 2;
                }
            }
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"[neural-host] connection lost: {e.Message}");
            return 1;
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"[neural-host] bad frame: {e.Message}");
            return 2;
        }
        catch (Exception e)
        {
            // Anything the environment throws must reach the trainer as a MESSAGE, not as a dead socket.
            // Without this the process just exits and the client reports "connection forcibly closed",
            // which says nothing about which of reset, step or bake actually failed — and the host's own
            // stderr is normally suppressed, so the reason is gone entirely.
            Console.Error.WriteLine($"[neural-host] {e.GetType().Name}: {e.Message}");
            Console.Error.WriteLine(e.StackTrace);
            try { frames.Write(OpCode.Error, Encoding.UTF8.GetBytes($"{e.GetType().Name}: {e.Message}")); }
            catch (IOException) { /* the trainer is already gone */ }
            return 3;
        }

        return 0;
    }

    // =============================================================================================
    // benchmark
    // =============================================================================================

    /// <summary>
    /// Run the env with random actions and report steps/s. No trainer, no socket: this is the number that
    /// says whether a training run is worth starting on this machine, and it is the first thing to re-measure
    /// after any change to the observation or the course generator.
    /// </summary>
    private static int RunBenchmark(Options opts)
    {
        var cfg = new TrainingEnv.Config
        {
            Agents = opts.Agents,
            TicksPerStep = opts.TicksPerStep,
            Stage = (CourseGenerator.Stage)opts.Stage,
            Seed = opts.Seed,
            TraceFan = !opts.NoTraceFan,
            DataRoot = opts.DataRoot,
            MapList = opts.MapList,
            // Mirror train.py's per-stage settings, or the bench measures a harder problem than the one
            // being trained. Stages 1 and 2 are pure locomotion: handing them the movement weapons only
            // adds an action dimension with no reward attached, and under RANDOM actions it adds a death
            // — 5% attack chance with a devastator in hand is a rocket at your own feet.
            WeaponChance = opts.Stage <= 2 ? 0f : 1f,
            PermitFlipChance = opts.Stage <= 3 ? 0f : 0.35f,
            AimConstraintChance = opts.Stage <= 2 ? 0f : 0.4f,
        };
        var env = new TrainingEnv(cfg) { Log = m => Console.Error.WriteLine(m) };
        PolicyNetwork? benchPolicy = null;
        PolicyNetwork.Scratch? benchScratch = null;
        float[]? benchLogits = null;

        // --policy scores an exported weight file on this stage's courses, running it through the same
        // locomotor the live server uses. The random and --scripted arms are the two reference points it
        // has to sit between: below random means broken, below scripted means not worth shipping.
        if (opts.PolicyPath is not null)
        {
            PolicyNetwork? net = PolicyNetwork.Load(opts.PolicyPath, out string? err);
            if (net is null)
            {
                Console.Error.WriteLine($"[bench] cannot load {opts.PolicyPath}: {err}");
                return 1;
            }
            if (net.InputSize != NeuralObservation.Size || net.OutputSize != ActionSpace.Size)
            {
                Console.Error.WriteLine($"[bench] {opts.PolicyPath} is {net.InputSize}x{net.OutputSize}, " +
                                        $"this build needs {NeuralObservation.Size}x{ActionSpace.Size}");
                return 1;
            }
            if (opts.PolicyExternal)
            {
                // Evaluate the SAME network here, but push the action through the trainer's external path:
                // observation out, action back in, one round trip later. Isolates the path from the policy.
                benchPolicy = net;
                benchScratch = new PolicyNetwork.Scratch(net);
                benchLogits = new float[ActionSpace.Size];
            }
            else
            {
                env.SetEvalPolicy(net);
            }
            Console.Error.WriteLine($"[bench] policy '{net.Label}' ({net.ParameterCount:N0} parameters)" +
                                    (opts.PolicyExternal ? " via the EXTERNAL action path" : " in-locomotor"));
        }

        int obsSize = TrainingEnv.ObservationSize;
        var observations = new float[cfg.Agents * obsSize];
        var rewards = new float[cfg.Agents];
        var dones = new byte[cfg.Agents];
        var truncated = new byte[cfg.Agents];
        var actions = new float[cfg.Agents * ActionEncoding.Size];
        var rng = new Random(opts.Seed);

        Console.Error.WriteLine($"[bench] stage {cfg.Stage}, {cfg.Agents} agents, {cfg.TicksPerStep} ticks/step, " +
                                $"trace fan {(cfg.TraceFan ? "on" : "off")}, obs {obsSize}, " +
                                $"actions {(opts.PolicyPath is not null ? "POLICY" : opts.Scripted ? "SCRIPTED forward" : "random")}");

        env.Reset(observations);

        // A warm-up episode so the JIT and the first-touch allocations are not in the measurement.
        for (int i = 0; i < 60; i++)
        {
            if (benchPolicy is not null) PolicyActions(benchPolicy, benchScratch!, benchLogits!, observations, actions, cfg.Agents);
            else if (opts.Scripted) ForwardActions(actions, cfg.Agents, i);
            else RandomActions(rng, actions, cfg.Agents);
            env.Step(actions, observations, rewards, dones, truncated);
            if (env.AllDone()) env.Reset(observations);
        }

        var sw = Stopwatch.StartNew();
        int steps = 0, episodes = 0, arrivedTotal = 0, agentEpisodes = 0;
        double resetMsTotal = 0;
        double rewardSum = 0, remainingSum = 0;
        int[] bucketArrived = new int[TrainingEnv.RouteBuckets.Length];
        int[] bucketAttempts = new int[TrainingEnv.RouteBuckets.Length];

        // Bounded by EPISODES when --bench-episodes is given, by steps otherwise.
        //
        // A step budget makes an eval a lottery. The course sequence is seeded, so every run sees the same
        // courses in the same order -- but a faster policy finishes them faster and so reaches MORE of them
        // inside the budget, while a slower one times out on the early ones. The two are scored on
        // different slices of the distribution. Measured on consecutive evals 25 updates apart, that swung
        // stage 3 between 20% and 59%, and a curriculum gate reading those numbers passes on luck.
        //
        // An episode budget scores every eval on the identical set of courses.
        while (opts.BenchEpisodes > 0 ? episodes < opts.BenchEpisodes && steps < opts.BenchSteps
                                      : steps < opts.BenchSteps)
        {
            if (benchPolicy is not null) PolicyActions(benchPolicy, benchScratch!, benchLogits!, observations, actions, cfg.Agents);
            else if (opts.Scripted) ForwardActions(actions, cfg.Agents, steps);
            else RandomActions(rng, actions, cfg.Agents);
            env.Step(actions, observations, rewards, dones, truncated);
            for (int i = 0; i < rewards.Length; i++) rewardSum += rewards[i];
            steps++;
            if (opts.Debug && steps % 60 == 0)
                Console.Error.WriteLine($"[dbg {steps,5}] {env.DebugAgent0()}");
            if (env.AllDone())
            {
                (int arrived, float meanTime, float meanRemaining) = env.EpisodeSummary();
                arrivedTotal += arrived;
                agentEpisodes += cfg.Agents;
                remainingSum += meanRemaining;
                (int[] bArr, int[] bAtt) = env.ArrivalByRouteLength();
                for (int b = 0; b < bArr.Length; b++) { bucketArrived[b] += bArr[b]; bucketAttempts[b] += bAtt[b]; }
                if (episodes < 6)
                    Console.Error.WriteLine($"[bench] episode {episodes}: {arrived}/{cfg.Agents} arrived, " +
                                            $"mean arrival {meanTime:F1}s, mean distance left {meanRemaining:F0} qu");
                long _rs = Stopwatch.GetTimestamp();
                env.Reset(observations);
                resetMsTotal += (Stopwatch.GetTimestamp() - _rs) * 1000.0 / Stopwatch.Frequency;
                episodes++;
            }
        }
        sw.Stop();

        double sec = sw.Elapsed.TotalSeconds;
        double stepsPerSec = steps / sec;
        double agentStepsPerSec = stepsPerSec * cfg.Agents;
        double simSecondsPerSec = stepsPerSec * cfg.TicksPerStep / 72.0;

        Console.Error.WriteLine("[bench] arrival by route length:");
        for (int b = 0; b < bucketAttempts.Length; b++)
        {
            if (bucketAttempts[b] == 0) continue;
            string lo = b == 0 ? "0" : $"{TrainingEnv.RouteBuckets[b - 1]:F0}";
            string hi = float.IsInfinity(TrainingEnv.RouteBuckets[b]) ? "inf" : $"{TrainingEnv.RouteBuckets[b]:F0}";
            Console.Error.WriteLine($"[bench]   {lo,5}-{hi,-5} qu  {bucketArrived[b],5}/{bucketAttempts[b],-5} " +
                                    $"{100.0 * bucketArrived[b] / bucketAttempts[b],5:F1}%");
        }

        Console.WriteLine($"steps          {steps}");
        Console.WriteLine($"episodes       {episodes}");
        Console.WriteLine($"wall seconds   {sec:F2}");
        Console.WriteLine($"  of which:    {resetMsTotal / 1000.0:F2} s in {episodes} resets " +
                          $"({resetMsTotal / Math.Max(1, episodes):F0} ms each), " +
                          $"{sec - resetMsTotal / 1000.0:F2} s stepping");
        Console.WriteLine($"steps/s        {stepsPerSec:F0}");
        Console.WriteLine($"agent-steps/s  {agentStepsPerSec:F0}");
        Console.WriteLine($"realtime x     {simSecondsPerSec:F1}");
        Console.WriteLine($"mean reward    {rewardSum / Math.Max(1, steps * cfg.Agents):F4}");
        Console.WriteLine($"arrival rate   {arrivedTotal / (double)Math.Max(1, agentEpisodes):P1} " +
                          $"({arrivedTotal}/{agentEpisodes} agent-episodes)");
        Console.WriteLine($"distance left  {remainingSum / Math.Max(1, episodes):F0} qu mean at episode end");
        return 0;
    }

    /// <summary>
    /// Evaluate the network on the observations the env just returned and encode the argmax action onto the
    /// wire, exactly as the Python trainer does. Same network, same decode, the trainer's timing.
    /// </summary>
    private static void PolicyActions(PolicyNetwork net, PolicyNetwork.Scratch scratch, float[] logits,
        float[] observations, float[] actions, int agents)
    {
        int obsSize = TrainingEnv.ObservationSize;
        for (int i = 0; i < agents; i++)
        {
            net.Evaluate(observations.AsSpan(i * obsSize, obsSize), scratch, logits);
            NeuralAction a = ActionSpace.Decode(logits, weaponAllowed: true, stackalloc bool[] { true, true, true });
            int o = i * ActionEncoding.Size;
            actions[o + 0] = MoveIndexOf(a);
            actions[o + 1] = a.Jump ? 1f : 0f;
            actions[o + 2] = a.Crouch ? 1f : 0f;
            actions[o + 3] = a.Attack1 ? 1f : 0f;
            actions[o + 4] = a.Attack2 ? 1f : 0f;
            actions[o + 5] = a.WeaponSelect + 1;
            actions[o + 6] = a.YawDelta / NeuralAction.MaxYawRate;
            actions[o + 7] = a.PitchDelta / NeuralAction.MaxPitchRate;
        }
    }

    /// <summary>Reverse the nine-way wishmove table, so a decoded action can go back on the wire.</summary>
    private static int MoveIndexOf(in NeuralAction a)
    {
        for (int i = 0; i < ActionEncoding.MoveTable.Length; i++)
        {
            (float f, float r) = ActionEncoding.MoveTable[i];
            if (MathF.Abs(f - a.MoveForward) < 1e-3f && MathF.Abs(r - a.MoveRight) < 1e-3f) return i;
        }
        return 0;
    }

    private static void RandomActions(Random rng, float[] actions, int agents)
    {
        for (int i = 0; i < agents; i++)
        {
            int o = i * ActionEncoding.Size;
            actions[o + 0] = rng.Next(0, 9);              // wishmove
            actions[o + 1] = rng.Next(0, 2);              // jump
            actions[o + 2] = 0f;                          // crouch
            actions[o + 3] = rng.NextDouble() < 0.05 ? 1f : 0f;  // attack1
            actions[o + 4] = 0f;                          // attack2
            // Exercise the weapon head too. A sampled policy picks from it constantly, and leaving it at
            // "keep current" here meant the bench never touched Inventory.SwitchWeapon — a whole code path
            // the trainer hits every step and the bench claimed to cover.
            actions[o + 5] = rng.Next(0, 4);
            actions[o + 6] = (float)(rng.NextDouble() * 2.0 - 1.0);
            actions[o + 7] = (float)(rng.NextDouble() * 0.4 - 0.2);
        }
    }

    /// <summary>
    /// The scripted baseline: hold forward, jump on a fixed cadence, never turn.
    ///
    /// <para>Wishmove is chosen in the GOAL frame, so "forward" is literally "toward the target" whatever
    /// the view is doing. That makes this close to optimal on stage 1 and a serviceable bunnyhopper on
    /// stage 2, which is exactly what a diagnostic needs: if THIS cannot arrive, the environment is broken
    /// and no amount of training will help. It is also the scripted baseline the design doc asks the
    /// learned policy to beat.</para>
    /// </summary>
    private static void ForwardActions(float[] actions, int agents, int step)
    {
        // Jump every 8th policy step. At 4 ticks per step and 72 Hz that is a hop about every 0.44 s,
        // roughly the ground contact rhythm a bunnyhop chain wants.
        float jump = step % 8 == 0 ? 1f : 0f;
        for (int i = 0; i < agents; i++)
        {
            int o = i * ActionEncoding.Size;
            actions[o + 0] = 1f;    // index 1 = straight forward in the goal frame
            actions[o + 1] = jump;
            actions[o + 2] = 0f;
            actions[o + 3] = 0f;
            actions[o + 4] = 0f;
            actions[o + 5] = 0f;
            actions[o + 6] = 0f;
            actions[o + 7] = 0f;
        }
    }

    // =============================================================================================
    // payload helpers
    // =============================================================================================

    private static TrainingEnv.Config ReadHello(ReadOnlySpan<byte> body, out int version)
    {
        version = BinaryPrimitives.ReadInt32LittleEndian(body);
        return new TrainingEnv.Config
        {
            Agents = BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
            TicksPerStep = BinaryPrimitives.ReadInt32LittleEndian(body[8..]),
            MaxSteps = BinaryPrimitives.ReadInt32LittleEndian(body[12..]),
            Stage = (CourseGenerator.Stage)BinaryPrimitives.ReadInt32LittleEndian(body[16..]),
            Seed = BinaryPrimitives.ReadInt32LittleEndian(body[20..]),
            WeaponChance = BinaryPrimitives.ReadSingleLittleEndian(body[24..]),
            PermitFlipChance = BinaryPrimitives.ReadSingleLittleEndian(body[28..]),
            AimConstraintChance = BinaryPrimitives.ReadSingleLittleEndian(body[32..]),
            TraceFan = BinaryPrimitives.ReadInt32LittleEndian(body[36..]) != 0,
            DataRoot = ReadPrefixedString(body, 40, out int after),
            MapList = ReadPrefixedString(body, after, out _),
        };
    }

    /// <summary>A u16 length followed by that many UTF-8 bytes. Stage 6's map settings ride the HELLO frame.</summary>
    private static string ReadPrefixedString(ReadOnlySpan<byte> body, int offset, out int next)
    {
        if (offset + 2 > body.Length) { next = body.Length; return ""; }
        int len = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
        int start = offset + 2;
        if (start + len > body.Length) { next = body.Length; return ""; }
        next = start + len;
        return len == 0 ? "" : Encoding.UTF8.GetString(body.Slice(start, len));
    }

    private static void SendError(Frames frames, string message)
    {
        Console.Error.WriteLine($"[neural-host] error: {message}");
        frames.Write(OpCode.Error, Encoding.UTF8.GetBytes(message));
    }

    private static ReadOnlySpan<byte> AsBytes(float[] a)
        => System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan());

    private static void CopyToFloats(ReadOnlySpan<byte> src, float[] dest)
        => src.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(dest.AsSpan()));

    private static void AppendBytes(List<byte> list, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) list.Add(b);
    }

    private static void AppendI32(List<byte> list, int v)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, v);
        AppendBytes(list, tmp);
    }

    private static void AppendF32(List<byte> list, float v)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(tmp, v);
        AppendBytes(list, tmp);
    }

    // =============================================================================================
    // CLI
    // =============================================================================================

    private sealed class Options
    {
        public int Port;
        public int Agents = 8;
        public int TicksPerStep = 4;
        public int Stage = 1;
        public int Seed = 1;
        public int BenchSteps;
        public int BenchEpisodes;
        public string? VerifyWeights;
        public bool NoTraceFan;
        public bool Scripted;
        public bool Debug;
        public string? PolicyPath;
        public bool PolicyExternal;
        public string DataRoot = "";
        public string MapList = "";
        public bool ShowHelp;

        public const string Usage = """
            va-neural-host — the reinforcement-learning environment host for neural bots.

              --port N        listen on 127.0.0.1:N (0 = pick one; the chosen port is printed to stdout)
              --agents N      agents in the world (default 8)
              --ticks N       sim ticks per policy step (default 4 = an 18 Hz decision rate)
              --stage N       curriculum stage 1-6 (flat, corridor, terrain, furniture, weapon-gaps,
                              real-maps)
              --data DIR      content root for stage 6 (the directory holding maps/)
              --maps A,B,C    stage 6 map list; empty means every installed map. The held-out
                              eval split is removed either way, whatever this says.
              --seed N        base RNG seed (default 1)
              --bench N       no trainer: run N steps with random actions and report throughput
              --bench-episodes N
                              stop after N episodes instead of N steps (--bench then caps the
                              run). Scores every eval on the SAME courses, which a step budget
                              does not: a faster policy reaches more of the sequence inside a
                              step budget and is scored on a different slice.
              --verify-weights PATH
                              load an exported policy, time a forward pass, and report whether
                              this build can use it (run after every export)
              --no-tracefan   skip the per-think box sweeps (faster, and not what the live server does)
              --scripted      bench with a hold-forward policy instead of random actions: the
                              sanity check that the environment is solvable at all, and the
                              scripted baseline the learned policy has to beat
              --policy PATH   score an exported weight file on this stage's courses, run through
                              the same locomotor the live server uses. Compare its arrival rate
                              against the random and --scripted arms on the same --stage/--seed.
              --help

            The trainer normally launches these; see tools/neural/train.py.
            """;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                switch (a)
                {
                    case "--port": o.Port = ParseInt(Next(), 0); break;
                    case "--agents": o.Agents = Math.Clamp(ParseInt(Next(), 8), 1, 64); break;
                    case "--ticks": o.TicksPerStep = Math.Clamp(ParseInt(Next(), 4), 1, 32); break;
                    case "--stage": o.Stage = Math.Clamp(ParseInt(Next(), 1), 1, 6); break;
                    case "--data": o.DataRoot = Next() ?? ""; break;
                    case "--maps": o.MapList = Next() ?? ""; break;
                    case "--seed": o.Seed = ParseInt(Next(), 1); break;
                    case "--bench": o.BenchSteps = ParseInt(Next(), 1000); break;
                    case "--bench-episodes": o.BenchEpisodes = ParseInt(Next(), 0); break;
                    case "--verify-weights": o.VerifyWeights = Next(); break;
                    case "--no-tracefan": o.NoTraceFan = true; break;
                    case "--scripted": o.Scripted = true; break;
                    case "--debug": o.Debug = true; break;
                    case "--policy": o.PolicyPath = Next(); break;
                    case "--policy-external": o.PolicyExternal = true; break;
                    case "--help" or "-h": o.ShowHelp = true; break;
                }
            }
            return o;
        }

        private static int ParseInt(string? s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }
}
