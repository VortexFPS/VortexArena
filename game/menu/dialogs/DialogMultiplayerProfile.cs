using System.Globalization;
using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The multiplayer Profile tab/dialog — a faithful C# port of <c>XonoticProfileTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_profile.qc). Two halves, like the QC grid:
/// <list type="bullet">
/// <item>LEFT — the Name section (header, the bold colour-coded name preview, the <c>_cl_name</c> box, then
/// the HSL colorpicker beside the charmap, both typing into the box), then the Model section: the Glowing /
/// Detail colour PALETTE GRIDS (15 <c>XonoticColorButton</c>s each, editing the two <c>_cl_color</c>
/// nibbles) on the left with the player-model selector (&lt;&lt; preview &gt;&gt;) beside them — QC's
/// "MODEL RIGHT, COLOR LEFT" arrangement.</item>
/// <item>RIGHT — the Statistics section (the three <c>cl_allow_uid*</c> opt-ins + the stats list, which is
/// an honest empty note until the stats backend exists) and the "Select language..." button.</item>
/// </list>
/// Bottom row: the full-width "Apply immediately" command button (same command string the QC issues).
/// </summary>
public partial class DialogMultiplayerProfile : MenuScreen
{
    // QC profileApplyButton: makeXonoticCommandButton(_("Apply immediately"), ..., COMMANDBUTTON_APPLY).
    private const string ApplyCommand =
        "color -1 -1; name \"$_cl_name\"; playermodel $_cl_playermodel; playerskin $_cl_playerskin";

    /// <summary>True when hosted as the Multiplayer dialog's Profile TAB (QC XonoticProfileTab): no title/Back.</summary>
    public bool Embedded { get; set; }

    protected override void BuildUi()
    {
        Name = "DialogMultiplayerProfile";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, Embedded ? 4 : 24);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        if (!Embedded && !HostProvidesTitle)
            root.AddChild(MakeTitle("Profile"));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 26);
        root.AddChild(body);

        // =========================================================================== LEFT HALF (QC cols 0-3)
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.05f };
        left.AddThemeConstantOverride("separation", 8);
        body.AddChild(left);

        // --- NAME ---
        var nameHeader = MakeHeader("Name");
        nameHeader.HorizontalAlignment = HorizontalAlignment.Center;
        left.AddChild(nameHeader);

        left.AddChild(new ProfileNamePreview());

        var nameBox = Widgets.InputBox("_cl_name", "Player", "Name under which you will appear in the game");
        left.AddChild(nameBox);

        // QC: colorpicker (1 col) beside the charmap (2 cols), both 5 rows tall, typing into the name box.
        var pickers = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pickers.AddThemeConstantOverride("separation", 10);
        var hsl = HslColorPicker.ForNameBox(nameBox);
        hsl.CustomMinimumSize = new Vector2(150, 132);
        hsl.SizeFlagsHorizontal = SizeFlags.Fill;
        pickers.AddChild(hsl);
        var charmap = new CharmapPicker(nameBox)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 132),
        };
        pickers.AddChild(charmap);
        left.AddChild(pickers);

        left.AddChild(Ui.Spacer(6));

        // --- MODEL (QC: colors at column 0, the model selector to their right) ---
        var modelHeader = MakeHeader("Model");
        modelHeader.HorizontalAlignment = HorizontalAlignment.Center;
        left.AddChild(modelHeader);

        var modelRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        modelRow.AddThemeConstantOverride("separation", 16);
        left.AddChild(modelRow);

        var colors = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
        colors.AddThemeConstantOverride("separation", 6);
        colors.AddChild(Ui.Header("Glowing color"));
        colors.AddChild(new ProfileColorGrid(pants: false));
        colors.AddChild(Ui.Spacer(10));
        colors.AddChild(Ui.Header("Detail color"));
        colors.AddChild(new ProfileColorGrid(pants: true));
        modelRow.AddChild(colors);

        var pms = new PlayerModelSelector
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        modelRow.AddChild(pms);

        // ========================================================================== RIGHT HALF (QC col 3.1+)
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        body.AddChild(right);

        var statsHeader = MakeHeader("Statistics");
        statsHeader.HorizontalAlignment = HorizontalAlignment.Center;
        right.AddChild(statsHeader);

        right.AddChild(Widgets.CheckBox("cl_allow_uidtracking",
            "Allow player statistics to track your client"));

        var uid2name = Widgets.CheckBox("cl_allow_uid2name",
            "Allow player statistics to use your nickname");
        right.AddChild(uid2name);
        Dependent.Bind(uid2name, "cl_allow_uidtracking", 1, 1);

        var uidranking = Widgets.CheckBox("cl_allow_uidranking",
            "Allow player statistics to rank you in leaderboards");
        right.AddChild(uidranking);
        Dependent.Bind(uidranking, "cl_allow_uidtracking", 1, 1);

        // QC makeXonoticStatsList() — an XonoticStatsList drained from the per-player stats DB (PS_D_IN_DB),
        // populated ASYNCHRONOUSLY by PlayerStats_PlayerDetail_CheckUpdate (an HTTP fetch to the stats server,
        // statslist.qc:295-298). The port has no playerstats DB / urllib fetch, so the list stays an honest
        // "pending" placeholder; we still kick the (inert) update on show, mirroring the QC showNotify hook.
        var statsList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 160),
            FocusMode = FocusModeEnum.None,
        };
        right.AddChild(statsList);
        var statsNote = Ui.Label("(player statistics fetched from the stats server — fetch backend pending)");
        statsNote.HorizontalAlignment = HorizontalAlignment.Center;
        statsNote.Modulate = new Color(1, 1, 1, 0.5f);
        right.AddChild(statsNote);
        // QC XonoticStatsList_showNotify → PlayerStats_PlayerDetail_CheckUpdate(); inert until T50 defines it.
        MenuCommand.Run("menu_cmd playerstats_update");

        // QC: "Select language..." centred at half width near the bottom of the right half.
        var langRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        langRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var lang = Widgets.CommandButton("Select language...", "menu_cmd languageselect");
        lang.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lang.SizeFlagsStretchRatio = 2f;
        langRow.AddChild(lang);
        langRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        right.AddChild(langRow);

        // ================================================================================== BOTTOM ROW
        // QC: the Apply button spans the full bottom row; a pushed dialog also gets Back.
        var apply = Widgets.CommandButton("Apply immediately", ApplyCommand);
        root.AddChild(Embedded ? MakeButtonBar(apply) : MakeButtonBar(apply, MakeButton("Back", GoBack)));
    }
}

/// <summary>
/// A read-only colorized echo of the <c>_cl_name</c> cvar — the C# stand-in for the QC bold title label that
/// mirrors the typed name. A <see cref="RichTextLabel"/> rendering BBCode so the player's Quake <c>^</c> colour
/// codes show in colour, exactly like the engine's colour-coded name draw (via <see cref="MenuColorCodes"/>).
/// </summary>
public partial class ProfileNamePreview : RichTextLabel
{
    private static CvarService Cvars => MenuState.Cvars;

    public ProfileNamePreview()
    {
        BbcodeEnabled = true;
        FitContent = true;
        ScrollActive = false;
        AutowrapMode = TextServer.AutowrapMode.Off;
        CustomMinimumSize = new Vector2(0, 36);
        AddThemeFontSizeOverride("normal_font_size", 28);
        AddThemeColorOverride("default_color", MenuSkin.Bright);
        if (MenuSkin.BoldFont is { } bold) AddThemeFontOverride("normal_font", bold);
    }

    public override void _EnterTree() { Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == "_cl_name") Refresh(); }

    private void Refresh()
    {
        string raw = Cvars.GetString("_cl_name");
        if (string.IsNullOrEmpty(raw)) raw = "Player";
        Text = "[center]" + MenuColorCodes.ToBBCode(raw) + "[/center]";
    }
}

/// <summary>
/// One player-colour palette grid — the faithful C# successor to the QC's row of 15
/// <c>XonoticColorButton</c>s (3×5, <c>colorbutton_*</c> skin art tinted by
/// <c>colormapPaletteColor(i, …)</c>). Both grids edit the single packed integer cvar <c>_cl_color</c>:
/// the glowing/shirt colour lives in the low nibble (<c>color &amp; 15</c>), the detail/pants colour in
/// the high nibble (<c>(color &amp; 240) / 16</c>); selecting rewrites just that nibble, exactly as
/// <c>XonoticColorButton_saveCvars</c> does.
/// </summary>
public partial class ProfileColorGrid : GridContainer
{
    private const string Cvar = "_cl_color";
    private const int ColorCount = 15; // QC: for(i = 0; i < 15; ++i) — no animated rainbow in the profile grid

    private static CvarService Cvars => MenuState.Cvars;

    private readonly bool _pants;
    private readonly Button[] _buttons = new Button[ColorCount];
    private bool _updating;

    /// <param name="pants">true = detail colour (high nibble); false = glowing colour (low nibble).</param>
    public ProfileColorGrid(bool pants)
    {
        _pants = pants;
        Columns = 5;
        AddThemeConstantOverride("h_separation", 4);
        AddThemeConstantOverride("v_separation", 4);

        Texture2D? art = MenuSkin.SkinImage("colorbutton_n");
        for (int i = 0; i < ColorCount; i++)
        {
            (float r, float g, float b) = CsqcModelAppearance.ColormapPaletteColor(i, pants, 0f);
            var tint = new Color(r, g, b);
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(30, 26),
                FocusMode = FocusModeEnum.None,
                ToggleMode = true,
                TooltipText = $"{(pants ? "Detail" : "Glowing")} color {i}",
            };
            btn.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            btn.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
            btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            var pressedRim = new StyleBoxFlat
            {
                BgColor = Colors.Transparent,
                BorderColor = MenuSkin.Bright,
                DrawCenter = false,
            };
            pressedRim.SetBorderWidthAll(2);
            btn.AddThemeStyleboxOverride("pressed", pressedRim);
            btn.AddThemeStyleboxOverride("hover_pressed", pressedRim);

            if (art is not null)
            {
                // The skin's colorbutton art tinted by the palette colour (QC draws colorbutton_n modulated).
                btn.Icon = art;
                btn.ExpandIcon = true;
                btn.IconAlignment = HorizontalAlignment.Center;
                foreach (string state in new[] { "icon_normal_color", "icon_hover_color", "icon_pressed_color",
                                                 "icon_hover_pressed_color", "icon_focus_color" })
                    btn.AddThemeColorOverride(state, tint);
            }
            else
            {
                btn.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = tint });
                btn.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = tint.Lightened(0.2f) });
            }

            int index = i;
            btn.Pressed += () => OnPick(index);
            _buttons[i] = btn;
            AddChild(btn);
        }
    }

    public override void _EnterTree() { Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == Cvar) Refresh(); }

    private int CurrentIndex()
    {
        int packed = (int)Cvars.GetFloat(Cvar);
        return _pants ? (packed & 240) / 16 : packed & 15;
    }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        int active = CurrentIndex();
        for (int i = 0; i < ColorCount; i++)
            _buttons[i].SetPressedNoSignal(i == active);
        _updating = false;
    }

    private void OnPick(int index)
    {
        if (_updating) return;
        int packed = (int)Cvars.GetFloat(Cvar);
        // Rewrite only our nibble, preserving the other (mirrors XonoticColorButton_saveCvars).
        packed = _pants
            ? (packed & 15) + index * 16
            : (packed & 240) + index;
        Cvars.Set(Cvar, packed.ToString(CultureInfo.InvariantCulture));
        Cvars.MarkArchived(Cvar);
        Refresh();
    }
}
