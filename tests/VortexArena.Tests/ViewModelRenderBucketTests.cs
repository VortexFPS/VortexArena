using System;
using System.IO;
using System.Numerics;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the FIRST-PERSON viewmodel render-bucket decision — Base <c>CL_WeaponEntity_SetModel</c>
/// (qcsrc/common/weapons/all.qc:381-400): a weapon's <c>h_*</c> HAND RIG is classified by whether it exposes a
/// <c>weapon</c>/<c>tag_weapon</c> attach bone.
/// <list type="bullet">
///   <item><b>INVISIBLE-HAND</b> (IQM rigs WITH a <c>weapon</c> bone, e.g. h_arc): Base creates a
///   <c>weaponchild</c> and renders the <c>v_</c> VISUAL model attached to that bone. The port renders the v_
///   model — <see cref="MuzzleTag.IsInvisibleHandRig"/> is <c>true</c>.</item>
///   <item><b>FULL-MODEL</b> (DPM rigs with NO weapon bone, e.g. h_rl/h_crylink): Base leaves the
///   <c>weaponchild</c> NULL and renders the <b>h_ RIG ITSELF</b>. The port renders the h_ rig —
///   <see cref="MuzzleTag.IsInvisibleHandRig"/> is <c>false</c>.</item>
/// </list>
/// This is the exact predicate <c>AssetLoader.WeaponRigIsInvisibleHand</c> / <c>GameDemo.BuildViewModelEquip</c>
/// branch on (the live + demo equip paths), so testing it Godot-free pins the model-selection logic the render
/// fix depends on. It also asserts the chosen rig still resolves a forward <c>tag_shot</c> muzzle, so the flash +
/// the (unchanged, faithful) shot origin coincide.
/// </summary>
public class ViewModelRenderBucketTests
{
    // ------------------------------------------------------------------------------------------------
    //  Synthetic rigs (Godot-free, deterministic) — the two buckets.
    // ------------------------------------------------------------------------------------------------

    /// <summary>An INVISIBLE-HAND rig like h_arc.iqm: bare socket skeleton with origin -> weapon -> {shot}.</summary>
    private static IqmData InvisibleHandRig() => new()
    {
        Version = 2,
        Joints = new[]
        {
            new IqmJoint("origin", -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
            new IqmJoint("weapon", 0, new Vector3(5, 0, -15), Quaternion.Identity, Vector3.One),
            new IqmJoint("shot", 1, new Vector3(30, 0, 5), Quaternion.Identity, Vector3.One),
        },
    };

    /// <summary>A FULL-MODEL rig like h_rl.iqm (DPM): a gun-mesh skeleton with tag_handle + tag_shot, NO
    /// <c>weapon</c>/<c>tag_weapon</c> bone — so Base renders the rig itself.</summary>
    private static DpmData FullModelRig()
    {
        DpmBonePose Pose(Vector3 origin) => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, origin);
        return new DpmData
        {
            Bones = new[]
            {
                new DpmBone("tag_handle", -1, 0),
                new DpmBone("BodyLP", 0, 0),
                new DpmBone("tag_shot", 0, DpmBone.AttachmentFlag),
            },
            Frames = new[]
            {
                new DpmFrame
                {
                    Name = "base",
                    BonePoses = new[] { Pose(Vector3.Zero), Pose(new Vector3(1, 0, 0)), Pose(new Vector3(40.9f, -9f, -17f)) },
                },
            },
        };
    }

    [Fact]
    public void InvisibleHandRig_Is_Classified_As_RenderVModel()
    {
        MuzzleTag.Model rig = MuzzleTag.Model.Of(InvisibleHandRig());
        // Has a `weapon` bone -> invisible-hand -> the port renders the v_ visual model attached to it.
        Assert.True(MuzzleTag.IsInvisibleHandRig(rig));
        Assert.True(rig.HasAttachBone());
    }

    [Fact]
    public void FullModelRig_Is_Classified_As_RenderHRig()
    {
        MuzzleTag.Model rig = MuzzleTag.Model.Of(FullModelRig());
        // No `weapon`/`tag_weapon` bone -> full-model -> the port renders the h_ RIG ITSELF (ignores v_).
        Assert.False(MuzzleTag.IsInvisibleHandRig(rig));
        Assert.False(rig.HasAttachBone());
    }

    [Fact]
    public void BothBuckets_Still_Expose_A_Forward_TagShot_Muzzle()
    {
        // The muzzle the viewmodel pins the flash to is the chosen rig's own tag_shot. Both buckets must resolve
        // it so the flash sits at the barrel (and, for the full-model rig, coincides with the unchanged shot origin).
        Vector3? hand = MuzzleTag.Extract(InvisibleHandRig());
        Assert.NotNull(hand);
        Assert.True(hand!.Value.X > 5f, "invisible-hand rig shot tag should sit forward of the eye");

        Vector3? full = MuzzleTag.Extract(FullModelRig());
        Assert.NotNull(full);
        Assert.True(full!.Value.X > 5f, "full-model rig shot tag should sit forward of the eye");
    }

    [Fact]
    public void EmptyRig_Degrades_To_RenderVModel()
    {
        // A missing/unparsable rig (the None model) reports NOT invisible-hand from the predicate, but the live
        // AssetLoader path returns null for a missing file and the equip helper then keeps the legacy v_ path.
        Assert.False(MuzzleTag.IsInvisibleHandRig(MuzzleTag.Model.None));
        Assert.False(MuzzleTag.Model.None.HasAttachBone());
    }

    // ------------------------------------------------------------------------------------------------
    //  Real-asset regression (no-op when the asset checkout is absent — mirrors MuzzleOffsetTests).
    //  h_rl = Devastator (DPM, full-model, the reported weapon); h_arc = Arc (IQM, invisible-hand).
    // ------------------------------------------------------------------------------------------------

    private const string Weapons =
        @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir\models\weapons";

    [Fact]
    public void Real_HRl_Devastator_Is_FullModel_RenderHRig()
    {
        string path = Path.Combine(Weapons, "h_rl.iqm");
        if (!File.Exists(path)) return; // no checkout -> no-op

        MuzzleTag.Model rig = LoadRig(path);
        // The reported weapon: a DPM full-model rig with NO weapon bone -> render the h_ RIG ITSELF.
        Assert.False(MuzzleTag.IsInvisibleHandRig(rig),
            "h_rl.iqm is a full-model DPM rig (no weapon bone) — the port must render the h_ rig itself");

        // Its own tag_shot is the muzzle = the faithful Devastator movedir (40.936, -9, -16.958), forward-dominant.
        Vector3? muzzle = ExtractRig(path);
        Assert.NotNull(muzzle);
        Assert.True(muzzle!.Value.X > MathF.Abs(muzzle.Value.Y) && muzzle.Value.X > MathF.Abs(muzzle.Value.Z),
            $"h_rl tag_shot should be forward-dominant, got {muzzle}");
        Assert.Equal(40.936f, muzzle.Value.X, 2);
    }

    [Fact]
    public void Real_HArc_Is_InvisibleHand_RenderVModel()
    {
        string path = Path.Combine(Weapons, "h_arc.iqm");
        if (!File.Exists(path)) return; // no checkout -> no-op

        MuzzleTag.Model rig = LoadRig(path);
        // An IQM invisible-hand rig WITH a weapon bone -> render the v_ visual model attached to "weapon".
        Assert.True(MuzzleTag.IsInvisibleHandRig(rig),
            "h_arc.iqm is an invisible-hand IQM rig (has a weapon bone) — the port keeps the v_-attach path");

        // The rig still carries a forward shot tag (used for the shot origin + the muzzle marker).
        Vector3? muzzle = ExtractRig(path);
        Assert.NotNull(muzzle);
        Assert.True(muzzle!.Value.X > 5f, $"h_arc tag_shot should be forward, got {muzzle}");
    }

    [Theory]
    // The 11 full-model DPM rigs (no weapon bone -> render the h_ rig itself).
    [InlineData("h_rl.iqm", false)]
    [InlineData("h_crylink.iqm", false)]
    [InlineData("h_electro.iqm", false)]
    [InlineData("h_gl.iqm", false)]
    [InlineData("h_hagar.iqm", false)]
    // The invisible-hand IQM rigs (have a weapon bone -> render the v_ model).
    [InlineData("h_arc.iqm", true)]
    [InlineData("h_nex.iqm", true)]
    [InlineData("h_shotgun.iqm", true)]
    [InlineData("h_uzi.iqm", true)]
    [InlineData("h_laser.iqm", true)]
    public void Real_Rig_Bucket_Matches_Expected(string file, bool expectInvisibleHand)
    {
        string path = Path.Combine(Weapons, file);
        if (!File.Exists(path)) return; // no checkout -> no-op

        bool got = MuzzleTag.IsInvisibleHandRig(LoadRig(path));
        Assert.True(got == expectInvisibleHand,
            $"{file}: expected invisible-hand={expectInvisibleHand} (render {(expectInvisibleHand ? "v_ model" : "h_ rig")}), got {got}");
    }

    // ---- helpers (magic-dispatch a model file; extensions lie, dispatch by on-disk magic) ----------

    private static MuzzleTag.Model LoadRig(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string magic = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        if (magic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal)) return MuzzleTag.Model.Of(IqmReader.Read(bytes));
        if (magic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal)) return MuzzleTag.Model.Of(DpmReader.Read(bytes));
        if (magic.StartsWith("IDP3", StringComparison.Ordinal)) return MuzzleTag.Model.Of(Md3Reader.Read(bytes));
        return MuzzleTag.Model.None;
    }

    private static Vector3? ExtractRig(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string magic = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        if (magic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(IqmReader.Read(bytes));
        if (magic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(DpmReader.Read(bytes));
        if (magic.StartsWith("IDP3", StringComparison.Ordinal)) return MuzzleTag.Extract(Md3Reader.Read(bytes));
        return null;
    }
}
