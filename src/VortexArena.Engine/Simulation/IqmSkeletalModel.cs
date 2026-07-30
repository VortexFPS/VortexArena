using XonoticGodot.Formats.Iqm;
using XonoticGodot.Common.Math;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Adapts a parsed <see cref="IqmData"/> (bone hierarchy + per-frame poses) to <see cref="ISkeletalModel"/>
/// so the <see cref="SkeletonManager"/> <c>skel_*</c> operations can pose it. Bind and animation transforms
/// are built straight from the IQM joints' / frames' TRS — the same data the renderer skins with.
/// </summary>
public sealed class IqmSkeletalModel : ISkeletalModel
{
    private readonly IqmData _iqm;
    public IqmSkeletalModel(IqmData iqm) => _iqm = iqm;

    public int BoneCount => _iqm.Joints.Length;
    public string BoneName(int bone) => (bone >= 0 && bone < _iqm.Joints.Length) ? _iqm.Joints[bone].Name : "";
    public int BoneParent(int bone) => (bone >= 0 && bone < _iqm.Joints.Length) ? _iqm.Joints[bone].Parent : -1;

    public BoneMatrix BindRelative(int bone)
    {
        if (bone < 0 || bone >= _iqm.Joints.Length) return BoneMatrix.Identity;
        IqmJoint j = _iqm.Joints[bone];
        return BoneMatrix.FromTRS(j.Translate, j.Rotate, j.Scale);
    }

    public int FrameCount => _iqm.Frames.Length;

    public BoneMatrix FrameRelative(int frame, int bone)
    {
        if (_iqm.Frames.Length == 0) return BindRelative(bone);
        int f = frame < 0 ? 0 : (frame >= _iqm.Frames.Length ? _iqm.Frames.Length - 1 : frame);
        IqmBonePose[] bones = _iqm.Frames[f].Bones;
        if (bone < 0 || bone >= bones.Length) return BindRelative(bone);
        IqmBonePose p = bones[bone];
        return BoneMatrix.FromTRS(p.Translate, p.Rotate, p.Scale);
    }
}
