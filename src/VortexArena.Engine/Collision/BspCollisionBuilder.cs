using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// Builds the engine collision representation from a parsed <see cref="BspData"/> — the C# successor to DP's
/// <c>Mod_Q3BSP_Load*</c> collision path. Splits the map's brushes into the <strong>static world</strong>
/// (worldspawn, <c>Models[0]</c>) and the per-entity <strong>inline brush models</strong> (<c>Models[1..N]</c>,
/// the <c>"*N"</c> geometry of moving SOLID_BSP entities — func_door/plat/button/rotating doors/…).
///
/// <para>Before this, every brush was dumped into the static world, so a door's brushes were baked into the
/// level at their closed position (a permanently-solid wall that never moved) and the door entity itself had
/// no collision body. Now only worldspawn goes to the static world; each <c>"*N"</c> model is registered on the
/// <see cref="ModelService"/> with its own brushes, so <c>SV_ClipMoveToEntity</c>'s SOLID_BSP path
/// (<see cref="EngineServices.TryGetEntityBrushModel"/>) clips against the real, moving brush geometry instead
/// of an AABB fallback.</para>
///
/// Godot-free (lives in the Engine layer) so the headless server and unit tests can build collision without a
/// Godot runtime. Everything is in Quake coordinates, fed straight to the <see cref="TraceService"/>.
/// </summary>
public static class BspCollisionBuilder
{
    /// <summary>An inline <c>"*N"</c> brush model: its name, world-space bounds, and model-local clip brushes.</summary>
    public readonly record struct Submodel(string Name, Vector3 Mins, Vector3 Maxs, Brush[] Brushes);

    /// <summary>The product of <see cref="Build"/>: the static collision world + the inline brush models.</summary>
    public sealed class Result
    {
        /// <summary>The static world collision (worldspawn geometry only), grid built and ready to trace.</summary>
        public required CollisionWorld World { get; init; }

        /// <summary>The <c>"*1".."*N"</c> inline brush models, to register on a <see cref="ModelService"/>.</summary>
        public required IReadOnlyList<Submodel> Submodels { get; init; }
    }

    /// <summary>
    /// Build the static collision world (worldspawn brushes) and the inline brush-model set from
    /// <paramref name="bsp"/>. When the BSP has a <see cref="BspData.Models"/> lump, the worldspawn range is
    /// <c>Models[0]</c> and each subsequent model becomes a <c>"*N"</c> submodel; without it (degenerate/old
    /// data) every brush goes to the world (the prior behavior).
    ///
    /// <paramref name="droppedSubmodels"/> (optional) names inline-model indices <c>N</c> that the active
    /// gametype filters out (see <see cref="MapEntityFilter.DroppedSubmodels"/>): their brushes are skipped so a
    /// gametype-conditional brush entity (e.g. a Race-only <c>func_wall "*N"</c>) carries no collision in a
    /// gametype it doesn't belong to. Null/empty → keep every submodel (prior behavior).
    /// </summary>
    public static Result Build(BspData bsp, IReadOnlySet<int>? droppedSubmodels = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        var world = new CollisionWorld();
        var submodels = new List<Submodel>();

        if (bsp.Models.Length == 0)
        {
            // No models lump: treat the whole brush array as the static world (legacy fallback).
            for (int bi = 0; bi < bsp.Brushes.Length; bi++)
                if (BuildBrush(bsp, bi) is { } b)
                    world.AddBrush(b);
            AppendPatchBrushes(bsp, 0, bsp.Faces.Length, world.AddBrush);
            world.BuildGrid();
            return new Result { World = world, Submodels = submodels };
        }

        // Model 0 == worldspawn → the static world.
        BspModel worldModel = bsp.Models[0];
        int worldEnd = worldModel.FirstBrush + worldModel.BrushCount;
        for (int bi = worldModel.FirstBrush; bi < worldEnd; bi++)
            if (bi >= 0 && bi < bsp.Brushes.Length && BuildBrush(bsp, bi) is { } b)
                world.AddBrush(b);
        // Curved (patch) surfaces have no brushes in the BSP — tessellate worldspawn patches into collision
        // slabs so floors/grates/platforms made of patches are solid. DP collides curve surfaces; without
        // this the brush-only path leaves them intangible (the "grate over lava" / "platform" fall-through).
        AppendPatchBrushes(bsp, worldModel.FirstFace, worldModel.FaceCount, world.AddBrush);
        world.BuildGrid();

        // Models 1..N → the "*N" inline brush models (per-entity collision geometry). A model whose entity the
        // active gametype filtered out is skipped entirely (no Submodel emitted → nothing to register/clip).
        for (int mi = 1; mi < bsp.Models.Length; mi++)
        {
            if (droppedSubmodels is not null && droppedSubmodels.Contains(mi))
                continue;

            BspModel m = bsp.Models[mi];
            var brushes = new List<Brush>(m.BrushCount);
            int end = m.FirstBrush + m.BrushCount;
            for (int bi = m.FirstBrush; bi < end; bi++)
                if (bi >= 0 && bi < bsp.Brushes.Length && BuildBrush(bsp, bi) is { } b)
                    brushes.Add(b);
            // Patch surfaces owned by this inline model (e.g. a curved func_door panel) get the same
            // tessellated-slab collision, in model-local space, so the SOLID_BSP clip path sees them.
            AppendPatchBrushes(bsp, m.FirstFace, m.FaceCount, brushes.Add);

            submodels.Add(new Submodel($"*{mi}", m.Mins, m.Maxs, brushes.ToArray()));
        }

        return new Result { World = world, Submodels = submodels };
    }

    /// <summary>
    /// Register each inline brush model on <paramref name="models"/> (DP's submodel precache), so a
    /// <c>setmodel(e, "*N")</c> resolves real bounds and <see cref="EngineServices.TryGetEntityBrushModel"/>
    /// returns the model's clip brushes. A submodel with no brushes (mesh-only, e.g. a func_static) registers
    /// with bounds only, falling back to the AABB box like DP does for a brushless SOLID_BSP model.
    /// </summary>
    public static void RegisterSubmodels(IEnumerable<Submodel> submodels, ModelService models)
    {
        ArgumentNullException.ThrowIfNull(submodels);
        ArgumentNullException.ThrowIfNull(models);
        foreach (Submodel sm in submodels)
        {
            models.Register(sm.Name, sm.Mins, sm.Maxs, isBrushModel: true);
            if (!models.TryGetModel(sm.Name, out ModelService.ModelDef def))
                continue;
            // Register is idempotent (won't overwrite an existing def's bounds), so stamp them explicitly.
            def.IsBrushModel = true;
            def.Mins = sm.Mins;
            def.Maxs = sm.Maxs;
            def.CollisionBrushes = sm.Brushes.Length > 0 ? sm.Brushes : null;
        }
    }

    /// <summary>Convenience: build the collision world and register the submodels on one model service.</summary>
    public static CollisionWorld BuildAndRegister(BspData bsp, ModelService models)
    {
        Result r = Build(bsp);
        RegisterSubmodels(r.Submodels, models);
        return r.World;
    }

    // =============================================================================================
    // brush construction (moved here from the Godot host's MapLoader so it's headless + server-usable)
    // =============================================================================================

    /// <summary>
    /// Build one engine <see cref="Brush"/> from BSP brush <paramref name="brushIndex"/>: gather its sides'
    /// planes (outward normals), derive the corner points and unique edge directions the SAT sweep needs, and
    /// stamp the content flags from the brush's texture entry. Returns null for a degenerate/open brush.
    /// </summary>
    private static Brush? BuildBrush(BspData bsp, int brushIndex)
    {
        BspBrush brush = bsp.Brushes[brushIndex];

        // The texture lump stores RAW Q3 native content bits (BspReader reads them verbatim). Convert to the
        // engine's SUPERCONTENTS bitspace HERE — exactly as DP's Mod_Q3BSP_LoadBrushes does — or the on-disk
        // bits alias the wrong masks (Q3 TRANSLUCENT == our Corpse), turning water/lava/hint/donotenter brushes
        // into invisible walls. A missing/out-of-range texture defaults to a plain solid brush.
        bool haveBrushTex = brush.TextureIndex >= 0 && brush.TextureIndex < bsp.Textures.Length;
        int q3Contents = haveBrushTex ? bsp.Textures[brush.TextureIndex].ContentFlags : Q3Contents.Solid;
        int contents = SuperContents.FromQ3Native(q3Contents);

        // Brush-wide shader name (DP colbrushf_t.texture->name) — the value the trace reports for an
        // edge-cross separating axis and for the brush as a whole. This is the name world.qc compares
        // against "common/caulk". A missing/out-of-range texture leaves it null (DP NULL → string_null).
        string? brushTexName = haveBrushTex ? bsp.Textures[brush.TextureIndex].ShaderName : null;

        var planes = new List<BrushPlane>(brush.SideCount);
        int end = brush.FirstSide + brush.SideCount;
        for (int s = brush.FirstSide; s < end; s++)
        {
            if (s < 0 || s >= bsp.BrushSides.Length)
                continue;
            BspBrushSide side = bsp.BrushSides[s];
            if (side.PlaneIndex < 0 || side.PlaneIndex >= bsp.Planes.Length)
                continue;
            BspPlane p = bsp.Planes[side.PlaneIndex];

            bool haveSideTex = side.TextureIndex >= 0 && side.TextureIndex < bsp.Textures.Length;

            int sideSurfaceFlags = side.SurfaceFlags >= 0
                ? side.SurfaceFlags
                : (haveSideTex ? bsp.Textures[side.TextureIndex].SurfaceFlags : 0);

            // Per-side shader name: prefer the side's own texture (IG/v48 maps carry a real per-side
            // TextureIndex), else fall back to the brush-wide name — mirroring the sideSurfaceFlags
            // fallback just above. (DP's Mod_Q3BSP_LoadBrushSides assigns each brushside its own texture.)
            string? sideTexName = haveSideTex ? bsp.Textures[side.TextureIndex].ShaderName : brushTexName;

            planes.Add(new BrushPlane(p.Normal, p.Distance, sideSurfaceFlags, contents, sideTexName));
        }

        if (planes.Count < 4)
            return null; // not a closed convex volume

        BrushPlane[] sides = planes.ToArray();
        Vector3[] points = ComputeBrushPoints(sides);
        if (points.Length < 4)
            return null; // degenerate / open brush

        Vector3[] edgeDirs = ComputeEdgeDirs(sides);

        int brushSurfaceFlags = 0;
        for (int i = 0; i < sides.Length; i++)
            brushSurfaceFlags |= sides[i].SurfaceFlags;

        return new Brush(sides, points, edgeDirs, contents, brushSurfaceFlags, isAabb: false, texture: brushTexName);
    }

    /// <summary>
    /// Corner vertices of a convex brush: intersect every plane triple, keep the points inside (or on) all
    /// planes. The standard Quake brush-winding derivation; the trace service uses them for SAT projections.
    /// </summary>
    private static Vector3[] ComputeBrushPoints(BrushPlane[] sides)
    {
        const float onEpsilon = 0.1f;
        var pts = new List<Vector3>();
        int n = sides.Length;

        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        for (int k = j + 1; k < n; k++)
        {
            if (!TryIntersect3(sides[i], sides[j], sides[k], out Vector3 p))
                continue;

            bool inside = true;
            for (int m = 0; m < n; m++)
            {
                if (Vector3.Dot(sides[m].Normal, p) - sides[m].Dist > onEpsilon)
                {
                    inside = false;
                    break;
                }
            }
            if (!inside)
                continue;

            bool dup = false;
            for (int q = 0; q < pts.Count; q++)
                if ((pts[q] - p).LengthSquared() < 0.01f) { dup = true; break; }
            if (!dup)
                pts.Add(p);
        }

        return pts.ToArray();
    }

    /// <summary>Solve the intersection point of three planes (Cramer's rule); false if near-parallel.</summary>
    private static bool TryIntersect3(in BrushPlane a, in BrushPlane b, in BrushPlane c, out Vector3 point)
    {
        Vector3 n1 = a.Normal, n2 = b.Normal, n3 = c.Normal;
        Vector3 cross23 = Vector3.Cross(n2, n3);
        float denom = Vector3.Dot(n1, cross23);
        if (MathF.Abs(denom) < 1e-6f)
        {
            point = default;
            return false;
        }
        Vector3 cross31 = Vector3.Cross(n3, n1);
        Vector3 cross12 = Vector3.Cross(n1, n2);
        point = (a.Dist * cross23 + b.Dist * cross31 + c.Dist * cross12) * (1f / denom);
        return true;
    }

    /// <summary>
    /// Unique edge directions of the brush: the distinct face-plane-pair cross products (each shared edge lies
    /// along the cross of its two faces' normals), deduplicated up to sign so the SAT edge-cross axes aren't
    /// redundant.
    /// </summary>
    private static Vector3[] ComputeEdgeDirs(BrushPlane[] sides)
    {
        var dirs = new List<Vector3>();
        int n = sides.Length;
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            Vector3 d = Vector3.Cross(sides[i].Normal, sides[j].Normal);
            float len2 = d.LengthSquared();
            if (len2 < 1e-6f)
                continue;
            d *= 1f / MathF.Sqrt(len2);

            bool dup = false;
            for (int q = 0; q < dirs.Count; q++)
                if (MathF.Abs(Vector3.Dot(dirs[q], d)) > 0.999f) { dup = true; break; }
            if (!dup)
                dirs.Add(d);
        }
        return dirs.ToArray();
    }

    // =============================================================================================
    // patch (bezier curve surface) collision
    //
    // Q3 patches carry NO brushes — DP makes them collidable by tessellating each curve into triangles and
    // tracing the box against those triangles (Mod_Q3BSP_LoadFaces patch-collision path, gated by
    // mod_q3bsp_curves_collisions). The port's trace clips against convex Brushes, so we tessellate the patch
    // (reusing the render-side BezierPatch) and turn each triangle into a convex brush. The brush shape depends
    // on the triangle's slope so a player can REST on a patch without ever sitting inside the collision volume:
    //
    //   * WALKABLE triangle (|normal.z| >= WalkableNormalZ): a solid skirt whose TOP face is exactly the
    //     triangle, extending PatchFloorDepth along -normal (downward). The player rests at the true surface
    //     height — matching the visual and any adjacent brush floor (no 1-unit lip) — and is never inside the
    //     volume from above, so a landing cannot momentarily embed the player. (On a listen server an embedded
    //     server position while the client predicts forward reads as a stuck/"disconnect"→reconcile snap.)
    //   * STEEP triangle (wall): a thin two-sided slab centered on the plane — you slide along it, never land on
    //     it, so centering can't trap a resting player. Thinness + the continuous SAT sweep prevent tunnelling.
    //
    // The normal is oriented up for the walkable case so winding doesn't matter; a near-vertical curved CEILING
    // is the one imperfect case (it then collides up to PatchFloorDepth low) — rare and minor.
    // =============================================================================================

    /// <summary>Floor-normal threshold (matches <c>PlayerPhysics.OnGroundNormalZ</c>): a patch triangle this
    /// flat or flatter is a walkable floor (downward skirt); steeper is a wall (thin centered slab).</summary>
    private const float WalkableNormalZ = 0.7f;

    /// <summary>Depth (units) of the solid skirt below a walkable patch triangle — generous so a fast landing or
    /// a patch over a pit can't fall through; the whole volume sits below the surface the player stands on.</summary>
    private const float PatchFloorDepth = 24.0f;

    /// <summary>Thickness of the thin two-sided slab used for STEEP (wall) patch triangles. Thin so the
    /// continuous sweep can't tunnel and a grazing slide never traps the player.</summary>
    private const float PatchWallThickness = 2.0f;

    /// <summary>Tessellation steps per 3x3 control group for COLLISION (render uses <see cref="BezierPatch.Subdivisions"/>
    /// = 8). Lower keeps the collision-brush count sane; flat patches (most floors/grates) are exact at any
    /// level, and gentle curves are well approximated. ~12k slabs on stormkeep at 3.</summary>
    private const int PatchCollisionSubdivisions = 3;

    /// <summary>A patch gets collision only if its (converted) contents physically block something — DP collides
    /// solid curve surfaces; a nonsolid/translucent-only/sky/fog patch contributes none.</summary>
    private const int PatchCollidableMask = SuperContents.Solid | SuperContents.PlayerClip | SuperContents.MonsterClip;

    /// <summary>
    /// Tessellate every <see cref="BspFaceType.Patch"/> face in <c>[firstFace, firstFace+faceCount)</c> whose
    /// shader is physically solid and emit a thin convex slab brush per triangle into <paramref name="add"/>
    /// (the static world or an inline model's brush list). Non-patch faces, nonsolid patches, and degenerate
    /// triangles are skipped.
    /// </summary>
    private static void AppendPatchBrushes(BspData bsp, int firstFace, int faceCount, Action<Brush> add)
    {
        if (faceCount <= 0 || bsp.Faces.Length == 0)
            return;

        int end = firstFace + faceCount;
        for (int fi = firstFace; fi < end; fi++)
        {
            if (fi < 0 || fi >= bsp.Faces.Length)
                continue;
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Patch)
                continue;

            // Contents/surface flags from the patch's texture (RAW Q3 native → SUPERCONTENTS, like BuildBrush).
            bool haveTex = face.TextureIndex >= 0 && face.TextureIndex < bsp.Textures.Length;
            int q3 = haveTex ? bsp.Textures[face.TextureIndex].ContentFlags : Q3Contents.Solid;
            int contents = SuperContents.FromQ3Native(q3);
            int surfaceFlags = haveTex ? bsp.Textures[face.TextureIndex].SurfaceFlags : 0;
            // The patch's shader name (DP stamps texture->name on a curve-triangle hit, collision.c:1364) so a
            // curve impact reports its shader exactly like a brush face does.
            string? surfaceName = haveTex ? bsp.Textures[face.TextureIndex].ShaderName : null;

            if ((contents & PatchCollidableMask) == 0)
                continue; // nonsolid / translucent-only / sky / fog patch — no collision (DP behavior)
            if ((surfaceFlags & Q3SurfaceFlags.NonSolid) != 0)
                continue;

            BezierPatch.Tessellation? tess = BezierPatch.Tessellate(face, bsp.Vertices, PatchCollisionSubdivisions);
            if (tess is null)
                continue;

            List<BezierPatch.PatchVertex> verts = tess.Vertices;
            List<int> idx = tess.Indices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                Vector3 a = verts[idx[i]].Position;
                Vector3 b = verts[idx[i + 1]].Position;
                Vector3 c = verts[idx[i + 2]].Position;
                if (TryBuildTriangleSlab(a, b, c, contents, surfaceFlags, surfaceName) is { } slab)
                    add(slab);
            }
        }
    }

    /// <summary>
    /// Build a convex collision brush for patch triangle <paramref name="a"/>/<paramref name="b"/>/<paramref name="c"/>.
    /// A WALKABLE triangle (|normal.z| ≥ <see cref="WalkableNormalZ"/>) becomes a downward skirt — top face ON the
    /// triangle, back face <see cref="PatchFloorDepth"/> below — so a player rests at the true surface and never
    /// inside the volume. A STEEP triangle becomes a thin two-sided slab (±<see cref="PatchWallThickness"/>/2)
    /// centered on the plane. Both add three edge planes (each oriented so the opposite vertex is inside). Returns
    /// null for a zero-area triangle or a degenerate winding (fewer than 4 derivable corner points).
    /// </summary>
    private static Brush? TryBuildTriangleSlab(Vector3 a, Vector3 b, Vector3 c, int contents, int surfaceFlags, string? surfaceName = null)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        float len2 = n.LengthSquared();
        if (len2 < 1e-6f)
            return null; // zero-area triangle
        n *= 1f / MathF.Sqrt(len2);

        BrushPlane front, back;
        if (MathF.Abs(n.Z) >= WalkableNormalZ)
        {
            // Walkable floor: orient the normal up (winding-independent) and back the surface with a downward
            // skirt. Front face is exactly the triangle plane → the player rests at the real height, never inside.
            if (n.Z < 0f)
                n = -n;
            float d = Vector3.Dot(n, a);
            front = new BrushPlane(n, d, surfaceFlags, contents, surfaceName);
            back = new BrushPlane(-n, -(d - PatchFloorDepth), surfaceFlags, contents, surfaceName);
        }
        else
        {
            // Steep wall: a thin two-sided slab centered on the plane.
            float d = Vector3.Dot(n, a);
            float half = PatchWallThickness * 0.5f;
            front = new BrushPlane(n, d + half, surfaceFlags, contents, surfaceName);
            back = new BrushPlane(-n, -(d - half), surfaceFlags, contents, surfaceName);
        }

        var planes = new List<BrushPlane>(5) { front, back };
        AddEdgePlane(planes, a, b, c, n, surfaceFlags, contents, surfaceName);
        AddEdgePlane(planes, b, c, a, n, surfaceFlags, contents, surfaceName);
        AddEdgePlane(planes, c, a, b, n, surfaceFlags, contents, surfaceName);

        BrushPlane[] sides = planes.ToArray();
        Vector3[] points = ComputeBrushPoints(sides);
        if (points.Length < 4)
            return null;
        Vector3[] edgeDirs = ComputeEdgeDirs(sides);
        return new Brush(sides, points, edgeDirs, contents, surfaceFlags, isAabb: false, texture: surfaceName);
    }

    /// <summary>
    /// Append the side plane through edge (<paramref name="e0"/>→<paramref name="e1"/>) perpendicular to the
    /// triangle normal <paramref name="n"/>, oriented so the opposite vertex lies inside (back of) the plane. A
    /// degenerate (zero-length) edge contributes no plane — the remaining planes still bound the slab.
    /// </summary>
    private static void AddEdgePlane(List<BrushPlane> planes, Vector3 e0, Vector3 e1, Vector3 opposite,
        Vector3 n, int surfaceFlags, int contents, string? surfaceName = null)
    {
        Vector3 sn = Vector3.Cross(e1 - e0, n);
        float len2 = sn.LengthSquared();
        if (len2 < 1e-6f)
            return;
        sn *= 1f / MathF.Sqrt(len2);
        float dist = Vector3.Dot(sn, e0);
        if (Vector3.Dot(sn, opposite) - dist > 0f)
        {
            sn = -sn;       // flip so 'opposite' is inside (Dot(normal,p) <= dist)
            dist = -dist;
        }
        planes.Add(new BrushPlane(sn, dist, surfaceFlags, contents, surfaceName));
    }
}
