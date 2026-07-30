using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// First-run setup dialog — a faithful C# port of <c>XonoticFirstRunDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_firstrun.qc). Shown once on the very first launch (QC
/// <c>shouldShow</c>: <c>_cl_name == defstring("_cl_name")</c>): it greets the player, lets them pick a
/// language, enter a player name, and choose whether player statistics may use their nickname, then
/// "Save settings" applies it all (and, because of the language selector, restarts the menu).
///
/// It binds the SAME engine cvars the QC binds:
///   * <c>_cl_name</c> — the player name input box;
///   * <c>_menu_prvm_language</c> — the text-language chooser;
///   * <c>cl_allow_uid2name</c> — the Yes/No/Undecided stats-nickname radio (1 / 0 / -1).
/// The "Save settings" button issues the SAME console command as QC's apply button:
/// <c>prvm_language "$_menu_prvm_language"; saveconfig; menu_restart</c>.
///
/// Approximations (no toolkit factory, faithful to the same cvar):
///   * QC <c>makeXonoticLanguageList</c> (a data-driven list box scanning languages.txt) becomes a
///     <see cref="Widgets.TextSlider"/> over the same representative locale set used by the User settings
///     tab — same cvar (<c>_menu_prvm_language</c>), approximate option set, INERT until a localization
///     backend exists (the Save command is still issued faithfully).
///   * QC <c>makeXonoticColorpicker(box)</c> + <c>makeXonoticCharmap(box)</c> (the name-color picker and
///     character map that edit the name field's colors/glyphs) have no VortexArena backend yet; rendered as a
///     short honest note label. INERT.
/// </summary>
public partial class DialogFirstRun : MenuScreen
{
    // Fallback locale set, used only when languages.txt can't be read (no content repo). The live picker is
    // data-driven from languages.txt via Localization.LoadLanguages() (QC makeXonoticLanguageList), same cvar
    // (_menu_prvm_language). Mirrors DialogSettingsUser's fallback so the two pickers agree headlessly.
    private static readonly (string Label, string Value)[] LanguagesFallback =
    {
        ("English", "en"),
        ("Deutsch", "de"),
        ("Español", "es"),
        ("Français", "fr"),
        ("Italiano", "it"),
        ("Português", "pt"),
        ("Русский", "ru"),
        ("中文（简体字）", "zh_CN"),
    };

    protected override void BuildUi()
    {
        Name = "DialogFirstRun";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        // QC: no title-size label, but the dialog title is "Welcome".
        root.AddChild(MakeTitle("Welcome"));

        // QC: the wrapping intro text label (makeXonoticTextLabel, allowWrap = true).
        var intro = MakeLabel(
            "Welcome to Xonotic, please select your language preference and enter your player name to " +
            "get started.  You can change these options later through the menu system.");
        intro.AutowrapMode = TextServer.AutowrapMode.Word;
        root.AddChild(intro);

        root.AddChild(Ui.Spacer());

        // --- Player name (QC: "Name:" label + colored preview label + makeXonoticInputBox_T(1,"_cl_name")). ---
        // QC binds _cl_name; the colored preview label mirrors the box (label.textEntity = box). We render the
        // bound input box directly (it already shows the live cvar value).
        root.AddChild(Ui.Row("Name:",
            Widgets.InputBox("_cl_name", "player", "Name under which you will appear in the game")));

        // QC makeXonoticColorpicker(box) + makeXonoticCharmap(box): name-color picker + character map editing
        // the name field. No VortexArena backend yet — honest note. INERT.
        var pickerNote = MakeLabel("(name color picker / character map — editing backend pending)");
        pickerNote.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.72f));
        root.AddChild(pickerNote);

        root.AddChild(Ui.Spacer());

        // --- Text language (QC: "Text language:" label + makeXonoticLanguageList). ---
        // QC binds _menu_prvm_language. Data-driven from languages.txt (localized name -> id), like QC; falls
        // back to a small set headlessly. Self-names are not translated (AddRaw).
        var lang = Widgets.TextSlider("_menu_prvm_language");
        var langs = Localization.LoadLanguages();
        if (langs.Count > 0)
            foreach (var l in langs)
                lang.AddRaw(l.Localized, l.Id);
        else
            foreach ((string label, string value) in LanguagesFallback)
                lang.AddRaw(label, value);
        root.AddChild(Ui.Row("Text language:", lang));

        root.AddChild(Ui.Spacer());

        // --- Player statistics nickname permission (QC: prompt + Yes/No/Undecided radio on cl_allow_uid2name). ---
        root.AddChild(MakeLabel("Allow player statistics to use your nickname at stats.xonotic.org?"));

        var uidGroup = new ButtonGroup();
        var uidRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        uidRow.AddThemeConstantOverride("separation", 8);
        // QC makeXonoticRadioButton(1, "cl_allow_uid2name", "1"/"0"/"-1").
        uidRow.AddChild(Widgets.RadioButton("cl_allow_uid2name", "1", "Yes", uidGroup));
        uidRow.AddChild(Widgets.RadioButton("cl_allow_uid2name", "0", "No", uidGroup));
        uidRow.AddChild(Widgets.RadioButton("cl_allow_uid2name", "-1", "Undecided", uidGroup));
        root.AddChild(uidRow);

        root.AddChild(MakeLabel(
            "Player statistics are enabled by default, you can change this in the Profile menu"));

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        // QC: makeXonoticCommandButton("Save settings", …, "prvm_language \"$_menu_prvm_language\"; saveconfig;
        // menu_restart", COMMANDBUTTON_APPLY). Same command string. We also persist the menu cvar store on the
        // way out (saveconfig's local equivalent) and provide the universal Back.
        var save = Widgets.CommandButton("Save settings",
            "prvm_language \"$_menu_prvm_language\"; saveconfig; menu_restart");
        save.Pressed += MenuState.SaveUserConfig; // mirror QC saveconfig against the VortexArena cvar store

        root.AddChild(MakeButtonBar(save, MakeButton("Back", OnBack)));
    }

    private void OnBack()
    {
        MenuState.SaveUserConfig();
        GoBack();
    }
}
