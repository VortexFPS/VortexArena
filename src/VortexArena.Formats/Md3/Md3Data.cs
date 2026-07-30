using System.Numerics;

namespace XonoticGodot.Formats.Md3;

/// <summary>
/// Engine-neutral, Godot-free representation of an MD3 (id Tech 3 "IDP3") model, parsed by
/// <see cref="Md3Reader"/>. MD3 is a vertex-morph format: geometry is stored once per surface as a
/// stack of per-frame vertex positions (int16, scaled by 1/64). Animation is selecting/blending frames.
///
/// Coordinates are Quake units, exactly as stored (no axis swap). The Godot host turns surfaces into
/// ArrayMesh morph targets (or baked Animation) and exposes <see cref="Tags"/> as attachment sockets.
///
/// Ground truth: Darkplaces <c>model_alias.h</c> (md3 structs) and <c>Mod_IDP3_Load</c> in <c>model_alias.c</c>.
/// </summary>
public sealed class Md3Data
{
    /// <summary>Internal model name (header name[64]).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Header flags field (MD3 model flags; used by DP to derive EF_* effects).</summary>
    public int Flags { get; init; }

    /// <summary>Number of animation frames. Every surface has exactly this many morph frames.</summary>
    public int FrameCount { get; init; }

    /// <summary>Per-frame bounding info (mins/maxs/origin/radius) and frame name.</summary>
    public Md3Frame[] Frames { get; init; } = Array.Empty<Md3Frame>();

    /// <summary>
    /// Attachment tags. Tags are stored per tag per frame; this is keyed by tag name to its
    /// per-frame transform (origin + 3x3 axis). Gameplay attaches weapons/effects to these sockets
    /// (<c>gettaginfo</c>/<c>setattachment</c>). Tag order and frame count are preserved.
    /// </summary>
    public IReadOnlyList<Md3Tag> Tags { get; init; } = Array.Empty<Md3Tag>();

    /// <summary>Renderable surfaces (meshes), each with its own vertex/triangle/shader data.</summary>
    public Md3Surface[] Surfaces { get; init; } = Array.Empty<Md3Surface>();

    /// <summary>The MD3 fixed-point vertex scale: stored int16 positions are multiplied by this.</summary>
    public const float VertexScale = 1.0f / 64.0f;

    /// <summary>
    /// Convenience lookup: tag name -> the tag's full per-frame transform list. Built from
    /// <see cref="Tags"/>. Names are matched ordinally (MD3 tag names are ASCII). If a model has
    /// duplicate tag names (unusual), the first wins.
    /// </summary>
    public IReadOnlyDictionary<string, Md3Tag> TagsByName { get; init; } =
        new Dictionary<string, Md3Tag>(StringComparer.Ordinal);
}

/// <summary>Per-frame bounding box / origin / radius and the frame's grab name (md3frameinfo_t).</summary>
public readonly record struct Md3Frame(Vector3 Mins, Vector3 Maxs, Vector3 Origin, float Radius, string Name);

/// <summary>
/// An attachment tag: a named coordinate frame that exists once per animation frame.
/// <see cref="Transforms"/> has one entry per model frame (length == <see cref="Md3Data.FrameCount"/>).
/// </summary>
public sealed class Md3Tag
{
    public string Name { get; init; } = string.Empty;

    /// <summary>One transform per model frame, in frame order.</summary>
    public Md3TagTransform[] Transforms { get; init; } = Array.Empty<Md3TagTransform>();
}

/// <summary>
/// A tag's pose for one frame: an origin plus a 3x3 orientation (the three basis axes as stored in the
/// file, row order forward/left/up per Quake convention). The host can compose these into a 4x4.
/// </summary>
public readonly record struct Md3TagTransform(Vector3 Origin, Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ);

/// <summary>
/// A renderable surface (md3 mesh). Triangles index into this surface's own vertex set. Positions and
/// normals are per-frame (vertex-morph): <see cref="FrameVertices"/> is a jagged array indexed
/// [frame][vertex]. Texcoords are shared across all frames.
/// </summary>
public sealed class Md3Surface
{
    public string Name { get; init; } = string.Empty;

    public int Flags { get; init; }

    /// <summary>Shader/material names referenced by this surface (usually one).</summary>
    public string[] Shaders { get; init; } = Array.Empty<string>();

    /// <summary>Number of vertices per frame.</summary>
    public int VertexCount { get; init; }

    /// <summary>Triangle indices into this surface's vertices (length = triangle count * 3).</summary>
    public int[] Triangles { get; init; } = Array.Empty<int>();

    /// <summary>Per-vertex texture coordinates (length = <see cref="VertexCount"/>), shared by all frames.</summary>
    public Vector2[] TexCoords { get; init; } = Array.Empty<Vector2>();

    /// <summary>
    /// Decoded per-frame vertices: <c>FrameVertices[frame][vertex]</c>. Length is [FrameCount][VertexCount].
    /// Positions are already scaled by <see cref="Md3Data.VertexScale"/>; normals are unit vectors
    /// decoded from the MD3 lat/long byte pair.
    /// </summary>
    public Md3Vertex[][] FrameVertices { get; init; } = Array.Empty<Md3Vertex[]>();
}

/// <summary>A single decoded morph-frame vertex: a position (Quake units) and a unit normal.</summary>
public readonly record struct Md3Vertex(Vector3 Position, Vector3 Normal);
