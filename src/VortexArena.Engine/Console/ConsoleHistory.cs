using System;
using System.Collections.Generic;
using System.Text;

namespace VortexArena.Engine.Console;

/// <summary>
/// The console input history — the C# successor to DarkPlaces' <c>Key_History_*</c> family
/// (Base/darkplaces/keys.c:50-324) and its <c>history</c> command.
///
/// <para>More than a list of previous lines. DP models an <b>editing cursor</b> into the history: index −1 means
/// "typing a fresh line", and stepping off −1 stashes the half-typed line so walking Up through old commands and
/// back Down returns you to exactly what you were writing (<see cref="SavedLine"/>). On top of that sits an
/// incremental pattern search: <see cref="FindBackwards"/>/<see cref="FindForwards"/> <em>point</em> the cursor at
/// a match and print it WITHOUT fetching it into the edit line, so repeated Ctrl+R keeps walking back through
/// matches; the next Up/Down is what actually pulls the found line in (<see cref="TakeFoundCommand"/>).</para>
///
/// <para>Deliberately excluded from the history, as in DP: empty lines, and anything beginning <c>quit</c> or
/// <c>rcon_password</c> — the first because recalling it by accident is expensive, the second because a password
/// should not be one Up-arrow away (nor written to the history file).</para>
///
/// <para>Godot-free and IO-free: <see cref="Load"/>/<see cref="Save"/> take and return plain text, so the host
/// decides where the file lives and this stays unit-testable.</para>
/// </summary>
public sealed class ConsoleHistory
{
    /// <summary>DP <c>HIST_MAXLINES</c> — how many input lines are kept.</summary>
    public const int MaxLines = 256;

    private readonly List<string> _lines = new();

    /// <summary>DP <c>history_line</c>: the cursor. −1 means "editing a fresh line, not navigating".</summary>
    private int _cursor = -1;

    /// <summary>DP <c>history_savedline</c>: the half-typed line stashed when navigation began.</summary>
    private string _saved = "";

    /// <summary>DP <c>history_searchstring</c>: what the current Ctrl+R run is searching for. A different string
    /// restarts the search from the end rather than continuing from the last hit.</summary>
    private string _searchString = "";

    /// <summary>DP <c>history_matchfound</c>: a search has pointed the cursor at a line that the next Up/Down
    /// should fetch instead of stepping past.</summary>
    private bool _matchFound;

    /// <summary>Every remembered line, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>How many lines are remembered.</summary>
    public int Count => _lines.Count;

    /// <summary>The stashed in-progress line (what Down returns to at the end of the history).</summary>
    public string SavedLine => _saved;

    /// <summary>True while the cursor is walking the history rather than editing a fresh line.</summary>
    public bool IsNavigating => _cursor >= 0;

    // =============================================================================================
    //  recording
    // =============================================================================================

    /// <summary>
    /// DP <c>Key_History_Push</c>: remember a submitted line and reset the cursor to "fresh line". Skips blanks
    /// and the two command prefixes DP refuses to record (<c>quit</c>, <c>rcon_password</c>). Consecutive
    /// duplicates collapse — DP keeps them, but a scrollback console makes the repetition visible and annoying.
    /// </summary>
    public void Push(string line)
    {
        _cursor = -1;
        _matchFound = false;
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (IsExcluded(line))
            return;
        if (_lines.Count > 0 && string.Equals(_lines[^1], line, StringComparison.Ordinal))
            return;
        _lines.Add(line);
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);
    }

    /// <summary>DP's history exclusions: <c>quit</c>[ …] and <c>rcon_password</c>[ …] are never recorded.</summary>
    private static bool IsExcluded(string line)
    {
        foreach (string bad in new[] { "quit", "rcon_password" })
            if (line.Equals(bad, StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(bad + " ", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>DP <c>history -c</c>: forget everything.</summary>
    public void Clear()
    {
        _lines.Clear();
        _cursor = -1;
        _matchFound = false;
        _searchString = "";
        _saved = "";
    }

    // =============================================================================================
    //  navigation (DP Key_History_Up / _Down / _First / _Last)
    // =============================================================================================

    /// <summary>
    /// DP <c>Key_History_Up</c>. <paramref name="current"/> is what the user has typed so far (stashed if this is
    /// the first step off a fresh line). Returns the line to put in the input, or null to leave it alone.
    /// </summary>
    public string? Up(string current)
    {
        if (_cursor == -1)
            _saved = current;

        if (TakeFoundCommand() is string found)
            return found;

        if (_cursor == -1)
        {
            if (_lines.Count == 0)
                return null;
            _cursor = _lines.Count - 1;
            return _lines[_cursor];
        }
        if (_cursor > 0)
            return _lines[--_cursor];
        return null;                            // already at the oldest line — DP holds there
    }

    /// <summary>DP <c>Key_History_Down</c>: step toward the newest line; past the end, restore the stashed
    /// in-progress line. Returns null while already editing a fresh line (nothing to step to).</summary>
    public string? Down()
    {
        if (_cursor == -1)
            return null;

        if (TakeFoundCommand() is string found)
            return found;

        if (_cursor < _lines.Count - 1)
            return _lines[++_cursor];

        _cursor = -1;
        return _saved;
    }

    /// <summary>DP <c>Key_History_First</c> (Ctrl+,): jump to the oldest remembered line.</summary>
    public string? First(string current)
    {
        if (_cursor == -1)
            _saved = current;
        if (_lines.Count == 0)
            return null;
        _cursor = 0;
        return _lines[_cursor];
    }

    /// <summary>DP <c>Key_History_Last</c> (Ctrl+.): jump to the newest remembered line.</summary>
    public string? Last(string current)
    {
        if (_cursor == -1)
            _saved = current;
        if (_lines.Count == 0)
            return null;
        _cursor = _lines.Count - 1;
        return _lines[_cursor];
    }

    /// <summary>DP <c>Key_History_Get_foundCommand</c>: if a search pointed the cursor at a line, consume that
    /// pointer and return the line (so Up/Down after Ctrl+R fetches the match instead of stepping past it).</summary>
    private string? TakeFoundCommand()
    {
        if (!_matchFound)
            return null;
        _matchFound = false;
        return _cursor >= 0 && _cursor < _lines.Count ? _lines[_cursor] : null;
    }

    // =============================================================================================
    //  search (DP Key_History_Find_Backwards / _Forwards / _All)
    // =============================================================================================

    /// <summary>
    /// DP <c>Key_History_Find_Backwards</c> (Ctrl+R): find the newest line at or before the cursor matching
    /// <paramref name="partial"/> (wrapped in <c>*…*</c> unless it already carries wildcards), POINT the cursor at
    /// it and hand it back for echoing — without fetching it into the edit line, so pressing Ctrl+R again
    /// continues the search. A different search string restarts from the newest line.
    /// Returns <c>(index, line)</c>, or null when nothing matches.
    /// </summary>
    public (int Index, string Line)? FindBackwards(string partial)
    {
        if (_cursor == -1)
            _saved = partial;

        int i;
        if (!string.Equals(partial, _searchString, StringComparison.Ordinal))
        {
            _searchString = partial;
            i = _lines.Count - 1;
        }
        else i = _cursor == -1 ? _lines.Count - 1 : _cursor - 1;

        string pattern = ToPattern(partial);
        for (; i >= 0; i--)
            if (ConsoleSearch.Glob(_lines[i].ToLowerInvariant(), pattern))
            {
                _cursor = i;
                _matchFound = true;
                return (i, _lines[i]);
            }
        return null;
    }

    /// <summary>DP <c>Key_History_Find_Forwards</c> (Ctrl+Shift+R): the same search running toward the newest
    /// line. A no-op while editing a fresh line (there is nothing ahead of the cursor).</summary>
    public (int Index, string Line)? FindForwards(string partial)
    {
        if (_cursor == -1)
            return null;

        int i;
        if (!string.Equals(partial, _searchString, StringComparison.Ordinal))
        {
            _searchString = partial;
            i = 0;
        }
        else i = _cursor + 1;

        string pattern = ToPattern(partial);
        for (; i < _lines.Count; i++)
            if (ConsoleSearch.Glob(_lines[i].ToLowerInvariant(), pattern))
            {
                _cursor = i;
                _matchFound = true;
                return (i, _lines[i]);
            }
        return null;
    }

    /// <summary>DP <c>Key_History_Find_All</c> (Ctrl+F): every matching line, with its 1-based index and whether
    /// it is the one the cursor currently points at (DP prints that one green and the rest yellow).</summary>
    public List<(int Index, string Line, bool IsCurrent)> FindAll(string partial)
    {
        string pattern = ToPattern(partial);
        var result = new List<(int, string, bool)>();
        for (int i = 0; i < _lines.Count; i++)
            if (ConsoleSearch.Glob(_lines[i].ToLowerInvariant(), pattern))
                result.Add((i + 1, _lines[i], i == _cursor));
        return result;
    }

    /// <summary>DP's search-pattern rule: an empty query is <c>*</c>, a query already carrying <c>*</c>/<c>?</c>
    /// is used as-is, anything else is wrapped as <c>*query*</c>. Lower-cased for case-insensitive matching.</summary>
    private static string ToPattern(string partial)
    {
        string p = (partial ?? "").ToLowerInvariant();
        if (p.Length == 0)
            return "*";
        return p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0 ? p : "*" + p + "*";
    }

    // =============================================================================================
    //  persistence (DP Key_History_Init / _Shutdown — darkplaces_history.txt)
    // =============================================================================================

    /// <summary>
    /// DP <c>Key_History_Init</c>'s file read: one command per line, blanks dropped, oldest first, capped at
    /// <see cref="MaxLines"/>. Replaces whatever is currently remembered. Null/empty text is a no-op, so a first
    /// run with no history file is not a special case at the call site.
    /// </summary>
    public void Load(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _lines.Clear();
        foreach (ReadOnlySpan<char> raw in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = raw.Trim();
            if (line.Length == 0)
                continue;
            string s = line.ToString();
            if (IsExcluded(s))
                continue;                        // a history file from an older build may still hold one
            _lines.Add(s);
        }
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);
        _cursor = -1;
        _matchFound = false;
    }

    /// <summary>DP <c>Key_History_Shutdown</c>'s file write: the remembered lines, oldest first, newline-separated.</summary>
    public string Save()
    {
        var sb = new StringBuilder();
        foreach (string line in _lines)
            sb.Append(line).Append('\n');
        return sb.ToString();
    }
}
