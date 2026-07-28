using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// User settings tab — a faithful C# port of <c>XonoticUserSettingsTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings_user.qc). It groups the menu-skin selector, the text-language
/// selector, and the "gentle mode" content cvars, binding the same engine cvars the QC binds, with the same
/// "Set skin" / "Set language" command buttons and the same dependencies (the gentle sub-options grey out
/// while full gentle mode is on).
///
/// The skin list (QC <c>makeXonoticSkinList</c>) is the faithful data-driven backend
/// <see cref="DialogMediaSkinList"/>, embedded directly here (it scans gfx/menu/*/skinvalues.txt and binds
/// <c>menu_skin</c>, exactly like the QC list box).
///
/// The language list is approximated: its QC counterpart (<c>makeXonoticLanguageList</c>) is a data-driven
/// list box; here it is a TextSlider populated from languages.txt (same cvar <c>_menu_prvm_language</c>),
/// inert until a localization backend exists (the "Set language" button still issues the same console command).
/// </summary>
public partial class DialogSettingsUser : SettingsTab
{
    // Fallback locale set, used only when languages.txt can't be read (no content repo). The live picker is now
    // data-driven from languages.txt via Localization.LoadLanguages() (QC makeXonoticLanguageList), so this is a
    // headless-only floor; same cvar (_menu_prvm_language), IDs match Xonotic's language codes.
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

    protected override void Fill(VBoxContainer box)
    {
        // dialog_settings_user.qc lays this tab out as TWO EXPLICIT COLUMNS on its 15.5×6 grid — LEFT: the
        // "Menu Skin" section (header + skin list spanning nearly the full tab height + "Set skin",
        // qc:16-31 / grid cols 0.25–2.75); RIGHT: "Text Language" (header + list + "Set language",
        // gotoRC(0, 3.65)) with the gentle-mode checkboxes below it (gotoRC(11, 3.25)). Build the same two
        // columns here instead of relying on SettingsTab's generic half-split, which used to land the language
        // section in the left column on top of the skin list. (A single child also opts out of that auto-split.)
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 28);
        box.AddChild(columns);

        // QC column widths: skin section 2.5 cells, language/gentle section ~1.7–2.5 → ≈1.4 : 1.
        var left = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.4f,
        };
        left.AddThemeConstantOverride("separation", 6);
        columns.AddChild(left);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 6);
        columns.AddChild(right);

        // --- LEFT: Menu Skin (QC makeXonoticHeaderLabel("Menu Skin") + makeXonoticSkinList + "Set skin" button) ---
        // The data-driven skin list box (qcsrc/menu/xonotic/skinlist.qc, embedded in dialog_settings_user.qc:16-31):
        // DialogMediaSkinList is the faithful file-scan backend (its own "Menu Skin" header, the gfx/menu/*/skinvalues.txt
        // list, and the "Set skin" button). It supersedes the old 3-name TextSlider approximation. Falls back to the
        // TextSlider only with no VFS — but the dialog itself renders an honest "no skins found" note in that case, so
        // we just embed it directly (matching QC, which embeds makeXonoticSkinList here).
        left.AddChild(new DialogMediaSkinList());

        // --- RIGHT: Text Language (QC makeXonoticHeaderLabel("Text Language") + makeXonoticLanguageList + button) ---
        right.AddChild(Ui.Header("Text Language"));

        // QC makeXonoticLanguageList -> _menu_prvm_language. Now data-driven from languages.txt (the localized
        // name is the label, the id is the stored value), like QC; falls back to a small set headlessly. The
        // option labels are language self-names, so they must NOT be translated (passed via AddRaw).
        var lang = Widgets.TextSlider("_menu_prvm_language");
        var langs = Localization.LoadLanguages();
        if (langs.Count > 0)
            foreach (var l in langs)
                lang.AddRaw(l.Localized, l.Id);
        else
            foreach ((string label, string value) in LanguagesFallback)
                lang.AddRaw(label, value);
        right.AddChild(Ui.Row("Language:", lang));

        // QC makeXonoticButton("Set language") -> SetLanguage_Click ->
        // prvm_language "$_menu_prvm_language"; menu_restart; menu_cmd languageselect.
        right.AddChild(Widgets.CommandButton("Set language",
            "prvm_language \"$_menu_prvm_language\"; menu_restart; menu_cmd languageselect"));

        right.AddChild(Ui.Spacer());

        // --- Gentle mode (QC trio of checkboxes, below the language section in the same column) ---
        // QC makeXonoticCheckBox_T(0, "cl_gentle", …) — disables gore and harsh language.
        right.AddChild(Widgets.CheckBox("cl_gentle", "Disable gore effects and harsh language",
            "Replace blood and gibs with content that does not have any gore effects"));

        // QC makeXonoticCheckBox(0, "cl_gentle_gibs", "Just the gore"); makeMulti(e, "cl_gentle_damage").
        // The QC widget drives BOTH cl_gentle_gibs and cl_gentle_damage (makeMulti). The toolkit has no
        // multi-cvar widget, so we render the primary cvar (cl_gentle_gibs); see note in summary.
        var justGore = Widgets.CheckBox("cl_gentle_gibs", "Just the gore");
        right.AddChild(justGore);
        Dependent.Bind(justGore, "cl_gentle", 0, 0); // QC setDependent(e,"cl_gentle",0,0)

        // QC makeXonoticCheckBox(0, "cl_gentle_messages", "Just the language").
        var justLang = Widgets.CheckBox("cl_gentle_messages", "Just the language");
        right.AddChild(justLang);
        Dependent.Bind(justLang, "cl_gentle", 0, 0); // QC setDependent(e,"cl_gentle",0,0)
    }
}
