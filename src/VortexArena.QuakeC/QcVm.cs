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
public sealed partial class QcVm
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

    // The watched-field index (WatchFields): which field cells are watched, and per entity one bit that
    // is set when a watched cell of it may be non-zero. Null / empty when nothing is watched.
    private bool[]? _watchedCells;
    private ulong[] _watchedEdicts = Array.Empty<ulong>();
    private int[] _watchedOffsets = Array.Empty<int>();

    // Mirrored fields (MirrorField): per field cell 0, or 1 + the index of the array that holds a copy
    // of that cell for every entity.
    private byte[]? _mirrorOf;
    private int[][] _mirrors = Array.Empty<int[]>();
    private int[] _mirrorOffsets = Array.Empty<int>();

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

        BuildFunctionInfo();
        BuildExecStream();
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
        if (_watchedCells is not null || _mirrorOf is not null) throw new InvalidOperationException("fields cannot be added once fields are watched or mirrored");
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

    public ref int FieldInt(int edict, int offset) => ref _fields[RefIndex(edict, offset, 1)];
    public ref float FieldFloat(int edict, int offset) => ref Unsafe.As<int, float>(ref _fields[RefIndex(edict, offset, 1)]);
    public ref QcVector FieldVector(int edict, int offset) => ref Unsafe.As<int, QcVector>(ref _fields[RefIndex(edict, offset, 3)]);

    // The three accessors above hand out a reference, and the VM cannot see whether the caller reads
    // through it or writes. So for a cell that is watched or mirrored it assumes a write: the entity is
    // marked in the watch index (which only ever errs towards "may be non-zero"), and a mirrored cell
    // is queued to be read back before the mirror is next consulted. Engine code therefore needs no
    // discipline to keep either true - the first version of this relied on callers reporting their
    // writes, and a caller that did not (a test setting .think directly) had its entity skipped.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RefIndex(int edict, int offset, int cells)
    {
        int index = FieldIndex(edict, offset, cells);
        if (_refRoles is { } roles) NoteRefAccess(roles, edict, offset, cells);
        return index;
    }

    private void NoteRefAccess(byte[] roles, int edict, int offset, int cells)
    {
        for (int c = offset; c < offset + cells; c++)
        {
            int role = roles[c];
            if (role == 0) continue;
            if ((role & 1) != 0) _watchedEdicts[edict >> 6] |= 1UL << edict;
            if ((role & 2) != 0)
            {
                if (_staleCount == _stale.Length) Array.Resize(ref _stale, Math.Max(64, _stale.Length * 2));
                _stale[_staleCount++] = (edict, c);
            }
        }
    }

    // 1: watched, 2: mirrored - per field cell, for the reference accessors. Null while neither exists.
    private byte[]? _refRoles;
    private (int Edict, int Cell)[] _stale = Array.Empty<(int, int)>();
    private int _staleCount;

    private void BuildRefRoles()
    {
        byte[] roles = new byte[_entityFields];
        if (_watchedCells is { } watched) for (int c = 0; c < roles.Length; c++) if (watched[c]) roles[c] |= 1;
        if (_mirrorOf is { } mirrorOf) for (int c = 0; c < roles.Length; c++) if (mirrorOf[c] != 0) roles[c] |= 2;
        _refRoles = roles;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SyncMirrors()
    {
        byte[] mirrorOf = _mirrorOf!;
        for (int i = 0; i < _staleCount; i++)
        {
            (int edict, int cell) = _stale[i];
            if ((uint)edict < (uint)_maxEdicts) _mirrors[mirrorOf[cell] - 1][edict] = _fields[edict * _entityFields + cell];
        }
        _staleCount = 0;
    }

    private int FieldIndex(int edict, int offset, int cells)
    {
        if ((uint)edict >= (uint)_maxEdicts) throw new QcRuntimeException($"entity {edict} is out of range");
        if ((uint)offset > (uint)(_entityFields - cells)) throw new QcRuntimeException($"field offset {offset} is out of range");
#if QC_OPSTATS
        TouchField(edict * _entityFields + offset);
#endif
        TouchFieldLine(edict * _entityFields + offset);
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

    /// <summary>The raw cell at <paramref name="offset"/> of <paramref name="edict"/>, read only: for code that
    /// walks every field of every entity (a dump, a checksum) and must not look like the program using them.</summary>
    public int PeekField(int edict, int offset)
    {
        if ((uint)edict >= (uint)_maxEdicts || (uint)offset >= (uint)_entityFields) throw new QcRuntimeException($"entity {edict} field {offset} is out of range");
        return _fields[edict * _entityFields + offset];
    }

    /// <summary>Copies every field of <paramref name="from"/> to <paramref name="to"/> (the copyentity builtin).</summary>
    public void CopyEdict(int from, int to)
    {
        if ((uint)from >= (uint)_maxEdicts || (uint)to >= (uint)_maxEdicts) throw new QcRuntimeException($"entity {((uint)from >= (uint)_maxEdicts ? from : to)} is out of range");
        Array.Copy(_fields, from * _entityFields, _fields, to * _entityFields, _entityFields);
        foreach (int[] column in _mirrors) column[to] = column[from];
        if (_watchedCells is not null && (_watchedEdicts[from >> 6] & (1UL << from)) != 0) _watchedEdicts[to >> 6] |= 1UL << to;
    }

    public void ClearEdict(int edict)
    {
        Array.Clear(_fields, edict * _entityFields, _entityFields);
        if (_watchedCells is not null) _watchedEdicts[edict >> 6] &= ~(1UL << edict);
        foreach (int[] column in _mirrors) column[edict] = 0;
    }

    // ---- mirrored fields ------------------------------------------------------------------------------

    /// <summary>
    /// Keeps a copy of one field cell of every entity in an array of its own, and returns the number to
    /// read it by (<see cref="Mirrored"/>). For an engine loop that needs one field of many entities -
    /// a trace asking each entity near its path whether it is solid: the field itself lies in the
    /// entity's ten kilobytes, a cache miss for each entity asked, where the copies of all of them fit
    /// in a few kilobytes.
    ///
    /// The copy is exact, not a hint. It is updated by every write the program makes (the STOREP
    /// instructions), when an entity is cleared or copied, and after any use of <see cref="FieldInt"/>,
    /// <see cref="FieldFloat"/> or <see cref="FieldVector"/> on the cell (the field is read back before
    /// the mirror is next consulted, so a reference must not be kept and written later).
    /// <see cref="VerifyMirrors"/> checks it.
    /// </summary>
    public int MirrorField(int cellOffset)
    {
        if ((uint)cellOffset >= (uint)_entityFields) throw new ArgumentOutOfRangeException(nameof(cellOffset));
        byte[] of = _mirrorOf ??= new byte[_entityFields];
        if (of[cellOffset] != 0) return of[cellOffset] - 1;
        if (_mirrors.Length >= 250) throw new InvalidOperationException("too many mirrored fields");
        int[] column = new int[_maxEdicts];
        for (int e = 0; e < _numEdicts; e++) column[e] = _fields[e * _entityFields + cellOffset];
        int id = _mirrors.Length;
        Array.Resize(ref _mirrors, id + 1);
        Array.Resize(ref _mirrorOffsets, id + 1);
        _mirrors[id] = column;
        _mirrorOffsets[id] = cellOffset;
        of[cellOffset] = (byte)(id + 1);
        BuildRefRoles();
        return id;
    }

    /// <summary>The mirrored cell <paramref name="mirror"/> (from <see cref="MirrorField"/>) of <paramref name="edict"/>.</summary>
    public int Mirrored(int mirror, int edict)
    {
        if (_staleCount != 0) SyncMirrors();
        return _mirrors[mirror][edict];
    }
    public float MirroredFloat(int mirror, int edict) => BitConverter.Int32BitsToSingle(Mirrored(mirror, edict));

    /// <summary>Compares every mirrored cell with the field it copies; the first difference, or null.</summary>
    public string? VerifyMirrors()
    {
        if (_staleCount != 0) SyncMirrors();
        for (int m = 0; m < _mirrors.Length; m++)
            for (int e = 0; e < _numEdicts; e++)
                if (_mirrors[m][e] != _fields[e * _entityFields + _mirrorOffsets[m]])
                    return $"mirror of field cell {_mirrorOffsets[m]}: entity {e} has {_fields[e * _entityFields + _mirrorOffsets[m]]:X8}, the copy {_mirrors[m][e]:X8}";
        return null;
    }

    // A write through a pointer to field cell `field` (validated), for the mirrors.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteMirrorWrite(byte[] mirrorOf, int field, int value)
    {
        if (field < 0) return;
        int edict = (int)((uint)field / (uint)_entityFields);
        int mirror = mirrorOf[field - edict * _entityFields];
        if (mirror != 0) _mirrors[mirror - 1][edict] = value;
    }

    // ---- the watched-field index --------------------------------------------------------------------

    /// <summary>
    /// Asks the VM to keep, for every entity, whether any of the given field cells may be non-zero.
    /// An engine loop that visits every entity only to find that almost none of them has, say, a think
    /// function or a draw mask can then visit just the ones that might (<see cref="NextWatched"/>):
    /// with ten kilobytes of fields per entity and thousands of entities, reading one field of each
    /// is thousands of cache misses a frame.
    ///
    /// The bit is conservative: set by any non-zero write the program makes to a watched cell (the
    /// STOREP instructions, OP_STATE) and by <see cref="NoteFieldWrite"/> for writes the engine makes;
    /// cleared when the entity's fields are cleared (allocation, freeing) and by
    /// <see cref="ClearWatched"/>, which a reader calls after finding every watched cell zero. An entity
    /// whose bit is clear has all watched cells zero; one whose bit is set may or may not.
    ///
    /// Engine code needs to do nothing: taking a reference to a watched cell through
    /// <see cref="FieldInt"/>, <see cref="FieldFloat"/> or <see cref="FieldVector"/> marks the entity,
    /// read or write. Code that only reads, and walks many entities, uses <see cref="PeekField"/>.
    /// </summary>
    public void WatchFields(ReadOnlySpan<int> cellOffsets)
    {
        bool[] watched = new bool[_entityFields];
        foreach (int offset in cellOffsets)
            if ((uint)offset < (uint)_entityFields) watched[offset] = true;
        List<int> offsets = new();
        for (int c = 0; c < watched.Length; c++) if (watched[c]) offsets.Add(c);
        _watchedOffsets = offsets.ToArray();
        _watchedEdicts = new ulong[(_maxEdicts + 63) >> 6];
        _watchedCells = watched;
        BuildRefRoles();
        // Whatever exists already is examined once.
        for (int e = 0; e < _numEdicts; e++)
        {
            if (_edictFree[e]) continue;
            int row = e * _entityFields;
            for (int c = 0; c < watched.Length; c++)
                if (watched[c] && _fields[row + c] != 0) { _watchedEdicts[e >> 6] |= 1UL << e; break; }
        }
    }

    /// <summary>True when <see cref="WatchFields"/> has been called.</summary>
    public bool HasWatchedFields => _watchedCells is not null;

    /// <summary>Whether a watched cell of <paramref name="edict"/> may be non-zero.</summary>
    public bool IsWatched(int edict) => (uint)edict < (uint)_maxEdicts && _watchedCells is not null && (_watchedEdicts[edict >> 6] & (1UL << edict)) != 0;

    /// <summary>The lowest entity number at or above <paramref name="from"/> whose bit is set and that is
    /// below <see cref="NumEdicts"/>, or -1. Reads the index as it is now, so an entity marked while a
    /// loop is running is found when the loop reaches its number.</summary>
    public int NextWatched(int from)
    {
        if (from < 0) from = 0;
        ulong[] bits = _watchedEdicts;
        int limit = _numEdicts;
        int word = from >> 6;
        if (from >= limit || word >= bits.Length) return -1;
        ulong w = bits[word] & (~0UL << from);
        while (true)
        {
            if (w != 0)
            {
                int edict = (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(w);
                return edict < limit ? edict : -1;
            }
            if (++word >= bits.Length || (word << 6) >= limit) return -1;
            w = bits[word];
        }
    }

    /// <summary>Drops <paramref name="edict"/> from the index if every watched cell of it is zero (it is
    /// checked here, so a caller cannot break the index's promise). Returns whether it was dropped.</summary>
    public bool ClearWatched(int edict)
    {
        bool[]? watched = _watchedCells;
        if (watched is null || (uint)edict >= (uint)_maxEdicts) return false;
        int row = edict * _entityFields;
        foreach (int offset in _watchedOffsets)
            if (_fields[row + offset] != 0) return false;
        _watchedEdicts[edict >> 6] &= ~(1UL << edict);
        return true;
    }

    /// <summary>The engine wrote <paramref name="cells"/> field cells of <paramref name="edict"/> starting at <paramref name="offset"/>.</summary>
    public void NoteFieldWrite(int edict, int offset, int cells = 1)
    {
        if ((uint)edict >= (uint)_maxEdicts) return;
        if (_mirrorOf is { } mirrorOf)
        {
            for (int c = Math.Max(offset, 0); c < offset + cells && c < mirrorOf.Length; c++)
                if (mirrorOf[c] != 0) _mirrors[mirrorOf[c] - 1][edict] = _fields[edict * _entityFields + c];
        }
        bool[]? watched = _watchedCells;
        if (watched is null) return;
        for (int c = offset; c < offset + cells; c++)
            if ((uint)c < (uint)watched.Length && watched[c]) { _watchedEdicts[edict >> 6] |= 1UL << edict; return; }
    }

    // A write through a pointer: `field` is the index into the entity fields, already validated.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotePointerWrite(bool[] watched, int field)
    {
        if (field < 0) return;
        int edict = (int)((uint)field / (uint)_entityFields);
        if (watched[field - edict * _entityFields]) _watchedEdicts[edict >> 6] |= 1UL << edict;
    }

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
        if (_watchedCells is not null) Array.Resize(ref _watchedEdicts, (newMax + 63) >> 6);
        for (int m = 0; m < _mirrors.Length; m++) Array.Resize(ref _mirrors[m], newMax);
        if (_fieldLines is not null) Array.Resize(ref _fieldLines, FieldLineWords(newMax));
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

    // Short strings a builtin has produced before, by content. A frame of a client program makes
    // thousands of temporary strings (substring, strcat and ftos calls in the HUD's text code) and
    // almost all of them are the strings it made the frame before; handing back the same object costs
    // a hash and a compare where a new string costs an allocation, and a steady frame then allocates
    // none. Direct-mapped and never grown: a slot is simply overwritten.
    private string?[]? _stringCache;
    private const int StringCacheSlots = 4096, StringCacheMaxLength = 96;

    /// <summary>
    /// A string with the given text: the one made for the same text last time, if it is still in the
    /// cache, else a new one. For builtins that build short strings; the result is an ordinary string.
    /// </summary>
    public string CachedString(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return "";
        if (text.Length > StringCacheMaxLength) return new string(text);
        string?[] cache = _stringCache ??= new string?[StringCacheSlots];
        int slot = string.GetHashCode(text) & (StringCacheSlots - 1);
        string? cached = cache[slot];
        if (cached is not null && cached.AsSpan().SequenceEqual(text)) return cached;
        string made = new(text);
        cache[slot] = made;
        return made;
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
#if QC_OPSTATS
        ExecuteCalls++;
#endif
        long profileStart = Profile is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        if (_executeDepth++ == 0) _jumpCount = 0;
        try
        {
            ArgCount = argCount;
            if (_functionInfo[function].FirstStatement < 0)
            {
                CallBuiltin(function);
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

    private void CallBuiltin(int functionIndex)
    {
        int number = -_functionInfo[functionIndex].FirstStatement;
        QcBuiltin? builtin = number < _builtins.Length ? _builtins[number] : null;
#if QC_OPSTATS
        BuiltinCallsCounted++;
#endif
        if (builtin is not null)
        {
            if (Profile is null) builtin(this);
            else CallBuiltinProfiled(builtin, number, Profile);
            return;
        }
        if (UnknownBuiltin is null)
            throw new QcRuntimeException($"{Name}: no such builtin #{number} ({_functions[functionIndex].Name})");
        _globals[ProgsFile.OfsReturn] = 0;
        _globals[ProgsFile.OfsReturn + 1] = 0;
        _globals[ProgsFile.OfsReturn + 2] = 0;
        UnknownBuiltin(this, number, _functions[functionIndex].Name);
    }

    /// <summary>
    /// When set, every builtin call is timed (exclusive of any program code it re-enters) and every
    /// outermost <see cref="Execute"/> is timed, so the cost of a frame can be split into "inside the
    /// interpreter" and "inside the engine". Off by default; the check is one null test per builtin call.
    /// </summary>
    public QcProfile? Profile { get; set; }

    private long _nestedExecuteTicks;

#if QC_OPSTATS
    // Measuring builds only (msbuild -p:QcOpStats=1): how often each instruction, each pair of
    // consecutive instructions and each function's statements ran. Compiled out otherwise.
    public readonly long[] OpCounts = new long[128];
    public readonly long[] OpPairCounts = new long[128 * 128];
    public long[] FunctionStatements => _functionStatements ??= new long[_functions.Length];
    private long[]? _functionStatements;
    private int _previousOp;
    public long ExecuteCalls, EnterCalls, LocalsCopied, BuiltinCallsCounted;
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CallBuiltinProfiled(QcBuiltin builtin, int number, QcProfile profile)
    {
        long outerNested = _nestedExecuteTicks;
        _nestedExecuteTicks = 0;
        long allocated = profile.MeasureAllocation ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { builtin(this); }
        finally
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            profile.Record(number, elapsed - _nestedExecuteTicks);
            // Inclusive of whatever program code the builtin called back into (addentities runs every
            // entity's draw function): read it as "under this builtin", not "by this builtin".
            if (profile.MeasureAllocation) profile.RecordAllocation(number, GC.GetAllocatedBytesForCurrentThread() - allocated);
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
        if (field >= 0 && (long)field + cells <= (long)_maxEdicts * _entityFields)
        {
            TouchFieldLine(field);
            return ref _fields[field];
        }
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
        NoteFieldWrite(self, _fldNextThink);
        NoteFieldWrite(self, _fldFrame);
        NoteFieldWrite(self, _fldThink);
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
    private long[] _allocated = new long[700];

    /// <summary>Also record the managed bytes allocated under each builtin (two more clock-like reads per call).</summary>
    public bool MeasureAllocation { get; set; }

    internal void RecordAllocation(int number, long bytes)
    {
        if (number >= _allocated.Length) System.Array.Resize(ref _allocated, number + 64);
        _allocated[number] += bytes;
    }

    /// <summary>Managed bytes allocated under builtin <paramref name="number"/> (see <see cref="MeasureAllocation"/>).</summary>
    public long AllocatedBytes(int number) => (uint)number < (uint)_allocated.Length ? _allocated[number] : 0;

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
