// legacy-server micro: the interpreter's floor - a QuakeC loop of five instructions, assembled here,
// run a few million times with everything it touches in the first-level cache and every branch
// predictable. What a recorded frame costs per instruction ("perf") is to be read against this: the gap
// is what cold memory and unpredictable dispatch cost, which no change to an instruction's own code
// removes.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private static int Micro(Options o)
    {
        // Globals: 0..27 reserved; 28 i, 29 one, 30 limit, 31 cond, 32 total.
        const int I = 28, One = 29, Limit = 30, Cond = 31, Total = 32, Globals = 40, Iterations = 2_000_000;
        (QcOp Op, int A, int B, int C)[] code =
        {
            (QcOp.Done, 0, 0, 0),
            (QcOp.LtF, I, Limit, Cond),      // 1: top
            (QcOp.IfNot, Cond, 4, 0),        // 2: -> 6
            (QcOp.AddF, Total, I, Total),    // 3
            (QcOp.AddF, I, One, I),          // 4
            (QcOp.Goto, -4, 0, 0),           // 5: -> 1
            (QcOp.Return, Total, 0, 0),      // 6
        };
        byte[] strings = "\0main\0"u8.ToArray();
        int statementsAt = 60, functionsAt = statementsAt + code.Length * 8, stringsAt = functionsAt + 2 * 36, globalsAt = stringsAt + strings.Length;
        byte[] file = new byte[globalsAt + Globals * 4];
        void W(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at), value);
        W(0, 6);
        W(8, statementsAt); W(12, code.Length);
        W(16, 60); W(20, 0);
        W(24, 60); W(28, 0);
        W(32, functionsAt); W(36, 2);
        W(40, stringsAt); W(44, strings.Length);
        W(48, globalsAt); W(52, Globals);
        W(56, 1);
        for (int i = 0; i < code.Length; i++)
        {
            Span<byte> s = file.AsSpan(statementsAt + i * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)code[i].Op);
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)code[i].A);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)code[i].B);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)code[i].C);
        }
        W(functionsAt + 36, 1);      // function 1 "main": first statement 1
        W(functionsAt + 36 + 4, 34); // parm_start
        W(functionsAt + 36 + 16, 1); // name
        strings.CopyTo(file, stringsAt);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(globalsAt + One * 4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(globalsAt + Limit * 4), Iterations);

        {
            QcVm vm = new(ProgsFile.Load(file), "micro");
            int main = vm.FindFunction("main");
            double best = double.MaxValue;
            for (int round = 0; round < 7; round++)
            {
                vm.GlobalFloat(I) = 0;
                vm.GlobalFloat(Total) = 0;
                long t0 = Stopwatch.GetTimestamp();
                vm.Execute(main);
                best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            long statements = 5L * Iterations + 3;
            Log(string.Create(CultureInfo.InvariantCulture, $"micro loop: {statements} instructions in {best:0.00} ms = {best * 1e6 / statements:0.00} ns per instruction (result {vm.ResultFloat})"));
        }
        return 0;
    }
}
