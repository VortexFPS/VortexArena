using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Advanced settings" — the raw cvar list editor, a faithful C# port of <c>XonoticCvarsDialog</c>
/// (qcsrc/menu/xonotic/dialog_settings_misc_cvars.qc). The QC dialog is a live, scrollable table of every
/// engine cvar (<c>makeXonoticCvarList</c>) with:
///   * a "Filter:" input box that filters the list as you type (QC <c>CvarList_Filter_Change</c>),
///   * a "Modified cvars only" checkbox bound to <c>menu_cvarlist_onlymodified</c>,
///   * a "Search in cvar descriptions too" checkbox bound to <c>menu_cvarlist_descriptions</c>,
///   * a result-count label,
///   * and a detail panel for the selected row: Setting (name), Type, an editable Value box, a
///     "Reset to default" button (QC <c>CvarList_Revert_Click</c>), and a Description.
///
/// FAITHFUL UI NOW: the full scrolling cvar TABLE needs a list/enumeration widget backend VortexArena's menu does
/// not have yet, so the live list area is rendered with an honest note instead of a fabricated row set. The
/// rest is REAL against the shared <see cref="MenuState.Cvars"/> store: the two filter checkboxes are bound
/// cvars; the "Filter:" box is live; and the detail panel is a working by-name editor — type a cvar name to
/// load its current value (and type/modified state), edit the Value to write it, or "Reset to default" to
/// revert it (QC CvarList_Revert_Click → <c>cvar_set(name, default)</c>). The QC "OK" button just closed the
/// dialog (<c>Dialog_Close</c>); here that is the standard Back. QC title "Advanced settings".
/// </summary>
public partial class DialogCvarList : MenuScreen
{
    private CvarService Cvars => MenuState.Cvars;

    // Detail-panel widgets (the QC cvarNameBox/cvarTypeBox/cvarValueBox/cvarDescriptionBox + Reset button).
    private LineEdit _nameBox = null!;
    private Label _typeBox = null!;
    private LineEdit _valueBox = null!;
    private Label _descriptionBox = null!;
    private Button _resetButton = null!;
    private bool _updatingValue;

    protected override void BuildUi()
    {
        Name = "DialogCvarList"; // QC XonoticCvarsDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Advanced settings"));

        // ---- Filter row (QC: "Filter:" input + "Modified cvars only" checkbox) ---------------------------
        // The filter box is live; with no enumerable list to filter yet it filters the by-name lookup below
        // (typing a full cvar name loads it). Bound checkboxes persist to the menu_cvarlist_* cvars.
        var filter = Widgets.InputBox("", "Filter…", "Filter the cvar list");
        // Typing a complete cvar name into the filter loads it into the detail panel (the closest live behavior
        // to the QC's filter-then-select flow without a list widget).
        filter.TextSubmitted += OnFilterSubmitted;
        root.AddChild(Ui.Row("Filter:", filter, labelMinWidth: 120f));

        root.AddChild(Widgets.CheckBox("menu_cvarlist_onlymodified", "Modified cvars only"));
        root.AddChild(Widgets.CheckBox("menu_cvarlist_descriptions", "Search in cvar descriptions too"));

        // ---- The cvar list area (QC makeXonoticCvarList) — honest placeholder note --------------------------
        var listPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        var listPad = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            listPad.AddThemeConstantOverride(side, 16);
        var listNote = MakeLabel(
            "(live cvar list — the scrolling cvar table needs a list-widget backend VortexArena's menu does not " +
            "have yet. Use the editor below to inspect or change a cvar by name.)");
        listNote.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        listNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        listNote.HorizontalAlignment = HorizontalAlignment.Center;
        listNote.VerticalAlignment = VerticalAlignment.Center;
        listNote.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        listNote.SizeFlagsVertical = SizeFlags.ExpandFill;
        listPad.AddChild(listNote);
        listPanel.AddChild(listPad);
        root.AddChild(listPanel);

        root.AddChild(Ui.Spacer());

        // ---- Detail panel (QC: Setting / Type / Value + Reset / Description) -------------------------------
        // "Setting:" — QC cvarNameBox (read-only label of the selected name). Here it's the by-name input that
        // drives the rest of the panel, since there is no list selection to source the name from.
        _nameBox = new LineEdit { PlaceholderText = "cvar name…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _nameBox.TextSubmitted += name => LoadCvar(name);
        root.AddChild(Ui.Row("Setting:", _nameBox, labelMinWidth: 120f));

        // "Type:" — QC cvarTypeBox.
        _typeBox = MakeLabel("");
        root.AddChild(Ui.Row("Type:", _typeBox, labelMinWidth: 120f));

        // "Value:" — QC cvarValueBox (editable) + "Reset to default" button (QC CvarList_Revert_Click).
        _valueBox = new LineEdit { PlaceholderText = "value…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _valueBox.TextChanged += OnValueChanged;
        _resetButton = MakeButton("Reset to default", OnResetToDefault);
        _resetButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _resetButton.CustomMinimumSize = new Vector2(160, 36);

        var valueRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        valueRow.AddThemeConstantOverride("separation", 10);
        var valueLabel = MakeLabel("Value:");
        valueLabel.CustomMinimumSize = new Vector2(120, 0);
        valueLabel.VerticalAlignment = VerticalAlignment.Center;
        valueRow.AddChild(valueLabel);
        valueRow.AddChild(_valueBox);
        valueRow.AddChild(_resetButton);
        root.AddChild(valueRow);

        // "Description:" — QC cvarDescriptionBox (wrapped).
        _descriptionBox = MakeLabel("");
        _descriptionBox.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _descriptionBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(Ui.Row("Description:", _descriptionBox, labelMinWidth: 120f));

        // QC "OK" button (Dialog_Close) — the universal Back.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));

        SetDetailEnabled(false); // nothing loaded yet.
    }

    public override void _EnterTree() => Cvars.Changed += OnCvarChanged;
    public override void _ExitTree() => Cvars.Changed -= OnCvarChanged;

    // Keep the Value box in sync if the loaded cvar changes elsewhere (mirrors the QC live binding).
    private void OnCvarChanged(string name)
    {
        if (_nameBox is null || name != CurrentName() || _updatingValue) return;
        _updatingValue = true;
        _valueBox.Text = Cvars.GetString(name);
        _updatingValue = false;
    }

    private string CurrentName() => _nameBox?.Text?.Trim() ?? "";

    /// <summary>Filter submit loads the named cvar if it exists (the closest live analog to the QC list).</summary>
    private void OnFilterSubmitted(string text)
    {
        string name = text.Trim();
        if (name.Length > 0 && Cvars.Has(name))
            LoadCvar(name);
    }

    /// <summary>Load a cvar by name into the detail panel: Type/Value/Description (QC selection → detail boxes).</summary>
    private void LoadCvar(string rawName)
    {
        string name = rawName.Trim();
        _nameBox.Text = name;
        if (name.Length == 0)
        {
            SetDetailEnabled(false);
            _typeBox.Text = "";
            _valueBox.Text = "";
            _descriptionBox.Text = "";
            return;
        }

        bool known = Cvars.Has(name);
        _updatingValue = true;
        _valueBox.Text = known ? Cvars.GetString(name) : "";
        _updatingValue = false;

        // QC cvarTypeBox shows the cvar's storage type; the store does not expose per-cvar metadata, so we
        // report whether it currently differs from its default (the same signal the "Modified cvars only"
        // filter surfaces) rather than fabricating a C/string/float type tag.
        _typeBox.Text = known
            ? (Cvars.IsModified(name) ? "modified (differs from default)" : "default")
            : "(unknown cvar)";

        // QC cvarDescriptionBox: per-cvar help text — the store has no description table, so note that honestly.
        _descriptionBox.Text = known
            ? "(cvar descriptions are not available — no description table wired in yet)"
            : "(no such cvar)";

        SetDetailEnabled(known);
    }

    /// <summary>QC cvarValueBox onChange: write the edited value to the cvar (CvarList_Value_Change).</summary>
    private void OnValueChanged(string newText)
    {
        if (_updatingValue) return;
        string name = CurrentName();
        if (name.Length == 0 || !Cvars.Has(name)) return;
        Cvars.Set(name, newText);
        Cvars.MarkArchived(name);
        _typeBox.Text = Cvars.IsModified(name) ? "modified (differs from default)" : "default";
    }

    /// <summary>QC "Reset to default" (CvarList_Revert_Click): revert the selected cvar to its default value.</summary>
    private void OnResetToDefault()
    {
        string name = CurrentName();
        if (name.Length == 0 || !Cvars.Has(name)) return;
        Cvars.ResetToDefault(name);
        _updatingValue = true;
        _valueBox.Text = Cvars.GetString(name);
        _updatingValue = false;
        _typeBox.Text = "default";
    }

    private void SetDetailEnabled(bool enabled)
    {
        _valueBox.Editable = enabled;
        _resetButton.Disabled = !enabled;
        float a = enabled ? 1f : 0.5f;
        _valueBox.Modulate = new Color(1, 1, 1, a);
        _resetButton.Modulate = new Color(1, 1, 1, a);
    }
}
