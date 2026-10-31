using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "User defined key bind" — <c>XonoticUserbindEditDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings_input_userbind.qc)
/// on its 4 x 3 grid, 0.7 of the menu wide: "Name:", "Command when pressed:" and "Command when released:", each with
/// its box over the other two columns, then "Save" and "Cancel" half the width each. It edits the user bind the
/// Input tab's key list has selected (KeyBinder_Bind_Edit / loadUserBind); "Save" writes its
/// <c>_description</c>, <c>_press</c> and <c>_release</c> cvars (editUserbind) and closes it.
/// </summary>
public partial class DialogUserbindEdit : MenuScreen, IQcDialog
{
    private readonly string _userbind;
    private LineEdit _nameBox = null!, _pressBox = null!, _releaseBox = null!;

    /// <param name="userbind">The bind's cvar prefix, e.g. "userbind1".</param>
    public DialogUserbindEdit(string userbind)
    {
        _userbind = userbind;
    }

    public string QcTitle => Localization.Tr("User defined key bind");
    public float QcIntendedWidth => 0.7f;
    public float QcRows => 4;

    protected override void BuildUi()
    {
        Name = "DialogUserbindEdit"; // QC XonoticUserbindEditDialog

        var me = new QcGrid(QcRows, 3);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 1, Qc.Label("Name:"));
            _nameBox = me.TD(1, me.Columns - 1, Box(_userbind + "_description"));
        me.TR();
            me.TD(1, 1, Qc.Label("Command when pressed:"));
            _pressBox = me.TD(1, me.Columns - 1, Box(_userbind + "_press"));
        me.TR();
            me.TD(1, 1, Qc.Label("Command when released:"));
            _releaseBox = me.TD(1, me.Columns - 1, Box(_userbind + "_release"));
        me.TR();
            me.TD(1, me.Columns / 2, Qc.Button("Save", Save));
            me.TD(1, me.Columns / 2, Qc.Button("Cancel", GoBack));
    }

    /// <summary>makeXonoticInputBox(0, string_null) loaded with the cvar's text, the caret at its end.</summary>
    private static LineEdit Box(string cvar)
    {
        string text = MenuState.Cvars.GetString(cvar);
        var box = QcLook.InputBox(new LineEdit { Text = text });
        box.CaretColumn = text.Length;
        return box;
    }

    /// <summary>XonoticUserbindEditDialog_Save: editUserbind, then Dialog_Close.</summary>
    private void Save()
    {
        MenuState.Cvars.Set(_userbind + "_description", _nameBox.Text);
        MenuState.Cvars.Set(_userbind + "_press", _pressBox.Text);
        MenuState.Cvars.Set(_userbind + "_release", _releaseBox.Text);
        foreach (string suffix in new[] { "_description", "_press", "_release" })
            MenuState.Cvars.MarkArchived(_userbind + suffix); // seta in binds-xonotic.cfg
        GoBack();
    }
}
