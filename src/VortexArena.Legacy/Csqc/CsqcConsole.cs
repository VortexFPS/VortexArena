// Port of the console plumbing a client program depends on: Base/darkplaces/cmd.c Cbuf_AddText /
// Cbuf_Execute and Cmd_CL_Callback (commands created by QuakeC), prvm_edict.c PRVM_GameCommand
// (cl_cmd, menu_cmd) and cl_cmd.c CL_ForwardToServer_f (cmd).
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
/// is that step. Each complete command line is first offered to <see cref="EngineCommand"/> (the
/// handful the engine itself owns: csqc_progcrc, cl_downloadbegin, ...) and otherwise handed to the
/// <see cref="ConfigInterpreter"/>, where the three commands that lead back into the program live:
/// <c>cl_cmd</c> (GameCommand), anything the program created with registercommand
/// (CSQC_ConsoleCommand), and <c>cmd</c> (forward to the server).
/// </summary>
public sealed class CsqcConsole
{
    private readonly LegacyQcHost _services;
    private readonly DpStuffTextBuffer _buffer = new();
    private readonly List<string> _lines = new();
    private readonly HashSet<string> _qcCommands = new(StringComparer.OrdinalIgnoreCase);

    public CsqcConsole(ConfigInterpreter interpreter, LegacyQcHost services)
    {
        Interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _services = services ?? throw new ArgumentNullException(nameof(services));

        interpreter.RegisterCommand("cl_cmd", argv =>
        {
            // PRVM_GameCommand passes Cmd_Args: everything after the command word.
            GameCommands++;
            Host?.GameCommand(JoinArguments(argv, 1));
        }, "calls the client QC function GameCommand with the supplied string as argument");

        interpreter.RegisterCommand("cmd", argv =>
        {
            string text = JoinArguments(argv, 1);
            if (text.Length == 0) return;
            ForwardedToServer++;
            SendToServer?.Invoke(text);
        }, "send a console commandline to the server (used by some mods)");

        interpreter.RegisterCommand("cl_particles_reloadeffects", argv => Host?.ReloadEffects(argv.Count > 1 ? argv[1] : null),
            "reloads effectinfo.txt and maps/levelname_effectinfo.txt (where levelname is the current map) if parameter is given, loads from custom file (no levelname_effectinfo are loaded in this case)");

        // Xonotic's client program issues a few menu_cmd commands (to sync a menu that is not running
        // here). With no menu program DarkPlaces prints a complaint and carries on; so does this. When a
        // menu program IS running on this interpreter (Menu/MenuHost.cs registers menu_cmd for itself, on
        // the console a session started from the Xonotic menu shares with it), its command is left alone.
        if (!interpreter.CommandNames.Contains("menu_cmd"))
            interpreter.RegisterCommand("menu_cmd", _ => MenuCommands++,
                "calls the menu QC function GameCommand with the supplied string as argument");
    }

    public ConfigInterpreter Interpreter { get; }

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

    /// <summary>Cbuf_AddText: queue text. It runs at the next <see cref="Execute"/>; a last line
    /// without a terminator waits for the text that completes it.</summary>
    public void AddText(string text)
    {
        if (!string.IsNullOrEmpty(text)) _buffer.Add(text, _lines);
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
    /// running that queues in turn. Bounded, so a command that re-queues itself costs one frame of
    /// work rather than a hang (DarkPlaces relies on its <c>wait</c> command for the same thing).
    /// </summary>
    public void Execute()
    {
        for (int round = 0; round < 16; round++)
        {
            foreach (string text in _services.TakePendingCommands()) AddText(text);
            if (_lines.Count == 0) return;
            string[] lines = _lines.ToArray();
            _lines.Clear();
            foreach (string line in lines) ExecuteNow(line);
        }
    }

    /// <summary>
    /// The registercommand builtin (Cmd_AddCommand with no engine function): typing the command calls
    /// CSQC_ConsoleCommand with the whole line. A name the interpreter already had is taken over.
    /// </summary>
    public void RegisterQcCommand(string name)
    {
        if (name.Length == 0 || name.Length > 128 || _qcCommands.Count >= 4096 || !_qcCommands.Add(name)) return;
        Interpreter.RegisterCommand(name, argv => Host?.ConsoleCommand(JoinArguments(argv, 0)), "console command created by QuakeC");
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
