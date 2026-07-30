using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Factory reset" confirm popup — a faithful C# port of <c>XonoticResetDialog</c>
/// (qcsrc/menu/xonotic/dialog_settings_misc_reset.qc). A two-line warning message followed by a Yes/No row:
///   * "Yes" is a QC <c>makeXonoticCommandButton(_("Yes"), '1 0 0', "saveconfig backup.cfg; exec default.cfg", 0)</c>
///     — it backs up the current config then re-execs the defaults. Reproduced with a
///     <see cref="Widgets.CommandButton"/> carrying the SAME command string (routed through
///     <see cref="MenuCommand"/>; <c>saveconfig</c>/<c>exec</c> have no client backend yet so they log inert).
///   * "No" was QC <c>Dialog_Close</c> — here the universal Back.
/// Binds no cvars (a pure action dialog). QC title "Factory reset".
/// </summary>
public partial class DialogSettingsReset : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogSettingsReset"; // QC XonoticResetDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Factory reset"));

        // The two QC message rows (makeXonoticTextLabel, centered align 0.5).
        var line1 = MakeLabel("Are you sure you want to reset all settings?");
        line1.HorizontalAlignment = HorizontalAlignment.Center;
        var line2 = MakeLabel("This will create a backup config in your data directory");
        line2.HorizontalAlignment = HorizontalAlignment.Center;
        root.AddChild(line1);
        root.AddChild(line2);

        root.AddChild(Ui.Spacer());

        // QC: Yes = command button "saveconfig backup.cfg; exec default.cfg"; No = Dialog_Close (Back here).
        var yes = Widgets.CommandButton("Yes", "saveconfig backup.cfg; exec default.cfg");
        root.AddChild(MakeButtonBar(yes, MakeButton("No", GoBack)));
    }
}
