using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Advanced settings" — <c>XonoticCvarsDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings_misc_cvars.qc) on its
/// 24 x 6 grid, 0.8 of the menu wide, every control in SKINCOLOR_CVARLIST_CONTROLS: "Filter:" and its box beside
/// "Modified cvars only"; the result count beside "Search in cvar descriptions too"; the cvar list
/// (<see cref="QcCvarList"/>) over rows 2..15; then Setting (the name in yellow), Type, Value with its
/// reset button (labelled with the default value, tooltip "Reset to default"), Description (wrapped), and "OK".
///
/// The list is every cvar of the shared <see cref="MenuState.Cvars"/> store except the hidden "_" ones, filtered
/// as CvarList_Load does (a plain filter matches anywhere, case-insensitively; descriptions searched on request;
/// modified-only on request). Editing the value sets the cvar; a cvar that would not be saved is forced into the
/// config (<c>seta</c>, menu_forced_saved_cvars), and reverting a forced one undoes that
/// (menu_reverted_nonsaved_cvars). The store keeps no engine/private/read-only flags, so the Type line names only
/// the save state and the value box is never disabled as read-only.
/// </summary>
public partial class DialogCvarList : MenuScreen, IQcDialog
{
    // dialog_settings_misc_cvars.qh
    public string QcTitle => Localization.Tr("Advanced settings");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 24;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_CVARS", 1f, 0.2f, 0.15f);

    private static CvarService Cvars => MenuState.Cvars;

    private QcCvarList _list = null!;
    private LineEdit _filter = null!;
    private QcLabel _resultCount = null!;
    private QcTextLabel _nameBox = null!;
    private QcLabel _typeBox = null!;
    private LineEdit _valueBox = null!;
    private Button _defaultBox = null!;
    private QcTextLabel _descriptionBox = null!;

    // XonoticCvarList's cvarName / cvarDefault / cvarNeedsForcing.
    private string _cvarName = "";
    private string _cvarDefault = "";
    private bool _needsForcing;

    protected override void BuildUi()
    {
        Name = "DialogCvarList"; // QC XonoticCvarsDialog

        Color controls = MenuSkin.SkinColor("COLOR_CVARLIST_CONTROLS", 1f, 0.2f, 0.15f);

        var me = new QcGrid(QcRows, 6);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        _list = new QcCvarList();
        _list.Selected += OnSelected;
        _list.EnterPressed += () => { if (_valueBox.Editable) _valueBox.GrabFocus(); };

        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 0.8f, Qc.Label("Filter:"));
            _filter = me.TD(1, 3, QcLook.InputBox(new LineEdit(), pictureTint: controls));
                _filter.TextChanged += _ => { Reload(); _list.Select(0); }; // CvarList_Filter_Change
            var modified = me.TD(1, 2, CheckBox("menu_cvarlist_onlymodified", "Modified cvars only", controls));
                modified.Toggled += _ => { Reload(); _list.Select(0); }; // CvarList_Filter_ModifiedCvars
        me.TR();
            _resultCount = me.TD(1, 1, Qc.Label(""));
            var descriptions = me.TD(1, 3, CheckBox("menu_cvarlist_descriptions", "Search in cvar descriptions too", controls));
                descriptions.Toggled += _ => { Reload(); _list.Select(0); }; // CvarList_Filter_Descriptions
        me.TR();
            me.TD(me.Rows - me.CurrentRow - 8, me.Columns, _list);
        me.GotoRC(me.Rows - 8, 0);
            me.TD(1, 1, Qc.Label("Setting:"));
            _nameBox = me.TD(1, me.Columns - 1, new QcTextLabel { Color = MenuSkin.TextLift(new Color(1, 1, 0, MenuSkin.Text.A)) });
        me.TR();
            me.TD(1, 1, Qc.Label("Type:"));
            _typeBox = me.TD(1, me.Columns - 1, Qc.Label(""));
        me.TR();
            me.TD(1, 1, Qc.Label("Value:"));
            _valueBox = me.TD(1, me.Columns - 2, QcLook.InputBox(new LineEdit(), pictureTint: controls));
                _valueBox.TextChanged += _ => OnValueChanged();                  // CvarList_Value_Change
                _valueBox.TextSubmitted += _ => _list.GrabFocus();               // CvarList_End_Editing
            _defaultBox = me.TD(1, 1, Qc.Button("", OnRevert, "Reset to default", controls)); // CvarList_Revert_Click
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Label("Description:"));
            _descriptionBox = me.TD(1, me.Columns - 1, new QcTextLabel { AllowWrap = true });
        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns, Qc.Button("OK", GoBack, tint: controls));

        // showNotify: load with the filter, and select the first row the first time.
        Reload();
        _list.Select(0);
    }

    /// <summary>makeXonoticCheckBox(0, cvar, text) with the dialog's control colour.</summary>
    private static CvarValueCheckBox CheckBox(string cvar, string text, Color tint)
        => QcLook.CheckBox(Widgets.ValueCheckBox(cvar, 1f, 0f, text), pictureTint: tint);

    /// <summary>CvarList_Load + CvarList_UpdateResults.</summary>
    private void Reload()
    {
        _list.Load(_filter.Text);
        int c = _list.ItemCount;
        _resultCount.Text = c == 1 ? Localization.Tr("1 result") : Localization.Tr("%d results").Replace("%d", c.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>XonoticCvarList_setSelected: fill the boxes under the list from the selected cvar.</summary>
    private void OnSelected(string name)
    {
        _cvarName = name;
        _cvarDefault = Cvars.GetDefault(name);
        _nameBox.Text = name;
        _descriptionBox.Text = Cvars.GetDescription(name);
        bool needsForcing = UpdateType();
        _defaultBox.Text = _cvarDefault;

        _needsForcing = false;
        SetValueText(Cvars.GetString(name));
        _needsForcing = needsForcing;
        _valueBox.CaretColumn = _valueBox.Text.Length;
    }

    /// <summary>XonoticInputBox_setText: a new text runs onChange.</summary>
    private void SetValueText(string text)
    {
        if (_valueBox.Text == text) return;
        _valueBox.Text = text; // a LineEdit's Text setter does not emit TextChanged
        OnValueChanged();
    }

    /// <summary>XonoticCvarList_updateCvarType: the Type line; true when the cvar would not be saved.</summary>
    private bool UpdateType()
    {
        string name = _cvarName;
        bool needsForcing;
        var parts = new List<string>();
        if (HasWord(Cvars.GetString("menu_forced_saved_cvars"), name))
        {
            parts.Add(Localization.Tr("forced to be saved to config.cfg"));
            needsForcing = false;
        }
        else if (HasWord(Cvars.GetString("menu_reverted_nonsaved_cvars"), name))
        {
            parts.Add(Localization.Tr("will not be saved")); // currently claims to be saved, but won't be next time
            needsForcing = true;
        }
        else if (Cvars.IsArchived(name))
        {
            parts.Add(Localization.Tr("will be saved to config.cfg"));
            needsForcing = false;
        }
        else
        {
            parts.Add(Localization.Tr("will not be saved"));
            needsForcing = true;
        }
        _typeBox.Text = string.Join(", ", parts);
        return needsForcing;
    }

    /// <summary>CvarList_Value_Change.</summary>
    private void OnValueChanged()
    {
        if (_cvarName.Length == 0 || !_valueBox.Editable) return;
        Cvars.Set(_cvarName, _valueBox.Text);
        _list.QueueRedraw();
        if (!_needsForcing) return;
        // seta name "$name": make it saved, and remember that the menu did so.
        Cvars.MarkArchived(_cvarName);
        SetList("menu_reverted_nonsaved_cvars", RemoveWord(Cvars.GetString("menu_reverted_nonsaved_cvars"), _cvarName));
        string forced = Cvars.GetString("menu_forced_saved_cvars");
        SetList("menu_forced_saved_cvars", forced.Length == 0 ? _cvarName : forced + " " + _cvarName);
        _needsForcing = false;
        UpdateType();
    }

    /// <summary>CvarList_Revert_Click: back to the default; a cvar the menu forced to be saved is no longer.</summary>
    private void OnRevert()
    {
        if (_cvarName.Length == 0) return;
        SetValueText(_cvarDefault);
        _valueBox.CaretColumn = _cvarDefault.Length;
        string forced = Cvars.GetString("menu_forced_saved_cvars");
        if (HasWord(forced, _cvarName))
        {
            SetList("menu_forced_saved_cvars", RemoveWord(forced, _cvarName));
            string reverted = Cvars.GetString("menu_reverted_nonsaved_cvars");
            SetList("menu_reverted_nonsaved_cvars", reverted.Length == 0 ? _cvarName : reverted + " " + _cvarName);
            Cvars.UnmarkArchived(_cvarName);
        }
        _needsForcing = UpdateType();
    }

    private static void SetList(string cvar, string value)
    {
        Cvars.Set(cvar, value);
        Cvars.MarkArchived(cvar);
    }

    private static bool HasWord(string list, string word)
        => list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(word, StringComparer.Ordinal);

    private static string RemoveWord(string list, string word)
        => string.Join(' ', list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w != word));
}

/// <summary>
/// xonotic/cvarlist.qc's list: one row per cvar, the name and (in a 20-character column a character after it) the
/// value, each cut to fit — white while the value is the default and SKINCOLOR_CVARLIST_CHANGED once it is not, at
/// SKINALPHA_CVARLIST_SAVED for a cvar that will be saved and SKINALPHA_CVARLIST_TEMPORARY for one that will not.
/// The selected and hovered rows are filled as in every list.
/// </summary>
public partial class QcCvarList : MenuListBox
{
    private readonly List<string> _names = new();

    public QcCvarList()
    {
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
    }

    /// <summary>Raised by every setSelected with a cvar to show (the QC fills the boxes even for the same row).</summary>
    public event Action<string>? Selected;

    /// <summary>Enter on the list: the dialog moves the focus to the value box.</summary>
    public event Action? EnterPressed;

    private static CvarService Cvars => MenuState.Cvars;

    /// <summary>
    /// CvarList_Load: buf_cvarlist without the "_" cvars, sorted; a filter with no wildcard is matched anywhere
    /// (*filter*), lowercased; with "Modified cvars only" or a description search the names are filtered one by one.
    /// </summary>
    public void Load(string filter)
    {
        if (filter == "*")
            filter = "";
        else if (filter.Length > 0)
        {
            bool isPattern = filter.Contains('*') || filter.Contains('?');
            filter = filter.ToLowerInvariant();
            if (!isPattern) filter = "*" + filter + "*";
        }
        bool onlyModified = Cvars.GetFloat("menu_cvarlist_onlymodified") != 0f;
        bool descriptions = Cvars.GetFloat("menu_cvarlist_descriptions") != 0f;

        _names.Clear();
        foreach (string name in Cvars.Names)
        {
            if (name.StartsWith('_')) continue;
            if (onlyModified && Cvars.GetString(name) == Cvars.GetDefault(name)) continue;
            if (filter.Length > 0 && !ConsoleSearch.Glob(name.ToLowerInvariant(), filter)
                && (!descriptions || !ConsoleSearch.Glob(Cvars.GetDescription(name).ToLowerInvariant(), filter)))
                continue;
            _names.Add(name);
        }
        _names.Sort(StringComparer.Ordinal);
        ItemCount = _names.Count;
        QueueRedraw();
    }

    /// <summary>setSelected(i), always telling the dialog (QC XonoticCvarList_setSelected).</summary>
    public void Select(int i)
    {
        _selecting = true;
        SetSelected(i);
        _selecting = false;
        if (ItemCount > 0) Selected?.Invoke(_names[SelectedItem]);
    }

    private bool _selecting;

    public override void SetSelected(int i)
    {
        int before = SelectedItem;
        base.SetSelected(i);
        // A changed selection by mouse or key is reported here; Select reports its own.
        if (!_selecting && ItemCount > 0 && SelectedItem != before) Selected?.Invoke(_names[SelectedItem]);
    }

    protected override bool HandleKey(InputEventKey key)
    {
        if (key.Keycode is Key.Enter or Key.KpEnter)
        {
            EnterPressed?.Invoke();
            return true;
        }
        return base.HandleKey(key);
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _names.Count) return;
        string k = _names[index];
        string v = Cvars.GetString(k), d = Cvars.GetDefault(k);

        string forced = Cvars.GetString("menu_forced_saved_cvars"), reverted = Cvars.GetString("menu_reverted_nonsaved_cvars");
        float alpha;
        if (HasWord(forced, k)) alpha = MenuSkin.SkinNumber("ALPHA_CVARLIST_SAVED", 0.85f);
        else if (HasWord(reverted, k)) alpha = MenuSkin.SkinNumber("ALPHA_CVARLIST_TEMPORARY", 0.55f);
        else if (Cvars.IsArchived(k)) alpha = MenuSkin.SkinNumber("ALPHA_CVARLIST_SAVED", 0.85f);
        else alpha = MenuSkin.SkinNumber("ALPHA_CVARLIST_TEMPORARY", 0.55f);
        Color rgb = v == d
            ? MenuSkin.SkinColor("COLOR_CVARLIST_UNCHANGED", 1f, 1f, 1f)
            : MenuSkin.SkinColor("COLOR_CVARLIST_CHANGED", 0.95f, 0.05f, 0.05f);
        Color color = MenuSkin.TextLift(new Color(rgb, alpha));

        // XonoticCvarList_resizeNotify.
        float ch = MenuMetrics.FontNormalU;
        float valueSize = 20f * ch, nameSize = rect.Size.X - valueSize - ch, valueOrigin = nameSize + ch;
        float y = rect.Position.Y + 0.5f * (rect.Size.Y - ch);
        QcDraw.Text(this, new Vector2(rect.Position.X, y), QcDraw.ShortenToWidth(k, nameSize), color);
        QcDraw.Text(this, new Vector2(rect.Position.X + valueOrigin, y), QcDraw.ShortenToWidth(v, valueSize), color);
    }

    private static bool HasWord(string list, string word)
        => list.Length > 0 && Array.IndexOf(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), word) >= 0;
}
