using System;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Disconnect" — <c>XonoticDemoStartConfirmDialog_fill</c> and <c>XonoticDemoTimeConfirmDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_demo_startconfirm.qc / _timeconfirm.qc) on their 4 x 2 grid, 0.5 of the menu
/// wide: the two centred lines ("Playing" or "Timing a demo will disconnect you from the current match."), then a
/// red "Yes" that starts the demo and a green "No" that closes it. The Demos tab opens it instead of starting the
/// demo when a match is running (DemoConfirm_ListClick_Check_Gamestatus).
/// </summary>
public partial class DialogDemoConfirm : MenuScreen, IQcDialog
{
    private readonly bool _timing;
    private readonly Action _start;

    public DialogDemoConfirm(bool timing, Action start)
    {
        _timing = timing;
        _start = start;
    }

    public string QcTitle => Localization.Tr("Disconnect");
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 4;

    protected override void BuildUi()
    {
        Name = _timing ? "DialogDemoTimeConfirm" : "DialogDemoStartConfirm"; // QC XonoticDemo{Time,Start}ConfirmDialog

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label(_timing
                ? "Timing a demo will disconnect you from the current match."
                : "Playing a demo will disconnect you from the current match.", 0.5f));
        me.TR();
            me.TD(1, 2, Qc.Label("Do you really wish to disconnect now?", 0.5f));
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Button("Yes", _start, tint: new Color(1, 0, 0)));
            me.TD(1, 1, Qc.Button("No", GoBack, tint: new Color(0, 1, 0)));
    }
}
