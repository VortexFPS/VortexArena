using System.Numerics;

namespace XonoticGodot.Formats.Mdl;

/// <summary>
/// Engine-neutral, Godot-free representation of a Quake1 MDL ("IDPO", <see cref="MdlReader.Version"/> 6)
/// alias model, parsed by <see cref="MdlReader"/>. MDL is a vertex-morph format like MD3: geometry is a
/// stack of per-frame byte-quantized vertex positions (scaled by the header <c>scale</c>/<c>origin</c>),
/// a single embedded 8-bit palettised skin, and a shared texcoord/triangle set.
///
/// <para>Xonotic ships a handful of these as legacy props — the shotgun shell casing
/// (<c>models/casing_shell.mdl</c> / <c>casing_steel.mdl</c>) and the fast gib chunk
/// (<c>models/gibs/chunk.mdl</c>) are the only ones that reach the runtime model loader; all are static
/// single-frame meshes. The host turns this into a Godot <c>MeshInstance3D</c> (frame 0) via
/// <c>MdlBuilder</c>.</para>
///
/// <para>Coordinates are Quake units (X fwd, Y left, Z up), exactly as stored; the Godot host swaps axes at
/// the render boundary. Ground truth: Darkplaces <c>Mod_IDP0_Load</c> (<c>model_alias.c</c>) and the on-disk
/// structs in <c>modelgen.h</c>.</para>
/// </summary>
public sealed class MdlData
{
    /// <summary>Frame 0's grab name (the model has no internal name field of its own).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Embedded skin width in texels (header <c>skinwidth</c>).</summary>
    public int SkinWidth { get; init; }

    /// <summary>Embedded skin height in texels (header <c>skinheight</c>).</summary>
    public int SkinHeight { get; init; }

    /// <summary>
    /// The first skin decoded to straight RGBA8 (<c>SkinWidth * SkinHeight * 4</c> bytes) through the Quake
    /// palette — the same <c>palette_bgra_complete</c> path DP's <c>R_SkinFrame_LoadInternalQuake</c> uses.
    /// Empty when the model ships no skin (rare); the builder then uses a flat material.
    /// </summary>
    public byte[] SkinRgba { get; init; } = System.Array.Empty<byte>();

    /// <summary>Number of unique model vertices (header <c>numverts</c>); each frame stores this many.</summary>
    public int VertexCount { get; init; }

    /// <summary>
    /// Render-ready triangle corners (length = triangle count * 3, one per triangle vertex). Each carries the
    /// index of the model vertex it draws (into a frame's <see cref="MdlFrame.Vertices"/>) and its final,
    /// seam-resolved UV. MDL stores texcoords per vertex but a vertex on the skin seam gets a different U on
    /// back-facing triangles (DP's <c>vertonseam</c>/<c>facesfront</c> "butcher" step), so we pre-expand to
    /// non-indexed corners here rather than duplicate/compact a vertex set.
    /// </summary>
    public MdlCorner[] Corners { get; init; } = System.Array.Empty<MdlCorner>();

    /// <summary>Animation frames (at least one). Static props (casings/chunk) have exactly one.</summary>
    public MdlFrame[] Frames { get; init; } = System.Array.Empty<MdlFrame>();
}

/// <summary>One triangle corner: the model-vertex index it draws and its seam-resolved texture UV (0..1).</summary>
public readonly record struct MdlCorner(int Vertex, Vector2 Uv);

/// <summary>One animation frame: its grab name and the decoded per-vertex positions + normals (Quake units).</summary>
public sealed class MdlFrame
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Decoded vertices (length == <see cref="MdlData.VertexCount"/>). Indexed by <see cref="MdlCorner.Vertex"/>.</summary>
    public MdlVertex[] Vertices { get; init; } = System.Array.Empty<MdlVertex>();
}

/// <summary>A decoded frame vertex: position (Quake units) and a unit normal (from the Quake anorms table).</summary>
public readonly record struct MdlVertex(Vector3 Position, Vector3 Normal);
