using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Sprites;

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// Turns a parsed <see cref="SpriteData"/> (the Godot-free sprite importer output, all four Quake-family
/// formats: spr / sprhl / spr32 / sp2) into a billboarded Godot node.
///
/// <para>A sprite is rendered as a <see cref="QuadMesh"/> on a <see cref="MeshInstance3D"/> rather than a
/// <see cref="Sprite3D"/>, because a quad lets us size it exactly from the frame's pixel
/// origin/width/height (Darkplaces derives a left/right/up/down quad from those — see
/// <see cref="SpriteFrame.QuadLeft"/> etc.) and bake the billboard / blend / alpha behaviour into a single
/// <see cref="StandardMaterial3D"/>. The material's <see cref="BaseMaterial3D.BillboardMode"/> is chosen from
/// <see cref="SpriteData.SpriteType"/> so <c>SPR_VP_PARALLEL</c> faces the camera fully while the upright
/// variants billboard around the Y (Godot up) axis only; <c>SPR_ORIENTED</c> keeps the node's own
/// orientation (no billboard). Half-Life additive sprites (and any additive render mode) use additive
/// blending.</para>
///
/// <para>Pixel sourcing per frame:
/// <list type="bullet">
///   <item><b>spr32 / sprhl</b>: <see cref="SpriteFrame.Rgba"/> is decoded RGBA8 — wrapped in an
///         <see cref="ImageTexture"/> directly.</item>
///   <item><b>sp2</b>: <see cref="SpriteFrame.ExternalImage"/> names an image resolved through the VFS via
///         <see cref="AssetSystem.LoadTexture"/>.</item>
///   <item><b>spr (Quake v1)</b>: only raw palette <see cref="SpriteFrame.Indices"/> are available (the
///         Quake palette is not embedded in the file and is not shipped by Xonotic), so we emit a small
///         magenta placeholder texture rather than guessing a palette. TODO(palette): colour through
///         gfx/palette.lmp once a palette source exists.</item>
/// </list></para>
///
/// <para>Multi-frame sprites get a <see cref="SpriteFramePlayer"/> child that swaps the quad texture over
/// time using the parsed group intervals (or a default rate when the sprite is a flat frame list), and also
/// exposes a manual <see cref="SpriteFramePlayer.SetFrame(int)"/> frame-swap API for code that drives the
/// frame itself (e.g. a networked sprite effect).</para>
///
/// Geometry is built in Godot (Y-up) space; the quad lies in the XY plane and billboards toward the camera.
/// </summary>
public static class SpriteBuilder
{
    /// <summary>Quake sprites are authored at 1 unit per pixel; scale the pixel quad down so a typical
    /// 32–64px sprite is a sensible world size. DP renders sprites at 1:1 world units per texel, so keep it.</summary>
    private const float UnitsPerPixel = 1.0f;

    /// <summary>Default animation rate for a flat (un-grouped) multi-frame sprite, frames per second.</summary>
    private const float DefaultFps = 10f;

    /// <summary>
    /// Build a billboarded sprite node from <paramref name="spr"/>. Frame 0 is shown initially; if the
    /// sprite has more than one frame a <see cref="SpriteFramePlayer"/> is attached to animate/swap frames.
    /// Never returns null — a sprite with no usable frames yields an empty placeholder quad.
    /// <paramref name="vpath"/> (the sprite's normalized path) enables the DP external-frame override: a plain
    /// <c>&lt;vpath&gt;_&lt;n&gt;</c> image shipped next to the .spr (e.g. <c>chatbubble.spr_0</c>) is used in
    /// preference to the embedded pixels, exactly like DP's <c>Mod_Sprite_SharedSetup</c>.
    /// </summary>
    public static Node3D Build(SpriteData spr, AssetSystem assets, string? vpath = null)
    {
        ArgumentNullException.ThrowIfNull(spr);

        var root = new Node3D { Name = "Sprite" };

        // Decode every frame's texture up front (cheap; sprites are small). Frames that fail to resolve
        // become a placeholder so the animation timing still lines up.
        var textures = new ImageTexture?[spr.FrameCount];
        for (int i = 0; i < spr.FrameCount; i++)
            textures[i] = FrameTexture(spr.Frames[i], assets, vpath, i);

        // Size the quad from frame 0's pixel dimensions (fall back to 1x1 for a null/zero frame).
        SpriteFrame? frame0 = spr.FrameCount > 0 ? spr.Frames[0] : null;
        float w = MathF.Max(1, frame0?.Width ?? 1) * UnitsPerPixel;
        float h = MathF.Max(1, frame0?.Height ?? 1) * UnitsPerPixel;

        var quad = new QuadMesh { Size = new Vector2(w, h) };

        StandardMaterial3D material = BuildMaterial(spr);
        material.AlbedoTexture = textures.Length > 0 ? textures[0] : null;

        var mi = new MeshInstance3D
        {
            Name = "SpriteQuad",
            Mesh = quad,
            MaterialOverride = material,
            // Offset the quad so the frame origin sits where DP places it (origin is the top-left-ish
            // offset; centre the quad on the origin+half-size point). Quad is centred on its own origin,
            // so shift by the frame's centre offset in pixels.
            Position = FrameCenterOffset(frame0),
        };
        root.AddChild(mi);

        // Animate / expose frame-swap when there is more than one frame.
        if (spr.FrameCount > 1)
        {
            var player = new SpriteFramePlayer
            {
                Name = "FramePlayer",
            };
            player.Configure(material, textures, BuildFrameTimes(spr));
            root.AddChild(player);
        }

        return root;
    }

    // ---------------------------------------------------------------------------------------------
    //  Material / billboard
    // ---------------------------------------------------------------------------------------------

    private static StandardMaterial3D BuildMaterial(SpriteData spr)
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, // sprites are fullbright
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,        // visible from both sides
            // Alpha: additive sprites blend add; everything else uses alpha-scissor + transparency so the
            // sprite's transparent border doesn't write depth / occlude.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            BillboardMode = BillboardFor(spr.SpriteType),
        };

        // Billboarded quads must keep their mesh size (BillboardKeepScale is a material FLAG in Godot 4,
        // set via SetFlag rather than a direct property).
        mat.SetFlag(BaseMaterial3D.Flags.BillboardKeepScale, true);

        if (spr.Additive)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            mat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
        }
        else
        {
            // Alpha scissor keeps hard sprite edges crisp; full alpha transparency handles soft edges.
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = 0.5f;
        }

        return mat;
    }

    /// <summary>
    /// Map the Quake sprite orientation to a Godot billboard mode:
    /// <list type="bullet">
    ///   <item><c>VP_PARALLEL*</c> / <c>FACING_UPRIGHT</c> / <c>LABEL*</c> / <c>OVERHEAD</c> → full camera-facing
    ///         billboard, except the "upright" family which only rotates about the world up axis.</item>
    ///   <item><c>ORIENTED</c> / <c>VP_PARALLEL_ORIENTED</c> → no billboard (use the node's own transform).</item>
    /// </list>
    /// </summary>
    private static BaseMaterial3D.BillboardModeEnum BillboardFor(SpriteType type) => type switch
    {
        SpriteType.VpParallelUpright => BaseMaterial3D.BillboardModeEnum.FixedY,
        SpriteType.FacingUpright => BaseMaterial3D.BillboardModeEnum.FixedY,
        SpriteType.Oriented => BaseMaterial3D.BillboardModeEnum.Disabled,
        SpriteType.VpParallelOriented => BaseMaterial3D.BillboardModeEnum.Disabled,
        // VpParallel, Label, LabelScale, Overhead and anything else: face the camera fully.
        _ => BaseMaterial3D.BillboardModeEnum.Enabled,
    };

    /// <summary>
    /// The quad is centred on its own local origin, but DP's frame origin offsets the image (left/up edges
    /// are <c>originX</c>/<c>originY</c>). Convert that to the centre offset in Godot units: the quad centre
    /// sits at <c>(originX + width/2, originY - height/2)</c> in DP's up-positive pixel space, which maps to
    /// Godot X right / Y up directly.
    /// </summary>
    private static Vector3 FrameCenterOffset(SpriteFrame? f)
    {
        if (f is null)
            return Vector3.Zero;
        float cx = (f.OriginX + f.Width * 0.5f) * UnitsPerPixel;
        float cy = (f.OriginY - f.Height * 0.5f) * UnitsPerPixel;
        return new Vector3(cx, cy, 0f);
    }

    // ---------------------------------------------------------------------------------------------
    //  Per-frame texture decode
    // ---------------------------------------------------------------------------------------------

    private static ImageTexture? FrameTexture(SpriteFrame frame, AssetSystem assets, string? vpath, int frameIndex)
    {
        // DP external-frame override (Mod_Sprite_SharedSetup): a plain `<vpath>_<n>` image shipped next to the
        // .spr replaces the embedded pixels — a higher-res / properly-alpha'd version (e.g. chatbubble.spr_0.tga,
        // a 256² alpha billboard vs. the embedded 16² opaque HL frame). Probe it first for container sprites
        // (sp2 names its own external image below, so skip when ExternalImage is already set).
        if (!string.IsNullOrEmpty(vpath) && string.IsNullOrEmpty(frame.ExternalImage))
        {
            ImageTexture? ext = AsImageTexture(SafeLoadTexture(assets, $"{vpath}_{frameIndex}"));
            if (ext is not null)
                return ext;
        }

        // sp2: external image resolved through the VFS/material system.
        if (!string.IsNullOrEmpty(frame.ExternalImage))
            return AsImageTexture(SafeLoadTexture(assets, frame.ExternalImage!)) ?? Placeholder(frame.Width, frame.Height);

        // spr32 / sprhl: decoded RGBA8 ready to upload.
        if (frame.Rgba is not null && frame.Width > 0 && frame.Height > 0)
        {
            var img = Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.Rgba);
            return ImageTexture.CreateFromImage(img);
        }

        // plain Quake spr (palette indices only, no embedded palette) or a zero-size frame.
        return Placeholder(frame.Width, frame.Height);
    }

    /// <summary>Normalize a loaded <see cref="Texture2D"/> to an <see cref="ImageTexture"/> (the type the
    /// frame-swap path expects): pass an ImageTexture through, wrap any other texture's image, null on miss.</summary>
    private static ImageTexture? AsImageTexture(Texture2D? tex)
    {
        if (tex is ImageTexture it)
            return it;
        Image? img = tex?.GetImage();
        return img is not null ? ImageTexture.CreateFromImage(img) : null;
    }

    private static Texture2D? SafeLoadTexture(AssetSystem assets, string name)
    {
        try
        {
            return assets.LoadTexture(name);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpriteBuilder] failed to load external sprite image '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>A small magenta checker so an unresolved frame is obviously a placeholder, sized to the frame.</summary>
    private static ImageTexture Placeholder(int width, int height)
    {
        int w = Math.Clamp(width <= 0 ? 8 : width, 1, 256);
        int h = Math.Clamp(height <= 0 ? 8 : height, 1, 256);
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var a = new Color(1f, 0f, 1f, 1f);
        var b = new Color(0f, 0f, 0f, 1f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, ((x >> 2) + (y >> 2)) % 2 == 0 ? a : b);
        return ImageTexture.CreateFromImage(img);
    }

    // ---------------------------------------------------------------------------------------------
    //  Frame timing
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Per-frame display time in seconds. If the sprite carries real animation groups with intervals we use
    /// the group intervals (DP stores cumulative-free per-frame seconds); otherwise every frame gets the
    /// default rate. The array length always equals <see cref="SpriteData.FrameCount"/>.
    /// </summary>
    private static float[] BuildFrameTimes(SpriteData spr)
    {
        var times = new float[spr.FrameCount];
        float fallback = 1f / DefaultFps;
        for (int i = 0; i < times.Length; i++)
            times[i] = fallback;

        foreach (SpriteGroup g in spr.GroupRanges)
        {
            if (g.Intervals.Length == 0)
                continue;
            for (int k = 0; k < g.FrameCount; k++)
            {
                int flat = g.FirstFrame + k;
                if (flat < 0 || flat >= times.Length)
                    continue;
                float dt = k < g.Intervals.Length ? g.Intervals[k] : fallback;
                times[flat] = dt > 0.001f ? dt : fallback;
            }
        }
        return times;
    }
}

/// <summary>
/// Drives a multi-frame sprite by swapping the quad material's albedo texture over time, and exposes a
/// manual <see cref="SetFrame(int)"/> frame-swap API for code that wants to set the frame itself (matching
/// QC's <c>self.frame</c> on a sprite). Created by <see cref="SpriteBuilder.Build"/> only when the sprite
/// has more than one frame.
/// </summary>
public partial class SpriteFramePlayer : Node
{
    private StandardMaterial3D _material = null!;
    private ImageTexture?[] _textures = Array.Empty<ImageTexture?>();
    private float[] _frameTimes = Array.Empty<float>();
    private int _frame;
    private float _accum;

    /// <summary>When false, automatic playback is paused (use <see cref="SetFrame(int)"/> to drive it).</summary>
    public bool Playing { get; set; } = true;

    /// <summary>Whole-animation speed multiplier.</summary>
    public float TimeScale { get; set; } = 1f;

    /// <summary>The frame index currently displayed.</summary>
    public int Frame => _frame;

    /// <summary>Number of frames available.</summary>
    public int FrameCount => _textures.Length;

    internal void Configure(StandardMaterial3D material, ImageTexture?[] textures, float[] frameTimes)
    {
        _material = material;
        _textures = textures;
        _frameTimes = frameTimes;
        _frame = 0;
        _accum = 0f;
        Apply();
    }

    /// <summary>Manually show a specific frame (wraps into range) and pause automatic playback.</summary>
    public void SetFrame(int frame)
    {
        Playing = false;
        if (_textures.Length == 0)
            return;
        _frame = ((frame % _textures.Length) + _textures.Length) % _textures.Length;
        _accum = 0f;
        Apply();
    }

    /// <summary>Resume automatic playback from the current frame.</summary>
    public void Play() => Playing = true;

    public override void _Process(double delta)
    {
        if (!Playing || _textures.Length <= 1)
            return;

        _accum += (float)delta * MathF.Max(0f, TimeScale);
        // Advance possibly several frames if dt is large; guard against a zero/short interval looping forever.
        int guard = 0;
        while (guard++ < _textures.Length + 1)
        {
            float dwell = _frame < _frameTimes.Length ? _frameTimes[_frame] : 0.1f;
            if (dwell <= 0.001f)
                dwell = 0.1f;
            if (_accum < dwell)
                break;
            _accum -= dwell;
            _frame = (_frame + 1) % _textures.Length;
            Apply();
        }
    }

    private void Apply()
    {
        if (_material is not null && _frame >= 0 && _frame < _textures.Length)
            _material.AlbedoTexture = _textures[_frame];
    }
}
