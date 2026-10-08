// Port of the lifetime Base/darkplaces/cmd.c gives a command that belongs to a program: Cmd_AddCommand adds it
// to the one command list of the process, and when the program is gone the command is still there but leads
// nowhere ("client: program is not loaded"). The list here is ConfigInterpreter's, which has no removal either.
using VortexArena.Common.Config;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// The console commands a session puts on an interpreter, registered so that the interpreter does not keep
/// the session alive.
///
/// A session started from the Xonotic menu runs on the MENU's interpreter, which outlives it. A command
/// registered there directly is a delegate into the session - its program, its level, every texture of it -
/// and the interpreter's command table held all of that for as long as the menu stayed open (until the next
/// session registered the same names over it). So a session registers through this instead: the table gets a
/// small relay that looks the handler up here, and <see cref="Release"/> empties the lookup when the session
/// ends. The command names stay known and do nothing, which is what a DarkPlaces command does once the
/// program behind it has been unloaded.
/// </summary>
public sealed class LegacySessionCommands
{
    private Dictionary<string, Action<IReadOnlyList<string>>>? _handlers = new(StringComparer.Ordinal);

    /// <summary>How many commands are registered and still lead somewhere (0 after <see cref="Release"/>).</summary>
    public int Count => _handlers?.Count ?? 0;

    /// <summary>
    /// Registers <paramref name="name"/> on <paramref name="interpreter"/> (taking over a command of that
    /// name, as ConfigInterpreter.RegisterCommand does). Registering the same name again replaces the handler.
    /// After <see cref="Release"/> this does nothing.
    /// </summary>
    public void Register(ConfigInterpreter interpreter, string name, Action<IReadOnlyList<string>> handler, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(handler);
        if (_handlers is not { } handlers) return;
        bool known = handlers.ContainsKey(name);
        handlers[name] = handler;
        if (known) return;   // the relay for this name is already in the interpreter's table
        LegacySessionCommands relay = this;
        interpreter.RegisterCommand(name, argv => relay.Run(name, argv), description);
    }

    private void Run(string name, IReadOnlyList<string> argv)
    {
        if (_handlers is { } handlers && handlers.TryGetValue(name, out Action<IReadOnlyList<string>>? handler)) handler(argv);
    }

    /// <summary>The session is over: every command registered here now does nothing, and nothing it led to is held.</summary>
    public void Release() => _handlers = null;
}
