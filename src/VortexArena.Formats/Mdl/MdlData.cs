using System.Numerics;

namespace VortexArena.Formats.Mdl;

/// <summary>
/// Engine-neutral, Godot-free representation of a Quake1 MDL ("IDPO", <see cref="MdlReader.Version"/> 6)
/// alias model, parsed by <see cref="MdlReader"/>. MDL is a vertex-morph format like MD3: geometry is a
/// stack of per-pose byte-quantized vertex positions (scaled by the header <c>scale</c>/<c>origin</c>),
/// embedded 8-bit palettised skins, and a shared texcoord/triangle set.
///
/// <para>Xonotic ships a handful of these as legacy props — the shotgun shell casing
/// (<c>models/casing_shell.mdl</c> / <c>casing_steel.mdl</c>) and the fast gib chunk
/// (<c>models/gibs/chunk.mdl</c>); all are static single-frame meshes. A server that hosts Quake 1 content
/// inside Xonotic sends hundreds of animated ones (monsters, items, projectiles), with skin groups, frame
/// groups and model flags.</para>
///
/// <para><b>Frames and poses.</b> What QuakeC calls a model's frame (<c>.frame</c>) is an entry of
/// <see cref="Scenes"/>: one pose, or a group of poses the engine plays by itself at the group's rate
/// (a torch's flame). <see cref="Frames"/> is the flat list of poses those scenes index, exactly DarkPlaces'
/// <c>animscenes</c> / <c>num_poses</c>. The same holds for <see cref="SkinScenes"/> (<c>.skin</c>) and
/// <see cref="Skins"/>.</para>
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
    /// The first skin decoded to opaque RGBA8 (<c>SkinWidth * SkinHeight * 4</c> bytes) through the built-in
    /// Quake palette — DarkPlaces' <c>palette_bgra_complete</c>. Empty when the model ships no skin (rare);
    /// the builder then uses a flat material. A host that honours <c>gfx/palette.lmp</c>, full-bright
    /// colours or a colormap converts <see cref="Skins"/> itself
    /// (<see cref="VortexArena.Formats.Images.QuakePalette"/>).
    /// </summary>
    public byte[] SkinRgba { get; init; } = System.Array.Empty<byte>();

    /// <summary>
    /// Every skin picture in file order, groups flattened (DarkPlaces' <c>totalskins</c>): raw 8-bit palette
    /// indices, <c>SkinWidth * SkinHeight</c> bytes each, row-major from the top.
    /// </summary>
    public byte[][] Skins { get; init; } = System.Array.Empty<byte[]>();

    /// <summary>
    /// One entry per header skin (<c>numskins</c>; what <c>.skin</c> selects): a run of <see cref="Skins"/>.
    /// A single skin is a run of one at 10 per second; a skin group cycles through its pictures at
    /// <c>1 / interval</c> of its FIRST interval (DarkPlaces ignores the others), an interval below 0.01
    /// counting as 0.1. Named "skin N".
    /// </summary>
    public MdlScene[] SkinScenes { get; init; } = System.Array.Empty<MdlScene>();

    /// <summary>The header's <c>flags</c> as stored (<see cref="MdlFlags"/>): trails, rotation.</summary>
    public int Flags { get; init; }

    /// <summary>
    /// DarkPlaces' <c>model_t.effects</c>, which it ORs into the effects of every entity that shows the model:
    /// the low eight flag bits moved to the top byte (<c>MF_ROCKET</c> becomes <c>EF_ROCKET</c> …) and bits
    /// 8..23 kept where they are (<c>EF_FULLBRIGHT</c> 512, <c>EF_NOSHADOW</c> 4096, <c>EF_DOUBLESIDED</c>
    /// 32768 …).
    /// </summary>
    public uint Effects => MdlFlags.ToEffects(Flags);

    /// <summary>The header's <c>synctype</c>: 0 synchronised, 1 random (unused by DarkPlaces' renderer).</summary>
    public int SyncType { get; init; }

    /// <summary>Number of model vertices in the file (header <c>numverts</c>); each pose stores this many.</summary>
    public int VertexCount { get; init; }

    /// <summary>
    /// Render-ready triangle corners (length = triangle count * 3, one per triangle vertex). Each carries the
    /// index of the model vertex it draws (into a pose's <see cref="MdlFrame.Vertices"/>) and its final,
    /// seam-resolved UV. MDL stores texcoords per vertex but a vertex on the skin seam gets a different U on
    /// back-facing triangles (DP's <c>vertonseam</c>/<c>facesfront</c> "butcher" step).
    /// </summary>
    public MdlCorner[] Corners { get; init; } = System.Array.Empty<MdlCorner>();

    /// <summary>
    /// The same triangles as DarkPlaces keeps them: indices (three per triangle) into the compacted vertex
    /// set of <see cref="MeshVertices"/> / <see cref="MeshTexCoords"/>.
    /// </summary>
    public int[] MeshElements { get; init; } = System.Array.Empty<int>();

    /// <summary>
    /// DarkPlaces' vertex set (<c>surfmesh.num_vertices</c>): each file vertex that some triangle uses, then
    /// each seam vertex a back-facing triangle uses (its copy with U + 0.5), unused ones dropped. The value is
    /// the file vertex to take the position from (an index into <see cref="MdlFrame.Vertices"/>).
    /// </summary>
    public int[] MeshVertices { get; init; } = System.Array.Empty<int>();

    /// <summary>Texture coordinates of <see cref="MeshVertices"/> (0..1, or up to 1.5 for a seam copy).</summary>
    public Vector2[] MeshTexCoords { get; init; } = System.Array.Empty<Vector2>();

    /// <summary>The poses, groups flattened, in file order (at least one).</summary>
    public MdlFrame[] Frames { get; init; } = System.Array.Empty<MdlFrame>();

    /// <summary>
    /// One entry per header frame (<c>numframes</c>; what <c>.frame</c> selects): a run of <see cref="Frames"/>.
    /// A single frame is a run of one at 10 per second; a frame group plays at <c>1 / interval</c> of its FIRST
    /// interval, an interval below 0.01 counting as 0.1. All loop. Named after the run's first pose.
    /// </summary>
    public MdlScene[] Scenes { get; init; } = System.Array.Empty<MdlScene>();

    /// <summary>
    /// DarkPlaces' <c>normalmins</c>/<c>normalmaxs</c> (<c>Mod_Alias_CalculateBoundingBox</c>): the box of
    /// every vertex a triangle uses, in every pose. Vertices no triangle uses do not count.
    /// </summary>
    public Vector3 Mins { get; init; }
    public Vector3 Maxs { get; init; }

    /// <summary>The largest horizontal distance of such a vertex from the origin (<c>yawmins</c>/<c>yawmaxs</c>).</summary>
    public float YawRadius { get; init; }

    /// <summary>The largest distance of such a vertex from the origin (<c>radius</c>, <c>rotatedmins</c>/<c>rotatedmaxs</c>).</summary>
    public float Radius { get; init; }

    /// <summary>DarkPlaces' <c>surfmesh.isanimated</c>: some pose differs from the first in a used vertex.</summary>
    public bool IsAnimated { get; init; }
}

/// <summary>
/// An <c>animscene_t</c>: a named run of poses (or of skin pictures) shown at a rate.
/// </summary>
public readonly record struct MdlScene(string Name, int First, int Count, float FrameRate, bool Loop);

/// <summary>One triangle corner: the model-vertex index it draws and its seam-resolved texture UV (0..1).</summary>
public readonly record struct MdlCorner(int Vertex, Vector2 Uv);

/// <summary>One pose: its grab name and the decoded per-vertex positions + normals (Quake units).</summary>
public sealed class MdlFrame
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Decoded vertices (length == <see cref="MdlData.VertexCount"/>). Indexed by <see cref="MdlCorner.Vertex"/>.</summary>
    public MdlVertex[] Vertices { get; init; } = System.Array.Empty<MdlVertex>();
}

/// <summary>A decoded frame vertex: position (Quake units) and a unit normal (from the Quake anorms table).</summary>
public readonly record struct MdlVertex(Vector3 Position, Vector3 Normal);

/// <summary>
/// The model flags of a Quake <c>.mdl</c> header (<c>MF_*</c>, DarkPlaces <c>protocol.h</c>) and the entity
/// effects DarkPlaces turns them into (<c>EF_*</c>).
/// </summary>
public static class MdlFlags
{
    /// <summary>Leave a rocket trail (<c>TR_ROCKET</c>).</summary>
    public const int Rocket = 1;
    /// <summary>Leave a grenade smoke trail (<c>TR_GRENADE</c>).</summary>
    public const int Grenade = 2;
    /// <summary>Leave a blood trail (<c>TR_BLOOD</c>).</summary>
    public const int Gib = 4;
    /// <summary>Spin about the vertical axis, 100 degrees a second (bonus items).</summary>
    public const int Rotate = 8;
    /// <summary>Green split trail (<c>TR_WIZSPIKE</c>).</summary>
    public const int Tracer = 16;
    /// <summary>Small blood trail (<c>TR_SLIGHTBLOOD</c>).</summary>
    public const int ZomGib = 32;
    /// <summary>Orange split trail (<c>TR_KNIGHTSPIKE</c>).</summary>
    public const int Tracer2 = 64;
    /// <summary>Purple trail (<c>TR_VORESPIKE</c>).</summary>
    public const int Tracer3 = 128;
    /// <summary>
    /// QuakeSpasm/FTE's <c>MF_HOLEY</c> (index 255 of the skin is a hole). DarkPlaces does not know it: the
    /// bit lands on <c>EF_SELECTABLE</c>, which does nothing here, and the skin is drawn opaque.
    /// </summary>
    public const int Holey = 0x4000;

    public const uint EfRocket = 1u << 24, EfGrenade = 1u << 25, EfGib = 1u << 26, EfRotate = 1u << 27,
        EfTracer = 1u << 28, EfZomGib = 1u << 29, EfTracer2 = 1u << 30, EfTracer3 = 1u << 31;

    /// <summary><c>EF_NOMODELFLAGS</c>: an entity with this effect ignores its model's.</summary>
    public const uint EfNoModelFlags = 1u << 23;

    /// <summary>
    /// The bits of an entity's own effects that stop DarkPlaces ORing in the model's
    /// (<c>CL_UpdateNetworkEntity</c>: "EF_NOMODELFLAGS plus all the higher EF_ flags such as EF_ROCKET").
    /// </summary>
    public const uint EntityOverrideMask = 0xFF800000;

    /// <summary>
    /// <c>Mod_IDP0_Load</c>: <c>effects = ((flags &amp; 255) &lt;&lt; 24) | (flags &amp; 0x00FFFF00)</c>.
    /// </summary>
    public static uint ToEffects(int flags) => (((uint)flags & 255u) << 24) | ((uint)flags & 0x00FFFF00u);

    /// <summary>
    /// The trail a set of entity effects asks for, by DarkPlaces' order of precedence
    /// (<c>CL_UpdateNetworkEntityTrail</c>): the name of the effect in <c>effectinfo.txt</c>, or null.
    /// <paramref name="alphaIsMinusOne"/> is the Nehahra cigar-smoke case of a grenade trail.
    /// </summary>
    public static string? TrailEffect(uint effects, bool alphaIsMinusOne = false)
    {
        if ((effects & EfGib) != 0) return "TR_BLOOD";
        if ((effects & EfZomGib) != 0) return "TR_SLIGHTBLOOD";
        if ((effects & EfTracer) != 0) return "TR_WIZSPIKE";
        if ((effects & EfTracer2) != 0) return "TR_KNIGHTSPIKE";
        if ((effects & EfRocket) != 0) return "TR_ROCKET";
        if ((effects & EfGrenade) != 0) return alphaIsMinusOne ? "TR_NEHAHRASMOKE" : "TR_GRENADE";
        if ((effects & EfTracer3) != 0) return "TR_VORESPIKE";
        return null;
    }

    /// <summary>
    /// What an entity showing a model with <paramref name="modelEffects"/> has for effects
    /// (<c>CL_UpdateNetworkEntity</c>): the model's are added unless the entity's own already carry one of
    /// the top nine bits. (A CSQC entity gets them unconditionally: <c>CSQC_AddRenderEdict</c>.)
    /// </summary>
    public static uint CombineNetworkEffects(uint entityEffects, uint modelEffects) =>
        (entityEffects & EntityOverrideMask) != 0 ? entityEffects : entityEffects | modelEffects;

    /// <summary><c>EF_ROTATE</c>'s yaw at a client time: <c>ANGLEMOD(100 * time)</c>, in degrees, 0..360.</summary>
    public static float RotateYaw(double time)
    {
        // ANGLEMOD: (360.0/65536) * ((int)(a*(65536/360.0)) & 65535)
        double a = 100.0 * time;
        return (float)(360.0 / 65536 * ((long)(a * (65536 / 360.0)) & 65535));
    }
}
