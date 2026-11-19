using System.Numerics;
using VortexArena.Common.Gameplay;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

public sealed class OnslaughtDestructionTests
{
    [Fact]
    public void CameraKeepsFirstBestDirectionAndStopsAtClearTrace()
    {
        var visited = new List<float>();
        float yaw = OnslaughtDestruction.ChooseYaw(angle =>
        {
            visited.Add(angle);
            return angle is 90 or 180 ? 0.7f : angle == 135 ? 1f : 0.2f;
        });

        Assert.Equal(135f, yaw);
        Assert.Equal(new float[] { 0, 90, 180, 270, 45, 135 }, visited);
        Assert.Equal(90f, OnslaughtDestruction.ChooseYaw(angle => angle is 90 or 180 ? 0.7f : 0.2f));
    }

    [Fact]
    public void StockPulseCadenceHasTwoShockwavesAndOneFinalBurst()
    {
        int[] pulses = Enumerable.Range(0, OnslaughtDestruction.PulseCount).ToArray();
        Assert.Equal(new[] { 0, 20 }, pulses.Where(OnslaughtDestruction.IsShockwave));
        Assert.Equal(39, Assert.Single(pulses, OnslaughtDestruction.IsFinal));
        Assert.Equal(2f, pulses.Length * OnslaughtDestruction.PulseInterval);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WireCarriesWorldSpaceCameraAndEffectTimeAndCanClear(bool showCamera)
    {
        var expected = new OnslaughtDestruction(new Vector3(20.05f, -40, 72), 315, 5.25f);
        var writer = new BitWriter();
        OnslaughtDestructionWire.Write(writer, expected, showCamera);
        var reader = new BitReader(writer.WrittenSpan);
        Assert.True(OnslaughtDestructionWire.TryRead(ref reader, out var state, out bool camera));
        Assert.Equal(expected, state);
        Assert.Equal(showCamera, camera);

        var truncated = new BitReader(writer.WrittenSpan[..^1]);
        Assert.False(OnslaughtDestructionWire.TryRead(ref truncated, out _, out _));

        writer.Reset();
        OnslaughtDestructionWire.Write(writer, null, showCamera);
        reader = new BitReader(writer.WrittenSpan);
        Assert.True(OnslaughtDestructionWire.TryRead(ref reader, out state, out camera));
        Assert.Null(state);
        Assert.False(camera);
    }
}
