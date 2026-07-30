using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The grid-size ladder (backlog T3/T4) and the snap contract it feeds.
///
/// Lives here rather than beside <c>EditorGrid</c> because the tests cannot reference the Godot layer — which
/// is also the reason the ladder itself is in <see cref="VmapEdit"/>. It is exactly the kind of arithmetic
/// that looks obviously right and is off by one rung.
/// </summary>
public class VmapGridTests
{
    [Theory]
    [InlineData(64f, +1, 128f)]
    [InlineData(64f, -1, 32f)]
    [InlineData(1f, -1, 1f)]          // clamped at the bottom
    [InlineData(1024f, +1, 1024f)]    // clamped at the top
    [InlineData(2f, -1, 1f)]
    public void StepGridSize_WalksThePowerOfTwoLadder(float from, int dir, float expected)
        => Assert.Equal(expected, VmapEdit.StepGridSize(from, dir, 1f, 1024f), 4);

    [Theory]
    [InlineData(100f, +1, 128f)]      // NOT 200 — an off-ladder size steps onto the ladder
    [InlineData(100f, -1, 64f)]       // NOT 50
    [InlineData(3f, +1, 4f)]
    [InlineData(3f, -1, 2f)]
    public void StepGridSize_SnapsOntoTheLadderFromAnOffLadderSize(float from, int dir, float expected)
        => Assert.Equal(expected, VmapEdit.StepGridSize(from, dir, 1f, 1024f), 4);

    [Fact]
    public void StepGridSize_IsStableUnderRepeatedStepping()
    {
        // Up-then-down from a ladder value returns to where it started, or a mapper nudging the size drifts
        // away from the grid they were working on. Stops BELOW the ceiling on purpose: at the clamp the step
        // has nowhere to go, so up-then-down legitimately lands a rung lower and the round trip is not the
        // property being claimed there.
        float v = 1f;
        while (v < 1024f)
        {
            float up = VmapEdit.StepGridSize(v, +1, 1f, 1024f);
            Assert.True(up > v, $"stepping up from {v} did not move");
            if (up >= 1024f)
                break;
            Assert.Equal(v, VmapEdit.StepGridSize(up, -1, 1f, 1024f), 4);
            v = up;
        }
    }

    [Fact]
    public void StepGridSize_AtTheCeilingStaysThereAndStillStepsBackDown()
    {
        Assert.Equal(1024f, VmapEdit.StepGridSize(1024f, +1, 1f, 1024f), 4);
        Assert.Equal(512f, VmapEdit.StepGridSize(1024f, -1, 1f, 1024f), 4);
    }

    [Fact]
    public void StepGridSize_SurvivesDegenerateBounds()
    {
        Assert.Equal(8f, VmapEdit.StepGridSize(8f, +1, 8f, 8f), 4);      // min == max
        Assert.True(VmapEdit.StepGridSize(64f, +1, 0f, 1024f) > 0f);     // min <= 0 is corrected, not divided by
    }

    [Fact]
    public void SnapToGrid_WithZeroGrid_IsIdentity()
    {
        // The contract EffectiveGridSnap relies on: it returns 0 when alignment is off, and every snap call
        // has to treat that as "leave it exactly where the mapper put it" rather than collapsing to the origin.
        var v = new Vector3(13.7f, -101.25f, 0.5f);
        Assert.Equal(v, VmapEdit.SnapToGrid(v, 0f));
        Assert.Equal(v, VmapEdit.SnapToGrid(v, -16f));
        Assert.Equal(13.7f, VmapEdit.SnapToGrid(13.7f, 0f), 4);
    }

    [Fact]
    public void SnapToGrid_QuantizesToTheNearestMultiple()
    {
        Assert.Equal(16f, VmapEdit.SnapToGrid(13.7f, 16f), 4);
        Assert.Equal(0f, VmapEdit.SnapToGrid(7.9f, 16f), 4);
        Assert.Equal(-16f, VmapEdit.SnapToGrid(-13.7f, 16f), 4);
        Assert.Equal(new Vector3(64, -128, 0), VmapEdit.SnapToGrid(new Vector3(60f, -130f, 3f), 64f));
    }
}
