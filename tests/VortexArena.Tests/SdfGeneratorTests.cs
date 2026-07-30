using System;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Particles;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the chunked SDF generator (planning/particles-dual-system.md §A.3) and the .psdf reader/writer (§A.2):
/// a single axis-aligned box brush generates a field that is negative inside the box and clamps to +Band far
/// away; the .psdf round-trips chunk distances within R16F tolerance; and the param/bsp hashes are stable.
/// </summary>
public class SdfGeneratorTests
{
    /// <summary>Build a static collision world holding a single solid box brush spanning [mins,maxs].</summary>
    private static CollisionWorld BoxWorld(Vector3 mins, Vector3 maxs)
    {
        var world = new CollisionWorld();
        // A real (non-AABB) convex box would also work, but FromBox carries the SOLID contents the generator
        // filters on and gives exact plane/point data for the signed-distance test.
        world.AddBrush(Brush.FromBox(mins, maxs, SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    /// <summary>Sample the generated field at a world point: find the covering chunk + nearest voxel.</summary>
    private static bool TrySample(SdfField field, Vector3 p, out float dist)
    {
        dist = 0f;
        float chunk = field.ChunkSize;
        float voxel = field.VoxelSize;

        int cx = (int)MathF.Floor((p.X - field.GridMins.X) / chunk);
        int cy = (int)MathF.Floor((p.Y - field.GridMins.Y) / chunk);
        int cz = (int)MathF.Floor((p.Z - field.GridMins.Z) / chunk);

        SdfChunk? c = field.Chunks.FirstOrDefault(ch => ch.Cx == cx && ch.Cy == cy && ch.Cz == cz);
        if (c is null)
            return false;

        int vx = Math.Clamp((int)MathF.Floor((p.X - c.CellMins.X) / voxel), 0, c.Res - 1);
        int vy = Math.Clamp((int)MathF.Floor((p.Y - c.CellMins.Y) / voxel), 0, c.Res - 1);
        int vz = Math.Clamp((int)MathF.Floor((p.Z - c.CellMins.Z) / voxel), 0, c.Res - 1);

        dist = c.Distances[vz * c.Res * c.Res + vy * c.Res + vx];
        return true;
    }

    private static SdfGenParams SmallParams() => new()
    {
        // Small chunk + voxel keeps the test field tiny (res = 16) while exercising the real code paths.
        ChunkSize = 256f,
        VoxelSize = 16f,
        Skirt = 64f,
        Band = 64f,
        Thickness = 0f,
    };

    [Fact]
    public void Generate_Box_Center_Is_Negative_And_Far_Is_Positive_Band()
    {
        // A 128qu box centered at the origin region.
        Vector3 mins = new(0, 0, 0), maxs = new(128, 128, 128);
        CollisionWorld world = BoxWorld(mins, maxs);

        var gen = new SdfGenerator(SmallParams());
        SdfField field = gen.Generate(world);

        Assert.NotEmpty(field.Chunks);

        // Box center is deep inside → negative signed distance.
        Assert.True(TrySample(field, new Vector3(64, 64, 64), out float center), "center voxel should exist");
        Assert.True(center < 0f, $"box center should be inside (negative), got {center}");

        // A point well outside (and well within the band reach via skirt) → positive, clamped near +Band.
        Assert.True(TrySample(field, new Vector3(64, 64, 248), out float above), "above-box voxel should exist");
        Assert.True(above > 0f, $"point above the box should be outside (positive), got {above}");
        Assert.True(above <= SmallParams().Band + 1e-3f, $"distance must clamp to +Band, got {above}");
    }

    [Fact]
    public void Generate_Distance_Magnitude_Is_Reasonable_Near_Surface()
    {
        Vector3 mins = new(0, 0, 0), maxs = new(128, 128, 128);
        CollisionWorld world = BoxWorld(mins, maxs);

        var gen = new SdfGenerator(SmallParams());
        SdfField field = gen.Generate(world);

        // A voxel center ~24qu above the top face (z=128). Voxel centers sit at cellMins + (i+0.5)*voxel;
        // for z=152 the covering voxel center is exactly 152, true surface distance 24, minus 8qu dilation = 16.
        Assert.True(TrySample(field, new Vector3(64, 64, 152), out float near));
        Assert.True(near > 0f, $"voxel above the surface should be positive, got {near}");
        // Small (near the surface), not slammed to the band.
        Assert.True(near < 40f, $"near-surface distance should be small, got {near}");
    }

    [Fact]
    public void Psdf_RoundTrips_Chunk_Distances_Within_R16F_Tolerance()
    {
        Vector3 mins = new(0, 0, 0), maxs = new(128, 128, 128);
        CollisionWorld world = BoxWorld(mins, maxs);

        var gen = new SdfGenerator(SmallParams());
        SdfField field = gen.Generate(world);
        field.BspHash = PsdfFile.ComputeBspHash(new byte[] { 1, 2, 3, 4 });
        field.ParamsHash = PsdfFile.ComputeParamsHash(SmallParams());

        using var ms = new MemoryStream();
        PsdfFile.Write(ms, field);
        ms.Position = 0;
        SdfField loaded = PsdfFile.Read(ms);

        // Metadata survives the round trip.
        Assert.Equal(field.VoxelSize, loaded.VoxelSize);
        Assert.Equal(field.ChunkSize, loaded.ChunkSize);
        Assert.Equal(field.GridMins, loaded.GridMins);
        Assert.Equal(field.GridDimsX, loaded.GridDimsX);
        Assert.Equal(field.GridDimsY, loaded.GridDimsY);
        Assert.Equal(field.GridDimsZ, loaded.GridDimsZ);
        Assert.Equal(field.ParamsHash, loaded.ParamsHash);
        Assert.Equal(field.BspHash, loaded.BspHash);
        Assert.Equal(field.Chunks.Count, loaded.Chunks.Count);

        // Distances match within half-float tolerance (R16F has ~3 decimal digits; |value| ≤ 64 here →
        // the ulp near 64 is ~0.0625, so a generous absolute tolerance covers it).
        for (int ci = 0; ci < field.Chunks.Count; ci++)
        {
            SdfChunk src = field.Chunks[ci];
            SdfChunk dst = loaded.Chunks[ci];
            Assert.Equal(src.Cx, dst.Cx);
            Assert.Equal(src.Cy, dst.Cy);
            Assert.Equal(src.Cz, dst.Cz);
            Assert.Equal(src.Res, dst.Res);
            Assert.Equal(src.CellMins, dst.CellMins);
            Assert.Equal(src.Distances.Length, dst.Distances.Length);
            for (int v = 0; v < src.Distances.Length; v++)
                Assert.True(MathF.Abs(src.Distances[v] - dst.Distances[v]) <= 0.1f,
                    $"voxel {v}: {src.Distances[v]} vs {dst.Distances[v]}");
        }
    }

    [Fact]
    public void Psdf_Read_Throws_On_Bad_Magic()
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0xDEADBEEFu);   // wrong magic
        w.Write(PsdfFile.Version);
        w.Flush();
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => PsdfFile.Read(ms));
    }

    [Fact]
    public void ComputeParamsHash_Changes_When_A_Param_Changes()
    {
        var a = new SdfGenParams { ChunkSize = 1024f, VoxelSize = 8f, Skirt = 128f, Thickness = 0f };
        uint baseHash = PsdfFile.ComputeParamsHash(a);

        Assert.NotEqual(baseHash, PsdfFile.ComputeParamsHash(
            new SdfGenParams { ChunkSize = 1024f, VoxelSize = 16f, Skirt = 128f, Thickness = 0f }));   // voxel
        Assert.NotEqual(baseHash, PsdfFile.ComputeParamsHash(
            new SdfGenParams { ChunkSize = 512f, VoxelSize = 8f, Skirt = 128f, Thickness = 0f }));      // chunk
        Assert.NotEqual(baseHash, PsdfFile.ComputeParamsHash(
            new SdfGenParams { ChunkSize = 1024f, VoxelSize = 8f, Skirt = 256f, Thickness = 0f }));     // skirt
        Assert.NotEqual(baseHash, PsdfFile.ComputeParamsHash(
            new SdfGenParams { ChunkSize = 1024f, VoxelSize = 8f, Skirt = 128f, Thickness = 4f }));     // thickness

        // Same params → same hash (stable).
        Assert.Equal(baseHash, PsdfFile.ComputeParamsHash(
            new SdfGenParams { ChunkSize = 1024f, VoxelSize = 8f, Skirt = 128f, Thickness = 0f }));
    }

    [Fact]
    public void ComputeBspHash_Is_Deterministic()
    {
        byte[] bytes = { 10, 20, 30, 40, 50, 60 };
        byte[] h1 = PsdfFile.ComputeBspHash(bytes);
        byte[] h2 = PsdfFile.ComputeBspHash((byte[])bytes.Clone());

        Assert.Equal(32, h1.Length);                  // SHA256 = 32 bytes
        Assert.Equal(h1, h2);                         // deterministic
        Assert.NotEqual(h1, PsdfFile.ComputeBspHash(new byte[] { 10, 20, 30, 40, 50, 61 }));  // sensitive
    }

    [Fact]
    public void CacheFileName_Includes_First16Hex_Of_Hash()
    {
        byte[] hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        string name = PsdfFile.CacheFileName("dance", hash);
        // First 8 bytes → 16 hex chars: 00 01 02 03 04 05 06 07.
        Assert.Equal("dance-0001020304050607.psdf", name);
    }
}
