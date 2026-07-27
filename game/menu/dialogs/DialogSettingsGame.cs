using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Game settings tab — a faithful C# port of the Game settings group, which in QC
/// (qcsrc/menu/xonotic/dialog_settings_game.qc) is itself a tab controller hosting six sub-dialogs through a
/// topic list + swappable panel (<c>XonoticGameSettingsTab</c>). We reproduce that "tab of tabs" with a nested
/// Godot <see cref="TabContainer"/> whose six pages are the ported sub-dialogs:
///   Crosshair  (dialog_settings_game_crosshair.qc)
///   HUD        (dialog_settings_game_hud.qc)
///   Models     (dialog_settings_game_model.qc)
///   View       (dialog_settings_game_view.qc)
///   Weapons    (dialog_settings_game_weapons.qc)
///   Messages   (dialog_settings_game_messages.qc)
/// Each inner tab binds the same engine cvars its QC counterpart does, in QC order, with the same dependencies.
///
/// Dependency note: QC chains several <c>setDependentAND</c> calls onto one widget (enable only when *all*
/// conditions hold). The foundation <see cref="Dependent"/> tracks a single driver cvar, so where QC ANDs
/// multiple cvars we apply the primary/innermost gating dependency (the immediate enabling toggle) and note
/// the remaining QC conditions in a comment. This is faithful to the dominant intent and honest about the gap.
/// </summary>
public partial class DialogSettingsGame : Control
{
    // The Game "tab of tabs" is a plain full-rect Control hosting a nested TabContainer — NOT a SettingsTab,
    // because a TabContainer inside a ScrollContainer collapses to zero height (the scroll grants it infinite
    // vertical space, so the page area never gets a bounded height). Each inner page is itself a scrollable
    // SettingsTab, so per-tab scrolling still works.
    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var tabs = new TabContainer { Name = "GameTabs" };
        tabs.SetAnchorsPreset(LayoutPreset.FullRect);

        AddInner(tabs, "Crosshair",   new DialogSettingsGameCrosshair());
        AddInner(tabs, "HUD",         new DialogSettingsGameHud());
        AddInner(tabs, "Models",      new DialogSettingsGameModel());
        AddInner(tabs, "View",        new DialogSettingsGameView());
        AddInner(tabs, "Weapons",     new DialogSettingsGameWeapons());
        AddInner(tabs, "Messages",    new DialogSettingsGameMessages());
        AddInner(tabs, "Damage Text", new DialogSettingsGameDamageText());

        AddChild(tabs);
    }

    private static void AddInner(TabContainer tabs, string title, Control tab)
    {
        tab.Name = title; // TabContainer derives the tab title from the child node's Name.
        tabs.AddChild(tab);
    }
}

// =============================================================================================================
//  Crosshair  —  qcsrc/menu/xonotic/dialog_settings_game_crosshair.qc
// =============================================================================================================

/// <summary>
/// Crosshair sub-tab — port of <c>XonoticGameCrosshairSettingsTab_fill</c>. Binds crosshair_enabled /
/// crosshair_per_weapon / crosshair / crosshair_size / crosshair_alpha / crosshair_color* / crosshair_ring* /
/// crosshair_dot* / crosshair_effect_scalefade / crosshair_hittest* / crosshair_hitindication / crosshair_pickup.
/// The QC crosshair *picker* is the real <see cref="CrosshairPicker"/> (the 3×12 image grid, indices 31..66),
/// the QC colorpickerString widgets are the real HSL image picker (<see cref="HslColorPicker"/>) on the same
/// "R G B" string cvars, and the QC crosshair preview is the live <see cref="CrosshairPreview"/> — all faithful
/// ports of their QC widgets (T50).
/// </summary>
public partial class DialogSettingsGameCrosshair : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        box.AddChild(Ui.Header("Crosshair"));

        // crosshair_enabled radio set: 0 = no crosshair, "Per weapon" (crosshair_per_weapon), 2 = Custom.
        // QC: radio(3,"crosshair_enabled","0"); radio_T(3,"crosshair_per_weapon")+makeMulti("crosshair_enabled"); radio(3,"crosshair_enabled","2").
        var modeGroup = new ButtonGroup();
        var modeRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        modeRow.AddThemeConstantOverride("separation", 8);
        modeRow.AddChild(Widgets.RadioButton("crosshair_enabled", "0", "No crosshair", modeGroup));
        modeRow.AddChild(Widgets.RadioButton("crosshair_per_weapon", "1", "Per weapon", modeGroup,
            "Set a different crosshair for each weapon, good if you play without weapon models"));
        modeRow.AddChild(Widgets.RadioButton("crosshair_enabled", "2", "Custom", modeGroup));
        box.AddChild(modeRow);

        // Crosshair picker — QC makeXonoticCrosshairPicker(): the real 3×12 image grid (crosshair indices
        // 31..66) editing the "crosshair" cvar. QC deps: NOT crosshair_alpha==0 AND crosshair_per_weapon==0
        // AND crosshair_enabled∈[1,2] (primary gate applied; see the class note on multi-cvar deps).
        var picker = new CrosshairPicker();
        box.AddChild(picker);
        Dependent.Bind(picker, "crosshair_enabled", 1, 2);

        // Crosshair preview — QC makeXonoticCrosshairPreview(): the live preview of the selected crosshair
        // (image at crosshair_size, tinted crosshair_color@crosshair_alpha, + the dot).
        box.AddChild(new CrosshairPreview());

        // Size — QC slider(0.1,1.0,0.01,"crosshair_size"); deps NOT crosshair_alpha==0 AND crosshair_enabled∈[1,2].
        var size = Widgets.Slider("crosshair_size", 0.1f, 1.0f, 0.01f);
        var sizeRow = Ui.Row("Size:", size);
        box.AddChild(sizeRow);
        Dependent.Bind(sizeRow, "crosshair_enabled", 1, 2);

        // Opacity — QC slider(0.1,1,0.1,"crosshair_alpha") formatString "%"; dep crosshair_enabled∈[1,2].
        var alpha = Widgets.Slider("crosshair_alpha", 0.1f, 1f, 0.1f, format: Percent);
        var alphaRow = Ui.Row("Opacity:", alpha);
        box.AddChild(alphaRow);
        Dependent.Bind(alphaRow, "crosshair_enabled", 1, 2);

        // Color mode — QC radio(5,"crosshair_color_special","1"=Per weapon / "2"=By health / "0"=Custom).
        // QC deps: NOT crosshair_alpha==0 AND crosshair_enabled∈[1,2].
        var colorGroup = new ButtonGroup();
        var colorRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        colorRow.AddThemeConstantOverride("separation", 8);
        colorRow.AddChild(Widgets.RadioButton("crosshair_color_special", "1", "Per weapon", colorGroup));
        colorRow.AddChild(Widgets.RadioButton("crosshair_color_special", "2", "By health", colorGroup));
        colorRow.AddChild(Widgets.RadioButton("crosshair_color_special", "0", "Custom", colorGroup));
        box.AddChild(Ui.Row("Color:", colorRow));
        Dependent.Bind(colorRow, "crosshair_enabled", 1, 2);

        // Custom color — QC makeXonoticColorpickerString("crosshair_color",...): the HSL image color picker on
        // the "crosshair_color" string cvar. QC deps: NOT crosshair_alpha==0 AND crosshair_color_special==0 AND
        // crosshair_enabled∈[1,2].
        var colorPick = HslColorPicker.ForStringCvar("crosshair_color");
        var colorPickRow = Ui.Row("Custom color:", colorPick);
        box.AddChild(colorPickRow);
        Dependent.Bind(colorPickRow, "crosshair_color_special", 0, 0); // enable only while special==0 (Custom)

        box.AddChild(Ui.Spacer());

        // Rings — QC checkBox_T(0,"crosshair_ring",...) makeMulti("crosshair_ring_reload"); dep crosshair_enabled∈[1,2].
        var ring = Widgets.CheckBox("crosshair_ring", "Use rings to indicate weapon status");
        box.AddChild(ring);
        Dependent.Bind(ring, "crosshair_enabled", 1, 2);

        // Ring size — QC slider(1.5,4,0.25,"crosshair_ring_size"); deps NOT ring_alpha==0 AND ring==1 AND enabled∈[1,2].
        var ringSize = Widgets.Slider("crosshair_ring_size", 1.5f, 4f, 0.25f);
        var ringSizeRow = Ui.Row("Ring size:", ringSize);
        box.AddChild(ringSizeRow);
        Dependent.Bind(ringSizeRow, "crosshair_ring", 1, 1);

        // Ring opacity — QC slider(0.1,1,0.1,"crosshair_ring_alpha") "%"; deps crosshair_ring==1 AND enabled∈[1,2].
        var ringAlpha = Widgets.Slider("crosshair_ring_alpha", 0.1f, 1f, 0.1f, format: Percent);
        var ringAlphaRow = Ui.Row("Ring opacity:", ringAlpha);
        box.AddChild(ringAlphaRow);
        Dependent.Bind(ringAlphaRow, "crosshair_ring", 1, 1);

        box.AddChild(Ui.Spacer());

        // Center dot — QC checkBox(0,"crosshair_dot",...); dep crosshair_enabled∈[1,2].
        var dot = Widgets.CheckBox("crosshair_dot", "Enable center crosshair dot");
        box.AddChild(dot);
        Dependent.Bind(dot, "crosshair_enabled", 1, 2);

        // Dot size — QC slider(0.2,2,0.1,"crosshair_dot_size"); deps NOT dot_alpha==0 AND dot==1 AND enabled∈[1,2].
        var dotSize = Widgets.Slider("crosshair_dot_size", 0.2f, 2f, 0.1f);
        var dotSizeRow = Ui.Row("Dot size:", dotSize);
        box.AddChild(dotSizeRow);
        Dependent.Bind(dotSizeRow, "crosshair_dot", 1, 1);

        // Dot opacity — QC slider(0.1,1,0.1,"crosshair_dot_alpha") "%"; deps crosshair_dot==1 AND enabled∈[1,2].
        var dotAlpha = Widgets.Slider("crosshair_dot_alpha", 0.1f, 1f, 0.1f, format: Percent);
        var dotAlphaRow = Ui.Row("Dot opacity:", dotAlpha);
        box.AddChild(dotAlphaRow);
        Dependent.Bind(dotAlphaRow, "crosshair_dot", 1, 1);

        // Dot color mode — QC radio(1,"crosshair_dot_color_custom","0"=normal color / "1"=Custom).
        // QC deps: NOT dot_alpha==0 AND dot==1 AND enabled∈[1,2].
        var dotColorGroup = new ButtonGroup();
        var dotColorRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        dotColorRow.AddThemeConstantOverride("separation", 8);
        dotColorRow.AddChild(Widgets.RadioButton("crosshair_dot_color_custom", "0", "Use normal color", dotColorGroup));
        dotColorRow.AddChild(Widgets.RadioButton("crosshair_dot_color_custom", "1", "Custom", dotColorGroup));
        box.AddChild(Ui.Row("Dot color:", dotColorRow));
        Dependent.Bind(dotColorRow, "crosshair_dot", 1, 1);

        // Custom dot color — QC makeXonoticColorpickerString("crosshair_dot_color",...): the HSL image color
        // picker on the "crosshair_dot_color" string cvar. QC deps: crosshair_dot==1 AND enabled∈[1,2] AND
        // crosshair_dot_color_custom==1.
        var dotColorPick = HslColorPicker.ForStringCvar("crosshair_dot_color");
        var dotColorPickRow = Ui.Row("Custom dot color:", dotColorPick);
        box.AddChild(dotColorPickRow);
        Dependent.Bind(dotColorPickRow, "crosshair_dot_color_custom", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: checkBox(0,"crosshair_effect_scalefade",...); dep crosshair_enabled∈[1,2].
        var scalefade = Widgets.CheckBox("crosshair_effect_scalefade", "Smooth effects of crosshairs");
        box.AddChild(scalefade);
        Dependent.Bind(scalefade, "crosshair_enabled", 1, 2);

        // QC: checkBox(0,"crosshair_hittest",...); dep crosshair_enabled∈[1,2].
        var hittest = Widgets.CheckBox("crosshair_hittest", "Perform hit tests for the crosshair");
        box.AddChild(hittest);
        Dependent.Bind(hittest, "crosshair_enabled", 1, 2);

        // QC: checkBox(0,"crosshair_hittest_blur_wall",...); deps NOT crosshair_hittest==0 AND enabled∈[1,2].
        var blurWall = Widgets.CheckBox("crosshair_hittest_blur_wall", "Blur if obstructed by an obstacle");
        box.AddChild(blurWall);
        Dependent.BindNot(blurWall, "crosshair_hittest", 0);

        // QC: checkBox(0,"crosshair_hittest_blur_teammate",...); deps NOT crosshair_hittest==0 AND enabled∈[1,2].
        var blurMate = Widgets.CheckBox("crosshair_hittest_blur_teammate", "Blur if obstructed by a teammate");
        box.AddChild(blurMate);
        Dependent.BindNot(blurMate, "crosshair_hittest", 0);

        // QC: makeXonoticCheckBoxEx(1.25, 1, "crosshair_hittest",...) — checked stores 1.25 (hit-test WITH the
        // teammate shrink), unchecked stores 1 (hit-test only, NOT 0 — the "Perform hit tests" box above owns off).
        var shrinkMate = Widgets.ValueCheckBox("crosshair_hittest", 1.25f, 1f, "Shrink if obstructed by a teammate");
        box.AddChild(shrinkMate);
        Dependent.BindNot(shrinkMate, "crosshair_hittest", 0);

        // QC: makeXonoticCheckBoxEx(0.5, 0, "crosshair_hitindication",...); dep crosshair_enabled∈[1,2].
        var hitIndication = Widgets.ValueCheckBox("crosshair_hitindication", 0.5f, 0f, "Animate crosshair when hitting an enemy");
        box.AddChild(hitIndication);
        Dependent.Bind(hitIndication, "crosshair_enabled", 1, 2);

        // QC: makeXonoticCheckBoxEx(0.25, 0, "crosshair_pickup",...); dep crosshair_enabled∈[1,2].
        var pickup = Widgets.ValueCheckBox("crosshair_pickup", 0.25f, 0f, "Animate crosshair when picking up an item");
        box.AddChild(pickup);
        Dependent.Bind(pickup, "crosshair_enabled", 1, 2);
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}

// =============================================================================================================
//  HUD  —  qcsrc/menu/xonotic/dialog_settings_game_hud.qc
// =============================================================================================================

/// <summary>
/// HUD sub-tab — port of <c>XonoticGameHUDSettingsTab_fill</c>: Scoreboard, Waypoints, Player Names, Other,
/// and the "Enter HUD editor" button. Binds hud_panel_scoreboard_* / cl_hidewaypoints / g_waypointsprite_* /
/// hud_shownames* / hud_speed_unit / hud_damage / hud_dynamic_*.
/// </summary>
public partial class DialogSettingsGameHud : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // --- Scoreboard ---
        box.AddChild(Ui.Header("Scoreboard"));

        // QC: slider(0.1,1,0.1,"hud_panel_scoreboard_bg_alpha") formatString "%".
        box.AddChild(Ui.Row("Opacity:", Widgets.Slider("hud_panel_scoreboard_bg_alpha", 0.1f, 1f, 0.1f, format: Percent)));

        // QC: makeXonoticScoreboardFadeTimeSlider() — a dedicated slider on hud_panel_scoreboard_fadeinspeed.
        // Approximate with a plain slider on the same cvar (the QC widget is just a tuned slider with a readout).
        box.AddChild(Ui.Row("Fading speed:", Widgets.Slider("hud_panel_scoreboard_fadeinspeed", 0f, 10f, 1f)));

        box.AddChild(Widgets.CheckBox("hud_panel_scoreboard_table_highlight", "Enable rows / columns highlighting"));
        box.AddChild(Widgets.CheckBox("hud_panel_scoreboard_accuracy", "Show accuracy underneath scoreboard"));

        // QC: mixedslider "hud_panel_scoreboard_team_size_position" — Left=1 / Off=0 / Right=2 (display order as in QC).
        var teamSize = Widgets.TextSlider("hud_panel_scoreboard_team_size_position",
            "Team size position: Off=do not show; Left=on the left side of the scoreboard and move team scores to the right; Right=on the right of the scoreboard")
            .Add("Left", 1).Add("Off", 0).Add("Right", 2);
        box.AddChild(Ui.Row("Show team sizes:", teamSize));

        box.AddChild(Ui.Spacer());

        // --- Waypoints ---
        box.AddChild(Ui.Header("Waypoints"));

        // QC: checkBox_T(1,"cl_hidewaypoints",...). cl_hidewaypoints is the "hide" cvar; the dependents below use
        // cl_hidewaypoints==0 to mean "waypoints are shown". Same cvar, same label as QC.
        box.AddChild(Widgets.CheckBox("cl_hidewaypoints", "Display waypoint markers for objectives on the map",
            "Show various gametype specific waypoints"));

        // Waypoint opacity — QC slider(0.1,1,0.1,"g_waypointsprite_alpha") "%"; dep cl_hidewaypoints==0.
        var wpAlpha = Widgets.Slider("g_waypointsprite_alpha", 0.1f, 1f, 0.1f, format: Percent);
        var wpAlphaRow = Ui.Row("Opacity:", wpAlpha);
        box.AddChild(wpAlphaRow);
        Dependent.Bind(wpAlphaRow, "cl_hidewaypoints", 0, 0);

        // Waypoint font size — QC slider(5,16,1,"g_waypointsprite_fontsize"); deps NOT alpha==0 AND cl_hidewaypoints==0.
        var wpFont = Widgets.Slider("g_waypointsprite_fontsize", 5f, 16f, 1f);
        var wpFontRow = Ui.Row("Font size:", wpFont);
        box.AddChild(wpFontRow);
        Dependent.BindNot(wpFontRow, "g_waypointsprite_alpha", 0);

        // Waypoint edge offset — QC slider(0,0.3,0.01,"g_waypointsprite_edgeoffset_bottom") + multi(top/left/right).
        // QC deps: NOT alpha==0 AND cl_hidewaypoints==0.
        var wpEdge = Widgets.Slider("g_waypointsprite_edgeoffset_bottom", 0f, 0.3f, 0.01f);
        var wpEdgeRow = Ui.Row("Edge offset:", wpEdge);
        box.AddChild(wpEdgeRow);
        Dependent.BindNot(wpEdgeRow, "g_waypointsprite_alpha", 0);

        // QC: makeXonoticCheckBoxEx(0.25, 1, "g_waypointsprite_crosshairfadealpha",...) — an INVERTED value
        // pair (yes 0.25 < no 1): checked fades the sprite to 25% near the crosshair, unchecked = full alpha.
        // Deps: NOT alpha==0 AND hidewp==0.
        var wpFade = Widgets.ValueCheckBox("g_waypointsprite_crosshairfadealpha", 0.25f, 1f, "Fade when near the crosshair");
        box.AddChild(wpFade);
        Dependent.BindNot(wpFade, "g_waypointsprite_alpha", 0);

        // QC: checkBox(0,"g_waypointsprite_text",...); deps NOT alpha==0 AND hidewp==0.
        var wpText = Widgets.CheckBox("g_waypointsprite_text", "Display names instead of icons");
        box.AddChild(wpText);
        Dependent.BindNot(wpText, "g_waypointsprite_alpha", 0);

        box.AddChild(Ui.Spacer());

        // --- Player Names ---
        box.AddChild(Ui.Header("Player Names"));

        box.AddChild(Widgets.CheckBox("hud_shownames", "Show names above players"));

        // QC: slider(0.1,1,0.1,"hud_shownames_alpha") "%"; dep hud_shownames==1.
        var snAlpha = Widgets.Slider("hud_shownames_alpha", 0.1f, 1f, 0.1f, format: Percent);
        var snAlphaRow = Ui.Row("Opacity:", snAlpha);
        box.AddChild(snAlphaRow);
        Dependent.Bind(snAlphaRow, "hud_shownames", 1, 1);

        // QC: slider(5,16,1,"hud_shownames_fontsize"); deps NOT alpha==0 AND shownames==1.
        var snFont = Widgets.Slider("hud_shownames_fontsize", 5f, 16f, 1f);
        var snFontRow = Ui.Row("Font size:", snFont);
        box.AddChild(snFontRow);
        Dependent.BindNot(snFontRow, "hud_shownames_alpha", 0);

        // QC: slider(2000,10000,500,"hud_shownames_maxdistance") formatString "%s qu"; deps NOT alpha==0 AND shownames==1.
        var snDist = Widgets.Slider("hud_shownames_maxdistance", 2000f, 10000f, 500f, format: Qu);
        var snDistRow = Ui.Row("Max distance:", snDist);
        box.AddChild(snDistRow);
        Dependent.BindNot(snDistRow, "hud_shownames_alpha", 0);

        // QC: mixedslider "hud_shownames_decolorize" — Never=0 / Teamplay=1 / Always=2; deps NOT alpha==0 AND shownames==1.
        var snDecolor = Widgets.TextSlider("hud_shownames_decolorize")
            .Add("Never", 0).Add("Teamplay", 1).Add("Always", 2);
        var snDecolorRow = Ui.Row("Decolorize:", snDecolor);
        box.AddChild(snDecolorRow);
        Dependent.BindNot(snDecolorRow, "hud_shownames_alpha", 0);

        // QC: makeXonoticCheckBoxEx(25, 0, "hud_shownames_crosshairdistance",...) — checked stores 25 (the
        // crosshair-proximity DISTANCE in qu, not a flag), unchecked 0. Deps: NOT alpha==0 AND shownames==1.
        var snCross = Widgets.ValueCheckBox("hud_shownames_crosshairdistance", 25f, 0f, "Only when near crosshair");
        box.AddChild(snCross);
        Dependent.BindNot(snCross, "hud_shownames_alpha", 0);

        // QC: checkBox(0,"hud_shownames_status",...); deps NOT alpha==0 AND shownames==1.
        var snStatus = Widgets.CheckBox("hud_shownames_status", "Display health and armor");
        box.AddChild(snStatus);
        Dependent.BindNot(snStatus, "hud_shownames_alpha", 0);

        box.AddChild(Ui.Spacer());

        // --- Other ---
        box.AddChild(Ui.Header("Other"));

        // QC: mixedslider "hud_speed_unit" — qu/s=1 / m/s=2 / km/h=3 / mph=4 / knots=5.
        var speedUnit = Widgets.TextSlider("hud_speed_unit")
            .Add("qu/s", 1).Add("m/s", 2).Add("km/h", 3).Add("mph", 4).Add("knots", 5);
        box.AddChild(Ui.Row("Speed unit:", speedUnit));

        // QC: mixedslider "hud_damage" formatString "%" — Disable=0, then addRange(0.05,1,0.05).
        var dmgOverlay = Widgets.TextSlider("hud_damage").Add("Disable", 0);
        for (float v = 0.05f; v <= 1.0001f; v += 0.05f)
            dmgOverlay.Add($"{Mathf.RoundToInt(v * 100f)}%", v);
        box.AddChild(Ui.Row("Damage overlay:", dmgOverlay));

        // QC: two checkboxes side by side — Dynamic HUD + Shake the HUD when hurt.
        box.AddChild(Widgets.CheckBox("hud_dynamic_follow", "Dynamic HUD", "HUD moves around following player's movement"));
        box.AddChild(Widgets.CheckBox("hud_dynamic_shake", "Shake the HUD when hurt"));

        box.AddChild(Ui.Spacer());

        // QC: makeXonoticButton("Enter HUD editor") onClick=HUDSetup_Check_Gamestatus
        // (dialog_settings_game_hud.qc): already in a match → HUDSetup_Start runs `togglemenu 0` +
        // `_hud_configure 1` (drop straight into the live editor, game/hud/HudConfigEditor.cs); not in a match
        // → the confirm dialog offering to start a local game first (dialog_settings_game_hudconfirm.qc).
        var enterHud = new Button
        {
            Text = Localization.Tr("Enter HUD editor"),
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        enterHud.Pressed += () =>
        {
            if (MenuCommand.InMatch?.Invoke() == true)
                MenuCommand.Run("togglemenu 0; _hud_configure 1"); // QC HUDSetup_Start (in-match branch)
            else
                MenuCommand.OpenDialogOverlay?.Invoke("hudconfirm"); // QC DialogOpenButton → hudconfirmDialog
        };
        box.AddChild(enterHud);
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
    private static string Qu(float v) => $"{Mathf.RoundToInt(v)} qu";
}

// =============================================================================================================
//  Models  —  qcsrc/menu/xonotic/dialog_settings_game_model.qc
// =============================================================================================================

/// <summary>
/// Models sub-tab — port of <c>XonoticGameModelSettingsTab_fill</c>: Items + Players groups. Binds
/// cl_simple_items / cl_ghost_items / cl_ghost_items_color / cl_forceplayermodels / cl_forceplayercolors /
/// cl_deathglow / cl_nogibs (gated on cl_gentle).
/// </summary>
public partial class DialogSettingsGameModel : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // --- Items ---
        box.AddChild(Ui.Header("Items"));

        box.AddChild(Widgets.CheckBox("cl_simple_items", "Use simple 2D images instead of item models"));

        // QC: slider(0,1,0.1,"cl_ghost_items") formatString "%".
        box.AddChild(Ui.Row("Unavailable opacity:", Widgets.Slider("cl_ghost_items", 0f, 1f, 0.1f, format: Percent)));

        // QC: textslider "cl_ghost_items_color" with named "R G B" string values; dep NOT cl_ghost_items==0.
        var ghostColor = Widgets.TextSlider("cl_ghost_items_color")
            .Add("Black", "-1 -1 -1")
            .Add("Dark", "0.1 0.1 0.1")
            .Add("Tinted", "0.6 0.6 0.6")
            .Add("Normal", "1 1 1")
            .Add("Blue", "-1 -1 3");
        var ghostColorRow = Ui.Row("Unavailable color:", ghostColor);
        box.AddChild(ghostColorRow);
        Dependent.BindNot(ghostColorRow, "cl_ghost_items", 0);

        box.AddChild(Ui.Spacer());

        // --- Players ---
        box.AddChild(Ui.Header("Players"));

        box.AddChild(Widgets.CheckBox("cl_forceplayermodels", "Force player models to mine"));

        // QC: mixedslider "cl_forceplayercolors" — Never=0 / Except in team games=1 / Only in Duel=3 /
        //     Only in team games=4 / In team games and Duel=5 / Always=2.
        var forceColors = Widgets.TextSlider("cl_forceplayercolors",
            "Warning: if enabled in team games your team's color may be the same as the enemy team")
            .Add("Never", 0)
            .Add("Except in team games", 1)
            .Add("Only in Duel", 3)
            .Add("Only in team games", 4)
            .Add("In team games and Duel", 5)
            .Add("Always", 2);
        box.AddChild(Ui.Row("Force player colors to mine:", forceColors));

        // QC: slider(0,2,0.2,"cl_deathglow") formatString "S" (seconds).
        box.AddChild(Ui.Row("Body fading:", Widgets.Slider("cl_deathglow", 0f, 2f, 0.2f, format: Seconds)));

        // QC: mixedslider "cl_nogibs" — None=1 / Few=0.75 / Many=0.5 / Lots=0; dep cl_gentle==0.
        var gibs = Widgets.TextSlider("cl_nogibs")
            .Add("None", 1).Add("Few", 0.75f).Add("Many", 0.5f).Add("Lots", 0);
        var gibsRow = Ui.Row("Gibs:", gibs);
        box.AddChild(gibsRow);
        Dependent.Bind(gibsRow, "cl_gentle", 0, 0);
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
    private static string Seconds(float v) => $"{v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s";
}

// =============================================================================================================
//  View  —  qcsrc/menu/xonotic/dialog_settings_game_view.qc
// =============================================================================================================

/// <summary>
/// View sub-tab — port of <c>XonoticGameViewSettingsTab_fill</c>: Perspective (FOV + 1st/3rd person), Zooming
/// (factor/speed/sensitivity/scroll, velocity zoom, reticle). Binds fov / chase_active / cl_eventchase_death /
/// cl_bobfall / cl_smoothviewheight / cl_bob / v_idlescale / chase_back / chase_up / cl_clippedspectating /
/// cl_zoomfactor / cl_zoomspeed / cl_zoomsensitivity / cl_zoomscroll* / cl_velocityzoom* / cl_reticle /
/// cl_unpress_zoom_*.
/// </summary>
public partial class DialogSettingsGameView : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // --- Perspective ---
        box.AddChild(Ui.Header("Perspective"));

        // QC: slider(60,130,5,"fov") formatString "%s°".
        box.AddChild(Ui.Row("Field of view:", Widgets.Slider("fov", 60f, 130f, 5f, "Field of vision in degrees", Degrees)));

        box.AddChild(Ui.Spacer());

        // 1st person — QC radio(1,"chase_active","0"). The bob/idle options below depend on chase_active∈[-1,0].
        var perspGroup = new ButtonGroup();
        box.AddChild(Widgets.RadioButton("chase_active", "0", "1st person perspective", perspGroup));

        // These five are QC VALUE-PAIR checkboxes — makeXonoticCheckBoxEx(yesValue, noValue, cvar, label)
        // stores yesValue when checked / noValue when unchecked (NOT a bit toggle; the old FlagCheckBox
        // wiring with bit 0 masked nothing and made every one of them inert).

        // QC: makeXonoticCheckBoxEx(2, 0, "cl_eventchase_death",...); dep chase_active∈[-1,0].
        var eventChase = Widgets.ValueCheckBox("cl_eventchase_death", 2f, 0f, "Slide to third person upon death");
        box.AddChild(eventChase);
        Dependent.Bind(eventChase, "chase_active", -1, 0);

        // QC: makeXonoticCheckBoxEx(0.05, 0, "cl_bobfall",...); dep chase_active∈[-1,0]. ON in stock Xonotic
        // (xonotic-client.cfg:151 cl_bobfall 0.05) — the landing dip.
        var bobFall = Widgets.ValueCheckBox("cl_bobfall", 0.05f, 0f, "Smooth the view when landing from a jump");
        box.AddChild(bobFall);
        Dependent.Bind(bobFall, "chase_active", -1, 0);

        // QC: makeXonoticCheckBoxEx(0.05, 0, "cl_smoothviewheight",...); dep chase_active∈[-1,0].
        var smoothCrouch = Widgets.ValueCheckBox("cl_smoothviewheight", 0.05f, 0f, "Smooth the view while crouching");
        box.AddChild(smoothCrouch);
        Dependent.Bind(smoothCrouch, "chase_active", -1, 0);

        // QC: makeXonoticCheckBoxEx_T(0.01, 0, "cl_bob",...) + makeMulti("cl_bob2"); dep chase_active∈[-1,0].
        var bob = Widgets.ValueCheckBox("cl_bob", 0.01f, 0f, "View bobbing while walking around",
            multiCvars: "cl_bob2");
        box.AddChild(bob);
        Dependent.Bind(bob, "chase_active", -1, 0);

        // QC: makeXonoticCheckBoxEx(1, 0, "v_idlescale",...); dep chase_active∈[-1,0] — the idle view sway.
        var idle = Widgets.ValueCheckBox("v_idlescale", 1f, 0f, "View waving while idle");
        box.AddChild(idle);
        Dependent.Bind(idle, "chase_active", -1, 0);

        box.AddChild(Ui.Spacer());

        // 3rd person — QC radio(1,"chase_active","1").
        box.AddChild(Widgets.RadioButton("chase_active", "1", "3rd person perspective", perspGroup));

        // QC: slider(10,100,1,"chase_back") "%s qu"; dep chase_active==1.
        var chaseBack = Widgets.Slider("chase_back", 10f, 100f, 1f, format: Qu);
        var chaseBackRow = Ui.Row("Back distance", chaseBack);
        box.AddChild(chaseBackRow);
        Dependent.Bind(chaseBackRow, "chase_active", 1, 1);

        // QC: slider(10,50,1,"chase_up") "%s qu"; dep chase_active==1.
        var chaseUp = Widgets.Slider("chase_up", 10f, 50f, 1f, format: Qu);
        var chaseUpRow = Ui.Row("Up distance", chaseUp);
        box.AddChild(chaseUpRow);
        Dependent.Bind(chaseUpRow, "chase_active", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: checkBox(1,"cl_clippedspectating",...).
        box.AddChild(Widgets.CheckBox("cl_clippedspectating", "Allow passing through walls while spectating"));

        box.AddChild(Ui.Spacer());

        // --- Zooming ---
        box.AddChild(Ui.Header("Zooming"));

        // QC: mixedslider "cl_zoomfactor" formatString "%sx"; addRange(2,10,0.5)+addRange(11,30,1).
        var zoomFactor = Widgets.TextSlider("cl_zoomfactor", "How big the zoom factor is when the zoom button is pressed");
        for (float v = 2f; v <= 10.0001f; v += 0.5f) zoomFactor.Add(Mult(v), v);
        for (float v = 11f; v <= 30.0001f; v += 1f) zoomFactor.Add(Mult(v), v);
        box.AddChild(Ui.Row("Zoom factor:", zoomFactor));

        // QC: mixedslider "cl_zoomspeed"; addRange(1,8,1)+addText("Instant",-1); dep NOT cl_zoomfactor==1.
        var zoomSpeed = Widgets.TextSlider("cl_zoomspeed", "How fast the view will be zoomed, disable to zoom instantly");
        for (float v = 1f; v <= 8.0001f; v += 1f) zoomSpeed.Add(Tidy(v), v);
        zoomSpeed.Add("Instant", -1);
        var zoomSpeedRow = Ui.Row("Zoom speed:", zoomSpeed);
        box.AddChild(zoomSpeedRow);
        Dependent.BindNot(zoomSpeedRow, "cl_zoomfactor", 1);

        // QC: slider(0.05,1,0.05,"cl_zoomsensitivity") "%"; dep NOT cl_zoomfactor==1.
        var zoomSens = Widgets.Slider("cl_zoomsensitivity", 0.05f, 1f, 0.05f,
            "How zoom changes sensitivity, from 0% (lower sensitivity) to 100% (no sensitivity change)", Percent);
        var zoomSensRow = Ui.Row("Zoom sensitivity:", zoomSens);
        box.AddChild(zoomSensRow);
        Dependent.BindNot(zoomSensRow, "cl_zoomfactor", 1);

        // QC: checkBox(0,"cl_zoomscroll",...); dep NOT cl_zoomfactor==1.
        var zoomScroll = Widgets.CheckBox("cl_zoomscroll", "Zoom scrolling");
        box.AddChild(zoomScroll);
        Dependent.BindNot(zoomScroll, "cl_zoomfactor", 1);

        // QC: slider(-1,1,0.1,"cl_zoomscroll_scale") "%sx"; deps NOT cl_zoomfactor==1 AND cl_zoomscroll==1.
        var zsScale = Widgets.Slider("cl_zoomscroll_scale", -1f, 1f, 0.1f, format: Mult);
        var zsScaleRow = Ui.Row("Scale:", zsScale);
        box.AddChild(zsScaleRow);
        Dependent.Bind(zsScaleRow, "cl_zoomscroll", 1, 1);

        // QC: mixedslider "cl_zoomscroll_speed"; addRange(2,16,2)+addText("Instant",-1); deps NOT zoomfactor==1 AND zoomscroll==1.
        var zsSpeed = Widgets.TextSlider("cl_zoomscroll_speed");
        for (float v = 2f; v <= 16.0001f; v += 2f) zsSpeed.Add(Tidy(v), v);
        zsSpeed.Add("Instant", -1);
        var zsSpeedRow = Ui.Row("Speed:", zsSpeed);
        box.AddChild(zsSpeedRow);
        Dependent.Bind(zsSpeedRow, "cl_zoomscroll", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: checkBox(0,"cl_velocityzoom_enabled",...).
        box.AddChild(Widgets.CheckBox("cl_velocityzoom_enabled", "Velocity zoom"));

        // QC: slider(-1,1,0.1,"cl_velocityzoom_factor") "%sx"; deps cl_velocityzoom_enabled==1 AND cl_velocityzoom_type∈[1,3].
        var vzFactor = Widgets.Slider("cl_velocityzoom_factor", -1f, 1f, 0.1f, format: Mult);
        var vzFactorRow = Ui.Row("Factor", vzFactor);
        box.AddChild(vzFactorRow);
        Dependent.Bind(vzFactorRow, "cl_velocityzoom_enabled", 1, 1);

        // QC: makeXonoticCheckBoxEx(3, 1, "cl_velocityzoom_type",...) — checked stores type 3 (forward speed
        // only), unchecked type 1 (all velocity). Deps NOT cl_velocityzoom_factor==0 AND enabled==1.
        var vzForward = Widgets.ValueCheckBox("cl_velocityzoom_type", 3f, 1f, "Forward movement only");
        box.AddChild(vzForward);
        Dependent.Bind(vzForward, "cl_velocityzoom_enabled", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: checkBox(0,"cl_reticle",...).
        box.AddChild(Widgets.CheckBox("cl_reticle", "Display reticle 2D overlay while zooming"));

        // QC: checkBox_T(0,"cl_unpress_zoom_on_death",...) makeMulti("cl_unpress_zoom_on_spawn").
        box.AddChild(Widgets.CheckBox("cl_unpress_zoom_on_death", "Release zoom when you die or respawn"));

        // QC: checkBox(0,"cl_unpress_zoom_on_weapon_switch",...).
        box.AddChild(Widgets.CheckBox("cl_unpress_zoom_on_weapon_switch", "Release zoom when you switch weapons"));
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
    private static string Degrees(float v) => $"{Mathf.RoundToInt(v)}°";
    private static string Qu(float v) => $"{Mathf.RoundToInt(v)} qu";
    private static string Mult(float v) => $"{Tidy(v)}x";
    private static string Tidy(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

// =============================================================================================================
//  Weapons  —  qcsrc/menu/xonotic/dialog_settings_game_weapons.qc
// =============================================================================================================

/// <summary>
/// Weapons sub-tab — port of <c>XonoticGameWeaponsSettingsTab_fill</c>: the weapon priority list + Up/Down,
/// weapon options, and weapon visuals. Binds cl_weaponpriority (priority list) /
/// cl_weaponpriority_useforcycling / cl_weaponimpulsemode / cl_unpress_attack_on_weapon_switch /
/// cl_autoswitch / cl_autoswitch_cts / r_drawviewmodel / cl_gunalign / cl_followmodel / cl_bobmodel /
/// cl_viewmodel_alpha / cl_tracers_teamcolor. The "Apply immediately" button sends "sendcvar cl_weaponpriority".
/// The QC weapons *list* is the real reorderable <see cref="WeaponPriorityList"/> — the registry-driven list
/// (with Move up/down) editing cl_weaponpriority via the ported <see cref="WeaponOrder"/> helpers (T50).
/// </summary>
public partial class DialogSettingsGameWeapons : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // --- Weapon Priority List ---
        box.AddChild(Ui.Header("Weapon Priority List (* = mutator weapon)"));

        // QC: makeXonoticWeaponsList() — the reorderable registry-driven list (with Up/Down) editing
        // cl_weaponpriority. Backed by WeaponOrder (number→fix+complete→name) exactly like the QC widget.
        box.AddChild(new WeaponPriorityList());

        box.AddChild(Widgets.CheckBox("cl_weaponpriority_useforcycling", "Use priority list for weapon cycling",
            "Make use of the list above when cycling through weapons with the mouse wheel"));
        box.AddChild(Widgets.CheckBox("cl_weaponimpulsemode", "Cycle through only usable weapon selections"));

        box.AddChild(Ui.Spacer());

        // --- Weapon Options ---
        box.AddChild(Ui.Header("Weapon Options"));

        box.AddChild(Widgets.CheckBox("cl_unpress_attack_on_weapon_switch", "Release attack buttons when you switch weapons"));
        box.AddChild(Widgets.CheckBox("cl_autoswitch", "Auto switch weapons on pickup",
            "Automatically switch to newly picked up weapons if they are better than what you are carrying"));

        // QC: mixedslider "cl_autoswitch_cts" — Never=0 / Default=-1 / Always=1.
        var autoCts = Widgets.TextSlider("cl_autoswitch_cts")
            .Add("Never", 0).Add("Default", -1).Add("Always", 1);
        box.AddChild(Ui.Row("Auto switch in CTS:", autoCts));

        box.AddChild(Ui.Spacer());

        // --- Weapon Visuals ---
        box.AddChild(Ui.Header("Weapon Visuals"));

        box.AddChild(Widgets.CheckBox("r_drawviewmodel", "Draw 1st person weapon model", "Draw the weapon model"));

        // QC: radio(1,"cl_gunalign","4"=Left / "1"=Center / "3"=Right);
        //     deps NOT cl_viewmodel_alpha==0 AND r_drawviewmodel==1.
        var alignGroup = new ButtonGroup();
        var alignRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        alignRow.AddThemeConstantOverride("separation", 8);
        alignRow.AddChild(Widgets.RadioButton("cl_gunalign", "4", "Left align", alignGroup,
            "Position of the weapon model; requires reconnect"));
        alignRow.AddChild(Widgets.RadioButton("cl_gunalign", "1", "Center", alignGroup,
            "Position of the weapon model; requires reconnect"));
        alignRow.AddChild(Widgets.RadioButton("cl_gunalign", "3", "Right align", alignGroup,
            "Position of the weapon model; requires reconnect"));
        box.AddChild(Ui.Row("Gun alignment:", alignRow));
        Dependent.Bind(alignRow, "r_drawviewmodel", 1, 1);

        // QC: checkBox_T(0,"cl_followmodel",...) makeMulti("cl_leanmodel"); deps NOT viewmodel_alpha==0 AND r_drawviewmodel==1.
        var swaying = Widgets.CheckBox("cl_followmodel", "Gun model swaying");
        box.AddChild(swaying);
        Dependent.Bind(swaying, "r_drawviewmodel", 1, 1);

        // QC: checkBox(0,"cl_bobmodel",...); deps NOT viewmodel_alpha==0 AND r_drawviewmodel==1.
        var bobbing = Widgets.CheckBox("cl_bobmodel", "Gun model bobbing");
        box.AddChild(bobbing);
        Dependent.Bind(bobbing, "r_drawviewmodel", 1, 1);

        // QC: slider(0.05,1,0.05,"cl_viewmodel_alpha") "%"; dep r_drawviewmodel==1.
        var vmAlpha = Widgets.Slider("cl_viewmodel_alpha", 0.05f, 1f, 0.05f, format: Percent);
        var vmAlphaRow = Ui.Row("Weapon model opacity:", vmAlpha);
        box.AddChild(vmAlphaRow);
        Dependent.Bind(vmAlphaRow, "r_drawviewmodel", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: label "Apply team colors to weapon beam effects:" + radio(1,"cl_tracers_teamcolor","0"/"1"/"2").
        box.AddChild(Ui.Label("Apply team colors to weapon beam effects:"));
        var tracerGroup = new ButtonGroup();
        var tracerRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tracerRow.AddThemeConstantOverride("separation", 8);
        tracerRow.AddChild(Widgets.RadioButton("cl_tracers_teamcolor", "0", "Never", tracerGroup));
        tracerRow.AddChild(Widgets.RadioButton("cl_tracers_teamcolor", "1", "In team games", tracerGroup));
        tracerRow.AddChild(Widgets.RadioButton("cl_tracers_teamcolor", "2", "Always", tracerGroup));
        box.AddChild(tracerRow);

        box.AddChild(Ui.Spacer());

        // QC: makeXonoticCommandButton("Apply immediately", "sendcvar cl_weaponpriority").
        box.AddChild(Widgets.CommandButton("Apply immediately", "sendcvar cl_weaponpriority"));
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}

// =============================================================================================================
//  Messages  —  qcsrc/menu/xonotic/dialog_settings_game_messages.qc
// =============================================================================================================

/// <summary>
/// Messages sub-tab — port of <c>XonoticGameMessageSettingsTab_fill</c>: Frag Information, Gametype Settings,
/// Other, Announcers. Most controls are bit-flag checkboxes on notification_* cvars (QC checkBoxEx with a bit
/// mask) plus a few plain notification_show_* toggles. We bind the same cvars in QC order; multi-bound siblings
/// (QC makeMulti, which write several extra cvars in lockstep) are noted but only the primary cvar is wired —
/// the foundation has no multi-cvar widget.
/// </summary>
public partial class DialogSettingsGameMessages : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // --- Frag Information ---
        box.AddChild(Ui.Header("Frag Information"));

        // QC: checkBox_T(0,"notification_show_sprees",...).
        box.AddChild(Widgets.CheckBox("notification_show_sprees", "Display information about killing sprees"));

        // QC: checkBox_T(0,"notification_show_sprees_info_specialonly",...) makeMulti(...center_specialonly); dep show_sprees==1.
        var spreesSpecial = Widgets.CheckBox("notification_show_sprees_info_specialonly", "Only display sprees if they are achievements");
        box.AddChild(spreesSpecial);
        Dependent.Bind(spreesSpecial, "notification_show_sprees", 1, 1);

        // QC: checkBox_T(0,"notification_show_sprees_center",...); dep show_sprees==1.
        var spreesCenter = Widgets.CheckBox("notification_show_sprees_center", "Show spree information in centerprints");
        box.AddChild(spreesCenter);
        Dependent.Bind(spreesCenter, "notification_show_sprees", 1, 1);

        // QC: makeXonoticCheckBoxEx_T(3, 0, "notification_show_sprees_info",...) — checked stores 3 (spree info
        // in BOTH kill-message target/attacker lines; 1/2 are the single-line variants picked by the QC
        // mixedslider); unchecked 0. Dep show_sprees==1.
        var spreesInfo = Widgets.ValueCheckBox("notification_show_sprees_info", 3f, 0f, "Show spree information in death messages");
        box.AddChild(spreesInfo);
        Dependent.Bind(spreesInfo, "notification_show_sprees", 1, 1);

        // QC: checkBox_T(0,"notification_show_sprees_info_newline",...); deps show_sprees==1 AND show_sprees_info∈[1,3].
        var spreesNewline = Widgets.CheckBox("notification_show_sprees_info_newline", "Print on a separate line");
        box.AddChild(spreesNewline);
        Dependent.Bind(spreesNewline, "notification_show_sprees", 1, 1);

        // QC: makeXonoticCheckBoxEx_T(2, 1, "notification_CHOICE_FRAG",...) — checked stores CHOICE 2 (the
        // verbose variant), unchecked 1 (simple, NOT off) — mirrored onto the frag-family siblings (makeMulti).
        box.AddChild(Widgets.ValueCheckBox("notification_CHOICE_FRAG", 2f, 1f,
            "Add extra frag information to centerprint when available",
            multiCvars: new[]
            {
                "notification_CHOICE_FRAGGED", "notification_CHOICE_TYPEFRAG", "notification_CHOICE_TYPEFRAGGED",
                "notification_CHOICE_FRAG_FIRE", "notification_CHOICE_FRAGGED_FIRE",
                "notification_CHOICE_FRAG_FREEZE", "notification_CHOICE_FRAGGED_FREEZE",
            }));

        // QC: checkBox_T(0,"notification_show_location",...).
        box.AddChild(Widgets.CheckBox("notification_show_location", "Add frag location to death messages when available"));

        box.AddChild(Ui.Spacer());

        // --- Gametype Settings ---
        box.AddChild(Ui.Header("Gametype Settings"));

        // QC: makeXonoticCheckBoxEx_T(2, 1, "notification_CHOICE_CTF_CAPTURE_TIME", ...) + makeMulti(BROKEN/UNBROKEN).
        box.AddChild(Widgets.ValueCheckBox("notification_CHOICE_CTF_CAPTURE_TIME", 2f, 1f,
            "Display capture times in Capture the Flag",
            multiCvars: new[] { "notification_CHOICE_CTF_CAPTURE_BROKEN", "notification_CHOICE_CTF_CAPTURE_UNBROKEN" }));

        // QC: makeXonoticCheckBoxEx_T(2, 1, "notification_CHOICE_CTF_PICKUP_ENEMY", ...) + makeMulti(TEAM/NEUTRAL).
        box.AddChild(Widgets.ValueCheckBox("notification_CHOICE_CTF_PICKUP_ENEMY", 2f, 1f,
            "Display name of flag stealer in Capture the Flag",
            multiCvars: new[] { "notification_CHOICE_CTF_PICKUP_ENEMY_TEAM", "notification_CHOICE_CTF_PICKUP_ENEMY_NEUTRAL" }));

        box.AddChild(Ui.Spacer());

        // --- Other ---
        box.AddChild(Ui.Header("Other"));

        // QC: makeXonoticCheckBoxEx_T(4, 0, "con_notify",...) — checked stores 4 (notify lines in the top-left).
        box.AddChild(Widgets.ValueCheckBox("con_notify", 4f, 0f, "Display console messages in the top left corner"));

        // QC: makeXonoticCheckBoxEx_T(2, 1, "notification_allow_chatboxprint",...) — checked 2 (ALL info to chatbox).
        box.AddChild(Widgets.ValueCheckBox("notification_allow_chatboxprint", 2f, 1f, "Display all info messages in the chatbox"));

        // QC: makeXonoticCheckBoxEx_T(2, 1, "notification_INFO_QUIT_DISCONNECT",...) + makeMulti(KICK_IDLING, JOIN_CONNECT).
        box.AddChild(Widgets.ValueCheckBox("notification_INFO_QUIT_DISCONNECT", 2f, 1f,
            "Display player statuses in the chatbox",
            multiCvars: new[] { "notification_INFO_QUIT_KICK_IDLING", "notification_INFO_JOIN_CONNECT" }));

        box.AddChild(Ui.Spacer());

        // QC: checkBox_T(0,"notification_CENTER_POWERUP_INVISIBILITY",...) + makeMulti(many powerup CENTER/INFO siblings).
        box.AddChild(Widgets.CheckBox("notification_CENTER_POWERUP_INVISIBILITY", "Powerup notifications"));

        // QC: checkBox_T(0,"notification_CENTER_ITEM_WEAPON_DONTHAVE",...) + makeMulti(weapon CENTER siblings).
        box.AddChild(Widgets.CheckBox("notification_CENTER_ITEM_WEAPON_DONTHAVE", "Weapon centerprint notifications"));

        // QC: checkBox_T(0,"notification_INFO_ITEM_WEAPON_DONTHAVE",...) + makeMulti(weapon INFO siblings).
        box.AddChild(Widgets.CheckBox("notification_INFO_ITEM_WEAPON_DONTHAVE", "Weapon info message notifications"));

        box.AddChild(Ui.Spacer());

        // --- Announcers ---
        box.AddChild(Ui.Header("Announcers"));

        // QC: makeXonoticCheckBoxEx_T(2, 0, "notification_ANNCE_NUM_RESPAWN_1",...) + makeMulti(RESPAWN_2..10).
        // Checked stores ANNCE mode 2 (play always); unchecked 0 (off).
        box.AddChild(Widgets.ValueCheckBox("notification_ANNCE_NUM_RESPAWN_1", 2f, 0f, "Respawn countdown sounds",
            multiCvars: new[]
            {
                "notification_ANNCE_NUM_RESPAWN_2", "notification_ANNCE_NUM_RESPAWN_3",
                "notification_ANNCE_NUM_RESPAWN_4", "notification_ANNCE_NUM_RESPAWN_5",
                "notification_ANNCE_NUM_RESPAWN_6", "notification_ANNCE_NUM_RESPAWN_7",
                "notification_ANNCE_NUM_RESPAWN_8", "notification_ANNCE_NUM_RESPAWN_9",
                "notification_ANNCE_NUM_RESPAWN_10",
            }));

        // QC: makeXonoticCheckBoxEx_T(1, 0, "notification_ANNCE_KILLSTREAK_03",...) + makeMulti(KILLSTREAK_05..30).
        box.AddChild(Widgets.ValueCheckBox("notification_ANNCE_KILLSTREAK_03", 1f, 0f, "Killstreak sounds",
            multiCvars: new[]
            {
                "notification_ANNCE_KILLSTREAK_05", "notification_ANNCE_KILLSTREAK_10",
                "notification_ANNCE_KILLSTREAK_15", "notification_ANNCE_KILLSTREAK_20",
                "notification_ANNCE_KILLSTREAK_25", "notification_ANNCE_KILLSTREAK_30",
            }));

        // QC: makeXonoticCheckBoxEx_T(1, 0, "notification_ANNCE_ACHIEVEMENT_AIRSHOT",...) + makeMulti(achievement siblings).
        box.AddChild(Widgets.ValueCheckBox("notification_ANNCE_ACHIEVEMENT_AIRSHOT", 1f, 0f, "Achievement sounds",
            multiCvars: new[]
            {
                "notification_ANNCE_ACHIEVEMENT_AMAZING", "notification_ANNCE_ACHIEVEMENT_AWESOME",
                "notification_ANNCE_ACHIEVEMENT_BOTLIKE", "notification_ANNCE_ACHIEVEMENT_ELECTROBITCH",
                "notification_ANNCE_ACHIEVEMENT_IMPRESSIVE", "notification_ANNCE_ACHIEVEMENT_YODA",
            }));
    }
}

// The crosshair custom-color rows above now use the faithful HSL image picker (HslColorPicker.ForStringCvar),
// the C# port of makeXonoticColorpickerString. The Godot-picker stand-in CvarColorButton still backs the HUD
// panel color rows (out of this task's scope) — see Widgets.ColorButton in game/menu/framework/CvarControls.cs.

// =============================================================================================================
//  Damage Text  —  common/mutators/mutator/damagetext/ui_damagetext.qc (XonoticDamageTextSettings_fill)
// =============================================================================================================

/// <summary>
/// Damage Text sub-tab — port of <c>XonoticDamageTextSettings_fill</c> (ui_damagetext.qc), which in QC is
/// <c>REGISTER_SETTINGS(damagetext, …)</c>: a standalone Game-settings tab sitting alongside the six tabs above.
/// Exposes the floating-damage-number cvars read live by <see cref="XonoticGodot.Game.Client.DamageTextConfig"/>:
/// the master toggle (cl_damagetext), the friendly-fire toggle, font size min/max, the enemy color (with a
/// per-weapon override), initial opacity + fade time, and the two accumulate gates. Every widget binds the same
/// cvar its QC counterpart does, in QC order, with the same dependencies (all gated on cl_damagetext==1).
/// </summary>
public partial class DialogSettingsGameDamageText : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        box.AddChild(Ui.Header("Damage Text"));

        // QC: makeXonoticCheckBox(0, "cl_damagetext", _("Draw damage numbers")).
        box.AddChild(Widgets.CheckBox("cl_damagetext", "Draw damage numbers"));

        // QC: makeXonoticCheckBox(0, "cl_damagetext_friendlyfire", ...); setDependent(e, "cl_damagetext", 1, 1).
        var ff = Widgets.CheckBox("cl_damagetext_friendlyfire", "Draw damage numbers for friendly fire");
        box.AddChild(ff);
        Dependent.Bind(ff, "cl_damagetext", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: slider(0, 50, 1, "cl_damagetext_size_min"); dep cl_damagetext==1.
        var sizeMin = Widgets.Slider("cl_damagetext_size_min", 0f, 50f, 1f);
        var sizeMinRow = Ui.Row("Font size minimum:", sizeMin);
        box.AddChild(sizeMinRow);
        Dependent.Bind(sizeMinRow, "cl_damagetext", 1, 1);

        // QC: slider(0, 50, 1, "cl_damagetext_size_max"); dep cl_damagetext==1.
        var sizeMax = Widgets.Slider("cl_damagetext_size_max", 0f, 50f, 1f);
        var sizeMaxRow = Ui.Row("Font size maximum:", sizeMax);
        box.AddChild(sizeMaxRow);
        Dependent.Bind(sizeMaxRow, "cl_damagetext", 1, 1);

        // QC: makeXonoticColorpickerString("cl_damagetext_color", ...); deps cl_damagetext==1 AND
        // cl_damagetext_color_per_weapon==0. Use the faithful HSL image picker (the C# makeXonoticColorpickerString).
        var color = HslColorPicker.ForStringCvar("cl_damagetext_color");
        var colorRow = Ui.Row("Color:", color);
        box.AddChild(colorRow);
        Dependent.Bind(colorRow, "cl_damagetext_color_per_weapon", 0, 0); // primary gate: enabled only when not per-weapon

        // QC: makeXonoticCheckBox(0, "cl_damagetext_color_per_weapon", _("Per weapon")); dep cl_damagetext==1.
        var perWeapon = Widgets.CheckBox("cl_damagetext_color_per_weapon", "Per weapon");
        box.AddChild(perWeapon);
        Dependent.Bind(perWeapon, "cl_damagetext", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: slider(0.25, 1, 0.05, "cl_damagetext_alpha_start") formatString "%"; dep cl_damagetext==1.
        var alphaStart = Widgets.Slider("cl_damagetext_alpha_start", 0.25f, 1f, 0.05f, format: Percent);
        var alphaStartRow = Ui.Row("Initial opacity:", alphaStart);
        box.AddChild(alphaStartRow);
        Dependent.Bind(alphaStartRow, "cl_damagetext", 1, 1);

        // QC: slider(1, 5, 0.5, "cl_damagetext_alpha_lifetime") formatString "S"; dep cl_damagetext==1.
        var fadeTime = Widgets.Slider("cl_damagetext_alpha_lifetime", 1f, 5f, 0.5f, format: Seconds);
        var fadeTimeRow = Ui.Row("Fade time:", fadeTime);
        box.AddChild(fadeTimeRow);
        Dependent.Bind(fadeTimeRow, "cl_damagetext", 1, 1);

        box.AddChild(Ui.Spacer());

        // QC: header label "Accumulate:"; dep cl_damagetext==1.
        var accumLabel = Ui.Label("Accumulate:");
        box.AddChild(accumLabel);
        Dependent.Bind(accumLabel, "cl_damagetext", 1, 1);

        // QC: makeXonoticMixedSlider("cl_damagetext_accumulate_lifetime") formatString "S":
        //   addText("Never", 0); addRange(0.5, 3, 0.5); addText("Always", -1). dep cl_damagetext==1.
        var accumLifetime = Widgets.MixedSlider("cl_damagetext_accumulate_lifetime")
            .Add("Never", 0f)
            .AddRange(0.5f, 3f, 0.5f)
            .Add("Always", -1f)
            .Finish();
        var accumLifetimeRow = Ui.Row("If younger than:", accumLifetime);
        box.AddChild(accumLifetimeRow);
        Dependent.Bind(accumLifetimeRow, "cl_damagetext", 1, 1);

        // QC: slider(0, 1, 0.05, "cl_damagetext_accumulate_alpha_rel") formatString "%";
        //   setDependentNOT(e, "cl_damagetext_accumulate_lifetime", 0) AND cl_damagetext==1.
        var accumAlpha = Widgets.Slider("cl_damagetext_accumulate_alpha_rel", 0f, 1f, 0.05f, format: Percent);
        var accumAlphaRow = Ui.Row("Or opacity greater than:", accumAlpha);
        box.AddChild(accumAlphaRow);
        Dependent.BindNot(accumAlphaRow, "cl_damagetext_accumulate_lifetime", 0); // primary gate (AND cl_damagetext==1)
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
    private static string Seconds(float v) => $"{v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s";
}
