namespace VortexArena.Game.Menu;

/// <summary>
/// The User settings tab — <c>XonoticUserSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_user.qc), TD for TD
/// on its 15.5 x 6 grid: the menu skin list (<see cref="QcSkinList"/>) and "Set skin" on the left, the text language
/// list (<see cref="QcLanguageList"/>) and "Set language" on the right, the gore options under it.
/// </summary>
public partial class DialogSettingsUser : QcTab
{
    public DialogSettingsUser() : base(rows: 15.5f, columns: 6f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        TR();
            TDempty(0.25f);
            TD(1, 2.5f, Qc.Header("Menu Skin"));
        TR();
            TDempty(0.25f);
            var skins = TD(Rows - 3.5f, 2.5f, new QcSkinList());
        GotoRC(Rows - 2.5f, 0.25f);
            TD(1, 2.5f, Qc.Button("Set skin", skins.SetSkin));

        GotoRC(0, 3.65f); SetFirstColumn(CurrentColumn);
            TD(1, 1.7f, Qc.Header("Text Language"));
        TR();
            var languages = TD(8, 1.7f, new QcLanguageList());

        GotoRC(9, 3.65f); SetFirstColumn(CurrentColumn);
            TD(1, 1.7f, Qc.Button("Set language", languages.SetLanguage));

        GotoRC(11, 3.25f); SetFirstColumn(CurrentColumn);
            TD(1, 2.5f, Qc.Check(0, "cl_gentle", "Disable gore effects and harsh language",
                "Replace blood and gibs with content that does not have any gore effects"));
        TR();
            TDempty(0.2f);
            TD(1, 2.3f, Qc.Check(0, "cl_gentle_gibs", "Just the gore"))
                .Multi("cl_gentle_gibs", "cl_gentle_damage").Dep("cl_gentle", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.3f, Qc.Check(0, "cl_gentle_messages", "Just the language")).Dep("cl_gentle", 0, 0);
    }
}
