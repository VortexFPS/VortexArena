using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers the entity ops (phase E8): create, move, rotate, delete and key edits on map entities.
///
/// Two behaviours here are easy to get wrong in ways a mapper only discovers much later. Rotating a spawn
/// point has to turn its FACING as well as its position, or you get a spawn in the right place looking the
/// wrong way — invisible until someone spawns there. And a brush entity has no origin at all, so "move the
/// door" can only mean moving the brushes it owns; writing an origin key onto it would be ignored by the
/// compiler and leave the door exactly where it was.
/// </summary>
public class VmapEntityOpTests
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

    private static VmapEntity Point(int id, string className, Vector3 origin)
    {
        var e = new VmapEntity { Id = id, ClassName = className };
        e.Fields["classname"] = className;
        e.SetOrigin(origin);
        return e;
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public void CreateMintsAnEntityWithItsClassnameAndOrigin()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);

        var op = new CreateEntityOp("weapon_devastator", new Vector3(64, 128, 32));
        Assert.True(session.Apply(op));

        VmapEntity e = Assert.Single(doc.Entities);
        Assert.Equal("weapon_devastator", e.ClassName);
        Assert.Equal("weapon_devastator", e.Fields["classname"]);
        Assert.Equal(new Vector3(64, 128, 32), e.Origin());
        Assert.Equal(e.Id, op.CreatedEntityId);
    }

    [Fact]
    public void CreateCarriesExtraFields()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        var fields = new Dictionary<string, string> { ["respawntime"] = "30", ["targetname"] = "gun1" };

        Assert.True(session.Apply(new CreateEntityOp("weapon_vortex", Vector3.Zero, fields)));

        Assert.Equal("30", doc.Entities[0].Fields["respawntime"]);
        Assert.Equal("gun1", doc.Entities[0].Fields["targetname"]);
    }

    [Fact]
    public void UndoingACreate_RemovesTheEntity()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new CreateEntityOp("item_health_mega", Vector3.Zero)));
        Assert.Single(doc.Entities);

        Assert.True(session.Undo());
        Assert.Empty(doc.Entities);

        Assert.True(session.Redo());
        Assert.Single(doc.Entities);
    }

    [Fact]
    public void CreateWithNoClassname_IsRefused()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new CreateEntityOp("", Vector3.Zero)));
    }

    // ---------------------------------------------------------------- move

    [Fact]
    public void MovingAPointEntity_RewritesItsOrigin()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "info_player_deathmatch", new Vector3(0, 0, 24)));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new MoveEntitiesOp(new[] { 1 }, new Vector3(128, -64, 0), doc)));
        Assert.Equal(new Vector3(128, -64, 24), doc.Entities[0].Origin());
    }

    /// <summary>
    /// A brush entity IS its brushes. Moving one must move the geometry, not write an origin key that the
    /// compiler would ignore.
    /// </summary>
    [Fact]
    public void MovingABrushEntity_MovesItsGeometry()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 16, 96), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new MoveEntitiesOp(new[] { 1 }, new Vector3(0, 0, 128), doc)));

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(128f, mins.Z, 2);
        Assert.Equal(224f, maxs.Z, 2);
    }

    /// <summary>
    /// The op resolves the owned geometry at CONSTRUCTION because the journal reads TouchedBrushIds before
    /// Apply runs. Without that the brushes move and undo cannot put them back.
    /// </summary>
    [Fact]
    public void UndoingABrushEntityMove_PutsTheGeometryBack()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 16, 96), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        var session = new VmapEditSession(doc);
        Assert.True(session.Apply(new MoveEntitiesOp(new[] { 1 }, new Vector3(0, 0, 128), doc)));
        Assert.True(session.Undo());

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out _));
        Assert.Equal(0f, mins.Z, 2);
    }

    [Fact]
    public void MovingAMissingEntity_IsRefused()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "info_null", Vector3.Zero));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new MoveEntitiesOp(new[] { 1, 99 }, new Vector3(10, 0, 0), doc)));
        Assert.Equal(Vector3.Zero, doc.Entities[0].Origin());
    }

    // ---------------------------------------------------------------- rotate

    [Fact]
    public void RotatingAnEntity_MovesItAroundThePivot()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "info_player_deathmatch", new Vector3(64, 0, 0)));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateEntitiesOp(new[] { 1 }, Vector3.Zero, 90f)));

        Vector3 o = doc.Entities[0].Origin();
        Assert.Equal(0f, o.X, 2);
        Assert.Equal(64f, o.Y, 2);
    }

    /// <summary>The bug this exists to prevent: a spawn rotated into place but still facing the old way.</summary>
    [Fact]
    public void RotatingAnEntity_AlsoTurnsItsAngleKey()
    {
        var doc = new VmapDocument();
        VmapEntity spawn = Point(1, "info_player_deathmatch", new Vector3(64, 0, 0));
        spawn.Fields["angle"] = "0";
        doc.Entities.Add(spawn);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateEntitiesOp(new[] { 1 }, Vector3.Zero, 90f)));
        Assert.Equal(90f, float.Parse(doc.Entities[0].Fields["angle"],
            System.Globalization.CultureInfo.InvariantCulture), 2);
    }

    [Fact]
    public void TheThreeComponentAnglesKeyWinsOverTheScalarOne()
    {
        var doc = new VmapDocument();
        VmapEntity e = Point(1, "misc_gamemodel", Vector3.Zero);
        e.Fields["angles"] = "10 20 30";
        doc.Entities.Add(e);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateEntitiesOp(new[] { 1 }, Vector3.Zero, 45f)));

        Assert.True(VmapEntity.TryParseVector(doc.Entities[0].Fields["angles"], out Vector3 pyr));
        Assert.Equal(10f, pyr.X, 2);
        Assert.Equal(65f, pyr.Y, 2);    // yaw advanced
        Assert.Equal(30f, pyr.Z, 2);
    }

    [Fact]
    public void AngleWrapsIntoZeroToThreeSixty()
    {
        var doc = new VmapDocument();
        VmapEntity e = Point(1, "info_player_deathmatch", Vector3.Zero);
        e.Fields["angle"] = "300";
        doc.Entities.Add(e);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new RotateEntitiesOp(new[] { 1 }, new Vector3(100, 0, 0), 90f)));
        float yaw = float.Parse(doc.Entities[0].Fields["angle"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(30f, yaw, 2);
    }

    [Fact]
    public void RotatingOnlyBrushEntities_IsRefused()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(32, 32, 32), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.BrushIds.Add(1);
        doc.Entities.Add(door);
        var session = new VmapEditSession(doc);

        // A brush entity's facing lives in its geometry, so there is nothing here for this op to turn.
        Assert.False(session.Apply(new RotateEntitiesOp(new[] { 1 }, Vector3.Zero, 90f)));
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public void DeletingAPointEntity_RemovesIt()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "item_shells", Vector3.Zero));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new DeleteEntitiesOp(new[] { 1 }, doc)));
        Assert.Empty(doc.Entities);

        Assert.True(session.Undo());
        Assert.Single(doc.Entities);
        Assert.Equal("item_shells", doc.Entities[0].ClassName);
    }

    /// <summary>
    /// Deleting a door takes its leaf with it. Leaving the brushes behind would silently promote them into
    /// worldspawn as a solid wall, which is neither outcome the mapper asked for.
    /// </summary>
    [Fact]
    public void DeletingABrushEntity_TakesItsGeometry_AndUndoBringsBothBack()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(0, 0, 0), new Vector3(64, 16, 96), id: 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.BrushIds.Add(1);
        doc.Entities.Add(door);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new DeleteEntitiesOp(new[] { 1 }, doc)));
        Assert.Empty(doc.Entities);
        Assert.Empty(doc.Brushes);

        Assert.True(session.Undo());
        Assert.Single(doc.Entities);
        Assert.Single(doc.Brushes);
        Assert.Equal(1, doc.Entities[0].BrushIds[0]);
    }

    // ---------------------------------------------------------------- keys

    [Fact]
    public void SettingAKeyWritesIt_AndUndoRestoresThePreviousValue()
    {
        var doc = new VmapDocument();
        VmapEntity e = Point(1, "weapon_devastator", Vector3.Zero);
        e.Fields["respawntime"] = "15";
        doc.Entities.Add(e);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SetEntityKeyOp(1, "respawntime", "45")));
        Assert.Equal("45", doc.Entities[0].Fields["respawntime"]);

        Assert.True(session.Undo());
        Assert.Equal("15", doc.Entities[0].Fields["respawntime"]);
    }

    [Fact]
    public void ClearingAKeyRemovesIt()
    {
        var doc = new VmapDocument();
        VmapEntity e = Point(1, "weapon_devastator", Vector3.Zero);
        e.Fields["targetname"] = "gun1";
        doc.Entities.Add(e);
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SetEntityKeyOp(1, "targetname", "")));
        Assert.False(doc.Entities[0].Fields.ContainsKey("targetname"));

        Assert.True(session.Undo());
        Assert.Equal("gun1", doc.Entities[0].Fields["targetname"]);
    }

    [Fact]
    public void SettingClassnameKeepsTheHoistedPropertyInStep()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "weapon_vortex", Vector3.Zero));
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SetEntityKeyOp(1, "classname", "weapon_devastator")));
        Assert.Equal("weapon_devastator", doc.Entities[0].ClassName);
        Assert.Equal("weapon_devastator", doc.Entities[0].Fields["classname"]);
    }

    /// <summary>An entity with no classname is not spawnable and would be dropped on the next load.</summary>
    [Fact]
    public void ClearingClassname_IsRefused()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "weapon_vortex", Vector3.Zero));
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new SetEntityKeyOp(1, "classname", "")));
        Assert.Equal("weapon_vortex", doc.Entities[0].ClassName);
    }

    [Fact]
    public void SettingAKeyToItsCurrentValue_JournalsNothing()
    {
        var doc = new VmapDocument();
        VmapEntity e = Point(1, "weapon_vortex", Vector3.Zero);
        e.Fields["respawntime"] = "15";
        doc.Entities.Add(e);
        var session = new VmapEditSession(doc);

        Assert.False(session.Apply(new SetEntityKeyOp(1, "respawntime", "15")));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void EditingAMissingEntity_IsRefused()
    {
        var doc = new VmapDocument();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new SetEntityKeyOp(99, "respawntime", "10")));
    }

    // ---------------------------------------------------------------- round trip

    /// <summary>
    /// Origins are written in the Quake entity-lump format the readers expect, so a value written by the
    /// editor has to parse back to what was set — with the INVARIANT culture, or a comma-decimal locale writes
    /// "16,5 0 0" and the next load reads garbage.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(16.5f, -32.25f, 128f)]
    [InlineData(-2048f, 4096f, -64f)]
    public void OriginRoundTripsThroughTheKey(float x, float y, float z)
    {
        var e = new VmapEntity { Id = 1, ClassName = "info_null" };
        var want = new Vector3(x, y, z);
        e.SetOrigin(want);

        Assert.Equal(want, e.Origin());
    }
}
