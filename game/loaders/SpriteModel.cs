using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Images;
using VortexArena.Formats.Sprites;

namespace VortexArena.Game.Loaders;

/// <summary>
/// What every instance of one sprite file shares when it is drawn the way DarkPlaces draws it
/// (<c>model_sprite.c</c> Mod_Sprite_SharedSetup, <c>r_sprites.c</c>): each frame's picture and the three
/// materials a frame can need.
///
/// <para><b>Pictures.</b> A frame is replaced by an external <c>&lt;sprite&gt;_&lt;slot&gt;</c> image
/// (<c>&lt;sprite&gt;_&lt;slot&gt;_&lt;n&gt;</c> for picture n of a group) when one exists - the name includes
/// the extension: <c>progs/s_explod.spr_0.tga</c>. Otherwise: a Quake sprite's 8-bit picture through the
/// session's Quake palette with index 255 transparent (no full-bright split: a sprite is drawn unlit
/// anyway), an SPR32's RGBA as stored, a Half-Life sprite's through its own palette and render mode.</para>
///
/// <para><b>Blending.</b> Additive for a Half-Life "additive" sprite or an entity with EF_ADDITIVE /
/// RF_ADDITIVE; alpha-blended when the picture has any texel that is not fully opaque, or the entity is
/// translucent; opaque otherwise. Always unlit - DarkPlaces lights a sprite only when its file name contains
/// '!', which is not reproduced. Always two-sided.</para>
/// </summary>
public sealed class SpriteShared
{
    private readonly Texture2D?[] _textures;
    private readonly bool[] _hasAlpha;
    private readonly Dictionary<(int Frame, int Mode), ShaderMaterial> _materials = new();

    public SpriteData Data { get; }

    internal const int Opaque = 0, Blend = 1, Add = 2;

    public SpriteShared(SpriteData sprite, AssetSystem? assets, string? vpath, QuakePalette? palette)
    {
        Data = sprite ?? throw new ArgumentNullException(nameof(sprite));
        palette ??= QuakePalette.Default;
        _textures = new Texture2D?[sprite.FrameCount];
        _hasAlpha = new bool[sprite.FrameCount];
        for (int slot = 0; slot < sprite.GroupRanges.Length; slot++)
        {
            SpriteGroup group = sprite.GroupRanges[slot];
            for (int n = 0; n < group.FrameCount; n++)
            {
                int index = group.FirstFrame + n;
                if ((uint)index >= (uint)_textures.Length)
                    continue;
                SpriteFrame frame = sprite.Frames[index];
                // "note: Nehahra's null.spr has width == 0 and height == 0": such a frame draws nothing.
                if (frame.Width <= 0 || frame.Height <= 0)
                    continue;
                Texture2D? texture = null;
                bool alpha = false;
                if (assets is not null)
                {
                    string? name = !string.IsNullOrEmpty(frame.ExternalImage) ? frame.ExternalImage
                        : string.IsNullOrEmpty(vpath) ? null
                        : group.FrameCount > 1 ? $"{vpath}_{slot}_{n}" : $"{vpath}_{slot}";
                    if (name is not null && SpriteBuilder.SafeLoadTexture(assets, name) is { } external)
                    {
                        texture = external;
                        // skinframe->hasalpha of an external image: any texel below full alpha.
                        alpha = external.GetImage() is { } image && image.DetectAlpha() != Image.AlphaMode.None;
                    }
                }
                if (texture is null && frame.ToRgba(palette) is { } rgba)
                {
                    texture = ImageTexture.CreateFromImage(Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, rgba));
                    alpha = frame.HasAlpha;
                }
                _textures[index] = texture;
                _hasAlpha[index] = alpha;
            }
        }
    }

    public bool HasPicture(int frame) => (uint)frame < (uint)_textures.Length && _textures[frame] is not null;

    internal ShaderMaterial? Material(int frame, bool additive, bool translucent)
    {
        if (!HasPicture(frame))
            return null;
        int mode = additive || Data.Additive ? Add : translucent || _hasAlpha[frame] ? Blend : Opaque;
        if (_materials.TryGetValue((frame, mode), out ShaderMaterial? material))
            return material;
        material = new ShaderMaterial { Shader = Shader(mode) };
        material.SetShaderParameter("picture", _textures[frame]!);
        _materials[(frame, mode)] = material;
        return material;
    }

    private static readonly Shader?[] s_shaders = new Shader?[3];

    // The stored texel times the entity's colour, written for the buffer's convention (dp_fb: display values
    // in a legacy session, linear light otherwise). MATERIALFLAG_ADD is GL_SRC_ALPHA GL_ONE,
    // MATERIALFLAG_ALPHA is GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA, neither writes depth; TEXF_CLAMP.
    private static Shader Shader(int mode) => s_shaders[mode] ??= new Shader
    {
        Code = "// VortexArena sprite shader (r_sprites.c). Generated in C#.\n" +
            "shader_type spatial;\n" +
            "render_mode unshaded, cull_disabled, shadows_disabled" +
            (mode == Add ? ", depth_draw_never, blend_add" : mode == Blend ? ", depth_draw_never, blend_mix" : "") + ";\n" +
            "uniform sampler2D picture : hint_default_white, filter_linear_mipmap, repeat_disable;\n" +
            "instance uniform vec4 sprite_tint = vec4(1.0);\n" +
            VortexArena.Game.Client.DisplayFramebuffer.ShaderFunctions +
            "void fragment() {\n" +
            "    vec4 c = texture(picture, UV) * sprite_tint;\n" +
            (mode == Add ? "    ALBEDO = dp_fb(c.rgb * c.a);\n    ALPHA = 1.0;\n"
                : mode == Blend ? "    ALBEDO = dp_fb(c.rgb);\n    ALPHA = c.a;\n"
                : "    ALBEDO = dp_fb(c.rgb);\n") +
            "}\n",
    };
}

/// <summary>
/// One sprite in the scene, drawn as DarkPlaces draws it: a quad whose corners are
/// <c>origin + left * x + up * y</c> for the frame's stored rectangle, with <c>left</c> and <c>up</c>
/// recomputed against the view every frame by the sprite's type
/// (<see cref="SpriteOrientation.Axes"/>). The owner calls <see cref="Orient"/> once the frame's view is
/// known and <see cref="ShowFrame"/> when the entity's frame or look changes; nothing here runs by itself.
/// </summary>
public partial class SpriteModel : Node3D
{
    private static readonly StringName TintUniform = "sprite_tint";

    private SpriteShared _shared = null!;
    private MeshInstance3D _quad = null!;
    private int _frame = int.MinValue;
    private bool _additive, _translucent;
    private Color _tint = new(float.NaN, 0, 0, 0);

    public SpriteShared Shared => _shared;
    public SpriteData Data => _shared.Data;

    internal static SpriteModel Create(SpriteShared shared)
    {
        var node = new SpriteModel { _shared = shared, Name = "SpriteModel" };
        node._quad = new MeshInstance3D
        {
            Name = "SpriteQuad",
            Mesh = UnitQuad(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        node.AddChild(node._quad);
        node.ShowFrame(0, false, Colors.White);
        return node;
    }

    private static QuadMesh? s_unitQuad;
    private static QuadMesh UnitQuad() => s_unitQuad ??= new QuadMesh { Size = Vector2.One };

    /// <summary>
    /// Shows one picture (an index into <see cref="SpriteData.Frames"/>: a pose, not a <c>.frame</c> value)
    /// with the entity's colour and alpha. <paramref name="additive"/>: EF_ADDITIVE or RF_ADDITIVE.
    /// </summary>
    public void ShowFrame(int frame, bool additive, Color tint)
    {
        SpriteData data = _shared.Data;
        if (data.FrameCount == 0)
        {
            _quad.Visible = false;
            return;
        }
        frame = Math.Clamp(frame, 0, data.FrameCount - 1);
        bool translucent = tint.A < 1f;
        if (frame != _frame || additive != _additive || translucent != _translucent)
        {
            _frame = frame;
            _additive = additive;
            _translucent = translucent;
            SpriteFrame f = data.Frames[frame];
            ShaderMaterial? material = _shared.Material(frame, additive, translucent);
            _quad.Visible = material is not null;
            _quad.MaterialOverride = material;
            // In the node's own space X is the sprite's RIGHT (minus DarkPlaces' "left") and Y its up, one
            // unit a texel. The picture's left edge is at left * QuadRight - that is, at -QuadRight along X.
            float x0 = -f.QuadRight, x1 = -f.QuadLeft, y0 = f.QuadDown, y1 = f.QuadUp;
            _quad.Transform = new Transform3D(
                new Basis(new Vector3(x1 - x0, 0, 0), new Vector3(0, y1 - y0, 0), Vector3.Back),
                new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, 0));
        }
        if (tint != _tint)
        {
            _tint = tint;
            _quad.SetInstanceShaderParameter(TintUniform, tint);
        }
    }

    /// <summary>
    /// Places the quad: <paramref name="origin"/> and the sprite's <paramref name="right"/> and
    /// <paramref name="up"/> axes (per texel, scale included), all in the parent's space.
    /// </summary>
    public void Orient(Vector3 origin, Vector3 right, Vector3 up)
    {
        Vector3 normal = right.Cross(up);
        float length = normal.Length();
        if (!(length > 1e-12f) || !float.IsFinite(length) || !origin.IsFinite())
            return;
        Transform = new Transform3D(new Basis(right, up, normal / length), origin);
    }
}
