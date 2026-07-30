namespace XonoticGodot.Formats.Materials;

/// <summary>
/// The OpenGL blend factors a Quake 3 <c>blendFunc</c> can name, stored verbatim so the
/// material compiler can decide how to fold them. The two-token shorthands
/// (<c>add</c>/<c>filter</c>/<c>blend</c>/<c>addalpha</c>) are expanded to their factor pair at
/// parse time, matching Darkplaces (<c>Mod_LoadQ3Shaders</c>).
/// </summary>
public enum BlendFactor
{
    /// <summary>No blendFunc was given on the stage (DP default: <c>GL_ONE GL_ZERO</c>, i.e. opaque).</summary>
    None = 0,
    One,
    Zero,
    SrcColor,
    OneMinusSrcColor,
    SrcAlpha,
    OneMinusSrcAlpha,
    DstColor,
    OneMinusDstColor,
    DstAlpha,
    OneMinusDstAlpha,
}

/// <summary>
/// A high-level classification of the stage's blend equation, derived from the
/// (<see cref="ShaderStage.BlendSrc"/>, <see cref="ShaderStage.BlendDst"/>) factor pair. The compiler
/// maps these to Godot transparency modes (see <c>asset-pipeline.md §4</c>):
/// <list type="bullet">
/// <item><see cref="Opaque"/> — <c>GL_ONE GL_ZERO</c> (or no blendFunc) → no transparency.</item>
/// <item><see cref="Add"/> — <c>GL_ONE GL_ONE</c> → <c>blend_add</c>.</item>
/// <item><see cref="Filter"/> — <c>GL_DST_COLOR GL_ZERO</c> or <c>GL_ZERO GL_SRC_COLOR</c> → <c>blend_mul</c>.</item>
/// <item><see cref="Blend"/> — <c>GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA</c> → <c>blend_mix</c>.</item>
/// <item><see cref="Custom"/> — any other factor pair; consult the raw factors.</item>
/// </list>
/// </summary>
public enum BlendMode
{
    Opaque = 0,
    Add,
    Filter,
    Blend,
    Custom,
}

/// <summary>
/// The <c>rgbGen</c> / <c>alphaGen</c> generator function. Names map 1:1 to the Darkplaces
/// <c>Q3RGBGEN_*</c> / <c>Q3ALPHAGEN_*</c> enums. Generators that are only valid for one of the two
/// (e.g. <see cref="LightingDiffuse"/> for rgb, <see cref="LightingSpecular"/>/<see cref="Portal"/> for
/// alpha) share this enum; the parser stores whatever the shader named without cross-validating.
/// <see cref="Wave"/> carries an associated <see cref="WaveForm"/> in <see cref="ColorGen"/>.
/// </summary>
public enum ColorGenType
{
    /// <summary>Not specified. DP default is <see cref="Identity"/>.</summary>
    Unset = 0,
    Identity,
    IdentityLighting,
    Const,
    Entity,
    OneMinusEntity,
    Vertex,
    ExactVertex,
    OneMinusVertex,
    LightingDiffuse,    // rgbGen only
    LightingSpecular,   // alphaGen only
    Portal,             // alphaGen only
    Wave,
}

/// <summary>
/// A Quake 3 waveform function used by <c>rgbGen/alphaGen wave</c>, <c>tcMod stretch</c>,
/// <c>tcMod turb</c>, and <c>deformVertexes wave/move/bulge</c>. Matches DP's
/// <c>Mod_LoadQ3Shaders_EnumerateWaveFunc</c> (the <c>user&lt;n&gt;</c> prefix DP supports is rare and is
/// folded onto the base function here; the original spelling is preserved in <see cref="WaveForm.RawName"/>).
/// </summary>
public enum WaveFunc
{
    None = 0,
    Sin,
    Square,
    Triangle,
    Sawtooth,
    InverseSawtooth,
    Noise,
}

/// <summary>
/// A parsed waveform: <c>func base amplitude phase freq</c>. The four parms are the standard Q3
/// wave parameters (base, amplitude, phase, frequency); fewer may be present for malformed input,
/// in which case the trailing values stay 0.
/// </summary>
public sealed class WaveForm
{
    public WaveFunc Func { get; init; } = WaveFunc.Sin;

    /// <summary>The function token as written (e.g. <c>"sin"</c>, <c>"user1sin"</c>), for faithful round-trip.</summary>
    public string RawName { get; init; } = "sin";

    /// <summary>Wave parameters in order: [0]=base, [1]=amplitude, [2]=phase, [3]=frequency.</summary>
    public float[] Parms { get; init; } = System.Array.Empty<float>();

    public float Base => Parms.Length > 0 ? Parms[0] : 0f;
    public float Amplitude => Parms.Length > 1 ? Parms[1] : 0f;
    public float Phase => Parms.Length > 2 ? Parms[2] : 0f;
    public float Frequency => Parms.Length > 3 ? Parms[3] : 0f;
}

/// <summary>A <c>rgbGen</c> or <c>alphaGen</c> directive: a generator plus its numeric/wave parameters.</summary>
public sealed class ColorGen
{
    public ColorGenType Type { get; init; } = ColorGenType.Unset;

    /// <summary>Numeric parms for <c>const</c> (rgb=3 floats, alpha=1 float) etc. Empty for parameterless gens.</summary>
    public float[] Parms { get; init; } = System.Array.Empty<float>();

    /// <summary>Set only when <see cref="Type"/> is <see cref="ColorGenType.Wave"/>.</summary>
    public WaveForm? Wave { get; init; }
}

/// <summary>The kind of a <c>tcMod</c> operation. Matches DP's <c>Q3TCMOD_*</c> set.</summary>
public enum TcModType
{
    Scale = 0,
    Scroll,
    Rotate,
    Stretch,
    Turb,
    Transform,
    EntityTranslate,
    Page,
}

/// <summary>
/// One <c>tcMod</c> operation on a stage. Q3 applies these in the order written, so the stage keeps an
/// ordered <see cref="System.Collections.Generic.List{T}"/> of them; preserve that order in the compiler.
/// <list type="bullet">
/// <item><c>scale sx sy</c> → Parms[0..1]</item>
/// <item><c>scroll sx sy</c> → Parms[0..1] (units per second)</item>
/// <item><c>rotate deg</c> → Parms[0] (degrees per second)</item>
/// <item><c>stretch &lt;wave&gt;</c> → <see cref="Wave"/></item>
/// <item><c>turb base amp phase freq</c> → Parms[0..3]</item>
/// <item><c>transform m00 m01 m10 m11 t0 t1</c> → Parms[0..5]</item>
/// </list>
/// </summary>
public sealed class TcMod
{
    public TcModType Type { get; init; }

    /// <summary>The raw type token (e.g. <c>"scale"</c>), preserved for diagnostics/round-trip.</summary>
    public string RawType { get; init; } = string.Empty;

    /// <summary>Numeric parameters, in source order. For <c>stretch</c> the wave is in <see cref="Wave"/> instead.</summary>
    public float[] Parms { get; init; } = System.Array.Empty<float>();

    /// <summary>Set only for <see cref="TcModType.Stretch"/>.</summary>
    public WaveForm? Wave { get; init; }

    public float P(int i) => i >= 0 && i < Parms.Length ? Parms[i] : 0f;
}

/// <summary>The <c>tcGen</c> texture-coordinate source. Matches DP's <c>Q3TCGEN_*</c>.</summary>
public enum TcGenType
{
    /// <summary>Not specified. DP default is <see cref="Texture"/> (a.k.a. <c>base</c>).</summary>
    Unset = 0,
    Texture,
    Lightmap,
    Environment,
    Vector,
}

/// <summary>
/// A <c>tcGen</c> directive. <c>vector</c> carries two 3-float axes (Parms[0..5]); the others are
/// parameterless in practice.
/// </summary>
public sealed class TcGen
{
    public TcGenType Type { get; init; } = TcGenType.Unset;
    public float[] Parms { get; init; } = System.Array.Empty<float>();
}

/// <summary>
/// An <c>animMap</c> / <c>animClampMap</c> directive: cycle through <see cref="Frames"/> at
/// <see cref="Fps"/> frames per second. <see cref="Clamp"/> is true for <c>animClampMap</c>.
/// </summary>
public sealed class AnimMap
{
    public float Fps { get; init; }
    public string[] Frames { get; init; } = System.Array.Empty<string>();
    public bool Clamp { get; init; }
}

/// <summary>
/// One render pass ("stage" / "layer") of a Q3 material — the <c>{ ... }</c> block inside a shader.
///
/// Field semantics follow Darkplaces' <c>q3shaderinfo_layer_t</c>. Everything the parser does not
/// model explicitly is dropped at the stage level (stages have no raw bag; unknown stage directives
/// are rare and non-load-bearing), but every directive in the DP keyword set is represented below.
/// </summary>
public sealed class ShaderStage
{
    /// <summary>
    /// The texture named by <c>map</c>/<c>clampmap</c>. May be a special token:
    /// <c>$lightmap</c> (use the BSP lightmap), <c>$whiteimage</c>/<c>$white</c> (a 1×1 white texture),
    /// or <c>-</c>. Empty when the stage only had <c>animMap</c> (see <see cref="AnimMap"/>) or no map at all.
    /// Path has no extension in many model shaders — the VFS resolver picks .tga/.jpg/.png.
    /// </summary>
    public string MapTexture { get; set; } = string.Empty;

    /// <summary>True if this stage's image is <c>$lightmap</c>.</summary>
    public bool IsLightmap =>
        string.Equals(MapTexture, "$lightmap", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this stage's image is the engine white image (<c>$whiteimage</c>/<c>$white</c>).</summary>
    public bool IsWhiteImage =>
        string.Equals(MapTexture, "$whiteimage", System.StringComparison.OrdinalIgnoreCase) ||
        string.Equals(MapTexture, "$white", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Source blend factor (first arg of <c>blendFunc</c>). <see cref="BlendFactor.None"/> if unset.</summary>
    public BlendFactor BlendSrc { get; set; } = BlendFactor.None;

    /// <summary>Destination blend factor (second arg of <c>blendFunc</c>). <see cref="BlendFactor.None"/> if unset.</summary>
    public BlendFactor BlendDst { get; set; } = BlendFactor.None;

    /// <summary>The raw <c>blendFunc</c> arguments as written (e.g. <c>["GL_ONE","GL_ONE"]</c> or <c>["add"]</c>).</summary>
    public string[] BlendRaw { get; set; } = System.Array.Empty<string>();

    /// <summary>High-level blend classification derived from <see cref="BlendSrc"/>/<see cref="BlendDst"/>.</summary>
    public BlendMode BlendMode { get; set; } = BlendMode.Opaque;

    /// <summary>Whether a <c>blendFunc</c> was present at all (distinguishes explicit <c>GL_ONE GL_ZERO</c> from default).</summary>
    public bool HasBlendFunc { get; set; }

    /// <summary><c>rgbGen</c> directive, or null if unset (DP default: identity).</summary>
    public ColorGen? RgbGen { get; set; }

    /// <summary><c>alphaGen</c> directive, or null if unset (DP default: identity).</summary>
    public ColorGen? AlphaGen { get; set; }

    /// <summary>
    /// The <c>alphaFunc</c> threshold test as written (e.g. <c>"GE128"</c>, <c>"GT0"</c>, <c>"LT128"</c>),
    /// or null if none. DP only records that an alpha test exists (sets <c>TEXF_ALPHA</c>); we keep the
    /// exact comparator so the compiler can pick the right cutoff. Presence implies alpha-test rendering.
    /// </summary>
    public string? AlphaFunc { get; set; }

    /// <summary>Ordered list of <c>tcMod</c> operations; apply in this order.</summary>
    public System.Collections.Generic.List<TcMod> TcMods { get; } = new();

    /// <summary><c>tcGen</c>/<c>texGen</c> directive, or null if unset (DP default: texture).</summary>
    public TcGen? TcGen { get; set; }

    /// <summary>True for <c>clampmap</c>/<c>animClampMap</c> (clamp UVs instead of repeat).</summary>
    public bool ClampMap { get; set; }

    /// <summary>True if the stage had <c>depthWrite</c>.</summary>
    public bool DepthWrite { get; set; }

    /// <summary>The <c>depthFunc</c> argument as written (e.g. <c>"equal"</c>, <c>"lequal"</c>), or null.</summary>
    public string? DepthFunc { get; set; }

    /// <summary>Set when the stage used <c>animMap</c>/<c>animClampMap</c> (in which case <see cref="MapTexture"/> is usually empty).</summary>
    public AnimMap? AnimMap { get; set; }

    /// <summary>True if the stage had <c>detail</c> (a detail pass; ignorable for rendering correctness).</summary>
    public bool Detail { get; set; }
}
