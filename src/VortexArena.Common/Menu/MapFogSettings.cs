using System.Globalization;
using System.Numerics;

namespace VortexArena.Common.Menu;

/// <summary>A validated native map fog value from worldspawn, mapinfo, or the client-init fog field.</summary>
public readonly record struct MapFogSettings(float Density, Vector3 Colour, float Alpha, float Start, float End)
{
    public const string None = "none";

    public static bool TryParse(string? text, out MapFogSettings fog)
    {
        fog = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || !MapTempDirective.IsSafeValue(text))
            return false;
        if (text.Trim() == None)
            return true;

        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // DarkPlaces fog also accepts height and fade depth. Validate them here, even though native depth fog
        // uses only the first seven fields.
        if (parts.Length > 9)
            return false;
        Span<float> values = stackalloc float[] { 0f, 0.3f, 0.3f, 0.3f, 1f, 0f, 16384f, 0f, 0f };
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !float.IsFinite(values[i]))
                return false;

        float end = values[6] > 0f ? values[6] : 16384f;
        if (values[5] > end)
            return false;
        var colour = Vector3.Clamp(new Vector3(values[1], values[2], values[3]), Vector3.Zero, Vector3.One);
        fog = new MapFogSettings(values[0], colour, values[4], values[5], end);
        return true;
    }
}
