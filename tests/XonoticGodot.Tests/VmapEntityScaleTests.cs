using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Scaling a selection that contains entities (backlog T7, reported as B4 "scaling doesn't work").
///
/// The two entity kinds scale differently, and the difference is not arbitrary. A BRUSH entity's position IS
/// the geometry it owns, so scaling it scales those brushes. A POINT entity has no geometry, so what scales
/// is its ORIGIN about the pivot — a selection being scaled spreads its spawn points apart with the walls,
/// which is what the gesture means. Making the entity itself bigger is a modelscale property edit.
/// </summary>
public class VmapEntityScaleTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id)
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

    private static VmapEntity Point(int id, string cls, Vector3 origin)
    {
        var e = new VmapEntity { Id = id, ClassName = cls };
        e.Fields["classname"] = cls;
        e.SetOrigin(origin);
        return e;
    }

    [Fact]
    public void ScalingAPointEntity_MovesItsOriginAboutThePivot()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(5, "info_player_deathmatch", new Vector3(100, 0, 0)));

        var op = new ScaleSelectionOp(
            Array.Empty<int>(), Array.Empty<int>(), Vector3.Zero, new Vector3(2f, 2f, 2f),
            entityIds: new[] { 5 }, doc: doc);

        Assert.True(op.Apply(doc));
        Assert.Equal(new Vector3(200, 0, 0), doc.Entities[0].Origin());
    }

    [Fact]
    public void ScalingABrushEntity_ScalesTheGeometryItOwns()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        var door = new VmapEntity { Id = 9, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        // The entity alone is selected — its brush is reached through ownership, not by being listed.
        var op = new ScaleSelectionOp(
            Array.Empty<int>(), Array.Empty<int>(), Vector3.Zero, new Vector3(2f, 2f, 2f),
            entityIds: new[] { 9 }, doc: doc);

        Assert.Contains(1, op.TouchedBrushIds);          // declared, so undo snapshots it
        Assert.True(op.Apply(doc));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(0f, mins.X, 3);
        Assert.Equal(128f, maxs.X, 3);
    }

    [Fact]
    public void ABrushSelectedDirectlyAndViaItsOwner_IsScaledOnce()
    {
        // Selecting a door AND one of its brushes must not apply the factor twice — the brush would land at
        // the square of the scale, which looks like a wildly overshooting drag.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        var door = new VmapEntity { Id = 9, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var op = new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), Vector3.Zero, new Vector3(2f, 2f, 2f),
            entityIds: new[] { 9 }, doc: doc);

        Assert.Single(op.TouchedBrushIds);
        Assert.True(op.Apply(doc));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 maxs));
        Assert.Equal(128f, maxs.X, 3);                   // 2x, not 4x
    }

    [Fact]
    public void ScalingIsUndoable_ForBothEntityKinds()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(64, 64, 64), id: 1));
        var door = new VmapEntity { Id = 9, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);
        doc.Entities.Add(Point(5, "info_player_deathmatch", new Vector3(100, 0, 0)));

        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new ScaleSelectionOp(
            Array.Empty<int>(), Array.Empty<int>(), Vector3.Zero, new Vector3(2f, 2f, 2f),
            entityIds: new[] { 9, 5 }, doc: doc)));

        Assert.True(session.Undo());

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out _, out Vector3 maxs));
        Assert.Equal(64f, maxs.X, 3);
        Assert.Equal(new Vector3(100, 0, 0), doc.Entities[1].Origin());
    }

    [Fact]
    public void AnEntityOnlyScale_ReplicatesWithItsEntityList()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(5, "info_player_deathmatch", new Vector3(100, 0, 0)));

        var op = new ScaleSelectionOp(
            Array.Empty<int>(), Array.Empty<int>(), Vector3.Zero, new Vector3(2f, 2f, 2f),
            textureLock: true, entityIds: new[] { 5 }, doc: doc);

        string line = VmapOpWire.Serialize(op)!;
        var back = (ScaleSelectionOp)VmapOpWire.Deserialize(line, doc)!;

        Assert.Equal(new[] { 5 }, back.EntityIds);
        Assert.True(back.TextureLock);
        Assert.Equal(line, VmapOpWire.Serialize(back));

        // And a line from before entity scaling existed still decodes, to no entities.
        var legacy = (ScaleSelectionOp)VmapOpWire.Deserialize("scale 1 1 0 0 0 0 0 0 2 2 2")!;
        Assert.Empty(legacy.EntityIds);
    }
}
