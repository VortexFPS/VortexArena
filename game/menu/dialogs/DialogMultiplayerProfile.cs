using System.Globalization;
using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The multiplayer Profile tab/dialog — a faithful C# port of <c>XonoticProfileTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_profile.qc), laid out on a <see cref="QcGrid"/> with the QC's own TD
/// calls (intendedWidth 0.9, 23 rows, 6.1 columns):
/// <list type="bullet">
/// <item>LEFT — the Name section (header, the bold colour-coded name preview, the <c>_cl_name</c> box, then
/// the HSL colorpicker beside the charmap, both typing into the box), then the Model section: the Glowing /
/// Detail colour buttons (15 <c>XonoticColorButton</c>s each, editing the two <c>_cl_color</c> nibbles) on the
/// left with the player-model selector (&lt;&lt; preview &gt;&gt;) beside them — QC's "MODEL RIGHT, COLOR LEFT".</item>
/// <item>RIGHT — the Statistics section (the three <c>cl_allow_uid*</c> opt-ins + the stats list, empty until
/// the stats backend exists) and the "Select language..." button.</item>
/// </list>
/// Bottom row: the full-width "Apply immediately" command button (same command string the QC issues), disabled
/// until something it applies changes and again once pressed (QC <c>disableOnClick</c>).
/// </summary>
public partial class DialogMultiplayerProfile : MenuScreen
{
    // QC profileApplyButton: makeXonoticCommandButton(_("Apply immediately"), ..., COMMANDBUTTON_APPLY).
    private const string ApplyCommand =
        "color -1 -1; name \"$_cl_name\"; playermodel $_cl_playermodel; playerskin $_cl_playerskin";

    public bool Embedded { get; set; }

    private CommandButton _apply = null!;

    protected override void BuildUi()
    {
        Name = "DialogMultiplayerProfile";

        var tab = new QcGrid(rows: 23, columns: 6.1f, intendedWidth: 0.9f);
        tab.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(tab);

        _apply = QcLook.Button(Widgets.CommandButton("Apply immediately", ApplyCommand));
        _apply.Disabled = true; // Button_resizeNotify: "if(me.disableOnClick) me.disabled = true; // initially disabled"
        _apply.Pressed += () => _apply.Disabled = true;

        // ==============
        //  NAME SECTION
        // ==============
        tab.GotoRC(0.5f, 0f);
        tab.TD(1, 3, Header("Name"));

        tab.GotoRC(1.5f, 0f);
        // The name as it will look: bold, at SKINFONTSIZE_TITLE, its colour codes drawn as colours, opaque.
        var preview = tab.TD(1, 3, new QcLabel("", 0.5f)
        {
            Bold = true, FontSize = MenuMetrics.FontTitle, AllowColors = true, Color = Colors.White,
        });

        tab.GotoRC(2.5f, 0f);
        var nameBox = Widgets.InputBox("_cl_name", "", "Name under which you will appear in the game");
        QcLook.InputBox(nameBox, clearButton: false, editColorCodes: true); // makeXonoticInputBox_T(1, ...), no clear button
        nameBox.TextChanged += t => preview.Text = t;  // label.textEntity = box
        preview.Text = nameBox.Text;
        nameBox.TreeEntered += () => preview.Text = nameBox.Text;
        tab.TD(1, 3f, nameBox);
        tab.TR();
        var picker = HslColorPicker.ForNameBox(nameBox);
        picker.CustomMinimumSize = Vector2.Zero;
        tab.TD(5, 1, picker);
        tab.TD(5, 2, new CharmapPicker(nameBox));

        // ===============
        //  MODEL SECTION
        // ===============
        tab.GotoRC(9f, 0f);
        tab.SetFirstColumn(tab.CurrentColumn); // BOTTOM LEFT
        tab.TD(1, 3, Header("Model"));
        tab.TR();
        tab.TDempty(1); // MODEL RIGHT, COLOR LEFT
        var pms = new PlayerModelSelector();
        tab.TD(1, 0.3f, SmallButton("<<", pms.Prev));
        tab.TD(11.5f, 1.4f, pms);
        tab.TD(1, 0.3f, SmallButton(">>", pms.Next));

        tab.GotoRC(tab.CurrentRow, 0f);
        tab.SetFirstColumn(tab.CurrentColumn);
        tab.TR();
        tab.TD(1, 1, Header("Glowing color"));
        for (int i = 0; i < 15; ++i)
        {
            if (i % 5 == 0) tab.TR();
            tab.TDNoMargin(1, 0.2f, new QcColorButton(pants: false, i), new Vector2(0f, 1f));
        }
        tab.TR();
        tab.TR();
        tab.TD(1, 1, Header("Detail color"));
        for (int i = 0; i < 15; ++i)
        {
            if (i % 5 == 0) tab.TR();
            tab.TDNoMargin(1, 0.2f, new QcColorButton(pants: true, i), new Vector2(0f, 1f));
        }

        // ====================
        //  STATISTICS SECTION
        // ====================
        tab.GotoRC(0.5f, 3.1f);
        tab.SetFirstColumn(tab.CurrentColumn); // TOP RIGHT
        tab.TD(1, 3, Header("Statistics"));
        tab.TR();
        tab.TDempty(0.25f);
        tab.TD(1, 2.5f, QcLook.CheckBox(Widgets.CheckBox("cl_allow_uidtracking", "Allow player statistics to track your client")));
        tab.TR();
        tab.TDempty(0.25f);
        var uid2name = tab.TD(1, 2.5f, QcLook.CheckBox(Widgets.CheckBox("cl_allow_uid2name", "Allow player statistics to use your nickname")));
        Dependent.Bind(uid2name, "cl_allow_uidtracking", 1, 1);
        tab.TR();
        tab.TDempty(0.25f);
        var uidranking = tab.TD(1, 2.5f, QcLook.CheckBox(Widgets.CheckBox("cl_allow_uidranking", "Allow player statistics to rank you in leaderboards")));
        Dependent.Bind(uidranking, "cl_allow_uidtracking", 1, 1);
        tab.GotoRC(4.5f, 3.1f);
        tab.TDempty(0.25f);
        // makeXonoticStatsList(): filled asynchronously by an HTTP fetch from the stats server
        // (PlayerStats_PlayerDetail_CheckUpdate). The port has no player-stats backend, so it stays empty - which
        // is also what Xonotic shows until the fetch returns. The update is still asked for, as showNotify does.
        tab.TD(9, 2.5f, new QcStatsList());
        MenuCommand.Run("menu_cmd playerstats_update");

        // =================
        //  COUNTRY SECTION
        // =================
        tab.GotoRC(16f, 3.1f);
        tab.SetFirstColumn(tab.CurrentColumn);
        tab.TR();
        tab.TDempty(0.75f);
        tab.TD(1, 1.5f, QcLook.Button(Widgets.CommandButton("Select language...", "menu_cmd languageselect")));

        tab.GotoRC(tab.Rows - 1f, 0f);
        tab.TD(1, tab.Columns, _apply);
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        MenuState.Cvars.Changed += OnCvarChanged;
    }

    public override void _ExitTree()
    {
        MenuState.Cvars.Changed -= OnCvarChanged;
        base._ExitTree();
    }

    // QC applyButton: every control the Apply command reads re-enables it when changed.
    private void OnCvarChanged(string name)
    {
        if (_apply is not null && name is "_cl_name" or "_cl_color" or "_cl_playermodel" or "_cl_playerskin")
            _apply.Disabled = false;
    }

    /// <summary>makeXonoticHeaderLabel: bold, centred, SKINCOLOR_HEADER @ SKINALPHA_HEADER.</summary>
    private static QcLabel Header(string text) => new(text, 0.5f) { Bold = true, Color = MenuSkin.Header };

    private static Button SmallButton(string text, System.Action pressed)
    {
        var b = QcLook.Button(new Button { Text = text });
        b.Pressed += pressed;
        return b;
    }
}

/// <summary>
/// xonotic/colorbutton.qc: one swatch of the Glowing / Detail colour palette — the colorbutton picture (n, f, or c
/// while it is the chosen one) filling its cell, tinted with the palette colour it stands for. The 15 buttons
/// are not in palette order: <c>configureXonoticColorButton</c> rearranges 1..14 into rainbow order.
/// </summary>
public partial class QcColorButton : Button
{
    private const string Cvar = "_cl_color";
    private static readonly int[] RainbowOrder = { 0, 10, 4, 1, 14, 12, 7, 3, 2, 6, 5, 13, 11, 8, 9 };

    private static CvarService Cvars => MenuState.Cvars;

    private readonly bool _pants;
    private readonly int _value;
    private readonly Color _tint;
    private bool _hover;

    public QcColorButton(bool pants, int index)
    {
        _pants = pants;
        _value = RainbowOrder[index];
        (float r, float g, float b) = CsqcModelAppearance.ColormapPaletteColor(_value, pants, 0f);
        _tint = new Color(r, g, b);
        FocusMode = FocusModeEnum.None;
        Flat = true;
        foreach (string s in new[] { "normal", "hover", "pressed", "focus", "hover_pressed", "disabled" })
            AddThemeStyleboxOverride(s, new StyleBoxEmpty());
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
        Pressed += OnPick;
    }

    public override void _EnterTree() { Cvars.Changed += OnCvarChanged; QueueRedraw(); }
    public override void _ExitTree() { Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == Cvar) QueueRedraw(); }

    private bool Chosen()
    {
        int packed = (int)Cvars.GetFloat(Cvar);
        return (_pants ? (packed & 240) / 16 : packed & 15) == _value;
    }

    private void OnPick()
    {
        int packed = (int)Cvars.GetFloat(Cvar);
        // Rewrite only our nibble, preserving the other (XonoticColorButton_saveCvars).
        packed = _pants ? (packed & 15) + _value * 16 : (packed & 240) + _value;
        Cvars.Set(Cvar, packed.ToString(CultureInfo.InvariantCulture));
        Cvars.MarkArchived(Cvar);
    }

    public override void _Draw()
    {
        // useDownAsChecked: the chosen swatch is drawn pressed.
        string state = Chosen() || IsPressed() ? "_c" : _hover ? "_f" : "_n";
        if (MenuSkin.SkinImage("colorbutton" + state) is { } tex)
            DrawTextureRect(tex, new Rect2(Vector2.Zero, Size), false, _tint);
        else
            DrawRect(new Rect2(Vector2.Zero, Size), _tint);
    }

    public override Vector2 _GetMinimumSize() => Vector2.Zero;
}

/// <summary>xonotic/statslist.qc's list box with nothing in it: the backdrop and the scrollbar groove.</summary>
public partial class QcStatsList : MenuListBox
{
    public QcStatsList()
    {
        ItemHeight = 1.4f * MenuMetrics.FontNormalU; // rowsPerItem 1.4
        SelectionDoesntMatter = true;
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused) { }
}
