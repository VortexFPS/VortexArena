namespace VortexArena.Common.Menu;

/// <summary>A mapinfo assignment. The catalog stores it without reading includes or changing cvars.</summary>
public readonly record struct MapTempDirective(string Mode, int Line, string Body)
{
    public const string SystemAcl = "-g_mapinfo_* -rcon_* -_* -g_ban* +*";

    // Map packs have a narrower surface than the console in the native host. These gameplay values
    // feed numeric rules, start items or weapon selection, and have no native command/file side effects.
    public static bool IsGameplaySetting(string name) =>
        name is "timelimit" or "fraglimit" or "leadlimit" or "bot_number" or "minplayers"
            or "g_use_ammunition" or "g_powerups" or "g_cts_finish_kill_delay"
        || name.StartsWith("g_balance_", StringComparison.Ordinal)
        || name.StartsWith("g_start_ammo_", StringComparison.Ordinal)
        || name.StartsWith("g_pickup_", StringComparison.Ordinal);

    public static bool IsSafeValue(string value) => value.IndexOfAny(['"', '\\', ';', '$', '\r', '\n', '\0']) < 0;

    // QC matchacl: first match, case-sensitive exact name or a single terminal prefix wildcard.
    public static bool Allows(string acl, string name)
    {
        foreach (string entry in acl.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string pattern = entry[0] is '-' or '+' ? entry[1..] : entry;
            bool matches = pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.Ordinal)
                : name.Equals(pattern, StringComparison.Ordinal);
            if (matches)
                return entry[0] != '-';
        }
        return false;
    }

    public IEnumerable<(string Name, string Value)> ReadAssignments(Func<string, string?>? readInclude)
    {
        if (ParseAssignment(Body) is not { } assignment)
            yield break;
        if (assignment.Name != "#include")
        {
            yield return assignment;
            yield break;
        }

        string path = assignment.Value;
        if (readInclude is null || !IsSafeValue(path) || !path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
            || path.Contains(':') || path.Split('/').Any(part => part is "" or "." or ".."))
            yield break;
        if (readInclude(path) is not { } text)
            yield break;

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Replace("\t", "", StringComparison.Ordinal);
            if (line.Length == 0 || line[0] is '#' or '_' || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("set ", StringComparison.Ordinal))
                line = line[4..];
            else if (line.StartsWith("seta ", StringComparison.Ordinal))
                line = line[5..];
            if (ParseAssignment(line) is { } included && included.Name != "#include")
                yield return included;
        }
    }

    private static (string Name, string Value)? ParseAssignment(string raw)
    {
        string text = raw.Replace("\t", "", StringComparison.Ordinal).TrimStart(' ');
        if (text.Length == 0)
            return null;
        int space = text.IndexOf(' ');
        string name = space < 0 ? text : text[..space];
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
            name = name[1..^1];

        string value = space < 0 ? "" : text[(space + 1)..].TrimStart(' ');
        if (value.StartsWith('"'))
            value = value[1..];
        int quote = value.IndexOf('"');
        if (quote >= 0)
            value = value[..quote];
        int comment = value.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
            value = value[..comment];
        return (name, value.TrimEnd(' '));
    }
}
