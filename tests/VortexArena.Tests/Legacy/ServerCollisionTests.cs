using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The server's collision world against what a DarkPlaces dedicated server does with the same maps.
/// The numbers asserted here were read off the reference server ("prvm_edicts server" and its console
/// a few seconds after "map"): where the program's DropToFloor and move-out-of-solid passes leave
/// items and spawn points depends on exactly how the engine collides with a map's curved surfaces
/// (Engine.Collision.DarkPlacesPatchCollision), how far it runs a trace past its end (collision_extend*:
/// TraceExtension), and in which order it tests the leaves a box starts in (CollisionBih). Every test
/// needs <c>../Base</c> and returns early without it. Traces recorded from that server by the thousand
/// are checked in DarkPlacesTraceParityTests.
/// </summary>
public class ServerCollisionTests
{
    private readonly ITestOutputHelper _output;
    public ServerCollisionTests(ITestOutputHelper output) => _output = output;

    private static readonly QcVector PlayerMins = new(-16, -16, -24), PlayerMaxs = new(16, 16, 45);

    [Fact]
    public void A_spawn_point_beside_a_curved_wall_is_not_in_solid()
    {
        if (!ServerTestRig.HaveData) return;
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        SvWorld world = new(env.Files);
        Assert.True(world.LoadMap("maps/atelier.bsp"), world.LoadError);
        Assert.True(world.PatchTriangles > 100, $"only {world.PatchTriangles} patch triangles");

        // info_player_deathmatch at '-1515 -39 24', nudged one unit off the floor by relocate_spawnpoint,
        // which then asks: tracebox(origin, PL_MIN, PL_MAX, origin). A curved clip wall passes a third
        // of a unit from the box's corner. With patches as thick slabs the box started in solid and the
        // program gave up on the spawn point ("could not get out of solid at all!"); DarkPlaces says clear.
        QcVector spawn = new(-1515, -39, 25);
        SvTrace trace = world.Trace(spawn, PlayerMins, PlayerMaxs, spawn, SvWorld.MoveWorldOnly, 0, SvWorld.ContentsSolid | SvWorld.ContentsBody);
        Assert.False(trace.StartSolid);
        Assert.Equal(1, trace.Fraction);
    }

    [Fact]
    public void A_trace_is_run_past_its_end_and_reports_a_surface_it_stops_just_short_of()
    {
        if (!ServerTestRig.HaveData) return;
        using SvEnvironment env = new(TestPaths.BaseData, writeRoot: null);
        SvWorld world = new(env.Files);
        Assert.True(world.LoadMap("maps/atelier.bsp"), world.LoadError);

        // A two-unit cube straight down onto a floor at z = 0, to stop with its underside a hundredth
        // of a unit above it. (A box: a line that meets one of this floor's patch triangles is backed
        // off by next to nothing - Collision_TraceLineTriangleFloat scales collision_impactnudge by
        // the length of an unnormalised normal - which is DarkPlaces' behaviour and is ported as it is.)
        QcVector lo = new(-1, -1, -1), hi = new(1, 1, 1);
        QcVector start = new(-1400.5f, -39.3f, 25), end = new(-1400.5f, -39.3f, 1.01f);
        SvTrace asGiven = world.Trace(start, lo, hi, end, SvWorld.MoveWorldOnly, 0, SvWorld.ContentsSolid);
        Assert.Equal(1, asGiven.Fraction);   // the move never reaches the plane, so nothing is hit
        // collision_extendtraceboxlength 1: the same move run one unit further finds the floor, and
        // the impact - backed off by collision_impactnudge - lies inside the move that was asked for.
        SvTrace extended = world.Trace(start, lo, hi, end, SvWorld.MoveWorldOnly, 0, SvWorld.ContentsSolid, extend: 1);
        Assert.InRange(extended.Fraction, 0.998f, 0.9999f);
        Assert.Equal(1, extended.PlaneNormal.Z, 3);
        Assert.Equal(0, extended.Ent);
        Assert.InRange(extended.EndPos.Z, 1.02f, 1.06f);   // underside 1/32 above the floor
        // An impact that lies only in the extra length is not an impact.
        SvTrace shortOfIt = world.Trace(start, lo, hi, new QcVector(-1400.5f, -39.3f, 1.5f), SvWorld.MoveWorldOnly, 0, SvWorld.ContentsSolid, extend: 1);
        Assert.Equal(1, shortOfIt.Fraction);
        Assert.Equal(-1, shortOfIt.Ent);
        Assert.Equal(1.5f, shortOfIt.EndPos.Z, 4);
    }

    private static (SvqcHost Host, List<string> Prints) Boot(SvEnvironment env, StringBuilder line, List<string> prints, string map)
    {
        env.SetCvar("bot_number", "0");
        env.SetCvar("g_warmup", "0");
        SvqcHost? host = env.StartLevel(map, new SvqcHostOptions { MaxClients = 8, RandomSeed = 1 });
        Assert.NotNull(host);
        for (int i = 0; i < 64 * 3; i++)
        {
            host!.RealTime += 1.0 / 64;
            host.RunFrame(1.0 / 64);
        }
        return (host!, prints);
    }

    private static SvEnvironment Environment(StringBuilder line, List<string> prints) => new(TestPaths.BaseData, writeRoot: null, print: text =>
    {
        foreach (char c in text)
        {
            if (c != '\n') { line.Append(c); continue; }
            prints.Add(line.ToString());
            line.Clear();
        }
    });

    [Fact]
    public void Items_on_curved_floors_come_to_rest_where_DarkPlaces_leaves_them()
    {
        if (!ServerTestRig.HaveData) return;
        StringBuilder line = new();
        List<string> prints = new();
        using SvEnvironment env = Environment(line, prints);
        SvqcHost host = Boot(env, line, prints, "glowplant").Host;
        using SvqcHost disposeHost = host;
        Assert.Equal(0, host.FaultCount);

        // The reference server's console for this map, in its order. The third line is the one that
        // tells: an item in a symmetric hollow of a curved floor, pushed out to one side - which side
        // is decided by which of two equally deep patch triangles is tested first.
        string[] expected =
        {
            "DropToFloor_QC at \"-1145.22 -810.076 384\": FIXED badly placed entity \"item_cells\" before drop",
            "DropToFloor_QC at \"-1145.22 -810.076 384\": COULD NOT FIX stuck entity \"item_cells\" after drop",
            "DropToFloor_QC at \"-1472 129.512 636.538\": FIXED badly placed entity \"item_health_big\" before drop",
            "DropToFloor_QC at \"-320.969 288 256\": FIXED badly placed entity \"item_health_medium\" before drop",
            "DropToFloor_QC at \"-320.969 -288 264\": FIXED badly placed entity \"item_health_medium\" before drop",
        };
        List<string> ours = prints.Where(p => p.Contains("DropToFloor_QC", StringComparison.Ordinal)).Select(p => p[p.IndexOf("DropToFloor_QC", StringComparison.Ordinal)..]).ToList();
        foreach (string p in ours) _output.WriteLine(p);
        Assert.Equal(expected, ours);

        // ... and where it ended up: "origin '-1472 129.512497 632.03125'" in the reference's prvm_edicts.
        int classname = host.F.ClassName;
        List<QcVector> health = new();
        for (int e = 1; e < host.Vm.NumEdicts; e++)
            if (!host.Vm.IsFree(e) && host.Vm.GetString(host.Vm.FieldInt(e, classname)) == "item_health_big") health.Add(host.Vm.FieldVector(e, host.F.Origin));
        Assert.Contains(health, o => Math.Abs(o.X + 1472) < 0.001 && Math.Abs(o.Y - 129.512497) < 0.001 && Math.Abs(o.Z - 632.03125) < 0.001);
    }

    [Fact]
    public void A_map_that_DarkPlaces_loads_without_complaint_loads_without_complaint()
    {
        if (!ServerTestRig.HaveData) return;
        StringBuilder line = new();
        List<string> prints = new();
        using SvEnvironment env = Environment(line, prints);
        SvqcHost host = Boot(env, line, prints, "atelier").Host;
        using SvqcHost disposeHost = host;
        Assert.Equal(0, host.FaultCount);
        // The reference prints neither for atelier; with slab patches this server printed an
        // "OBJECT ERROR in relocate_spawnpoint" and lost a spawn point.
        Assert.DoesNotContain(prints, p => p.Contains("OBJECT ERROR", StringComparison.Ordinal) || p.Contains("needs FIXING", StringComparison.Ordinal));
        Assert.DoesNotContain(prints, p => p.Contains("DropToFloor_QC", StringComparison.Ordinal));
        // Fidelity of another kind, checked here because the level is up: sv_jumpspeedcap_min is "nan"
        // (no cap) and reaches every client as a stat, bit for bit. The reference sends 0x7FC00000 -
        // atof("nan") - where .NET's own NaN is 0xFFC00000.
        QcDef? cap = host.Vm.FindGlobal("autocvar_sv_jumpspeedcap_min");
        Assert.NotNull(cap);
        Assert.Equal(0x7FC00000, host.Vm.GlobalInt(cap!.Offset));
        int spawns = 0;
        for (int e = 1; e < host.Vm.NumEdicts; e++)
            if (!host.Vm.IsFree(e) && host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName)) == "info_player_deathmatch") spawns++;
        Assert.Equal(13, spawns);
        // 4,397 entities in use at the reference three seconds in (one of them the pending "deferred"
        // call that is also pending here), each at the number it has there.
        int inUse = 0;
        for (int e = 0; e < host.Vm.NumEdicts; e++) if (!host.Vm.IsFree(e) && (e == 0 || e > 8 || host.Vm.FieldInt(e, host.F.ClassName) != 0)) inUse++;
        _output.WriteLine($"{inUse} entities in use of {host.Vm.NumEdicts}");
        Assert.Equal(4397, inUse);
    }

    [Fact]
    public void A_Wavefront_model_gets_the_box_DarkPlaces_gives_it()
    {
        if (!ServerTestRig.HaveData) return;
        StringBuilder line = new();
        List<string> prints = new();
        using SvEnvironment env = Environment(line, prints);
        SvqcHost host = Boot(env, line, prints, "darkzone").Host;
        using SvqcHost disposeHost = host;
        // misc_gamemodel "models/industrial/cogwheel2.obj": a wheel 80 units across and 16 thick, lying
        // flat - which it only is if the file's axes are taken as they are (Xonotic sets
        // mod_obj_orientation 0; with DarkPlaces' default the wheel would stand on its rim). The
        // reference has mins '-56.5685425 -56.5685425 -8' for the ones turned half round (their yaw
        // box) and '-40 -40 -8' for the rest.
        bool found = false, turned = false;
        for (int e = 1; e < host.Vm.NumEdicts; e++)
        {
            if (host.Vm.IsFree(e) || host.Vm.GetString(host.Vm.FieldInt(e, host.F.Model)) != "models/industrial/cogwheel2.obj") continue;
            if (host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName)) != "misc_gamemodel") continue;
            found = true;
            QcVector mins = host.Vm.FieldVector(e, host.F.Mins), maxs = host.Vm.FieldVector(e, host.F.Maxs);
            Assert.Equal(-8f, mins.Z, 3);
            Assert.Equal(8f, maxs.Z, 3);
            if (MathF.Abs(mins.X + 56.5685425f) < 0.001f && MathF.Abs(mins.Y + 56.5685425f) < 0.001f && MathF.Abs(maxs.X - 56.5685425f) < 0.001f) turned = true;
            else Assert.Equal(-40f, mins.X, 3);
        }
        Assert.True(turned, "no cogwheel2.obj entity has the yaw box the reference gives the turned ones");
        Assert.True(found, "the map has no cogwheel2.obj entity");
    }
}
