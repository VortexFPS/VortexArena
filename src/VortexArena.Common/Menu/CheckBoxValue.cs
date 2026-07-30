// Port of qcsrc/menu/xonotic/checkbox.qc (XonoticCheckBox loadCvars/saveCvars — the VALUE-PAIR checkbox).
//
// Xonotic's menu checkbox is not a bit toggle: makeXonoticCheckBoxEx(theYesValue, theNoValue, cvar, label)
// binds the box to a cvar that stores theYesValue when checked and theNoValue when unchecked — e.g. the
// Settings→Game→View tab's "Smooth the view when landing from a jump" writes cl_bobfall 0.05/0, "View
// waving while idle" writes v_idlescale 1/0, and "Slide to third person upon death" writes
// cl_eventchase_death 2/0. makeXonoticCheckBox(isInverted, ...) is the same widget with a (yes,no) pair of
// (1,0) or (0,1). The value math lives here, Godot-free (ADR-0008), so the widget semantics are testable;
// the Godot Control shell is game/menu/framework/CvarControls.cs (CvarValueCheckBox).

using System.Globalization;

namespace XonoticGodot.Common.Menu;

/// <summary>
/// The value math of Base's menu value-pair checkbox (<c>XonoticCheckBox</c>, checkbox.qc:44-84):
/// checked-state derivation from the live cvar value and the exact string written back on toggle.
/// </summary>
public static class CheckBoxValue
{
    /// <summary>
    /// Whether <paramref name="value"/> reads as CHECKED — QC <c>XonoticCheckBox_loadCvars</c>
    /// (checkbox.qc:64-72): the value sits on the yes side of the yes/no midpoint
    /// (<c>m = (yes+no)/2; d = (v-m)/(yes-m); checked = d &gt; 0</c>). So any value past halfway toward
    /// yes counts (a hand-set <c>cl_bobfall 0.1</c> still reads checked against the 0.05/0 pair), the
    /// exact no-value (and the midpoint itself) reads unchecked, and an inverted pair (yes &lt; no,
    /// from <c>makeXonoticCheckBox(1, ...)</c>) works symmetrically.
    /// </summary>
    public static bool LoadChecked(float value, float yesValue, float noValue)
    {
        // yes == no would be a meaningless widget (division by zero) — QC never builds one; read as unchecked.
        float m = (yesValue + noValue) * 0.5f;
        float denom = yesValue - m;
        if (denom == 0f)
            return false;
        return (value - m) / denom > 0f;
    }

    /// <summary>
    /// The cvar string stored for a checked/unchecked state — QC <c>XonoticCheckBox_saveCvars</c>
    /// (checkbox.qc:73-84) writes <c>ftos_mindecimals(yes|no)</c>: the shortest decimal form
    /// ("0.05", "1", "2" — never "0.050000").
    /// </summary>
    public static string SaveValue(bool isChecked, float yesValue, float noValue)
        => FormatMinDecimals(isChecked ? yesValue : noValue);

    /// <summary>QC <c>ftos_mindecimals</c>: minimal decimal representation, invariant culture.</summary>
    public static string FormatMinDecimals(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);
}
