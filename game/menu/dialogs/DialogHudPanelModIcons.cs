namespace VortexArena.Game.Menu;

/// <summary>
/// "Mod Icons Panel" — <c>XonoticHUDModIconsDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_modicons.qc) on its
/// 15.5 x 4 grid: the shared rows, then a "Show icons" checkbox per gametype. The labels are
/// <c>ZCTX(sprintf(_("GAMETYPE^%s:"), MapInfo_Type_ToText(...)))</c> for Clan Arena, Domination and Freeze Tag.
/// </summary>
public partial class DialogHudPanelModIcons : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Mod Icons Panel");
    public override float QcRows => 15.5f;

    private static string Gametype(string name) => HudPanelCommon.Z("GAMETYPE^%s:").Replace("%s", Localization.Tr(name));

    protected override void Fill(QcGrid me)
    {
        const string panelname = "modicons";

        HudPanelCommon.MainCheckbox(me, panelname);

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label(Gametype("Clan Arena")));
            me.TD(1, 2.6f, Qc.Check(0, "hud_panel_modicons_ca_layout", "Show icons"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label(Gametype("Domination")));
            me.TD(1, 2.6f, Qc.Check(0, "hud_panel_modicons_dom_layout", "Show icons"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label(Gametype("Freeze Tag")));
            me.TD(1, 2.6f, Qc.Check(0, "hud_panel_modicons_freezetag_layout", "Show icons"));
    }
}
