// Port of the CSQC half of qcsrc/common/mapobjects/func/pointparticles.qc (Draw_PointParticles,
// pointparticles.qc:166-233): the persistent client emitters for func_pointparticles / func_sparks.
//
// DP re-spawns individual particles every draw frame (n = impulse * drawframetime emissions, each a
// __pointparticles of .count multiplier). Doing that through EffectSystem.Spawn would churn a transient
// GpuParticles3D node per frame per emitter (courtfun has 44) — so instead each map entity gets ONE
// long-lived continuous GpuParticles3D configured from its effectinfo block, with Amount sized so the
// steady-state particles-per-second matches DP's:
//     rate/sec = impulse (absolute) | -impulse * volume/64^3 (relative)
//     particles/sec = rate * (countabsolute + count * block.count)
//     Amount = particles/sec * particle lifetime (capped)
// PARTICLES_IMPULSE entities instead fire an absolute one-shot burst of .impulse on each toggle-ON
// (QC ABSOLUTE_ONLY_SPAWN_AT_TOGGLE).
//
// Scans the ambient entity facade like LaserRenderer/TriggerTouch.Predict*Ambient — live on the
// listen-server/demo paths only (a pure --connect client has no facade/BSP yet; the established seam).
//
// Known approximations (documented residuals): the WarpZoneLib_BoxTouchesBrush point-in-brush retry (we
// emit in the whole bbox), the per-emission .noise sound, bgmscript ADSR, and .movedir surface projection
// (approximated by ONE trace from the box center instead of per-particle traces).

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>Persistent func_pointparticles/func_sparks emitters. Hosted by <see cref="ClientWorld"/>.</summary>
public partial class MapParticleEmitters : Node3D
{
    /// <summary>Effectinfo block lookup + particle-mesh construction (shared catalog).</summary>
    public EffectSystem? Effects { get; set; }

    private const float RescanInterval = 2f;
    private const int MaxAmountPerEmitter = 1500;
    private const float DpGravity = 800f;

    private sealed class MapEmitter
    {
        public Entity Entity = null!;
        public GpuParticles3D Particles = null!;
        public bool ImpulseMode;     // PARTICLES_IMPULSE: one-shot burst on toggle-ON only
        public bool WasActive;
    }

    private readonly Dictionary<Entity, MapEmitter> _emitters = new();
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

        foreach (MapEmitter em in _emitters.Values)
        {
            Entity e = em.Entity;
            if (e.IsFreed || !GodotObject.IsInstanceValid(em.Particles))
                continue;

            // #30 slowmo/pause: ambient map emitters are Godot self-processing particle nodes (they ignore the
            // scaled deltas the CPU-side drivers use), so drive their SpeedScale from the shared factor — frozen
            // mid-air at slowmo 0, slow drift at fractional slowmo. Nothing else sets SpeedScale (default 1).
            em.Particles.SpeedScale = VortexArena.Game.Client.ClientRenderTime.Scale;

            bool active = e.Active == MapMover.ActiveActive;
            if (em.ImpulseMode)
            {
                // ABSOLUTE_ONLY_SPAWN_AT_TOGGLE: a burst of .impulse particles exactly on the rising edge.
                if (active && !em.WasActive)
                {
                    em.Particles.Amount = System.Math.Clamp((int)MathF.Max(1f, e.Impulse), 1, MaxAmountPerEmitter);
                    em.Particles.Restart();
                    em.Particles.Emitting = true;
                }
            }
            else if (em.Particles.Emitting != active)
            {
                em.Particles.Emitting = active;
            }
            em.WasActive = active;
        }
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
        {
            foreach (Entity e in dead)
            {
                if (GodotObject.IsInstanceValid(_emitters[e].Particles))
                    _emitters[e].Particles.QueueFree();
                _emitters.Remove(e);
            }
        }
    }

    private void Scan(string className)
    {
        foreach (Entity e in Api.Entities.FindByClass(className))
        {
            if (e.IsFreed || _emitters.ContainsKey(e))
                continue;
            MapEmitter? em = Build(e);
            if (em is not null)
                _emitters[e] = em;
        }
    }

    // =================================================================================================
    //  Emitter construction
    // =================================================================================================

    private MapEmitter? Build(Entity e)
    {
        // Resolve the effectinfo block the .mdl names (first defined, renderable, dry-land block).
        EffectInfoEmitter? block = null;
        IReadOnlyList<EffectInfoEmitter>? blocks = Effects?.GetInfoBlocks(e.Mdl);
        if (blocks is not null)
        {
            foreach (EffectInfoEmitter b in blocks)
            {
                if (!b.Defined || b.Underwater) continue;
                if (b.Type is EiType.Decal or EiType.Beam or EiType.Bubble) continue;
                block = b;
                break;
            }
        }

        // --- emission volume: the entity's linked bbox (brush model or explicit mins/maxs box) ---
        NVec3 qMin = e.Origin + e.Mins;
        NVec3 qMax = e.Origin + e.Maxs;
        NVec3 qCenter = (qMin + qMax) * 0.5f;
        NVec3 qSize = qMax - qMin;

        // --- base velocity (DP: __pointparticles emit velocity = .velocity + randomvec()*.waterlevel; the
        //     block then applies its own multiplier/offset/jitter) ---
        NVec3 emitVelQ = e.Velocity;

        // movedir: DP traces each random point along movedir 4096 and emits at the surface with velocity
        // plane_normal*|movedir|. Approximate with ONE center trace (documented above).
        if (e.MoveDir != NVec3.Zero && Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(qCenter, NVec3.Zero, NVec3.Zero,
                qCenter + VortexArena.Common.Math.QMath.Normalize(e.MoveDir) * 4096f, MoveFilter.Normal, e);
            if (tr.Fraction < 1f)
            {
                qCenter = tr.EndPos;
                qSize = NVec3.Zero;
                emitVelQ += tr.PlaneNormal * e.MoveDir.Length();
            }
        }

        // --- rate: emissions/sec; negative impulse = relative density per 64^3 cube (wire decode) ---
        float rate = e.Impulse;
        if (rate < 0f)
        {
            float vol = MathF.Max(1f, MathF.Abs(qSize.X * qSize.Y * qSize.Z));
            rate = -rate * vol / (64f * 64f * 64f);
        }
        if (rate <= 0f)
            rate = 1f;

        float perEmission = block is null ? 1f : block.CountAbsolute + e.ParticleCount * block.CountMultiplier;
        if (perEmission <= 0f)
            perEmission = e.ParticleCount;
        float pps = MathF.Max(0.25f, rate * perEmission);
        float life = block?.Lifetime() ?? 1.5f;
        life = Mathf.Clamp(life, 0.1f, 10f);

        bool impulseMode = (e.SpawnFlags & PointParticles.ParticlesImpulse) != 0;

        var particles = new GpuParticles3D
        {
            Name = $"pp#{e.Index}_{e.Mdl}",
            Amount = System.Math.Clamp((int)MathF.Ceiling(pps * life), 1, MaxAmountPerEmitter),
            Lifetime = life,
            OneShot = impulseMode,
            Explosiveness = impulseMode ? 1f : 0f,   // continuous emitters stream steadily
            Emitting = !impulseMode && e.Active == MapMover.ActiveActive,
            Position = Coords.ToGodot(qCenter),
            // Particles can drift far from a big volume; give the AABB room so the whole emitter isn't culled.
            VisibilityAabb = new Aabb(new Vector3(-512f, -512f, -512f), new Vector3(1024f, 1024f, 1024f)),
        };

        var mat = new ParticleProcessMaterial();

        // emission box spans the volume (+ the block's originjitter).
        Vector3 halfG = AbsToGodot(qSize * 0.5f);
        Vector3 jitterG = block is null ? Vector3.Zero : AbsToGodot(block.OriginJitter);
        mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
        mat.EmissionBoxExtents = new Vector3(
            MathF.Max(0.05f, halfG.X + jitterG.X),
            MathF.Max(0.05f, halfG.Y + jitterG.Y),
            MathF.Max(0.05f, halfG.Z + jitterG.Z));

        // velocity: block multiplier/offset on the entity velocity, plus the jitter spread.
        NVec3 baseVelQ = block is null
            ? emitVelQ
            : emitVelQ * (block.VelocityMultiplier != 0f ? block.VelocityMultiplier : 1f) + block.VelocityOffset;
        Vector3 baseVelG = Coords.ToGodot(baseVelQ);
        float baseSpeed = baseVelG.Length();
        mat.Direction = baseSpeed > 0.001f ? baseVelG / baseSpeed : Vector3.Up;
        mat.InitialVelocityMin = baseSpeed;
        mat.InitialVelocityMax = baseSpeed;
        float jitterSpeed = e.ParticleJitter;
        if (block is not null)
        {
            Vector3 vj = AbsToGodot(block.VelocityJitter);
            jitterSpeed += (vj.X + vj.Y + vj.Z) / 3f;
        }
        if (jitterSpeed > 0.001f)
        {
            mat.Spread = baseSpeed > 0.001f ? 60f : 180f;
            mat.InitialVelocityMin = MathF.Max(0f, baseSpeed - jitterSpeed);
            mat.InitialVelocityMax = baseSpeed + jitterSpeed;
        }

        mat.Gravity = new Vector3(0f, -DpGravity * (block?.Gravity ?? 0f), 0f);

        // color/alpha: the block's midpoint color fading out over life (TE_SPARK and friends are additive
        // via the mesh material). No block => a warm spark-ish default so func_sparks still reads.
        Color baseColor;
        if (block is not null)
        {
            (float r, float g, float bl) = block.MidColor();
            baseColor = new Color(r, g, bl);
        }
        else
        {
            baseColor = new Color(1f, 0.85f, 0.4f);
        }
        float a0 = block?.MidAlpha01() ?? 1f;
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(baseColor.R, baseColor.G, baseColor.B, a0));
        ramp.SetColor(1, new Color(baseColor.R, baseColor.G, baseColor.B, 0f));
        mat.ColorRamp = new GradientTexture1D { Gradient = ramp };
        mat.Color = baseColor;

        if (block is not null)
        {
            float sMin = MathF.Max(0.01f, block.SizeMin) * 2f;
            float sMax = MathF.Max(0.01f, block.SizeMax) * 2f;
            float grow = block.SizeIncrease * 2f * life;
            mat.ScaleMin = MathF.Max(0.4f, MathF.Min(sMin, sMin + grow));
            mat.ScaleMax = MathF.Max(mat.ScaleMin, MathF.Max(sMax, sMax + grow));
        }
        else
        {
            mat.ScaleMin = 0.5f;
            mat.ScaleMax = 1f;
        }

        particles.ProcessMaterial = mat;
        particles.DrawPass1 = block is not null && Effects is not null
            ? Effects.BuildEmitterMesh(block, baseColor)
            : DefaultSparkMesh(baseColor);

        AddChild(particles);
        return new MapEmitter
        {
            Entity = e,
            Particles = particles,
            ImpulseMode = impulseMode,
            WasActive = e.Active == MapMover.ActiveActive,
        };
    }

    private static Mesh DefaultSparkMesh(Color color)
    {
        var quad = new QuadMesh { Size = new Vector2(0.5f, 0.5f) };
        quad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = color,
            DisableReceiveShadows = true,
        };
        return quad;
    }

    private static Vector3 AbsToGodot(NVec3 q)
    {
        Vector3 g = Coords.ToGodot(q);
        return new Vector3(MathF.Abs(g.X), MathF.Abs(g.Y), MathF.Abs(g.Z));
    }
}
