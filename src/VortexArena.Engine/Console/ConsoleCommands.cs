using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using XonoticGodot.Common.Config;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Engine.Console;

/// <summary>
/// The console/cvar builtins layered onto the shared <see cref="ConfigInterpreter"/> — the C# successor to the
/// DP engine commands the console exposes beyond the interpreter's own <c>set</c>/<c>seta</c>/<c>alias</c>/
/// <c>exec</c> (<c>echo</c>, <c>toggle</c>/<c>inc</c>/<c>dec</c>, <c>cvar</c>, <c>cvarlist</c>/<c>cmdlist</c>,
/// <c>bind</c>/<c>unbind</c>/<c>bindlist</c>, <c>name</c>, <c>developer</c>, …). It registers each on the
/// interpreter (which consults registered commands before alias/cvar fallback) and installs the interpreter's
/// <see cref="ConfigInterpreter.UnknownCommandHandler"/> so any line that isn't a console/cvar command is
/// routed to the live game — the in-process listen-server world (<paramref>localRouter</paramref>) or, on a pure
/// client, the remote string-command channel (<paramref>remoteSender</paramref>).
///
/// <para>Godot-free and host-free by construction: cvar reads/writes go through the injected
/// <see cref="CvarService"/>, console output through <c>print</c>, and the screen clear / command routing /
/// remote send through injected delegates. So it lives in a <c>src</c> library and is unit-testable headlessly;
/// the Godot overlay and the engine/host actions (quit/connect/map/vid_restart) are wired around it by the
/// client (<c>Game.Console.ConsoleOverlay</c> / <c>Shell</c>).</para>
/// </summary>
public sealed class ConsoleCommands
{
    private readonly ConfigInterpreter _interp;
    private readonly CvarService _cvars;
    private readonly Action<string> _print;
    private readonly Action? _clear;
    private readonly Func<string, string?>? _localRouter;
    private readonly Action<string>? _remoteSender;

    /// <summary>The interpreter's intrinsic builtins (handled in its dispatch switch, not the registered table) —
    /// folded into <c>cmdlist</c>/completion so they show up alongside the registered commands.</summary>
    private static readonly string[] InterpreterBuiltins =
        { "set", "seta", "set_temp", "seta_temp", "setp", "alias", "unalias", "exec", "unset", "cvar_reset" };

    /// <param name="interp">The shared command buffer to register on (also gets the unknown-command router).</param>
    /// <param name="cvars">The cvar store the cvar builtins act on (the front-end's shared store).</param>
    /// <param name="print">Sink for one line of console output.</param>
    /// <param name="clear">Clears the console scrollback (the <c>clear</c> command); null → <c>clear</c> no-ops.</param>
    /// <param name="localRouter">Runs a gameplay command on the in-process world and returns its output, or null
    /// when there is no local world (pure client) — then <paramref name="remoteSender"/> is tried.</param>
    /// <param name="remoteSender">Forwards a gameplay command to the connected server (clc_stringcmd); its reply
    /// arrives asynchronously and is printed by the host, not here.</param>
    public ConsoleCommands(
        ConfigInterpreter interp,
        CvarService cvars,
        Action<string> print,
        Action? clear = null,
        Func<string, string?>? localRouter = null,
        Action<string>? remoteSender = null)
    {
        _interp = interp ?? throw new ArgumentNullException(nameof(interp));
        _cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _clear = clear;
        _localRouter = localRouter;
        _remoteSender = remoteSender;

        Register();
    }

    private void Register()
    {
        _interp.RegisterCommand("echo", a => _print(JoinTail(a, 1)));
        _interp.RegisterCommand("clear", _ => _clear?.Invoke());

        _interp.RegisterCommand("toggle", CmdToggle);
        _interp.RegisterCommand("cycle", CmdToggle); // cycle <cvar> v1 v2 … — same advance-through-values logic
        _interp.RegisterCommand("inc", a => CmdIncDec(a, +1f));
        _interp.RegisterCommand("dec", a => CmdIncDec(a, -1f));

        _interp.RegisterCommand("cvar", CmdCvar);
        _interp.RegisterCommand("cvarlist", CmdCvarList);
        _interp.RegisterCommand("cvar_orphans", CmdCvarOrphans);
        _interp.RegisterCommand("cmdlist", CmdCmdList);
        _interp.RegisterCommand("apropos", CmdApropos);
        _interp.RegisterCommand("search", CmdApropos); // friendlier alias for apropos (search commands+cvars by substring)
        _interp.RegisterCommand("help", CmdHelp);

        _interp.RegisterCommand("bind", CmdBind);
        _interp.RegisterCommand("unbind", a => { if (a.Count >= 2) BindTable.Unbind(a[1]); });
        _interp.RegisterCommand("unbindall", _ => BindTable.UnbindAll());
        _interp.RegisterCommand("bindlist", _ => CmdBindList());

        _interp.RegisterCommand("name", CmdName);
        _interp.RegisterCommand("developer", CmdDeveloper);

        // DP/QC `cl_cmd sendcvar <name>` (qcsrc/client/command/cl_cmd.qc:395-428, minus the cl_cmd prefix —
        // the menu's "Apply immediately" button and the QC binds issue the bare `sendcvar cl_weaponpriority`):
        // read the cvar from the local store and push it to the live game as `sentcvar <name> "<value>"` (the
        // server-side per-client replication command). The QC client-side cl_weaponpriority W_FixWeaponOrder
        // pre-send fixup is skipped — the server applies the same fixup on receive (Commands.CmdSentCvar).
        _interp.RegisterCommand("sendcvar", CmdSendCvar);

        // ---- generic commands (DP common/command/generic.qc + rpn.qc) — present in ALL programs (menu/
        //      client/server) in QC, so they live on the SHARED console surface here too. Pure cvar/string ops.
        _interp.RegisterCommand("rpn", a => Rpn.Run(a, _cvars, _print));
        _interp.RegisterCommand("addtolist", CmdAddToList);
        _interp.RegisterCommand("removefromlist", CmdRemoveFromList);
        _interp.RegisterCommand("maplist", CmdMaplist);
        _interp.RegisterCommand("nextframe", CmdNextFrame);
        _interp.RegisterCommand("settemp", CmdSettemp);
        _interp.RegisterCommand("settemp_restore", CmdSettempRestore);

        // route everything else (a gameplay/client command like kill/say/team) to the live game, and persist
        // `seta` to the user config the way DP's CVAR_SAVE flag does.
        _interp.UnknownCommandHandler = RouteUnknown;
        _interp.CvarArchiveHook = name => _cvars.MarkArchived(name);
    }

    // =============================================================================================
    //  cvar builtins (DP generic.qc echo/toggle/inc/dec + cvar/cvarlist)
    // =============================================================================================

    private void CmdToggle(IReadOnlyList<string> a)
    {
        if (a.Count < 2) { _print("usage: toggle <cvar> [value1 value2 ...]"); return; }
        string name = a[1];
        if (a.Count > 2)
        {
            // advance to the next value in the list (wrap), DP cycle/toggle-with-values behaviour.
            string cur = _cvars.GetString(name);
            int at = -1;
            for (int i = 2; i < a.Count; i++)
                if (a[i] == cur) { at = i; break; }
            int next = at < 0 ? 2 : (at + 1 >= a.Count ? 2 : at + 1);
            _cvars.Set(name, a[next]);
        }
        else
        {
            _cvars.Set(name, _cvars.GetFloat(name) != 0f ? "0" : "1"); // flip 0<->1
        }
    }

    private void CmdIncDec(IReadOnlyList<string> a, float sign)
    {
        if (a.Count < 2) { _print($"usage: {(sign < 0 ? "dec" : "inc")} <cvar> [step]"); return; }
        string name = a[1];
        float step = a.Count >= 3 && TryFloat(a[2], out float s) ? s : 1f;
        _cvars.Set(name, (_cvars.GetFloat(name) + sign * step).ToString(CultureInfo.InvariantCulture));
    }

    private void CmdCvar(IReadOnlyList<string> a)
    {
        if (a.Count < 2) { _print("usage: cvar <name> [value]"); return; }
        string name = a[1];
        if (a.Count >= 3) { _cvars.Set(name, a[2]); return; }
        PrintCvar(name);
    }

    private void PrintCvar(string name)
    {
        string val = _cvars.GetString(name);
        string def = _cvars.GetDefault(name);
        _print(string.Equals(val, def, StringComparison.Ordinal) || string.IsNullOrEmpty(def)
            ? $"\"{name}\" is \"{val}\""
            : $"\"{name}\" is \"{val}\" [default \"{def}\"]");
    }

    private void CmdCvarList(IReadOnlyList<string> a)
    {
        string? filter = a.Count >= 2 ? a[1] : null;
        int n = 0;
        foreach (string name in _cvars.Names.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _print($"{name} \"{_cvars.GetString(name)}\"");
            n++;
        }
        _print($"{n} cvar(s)");
    }

    /// <summary>
    /// <c>cvar_orphans</c> — list cvars that some code read while they were ABSENT from the store (never registered
    /// nor set), so the read silently returned 0/"" and the name is hidden from <c>cvarlist</c>/<c>search</c> (the
    /// <c>vid_vsync</c> class of bug). A diagnostic for "are all cvars registered?": run it after exercising the
    /// client (open menus, join a match) to populate the read set. Some entries are legitimately optional cvars
    /// (e.g. a mutator's per-feature toggle absent in this ruleset), so eyeball the list rather than treating every
    /// line as a bug. Server-only cvars live in the listen server's private store, not this one.
    /// </summary>
    private void CmdCvarOrphans(IReadOnlyList<string> a)
    {
        var orphans = _cvars.UnregisteredReadNames.OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (string name in orphans)
            _print(name);
        _print($"{orphans.Count} cvar(s) read but never registered or set (default to 0/\"\", hidden from cvarlist).");
    }

    private void CmdCmdList(IReadOnlyList<string> a)
    {
        string? filter = a.Count >= 2 ? a[1] : null;
        var names = AllCommandNames()
            .Where(name => filter == null || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (string name in names)
            _print(name);
        _print($"{names.Count} command(s)");
    }

    private void CmdApropos(IReadOnlyList<string> a)
    {
        if (a.Count < 2) { _print($"usage: {a[0]} <substring>"); return; }
        string q = a[1];
        bool any = false;
        foreach (string name in AllCommandNames().OrderBy(x => x, StringComparer.Ordinal))
            if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { _print($"command  {name}"); any = true; }
        foreach (string name in _cvars.Names.OrderBy(x => x, StringComparer.Ordinal))
            if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { _print($"cvar     {name} \"{_cvars.GetString(name)}\""); any = true; }
        if (!any) _print($"nothing matching \"{q}\"");
    }

    private void CmdHelp(IReadOnlyList<string> a)
    {
        if (a.Count >= 2)
        {
            string name = a[1];
            if (AllCommandNames().Contains(name, StringComparer.OrdinalIgnoreCase))
                _print($"\"{name}\" is a command");
            else if (_cvars.Has(name))
                PrintCvar(name);
            else
                _print($"no command or cvar named \"{name}\"");
            return;
        }
        _print("XonoticGodot console — type a command or `cvar value`. Try: cmdlist, cvarlist [filter], search <text>,");
        _print("bind <key> <command>, exec <file.cfg>, toggle <cvar>, connect <addr>, disconnect, quit.");
    }

    // =============================================================================================
    //  binds (DP bind / unbind / unbindall / bindlist over the shared BindTable)
    // =============================================================================================

    private void CmdBind(IReadOnlyList<string> a)
    {
        if (a.Count < 2) { CmdBindList(); return; }
        string key = a[1];
        if (a.Count < 3) // query a single key
        {
            string c = BindTable.Get(key);
            _print(c.Length > 0 ? $"\"{key}\" = \"{c}\"" : $"\"{key}\" is not bound");
            return;
        }
        // join the tail so both `bind x "+forward"` (one token) and `bind x say hi` (many) work.
        BindTable.Bind(key, JoinTail(a, 2));
    }

    private void CmdBindList()
    {
        int n = 0;
        foreach (var kv in BindTable.List()) { _print($"\"{kv.Key}\" \"{kv.Value}\""); n++; }
        _print($"{n} bind(s)");
    }

    // =============================================================================================
    //  identity / diagnostics
    // =============================================================================================

    private void CmdName(IReadOnlyList<string> a)
    {
        if (a.Count < 2) { _print($"name is \"{_cvars.GetString("_cl_name")}\""); return; }
        string newName = JoinTail(a, 1);
        foreach (string cv in new[] { "_cl_name", "name" })
        {
            _cvars.Set(cv, newName);
            _cvars.MarkArchived(cv);
        }
        _print($"name set to \"{newName}\"");
    }

    private void CmdDeveloper(IReadOnlyList<string> a)
    {
        if (a.Count >= 2) { _cvars.Set("developer", a[1]); return; }
        _print($"developer is \"{_cvars.GetString("developer")}\"");
    }

    // =============================================================================================
    //  generic list/maplist/settemp/nextframe commands (DP common/command/generic.qc)
    // =============================================================================================

    /// <summary>
    /// Optional host hook: does the named map exist (QC <c>fexists("maps/&lt;m&gt;.bsp")</c>)? Wired by a host
    /// with a map catalog so <c>maplist add</c> rejects a missing map exactly like QC. Null (the default, e.g.
    /// the menu/headless console) → the existence check is SKIPPED and the map is added as-is (deviation R4).
    /// </summary>
    public Func<string, bool>? MapExists { get; set; }

    /// <summary>The RNG behind <c>maplist shuffle</c> (seedable for deterministic tests; QC uses <c>random()</c>).</summary>
    public Random ShuffleRng { get; set; } = new Random();

    /// <summary>name → value held BEFORE the first <c>settemp</c> override (QC cvar_settemp saved value).</summary>
    private readonly Dictionary<string, string> _settemp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>QC <c>GenericCommand_addtolist</c>: append <c>value</c> to the space-separated list cvar, deduped.</summary>
    private void CmdAddToList(IReadOnlyList<string> a)
    {
        if (a.Count < 3)
        {
            _print("Usage: addtolist <cvar> <value>");
            return;
        }
        string cvar = a[1];
        string value = a[2];
        string cur = _cvars.GetString(cvar);
        if (cur == "")
        {
            _cvars.Set(cvar, value); // QC: empty cvar → just set the value
            return;
        }
        // QC FOREACH_WORD(list, it == value, return) — skip if already present.
        foreach (string w in WordList.Words(cur))
            if (w == value)
                return;
        _cvars.Set(cvar, WordList.Cons(cur, value)); // append at the END (note: maplist add PREPENDS).
    }

    /// <summary>QC <c>GenericCommand_removefromlist</c>: rebuild the list cvar keeping only words != value.</summary>
    private void CmdRemoveFromList(IReadOnlyList<string> a)
    {
        if (a.Count != 3)
        {
            _print("Usage: removefromlist <cvar> <value>");
            return;
        }
        string cvar = a[1];
        string removal = a[2];
        string rebuilt = "";
        foreach (string w in WordList.Words(_cvars.GetString(cvar)))
            if (w != removal)
                rebuilt = WordList.Cons(rebuilt, w);
        _cvars.Set(cvar, rebuilt);
    }

    /// <summary>
    /// QC <c>GenericCommand_maplist</c>: <c>add</c> (PREPEND to g_maplist after a bsp-existence check),
    /// <c>remove</c> (drop a map), <c>shuffle</c> (Fisher–Yates), <c>cleanup</c> (keep only usable maps —
    /// best-effort: identity unless a <see cref="MapExists"/> catalog is wired). NOTE: <c>add</c> PREPENDS,
    /// unlike <see cref="CmdAddToList"/> which appends.
    /// </summary>
    private void CmdMaplist(IReadOnlyList<string> a)
    {
        string action = a.Count >= 2 ? a[1] : "";
        switch (action)
        {
            case "add":
                if (a.Count == 3)
                {
                    string map = a[2];
                    if (MapExists is not null && !MapExists(map))
                    {
                        _print($"maplist: ERROR: {map} does not exist!");
                        return;
                    }
                    string cur = _cvars.GetString("g_maplist");
                    // QC: if empty set to map, else PREPEND "map existing".
                    _cvars.Set("g_maplist", cur == "" ? map : map + " " + cur);
                    return;
                }
                break;
            case "remove":
                if (a.Count == 3)
                {
                    string del = a[2];
                    string rebuilt = "";
                    foreach (string w in WordList.Words(_cvars.GetString("g_maplist")))
                        if (w != del)
                            rebuilt = WordList.Cons(rebuilt, w);
                    _cvars.Set("g_maplist", rebuilt);
                    return;
                }
                break;
            case "shuffle":
                _cvars.Set("g_maplist", WordList.Shuffle(_cvars.GetString("g_maplist"), ShuffleRng));
                return;
            case "cleanup":
                // QC filters by MapInfo_CheckMap; without a catalog the faithful fallback is identity (keep all),
                // honoring MapExists when wired (drop words whose bsp is gone). Deviation R4.
                if (MapExists is not null)
                {
                    string filtered = "";
                    foreach (string w in WordList.Words(_cvars.GetString("g_maplist")))
                        if (MapExists(w))
                            filtered = WordList.Cons(filtered, w);
                    _cvars.Set("g_maplist", filtered);
                }
                return;
        }
        _print("Usage: maplist <action> [<map>] — actions: add, cleanup, remove, shuffle");
    }

    /// <summary>
    /// QC <c>GenericCommand_nextframe</c>: run a command on the next VM frame. The console has no frame pump, so
    /// when a live world is wired we forward to its command bus (the server's <c>nextframe</c> enqueues it on the
    /// sim-clock <c>defer 0</c> queue — the real "next tick"); on a bare menu/headless console we run it inline
    /// (a documented degenerate — "next frame" with no scheduler == now).
    /// </summary>
    private void CmdNextFrame(IReadOnlyList<string> a)
    {
        if (a.Count < 2)
        {
            _print("Usage: nextframe <command>");
            return;
        }
        string tail = JoinTail(a, 1);
        if (_localRouter is not null)
            _localRouter($"nextframe {tail}"); // reaches the server bus → Deferred.Defer(0, tail)
        else
            _interp.ExecuteLine(tail);          // no world: run inline
    }

    /// <summary>
    /// QC <c>GenericCommand_settemp</c> / <c>cvar_settemp</c>: remember the cvar's current value (once), then
    /// set the new value. Restored by <see cref="CmdSettempRestore"/>. (The server has its own
    /// <c>SettempCvars</c> for map-end restore; this is the console/menu-surface twin over the shared store.)
    /// </summary>
    private void CmdSettemp(IReadOnlyList<string> a)
    {
        if (a.Count < 3)
        {
            _print("Usage: settemp <cvar> <value>");
            return;
        }
        string name = a[1];
        if (!_settemp.ContainsKey(name))
            _settemp[name] = _cvars.GetString(name); // capture the original exactly once
        _cvars.Set(name, a[2]);
    }

    /// <summary>QC <c>GenericCommand_settemp_restore</c> / <c>cvar_settemp_restore</c>: write every saved original back.</summary>
    private void CmdSettempRestore(IReadOnlyList<string> _)
    {
        foreach (var kv in _settemp)
            _cvars.Set(kv.Key, kv.Value);
        _settemp.Clear();
    }

    // =============================================================================================
    //  cvar replication (DP/QC LocalCommand_sendcvar — cl_cmd.qc:395-428)
    // =============================================================================================

    /// <summary>
    /// <c>sendcvar &lt;cvar&gt;</c>: push the local value of a replicated client cvar to the server. Routes the
    /// resulting <c>sentcvar &lt;name&gt; "&lt;value&gt;"</c> line exactly like an unknown gameplay command —
    /// the in-process listen world first (with the caller attached by the host's router), else the remote
    /// string-command channel. With neither wired (a bare menu console) it is a silent no-op, like QC's
    /// <c>cmd</c> into a disconnected client.
    /// </summary>
    private void CmdSendCvar(IReadOnlyList<string> a)
    {
        if (a.Count < 2)
        {
            _print("usage: sendcvar <cvar>");
            return;
        }
        string name = a[1];
        string line = $"sentcvar {name} \"{_cvars.GetString(name)}\"";
        if (_localRouter != null)
        {
            string? output = _localRouter(line);   // null = no local world; "" = handled silently
            if (output != null)
            {
                string trimmed = output.TrimEnd('\n', '\r', ' ', '\t');
                if (trimmed.Length > 0)
                    _print(trimmed);
                return;
            }
        }
        _remoteSender?.Invoke(line);
    }

    // =============================================================================================
    //  unknown-command routing (DP Cmd_ForwardToServer)
    // =============================================================================================

    private void RouteUnknown(string name, IReadOnlyList<string> argv)
    {
        // DP Cmd_ExecuteString's cvar fallback: a lone cvar name typed at the console prints its value and is
        // NOT forwarded to the server. (A `name value` line is already a bare cvar assignment in the interpreter,
        // so only the no-value query reaches here.) Without this, `g_balance_blaster_primary_radius` typed alone
        // fell through to "Unknown command".
        if (argv.Count == 1 && _cvars.Has(name))
        {
            PrintCvar(name);
            return;
        }

        string line = Rejoin(argv);
        if (_localRouter != null)
        {
            string? output = _localRouter(line);   // null = no local world; "" = handled silently
            if (output != null)
            {
                string trimmed = output.TrimEnd('\n', '\r', ' ', '\t');
                if (trimmed.Length > 0)
                    _print(trimmed);
                return;
            }
        }
        if (_remoteSender != null)
        {
            _remoteSender(line);   // reply (if any) arrives async via the host's print event
            return;
        }
        _print($"Unknown command \"{name}\"");
    }

    // =============================================================================================
    //  completion (Tab) — pure, so the overlay just renders the result
    // =============================================================================================

    /// <summary>Every name a console line can start with: registered commands + interpreter builtins + aliases.</summary>
    public IEnumerable<string> AllCommandNames()
        => _interp.CommandNames.Concat(InterpreterBuiltins).Concat(_interp.Aliases.Keys);

    /// <summary>The full completion universe: commands ∪ cvar names (for `cvar`/bare-cvar completion).</summary>
    public IEnumerable<string> CompletionNames()
        => AllCommandNames().Concat(_cvars.Names);

    /// <summary>
    /// DP Tab-completion over <paramref name="names"/> for <paramref name="prefix"/>: the matches (case-insensitive
    /// prefix), and the text the input should become — the single match, the longest common prefix when several
    /// match, or the prefix unchanged when none do. The caller appends a trailing space on a unique completion and
    /// lists <see cref="CompletionResult.Matches"/> when there is more than one.
    /// </summary>
    public static CompletionResult Complete(string prefix, IEnumerable<string> names)
    {
        var matches = names
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
            return new CompletionResult(prefix, matches);
        if (matches.Count == 1)
            return new CompletionResult(matches[0], matches);

        string common = LongestCommonPrefix(matches);
        return new CompletionResult(common.Length >= prefix.Length ? common : prefix, matches);
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> items)
    {
        string first = items[0];
        int len = first.Length;
        for (int i = 1; i < items.Count; i++)
        {
            string s = items[i];
            int j = 0;
            while (j < len && j < s.Length && char.ToLowerInvariant(first[j]) == char.ToLowerInvariant(s[j]))
                j++;
            len = j;
            if (len == 0) break;
        }
        return first.Substring(0, len);
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>Join <paramref name="argv"/> from <paramref name="first"/> to the end with single spaces.</summary>
    private static string JoinTail(IReadOnlyList<string> argv, int first)
    {
        if (first >= argv.Count) return "";
        var sb = new StringBuilder();
        for (int i = first; i < argv.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(argv[i]);
        }
        return sb.ToString();
    }

    /// <summary>Re-join an already-expanded argv into a command line, re-quoting tokens that contain whitespace so
    /// it re-tokenizes to the same vector on the receiving side (the server <c>Commands</c> tokenizes again).</summary>
    private static string Rejoin(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        foreach (string t in argv)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (t.Length == 0 || t.IndexOf(' ') >= 0 || t.IndexOf('\t') >= 0)
                sb.Append('"').Append(t).Append('"');
            else
                sb.Append(t);
        }
        return sb.ToString();
    }

    private static bool TryFloat(string s, out float f)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
}

/// <summary>The outcome of a Tab completion: the text the input becomes, plus all matching names.</summary>
public readonly struct CompletionResult
{
    /// <summary>The completed text (unique match, common prefix, or the original prefix when nothing matched).</summary>
    public readonly string Completed;

    /// <summary>All names that matched the prefix (empty if none). More than one → the overlay lists them.</summary>
    public readonly IReadOnlyList<string> Matches;

    public CompletionResult(string completed, IReadOnlyList<string> matches)
    {
        Completed = completed;
        Matches = matches;
    }
}
