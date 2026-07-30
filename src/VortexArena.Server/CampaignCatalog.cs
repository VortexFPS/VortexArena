using System;
using System.Collections.Generic;

namespace XonoticGodot.Server;

/// <summary>
/// One campaign level as the MENU needs it — the C# successor to the menu-side <c>campaign_*</c> parallel
/// arrays (qcsrc/menu/xonotic/campaign.qc + qcsrc/common/campaign_file.qc on the <c>MENUQC</c> path). Unlike
/// the server's <see cref="CampaignEntry"/> (which drops the two description columns, the SVQC slice) this
/// keeps <see cref="ShortDesc"/>/<see cref="LongDesc"/> so the singleplayer screen can render the level title
/// and briefing text.
/// </summary>
public sealed class CampaignLevel
{
    /// <summary>The absolute level index — comment/blank lines are transparent to it, exactly like
    /// <see cref="Campaign.Load"/>, so this value can be handed straight to <c>_campaign_index</c>.</summary>
    public int Index;
    public string Gametype = "";
    public string MapName = "";
    public int Bots;
    public int BotSkill;
    public string FragLimit = ""; // "score+lead" form ("default"/"" have special meaning, see Campaign)
    public string TimeLimit = ""; // minutes ("default"/"" have special meaning)
    public string Mutators = "";  // "; a; b" settemp list
    public string ShortDesc = ""; // column 7 — the level title, e.g. "Deathmatch: Boil"
    public string LongDesc = "";  // column 8 — the briefing text (\n-separated lines)
}

/// <summary>
/// Reads the WHOLE campaign <c>.txt</c> for the front-end singleplayer/campaign list (QC
/// <c>XonoticCampaignList_loadCvars</c> → <c>CampaignFile_Load</c> on the menu side). It reuses
/// <see cref="Campaign.ParseCsvLine"/> and mirrors <see cref="Campaign.Load"/>'s line classification exactly,
/// so a level's <see cref="CampaignLevel.Index"/> equals the <c>_campaign_index</c> the server resolves it at
/// — picking level <c>i</c> in the menu and booting with <c>_campaign_index = i</c> load the same level.
///
/// The server's <see cref="Campaign"/> deliberately holds only two entries at a time
/// (<see cref="Campaign.MaxEntries"/>) and drops the descriptions; this is the menu's complement — all levels,
/// with descriptions — and is intentionally pure/Godot-free so it stays unit-testable next to its sibling.
/// </summary>
public static class CampaignCatalog
{
    /// <summary>The default campaign id (QC <c>g_campaign_name</c>); selects <c>maps/campaign&lt;id&gt;.txt</c>
    /// and the <c>g_campaign&lt;id&gt;_index</c> progress cvar. Stock Xonotic ships only this one.</summary>
    public const string DefaultName = "xonoticbeta";

    /// <summary>The VFS path of a campaign file for the given id (QC <c>strcat("maps/campaign", name, ".txt")</c>).</summary>
    public static string FileName(string name) => $"maps/campaign{name}.txt";

    /// <summary>
    /// Parse every level out of a campaign file's <paramref name="text"/>. <paramref name="title"/> receives the
    /// <c>//campaign:</c> header (QC <c>campaign_title</c>). Comment (<c>//</c>) and blank lines are transparent
    /// to the index — only data rows advance it — so the indices line up with <see cref="Campaign.Load"/>.
    /// Returns an empty list (and empty title) for null/blank input.
    /// </summary>
    public static List<CampaignLevel> Parse(string? text, out string title)
    {
        var list = new List<CampaignLevel>();
        title = "";
        if (string.IsNullOrEmpty(text))
            return list;

        int lineno = 0;
        foreach (string l in SplitLines(text))
        {
            if (l.Length == 0)
                continue; // blank: transparent (no lineno++)

            if (l.Length >= 11 && l.StartsWith("//campaign:", StringComparison.Ordinal))
                title = l.Substring(11); // header, then fall through to the comment skip
            if (l.Length >= 2 && l[0] == '/' && l[1] == '/')
                continue; // comment
            if (l.Length >= 12 && l.StartsWith("\"//campaign:", StringComparison.Ordinal))
                title = l.Substring(12, Math.Max(0, l.Length - 13));
            if (l.Length >= 3 && l[0] == '"' && l[1] == '/' && l[2] == '/')
                continue; // quoted comment

            // A data row: parse it (a malformed row with < 7 fields still consumes an index, matching
            // Campaign.Load, so a later level's index can never drift out of parity with the server).
            List<string> f = Campaign.ParseCsvLine(l);
            if (f.Count >= 7)
                list.Add(new CampaignLevel
                {
                    Index = lineno,
                    Gametype = f[0],
                    MapName = f[1],
                    Bots = ParseInt(f[2]),
                    BotSkill = ParseInt(f[3]),
                    FragLimit = f[4],
                    TimeLimit = f[5],
                    Mutators = f[6],
                    ShortDesc = f.Count > 7 ? f[7] : "",
                    LongDesc = f.Count > 8 ? f[8] : "",
                });
            lineno++;
        }
        return list;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            yield return line;
    }

    private static int ParseInt(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? (int)f : 0;
}
