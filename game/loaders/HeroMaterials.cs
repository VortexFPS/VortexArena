using System;
using Godot;
using VortexArena.Formats.Materials;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The "hero material" override table: a small set of named-and-keyworded Quake 3 / Darkplaces shaders
/// whose visual intent (translucent water, a mirror/portal/camera view, an additive force field) the
/// generic <see cref="ShaderCompiler"/> path cannot reproduce — left to it, they degrade to a plain or
/// white surface. <see cref="TryOverride"/> recognizes them up front and emits a purpose-built Godot
/// material instead.
///
/// <para>Recognition keys, drawn from the real Xonotic <c>scripts/*.shader</c> set
/// (<c>liquids_water.shader</c>, <c>effects_forcefield.shader</c>, <c>effects_warpzone.shader</c>,
/// <c>portals.shader</c>) and the Darkplaces extension keywords:</para>
/// <list type="bullet">
///   <item><b>Water</b> — <c>surfaceparm water</c> or a <c>dpwater</c>/<c>dpwaterscroll</c> directive →
///   a translucent blue-green surface that scrolls its diffuse (or a flat tint when the diffuse is
///   missing), so a pool reads as water rather than an opaque or invisible plane.</item>
///   <item><b>Portal / mirror / camera</b> — a <c>dpcamera</c> directive, or a name under
///   <c>portals/</c> / <c>effects_warpzone/</c> / containing <c>mirror</c> → a dark, low-roughness,
///   metallic placeholder (a screen-space portal/mirror render is out of scope; this at least reads as a
///   reflective gateway, not a flat texture).</item>
///   <item><b>Force field</b> — a name under <c>effects_forcefield/</c> (or a <c>dpwater</c>-less
///   additive <c>trans</c>+<c>nomarks</c> field shader) → an additive, unshaded, two-sided translucent
///   surface, the energy-wall look Q3 gets from <c>blendfunc GL_ONE GL_ONE</c>.</item>
/// </list>
///
/// <para>The override deliberately keeps the surface's own diffuse texture where one resolves (so a
/// specific water/forcefield image still shows through the effect) and only synthesizes a tint when it
/// does not. Returns <c>null</c> when the shader is not a known hero shader, leaving the generic compiler
/// in charge.</para>
/// </summary>
internal static class HeroMaterials
{
    /// <summary>A resource-name marker so a caller can tell a surface was produced by the hero table.</summary>
    public const string Marker = "__hero__";

    /// <summary>
    /// Try to produce a hero-material override for <paramref name="def"/>, or null if it is not a recognized
    /// hero shader. Textures are resolved through <paramref name="ctx"/>; the returned material is never the
    /// fallback checkerboard (a hero shader with a missing image still gets its synthesized effect tint).
    /// </summary>
    public static Material? TryOverride(ShaderDef def, AssetSystem ctx)
    {
        if (def is null || ctx is null)
            return null;

        HeroKind kind = Classify(def);
        return kind switch
        {
            HeroKind.Water => BuildWater(def, ctx),
            HeroKind.Portal => BuildPortal(def, ctx),
            HeroKind.ForceField => BuildForceField(def, ctx),
            _ => null,
        };
    }

    private enum HeroKind { None, Water, Portal, ForceField }

    /// <summary>
    /// Classify a shader into a hero kind from its surfaceparms, DP extensions, and name. Order matters:
    /// a <c>dpcamera</c> portal that also happens to be flagged <c>trans</c> is a portal, not a force field,
    /// so the explicit DP/name keys are checked before the generic additive-field fallback.
    /// </summary>
    private static HeroKind Classify(ShaderDef def)
    {
        string name = (def.Name ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        DpExtensions dp = def.Dp;

        // ---- portal / mirror / camera ----
        // dpcamera renders the surface as a camera view (warpzones/portals). Names cover the Xonotic
        // portal vortex shaders and the warpzone "wavy" camera plane.
        if (dp.Camera
            || name.Contains("/portals/") || name.StartsWith("portals/", StringComparison.Ordinal)
            || name.Contains("portals_") || name.Contains("/effects_warpzone/")
            || name.Contains("mirror"))
            return HeroKind.Portal;

        // ---- water ----
        if (dp.Water != null || dp.WaterScroll != null || def.SurfaceParms.Contains("water"))
            return HeroKind.Water;

        // ---- force field ----
        if (name.Contains("/effects_forcefield/") || name.Contains("forcefield"))
            return HeroKind.ForceField;

        return HeroKind.None;
    }

    // -------------------------------------------------------------------------------------------------
    //  Water
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A translucent water surface. Uses the shader's own diffuse where it resolves (most Xonotic water has
    /// a <c>water0.tga</c>-style stage), modulated toward a blue-green tint and scrolled slowly in a
    /// generated spatial shader so the surface visibly moves. The water alpha follows the <c>dpwater</c>
    /// wateralpha when present (DP's <c>WaterAlpha</c>), else a sensible translucent default.
    /// </summary>
    private static Material BuildWater(ShaderDef def, AssetSystem ctx)
    {
        Texture2D? albedo = ResolveFirstStageTexture(def, ctx);

        // dpwater carries a per-shader water alpha and a reflect color we fold into the tint.
        DpWater? w = def.Dp.Water;
        float alpha = w != null ? Clamp01(w.WaterAlpha) : 0.55f;
        if (alpha <= 0f) alpha = 0.55f;

        // Tint toward the reflect color of the water (defaults to white → use a canonical blue-green).
        Color tint = w != null
            ? new Color(Clamp01(w.ReflectR), Clamp01(w.ReflectG), Clamp01(w.ReflectB))
            : Colors.White;
        // Bias a plain-white reflect (the common case) into the recognizable water blue-green.
        if (IsApproxWhite(tint))
            tint = new Color(0.20f, 0.45f, 0.55f);

        // Scroll rate: prefer dpwaterscroll, else a gentle default.
        float scrollX = 0.05f, scrollY = 0.03f;
        if (def.Dp.WaterScroll is { Length: >= 2 } ws)
        {
            scrollX = 0.05f * ws[0];
            scrollY = 0.03f * ws[1];
        }

        var shader = new Shader { Code = WaterShaderCode };
        var mat = new ShaderMaterial { Shader = shader, ResourceName = Combine(def.Name, Marker, "water") };
        mat.SetShaderParameter("albedo_tex", albedo ?? ctx.WhiteTexture());
        mat.SetShaderParameter("has_albedo", albedo != null);
        mat.SetShaderParameter("water_tint", tint);
        mat.SetShaderParameter("water_alpha", alpha);
        mat.SetShaderParameter("scroll", new Vector2(scrollX, scrollY));
        return mat;
    }

    private const string WaterShaderCode = @"// VortexArena hero water shader (Q3 surfaceparm water / dpwater). Generated in C#.
shader_type spatial;
render_mode cull_disabled, blend_mix, depth_draw_opaque, specular_schlick_ggx;

uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform bool has_albedo = false;
uniform vec3 water_tint : source_color = vec3(0.20, 0.45, 0.55);
uniform float water_alpha = 0.55;
uniform vec2 scroll = vec2(0.05, 0.03);

void fragment() {
    vec2 uv = UV + scroll * TIME;
    vec3 base = has_albedo ? texture(albedo_tex, uv).rgb : vec3(1.0);
    ALBEDO = base * water_tint;
    ALPHA = water_alpha;
    // Smooth, faintly metallic so reflections/speculars read like a liquid surface.
    ROUGHNESS = 0.08;
    METALLIC = 0.30;
}
";

    // -------------------------------------------------------------------------------------------------
    //  Portal / mirror / camera
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A reflective dark placeholder for a portal/mirror/camera surface. A real planar reflection or
    /// camera render is out of scope here, so the surface is made smooth + metallic (Godot reflects the
    /// environment/sky and reflection probes off it) over a dark base, reading as a portal gateway rather
    /// than a flat texture. Two-sided because portal planes are typically <c>cull none</c>.
    /// </summary>
    private static Material BuildPortal(ShaderDef def, AssetSystem ctx)
    {
        // Keep the portal's own diffuse if it has one (the vortex texture), tinted dark; otherwise a near-black mirror.
        Texture2D? albedo = ResolveFirstStageTexture(def, ctx);

        var mat = new StandardMaterial3D
        {
            ResourceName = Combine(def.Name, Marker, "portal"),
            AlbedoColor = albedo != null ? new Color(0.35f, 0.35f, 0.45f, 1f) : new Color(0.04f, 0.05f, 0.08f, 1f),
            Metallic = 0.9f,
            Roughness = 0.08f,
            MetallicSpecular = 0.9f,
            CullMode = def.Cull == CullMode.None
                ? BaseMaterial3D.CullModeEnum.Disabled
                : BaseMaterial3D.CullModeEnum.Back,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (albedo != null)
            mat.AlbedoTexture = albedo;
        return mat;
    }

    // -------------------------------------------------------------------------------------------------
    //  Force field
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// An additive, two-sided, unshaded translucent force field — the energy-wall look Q3 builds from
    /// <c>blendfunc GL_ONE GL_ONE</c> over a scrolling/turbulent texture. Uses the shader's own field
    /// texture where present; emissive-white when it is not, so the field still glows.
    /// </summary>
    private static Material BuildForceField(ShaderDef def, AssetSystem ctx)
    {
        Texture2D? albedo = ResolveFirstStageTexture(def, ctx);

        var mat = new StandardMaterial3D
        {
            ResourceName = Combine(def.Name, Marker, "forcefield"),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // force fields are cull none
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (albedo != null)
            mat.AlbedoTexture = albedo;
        else
            mat.AlbedoColor = new Color(0.3f, 0.6f, 1.0f, 1f); // faint blue energy when no field texture
        return mat;
    }

    // -------------------------------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>Resolve the texture of the shader's first stage that names a real image, or null.</summary>
    private static Texture2D? ResolveFirstStageTexture(ShaderDef def, AssetSystem ctx)
    {
        foreach (ShaderStage s in def.Stages)
        {
            string img = !string.IsNullOrEmpty(s.MapTexture)
                ? s.MapTexture
                : (s.AnimMap is { Frames.Length: > 0 } ? s.AnimMap.Frames[0] : string.Empty);
            if (string.IsNullOrEmpty(img) || img == "-" || img.StartsWith('$'))
                continue;
            Texture2D? tex = ctx.LoadTexture(img);
            if (tex != null)
                return tex;
        }
        // Fall back to the shader name as a texture (many world shaders are named after their diffuse).
        return string.IsNullOrEmpty(def.Name) ? null : ctx.LoadTexture(def.Name);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static bool IsApproxWhite(Color c) => c.R > 0.95f && c.G > 0.95f && c.B > 0.95f;

    private static string Combine(string? name, params string[] markers)
    {
        string s = string.IsNullOrEmpty(name) ? string.Empty : name!;
        foreach (string m in markers)
            s = string.IsNullOrEmpty(s) ? m : s + " " + m;
        return s;
    }
}
