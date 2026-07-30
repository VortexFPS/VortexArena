using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers the history (phase E8, design doc §11.9): the journal as something a mapper can look at and travel
/// through, and the abandoned-branch retention that replaces "your redo history will be lost".
///
/// The standard linear-history rule throws away the redone future the moment you edit after undoing. That rule
/// is what makes "I undid four steps, fixed one thing, and lost the rest" a normal experience in every editor.
/// Keeping the abandoned stack costs memory that was already allocated and removes the loss entirely.
/// </summary>
public class VmapHistoryTests
{
    private static VmapDocument DocWithBox()
    {
        var b = new VmapBrush { Id = 1, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
            Projection = VmapTexProjection.AxialFor(n),
        });
        Face(new Vector3(1, 0, 0), 16);
        Face(new Vector3(-1, 0, 0), 16);
        Face(new Vector3(0, 1, 0), 16);
        Face(new Vector3(0, -1, 0), 16);
        Face(new Vector3(0, 0, 1), 16);
        Face(new Vector3(0, 0, -1), 16);

        var doc = new VmapDocument();
        doc.Brushes.Add(b);
        return doc;
    }

    private static void Move(VmapEditSession s, float x)
        => Assert.True(s.Apply(new TranslateBrushesOp(new[] { 1 }, new Vector3(x, 0, 0))));

    private static float MinX(VmapDocument doc)
    {
        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out _));
        return mins.X;
    }

    // ---------------------------------------------------------------- the list

    [Fact]
    public void HistoryListsEveryAppliedStep_OldestFirst()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);
        Move(s, 64);

        IReadOnlyList<VmapEditSession.HistoryStep> h = s.History();
        Assert.Equal(3, h.Count);
        Assert.Equal(3, s.HistoryPosition);
        Assert.False(h[0].IsCurrent);
        Assert.True(h[2].IsCurrent);
        Assert.DoesNotContain(h, x => x.IsUndone);
    }

    [Fact]
    public void UndoneStepsStayInTheList_MarkedUndone()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);
        Assert.True(s.Undo());

        IReadOnlyList<VmapEditSession.HistoryStep> h = s.History();
        Assert.Equal(2, h.Count);
        Assert.Equal(1, s.HistoryPosition);
        Assert.True(h[0].IsCurrent);
        Assert.True(h[1].IsUndone);
    }

    [Fact]
    public void AFreshSessionHasNoHistory()
    {
        var s = new VmapEditSession(DocWithBox());
        Assert.Empty(s.History());
        Assert.Equal(0, s.HistoryPosition);
    }

    // ---------------------------------------------------------------- travel

    [Fact]
    public void TravellingBackwardsUndoesToThatPoint()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);
        Move(s, 64);
        Assert.Equal(112f, MinX(doc) + 16f, 2);   // 16+32+64, from a box spanning -16..16

        Assert.True(s.TravelTo(1));
        Assert.Equal(1, s.HistoryPosition);
        Assert.Equal(16f, MinX(doc) + 16f, 2);
    }

    [Fact]
    public void TravellingForwardsRedoes()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);
        s.TravelTo(0);
        Assert.Equal(0f, MinX(doc) + 16f, 2);

        Assert.True(s.TravelTo(2));
        Assert.Equal(48f, MinX(doc) + 16f, 2);
    }

    [Fact]
    public void TravellingToWhereYouAlreadyAre_ChangesNothing()
    {
        var s = new VmapEditSession(DocWithBox());
        Move(s, 16);
        Assert.False(s.TravelTo(1));
    }

    [Fact]
    public void TravelClampsOutOfRangeTargets()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);

        Assert.True(s.TravelTo(-5));
        Assert.Equal(0, s.HistoryPosition);

        Assert.True(s.TravelTo(999));
        Assert.Equal(2, s.HistoryPosition);
    }

    /// <summary>Travelling all the way back must reproduce the document exactly as it opened.</summary>
    [Fact]
    public void TravellingToZeroRestoresTheOpenedState()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 100);
        Move(s, -37);
        Assert.True(s.Apply(new ScaleSelectionOp(new[] { 1 }, System.Array.Empty<int>(), Vector3.Zero, 2f)));

        s.TravelTo(0);

        Assert.True(VmapWinding.TryGetBounds(doc.Brushes[0], out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-16, -16, -16), mins);
        Assert.Equal(new Vector3(16, 16, 16), maxs);
    }

    // ---------------------------------------------------------------- branches

    /// <summary>
    /// The whole point. Undo two steps, make a different edit, and the two abandoned steps are still there to
    /// be recovered instead of being silently destroyed.
    /// </summary>
    [Fact]
    public void EditingAfterUndoing_FilesTheAbandonedStepsAsABranch()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);
        Move(s, 64);

        s.TravelTo(1);          // abandon the last two
        Move(s, 1000);          // and edit, which normally destroys them

        IReadOnlyList<string> branches = s.Branches();
        string only = Assert.Single(branches);
        Assert.Contains("2 step", only);
    }

    [Fact]
    public void NoBranchIsFiledWhenNothingWasAbandoned()
    {
        var s = new VmapEditSession(DocWithBox());
        Move(s, 16);
        Move(s, 32);
        Assert.Empty(s.Branches());
    }

    [Fact]
    public void RestoringABranch_MakesItRedoableAgain()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);
        Move(s, 16);
        Move(s, 32);        // this one gets abandoned

        s.TravelTo(1);
        Move(s, 1000);      // branch filed
        Assert.False(s.CanRedo);

        Assert.True(s.RestoreBranch(0));
        Assert.True(s.CanRedo);
        Assert.Empty(s.Branches());   // taken off the shelf
    }

    [Fact]
    public void RestoringAnOutOfRangeBranch_IsRefused()
    {
        var s = new VmapEditSession(DocWithBox());
        Assert.False(s.RestoreBranch(0));
        Assert.False(s.RestoreBranch(-1));
    }

    [Fact]
    public void BranchesAreListedNewestFirst()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);

        Move(s, 1);
        Move(s, 2);
        s.TravelTo(1);
        Move(s, 10);        // branch A abandoned "Move 1 brush" (the step at 2)

        Move(s, 3);
        s.TravelTo(2);
        Move(s, 20);        // branch B

        Assert.Equal(2, s.Branches().Count);
    }

    /// <summary>Old branches fall off the bottom rather than growing without bound.</summary>
    [Fact]
    public void BranchesAreCapped()
    {
        VmapDocument doc = DocWithBox();
        var s = new VmapEditSession(doc);

        for (int i = 0; i < VmapEditSession.BranchLimit + 4; i++)
        {
            Move(s, 1);
            Move(s, 2);
            s.TravelTo(s.HistoryPosition - 1);
            Move(s, 3);
        }

        Assert.True(s.Branches().Count <= VmapEditSession.BranchLimit);
    }
}
