using System.Numerics;

namespace XonoticGodot.Formats.Dpm;

/// <summary>
/// Engine-neutral, Godot-free representation of a DarkPlaces Model (DPM, id
/// "DARKPLACESMODEL\0", type 2), parsed by <see cref="DpmReader"/>. DPM is a <b>skeletal</b> format:
/// geometry is stored once as bone-relative, weighted vertices (the bind/base pose), and animation is
/// a per-frame array of bone poses (a 3x4 matrix per bone). This is fundamentally different from MD3's
/// vertex-morph layout.
///
/// Coordinates are Quake units, exactly as stored (no axis swap). All multi-byte values on disk are
/// <b>big-endian</b> and have already been decoded to native here.
///
/// The Godot host turns <see cref="Bones"/> + <see cref="Frames"/> into a <c>Skeleton3D</c> +
/// <c>Animation</c>, and each <see cref="DpmMesh"/> into a skinned <c>ArrayMesh</c>; the parser does no
/// skinning itself (see remarks on <see cref="DpmMesh"/>).
///
/// Ground truth: DarkPlaces <c>model_dpmodel.h</c> (structs) and <c>Mod_DARKPLACESMODEL_Load</c> in
/// <c>model_alias.c</c> (read order + byteswap).
/// </summary>
public sealed class DpmData
{
    /// <summary>Model bounding box minimum (header mins[3]), Quake units.</summary>
    public Vector3 Mins { get; init; }

    /// <summary>Model bounding box maximum (header maxs[3]), Quake units.</summary>
    public Vector3 Maxs { get; init; }

    /// <summary>Radius of the model about the Z (yaw) axis (header yawradius).</summary>
    public float YawRadius { get; init; }

    /// <summary>Radius of the model about its origin in any rotation (header allradius).</summary>
    public float AllRadius { get; init; }

    /// <summary>
    /// The skeleton, in file order. A bone's <see cref="DpmBone.Parent"/> is an index into this array
    /// (or -1 for a root). The format guarantees a parent's index is strictly less than its child's,
    /// so a single forward pass composes world matrices.
    /// </summary>
    public DpmBone[] Bones { get; init; } = Array.Empty<DpmBone>();

    /// <summary>Renderable meshes, each with its own shader, weighted vertices, texcoords and indices.</summary>
    public DpmMesh[] Meshes { get; init; } = Array.Empty<DpmMesh>();

    /// <summary>
    /// Animation frames. Each frame carries its own bounds and a pose for every bone
    /// (<see cref="DpmFrame.BonePoses"/> has length <see cref="Bones"/>.Length). Frame 0 is the base
    /// pose the skeleton was authored in.
    /// </summary>
    public DpmFrame[] Frames { get; init; } = Array.Empty<DpmFrame>();
}

/// <summary>
/// One skeleton bone (dpmbone_t): a name, the index of its parent bone (-1 = root, parents always come
/// first), and DPM bone flags. <see cref="DpmBone.AttachmentFlag"/> (bit 0, DPMBONEFLAG_ATTACHMENT)
/// marks bones used as attachment points; gameplay looks bones up by name for attachments.
/// </summary>
public readonly record struct DpmBone(string Name, int Parent, uint Flags)
{
    /// <summary>DPMBONEFLAG_ATTACHMENT — the bone is an attachment socket and must not be culled.</summary>
    public const uint AttachmentFlag = 1;

    /// <summary>True if this bone is flagged as an attachment point.</summary>
    public bool IsAttachment => (Flags & AttachmentFlag) != 0;
}

/// <summary>
/// A renderable DPM mesh (dpmmesh_t). Vertices are bone-relative and weighted: each entry in
/// <see cref="Vertices"/> carries one or more <see cref="DpmBoneWeight"/>s. Triangles index into this
/// mesh's own vertex set.
///
/// <para><b>The parser does not skin.</b> To produce a posed vertex the host must, for a given set of
/// world-space bone matrices <c>M[bone]</c> (composed from <see cref="DpmData.Bones"/> hierarchy and a
/// frame's <see cref="DpmFrame.BonePoses"/>), evaluate per vertex:</para>
/// <code>
/// posedPos    = sum over weights w of  M[w.BoneNum] * (w.Origin, 1)  * w.Influence
/// posedNormal = sum over weights w of  M[w.BoneNum].RotationOnly * w.Normal * w.Influence
/// </code>
/// The influences of a vertex's weights sum to 1, so this is a convex blend. (DarkPlaces folds the
/// influence into the matrix-vector add directly; mathematically identical.)
/// </summary>
public sealed class DpmMesh
{
    /// <summary>
    /// The mesh's shader name (dpmmesh_t.shadername, char[32]). DPM meshes have no separate section or
    /// texture name; DarkPlaces reuses this single field as both the shader and the material/section
    /// name, so <see cref="TextureName"/> mirrors it for the host's convenience.
    /// </summary>
    public string ShaderName { get; init; } = string.Empty;

    /// <summary>
    /// Alias of <see cref="ShaderName"/>. The on-disk struct has only one name field; this exists so a
    /// builder that wants a "texture name" has it without special-casing DPM.
    /// </summary>
    public string TextureName => ShaderName;

    /// <summary>
    /// Per-vertex skinning data. Each vertex holds a variable-length list of weighted bone influences
    /// (the on-disk <c>dpmvertex_t.numbones</c> count). Length == the mesh's vertex count and matches
    /// the index space of <see cref="TexCoords"/> and <see cref="Triangles"/>.
    /// </summary>
    public DpmVertex[] Vertices { get; init; } = Array.Empty<DpmVertex>();

    /// <summary>Per-vertex texture coordinates (st), one per entry in <see cref="Vertices"/>.</summary>
    public Vector2[] TexCoords { get; init; } = Array.Empty<Vector2>();

    /// <summary>
    /// Triangle indices into <see cref="Vertices"/>, length = triangle count * 3, stored exactly as on
    /// disk (no winding flip). DPM uses counter-clockwise front faces; DarkPlaces reverses the order
    /// when emitting to its own clockwise convention. The Godot builder should pick the winding its
    /// pipeline expects (Godot is CCW-front like DPM, so the raw order is usually fine).
    /// </summary>
    public int[] Triangles { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Per-triangle group ids (dpmmesh_t ofs_groupids; one uint per triangle). Gameplay-defined values
    /// (e.g. hit-region tagging); preserved verbatim. Length = triangle count.
    /// </summary>
    public uint[] GroupIds { get; init; } = Array.Empty<uint>();
}

/// <summary>
/// A single mesh vertex as stored in DPM (dpmvertex_t): a variable-length set of weighted bone
/// influences. The blended bind-pose position/normal is the influence-weighted sum of each weight's
/// bone-relative <see cref="DpmBoneWeight.Origin"/>/<see cref="DpmBoneWeight.Normal"/> transformed by
/// that bone's matrix. Weight influences sum to 1.
/// </summary>
public sealed class DpmVertex
{
    /// <summary>One or more weighted bone influences (dpmbonevert_t[numbones]); never empty in valid files.</summary>
    public DpmBoneWeight[] Weights { get; init; } = Array.Empty<DpmBoneWeight>();
}

/// <summary>
/// A single bone influence on a vertex (dpmbonevert_t): the vertex position/normal expressed in the
/// space of bone <see cref="BoneNum"/>, plus the fraction of this bone's contribution. The on-disk
/// layout pairs (origin, influence) and (normal, bonenum) for SIMD; here they are plain fields.
/// </summary>
public readonly record struct DpmBoneWeight(int BoneNum, Vector3 Origin, float Influence, Vector3 Normal);

/// <summary>
/// One animation frame (dpmframe_t): a name, its own bounding info, and a full set of bone poses
/// (<see cref="BonePoses"/> length == bone count, in bone order). Frame 0 is the base/bind pose.
/// </summary>
public sealed class DpmFrame
{
    public string Name { get; init; } = string.Empty;

    public Vector3 Mins { get; init; }

    public Vector3 Maxs { get; init; }

    public float YawRadius { get; init; }

    public float AllRadius { get; init; }

    /// <summary>One <see cref="DpmBonePose"/> per bone, in the same order as <see cref="DpmData.Bones"/>.</summary>
    public DpmBonePose[] BonePoses { get; init; } = Array.Empty<DpmBonePose>();
}

/// <summary>
/// A bone's pose for one frame: the DPM 3x4 matrix (dpmbonepose_t, <c>float matrix[3][4]</c>) split
/// into its three basis columns plus the translation, all in the bone's <i>parent-relative</i> space.
///
/// <para>On disk the 12 floats are row-major: row r = (matrix[r][0..3]). The first three columns are the
/// rotation/scale basis and the fourth column is the translation, so a point <c>v</c> maps as:</para>
/// <code>
/// out.x = v.x*Right.X + v.y*Up.X + v.z*Forward.X + Origin.X   // row 0 = (Right.X, Up.X, Forward.X, Origin.X)
/// out.y = v.x*Right.Y + v.y*Up.Y + v.z*Forward.Y + Origin.Y   // row 1
/// out.z = v.x*Right.Z + v.y*Up.Z + v.z*Forward.Z + Origin.Z   // row 2
/// </code>
/// i.e. <see cref="Right"/>/<see cref="Up"/>/<see cref="Forward"/> are the matrix <b>columns</b> (the
/// images of the basis vectors), and <see cref="Origin"/> is the translation column. Use
/// <see cref="ToMatrix"/> to get a <see cref="Matrix4x4"/>.
/// </summary>
public readonly record struct DpmBonePose(Vector3 Right, Vector3 Up, Vector3 Forward, Vector3 Origin)
{
    /// <summary>
    /// Builds a <see cref="Matrix4x4"/> from this pose. The returned matrix uses
    /// <see cref="System.Numerics"/>' row-vector convention so that
    /// <c>Vector3.Transform(v, ToMatrix())</c> reproduces the DPM transform above (basis vectors in the
    /// matrix rows M11/M12/M13 etc., translation in M41/M42/M43).
    /// </summary>
    public Matrix4x4 ToMatrix() => new(
        Right.X, Right.Y, Right.Z, 0f,
        Up.X, Up.Y, Up.Z, 0f,
        Forward.X, Forward.Y, Forward.Z, 0f,
        Origin.X, Origin.Y, Origin.Z, 1f);
}
