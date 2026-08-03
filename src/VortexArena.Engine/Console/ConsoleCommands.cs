using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;

namespace VortexArena.Engine.Console;

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

    /// <summary>
    /// Help strings for <see cref="InterpreterBuiltins"/> (DP's <c>Cmd_AddCommand</c> descriptions for the same
    /// verbs). They live here rather than on the interpreter because the interpreter handles them inside its
    /// dispatch switch — they are never registered, so there is no <c>RegisterCommand</c> call to hang a
    /// description off. <c>search</c> and Tab completion read them through <see cref="InterpreterBuiltinHelp"/>.
    /// </summary>
    private static string InterpreterBuiltinHelp(string name) => name switch
    {
        "set" => "create or change a cvar: set <name> <value> [\"description\"]",
        "seta" => "like set, but also marks the cvar to be saved to the user config",
        "set_temp" => "like set, but the value is restored at the end of the map",
        "seta_temp" => "like seta, but the value is restored at the end of the map",
        "setp" => "like set, but marks the cvar private (its value is not shown to other players)",
        "alias" => "create a script function: alias <name> \"<commands>\" (no body removes it)",
        "unalias" => "remove an alias created with alias",
        "exec" => "execute a script file: exec <file.cfg>",
        "unset" => "remove a cvar entirely",
        "cvar_reset" => "reset a cvar to its default value",
        _ => "",
    };

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

        // The Tab-completion engine (DP Con_CompleteCommandLine). Built here because it needs exactly the three
        // things this class already holds — the command buffer, the cvar store, and the builtin help table — and
        // because the overlay should have one obvious place to reach it. Its filesystem/map/key hooks are wired
        // by the host afterwards; without them it still does the full command/cvar/alias completion.
        Completion = new CommandCompletion(interp, cvars, InterpreterBuiltins, InterpreterBuiltinHelp);
    }

    /// <summary>Tab completion over this console's commands, cvars and aliases (DP <c>Con_CompleteCommandLine</c>).
    /// Wire its <see cref="CommandCompletion.FileSearch"/>/<see cref="CommandCompletion.MapNames"/>/
    /// <see cref="CommandCompletion.KeyNames"/> hooks from the host to get argument completion too.</summary>
    public CommandCompletion Completion { get; }

    private void Register()
    {
        // Every registration carries DP's Cmd_AddCommand fourth argument — the one-line help. It is what
        // `search`/`apropos` matches keywords against and what Tab completion prints beside each candidate;
        // without it the whole command half of the console is undiscoverable.
        _interp.RegisterCommand("echo", a => _print(JoinTail(a, 1)),
            "print a message to the console");
        _interp.RegisterCommand("clear", _ => _clear?.Invoke(),
            "clear the console scrollback");

        _interp.RegisterCommand("toggle", CmdToggle,
            "flip a cvar between 0 and 1, or step it through the listed values: toggle <cvar> [value1 value2 ...]");
        _interp.RegisterCommand("cycle", CmdToggle, // cycle <cvar> v1 v2 … — same advance-through-values logic
            "step a cvar through the listed values, wrapping at the end: cycle <cvar> <value1> <value2> ...");
        _interp.RegisterCommand("inc", a => CmdIncDec(a, +1f),
            "increase a cvar by 1, or by the given step: inc <cvar> [step]");
        _interp.RegisterCommand("dec", a => CmdIncDec(a, -1f),
            "decrease a cvar by 1, or by the given step: dec <cvar> [step]");

        _interp.RegisterCommand("cvar", CmdCvar,
            "print a cvar's value and default, or set it: cvar <name> [value]");
        _interp.RegisterCommand("cvarlist", CmdCvarList,
            "list every cvar, optionally filtered by a substring: cvarlist [filter]");
        _interp.RegisterCommand("cvar_orphans", CmdCvarOrphans,
            "list cvars some code read while they were absent from the store (they silently returned 0/\"\")");
        _interp.RegisterCommand("cvar_changes", CmdCvarChanges,
            "list every cvar whose value differs from the shipped default — what your setup actually changes: "
            + "cvar_changes [filter]");
        _interp.RegisterCommand("diff", CmdCvarChanges, // friendlier name for the same report
            "list every cvar whose value differs from the shipped default: diff [filter]");
        _interp.RegisterCommand("cmdlist", CmdCmdList,
            "list every console command, optionally filtered by a substring: cmdlist [filter]");
        _interp.RegisterCommand("apropos", CmdApropos,
            "find cvars and commands by keyword, searching names AND descriptions; best match printed last");
        _interp.RegisterCommand("search", CmdApropos, // friendlier name for apropos
            "find cvars and commands by keyword, searching names AND descriptions; best match printed last");
        _interp.RegisterCommand("help", CmdHelp,
            "describe a command or cvar, or print the console's own quick reference: help [name]");

        _interp.RegisterCommand("bind", CmdBind,
            "bind a key to a command, or show what it is bound to: bind <key> [command]");
        _interp.RegisterCommand("unbind", a => { if (a.Count >= 2) BindTable.Unbind(a[1]); },
            "remove the binding from a key: unbind <key>");
        _interp.RegisterCommand("unbindall", _ => BindTable.UnbindAll(),
            "remove all key bindings");
        _interp.RegisterCommand("bindlist", _ => CmdBindList(),
            "list every bound key and the command it runs");

        _interp.RegisterCommand("name", CmdName,
            "set your player name: name <newname>");
        _interp.RegisterCommand("developer", CmdDeveloper,
            "set the developer log level (0 = normal, 1+ reveals buffered debug/trace lines in the console)");

        // DP/QC `cl_cmd sendcvar <name>` (qcsrc/client/command/cl_cmd.qc:395-428, minus the cl_cmd prefix —
        // the menu's "Apply immediately" button and the QC binds issue the bare `sendcvar cl_weaponpriority`):
        // read the cvar from the local store and push it to the live game as `sentcvar <name> "<value>"` (the
        // server-side per-client replication command). The QC client-side cl_weaponpriority W_FixWeaponOrder
        // pre-send fixup is skipped — the server applies the same fixup on receive (Commands.CmdSentCvar).
        _interp.RegisterCommand("sendcvar", CmdSendCvar,
            "send a replicated client cvar's current value to the server: sendcvar <cvar>");

        // ---- generic commands (DP common/command/generic.qc + rpn.qc) — present in ALL programs (menu/
        //      client/server) in QC, so they live on the SHARED console surface here too. Pure cvar/string ops.
        _interp.RegisterCommand("rpn", a => Rpn.Run(a, _cvars, _print),
            "reverse-polish calculator over cvars: rpn <expression>");
        _interp.RegisterCommand("addtolist", CmdAddToList,
            "append a value to a space-separated list cvar if not already present: addtolist <cvar> <value>");
        _interp.RegisterCommand("removefromlist", CmdRemoveFromList,
            "remove a value from a space-separated list cvar: removefromlist <cvar> <value>");
        _interp.RegisterCommand("maplist", CmdMaplist,
            "edit the g_maplist rotation: maplist add|remove|shuffle|cleanup [map]");
        _interp.RegisterCommand("nextframe", CmdNextFrame,
            "run a command on the next server frame: nextframe <command>");
        _interp.RegisterCommand("settemp", CmdSettemp,
            "set a cvar, remembering its old value for settemp_restore: settemp <cvar> <value>");
        _interp.RegisterCommand("settemp_restore", CmdSettempRestore,
            "restore every cvar changed by settemp to the value it had before");

        // ---- console-surface commands (DP console.c): dump the scrollback, list/replay the input history.
        _interp.RegisterCommand("condump", CmdCondump,
            "write the console scrollback to a file: condump [filename] (default condump.txt)");
        _interp.RegisterCommand("history", CmdHistory,
            "list the console input history; `history -c` clears it, `history <n>` shows the last n lines");

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

    /// <summary>
    /// DP <c>Cvar_PrintHelp</c> (cvar.c:274): <c>^3name^7 is "value" ["default"] description</c>. The name is
    /// yellow, the description trails the value, and the default is always shown — DP prints both
    /// unconditionally so a glance tells you whether the value is stock. <paramref name="full"/> false drops the
    /// description (DP's <c>full</c> flag; the terse <c>cvarlist</c> uses it).
    /// </summary>
    private void PrintCvar(string name, bool full = true)
    {
        string val = _cvars.GetString(name);
        string def = _cvars.GetDefault(name);
        string desc = full ? _cvars.GetDescription(name) : "";
        string line = $"^3{name}^7 is \"{val}^7\" [\"{def}^7\"]";
        _print(desc.Length > 0 ? line + " " + desc : line);
    }

    /// <summary>DP <c>Cvar_List_f</c>: every cvar (optionally prefix/substring filtered) in
    /// <c>Cvar_PrintHelp</c> form — name, value, default, description.</summary>
    private void CmdCvarList(IReadOnlyList<string> a)
    {
        string? filter = a.Count >= 2 ? a[1] : null;
        int n = 0;
        foreach (string name in _cvars.Names.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            PrintCvar(name);
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

    /// <summary>
    /// <c>cvar_changes</c> / <c>diff</c> — QC's <c>cvar_changes</c> (server/world.qc builds the same report for
    /// the server browser): every cvar whose live value differs from the baseline the shipped cfg tree locked in
    /// at boot. In other words, everything your <c>config.cfg</c>, this session's console edits, the menu and any
    /// <c>--cvar</c> pin have actually changed — the answer to "what is different about MY install".
    ///
    /// <para>Split into two blocks, because they answer different questions. <b>Saved</b> is what
    /// <c>config.cfg</c> will be written with (DP <c>Cvar_WriteVariables</c>: archived AND changed), i.e. what
    /// follows you to the next launch. <b>Session only</b> is changed but NOT archived — a console <c>set</c> on
    /// a server-op or debug cvar, or a <c>--cvar</c> pin — which is exactly the class of change that makes a
    /// machine behave oddly and then evaporates on restart, so it is worth seeing separately.</para>
    /// </summary>
    private void CmdCvarChanges(IReadOnlyList<string> a)
    {
        string? filter = a.Count >= 2 ? a[1] : null;

        var saved = new List<string>();
        var session = new List<string>();
        foreach (string name in _cvars.Names.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!_cvars.IsModified(name))
                continue;
            (_cvars.IsArchived(name) ? saved : session).Add(name);
        }

        if (saved.Count == 0 && session.Count == 0)
        {
            _print(filter is null
                ? "No cvars differ from their shipped defaults — this is a stock configuration."
                : $"No cvars matching \"{filter}\" differ from their shipped defaults.");
            return;
        }

        if (saved.Count > 0)
        {
            _print($"^5{saved.Count}^7 saved to your config (persist across launches):");
            foreach (string name in saved)
                _print("  " + FormatChange(name));
        }
        if (session.Count > 0)
        {
            _print($"^5{session.Count}^7 changed for this session only (not saved — a console `set`, "
                 + "a --cvar pin, or a server-op/debug cvar):");
            foreach (string name in session)
                _print("  " + FormatChange(name));
        }
    }

    /// <summary>One <c>cvar_changes</c> row: <c>name  "now"  (default "was")  description</c>.</summary>
    private string FormatChange(string name)
    {
        string desc = _cvars.GetDescription(name);
        string line = $"^3{name}^7 \"{_cvars.GetString(name)}^7\" ^8(was \"{_cvars.GetDefault(name)}^8\")^7";
        return desc.Length > 0 ? line + " " + desc : line;
    }

    private void CmdCmdList(IReadOnlyList<string> a)
    {
        string? filter = a.Count >= 2 ? a[1] : null;
        var names = AllCommandNames()
            .Where(name => filter == null || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (string name in names)
        {
            // DP Cmd_List_f prints each command with its description; an alias shows its body instead.
            string help = _interp.CommandDescription(name);
            if (help.Length == 0 && InterpreterBuiltins.Contains(name, StringComparer.OrdinalIgnoreCase))
                help = InterpreterBuiltinHelp(name);
            if (help.Length == 0 && _interp.Aliases.TryGetValue(name, out string? body))
            {
                _print($"^5{name}^7: {Ellipsis(body, 100)}");
                continue;
            }
            _print(help.Length > 0 ? $"^2{name}^7: {help}" : $"^2{name}^7");
        }
        _print($"{names.Count} command(s)");
    }

    /// <summary>How many hits <c>search</c> prints before it starts dropping the least likely ones.</summary>
    private const int MaxSearchResults = 60;

    /// <summary>
    /// <c>search</c> / <c>apropos</c> — DP <c>Cmd_Apropos_f</c> (cmd.c:1400), widened two ways.
    ///
    /// <para>DP matched ONE glob against each name and description; every argument here is a separate keyword and
    /// ALL of them must appear, in the name or in the description. That is what makes a plain-English query work:
    /// <c>search max fps</c> finds <c>cl_maxfps</c>, which no single-pattern search ever could. A keyword carrying
    /// <c>*</c>/<c>?</c> is still globbed, so DP's <c>apropos g_balance_*</c> form is unchanged.</para>
    ///
    /// <para>Ranked, and printed WORST FIRST so the most likely answer is the last line — directly above the
    /// prompt, where it survives a long result set scrolling past. When the set overflows
    /// <see cref="MaxSearchResults"/> it is the LEAST likely end that is dropped, and the header says how many.</para>
    /// </summary>
    private void CmdApropos(IReadOnlyList<string> a)
    {
        if (a.Count < 2)
        {
            _print($"usage: {a[0]} <keyword> [keyword ...] — matches cvar/command names AND their descriptions;");
            _print("       best match is printed LAST. Wildcards (* ?) work in any keyword.");
            return;
        }

        var keywords = new List<string>();
        for (int i = 1; i < a.Count; i++)
            if (a[i].Length > 0)
                keywords.Add(a[i]);

        List<SearchHit> hits = ConsoleSearch.Rank(keywords, EnumerateSearchable());
        if (hits.Count == 0)
        {
            _print($"nothing matching \"{string.Join(' ', keywords)}\"");
            return;
        }

        // The header carries the count because the RESULTS end at the prompt: a trailing summary line (DP's
        // "%i results") would push the best match one line further away, which is the line this ordering exists
        // to protect.
        int shown = Math.Min(hits.Count, MaxSearchResults);
        int dropped = hits.Count - shown;
        _print(dropped > 0
            ? $"^5{hits.Count}^7 result{(hits.Count == 1 ? "" : "s")} for \"{string.Join(' ', keywords)}\" — best last, {dropped} less likely omitted:"
            : $"^5{hits.Count}^7 result{(hits.Count == 1 ? "" : "s")} for \"{string.Join(' ', keywords)}\" — best last:");

        for (int i = hits.Count - shown; i < hits.Count; i++)
            PrintSearchHit(hits[i]);
    }

    /// <summary>Every console entity <c>search</c> looks through: cvars (with their help strings), registered
    /// commands + interpreter builtins (ditto), and aliases (matched on their body, DP's <c>alias-&gt;value</c>).</summary>
    private IEnumerable<SearchCandidate> EnumerateSearchable()
    {
        foreach (string name in _cvars.Names)
            yield return new SearchCandidate(SearchKind.Cvar, name, _cvars.GetDescription(name));
        foreach (string name in _interp.CommandNames)
            yield return new SearchCandidate(SearchKind.Command, name, _interp.CommandDescription(name));
        foreach (string name in InterpreterBuiltins)
            yield return new SearchCandidate(SearchKind.Command, name, InterpreterBuiltinHelp(name));
        foreach (var kv in _interp.Aliases)
            yield return new SearchCandidate(SearchKind.Alias, kv.Key, kv.Value);
    }

    /// <summary>One search hit, in DP's <c>Cmd_Apropos_f</c> shape and colours (cvar ^3, command ^2, alias ^5).</summary>
    private void PrintSearchHit(in SearchHit h)
    {
        switch (h.Kind)
        {
            case SearchKind.Cvar:
                _print("cvar    " + FormatCvarHelp(h.Name));
                break;
            case SearchKind.Alias:
                // An alias body can be a whole script; keep the line readable.
                _print($"alias   ^5{h.Name}^7: {Ellipsis(h.Description, 120)}");
                break;
            default:
                _print(h.Description.Length > 0
                    ? $"command ^2{h.Name}^7: {h.Description}"
                    : $"command ^2{h.Name}^7");
                break;
        }
    }

    /// <summary>DP <c>Cvar_PrintHelp</c>'s text, as a string (the search/completion printers wrap it themselves).</summary>
    private string FormatCvarHelp(string name)
    {
        string desc = _cvars.GetDescription(name);
        string line = $"^3{name}^7 is \"{_cvars.GetString(name)}^7\" [\"{_cvars.GetDefault(name)}^7\"]";
        return desc.Length > 0 ? line + " " + desc : line;
    }

    private static string Ellipsis(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private void CmdHelp(IReadOnlyList<string> a)
    {
        if (a.Count >= 2)
        {
            string name = a[1];
            if (_interp.CommandNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                string d = _interp.CommandDescription(name);
                _print(d.Length > 0 ? $"command ^2{name}^7: {d}" : $"^2{name}^7 is a command");
            }
            else if (InterpreterBuiltins.Contains(name, StringComparer.OrdinalIgnoreCase))
                _print($"command ^2{name}^7: {InterpreterBuiltinHelp(name)}");
            else if (_interp.Aliases.TryGetValue(name, out string? body))
                _print($"alias   ^5{name}^7: {body}");
            else if (_cvars.Has(name))
                PrintCvar(name);
            else
                _print($"no command or cvar named \"{name}\"");
            return;
        }
        // Laid out in fixed character columns — the console renders in a monospace face precisely so tables like
        // this one line up. The colour code wraps the PADDED field so the padding is counted in visible
        // characters, not in the raw string (a ^3 costs two characters and no width).
        static string Cell(string name, int width) => "^3" + name.PadRight(width) + "^7";
        void Row(string a, string b, string c = "", string d = "")
            => _print("  " + Cell(a, 18) + b.PadRight(c.Length > 0 ? 20 : 0) + Cell(c, c.Length > 0 ? 18 : 0) + d);

        _print("VortexArena console — type a command, or `cvar value` to change a setting.");
        Row("search <words>", "find a cvar or command by keyword — searches descriptions too, best match last");
        Row("help <name>", "what one cvar or command does, with its current value and default");
        Row("cvarlist [filter]", "list cvars", "cmdlist [filter]", "list commands");
        Row("cvar_changes", "what YOUR setup changes from the shipped defaults");
        Row("bind <key> <cmd>", "bind a key", "bindlist", "list every bind");
        Row("toggle <cvar>", "flip a setting", "exec <file.cfg>", "run a script");
        Row("connect <addr>", "join a server", "disconnect", "leave the match");
        Row("map <name>", "host a match", "condump [file]", "save this scrollback");
        _print("Keys: ^3Tab^7 complete · ^3Up^7/^3Down^7 history · ^3Ctrl+R^7 search history · " +
               "^3PgUp^7/^3PgDn^7 scroll · ^3Ctrl+L^7 clear · ^3Ctrl+-^7/^3Ctrl+=^7 text size");
    }

    // =============================================================================================
    //  console-surface commands (DP console.c Con_ConDump_f + keys.c Key_History_f)
    // =============================================================================================

    /// <summary>Host hook: the console scrollback as plain text, for <c>condump</c>. Wired by the overlay (which
    /// owns the scrollback); null on a headless console, where <c>condump</c> reports it has nothing to dump.</summary>
    public Func<string>? ScrollbackProvider { get; set; }

    /// <summary>Host hook: write <c>(path, text)</c> to the user data directory, returning the path actually
    /// written (for the confirmation line) or null on failure. Wired by the overlay; null → <c>condump</c> is
    /// reported as unavailable rather than silently doing nothing.</summary>
    public Func<string, string, string?>? FileWriter { get; set; }

    /// <summary>The input history the <c>history</c> command lists. Wired by the overlay, which owns it.</summary>
    public ConsoleHistory? History { get; set; }

    /// <summary>
    /// DP <c>Con_ConDump_f</c> (console.c:802): write the console scrollback to a file. DP defaults the name to
    /// <c>condump.txt</c> and appends <c>.txt</c> when the argument has no extension; <c>condump_stripcolors</c>
    /// controls whether <c>^</c> codes survive into the file (default: they do not, so the dump is readable in a
    /// text editor).
    /// </summary>
    private void CmdCondump(IReadOnlyList<string> a)
    {
        if (ScrollbackProvider is null || FileWriter is null)
        {
            _print("condump: no console scrollback here (this console has no display).");
            return;
        }
        string name = a.Count >= 2 ? a[1] : "condump.txt";
        if (name.IndexOf('.') < 0)
            name += ".txt";

        string text = ScrollbackProvider() ?? "";
        // DP condump_stripcolors (default 0): the dump keeps its ^-codes unless you ask otherwise, so it can be
        // fed back into the game verbatim; turn it on for something readable to paste into a bug report.
        // GetFloat, not GetString, so an unregistered cvar reads 0 (= keep the codes) rather than "" != "0".
        if (_cvars.GetFloat("condump_stripcolors") != 0f)
            text = VortexArena.Common.Diagnostics.Log.StripColors(text);

        string? written = FileWriter(name, text);
        _print(written is null
            ? $"condump: could not write \"{name}\"."
            : $"Dumped console text to {written}.");
    }

    /// <summary>
    /// DP <c>Key_History_f</c> (keys.c:300): <c>history</c> lists the input history, <c>history -c</c> clears it,
    /// <c>history &lt;n&gt;</c> lists only the last n lines.
    /// </summary>
    private void CmdHistory(IReadOnlyList<string> a)
    {
        if (History is not { } h)
        {
            _print("history: unavailable (this console has no input line).");
            return;
        }
        if (a.Count >= 2 && a[1] == "-c")
        {
            h.Clear();
            _print("Command history cleared.");
            return;
        }

        int from = 0;
        if (a.Count >= 2 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n > 0 && n <= h.Count)
            from = h.Count - n;

        int width = h.Count.ToString(CultureInfo.InvariantCulture).Length;
        for (int i = from; i < h.Count; i++)
            _print($"^3{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)}^7 {h.Lines[i]}");
        if (h.Count == 0)
            _print("(no command history yet)");
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
