using System;
using System.Collections.Generic;
using System.Numerics;

namespace VortexArena.Engine.Simulation;

/// <summary>The world contact a <see cref="RagdollTraceFn"/> reports: where the swept particle stopped and
/// the surface normal there. <see cref="Hit"/> false = the full move was clear.</summary>
public readonly struct RagdollTraceHit
{
    public readonly Vector3 End;
    public readonly Vector3 Normal;
    public readonly bool Hit;

    public RagdollTraceHit(Vector3 end, Vector3 normal, bool hit)
    {
        End = end; Normal = normal; Hit = hit;
    }
}

/// <summary>Sweep a particle (a small box of half-extent <paramref name="halfExtent"/>) from
/// <paramref name="start"/> to <paramref name="end"/> against the world. The driver adapts the client-world
/// TraceService; tests inject planes. Quake space, Quake units.</summary>
public delegate RagdollTraceHit RagdollTraceFn(Vector3 start, Vector3 end, float halfExtent);

/// <summary>
/// A tiny position-based (Verlet/Jakobsen) ragdoll solver for corpse visuals — deliberately NOT Godot physics:
/// the map has no Godot-physics collision (the trace world is the truth), and integrating ourselves means the
/// #30 pause/slowmo-scaled client clock drives it for free. Particles carry position + previous position;
/// constraints are sticks (bone segments / rigid braces) and one-sided distance limits (Jakobsen joint stops —
/// min keeps a head out of a chest, max stops hyperextension). World contact is a per-particle sweep through
/// the injected <see cref="RagdollTraceFn"/> with friction + restitution folded into the implied velocity.
///
/// Everything is in QUAKE WORLD space and Quake units (gravity 800 down −Z). Fixed substeps with an
/// accumulator (stability); <see cref="PositionLerped"/> interpolates render reads between substeps so a
/// 144 Hz frame doesn't see 60 Hz stepping. Sleeps when every particle settles — a slept ragdoll costs zero.
/// </summary>
public sealed class RagdollSolver
{
    /// <summary>Fixed integration substep (60 Hz — corpse flops don't need finer).</summary>
    public const float SubstepSeconds = 1f / 60f;

    private const int MaxSubstepsPerCall = 4;   // a hitch simulates at most this much per frame, then drops backlog
    private const int ConstraintIterations = 8;
    private const float AirDamping = 0.995f;    // per-substep velocity keep (light drag so flails settle)
    private const float Friction = 0.55f;       // tangential velocity removed per ground contact
    private const float Restitution = 0.2f;     // normal bounce kept per contact
    private const float SleepSpeed = 12f;       // u/s: below this for SleepAfter seconds → sleep
    private const float SleepAfter = 0.5f;
    private const float MaxSimSeconds = 12f;    // hard stop — a corpse still jittering by now sleeps anyway

    private readonly Vector3[] _pos;
    private readonly Vector3[] _prev;
    private readonly Vector3[] _renderFrom; // positions at the previous substep boundary (render interpolation)
    private readonly RagdollTraceFn _trace;
    private readonly float _gravity;

    private readonly List<(int A, int B, float Len)> _sticks = new();
    private readonly List<(int A, int B, float Min, float Max)> _limits = new();

    private float _accum;
    private float _sleepTimer;
    private float _age;

    /// <summary>Per-particle collision half-extent (Quake units).</summary>
    public float Radius = 3f;

    /// <summary>True once every particle has settled (or the hard cap hit); Step is a no-op after.</summary>
    public bool Sleeping { get; private set; }

    public int ParticleCount => _pos.Length;

    public RagdollSolver(IReadOnlyList<Vector3> seedPositions, RagdollTraceFn trace, float gravity = 800f)
    {
        _pos = new Vector3[seedPositions.Count];
        _prev = new Vector3[seedPositions.Count];
        _renderFrom = new Vector3[seedPositions.Count];
        for (int i = 0; i < _pos.Length; i++)
            _renderFrom[i] = _prev[i] = _pos[i] = seedPositions[i];
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _gravity = gravity;
    }

    /// <summary>Add a rigid stick between two particles; the rest length is their SEEDED distance (the death
    /// pose IS the rig's proportions — no per-model tables needed).</summary>
    public void AddStick(int a, int b) => _sticks.Add((a, b, Vector3.Distance(_pos[a], _pos[b])));

    /// <summary>Add a one-sided joint stop: the pair's distance is kept within
    /// [<paramref name="minFactor"/>, <paramref name="maxFactor"/>] × the seeded distance.</summary>
    public void AddLimit(int a, int b, float minFactor, float maxFactor)
    {
        float rest = Vector3.Distance(_pos[a], _pos[b]);
        _limits.Add((a, b, rest * minFactor, rest * maxFactor));
    }

    /// <summary>Add a joint stop with ABSOLUTE bounds (Quake units) — for caps derived from segment sums
    /// rather than the seeded end-to-end distance (a death pose may already be bent).</summary>
    public void AddLimitAbsolute(int a, int b, float min, float max) => _limits.Add((a, b, min, max));

    /// <summary>Seed a uniform initial velocity (u/s) — the corpse entity's toss velocity at death.</summary>
    public void SetVelocity(Vector3 v)
    {
        for (int i = 0; i < _pos.Length; i++)
            _prev[i] = _pos[i] - v * SubstepSeconds;
    }

    /// <summary>Add velocity to one particle (per-particle tumble/impulse noise on top of the uniform seed).</summary>
    public void AddParticleVelocity(int i, Vector3 dv) => _prev[i] -= dv * SubstepSeconds;

    /// <summary>The particle's position at the last completed substep (Quake world space).</summary>
    public Vector3 Position(int i) => _pos[i];

    /// <summary>The render-read position: interpolated across the substep accumulator so per-frame reads are
    /// smooth at any frame rate.</summary>
    public Vector3 PositionLerped(int i)
    {
        float alpha = Math.Clamp(_accum / SubstepSeconds, 0f, 1f);
        return Vector3.Lerp(_renderFrom[i], _pos[i], alpha);
    }

    /// <summary>
    /// Advance by <paramref name="dt"/> seconds of the pause/slowmo-scaled client clock (dt 0 = frozen).
    /// Substeps at the fixed rate; a spike simulates at most <see cref="MaxSubstepsPerCall"/> substeps and
    /// drops the rest (stability over catch-up, the gib/anim precedent of clamping to 0.1 s).
    /// </summary>
    public void Step(float dt)
    {
        if (Sleeping || !(dt > 0f))
            return;
        _accum += Math.Min(dt, 0.1f);

        int steps = 0;
        while (_accum >= SubstepSeconds && steps < MaxSubstepsPerCall)
        {
            Substep();
            _accum -= SubstepSeconds;
            steps++;
        }
        if (steps == MaxSubstepsPerCall && _accum >= SubstepSeconds)
            _accum = 0f; // drop the backlog a hitch built up
    }

    private void Substep()
    {
        const float h = SubstepSeconds;
        _age += h;

        // Verlet integrate (gravity + light drag). _prev becomes this substep's start position.
        for (int i = 0; i < _pos.Length; i++)
        {
            _renderFrom[i] = _pos[i];
            Vector3 v = (_pos[i] - _prev[i]) * AirDamping;
            v.Z -= _gravity * h * h;
            _prev[i] = _pos[i];
            _pos[i] += v;
        }

        // Relax the constraint network (equal-mass projection).
        for (int iter = 0; iter < ConstraintIterations; iter++)
        {
            foreach ((int a, int b, float rest) in _sticks)
                ProjectDistance(a, b, rest, rest);
            foreach ((int a, int b, float min, float max) in _limits)
                ProjectDistance(a, b, min, max);
        }

        // World contact: sweep each particle from its substep start to its solved position.
        for (int i = 0; i < _pos.Length; i++)
        {
            Vector3 from = _prev[i];
            Vector3 to = _pos[i];
            if (Vector3.DistanceSquared(from, to) < 1e-6f)
                continue; // resting — no trace spent
            RagdollTraceHit hit = _trace(from, to, Radius);
            if (!hit.Hit)
                continue;
            _pos[i] = hit.End;
            // Fold friction + restitution into the implied velocity (rewrite _prev).
            Vector3 v = _pos[i] - _prev[i] + (to - _pos[i]); // what the particle "wanted" this substep
            float vn = Vector3.Dot(v, hit.Normal);
            if (vn < 0f)
            {
                Vector3 vNormal = hit.Normal * vn;
                Vector3 vTangent = v - vNormal;
                v = vTangent * (1f - Friction) - vNormal * Restitution;
            }
            _prev[i] = _pos[i] - v;
        }

        // Sleep once everything settles (or the hard cap passes). NaN anywhere → bail to sleep (render keeps
        // the last good pose; a visual corpse must never propagate NaN into bone writes).
        float maxSq = 0f;
        bool bad = false;
        for (int i = 0; i < _pos.Length; i++)
        {
            float dSq = Vector3.DistanceSquared(_pos[i], _prev[i]);
            if (float.IsNaN(dSq)) { bad = true; break; }
            if (dSq > maxSq) maxSq = dSq;
        }
        if (bad)
        {
            for (int i = 0; i < _pos.Length; i++) _pos[i] = _prev[i] = _renderFrom[i];
            Sleeping = true;
            return;
        }
        float speed = MathF.Sqrt(maxSq) / h;
        _sleepTimer = speed < SleepSpeed ? _sleepTimer + h : 0f;
        if (_sleepTimer >= SleepAfter || _age >= MaxSimSeconds)
        {
            Sleeping = true;
            for (int i = 0; i < _pos.Length; i++) { _prev[i] = _pos[i]; _renderFrom[i] = _pos[i]; }
        }
    }

    /// <summary>Project a pair to keep its distance within [min, max] (equal shares; min == max = a stick).</summary>
    private void ProjectDistance(int a, int b, float min, float max)
    {
        Vector3 d = _pos[b] - _pos[a];
        float len = d.Length();
        if (len < 1e-6f)
        {
            if (min > 0f) _pos[b].Z += min * 0.5f; // coincident: split along Z, next iteration fixes direction
            return;
        }
        float target = len < min ? min : (len > max ? max : len);
        if (target == len)
            return;
        Vector3 corr = d * ((len - target) / len * 0.5f);
        _pos[a] += corr;
        _pos[b] -= corr;
    }
}
