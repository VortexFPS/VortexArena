using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Game settings tab — <c>XonoticGameSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_game.qc) on its
/// 15.5 x 6.5 grid: the topic list (the Settings registry of menu/gamesettings.qh, in its order: View, HUD, Crosshair,
/// Weapons, Models, Messages, Damage text) in the first column, and the selected topic's own tab filling the rest.
/// Each topic is a <see cref="QcTab"/> ported TD for TD from its QC fill function.
/// </summary>
public partial class DialogSettingsGame : QcTab
{
    private static readonly (string Title, string Tooltip, Func<QcTab> Make)[] Topics =
    {
        ("View", "", () => new DialogSettingsGameView()),
        ("HUD", "", () => new DialogSettingsGameHud()),
        ("Crosshair", "", () => new DialogSettingsGameCrosshair()),
        ("Weapons", "", () => new DialogSettingsGameWeapons()),
        ("Models", "Customize how players and items are displayed in game", () => new DialogSettingsGameModel()),
        ("Messages", "", () => new DialogSettingsGameMessages()),
        ("Damage text", "", () => new DialogSettingsGameDamageText()),
    };

    private readonly QcTab?[] _made = new QcTab?[Topics.Length];
    private Control _panel = null!;
    private QcTopicList _list = null!;

    public DialogSettingsGame() : base(rows: 15.5f, columns: 6.5f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        _list = new QcTopicList(Array.ConvertAll(Topics, t => (Localization.Tr(t.Title), Localization.Tr(t.Tooltip))));
        _list.ItemSelected += _ => TopicChanged();

        GotoRC(0, 0);
            TD(Rows, 1, _list);
        GotoRC(0, 1); SetFirstColumn(CurrentColumn);
            _panel = TD(Rows, Columns - 1, new Control { MouseFilter = MouseFilterEnum.Pass });

        _list.SetSelected(0);
        TopicChanged();
    }

    /// <summary>Show the topic titled <paramref name="title"/> (case-insensitive), for dev/CI capture.</summary>
    public void SelectTopic(string title)
    {
        int i = Array.FindIndex(Topics, t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _list?.SetSelected(i);
    }

    // topicChangeNotify: the selected topic's tab replaces the current one in the panel.
    private void TopicChanged()
    {
        int sel = _list.SelectedItem;
        for (int i = 0; i < Topics.Length; i++)
        {
            if (i != sel)
            {
                if (_made[i] is { } other) other.Visible = false;
                continue;
            }
            if (_made[i] is null)
            {
                QcTab tab = Topics[i].Make();
                tab.SetAnchorsPreset(LayoutPreset.FullRect);
                _panel.AddChild(tab);
                _made[i] = tab;
            }
            _made[i]!.Visible = true;
        }
    }
}

/// <summary>XonoticRegisteredSettingsList: two text rows per topic, its title half a character in, in white.</summary>
public partial class QcTopicList : MenuListBox
{
    private readonly (string Title, string Tooltip)[] _topics;

    public QcTopicList((string Title, string Tooltip)[] topics)
    {
        _topics = topics;
        ItemHeight = 2f * MenuMetrics.FontNormalU; // rowsPerItem 2
        ItemCount = topics.Length;
        FocusedItemChanged += i => TooltipText = i >= 0 && i < _topics.Length ? _topics[i].Tooltip : "";
    }

    protected override bool DrawsBackground => false; // alphaBG 0

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _topics.Length) return;
        float ch = MenuMetrics.FontNormalU;
        string s = QcDraw.ShortenToWidth(MenuColorCodes.Strip(_topics[index].Title), rect.Size.X);
        // draw_Text(..., '1 1 1', SKINALPHA_TEXT)
        QcDraw.Text(this, rect.Position + new Vector2(0.5f * ch, 0.5f * (rect.Size.Y - ch)), s,
            MenuSkin.Fade(Colors.White, MenuSkin.Text.A));
    }
}

/// <summary>Shared pieces of the Game topics.</summary>
internal static class GameTopic
{
    /// <summary>A picker or preview widget at its grid cell: no minimum size of its own (a QC item is its cell).</summary>
    public static T Cell<T>(T c) where T : Control
    {
        c.CustomMinimumSize = Vector2.Zero;
        return c;
    }
}

// =============================================================================================================
//  View — dialog_settings_game_view.qc
// =============================================================================================================

public partial class DialogSettingsGameView : QcTab
{
    public DialogSettingsGameView() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var chase = new QcRadioGroup();
        TR();
            TD(1, 3, Qc.Header("Perspective"));
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Field of view:"));
            TD(1, 1.8f, Qc.Slider(60, 130, 5, "fov", "Field of vision in degrees", "%s°"));
        TR();
        TR();
            TD(1, 3, Qc.Radio(chase, "chase_active", "0", "1st person perspective"));
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(2, 0, "cl_eventchase_death", "Slide to third person upon death")).Dep("chase_active", -1, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(0.05f, 0, "cl_bobfall", "Smooth the view when landing from a jump")).Dep("chase_active", -1, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(0.05f, 0, "cl_smoothviewheight", "Smooth the view while crouching")).Dep("chase_active", -1, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(0.01f, 0, "cl_bob", "View bobbing while walking around"))
                .Multi("cl_bob", "cl_bob2").Dep("chase_active", -1, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(1, 0, "v_idlescale", "View waving while idle")).Dep("chase_active", -1, 0);
        TR();
        TR();
            TD(1, 3, Qc.Radio(chase, "chase_active", "1", "3rd person perspective"));
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Back distance")).Dep("chase_active", 1, 1);
            TD(1, 1.8f, Qc.Slider(10, 100, 1, "chase_back", "", "%s qu")).Dep("chase_active", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Up distance")).Dep("chase_active", 1, 1);
            TD(1, 1.8f, Qc.Slider(10, 50, 1, "chase_up", "", "%s qu")).Dep("chase_active", 1, 1);
        TR();
        TR();
            TD(1, 3, Qc.Check(1, "cl_clippedspectating", "Allow passing through walls while spectating"));

        GotoRC(0, 3); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Zooming"));
        TR();
            TD(1, 1, Qc.Label("Zoom factor:"));
            TD(1, 2, Qc.Mixed("cl_zoomfactor", "How big the zoom factor is when the zoom button is pressed", "%sx")
                .AddRange(2, 10, 0.5f).AddRange(11, 30, 1).Done());
        TR();
            TD(1, 1, Qc.Label("Zoom speed:")).DepNot("cl_zoomfactor", 1);
            TD(1, 2, Qc.Mixed("cl_zoomspeed", "How fast the view will be zoomed, disable to zoom instantly")
                .AddRange(1, 8, 1).Add("Instant", -1).Done()).DepNot("cl_zoomfactor", 1);
        TR();
            TD(1, 1, Qc.Label("Zoom sensitivity:")).DepNot("cl_zoomfactor", 1);
            TD(1, 2, Qc.Slider(0.05f, 1, 0.05f, "cl_zoomsensitivity",
                "How zoom changes sensitivity, from 0% (lower sensitivity) to 100% (no sensitivity change)", "%"))
                .DepNot("cl_zoomfactor", 1);
        TR();
            TD(1, 3, Qc.Check(0, "cl_zoomscroll", "Zoom scrolling")).DepNot("cl_zoomfactor", 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Scale:")).DepNot("cl_zoomfactor", 1).DepAnd("cl_zoomscroll", 1, 1);
            TD(1, 2, Qc.Slider(-1, 1, 0.1f, "cl_zoomscroll_scale", "", "%sx")).DepNot("cl_zoomfactor", 1).DepAnd("cl_zoomscroll", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Speed:")).DepNot("cl_zoomfactor", 1).DepAnd("cl_zoomscroll", 1, 1);
            TD(1, 2, Qc.Mixed("cl_zoomscroll_speed").AddRange(2, 16, 2).Add("Instant", -1).Done())
                .DepNot("cl_zoomfactor", 1).DepAnd("cl_zoomscroll", 1, 1);
        TR();
        TR();
            TD(1, 3, Qc.Check(0, "cl_velocityzoom_enabled", "Velocity zoom"));
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Factor")).Dep("cl_velocityzoom_enabled", 1, 1).DepAnd("cl_velocityzoom_type", 1, 3);
            TD(1, 2, Qc.Slider(-1, 1, 0.1f, "cl_velocityzoom_factor", "", "%sx"))
                .Dep("cl_velocityzoom_enabled", 1, 1).DepAnd("cl_velocityzoom_type", 1, 3);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(3, 1, "cl_velocityzoom_type", "Forward movement only"))
                .DepNot("cl_velocityzoom_factor", 0).DepAnd("cl_velocityzoom_enabled", 1, 1);
        TR();
        TR();
            TD(1, 3, Qc.Check(0, "cl_reticle", "Display reticle 2D overlay while zooming"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_unpress_zoom_on_death", "Release zoom when you die or respawn"))
                .Multi("cl_unpress_zoom_on_death", "cl_unpress_zoom_on_spawn");
        TR();
            TD(1, 3, Qc.Check(0, "cl_unpress_zoom_on_weapon_switch", "Release zoom when you switch weapons"));
    }
}

// =============================================================================================================
//  HUD — dialog_settings_game_hud.qc
// =============================================================================================================

public partial class DialogSettingsGameHud : QcTab
{
    public DialogSettingsGameHud() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var c = MenuState.Cvars;
        TR();
            TD(1, 3, Qc.Header("Scoreboard"));
        TR();
            TD(1, 1, Qc.Label("Opacity:"));
            TD(1, 2, Qc.Slider(0.1f, 1, 0.1f, "hud_panel_scoreboard_bg_alpha", "", "%"));
        TR();
            TD(1, 1, Qc.Label("Fading speed:"));
            // makeXonoticScoreboardFadeTimeSlider: the fade-in and fade-out speeds as one choice.
            TD(1, 2, QcLook.Slider(new QcTextSlider(
                    () => c.GetString("hud_panel_scoreboard_fadeinspeed") + " " + c.GetString("hud_panel_scoreboard_fadeoutspeed"),
                    id =>
                    {
                        string[] p = id.Split(' ');
                        c.Set("hud_panel_scoreboard_fadeinspeed", p[0]);
                        c.Set("hud_panel_scoreboard_fadeoutspeed", p[1]);
                        c.MarkArchived("hud_panel_scoreboard_fadeinspeed");
                        c.MarkArchived("hud_panel_scoreboard_fadeoutspeed");
                    })
                .Add(Localization.Tr("Slow"), "5 2.5").Add(Localization.Tr("Normal"), "10 5")
                .Add(Localization.Tr("Fast"), "15 7.5").Add(Localization.Tr("Instant"), "0 0")));
        TR();
            TD(1, 3, Qc.Check(0, "hud_panel_scoreboard_table_highlight", "Enable rows / columns highlighting"));
        TR();
            TD(1, 3, Qc.Check(0, "hud_panel_scoreboard_accuracy", "Show accuracy underneath scoreboard"));
        TR();
            TD(1, 1.2f, Qc.Label("Show team sizes:"));
            TD(1, 1.8f, Qc.Mixed("hud_panel_scoreboard_team_size_position",
                    "Team size position: Off=do not show; Left=on the left side of the scoreboard and move team scores to the right; Right=on the right of the scoreboard")
                .Add("Left", 1).Add("Off", 0).Add("Right", 2).Done(10f / 9f));

        TR();
        TR();
            TD(1, 3, Qc.Header("Waypoints"));
        TR();
            TD(1, 3, Qc.Check(1, "cl_hidewaypoints", "Display waypoint markers for objectives on the map",
                "Show various gametype specific waypoints"));
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Opacity:")).Dep("cl_hidewaypoints", 0, 0);
            TD(1, 2, Qc.Slider(0.1f, 1, 0.1f, "g_waypointsprite_alpha", "", "%")).Dep("cl_hidewaypoints", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Font size:")).DepNot("g_waypointsprite_alpha", 0).DepAnd("cl_hidewaypoints", 0, 0);
            TD(1, 2, Qc.Slider(5, 16, 1, "g_waypointsprite_fontsize")).DepNot("g_waypointsprite_alpha", 0).DepAnd("cl_hidewaypoints", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Edge offset:")).DepNot("g_waypointsprite_alpha", 0).DepAnd("cl_hidewaypoints", 0, 0);
            TD(1, 2, Qc.Slider(0, 0.3f, 0.01f, "g_waypointsprite_edgeoffset_bottom"))
                .Multi("g_waypointsprite_edgeoffset_bottom", "g_waypointsprite_edgeoffset_top g_waypointsprite_edgeoffset_left g_waypointsprite_edgeoffset_right")
                .DepNot("g_waypointsprite_alpha", 0).DepAnd("cl_hidewaypoints", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(0.25f, 1, "g_waypointsprite_crosshairfadealpha", "Fade when near the crosshair"))
                .DepNot("g_waypointsprite_alpha", 0).DepAnd("cl_hidewaypoints", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "g_waypointsprite_text", "Display names instead of icons"))
                .DepNot("g_waypointsprite_alpha", 0).DepAnd("cl_hidewaypoints", 0, 0);

        GotoRC(0, 3); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Player Names"));
        TR();
            TD(1, 3, Qc.Check(0, "hud_shownames", "Show names above players"));
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Opacity:")).Dep("hud_shownames", 1, 1);
            TD(1, 2, Qc.Slider(0.1f, 1, 0.1f, "hud_shownames_alpha", "", "%")).Dep("hud_shownames", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Font size:")).DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
            TD(1, 2, Qc.Slider(5, 16, 1, "hud_shownames_fontsize")).DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Max distance:")).DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
            TD(1, 2, Qc.Slider(2000, 10000, 500, "hud_shownames_maxdistance", "", "%s qu"))
                .DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Decolorize:")).DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
            TD(1, 2, Qc.Mixed("hud_shownames_decolorize").Add("Never", 0).Add("Teamplay", 1).Add("Always", 2).Done())
                .DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(25, 0, "hud_shownames_crosshairdistance", "Only when near crosshair"))
                .DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "hud_shownames_status", "Display health and armor"))
                .DepNot("hud_shownames_alpha", 0).DepAnd("hud_shownames", 1, 1);

        TR();
        TR();
            TD(1, 3, Qc.Header("Other"));
        TR();
            TD(1, 1, Qc.Label("Speed unit:"));
            TD(1, 2, Qc.Mixed("hud_speed_unit")
                .Add("qu/s", 1).Add("m/s", 2).Add("km/h", 3).Add("mph", 4).Add("knots", 5).Done());
        TR();
            TD(1, 1, Qc.Label("Damage overlay:"));
            TD(1, 2, Qc.Mixed("hud_damage", "", "%").Add("Disable", 0).AddRange(0.05f, 1, 0.05f).Done());
        TR();
            TD(1, 1.2f, Qc.Check(0, "hud_dynamic_follow", "Dynamic HUD", "HUD moves around following player's movement"));
            TD(1, 1.8f, Qc.Check(0, "hud_dynamic_shake", "Shake the HUD when hurt"));

        GotoRC(Rows - 1, 0); SetFirstColumn(CurrentColumn);
            // HUDSetup_Check_Gamestatus: in a match, straight into the HUD editor; otherwise the dialog offering to
            // start a local game for it (dialog_settings_game_hudconfirm.qc).
            TD(1, Columns, Qc.Button("Enter HUD editor", () =>
            {
                if (MenuCommand.InMatch?.Invoke() == true)
                    MenuCommand.Run("togglemenu 0; _hud_configure 1");
                else
                    MenuCommand.OpenDialogOverlay?.Invoke("hudconfirm");
            }));
    }
}

// =============================================================================================================
//  Crosshair — dialog_settings_game_crosshair.qc
// =============================================================================================================

public partial class DialogSettingsGameCrosshair : QcTab
{
    public DialogSettingsGameCrosshair() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var mode = new QcRadioGroup();
        TR();
            TD(1, 3, Qc.Header("Crosshair"));
        // crosshair_enabled: 0 = no crosshair options, 1 = no crosshair selection, but everything else enabled,
        // 2 = all crosshair options enabled
        TR();
            TD(1, 1, Qc.Radio(mode, "crosshair_enabled", "0", "No crosshair"));
            TD(1, 1, Qc.Radio(mode, "crosshair_per_weapon", null, "Per weapon",
                    "Set a different crosshair for each weapon, good if you play without weapon models"))
                .Multi("crosshair_per_weapon", "crosshair_enabled");
            TD(1, 1, Qc.Radio(mode, "crosshair_enabled", "2", "Custom"));
        TR();
            TDempty(0.2f);
            TD(3, 2, GameTopic.Cell(new CrosshairPicker()))
                .DepNot("crosshair_alpha", 0).DepAnd("crosshair_per_weapon", 0, 0).DepAnd("crosshair_enabled", 1, 2);
            TD(3, 0.8f, GameTopic.Cell(new CrosshairPreview()))
                .DepNot("crosshair_alpha", 0).DepAnd("crosshair_per_weapon", 0, 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
        TR();
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Size:")).DepNot("crosshair_alpha", 0).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.8f, Qc.Slider(0.1f, 1.0f, 0.01f, "crosshair_size")).DepNot("crosshair_alpha", 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Opacity:")).Dep("crosshair_enabled", 1, 2);
            TD(1, 1.8f, Qc.Slider(0.1f, 1, 0.1f, "crosshair_alpha", "", "%")).Dep("crosshair_enabled", 1, 2);
        var color = new QcRadioGroup();
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Color:")).DepNot("crosshair_alpha", 0).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.8f / 2, Qc.Radio(color, "crosshair_color_special", "1", "Per weapon"))
                .DepNot("crosshair_alpha", 0).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.8f / 2, Qc.Radio(color, "crosshair_color_special", "2", "By health"))
                .DepNot("crosshair_alpha", 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Radio(color, "crosshair_color_special", "0", "Custom"))
                .DepNot("crosshair_alpha", 0).DepAnd("crosshair_enabled", 1, 2);
            TD(2, 2, GameTopic.Cell(HslColorPicker.ForStringCvar("crosshair_color")))
                .DepNot("crosshair_alpha", 0).DepAnd("crosshair_color_special", 0, 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
        TR();
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "crosshair_ring", "Use rings to indicate weapon status"))
                .Multi("crosshair_ring", "crosshair_ring_reload").Dep("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.4f);
            TD(1, 1, Qc.Label("Size:"))
                .DepNot("crosshair_ring_alpha", 0).DepAnd("crosshair_ring", 1, 1).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.6f, Qc.Slider(1.5f, 4, 0.25f, "crosshair_ring_size"))
                .DepNot("crosshair_ring_alpha", 0).DepAnd("crosshair_ring", 1, 1).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.4f);
            TD(1, 1, Qc.Label("Opacity:")).Dep("crosshair_ring", 1, 1).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.6f, Qc.Slider(0.1f, 1, 0.1f, "crosshair_ring_alpha", "", "%")).Dep("crosshair_ring", 1, 1).DepAnd("crosshair_enabled", 1, 2);

        GotoRC(0, 3.2f); SetFirstColumn(CurrentColumn);
            TD(1, 2.8f, Qc.Check(0, "crosshair_dot", "Enable center crosshair dot")).Dep("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Size:")).DepNot("crosshair_dot_alpha", 0).DepAnd("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.8f, Qc.Slider(0.2f, 2, 0.1f, "crosshair_dot_size"))
                .DepNot("crosshair_dot_alpha", 0).DepAnd("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Opacity:")).Dep("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.8f, Qc.Slider(0.1f, 1, 0.1f, "crosshair_dot_alpha", "", "%")).Dep("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
        var dotColor = new QcRadioGroup();
        TR();
            TDempty(0.2f);
            TD(1, 1, Qc.Label("Color:")).DepNot("crosshair_dot_alpha", 0).DepAnd("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
            TD(1, 1.8f, Qc.Radio(dotColor, "crosshair_dot_color_custom", "0", "Use normal color"))
                .DepNot("crosshair_dot_alpha", 0).DepAnd("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.4f);
            TD(1, 0.8f, Qc.Radio(dotColor, "crosshair_dot_color_custom", "1", "Custom")).Dep("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2);
            TD(2, 1.8f, GameTopic.Cell(HslColorPicker.ForStringCvar("crosshair_dot_color")))
                .Dep("crosshair_dot", 1, 1).DepAnd("crosshair_enabled", 1, 2).DepAnd("crosshair_dot_color_custom", 1, 1);
        TR();
        TR();
        TR();
            TD(1, 3, Qc.Check(0, "crosshair_effect_scalefade", "Smooth effects of crosshairs")).Dep("crosshair_enabled", 1, 2);
        TR();
            TD(1, 3, Qc.Check(0, "crosshair_hittest", "Perform hit tests for the crosshair")).Dep("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "crosshair_hittest_blur_wall", "Blur if obstructed by an obstacle"))
                .DepNot("crosshair_hittest", 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "crosshair_hittest_blur_teammate", "Blur if obstructed by a teammate"))
                .DepNot("crosshair_hittest", 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(1.25f, 1, "crosshair_hittest", "Shrink if obstructed by a teammate"))
                .DepNot("crosshair_hittest", 0).DepAnd("crosshair_enabled", 1, 2);
        TR();
            TD(1, 3, Qc.CheckEx(0.5f, 0, "crosshair_hitindication", "Animate crosshair when hitting an enemy")).Dep("crosshair_enabled", 1, 2);
        TR();
            TD(1, 3, Qc.CheckEx(0.25f, 0, "crosshair_pickup", "Animate crosshair when picking up an item")).Dep("crosshair_enabled", 1, 2);
    }
}

// =============================================================================================================
//  Weapons — dialog_settings_game_weapons.qc
// =============================================================================================================

public partial class DialogSettingsGameWeapons : QcTab
{
    public DialogSettingsGameWeapons() : base(rows: 15.5f, columns: 6f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var apply = Qc.Command("Apply immediately", "sendcvar cl_weaponpriority");
        QcApplyOnChange.Bind(apply, "cl_weaponpriority");

        TR();
            TD(1, 3, Qc.Header("Weapon Priority List (* = mutator weapon)"));
        TR();
            TDempty(0.2f);
            var list = TD(9, 2.6f, new QcWeaponsList());
        GotoRC(10, 0.2f);
            TD(1, 2.6f / 2, Qc.Button("Up", list.MoveUp));
            TD(1, 2.6f / 2, Qc.Button("Down", list.MoveDown));
        TR();
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "cl_weaponpriority_useforcycling", "Use priority list for weapon cycling",
                "Make use of the list above when cycling through weapons with the mouse wheel"));
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(1, "cl_weaponimpulsemode", "Cycle through only usable weapon selections"));

        GotoRC(0, 3); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Weapon Options"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_unpress_attack_on_weapon_switch", "Release attack buttons when you switch weapons"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_autoswitch", "Auto switch weapons on pickup",
                "Automatically switch to newly picked up weapons if they are better than what you are carrying"));
        TR();
            TD(1, 4f / 3, Qc.Label("Auto switch in CTS:"));
            TD(1, 5f / 3, Qc.Mixed("cl_autoswitch_cts").Add("Never", 0).Add("Default", -1).Add("Always", 1).Done(6f / 5f));

        TR();
        TR();
            TD(1, 3, Qc.Header("Weapon Visuals"));
        TR();
            TD(1, 3, Qc.Check(0, "r_drawviewmodel", "Draw 1st person weapon model", "Draw the weapon model"));
        var align = new QcRadioGroup();
        TR();
            TDempty(0.2f);
            foreach ((string value, string text) in new[] { ("4", "Left align"), ("1", "Center"), ("3", "Right align") })
                TD(1, 2.8f / 3, Qc.Radio(align, "cl_gunalign", value, text, "Position of the weapon model; requires reconnect"))
                    .DepNot("cl_viewmodel_alpha", 0).DepAnd("r_drawviewmodel", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f / 2, Qc.Check(0, "cl_followmodel", "Gun model swaying"))
                .Multi("cl_followmodel", "cl_leanmodel").DepNot("cl_viewmodel_alpha", 0).DepAnd("r_drawviewmodel", 1, 1);
            TD(1, 2.8f / 2, Qc.Check(0, "cl_bobmodel", "Gun model bobbing")).DepNot("cl_viewmodel_alpha", 0).DepAnd("r_drawviewmodel", 1, 1);
        TR();
            TD(1, 4f / 3, Qc.Label("Weapon model opacity:")).Dep("r_drawviewmodel", 1, 1);
            TD(1, 5f / 3, Qc.Slider(0.05f, 1, 0.05f, "cl_viewmodel_alpha", "", "%", QcLook.DefaultValueSpace * 6f / 5f))
                .Dep("r_drawviewmodel", 1, 1);

        TR();
        TR();
            TD(1, 3, Qc.Label("Apply team colors to weapon beam effects:"));
        var tracers = new QcRadioGroup();
        TR();
            TDempty(0.2f);
            TD(1, 2.8f / 3, Qc.Radio(tracers, "cl_tracers_teamcolor", "0", "Never"));
            TD(1, 2.8f / 3, Qc.Radio(tracers, "cl_tracers_teamcolor", "1", "In team games"));
            TD(1, 2.8f / 3, Qc.Radio(tracers, "cl_tracers_teamcolor", "2", "Always"));

        GotoRC(Rows - 1, 0); SetFirstColumn(CurrentColumn);
            TD(1, Columns, apply);
    }
}

/// <summary>
/// xonotic/weaponslist.qc: <c>cl_weaponpriority</c> as a list — each weapon's HUD icon (at the first weapon's icon
/// aspect) a quarter character in, its name after it (condensed to fit) and a "*" in the header colour for a weapon
/// only a mutator gives. Up / Down (or Ctrl+arrows, +/-) and dragging a row reorder it.
/// </summary>
public partial class QcWeaponsList : MenuListBox
{
    private const string Cvar = "cl_weaponpriority";
    private readonly List<int> _ids = new();
    private bool _updating;
    private int _dragFrom = -1;

    public QcWeaponsList()
    {
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
        Rebuild();
    }

    public override void _EnterTree() => MenuState.Cvars.Changed += OnCvarChanged;
    public override void _ExitTree() => MenuState.Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (name == Cvar && !_updating) Rebuild();
    }

    // XonoticWeaponsList_draw: number the cvar, fix and complete it, write the names back if that changed it.
    private void Rebuild()
    {
        var c = MenuState.Cvars;
        string s = Common.Gameplay.WeaponOrder.NumberWeaponOrder(c.GetString(Cvar));
        string t = Common.Gameplay.WeaponOrder.FixWeaponOrder(s, complete: true, menuRegistryView: true);
        if (t != s)
        {
            _updating = true;
            c.Set(Cvar, Common.Gameplay.WeaponOrder.NameWeaponOrder(t));
            c.MarkArchived(Cvar);
            _updating = false;
        }
        _ids.Clear();
        foreach (string tok in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok, out int id) && id >= 0 && id < Common.Gameplay.Weapons.Count)
                _ids.Add(id);
        ItemCount = _ids.Count;
        QueueRedraw();
    }

    private void Swap(int a, int b)
    {
        string swapped = Common.Gameplay.WeaponOrder.SwapInPriorityList(string.Join(' ', _ids), a, b);
        MenuState.Cvars.Set(Cvar, Common.Gameplay.WeaponOrder.NameWeaponOrder(swapped));
        MenuState.Cvars.MarkArchived(Cvar);
    }

    public void MoveUp()
    {
        int i = SelectedItem;
        if (i <= 0) return;
        Swap(i - 1, i);
        SetSelected(i - 1);
    }

    public void MoveDown()
    {
        int i = SelectedItem;
        if (i < 0 || i >= ItemCount - 1) return;
        Swap(i, i + 1);
        SetSelected(i + 1);
    }

    protected override bool HandleKey(InputEventKey key)
    {
        if (key.CtrlPressed && key.Keycode is Key.Up or Key.Kp8 || key.Unicode == '+') { MoveUp(); return true; }
        if (key.CtrlPressed && key.Keycode is Key.Down or Key.Kp2 || key.Unicode == '-') { MoveDown(); return true; }
        return base.HandleKey(key);
    }

    // mouseDrag: dragging the selected row (not the scrollbar) moves the weapon with it.
    public override void _GuiInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            _dragFrom = mb.Pressed && mb.Position.X < ContentWidth ? SelectedItem : -1;
        int before = SelectedItem;
        base._GuiInput(ev);
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            _dragFrom = SelectedItem;
        if (ev is InputEventMouseMotion && _dragFrom >= 0 && MousePressed && SelectedItem != before && before >= 0)
            Swap(SelectedItem, before);
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _ids.Count) return;
        Common.Gameplay.Weapon w = Common.Gameplay.Weapons.ById(_ids[index]);
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        Texture2D? first = null;
        foreach (Common.Gameplay.Weapon f in Common.Gameplay.Weapons.All)
            if (f.NetName.Length > 0) { first = Hud.WeaponHud.Icon(f); break; }
        float aspect = first is not null && first.GetHeight() > 0 ? (float)first.GetWidth() / first.GetHeight() : 1f;
        float iconOrigin = 0.25f * ch, iconW = h * aspect, nameOrigin = iconOrigin + iconW + 0.25f * ch;
        if (Hud.WeaponHud.Icon(w) is { } icon)
            DrawTextureRect(icon, new Rect2(rect.Position + new Vector2(iconOrigin, 0f), new Vector2(iconW, h)), false,
                MenuSkin.Fade(Colors.White, MenuSkin.SelectionAlpha));

        string name = w.DisplayName.Length > 0 ? w.DisplayName : w.NetName;
        bool star = (w.SpawnFlags & Common.Gameplay.WeaponFlags.MutatorBlocked) != 0;
        float full = QcDraw.TextWidth(name + (star ? "*" : "")), f2 = full > rect.Size.X ? rect.Size.X / full : 1f;
        float y = rect.Position.Y + 0.5f * (h - ch);
        QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin, y), name, MenuSkin.Text, condense: f2);
        if (star)
            QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin + QcDraw.TextWidth(name) * f2, y), "*", MenuSkin.Header, condense: f2);
    }
}

// =============================================================================================================
//  Models — dialog_settings_game_model.qc
// =============================================================================================================

public partial class DialogSettingsGameModel : QcTab
{
    public DialogSettingsGameModel() : base(rows: 15.5f, columns: 5f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var c = MenuState.Cvars;
        GotoRC(0, 1); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Items"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_simple_items", "Use simple 2D images instead of item models"));
        TR();
            TD(1, 1, Qc.Label("Unavailable opacity:"));
            TD(1, 2, Qc.Slider(0, 1, 0.1f, "cl_ghost_items", "", "%"));
        TR();
            TD(1, 1, Qc.Label("Unavailable color:")).DepNot("cl_ghost_items", 0);
            TD(1, 2, QcLook.Slider(new QcTextSlider(() => c.GetString("cl_ghost_items_color"), id =>
                    {
                        c.Set("cl_ghost_items_color", id);
                        c.MarkArchived("cl_ghost_items_color");
                    })
                .Add(Localization.Tr("Black"), "-1 -1 -1").Add(Localization.Tr("Dark"), "0.1 0.1 0.1")
                .Add(Localization.Tr("Tinted"), "0.6 0.6 0.6").Add(Localization.Tr("Normal"), "1 1 1")
                .Add(Localization.Tr("Blue"), "-1 -1 3"))).DepNot("cl_ghost_items", 0);

        TR();
        TR();
            TD(1, 3, Qc.Header("Players"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_forceplayermodels", "Force player models to mine"));
        TR();
            TD(1, 4f / 3, Qc.Label("Force player colors to mine:"));
            TD(1, 5f / 3, Qc.Mixed("cl_forceplayercolors",
                    "Warning: if enabled in team games your team's color may be the same as the enemy team")
                .Add("Never", 0).Add("Except in team games", 1).Add("Only in Duel", 3).Add("Only in team games", 4)
                .Add("In team games and Duel", 5).Add("Always", 2).Done(2f));
        TR();
            TD(1, 1, Qc.Label("Body fading:"));
            TD(1, 2, Qc.Slider(0, 2, 0.2f, "cl_deathglow", "", "S"));
        TR();
            TD(1, 1, Qc.Label("Gibs:")).Dep("cl_gentle", 0, 0);
            TD(1, 2, Qc.Mixed("cl_nogibs").Add("None", 1).Add("Few", 0.75f).Add("Many", 0.5f).Add("Lots", 0).Done())
                .Dep("cl_gentle", 0, 0);
    }
}

// =============================================================================================================
//  Messages — dialog_settings_game_messages.qc
// =============================================================================================================

public partial class DialogSettingsGameMessages : QcTab
{
    public DialogSettingsGameMessages() : base(rows: 15.5f, columns: 6f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        TR();
            TD(1, 3.1f, Qc.Header("Frag Information"));
        TR();
            TD(1, 3.1f, Qc.Check(0, "notification_show_sprees", "Display information about killing sprees"));
        TR();
            TDempty(0.2f);
            TD(1, 2.9f, Qc.Check(0, "notification_show_sprees_info_specialonly", "Only display sprees if they are achievements"))
                .Multi("notification_show_sprees_info_specialonly", "notification_show_sprees_center_specialonly")
                .Dep("notification_show_sprees", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 2.9f, Qc.Check(0, "notification_show_sprees_center", "Show spree information in centerprints"))
                .Dep("notification_show_sprees", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 2.9f, Qc.CheckEx(3, 0, "notification_show_sprees_info", "Show spree information in death messages"))
                .Dep("notification_show_sprees", 1, 1);
        TR();
            TDempty(0.4f);
            TD(1, 2.7f, Qc.Check(0, "notification_show_sprees_info_newline", "Print on a separate line"))
                .Dep("notification_show_sprees", 1, 1).DepAnd("notification_show_sprees_info", 1, 3);
        TR();
            TD(1, 3.1f, Qc.CheckEx(2, 1, "notification_CHOICE_FRAG", "Add extra frag information to centerprint when available"))
                .Multi("notification_CHOICE_FRAG", "notification_CHOICE_FRAGGED notification_CHOICE_TYPEFRAG notification_CHOICE_TYPEFRAGGED notification_CHOICE_FRAG_FIRE notification_CHOICE_FRAGGED_FIRE notification_CHOICE_FRAG_FREEZE notification_CHOICE_FRAGGED_FREEZE");
        TR();
            TD(1, 3.1f, Qc.Check(0, "notification_show_location", "Add frag location to death messages when available"));

        TR();
        TR();
            TD(1, 3.1f, Qc.Header("Gametype Settings"));
        string ctf = Localization.Tr("Capture the Flag");
        TR();
            TD(1, 3.1f, Qc.CheckEx(2, 1, "notification_CHOICE_CTF_CAPTURE_TIME",
                    string.Format(Localization.Tr("Display capture times in {0}"), ctf)))
                .Multi("notification_CHOICE_CTF_CAPTURE_TIME", "notification_CHOICE_CTF_CAPTURE_BROKEN notification_CHOICE_CTF_CAPTURE_UNBROKEN");
        TR();
            TD(1, 3.1f, Qc.CheckEx(2, 1, "notification_CHOICE_CTF_PICKUP_ENEMY",
                    string.Format(Localization.Tr("Display name of flag stealer in {0}"), ctf)))
                .Multi("notification_CHOICE_CTF_PICKUP_ENEMY", "notification_CHOICE_CTF_PICKUP_ENEMY_TEAM notification_CHOICE_CTF_PICKUP_ENEMY_NEUTRAL");

        GotoRC(0, 3.1f); SetFirstColumn(CurrentColumn);
            TD(1, 2.9f, Qc.Header("Other"));
        TR();
            TD(1, 2.9f, Qc.CheckEx(4, 0, "con_notify", "Display console messages in the top left corner"));
        TR();
            TD(1, 2.9f, Qc.CheckEx(2, 1, "notification_allow_chatboxprint", "Display all info messages in the chatbox"));
        TR();
            TD(1, 2.9f, Qc.CheckEx(2, 1, "notification_INFO_QUIT_DISCONNECT", "Display player statuses in the chatbox"))
                .Multi("notification_INFO_QUIT_DISCONNECT", "notification_INFO_QUIT_KICK_IDLING notification_INFO_JOIN_CONNECT");
        TR();
        TR();
            TD(1, 2.9f, Qc.Check(0, "notification_CENTER_POWERUP_INVISIBILITY", "Powerup notifications"))
                .Multi("notification_CENTER_POWERUP_INVISIBILITY", "notification_CENTER_POWERUP_SHIELD notification_CENTER_POWERUP_SPEED notification_CENTER_POWERUP_STRENGTH notification_CENTER_POWERDOWN_INVISIBILITY notification_CENTER_POWERDOWN_SHIELD notification_CENTER_POWERDOWN_SPEED notification_CENTER_POWERDOWN_STRENGTH notification_CENTER_SUPERWEAPON_BROKEN notification_CENTER_SUPERWEAPON_LOST notification_CENTER_SUPERWEAPON_PICKUP notification_INFO_POWERUP_INVISIBILITY notification_INFO_POWERUP_SHIELD notification_INFO_POWERUP_SPEED notification_INFO_POWERUP_STRENGTH");
        TR();
            TD(1, 2.9f, Qc.Check(0, "notification_CENTER_ITEM_WEAPON_DONTHAVE", "Weapon centerprint notifications"))
                .Multi("notification_CENTER_ITEM_WEAPON_DONTHAVE", "notification_CENTER_ITEM_WEAPON_DROP notification_CENTER_ITEM_WEAPON_GOT notification_CENTER_ITEM_WEAPON_NOAMMO notification_CENTER_ITEM_WEAPON_PRIMORSEC notification_CENTER_ITEM_WEAPON_UNAVAILABLE");
        TR();
            TD(1, 2.9f, Qc.Check(0, "notification_INFO_ITEM_WEAPON_DONTHAVE", "Weapon info message notifications"))
                .Multi("notification_INFO_ITEM_WEAPON_DONTHAVE", "notification_INFO_ITEM_WEAPON_DROP notification_INFO_ITEM_WEAPON_GOT notification_INFO_ITEM_WEAPON_NOAMMO notification_INFO_ITEM_WEAPON_PRIMORSEC notification_INFO_ITEM_WEAPON_UNAVAILABLE");

        TR();
        TR();
            TD(1, 2.9f, Qc.Header("Announcers"));
        TR();
            TD(1, 2.9f, Qc.CheckEx(2, 0, "notification_ANNCE_NUM_RESPAWN_1", "Respawn countdown sounds"))
                .Multi("notification_ANNCE_NUM_RESPAWN_1", "notification_ANNCE_NUM_RESPAWN_2 notification_ANNCE_NUM_RESPAWN_3 notification_ANNCE_NUM_RESPAWN_4 notification_ANNCE_NUM_RESPAWN_5 notification_ANNCE_NUM_RESPAWN_6 notification_ANNCE_NUM_RESPAWN_7 notification_ANNCE_NUM_RESPAWN_8 notification_ANNCE_NUM_RESPAWN_9 notification_ANNCE_NUM_RESPAWN_10");
        TR();
            TD(1, 2.9f, Qc.CheckEx(1, 0, "notification_ANNCE_KILLSTREAK_03", "Killstreak sounds"))
                .Multi("notification_ANNCE_KILLSTREAK_03", "notification_ANNCE_KILLSTREAK_05 notification_ANNCE_KILLSTREAK_10 notification_ANNCE_KILLSTREAK_15 notification_ANNCE_KILLSTREAK_20 notification_ANNCE_KILLSTREAK_25 notification_ANNCE_KILLSTREAK_30");
        TR();
            TD(1, 2.9f, Qc.CheckEx(1, 0, "notification_ANNCE_ACHIEVEMENT_AIRSHOT", "Achievement sounds"))
                .Multi("notification_ANNCE_ACHIEVEMENT_AIRSHOT", "notification_ANNCE_ACHIEVEMENT_AMAZING notification_ANNCE_ACHIEVEMENT_AWESOME notification_ANNCE_ACHIEVEMENT_BOTLIKE notification_ANNCE_ACHIEVEMENT_ELECTROBITCH notification_ANNCE_ACHIEVEMENT_IMPRESSIVE notification_ANNCE_ACHIEVEMENT_YODA");
    }
}

// =============================================================================================================
//  Damage text — common/mutators/mutator/damagetext/ui_damagetext.qc
// =============================================================================================================

public partial class DialogSettingsGameDamageText : QcTab
{
    public DialogSettingsGameDamageText() : base(rows: 15.5f, columns: 5f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        GotoRC(0, 1); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Damage Text"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_damagetext", "Draw damage numbers"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_damagetext_friendlyfire", "Draw damage numbers for friendly fire")).Dep("cl_damagetext", 1, 1);
        TR();
        TR();
            TD(1, 1, Qc.Label("Font size minimum:")).Dep("cl_damagetext", 1, 1);
            TD(1, 2, Qc.Slider(0, 50, 1, "cl_damagetext_size_min")).Dep("cl_damagetext", 1, 1);
        TR();
            TD(1, 1, Qc.Label("Font size maximum:")).Dep("cl_damagetext", 1, 1);
            TD(1, 2, Qc.Slider(0, 50, 1, "cl_damagetext_size_max")).Dep("cl_damagetext", 1, 1);
        TR();
            TD(1, 1, Qc.Label("Color:")).Dep("cl_damagetext", 1, 1);
            TD(2, 2, GameTopic.Cell(HslColorPicker.ForStringCvar("cl_damagetext_color")))
                .Dep("cl_damagetext", 1, 1).DepAnd("cl_damagetext_color_per_weapon", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Check(0, "cl_damagetext_color_per_weapon", "Per weapon")).Dep("cl_damagetext", 1, 1);
        TR();
        TR();
            TD(1, 1, Qc.Label("Initial opacity:")).Dep("cl_damagetext", 1, 1);
            TD(1, 2, Qc.Slider(0.25f, 1, 0.05f, "cl_damagetext_alpha_start", "", "%")).Dep("cl_damagetext", 1, 1);
        TR();
            TD(1, 1, Qc.Label("Fade time:")).Dep("cl_damagetext", 1, 1);
            TD(1, 2, Qc.Slider(1, 5, 0.5f, "cl_damagetext_alpha_lifetime", "", "S")).Dep("cl_damagetext", 1, 1);
        TR();
            TD(1, 3, Qc.Label("Accumulate:")).Dep("cl_damagetext", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 1.2f, Qc.Label("If younger than:")).Dep("cl_damagetext", 1, 1);
            TD(1, 1.6f, Qc.Mixed("cl_damagetext_accumulate_lifetime", "", "S")
                .Add("Never", 0).AddRange(0.5f, 3, 0.5f).Add("Always", -1).Done(1.25f)).Dep("cl_damagetext", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 1.2f, Qc.Label("Or opacity greater than:")).DepNot("cl_damagetext_accumulate_lifetime", 0).DepAnd("cl_damagetext", 1, 1);
            TD(1, 1.6f, Qc.Slider(0, 1, 0.05f, "cl_damagetext_accumulate_alpha_rel", "", "%"))
                .DepNot("cl_damagetext_accumulate_lifetime", 0).DepAnd("cl_damagetext", 1, 1);
    }
}
