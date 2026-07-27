using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using XonoticGodot.Common.Menu;   // CheckBoxValue (the QC value-pair checkbox math)
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The cvar-bound widget toolkit — the C# successors to Xonotic's <c>makeXonotic*</c> widget factories
/// (qcsrc/menu/xonotic/{checkbox,slider,textslider,radiobutton,inputbox,commandbutton}.qc). Every Xonotic
/// settings widget is bound to an engine cvar: it shows the cvar's current value, writes the cvar when the
/// user changes it, and several widgets on one cvar stay in sync. These controls reproduce that against the
/// shared <see cref="MenuState.Cvars"/> store: read on enter, write on change (marking the cvar archived so
/// it persists), and re-read on the store's <see cref="CvarService.Changed"/> event so dependents and
/// duplicates track. Use the <see cref="Widgets"/> factory methods to build them, mirroring the QC call
/// sites; use <see cref="Dependent"/> for the <c>setDependent</c> enable-on-another-cvar behavior.
/// </summary>
public static class Widgets
{
    // i18n: the widget label/tooltip/placeholder are user-facing text the QC built with _() — route them through
    // Localization.Tr here (the single seam for the bound widgets, mirroring Ui/MenuScreen for the layout helpers).

    /// <summary>QC <c>makeXonoticCheckBox(0, cvar, label)</c> — a cvar-bound on/off checkbox.</summary>
    public static CvarCheckBox CheckBox(string cvar, string label, string tooltip = "", string on = "1", string off = "0")
        => new(cvar, Localization.Tr(label), on, off) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>
    /// QC <c>makeXonoticCheckBoxEx(yesValue, noValue, cvar, label)</c> — a checkbox that stores one of two
    /// VALUES in the cvar (checkbox.qc:33-42; e.g. cl_bobfall 0.05/0, v_idlescale 1/0, cl_eventchase_death
    /// 2/0), with the QC midpoint load rule (<see cref="XonoticGodot.Common.Menu.CheckBoxValue"/>).
    /// <paramref name="multiCvars"/> = QC <c>makeMulti</c> (util.qc:92): extra cvars written with the same
    /// stored value on toggle (the View tab's cl_bob checkbox also drives cl_bob2).
    /// </summary>
    public static CvarValueCheckBox ValueCheckBox(string cvar, float yesValue, float noValue, string label,
        string tooltip = "", params string[] multiCvars)
        => new(cvar, yesValue, noValue, Localization.Tr(label), multiCvars) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>QC <c>makeXonoticSlider(min, max, step, cvar)</c> — a slider with a live value readout.</summary>
    public static CvarSlider Slider(string cvar, float min, float max, float step, string tooltip = "", Func<float, string>? format = null)
        => new(cvar, min, max, step, format) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>QC <c>makeXonoticTextSlider/MixedSlider(cvar)</c> — a labeled discrete chooser bound to a cvar.</summary>
    public static CvarTextSlider TextSlider(string cvar, string tooltip = "")
        => new(cvar) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>QC <c>makeXonoticRadioButton(group, cvar, value, label)</c> — one option of a cvar-valued radio set.</summary>
    public static CvarRadioButton RadioButton(string cvar, string value, string label, ButtonGroup group, string tooltip = "")
        => new(cvar, value, Localization.Tr(label), group) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>QC <c>makeXonoticMixedSlider(cvar)</c> — a DISCRETE slider over labeled values ("Default", a
    /// numeric range, "Infinite") with a live text readout. Chain <see cref="CvarMixedSlider.Add"/> /
    /// <see cref="CvarMixedSlider.AddRange"/> then call <see cref="CvarMixedSlider.Finish"/>.</summary>
    public static CvarMixedSlider MixedSlider(string cvar, string tooltip = "")
        => new(cvar) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>QC <c>makeXonoticInputBox(cvar)</c> — a text field bound to a string cvar.</summary>
    public static CvarLineEdit InputBox(string cvar, string placeholder = "", string tooltip = "")
        => new(cvar) { PlaceholderText = Localization.Tr(placeholder), TooltipText = Localization.Tr(tooltip) };

    /// <summary>QC <c>makeXonoticCommandButton(label, command)</c> — a button that runs a console command.</summary>
    public static CommandButton CommandButton(string label, string command, string tooltip = "")
        => new(Localization.Tr(label), command) { TooltipText = Localization.Tr(tooltip) };

    /// <summary>
    /// QC <c>makeXonoticColorpickerString(cvar, ...)</c> — a color picker bound to a string cvar holding a
    /// space-separated "r g b" triplet (each component 0..1). The QC widget is a palette grid; this is the
    /// closest faithful stand-in (a Godot color picker editing the same string cvar).
    /// </summary>
    public static CvarColorButton ColorButton(string cvar, string tooltip = "")
        => new(cvar) { TooltipText = tooltip };
}

/// <summary>Shared helpers for the cvar controls (enable/disable, formatting, store access).</summary>
internal static class CvarUi
{
    internal static CvarService Cvars => MenuState.Cvars;

    /// <summary>Recursively enable/disable a control subtree (buttons/sliders/fields) and dim it when off.</summary>
    internal static void SetEnabledRecursive(Control root, bool enabled)
    {
        Apply(root, enabled);
        root.Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.4f);
    }

    private static void Apply(Node node, bool enabled)
    {
        switch (node)
        {
            case BaseButton b: b.Disabled = !enabled; break;
            case Slider s: s.Editable = enabled; break;
            case LineEdit le: le.Editable = enabled; break;
            case SpinBox sb: sb.Editable = enabled; break;
        }
        foreach (Node child in node.GetChildren())
            Apply(child, enabled);
    }

    /// <summary>Trim a float to a tidy string (no trailing zeros), invariant culture.</summary>
    internal static string Tidy(float v)
        => v.ToString("0.###", CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------------------------------------
//  Checkbox
// ---------------------------------------------------------------------------------------------------------

/// <summary>A cvar-bound checkbox: checked when the cvar equals <c>on</c>; writes <c>on</c>/<c>off</c>.</summary>
public partial class CvarCheckBox : CheckBox
{
    private readonly string _cvar, _on, _off;
    private bool _updating;

    public CvarCheckBox(string cvar, string label, string on = "1", string off = "0")
    {
        _cvar = cvar; _on = on; _off = off;
        Text = label;
        Toggled += OnToggled;
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        // "on" by exact string match, or (the common numeric case) any nonzero when on=="1".
        string v = CvarUi.Cvars.GetString(_cvar);
        ButtonPressed = v == _on || (_on == "1" && v != "" && CvarUi.Cvars.GetFloat(_cvar) != 0f);
        _updating = false;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating) return;
        CvarUi.Cvars.Set(_cvar, pressed ? _on : _off);
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}

/// <summary>
/// A value-pair checkbox — the faithful port of Base <c>XonoticCheckBox</c> with an explicit yes/no value
/// (<c>makeXonoticCheckBoxEx(yes, no, cvar, label)</c>): checked when the cvar reads on the yes side of the
/// yes/no midpoint (checkbox.qc:64-72, via <see cref="CheckBoxValue.LoadChecked"/>), and toggling stores
/// exactly the yes/no value (mindecimals). Optional linked cvars mirror the stored value (QC <c>makeMulti</c>).
/// </summary>
public partial class CvarValueCheckBox : CheckBox
{
    private readonly string _cvar;
    private readonly float _yes, _no;
    private readonly string[] _multi;
    private bool _updating;

    public CvarValueCheckBox(string cvar, float yesValue, float noValue, string label, string[]? multiCvars = null)
    {
        _cvar = cvar; _yes = yesValue; _no = noValue;
        _multi = multiCvars ?? System.Array.Empty<string>();
        Text = label;
        Toggled += OnToggled;
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        ButtonPressed = CheckBoxValue.LoadChecked(CvarUi.Cvars.GetFloat(_cvar), _yes, _no);
        _updating = false;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating) return;
        string value = CheckBoxValue.SaveValue(pressed, _yes, _no);
        CvarUi.Cvars.Set(_cvar, value);
        CvarUi.Cvars.MarkArchived(_cvar);
        // QC makeMulti (util.qc:92 saveCvarsMulti): the linked cvars receive the SAME stored value.
        foreach (string extra in _multi)
        {
            CvarUi.Cvars.Set(extra, value);
            CvarUi.Cvars.MarkArchived(extra);
        }
    }
}

// NOTE: there is deliberately NO bit-flag checkbox here. QC has no such widget — every
// makeXonoticCheckBoxEx(a, b, cvar, label) call is a VALUE-pair checkbox (a/b are the stored yes/no
// values, checkbox.qc:33-42). An earlier CvarFlagCheckBox misread those first two args as a bit mask,
// which left 19 menu checkboxes inert (bit 0 masks nothing) — port new checkboxes with ValueCheckBox
// (or plain CheckBox for the makeXonoticCheckBox(isInverted, ...) 1/0 form).

// ---------------------------------------------------------------------------------------------------------
//  Slider (with value readout)
// ---------------------------------------------------------------------------------------------------------

/// <summary>A cvar-bound slider packing a live value readout, mirroring the QC slider's number display.</summary>
public partial class CvarSlider : HBoxContainer
{
    private readonly string _cvar;
    private readonly Func<float, string> _format;
    private readonly HSlider _slider;
    private readonly Label _readout;
    private bool _updating;

    /// <summary>The inner slider (for callers that need to tweak ticks/size).</summary>
    public HSlider Slider => _slider;

    public CvarSlider(string cvar, float min, float max, float step, Func<float, string>? format = null)
    {
        _cvar = cvar;
        _format = format ?? CvarUi.Tidy;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);

        _slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 26), // tall enough for the skin's groove + chevron knob to show
        };
        _readout = new Label { CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _readout.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.94f));

        _slider.ValueChanged += OnValueChanged;
        AddChild(_slider);
        AddChild(_readout);
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        _slider.Value = CvarUi.Cvars.GetFloat(_cvar);
        _readout.Text = _format((float)_slider.Value);
        _updating = false;
    }

    private void OnValueChanged(double v)
    {
        _readout.Text = _format((float)v);
        if (_updating) return;
        CvarUi.Cvars.Set(_cvar, ((float)v).ToString(CultureInfo.InvariantCulture));
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}

// ---------------------------------------------------------------------------------------------------------
//  Text slider (labeled discrete values) — QC textslider / mixedslider
// ---------------------------------------------------------------------------------------------------------

/// <summary>
/// A labeled discrete chooser bound to a cvar (QC <c>makeXonoticTextSlider</c>/<c>MixedSlider</c>): each entry
/// pairs a display label with a stored value. Presented as a dropdown; selecting writes the value, and the
/// store value snaps to the nearest entry on refresh. Build with <see cref="Add"/> then it's ready.
/// </summary>
public partial class CvarTextSlider : OptionButton
{
    private readonly string _cvar;
    private readonly List<string> _values = new();
    private bool _updating;

    public CvarTextSlider(string cvar)
    {
        _cvar = cvar;
        ItemSelected += OnItemSelected;
    }

    /// <summary>Add a labeled value (QC <c>e.addText(label, value)</c>). The display label is translated (QC built
    /// each entry label with <c>_()</c>); the stored cvar value is left verbatim.</summary>
    public CvarTextSlider Add(string label, string value)
    {
        AddItem(Localization.Tr(label));
        _values.Add(value);
        return this;
    }

    /// <summary>Add a labeled value WITHOUT translating the label (QC <c>e.addText(s, value)</c> where <c>s</c> is
    /// already a proper name — e.g. a language self-name like "Deutsch", or a player/skin/map name). Use for
    /// labels that must not pass through the PO catalog.</summary>
    public CvarTextSlider AddRaw(string label, string value)
    {
        AddItem(label);
        _values.Add(value);
        return this;
    }

    /// <summary>Add a labeled numeric value.</summary>
    public CvarTextSlider Add(string label, float value)
        => Add(label, value.ToString(CultureInfo.InvariantCulture));

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating || _values.Count == 0) return;
        _updating = true;
        // Pick the exact-string match, else the nearest numeric value.
        string cur = CvarUi.Cvars.GetString(_cvar);
        int idx = _values.IndexOf(cur);
        if (idx < 0)
        {
            float target = CvarUi.Cvars.GetFloat(_cvar);
            float best = float.MaxValue;
            for (int i = 0; i < _values.Count; i++)
            {
                if (float.TryParse(_values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                {
                    float d = Math.Abs(val - target);
                    if (d < best) { best = d; idx = i; }
                }
            }
        }
        if (idx >= 0) Selected = idx;
        _updating = false;
    }

    private void OnItemSelected(long index)
    {
        if (_updating || index < 0 || index >= _values.Count) return;
        CvarUi.Cvars.Set(_cvar, _values[(int)index]);
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}

// ---------------------------------------------------------------------------------------------------------
//  Mixed slider (discrete labeled values on a real slider) — QC mixedslider.qc
// ---------------------------------------------------------------------------------------------------------

/// <summary>
/// The C# successor to QC <c>XonoticMixedSlider</c>: a real slider whose positions are DISCRETE labeled
/// values — typically "Default" (-1), a numeric range, "Infinite" (0) — with the active label read out to the
/// right (the Create-game Time/Frag/Teams/Bot-skill rows). Reads/writes the cvar like every other bound
/// widget; the store value snaps to the nearest entry on refresh.
/// </summary>
public partial class CvarMixedSlider : HBoxContainer
{
    private readonly string _cvar;
    private readonly List<(string Label, string Value)> _entries = new();
    private readonly HSlider _slider;
    private readonly Label _readout;
    private bool _updating;

    public CvarMixedSlider(string cvar)
    {
        _cvar = cvar;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);

        _slider = new HSlider
        {
            MinValue = 0, Step = 1,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 26),
        };
        _readout = new Label { CustomMinimumSize = new Vector2(76, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _readout.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.94f));
        _slider.ValueChanged += OnValueChanged;
        AddChild(_slider);
        AddChild(_readout);
    }

    /// <summary>QC <c>e.addText(label, value)</c>: one labeled stop.</summary>
    public CvarMixedSlider Add(string label, float value)
    {
        _entries.Add((Localization.Tr(label), value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>QC <c>e.addRange(min, max, step)</c>: numeric stops labeled by their value.</summary>
    public CvarMixedSlider AddRange(float min, float max, float step)
    {
        for (float v = min; v <= max + 0.001f; v += step)
            _entries.Add((CvarUi.Tidy(v), v.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>QC <c>configureXonoticMixedSliderValues</c>: lock the entry list in and sync from the cvar.</summary>
    public CvarMixedSlider Finish()
    {
        _slider.MaxValue = Math.Max(0, _entries.Count - 1);
        Refresh();
        return this;
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating || _entries.Count == 0) return;
        _updating = true;
        string cur = CvarUi.Cvars.GetString(_cvar);
        int idx = _entries.FindIndex(e => e.Value == cur);
        if (idx < 0)
        {
            // nearest numeric entry (the store may hold a value between stops)
            float target = CvarUi.Cvars.GetFloat(_cvar);
            float best = float.MaxValue;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (float.TryParse(_entries[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                {
                    float d = Math.Abs(val - target);
                    if (d < best) { best = d; idx = i; }
                }
            }
            if (idx < 0) idx = 0;
        }
        _slider.Value = idx;
        _readout.Text = _entries[idx].Label;
        _updating = false;
    }

    private void OnValueChanged(double v)
    {
        int idx = Mathf.Clamp((int)v, 0, _entries.Count - 1);
        if (_entries.Count == 0) return;
        _readout.Text = _entries[idx].Label;
        if (_updating) return;
        CvarUi.Cvars.Set(_cvar, _entries[idx].Value);
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}

// ---------------------------------------------------------------------------------------------------------
//  Radio button (one cvar value per option) — QC radiobutton
// ---------------------------------------------------------------------------------------------------------

/// <summary>One option of a cvar-valued radio set: checked when the cvar equals <c>value</c>; selecting writes it.</summary>
public partial class CvarRadioButton : CheckBox
{
    private readonly string _cvar, _value;
    private bool _updating;

    public CvarRadioButton(string cvar, string value, string label, ButtonGroup group)
    {
        _cvar = cvar; _value = value;
        Text = label;
        ButtonGroup = group;
        ToggleMode = true;
        // A radio is a Godot CheckBox here; swap in the skin's radiobutton_* graphics so it reads as a radio,
        // not a checkbox (per-instance overrides win over the CheckBox theme icons). No-op without the skin VFS.
        if (MenuSkin.RadioUnchecked is { } ru) AddThemeIconOverride("unchecked", ru);
        if (MenuSkin.RadioChecked is { } rc) AddThemeIconOverride("checked", rc);
        Toggled += OnToggled;
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        ButtonPressed = CvarUi.Cvars.GetString(_cvar) == _value;
        _updating = false;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating || !pressed) return;
        CvarUi.Cvars.Set(_cvar, _value);
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}

// ---------------------------------------------------------------------------------------------------------
//  Input box (string cvar) — QC inputbox
// ---------------------------------------------------------------------------------------------------------

/// <summary>A text field bound to a string cvar (QC <c>makeXonoticInputBox</c>): writes on every edit.</summary>
public partial class CvarLineEdit : LineEdit
{
    private readonly string _cvar;
    private bool _updating;

    public CvarLineEdit(string cvar)
    {
        _cvar = cvar;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        TextChanged += OnTextChanged;
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        Text = CvarUi.Cvars.GetString(_cvar);
        _updating = false;
    }

    private void OnTextChanged(string newText)
    {
        if (_updating) return;
        CvarUi.Cvars.Set(_cvar, newText);
        CvarUi.Cvars.MarkArchived(_cvar);
    }

    /// <summary>
    /// QC <c>inputBox.enterText(s)</c>: insert <paramref name="s"/> at the caret (replacing any selection),
    /// advance the caret past it, and write the cvar — used by the charmap and the name colorpicker to type a
    /// glyph / a <c>^xRGB</c> color code into this box. Mirrors the engine inputbox's enterText.
    /// </summary>
    public void InsertAtCaret(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (HasSelection())
            DeleteText(GetSelectionFromColumn(), GetSelectionToColumn());
        int caret = CaretColumn;
        string text = Text;
        Text = text[..caret] + s + text[caret..];   // setting Text doesn't raise TextChanged → write the cvar below
        CaretColumn = caret + s.Length;
        CvarUi.Cvars.Set(_cvar, Text);
        CvarUi.Cvars.MarkArchived(_cvar);
    }
}

// ---------------------------------------------------------------------------------------------------------
//  Command button — QC commandbutton
// ---------------------------------------------------------------------------------------------------------

/// <summary>A button that runs a console command via <see cref="MenuCommand"/> (QC <c>makeXonoticCommandButton</c>).</summary>
public partial class CommandButton : Button
{
    private readonly string _command;

    public CommandButton(string label, string command)
    {
        _command = command;
        Text = label;
        CustomMinimumSize = new Vector2(0, 36);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Pressed += () => MenuCommand.Run(_command);
    }
}

// ---------------------------------------------------------------------------------------------------------
//  Color button (string "r g b" cvar) — QC colorpicker_string / makeXonoticColorpickerString
// ---------------------------------------------------------------------------------------------------------

/// <summary>
/// A Godot <see cref="ColorPickerButton"/> bound to an Xonotic "r g b" string cvar (each component 0..1,
/// space-separated) — the C# successor to QC <c>makeXonoticColorpickerString</c>
/// (qcsrc/menu/xonotic/colorpicker_string.qc), which is a palette grid editing that same string cvar. Reads
/// the cvar on enter / on the store's change event, writes the picked color back as "r g b" (RGB only — the
/// QC picker has no alpha) and marks the cvar archived. An empty/garbage value shows as white (the QC default).
/// Build via <see cref="Widgets.ColorButton"/>.
/// </summary>
public partial class CvarColorButton : ColorPickerButton
{
    private readonly string _cvar;
    private bool _updating;

    public CvarColorButton(string cvar)
    {
        _cvar = cvar;
        // A compact swatch (the Xonotic color picker is a small button, not a full-width bar). ShrinkBegin so
        // the row layout leaves it small and left-aligned rather than stretching it into a saturated banner.
        CustomMinimumSize = new Vector2(72, 24);
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        EditAlpha = false;
        ColorChanged += OnColorChanged;
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        Color = Parse(CvarUi.Cvars.GetString(_cvar));
        _updating = false;
    }

    private void OnColorChanged(Color c)
    {
        if (_updating) return;
        CvarUi.Cvars.Set(_cvar, $"{c.R.ToString("0.###", CultureInfo.InvariantCulture)} {c.G.ToString("0.###", CultureInfo.InvariantCulture)} {c.B.ToString("0.###", CultureInfo.InvariantCulture)}");
        CvarUi.Cvars.MarkArchived(_cvar);
    }

    /// <summary>Parse an "r g b" triplet (0..1 components) into a Color; defaults to white on parse failure.</summary>
    private static Color Parse(string s)
    {
        string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
        {
            return new Color(Mathf.Clamp(r, 0f, 1f), Mathf.Clamp(g, 0f, 1f), Mathf.Clamp(b, 0f, 1f));
        }
        return Colors.White;
    }
}

// ---------------------------------------------------------------------------------------------------------
//  setDependent — enable/disable a control based on another cvar's value
// ---------------------------------------------------------------------------------------------------------

/// <summary>
/// The C# successor to QC's <c>setDependent</c>/<c>setDependentNOT</c>: a control is enabled only while a
/// driver cvar's value lies inside (or, with <c>negate</c>, outside) a range, re-evaluated whenever that cvar
/// changes. Attach via <see cref="Bind"/>; it self-detaches when the target leaves the tree.
/// </summary>
public sealed partial class Dependent : Node
{
    private Control _target = null!;
    private string _cvar = "";
    private float _min, _max;
    private bool _negate;
    private string? _stringNotEqual; // non-null → string-compare mode (enabled while cvar != this value)

    /// <summary>Enable <paramref name="target"/> only while <paramref name="cvar"/> ∈ [min,max] (or ∉, if negate).</summary>
    public static void Bind(Control target, string cvar, float min, float max, bool negate = false)
    {
        var dep = new Dependent { _target = target, _cvar = cvar, _min = min, _max = max, _negate = negate, Name = "Dependent" };
        target.AddChild(dep);
    }

    /// <summary>Convenience for QC <c>setDependentNOT(w, cvar, v)</c>: enabled while cvar != v.</summary>
    public static void BindNot(Control target, string cvar, float value) => Bind(target, cvar, value, value, negate: true);

    /// <summary>
    /// QC <c>setDependentStringNotEqual(w, cvar, value)</c>: enable <paramref name="target"/> while the string
    /// cvar's value is NOT equal to <paramref name="value"/>. Unlike <see cref="BindNot"/> this is a true string
    /// compare, so non-numeric presets (e.g. <c>"border_default"</c>) are correctly treated as "not 0".
    /// </summary>
    public static void BindStringNotEqual(Control target, string cvar, string value)
    {
        var dep = new Dependent { _target = target, _cvar = cvar, _stringNotEqual = value, Name = "Dependent" };
        target.AddChild(dep);
    }

    public override void _EnterTree() { CvarUi.Cvars.Changed += OnChanged; Evaluate(); }
    public override void _ExitTree() { CvarUi.Cvars.Changed -= OnChanged; }

    private void OnChanged(string name) { if (name == _cvar) Evaluate(); }

    private void Evaluate()
    {
        bool enabled;
        if (_stringNotEqual is not null)
        {
            enabled = CvarUi.Cvars.GetString(_cvar) != _stringNotEqual;
        }
        else
        {
            float v = CvarUi.Cvars.GetFloat(_cvar);
            bool inRange = v >= _min && v <= _max;
            enabled = _negate ? !inRange : inRange;
        }
        CvarUi.SetEnabledRecursive(_target, enabled);
    }
}
