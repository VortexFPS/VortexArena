using System.Numerics;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

public sealed class WarpzoneRemoteViewsTests
{
    private const string Camera = """
        { "classname" "func_camera" "model" "*1" "target" "camera_target" }
        """;
    private const string Target = """
        { "classname" "target_position" "targetname" "camera_target" "origin" "1280 -384 404" "angle" "-44.451908" }
        """;
    private static readonly WarpzoneCamera.Pose CourtfunPose = new(new(1280, -384, 404), new(0, -44.451908f, 0));
    private static readonly MapEntityFilter.GametypeContext Deathmatch = new("dm", false, false);

    [Fact]
    public void UsesEmbeddedPassiveTargetWhenServerReplacesEntities()
    {
        var bsp = new BspData
        {
            Models = new BspModel[2],
            Entities = EntityLumpParser.Parse(Camera + Target.Replace("-44.451908", "90")),
            EmbeddedEntities = EntityLumpParser.Parse(Camera + Target),
        };

        Assert.Equal(CourtfunPose, BspWarpzoneViews.StaticCameras(bsp, Deathmatch)[1]);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("moving")]
    [InlineData("mover target")]
    [InlineData("filtered target")]
    public void ServerSelectedTargetsHaveNoStaticPose(string scenario)
    {
        string camera = scenario == "moving" ? Camera.Replace("\"target\" ", "\"spawnflags\" \"1\" \"target\" ") : Camera;
        string target = scenario switch
        {
            "ambiguous" => Target + Target,
            "mover target" => Target.Replace("target_position", "func_train"),
            "filtered target" => Target.Replace("\"origin\"", "\"gametypefilter\" \"+ctf\" \"origin\""),
            _ => Target,
        };
        var bsp = new BspData { Models = new BspModel[2], Entities = EntityLumpParser.Parse(camera + target) };
        Assert.Empty(BspWarpzoneViews.StaticCameras(bsp, Deathmatch));
    }

    [Fact]
    public void StaticZonesLinkAnAsymmetricPairWithoutInstallingGameplayEdicts()
    {
        const string entities = """
            { "classname" "trigger_warpzone" "model" "*1" "targetname" "destination" "killtarget" "left" }
            { "classname" "trigger_warpzone" "model" "*2" "target" "destination" "killtarget" "right" }
            { "classname" "target_position" "targetname" "left" "origin" "1 2 3" }
            { "classname" "target_position" "targetname" "right" "origin" "4 5 6" "angle" "90" }
            """;
        var bsp = new BspData { Models = new BspModel[3], Entities = EntityLumpParser.Parse(entities) };
        var zones = BspWarpzoneViews.StaticZones(bsp, Deathmatch, new SurfaceService(new ModelService()));
        Assert.Equal(2, zones.Zones.Count);
        Assert.All(zones.Zones, zone =>
        {
            Assert.True(zone.Linked);
            Assert.Null(zone.Trigger!.Touch);
        });
        Assert.Equal(new Vector3(4, 5, 6), zones.Zones[0].OutOrigin);
    }

    [Fact]
    public void CameraPacketDecodesAnOverrideAndAnExplicitlyHiddenMonitor()
    {
        var receiver = new WarpzoneCameraWire.Receiver();
        Assert.True(Receive(receiver, [new(7, CourtfunPose), new(19, null)]));
        Assert.Equal(CourtfunPose, receiver.Poses[7]);
        Assert.Null(receiver.Poses[19]);
    }

    [Fact]
    public void CameraChunksApplyFortyNineEntriesAtomicallyAndSupportAnExplicitClear()
    {
        var receiver = new WarpzoneCameraWire.Receiver();
        Assert.True(Receive(receiver, [new(77, CourtfunPose)]));

        var entries = Enumerable.Range(1, 49)
            .Select(model => new WarpzoneCameraWire.Entry(model, CourtfunPose)).ToArray();
        Assert.True(Receive(receiver, entries, 0));
        Assert.Single(receiver.Poses);
        Assert.True(receiver.Poses.ContainsKey(77));

        Assert.True(Receive(receiver, entries, WarpzoneCameraWire.EntriesPerChunk));
        Assert.Equal(49, receiver.Poses.Count);
        Assert.Equal(CourtfunPose, receiver.Poses[49]);

        Assert.True(Receive(receiver, []));
        Assert.Empty(receiver.Poses);
    }

    [Fact]
    public void InvalidChunksDoNotReplaceAnAppliedOverlay()
    {
        var receiver = new WarpzoneCameraWire.Receiver();
        Assert.True(Receive(receiver, [new(7, CourtfunPose)]));
        Assert.False(Receive(receiver, [new(7, CourtfunPose), new(7, null)]));
        Assert.False(Receive(receiver, [new(19, new WarpzoneCamera.Pose(Vector3.One, new(float.NaN, 0, 0)))]));
        Assert.False(Receive(receiver, [new(0, null)]));
        var entries = Enumerable.Range(1, 49)
            .Select(model => new WarpzoneCameraWire.Entry(model, CourtfunPose)).ToArray();
        Assert.False(Receive(receiver, entries, WarpzoneCameraWire.EntriesPerChunk));
        Assert.True(Receive(receiver, entries));
        entries[^1] = new(48, CourtfunPose);
        Assert.False(Receive(receiver, entries, WarpzoneCameraWire.EntriesPerChunk));
        Assert.Equal(CourtfunPose, receiver.Poses[7]);
        Assert.Single(receiver.Poses);
    }

    [Fact]
    public void RejectsTruncationOverflowAndTrailingData()
    {
        var receiver = new WarpzoneCameraWire.Receiver();
        var writer = new BitWriter();
        WarpzoneCameraWire.WriteChunk(writer, [new(7, CourtfunPose)], 0);
        for (int i = 0; i < writer.WrittenSpan.Length; ++i)
        {
            var reader = new BitReader(writer.WrittenSpan[..i]);
            Assert.False(receiver.Receive(ref reader));
        }

        writer.WriteByte(0);
        var trailing = new BitReader(writer.WrittenSpan);
        Assert.False(receiver.Receive(ref trailing));

        var overflow = new BitReader(new byte[] { 3, WarpzoneCameraWire.EntriesPerChunk + 1 });
        Assert.False(receiver.Receive(ref overflow));
    }

    private static bool Receive(WarpzoneCameraWire.Receiver receiver, WarpzoneCameraWire.Entry[] entries, int start = 0)
    {
        var writer = new BitWriter();
        WarpzoneCameraWire.WriteChunk(writer, entries, start);
        var reader = new BitReader(writer.WrittenSpan);
        return receiver.Receive(ref reader);
    }
}
