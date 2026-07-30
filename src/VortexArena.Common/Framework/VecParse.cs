using System;
using System.Globalization;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// The canonical "parse a float vector from a string" helper for CVAR/CLI values. The codebase had
/// accumulated ~9 private copies (MapLoader, NetGame, ViewModel, HudPanel, ViewEffects, …), each with
/// subtly different separator and error semantics — new code should call this instead of adding copy N+1.
///
/// <para>Semantics: invariant culture; WHITESPACE separators (matching the <c>Split(null)</c> the migrated
/// call sites used); ALL tokens must parse and at least <c>min</c> values must be present, else the whole
/// parse fails (no zero-fill, no partial results — a malformed config value should be loud at the call
/// site, not silently munged).</para>
///
/// <para>Comma is NOT a separator, deliberately. DP's <c>Math_atov</c> splits on space/tab only, and
/// treating ',' as a separator silently turns the decimal-comma typo <c>"-1,5 -1 -1"</c> into the four
/// valid tokens (-1, 5, -1, -1) — a bright-green ghost instead of the intended loud fallback. That is the
/// opposite of this class's stated contract.</para>
///
/// <para>Note this is NOT a drop-in for the DP-faithful <c>stov</c> copies in <c>Cheats.StoV</c>,
/// <c>SandboxMutator</c>, and <c>MapLoader.ParseVec3</c>: those ZERO-FILL missing components per
/// <c>Math_atov</c>, while this fails the parse. Do not migrate them to this helper without preserving
/// that difference.</para>
/// </summary>
public static class VecParse
{

    /// <summary>
    /// Parse <paramref name="s"/> as a list of floats. Returns false (and <paramref name="vals"/> = empty)
    /// when the string is null/empty, any token fails to parse, or fewer than <paramref name="min"/> values
    /// are present. On success <paramref name="vals"/> holds every parsed value (callers may accept more
    /// than <paramref name="min"/> — e.g. an optional yaw/pitch tail after an x y z position).
    /// </summary>
    public static bool TryParseFloats(string? s, int min, out float[] vals)
    {
        vals = Array.Empty<float>();
        if (string.IsNullOrWhiteSpace(s))
            return false;
        // Split(null) = every Unicode whitespace char, exactly what the migrated call sites did (a value
        // pasted with a newline or non-breaking space keeps working).
        string[] parts = s!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < min)
            return false;
        var parsed = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }
        vals = parsed;
        return true;
    }
}
