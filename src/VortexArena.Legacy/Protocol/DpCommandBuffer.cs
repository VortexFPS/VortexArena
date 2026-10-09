// Port of Base/darkplaces/cmd.c: the command buffer (Cbuf_AddText, Cbuf_InsertText, Cbuf_Execute,
// Cbuf_Execute_Deferred, Cbuf_Frame), Cmd_Wait_f, Cmd_Defer_f, and the part of Cmd_ExecuteAlias / Cmd_Exec
// that matters for order: an alias's body and an executed file are INSERTED at the front of the buffer,
// so a "wait" inside one holds the rest of it, and everything behind it, until the next frame.
using System.Globalization;
using VortexArena.Common.Config;

namespace VortexArena.Legacy.Protocol;

/// <summary>
/// DarkPlaces' console command buffer, once, for every console legacy mode has: the Xonotic menu's
/// (<see cref="VortexArena.Legacy.Menu.LegacyConsole"/>) and a session's own
/// (<see cref="VortexArena.Legacy.Csqc.CsqcConsole"/>).
///
/// <para>Text is appended (<see cref="AddText"/>: what a server stuffs, what a program's localcmd adds, what
/// the player types) or put in front (<see cref="InsertText"/>: a key's bind). Nothing runs when it is added.
/// <see cref="Execute"/> runs commands from the front until the buffer is empty or a <c>wait</c> ran:
/// "Causes execution of the remainder of the command buffer to be delayed until next frame. This allows
/// commands like: bind g "impulse 5 ; +attack ; wait ; -attack ; impulse 2"". <see cref="Frame"/> is
/// Cbuf_Frame: once a frame, deferred commands that have come due are appended, the hold of the frame before
/// is lifted, and the buffer runs.</para>
///
/// <para><b>wait is a LOCAL command.</b> It never leaves the client. A console that does not know it sends it
/// to the server as an unknown command, where Xonotic's command flood control counts it against the player.</para>
///
/// <para><b>Aliases.</b> The interpreter runs an alias's body in place, which is the same order as inserting
/// it at the front - until a <c>wait</c> runs inside it. Then the interpreter hands back every command of the
/// body that has not run (<see cref="ConfigInterpreter.HeldCommandSink"/>), innermost alias first, and they go
/// to the front of the buffer with the alias's arguments, to run next frame before anything else.</para>
/// </summary>
public sealed class DpCommandBuffer
{
    /// <summary>Commands the buffer holds at most; text past that is dropped (DarkPlaces: "input too large").</summary>
    public const int MaxCommands = 65536;
    /// <summary>Deferred commands held at most.</summary>
    public const int MaxDeferred = 256;
    /// <summary>Commands run by one <see cref="Execute"/> at most: a command that queues itself again costs
    /// a frame of work, not a hang (DarkPlaces has its own runaway check).</summary>
    public const int MaxPerExecute = 16384;

    private readonly struct Entry
    {
        public Entry(string text, IReadOnlyList<string>? aliasArguments)
        {
            Text = text;
            AliasArguments = aliasArguments;
        }
        public readonly string Text;
        /// <summary>Non-null for the rest of an alias's body that a wait held back: the alias's arguments.</summary>
        public readonly IReadOnlyList<string>? AliasArguments;
    }

    private readonly ConfigInterpreter _interpreter;
    private readonly DpStuffTextBuffer _parser = new();
    private readonly List<string> _parsed = new();
    private readonly List<Entry> _queue = new();
    private readonly List<Entry> _held = new();
    private readonly List<(double Delay, string Text)> _deferred = new();
    private double _deferredOldTime = double.NaN;
    private bool _wait;
    private bool _executing;

    [ThreadStatic] private static DpCommandBuffer? t_active;

    public DpCommandBuffer(ConfigInterpreter interpreter) => _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));

    /// <summary>The buffer whose command is running on this thread, or null. <c>wait</c> and <c>defer</c> act on it.</summary>
    public static DpCommandBuffer? Active => t_active;

    /// <summary>Asked for more text before each command is taken (a program's localcmd output, which
    /// DarkPlaces appends the moment the builtin runs): the callee calls <see cref="AddText"/>.</summary>
    public Action? MoreText { get; set; }

    /// <summary>
    /// Whether <see cref="Execute"/> runs a last line that has no terminator yet, as Cbuf_Execute does
    /// ("current->pending = false"). True for a console that runs its buffer once a frame. A session's console
    /// runs its buffer after every server message, and a server may send one command in two messages: there
    /// the half line has to wait for its end, so that console leaves this off.
    /// </summary>
    public bool RunUnterminatedLine { get; set; }

    /// <summary>A <c>wait</c> ended the last <see cref="Execute"/>; what is left runs after <see cref="ReleaseHold"/>
    /// (the next frame).</summary>
    public bool HeldForNextFrame { get; private set; }
    /// <summary>Commands waiting, a last line that has no terminator yet included.</summary>
    public int Pending => _queue.Count + (_parser.Pending.Length != 0 ? 1 : 0);
    /// <summary>Deferred commands waiting for their time.</summary>
    public int DeferredCount => _deferred.Count;
    /// <summary>Times a <c>wait</c> held the buffer.</summary>
    public long Waits { get; private set; }
    /// <summary>Commands run.</summary>
    public long Executed { get; private set; }
    /// <summary>Commands dropped because the buffer was full.</summary>
    public long Dropped { get; private set; }

    /// <summary>Cbuf_AddText: append. A last line without a terminator can still be completed by text added
    /// later (see <see cref="RunUnterminatedLine"/> for how long it waits).</summary>
    public void AddText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _parsed.Clear();
        _parser.Add(text, _parsed);
        foreach (string command in _parsed)
        {
            if (_queue.Count >= MaxCommands) { Dropped++; continue; }
            _queue.Add(new Entry(command, null));
        }
        _parsed.Clear();
    }

    /// <summary>Cbuf_InsertText: put text in front of what is waiting. Nothing of it stays pending: its last
    /// line is complete whether or not it ends in a newline ("when prepending to the buffer it never makes
    /// sense to leave node(s) in the pending state").</summary>
    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        DpStuffTextBuffer parser = new();
        List<string> commands = new();
        parser.Add(text, commands);
        parser.Add("\n", commands);
        if (_queue.Count + commands.Count > MaxCommands) { Dropped += commands.Count; return; }
        List<Entry> entries = new(commands.Count);
        foreach (string command in commands) entries.Add(new Entry(command, null));
        _queue.InsertRange(0, entries);
    }

    private bool CompletePendingLine()
    {
        if (_parser.Pending.Length == 0) return false;
        AddText("\n");
        return _queue.Count != 0;
    }

    /// <summary>Cmd_Wait_f: "cbuf->wait = true".</summary>
    public void Wait() => _wait = true;

    /// <summary>The frame after a <c>wait</c> has begun: what was held may run.</summary>
    public void ReleaseHold() => HeldForNextFrame = false;

    /// <summary>Cmd_Defer_f with two arguments: run <paramref name="text"/> after <paramref name="seconds"/>.</summary>
    public bool Defer(double seconds, string text)
    {
        if (string.IsNullOrEmpty(text) || _deferred.Count >= MaxDeferred) return false;
        _deferred.Add((double.IsFinite(seconds) ? seconds : 0, text));
        return true;
    }

    public void ClearDeferred() => _deferred.Clear();

    /// <summary>The deferred commands and the seconds each still has to wait ("defer" by itself lists them).</summary>
    public IReadOnlyList<(double Delay, string Text)> Deferred => _deferred;

    /// <summary>Everything waiting is forgotten: commands, a pending line, deferred commands, a hold.</summary>
    public void Clear()
    {
        _queue.Clear();
        _held.Clear();
        _deferred.Clear();
        _parser.Clear();
        _wait = false;
        HeldForNextFrame = false;
    }

    /// <summary>
    /// Cbuf_Execute_Deferred: every deferred command's delay runs down by the time that has passed (at most
    /// once every 1/128 s; a jump of more than half an hour, or backwards, counts as none), and one that
    /// reaches zero is appended to the buffer as whole commands of its own.
    /// </summary>
    public void RunDeferred(double realTime)
    {
        if (double.IsNaN(_deferredOldTime) || realTime - _deferredOldTime < 0 || realTime - _deferredOldTime > 1800) _deferredOldTime = realTime;
        double eat = realTime - _deferredOldTime;
        if (eat < 1.0 / 128.0) return;
        _deferredOldTime = realTime;
        for (int i = 0; i < _deferred.Count; i++)
        {
            (double delay, string text) = _deferred[i];
            delay -= eat;
            if (delay <= 0)
            {
                // "parse deferred string and append its cmdstring(s)" with "pending = false": terminated on both
                // sides, so it neither completes a half line that was waiting nor runs into the next deferred one.
                AddText("\n" + text + "\n");
                _deferred.RemoveAt(i--);
            }
            else _deferred[i] = (delay, text);
        }
    }

    /// <summary>Cbuf_Frame: deferred commands that are due, then the buffer - a new frame, so a hold is lifted.</summary>
    public void Frame(double realTime, Action<string> run)
    {
        RunDeferred(realTime);
        ReleaseHold();
        Execute(run);
    }

    /// <summary>
    /// Cbuf_Execute: run commands from the front until the buffer is empty or a <c>wait</c> ran. Does nothing
    /// while a hold of this frame stands (<see cref="HeldForNextFrame"/>) and nothing when called from inside
    /// one of its own commands. <paramref name="run"/> is Cmd_ExecuteString for one command.
    /// </summary>
    public void Execute(Action<string> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_executing || HeldForNextFrame) return;
        DpCommandBuffer? outer = t_active;
        Func<bool>? outerHold = _interpreter.HoldRequested;
        Action<string, IReadOnlyList<string>?>? outerSink = _interpreter.HeldCommandSink;
        t_active = this;
        _executing = true;
        _interpreter.HoldRequested = () => _wait;
        _interpreter.HeldCommandSink = (command, arguments) => _held.Add(new Entry(command, arguments));
        try
        {
            _wait = false;
            for (int budget = MaxPerExecute; budget > 0; budget--)
            {
                MoreText?.Invoke();
                // "current->pending = false": a last line that has no terminator yet runs when the buffer gets
                // to it. (It waits only for text added before the buffer runs: a program writes one command
                // with several localcmd calls.)
                if (_queue.Count == 0 && !(RunUnterminatedLine && CompletePendingLine())) break;
                Entry entry = _queue[0];
                _queue.RemoveAt(0);
                Executed++;
                _held.Clear();
                if (entry.AliasArguments is null) run(entry.Text);
                else _interpreter.ExecuteCommand(entry.Text, entry.AliasArguments);
                if (_held.Count != 0)
                {
                    // The rest of the alias (or file) a wait stopped: in front of everything, innermost first.
                    if (_queue.Count + _held.Count <= MaxCommands) _queue.InsertRange(0, _held);
                    else Dropped += _held.Count;
                    _held.Clear();
                }
                if (_wait)
                {
                    // "Skip out while text still remains in buffer, leaving it for next frame"
                    _wait = false;
                    HeldForNextFrame = true;
                    Waits++;
                    break;
                }
            }
        }
        finally
        {
            _interpreter.HoldRequested = outerHold;
            _interpreter.HeldCommandSink = outerSink;
            _executing = false;
            t_active = outer;
        }
    }

    /// <summary>
    /// The handlers of <c>wait</c> and <c>defer</c>, for whoever owns the interpreter's command table to
    /// register. Both act on the buffer that is executing (<see cref="Active"/>) - on a console two buffers
    /// share, a server's "wait" holds the server's text, not the player's - and <c>defer</c> falls back to
    /// <paramref name="home"/> when a command is run outside any buffer.
    /// </summary>
    public static void Handlers(DpCommandBuffer home, Action<string> print, out Action<IReadOnlyList<string>> wait, out Action<IReadOnlyList<string>> defer)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(print);
        wait = _ => t_active?.Wait();
        defer = argv =>
        {
            DpCommandBuffer buffer = t_active ?? home;
            if (argv.Count == 1)
            {
                if (buffer._deferred.Count == 0) print("No commands are pending.\n");
                foreach ((double delay, string text) in buffer._deferred)
                    print(string.Create(CultureInfo.InvariantCulture, $"-> In {delay,9:0.00}: {text}\n"));
            }
            else if (argv.Count == 2 && argv[1].Equals("clear", StringComparison.OrdinalIgnoreCase)) buffer.ClearDeferred();
            else if (argv.Count == 3 && argv[2].Length != 0)
                buffer.Defer(double.TryParse(argv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? seconds : 0, argv[2]);
            else print("usage: defer <seconds> <command>\n       defer clear\n");
        };
    }

    public const string WaitHelp = "make script execution wait for next rendered frame";
    public const string DeferHelp = "execute a command in the future";
}

/// <summary>
/// Which console commands a DarkPlaces client sends to the server, and which it does not.
///
/// <para>cmd.c Cmd_ExecuteString: a command line is a command, an alias or a cvar; anything else prints
/// <c>Unknown command "x"</c> and goes nowhere. (Quake forwarded every unknown word to the server; this
/// DarkPlaces does not.) What reaches the server is: <c>cmd &lt;text&gt;</c> (cl_cmd.c CL_ForwardToServer_f),
/// the commands registered with CF_SERVER_FROM_CLIENT (sv_ccmds.c, sv_main.c: Cmd_CL_Callback forwards them,
/// name and arguments), and the player's settings a server keeps a copy of (CF_USERINFO: cl_cmd.c
/// CL_SetInfo sends "name x", "color a b", ...). Xonotic's own client commands are aliases of "cmd ..."
/// (commands.cfg: join, spectate, ready, selectteam, ...).</para>
/// </summary>
public static class DpClientCommands
{
    private static readonly HashSet<string> s_forwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        // CF_SERVER_FROM_CLIENT (sv_ccmds.c SV_InitOperatorCommands, sv_main.c)
        "status", "say", "say_team", "tell", "pause", "ping", "pings", "prespawn", "spawn", "begin",
        "god", "notarget", "fly", "noclip", "give", "kill", "ent_create", "ent_remove_all", "ent_remove",
        "download", "sv_startdownload",
        // CF_USERINFO: sent by CL_SetInfo when the player changes them
        "name", "color", "rate", "rate_burstsize", "pmodel", "playermodel", "playerskin",
    };

    /// <summary>True if a DarkPlaces client forwards a command of this name to the server it is connected to.</summary>
    public static bool IsForwarded(string name) => s_forwarded.Contains(name);

    /// <summary>The names, for a listing.</summary>
    public static IReadOnlyCollection<string> Forwarded => s_forwarded;
}
