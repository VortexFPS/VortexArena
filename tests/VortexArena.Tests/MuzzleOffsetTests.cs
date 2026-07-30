using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Verifies the per-weapon weapon-shot-origin (QC w_shotorg muzzle = <c>ent.(weaponentity).movedir</c>, the
/// weapon model's <c>tag_shot</c> set in <c>CL_WeaponEntity_SetModel</c>): the registry +
/// <see cref="WeaponFiring.SetupShot"/> consumption (a registered per-weapon offset is applied via the view
/// basis; an un-registered weapon falls back to the generic <see cref="WeaponFiring.DefaultMuzzleOffset"/>),
/// and the Godot-free <see cref="MuzzleTag"/> tag-extraction for MD3/IQM/DPM in model-local Quake coords.
///
/// Installs the ambient <see cref="Api.Services"/> (a free-space trace so the muzzle tracebox legs land exactly
/// at the offset), so it runs in the serialized GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public class MuzzleOffsetTests
{
    // A player actor at the origin facing +X (Angles 0). Eye = origin + view_ofs. With Angles 0 the Quake basis
    // is forward=(1,0,0), right=(0,-1,0), up=(0,0,1), so SetupShot's `right*(-md.y) + up*md.z` then forward-by-md.x
    // places w_shotorg at eye + (md.x, md.y, md.z) — i.e. the model-local offset read straight off in world axes.
    private static Entity MakePlayer(int weaponId)
        => new()
        {
            Flags = EntFlags.Client,
            Origin = new Vector3(0, 0, 0),
            Angles = Vector3.Zero,
            ViewOfs = new Vector3(0, 0, 20),
            ActiveWeaponId = weaponId,
        };

    [Fact]
    public void SetupShot_Uses_Registered_PerWeapon_Offset()
    {
        using var _ = new MuzzleEnv();
        WeaponFiring.ClearMuzzleOffsets();
        const int weaponId = 7;
        // A distinctive non-default tag_shot (model-local Quake: forward, +left, up).
        var movedir = new Vector3(25f, 6f, -3f);
        WeaponFiring.RegisterMuzzleOffset(weaponId, movedir);

        Entity actor = MakePlayer(weaponId);
        Vector3 eye = actor.Origin + actor.ViewOfs;

        ShotInfo shot = WeaponFiring.SetupShot(actor, new Vector3(1, 0, 0));

        // With Angles 0 the offset maps to world axes directly: +X forward, +Y left, +Z up.
        Assert.Equal(eye.X + movedir.X, shot.Origin.X, 3);
        Assert.Equal(eye.Y + movedir.Y, shot.Origin.Y, 3);
        Assert.Equal(eye.Z + movedir.Z, shot.Origin.Z, 3);
    }

    [Fact]
    public void SetupShot_Falls_Back_To_Generic_When_Unregistered()
    {
        using var _ = new MuzzleEnv();
        WeaponFiring.ClearMuzzleOffsets();
        const int weaponId = 3; // nothing registered for it
        Vector3 md = WeaponFiring.DefaultMuzzleOffset;

        Entity actor = MakePlayer(weaponId);
        Vector3 eye = actor.Origin + actor.ViewOfs;

        ShotInfo shot = WeaponFiring.SetupShot(actor, new Vector3(1, 0, 0));

        Assert.Equal(eye.X + md.X, shot.Origin.X, 3);
        Assert.Equal(eye.Y + md.Y, shot.Origin.Y, 3);
        Assert.Equal(eye.Z + md.Z, shot.Origin.Z, 3);
    }

    [Fact]
    public void MuzzleOffsetFor_Picks_Active_Weapon()
    {
        WeaponFiring.ClearMuzzleOffsets();
        WeaponFiring.RegisterMuzzleOffset(1, new Vector3(10f, 0f, 0f));
        WeaponFiring.RegisterMuzzleOffset(2, new Vector3(20f, 0f, 0f));

        Assert.Equal(new Vector3(20f, 0f, 0f), WeaponFiring.MuzzleOffsetFor(MakePlayer(2)));
        Assert.Equal(new Vector3(10f, 0f, 0f), WeaponFiring.MuzzleOffsetFor(MakePlayer(1)));
        // Unregistered id -> generic.
        Assert.Equal(WeaponFiring.DefaultMuzzleOffset, WeaponFiring.MuzzleOffsetFor(MakePlayer(99)));
    }

    // ---- MuzzleTag extraction (Godot-free, model-local Quake coords) --------------------------------

    [Fact]
    public void MuzzleTag_Md3_Reads_Raw_Tag_Origin()
    {
        var shot = new Vector3(23.5f, 0.5f, -1.25f); // a plausible forward muzzle
        var tag = new Md3Tag
        {
            Name = "tag_shot",
            Transforms = new[] { new Md3TagTransform(shot, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ) },
        };
        var md3 = new Md3Data
        {
            FrameCount = 1,
            Tags = new[] { tag },
            TagsByName = new Dictionary<string, Md3Tag>(StringComparer.Ordinal) { ["tag_shot"] = tag },
        };

        Vector3? got = MuzzleTag.Extract(md3);
        Assert.NotNull(got);
        Assert.Equal(shot, got!.Value);
        Assert.True(got.Value.X > 1f, "a real shot tag sits well forward of the eye (non-default forward offset)");
    }

    [Fact]
    public void MuzzleTag_Md3_Null_When_No_Shot_Tag()
    {
        var md3 = new Md3Data { FrameCount = 1 }; // no tags at all
        Assert.Null(MuzzleTag.Extract(md3));
    }

    [Fact]
    public void MuzzleTag_Iqm_Composes_Bind_Pose_Up_The_Parent_Chain()
    {
        // Two-joint chain: root translated +X 10, child "tag_shot" translated +X 15 (local). The model-local
        // bind position of the child is the composed translation = (25, 0, 0) (no rotation/scale).
        var iqm = new IqmData
        {
            Version = 2,
            Joints = new[]
            {
                new IqmJoint("root", -1, new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
                new IqmJoint("tag_shot", 0, new Vector3(15, 0, 0), Quaternion.Identity, Vector3.One),
            },
        };

        Vector3? got = MuzzleTag.Extract(iqm);
        Assert.NotNull(got);
        Assert.Equal(25f, got!.Value.X, 3);
        Assert.Equal(0f, got.Value.Y, 3);
        Assert.Equal(0f, got.Value.Z, 3);
    }

    [Fact]
    public void MuzzleTag_Iqm_Honors_Parent_Rotation()
    {
        // root yawed +90deg about Z, child offset +X 10 in the root's LOCAL frame -> in model-local space the
        // child sits along +Y (the rotated forward). Verifies the full TRS chain (not just translation sums).
        Quaternion yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var iqm = new IqmData
        {
            Version = 2,
            Joints = new[]
            {
                new IqmJoint("root", -1, Vector3.Zero, yaw90, Vector3.One),
                new IqmJoint("shot", 0, new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
            },
        };

        Vector3? got = MuzzleTag.Extract(iqm);
        Assert.NotNull(got);
        Assert.Equal(0f, got!.Value.X, 2);
        Assert.Equal(10f, got.Value.Y, 2);
        Assert.Equal(0f, got.Value.Z, 2);
    }

    [Fact]
    public void MuzzleTag_Dpm_Composes_Bind_Frame_Pose()
    {
        // Identity-basis poses (Right=+X, Up=+Y, Forward=+Z) with translations; child "tag_shot" under root.
        DpmBonePose Pose(Vector3 origin) =>
            new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, origin);

        var dpm = new DpmData
        {
            Bones = new[]
            {
                new DpmBone("root", -1, 0),
                new DpmBone("tag_shot", 0, DpmBone.AttachmentFlag),
            },
            Frames = new[]
            {
                new DpmFrame
                {
                    Name = "base",
                    BonePoses = new[] { Pose(new Vector3(5, 0, 0)), Pose(new Vector3(12, 0, 0)) },
                },
            },
        };

        Vector3? got = MuzzleTag.Extract(dpm);
        Assert.NotNull(got);
        Assert.Equal(17f, got!.Value.X, 3);
        Assert.Equal(0f, got.Value.Y, 3);
        Assert.Equal(0f, got.Value.Z, 3);
    }

    // ---- ComputeShotOrigin: Base's full v_-then-h_ movedir selection (CL_WeaponEntity_SetModel) -------

    [Fact]
    public void ComputeShotOrigin_Composes_VShotTag_Through_HWeaponBone()
    {
        // The v_ model HAS a shot tag (branch 3, all.qc:411-412): movedir = (h_ weapon-attach bone rest) o
        // (v_ shot tag local). Here the h_ "weapon" bone is yawed +90deg about Z and translated +X 5, and the
        // v_ shot tag sits +X 10 in the v_ model's own frame. The composed muzzle = rotate the (10,0,0) offset
        // by +90deg (-> (0,10,0)) then add the bone translation (5,0,0) = (5,10,0). Proves the attach-bone
        // ROTATION is applied to the v_ shot-tag offset (not just two translations summed).
        Quaternion yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var vModel = new Md3Data
        {
            FrameCount = 1,
            Tags = new[] { Md3ShotTag(new Vector3(10, 0, 0)) },
            TagsByName = new Dictionary<string, Md3Tag>(StringComparer.Ordinal)
                { ["tag_shot"] = Md3ShotTag(new Vector3(10, 0, 0)) },
        };
        var hRig = new IqmData
        {
            Version = 2,
            Joints = new[]
            {
                new IqmJoint("origin", -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
                new IqmJoint("weapon", 0, new Vector3(5, 0, 0), yaw90, Vector3.One),
                // the rig also has its own shot tag — but the v_ tag must WIN (branch 3 before branch 4).
                new IqmJoint("shot", 0, new Vector3(99, 99, 99), Quaternion.Identity, Vector3.One),
            },
        };

        Vector3? got = MuzzleTag.ComputeShotOrigin(MuzzleTag.Model.Of(vModel), MuzzleTag.Model.Of(hRig));
        Assert.NotNull(got);
        Assert.Equal(5f, got!.Value.X, 2);
        Assert.Equal(10f, got.Value.Y, 2);
        Assert.Equal(0f, got.Value.Z, 2);
    }

    [Fact]
    public void ComputeShotOrigin_Falls_Back_To_HRig_OwnShotTag_When_VModel_TagLess()
    {
        // The v_ model has NO shot tag (branch 4, all.qc:413-417) — the live path for ALL stock weapons.
        // movedir = the h_ rig's OWN shot tag, regardless of the rig's weapon-attach bone.
        var vModel = new Md3Data { FrameCount = 1 }; // tag-less, like every shipped v_*.md3
        var hRig = new IqmData
        {
            Version = 2,
            Joints = new[]
            {
                new IqmJoint("origin", -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
                new IqmJoint("weapon", 0, new Vector3(5, 0, -15), Quaternion.Identity, Vector3.One),
                new IqmJoint("shot", 1, new Vector3(30, 0, 5), Quaternion.Identity, Vector3.One),
            },
        };

        Vector3? got = MuzzleTag.ComputeShotOrigin(MuzzleTag.Model.Of(vModel), MuzzleTag.Model.Of(hRig));
        Assert.NotNull(got);
        // h_ own shot world rest = weapon(5,0,-15) + shot(30,0,5) = (35,0,-10).
        Assert.Equal(35f, got!.Value.X, 3);
        Assert.Equal(0f, got.Value.Y, 3);
        Assert.Equal(-10f, got.Value.Z, 3);
    }

    [Fact]
    public void ComputeShotOrigin_Null_When_Neither_Has_Shot_Tag()
    {
        var vModel = new Md3Data { FrameCount = 1 };
        var hRig = new IqmData
        {
            Version = 2,
            Joints = new[] { new IqmJoint("origin", -1, Vector3.Zero, Quaternion.Identity, Vector3.One) },
        };
        Assert.Null(MuzzleTag.ComputeShotOrigin(MuzzleTag.Model.Of(vModel), MuzzleTag.Model.Of(hRig)));
        Assert.Null(MuzzleTag.ComputeShotOrigin(MuzzleTag.Model.None, MuzzleTag.Model.None));
    }

    private static Md3Tag Md3ShotTag(Vector3 origin) => new()
    {
        Name = "tag_shot",
        Transforms = new[] { new Md3TagTransform(origin, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ) },
    };

    // ---- real-asset regression (guards the v_-vs-h_ wiring: the shot tag lives on the h_ HAND RIG) --

    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";

    /// <summary>
    /// The real shipped weapon HAND RIGS (h_*.iqm) carry the <c>shot</c>/<c>tag_shot</c> tag and yield a sensible
    /// forward muzzle offset; the v_ VISUAL models do NOT (they're attached to the rig). This is the regression
    /// that a synthetic-only suite missed — the feature was silently inert when wired to the v_ path. CI-portable:
    /// no-ops when the asset checkout is absent (mirrors WeaponBalanceTests/ConfigTests).
    /// </summary>
    [Theory]
    [InlineData("models/weapons/h_rl.iqm")]   // Devastator (rocket launcher) — the reported weapon
    [InlineData("models/weapons/h_arc.iqm")]
    [InlineData("models/weapons/h_crylink.iqm")]
    [InlineData("models/weapons/h_electro.iqm")]
    [InlineData("models/weapons/h_hagar.iqm")]
    public void MuzzleTag_RealHandRig_Yields_Forward_Muzzle(string rel)
    {
        string path = System.IO.Path.Combine(Pk3Dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(path)) return; // no checkout → no-op

        Vector3? got = ExtractFromFile(path);
        Assert.True(got is not null, $"{rel} has no shot tag — the per-weapon muzzle would be silently inert");
        Vector3 md = got!.Value;
        // tag_shot sits well forward of the weapon origin (the verifier measured X≈18-61 across all 27 rigs),
        // and forward must dominate the lateral/vertical components (it's a muzzle down the barrel).
        Assert.True(md.X > 5f, $"{rel}: expected a forward muzzle offset, got {md}");
        Assert.True(md.X > MathF.Abs(md.Y) && md.X > MathF.Abs(md.Z), $"{rel}: forward should dominate, got {md}");
        // And it must NOT equal the generic fallback (proves a real per-weapon value was read).
        Assert.NotEqual(WeaponFiring.DefaultMuzzleOffset, md);
    }

    /// <summary>
    /// End-to-end real-asset regression for the reported weapon, the Devastator, exercising the FULL Base
    /// movedir path (<see cref="MuzzleTag.ComputeShotOrigin"/> over BOTH v_rl.md3 and h_rl.iqm), exactly as
    /// <c>AssetLoader.LoadMuzzleOffset(v, h)</c> / <c>NetGame.PrecacheWeaponModels</c> wire it.
    ///
    /// <para>VERIFIED FINDING (2026-06-08): v_rl.md3 carries NO shot tag (num_tags == 0 — true of EVERY shipped
    /// v_*.md3), so Base takes the ELSE branch (all.qc:413-417) and movedir = the h_rl rig's OWN tag_shot rest
    /// = (40.936, -9.000, -16.958). The composition branch (3) is therefore a no-op for stock content, and the
    /// full-path result EQUALS the h_-only single-bone value — there is no "fires low" gap in the shot ORIGIN;
    /// the v_-vs-visible-muzzle divergence is a render-side rotation-strip (GameDemo.WeaponAttachTransform),
    /// not the shot origin. This test pins that the wired v+h path reproduces the correct h_-own-shot movedir.</para>
    /// </summary>
    [Fact]
    public void ComputeShotOrigin_Devastator_RealAssets_Uses_HRig_OwnShot()
    {
        string vPath = System.IO.Path.Combine(Pk3Dir, @"models\weapons\v_rl.md3");
        string hPath = System.IO.Path.Combine(Pk3Dir, @"models\weapons\h_rl.iqm");
        if (!System.IO.File.Exists(vPath) || !System.IO.File.Exists(hPath)) return; // no checkout → no-op

        MuzzleTag.Model vModel = LoadModelFromFile(vPath);
        MuzzleTag.Model hRig = LoadModelFromFile(hPath);

        Vector3? full = MuzzleTag.ComputeShotOrigin(vModel, hRig);
        Assert.NotNull(full);
        Vector3 md = full!.Value;

        // The faithful Devastator movedir = h_rl's own tag_shot (the v_ model is tag-less).
        Assert.Equal(40.936f, md.X, 2);
        Assert.Equal(-9.000f, md.Y, 2);
        Assert.Equal(-16.958f, md.Z, 2);

        // It must equal the h_-only single-bone extraction (proves branch (4) is what's live — no composition
        // delta for stock content) and NOT the generic fallback.
        Vector3? hOnly = ExtractFromFile(hPath);
        Assert.NotNull(hOnly);
        Assert.Equal(hOnly!.Value.X, md.X, 4);
        Assert.Equal(hOnly.Value.Y, md.Y, 4);
        Assert.Equal(hOnly.Value.Z, md.Z, 4);
        Assert.NotEqual(WeaponFiring.DefaultMuzzleOffset, md);
    }

    /// <summary>
    /// The Blaster (the reported weapon): an IQM invisible-hand rig (h_laser). Its real movedir is
    /// (24.2, -5.8, -8.9) — forward, then to the RIGHT (y is +left, so -5.8 = 5.8 right) and DOWN. So the shot
    /// must leave lower-RIGHT (where the gun is held), NOT screen-center. A weapon firing "centered horizontally,
    /// below the crosshair" is the generic fallback (12,0,-8) (y==0) — i.e. the per-weapon offset failed to
    /// register (the AssetLoader h_-rig load regression). This pins the real value + that y != 0.
    /// </summary>
    [Fact]
    public void ComputeShotOrigin_Blaster_RealAssets_IsForwardRightAndDown_NotCentered()
    {
        string vPath = System.IO.Path.Combine(Pk3Dir, @"models\weapons\v_laser.md3");
        string hPath = System.IO.Path.Combine(Pk3Dir, @"models\weapons\h_laser.iqm");
        if (!System.IO.File.Exists(vPath) || !System.IO.File.Exists(hPath)) return; // no checkout → no-op

        Vector3? full = MuzzleTag.ComputeShotOrigin(LoadModelFromFile(vPath), LoadModelFromFile(hPath));
        Assert.NotNull(full);
        Vector3 md = full!.Value;

        Assert.Equal(24.230f, md.X, 2);
        Assert.Equal(-5.819f, md.Y, 2);  // NEGATIVE y = to the RIGHT — NOT centered (the bug shows y==0)
        Assert.Equal(-8.857f, md.Z, 2);
        Assert.True(MathF.Abs(md.Y) > 1f, "blaster muzzle must be offset sideways (to the gun), not horizontally centered");
        Assert.NotEqual(WeaponFiring.DefaultMuzzleOffset, md);
    }

    /// <summary>Magic-byte model dispatch (extensions lie) → <see cref="MuzzleTag"/>, mirroring AssetLoader.</summary>
    private static Vector3? ExtractFromFile(string path)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        string magic = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        if (magic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(IqmReader.Read(bytes));
        if (magic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(DpmReader.Read(bytes));
        if (magic.StartsWith("IDP3", StringComparison.Ordinal)) return MuzzleTag.Extract(Md3Reader.Read(bytes));
        return null;
    }

    /// <summary>Magic-dispatch a real model file into a <see cref="MuzzleTag.Model"/>, mirroring AssetLoader.LoadMuzzleModel.</summary>
    private static MuzzleTag.Model LoadModelFromFile(string path)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        string magic = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        if (magic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal)) return MuzzleTag.Model.Of(IqmReader.Read(bytes));
        if (magic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal)) return MuzzleTag.Model.Of(DpmReader.Read(bytes));
        if (magic.StartsWith("IDP3", StringComparison.Ordinal)) return MuzzleTag.Model.Of(Md3Reader.Read(bytes));
        return MuzzleTag.Model.None;
    }

    // ---- scaffolding -------------------------------------------------------------------------------

    /// <summary>A trace that never obstructs: every leg lands at the requested end (free space).</summary>
    private sealed class FreeTrace : ITraceService
    {
        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
            => TraceResult.Miss(end);
        public int PointContents(Vector3 point) => 0;
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    private sealed class MuzzleEnv : IDisposable
    {
        private readonly IEngineServices _prev;
        public MuzzleEnv() { _prev = Api.Services; Api.Services = new MuzzleServices(new FreeTrace()); }
        public void Dispose() { WeaponFiring.ClearMuzzleOffsets(); Api.Services = _prev; }
    }

    private sealed class MuzzleServices : IEngineServices
    {
        public ITraceService Trace { get; }
        public IGameClock Clock { get; } = new ZeroClock();
        public ICvarService Cvars { get; } = new EmptyCvars();
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();
        public MuzzleServices(ITraceService trace) => Trace = trace;

        private sealed class ZeroClock : IGameClock { public float Time => 0f; public float FrameTime => 1f / 72f; }
        private sealed class EmptyCvars : ICvarService
        {
            public float GetFloat(string name) => 0f;
            public string GetString(string name) => "";
            public void Set(string name, string value) { }
            public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
        }
        private sealed class NullEntities : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => Array.Empty<Entity>();
        }
        private sealed class NullSound : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
