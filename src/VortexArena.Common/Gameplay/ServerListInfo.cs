// Port of the data half of qcsrc/menu/xonotic/serverlist.qc: the `qcstatus` infostring parse that
// CategoryForEntry and drawListBoxItem both perform, the category assignment those feed, the override
// folding of CategoryOverride, and the exponential scroll averaging of qcsrc/menu/item/listbox.qc.
//
// The Godot-free home for all of it, for the same reason MenuPickerMath exists: the menu widgets under
// game/menu/ cannot be referenced by the test project, and this is the part of the server browser whose
// behaviour is defined by Base rather than by Godot — so it is the part that can silently drift.
namespace VortexArena.Common.Gameplay;

/// <summary>
/// The server-browser categories, in the QC's declaration order — which is also their sort order, since
/// <c>sethostcachesort</c> is given SLSF_CATEGORIES and the category number is the primary key
/// (serverlist.qh <c>SLIST_CATEGORIES</c>). Numbering starts at 1 to match CATEGORY_FIRST.
/// </summary>
public enum ServerCategory
{
    Favorited = 1,
    Recommended,
    Normal,
    Servers,
    Xpm,
    Modified,
    Overkill,
    Instagib,
    Defrag,
}

/// <summary>
/// What a server's <c>qcstatus</c> key says about it. Built by <c>WinningConditionHelper</c>
/// (qcsrc/server/scores.qc:452) as
/// <c>gametype:version:P&lt;purechanges&gt;:S&lt;freeslots&gt;:F&lt;serverflags&gt;:T&lt;tos&gt;:M&lt;modname&gt;::…</c>.
/// </summary>
public readonly record struct QcStatus(
    string Gametype,
    string Version,
    bool Pure,
    bool PureAvailable,
    int FreeSlots,
    int ServerFlags,
    string ModName,
    string TermsOfServiceUrl)
{
    /// <summary>What an absent or unparseable qcstatus means: nothing known.</summary>
    public static readonly QcStatus Unknown = new("", "", false, false, -1, -1, "", "");
}

/// <summary>
/// The Base-defined logic behind the server browser, factored out of the Godot widgets so it can be tested.
/// </summary>
public static class ServerListInfo
{
    /// <summary>SERVERFLAG_PLAYERSTATS — the server submits player statistics (common/constants.qh:19).</summary>
    public const int ServerFlagPlayerStats = 4;
    /// <summary>SERVERFLAG_PLAYERSTATS_CUSTOM — ... to a non-default stats server (common/constants.qh:20).</summary>
    public const int ServerFlagPlayerStatsCustom = 8;

    /// <summary>
    /// This game's own mod name, lowercased — the M token a VortexArena server puts in its qcstatus. Kept
    /// here rather than referenced from VortexArena.Net so this stays a leaf with no project dependencies;
    /// <c>GameIdentity.Name</c> is the authority and <c>ServerListInfoTests</c> pins the two together.
    /// </summary>
    public const string OwnModName = "vortex";

    /// <summary>
    /// The mods on which a reported purity value actually means something (serverlist.qc:900). Anywhere else
    /// the P token says nothing, so <see cref="QcStatus.PureAvailable"/> is cleared.
    /// </summary>
    private static bool PurityIsMeaningful(string modName)
        => modName is "xonotic" or OwnModName or "instagib" or "minstagib" or "cts" or "nix" or "newtoys";

    /// <summary>
    /// Split a <c>qcstatus</c> value. Token 0 is the gametype and token 1 the version; from token 2 on each is
    /// a one-letter key followed by its value, and an EMPTY token ends the header (what follows is the score
    /// label block). Unknown keys are skipped, so a newer server adding one doesn't break the parse.
    /// </summary>
    public static QcStatus ParseQcStatus(string? qcstatus)
    {
        if (string.IsNullOrEmpty(qcstatus))
            return QcStatus.Unknown;

        string[] tok = qcstatus.Split(':');
        string gametype = tok.Length > 0 ? tok[0] : "";
        string version = tok.Length > 1 ? tok[1] : "";
        bool pure = false, pureAvailable = false;
        int freeSlots = -1, flags = -1;
        string mod = "", tos = "";

        for (int j = 2; j < tok.Length; j++)
        {
            if (tok[j].Length == 0)
                break;
            string value = tok[j][1..];
            switch (tok[j][0])
            {
                case 'P':
                    // The count of settings changed from stock: 0 means "official settings".
                    pure = value == "0";
                    pureAvailable = true;
                    break;
                case 'S':
                    freeSlots = int.TryParse(value, out int s) ? s : -1;
                    break;
                case 'F':
                    flags = int.TryParse(value, out int f) ? f : -1;
                    break;
                case 'M':
                    mod = value.ToLowerInvariant();
                    break;
                case 'T':
                    // The whole line is colon-separated, so the server ships the URL with its colons swapped
                    // for pipes (world.qc:798 strreplace(":", "|", …)); undo that here.
                    tos = value.Replace('|', ':');
                    break;
            }
        }

        if (!PurityIsMeaningful(mod))
            pureAvailable = false;
        return new QcStatus(gametype, version, pure, pureAvailable, freeSlots, flags, mod, tos);
    }

    /// <summary>What <see cref="CategoryForEntry"/> needs to know about a row.</summary>
    public readonly record struct CategoryInput(
        bool IsFavorite,
        bool IsPromoted,
        bool IsRecommended,
        string ModName,
        bool Pure,
        bool PureAvailable,
        int QcFreeSlots,
        int Humans,
        int Ping);

    /// <summary>The <c>menu_slist_recommendations*</c> cvar family, passed in so this stays store-agnostic.</summary>
    public readonly record struct RecommendationRules(
        int Mode,
        float MaxPing,
        float MinFreeSlots,
        float MinHumans,
        float PureThreshold,
        float ModImpurity);

    /// <summary>
    /// The category a row belongs to before any override — a port of <c>CategoryForEntry</c>
    /// (serverlist.qc:117). Bookmarks win outright, then the recommendation vote, then the reported mod.
    /// </summary>
    public static ServerCategory CategoryForEntry(in CategoryInput e, in RecommendationRules rules)
    {
        if (e.IsFavorite)
            return ServerCategory.Favorited;

        float impure = e.PureAvailable && !e.Pure ? 1f : 0f;
        if (!IsStockGame(e.ModName))
            impure += rules.ModImpurity;

        if (rules.Mode != 0)
        {
            if (e.IsPromoted)
                return ServerCategory.Recommended;

            int vote = 0;
            if ((rules.Mode & 1) != 0)
                vote += e.IsRecommended ? +1 : -1;
            if ((rules.Mode & 2) != 0)
            {
                bool good = e.QcFreeSlots >= rules.MinFreeSlots
                            && (rules.PureThreshold < 0 || impure <= rules.PureThreshold)
                            && e.Humans >= rules.MinHumans
                            && e.Ping >= 0
                            && e.Ping <= rules.MaxPing;
                vote += good ? +1 : -1;
            }
            if (vote > 0)
                return ServerCategory.Recommended;
        }

        return e.ModName switch
        {
            // DIVERGENCE from Base, and the only one in this table: a VortexArena host reports its own mod
            // name, so it needs an arm of its own or it would fall through to the default and be listed as
            // somebody's modified Xonotic.
            "xonotic" or OwnModName => ServerCategory.Normal,
            // A server too old to report its mod name counts as modified.
            "" => ServerCategory.Modified,
            "xpm" => ServerCategory.Xpm,
            "instagib" or "minstagib" => ServerCategory.Instagib,
            "overkill" => ServerCategory.Overkill,
            // "cts" is kept as a compatibility spelling of xdf.
            "cts" or "xdf" => ServerCategory.Defrag,
            _ => ServerCategory.Modified,
        };
    }

    /// <summary>
    /// Fold a raw category through the override table — a port of <c>CategoryOverride</c> (serverlist.qc:101).
    /// With categories switched OFF (the shipped default) the hardcoded "disabled" column of SLIST_CATEGORIES
    /// applies, collapsing every ordinary category into a single <c>Servers</c> heading and leaving only
    /// Favorites and Recommended standing apart. Switched ON, the per-category
    /// <c>menu_slist_categories_CAT_*_override</c> cvars decide, read through
    /// <paramref name="enabledOverride"/>. An override naming an unknown category, or the category itself, is
    /// ignored — both are what the QC does.
    /// </summary>
    public static ServerCategory ApplyOverride(
        ServerCategory cat, bool categoriesEnabled, Func<string, string>? enabledOverride)
    {
        string key = CategoryKey(cat);
        string s = categoriesEnabled
            ? enabledOverride?.Invoke(key) ?? ""
            : DisabledOverride(cat);
        if (string.IsNullOrEmpty(s) || s == key)
            return cat;
        return ParseCategoryKey(s) ?? cat;
    }

    /// <summary>The hardcoded "categories off" override column of SLIST_CATEGORIES (serverlist.qh:150).</summary>
    private static string DisabledOverride(ServerCategory cat) => cat switch
    {
        ServerCategory.Favorited or ServerCategory.Recommended => "",
        _ => "CAT_SERVERS",
    };

    /// <summary>The cvar/table key for a category (<c>CAT_FAVORITED</c>, …).</summary>
    public static string CategoryKey(ServerCategory cat) => cat switch
    {
        ServerCategory.Favorited => "CAT_FAVORITED",
        ServerCategory.Recommended => "CAT_RECOMMENDED",
        ServerCategory.Normal => "CAT_NORMAL",
        ServerCategory.Servers => "CAT_SERVERS",
        ServerCategory.Xpm => "CAT_XPM",
        ServerCategory.Modified => "CAT_MODIFIED",
        ServerCategory.Overkill => "CAT_OVERKILL",
        ServerCategory.Instagib => "CAT_INSTAGIB",
        _ => "CAT_DEFRAG",
    };

    /// <summary>The inverse of <see cref="CategoryKey"/>; null for a key no category owns.</summary>
    public static ServerCategory? ParseCategoryKey(string key) => key switch
    {
        "CAT_FAVORITED" => ServerCategory.Favorited,
        "CAT_RECOMMENDED" => ServerCategory.Recommended,
        "CAT_NORMAL" => ServerCategory.Normal,
        "CAT_SERVERS" => ServerCategory.Servers,
        "CAT_XPM" => ServerCategory.Xpm,
        "CAT_MODIFIED" => ServerCategory.Modified,
        "CAT_OVERKILL" => ServerCategory.Overkill,
        "CAT_INSTAGIB" => ServerCategory.Instagib,
        "CAT_DEFRAG" => ServerCategory.Defrag,
        _ => null,
    };

    /// <summary>True for the unmodified game — either this one or the upstream it descends from.</summary>
    public static bool IsStockGame(string modName) => modName is "xonotic" or OwnModName;

    /// <summary>The untranslated heading a category draws (the SLCAT^ strings of serverlist.qh:150).</summary>
    public static string CategoryTitle(ServerCategory cat) => cat switch
    {
        ServerCategory.Favorited => "Favorites",
        ServerCategory.Recommended => "Recommended",
        ServerCategory.Normal => "Normal Servers",
        ServerCategory.Servers => "Servers",
        ServerCategory.Xpm => "Competitive Mode",
        ServerCategory.Modified => "Modified Servers",
        ServerCategory.Overkill => "Overkill",
        ServerCategory.Instagib => "InstaGib",
        _ => "Defrag Mode",
    };

    /// <summary>
    /// Split the server-browser filter box into its optional <c>gametype:</c> prefix and the free-text
    /// remainder — the <c>strstrofs(s, ":", 0)</c> parse in <c>refreshServerList</c>, including the
    /// "skip the spaces after the colon" step.
    /// </summary>
    public static (string Type, string Text) SplitFilter(string? raw)
    {
        string s = raw ?? "";
        int colon = s.IndexOf(':');
        if (colon < 0)
            return ("", s.Trim());
        return (s[..colon].Trim(), s[(colon + 1)..].TrimStart(' ').Trim());
    }

    /// <summary>
    /// One frame of the QC listbox scroll easing (item/listbox.qc:348-357): move <paramref name="pos"/> toward
    /// <paramref name="target"/> with the exponential average
    /// <c>pos = pos*f + target*(1-f)</c>, <c>f = exp(-dt/averagingTime)</c> — framerate-independent by
    /// construction — and snap once within <paramref name="epsilon"/>. An averaging time of 0 means "no
    /// smoothing", which the QC guards for the same way.
    /// </summary>
    public static double AdvanceScroll(double pos, double target, double dt, double averagingTime, double epsilon)
    {
        if (pos == target)
            return target;
        double f = averagingTime > 0 ? global::System.Math.Exp(-dt / averagingTime) : 0.0;
        pos = pos * f + target * (1.0 - f);
        return global::System.Math.Abs(pos - target) < epsilon ? target : pos;
    }
}
