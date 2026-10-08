// Port of Base/darkplaces/pr_comp.h (file layout) and prvm_edict.c PRVM_Prog_Load (validation).
using System.Buffers.Binary;
using System.Text;

namespace VortexArena.QuakeC;

/// <summary>The classic QuakeC instruction set (pr_comp.h, opcodes 0..65).</summary>
/// <remarks>
/// DarkPlaces also accepts FTE-numbered extended opcodes (113 and up: integer and pointer maths).
/// They are deliberately absent. Xonotic's csprogs.dat, as compiled by gmqcc, contains none - measured
/// over all 330,689 statements of the 0.8.6-line build - and the loader refuses a file that does, by
/// opcode number, rather than guessing at semantics nothing here has been tested against.
/// </remarks>
public enum QcOp : ushort
{
    Done = 0,
    MulF, MulV, MulFV, MulVF, DivF,
    AddF, AddV, SubF, SubV,
    EqF, EqV, EqS, EqE, EqFnc,
    NeF, NeV, NeS, NeE, NeFnc,
    LeF, GeF, LtF, GtF,
    LoadF, LoadV, LoadS, LoadEnt, LoadFld, LoadFnc,
    Address,
    StoreF, StoreV, StoreS, StoreEnt, StoreFld, StoreFnc,
    StorepF, StorepV, StorepS, StorepEnt, StorepFld, StorepFnc,
    Return,
    NotF, NotV, NotS, NotEnt, NotFnc,
    If, IfNot,
    Call0, Call1, Call2, Call3, Call4, Call5, Call6, Call7, Call8,
    State, Goto,
    AndF, OrF, BitAndF, BitOrF,
}

/// <summary>Value types a def can declare (pr_comp.h etype_t).</summary>
public enum QcType : ushort
{
    Void = 0, String = 1, Float = 2, Vector = 3, Entity = 4, Field = 5, Function = 6, Pointer = 7,
}

/// <summary>One instruction, widened from the file's 8 bytes so the interpreter indexes with ints.</summary>
public readonly struct QcStatement
{
    public readonly int Op, A, B, C;
    public QcStatement(int op, int a, int b, int c) { Op = op; A = a; B = b; C = c; }
}

/// <summary>A named global or entity field. <see cref="Offset"/> is in 32-bit cells.</summary>
public sealed record QcDef(QcType Type, bool SaveGlobal, int Offset, string Name);

public sealed class QcFunction
{
    /// <summary>Index of the first statement, or the negated builtin number when negative.</summary>
    public int FirstStatement { get; init; }
    /// <summary>First global cell of this function's parameters and locals.</summary>
    public int ParmStart { get; init; }
    /// <summary>Cells from <see cref="ParmStart"/> saved on entry and restored on return.</summary>
    public int Locals { get; init; }
    public string Name { get; init; } = "";
    public string File { get; init; } = "";
    public int NumParms { get; init; }
    /// <summary>Size in cells of each parameter (1, or 3 for a vector).</summary>
    public byte[] ParmSize { get; init; } = Array.Empty<byte>();

    public bool IsBuiltin => FirstStatement < 0;
    public int BuiltinNumber => -FirstStatement;
}

public sealed class ProgsFormatException : Exception
{
    public ProgsFormatException(string message) : base(message) { }
}

/// <summary>
/// A parsed and validated progs file (progs.dat / csprogs.dat / menu.dat), version 6.
///
/// Validation is what lets the interpreter run without per-instruction bounds checks: every global
/// operand and jump target of every statement is proven in range here, once. A file that passes can
/// still compute nonsense, but it cannot read or write outside the VM's own arrays. That matters
/// because this file is downloaded from whatever server the player joined.
/// </summary>
public sealed class ProgsFile
{
    public const int Version6 = 6;
    public const int HeaderSize = 60;

    // pr_comp.h: the fixed global cells every program shares.
    public const int OfsReturn = 1;
    public const int OfsParm0 = 4;
    public const int ReservedOfs = 28;
    public const int MaxParms = 8;

    /// <summary>Largest file accepted. The stock csprogs.dat is about 4 MB.</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;

    public int HeaderCrc { get; private init; }
    public QcStatement[] Statements { get; private init; } = Array.Empty<QcStatement>();
    public QcDef[] GlobalDefs { get; private init; } = Array.Empty<QcDef>();
    public QcDef[] FieldDefs { get; private init; } = Array.Empty<QcDef>();
    public QcFunction[] Functions { get; private init; } = Array.Empty<QcFunction>();
    /// <summary>The string table: NUL-terminated strings, addressed by byte offset.</summary>
    public byte[] Strings { get; private init; } = Array.Empty<byte>();
    /// <summary>Initial values of the globals, as raw 32-bit cells.</summary>
    public int[] Globals { get; private init; } = Array.Empty<int>();
    /// <summary>Cells per entity that the program itself declares.</summary>
    public int EntityFields { get; private init; }

    public static ProgsFile Load(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxFileBytes) throw new ProgsFormatException($"file is {data.Length} bytes; the limit is {MaxFileBytes}");
        if (data.Length < HeaderSize) throw new ProgsFormatException("file is shorter than the 60-byte header");

        int version = I32(data, 0);
        if (version != Version6)
            throw new ProgsFormatException($"progs version {version} is not supported (only version 6, which is what gmqcc writes)");

        int ofsStatements = I32(data, 8), numStatements = I32(data, 12);
        int ofsGlobalDefs = I32(data, 16), numGlobalDefs = I32(data, 20);
        int ofsFieldDefs = I32(data, 24), numFieldDefs = I32(data, 28);
        int ofsFunctions = I32(data, 32), numFunctions = I32(data, 36);
        int ofsStrings = I32(data, 40), numStrings = I32(data, 44);
        int ofsGlobals = I32(data, 48), numGlobals = I32(data, 52);
        int entityFields = I32(data, 56);

        ReadOnlySpan<byte> statementBytes = Lump(data, "statements", ofsStatements, numStatements, 8);
        ReadOnlySpan<byte> globalDefBytes = Lump(data, "globaldefs", ofsGlobalDefs, numGlobalDefs, 8);
        ReadOnlySpan<byte> fieldDefBytes = Lump(data, "fielddefs", ofsFieldDefs, numFieldDefs, 8);
        ReadOnlySpan<byte> functionBytes = Lump(data, "functions", ofsFunctions, numFunctions, 36);
        ReadOnlySpan<byte> stringBytes = Lump(data, "strings", ofsStrings, numStrings, 1);
        ReadOnlySpan<byte> globalBytes = Lump(data, "globals", ofsGlobals, numGlobals, 4);

        if (numStatements < 1) throw new ProgsFormatException("program has no statements");
        if (numFunctions < 1) throw new ProgsFormatException("program has no functions");
        if (numGlobals < ReservedOfs) throw new ProgsFormatException($"program has {numGlobals} globals; at least {ReservedOfs} are reserved");
        if (numStrings < 1 || stringBytes[^1] != 0) throw new ProgsFormatException("string table is empty or not NUL-terminated");
        if (entityFields < 0 || entityFields > 1 << 20) throw new ProgsFormatException($"implausible entity size of {entityFields} cells");

        byte[] strings = stringBytes.ToArray();

        // Two spare cells past the end, as DarkPlaces allocates: RETURN and the vector opcodes always
        // touch three consecutive cells, so an operand naming the last global reads harmless padding.
        int[] globals = new int[numGlobals + 2];
        for (int i = 0; i < numGlobals; i++) globals[i] = I32(globalBytes, i * 4);

        QcFunction[] functions = new QcFunction[numFunctions];
        for (int i = 0; i < numFunctions; i++)
        {
            ReadOnlySpan<byte> f = functionBytes.Slice(i * 36, 36);
            QcFunction function = new()
            {
                FirstStatement = I32(f, 0),
                ParmStart = I32(f, 4),
                Locals = I32(f, 8),
                Name = StringAt(strings, I32(f, 16), "function name"),
                File = StringAt(strings, I32(f, 20), "function file"),
                NumParms = I32(f, 24),
                ParmSize = f.Slice(28, 8).ToArray(),
            };
            if (function.FirstStatement >= numStatements)
                throw new ProgsFormatException($"function '{function.Name}' starts at statement {function.FirstStatement}, past the end");
            if (!function.IsBuiltin)
            {
                if (function.Locals < 0 || function.ParmStart < 0 || (long)function.ParmStart + function.Locals > numGlobals)
                    throw new ProgsFormatException($"function '{function.Name}' has locals outside the globals");
                if (function.NumParms < 0 || function.NumParms > MaxParms)
                    throw new ProgsFormatException($"function '{function.Name}' declares {function.NumParms} parameters");
                int parmCells = 0;
                for (int p = 0; p < function.NumParms; p++)
                {
                    if (function.ParmSize[p] > 3) throw new ProgsFormatException($"function '{function.Name}' has a parameter of {function.ParmSize[p]} cells");
                    parmCells += function.ParmSize[p];
                }
                // Entering a function copies its parameters to ParmStart onwards: that range must exist.
                if ((long)function.ParmStart + parmCells > numGlobals)
                    throw new ProgsFormatException($"function '{function.Name}' has parameters outside the globals");
            }
            functions[i] = function;
        }

        QcStatement[] statements = new QcStatement[numStatements];
        for (int i = 0; i < numStatements; i++)
        {
            ReadOnlySpan<byte> s = statementBytes.Slice(i * 8, 8);
            int op = U16(s, 0);
            // Operands are UNSIGNED global indices - Xonotic's client has 36,220 globals, past what a
            // signed 16-bit operand could name - and signed only where they are relative jumps.
            int a = U16(s, 2), b = U16(s, 4), c = U16(s, 6);
            switch (op)
            {
                case (int)QcOp.If or (int)QcOp.IfNot:
                    b = (short)b;
                    RequireGlobal(a, numGlobals, i);
                    RequireJump(i + b, numStatements, i);
                    break;
                case (int)QcOp.Goto:
                    a = (short)a;
                    RequireJump(i + a, numStatements, i);
                    break;
                case <= (int)QcOp.BitOrF:
                    RequireGlobal(a, numGlobals, i);
                    RequireGlobal(b, numGlobals, i);
                    RequireGlobal(c, numGlobals, i);
                    break;
                default:
                    throw new ProgsFormatException($"statement {i} uses opcode {op}; only the classic QuakeC opcodes 0..{(int)QcOp.BitOrF} are supported");
            }
            statements[i] = new QcStatement(op, a, b, c);
        }

        // Execution falls off the end of the array otherwise (DarkPlaces makes the same check).
        int last = statements[^1].Op;
        if (last is not ((int)QcOp.Done or (int)QcOp.Return or (int)QcOp.Goto))
            throw new ProgsFormatException("the last statement is not DONE, RETURN or GOTO");

        return new ProgsFile
        {
            HeaderCrc = I32(data, 4),
            Statements = statements,
            GlobalDefs = ReadDefs(globalDefBytes, strings, numGlobals, "global"),
            FieldDefs = ReadDefs(fieldDefBytes, strings, entityFields, "field"),
            Functions = functions,
            Strings = strings,
            Globals = globals,
            EntityFields = entityFields,
        };
    }

    /// <summary>Number of globals the file declares (the array carries two cells of padding beyond it).</summary>
    public int NumGlobals => Globals.Length - 2;

    private static QcDef[] ReadDefs(ReadOnlySpan<byte> bytes, byte[] strings, int limit, string what)
    {
        QcDef[] defs = new QcDef[bytes.Length / 8];
        for (int i = 0; i < defs.Length; i++)
        {
            ReadOnlySpan<byte> d = bytes.Slice(i * 8, 8);
            int rawType = U16(d, 0);
            QcType type = (QcType)(rawType & 0x7FFF);
            int offset = U16(d, 2);
            string name = StringAt(strings, I32(d, 4), $"{what} name");
            int size = type == QcType.Vector ? 3 : 1;
            // Def 0 is the conventional null def; real ones must lie inside the area they describe.
            if (offset + size > limit && i != 0 && name.Length > 0)
                throw new ProgsFormatException($"{what} '{name}' is at cell {offset}, outside the {limit} available");
            defs[i] = new QcDef(type, (rawType & 0x8000) != 0, offset, name);
        }
        return defs;
    }

    private static ReadOnlySpan<byte> Lump(ReadOnlySpan<byte> data, string name, int offset, int count, int elementSize)
    {
        if (offset < 0 || count < 0 || (long)offset + (long)count * elementSize > data.Length)
            throw new ProgsFormatException($"the {name} lump lies outside the file");
        return data.Slice(offset, count * elementSize);
    }

    private static void RequireGlobal(int index, int numGlobals, int statement)
    {
        if (index >= numGlobals) throw new ProgsFormatException($"statement {statement} refers to global {index}; the program has {numGlobals}");
    }

    private static void RequireJump(int target, int numStatements, int statement)
    {
        if (target < 0 || target >= numStatements) throw new ProgsFormatException($"statement {statement} jumps to {target}, outside the program");
    }

    private static string StringAt(byte[] strings, int offset, string what)
    {
        if (offset < 0 || offset >= strings.Length) throw new ProgsFormatException($"{what} offset {offset} is outside the string table");
        return DecodeCString(strings, offset);
    }

    /// <summary>Decodes the NUL-terminated string at <paramref name="offset"/> (the table always ends in NUL).</summary>
    internal static string DecodeCString(byte[] strings, int offset)
    {
        int length = Array.IndexOf(strings, (byte)0, offset) - offset;
        return length <= 0 ? "" : Encoding.UTF8.GetString(strings, offset, length);
    }

    private static int I32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
    private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
}
