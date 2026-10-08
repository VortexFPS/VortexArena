// legacy-server trace: boot a level and ask its collision world one question - a box swept from one
// point to another - printing everything the answer holds. "--mode nudge" writes out the steps of
// PHYS_NudgeOutOfSolid for the box at --from, "--mode scan" which points inside it are in solid,
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

    private static int TraceMode(Options o)
    {
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        StringBuilder printLine = new();
        using SvEnvironment? env = Environment(o, warnings, printLine);
        if (env is null) return 1;
        SvqcHost? host = env.StartLevel(o.Map, new SvqcHostOptions { MaxClients = 8, KeepRunningAfterFault = true, RandomSeed = 1 });
        if (host is null) return Fail("the level could not be started");
        using SvqcHost _ = host;
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
