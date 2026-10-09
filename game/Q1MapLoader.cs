// The render half of a Quake 1 format map (BSP 29, BSP2, 2PSB, Half-Life 30): what Base/darkplaces draws for
// one, from model_brush.c Mod_Q1BSP_LoadTextures / Mod_Q1BSP_LoadFaces, gl_rsurf.c R_BuildLightMap,
// gl_rmain.c R_UpdateCurrentTexture (texture animation, r_wateralpha, r_waterscroll) and r_sky.c.
// The parsed map is VortexArena.Formats.Bsp.Q1BspData; the rules that need no engine (names, palette,
// lightmap packing, light styles) are beside it in src/VortexArena.Formats/Bsp and are tested there.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game;

/// <summary>
/// One Quake 1 format map prepared for drawing: its textures, its lightmap atlas, its materials, and the
/// meshes of its models. The level of a legacy session is one; so is each Quake 1 format file an entity uses
/// as its model (Quake's ammo and health boxes are small maps).
///
/// <para>Model 0 is the world (<see cref="BuildWorld"/>: cut into a grid of cells that the map's own
/// visibility data shows and hides, <see cref="WorldPvsCuller"/>); model N is what an entity names "*N"
/// (<see cref="BuildModel"/>: one mesh, moved by its entity). All of them draw from the same lightmap pages
/// and share materials, except that a model with an animated texture has that material to itself: its entity's
/// frame chooses between the texture's two chains (a button that has been pressed).</para>
/// </summary>
public sealed class Q1Level
{
    private const int SkyTexture = -3;

    private readonly Q1BspData _bsp;
    private readonly AssetSystem _assets;
    private readonly Q1Palette _palette;
    private readonly string _mapName;
    private readonly Q1LightmapAtlas _atlas;
    private readonly Texture2DArray?[] _pages;
    private readonly Q1TextureAnimation?[] _animations;
    private readonly Dictionary<int, TextureEntry> _textures = new();
    private readonly Dictionary<(int Texture, int Page), ShaderMaterial> _shared = new();
    private readonly List<Animated> _animated = new();
    private readonly List<ShaderMaterial> _waterAlphaMaterials = new();
    private readonly Dictionary<ulong, List<Animated>> _animatedOfNode = new();
    private readonly Dictionary<int, (ArrayMesh Mesh, List<(int Texture, int Page)> Keys)> _modelMeshes = new();
    private ShaderMaterial? _skyMaterial;
    private float _waterAlpha = 1f, _waterScroll = 1f;
    private int _lastAnimationStep = int.MinValue;

    private sealed record TextureEntry(Texture2D Albedo, Texture2D? Glow, Q1TextureClass Class, float Width, float Height, bool External);

    private sealed class Animated
    {
        public ShaderMaterial Material = null!;
        public Q1TextureAnimation Animation = null!;
        public bool Alternate;
        public int Current;
        // The model node this material belongs to; null for the world's. A freed node's entry is dropped.
        public Node3D? Owner;
    }

    /// <summary>What the level holds, for a load report.</summary>
    public int Faces => _bsp.Faces.Length;
    public int LightmapPages => _atlas.Pages.Length;
    public long LightmapSamples => _atlas.SampleCount;
    public int TexturesResolved => _textures.Count;
    public int ExternalTextures { get; private set; }
    public int MissingTextures { get; private set; }
    public int Materials => _shared.Count;
    public int AnimatedMaterials => _animated.Count;
    public int WorldCells { get; private set; }
    public int WorldSurfaces { get; private set; }
    public int WorldTriangles { get; private set; }
    public bool HasSkyTexture { get; private set; }
    public Q1BspData Bsp => _bsp;

    /// <param name="mapPath">The file's name in the game data ("maps/e1m1.bsp"): replacement textures are
    /// looked for under <c>textures/&lt;its name&gt;/</c> first.</param>
    /// <param name="skyBox">The sky box of the level (a worldspawn "sky" key that named one that exists), or
    /// null: sky surfaces then show the map's own scrolling sky.</param>
    /// <param name="fog">The level's fog as the sky is to show it: colour and the opacity it reaches far away.</param>
    public Q1Level(Q1BspData bsp, string mapPath, AssetSystem assets, Q1Palette palette, Sky? skyBox = null, (Color Colour, float Amount)? fog = null)
    {
        _bsp = bsp ?? throw new ArgumentNullException(nameof(bsp));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
        _mapName = Q1TextureRules.MapName(mapPath);

        string[] names = new string[bsp.Textures.Length];
        for (int i = 0; i < names.Length; i++) names[i] = bsp.Textures[i].Name;
        _animations = Q1TextureRules.BuildAnimations(names);

        _atlas = Q1LightmapAtlas.Build(bsp);
        _pages = new Texture2DArray?[_atlas.Pages.Length];
        for (int p = 0; p < _pages.Length; p++)
        {
            Q1LightmapPage page = _atlas.Pages[p];
            var layers = new Godot.Collections.Array<Image>();
            foreach (byte[] rgb in page.Rgb) layers.Add(Image.CreateFromData(page.Width, page.Height, false, Image.Format.Rgb8, rgb));
            var texture = new Texture2DArray();
            if (texture.CreateFromImages(layers) == Error.Ok) _pages[p] = texture;
            foreach (Image layer in layers) layer.Dispose();
        }
        BuildSky(skyBox, fog);
    }

    // ---- the per-frame part ------------------------------------------------------------------------------

    /// <summary>
    /// The level at <paramref name="time"/> (cl.time): "+" textures step five times a second
    /// (gl_rmain.c: <c>(int)(rsurface.shadertime * 5.0f) % anim_total</c>). Does nothing between steps.
    /// </summary>
    public void Update(double time)
    {
        if (_animated.Count == 0) return;
        int step = (int)((float)time * 5.0f);
        if (step == _lastAnimationStep) return;
        _lastAnimationStep = step;
        bool stale = false;
        foreach (Animated a in _animated)
        {
            if (a.Owner is not null && !GodotObject.IsInstanceValid(a.Owner))
            {
                stale = true;
                continue;
            }
            ApplyFrame(a, time);
        }
        if (!stale) return;
        _animated.RemoveAll(static a => a.Owner is not null && !GodotObject.IsInstanceValid(a.Owner));
        var gone = new List<ulong>();
        foreach ((ulong id, List<Animated> list) in _animatedOfNode)
            if (list.Count == 0 || !GodotObject.IsInstanceValid(list[0].Owner)) gone.Add(id);
        foreach (ulong id in gone) _animatedOfNode.Remove(id);
    }

    private void ApplyFrame(Animated a, double time)
    {
        int texture = a.Animation.FrameAt(time, a.Alternate);
        if (texture == a.Current || texture < 0) return;
        a.Current = texture;
        TextureEntry entry = Resolve(texture);
        a.Material.SetShaderParameter(Q1SurfaceShader.AlbedoUniform, entry.Albedo);
        a.Material.SetShaderParameter(Q1SurfaceShader.UseGlowUniform, entry.Glow is not null);
        if (entry.Glow is not null) a.Material.SetShaderParameter(Q1SurfaceShader.GlowUniform, entry.Glow);
    }

    /// <summary>
    /// An entity's frame is not 0: the animated textures of its model (made by <see cref="BuildModel"/>) show
    /// their alternate chain ("use an alternate animation if the entity's frame is not 0").
    /// </summary>
    public void SetAlternate(Node3D modelNode, bool alternate, double time)
    {
        if (!_animatedOfNode.TryGetValue(modelNode.GetInstanceId(), out List<Animated>? list)) return;
        foreach (Animated a in list)
        {
            if (a.Alternate == alternate) continue;
            a.Alternate = alternate;
            ApplyFrame(a, time);
        }
    }

    /// <summary>True if <see cref="SetAlternate"/> has anything to do for this node.</summary>
    public bool HasAnimatedTextures(Node3D modelNode) => _animatedOfNode.ContainsKey(modelNode.GetInstanceId());

    /// <summary>A model node made by <see cref="BuildModel"/> is gone.</summary>
    public void Forget(Node3D modelNode)
    {
        if (!_animatedOfNode.Remove(modelNode.GetInstanceId(), out List<Animated>? list)) return;
        foreach (Animated a in list) _animated.Remove(a);
    }

    /// <summary>
    /// <c>r_wateralpha</c> and <c>r_waterscroll</c>. The alpha applies to water and slime only, and only on a
    /// map whose visibility data was made for see-through water (gl_rmain.c: <c>MATERIALFLAG_WATERALPHA &amp;&amp;
    /// model->brush.supportwateralpha</c>).
    /// </summary>
    public void SetWater(float alpha, float scroll)
    {
        alpha = float.IsFinite(alpha) ? Math.Clamp(alpha, 0f, 1f) : 1f;
        if (!_bsp.SupportsWaterAlpha) alpha = 1f;
        if (!float.IsFinite(scroll)) scroll = 1f;
        if (alpha == _waterAlpha && scroll == _waterScroll) return;
        bool blendChanged = (alpha < 1f) != (_waterAlpha < 1f);
        bool scrollChanged = scroll != _waterScroll;
        _waterAlpha = alpha;
        _waterScroll = scroll;
        foreach (ShaderMaterial material in _waterAlphaMaterials)
        {
            if (blendChanged) material.Shader = alpha < 1f ? Q1SurfaceShader.Blended : Q1SurfaceShader.Opaque;
            material.SetShaderParameter(Q1SurfaceShader.SurfaceAlphaUniform, alpha);
        }
        if (!scrollChanged) return;
        foreach (((int texture, _), ShaderMaterial material) in _shared)
            if (ClassOf(texture).WaterScroll) material.SetShaderParameter(Q1SurfaceShader.WaterScrollUniform, scroll);
    }

    // ---- textures ----------------------------------------------------------------------------------------

    private Q1TextureClass ClassOf(int texture) =>
        texture == -2 ? new Q1TextureClass(Q1SurfaceKind.Liquid, false)        // DarkPlaces' spare "no texture" liquid
        : texture < 0 || texture >= _bsp.Textures.Length ? new Q1TextureClass(Q1SurfaceKind.Wall, false)
        : Q1TextureRules.Classify(_bsp.Textures[texture].Name);

    private TextureEntry Resolve(int texture)
    {
        if (_textures.TryGetValue(texture, out TextureEntry? entry)) return entry;
        entry = Load(texture);
        _textures[texture] = entry;
        return entry;
    }

    private TextureEntry Load(int index)
    {
        Q1TextureClass kind = ClassOf(index);
        if (index < 0 || index >= _bsp.Textures.Length)
        {
            MissingTextures++;
            return new TextureEntry(MissingTexture(), null, kind, 16, 16, false);
        }
        Q1Texture texture = _bsp.Textures[index];
        float width = Math.Max(1, texture.Width), height = Math.Max(1, texture.Height);

        // "textures/<map>/<name>", then "textures/<name>": a replacement image wins over the one in the file.
        foreach (string candidate in Q1TextureRules.ExternalCandidates(_mapName, texture.Name))
        {
            if (_assets.LoadTexture(candidate) is not { } external) continue;
            Texture2D? glow = null;
            foreach (string suffix in Q1TextureRules.GlowSuffixes)
                if ((glow = _assets.LoadTexture(candidate + suffix)) is not null) break;
            ExternalTextures++;
            return new TextureEntry(external, glow, kind, width, height, true);
        }

        if (!texture.Present || texture.Pixels is not { } pixels || pixels.Length < texture.Width * texture.Height || texture.Width <= 0 || texture.Height <= 0)
        {
            MissingTextures++;
            return new TextureEntry(MissingTexture(), null, kind, width, height, false);
        }

        bool fence = kind.Kind == Q1SurfaceKind.Fence;
        if (texture.Palette is { Length: >= 768 } own)
        {
            // Half-Life: the texture's own palette; index 255 of a "{" texture is see-through (wad.c).
            byte[] rgba = new byte[pixels.Length * 4];
            for (int i = 0, o = 0; i < pixels.Length; i++, o += 4)
            {
                int p = pixels[i];
                if (fence && p == 255) continue;
                rgba[o] = own[p * 3];
                rgba[o + 1] = own[p * 3 + 1];
                rgba[o + 2] = own[p * 3 + 2];
                rgba[o + 3] = 255;
            }
            return new TextureEntry(Upload(rgba, texture.Width, texture.Height, true), null, kind, width, height, false);
        }

        // gl_rmain.c R_SkinFrame_LoadInternalQuake: with full-bright colours in the palette and in the image,
        // they go to a glow layer and are colour 0 in the base.
        bool glows = _palette.AnyFullbright(pixels);
        Q1PaletteMode baseMode = glows
            ? (fence ? Q1PaletteMode.NoFullbrightsTransparent : Q1PaletteMode.NoFullbrights)
            : (fence ? Q1PaletteMode.Transparent : Q1PaletteMode.Complete);
        Texture2D albedo = Upload(_palette.ToRgba(pixels, baseMode), texture.Width, texture.Height, true);
        Texture2D? glowTexture = glows
            ? Upload(_palette.ToRgba(pixels, fence ? Q1PaletteMode.OnlyFullbrightsTransparent : Q1PaletteMode.OnlyFullbrights), texture.Width, texture.Height, true)
            : null;
        return new TextureEntry(albedo, glowTexture, kind, width, height, false);
    }

    private static ImageTexture Upload(byte[] rgba, int width, int height, bool mipmaps)
    {
        using Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
        if (mipmaps) image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private static ImageTexture? s_missing;

    // R_SkinFrame_LoadMissing's stand-in: a grey checker.
    private static ImageTexture MissingTexture()
    {
        if (s_missing is not null && GodotObject.IsInstanceValid(s_missing)) return s_missing;
        const int size = 16;
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = (byte)((((x >> 3) ^ (y >> 3)) & 1) != 0 ? 128 : 64);
                int o = (y * size + x) * 4;
                rgba[o] = rgba[o + 1] = rgba[o + 2] = v;
                rgba[o + 3] = 255;
            }
        return s_missing = Upload(rgba, size, size, true);
    }

    // ---- sky ---------------------------------------------------------------------------------------------

    private void BuildSky(Sky? skyBox, (Color Colour, float Amount)? fog)
    {
        // A sky box that exists wins over the map's own sky (r_sky.c R_SkyStartFrame).
        if (skyBox?.SkyMaterial is ShaderMaterial box)
        {
            _skyMaterial = new ShaderMaterial { Shader = Q1SurfaceShader.SkyBox };
            foreach (string face in Q1SurfaceShader.SkyBoxFaceUniforms)
                _skyMaterial.SetShaderParameter(face, box.GetShaderParameter(face));
        }
        else
        {
            _skyMaterial = new ShaderMaterial { Shader = Q1SurfaceShader.SkySphere };
            // Each sky texture of the file replaces the one before it: the last one is the sky.
            for (int i = _bsp.Textures.Length - 1; i >= 0 && !HasSkyTexture; i--)
            {
                Q1Texture texture = _bsp.Textures[i];
                if (Q1TextureRules.Classify(texture.Name).Kind != Q1SurfaceKind.Sky || texture.Width != texture.Height * 2) continue;
                if (LoadExternalSky(texture.Name)) break;
                if (texture.Pixels is not { } pixels || texture.Palette is not null || pixels.Length < texture.Width * texture.Height) continue;
                (byte[] solid, byte[] alpha, int w, int h) = _palette.SplitSky(pixels, texture.Width, texture.Height);
                if (w <= 0) continue;
                _skyMaterial.SetShaderParameter("sky_solid", Upload(solid, w, h, false));
                _skyMaterial.SetShaderParameter("sky_alpha", Upload(alpha, w, h, false));
                HasSkyTexture = true;
            }
        }
        if (fog is { } f && f.Amount > 0)
        {
            _skyMaterial.SetShaderParameter("sky_fog_colour", new Vector3(f.Colour.R, f.Colour.G, f.Colour.B));
            _skyMaterial.SetShaderParameter("sky_fog_amount", Math.Clamp(f.Amount, 0f, 1f));
        }
    }

    // "textures/<map>/<sky>" or "textures/<sky>", twice as wide as high: split in the middle, the left half
    // keyed by its own alpha channel.
    private bool LoadExternalSky(string name)
    {
        foreach (string candidate in Q1TextureRules.ExternalCandidates(_mapName, name))
        {
            using Image? image = _assets.LoadImage(candidate);
            if (image is null || image.GetWidth() != image.GetHeight() * 2) continue;
            if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
            int w = image.GetWidth() / 2, h = image.GetHeight();
            using Image left = image.GetRegion(new Rect2I(0, 0, w, h)), right = image.GetRegion(new Rect2I(w, 0, w, h));
            _skyMaterial!.SetShaderParameter("sky_solid", ImageTexture.CreateFromImage(right));
            _skyMaterial.SetShaderParameter("sky_alpha", ImageTexture.CreateFromImage(left));
            HasSkyTexture = true;
            return true;
        }
        return false;
    }

    // ---- materials ---------------------------------------------------------------------------------------

    private ShaderMaterial MakeMaterial(int texture, int page)
    {
        if (texture == SkyTexture) return _skyMaterial!;
        TextureEntry entry = Resolve(texture);
        Q1TextureClass kind = entry.Class;
        bool waterAlpha = kind.WaterAlpha;
        Shader shader = kind.Kind == Q1SurfaceKind.Fence ? Q1SurfaceShader.Masked
            : waterAlpha && _waterAlpha < 1f ? Q1SurfaceShader.Blended : Q1SurfaceShader.Opaque;
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter(Q1SurfaceShader.AlbedoUniform, entry.Albedo);
        if (entry.Glow is not null)
        {
            material.SetShaderParameter(Q1SurfaceShader.GlowUniform, entry.Glow);
            material.SetShaderParameter(Q1SurfaceShader.UseGlowUniform, true);
        }
        if (page >= 0 && page < _pages.Length && _pages[page] is { } lightmap)
        {
            material.SetShaderParameter(Q1SurfaceShader.LightmapUniform, lightmap);
            material.SetShaderParameter(Q1SurfaceShader.LightmapLayersUniform, _atlas.Pages[page].Layers);
        }
        material.SetShaderParameter(Q1SurfaceShader.StyleUniform, Q1LightStyleTexture.Texture);
        if (_atlas.FullBright) material.SetShaderParameter(Q1SurfaceShader.FixedLightUniform, true);
        if (kind.WaterScroll) material.SetShaderParameter(Q1SurfaceShader.WaterScrollUniform, _waterScroll);
        if (waterAlpha)
        {
            material.SetShaderParameter(Q1SurfaceShader.SurfaceAlphaUniform, _waterAlpha);
            _waterAlphaMaterials.Add(material);
        }
        return material;
    }

    private ShaderMaterial SharedMaterial(int texture, int page)
    {
        if (texture == SkyTexture) return _skyMaterial!;
        if (_shared.TryGetValue((texture, page), out ShaderMaterial? material)) return material;
        material = MakeMaterial(texture, page);
        _shared[(texture, page)] = material;
        // In the world, an animated texture runs its primary chain (the world's frame is 0).
        if (texture >= 0 && texture < _animations.Length && _animations[texture] is { } animation)
            _animated.Add(new Animated { Material = material, Animation = animation, Alternate = false, Current = texture });
        return material;
    }

    // ---- geometry ----------------------------------------------------------------------------------------

    private sealed class Builder
    {
        public readonly List<Vector3> Positions = new(), Normals = new();
        public readonly List<Vector2> Uvs = new(), Uv2 = new();
        public readonly List<Color> Styles = new();
        public readonly List<int> Indices = new();
    }

    // The batch a face belongs to, or false for a face that is not drawn.
    private bool KeyOf(int faceIndex, out (int Texture, int Page) key)
    {
        ref readonly Q1Face face = ref _bsp.Faces[faceIndex];
        key = default;
        if (face.VertexCount < 3) return false;
        Q1TextureClass kind = ClassOf(face.TextureIndex);
        if (kind.Kind == Q1SurfaceKind.NoDraw) return false;
        if (kind.Kind == Q1SurfaceKind.Sky)
        {
            key = (SkyTexture, -1);
            return true;
        }
        key = (face.TextureIndex, _atlas.Faces[faceIndex].Page);
        return true;
    }

    private void Append(Builder b, int faceIndex, float texWidth, float texHeight)
    {
        ref readonly Q1Face face = ref _bsp.Faces[faceIndex];
        ref readonly Q1LightmapPlacement light = ref _atlas.Faces[faceIndex];
        Q1TexInfo info = (uint)face.TexInfoIndex < (uint)_bsp.TexInfo.Length ? _bsp.TexInfo[face.TexInfoIndex] : default;
        int first = b.Positions.Count;
        Vector3 normal = Coords.ToGodot(face.Normal);
        Color styles = new(light.Style0 / 255f, light.Style1 / 255f, light.Style2 / 255f, light.Style3 / 255f);
        for (int v = 0; v < face.VertexCount; v++)
        {
            NVec3 p = _bsp.FaceVertices[face.FirstVertex + v];
            // "s = DotProduct(vertex, vecs[0]) + vecs[0][3]", texture coordinates s / width, t / height
            float s = p.X * info.S.X + p.Y * info.S.Y + p.Z * info.S.Z + info.S.W;
            float t = p.X * info.T.X + p.Y * info.T.Y + p.Z * info.T.Z + info.T.W;
            b.Positions.Add(Coords.ToGodot(p));
            b.Normals.Add(normal);
            b.Uvs.Add(new Vector2(s / texWidth, t / texHeight));
            System.Numerics.Vector2 lm = _atlas.TexCoord(_bsp, faceIndex, s, t);
            b.Uv2.Add(new Vector2(lm.X, lm.Y));
            b.Styles.Add(styles);
        }
        // "triangle fan 0, i+1, i+2"
        for (int i = 0; i + 2 < face.VertexCount; i++)
        {
            b.Indices.Add(first);
            b.Indices.Add(first + i + 1);
            b.Indices.Add(first + i + 2);
        }
    }

    private void Add(Dictionary<(int, int), Builder> batches, int faceIndex)
    {
        if (!KeyOf(faceIndex, out (int Texture, int Page) key)) return;
        if (!batches.TryGetValue(key, out Builder? builder)) batches[key] = builder = new Builder();
        float width = 16, height = 16;
        if (key.Texture >= 0 && key.Texture < _bsp.Textures.Length)
        {
            width = Math.Max(1, _bsp.Textures[key.Texture].Width);
            height = Math.Max(1, _bsp.Textures[key.Texture].Height);
        }
        Append(builder, faceIndex, width, height);
    }

    private static int Pack(ArrayMesh mesh, Builder b)
    {
        if (b.Indices.Count == 0) return 0;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = b.Positions.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = b.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = b.Uvs.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = b.Uv2.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = b.Styles.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = b.Indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        GC.KeepAlive(arrays);   // see MapLoader.PackSurface
        return b.Indices.Count / 3;
    }

    /// <summary>
    /// Model 0: every face of the world, batched by texture and lightmap page inside each cell of a grid of
    /// <paramref name="cellSize"/> units (0: one cell). On a map with visibility data the cells are shown and
    /// hidden by <see cref="WorldPvsCuller"/>, as a Quake 3 level's are.
    /// </summary>
    public Node3D BuildWorld(float cellSize)
    {
        var root = new Node3D { Name = "Q1Map" };
        if (_bsp.Models.Length == 0) return root;
        Q1Model world = _bsp.Models[0];
        int first = Math.Max(0, world.FirstFace), end = (int)Math.Min((long)world.FirstFace + world.FaceCount, _bsp.Faces.Length);

        var cells = new Dictionary<(int, int, int), Dictionary<(int, int), Builder>>();
        var cellOfFace = new (int, int, int)[_bsp.Faces.Length];
        float inverse = cellSize > 0 ? 1f / cellSize : 0f;
        for (int f = first; f < end; f++)
        {
            ref readonly Q1Face face = ref _bsp.Faces[f];
            NVec3 centre = (face.Mins + face.Maxs) * 0.5f;
            (int, int, int) cell = cellSize > 0
                ? ((int)MathF.Floor(centre.X * inverse), (int)MathF.Floor(centre.Y * inverse), (int)MathF.Floor(centre.Z * inverse))
                : (0, 0, 0);
            cellOfFace[f] = cell;
            if (!cells.TryGetValue(cell, out Dictionary<(int, int), Builder>? batches)) cells[cell] = batches = new Dictionary<(int, int), Builder>();
            Add(batches, f);
        }

        // Which visibility clusters see each cell: every leaf lists its faces.
        Dictionary<(int, int, int), HashSet<int>>? clustersOfCell = null;
        if (_bsp.HasVis && _bsp.PvsClusterCount > 0 && cellSize > 0)
        {
            clustersOfCell = new Dictionary<(int, int, int), HashSet<int>>();
            foreach (Q1Leaf leaf in _bsp.Leafs)
            {
                if (leaf.Cluster < 0) continue;
                for (int m = 0; m < leaf.MarkSurfaceCount; m++)
                {
                    int at = leaf.FirstMarkSurface + m;
                    if ((uint)at >= (uint)_bsp.MarkSurfaces.Length) break;
                    int face = _bsp.MarkSurfaces[at];
                    if (face < first || face >= end) continue;
                    (int, int, int) cell = cellOfFace[face];
                    if (!clustersOfCell.TryGetValue(cell, out HashSet<int>? set)) clustersOfCell[cell] = set = new HashSet<int>();
                    set.Add(leaf.Cluster);
                }
            }
        }

        var pvsCells = new List<(MeshInstance3D Node, int[] Clusters)>();
        foreach (((int x, int y, int z) cell, Dictionary<(int, int), Builder> batches) in cells)
        {
            var mesh = new ArrayMesh();
            int surface = 0;
            foreach (((int texture, int page) key, Builder builder) in batches)
            {
                int triangles = Pack(mesh, builder);
                if (triangles == 0) continue;
                mesh.SurfaceSetMaterial(surface++, SharedMaterial(key.texture, key.page));
                WorldTriangles += triangles;
            }
            if (surface == 0) continue;
            WorldSurfaces += surface;
            WorldCells++;
            var instance = new MeshInstance3D
            {
                Name = $"Q1Cell_{cell.x}_{cell.y}_{cell.z}",
                Mesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            root.AddChild(instance);
            int[] clusters = Array.Empty<int>();
            if (clustersOfCell is not null && clustersOfCell.TryGetValue(cell, out HashSet<int>? seen))
            {
                clusters = new int[seen.Count];
                seen.CopyTo(clusters);
            }
            pvsCells.Add((instance, clusters));
        }
        if (clustersOfCell is not null && pvsCells.Count > 1)
            root.AddChild(new WorldPvsCuller(new BspPvs(Q1BspTreeView.Create(_bsp)), pvsCells));
        return root;
    }

    /// <summary>
    /// One model of the map as its own node: "*N" of the level, or model 0 of a file that is an entity's model.
    /// The vertices are where the map compiler left them; the entity's origin and angles move the node. Null for
    /// a model with nothing to draw (a trigger's faces are all "trigger"-textured and are still drawn if an
    /// entity shows them: what is not drawn is a model no entity shows).
    /// </summary>
    public Node3D? BuildModel(int index, double time)
    {
        if (index < 0 || index >= _bsp.Models.Length) return null;
        if (!_modelMeshes.TryGetValue(index, out (ArrayMesh Mesh, List<(int Texture, int Page)> Keys) built))
        {
            Q1Model model = _bsp.Models[index];
            int first = Math.Max(0, model.FirstFace), end = (int)Math.Min((long)model.FirstFace + model.FaceCount, _bsp.Faces.Length);
            var batches = new Dictionary<(int, int), Builder>();
            for (int f = first; f < end; f++) Add(batches, f);
            var mesh = new ArrayMesh();
            var keys = new List<(int, int)>();
            foreach (((int, int) key, Builder builder) in batches)
                if (Pack(mesh, builder) > 0) keys.Add(key);
            built = (mesh, keys);
            _modelMeshes[index] = built;
        }
        if (built.Keys.Count == 0) return null;

        var instance = new MeshInstance3D { Name = "Q1Model" + index, Mesh = built.Mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        List<Animated>? own = null;
        for (int s = 0; s < built.Keys.Count; s++)
        {
            (int texture, int page) = built.Keys[s];
            if (texture >= 0 && texture < _animations.Length && _animations[texture] is { } animation)
            {
                // Its own material: this entity's frame picks the chain.
                ShaderMaterial material = MakeMaterial(texture, page);
                var animated = new Animated { Material = material, Animation = animation, Alternate = false, Current = texture };
                ApplyFrame(animated, time);
                _animated.Add(animated);
                (own ??= new List<Animated>()).Add(animated);
                instance.SetSurfaceOverrideMaterial(s, material);
            }
            else instance.SetSurfaceOverrideMaterial(s, SharedMaterial(texture, page));
        }
        var root = new Node3D { Name = "Q1Model" + index };
        root.AddChild(instance);
        if (own is not null)
        {
            foreach (Animated a in own) a.Owner = root;
            _animatedOfNode[root.GetInstanceId()] = own;
        }
        return root;
    }

    /// <summary>One line for the log.</summary>
    public string Describe() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{_bsp.Format}, {_bsp.Faces.Length} faces, {_bsp.Models.Length} models, {_textures.Count} textures ({ExternalTextures} replaced by files, {MissingTextures} missing), " +
        $"{_atlas.Pages.Length} lightmap pages ({_atlas.SampleCount * 4 / 1048576.0:0.0} MB{(_atlas.FullBright ? ", no light data: full bright" : "")}), " +
        $"{WorldCells} cells, {WorldSurfaces} surfaces, {WorldTriangles} triangles, {_shared.Count} materials ({_animated.Count} animated), " +
        $"sky {(_skyMaterial?.Shader == Q1SurfaceShader.SkyBox ? "box" : HasSkyTexture ? "Quake clouds" : "none")}, " +
        $"vis {(_bsp.HasVis ? "yes" : "no")}, water alpha {(_bsp.SupportsWaterAlpha ? "supported" : "not supported")}, palette full-brights {(_palette.HasFullbrights ? "from " + _palette.FullbrightStart : "none")}");
}

/// <summary>Reads what a Quake 1 format map needs from the game data besides the map itself.</summary>
public static class Q1MapLoader
{
    /// <summary>
    /// Palette_Load: <c>gfx/palette.lmp</c> over the built-in Quake palette, and the full-bright range from
    /// <c>gfx/colormap.lmp</c> - none without that file, which is the case for Xonotic's own data.
    /// </summary>
    public static Q1Palette LoadPalette(VortexArena.Formats.Vfs.VirtualFileSystem files)
    {
        byte[] palette = Read(files, "gfx/palette.lmp"), colormap = Read(files, "gfx/colormap.lmp");
        return palette.Length == 0 && colormap.Length == 0 ? Q1Palette.BuiltIn : Q1Palette.Load(palette, colormap);

        static byte[] Read(VortexArena.Formats.Vfs.VirtualFileSystem vfs, string path)
        {
            try { return vfs.Exists(path) ? vfs.ReadBytes(path) : Array.Empty<byte>(); }
            catch (Exception e) when (e is System.IO.IOException or InvalidOperationException) { return Array.Empty<byte>(); }
        }
    }
}
