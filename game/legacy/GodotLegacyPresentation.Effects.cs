// Port of Base/darkplaces/cl_particles.c CL_ParticleEffect, CL_ParticleTrail and CL_ParticleBox as
// their callers reach them (clvm_cmds.c VM_CL_pointparticles / VM_CL_trailparticles / VM_CL_boxparticles /
// VM_CL_particle, cl_parse.c svc_pointparticles / svc_trailparticles / svc_particle), and of
// cl_parse.c CL_ParseTempEntity's mapping from a TE_* message to the effect it plays. The particles
// themselves are the existing EffectSystem's (game/client/EffectSystem.cs), found by effect NAME.
using System;
using Godot;
using VortexArena.Game.Client;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // A program that spawns effects in a loop costs itself that frame's later effects, nothing more.
    private const int MaxEffectsPerFrame = 256;

    private int _effectsThisFrame;
    private readonly System.Collections.Generic.Dictionary<string, int> _debugEffects = new();

    /// <summary>Effects refused because the frame's bound was reached.</summary>
    public long EffectsDropped { get; private set; }
    /// <summary>Engine temp entities with no equivalent in the particle system (beams, rain cubes, coloured flashes): counted, not drawn.</summary>
    public long TempEntitiesNotDrawn { get; private set; }
    /// <summary>svc_effect / effect(): a sprite animation at a point. Counted, not drawn.</summary>
    public long SpriteEffectsNotDrawn { get; private set; }

    private void BeginEffectsFrame()
    {
        _effectsThisFrame = 0;
        _directParticlesThisFrame = 0;
    }

    // spawnparticle makes one particle a call, so a program may rightly call it hundreds of times a frame;
    // past this many in one frame the answer is the one a full pool gives (no particle, 0 to the program).
    private const int MaxDirectParticlesPerFrame = 4096;

    private int _directParticlesThisFrame;

    /// <summary>Particles made for spawnparticle / delayedparticle, and how many were refused (the frame's
    /// bound, cl_particles 0, a full pool, values that are not numbers).</summary>
    public long DirectParticlesSpawned { get; private set; }
    public long DirectParticlesRefused { get; private set; }

    /// <summary>
    /// CL_NewParticle for the DP_CSQC_SPAWNPARTICLE builtins: the particle goes into the faithful particle
    /// simulation's pool as it stands (VortexArena.Engine ParticleSim.SpawnDirect), with DarkPlaces' own
    /// type, blend and orientation numbers. See that method for what the pool cannot represent.
    /// </summary>
    bool ILegacyEffects.SpawnParticle(in LegacySpawnParticle particle)
    {
        if (s_noEffects || _effects.FaithfulParticles is not { } backend || _directParticlesThisFrame >= MaxDirectParticlesPerFrame
            || !Finite(particle.Origin) || !Finite(particle.Velocity) || !FiniteParticle(particle))
        {
            DirectParticlesRefused++;
            return false;
        }
        _directParticlesThisFrame++;
        VortexArena.Engine.Particles.DirectParticle direct = new()
        {
            Origin = N(particle.Origin), Velocity = N(particle.Velocity),
            Type = particle.Type, Blend = particle.Blend, Orientation = particle.Orientation,
            Color1 = particle.Color1, Color2 = particle.Color2, Texture = particle.Texture,
            Size = particle.Size, SizeIncrease = particle.SizeIncrease, Alpha = particle.Alpha, AlphaFade = particle.AlphaFade,
            Gravity = particle.Gravity, Bounce = particle.Bounce, AirFriction = particle.AirFriction, LiquidFriction = particle.LiquidFriction,
            OriginJitter = particle.OriginJitter, VelocityJitter = particle.VelocityJitter,
            Lifetime = particle.Lifetime, Stretch = particle.Stretch,
            StainColor1 = particle.StainColor1, StainColor2 = particle.StainColor2, StainTexture = particle.StainTexture,
            StainAlpha = particle.StainAlpha, StainSize = particle.StainSize, Angle = particle.Angle, Spin = particle.Spin,
            // A delay is bounded to what a level lasts: the particle holds a pool slot until it appears.
            Delay = Math.Clamp(particle.Delay, 0f, 600f),
        };
        if (!backend.Sim.SpawnDirect(direct))
        {
            DirectParticlesRefused++;
            return false;
        }
        DirectParticlesSpawned++;
        return true;
    }

    // Every number the simulation integrates or the renderer scales by. A program can hand over NaN or
    // infinity (a division by zero in QuakeC is not an error); such a particle is not made.
    private static bool FiniteParticle(in LegacySpawnParticle p) =>
        float.IsFinite(p.Size) && float.IsFinite(p.SizeIncrease) && float.IsFinite(p.Alpha) && float.IsFinite(p.AlphaFade)
        && float.IsFinite(p.Gravity) && float.IsFinite(p.Bounce) && float.IsFinite(p.AirFriction) && float.IsFinite(p.LiquidFriction)
        && float.IsFinite(p.OriginJitter) && float.IsFinite(p.VelocityJitter) && float.IsFinite(p.Lifetime) && float.IsFinite(p.Stretch)
        && float.IsFinite(p.StainAlpha) && float.IsFinite(p.StainSize) && float.IsFinite(p.Angle) && float.IsFinite(p.Spin) && float.IsFinite(p.Delay);

    // particleeffectnum answers numbers in effectinfo.txt order; the effect system knows names. The table
    // that maps one to the other is the loaded program's (CsqcHost.Effects), built from the same file.
    private string? EffectName(int effect) => _host?.Effects.NameForIndex(effect);

    private bool Budget()
    {
        if (s_noEffects) return false;
        if (_effectsThisFrame < MaxEffectsPerFrame)
        {
            _effectsThisFrame++;
            return true;
        }
        EffectsDropped++;
        return false;
    }

    private static NVec3 Middle(QcVector a, QcVector b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f);

    private NVec3 Within(QcVector a, QcVector b) => new(
        a.X + (float)_boxRandom.NextDouble() * (b.X - a.X), a.Y + (float)_boxRandom.NextDouble() * (b.Y - a.Y), a.Z + (float)_boxRandom.NextDouble() * (b.Z - a.Z));

    // How many points a box of particles is spread over at most. The effect system spawns at a point; a box
    // (boxparticles, particle rain, an effect over an area) becomes this many points inside it.
    private const int MaxBoxPoints = 8;
    private readonly Random _boxRandom = new(12345);

    /// <summary>
    /// CL_ParticleEffect over a box: "org = originmins + random * (originmaxs - originmins)" and the same for
    /// the velocity, per particle (cl_particles.c CL_NewParticlesFromEffectinfo). The effect system spawns a
    /// burst at one point with one velocity, so a box with any extent is spread over up to
    /// <see cref="MaxBoxPoints"/> random points inside it, the count shared between them; for pointparticles
    /// the two corners are equal and it is one burst. The palette colour of the old particle() builtin is
    /// not used.
    /// </summary>
    void ILegacyEffects.ParticleEffect(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, int paletteColor) =>
        SpawnParticles(effect, count, originMin, originMax, velocityMin, velocityMax, null);

    private void SpawnParticles(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, Color? tint)
    {
        if (!Finite(originMin) || !Finite(originMax) || !Finite(velocityMin) || !Finite(velocityMax) || !float.IsFinite(count)) return;
        if (EffectName(effect) is not { } name)
        {
            EffectsUnknown++;
            return;
        }
        if (!Budget()) return;
        // Spawn answers a node only for some routes (a pooled or routed burst has none), so its result says
        // nothing about success: what is counted is what was handed over.
        count = Math.Clamp(count, 0f, 1024f);
        bool box = originMin.X != originMax.X || originMin.Y != originMax.Y || originMin.Z != originMax.Z
            || velocityMin.X != velocityMax.X || velocityMin.Y != velocityMax.Y || velocityMin.Z != velocityMax.Z;
        int points = box ? Math.Clamp((int)MathF.Ceiling(count), 1, MaxBoxPoints) : 1;
        long began = LegacyPerfLog.Stamp();
        if (points == 1) _effects.Spawn(name, Middle(originMin, originMax), Middle(velocityMin, velocityMax), count, tint);
        else
            for (int i = 0; i < points; i++)
                _effects.Spawn(name, Within(originMin, originMax), Within(velocityMin, velocityMax), count / points, tint);
        if (LegacyPerfLog.Enabled && System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds >= 0.3)
            LegacyPerfLog.Event(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"effect {name} count {count:0.##} points {points}"), began);
        EffectsSpawned++;
        if (s_debugEntities) _debugEffects[name] = _debugEffects.GetValueOrDefault(name) + 1;
    }

    /// <summary>
    /// CL_ParticleTrail from <paramref name="start"/> to <paramref name="end"/>. The per-segment trail
    /// path is used when the effect system offers it; otherwise the effect is spawned once with the end
    /// point, which is how that system takes a trail. The boxparticles tint is not applied.
    /// </summary>
    void ILegacyEffects.ParticleTrail(int effect, float count, QcVector start, QcVector end, QcVector velocityMin, QcVector velocityMax, int paletteColor, in LegacyParticleTint tint)
    {
        if (!Finite(start) || !Finite(end) || !Finite(velocityMin) || !Finite(velocityMax)) return;
        if (EffectName(effect) is not { } name)
        {
            EffectsUnknown++;
            return;
        }
        if (!Budget()) return;
        if (!_effects.SpawnTrailSegment(name, N(start), N(end), Middle(velocityMin, velocityMax))) _effects.Spawn(name, N(start), N(end), 1f);
        EffectsSpawned++;
        if (s_debugEntities) _debugEffects["trail:" + name] = _debugEffects.GetValueOrDefault("trail:" + name) + 1;
    }

    /// <summary>
    /// CL_ParticleBox: as <see cref="ILegacyEffects.ParticleEffect"/>, with the tint the program asked for
    /// (PARTICLES_USECOLOR / PARTICLES_USEALPHA: particles_colormin..max and particles_alphamin..max multiply
    /// each particle's colour and alpha). One tint for the call - the middle of the range, which is exact for
    /// Xonotic's own calls (they set min and max alike: a team's colour on its spawn points, its spawn flash).
    /// Without it every team-coloured effect was drawn white. The fade (a count multiplier) is not applied.
    /// </summary>
    void ILegacyEffects.ParticleBox(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, in LegacyParticleTint tint)
    {
        Color? colour = null;
        float r = (tint.ColorMin.X + tint.ColorMax.X) * 0.5f, g = (tint.ColorMin.Y + tint.ColorMax.Y) * 0.5f, b = (tint.ColorMin.Z + tint.ColorMax.Z) * 0.5f;
        float a = (tint.AlphaMin + tint.AlphaMax) * 0.5f;
        if (float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b) && float.IsFinite(a) && (r != 1 || g != 1 || b != 1 || a != 1))
            colour = new Color(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), Math.Clamp(a, 0f, 1f));
        SpawnParticles(effect, count, originMin, originMax, velocityMin, velocityMax, colour);
    }

    /// <summary>
    /// CL_ParseTempEntity: each engine temp entity is "CL_ParticleEffect(EFFECT_TE_x, ...)" plus, for
    /// some, a light flash and a sound. The ones with a standard effect of the same name are played
    /// through it; the others are counted.
    /// </summary>
    void ILegacyEffects.TempEntity(in DpTempEntity effect)
    {
        string? name = effect.Type switch
        {
            TempEntityType.Gunshot => "TE_GUNSHOT",
            TempEntityType.GunshotQuad => "TE_GUNSHOTQUAD",
            TempEntityType.Spike => "TE_SPIKE",
            TempEntityType.SpikeQuad => "TE_SPIKEQUAD",
            TempEntityType.SuperSpike => "TE_SUPERSPIKE",
            TempEntityType.SuperSpikeQuad => "TE_SUPERSPIKEQUAD",
            TempEntityType.WizSpike => "TE_WIZSPIKE",
            TempEntityType.KnightSpike => "TE_KNIGHTSPIKE",
            // The coloured variants differ from TE_EXPLOSION only in their light.
            TempEntityType.Explosion or TempEntityType.Explosion2 or TempEntityType.Explosion3 or TempEntityType.ExplosionRgb => "TE_EXPLOSION",
            TempEntityType.ExplosionQuad => "TE_EXPLOSIONQUAD",
            TempEntityType.TarExplosion => "TE_TAREXPLOSION",
            TempEntityType.Teleport => "TE_TELEPORT",
            TempEntityType.LavaSplash => "TE_LAVASPLASH",
            TempEntityType.SmallFlash => "TE_SMALLFLASH",
            TempEntityType.FlameJet => "TE_FLAMEJET",
            TempEntityType.Blood => "TE_BLOOD",
            TempEntityType.Spark => "TE_SPARK",
            TempEntityType.PlasmaBurn => "TE_PLASMABURN",
            TempEntityType.TeiG3 => "TE_TEI_G3",
            TempEntityType.TeiSmoke => "TE_TEI_SMOKE",
            TempEntityType.TeiBigExplosion => "TE_TEI_BIGEXPLOSION",
            TempEntityType.TeiPlasmaHit => "TE_TEI_PLASMAHIT",
            // Lightning1-3, Beam, Lightning4Neh (beams between two points), BloodShower, ParticleCube,
            // ParticleRain, ParticleSnow (boxes of generated particles) and CustomFlash (a light only).
            _ => null,
        };
        if (name is null || !Finite(effect.Origin) || !Finite(effect.Direction))
        {
            TempEntitiesNotDrawn++;
            return;
        }
        if (!Budget()) return;
        _effects.Spawn(name, effect.Origin, effect.Direction, Math.Clamp(effect.Count, 1, 1024));
        EffectsSpawned++;
    }

    void ILegacyEffects.SpriteEffect(QcVector origin, string model, int startFrame, int frameCount, float frameRate) =>
        AddSpriteEffect(origin, model, startFrame, frameCount, frameRate);

    // ---- the server's own effect messages ----------------------------------------------------------------

    /// <summary>svc_particle: "CL_ParticleEffect(EFFECT_SVC_PARTICLE, count, org, org, dir, dir, NULL, color)".</summary>
    void IDpClientHandler.OnParticle(in DpParticle particle)
    {
        QcVector origin = new(particle.Origin.X, particle.Origin.Y, particle.Origin.Z), direction = new(particle.Direction.X, particle.Direction.Y, particle.Direction.Z);
        ((ILegacyEffects)this).ParticleEffect(CsqcEffectInfo.SvcParticle, particle.Count, origin, origin, direction, direction, particle.Color);
    }

    /// <summary>svc_pointparticles / svc_pointparticles1.</summary>
    void IDpClientHandler.OnPointParticles(in DpPointParticles particles)
    {
        QcVector origin = new(particles.Origin.X, particles.Origin.Y, particles.Origin.Z), velocity = new(particles.Velocity.X, particles.Velocity.Y, particles.Velocity.Z);
        ((ILegacyEffects)this).ParticleEffect(particles.EffectIndex, particles.Count, origin, origin, velocity, velocity, 0);
    }

    /// <summary>svc_trailparticles.</summary>
    void IDpClientHandler.OnTrailParticles(in DpTrailParticles trail)
    {
        LegacyParticleTint none = default;
        ((ILegacyEffects)this).ParticleTrail(trail.EffectIndex, 1, new QcVector(trail.Start.X, trail.Start.Y, trail.Start.Z),
            new QcVector(trail.End.X, trail.End.Y, trail.End.Z), default, default, 0, none);
    }

    /// <summary>svc_effect / svc_effect2: the sprite animation of <see cref="ILegacyEffects.SpriteEffect"/>.</summary>
    void IDpClientHandler.OnEffect(in DpEffect effect) => SpriteEffectsNotDrawn++;
}
