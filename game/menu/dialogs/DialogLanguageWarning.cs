using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Warning" language-change popup — a faithful C# port of <c>XonoticLanguageWarningDialog</c>
/// (qcsrc/menu/xonotic/dialog_settings_user_languagewarning.qc). Shown after the user picks a new menu language
/// while connected: a wrapped warning paragraph then two command buttons.
///   * "Disconnect now" — QC <c>makeXonoticCommandButton(_("Disconnect now"), '0 0 0', "disconnect", 0)</c>;
///     reproduced with a <see cref="Widgets.CommandButton"/> on the SAME <c>disconnect</c> command (wired to the
///     <see cref="MenuCommand"/> Disconnect host hook).
///   * "Switch language" — QC command
///     <c>"prvm_language \"$_menu_prvm_language\"; menu_restart; menu_cmd languageselect"</c>; reproduced with
///     the SAME command string ($_menu_prvm_language is expanded by MenuCommand; <c>prvm_language</c>/
///     <c>menu_restart</c>/<c>menu_cmd</c> have no client backend yet so they log inert).
/// Binds no cvars. The QC dialog has no explicit close button (it relies on the dialog frame's X); the standard
/// Back is added so it can be dismissed without acting. QC title "Warning".
/// </summary>
public partial class DialogLanguageWarning : MenuScreen
{
    // QC "Switch language" command, verbatim ($_menu_prvm_language expanded at run time by MenuCommand).
    private const string SwitchCommand =
        "prvm_language \"$_menu_prvm_language\"; menu_restart; menu_cmd languageselect";

    protected override void BuildUi()
    {
        Name = "DialogLanguageWarning"; // QC XonoticLanguageWarningDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Warning"));

        // The QC wrapped message label (allowWrap = true).
        var msg = MakeLabel(
            "While connected language changes will be applied only to the menu, full language changes will " +
            "take effect starting from the next game");
        msg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        msg.HorizontalAlignment = HorizontalAlignment.Center;
        msg.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(msg);

        root.AddChild(Ui.Spacer());

        // The two QC command buttons, side by side, with the same command strings.
        var disconnect = Widgets.CommandButton("Disconnect now", "disconnect");
        var switchLang = Widgets.CommandButton("Switch language", SwitchCommand);
        root.AddChild(MakeButtonBar(disconnect, switchLang));

        // QC has no explicit close button; the standard Back lets the user dismiss without acting.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
