namespace VortexArena.Game.Menu;

/// <summary>
/// "Pressed Keys Panel" — <c>XonoticHUDPressedKeysDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudpanel_pressedkeys.qc)
/// on its 15.5 x 4 grid: the three-way enable as a full-width mixed slider, the shared background rows, then the
/// forced aspect and the attack keys.
/// </summary>
public partial class DialogHudPanelPressedKeys : HudPanelDialog
{
    public override string QcTitle => Localization.Tr("Pressed Keys Panel");
    public override float QcRows => 15.5f;

    protected override void Fill(QcGrid me)
    {
        const string panelname = "pressedkeys";

        me.TR();
            me.TD(1, 4, Qc.Mixed("hud_panel_pressedkeys")
                .Add("Disable", 0)
                .Add("Enable spectating", 1)
                .Add("Always enable", 2)
                .Done());

        HudPanelCommon.MainSettings(me, panelname);

        me.TR();
            me.TD(1, 1.4f, Qc.Label("Forced aspect:"));
            me.TD(1, 2.6f, Qc.Slider(0.2f, 4, 0.1f, "hud_panel_pressedkeys_aspect"));

        me.TR();
            me.TD(1, 4, Qc.Check(0, "hud_panel_pressedkeys_attack", "Show attack keys"));
    }
}
