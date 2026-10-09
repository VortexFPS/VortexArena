using System.Numerics;

namespace VortexArena.Formats.Bsp;

/// <summary>Which of the Quake 1 family of map formats a file is (DarkPlaces <c>model_shared.c</c> loader table).</summary>
public enum Q1BspFormat
{
    /// <summary>Quake: version 29, 16-bit indices.</summary>
    Bsp29,
    /// <summary>"BSP2": 32-bit indices, float node and leaf bounds.</summary>
    Bsp2,
    /// <summary>"2PSB" (RMQ engine): 32-bit indices, 16-bit node and leaf bounds.</summary>
    Bsp2Rmqe,
    /// <summary>Half-Life: version 30. Coloured light in the map, a palette per texture, other hull sizes.</summary>
    HalfLife,
}

/// <summary>
/// A Quake 1 format map (BSP 29, "BSP2", "2PSB", Half-Life 30) as <see cref="Q1BspReader"/> parses it:
/// the data of DarkPlaces' <c>Mod_Q1BSP_Load</c> (<c>model_brush.c</c>) before anything is built for a
/// renderer. Quake coordinates and units, exactly as stored. Engine-neutral and immutable after the read.
///
/// What differs from a Quake 3 map (<see cref="BspData"/>), and why the two do not share a type: no brushes
/// (collision is by clipping hulls: <see cref="ClipNodes"/> and the node tree), faces are polygons over
/// shared edges with texture axes in place of texture coordinates, light is per-face sample blocks in up to
/// four style layers instead of 128x128 pages, textures are 8-bit images inside the file, and there is no
/// light grid.
/// </summary>
public sealed class Q1BspData
{
    public Q1BspFormat Format { get; init; }
    public bool IsHalfLife => Format == Q1BspFormat.HalfLife;
    public bool IsBsp2 => Format is Q1BspFormat.Bsp2 or Q1BspFormat.Bsp2Rmqe;

    /// <summary>The entity lump, as text.</summary>
    public string EntitiesText { get; init; } = string.Empty;
    /// <summary>The entity lump parsed (<see cref="EntityLumpParser"/>); the first is worldspawn.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Entities { get; init; } = Array.Empty<IReadOnlyDictionary<string, string>>();

    public Q1Plane[] Planes { get; init; } = Array.Empty<Q1Plane>();

    /// <summary>The textures of the file, in file order (a face names one through its <see cref="Q1TexInfo"/>).
    /// An entry whose miptex is absent from the lump has <see cref="Q1Texture.Present"/> false.</summary>
    public Q1Texture[] Textures { get; init; } = Array.Empty<Q1Texture>();
    public Q1TexInfo[] TexInfo { get; init; } = Array.Empty<Q1TexInfo>();

    /// <summary>The faces. Each owns <see cref="FaceVertices"/>[FirstVertex .. FirstVertex+VertexCount), a
    /// convex polygon in the winding DarkPlaces draws (triangle fan 0, i+1, i+2).</summary>
    public Q1Face[] Faces { get; init; } = Array.Empty<Q1Face>();
    public Vector3[] FaceVertices { get; init; } = Array.Empty<Vector3>();

    /// <summary>
    /// Light samples, three bytes (R, G, B) a sample whatever the file held: a Quake map's one byte expanded
    /// (or replaced by its <c>.lit</c> file), a Half-Life map's colours halved, as
    /// <c>Mod_Q1BSP_LoadLighting</c> stores them. A sample of 128 is full light (the range is 0 to 2).
    /// Empty for an unlit map: every surface is then drawn at full light.
    /// </summary>
    public byte[] LightData { get; init; } = Array.Empty<byte>();
    /// <summary>True when the light came from a <c>.lit</c> file or a Half-Life map.</summary>
    public bool LightIsColoured { get; init; }
    /// <summary>The <c>.dlit</c> file's light directions, parallel to <see cref="LightData"/>; empty without one.</summary>
    public byte[] DeluxeData { get; init; } = Array.Empty<byte>();

    public Q1Node[] Nodes { get; init; } = Array.Empty<Q1Node>();
    public Q1Leaf[] Leafs { get; init; } = Array.Empty<Q1Leaf>();
    /// <summary>Leaf to face references; a leaf owns [FirstMarkSurface, FirstMarkSurface+MarkSurfaceCount).</summary>
    public int[] MarkSurfaces { get; init; } = Array.Empty<int>();
    /// <summary>The clipping hulls 1 to 3 (hull 0 is the node tree itself).</summary>
    public Q1ClipNode[] ClipNodes { get; init; } = Array.Empty<Q1ClipNode>();

    /// <summary>Model 0 is the world; model N is what an entity names as "*N".</summary>
    public Q1Model[] Models { get; init; } = Array.Empty<Q1Model>();

    /// <summary>Visibility: <see cref="PvsClusterCount"/> rows of <see cref="PvsClusterBytes"/> bytes, already
    /// decompressed (a row not in the file is all ones). Cluster c is leaf c+1. Zero rows for an unvised map.</summary>
    public byte[] PvsClusters { get; init; } = Array.Empty<byte>();
    public int PvsClusterCount { get; init; }
    public int PvsClusterBytes { get; init; }
    /// <summary>True when the file had a visibility lump.</summary>
    public bool HasVis { get; init; }

    /// <summary><c>Mod_Q1BSP_CheckWaterAlphaSupport</c>: false for a map whose visibility data was made for
    /// opaque water (no water or slime leaf can see an empty one). Such a map is drawn with opaque liquids
    /// whatever <c>r_wateralpha</c> says, because nothing behind the surface is in the visible set.</summary>
    public bool SupportsWaterAlpha { get; init; } = true;

    /// <summary>The clipping hull boxes of the format: [hull] (mins, maxs). Quake: point, 32x32x56, 64x64x88.
    /// Half-Life: point, 32x32x72, 64x64x64, 32x32x36.</summary>
    public (Vector3 Mins, Vector3 Maxs)[] HullSizes { get; init; } = Array.Empty<(Vector3, Vector3)>();

    /// <summary>True if cluster <paramref name="to"/> may be seen from cluster <paramref name="from"/>.</summary>
    public bool ClusterVisible(int from, int to)
    {
        if (PvsClusterCount <= 0 || from < 0 || to < 0 || from >= PvsClusterCount || to >= PvsClusterCount) return true;
        return (PvsClusters[from * PvsClusterBytes + (to >> 3)] & (1 << (to & 7))) != 0;
    }

    /// <summary><c>Mod_BSP_PointInLeaf</c> on the world: the leaf a point is in (0, the solid leaf, for a tree that cannot be walked).</summary>
    public int PointInLeaf(Vector3 point) => PointInLeaf(point, Models.Length > 0 ? Models[0].HeadNode0 : 0);

    public int PointInLeaf(Vector3 point, int node)
    {
        int guard = Nodes.Length + 1;
        while (node >= 0)
        {
            if (node >= Nodes.Length || guard-- <= 0) return 0;
            ref readonly Q1Node n = ref Nodes[node];
            ref readonly Q1Plane plane = ref Planes[n.PlaneIndex];
            float d = plane.Type < 3 ? Component(point, plane.Type) : Vector3.Dot(point, plane.Normal);
            node = d < plane.Dist ? n.Child1 : n.Child0;
        }
        int leaf = -(node + 1);
        return leaf < Leafs.Length ? leaf : 0;
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}

/// <summary>A plane. <see cref="Type"/> is DarkPlaces' <c>PlaneClassify</c>: 0, 1 or 2 when the normal is
/// exactly +X, +Y or +Z (the distance is then compared with one coordinate), 3 otherwise.</summary>
public readonly record struct Q1Plane(Vector3 Normal, float Dist, int Type);

/// <summary>
/// A texture of the file. <see cref="Name"/> is lower case, at most 16 characters ("unnamedN" for an empty
/// one). <see cref="Pixels"/> is the full-size image, one palette index a pixel, row by row from the top, or
/// null when the file holds only the name and size (the image then comes from elsewhere).
/// <see cref="Palette"/> is a Half-Life texture's own 256 RGB colours, null otherwise (the Quake palette applies).
/// </summary>
public sealed record Q1Texture(string Name, int Width, int Height, byte[]? Pixels, byte[]? Palette, bool Present);

/// <summary>Texture axes of a face: s = dot(position, S.xyz) + S.w, t likewise, in texels.
/// <see cref="Flags"/> bit 0 (TEX_SPECIAL) marks sky and liquid: no lightmap.</summary>
public readonly record struct Q1TexInfo(Vector4 S, Vector4 T, int MipTex, int Flags)
{
    public const int Special = 1;
    public bool IsSpecial => (Flags & Special) != 0;
}

/// <summary>
/// A face. <see cref="TextureIndex"/> is into <see cref="Q1BspData.Textures"/>, or -1 for "no texture found"
/// (a wall), or -2 for the same on a TEX_SPECIAL face (drawn as water), as DarkPlaces assigns its two spare
/// textures. The light block of the face is <see cref="LightWidth"/> x <see cref="LightHeight"/> samples, one
/// layer per style in <see cref="Style0"/>..<see cref="Style3"/> that is not 255, starting at
/// <see cref="LightOffset"/> bytes into <see cref="Q1BspData.LightData"/> (-1: none). A liquid face without
/// samples has <see cref="WhiteLight"/>: one layer of 128 on style 0. A face with <see cref="Lightmapped"/>
/// false is drawn without a lightmap (sky; any face of an unlit map is still lightmapped, by a white block).
/// </summary>
public readonly record struct Q1Face(
    int PlaneIndex, bool PlaneBack, int FirstVertex, int VertexCount, int TexInfoIndex, int TextureIndex,
    byte Style0, byte Style1, byte Style2, byte Style3, int LightOffset, bool WhiteLight, bool Lightmapped,
    int TextureMinS, int TextureMinT, int ExtentS, int ExtentT, Vector3 Normal, Vector3 Mins, Vector3 Maxs)
{
    public int LightWidth => (ExtentS >> 4) + 1;
    public int LightHeight => (ExtentT >> 4) + 1;
    public byte Style(int layer) => layer == 0 ? Style0 : layer == 1 ? Style1 : layer == 2 ? Style2 : Style3;
}

/// <summary>A node of the drawing tree. A child of 0 or more is a node; a negative child is the leaf <c>-(child+1)</c>.
/// The node owns the faces [FirstFace, FirstFace+FaceCount), all lying on its plane.</summary>
public readonly record struct Q1Node(int PlaneIndex, int Child0, int Child1, Vector3 Mins, Vector3 Maxs, int FirstFace, int FaceCount);

/// <summary>A leaf. <see cref="Contents"/> is CONTENTS_EMPTY (-1), SOLID (-2), WATER (-3), SLIME (-4), LAVA (-5) or
/// SKY (-6). <see cref="Cluster"/> is the visibility cluster (leaf index - 1), -1 for none.</summary>
public readonly record struct Q1Leaf(int Contents, int Cluster, Vector3 Mins, Vector3 Maxs, int FirstMarkSurface, int MarkSurfaceCount, uint AmbientLevels);

/// <summary>A node of a clipping hull. A child of 0 or more is a clip node; a negative child is a contents value.</summary>
public readonly record struct Q1ClipNode(int PlaneIndex, int Child0, int Child1);

/// <summary>A model of the map. The bounds are the file's, spread by one unit as DarkPlaces spreads them.
/// <see cref="HeadNode0"/> is into the node tree, the others into <see cref="Q1BspData.ClipNodes"/>.</summary>
public readonly record struct Q1Model(Vector3 Mins, Vector3 Maxs, Vector3 Origin, int HeadNode0, int HeadNode1, int HeadNode2, int HeadNode3, int VisLeafs, int FirstFace, int FaceCount)
{
    public int HeadNode(int hull) => hull == 0 ? HeadNode0 : hull == 1 ? HeadNode1 : hull == 2 ? HeadNode2 : HeadNode3;
}

/// <summary>Leaf contents of a Quake 1 map (<c>bspfile.h</c>).</summary>
public static class Q1Contents
{
    public const int Empty = -1, Solid = -2, Water = -3, Slime = -4, Lava = -5, Sky = -6;
}
