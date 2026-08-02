using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VortexArena.Server;

/// <summary>
/// Where the map list lives. C# successor to QC <c>common/maplist.qc</c>, resolved in precedence order:
///
/// <list type="number">
/// <item>the <c>g_maplist</c> cvar, if set — a curated list, exactly as before. QC caps a cvar at 16383
/// bytes (roughly 1300 maps); the port has no such limit but keeps the same tier so configs, the
/// <c>maplist</c> command and the menu all keep behaving identically.</item>
/// <item><see cref="FilePath"/>, if it exists — one map name per line, no length limit. Written when a
/// selection outgrows the cvar, or supplied by hand.</item>
/// <item>every installed map — resolved fresh each time.</item>
/// </list>
///
/// <para><b>Case 3 is deliberately never saved.</b> Writing it out would turn a list that tracks the
/// installed maps into a frozen snapshot, and a map added afterwards would never enter the rotation until
/// someone deleted the file. Nothing configured means "whatever is installed right now", which is what an
/// empty <c>g_maplist</c> has always meant (xonotic-data#3002).</para>
///
/// <para>Names are stored unfiltered in all three cases, exactly like <c>g_maplist</c> always has been: the
/// gametype filter is applied when the list is read (<see cref="MapRotation.Init"/>), not when it is
/// written, so one list serves every gametype.</para>
/// </summary>
public static class MapListStore
{
    /// <summary>QC <c>MAPLIST_FILE</c>: the list file's name inside the user gamedir.</summary>
    public const string FileName = "maplist.txt";

    /// <summary>
    /// Absolute path of the list file. Null (the default) means no file tier at all — a bare server, a
    /// unit test, or a host that hasn't wired its gamedir — leaving cvar-or-everything, which is exactly
    /// the behaviour before a file store existed.
    /// </summary>
    public static string? FilePath { get; set; }

    /// <summary>QC <c>MAPLIST_CVAR_MAXLEN</c>: how much list a cvar is allowed to carry before the file
    /// takes over. Matches Base so the two agree on when a list stops fitting.</summary>
    public const int CvarMaxLen = 15000;

    public enum Source { Cvar, File, All }

    /// <summary>Which tier <see cref="Resolve"/> last answered from. Valid after a Resolve call.</summary>
    public static Source LastSource { get; private set; } = Source.All;

    /// <summary>
    /// The configured map list, unfiltered. <paramref name="installed"/> supplies tier 3 (the host's map
    /// catalog); it is only called if neither the cvar nor the file is set.
    /// </summary>
    public static List<string> Resolve(Func<IReadOnlyList<string>> installed)
    {
        var maps = new List<string>();

        string cvar = Cvars.String("g_maplist");
        if (!string.IsNullOrWhiteSpace(cvar))
        {
            LastSource = Source.Cvar;
            foreach (string m in cvar.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                maps.Add(m);
            return maps;
        }

        if (ReadFile(maps))
        {
            LastSource = Source.File;
            return maps;
        }

        // Tier 3 — resolved live, never written back (see the class remarks).
        LastSource = Source.All;
        maps.AddRange(installed());
        return maps;
    }

    /// <summary>
    /// Saves the list: into <c>g_maplist</c> when it fits (so small lists keep living in the cvar, where
    /// admins and configs expect them), otherwise into the file with <c>g_maplist</c> cleared. Returns
    /// which tier it used. This is only ever called for a deliberate edit, never to cache a resolve.
    /// </summary>
    public static Source Save(IReadOnlyList<string> maps)
    {
        int len = 0;
        foreach (string m in maps)
            len += m.Length + 1;

        if (len < CvarMaxLen || FilePath is null)
        {
            Cvars.Set("g_maplist", string.Join(' ', maps));
            return LastSource = Source.Cvar;
        }

        WriteFile(maps);
        Cvars.Set("g_maplist", "");
        return LastSource = Source.File;
    }

    /// <summary>Drops the file so the list falls back to the next tier down.</summary>
    public static void Forget()
    {
        if (FilePath is null) return;
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (IOException) { /* a read-only gamedir must not take the server down */ }
        catch (UnauthorizedAccessException) { }
    }

    // ---- file tier -----------------------------------------------------------------------------------

    private static bool ReadFile(List<string> into)
    {
        if (FilePath is null || !File.Exists(FilePath)) return false;

        string[] lines;
        try { lines = File.ReadAllLines(FilePath); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            // one map per line; # or // comments a line out, and anything after the name is ignored so
            // a comment can sit next to an entry
            if (line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            into.Add(sp < 0 ? line : line[..sp]);
        }
        return into.Count > 0;
    }

    private static void WriteFile(IReadOnlyList<string> maps)
    {
        if (FilePath is null) return;
        var sb = new StringBuilder();
        sb.Append("// Xonotic map list: one map per line, # or // comments a line out.\n");
        sb.Append("// Used when g_maplist is empty. Delete it to go back to using every installed map.\n");
        foreach (string m in maps)
            sb.Append(m).Append('\n');
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (IOException) { /* as above: losing persistence must not be fatal */ }
        catch (UnauthorizedAccessException) { }
    }
}
