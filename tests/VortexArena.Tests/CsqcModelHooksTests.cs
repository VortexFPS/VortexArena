using XonoticGodot.Engine.Simulation;
using Xunit;
using EF = XonoticGodot.Common.Framework.EffectFlags;

namespace XonoticGodot.Tests;

/// <summary>
/// Parity tests for the ported CSQCModel PreDraw hooks (qcsrc/client/csqcmodel_hooks.qc): the EF_*/MF_*
/// constants, the fallback-frame anim remap, the LOD distance selection, the force-color predicate, the
/// unique-color combo, the palette and the death-fade glow factor. Pure logic only — these live in
/// XonoticGodot.Engine.Simulation precisely so the test project (which can't see game/) can reach them.
/// </summary>
public class CsqcModelHooksTests
{
    // =========================================================================================
    //  (1) CsqcModelEffectFlags — engine EF_* + CSQC MF_* values, and the MF→trail mapping
    // =========================================================================================

    [Fact]
    public void EffectFlags_Match_Engine_DpDefs_Values()
    {
        // darkplaces/dpdefs/progsdefs.qc + dpextensions.qc
        Assert.Equal(1, CsqcModelEffectFlags.EF_BRIGHTFIELD);
        Assert.Equal(2, CsqcModelEffectFlags.EF_MUZZLEFLASH);
        Assert.Equal(4, CsqcModelEffectFlags.EF_BRIGHTLIGHT);
        Assert.Equal(8, CsqcModelEffectFlags.EF_DIMLIGHT);
        Assert.Equal(16, CsqcModelEffectFlags.EF_NODRAW);
        Assert.Equal(32, CsqcModelEffectFlags.EF_ADDITIVE);
        Assert.Equal(64, CsqcModelEffectFlags.EF_BLUE);
        Assert.Equal(128, CsqcModelEffectFlags.EF_RED);
        Assert.Equal(512, CsqcModelEffectFlags.EF_FULLBRIGHT);
        Assert.Equal(1024, CsqcModelEffectFlags.EF_FLAME);
        Assert.Equal(2048, CsqcModelEffectFlags.EF_STARDUST);
        Assert.Equal(4096, CsqcModelEffectFlags.EF_NOSHADOW);
        Assert.Equal(8192, CsqcModelEffectFlags.EF_NODEPTHTEST);
        Assert.Equal(16384, CsqcModelEffectFlags.EF_SELECTABLE);
        Assert.Equal(32768, CsqcModelEffectFlags.EF_DOUBLESIDED);
        // CSQCMODEL_EF_RESPAWNGHOST == EF_SELECTABLE (common/csqcmodel_settings.qh:110)
        Assert.Equal(CsqcModelEffectFlags.EF_SELECTABLE, CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST);
    }

    [Fact]
    public void EffectFlags_Agree_With_Server_Side_EffectFlags_For_Shared_Bits()
    {
        // The client-side set must be numerically identical to the server EffectFlags for EVERY shared bit,
        // because the server packs them into Entity.Effects which is networked and read here. (The server's
        // FullBright/NoShadow were previously mislabeled as 8/8192 = EF_DIMLIGHT/EF_NODEPTHTEST; corrected to the
        // engine 512/4096 so instagib/buffs fullbright+noshadow actually render — they now agree.)
        Assert.Equal(EF.Additive, CsqcModelEffectFlags.EF_ADDITIVE);
        Assert.Equal(EF.NoDraw, CsqcModelEffectFlags.EF_NODRAW);
        Assert.Equal(EF.Stardust, CsqcModelEffectFlags.EF_STARDUST);
        Assert.Equal(EF.FullBright, CsqcModelEffectFlags.EF_FULLBRIGHT);
        Assert.Equal(EF.NoShadow, CsqcModelEffectFlags.EF_NOSHADOW);
    }

    [Fact]
    public void ModelFlags_Match_BIT_0_Through_7()
    {
        Assert.Equal(1, CsqcModelEffectFlags.MF_ROCKET);
        Assert.Equal(2, CsqcModelEffectFlags.MF_GRENADE);
        Assert.Equal(4, CsqcModelEffectFlags.MF_GIB);
        Assert.Equal(8, CsqcModelEffectFlags.MF_ROTATE);
        Assert.Equal(16, CsqcModelEffectFlags.MF_TRACER);
        Assert.Equal(32, CsqcModelEffectFlags.MF_ZOMGIB);
        Assert.Equal(64, CsqcModelEffectFlags.MF_TRACER2);
        Assert.Equal(128, CsqcModelEffectFlags.MF_TRACER3);
    }

    [Theory]
    [InlineData(1, "TR_ROCKET")]     // MF_ROCKET
    [InlineData(2, "TR_GRENADE")]    // MF_GRENADE
    [InlineData(4, "TR_BLOOD")]      // MF_GIB
    [InlineData(16, "TR_WIZSPIKE")]  // MF_TRACER
    [InlineData(32, "TR_SLIGHTBLOOD")] // MF_ZOMGIB
    [InlineData(64, "TR_KNIGHTSPIKE")] // MF_TRACER2
    [InlineData(128, "TR_VORESPIKE")]  // MF_TRACER3
    public void ModelFlagToTrail_Maps_Each_Trail_Flag(int mf, string trail)
        => Assert.Equal(trail, CsqcModelEffectFlags.ModelFlagToTrail(mf));

    [Fact]
    public void ModelFlagToTrail_Null_When_No_Trail_Flag()
    {
        Assert.Null(CsqcModelEffectFlags.ModelFlagToTrail(0));
        Assert.Null(CsqcModelEffectFlags.ModelFlagToTrail(CsqcModelEffectFlags.MF_ROTATE)); // rotate has no trail
    }

    [Fact]
    public void ModelFlagToTrail_Last_Matching_Flag_Wins()
    {
        // QC assigns tref in a fixed order; the later branch overwrites. MF_ROCKET|MF_TRACER3 → TR_VORESPIKE.
        int both = CsqcModelEffectFlags.MF_ROCKET | CsqcModelEffectFlags.MF_TRACER3;
        Assert.Equal("TR_VORESPIKE", CsqcModelEffectFlags.ModelFlagToTrail(both));
    }

    // =========================================================================================
    //  (2) CsqcFallbackFrame — the anim remap table
    // =========================================================================================

    // A model that HAS frame f (and a non-static frame 1). Only the listed missing frames are absent.
    private static System.Func<int, float> ModelMissing(params int[] missing)
        => f =>
        {
            foreach (int m in missing) if (m == f) return 0f;
            return 1f; // every other frame (incl. frame 1) exists
        };

    [Fact]
    public void FallbackFrame_Returns_Frame_When_It_Exists()
    {
        // frame 23 present → unchanged
        Assert.Equal(23, CsqcFallbackFrame.Remap(23, ModelMissing(/* nothing missing */)));
    }

    [Fact]
    public void FallbackFrame_Melee_Remaps_To_Shoot_When_Missing()
    {
        // 23 (anim_melee) missing, frame 1 present → 11 (anim_shoot)
        Assert.Equal(11, CsqcFallbackFrame.Remap(23, ModelMissing(23)));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    public void FallbackFrame_Duckwalk_Variants_Remap_To_Duckwalk(int frame)
        => Assert.Equal(4, CsqcFallbackFrame.Remap(frame, ModelMissing(frame)));

    [Fact]
    public void FallbackFrame_Static_Model_Cannot_Be_Fixed()
    {
        // frameduration(1) <= 0 → static model: return the (missing) frame unchanged.
        System.Func<int, float> staticModel = _ => 0f;
        Assert.Equal(23, CsqcFallbackFrame.Remap(23, staticModel));
        Assert.Equal(24, CsqcFallbackFrame.Remap(24, staticModel));
    }

    [Fact]
    public void FallbackFrame_Unmapped_Missing_Frame_Returns_Input()
    {
        // 99 missing, frame 1 present, no table entry → returns 99 (the FAIL branch).
        Assert.Equal(99, CsqcFallbackFrame.Remap(99, ModelMissing(99)));
    }

    // =========================================================================================
    //  (3) CsqcModelLod — distance selection + name derivation
    // =========================================================================================

    [Theory]
    [InlineData(0, 0)]   // detailReduction 0 → lod0
    [InlineData(-1, 1)]  // <=-1 → lod1
    [InlineData(-2, 2)]  // <=-2 → lod2
    [InlineData(-5, 2)]  // still lod2
    public void Lod_NonPositive_DetailReduction_Is_Stepped(int detailReduction, int expected)
        => Assert.Equal(expected, CsqcModelLod.SelectLodIndex(detailReduction, distance: 9999f, viewZoom: 1f, viewQuality: 1f, dist1: 1024f, dist2: 3072f));

    [Fact]
    public void Lod_Distance_Driven_Near_Mid_Far()
    {
        // shipped cl_playerdetailreduction=4, dist1=1024, dist2=3072, viewZoom=1, viewQuality=1.
        // f = (distance*1 + 100) * 4.
        // near: distance=0 → f=400 (<=1024) → lod0
        Assert.Equal(0, CsqcModelLod.SelectLodIndex(4, 0f, 1f, 1f, 1024f, 3072f));
        // mid: f between 1024 and 3072. distance=300 → f=(400)*4=1600 → lod1
        Assert.Equal(1, CsqcModelLod.SelectLodIndex(4, 300f, 1f, 1f, 1024f, 3072f));
        // far: distance=800 → f=(900)*4=3600 (>3072) → lod2
        Assert.Equal(2, CsqcModelLod.SelectLodIndex(4, 800f, 1f, 1f, 1024f, 3072f));
    }

    [Fact]
    public void Lod_ViewQuality_Scales_f_Up()
    {
        // distance=200, dr=4 → base f=(300)*4=1200 (lod1). With view_quality=0.5, f/=0.5 → 2400 (still lod1).
        Assert.Equal(1, CsqcModelLod.SelectLodIndex(4, 200f, 1f, 1f, 1024f, 3072f));
        // view_quality below the 0.01 bound is clamped; with q=0.3 f=4000 → lod2.
        Assert.Equal(2, CsqcModelLod.SelectLodIndex(4, 200f, 1f, 0.3f, 1024f, 3072f));
    }

    [Fact]
    public void Lod_ViewZoom_Increases_Effective_Distance()
    {
        // distance=200, zoom=4, dr=4 → f=(200*4+100)*4 = 3600 (>3072) → lod2 (zoomed-in models stay high detail).
        Assert.Equal(2, CsqcModelLod.SelectLodIndex(4, 200f, 4f, 1f, 1024f, 3072f));
    }

    [Theory]
    [InlineData("models/player/erebus.iqm", 1, "models/player/erebus_lod1.iqm")]
    [InlineData("models/player/erebus.iqm", 2, "models/player/erebus_lod2.iqm")]
    [InlineData("models/weapons/v_rl.md3", 1, "models/weapons/v_rl_lod1.md3")]
    public void Lod_ModelName_Inserts_LodN_Before_Extension(string model, int lod, string expected)
        => Assert.Equal(expected, CsqcModelLod.LodModelName(model, lod));

    [Fact]
    public void Lod_ModelName_Lod0_Is_Unchanged()
        => Assert.Equal("models/player/erebus.iqm", CsqcModelLod.LodModelName("models/player/erebus.iqm", 0));

    [Fact]
    public void Lod_NearestPointOnBox_Clamps_Into_Box()
    {
        var min = new System.Numerics.Vector3(-16, -16, -24);
        var max = new System.Numerics.Vector3(16, 16, 40);
        // outside on +x, inside on y, below on z → clamps each component
        var p = CsqcModelLod.NearestPointOnBox(min, max, new System.Numerics.Vector3(100, 5, -100));
        Assert.Equal(16f, p.X);
        Assert.Equal(5f, p.Y);
        Assert.Equal(-24f, p.Z);
    }

    // =========================================================================================
    //  (4) CsqcModelAppearance — force-colors predicate, unique color, palette, death glow
    // =========================================================================================

    [Theory]
    // FFA (not 1v1, not teamplay): fpc 1 or 2 enabled, others disabled
    [InlineData(1, false, false, 0, 3, true)]
    [InlineData(2, false, false, 0, 3, true)]
    [InlineData(0, false, false, 0, 3, false)]
    [InlineData(3, false, false, 0, 3, false)]
    public void ForceColors_FFA(int fpc, bool is1v1, bool teamplay, int teamCount, int myTeam, bool expected)
        => Assert.Equal(expected, CsqcModelAppearance.ForcePlayerColorsEnabled(fpc, is1v1, teamplay, teamCount, myTeam));

    [Theory]
    // 1v1/Duel: fpc∈{1,2,3,5} enabled, 4 disabled — but disabled entirely when spectating.
    [InlineData(1, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(5, 3, true)]
    [InlineData(4, 3, false)]
    [InlineData(1, CsqcModelAppearance.NumSpectator, false)] // spectator → never forced
    public void ForceColors_1v1(int fpc, int myTeam, bool expected)
        => Assert.Equal(expected, CsqcModelAppearance.ForcePlayerColorsEnabled(fpc, is1v1: true, isTeamplay: false, teamCount: 2, myTeam: myTeam));

    [Theory]
    // 2-team teamplay: fpc∈{2,4,5} enabled, {1,3} disabled (and spectator/teamCount!=2 disabled).
    [InlineData(2, 2, 3, true)]
    [InlineData(4, 2, 3, true)]
    [InlineData(5, 2, 3, true)]
    [InlineData(1, 2, 3, false)]
    [InlineData(3, 2, 3, false)]
    [InlineData(2, 3, 3, false)]                                   // teamCount 3 → disabled
    [InlineData(2, 2, CsqcModelAppearance.NumSpectator, false)]    // spectator → disabled
    public void ForceColors_Teamplay(int fpc, int teamCount, int myTeam, bool expected)
        => Assert.Equal(expected, CsqcModelAppearance.ForcePlayerColorsEnabled(fpc, is1v1: false, isTeamplay: true, teamCount: teamCount, myTeam: myTeam));

    [Fact]
    public void UniqueColormap_Matches_QC_Table()
    {
        // pl01: num=0 → c1=0, q=0, c2=1 → 1024 + (0<<4) + 1
        Assert.Equal(1024 + (0 << 4) + 1, CsqcModelAppearance.UniqueColormap(0));
        // pl14: num=13 → c1=13, c2=14
        Assert.Equal(1024 + (13 << 4) + 14, CsqcModelAppearance.UniqueColormap(13));
        // pl15: num=14 → c1=14, q=0, c2=(14+1)%15=0
        Assert.Equal(1024 + (14 << 4) + 0, CsqcModelAppearance.UniqueColormap(14));
        // pl16: num=15 → c1=0, q=1, c2=(0+1+1)%15=2
        Assert.Equal(1024 + (0 << 4) + 2, CsqcModelAppearance.UniqueColormap(15));
        // pl30: num=29 → c1=14, q=1, c2=(14+1+1)%15=1
        Assert.Equal(1024 + (14 << 4) + 1, CsqcModelAppearance.UniqueColormap(29));
    }

    [Fact]
    public void PaletteColor_Matches_lib_color_qh()
    {
        // a few static entries (lib/color.qh)
        Assert.Equal((1f, 0f, 0f), CsqcModelAppearance.ColormapPaletteColor(4, true, 0f));     // red
        Assert.Equal((0f, 0.333333f, 1f), CsqcModelAppearance.ColormapPaletteColor(13, true, 0f)); // blue
        Assert.Equal((1f, 1f, 0f), CsqcModelAppearance.ColormapPaletteColor(12, true, 0f));    // yellow
        Assert.Equal((1f, 0f, 0.501961f), CsqcModelAppearance.ColormapPaletteColor(10, true, 0f)); // pink
        Assert.Equal((1f, 1f, 1f), CsqcModelAppearance.ColormapPaletteColor(0, true, 0f));     // white
        // out-of-range → black
        Assert.Equal((0f, 0f, 0f), CsqcModelAppearance.ColormapPaletteColor(99, true, 0f));
    }

    [Fact]
    public void DeathGlow_Full_At_Death_Instant()
    {
        // deathglow=2, min=0.5, no color, dead, now==deathTime → glow_fade=1 → factor = 0.5 + 1*(0.5) = 1.0
        float f = CsqcModelAppearance.DeathGlowFactor(deathglow: 2f, deathglowMin: 0.5f, hasColor: false, isDead: true, now: 10f, deathTime: 10f);
        Assert.Equal(1.0f, f, 5);
    }

    [Fact]
    public void DeathGlow_Decays_To_MinFactor()
    {
        // now = deathTime + deathglow → glow_fade=0 → factor = min_factor = 0.5
        float f = CsqcModelAppearance.DeathGlowFactor(2f, 0.5f, hasColor: false, isDead: true, now: 12f, deathTime: 10f);
        Assert.Equal(0.5f, f, 5);
    }

    [Fact]
    public void DeathGlow_MinFactor_Halved_When_HasColor()
    {
        // hasColor → min_factor /= 2 → 0.25 floor far past death.
        float f = CsqcModelAppearance.DeathGlowFactor(2f, 0.5f, hasColor: true, isDead: true, now: 100f, deathTime: 10f);
        Assert.Equal(0.25f, f, 5);
    }

    [Fact]
    public void DeathGlow_No_Fade_When_Disabled_Or_Alive()
    {
        Assert.Equal(1f, CsqcModelAppearance.DeathGlowFactor(0f, 0.5f, hasColor: false, isDead: true, now: 10f, deathTime: 10f));  // disabled
        Assert.Equal(1f, CsqcModelAppearance.DeathGlowFactor(2f, 0.5f, hasColor: false, isDead: false, now: 10f, deathTime: 10f)); // alive
    }

    [Fact]
    public void DeathGlow_Clamps_GlowFade_Into_Unit_Range()
    {
        // now far past deathTime + deathglow → 1 - big = negative → bound to 0 → factor = min_factor, not negative.
        float f = CsqcModelAppearance.DeathGlowFactor(2f, 0.5f, hasColor: false, isDead: true, now: 1000f, deathTime: 10f);
        Assert.Equal(0.5f, f, 5);
        Assert.True(f >= 0f);
    }
}
