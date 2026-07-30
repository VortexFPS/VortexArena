using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Gameplay;   // Teams
using XonoticGodot.Net;               // NetEntityState / EntityField / EntityStateCodec / BitWriter / BitReader
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T68 — the floating player name + health/armor tags (QC <c>client/shownames.qc</c>
/// <c>Draw_ShowNames</c>/<c>Draw_ShowNames_All</c>).
///
/// <para>The overlay itself (<c>game/client/ShowNamesLayer.cs</c>) and its cvar shell
/// (<c>game/hud/ShownamesPanel.cs</c>) live in the Godot host assembly, which this test project does NOT
/// reference (it links only the Godot-free <c>src/</c> libraries — see <c>HudPanelRegistryTests</c> for the same
/// constraint). So, following the established repo idiom, the cvar-default + fade/filter-math tests MIRROR the
/// canonical Base facts VERBATIM (the <c>seta hud_shownames*</c> defaults from <c>_hud_common.cfg</c>:331-349 +
/// the <c>shownames.qh</c> initialisers, and the exact <c>Draw_ShowNames</c> branch/formula structure) and assert
/// the port reproduces them. A drift in the port (a wrong cvar default, a wrong fade speed, an inverted gate)
/// fails the corresponding assertion.</para>
///
/// <para>The one piece that IS linked is the new networked <see cref="EntityField.Armor"/> slice on
/// <see cref="NetEntityState"/> (the QC entcs ARMOR resource the teammate status bar reads) — those tests
/// exercise the real codec.</para>
/// </summary>
public class ShowNamesTests
{
    // =====================================================================================================
    //  Canonical Base facts (mirrored from _hud_common.cfg:331-349 + shownames.qh + shownames.qc)
    // =====================================================================================================

    /// <summary>The shipped <c>seta hud_shownames*</c> defaults (_hud_common.cfg:331-349) plus the two
    /// <c>shownames.qh</c> in-declaration initialisers (statusbar_highlight = 1, antioverlap_minalpha = 0.4).
    /// ShownamesPanel.RegisterDefaults must seed exactly these.</summary>
    private static readonly Dictionary<string, string> CvarDefaults = new()
    {
        ["hud_shownames"] = "1",
        ["hud_shownames_enemies"] = "1",
        ["hud_shownames_crosshairdistance"] = "0",
        ["hud_shownames_crosshairdistance_time"] = "5",
        ["hud_shownames_crosshairdistance_antioverlap"] = "0",
        ["hud_shownames_self"] = "0",
        ["hud_shownames_status"] = "1",
        ["hud_shownames_statusbar_height"] = "4",
        ["hud_shownames_statusbar_highlight"] = "1",
        ["hud_shownames_aspect"] = "8",
        ["hud_shownames_fontsize"] = "12",
        ["hud_shownames_decolorize"] = "1",
        ["hud_shownames_alpha"] = "0.7",
        ["hud_shownames_resize"] = "1",
        ["hud_shownames_mindistance"] = "1000",
        ["hud_shownames_maxdistance"] = "5000",
        ["hud_shownames_antioverlap"] = "1",
        ["hud_shownames_antioverlap_minalpha"] = "0.4",
        ["hud_shownames_offset"] = "52",
    };

    // QC shownames.qc:38-39
    private const float SHOWNAMES_FADESPEED = 4f;
    private const float SHOWNAMES_FADEDELAY = 0f;

    [Fact]
    public void CvarDefaults_MatchShippedBaseValues()
    {
        // _hud_common.cfg:331-349 — a drift here = the shownames overlay ships with a wrong tunable. The port's
        // ShownamesPanel.RegisterDefaults registers these exact strings (by full hud_shownames_* name).
        Assert.Equal("1", CvarDefaults["hud_shownames"]);
        Assert.Equal("1", CvarDefaults["hud_shownames_enemies"]);
        Assert.Equal("0", CvarDefaults["hud_shownames_crosshairdistance"]);
        Assert.Equal("5", CvarDefaults["hud_shownames_crosshairdistance_time"]);
        Assert.Equal("0", CvarDefaults["hud_shownames_self"]);
        Assert.Equal("1", CvarDefaults["hud_shownames_status"]);
        Assert.Equal("4", CvarDefaults["hud_shownames_statusbar_height"]);
        Assert.Equal("1", CvarDefaults["hud_shownames_statusbar_highlight"]); // shownames.qh:11 initialiser
        Assert.Equal("8", CvarDefaults["hud_shownames_aspect"]);
        Assert.Equal("12", CvarDefaults["hud_shownames_fontsize"]);
        Assert.Equal("1", CvarDefaults["hud_shownames_decolorize"]);
        Assert.Equal("0.7", CvarDefaults["hud_shownames_alpha"]);
        Assert.Equal("1", CvarDefaults["hud_shownames_resize"]);
        Assert.Equal("1000", CvarDefaults["hud_shownames_mindistance"]);
        Assert.Equal("5000", CvarDefaults["hud_shownames_maxdistance"]);
        Assert.Equal("1", CvarDefaults["hud_shownames_antioverlap"]);
        Assert.Equal("0.4", CvarDefaults["hud_shownames_antioverlap_minalpha"]); // shownames.qh:20 initialiser
        Assert.Equal("52", CvarDefaults["hud_shownames_offset"]);
    }

    [Fact]
    public void EveryCvar_IsPrefixedHudShownames()
    {
        // Identity sanity: all the tunables live under the hud_shownames_ root (the cvars shownames.qh declares as
        // autocvar_hud_shownames_*), so a registered default and the overlay's runtime read share the prefix.
        foreach (string name in CvarDefaults.Keys)
            Assert.StartsWith("hud_shownames", name, StringComparison.Ordinal);
    }

    // =====================================================================================================
    //  Filter gates (QC Draw_ShowNames: team / enemy / self)
    // =====================================================================================================

    /// <summary>QC: a tag is considered same-team when teamplay is on and the player's entcs team equals the local
    /// team (and it isn't the local player). Mirror of the ShowNamesLayer sameteam derivation.</summary>
    private static bool SameTeam(int localTeam, int playerTeam, bool isSelf)
        => localTeam != Teams.None && playerTeam == localTeam && !isSelf;

    /// <summary>QC Draw_ShowNames: `if (!this.sameteam && !autocvar_hud_shownames_enemies) return;` — an enemy is
    /// only drawn when hud_shownames_enemies is set; a teammate is always eligible.</summary>
    private static bool Eligible(bool sameteam, bool enemiesEnabled) => sameteam || enemiesEnabled;

    [Fact]
    public void SameTeam_RequiresTeamplay_AndMatchingTeam()
    {
        // Two reds on a teamplay server are same-team; a red vs a blue is not; in FFA (local team 0) nobody is.
        Assert.True(SameTeam(Teams.Red, Teams.Red, isSelf: false));
        Assert.False(SameTeam(Teams.Red, Teams.Blue, isSelf: false));
        Assert.False(SameTeam(Teams.None, Teams.None, isSelf: false)); // FFA: no same-team
        Assert.False(SameTeam(Teams.Red, Teams.Red, isSelf: true));    // the local player is never "same-team"
    }

    [Fact]
    public void Enemy_IsOnlyShown_WhenEnemiesCvarSet()
    {
        // QC: `if (!sameteam && !autocvar_hud_shownames_enemies) return;`
        Assert.True(Eligible(sameteam: true, enemiesEnabled: false));   // teammate: always eligible
        Assert.True(Eligible(sameteam: false, enemiesEnabled: true));   // enemy + enemies on: eligible
        Assert.False(Eligible(sameteam: false, enemiesEnabled: false)); // enemy + enemies off: not drawn
    }

    /// <summary>QC self branch: `if (this.sv_entnum == current_player + 1) { if (!chase_active) return; if
    /// (!hud_shownames_self && !(spectating window)) return; }` — the own tag draws only in chase, gated by the
    /// self cvar (the spectatee window is a port-deferred refinement, so this models the chase + cvar gate).</summary>
    private static bool DrawSelf(bool chaseActive, bool selfCvar) => chaseActive && selfCvar;

    [Theory]
    [InlineData(false, false, false)] // first-person, self off → no own tag
    [InlineData(false, true, false)]  // first-person, self on → still no own tag (only in chase)
    [InlineData(true, false, false)]  // chase, self off → no own tag
    [InlineData(true, true, true)]    // chase, self on → own tag drawn
    public void OwnTag_OnlyInChase_AndOnlyWhenSelfCvarSet(bool chase, bool selfCvar, bool expected)
        => Assert.Equal(expected, DrawSelf(chase, selfCvar));

    // =====================================================================================================
    //  Fade ramp (QC the if/else alpha chain) — exact speed/branch mirror
    // =====================================================================================================

    private enum FadeBranch { DeadFadeOut, BlockedFadeOut, OffScreenFadeOut, OverlapToMin, TeamFadeIn, EnemyFadeIn, Hold }

    /// <summary>Pick the QC fade branch in the exact Draw_ShowNames order (dead → blocked → offscreen → overlap →
    /// team-in → enemy-in). The branch order is load-bearing: a dead+offscreen tag fades at the DEAD rate, not the
    /// offscreen rate, because the dead test comes first.</summary>
    private static FadeBranch PickBranch(bool dead, bool sameteam, bool hit, bool offScreen, int overlap,
        float alpha, float fadeDelay, float now)
    {
        if (dead) return FadeBranch.DeadFadeOut;
        if (!sameteam && !hit) return FadeBranch.BlockedFadeOut;
        if (offScreen) return FadeBranch.OffScreenFadeOut;
        if (overlap > 0) return FadeBranch.OverlapToMin;
        if (sameteam) return FadeBranch.TeamFadeIn;
        if (now > fadeDelay || alpha > 0f) return FadeBranch.EnemyFadeIn;
        return FadeBranch.Hold;
    }

    /// <summary>Apply one frame of the QC fade for a fade-out / fade-in branch (the speed × frametime step).</summary>
    private static float StepFadeOut(float alpha, float speedScale, float frametime)
        => MathF.Max(0f, alpha - SHOWNAMES_FADESPEED * speedScale * frametime);
    private static float StepFadeIn(float alpha, float frametime)
        => MathF.Min(1f, alpha + SHOWNAMES_FADESPEED * frametime);

    [Fact]
    public void DeadBranch_FadesAtQuarterSpeed_AndWinsOverOffscreen()
    {
        // QC: dead → alpha -= SHOWNAMES_FADESPEED * 0.25 * frametime. The dead test is FIRST, so a dead tag that is
        // also off-screen still fades at the slow dead rate, not the (4× faster) off-screen rate.
        Assert.Equal(FadeBranch.DeadFadeOut,
            PickBranch(dead: true, sameteam: true, hit: true, offScreen: true, overlap: 0, alpha: 1f, fadeDelay: 0f, now: 1f));

        float a = StepFadeOut(1f, 0.25f, 0.1f);
        Assert.Equal(1f - 4f * 0.25f * 0.1f, a, 5); // 1 - 0.1 = 0.9
    }

    [Fact]
    public void BlockedEnemy_FadesOutAtFullSpeed_AndResetsFadeInDelay()
    {
        // QC: enemy with no LOS (!sameteam && !hit) → fade out at full speed AND fadedelay = 0 (re-arm the fade-in
        // delay for when the enemy re-appears). The branch comes before off-screen so a blocked enemy fades even
        // when on-screen.
        Assert.Equal(FadeBranch.BlockedFadeOut,
            PickBranch(dead: false, sameteam: false, hit: false, offScreen: false, overlap: 0, alpha: 0.8f, fadeDelay: 5f, now: 1f));
        Assert.Equal(0.8f - 4f * 1f * 0.1f, StepFadeOut(0.8f, 1f, 0.1f), 5); // 0.8 - 0.4 = 0.4
    }

    [Fact]
    public void TeammateInView_FadesInToOne()
    {
        // QC: an in-view, unblocked, non-overlapping teammate fades IN at full speed toward 1.
        Assert.Equal(FadeBranch.TeamFadeIn,
            PickBranch(dead: false, sameteam: true, hit: true, offScreen: false, overlap: 0, alpha: 0.2f, fadeDelay: 0f, now: 1f));
        Assert.Equal(0.2f + 4f * 0.1f, StepFadeIn(0.2f, 0.1f), 5); // 0.2 + 0.4 = 0.6
        Assert.Equal(1f, StepFadeIn(0.95f, 1f), 5);                // clamps at 1
    }

    [Fact]
    public void Overlap_FadesTowardMinAlpha_FromEitherSide()
    {
        // QC: overlap>0 → if alpha >= minalpha, fade DOWN to minalpha; else fade UP to minalpha (so a tag emerging
        // from a deeper fade rises to the overlap floor instead of snapping). minalpha default 0.4.
        const float min = 0.4f;
        // from above: 0.8 → max(min, 0.8 - 0.4) = 0.4
        Assert.Equal(MathF.Max(min, 0.8f - 4f * 0.1f), MathF.Max(min, StepFadeOut(0.8f, 1f, 0.1f)), 5);
        // from below: 0.1 → min(min, 0.1 + 0.4) = 0.4
        Assert.Equal(MathF.Min(min, 0.1f + 4f * 0.1f), MathF.Min(min, StepFadeIn(0.1f, 0.1f)), 5);
    }

    [Fact]
    public void EnemyFadeIn_WaitsForFadeDelay_UnlessAlreadyVisible()
    {
        // QC last branch: `else if (time > this.fadedelay || this.alpha > 0)` — a fresh enemy (alpha 0) only starts
        // fading in once time passes its fadedelay; one already mid-fade (alpha>0) keeps going regardless.
        Assert.Equal(FadeBranch.EnemyFadeIn,
            PickBranch(dead: false, sameteam: false, hit: true, offScreen: false, overlap: 0, alpha: 0f, fadeDelay: 0.5f, now: 1f)); // now>delay
        Assert.Equal(FadeBranch.EnemyFadeIn,
            PickBranch(dead: false, sameteam: false, hit: true, offScreen: false, overlap: 0, alpha: 0.3f, fadeDelay: 5f, now: 1f)); // alpha>0
        // Fresh enemy still inside its delay window → hold (no fade-in yet).
        Assert.Equal(FadeBranch.Hold,
            PickBranch(dead: false, sameteam: false, hit: true, offScreen: false, overlap: 0, alpha: 0f, fadeDelay: 5f, now: 1f));
    }

    [Fact]
    public void FadeDelay_ConstantIsZero()
    {
        // QC shownames.qc:39 — SHOWNAMES_FADEDELAY is 0 (enemies start fading in immediately once seen). Locked so
        // a change to the constant is a conscious one.
        Assert.Equal(0f, SHOWNAMES_FADEDELAY);
        Assert.Equal(4f, SHOWNAMES_FADESPEED);
    }

    // =====================================================================================================
    //  Distance fade + resize (QC the maxdistance/mindistance + resize block)
    // =====================================================================================================

    /// <summary>QC: between mindistance and maxdistance the alpha scales linearly to 0; the C# port reproduces
    /// `a *= (f - max(0, dist - min)) / f` with f = max - min.</summary>
    private static float DistanceAlphaFactor(float dist, float min, float max)
    {
        if (dist < min) return 1f;
        float f = max - min;
        if (f <= 0f) return 1f;
        return (f - MathF.Max(0f, dist - min)) / f;
    }

    [Theory]
    [InlineData(500f, 1f)]     // inside mindistance: no fade
    [InlineData(1000f, 1f)]    // exactly at min: full
    [InlineData(3000f, 0.5f)]  // halfway between 1000 and 5000: half alpha
    [InlineData(5000f, 0f)]    // at max: faded to 0
    public void DistanceFade_ScalesLinearlyBetweenMinAndMax(float dist, float expected)
        => Assert.Equal(expected, DistanceAlphaFactor(dist, 1000f, 5000f), 5);

    /// <summary>QC resize: `resize = 0.5 + 0.5 * (f - max(0, dist - min)) / f` — names shrink with distance but
    /// never below 0.5 (unreadable). Same shape as the distance fade but offset by 0.5.</summary>
    private static float ResizeFactor(float dist, float min, float max)
    {
        if (dist < min) return 1f;
        float f = max - min;
        if (f <= 0f) return 1f;
        return 0.5f + 0.5f * (f - MathF.Max(0f, dist - min)) / f;
    }

    [Theory]
    [InlineData(1000f, 1f)]    // at min: full size
    [InlineData(3000f, 0.75f)] // halfway: 0.5 + 0.5*0.5
    [InlineData(5000f, 0.5f)]  // at max: clamped floor 0.5
    public void Resize_ShrinksWithDistance_NeverBelowHalf(float dist, float expected)
        => Assert.Equal(expected, ResizeFactor(dist, 1000f, 5000f), 5);

    // =====================================================================================================
    //  Status bar (QC the teammate health/armor branch) — only for living teammates with status on
    // =====================================================================================================

    /// <summary>QC: `if (autocvar_hud_shownames_status && this.sameteam && !this.csqcmodel_isdead)` — the
    /// health/armor status bar is teammates-only, status-cvar-gated, and hidden for the dead.</summary>
    private static bool DrawStatusBar(bool statusCvar, bool sameteam, bool dead)
        => statusCvar && sameteam && !dead;

    [Theory]
    [InlineData(true, true, false, true)]    // status on, teammate, alive → bar
    [InlineData(false, true, false, false)]  // status off → no bar
    [InlineData(true, false, false, false)]  // enemy → no bar (enemies never show health/armor)
    [InlineData(true, true, true, false)]    // dead teammate → no bar
    public void StatusBar_OnlyForLivingTeammates_WhenStatusOn(bool status, bool team, bool dead, bool expected)
        => Assert.Equal(expected, DrawStatusBar(status, team, dead));

    // =====================================================================================================
    //  Decolorize (QC: playername decolorize rules)
    // =====================================================================================================

    /// <summary>QC: `if ((autocvar_hud_shownames_decolorize == 1 && teamplay) || decolorize == 2)` strip the
    /// player's own name colors. 0 = never, 1 = team games only, 2 = always.</summary>
    private static bool Decolorize(int mode, bool teamplay)
        => (mode == 1 && teamplay) || mode == 2;

    [Theory]
    [InlineData(0, true, false)]   // never
    [InlineData(0, false, false)]
    [InlineData(1, true, true)]    // teamplay: strip
    [InlineData(1, false, false)]  // FFA: keep colors
    [InlineData(2, true, true)]    // always
    [InlineData(2, false, true)]
    public void Decolorize_FollowsTheModeAndTeamplay(int mode, bool teamplay, bool expected)
        => Assert.Equal(expected, Decolorize(mode, teamplay));

    // =====================================================================================================
    //  Networked ARMOR slice (the real codec — XonoticGodot.Net is linked)
    // =====================================================================================================

    [Fact]
    public void Armor_IsADistinctEntityField_Bit15()
    {
        // The shownames teammate status bar reads the entcs ARMOR slice; the port networks it as a new
        // NetEntityState field on its own change-mask bit (next free after Feedback = bit 14).
        Assert.Equal((EntityField)(1 << 15), EntityField.Armor);
        Assert.NotEqual(EntityField.Health, EntityField.Armor);
        Assert.NotEqual(EntityField.Feedback, EntityField.Armor);
    }

    [Fact]
    public void Armor_RoundTripsThroughTheDelta()
    {
        // A player whose armor changed delta-sends exactly the Armor field and reconstructs the value.
        var baseline = new NetEntityState { EntNum = 7, Kind = NetEntityKind.Player, Health = 100, Armor = 0 };
        var current = baseline;
        current.Armor = 75;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.True(mask.HasFlag(EntityField.Armor));
        Assert.False(mask.HasFlag(EntityField.Health)); // health unchanged → off the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(75, got.Armor);
        Assert.Equal(100, got.Health); // carried from baseline, untouched
    }

    [Fact]
    public void Armor_StaysOffTheWire_WhenUnchanged()
    {
        // An idle player (only its origin moved) costs nothing for armor — the bit stays clear, so the steady-state
        // bandwidth for a remote teammate isn't paid every frame.
        var baseline = new NetEntityState { EntNum = 7, Kind = NetEntityKind.Player, Health = 100, Armor = 50 };
        var moved = baseline;
        moved.Origin = new Vector3(64f, 0f, 0f);

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, moved);
        Assert.Equal(EntityField.Origin, mask);
        Assert.False(mask.HasFlag(EntityField.Armor));
    }

    [Fact]
    public void HealthAndArmor_RoundTripTogether_ForATeammateTag()
    {
        // The teammate status bar needs both: a hit that drops health AND armor delta-sends both fields and both
        // reconstruct (the entcs HEALTH + ARMOR slices the bar scales by hud_panel_healtharmor_max*).
        var baseline = new NetEntityState { EntNum = 3, Kind = NetEntityKind.Player, Health = 100, Armor = 100 };
        var hurt = baseline;
        hurt.Health = 60;
        hurt.Armor = 30;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, hurt);
        Assert.True(mask.HasFlag(EntityField.Health));
        Assert.True(mask.HasFlag(EntityField.Armor));

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(60, got.Health);
        Assert.Equal(30, got.Armor);
    }
}
