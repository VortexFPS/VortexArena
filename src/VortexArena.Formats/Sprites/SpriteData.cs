namespace XonoticGodot.Formats.Sprites;

/// <summary>
/// Engine-neutral, Godot-free representation of a Quake-family sprite, parsed by <see cref="SpriteReader"/>.
/// Covers four on-disk formats, all loaded by Darkplaces <c>model_sprite.c</c>:
/// <list type="bullet">
///   <item><b>spr</b> ("IDSP" v1): 8-bit paletted frames (Quake palette).</item>
///   <item><b>sprhl</b> ("IDSP" v2): Half-Life sprite, 8-bit with an embedded 256-color palette + rendermode.</item>
///   <item><b>spr32</b> ("IDSP" v32): 32-bit BGRA frames (decoded to RGBA here).</item>
///   <item><b>sp2</b> ("IDS2" v2): Quake2 sprite; frames are references to external image files (no pixels).</item>
/// </list>
///
/// A sprite is a flat list of <see cref="SpriteFrame"/>s. "IDSP" sprites support animation groups
/// (a frame entry can be a single image or a group of N images with intervals); we flatten groups into
/// individual frames (matching DP's <c>realframes</c>) and expose the grouping via
/// <see cref="GroupRanges"/> so a host can rebuild the animation timing if desired.
/// </summary>
public sealed class SpriteData
{
    /// <summary>The concrete file format that was parsed.</summary>
    public SpriteFormat Format { get; init; }

    /// <summary>
    /// The sprite's orientation/billboard type (<c>SPR_VP_PARALLEL</c>, <c>SPR_ORIENTED</c>,
    /// <c>SPR_LABEL</c>, <c>SPR_OVERHEAD</c>, ...). For sp2 DP forces <see cref="SpriteType.VpParallel"/>.
    /// </summary>
    public SpriteType SpriteType { get; init; }

    /// <summary>
    /// Half-Life render mode (only meaningful when <see cref="Format"/> is <see cref="SpriteFormat.SprHl"/>);
    /// otherwise <see cref="SpriteHlRenderMode.Opaque"/>. Additive sprites render with additive blending.
    /// </summary>
    public SpriteHlRenderMode HlRenderMode { get; init; }

    /// <summary>
    /// True when the sprite renders additively (HL additive rendermode). The Godot builder should set an
    /// additive blend material for these.
    /// </summary>
    public bool Additive { get; init; }

    /// <summary>Number of flattened frames; equals <c>Frames.Length</c>.</summary>
    public int FrameCount => Frames.Length;

    /// <summary>All frames, groups flattened, in file order.</summary>
    public SpriteFrame[] Frames { get; init; } = Array.Empty<SpriteFrame>();

    /// <summary>
    /// One entry per top-level frame slot in the file. A single-image slot is a group of size 1; a real
    /// animation group has size N with per-frame intervals (seconds). Indices reference <see cref="Frames"/>.
    /// </summary>
    public SpriteGroup[] GroupRanges { get; init; } = Array.Empty<SpriteGroup>();
}

/// <summary>The four sprite file formats handled by <see cref="SpriteReader"/>.</summary>
public enum SpriteFormat
{
    /// <summary>"IDSP" version 1: 8-bit paletted (Quake palette, not embedded).</summary>
    Spr,
    /// <summary>"IDSP" version 2: Half-Life, 8-bit with embedded palette + rendermode.</summary>
    SprHl,
    /// <summary>"IDSP" version 32: 32-bit BGRA (decoded to RGBA).</summary>
    Spr32,
    /// <summary>"IDS2" version 2: Quake2, external image references (.sp2).</summary>
    Sp2,
}

/// <summary>
/// Sprite orientation/billboard type. Values match the <c>SPR_*</c> constants in Darkplaces
/// <c>spritegn.h</c> so they round-trip with the stored <c>type</c> field.
/// </summary>
public enum SpriteType
{
    VpParallelUpright = 0,
    FacingUpright = 1,
    VpParallel = 2,
    Oriented = 3,
    VpParallelOriented = 4,
    Label = 5,
    LabelScale = 6,
    Overhead = 7,
}

/// <summary>Half-Life sprite render modes (<c>SPRHL_*</c> in <c>model_sprite.c</c>).</summary>
public enum SpriteHlRenderMode
{
    Opaque = 0,
    Additive = 1,
    IndexAlpha = 2,
    AlphaTest = 3,
}

/// <summary>
/// A top-level frame slot. <see cref="FirstFrame"/>/<see cref="FrameCount"/> index into
/// <see cref="SpriteData.Frames"/>. For a single image, <see cref="FrameCount"/> is 1 and
/// <see cref="Intervals"/> is empty; for a group, <see cref="Intervals"/> has one cumulative-free
/// per-frame display time in seconds (as stored; DP rejects intervals &lt; 0.01).
/// </summary>
public readonly record struct SpriteGroup(int FirstFrame, int FrameCount, float[] Intervals);

/// <summary>
/// A single decoded sprite frame.
///
/// The placement fields are stored as a signed pixel <see cref="OriginX"/>/<see cref="OriginY"/> offset plus
/// <see cref="Width"/>/<see cref="Height"/>. DP derives a quad from these (left/right/up/down); we keep the
/// raw origin so the Godot builder can apply whichever convention it needs. Note the sign convention differs
/// between formats and is normalized here to "spr/spr32/hl" semantics:
/// <list type="bullet">
///   <item>spr/spr32/hl: left = originX, right = originX + width, up = originY, down = originY - height.</item>
///   <item>sp2 on disk uses the opposite X sign; <see cref="SpriteReader"/> negates it on load so the
///         left/right/up/down derivation above holds uniformly. See <see cref="QuadLeft"/> etc.</item>
/// </list>
///
/// Pixel data: for spr32 and sprhl, <see cref="Rgba"/> holds decoded 8-bit-per-channel RGBA
/// (<see cref="Width"/> * <see cref="Height"/> * 4 bytes, row-major top-to-bottom). For plain spr (Quake
/// palette), <see cref="Indices"/> holds the raw 8-bit palette indices and <see cref="Rgba"/> is null
/// (the Quake palette is not embedded in the file — see <see cref="SpriteReader"/> remarks). For sp2,
/// both are null and <see cref="ExternalImage"/> names the image file to load instead.
/// </summary>
public sealed class SpriteFrame
{
    /// <summary>Signed X origin offset in pixels (normalized to spr/spr32 sign convention).</summary>
    public int OriginX { get; init; }

    /// <summary>Signed Y origin offset in pixels.</summary>
    public int OriginY { get; init; }

    /// <summary>Frame width in pixels (0 is legal, e.g. Nehahra null.spr).</summary>
    public int Width { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>
    /// Decoded RGBA8 pixels (Width*Height*4, row-major). Non-null for spr32 and sprhl. Null for plain spr
    /// (see <see cref="Indices"/>) and for sp2 (see <see cref="ExternalImage"/>).
    /// </summary>
    public byte[]? Rgba { get; init; }

    /// <summary>
    /// Raw 8-bit palette indices (Width*Height) for plain "IDSP" v1 sprites. The host must colour these
    /// through the Quake palette (gfx/palette.lmp). Null unless <see cref="SpriteFrame"/> is a plain-spr frame.
    /// </summary>
    public byte[]? Indices { get; init; }

    /// <summary>
    /// External image filename for sp2 frames (the Quake2 <c>name[64]</c>, typically a .pcx/.tga path).
    /// Null for all "IDSP" formats. The image is resolved through the VFS by the host.
    /// </summary>
    public string? ExternalImage { get; init; }

    /// <summary>DP quad bounds derived from origin/size (left edge X).</summary>
    public int QuadLeft => OriginX;
    /// <summary>DP quad bounds derived from origin/size (right edge X).</summary>
    public int QuadRight => OriginX + Width;
    /// <summary>DP quad bounds derived from origin/size (top edge Y).</summary>
    public int QuadUp => OriginY;
    /// <summary>DP quad bounds derived from origin/size (bottom edge Y).</summary>
    public int QuadDown => OriginY - Height;
}
