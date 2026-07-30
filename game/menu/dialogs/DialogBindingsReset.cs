using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Reset key bindings" confirm popup — a faithful C# port of <c>XonoticBindingsResetDialog</c>
/// (qcsrc/menu/xonotic/dialog_settings_bindings_reset.qc). A single warning line then a Yes/No row.
///
/// In the QC, "Yes" runs <c>KeyBinder_Bind_Reset_All</c> (keybinder.qc) which issues
/// <c>unbindall; exec binds-xonotic.cfg; -zoom</c>, sets <c>_hud_showbinds_reload 1</c>, then closes the
/// dialog. We reproduce that exactly: a <see cref="Widgets.CommandButton"/> carrying the same command string
/// (routed through <see cref="MenuCommand"/>) plus the <c>_hud_showbinds_reload</c> set, then Back. VortexArena has
/// no keybind backend yet, so <c>unbindall</c>/<c>exec</c>/<c>-zoom</c> log inert; the cvar write is real.
/// "No" was QC <c>Dialog_Close</c> — here the universal Back. Binds no persistent settings cvars of its own.
/// QC title "Reset key bindings".
/// </summary>
public partial class DialogBindingsReset : MenuScreen
{
    // The QC KeyBinder_Bind_Reset_All command sequence, verbatim, plus the reload flag it sets via cvar_set.
    private const string ResetCommand =
        "unbindall; exec binds-xonotic.cfg; -zoom; set _hud_showbinds_reload 1";

    protected override void BuildUi()
    {
        Name = "DialogBindingsReset"; // QC XonoticBindingsResetDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Reset key bindings"));

        // The QC message row (makeXonoticTextLabel, centered align 0.5).
        var line = MakeLabel("Are you sure you want to reset all key bindings?");
        line.HorizontalAlignment = HorizontalAlignment.Center;
        root.AddChild(line);

        root.AddChild(Ui.Spacer());

        // QC: Yes = KeyBinder_Bind_Reset_All (the command sequence above + close); No = Dialog_Close (Back).
        // The button runs the commands then pops the dialog, matching the QC me.close(me) at the end.
        var yes = MakeButton("Yes", () => { MenuCommand.Run(ResetCommand); GoBack(); });
        yes.TooltipText = "Reset every key binding to the Xonotic defaults";
        root.AddChild(MakeButtonBar(yes, MakeButton("No", GoBack)));
    }
}
