using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace VortexArena.Engine.Particles;

/// <summary>
/// Runs a <see cref="ParticleSim"/>'s work - applying the spawns a frame asked for, and the per-frame update -
/// on a thread of its own while the caller's thread goes on with the frame, and hands the result back in an
/// order that makes the outcome the same as doing it all in place.
///
/// <para><b>Why the outcome is the same.</b> To the simulation a session is one sequence: spawn, spawn,
/// update, spawn, update, ... Nothing in it depends on WHEN a step runs, only on the order (a spawn reads the
/// simulation's clock, which only an update moves; the random numbers are drawn in step order). The runner
/// keeps that order and moves the steps: <see cref="Spawn"/> only records the request, with the cvars it
/// would have read (<see cref="ParticleSim.ReadSpawnSettings"/>, taken at the call, on the caller's thread);
/// <see cref="Begin"/> hands the worker every request recorded so far, in order, followed by the update;
/// <see cref="Join"/> waits for the worker and delivers what those steps raised (a mark on a wall, a beam), in
/// the order raised, to the owner's handlers on the owner's thread. Anything that reads or changes the
/// simulation directly goes through <see cref="Flush"/> first, which also applies the requests recorded since
/// the last Begin, so what it sees is what in-place execution would show at that point.
/// <c>ParticleSimRunnerTests</c> compares the pool with an in-place twin's, byte for byte, every frame.</para>
///
/// <para><b>What the worker may touch.</b> Only the simulation, its own random source and its own tracer. The
/// tracer must not share scratch state with anything the owner's thread uses meanwhile: give the simulation
/// a tracer over <c>CollisionWorld.ShareForThread()</c>.</para>
///
/// <para>With <see cref="Threaded"/> false every step runs in place at its call, as without a runner.</para>
/// </summary>
public sealed class ParticleSimRunner : IDisposable
{
    private readonly ParticleSim _sim;
    private List<SpawnRequest> _pending = new(), _working = new();
    private readonly List<Deferred> _events = new();
    private readonly ManualResetEventSlim _go = new(false, spinCount: 2000), _done = new(true, spinCount: 2000);
    private Thread? _thread;
    private volatile bool _inFlight, _stop;
    private volatile Exception? _failure;
    private float _time;
    private ParticleSim.UpdateSettings _settings;
    private Action? _afterUpdate;

    private readonly struct SpawnRequest
    {
        public readonly IReadOnlyList<ParticleEmitterInfo> Blocks;
        public readonly float Count, Fade;
        public readonly Vector3 OriginMins, OriginMaxs, VelocityMins, VelocityMaxs;
        public readonly uint Tint;
        public readonly bool WantTrail;
        public readonly ParticleSim.SpawnSettings Settings;

        public SpawnRequest(IReadOnlyList<ParticleEmitterInfo> blocks, float count, Vector3 originMins, Vector3 originMaxs,
            Vector3 velocityMins, Vector3 velocityMaxs, uint tint, float fade, bool wantTrail, in ParticleSim.SpawnSettings settings)
        {
            Blocks = blocks; Count = count; OriginMins = originMins; OriginMaxs = originMaxs;
            VelocityMins = velocityMins; VelocityMaxs = velocityMaxs; Tint = tint; Fade = fade; WantTrail = wantTrail; Settings = settings;
        }
    }

    private readonly struct Deferred
    {
        public readonly bool IsBeam;
        public readonly StainEvent Stain;
        public readonly BeamEvent Beam;

        public Deferred(in StainEvent stain) { IsBeam = false; Stain = stain; Beam = default; }
        public Deferred(in BeamEvent beam) { IsBeam = true; Stain = default; Beam = beam; }
    }

    public ParticleSimRunner(ParticleSim sim)
    {
        _sim = sim ?? throw new ArgumentNullException(nameof(sim));
        // The simulation raises its events to the runner; the runner hands them on - at once when the caller's
        // thread raised them, at Join when the worker did.
        _sim.OnStain = Stain;
        _sim.OnBeam = Beam;
    }

    /// <summary>Where a mark the simulation wants drawn goes. Always called on the owner's thread.</summary>
    public Action<StainEvent>? OnStain { get; set; }

    /// <summary>Where a beam the simulation wants drawn goes. Always called on the owner's thread.</summary>
    public Action<BeamEvent>? OnBeam { get; set; }

    /// <summary>False runs every step in place on the caller's thread. Change it only after a <see cref="Flush"/>.</summary>
    public bool Threaded { get; set; } = true;

    /// <summary>True between <see cref="Begin"/> and the <see cref="Join"/> that follows it.</summary>
    public bool InFlight => _inFlight;

    /// <summary>Spawns recorded and not yet handed to the worker.</summary>
    public int PendingSpawns => _pending.Count;

    private void Stain(StainEvent e)
    {
        if (_inFlight && Thread.CurrentThread == _thread) _events.Add(new Deferred(e));
        else OnStain?.Invoke(e);
    }

    private void Beam(BeamEvent e)
    {
        if (_inFlight && Thread.CurrentThread == _thread) _events.Add(new Deferred(e));
        else OnBeam?.Invoke(e);
    }

    /// <summary>
    /// <see cref="ParticleSim.SpawnEffect(IReadOnlyList{ParticleEmitterInfo}, float, Vector3, Vector3, Vector3, Vector3, uint, float, bool)"/>,
    /// recorded for the worker (or applied now when not <see cref="Threaded"/>). The cvars are read here; the
    /// block list is kept by reference until the request is applied.
    /// </summary>
    public void Spawn(IReadOnlyList<ParticleEmitterInfo> blocks, float count, Vector3 originMins, Vector3 originMaxs,
        Vector3 velocityMins, Vector3 velocityMaxs, uint tint = 0xFFFFFFFFu, float fade = 1f, bool wantTrail = false)
    {
        if (blocks is null || blocks.Count == 0) return;
        ParticleSim.SpawnSettings settings = _sim.ReadSpawnSettings();
        if (!Threaded)
        {
            _sim.SpawnEffect(blocks, count, originMins, originMaxs, velocityMins, velocityMaxs, tint, fade, wantTrail, settings);
            return;
        }
        _pending.Add(new SpawnRequest(blocks, count, originMins, originMaxs, velocityMins, velocityMaxs, tint, fade, wantTrail, settings));
    }

    /// <summary>
    /// Starts, on the worker: the spawns recorded so far, in order; then <c>sim.Update(time, settings)</c>; then
    /// <paramref name="afterUpdate"/> (the owner's reading of the pool for drawing - it must touch nothing the
    /// owner's thread uses before Join). Joins a run still in flight first.
    /// </summary>
    public void Begin(float time, in ParticleSim.UpdateSettings settings, Action? afterUpdate = null)
    {
        Join();
        if (!Threaded)
        {
            Apply(_pending);
            _sim.Update(time, settings);
            afterUpdate?.Invoke();
            return;
        }
        if (_thread is null)
        {
            _thread = new Thread(Work) { IsBackground = true, Name = "particle-sim" };
            _thread.Start();
        }
        (_pending, _working) = (_working, _pending);
        _time = time;
        _settings = settings;
        _afterUpdate = afterUpdate;
        _done.Reset();
        _inFlight = true;
        _go.Set();
    }

    /// <summary>
    /// Waits for the run that <see cref="Begin"/> started and hands the marks and beams it raised to the
    /// handlers, in the order raised. Nothing to do, and cheap, when no run is in flight. Returns true if
    /// there was one. Spawns recorded since Begin stay recorded for the next Begin (see <see cref="Flush"/>).
    /// </summary>
    public bool Join()
    {
        if (!_inFlight) return false;
        _done.Wait();
        _inFlight = false;
        if (_failure is { } failure)
        {
            _failure = null;
            _events.Clear();
            _working.Clear();
            throw new InvalidOperationException("the particle run failed on its thread", failure);
        }
        // An index loop: a handler may ask for a spawn, which is recorded behind the rest.
        for (int i = 0; i < _events.Count; i++)
        {
            Deferred e = _events[i];
            if (e.IsBeam) OnBeam?.Invoke(e.Beam);
            else OnStain?.Invoke(e.Stain);
        }
        _events.Clear();
        return true;
    }

    /// <summary>
    /// <see cref="Join"/>, then every recorded spawn applied here and now: afterwards the simulation is as
    /// in-place execution would have it at this point. For whatever is about to read or change it directly.
    /// </summary>
    public void Flush()
    {
        Join();
        Apply(_pending);
    }

    private void Apply(List<SpawnRequest> requests)
    {
        // An index loop: a mark raised by a spawn may make its handler ask for another spawn.
        for (int i = 0; i < requests.Count; i++)
        {
            SpawnRequest s = requests[i];
            _sim.SpawnEffect(s.Blocks, s.Count, s.OriginMins, s.OriginMaxs, s.VelocityMins, s.VelocityMaxs, s.Tint, s.Fade, s.WantTrail, s.Settings);
        }
        requests.Clear();
    }

    private void Work()
    {
        while (true)
        {
            _go.Wait();
            _go.Reset();
            if (_stop) return;
            try
            {
                Apply(_working);
                _sim.Update(_time, _settings);
                _afterUpdate?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _failure = e;
            }
            _done.Set();
        }
    }

    public void Dispose()
    {
        if (_stop) return;
        try { Join(); }
        catch (InvalidOperationException) { }
        _stop = true;
        _go.Set();
        _thread?.Join(1000);
    }
}
