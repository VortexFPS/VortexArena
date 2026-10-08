// Port of Base/darkplaces/prvm_cmds.c (VM_remove, VM_find, VM_findfloat, VM_findchain, VM_findchainfloat,
// VM_findflags, VM_findchainflags, VM_nextent, VM_wasfreed, VM_ftoe, VM_etof, VM_numentityfields,
// VM_entityfieldname, VM_entityfieldtype, VM_getentityfieldstring, VM_putentityfieldstring, VM_eprint,
// VM_callfunction, VM_isfunction), clvm_cmds.c (VM_CL_spawn, VM_CL_copyentity), prvm_edict.c
// (PRVM_ED_CanAlloc, PRVM_ED_Alloc, PRVM_ED_Free, PRVM_ValueString, PRVM_UglyValueString,
// PRVM_ED_ParseEpair, PRVM_ED_Print) and the edict policy csprogs.c CL_VM_Init sets up.
using System.Text;

namespace VortexArena.QuakeC;

public sealed partial class QcCoreBuiltins
{
    /// <summary>Entities after the world that spawn() never hands out and remove() refuses. 0 for the client program.</summary>
    public int ReservedEdicts { get; set; }

    /// <summary>DarkPlaces' <c>prvm_reuseedicts_neverinsameframe</c>: a slot freed within the last 0.1 s is not reused.</summary>
    public bool ReuseNeverInSameFrame { get; set; } = true;

    /// <summary>DarkPlaces' <c>prvm_reuseedicts_startuptime</c>: slots freed this soon after load are reusable at once.</summary>
    public double ReuseStartupTime { get; set; } = 2;

    /// <summary>When the program was loaded, on the <see cref="IQcHost.RealTime"/> clock. Defaults to construction time.</summary>
    public double StartTime { get; set; }

    /// <summary>Called after an entity is allocated (csprogs.c CLVM_init_edict): the host resets its render state.</summary>
    public Action<int>? EdictSpawned { get; set; }

    /// <summary>Called before an entity is freed, fields still intact (CLVM_free_edict): the host unlinks it.</summary>
    public Action<int>? EdictFreeing { get; set; }

    /// <summary>Called when an entity's fields were replaced wholesale and its world link is stale (CL_LinkEdict).</summary>
    public Action<int>? EdictLinked { get; set; }

    private bool _alwaysAllowReuse;

    /// <summary>
    /// DarkPlaces' <c>prvm_reuseedicts_always_allow</c>: PRVM_ED_LoadFromFile stores host.realtime in
    /// it, and PRVM_ED_CanAlloc lets any freed slot be reused for as long as host.realtime still has
    /// that value - which on a server is the whole of SV_SpawnServer, settling frames included,
    /// because the host clock only moves between host frames. NaN (the default) for "never".
    /// <see cref="LoadMapEntities"/> sets it.
    /// </summary>
    public double ReuseAlwaysAllowTime { get; set; } = double.NaN;

    /// <summary>
    /// PRVM_ED_Alloc. A freed slot is not handed out again for a second, so that references to the old
    /// entity (and the engine's interpolation of it) have died - except during the first seconds after
    /// load, when programs spawn and remove in bulk.
    /// </summary>
    public int AllocEdict()
    {
        double now = _host.RealTime;
        int edict = _vm.AllocEdict(ReservedEdicts + 1, (_, freeTime) =>
        {
            if (_alwaysAllowReuse || now == ReuseAlwaysAllowTime) return true;
            if (now <= freeTime + 0.1 && ReuseNeverInSameFrame) return false;
            if (freeTime < StartTime + ReuseStartupTime) return true;
            return now > freeTime + 1;
        });
        EdictSpawned?.Invoke(edict);
        return edict;
    }

    /// <summary>
    /// The end of a server frame (sv_phys.c SV_Physics): lowers the VM's entity count past trailing
    /// free entities that <see cref="AllocEdict"/> would hand out again (PRVM_ED_CanAlloc), never
    /// below <paramref name="minimum"/>.
    /// </summary>
    public void TrimEdicts(int minimum)
    {
        double now = _host.RealTime;
        _vm.TrimEdicts(minimum, (_, freeTime) =>
        {
            if (now == ReuseAlwaysAllowTime) return true;
            if (now <= freeTime + 0.1 && ReuseNeverInSameFrame) return false;
            if (freeTime < StartTime + ReuseStartupTime) return true;
            return now > freeTime + 1;
        });
    }

    /// <summary>PRVM_ED_Free: silently refuses the world and reserved entities.</summary>
    public void FreeEdict(int edict)
    {
        if (edict <= ReservedEdicts || edict >= _vm.NumEdicts) return;
        EdictFreeing?.Invoke(edict);
        _vm.FreeEdict(edict, _host.RealTime);
    }

    // #14 entity() spawn. The client table's version takes any argument count.
    private void Spawn(QcVm vm) => vm.ReturnInt(AllocEdict());

    // #15 void(entity e) remove
    private void Remove(QcVm vm)
    {
        Parms(1, "VM_remove");
        int edict = vm.ArgEdict(0);
        if (edict <= ReservedEdicts)
        {
            if (_host.Developer) Warning("VM_remove: tried to remove the null entity or a reserved entity!\n");
        }
        else if (vm.IsFree(edict) || edict >= vm.NumEdicts)
        {
            // Slots past NumEdicts exist in memory but were never spawned.
            if (_host.Developer) Warning("VM_remove: tried to remove an already freed entity!\n");
        }
        else FreeEdict(edict);
    }

    // #18 entity(entity start, .string fld, string match) find. An unset string field matches "".
    private void Find(QcVm vm)
    {
        Parms(3, "VM_find");
        int field = FieldArg(1);
        string match = vm.ArgString(2);
        for (int e = vm.ArgEdict(0) + 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e) || !string.Equals(vm.GetString(vm.FieldInt(e, field)), match, StringComparison.Ordinal)) continue;
            vm.ReturnInt(e);
            return;
        }
        vm.ReturnInt(0);
    }

    // #98 entity(entity start, .float fld, float match) findfloat. Also findentity: an entity number
    // compared as a float is the same test for every number that fits a float's mantissa.
    private void FindFloat(QcVm vm)
    {
        Parms(3, "VM_findfloat");
        int field = FieldArg(1);
        float match = vm.ArgFloat(2);
        for (int e = vm.ArgEdict(0) + 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e) || vm.FieldFloat(e, field) != match) continue;
            vm.ReturnInt(e);
            return;
        }
        vm.ReturnInt(0);
    }

    // #449 entity(entity start, .float fld, float match) findflags
    private void FindFlags(QcVm vm)
    {
        Parms(3, "VM_findflags");
        int field = FieldArg(1);
        int flags = QcVm.FloatToInt(vm.ArgFloat(2));
        for (int e = vm.ArgEdict(0) + 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e)) continue;
            float value = vm.FieldFloat(e, field);
            if (value == 0 || (QcVm.FloatToInt(value) & flags) == 0) continue;
            vm.ReturnInt(e);
            return;
        }
        vm.ReturnInt(0);
    }

    // The chain builtins return the LAST match and link each match to the one before it through .chain
    // (or the field given as a third argument), ending at the world.
    private int ChainField(int argument, string name)
    {
        if (_vm.ArgCount == 3) return FieldArg(argument);
        QcDef? chain = _vm.FindField("chain");
        if (chain is null) throw Fault($"{name}: {_vm.Name} doesnt have the specified chain field !");
        return chain.Offset;
    }

    // #402 entity(.string fld, string match[, .entity chainfield]) findchain
    private void FindChain(QcVm vm)
    {
        Parms(2, 3, "VM_findchain");
        int chainField = ChainField(2, "VM_findchain"), field = FieldArg(0), chain = 0;
        string match = vm.ArgString(1);
        for (int e = 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e) || !string.Equals(vm.GetString(vm.FieldInt(e, field)), match, StringComparison.Ordinal)) continue;
            vm.FieldInt(e, chainField) = chain;
            chain = e;
        }
        vm.ReturnInt(chain);
    }

    // #403 entity(.float fld, float match[, .entity chainfield]) findchainfloat
    private void FindChainFloat(QcVm vm)
    {
        Parms(2, 3, "VM_findchainfloat");
        int chainField = ChainField(2, "VM_findchainfloat"), field = FieldArg(0), chain = 0;
        float match = vm.ArgFloat(1);
        for (int e = 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e) || vm.FieldFloat(e, field) != match) continue;
            vm.FieldInt(e, chainField) = chain;
            chain = e;
        }
        vm.ReturnInt(chain);
    }

    // #450 entity(.float fld, float match[, .entity chainfield]) findchainflags
    private void FindChainFlags(QcVm vm)
    {
        Parms(2, 3, "VM_findchainflags");
        int chainField = ChainField(2, "VM_findchainflags"), field = FieldArg(0), chain = 0;
        int flags = QcVm.FloatToInt(vm.ArgFloat(1));
        for (int e = 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e)) continue;
            float value = vm.FieldFloat(e, field);
            if (value == 0 || (QcVm.FloatToInt(value) & flags) == 0) continue;
            vm.FieldInt(e, chainField) = chain;
            chain = e;
        }
        vm.ReturnInt(chain);
    }

    // #47 entity(entity e) nextent
    private void NextEnt(QcVm vm)
    {
        Parms(1, "VM_nextent");
        // ">=" where the C tests "==": the C walks off the edict array when started past NumEdicts.
        for (int e = vm.ArgEdict(0) + 1; e < vm.NumEdicts; e++)
        {
            if (vm.IsFree(e)) continue;
            vm.ReturnInt(e);
            return;
        }
        vm.ReturnInt(0);
    }

    // #353 float(entity ent) wasfreed
    private void WasFreed(QcVm vm)
    {
        Parms(1, "VM_wasfreed");
        vm.ReturnFloat(vm.IsFree(vm.ArgEdict(0)) ? 1 : 0);
    }

    // #459 entity(float num) ftoe: the world rather than a free or impossible entity.
    private void FToE(QcVm vm)
    {
        Parms(1, "VM_ftoe");
        int edict = QcVm.FloatToInt(vm.ArgFloat(0));
        vm.ReturnInt(edict < 0 || edict >= vm.MaxEdicts || vm.IsFree(edict) ? 0 : edict);
    }

    // #512 float(entity ent) etof
    private void EToF(QcVm vm)
    {
        Parms(1, "VM_etof");
        vm.ReturnFloat(vm.ArgEdict(0));
    }

    // #400 void(entity from, entity to) copyentity
    private void CopyEntity(QcVm vm)
    {
        Parms(2, "VM_CL_copyentity");
        int from = vm.ArgEdict(0);
        if (from == 0) { Warning("copyentity: can not read world entity\n"); return; }
        if (vm.IsFree(from)) { Warning("copyentity: can not read free entity\n"); return; }
        int to = vm.ArgEdict(1);
        if (to == 0) { Warning("copyentity: can not modify world entity\n"); return; }
        if (vm.IsFree(to)) { Warning("copyentity: can not modify free entity\n"); return; }
        for (int i = 0; i < vm.EntityFields; i++) vm.FieldInt(to, i) = vm.FieldInt(from, i);
        EdictLinked?.Invoke(to);
    }

    // ---- DP_QC_ENTITYDATA: fields addressed by their index in the def table -------------------------

    private QcDef[]? _fieldDefs;
    private int _fieldDefsFor = -1;
    private Dictionary<int, string>? _fieldNameAtOffset;

    /// <summary>The program's field defs in file order (index 0 is the null def), then any the engine appended.</summary>
    private QcDef[] FieldDefs()
    {
        if (_fieldDefs is not null && _fieldDefsFor == _vm.EntityFields) return _fieldDefs;
        QcDef[] declared = _vm.Progs.FieldDefs;
        _fieldDefs = declared.Concat(_vm.FieldDefs.Where(d => d.Offset >= _vm.Progs.EntityFields && Array.IndexOf(declared, d) < 0)).ToArray();
        _fieldDefsFor = _vm.EntityFields;
        _fieldNameAtOffset = null;
        return _fieldDefs;
    }

    // pr_comp.h prvm_type_size
    private static int TypeSize(QcType type) => type == QcType.Vector ? 3 : 1;

    private bool DefIsUsable(QcDef def) => def.Offset >= 0 && def.Offset + TypeSize(def.Type) <= _vm.EntityFields;

    private bool FieldIsZero(int edict, QcDef def)
    {
        for (int j = 0; j < TypeSize(def.Type); j++)
            if (_vm.FieldInt(edict, def.Offset + j) != 0) return false;
        return true;
    }

    private bool FieldIndex(float argument, out QcDef def)
    {
        QcDef[] defs = FieldDefs();
        int index = QcVm.FloatToInt(argument);
        if (index < 0 || index >= defs.Length)
        {
            def = defs.Length > 0 ? defs[0] : new QcDef(QcType.Void, false, 0, "");
            return false;
        }
        def = defs[index];
        return true;
    }

    // #496 float() numentityfields: the number of defs, not of cells.
    private void NumEntityFields(QcVm vm) => vm.ReturnFloat(FieldDefs().Length);

    // #497 string(float fieldnum) entityfieldname
    private void EntityFieldName(QcVm vm)
    {
        if (!FieldIndex(vm.ArgFloat(0), out QcDef def))
        {
            Warning("VM_entityfieldname: field index out of bounds!\n");
            ReturnNonNullString("");
            return;
        }
        // The C returns the def's own name, a string that outlives every call; an interned engine string
        // has the same lifetime and there are only as many as the program has fields.
        vm.ReturnInt(vm.EngineString(def.Name));
    }

    // #498 float(float fieldnum) entityfieldtype: the raw def type, DEF_SAVEGLOBAL bit included.
    private void EntityFieldType(QcVm vm)
    {
        if (!FieldIndex(vm.ArgFloat(0), out QcDef def))
        {
            Warning("VM_entityfieldtype: field index out of bounds!\n");
            vm.ReturnFloat(-1);
            return;
        }
        vm.ReturnFloat((int)def.Type | (def.SaveGlobal ? 0x8000 : 0));
    }

    // #499 string(float fieldnum, entity ent) getentityfieldstring
    private void GetEntityFieldString(QcVm vm)
    {
        ReturnNonNullString(""); // every refusal below, and an all-zero field, is the empty string
        if (!FieldIndex(vm.ArgFloat(0), out QcDef def))
        {
            Warning("VM_entityfielddata: field index out of bounds!\n");
            return;
        }
        int edict = vm.ArgEdict(1);
        if (vm.IsFree(edict))
        {
            Warning($"VM_entityfielddata: entity {edict} is free!\n");
            return;
        }
        if (FieldIsZero(edict, def)) return;
        vm.ReturnString(UglyValueString(def.Type, edict, def.Offset));
    }

    // #500 float(float fieldnum, entity ent, string s) putentityfieldstring
    private void PutEntityFieldString(QcVm vm)
    {
        vm.ReturnFloat(0);
        if (!FieldIndex(vm.ArgFloat(0), out QcDef def))
        {
            Warning("VM_entityfielddata: field index out of bounds!\n");
            return;
        }
        int edict = vm.ArgEdict(1);
        if (vm.IsFree(edict))
        {
            Warning($"VM_entityfielddata: entity {edict} is free!\n");
            return;
        }
        string value = vm.ArgString(2);
        vm.ReturnFloat(ParseEpair(edict, def, value) ? 1 : 0);
    }

    // PRVM_ED_FieldAtOfs: the first def at an offset. Def 0 sits at offset 0 with an empty name, so
    // offset 0 always prints as ".".
    private string? FieldNameAtOffset(int offset)
    {
        if (_fieldNameAtOffset is null)
        {
            Dictionary<int, string> names = new();
            foreach (QcDef def in FieldDefs()) names.TryAdd(def.Offset, def.Name);
            _fieldNameAtOffset = names;
        }
        return _fieldNameAtOffset.GetValueOrDefault(offset);
    }

    /// <summary>PRVM_UglyValueString: the save-file form of a field, which <see cref="ParseEpair"/> reads back.</summary>
    private string UglyValueString(QcType type, int edict, int offset)
    {
        switch (type)
        {
            case QcType.String:
            {
                string s = _vm.GetString(_vm.FieldInt(edict, offset));
                StringBuilder line = new(s.Length + 8);
                foreach (char c in s)
                {
                    if (line.Length >= _vm.MaxStringLength - 2) break;
                    switch (c)
                    {
                        case '\n': line.Append("\\n"); break;
                        case '\r': line.Append("\\r"); break;
                        case '\\': line.Append("\\\\"); break;
                        case '"': line.Append("\\\""); break;
                        default: line.Append(c); break;
                    }
                }
                return line.ToString();
            }
            case QcType.Entity:
                return _vm.FieldInt(edict, offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case QcType.Function:
            {
                int function = _vm.FieldInt(edict, offset);
                return (uint)function < (uint)_vm.Functions.Count ? _vm.Functions[function].Name : $"bad function {function} (invalid!)";
            }
            case QcType.Field:
            {
                int field = _vm.FieldInt(edict, offset);
                return FieldNameAtOffset(field) is string name ? "." + name : $"field {field}(invalid!)";
            }
            case QcType.Void:
                return "void";
            case QcType.Float:
                return FormatG(_vm.FieldFloat(edict, offset), 9);
            case QcType.Vector:
            {
                QcVector v = _vm.FieldVector(edict, offset);
                return $"{FormatG(v.X, 9)} {FormatG(v.Y, 9)} {FormatG(v.Z, 9)}";
            }
            default:
                return $"bad type {(int)type}";
        }
    }

    // PRVM_ValueString: the human-readable form eprint shows.
    private string ValueString(QcType type, int edict, int offset)
    {
        switch (type)
        {
            case QcType.String:
                return _vm.GetString(_vm.FieldInt(edict, offset));
            case QcType.Entity:
            {
                int n = _vm.FieldInt(edict, offset);
                return n < 0 || n >= _vm.MaxEdicts ? $"entity {n} (invalid!)" : $"entity {n}";
            }
            case QcType.Function:
            {
                int function = _vm.FieldInt(edict, offset);
                return (uint)function < (uint)_vm.Functions.Count ? _vm.Functions[function].Name + "()" : $"function {function}() (invalid!)";
            }
            case QcType.Field:
            {
                int field = _vm.FieldInt(edict, offset);
                return FieldNameAtOffset(field) is string name ? "." + name : $"field {field} (invalid!)";
            }
            case QcType.Void:
                return "void";
            case QcType.Float:
                return FormatG(_vm.FieldFloat(edict, offset), 9);
            case QcType.Vector:
            {
                QcVector v = _vm.FieldVector(edict, offset);
                return $"'{FormatG(v.X, 9)} {FormatG(v.Y, 9)} {FormatG(v.Z, 9)}'";
            }
            case QcType.Pointer:
                return "pointer";
            default:
                return $"bad type {(int)type}";
        }
    }

    /// <summary>
    /// PRVM_ED_ParseEpair for an entity field, without backslash parsing (every caller here has already
    /// unescaped the text, or must not). False if the text cannot be a value of the field's type.
    /// </summary>
    private bool ParseEpair(int edict, QcDef key, string s)
    {
        switch (key.Type)
        {
            case QcType.String:
                // A zone string nothing will free unless the program strunzones it - as in the C.
                _vm.FieldInt(edict, key.Offset) = _vm.AllocString(s);
                return true;

            case QcType.Float:
                _vm.FieldFloat(edict, key.Offset) = (float)Atof(s);
                return true;

            case QcType.Vector:
            {
                // Components the text does not supply keep their old value.
                int i = 0;
                for (int component = 0; component < 3; component++)
                {
                    while (i < s.Length && IsWhitespace(s[i])) i++;
                    if (i >= s.Length) break;
                    _vm.FieldFloat(edict, key.Offset + component) = (float)Atof(s.AsSpan(i));
                    while (i < s.Length && !IsWhitespace(s[i])) i++;
                    if (i >= s.Length) break;
                }
                return true;
            }

            case QcType.Entity:
            {
                // The C grows the edict array to make any number valid. The VM cannot, so a number
                // outside it is refused instead of becoming a reference that faults on first use.
                int number = Atoi(s);
                if ((uint)number >= (uint)_vm.MaxEdicts)
                {
                    _host.Print($"PRVM_ED_ParseEpair: ev_entity reference {number} is outside the {_vm.MaxEdicts} entities of {_vm.Name}\n");
                    return false;
                }
                _vm.FieldInt(edict, key.Offset) = number;
                return true;
            }

            case QcType.Field:
            {
                if (s.Length == 0 || s[0] != '.')
                {
                    if (_host.Developer) _host.Print($"PRVM_ED_ParseEpair: Bogus field name {s} in {_vm.Name}\n");
                    return false;
                }
                QcDef? def = _vm.FindField(s[1..]);
                if (def is null)
                {
                    if (_host.Developer) _host.Print($"PRVM_ED_ParseEpair: Can't find field {s} in {_vm.Name}\n");
                    return false;
                }
                _vm.FieldInt(edict, key.Offset) = def.Offset;
                return true;
            }

            case QcType.Function:
            {
                // Function 0 is the unnamed null function, which is what an empty name finds.
                int function = s.Length == 0 ? 0 : _vm.FindFunction(s);
                if (function == 0 && s.Length != 0)
                {
                    _host.Print($"PRVM_ED_ParseEpair: Can't find function {s} in {_vm.Name}\n");
                    return false;
                }
                _vm.FieldInt(edict, key.Offset) = function;
                return true;
            }

            default:
                _host.Print($"PRVM_ED_ParseEpair: Unknown key->type {(int)key.Type} for key \"{key.Name}\" on {_vm.Name}\n");
                return false;
        }
    }

    // #31 void(entity e) eprint
    private void EPrint(QcVm vm)
    {
        Parms(1, "VM_eprint");
        PrintEdict(vm.ArgEdict(0));
    }

    /// <summary>PRVM_ED_Print: every non-zero field of an entity, to the console.</summary>
    private void PrintEdict(int edict)
    {
        if (_vm.IsFree(edict))
        {
            _host.Print($"{_vm.Name}: FREE\n");
            return;
        }

        const int Clip = 256; // sizeof(tempstring2) - 4
        StringBuilder text = new($"\n{_vm.Name} EDICT {edict}:\n");
        QcDef[] defs = FieldDefs();
        for (int i = 1; i < defs.Length; i++)
        {
            QcDef def = defs[i];
            string name = def.Name;
            if (IsVectorComponentName(name) || !DefIsUsable(def) || FieldIsZero(edict, def)) continue;

            if (name.Length > Clip) name = name[..Clip] + "...";
            text.Append(name).Append(' ', Math.Max(0, 14 - name.Length)).Append(' ');
            string value = ValueString(def.Type, edict, def.Offset);
            if (value.Length > Clip) value = value[..Clip] + "...";
            text.Append(value).Append('\n');

            // Flushed in pieces so an entity with thousands of set fields cannot build one giant line.
            if (text.Length >= _vm.MaxStringLength / 2)
            {
                _host.Print(text.ToString());
                text.Clear();
            }
        }
        if (text.Length > 0) _host.Print(text.ToString());
    }

    // The _x/_y/_z float defs a compiler emits beside every vector def.
    internal static bool IsVectorComponentName(string name) =>
        name.Length > 1 && name[^2] == '_' && name[^1] is 'x' or 'y' or 'z';

    // ---- functions by name ---------------------------------------------------------------------------

    // #605 callfunction(..., string function_name): the LAST argument names the function, the ones before
    // it are already in the parameter cells the callee will read.
    private void CallFunction(QcVm vm)
    {
        Parms(1, 8, "VM_callfunction");
        int argCount = vm.ArgCount;
        string name = vm.ArgString(argCount - 1);
        CheckEmptyString(name);
        int function = vm.FindFunction(name);
        if (function == 0) throw Fault($"VM_callfunction: function {name} not found !");
        // A builtin is entered with the argument count unchanged (name included), a QuakeC function with
        // the name dropped. Execute tells the two apart; a missing builtin faults there.
        vm.Execute(function, vm.Functions[function].IsBuiltin ? argCount : argCount - 1);
    }

    // #607 float(string function_name) isfunction
    private void IsFunction(QcVm vm)
    {
        Parms(1, "VM_isfunction");
        string name = vm.ArgString(0);
        CheckEmptyString(name);
        vm.ReturnFloat(vm.FindFunction(name) != 0 ? 1 : 0);
    }
}
