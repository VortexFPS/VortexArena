using System.Numerics;

namespace XonoticGodot.Formats.Iqm;

/// <summary>
/// Engine-neutral, Godot-free representation of an IQM ("INTERQUAKEMODEL", version 1 or 2) skeletal
/// model, parsed by <see cref="IqmReader"/>. IQM is Xonotic's animated-character format (players,
/// monsters, vehicles, view weapons): a single shared vertex buffer skinned to a bone hierarchy, with
/// animation expressed as per-frame bone-local transforms (translate + rotate + scale) rather than the
/// vertex-morph frames of MD3.
///
/// Coordinates are Quake units exactly as stored (no axis swap, no scale) — the Godot host applies any
/// conversion when it builds the <c>Skeleton3D</c>, <c>ArrayMesh</c> and <c>Animation</c> resources.
/// Quaternions are <c>(x, y, z, w)</c> as in <see cref="Quaternion"/>.
///
/// Ground truth: Darkplaces <c>model_iqm.h</c> (the on-disk structs) and <c>Mod_INTERQUAKEMODEL_Load</c>
/// in <c>model_alias.c</c> (vertex-array decode, base pose from joints, and the framedata bitmask +
/// channeloffset/channelscale pose decode that this mirrors).
/// </summary>
public sealed class IqmData
{
    /// <summary>File format version (1 or 2). Version 2 stores a full 4-component joint/pose quaternion;
    /// version 1 stores only the xyz of the quaternion and reconstructs w. Both are decoded here.</summary>
    public int Version { get; init; }

    /// <summary>Header <c>flags</c> field (currently unused by the format; preserved verbatim).</summary>
    public uint Flags { get; init; }

    /// <summary>Number of vertices in the shared vertex buffer (all the per-vertex arrays have this length).</summary>
    public int VertexCount { get; init; }

    // ----- Skeleton / base pose -----

    /// <summary>
    /// The bone hierarchy and its rest ("base") pose, in file order. <c>Joints[i].Parent</c> is an index
    /// into this array or -1 for a root, and is guaranteed &lt; i (IQM stores parents before children), so a
    /// single forward pass can resolve world-space rest transforms. Each joint carries its bone-LOCAL rest
    /// transform (relative to its parent).
    /// </summary>
    public IqmJoint[] Joints { get; init; } = Array.Empty<IqmJoint>();

    // ----- Geometry -----

    /// <summary>Renderable meshes. Each is a contiguous range of <see cref="Triangles"/> and of the vertex
    /// arrays; meshes share one global vertex/index buffer (they are not self-indexed like MD3 surfaces).</summary>
    public IqmMesh[] Meshes { get; init; } = Array.Empty<IqmMesh>();

    /// <summary>Triangle indices as a flat array (length = triangle count * 3) into the shared vertex
    /// arrays. These are GLOBAL vertex indices; a mesh's own range is
    /// <c>Triangles[mesh.FirstTriangle*3 .. (mesh.FirstTriangle+mesh.TriangleCount)*3]</c>.</summary>
    public int[] Triangles { get; init; } = Array.Empty<int>();

    /// <summary>Per-vertex positions (IQM_POSITION, float3). Always present. Length = <see cref="VertexCount"/>.</summary>
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();

    /// <summary>Per-vertex texture coordinates (IQM_TEXCOORD, float2). Always present. Length = <see cref="VertexCount"/>.</summary>
    public Vector2[] TexCoords { get; init; } = Array.Empty<Vector2>();

    /// <summary>Per-vertex normals (IQM_NORMAL, float3), or <c>null</c> if the file omits them
    /// (the host must then synthesize normals from the triangles).</summary>
    public Vector3[]? Normals { get; init; }

    /// <summary>Per-vertex tangents (IQM_TANGENT, float4: xyz tangent, w handedness/bitangent sign),
    /// or <c>null</c> if absent.</summary>
    public Vector4[]? Tangents { get; init; }

    /// <summary>
    /// Per-vertex skinning bone indices (IQM_BLENDINDEXES, ubyte4) into <see cref="Joints"/>, FLAT: vertex
    /// <c>v</c>'s 4 influencing bone indices are <c>[v*4 .. v*4+3]</c>. <c>null</c> if the model is unskinned
    /// (no bones / no animation). Length = <see cref="VertexCount"/> * 4. (Flat rather than jagged — one
    /// <c>byte[4]</c> object per vertex was tens of thousands of tiny GC objects per player model, a
    /// measurable slice of the bot-join allocation storm.)
    /// </summary>
    public byte[]? BlendIndexes { get; init; }

    /// <summary>
    /// Per-vertex skinning weights matching <see cref="BlendIndexes"/> (IQM_BLENDWEIGHTS), same FLAT
    /// <c>v*4</c> layout. Weights are normalized 0..255 (a vertex's 4 weights sum to ~255). <c>null</c> if
    /// unskinned. Length = <see cref="VertexCount"/> * 4. (IQM also permits float4 weights; if the file used
    /// that format it is converted to the 0..255 byte scale here so consumers have one representation.)
    /// </summary>
    public byte[]? BlendWeights { get; init; }

    /// <summary>Per-vertex vertex colors (IQM_COLOR) as RGBA 0..1, or <c>null</c> if absent. Both the
    /// float4 and ubyte4 on-disk formats are decoded to this normalized form.</summary>
    public Vector4[]? Colors { get; init; }

    // ----- Animation -----

    /// <summary>Named animation clips. Each references a contiguous range of <see cref="Frames"/> via
    /// <see cref="IqmAnim.FirstFrame"/>/<see cref="IqmAnim.FrameCount"/>.</summary>
    public IqmAnim[] Anims { get; init; } = Array.Empty<IqmAnim>();

    /// <summary>
    /// Decoded animation frames. <c>Frames[f].Bones[b]</c> is the bone-LOCAL transform (relative to the
    /// bone's parent, same space as <see cref="IqmJoint.Local"/>) of bone <c>b</c> at frame <c>f</c>. The
    /// number of bones per frame equals <c>Poses.Length</c>, which normally equals <see cref="Joints"/>
    /// length. Empty if the file has no frames (a static model); use the base pose from
    /// <see cref="Joints"/> in that case.
    /// </summary>
    public IqmFrame[] Frames { get; init; } = Array.Empty<IqmFrame>();

    /// <summary>The pose channel descriptors (one per animated bone), parsed but exposed mainly for
    /// debugging/tooling; the per-frame values are already decoded into <see cref="Frames"/>.</summary>
    public IqmPose[] Poses { get; init; } = Array.Empty<IqmPose>();

    /// <summary>Per-frame bounding boxes (IQM bounds lump), or <c>null</c> if the file omits them.
    /// Length = <see cref="Frames"/> length when present.</summary>
    public IqmBounds[]? Bounds { get; init; }

    /// <summary>The raw comment blob (IQM comment lump), if any; otherwise empty.</summary>
    public string Comment { get; init; } = string.Empty;
}

/// <summary>
/// A bone in the skeleton with its rest (base-pose) transform. <see cref="Parent"/> indexes
/// <see cref="IqmData.Joints"/> (or -1 for a root) and is always less than this joint's own index.
/// The transform is bone-LOCAL: it positions the bone relative to its parent. The local-to-world rest
/// matrix is <c>world(parent) * Local</c> (identity parent for roots).
/// </summary>
public readonly record struct IqmJoint(
    string Name,
    int Parent,
    Vector3 Translate,
    Quaternion Rotate,
    Vector3 Scale)
{
    /// <summary>The bone-local rest transform as a single value (translate + rotate + scale). Identical in
    /// meaning to a per-frame <see cref="IqmBonePose"/>, so base pose and animated pose interoperate.</summary>
    public IqmBonePose Local => new(Translate, Rotate, Scale);
}

/// <summary>A renderable mesh: a name, a material/shader name, and contiguous ranges into the model's
/// shared vertex arrays and triangle list.</summary>
public readonly record struct IqmMesh(
    string Name,
    string Material,
    int FirstVertex,
    int VertexCount,
    int FirstTriangle,
    int TriangleCount);

/// <summary>An animation clip: a name plus a span of frames and its playback rate / loop flag.</summary>
public readonly record struct IqmAnim(
    string Name,
    int FirstFrame,
    int FrameCount,
    float FrameRate,
    bool Loop);

/// <summary>
/// A single decoded animation frame: one bone-local transform per animated bone, in bone order
/// (matching <see cref="IqmData.Poses"/> and, normally, <see cref="IqmData.Joints"/>).
/// </summary>
public sealed class IqmFrame
{
    /// <summary>Bone-local transforms for this frame, indexed by bone. Same space as <see cref="IqmJoint.Local"/>.</summary>
    public IqmBonePose[] Bones { get; init; } = Array.Empty<IqmBonePose>();
}

/// <summary>
/// A bone-local transform: a translation, a unit rotation quaternion <c>(x, y, z, w)</c>, and a scale.
/// This is the building block for both the rest pose and every animation frame. Compose to a 4x4 as
/// <c>T(Translate) * R(Rotate) * S(Scale)</c> (TRS), then chain through parents for world space.
/// </summary>
public readonly record struct IqmBonePose(Vector3 Translate, Quaternion Rotate, Vector3 Scale);

/// <summary>
/// The on-disk pose descriptor for one animated bone: a parent index plus, for each of the 10 logical
/// channels (3 translate, 4 rotate, 3 scale), whether that channel varies per frame
/// (<see cref="ChannelMask"/> bit) and its decode parameters (<c>value = offset + raw*scale</c>).
/// Version-1 files use only 9 channels (the rotation w is reconstructed); the mask/arrays are normalized
/// here to the 10-channel layout. Mostly informational — frame values are pre-decoded into
/// <see cref="IqmData.Frames"/>.
/// </summary>
public sealed class IqmPose
{
    public int Parent { get; init; }

    /// <summary>Bitmask over the 10 channels (bit c set =&gt; channel c is animated and consumes one ushort
    /// per frame from the framedata stream).</summary>
    public uint ChannelMask { get; init; }

    /// <summary>Per-channel base value (length 10): channels 0-2 translate xyz, 3-6 rotate xyzw, 7-9 scale xyz.</summary>
    public float[] ChannelOffset { get; init; } = new float[10];

    /// <summary>Per-channel multiplier for the raw ushort (length 10), same channel layout as <see cref="ChannelOffset"/>.</summary>
    public float[] ChannelScale { get; init; } = new float[10];
}

/// <summary>A per-frame axis-aligned bounding box plus the two radii from the IQM bounds lump.</summary>
public readonly record struct IqmBounds(Vector3 Mins, Vector3 Maxs, float XyRadius, float Radius);
