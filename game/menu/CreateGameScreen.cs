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

    // Bot skill rungs, copied from the QC "skill" mixed-slider labels.
    private static readonly string[] BotSkillNames =
    {
        "Botlike", "Beginner", "You will win", "You can win", "You might win",
        "Advanced", "Expert", "Pro", "Assassin", "Unhuman", "Godlike",
    };

    private Tree _gametypeTree = null!;
    private readonly List<GameType> _shownGametypes = new();
    private Label _fragLabel = null!;
    private HBoxContainer _fragRow = null!;
    private Control? _fragControl;
    private Label _teamsLabel = null!;
    private HBoxContainer _teamsRow = null!;
    private Control? _teamsControl;
    private VBoxContainer _mapRows = null!;
    private LineEdit _mapFilter = null!;
    private Button _leaveButton = null!;
    private readonly ButtonGroup _mapGroup = new();
    private readonly List<MapRowButton> _mapRowButtons = new();
    private string _selectedMap = "";

    protected override void BuildUi()
    {
        Name = "CreateGameScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, Embedded ? 4 : 24);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        if (!Embedded && !HostProvidesTitle)
            root.AddChild(MakeTitle("Create Game"));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 26);
        root.AddChild(body);

        body.AddChild(BuildGametypeColumn());
        body.AddChild(BuildMapColumn());

        // Bottom row (QC rows-1): Leave current match (5/12) | Start multiplayer! (5/12).
        _leaveButton = MakeButton("Leave current match", () => MenuCommand.Run("disconnect"));
        root.AddChild(MakeButtonBar(_leaveButton, MakeButton("Start multiplayer!", OnStart)));

        if (!string.IsNullOrWhiteSpace(_initialMap))
            _selectedMap = _initialMap!;
        RefreshGametypes();
        ConfigureModeRows(SelectedGametype() ?? "dm");
        RefilterMaps();
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree())
            _leaveButton.Disabled = MenuCommand.InMatch is null || !MenuCommand.InMatch();
    }

    // ---------------------------------------------------------------------------------------------------
    //  Left half — gametype list + match settings
    // ---------------------------------------------------------------------------------------------------

    private Control BuildGametypeColumn()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 8);

        var header = MakeHeader("Gametype");
        header.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(header);

        // QC makeXonoticGametypeList: rows of [gametype icon | name | free for all / teamplay].
        _gametypeTree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            FocusMode = FocusModeEnum.None,
        };
        _gametypeTree.SetColumnExpand(0, true);
        _gametypeTree.SetColumnExpandRatio(0, 65);
        _gametypeTree.SetColumnExpand(1, true);
        _gametypeTree.SetColumnExpandRatio(1, 35);
        _gametypeTree.ItemSelected += OnGametypeSelected;
        col.AddChild(_gametypeTree);

        var showAll = Widgets.CheckBox("menu_create_show_all", "Show all", "Show all available gametypes");
        showAll.Toggled += _ => RefreshGametypes();
        col.AddChild(showAll);

        // --- the match-settings rows (QC rows 14..20 of the left half) -------------------------------
        var time = Widgets.MixedSlider("timelimit_override", "Time limit in minutes that when hit, will end the match")
            .Add("Default", -1).AddRange(1, 10, 1).AddRange(15, 30, 5).AddRange(40, 60, 10).Add("Infinite", 0)
            .Finish();
        col.AddChild(SettingRow(out _, "Time limit:", time));

        _fragRow = SettingRow(out _fragLabel, "Frag limit:", null);
        col.AddChild(_fragRow);

        _teamsRow = SettingRow(out _teamsLabel, "Teams:", null);
        col.AddChild(_teamsRow);

        var slots = Widgets.Slider("menu_maxplayers", 1, 32, 1,
            "The maximum amount of players or bots that can be connected to your server at once");
        col.AddChild(SettingRow(out _, "Player slots:", slots));

        var bots = Widgets.Slider("bot_number", 0, 9, 1, "Amount of bots on your server");
        col.AddChild(SettingRow(out Label botsLabel, "Number of bots:", bots));
        Dependent.Bind(bots, "bot_vs_human", 0, 0);
        Dependent.Bind(botsLabel, "bot_vs_human", 0, 0);

        var skill = Widgets.MixedSlider("skill", "Specify how experienced the bots will be");
        for (int i = 0; i < BotSkillNames.Length; i++)
            skill.Add(BotSkillNames[i], i);
        skill.Finish();
        var skillRow = SettingRow(out _, "Bot skill:", skill);
        col.AddChild(skillRow);
        // QC allowBotSkill is bots>0 OR bot_vs_human; the dominant gate is having bots at all.
        Dependent.Bind(skillRow, "bot_number", 1, 99);

        col.AddChild(Widgets.CheckBox("bot_vs_human", "Bots against humans",
            "Force humans to play on the same team against an equal-sized team of bots"));

        return col;
    }

    /// <summary>One "Label: control" settings row with the QC's narrow label column.</summary>
    private static HBoxContainer SettingRow(out Label label, string text, Control? control)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        label = MakeLabel(text);
        label.CustomMinimumSize = new Vector2(130, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);
        if (control is not null)
        {
            if (control.SizeFlagsHorizontal == SizeFlags.Fill)
                control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(control);
        }
        return row;
    }

    /// <summary>Repopulate the gametype rows (registry ∩ visibility), keeping/restoring the selection.</summary>
    private void RefreshGametypes()
    {
        bool showAll = MenuState.Cvars.GetFloat("menu_create_show_all") != 0f;
        string keep = SelectedGametype() ?? _initialGametype ?? "dm";

        _shownGametypes.Clear();
        _gametypeTree.Clear();
        TreeItem rootItem = _gametypeTree.CreateItem();
        TreeItem? reselect = null;

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

        foreach (GameType gt in _shownGametypes)
        {
            TreeItem item = _gametypeTree.CreateItem(rootItem);
            item.SetText(0, string.IsNullOrEmpty(gt.DisplayName) ? gt.NetName : gt.DisplayName);
            if (MenuSkin.SkinImage("gametype_" + GametypeIconName(gt.NetName)) is { } icon)
            {
                item.SetIcon(0, icon);
                item.SetIconMaxWidth(0, 26);
            }
            item.SetText(1, Localization.Tr(gt.TeamGame ? "teamplay" : "free for all"));
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetCustomColor(1, MenuSkin.Header);
            item.SetMetadata(0, gt.NetName);
            if (gt.NetName == keep)
                reselect = item;
        }
        (reselect ?? rootItem.GetFirstChild())?.Select(0);
    }

    private string? SelectedGametype()
    {
        TreeItem? sel = _gametypeTree?.GetSelected();
        if (sel is null) return null;
        Variant meta = sel.GetMetadata(0);
        return meta.VariantType == Variant.Type.String ? meta.AsString() : null;
    }

    private void OnGametypeSelected()
    {
        ConfigureModeRows(SelectedGametype() ?? "dm");
        RefilterMaps(); // QC gameTypeChangeNotify → mapListBox.refilter
    }

    /// <summary>QC GameType_ConfigureSliders: rebind the frag-limit row (per-mode label+cvar) + the Teams row.</summary>
    private void ConfigureModeRows(string gametype)
    {
        ModeMenu cfg = ModeMenus.TryGetValue(gametype, out ModeMenu? m)
            ? m!
            : new ModeMenu("Frag limit:", "fraglimit_override", 5, 100, 5, null);

        if (_fragControl is not null)
        {
            _fragRow.RemoveChild(_fragControl);
            _fragControl.QueueFree();
        }
        _fragLabel.Text = Localization.Tr(cfg.FragLabel.Length > 0 ? cfg.FragLabel : "Frag limit:");
        if (cfg.FragCvar.Length > 0)
        {
            var slider = Widgets.MixedSlider(cfg.FragCvar)
                .Add("Default", -1).AddRange(cfg.Min, cfg.Max, cfg.Step).Add("Unlimited", 0)
                .Finish();
            slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _fragRow.AddChild(slider);
            _fragControl = slider;
            _fragLabel.Modulate = Colors.White;
        }
        else
        {
            _fragControl = null;
            _fragLabel.Modulate = new Color(1, 1, 1, 0.4f); // mode has no limit (QC disables the pair)
        }

        if (_teamsControl is not null)
        {
            _teamsRow.RemoveChild(_teamsControl);
            _teamsControl.QueueFree();
        }
        if (cfg.TeamsCvar is { } teamsCvar)
        {
            var teams = Widgets.MixedSlider(teamsCvar)
                .Add("Default", 0).Add("2 teams", 2).Add("3 teams", 3).Add("4 teams", 4)
                .Finish();
            teams.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _teamsRow.AddChild(teams);
            _teamsControl = teams;
            _teamsLabel.Modulate = Colors.White;
        }
        else
        {
            _teamsControl = null;
            _teamsLabel.Modulate = new Color(1, 1, 1, 0.4f);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    //  Right half — the maplist
    // ---------------------------------------------------------------------------------------------------

    private Control BuildMapColumn()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 8);

        var header = MakeHeader("Maplist");
        header.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(header);

        var listPanel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        listPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.25f) });
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _mapRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapRows.AddThemeConstantOverride("separation", 1);
        scroll.AddChild(_mapRows);
        listPanel.AddChild(scroll);
        col.AddChild(listPanel);

        // Filter: narrows the shown rows (QC stringFilterBox).
        var filterRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        filterRow.AddThemeConstantOverride("separation", 8);
        var fl = MakeLabel("Filter:");
        fl.VerticalAlignment = VerticalAlignment.Center;
        filterRow.AddChild(fl);
        _mapFilter = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapFilter.TextChanged += _ => RefilterMaps();
        filterRow.AddChild(_mapFilter);
        col.AddChild(filterRow);

        // The g_maplist selection buttons (QC Add/Remove shown, Add/Remove all).
        col.AddChild(MakeButtonBar(
            MakeButton("Add shown", () => EditMaplist(add: true, shownOnly: true)),
            MakeButton("Remove shown", () => EditMaplist(add: false, shownOnly: true))));
        col.AddChild(MakeButtonBar(
            MakeButton("Add all", () => EditMaplist(add: true, shownOnly: false)),
            MakeButton("Remove all", () => EditMaplist(add: false, shownOnly: false))));

        // Mutators... centred at 2/3 width (QC TDempty 0.5 + TD 2 of 3 columns).
        var mutRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mutRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var mut = MakeButton("Mutators...", () => Menu?.Push(new DialogMutators()));
        mut.TooltipText = Localization.Tr("Mutators and weapon arenas");
        mut.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        mut.SizeFlagsStretchRatio = 4f;
        mutRow.AddChild(mut);
        mutRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        col.AddChild(mutRow);

        return col;
    }

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
        if (_mapRows is null)
            return;
        foreach (MapRowButton row in _mapRowButtons)
            row.QueueFree();
        _mapRowButtons.Clear();

        string gametype = SelectedGametype() ?? "dm";
        string needle = _mapFilter.Text.Trim();
        HashSet<string> included = MaplistSet();

        foreach (string map in MapList.Available())
        {
            MapInfoCache.Entry info = MapInfoCache.Get(map);
            if (!MapSupports(map, gametype))
                continue;
            if (needle.Length > 0
                && !map.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !info.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            var row = new MapRowButton(map, info.Title, info.Author, included.Contains(map), _mapGroup);
            row.Pressed += () => _selectedMap = map;
            row.ToggleIncludeRequested += () => ToggleInclude(map);
            // QC XonoticMapList_doubleClickListBoxItem: double-clicking the row opens the map info dialog for it
            // (using the create-game screen's currently selected gametype as the preferred "Play" gametype).
            row.MapInfoRequested += () =>
            {
                _selectedMap = map;
                Menu?.Push(new DialogCreateGameMapInfo(map, SelectedGametype()));
            };
            _mapRows.AddChild(row);
            _mapRowButtons.Add(row);
            if (map.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase))
                row.SetPressedNoSignal(true);
        }

        // Keep the selection meaningful: default to the first shown map.
        if (_mapRowButtons.Count > 0
            && !_mapRowButtons.Exists(r => r.MapName.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedMap = _mapRowButtons[0].MapName;
            _mapRowButtons[0].SetPressedNoSignal(true);
        }
    }

    /// <summary>QC g_maplistCacheToggle: clicking a row's preview adds/removes that map from g_maplist.</summary>
    private void ToggleInclude(string map)
    {
        HashSet<string> set = MaplistSet();
        if (!set.Remove(map))
            set.Add(map);
        SaveMaplist(set);
        foreach (MapRowButton row in _mapRowButtons)
            if (row.MapName.Equals(map, StringComparison.OrdinalIgnoreCase))
                row.SetIncluded(set.Contains(map));
    }

    /// <summary>QC MapList_Add/Remove_Shown/All: bulk-edit g_maplist with the filtered (or full) map set.</summary>
    private void EditMaplist(bool add, bool shownOnly)
    {
        HashSet<string> set = MaplistSet();
        IEnumerable<string> affected = shownOnly
            ? _mapRowButtons.ConvertAll(r => r.MapName)
            : MapList.Available();
        foreach (string map in affected)
        {
            if (add) set.Add(map);
            else set.Remove(map);
        }
        SaveMaplist(set);
        foreach (MapRowButton row in _mapRowButtons)
            row.SetIncluded(set.Contains(row.MapName));
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
        };
        RaiseStartGame(config);
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

/// <summary>
/// One maplist row — what <c>XonoticMapList_drawListBoxItem</c> draws: the 4:3 mapshot (checkmark overlay
/// when the map is in the g_maplist selection), the map title, and the author right-aligned, all dimmed
/// when not included. Clicking the PREVIEW toggles inclusion (QC clickListBoxItem's preview-column zone);
/// clicking the text selects the row (orange fill).
/// </summary>
internal partial class MapRowButton : Button
{
    private const int RowHeight = 48;
    private const int ThumbWidth = 56;

    public string MapName { get; }

    /// <summary>Raised when the user clicks the preview zone (toggle this map in g_maplist).</summary>
    public event Action? ToggleIncludeRequested;

    /// <summary>Raised when the user double-clicks the name column (QC XonoticMapList_doubleClickListBoxItem →
    /// pop up the map info dialog).</summary>
    public event Action? MapInfoRequested;

    private readonly Control _content;
    private readonly TextureRect _checkmark;
    private bool _included;

    public MapRowButton(string map, string title, string author, bool included, ButtonGroup group)
    {
        MapName = map;
        _included = included;
        ToggleMode = true;
        ButtonGroup = group;
        FocusMode = FocusModeEnum.None;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(0, RowHeight);

        AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(0.09f, 0.42f, 0.69f, 0.30f) });
        var selectedFill = new StyleBoxFlat { BgColor = new Color(0.9f, 0.53f, 0.28f, 0.95f) };
        AddThemeStyleboxOverride("pressed", selectedFill);
        AddThemeStyleboxOverride("hover_pressed", selectedFill);

        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 3);
        AddChild(pad);

        var box = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 10);
        pad.AddChild(box);

        var thumbFrame = new Control
        {
            CustomMinimumSize = new Vector2(ThumbWidth, RowHeight - 6),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var thumb = new TextureRect
        {
            Texture = MenuSkin.Image("maps/" + map) ?? MenuSkin.SkinImage("nopreview_map"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        thumb.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        thumbFrame.AddChild(thumb);
        _checkmark = new TextureRect
        {
            Texture = MenuSkin.SkinImage("checkmark"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 1, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = -20, OffsetTop = -20, OffsetRight = -1, OffsetBottom = -1,
        };
        thumbFrame.AddChild(_checkmark);
        box.AddChild(thumbFrame);

        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        text.AddThemeConstantOverride("separation", 0);
        var titleLabel = new Label
        {
            Text = MenuColorCodes.Strip(title),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipText = true,
        };
        titleLabel.AddThemeColorOverride("font_color", new Color(0.96f, 0.99f, 1f));
        text.AddChild(titleLabel);
        if (author.Length > 0)
        {
            var authorLabel = new Label
            {
                Text = MenuColorCodes.Strip(author),
                HorizontalAlignment = HorizontalAlignment.Right,
                MouseFilter = MouseFilterEnum.Ignore,
                ClipText = true,
            };
            authorLabel.AddThemeColorOverride("font_color", MenuSkin.Header);
            authorLabel.AddThemeFontSizeOverride("font_size", 13);
            text.AddChild(authorLabel);
        }
        box.AddChild(text);
        _content = box;
        ApplyIncludedLook();

        GuiInput += ev =>
        {
            if (ev is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
                return;

            // QC clickListBoxItem: a click within the preview column toggles g_maplist inclusion.
            if (mb.Position.X <= ThumbWidth + 6)
            {
                ToggleIncludeRequested?.Invoke();
                AcceptEvent();
                return;
            }

            // QC XonoticMapList_doubleClickListBoxItem: a double-click on the name column (outside the preview)
            // pops up the map info dialog.
            if (mb.DoubleClick)
            {
                MapInfoRequested?.Invoke();
                AcceptEvent();
            }
        };
    }

    public void SetIncluded(bool included)
    {
        _included = included;
        ApplyIncludedLook();
    }

    private void ApplyIncludedLook()
    {
        // QC SKINALPHA_MAPLIST_INCLUDEDFG vs NOTINCLUDEDFG: not-included rows render dim.
        _content.Modulate = new Color(1, 1, 1, _included ? 1f : 0.45f);
        _checkmark.Visible = _included;
    }
}
