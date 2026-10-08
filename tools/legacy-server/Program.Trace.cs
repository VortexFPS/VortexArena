// legacy-server trace: boot a level and ask its collision world one question - a box swept from one
// point to another - printing everything the answer holds. "--mode nudge" writes out the steps of
// PHYS_NudgeOutOfSolid for the box at --from, "--mode leaves" the world brushes and patch triangles
// that box touches, "--mode scan" which points inside it are in solid,
// "--mode pvs" whether the entities named by --maps (numbers) touch the fat PVS of --from. For tracking down a difference from
// DarkPlaces (compare with "prvm_edict server N" and a QuakeC tracebox on the reference server).
using System.Globalization;
using System.Text;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private static QcVector ParseVector(string text)
    {
        string[] parts = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return new QcVector(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    // "--mode file --from IN --to OUT": the traces listed in IN, one a line, answered one a line in OUT -
    // the same two formats as the "probetraces" command of the instrumented reference server
    // (_scratch/collision-probe/ has the patch that adds it to a private copy of DarkPlaces and the
    // script that builds and runs it), so the two files can be compared number for number. A line of IN:
    //   type passent sx sy sz minx miny minz maxx maxy maxz ex ey ez extend mask
    // (mask 0 = SV_GenericHitSuperContentsMask of the pass entity). A line of OUT:
    //   fraction endpos(3) startsolid allsolid worldstartsolid bmodelstartsolid startdepth startdepthnormal(3)
    //   planenormal(3) planedist ent hitsupercontents hitq3surfaceflags startsupercontents texture
    // startdepth is measured for a box at rest under MOVE_NOMONSTERS / MOVE_WORLDONLY only (0 otherwise),
    // and startsupercontents is the contents at the start point, not of every brush the box starts in.
    private static int TraceFile(Options o, SvqcHost host)
    {
        if (o.From is null || o.To is null) return Fail("--mode file needs --from INFILE and --to OUTFILE");
        static string G(float f) => ((double)f).ToString("G9", CultureInfo.InvariantCulture);
        int count = 0;
        using StreamWriter w = new(o.To, false, new UTF8Encoding(false));
        foreach (string line in File.ReadLines(o.From))
        {
            if (line.StartsWith('#')) continue;
            string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 16) continue;
            float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
            int type = int.Parse(p[0], CultureInfo.InvariantCulture), pass = int.Parse(p[1], CultureInfo.InvariantCulture), mask = int.Parse(p[15], CultureInfo.InvariantCulture);
            QcVector start = new(F(2), F(3), F(4)), mins = new(F(5), F(6), F(7)), maxs = new(F(8), F(9), F(10)), end = new(F(11), F(12), F(13));
            if (pass <= 0 || pass >= host.Vm.NumEdicts) pass = 0;
            if (mask == 0) mask = host.GenericHitSuperContentsMask(pass);
            SvTrace t = host.World.Trace(start, mins, maxs, end, type, pass, mask, F(14));
            float depth = 0;
            QcVector depthNormal = default;
            bool atRest = start.X == end.X && start.Y == end.Y && start.Z == end.Z;
            if (atRest && (type == SvWorld.MoveNoMonsters || type == SvWorld.MoveWorldOnly))
                host.World.StartDepth(start, mins, maxs, type == SvWorld.MoveWorldOnly, pass, mask, out depth, out depthNormal, out _, out _);
            w.WriteLine(string.Join(' ', G(t.Fraction), G(t.EndPos.X), G(t.EndPos.Y), G(t.EndPos.Z),
                t.StartSolid ? 1 : 0, t.AllSolid ? 1 : 0, t.WorldStartSolid ? 1 : 0, t.BModelStartSolid ? 1 : 0,
                G(depth), G(depthNormal.X), G(depthNormal.Y), G(depthNormal.Z),
                G(t.PlaneNormal.X), G(t.PlaneNormal.Y), G(t.PlaneNormal.Z), G(t.PlaneDist),
                t.Ent, t.HitContents, t.HitQ3SurfaceFlags, t.StartContents, string.IsNullOrEmpty(t.HitTextureName) ? "-" : t.HitTextureName));
            count++;
        }
        Log($"trace file: {count} traces written to {o.To}; mesh models read {host.World.ModelCollision.ModelsRead}, held {host.World.ModelCollision.CachedMeshes} ({host.World.ModelCollision.CachedBytes} bytes), refused {host.World.ModelCollision.Refused}");
        return 0;
    }

    // "--mode bench": what a trace costs on this map through the client half's world (no program, so the
    // world alone): short player-box moves, long lines and long player-box moves between random points.
    private static int TraceBench(Options o, SvEnvironment env)
    {
        VortexArena.Legacy.Csqc.BspLegacyWorld world = new(env.Files);
        long before = GC.GetTotalMemory(true);
        System.Diagnostics.Stopwatch load = System.Diagnostics.Stopwatch.StartNew();
        if (!world.LoadMap($"maps/{o.Map}.bsp")) return Fail(world.LoadError ?? "no map");
        load.Stop();
        long after = GC.GetTotalMemory(true);
        world.Bounds(out QcVector lo, out QcVector hi);
        Log($"bench {o.Map}: world built in {load.Elapsed.TotalMilliseconds:0} ms, {(after - before) / (1024.0 * 1024.0):0.0} MB held, {world.Collision!.Brushes.Count} leaves ({world.Collision.Brushes.Count(b => b.IsTriangle)} triangles), bih nodes {world.Collision.Bih?.NodeCount ?? 0}");
        int huge = world.Collision.Brushes.Count(b => { System.Numerics.Vector3 size = b.Maxs - b.Mins; return MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > 4096; });
        Log($"bench {o.Map}: {huge} leaves are more than 4096 units long on some axis; the world's box is ({lo.X:0} {lo.Y:0} {lo.Z:0})..({hi.X:0} {hi.Y:0} {hi.Z:0})");
        Random random = new(12345);
        const int n = 20000;
        QcVector[] starts = new QcVector[n], ends = new QcVector[n], shortEnds = new QcVector[n];
        for (int i = 0; i < n; i++)
        {
            float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
            starts[i] = new QcVector(R(lo.X, hi.X), R(lo.Y, hi.Y), R(lo.Z, hi.Z));
            ends[i] = new QcVector(R(lo.X, hi.X), R(lo.Y, hi.Y), R(lo.Z, hi.Z));
            shortEnds[i] = new QcVector(starts[i].X + R(-8, 8), starts[i].Y + R(-8, 8), starts[i].Z + R(-8, 8));
        }
        QcVector pmin = new(-16, -16, -24), pmax = new(16, 16, 45);
        const int mask = 0x1 | 0x20 | 0x100;
        double Time(Func<int, VortexArena.Legacy.Csqc.LegacyTrace> trace, out double checksum)
        {
            // the best of five passes: the first ones run before the runtime has finished optimising the code
            double best = double.MaxValue;
            checksum = 0;
            for (int pass = 0; pass < 5; pass++)
            {
                checksum = 0;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) checksum += trace(i).Fraction;
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / n);
            }
            return best;
        }
        double boxNs = Time(i => world.Trace(starts[i], pmin, pmax, shortEnds[i], 0, 0, mask, isLine: false), out double c1);
        double lineNs = Time(i => world.Trace(starts[i], default, default, ends[i], 0, 0, mask, isLine: true), out double c2);
        double longBoxNs = Time(i => world.Trace(starts[i], pmin, pmax, ends[i], 0, 0, mask, isLine: false), out double c3);
        Log(string.Create(CultureInfo.InvariantCulture, $"bench {o.Map}: short player-box move {boxNs:0} ns; long line {lineNs:0} ns; long player-box move {longBoxNs:0} ns (checksums {c1:0.###} {c2:0.###} {c3:0.###})"));
        return 0;
    }

    private static int TraceMode(Options o)
    {
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        StringBuilder printLine = new();
        using SvEnvironment? env = Environment(o, warnings, printLine);
        if (env is null) return 1;
        if (o.Mode == "bench") return TraceBench(o, env);
        SvqcHost? host = env.StartLevel(o.Map, new SvqcHostOptions { MaxClients = 8, KeepRunningAfterFault = true, RandomSeed = 1 });
        if (host is null) return Fail("the level could not be started");
        using SvqcHost _ = host;
        if (o.HasSeconds)
        {
            // let the level settle first (items dropped, movers started), as a server that has run a while
            double ticRate = env.Cvars.GetFloat("sys_ticrate") > 0 ? env.Cvars.GetFloat("sys_ticrate") : 1.0 / 72, until = host.Time + o.Seconds;
            host.ExecuteCommands();
            while (host.Time < until && !host.QuitRequested)
            {
                host.RealTime += ticRate;
                if (!host.RunFrame(ticRate)) break;
            }
        }
        if (o.Mode == "file") return TraceFile(o, host);
        QcVector start = ParseVector(o.From ?? "0 0 0"), end = ParseVector(o.To ?? o.From ?? "0 0 0");
        QcVector mins = ParseVector(o.Mins ?? "0 0 0"), maxs = ParseVector(o.Maxs ?? "0 0 0");
        foreach (int type in new[] { SvWorld.MoveWorldOnly, SvWorld.MoveNoMonsters, SvWorld.MoveNormal })
        {
            SvTrace t = host.World.Trace(start, mins, maxs, end, type, 0, SvWorld.ContentsSolid | 0x10000 /* body */ | 0x8000000 /* playerclip */);
            string cls = t.Ent > 0 ? host.Vm.GetString(host.Vm.FieldInt(t.Ent, host.F.ClassName)) : "";
            Log(string.Create(CultureInfo.InvariantCulture, $"type {type}: fraction {t.Fraction:R} endpos {V(t.EndPos)} startsolid {t.StartSolid} allsolid {t.AllSolid} worldstartsolid {t.WorldStartSolid} bmodelstartsolid {t.BModelStartSolid} ent {t.Ent} {cls} normal {V(t.PlaneNormal)} dist {t.PlaneDist:R} startcontents 0x{t.StartContents:X} hitcontents 0x{t.HitContents:X} surfaceflags 0x{t.HitQ3SurfaceFlags:X} texture {t.HitTextureName}"));
        }
        if (o.Mode == "pvs")
        {
            // Which entities' boxes touch the fat PVS of the point --from (no camera eyes): by their
            // absolute box, and by that box grown 40 units.
            byte[] pvs = new byte[host.World.PvsBytes];
            host.World.FatPvs(start, 8, pvs, merge: false);
            foreach (string number in (o.Maps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int e = int.Parse(number, CultureInfo.InvariantCulture);
                QcVector org = host.Vm.FieldVector(e, host.F.Origin), lo = host.Vm.FieldVector(e, host.F.Mins), hi = host.Vm.FieldVector(e, host.F.Maxs);
                QcVector a = new(org.X + lo.X, org.Y + lo.Y, org.Z + lo.Z), b = new(org.X + hi.X, org.Y + hi.Y, org.Z + hi.Z);
                QcVector a2 = new(a.X - 40, a.Y - 40, a.Z - 40), b2 = new(b.X + 40, b.Y + 40, b.Z + 40);
                Log($"entity {e} {host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName))} at {V(org)} effects {host.Vm.FieldFloat(e, host.F.Effects)} modelflags {host.Vm.FieldFloat(e, host.F.ModelFlags)} angles {V(host.Vm.FieldVector(e, host.F.Angles))}: box touches PVS {host.World.BoxTouchingPvs(pvs, a, b)}, grown box {host.World.BoxTouchingPvs(pvs, a2, b2)}, line of sight to centre {host.World.TraceLineOfSight(start, new QcVector((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2), a, b)}");
            }
        }
        if (o.Mode == "nudge")
        {
            // PHYS_NudgeOutOfSolid's loop, written out step by step.
            const float separation = 0.03125f;
            QcVector smins = new(mins.X - separation, mins.Y - separation, mins.Z - separation), smaxs = new(maxs.X + separation, maxs.Y + separation, maxs.Z + separation);
            int mask = SvWorld.ContentsSolid | SvWorld.ContentsBody;
            for (int pass = 0; pass < 2; pass++)
            {
                QcVector test = start;
                for (int bump = 0; bump < 10; bump++)
                {
                    host.World.StartDepth(test, smins, smaxs, pass != 0, 0, mask, out float depth, out QcVector normal, out bool worldSolid, out bool bmodelSolid);
                    Log(string.Create(CultureInfo.InvariantCulture, $"pass {pass} bump {bump}: at '{test.X:R} {test.Y:R} {test.Z:R}' startdepth {depth:R} normal '{normal.X:R} {normal.Y:R} {normal.Z:R}' world {worldSolid} bmodel {bmodelSolid}"));
                    if (-depth <= separation || (!bmodelSolid && !worldSolid) || (pass != 0 && !worldSolid)) { Log("  good location"); pass = 2; break; }
                    QcVector target = new(test.X - depth * normal.X, test.Y - depth * normal.Y, test.Z - depth * normal.Z);
                    SvTrace t = host.World.Trace(test, smins, smaxs, target, pass != 0 ? SvWorld.MoveWorldOnly : SvWorld.MoveNoMonsters, 0, mask, 16);
                    Log(string.Create(CultureInfo.InvariantCulture, $"  trace to target: fraction {t.Fraction:R} endpos '{t.EndPos.X:R} {t.EndPos.Y:R} {t.EndPos.Z:R}' normal {V(t.PlaneNormal)} texture {t.HitTextureName}"));
                    if (t.Fraction != 0) test = t.EndPos; else break;
                }
            }
        }
        if (o.Mode == "leaves" && host.World.Collision is { } collision)
        {
            // The world leaves (brushes, patch triangles) whose box the box at --from touches, in the
            // order they are tested, each with what it is made of.
            List<VortexArena.Engine.Collision.Brush> leaves = new();
            System.Numerics.Vector3 at = new(start.X, start.Y, start.Z), lo = new(mins.X, mins.Y, mins.Z), hi = new(maxs.X, maxs.Y, maxs.Z);
            collision.Query(at + lo, at + hi, leaves);
            foreach (VortexArena.Engine.Collision.Brush leaf in leaves)
            {
                StringBuilder line = new();
                line.Append(CultureInfo.InvariantCulture, $"leaf {(leaf.IsTriangle ? "triangle" : "brush")} contents 0x{leaf.Contents:X} texture {leaf.Texture} planes {leaf.Sides.Length} bounds ({leaf.Mins.X:R} {leaf.Mins.Y:R} {leaf.Mins.Z:R})..({leaf.Maxs.X:R} {leaf.Maxs.Y:R} {leaf.Maxs.Z:R})");
                if (leaf.IsTriangle)
                    foreach (System.Numerics.Vector3 p in leaf.Points) line.Append(CultureInfo.InvariantCulture, $" ({p.X:R} {p.Y:R} {p.Z:R})");
                else
                    foreach (VortexArena.Engine.Collision.BrushPlane side in leaf.Sides) line.Append(CultureInfo.InvariantCulture, $" [{side.Normal.X:0.####} {side.Normal.Y:0.####} {side.Normal.Z:0.####} {side.Dist:R}]");
                Log(line.ToString());
            }
        }
        if (o.Mode == "scan")   // which sample points inside the box are in solid
        {
            int solid = 0, total = 0;
            float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue, hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue;
            for (float x = mins.X; x <= maxs.X; x += 2)
                for (float y = mins.Y; y <= maxs.Y; y += 2)
                    for (float z = mins.Z; z <= maxs.Z; z += 2)
                    {
                        total++;
                        QcVector p = new(start.X + x, start.Y + y, start.Z + z);
                        if ((host.World.PointSuperContents(p) & SvWorld.ContentsSolid) == 0) continue;
                        solid++;
                        lox = MathF.Min(lox, p.X); loy = MathF.Min(loy, p.Y); loz = MathF.Min(loz, p.Z);
                        hix = MathF.Max(hix, p.X); hiy = MathF.Max(hiy, p.Y); hiz = MathF.Max(hiz, p.Z);
                    }
            Log(string.Create(CultureInfo.InvariantCulture, $"scan: {solid} of {total} sample points are in solid; their bounds ({lox} {loy} {loz}) .. ({hix} {hiy} {hiz})"));
        }
        return 0;
    }
}
