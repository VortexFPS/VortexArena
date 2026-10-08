// Port of Base/darkplaces/prvm_cmds.c as far as the menu program's view of the console goes: VM_cvar_type
// (the flags), PRVM_Cvar_ReadOk (private cvars), VM_cvar_set / VM_registercvar (what may be written) and
// VM_localcmd (Cbuf_AddText).
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Menu;

/// <summary>
/// The engine services the menu program runs against: the shared <see cref="LegacyConsole"/> for cvars
/// and commands, the game data for reads, the legacy user folder for writes.
///
/// It differs from <see cref="LegacyQcHost"/> - what a server's client program gets - in what it may
/// touch. menu.dat is part of the player's own Xonotic data and is the thing the player changes
/// settings with, so it sees and sets every cvar DarkPlaces would let it (all but CF_PRIVATE ones, and
/// not read-only ones), and what it writes is the player's configuration. Files are bounded exactly as
/// for any program: reads from the mounted data, writes under one directory, no path that leaves either.
/// </summary>
public sealed class MenuQcHost : IQcHost
{
    private readonly LegacyConsole _console;
    private readonly LegacyQcHost _files;

    /// <param name="print">Console output of the program.</param>
    /// <param name="warning">VM warnings (a builtin called with arguments it refuses).</param>
    public MenuQcHost(LegacyConsole console, Action<string>? print = null, Action<string>? warning = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _files = new LegacyQcHost(console.Cvars, console.Files)
        {
            WriteRoot = console.UserDataDirectory,
            PrintSink = print ?? (_ => { }),
            WarningSink = warning ?? (_ => { }),
            IsPrivateCvar = LegacyConsole.IsPrivateCvar,
        };
        _files.CvarChanged += name => CvarChanged?.Invoke(name);
    }

    public LegacyConsole Console => _console;
    /// <summary>The file half, for a host that needs whole files (a sound, a font).</summary>
    public LegacyQcHost Files => _files;

    /// <summary>A cvar's value changed, by whatever route. Keeps the program's autocvar globals current.</summary>
    public event Action<string>? CvarChanged;

    public void Print(string text) => _files.Print(text);
    public void Warning(string text) => _files.Warning(text);
    public bool Developer => _files.Developer;
    public bool Utf8Enabled => _files.Utf8Enabled;
    /// <summary>host.realtime. Null: the wall clock since this host was made. A test sets it to step the
    /// menu's fades and double-click timing without waiting for them.</summary>
    public Func<double>? Clock { get; set; }
    public double RealTime => Clock?.Invoke() ?? _files.RealTime;

    // ---- cvars -------------------------------------------------------------------------------------

    public bool CvarExists(string name) => name.Length != 0 && _console.Cvars.Has(name) && !LegacyConsole.IsAbsentOnThisPlatform(name);
    // PRVM_Cvar_ReadOk is the builtin's to apply (it asks CvarTypeFlags for the private bit).
    public string CvarString(string name) => CvarExists(name) ? _console.Cvars.GetString(name) : "";
    public float CvarFloat(string name) => CvarExists(name) ? _console.Cvars.GetFloat(name) : 0f;
    public string CvarDefaultString(string name) => CvarExists(name) ? _console.Cvars.GetDefault(name) : "";
    public string CvarDescription(string name) => CvarExists(name) ? _console.Cvars.GetDescription(name) : "";
    public int CvarTypeFlags(string name) => _console.CvarTypeFlags(name);

    public void CvarSet(string name, string value)
    {
        // The builtin has already refused an unknown or read-only cvar; a private one is refused here.
        if (!CvarExists(name) || LegacyConsole.IsPrivateCvar(name)) return;
        _console.Cvars.Set(name, value);
    }

    public bool RegisterCvar(string name, string value, int flags)
    {
        // "check for overlap with a command"
        if (name.Length == 0 || _console.Cvars.Has(name) || _console.Interpreter.CommandNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        _console.Cvars.Register(name, value);
        _console.NoteProgramCvar(name);
        return true;
    }

    public IEnumerable<string> CvarNames(string prefix, string antiPrefix)
    {
        foreach (string name in _console.Cvars.Names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (antiPrefix.Length > 0 && name.StartsWith(antiPrefix, StringComparison.Ordinal)) continue;
            if (LegacyConsole.IsPrivateCvar(name)) continue;
            yield return name;
        }
    }

    // ---- console -----------------------------------------------------------------------------------

    /// <summary>Cbuf_AddText: the text runs when the buffer is next executed, never from inside the builtin.</summary>
    public void LocalCommand(string text) => _console.AddText(text);

    // ---- files -------------------------------------------------------------------------------------

    public Stream? OpenRead(string path) => _files.OpenRead(path);
    public Stream? OpenWrite(string path, bool append) => _files.OpenWrite(path, append);
    public IReadOnlyList<string> Search(string pattern, bool caseInsensitive, string? packFile) => _files.Search(pattern, caseInsensitive, packFile);
    public string WhichPack(string path) => _files.WhichPack(path);
}
