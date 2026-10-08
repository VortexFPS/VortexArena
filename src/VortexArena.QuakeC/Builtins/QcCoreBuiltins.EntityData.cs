// Port of Base/darkplaces/prvm_cmds.c (VM_loadfromdata, VM_loadfromfile, VM_parseentitydata,
// VM_writetofile), prvm_edict.c (PRVM_ED_ParseEdict, PRVM_ED_CallPrespawnFunction,
// PRVM_ED_CallSpawnFunction, PRVM_ED_CallPostspawnFunction, PRVM_ED_LoadFromFile, PRVM_ED_Write) and
// common.c COM_ParseToken_Simple.
using System.Text;

namespace VortexArena.QuakeC;

public sealed partial class QcCoreBuiltins
{
    /// <summary>Largest entity file loadfromfile reads. Map entity lumps are a few hundred kilobytes.</summary>
    public int MaxEntityFileBytes { get; set; } = 16 << 20;

    /// <summary>
    /// COM_ParseToken_Simple without newline tokens and with comments: the next whitespace-delimited or
    /// double-quoted token. False at the end of the text, after which <paramref name="pos"/> is -1.
    /// </summary>
    private bool ParseToken(string data, ref int pos, bool parseBackslash, out string token)
    {
        token = "";
        if (pos < 0) return false;
        while (true)
        {
            while (pos < data.Length && IsWhitespace(data[pos])) pos++;
            if (pos >= data.Length)
            {
                pos = -1;
                return false;
            }
            if (data[pos] != '/' || pos + 1 >= data.Length) break;
            if (data[pos + 1] == '/')
            {
                while (pos < data.Length && data[pos] != '\n' && data[pos] != '\r') pos++;
            }
            else if (data[pos + 1] == '*')
            {
                pos++;
                while (pos < data.Length && (data[pos] != '*' || pos + 1 >= data.Length || data[pos + 1] != '/')) pos++;
                pos = Math.Min(pos + 2, data.Length);
            }
            else break;
        }

        int room = _vm.MaxStringLength - 1; // sizeof(com_token) - 1; the excess is read and dropped
        StringBuilder text = new();
        if (data[pos] == '"')
        {
            for (pos++; pos < data.Length && data[pos] != '"'; pos++)
            {
                char c = data[pos];
                if (c == '\\' && parseBackslash)
                {
                    if (++pos >= data.Length) break;
                    c = data[pos] switch { 'n' => '\n', 't' => '\t', _ => data[pos] };
                }
                if (text.Length < room) text.Append(c);
            }
            if (pos < data.Length) pos++;
        }
        else
        {
            for (; pos < data.Length && !IsWhitespace(data[pos]); pos++)
                if (text.Length < room) text.Append(data[pos]);
        }
        token = text.ToString();
        return true;
    }

    /// <summary>
    /// PRVM_ED_ParseEdict: reads "key" "value" pairs up to the closing brace into an entity. An entity
    /// given no pairs at all is marked free.
    /// </summary>
    private void ParseEdict(string data, ref int pos, int edict, bool saveLoad)
    {
        bool init = false;
        // Not reset per key in the C either: once one key of an entity is on the no-escapes list, every
        // later value of that entity is read raw.
        bool parseBackslash = true;
        string[]? noEscapes = null;

        while (true)
        {
            if (!ParseToken(data, ref pos, false, out string key)) throw Fault("PRVM_ED_ParseEdict: EOF without closing brace");
            if (key.StartsWith('}')) break;

            // QuakeEd wrote a single yaw as "angle"; it becomes the vector "0 yaw 0".
            bool angleHack = key == "angle";
            if (angleHack) key = "angles";
            if (key == "light") key = "light_lev";
            if (key.Length > 255) key = key[..255]; // char keyname[256]
            key = key.TrimEnd(' ');

            if (!saveLoad)
            {
                noEscapes ??= _host.CvarString("sv_entfields_noescapes").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (Array.IndexOf(noEscapes, key) >= 0) parseBackslash = false;
            }

            if (!ParseToken(data, ref pos, parseBackslash, out string value)) throw Fault("PRVM_ED_ParseEdict: EOF without closing brace");
            if (value.StartsWith('}')) throw Fault("PRVM_ED_ParseEdict: closing brace without data");
            init = true;

            // "" is a key some maps really contain; a leading underscore marks a compiler-only key.
            if (key.Length == 0 || key[0] == '_') continue;
            QcDef? def = _vm.FindField(key);
            if (def is null)
            {
                if (_host.Developer) _host.Print($"{_vm.Name}: '{key}' is not a field\n");
                continue;
            }
            if (angleHack) value = $"0 {(value.Length > 31 ? value[..31] : value)} 0";
            if (!ParseEpair(edict, def, value)) throw Fault("PRVM_ED_ParseEdict: parse error");
        }

        if (!init && edict > 0 && edict < _vm.NumEdicts) _vm.FreeEdict(edict, _host.RealTime);
    }

    // #613 void(entity ent, string data) parseentitydata
    private void ParseEntityData(QcVm vm)
    {
        Parms(2, "VM_parseentitydata");
        int edict = vm.ArgEdict(0);
        if (vm.IsFree(edict)) throw Fault($"VM_parseentitydata: Can only set already spawned entities (entity {edict} is free)!");
        string data = vm.ArgString(1);
        int pos = 0;
        if (!ParseToken(data, ref pos, false, out string brace) || !brace.StartsWith('{'))
            throw Fault($"VM_parseentitydata: Couldn't parse entity data:\n{data}\n{vm.StackTrace()}");
        ParseEdict(data, ref pos, edict, saveLoad: true);
    }

    // #529 void(string data) loadfromdata
    private void LoadFromData(QcVm vm)
    {
        Parms(1, "VM_loadfromdata");
        LoadEntities(vm.ArgString(0));
    }

    // #530 float(string file) loadfromfile. Read from the game's files directly, not from data/.
    private void LoadFromFile(QcVm vm)
    {
        Parms(1, "VM_loadfromfile");
        string filename = vm.ArgString(0);
        if (IsNastyPath(filename))
        {
            vm.ReturnFloat(-4);
            Warning($"VM_loadfromfile: dangerous or non-portable filename \"{filename}\" not allowed. (contains : or \\ or begins with .. or /)\n");
            return;
        }

        string? data = null;
        using (Stream? stream = OpenRead(filename))
        {
            if (stream is not null)
            {
                try
                {
                    using MemoryStream bytes = new();
                    byte[] chunk = new byte[65536];
                    int read;
                    while (bytes.Length <= MaxEntityFileBytes && (read = stream.Read(chunk, 0, chunk.Length)) > 0) bytes.Write(chunk, 0, read);
                    if (bytes.Length <= MaxEntityFileBytes) data = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
                    else Warning($"VM_loadfromfile: \"{filename}\" is larger than {MaxEntityFileBytes} bytes\n");
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { }
            }
        }
        if (data is null) vm.ReturnFloat(-1);
        else LoadEntities(data);
    }

    /// <summary>
    /// PRVM_ED_LoadFromFile: spawns one entity per brace block and runs its spawn function
    /// (spawnfunc_CLASSNAME, or CLASSNAME unless the program sets require_spawnfunc_prefix).
    /// </summary>
    private void LoadEntities(string data)
    {
        QcDef? self = _vm.FindGlobal("self"), classname = _vm.FindField("classname");
        int preSpawn = _vm.FindFunction("SV_OnEntityPreSpawnFunction"), postSpawn = _vm.FindFunction("SV_OnEntityPostSpawnFunction");
        int noSpawn = _vm.FindFunction("SV_OnEntityNoSpawnFunction");
        QcDef? requirePrefix = _vm.FindGlobal("require_spawnfunc_prefix");
        int pos = 0, parsed = 0, spawned = 0;

        // While loading, a slot freed by one entity's spawn function is given straight to the next.
        _alwaysAllowReuse = true;
        try
        {
            while (ParseToken(data, ref pos, false, out string brace))
            {
                if (!brace.StartsWith('{')) throw Fault($"PRVM_ED_LoadFromFile: found {brace} when expecting {{");
                int edict = AllocEdict();
                ParseEdict(data, ref pos, edict, saveLoad: false);
                parsed++;
                if (_vm.IsFree(edict)) continue;

                if (preSpawn != 0) Call(preSpawn, edict);
                if (_vm.IsFree(edict)) continue;
                EdictLinked?.Invoke(edict);

                string name = classname is null ? "" : _vm.GetString(_vm.FieldInt(edict, classname.Offset));
                if (classname is null || _vm.FieldInt(edict, classname.Offset) == 0)
                {
                    _host.Print("No classname for:\n");
                    PrintEdict(edict);
                    FreeEdict(edict);
                    continue;
                }
                int function = _vm.FindFunction("spawnfunc_" + name);
                if (function == 0 && (requirePrefix is null || _vm.GlobalFloat(requirePrefix.Offset) == 0) && name.Length > 0) function = _vm.FindFunction(name);
                if (function == 0) function = noSpawn;
                if (function == 0)
                {
                    if (_host.Developer)
                    {
                        _host.Print("No spawn function for:\n");
                        PrintEdict(edict);
                    }
                    FreeEdict(edict);
                    continue;
                }
                Call(function, edict);
                if (postSpawn != 0 && !_vm.IsFree(edict)) Call(postSpawn, edict);
                spawned++;
            }
        }
        finally
        {
            _alwaysAllowReuse = false;
        }
        if (_host.Developer) _host.Print($"{_vm.Name}: {parsed} new entities parsed, {spawned} spawned ({_vm.NumEdicts} entities in use)\n");

        void Call(int function, int edict)
        {
            if (self is null) throw Fault("PRVM_ED_LoadFromFile: program has no self global");
            _vm.GlobalInt(self.Offset) = edict;
            _vm.Execute(function);
        }
    }

    /// <summary>
    /// PRVM_ED_LoadFromFile as the server runs it on a map's entity lump (SV_SpawnServer), which
    /// differs from the loadfromdata builtin in four ways: the first block is parsed into the world
    /// entity instead of a new one (prog->loadintoworld); the engine may drop an entity once its
    /// fields are known (prog->load_edict - SVVM_load_edict removes by spawnflags);
    /// <paramref name="beforeCall"/> runs before each call into the program (the C sets the time
    /// global there); and a program with a <c>__fullspawndata</c> global gets each entity's own text
    /// in it, newlines turned to tabs, before its spawn function runs.
    /// </summary>
    /// <returns>Entities parsed, inhibited (dropped before their spawn function) and spawned.</returns>
    public (int Parsed, int Inhibited, int Spawned) LoadMapEntities(string data, bool loadIntoWorld, Func<int, bool>? keep = null, Action? beforeCall = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        QcDef? self = _vm.FindGlobal("self"), classname = _vm.FindField("classname");
        int preSpawn = _vm.FindFunction("SV_OnEntityPreSpawnFunction"), postSpawn = _vm.FindFunction("SV_OnEntityPostSpawnFunction");
        int noSpawn = _vm.FindFunction("SV_OnEntityNoSpawnFunction");
        QcDef? requirePrefix = _vm.FindGlobal("require_spawnfunc_prefix"), fullData = _vm.FindGlobal("__fullspawndata");
        int pos = 0, parsed = 0, inhibited = 0, spawned = 0;

        // "prvm_reuseedicts_always_allow = host.realtime": it outlives this call (see the property).
        ReuseAlwaysAllowTime = _host.RealTime;
        _alwaysAllowReuse = true;
        try
        {
            while (true)
            {
                int start = pos;
                if (!ParseToken(data, ref pos, false, out string brace)) break;
                if (!brace.StartsWith('{')) throw Fault($"PRVM_ED_LoadFromFile: {_vm.Name}: found {brace} when expecting {{");
                int edict;
                if (loadIntoWorld)
                {
                    // "CHANGED: this is not conform to PR_LoadFromFile": the world is not cleared - the
                    // engine has already written its model and bounds into it.
                    loadIntoWorld = false;
                    edict = 0;
                }
                else edict = AllocEdict();
                ParseEdict(data, ref pos, edict, saveLoad: false);
                parsed++;
                if (edict != 0 && _vm.IsFree(edict)) continue;

                if (keep is not null && !keep(edict))
                {
                    FreeEdict(edict);
                    inhibited++;
                    continue;
                }
                if (preSpawn != 0) Call(preSpawn, edict);
                if (_vm.IsFree(edict))
                {
                    inhibited++;
                    continue;
                }
                EdictLinked?.Invoke(edict);

                if (classname is null || _vm.FieldInt(edict, classname.Offset) == 0)
                {
                    _host.Print("No classname for:\n");
                    PrintEdict(edict);
                    FreeEdict(edict);
                    continue;
                }
                if (fullData is not null && pos >= 0)
                {
                    // PRVM_AllocString: a zoned string per entity, which the program owns from here.
                    string text = data[Math.Min(start, pos)..pos].Replace('\n', '\t');
                    _vm.GlobalInt(fullData.Offset) = _vm.AllocString(text);
                }
                string name = _vm.GetString(_vm.FieldInt(edict, classname.Offset));
                int function = _vm.FindFunction("spawnfunc_" + name);
                if (function == 0 && (requirePrefix is null || _vm.GlobalFloat(requirePrefix.Offset) == 0) && name.Length > 0) function = _vm.FindFunction(name);
                if (function == 0) function = noSpawn;
                if (function == 0)
                {
                    if (_host.Developer)
                    {
                        _host.Print("No spawn function for:\n");
                        PrintEdict(edict);
                    }
                    FreeEdict(edict);
                    continue;
                }
                Call(function, edict);
                if (postSpawn != 0 && !_vm.IsFree(edict)) Call(postSpawn, edict);
                spawned++;
            }
        }
        finally
        {
            _alwaysAllowReuse = false;
        }
        return (parsed, inhibited, spawned);

        void Call(int function, int edict)
        {
            if (self is null) throw Fault("PRVM_ED_LoadFromFile: program has no self global");
            beforeCall?.Invoke();
            _vm.GlobalInt(self.Offset) = edict;
            _vm.Execute(function);
        }
    }

    // #606 void(float fhandle, entity ent) writetofile
    private void WriteToFile(QcVm vm)
    {
        Parms(2, "VM_writetofile");
        OpenFile? file = FileArg(0, "VM_writetofile");
        if (file is null)
        {
            Warning("VM_writetofile: invalid or closed file handle\n");
            return;
        }
        int edict = vm.ArgEdict(1);
        if (vm.IsFree(edict))
        {
            Warning($"VM_writetofile: entity {edict} is free!\n");
            return;
        }

        // PRVM_ED_Write: one "name" "value" line per non-zero field, in the form ParseEdict reads back.
        StringBuilder text = new("{\n");
        QcDef[] defs = FieldDefs();
        for (int i = 1; i < defs.Length; i++)
        {
            QcDef def = defs[i];
            // Not only the _x/_y/_z halves of vectors: every name ending in "_?" is left out of saves.
            if ((def.Name.Length > 1 && def.Name[^2] == '_') || !DefIsUsable(def) || FieldIsZero(edict, def)) continue;
            text.Append('"').Append(def.Name).Append("\" \"").Append(UglyValueString(def.Type, edict, def.Offset)).Append("\"\n");
            if (text.Length < 65536) continue;
            if (!WriteText(file, text.ToString(), "VM_writetofile")) return;
            text.Clear();
        }
        text.Append("}\n");
        WriteText(file, text.ToString(), "VM_writetofile");
    }
}
