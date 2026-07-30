using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Server; // CampaignCatalog / CampaignLevel

namespace VortexArena.Game.Menu;

/// <summary>
/// The Singleplayer dialog — C# successor to <c>XonoticSingleplayerDialog_fill</c> + the campaign listbox
/// (qcsrc/menu/xonotic/dialog_singleplayer.qc + campaign.qc). Faithful layout, top to bottom:
/// the big centred "Instant action with bots!" button, the campaign title row (&lt;&lt; / title / &gt;&gt;
/// cycling <c>maps/campaign*.txt</c>), the campaign LIST whose rows each contain the map preview, the
/// "Level N:" title line, the wrapped briefing text, the gametype icon and the completion checkmark —
/// exactly what <c>XonoticCampaignList_drawListBoxItem</c> draws INSIDE every 10-row item — then the
/// Campaign Difficulty radios (<c>g_campaign_skill</c>) and the Leave-match / "Play campaign!" row.
///
/// Unlock model is faithful to <c>XonoticCampaignList_loadCvars</c>: the saved frontier
/// <c>g_campaign&lt;id&gt;_index</c> is the highest unlocked level; the list reveals exactly
/// <c>frontier+2</c> rows — completed (dimmed + checkmark), the current one, and ONE locked "???" peek.
/// Starting a level fires the shared <see cref="CreateGameScreen.StartGameRequested"/> callback with the
/// campaign id + level index; Instant Action rolls the QC probability table for a random bot match.
/// </summary>
public partial class SingleplayerScreen : MenuScreen
{
    private Label _titleLabel = null!;
    private Button _prevButton = null!;
    private Button _nextButton = null!;
    private Button _leaveButton = null!;
    private ScrollContainer _listScroll = null!;
    private VBoxContainer _rowsBox = null!;
    private readonly ButtonGroup _rowGroup = new();

    private readonly List<CampaignRowButton> _rows = new();
    private readonly List<CampaignLevel> _levels = new();
    private string _campaignId = CampaignCatalog.DefaultName;
    private string _campaignTitle = "Campaign";
    private int _frontier;       // QC campaignIndex: highest unlocked level (the current playable one)
    private int _selected = -1;  // QC selectedItem

    protected override void BuildUi()
    {
        Name = "SingleplayerScreen";

        LoadCampaign();

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 18);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Singleplayer"));

        // --- the big centred Instant-action button (QC makeXonoticBigButton spanning 3 of 5 columns) ----
        var bigRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bigRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var big = MakeButton("Instant action with bots!", OnInstantAction);
        big.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        big.SizeFlagsStretchRatio = 3f;
        big.CustomMinimumSize = new Vector2(0, 52);
        bigRow.AddChild(big);
        bigRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        root.AddChild(bigRow);

        // --- campaign title row: << | title | >> (QC btnPrev/lblTitle/btnNext, MultiCampaign_Prev/Next) --
        var titleRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titleRow.AddThemeConstantOverride("separation", 10);
        _prevButton = MakeButton("<<", () => CycleCampaign(-1));
        _prevButton.SizeFlagsHorizontal = SizeFlags.Fill;
        _prevButton.CustomMinimumSize = new Vector2(64, 30);
        _titleLabel = MakeHeader(_campaignTitle);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _nextButton = MakeButton(">>", () => CycleCampaign(+1));
        _nextButton.SizeFlagsHorizontal = SizeFlags.Fill;
        _nextButton.CustomMinimumSize = new Vector2(64, 30);
        titleRow.AddChild(_prevButton);
        titleRow.AddChild(_titleLabel);
        titleRow.AddChild(_nextButton);
        root.AddChild(titleRow);

        // --- the campaign listbox: flat translucent-black backing, big self-contained rows --------------
        var listPanel = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        listPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.25f) });
        _listScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _rowsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rowsBox.AddThemeConstantOverride("separation", 2);
        _listScroll.AddChild(_rowsBox);
        listPanel.AddChild(_listScroll);
        root.AddChild(listPanel);

        PopulateList();

        // --- Campaign Difficulty: Easy / Medium / Hard (g_campaign_skill -2 / 0 / 2) ---------------------
        var diff = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        diff.AddThemeConstantOverride("separation", 14);
        var diffLabel = MakeLabel("Campaign Difficulty:");
        diffLabel.HorizontalAlignment = HorizontalAlignment.Right;
        diffLabel.VerticalAlignment = VerticalAlignment.Center;
        diffLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        diff.AddChild(diffLabel);
        var group = new ButtonGroup();
        foreach (var (value, label) in new[] { ("-2", "Easy"), ("0", "Medium"), ("2", "Hard") })
        {
            var radio = Widgets.RadioButton("g_campaign_skill", value, label, group);
            radio.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            diff.AddChild(radio);
        }
        root.AddChild(diff);

        // --- bottom row: Leave current match | Play campaign! -------------------------------------------
        _leaveButton = MakeButton("Leave current match", () => MenuCommand.Run("disconnect"));
        root.AddChild(MakeButtonBar(_leaveButton, MakeButton("Play campaign!", OnStartLevel)));
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree())
            _leaveButton.Disabled = MenuCommand.InMatch is null || !MenuCommand.InMatch();
    }

    // -------------------------------------------------------------------------------------------------
    //  Campaign data
    // -------------------------------------------------------------------------------------------------

    /// <summary>The saved-progress cvar (QC <c>strcat("g_campaign", name, "_index")</c>).</summary>
    private string ProgressCvar => $"g_campaign{_campaignId}_index";

    /// <summary>Read the shipped campaign definition (default <c>xonoticbeta</c>) + the saved frontier.</summary>
    private void LoadCampaign()
    {
        _campaignId = MenuState.Cvars.GetString("g_campaign_name");
        if (string.IsNullOrWhiteSpace(_campaignId))
            _campaignId = CampaignCatalog.DefaultName;

        string path = CampaignCatalog.FileName(_campaignId);
        string? text = MenuState.Vfs is { } vfs && vfs.Exists(path) ? vfs.ReadText(path) : null;

        _levels.Clear();
        _levels.AddRange(CampaignCatalog.Parse(text, out _campaignTitle));
        if (string.IsNullOrWhiteSpace(_campaignTitle))
            _campaignTitle = "Campaign";

        // QC: campaignIndex = bound(0, cvar(g_campaign<id>_index), campaign_entries).
        _frontier = Math.Clamp((int)MenuState.Cvars.GetFloat(ProgressCvar), 0, _levels.Count);

        if (_levels.Count == 0)
            GD.PrintErr($"[Singleplayer] campaign '{path}' not found or empty in the VFS — the level list is empty.");
    }

    /// <summary>All shipped campaign ids (QC search "maps/campaign*.txt"), for the &lt;&lt;/&gt;&gt; cycler.</summary>
    private List<string> CampaignIds()
    {
        var ids = new List<string>();
        if (MenuState.Vfs is { } vfs)
        {
            foreach (string p in vfs.Find("maps/campaign", ".txt"))
            {
                // "maps/campaign<id>.txt" → "<id>"
                string id = p["maps/campaign".Length..];
                ids.Add(id[..^".txt".Length]);
            }
        }
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    /// <summary>QC MultiCampaign_Prev/Next: step through the campaign files, reload + rebuild.</summary>
    private void CycleCampaign(int step)
    {
        List<string> ids = CampaignIds();
        if (ids.Count == 0)
            return;
        int at = ids.FindIndex(s => string.Equals(s, _campaignId, StringComparison.OrdinalIgnoreCase));
        int next = at < 0 ? (step >= 0 ? 0 : ids.Count - 1) : Math.Clamp(at + step, 0, ids.Count - 1);
        if (next == at)
            return;
        MenuState.Cvars.Set("g_campaign_name", ids[next]);
        MenuState.Cvars.MarkArchived("g_campaign_name");
        LoadCampaign();
        _titleLabel.Text = _campaignTitle;
        PopulateList();
    }

    // -------------------------------------------------------------------------------------------------
    //  The list rows (QC XonoticCampaignList_drawListBoxItem)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Rebuild the rows: frontier+2 revealed — completed (✓, dim), current, one locked "???" peek.</summary>
    private void PopulateList()
    {
        foreach (CampaignRowButton row in _rows)
            row.QueueFree();
        _rows.Clear();

        List<string> ids = CampaignIds();
        int at = ids.FindIndex(s => string.Equals(s, _campaignId, StringComparison.OrdinalIgnoreCase));
        _prevButton.Disabled = ids.Count <= 1 || at <= 0;
        _nextButton.Disabled = ids.Count <= 1 || at >= ids.Count - 1;

        int shown = Math.Min(_frontier + 2, _levels.Count); // QC nItems = min(campaignIndex + 2, entries)
        for (int i = 0; i < shown; i++)
        {
            var row = new CampaignRowButton(_levels[i], i, _frontier, _rowGroup);
            int index = i;
            row.Pressed += () => _selected = index;
            row.PlayRequested += () => { _selected = index; OnStartLevel(); };
            _rowsBox.AddChild(row);
            _rows.Add(row);
        }

        if (shown > 0)
        {
            _selected = Math.Min(_frontier, shown - 1);     // QC setSelected(min(campaignIndex, nItems-1))
            _rows[_selected].SetPressedNoSignal(true);
            CampaignRowButton target = _rows[_selected];
            // Scroll the current level into view once layout has sized the rows.
            Callable.From(() => _listScroll.EnsureControlVisible(target)).CallDeferred();
        }
        else
        {
            _selected = -1;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Actions
    // -------------------------------------------------------------------------------------------------

    /// <summary>Start the selected campaign level via the shared StartGame callback, in campaign mode.</summary>
    private void OnStartLevel()
    {
        // QC CampaignList_LoadMap: only an unlocked level launches (setSelected clamps to campaignIndex).
        if (_selected < 0 || _selected > _frontier || _selected >= _levels.Count)
            return;

        CampaignLevel lvl = _levels[_selected];
        var config = new MatchConfig
        {
            // The menu's pre-resolved copy (loads the BSP + fills bots client-side); the server re-resolves the
            // authoritative gametype/skill/limits/mutators from the campaign file via g_campaign + the index.
            Gametype = lvl.Gametype,
            Map = lvl.MapName,
            BotCount = lvl.Bots,
            BotSkill = lvl.BotSkill,
            TimeLimit = 0,   // campaign limits come from the file (CampaignPostInit), not the menu
            FragLimit = 0,
            CampaignId = _campaignId,
            CampaignIndex = lvl.Index,
        };

        // Reuse the Create screen's wiring point so the host has a single StartGame entry.
        CreateGameScreen.RaiseStartGame(config);
    }

    /// <summary>
    /// QC <c>InstantAction_LoadMap</c>: roll the gametype probability table (30% dm, 25% ctf, 15% tdm,
    /// 10% ca, 10% ft, 5% kh, 5% one of lms/dom/ons/as), pick a random map and a bot count from the
    /// per-mode range, and start immediately (timelimit 10).
    /// </summary>
    private void OnInstantAction()
    {
        var rng = new Random();
        float r = (float)rng.NextDouble();
        string gt; int pmin, pmax, pstep;
        if ((r -= 0.30f) < 0) { gt = "dm"; pmin = 2; pmax = 8; pstep = 1; }
        else if ((r -= 0.25f) < 0) { gt = "ctf"; pmin = 4; pmax = 12; pstep = 2; }
        else if ((r -= 0.15f) < 0) { gt = "tdm"; pmin = 4; pmax = 8; pstep = 2; }
        else if ((r -= 0.10f) < 0) { gt = "ca"; pmin = 4; pmax = 8; pstep = 2; }
        else if ((r -= 0.10f) < 0) { gt = "ft"; pmin = 4; pmax = 8; pstep = 2; }
        else if ((r -= 0.05f) < 0) { gt = "kh"; pmin = 6; pmax = 6; pstep = 6; }
        else
        {
            switch (rng.Next(4))
            {
                default: gt = "lms"; pmin = 2; pmax = 6; pstep = 1; break;
                case 1: gt = "dom"; pmin = 2; pmax = 8; pstep = 2; break;
                case 2: gt = "ons"; pmin = 6; pmax = 16; pstep = 2; break;
                case 3: gt = "as"; pmin = 4; pmax = 16; pstep = 2; break;
            }
        }
        if (GameTypes.ByName(gt) is null)
        {
            gt = "dm"; pmin = 2; pmax = 8; pstep = 1; // mode not registered in the port yet — QC dm weights
        }

        IReadOnlyList<string> maps = MapList.Available();
        string map = maps.Count > 0 ? maps[rng.Next(maps.Count)] : "dm_example";

        // QC: p = pmin + pstep*floor(random()*((pmax-pmin)/pstep+1)); bot_number = p - 1 (the human fills a slot).
        pmin = pstep * (int)Math.Ceiling(pmin / (double)pstep);
        pmax = pstep * (int)Math.Floor(pmax / (double)pstep);
        int players = pmin + pstep * rng.Next((pmax - pmin) / pstep + 1);

        var config = new MatchConfig
        {
            Gametype = gt,
            Map = map,
            BotCount = Math.Max(1, players - 1),
            BotSkill = (int)MenuState.Cvars.GetFloat("skill"),
            TimeLimit = 10, // QC: cvar_set("timelimit_override", "10")
            FragLimit = 0,
        };
        GD.Print($"[Menu] Instant action: {config}");
        CreateGameScreen.RaiseStartGame(config);
    }
}

/// <summary>
/// One campaign listbox row — everything <c>XonoticCampaignList_drawListBoxItem</c> draws inside an item:
/// the 4:3 map preview (with the gametype icon in its corner), the "Level N: title" line, the wrapped
/// briefing text, and the completion checkmark on the right. A toggle <see cref="Button"/> so selection
/// (orange fill, SKINCOLOR_LISTBOX_SELECTED) and hover (faint blue) come from the button states; a locked
/// future level renders as "???" at the QC's 0.2 alpha and is not selectable.
/// </summary>
internal partial class CampaignRowButton : Button
{
    /// <summary>Row height ≈ the QC 10-text-rows item (shows ~2 rows in the list like Base).</summary>
    private const int RowHeight = 170;

    /// <summary>Double-click / Enter on a row plays it (QC doubleClickListBoxItem → CampaignList_LoadMap).</summary>
    public event Action? PlayRequested;

    public CampaignRowButton(CampaignLevel level, int index, int frontier, ButtonGroup group)
    {
        ToggleMode = true;
        ButtonGroup = group;
        FocusMode = FocusModeEnum.None;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(0, RowHeight);

        bool completed = index < frontier;
        bool current = index == frontier;
        bool locked = index > frontier;
        // QC alphas: SELECTABLE 0.6 (done), CURRENT 1, FUTURE 0.2; description ×0.8.
        float alpha = completed ? 0.6f : current ? 1f : 0.2f;

        // The QC fills: selection = solid orange, hover = faint blue, otherwise nothing (the listbox backing).
        AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        AddThemeStyleboxOverride("disabled", new StyleBoxEmpty());
        AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = new Color(0.09f, 0.42f, 0.69f, 0.30f) });
        var selectedFill = new StyleBoxFlat { BgColor = new Color(0.9f, 0.53f, 0.28f, 0.95f) };
        AddThemeStyleboxOverride("pressed", selectedFill);
        AddThemeStyleboxOverride("hover_pressed", selectedFill);

        Disabled = locked; // the locked peek: visible but not selectable (QC setSelected clamps)

        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 6);
        AddChild(pad);

        var box = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 12);
        pad.AddChild(box);

        // --- the 4:3 map preview, gametype icon in its bottom-right corner (QC columnPreview/typeIcon) ---
        var previewFrame = new Control
        {
            CustomMinimumSize = new Vector2((RowHeight - 12) * 4f / 3f, RowHeight - 12),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var preview = new TextureRect
        {
            Texture = MenuSkin.Image("maps/" + level.MapName) ?? MenuSkin.SkinImage("nopreview_map"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Modulate = new Color(1, 1, 1, alpha),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        preview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        previewFrame.AddChild(preview);
        if (!locked && MenuSkin.SkinImage("gametype_" + level.Gametype) is { } typeIcon)
        {
            var icon = new TextureRect
            {
                Texture = typeIcon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                AnchorLeft = 1, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1,
                OffsetLeft = -30, OffsetTop = -30, OffsetRight = -2, OffsetBottom = -2,
            };
            previewFrame.AddChild(icon);
        }
        box.AddChild(previewFrame);

        // --- the text column: "Level N: <title>" + the wrapped briefing (QC name + longdesc lines) -------
        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        text.AddThemeConstantOverride("separation", 4);

        string title = locked
            ? string.Format(Localization.Tr("Level {0}:"), index + 1) + " ???"
            : string.Format(Localization.Tr("Level {0}:"), index + 1) + " " +
              (string.IsNullOrWhiteSpace(level.ShortDesc) ? level.MapName : level.ShortDesc);
        var titleLabel = new Label { Text = title, MouseFilter = MouseFilterEnum.Ignore };
        titleLabel.AddThemeColorOverride("font_color", new Color(0.96f, 0.99f, 1f, alpha));
        if (MenuSkin.BoldFont is { } bold) titleLabel.AddThemeFontOverride("font", bold);
        text.AddChild(titleLabel);

        if (!locked)
        {
            var desc = new Label
            {
                // The longdesc stores line breaks as the literal two characters "\n".
                Text = level.LongDesc.Replace("\\n", "\n"),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = true,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            desc.AddThemeColorOverride("font_color", new Color(0.96f, 0.99f, 1f, alpha * 0.8f));
            desc.AddThemeFontSizeOverride("font_size", 14);
            text.AddChild(desc);
        }
        box.AddChild(text);

        // --- the completion checkmark on the right (QC checkMark, only for beaten levels) ----------------
        if (completed && MenuSkin.SkinImage("checkmark") is { } check)
        {
            var mark = new TextureRect
            {
                Texture = check,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(30, 30),
                SizeFlagsVertical = SizeFlags.ShrinkEnd,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            box.AddChild(mark);
        }

        GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left } && !Disabled)
                PlayRequested?.Invoke();
        };
    }
}
