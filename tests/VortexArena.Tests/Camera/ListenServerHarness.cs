using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;

namespace XonoticGodot.Tests.Camera;

/// <summary>
/// Deterministic model of the LISTEN-SERVER per-frame loop (apparatus for the bhop/movement-inconsistency
/// diagnosis). It reproduces the TWO independent fixed-72Hz accumulators — the SERVER sim (soft-capped at
/// <see cref="ServerSoftCap"/> ticks/frame, rigid) and the CLIENT input/predict (proportional, capped at
/// <see cref="ClientInputBacklog"/>) — driven by an arbitrary per-frame dt stream (so we can feed it catharsis-like
/// variable fps + hitches). It uses the REAL <see cref="Reconciler"/> + <see cref="PlayerPhysics"/> so the measured
/// behavior reflects the shipping code, and exposes per-frame metrics: the reconcile CORRECTION (the rubberband
/// the camera shows) and the server/client hop times.
///
/// <para>Loop order mirrors NetGame._Process: server.Tick (consumes input queued LAST frame) → client.Poll
/// (in-process snapshot) → reconcile → client sample+send+predict → render. The server drains its queue per-tick
/// either ALL-at-once (per-frame mode, the default — first tick drains the whole batch, remaining soft-capped ticks
/// STARVE-REPEAT the last command) or ONE-per-tick (legacy). Toggles let us attribute the dominant cause.</para>
/// </summary>
public sealed class ListenServerHarness
{
    private const float Tic = 1f / 72f;

    // --- config / toggles ---
    public int ServerSoftCap = 4;                 // NetGame.cs:531 MaxTicksPerFrame on the interactive listen path
    public float ClientInputBacklog = 0.25f;      // NetGame MaxInputBacklog
    public bool PerFrameDrain = true;             // true = ProvideInputPerFrame (Bryan's default), false = legacy 1/tick
    public bool Lockstep = false;                 // FIX-PROBE: server runs exactly as many ticks as the client predicted (no soft cap)
    public bool CommandDriven = false;            // THE FIX (Path B): player advances ONLY by real client commands — never
                                                  // a fabricated starve-repeat on a soft-capped extra world tick. Makes
                                                  // client prediction and server authority apply the identical command
                                                  // sequence → zero divergence at any fps.
    public float MaxSpeed = 360f;
    public float Slowmo = 1f;                     // time scale: scales BOTH accumulators (server sim + client input)
    public int ClientCatchupCap = int.MaxValue;   // GRACEFUL HITCH RECOVERY: max input ticks the client drains per
                                                  // frame. int.MaxValue = old (drain the whole backlog at once → a
                                                  // one-frame lunge after a hitch). Set to the server tick budget so
                                                  // a hitch's backlog spreads over several frames in lockstep.

    public readonly record struct FrameMetric(
        int Frame, int ServerTicks, int ClientTicks, float ReconcileCorrection, float ServerZ, float ClientZ,
        bool ServerOnGround, float ClientX);

    public List<FrameMetric> Metrics { get; } = new();

    /// <summary>The server-authoritative and client-predicted player origins after the run (for slowmo distance checks).</summary>
    public Vector3 ServerOrigin => _serverState.Origin;
    public Vector3 ClientOrigin => _rec.Predicted.Origin;

    // server authoritative sim
    private PlayerPhysicsStep _server = null!;
    private PredictedState _serverState;
    private float _serverAccum;
    private uint _serverLastProcessedSeq;
    private InputCommand _serverLastCmd;
    private bool _serverHasLast;

    // network queue (client -> server), delivered the frame AFTER it is sent (server.Tick precedes SendInput)
    private readonly Queue<InputCommand> _queue = new();

    // client predictor
    private PredictionBuffer _buf = null!;
    private Reconciler _rec = null!;
    private PlayerPhysicsStep _client = null!;
    private float _clientInputAccum;

    private MutableClock _clock = null!;

    /// <summary>Run the loop over <paramref name="dtStream"/> with an auto-bhop input (forward + jump-when-onground).
    /// Returns when the stream is exhausted. Requires Api.Services to be set by the caller (analytic flat world).</summary>
    public void Run(IReadOnlyList<float> dtStream, AnalyticWorld world, MutableClock clock)
    {
        _clock = clock;
        _server = new PlayerPhysicsStep(new Vector3(-16, -16, -24), new Vector3(16, 16, 45));
        _client = new PlayerPhysicsStep(new Vector3(-16, -16, -24), new Vector3(16, 16, 45));
        _buf = new PredictionBuffer();
        _rec = new Reconciler(_buf, _client) { ErrorCompensation = 0f, TickRate = 72f }; // faithful default = SNAP

        // settle both onto the floor
        var start = new PredictedState { Origin = new Vector3(0, 0, 24), Velocity = Vector3.Zero, OnGround = true };
        float t = 0f;
        for (int i = 0; i < 20; i++) { t += Tic; clock.Time = t; var c = Idle(); _server.Step(ref start, in c, default); }
        _serverState = start;
        _rec.Predict(start, 0, default, t);

        uint seq = 0;
        float now = t;
        for (int f = 0; f < dtStream.Count; f++)
        {
            float dt = dtStream[f];
            now += dt;

            // ---- 1) SERVER tick: consume the queue, run soft-capped fixed ticks ----
            int serverTicks = ServerStep(dt, now);

            // ---- 2) client poll (snapshot) + reconcile (measure the correction the camera shows) ----
            clock.Time = now;
            PredictedState predBefore = _rec.Predicted;
            PredictedState snapshot = _serverState;
            _rec.Reconcile(snapshot, _serverLastProcessedSeq, default, now, predBefore);
            float correction = (_rec.Predicted.Origin - predBefore.Origin).Length();

            // ---- 3) client sample + send + predict (drain fixed input ticks) ----
            int clientTicks = 0;
            _clientInputAccum += dt * Slowmo; // slowmo scales the client command cadence (matches server TimeScale)
            if (_clientInputAccum > ClientInputBacklog) _clientInputAccum = ClientInputBacklog;
            while (_clientInputAccum >= Tic && clientTicks < ClientCatchupCap) // graceful: cap the per-frame catch-up
            {
                _clientInputAccum -= Tic;
                var cmd = AutoBhopInput(_rec.Predicted.OnGround);
                seq = _buf.Push(cmd);
                cmd.Seq = seq;
                _queue.Enqueue(cmd);            // delivered to the server NEXT frame
                clientTicks++;
            }
            _rec.Predict(snapshot, _serverLastProcessedSeq, default, now);

            Metrics.Add(new FrameMetric(f, serverTicks, clientTicks, correction,
                _serverState.Origin.Z, _rec.Predicted.Origin.Z, _serverState.OnGround, _rec.Predicted.Origin.X));
        }
    }

    // Server: accumulate dt, run min(wanted, softCap) fixed ticks. Per-frame drain: the first tick consumes the
    // whole queued batch (one movement step per command); remaining soft-capped ticks STARVE-REPEAT the last
    // command (held keys keep moving). Legacy: one command per tick. Lockstep probe: run exactly the queued count.
    private int ServerStep(float dt, float now)
    {
        // move last frame's sent commands into the server's reachable queue (server.Tick precedes SendInput).
        // (already enqueued at send time; _queue holds them.)
        _serverAccum += dt * Slowmo; // slowmo scales the server world tick rate (SimulationLoop.TimeScale)
        if (_serverAccum > 16 * Tic) _serverAccum = 0f; // spiral guard (SimulationLoop drops past 16)

        int wanted = (int)(_serverAccum / Tic);
        int cap = Lockstep ? wanted : ServerSoftCap;
        int ticks = Math.Min(wanted, cap);
        _serverAccum -= ticks * Tic;

        for (int i = 0; i < ticks; i++)
        {
            clockAdvanceServer(now);
            if (PerFrameDrain)
            {
                if (i == 0)
                {
                    // drain ALL queued commands this first tick (ProvideInputPerFrame batch)
                    if (_queue.Count > 0)
                        while (_queue.Count > 0) { InputCommand c = _queue.Dequeue(); StepServer(c); }
                    else if (!CommandDriven && _serverHasLast)
                        StepServer(_serverLastCmd);                 // OLD: starve-repeat (fabricates) — the bug
                    // CommandDriven: empty queue → the player does NOT move this tick (no fabrication)
                }
                else if (!CommandDriven && _serverHasLast)
                {
                    StepServer(_serverLastCmd);                     // OLD: soft-capped extra tick → starve-repeat (the bug)
                }
                // CommandDriven: soft-capped extra ticks advance the WORLD but NOT the player (command-driven movement)
            }
            else
            {
                // legacy: one command per tick, with the InputQueuePolicy trim when the queue grows
                if (_queue.Count > InputQueuePolicy.MaxLegacyQueuedCommands)
                {
                    // trim oldest down to residual (acked unsimulated) — the rubberband source on legacy
                    while (_queue.Count > InputQueuePolicy.LegacyTrimResidual)
                    {
                        InputCommand dropped = _queue.Dequeue();
                        if (dropped.Seq > _serverLastProcessedSeq) _serverLastProcessedSeq = dropped.Seq;
                    }
                }
                if (_queue.Count > 0) StepServer(_queue.Dequeue());
                else if (_serverHasLast) StepServer(_serverLastCmd);
            }
        }
        return ticks;
    }

    private void StepServer(InputCommand c)
    {
        InputCommand cmd = c; cmd.DeltaTime = Tic;
        _server.Step(ref _serverState, in cmd, default);
        _serverLastCmd = cmd; _serverHasLast = true;
        if (cmd.Seq > _serverLastProcessedSeq) _serverLastProcessedSeq = cmd.Seq;
    }

    private void clockAdvanceServer(float now) => _clock.Time = now;

    private InputCommand AutoBhopInput(bool predictedOnGround) => new()
    {
        ViewAngles = Vector3.Zero,
        Forward = 1f,
        Buttons = predictedOnGround ? (int)InputButtons.Jump : 0, // hold-jump auto-bhop
        DeltaTime = Tic,
    };

    private static InputCommand Idle() => new() { ViewAngles = Vector3.Zero, DeltaTime = Tic };
}
