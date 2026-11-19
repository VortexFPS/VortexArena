using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Playtest 2026-07-31 #74: "a spot in the centre of boil kills you instantly". boil's only trigger_hurt (model
/// <c>*3</c>) is ONE brush — the box x 409..1638, y 0..1228, z -517..-414 cut by a diagonal plane (x + y &gt;= 1638),
/// i.e. a triangular prism under the corner of the map. The engine fires a trigger's touch whenever the two
/// bounding boxes overlap (DP SV_LinkEdict_TouchAreaGrid does the same), and Base's trigger_hurt_touch then calls
/// <c>EXACTTRIGGER_TOUCH</c> to confirm the toucher is inside the real brush. The port skipped that second step,
/// so the whole bbox killed — including the floor of the central pit, which sits inside the bbox but well clear
/// of the wedge. These tests pin the brush test (<see cref="MapMover.ExactTriggerTouch"/>) on synthetic geometry
/// and on the shipped map.
/// </summary>
[Collection("GlobalState")]
public sealed class ExactTriggerTouchTests
{
    private static readonly string DataDir = TestPaths.Data;
    private readonly ITestOutputHelper _out;
    public ExactTriggerTouchTests(ITestOutputHelper output) => _out = output;

    // ---- synthetic: a wedge brush as an inline model -------------------------------------------------

    /// <summary>A world with one inline model "*1": boil's wedge in miniature — the box 0..100 cubed with the
    /// half where x + y &lt; 100 cut away by a diagonal plane (normal (-1,-1,0)/√2).</summary>
    private static EngineServices WedgeWorld()
    {
        Brush box = Brush.FromBox(Vector3.Zero, new Vector3(100f), SuperContents.Solid);
        Vector3 n = Vector3.Normalize(new Vector3(-1f, -1f, 0f));
        var sides = box.Sides.ToList();
        sides.Add(new BrushPlane(n, Vector3.Dot(n, new Vector3(50f, 50f, 0f)))); // keeps x + y >= 100
        // Corner points of the cut prism (the builder computes these for real brushes; the test only needs
        // them for the quick bbox reject, so the uncut box corners are a valid superset).
        var wedge = new Brush(sides.ToArray(), box.Points, box.EdgeDirs, SuperContents.Solid);

        var services = new EngineServices(new CollisionWorld());
        BspCollisionBuilder.RegisterSubmodels(
            new[] { new BspCollisionBuilder.Submodel("*1", Vector3.Zero, new Vector3(100f), new[] { wedge }) },
            services.ModelsImpl);
        return services;
    }

    private static Entity Trigger(EngineServices s)
    {
        Entity t = s.Entities.Spawn();
        t.Model = "*1";
        MapMover.InitTrigger(t);
        return t;
    }

    private static Entity BoxAt(EngineServices s, Vector3 origin)
    {
        Entity e = s.Entities.Spawn();
        s.Entities.SetSize(e, new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        s.Entities.SetOrigin(e, origin);
        return e;
    }

    [Fact]
    public void BoxInTheCutAwayCornerOfTheBboxDoesNotTouchTheBrush()
    {
        EngineServices s = WedgeWorld();
        IEngineServices prev = Api.Services;
        Api.Services = s;
        try
        {
            Entity trig = Trigger(s);
            Assert.Equal(new Vector3(100f), trig.Maxs); // setmodel gave it the model's bbox

            Entity corner = BoxAt(s, new Vector3(20f, 20f, 50f)); // inside the bbox, far from the diagonal
            Assert.True(CollisionWorld.BoxesOverlap(corner.AbsMin, corner.AbsMax, trig.AbsMin, trig.AbsMax));
            Assert.False(MapMover.ExactTriggerTouch(trig, corner));

            Entity inside = BoxAt(s, new Vector3(80f, 80f, 50f));
            Assert.True(MapMover.ExactTriggerTouch(trig, inside));

            // Touching the diagonal face from outside counts (the toucher box grows by 1 unit, as in QC).
            // Box at (33,33): its max corner (49,49) sums to 98; +1 expansion → 100, exactly on the plane.
            Entity edge = BoxAt(s, new Vector3(33f, 33f, 50f));
            Assert.True(MapMover.ExactTriggerTouch(trig, edge));
            Entity justOff = BoxAt(s, new Vector3(32f, 32f, 50f)); // 48+48+2 = 98 < 100
            Assert.False(MapMover.ExactTriggerTouch(trig, justOff));
        }
        finally { Api.Services = prev; }
    }

    [Fact]
    public void TriggerWithoutAModelIsItsBoundingBox()
    {
        EngineServices s = WedgeWorld();
        IEngineServices prev = Api.Services;
        Api.Services = s;
        try
        {
            Entity trig = s.Entities.Spawn();
            MapMover.InitTrigger(trig);
            s.Entities.SetSize(trig, Vector3.Zero, new Vector3(100f));
            s.Entities.SetOrigin(trig, Vector3.Zero);
            Assert.True(MapMover.ExactTriggerTouch(trig, BoxAt(s, new Vector3(20f, 20f, 50f))));
        }
        finally { Api.Services = prev; }
    }

    // ---- real data: boil -----------------------------------------------------------------------------

    private GameWorld? BootMap(string map, VirtualFileSystem vfs)
    {
        string bspPath = $"maps/{map}.bsp";
        if (!vfs.Exists(bspPath)) { _out.WriteLine($"{bspPath} missing — skipped"); return null; }
        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp);
        var world = new GameWorld(built.World, EntityDicts(bsp)) { MapName = map };
        world.BrushModels = built.Submodels;
        world.MapBsp = bsp;
        world.Boot("dm");
        return world;
    }

    /// <summary>
    /// The #74 spot: the ramp just south of the map centre (y ≈ -16, x 396..588, floor z -473..-398) runs along
    /// the trigger_hurt's bbox edge at y = 0. A player walking up it has a box that reaches into the bbox (by the
    /// 1-unit touch expansion and the hull's 16-unit half-width) while staying ~1000 units from the wedge's
    /// diagonal face — it died there instantly. Scan that strip on an 8-unit grid, stand a player hull on every
    /// floor whose box overlaps the bbox, and check both the brush test and the real touch function.
    /// </summary>
    [Fact]
    public void Boil_FloorInsideTheHurtBboxButOutsideTheBrushDoesNotKill()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));
        GameWorld? world = BootMap("boil", vfs);
        if (world is null) return;

        List<Entity> hurts = Api.Entities.FindByClass("trigger_hurt").ToList();
        Assert.Single(hurts);
        Entity hurt = hurts[0];
        _out.WriteLine($"trigger_hurt bbox {hurt.AbsMin} .. {hurt.AbsMax}");

        Vector3 mins = new(-16f, -16f, -24f), maxs = new(16f, 16f, 45f);
        var spots = new List<Vector3>();
        int falsePositives = 0;
        for (float x = 380f; x <= 760f; x += 8f)
        for (float y = -48f; y <= 48f; y += 8f)
        {
            // Every floor in this column: scan down for free space, drop the hull to the floor, continue below it.
            for (float z0 = -300f; z0 > -680f; z0 -= 8f)
            {
                Vector3 start = new(x, y, z0);
                if (Api.Trace.Trace(start, mins, maxs, start, MoveFilter.NoMonsters, null).StartSolid) continue;
                TraceResult down = Api.Trace.Trace(start, mins, maxs, new Vector3(x, y, -900f), MoveFilter.NoMonsters, null);
                if (down.Fraction >= 1f) break;
                Vector3 stand = down.EndPos;
                z0 = stand.Z - 60f;

                Entity p = Api.Entities.Spawn();
                Api.Entities.SetSize(p, mins, maxs);
                Api.Entities.SetOrigin(p, stand);
                // What the engine's touch pass sees: the toucher box (+1, DP areagrid) overlaps the trigger bbox.
                if (CollisionWorld.BoxesOverlap(p.AbsMin - Vector3.One, p.AbsMax + Vector3.One, hurt.AbsMin, hurt.AbsMax))
                {
                    spots.Add(stand);
                    if (MapMover.ExactTriggerTouch(hurt, p)) falsePositives++;
                }
                Api.Entities.Remove(p);
            }
        }
        _out.WriteLine($"standing spots touching the hurt bbox: {spots.Count} (e.g. {(spots.Count > 0 ? spots[0] : default)}); judged inside the brush: {falsePositives}");
        Assert.True(spots.Count > 0, "precondition: boil has floor whose player box reaches the hurt bbox (the #74 spot)");
        Assert.Equal(0, falsePositives);

        // The real touch function on a live creature standing there: no damage.
        Entity victim = Api.Entities.Spawn();
        victim.Flags |= EntFlags.Monster;
        victim.TakeDamage = DamageMode.Yes;
        victim.SetResource(ResourceType.Health, 100f);
        Api.Entities.SetSize(victim, mins, maxs);
        Api.Entities.SetOrigin(victim, spots[0]);
        hurt.Touch!(hurt, victim);
        Assert.Equal(100f, victim.GetResource(ResourceType.Health));
    }

    /// <summary>The wedge still kills: a box inside the brush is touched.</summary>
    [Fact]
    public void Boil_InsideTheWedgeStillTouches()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));
        GameWorld? world = BootMap("boil", vfs);
        if (world is null) return;

        Entity hurt = Api.Entities.FindByClass("trigger_hurt").Single();
        Entity p = Api.Entities.Spawn();
        Api.Entities.SetSize(p, new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        Api.Entities.SetOrigin(p, new Vector3(1400f, 1000f, -470f)); // x + y = 2400, well inside
        Assert.True(MapMover.ExactTriggerTouch(hurt, p));
    }

    /// <summary>
    /// A second map, a different shape: glowplant's dmg-50 trigger_hurt <c>*12</c> is TWO thin panels
    /// (x -192..-64 and 64..192, y 1024..1032, z 480..608) with a 128-unit gap between them. Its bbox spans the
    /// gap, so with bbox-only touches a player passing through the gap took damage; Base's EXACTTRIGGER_TOUCH
    /// only bites inside a panel.
    /// </summary>
    [Fact]
    public void Glowplant_GapBetweenTwoHurtPanelsDoesNotHurt()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));
        GameWorld? world = BootMap("glowplant", vfs);
        if (world is null) return;

        Entity hurt = Api.Entities.FindByClass("trigger_hurt").Single(e => e.Model == "*12");
        _out.WriteLine($"glowplant *12 bbox {hurt.AbsMin} .. {hurt.AbsMax}");
        Vector3 mins = new(-16f, -16f, -24f), maxs = new(16f, 16f, 45f);

        Entity gap = Api.Entities.Spawn();
        Api.Entities.SetSize(gap, mins, maxs);
        Api.Entities.SetOrigin(gap, new Vector3(0f, 1028f, 530f));
        Assert.True(CollisionWorld.BoxesOverlap(gap.AbsMin, gap.AbsMax, hurt.AbsMin, hurt.AbsMax)); // the engine fires the touch
        Assert.False(MapMover.ExactTriggerTouch(hurt, gap));

        Entity panel = Api.Entities.Spawn();
        Api.Entities.SetSize(panel, mins, maxs);
        Api.Entities.SetOrigin(panel, new Vector3(128f, 1028f, 530f));
        Assert.True(MapMover.ExactTriggerTouch(hurt, panel));
    }

    private static List<EntityDict> EntityDicts(BspData bsp)
    {
        var list = new List<EntityDict>(bsp.Entities.Count);
        foreach (IReadOnlyDictionary<string, string> dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                continue;
            var ed = new EntityDict { ClassName = cls, Origin = Vec(dict, "origin"), Angles = Vec(dict, "angles") };
            foreach (KeyValuePair<string, string> kv in dict)
                ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static Vector3 Vec(IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s)) return Vector3.Zero;
        string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return Vector3.Zero;
        return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture),
            float.Parse(p[2], CultureInfo.InvariantCulture));
    }
}
