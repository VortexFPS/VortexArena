using System;

namespace VortexArena.Formats.Vfs;

/// <summary>
/// DarkPlaces' texture-compression buckets, one per <c>gl_texturecompression_*</c> cvar
/// (<c>gl_textures.c:38-48</c> — eleven categories gated by the <c>gl_texturecompression</c> master).
///
/// <para><b>Two are registered but currently unreachable</b>, deliberately — a registered cvar with no
/// consumer is honest parity, a silently-renamed one is not. <see cref="Q3BspDeluxemaps"/>: this port packs
/// lightmap and deluxe pages into one atlas (<c>MapLoader.BuildLightmapAtlas</c>) and external pages of both
/// kinds share the <c>lm_</c> prefix, so nothing at this layer can tell them apart — both classify as
/// <see cref="Q3BspLightmaps"/>. <see cref="LightCubemaps"/>: nothing loads DP's rtlight projection cubemaps
/// yet.</para>
/// </summary>
public enum TexCategory
{
    /// <summary>Diffuse/colormap — the default bucket for anything unclassified.</summary>
    Color,
    /// <summary>Tangent-space normal maps (<c>_norm</c>). DP defaults this OFF and so do we.</summary>
    Normal,
    /// <summary>Specular/gloss companions (<c>_gloss</c>).</summary>
    Gloss,
    /// <summary>Luma/glow companions (<c>_glow</c>, and Xonotic's <c>_luma</c> spelling).</summary>
    Glow,
    /// <summary>HUD/menu art — DP's "2d (hud/menu) textures other than the font".</summary>
    TwoD,
    /// <summary>q3bsp external lightmap pages (<c>lm_</c>), plus deluxe pages we cannot distinguish.</summary>
    Q3BspLightmaps,
    /// <summary>q3bsp deluxemap pages. Unreachable here — see the type remarks.</summary>
    Q3BspDeluxemaps,
    /// <summary>Skybox faces (<c>env/</c>, <c>gfx/env/</c>).</summary>
    Sky,
    /// <summary>Light projection cubemaps. Unreachable here — see the type remarks.</summary>
    LightCubemaps,
    /// <summary>Reflection cubemap masks (<c>_reflect</c>).</summary>
    ReflectMask,
    /// <summary>Sprites (<c>.spr</c> frames and the particle/decal sprite sheets).</summary>
    Sprites,
}

/// <summary>
/// Bucket a texture into a <see cref="TexCategory"/> from its vpath alone.
///
/// <para>DP classifies at the skinframe level, where the channel is known outright because the caller asked
/// for it (<c>R_SkinFrame_LoadExternal</c> loads <c>_norm</c>/<c>_gloss</c>/<c>_glow</c> by construction).
/// Nothing carries that context down to our upload path, so we recover the same split from the naming
/// conventions the rest of the loader already depends on: the <c>_norm/_gloss/_glow/_reflect</c> companion
/// suffixes (<c>AssetSystem.AddWithCompanions</c>), the <c>lm_</c> lightmap-page prefix
/// (<c>AssetSystem.EnsureMipmaps</c>), and <c>SkyboxLoader</c>'s <c>env/</c> search roots.</para>
///
/// <para>Pure string logic with no engine dependency, so it lives beside <see cref="AssetPaths"/> and is
/// directly unit-testable — the classification is the part of the per-category cvar work that can actually be
/// got wrong silently, since a mis-bucketed texture just quietly obeys the wrong cvar.</para>
/// </summary>
public static class TextureCategories
{
    /// <summary>DP's registered defaults as a bitmask over <see cref="TexCategory"/>.</summary>
    public const int DefaultMask =
        (1 << (int)TexCategory.Color) | (1 << (int)TexCategory.Gloss) | (1 << (int)TexCategory.Glow)
        | (1 << (int)TexCategory.LightCubemaps) | (1 << (int)TexCategory.ReflectMask)
        | (1 << (int)TexCategory.Sprites);

    /// <summary>True when <paramref name="mask"/> has <paramref name="category"/> set.</summary>
    public static bool Enabled(int mask, TexCategory category) => (mask & (1 << (int)category)) != 0;

    /// <summary>
    /// Classify <paramref name="vpath"/> (a resolved virtual path, e.g. <c>dds/textures/x/foo_norm.dds</c>).
    /// Suffix tests run against the extension-stripped filename, so both <c>foo_norm</c> and
    /// <c>foo_norm.dds</c> classify identically.
    /// </summary>
    public static TexCategory Classify(string vpath)
    {
        if (string.IsNullOrEmpty(vpath))
            return TexCategory.Color;

        int slash = vpath.LastIndexOf('/');
        string file = slash >= 0 ? vpath[(slash + 1)..] : vpath;

        // Lightmap/deluxe pages first: the prefix is unambiguous and outranks any suffix that follows it
        // (a deluxe page can legitimately be named lm_0001_norm in some compiler outputs).
        if (file.StartsWith("lm_", StringComparison.OrdinalIgnoreCase))
            return TexCategory.Q3BspLightmaps;

        string stem = AssetPaths.StripImageExtension(file);
        if (stem.EndsWith("_norm", StringComparison.OrdinalIgnoreCase))
            return TexCategory.Normal;
        if (stem.EndsWith("_gloss", StringComparison.OrdinalIgnoreCase))
            return TexCategory.Gloss;
        if (stem.EndsWith("_glow", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_luma", StringComparison.OrdinalIgnoreCase))
            return TexCategory.Glow;
        if (stem.EndsWith("_reflect", StringComparison.OrdinalIgnoreCase))
            return TexCategory.ReflectMask;

        // Skybox faces: DP's R_LoadSkyBox search roots (SkyboxLoader.LoadFace probes name, env/name and
        // gfx/env/name). Deliberately a PATH test, not a suffix test — a world texture merely ending in "_up"
        // is not sky, and the six side suffixes are far too generic to key on by themselves.
        if (vpath.StartsWith("env/", StringComparison.OrdinalIgnoreCase)
            || vpath.StartsWith("gfx/env/", StringComparison.OrdinalIgnoreCase)
            || vpath.Contains("/env/", StringComparison.OrdinalIgnoreCase))
            return TexCategory.Sky;

        if (vpath.StartsWith("sprites/", StringComparison.OrdinalIgnoreCase)
            || vpath.Contains("/sprites/", StringComparison.OrdinalIgnoreCase)
            || stem.Contains(".spr_", StringComparison.OrdinalIgnoreCase))
            return TexCategory.Sprites;

        // DP's "2d" bucket is HUD/menu art, which in Xonotic content lives under gfx/. DP excludes the font
        // from this category; fonts never reach this path here (Godot owns font rasterisation), so there is
        // nothing extra to exclude.
        if (vpath.StartsWith("gfx/", StringComparison.OrdinalIgnoreCase))
            return TexCategory.TwoD;

        return TexCategory.Color;
    }
}
