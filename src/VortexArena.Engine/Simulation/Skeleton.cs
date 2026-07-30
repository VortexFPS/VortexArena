using XonoticGodot.Common.Math;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// The bone hierarchy + animation-frame source a <see cref="Skeleton"/> poses against — the C# stand-in for
/// DarkPlaces <c>model_t</c>'s skeletal data (<c>data_bones</c> + <c>data_poses7s</c>). Bones are 0-indexed;
/// a bone's <see cref="BoneParent"/> is always less than its own index (roots return -1), as in IQM/MD5.
/// </summary>
public interface ISkeletalModel
{
    int BoneCount { get; }
    string BoneName(int bone);
    int BoneParent(int bone);

    /// <summary>The bone's bind-pose transform, relative to its parent.</summary>
    BoneMatrix BindRelative(int bone);

    /// <summary>Number of animation frames (0 if the model has only a bind pose).</summary>
    int FrameCount { get; }

    /// <summary>The bone's transform (relative to its parent) at animation frame <paramref name="frame"/>
    /// (clamped into range; falls back to the bind pose when the model has no frames).</summary>
    BoneMatrix FrameRelative(int frame, int bone);
}

/// <summary>
/// The per-entity animation state the skeletal blend reads — QuakeC's <c>.frame/.frame2/.frame3/.frame4</c>
/// plus <c>.lerpfrac/.lerpfrac3/.lerpfrac4</c> (the four-pose blend used for the upper/lower body split). The
/// frame fields are resolved frame indices (the animator picks them); skel_build blends them by these weights.
/// </summary>
public struct SkeletonAnim
{
    public int Frame, Frame2, Frame3, Frame4;
    public float Lerp, Lerp3, Lerp4;

    public SkeletonAnim(int frame = 0, int frame2 = 0, int frame3 = 0, int frame4 = 0,
        float lerp = 0f, float lerp3 = 0f, float lerp4 = 0f)
    {
        Frame = frame; Frame2 = frame2; Frame3 = frame3; Frame4 = frame4;
        Lerp = lerp; Lerp3 = lerp3; Lerp4 = lerp4;
    }
}

/// <summary>
/// A CPU pose of a skeleton — DP's <c>skeleton_t</c>: the model's bone hierarchy plus one relative-to-parent
/// <see cref="BoneMatrix"/> per bone, blended in by <see cref="SkeletonManager"/>'s <c>skel_*</c> operations.
/// Created with identity relative transforms (matching <c>skel_create</c>); <c>skel_build</c> fills them.
/// </summary>
public sealed class Skeleton
{
    public ISkeletalModel Model { get; }
    public BoneMatrix[] Relative { get; }

    public Skeleton(ISkeletalModel model)
    {
        Model = model;
        Relative = new BoneMatrix[model.BoneCount];
        for (int i = 0; i < Relative.Length; i++)
            Relative[i] = BoneMatrix.Identity; // DP skel_create initializes to identity
    }
}

/// <summary>
/// The skeletal CPU-manipulation builtins (<c>skel_*</c>, DP <c>VM_CL_skel_*</c> in clvm_cmds.c) — procedural
/// bone blend/aim used by the player model's upper/lower-body split and view-pitch aiming
/// (<c>qcsrc/client/player_skeleton.qc</c>). Godot-free: poses are computed on the CPU as
/// <see cref="BoneMatrix"/> values, which the renderer then applies to its Skeleton3D.
///
/// Handles are 1-based (0 == "no skeleton") and <paramref name="bonenum"/> arguments are 1-based, exactly as
/// the QuakeC builtins expose them. The <c>v_forward/v_right/v_up</c> globals the QC reads/writes are modelled
/// as the columns of the <see cref="BoneMatrix"/> (with <c>v_right = -Left</c>), so a caller mirrors that sign
/// at the boundary (see <see cref="GetBoneAbsVectors"/> / <see cref="SetBoneFromVectors"/>).
/// </summary>
public sealed class SkeletonManager
{
    private readonly List<Skeleton?> _skeletons = new();

    /// <summary>skel_create(modelindex): allocate a skeleton for a skeletal model; returns a 1-based handle (0 on failure).</summary>
    public int Create(ISkeletalModel? model)
    {
        if (model is null || model.BoneCount <= 0)
            return 0;
        for (int i = 0; i < _skeletons.Count; i++)
            if (_skeletons[i] is null) { _skeletons[i] = new Skeleton(model); return i + 1; }
        _skeletons.Add(new Skeleton(model));
        return _skeletons.Count;
    }

    /// <summary>skel_delete(skel).</summary>
    public void Delete(int skel)
    {
        int i = skel - 1;
        if (i >= 0 && i < _skeletons.Count) _skeletons[i] = null;
    }

    private Skeleton? Get(int skel)
    {
        int i = skel - 1;
        return (i >= 0 && i < _skeletons.Count) ? _skeletons[i] : null;
    }

    /// <summary>skel_get_numbones(skel).</summary>
    public int GetNumBones(int skel) => Get(skel)?.Model.BoneCount ?? 0;

    /// <summary>skel_get_bonename(skel, bonenum) — 1-based bonenum.</summary>
    public string GetBoneName(int skel, int bonenum)
    {
        Skeleton? s = Get(skel);
        int b = bonenum - 1;
        return (s is not null && b >= 0 && b < s.Model.BoneCount) ? s.Model.BoneName(b) : "";
    }

    /// <summary>skel_get_boneparent(skel, bonenum): returns the parent's 1-based number (0 if root / invalid).</summary>
    public int GetBoneParent(int skel, int bonenum)
    {
        Skeleton? s = Get(skel);
        int b = bonenum - 1;
        if (s is null || b < 0 || b >= s.Model.BoneCount) return 0;
        return s.Model.BoneParent(b) + 1; // DP: parent + 1 (root parent -1 -> 0)
    }

    /// <summary>skel_find_bone(skel, name): 1-based bone number, 0 if not found.</summary>
    public int FindBone(int skel, string name)
    {
        Skeleton? s = Get(skel);
        if (s is null) return 0;
        for (int b = 0; b < s.Model.BoneCount; b++)
            if (string.Equals(s.Model.BoneName(b), name, System.StringComparison.Ordinal))
                return b + 1;
        return 0;
    }

    /// <summary>skel_get_bonerel(skel, bonenum): the bone's transform relative to its parent.</summary>
    public BoneMatrix GetBoneRel(int skel, int bonenum)
    {
        Skeleton? s = Get(skel);
        int b = bonenum - 1;
        return (s is not null && b >= 0 && b < s.Relative.Length) ? s.Relative[b] : BoneMatrix.Identity;
    }

    /// <summary>skel_get_boneabs(skel, bonenum): the bone's transform in model space (parent chain composed).</summary>
    public BoneMatrix GetBoneAbs(int skel, int bonenum)
    {
        Skeleton? s = Get(skel);
        int b = bonenum - 1;
        if (s is null || b < 0 || b >= s.Relative.Length) return BoneMatrix.Identity;
        BoneMatrix matrix = s.Relative[b];
        int parent = b;
        while ((parent = s.Model.BoneParent(parent)) >= 0)
            matrix = BoneMatrix.Concat(s.Relative[parent], matrix);
        return matrix;
    }

    /// <summary>skel_set_bone(skel, bonenum, matrix): set the bone's relative transform.</summary>
    public void SetBone(int skel, int bonenum, in BoneMatrix matrix)
    {
        Skeleton? s = Get(skel);
        int b = bonenum - 1;
        if (s is not null && b >= 0 && b < s.Relative.Length) s.Relative[b] = matrix;
    }

    /// <summary>skel_mul_bone(skel, bonenum, matrix): pre-multiply the bone's relative transform by <paramref name="matrix"/>.</summary>
    public void MulBone(int skel, int bonenum, in BoneMatrix matrix)
    {
        Skeleton? s = Get(skel);
        int b = bonenum - 1;
        if (s is not null && b >= 0 && b < s.Relative.Length)
            s.Relative[b] = BoneMatrix.Concat(matrix, s.Relative[b]);
    }

    /// <summary>skel_mul_bones(skel, firstbone, lastbone, matrix): mul a range of bones (1-based, inclusive).</summary>
    public void MulBones(int skel, int firstbone, int lastbone, in BoneMatrix matrix)
    {
        Skeleton? s = Get(skel);
        if (s is null) return;
        int first = System.Math.Max(0, firstbone - 1);
        int last = System.Math.Min(lastbone - 1, s.Relative.Length - 1);
        for (int b = first; b <= last; b++)
            s.Relative[b] = BoneMatrix.Concat(matrix, s.Relative[b]);
    }

    /// <summary>skel_copybones(dst, src, firstbone, lastbone): copy a relative-transform range (1-based, inclusive).</summary>
    public void CopyBones(int dst, int src, int firstbone, int lastbone)
    {
        Skeleton? d = Get(dst), srcSkel = Get(src);
        if (d is null || srcSkel is null) return;
        int first = System.Math.Max(0, firstbone - 1);
        int last = System.Math.Min(lastbone - 1, System.Math.Min(d.Relative.Length, srcSkel.Relative.Length) - 1);
        for (int b = first; b <= last; b++)
            d.Relative[b] = srcSkel.Relative[b];
    }

    /// <summary>
    /// skel_build(skel, ent, modelindex, retainfrac, firstbone, lastbone): blend the animation defined by
    /// <paramref name="anim"/> into the bone range [firstbone, lastbone] (1-based, inclusive), keeping
    /// <paramref name="retainfrac"/> of the existing pose. The four frame poses are weighted by
    /// (1-lerp-lerp3-lerp4, lerp, lerp3, lerp4), accumulated, renormalized, then interpolated with the
    /// current relative transform. Returns the handle on success, 0 on failure.
    /// </summary>
    public int Build(int skel, in SkeletonAnim anim, ISkeletalModel model, float retainfrac, int firstbone, int lastbone)
    {
        Skeleton? s = Get(skel);
        if (s is null || model is null) return 0;

        int first = System.Math.Max(0, firstbone - 1);
        int last = System.Math.Min(lastbone - 1, System.Math.Min(model.BoneCount, s.Model.BoneCount) - 1);

        float w0 = 1f - anim.Lerp - anim.Lerp3 - anim.Lerp4;
        float w1 = anim.Lerp, w2 = anim.Lerp3, w3 = anim.Lerp4;

        for (int b = first; b <= last; b++)
        {
            BoneMatrix acc = BoneMatrix.Zero;
            float total = 0f;
            if (w0 > 0f) { acc = acc.Accumulate(model.FrameRelative(anim.Frame, b), w0); total += w0; }
            if (w1 > 0f) { acc = acc.Accumulate(model.FrameRelative(anim.Frame2, b), w1); total += w1; }
            if (w2 > 0f) { acc = acc.Accumulate(model.FrameRelative(anim.Frame3, b), w2); total += w2; }
            if (w3 > 0f) { acc = acc.Accumulate(model.FrameRelative(anim.Frame4, b), w3); total += w3; }
            if (total <= 0f)
                acc = model.FrameRelative(anim.Frame, b); // degenerate weights → just the base frame
            acc = acc.Normalize3();
            s.Relative[b] = BoneMatrix.Interpolate(acc, s.Relative[b], retainfrac);
        }
        return skel;
    }

    // --- convenience helpers exposing the v_forward/v_right/v_up boundary (DP negates left into v_right) ---

    /// <summary>skel_get_boneabs as (origin, v_forward, v_right, v_up) — the form the QC reads after the call.</summary>
    public void GetBoneAbsVectors(int skel, int bonenum, out System.Numerics.Vector3 origin,
        out System.Numerics.Vector3 vForward, out System.Numerics.Vector3 vRight, out System.Numerics.Vector3 vUp)
    {
        BoneMatrix m = GetBoneAbs(skel, bonenum);
        m.ToVectors(out vForward, out System.Numerics.Vector3 left, out vUp, out origin);
        vRight = -left; // DP: VectorNegate(left, v_right)
    }

    /// <summary>skel_set_bone from (v_forward, v_right, v_up, origin) — the form the QC passes (left = -v_right).</summary>
    public void SetBoneFromVectors(int skel, int bonenum, System.Numerics.Vector3 vForward,
        System.Numerics.Vector3 vRight, System.Numerics.Vector3 vUp, System.Numerics.Vector3 origin)
        => SetBone(skel, bonenum, BoneMatrix.FromVectors(vForward, -vRight, vUp, origin));
}
