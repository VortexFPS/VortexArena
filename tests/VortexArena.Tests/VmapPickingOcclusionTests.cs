using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="VmapPicking.IsOccluded"/> — the test behind the editor's depth-checked entity boxes
/// (backlog T1).
///
/// The interesting cases are the ones where "is there geometry in the way" and "is there something the mapper
/// can SEE in the way" give different answers. A Xonotic map wraps its architecture in caulk and fills its
/// dead space with nodraw structural brushes, so a naive solid test would report almost everything as
/// occluded — including entities standing in plain sight inside a caulked shell.
/// </summary>
public class VmapPickingOcclusionTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id = 1, string material = "textures/test/wall")
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = material,
            Projection = VmapTexProjection.AxialFor(n),
        });
        Face(new Vector3(1, 0, 0), maxs.X);
        Face(new Vector3(-1, 0, 0), -mins.X);
        Face(new Vector3(0, 1, 0), maxs.Y);
        Face(new Vector3(0, -1, 0), -mins.Y);
        Face(new Vector3(0, 0, 1), maxs.Z);
        Face(new Vector3(0, 0, -1), -mins.Z);
        return b;
    }

    /// <summary>A flat 3x3 patch spanning x/y at a given z — a curved surface's simplest case.</summary>
    private static VmapPatch FlatPatch(float z, float half = 128f, int id = 1)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * half, (row - 1) * half, z));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    private static VmapPickIndex IndexOf(VmapDocument doc, bool includeToolBrushes = false)
    {
        var index = new VmapPickIndex();
        index.EnsureBuilt(doc, 0, includeToolBrushes);
        return index;
    }

    // A wall in the y = [-16, 16] slab, spanning enough x/z to be crossed by a ray along +y.
    private static VmapDocument DocWithWall(string material = "textures/test/wall")
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-256, -16, -256), new Vector3(256, 16, 256), 1, material));
        return doc;
    }

    [Fact]
    public void GeometryBetweenTheTwoPointsOccludes()
    {
        VmapPickIndex index = IndexOf(DocWithWall());

        Assert.True(VmapPicking.IsOccluded(index, new Vector3(0, -128, 0), new Vector3(0, 128, 0)));
    }

    [Fact]
    public void NothingBetweenTheTwoPointsDoesNotOcclude()
    {
        VmapPickIndex index = IndexOf(DocWithWall());

        // Both points on the far side of the wall: the ray never reaches it.
        Assert.False(VmapPicking.IsOccluded(index, new Vector3(0, 64, 0), new Vector3(0, 128, 0)));
    }

    [Fact]
    public void GeometryToTheSideDoesNotOcclude()
    {
        VmapPickIndex index = IndexOf(DocWithWall());

        // Parallel to the wall, 128 units clear of it.
        Assert.False(VmapPicking.IsOccluded(index, new Vector3(-128, 128, 0), new Vector3(128, 128, 0)));
    }

    /// <summary>
    /// The case that decides whether the feature is usable at all: caulk and nodraw draw nothing, so they must
    /// not hide what is behind them. Every Xonotic room is inside such a shell.
    /// </summary>
    [Fact]
    public void AnInvisibleShaderDoesNotOcclude()
    {
        VmapPickIndex index = IndexOf(DocWithWall("textures/common/caulk"));

        Assert.False(VmapPicking.IsOccluded(index, new Vector3(0, -128, 0), new Vector3(0, 128, 0)));
    }

    [Fact]
    public void ANodrawFaceDoesNotOcclude()
    {
        var doc = new VmapDocument();
        VmapBrush b = Box(new Vector3(-256, -16, -256), new Vector3(256, 16, 256));
        foreach (VmapFace f in b.Faces)
            f.SurfaceFlags = 0x0080;   // Q3SURFACEFLAG_NODRAW
        doc.Brushes.Add(b);

        Assert.False(VmapPicking.IsOccluded(IndexOf(doc), new Vector3(0, -128, 0), new Vector3(0, 128, 0)));
    }

    /// <summary>
    /// Tool geometry is invisible by policy, not by shader — so when the mapper has explicitly asked to see and
    /// grab it, it blocks like anything else they can see.
    /// </summary>
    [Fact]
    public void AnInvisibleShaderOccludesWhenToolBrushesAreIncluded()
    {
        VmapPickIndex index = IndexOf(DocWithWall("textures/common/caulk"), includeToolBrushes: true);

        Assert.True(VmapPicking.IsOccluded(index, new Vector3(0, -128, 0), new Vector3(0, 128, 0)));
    }

    /// <summary>
    /// The bias exists for the commonest arrangement there is: an entity resting on a floor, whose box centre
    /// ends up level with — or a hair inside — the surface it stands on. Without it that surface hides it.
    /// </summary>
    [Fact]
    public void TheSurfaceTheTargetSitsOnDoesNotOccludeIt()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-256, -256, -32), new Vector3(256, 256, 0)));   // floor, top at z = 0

        // Looking straight down at a point one unit INTO the floor: the surface is between the eye and the
        // target, but only just, and it is the very surface the entity is standing on.
        Assert.False(VmapPicking.IsOccluded(
            IndexOf(doc), new Vector3(0, 0, 256), new Vector3(0, 0, -1), bias: 4f));

        // With no bias the same ray does reach the floor, which is what proves the bias is doing the work.
        Assert.True(VmapPicking.IsOccluded(
            IndexOf(doc), new Vector3(0, 0, 256), new Vector3(0, 0, -1), bias: 0f));
    }

    [Fact]
    public void ATargetAtTheEyeIsNeverOccluded()
    {
        VmapPickIndex index = IndexOf(DocWithWall());
        var eye = new Vector3(0, -128, 0);

        Assert.False(VmapPicking.IsOccluded(index, eye, eye));
    }

    /// <summary>A curve is a surface a mapper can see, so it blocks — it has no plane set, hence its own path.</summary>
    [Fact]
    public void APatchOccludes()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch(z: 0f));

        Assert.True(VmapPicking.IsOccluded(IndexOf(doc), new Vector3(0, 0, -128), new Vector3(0, 0, 128)));
    }

    [Fact]
    public void APatchToTheSideDoesNotOcclude()
    {
        var doc = new VmapDocument();
        doc.Patches.Add(FlatPatch(z: 0f, half: 16f));

        // Ray passes 512 units clear of the patch's 32-unit span.
        Assert.False(VmapPicking.IsOccluded(
            IndexOf(doc), new Vector3(512, 0, -128), new Vector3(512, 0, 128)));
    }

    /// <summary>
    /// Entities never occlude each other. A rack of pickups on one wall would otherwise show only the box
    /// nearest the camera, which is the opposite of what boxing them is for.
    /// </summary>
    [Fact]
    public void EntitiesDoNotOccludeEachOther()
    {
        var doc = new VmapDocument();
        for (int i = 0; i < 4; i++)
        {
            var e = new VmapEntity { Id = i + 1, ClassName = "item_health_medium" };
            e.Fields["classname"] = "item_health_medium";
            e.SetOrigin(new Vector3(0, i * 32f, 0));
            doc.Entities.Add(e);
        }

        VmapPickIndex index = IndexOf(doc);
        Assert.Equal(4, index.Entities.Count);
        Assert.False(VmapPicking.IsOccluded(index, new Vector3(0, -128, 0), new Vector3(0, 128, 0)));
    }

    /// <summary>
    /// The property the segment broadphase has to hold: narrowing the candidate set must not change a single
    /// answer. Boxes on a lattice, segments across them, and an analytic reference — a convex box occludes
    /// exactly when the ray ENTERS it within reach, which is a slab test the grid plays no part in.
    ///
    /// This is the test that catches a cell walk which skips a cell. A missed cell is invisible in every hand-
    /// written case (they all cross one or two cells) and shows up in play as entities flickering back into
    /// view along one diagonal.
    /// </summary>
    [Fact]
    public void TheBroadphaseAgreesWithAnExhaustiveTest()
    {
        // Deterministic: a seed that fails must fail again.
        var rng = new Random(20260729);
        var doc = new VmapDocument();
        var boxes = new List<(Vector3 Mins, Vector3 Maxs)>();

        for (int i = 0; i < 200; i++)
        {
            var mins = new Vector3(
                rng.Next(-16, 16) * 64f, rng.Next(-16, 16) * 64f, rng.Next(-4, 4) * 64f);
            Vector3 maxs = mins + new Vector3(
                rng.Next(1, 5) * 64f, rng.Next(1, 5) * 64f, rng.Next(1, 3) * 64f);
            doc.Brushes.Add(Box(mins, maxs, i + 1));
            boxes.Add((mins, maxs));
        }

        VmapPickIndex index = IndexOf(doc);
        int agreed = 0, occluded = 0;

        for (int q = 0; q < 500; q++)
        {
            // Half-unit offsets keep endpoints and rays off the lattice, so no case sits exactly on a face.
            var from = new Vector3(
                rng.Next(-1200, 1200) + 0.5f, rng.Next(-1200, 1200) + 0.5f, rng.Next(-300, 300) + 0.5f);
            var to = new Vector3(
                rng.Next(-1200, 1200) + 0.5f, rng.Next(-1200, 1200) + 0.5f, rng.Next(-300, 300) + 0.5f);

            float length = (to - from).Length();
            if (length < 1f)
                continue;
            float reach = length - 1f;
            Vector3 dir = (to - from) / length;

            bool expected = false;
            bool ambiguous = false;
            foreach ((Vector3 mins, Vector3 maxs) in boxes)
            {
                if (!SlabEntry(from, dir, mins, maxs, out float t))
                    continue;
                // A ray that starts inside a solid sees no front face, exactly as the pick does.
                if (t <= 0f)
                    continue;
                if (MathF.Abs(t - reach) < 1e-2f)
                    ambiguous = true;    // grazing the reach cut-off: not a case worth pinning
                if (t < reach)
                    expected = true;
            }
            if (ambiguous)
                continue;

            Assert.Equal(expected, VmapPicking.IsOccluded(index, from, to));
            agreed++;
            if (expected)
                occluded++;
        }

        // Guard the guard: a run where nothing was ever occluded would pass while testing nothing.
        Assert.True(agreed > 400, $"only {agreed} comparable cases");
        Assert.True(occluded > 50, $"only {occluded} occluded cases — the sample is not exercising the walk");
    }

    /// <summary>Ray/AABB entry distance. Negative when the ray starts inside; false when it misses.</summary>
    private static bool SlabEntry(Vector3 origin, Vector3 dir, Vector3 mins, Vector3 maxs, out float t)
    {
        float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
            float lo = axis == 0 ? mins.X : axis == 1 ? mins.Y : mins.Z;
            float hi = axis == 0 ? maxs.X : axis == 1 ? maxs.Y : maxs.Z;

            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) { t = 0f; return false; }
                continue;
            }
            float ta = (lo - o) / d, tb = (hi - o) / d;
            if (ta > tb)
                (ta, tb) = (tb, ta);
            tmin = MathF.Max(tmin, ta);
            tmax = MathF.Min(tmax, tb);
        }
        t = tmin;
        return tmax >= tmin;
    }

    /// <summary>
    /// The pick's entity filter is what stops a hidden box still being clickable. Two behaviours, one rule —
    /// so they cannot drift apart.
    /// </summary>
    [Fact]
    public void ThePickHonoursTheEntityFilter()
    {
        var doc = new VmapDocument();
        var e = new VmapEntity { Id = 7, ClassName = "item_health_mega" };
        e.Fields["classname"] = "item_health_mega";
        e.SetOrigin(new Vector3(0, 0, 0));
        doc.Entities.Add(e);

        VmapPickIndex index = IndexOf(doc);
        var from = new Vector3(0, -256, 0);
        var dir = new Vector3(0, 1, 0);

        VmapPickResult open = VmapPicking.Pick(index, from, dir, VmapSelectionKind.Entity);
        Assert.True(open.Hit);
        Assert.Equal(7, open.Selection.EntityId);

        VmapPickResult filtered = VmapPicking.Pick(
            index, from, dir, VmapSelectionKind.Entity, entityFilter: _ => false);
        Assert.False(filtered.Hit);
    }
}
