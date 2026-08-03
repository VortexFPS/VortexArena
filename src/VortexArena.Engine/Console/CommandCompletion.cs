using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;

namespace VortexArena.Engine.Console;

/// <summary>What a Tab press turned the input line into, plus anything it wants echoed to the scrollback.</summary>
/// <param name="Line">The new input text.</param>
/// <param name="Caret">Where the caret should sit in <paramref name="Line"/>.</param>
/// <param name="Output">Lines to print (already carrying <c>^</c> colour codes), empty when Tab just completed.</param>
public readonly record struct CompletionOutcome(string Line, int Caret, IReadOnlyList<string> Output);

/// <summary>
/// Tab completion — the C# successor to DarkPlaces' <c>Con_CompleteCommandLine</c> (console.c:2898) together
/// with the <c>Cmd_Complete*</c>/<c>Cvar_Complete*</c> helpers it calls.
///
/// <para>The port previously completed a bare prefix against one flat pool of names and printed the matches as
/// a single run-on line. DP does considerably more, and all of it is what makes the console usable:</para>
///
/// <list type="bullet">
///   <item><b>Grouped, described results.</b> Matches are counted and printed per kind — "3 possible commands",
///     "12 possible variables", "2 possible aliases" — each entry with its help string (a cvar also shows its
///     value and default). Finding out what <c>cl_bob</c> does is a Tab press, not a trip to the wiki.</item>
///   <item><b>Completion across all three groups at once.</b> The line advances to the longest prefix common to
///     every match, whatever kind it is, and only gains a trailing space when exactly one thing matched.</item>
///   <item><b>Argument completion.</b> DP reads <c>con_completion_&lt;command&gt;</c> to learn what a command's
///     first argument looks like: <c>exec</c> completes <c>*.cfg</c>, <c>playdemo</c> completes <c>*.dem</c>,
///     and the value <c>"map"</c> means map names (which is how Xonotic wires <c>chmap</c>, <c>gotomap</c>,
///     <c>vmap</c>…). <c>map</c>/<c>changelevel</c>/<c>devmap</c> are map-completing unconditionally.</item>
///   <item><b>Ctrl+Tab</b> appends a cvar's CURRENT value to its name, so you can edit it in place instead of
///     retyping it (<see cref="AppendCvarValue"/>).</item>
/// </list>
///
/// <para>Two deliberate departures from DP. First, <c>bind</c>/<c>unbind</c> complete key names — DP has no such
/// rule and falls through to matching key names against cvars, which finds nothing; this is a port addition, not
/// parity. Second, a group with more than <see cref="CompactThreshold"/> matches is printed in DP's packed
/// <c>Con_DisplayList</c> columns (names only) instead of one described line each: a described dump of the 900
/// cvars starting <c>g_</c> would blow the scrollback away for no gain. Both are noted where they happen.</para>
///
/// <para>Godot-free: the filesystem and map catalog arrive as delegates, so this is unit-testable headlessly and
/// the overlay only has to render <see cref="CompletionOutcome.Output"/>.</para>
/// </summary>
public sealed class CommandCompletion
{
    /// <summary>Above this many matches in one group, print packed name-only columns instead of described lines.</summary>
    public const int CompactThreshold = 12;

    /// <summary>Hard cap on printed matches per group, so a stray bare Tab can't evict the whole scrollback.</summary>
    public const int MaxListed = 400;

    /// <summary>DP's token delimiters — completion starts after the nearest one before the caret.</summary>
    private static readonly char[] Delimiters = { '"', ';', ' ', '\'' };

    private readonly ConfigInterpreter _interp;
    private readonly CvarService _cvars;
    private readonly IReadOnlyList<string> _builtins;
    private readonly Func<string, string> _builtinHelp;

    /// <summary>
    /// DP <c>con_linewidth</c>: how many characters fit across the console, used to lay out the packed columns.
    /// The overlay keeps this in step with the real console width / <c>con_textsize</c>; 80 is a sane headless
    /// default.
    /// </summary>
    public int LineWidth { get; set; } = 80;

    /// <summary>Host hook: expand one glob (e.g. <c>*.cfg</c>, <c>models/player/*.iqm</c>) into matching content
    /// paths. Null → file-pattern completion is skipped and the generic name completion runs instead.</summary>
    public Func<string, IReadOnlyList<string>>? FileSearch { get; set; }

    /// <summary>Host hook: the installed map names, for <c>map</c>/<c>changelevel</c> and any command whose
    /// <c>con_completion_*</c> is <c>"map"</c>. Null → those fall back to generic name completion.</summary>
    public Func<IReadOnlyList<string>>? MapNames { get; set; }

    /// <summary>Host hook: bindable key names, for <c>bind</c>/<c>unbind</c> (a port addition — see the type
    /// remarks). Null → those fall back to generic name completion.</summary>
    public Func<IReadOnlyList<string>>? KeyNames { get; set; }

    /// <summary>Host hook: the display names of everyone on the server, colour codes included (DP
    /// <c>cl.scores[].name</c>). Null / empty at the menu, where there is nobody to complete.</summary>
    public Func<IReadOnlyList<string>>? NickNames { get; set; }

    /// <param name="interp">The command buffer whose commands + aliases are completed.</param>
    /// <param name="cvars">The cvar store whose names are completed (and whose values/help are printed).</param>
    /// <param name="builtins">Interpreter builtins (<c>set</c>, <c>exec</c>, …) — handled inside the dispatch
    /// switch, so they are not in <see cref="ConfigInterpreter.CommandNames"/> and must be folded in here.</param>
    /// <param name="builtinHelp">Help text for those builtins.</param>
    public CommandCompletion(ConfigInterpreter interp, CvarService cvars,
        IReadOnlyList<string> builtins, Func<string, string> builtinHelp)
    {
        _interp = interp ?? throw new ArgumentNullException(nameof(interp));
        _cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        _builtins = builtins ?? Array.Empty<string>();
        _builtinHelp = builtinHelp ?? (_ => "");
    }

    // =============================================================================================
    //  Tab (DP Con_CompleteCommandLine)
    // =============================================================================================

    /// <summary>Complete the token ending at <paramref name="caret"/> in <paramref name="line"/>.</summary>
    public CompletionOutcome Complete(string line, int caret)
    {
        line ??= "";
        caret = Math.Clamp(caret, 0, line.Length);

        // DP: walk back to the nearest delimiter; everything from there to the caret is the token to complete.
        int pos = caret;
        while (--pos >= 0 && Array.IndexOf(Delimiters, line[pos]) < 0) { }
        pos++;

        string head = line[..pos];
        string token = line[pos..caret];
        string tail = line[caret..];        // chars after the cursor, restored onto the end (DP's s2)

        // ---- argument completion: are we on the token right after the command name? ----
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space >= 0 && pos == space + 1)
        {
            string command = line[..space];
            if (TryCompleteArgument(command, token, head, tail, out CompletionOutcome argResult))
                return argResult;
        }

        // ---- generic: commands, then variables, then aliases (DP's three Cmd_/Cvar_Complete* passes) ----
        var output = new List<string>();

        List<string> commands = MatchingCommands(token);
        List<string> variables = _cvars.Names.Where(n => Prefix(n, token))
                                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> aliases = _interp.Aliases.Keys.Where(n => Prefix(n, token))
                                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> nicks = MatchingNicks(token);

        int total = commands.Count + variables.Count + aliases.Count + nicks.Count;
        if (total == 0)
            return new CompletionOutcome(line, caret, output);      // nothing matched: line untouched

        // DP prints all four groups BEFORE editing the line, and only when there is something to choose from —
        // a unique match completes silently.
        if (total > 1)
        {
            EmitGroup(output, commands, token, "command", 's',
                n => FormatCommand(n));
            EmitGroup(output, variables, token, "variable", 's',
                n => "  " + FormatCvar(n));
            EmitGroup(output, aliases, token, "alias", 'e',    // DP: "aliases", so the plural suffix is "es"
                n => $"  ^5{n}^7: {Ellipsis(_interp.Aliases[n], 100)}");
            EmitGroup(output, nicks, token, "nick", 's', n => "  " + n);
        }

        // The line advances to the longest prefix shared by EVERY match, across all four kinds.
        string common = LongestCommonPrefix(commands.Concat(variables).Concat(aliases).Concat(nicks).ToList());
        string completed = common.Length >= token.Length ? common : token;
        if (total == 1)
            completed += ' ';                                   // DP appends a space after a unique completion

        string newLine = head + completed + tail;
        return new CompletionOutcome(newLine, head.Length + completed.Length, output);
    }

    /// <summary>
    /// DP <c>Nicks_CompleteCountPossible</c> (console.c:2570): player names matching the prefix. Matched — and
    /// completed to — the COLOUR-STRIPPED name, because that is what a console argument wants:
    /// <c>kick ^1Play^7er</c> would have to be typed with the codes intact, while <c>kick Player</c> is what the
    /// server matches on. (DP re-attaches the colours because its nick completion is aimed at chat; here it is
    /// aimed at commands.) Nicks with spaces are quoted so they survive re-tokenizing.
    /// </summary>
    private List<string> MatchingNicks(string token)
    {
        if (NickNames is null || token.Length == 0)
            return new List<string>();      // a bare Tab must not dump the roster into the command list
        return NickNames()
            .Select(StripColors)
            .Where(n => n.Length > 0 && Prefix(n, token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Drop Quake <c>^N</c> / <c>^xRGB</c> colour codes from a display name.</summary>
    private static string StripColors(string s)
        => VortexArena.Common.Diagnostics.Log.StripColors(s ?? "").Trim();

    /// <summary>Registered commands + interpreter builtins matching the prefix, deduped and sorted.</summary>
    private List<string> MatchingCommands(string token)
        => _interp.CommandNames.Concat(_builtins)
            .Where(n => Prefix(n, token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// DP's per-group print: a <c>"\nN possible variables:"</c> header, then either one described line per match
    /// or — past <see cref="CompactThreshold"/> — the packed name-only columns (see the type remarks).
    /// </summary>
    private void EmitGroup(List<string> output, List<string> matches, string token,
        string noun, char pluralSuffix, Func<string, string> describe)
    {
        if (matches.Count == 0)
            return;

        string plural = matches.Count > 1 ? noun + pluralSuffix : noun;
        output.Add($"^5{matches.Count}^7 possible {plural}:");

        if (matches.Count > CompactThreshold)
        {
            foreach (string row in Columns(matches.Take(MaxListed).ToList(), LineWidth))
                output.Add(row);
            if (matches.Count > MaxListed)
                output.Add($"^8…and {matches.Count - MaxListed} more (narrow the prefix)^7");
            return;
        }

        foreach (string n in matches)
            output.Add(describe(n));
    }

    /// <summary>DP <c>Cmd_CompleteCommandPrint</c>'s line: <c>^2name^7: description</c>.</summary>
    private string FormatCommand(string name)
    {
        string help = _interp.CommandDescription(name);
        if (help.Length == 0)
            help = _builtinHelp(name);
        return help.Length > 0 ? $"  ^2{name}^7: {help}" : $"  ^2{name}^7";
    }

    /// <summary>DP <c>Cvar_PrintHelp</c>'s line: <c>^3name^7 is "value" ["default"] description</c>.</summary>
    private string FormatCvar(string name)
    {
        string desc = _cvars.GetDescription(name);
        string line = $"^3{name}^7 is \"{_cvars.GetString(name)}^7\" [\"{_cvars.GetDefault(name)}^7\"]";
        return desc.Length > 0 ? line + " " + desc : line;
    }

    // =============================================================================================
    //  argument completion (DP's con_completion_<command> block)
    // =============================================================================================

    /// <summary>Commands DP map-completes without needing a <c>con_completion_*</c> cvar.</summary>
    private static readonly string[] AlwaysMapCommands = { "map", "changelevel", "devmap" };

    /// <summary>Commands whose first argument is a key name. A port addition: DP has no key completion, and its
    /// generic fallback matches <c>MOUSE1</c> against cvar names, which never hits.</summary>
    private static readonly string[] KeyCommands = { "bind", "unbind" };

    private bool TryCompleteArgument(string command, string token, string head, string tail,
        out CompletionOutcome result)
    {
        result = default;

        // DP: `set con_completion_<command> "<patterns>"`; the literal "map" means map names.
        string patterns = _cvars.GetString("con_completion_" + command);

        if (AlwaysMapCommands.Contains(command, StringComparer.OrdinalIgnoreCase) || patterns == "map")
        {
            if (MapNames is null)
                return false;
            result = CompleteFromList(MapNames(), token, head, tail, "map");
            return true;
        }

        if (KeyCommands.Contains(command, StringComparer.OrdinalIgnoreCase) && KeyNames is not null)
        {
            result = CompleteFromList(KeyNames(), token, head, tail, "key");
            return true;
        }

        if (patterns.Length > 0 && FileSearch is not null)
        {
            result = CompleteFiles(patterns, token, head, tail);
            return true;
        }

        return false;   // no argument rule — fall through to generic name completion
    }

    /// <summary>Complete a token against a flat candidate list (map names, key names): the same
    /// common-prefix + list-on-ambiguity behaviour the generic path has.</summary>
    private CompletionOutcome CompleteFromList(IReadOnlyList<string> candidates, string token,
        string head, string tail, string noun)
    {
        var output = new List<string>();
        List<string> matches = candidates.Where(n => Prefix(n, token))
                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                         .ToList();
        if (matches.Count == 0)
            return new CompletionOutcome(head + token + tail, head.Length + token.Length, output);

        string completed;
        if (matches.Count == 1)
        {
            completed = matches[0] + " ";
        }
        else
        {
            output.Add($"^5{matches.Count}^7 possible {noun}{(matches.Count > 1 ? "s" : "")}:");
            foreach (string row in Columns(matches.Take(MaxListed).ToList(), LineWidth))
                output.Add(row);
            if (matches.Count > MaxListed)
                output.Add($"^8…and {matches.Count - MaxListed} more^7");
            string common = LongestCommonPrefix(matches);
            completed = common.Length >= token.Length ? common : token;
        }
        return new CompletionOutcome(head + completed + tail, head.Length + completed.Length, output);
    }

    /// <summary>
    /// DP's <c>con_completion_*</c> file completion: expand each space-separated glob, keep the results that
    /// start with the typed token, and complete to their common prefix. Directories are listed in blue with a
    /// trailing slash and a lone directory completes to <c>dir/</c>, exactly as DP does — so
    /// <c>playermodel models/pl&lt;Tab&gt;</c> walks into the folder rather than dead-ending.
    /// </summary>
    private CompletionOutcome CompleteFiles(string patterns, string token, string head, string tail)
    {
        var output = new List<string>();
        var files = new List<string>();
        var dirs = new List<string>();

        // A pattern containing '/' is absolute; a bare one searches the innermost directory the user typed.
        int slash = token.LastIndexOf('/');
        string dirPrefix = slash >= 0 ? token[..(slash + 1)] : "";

        foreach (string pattern in patterns.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string glob = pattern.Contains('/', StringComparison.Ordinal) ? pattern : dirPrefix + pattern;
            foreach (string path in FileSearch!(glob))
                if (Prefix(path, token) && !files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);
        }

        // DP also offers the directories under the innermost path, whatever the patterns were.
        foreach (string path in FileSearch!(dirPrefix + "*/"))
        {
            string d = path.TrimEnd('/');
            if (Prefix(d, token) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d);
        }

        if (files.Count == 0 && dirs.Count == 0)
            return new CompletionOutcome(head + token + tail, head.Length + token.Length, output);

        files.Sort(StringComparer.OrdinalIgnoreCase);
        dirs.Sort(StringComparer.OrdinalIgnoreCase);

        string completed;
        if (files.Count == 0 && dirs.Count == 1)
        {
            completed = dirs[0] + "/";            // step into the folder (no space — you're still typing a path)
        }
        else if (files.Count == 1 && dirs.Count == 0)
        {
            completed = files[0] + " ";
        }
        else
        {
            output.Add($"^5{files.Count + dirs.Count}^7 possible filenames:");
            foreach (string d in dirs.Take(MaxListed))
                output.Add($"  ^4{d}^7/");
            foreach (string f in files.Take(MaxListed))
                output.Add("  " + f);
            var all = new List<string>(dirs.Count + files.Count);
            all.AddRange(dirs);
            all.AddRange(files);
            string common = LongestCommonPrefix(all);
            completed = common.Length >= token.Length ? common : token;
        }
        return new CompletionOutcome(head + completed + tail, head.Length + completed.Length, output);
    }

    // =============================================================================================
    //  Ctrl+Tab (DP Key_Parse_CommonKeys' is_console && KM_CTRL branch)
    // =============================================================================================

    /// <summary>
    /// DP Ctrl+Tab: take the cvar name the caret is inside, jump to its end, and insert a space plus the cvar's
    /// CURRENT value — so <c>cl_bob^</c> becomes <c>cl_bob 0.02^</c>, ready to be edited into the new value
    /// instead of retyped. A no-op on a token that is not a live cvar.
    /// </summary>
    public CompletionOutcome AppendCvarValue(string line, int caret)
    {
        line ??= "";
        caret = Math.Clamp(caret, 0, line.Length);

        int start = caret;
        while (--start >= 0 && Array.IndexOf(Delimiters, line[start]) < 0) { }
        start++;

        int end = start;
        while (end < line.Length && Array.IndexOf(Delimiters, line[end]) < 0)
            end++;

        string name = line[start..end];
        if (name.Length == 0 || !_cvars.Has(name))
            return new CompletionOutcome(line, caret, Array.Empty<string>());

        string value = _cvars.GetString(name);
        if (value.Length == 0)
            return new CompletionOutcome(line, end, Array.Empty<string>());

        string inserted = " " + value;
        string newLine = line[..end] + inserted + line[end..];
        return new CompletionOutcome(newLine, end + inserted.Length, Array.Empty<string>());
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    private static bool Prefix(string name, string token)
        => token.Length == 0 || name.StartsWith(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>The longest case-insensitive common prefix of the matches, spelled as it appears in the first
    /// one (so completing <c>CL_<c/>Tab</c> yields the store's own casing).</summary>
    public static string LongestCommonPrefix(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return "";
        string first = items[0];
        int len = first.Length;
        for (int i = 1; i < items.Count; i++)
        {
            string s = items[i];
            int j = 0;
            while (j < len && j < s.Length && char.ToLowerInvariant(first[j]) == char.ToLowerInvariant(s[j]))
                j++;
            len = j;
            if (len == 0)
                break;
        }
        return first[..len];
    }

    /// <summary>
    /// DP <c>Con_DisplayList</c> (console.c:2452): pack names into equal-width columns across
    /// <paramref name="width"/> characters. Returns the rows.
    /// </summary>
    public static List<string> Columns(IReadOnlyList<string> items, int width)
    {
        var rows = new List<string>();
        if (items.Count == 0)
            return rows;

        int cellWidth = items.Max(s => s.Length) + 2;      // DP: maxlen + 1, +1 more so columns visibly separate
        int perRow = Math.Max(1, (width - 2) / cellWidth);

        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append(items[i]);
            bool lastInRow = (i % perRow) == perRow - 1 || i == items.Count - 1;
            if (lastInRow)
            {
                rows.Add(sb.ToString().TrimEnd());
                sb.Clear();
            }
            else
            {
                sb.Append(' ', cellWidth - items[i].Length);
            }
        }
        return rows;
    }

    private static string Ellipsis(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
