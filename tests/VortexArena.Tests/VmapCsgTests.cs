using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers CSG: subtract, hollow, room and merge (backlog F5, F6).
///
/// The properties worth pinning are geometric rather than structural. A carve has to remove exactly the
/// cutter's volume and no more; a room has to be SEALED, which is the thing the obvious implementation gets
/// wrong; and a merge has to refuse anything whose union is not convex, because a merge that quietly invents
/// volume produces a map that looks right in the editor and is solid where it should not be in play.
/// </summary>
public class VmapCsgTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
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

    private static VmapDocument Doc(params VmapBrush[] brushes)
    {
        var doc = new VmapDocument();
        foreach (VmapBrush b in brushes)
            doc.Brushes.Add(b);
        return doc;
    }

    /// <summary>Interiors are Dot(n, p) &lt;= d, so a point is inside when every face agrees.</summary>
    private static bool Inside(VmapBrush b, Vector3 p)
    {
        foreach (VmapFace f in b.Faces)
        {
            Vector3 n = Vector3.Normalize(f.Plane.Normal);
            float d = f.Plane.Dist / f.Plane.Normal.Length();
            if (Vector3.Dot(n, p) - d > 1e-3f)
                return false;
        }
        return true;
    }

    private static bool InsideAny(IEnumerable<VmapBrush> brushes, Vector3 p)
    {
        foreach (VmapBrush b in brushes)
            if (Inside(b, p))
                return true;
        return false;
    }

    // ---------------------------------------------------------------- volume

    [Fact]
    public void VolumeOfACubeIsItsCube()
        => Assert.Equal(64f * 64f * 64f,
            VmapWinding.Volume(Box(Vector3.Zero, new Vector3(64, 64, 64))), 1f);

    [Fact]
    public void VolumeOfANonSolidIsZero()
        => Assert.Equal(0f, VmapWinding.Volume(new VmapBrush()));

    // ---------------------------------------------------------------- subtract

    /// <summary>
    /// The property that says the carve is right: the pieces are exactly the target minus the cutter. Testing
    /// piece COUNT instead would pin an implementation detail — a different but equally correct split order
    /// gives a different count.
    /// </summary>
    [Fact]
    public void SubtractingACentredCubeLeavesTheDifferenceInVolume()
    {
        VmapBrush target = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        VmapBrush cutter = Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 2);

        var pieces = new List<VmapBrush>();
        Assert.Equal(VmapCsg.SubtractOutcome.Carved, VmapCsg.Subtract(target, cutter, pieces));

        float total = 0f;
        foreach (VmapBrush p in pieces)
            total += VmapWinding.Volume(p);

        Assert.Equal(64f * 64f * 64f - 32f * 32f * 32f, total, 64f * 64f * 64f * 1e-3f);

        // And nothing is left inside the hole.
        Assert.False(InsideAny(pieces, new Vector3(32, 32, 32)));
        Assert.True(InsideAny(pieces, new Vector3(8, 8, 8)));
    }

    [Fact]
    public void ACutterThatContainsTheTargetSwallowsIt()
    {
        VmapBrush target = Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 1);
        VmapBrush cutter = Box(Vector3.Zero, new Vector3(64, 64, 64), 2);

        var pieces = new List<VmapBrush>();
        Assert.Equal(VmapCsg.SubtractOutcome.Swallowed, VmapCsg.Subtract(target, cutter, pieces));
        Assert.Empty(pieces);
    }

    [Fact]
    public void ACutterThatMissesReportsDisjoint()
    {
        VmapBrush target = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        VmapBrush far = Box(new Vector3(512, 0, 0), new Vector3(576, 64, 64), 2);

        var pieces = new List<VmapBrush>();
        Assert.Equal(VmapCsg.SubtractOutcome.Disjoint, VmapCsg.Subtract(target, far, pieces));
    }

    /// <summary>
    /// Face-to-face is the case a bounds test gets wrong: the AABBs touch, the solids do not overlap, and a
    /// naive carve would emit a zero-thickness sliver.
    /// </summary>
    [Fact]
    public void ACutterTouchingFaceToFaceReportsDisjoint()
    {
        VmapBrush target = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        VmapBrush flush = Box(new Vector3(64, 0, 0), new Vector3(128, 64, 64), 2);

        var pieces = new List<VmapBrush>();
        Assert.Equal(VmapCsg.SubtractOutcome.Disjoint, VmapCsg.Subtract(target, flush, pieces));
    }

    [Fact]
    public void SubtractCarvesTheTargetInPlaceAndKeepsTheCutter()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 2));
        var session = new VmapEditSession(doc);

        var op = new SubtractBrushesOp(2, new[] { 1 });
        Assert.True(session.Apply(op));

        // The target keeps its id — a live selection and any entity link stay valid.
        Assert.NotNull(doc.FindBrush(1));
        // And the cutter survives, so the same block cuts the next doorway.
        Assert.NotNull(doc.FindBrush(2));
        Assert.NotEmpty(op.CreatedBrushIds);
    }

    /// <summary>Undo is the test that says <c>TouchedBrushIds</c> is complete.</summary>
    [Fact]
    public void UndoingASubtractRestoresTheTargetExactly()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 2));
        var session = new VmapEditSession(doc);

        float before = VmapWinding.Volume(doc.FindBrush(1)!);
        int brushesBefore = doc.Brushes.Count;

        Assert.True(session.Apply(new SubtractBrushesOp(2, new[] { 1 })));
        Assert.True(session.Undo());

        Assert.Equal(brushesBefore, doc.Brushes.Count);
        Assert.Equal(before, VmapWinding.Volume(doc.FindBrush(1)!), 1f);
    }

    [Fact]
    public void ASubtractThatIntersectsNothingIsRefusedAndJournalsNothing()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(512, 0, 0), new Vector3(576, 64, 64), 2));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new SubtractBrushesOp(2, new[] { 1 })));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ASubtractWithNoCutterIsRefused()
    {
        VmapDocument doc = Doc(Box(Vector3.Zero, new Vector3(64, 64, 64), 1));
        Assert.False(new SubtractBrushesOp(99, new[] { 1 }).Apply(doc));
    }

    /// <summary>A carved brush entity must not lose limbs into worldspawn.</summary>
    [Fact]
    public void CarvedPiecesInheritTheirEntityOwner()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 2));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var op = new SubtractBrushesOp(2, new[] { 1 });
        Assert.True(op.Apply(doc));

        Assert.NotEmpty(op.CreatedBrushIds);
        foreach (int id in op.CreatedBrushIds)
            Assert.Contains(id, door.BrushIds);
    }

    [Fact]
    public void ASwallowedTargetIsUnhookedFromItsEntity()
    {
        VmapDocument doc = Doc(
            Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 1),
            Box(Vector3.Zero, new Vector3(64, 64, 64), 2));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        Assert.True(new SubtractBrushesOp(2, new[] { 1 }).Apply(doc));

        Assert.Null(doc.FindBrush(1));
        Assert.DoesNotContain(1, door.BrushIds);
    }

    /// <summary>
    /// A short forced-id list would leave the tail minted from NextBrushId, which can collide with a forced id
    /// further along and hand two brushes the same id on one peer.
    /// </summary>
    [Fact]
    public void AForcedIdListOfTheWrongLengthIsRefused()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 2));

        Assert.False(new SubtractBrushesOp(2, new[] { 1 }, new[] { 900 }).Apply(doc));
        Assert.Equal(2, doc.Brushes.Count);
    }

    /// <summary>Carving is scoped to the cutter's own owner: a world cutter never eats a door.</summary>
    [Fact]
    public void ResolveTargetsStaysWithinTheCuttersOwnEntity()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),       // worldspawn wall
            Box(new Vector3(16, 16, 16), new Vector3(48, 48, 48), 2),   // the cutter, also worldspawn
            Box(new Vector3(8, 8, 8), new Vector3(56, 56, 56), 3));     // a door, overlapping both
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(3);
        doc.Entities.Add(door);

        List<int> targets = SubtractBrushesOp.ResolveTargets(doc, 2, includeToolBrushes: false);

        Assert.Contains(1, targets);
        Assert.DoesNotContain(3, targets);
        Assert.DoesNotContain(2, targets);
    }

    // ---------------------------------------------------------------- hollow / room

    [Fact]
    public void HollowingACubeLeavesAVoidInsideAndSolidWalls()
    {
        VmapBrush cube = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        var walls = new List<VmapBrush>();

        Assert.True(VmapCsg.Shell(cube, 8f, outward: false, walls));
        Assert.Equal(6, walls.Count);

        Assert.False(InsideAny(walls, new Vector3(32, 32, 32)));     // the void
        Assert.True(InsideAny(walls, new Vector3(32, 32, 4)));       // inside the floor slab
        Assert.True(InsideAny(walls, new Vector3(4, 32, 32)));       // inside the -X wall
    }

    /// <summary>
    /// A thickness at or past half the brush collapses the void; every slab then becomes the whole brush and
    /// the "hollow" is solid all the way through, which looks correct in a wireframe.
    /// </summary>
    [Fact]
    public void AThicknessThatCollapsesTheVoidIsRefused()
    {
        VmapBrush cube = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        var walls = new List<VmapBrush>();

        Assert.False(VmapCsg.Shell(cube, 32f, outward: false, walls));
        Assert.False(VmapCsg.Shell(cube, 0f, outward: false, walls));
        Assert.False(VmapCsg.Shell(cube, -8f, outward: false, walls));
    }

    /// <summary>
    /// A room's void is exactly the brush you drew, and the walls grow OUTSIDE it — so the volume you were
    /// looking at when you drew it is the volume you get to stand in.
    /// </summary>
    [Fact]
    public void ARoomKeepsTheOriginalVolumeAsItsVoid()
    {
        VmapBrush cube = Box(Vector3.Zero, new Vector3(256, 256, 128), 1);
        var walls = new List<VmapBrush>();

        Assert.True(VmapCsg.Shell(cube, 16f, outward: true, walls));
        Assert.Equal(6, walls.Count);

        Assert.False(InsideAny(walls, new Vector3(128, 128, 64)));    // dead centre: void
        Assert.False(InsideAny(walls, new Vector3(2, 2, 2)));         // just inside a corner: still void
        Assert.True(InsideAny(walls, new Vector3(128, 128, -8)));     // under the floor: wall
    }

    /// <summary>
    /// The leak test, and the reason a room is expand-then-hollow. Growing each face outward and keeping the
    /// outer slab leaves a thickness-square gap along every edge, and the room is open at the corners — which
    /// is invisible until something falls through it.
    /// </summary>
    [Fact]
    public void ARoomIsSealedAtItsCorners()
    {
        VmapBrush cube = Box(Vector3.Zero, new Vector3(256, 256, 128), 1);
        var walls = new List<VmapBrush>();
        Assert.True(VmapCsg.Shell(cube, 16f, outward: true, walls));

        // The eight corner regions are exactly where a naive shell leaks.
        foreach (float x in new[] { -8f, 264f })
            foreach (float y in new[] { -8f, 264f })
                foreach (float z in new[] { -8f, 136f })
                    Assert.True(InsideAny(walls, new Vector3(x, y, z)),
                        $"corner ({x}, {y}, {z}) is not inside any wall — the room leaks");
    }

    [Fact]
    public void HollowReplacesTheSourceInPlaceAndUndoRestoresIt()
    {
        VmapDocument doc = Doc(Box(Vector3.Zero, new Vector3(64, 64, 64), 1));
        var session = new VmapEditSession(doc);
        float before = VmapWinding.Volume(doc.FindBrush(1)!);

        var op = new HollowBrushesOp(new[] { 1 }, 8f);
        Assert.True(session.Apply(op));
        Assert.Equal(6, doc.Brushes.Count);
        Assert.Equal(5, op.CreatedBrushIds.Count);

        Assert.True(session.Undo());
        Assert.Single(doc.Brushes);
        Assert.Equal(before, VmapWinding.Volume(doc.FindBrush(1)!), 1f);
    }

    [Fact]
    public void HollowingAMissingBrushIsRefused()
        => Assert.False(new HollowBrushesOp(new[] { 99 }, 8f)
            .Apply(Doc(Box(Vector3.Zero, new Vector3(64, 64, 64), 1))));

    // ---------------------------------------------------------------- merge

    [Fact]
    public void TwoAbuttingCubesMergeIntoOne()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(64, 0, 0), new Vector3(128, 64, 64), 2));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new MergeBrushesOp(new[] { 1, 2 })));

        VmapBrush survivor = Assert.Single(doc.Brushes);
        Assert.Equal(1, survivor.Id);
        Assert.Equal(2f * 64f * 64f * 64f, VmapWinding.Volume(survivor), 1f);
        Assert.Equal(6, survivor.Faces.Count);      // no duplicate coincident plane
    }

    [Fact]
    public void ThreeCollinearCubesMergeIntoOne()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(64, 0, 0), new Vector3(128, 64, 64), 2),
            Box(new Vector3(128, 0, 0), new Vector3(192, 64, 64), 3));

        Assert.True(new MergeBrushesOp(new[] { 1, 2, 3 }).Apply(doc));

        VmapBrush survivor = Assert.Single(doc.Brushes);
        Assert.Equal(3f * 64f * 64f * 64f, VmapWinding.Volume(survivor), 1f);
    }

    /// <summary>An L is the whole reason a volume check is needed: its bounding brush passes convexity.</summary>
    [Fact]
    public void AnLShapedPairIsRefused()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(64, 64, 0), new Vector3(128, 128, 64), 2));

        Assert.False(new MergeBrushesOp(new[] { 1, 2 }).Apply(doc));
        Assert.Equal(2, doc.Brushes.Count);
    }

    [Fact]
    public void CubesWithAGapBetweenThemAreRefused()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(128, 0, 0), new Vector3(192, 64, 64), 2));

        Assert.False(new MergeBrushesOp(new[] { 1, 2 }).Apply(doc));
        Assert.Equal(2, doc.Brushes.Count);
    }

    /// <summary>Merging a detail brush into a structural one silently changes how the map vises.</summary>
    [Fact]
    public void MergingAcrossDetailClassificationIsRefused()
    {
        VmapBrush a = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        VmapBrush b = Box(new Vector3(64, 0, 0), new Vector3(128, 64, 64), 2);
        b.IsDetail = true;

        Assert.False(new MergeBrushesOp(new[] { 1, 2 }).Apply(Doc(a, b)));
    }

    [Fact]
    public void MergingAcrossEntityOwnersIsRefused()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(64, 0, 0), new Vector3(128, 64, 64), 2));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(2);
        doc.Entities.Add(door);

        Assert.False(new MergeBrushesOp(new[] { 1, 2 }).Apply(doc));
        Assert.Equal(2, doc.Brushes.Count);
    }

    [Fact]
    public void MergingOneBrushIsRefused()
        => Assert.False(new MergeBrushesOp(new[] { 1 })
            .Apply(Doc(Box(Vector3.Zero, new Vector3(64, 64, 64), 1))));

    [Fact]
    public void UndoingAMergeBringsBothBrushesBack()
    {
        VmapDocument doc = Doc(
            Box(Vector3.Zero, new Vector3(64, 64, 64), 1),
            Box(new Vector3(64, 0, 0), new Vector3(128, 64, 64), 2));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new MergeBrushesOp(new[] { 1, 2 })));
        Assert.True(session.Undo());

        Assert.Equal(2, doc.Brushes.Count);
        Assert.Equal(64f * 64f * 64f, VmapWinding.Volume(doc.FindBrush(1)!), 1f);
        Assert.Equal(64f * 64f * 64f, VmapWinding.Volume(doc.FindBrush(2)!), 1f);
    }
}
