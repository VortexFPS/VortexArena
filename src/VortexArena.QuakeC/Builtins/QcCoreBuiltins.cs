// Port of Base/darkplaces/prvm_cmds.c (VM_Warning, VM_VarString, VM_CheckEmptyString), the
// VM_SAFEPARMCOUNT macros of prvm_cmds.h, and the registration half of clvm_cmds.c vm_cl_builtins[].
using System.Globalization;

namespace VortexArena.QuakeC;

/// <summary>
/// The builtins of DarkPlaces' client program table that need nothing but the VM and an
/// <see cref="IQcHost"/>: maths, entity bookkeeping, cvars, files and console output. Builtin numbers
/// are the ones in <c>vm_cl_builtins[]</c>; anything touching the renderer, sound, the network or the
/// collision world is the host's to register.
///
/// One instance per VM. It owns the program's open files and searches, so dispose it (or call
/// <see cref="Reset"/>) when the program is unloaded.
/// </summary>
public sealed partial class QcCoreBuiltins : IDisposable
{
    private readonly QcVm _vm;
    private readonly IQcHost _host;

    /// <summary>Extension names checkextension answers true for. The host fills it; compared without regard to case.</summary>
    public ISet<string> Extensions { get; }

    public QcCoreBuiltins(QcVm vm, IQcHost host, ISet<string>? extensions = null)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Extensions = extensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        StartTime = host.RealTime;
    }

    /// <summary>Registers every builtin of this class under its DarkPlaces CSQC number.</summary>
    public void Register()
    {
        foreach ((int number, string _, QcBuiltin builtin) in Table()) _vm.RegisterBuiltin(number, builtin);
    }

    /// <summary>Number and DarkPlaces name of every builtin <see cref="Register"/> installs.</summary>
    public IEnumerable<(int Number, string Name)> Registered => Table().Select(e => (e.Number, e.Name));

    /// <summary>
    /// The builtin of that DarkPlaces name, for a host whose table gives it another number: the menu
    /// program's vm_m_builtins[] has the same C functions as the client's under different numbers
    /// (VM_ftos is #26 for a client program and #17 for the menu). Null if there is none of that name.
    /// </summary>
    public QcBuiltin? Find(string name)
    {
        foreach ((int _, string candidate, QcBuiltin builtin) in Table())
            if (candidate == name) return builtin;
        return null;
    }

    private (int Number, string Name, QcBuiltin Builtin)[] Table() => new (int, string, QcBuiltin)[]
    {
        (1, "makevectors", MakeVectors),
        (6, "break", Break),
        (7, "random", RandomFloat),
        (9, "normalize", Normalize),
        (10, "error", Error),
        (11, "objerror", ObjError),
        (12, "vlen", VLen),
        (13, "vectoyaw", VecToYaw),
        (14, "spawn", Spawn),
        (15, "remove", Remove),
        (18, "find", Find),
        (25, "dprint", DPrint),
        (28, "coredump", CoreDump),
        (29, "traceon", TraceOn),
        (30, "traceoff", TraceOff),
        (31, "eprint", EPrint),
        (36, "rint", Rint),
        (37, "floor", Floor),
        (38, "ceil", Ceil),
        (43, "fabs", FAbs),
        (45, "cvar", Cvar),
        (46, "localcmd", LocalCmd),
        (47, "nextent", NextEnt),
        (49, "ChangeYaw", ChangeYaw),
        (51, "vectoangles", VecToAngles),
        (60, "sin", Sin),
        (61, "cos", Cos),
        (62, "sqrt", Sqrt),
        (63, "changepitch", ChangePitch),
        (68, "precache_file", PrecacheFile),
        (72, "cvar_set", CvarSet),
        (77, "precache_file2", PrecacheFile),
        (91, "randomvec", RandomVec),
        (93, "registercvar", RegisterCvar),
        (94, "min", Min),
        (95, "max", Max),
        (96, "bound", Bound),
        (97, "pow", Pow),
        (98, "findfloat", FindFloat),
        (99, "checkextension", CheckExtension),
        (110, "fopen", FOpen),
        (111, "fclose", FClose),
        (112, "fgets", FGets),
        (113, "fputs", FPuts),
        (218, "bitshift", BitShift),
        (245, "modulo", Modulo),
        (339, "print", Print),
        (353, "wasfreed", WasFreed),
        (400, "copyentity", CopyEntity),
        (402, "findchain", FindChain),
        (403, "findchainfloat", FindChainFloat),
        (432, "vectorvectors", VectorVectors),
        (444, "search_begin", SearchBegin),
        (445, "search_end", SearchEnd),
        (446, "search_getsize", SearchGetSize),
        (447, "search_getfilename", SearchGetFilename),
        (448, "cvar_string", CvarString),
        (449, "findflags", FindFlags),
        (450, "findchainflags", FindChainFlags),
        (459, "ftoe", FToE),
        (471, "asin", ASin),
        (472, "acos", ACos),
        (473, "atan", ATan),
        (474, "atan2", ATan2),
        (475, "tan", Tan),
        (478, "strftime", StrFTime),
        (482, "cvar_defstring", CvarDefString),
        (495, "cvar_type", CvarType),
        (496, "numentityfields", NumEntityFields),
        (497, "entityfieldname", EntityFieldName),
        (498, "entityfieldtype", EntityFieldType),
        (499, "getentityfieldstring", GetEntityFieldString),
        (500, "putentityfieldstring", PutEntityFieldString),
        (503, "whichpack", WhichPack),
        (512, "etof", EToF),
        (518, "cvar_description", CvarDescription),
        (519, "gettime", GetTime),
        (529, "loadfromdata", LoadFromData),
        (530, "loadfromfile", LoadFromFile),
        (532, "log", Log),
        (605, "callfunction", CallFunction),
        (606, "writetofile", WriteToFile),
        (607, "isfunction", IsFunction),
        (613, "parseentitydata", ParseEntityData),
        (642, "coverage", Coverage),
    };

    /// <summary>Closes every file and search the program left open (VM_Cmd_Reset).</summary>
    public void Reset()
    {
        for (int i = 0; i < _files.Length; i++)
        {
            _files[i]?.Dispose();
            _files[i] = null;
        }
        Array.Clear(_searches);
    }

    public void Dispose() => Reset();

    // ---- helpers shared by every group -------------------------------------------------------------

    private QcRuntimeException Fault(string message) => new($"{_vm.Name}: {message}");

    private void Warning(string message) => _host.Warning($"{_vm.Name} VM warning: {message}");

    // VM_SAFEPARMCOUNT. DarkPlaces treats a wrong argument count as fatal to the program, which is also
    // what stops a builtin reading parameter cells the caller never wrote.
    private void Parms(int count, string name)
    {
        if (_vm.ArgCount != count) throw Fault($"{name} wrong parameter count {_vm.ArgCount} ({count} expected ) !");
    }

    private void Parms(int min, int max, string name)
    {
        if (_vm.ArgCount < min || _vm.ArgCount > max)
            throw Fault($"{name} wrong parameter count {_vm.ArgCount} ({min} to {max} expected ) !");
    }

    /// <summary>VM_VarString: arguments <paramref name="first"/> onward concatenated, cut at the tempstring size.</summary>
    private string VarString(int first)
    {
        int count = Math.Min(_vm.ArgCount, ProgsFile.MaxParms);
        if (first >= count) return "";
        if (first == count - 1)
        {
            string only = _vm.ArgString(first);
            return only.Length < _vm.MaxStringLength ? only : Truncated(only, only[..(_vm.MaxStringLength - 1)]);
        }

        int room = _vm.MaxStringLength - 1;
        System.Text.StringBuilder text = new();
        for (int i = first; i < count && text.Length < room; i++)
        {
            string s = _vm.ArgString(i);
            if (text.Length + s.Length <= room) text.Append(s);
            else text.Append(Truncated(s, s[..(room - text.Length)]));
        }
        return text.ToString();
    }

    private string Truncated(string whole, string kept)
    {
        Warning($"{kept.Length} of {_vm.MaxStringLength - 1} bytes available, will truncate {whole.Length} byte string\n");
        return kept;
    }

    // VM_CheckEmptyString. ISWHITESPACE counts the terminator, so the empty string is "bad" too.
    private void CheckEmptyString(string s)
    {
        if (s.Length == 0 || IsWhitespace(s[0])) throw Fault("Bad string");
    }

    // qdefs.h ISWHITESPACE, minus the NUL case (a C# string has no terminator to run into).
    internal static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n';

    // VM_CheckFieldOffset: a field offset handed to a builtin is program data nothing has validated yet.
    private int FieldArg(int index, int cells = 1)
    {
        int offset = _vm.ArgInt(index);
        if ((uint)offset > (uint)(_vm.EntityFields - cells)) throw Fault($"field offset {offset} is out of bounds");
        return offset;
    }

    /// <summary>Name of the QuakeC function running now (DarkPlaces' prog->xfunction), from the VM's stack trace.</summary>
    private string CurrentFunctionName()
    {
        // "  file : name : statement n" - the first line is the innermost frame.
        string trace = _vm.StackTrace();
        int end = trace.IndexOf('\n');
        string[] parts = (end < 0 ? trace : trace[..end]).Split(" : ");
        return parts.Length >= 2 ? parts[1] : "?";
    }

    // ---- C library text conversions the ports depend on --------------------------------------------

    /// <summary>C's <c>printf("%.{precision}g")</c> for a double, in the C locale.</summary>
    internal static string FormatG(double value, int precision)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (precision <= 0) precision = 1;
        if (value == 0) return double.IsNegative(value) ? "-0" : "0";

        // The exponent %g switches on is the one of the value AFTER rounding to the precision, which is
        // exactly what the E format reports.
        string scientific = value.ToString("E" + (precision - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int e = scientific.LastIndexOf('E');
        int exponent = int.Parse(scientific.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponent < -4 || exponent >= precision)
            return TrimFraction(scientific[..e]) + (exponent < 0 ? "e-" : "e+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        return TrimFraction(value.ToString("F" + (precision - 1 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

        static string TrimFraction(string number) => number.Contains('.') ? number.TrimEnd('0').TrimEnd('.') : number;
    }

    /// <summary>C's <c>atof</c>: the longest numeric prefix after leading whitespace, 0 if there is none. Never throws.</summary>
    internal static double Atof(ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')) i++;
        int start = i;
        bool negative = false;
        if (i < s.Length && (s[i] is '+' or '-')) negative = s[i++] == '-';

        if (s[i..].StartsWith("inf", StringComparison.OrdinalIgnoreCase)) return negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (s[i..].StartsWith("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;

        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] is 'x' or 'X') && HexFloat(s[(i + 2)..], out double hex))
            return negative ? -hex : hex;

        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) return 0;
        if (i < s.Length && (s[i] is 'e' or 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] is '+' or '-')) j++;
            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                i = j;
            }
        }
        return double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
    }

    private static bool HexFloat(ReadOnlySpan<char> s, out double value)
    {
        value = 0;
        int i = 0, digits = 0, scale = 0;
        for (; i < s.Length && char.IsAsciiHexDigit(s[i]); i++, digits++) value = value * 16 + Convert.ToInt32(s[i].ToString(), 16);
        if (i < s.Length && s[i] == '.')
            for (i++; i < s.Length && char.IsAsciiHexDigit(s[i]); i++, digits++, scale -= 4) value = value * 16 + Convert.ToInt32(s[i].ToString(), 16);
        if (digits == 0) return false;
        if (i < s.Length && (s[i] is 'p' or 'P'))
        {
            int j = i + 1, sign = 1, exponent = 0;
            if (j < s.Length && (s[j] is '+' or '-')) sign = s[j++] == '-' ? -1 : 1;
            for (; j < s.Length && char.IsAsciiDigit(s[j]); j++) exponent = Math.Min(exponent * 10 + (s[j] - '0'), 100000);
            scale += sign * exponent;
        }
        value = Math.ScaleB(value, scale);
        return true;
    }

    /// <summary>C's <c>atoi</c>, saturating where C leaves the result undefined.</summary>
    internal static int Atoi(ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')) i++;
        bool negative = false;
        if (i < s.Length && (s[i] is '+' or '-')) negative = s[i++] == '-';
        long value = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++) value = Math.Min(value * 10 + (s[i] - '0'), 1L << 40);
        return (int)Math.Clamp(negative ? -value : value, int.MinValue, int.MaxValue);
    }
}
