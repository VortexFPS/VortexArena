// Port of the number-formatting + size math in common/mutators/mutator/damagetext/cl_damagetext.qc
// (DamageText_update). Pure / Godot-free so it is unit-testable (the draw node lives in game/).

using System.Globalization;
using System.Text;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The damage-text number formatting + size mapping — the testable, Godot-free core of the CSQC
/// <c>DamageText_update</c> (common/mutators/mutator/damagetext/cl_damagetext.qc). Builds the on-screen label
/// from <c>cl_damagetext_format</c> (the <c>{health}{armor}{total}{potential}{potential_health}</c> token
/// replacement, with the verbose + hide-redundant variants) and maps the potential damage to a font size via
/// <c>map_bound_ranges</c>. Inputs are the PRECISION-multiplied amounts the wire/store carry (QC's
/// <c>m_healthdamage</c>/<c>m_armordamage</c>/<c>m_potential_damage</c>); they're divided by the precision
/// multiplier and rounded exactly as QC does.
/// </summary>
public static class DamageTextFormat
{
    /// <summary>
    /// Port of the format-token replacement in <c>DamageText_update</c>. <paramref name="health"/>,
    /// <paramref name="armor"/>, <paramref name="potential"/> are PRECISION-multiplied (the stored fields);
    /// they're divided by <see cref="DamageTextWire.PrecisionMultiplier"/> and rounded. The tokens:
    /// <c>{health}</c>, <c>{armor}</c>, <c>{total}</c> (= health+armor), <c>{potential}</c>,
    /// <c>{potential_health}</c> (= potential - armor). With <paramref name="verbose"/>, {health}/{total} show
    /// "actual (potential)" when they differ; with <paramref name="hideRedundant"/>, {armor} is hidden when 0
    /// and {potential}/{potential_health} when equal to the actual within 5. Unknown <c>{...}</c> tokens are
    /// stripped (QC's futureproofing), then leading/trailing spaces are trimmed.
    /// </summary>
    public static string Build(string format, bool verbose, bool hideRedundant,
        float health, float armor, float potential)
    {
        const int mul = DamageTextWire.PrecisionMultiplier;
        int h  = Rint(health / mul);
        int a  = Rint(armor / mul);
        int t  = Rint((health + armor) / mul);
        int p  = Rint(potential / mul);
        int ph = Rint((potential - armor) / mul);

        bool redundant = AlmostEqualsEps(health + armor, potential, 5f);

        string s = format ?? "";
        s = Replace(s, "{armor}",
            (armor == 0f && hideRedundant) ? "" : a.ToString(CultureInfo.InvariantCulture));
        s = Replace(s, "{potential}",
            (redundant && hideRedundant) ? "" : p.ToString(CultureInfo.InvariantCulture));
        s = Replace(s, "{potential_health}",
            (redundant && hideRedundant) ? "" : ph.ToString(CultureInfo.InvariantCulture));
        s = Replace(s, "{health}",
            (h == ph || !verbose) ? h.ToString(CultureInfo.InvariantCulture)
                                  : $"{h} ({ph})");
        s = Replace(s, "{total}",
            (t == p || !verbose) ? t.ToString(CultureInfo.InvariantCulture)
                                 : $"{t} ({p})");

        // Futureproofing: strip any remaining unknown {...} tokens.
        s = StripUnknownTokens(s);

        return s.Trim(' ');
    }

    /// <summary>
    /// Port of <c>map_bound_ranges(potential, size_min_damage, size_max_damage, size_min, size_max)</c>
    /// (lib/math): linearly map <paramref name="value"/> from the input range
    /// [<paramref name="inMin"/>, <paramref name="inMax"/>] onto [<paramref name="outMin"/>,
    /// <paramref name="outMax"/>], clamped to the output range. <paramref name="value"/> is the
    /// un-multiplied potential damage.
    /// </summary>
    public static float MapSize(float value, float inMin, float inMax, float outMin, float outMax)
    {
        if (inMax == inMin) return outMin;
        float f = (value - inMin) / (inMax - inMin);
        float outv = outMin + f * (outMax - outMin);
        float lo = MathF.Min(outMin, outMax), hi = MathF.Max(outMin, outMax);
        return MathF.Max(lo, MathF.Min(hi, outv));
    }

    /// <summary>QC rint(): round-half-to-even (banker's rounding), matching the engine's rint builtin.</summary>
    private static int Rint(float v) => (int)MathF.Round(v, MidpointRounding.ToEven);

    private static bool AlmostEqualsEps(float a, float b, float eps) => MathF.Abs(a - b) < eps;

    private static string Replace(string s, string token, string value) => s.Replace(token, value);

    private static string StripUnknownTokens(string s)
    {
        // QC: while there's a "{" with a matching "}" after it, cut the {...} span. (Mirrors the loop exactly.)
        while (true)
        {
            int open = s.IndexOf('{');
            if (open < 0) break;
            int close = s.IndexOf('}', open);
            if (close < 0 || close <= open) break;
            s = string.Concat(s.AsSpan(0, open), s.AsSpan(close + 1));
        }
        return s;
    }
}
