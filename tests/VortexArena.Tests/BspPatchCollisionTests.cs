using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression tests for bezier-PATCH collision. Q3 patches carry no brushes; DP makes them collidable by
/// tessellating the curve and tracing its triangles. The port builds collision from brushes only, so patch
/// floors/grates/platforms (e.g. Stormkeep's grate over lava, the ring platform around a pillar) fell through.
/// <see cref="BspCollisionBuilder"/> now tessellates solid patches into thin slab brushes.
/// </summary>
public class BspPatchCollisionTests
{
    /// <summary>A single flat 3x3 bezier patch spanning [0,128]² at <paramref name="zPlane"/>, textured with the
    /// raw Q3 content value <paramref name="q3"/>. No brushes / no models lump → the legacy path tessellates it.</summary>
    private static BspData FlatPatchBsp(int q3, float zPlane = 0f)
    {
        var verts = new List<BspVertex>();
        float[] coords = { 0f, 64f, 128f };
        foreach (float y in coords)
        foreach (float x in coords)
            verts.Add(new BspVertex(new Vector3(x, y, zPlane), Vector2.Zero, Vector2.Zero,
                                    new Vector3(0, 0, 1), new BspColor(255, 255, 255, 255)));

        return new BspData
        {
            Vertices = verts.ToArray(),
            Faces = new[] { new BspFace(0, -1, BspFaceType.Patch, 0, 9, 0, 0, -1, 3, 3) },
            Textures = new[] { new BspTexture("textures/exx/floor-grate01", 0, q3) },
        };
    }

    private static readonly Vector3 Hull = new(8, 8, 8);

    private static TraceResult Drop(TraceService trace, float x, float y)
    {
        var player = new Entity { Solid = Solid.SlideBox };
        return trace.Trace(new Vector3(x, y, 64), -Hull, Hull, new Vector3(x, y, -64), MoveFilter.Normal, player);
    }

    [Fact]
    public void Solid_Patch_Floor_Gets_Collision()
    {
        // floor-grate01: SOLID|TRANSLUCENT → solid, walkable.
        var world = BspCollisionBuilder.Build(FlatPatchBsp(0x20000001)).World;
        Assert.NotEmpty(world.Brushes);              // tessellated into slab brushes

        TraceResult tr = Drop(new TraceService(world), 64, 64);
        Assert.True(tr.Fraction < 1f, "player should land on the patch floor, not fall through");
        // Box bottom rests on the slab top (≈ z=+1); origin ≈ +9. Allow slack for the impact nudge.
        Assert.True(tr.EndPos.Z is > 0f and < 18f, $"landed at z={tr.EndPos.Z}, expected near the patch plane");
    }

    [Fact]
    public void Trace_Beside_The_Patch_Falls_Through()
    {
        // x=300 is off the [0,128] footprint → nothing to stand on.
        var world = BspCollisionBuilder.Build(FlatPatchBsp(0x20000001)).World;
        Assert.Equal(1f, Drop(new TraceService(world), 300, 64).Fraction, 3);
    }

    [Fact]
    public void Nonsolid_Translucent_Patch_Has_No_Collision()
    {
        // A translucent-only curve (e.g. an fx/decal patch, no SOLID bit) must stay intangible.
        var world = BspCollisionBuilder.Build(FlatPatchBsp(0x20000000)).World;  // TRANSLUCENT only
        Assert.Empty(world.Brushes);
        Assert.Equal(1f, Drop(new TraceService(world), 64, 64).Fraction, 3);
    }

    // ---- real data: the exact Stormkeep spot from the bug report -------------------------------------

    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    [Fact]
    public void Stormkeep_Grate_Over_Lava_Collides()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? path = vfs.Find("maps/", "bsp").FirstOrDefault(p => p.Contains("stormkeep"));
        if (path is null) return;   // map not in this checkout — skip

        BspData bsp = BspReader.Read(vfs.ReadBytes(path));
        var trace = new TraceService(BspCollisionBuilder.Build(bsp).World);

        // The grate over the negative-x lava pool is a bezier patch (floor-grate01) at z≈-34 with no brush
        // under it; the lava surface below sits at z≈-56. A point trace straight down must land ON the grate
        // now (patch collision), not fall past it into the lava or through the world.
        TraceResult tr = trace.Trace(new Vector3(-384, 1152, 64), Vector3.Zero, Vector3.Zero,
                                     new Vector3(-384, 1152, -256), MoveFilter.WorldOnly, null);

        Assert.True(tr.Fraction < 1f, "the grate-over-lava patch must collide");
        Assert.True(tr.EndPos.Z > -50f, $"landed at z={tr.EndPos.Z}; expected to rest on the grate (~-34), not fall into the lava (~-56)");
    }

    // Standard Xonotic standing hull.
    private static readonly Vector3 PlayerMins = new(-16, -16, -24);
    private static readonly Vector3 PlayerMaxs = new(16, 16, 45);

    [Fact]
    public void Stormkeep_Platform_Landings_Are_Not_Embedded()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? path = vfs.Find("maps/", "bsp").FirstOrDefault(p => p.Contains("stormkeep"));
        if (path is null) return;

        var trace = new TraceService(BspCollisionBuilder.Build(BspReader.Read(vfs.ReadBytes(path))).World);
        var player = new Entity { Solid = Solid.SlideBox };

        // Drop a player hull onto a grid over the pillar-platform region; wherever it lands, the RESTING
        // position must not be inside solid. An embedded rest = the server sticks while the client predicts
        // forward → reconcile snap (the reported landing glitch). Walkable patch slabs are extruded downward
        // (front face on the surface) precisely so a landing never sits inside the volume.
        int landed = 0, embedded = 0;
        for (float x = 860; x <= 1030; x += 8)
        for (float y = 830; y <= 1000; y += 8)
        {
            TraceResult drop = trace.Trace(new Vector3(x, y, 200), PlayerMins, PlayerMaxs,
                                           new Vector3(x, y, -260), MoveFilter.Normal, player);
            if (drop.StartSolid || drop.Fraction >= 1f) continue;
            landed++;
            if (trace.Trace(drop.EndPos, PlayerMins, PlayerMaxs, drop.EndPos, MoveFilter.Normal, player).StartSolid)
                embedded++;
        }

        Assert.True(landed > 50, $"expected many landing spots over the platform, got {landed}");
        Assert.Equal(0, embedded);
    }
}
