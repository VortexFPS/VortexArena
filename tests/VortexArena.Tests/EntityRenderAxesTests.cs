// playtest-bugs #76 (items face the mirror of their map angle) and #7 (carried flag counter-rotates): the entity
// render orientation must be DarkPlaces' entity matrix — Matrix4x4_CreateFromQuakeEntity (matrixlib.c), with
// CL_UpdateNetworkEntity's alias-model pitch reversal (cl_main.c) — not a Godot Euler built from the yaw.
//
// The renderer (game/Coords.EntityBasis) takes EntityRenderAxes' three Quake vectors and only runs them through
// Coords.ToGodot = (x, z, -y) as basis columns, so pinning the Quake vectors here pins the rendered basis.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Math;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using VortexArena.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class EntityRenderAxesTests
{
    private readonly ITestOutputHelper _out;
    public EntityRenderAxesTests(ITestOutputHelper output) => _out = output;

    // DarkPlaces matrixlib.c Matrix4x4_CreateFromQuakeEntity, transcribed: the three COLUMNS of the rotation part
    // (the images of model +X, +Y, +Z) for (pitch, yaw, roll) in degrees.
    private static void DpEntityMatrix(Vector3 a, out Vector3 c0, out Vector3 c1, out Vector3 c2)
    {
        double yaw = a.Y * Math.PI / 180, pitch = a.X * Math.PI / 180, roll = a.Z * Math.PI / 180;
        double sy = Math.Sin(yaw), cy = Math.Cos(yaw), sp = Math.Sin(pitch), cp = Math.Cos(pitch);
        double sr = Math.Sin(roll), cr = Math.Cos(roll);
        // m[row][col]
        double m00 = cp * cy, m01 = sr * sp * cy + cr * -sy, m02 = cr * sp * cy + -sr * -sy;
        double m10 = cp * sy, m11 = sr * sp * sy + cr * cy, m12 = cr * sp * sy + -sr * cy;
        double m20 = -sp, m21 = sr * cp, m22 = cr * cp;
        c0 = new Vector3((float)m00, (float)m10, (float)m20);
        c1 = new Vector3((float)m01, (float)m11, (float)m21);
        c2 = new Vector3((float)m02, (float)m12, (float)m22);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, string what)
        => Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{what}: expected {expected}, got {actual}");

    // The port's Quake→Godot axis swap (game/Coords.cs ToGodot), and what Godot's Euler `Rotation = (0, φ, 0)`
    // does to the model's local +X: Basis.from_euler's Y matrix carries (1,0,0) to (cos φ, 0, −sin φ). Both are
    // plain arithmetic, so the old renderer path can be reproduced here without the engine (the engine result was
    // checked once headless: Rotation.Y = −90° sends +X to (0, 0, +1)).
    private static Vector3 ToGodot(Vector3 q) => new(q.X, q.Z, -q.Y);
    private static Vector3 ToQuake(Vector3 g) => new(g.X, -g.Z, g.Y);
    private static Vector3 GodotEulerYImageOfX(float phiRad) => new(MathF.Cos(phiRad), 0f, -MathF.Sin(phiRad));

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0f, 90f, 0f)]
    [InlineData(0f, 135f, 0f)]
    [InlineData(0f, -90f, 0f)]
    [InlineData(18.8f, 0f, 0f)]
    [InlineData(-17.8f, -2.06f, 16.12f)]
    [InlineData(4.7f, 132.74f, 13.63f)]
    public void Axes_AreDarkPlacesEntityMatrixColumns(float pitch, float yaw, float roll)
    {
        var a = new Vector3(pitch, yaw, roll);

        // Non-alias model (brush / OBJ / sprite): DP uses the angles as given.
        DpEntityMatrix(a, out Vector3 c0, out Vector3 c1, out Vector3 c2);
        EntityRenderAxes.Axes(a, reversePitch: false, out Vector3 f, out Vector3 l, out Vector3 u);
        AssertNear(c0, f, "forward");
        AssertNear(c1, l, "left");
        AssertNear(c2, u, "up");

        // Alias model (MD3/IQM/DPM/...): DP negates the pitch first (cl_main.c "reverse pitch direction").
        DpEntityMatrix(new Vector3(-pitch, yaw, roll), out c0, out c1, out c2);
        EntityRenderAxes.Axes(a, reversePitch: true, out f, out l, out u);
        AssertNear(c0, f, "alias forward");
        AssertNear(c1, l, "alias left");
        AssertNear(c2, u, "alias up");
    }

    [Theory]
    [InlineData("models/items/a_rockets.md3", true)]
    [InlineData("models/weapons/g_rl.md3", true)]
    [InlineData("models/player/erebus.iqm", true)]
    [InlineData("models/monsters/golem.dpm", true)]
    [InlineData("progs/player.mdl", true)]
    [InlineData("models/containers/crate01.MD3", true)]
    [InlineData("models/props/cog.obj", false)]       // DP mod_obj
    [InlineData("models/sprites/glow.spr32", false)]  // DP mod_sprite
    [InlineData("*12", false)]                         // brush submodel
    [InlineData("maps/stormkeep.bsp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReversesPitch_FollowsDarkPlacesModAlias(string? model, bool expected)
        => Assert.Equal(expected, EntityRenderAxes.ReversesPitch(model));

    /// <summary>
    /// The playtest case on real data: stormkeep's rocket box at (16 −256 −176) has <c>"angle" "135"</c>. After the
    /// port's own spawn path, the rendered model's forward must point along DP's AngleVectors for yaw 135 — and the
    /// old renderer (<c>Rotation.Y = −yaw</c>) pointed it along yaw 225, the mirror image.
    /// </summary>
    [Fact]
    public void StormkeepRocketBox_FacesItsMapAngle()
    {
        BspData? bsp = LoadMapBsp("stormkeep");
        if (bsp is null) { _out.WriteLine($"no stormkeep map — skipped. {TestPaths.NoMapsReason}"); return; }

        List<EntityDict> dicts = Dicts(bsp);
        EntityDict src = dicts.Single(d => d.ClassName == "item_rockets"
            && d.Fields.TryGetValue("angle", out string? ang) && ang == "135");

        var world = new GameWorld(new CollisionWorld(), dicts);
        world.Boot("dm");
        var spawned = VortexArena.Common.Services.Api.Entities.FindByClass("item_rockets")
            .Where(e => MathF.Abs(e.Origin.X - src.Origin.X) < 1f && MathF.Abs(e.Origin.Y - src.Origin.Y) < 1f)
            .ToList();
        var item = Assert.Single(spawned);
        Assert.Equal(135f, item.Angles.Y, 3);

        EntityRenderAxes.Axes(item.Angles, EntityRenderAxes.ReversesPitch(item.Model), out Vector3 fwd, out _, out _);
        QMath.AngleVectors(new Vector3(0f, 135f, 0f), out Vector3 dpForward, out _, out _);
        AssertNear(dpForward, fwd, "rendered model forward (Quake space)");

        // What the old yaw-only path drew: Godot Euler −yaw applied to the model's +X, read back in Quake space.
        Vector3 oldForward = ToQuake(GodotEulerYImageOfX(-135f * MathF.PI / 180f));
        QMath.AngleVectors(new Vector3(0f, 225f, 0f), out Vector3 mirrored, out _, out _);
        AssertNear(mirrored, oldForward, "old path = the mirror (yaw 225)");

        // And the fixed path, expressed as the Godot basis column the renderer builds: +θ about Godot +Y.
        AssertNear(GodotEulerYImageOfX(135f * MathF.PI / 180f), ToGodot(fwd), "Godot image of model +X");
    }

    /// <summary>
    /// trident's tilted jump-pad prop: <c>misc_gamemodel</c> with <c>angles "-18.8 0 0"</c>. Base flips the pitch in
    /// the spawnfunc (models.qc:207) because DP flips it back for an alias model at draw time; the net effect is the
    /// mapper's angles. Before #76 the port did the spawnfunc flip but not the draw-time one, so the pad tilted the
    /// other way.
    /// </summary>
    [Fact]
    public void TridentJumpPadProp_RendersAtTheMappersPitch()
    {
        BspData? bsp = LoadMapBsp("trident");
        if (bsp is null) { _out.WriteLine($"no trident map — skipped. {TestPaths.NoMapsReason}"); return; }

        EntityDict src = Dicts(bsp).First(d => d.ClassName == "misc_gamemodel"
            && d.Fields.TryGetValue("model", out string? m) && m.EndsWith("xonotic_jumppad01.md3", StringComparison.Ordinal));
        Assert.True(src.Angles.X < -1f, $"expected a negative mapper pitch, got {src.Angles}");

        var world = new GameWorld(new CollisionWorld(), new List<EntityDict> { src });
        world.Boot("dm");
        var prop = Assert.Single(VortexArena.Common.Services.Api.Entities.FindByClass("misc_gamemodel"));
        Assert.Equal(-src.Angles.X, prop.Angles.X, 3); // the spawnfunc flip (Base and port alike)

        EntityRenderAxes.Axes(prop.Angles, EntityRenderAxes.ReversesPitch(prop.Model), out Vector3 f, out Vector3 l, out Vector3 u);
        DpEntityMatrix(src.Angles, out Vector3 c0, out Vector3 c1, out Vector3 c2);
        AssertNear(c0, f, "forward");
        AssertNear(c1, l, "left");
        AssertNear(c2, u, "up");
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static BspData? LoadMapBsp(string map)
    {
        string pack = Path.Combine(TestPaths.Data, "maps", map + ".pk3");
        if (!File.Exists(pack))
            return null;
        using var zip = System.IO.Compression.ZipFile.OpenRead(pack);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals($"maps/{map}.bsp", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return BspReader.Read(ms.ToArray());
    }

    // Mirrors NetGame.BuildEntityDicts: first-class origin/angles, every key kept in Fields.
    private static List<EntityDict> Dicts(BspData bsp)
    {
        var list = new List<EntityDict>();
        foreach (IReadOnlyDictionary<string, string> d in bsp.Entities)
        {
            if (!d.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                continue;
            var ed = new EntityDict { ClassName = cls, Origin = Vec(d, "origin"), Angles = Vec(d, "angles") };
            foreach (var kv in d)
                ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static Vector3 Vec(IReadOnlyDictionary<string, string> d, string key)
    {
        if (!d.TryGetValue(key, out string? s)) return Vector3.Zero;
        float[] p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return p.Length >= 3 ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;
    }
}
