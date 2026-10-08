// Port of Base/darkplaces/prvm_execprogram.h (the interpreter), prvm_exec.c (function entry/exit)
// and the string and edict bookkeeping of prvm_edict.c.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VortexArena.QuakeC;

/// <summary>A native function the program can call by number. Arguments and the result go through the VM.</summary>
public delegate void QcBuiltin(QcVm vm);

/// <summary>The program did something the VM cannot continue from. Carries the QuakeC call stack.</summary>
public sealed class QcRuntimeException : Exception
{
    public QcRuntimeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// One loaded QuakeC program and its memory: globals, entity fields, strings, and the call stack.
///
/// Memory is modelled the way DarkPlaces models it, not the way C# would like it: globals and entity
/// fields are flat arrays of untyped 32-bit cells, and a float, an entity number, a string handle and
/// a function index are all just one cell. The program relies on that (it copies cells without
/// knowing their type, and stores field offsets in variables), so a typed object model cannot run it.
///
/// Not thread-safe. Re-entrant: a builtin may call <see cref="Execute"/>.
/// </summary>
public sealed class QcVm
{
    // prvm limits (progsvm.h).
    public const int MaxStackDepth = 1024;
    public const int LocalStackSize = 16384;
    /// <summary>Taken jumps allowed per outermost <see cref="Execute"/> before the program is declared stuck.</summary>
    public const int RunawayJumpLimit = 10_000_000;

    // String handle tags. Handles below the string table size are offsets into it.
    private const int KnownStringTag = 0x40000000;
    private const int TempStringTag = 0x20000000;
    private const int StringIndexMask = 0x1FFFFFFF;

    public ProgsFile Progs { get; }
    public string Name { get; }

    private readonly QcStatement[] _statements;
    private readonly QcFunction[] _functions;
    private readonly int[] _globals;
    private readonly int _numGlobals;

    private int[] _fields;
    private bool[] _edictFree;
    private double[] _edictFreeTime;
    private int _entityFields;
    private int _numEdicts;
    private int _maxEdicts;

    private readonly int[] _stackFunction = new int[MaxStackDepth];
    private readonly int[] _stackStatement = new int[MaxStackDepth];
    private readonly int[] _localStack = new int[LocalStackSize];
    private int _depth;
    private int _localStackUsed;
    private int _currentFunction;
    private int _currentStatement;
    private int _jumpCount;
    private int _executeDepth;

    private readonly Dictionary<string, QcDef> _globalsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QcDef> _fieldsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _functionsByName = new(StringComparer.Ordinal);
    private readonly List<QcDef> _extraFields = new();

    // Decoded text of the program's string constants, indexed by their offset in the string table.
    // An array rather than a dictionary: this lookup sits under every string builtin (nine million
    // substring and stof calls in one recorded match), and the table is fixed for the program's life.
    // Eight bytes per table byte - about 6 MB for Xonotic's client program.
    private string?[]? _staticStrings;
    private readonly List<string?> _knownStrings = new();
    private readonly Stack<int> _freeKnownStrings = new();
    private readonly Dictionary<string, int> _engineStrings = new(StringComparer.Ordinal);
    private readonly List<string> _tempStrings = new();
    private int _tempStringCount;

    private QcBuiltin?[] _builtins = new QcBuiltin?[700];

    // Offsets OP_STATE needs; resolved on first use.
    private int _ofsSelf = -1, _ofsTime = -1, _fldNextThink = -1, _fldFrame = -1, _fldThink = -1;

    /// <summary>Number of arguments the program passed to the builtin now running.</summary>
    public int ArgCount { get; private set; }

    /// <summary>Hard ceiling on entities, whatever the program asks for.</summary>
    public int EdictLimit { get; init; } = 32768;

    /// <summary>Longest string a builtin may create; DarkPlaces' VM_TEMPSTRING_MAXSIZE.</summary>
    public int MaxStringLength { get; init; } = 16384;

    /// <summary>Called for a builtin number nothing is registered for. Returning normally makes the call a no-op that returns 0.</summary>
    public Action<QcVm, int, string>? UnknownBuiltin { get; set; }

    public QcVm(ProgsFile progs, string name = "progs", int initialEdicts = 512)
    {
        Progs = progs;
        Name = name;
        _statements = progs.Statements;
        _functions = progs.Functions;
        _globals = (int[])progs.Globals.Clone();
        _numGlobals = progs.NumGlobals;
        _entityFields = progs.EntityFields;

        foreach (QcDef def in progs.GlobalDefs) if (def.Name.Length > 0) _globalsByName.TryAdd(def.Name, def);
        foreach (QcDef def in progs.FieldDefs) if (def.Name.Length > 0) _fieldsByName.TryAdd(def.Name, def);
        for (int i = 1; i < _functions.Length; i++) if (_functions[i].Name.Length > 0) _functionsByName.TryAdd(_functions[i].Name, i);

        _maxEdicts = Math.Clamp(initialEdicts, 1, EdictLimit);
        _fields = new int[_maxEdicts * _entityFields];
        _edictFree = new bool[_maxEdicts];
        _edictFreeTime = new double[_maxEdicts];
        _numEdicts = 1; // edict 0 is the world and always exists
    }

    // ---- names -------------------------------------------------------------------------------------

    public QcDef? FindGlobal(string name) => _globalsByName.GetValueOrDefault(name);
    public QcDef? FindField(string name) => _fieldsByName.GetValueOrDefault(name);
    /// <summary>Function index for <paramref name="name"/>, or 0 (the null function) if the program has none.</summary>
    public int FindFunction(string name) => _functionsByName.GetValueOrDefault(name);
    public IReadOnlyList<QcFunction> Functions => _functions;
    public IEnumerable<QcDef> GlobalDefs => _globalsByName.Values;
    public IEnumerable<QcDef> FieldDefs => _fieldsByName.Values;

    /// <summary>
    /// Returns the cell offset of entity field <paramref name="name"/>, appending the field if the program
    /// does not declare it - how the engine gets storage for fields it needs (.entnum, .drawmask) that a
    /// given program never mentions. Only before the first entity beyond the world is allocated.
    /// </summary>
    public int EnsureField(string name, QcType type)
    {
        if (_fieldsByName.TryGetValue(name, out QcDef? existing)) return existing.Offset;
        if (_numEdicts > 1) throw new InvalidOperationException("fields cannot be added once entities exist");
        QcDef def = new(type, false, _entityFields, name);
        _entityFields += type == QcType.Vector ? 3 : 1;
        _fieldsByName[name] = def;
        _extraFields.Add(def);
        _fields = new int[_maxEdicts * _entityFields];
        return def.Offset;
    }

    public void RegisterBuiltin(int number, QcBuiltin builtin)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        if (number >= _builtins.Length) Array.Resize(ref _builtins, number + 64);
        _builtins[number] = builtin;
    }

    public bool HasBuiltin(int number) => number > 0 && number < _builtins.Length && _builtins[number] is not null;

    // ---- globals -----------------------------------------------------------------------------------

    public int NumGlobals => _numGlobals;

    public ref int GlobalInt(int offset) => ref _globals[CheckGlobal(offset, 1)];
    public ref float GlobalFloat(int offset) => ref Unsafe.As<int, float>(ref _globals[CheckGlobal(offset, 1)]);
    public ref QcVector GlobalVector(int offset) => ref Unsafe.As<int, QcVector>(ref _globals[CheckGlobal(offset, 3)]);

    private int CheckGlobal(int offset, int cells)
    {
        if ((uint)offset > (uint)(_numGlobals - cells)) throw new QcRuntimeException($"global offset {offset} is out of range");
        return offset;
    }

    // ---- builtin arguments and results -------------------------------------------------------------

    public float ArgFloat(int index) => Unsafe.As<int, float>(ref _globals[ProgsFile.OfsParm0 + index * 3]);
    public int ArgInt(int index) => _globals[ProgsFile.OfsParm0 + index * 3];
    public QcVector ArgVector(int index) => Unsafe.As<int, QcVector>(ref _globals[ProgsFile.OfsParm0 + index * 3]);
    public string ArgString(int index) => GetString(_globals[ProgsFile.OfsParm0 + index * 3]);
    /// <summary>Entity argument, validated: a builtin never receives an entity number outside the edict array.</summary>
    public int ArgEdict(int index)
    {
        int edict = _globals[ProgsFile.OfsParm0 + index * 3];
        if ((uint)edict >= (uint)_maxEdicts) throw new QcRuntimeException($"entity {edict} is out of range");
        return edict;
    }

    public void SetArgFloat(int index, float value) => Unsafe.As<int, float>(ref _globals[ProgsFile.OfsParm0 + index * 3]) = value;
    public void SetArgInt(int index, int value) => _globals[ProgsFile.OfsParm0 + index * 3] = value;
    public void SetArgVector(int index, QcVector value) => Unsafe.As<int, QcVector>(ref _globals[ProgsFile.OfsParm0 + index * 3]) = value;

    public void ReturnFloat(float value)
    {
        Unsafe.As<int, float>(ref _globals[ProgsFile.OfsReturn]) = value;
    }
    public void ReturnInt(int value) => _globals[ProgsFile.OfsReturn] = value;
    public void ReturnVector(QcVector value) => Unsafe.As<int, QcVector>(ref _globals[ProgsFile.OfsReturn]) = value;
    /// <summary>Returns a string that lives until the outermost engine-to-program call finishes.</summary>
    public void ReturnString(string value) => _globals[ProgsFile.OfsReturn] = TempString(value);

    public float ResultFloat => Unsafe.As<int, float>(ref _globals[ProgsFile.OfsReturn]);
    public int ResultInt => _globals[ProgsFile.OfsReturn];
    public QcVector ResultVector => Unsafe.As<int, QcVector>(ref _globals[ProgsFile.OfsReturn]);
    public string ResultString => GetString(_globals[ProgsFile.OfsReturn]);

    // ---- entities ----------------------------------------------------------------------------------

    public int EntityFields => _entityFields;
    /// <summary>One past the highest entity number in use.</summary>
    public int NumEdicts => _numEdicts;
    public int MaxEdicts => _maxEdicts;

    public bool IsFree(int edict) => (uint)edict < (uint)_maxEdicts && _edictFree[edict];

    public ref int FieldInt(int edict, int offset) => ref _fields[FieldIndex(edict, offset, 1)];
    public ref float FieldFloat(int edict, int offset) => ref Unsafe.As<int, float>(ref _fields[FieldIndex(edict, offset, 1)]);
    public ref QcVector FieldVector(int edict, int offset) => ref Unsafe.As<int, QcVector>(ref _fields[FieldIndex(edict, offset, 3)]);

    private int FieldIndex(int edict, int offset, int cells)
    {
        if ((uint)edict >= (uint)_maxEdicts) throw new QcRuntimeException($"entity {edict} is out of range");
        if ((uint)offset > (uint)(_entityFields - cells)) throw new QcRuntimeException($"field offset {offset} is out of range");
        return edict * _entityFields + offset;
    }

    /// <summary>
    /// Allocates an entity: the lowest free slot that <paramref name="canReuse"/> accepts (DarkPlaces
    /// refuses to recycle a slot freed very recently, so stale references have time to die), else a new one.
    /// </summary>
    public int AllocEdict(int firstUsable = 1, Func<int, double, bool>? canReuse = null)
    {
        for (int i = Math.Max(firstUsable, 1); i < _numEdicts; i++)
        {
            if (_edictFree[i] && (canReuse is null || canReuse(i, _edictFreeTime[i])))
            {
                _edictFree[i] = false;
                ClearEdict(i);
                return i;
            }
        }

        if (_numEdicts >= EdictLimit) throw new QcRuntimeException($"{Name}: no free entities (limit {EdictLimit})");
        if (_numEdicts >= _maxEdicts) GrowEdicts(Math.Min(EdictLimit, _maxEdicts + 256));
        int edict = _numEdicts++;
        _edictFree[edict] = false;
        ClearEdict(edict);
        return edict;
    }

    public void FreeEdict(int edict, double freeTime)
    {
        if (edict <= 0 || edict >= _numEdicts) throw new QcRuntimeException($"cannot free entity {edict}");
        ClearEdict(edict);
        _edictFree[edict] = true;
        _edictFreeTime[edict] = freeTime;
    }

    public void ClearEdict(int edict) => Array.Clear(_fields, edict * _entityFields, _entityFields);

    /// <summary>
    /// Makes entities 1..<paramref name="count"/> exist (allocated, zeroed, not free) without going
    /// through <see cref="AllocEdict"/>: the server's client slots, which SV_SpawnServer addresses by
    /// number before any spawn() runs ("leave slots at start for clients only").
    /// </summary>
    public void ReserveEdicts(int count)
    {
        if (count < 0 || count >= EdictLimit) throw new ArgumentOutOfRangeException(nameof(count));
        if (count + 1 > _maxEdicts) GrowEdicts(Math.Min(EdictLimit, count + 1 + 256));
        for (int i = Math.Max(_numEdicts, 1); i <= count; i++)
        {
            _edictFree[i] = false;
            ClearEdict(i);
        }
        if (_numEdicts < count + 1) _numEdicts = count + 1;
    }

    /// <summary>When <paramref name="edict"/> was last freed, on the clock <see cref="FreeEdict"/> was given.</summary>
    public double EdictFreeTime(int edict) => (uint)edict < (uint)_maxEdicts ? _edictFreeTime[edict] : 0;

    /// <summary>
    /// The end of SV_Physics: "for (;PRVM_ED_CanAlloc(prog, PRVM_EDICT_NUM(prog->num_edicts - 1));
    /// prog->num_edicts--)" - drops trailing free entities that <paramref name="canReuse"/> would hand
    /// out again, so loops over every entity stay short. Never below <paramref name="minimum"/>.
    /// </summary>
    public void TrimEdicts(int minimum, Func<int, double, bool> canReuse)
    {
        while (_numEdicts > Math.Max(minimum, 1) && _edictFree[_numEdicts - 1] && canReuse(_numEdicts - 1, _edictFreeTime[_numEdicts - 1]))
            _numEdicts--;
    }

    private void GrowEdicts(int newMax)
    {
        Array.Resize(ref _fields, newMax * _entityFields);
        Array.Resize(ref _edictFree, newMax);
        Array.Resize(ref _edictFreeTime, newMax);
        _maxEdicts = newMax;
    }

    // ---- strings -----------------------------------------------------------------------------------

    /// <summary>The text behind a string handle. Never throws: a bad handle is the empty string, as in DarkPlaces.</summary>
    public string GetString(int handle)
    {
        if (handle <= 0) return "";
        if (handle < Progs.Strings.Length)
        {
            string?[] cache = _staticStrings ??= new string?[Progs.Strings.Length];
            return cache[handle] ??= ProgsFile.DecodeCString(Progs.Strings, handle);
        }
        int index = handle & StringIndexMask;
        if ((handle & KnownStringTag) != 0) return index < _knownStrings.Count ? _knownStrings[index] ?? "" : "";
        if ((handle & TempStringTag) != 0) return index < _tempStrings.Count ? _tempStrings[index] : "";
        return "";
    }

    /// <summary>A handle valid until the outermost <see cref="Execute"/> returns. What builtins return.</summary>
    public int TempString(string text)
    {
        // An empty result is still a real, non-null handle (PRVM_SetTempString, prvm_edict.c:3525).
        // Programs test the handle, not the text: `while ((s = fgets(fh)))` must continue past a blank
        // line and stop only at end of file, which is the one case that returns the null string.
        if (text.Length > MaxStringLength) text = text[..MaxStringLength];
        if (_tempStringCount >= StringIndexMask) throw new QcRuntimeException($"{Name}: out of temporary strings");
        if (_tempStringCount < _tempStrings.Count) _tempStrings[_tempStringCount] = text;
        else _tempStrings.Add(text);
        return TempStringTag | _tempStringCount++;
    }

    /// <summary>
    /// How many temp strings are live (prog->tempstringsbuf.cursize). The engine makes a temp string
    /// for a text argument BEFORE it calls <see cref="Execute"/>, which therefore cannot count it among
    /// the ones to drop on return; the caller takes this mark first and hands it to
    /// <see cref="ReleaseTempStrings"/> afterwards, as CL_VM_Parse_StuffCmd and its siblings do.
    /// </summary>
    public int TempStringMark => _tempStringCount;

    /// <summary>Drops every temp string made since <paramref name="mark"/>. Ignored while the program is running.</summary>
    public void ReleaseTempStrings(int mark)
    {
        if (_executeDepth == 0 && mark >= 0 && mark < _tempStringCount) _tempStringCount = mark;
    }

    /// <summary>True while a call into the program is in progress (a builtin is asking).</summary>
    public bool IsExecuting => _executeDepth > 0;

    /// <summary>A handle that lives until <see cref="FreeString"/> (the strzone builtin).</summary>
    public int AllocString(string text)
    {
        if (text.Length > MaxStringLength) text = text[..MaxStringLength];
        int index;
        if (_freeKnownStrings.Count > 0) _knownStrings[index = _freeKnownStrings.Pop()] = text;
        else
        {
            if (_knownStrings.Count >= StringIndexMask) throw new QcRuntimeException($"{Name}: out of strings");
            index = _knownStrings.Count;
            _knownStrings.Add(text);
        }
        return KnownStringTag | index;
    }

    /// <summary>Frees a handle from <see cref="AllocString"/>. Anything else is ignored (the strunzone builtin).</summary>
    public void FreeString(int handle)
    {
        if (handle <= 0 || (handle & KnownStringTag) == 0) return;
        int index = handle & StringIndexMask;
        if (index >= _knownStrings.Count || _knownStrings[index] is null) return;
        _knownStrings[index] = null;
        _freeKnownStrings.Push(index);
    }

    /// <summary>A permanent handle for text the engine writes into the program (a map name, a model name). Interned.</summary>
    public int EngineString(string text)
    {
        if (text.Length == 0) return 0;
        if (!_engineStrings.TryGetValue(text, out int handle))
        {
            handle = KnownStringTag | _knownStrings.Count;
            _knownStrings.Add(text);
            _engineStrings[text] = handle;
        }
        return handle;
    }

    /// <summary>
    /// Replaces the text behind a handle from <see cref="AllocString"/>, keeping the handle. This is
    /// DarkPlaces' PRVM_SetEngineString of a buffer the engine then rewrites (a client's name): every
    /// copy of the handle the program holds reads the new text. Ignored for any other handle.
    /// </summary>
    public void SetString(int handle, string text)
    {
        if (handle <= 0 || (handle & KnownStringTag) == 0) return;
        int index = handle & StringIndexMask;
        if (index >= _knownStrings.Count || _knownStrings[index] is null) return;
        _knownStrings[index] = text.Length > MaxStringLength ? text[..MaxStringLength] : text;
    }

    public int ZonedStringCount => _knownStrings.Count - _freeKnownStrings.Count - _engineStrings.Count;

    // ---- execution ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs function <paramref name="function"/> to completion. Arguments are whatever was written with
    /// the SetArg methods; the result is read with the Result properties.
    /// </summary>
    /// <exception cref="QcRuntimeException">The program faulted. The VM's stack is unwound to where this call began.</exception>
    public void Execute(int function, int argCount = 0)
    {
        if (function <= 0 || function >= _functions.Length)
            throw new QcRuntimeException($"{Name}: attempted to execute function {function}, which does not exist");

        int savedDepth = _depth, savedLocals = _localStackUsed, savedFunction = _currentFunction, savedStatement = _currentStatement;
        int savedTemps = _tempStringCount;
        long profileStart = Profile is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        if (_executeDepth++ == 0) _jumpCount = 0;
        try
        {
            QcFunction target = _functions[function];
            ArgCount = argCount;
            if (target.IsBuiltin)
            {
                CallBuiltin(target);
                return;
            }
            // The frame pushed here returns to a statement index of -1; Run stops when it pops it.
            _currentStatement = -1;
            Run(EnterFunction(function), savedDepth);
        }
        catch (Exception e) when (e is not QcRuntimeException)
        {
            // A builtin threw something of its own (a null host service, an index bug). Report it as
            // a program fault with the QuakeC stack attached, which is what locates it.
            string stack = StackTrace();
            Unwind(savedDepth, savedLocals);
            throw new QcRuntimeException($"{Name}: {e.GetType().Name}: {e.Message}\n{stack}", e);
        }
        catch (QcRuntimeException e) when (!e.Message.Contains('\n'))
        {
            string stack = StackTrace();
            Unwind(savedDepth, savedLocals);
            throw new QcRuntimeException($"{e.Message}\n{stack}", e);
        }
        catch
        {
            Unwind(savedDepth, savedLocals);
            throw;
        }
        finally
        {
            _executeDepth--;
            if (Profile is { } profile)
            {
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - profileStart;
                // Program time spent inside a builtin's re-entrant call belongs to the program, not to
                // the builtin that made the call.
                if (_executeDepth == 0) profile.TotalTicks += elapsed;
                else _nestedExecuteTicks += elapsed;
            }
            _currentFunction = savedFunction;
            _currentStatement = savedStatement;
            // Temp strings die when the OUTERMOST call returns, not a nested one: a string handed back
            // through callfunction (a builtin re-entering the VM) is still held by the QuakeC that called
            // it. The slots keep their text until reused, so the engine can still read a string result.
            if (_executeDepth == 0) _tempStringCount = savedTemps;
        }
    }

    /// <summary>Convenience: <see cref="Execute"/> by name. Returns false if the program has no such function.</summary>
    public bool TryExecute(string functionName, int argCount = 0)
    {
        int function = FindFunction(functionName);
        if (function == 0) return false;
        Execute(function, argCount);
        return true;
    }

    private void Unwind(int depth, int localStackUsed)
    {
        // Restore the locals of every frame abandoned by the fault, innermost first, so the globals they
        // overlap are what the surviving callers left there.
        while (_depth > depth) LeaveFunction();
        _localStackUsed = localStackUsed;
    }

    public string StackTrace()
    {
        StringBuilder text = new();
        Append(_currentFunction, _currentStatement);
        for (int i = _depth - 1; i >= 0 && text.Length < 4000; i--)
            if (_stackFunction[i] != 0) Append(_stackFunction[i], _stackStatement[i]);
        return text.ToString().TrimEnd();

        void Append(int function, int statement)
        {
            if (function <= 0 || function >= _functions.Length) return;
            QcFunction f = _functions[function];
            text.Append("  ").Append(f.File).Append(" : ").Append(f.Name).Append(" : statement ").Append(statement - f.FirstStatement).Append('\n');
        }
    }

    private int EnterFunction(int function)
    {
        QcFunction f = _functions[function];
        int locals = f.Locals;
        // Both checks come before any state changes, so a fault here leaves nothing half-entered to unwind.
        if (_depth >= MaxStackDepth - 1) throw new QcRuntimeException($"{Name}: stack overflow");
        if (_localStackUsed + locals > LocalStackSize) throw new QcRuntimeException($"{Name}: locals stack overflow");
        _stackStatement[_depth] = _currentStatement;
        _stackFunction[_depth] = _currentFunction;
        _depth++;

        // Functions share global cells for their locals (gmqcc -Ooverlap-locals), so whatever is in this
        // function's range belongs to a caller: save it, and put it back on return.
        Array.Copy(_globals, f.ParmStart, _localStack, _localStackUsed, locals);
        _localStackUsed += locals;

        int o = f.ParmStart;
        for (int i = 0; i < f.NumParms; i++)
            for (int j = 0; j < f.ParmSize[i]; j++)
                _globals[o++] = _globals[ProgsFile.OfsParm0 + i * 3 + j];

        _currentFunction = function;
        return f.FirstStatement;
    }

    /// <summary>Pops a frame and returns the statement index of the call that created it.</summary>
    private int LeaveFunction()
    {
        QcFunction f = _functions[_currentFunction];
        _localStackUsed -= f.Locals;
        Array.Copy(_localStack, _localStackUsed, _globals, f.ParmStart, f.Locals);
        _depth--;
        _currentFunction = _stackFunction[_depth];
        return _stackStatement[_depth];
    }

    private void CallBuiltin(QcFunction function)
    {
        int number = function.BuiltinNumber;
        QcBuiltin? builtin = number < _builtins.Length ? _builtins[number] : null;
        if (builtin is not null)
        {
            if (Profile is null) builtin(this);
            else CallBuiltinProfiled(builtin, number, Profile);
            return;
        }
        if (UnknownBuiltin is null)
            throw new QcRuntimeException($"{Name}: no such builtin #{number} ({function.Name})");
        _globals[ProgsFile.OfsReturn] = 0;
        _globals[ProgsFile.OfsReturn + 1] = 0;
        _globals[ProgsFile.OfsReturn + 2] = 0;
        UnknownBuiltin(this, number, function.Name);
    }

    /// <summary>
    /// When set, every builtin call is timed (exclusive of any program code it re-enters) and every
    /// outermost <see cref="Execute"/> is timed, so the cost of a frame can be split into "inside the
    /// interpreter" and "inside the engine". Off by default; the check is one null test per builtin call.
    /// </summary>
    public QcProfile? Profile { get; set; }

    private long _nestedExecuteTicks;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CallBuiltinProfiled(QcBuiltin builtin, int number, QcProfile profile)
    {
        long outerNested = _nestedExecuteTicks;
        _nestedExecuteTicks = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { builtin(this); }
        finally
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            profile.Record(number, elapsed - _nestedExecuteTicks);
            _nestedExecuteTicks = outerNested;
        }
    }

    // DarkPlaces' FLOAT_IS_TRUE_FOR_INT: every bit pattern is true except +0.0 and -0.0. Tested on the
    // raw cell, so it gives the right answer for entities, strings and functions as well as floats.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTrue(int cell) => (cell & 0x7FFFFFFF) != 0;

    // C's (int)float, pinned to the x86 result for values that do not fit. .NET's own conversion
    // returns different answers on x64 and ARM64 for those, and the program must not notice the CPU.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FloatToInt(float value) =>
        value >= -2147483648f && value < 2147483648f ? (int)value : int.MinValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref float F(ref int g, int index) => ref Unsafe.As<int, float>(ref Unsafe.Add(ref g, index));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref int I(ref int g, int index) => ref Unsafe.Add(ref g, index);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Run(int pc, int exitDepth)
    {
        // Unchecked access to the globals is sound because ProgsFile.Load proved every operand of every
        // statement in range, and the array (with its two cells of padding) is never reallocated.
        ref int g = ref MemoryMarshal.GetArrayDataReference(_globals);
        QcStatement[] statements = _statements;

        while (true)
        {
            // By reference: no 16-byte copy per instruction. Unchecked, because every way pc can change
            // was proven in range by ProgsFile.Load (jump targets, function entry points, and a final
            // statement that cannot fall off the end).
            ref readonly QcStatement st = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(statements), pc);
            switch ((QcOp)st.Op)
            {
                case QcOp.AddF: F(ref g, st.C) = F(ref g, st.A) + F(ref g, st.B); break;
                case QcOp.SubF: F(ref g, st.C) = F(ref g, st.A) - F(ref g, st.B); break;
                case QcOp.MulF: F(ref g, st.C) = F(ref g, st.A) * F(ref g, st.B); break;
                // Division by zero is the IEEE result (prvm_gameplayfix_div0is0 defaults to off).
                case QcOp.DivF: F(ref g, st.C) = F(ref g, st.A) / F(ref g, st.B); break;

                case QcOp.AddV:
                {
                    float x = F(ref g, st.A) + F(ref g, st.B), y = F(ref g, st.A + 1) + F(ref g, st.B + 1), z = F(ref g, st.A + 2) + F(ref g, st.B + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }
                case QcOp.SubV:
                {
                    float x = F(ref g, st.A) - F(ref g, st.B), y = F(ref g, st.A + 1) - F(ref g, st.B + 1), z = F(ref g, st.A + 2) - F(ref g, st.B + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }
                case QcOp.MulV:
                    F(ref g, st.C) = F(ref g, st.A) * F(ref g, st.B) + F(ref g, st.A + 1) * F(ref g, st.B + 1) + F(ref g, st.A + 2) * F(ref g, st.B + 2);
                    break;
                case QcOp.MulFV:
                {
                    float s = F(ref g, st.A);
                    float x = s * F(ref g, st.B), y = s * F(ref g, st.B + 1), z = s * F(ref g, st.B + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }
                case QcOp.MulVF:
                {
                    float s = F(ref g, st.B);
                    float x = s * F(ref g, st.A), y = s * F(ref g, st.A + 1), z = s * F(ref g, st.A + 2);
                    F(ref g, st.C) = x; F(ref g, st.C + 1) = y; F(ref g, st.C + 2) = z;
                    break;
                }

                case QcOp.BitAndF: F(ref g, st.C) = FloatToInt(F(ref g, st.A)) & FloatToInt(F(ref g, st.B)); break;
                case QcOp.BitOrF: F(ref g, st.C) = FloatToInt(F(ref g, st.A)) | FloatToInt(F(ref g, st.B)); break;

                case QcOp.GeF: F(ref g, st.C) = F(ref g, st.A) >= F(ref g, st.B) ? 1f : 0f; break;
                case QcOp.LeF: F(ref g, st.C) = F(ref g, st.A) <= F(ref g, st.B) ? 1f : 0f; break;
                case QcOp.GtF: F(ref g, st.C) = F(ref g, st.A) > F(ref g, st.B) ? 1f : 0f; break;
                case QcOp.LtF: F(ref g, st.C) = F(ref g, st.A) < F(ref g, st.B) ? 1f : 0f; break;
                case QcOp.AndF: F(ref g, st.C) = IsTrue(I(ref g, st.A)) && IsTrue(I(ref g, st.B)) ? 1f : 0f; break;
                case QcOp.OrF: F(ref g, st.C) = IsTrue(I(ref g, st.A)) || IsTrue(I(ref g, st.B)) ? 1f : 0f; break;

                case QcOp.NotF: F(ref g, st.C) = IsTrue(I(ref g, st.A)) ? 0f : 1f; break;
                case QcOp.NotV: F(ref g, st.C) = F(ref g, st.A) == 0f && F(ref g, st.A + 1) == 0f && F(ref g, st.A + 2) == 0f ? 1f : 0f; break;
                case QcOp.NotS: F(ref g, st.C) = I(ref g, st.A) == 0 || GetString(I(ref g, st.A)).Length == 0 ? 1f : 0f; break;
                case QcOp.NotFnc:
                case QcOp.NotEnt: F(ref g, st.C) = I(ref g, st.A) == 0 ? 1f : 0f; break;

                case QcOp.EqF: F(ref g, st.C) = F(ref g, st.A) == F(ref g, st.B) ? 1f : 0f; break;
                case QcOp.NeF: F(ref g, st.C) = F(ref g, st.A) != F(ref g, st.B) ? 1f : 0f; break;
                case QcOp.EqV:
                    F(ref g, st.C) = F(ref g, st.A) == F(ref g, st.B) && F(ref g, st.A + 1) == F(ref g, st.B + 1) && F(ref g, st.A + 2) == F(ref g, st.B + 2) ? 1f : 0f;
                    break;
                case QcOp.NeV:
                    F(ref g, st.C) = F(ref g, st.A) != F(ref g, st.B) || F(ref g, st.A + 1) != F(ref g, st.B + 1) || F(ref g, st.A + 2) != F(ref g, st.B + 2) ? 1f : 0f;
                    break;
                case QcOp.EqS: F(ref g, st.C) = StringsEqual(I(ref g, st.A), I(ref g, st.B)) ? 1f : 0f; break;
                case QcOp.NeS: F(ref g, st.C) = StringsEqual(I(ref g, st.A), I(ref g, st.B)) ? 0f : 1f; break;
                case QcOp.EqE:
                case QcOp.EqFnc: F(ref g, st.C) = I(ref g, st.A) == I(ref g, st.B) ? 1f : 0f; break;
                case QcOp.NeE:
                case QcOp.NeFnc: F(ref g, st.C) = I(ref g, st.A) != I(ref g, st.B) ? 1f : 0f; break;

                case QcOp.StoreF:
                case QcOp.StoreEnt:
                case QcOp.StoreFld:
                case QcOp.StoreFnc:
                case QcOp.StoreS:
                    I(ref g, st.B) = I(ref g, st.A);
                    break;
                case QcOp.StoreV:
                {
                    int x = I(ref g, st.A), y = I(ref g, st.A + 1), z = I(ref g, st.A + 2);
                    I(ref g, st.B) = x; I(ref g, st.B + 1) = y; I(ref g, st.B + 2) = z;
                    break;
                }

                case QcOp.StorepF:
                case QcOp.StorepEnt:
                case QcOp.StorepFld:
                case QcOp.StorepFnc:
                case QcOp.StorepS:
                    _currentStatement = pc;
                    Pointer(I(ref g, st.B) + I(ref g, st.C), 1) = I(ref g, st.A);
                    break;
                case QcOp.StorepV:
                {
                    _currentStatement = pc;
                    ref int target = ref Pointer(I(ref g, st.B) + I(ref g, st.C), 3);
                    int x = I(ref g, st.A), y = I(ref g, st.A + 1), z = I(ref g, st.A + 2);
                    target = x; Unsafe.Add(ref target, 1) = y; Unsafe.Add(ref target, 2) = z;
                    break;
                }

                case QcOp.Address:
                    _currentStatement = pc;
                    I(ref g, st.C) = _numGlobals + FieldIndex(I(ref g, st.A), I(ref g, st.B), 1);
                    break;

                case QcOp.LoadF:
                case QcOp.LoadFld:
                case QcOp.LoadEnt:
                case QcOp.LoadFnc:
                case QcOp.LoadS:
                    _currentStatement = pc;
                    I(ref g, st.C) = _fields[FieldIndex(I(ref g, st.A), I(ref g, st.B), 1)];
                    break;
                case QcOp.LoadV:
                {
                    _currentStatement = pc;
                    int at = FieldIndex(I(ref g, st.A), I(ref g, st.B), 3);
                    int[] fields = _fields;
                    int x = fields[at], y = fields[at + 1], z = fields[at + 2];
                    I(ref g, st.C) = x; I(ref g, st.C + 1) = y; I(ref g, st.C + 2) = z;
                    break;
                }

                case QcOp.IfNot:
                    if (!IsTrue(I(ref g, st.A))) { pc = Jump(pc, st.B); continue; }
                    break;
                case QcOp.If:
                    if (IsTrue(I(ref g, st.A))) { pc = Jump(pc, st.B); continue; }
                    break;
                case QcOp.Goto:
                    pc = Jump(pc, st.A);
                    continue;

                case QcOp.Call0:
                case QcOp.Call1:
                case QcOp.Call2:
                case QcOp.Call3:
                case QcOp.Call4:
                case QcOp.Call5:
                case QcOp.Call6:
                case QcOp.Call7:
                case QcOp.Call8:
                {
                    _currentStatement = pc;
                    int function = I(ref g, st.A);
                    if (function <= 0 || function >= _functions.Length)
                        throw new QcRuntimeException(function == 0 ? $"{Name}: NULL function" : $"{Name}: attempted CALL outside the program");
                    QcFunction target = _functions[function];
                    ArgCount = st.Op - (int)QcOp.Call0;
                    if (target.IsBuiltin)
                    {
                        CallBuiltin(target);
                        break;
                    }
                    pc = EnterFunction(function);
                    continue;
                }

                case QcOp.Done:
                case QcOp.Return:
                {
                    int x = I(ref g, st.A), y = I(ref g, st.A + 1), z = I(ref g, st.A + 2);
                    I(ref g, ProgsFile.OfsReturn) = x; I(ref g, ProgsFile.OfsReturn + 1) = y; I(ref g, ProgsFile.OfsReturn + 2) = z;
                    _currentStatement = pc;
                    pc = LeaveFunction();
                    if (_depth <= exitDepth) return;
                    break; // resume after the CALL statement
                }

                case QcOp.State:
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Jump(int pc, int offset)
    {
        if (++_jumpCount >= RunawayJumpLimit) Runaway(pc);
        return pc + offset;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Runaway(int pc)
    {
        _currentStatement = pc;
        throw new QcRuntimeException($"{Name}: runaway loop counter hit limit of {RunawayJumpLimit} jumps");
    }

    /// <summary>
    /// Resolves a pointer produced by OP_ADDRESS. The address space is the globals followed by the entity
    /// fields; the classic instruction set can only manufacture pointers into the second.
    /// </summary>
    private ref int Pointer(int address, int cells)
    {
        int field = address - _numGlobals;
        if (field >= 0 && (long)field + cells <= (long)_maxEdicts * _entityFields) return ref _fields[field];
        if (address >= 0 && (long)address + cells <= _numGlobals) return ref _globals[address];
        throw new QcRuntimeException($"{Name}: attempted to write to an out of bounds address {address}");
    }

    private bool StringsEqual(int a, int b) => a == b || string.Equals(GetString(a), GetString(b), StringComparison.Ordinal);

    private void State(float frame, int think)
    {
        if (_ofsSelf < 0)
        {
            _ofsSelf = FindGlobal("self")?.Offset ?? -1;
            _ofsTime = FindGlobal("time")?.Offset ?? -1;
            _fldNextThink = FindField("nextthink")?.Offset ?? -1;
            _fldFrame = FindField("frame")?.Offset ?? -1;
            _fldThink = FindField("think")?.Offset ?? -1;
            if (_ofsSelf < 0 || _ofsTime < 0 || _fldNextThink < 0 || _fldFrame < 0 || _fldThink < 0)
            {
                _ofsSelf = -1;
                throw new QcRuntimeException($"{Name}: OP_STATE not supported by this program");
            }
        }
        int self = GlobalInt(_ofsSelf);
        FieldFloat(self, _fldNextThink) = GlobalFloat(_ofsTime) + 0.1f;
        FieldFloat(self, _fldFrame) = frame;
        FieldInt(self, _fldThink) = think;
    }
}

/// <summary>Three consecutive float cells: a QuakeC vector, laid out exactly as the VM stores it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct QcVector
{
    public float X, Y, Z;
    public QcVector(float x, float y, float z) { X = x; Y = y; Z = z; }
    public override readonly string ToString() => FormattableString.Invariant($"'{X} {Y} {Z}'");
}

/// <summary>Where a program's time went: see <see cref="QcVm.Profile"/>. Times are Stopwatch ticks.</summary>
public sealed class QcProfile
{
    private long[] _ticks = new long[700];
    private long[] _calls = new long[700];

    /// <summary>Wall time of all outermost calls into the program.</summary>
    public long TotalTicks;
    /// <summary>Time inside builtins, excluding program code they called back into.</summary>
    public long BuiltinTicks;
    public long BuiltinCalls;

    /// <summary>Time in the interpreter itself: everything that was not a builtin.</summary>
    public long InterpreterTicks => TotalTicks - BuiltinTicks;

    internal void Record(int number, long ticks)
    {
        if (number >= _ticks.Length)
        {
            System.Array.Resize(ref _ticks, number + 64);
            System.Array.Resize(ref _calls, number + 64);
        }
        _ticks[number] += ticks;
        _calls[number]++;
        BuiltinTicks += ticks;
        BuiltinCalls++;
    }

    /// <summary>Builtins by time spent, largest first: (number, calls, ticks).</summary>
    public System.Collections.Generic.IEnumerable<(int Number, long Calls, long Ticks)> ByBuiltin()
    {
        var rows = new System.Collections.Generic.List<(int, long, long)>();
        for (int i = 0; i < _ticks.Length; i++)
            if (_calls[i] > 0) rows.Add((i, _calls[i], _ticks[i]));
        rows.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        return rows;
    }

    public static double ToMilliseconds(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
