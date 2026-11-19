using System.Numerics;
using VortexArena.Common.Gameplay;
using Xunit;

namespace VortexArena.Tests;

public sealed class ViewLocationCameraTests
{
    [Fact]
    public void CloseSnapOverridesCombinedFlagsAndKeepsHistoryAcrossExit()
    {
        var flags = ViewLocationFlags.Track | ViewLocationFlags.NoAnchorAngle | ViewLocationFlags.HardSnap
            | ViewLocationFlags.CloseSnap | ViewLocationFlags.UnlockPitch | ViewLocationFlags.FreeAim;
        var rail = new ViewLocationState(1, flags, true, Vector3.Zero, -Vector3.UnitY, new(15, 200, 5));
        float yaw = 0f;
        var belowThreshold = new Vector3(64, 44, -59);

        var first = rail.ResolveCamera(belowThreshold, ref yaw);
        Assert.Equal(0f, first.Angles.Y);
        Assert.InRange(first.Angles.X, 36f, 38f);

        Vector3 crossing = new(24, 65, -59);
        Assert.InRange(rail.ResolveCamera(crossing, ref yaw).Angles.Y, 69f, 71f);
        Assert.Equal(90f, yaw);
        Assert.Equal(90f, rail.ResolveCamera(crossing, ref yaw).Angles.Y);
        Assert.Equal(90f, rail.ResolveCamera(belowThreshold, ref yaw).Angles.Y);

        // An intervening fixed-angle region leaves the close-snap history alone.
        var other = rail with { TriggerId = 2, Flags = ViewLocationFlags.None };
        Assert.Equal(200f, other.ResolveCamera(belowThreshold, ref yaw).Angles.Y);
        Assert.Equal(90f, rail.ResolveCamera(belowThreshold, ref yaw).Angles.Y);

        var coincident = rail.ResolveCamera(Vector3.Zero, ref yaw);
        Assert.Equal(new Vector3(270, 0, 0), coincident.Angles);
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void FreeAimUnprojectsFromTheCameraWithAxialDepthAndSeparateScreenAxes()
    {
        Vector3 origin = new(5, 10, 15);
        var frustum = new Vector2(1.25f, 0.75f);
        Assert.Equal(origin + new Vector3(100, 0, 0),
            ViewLocationState.FreeAimRayEnd(origin, Vector3.Zero, new Vector2(0.5f), frustum, 100f));
        Assert.Equal(origin + new Vector3(100, -125, 75),
            ViewLocationState.FreeAimRayEnd(origin, Vector3.Zero, new Vector2(1f, 0f), frustum, 100f));
    }

    [Theory]
    [InlineData(-40, 50, -90, -45)]
    [InlineData(40, -30, 90, 45)]
    [InlineData(0, 10, 0, -90)]
    public void FreeAimUsesTheProjectedHitOnThePlayersPlaneAndEyeHeight(int y, int z, int yaw, int pitch)
    {
        var origin = new Vector3(100, 0, -10);
        var aim = ViewLocationState.FreeAimAngles(origin, new Vector3(0, 0, 20), new Vector3(650, y, z));
        Assert.Equal(new Vector3(pitch, yaw, 0), aim);
    }
}
