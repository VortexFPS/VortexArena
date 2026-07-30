using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the <c>getsurface*</c> mesh-query builtins (<see cref="SurfaceService"/> + <see cref="BspSurfaceBuilder"/>)
/// and the warpzone brush→plane auto-derivation that consumes them (REMAINING-WORK §6 — getsurface* BSP/model
/// mesh queries). A synthetic single-face BSP inline model exercises every builtin deterministically; the
/// warpzone test reproduces <c>WarpZone_InitStep_UpdateTransform</c>; a real shipped map is a smoke test.
/// </summary>
public class SurfaceQueryTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // A unit-ish quad in the YZ plane at x=0, centred on the origin, wound so cross(c-a, b-a) faces +X
    // (matching the +X vertex normals). Triangles are face-local indices, as in a real BSP.
    private static readonly Vector3[] QuadVerts =
    {
        new(0, -16, -16), new(0, 16, -16), new(0, 16, 16), new(0, -16, 16),
    };
    private static readonly int[] QuadTris = { 0, 2, 1, 0, 3, 2 };

    private static BspData QuadBsp(string texture)
    {
        var verts = QuadVerts.Select(p => new BspVertex(
            p, new Vector2(0, 0), new Vector2(0, 0), new Vector3(1, 0, 0), new BspColor(255, 255, 255, 255))).ToArray();

        return new BspData
        {
            Textures = new[] { new BspTexture(texture, 0, SuperContents.Solid) },
            Vertices = verts,
            Triangles = QuadTris,
            Faces = new[]
            {
                new BspFace(0, -1, BspFaceType.Flat, FirstVertex: 0, VertexCount: 4, FirstIndex: 0, IndexCount: 6,
                    LightmapIndex: -1, PatchWidth: 0, PatchHeight: 0),
            },
            Models = new[]
            {
                new BspModel(Vector3.Zero, Vector3.Zero, FirstFace: 0, FaceCount: 0, FirstBrush: 0, BrushCount: 0), // worldspawn *0 (empty)
                new BspModel(new Vector3(0, -16, -16), new Vector3(0, 16, 16), FirstFace: 0, FaceCount: 1, FirstBrush: 0, BrushCount: 0), // *1 (the quad)
            },
        };
    }

    private static (EngineServices services, Entity brush) SetupQuad(string texture = "textures/example/wall")
    {
        var services = new EngineServices(new CollisionWorld());
        Api.Services = services;
        BspSurfaceBuilder.BuildAndAttach(QuadBsp(texture), services.ModelsImpl);
        services.ModelsImpl.Register("*1", new Vector3(0, -16, -16), new Vector3(0, 16, 16), isBrushModel: true);
        var brush = services.Entities.Spawn();
        services.Entities.SetModel(brush, "*1");
        return (services, brush);
    }

    [Fact]
    public void GetSurface_Points_Triangles_Normal_Texture()
    {
        (EngineServices services, Entity brush) = SetupQuad();
        ISurfaceService s = services.Surfaces;

        Assert.Equal(4, s.GetSurfaceNumPoints(brush, 0));
        Assert.Equal(2, s.GetSurfaceNumTriangles(brush, 0));
        Assert.Equal(0, s.GetSurfaceNumPoints(brush, 1));           // no second surface
        Assert.Equal("textures/example/wall", s.GetSurfaceTexture(brush, 0));
        Assert.Equal("", s.GetSurfaceTexture(brush, 5));            // out of range -> "" (terminates the warpzone loop)

        // point 2 is the third quad vertex (identity transform at origin/angles 0)
        AssertVec(new Vector3(0, 16, 16), s.GetSurfacePoint(brush, 0, 2));
        // triangle 0 is the face-local index triple
        AssertVec(new Vector3(0, 2, 1), s.GetSurfaceTriangle(brush, 0, 0));
        // the surface normal is +X (from the vertex normals)
        AssertVec(new Vector3(1, 0, 0), s.GetSurfaceNormal(brush, 0), 1e-4f);
    }

    [Fact]
    public void GetSurface_AppliesEntityTransform()
    {
        (EngineServices services, Entity brush) = SetupQuad();
        // move + yaw the entity 90°: the +X face normal rotates to +Y, points translate.
        brush.Origin = new Vector3(100, 200, 0);
        brush.Angles = new Vector3(0, 90, 0);

        AssertVec(new Vector3(0, 1, 0), services.Surfaces.GetSurfaceNormal(brush, 0), 1e-3f);

        // vertex 1 = (0,16,-16) local -> yaw 90 maps (x,y)->(... ) then + origin. Verify via makevectors basis.
        QMath.AngleVectors(brush.Angles, out Vector3 f, out Vector3 r, out Vector3 u);
        Vector3 expect = brush.Origin + f * 0f + (-r) * 16f + u * (-16f); // local (0,16,-16): x*f + y*left + z*up
        AssertVec(expect, services.Surfaces.GetSurfacePoint(brush, 0, 1), 1e-2f);
    }

    [Fact]
    public void GetSurfacePointAttribute_Channels()
    {
        (EngineServices services, Entity brush) = SetupQuad();
        ISurfaceService s = services.Surfaces;

        AssertVec(new Vector3(0, 16, -16), s.GetSurfacePointAttribute(brush, 0, 1, (int)SurfaceAttribute.Position));
        AssertVec(new Vector3(1, 0, 0), s.GetSurfacePointAttribute(brush, 0, 1, (int)SurfaceAttribute.Normal), 1e-4f);
        AssertVec(new Vector3(1, 1, 1), s.GetSurfacePointAttribute(brush, 0, 0, (int)SurfaceAttribute.LightmapColor)); // white vertex color
    }

    [Fact]
    public void GetSurfaceNearPoint_And_ClippedPoint()
    {
        (EngineServices services, Entity brush) = SetupQuad();
        ISurfaceService s = services.Surfaces;

        Assert.Equal(0, s.GetSurfaceNearPoint(brush, new Vector3(50, 0, 0)));  // the only surface
        Assert.Equal(-1, s.GetSurfaceNearPoint(services.Entities.Spawn(), Vector3.Zero)); // entity with no model

        // a point off the +X face clips back onto the quad (x -> 0, y/z clamped within [-16,16])
        Vector3 clipped = s.GetSurfaceClippedPoint(brush, 0, new Vector3(40, 0, 0));
        Assert.True(MathF.Abs(clipped.X) < 0.01f, $"clipped onto the quad plane x=0, got {clipped}");
        Vector3 clampedCorner = s.GetSurfaceClippedPoint(brush, 0, new Vector3(40, 100, 100));
        AssertVec(new Vector3(0, 16, 16), clampedCorner, 0.05f);
    }

    [Fact]
    public void Warpzone_AutoDerivesPlaneFromBrush()
    {
        (EngineServices services, Entity brush) = SetupQuad();
        var mgr = new WarpzoneManager();

        (Vector3 org, Vector3 ang, bool ok) = mgr.DerivePlaneFromBrush(brush);
        Assert.True(ok);
        AssertVec(Vector3.Zero, org, 0.01f);                          // the quad centroid is the origin

        // the IN forward (makevectors of the derived angles) is the brush plane normal (+X here).
        QMath.AngleVectors(ang, out Vector3 fwd, out _, out _);
        AssertVec(new Vector3(1, 0, 0), fwd, 1e-3f);

        // a trigger-textured brush yields no usable surface -> the QC errors / returns not-ok.
        (EngineServices svc2, Entity trig) = SetupQuad("textures/common/trigger");
        Assert.False(new WarpzoneManager().DerivePlaneFromBrush(trig).ok);
        _ = svc2;
    }

    [Fact]
    public void SpawnFromBrush_BuildsAWorkingPortal()
    {
        (EngineServices services, Entity inBrush) = SetupQuad();
        var mgr = new WarpzoneManager();

        Warpzone? wz = mgr.SpawnFromBrush(inBrush, targetName: "wz_in", target: "wz_out");
        Assert.NotNull(wz);
        Assert.Equal("trigger_warpzone", inBrush.ClassName);
        Assert.Equal(Solid.Trigger, inBrush.Solid);

        // pair it with an explicit exit plane and confirm the transform is built.
        mgr.Spawn(new Vector3(500, 0, 0), new Vector3(0, 180, 0), targetName: "wz_out", target: "wz_in",
            new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        mgr.Link();
        Assert.True(wz!.Linked);
    }

    [Fact]
    public void Real_Bsp_Surfaces_Build_From_A_Shipped_Map()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault();
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        var services = new EngineServices(new CollisionWorld());
        BspSurfaceBuilder.BuildAndAttach(bsp, services.ModelsImpl);

        // worldspawn (*0) is a big map: it must have many surfaces, each with points + triangles, and a unit normal.
        Assert.True(services.ModelsImpl.TryGetModel("*0", out ModelService.ModelDef world));
        Assert.NotNull(world.Surfaces);
        Assert.True(world.Surfaces!.Count > 0, "worldspawn should expose render surfaces");

        var brush = services.Entities.Spawn();
        services.Entities.SetModel(brush, "*0"); // resolves *0 even with zero bounds; surface queries read its faces
        brush.Model = "*0";
        Api.Services = services;
        int total = 0;
        for (int si = 0; si < world.Surfaces.Count && si < 200; si++)
        {
            int np = services.Surfaces.GetSurfaceNumPoints(brush, si);
            int nt = services.Surfaces.GetSurfaceNumTriangles(brush, si);
            if (np > 0 && nt > 0)
            {
                total++;
                Vector3 n = services.Surfaces.GetSurfaceNormal(brush, si);
                Assert.InRange(n.Length(), 0.9f, 1.1f); // a real unit-ish normal
            }
        }
        Assert.True(total > 0, "expected at least one renderable surface with points + triangles");
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-4f)
        => Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual} (tol {tol})");
}
