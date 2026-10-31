using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A frameless dialog tab on Xonotic's grid (xonotic/tab.qc): a <see cref="QcGrid"/> that runs its fill function the
/// first time it enters the tree, so a ported tab reads as the QC <c>..._fill(me)</c> it comes from, TD for TD.
/// </summary>
public abstract partial class QcTab : QcGrid
{
    private bool _filled;

    protected QcTab(float rows, float columns, float intendedWidth) : base(rows, columns, intendedWidth) { }

    public override void _Ready()
    {
        if (_filled) return;
        _filled = true;
        Fill();
    }

    /// <summary>The QC fill function: <c>TR</c> / <c>TD</c> / <c>gotoRC</c> calls on this grid.</summary>
    protected abstract void Fill();

    /// <summary>The menu this tab is shown in, for buttons that open another dialog (DialogOpenButton_Click).</summary>
    protected MenuRoot? Menu => Qc.MenuOf(this);
}

/// <summary>
/// The <c>makeXonotic*</c> factories for a <see cref="QcTab"/>: the existing cvar widgets (<see cref="Widgets"/>) with
/// the QC look (<see cref="QcLook"/>) and the QC's own semantics (checkbox values, slider text, dependencies).
/// </summary>
public static class Qc
{
    /// <summary>makeXonoticTextLabel(align, text).</summary>
    public static QcLabel Label(string text, float align = 0f) => new(text, align);

    /// <summary>makeXonoticHeaderLabel: bold, centred, SKINCOLOR_HEADER @ SKINALPHA_HEADER.</summary>
    public static QcLabel Header(string text) => new(text, 0.5f) { Bold = true, Color = MenuSkin.Header };

    /// <summary>makeXonoticCheckBox_T(isInverted, cvar, text, tooltip): its yes/no values as checkbox.qc derives them
    /// (0 = 1/0, 1 = 0/1, beyond +-1 = the value pair +-(x - 1), e.g. m_pitch's 1.022). A null cvar is a checkbox
    /// "just there for show".</summary>
    public static CvarValueCheckBox Check(float isInverted, string? cvar, string text, string tooltip = "")
    {
        float yes, no;
        if (isInverted > 1f) { no = isInverted - 1f; yes = -no; }
        else if (isInverted < -1f) { no = isInverted + 1f; yes = -no; }
        else if (isInverted == 1f) { no = 1f; yes = 0f; }
        else { no = 0f; yes = 1f; }
        return CheckEx(yes, no, cvar, text, tooltip);
    }

    /// <summary>makeXonoticCheckBoxEx_T(yes, no, cvar, text, tooltip).</summary>
    public static CvarValueCheckBox CheckEx(float yes, float no, string? cvar, string text, string tooltip = "")
    {
        var c = Widgets.ValueCheckBox(cvar ?? "", yes, no, text, tooltip);
        if (cvar is null) c.Disabled = true;
        return QcLook.CheckBox(c);
    }

    /// <summary>makeXonoticRadioButton_T(group, cvar, value, text, tooltip).</summary>
    public static QcRadio Radio(QcRadioGroup group, string cvar, string? value, string text, string tooltip = "")
        => QcLook.CheckBox(new QcRadio(group, cvar, value, text, tooltip), "radiobutton");

    /// <summary>makeXonoticSlider_T(min, max, step, cvar, tooltip), with its formatString.</summary>
    public static CvarSlider Slider(float min, float max, float step, string cvar, string tooltip = "", string format = "",
        float valueSpace = -1f)
    {
        var s = Widgets.Slider(cvar, min, max, step, tooltip);
        s.FormatString = Localization.Tr(format);
        return QcLook.Slider(s, valueSpace);
    }

    /// <summary>makeXonoticMixedSlider_T(cvar, tooltip): add its values, then <see cref="Done"/>.</summary>
    public static CvarMixedSlider Mixed(string cvar, string tooltip = "", string format = "")
    {
        var m = Widgets.MixedSlider(cvar, tooltip);
        m.FormatString = Localization.Tr(format);
        return m;
    }

    /// <summary>configureXonoticMixedSliderValues, and the QC look (with setValueSpace's factor when given).</summary>
    public static CvarMixedSlider Done(this CvarMixedSlider m, float valueSpaceFactor = 1f)
        => QcLook.Slider(m.Finish(), valueSpaceFactor * QcLook.DefaultValueSpace);

    /// <summary>makeXonoticButton(text, color), running <paramref name="pressed"/> (null: a button with no action).</summary>
    public static Button Button(string text, Action? pressed, string tooltip = "", Color? tint = null)
    {
        var b = new Button { Text = Localization.Tr(text), TooltipText = Localization.Tr(tooltip), FocusMode = Control.FocusModeEnum.All };
        if (pressed is not null) b.Pressed += pressed;
        return QcLook.Button(b, tint);
    }

    /// <summary>makeXonoticCommandButton(text, color, command, flags).</summary>
    public static CommandButton Command(string text, string command, string tooltip = "")
        => QcLook.Button(Widgets.CommandButton(text, command, tooltip));

    // ---------------------------------------------------------------------------------------------------
    //  setDependent* and makeMulti (xonotic/util.qc)
    // ---------------------------------------------------------------------------------------------------

    /// <summary>setDependent(e, cvar, min, max): enabled while min &lt;= cvar &lt;= max (min &gt; max: while outside max..min).</summary>
    public static T Dep<T>(this T e, string cvar, float min, float max) where T : Control
    {
        QcDependent d = QcDependent.Of(e);
        d.Reset();
        d.Cvar1 = CvarUi.EnsureEngine(cvar); d.Min1 = min; d.Max1 = max;
        d.Evaluate();
        return e;
    }

    /// <summary>setDependentNOT(e, cvar, value): enabled while cvar != value.</summary>
    public static T DepNot<T>(this T e, string cvar, float value) where T : Control
    {
        QcDependent d = QcDependent.Of(e);
        d.Reset();
        d.Cvar1 = CvarUi.EnsureEngine(cvar); d.Min1 = d.Max1 = value; d.Not = true;
        d.Evaluate();
        return e;
    }

    /// <summary>setDependentStringNotEqual(e, cvar, value).</summary>
    public static T DepStringNot<T>(this T e, string cvar, string value) where T : Control
    {
        QcDependent d = QcDependent.Of(e);
        d.Reset();
        d.StringCvar = cvar; d.StringValue = value;
        d.Evaluate();
        return e;
    }

    /// <summary>setDependentWeird(e, func): enabled while <paramref name="enabled"/> says so.</summary>
    public static T DepFunc<T>(this T e, Func<bool> enabled) where T : Control
    {
        QcDependent d = QcDependent.Of(e);
        d.Reset();
        d.Func = enabled;
        d.Evaluate();
        return e;
    }

    /// <summary>setDependentAND(e, cvar, min, max).</summary>
    public static T DepAnd<T>(this T e, string cvar, float min, float max) where T : Control
        => QcDependent.Of(e).Add(cvar, min, max, or: false, e);

    /// <summary>setDependentOR(e, cvar, min, max).</summary>
    public static T DepOr<T>(this T e, string cvar, float min, float max) where T : Control
        => QcDependent.Of(e).Add(cvar, min, max, or: true, e);

    /// <summary>makeMulti(e, "cvar !cvar2 ..."): whenever <paramref name="cvar"/> (the widget's own) changes while the
    /// widget is showing, the others get its value (a <c>!</c> prefix: the inverse, "1" for "0" and "0" otherwise).</summary>
    public static T Multi<T>(this T e, string cvar, string others) where T : Control
    {
        e.AddChild(new QcMulti(cvar, others));
        return e;
    }

    /// <summary>The menu that hosts <paramref name="n"/>, for opening another dialog from a tab.</summary>
    public static MenuRoot? MenuOf(Node n)
    {
        for (Node? p = n; p is not null; p = p.GetParent())
        {
            if (p is MenuRoot root) return root;
            if (p is MenuScreen { Menu: { } m }) return m;
        }
        return null;
    }
}

/// <summary>
/// setDependent_Check (xonotic/util.qc): one primary condition (a cvar range, its NOT, a string inequality or a
/// function) combined with up to two more ranges by AND or OR; re-evaluated when any cvar changes, and every frame
/// for a function condition (the QC checks on each draw).
/// </summary>
public sealed partial class QcDependent : Node
{
    private Control _target = null!;
    public string? Cvar1, StringCvar, StringValue;
    public float Min1, Max1;
    public bool Not, Or;
    public Func<bool>? Func;
    private readonly List<(string Cvar, float Min, float Max)> _more = new();

    public static QcDependent Of(Control target)
    {
        foreach (Node c in target.GetChildren())
            if (c is QcDependent d) return d;
        var dep = new QcDependent { _target = target, Name = "QcDependent" };
        target.AddChild(dep);
        return dep;
    }

    public void Reset()
    {
        Cvar1 = StringCvar = StringValue = null;
        Func = null;
        Not = false;
    }

    public T Add<T>(string cvar, float min, float max, bool or, T e)
    {
        CvarUi.EnsureEngine(cvar);
        if (_more.Count < 2) _more.Add((cvar, min, max));
        else _more[1] = (cvar, min, max);
        Or = or;
        Func = null;
        Evaluate();
        return e;
    }

    public override void _EnterTree()
    {
        CvarUi.Cvars.Changed += OnChanged;
        Evaluate();
    }

    public override void _ExitTree() => CvarUi.Cvars.Changed -= OnChanged;

    public override void _Process(double delta)
    {
        if (Func is not null) Evaluate();
    }

    private void OnChanged(string name) => Evaluate();

    private static bool Outside(float f, float min, float max)
        => min <= max ? f < min || f > max : f >= max && f <= min;

    public void Evaluate()
    {
        if (!IsInstanceValid(_target)) return;
        bool disabled = false;
        if (Func is not null) disabled = !Func();
        else if (StringCvar is not null) disabled = CvarUi.Cvars.GetString(StringCvar) == StringValue;
        else if (Cvar1 is not null)
        {
            float f = CvarUi.Cvars.GetFloat(Cvar1);
            disabled = Not ? f == Min1 : Outside(f, Min1, Max1);
        }
        foreach ((string cvar, float min, float max) in _more)
        {
            bool off = Outside(CvarUi.Cvars.GetFloat(cvar), min, max);
            // The QC sums the two disabled flags and compares with the operator bit: AND disables on either,
            // OR only on both.
            disabled = (disabled ? 1 : 0) + (off ? 1 : 0) > (Or ? 1 : 0);
        }
        CvarUi.SetEnabledRecursive(_target, !disabled);
    }
}

/// <summary>makeMulti's saveCvarsMulti, keyed on the widget's cvar changing while the widget is in the tree.</summary>
public sealed partial class QcMulti : Node
{
    private readonly string _cvar;
    private readonly string[] _others;

    public QcMulti(string cvar, string others)
    {
        _cvar = cvar;
        _others = others.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Name = "QcMulti";
    }

    public override void _EnterTree() => CvarUi.Cvars.Changed += OnChanged;
    public override void _ExitTree() => CvarUi.Cvars.Changed -= OnChanged;

    private void OnChanged(string name)
    {
        if (!string.Equals(name, _cvar, StringComparison.OrdinalIgnoreCase)) return;
        string s = CvarUi.Cvars.GetString(_cvar);
        foreach (string other in _others)
        {
            bool invert = other.StartsWith('!');
            string target = invert ? other[1..] : other;
            CvarUi.Cvars.Set(target, invert ? (s == "0" ? "1" : "0") : s);
            CvarUi.Cvars.MarkArchived(target);
        }
    }
}

/// <summary>The QC menu's number-to-text rules (item/slider.qc, lib/string.qh, lib/counting.qh).</summary>
public static class QcFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Slider_configureSliderValues: 0-3 decimals, the fewest that show <paramref name="step"/>.</summary>
    public static int Digits(float step)
    {
        int d = 3;
        if (MathF.Abs(MathF.Floor(step * 100f + 0.5f) - step * 100f) < 0.01f) d = 2;
        if (MathF.Abs(MathF.Floor(step * 10f + 0.5f) - step * 10f) < 0.01f) d = 1;
        if (MathF.Abs(MathF.Floor(step + 0.5f) - step) < 0.01f) d = 0;
        return d;
    }

    /// <summary>ftos_decimals: sprintf("%.*f").</summary>
    public static string Decimals(float v, int decimals)
        => (v == 0f ? 0f : v).ToString("F" + Math.Max(0, decimals), Inv);

    /// <summary>ftos_mindecimals: the shortest text for the value, at most 4 decimals.</summary>
    public static string MinDecimals(float v)
    {
        if (MathF.Abs(v) < 0.0001f) return "0";
        float r = MathF.Round(v);
        if (MathF.Abs(v - r) < 0.0001f) return ((int)r).ToString(Inv);
        return v.ToString("0.####", Inv);
    }

    /// <summary>formatStringSpecial: "%sx"-style formats, "%" for a percentage, s/m/h/d/w/y for counted time units
    /// (upper case: with decimals).</summary>
    public static string Special(string format, float v, int decs)
    {
        if (format.Length == 1)
            switch (format)
            {
                case "%": return Decimals(v * 100f, Math.Max(0, decs - 2)) + "%";
                case "s": return Count(v, "second");
                case "m": return Count(v, "minute");
                case "h": return Count(v, "hour");
                case "d": return Count(v, "day");
                case "w": return Count(v, "week");
                case "y": return Count(v, "year");
                case "S": return Decimals(v, decs) + " seconds";
                case "M": return Decimals(v, decs) + " minutes";
                case "H": return Decimals(v, decs) + " hours";
                case "D": return Decimals(v, decs) + " days";
                case "W": return Decimals(v, decs) + " weeks";
                case "Y": return Decimals(v, decs) + " years";
            }
        string txt = Decimals(v, decs);
        return format.Length > 0 ? format.Replace("%s", txt) : txt;
    }

    /// <summary>count_fill: "%d units", singular for exactly 1.</summary>
    private static string Count(float v, string unit)
        => ((int)v).ToString(Inv) + " " + (v == 1f ? unit : unit + "s");
}

/// <summary>
/// A XonoticTextSlider over string identifiers (item/textslider.qc): discrete values with a text each, "Custom" when
/// the current setting is none of them. Load and save are the caller's, as in the QC subclasses that override them
/// (the resolution slider).
/// </summary>
public partial class QcTextSlider : HBoxContainer, IQcSlider
{
    private readonly List<(string Text, string Id)> _values = new();
    private readonly Func<string> _load;
    private readonly Action<string> _save;
    private int _index = -1;
    private bool _updating;

    public HSlider Slider { get; } = new() { MinValue = 0, Step = 1 };
    public Label Readout { get; } = new();
    public double? ShownValue => _index >= 0 ? _index : null;

    public QcTextSlider(Func<string> load, Action<string> save)
    {
        _load = load;
        _save = save;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Slider.ValueChanged += v =>
        {
            if (_updating || _values.Count == 0) return;
            _index = Math.Clamp((int)v, 0, _values.Count - 1);
            Readout.Text = _values[_index].Text;
            _save(_values[_index].Id);
        };
        AddChild(Slider);
        AddChild(Readout);
    }

    public QcTextSlider Add(string text, string id)
    {
        _values.Add((text, id));
        return this;
    }

    public override void _EnterTree() => Reload();

    /// <summary>loadCvars: select the value whose identifier is the current setting, else "Custom".</summary>
    public void Reload()
    {
        _updating = true;
        Slider.MaxValue = Math.Max(0, _values.Count - 1);
        _index = _values.FindIndex(v => v.Id == _load());
        if (_index >= 0) Slider.Value = _index;
        Readout.Text = _index >= 0 ? _values[_index].Text : Localization.Tr("Custom");
        _updating = false;
        QueueRedraw();
    }
}

/// <summary>
/// XonoticDecibelsSlider (xonotic/slider_decibels.qc): a volume cvar shown and stepped in decibels, linear below
/// the slider's minimum so it can reach zero; "MAX" at the top, "OFF" at the bottom.
/// </summary>
public partial class QcDecibelsSlider : HBoxContainer, IQcSlider
{
    private static readonly float A = MathF.Log(10f) / 20f;
    private readonly string _cvar;
    private readonly float _min, _max, _step;
    private bool _updating;

    public HSlider Slider { get; } = new();
    public Label Readout { get; } = new();

    public QcDecibelsSlider(float min, float max, float step, string cvar, string tooltip = "")
    {
        _cvar = CvarUi.EnsureEngine(cvar); _min = min; _max = max; _step = step;
        TooltipText = Localization.Tr(tooltip);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Slider.MinValue = min;
        Slider.MaxValue = max;
        Slider.Step = step;
        Slider.ValueChanged += v =>
        {
            Readout.Text = Text((float)v);
            if (_updating) return;
            float db = (float)v > _max - 0.5f * _step ? _max : (float)v;
            CvarUi.Cvars.Set(_cvar, FromDb(db, _min).ToString("0.######", CultureInfo.InvariantCulture));
            CvarUi.Cvars.MarkArchived(_cvar);
        };
        AddChild(Slider);
        AddChild(Readout);
    }

    public static float ToDb(float f, float mi)
    {
        if (mi != 0f)
        {
            float t = 1f / A + mi, u = MathF.Exp(1f + A * mi);
            if (f <= u) return mi + (t - mi) * (f / u);
        }
        return MathF.Log(f) / A;
    }

    public static float FromDb(float f, float mi)
    {
        if (mi != 0f)
        {
            float t = 1f / A + mi, u = MathF.Exp(1f + A * mi);
            if (f <= t) return u * ((f - mi) / (t - mi));
        }
        return MathF.Exp(A * f);
    }

    public override void _EnterTree()
    {
        CvarUi.Cvars.Changed += OnChanged;
        Load();
    }

    public override void _ExitTree() => CvarUi.Cvars.Changed -= OnChanged;

    private void OnChanged(string name)
    {
        if (name == _cvar) Load();
    }

    private void Load()
    {
        _updating = true;
        float v = CvarUi.Cvars.GetFloat(_cvar);
        Slider.Value = v > FromDb(_max - 0.5f * _step, _min) ? _max : _step * MathF.Floor(0.5f + ToDb(v, _min) / _step);
        Readout.Text = Text((float)Slider.Value);
        _updating = false;
    }

    private string Text(float v)
    {
        if (v > _max - 0.5f * _step) return Localization.Tr("MAX");
        if (v <= _min) return Localization.Tr("OFF");
        float range = _max - _min;
        switch ((int)CvarUi.Cvars.GetFloat("menu_snd_sliderscale"))
        {
            case 3: return QcFormat.Decimals((v - _min) / range * 100f, 0) + "%";
            case 2: return QcFormat.Decimals((v - _min) / range * 10f, 1);
            case 1: return QcFormat.Decimals(FromDb(v, _min) * 100f, 2) + "%";
            default: return QcFormat.Decimals(ToDb(FromDb(v, _min), 0f), QcFormat.Digits(_step)) + " dB";
        }
    }
}

/// <summary>
/// XonoticSliderCheckBox (xonotic/checkbox_slider_invalid.qc): a checkbox that switches a slider between an "off"
/// value and the last value it had inside its range — "Motion blur:" beside the r_motionblur slider.
/// </summary>
public partial class QcSliderCheckBox : CheckBox
{
    private readonly CvarSlider _slider;
    private readonly float _off;
    private readonly bool _inverted;
    private float _saved;
    private bool _updating;

    public QcSliderCheckBox(float offValue, bool inverted, CvarSlider slider, string text, float? savedDefault = null)
    {
        _off = offValue;
        _inverted = inverted;
        _slider = slider;
        Text = Localization.Tr(text);
        TooltipText = slider.TooltipText;
        float v = Value;
        _saved = InRange(v) ? v : (float)slider.Slider.MinValue;
        // The Effects tab: "if(s.value != e.savedValue) e.savedValue = 0.4; // default"
        if (savedDefault is { } d && v != _saved) _saved = d;
        Toggled += OnToggled;
        QcLook.CheckBox(this);
    }

    private float Value => CvarUi.Cvars.GetFloat(_slider.Cvar);
    private bool InRange(float v) => v >= _slider.Slider.MinValue && v <= _slider.Slider.MaxValue;

    public override void _EnterTree()
    {
        CvarUi.Cvars.Changed += OnCvarChanged;
        Sync();
    }

    public override void _ExitTree() => CvarUi.Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (name == _slider.Cvar) Sync();
    }

    // XonoticSliderCheckBox_draw: checked while the slider is (not) at the off value; remember an in-range value.
    private void Sync()
    {
        float v = Value;
        if (InRange(v)) _saved = v;
        _updating = true;
        ButtonPressed = (v == _off) != _inverted;
        _updating = false;
    }

    private void OnToggled(bool on)
    {
        if (_updating) return;
        float v = on == _inverted
            ? Math.Clamp(_saved, (float)_slider.Slider.MinValue, (float)_slider.Slider.MaxValue)
            : _off;
        CvarUi.Cvars.Set(_slider.Cvar, QcFormat.MinDecimals(v));
        CvarUi.Cvars.MarkArchived(_slider.Cvar);
    }
}

/// <summary>
/// An "Apply immediately" button the QC way (<c>e.applyButton</c> + <c>disableOnClick</c>): greyed until one of the
/// widgets that names it changes its cvar, greyed again once pressed. For a tab whose applied state nothing records
/// (see <see cref="PendingApply"/> for the ones that do).
/// </summary>
public sealed partial class QcApplyOnChange : Node
{
    private readonly Button _button;
    private readonly HashSet<string> _cvars;

    private QcApplyOnChange(Button button, string[] cvars)
    {
        _button = button;
        _cvars = new HashSet<string>(cvars, StringComparer.OrdinalIgnoreCase);
        Name = "QcApplyOnChange";
    }

    public static void Bind(Button button, params string[] cvars) => button.AddChild(new QcApplyOnChange(button, cvars));

    public override void _EnterTree()
    {
        CvarUi.Cvars.Changed += OnChanged;
        _button.Pressed += Disable;
        Disable();
    }

    public override void _ExitTree()
    {
        CvarUi.Cvars.Changed -= OnChanged;
        _button.Pressed -= Disable;
    }

    private void Disable() => CvarUi.SetEnabledRecursive(_button, false);

    private void OnChanged(string name)
    {
        if (_cvars.Contains(name)) CvarUi.SetEnabledRecursive(_button, true);
    }
}

/// <summary>The radio buttons of one QC radio group number (the <c>group</c> argument of makeXonoticRadioButton).</summary>
public sealed class QcRadioGroup
{
    internal readonly List<QcRadio> Members = new();
}

/// <summary>
/// XonoticRadioButton (xonotic/radiobutton.qc, item/radiobutton.qc). Unlike a Godot button group, each radio's
/// state is its own cvar's: checked while the cvar equals its value (or, with no value, while the cvar is non-zero),
/// so two can show checked at once. A click on an unchecked one unchecks the rest of its group first — a radio with
/// no value writes "0" then, one with a value writes nothing — and then checks itself, writing its value (or "1").
/// That order is what lets "Per weapon" (crosshair_per_weapon, makeMulti'd onto crosshair_enabled) and "Custom"
/// (crosshair_enabled 2) share a group.
/// </summary>
public partial class QcRadio : CheckBox
{
    private readonly string _cvar;
    private readonly string? _value;
    private readonly QcRadioGroup _group;
    private bool _updating;

    public QcRadio(QcRadioGroup group, string cvar, string? value, string text, string tooltip = "")
    {
        _group = group;
        _cvar = CvarUi.EnsureEngine(cvar);
        _value = value;
        Text = Localization.Tr(text);
        TooltipText = Localization.Tr(tooltip);
        ToggleMode = true;
        group.Members.Add(this);
        Toggled += OnToggled;
    }

    public override void _EnterTree()
    {
        CvarUi.Cvars.Changed += OnCvarChanged;
        Load();
    }

    public override void _ExitTree() => CvarUi.Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (string.Equals(name, _cvar, StringComparison.OrdinalIgnoreCase)) Load();
    }

    private void Load() => Show(_value is not null ? CvarUi.Cvars.GetString(_cvar) == _value : CvarUi.Cvars.GetFloat(_cvar) != 0f);

    private void Show(bool on)
    {
        _updating = true;
        ButtonPressed = on;
        _updating = false;
    }

    private void Save(bool on)
    {
        if (_value is not null)
        {
            if (!on) return;
            CvarUi.Cvars.Set(_cvar, _value);
        }
        else
            CvarUi.Cvars.Set(_cvar, on ? "1" : "0");
        CvarUi.Cvars.MarkArchived(_cvar);
    }

    // RadioButton_Click: a checked radio stays checked; an unchecked one unchecks the others, then itself.
    private void OnToggled(bool on)
    {
        if (_updating) return;
        if (!on)
        {
            Show(true);
            return;
        }
        foreach (QcRadio other in _group.Members)
            if (other != this && other.ButtonPressed)
            {
                other.Show(false);
                other.Save(false);
            }
        Save(true);
    }
}
