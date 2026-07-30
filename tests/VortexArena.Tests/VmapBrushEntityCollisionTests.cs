using System.Numerics;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// What a brush entity created in the editor (backlog F4) has to become on the collision side: its geometry
/// out of the static world and into an inline <c>"*N"</c> submodel the server's <c>setmodel</c> can resolve.
///
/// This is the payoff test for assign. Everything else about the op is bookkeeping in the document; whether
/// the door actually MOVES in playtest is decided here.
/// </summary>
public class VmapBrushEntityCollisionTests
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

    private static VmapDocument DocWithBoxes(int count)
    {
        var doc = new VmapDocument();
        for (int i = 0; i < count; i++)
            doc.Brushes.Add(Box(new Vector3(i * 256f, 0, 0), new Vector3(i * 256f + 64f, 64, 64), i + 1));
        return doc;
    }

    [Fact]
    public void AnAssignedBrushBecomesAnInlineSubmodel()
    {
        VmapDocument doc = DocWithBoxes(2);
        var make = new CreateBrushEntityOp("func_door", new[] { 2 }, System.Array.Empty<int>());
        Assert.True(make.Apply(doc));

        BspCollisionBuilder.Result result = VmapCollisionBuilder.Build(doc);

        BspCollisionBuilder.Submodel model = Assert.Single(result.Submodels);
        Assert.StartsWith("*", model.Name, System.StringComparison.Ordinal);
        Assert.NotEmpty(model.Brushes);

        // And the server can find it: the op leaves no model key, the build writes one.
        VmapEntity door = Assert.Single(doc.Entities);
        Assert.Equal(model.Name, door.Fields["model"]);
    }

    /// <summary>
    /// The name is minted from the FREE indices, not positionally. Dissolve an imported entity that held
    /// <c>*2</c>, assign a new one, and a positional counter hands the new one <c>*3</c> — which the imported
    /// entity holding <c>*3</c> already answers to. Both submodels then reach the registry under one name and
    /// one of them silently wins.
    /// </summary>
    [Fact]
    public void AMintedModelNameNeverCollidesWithAnImportedOne()
    {
        VmapDocument doc = DocWithBoxes(3);

        // An imported brush entity that already carries "*1" — the name a positional counter would hand out
        // first to the newly assigned one below.
        var imported = new VmapEntity { Id = 10, ClassName = "func_wall" };
        imported.Fields["classname"] = "func_wall";
        imported.Fields["model"] = "*1";
        imported.BrushIds.Add(3);
        doc.Entities.Add(imported);

        // Authored in the editor, listed FIRST so it is the one the counter reaches first.
        var authored = new VmapEntity { Id = 5, ClassName = "func_door" };
        authored.Fields["classname"] = "func_door";
        authored.BrushIds.Add(2);
        doc.Entities.Insert(0, authored);

        BspCollisionBuilder.Result result = VmapCollisionBuilder.Build(doc);

        Assert.Equal(2, result.Submodels.Count);
        Assert.NotEqual(result.Submodels[0].Name, result.Submodels[1].Name);
        Assert.Equal("*1", imported.Fields["model"]);
        Assert.NotEqual("*1", authored.Fields["model"]);
    }

    /// <summary>
    /// Dissolve is the inverse all the way down: the geometry goes back to the static world rather than
    /// disappearing with the entity.
    /// </summary>
    [Fact]
    public void DissolvingReturnsTheGeometryToTheStaticWorld()
    {
        VmapDocument doc = DocWithBoxes(2);
        var make = new CreateBrushEntityOp("func_door", new[] { 2 }, System.Array.Empty<int>());
        Assert.True(make.Apply(doc));
        Assert.Single(VmapCollisionBuilder.Build(doc).Submodels);

        Assert.True(new DissolveBrushEntityOp(new[] { make.CreatedEntityId }, doc).Apply(doc));

        BspCollisionBuilder.Result after = VmapCollisionBuilder.Build(doc);
        Assert.Empty(after.Submodels);
        Assert.Equal(2, doc.Brushes.Count);
    }
}
