// Port of Base/darkplaces/prvm_edict.c PRVM_GameCommand ("sv_cmd") and PRVM_ConsoleCommand (commands
// the program registered); cmd.c Cmd_Defer_f and Cbuf_Execute_Deferred ("defer"), Cbuf_Execute;
// sv_ccmds.c SV_Map_f, SV_Changelevel_f, SV_Restart_f (as events for the owner to act on), SV_Say,
// SV_Kick_f, SV_Status_f; cvar.c Cvar_Toggle-like "toggle".
using System.Globalization;
using System.Text;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvqcHost
{
    private readonly DpStuffTextBuffer _commandBuffer = new();
    private readonly List<string> _commandLines = new();
    private readonly List<(double Time, string Command)> _deferred = new();
    private const int MaxDeferred = 4096;
    private readonly string[] _engineCommands = { "sv_cmd", "defer", "map", "changelevel", "restart", "say", "echo", "toggle", "kick", "status", "quit", "exit" };

    public long CommandLinesExecuted { get; private set; }
    public long GameCommands { get; private set; }
    /// <summary>The console asked the server to end (quit / exit).</summary>
    public bool QuitRequested { get; private set; }
    /// <summary>Names the program created with registercommand.</summary>
    public IReadOnlyCollection<string> QcCommands => _qcCommands;

    private void RegisterConsoleCommands()
    {
        Interpreter.RegisterCommand("sv_cmd", argv => GameCommand(CsqcConsoleArgs.Join(argv, 1)),
            "calls the server QC function GameCommand with the supplied string as argument");
        Interpreter.RegisterCommand("defer", Defer, "execute a command in the future");
        Interpreter.RegisterCommand("map", argv => RequestMap(argv, restart: true), "kick everyone off the server and start a new level");
        Interpreter.RegisterCommand("changelevel", argv => RequestMap(argv, restart: false), "change to another level, bringing along all connected clients");
        Interpreter.RegisterCommand("restart", _ =>
        {
            PendingMap = WorldBaseName;
            PendingMapKind = SvMapRequest.Restart;
        }, "restart current level");
        Interpreter.RegisterCommand("say", argv =>
        {
            // SV_Say from the server console: "<hostname>: text", to everyone.
            string text = CsqcConsoleArgs.Join(argv, 1);
            if (text.Length > 0 && State == SvState.Active) BroadcastPrint($"\x01<{Cvars.GetString("hostname")}> {text}\n");
        }, "send a chat message to everyone on the server");
        Interpreter.RegisterCommand("echo", argv => Print(string.Join(' ', argv.Skip(1)) + "\n"), "print a message to the console (useful in scripts)");
        Interpreter.RegisterCommand("toggle", argv =>
        {
            if (argv.Count >= 2 && Cvars.Has(argv[1])) Cvars.Set(argv[1], Cvars.GetFloat(argv[1]) != 0 ? "0" : "1");
        }, "toggles a console variable's values");
        Interpreter.RegisterCommand("kick", Kick, "kick a player off the server by number or name");
        Interpreter.RegisterCommand("status", _ => Print(StatusText()), "print server status information");
        Interpreter.RegisterCommand("quit", _ => QuitRequested = true, "quit the game");
        Interpreter.RegisterCommand("exit", _ => QuitRequested = true, "quit the game");
    }

    private void UnregisterConsoleCommands()
    {
        // The interpreter outlives a level; a command of a dead level must not call into its program.
        foreach (string name in _engineCommands) Interpreter.RegisterCommand(name, _ => { });
        foreach (string name in _qcCommands) Interpreter.RegisterCommand(name, _ => { });
    }

    private void RequestMap(IReadOnlyList<string> argv, bool restart)
    {
        if (argv.Count != 2)
        {
            Print(restart ? "map <levelname> : start a new game (kicks off all players)\n" : "changelevel <levelname> : continue game on a new level\n");
            return;
        }
        // The owner acts on it between frames (SV_SpawnServer cannot run inside the level it replaces).
        PendingMap = argv[1];
        PendingMapKind = restart ? SvMapRequest.Map : SvMapRequest.ChangeLevel;
    }

    // Cmd_Defer_f: "defer <seconds> <command>", "defer clear", or a listing.
    private void Defer(IReadOnlyList<string> argv)
    {
        if (argv.Count == 2 && argv[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _deferred.Clear();
            return;
        }
        if (argv.Count != 3)
        {
            if (argv.Count == 1)
                foreach ((double time, string command) in _deferred)
                    Print(string.Create(CultureInfo.InvariantCulture, $"-> In {time - RealTime:0.00}: {command}\n"));
            return;
        }
        if (_deferred.Count >= MaxDeferred) return;
        double delay = QcCoreBuiltinsAtof(argv[1]);
        _deferred.Add((RealTime + delay, argv[2]));
    }

    private static double QcCoreBuiltinsAtof(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;

    // SV_Kick_f, reduced: "kick # <slot>" or "kick <name>", with an optional reason.
    private void Kick(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2 || State != SvState.Active) return;
        SvClient? target = null;
        int reasonAt = 2;
        if (argv[1] == "#" && argv.Count >= 3 && int.TryParse(argv[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
        {
            if (slot >= 1 && slot <= Clients.Length && Clients[slot - 1].Active) target = Clients[slot - 1];
            reasonAt = 3;
        }
        else
            foreach (SvClient client in Clients)
                if (client.Active && string.Equals(client.Name, argv[1], StringComparison.OrdinalIgnoreCase)) target = client;
        if (target is null) return;
        string reason = argv.Count > reasonAt ? string.Join(' ', argv.Skip(reasonAt)) : "";
        if (target.Connection is not null) ClientPrint(target, reason.Length > 0 ? $"Kicked by server: {reason}\n" : "Kicked by server\n");
        DropClient(target, reason.Length > 0 ? $"Kicked by server: {reason}" : "Kicked by server");
    }

    /// <summary>SV_Status_f, reduced to what a console needs: the level and one line per player.</summary>
    public string StatusText()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"host:     {Cvars.GetString("hostname")}\n");
        text.Append(CultureInfo.InvariantCulture, $"map:      {WorldBaseName}\n");
        text.Append(CultureInfo.InvariantCulture, $"timing:   time {Time:0.00}, {Frames} frames, {Vm.NumEdicts} edicts\n");
        text.Append(CultureInfo.InvariantCulture, $"players:  {ActiveClients} active ({Clients.Length} max)\n");
        foreach (SvClient client in Clients)
        {
            if (!client.Active) continue;
            string kind = client.Connection is null ? "bot" : client.NetAddress;
            text.Append(CultureInfo.InvariantCulture, $"#{client.Edict,-3} {client.Name,-24} frags {client.Frags,4}  {kind}\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// The registercommand builtin (Cmd_AddCommand with no engine function): typing the command calls
    /// the program's ConsoleCmd with the whole line.
    /// </summary>
    private void RegisterQcCommand(string name)
    {
        if (name.Length == 0 || name.Length > 128 || _qcCommands.Count >= 4096 || !_qcCommands.Add(name)) return;
        Interpreter.RegisterCommand(name, argv => ConsoleCommand(CsqcConsoleArgs.Join(argv, 0)), "console command created by QuakeC");
    }

    /// <summary>Cbuf_AddText: queue console text. It runs at the next <see cref="ExecuteCommands"/>.</summary>
    public void AddCommandText(string text)
    {
        if (!string.IsNullOrEmpty(text)) _commandBuffer.Add(text, _commandLines);
    }

    /// <summary>
    /// Cbuf_Execute: run everything queued - what the owner added, what the program's localcmd
    /// added, deferred commands that have come due - and whatever running that queues in turn.
    /// Bounded, so a command that re-queues itself costs one frame of work rather than a hang.
    /// </summary>
    public void ExecuteCommands()
    {
        if (_disposed) return;
        // Cbuf_Execute_Deferred
        for (int i = 0; i < _deferred.Count;)
        {
            if (_deferred[i].Time > RealTime) { i++; continue; }
            string command = _deferred[i].Command;
            _deferred.RemoveAt(i);
            AddCommandText(command + "\n");
        }
        for (int round = 0; round < 16; round++)
        {
            foreach (string text in Services.TakePendingCommands()) AddCommandText(text);
            if (_commandLines.Count == 0) return;
            string[] lines = _commandLines.ToArray();
            _commandLines.Clear();
            foreach (string line in lines)
            {
                CommandLinesExecuted++;
                try { Interpreter.ExecuteLine(line); }
                catch (QcRuntimeException e) { RecordFault("console: " + (line.Length > 60 ? line[..60] : line), e.Message); }
                if (_disposed) return;
            }
        }
    }

    /// <summary>PRVM_GameCommand ("sv_cmd"): the program's GameCommand with the rest of the line.</summary>
    public void GameCommand(string text)
    {
        if (Fn.GameCommand == 0)
        {
            Print("server program do not support GameCommand!\n");
            return;
        }
        GameCommands++;
        Guard("GameCommand", () =>
        {
            int mark = Vm.TempStringMark;
            Vm.SetArgInt(0, Vm.TempString(text));
            Vm.Execute(Fn.GameCommand, 1);
            Vm.ReleaseTempStrings(mark);
        });
    }

    /// <summary>
    /// PRVM_ConsoleCommand (SV_VM_ConsoleCommand): the program's ConsoleCmd with a whole command
    /// line, self being the world. Returns what it returned: true if it handled the command.
    /// </summary>
    public bool ConsoleCommand(string text)
    {
        if (Fn.ConsoleCmd == 0 || State == SvState.Dead) return false;
        bool handled = false;
        Guard("ConsoleCmd", () =>
        {
            int saveSelf = Self;
            int mark = Vm.TempStringMark;
            SetTime(Time);
            Self = 0;
            Vm.SetArgInt(0, Vm.TempString(text));
            Vm.Execute(Fn.ConsoleCmd, 1);
            handled = QcVm.FloatToInt(Vm.ResultFloat) != 0;
            Vm.ReleaseTempStrings(mark);
            Self = saveSelf;
        });
        return handled;
    }
}

/// <summary>Rebuilds a command line from an interpreter's argument vector (see CsqcConsole.JoinArguments, whose rule this is).</summary>
internal static class CsqcConsoleArgs
{
    public static string Join(IReadOnlyList<string> argv, int first)
    {
        if (argv.Count <= first) return "";
        if (argv.Count == first + 1 && !NeedsQuotes(argv[first])) return argv[first];
        StringBuilder text = new();
        for (int i = first; i < argv.Count; i++)
        {
            if (i > first) text.Append(' ');
            string arg = argv[i];
            if (NeedsQuotes(arg)) text.Append('"').Append(arg.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            else text.Append(arg);
        }
        return text.ToString();
    }

    private static bool NeedsQuotes(string arg)
    {
        if (arg.Length == 0) return true;
        foreach (char c in arg)
            if (c <= ' ' || c is '"' or ';') return true;
        return false;
    }
}
