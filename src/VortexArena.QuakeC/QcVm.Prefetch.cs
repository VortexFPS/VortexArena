// Nothing here is a port: DarkPlaces has no counterpart. It exists because of how the two engines sit
// in memory. A frame of Xonotic's client program reads about 2,500 different 64-byte lines of entity
// fields (out of 32 MB of them), 2,300 lines of instructions and most of its globals - a third of a
// megabyte, scattered. Between two frames the renderer, the sound mixer and the server thread go
// through far more memory than the processor caches hold, so the next frame finds almost none of it
// cached, and an interpreter takes its cache misses one at a time: measured, a field read cost 40 to
// 70 ns where an instruction that stays in the globals cost 2.
//
// So the VM remembers which lines a frame touched, and the host asks for them all at once before the
// next frame (PrefetchRecorded). The processor fetches a dozen or more lines in parallel, which turns
// thousands of waits in a row into a few dozen. It changes nothing the program can observe: a prefetch
// reads no value and writes none.
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace VortexArena.QuakeC;

public sealed partial class QcVm
{
    // One bit per 64-byte line (16 cells) of the entity fields, set when the line is read or written.
    // Null while recording is off.
    private ulong[]? _fieldLines;
    // One bit per function, set when it is entered.
    private ulong[]? _functionsEntered;
    // Where each function's instructions end (the next function's first instruction), capped.
    private int[]? _functionEnd;

    private const int MaxPrefetchedStatementsPerFunction = 512;

    /// <summary>
    /// Record which entity-field lines are touched and which functions are entered, for
    /// <see cref="PrefetchRecorded"/>. Off by default; costs one bit set per field access and per call.
    /// </summary>
    public bool RecordTouches
    {
        get => _fieldLines is not null;
        set
        {
            if (value == (_fieldLines is not null)) return;
            if (!value)
            {
                _fieldLines = null;
                _functionsEntered = null;
                return;
            }
            _fieldLines = new ulong[FieldLineWords(_maxEdicts)];
            _functionsEntered = new ulong[(_functions.Length + 63) >> 6];
            if (_functionEnd is null)
            {
                // A function's instructions run from its first to the next function's first, in file order.
                int[] starts = new int[_functions.Length];
                int count = 0;
                foreach (QcFunction f in _functions) if (f.FirstStatement > 0) starts[count++] = f.FirstStatement;
                Array.Sort(starts, 0, count);
                int[] end = new int[_functions.Length];
                for (int i = 0; i < _functions.Length; i++)
                {
                    int first = _functions[i].FirstStatement;
                    if (first <= 0) continue;
                    int at = Array.BinarySearch(starts, 0, count, first);
                    while (at + 1 < count && starts[at + 1] == first) at++;
                    int next = at + 1 < count ? starts[at + 1] : _statements.Length;
                    end[i] = Math.Min(next, first + MaxPrefetchedStatementsPerFunction);
                }
                _functionEnd = end;
            }
        }
    }

    private int FieldLineWords(int maxEdicts) => (int)((((long)maxEdicts * _entityFields) >> 10) + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TouchFieldLine(int cell)
    {
        if (_fieldLines is { } lines) lines[cell >> 10] |= 1UL << (cell >> 4);
    }

    /// <summary>
    /// Asks the processor to load everything recorded since the last call - the entity-field lines that
    /// were touched, the instructions of the functions that were entered, and the globals - and starts
    /// recording afresh. Call it just before running a frame that will do much the same as the last.
    /// Does nothing unless <see cref="RecordTouches"/> is on, or on a processor with no prefetch
    /// instruction this runtime can issue.
    /// </summary>
    public unsafe void PrefetchRecorded()
    {
        ulong[]? fieldLines = _fieldLines, entered = _functionsEntered;
        if (fieldLines is null || entered is null) return;
        bool can = Sse.IsSupported;

        fixed (int* fields = _fields)
        {
            int cells = _fields.Length;
            for (int w = 0; w < fieldLines.Length; w++)
            {
                ulong bits = fieldLines[w];
                if (bits == 0) continue;
                fieldLines[w] = 0;
                if (!can) continue;
                while (bits != 0)
                {
                    int cell = ((w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits)) << 4;
                    bits &= bits - 1;
                    if (cell < cells) Sse.Prefetch0(fields + cell);
                }
            }
        }

        int[] functionEnd = _functionEnd!;
        fixed (Exec* code = _exec)
        {
            for (int w = 0; w < entered.Length; w++)
            {
                ulong bits = entered[w];
                if (bits == 0) continue;
                entered[w] = 0;
                if (!can) continue;
                while (bits != 0)
                {
                    int function = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int end = functionEnd[function];
                    // Eight instructions to a line.
                    for (int statement = _functionInfo[function].FirstStatement; statement < end; statement += 8) Sse.Prefetch0(code + statement);
                }
            }
        }

        if (!can) return;
        fixed (int* globals = _globals)
        {
            for (int cell = 0; cell < _globals.Length; cell += 16) Sse.Prefetch0(globals + cell);
        }
    }
}
