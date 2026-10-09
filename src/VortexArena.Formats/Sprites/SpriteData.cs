using VortexArena.Formats.Images;

namespace VortexArena.Formats.Sprites;

/// <summary>
/// Engine-neutral, Godot-free representation of a Quake-family sprite, parsed by <see cref="SpriteReader"/>.
/// Covers four on-disk formats, all loaded by Darkplaces <c>model_sprite.c</c>:
/// <list type="bullet">
///   <item><b>spr</b> ("IDSP" v1): 8-bit paletted frames (Quake palette).</item>
///   <item><b>sprhl</b> ("IDSP" v2): Half-Life sprite, 8-bit with an embedded 256-color palette + rendermode.</item>
///   <item><b>spr32</b> ("IDSP" v32): 32-bit RGBA frames.</item>
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

    /// <summary>
    /// DarkPlaces' <c>animscenes</c>: one per top-level frame slot, which is what an entity's <c>.frame</c>
    /// selects (NOT an index into <see cref="Frames"/> once a sprite has groups). A single image is a run of
    /// one at 10 per second; a group plays at <c>1 / interval</c> of its FIRST interval, looping.
    /// </summary>
    public SpriteScene[] Scenes { get; init; } = Array.Empty<SpriteScene>();

    /// <summary>The header's <c>synctype</c> (0 synchronised, 1 random); 0 for sp2.</summary>
    public int SyncType { get; init; }

    /// <summary>
    /// DarkPlaces' model radius: the largest distance of any frame's corner from the origin. The model's box
    /// is the cube from <c>-Radius</c> to <c>Radius</c> on every axis.
    /// </summary>
    public float Radius { get; init; }
}

/// <summary>An <c>animscene_t</c> of a sprite: a run of <see cref="SpriteData.Frames"/> shown at a rate, looping.</summary>
public readonly record struct SpriteScene(string Name, int FirstFrame, int FrameCount, float FrameRate);

/// <summary>
/// How DarkPlaces orients each sprite type when it draws (<c>r_sprites.c</c>
/// <c>R_Model_Sprite_Draw_TransparentCallback</c>), as pure vector maths in Quake coordinates. The result is
/// the sprite's <c>left</c> and <c>up</c> axes, already multiplied by the entity's scale; the corners of a
/// frame are then <see cref="Corners"/>.
/// </summary>
public static class SpriteOrientation
{
    /// <summary>
    /// The <c>left</c> and <c>up</c> axes of a sprite. False for the two label types, which are sized in
    /// screen pixels and need the view's frustum (they are not handled here).
    /// </summary>
    /// <param name="type">The sprite's type; an unknown one draws as <see cref="SpriteType.VpParallel"/>.</param>
    /// <param name="origin">The entity's origin BEFORE the one-unit nudge toward the view.</param>
    /// <param name="scale">The entity's scale.</param>
    /// <param name="entityLeft">The entity matrix's left axis (scale included), for the oriented types.</param>
    /// <param name="entityUp">The entity matrix's up axis (scale included).</param>
    /// <param name="viewOrigin">The eye.</param>
    /// <param name="viewForward">The view's forward, left and up unit vectors.</param>
    public static bool Axes(SpriteType type, System.Numerics.Vector3 origin, float scale,
        System.Numerics.Vector3 entityLeft, System.Numerics.Vector3 entityUp,
        System.Numerics.Vector3 viewOrigin, System.Numerics.Vector3 viewForward, System.Numerics.Vector3 viewLeft, System.Numerics.Vector3 viewUp,
        out System.Numerics.Vector3 left, out System.Numerics.Vector3 up)
    {
        // "nudge it toward the view to make sure it isn't in a wall"
        System.Numerics.Vector3 org = Nudge(origin, viewForward);
        switch (type)
        {
            case SpriteType.VpParallelUpright:
            {
                // "vertical beam sprite, faces view plane"
                float s = scale / MathF.Sqrt(viewForward.X * viewForward.X + viewForward.Y * viewForward.Y);
                left = new System.Numerics.Vector3(-viewForward.Y * s, viewForward.X * s, 0);
                up = new System.Numerics.Vector3(0, 0, scale);
                return true;
            }
            case SpriteType.FacingUpright:
            {
                // "vertical beam sprite, faces viewer's origin (not the view plane)"
                float dx = org.X - viewOrigin.X, dy = org.Y - viewOrigin.Y;
                float s = scale / MathF.Sqrt(dx * dx + dy * dy);
                left = new System.Numerics.Vector3(dy * s, -dx * s, 0);
                up = new System.Numerics.Vector3(0, 0, scale);
                return true;
            }
            case SpriteType.Oriented:
                // "bullet marks on walls; ignores viewer entirely"
                left = entityLeft;
                up = entityUp;
                return true;
            case SpriteType.VpParallelOriented:
                // "oriented relative to view space"
                left = entityLeft.X * viewForward + entityLeft.Y * viewLeft + entityLeft.Z * viewUp;
                up = entityUp.X * viewForward + entityUp.Y * viewLeft + entityUp.Z * viewUp;
                return true;
            case SpriteType.Label:
            case SpriteType.LabelScale:
                left = viewLeft;
                up = viewUp;
                return false;
            case SpriteType.Overhead:
            {
                // r_overheadsprites_perspective 5, _pushback 15, _scalex 1, _scaley 1 (the defaults); the
                // pushback of the origin is the caller's (see OverheadOrigin).
                left = viewLeft * scale;
                up = viewUp * scale;
                System.Numerics.Vector3 middle = System.Numerics.Vector3.Normalize(org - viewOrigin);
                float angle = 5f * (1 - MathF.Abs(System.Numerics.Vector3.Dot(middle, viewForward)));
                up.Z += angle;
                up = System.Numerics.Vector3.Normalize(up) * scale;
                up.Z += angle * 0.3f;
                up += viewForward * 0.07f;
                return true;
            }
            default:
                // SPR_VP_PARALLEL, and "unknown sprite type": "normal sprite, faces view plane"
                left = viewLeft * scale;
                up = viewUp * scale;
                return true;
        }
    }

    /// <summary>The origin a sprite is drawn about: one unit toward the eye along the view direction.</summary>
    public static System.Numerics.Vector3 Nudge(System.Numerics.Vector3 origin, System.Numerics.Vector3 viewForward) => origin - viewForward;

    /// <summary>
    /// The four corners of a frame (<c>R_CalcSprite_Vertex3f</c>), in the order of DarkPlaces' texture
    /// coordinates (0,1) (0,0) (1,0) (1,1): bottom-left, top-left, top-right, bottom-right of the picture.
    /// Note that the picture's LEFT edge is at <c>left * QuadRight</c>: the frame's stored X origin is
    /// measured along the sprite's right.
    /// </summary>
    public static void Corners(SpriteFrame frame, System.Numerics.Vector3 origin, System.Numerics.Vector3 left, System.Numerics.Vector3 up,
        Span<System.Numerics.Vector3> corners)
    {
        corners[0] = origin + left * frame.QuadRight + up * frame.QuadDown;
        corners[1] = origin + left * frame.QuadRight + up * frame.QuadUp;
        corners[2] = origin + left * frame.QuadLeft + up * frame.QuadUp;
        corners[3] = origin + left * frame.QuadLeft + up * frame.QuadDown;
    }
}

/// <summary>The four sprite file formats handled by <see cref="SpriteReader"/>.</summary>
public enum SpriteFormat
{
    /// <summary>"IDSP" version 1: 8-bit paletted (Quake palette, not embedded).</summary>
    Spr,
    /// <summary>"IDSP" version 2: Half-Life, 8-bit with embedded palette + rendermode.</summary>
    SprHl,
    /// <summary>"IDSP" version 32: 32-bit RGBA.</summary>
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

    /// <summary>
    /// The frame as RGBA8, the way DarkPlaces uploads it: <see cref="Rgba"/> when the file carries colours,
    /// otherwise the palette indices of a Quake sprite through <paramref name="palette"/> with index 255
    /// transparent (<c>palette_bgra_transparent</c>; a sprite's full-bright colours are not split out, the
    /// whole sprite is drawn unlit). Null for an sp2 frame or an empty one.
    /// </summary>
    public byte[]? ToRgba(QuakePalette palette)
    {
        if (Rgba is not null) return Rgba;
        if (Indices is null || Indices.Length == 0) return null;
        return palette.ToRgba(Indices, QuakePaletteTable.Transparent);
    }

    /// <summary>
    /// DarkPlaces' <c>skinframe->hasalpha</c> for the embedded picture: some texel is not fully opaque. Such
    /// a frame is alpha-BLENDED (not alpha-tested); one without is drawn opaque.
    /// </summary>
    public bool HasAlpha
    {
        get
        {
            if (Rgba is not null)
            {
                for (int i = 3; i < Rgba.Length; i += 4)
                    if (Rgba[i] < 255) return true;
                return false;
            }
            return Indices is not null && Array.IndexOf(Indices, (byte)QuakePalette.TransparentIndex) >= 0;
        }
    }

    /// <summary>DP quad bounds derived from origin/size (left edge X).</summary>
    public int QuadLeft => OriginX;
    /// <summary>DP quad bounds derived from origin/size (right edge X).</summary>
    public int QuadRight => OriginX + Width;
    /// <summary>DP quad bounds derived from origin/size (top edge Y).</summary>
    public int QuadUp => OriginY;
    /// <summary>DP quad bounds derived from origin/size (bottom edge Y).</summary>
    public int QuadDown => OriginY - Height;
}
