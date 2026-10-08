// Port of Base/darkplaces/clvm_cmds.c VM_CL_getinputstate, VM_CL_setcursormode, VM_CL_getmousepos,
// VM_CL_setsensitivityscale, VM_CL_registercmd, VM_CL_setpause, VM_CL_RotateMoves,
// VM_CL_particleeffectnum; prvm_cmds.c VM_keynumtostring, VM_stringtokeynum, VM_getkeybind,
// VM_findkeysforcommand, VM_centerprint.
using System.Text;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed partial class CsqcBuiltins
{
    private const int FindKeysForCommandKeys = 5; // FKFC_NUMKEYS

    private void RegisterInput()
    {
        Here(335, "particleeffectnum", ParticleEffectNum);
        Here(338, "centerprint", CenterPrint);
        Here(340, "keynumtostring", KeynumToString);
        Here(341, "stringtokeynum", StringToKeynum);
        Here(342, "getkeybind", GetKeyBind);
        Here(343, "setcursormode", SetCursorMode);
        Here(344, "getmousepos", GetMousePos);
        Here(345, "getinputstate", GetInputState);
        Here(346, "setsensitivityscale", SetSensitivityScale);
        Here(352, "registercommand", RegisterCommand);
        Here(520, "keynumtostring", KeynumToString);
        Here(521, "findkeysforcommand", FindKeysForCommand);
        Here(531, "setpause", SetPause);
        Here(610, "findkeysforcommand", FindKeysForCommand);
        Here(638, "CL_RotateMoves", RotateMoves);
    }

    // #335 float(string effectname) particleeffectnum: -1 for an unknown name.
    private void ParticleEffectNum(QcVm vm)
    {
        Parms(1, "VM_CL_particleeffectnum");
        int index = _host.Effects.IndexForName(vm.ArgString(0));
        vm.ReturnFloat(index == 0 ? -1 : index);
    }

    // #338 void(string s, ...) centerprint
    private void CenterPrint(QcVm vm)
    {
        Parms(1, 8, "VM_centerprint");
        _host.CenterPrint(VarString(0));
    }

    // #340, #520 string(float keynum) keynumtostring
    private void KeynumToString(QcVm vm)
    {
        Parms(1, "VM_keynumtostring");
        vm.ReturnString(CsqcKeys.KeynumToString(ArgInt(0)));
    }

    // #341 float(string keyname) stringtokeynum
    private void StringToKeynum(QcVm vm)
    {
        Parms(1, "VM_stringtokeynum");
        vm.ReturnFloat(CsqcKeys.StringToKeynum(vm.ArgString(0)));
    }

    // bound(-1, bindmap, MAX_BINDMAPS-1) of the optional argument; 0 without it ("consistent to bind").
    private int BindMapArg(int index)
    {
        if (_vm.ArgCount <= index) return 0;
        float value = _vm.ArgFloat(index);
        return value >= -1 ? (value < CsqcKeys.MaxBindMaps - 1 ? Int(value) : CsqcKeys.MaxBindMaps - 1) : -1;
    }

    // Key_GetBind.
    private string? Binding(int key, int bindMap) =>
        (uint)key < CsqcKeys.MaxKeys && bindMap < CsqcKeys.MaxBindMaps ? _host.KeyBinding(key, bindMap) : null;

    // #342 string(float keynum[, float bindmap]) getkeybind: the null string for an unbound key.
    private void GetKeyBind(QcVm vm)
    {
        Parms(1, 2, "VM_getkeybind");
        ReturnStringOrNull(Binding(ArgInt(0), BindMapArg(1)));
    }

    // #521, #610 string(string command[, float bindmap]) findkeysforcommand: up to five key numbers
    // bound to the command, as " 'n' 'n' 'n' 'n' 'n'" with -1 for the unused places.
    private void FindKeysForCommand(QcVm vm)
    {
        Parms(1, 2, "VM_findkeysforcommand");
        string command = vm.ArgString(0);
        int bindMap = BindMapArg(1);
        CheckEmptyString(command);

        Span<int> keys = stackalloc int[FindKeysForCommandKeys];
        keys.Fill(-1);
        int count = 0;
        // The C walks all 44,032 key slots; with no bind table behind the host there is nothing to find.
        if (_host.FindKeysForCommand is { } find && bindMap < CsqcKeys.MaxBindMaps) find(command, keys, bindMap);
        else
        for (int key = 0; _host.HasKeyBindings && key < CsqcKeys.MaxKeys && count < keys.Length; key++)
        {
            string? bind = Binding(key, bindMap);
            if (bind is not null && string.Equals(bind, command, StringComparison.Ordinal)) keys[count++] = key;
        }

        StringBuilder text = new();
        foreach (int key in keys) text.Append(" '").Append(key).Append('\'');
        vm.ReturnString(text.ToString());
    }

    // #343 void(float usecursor) setcursormode
    private void SetCursorMode(QcVm vm)
    {
        Parms(1, "VM_CL_setcursormode");
        _state.WantsMouseMove = vm.ArgFloat(0) != 0;
    }

    // #344 vector() getmousepos: zero unless the game has the keyboard.
    private void GetMousePos(QcVm vm)
    {
        Parms(0, "VM_CL_getmousepos");
        vm.ReturnVector(_state.GameHasKeyFocus ? new QcVector(_state.MousePosition.X, _state.MousePosition.Y, 0) : default);
    }

    // #345 float(float framenum) getinputstate: load the input_* globals from the remembered input
    // command with that sequence number. Xonotic replays these to predict the player's movement.
    private void GetInputState(QcVm vm)
    {
        Parms(1, "VM_CL_getinputstate");
        // (unsigned int) of a float: a negative or huge number names no command.
        float argument = vm.ArgFloat(0);
        vm.ReturnFloat(0);
        if (!(argument >= 0 && argument < 4294967296f)) return;
        uint frame = (uint)argument;

        CsqcUserCommand[] commands = _state.MoveCommands;
        for (int i = 0; i < commands.Length; i++)
        {
            if (commands[i].Sequence != frame) continue;
            ref CsqcUserCommand c = ref commands[i];
            _host.SetVector(_g.InputAngles, c.ViewAngles);
            _host.SetFloat(_g.InputButtons, c.Buttons);
            _host.SetVector(_g.InputMoveValues, new QcVector(c.ForwardMove, c.SideMove, c.UpMove));
            _host.SetFloat(_g.InputTimeLength, c.FrameTime);
            // "this probably shouldn't be here"
            _host.SetVector(_g.PmoveMins, c.Crouch ? _state.PlayerCrouchMins : _state.PlayerStandMins);
            _host.SetVector(_g.PmoveMaxs, c.Crouch ? _state.PlayerCrouchMaxs : _state.PlayerStandMaxs);
            vm.ReturnFloat(1);
        }
    }

    // #346 void(float sens) setsensitivityscale
    private void SetSensitivityScale(QcVm vm)
    {
        Parms(1, "VM_CL_setsensitivityscale");
        _state.SensitivityScale = vm.ArgFloat(0);
    }

    // #352 void(string cmdname) registercommand
    private void RegisterCommand(QcVm vm)
    {
        Parms(1, "VM_CL_registercmd");
        _host.Console.RegisterQcCommand(vm.ArgString(0));
    }

    // #531 void(float pause) setpause: only a single-player game can be paused from the client.
    private void SetPause(QcVm vm)
    {
        Parms(1, "VM_CL_setpause");
        if (_state.IsLocalGame) _state.Paused = ArgInt(0) != 0;
    }

    // #638 void(vector ang) CL_RotateMoves: rotate the not-yet-acknowledged input commands by a
    // warpzone's transform, given as euler angles.
    private void RotateMoves(QcVm vm)
    {
        Parms(1, "VM_CL_RotateMoves");
        // AngleVectorsFLU: forward, left, up.
        QcCoreBuiltins.AngleVectors(vm.ArgVector(0), out QcVector forward, out QcVector right, out QcVector up);
        _state.RotateMoves(forward, new QcVector(-right.X, -right.Y, -right.Z), up);
    }
}
