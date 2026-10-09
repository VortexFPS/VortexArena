// Port of Base/darkplaces/cl_main.c CL_RelinkLightFlashes ("light animations") as it applies to the
// lightmaps of a Quake 1 format map: r_refdef.scene.lightstylevalue, which gl_rsurf.c R_BuildLightMap
// multiplies each style layer of a face by.
namespace VortexArena.Formats.Bsp;

/// <summary>
/// Light styles: a string of letters a server sends for each style number, stepped ten times a second.
/// 'a' is dark, 'm' is normal light, 'z' is double. Style 0 is the ordinary light of a map ("m"), 1 to 11
/// are Quake's flickers and pulses, 32 and up are lights a trigger switches ("m" or "a").
/// </summary>
public static class Q1LightStyles
{
    /// <summary>MAX_LIGHTSTYLES of the map format: a face names a style in one byte, 255 meaning "no layer".</summary>
    public const int Count = 256;
    public const int Unused = 255;

    /// <summary>
    /// r_refdef.scene.lightstylevalue for one style at <paramref name="time"/> (cl.time): 256 is a multiplier
    /// of one. A style without a string is 256. A letter is worth <c>(letter - 'a') * 22</c>, so the usual "m"
    /// is 264: a Quake map under DarkPlaces is 3 % brighter than its samples. With
    /// <paramref name="lerp"/> (r_lerplightstyles 1; off by default) the letter is blended with the one before it.
    /// A "=number" style is that number (as a multiplier of one).
    /// </summary>
    public static int Value(string? style, double time, bool lerp = false)
    {
        if (string.IsNullOrEmpty(style)) return 256;
        if (style[0] == '=')
        {
            // DarkPlaces stores the number itself into the integer value here, which would make such a style
            // all but black on a lightmap; the multiplier the number plainly means is used instead.
            return float.TryParse(style.AsSpan(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number) && float.IsFinite(number)
                ? (int)Math.Clamp(number * 256f, 0f, 65535f) : 0;
        }
        double f = time * 10;
        if (!double.IsFinite(f)) f = 0;
        long i = (long)Math.Floor(f);
        float frac = (float)(f - i);
        int k = style[Modulo(i, style.Length)] - 'a';
        if (!lerp) return Math.Clamp(k * 22, 0, 65535);
        int l = style[Modulo(i - 1, style.Length)] - 'a';
        // "(unsigned short)(((k*frac)+(l*(1-frac)))*22)"
        return (int)Math.Clamp((k * frac + l * (1 - frac)) * 22, 0f, 65535f);
    }

    /// <summary>
    /// The multiplier of every style at <paramref name="time"/>, as the lightmap shader wants it (value / 256),
    /// into <paramref name="scales"/> (at least <see cref="Count"/> long). Entry 255 is always 0: it is the
    /// "no layer" marker of a face. Returns true if any entry changed.
    /// </summary>
    public static bool Evaluate(IReadOnlyList<string?> styles, double time, bool lerp, Span<float> scales)
    {
        bool changed = false;
        for (int j = 0; j < Count; j++)
        {
            float value = j == Unused ? 0f : Value(j < styles.Count ? styles[j] : null, time, lerp) * (1f / 256f);
            if (scales[j] != value)
            {
                scales[j] = value;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// What gl_rsurf.c R_BuildLightMap stores for one channel of one sample: every layer's sample times its
    /// style's value, summed, shifted down by eight, at most 255. Drawn, the texel is this over 255, times two.
    /// </summary>
    public static int Combine(ReadOnlySpan<byte> samples, ReadOnlySpan<int> styleValues)
    {
        int sum = 0;
        for (int i = 0; i < samples.Length && i < styleValues.Length; i++) sum += samples[i] * styleValues[i];
        return Math.Min(sum >> 8, 255);
    }

    private static int Modulo(long value, int length) => (int)(((value % length) + length) % length);
}
