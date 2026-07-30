using System;
using System.Collections.Generic;
using VortexArena.Game.Client;   // EffectInfoEmitter, EiType/EiBlend/EiOrientation
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  ParticleTranslation — the two bidirectional translation layers of §D.2 (planning/particles-dual-system.md).
//
//   modern -> original   ToFaithful(name, preset, authored):
//     A modern-authored effect (defined only by a ModernPreset, with no effectinfo.txt blocks) must still
//     render *something faithful-shaped* in mode 0 (cl_particles_modern 0 → everything is faithful). This
//     synthesizes EffectInfoEmitter blocks from the preset knobs, with the auto-derivation rules the spec
//     calls out:
//       - turbulence (CurlNoise)        -> a velocityjitter boost (the faithful look of swirl is more spread)
//       - ribbon (RibbonTrail)          -> spark trail blocks at equivalent spacing (a streak of sparks)
//       - soft-smoke (SoftParticles)    -> an alphastatic smoke puff (the faithful smoke baseline)
//       - sub-emitters / showers        -> pre-spawned counts folded into the block's countmultiplier
//       - emissive boost                -> an additive `static` core block (the faithful "glow")
//     If the author supplied explicit fallback blocks (from the overlay body, §D.1), those OVERRIDE the
//     auto-derivation entirely — hand-authored faithful shape always wins.
//
//   original -> modern   DerivePreset(blocks):
//     The §B custom shader IS the original->modern translation (it runs DP's faithful math on the GPU with
//     modern draw features), so mode 2 needs only a *default preset* for an effectinfo-defined effect: which
//     modern draw features suit its block composition (smoke→soft+lit, beam/trail→ribbon, spark/blood→ember,
//     explosion→lit-explosion). This is a thin heuristic descriptor, not a re-synthesis.
//
//  Pure value logic — no Godot deps — so it is unit-testable and shared by the router on both backends.
// =====================================================================================================

/// <summary>Static modern↔original translation helpers (§D.2). Stateless; safe to call per spawn.</summary>
public static class ParticleTranslation
{
    // -------------------------------------------------------------------------------------------------
    //  modern -> original
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Synthesize the faithful (effectinfo-shaped) emitter blocks a modern-authored effect needs to render in
    /// mode 0. When <paramref name="authored"/> is non-empty those blocks win verbatim (hand-authored fallback
    /// from the overlay body overrides the auto-derivation, §D.1/§D.2). Otherwise derive blocks from the
    /// <paramref name="preset"/> knobs per the §D.2 auto-derivation rules. Always returns at least one block so
    /// a modern-only effect is never silently invisible in mode 0.
    /// </summary>
    public static IReadOnlyList<EffectInfoEmitter> ToFaithful(
        string effectName, ModernPreset preset, IReadOnlyList<EffectInfoEmitter>? authored)
    {
        // 1) Hand-authored fallback blocks override the auto-derivation entirely.
        if (authored is { Count: > 0 })
            return authored;

        // 2) Auto-derive from the preset's enabled modern features.
        var blocks = new List<EffectInfoEmitter>(2);

        if (preset.RibbonTrail)
        {
            // Ribbon/tube trail -> a spark trail at equivalent spacing: a velocity-aligned streak emitter. The
            // ribbon is a connected strip in modern; the faithful equivalent is a dense pt_spark trail (DP's
            // own projectile streaks are exactly this).
            blocks.Add(DeriveRibbonTrailBlock(preset));
        }
        else
        {
            // A billboard puff/burst. Soft-smoke -> alphastatic smoke; otherwise a generic additive puff.
            blocks.Add(DerivePuffBlock(preset));
        }

        // Emissive-boosted effects (explosion/ember cores) get an extra additive `static` glow core so the
        // faithful render carries the same "hot center" the modern HDR boost gives. Skip for pure trails.
        if (preset.EmissiveBoost > 1.25f && !preset.RibbonTrail)
            blocks.Add(DeriveGlowCoreBlock(preset));

        return blocks;
    }

    /// <summary>The soft-smoke / generic puff fallback: an alphastatic (or additive) billboard, with the
    /// CurlNoise turbulence folded into a velocityjitter boost so the faithful spread approximates the swirl.</summary>
    private static EffectInfoEmitter DerivePuffBlock(ModernPreset preset)
    {
        var b = new EffectInfoEmitter { Defined = true };

        if (preset.SoftParticles && preset.EmissiveBoost <= 1.25f)
        {
            // Soft volumetric smoke -> the faithful smoke baseline (alphastatic, alpha-blended, slow billow).
            b.Type = EiType.AlphaStatic;
            b.Blend = EiBlend.Alpha;
            b.Orientation = EiOrientation.Billboard;
            b.Color0 = 0x303030; b.Color1 = 0x606060;     // grey smoke
            b.SizeMin = 8f; b.SizeMax = 14f;
            b.SizeIncrease = 12f;                          // smoke grows as it rises/disperses
            b.AlphaMin = 80f; b.AlphaMax = 140f; b.AlphaFade = 180f;
            b.Gravity = -0.02f;                            // gentle rise
            b.AirFriction = 1.0f;
        }
        else
        {
            // A generic additive energy puff (used when not specifically smoke).
            b.Type = EiType.Static;
            b.Blend = EiBlend.Add;
            b.Orientation = EiOrientation.Billboard;
            b.SizeMin = 6f; b.SizeMax = 10f;
            b.SizeIncrease = 8f;
            b.AlphaMin = 128f; b.AlphaMax = 200f; b.AlphaFade = 512f;
            b.AirFriction = 2.0f;
        }

        b.CountMultiplier = 8f;                            // a small puff cloud
        ApplyTurbulenceJitter(b, preset.CurlNoise);
        return b;
    }

    /// <summary>The ribbon-trail fallback: a dense velocity-aligned spark trail at equivalent spacing.</summary>
    private static EffectInfoEmitter DeriveRibbonTrailBlock(ModernPreset preset)
    {
        // A trail emitter: trailspacing sets the per-unit density (DP: count = 1/trailspacing). A modern ribbon
        // is continuous; ~8qu spacing gives a visually continuous faithful spark streak without flooding the pool.
        var b = new EffectInfoEmitter { Defined = true };
        b.Type = EiType.Spark;
        b.Blend = EiBlend.Add;
        b.Orientation = EiOrientation.Spark;              // velocity-stretched streak (the ribbon analogue)
        b.TrailSpacing = 8f;
        b.CountMultiplier = 1f / b.TrailSpacing;          // mirror EffectInfo's trailspacing->count rule
        b.Color0 = 0xFFD080; b.Color1 = 0xFFFFFF;          // warm streak; HDR boost handled faithfully via alpha
        b.SizeMin = 1.5f; b.SizeMax = 2.5f;
        b.StretchFactor = 1.0f;
        b.AlphaMin = 160f; b.AlphaMax = 256f; b.AlphaFade = 640f;
        b.AirFriction = 4.0f;
        b.VelocityMultiplier = 1.0f;
        ApplyTurbulenceJitter(b, preset.CurlNoise);
        return b;
    }

    /// <summary>An additive `static` glow core for an emissive-boosted effect (explosion/ember hot center).</summary>
    private static EffectInfoEmitter DeriveGlowCoreBlock(ModernPreset preset)
    {
        var b = new EffectInfoEmitter { Defined = true };
        b.Type = EiType.Static;
        b.Blend = EiBlend.Add;
        b.Orientation = EiOrientation.Billboard;
        b.Color0 = 0xFFE0A0; b.Color1 = 0xFFFFFF;
        b.SizeMin = 10f; b.SizeMax = 18f;
        b.SizeIncrease = 24f;
        // The HDR boost in modern is brightness; faithfully we mirror it as a brighter, faster-fading additive
        // flash (additive blend already reads as "glow"). Higher boost -> higher peak alpha.
        float peak = Math.Clamp(160f + preset.EmissiveBoost * 32f, 160f, 256f);
        b.AlphaMin = peak; b.AlphaMax = 256f; b.AlphaFade = 700f;
        b.CountMultiplier = 1f;                            // one bright core sprite per spawn
        b.AirFriction = 2.0f;
        return b;
    }

    /// <summary>
    /// Fold a modern curl-noise turbulence strength into a faithful velocityjitter boost. Modern turbulence is
    /// world-units/sec of swirl added each frame in the process shader; the faithful look of that swirl is a
    /// wider initial velocity spread, so we add the strength (scaled) into the per-axis velocityjitter, which DP
    /// applies as <c>jitter ⊙ unitBallRandom</c> at spawn (the same anisotropic-ball spread the faithful sim uses).
    /// </summary>
    private static void ApplyTurbulenceJitter(EffectInfoEmitter b, float curlNoise)
    {
        if (curlNoise <= 0f)
            return;
        // ~2.5x the curl strength reads as a comparable spread once integrated over the particle's short life;
        // applied isotropically (curl swirls in all axes).
        float boost = curlNoise * 2.5f;
        b.VelocityJitter = new NVec3(
            b.VelocityJitter.X + boost,
            b.VelocityJitter.Y + boost,
            b.VelocityJitter.Z + boost);
    }

    // -------------------------------------------------------------------------------------------------
    //  original -> modern
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Derive a sensible default <see cref="ModernPreset"/> for an effectinfo-defined effect, from its block
    /// composition. This is the thin original->modern descriptor (§D.2): the heavy lifting — DP's faithful
    /// spawn/integration math — lives in the §B custom shader, so all we choose here is which modern *draw*
    /// features suit the effect. Heuristic priority (most specific wins):
    ///   - any beam / trailspacing block            -> ribbon-trail
    ///   - explosion-shaped (smoke + bright additive)-> lit-explosion
    ///   - smoke-dominated                          -> soft-smoke
    ///   - spark/blood/ember-dominated              -> ember-shower
    ///   - otherwise                                -> Default (faithful look on GPU, no modern features)
    /// Returns <see cref="ModernPreset.Default"/> for an empty/undefined block set.
    /// </summary>
    public static ModernPreset DerivePreset(IReadOnlyList<EffectInfoEmitter> blocks)
    {
        if (blocks is null || blocks.Count == 0)
            return ModernPreset.Default;

        bool anyTrail = false;     // beam or trailspacing -> a connected streak
        bool anySmoke = false;     // pt_smoke / pt_alphastatic alpha-blended billboard
        bool anyBrightAdditive = false; // pt_static/spark additive (a glow/energy/fire core)
        bool anySparkOrBlood = false;
        int defined = 0;

        foreach (EffectInfoEmitter b in blocks)
        {
            if (b is null || !b.Defined)
                continue;
            defined++;

            if (b.Orientation == EiOrientation.Beam || b.TrailSpacing > 0f)
                anyTrail = true;

            switch (b.Type)
            {
                case EiType.Smoke:
                case EiType.AlphaStatic:
                    if (b.Blend == EiBlend.Alpha)
                        anySmoke = true;
                    break;
                case EiType.Static:
                    if (b.Blend == EiBlend.Add)
                        anyBrightAdditive = true;
                    break;
                case EiType.Spark:
                case EiType.Blood:
                    anySparkOrBlood = true;
                    if (b.Blend == EiBlend.Add)
                        anyBrightAdditive = true;
                    break;
            }
        }

        if (defined == 0)
            return ModernPreset.Default;

        // Most specific first.
        if (anyTrail)
            return ModernPresetLibrary.RibbonTrail_;

        // Explosion-shaped: a smoke cloud AND a bright additive core together (the classic effectinfo explosion).
        if (anySmoke && anyBrightAdditive)
            return ModernPresetLibrary.LitExplosion;

        if (anySmoke)
            return ModernPresetLibrary.SoftSmoke;

        if (anySparkOrBlood || anyBrightAdditive)
            return ModernPresetLibrary.EmberShower;

        // Plain alpha billboards etc. — faithful math on GPU, no modern draw features.
        return ModernPreset.Default;
    }
}
