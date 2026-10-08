// Port of Base/darkplaces/model_alias.c Mod_IDP3_Load and Mod_INTERQUAKEMODEL_Load as far as they
// decide what an alias model collides as: surfmesh.data_vertex3f (the first frame of an MD3, the
// rest pose of an IQM) and data_element3i, surfmesh.isanimated (Mod_Alias_CalculateBoundingBox's
// answer for an MD3, the bone and frame counts for an IQM), and the texture of each surface
// (Mod_BuildAliasSkinsFromSkinFiles); model_shared.c Mod_LoadSkinFiles, Mod_LookupQ3Shader and the
// part of Mod_LoadTextureFromQ3Shader that gives a texture its supercontents and surfaceflags.
// The mesh itself and how it is traced are VortexArena.Engine.Collision.CollisionMesh / TraceService.
using System.Numerics;
using VortexArena.Engine.Collision;
using VortexArena.Formats;
using VortexArena.Formats.Iqm;
using VortexArena.Formats.Materials;
using VortexArena.Formats.Md3;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Legacy.Server;

/// <summary>
/// The collision meshes of the models a level's entities show, by model name: what a SOLID_BSP
/// entity whose model is not a map submodel (a crate, a barrel, a jump pad) is clipped against, and
/// any entity under MOVE_HITMODEL. Used by both halves of legacy mode - <see cref="SvWorld"/> and
/// the client's BspLegacyWorld - each with its own instance.
///
/// The names come from a QuakeC program, and on the client half that program and the models it
/// names may be a remote server's. So: a file is read once and its answer kept, including "no
/// mesh"; a model over <see cref="MaxTrianglesPerModel"/> triangles, or one that would take the
/// cache past <see cref="MaxCachedModels"/> meshes or <see cref="MaxCachedBytes"/>, gets no mesh
/// and its entity is clipped as its box; nothing is ever evicted, so no sequence of names can make
/// this read files in a loop; and nothing thrown while reading or parsing leaves <see cref="Get"/>.
/// </summary>
/// <remarks>
/// Deviations from DarkPlaces:
/// <list type="bullet">
/// <item>The mesh is always the model's first frame. DarkPlaces poses an animated model by the
/// entity's frame, lerp and skeleton when those are not the first frame at rest
/// (Mod_MDLMD2MD3_TraceBox: "for static cases we can just call CollisionBIH which is much faster").
/// Xonotic's solid map models are static, and its program never asks for MOVE_HITMODEL.</item>
/// <item>MD3 and IQM only. An OBJ, DPM, MDL, ZYM or PSK model has no mesh here and is clipped as
/// its box, as every model was before.</item>
/// <item>dpmeshcollisions and the per-material skip masks of a trace are not read.</item>
/// </list>
/// </remarks>
public sealed class SvModelCollision
{
    /// <summary>More triangles than this and the model is clipped as its box. (The largest stock
    /// Xonotic model, a player at full detail, has about 14,000.)</summary>
    public const int MaxTrianglesPerModel = 65536;
    /// <summary>Distinct models that may hold a mesh at once.</summary>
    public const int MaxCachedModels = 96;
    /// <summary>What all cached meshes together may hold, by <see cref="CollisionMesh.ApproximateBytes"/>.</summary>
    public const long MaxCachedBytes = 48L * 1024 * 1024;
    /// <summary>Names whose answer ("no mesh") is remembered; past this an unknown name is refused unread.</summary>
    public const int MaxRememberedNames = 2048;
    /// <summary>A model file larger than this is not read for its mesh.</summary>
    public const int MaxFileBytes = 32 * 1024 * 1024;
    private const int MaxShaderFiles = 1024;
    private const long MaxShaderBytes = 64L * 1024 * 1024;
    private const int MaxSkinFileBytes = 256 * 1024;

    private readonly VirtualFileSystem _files;
    private readonly Dictionary<string, CollisionMesh?> _cache = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, ShaderDef>? _shaders;
    private int _meshes;
    private long _bytes;

    public SvModelCollision(VirtualFileSystem files) => _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <summary>Meshes held.</summary>
    public int CachedMeshes => _meshes;
    public long CachedBytes => _bytes;
    /// <summary>Model files read and parsed for a mesh since the last <see cref="Clear"/>.</summary>
    public int ModelsRead { get; private set; }
    /// <summary>Models that would have had a mesh but were refused by one of the bounds.</summary>
    public int Refused { get; private set; }

    /// <summary>A new level: forget every mesh. (The shader scripts are kept; the game data has not changed.)</summary>
    public void Clear()
    {
        _cache.Clear();
        _meshes = 0;
        _bytes = 0;
        ModelsRead = 0;
        Refused = 0;
    }

    /// <summary>
    /// The mesh of the model of that name, or null if it has none here: no such file, not an MD3
    /// or IQM, damaged, or refused by a bound. Never throws.
    /// </summary>
    public CollisionMesh? Get(string? name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '*') return null;
        if (_cache.TryGetValue(name, out CollisionMesh? known)) return known;
        if (_cache.Count >= MaxRememberedNames) return null;
        CollisionMesh? mesh = null;
        try
        {
            if (_meshes < MaxCachedModels && _bytes < MaxCachedBytes) mesh = Load(name);
            else Refused++;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // The readers reject what they detect; whatever else a damaged file trips is the same answer.
            mesh = null;
        }
        if (mesh is not null)
        {
            _meshes++;
            _bytes += mesh.ApproximateBytes;
        }
        _cache[name] = mesh;
        return mesh;
    }

    private CollisionMesh? Load(string name)
    {
        if (!LegacyQcHost.IsSafePath(name) || !_files.Exists(name)) return null;
        byte[] data = _files.ReadBytes(name);
        ModelsRead++;
        if (data.Length < 8 || data.Length > MaxFileBytes) return null;
        if (data.AsSpan().StartsWith("IDP3"u8)) return FromMd3(name, Md3Reader.Read(data));
        if (data.AsSpan().StartsWith("INTERQUAKEMODEL\0"u8)) return FromIqm(name, IqmReader.Read(data));
        return null;
    }

    // Mod_IDP3_Load: every mesh of the file is a surface; its vertices are those of frame 0 (the
    // short coordinates times 1/64, which the reader has done), its elements as the file has them.
    private CollisionMesh? FromMd3(string name, Md3Data md3)
    {
        long triangles = 0, vertexCount = 0;
        foreach (Md3Surface surface in md3.Surfaces)
        {
            triangles += surface.Triangles.Length / 3;
            vertexCount += surface.VertexCount;
        }
        if (triangles == 0) return null;
        if (triangles > MaxTrianglesPerModel || vertexCount > MaxTrianglesPerModel * 3L) { Refused++; return null; }

        Dictionary<string, string>? skin = LoadSkinFile(name);
        Vector3[] vertices = new Vector3[vertexCount];
        int[] elements = new int[triangles * 3], triangleSurface = new int[triangles];
        CollisionMesh.Surface[] surfaces = new CollisionMesh.Surface[md3.Surfaces.Length];
        int firstVertex = 0, firstTriangle = 0;
        bool animated = false;
        for (int s = 0; s < md3.Surfaces.Length; s++)
        {
            Md3Surface surface = md3.Surfaces[s];
            // "LittleLong(pinmesh->num_shaders) >= 1 ? md3shader->name : """
            surfaces[s] = SurfaceFor(skin, surface.Name, surface.Shaders.Length >= 1 ? surface.Shaders[0] : "");
            Md3Vertex[][] frames = surface.FrameVertices;
            int count = surface.VertexCount;
            if (frames.Length > 0)
            {
                Md3Vertex[] first = frames[0];
                for (int v = 0; v < count && v < first.Length; v++) vertices[firstVertex + v] = first[v].Position;
                // Mod_Alias_CalculateBoundingBox: "if (!isanimated && memcmp(refvertex3f, vertex3f, ...)) isanimated = true"
                for (int f = 1; f < frames.Length && !animated; f++)
                {
                    Md3Vertex[] frame = frames[f];
                    for (int v = 0; v < count && v < first.Length && v < frame.Length; v++)
                        if (frame[v].Position != first[v].Position) { animated = true; break; }
                }
            }
            int surfaceTriangles = surface.Triangles.Length / 3;
            for (int t = 0; t < surfaceTriangles; t++)
            {
                // an index outside the surface's own vertices would reach another surface's: make it invalid
                for (int k = 0; k < 3; k++)
                {
                    int index = surface.Triangles[t * 3 + k];
                    elements[(firstTriangle + t) * 3 + k] = (uint)index < (uint)count ? firstVertex + index : -1;
                }
                triangleSurface[firstTriangle + t] = s;
            }
            firstVertex += count;
            firstTriangle += surfaceTriangles;
        }
        return CollisionMesh.Create(vertices, elements, triangleSurface, surfaces, MaxTrianglesPerModel, pointUsesBodyBox: animated);
    }

    // Mod_INTERQUAKEMODEL_Load: the vertex positions as the file has them (the rest pose), the
    // triangles in the file's order, one surface per mesh with the mesh's material as its texture.
    private CollisionMesh? FromIqm(string name, IqmData iqm)
    {
        int triangles = iqm.Triangles.Length / 3;
        if (triangles == 0) return null;
        if (triangles > MaxTrianglesPerModel || iqm.Positions.Length > MaxTrianglesPerModel * 3L) { Refused++; return null; }
        Dictionary<string, string>? skin = LoadSkinFile(name);
        // a triangle no mesh claims has no surface and is left out
        int[] triangleSurface = new int[triangles];
        Array.Fill(triangleSurface, -1);
        CollisionMesh.Surface[] surfaces = new CollisionMesh.Surface[iqm.Meshes.Length];
        for (int m = 0; m < iqm.Meshes.Length; m++)
        {
            IqmMesh mesh = iqm.Meshes[m];
            // Mod_BuildAliasSkinsFromSkinFiles(..., IQM_TEXT(mesh.name), IQM_TEXT(mesh.material))
            surfaces[m] = SurfaceFor(skin, mesh.Name, mesh.Material);
            long last = (long)mesh.FirstTriangle + mesh.TriangleCount;
            for (long t = Math.Max(0, mesh.FirstTriangle); t < last && t < triangles; t++) triangleSurface[t] = m;
        }
        // "loadmodel->surfmesh.isanimated = loadmodel->num_bones > 1 || loadmodel->numframes > 1 ||
        // (loadmodel->animscenes && loadmodel->animscenes[0].framecount > 1)"; numframes is max(num_anims, 1)
        bool animated = iqm.Joints.Length > 1 || iqm.Anims.Length > 1 || (iqm.Anims.Length > 0 && iqm.Anims[0].FrameCount > 1);
        return CollisionMesh.Create(iqm.Positions, iqm.Triangles, triangleSurface, surfaces, MaxTrianglesPerModel, pointUsesBodyBox: animated);
    }

    // ---- textures ------------------------------------------------------------------------------------

    // Mod_LoadSkinFiles for skin 0 ("%s_%i.skin"): mesh name to replacement shader. Null if the
    // model has no skin file. The collision leaves use the textures of skin 0 (surface->texture).
    private Dictionary<string, string>? LoadSkinFile(string model)
    {
        string path = model + "_0.skin";
        if (!LegacyQcHost.IsSafePath(path) || !_files.Exists(path)) return null;
        byte[] data = _files.ReadBytes(path);
        if (data.Length > MaxSkinFileBytes) return new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> items = new(StringComparer.Ordinal);
        foreach (string rawLine in System.Text.Encoding.UTF8.GetString(data).Split('\n'))
        {
            // COM_ParseToken_QuakeC: whitespace separates words and a comma is a word of its own
            string[] words = rawLine.Replace(",", " , ").Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > 10) continue;
            if (words[0] == "replace")
            {
                // (Of two lines that name one mesh the later wins: the C prepends each item to a list
                // and searches it from the head.)
                if (words.Length == 3) items[words[1]] = words[2];
            }
            else if (words.Length >= 2 && words[0].StartsWith("tag_", StringComparison.Ordinal)) { /* "not used for anything" */ }
            else if (words.Length >= 2 && words[1] == ",") items[words[0]] = words.Length >= 3 ? words[2] : "";
        }
        return items;
    }

    // Mod_BuildAliasSkinsFromSkinFiles for one mesh, then Mod_LoadTextureFromQ3Shader.
    private CollisionMesh.Surface SurfaceFor(Dictionary<string, string>? skin, string meshName, string shaderName)
    {
        if (skin is not null)
        {
            // "don't render unmentioned meshes": Mod_LoadCustomMaterial(..., SUPERCONTENTS_SOLID, ...)
            if (!skin.TryGetValue(meshName, out string? replacement)) return new CollisionMesh.Surface(SuperContents.Solid, 0, meshName);
            shaderName = replacement;
        }
        string name = StripImageExtension(shaderName);
        if (name.Length > 0 && Shaders().TryGetValue(name, out ShaderDef? shader)) return FromShader(name, shader);
        // "using fallback noshader material" / "No shader found for texture": solid and opaque, except
        // the nodraw material, which is solid alone. (An alias model's texture has no surfaceflags of its own.)
        bool noDraw = name is "common/nodraw" or "textures/common/nodraw";
        return new CollisionMesh.Surface(noDraw ? SuperContents.Solid : SuperContents.Solid | SuperContents.Opaque, 0, name);
    }

    // bspfile.h Q3SURFACEFLAG_*
    private const int FlagNoDamage = 1, FlagSlick = 2, FlagSky = 4, FlagNoImpact = 16, FlagNoMarks = 32, FlagNoDraw = 128, FlagHint = 256,
        FlagNoLightmap = 1024, FlagPointLight = 2048, FlagMetalSteps = 4096, FlagNonSolid = 16384, FlagLightFilter = 32768,
        FlagAlphaShadow = 65536, FlagNoDlight = 131072, FlagDust = 262144;

    // "set up default supercontents (on q3bsp this is overridden by the q3bsp loader)" and the
    // surfaceflags below it. The C assigns, in this order, then ORs: the order is kept.
    private static CollisionMesh.Surface FromShader(string name, ShaderDef shader)
    {
        HashSet<string> p = shader.SurfaceParms;
        int contents = SuperContents.Solid | SuperContents.Opaque;
        if (p.Contains("lava")) contents = SuperContents.Lava;
        if (p.Contains("slime")) contents = SuperContents.Slime;
        if (p.Contains("water")) contents = SuperContents.Water;
        if (p.Contains("nonsolid")) contents = 0;
        if (p.Contains("playerclip")) contents = SuperContents.PlayerClip;
        if (p.Contains("botclip")) contents = SuperContents.MonsterClip;
        if (p.Contains("sky")) contents = SuperContents.Sky;
        if (p.Contains("donotenter")) contents |= SuperContents.DonotEnter;
        if (p.Contains("lava")) contents |= SuperContents.Lava;
        if (p.Contains("nodrop")) contents |= SuperContents.NoDrop;
        if (p.Contains("playerclip")) contents |= SuperContents.PlayerClip;
        if (p.Contains("sky")) contents |= SuperContents.Sky;
        if (p.Contains("slime")) contents |= SuperContents.Slime;
        if (p.Contains("water")) contents |= SuperContents.Water;
        if (p.Contains("botclip")) contents |= SuperContents.BotClip | SuperContents.MonsterClip;

        int flags = 0;
        if (p.Contains("alphashadow")) flags |= FlagAlphaShadow;
        if (p.Contains("lightfilter")) flags |= FlagLightFilter;
        if (p.Contains("metalsteps")) flags |= FlagMetalSteps;
        if (p.Contains("nodamage")) flags |= FlagNoDamage;
        if (p.Contains("nodlight")) flags |= FlagNoDlight;
        if (p.Contains("nodraw")) flags |= FlagNoDraw;
        if (p.Contains("noimpact")) flags |= FlagNoImpact;
        if (p.Contains("nolightmap")) flags |= FlagNoLightmap;
        if (p.Contains("nomarks")) flags |= FlagNoMarks;
        if (p.Contains("nonsolid")) flags |= FlagNonSolid;
        if (p.Contains("sky")) flags |= FlagSky;
        if (p.Contains("slick")) flags |= FlagSlick;
        if (p.Contains("pointlight")) flags |= FlagPointLight;
        if (p.Contains("hint")) flags |= FlagHint;
        if (p.Contains("dust")) flags |= FlagDust;
        return new CollisionMesh.Surface(contents, flags, name);
    }

    // Mod_LoadQ3Shaders, on first need: every scripts/*.shader, the first definition of a name
    // winning, names compared without case (Mod_LookupQ3Shader). An empty table if none can be read.
    private IReadOnlyDictionary<string, ShaderDef> Shaders()
    {
        if (_shaders is not null) return _shaders;
        List<string> texts = new();
        try
        {
            long total = 0;
            foreach (string path in _files.Find("scripts/", ".shader").OrderBy(p => p, StringComparer.Ordinal).Take(MaxShaderFiles))
            {
                byte[] data = _files.ReadBytes(path);
                total += data.Length;
                if (total > MaxShaderBytes) break;
                texts.Add(System.Text.Encoding.UTF8.GetString(data));
            }
            _shaders = Q3ShaderParser.ParseFiles(texts);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            _shaders = new Dictionary<string, ShaderDef>(StringComparer.OrdinalIgnoreCase);
        }
        return _shaders;
    }

    // Image_StripImageExtension: ".tga", ".pcx", ".lmp", ".png", ".jpg", ".wal" - and only those.
    private static string StripImageExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || name.Length - dot != 4) return name;
        ReadOnlySpan<char> ext = name.AsSpan(dot + 1);
        foreach (string known in ImageExtensions)
            if (ext.Equals(known, StringComparison.OrdinalIgnoreCase)) return name[..dot];
        return name;
    }

    private static readonly string[] ImageExtensions = { "tga", "pcx", "lmp", "png", "jpg", "wal" };
}
