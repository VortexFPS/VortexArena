// Port of Base/darkplaces/prvm_edict.c PRVM_Prog_Load (the autocvar_ pass at its end) and cvar.c
// Cvar_UpdateAutoCvar.
namespace VortexArena.QuakeC;

/// <summary>
/// Autocvars: a global the program names <c>autocvar_foo</c> mirrors the cvar <c>foo</c>. The program
/// reads the global as an ordinary variable and never calls cvar(), so the engine has to write the
/// cvar's value into it at load and again whenever the cvar changes.
///
/// The global's compiled-in initial value is the cvar's default: a cvar that does not exist yet is
/// created from it. One that does exist overrides it.
/// </summary>
public sealed class QcAutocvars
{
    private const string Prefix = "autocvar_";
    private const int CvarPrivate = 4; // cvar_type bit, see IQcHost.CvarTypeFlags

    private readonly QcVm _vm;
    private readonly IQcHost _host;
    private readonly Dictionary<string, QcDef> _bound = new(StringComparer.Ordinal);
    // The zone string each string autocvar currently points at, so an update frees the one it replaces.
    private readonly Dictionary<int, int> _ownedStrings = new();

    public QcAutocvars(QcVm vm, IQcHost host)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Number of globals bound to a cvar.</summary>
    public int Count => _bound.Count;

    /// <summary>Cvar names (without the prefix) that have a bound global.</summary>
    public IEnumerable<string> CvarNames => _bound.Keys;

    /// <summary>
    /// Binds every autocvar global of the program: creates the missing cvars from the globals' initial
    /// values and loads the globals of the existing ones. Call once, after loading and before running
    /// anything. Returns the number bound.
    /// </summary>
    public int Bind()
    {
        // (The C reuses its loop counter inside the vector case and so revisits most globals a second
        // time; the second visit finds every cvar present and reloads the same values. One pass here.)
        foreach (QcDef def in _vm.Progs.GlobalDefs)
        {
            string name = def.Name;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || QcCoreBuiltins.IsVectorComponentName(name)) continue;
            if (def.Type is not (QcType.Float or QcType.Vector or QcType.String))
            {
                _host.Print($"PRVM_Prog_Load: invalid type of autocvar global {name} in {_vm.Name}\n");
                continue;
            }
            if (def.Offset + (def.Type == QcType.Vector ? 3 : 1) > _vm.NumGlobals) continue;

            string cvar = name[Prefix.Length..];
            if (!_host.CvarExists(cvar))
            {
                _host.RegisterCvar(cvar, InitialValue(def), 0);
                // A float or vector global keeps the bits it was compiled with - they ARE the value just
                // registered. A string global is repointed at the cvar's own copy of the text, the
                // string that later updates replace.
                if (def.Type == QcType.String) Load(cvar, def);
                _bound[cvar] = def;
            }
            else if ((_host.CvarTypeFlags(cvar) & CvarPrivate) == 0)
            {
                Load(cvar, def);
                _bound[cvar] = def;
            }
            else _host.Print($"PRVM_Prog_Load: private cvar for autocvar global {name} in {_vm.Name}\n");
        }
        return _bound.Count;
    }

    /// <summary>Copies the cvar's current value into its global. False if no global mirrors that cvar.</summary>
    public bool Update(string cvarName)
    {
        if (!_bound.TryGetValue(cvarName, out QcDef? def)) return false;
        Load(cvarName, def);
        return true;
    }

    public void UpdateAll()
    {
        foreach ((string cvar, QcDef def) in _bound) Load(cvar, def);
    }

    // The text a new cvar is created with: the shortest decimal that reads back as the same float
    // (DarkPlaces' "ftos_slow": try 7, 8, then 9 significant digits), or the integer when it is one.
    private string InitialValue(QcDef def)
    {
        switch (def.Type)
        {
            case QcType.Float:
            {
                float f = _vm.GlobalFloat(def.Offset);
                int whole = QcVm.FloatToInt(f);
                return whole == f ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture) : Shortest(f);
            }
            case QcType.Vector:
            {
                QcVector v = _vm.GlobalVector(def.Offset);
                return $"{Shortest(v.X)} {Shortest(v.Y)} {Shortest(v.Z)}";
            }
            default:
                return _vm.GetString(_vm.GlobalInt(def.Offset));
        }

        static string Shortest(float f)
        {
            for (int precision = 7; precision < 9; precision++)
            {
                string text = QcCoreBuiltins.FormatG(f, precision);
                if ((float)QcCoreBuiltins.Atof(text) == f) return text;
            }
            return QcCoreBuiltins.FormatG(f, 9);
        }
    }

    private void Load(string cvar, QcDef def)
    {
        switch (def.Type)
        {
            case QcType.Float:
                _vm.GlobalFloat(def.Offset) = _host.CvarFloat(cvar);
                break;

            case QcType.Vector:
            {
                // Up to three whitespace-separated numbers; the components the text lacks are 0.
                string s = _host.CvarString(cvar);
                QcVector v = default;
                int i = 0;
                for (int component = 0; component < 3; component++)
                {
                    while (i < s.Length && QcCoreBuiltins.IsWhitespace(s[i])) i++;
                    if (i >= s.Length) break;
                    float value = (float)QcCoreBuiltins.Atof(s.AsSpan(i));
                    if (component == 0) v.X = value;
                    else if (component == 1) v.Y = value;
                    else v.Z = value;
                    while (i < s.Length && !QcCoreBuiltins.IsWhitespace(s[i])) i++;
                }
                _vm.GlobalVector(def.Offset) = v;
                break;
            }

            case QcType.String:
            {
                // DarkPlaces keeps one engine string per autocvar and rewrites it in place. The
                // equivalent here is one zone string per autocvar, freed and reallocated on change: the
                // freed slot is the first one AllocString hands back, so the count stays at one per
                // autocvar however often the cvar changes. An empty value still gets a real (non-null)
                // string, as in the C, where "if (autocvar_x)" tests the handle and not the text.
                string text = _host.CvarString(cvar);
                if (_ownedStrings.TryGetValue(def.Offset, out int old))
                {
                    if (_vm.GlobalInt(def.Offset) == old && string.Equals(_vm.GetString(old), text, StringComparison.Ordinal)) break;
                    _vm.FreeString(old);
                }
                int handle = _vm.AllocString(text);
                _ownedStrings[def.Offset] = handle;
                _vm.GlobalInt(def.Offset) = handle;
                break;
            }
        }
    }
}
