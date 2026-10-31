using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Server; // CampaignCatalog / CampaignLevel

namespace VortexArena.Game.Menu;

/// <summary>
/// The Singleplayer dialog — <c>XonoticSingleplayerDialog_fill</c> (qcsrc/menu/xonotic/dialog_singleplayer.qc) on its
/// 24 x 5 grid: the big "Instant action with bots!" button over the middle three columns, the campaign title row
/// (&lt;&lt; / title / &gt;&gt; cycling <c>maps/campaign*.txt</c>), the campaign list (<see cref="QcCampaignList"/>), the
/// Campaign Difficulty radios (<c>g_campaign_skill</c>) and the Leave-match / "Play campaign!" row.
///
/// Starting a level fires the shared <see cref="CreateGameScreen.StartGameRequested"/> callback with the campaign id
/// and level index; Instant Action rolls the QC probability table for a random bot match.
/// </summary>
public partial class SingleplayerScreen : MenuScreen
{
    private QcLabel _titleLabel = null!;
    private Button _prevButton = null!;
    private Button _nextButton = null!;
    private QcCampaignList _list = null!;

    private readonly List<CampaignLevel> _levels = new();
    private string _campaignId = CampaignCatalog.DefaultName;
    private string _campaignTitle = "Campaign";
    private int _frontier; // QC campaignIndex: highest unlocked level (the current playable one)

    protected override void BuildUi()
    {
        Name = "SingleplayerScreen";

        var me = new QcGrid(rows: 24, columns: 5);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TDempty((me.Columns - 3) * 0.5f);
            me.TD(2, 3, QcLook.Button(new Button { Text = Localization.Tr("Instant action with bots!") }, image: "bigbutton"))
                .Pressed += OnInstantAction;
        me.TR();
        me.TR();
        me.TR();
            _prevButton = me.TD(1, 1, Qc.Button("<<", () => CycleCampaign(-1)));
            _titleLabel = me.TD(1, me.Columns - 2, Qc.Header("???"));
            _nextButton = me.TD(1, 1, Qc.Button(">>", () => CycleCampaign(+1)));
        me.TR();
            _list = me.TD(me.Rows - 6, me.Columns, new QcCampaignList());
            _list.ItemActivated += _ => OnStartLevel();

        me.GotoRC(me.Rows - 2, 0);
            me.TD(1, 2, Qc.Label("Campaign Difficulty:", 0.5f));
            var skill = new QcRadioGroup();
            me.TD(1, 1, Qc.Radio(skill, "g_campaign_skill", "-2", "Easy"));
            me.TD(1, 1, Qc.Radio(skill, "g_campaign_skill", "0", "Medium"));
            me.TD(1, 1, Qc.Radio(skill, "g_campaign_skill", "2", "Hard"));
        me.TR();
            me.TDempty(me.Columns * 1f / 13f);
            var leave = new LeaveMatchButton(() => MenuCommand.Run("disconnect")) { CustomMinimumSize = Vector2.Zero };
            me.TD(1, me.Columns * 5f / 13f, QcLook.Button(leave));
            me.TDempty(me.Columns * 1f / 13f);
            me.TD(1, me.Columns * 5f / 13f, Qc.Button("Play campaign!", OnStartLevel, tint: new Color(0, 1, 0)));

        LoadCampaign();
    }

    public override void _Process(double delta)
    {
        // XonoticCampaignList_draw: reload when the campaign or its progress changed underneath (a level was won).
        if (!IsVisibleInTree()) return;
        string name = MenuState.Cvars.GetString("g_campaign_name");
        if (string.IsNullOrWhiteSpace(name)) name = CampaignCatalog.DefaultName;
        if (!string.Equals(name, _campaignId, StringComparison.OrdinalIgnoreCase)
            || Math.Clamp((int)MenuState.Cvars.GetFloat(ProgressCvar), 0, _levels.Count) != _frontier)
            LoadCampaign();
    }

    // -------------------------------------------------------------------------------------------------
    //  Campaign data
    // -------------------------------------------------------------------------------------------------

    /// <summary>The saved-progress cvar (QC <c>strcat("g_campaign", name, "_index")</c>).</summary>
    private string ProgressCvar => $"g_campaign{_campaignId}_index";

    /// <summary>XonoticCampaignList_loadCvars: read the campaign file (default <c>xonoticbeta</c>) and the saved
    /// frontier, then refill the list and the title.</summary>
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

        _titleLabel.Text = _campaignTitle;
        List<string> ids = CampaignIds();
        int at = ids.FindIndex(s => string.Equals(s, _campaignId, StringComparison.OrdinalIgnoreCase));
        _prevButton.Disabled = at <= 0;               // hasPrevCampaign = (j != 0)
        _nextButton.Disabled = at < 0 || at >= ids.Count - 1; // hasNextCampaign = (j != n - 1)
        _list.SetCampaign(_levels, _frontier);
    }

    /// <summary>All shipped campaign ids (QC search "maps/campaign*.txt"), for the &lt;&lt;/&gt;&gt; cycler.</summary>
    private static List<string> CampaignIds()
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

    /// <summary>QC MultiCampaign_Prev/Next → campaignGo: step through the campaign files and reload.</summary>
    private void CycleCampaign(int step)
    {
        List<string> ids = CampaignIds();
        if (ids.Count == 0)
            return;
        int at = ids.FindIndex(s => string.Equals(s, _campaignId, StringComparison.OrdinalIgnoreCase));
        int next = at < 0 ? (step >= 0 ? 0 : ids.Count - 1) : ((at + step) % ids.Count + ids.Count) % ids.Count;
        MenuState.Cvars.Set("g_campaign_name", ids[next]);
        MenuState.Cvars.MarkArchived("g_campaign_name");
        LoadCampaign();
    }

    // -------------------------------------------------------------------------------------------------
    //  Actions
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC CampaignList_LoadMap: start the selected level via the shared StartGame callback, in campaign mode.</summary>
    private void OnStartLevel()
    {
        int selected = _list.SelectedItem;
        // Only an unlocked level launches (setSelected clamps to campaignIndex).
        if (selected < 0 || selected > _frontier || selected >= _levels.Count)
            return;

        CampaignLevel lvl = _levels[selected];
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
/// xonotic/campaign.qc: ten text rows per level — the 4:3 mapshot with the gametype icon in its lower-right quarter
/// (unlocked levels), "Level N: title" half a row down, the briefing wrapped to the name column two rows below it
/// (at most seven lines, "..." when it runs over; an empty line is half a row), the checkmark in the row's lower
/// right for a beaten level. Beaten levels are SKINCOLOR/ALPHA_CAMPAIGN_SELECTABLE, the current one _CURRENT, the
/// one locked level shown _FUTURE as "???". The list holds the levels up to one past the current; the selection
/// never goes past the current one.
/// </summary>
public partial class QcCampaignList : MenuListBox
{
    private const int RowsPerItem = 10;
    private const float EmptyLineHeight = 0.5f;

    private IReadOnlyList<CampaignLevel> _levels = Array.Empty<CampaignLevel>();
    private int _campaignIndex;
    private readonly List<string> _wrapped = new();
    private float _wrappedFor = -1f;

    public QcCampaignList()
    {
        ItemHeight = RowsPerItem * MenuMetrics.FontNormalU;
        Resized += () => _wrappedFor = -1f;
    }

    /// <summary>loadCvars: nItems = min(campaignIndex + 2, entries), the current level selected, the last row scrolled
    /// into view.</summary>
    public void SetCampaign(IReadOnlyList<CampaignLevel> levels, int campaignIndex)
    {
        _levels = levels;
        _campaignIndex = campaignIndex;
        _wrappedFor = -1f;
        ItemCount = Math.Min(campaignIndex + 2, levels.Count);
        SetSelected(Math.Min(campaignIndex, ItemCount - 1));
        if (ItemCount - 1 > campaignIndex)
            ScrollToItem(ItemCount - 1);
        QueueRedraw();
    }

    // "prevent too late items from being played"
    public override void SetSelected(int i) => base.SetSelected(Math.Min(i, _campaignIndex));

    private float CheckMarkSize => 0.5f * ItemHeight;
    private float PreviewWidth => ItemHeight * 4f / 3f;
    private float NameSize => ContentWidth - PreviewWidth - CheckMarkSize - 4f * MenuMetrics.FontNormalU;

    /// <summary>rewrapCampaign: each briefing wrapped to the name column, at most rowsPerItem - 3 lines.</summary>
    private void Rewrap()
    {
        float width = NameSize;
        if (_wrappedFor == width) return;
        _wrappedFor = width;
        _wrapped.Clear();
        var item = new Vector2(ContentWidth, ItemHeight);
        Func<string, float> measure = t => QcDraw.TextWidthAtResize(t, item);
        foreach (CampaignLevel level in _levels)
        {
            float l = RowsPerItem - 3;
            var r = new System.Text.StringBuilder();
            bool tooLong = false;
            foreach (string paragraph in level.LongDesc.Replace("\\n", "\n").Split('\n'))
            {
                if (paragraph.Length == 0)
                {
                    l -= EmptyLineHeight;
                    r.Append('\n');
                    continue;
                }
                string? remaining = paragraph;
                while (remaining is not null)
                {
                    string s = QcDraw.WrappedLine(ref remaining, width, measure: measure);
                    if (--l < 0) { tooLong = true; break; }
                    r.Append(s).Append('\n');
                }
                if (tooLong) break;
            }
            string text = r.ToString();
            if (tooLong) text = text.TrimEnd('\n') + "...\n";
            _wrapped.Add(text.Length > 0 ? text[..^1] : text);
        }
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _levels.Count) return;
        Rewrap();
        CampaignLevel level = _levels[index];
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU, pw = PreviewWidth, cm = CheckMarkSize;

        (string which, float a, Color c) = index < _campaignIndex ? ("SELECTABLE", 0.8f, new Color(1, 1, 1))
            : index == _campaignIndex ? ("CURRENT", 1f, new Color(1, 1, 0)) : ("FUTURE", 0.2f, new Color(1, 1, 1));
        float alpha = MenuSkin.SkinNumber("ALPHA_CAMPAIGN_" + which, a);
        Color color = MenuSkin.SkinColor("COLOR_CAMPAIGN_" + which, c.R, c.G, c.B);

        Texture2D? shot = MenuSkin.Image("maps/" + level.MapName) ?? MenuSkin.SkinImage("nopreview_map");
        if (shot is not null)
            DrawTextureRect(shot, new Rect2(rect.Position, new Vector2(pw, h)), false, MenuSkin.Fade(Colors.White, alpha));

        if (index <= _campaignIndex && MenuSkin.SkinImage("gametype_" + level.Gametype) is { } icon)
            DrawTextureRect(icon, new Rect2(rect.Position + new Vector2(pw - cm, h - cm), new Vector2(cm, cm)), false);

        float nameOrigin = pw + ch, nameSize = NameSize;
        if (index < _campaignIndex && MenuSkin.SkinImage("checkmark") is { } check)
            DrawTextureRect(check, new Rect2(rect.Position + new Vector2(nameOrigin + nameSize + 2f * ch, h - cm), new Vector2(cm, cm)), false);

        string title = index <= _campaignIndex ? level.ShortDesc : "???";
        string s = QcDraw.ShortenToWidth(string.Format(Localization.Tr("Level {0}:"), index + 1) + " " + title, nameSize);
        Color textColor = MenuSkin.TextLift(MenuSkin.Fade(color, alpha));
        QcDraw.Text(this, rect.Position + new Vector2(nameOrigin, 0.5f * ch), s, textColor);

        if (index > _campaignIndex || index >= _wrapped.Count) return;
        Color descColor = MenuSkin.TextLift(MenuSkin.Fade(color, alpha * MenuSkin.SkinNumber("ALPHA_CAMPAIGN_DESCRIPTION", 0.7f)));
        float y = 2.5f * ch;
        foreach (string line in _wrapped[index].Split('\n'))
        {
            if (line.Length > 0)
            {
                QcDraw.Text(this, rect.Position + new Vector2(nameOrigin, y), line, descColor);
                y += ch;
            }
            else
                y += ch * EmptyLineHeight;
        }
    }
}
