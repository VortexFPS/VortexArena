using System.Numerics;
using VortexArena.Common.Math;

namespace VortexArena.Engine.Simulation;

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
    /// Resolve a parsed <see cref="VortexArena.Formats.Sidecars.ModelInfo"/> to a config for skeleton
    /// <paramref name="skel"/>, mapping bone NAMES → 1-based bone numbers via
    /// <see cref="SkeletonManager.FindBone"/> (against the model's raw joint names). Unresolvable bones drop
    /// out (0/skipped), matching the QC's <c>gettagindex</c>-returns-0 behaviour. The weapon bone falls back
    /// to the engine defaults <c>weapon</c>/<c>tag_weapon</c>/<c>bip01 r hand</c> like <c>skeleton_loadinfo</c>.
    /// </summary>
    public static PlayerSkeletonConfig FromModelInfo(VortexArena.Formats.Sidecars.ModelInfo info, SkeletonManager mgr, int skel)
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
        => FromFrames(anim, anim, 0f, 0f, viewPitch, isDead);

    /// <summary>
    /// The crossfading form (Base csqcmodel's <c>cl_lerpanim</c> framegroup blend): pose from the CURRENT blend
    /// <paramref name="anim"/> while keeping <paramref name="legsRetain"/> / <paramref name="torsoRetain"/> of
    /// the OUTGOING blend <paramref name="prev"/> per channel (1 = all old → 0 = all new; the caller ramps each
    /// over the fade window). Composed exactly the way DP's <c>skel_build</c> is meant to be layered: build the
    /// old pose (retainfrac 0), then the new pose with <c>retainfrac = oldWeight</c> — the
    /// <c>Matrix4x4_Interpolate</c> tail of VM_CL_skel_build (clvm_cmds.c:4651-4661), which DP does NOT
    /// renormalize after, and neither do we. With both retains 0 this is a single build per bone run,
    /// bit-identical to the plain form.
    /// </summary>
    public void FromFrames(SkeletonAnim anim, SkeletonAnim prev, float legsRetain, float torsoRetain,
        float viewPitch, bool isDead)
    {
        EnsureCreated();
        if (_skel == 0) return;
        int n = _numBones;

        // The per-channel frame routing (the QC's lerpfrac juggling, [W14b LI3]):
        // UPPER = the torso frames 3+4, both DOUBLED so a (Lerp3,Lerp4) pair summing to 0.5 fully fills the
        // upper body (frame1's weight → 0). LOWER = the legs frames 1+2 only; the torso lerps are pinned to 0
        // so the torso frames can NEVER bleed into the legs. Copies by value — the caller's anim is untouched.
        static SkeletonAnim Upper(SkeletonAnim a) { a.Lerp = 0f; a.Lerp3 *= 2f; a.Lerp4 *= 2f; return a; }
        static SkeletonAnim Lower(SkeletonAnim a) { a.Lerp *= 2f; a.Lerp3 = 0f; a.Lerp4 = 0f; return a; }

        // Build one channel's bone range, crossfading from the outgoing blend when retain > 0.
        void BuildRange(in SkeletonAnim cur, in SkeletonAnim old, float retain, int first1, int last1)
        {
            if (retain > 0f)
                _mgr.Build(_skel, old, _model, 0f, first1, last1); // seed: the outgoing clips' continuing pose
            _mgr.Build(_skel, cur, _model, retain, first1, last1);
        }

        // fixbone: build everything as the UPPER body first and snapshot the split bone's orientation, so it can
        // be re-anchored after the per-group split (QC fixbone_oldangles). Uses the SAME (crossfaded) upper blend
        // the real upper run gets below, so the re-anchor matches the rendered torso mid-fade too.
        BoneMatrix fixboneOrientation = default;
        bool haveFix = false;
        if (_cfg.FixBone && _cfg.BoneUpperBody > 0)
        {
            BuildRange(Upper(anim), Upper(prev), torsoRetain, 1, n);
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

            if (type == BoneTypeUpper)
                BuildRange(Upper(anim), Upper(prev), torsoRetain, firstBone + 1, bone); // frames 3+4 (torso)
            else
                BuildRange(Lower(anim), Lower(prev), legsRetain, firstBone + 1, bone);  // frames 1+2 (legs)
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
    /// matrix-native form of the QC's AnglesTransform left-divide). 1-based bonenum. Public for the corpse
    /// ragdoll, which writes its driven bones through this exact seam (parents first).
    /// </summary>
    public void SetBoneAbs(int bonenum, in BoneMatrix abs)
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
