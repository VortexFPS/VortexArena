// Port of the console plumbing a client program depends on: Base/darkplaces/cmd.c Cbuf_AddText /
// Cbuf_Execute / Cmd_Wait_f / Cmd_Defer_f (through Protocol/DpCommandBuffer.cs) and Cmd_CL_Callback
// (commands created by QuakeC), prvm_edict.c PRVM_GameCommand (cl_cmd, menu_cmd) and cl_cmd.c
// CL_ForwardToServer_f (cmd).
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Legacy.Protocol;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// The command buffer between the server, the client program and the console interpreter.
///
/// Text reaches it from three places - svc_stufftext, the program's localcmd builtin, and the host -
/// and none of it runs at once: DarkPlaces appends to a buffer and executes the buffer between
/// frames, so that a command issued from inside QuakeC never re-enters QuakeC. <see cref="Execute"/>
/// is that step, and a <c>wait</c> ends it for the frame: what is left runs after the next
/// <see cref="NewFrame"/> (<see cref="DpCommandBuffer"/> is the buffer, the same one the Xonotic menu's
/// console uses). Each complete command line is first offered to <see cref="EngineCommand"/> (the
/// handful the engine itself owns: csqc_progcrc, cl_downloadbegin, ...) and otherwise handed to the
/// <see cref="ConfigInterpreter"/>, where the three commands that lead back into the program live:
/// <c>cl_cmd</c> (GameCommand), anything the program created with registercommand
/// (CSQC_ConsoleCommand), and <c>cmd</c> (forward to the server).
/// </summary>
public sealed class CsqcConsole
{
    private readonly LegacyQcHost _services;
    private readonly DpCommandBuffer _buffer;
    private bool _frameDriven;
    private readonly HashSet<string> _qcCommands = new(StringComparer.OrdinalIgnoreCase);
    // cl_cmd, cmd and whatever the program creates: through a relay, so that an interpreter which outlives
    // this console (the Xonotic menu's) does not hold it - and through it the session - after Detach.
    private readonly LegacySessionCommands _commands = new();

    public CsqcConsole(ConfigInterpreter interpreter, LegacyQcHost services)
    {
        Interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        // localcmd: "Cbuf_AddText" the moment the builtin runs, so behind whatever is already waiting.
        _buffer = new DpCommandBuffer(interpreter) { MoreText = TakeProgramText };

        // wait and defer are the ENGINE's commands (cmd.c). On the Xonotic menu's console that console has
        // registered them already (they act on whichever buffer is running); a session's own interpreter
        // gets them here. Without them "wait" was an unknown command, and unknown commands went to the server.
        if (!interpreter.CommandNames.Contains("wait"))
        {
            DpCommandBuffer.Handlers(_buffer, text => _services.Print(text), out Action<IReadOnlyList<string>> wait, out Action<IReadOnlyList<string>> defer);
            _commands.Register(interpreter, "wait", wait, DpCommandBuffer.WaitHelp);
            _commands.Register(interpreter, "defer", defer, DpCommandBuffer.DeferHelp);
        }

        _commands.Register(interpreter, "cl_cmd", argv =>
        {
            // PRVM_GameCommand passes Cmd_Args: everything after the command word.
            GameCommands++;
            Host?.GameCommand(JoinArguments(argv, 1));
        }, "calls the client QC function GameCommand with the supplied string as argument");

        _commands.Register(interpreter, "cmd", argv =>
        {
            string text = JoinArguments(argv, 1);
            if (text.Length == 0) return;
            ForwardedToServer++;
            SendToServer?.Invoke(text);
        }, "send a console commandline to the server (used by some mods)");

        _commands.Register(interpreter, "cl_particles_reloadeffects", argv => Host?.ReloadEffects(argv.Count > 1 ? argv[1] : null),
            "reloads effectinfo.txt and maps/levelname_effectinfo.txt (where levelname is the current map) if parameter is given, loads from custom file (no levelname_effectinfo are loaded in this case)");

        // Xonotic's client program issues a few menu_cmd commands (to sync a menu that is not running
        // here). With no menu program DarkPlaces prints a complaint and carries on; so does this. When a
        // menu program IS running on this interpreter (Menu/MenuHost.cs registers menu_cmd for itself, on
        // the console a session started from the Xonotic menu shares with it), its command is left alone.
        if (!interpreter.CommandNames.Contains("menu_cmd"))
            _commands.Register(interpreter, "menu_cmd", _ => MenuCommands++,
                "calls the menu QC function GameCommand with the supplied string as argument");
    }

    public ConfigInterpreter Interpreter { get; }

    /// <summary>
    /// The session is over: the commands this console put on the interpreter stay known but lead nowhere
    /// (DarkPlaces: "client: program is not loaded"), and the interpreter no longer holds this console, its
    /// program or its connection. Call it when the interpreter is not the session's own.
    /// </summary>
    public void Detach()
    {
        _commands.Release();
        Host = null;
        EngineCommand = null;
        SendToServer = null;
    }

    /// <summary>The loaded program, which <c>cl_cmd</c> and QuakeC-created commands call into. Null while none is.</summary>
    public CsqcHost? Host { get; set; }

    /// <summary>Offered each command line before the interpreter sees it; true means "mine, done".
    /// For the engine's own commands (see <see cref="DpSignon.HandleCommand"/>).</summary>
    public Func<string, bool>? EngineCommand { get; set; }

    /// <summary>Where <c>cmd &lt;text&gt;</c> goes: a clc_stringcmd to the server.</summary>
    public Action<string>? SendToServer { get; set; }

    public long LinesExecuted { get; private set; }
    public long GameCommands { get; private set; }
    public long MenuCommands { get; private set; }
    public long ForwardedToServer { get; private set; }

    /// <summary>Names the program created with registercommand.</summary>
    public IReadOnlyCollection<string> QcCommands => _qcCommands;

    /// <summary>Command lines that were no command, alias or cvar and that DarkPlaces would not forward either
    /// ("Unknown command"): they went nowhere.</summary>
    public long UnknownCommands { get; private set; }
    /// <summary>Told the name of each such command.</summary>
    public Action<string>? UnknownCommand { get; set; }

    /// <summary>
    /// The end of cmd.c Cmd_ExecuteString for a console of the session's own: the interpreter found no
    /// command, alias or cvar of this name. A DarkPlaces client forwards a fixed list of commands to the
    /// server (<see cref="DpClientCommands"/>: say, kill, status, ...; "cmd" is a command of its own) and
    /// prints <c>Unknown command "x"</c> for everything else. It never forwards what it does not know.
    /// Hook it up with <see cref="ForwardAsDarkPlaces"/>.
    /// </summary>
    public void HandleUnknownCommand(string name, IReadOnlyList<string> argv)
    {
        if (DpClientCommands.IsForwarded(name))
        {
            ForwardedToServer++;
            SendToServer?.Invoke(JoinArguments(argv, 0));
            return;
        }
        UnknownCommands++;
        UnknownCommand?.Invoke(name);
    }

    /// <summary>Makes <see cref="HandleUnknownCommand"/> the interpreter's answer to a command it does not
    /// know. For an interpreter that is the session's own; the Xonotic menu's console has its own handler.</summary>
    public void ForwardAsDarkPlaces() => Interpreter.UnknownCommandHandler = HandleUnknownCommand;

    /// <summary>The command buffer (for its counters: commands waiting, waits, deferred commands).</summary>
    public DpCommandBuffer Buffer => _buffer;

    /// <summary>Cbuf_AddText: queue text. It runs at the next <see cref="Execute"/>; a last line
    /// without a terminator waits for the text that completes it.</summary>
    public void AddText(string text) => _buffer.AddText(text);

    /// <summary>Cbuf_InsertText: queue text in front of what is waiting (a key's bind).</summary>
    public void InsertText(string text) => _buffer.InsertText(text);

    private void TakeProgramText()
    {
        foreach (string text in _services.TakePendingCommands()) _buffer.AddText(text);
    }

    /// <summary>
    /// A client frame begins (host.c Host_Frame's Cbuf_Frame, as far as time goes): deferred commands that have
    /// come due are appended, and what a <c>wait</c> held in the frame before may run again. The owner calls it
    /// once a frame with its clock; the buffer itself runs at the <see cref="Execute"/> calls that follow.
    /// An owner that never calls it (a replay that has no frames) gets a console on which <c>wait</c> ends
    /// one Execute and the next one carries on.
    /// </summary>
    public void NewFrame(double realTime)
    {
        _frameDriven = true;
        _buffer.RunDeferred(realTime);
        _buffer.ReleaseHold();
    }

    /// <summary>Cmd_ExecuteString: run one command now, bypassing the buffer.</summary>
    public void ExecuteNow(string line)
    {
        LinesExecuted++;
        if (EngineCommand?.Invoke(line) == true) return;
        // "cmd" has to pass its arguments on as they were written (see TryForwardVerbatim); everything else
        // is the interpreter's. A line is rarely more than one command, so the split costs nothing.
        if (line.Contains("cmd", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string command in ConfigInterpreter.SplitIntoCommands(line))
                if (!TryForwardVerbatim(command)) Interpreter.ExecuteLine(command);
            return;
        }
        Interpreter.ExecuteLine(line);
    }

    /// <summary>
    /// cl_cmd.c CL_ForwardToServer_f: "we want to strip off "cmd", so just send the args" - Cmd_Args, which
    /// is the REST OF THE LINE as written, quotes included. The interpreter hands a command its arguments
    /// with the quotes already taken off, and putting quotes back only where an argument needs them is not
    /// the same text: Xonotic's client program sends its client-to-server messages as
    /// <c>cmd c2s "&lt;bytes&gt;"</c> and its server program cuts the first and the last character off the
    /// argument text to get at the bytes (lib/net.qh Net_ClientCommand). Without the quotes it cut two
    /// bytes of the message off instead, and every such message - the one that ends a pause among them -
    /// was thrown away as malformed.
    ///
    /// So a command whose first word is exactly <c>cmd</c> is forwarded here from its text. DarkPlaces
    /// expands <c>$</c> references before a command runs (Cmd_PreprocessString); the only one that can be
    /// undone without the interpreter is <c>$$</c>, which is what the program writes for a dollar sign.
    /// A line with any other <c>$</c> is left to the interpreter, as before.
    /// </summary>
    private bool TryForwardVerbatim(string command)
    {
        int i = 0;
        while (i < command.Length && command[i] <= ' ') i++;
        if (command.Length - i < 4 || string.Compare(command, i, "cmd", 0, 3, StringComparison.OrdinalIgnoreCase) != 0 || command[i + 3] > ' ') return false;
        i += 3;
        while (i < command.Length && command[i] <= ' ') i++;
        string text = command[i..].TrimEnd();
        if (text.Length == 0) return true;   // "cmd" by itself sends nothing
        if (text.Contains('$'))
        {
            for (int at = 0; at < text.Length; at++)
            {
                if (text[at] != '$') continue;
                if (at + 1 >= text.Length || text[at + 1] != '$') return false;   // a real reference: the interpreter's
                at++;
            }
            text = text.Replace("$$", "$");
        }
        ForwardedToServer++;
        SendToServer?.Invoke(text);
        return true;
    }

    /// <summary>
    /// Cbuf_Execute: run everything queued, including what the program's localcmd added, and whatever
    /// running that queues in turn - until the buffer is empty or a <c>wait</c> ran, which leaves the rest
    /// for the next frame. Bounded, so a command that re-queues itself costs one frame of work rather
    /// than a hang.
    /// </summary>
    public void Execute()
    {
        if (!_frameDriven) _buffer.ReleaseHold();
        _buffer.Execute(ExecuteNow);
    }

    /// <summary>
    /// The registercommand builtin (Cmd_AddCommand with no engine function): typing the command calls
    /// CSQC_ConsoleCommand with the whole line. A name the interpreter already had is taken over.
    /// </summary>
    public void RegisterQcCommand(string name)
    {
        if (name.Length == 0 || name.Length > 128 || _qcCommands.Count >= 4096 || !_qcCommands.Add(name)) return;
        _commands.Register(Interpreter, name, argv => Host?.ConsoleCommand(JoinArguments(argv, 0)), "console command created by QuakeC");
    }

    /// <summary>
    /// Rebuilds a command line from the interpreter's argument vector. DarkPlaces hands the program
    /// the text as typed; the interpreter only keeps the arguments, so an argument that needs quoting
    /// to survive the program's own tokenizer is quoted again here.
    /// </summary>
    internal static string JoinArguments(IReadOnlyList<string> argv, int first)
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
