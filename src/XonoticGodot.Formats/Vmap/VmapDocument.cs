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
    /// <summary>
    /// Highest on-disk format version this build can read. 1 was the JSON sections, 2 added painted blend
    /// maps to them, 3 is the single text file (<see cref="VmapText"/>) and is what gets written.
    ///
    /// The older two are still READ — see <c>VmapPackage.ReadFromDirectory</c> — because a mapper with saves
    /// on disk should not lose them to a format change.
    /// </summary>
    public const int CurrentFormatVersion = 3;

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

    /// <summary>
    /// Painted layer-weight textures (backlog F2), addressed by <see cref="VmapFace.BlendMapId"/>.
    ///
    /// SOURCE data, not derived: the mapper painted it, so it belongs in the package next to the geometry
    /// rather than in a build cache that can be deleted without losing anything. That makes this the first
    /// binary payload the format carries.
    /// </summary>
    public List<VmapBlendMap> BlendMaps { get; } = new();

    /// <summary>Look up a blend map by its stable <see cref="VmapBlendMap.Id"/>.</summary>
    public VmapBlendMap? FindBlendMap(int id)
    {
        for (int i = 0; i < BlendMaps.Count; i++)
            if (BlendMaps[i].Id == id)
                return BlendMaps[i];
        return null;
    }

    /// <summary>The next unused blend-map id. Its own sequence, like patches and entities.</summary>
    public int NextBlendMapId()
    {
        int max = 0;
        for (int i = 0; i < BlendMaps.Count; i++)
            if (BlendMaps[i].Id > max)
                max = BlendMaps[i].Id;
        return max + 1;
    }

    /// <summary>
    /// Named object sets (backlog F8). Empty on every map that has none, and written to the package only when
    /// non-empty, so an existing <c>.vmap</c> round-trips byte for byte.
    /// </summary>
    public List<VmapGroup> Groups { get; } = new();

    /// <summary>Look up a group by its stable <see cref="VmapGroup.Id"/>.</summary>
    public VmapGroup? FindGroup(int id)
    {
        for (int i = 0; i < Groups.Count; i++)
            if (Groups[i].Id == id)
                return Groups[i];
        return null;
    }

    /// <summary>Look up a group by name, case-insensitively — how a mapper refers to one.</summary>
    public VmapGroup? FindGroup(string name)
    {
        for (int i = 0; i < Groups.Count; i++)
            if (string.Equals(Groups[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return Groups[i];
        return null;
    }

    /// <summary>The next unused group id. Id 0 is reserved for "ungrouped".</summary>
    public int NextGroupId()
    {
        int max = 0;
        for (int i = 0; i < Groups.Count; i++)
            if (Groups[i].Id > max)
                max = Groups[i].Id;
        return max + 1;
    }

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

    /// <summary>Look up a patch by its stable <see cref="VmapPatch.Id"/>.</summary>
    public VmapPatch? FindPatch(int id)
    {
        for (int i = 0; i < Patches.Count; i++)
            if (Patches[i].Id == id)
                return Patches[i];
        return null;
    }

    /// <summary>The next unused patch id. Independent of the brush sequence — the two never collide.</summary>
    public int NextPatchId()
    {
        int max = 0;
        for (int i = 0; i < Patches.Count; i++)
            if (Patches[i].Id > max)
                max = Patches[i].Id;
        return max + 1;
    }

    /// <summary>Look up an entity by its stable <see cref="VmapEntity.Id"/>.</summary>
    public VmapEntity? FindEntity(int id)
    {
        for (int i = 0; i < Entities.Count; i++)
            if (Entities[i].Id == id)
                return Entities[i];
        return null;
    }

    /// <summary>The next unused entity id.</summary>
    public int NextEntityId()
    {
        int max = 0;
        for (int i = 0; i < Entities.Count; i++)
            if (Entities[i].Id > max)
                max = Entities[i].Id;
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

    /// <summary>
    /// The brush entity that owns a brush, or null when it belongs to worldspawn.
    ///
    /// Needed because a brush entity is deliberately NOT pickable — it has no origin, so clicking a door
    /// returns the door's BRUSH. Every path that wants the door itself (its keys, its delete, its dissolve)
    /// has to go geometry → owner, and doing that in one place is what keeps them agreeing.
    /// </summary>
    public VmapEntity? OwnerOfBrush(int brushId)
    {
        for (int i = 0; i < Entities.Count; i++)
            if (Entities[i].BrushIds.Contains(brushId))
                return Entities[i];
        return null;
    }

    /// <summary>The brush entity that owns a patch, or null when it belongs to worldspawn.</summary>
    public VmapEntity? OwnerOfPatch(int patchId)
    {
        for (int i = 0; i < Entities.Count; i++)
            if (Entities[i].PatchIds.Contains(patchId))
                return Entities[i];
        return null;
    }
}

/// <summary>
/// A painted RGBA weight texture for one face's layer stack (backlog F2).
///
/// The weights are a TEXTURE rather than a vertex attribute because a brush face is a convex polygon with as
/// few as three corners: a flat wall would offer four control points to paint with, and subdividing it to
/// gain resolution would mean inventing a second, denser geometry representation purely so the painting had
/// somewhere to live.
///
/// The projection is planar and world-anchored, exactly like <see cref="VmapFace.Projection"/>, so a brush
/// face needs no UV unwrap — the same trick the diffuse already uses. It also means paint has to be carried
/// by texture lock when the geometry moves, or a nudged wall slides its moss off.
///
/// Patches have one material and no layer stack, so they get no blend map. When their turn comes the natural
/// store is a map keyed on the patch id with a UV-space projection; nothing here forecloses that.
/// </summary>
public sealed class VmapBlendMap
{
    /// <summary>Stable identifier, unique within the document. Never 0 — that value means "no blend map".</summary>
    public int Id { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>
    /// World units per texel, FROZEN at creation. Deliberately not re-read from its cvar: changing the
    /// setting later would silently rescale every map painted before it.
    /// </summary>
    public float UnitsPerTexel { get; set; } = 4f;

    /// <summary>Planar world to [0,1] map over the owning face.</summary>
    public VmapTexProjection Projection { get; set; }

    /// <summary>RGBA8, row-major, Width x Height x 4 bytes. One layer weight per channel.</summary>
    public byte[] Texels { get; set; } = Array.Empty<byte>();

    public bool IsValid => Width > 0 && Height > 0 && Texels.Length == Width * Height * 4;

    /// <summary>Deep copy — the TEXELS too, or an undo snapshot would alias the live buffer.</summary>
    public VmapBlendMap Clone()
    {
        var copy = new VmapBlendMap
        {
            Id = Id,
            Width = Width,
            Height = Height,
            UnitsPerTexel = UnitsPerTexel,
            Projection = Projection,
            Texels = new byte[Texels.Length],
        };
        Buffer.BlockCopy(Texels, 0, copy.Texels, 0, Texels.Length);
        return copy;
    }

    /// <summary>
    /// Copy a rectangle of texels out, clamped to the map. The undo journal snapshots RECTANGLES rather than
    /// whole maps: 256 entries of before-and-after on a 256-square map would be 128 MB for one painted wall.
    /// </summary>
    public byte[] CopyRegion(int x, int y, int w, int h)
    {
        Clamp(ref x, ref y, ref w, ref h);
        var region = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Texels, ((y + row) * Width + x) * 4, region, row * w * 4, w * 4);
        return region;
    }

    /// <summary>Put a rectangle of texels back. The counterpart of <see cref="CopyRegion"/>.</summary>
    public bool PasteRegion(int x, int y, int w, int h, byte[] src)
    {
        ArgumentNullException.ThrowIfNull(src);
        Clamp(ref x, ref y, ref w, ref h);
        if (w == 0 || h == 0)
            return true;
        if (src.Length < w * h * 4)
            return false;
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src, row * w * 4, Texels, ((y + row) * Width + x) * 4, w * 4);
        return true;
    }

    private void Clamp(ref int x, ref int y, ref int w, ref int h)
    {
        int x0 = Math.Clamp(x, 0, Math.Max(0, Width));
        int y0 = Math.Clamp(y, 0, Math.Max(0, Height));
        int x1 = Math.Clamp((int)Math.Min((long)x + w, Width), 0, Math.Max(0, Width));
        int y1 = Math.Clamp((int)Math.Min((long)y + h, Height), 0, Math.Max(0, Height));
        x = x0;
        y = y0;
        w = Math.Max(0, x1 - x0);
        h = Math.Max(0, y1 - y0);
    }
}

/// <summary>
/// A rectangle of one blend map — the unit an op declares it will touch, and the unit the journal snapshots.
///
/// The rectangle is in the interface rather than just the id because the alternative does not fit in memory:
/// the journal keeps 256 entries with a before AND an after, and whole-map snapshots of a 256-square map
/// would cost 128 MB for one wall.
/// </summary>
public readonly record struct VmapBlendRegion(int BlendMapId, int X, int Y, int Width, int Height)
{
    /// <summary>The whole map, whatever size it turns out to be — clamped when it is snapshotted.</summary>
    public static VmapBlendRegion Whole(int id) => new(id, 0, 0, int.MaxValue, int.MaxValue);
}

/// <summary>
/// A named set of objects that select, hide and show together (backlog F8).
///
/// DOCUMENT state, not view state: a group and whether it is hidden are properties of the map, so they save
/// and they replicate. That is the line between this and <see cref="VmapVisibility"/>'s ad-hoc hide, which is
/// one mapper's temporary view and belongs to nobody else.
/// </summary>
public sealed class VmapGroup
{
    /// <summary>Stable identifier, unique within the document. Never 0 — that value means "ungrouped".</summary>
    public int Id { get; set; }

    /// <summary>What the mapper called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hidden as a unit.</summary>
    public bool Hidden { get; set; }

    public VmapGroup Clone() => new() { Id = Id, Name = Name, Hidden = Hidden };
}

/// <summary>Map-level identity, provenance and environment settings — the <c>map</c> records of the file.</summary>
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

/// <summary>How a face layer combines with the layers beneath it.</summary>
public enum VmapBlend
{
    /// <summary>Replaces what is under it. What the base layer is, and the only thing a <c>.map</c> export keeps.</summary>
    Opaque,

    /// <summary>
    /// Steered by a painted WEIGHT MAP (see <see cref="VmapFace.BlendMapId"/> and
    /// <see cref="VmapFaceLayer.WeightChannel"/>) — the terrain-painting case, and the one a BSP cannot
    /// express beyond a single RGBA drawvert.
    ///
    /// The name is historical. The first design put the weight on the mesh VERTEX, which is wrong here for a
    /// reason that only shows up when you try to use it: a brush face is a convex polygon with as few as three
    /// corners, so a flat wall would offer four control points to paint with. Terrain meshes get away with it
    /// because they are already a dense grid. The enum VALUE is in package bytes and on the wire, so the name
    /// stays; only where the weight comes FROM changed.
    /// </summary>
    Vertex,

    /// <summary>Added to what is under it.</summary>
    Add,

    /// <summary>Multiplied with what is under it.</summary>
    Multiply,

    /// <summary>Alpha-blended using the layer texture's own alpha.</summary>
    Alpha,
}

/// <summary>
/// One textured layer of a face: a material, the projection that maps it onto the plane, and how it combines
/// with what is beneath it.
///
/// A face is a STACK of these rather than a single material, which is the point of departure from the Q3
/// lineage. A BSP drawvert carries one shader per face, one RGBA, and two UV sets, so a compiled map can only
/// fake blending by hiding stages inside one shader and steering them from that single colour. Layers here are
/// independent — their own material, their own projection, their own blend — because <c>.vmap</c> is not
/// constrained by a 1999 file format and should not be designed as though it were.
/// </summary>
public sealed class VmapFaceLayer
{
    /// <summary>Shader/texture name as referenced by the material system (e.g. "textures/exx/floor01").</summary>
    public string Material { get; set; } = string.Empty;

    /// <summary>How this layer's texture is mapped onto the face's plane. Independent per layer.</summary>
    public VmapTexProjection Projection { get; set; }

    /// <summary>How this layer combines with the layers under it.</summary>
    public VmapBlend Blend { get; set; } = VmapBlend.Opaque;

    /// <summary>
    /// Which channel of the FACE's blend map steers a <see cref="VmapBlend.Vertex"/> layer: 0-3, addressing
    /// R/G/B/A. -1 when the layer is not weight-steered.
    ///
    /// One RGBA map therefore drives up to four layers on one face, which is why the channel is an index into
    /// a texture rather than a texture of its own.
    /// </summary>
    public int WeightChannel { get; set; } = -1;

    public VmapFaceLayer Clone() => new()
    {
        Material = Material,
        Projection = Projection,
        Blend = Blend,
        WeightChannel = WeightChannel,
    };
}

/// <summary>
/// One bounding face of a convex brush: its outward plane, its stack of textured layers, and its Q3 flags.
/// </summary>
public sealed class VmapFace
{
    /// <summary>Outward-facing plane; the brush interior is <c>Dot(Normal, p) &lt;= Dist</c>.</summary>
    public VmapPlane Plane { get; set; }

    /// <summary>
    /// The layer stack, base first. Never empty: a face always has a base layer even when it is untextured,
    /// so <see cref="Material"/> and <see cref="Projection"/> always have somewhere to read and write.
    /// </summary>
    public List<VmapFaceLayer> Layers { get; } = new() { new VmapFaceLayer() };

    /// <summary>The base layer — what a single-textured face is, and what an export flattens a stack to.</summary>
    public VmapFaceLayer Base
    {
        get
        {
            // Defensive rather than an invariant: a caller that emptied the list would otherwise turn every
            // read of Material into an index-out-of-range, a long way from the code that emptied it.
            if (Layers.Count == 0)
                Layers.Add(new VmapFaceLayer());
            return Layers[0];
        }
    }

    /// <summary>True when this face carries more than its base layer.</summary>
    public bool IsLayered => Layers.Count > 1;

    /// <summary>
    /// The base layer's material. Kept as a direct property because the overwhelming majority of faces have
    /// exactly one layer, and every existing reader, writer and tool says <c>face.Material</c>.
    /// </summary>
    public string Material
    {
        get => Base.Material;
        set => Base.Material = value;
    }

    /// <summary>The base layer's texture projection. Same reasoning as <see cref="Material"/>.</summary>
    public VmapTexProjection Projection
    {
        get => Base.Projection;
        set => Base.Projection = value;
    }

    /// <summary>Q3 surface flags (Q3SURFACEFLAG_*) for this face — nodraw/sky/slick/nonsolid etc.</summary>
    public int SurfaceFlags { get; set; }

    /// <summary>
    /// The <see cref="VmapBlendMap"/> whose channels steer this face's weight layers, or 0 for none
    /// (backlog F2).
    ///
    /// Per FACE, not per brush: a brush's six sides are six disjoint planes with no shared parameterisation,
    /// so a per-brush map would need six sub-rectangles anyway — the same atlas, with a worse packer.
    /// </summary>
    public int BlendMapId { get; set; }

    /// <summary>Q3 NATIVE content flags of this face, as stored in a BSP/.map (converted at collision-build time).</summary>
    public int ContentFlags { get; set; }

    /// <summary>Deep copy, layer stack included.</summary>
    public VmapFace Clone()
    {
        var copy = new VmapFace
        {
            Plane = Plane,
            SurfaceFlags = SurfaceFlags,
            ContentFlags = ContentFlags,
            BlendMapId = BlendMapId,
        };
        copy.Layers.Clear();
        foreach (VmapFaceLayer l in Layers)
            copy.Layers.Add(l.Clone());
        if (copy.Layers.Count == 0)
            copy.Layers.Add(new VmapFaceLayer());
        return copy;
    }
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
    /// Which inline brush model owns this brush: 0 = worldspawn, N = the entity whose <c>model</c> is "*N".
    ///
    /// Kept so the editor can FILTER by gametype instead of discarding: a func_wall that only exists in CTF is
    /// still real map data and must survive a load/save round-trip, it just should not be shown while editing
    /// for a mode that does not have it.
    /// </summary>
    public int SubmodelIndex { get; set; }

    /// <summary>
    /// True for a q3map2 TOOL brush — hint/skip/caulk/nodraw/clip/trigger/areaportal/origin. These are
    /// compiler and gameplay scaffolding (vis hints, collision volumes, trigger bounds), not level
    /// architecture: they render nothing, they outnumber the visible geometry on a real map, and they sit in
    /// front of it. An editor that lets you grab them makes the visible world nearly unclickable, so they are
    /// classified here and filtered out of picking by default.
    /// </summary>
    public bool IsToolBrush { get; set; }

    /// <summary>
    /// The <see cref="VmapGroup"/> this object belongs to, or 0 for none (backlog F8).
    ///
    /// EXCLUSIVE membership, which is Radiant's model rather than a layer stack: object-to-group is then one
    /// field to read, one field to snapshot, and undo is exact. Multi-membership would need a side table and
    /// would make "which group does clicking this select" a question with several answers.
    /// </summary>
    public int GroupId { get; set; }

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
        // SubmodelIndex and IsToolBrush must ride along. Both are classification rather than geometry, which is
        // why they were easy to miss, and both change what the editor DOES with the brush: dropping the
        // submodel silently moves a gametype-conditional func_wall into worldspawn, and dropping the tool flag
        // makes a caulk volume pickable. Undo restores from a clone, so an omission here surfaces as "undoing a
        // delete brought the brush back subtly different".
        var copy = new VmapBrush
        {
            Id = Id,
            IsDetail = IsDetail,
            ContentFlags = ContentFlags,
            SubmodelIndex = SubmodelIndex,
            IsToolBrush = IsToolBrush,
            GroupId = GroupId,
        };
        // Through VmapFace.Clone so the whole LAYER STACK comes along. Copying Material/Projection by hand
        // would silently flatten a layered face to its base every time undo snapshotted it — the same class of
        // omission as the classification fields above, and harder to see because the face still looks right.
        foreach (VmapFace f in Faces)
            copy.Faces.Add(f.Clone());
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

    /// <summary>
    /// The <see cref="VmapGroup"/> this object belongs to, or 0 for none (backlog F8).
    ///
    /// EXCLUSIVE membership, which is Radiant's model rather than a layer stack: object-to-group is then one
    /// field to read, one field to snapshot, and undo is exact. Multi-membership would need a side table and
    /// would make "which group does clicking this select" a question with several answers.
    /// </summary>
    public int GroupId { get; set; }

    /// <summary>True when the grid dimensions and buffer sizes are self-consistent.</summary>
    public bool IsValid =>
        Width >= 3 && Height >= 3 && (Width & 1) == 1 && (Height & 1) == 1
        && Controls.Count == Width * Height && ControlUvs.Count == Controls.Count;

    /// <summary>Deep copy, for the undo journal and the clipboard. Same contract as <see cref="VmapBrush.Clone"/>.</summary>
    public VmapPatch Clone()
    {
        var copy = new VmapPatch
        {
            Id = Id,
            Material = Material,
            Width = Width,
            Height = Height,
            SurfaceFlags = SurfaceFlags,
            ContentFlags = ContentFlags,
            GroupId = GroupId,
        };
        copy.Controls.AddRange(Controls);
        copy.ControlUvs.AddRange(ControlUvs);
        return copy;
    }
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

    /// <summary>
    /// The <see cref="VmapGroup"/> this entity belongs to, or 0 for none (backlog F8). Exclusive membership,
    /// same as brushes and patches.
    /// </summary>
    public int GroupId { get; set; }

    /// <summary>Read the <c>origin</c> key as a vector, or <see cref="Vector3.Zero"/> when absent/malformed.</summary>
    public Vector3 Origin()
        => Fields.TryGetValue("origin", out string? s) && TryParseVector(s, out Vector3 v) ? v : Vector3.Zero;

    /// <summary>Write the <c>origin</c> key, in the Quake entity-lump format the readers expect.</summary>
    public void SetOrigin(Vector3 v)
        => Fields["origin"] = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}");

    /// <summary>
    /// Deep copy, for the undo journal and the clipboard. The field dictionary is copied, not shared: an entity
    /// on the clipboard that still pointed at the original's dictionary would pick up every later key edit to
    /// the thing it was copied from.
    /// </summary>
    public VmapEntity Clone()
    {
        var copy = new VmapEntity { Id = Id, ClassName = ClassName, GroupId = GroupId };
        foreach (KeyValuePair<string, string> kv in Fields)
            copy.Fields[kv.Key] = kv.Value;
        copy.BrushIds.AddRange(BrushIds);
        copy.PatchIds.AddRange(PatchIds);
        return copy;
    }

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
