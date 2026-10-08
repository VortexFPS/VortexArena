// Port of Base/darkplaces/clvm_cmds.c vm_cl_builtins[] (the entries QcCoreBuiltins and QcStringBuiltins
// do not already cover) and the VM_SAFEPARMCOUNT / VM_Warning helpers of prvm_cmds.h.
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// The client-program builtins that need the engine around the VM: the message being parsed, the
/// stats and scoreboard, the precache tables, the input history, the console - implemented here - and
/// the renderer, sound and collision world, which are forwarded to <see cref="ILegacyPresentation"/>
/// after the same argument checks DarkPlaces makes.
///
/// Numbers are the ones in vm_cl_builtins[]. Every call is counted (<see cref="CallCounts"/>), so a
/// run can report what a program actually uses.
/// </summary>
public sealed partial class CsqcBuiltins
{
    private readonly CsqcHost _host;
    private readonly QcVm _vm;
    private readonly QcCoreBuiltins _core;
    private readonly CsqcClientState _state;
    private readonly CsqcFieldOffsets _f;
    private readonly CsqcGlobalOffsets _g;
    private readonly ILegacyPresentation _presentation;
    private readonly List<(int Number, string Name, bool Forwarded)> _registered = new();
    private readonly long[] _calls = new long[700];

    internal CsqcBuiltins(CsqcHost host, QcCoreBuiltins core)
    {
        _host = host;
        _vm = host.Vm;
        _core = core;
        _state = host.State;
        _f = host.Fields;
        _g = host.Globals;
        _presentation = host.Presentation;
    }

    /// <summary>Every builtin this class registers: its number, its DarkPlaces name, and whether its
    /// work is done by the presentation (true) or here (false).</summary>
    public IReadOnlyList<(int Number, string Name, bool Forwarded)> Registered => _registered;

    /// <summary>Calls received by builtin number, since the program was loaded.</summary>
    public long CallCount(int number) => (uint)number < (uint)_calls.Length ? _calls[number] : 0;

    /// <summary>The builtins called at least once, with their counts.</summary>
    public IEnumerable<(int Number, string Name, bool Forwarded, long Calls)> CallCounts =>
        _registered.Where(r => _calls[r.Number] != 0).Select(r => (r.Number, r.Name, r.Forwarded, _calls[r.Number]));

    private void Here(int number, string name, QcBuiltin builtin) => Add(number, name, builtin, false);
    private void Forward(int number, string name, QcBuiltin builtin) => Add(number, name, builtin, true);

    private void Add(int number, string name, QcBuiltin builtin, bool forwarded)
    {
        _registered.Add((number, name, forwarded));
        long[] calls = _calls;
        _vm.RegisterBuiltin(number, vm =>
        {
            calls[number]++;
            builtin(vm);
        });
    }

    internal void Register()
    {
        RegisterNetwork();
        RegisterWorld();
        RegisterInput();
        RegisterScene();
        RegisterDraw();
        RegisterSoundAndEffects();
        RegisterModels();
    }

    /// <summary>Per-frame reset (CL_VM_UpdateView: "polygonbegin without draw2d arg has to guess").</summary>
    internal void BeginFrame()
    {
        _polygonGuess2D = false;
        _polygonOpen = false;
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private QcRuntimeException Fault(string message) => new($"{_vm.Name}: {message}");

    private void Warning(string message) => _host.Services.Warning($"{_vm.Name} VM warning: {message}");

    // VM_SAFEPARMCOUNT: a wrong argument count is fatal to the program, which is also what stops a
    // builtin from reading parameter cells the caller never wrote.
    private void Parms(int count, string name)
    {
        if (_vm.ArgCount != count) throw Fault($"{name} wrong parameter count {_vm.ArgCount} ({count} expected ) !");
    }

    private void Parms(int min, int max, string name)
    {
        if (_vm.ArgCount < min || _vm.ArgCount > max)
            throw Fault($"{name} wrong parameter count {_vm.ArgCount} ({min} to {max} expected ) !");
    }

    // VM_CheckEmptyString.
    private void CheckEmptyString(string s)
    {
        if (s.Length == 0 || s[0] is ' ' or '\t' or '\r' or '\n') throw Fault("Bad string");
    }

    // VM_VarString: arguments from `first` on, concatenated, cut at the tempstring size.
    private string VarString(int first)
    {
        int count = Math.Min(_vm.ArgCount, ProgsFile.MaxParms);
        if (first >= count) return "";
        if (first == count - 1)
        {
            string only = _vm.ArgString(first);
            return only.Length < _vm.MaxStringLength ? only : only[..(_vm.MaxStringLength - 1)];
        }
        System.Text.StringBuilder text = new();
        int room = _vm.MaxStringLength - 1;
        for (int i = first; i < count && text.Length < room; i++)
        {
            string s = _vm.ArgString(i);
            text.Append(text.Length + s.Length <= room ? s : s[..(room - text.Length)]);
        }
        return text.ToString();
    }

    private static int Int(float value) => QcVm.FloatToInt(value);

    private int ArgInt(int index) => QcVm.FloatToInt(_vm.ArgFloat(index));

    // PRVM_PROG_TO_EDICT(self), checked: the program can store anything in its own global.
    private int Self
    {
        get
        {
            int self = _host.GetInt(_g.Self);
            if ((uint)self >= (uint)_vm.MaxEdicts) throw Fault($"entity {self} is out of range");
            return self;
        }
    }

    // A string result that may be the null string (OFS_NULL) rather than an empty one.
    private void ReturnStringOrNull(string? text)
    {
        if (text is null) _vm.ReturnInt(0);
        else _vm.ReturnString(text);
    }

    private static bool IsNaN(QcVector v) => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z);
}
