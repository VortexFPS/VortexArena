// Port of the CSQC half of qcsrc/common/mapobjects/func/pointparticles.qc (Draw_PointParticles,
// pointparticles.qc:166-233): the client emitters for func_pointparticles / func_sparks.
//
// A map emitter is NOT a stream of particles. Each frame the QuakeC works out a number of EMISSIONS
// (n = impulse * frametime; `for (i = random(); i <= n; ++i)`), and every emission is one whole
// pointparticles(effect, point, velocity, count) — a complete burst of the effectinfo effect, every block of
// it, at a random point of the entity's brush. Stormkeep's one emitter ("mdl" "sparks", impulse 4, count 6,
// velocity 50 -100 30) is therefore about four separate bursts a second of ninety sparks each, thrown
// sideways, slowed by the effect's air friction, bouncing, and gone within a second.
//
// Until 2026-10 each entity instead owned ONE continuous GpuParticles3D built from the effect's FIRST block,
// with the emission rate, a cone spread and the lifetime estimated from it — no air friction, no bounce, no
// velocity stretch, not the effect's other blocks. On stormkeep that drew a steady curtain of long streaks
// falling straight down. Emissions now go through EffectSystem.Spawn like every other effect, so the
// faithful simulation (src/VortexArena.Engine/Particles/ParticleSim.cs) draws them exactly as it draws a
// weapon impact; the emission arithmetic itself is PointParticleEmitter (Godot-free, unit-tested).
//
// Scans the ambient entity facade like LaserRenderer/TriggerTouch.Predict*Ambient — live on the
// listen-server/demo paths only (a pure --connect client has no facade/BSP yet; the established seam).
//
// What still differs from the QuakeC (documented residuals): the per-emission .noise sound and the bgmscript
// envelope are not played; a ROTATED brush entity emits from its whole bounding box (the brush test is done
// for unrotated entities, which is every stock emitter).

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Particles;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>func_pointparticles/func_sparks emitters. Hosted by <see cref="ClientWorld"/>.</summary>
public partial class MapParticleEmitters : Node3D
{
    /// <summary>The effect system the emissions are played through.</summary>
    public EffectSystem? Effects { get; set; }

    private const float RescanInterval = 2f;

    // The particle simulation's clock advances by at most this much a frame (FaithfulParticleBackend); the
    // emitters use the same step so a stall does not arrive as one frame's worth of bursts all at once.
    private const float MaxStep = 0.05f;

    private sealed class MapEmitter
    {
        public Entity Entity = null!;
        public readonly PointParticleEmitter Emission = new();
        public bool WasActive;
        public NVec3 BuiltAt;                             // entity origin the box/brush were taken at
        // (draws 2026-08-02) PVS gating state: the cluster of the emitter's position (re-derived when it
        // moves — a train-mounted emitter changes rooms).
        public int PvsCluster = -2;                       // -2 = never derived
        public NVec3 PvsClusterAt;                        // origin the cluster was derived at
    }

    private readonly Dictionary<Entity, MapEmitter> _emitters = new();
    private readonly List<NVec3> _points = new();
    private readonly Random _rng = new();
    private float _rescanIn;

    public override void _Process(double delta)
    {
        if (Api.Services is null)
            return;

        using var _prof = FrameProfiler.Scope("emitters");

        _rescanIn -= (float)delta;
        if (_rescanIn <= 0f)
        {
            Rescan();
            _rescanIn = RescanInterval;
        }

        EffectSystem? fx = Effects;
        if (fx is null || _emitters.Count == 0)
            return;

        // #30 slowmo/pause: the same scaled step the particle simulation ages on, so the bursts-per-second
        // stay in step with the particles (frozen at slowmo 0).
        float frametime = MathF.Min(ClientRenderTime.ScaleDelta((float)delta), MaxStep);
        if (!(frametime > 0f))
            return;

        // (draws 2026-08-02) PVS gate: an emitter in a room no current viewpoint can see emits nothing (courtfun
        // has 44 of these). Same conservative contract as the world cells — the culler answers "show" whenever
        // it cannot prove otherwise. `r_pvs_cull_emitters 0` restores always-on for A/B.
        var culler = VortexArena.Game.WorldPvsCuller.Instance;
        bool pvsGate = culler is { CullActive: true }
            && Api.Cvars.GetFloat("r_pvs_cull_emitters") != 0f;

        foreach (MapEmitter em in _emitters.Values)
        {
            Entity e = em.Entity;
            if (e.IsFreed)
                continue;

            // QC: "if (i && !this.impulse) this.just_toggled = 1" — the entity went from off to on.
            bool active = e.Active == MapMover.ActiveActive;
            if (active && !em.WasActive)
                em.Emission.JustToggled = true;
            em.WasActive = active;
            if (!active)
                continue;

            if (e.Origin != em.BuiltAt)
                Place(em);

            if (pvsGate)
            {
                // Re-derive the cluster only when the emitter moved (~all are static; 32qu covers mover sway).
                if (em.PvsCluster == -2 || NVec3.DistanceSquared(e.Origin, em.PvsClusterAt) > 32f * 32f)
                {
                    em.PvsCluster = culler!.ClusterAt((em.Emission.BoxMin + em.Emission.BoxMax) * 0.5f);
                    em.PvsClusterAt = e.Origin;
                }
                if (!culler!.ClusterVisibleFromView(em.PvsCluster))
                    continue;
            }

            if (em.Emission.Step(frametime, _rng, _points) == 0)
                continue;

            foreach (NVec3 point in _points)
                Emit(fx, e, point);
        }
    }

    /// <summary>One emission: Draw_PointParticles' <c>__pointparticles(eff, p, velocity, count)</c>.</summary>
    private void Emit(EffectSystem fx, Entity e, NVec3 p)
    {
        NVec3 velocity = e.Velocity;
        if (e.MoveDir != NVec3.Zero)
        {
            // traceline(p, p + normalize(movedir) * 4096, 0, NULL): the emission moves to the surface the
            // entity points at and leaves along its normal at |movedir|.
            ITraceService trace = fx.FaithfulParticles?.Sim.Trace ?? Api.Trace;
            TraceResult tr = trace.Trace(p, NVec3.Zero, NVec3.Zero,
                p + VortexArena.Common.Math.QMath.Normalize(e.MoveDir) * 4096f, MoveFilter.WorldOnly, e);
            p = tr.EndPos;
            velocity += tr.PlaneNormal * e.MoveDir.Length();
        }
        if (e.ParticleJitter != 0f)
            velocity += PointParticleEmitter.RandomVec(_rng) * e.ParticleJitter;   // randomvec() * waterlevel
        fx.Spawn(e.Mdl, p, velocity, e.ParticleCount);
    }

    // =================================================================================================
    //  Facade scan
    // =================================================================================================

    private void Rescan()
    {
        Scan("func_pointparticles");
        Scan("func_sparks");

        List<Entity>? dead = null;
        foreach (var kv in _emitters)
            if (kv.Key.IsFreed)
                (dead ??= new List<Entity>()).Add(kv.Key);
        if (dead is not null)
            foreach (Entity e in dead)
                _emitters.Remove(e);
    }

    private void Scan(string className)
    {
        foreach (Entity e in Api.Entities.FindByClass(className))
        {
            if (e.IsFreed || _emitters.ContainsKey(e) || string.IsNullOrEmpty(e.Mdl))
                continue;
            var em = new MapEmitter { Entity = e, WasActive = e.Active == MapMover.ActiveActive };
            Place(em);
            _emitters[e] = em;
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[MapEmitters] {className} '{e.Mdl}': {em.Emission.Impulse:0.###} emissions/s ({em.Emission.Absolute}), " +
                $"count {e.ParticleCount:0.###}, velocity {e.Velocity}, box {em.Emission.BoxMin}..{em.Emission.BoxMax}, " +
                $"{(em.Emission.Brushes is { } b ? b.Count + " brush(es), " + (b.Count > 0 ? b[0].Sides.Length : 0) + " planes in the first" : "no brush: whole box")}"));
        }
    }

    /// <summary>Take the emission box, the brush and the rate from the entity (again when it has moved).</summary>
    private static void Place(MapEmitter em)
    {
        Entity e = em.Entity;
        PointParticleEmitter emission = em.Emission;
        em.BuiltAt = e.Origin;
        emission.BoxMin = e.Origin + e.Mins;
        emission.BoxMax = e.Origin + e.Maxs;
        emission.BrushOrigin = e.Origin;
        emission.Brushes = null;
        // The entity's own brushes, so a point is taken from the brush and not from its bounding box
        // (WarpZoneLib_BoxTouchesBrush). Stormkeep's emitter is a tilted slab that fills about half its box.
        if (e.ModelIndex != 0 && e.Angles == NVec3.Zero
            && Api.Entities is VortexArena.Engine.Simulation.EntityService table
            && table.TryGetEntityBrushModel(e, out IReadOnlyList<Brush> brushes, out _))
            emission.Brushes = brushes;
        emission.Configure(e.Impulse, (e.SpawnFlags & PointParticles.ParticlesImpulse) != 0);
    }
}
