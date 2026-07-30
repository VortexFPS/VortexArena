namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  ModernPreset — the per-effect DRAW knobs of the modern GPU backend (planning/particles-dual-system.md
//  §B.3 / §D.1). The faithful SPAWN+INTEGRATE math is identical for every modern emitter (it's DP's actual
//  algorithm, in ModernParticleShaders' process shader); a preset only flips the modern *rendering* features
//  on/off per effect so gameplay readability is preserved one effect at a time — soft-particle depth fade,
//  lit/shadowed smoke, curl-noise turbulence, ribbon/tube trail meshes, flipbook atlases, HDR emissive boost.
//
//  Presets are pure value records (no Godot deps): ModernPresetLibrary returns them by id, the authoring
//  overlay (effectinfo_xg.txt, §D.1) can override individual knobs, and ModernParticleBackend reads them when
//  building each emitter's draw-pass material. The SHAPE of the enabled knobs also keys the compiled-shader
//  cache in ModernParticleShaders, so two effects sharing a preset share one GPU pipeline.
// =====================================================================================================

/// <summary>
/// The modern draw-feature set for one effect. A plain readonly record struct — equality is by value, so it
/// doubles as a cache key for the draw-pass material/shader (see <see cref="ModernParticleShaders"/>).
/// </summary>
public readonly record struct ModernPreset
{
    /// <summary>Depth-fade the billboard where it intersects opaque geometry (samples DEPTH_TEXTURE in the draw
    /// shader). The single biggest modernization (§B.3) — kills the hard sprite/wall seam on smoke and dust.</summary>
    public bool SoftParticles { get; init; }

    /// <summary>Render the billboard LIT/shadowed (spatial shading) instead of unshaded. Optional per-preset:
    /// smoke catches scene light; energy/fire effects stay unshaded so their emissive reads at full brightness.</summary>
    public bool Lit { get; init; }

    /// <summary>Curl-noise turbulence STRENGTH (world units/sec of swirl added in the process shader). 0 disables
    /// the whole curl branch (and the gradient-noise cost). Typical smoke/fire values ≈ 8..40.</summary>
    public float CurlNoise { get; init; }

    /// <summary>Build the emitter as a ribbon/tube trail mesh (connected quad strip following the path) rather
    /// than discrete billboards — for projectile streaks. The backend still spawns the same particles; this only
    /// selects the trail draw pass. (Mesh build is a backend concern; the flag is the authoring intent.)</summary>
    public bool RibbonTrail { get; init; }

    /// <summary>Animate the atlas cell over the particle's life (flipbook): the draw shader advances the UV cell
    /// by the lifetime phase across an N-frame strip instead of holding one static cell. For fire/explosion
    /// sprites that have an animation band in the particlefont atlas.</summary>
    public bool Flipbook { get; init; }

    /// <summary>Multiplier on the emissive output fed to the existing bloom pipeline (HDR). 1 = neutral (the
    /// faithful brightness); &gt;1 makes energy/explosion cores bloom; the draw shader multiplies EMISSION by it
    /// for lit passes or scales ALBEDO for the unshaded additive path. Clamped to a sane range by the backend.</summary>
    public float EmissiveBoost { get; init; }

    /// <summary>The neutral preset: faithful look, no modern features. EmissiveBoost 1 = unchanged brightness.</summary>
    public static readonly ModernPreset Default = new()
    {
        SoftParticles = false,
        Lit = false,
        CurlNoise = 0f,
        RibbonTrail = false,
        Flipbook = false,
        EmissiveBoost = 1f,
    };
}
