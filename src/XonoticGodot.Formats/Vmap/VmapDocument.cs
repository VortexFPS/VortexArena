using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// The in-memory TRUTH model of a Vortex Arena map (<c>.vmap</c>): convex brushes, bezier patches and
/// entities, in Quake units and Quake coordinates exactly like <see cref="Bsp.BspData"/> (no axis swap,
/// no scale — the Godot host converts when it builds meshes).
///
/// This is the editable representation AND the shipping representation (planning/procedural-map-decoration.html
/// §11.2): everything here is authored/edited data. Render meshes, collision, PVS and lighting are DERIVED
/// from it (the <c>bake/</c> cache) and are always regenerable — see <see cref="VmapGeometryBuilder"/>.
///
/// Brush geometry follows the Quake convention throughout: a brush is the INTERSECTION of its faces'
/// half-spaces, each face plane has an OUTWARD normal, and the solid interior is <c>Dot(Normal, p) &lt;= Dist</c>
/// (identical to the engine's <c>BrushPlane</c>, so collision building is a straight transcription).
/// </summary>
public sealed class VmapDocument
{
    /// <summary>Current on-disk format version written by <see cref="VmapWriter"/>.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Format version this document was loaded from (or <see cref="CurrentFormatVersion"/> when built in memory).</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Manifest: identity, provenance and environment settings.</summary>
    public VmapManifest Manifest { get; set; } = new();

    /// <summary>Every convex brush in the map, world brushes and brush-entity brushes alike.</summary>
    public List<VmapBrush> Brushes { get; } = new();

    /// <summary>Bezier patch meshes (Q3 patchDef2/patchDef3 and BSP <c>BspFaceType.Patch</c> faces).</summary>
    public List<VmapPatch> Patches { get; } = new();

    /// <summary>
    /// Entities, including <c>worldspawn</c> at index 0 by convention. A brush entity (func_door, trigger_*)
    /// owns brushes/patches through <see cref="VmapEntity.BrushIds"/> / <see cref="VmapEntity.PatchIds"/>;
    /// brushes not claimed by any entity belong to worldspawn.
    /// </summary>
    public List<VmapEntity> Entities { get; } = new();

    /// <summary>Look up a brush by its stable <see cref="VmapBrush.Id"/> (ids survive edits; list order does not).</summary>
    public VmapBrush? FindBrush(int id)
    {
        for (int i = 0; i < Brushes.Count; i++)
            if (Brushes[i].Id == id)
                return Brushes[i];
        return null;
    }

    /// <summary>The next unused brush id — allocation point for editor ops that create geometry.</summary>
    public int NextBrushId()
    {
        int max = 0;
        for (int i = 0; i < Brushes.Count; i++)
            if (Brushes[i].Id > max)
                max = Brushes[i].Id;
        return max + 1;
    }

    /// <summary>The worldspawn entity (classname <c>worldspawn</c>), or null if the map has none.</summary>
    public VmapEntity? Worldspawn()
    {
        for (int i = 0; i < Entities.Count; i++)
            if (string.Equals(Entities[i].ClassName, "worldspawn", StringComparison.OrdinalIgnoreCase))
                return Entities[i];
        return null;
    }
}

/// <summary>Map-level identity, provenance and environment settings (the <c>map.json</c> manifest).</summary>
public sealed class VmapManifest
{
    /// <summary>Short map name (e.g. "catharsis") — the name used to load it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form display title; falls back to <see cref="Name"/> when empty.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>What this map was imported from ("bsp", "map", or "" when authored natively).</summary>
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>Virtual path of the import source, for provenance (e.g. "maps/catharsis.bsp").</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the import source bytes, so a re-import can be detected as redundant and so a bake cache can be
    /// keyed against the geometry it was built from. Hex, lower-case; empty when authored natively.
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;
}

/// <summary>
/// A plane in Hessian normal form: the point set where <c>Dot(Normal, p) == Dist</c>.
/// For a brush face the normal points OUTWARD, so the brush interior is <c>Dot(Normal, p) &lt;= Dist</c>.
/// </summary>
public readonly record struct VmapPlane(Vector3 Normal, float Dist)
{
    /// <summary>Signed distance of <paramref name="p"/> from the plane (positive = in front / outside).</summary>
    public float Distance(Vector3 p) => Vector3.Dot(Normal, p) - Dist;

    /// <summary>
    /// The plane through three points in Quake winding order (clockwise seen from the FRONT of the plane),
    /// matching q3map2's <c>PlaneFromPoints</c>: <c>normal = cross(p0 - p1, p2 - p1)</c>, <c>dist = dot(p0, normal)</c>.
    /// Returns false when the points are collinear/degenerate.
    /// </summary>
    public static bool TryFromPoints(Vector3 p0, Vector3 p1, Vector3 p2, out VmapPlane plane)
    {
        Vector3 n = Vector3.Cross(p0 - p1, p2 - p1);
        float len = n.Length();
        if (len < 1e-6f)
        {
            plane = default;
            return false;
        }
        n /= len;
        plane = new VmapPlane(n, Vector3.Dot(p0, n));
        return true;
    }
}

/// <summary>
/// How a material projects onto a face, in the canonical form <c>u = Dot(p, AxisU) + OffsetU</c> (and likewise
/// for v), with UV measured in TEXTURE REPEATS — so <see cref="AxisU"/>'s magnitude is "repeats per world unit"
/// and no texture pixel size is needed to evaluate it.
///
/// Importers normalize into this form: the BSP importer FITS it from the compiled face's vertex UVs
/// (<see cref="BspToVmap"/>), and the .map importer converts Radiant's texdef (which is expressed in texels,
/// hence needs the texture's pixel size) in <see cref="MapSourceReader"/>.
/// </summary>
public struct VmapTexProjection
{
    /// <summary>World-space gradient of the U coordinate, in texture repeats per world unit.</summary>
    public Vector3 AxisU;

    /// <summary>World-space gradient of the V coordinate, in texture repeats per world unit.</summary>
    public Vector3 AxisV;

    /// <summary>Constant term of U (texture repeats).</summary>
    public float OffsetU;

    /// <summary>Constant term of V (texture repeats).</summary>
    public float OffsetV;

    public VmapTexProjection(Vector3 axisU, Vector3 axisV, float offsetU, float offsetV)
    {
        AxisU = axisU;
        AxisV = axisV;
        OffsetU = offsetU;
        OffsetV = offsetV;
    }

    /// <summary>Evaluate the texture coordinate of a world-space point.</summary>
    public readonly Vector2 Evaluate(Vector3 p)
        => new(Vector3.Dot(p, AxisU) + OffsetU, Vector3.Dot(p, AxisV) + OffsetV);

    /// <summary>True when neither axis is degenerate (a zero axis collapses the texture to a line).</summary>
    public readonly bool IsValid => AxisU.LengthSquared() > 1e-20f && AxisV.LengthSquared() > 1e-20f;

    /// <summary>
    /// The default axis-aligned projection for a face with the given normal, at <paramref name="repeatsPerUnit"/>
    /// repeats per world unit — the Quake "dominant axis" box mapping (idTech's <c>TextureAxisFromPlane</c>).
    /// Used when no better projection is known (bare geometry, a failed fit, §5.3's auto-texturing fallback).
    /// </summary>
    public static VmapTexProjection AxialFor(Vector3 normal, float repeatsPerUnit = 1f / 64f)
    {
        // Pick the dominant axis of the normal; the two remaining axes become U and V (idTech baseaxis table).
        float ax = MathF.Abs(normal.X), ay = MathF.Abs(normal.Y), az = MathF.Abs(normal.Z);
        Vector3 u, v;
        if (az >= ax && az >= ay)          { u = new Vector3(1, 0, 0);  v = new Vector3(0, -1, 0); } // floor/ceiling
        else if (ax >= ay)                 { u = new Vector3(0, 1, 0);  v = new Vector3(0, 0, -1); } // east/west wall
        else                               { u = new Vector3(1, 0, 0);  v = new Vector3(0, 0, -1); } // north/south wall
        return new VmapTexProjection(u * repeatsPerUnit, v * repeatsPerUnit, 0f, 0f);
    }
}

/// <summary>One bounding face of a convex brush: its outward plane, its material, and its texture projection.</summary>
public sealed class VmapFace
{
    /// <summary>Outward-facing plane; the brush interior is <c>Dot(Normal, p) &lt;= Dist</c>.</summary>
    public VmapPlane Plane { get; set; }

    /// <summary>Shader/texture name as referenced by the material system (e.g. "textures/exx/floor01").</summary>
    public string Material { get; set; } = string.Empty;

    /// <summary>Texture projection for this face.</summary>
    public VmapTexProjection Projection { get; set; }

    /// <summary>Q3 surface flags (Q3SURFACEFLAG_*) for this face — nodraw/sky/slick/nonsolid etc.</summary>
    public int SurfaceFlags { get; set; }

    /// <summary>Q3 NATIVE content flags of this face, as stored in a BSP/.map (converted at collision-build time).</summary>
    public int ContentFlags { get; set; }
}

/// <summary>
/// A convex brush: the intersection of its faces' half-spaces. Needs at least 4 faces to bound a volume.
/// <see cref="Id"/> is stable across edits so editor ops, selections and override deltas can reference it.
/// </summary>
public sealed class VmapBrush
{
    /// <summary>Stable identifier, unique within the document.</summary>
    public int Id { get; set; }

    /// <summary>The bounding faces (>= 4 for a closed volume).</summary>
    public List<VmapFace> Faces { get; } = new();

    /// <summary>
    /// True when this brush is a q3map2 DETAIL brush (does not seal the world / take part in vis).
    /// Detail brushes are the primary detection signal for ornament amplification (design doc §9.1).
    /// </summary>
    public bool IsDetail { get; set; }

    /// <summary>Brush-wide Q3 NATIVE content flags (union of the faces' content flags at import time).</summary>
    public int ContentFlags { get; set; }

    /// <summary>
    /// True for a q3map2 TOOL brush — hint/skip/caulk/nodraw/clip/trigger/areaportal/origin. These are
    /// compiler and gameplay scaffolding (vis hints, collision volumes, trigger bounds), not level
    /// architecture: they render nothing, they outnumber the visible geometry on a real map, and they sit in
    /// front of it. An editor that lets you grab them makes the visible world nearly unclickable, so they are
    /// classified here and filtered out of picking by default.
    /// </summary>
    public bool IsToolBrush { get; set; }

    /// <summary>
    /// Classify from the faces: every face draws nothing (Q3 SURF_NODRAW) or names a <c>common/</c> tool
    /// shader. "Every" rather than "any" matters — a normal brush may legitimately have caulked back faces.
    /// </summary>
    public bool ClassifyToolBrush()
    {
        if (Faces.Count == 0)
            return false;
        foreach (VmapFace f in Faces)
        {
            bool nodraw = (f.SurfaceFlags & 0x0080) != 0;   // Q3SURFACEFLAG_NODRAW
            if (!nodraw && !IsToolMaterial(f.Material))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True for a material that never produces a visible surface: the Q3 <c>common/</c> tool shader family,
    /// and <c>noshader</c>.
    ///
    /// <c>noshader</c> matters more than the named tools on a COMPILED map. q3map2 writes it for every brush
    /// side that was not a drawn surface, so the vis/structural brushes a mapper never wants to grab are almost
    /// all "noshader on every face" rather than "common/hint". Classifying only the named tool shaders caught
    /// 66 brushes out of stormkeep's 5400 and left the rest in the way.
    /// </summary>
    public static bool IsToolMaterial(string material)
    {
        // An empty or placeholder shader draws nothing, so a brush made entirely of them is not architecture.
        if (string.IsNullOrEmpty(material))
            return true;
        if (material.Equals("noshader", StringComparison.OrdinalIgnoreCase))
            return true;
        ReadOnlySpan<char> m = material.AsSpan();
        int slash = m.LastIndexOf('/');
        ReadOnlySpan<char> leaf = slash >= 0 ? m[(slash + 1)..] : m;

        return material.Contains("common/", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("hint", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("skip", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("caulk", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("nodraw", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("clip", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("trigger", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("areaportal", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("origin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deep copy, used by the editor's undo journal to snapshot a brush before an op mutates it. Undo by
    /// snapshot rather than by inverse op: a vertex drag re-derives planes through a least-squares fit, so
    /// "drag back the other way" is not an exact inverse and would let geometry drift with every undo cycle.
    /// </summary>
    public VmapBrush Clone()
    {
        var copy = new VmapBrush { Id = Id, IsDetail = IsDetail, ContentFlags = ContentFlags };
        foreach (VmapFace f in Faces)
        {
            copy.Faces.Add(new VmapFace
            {
                Plane = f.Plane,
                Material = f.Material,
                Projection = f.Projection,
                SurfaceFlags = f.SurfaceFlags,
                ContentFlags = f.ContentFlags,
            });
        }
        return copy;
    }
}

/// <summary>
/// A bezier patch mesh: a (2n+1) x (2m+1) grid of control points forming n x m biquadratic patches —
/// Q3's <c>patchDef2</c> and BSP <c>BspFaceType.Patch</c> faces.
/// </summary>
public sealed class VmapPatch
{
    /// <summary>Stable identifier, unique within the document's patches.</summary>
    public int Id { get; set; }

    /// <summary>Shader/texture name.</summary>
    public string Material { get; set; } = string.Empty;

    /// <summary>Control-point grid width (odd, >= 3).</summary>
    public int Width { get; set; }

    /// <summary>Control-point grid height (odd, >= 3).</summary>
    public int Height { get; set; }

    /// <summary>Control-point positions, row-major, <see cref="Width"/> * <see cref="Height"/> entries.</summary>
    public List<Vector3> Controls { get; } = new();

    /// <summary>Control-point texture coordinates, parallel to <see cref="Controls"/>.</summary>
    public List<Vector2> ControlUvs { get; } = new();

    /// <summary>Q3 surface flags for the patch's shader.</summary>
    public int SurfaceFlags { get; set; }

    /// <summary>Q3 native content flags for the patch's shader.</summary>
    public int ContentFlags { get; set; }

    /// <summary>True when the grid dimensions and buffer sizes are self-consistent.</summary>
    public bool IsValid =>
        Width >= 3 && Height >= 3 && (Width & 1) == 1 && (Height & 1) == 1
        && Controls.Count == Width * Height && ControlUvs.Count == Controls.Count;
}

/// <summary>
/// A map entity: a classname plus arbitrary key/values, optionally owning brushes and patches
/// (a "brush entity" such as <c>func_door</c> or <c>trigger_multiple</c>).
/// </summary>
public sealed class VmapEntity
{
    /// <summary>Stable identifier, unique within the document's entities.</summary>
    public int Id { get; set; }

    /// <summary>The <c>classname</c> key, hoisted for convenience; always mirrored in <see cref="Fields"/>.</summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>All key/values including <c>classname</c>, <c>origin</c>, <c>angle</c>, targets, spawnflags.</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids of brushes owned by this entity (empty for a point entity).</summary>
    public List<int> BrushIds { get; } = new();

    /// <summary>Ids of patches owned by this entity.</summary>
    public List<int> PatchIds { get; } = new();

    /// <summary>True when this entity owns geometry (a brush entity rather than a point entity).</summary>
    public bool IsBrushEntity => BrushIds.Count > 0 || PatchIds.Count > 0;

    /// <summary>Read the <c>origin</c> key as a vector, or <see cref="Vector3.Zero"/> when absent/malformed.</summary>
    public Vector3 Origin()
        => Fields.TryGetValue("origin", out string? s) && TryParseVector(s, out Vector3 v) ? v : Vector3.Zero;

    /// <summary>Parse a whitespace-separated "x y z" value (the Quake entity-lump vector format).</summary>
    public static bool TryParseVector(string? s, out Vector3 v)
    {
        v = Vector3.Zero;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        string[] parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            return false;
        v = new Vector3(x, y, z);
        return true;
    }
}
