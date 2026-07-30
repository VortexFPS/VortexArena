using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Config;

/// <summary>
/// A Darkplaces-faithful console <em>config interpreter</em> — the C# successor to the engine command
/// buffer (DP <c>Cbuf_AddText</c>/<c>Cmd_ExecuteString</c>) plus the cvar/alias commands it dispatches
/// (<c>set</c>/<c>seta</c>/<c>alias</c>/<c>exec</c> in DP <c>cmd.c</c> + <c>cvar.c</c>). It reads the stock
/// Xonotic <c>.cfg</c> files (<c>xonotic-server.cfg</c> → <c>balance-*.cfg</c> / <c>physics*.cfg</c> /
/// <c>gametypes-server.cfg</c> / <c>mutators.cfg</c> / …) and stamps their thousands of <c>set</c>/bare cvar
/// assignments into an <see cref="ICvarService"/>, so the ~460 live <c>GetFloat()/GetString()</c> reads
/// scattered across the gameplay layer return <strong>authentic</strong> Xonotic values instead of the
/// hand-curated defaults table.
///
/// <para>Grammar handled (the DP console subset the configs actually use):</para>
/// <list type="bullet">
///   <item><c>// line</c> and <c>/* block */</c> comments (outside quotes).</item>
///   <item>Double-quoted tokens with <c>\"</c>/<c>\\</c>/<c>\n</c>/<c>\t</c> escapes (cvar descriptions).</item>
///   <item><c>;</c> and newlines as command separators (outside quotes).</item>
///   <item><c>set</c>/<c>seta</c>/<c>set_temp</c>/<c>seta_temp</c> <c>name value ["desc"]</c> — create-or-update.</item>
///   <item>bare <c>name value</c> — direct cvar assignment (how <c>physicsX.cfg</c> sets <c>sv_*</c>), guarded by a
///         small denylist so engine <em>commands</em> (<c>bind</c>, <c>map</c>, …) don't become junk cvars.</item>
///   <item><c>exec file.cfg</c> — recursive include with a reentrancy/cycle guard and depth cap.</item>
///   <item><c>alias</c>/<c>unalias</c> + alias invocation with argument substitution
///         (<c>$1</c>..<c>$9</c>, <c>$*</c>, <c>${* asis}</c>) and <c>$cvar</c>/<c>${cvar}</c> expansion, <c>$$</c>→<c>$</c>.</item>
/// </list>
///
/// Godot-free (ADR-0008) and dependency-light: file access is an injected
/// <c>Func&lt;string,string?&gt;</c> so the same interpreter serves disk-backed tests and the VFS-backed host.
/// Robust by construction — every command runs under try/catch and unknown commands are counted, not fatal,
/// so a stray client/render directive never aborts the gameplay-cvar load.
/// </summary>
public sealed class ConfigInterpreter
{
    private readonly ICvarService _cvars;
    private readonly Func<string, string?> _readFile;

    /// <summary>Alias table (DP <c>cmd_alias</c> list): name → raw body text (expanded on invocation).</summary>
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Host-registered commands (e.g. a <c>bind</c> sink) consulted before alias/cvar fallback.</summary>
    private readonly Dictionary<string, Action<IReadOnlyList<string>>> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files currently on the <c>exec</c> stack — prevents infinite reentrant include recursion.</summary>
    private readonly HashSet<string> _execStack = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _diagnostics = new();
    private int _aliasDepth;
    private long _commandBudget;

    // Caps (generous; only there to stop a pathological alias/exec loop, never hit by real configs).
    private const int MaxExecDepth = 48;
    private const int MaxAliasDepth = 64;
    private const long MaxCommands = 2_000_000;
    private const int MaxDiagnostics = 64;

    /// <summary>
    /// DP commands that legitimately take trailing arguments but are <em>not</em> cvars — used so a bare
    /// <c>name value…</c> line that is actually one of these doesn't get mis-recorded as a cvar. Engine cvars
    /// set bare (<c>sv_gravity 800</c>, <c>gameversion 806</c>) are <em>not</em> here, so they assign normally.
    /// </summary>
    private static readonly HashSet<string> NonCvarCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "bind", "unbind", "unbindall", "in_bind", "in_bindmap", "in_releaseall", "bindlist",
        "sv_cmd", "cl_cmd", "menu_cmd", "cmd", "rcon", "rcon_secure",
        "map", "changelevel", "gotomap", "gametype", "kill", "give", "god", "noclip",
        "say", "say_team", "tell", "name", "color", "playermodel", "playerskin",
        "kick", "ban", "banlist", "status", "quit", "disconnect", "connect", "reconnect",
        "defer", "wait", "echo", "toggle", "cycle", "inc", "dec", "play", "play2", "playall",
        "cd", "sv_startdownload", "prvm_language", "fpredict", "fdisconnect", "togglemenu",
        "alias", "unalias", "exec", "set", "seta", // builtins (already handled; listed for completeness)
    };

    public ConfigInterpreter(ICvarService cvars, Func<string, string?> readFile)
    {
        _cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        _readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
        _commandBudget = MaxCommands;
    }

    // =============================================================================================
    // diagnostics (so a host/test can audit what loaded and what was skipped)
    // =============================================================================================

    /// <summary>Count of cvar assignments performed (<c>set</c>/<c>seta</c> + bare). The headline "it worked" number.</summary>
    public int CvarsAssigned { get; private set; }

    /// <summary>Count of <c>alias</c> definitions processed.</summary>
    public int AliasesDefined { get; private set; }

    /// <summary>Count of <c>.cfg</c> files successfully exec'd (including nested includes).</summary>
    public int FilesExecuted { get; private set; }

    /// <summary>Count of <c>exec</c> targets that the file-reader couldn't resolve.</summary>
    public int FilesMissing { get; private set; }

    /// <summary>Count of single-token lines that matched no builtin/alias/cvar-assignment (render/client cmds, etc.).</summary>
    public int UnknownCommands { get; private set; }

    /// <summary>A bounded sample of notable events (missing files, cycles, first few unknown commands) for logging.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>The current alias table (read-only) — e.g. to inspect <c>if_client</c>/<c>if_dedicated</c> state.</summary>
    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    /// <summary>The host-registered command names (DP <c>Cmd_AddCommand</c> table) — for console Tab-completion.</summary>
    public IReadOnlyCollection<string> CommandNames => _commands.Keys;

    /// <summary>
    /// Optional host catch-all for a line that matched no builtin / registered command / alias / bare cvar
    /// assignment — the C# successor to DP's "forward unknown commands to the server" (<c>Cmd_ForwardToServer</c>).
    /// Receives <c>(name, argv)</c> with <paramref>argv</paramref> already <c>$</c>-expanded and tokenized.
    /// It fires for exactly the gameplay/engine command surface: a single bare token (<c>kill</c>) OR a
    /// denylisted multi-token line (<c>say hello</c>) — a non-denylisted <c>name value</c> line is still a cvar
    /// assignment and never routed here. Null during config load, so the bulk cfg exec is byte-for-byte unchanged
    /// (unknowns are merely counted). The console sets this to its router into the live <see cref="ICvarService"/>'s
    /// world / the remote string-command channel.
    /// </summary>
    public Action<string, IReadOnlyList<string>>? UnknownCommandHandler { get; set; }

    /// <summary>
    /// Optional host callback fired after a <c>seta</c>/<c>seta_temp</c> assignment with the cvar name — the C#
    /// successor to DP's <c>CVAR_SAVE</c> archive flag that <c>seta</c> sets (a plain <c>set</c> does not).
    /// Wired BOTH at boot config load (ConfigLoader → MenuState, so the shipped tree's <c>seta</c>-vs-<c>set</c>
    /// choice decides which cvars may persist to <c>user://config.cfg</c> — DP's CF_ARCHIVE provenance) and by
    /// the console (a typed <c>seta</c> archives, like DP's <c>Cvar_SetA_f</c>). Null on stores that are never
    /// saved (a private world store), where archiving would be meaningless.
    /// </summary>
    public Action<string>? CvarArchiveHook { get; set; }

    private void Diag(string message)
    {
        if (_diagnostics.Count < MaxDiagnostics)
            _diagnostics.Add(message);
    }

    /// <summary>
    /// Register a host command handler (DP <c>Cmd_AddCommand</c>) consulted before the alias/cvar fallback —
    /// e.g. a <c>bind</c> collector, or a test probe. Returning normally consumes the command.
    /// </summary>
    public void RegisterCommand(string name, Action<IReadOnlyList<string>> handler)
        => _commands[name] = handler;

    /// <summary>Pre-seed an alias (DP engine aliases). Body is stored raw and expanded on invocation.</summary>
    public void DefineAlias(string name, string body) => _aliases[name] = body;

    // =============================================================================================
    // public execution entry points
    // =============================================================================================

    /// <summary>
    /// Resolve <paramref name="path"/> through the file-reader and execute it as a config script (DP
    /// <c>exec</c>). Guards against reentrant include cycles and a depth blow-out. Returns false (and records a
    /// diagnostic) when the file can't be read; a missing include is never fatal.
    /// </summary>
    public bool ExecuteFile(string path)
    {
        if (_execStack.Count >= MaxExecDepth)
        {
            Diag($"exec depth limit ({MaxExecDepth}) reached at '{path}'");
            return false;
        }
        if (!_execStack.Add(path))
        {
            Diag($"exec cycle skipped: '{path}'");
            return false;
        }
        try
        {
            string? text;
            try { text = _readFile(path); }
            catch (Exception ex) { Diag($"exec read error '{path}': {ex.Message}"); FilesMissing++; return false; }

            if (text is null)
            {
                Diag($"exec file not found: '{path}'");
                FilesMissing++;
                return false;
            }
            FilesExecuted++;
            ExecuteScript(text);
            return true;
        }
        finally
        {
            _execStack.Remove(path);
        }
    }

    /// <summary>Execute a whole config script (a <c>.cfg</c> file's text): split into commands and run each.</summary>
    public void ExecuteScript(string text)
    {
        foreach (string command in SplitIntoCommands(text))
            ExecuteCommandLine(command, null);
    }

    /// <summary>Execute a single console line (may contain <c>;</c>-separated commands and a trailing comment).</summary>
    public void ExecuteLine(string line)
    {
        foreach (string command in SplitIntoCommands(line))
            ExecuteCommandLine(command, null);
    }

    // =============================================================================================
    // command dispatch
    // =============================================================================================

    /// <summary>
    /// Execute one already-separated command string (comments/<c>;</c> already stripped). <paramref name="args"/>
    /// is non-null only when running an alias body — it carries the alias's argument vector (index 0 = alias
    /// name) for <c>$1</c>/<c>$*</c> substitution.
    /// </summary>
    private void ExecuteCommandLine(string command, IReadOnlyList<string>? args)
    {
        if (--_commandBudget < 0)
            return; // runaway guard; real configs are nowhere near the budget

        // Peek the raw first token WITHOUT expansion. `alias` must protect its body from $-expansion at
        // definition time (so `alias if_client "${* asis}"` stores the literal body), whereas every other
        // command expands its whole line. The command name itself may still need expansion
        // (e.g. `_sv_dedicated_${sv_dedicated}`), so only treat it as a literal `alias` define when the raw
        // first token is exactly `alias`.
        List<string> rawArgv = Tokenize(command);
        if (rawArgv.Count == 0)
            return;

        if (rawArgv[0].Equals("alias", StringComparison.OrdinalIgnoreCase))
        {
            CmdAlias(rawArgv, args);
            return;
        }

        // Normal path: expand the full line (cvar refs, alias args, $$→$), then re-tokenize and dispatch.
        string expanded = Expand(command, args);
        List<string> argv = Tokenize(expanded);
        if (argv.Count == 0)
            return;

        Dispatch(argv, args);
    }

    private void Dispatch(List<string> argv, IReadOnlyList<string>? args)
    {
        string cmd = argv[0];

        switch (cmd.ToLowerInvariant())
        {
            case "set":
            case "seta":
            case "set_temp":
            case "seta_temp":
            case "setp": // DP "set private" — same store for us
                CmdSet(argv);
                return;
            case "alias": // reached only via an expanded alias body that produced an `alias …` line
                CmdAlias(argv, args);
                return;
            case "unalias":
                if (argv.Count >= 2) _aliases.Remove(argv[1]);
                return;
            case "exec":
                if (argv.Count >= 2) ExecuteFile(argv[1]);
                return;
            case "unset":
            case "cvar_reset":
            case "reset":
                // No erase on the facade; reset to empty so a later read sees "unset" rather than a stale value.
                if (argv.Count >= 2) SafeSet(argv[1], "");
                return;
        }

        // Host-registered command (bind sink, etc.).
        if (_commands.TryGetValue(cmd, out Action<IReadOnlyList<string>>? handler))
        {
            handler(argv);
            return;
        }

        // Alias invocation (DP: aliases shadow cvars but not built-in commands).
        if (_aliases.TryGetValue(cmd, out string? body))
        {
            RunAlias(cmd, body, argv);
            return;
        }

        // Bare cvar assignment: `name value` (how physicsX.cfg / many engine cvars are set). DP assigns only
        // when `name` is a known cvar; we relax that to "anything not on the command denylist" so the dozens of
        // bare `sv_*`/`g_*` movement cvars register, while real commands (bind/map/…) are ignored.
        if (argv.Count >= 2 && !NonCvarCommands.Contains(cmd))
        {
            SafeSet(cmd, argv[1]);
            CvarsAssigned++;
            return;
        }

        // Not a builtin, registered command, alias, or bare cvar assignment — i.e. the gameplay/engine command
        // surface (a single bare token like `kill`, or a denylisted multi-token like `say hello`). Hand it to the
        // host's catch-all (the console's router to the live server / remote stringcmd) if one is wired. During
        // config load the hook is null, so this stays the original "count and move on" (the cfg exec is unchanged).
        if (UnknownCommandHandler is not null)
        {
            UnknownCommandHandler(cmd, argv);
            return;
        }

        // Single bare token, or a known non-cvar command we don't model: count and move on.
        UnknownCommands++;
        if (_diagnostics.Count < MaxDiagnostics && !NonCvarCommands.Contains(cmd))
            Diag($"unknown command: '{cmd}'");
    }

    /// <summary><c>set</c>/<c>seta</c> <c>name value ["description"]</c> (DP <c>Cvar_Set</c>, create-or-update).</summary>
    private void CmdSet(List<string> argv)
    {
        if (argv.Count < 2)
            return;
        string name = argv[1];
        string value = argv.Count >= 3 ? argv[2] : "";
        SafeSet(name, value);
        CvarsAssigned++;
        // `seta`/`seta_temp` carry DP's CVAR_SAVE (archive) bit; let the host persist them (no-op during cfg load).
        if (argv[0].StartsWith("seta", StringComparison.OrdinalIgnoreCase))
            CvarArchiveHook?.Invoke(name);
    }

    /// <summary>
    /// <c>alias name "body"</c> (DP <c>Cmd_Alias_f</c>). The body is stored raw (un-expanded) so deferred
    /// <c>$*</c>/<c>$1</c>/<c>$$</c> tokens expand when the alias runs, not when it's defined. Only the alias
    /// <em>name</em> is expanded (covers <c>alias _sv_dedicated_${x} …</c>, though stock configs don't use it).
    /// </summary>
    private void CmdAlias(List<string> rawArgv, IReadOnlyList<string>? args)
    {
        if (rawArgv.Count < 2)
            return;
        string name = rawArgv[1].IndexOf('$') >= 0 ? Expand(rawArgv[1], args) : rawArgv[1];
        if (string.IsNullOrEmpty(name))
            return;

        if (rawArgv.Count < 3)
        {
            _aliases[name] = "";
        }
        else
        {
            // DP joins everything after the name; near-universally there's a single quoted body token.
            string body = rawArgv.Count == 3 ? rawArgv[2] : string.Join(' ', rawArgv.GetRange(2, rawArgv.Count - 2));
            // Collapse `$$`→`$` at definition (DP's one-level deferral): `alias foo "cmd $$*"` stores `cmd $*`,
            // so the `$*` expands against foo's own arguments when foo runs — not at definition. Other refs
            // (`$cvar`, `$1`, `${* asis}`) are left intact for invocation-time expansion.
            _aliases[name] = CollapseDollarEscapes(body);
        }
        AliasesDefined++;
    }

    /// <summary>
    /// Invoke an alias: expand its body with the call's argument vector and execute the resulting command(s).
    /// Bounded by <see cref="MaxAliasDepth"/> against mutually-recursive aliases.
    /// </summary>
    private void RunAlias(string name, string body, IReadOnlyList<string> callArgs)
    {
        if (string.IsNullOrEmpty(body))
            return;
        if (_aliasDepth >= MaxAliasDepth)
        {
            Diag($"alias recursion limit at '{name}'");
            return;
        }
        _aliasDepth++;
        try
        {
            foreach (string command in SplitIntoCommands(body))
                ExecuteCommandLine(command, callArgs);
        }
        finally
        {
            _aliasDepth--;
        }
    }

    /// <summary>Collapse <c>$$</c>→<c>$</c> (DP's escape de-doubling) without touching single-<c>$</c> refs.</summary>
    private static string CollapseDollarEscapes(string s)
    {
        if (s.IndexOf("$$", StringComparison.Ordinal) < 0)
            return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '$' && i + 1 < s.Length && s[i + 1] == '$')
            {
                sb.Append('$');
                i++; // consume the second '$'
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Assign a cvar, swallowing any facade error (a ReadOnly cvar, etc.) so the load never aborts.</summary>
    private void SafeSet(string name, string value)
    {
        try { _cvars.Set(name, value); }
        catch (Exception ex) { Diag($"set '{name}' failed: {ex.Message}"); }
    }

    // =============================================================================================
    // $-expansion (DP Cmd_PreprocessString — the subset the configs use)
    // =============================================================================================

    /// <summary>
    /// Expand <c>$</c> references in a command string. <c>$$</c>→literal <c>$</c> (deferral); <c>$name</c> /
    /// <c>${name}</c> → cvar string value; inside an alias body <c>$1</c>..<c>$9</c>/<c>$0</c> → that argument,
    /// <c>$*</c> / <c>${* …}</c> / <c>${n- …}</c> → a range of arguments joined by spaces (tokens containing
    /// spaces are re-quoted so the result re-tokenizes faithfully). Unknown refs expand to empty (DP behavior).
    /// </summary>
    internal string Expand(string s, IReadOnlyList<string>? args)
    {
        if (s.IndexOf('$') < 0)
            return s;

        var sb = new System.Text.StringBuilder(s.Length + 16);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c != '$')
            {
                sb.Append(c);
                i++;
                continue;
            }
            // c == '$'
            if (i + 1 < n && s[i + 1] == '$')
            {
                sb.Append('$'); // $$ → literal $ (the following char is left untouched -> deferral)
                i += 2;
                continue;
            }
            if (i + 1 >= n)
            {
                sb.Append('$');
                break;
            }

            if (s[i + 1] == '{')
            {
                int close = s.IndexOf('}', i + 2);
                if (close < 0)
                {
                    sb.Append(s, i, n - i); // unterminated — emit the rest literally
                    break;
                }
                string inner = s.Substring(i + 2, close - (i + 2)).Trim();
                sb.Append(ResolveRef(inner, args));
                i = close + 1;
            }
            else
            {
                int start = i + 1;
                int j = start;
                // A bare $name selector: identifier chars, or a lone `*`.
                if (j < n && s[j] == '*')
                {
                    j++;
                }
                else
                {
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_'))
                        j++;
                }
                if (j == start)
                {
                    sb.Append('$'); // lone $ with nothing parseable
                    i++;
                    continue;
                }
                sb.Append(ResolveRef(s.Substring(start, j - start), args));
                i = j;
            }
        }
        return sb.ToString();
    }

    /// <summary>Resolve the inside of a <c>$ref</c> / <c>${ref}</c> to its replacement text.</summary>
    private string ResolveRef(string inner, IReadOnlyList<string>? args)
    {
        if (inner.Length == 0)
            return "";

        // Strip a trailing modifier word ("asis", "?", "!", "q", …) — we don't distinguish them.
        string selector = inner;
        int sp = inner.IndexOf(' ');
        if (sp >= 0)
            selector = inner.Substring(0, sp);
        if (selector.Length == 0)
            return "";

        // All-arguments: $*  /  ${* asis}
        if (selector == "*")
            return JoinArgs(args, 1, int.MaxValue);

        // Range from an index: ${2-}  /  ${2- asis}
        if (selector.Length >= 2 && selector[^1] == '-' && int.TryParse(selector[..^1], out int from))
            return JoinArgs(args, from, int.MaxValue);

        // Single positional argument: $1 .. $9, $0 (alias name)
        if (int.TryParse(selector, out int idx))
            return ArgAt(args, idx);

        // Otherwise a cvar reference.
        return _cvars.GetString(selector);
    }

    private static string ArgAt(IReadOnlyList<string>? args, int index)
        => args is not null && index >= 0 && index < args.Count ? args[index] : "";

    private static string JoinArgs(IReadOnlyList<string>? args, int from, int toExclusive)
    {
        if (args is null || from >= args.Count)
            return "";
        int end = System.Math.Min(toExclusive, args.Count);
        var sb = new System.Text.StringBuilder();
        for (int k = from; k < end; k++)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            string a = args[k];
            // Re-quote a token that contains whitespace so the spliced result re-tokenizes to the same tokens.
            if (a.Length == 0 || a.IndexOf(' ') >= 0 || a.IndexOf('\t') >= 0)
            {
                sb.Append('"').Append(a).Append('"');
            }
            else
            {
                sb.Append(a);
            }
        }
        return sb.ToString();
    }

    // =============================================================================================
    // tokenizer (DP COM_ParseToken_Console / Cmd_TokenizeString — the relevant slice)
    // =============================================================================================

    /// <summary>
    /// Split a script/line into individual command strings, stripping <c>//</c> and <c>/* */</c> comments and
    /// breaking on top-level <c>;</c> and newlines — all while respecting double-quoted spans (so a <c>;</c> or
    /// <c>//</c> inside a quoted alias body or description is preserved). Quote characters and escapes are kept
    /// intact for <see cref="Tokenize"/> to interpret. Returns trimmed, non-empty command strings.
    /// </summary>
    public static List<string> SplitIntoCommands(string text)
    {
        var commands = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        int i = 0, n = text.Length;

        void Flush()
        {
            string s = sb.ToString().Trim();
            if (s.Length > 0)
                commands.Add(s);
            sb.Clear();
        }

        while (i < n)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '\\' && i + 1 < n)
                {
                    sb.Append(c).Append(text[i + 1]); // keep the escape pair for Tokenize
                    i += 2;
                    continue;
                }
                if (c == '"')
                    inQuotes = false;
                sb.Append(c);
                i++;
                continue;
            }

            // not in quotes
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }
            if (c == '"')
            {
                inQuotes = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '\n' || c == ';')
            {
                Flush();
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        Flush();
        return commands;
    }

    /// <summary>
    /// Tokenize one command string into its argument vector, honoring double-quoted tokens (with
    /// <c>\"</c>/<c>\\</c>/<c>\n</c>/<c>\t</c>/<c>\r</c> escapes) so a quoted value/description is a single
    /// token. An empty quoted token (<c>set foo ""</c>) is preserved as an empty-string argument.
    /// </summary>
    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool started = false;
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    char e = line[i + 1];
                    sb.Append(e switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '"' => '"',
                        '\\' => '\\',
                        _ => e,
                    });
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inQuotes = false;
                    continue;
                }
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                started = true; // an opening quote starts a token even if it ends up empty
                continue;
            }
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (started)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    started = false;
                }
                continue;
            }
            started = true;
            sb.Append(c);
        }
        if (started)
            tokens.Add(sb.ToString());
        return tokens;
    }
}
