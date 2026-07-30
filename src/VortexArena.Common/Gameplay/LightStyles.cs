// Port of the worldspawn animated-lightstyle table (qcsrc/server/world.qc:882-920): the 12 named styles
// (0-11) plus the style-63 test pattern the server installs via lightstyle(N, "<frames>") at map init. Each
// frame char encodes a brightness: 'a' = 0.0 (dark) .. 'm' = 1.0 (normal) .. 'z' = 2.0 (double) — the classic
// Quake light-animation convention, evaluated at 10 frames/second. Map lights (and dynlights) that reference a
// style index animate by sampling the current frame of their style string.
//
// In Base these are installed engine-side (the DP engine drives the lightmap modulation from the style table).
// The port has no animated-lightstyle render consumer yet (DynamicLightRenderer renders a styled dynlight at a
// steady radius — see its "Known approximations" note), so this table is the SERVER-side authority half: the
// world-rules layer installs it at worldspawn (GameWorld), making the named strings available for a future
// client lightstyle-animation consumer. Styles 32-62 are assigned at runtime by switchable spawnfunc_light
// entities (not part of this fixed table).

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// QC the worldspawn lightstyle table (server/world.qc:882-920) — the 12 named animated styles + style 63.
/// Installed once per map by the world-rules boot (GameWorld). <see cref="Frames"/> indexed by style number
/// gives the animation string; <see cref="Sample"/> evaluates a style's brightness at a sim time.
/// </summary>
public static class LightStyles
{
    /// <summary>Highest style index this fixed table populates (style 63 = the test pattern). 0..63.</summary>
    public const int MaxStyle = 63;

    /// <summary>Quake light animation rate: 10 frames per second (each frame char is 0.1 s).</summary>
    public const float FramesPerSecond = 10f;

    // The installed style strings, indexed by style number. Unset entries are null (no animation = steady).
    private static readonly string?[] _frames = new string?[MaxStyle + 1];

    /// <summary>The installed animation string for <paramref name="style"/> (null = not in the named table).</summary>
    public static string? Frames(int style)
        => style >= 0 && style <= MaxStyle ? _frames[style] : null;

    /// <summary>
    /// Install the fixed worldspawn lightstyle table (QC the lightstyle(0..11)+lightstyle(63) calls in
    /// spawnfunc(worldspawn)). Idempotent — safe to call once per map at boot. The exact frame strings are
    /// verbatim from server/world.qc:882-920.
    /// </summary>
    public static void InstallWorldspawnTable()
    {
        for (int i = 0; i < _frames.Length; i++)
            _frames[i] = null;

        _frames[0] = "m";                                                   // 0 normal
        _frames[1] = "mmnmmommommnonmmonqnmmo";                             // 1 FLICKER (first variety)
        _frames[2] = "abcdefghijklmnopqrstuvwxyzyxwvutsrqponmlkjihgfedcba"; // 2 SLOW STRONG PULSE
        _frames[3] = "mmmmmaaaaammmmmaaaaaabcdefgabcdefg";                  // 3 CANDLE (first variety)
        _frames[4] = "mamamamamama";                                        // 4 FAST STROBE
        _frames[5] = "jklmnopqrstuvwxyzyxwvutsrqponmlkj";                   // 5 GENTLE PULSE 1
        _frames[6] = "nmonqnmomnmomomno";                                   // 6 FLICKER (second variety)
        _frames[7] = "mmmaaaabcdefgmmmmaaaammmaamm";                        // 7 CANDLE (second variety)
        _frames[8] = "mmmaaammmaaammmabcdefaaaammmmabcdefmmmaaaa";          // 8 CANDLE (third variety)
        _frames[9] = "aaaaaaaazzzzzzzz";                                    // 9 SLOW STROBE (fourth variety)
        _frames[10] = "mmamammmmammamamaaamammma";                          // 10 FLUORESCENT FLICKER
        _frames[11] = "abcdefghijklmnopqrrqponmlkjihgfedcba";               // 11 SLOW PULSE NOT FADE TO BLACK
        // styles 32-62 are assigned by the spawnfunc_light program for switchable lights
        _frames[63] = "a";                                                  // 63 testing
    }

    /// <summary>
    /// Sample a style's brightness at <paramref name="time"/> seconds (the classic Quake decode:
    /// frame char 'a'..'z' → 0.0..2.0, looped at <see cref="FramesPerSecond"/>). Returns 1.0 (normal) for a
    /// style with no installed string, so an unstyled / out-of-table index renders steady.
    /// </summary>
    public static float Sample(int style, float time)
    {
        string? s = Frames(style);
        if (string.IsNullOrEmpty(s))
            return 1f;
        int frame = (int)(time * FramesPerSecond) % s!.Length;
        if (frame < 0) frame += s.Length;
        // Quake light decode: 'a' = 0.0 (dark), 'm' = 1.0 (normal), 'z' = ~2.08 (bright). Normalized so 'm'
        // (index 12) is exactly the "normal" light level — (char - 'a') / ('m' - 'a').
        return (s[frame] - 'a') / (float)('m' - 'a');
    }
}
