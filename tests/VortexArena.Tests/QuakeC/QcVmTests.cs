using System;
using System.IO;
using System.Linq;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.QuakeC;

/// <summary>
/// The QuakeC VM against DarkPlaces' semantics (Base/darkplaces/prvm_execprogram.h). The cases that
/// matter are the ones where QuakeC is not C#: every value is a 32-bit cell, truth is a bit test,
/// functions share their locals, and a string is a handle whose lifetime the VM owns.
/// </summary>
public class QcVmTests
{
    private const int Ret = ProgsFile.OfsReturn;
    private const int Parm0 = ProgsFile.OfsParm0;
    private const int Parm1 = ProgsFile.OfsParm0 + 3;

    private static float Run(Action<ProgsBuilder, int> body)
    {
        ProgsBuilder b = new();
        int result = b.Float(0);
        b.Function("main");
        body(b, result);
        b.Emit(QcOp.Return, result);
        QcVm vm = b.BuildVm();
        vm.Execute(vm.FindFunction("main"));
        return vm.ResultFloat;
    }

    [Theory]
    [InlineData(QcOp.AddF, 7f, 2f, 9f)]
    [InlineData(QcOp.SubF, 7f, 2f, 5f)]
    [InlineData(QcOp.MulF, 7f, 2f, 14f)]
    [InlineData(QcOp.DivF, 7f, 2f, 3.5f)]
    [InlineData(QcOp.BitAndF, 7.9f, 5.9f, 5f)]      // operands truncate to int first
    [InlineData(QcOp.BitOrF, 8.9f, 1.2f, 9f)]
    [InlineData(QcOp.BitAndF, -1f, 255f, 255f)]
    [InlineData(QcOp.LtF, 1f, 2f, 1f)]
    [InlineData(QcOp.GeF, 1f, 2f, 0f)]
    [InlineData(QcOp.EqF, 0f, -0f, 1f)]             // float compare: the two zeros are equal
    [InlineData(QcOp.NeF, float.NaN, float.NaN, 1f)] // and NaN is unequal to itself
    [InlineData(QcOp.AndF, 1f, 0f, 0f)]
    [InlineData(QcOp.OrF, 0f, 3f, 1f)]
    [InlineData(QcOp.AndF, 1f, -0f, 0f)]            // truth is a bit test in which -0.0 is false
    public void FloatOpcodes(QcOp op, float a, float b, float expected)
    {
        Assert.Equal(expected, Run((p, result) => p.Emit(op, p.Float(a), p.Float(b), result)));
    }

    [Fact]
    public void DivisionByZero_IsTheIeeeResult_NotAnError()
    {
        Assert.Equal(float.PositiveInfinity, Run((p, r) => p.Emit(QcOp.DivF, p.Float(1), p.Float(0), r)));
        Assert.True(float.IsNaN(Run((p, r) => p.Emit(QcOp.DivF, p.Float(0), p.Float(0), r))));
    }

    [Fact]
    public void FloatToInt_IsTheSameOnEveryCpu()
    {
        // Out-of-range and NaN conversions are where x64 and ARM64 disagree; the VM pins the x86 answer.
        Assert.Equal(int.MinValue, QcVm.FloatToInt(float.NaN));
        Assert.Equal(int.MinValue, QcVm.FloatToInt(3e9f));
        Assert.Equal(int.MinValue, QcVm.FloatToInt(float.NegativeInfinity));
        Assert.Equal(-7, QcVm.FloatToInt(-7.9f));
    }

    [Fact]
    public void Truthiness_TestsTheCellBits()
    {
        // IF on an integer-valued cell (an entity number, a string handle): any non-zero bits are true,
        // which a float comparison would get wrong for a denormal like 1.4E-45 (the cell value 1).
        float taken = Run((p, r) =>
        {
            int entityOne = p.Int(1, null, QcType.Entity);
            int jump = p.Emit(QcOp.If, entityOne);
            p.Emit(QcOp.Return, p.Float(0));
            p.PatchJump(jump, p.NextStatement);
            p.Emit(QcOp.StoreF, p.Float(1), r);
        });
        Assert.Equal(1f, taken);

        // NOT_F of negative zero is true.
        Assert.Equal(1f, Run((p, r) => p.Emit(QcOp.NotF, p.Float(-0f), 0, r)));
    }

    [Fact]
    public void VectorOpcodes()
    {
        ProgsBuilder b = new();
        int a = b.Vector(1, 2, 3), c = b.Vector(4, 5, 6), sum = b.Vector(0, 0, 0, "sum"), scaled = b.Vector(0, 0, 0, "scaled");
        int dot = b.Float(0, "dot"), eq = b.Float(0, "eq"), notZero = b.Float(9, "notzero");
        b.Function("main");
        b.Emit(QcOp.AddV, a, c, sum);
        b.Emit(QcOp.MulV, a, c, dot);
        b.Emit(QcOp.MulFV, b.Float(2), a, scaled);
        b.Emit(QcOp.EqV, a, a, eq);
        b.Emit(QcOp.NotV, a, 0, notZero);
        b.Emit(QcOp.Return, sum);
        QcVm vm = b.BuildVm();
        vm.Execute(vm.FindFunction("main"));

        Assert.Equal(new QcVector(5, 7, 9), vm.ResultVector);  // RETURN copies all three cells
        Assert.Equal(32f, vm.GlobalFloat(vm.FindGlobal("dot")!.Offset));
        Assert.Equal(new QcVector(2, 4, 6), vm.GlobalVector(vm.FindGlobal("scaled")!.Offset));
        Assert.Equal(1f, vm.GlobalFloat(vm.FindGlobal("eq")!.Offset));
        Assert.Equal(0f, vm.GlobalFloat(vm.FindGlobal("notzero")!.Offset));
    }

    [Fact]
    public void VectorOpcode_WithOverlappingOperands_ReadsBeforeWriting()
    {
        // a = a + b where the destination is a source: all three components must come from the old value.
        ProgsBuilder b = new();
        int a = b.Vector(1, 2, 3, "a"), c = b.Vector(10, 20, 30);
        b.Function("main");
        b.Emit(QcOp.AddV, a, c, a);
        b.Emit(QcOp.Return, a);
        QcVm vm = b.BuildVm();
        vm.Execute(vm.FindFunction("main"));
        Assert.Equal(new QcVector(11, 22, 33), vm.ResultVector);
    }

    [Fact]
    public void Loop_SumsWithBackwardJump()
    {
        // for (i = 0; i < 10; i++) total += i;
        float total = Run((p, r) =>
        {
            int i = p.Float(0), one = p.Float(1), ten = p.Float(10), cond = p.Float(0);
            int top = p.Emit(QcOp.LtF, i, ten, cond);
            int exit = p.Emit(QcOp.IfNot, cond);
            p.Emit(QcOp.AddF, r, i, r);
            p.Emit(QcOp.AddF, i, one, i);
            p.PatchJump(p.Emit(QcOp.Goto), top);
            p.PatchJump(exit, p.NextStatement);
        });
        Assert.Equal(45f, total);
    }

    [Fact]
    public void RunawayLoop_IsStopped_AndTheVmIsUsableAfterwards()
    {
        ProgsBuilder b = new();
        b.Function("spin");
        b.PatchJump(b.Emit(QcOp.Goto), b.NextStatement - 1);
        b.Function("ok");
        b.Emit(QcOp.Return, b.Float(3));
        QcVm vm = b.BuildVm();

        QcRuntimeException e = Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("spin")));
        Assert.Contains("runaway loop", e.Message);
        Assert.Contains("spin", e.Message); // the QuakeC stack is attached

        vm.Execute(vm.FindFunction("ok"));
        Assert.Equal(3f, vm.ResultFloat);
    }

    [Fact]
    public void Call_CopiesParameters_AndRestoresTheCallersLocals()
    {
        // gmqcc's -Ooverlap-locals: caller and callee keep their locals in the SAME global cells. The
        // callee overwrites them; the VM must put the caller's values back on return.
        ProgsBuilder b = new();
        int shared = b.Float(0);       // caller's local AND callee's parameter
        int shared2 = b.Float(0);      // caller's local AND callee's local
        int hundred = b.Float(100), five = b.Float(5), seven = b.Float(7), out1 = b.Float(0, "out1"), out2 = b.Float(0, "out2");

        int callee = b.Function("callee", parmStart: shared, locals: 2, parmSizes: 1);
        b.Emit(QcOp.AddF, shared, hundred, shared2);   // local = parm + 100
        b.Emit(QcOp.Return, shared2);

        b.Function("caller", parmStart: shared, locals: 2);
        b.Emit(QcOp.StoreF, five, shared);
        b.Emit(QcOp.StoreF, seven, shared2);
        b.Emit(QcOp.StoreF, b.Float(1), Parm0);
        b.Emit(QcOp.Call1, callee);
        b.Emit(QcOp.StoreF, shared, out1);
        b.Emit(QcOp.StoreF, shared2, out2);
        b.Emit(QcOp.Return, Ret);

        QcVm vm = b.BuildVm();
        vm.Execute(vm.FindFunction("caller"));
        Assert.Equal(101f, vm.ResultFloat);
        Assert.Equal(5f, vm.GlobalFloat(vm.FindGlobal("out1")!.Offset));
        Assert.Equal(7f, vm.GlobalFloat(vm.FindGlobal("out2")!.Offset));
    }

    [Fact]
    public void Recursion_Factorial()
    {
        // float fact(float n) { if (n <= 1) return 1; return n * fact(n - 1); }
        ProgsBuilder b = new();
        int n = b.Float(0), tmp = b.Float(0), one = b.Float(1);
        int self = b.Int(0, null, QcType.Function);
        int fact = b.Function("fact", parmStart: n, locals: 2, parmSizes: 1);
        b.Emit(QcOp.LeF, n, one, tmp);
        int skip = b.Emit(QcOp.IfNot, tmp);
        b.Emit(QcOp.Return, one);
        b.PatchJump(skip, b.NextStatement);
        b.Emit(QcOp.SubF, n, one, Parm0);
        b.Emit(QcOp.Call1, fact);
        b.Emit(QcOp.MulF, n, Ret, tmp);
        b.Emit(QcOp.Return, tmp);

        QcVm vm = b.BuildVm();
        vm.SetArgFloat(0, 10);
        vm.Execute(vm.FindFunction("fact"), 1);
        Assert.Equal(3628800f, vm.ResultFloat);
    }

    [Fact]
    public void UnboundedRecursion_OverflowsTheVmStack_NotTheHosts()
    {
        ProgsBuilder b = new();
        int f = b.Function("forever");
        b.Emit(QcOp.Call0, f);
        b.Emit(QcOp.Return);
        QcVm vm = b.BuildVm();
        Assert.Contains("stack overflow", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("forever"))).Message);
        // The stack unwound: a second attempt fails the same way instead of failing immediately.
        Assert.Contains("stack overflow", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("forever"))).Message);
    }

    [Fact]
    public void EntityFields_LoadAddressStore()
    {
        ProgsBuilder b = new();
        int health = b.Field("health"), origin = b.Field("origin", QcType.Vector);
        int ent = b.Int(0, "ent", QcType.Entity), ptr = b.Int(0, null, QcType.Pointer), value = b.Float(75), vec = b.Vector(1, 2, 3), loaded = b.Vector(0, 0, 0);
        b.Function("main");
        b.Emit(QcOp.Address, ent, health, ptr);      // ent.health = 75
        b.Emit(QcOp.StorepF, value, ptr);
        b.Emit(QcOp.Address, ent, origin, ptr);      // ent.origin = '1 2 3'
        b.Emit(QcOp.StorepV, vec, ptr);
        b.Emit(QcOp.LoadV, ent, origin, loaded);
        b.Emit(QcOp.Return, loaded);
        QcVm vm = b.BuildVm();

        int edict = vm.AllocEdict();
        vm.GlobalInt(vm.FindGlobal("ent")!.Offset) = edict;
        vm.Execute(vm.FindFunction("main"));

        Assert.Equal(new QcVector(1, 2, 3), vm.ResultVector);
        Assert.Equal(75f, vm.FieldFloat(edict, vm.FindField("health")!.Offset));
        Assert.Equal(0f, vm.FieldFloat(0, vm.FindField("health")!.Offset)); // the world was not touched
    }

    [Fact]
    public void EntityAccess_OutOfRange_IsAProgramFault()
    {
        ProgsBuilder b = new();
        int health = b.Field("health");
        int bogusEntity = b.Int(1_000_000, null, QcType.Entity), bogusField = b.Int(5000, null, QcType.Field), bogusPointer = b.Int(int.MaxValue - 1), tmp = b.Float(0);
        b.Function("load"); b.Emit(QcOp.LoadF, bogusEntity, health, tmp); b.Emit(QcOp.Return);
        b.Function("field"); b.Emit(QcOp.LoadF, b.Int(0, null, QcType.Entity), bogusField, tmp); b.Emit(QcOp.Return);
        b.Function("store"); b.Emit(QcOp.StorepV, tmp, bogusPointer); b.Emit(QcOp.Return);
        QcVm vm = b.BuildVm();

        Assert.Contains("entity 1000000 is out of range", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("load"))).Message);
        Assert.Contains("field offset 5000", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("field"))).Message);
        Assert.Contains("out of bounds address", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("store"))).Message);
    }

    [Fact]
    public void Edicts_GrowPastTheInitialAllocation_AndFreedSlotsAreReused()
    {
        ProgsBuilder b = new();
        b.Field("health");
        b.Function("main"); b.Emit(QcOp.Return);
        QcVm vm = new(ProgsFile.Load(b.Build()), "test", initialEdicts: 4) { EdictLimit = 600 };

        int[] made = Enumerable.Range(0, 300).Select(_ => vm.AllocEdict()).ToArray();
        Assert.Equal(Enumerable.Range(1, 300), made);
        vm.FieldFloat(300, 0) = 5;

        vm.FreeEdict(42, freeTime: 10);
        Assert.True(vm.IsFree(42));
        Assert.Equal(301, vm.AllocEdict(canReuse: (_, freed) => freed < 5)); // too recently freed to recycle
        Assert.Equal(42, vm.AllocEdict());

        for (int i = vm.NumEdicts; i < 600; i++) vm.AllocEdict();
        Assert.Contains("no free entities", Assert.Throws<QcRuntimeException>(() => vm.AllocEdict()).Message);
    }

    [Fact]
    public void Strings_StaticTempZonedAndEngine()
    {
        ProgsBuilder b = new();
        int hello = b.Int(b.String("hello"), "hello", QcType.String), empty = b.Int(b.String(""), null, QcType.String), other = b.Int(0, "other", QcType.String);
        int eq = b.Float(0, "eq"), notEmpty = b.Float(0, "notempty"), notNull = b.Float(0, "notnull");
        b.Function("main");
        b.Emit(QcOp.EqS, hello, other, eq);
        b.Emit(QcOp.NotS, empty, 0, notEmpty);   // a non-null but empty string is false
        b.Emit(QcOp.NotS, hello, 0, notNull);
        b.Emit(QcOp.Return, hello);
        QcVm vm = b.BuildVm();

        // Different handles, same text: EQ_S compares contents.
        int zoned = vm.AllocString("hello");
        vm.GlobalInt(vm.FindGlobal("other")!.Offset) = zoned;
        vm.Execute(vm.FindFunction("main"));
        Assert.Equal("hello", vm.ResultString);
        Assert.Equal(1f, vm.GlobalFloat(vm.FindGlobal("eq")!.Offset));
        Assert.Equal(1f, vm.GlobalFloat(vm.FindGlobal("notempty")!.Offset));
        Assert.Equal(0f, vm.GlobalFloat(vm.FindGlobal("notnull")!.Offset));

        Assert.Equal(1, vm.ZonedStringCount);
        vm.FreeString(zoned);
        vm.FreeString(zoned); // double free is ignored
        Assert.Equal(0, vm.ZonedStringCount);
        Assert.Equal("", vm.GetString(zoned));

        Assert.Equal(vm.EngineString("maps/stormkeep.bsp"), vm.EngineString("maps/stormkeep.bsp"));
        Assert.Equal("", vm.GetString(-5));
        Assert.Equal("", vm.GetString(0x7FFFFFFF));
    }

    [Fact]
    public void TempStrings_DieWithTheOutermostCall_ButTheResultStaysReadable()
    {
        ProgsBuilder b = new();
        int make = b.Builtin("make", 1);
        b.Function("main");
        b.Emit(QcOp.Call0, make);
        b.Emit(QcOp.Return, Ret);
        QcVm vm = b.BuildVm();
        int made = 0;
        vm.RegisterBuiltin(1, v => v.ReturnString($"temp{++made}"));

        vm.Execute(vm.FindFunction("main"));
        int first = vm.ResultInt;
        Assert.Equal("temp1", vm.ResultString);

        // The next call reuses the slot: the old handle now names the new text, as in DarkPlaces.
        vm.Execute(vm.FindFunction("main"));
        Assert.Equal(first, vm.ResultInt);
        Assert.Equal("temp2", vm.GetString(first));
    }

    [Fact]
    public void TempStrings_MadeForAnArgumentBeforeTheCall_AreReleasedByTheCaller()
    {
        // The engine passes text to an entry point as a temp string it makes BEFORE Execute, so
        // Execute's own bookkeeping starts counting after it. Without the mark/release pair every
        // such call (one per stuffed command, per printed line) would keep its string for good.
        ProgsBuilder b = new();
        int seen = b.Int(0, "seen", QcType.String);
        b.Function("takes");
        b.Emit(QcOp.StoreS, ProgsFile.OfsParm0, seen);
        b.Emit(QcOp.Done);
        QcVm vm = b.BuildVm();
        int function = vm.FindFunction("takes");

        Assert.Equal(0, vm.TempStringMark);
        for (int i = 0; i < 100; i++)
        {
            int mark = vm.TempStringMark;
            vm.SetArgInt(0, vm.TempString("argument " + i));
            vm.Execute(function, 1);
            Assert.Equal(mark + 1, vm.TempStringMark); // Execute alone leaves the argument behind
            vm.ReleaseTempStrings(mark);
            Assert.Equal(mark, vm.TempStringMark);
        }
        Assert.Equal(0, vm.TempStringMark);
        Assert.False(vm.IsExecuting);

        // A stale or wild mark is ignored, and nothing is released from inside a running call.
        vm.ReleaseTempStrings(-1);
        vm.ReleaseTempStrings(50);
        int inner = -1;
        ProgsBuilder nested = new();
        int probe = nested.Builtin("probe", 1);
        nested.Function("main");
        nested.Emit(QcOp.Call0, probe);
        nested.Emit(QcOp.Done);
        QcVm running = nested.BuildVm();
        running.RegisterBuiltin(1, v =>
        {
            v.TempString("held");
            Assert.True(v.IsExecuting);
            v.ReleaseTempStrings(0);
            inner = v.TempStringMark;
        });
        running.Execute(running.FindFunction("main"));
        Assert.Equal(1, inner);
    }

    [Fact]
    public void TempStrings_SurviveANestedCall_AndAnEmptyOneIsNotTheNullString()
    {
        // outer() calls the builtin `reenter`, which runs inner(); inner() returns a temp string. Back in
        // outer, a second temp string is made. The first must still read correctly: the nested call's
        // return must not have released its slot.
        ProgsBuilder b = new();
        int make = b.Builtin("make", 1), reenter = b.Builtin("reenter", 2), empty = b.Builtin("empty", 3);
        int first = b.Int(0, "first", QcType.String), isNull = b.Float(9, "isnull");
        b.Function("inner");
        b.Emit(QcOp.Call0, make);
        b.Emit(QcOp.Return, Ret);
        b.Function("outer");
        b.Emit(QcOp.Call0, reenter);
        b.Emit(QcOp.StoreS, Ret, first);
        b.Emit(QcOp.Call0, make);
        b.Emit(QcOp.Call0, empty);
        b.Emit(QcOp.NotFnc, Ret, 0, isNull);   // tests the handle itself, as `if (s)` on a function-typed cell would
        b.Emit(QcOp.Return, first);
        QcVm vm = b.BuildVm();
        int made = 0;
        vm.RegisterBuiltin(1, v => v.ReturnString($"temp{++made}"));
        vm.RegisterBuiltin(2, v => v.Execute(v.FindFunction("inner")));
        vm.RegisterBuiltin(3, v => v.ReturnString(""));

        vm.Execute(vm.FindFunction("outer"));
        Assert.Equal("temp1", vm.ResultString);
        Assert.Equal(0f, vm.GlobalFloat(vm.FindGlobal("isnull")!.Offset)); // "" came back as a non-null handle
    }

    [Fact]
    public void Builtins_ReceiveArguments_AndMayReenterTheVm()
    {
        ProgsBuilder b = new();
        int add = b.Builtin("add", 5), callback = b.Builtin("callback", 6);
        int parm = b.Float(0);
        b.Function("double", parmStart: parm, locals: 1, parmSizes: 1);
        b.Emit(QcOp.AddF, parm, parm, Ret);
        b.Emit(QcOp.Return, Ret);
        b.Function("main");
        b.Emit(QcOp.StoreF, b.Float(3), Parm0);
        b.Emit(QcOp.StoreF, b.Float(4), Parm1);
        b.Emit(QcOp.Call2, add);
        b.Emit(QcOp.StoreF, Ret, Parm0);
        b.Emit(QcOp.Call1, callback);
        b.Emit(QcOp.Return, Ret);
        QcVm vm = b.BuildVm();

        vm.RegisterBuiltin(5, v => { Assert.Equal(2, v.ArgCount); v.ReturnFloat(v.ArgFloat(0) + v.ArgFloat(1)); });
        vm.RegisterBuiltin(6, v => v.Execute(v.FindFunction("double"), 1)); // argument 0 is already in place
        vm.Execute(vm.FindFunction("main"));
        Assert.Equal(14f, vm.ResultFloat);
    }

    [Fact]
    public void MissingBuiltin_IsAFault_UnlessTheHostOptsIntoStubbing()
    {
        ProgsBuilder b = new();
        int missing = b.Builtin("drawpic", 322);
        b.Function("main");
        b.Emit(QcOp.Call0, missing);
        b.Emit(QcOp.Return, Ret);
        QcVm vm = b.BuildVm();

        Assert.Contains("no such builtin #322 (drawpic)", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("main"))).Message);

        string? seen = null;
        vm.UnknownBuiltin = (_, number, name) => seen = $"{number}:{name}";
        vm.Execute(vm.FindFunction("main"));
        Assert.Equal("322:drawpic", seen);
        Assert.Equal(0f, vm.ResultFloat);
    }

    [Fact]
    public void BuiltinException_BecomesAProgramFaultWithTheQuakeCStack()
    {
        ProgsBuilder b = new();
        int bad = b.Builtin("bad", 9);
        b.Function("main");
        b.Emit(QcOp.Call0, bad);
        b.Emit(QcOp.Return);
        QcVm vm = b.BuildVm();
        vm.RegisterBuiltin(9, _ => throw new InvalidOperationException("host bug"));

        QcRuntimeException e = Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("main")));
        Assert.Contains("host bug", e.Message);
        Assert.Contains("main", e.Message);
        Assert.IsType<InvalidOperationException>(e.InnerException);
    }

    [Fact]
    public void NullAndWildFunctionCalls_AreFaults()
    {
        ProgsBuilder b = new();
        int nullFn = b.Int(0, null, QcType.Function), wild = b.Int(99999, null, QcType.Function);
        b.Function("callnull"); b.Emit(QcOp.Call0, nullFn); b.Emit(QcOp.Return);
        b.Function("callwild"); b.Emit(QcOp.Call0, wild); b.Emit(QcOp.Return);
        QcVm vm = b.BuildVm();
        Assert.Contains("NULL function", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("callnull"))).Message);
        Assert.Contains("CALL outside the program", Assert.Throws<QcRuntimeException>(() => vm.Execute(vm.FindFunction("callwild"))).Message);
        Assert.Throws<QcRuntimeException>(() => vm.Execute(0));
        Assert.False(vm.TryExecute("no_such_function"));
    }

    [Fact]
    public void State_SetsFrameThinkAndNextThinkOnSelf()
    {
        ProgsBuilder b = new();
        int self = b.Int(0, "self", QcType.Entity), time = b.Float(2.5f, "time");
        b.Field("nextthink"); b.Field("frame"); b.Field("think", QcType.Function);
        int think = b.Function("think_fn");
        b.Emit(QcOp.Return);
        b.Function("main");
        b.Emit(QcOp.State, b.Float(12), think);
        b.Emit(QcOp.Return);
        QcVm vm = b.BuildVm();
        int edict = vm.AllocEdict();
        vm.GlobalInt(self) = edict;
        vm.Execute(vm.FindFunction("main"));

        Assert.Equal(2.6f, vm.FieldFloat(edict, vm.FindField("nextthink")!.Offset));
        Assert.Equal(12f, vm.FieldFloat(edict, vm.FindField("frame")!.Offset));
        Assert.Equal(vm.FindFunction("think_fn"), vm.FieldInt(edict, vm.FindField("think")!.Offset));
    }

    // ---- the loader refuses what it cannot prove safe ----------------------------------------------

    [Fact]
    public void Load_RejectsMalformedFiles()
    {
        Assert.Contains("shorter than", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(new byte[10])).Message);
        Assert.Contains("version 7", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(Minimal().Build(version: 7))).Message);

        byte[] truncated = Minimal().Build();
        Assert.Contains("outside the file", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(truncated.AsSpan(0, truncated.Length - 8))).Message);

        ProgsBuilder wildGlobal = Minimal();
        wildGlobal.Function("f"); wildGlobal.Emit(QcOp.AddF, 60000, 0, 0); wildGlobal.Emit(QcOp.Return);
        Assert.Contains("refers to global 60000", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(wildGlobal.Build())).Message);

        ProgsBuilder wildJump = Minimal();
        wildJump.Function("f"); wildJump.Emit(QcOp.Goto, 30000); wildJump.Emit(QcOp.Return);
        Assert.Contains("outside the program", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(wildJump.Build())).Message);

        ProgsBuilder extended = Minimal();
        extended.Function("f"); extended.EmitRaw(113); extended.Emit(QcOp.Return); // OP_STORE_I
        Assert.Contains("opcode 113", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(extended.Build())).Message);

        ProgsBuilder fallsOff = Minimal();
        fallsOff.Function("f"); fallsOff.Emit(QcOp.AddF);
        Assert.Contains("last statement", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(fallsOff.Build())).Message);

        ProgsBuilder wildLocals = Minimal();
        wildLocals.Function("f", parmStart: 20, locals: 5000); wildLocals.Emit(QcOp.Return);
        Assert.Contains("locals outside the globals", Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(wildLocals.Build())).Message);
    }

    [Fact]
    public void Load_NeverThrowsAnythingButAFormatError_OnCorruptedInput()
    {
        // Deterministic corruption of a valid file: flip bytes, then load. Whatever happens must be a
        // ProgsFormatException or a successful load - never an index fault from inside the loader.
        ProgsBuilder b = Minimal();
        b.Field("health");
        b.Function("f"); b.Emit(QcOp.AddF, 28, 28, 28); b.Emit(QcOp.Return);
        byte[] valid = b.Build();
        Random random = new(4042);
        for (int i = 0; i < 5000; i++)
        {
            byte[] corrupt = (byte[])valid.Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips; f++) corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
            try { ProgsFile.Load(corrupt); }
            catch (ProgsFormatException) { }
        }
    }

    private static ProgsBuilder Minimal()
    {
        ProgsBuilder b = new();
        b.Float(0); // one global past the reserved block, so cell 28 exists
        return b;
    }

    // ---- the real thing ----------------------------------------------------------------------------

    private static string CsprogsPath => Path.Combine(TestPaths.BaseCorePk3Dir, "csprogs.dat");

    [Fact]
    public void LoadsTheStockXonoticClientProgram()
    {
        if (!File.Exists(CsprogsPath)) return; // needs the upstream reference checkout (../Base)
        ProgsFile progs = ProgsFile.Load(File.ReadAllBytes(CsprogsPath));

        Assert.True(progs.Statements.Length > 300_000);
        Assert.True(progs.NumGlobals > 32768, "the client program is past the reach of signed 16-bit operands");
        Assert.True(progs.Functions.Length > 9000);

        QcVm vm = new(progs, "csprogs");
        // The entry points the engine calls, and the system globals and fields it sets, by name.
        foreach (string function in new[] { "CSQC_Init", "CSQC_Shutdown", "CSQC_UpdateView", "CSQC_InputEvent", "CSQC_ConsoleCommand",
                     "CSQC_Parse_StuffCmd", "CSQC_Parse_Print", "CSQC_Parse_CenterPrint", "CSQC_Parse_TempEntity", "CSQC_Ent_Update", "CSQC_Ent_Remove" })
            Assert.True(vm.FindFunction(function) > 0, $"missing entry point {function}");
        foreach (string global in new[] { "self", "time", "frametime", "mapname", "v_forward", "trace_fraction", "player_localentnum", "clientcommandframe" })
            Assert.NotNull(vm.FindGlobal(global));
        Assert.Equal(ProgsFile.ReservedOfs, vm.FindGlobal("self")!.Offset);
        Assert.Equal(0, vm.FindField("modelindex")!.Offset);
        Assert.True(vm.GlobalDefs.Count(d => d.Name.StartsWith("autocvar_", StringComparison.Ordinal)) > 1000);
    }
}
