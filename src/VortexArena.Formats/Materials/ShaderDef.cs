namespace XonoticGodot.Formats.Materials;

/// <summary>Back-face culling mode from the <c>cull</c> directive.</summary>
public enum CullMode
{
    /// <summary>Not specified — Q3 default is <see cref="Front"/> (cull back faces).</summary>
    Unset = 0,

    /// <summary>Default: cull back faces (single-sided). Also written <c>cull front</c>.</summary>
    Front,

    /// <summary>Cull front faces (rare; <c>cull back</c>).</summary>
    Back,

    /// <summary>No culling — two-sided. <c>cull none</c>/<c>disable</c>/<c>twosided</c>. DP sets <c>TWOSIDED</c>.</summary>
    None,
}

/// <summary>The kind of a <c>deformVertexes</c> directive. Matches DP's <c>Q3DEFORM_*</c>.</summary>
public enum DeformType
{
    Wave = 0,
    Normal,
    Bulge,
    Move,
    Autosprite,
    Autosprite2,
    ProjectionShadow,
    Text0, Text1, Text2, Text3, Text4, Text5, Text6, Text7,
}

/// <summary>
/// A parsed <c>deformVertexes</c> directive.
/// <list type="bullet">
/// <item><c>wave &lt;div&gt; &lt;wave&gt;</c> — Parms[0]=spread/div, <see cref="Wave"/> holds the waveform.</item>
/// <item><c>bulge &lt;width&gt; &lt;height&gt; &lt;speed&gt;</c> — Parms[0..2].</item>
/// <item><c>move &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;wave&gt;</c> — Parms[0..2]=vector, <see cref="Wave"/> holds the waveform.</item>
/// <item><c>normal &lt;amp&gt; &lt;freq&gt;</c> — Parms[0..1].</item>
/// <item><c>autosprite</c>/<c>autosprite2</c> — no parms (billboard).</item>
/// </list>
/// </summary>
public sealed class DeformVertexes
{
    public DeformType Type { get; init; }

    /// <summary>The raw type token (e.g. <c>"wave"</c>, <c>"autosprite"</c>).</summary>
    public string RawType { get; init; } = string.Empty;

    /// <summary>Leading numeric parameters (their meaning depends on <see cref="Type"/>; see class remarks).</summary>
    public float[] Parms { get; init; } = System.Array.Empty<float>();

    /// <summary>Waveform for <c>wave</c> and <c>move</c>; null otherwise.</summary>
    public WaveForm? Wave { get; init; }
}

/// <summary>
/// The <c>skyParms &lt;farbox&gt; &lt;cloudheight&gt; &lt;nearbox&gt;</c> directive (a sky shader). A token of
/// <c>-</c> means "none" for that slot. DP also treats a bare <c>sky &lt;name&gt;</c> as a skybox name and
/// always sets the <c>sky</c> surfaceparm; <see cref="ShaderDef.SurfaceParms"/> will contain <c>"sky"</c>.
/// </summary>
public sealed class SkyParms
{
    /// <summary>Far skybox basename (e.g. <c>env/exosystem2/exosystem2</c>), or null if <c>-</c>.</summary>
    public string? FarBox { get; init; }

    /// <summary>Cloud height token (often <c>-</c>); kept as written, or null if <c>-</c>.</summary>
    public string? CloudHeight { get; init; }

    /// <summary>Near skybox basename, or null if <c>-</c>.</summary>
    public string? NearBox { get; init; }
}

/// <summary>
/// The <c>fogParms ( r g b ) &lt;distance&gt;</c> directive. Not used by current Xonotic maps but part of
/// the Q3 format, so it is parsed when present. Color is the three floats; <see cref="Distance"/> is the
/// opacity distance.
/// </summary>
public sealed class FogParms
{
    public float Red { get; init; }
    public float Green { get; init; }
    public float Blue { get; init; }
    public float Distance { get; init; }
}

/// <summary>
/// Darkplaces shader extensions captured as structured fields (the <c>dp*</c> family from
/// <c>Mod_LoadQ3Shaders</c>). Defaults mirror DP's per-shader init block. The material compiler reads
/// these to drive normal/gloss/reflection/refraction/water effects that have no stock Q3 equivalent.
/// Note: DP rewrites a leading <c>dp_</c> on the keyword to <c>dp</c> (so <c>dp_water</c> ≡ <c>dpwater</c>,
/// <c>dp_reflect</c> ≡ <c>dpreflect</c>); this parser applies the same remap before matching.
/// </summary>
public sealed class DpExtensions
{
    /// <summary><c>dpglosstexture &lt;tex&gt;</c> — explicit gloss/specular map path, or null.</summary>
    public string? GlossTexture { get; set; }

    /// <summary><c>dpreflectcube &lt;tex&gt;</c> — cubemap basename for reflections, or null.</summary>
    public string? ReflectCube { get; set; }

    /// <summary>True if <c>dpnortlight</c> (do not receive realtime lights).</summary>
    public bool NoRtLight { get; set; }

    /// <summary>True if <c>dpshadow</c> (force shadow casting).</summary>
    public bool Shadow { get; set; }

    /// <summary>True if <c>dpnoshadow</c> (suppress shadow casting).</summary>
    public bool NoShadow { get; set; }

    /// <summary>True if <c>dpmeshcollisions</c> (collide against the triangle mesh, not the brush).</summary>
    public bool MeshCollisions { get; set; }

    /// <summary>True if <c>dpcamera</c> (render this surface as a camera/portal view).</summary>
    public bool Camera { get; set; }

    /// <summary>
    /// Offset/parallax mapping. Null means the directive was absent (use engine default). When present,
    /// <see cref="OffsetMapping.Mode"/> is the mode token and the scale/bias parms are captured.
    /// </summary>
    public OffsetMapping? OffsetMapping { get; set; }

    /// <summary><c>dpglossintensitymod &lt;x&gt;</c> — specular scale multiplier (DP default 1).</summary>
    public float? GlossIntensityMod { get; set; }

    /// <summary><c>dpglossexponentmod &lt;x&gt;</c> — specular power multiplier (DP default 1).</summary>
    public float? GlossExponentMod { get; set; }

    /// <summary><c>dprtlightambient &lt;x&gt;</c> — ambient added under realtime lights (DP default 0).</summary>
    public float? RtLightAmbient { get; set; }

    /// <summary><c>dpreflect &lt;factor&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt; &lt;a&gt;</c> planar reflection, or null.</summary>
    public DpReflect? Reflect { get; set; }

    /// <summary><c>dprefract &lt;factor&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt;</c> refraction, or null.</summary>
    public DpRefract? Refract { get; set; }

    /// <summary><c>dpwater</c>/<c>dp_water</c> (12 args) — combined reflect+refract water surface, or null.</summary>
    public DpWater? Water { get; set; }

    /// <summary><c>dpwaterscroll &lt;a&gt; &lt;b&gt;</c> — normal-map scroll for water (stored as raw args).</summary>
    public float[]? WaterScroll { get; set; }

    /// <summary><c>dptransparentsort &lt;sky|distance|hud&gt;</c> category, as written, or null.</summary>
    public string? TransparentSort { get; set; }

    /// <summary><c>dppolygonoffset [factor] [offset]</c> — present flag plus optional factor/offset.</summary>
    public bool PolygonOffset { get; set; }

    /// <summary>Optional [factor, offset] for <c>dppolygonoffset</c>; null when bare or absent.</summary>
    public float[]? PolygonOffsetParms { get; set; }
}

/// <summary><c>dpoffsetmapping &lt;mode&gt; [scale] [bias|match|match8|match16 &lt;v&gt;]</c>.</summary>
public sealed class OffsetMapping
{
    /// <summary>Mode token as written: <c>off</c>/<c>none</c>/<c>disable</c>, <c>default</c>/<c>normal</c>, <c>linear</c>, <c>relief</c>, or <c>-</c>.</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>Optional scale (arg 2); null if absent.</summary>
    public float? Scale { get; init; }

    /// <summary>Optional bias kind token (<c>bias</c>/<c>match</c>/<c>match8</c>/<c>match16</c>); null if absent.</summary>
    public string? BiasKind { get; init; }

    /// <summary>Optional bias value paired with <see cref="BiasKind"/>; null if absent.</summary>
    public float? BiasValue { get; init; }
}

/// <summary><c>dpreflect factor r g b a</c>.</summary>
public sealed class DpReflect
{
    public float Factor { get; init; } = 1f;
    public float R { get; init; } = 1f;
    public float G { get; init; } = 1f;
    public float B { get; init; } = 1f;
    public float A { get; init; } = 1f;
}

/// <summary><c>dprefract factor r g b</c> (alpha is implicitly 1 in DP).</summary>
public sealed class DpRefract
{
    public float Factor { get; init; } = 1f;
    public float R { get; init; } = 1f;
    public float G { get; init; } = 1f;
    public float B { get; init; } = 1f;
}

/// <summary>
/// <c>dpwater reflectmin reflectmax refractfactor reflectfactor refractR refractG refractB reflectR
/// reflectG reflectB wateralpha</c> (11 numeric args after the keyword). Stored faithfully; the compiler
/// builds a water material from it.
/// </summary>
public sealed class DpWater
{
    public float ReflectMin { get; init; }
    public float ReflectMax { get; init; } = 1f;
    public float RefractFactor { get; init; } = 1f;
    public float ReflectFactor { get; init; } = 1f;
    public float RefractR { get; init; } = 1f;
    public float RefractG { get; init; } = 1f;
    public float RefractB { get; init; } = 1f;
    public float ReflectR { get; init; } = 1f;
    public float ReflectG { get; init; } = 1f;
    public float ReflectB { get; init; } = 1f;
    public float WaterAlpha { get; init; } = 1f;
}

/// <summary>
/// A parsed Quake 3 material ("shader"): a name, an ordered list of render <see cref="Stages"/>, and the
/// global directives that apply to the whole surface.
///
/// Modeled on Darkplaces' <c>shader_t</c> (<c>Mod_LoadQ3Shaders</c>). Directives that DP recognizes are
/// promoted to typed fields; <c>surfaceparm</c>s become a <see cref="SurfaceParms"/> set (kept verbatim,
/// case-folded — these are <b>gameplay</b> flags consumed by the BSP/model importer and must never be
/// dropped); compiler-only hints (<c>q3map_*</c>, <c>qer_*</c>) and any unrecognized line are kept in
/// <see cref="Raw"/> so nothing is silently lost.
/// </summary>
public sealed class ShaderDef
{
    /// <summary>
    /// The shader name exactly as written (e.g. <c>textures/map_x/foo</c> or <c>models/ctf/glow</c>).
    /// This is also the dictionary key, but matched case-insensitively. Forward slashes; no extension.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Render passes in source order. May be empty (a global-only / nodraw / compiler shader).</summary>
    public System.Collections.Generic.List<ShaderStage> Stages { get; } = new();

    /// <summary>
    /// <c>surfaceparm</c> names, case-folded to lower-case, deduplicated. Includes both engine-known parms
    /// (nodraw, nonsolid, nomarks, trans, nolightmap, sky, fog, lava, slime, water, playerclip, slick,
    /// detail, structural, …) and any custom parm names; the importer maps these to content/collision flags.
    /// </summary>
    public System.Collections.Generic.HashSet<string> SurfaceParms { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary><c>cull</c> mode. <see cref="CullMode.Unset"/> means default (cull back faces).</summary>
    public CullMode Cull { get; set; } = CullMode.Unset;

    /// <summary>True if <c>nopicmip</c> (do not downscale on low texture-quality settings).</summary>
    public bool NoPicmip { get; set; }

    /// <summary>True if <c>nomipmaps</c> (no mipmaps; also implies no picmip in DP).</summary>
    public bool NoMipmaps { get; set; }

    /// <summary>True if <c>polygonOffset</c> (push the surface toward the camera to avoid z-fighting; decals).</summary>
    public bool PolygonOffset { get; set; }

    /// <summary>Ordered list of <c>deformVertexes</c> directives.</summary>
    public System.Collections.Generic.List<DeformVertexes> Deforms { get; } = new();

    /// <summary>The <c>skyParms</c>/<c>sky</c> directive, or null. When set, <see cref="SurfaceParms"/> contains <c>"sky"</c>.</summary>
    public SkyParms? SkyParms { get; set; }

    /// <summary>The <c>fogParms</c> directive, or null.</summary>
    public FogParms? FogParms { get; set; }

    /// <summary>
    /// The <c>sort</c> directive. Q3 accepts a small set of named keys (portal, sky, opaque, banner,
    /// underwater, additive, nearest) or an explicit integer. Stored as written; <see cref="SortValue"/>
    /// resolves the named keys to their conventional integer when possible.
    /// </summary>
    public string? Sort { get; set; }

    /// <summary>
    /// The numeric sort priority resolved from <see cref="Sort"/> (named keys mapped to Q3's standard
    /// values, or the literal integer). Null if <see cref="Sort"/> is unset or unrecognized.
    /// </summary>
    public int? SortValue { get; set; }

    /// <summary>Darkplaces <c>dp*</c> extension fields. Always non-null; individual members are null/false when unset.</summary>
    public DpExtensions Dp { get; } = new();

    /// <summary>
    /// Every global directive line the parser did not promote to a typed field, keyed by the lower-cased
    /// first token. The value is the remainder of the line (arguments joined by single spaces). This is
    /// where <c>q3map_*</c> compiler hints, <c>qer_*</c> editor hints, and any unknown keyword land. On a
    /// duplicate key the last occurrence wins; the count is not preserved.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string> Raw { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Convenience: this material draws nothing (has the <c>nodraw</c> surfaceparm).</summary>
    public bool IsNoDraw => SurfaceParms.Contains("nodraw");

    /// <summary>Convenience: this material is a sky shader.</summary>
    public bool IsSky => SurfaceParms.Contains("sky") || SkyParms != null;

    /// <summary>Convenience: any stage references <c>$lightmap</c>.</summary>
    public bool UsesLightmap
    {
        get
        {
            foreach (var s in Stages)
                if (s.IsLightmap) return true;
            return false;
        }
    }
}
