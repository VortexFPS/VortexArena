using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The rows every HUD-panel dialog shares — <c>dialog_hudpanel_main_checkbox</c> and
/// <c>dialog_hudpanel_main_settings</c> (qcsrc/menu/xonotic/util.qc) — written on a <see cref="QcGrid"/> TD for TD,
/// so a <c>DialogHudPanel*</c> fill calls them exactly where its <c>XonoticHUD*Dialog_fill</c> does. Also the
/// small factories those rows need that the <see cref="Qc"/> set has no counterpart for:
/// <c>makeXonoticTextSlider</c> over a string cvar and <c>makeXonoticColorpickerString</c>.
/// </summary>
public static class HudPanelCommon
{
    /// <summary>
    /// The HUD skin files for <see cref="DialogHudSetupExit"/>'s list (QC <c>getHUDSkinFiles</c>: <c>hud_*.cfg</c>
    /// and <c>data/hud_*.cfg</c>), as paths relative to the game directory. The menu has no file scan of its own
    /// for these yet; until something sets this the list is empty. The editor's Ctrl+S and "Save current skin"
    /// write the files (<see cref="Game.Hud.HudConfigEditor.ExportCfg"/>).
    /// </summary>
    public static Func<IEnumerable<string>>? SkinListProvider { get; set; }

    /// <summary>The QC dialog colour of every HUD-editor dialog (SKINCOLOR_DIALOG_TEAMSELECT).</summary>
    public static Color DialogColor => MenuSkin.SkinColor("COLOR_DIALOG_TEAMSELECT", 1f, 1f, 1f);

    /// <summary>dialog_hudpanel_main_checkbox(me, panelname).</summary>
    public static void MainCheckbox(QcGrid me, string panelname)
    {
        me.TR();
            me.TDempty(1.5f);
            me.TD(1, 2.5f, Qc.Check(0, "hud_panel_" + panelname, "Enable"));
    }

    /// <summary>dialog_hudpanel_main_settings(me, panelname).</summary>
    public static void MainSettings(QcGrid me, string panelname)
    {
        string p = "hud_panel_" + panelname;

        me.GotoRC(me.CurrentRow + 1.5f, 0);
            me.TD(1, 1.4f, Qc.Label("Background:"));
                me.TD(1, 2.6f, TextSlider(p + "_bg", 1.25f)
                    .Add(Localization.Tr("Default"), "")
                    .Add(Localization.Tr("Disable"), "0")
                    .Add("border_" + panelname, "border_" + panelname));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Color:")).DepStringNot(p + "_bg", "0");
            me.TD(2, 2.6f, ColorPicker(p + "_bg_color")).DepStringNot(p + "_bg_color", "");
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 3.6f, CheckString("", "1 1 1", p + "_bg_color", "Default"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Border size:"));
                QcTextSlider border = TextSlider(p + "_bg_border")
                    .Add(Localization.Tr("Default"), "")
                    .Add(Localization.Tr("Disable"), "0");
                for (int i = 1; i <= 10; ++i)
                    border.Add(QcFormat.Decimals(i * 2, 0), QcFormat.MinDecimals(i * 2));
                me.TD(1, 2.6f, border);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Opacity:"));
                QcTextSlider alpha = TextSlider(p + "_bg_alpha")
                    .Add(Localization.Tr("Default"), "");
                for (int i = 1; i <= 20; ++i)
                    alpha.Add(QcFormat.Decimals(i / 20f * 100f, 0) + "%", QcFormat.MinDecimals(i / 20f));
                me.TD(1, 2.6f, alpha);
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Team color:")).DepNot(p + "_bg_alpha", 0);
                QcTextSlider team = TextSlider(p + "_bg_color_team")
                    .Add(Localization.Tr("Default"), "")
                    .Add(Localization.Tr("Disable"), "0");
                for (int i = 1; i <= 10; ++i)
                    team.Add(QcFormat.Decimals(i / 10f * 100f, 0) + "%", QcFormat.MinDecimals(i / 10f));
                me.TD(1, 2.6f, team).DepNot(p + "_bg_alpha", 0);
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 3.6f, Qc.Check(0, "hud_configure_teamcolorforced", "Test team color in configure mode"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Padding:"));
                QcTextSlider padding = TextSlider(p + "_bg_padding")
                    .Add(Localization.Tr("Default"), "");
                for (int i = 0; i <= 10; ++i)
                    padding.Add(QcFormat.Decimals(i - 5, 0), QcFormat.MinDecimals(i - 5));
                me.TD(1, 2.6f, padding);

        // cvar_type(hide_ondeath) & CVAR_TYPEFLAG_EXISTS: only the panels that have the cvar get the row.
        string hideOnDeath = p + "_hide_ondeath";
        if (MenuState.Cvars.Has(hideOnDeath))
        {
            me.TR();
                me.TDempty(0.2f);
                me.TD(1, 3.8f, Qc.Check(0, hideOnDeath, "Hide on death"));
        }

        me.GotoRC(me.CurrentRow + 0.5f, 0); me.SetFirstColumn(me.CurrentColumn);
    }

    /// <summary>
    /// makeXonoticTextSlider(cvar) (xonotic/textslider.qc): values identified by the cvar's string, "Custom" when
    /// it holds none of them; <paramref name="valueSpaceFactor"/> is a following <c>setValueSpace(e, e.valueSpace *
    /// factor)</c>. Add the values, then place it (the QC's configureXonoticTextSliderValues is the first load).
    /// </summary>
    public static QcTextSlider TextSlider(string cvar, float valueSpaceFactor = 1f)
    {
        string c = CvarUi.EnsureEngine(cvar);
        var s = new QcTextSlider(() => MenuState.Cvars.GetString(c), id =>
        {
            MenuState.Cvars.Set(c, id);
            MenuState.Cvars.MarkArchived(c);
        });
        return QcLook.Slider(s, valueSpaceFactor * QcLook.DefaultValueSpace);
    }

    /// <summary>makeXonoticColorpickerString(cvar, defaultCvar): the HSL image picker writing "r g b" to the cvar.
    /// (The QC's default cvar is stored but never read, so it is not taken here.) No minimum size: a QC item is
    /// its cell.</summary>
    public static HslColorPicker ColorPicker(string cvar)
    {
        HslColorPicker picker = HslColorPicker.ForStringCvar(CvarUi.EnsureEngine(cvar));
        picker.CustomMinimumSize = Vector2.Zero;
        return picker;
    }

    /// <summary>makeXonoticCheckBoxString(yes, no, cvar, text): checked while the cvar is the yes string.</summary>
    public static QcCheckBoxString CheckString(string yes, string no, string cvar, string text)
        => QcLook.CheckBox(new QcCheckBoxString(yes, no, cvar, text));

    /// <summary>The QC <c>ZCTX(_("CONTEXT^Text"))</c> idiom: translated, context prefix stripped.</summary>
    public static string Z(string source) => Localization.CtxTr(source);
}

/// <summary>
/// What every <c>XonoticHUD*Dialog</c> (xonotic/dialog_hudpanel_*.qh) declares alike: a root dialog 0.4 of the menu
/// wide on a 4-column grid, coloured SKINCOLOR_DIALOG_TEAMSELECT, placed by mainwindow.qc with
/// <c>addItemRightCentered</c> at SKINALPHAS_MAINMENU_z (1). A panel dialog gives its title and rows and its
/// <c>_fill</c> as <see cref="Fill"/>.
/// </summary>
public abstract partial class HudPanelDialog : MenuScreen, IQcDialog
{
    public abstract string QcTitle { get; }
    public float QcIntendedWidth => 0.4f;
    public abstract float QcRows { get; }
    public Color QcBorderColor => HudPanelCommon.DialogColor;
    public bool QcRightCentered => true;

    protected override void BuildUi()
    {
        Name = GetType().Name;
        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);
        Fill(me);
    }

    /// <summary>The QC <c>XonoticHUD*Dialog_fill(me)</c>.</summary>
    protected abstract void Fill(QcGrid me);
}

/// <summary>
/// XonoticCheckBoxString (xonotic/checkbox_string.qc): checked while the cvar's string is exactly the yes string;
/// toggling writes the yes or the no string. Unlike <see cref="CvarCheckBox"/> it never compares numerically, so
/// an empty yes string ("use the global default", the HUD panels' bg colour) works.
/// </summary>
public partial class QcCheckBoxString : CheckBox
{
    private readonly string _yes, _no, _cvar;
    private bool _updating;

    public QcCheckBoxString(string yes, string no, string cvar, string text)
    {
        _yes = yes;
        _no = no;
        _cvar = CvarUi.EnsureEngine(cvar);
        Text = Localization.Tr(text);
        ToggleMode = true;
        Toggled += OnToggled;
    }

    public override void _EnterTree()
    {
        CvarUi.Cvars.Changed += OnCvarChanged;
        Load();
    }

    public override void _ExitTree() => CvarUi.Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (string.Equals(name, _cvar, StringComparison.OrdinalIgnoreCase)) Load();
    }

    private void Load()
    {
        _updating = true;
        ButtonPressed = CvarUi.Cvars.GetString(_cvar) == _yes;
        _updating = false;
    }

    private void OnToggled(bool on)
    {
        if (_updating) return;
        CvarUi.Cvars.Set(_cvar, on ? _yes : _no);
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}
