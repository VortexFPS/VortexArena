using System.Text;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// One campaign level — the C# successor to the parallel QC arrays <c>campaign_gametype[i]</c> /
/// <c>campaign_mapname[i]</c> / … (common/campaign_common.qh). The server only needs the gameplay columns
/// (the two description columns are menu-only and dropped here, matching the QC <c>#ifdef SVQC</c> path).
/// </summary>
public sealed class CampaignEntry
{
    public string Gametype = "";
    public string MapName = "";
    public float Bots;
    public float BotSkill;
    public string FragLimit = ""; // "score+lead" form ("default" / "" have special meaning)
    public string TimeLimit = ""; // minutes ("default" / "" have special meaning)
    public string Mutators = "";  // "; a; b" settemp list
}

/// <summary>
/// The single-player campaign server core — a faithful Godot-free port of server/campaign.qc +
/// common/campaign_file.qc. It parses the campaign <c>.txt</c> (a quoted-CSV of levels), configures the
/// current level (gametype + bot count/skill + frag/time limits + mutator settemps), detects win/lose at
/// match end, persists progress to <c>campaign.cfg</c>, and advances (or replays) the level on transition.
///
/// Faithful to QC: comment/blank lines are transparent to the level index; <c>default</c> vs empty vs a value
/// are three distinct limit meanings; bot skill = <c>max(0, g_campaign_skill + per-level skill)</c>;
/// <c>sv_public</c>/<c>pausable</c>/limits are permanent sets while skill/bot_number/mutators are revertible
/// settemps; progress saves only at the frontier level and only when no cheats were used; a win requires the
/// local human to be the sole winner. The server holds only the current + next entry (QC
/// <c>CAMPAIGN_MAX_ENTRIES = 2</c>). The actual map/level transition is issued to the host through
/// <see cref="OnLevelTransition"/> (QC's <c>localcmd</c> + <c>MapInfo_LoadMap</c>).
/// </summary>
public sealed class Campaign
{
    /// <summary>QC <c>CAMPAIGN_MAX_ENTRIES</c> (server): the working buffer holds only current + next.</summary>
    public const int MaxEntries = 2;

    private readonly List<CampaignEntry> _entries = new();

    /// <summary>QC <c>campaign_title</c> (from the <c>//campaign:</c> header line).</summary>
    public string Title { get; private set; } = "";

    /// <summary>QC <c>campaign_offset</c>: the level index of <c>_entries[0]</c>.</summary>
    public int Offset { get; private set; }

    /// <summary>QC <c>campaign_level</c>: the absolute index of the CURRENT level.</summary>
    public int Level { get; private set; }

    /// <summary>QC <c>campaign_name</c>: the campaign id (selects the file + the progress cvar).</summary>
    public string Name { get; private set; } = "";

    /// <summary>QC <c>campaign_won</c>: 0 = stay/replay, 1 = advance (relative next-level offset).</summary>
    public int Won { get; private set; }

    /// <summary>QC <c>campaign_forcewin</c>: set by a level-end trigger to force a win this level.</summary>
    public bool ForceWin { get; set; }

    /// <summary>QC <c>campaign_bots_may_start</c>: gate bots/monsters/rounds until the human has spawned.</summary>
    public bool BotsMayStart { get; set; }

    /// <summary>The loaded levels (read-only) — <c>_entries[0]</c> is the current level after a load.</summary>
    public IReadOnlyList<CampaignEntry> Entries => _entries;

    /// <summary>The current level's gametype NetName (after <see cref="PreInit"/>), for the host to boot.</summary>
    public string CurrentGametype => _entries.Count > 0 ? _entries[0].Gametype : "";

    /// <summary>The current level's map name (after <see cref="PreInit"/>), for the host to load.</summary>
    public string CurrentMap => _entries.Count > 0 ? _entries[0].MapName : "";

    /// <summary>True once <see cref="PreInit"/> failed and the campaign was aborted (fall back to normal play).</summary>
    public bool Aborted { get; private set; }

    /// <summary>
    /// QC <c>Campaign_GetLevelNum</c> (<c>server/campaign.qc</c>): the 1-based level number shown on the client
    /// welcome/info dialog (<c>campaign_level + 1</c>). The host networks this in SendWelcomeMessage when
    /// <c>g_campaign</c>.
    /// </summary>
    public int LevelNum => Level + 1;

    /// <summary>Diagnostics sink (QC bprint/LOG). Defaults to swallowing.</summary>
    public Action<string>? Log { get; set; }

    private void Echo(string s) => Log?.Invoke(s);

    // ---- host I/O hooks ----

    /// <summary>Reads the campaign <c>.txt</c> by name (relative to the data root), or null if absent. Host-wired.</summary>
    public Func<string, string?>? FileReader { get; set; }

    /// <summary>Reads <c>campaign.cfg</c>'s contents (null if absent). Defaults to <see cref="System.IO"/>.</summary>
    public Func<string?>? CfgReader { get; set; }

    /// <summary>Writes <c>campaign.cfg</c>'s contents. Defaults to <see cref="System.IO"/>.</summary>
    public Action<string>? CfgWriter { get; set; }

    /// <summary>
    /// QC <c>GetMapname()</c>: the map the engine actually loaded (host-wired). Used by <see cref="Validate"/>
    /// (QC <c>Campaign_Invalid</c>) to confirm the loaded map matches the campaign file's level-0 entry. When
    /// unset the map check is skipped (the menu pre-resolves the map, so a mismatch is unlikely).
    /// </summary>
    public Func<string>? LoadedMapName { get; set; }

    /// <summary>
    /// QC <c>MapInfo_CurrentGametype() != MapInfo_Type_FromString(campaign_gametype[0])</c>: returns true when the
    /// running gametype does NOT match the campaign file's level-0 gametype NetName (host-wired). Used by
    /// <see cref="Validate"/>; when unset the gametype check is skipped.
    /// </summary>
    public Func<string, bool>? GametypeMismatch { get; set; }

    /// <summary>
    /// QC <c>CampaignSetup</c>: issued when the campaign transitions to a level. Args: (campaignName, index,
    /// mapName) — the host persists the index/name and loads the map. Without a hook, the cvars are still set.
    /// </summary>
    public Action<string, int, string>? OnLevelTransition { get; set; }

    /// <summary>
    /// QC <c>CampaignSaveCvar</c>'s <c>localcmd("seta …")</c> side: fired for each progress cvar saved
    /// (<c>g_campaign&lt;id&gt;_index</c> / <c>_won</c>). On an in-process listen server the host mirrors it to
    /// the shared (menu) cvar store so the front-end campaign list sees the new frontier — the world keeps a
    /// PRIVATE store, so without this the menu would never learn a level was won. Args: (name, value).
    /// </summary>
    public Action<string, float>? OnProgressSaved { get; set; }

    // =============================================================================================
    // file parsing (QC CampaignFile_Load / CampaignFile_Unload)
    // =============================================================================================

    /// <summary>
    /// QC <c>CampaignFile_Load(offset, n)</c>: parse up to <paramref name="n"/> levels starting at level-index
    /// <paramref name="offset"/> from the campaign file <c>maps/campaign&lt;Name&gt;.txt</c>. Returns the count
    /// loaded. Comment/blank lines are transparent to the index (they don't advance the level counter).
    /// </summary>
    public int Load(int offset, int n)
    {
        _entries.Clear();
        Offset = offset;
        // QC CampaignFile_Load wraps the name in language_filename() (i18n.qh): localized campaign briefings
        // live in maps/campaign<Name>.<lang>.txt and override the base file when present, else fall back to it.
        // Reproduce that: try the localized variant first when the active language is non-identity, else (or on
        // a miss) read the un-localized base name.
        string baseFn = $"maps/campaign{Name}.txt";
        string? text = null;
        string lang = Cvars.String("prvm_language");
        if (!XonoticGodot.Common.Localization.Ctx.IsIdentityLanguage(lang))
            text = FileReader?.Invoke($"maps/campaign{Name}.{lang}.txt");
        text ??= FileReader?.Invoke(baseFn);
        if (text is null)
            return 0;

        int lineno = 0;
        foreach (string raw in SplitLines(text))
        {
            string l = raw;
            if (l.Length == 0)
                continue; // blank: transparent (no lineno++)

            if (l.Length >= 11 && l.StartsWith("//campaign:", StringComparison.Ordinal))
                Title = l.Substring(11); // sets title, then falls through to the comment skip
            if (l.Length >= 2 && l[0] == '/' && l[1] == '/')
                continue; // comment
            if (l.Length >= 12 && l.StartsWith("\"//campaign:", StringComparison.Ordinal))
                Title = l.Substring(12, System.Math.Max(0, l.Length - 13));
            if (l.Length >= 3 && l[0] == '"' && l[1] == '/' && l[2] == '/')
                continue; // quoted comment

            if (lineno >= offset)
            {
                List<string> f = ParseCsvLine(l);
                if (f.Count < 7)
                    Echo($"^1campaign: line has too few fields: {l}");
                else
                {
                    _entries.Add(new CampaignEntry
                    {
                        Gametype = f[0],
                        MapName = f[1],
                        Bots = ParseFloat(f[2]),
                        BotSkill = ParseFloat(f[3]),
                        FragLimit = f[4],
                        TimeLimit = f[5],
                        Mutators = f[6],
                    });
                    if (_entries.Count >= n)
                        break;
                }
            }
            lineno++;
        }
        return _entries.Count;
    }

    /// <summary>QC <c>CampaignFile_Unload</c>: drop the loaded levels.</summary>
    public void Unload() => _entries.Clear();

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            yield return line;
    }

    /// <summary>Parse one quoted-CSV campaign line into its fields (quotes stripped, empty fields preserved).</summary>
    public static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { fields.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

    // =============================================================================================
    // level setup (QC CampaignPreInit / CampaignPostInit)
    // =============================================================================================

    /// <summary>
    /// QC <c>CampaignPreInit</c>: read the progress cvars, load current + next level, and apply the per-level
    /// settings (gametype, bot count/skill, mutator settemps, the permanent sv_public/pausable). After this the
    /// host boots with <see cref="CurrentGametype"/> / <see cref="CurrentMap"/>. Returns false (and sets
    /// <see cref="Aborted"/>) on an unknown/cheating level.
    /// </summary>
    public bool PreInit()
    {
        Aborted = false;
        Level = Cvars.Int("_campaign_index");
        Name = Cvars.String("_campaign_name");

        if (Load(Level, MaxEntries) < 1)
        {
            Bailout("unknown map");
            return false;
        }

        if (Cvars.Int("sv_cheats") != 0)
        {
            Unload();
            Bailout("cheats are enabled");
            return false;
        }

        float baseSkill = System.Math.Max(0f, Cvars.Float("g_campaign_skill") + _entries[0].BotSkill);
        ForceWin = false;
        BotsMayStart = false;

        // permanent sets (QC cvar_set).
        Cvars.Set("sv_public", "0");
        Cvars.Set("pausable", "1");

        // mutator settemps (QC the _MapInfo_Parse_Settemp loop over campaign_mutators).
        ApplyMutators(_entries[0].Mutators);

        // per-level settemps (revert when the level ends).
        SettempCvars.Set("g_campaign", "1");
        SettempCvars.Set("g_dm", "0");
        SettempCvars.Set("skill", baseSkill);
        SettempCvars.Set("bot_number", _entries[0].Bots);
        SettempCvars.Set("bot_vs_human", "0");

        // QC: MapInfo_SwitchGameType(...) is reproduced indirectly (the host boots with CurrentGametype),
        // then Campaign_Invalid() revalidates the running map/gametype against the loaded entry.
        if (!Validate())
            return false;

        return true;
    }

    /// <summary>
    /// QC <c>Campaign_Invalid</c> (<c>server/campaign.qc</c>): confirm the running gametype + map match the loaded
    /// level-0 entry, else <see cref="Bailout"/> and return false. Base calls it at the end of
    /// <c>CampaignPreInit</c> and the start of <c>CampaignPostInit</c>. The map/gametype probes are host-wired
    /// (<see cref="LoadedMapName"/> / <see cref="GametypeMismatch"/>); when a probe is unset its check is skipped.
    /// </summary>
    public bool Validate()
    {
        if (_entries.Count < 1)
            return true;

        if (GametypeMismatch is not null && GametypeMismatch(_entries[0].Gametype))
        {
            Bailout("wrong gametype!");
            return false;
        }

        if (LoadedMapName is not null)
        {
            string wanted = _entries[0].MapName;
            string actual = LoadedMapName();
            if (!string.Equals(wanted, actual, StringComparison.Ordinal))
            {
                Bailout($"wrong map: {wanted} != {actual}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// QC <c>CampaignPostInit</c>: apply the per-level frag/time limits (permanent sets). <c>default</c> leaves
    /// the implicit value; empty clears the limit; a value sets it. The fraglimit column is "score+lead".
    /// </summary>
    public void PostInit()
    {
        if (_entries.Count < 1)
            return;
        // QC: now some sanity checks — Campaign_Invalid() at PostInit entry. Aborted stops the limit application.
        if (!Validate())
            return;
        if (Cvars.Bool("_campaign_testrun"))
        {
            Cvars.Set("fraglimit", "0");
            Cvars.Set("leadlimit", "0");
            Cvars.Set("timelimit", "0.01");
            return;
        }

        string[] frag = _entries[0].FragLimit.Split('+');
        if (frag.Length >= 1 && frag[0] != "default") Cvars.Set("fraglimit", frag[0]);
        if (frag.Length >= 2 && frag[1] != "default") Cvars.Set("leadlimit", frag[1]);
        if (_entries[0].TimeLimit != "default") Cvars.Set("timelimit", _entries[0].TimeLimit);
    }

    private void ApplyMutators(string mutators)
    {
        if (string.IsNullOrWhiteSpace(mutators))
            return;
        foreach (string rawEntry in mutators.Split(';'))
        {
            string entry = rawEntry.Trim();
            if (entry.Length == 0) continue;
            if (entry.StartsWith("set ", StringComparison.Ordinal)) entry = entry.Substring(4).Trim();
            int sp = entry.IndexOf(' ');
            if (sp < 0) continue; // a bare flag with no value: skip (QC needs a value)
            string name = entry.Substring(0, sp);
            string value = entry.Substring(sp + 1).Trim();
            SettempCvars.Set(name, value);
        }
    }

    private void Bailout(string reason)
    {
        Cvars.Set("g_campaign", "0");
        Echo($"^4campaign initialization failed: {reason}");
        Aborted = true;
    }

    // =============================================================================================
    // win / lose + progress (QC CampaignPreIntermission / CampaignSaveCvar)
    // =============================================================================================

    /// <summary>
    /// QC <c>CampaignPreIntermission</c>: decide whether the current level was won, then (if won, cheat-free,
    /// and at the frontier level) save progress. <paramref name="isWinner"/> reports a player's
    /// <c>.winning</c>; <paramref name="checkrulesEquality"/> is the degenerate-tie flag; <paramref name="cheatCount"/>
    /// is the map's total cheat count (nonzero blocks the save). Returns the decided <see cref="Won"/>.
    /// </summary>
    public int PreIntermission(IEnumerable<Player> realClients, Func<Player, bool> isWinner,
        bool checkrulesEquality, int cheatCount, float timeNow)
    {
        int won = 0, lost = 0;
        foreach (Player p in realClients)
        {
            if (isWinner(p)) won++; else lost++;
        }

        float timelimit = Cvars.Float("timelimit");
        float fraglimit = Cvars.Float("fraglimit");
        bool overTime = timelimit != 0f && timeNow > timelimit * 60f;

        if (Cvars.Bool("_campaign_testrun"))
        {
            Won = 1; Echo("Campaign test run, advancing level.");
        }
        else if (ForceWin)
        {
            Won = 1; Echo("The current level has been WON.");
        }
        else if (won == 1 && lost == 0 && !checkrulesEquality)
        {
            if (timelimit != 0f && fraglimit != 0f && overTime)
            { Won = 0; Echo("Time's up! The current level has been LOST."); }
            else
            { Won = 1; Echo("The current level has been WON."); }
        }
        else if (overTime)
        {
            Won = 0; Echo("Time's up! The current level has been LOST.");
        }
        else
        {
            Won = 0; Echo("The current level has been LOST.");
        }

        // progress save: only at the frontier level, cheat-free, not a test run.
        if (Won != 0 && cheatCount == 0 && !Cvars.Bool("_campaign_testrun"))
        {
            string indexVar = $"g_campaign{Name}_index";
            if (Level == Cvars.Int(indexVar))
            {
                if (_entries.Count < 2)
                {
                    SaveCvar($"g_campaign{Name}_won", 1);
                    SaveCvar(indexVar, Level + 1);
                }
                else
                {
                    SaveCvar(indexVar, Level + 1);
                }
            }
        }

        return Won;
    }

    /// <summary>
    /// QC <c>CampaignSaveCvar</c>: persist a <c>set &lt;name&gt; &lt;value&gt;</c> line into <c>campaign.cfg</c>,
    /// preserving all unrelated lines, and set the live cvar. Read-modify-write of the simple key/value config.
    /// </summary>
    public void SaveCvar(string name, float value)
    {
        string v = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Cvars.Set(name, v);
        OnProgressSaved?.Invoke(name, value); // mirror to the shared menu store (QC the seta side)

        string existing = (CfgReader is not null ? CfgReader() : TryRead("campaign.cfg")) ?? "";
        var sb = new StringBuilder();
        foreach (string line in existing.Replace("\r\n", "\n").Split('\n'))
        {
            string[] tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 3 && tok[0] == "set" && tok[1] != name)
                sb.Append("set ").Append(tok[1]).Append(' ').Append(tok[2]).Append('\n');
        }
        sb.Append("set ").Append(name).Append(' ').Append(v).Append('\n');

        if (CfgWriter is not null) CfgWriter(sb.ToString());
        else TryWrite("campaign.cfg", sb.ToString());
    }

    private static string? TryRead(string file)
    {
        try { return System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null; }
        catch { return null; }
    }

    private static void TryWrite(string file, string text)
    {
        try { System.IO.File.WriteAllText(file, text); } catch { /* read-only host */ }
    }

    // =============================================================================================
    // transition (QC CampaignPostIntermission / CampaignSetup / CampaignLevelWarp)
    // =============================================================================================

    /// <summary>
    /// QC <c>CampaignPostIntermission</c>: change the map after intermission. When the LAST level was won the
    /// campaign is over (returns false — the host pops the menu). Otherwise sets up the next level
    /// (<see cref="Won"/> = 1 advance, 0 replay) and returns true.
    /// </summary>
    public bool PostIntermission()
    {
        if (Won != 0 && _entries.Count < 2)
        {
            Echo("^2campaign complete");
            Unload();
            return false; // campaign finished
        }
        Setup(Won);
        Unload();
        Name = "";
        return true;
    }

    /// <summary>
    /// QC <c>CampaignSetup(n)</c>: transition to level <c>Offset + n</c> — persist the index/name cvars and ask
    /// the host to load <c>_entries[n]</c>'s map (via <see cref="OnLevelTransition"/>).
    /// </summary>
    public void Setup(int n)
    {
        int index = Offset + n;
        Cvars.Set("g_campaign", "1");
        Cvars.Set("_campaign_name", Name);
        Cvars.Set("_campaign_index", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string map = (n >= 0 && n < _entries.Count) ? _entries[n].MapName : CurrentMap;
        OnLevelTransition?.Invoke(Name, index, map);
    }

    /// <summary>
    /// QC <c>CampaignLevelWarp(n)</c>: jump to level <paramref name="n"/> (or the next when &lt;0). Reloads the
    /// single target entry and sets it up. Used by the <c>warp</c> command + the <c>target_levelwarp</c> entity.
    /// </summary>
    public void LevelWarp(int n)
    {
        if (n < 0) n = Level + 1;
        Unload();
        if (Load(n, 1) > 0)
            Setup(0); // offset == n, so offset+0 == n
        else
            Echo("^1campaign: level warp target out of range");
        Unload();
    }
}
