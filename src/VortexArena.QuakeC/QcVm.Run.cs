// Port of Base/darkplaces/prvm_execprogram.h (the interpreter loop) and prvm_exec.c PRVM_EnterFunction /
// PRVM_LeaveFunction. What the loop executes is not the file's statement array but a copy made for it
// at load (BuildExecStream) at half the size. That is about the processor, not the program: every
// instruction does exactly what it does in DarkPlaces, in the same order.
//
// Two things were tried here on a recorded frame of Xonotic's client program (tools/legacy-server
// "perf") and taken out again, so that nobody repeats them without new evidence:
//  - Giving the commonest pairs of consecutive instructions one opcode each ("superinstructions")
//    removed 34% of all dispatches (61,952 to 41,092 a frame) and changed the frame's time by nothing
//    (0.892 against 0.890 ms).
//  - Translating every function to a .NET method that hands calls and returns back to this loop made
//    a hot loop three times faster (0.59 against 1.7 ns an instruction) and the real frame no faster
//    (0.911 against 0.875 ms): the program calls something every 14 instructions, and a call costs the
//    same either way.
// The loop itself is about a fifth of the program's frame; the builtins and cold memory are the rest.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VortexArena.QuakeC;

public sealed partial class QcVm
{
    /// <summary>
    /// One instruction as the loop reads it: the file's own eight bytes. Operands are unsigned global
    /// indices; the jump offsets of IF, IFNOT and GOTO are the same sixteen bits read as signed.
    /// (<see cref="QcStatement"/>, the public form, is twice the size. A frame of Xonotic's client
    /// program runs some 18,000 distinct instructions - 141 KB of them at eight bytes each, measured -
    /// beside 34 KB of globals and 154 KB of entity fields; the smaller the share of the processor's
    /// caches the instructions take, the more of the rest stays in them. The effect of this change
    /// alone was not measured separately.)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Exec
    {
        public ushort Op, A, B, C;
    }

    // What EnterFunction and LeaveFunction need of a function, side by side in one array instead of
    // behind an object and a byte array each.
    private struct FunctionInfo
    {
        public int FirstStatement, ParmStart, Locals, NumParms;
        /// <summary>The size in cells of each parameter, two bits each, the first in the lowest bits.</summary>
        public uint ParmSizes;
    }

    private Exec[] _exec = Array.Empty<Exec>();
    private FunctionInfo[] _functionInfo = Array.Empty<FunctionInfo>();

#if QC_OPSTATS
    // Measuring builds: which 64-byte lines of the instruction stream, the globals and the entity
    // fields were touched since the last TakeTouched - the memory one frame needs warm.
    private ulong[] _touchedCode = new ulong[1], _touchedGlobals = new ulong[1], _touchedFields = new ulong[1];

    /// <summary>Measuring builds: time each instruction (the clock reads cost more than most instructions,
    /// so only the differences between opcodes mean anything).</summary>
    public bool OpTiming;
    public readonly long[] OpTicks = new long[128];
    private int _timedOp;
    private long _timedAt;

    private static void Touch(ulong[] set, int line) => set[line >> 6] |= 1UL << line;

    internal void TouchField(int cell)
    {
        int line = cell >> 4;
        if ((line >> 6) >= _touchedFields.Length) Array.Resize(ref _touchedFields, (line >> 6) + 4096);
        Touch(_touchedFields, line);
    }

    /// <summary>Distinct 64-byte lines touched since the last call: instruction stream, globals, entity fields.</summary>
    public (int Code, int Globals, int Fields) TakeTouched()
    {
        static int Count(ulong[] set) { int n = 0; for (int i = 0; i < set.Length; i++) { n += System.Numerics.BitOperations.PopCount(set[i]); set[i] = 0; } return n; }
        return (Count(_touchedCode), Count(_touchedGlobals), Count(_touchedFields));
    }
#endif

    private void BuildExecStream()
    {
        QcStatement[] source = _statements;
        Exec[] exec = new Exec[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            QcStatement s = source[i];
            // ProgsFile.Load has proven every operand in 0..65535 (or, for the jump offsets, in
            // -32768..32767), so nothing is lost in sixteen bits.
            exec[i] = new Exec { Op = (ushort)s.Op, A = (ushort)s.A, B = (ushort)s.B, C = (ushort)s.C };
        }
        _exec = exec;
#if QC_OPSTATS
        _touchedCode = new ulong[(exec.Length >> 9) + 2];
        _touchedGlobals = new ulong[(_globals.Length >> 10) + 70];
#endif
    }

    private void BuildFunctionInfo()
    {
        FunctionInfo[] info = new FunctionInfo[_functions.Length];
        for (int i = 0; i < info.Length; i++)
        {
            QcFunction f = _functions[i];
            uint sizes = 0;
            // ProgsFile.Load refuses a parameter of more than three cells in anything but a builtin
            // declaration, which is never entered.
            for (int p = 0; p < f.NumParms && p < ProgsFile.MaxParms && p < f.ParmSize.Length; p++) sizes |= (uint)(f.ParmSize[p] & 3) << (p * 2);
            info[i] = new FunctionInfo { FirstStatement = f.FirstStatement, ParmStart = f.ParmStart, Locals = f.Locals, NumParms = f.NumParms, ParmSizes = sizes };
        }
        _functionInfo = info;
    }

    private int EnterFunction(int function)
    {
        ref FunctionInfo f = ref _functionInfo[function];
        int locals = f.Locals;
        // Both checks come before any state changes, so a fault here leaves nothing half-entered to unwind.
        if (_depth >= MaxStackDepth - 1) throw new QcRuntimeException($"{Name}: stack overflow");
        if (_localStackUsed + locals > LocalStackSize) throw new QcRuntimeException($"{Name}: locals stack overflow");
        _stackStatement[_depth] = _currentStatement;
        _stackFunction[_depth] = _currentFunction;
        _depth++;
#if QC_OPSTATS
        EnterCalls++;
        LocalsCopied += locals;
#endif

        // Functions share global cells for their locals (gmqcc -Ooverlap-locals), so whatever is in this
        // function's range belongs to a caller: save it, and put it back on return.
        new ReadOnlySpan<int>(_globals, f.ParmStart, locals).CopyTo(new Span<int>(_localStack, _localStackUsed, locals));
        _localStackUsed += locals;

        // Unchecked: the loader proved that ParmStart plus the parameters' cells lies inside the globals.
        ref int g = ref MemoryMarshal.GetArrayDataReference(_globals);
        int o = f.ParmStart;
        uint sizes = f.ParmSizes;
        for (int i = 0, parm = ProgsFile.OfsParm0; i < f.NumParms; i++, parm += 3, sizes >>= 2)
        {
            uint size = sizes & 3;
            if (size == 0) continue;
            Unsafe.Add(ref g, o) = Unsafe.Add(ref g, parm);
            if (size > 1)
            {
                Unsafe.Add(ref g, o + 1) = Unsafe.Add(ref g, parm + 1);
                if (size > 2) Unsafe.Add(ref g, o + 2) = Unsafe.Add(ref g, parm + 2);
            }
            o += (int)size;
        }

        if (_functionsEntered is { } entered) entered[function >> 6] |= 1UL << function;
        _currentFunction = function;
        return f.FirstStatement;
    }

    /// <summary>Pops a frame and returns the statement index of the call that created it.</summary>
    private int LeaveFunction()
    {
        ref FunctionInfo f = ref _functionInfo[_currentFunction];
        int locals = f.Locals;
        _localStackUsed -= locals;
        new ReadOnlySpan<int>(_localStack, _localStackUsed, locals).CopyTo(new Span<int>(_globals, f.ParmStart, locals));
        _depth--;
        _currentFunction = _stackFunction[_depth];
        return _stackStatement[_depth];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Run(int pc, int exitDepth)
    {
        // Unchecked access to the globals is sound because ProgsFile.Load proved every operand of every
        // statement in range, and the array (with its two cells of padding) is never reallocated.
        ref int g = ref MemoryMarshal.GetArrayDataReference(_globals);
        ref Exec code = ref MemoryMarshal.GetArrayDataReference(_exec);

        while (true)
        {
            // By reference: no copy per instruction. Unchecked, because every way pc can change was
            // proven in range by ProgsFile.Load (jump targets, function entry points, and a final
            // statement that cannot fall off the end).
            ref Exec st = ref Unsafe.Add(ref code, pc);
#if QC_OPSTATS
            OpCounts[st.Op]++;
            OpPairCounts[_previousOp * 128 + st.Op]++;
            _previousOp = st.Op;
            FunctionStatements[_currentFunction]++;
            if (OpTiming)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                OpTicks[_timedOp] += now - _timedAt;
                _timedOp = st.Op;
                _timedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            Touch(_touchedCode, pc >> 3);
            Touch(_touchedGlobals, st.A >> 4);
            Touch(_touchedGlobals, st.B >> 4);
            Touch(_touchedGlobals, st.C >> 4);
#endif
            switch ((int)st.Op)
            {
                case (int)QcOp.AddF: F(ref g, st.C) = F(ref g, st.A) + F(ref g, st.B); break;
                case (int)QcOp.SubF: F(ref g, st.C) = F(ref g, st.A) - F(ref g, st.B); break;
                case (int)QcOp.MulF: F(ref g, st.C) = F(ref g, st.A) * F(ref g, st.B); break;
                // Division by zero is the IEEE result (prvm_gameplayfix_div0is0 defaults to off).
                case (int)QcOp.DivF: F(ref g, st.C) = F(ref g, st.A) / F(ref g, st.B); break;

                case (int)QcOp.AddV:
                {
                    float x = F(ref g, st.A) + F(ref g, st.B), y = F(ref g, st.A + 1) + F(ref g, st.B + 1), z = F(ref g, st.A + 2) + F(ref g, st.B + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }
                case (int)QcOp.SubV:
                {
                    float x = F(ref g, st.A) - F(ref g, st.B), y = F(ref g, st.A + 1) - F(ref g, st.B + 1), z = F(ref g, st.A + 2) - F(ref g, st.B + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }
                case (int)QcOp.MulV:
                    F(ref g, st.C) = F(ref g, st.A) * F(ref g, st.B) + F(ref g, st.A + 1) * F(ref g, st.B + 1) + F(ref g, st.A + 2) * F(ref g, st.B + 2);
                    break;
                case (int)QcOp.MulFV:
                {
                    float s = F(ref g, st.A);
                    float x = s * F(ref g, st.B), y = s * F(ref g, st.B + 1), z = s * F(ref g, st.B + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }
                case (int)QcOp.MulVF:
                {
                    float s = F(ref g, st.B);
                    float x = s * F(ref g, st.A), y = s * F(ref g, st.A + 1), z = s * F(ref g, st.A + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }

                case (int)QcOp.BitAndF: F(ref g, st.C) = FloatToInt(F(ref g, st.A)) & FloatToInt(F(ref g, st.B)); break;
                case (int)QcOp.BitOrF: F(ref g, st.C) = FloatToInt(F(ref g, st.A)) | FloatToInt(F(ref g, st.B)); break;

                case (int)QcOp.GeF: F(ref g, st.C) = F(ref g, st.A) >= F(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.LeF: F(ref g, st.C) = F(ref g, st.A) <= F(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.GtF: F(ref g, st.C) = F(ref g, st.A) > F(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.LtF: F(ref g, st.C) = F(ref g, st.A) < F(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.AndF: F(ref g, st.C) = IsTrue(I(ref g, st.A)) && IsTrue(I(ref g, st.B)) ? 1f : 0f; break;
                case (int)QcOp.OrF: F(ref g, st.C) = IsTrue(I(ref g, st.A)) || IsTrue(I(ref g, st.B)) ? 1f : 0f; break;

                case (int)QcOp.NotF: F(ref g, st.C) = IsTrue(I(ref g, st.A)) ? 0f : 1f; break;
                case (int)QcOp.NotV: F(ref g, st.C) = F(ref g, st.A) == 0f && F(ref g, st.A + 1) == 0f && F(ref g, st.A + 2) == 0f ? 1f : 0f; break;
                case (int)QcOp.NotS: F(ref g, st.C) = I(ref g, st.A) == 0 || GetString(I(ref g, st.A)).Length == 0 ? 1f : 0f; break;
                case (int)QcOp.NotFnc:
                case (int)QcOp.NotEnt: F(ref g, st.C) = I(ref g, st.A) == 0 ? 1f : 0f; break;

                case (int)QcOp.EqF: F(ref g, st.C) = F(ref g, st.A) == F(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.NeF: F(ref g, st.C) = F(ref g, st.A) != F(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.EqV:
                    F(ref g, st.C) = F(ref g, st.A) == F(ref g, st.B) && F(ref g, st.A + 1) == F(ref g, st.B + 1) && F(ref g, st.A + 2) == F(ref g, st.B + 2) ? 1f : 0f;
                    break;
                case (int)QcOp.NeV:
                    F(ref g, st.C) = F(ref g, st.A) != F(ref g, st.B) || F(ref g, st.A + 1) != F(ref g, st.B + 1) || F(ref g, st.A + 2) != F(ref g, st.B + 2) ? 1f : 0f;
                    break;
                case (int)QcOp.EqS: F(ref g, st.C) = StringsEqual(I(ref g, st.A), I(ref g, st.B)) ? 1f : 0f; break;
                case (int)QcOp.NeS: F(ref g, st.C) = StringsEqual(I(ref g, st.A), I(ref g, st.B)) ? 0f : 1f; break;
                case (int)QcOp.EqE:
                case (int)QcOp.EqFnc: F(ref g, st.C) = I(ref g, st.A) == I(ref g, st.B) ? 1f : 0f; break;
                case (int)QcOp.NeE:
                case (int)QcOp.NeFnc: F(ref g, st.C) = I(ref g, st.A) != I(ref g, st.B) ? 1f : 0f; break;

                case (int)QcOp.StoreF:
                case (int)QcOp.StoreEnt:
                case (int)QcOp.StoreFld:
                case (int)QcOp.StoreFnc:
                case (int)QcOp.StoreS:
                    I(ref g, st.B) = I(ref g, st.A);
                    break;
                case (int)QcOp.StoreV:
                {
                    int x = I(ref g, st.A), y = I(ref g, st.A + 1), z = I(ref g, st.A + 2);
                    I(ref g, st.B) = x; I(ref g, st.B + 1) = y; I(ref g, st.B + 2) = z;
                    break;
                }

                case (int)QcOp.StorepF:
                case (int)QcOp.StorepEnt:
                case (int)QcOp.StorepFld:
                case (int)QcOp.StorepFnc:
                case (int)QcOp.StorepS:
                {
                    _currentStatement = pc;
                    int address = I(ref g, st.B) + I(ref g, st.C), value = I(ref g, st.A);
                    Pointer(address, 1) = value;
                    // Storing zero cannot make a watched cell non-zero, and programs clear fields constantly.
                    if (value != 0 && _watchedCells is { } watched) NotePointerWrite(watched, address - _numGlobals);
                    if (_mirrorOf is { } mirrorOf) NoteMirrorWrite(mirrorOf, address - _numGlobals, value);
                    break;
                }
                case (int)QcOp.StorepV:
                {
                    _currentStatement = pc;
                    int address = I(ref g, st.B) + I(ref g, st.C);
                    ref int target = ref Pointer(address, 3);
                    int x = I(ref g, st.A), y = I(ref g, st.A + 1), z = I(ref g, st.A + 2);
                    target = x; Unsafe.Add(ref target, 1) = y; Unsafe.Add(ref target, 2) = z;
                    if ((x | y | z) != 0 && _watchedCells is { } watched)
                    {
                        // Cell by cell: a vector stored at an entity's last cells runs on into the next entity.
                        int field = address - _numGlobals;
                        if (x != 0) NotePointerWrite(watched, field);
                        if (y != 0) NotePointerWrite(watched, field + 1);
                        if (z != 0) NotePointerWrite(watched, field + 2);
                    }
                    if (_mirrorOf is { } mirrorOf)
                    {
                        int field = address - _numGlobals;
                        NoteMirrorWrite(mirrorOf, field, x);
                        NoteMirrorWrite(mirrorOf, field + 1, y);
                        NoteMirrorWrite(mirrorOf, field + 2, z);
                    }
                    break;
                }

                case (int)QcOp.Address:
                    _currentStatement = pc;
                    I(ref g, st.C) = _numGlobals + FieldIndex(I(ref g, st.A), I(ref g, st.B), 1);
                    break;

                case (int)QcOp.LoadF:
                case (int)QcOp.LoadFld:
                case (int)QcOp.LoadEnt:
                case (int)QcOp.LoadFnc:
                case (int)QcOp.LoadS:
                    _currentStatement = pc;
                    I(ref g, st.C) = _fields[FieldIndex(I(ref g, st.A), I(ref g, st.B), 1)];
                    break;
                case (int)QcOp.LoadV:
                {
                    _currentStatement = pc;
                    int at = FieldIndex(I(ref g, st.A), I(ref g, st.B), 3);
                    int[] fields = _fields;
                    int x = fields[at], y = fields[at + 1], z = fields[at + 2];
                    I(ref g, st.C) = x; I(ref g, st.C + 1) = y; I(ref g, st.C + 2) = z;
                    break;
                }

                case (int)QcOp.IfNot:
                    if (!IsTrue(I(ref g, st.A))) { pc = Jump(pc, (short)st.B); continue; }
                    break;
                case (int)QcOp.If:
                    if (IsTrue(I(ref g, st.A))) { pc = Jump(pc, (short)st.B); continue; }
                    break;
                case (int)QcOp.Goto:
                    pc = Jump(pc, (short)st.A);
                    continue;

                case (int)QcOp.Call0:
                case (int)QcOp.Call1:
                case (int)QcOp.Call2:
                case (int)QcOp.Call3:
                case (int)QcOp.Call4:
                case (int)QcOp.Call5:
                case (int)QcOp.Call6:
                case (int)QcOp.Call7:
                case (int)QcOp.Call8:
                {
                    _currentStatement = pc;
                    int function = I(ref g, st.A);
                    FunctionInfo[] functions = _functionInfo;
                    if ((uint)(function - 1) >= (uint)(functions.Length - 1))
                        throw new QcRuntimeException(function == 0 ? $"{Name}: NULL function" : $"{Name}: attempted CALL outside the program");
                    ArgCount = st.Op - (int)QcOp.Call0;
                    if (functions[function].FirstStatement < 0)
                    {
                        CallBuiltin(function);
                        break;
                    }
                    pc = EnterFunction(function);
                    continue;
                }

                case (int)QcOp.Done:
                case (int)QcOp.Return:
                {
                    int x = I(ref g, st.A), y = I(ref g, st.A + 1), z = I(ref g, st.A + 2);
                    I(ref g, ProgsFile.OfsReturn) = x; I(ref g, ProgsFile.OfsReturn + 1) = y; I(ref g, ProgsFile.OfsReturn + 2) = z;
                    _currentStatement = pc;
                    pc = LeaveFunction();
                    if (_depth <= exitDepth) return;
                    break; // resume after the CALL statement
                }

                case (int)QcOp.State:
                    _currentStatement = pc;
                    State(F(ref g, st.A), I(ref g, st.B));
                    break;

                default:
                    _currentStatement = pc;
                    throw new QcRuntimeException($"{Name}: bad opcode {st.Op}");
            }
            pc++;
        }
    }
}
