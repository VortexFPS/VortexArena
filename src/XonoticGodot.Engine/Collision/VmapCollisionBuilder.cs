using System.Numerics;
using XonoticGodot.Formats.Vmap;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// Builds engine collision from an editable <see cref="VmapDocument"/> — the <c>.vmap</c> counterpart of
/// <see cref="BspCollisionBuilder"/>, producing the identical <see cref="BspCollisionBuilder.Result"/> so the
/// host wires it up exactly the same way (static world + <c>"*N"</c> inline brush models).
///
/// Where the BSP builder reads a precompiled brush lump, this one evaluates the truth planes directly through
/// <see cref="VmapWinding"/>. That is what lets collision refit IMMEDIATELY after a geometry edit, which the
/// editor's PLAYTEST state depends on: the surface you just dragged has to be solid where you see it
/// (design doc §11.4).
///
/// Godot-free, like its BSP sibling, so the headless server and tests can build collision.
/// </summary>
public static class VmapCollisionBuilder
{
    /// <summary>
    /// How far a curved patch's collision hull may sit from the surface the renderer draws, in world units
    /// (backlog B3). Matches <c>BspCollisionBuilder</c>'s tolerance, because a patch should collide the same
    /// way whether it arrived from a compiled map or from the document.
    /// </summary>
    private const float PatchCollisionTolerance = 1.0f;

    /// <inheritdoc cref="BspCollisionBuilder"/>
    /// <summary>The looser tolerance a near-vertical patch gets — same reasoning as the BSP builder's.</summary>
    private const float PatchWallTolerance = 6.0f;

    /// <summary>
    /// Build the static world (brushes not owned by any brush entity) plus one submodel per brush entity.
    /// Brush entities that lack a <c>model</c> key are assigned the next free <c>"*N"</c> name, and the key is
    /// written back onto the entity so the server's <c>setmodel</c> resolves it.
    /// </summary>
    public static BspCollisionBuilder.Result Build(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var world = new CollisionWorld();
        var submodels = new List<BspCollisionBuilder.Submodel>();

        // Brushes and patches claimed by a brush entity belong to that entity's inline model, not the world.
        var claimedBrushes = new HashSet<int>();
        var claimedPatches = new HashSet<int>();
        foreach (VmapEntity e in doc.Entities)
        {
            if (!e.IsBrushEntity)
                continue;
            foreach (int id in e.BrushIds)
                claimedBrushes.Add(id);
            foreach (int id in e.PatchIds)
                claimedPatches.Add(id);
        }

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (claimedBrushes.Contains(brush.Id))
                continue;
            if (TryBuildBrush(brush, out Brush? built))
                world.AddBrush(built!);
        }

        foreach (VmapPatch patch in doc.Patches)
        {
            if (claimedPatches.Contains(patch.Id))
                continue;
            AppendPatchBrushes(patch, world.AddBrush);
        }

        world.BuildGrid();

        // Names already spoken for by imported entities. Minting positionally is not enough on a document a
        // mapper has edited: dissolve an entity that held "*2" and assign a new one, and the counter hands the
        // new one "*3" while the imported entity holding "*3" is still there. Two submodels then arrive at the
        // registry under one name and one of them silently wins.
        var takenModelNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (VmapEntity e in doc.Entities)
            if (e.IsBrushEntity && e.Fields.TryGetValue("model", out string? held) && held.StartsWith('*'))
                takenModelNames.Add(held);

        int nextModelIndex = 1;
        foreach (VmapEntity e in doc.Entities)
        {
            if (!e.IsBrushEntity)
                continue;

            var brushes = new List<Brush>(e.BrushIds.Count);
            Vector3 mins = new(float.MaxValue), maxs = new(float.MinValue);
            bool any = false;

            foreach (int id in e.BrushIds)
            {
                VmapBrush? vb = doc.FindBrush(id);
                if (vb is null || !TryBuildBrush(vb, out Brush? built))
                    continue;
                brushes.Add(built!);
                mins = Vector3.Min(mins, built!.Mins);
                maxs = Vector3.Max(maxs, built.Maxs);
                any = true;
            }

            foreach (int id in e.PatchIds)
            {
                VmapPatch? patch = doc.Patches.FirstOrDefault(p => p.Id == id);
                if (patch is null)
                    continue;
                foreach (Brush slab in BuildPatchBrushes(patch))
                {
                    brushes.Add(slab);
                    mins = Vector3.Min(mins, slab.Mins);
                    maxs = Vector3.Max(maxs, slab.Maxs);
                    any = true;
                }
            }

            if (!any)
            {
                mins = maxs = Vector3.Zero;
            }

            // Preserve the imported "*N" name when there is one; otherwise mint the next FREE index and record
            // it, so a natively-authored brush entity gets a model the engine can resolve — and cannot be
            // handed a name another entity already answers to.
            string name;
            if (e.Fields.TryGetValue("model", out string? existing) && existing.StartsWith('*'))
            {
                name = existing;
            }
            else
            {
                while (takenModelNames.Contains($"*{nextModelIndex}"))
                    nextModelIndex++;
                name = $"*{nextModelIndex}";
                takenModelNames.Add(name);
                e.Fields["model"] = name;
                nextModelIndex++;
            }

            submodels.Add(new BspCollisionBuilder.Submodel(name, mins, maxs, brushes.ToArray()));
        }

        return new BspCollisionBuilder.Result { World = world, Submodels = submodels };
    }

    /// <summary>Convenience mirror of <see cref="BspCollisionBuilder.BuildAndRegister"/>.</summary>
    public static CollisionWorld BuildAndRegister(VmapDocument doc, Simulation.ModelService models)
    {
        BspCollisionBuilder.Result r = Build(doc);
        BspCollisionBuilder.RegisterSubmodels(r.Submodels, models);
        return r.World;
    }

    /// <summary>
    /// Convert one truth brush into an engine clip brush: outward planes, corner points and SAT edge
    /// directions, with Q3 native content bits translated into the engine's SUPERCONTENTS space (the same
    /// conversion <see cref="BspCollisionBuilder"/> performs — skipping it aliases water/lava/hint onto the
    /// wrong masks and turns them into invisible walls).
    /// </summary>
    public static bool TryBuildBrush(VmapBrush brush, out Brush? result)
    {
        result = null;
        ArgumentNullException.ThrowIfNull(brush);
        if (brush.Faces.Count < 4)
            return false;

        int contents = SuperContents.FromQ3Native(brush.ContentFlags != 0 ? brush.ContentFlags : Q3Contents.Solid);

        // The brush-wide texture (DP colbrushf_t.texture->name) is what world.qc compares to "common/caulk";
        // take the first face's material as the representative, matching the BSP path's brush-wide texture.
        string? brushTexture = brush.Faces.Count > 0 ? NullIfEmpty(brush.Faces[0].Material) : null;

        var sides = new BrushPlane[brush.Faces.Count];
        int surfaceFlags = 0;
        for (int i = 0; i < brush.Faces.Count; i++)
        {
            VmapFace f = brush.Faces[i];
            sides[i] = new BrushPlane(
                f.Plane.Normal,
                f.Plane.Dist,
                f.SurfaceFlags,
                contents,
                NullIfEmpty(f.Material));
            surfaceFlags |= f.SurfaceFlags;
        }

        Vector3[] points = VmapWinding.BrushPoints(brush);
        if (points.Length < 4)
            return false; // degenerate / open plane set

        Vector3[] edgeDirs = ComputeEdgeDirs(sides);
        result = new Brush(sides, points, edgeDirs, contents, surfaceFlags, isAabb: false, texture: brushTexture);
        return true;
    }

    /// <summary>
    /// Unique edge directions for the SAT sweep: each shared edge of a convex solid runs along the cross
    /// product of its two faces' normals; deduplicated up to sign so the axis set is not redundant.
    /// </summary>
    private static Vector3[] ComputeEdgeDirs(BrushPlane[] sides)
    {
        var dirs = new List<Vector3>();
        for (int i = 0; i < sides.Length; i++)
        for (int j = i + 1; j < sides.Length; j++)
        {
            Vector3 d = Vector3.Cross(sides[i].Normal, sides[j].Normal);
            float len2 = d.LengthSquared();
            if (len2 < 1e-6f)
                continue;
            d *= 1f / MathF.Sqrt(len2);

            bool dup = false;
            for (int q = 0; q < dirs.Count; q++)
            {
                if (MathF.Abs(Vector3.Dot(dirs[q], d)) > 0.999f)
                {
                    dup = true;
                    break;
                }
            }
            if (!dup)
                dirs.Add(d);
        }
        return dirs.ToArray();
    }

    // =============================================================================================
    //  Patch collision — bezier surfaces carry no brushes, so tessellate them into thin convex slabs
    //  (the same technique BspCollisionBuilder uses; without it patch floors and grates are intangible).
    // =============================================================================================

    private static void AppendPatchBrushes(VmapPatch patch, Action<Brush> sink)
    {
        foreach (Brush b in BuildPatchBrushes(patch))
            sink(b);
    }

    private static IEnumerable<Brush> BuildPatchBrushes(VmapPatch patch)
    {
        if (!patch.IsValid)
            yield break;
        if ((patch.SurfaceFlags & Q3SurfaceFlags.NonSolid) != 0)
            yield break;

        var doc = new VmapDocument();
        doc.Patches.Add(patch);

        // Subdivision measured from the patch, not inherited (backlog B3). The editor's RENDER subdivision is
        // cvar-driven (cl_editor_patch_subdiv, 2..24) while this used the fixed default, so the two disagreed
        // about where a curve is the moment a mapper touched that cvar — the same class of bug as the
        // collision/render mismatch on the BSP side, just harder to notice because it depends on a setting.
        // Asking the geometry instead makes collision accurate on its own terms and independent of how
        // finely anyone happens to be drawing.
        float horizontality = XonoticGodot.Formats.Bsp.BezierPatch.Horizontality(
            patch.Controls, patch.Width, patch.Height);
        float tolerance = float.Lerp(PatchWallTolerance, PatchCollisionTolerance, horizontality);
        int subdivisions = XonoticGodot.Formats.Bsp.BezierPatch.SubdivisionsFor(
            patch.Controls, patch.Width, patch.Height, tolerance);

        // Sky-flagged patches still need collision, so ask for them explicitly.
        IReadOnlyList<VmapSurface> surfaces =
            VmapGeometryBuilder.BuildSurfaces(doc, includeSky: true, patchSubdivisions: subdivisions);

        int contents = SuperContents.FromQ3Native(patch.ContentFlags != 0 ? patch.ContentFlags : Q3Contents.Solid);
        string? texture = NullIfEmpty(patch.Material);

        foreach (VmapSurface s in surfaces)
        {
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                Vector3 a = s.Positions[s.Indices[i]];
                Vector3 b = s.Positions[s.Indices[i + 1]];
                Vector3 c = s.Positions[s.Indices[i + 2]];
                if (TryBuildTriangleSlab(a, b, c, contents, patch.SurfaceFlags, texture) is { } slab)
                    yield return slab;
            }
        }
    }

    /// <summary>Thickness of the solid slab generated behind a tessellated patch triangle, in Quake units.</summary>
    private const float PatchSlabThickness = 2f;

    /// <summary>
    /// Turn one tessellated triangle into a thin convex brush: the triangle's own plane on top, an offset copy
    /// behind it, and three side planes through its edges. Walkable (near-horizontal) triangles get a deeper
    /// skirt so a player standing on a curved floor cannot fall between adjacent slabs.
    /// </summary>
    private static Brush? TryBuildTriangleSlab(Vector3 a, Vector3 b, Vector3 c, int contents, int surfaceFlags, string? texture)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        if (len < 1e-4f)
            return null; // degenerate triangle
        n /= len;

        // A near-horizontal triangle is a floor: give it a deeper skirt for stable standing.
        float thickness = MathF.Abs(n.Z) > 0.7f ? 8f : PatchSlabThickness;

        float dist = Vector3.Dot(a, n);
        var sides = new List<BrushPlane>(5)
        {
            new(n, dist, surfaceFlags, contents, texture),
            new(-n, -(dist - thickness), surfaceFlags, contents, texture),
        };

        // Three side planes, each through an edge and perpendicular to the triangle plane, facing outward.
        AddEdgePlane(sides, a, b, n, surfaceFlags, contents, texture);
        AddEdgePlane(sides, b, c, n, surfaceFlags, contents, texture);
        AddEdgePlane(sides, c, a, n, surfaceFlags, contents, texture);
        if (sides.Count < 5)
            return null;

        BrushPlane[] planes = sides.ToArray();
        Vector3[] points = SlabPoints(a, b, c, n, thickness);
        Vector3[] edgeDirs = ComputeEdgeDirs(planes);
        return new Brush(planes, points, edgeDirs, contents, surfaceFlags, isAabb: false, texture: texture);
    }

    private static void AddEdgePlane(List<BrushPlane> sides, Vector3 p0, Vector3 p1, Vector3 faceNormal,
        int surfaceFlags, int contents, string? texture)
    {
        Vector3 edge = p1 - p0;
        Vector3 outward = Vector3.Cross(edge, faceNormal);
        float len = outward.Length();
        if (len < 1e-5f)
            return;
        outward /= len;
        sides.Add(new BrushPlane(outward, Vector3.Dot(p0, outward), surfaceFlags, contents, texture));
    }

    private static Vector3[] SlabPoints(Vector3 a, Vector3 b, Vector3 c, Vector3 n, float thickness)
    {
        Vector3 d = n * thickness;
        return new[] { a, b, c, a - d, b - d, c - d };
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
