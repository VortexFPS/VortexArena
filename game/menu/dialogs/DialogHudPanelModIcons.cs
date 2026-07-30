using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Mod Icons" panel config dialog — a faithful C# port of <c>XonoticHUDModIconsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_modicons.qc).
///
/// Emits the common panel block first (<c>dialog_hudpanel_main_checkbox</c> "Enable" on
/// <c>hud_panel_modicons</c>, then the <c>dialog_hudpanel_main_settings</c> "Background" group on
/// <c>hud_panel_modicons_bg*</c>), then one "Show icons" checkbox per gametype the QC lists: Clan Arena,
/// Domination, Freeze Tag (bound to <c>hud_panel_modicons_{ca,dom,freezetag}_layout</c>).
///
/// The QC labels read <c>ZCTX(sprintf(_("GAMETYPE^%s:"), MapInfo_Type_ToText(MAPINFO_TYPE_*)))</c>; the
/// gametype display names are resolved here from the same mapinfo table (CA -> "Clan Arena", etc.).
///
/// FAITHFUL UI NOW: every binding writes the real <c>hud_panel_modicons*</c> cvars the in-game mod-icons
/// panel reads. There is no live HUD editor/preview in VortexArena yet, so nothing previews here.
/// </summary>
public partial class DialogHudPanelModIcons : MenuScreen
{
    private const string Panel = "modicons";

    protected override void BuildUi()
    {
        Name = "DialogHudPanelModIcons";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Mod Icons Panel"));

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // Common HUD-panel block: enable mode + Background group.
        HudPanelCommon.BuildCommon(box, Panel);

        box.AddChild(Ui.Spacer());

        // --- Panel-specific rows (dialog_hudpanel_modicons.qc) ------------------------------------------
        // One per-gametype "Show icons" checkbox. The QC label is the gametype's display name + ":".

        // QC: GAMETYPE^<MapInfo_Type_ToText(MAPINFO_TYPE_CA)> -> "Clan Arena".
        box.AddChild(Ui.Row("Clan Arena:",
            Widgets.CheckBox("hud_panel_modicons_ca_layout", "Show icons")));

        // QC: GAMETYPE^<MapInfo_Type_ToText(MAPINFO_TYPE_DOMINATION)> -> "Domination".
        box.AddChild(Ui.Row("Domination:",
            Widgets.CheckBox("hud_panel_modicons_dom_layout", "Show icons")));

        // QC: GAMETYPE^<MapInfo_Type_ToText(MAPINFO_TYPE_FREEZETAG)> -> "Freeze Tag".
        box.AddChild(Ui.Row("Freeze Tag:",
            Widgets.CheckBox("hud_panel_modicons_freezetag_layout", "Show icons")));

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
