using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Images;
using VortexArena.Formats.Md3;
using VortexArena.Formats.Mdl;

namespace VortexArena.Game.Loaders.Models;

/// <summary>
/// What every instance of one Quake <c>.mdl</c> shares: the geometry in the form the vertex-morph node takes
/// (<see cref="Md3Data"/>: DarkPlaces' compacted vertex set, one surface, one entry per pose) and the skin
/// materials, each built the first time an entity shows that skin.
///
/// <para><b>Skins</b>, as <c>Mod_IDP0_Load</c> resolves them. Skin picture <c>i</c> of the file (or picture
/// <c>j</c> of skin group <c>i</c>) is replaced by an external <c>&lt;model&gt;_&lt;i&gt;</c>
/// (<c>&lt;model&gt;_&lt;i&gt;_&lt;j&gt;</c>) when a shader or an image of that name exists - the name
/// includes the model's extension: <c>progs/soldier.mdl_0.tga</c> - with its <c>_glow</c> (or
/// <c>_blend</c>, or <c>_luma</c>), <c>_pants</c>, <c>_shirt</c>, <c>_norm</c> and <c>_gloss</c>
/// companions. Otherwise the 8-bit picture inside the file is converted through the session's Quake palette
/// (<see cref="QuakePalette"/>): a lit base, a glow of the full-bright texels when the palette has a
/// full-bright range, and - for an entity with a colormap, when the picture has pants or shirt texels - the
/// base without them plus the two masks. After the file's own skins come any further
/// <c>&lt;model&gt;_&lt;N&gt;</c> pictures found beside the model, one skin each.</para>
///
/// <para>Not reproduced: <c>&lt;model&gt;_&lt;N&gt;.skin</c> files for a <c>.mdl</c> (no Quake content uses
/// them), and the alpha channel of an external skin (DarkPlaces blends such a skin; here it is opaque).</para>
/// </summary>
public sealed class MdlShared
{
    private readonly AssetSystem? _assets;
    private readonly QuakePalette _palette;
    private readonly string? _vpath;

    // One entry per skin PICTURE: the file's, in order, then the external extras.
    private readonly List<(int Internal, string? External)> _pictures = new();
    // Each form is built the first time it is shown: most skins are never seen colormapped, many never at all.
    private readonly List<(Material? Merged, bool MergedBuilt, Material? Colormapped, bool ColormappedBuilt)> _materials = new();

    /// <summary>Extra external skins are looked for up to this many; DarkPlaces has no limit.</summary>
    private const int MaxExtraSkins = 64;

    public MdlData Data { get; }

    /// <summary>The geometry for <see cref="Md3Morph"/>: surface 0, every pose.</summary>
    public Md3Data Morph { get; }

    /// <summary>What <c>.skin</c> selects: the file's skin scenes, then one per extra external picture.</summary>
    public MdlScene[] SkinScenes { get; }

    /// <param name="mdl">The parsed model.</param>
    /// <param name="assets">Resolves external replacement skins; null for none (a test).</param>
    /// <param name="vpath">The model's name as the game refers to it (with extension), for the external names.</param>
    /// <param name="palette">The session's palette; null for the built-in one without full-brights.</param>
    public MdlShared(MdlData mdl, AssetSystem? assets, string? vpath, QuakePalette? palette)
    {
        Data = mdl ?? throw new ArgumentNullException(nameof(mdl));
        _assets = assets;
        _vpath = string.IsNullOrEmpty(vpath) ? null : vpath;
        _palette = palette ?? QuakePalette.Default;
        Morph = MdlRender.ToMd3(mdl);

        var scenes = new List<MdlScene>(mdl.SkinScenes);
        for (int i = 0; i < mdl.SkinScenes.Length; i++)
        {
            MdlScene scene = mdl.SkinScenes[i];
            for (int j = 0; j < scene.Count; j++)
            {
                // "if (groupskins > 1) name = %s_%i_%i else name = %s_%i"
                string? name = _vpath is null ? null : scene.Count > 1 ? $"{_vpath}_{i}_{j}" : $"{_vpath}_{i}";
                _pictures.Add((scene.First + j, name is not null && ExternalExists(name) ? name : null));
            }
        }
        // "check for skins that don't exist in the model, but do exist as external images"
        for (int extra = 0; _vpath is not null && extra < MaxExtraSkins; extra++)
        {
            string name = $"{_vpath}_{scenes.Count}";
            if (_assets?.LoadTexture(name) is null)
                break;
            scenes.Add(new MdlScene(name, _pictures.Count, 1, 10f, true));
            _pictures.Add((-1, name));
        }
        SkinScenes = scenes.ToArray();
        for (int i = 0; i < _pictures.Count; i++)
            _materials.Add((null, false, null, false));
    }

    /// <summary>The picture a skin number shows at a time (<see cref="MdlRender.SkinPicture"/>), extras included.</summary>
    public int PictureFor(int skin, double shaderTime) => MdlRender.SkinPicture(SkinScenes, skin, shaderTime);

    /// <summary>
    /// The material of a skin picture. <paramref name="colormapped"/>: the entity has pants or shirt colours;
    /// the answer is then the form with the two masks, when the picture has such texels (DarkPlaces'
    /// <c>qhascolormapping</c>) - otherwise, and for an entity without colours, the merged picture.
    /// </summary>
    public Material? MaterialFor(int picture, bool colormapped)
    {
        if ((uint)picture >= (uint)_pictures.Count)
            return null;
        (Material? merged, bool mergedBuilt, Material? mapped, bool mappedBuilt) = _materials[picture];
        if (colormapped && !mappedBuilt)
        {
            mapped = BuildColormapped(picture);
            mappedBuilt = true;
            _materials[picture] = (merged, mergedBuilt, mapped, mappedBuilt);
        }
        if (colormapped && mapped is not null)
            return mapped;
        if (!mergedBuilt)
        {
            merged = BuildMerged(picture);
            _materials[picture] = (merged, true, mapped, mappedBuilt);
        }
        return merged;
    }

    private bool ExternalExists(string name) =>
        _assets is not null && (_assets.GetShader(name) is not null || _assets.LoadTexture(name) is not null);

    private byte[]? Indices(int picture)
    {
        int index = _pictures[picture].Internal;
        return (uint)index < (uint)Data.Skins.Length && Data.SkinWidth > 0 && Data.SkinHeight > 0 ? Data.Skins[index] : null;
    }

    private Material? BuildMerged(int picture)
    {
        string? external = _pictures[picture].External;
        if (external is not null && _assets is not null)
        {
            Material material = _assets.ResolveModelMaterial(external);
            // R_SkinFrame_LoadExternal looks for "<name>_glow", then "<name>.blend", "<name>_blend" and
            // "<name>_luma" (the name Quake engines use; most replacement skins of Quake content have one).
            // The asset system knows the first; the others are added here.
            if (material is ShaderMaterial skin && skin.Shader == PlayerSkinShader.Shader
                && skin.GetShaderParameter("has_glow").AsBool() == false)
            {
                Texture2D? glow = _assets.LoadTexture(external + "_blend") ?? _assets.LoadTexture(external + "_luma");
                if (glow is not null)
                {
                    skin.SetShaderParameter(PlayerSkinShader.GlowUniform, glow);
                    skin.SetShaderParameter("has_glow", true);
                }
            }
            return material;
        }
        if (Indices(picture) is not { } indices)
            return null;
        QuakeTexture merged = _palette.Convert(indices);
        return SkinMaterial(Texture(merged.Base), merged.Glow is null ? null : Texture(merged.Glow), null, null);
    }

    // Null for an external skin (it carries its own _pants / _shirt masks in the one material) and for a
    // picture without pants or shirt texels: the merged form is shown whatever the entity's colours.
    private Material? BuildColormapped(int picture)
    {
        if (_pictures[picture].External is not null || Indices(picture) is not { } indices)
            return null;
        if (_palette.ConvertColormapped(indices) is not { } mapped)
            return null;
        return SkinMaterial(Texture(mapped.Base), mapped.Glow is null ? null : Texture(mapped.Glow), Texture(mapped.Shirt), Texture(mapped.Pants));
    }

    private ImageTexture Texture(byte[] rgba) =>
        ImageTexture.CreateFromImage(Image.CreateFromData(Data.SkinWidth, Data.SkinHeight, false, Image.Format.Rgba8, rgba));

    /// <summary>
    /// The model shader of a legacy session and of the native game's models alike: the texel plus the two
    /// masks times the entity's colours, lit from the level's light grid (or by the entity's own light, see
    /// <c>ModelTint.ApplyGridLight</c>), plus the glow unlit.
    /// </summary>
    private static ShaderMaterial SkinMaterial(Texture2D albedo, Texture2D? glow, Texture2D? shirt, Texture2D? pants)
    {
        var material = new ShaderMaterial { Shader = PlayerSkinShader.Shader };
        material.SetShaderParameter(PlayerSkinShader.AlbedoUniform, albedo);
        if (glow is not null)
        {
            material.SetShaderParameter(PlayerSkinShader.GlowUniform, glow);
            material.SetShaderParameter("has_glow", true);
        }
        if (shirt is not null) material.SetShaderParameter(PlayerSkinShader.ShirtMaskUniform, shirt);
        if (pants is not null) material.SetShaderParameter(PlayerSkinShader.PantsMaskUniform, pants);
        return material;
    }
}

/// <summary>
/// One Quake <c>.mdl</c> in the scene: a vertex-morph node (<see cref="Md3Morph"/>, which the callers that
/// pose MD3 models already drive with <see cref="Md3Morph.SetFrame"/> / <see cref="Md3Morph.LerpFrames"/> on
/// POSE numbers) plus the skin, which unlike an MD3's is chosen per entity and per moment: by
/// <c>.skin</c>, by time within a skin group, and by whether the entity has a colormap.
/// </summary>
public partial class MdlModel : Node3D
{
    private MdlShared _shared = null!;
    private Md3Morph _morph = null!;
    private int _skin, _picture = int.MinValue;
    private bool _colormapped;

    public MdlShared Shared => _shared;
    public MdlData Data => _shared.Data;
    public Md3Morph Morph => _morph;

    /// <summary>True when the skin number shown is a group, so that <see cref="UpdateSkin"/> has to be called as time passes.</summary>
    public bool SkinAnimates { get; private set; }

    internal static MdlModel Create(MdlShared shared, int skin)
    {
        var node = new MdlModel { _shared = shared, Name = "MdlModel" };
        var morph = new Md3Morph();
        morph.Initialize(shared.Morph, null, null, null);
        // A Quake model's frame is the entity's to choose; the morph node's own "play every frame" clip is not wanted.
        morph.SetFrame(0);
        node._morph = morph;
        node.AddChild(morph);
        node.SetSkin(skin);
        return node;
    }

    /// <summary>The entity's <c>.skin</c>. Out of range shows skin 0, as in DarkPlaces.</summary>
    public void SetSkin(int skin)
    {
        _skin = skin;
        MdlScene[] scenes = _shared.SkinScenes;
        SkinAnimates = scenes.Length > 0 && scenes[(uint)skin < (uint)scenes.Length ? skin : 0].Count > 1;
        UpdateSkin(0, _colormapped);
    }

    /// <summary>
    /// Shows the picture the skin has at <paramref name="shaderTime"/> (seconds since the entity's shader
    /// clock began), in its colormapped form or not. True when the material changed.
    /// </summary>
    public bool UpdateSkin(double shaderTime, bool colormapped)
    {
        int picture = _shared.PictureFor(_skin, shaderTime);
        if (picture == _picture && colormapped == _colormapped)
            return false;
        _picture = picture;
        _colormapped = colormapped;
        _morph.SetSurfaceMaterial(0, _shared.MaterialFor(picture, colormapped));
        return true;
    }
}
