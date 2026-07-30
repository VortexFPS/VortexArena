using System.Numerics;
using XonoticGodot.Common.Math;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// The per-model skeletal split/aim parameters (Xonotic <c>get_model_parameters</c> / the model's
/// <c>.modelinfo</c>): which bone divides the upper and lower body, whether the upper body needs re-anchoring
/// (<see cref="FixBone"/>), and the spine/head aim bones bent by the view pitch with their per-bone weights.
/// Bone numbers are 1-based (0 = unused), as returned by <see cref="SkeletonManager.FindBone"/>.
/// </summary>
public sealed class PlayerSkeletonConfig
{
    public int BoneUpperBody;                 // the split bone (gettagindex), 0 = no split
    public int BoneWeapon;                    // the weapon attachment bone, 0 = none
    public bool FixBone;                      // re-anchor the upper body after the split
    public List<(int bone, float weight)> AimBones { get; } = new(); // spine/head bones bent by v_angle.x

    /// <summary>
    /// Resolve a parsed <see cref="XonoticGodot.Formats.Sidecars.ModelInfo"/> to a config for skeleton
    /// <paramref name="skel"/>, mapping bone NAMES → 1-based bone numbers via
    /// <see cref="SkeletonManager.FindBone"/> (against the model's raw joint names). Unresolvable bones drop
    /// out (0/skipped), matching the QC's <c>gettagindex</c>-returns-0 behaviour. The weapon bone falls back
    /// to the engine defaults <c>weapon</c>/<c>tag_weapon</c>/<c>bip01 r hand</c> like <c>skeleton_loadinfo</c>.
    /// </summary>
    public static PlayerSkeletonConfig FromModelInfo(XonoticGodot.Formats.Sidecars.ModelInfo info, SkeletonManager mgr, int skel)
    {
        var cfg = new PlayerSkeletonConfig
        {
            BoneUpperBody = mgr.FindBone(skel, info.BoneUpperBody),
            FixBone = info.FixBone,
        };
        // QC: fixbone only matters when the split bone exists.
        if (cfg.BoneUpperBody == 0) cfg.FixBone = false;

        cfg.BoneWeapon = mgr.FindBone(skel, info.BoneWeapon);
        if (cfg.BoneWeapon == 0) cfg.BoneWeapon = mgr.FindBone(skel, "weapon");
        if (cfg.BoneWeapon == 0) cfg.BoneWeapon = mgr.FindBone(skel, "tag_weapon");
        if (cfg.BoneWeapon == 0) cfg.BoneWeapon = mgr.FindBone(skel, "bip01 r hand");

        foreach ((float weight, string bone) in info.AimBones)
        {
            int b = mgr.FindBone(skel, bone);
            if (b != 0) cfg.AimBones.Add((b, weight));
        }
        return cfg;
    }
}

/// <summary>
/// The player-model skeletal animation port (<c>qcsrc/client/player_skeleton.qc</c>): the CPU upper/lower-body
/// split (the torso plays the aiming animation while the legs play the run/strafe cycle) plus the view-pitch
/// AIM bones (the spine/head bend so the avatar looks where the player looks). Built on the <c>skel_*</c>
/// primitives in <see cref="SkeletonManager"/>; Godot-free, so the renderer just reads the posed bone matrices
/// (<see cref="GetBoneAbs"/>) and pushes them onto its Skeleton3D.
///
/// Frames 1+3 drive the upper body, frames 2+4 the lower body — exactly the QC's lerpfrac juggling in
/// <c>skeleton_from_frames</c>.
/// </summary>
public sealed class PlayerSkeleton
{
    private const int BoneTypeLower = 0;
    private const int BoneTypeUpper = 1;

    private readonly SkeletonManager _mgr;
    private readonly ISkeletalModel _model;
    private readonly PlayerSkeletonConfig _cfg;

    private int _skel;
    private int _numBones;
    private int[] _boneType = System.Array.Empty<int>();   // per bone (0-based)

    public PlayerSkeleton(SkeletonManager mgr, ISkeletalModel model, PlayerSkeletonConfig cfg)
    {
        _mgr = mgr; _model = model; _cfg = cfg;
    }

    public int Handle => _skel;

    /// <summary>The resolved weapon attachment bone (1-based bonenum; 0 = unresolved). The renderer hangs a
    /// remote player's held weapon off this bone (QC <c>tag_weapon</c>).</summary>
    public int BoneWeapon => _cfg.BoneWeapon;

    /// <summary>QC <c>skeleton_from_frames</c> skeleton creation + <c>skeleton_markbones</c>.</summary>
    private void EnsureCreated()
    {
        if (_skel != 0) return;
        _skel = _mgr.Create(_model);
        MarkBones();
    }

    /// <summary>QC <c>skeleton_markbones</c>: a bone is UPPER iff it is (a descendant of) the split bone.</summary>
    private void MarkBones()
    {
        _numBones = _mgr.GetNumBones(_skel);
        _boneType = new int[_numBones];
        for (int i = 1; i <= _numBones; i++) // 1-based bonenum
        {
            int t = BoneTypeLower;
            int p = _mgr.GetBoneParent(_skel, i); // 1-based parent, 0 if root
            if (p > 0) t = _boneType[p - 1];
            if (i == _cfg.BoneUpperBody) t = BoneTypeUpper;
            _boneType[i - 1] = t;
        }
    }

    /// <summary>Free the skeleton (QC <c>free_skeleton_from_frames</c> / <c>skel_delete</c>).</summary>
    public void Free()
    {
        if (_skel != 0) { _mgr.Delete(_skel); _skel = 0; }
    }

    /// <summary>
    /// QC <c>skeleton_from_frames</c>: pose the skeleton for this frame. <paramref name="anim"/> is the entity's
    /// resolved 4-frame blend (frame1-4 + lerpfrac/3/4); the upper body uses frames 1+3, the lower body frames
    /// 2+4. <paramref name="viewPitch"/> is <c>v_angle.x</c> (bends the aim bones unless <paramref name="isDead"/>).
    /// </summary>
    public void FromFrames(SkeletonAnim anim, float viewPitch, bool isDead)
    {
        EnsureCreated();
        if (_skel == 0) return;
        int n = _numBones;

        float saveLerp = anim.Lerp, saveLerp3 = anim.Lerp3, saveLerp4 = anim.Lerp4;

        // fixbone: build everything as the UPPER body first and snapshot the split bone's orientation, so it can
        // be re-anchored after the per-group split (QC fixbone_oldangles).
        BoneMatrix fixboneOrientation = default;
        bool haveFix = false;
        if (_cfg.FixBone && _cfg.BoneUpperBody > 0)
        {
            // [W14b LI3] snapshot the split bone with the SAME upper-body weighting the real upper run uses below
            // (frames 1+3+4, both doubled) so the re-anchor orientation matches the action-animated torso, not a
            // stale frame3-only pose. Lerp4 = 0 for the static aim path keeps this bit-identical there.
            SkeletonAnim a = anim; a.Lerp = 0; a.Lerp3 = saveLerp3 * 2; a.Lerp4 = saveLerp4 * 2;
            _mgr.Build(_skel, a, _model, 0f, 1, n);
            fixboneOrientation = _mgr.GetBoneAbs(_skel, _cfg.BoneUpperBody);
            haveFix = true;
        }

        // Walk contiguous same-type bone runs, building each with its body's frame pair.
        int bone = 0;
        while (bone < n)
        {
            int firstBone = bone;
            int type = _boneType[bone];
            for (bone++; bone < n && _boneType[bone] == type; bone++) { }

            SkeletonAnim a = anim;
            // [W14b LI3] UPPER body = the torso/action frames 3+4 (frame1 weight → 0 when Lerp3+Lerp4 = 0.5),
            // both DOUBLED so a (Lerp3,Lerp4) pair summing to 0.5 fully fills the upper body with the action clip
            // (Frame3 current + Frame4 next). The static aim path passes Lerp4 = 0, so this stays bit-identical
            // (frame3 only) there; an active action passes a non-zero Lerp4 to animate frame3→frame4.
            if (type == BoneTypeUpper) { a.Lerp = 0; a.Lerp3 = saveLerp3 * 2; a.Lerp4 = saveLerp4 * 2; } // frames 1+3+4
            // LOWER body = the LEGS frames 1+2 only. Frame4 carries the TORSO's next frame (LI3), which must NEVER
            // bleed into the legs, so pin the lower Lerp4 to 0 (was saveLerp4*2 — a no-op for the static path where
            // Lerp4 was always 0, but it would tear the torso into the legs once an action feeds a non-zero Lerp4).
            else { a.Lerp = saveLerp * 2; a.Lerp3 = 0; a.Lerp4 = 0; }                                    // frames 1+2
            _mgr.Build(_skel, a, _model, 0f, firstBone + 1, bone); // 1-based inclusive [firstBone+1 .. bone]
        }

        if (haveFix)
        {
            // keep the post-split origin, restore the captured upper-body orientation.
            BoneMatrix cur = _mgr.GetBoneAbs(_skel, _cfg.BoneUpperBody);
            SetBoneAbs(_cfg.BoneUpperBody,
                new BoneMatrix(fixboneOrientation.Fwd, fixboneOrientation.Left, fixboneOrientation.Up, cur.Origin));
        }

        if (!isDead)
        {
            float pitch = QMath.Bound(-90f, viewPitch, 90f);
            foreach ((int aimBone, float weight) in _cfg.AimBones)
            {
                if (aimBone <= 0) continue;
                BoneMatrix curAbs = _mgr.GetBoneAbs(_skel, aimBone);
                // an extra pitch*weight rotation about the bone's left/right axis (QC AnglesTransform_Multiply
                // of aim=(pitch*weight,0,0) onto the bone's current orientation), keeping the bone's origin.
                QMath.AngleVectors(new Vector3(pitch * weight, 0f, 0f), out Vector3 f, out Vector3 r, out Vector3 u);
                BoneMatrix aim = BoneMatrix.FromVectors(f, -r, u, Vector3.Zero);
                BoneMatrix newAbs = new(aim.Rotate(curAbs.Fwd), aim.Rotate(curAbs.Left), aim.Rotate(curAbs.Up), curAbs.Origin);
                SetBoneAbs(aimBone, newAbs);
            }
        }
    }

    /// <summary>The bone's model-space transform after posing (the renderer reads this). 1-based bonenum.</summary>
    public BoneMatrix GetBoneAbs(int bonenum) => _mgr.GetBoneAbs(_skel, bonenum);

    /// <summary>The bone's local (relative-to-parent) transform after posing. 1-based bonenum.</summary>
    public BoneMatrix GetBoneRel(int bonenum) => _mgr.GetBoneRel(_skel, bonenum);

    /// <summary>
    /// QC <c>skel_set_boneabs</c> (player_skeleton.qc): set a bone's MODEL-SPACE transform by deriving the
    /// relative transform from its parent's current absolute transform — <c>rel = parentAbs⁻¹ · abs</c> (the
    /// matrix-native form of the QC's AnglesTransform left-divide). 1-based bonenum.
    /// </summary>
    private void SetBoneAbs(int bonenum, in BoneMatrix abs)
    {
        int parent = _mgr.GetBoneParent(_skel, bonenum); // 1-based parent, 0 if root
        if (parent > 0)
        {
            BoneMatrix parentAbs = _mgr.GetBoneAbs(_skel, parent);
            _mgr.SetBone(_skel, bonenum, BoneMatrix.Concat(parentAbs.InverseOrthonormal(), abs));
        }
        else
        {
            _mgr.SetBone(_skel, bonenum, abs);
        }
    }
}
