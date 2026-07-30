using System.Collections.Generic;
using XonoticGodot.Common.Menu;
using XonoticGodot.Formats.Sidecars;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the two src-level helpers behind the landing view-bob / idle-sway work:
///
/// <para><see cref="CheckBoxValue"/> — the QC value-pair checkbox math (Base <c>XonoticCheckBox</c>,
/// qcsrc/menu/xonotic/checkbox.qc:64-84). The Settings→Game→View checkboxes are
/// <c>makeXonoticCheckBoxEx(yesValue, noValue, cvar, label)</c> widgets (cl_bobfall 0.05/0,
/// v_idlescale 1/0, cl_eventchase_death 2/0, cl_bob 0.01/0) — the load rule is the yes/no MIDPOINT test
/// (<c>d = (v-m)/(yes-m) &gt; 0</c>), and saving writes the exact yes/no value with minimal decimals
/// (<c>ftos_mindecimals</c>). The old bit-flag wiring (<c>(v &amp; 0) != 0</c>) made every one of those
/// checkboxes inert — this pins the replacement semantics.</para>
///
/// <para><see cref="WeaponRigAnims"/> — Base's weapon hand-rig animation slot convention
/// (<c>CL_WeaponEntity_SetModel</c>, all.qc:373-376): h_ rig frame groups are addressed by FIXED INDEX
/// (0=fire, 1=fire2, 2=idle, 3=reload) and the shipped <c>h_*.iqm.framegroups</c> sidecars are NAMELESS
/// (their "// fire" trailers are comments). Without the stamped slot names the port's name-driven clip
/// player put the FIRE group under the player-canonical name "idle" (IQM) or found no idle at all (DPM) —
/// so the gun either looped its fire animation or sat frozen instead of playing the authored idle sway.</para>
/// </summary>
public class ViewBobIdleSwayTests
{
    // =====================================================================================
    //  CheckBoxValue — QC XonoticCheckBox load/save semantics
    // =====================================================================================

    [Theory]
    // cl_bobfall (yes 0.05 / no 0) — ON in stock Xonotic (xonotic-client.cfg:151).
    [InlineData(0.05f, 0.05f, 0f, true)]   // stock value reads checked
    [InlineData(0f, 0.05f, 0f, false)]     // disabled reads unchecked
    [InlineData(0.1f, 0.05f, 0f, true)]    // hand-tuned stronger dip still reads checked (midpoint rule)
    [InlineData(0.02f, 0.05f, 0f, false)]  // below the 0.025 midpoint reads unchecked
    // v_idlescale (yes 1 / no 0) — the "View waving while idle" pair.
    [InlineData(1f, 1f, 0f, true)]
    [InlineData(0f, 1f, 0f, false)]
    [InlineData(2f, 1f, 0f, true)]         // stronger sway still checked
    // cl_eventchase_death (yes 2 / no 0): value 1 sits exactly ON the midpoint -> d == 0 -> unchecked (QC d > 0).
    [InlineData(2f, 2f, 0f, true)]
    [InlineData(1f, 2f, 0f, false)]
    [InlineData(0f, 2f, 0f, false)]
    // Inverted pair (makeXonoticCheckBox(1, ...) builds yes=0 / no=1): 0 reads checked, 1 unchecked.
    [InlineData(0f, 0f, 1f, true)]
    [InlineData(1f, 0f, 1f, false)]
    // crosshair_hittest (yes 1.25 / no 1): 1 = hit tests without the shrink (unchecked), 0 = tests off
    // (still unchecked — the separate "Perform hit tests" box owns that), 1.25 = shrink on.
    [InlineData(1.25f, 1.25f, 1f, true)]
    [InlineData(1f, 1.25f, 1f, false)]
    [InlineData(0f, 1.25f, 1f, false)]
    // g_waypointsprite_crosshairfadealpha (yes 0.25 / no 1) — inverted VALUE pair: smaller = checked.
    [InlineData(0.25f, 0.25f, 1f, true)]
    [InlineData(1f, 0.25f, 1f, false)]
    // hud_shownames_crosshairdistance (yes 25 / no 0): the yes value is a DISTANCE; a hand-set larger
    // radius still reads checked.
    [InlineData(25f, 25f, 0f, true)]
    [InlineData(100f, 25f, 0f, true)]
    [InlineData(0f, 25f, 0f, false)]
    // con_notify (yes 4 / no 0): the hand-set modes 1/2 sit below the midpoint (2) -> unchecked.
    [InlineData(4f, 4f, 0f, true)]
    [InlineData(1f, 4f, 0f, false)]
    // notification CHOICE pairs (yes 2 / no 1): unchecked is the SIMPLE variant, not off.
    [InlineData(2f, 2f, 1f, true)]
    [InlineData(1f, 2f, 1f, false)]
    public void LoadChecked_UsesQcMidpointRule(float value, float yes, float no, bool expected)
    {
        Assert.Equal(expected, CheckBoxValue.LoadChecked(value, yes, no));
    }

    [Fact]
    public void LoadChecked_DegenerateEqualPair_ReadsUnchecked()
    {
        // yes == no is a meaningless widget (QC would divide by zero); we read it as unchecked, not NaN.
        Assert.False(CheckBoxValue.LoadChecked(1f, 1f, 1f));
    }

    [Theory]
    [InlineData(true, 0.05f, 0f, "0.05")]  // ftos_mindecimals: no trailing zeros
    [InlineData(false, 0.05f, 0f, "0")]
    [InlineData(true, 1f, 0f, "1")]
    [InlineData(true, 2f, 0f, "2")]
    [InlineData(true, 0.01f, 0f, "0.01")]  // the cl_bob pair
    public void SaveValue_WritesMinimalDecimals(bool isChecked, float yes, float no, string expected)
    {
        Assert.Equal(expected, CheckBoxValue.SaveValue(isChecked, yes, no));
    }

    // =====================================================================================
    //  WeaponRigAnims — h_ rig framegroup slot naming (all.qc:373-376)
    // =====================================================================================

    // The shipped h_shotgun.iqm.framegroups, verbatim: nameless ranges, names only in comments.
    private const string ShotgunSidecar =
        "1 8 20 0 // fire\n" +
        "9 23 20 0 // fire2\n" +
        "32 200 20 1 // idle\n" +
        "232 40 20 0 // reload\n";

    [Fact]
    public void HandRig_NamelessGroups_GetSlotNames()
    {
        List<FrameGroup>? groups = WeaponRigAnims.NameGroups(
            "models/weapons/h_shotgun.iqm", FrameGroups.Parse(ShotgunSidecar));

        Assert.NotNull(groups);
        Assert.Equal(4, groups!.Count);
        Assert.Equal(new[] { "fire", "fire2", "idle", "reload" },
            new[] { groups[0].Name, groups[1].Name, groups[2].Name, groups[3].Name });

        // Ranges/fps/loop untouched: the idle is the long looping sway, the fire is a one-shot.
        Assert.Equal(32, groups[2].FirstFrame);
        Assert.Equal(200, groups[2].FrameCount);
        Assert.True(groups[2].Loop);
        Assert.Equal(1, groups[0].FirstFrame);
        Assert.False(groups[0].Loop);
    }

    [Fact]
    public void HandRig_AuthoredNamesAndExtraGroups_ArePreserved()
    {
        var groups = new List<FrameGroup>
        {
            new(0, 8, 20f, false, "custom"),   // authored 5th-token name wins over the slot name
            new(8, 23, 20f, false),
            new(31, 200, 20f, true),
            new(231, 40, 20f, false),
            new(271, 10, 20f, true),           // a 5th group has no slot name — stays nameless
        };
        List<FrameGroup>? named = WeaponRigAnims.NameGroups("models/weapons/h_custom.iqm", groups);

        Assert.Equal("custom", named![0].Name);
        Assert.Equal("fire2", named[1].Name);
        Assert.Equal("idle", named[2].Name);
        Assert.Equal("reload", named[3].Name);
        Assert.Equal(string.Empty, named[4].Name);
    }

    [Fact]
    public void NonHandRigPaths_AreUntouched()
    {
        // Player/monster models keep their nameless groups (they map clips by ordinal, not by these names).
        foreach (string path in new[]
        {
            "models/player/erebus.iqm",             // player model
            "models/monsters/zombie.dpm",           // monster model
            "models/weapons/v_shotgun.md3",         // v_ visual model, not the h_ rig
            "models/weapons/g_shotgun.md3",         // world pickup model
        })
        {
            List<FrameGroup>? groups = WeaponRigAnims.NameGroups(path, FrameGroups.Parse(ShotgunSidecar));
            Assert.All(groups!, g => Assert.Equal(string.Empty, g.Name));
        }
    }

    [Fact]
    public void NullOrEmptyGroups_PassThrough()
    {
        Assert.Null(WeaponRigAnims.NameGroups("models/weapons/h_shotgun.iqm", null));
        var empty = new List<FrameGroup>();
        Assert.Same(empty, WeaponRigAnims.NameGroups("models/weapons/h_shotgun.iqm", empty));
    }
}
