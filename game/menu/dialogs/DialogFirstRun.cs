using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The first-run dialog ("Welcome") — <c>XonoticFirstRunDialog_fill</c> (qcsrc/menu/xonotic/dialog_firstrun.qc) on its
/// 16 x 6 grid, 0.8 of the menu wide, TD for TD: the wrapped greeting; "Name:" with the name as it will look
/// (colour codes drawn), the <c>_cl_name</c> box, the colour picker and character map under it; "Text language:" and
/// the language list on the right; the stats-nickname question with Yes / No / Undecided on
/// <c>cl_allow_uid2name</c>; and "Save settings" (<c>prvm_language "$_menu_prvm_language"; saveconfig;
/// menu_restart</c>), enabled once something was chosen (CheckFirstRunButton).
///
/// Shown once, on the first launch (shouldShow: <c>_cl_name</c> still at its default).
/// </summary>
public partial class DialogFirstRun : MenuScreen, IQcDialog
{
    public bool QcClosable => false;

    public string QcTitle => Localization.Tr("Welcome");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 16;

    protected override void BuildUi()
    {
        Name = "DialogFirstRun"; // QC XonoticFirstRunDialog

        var me = new QcGrid(QcRows, 6);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
        me.TR();
            me.TDempty(1);
            me.TD(2, 4, new QcTextLabel("Welcome to Xonotic, please select your language preference and enter your player name to get started.  You can change these options later through the menu system.")
            {
                AllowWrap = true, WrapSpacing = 0.5f,
            });
        me.TR();

        me.TR();
        me.TR();
            me.TD(1, 0.5f, Qc.Label("Name:"));
            // The name as it will look: allowCut, allowColors, alpha 1, its text the box's (label.textEntity = box).
            var label = me.TD(1, 3.25f, new QcLabel("")
            {
                AllowColors = true, Color = MenuSkin.SkinColor("COLOR_TEXT", 0.96f, 0.99f, 1f),
            });
        me.TR();
            var box = Widgets.InputBox("_cl_name", "", "Name under which you will appear in the game");
            QcLook.InputBox(box, clearButton: false, editColorCodes: true); // makeXonoticInputBox_T(1, ...), enableClearButton false
            box.TextChanged += t => label.Text = t;
            box.TreeEntered += () => label.Text = box.Text;
            me.TD(1, 3.75f, box);
        me.TR();
            var picker = HslColorPicker.ForNameBox(box);
            picker.CustomMinimumSize = Vector2.Zero;
            me.TD(5, 1.25f, picker);
            me.TD(5, 2.5f, new CharmapPicker(box));
        me.TR();
        me.TR();
        me.TR();
        me.TR();

        me.GotoRC(3, 4); me.SetFirstColumn(me.CurrentColumn);
        me.TR();
            me.TD(1, 2, Qc.Label("Text language:"));
        me.TR();
            me.TD(6, 2, new QcLanguageList());
        me.TR();
        me.TR();

        me.GotoRC(me.Rows - 4, 0); me.SetFirstColumn(me.CurrentColumn);
        me.TD(1, me.Columns, Qc.Label("Allow player statistics to use your nickname at stats.xonotic.org?", 0.5f));

        me.GotoRC(me.Rows - 3, 0);
        var uid2name = new QcRadioGroup();
        me.TDempty(1.5f);
        me.TD(1, 1, Qc.Radio(uid2name, "cl_allow_uid2name", "1", "Yes"));
        me.TD(1, 1, Qc.Radio(uid2name, "cl_allow_uid2name", "0", "No"));
        me.TD(1, 1, Qc.Radio(uid2name, "cl_allow_uid2name", "-1", "Undecided"));
        me.TR();
        me.TD(1, me.Columns, Qc.Label("Player statistics are enabled by default, you can change this in the Profile menu", 0.5f));

        // because of the language selector, this is a menu_restart!
        me.GotoRC(me.Rows - 1, 0);
        var save = Qc.Command("Save settings", "prvm_language \"$_menu_prvm_language\"; saveconfig; menu_restart");
        save.Pressed += MenuState.SaveUserConfig; // saveconfig against the port's own cvar store
        me.TD(1, me.Columns, save).DepFunc(CheckFirstRunButton);
    }

    /// <summary>CheckFirstRunButton: a name, a language other than the running one, or a stats answer was given.</summary>
    private static bool CheckFirstRunButton()
        => MenuState.Cvars.GetString("_cl_name") != MenuState.Cvars.GetDefault("_cl_name")
           || MenuState.Cvars.GetString("_menu_prvm_language") != MenuState.Cvars.GetString("prvm_language")
           || MenuState.Cvars.GetString("cl_allow_uid2name") != "-1";
}
