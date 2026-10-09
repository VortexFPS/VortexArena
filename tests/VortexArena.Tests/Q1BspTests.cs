using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Quake 1 format maps: the reader (<see cref="Q1BspReader"/>), the hull collision
/// (<see cref="Q1HullCollision"/>) against a second transcription of DarkPlaces' functions kept in this
/// file, the trace service and the two legacy worlds on such a map. Every map here is built in code
/// (<see cref="Q1BspFixture"/>); the one test that uses real content skips when it is not on the machine.
/// </summary>
public class Q1BspTests
{
    private readonly ITestOutputHelper _output;
    public Q1BspTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Formats() => new[]
    {
        new object[] { Q1BspFormat.Bsp29 }, new object[] { Q1BspFormat.Bsp2 }, new object[] { Q1BspFormat.Bsp2Rmqe }, new object[] { Q1BspFormat.HalfLife },
    };

    // ---- the reader ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Formats))]
    public void Every_Layout_Of_The_Same_Level_Reads_The_Same(Q1BspFormat format)
    {
        byte[] file = Q1BspFixture.Build(format);
        Assert.True(Q1BspReader.IsQ1Format(file));
        Q1BspData map = Q1BspReader.Read(file);
        Assert.Equal(format, map.Format);
        Assert.Equal(format == Q1BspFormat.HalfLife, map.IsHalfLife);
        Assert.Equal(format is Q1BspFormat.Bsp2 or Q1BspFormat.Bsp2Rmqe, map.IsBsp2);

        // two models: the world and the door; bounds spread by a unit
        Assert.Equal(2, map.Models.Length);
        Assert.Equal(new Vector3(-257, -257, -1), map.Models[0].Mins);
        Assert.Equal(new Vector3(257, 257, 257), map.Models[0].Maxs);
        Assert.Equal(new Vector3(99, -33, -1), map.Models[1].Mins);
        Assert.Equal(new Vector3(133, 33, 97), map.Models[1].Maxs);
        Assert.Equal(15, map.Nodes.Length);
        Assert.Equal(26, map.Leafs.Length);
        Assert.Equal(Q1Contents.Solid, map.Leafs[0].Contents);
        Assert.Equal(new[] { Q1Contents.Sky, Q1Contents.Empty, Q1Contents.Water, Q1Contents.Lava }, map.Leafs.Skip(1).Take(4).Select(l => l.Contents));
        Assert.Equal(-1, map.Leafs[0].Cluster);
        Assert.Equal(0, map.Leafs[1].Cluster);
        Assert.Equal(-1, map.Leafs[25].Cluster); // the door's outside: past the world's vis leafs

        // the tree is walked the way DarkPlaces walks it
        Assert.Equal(Q1Contents.Empty, map.Leafs[map.PointInLeaf(new Vector3(200, 100, 100))].Contents);
        Assert.Equal(Q1Contents.Water, map.Leafs[map.PointInLeaf(new Vector3(200, 100, 20))].Contents);
        Assert.Equal(Q1Contents.Lava, map.Leafs[map.PointInLeaf(new Vector3(120, 100, 20))].Contents);
        Assert.Equal(Q1Contents.Solid, map.Leafs[map.PointInLeaf(new Vector3(-100, 100, 20))].Contents); // under the ramp
        Assert.Equal(Q1Contents.Solid, map.Leafs[map.PointInLeaf(new Vector3(0, 0, -10))].Contents);
        Assert.Equal(Q1Contents.Sky, map.Leafs[map.PointInLeaf(new Vector3(0, 0, 300))].Contents);

        // textures: lower case, embedded pixels, the one that is only a name
        Assert.Equal(new[] { "floor", "wall1", "sky1", "*water0", "*lava1", "+0button", "{grate", "crate" }, map.Textures.Select(t => t.Name));
        Assert.Equal(64 * 64, map.Textures[0].Pixels!.Length);
        Assert.Equal((byte)((5 * 7 + 5) & 255), map.Textures[0].Pixels![5]);
        Assert.Null(map.Textures[7].Pixels);
        Assert.True(map.Textures[7].Present);
        Assert.Equal(format == Q1BspFormat.HalfLife, map.Textures[0].Palette is not null);
        if (map.Textures[0].Palette is { } palette) Assert.Equal(new byte[] { 3, 252, 3 ^ 0x55 }, palette.Skip(9).Take(3));

        // faces: every one has the normal it was made to face with
        Q1Face floor = map.Faces.Single(f => f.Style0 == 0 && f.Style1 == 5);
        Assert.Equal(Vector3.UnitZ, floor.Normal);
        Assert.Equal((-256, -256, 512, 512), (floor.TextureMinS, floor.TextureMinT, floor.ExtentS, floor.ExtentT));
        Assert.Equal((33, 33), (floor.LightWidth, floor.LightHeight));
        Assert.True(floor.Lightmapped);
        Assert.Equal(0, floor.LightOffset);
        Assert.Equal(4, floor.VertexCount);
        foreach (Q1Face face in map.Faces) Assert.InRange(MathF.Abs(face.Normal.Length() - 1), 0, 1e-5f);
        Q1Face ramp = map.Faces.Single(f => MathF.Abs(f.Normal.X - 0.6f) < 1e-4f);
        Assert.InRange(MathF.Abs(ramp.Normal.Z - 0.8f), 0, 1e-5f);
        // a face whose miptex number is past the end takes DarkPlaces' spare texture
        Assert.Contains(map.Faces, f => f.TextureIndex == -1);
        // sky and liquid: TEX_SPECIAL. The sky has no lightmap; a liquid without samples has the white one.
        Q1Face sky = map.Faces.Single(f => f.TextureIndex == 2);
        Assert.False(sky.Lightmapped);
        Q1Face water = map.Faces.Single(f => f.TextureIndex == 3);
        Assert.True(water.WhiteLight);
        Assert.True(water.Lightmapped);
        Assert.Equal(0, water.Style0);
        // the wall whose edges are stored backwards has the same four corners
        Q1Face backwards = map.Faces[2];
        Assert.Equal(Vector3.UnitY, backwards.Normal);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(-256f, map.FaceVertices[backwards.FirstVertex + i].Y));

        // light: three bytes a sample whatever the file held
        Assert.Equal(Q1BspFixture.LightLumpLength * 3, map.LightData.Length);
        int sample = (7 * 33 + 4) * 3;
        byte expected = Q1BspFixture.FloorLight(0, 4, 7);
        if (format == Q1BspFormat.HalfLife) expected >>= 1;
        Assert.Equal(new[] { expected, expected, expected }, map.LightData.Skip(sample).Take(3));
        Assert.Equal(format == Q1BspFormat.HalfLife, map.LightIsColoured);

        // visibility: rows three bytes wide, decompressed
        Assert.Equal(24, map.PvsClusterCount);
        Assert.Equal(3, map.PvsClusterBytes);
        Assert.True(map.HasVis);
        Assert.True(map.ClusterVisible(1, 2));
        Assert.False(map.ClusterVisible(1, 9));
        Assert.False(map.ClusterVisible(9, 1)); // a row of zeros
        Assert.True(map.SupportsWaterAlpha);

        Assert.Equal("worldspawn", map.Entities[0]["classname"]);
        Assert.Equal("*1", map.Entities[1]["model"]);
        Assert.Equal(format == Q1BspFormat.HalfLife ? 4 : 4, map.HullSizes.Length);
        Assert.Equal(format == Q1BspFormat.HalfLife ? new Vector3(-16, -16, -36) : new Vector3(-16, -16, -24), map.HullSizes[1].Mins);
    }

    [Fact]
    public void A_Lit_File_Replaces_The_Light_Only_When_It_Fits_The_Map()
    {
        byte[] file = Q1BspFixture.Build(Q1BspFormat.Bsp29);
        byte[] lit = Q1BspFixture.Lit(Q1BspFixture.LightLumpLength);
        Q1BspData coloured = Q1BspReader.Read(file, lit);
        Assert.True(coloured.LightIsColoured);
        Assert.Equal(new byte[] { 10, 20, 30 }, coloured.LightData.Skip(30).Take(3));
        Assert.Empty(coloured.DeluxeData);

        Q1BspData withDirections = Q1BspReader.Read(file, lit, lit);
        Assert.Equal(lit.Length - 8, withDirections.DeluxeData.Length);

        // one sample short, a wrong version, a wrong magic: DarkPlaces ignores each and so does this
        foreach (byte[] bad in new[] { lit[..^3], Altered(lit, 4, 2), Altered(lit, 0, (byte)'X'), new byte[8], Array.Empty<byte>() })
        {
            Q1BspData white = Q1BspReader.Read(file, bad);
            Assert.False(white.LightIsColoured);
            byte d = Q1BspFixture.FloorLight(0, 10, 0);
            Assert.Equal(new[] { d, d, d }, white.LightData.Skip(30).Take(3));
        }
        // a Half-Life map has its colours in the file and no .lit is looked at
        Q1BspData halfLife = Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.HalfLife), lit);
        Assert.Equal((byte)(Q1BspFixture.FloorLight(0, 10, 0) >> 1), halfLife.LightData[30]);
    }

    private static byte[] Altered(byte[] source, int at, byte value)
    {
        byte[] copy = (byte[])source.Clone();
        copy[at] = value;
        return copy;
    }

    [Fact]
    public void A_Map_Whose_Water_Sees_No_Air_Does_Not_Support_Transparent_Water()
    {
        Assert.True(Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29)).SupportsWaterAlpha);
        Assert.False(Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29, waterSeesAir: false)).SupportsWaterAlpha);
    }

    [Fact]
    public void Files_That_Are_Not_Quake_1_Maps_Are_Told_Apart_By_Their_First_Bytes()
    {
        Assert.False(Q1BspReader.IsQ1Format("IBSP\x2e\0\0\0"u8));
        Assert.False(Q1BspReader.IsQ1Format(new byte[] { 38, 0, 0, 0 })); // Quake 2
        Assert.False(Q1BspReader.IsQ1Format(new byte[] { 29, 0 }));
        Assert.True(Q1BspReader.IsQ1Format("BSP2"u8));
        Assert.True(Q1BspReader.IsQ1Format("2PSB"u8));
        Assert.True(Q1BspReader.IsQ1Format(new byte[] { 30, 0, 0, 0 }));
        Assert.Throws<AssetParseException>(() => Q1BspReader.Read("IBSP\x2e\0\0\0"u8));
        Assert.Throws<AssetParseException>(() => Q1BspReader.Read("BSP2 and then nothing"u8));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_Damaged_Map_Is_Refused_And_Never_Read_Out_Of_Bounds(Q1BspFormat format)
    {
        byte[] good = Q1BspFixture.Build(format);
        // a lump that ends past the file, a lump whose size is not a whole number of records
        Assert.Throws<AssetParseException>(() => Q1BspReader.Read(Altered(good, 4 + 3 * 8 + 4 + 2, 0x7F)));
        (int planesAt, int planesLength) = Q1BspFixture.LumpOf(good, 1);
        byte[] funny = (byte[])good.Clone();
        BitConverter.TryWriteBytes(funny.AsSpan(4 + 1 * 8 + 4), planesLength - 1);
        Assert.Throws<AssetParseException>(() => Q1BspReader.Read(funny));
        _ = planesAt;

        // a node that is its own child: a tracer would never come back from it
        (int nodesAt, _) = Q1BspFixture.LumpOf(good, 5);
        byte[] loop = (byte[])good.Clone();
        if (format is Q1BspFormat.Bsp2 or Q1BspFormat.Bsp2Rmqe) BitConverter.TryWriteBytes(loop.AsSpan(nodesAt + 4), 0);
        else BitConverter.TryWriteBytes(loop.AsSpan(nodesAt + 4), (short)0);
        Assert.Contains("loops", Assert.Throws<AssetParseException>(() => Q1BspReader.Read(loop)).Message);

        // the same in a clipping hull
        (int clipAt, _) = Q1BspFixture.LumpOf(good, 9);
        byte[] clipLoop = (byte[])good.Clone();
        if (format is Q1BspFormat.Bsp2 or Q1BspFormat.Bsp2Rmqe) BitConverter.TryWriteBytes(clipLoop.AsSpan(clipAt + 4), 0);
        else BitConverter.TryWriteBytes(clipLoop.AsSpan(clipAt + 4), (short)0);
        Assert.Contains("loops", Assert.Throws<AssetParseException>(() => Q1BspReader.Read(clipLoop)).Message);

        // a clipping plane that does not exist
        byte[] badPlane = (byte[])good.Clone();
        BitConverter.TryWriteBytes(badPlane.AsSpan(clipAt), 1_000_000);
        Assert.Throws<AssetParseException>(() => Q1BspReader.Read(badPlane));

        // noise: whatever a byte is changed to, the answer is a map or a refusal - and a map that is
        // accepted can be traced through without leaving its arrays
        Random random = new(format.GetHashCode() + 7);
        int accepted = 0;
        for (int round = 0; round < 1500; round++)
        {
            byte[] noisy = (byte[])good.Clone();
            int changes = 1 + random.Next(4);
            for (int c = 0; c < changes; c++)
            {
                // most changes go where the structure is (header and the small lumps), a few anywhere
                int lump = random.Next(15);
                (int at, int length) = Q1BspFixture.LumpOf(good, lump);
                int where = random.Next(4) == 0 || length == 0 ? random.Next(4 + 15 * 8) : at + random.Next(length);
                noisy[where] = (byte)random.Next(256);
            }
            Q1BspData map;
            try { map = Q1BspReader.Read(noisy); }
            catch (AssetParseException) { continue; }
            accepted++;
            Q1HullCollision hulls = new(map);
            for (int t = 0; t < 12; t++)
            {
                Vector3 a = new(random.Next(-300, 300), random.Next(-300, 300), random.Next(-50, 300)), b = new(random.Next(-300, 300), random.Next(-300, 300), random.Next(-50, 300));
                Vector3 mins = t % 3 == 0 ? Vector3.Zero : t % 3 == 1 ? new Vector3(-16, -16, -24) : new Vector3(-32, -32, -24);
                Vector3 maxs = t % 3 == 0 ? Vector3.Zero : t % 3 == 1 ? new Vector3(16, 16, 32) : new Vector3(32, 32, 64);
                for (int model = 0; model < map.Models.Length; model++) hulls.TraceBox(model, a, mins, maxs, b, SuperContents.Solid, out _);
                hulls.PointContents(0, a);
                hulls.LightPoint(a, _ => 1, out _);
            }
        }
        _output.WriteLine($"{format}: {accepted} of 1500 damaged files were still maps");
        Assert.InRange(accepted, 100, 1500);
    }

    // ---- the hull collision against a second transcription --------------------------------------------------

    [Theory]
    [MemberData(nameof(Formats))]
    public void Traces_Agree_With_A_Second_Transcription_Of_DarkPlaces_To_The_Last_Bit(Q1BspFormat format)
    {
        Q1BspData map = Q1BspReader.Read(Q1BspFixture.Build(format, sliverOnFloor: true));
        foreach (bool areaWeighted in new[] { true, false })
            foreach (bool reportsTexture in new[] { true, false })
                foreach (bool outOfSolid in new[] { true, false })
                {
                    Q1HullCollision hulls = new(map, areaWeighted) { LineReportsTexture = reportsTexture, TraceOutOfSolid = outOfSolid };
                    DpQ1Reference reference = new(map, areaWeighted) { LineReportsTexture = reportsTexture, TraceOutOfSolid = outOfSolid, ZeroHullCutoff = hulls.ZeroHullSizeCutoff };
                    Random random = new(1234);
                    (Vector3, Vector3)[] boxes =
                    {
                        (Vector3.Zero, Vector3.Zero), (new(-16, -16, -24), new(16, 16, 32)), (new(-16, -16, -24), new(16, 16, 45)), (new(-32, -32, -24), new(32, 32, 64)),
                        (new(-30, -30, 0), new(30, 30, 48)), (new(-2, -2, -2), new(2, 2, 2)), (new(-5, -5, -5), new(5, 5, 5)), (new(-16, -16, -18), new(16, 16, 18)),
                    };
                    int[] masks = { SuperContents.Solid, SuperContents.Solid | SuperContents.Body | SuperContents.PlayerClip, SuperContents.Solid | SuperContents.Water | SuperContents.Lava | SuperContents.Sky, SuperContents.Opaque, SuperContents.Water };
                    int hits = 0, startSolid = 0, water = 0;
                    for (int i = 0; i < 6000; i++)
                    {
                        Vector3 start = new((float)(random.NextDouble() * 620 - 310), (float)(random.NextDouble() * 620 - 310), (float)(random.NextDouble() * 340 - 40));
                        Vector3 end = (i % 7) switch
                        {
                            0 => start,
                            1 => start + new Vector3(0, 0, -(float)(random.NextDouble() * 300)),
                            2 => start + new Vector3((float)(random.NextDouble() * 40 - 20), (float)(random.NextDouble() * 40 - 20), (float)(random.NextDouble() * 10 - 5)),
                            _ => new((float)(random.NextDouble() * 620 - 310), (float)(random.NextDouble() * 620 - 310), (float)(random.NextDouble() * 340 - 40)),
                        };
                        (Vector3 mins, Vector3 maxs) = boxes[random.Next(boxes.Length)];
                        int mask = masks[random.Next(masks.Length)], model = random.Next(5) == 0 ? 1 : 0;
                        hulls.TraceBox(model, start, mins, maxs, end, mask, out Q1HullTrace ours);
                        DpQ1Reference.Trace theirs = reference.TraceBox(model, start, mins, maxs, end, mask);
                        string what = $"{format} weighted={areaWeighted} texture={reportsTexture} out={outOfSolid} #{i} model {model} {start} -> {end} box {mins}..{maxs} mask {mask:x}";
                        Assert.True(BitConverter.DoubleToInt64Bits(ours.Fraction) == BitConverter.DoubleToInt64Bits(theirs.Fraction), $"fraction {ours.Fraction:R} / {theirs.Fraction:R}: {what}");
                        Assert.True(ours.StartSolid == theirs.StartSolid && ours.AllSolid == theirs.AllSolid && ours.InOpen == theirs.InOpen && ours.InWater == theirs.InWater, "flags: " + what);
                        Assert.True(ours.PlaneNormal == theirs.PlaneNormal && ours.PlaneDist == theirs.PlaneDist, $"plane {ours.PlaneNormal} {ours.PlaneDist} / {theirs.PlaneNormal} {theirs.PlaneDist}: {what}");
                        Assert.True(ours.HitContents == theirs.HitContents && ours.StartContents == theirs.StartContents && ours.HitSurfaceFlags == theirs.HitSurfaceFlags, "contents: " + what);
                        Assert.True(ours.HitTexture == theirs.HitTexture, $"texture {ours.HitTexture} / {theirs.HitTexture}: {what}");
                        Assert.Equal(reference.PointContents(model, start), hulls.PointContents(model, start));
                        if (ours.Fraction < 1) hits++;
                        if (ours.StartSolid) startSolid++;
                        if (ours.InWater) water++;
                    }
                    // the level is varied enough that each kind of answer is exercised
                    Assert.True(hits > 300 && startSolid > 100 && water > 100, $"{hits} hits, {startSolid} starting in solid, {water} in water");
                }
    }

    [Fact]
    public void A_Box_Is_Traced_Through_The_Hull_Its_Width_Selects()
    {
        Q1HullCollision quake = new(Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29)));
        Assert.Equal(0, quake.HullForTrace(new(-4, -4, -4), new(4, 4, 4)));          // 8 < 8.03125: Xonotic's cutoff
        Assert.Equal(1, quake.HullForTrace(new(-5, -5, -5), new(5, 5, 5)));
        Assert.Equal(1, quake.HullForTrace(new(-16, -16, -24), new(16, 16, 45)));
        Assert.Equal(1, quake.HullForTrace(new(-16.04f, -16, -24), new(16.04f, 16, 45))); // "a minor tolerance (the .1)"
        Assert.Equal(2, quake.HullForTrace(new(-16.06f, -16, -24), new(16.06f, 16, 45)));
        Assert.Equal(2, quake.HullForTrace(new(-32, -32, -24), new(32, 32, 64)));
        quake.ZeroHullSizeCutoff = 3; // DarkPlaces' own default
        Assert.Equal(1, quake.HullForTrace(new(-2, -2, -2), new(2, 2, 2)));
        Assert.Equal(0, quake.HullForTrace(new(-1, -1, -1), new(1, 1, 1)));

        Q1HullCollision halfLife = new(Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.HalfLife)));
        Assert.Equal(3, halfLife.HullForTrace(new(-16, -16, -18), new(16, 16, 18)));  // crouched
        Assert.Equal(1, halfLife.HullForTrace(new(-16, -16, -36), new(16, 16, 36)));
        Assert.Equal(1, halfLife.HullForTrace(new(-16, -16, -27), new(16, 16, 27)));  // 54 high: the taller of the two
        Assert.Equal(2, halfLife.HullForTrace(new(-32, -32, -32), new(32, 32, 32)));

        // what an entity of a given size collides as against the map's models
        quake.ZeroHullSizeCutoff = 8.03125f;
        quake.RoundUpToHullSize(new(-5, -5, -5), new(5, 5, 5), out Vector3 lo, out Vector3 hi);
        Assert.Equal((new Vector3(-5, -5, -5), new Vector3(27, 27, 51)), (lo, hi));
        quake.RoundUpToHullSize(new(-30, -30, 0), new(30, 30, 48), out lo, out hi);
        Assert.Equal((new Vector3(-30, -30, 0), new Vector3(34, 34, 88)), (lo, hi));
        quake.RoundUpToHullSize(new(-1, -1, -1), new(1, 1, 1), out lo, out hi);
        Assert.Equal((new Vector3(-1, -1, -1), new Vector3(-1, -1, -1)), (lo, hi));
    }

    [Fact]
    public void Contents_Map_To_The_Flags_DarkPlaces_Gives_Them()
    {
        Assert.Equal(0, Q1HullCollision.SuperContentsFromNative(Q1Contents.Empty));
        Assert.Equal(SuperContents.Solid | SuperContents.Opaque, Q1HullCollision.SuperContentsFromNative(Q1Contents.Solid));
        Assert.Equal(SuperContents.Water, Q1HullCollision.SuperContentsFromNative(Q1Contents.Water));
        Assert.Equal(SuperContents.Slime, Q1HullCollision.SuperContentsFromNative(Q1Contents.Slime));
        Assert.Equal(SuperContents.Lava | SuperContents.NoDrop, Q1HullCollision.SuperContentsFromNative(Q1Contents.Lava));
        Assert.Equal(SuperContents.Sky | SuperContents.NoDrop | SuperContents.Opaque, Q1HullCollision.SuperContentsFromNative(Q1Contents.Sky));
        Assert.Equal(0, Q1HullCollision.SuperContentsFromNative(-9));   // a current, a clip: nothing DarkPlaces knows
        Assert.Equal(Q1Contents.Solid, Q1HullCollision.NativeFromSuperContents(SuperContents.Body));
        Assert.Equal(Q1Contents.Lava, Q1HullCollision.NativeFromSuperContents(SuperContents.Lava | SuperContents.Water));
        Assert.Equal(Q1Contents.Empty, Q1HullCollision.NativeFromSuperContents(SuperContents.PlayerClip));

        Q1HullCollision.TextureCollision("*lava1", true, false, out int contents, out int flags);
        Assert.Equal((SuperContents.Lava | SuperContents.NoDrop, 32), (contents, flags));
        Q1HullCollision.TextureCollision("*slime0", true, false, out contents, out _);
        Assert.Equal(SuperContents.Slime, contents);
        Q1HullCollision.TextureCollision("*teleport", true, false, out contents, out _);
        Assert.Equal(SuperContents.Water, contents);
        Q1HullCollision.TextureCollision("sky4", true, false, out contents, out flags);
        Assert.Equal((SuperContents.Sky | SuperContents.NoDrop | SuperContents.Solid, 4 | 16 | 32 | 131072 | 1024), (contents, flags));
        Q1HullCollision.TextureCollision("wall", true, false, out contents, out flags);
        Assert.Equal((SuperContents.Solid, 0), (contents, flags));
        Q1HullCollision.TextureCollision("NO TEXTURE FOUND", false, true, out contents, out _);
        Assert.Equal(SuperContents.Water, contents);
    }

    [Fact]
    public void A_Face_Without_Area_Has_No_Normal_And_Stops_A_Line_As_It_Does_In_DarkPlaces()
    {
        Q1BspData map = Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29, sliverOnFloor: true));
        Q1Face sliver = map.Faces.Single(f => f.VertexCount == 3);
        Assert.Equal(Vector3.Zero, sliver.Normal);
        // a line that reaches the floor's node away from the floor's own face... there is none: the floor covers
        // the room, and it comes first on the node. So look from below, where the floor is a back face.
        Q1HullCollision hulls = new(map);
        hulls.TraceBox(0, new Vector3(200, 100, -20), Vector3.Zero, Vector3.Zero, new Vector3(200, 100, 20), SuperContents.Solid, out Q1HullTrace up);
        Assert.Equal(0, up.Fraction);
        Assert.Equal(Vector3.Zero, up.PlaneNormal);
        Assert.Equal("floor", up.HitTexture);
        // without it the line from inside the ground comes up into the room unhindered (surfaces are one sided)
        Q1HullCollision plain = new(Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29)));
        plain.TraceBox(0, new Vector3(200, 100, -20), Vector3.Zero, Vector3.Zero, new Vector3(200, 100, 20), SuperContents.Solid, out up);
        Assert.Equal(1, up.Fraction);
        Assert.True(up.StartSolid);
    }

    [Fact]
    public void A_Model_Is_Lit_From_The_Lightmap_Under_It()
    {
        Q1BspData map = Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29));
        Q1HullCollision hulls = new(map);
        // over sample (28, 12) of the floor: x = -256 + 16 * 28, y = 256 - 16 * 12 (t runs along -y)
        Vector3 over = new(-256 + 16 * 28, 256 - 16 * 12, 100);
        Assert.True(hulls.LightPoint(over, _ => 1, out Vector3 light));
        float both = (Q1BspFixture.FloorLight(0, 28, 12) + Q1BspFixture.FloorLight(1, 28, 12)) / 128f;
        Assert.InRange(MathF.Abs(light.X - both), 0, 1e-4f);
        Assert.Equal(light.X, light.Y);
        // each style layer times its current value
        Assert.True(hulls.LightPoint(over, style => style == 5 ? 0 : 2, out light));
        Assert.InRange(MathF.Abs(light.X - 2 * Q1BspFixture.FloorLight(0, 28, 12) / 128f), 0, 1e-4f);
        // half way between two samples: the mean
        Assert.True(hulls.LightPoint(over + new Vector3(8, 0, 0), style => style == 0 ? 1 : 0, out light));
        Assert.InRange(MathF.Abs(light.X - (Q1BspFixture.FloorLight(0, 28, 12) + Q1BspFixture.FloorLight(0, 29, 12)) / 2f / 128f), 0, 1e-4f);
        // over the water the white block of 128 answers; outside the map nothing does
        Assert.True(hulls.LightPoint(new Vector3(200, -100, 200), style => style == 0 ? 0.5f : 9, out light));
        Assert.Equal(new Vector3(0.5f), light);
        Assert.False(hulls.LightPoint(new Vector3(0, 0, -50), _ => 1, out light));
        Assert.Equal(Vector3.Zero, light);
    }

    // ---- through the trace service ----------------------------------------------------------------------------

    private static (TraceService Service, Q1HullCollision Hulls, DoorProvider Door) Service(Vector3 doorOrigin)
    {
        Q1HullCollision hulls = new(Q1BspReader.Read(Q1BspFixture.Build(Q1BspFormat.Bsp29)));
        CollisionWorld world = new() { Hulls = hulls };
        world.BuildGrid();
        DoorProvider door = new(hulls, doorOrigin);
        return (new TraceService(world, door) { DarkPlacesArithmetic = true }, hulls, door);
    }

    // One SOLID_BSP entity showing "*1".
    private sealed class DoorProvider : TraceService.IEntityProvider
    {
        private readonly Q1HullCollision _hulls;
        public readonly Entity Door;
        public DoorProvider(Q1HullCollision hulls, Vector3 origin)
        {
            _hulls = hulls;
            Door = new Entity { Index = 7, Solid = Solid.Bsp, Origin = origin, Mins = hulls.Bsp.Models[1].Mins, Maxs = hulls.Bsp.Models[1].Maxs };
        }
        public IReadOnlyList<Entity> SolidEntities => new[] { Door };
        public void EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
        {
            results.Clear();
            if (CollisionWorld.BoxesOverlap(mins, maxs, Door.Origin + Door.Mins, Door.Origin + Door.Maxs)) results.Add(Door);
        }
        public bool TryGetEntityBrushModel(Entity e, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld)
        {
            localBrushes = Array.Empty<Brush>();
            toWorld = EntityMatrix.Identity;
            return false;
        }
        public bool TryGetEntityHullModel(Entity e, out Q1HullCollision? hulls, out int model, out EntityMatrix toWorld)
        {
            hulls = _hulls;
            model = 1;
            toWorld = EntityMatrix.FromQuakeEntity(e.Origin, e.Angles);
            return true;
        }
    }

    [Fact]
    public void A_Player_Box_Lands_On_The_Floor_Where_The_Arithmetic_Says()
    {
        (TraceService service, _, _) = Service(new Vector3(0, 0, 1000));
        Entity mover = new() { Index = 1 };
        Vector3 start = new(200, 100, 200);
        TraceResult drop = service.Trace(start, new(-16, -16, -24), new(16, 16, 32), new(200, 100, 30), MoveFilter.Normal, mover);
        // down into the water (which stops nothing unless the mask asks) and not yet to the floor
        Assert.Equal(1f, drop.Fraction);
        Assert.True(drop.InOpen);
        Assert.True(drop.InWater);
        drop = service.Trace(start, new(-16, -16, -24), new(16, 16, 32), new(200, 100, -100), MoveFilter.Normal, mover);
        // the hull's floor is at z = 24; the fraction is nudged back by 1/32 of a unit along the move
        double expected = (200.0 - 24 - 0.03125) / 300.0;
        Assert.Equal(expected, service.LastFraction);
        Assert.Equal((float)expected, drop.Fraction);
        Assert.Equal((float)(200 + expected * -300), drop.EndPos.Z);
        Assert.Equal(Vector3.UnitZ, drop.PlaneNormal);
        Assert.Equal(24f, drop.PlaneDist);
        Assert.Null(drop.Ent);
        Assert.Equal("solid", drop.DpHitTextureName);
        Assert.Equal(SuperContents.Solid | SuperContents.Opaque, drop.DpHitContents);
        Assert.False(drop.StartSolid);
        Assert.Equal(0, service.LastStartContents);

        // a miss still names what the last leaf held, as DarkPlaces does
        TraceResult miss = service.Trace(new(200, 100, 200), new(-16, -16, -24), new(16, 16, 32), new(150, 50, 180), MoveFilter.Normal, mover);
        Assert.Equal(1f, miss.Fraction);
        Assert.Equal("*water", miss.DpHitTextureName);
        Assert.Equal(Vector3.Zero, miss.PlaneNormal);

        // contents at a point: the world's hull 0
        Assert.Equal(SuperContents.Water, service.PointContents(new Vector3(200, 100, 20)));
        Assert.Equal(SuperContents.Lava | SuperContents.NoDrop, service.PointContents(new Vector3(120, 100, 20)));
        Assert.Equal(SuperContents.Solid | SuperContents.Opaque, service.PointContents(new Vector3(0, 0, -5)));
        Assert.Equal(0, service.PointContents(new Vector3(200, 100, 100)));
    }

    [Fact]
    public void A_Door_Is_Hit_As_The_Hull_Of_Its_Model_Where_Its_Entity_Stands()
    {
        // the door stands at x 100..132 in the map; its entity has moved it 50 units along +Y and 60 up
        (TraceService service, _, DoorProvider provider) = Service(new Vector3(0, 50, 60));
        Entity mover = new() { Index = 1 };
        TraceResult hit = service.Trace(new(200, 50, 120), new(-16, -16, -24), new(16, 16, 32), new(0, 50, 120), MoveFilter.Normal, mover);
        // hull 1's face of the door is at x = 132 + 16
        double expected = (200.0 - 148 - 0.03125) / 200.0;
        Assert.Equal(expected, service.LastFraction);
        Assert.Same(provider.Door, hit.Ent);
        Assert.Equal(Vector3.UnitX, hit.PlaneNormal);
        Assert.Equal(148f, hit.PlaneDist);
        // moved with the entity: the plane's distance is in world space
        (service, _, provider) = Service(new Vector3(40, 50, 60));
        hit = service.Trace(new(250, 50, 120), new(-16, -16, -24), new(16, 16, 32), new(0, 50, 120), MoveFilter.Normal, mover);
        Assert.Equal(188f, hit.PlaneDist);
        Assert.Equal((250.0 - 188 - 0.03125) / 250.0, service.LastFraction);

        // a point inside it is in solid; MOVE_WORLDONLY does not see it; a line names the face's texture
        Assert.Equal(SuperContents.Solid | SuperContents.Opaque, service.PointContents(new Vector3(156, 50, 100)));
        Assert.Equal(1f, service.Trace(new(250, 50, 120), new(-16, -16, -24), new(16, 16, 32), new(200, 50, 120), MoveFilter.WorldOnly, mover).Fraction);
        TraceResult line = service.Trace(new(250, 50, 120), Vector3.Zero, Vector3.Zero, new(100, 50, 120), MoveFilter.Normal, mover);
        Assert.Same(provider.Door, line.Ent);
        Assert.Equal("wall1", line.DpHitTextureName);
        Assert.Equal(172f, line.PlaneDist);

        // "get adjusted box for bmodel collisions": a box ten units wide is traced as the 32 x 32 x 56 hull, whose
        // far side reaches the door although the box itself passes 18 units clear of it
        (service, _, provider) = Service(Vector3.Zero);
        TraceResult narrow = service.Trace(new(160, -55, 30), new(-5, -5, -5), new(5, 5, 5), new(60, -55, 30), MoveFilter.Normal, mover);
        Assert.Same(provider.Door, narrow.Ent);
        Assert.True(narrow.Fraction < 1);
    }

    // ---- the two legacy worlds --------------------------------------------------------------------------------

    private static string TempData(byte[] map, byte[]? lit = null, string name = "fixture")
    {
        string root = Path.Combine(Path.GetTempPath(), "q1bsp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "maps"));
        File.WriteAllBytes(Path.Combine(root, "maps", name + ".bsp"), map);
        if (lit is not null) File.WriteAllBytes(Path.Combine(root, "maps", name + ".lit"), lit);
        return root;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void The_Client_World_Loads_Such_A_Map_And_Traces_Through_Its_Hulls(Q1BspFormat format)
    {
        string root = TempData(Q1BspFixture.Build(format), format == Q1BspFormat.Bsp29 ? Q1BspFixture.Lit(Q1BspFixture.LightLumpLength) : null);
        try
        {
            using VirtualFileSystem files = new();
            Assert.True(files.Mount(root));
            BspLegacyWorld world = new(files);
            Assert.True(world.LoadMap("maps/fixture.bsp"), world.LoadError);
            Assert.True(world.HasMap);
            Assert.Null(world.Bsp);
            Assert.NotNull(world.Q1Bsp);
            Assert.NotNull(world.Hulls);
            Assert.Equal(format is Q1BspFormat.Bsp29 or Q1BspFormat.HalfLife, world.Q1Bsp!.LightIsColoured);
            world.Bounds(out QcVector mins, out QcVector maxs);
            Assert.Equal((new QcVector(-257, -257, -1), new QcVector(257, 257, 257)), (mins, maxs));
            Assert.Equal(256, world.DropToFloorDistance);
            Assert.True(world.TryGetSubmodel("*1", out Vector3 doorMins, out Vector3 doorMaxs, out Brush[] brushes));
            Assert.Equal((new Vector3(99, -33, -1), new Vector3(133, 33, 97)), (doorMins, doorMaxs));
            Assert.Empty(brushes);

            const int solid = BspLegacyWorld.ContentsSolid;
            // a line down onto the floor: the face's texture, and the impact a 32nd of a unit above it
            LegacyTrace line = world.Trace(new QcVector(200, -100, 200), default, default, new QcVector(200, -100, -50), 0, 0, solid, isLine: true);
            Assert.Equal("floor", line.HitTextureName);
            Assert.Equal(0.03125f, line.EndPos.Z, 4);
            Assert.True(line.InOpen);
            Assert.True(line.InWater);
            Assert.Equal(new QcVector(0, 0, 1), line.PlaneNormal);
            // a player box: stopped by hull 1 (Half-Life's is 36 below the origin, Quake's 24)
            LegacyTrace box = world.Trace(new QcVector(200, 100, 200), new QcVector(-16, -16, -24), new QcVector(16, 16, 32), new QcVector(200, 100, -50), 0, 0, solid, isLine: false);
            // (the box's minimum corner is put where the hull's would be - start + boxmins - clip_mins - so its
            // underside meets the floor whichever hull, of whatever format, it is traced through)
            const float floor = 24;
            Assert.Equal(floor + 0.03125f, box.EndPos.Z, 3);
            Assert.False(box.StartSolid);
            // water stops a move that asks for it
            LegacyTrace wet = world.Trace(new QcVector(200, -100, 200), default, default, new QcVector(200, -100, -50), 0, 0, solid | BspLegacyWorld.ContentsWater, isLine: true);
            Assert.Equal(48.03125f, wet.EndPos.Z, 3);
            Assert.Equal("*water0", wet.HitTextureName);

            Assert.Equal(BspLegacyWorld.ContentsWater, world.PointSuperContents(new QcVector(200, 100, 20)));
            Assert.Equal(BspLegacyWorld.ContentsSolid | BspLegacyWorld.ContentsOpaque, world.PointSuperContents(new QcVector(0, 0, -5)));
            Assert.Equal(BspLegacyWorld.ContentsSky | BspLegacyWorld.ContentsNoDrop | BspLegacyWorld.ContentsOpaque, world.PointSuperContents(new QcVector(0, 0, 300)));

            // checkpvs: from the open room a box in the water is visible; from inside the ground there is no cluster
            Assert.Equal(1, world.CheckPvs(new QcVector(200, 100, 100), new QcVector(190, 90, 10), new QcVector(210, 110, 30)));
            Assert.Equal(2, world.CheckPvs(new QcVector(0, 0, -20), new QcVector(190, 90, 10), new QcVector(210, 110, 30)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_Map_That_Is_There_But_Cannot_Be_Read_Is_Told_Apart_From_One_That_Is_Missing()
    {
        byte[] good = Q1BspFixture.Build(Q1BspFormat.Bsp2);
        byte[] broken = (byte[])good.Clone();
        BitConverter.TryWriteBytes(broken.AsSpan(4 + 9 * 8 + 4), 7); // the clipnode lump: a funny size
        string root = TempData(broken, name: "broken");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "maps", "quake2.bsp"), "IBSP\x26\0\0\0 a Quake 2 map is not one this client reads"u8.ToArray());
            File.WriteAllBytes(Path.Combine(root, "maps", "empty.bsp"), Array.Empty<byte>());
            using VirtualFileSystem files = new();
            Assert.True(files.Mount(root));
            BspLegacyWorld world = new(files);
            foreach (string name in new[] { "maps/broken.bsp", "maps/quake2.bsp", "maps/empty.bsp" })
            {
                Assert.False(world.LoadMap(name));
                Assert.True(world.LoadFailedOnPresentFile, name);
                Assert.False(world.HasMap);
                Assert.Contains("could not be loaded", world.LoadError);
                HeadlessLegacyPresentation presentation = new(files);
                presentation.Map.LoadMap(name);
                Assert.Contains("could not be loaded", ((ILegacyPresentation)presentation).WorldLoadError);
            }
            Assert.False(world.LoadMap("maps/absent.bsp"));
            Assert.False(world.LoadFailedOnPresentFile);
            Assert.Contains("is not in the game data", world.LoadError);

            SvWorld server = new(files);
            Assert.False(server.LoadMap("maps/broken.bsp"));
            Assert.Contains("could not be loaded", server.LoadError);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void The_Server_World_Loads_Such_A_Map(Q1BspFormat format)
    {
        string root = TempData(Q1BspFixture.Build(format));
        try
        {
            using VirtualFileSystem files = new();
            Assert.True(files.Mount(root));
            SvWorld world = new(files);
            Assert.True(world.LoadMap("maps/fixture.bsp"), world.LoadError);
            Assert.NotNull(world.Hulls);
            Assert.NotNull(world.Q1Bsp);
            Assert.Equal(2, world.NumSubmodels);
            Assert.Contains("func_door", world.EntitiesText);
            Assert.True(world.TryGetSubmodelBounds("*1", out QcVector mins, out QcVector maxs));
            Assert.Equal((new QcVector(99, -33, -1), new QcVector(133, 33, 97)), (mins, maxs));

            const int solid = BspLegacyWorld.ContentsSolid;
            // SV_TraceBox with the engine's own 16 units of extension: the same floor, and the flags a Quake 1 map sets
            SvTrace box = world.Trace(new QcVector(200, 100, 200), new QcVector(-32, -32, -24), new QcVector(32, 32, 64), new QcVector(200, 100, 0), SvWorld.MoveWorldOnly, 0, solid, 16);
            const float below = 24;
            Assert.Equal(below + 0.03125f, box.EndPos.Z, 3);
            Assert.True(box.InOpen);
            Assert.True(box.InWater);
            Assert.Equal(0, box.Ent);
            // a line that starts inside the ground: the world's start is reported by the sweep itself
            SvTrace up = world.Trace(new QcVector(200, 100, -20), default, default, new QcVector(200, 100, 100), SvWorld.MoveNoMonsters, 0, solid, 1);
            Assert.True(up.StartSolid);
            Assert.Equal(1f, up.Fraction);
            Assert.Equal(BspLegacyWorld.ContentsWater, world.PointSuperContents(new QcVector(200, 100, 20)));
            Assert.Equal(1, world.CheckPvs(new QcVector(200, 100, 100), new QcVector(190, 90, 10), new QcVector(210, 110, 30)));
            // Mod_Q1BSP_TraceLineOfSight with the default surface traceline: nothing a face holds is opaque
            Assert.True(world.TraceLineOfSight(new QcVector(200, 100, 100), new QcVector(200, 100, -400), new QcVector(-1, -1, -1), new QcVector(1, 1, 1)));
            // fat visibility set: the three bytes of a row
            Assert.Equal(3, world.PvsBytes);
            byte[] pvs = new byte[world.PvsBytes];
            world.FatPvs(new QcVector(200, 100, 100), 8, pvs, merge: false);
            Assert.Equal(0x0F, pvs[0]);
            Assert.True(world.BoxTouchingPvs(pvs, new QcVector(190, 90, 10), new QcVector(210, 110, 30)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- real content, when it is on the machine ---------------------------------------------------------------

    /// <summary>
    /// World-only traces answered by the reference dedicated server on maps of a Quake map pack, against the
    /// same traces here. The pack is somebody's content and is not in the repository, and neither are the
    /// reference's answers, which are derived from it: both live under <c>_scratch/q1/</c> on the machine that
    /// recorded them (planning/specs/legacy-compat.md says how), and the test returns early anywhere else.
    /// </summary>
    [Theory]
    [InlineData("caffeinefreak")]
    [InlineData("kerrash")]
    [InlineData("start")]
    public void Recorded_DarkPlaces_Traces_On_A_Real_Quake_Map_Are_Answered_The_Same(string map)
    {
        string scratch = Path.Combine(TestPaths.RepoRoot, "_scratch", "q1");
        string pack = Path.Combine(scratch, "pk3", "q1-mc30_01.pk3"), traces = Path.Combine(scratch, "probe", map + ".in"), answers = Path.Combine(scratch, "probe", map + ".dp");
        if (!File.Exists(pack) || !File.Exists(traces) || !File.Exists(answers)) return;
        using VirtualFileSystem files = new();
        Assert.True(files.Mount(pack));
        // the recording is a dedicated server's: see Q1HullCollision's constructor for what that changes
        SvWorld world = new(files) { Q1DedicatedNormals = true };
        Assert.True(world.LoadMap($"maps/mc_q30th_{map}.bsp"), world.LoadError);
        string[] ins = File.ReadAllLines(traces), outs = File.ReadAllLines(answers);
        int compared = 0, exact = 0;
        List<string> wrong = new();
        for (int i = 0; i < Math.Min(ins.Length, outs.Length); i++)
        {
            string[] p = ins[i].Split(' ', StringSplitOptions.RemoveEmptyEntries), o = outs[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 16 || o.Length < 21 || p[0] != "3") continue; // MOVE_WORLDONLY: no game code needed
            float F(int k) => float.Parse(p[k], CultureInfo.InvariantCulture);
            SvTrace t = world.Trace(new QcVector(F(2), F(3), F(4)), new QcVector(F(5), F(6), F(7)), new QcVector(F(8), F(9), F(10)), new QcVector(F(11), F(12), F(13)),
                SvWorld.MoveWorldOnly, 0, int.Parse(p[15], CultureInfo.InvariantCulture), F(14));
            compared++;
            // the reference printed its doubles with nine digits: a float of ours agrees when it is a nearest one
            static bool Near(string printed, float ours)
            {
                double r = double.Parse(printed, CultureInfo.InvariantCulture);
                double ulp = Math.Max(Math.Abs((double)MathF.BitIncrement(MathF.Abs(ours)) - MathF.Abs(ours)), 1e-37);
                return Math.Abs(r - ours) <= ulp * 0.5 + Math.Abs(r) * 6e-9 + 1e-12;
            }
            bool same = Near(o[0], t.Fraction) && Near(o[1], t.EndPos.X) && Near(o[2], t.EndPos.Y) && Near(o[3], t.EndPos.Z)
                && (o[4] != "0") == t.StartSolid && (o[5] != "0") == t.AllSolid
                && Math.Abs(double.Parse(o[12], CultureInfo.InvariantCulture) - t.PlaneNormal.X) < 1e-6 && Math.Abs(double.Parse(o[13], CultureInfo.InvariantCulture) - t.PlaneNormal.Y) < 1e-6
                && Math.Abs(double.Parse(o[14], CultureInfo.InvariantCulture) - t.PlaneNormal.Z) < 1e-6
                && int.Parse(o[17], CultureInfo.InvariantCulture) == t.HitContents && (o[20] == "-" ? string.IsNullOrEmpty(t.HitTextureName) : o[20] == t.HitTextureName);
            if (same) exact++;
            else if (wrong.Count < 5) wrong.Add($"#{i}: {ins[i]} => {outs[i]} / ours {t.Fraction:R} {t.EndPos} {t.HitTextureName}");
        }
        _output.WriteLine($"{map}: {exact} of {compared} world-only traces identical to the reference");
        Assert.True(compared > 1000, "no traces were compared");
        Assert.True(exact == compared, string.Join("\n", wrong));
    }
}

/// <summary>
/// A second transcription of DarkPlaces' Quake 1 hull tracing (<c>model_brush.c</c>: Mod_Q1BSP_TraceBox,
/// TraceLine, TracePoint, RecursiveHullCheck, RecursiveHullCheckPoint, TraceLineAgainstSurfaces and its two
/// helpers, PointSuperContents, Mod_BuildNormals), written from the C on its own terms - arrays and loops as
/// the C has them, its own hull tables, its own face normals - to be compared with
/// <see cref="Q1HullCollision"/>, not to share anything with it but the parsed file.
/// </summary>
internal sealed class DpQ1Reference
{
    public struct Trace
    {
        public double Fraction;
        public bool StartSolid, AllSolid, InOpen, InWater, StartFound;
        public Vector3 PlaneNormal;
        public float PlaneDist;
        public int HitContents, HitSurfaceFlags, StartContents, HitMask;
        public string? HitTexture;
    }

    private struct Hull
    {
        public int[] PlaneNum, Child0, Child1;
        public int First;
        public float[] ClipMins;
    }

    private readonly Q1BspData _map;
    private readonly Hull[][] _hulls; // [model][hull]
    private readonly float[][] _normals;
    private readonly int[] _surfaceContents, _surfaceFlags;
    private readonly string[] _surfaceTexture;
    public bool LineReportsTexture = true, TraceOutOfSolid = true;
    public float ZeroHullCutoff = 8.03125f;

    private const int Empty = 0, SolidState = 1, Done = 2;
    private const int SkyFlags = 4 | 16 | 32 | 131072 | 1024, NoMarks = 32;

    public DpQ1Reference(Q1BspData map, bool areaWeighting)
    {
        _map = map;
        // Mod_Q1BSP_MakeHull0
        int n = map.Nodes.Length;
        int[] plane0 = new int[n], a0 = new int[n], b0 = new int[n];
        for (int i = 0; i < n; i++)
        {
            plane0[i] = map.Nodes[i].PlaneIndex;
            a0[i] = map.Nodes[i].Child0 >= 0 ? map.Nodes[i].Child0 : map.Leafs[-1 - map.Nodes[i].Child0].Contents;
            b0[i] = map.Nodes[i].Child1 >= 0 ? map.Nodes[i].Child1 : map.Leafs[-1 - map.Nodes[i].Child1].Contents;
        }
        int c = map.ClipNodes.Length;
        int[] planeN = new int[c], aN = new int[c], bN = new int[c];
        for (int i = 0; i < c; i++)
        {
            planeN[i] = map.ClipNodes[i].PlaneIndex;
            aN[i] = map.ClipNodes[i].Child0;
            bN[i] = map.ClipNodes[i].Child1;
        }
        float[][] clipMins = map.IsHalfLife
            ? new[] { new float[] { 0, 0, 0 }, new float[] { -16, -16, -36 }, new float[] { -32, -32, -32 }, new float[] { -16, -16, -18 } }
            : new[] { new float[] { 0, 0, 0 }, new float[] { -16, -16, -24 }, new float[] { -32, -32, -24 }, new float[] { 0, 0, 0 } };
        _hulls = new Hull[map.Models.Length][];
        for (int m = 0; m < map.Models.Length; m++)
        {
            _hulls[m] = new Hull[4];
            _hulls[m][0] = new Hull { PlaneNum = plane0, Child0 = a0, Child1 = b0, First = map.Models[m].HeadNode0, ClipMins = clipMins[0] };
            for (int h = 1; h < 4; h++) _hulls[m][h] = new Hull { PlaneNum = planeN, Child0 = aN, Child1 = bN, First = map.Models[m].HeadNode(h), ClipMins = clipMins[h] };
        }

        // Mod_BuildNormals on each surface's fan, and the texture's collision flags
        _normals = new float[map.Faces.Length][];
        _surfaceContents = new int[map.Faces.Length];
        _surfaceFlags = new int[map.Faces.Length];
        _surfaceTexture = new string[map.Faces.Length];
        for (int s = 0; s < map.Faces.Length; s++)
        {
            Q1Face face = map.Faces[s];
            float[] normal = new float[3];
            for (int t = 0; t < face.VertexCount - 2; t++)
            {
                Vector3 va = map.FaceVertices[face.FirstVertex], vb = map.FaceVertices[face.FirstVertex + t + 1], vc = map.FaceVertices[face.FirstVertex + t + 2];
                float[] area =
                {
                    (va.Y - vb.Y) * (vc.Z - vb.Z) - (va.Z - vb.Z) * (vc.Y - vb.Y),
                    (va.Z - vb.Z) * (vc.X - vb.X) - (va.X - vb.X) * (vc.Z - vb.Z),
                    (va.X - vb.X) * (vc.Y - vb.Y) - (va.Y - vb.Y) * (vc.X - vb.X),
                };
                if (!areaWeighting) Normalize(area);
                normal[0] += area[0]; normal[1] += area[1]; normal[2] += area[2];
            }
            Normalize(normal);
            _normals[s] = normal;
            string name = face.TextureIndex >= 0 && map.Textures[face.TextureIndex].Present ? map.Textures[face.TextureIndex].Name : "NO TEXTURE FOUND";
            bool real = face.TextureIndex >= 0 && map.Textures[face.TextureIndex].Present;
            _surfaceTexture[s] = face.TextureIndex >= 0 ? map.Textures[face.TextureIndex].Name : "NO TEXTURE FOUND";
            if (!real) { _surfaceContents[s] = face.TextureIndex == -2 ? SuperContents.Water : SuperContents.Solid; _surfaceFlags[s] = face.TextureIndex == -2 ? NoMarks : 0; }
            else if (name[0] == '*')
            {
                _surfaceFlags[s] = NoMarks;
                _surfaceContents[s] = name.StartsWith("*lava", StringComparison.Ordinal) ? SuperContents.Lava | SuperContents.NoDrop
                    : name.StartsWith("*slime", StringComparison.Ordinal) ? SuperContents.Slime : SuperContents.Water;
            }
            else if (name.StartsWith("sky", StringComparison.Ordinal)) { _surfaceContents[s] = SuperContents.Sky | SuperContents.NoDrop | SuperContents.Solid; _surfaceFlags[s] = SkyFlags; }
            else _surfaceContents[s] = SuperContents.Solid;
        }
    }

    private static void Normalize(float[] v)
    {
        float ilength = v[0] * v[0] + v[1] * v[1] + v[2] * v[2];
        if (ilength != 0) ilength = (float)(1.0f / Math.Sqrt(ilength));
        v[0] *= ilength; v[1] *= ilength; v[2] *= ilength;
    }

    private static int Super(int native) => native switch
    {
        -1 => 0,
        -2 => SuperContents.Solid | SuperContents.Opaque,
        -3 => SuperContents.Water,
        -4 => SuperContents.Slime,
        -5 => SuperContents.Lava | SuperContents.NoDrop,
        -6 => SuperContents.Sky | SuperContents.NoDrop | SuperContents.Opaque,
        _ => 0,
    };

    private sealed class Info
    {
        public Hull Hull;
        public Trace T;
        public double[] Start = new double[3], End = new double[3], Dist = new double[3];
    }

    private float PlaneType(int plane, out float[] normal, out float dist)
    {
        Q1Plane p = _map.Planes[plane];
        normal = new[] { p.Normal.X, p.Normal.Y, p.Normal.Z };
        dist = p.Dist;
        return normal[0] == 1 ? 0 : normal[1] == 1 ? 1 : normal[2] == 1 ? 2 : 3;
    }

    private int RecursiveHullCheck(Info t, int num, double p1f, double p2f, double[] p1, double[] p2)
    {
        while (num >= 0)
        {
            int type = (int)PlaneType(t.Hull.PlaneNum[num], out float[] normal, out float dist);
            double t1, t2;
            if (type < 3) { t1 = p1[type] - dist; t2 = p2[type] - dist; }
            else { t1 = normal[0] * p1[0] + normal[1] * p1[1] + normal[2] * p1[2] - dist; t2 = normal[0] * p2[0] + normal[1] * p2[1] + normal[2] * p2[2] - dist; }
            int p1side = t1 < 0 ? 1 : 0, p2side = t2 < 0 ? 1 : 0;
            if (p1side == p2side)
            {
                num = p1side != 0 ? t.Hull.Child1[num] : t.Hull.Child0[num];
                continue;
            }
            if (type < 3) { t1 = t.Start[type] - dist; t2 = t.End[type] - dist; }
            else { t1 = normal[0] * t.Start[0] + normal[1] * t.Start[1] + normal[2] * t.Start[2] - dist; t2 = normal[0] * t.End[0] + normal[1] * t.End[1] + normal[2] * t.End[2] - dist; }
            double midf = t1 / (t1 - t2);
            midf = midf < p1f ? p1f : midf >= p2f ? p2f : midf;
            double[] mid = { t.Start[0] + midf * t.Dist[0], t.Start[1] + midf * t.Dist[1], t.Start[2] + midf * t.Dist[2] };
            int ret = RecursiveHullCheck(t, p1side != 0 ? t.Hull.Child1[num] : t.Hull.Child0[num], p1f, midf, p1, mid);
            if (ret != Empty && (!t.T.AllSolid || !TraceOutOfSolid)) return ret;
            ret = RecursiveHullCheck(t, p2side != 0 ? t.Hull.Child1[num] : t.Hull.Child0[num], midf, p2f, mid, p2);
            if (ret != SolidState) return ret;
            if (p1side != 0)
            {
                t.T.PlaneDist = -dist;
                t.T.PlaneNormal = new Vector3(-normal[0], -normal[1], -normal[2]);
            }
            else
            {
                t.T.PlaneDist = dist;
                t.T.PlaneNormal = new Vector3(normal[0], normal[1], normal[2]);
            }
            t1 = t.T.PlaneNormal.X * t.Start[0] + t.T.PlaneNormal.Y * t.Start[1] + t.T.PlaneNormal.Z * t.Start[2] - t.T.PlaneDist;
            t2 = t.T.PlaneNormal.X * t.End[0] + t.T.PlaneNormal.Y * t.End[1] + t.T.PlaneNormal.Z * t.End[2] - t.T.PlaneDist;
            midf = (t1 - 0.03125f) / (t1 - t2);
            t.T.Fraction = midf < 0 ? 0 : midf >= 1 ? 1 : midf;
            return Done;
        }
        num = Super(num);
        if (!t.T.StartFound)
        {
            t.T.StartFound = true;
            t.T.StartContents |= num;
        }
        if ((num & SuperContents.LiquidsMask) != 0) t.T.InWater = true;
        if (num == 0) t.T.InOpen = true;
        if ((num & SuperContents.Solid) != 0) { t.T.HitTexture = "solid"; t.T.HitSurfaceFlags = 0; }
        else if ((num & SuperContents.Sky) != 0) { t.T.HitTexture = "sky"; t.T.HitSurfaceFlags = SkyFlags; }
        else if ((num & SuperContents.Lava) != 0) { t.T.HitTexture = "*lava"; t.T.HitSurfaceFlags = NoMarks; }
        else if ((num & SuperContents.Slime) != 0) { t.T.HitTexture = "*slime"; t.T.HitSurfaceFlags = NoMarks; }
        else { t.T.HitTexture = "*water"; t.T.HitSurfaceFlags = NoMarks; }
        t.T.HitContents = num;
        if ((num & t.T.HitMask) != 0)
        {
            if (t.T.AllSolid) t.T.StartSolid = true;
            return SolidState;
        }
        t.T.AllSolid = false;
        return Empty;
    }

    private int PointNative(Hull hull, int num, float[] point)
    {
        while (num >= 0)
        {
            int type = (int)PlaneType(hull.PlaneNum[num], out float[] normal, out float dist);
            float d = type < 3 ? point[type] : normal[0] * point[0] + normal[1] * point[1] + normal[2] * point[2];
            num = d < dist ? hull.Child1[num] : hull.Child0[num];
        }
        return num;
    }

    private void RecursiveHullCheckPoint(Info t, int num)
    {
        float[] point = { (float)t.Start[0], (float)t.Start[1], (float)t.Start[2] };
        num = Super(PointNative(t.Hull, num, point));
        t.T.StartContents |= num;
        if ((num & SuperContents.LiquidsMask) != 0) t.T.InWater = true;
        if (num == 0) t.T.InOpen = true;
        t.T.AllSolid = t.T.StartSolid = (num & t.T.HitMask) != 0;
    }

    public int PointContents(int model, Vector3 point) => Super(PointNative(_hulls[model][0], _hulls[model][0].First, new[] { point.X, point.Y, point.Z }));

    public Trace TraceBox(int model, Vector3 start, Vector3 boxMins, Vector3 boxMaxs, Vector3 end, int hitMask)
    {
        Info t = new();
        t.T.Fraction = 1;
        t.T.AllSolid = true;
        if (boxMins == boxMaxs)
        {
            if (start == end)
            {
                // Mod_Q1BSP_TracePoint: the mask is left at the zero of the memset
                t.Hull = _hulls[model][0];
                t.Start = new double[] { start.X, start.Y, start.Z };
                t.End = new double[] { start.X, start.Y, start.Z };
                RecursiveHullCheckPoint(t, t.Hull.First);
                return t.T;
            }
            t.T.HitMask = hitMask;
            t.Hull = _hulls[model][0];
            t.Start = new double[] { start.X, start.Y, start.Z };
            t.End = new double[] { end.X, end.Y, end.Z };
            for (int i = 0; i < 3; i++) t.Dist[i] = t.End[i] - t.Start[i];
            if (LineReportsTexture)
            {
                SurfaceNode(t, t.Hull.First, t.Start, t.End);
                return t.T;
            }
            if (t.Dist[0] * t.Dist[0] + t.Dist[1] * t.Dist[1] + t.Dist[2] * t.Dist[2] != 0) RecursiveHullCheck(t, t.Hull.First, 0, 1, t.Start, t.End);
            else RecursiveHullCheckPoint(t, t.Hull.First);
            return t.T;
        }
        t.T.HitMask = hitMask;
        double[] boxsize = { boxMaxs.X - boxMins.X, boxMaxs.Y - boxMins.Y, boxMaxs.Z - boxMins.Z };
        int hull;
        if (boxsize[0] < ZeroHullCutoff) hull = 0;
        else if (_map.IsHalfLife) hull = boxsize[0] < 32.1 ? (boxsize[2] < 54 ? 3 : 1) : 2;
        else hull = boxsize[0] < 32.1 ? 1 : 2;
        t.Hull = _hulls[model][hull];
        float[] s = { start.X, start.Y, start.Z }, e = { end.X, end.Y, end.Z }, mn = { boxMins.X, boxMins.Y, boxMins.Z };
        for (int i = 0; i < 3; i++)
        {
            float a = 1 * s[i] + 1 * mn[i] + -1 * t.Hull.ClipMins[i], b = 1 * e[i] + 1 * mn[i] + -1 * t.Hull.ClipMins[i];
            t.Start[i] = a;
            t.End[i] = b;
            t.Dist[i] = t.End[i] - t.Start[i];
        }
        if (t.Dist[0] * t.Dist[0] + t.Dist[1] * t.Dist[1] + t.Dist[2] * t.Dist[2] != 0) RecursiveHullCheck(t, t.Hull.First, 0, 1, t.Start, t.End);
        else RecursiveHullCheckPoint(t, t.Hull.First);
        return t.T;
    }

    // Mod_Q1BSP_TraceLineAgainstSurfacesRecursiveBSPNode
    private int SurfaceNode(Info t, int num, double[] p1, double[] p2)
    {
        while (num >= 0)
        {
            int type = (int)PlaneType(t.Hull.PlaneNum[num], out float[] normal, out float dist);
            double t1, t2;
            if (type < 3) { t1 = p1[type] - dist; t2 = p2[type] - dist; }
            else { t1 = normal[0] * p1[0] + normal[1] * p1[1] + normal[2] * p1[2] - dist; t2 = normal[0] * p2[0] + normal[1] * p2[1] + normal[2] * p2[2] - dist; }
            int side;
            if (t1 < 0)
            {
                if (t2 < 0) { num = t.Hull.Child1[num]; continue; }
                side = 1;
            }
            else
            {
                if (t2 >= 0) { num = t.Hull.Child0[num]; continue; }
                side = 0;
            }
            if (type < 3) { t1 = t.Start[type] - dist; t2 = t.End[type] - dist; }
            else { t1 = normal[0] * t.Start[0] + normal[1] * t.Start[1] + normal[2] * t.Start[2] - dist; t2 = normal[0] * t.End[0] + normal[1] * t.End[1] + normal[2] * t.End[2] - dist; }
            double midf = t1 / (t1 - t2);
            double[] mid = { t.Start[0] + midf * t.Dist[0], t.Start[1] + midf * t.Dist[1], t.Start[2] + midf * t.Dist[2] };
            if (SurfaceNode(t, side == 1 ? t.Hull.Child1[num] : t.Hull.Child0[num], p1, mid) == Done) return Done;
            FindTextureOnNode(t, num, mid);
            if (t.T.HitTexture is not null) return Done;
            return SurfaceNode(t, side == 1 ? t.Hull.Child0[num] : t.Hull.Child1[num], mid, p2);
        }
        int contents = Super(num);
        if (!t.T.StartFound)
        {
            t.T.StartFound = true;
            t.T.StartContents |= contents;
        }
        if ((contents & SuperContents.LiquidsMask) != 0) t.T.InWater = true;
        if (contents == 0) t.T.InOpen = true;
        if ((contents & t.T.HitMask) != 0)
        {
            if (t.T.AllSolid) t.T.StartSolid = true;
            return SolidState;
        }
        t.T.AllSolid = false;
        return Empty;
    }

    private void FindTextureOnNode(Info t, int nodeIndex, double[] mid)
    {
        Q1Node node = _map.Nodes[nodeIndex];
        float[] p = { (float)mid[0], (float)mid[1], (float)mid[2] };
        for (int i = 0; i < node.FaceCount; i++)
        {
            int surface = node.FirstFace + i;
            if ((t.T.HitMask & _surfaceContents[surface]) == 0) continue;
            float[] normal = _normals[surface];
            if (t.Dist[0] * normal[0] + t.Dist[1] * normal[1] + t.Dist[2] * normal[2] > 0) continue;
            Q1Face face = _map.Faces[surface];
            int j, k;
            for (j = 0, k = face.VertexCount - 1; j < face.VertexCount; k = j, j++)
            {
                Vector3 a = _map.FaceVertices[face.FirstVertex + k], b = _map.FaceVertices[face.FirstVertex + j];
                float[] v0 = { a.X, a.Y, a.Z }, edgedir = { a.X - b.X, a.Y - b.Y, a.Z - b.Z };
                float[] edgenormal = { edgedir[1] * normal[2] - edgedir[2] * normal[1], edgedir[2] * normal[0] - edgedir[0] * normal[2], edgedir[0] * normal[1] - edgedir[1] * normal[0] };
                if (edgenormal[0] * p[0] + edgenormal[1] * p[1] + edgenormal[2] * p[2] > edgenormal[0] * v0[0] + edgenormal[1] * v0[1] + edgenormal[2] * v0[2]) break;
            }
            if (j < face.VertexCount) continue;
            t.T.PlaneNormal = new Vector3(normal[0], normal[1], normal[2]);
            t.T.PlaneDist = normal[0] * p[0] + normal[1] * p[1] + normal[2] * p[2];
            float t1 = (float)(t.Start[0] * normal[0] + t.Start[1] * normal[1] + t.Start[2] * normal[2] - t.T.PlaneDist);
            float t2 = (float)(t.End[0] * normal[0] + t.End[1] * normal[1] + t.End[2] * normal[2] - t.T.PlaneDist);
            float midf = (t1 - 0.03125f) / (t1 - t2);
            t.T.Fraction = midf < 0 ? 0 : midf >= 1 ? 1 : midf;
            t.T.HitTexture = _surfaceTexture[surface];
            t.T.HitSurfaceFlags = _surfaceFlags[surface];
            t.T.HitContents = _surfaceContents[surface];
            return;
        }
    }
}
