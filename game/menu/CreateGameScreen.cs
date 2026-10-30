using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Create" — the faithful C# port of <c>XonoticServerCreateTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_create.qc). Left half: the Gametype list (gametype icon + name +
/// free-for-all/teamplay column), "Show all", then the match settings — Time limit / per-gametype frag
/// limit / Teams / Player slots / Number of bots / Bot skill (all bound to the same cvars the QC binds:
/// <c>timelimit_override</c>, <c>fraglimit_override</c>&amp;friends, <c>menu_maxplayers</c>,
/// <c>bot_number</c>, <c>skill</c>) and "Bots against humans". Right half: the Maplist — rows carrying the
/// mapshot, title and author, dimmed when not in the <c>g_maplist</c> selection (click the preview to
/// toggle, like QC's clickListBoxItem) — with the filter box, Add/Remove shown/all and Mutators…
/// Bottom row: Leave current match | Start multiplayer!.
///
/// Start gathers the selection + cvars into a <see cref="MatchConfig"/> and fires
/// <see cref="StartGameRequested"/>; the host turns it into a real listen server (the QC equivalent was
/// <c>MapList_LoadMap</c>).
/// </summary>
public partial class CreateGameScreen : MenuScreen
{
    /// <summary>
    /// Fired when the user starts a match. The host subscribes once (e.g. at boot) and turns the
    /// <see cref="MatchConfig"/> into a real local server + client connect. Static so any instance of this
    /// screen shares one wiring point.
    /// </summary>
    public static event Action<MatchConfig>? StartGameRequested;

    /// <summary>
    /// Fire <see cref="StartGameRequested"/> from another menu screen (a C# event can only be invoked from
    /// its declaring type). Used by <see cref="SingleplayerScreen"/> so campaign levels share this wiring.
    /// </summary>
    public static void RaiseStartGame(MatchConfig config)
    {
        if (StartGameRequested is null)
            GD.Print($"[Menu] Start game ({config}) — no StartGame handler attached yet.");
        else
            StartGameRequested.Invoke(config);
    }

    // Optional starting selections (set before the screen is pushed; BuildUi reads them).
    private string? _initialGametype;
    private string? _initialMap;

    /// <summary>Pre-select this gametype (NetName) when the screen builds. Set before pushing.</summary>
    public string? InitialGametype { get => _initialGametype; init => _initialGametype = value; }

    /// <summary>Pre-select this map when the screen builds. Set before pushing.</summary>
    public string? InitialMap { get => _initialMap; init => _initialMap = value; }

    /// <summary>
    /// True when hosted as the Multiplayer dialog's Create TAB (QC XonoticServerCreateTab) rather than
    /// pushed as its own screen: no title, no Back button (the tab host owns navigation), tighter margins.
    /// </summary>
    public bool Embedded { get; set; }

    // ---------------------------------------------------------------------------------------------------
    //  Per-gametype menu config — QC gt.m_configuremenu → GameType_ConfigureSliders (label, cvar, range,
    //  teams cvar). Modes not listed fall back to the plain frag limit; "" frag cvar = no limit (cts).
    // ---------------------------------------------------------------------------------------------------
    private sealed record ModeMenu(string FragLabel, string FragCvar, float Min, float Max, float Step, string? TeamsCvar);

    private static readonly Dictionary<string, ModeMenu> ModeMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dm"]      = new("Frag limit:", "fraglimit_override", 5, 100, 5, null),
        ["duel"]    = new("Frag limit:", "fraglimit_override", 5, 100, 5, null),
        ["tdm"]     = new("Frag limit:", "fraglimit_override", 5, 100, 5, "g_tdm_teams_override"),
        ["ctf"]     = new("Capture limit:", "capturelimit_override", 1, 20, 1, null),
        ["ca"]        = new("Round limit:", "fraglimit_override", 5, 100, 5, "g_ca_teams_override"),
        ["freezetag"] = new("Round limit:", "fraglimit_override", 5, 100, 5, "g_freezetag_teams_override"),
        ["lms"]     = new("Lives:", "g_lms_lives_override", 3, 50, 1, null),
        ["kh"]      = new("Point limit:", "fraglimit_override", 200, 1500, 50, "g_keyhunt_teams_override"),
        ["dom"]     = new("Point limit:", "fraglimit_override", 50, 500, 10, "g_domination_teams_override"),
        ["ka"]      = new("Point limit:", "fraglimit_override", 5, 100, 5, null),
        ["tka"]     = new("Point limit:", "fraglimit_override", 5, 100, 5, "g_tka_teams_override"),
        ["mayhem"]  = new("Point limit:", "fraglimit_override", 5, 100, 5, null),
        ["tmayhem"] = new("Point limit:", "fraglimit_override", 5, 100, 5, "g_tmayhem_teams_override"),
        ["nb"]      = new("Goals:", "g_nexball_goallimit", 1, 50, 1, null),
        ["race"]    = new("Laps:", "g_race_laps_limit", 1, 25, 1, null),
        ["cts"]     = new("", "", 0, 0, 0, null),
        // Invasion: "Point limit:" label (50..500 step 10) but no cvar binding — Base m_configuremenu
        // passes string_null for pCvar (same as Assault/CTS/Onslaught), so the slider is disabled/greyed.
        // The live point-limit is set via g_invasion_point_limit / mapinfo pointlimit=50 at server start.
        ["inv"]     = new("Point limit:", "", 50, 500, 10, null),
    };

    // QC: only "priority" gametypes show unless menu_create_show_all, in the QC menu order (the stock list).
    private static readonly string[] DefaultGametypeOrder =
    {
        "dm", "tdm", "ctf", "ca", "freezetag", "mayhem", "tmayhem", "ka", "tka", "kh", "lms", "dom",
    };

    private static readonly HashSet<string> DefaultVisibleGametypes = new(DefaultGametypeOrder, StringComparer.OrdinalIgnoreCase);

    /// <summary>The skin icon name for a gametype (the lone NetName↔icon mismatch is freezetag → ft).</summary>
    private static string GametypeIconName(string netName)
        => netName.Equals("freezetag", StringComparison.OrdinalIgnoreCase) ? "ft" : netName;

    /// <summary>Top of the menu's "Player slots" range, and — less your own slot — of "Number of bots". Well under
    /// the engine ceiling (<c>ServerSlots.MaxSlots</c>, 255) because this is a slider a user drags: the console's
    /// <c>maxplayers</c> command is the way to ask for more than a listen server sensibly hosts.
    /// <para>Twice the shipped default (32, vortex-server.cfg) rather than equal to it — a slider whose default
    /// sits on its own maximum can only ever be dragged down, which is not a control.</para></summary>
    private const int MenuMaxSlots = 64;

    // Bot skill rungs, copied from the QC "skill" mixed-slider labels.
    private static readonly string[] BotSkillNames =
    {
        "Botlike", "Beginner", "You will win", "You can win", "You might win",
        "Advanced", "Expert", "Pro", "Assassin", "Unhuman", "Godlike",
    };

    private QcGametypeList _gametypeList = null!;
    private readonly List<GameType> _shownGametypes = new();
    private QcLabel _fragLabel = null!;
    private Control _fragCell = null!;
    private QcLabel _teamsLabel = null!;
    private Control _teamsCell = null!;
    private QcMapList _mapList = null!;
    private LineEdit _mapFilter = null!;
    private Button _leaveButton = null!;
    private string _selectedMap = "";
    private bool _refreshing;

    protected override void BuildUi()
    {
        Name = "CreateGameScreen";

        // XonoticServerCreateTab_fill (dialog_multiplayer_create.qc), TD for TD: intendedWidth 0.9, 23 rows,
        // 6.2 columns (the extra .2 is the gap between the halves).
        var tab = new QcGrid(rows: 23, columns: 6.2f, intendedWidth: 0.9f);
        tab.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(tab);

        // --- the left half ------------------------------------------------------------------------------
        tab.GotoRC(0.5f, 0f);
        tab.TD(1, 3, Header("Gametype"));
        tab.TR();
        _gametypeList = new QcGametypeList();
        _gametypeList.ItemSelected += _ => OnGametypeSelected();
        tab.TD(11, 3, _gametypeList);

        tab.GotoRC(12.5f, 0f);
        tab.TDempty(0.1f);
        var showAll = QcLook.CheckBox(Widgets.CheckBox("menu_create_show_all", "Show all", "Show all available gametypes"));
        showAll.Toggled += _ => RefreshGametypes();
        tab.TD(1, 2.9f, showAll);

        tab.GotoRC(14f, 0f);
        tab.TD(1, 1, new QcLabel("Time limit:"));
        tab.TD(1, 2, QcLook.Slider(Widgets.MixedSlider("timelimit_override", "Time limit in minutes that when hit, will end the match")
            .Add("Default", -1).AddRange(1, 10, 1).AddRange(15, 30, 5).AddRange(40, 60, 10).Add("Infinite", 0)
            .Finish()));
        tab.TR();
        _fragLabel = tab.TD(1, 1, new QcLabel("Frag limit:"));
        _fragCell = tab.TD(1, 2, new Control { MouseFilter = MouseFilterEnum.Pass });

        tab.GotoRC(16.5f, 0f);
        _teamsLabel = tab.TD(1, 1, new QcLabel("Teams:"));
        _teamsCell = tab.TD(1, 2, new Control { MouseFilter = MouseFilterEnum.Pass });
        tab.TR();
        tab.TD(1, 1, new QcLabel("Player slots:"));
        tab.TD(1, 2, QcLook.Slider(Widgets.Slider("menu_maxplayers", 1, MenuMaxSlots, 1,
            "The maximum amount of players or bots that can be connected to your server at once")));
        tab.TR();
        // Bots share the slot budget with humans, so this tracks the slots slider rather than the QC's fixed 9,
        // less the one slot you occupy. menu_loadmap_prepare's `bot_number+1` term (see MenuSlotCount) raises the
        // slot count to fit the bots asked for, so the two sliders cannot contradict each other.
        var botsLabel = tab.TD(1, 1, new QcLabel("Number of bots:"));
        var bots = tab.TD(1, 2, QcLook.Slider(Widgets.Slider("bot_number", 0, MenuMaxSlots - 1, 1, "Amount of bots on your server")));
        Dependent.Bind(bots, "bot_vs_human", 0, 0);
        Dependent.Bind(botsLabel, "bot_vs_human", 0, 0);
        tab.TR();
        var skillLabel = tab.TD(1, 1, new QcLabel("Bot skill:"));
        var skill = Widgets.MixedSlider("skill", "Specify how experienced the bots will be");
        for (int i = 0; i < BotSkillNames.Length; i++)
            skill.Add(BotSkillNames[i], i);
        tab.TD(1, 2, QcLook.Slider(skill.Finish()));
        // QC allowBotSkill is bots>0 OR bot_vs_human; the dominant gate is having bots at all.
        Dependent.Bind(skillLabel, "bot_number", 1, 99);
        Dependent.Bind(skill, "bot_number", 1, 99);
        tab.TR();
        tab.TDempty(0.1f);
        tab.TD(1, 2.9f, QcLook.CheckBox(Widgets.CheckBox("bot_vs_human", "Bots against humans",
            "Force humans to play on the same team against an equal-sized team of bots")));

        // --- the right half -----------------------------------------------------------------------------
        tab.GotoRC(0.5f, 3.2f);
        tab.SetFirstColumn(tab.CurrentColumn);
        tab.TD(1, 3, Header("Maplist"));
        tab.TR();
        _mapList = new QcMapList();
        _mapList.ItemSelected += i => { if (i >= 0 && i < _mapList.Entries.Count) _selectedMap = _mapList.Entries[i].Map; };
        _mapList.ToggleIncludeRequested += ToggleInclude;
        // QC XonoticMapList_doubleClickListBoxItem: the map info dialog, with this tab's gametype as the one to play.
        _mapList.ItemActivated += i =>
        {
            if (i < 0 || i >= _mapList.Entries.Count) return;
            _selectedMap = _mapList.Entries[i].Map;
            Menu?.Push(new DialogCreateGameMapInfo(_selectedMap, SelectedGametype()));
        };
        // "we use 7.8 here to visually match the component on the left"
        tab.TD(tab.Rows - 7.8f, 3, _mapList);

        tab.GotoRC(tab.Rows - 6f, 3.2f);
        tab.TD(1, 0.35f, new QcLabel("Filter:", 1f));
        _mapFilter = QcLook.InputBox(new LineEdit
        {
            TooltipText = Localization.Tr("Click here or CTRL+F to provide a keyword to narrow down the list; CTRL+DEL to clear; ENTER when done."),
        });
        _mapFilter.TextChanged += _ => RefilterMaps();
        tab.TD(1, tab.Columns - 3.2f - 0.35f, _mapFilter);

        tab.GotoRC(tab.Rows - 5f, 3.2f);
        tab.TDempty(0.2f);
        tab.TD(1, 1.3f, Button("Add shown", "Add the maps shown in the list to your selection", () => EditMaplist(add: true, shownOnly: true)));
        tab.TD(1, 1.3f, Button("Remove shown", "Remove the maps shown in the list from your selection", () => EditMaplist(add: false, shownOnly: true)));
        tab.GotoRC(tab.Rows - 4f, 3.2f);
        tab.TDempty(0.2f);
        tab.TD(1, 1.3f, Button("Add all", "Add every available map to your selection", () => EditMaplist(add: true, shownOnly: false)));
        tab.TD(1, 1.3f, Button("Remove all", "Remove all the maps from your selection", () => EditMaplist(add: false, shownOnly: false)));

        // "the following line instead of me.TR(me) for better visual spacing"
        tab.GotoRC(tab.Rows - 2.5f, 3.2f);
        tab.TDempty(0.5f);
        var mutators = tab.TD(1, 2, Button("Mutators...", "Mutators and weapon arenas", () => Menu?.Push(new DialogMutators())));
        Dependent.Bind(mutators, "g_campaign", 0, 0);

        // --- bottom row ---------------------------------------------------------------------------------
        tab.GotoRC(tab.Rows - 1f, 0f);
        tab.TDempty(tab.Columns / 12f);
        _leaveButton = tab.TD(1, tab.Columns * 5f / 12f, Button("Leave current match", "", () => MenuCommand.Run("disconnect")));
        // makeXonoticButton(_("Start multiplayer!"), '0 1 0'): the gray art, tinted green.
        tab.TD(1, tab.Columns * 5f / 12f, QcLook.Button(MakeButton("Start multiplayer!", OnStart), tint: new Color(0f, 1f, 0f)));

        if (!string.IsNullOrWhiteSpace(_initialMap))
            _selectedMap = _initialMap!;
        RefreshGametypes();
        ConfigureModeRows(SelectedGametype() ?? "dm");
        RefilterMaps();
    }

    /// <summary>makeXonoticHeaderLabel: bold, centred, SKINCOLOR_HEADER @ SKINALPHA_HEADER.</summary>
    private static QcLabel Header(string text) => new(text, 0.5f) { Bold = true, Color = MenuSkin.Header };

    private static Button Button(string text, string tooltip, Action pressed)
    {
        Button b = QcLook.Button(MakeButton(text, pressed));
        if (tooltip.Length > 0) b.TooltipText = Localization.Tr(tooltip);
        return b;
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree())
            _leaveButton.Disabled = MenuCommand.InMatch is null || !MenuCommand.InMatch();
    }

    // ---------------------------------------------------------------------------------------------------
    //  Gametypes
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Repopulate the gametype rows (registry ∩ visibility), keeping/restoring the selection.</summary>
    private void RefreshGametypes()
    {
        bool showAll = MenuState.Cvars.GetFloat("menu_create_show_all") != 0f;
        string keep = SelectedGametype() ?? _initialGametype ?? "dm";

        _shownGametypes.Clear();
        // QC menu order: the stock priority list first, any remaining (show-all) modes after, alphabetical.
        foreach (GameType gt in GameTypes.All)
        {
            if (showAll || DefaultVisibleGametypes.Contains(gt.NetName) || gt.NetName == keep)
                _shownGametypes.Add(gt);
        }
        _shownGametypes.Sort((a, b) =>
        {
            int ia = System.Array.FindIndex(DefaultGametypeOrder, n => n.Equals(a.NetName, StringComparison.OrdinalIgnoreCase));
            int ib = System.Array.FindIndex(DefaultGametypeOrder, n => n.Equals(b.NetName, StringComparison.OrdinalIgnoreCase));
            if (ia < 0 && ib < 0) return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (ia < 0) return 1;
            if (ib < 0) return -1;
            return ia.CompareTo(ib);
        });

        _refreshing = true;
        _gametypeList.SetEntries(_shownGametypes.ConvertAll(gt => new QcGametypeList.Entry(gt.NetName,
            string.IsNullOrEmpty(gt.DisplayName) ? gt.NetName : gt.DisplayName,
            "gametype_" + GametypeIconName(gt.NetName), gt.TeamGame)));
        int index = _shownGametypes.FindIndex(gt => gt.NetName == keep);
        _gametypeList.SelectedItem = Math.Max(0, index);
        _refreshing = false;
    }

    private string? SelectedGametype()
    {
        if (_gametypeList is null) return null;
        int i = _gametypeList.SelectedItem;
        return i >= 0 && i < _gametypeList.Entries.Count ? _gametypeList.Entries[i].NetName : null;
    }

    private void OnGametypeSelected()
    {
        if (_refreshing) return;
        ConfigureModeRows(SelectedGametype() ?? "dm");
        RefilterMaps(); // QC gameTypeChangeNotify → mapListBox.refilter
    }

    /// <summary>QC GameType_ConfigureSliders: rebind the frag-limit row (per-mode label+cvar) + the Teams row.
    /// A mode without the setting keeps the row, disabled and empty, as the QC's string_null slider does.</summary>
    private void ConfigureModeRows(string gametype)
    {
        ModeMenu cfg = ModeMenus.TryGetValue(gametype, out ModeMenu? m)
            ? m!
            : new ModeMenu("Frag limit:", "fraglimit_override", 5, 100, 5, null);

        _fragLabel.Text = Localization.Tr(cfg.FragLabel.Length > 0 ? cfg.FragLabel : "Frag limit:");
        Fill(_fragCell, _fragLabel, cfg.FragCvar.Length > 0
            ? Widgets.MixedSlider(cfg.FragCvar).Add("Default", -1).AddRange(cfg.Min, cfg.Max, cfg.Step).Add("Unlimited", 0).Finish()
            : null);
        Fill(_teamsCell, _teamsLabel, cfg.TeamsCvar is { } teamsCvar
            ? Widgets.MixedSlider(teamsCvar).Add("Default", 0).Add("2 teams", 2).Add("3 teams", 3).Add("4 teams", 4).Finish()
            : null);
    }

    private static void Fill(Control cell, QcLabel label, CvarMixedSlider? slider)
    {
        foreach (Node child in cell.GetChildren())
        {
            cell.RemoveChild(child);
            child.QueueFree();
        }
        bool enabled = slider is not null;
        slider ??= Widgets.MixedSlider("").Finish(); // no values: the track alone, no knob, no text
        QcLook.Slider(slider);
        slider.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        cell.AddChild(slider);
        CvarUi.SetEnabledRecursive(slider, enabled);
        label.Disabled = !enabled;
    }

    // ---------------------------------------------------------------------------------------------------
    //  The maplist
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The current selection, read through the store: it may live in a file rather than the
    /// cvar (a selection too long for one), and an unset list means every installed map.</summary>
    private static HashSet<string> MaplistSet()
        => new(VortexArena.Server.MapListStore.Resolve(MapList.Available), StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves the selection, to the cvar when it fits and to the list file when it does not.
    /// Always a deliberate edit &#8212; "add all" pins the current set on purpose.</summary>
    private static void SaveMaplist(IEnumerable<string> maps)
    {
        var list = new List<string>(maps);
        if (VortexArena.Server.MapListStore.Save(list) == VortexArena.Server.MapListStore.Source.Cvar)
            MenuState.Cvars.MarkArchived("g_maplist");
    }

    /// <summary>Maps whose .mapinfo declares support for <paramref name="gametype"/> (no mapinfo = shown).</summary>
    private static bool MapSupports(string map, string gametype)
    {
        MapInfoCache.Entry info = MapInfoCache.Get(map);
        return info.Gametypes.Count == 0 || info.Gametypes.Contains(gametype);
    }

    /// <summary>Rebuild the visible map rows from the gametype + text filter (QC refilter).</summary>
    private void RefilterMaps()
    {
        if (_mapList is null)
            return;
        string gametype = SelectedGametype() ?? "dm";
        string needle = _mapFilter.Text.Trim();
        HashSet<string> included = MaplistSet();

        var rows = new List<QcMapList.Entry>();
        foreach (string map in MapList.Available())
        {
            MapInfoCache.Entry info = MapInfoCache.Get(map);
            if (!MapSupports(map, gametype))
                continue;
            if (needle.Length > 0
                && !map.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !info.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new QcMapList.Entry(map, info.Title, info.Author));
        }
        _mapList.SetEntries(rows, included.Contains);

        // Keep the selection meaningful: default to the first shown map.
        int index = rows.FindIndex(r => r.Map.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && rows.Count > 0)
        {
            index = 0;
            _selectedMap = rows[0].Map;
        }
        if (index >= 0) _mapList.SelectedItem = index;
    }

    /// <summary>QC g_maplistCacheToggle: clicking a row's preview adds/removes that map from g_maplist.</summary>
    private void ToggleInclude(string map)
    {
        HashSet<string> set = MaplistSet();
        if (!set.Remove(map))
            set.Add(map);
        SaveMaplist(set);
        _mapList.SetEntries(new List<QcMapList.Entry>(_mapList.Entries), set.Contains);
    }

    /// <summary>QC MapList_Add/Remove_Shown/All: bulk-edit g_maplist with the filtered (or full) map set.</summary>
    private void EditMaplist(bool add, bool shownOnly)
    {
        HashSet<string> set = MaplistSet();
        IEnumerable<string> affected = shownOnly
            ? new List<QcMapList.Entry>(_mapList.Entries).ConvertAll(r => r.Map)
            : MapList.Available();
        foreach (string map in affected)
        {
            if (add) set.Add(map);
            else set.Remove(map);
        }
        SaveMaplist(set);
        _mapList.SetEntries(new List<QcMapList.Entry>(_mapList.Entries), set.Contains);
    }

    // ---------------------------------------------------------------------------------------------------
    //  Start
    // ---------------------------------------------------------------------------------------------------

    private void OnStart()
    {
        string? gametype = SelectedGametype();
        if (string.IsNullOrEmpty(gametype))
        {
            GD.Print("[Menu] Start game: no gametype selected.");
            return;
        }

        // The match settings live in the same cvars the QC writes; gather the MatchConfig from them.
        float timeLimit = MenuState.Cvars.GetFloat("timelimit_override");
        float fragLimit = MenuState.Cvars.GetFloat("fraglimit_override");
        var config = new MatchConfig
        {
            Gametype = gametype!,
            Map = _selectedMap,
            BotCount = (int)MenuState.Cvars.GetFloat("bot_number"),
            BotSkill = (int)MenuState.Cvars.GetFloat("skill"),
            TimeLimit = timeLimit > 0 ? (int)timeLimit : 0,
            FragLimit = fragLimit > 0 ? (int)fragLimit : 0,
            Slots = MenuSlotCount(),
        };
        RaiseStartGame(config);
    }

    /// <summary>
    /// QC <c>menu_loadmap_prepare</c> (data/core.pk3dir/commands.cfg:114), whose rpn is
    /// <c>max(menu_maxplayers, minplayers, bot_number + 1, minplayers_per_team * 4)</c> — the value it then hands
    /// to the engine's <c>maxplayers</c> command.
    ///
    /// <para>The <c>bot_number + 1</c> term is the point of the max(): asking for N bots raises the slot count to
    /// fit them AND you, so the two sliders can never be set to a contradiction where the fill silently loses
    /// bots. <c>minplayers_per_team * 4</c> is upstream's fixed four-team worst case, not a live team count.</para>
    /// </summary>
    private static int MenuSlotCount()
    {
        VortexArena.Common.Services.ICvarService cv = MenuState.Cvars;
        int slots = (int)cv.GetFloat("menu_maxplayers");
        slots = System.Math.Max(slots, (int)cv.GetFloat("minplayers"));
        slots = System.Math.Max(slots, (int)cv.GetFloat("bot_number") + 1);
        slots = System.Math.Max(slots, (int)cv.GetFloat("minplayers_per_team") * 4);
        return slots;
    }
}

/// <summary>
/// Per-map .mapinfo metadata (title / author / supported gametypes), parsed once from the VFS — the C#
/// stand-in for the engine MapInfo cache the QC maplist reads. Maps without a .mapinfo get their file name
/// as the title and an empty gametype set (treated as "supports everything", like MapInfo's autogeneration).
/// </summary>
public static class MapInfoCache
{
    public sealed class Entry
    {
        public string Title = "";
        public string Author = "";
        public readonly HashSet<string> Gametypes = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop every parsed entry so the next <see cref="Get"/> re-reads from the CURRENT search path —
    /// <c>fs_rescan</c> (<see cref="MenuState.RescanContent"/>). Needed because a miss is cached too: a map
    /// listed before its pack was mounted is remembered as having no <c>.mapinfo</c> at all.</summary>
    public static void Invalidate() => Cache.Clear();

    public static Entry Get(string map)
    {
        if (Cache.TryGetValue(map, out Entry? hit))
            return hit;

        var e = new Entry { Title = map };
        if (MenuState.Vfs is { } vfs && vfs.Exists($"maps/{map}.mapinfo"))
        {
            foreach (string raw in vfs.ReadText($"maps/{map}.mapinfo").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase))
                    e.Title = line[6..].Trim();
                else if (line.StartsWith("author ", StringComparison.OrdinalIgnoreCase))
                    e.Author = line[7..].Trim();
                else if (line.StartsWith("gametype ", StringComparison.OrdinalIgnoreCase)
                         || line.StartsWith("type ", StringComparison.OrdinalIgnoreCase))
                {
                    // "gametype dm [options]" (new) / "type dm ..." (legacy) — the mode is the 2nd token.
                    string[] tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length >= 2)
                        e.Gametypes.Add(tok[1]);
                }
            }
        }
        // QC Duel.m_isForcedSupported (duel.qh:15-28): any DM map also supports duel, unless
        // g_duel_not_dm_maps is set. Mirrors MapInfoBackend.ApplyForcedGametypes.
        bool forceDuelOnDm = MenuState.Cvars.GetFloat("g_duel_not_dm_maps") == 0f;
        if (forceDuelOnDm && e.Gametypes.Contains("dm") && !e.Gametypes.Contains("duel"))
            e.Gametypes.Add("duel");
        // QC TeamDeathmatch.m_isForcedSupported (tdm.qh): any DM map also supports TDM when
        // g_tdm_on_dm_maps is set (default 0 = off, opposite polarity to g_duel_not_dm_maps).
        // Mirrors MapInfoBackend.ApplyForcedGametypes (the dead MapInfoBackend path; this is the live one).
        bool forceTdmOnDm = MenuState.Cvars.GetFloat("g_tdm_on_dm_maps") != 0f;
        if (forceTdmOnDm && e.Gametypes.Contains("dm") && !e.Gametypes.Contains("tdm"))
            e.Gametypes.Add("tdm");
        Cache[map] = e;
        return e;
    }
}
