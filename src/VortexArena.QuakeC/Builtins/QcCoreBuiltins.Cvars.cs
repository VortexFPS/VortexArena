// Port of Base/darkplaces/prvm_cmds.c (checkextension, VM_checkextension, VM_localcmd, PRVM_Cvar_ReadOk,
// VM_cvar, VM_cvar_type, VM_cvar_string, VM_cvar_defstring, VM_cvar_description, VM_cvar_set,
// VM_registercvar).
namespace VortexArena.QuakeC;

public sealed partial class QcCoreBuiltins
{
    // DarkPlaces cvar_type bits, as IQcHost.CvarTypeFlags reports them.
    private const int CvarPrivate = 4, CvarReadOnly = 32;
    // cmd.h CF_MAXFLAGSVAL: the highest flag combination registercvar accepts.
    private const uint MaxCvarFlags = (1u << 12) - 1;

    // #99 float(string s) checkextension. The C also answers false for listed extensions whose library
    // is missing (crypto, curl, ODE); here the host simply does not list what it cannot do.
    private void CheckExtension(QcVm vm)
    {
        Parms(1, "VM_checkextension");
        string name = vm.ArgString(0);
        // Programs ask every frame, and mostly for extensions this engine does not have - which is the
        // slow answer below (a pass over the whole set). The answer for a name never changes.
        // (...unless the owner adds to the set, which is what the count is kept for.)
        if (_extensionAnswersFor != Extensions.Count)
        {
            _extensionAnswers.Clear();
            _extensionAnswersFor = Extensions.Count;
        }
        if (!_extensionAnswers.TryGetValue(name, out bool supported))
        {
            supported = Extensions.Contains(name);
            if (!supported)
            {
                // The set may have been built with a case-sensitive comparer; the C compares with strcasecmp.
                foreach (string extension in Extensions)
                {
                    if (!string.Equals(extension, name, StringComparison.OrdinalIgnoreCase)) continue;
                    supported = true;
                    break;
                }
            }
            if (_extensionAnswers.Count < 1024) _extensionAnswers[name] = supported;
        }
        vm.ReturnFloat(supported ? 1 : 0);
    }

    private readonly Dictionary<string, bool> _extensionAnswers = new(StringComparer.Ordinal);
    private int _extensionAnswersFor = -1;

    // #46 void(string s, ...) localcmd
    private void LocalCmd(QcVm vm)
    {
        Parms(1, 8, "VM_localcmd");
        _host.LocalCommand(VarString(0));
    }

    // PRVM_Cvar_ReadOk: a private cvar (rcon_password and the like) reads as if it did not exist.
    private bool CvarReadOk(string name) => _host.CvarExists(name) && (_host.CvarTypeFlags(name) & CvarPrivate) == 0;

    private string CvarNameArg(string builtin)
    {
        Parms(1, 8, builtin);
        string name = VarString(0);
        CheckEmptyString(name);
        return name;
    }

    // #45 float(string s, ...) cvar
    private void Cvar(QcVm vm)
    {
        string name = CvarNameArg("VM_cvar");
        vm.ReturnFloat(_host.TryCvarFloat(name, out float value) ? value : 0);
    }

    // #495 float(string name, ...) cvar_type. Unlike the readers this does report a private cvar: bit 4.
    private void CvarType(QcVm vm)
    {
        string name = CvarNameArg("VM_cvar_type");
        vm.ReturnFloat(_host.CvarExists(name) ? _host.CvarTypeFlags(name) | 1 : 0);
    }

    // #448 string(string s, ...) cvar_string
    private void CvarString(QcVm vm)
    {
        string name = CvarNameArg("VM_cvar_string");
        ReturnNonNullString(_host.TryCvarString(name, out string value) ? value : "");
    }

    // #482 string(string s, ...) cvar_defstring. No private check in the C; the host decides what it tells.
    private void CvarDefString(QcVm vm)
    {
        string name = CvarNameArg("VM_cvar_defstring");
        ReturnNonNullString(_host.CvarDefaultString(name) ?? "");
    }

    // #518 string(string name, ...) cvar_description
    private void CvarDescription(QcVm vm)
    {
        string name = CvarNameArg("VM_cvar_description");
        ReturnNonNullString(_host.CvarDescription(name) ?? "");
    }

    // #72 void(string var, string val, ...) cvar_set. Only the VALUE is built from several arguments.
    private void CvarSet(QcVm vm)
    {
        Parms(2, 8, "VM_cvar_set");
        string name = vm.ArgString(0);
        CheckEmptyString(name);
        if (!_host.CvarExists(name))
        {
            Warning($"VM_cvar_set: variable {name} not found\n");
            return;
        }
        if ((_host.CvarTypeFlags(name) & CvarReadOnly) != 0)
        {
            Warning($"VM_cvar_set: variable {name} is read-only\n");
            return;
        }
        _host.CvarSet(name, VarString(1));
    }

    // #93 float(string name, string value[, float flags]) registercvar. The C also refuses a name that
    // is a console command; that table is the host's, so RegisterCvar returning false covers it.
    private void RegisterCvar(QcVm vm)
    {
        Parms(2, 3, "VM_registercvar");
        string name = vm.ArgString(0), value = vm.ArgString(1);
        uint flags = vm.ArgCount >= 3 ? unchecked((uint)QcVm.FloatToInt(vm.ArgFloat(2))) : 0;
        vm.ReturnFloat(0);
        if (flags > MaxCvarFlags || _host.CvarExists(name)) return;
        if (_host.RegisterCvar(name, value, (int)flags)) vm.ReturnFloat(1);
    }
}
