using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="VmapVisibility"/> and the group ops behind it (backlog F8, F9).
///
/// The property that matters is that ONE object answers for everything: the renderer and the picker read the
/// same predicate, so the editor cannot grow a brush you can click but not see. Everything here tests the
/// predicate; the two consumers are wired to it in the same change and have nothing of their own to disagree
/// about.
/// </summary>
public class VmapVisibilityTests
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

    private static VmapPatch FlatPatch(int id, Vector3 at, float half = 64f)
    {
        var p = new VmapPatch { Id = id, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(at + new Vector3((col - 1) * half, (row - 1) * half, 0f));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        return p;
    }

    private static VmapEntity Point(int id, string className, Vector3 origin)
    {
        var e = new VmapEntity { Id = id, ClassName = className };
        e.Fields["classname"] = className;
        e.SetOrigin(origin);
        return e;
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void EverythingIsVisibleByDefault()
    {
        var vis = new VmapVisibility();
        Assert.True(vis.IsBrushVisible(Box(Vector3.Zero, new Vector3(64, 64, 64))));
        Assert.True(vis.IsPatchVisible(FlatPatch(1, Vector3.Zero)));
        Assert.True(vis.IsEntityVisible(Point(1, "info_null", Vector3.Zero)));
        Assert.False(vis.HasRegion);
    }

    [Fact]
    public void ToolBrushesAreHiddenUnlessAskedFor()
    {
        var vis = new VmapVisibility();
        VmapBrush caulk = Box(Vector3.Zero, new Vector3(64, 64, 64));
        caulk.IsToolBrush = true;

        Assert.False(vis.IsBrushVisible(caulk));
        vis.IncludeToolBrushes = true;
        Assert.True(vis.IsBrushVisible(caulk));
    }

    // ---------------------------------------------------------------- hide

    [Fact]
    public void AnExplicitlyHiddenObjectIsHidden()
    {
        var vis = new VmapVisibility();
        vis.HiddenBrushIds.Add(7);
        vis.HiddenPatchIds.Add(8);
        vis.HiddenEntityIds.Add(9);

        Assert.False(vis.IsBrushVisible(Box(Vector3.Zero, new Vector3(64, 64, 64), 7)));
        Assert.True(vis.IsBrushVisible(Box(Vector3.Zero, new Vector3(64, 64, 64), 6)));
        Assert.False(vis.IsPatchVisible(FlatPatch(8, Vector3.Zero)));
        Assert.False(vis.IsEntityVisible(Point(9, "info_null", Vector3.Zero)));
    }

    [Fact]
    public void ShowAllHiddenLeavesGroupsAndTheGametypeFilterAlone()
    {
        var vis = new VmapVisibility();
        vis.HiddenBrushIds.Add(1);
        vis.HiddenGroups.Add(4);
        vis.HiddenSubmodels.Add(2);

        vis.ShowAllHidden();

        Assert.Equal(0, vis.ExplicitHiddenCount);
        Assert.Contains(4, vis.HiddenGroups);
        Assert.Contains(2, vis.HiddenSubmodels);
    }

    // ---------------------------------------------------------------- region

    /// <summary>
    /// Anything TOUCHING the box stays visible. Containment would drop the walls of the very room you
    /// regioned, since they straddle its edge.
    /// </summary>
    [Fact]
    public void ARegionKeepsAnythingThatTouchesIt()
    {
        var vis = new VmapVisibility();
        vis.SetRegion(Vector3.Zero, new Vector3(256, 256, 256));

        Assert.True(vis.IsBrushVisible(Box(new Vector3(64, 64, 64), new Vector3(128, 128, 128), 1)));
        Assert.True(vis.IsBrushVisible(Box(new Vector3(-64, 0, 0), new Vector3(64, 64, 64), 2)));
        Assert.False(vis.IsBrushVisible(Box(new Vector3(1024, 0, 0), new Vector3(1088, 64, 64), 3)));
    }

    [Fact]
    public void ARegionAppliesToPatchesAndPointEntities()
    {
        var vis = new VmapVisibility();
        vis.SetRegion(Vector3.Zero, new Vector3(256, 256, 256));

        Assert.True(vis.IsPatchVisible(FlatPatch(1, new Vector3(128, 128, 128))));
        Assert.False(vis.IsPatchVisible(FlatPatch(2, new Vector3(2048, 0, 0))));

        Assert.True(vis.IsEntityVisible(Point(1, "info_null", new Vector3(128, 128, 128))));
        Assert.False(vis.IsEntityVisible(Point(2, "info_null", new Vector3(2048, 0, 0))));
    }

    /// <summary>
    /// A brush entity has no origin of its own — it is in view when its geometry is, and the geometry is
    /// filtered separately. Testing its non-existent origin would hide every door in the map.
    /// </summary>
    [Fact]
    public void ARegionNeverHidesABrushEntityByItsOrigin()
    {
        var vis = new VmapVisibility();
        // Deliberately AWAY from the world origin, which is what a brush entity's absent origin key reads as.
        vis.SetRegion(new Vector3(512, 512, 512), new Vector3(1024, 1024, 1024));

        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);

        Assert.True(vis.IsEntityVisible(door));

        // ...while a POINT entity out there is hidden, so the case above is not passing by accident.
        Assert.False(vis.IsEntityVisible(Point(2, "info_null", Vector3.Zero)));
    }

    [Fact]
    public void ClearingTheRegionShowsEverythingAgain()
    {
        var vis = new VmapVisibility();
        vis.SetRegion(Vector3.Zero, new Vector3(64, 64, 64));
        Assert.False(vis.IsBrushVisible(Box(new Vector3(1024, 0, 0), new Vector3(1088, 64, 64), 1)));

        vis.ClearRegion();
        Assert.False(vis.HasRegion);
        Assert.True(vis.IsBrushVisible(Box(new Vector3(1024, 0, 0), new Vector3(1088, 64, 64), 1)));
    }

    /// <summary>
    /// Every mutation moves the version, because that is what the pick cache keys on. The old test was a
    /// COUNT, which cannot notice one hidden submodel being swapped for another.
    /// </summary>
    [Fact]
    public void EveryMutationMovesTheVersion()
    {
        var vis = new VmapVisibility();
        int start = vis.Version;

        vis.SetRegion(Vector3.Zero, Vector3.One);
        Assert.True(vis.Version > start);

        int afterRegion = vis.Version;
        vis.ClearRegion();
        Assert.True(vis.Version > afterRegion);

        int afterClear = vis.Version;
        vis.HiddenBrushIds.Add(1);
        vis.ShowAllHidden();
        Assert.True(vis.Version > afterClear);
    }

    // ---------------------------------------------------------------- groups

    [Fact]
    public void AHiddenGroupHidesEveryKindOfMember()
    {
        var vis = new VmapVisibility();
        vis.HiddenGroups.Add(3);

        VmapBrush b = Box(Vector3.Zero, new Vector3(64, 64, 64), 1);
        b.GroupId = 3;
        VmapPatch p = FlatPatch(1, Vector3.Zero);
        p.GroupId = 3;
        VmapEntity e = Point(1, "info_null", Vector3.Zero);
        e.GroupId = 3;

        Assert.False(vis.IsBrushVisible(b));
        Assert.False(vis.IsPatchVisible(p));
        Assert.False(vis.IsEntityVisible(e));
    }

    private static VmapDocument DocWithBoxes(int count)
    {
        var doc = new VmapDocument();
        for (int i = 0; i < count; i++)
            doc.Brushes.Add(Box(new Vector3(i * 128f, 0, 0), new Vector3(i * 128f + 64f, 64, 64), i + 1));
        return doc;
    }

    [Fact]
    public void GroupingMintsAGroupAndStampsItsMembers()
    {
        VmapDocument doc = DocWithBoxes(2);
        var session = new VmapEditSession(doc);

        var op = new SetGroupOp("north wing", hidden: false, new[] { 1, 2 },
            Array.Empty<int>(), Array.Empty<int>());
        Assert.True(session.Apply(op));

        VmapGroup g = Assert.Single(doc.Groups);
        Assert.Equal("north wing", g.Name);
        Assert.Equal(op.GroupId, g.Id);
        Assert.Equal(g.Id, doc.FindBrush(1)!.GroupId);
        Assert.Equal(g.Id, doc.FindBrush(2)!.GroupId);
    }

    /// <summary>
    /// Removing an object from a group changes ITS field, and an op only gets to undo what it declared. The
    /// current members have to be folded into the touched set at construction, which is why the op takes a
    /// document.
    /// </summary>
    [Fact]
    public void RemovingAMemberFromAGroupIsUndoable()
    {
        VmapDocument doc = DocWithBoxes(2);
        var session = new VmapEditSession(doc);

        var make = new SetGroupOp("wing", hidden: false, new[] { 1, 2 },
            Array.Empty<int>(), Array.Empty<int>());
        Assert.True(session.Apply(make));

        // Same group, one member fewer.
        Assert.True(session.Apply(new SetGroupOp("wing", hidden: false, new[] { 1 },
            Array.Empty<int>(), Array.Empty<int>(), doc, make.GroupId)));
        Assert.Equal(0, doc.FindBrush(2)!.GroupId);

        Assert.True(session.Undo());
        Assert.Equal(make.GroupId, doc.FindBrush(2)!.GroupId);
    }

    /// <summary>An empty member list IS the dissolve — one op rather than a second verb to keep in step.</summary>
    [Fact]
    public void AnEmptyMemberListDissolvesTheGroup()
    {
        VmapDocument doc = DocWithBoxes(2);
        var session = new VmapEditSession(doc);

        var make = new SetGroupOp("wing", hidden: false, new[] { 1, 2 },
            Array.Empty<int>(), Array.Empty<int>());
        Assert.True(session.Apply(make));

        Assert.True(session.Apply(new SetGroupOp("wing", hidden: false, Array.Empty<int>(),
            Array.Empty<int>(), Array.Empty<int>(), doc, make.GroupId)));

        Assert.Empty(doc.Groups);
        Assert.Equal(0, doc.FindBrush(1)!.GroupId);
        Assert.Equal(0, doc.FindBrush(2)!.GroupId);
    }

    [Fact]
    public void UndoingADissolveBringsTheGroupMembershipBack()
    {
        VmapDocument doc = DocWithBoxes(2);
        var session = new VmapEditSession(doc);

        var make = new SetGroupOp("wing", hidden: false, new[] { 1, 2 },
            Array.Empty<int>(), Array.Empty<int>());
        Assert.True(session.Apply(make));
        Assert.True(session.Apply(new SetGroupOp("wing", hidden: false, Array.Empty<int>(),
            Array.Empty<int>(), Array.Empty<int>(), doc, make.GroupId)));

        Assert.True(session.Undo());

        Assert.Equal(make.GroupId, doc.FindBrush(1)!.GroupId);
        Assert.Equal(make.GroupId, doc.FindBrush(2)!.GroupId);
    }

    [Fact]
    public void MembershipIsExclusive()
    {
        VmapDocument doc = DocWithBoxes(2);
        var a = new SetGroupOp("a", hidden: false, new[] { 1, 2 }, Array.Empty<int>(), Array.Empty<int>());
        Assert.True(a.Apply(doc));

        var b = new SetGroupOp("b", hidden: false, new[] { 2 }, Array.Empty<int>(), Array.Empty<int>(), doc);
        Assert.True(b.Apply(doc));

        Assert.Equal(a.GroupId, doc.FindBrush(1)!.GroupId);
        Assert.Equal(b.GroupId, doc.FindBrush(2)!.GroupId);
        Assert.NotEqual(a.GroupId, b.GroupId);
    }

    [Fact]
    public void CreatingAnEmptyGroupIsRefused()
        => Assert.False(new SetGroupOp("nothing", hidden: false, Array.Empty<int>(),
            Array.Empty<int>(), Array.Empty<int>()).Apply(new VmapDocument()));

    // ---------------------------------------------------------------- persistence

    /// <summary>A map with no groups writes no group records at all.</summary>
    [Fact]
    public void AMapWithNoGroupsWritesNothingExtra()
    {
        string text = VmapText.Write(DocWithBoxes(2));
        Assert.DoesNotContain("\ngrp ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupsAndMembershipSurviveASaveAndLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vmapgroups_" + Guid.NewGuid().ToString("N"));
        try
        {
            VmapDocument doc = DocWithBoxes(2);
            doc.Patches.Add(FlatPatch(1, Vector3.Zero));
            doc.Entities.Add(Point(1, "info_player_deathmatch", new Vector3(0, 0, 24)));

            var op = new SetGroupOp("north wing", hidden: true, new[] { 1 }, new[] { 1 }, new[] { 1 });
            Assert.True(op.Apply(doc));

            string path = Path.Combine(dir, "groups.vmap");
            VmapPackage.Write(doc, path);
            VmapDocument back = VmapPackage.Read(path);

            VmapGroup g = Assert.Single(back.Groups);
            Assert.Equal("north wing", g.Name);
            Assert.True(g.Hidden);
            Assert.Equal(g.Id, back.FindBrush(1)!.GroupId);
            Assert.Equal(g.Id, back.FindPatch(1)!.GroupId);
            Assert.Equal(g.Id, back.FindEntity(1)!.GroupId);
            Assert.Equal(0, back.FindBrush(2)!.GroupId);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Dissolving the last group leaves nothing behind naming it. With sections in separate files this needed
    /// an explicit delete; one file cannot go stale in pieces, which is one of the reasons it is one file.
    /// </summary>
    [Fact]
    public void DissolvingTheLastGroupLeavesNoTrace()
    {
        VmapDocument doc = DocWithBoxes(2);
        var make = new SetGroupOp("wing", hidden: false, new[] { 1 },
            Array.Empty<int>(), Array.Empty<int>());
        Assert.True(make.Apply(doc));
        Assert.Contains("\ngrp ", VmapText.Write(doc), StringComparison.Ordinal);

        Assert.True(new SetGroupOp("wing", hidden: false, Array.Empty<int>(), Array.Empty<int>(),
            Array.Empty<int>(), doc, make.GroupId).Apply(doc));

        string text = VmapText.Write(doc);
        Assert.DoesNotContain("\ngrp ", text, StringComparison.Ordinal);
        Assert.Empty(VmapText.Read(text).Groups);
    }
}
