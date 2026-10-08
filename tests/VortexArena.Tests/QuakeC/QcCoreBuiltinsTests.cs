using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.QuakeC;

/// <summary>An in-memory <see cref="IQcHost"/>: a cvar table, a file table, and a record of everything the program said.</summary>
internal sealed class CoreTestHost : IQcHost
{
    public sealed class CvarEntry
    {
        public string Value = "", Default = "", Description = "";
        public int Flags;
    }

    public readonly Dictionary<string, CvarEntry> Cvars = new(StringComparer.Ordinal);
    public readonly StringBuilder Printed = new();
    public readonly List<string> Warnings = new();
    public readonly List<string> Commands = new();
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public readonly Dictionary<string, MemoryStream> Written = new(StringComparer.Ordinal);
    public readonly List<string> PathsAsked = new();
    public Func<string, bool, string?, IReadOnlyList<string>>? SearchHandler;

    public bool Developer { get; set; }
    public bool Utf8Enabled => true;
    public double RealTime { get; set; }

    public void Set(string name, string value, int flags = 0, string description = "") =>
        Cvars[name] = new CvarEntry { Value = value, Default = value, Flags = flags, Description = description };

    public void File(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);
    public string WrittenText(string path) => Encoding.UTF8.GetString(Written[path].ToArray());

    public void Print(string text) => Printed.Append(text);
    public void Warning(string text) => Warnings.Add(text);

    /// <summary>Names asked about that did not exist, with how often: what a real host would have had to supply.</summary>
    public readonly Dictionary<string, int> CvarMisses = new(StringComparer.Ordinal);

    public bool CvarExists(string name)
    {
        if (Cvars.ContainsKey(name)) return true;
        CvarMisses[name] = CvarMisses.GetValueOrDefault(name) + 1;
        return false;
    }
    public string CvarString(string name) => Cvars.TryGetValue(name, out CvarEntry? c) ? c.Value : "";
    public string CvarDefaultString(string name) => Cvars.TryGetValue(name, out CvarEntry? c) ? c.Default : "";
    public string CvarDescription(string name) => Cvars.TryGetValue(name, out CvarEntry? c) ? c.Description : "";
    public int CvarTypeFlags(string name) => Cvars.TryGetValue(name, out CvarEntry? c) ? c.Flags | 1 : 0;

    public float CvarFloat(string name)
    {
        // atof: the numeric prefix.
        string s = CvarString(name).TrimStart();
        int end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] is '.' or '-' or '+' or 'e' or 'E')) end++;
        while (end > 0 && !float.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) end--;
        return end == 0 ? 0 : float.Parse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public void CvarSet(string name, string value)
    {
        if (Cvars.TryGetValue(name, out CvarEntry? c)) c.Value = value;
    }

    public bool RegisterCvar(string name, string value, int flags)
    {
        if (Cvars.ContainsKey(name)) return false;
        Set(name, value, flags);
        return true;
    }

    public IEnumerable<string> CvarNames(string prefix, string antiPrefix) =>
        Cvars.Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && (antiPrefix.Length == 0 || !n.StartsWith(antiPrefix, StringComparison.Ordinal)));

    public void LocalCommand(string text) => Commands.Add(text);

    public Stream? OpenRead(string path)
    {
        PathsAsked.Add(path);
        return Files.TryGetValue(path, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : null;
    }

    public Stream? OpenWrite(string path, bool append)
    {
        PathsAsked.Add(path);
        MemoryStream stream = new();
        if (append && Written.TryGetValue(path, out MemoryStream? old)) stream.Write(old.ToArray());
        Written[path] = stream;
        return stream;
    }

    public IReadOnlyList<string> Search(string pattern, bool caseInsensitive, string? packFile)
    {
        PathsAsked.Add(pattern);
        return SearchHandler?.Invoke(pattern, caseInsensitive, packFile) ?? Array.Empty<string>();
    }

    public string WhichPack(string path)
    {
        PathsAsked.Add(path);
        return path.StartsWith("packed/", StringComparison.Ordinal) ? "data.pk3" : "";
    }
}

/// <summary>
/// The engine-independent builtins against DarkPlaces (Base/darkplaces/prvm_cmds.c, mathlib.c,
/// prvm_edict.c). Each builtin is called directly, the way the VM would on a CALL statement: arguments
/// in the parameter cells, the argument count set, the result read from the return cell.
/// </summary>
public class QcCoreBuiltinsTests
{
    /// <summary>A program declaring every builtin number plus the globals and fields the builtins look up by name.</summary>
    private sealed class Rig
    {
        public readonly QcVm Vm;
        public readonly CoreTestHost Host = new() { RealTime = 100 };
        public readonly QcCoreBuiltins Core;

        public Rig(Action<ProgsBuilder>? extra = null)
        {
            ProgsBuilder b = new();
            b.Int(0, "self", QcType.Entity);
            b.Float(0, "time");
            b.Vector(0, 0, 0, "v_forward");
            b.Vector(0, 0, 0, "v_right");
            b.Vector(0, 0, 0, "v_up");
            // Field defs 1..14, in this order (def 0 is the null def).
            b.Field("classname", QcType.String);   // cell 0
            b.Field("health");                     // 1
            b.Field("flags");                      // 2
            b.Field("origin", QcType.Vector);      // 3..5
            b.Field("angles", QcType.Vector);      // 6..8
            b.Field("ideal_yaw");                  // 9
            b.Field("yaw_speed");                  // 10
            b.Field("idealpitch");                 // 11
            b.Field("pitch_speed");                // 12
            b.Field("chain", QcType.Entity);       // 13
            b.Field("alt", QcType.Entity);         // 14
            b.Field("owner", QcType.Entity);       // 15
            b.Field("think", QcType.Function);     // 16
            b.Field("fld", QcType.Field);          // 17
            for (int number = 1; number <= 642; number++) b.Builtin("b" + number, number);

            int parm = b.Float(0);
            b.Function("double", parmStart: parm, locals: 1, parmSizes: 1);
            b.Emit(QcOp.AddF, parm, parm, ProgsFile.OfsReturn);
            b.Emit(QcOp.Return, ProgsFile.OfsReturn);
            extra?.Invoke(b);
            b.Function("noop");
            b.Emit(QcOp.Return);

            Vm = b.BuildVm();
            Core = new QcCoreBuiltins(Vm, Host);
            Core.Register();
        }

        public void Call(int number, int argCount = 0) => Vm.Execute(Vm.FindFunction("b" + number), argCount);
        public string Fault(int number, int argCount = 0) => Assert.Throws<QcRuntimeException>(() => Call(number, argCount)).Message;

        public float F(int number, params float[] args)
        {
            for (int i = 0; i < args.Length; i++) Vm.SetArgFloat(i, args[i]);
            Call(number, args.Length);
            return Vm.ResultFloat;
        }

        /// <summary>Calls with one argument already in place.</summary>
        public float F1(int number)
        {
            Call(number, 1);
            return Vm.ResultFloat;
        }

        /// <summary>Calls with argument 0 already in place and a float as argument 1.</summary>
        public float F2(int number, float second)
        {
            Vm.SetArgFloat(1, second);
            Call(number, 2);
            return Vm.ResultFloat;
        }

        public void Str(int index, string text) => Vm.SetArgInt(index, Vm.AllocString(text));
        public int Field(string name) => Vm.FindField(name)!.Offset;
        public QcVector Global(string name) => Vm.GlobalVector(Vm.FindGlobal(name)!.Offset);
        public ref int Self => ref Vm.GlobalInt(Vm.FindGlobal("self")!.Offset);

        public int Spawn()
        {
            Call(14);
            return Vm.ResultInt;
        }
    }

    private static void Near(QcVector expected, QcVector actual, int precision = 5)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    // ---- maths ---------------------------------------------------------------------------------------

    [Fact]
    public void MakeVectors_WritesTheThreeGlobals()
    {
        Rig r = new();
        // mathlib.c:444 AngleVectors, yaw 90: forward is +Y, right is +X, up is +Z.
        r.Vm.SetArgVector(0, new QcVector(0, 90, 0));
        r.Call(1, 1);
        Near(new QcVector(0, 1, 0), r.Global("v_forward"));
        Near(new QcVector(1, 0, 0), r.Global("v_right"));
        Near(new QcVector(0, 0, 1), r.Global("v_up"));

        // Pitch is positive DOWN: forward_z = -sin(pitch) (mathlib.c:458).
        r.Vm.SetArgVector(0, new QcVector(90, 0, 0));
        r.Call(1, 1);
        Near(new QcVector(0, 0, -1), r.Global("v_forward"));
        Near(new QcVector(1, 0, 0), r.Global("v_up"));

        // The roll branch (mathlib.c:462): roll 90 turns right to -Z and up to -Y.
        r.Vm.SetArgVector(0, new QcVector(0, 0, 90));
        r.Call(1, 1);
        Near(new QcVector(1, 0, 0), r.Global("v_forward"));
        Near(new QcVector(0, 0, -1), r.Global("v_right"));
        Near(new QcVector(0, -1, 0), r.Global("v_up"));

        Assert.Contains("wrong parameter count", r.Fault(1, 0));
    }

    [Fact]
    public void Normalize_VLen()
    {
        Rig r = new();
        r.Vm.SetArgVector(0, new QcVector(3, 4, 0));
        r.Call(9, 1);
        Assert.Equal(new QcVector(0.6f, 0.8f, 0), r.Vm.ResultVector);
        r.Vm.SetArgVector(0, default);
        r.Call(9, 1);
        Assert.Equal(default, r.Vm.ResultVector); // zero stays zero, no division

        r.Vm.SetArgVector(0, new QcVector(3, 4, 12));
        r.Call(12, 1);
        Assert.Equal(13f, r.Vm.ResultFloat);
    }

    [Theory]
    [InlineData(1f, 1f, 45f)]
    [InlineData(1f, -1f, 315f)]
    [InlineData(-1f, 0f, 180f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, -0.01f, 0f)]   // prvm_cmds.c:570: (int) truncates -0.57 to 0 before the "< 0" wrap, so not 359
    [InlineData(1f, 0.99f, 44f)]   // and 44.7 to 44
    public void VecToYaw(float x, float y, float expected)
    {
        Rig r = new();
        r.Vm.SetArgVector(0, new QcVector(x, y, 77));
        r.Call(13, 1);
        Assert.Equal(expected, r.Vm.ResultFloat);
    }

    [Fact]
    public void VecToAngles()
    {
        Rig r = new();
        QcVector Angles(QcVector forward, QcVector? up = null)
        {
            r.Vm.SetArgVector(0, forward);
            if (up is QcVector u) r.Vm.SetArgVector(1, u);
            r.Call(51, up is null ? 1 : 2);
            return r.Vm.ResultVector;
        }

        Assert.Equal(new QcVector(0, 0, 0), Angles(new QcVector(1, 0, 0)));
        // mathlib.c:654: straight up is pitch -90 before the flip VM_vectoangles asks for, so +90; down is 270.
        Assert.Equal(new QcVector(90, 0, 0), Angles(new QcVector(0, 0, 1)));
        Assert.Equal(new QcVector(270, 0, 0), Angles(new QcVector(0, 0, -5)));
        Near(new QcVector(45, 0, 0), Angles(new QcVector(1, 0, 1)), 4);
        Near(new QcVector(0, 225, 0), Angles(new QcVector(-1, -1, 0)), 4);   // not truncated, unlike vectoyaw
        Near(new QcVector(0, 0.5729387f, 0), Angles(new QcVector(1, 0.01f, 0)), 5);

        // Two-argument form (mathlib.c:671): roll from the up vector. Up along +Y is the left vector: roll -90 = 270.
        Near(new QcVector(0, 0, 270), Angles(new QcVector(1, 0, 0), new QcVector(0, 1, 0)), 4);
        Near(new QcVector(0, 0, 0), Angles(new QcVector(1, 0, 0), new QcVector(0, 0, 1)), 4);
        // Vertical forward takes its yaw from the up vector (mathlib.c:657).
        Near(new QcVector(90, 180, 0), Angles(new QcVector(0, 0, 1), new QcVector(1, 0, 0)), 4);
    }

    [Fact]
    public void VectorVectors()
    {
        Rig r = new();
        r.Vm.SetArgVector(0, new QcVector(0, 0, 5));
        r.Call(432, 1);
        Assert.Equal(new QcVector(0, 0, 1), r.Global("v_forward"));
        Assert.Equal(new QcVector(0, -1, 0), r.Global("v_right"));   // mathlib.c:206
        Assert.Equal(new QcVector(-1, 0, 0), r.Global("v_up"));

        r.Vm.SetArgVector(0, new QcVector(2, 0, 0));
        r.Call(432, 1);
        Assert.Equal(new QcVector(1, 0, 0), r.Global("v_forward"));
        Assert.Equal(new QcVector(0, -1, 0), r.Global("v_right"));
        Assert.Equal(new QcVector(0, 0, 1), r.Global("v_up"));
    }

    [Theory]
    [InlineData(36, 2.5f, 3f)]      // rint: halves away from zero (prvm_cmds.c:1531), not banker's rounding
    [InlineData(36, -2.5f, -3f)]
    [InlineData(36, 0.4f, 0f)]
    [InlineData(36, -1.4f, -1f)]
    [InlineData(37, -1.5f, -2f)]
    [InlineData(38, -1.5f, -1f)]
    [InlineData(43, -3f, 3f)]
    [InlineData(60, 0f, 0f)]
    [InlineData(61, 0f, 1f)]
    [InlineData(62, 16f, 4f)]
    [InlineData(471, 1f, 1.5707964f)]
    [InlineData(472, 1f, 0f)]
    [InlineData(473, 1f, 0.7853982f)]
    [InlineData(475, 0f, 0f)]
    [InlineData(532, 1f, 0f)]
    public void OneArgumentMaths(int builtin, float argument, float expected)
    {
        Assert.Equal(expected, new Rig().F(builtin, argument));
    }

    [Fact]
    public void MathsDomainErrors_AreIeeeResults()
    {
        Rig r = new();
        Assert.True(float.IsNaN(r.F(62, -1)));
        Assert.True(float.IsNaN(r.F(471, 2)));
        Assert.Equal(float.NegativeInfinity, r.F(532, 0));
        Assert.Equal(float.PositiveInfinity, r.F(97, 0, -1));
    }

    [Fact]
    public void MinMaxBoundPowAtan2()
    {
        Rig r = new();
        Assert.Equal(1f, r.F(94, 3, 1));
        Assert.Equal(1f, r.F(94, 3, 1, 2));
        Assert.Equal(-4f, r.F(94, 3, 1, 2, 9, 9, 9, 9, -4));
        Assert.Equal(5f, r.F(95, 1, 5));
        Assert.Equal(9f, r.F(95, 1, 5, 9));
        // The C macros (mathlib.h): a comparison with NaN is false, so the second operand is returned.
        Assert.Equal(1f, r.F(94, float.NaN, 1));
        Assert.True(float.IsNaN(r.F(95, 1, float.NaN)));
        Assert.True(float.IsNaN(r.F(94, float.NaN, 1, 2)));   // the 3+ form keeps the first value instead

        Assert.Equal(3f, r.F(96, 0, 5, 3));
        Assert.Equal(0f, r.F(96, 0, -1, 3));
        Assert.Equal(2f, r.F(96, 0, 2, 3));
        Assert.Equal(5f, r.F(96, 5, 2, 1));   // crossed bounds: mathlib.h:34 tests the minimum first

        Assert.Equal(1024f, r.F(97, 2, 10));
        Assert.Equal(0.7853982f, r.F(474, 1, 1));
        Assert.Contains("wrong parameter count", Assert.Throws<QcRuntimeException>(() => r.F(94, 1)).Message);
        Assert.Contains("wrong parameter count", Assert.Throws<QcRuntimeException>(() => r.F(96, 1, 2)).Message);
    }

    [Theory]
    [InlineData(1f, 4f, 16f)]
    [InlineData(16f, -2f, 4f)]
    [InlineData(-8f, 1f, 16f)]     // prvm_cmds.c:3616: fabs first, the sign is lost
    [InlineData(0f, 5f, 0f)]
    [InlineData(5.9f, 1f, 10f)]    // truncated to 5
    [InlineData(1f, 33f, 2f)]      // the count wraps at 32, as the x86 shift does
    public void BitShift(float number, float quantity, float expected)
    {
        Assert.Equal(expected, new Rig().F(218, number, quantity));
    }

    [Fact]
    public void Modulo()
    {
        Rig r = new();
        Assert.Equal(1f, r.F(245, 7, 3));
        Assert.Equal(-1f, r.F(245, -7, 3));     // prvm_cmds.c:3144: truncated division, sign of the dividend
        Assert.Equal(1.5f, r.F(245, 7.5f, 2));
        Assert.Empty(r.Host.Warnings);
        Assert.Equal(0f, r.F(245, 5, 0));
        Assert.Contains("modulo of 5.000000 by zero", Assert.Single(r.Host.Warnings));
    }

    [Fact]
    public void Random_StaysInsideItsOpenInterval_AndRandomVecInsideTheUnitSphere()
    {
        Rig r = new();
        r.Core.Random = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            r.Call(7);
            Assert.InRange(r.Vm.ResultFloat, float.Epsilon, 0.99999994f);
            r.Call(91);
            QcVector v = r.Vm.ResultVector;
            Assert.True(v.X * v.X + v.Y * v.Y + v.Z * v.Z <= 1f);
        }
        Rig again = new();
        again.Core.Random = new Random(1234);
        r.Core.Random = new Random(1234);
        r.Call(7);
        again.Call(7);
        Assert.Equal(r.Vm.ResultFloat, again.Vm.ResultFloat);
        Assert.Contains("wrong parameter count", r.Fault(7, 1));
    }

    [Fact]
    public void ChangeYaw_ChangePitch()
    {
        Rig r = new();
        int e = r.Spawn();
        // prvm_cmds.c:4810: from 350 toward 10 the short way is +20; the speed limit makes it +15, and 365 wraps to 5.
        r.Vm.FieldFloat(e, r.Field("angles") + 1) = 350;
        r.Vm.FieldFloat(e, r.Field("ideal_yaw")) = 10;
        r.Vm.FieldFloat(e, r.Field("yaw_speed")) = 15;
        r.Self = e;
        r.Call(49);
        Assert.Equal(5f, r.Vm.FieldFloat(e, r.Field("angles") + 1));

        r.Vm.FieldFloat(e, r.Field("angles")) = 10;
        r.Vm.FieldFloat(e, r.Field("pitch_speed")) = 4;
        r.Vm.SetArgInt(0, e);
        r.Call(63, 1);
        Assert.Equal(6f, r.Vm.FieldFloat(e, r.Field("angles")));

        // The world and free entities are refused with a warning.
        r.Self = 0;
        r.Call(49);
        Assert.Contains("can not modify world entity", r.Host.Warnings[^1]);
        r.Vm.SetArgInt(0, e);
        r.Call(15, 1);
        r.Vm.SetArgInt(0, e);
        r.Call(63, 1);
        Assert.Contains("can not modify free entity", r.Host.Warnings[^1]);
    }

    // ---- entities ------------------------------------------------------------------------------------

    [Fact]
    public void Spawn_Remove_AndTheSlotReusePolicy()
    {
        Rig r = new();
        r.Core.StartTime = 0;   // well past the start-up grace period
        Assert.Equal(1, r.Spawn());
        Assert.Equal(2, r.Spawn());
        r.Vm.FieldFloat(2, r.Field("health")) = 50;

        List<int> freed = new(), spawned = new();
        r.Core.EdictFreeing = freed.Add;
        r.Core.EdictSpawned = spawned.Add;
        r.Vm.SetArgInt(0, 2);
        r.Call(15, 1);
        Assert.Equal(new[] { 2 }, freed);
        Assert.Equal(1f, r.F(353, BitConverter.Int32BitsToSingle(2)));   // wasfreed
        Assert.Equal(0f, r.Vm.FieldFloat(2, r.Field("health")));

        // prvm_edict.c:256: not in the same frame; :260: not within a second; after that, yes.
        Assert.Equal(3, r.Spawn());
        r.Host.RealTime = 100.5;
        Assert.Equal(4, r.Spawn());
        r.Host.RealTime = 101.5;
        Assert.Equal(2, r.Spawn());
        Assert.Equal(new[] { 3, 4, 2 }, spawned);

        // prvm_edict.c:258: during the first seconds after load a freed slot is reusable after 0.1 s.
        r.Core.StartTime = 101;
        r.Vm.SetArgInt(0, 3);
        r.Call(15, 1);
        Assert.Equal(5, r.Spawn());
        r.Host.RealTime = 101.7;
        Assert.Equal(3, r.Spawn());
    }

    [Fact]
    public void Remove_RefusesTheWorld_AndWarnsOnDoubleFree()
    {
        Rig r = new();
        r.Host.Developer = true;
        int e = r.Spawn();
        r.Vm.SetArgInt(0, 0);
        r.Call(15, 1);
        Assert.Contains("null entity", r.Host.Warnings[^1]);
        Assert.False(r.Vm.IsFree(0));

        r.Vm.SetArgInt(0, e);
        r.Call(15, 1);
        r.Vm.SetArgInt(0, e);
        r.Call(15, 1);
        Assert.Contains("already freed", r.Host.Warnings[^1]);
        r.Vm.SetArgInt(0, 300);   // inside the edict array, never spawned
        r.Call(15, 1);
        Assert.Contains("already freed", r.Host.Warnings[^1]);
        Assert.Equal(2, r.Host.Warnings.Count(w => w.Contains("already freed")));

        r.Vm.SetArgInt(0, 1_000_000);
        Assert.Contains("out of range", r.Fault(15, 1));
        r.Vm.SetArgInt(0, -1);
        Assert.Contains("out of range", r.Fault(15, 1));
    }

    private static Rig WithFive(out int classname)
    {
        // Entities 1..5: classnames a, b, (unset), b, a; entity 4 is then freed.
        Rig r = new();
        classname = r.Field("classname");
        string?[] names = { "a", "b", null, "b", "a" };
        for (int i = 0; i < names.Length; i++)
        {
            int e = r.Spawn();
            if (names[i] is string name) r.Vm.FieldInt(e, classname) = r.Vm.AllocString(name);
            r.Vm.FieldFloat(e, r.Field("health")) = 10 * e;
            r.Vm.FieldFloat(e, r.Field("flags")) = e;   // 1, 2, 3, 4, 5
        }
        r.Vm.SetArgInt(0, 4);
        r.Call(15, 1);
        return r;
    }

    [Fact]
    public void Find_FindFloat_FindFlags()
    {
        Rig r = WithFive(out int classname);
        int Find(int start, string match)
        {
            r.Vm.SetArgInt(0, start);
            r.Vm.SetArgInt(1, classname);
            r.Str(2, match);
            r.Call(18, 3);
            return r.Vm.ResultInt;
        }
        Assert.Equal(2, Find(0, "b"));
        Assert.Equal(0, Find(2, "b"));      // entity 4 is free; the search ends at the world
        Assert.Equal(1, Find(0, "a"));
        Assert.Equal(5, Find(1, "a"));
        Assert.Equal(3, Find(0, ""));       // prvm_cmds.c:1132: an unset string field matches ""
        Assert.Equal(0, Find(0, "A"));      // strcmp: case matters

        int FindNumber(int builtin, int start, string field, float match)
        {
            r.Vm.SetArgInt(0, start);
            r.Vm.SetArgInt(1, r.Field(field));
            r.Vm.SetArgFloat(2, match);
            r.Call(builtin, 3);
            return r.Vm.ResultInt;
        }
        Assert.Equal(3, FindNumber(98, 0, "health", 30));
        Assert.Equal(0, FindNumber(98, 3, "health", 30));
        Assert.Equal(0, FindNumber(98, 0, "health", 40));   // freed
        // findflags: flags & 6 is non-zero for 2, 3, (4), 5 - bit 2 or bit 4.
        Assert.Equal(2, FindNumber(449, 0, "flags", 6));
        Assert.Equal(3, FindNumber(449, 2, "flags", 6));
        Assert.Equal(5, FindNumber(449, 3, "flags", 6));
        Assert.Equal(0, FindNumber(449, 5, "flags", 6));

        // A field offset is program data: out of range is a fault, not a read of someone else's memory.
        r.Vm.SetArgInt(0, 0);
        r.Vm.SetArgInt(1, 5000);
        r.Str(2, "a");
        Assert.Contains("field offset 5000 is out of bounds", r.Fault(18, 3));
        r.Vm.SetArgInt(1, -1);
        Assert.Contains("out of bounds", r.Fault(98, 3));
        Assert.Contains("wrong parameter count", r.Fault(18, 2));
    }

    [Fact]
    public void FindChain_LinksMatchesBackToTheWorld()
    {
        Rig r = WithFive(out int classname);
        int chain = r.Field("chain"), alt = r.Field("alt");

        r.Vm.SetArgInt(0, classname);
        r.Str(1, "a");
        r.Call(402, 2);
        Assert.Equal(5, r.Vm.ResultInt);                 // the LAST match is the head (prvm_cmds.c:1243)
        Assert.Equal(1, r.Vm.FieldInt(5, chain));
        Assert.Equal(0, r.Vm.FieldInt(1, chain));

        // The optional third argument names another chain field; .chain is left alone.
        r.Vm.SetArgInt(0, r.Field("health"));
        r.Vm.SetArgFloat(1, 20);
        r.Vm.SetArgInt(2, alt);
        r.Call(403, 3);
        Assert.Equal(2, r.Vm.ResultInt);
        Assert.Equal(0, r.Vm.FieldInt(2, alt));
        Assert.Equal(1, r.Vm.FieldInt(5, chain));

        r.Vm.SetArgInt(0, r.Field("flags"));
        r.Vm.SetArgFloat(1, 1);
        r.Call(450, 2);                                  // flags & 1: entities 1, 3, 5
        Assert.Equal(5, r.Vm.ResultInt);
        Assert.Equal(3, r.Vm.FieldInt(5, chain));
        Assert.Equal(1, r.Vm.FieldInt(3, chain));
        Assert.Equal(0, r.Vm.FieldInt(1, chain));

        r.Vm.SetArgInt(0, r.Field("health"));
        r.Vm.SetArgFloat(1, 999);
        r.Call(403, 2);
        Assert.Equal(0, r.Vm.ResultInt);                 // no match: the world

        r.Vm.SetArgInt(2, 99999);
        Assert.Contains("out of bounds", r.Fault(403, 3));
    }

    [Fact]
    public void NextEnt_FToE_EToF_WasFreed()
    {
        Rig r = WithFive(out _);
        int Next(int e)
        {
            r.Vm.SetArgInt(0, e);
            r.Call(47, 1);
            return r.Vm.ResultInt;
        }
        Assert.Equal(1, Next(0));
        Assert.Equal(5, Next(3));   // skips freed 4
        Assert.Equal(0, Next(5));
        Assert.Equal(0, Next(400)); // past the last entity: the world, where the C would walk off the array

        Assert.Equal(3, BitConverter.SingleToInt32Bits(r.F(459, 3)));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(r.F(459, 4)));       // free
        Assert.Equal(0, BitConverter.SingleToInt32Bits(r.F(459, -1)));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(r.F(459, 1e9f)));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(r.F(459, float.NaN)));

        r.Vm.SetArgInt(0, 5);
        r.Call(512, 1);
        Assert.Equal(5f, r.Vm.ResultFloat);
        r.Vm.SetArgInt(0, 3);
        r.Call(353, 1);
        Assert.Equal(0f, r.Vm.ResultFloat);
        r.Vm.SetArgInt(0, int.MaxValue);
        Assert.Contains("out of range", r.Fault(353, 1));
    }

    [Fact]
    public void CopyEntity()
    {
        Rig r = WithFive(out int classname);
        List<int> linked = new();
        r.Core.EdictLinked = linked.Add;
        r.Vm.FieldVector(1, r.Field("origin")) = new QcVector(1, 2, 3);
        r.Vm.SetArgInt(0, 1);
        r.Vm.SetArgInt(1, 2);
        r.Call(400, 2);
        Assert.Equal(new QcVector(1, 2, 3), r.Vm.FieldVector(2, r.Field("origin")));
        Assert.Equal("a", r.Vm.GetString(r.Vm.FieldInt(2, classname)));
        Assert.Equal(10f, r.Vm.FieldFloat(2, r.Field("health")));
        Assert.Equal(new[] { 2 }, linked);

        foreach ((int from, int to, string warning) in new[] { (0, 2, "read world"), (4, 2, "read free"), (1, 0, "modify world"), (1, 4, "modify free") })
        {
            r.Vm.SetArgInt(0, from);
            r.Vm.SetArgInt(1, to);
            r.Call(400, 2);
            Assert.Contains(warning, r.Host.Warnings[^1]);
        }
        Assert.Single(linked);
    }

    [Fact]
    public void EntityFieldTable()
    {
        Rig r = new();
        Assert.Equal(15f, r.F(496));   // 14 fields and the null def (prvm_cmds.c:2134: defs, not cells)
        string Name(float index)
        {
            r.F(497, index);
            return r.Vm.ResultString;
        }
        Assert.Equal("classname", Name(1));
        Assert.Equal("origin", Name(4));
        Assert.Equal("fld", Name(14));
        Assert.Equal("", Name(15));
        Assert.Contains("out of bounds", r.Host.Warnings[^1]);
        Assert.Equal("", Name(-1));

        Assert.Equal((float)QcType.String, r.F(498, 1));
        Assert.Equal((float)QcType.Vector, r.F(498, 4));
        Assert.Equal((float)QcType.Function, r.F(498, 13));
        Assert.Equal(-1f, r.F(498, 99));
    }

    [Fact]
    public void GetAndPutEntityFieldString_RoundTripEveryType()
    {
        Rig r = new();
        int e = r.Spawn(), other = r.Spawn();
        string Get(int index, int edict)
        {
            r.Vm.SetArgFloat(0, index);
            r.Vm.SetArgInt(1, edict);
            r.Call(499, 2);
            return r.Vm.ResultString;
        }
        float Put(int index, int edict, string text)
        {
            r.Vm.SetArgFloat(0, index);
            r.Vm.SetArgInt(1, edict);
            r.Str(2, text);
            r.Call(500, 3);
            return r.Vm.ResultFloat;
        }

        Assert.Equal("", Get(2, e));                        // all-zero: the empty string (prvm_cmds.c:2228)

        Assert.Equal(1f, Put(2, e, "  12.5abc"));           // atof: leading space skipped, trailing junk ignored
        Assert.Equal(12.5f, r.Vm.FieldFloat(e, r.Field("health")));
        Assert.Equal("12.5", Get(2, e));
        Put(2, e, "0.1");
        Assert.Equal("0.100000001", Get(2, e));             // %.9g of the float nearest 0.1 (qdefs.h:194)
        Put(2, e, "1e10");
        Assert.Equal("1e+10", Get(2, e));
        Put(2, e, "0.00001");
        Assert.Equal("9.99999975e-06", Get(2, e));
        Put(2, e, "nonsense");
        Assert.Equal(0f, r.Vm.FieldFloat(e, r.Field("health")));

        Assert.Equal(1f, Put(4, e, "1 2.5 -3"));
        Assert.Equal(new QcVector(1, 2.5f, -3), r.Vm.FieldVector(e, r.Field("origin")));
        Assert.Equal("1 2.5 -3", Get(4, e));
        Put(4, e, "7");                                     // prvm_edict.c:1043: missing components keep their value
        Assert.Equal(new QcVector(7, 2.5f, -3), r.Vm.FieldVector(e, r.Field("origin")));

        Put(1, e, "two\nlines \"quoted\" back\\slash");
        Assert.Equal("two\nlines \"quoted\" back\\slash", r.Vm.GetString(r.Vm.FieldInt(e, r.Field("classname"))));
        Assert.Equal("two\\nlines \\\"quoted\\\" back\\\\slash", Get(1, e));   // prvm_edict.c:538: escaped for save files

        Assert.Equal(1f, Put(12, e, other.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(other, r.Vm.FieldInt(e, r.Field("owner")));
        Assert.Equal("2", Get(12, e));
        Assert.Equal(0f, Put(12, e, "99999999"));           // would index outside the edict array
        Assert.Equal(0f, Put(12, e, "-3"));
        Assert.Equal(other, r.Vm.FieldInt(e, r.Field("owner")));

        Assert.Equal(1f, Put(13, e, "double"));
        Assert.Equal(r.Vm.FindFunction("double"), r.Vm.FieldInt(e, r.Field("think")));
        Assert.Equal("double", Get(13, e));
        Assert.Equal(0f, Put(13, e, "no_such_function"));
        r.Vm.FieldInt(e, r.Field("think")) = 123456;
        Assert.Equal("bad function 123456 (invalid!)", Get(13, e));

        Assert.Equal(1f, Put(14, e, ".health"));
        Assert.Equal(r.Field("health"), r.Vm.FieldInt(e, r.Field("fld")));
        Assert.Equal(".health", Get(14, e));
        Assert.Equal(0f, Put(14, e, "health"));             // prvm_edict.c:1068: must start with a dot
        Assert.Equal(0f, Put(14, e, ".nosuch"));

        // Bad index and free entity: warning and the empty result, no fault.
        Assert.Equal(0f, Put(99, e, "1"));
        Assert.Equal("", Get(-5, e));
        r.Vm.SetArgInt(0, other);
        r.Call(15, 1);
        Assert.Equal(0f, Put(2, other, "1"));
        Assert.Contains("is free", r.Host.Warnings[^1]);
        Assert.Equal("", Get(2, other));
        r.Vm.SetArgFloat(0, 2);
        r.Vm.SetArgInt(1, 5_000_000);
        Assert.Contains("out of range", r.Fault(499, 2));
    }

    [Fact]
    public void CallFunction_IsFunction()
    {
        Rig r = new();
        r.Vm.SetArgFloat(0, 21);
        r.Str(1, "double");
        r.Call(605, 2);
        Assert.Equal(42f, r.Vm.ResultFloat);

        // A builtin is called with the argument count unchanged, the name included (prvm_cmds.c:5762):
        // min(3, 1, <the name's handle read as a float>).
        r.Vm.SetArgFloat(0, 3);
        r.Vm.SetArgFloat(1, 1);
        r.Vm.SetArgInt(2, r.Vm.EngineString("b94"));
        r.Call(605, 3);
        Assert.Equal(1f, r.Vm.ResultFloat);

        r.Str(0, "nope");
        Assert.Contains("function nope not found", r.Fault(605, 1));
        r.Str(0, "");
        Assert.Contains("Bad string", r.Fault(605, 1));
        r.Str(0, "b600");   // a declared builtin nothing implements
        Assert.Contains("no such builtin #600", r.Fault(605, 1));

        r.Str(0, "double");
        r.Call(607, 1);
        Assert.Equal(1f, r.Vm.ResultFloat);
        r.Str(0, "triple");
        r.Call(607, 1);
        Assert.Equal(0f, r.Vm.ResultFloat);
    }

    [Fact]
    public void EPrint_Error_ObjError()
    {
        Rig r = new();
        int e = r.Spawn();
        r.Vm.FieldFloat(e, r.Field("health")) = 75;
        r.Vm.FieldVector(e, r.Field("origin")) = new QcVector(1, 2, 3);
        r.Vm.FieldInt(e, r.Field("classname")) = r.Vm.AllocString("player");
        r.Vm.SetArgInt(0, e);
        r.Call(31, 1);
        string printed = r.Host.Printed.ToString();
        Assert.Contains("\ntest EDICT 1:\n", printed);
        Assert.Contains("classname      player\n", printed);   // names padded to 14 (prvm_edict.c:715)
        Assert.Contains("health         75\n", printed);
        Assert.Contains("origin         '1 2 3'\n", printed);
        Assert.DoesNotContain("flags", printed);                // zero fields are skipped

        // error(): prints, dumps self, and ends the program with the message.
        r.Self = e;
        r.Str(0, "it ");
        r.Str(1, "broke");
        QcRuntimeException fault = Assert.Throws<QcRuntimeException>(() => r.Call(10, 2));
        Assert.Contains("Program error in function", fault.Message);
        Assert.Contains("it broke", fault.Message);
        Assert.Contains("ERROR in", r.Host.Printed.ToString());

        // objerror(): prints, REMOVES self, and returns (prvm_cmds.c:403 has no error_cmd).
        r.Host.Printed.Clear();
        r.Str(0, "bad object");
        r.Call(11, 1);
        Assert.True(r.Vm.IsFree(e));
        Assert.Contains("OBJECT ERROR", r.Host.Printed.ToString());
        Assert.Contains("bad object", r.Host.Printed.ToString());
        r.Self = 0;
        r.Call(11, 1);   // self is the world: nothing to remove
        Assert.False(r.Vm.IsFree(0));

        Assert.Contains("break statement", r.Fault(6));
    }

    // ---- cvars and the console -----------------------------------------------------------------------

    [Fact]
    public void Cvar_CvarString_AndThePrivateRule()
    {
        Rig r = new();
        r.Host.Set("g_speed", "320.5", flags: 2, description: "how fast");
        r.Host.Set("rcon_password", "hunter2", flags: 4);
        r.Host.Set("empty", "");

        r.Str(0, "g_speed");
        r.Call(45, 1);
        Assert.Equal(320.5f, r.Vm.ResultFloat);
        r.Str(0, "g_");           // the name is built from every argument (prvm_cmds.c:704)
        r.Str(1, "speed");
        r.Call(448, 2);
        Assert.Equal("320.5", r.Vm.ResultString);

        // prvm_cmds.c:686: a private cvar reads as 0 and "", though cvar_type admits it exists.
        r.Str(0, "rcon_password");
        r.Call(45, 1);
        Assert.Equal(0f, r.Vm.ResultFloat);
        r.Call(448, 1);
        Assert.Equal("", r.Vm.ResultString);
        r.Call(495, 1);
        Assert.Equal(5f, r.Vm.ResultFloat);

        r.Str(0, "g_speed");
        r.Call(495, 1);
        Assert.Equal(3f, r.Vm.ResultFloat);
        r.Call(482, 1);
        Assert.Equal("320.5", r.Vm.ResultString);
        r.Call(518, 1);
        Assert.Equal("how fast", r.Vm.ResultString);

        r.Str(0, "missing");
        r.Call(45, 1);
        Assert.Equal(0f, r.Vm.ResultFloat);
        r.Call(495, 1);
        Assert.Equal(0f, r.Vm.ResultFloat);

        // An empty value is still a string, not the null string: "if (cvar_string(x))" is true in DarkPlaces.
        r.Str(0, "empty");
        r.Call(448, 1);
        Assert.Equal("", r.Vm.ResultString);
        Assert.NotEqual(0, r.Vm.ResultInt);

        // prvm_cmds.c:62: a name that is empty or starts with whitespace kills the program.
        r.Str(0, "");
        Assert.Contains("Bad string", r.Fault(45, 1));
        r.Str(0, " g_speed");
        Assert.Contains("Bad string", r.Fault(448, 1));
        Assert.Contains("wrong parameter count", r.Fault(45, 0));
    }

    [Fact]
    public void CvarSet_RegisterCvar_LocalCmd_CheckExtension()
    {
        Rig r = new();
        r.Host.Set("g_speed", "320");
        r.Host.Set("locked", "1", flags: 32);

        r.Str(0, "g_speed");
        r.Str(1, "4");
        r.Str(2, "00");
        r.Call(72, 3);
        Assert.Equal("400", r.Host.Cvars["g_speed"].Value);

        r.Str(0, "locked");
        r.Str(1, "0");
        r.Call(72, 2);
        Assert.Equal("1", r.Host.Cvars["locked"].Value);
        Assert.Contains("read-only", r.Host.Warnings[^1]);
        r.Str(0, "nosuch");
        r.Call(72, 2);
        Assert.Contains("not found", r.Host.Warnings[^1]);
        Assert.False(r.Host.Cvars.ContainsKey("nosuch"));

        float Register(string name, string value, float? flags = null)
        {
            r.Str(0, name);
            r.Str(1, value);
            if (flags is float f) r.Vm.SetArgFloat(2, f);
            r.Call(93, flags is null ? 2 : 3);
            return r.Vm.ResultFloat;
        }
        Assert.Equal(1f, Register("cl_new", "7"));
        Assert.Equal("7", r.Host.Cvars["cl_new"].Value);
        Assert.Equal(0f, Register("cl_new", "8"));                 // already exists
        Assert.Equal("7", r.Host.Cvars["cl_new"].Value);
        Assert.Equal(1f, Register("cl_saved", "1", 32));
        Assert.Equal(32, r.Host.Cvars["cl_saved"].Flags);
        Assert.Equal(0f, Register("cl_wild", "1", 4096));          // cmd.h:60 CF_MAXFLAGSVAL
        Assert.Equal(0f, Register("cl_wild", "1", -1));
        Assert.False(r.Host.Cvars.ContainsKey("cl_wild"));

        r.Str(0, "echo ");
        r.Str(1, "hi\n");
        r.Call(46, 2);
        Assert.Equal("echo hi\n", Assert.Single(r.Host.Commands));

        r.Core.Extensions.Add("DP_QC_SINCOSSQRTPOW");
        r.Str(0, "dp_qc_sincossqrtpow");                           // strcasecmp
        r.Call(99, 1);
        Assert.Equal(1f, r.Vm.ResultFloat);
        r.Str(0, "DP_NOT_A_THING");
        r.Call(99, 1);
        Assert.Equal(0f, r.Vm.ResultFloat);
    }

    // ---- files ---------------------------------------------------------------------------------------

    [Fact]
    public void FOpen_FGets_ReadsLinesAndTellsBlankFromEndOfFile()
    {
        Rig r = new();
        r.Host.File("data/notes.txt", "alpha\r\nbeta\n\ngamma\rlast");
        r.Str(0, "notes.txt");
        float handle = r.F2(110, 0);
        Assert.Equal(0f, handle);

        List<(string Text, bool Null)> lines = new();
        for (int i = 0; i < 6; i++)
        {
            r.F(112, handle);
            lines.Add((r.Vm.ResultString, r.Vm.ResultInt == 0));
        }
        // prvm_cmds.c:2054: a blank line is an empty NON-null string; only end of file is the null string.
        Assert.Equal(new[] { ("alpha", false), ("beta", false), ("", false), ("gamma", false), ("last", false), ("", true) }, lines);

        r.F(111, handle);
        r.F(112, handle);
        Assert.Contains("no such file handle 0", r.Host.Warnings[^1]);
        r.F(111, 9999);
        Assert.Contains("invalid file handle 9999", r.Host.Warnings[^1]);
        r.F(111, float.NaN);
        Assert.Contains("invalid file handle", r.Host.Warnings[^1]);

        // Reading falls back from data/ to the bare name (prvm_cmds.c:1941); a missing file is -1.
        r.Host.File("maps/x.ent", "{}");
        r.Str(0, "maps/x.ent");
        Assert.Equal(0f, r.F2(110, 0));
        Assert.Equal(new[] { "data/maps/x.ent", "maps/x.ent" }, r.Host.PathsAsked.TakeLast(2));
        r.Str(0, "nothing.txt");
        Assert.Equal(-1f, r.F2(110, 0));
        r.Str(0, "notes.txt");
        Assert.Equal(-3f, r.F2(110, 7));
        Assert.Contains("no such mode 7", r.Host.Warnings[^1]);
    }

    [Fact]
    public void FOpen_WriteAndAppend_GoUnderData()
    {
        Rig r = new();
        r.Str(0, "out.cfg");
        float handle = r.F2(110, 2);
        r.Vm.SetArgFloat(0, handle);
        r.Str(1, "line one\n");
        r.Call(113, 2);
        r.F(111, handle);
        Assert.Equal("line one\n", r.Host.WrittenText("data/out.cfg"));

        r.Str(0, "out.cfg");
        handle = r.F2(110, 1);
        r.Vm.SetArgFloat(0, handle);
        r.Str(1, "line two\n");
        r.Call(113, 2);
        r.F(111, handle);
        Assert.Equal("line one\nline two\n", r.Host.WrittenText("data/out.cfg"));

        // The write budget: nothing past it reaches the host.
        r.Core.MaxWriteBytes = 20;
        r.Str(0, "big.txt");
        handle = r.F2(110, 2);
        r.Vm.SetArgFloat(0, handle);
        r.Str(1, "0123456789");
        r.Call(113, 2);
        Assert.Contains("limit of 20 bytes", r.Host.Warnings[^1]);
        Assert.Equal("", r.Host.WrittenText("data/big.txt"));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("c:/windows/win.ini")]
    [InlineData("dir\\file.txt")]
    [InlineData("a//b")]
    [InlineData("./a")]
    [InlineData("a/.hidden")]
    [InlineData("a/../../b")]
    [InlineData("a\0b")]
    public void FOpen_RefusesPathsThatLeaveTheDataArea(string path)
    {
        // fs.c:2619 FS_CheckNastyPath. The host must never even be asked.
        Rig r = new();
        for (int mode = 0; mode <= 2; mode++)
        {
            r.Vm.SetArgInt(0, r.Vm.AllocString(path));
            Assert.Equal(-1f, r.F2(110, mode));
        }
        r.Vm.SetArgInt(0, r.Vm.AllocString(path));
        r.Call(503, 1);
        r.Vm.SetArgInt(0, r.Vm.AllocString(path));
        Assert.Equal(-4f, r.F1(530));
        r.Vm.SetArgInt(0, r.Vm.AllocString(path));
        r.Vm.SetArgFloat(1, 0);
        r.Vm.SetArgFloat(2, 0);
        r.Call(444, 3);
        Assert.Equal(-1f, r.Vm.ResultFloat);
        Assert.Empty(r.Host.PathsAsked);
        Assert.True(QcCoreBuiltins.IsNastyPath(path));
    }

    [Fact]
    public void FOpen_RunsOutOfHandlesAt256()
    {
        Rig r = new();
        r.Host.File("data/a.txt", "x");
        for (int i = 0; i < QcCoreBuiltins.MaxOpenFiles; i++)
        {
            r.Str(0, "a.txt");
            Assert.Equal((float)i, r.F2(110, 0));
        }
        r.Str(0, "a.txt");
        Assert.Equal(-2f, r.F2(110, 0));
        Assert.Contains("ran out of file handles (max 256)", r.Host.Warnings[^1]);

        r.F(111, 100);            // a closed handle is the next one handed out
        r.Str(0, "a.txt");
        Assert.Equal(100f, r.F2(110, 0));
        r.Core.Reset();
        r.Str(0, "a.txt");
        Assert.Equal(0f, r.F2(110, 0));
    }

    [Fact]
    public void Search()
    {
        Rig r = new();
        string? packSeen = "unset";
        r.Host.SearchHandler = (pattern, insensitive, pack) =>
        {
            packSeen = pack;
            return pattern == "maps/*.bsp" && insensitive ? new[] { "maps/a.bsp", "maps/b.bsp" } : Array.Empty<string>();
        };
        float Begin(string pattern, string? pack = null)
        {
            r.Str(0, pattern);
            r.Vm.SetArgFloat(1, 1);
            r.Vm.SetArgFloat(2, 1);
            if (pack is not null) r.Str(3, pack);
            r.Call(444, pack is null ? 3 : 4);
            return r.Vm.ResultFloat;
        }

        Assert.Equal(0f, Begin("maps/*.bsp"));
        Assert.Null(packSeen);
        Assert.Equal(1f, Begin("maps/*.bsp", "data.pk3"));
        Assert.Equal("data.pk3", packSeen);
        Assert.Equal(-1f, Begin("sound/*.ogg"));        // no matches is a failure (prvm_cmds.c:3208)

        Assert.Equal(2f, r.F(446, 0));
        r.Vm.SetArgFloat(0, 0);
        r.Vm.SetArgFloat(1, 1);
        r.Call(447, 2);
        Assert.Equal("maps/b.bsp", r.Vm.ResultString);
        r.Vm.SetArgFloat(1, 2);
        r.Call(447, 2);
        Assert.Contains("invalid filenum 2", r.Host.Warnings[^1]);

        r.F(445, 0);
        r.F(446, 0);
        Assert.Contains("no such handle 0", r.Host.Warnings[^1]);
        r.F(445, -1);
        Assert.Contains("invalid handle -1", r.Host.Warnings[^1]);
        Assert.Equal(0f, Begin("maps/*.bsp"));          // the freed handle is reused
        Assert.Equal(1f, r.F(446, 1) - 1);              // and handle 1 still answers

        for (int i = 2; i < QcCoreBuiltins.MaxOpenSearches; i++) Assert.Equal((float)i, Begin("maps/*.bsp"));
        Assert.Equal(-2f, Begin("maps/*.bsp"));
        Assert.Contains("ran out of search handles (max 128)", r.Host.Warnings[^1]);
        Assert.Contains("Bad string", Assert.Throws<QcRuntimeException>(() => Begin("")).Message);
    }

    [Fact]
    public void WhichPack()
    {
        Rig r = new();
        r.Str(0, "packed/file.txt");
        r.Call(503, 1);
        Assert.Equal("data.pk3", r.Vm.ResultString);
        r.Str(0, "loose.txt");
        r.Call(503, 1);
        Assert.Equal("", r.Vm.ResultString);
    }

    // ---- entity text -----------------------------------------------------------------------------------

    private static Rig WithSpawnFunctions() => new(b =>
    {
        int counter = b.Float(0, "counter"), one = b.Float(1);
        b.Function("spawnfunc_item");
        b.Emit(QcOp.AddF, counter, one, counter);
        b.Emit(QcOp.Return);
        b.Function("bare");
        b.Emit(QcOp.AddF, counter, b.Float(10), counter);
        b.Emit(QcOp.Return);
    });

    [Fact]
    public void LoadFromData_SpawnsAnEntityPerBlock_AndRunsItsSpawnFunction()
    {
        Rig r = WithSpawnFunctions();
        r.Str(0, """
            // a comment
            { "classname" "item" "health" "5" "origin" "1 2 3" "_compiler" "ignored" "nosuchfield" "x" }
            { "classname" "bare" /* found without the spawnfunc_ prefix */ "angle" "90" "health" "1" }
            { "classname" "unknown" }
            { "health" "3" }
            """);
        r.Call(529, 1);

        Assert.Equal(11f, r.Vm.GlobalFloat(r.Vm.FindGlobal("counter")!.Offset));
        Assert.Equal(5f, r.Vm.FieldFloat(1, r.Field("health")));
        Assert.Equal(new QcVector(1, 2, 3), r.Vm.FieldVector(1, r.Field("origin")));
        Assert.Equal("item", r.Vm.GetString(r.Vm.FieldInt(1, r.Field("classname"))));
        // prvm_edict.c:1316: "angle" is the yaw of "angles".
        Assert.Equal(new QcVector(0, 90, 0), r.Vm.FieldVector(2, r.Field("angles")));
        // No spawn function, then no classname: both removed - and with reuse allowed while loading
        // (prvm_edict.c:1535) the fourth block took the third one's slot.
        Assert.Equal(4, r.Vm.NumEdicts);
        Assert.True(r.Vm.IsFree(3));
        Assert.Contains("No classname for", r.Host.Printed.ToString());

        r.Str(0, "{ \"classname\" \"item\"");
        Assert.Contains("EOF without closing brace", r.Fault(529, 1));
        r.Str(0, "junk");
        Assert.Contains("when expecting {", r.Fault(529, 1));
        r.Str(0, "{ \"classname\" }");
        Assert.Contains("closing brace without data", r.Fault(529, 1));
    }

    [Fact]
    public void LoadFromFile_ReadsTheBareName()
    {
        Rig r = WithSpawnFunctions();
        r.Host.File("maps/test.ent", "{ \"classname\" \"item\" }\n{ \"classname\" \"item\" }\n");
        r.Str(0, "maps/test.ent");
        r.Call(530, 1);
        Assert.Equal(2f, r.Vm.GlobalFloat(r.Vm.FindGlobal("counter")!.Offset));
        Assert.Equal(new[] { "maps/test.ent" }, r.Host.PathsAsked);
        r.Str(0, "maps/missing.ent");
        Assert.Equal(-1f, r.F1(530));
    }

    [Fact]
    public void ParseEntityData_WriteToFile()
    {
        Rig r = new();
        int e = r.Spawn();
        r.Vm.SetArgInt(0, e);
        r.Str(1, "{ \"classname\" \"a \\\"b\\\"\" \"health\" \"7\" \"owner\" \"1\" \"think\" \"double\" \"fld\" \".health\" \"origin\" \"0 0 8\" }");
        r.Call(613, 2);
        Assert.Equal(7f, r.Vm.FieldFloat(e, r.Field("health")));
        Assert.Equal("a \"b\"", r.Vm.GetString(r.Vm.FieldInt(e, r.Field("classname"))));   // escapes on, as for a save file

        r.Str(0, "ent.txt");
        float handle = r.F2(110, 2);
        r.Vm.SetArgFloat(0, handle);
        r.Vm.SetArgInt(1, e);
        r.Call(606, 2);
        r.F(111, handle);
        // prvm_edict.c:746 PRVM_ED_Write: non-zero fields in def order, values as PRVM_UglyValueString.
        Assert.Equal(
            "{\n\"classname\" \"a \\\"b\\\"\"\n\"health\" \"7\"\n\"origin\" \"0 0 8\"\n\"owner\" \"1\"\n\"think\" \"double\"\n\"fld\" \".health\"\n}\n",
            r.Host.WrittenText("data/ent.txt"));

        r.Vm.SetArgInt(0, e);
        r.Str(1, "no brace");
        Assert.Contains("Couldn't parse entity data", r.Fault(613, 2));
        r.Vm.SetArgInt(0, 300);
        r.Str(1, "{ }");
        r.Call(613, 2);            // never spawned is not "free"; an empty block changes nothing
        r.Vm.SetArgInt(0, e);
        r.Call(15, 1);
        r.Vm.SetArgInt(0, e);
        r.Str(1, "{ \"health\" \"1\" }");
        Assert.Contains("is free", r.Fault(613, 2));
    }

    // ---- misc ------------------------------------------------------------------------------------------

    [Fact]
    public void Print_DPrint_CoreDump_PrecacheFile()
    {
        Rig r = new();
        r.Str(0, "a");
        r.Str(1, "b");
        r.Str(2, "c\n");
        r.Call(339, 3);
        Assert.Equal("abc\n", r.Host.Printed.ToString());

        r.Str(0, "debug\n");
        r.Call(25, 1);
        Assert.Equal("abc\n", r.Host.Printed.ToString());   // not a developer: dprint is silent
        r.Host.Developer = true;
        r.Call(25, 1);
        Assert.Equal("abc\ndebug\n", r.Host.Printed.ToString());

        r.Call(28);
        Assert.Equal("prvm_edicts test\n", r.Host.Commands[^1]);
        r.Call(29);
        Assert.True(r.Core.Trace);
        r.Call(30);
        Assert.False(r.Core.Trace);
        r.Call(642);

        int handle = r.Vm.AllocString("sound/x.wav");
        r.Vm.SetArgInt(0, handle);
        r.Call(68, 1);
        Assert.Equal(handle, r.Vm.ResultInt);
    }

    [Fact]
    public void VarString_IsCutAtTheTempStringSize()
    {
        Rig r = new();
        for (int i = 0; i < 8; i++) r.Str(i, new string((char)('a' + i), 4000));
        r.Call(339, 8);
        string printed = r.Host.Printed.ToString();
        Assert.Equal(r.Vm.MaxStringLength - 1, printed.Length);   // prvm_cmds.c:280: 16383 characters and a terminator
        Assert.EndsWith(new string('e', 383), printed);
        Assert.Contains("will truncate", Assert.Single(r.Host.Warnings));
    }

    [Fact]
    public void GetTime()
    {
        Rig r = new();
        r.Host.RealTime = 130.25;
        r.Core.DirtyTime = () => 5000.5;
        r.Core.FrameDirtyTime = () => 5000.25;
        r.Core.CdTrackPosition = () => 12;
        Assert.Equal(130.25f, r.F(519));
        Assert.Equal(30.25f, r.F(519, 0));    // since the program was loaded (at RealTime 100)
        Assert.Equal(5000.5f, r.F(519, 1));
        Assert.Equal(0.25f, r.F(519, 2));
        Assert.Equal(130.25f, r.F(519, 3));
        Assert.Equal(12f, r.F(519, 4));
        Assert.Equal(130.25f, r.F(519, 77));
        Assert.Contains("unsupported timer", r.Host.Warnings[^1]);
    }

    [Fact]
    public void StrFTime()
    {
        Rig r = new();
        r.Core.Clock = () => new DateTimeOffset(2024, 2, 29, 13, 5, 9, TimeSpan.Zero);
        string Format(string format)
        {
            r.Vm.SetArgFloat(0, 0);
            r.Str(1, format);
            r.Call(478, 2);
            return r.Vm.ResultString;
        }
        Assert.Equal("2024-02-29 13:05:09", Format("%Y-%m-%d %H:%M:%S"));
        Assert.Equal("Thu Thursday Feb February 060 PM 01 24 100%", Format("%a %A %b %B %j %p %I %y 100%%"));
        Assert.Equal("Thu Feb 29 13:05:09 2024", Format("%c"));
        Assert.Equal("02/29/24 13:05:09 1709211909 +0000 GMT", Format("%D %T %s %z %Z"));
        Assert.Equal("4 4 08 09 09 2024", Format("%u %w %U %W %V %G"));
        Assert.Equal("%q trailing %", Format("%q trailing %"));
        // A result that cannot fit is the empty string, as when C's strftime returns 0.
        Assert.Equal("", Format(string.Concat(Enumerable.Repeat("%A%B", 1500))));
        Assert.Contains("wrong parameter count", r.Fault(478, 1));
    }

    // ---- autocvars -------------------------------------------------------------------------------------

    [Fact]
    public void Autocvars_CreateMissingCvars_AndLoadExistingOnes()
    {
        ProgsBuilder b = new();
        b.Float(3, "autocvar_cl_whole");
        b.Float(0.1f, "autocvar_cl_fraction");
        b.Float(16777217f, "autocvar_cl_large");
        b.Vector(1, 2.5f, -0.75f, "autocvar_cl_vec");
        b.Float(9, "autocvar_cl_vec_x");                       // a component def: not an autocvar of its own
        b.Int(b.String("hello"), "autocvar_cl_text", QcType.String);
        b.Float(1, "autocvar_existing");
        b.Vector(9, 9, 9, "autocvar_existing_vec");
        b.Int(0, "autocvar_existing_text", QcType.String);
        b.Float(5, "autocvar_secret");
        b.Int(0, "autocvar_wrongtype", QcType.Entity);
        b.Float(2, "not_an_autocvar");
        b.Function("main");
        b.Emit(QcOp.Return);
        QcVm vm = b.BuildVm();
        float F(string name) => vm.GlobalFloat(vm.FindGlobal(name)!.Offset);
        string S(string name) => vm.GetString(vm.GlobalInt(vm.FindGlobal(name)!.Offset));

        CoreTestHost host = new();
        host.Set("existing", "7.5");
        host.Set("existing_vec", " 4 5");
        host.Set("existing_text", "from cvar");
        host.Set("secret", "42", flags: 4);
        QcAutocvars autocvars = new(vm, host);
        Assert.Equal(8, autocvars.Bind());   // five created, three loaded; the private and the mistyped ones are left alone

        // prvm_edict.c:2753: integers print as integers, other floats with the fewest digits that round-trip.
        Assert.Equal("3", host.Cvars["cl_whole"].Value);
        Assert.Equal("0.1", host.Cvars["cl_fraction"].Value);
        Assert.Equal("16777216", host.Cvars["cl_large"].Value);
        Assert.Equal("1 2.5 -0.75", host.Cvars["cl_vec"].Value);
        Assert.Equal("hello", host.Cvars["cl_text"].Value);
        Assert.Equal("hello", S("autocvar_cl_text"));
        Assert.False(host.Cvars.ContainsKey("cl_vec_x"));
        Assert.False(host.Cvars.ContainsKey("wrongtype"));

        Assert.Equal(7.5f, F("autocvar_existing"));
        Assert.Equal(new QcVector(4, 5, 0), vm.GlobalVector(vm.FindGlobal("autocvar_existing_vec")!.Offset));   // missing component is 0
        Assert.Equal("from cvar", S("autocvar_existing_text"));
        Assert.Equal(5f, F("autocvar_secret"));                 // prvm_edict.c:2838: a private cvar is not copied in
        Assert.Contains("private cvar", host.Printed.ToString());
        Assert.Contains("invalid type", host.Printed.ToString());

        // Later changes are pushed by the host.
        host.CvarSet("cl_whole", "12");
        host.CvarSet("cl_vec", "0 0 1");
        Assert.Equal(3f, F("autocvar_cl_whole"));
        Assert.True(autocvars.Update("cl_whole"));
        Assert.Equal(12f, F("autocvar_cl_whole"));
        Assert.False(autocvars.Update("not_bound"));
        Assert.False(autocvars.Update("secret"));
        autocvars.UpdateAll();
        Assert.Equal(new QcVector(0, 0, 1), vm.GlobalVector(vm.FindGlobal("autocvar_cl_vec")!.Offset));

        // A string autocvar owns exactly one string however often it changes.
        int strings = vm.ZonedStringCount;
        for (int i = 0; i < 500; i++)
        {
            host.CvarSet("cl_text", "value " + i);
            autocvars.Update("cl_text");
        }
        Assert.Equal("value 499", S("autocvar_cl_text"));
        Assert.Equal(strings, vm.ZonedStringCount);

        // Empty is a real string, not the null one.
        host.CvarSet("cl_text", "");
        autocvars.Update("cl_text");
        Assert.Equal("", S("autocvar_cl_text"));
        Assert.NotEqual(0, vm.GlobalInt(vm.FindGlobal("autocvar_cl_text")!.Offset));
        Assert.Equal(strings, vm.ZonedStringCount);
    }

    // ---- hostile input ---------------------------------------------------------------------------------

    [Fact]
    public void EveryBuiltin_OnGarbageArguments_FaultsCleanlyOrReturns()
    {
        // Whatever a program passes, a builtin either works or raises QcRuntimeException - never an
        // index fault, a hang, or a path handed to the host.
        Rig r = WithSpawnFunctions();
        r.Host.SearchHandler = (_, _, _) => new[] { "a" };
        for (int i = 0; i < 20; i++) r.Spawn();
        Random random = new(99);
        int[] interesting = { 0, 1, -1, 2, 19, 20, 21, 511, 512, 100000, int.MaxValue, int.MinValue, 0x40000000, 0x20000000, 0x7FC00000, unchecked((int)0xFF800000) };
        int[] numbers = r.Core.Registered.Select(b => b.Number).ToArray();
        Assert.True(numbers.Length > 80);

        for (int round = 0; round < 6000; round++)
        {
            int number = numbers[random.Next(numbers.Length)];
            if (number is 10 or 6) continue;   // error and break always fault, by design
            for (int a = 0; a < 8; a++)
            {
                int cell = random.Next(4) switch
                {
                    0 => interesting[random.Next(interesting.Length)],
                    1 => BitConverter.SingleToInt32Bits(random.Next(-30, 700)),
                    2 => r.Vm.AllocString(random.Next(5) switch { 0 => "", 1 => "../x", 2 => "{ \"health\" \"1\" }", 3 => "double", _ => "classname" }),
                    _ => random.Next(),
                };
                r.Vm.SetArgVector(a, new QcVector(BitConverter.Int32BitsToSingle(cell), random.Next(-5, 5), BitConverter.Int32BitsToSingle(random.Next())));
            }
            r.Self = random.Next(30);
            try { r.Call(number, random.Next(9)); }
            catch (QcRuntimeException) { }
        }

        Assert.All(r.Host.PathsAsked, path => Assert.False(QcCoreBuiltins.IsNastyPath(path), path));
        Assert.True(r.Vm.NumEdicts <= r.Vm.EdictLimit);
        r.Core.Dispose();
    }
}
