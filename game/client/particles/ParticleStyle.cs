namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  Routing vocabulary shared by the EffectStyleRegistry (authoring overlay) and the ParticleRouter
//  (cl_particles_modern mode resolution). See planning/particles-dual-system.md §D.1/§D.2.
// =====================================================================================================

/// <summary>Per-effect authored style. <c>Auto</c> = original for effectinfo-defined effects, modern for
/// modern-authored ones (resolved by the router in mode 1).</summary>
public enum ParticleStyle { Auto, Original, Modern }

/// <summary>One effect's authored routing record (from effectinfo_xg.txt overlay): style + optional preset.</summary>
public readonly struct EffectStyleEntry
{
    public readonly ParticleStyle Style;
    public readonly string? ModernPresetId;   // named ModernPresetLibrary recipe, when style routes modern

    public EffectStyleEntry(ParticleStyle style, string? modernPresetId = null)
    {
        Style = style;
        ModernPresetId = modernPresetId;
    }

    public static readonly EffectStyleEntry Default = new(ParticleStyle.Auto, null);
}

/// <summary>The resolved backend choice for one spawn, after mode + style + SDF-availability is applied.</summary>
public enum ParticleBackendKind { Faithful, Modern }
