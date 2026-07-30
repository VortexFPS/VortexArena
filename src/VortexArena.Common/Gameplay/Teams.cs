using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Team identity (QC NUM_TEAM_* color codes, common/teams.qh). Entity.Team holds the team value.
/// Minimal shared contract so the team gametypes (CTF/TDM/CA/...) and mutators agree.
/// </summary>
public static class Teams
{
    public const int None = 0;
    public const int Red = 4;     // NUM_TEAM_1
    public const int Blue = 13;   // NUM_TEAM_2
    public const int Yellow = 12; // NUM_TEAM_3
    public const int Pink = 9;    // NUM_TEAM_4

    public static readonly int[] All = { Red, Blue, Yellow, Pink };

    /// <summary>Human label for a team color code.</summary>
    public static string Name(int team) => team switch
    {
        Red => "Red",
        Blue => "Blue",
        Yellow => "Yellow",
        Pink => "Pink",
        _ => "Neutral",
    };

    /// <summary>
    /// QC <c>Team_ColorCode(teamid)</c> (common/teams.qh:63): the chat color escape for a team — fed as the
    /// <c>%s</c> tint prefix of team-aware notifications ("Your %steammate…" — Send_Notification's
    /// Team_ColorCode(player.team) arg). Non-team values fall through to white (^7), matching Base.
    /// </summary>
    public static string ColorCode(int team) => team switch
    {
        Red => "^1",
        Blue => "^4",
        Yellow => "^3",
        Pink => "^6",
        _ => "^7",
    };

    /// <summary>The first <paramref name="count"/> teams (2..4) used by a gametype.</summary>
    public static IEnumerable<int> Active(int count)
    {
        for (int i = 0; i < count && i < All.Length; i++) yield return All[i];
    }

    /// <summary>RAW same-team-value compare. NOT the full QC <c>SAME_TEAM(a,b)</c>, which is
    /// <c>teamplay ? (a.team == b.team) : (a == b)</c> — callers on paths that can run OUTSIDE team modes must
    /// pair this with their own teamplay/teamGame gate (as <c>Scores.cs</c> does: <c>teamGame &amp;&amp;
    /// SameTeam(…)</c>). This matters because in FFA a player's <c>.team</c> still carries a pants-color-derived
    /// NON-zero value (Quake tradition), so two like-colored players compare EQUAL here — an ungated caller then
    /// runs friendly-fire logic in DEATHMATCH (playtest #27: the damage path did exactly that — mirror/complain
    /// accrual + the "I'm on your team!" teamshoot voice on plain DM hits). Kept raw (not reading the
    /// <c>GameScores.Teamplay</c> static) so the predicate stays order-independent for tests and non-match tools.</summary>
    public static bool SameTeam(Entity a, Entity b) => a.Team != 0 && a.Team == b.Team;

    /// <summary>
    /// QC <c>Team_ColorRGB(teamid)</c> (common/teams.qh:76) — the BRIGHT per-team RGB used to tint particle
    /// bursts (the spawn-event flash, client/spawnpoints.qc:58) and waypoint sprites. Switches on the CSQC team
    /// numbering (NUM_TEAM_1..4 = 4/13/12/9), which is exactly the port's <see cref="Red"/>/<see cref="Blue"/>/
    /// <see cref="Yellow"/>/<see cref="Pink"/> constants, so no team-id offset is needed (Entity.Team already
    /// holds the CSQC value). Any other team (incl. <see cref="None"/>) falls through to white, matching Base's
    /// <c>return '1 1 1'</c>. Distinct from the darker radar palette — these are the saturated flash colors.
    /// </summary>
    public static Vector3 ColorRgb(int team) => team switch
    {
        Red    => new Vector3(1f, 0.0625f, 0.0625f),   // 0xFF0F0F
        Blue   => new Vector3(0.0625f, 0.0625f, 1f),   // 0x0F0FFF
        Yellow => new Vector3(1f, 1f, 0.0625f),        // 0xFFFF0F
        Pink   => new Vector3(1f, 0.0625f, 1f),        // 0xFF0FFF
        _      => new Vector3(1f, 1f, 1f),
    };

    /// <summary>
    /// QC <c>colormapPaletteColor(c, isPants:true)</c> (lib/color.qh) over the 0..15 colormap palette — the
    /// player's individual pants-nibble color, used to tint the spawn-event flash in NON-team play
    /// (<c>entcs_GetColor</c>, client/spawnpoints.qc:58). <paramref name="c"/> is the low colormap nibble
    /// (<c>clientcolors &amp; 15</c>). Cases 0..14 are the static palette; case 15 is the animated rainbow, which
    /// needs <paramref name="time"/> (the sim clock) for its phase. Mirrors the Engine-layer
    /// <c>CsqcModelAppearance.ColormapPaletteColor</c> table exactly (the Engine copy can't be referenced from
    /// here — Common must not depend on Engine — so the pure 16-entry table is duplicated for the server emit).
    /// </summary>
    public static Vector3 ColormapPaletteColor(int c, float time) => (c & 15) switch
    {
        0  => new Vector3(1.000000f, 1.000000f, 1.000000f),
        1  => new Vector3(1.000000f, 0.333333f, 0.000000f),
        2  => new Vector3(0.000000f, 1.000000f, 0.501961f),
        3  => new Vector3(0.000000f, 1.000000f, 0.000000f),
        4  => new Vector3(1.000000f, 0.000000f, 0.000000f),
        5  => new Vector3(0.000000f, 0.666667f, 1.000000f),
        6  => new Vector3(0.000000f, 1.000000f, 1.000000f),
        7  => new Vector3(0.501961f, 1.000000f, 0.000000f),
        8  => new Vector3(0.501961f, 0.000000f, 1.000000f),
        9  => new Vector3(1.000000f, 0.000000f, 1.000000f),
        10 => new Vector3(1.000000f, 0.000000f, 0.501961f),
        11 => new Vector3(0.000000f, 0.000000f, 1.000000f),
        12 => new Vector3(1.000000f, 1.000000f, 0.000000f),
        13 => new Vector3(0.000000f, 0.333333f, 1.000000f),
        14 => new Vector3(1.000000f, 0.666667f, 0.000000f),
        // case 15: the animated rainbow (lib/color.qh:32-40), isPants phase (M_E period).
        _  => RainbowPants(time),
    };

    private static Vector3 RainbowPants(float time)
    {
        const float ME = 2.718281828459045f;
        const float MPI = 3.141592653589793f;
        return new Vector3(
            0.502f + 0.498f * System.MathF.Sin(time / ME + 0f),
            0.502f + 0.498f * System.MathF.Sin(time / ME + MPI * 2f / 3f),
            0.502f + 0.498f * System.MathF.Sin(time / ME + MPI * 4f / 3f));
    }
}
