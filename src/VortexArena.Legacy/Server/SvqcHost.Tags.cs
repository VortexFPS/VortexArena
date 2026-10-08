// Port of Base/darkplaces/svvm_cmds.c SV_GetTagIndex, SV_GetExtendedTagInfo, SV_GetEntityMatrix,
// SV_GetEntityLocalTagMatrix, SV_GetTagMatrix, VM_SV_gettaginfo; sv_phys.c SV_GetPitchSign; prvm_cmds.c
// VM_GenerateFrameGroupBlend and VM_FrameBlendFromFrameGroupBlend; model_alias.c Mod_Alias_GetTagMatrix
// and Mod_Alias_GetExtendedTagInfoForIndex. The client half's counterpart is
// Csqc/Headless/FormatLegacyModels.cs (CL_GetTagMatrix), whose model reader this shares.
using System.Numerics;
using VortexArena.Common.Math;
using VortexArena.Engine.Collision;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvqcHost
{
    private const int MaxFrameBlends = 8;   // MAX_FRAMEBLENDS

    private struct FrameBlend { public int SubFrame; public float Lerp; }

    // SV_GetModelFromEdict, loaded: an alias model (or sprite) with frames and tags. Null for no
    // model, a map submodel, or a file that does not load.
    private LegacyModel? TagModelOf(int edict)
    {
        string model = ModelName(QcVm.FloatToInt(Fl(edict, F.ModelIndex)));
        return model.Length == 0 || model[0] == '*' || ModelIsBrush(QcVm.FloatToInt(Fl(edict, F.ModelIndex))) ? null : Models.Load(model);
    }

    // VM_GenerateFrameGroupBlend + VM_FrameBlendFromFrameGroupBlend: the entity's .frame .. .frame4,
    // their start times and .lerpfrac .. .lerpfrac4, turned into up to eight weighted poses.
    private void FrameBlends(int edict, LegacyModel model, Span<FrameBlend> blend)
    {
        blend.Clear();
        Span<int> frame = stackalloc int[4];
        Span<float> start = stackalloc float[4], lerp = stackalloc float[4];
        frame[0] = QcVm.FloatToInt(Fl(edict, F.Frame));
        frame[1] = QcVm.FloatToInt(Fl(edict, F.Frame2));
        frame[2] = QcVm.FloatToInt(Fl(edict, F.Frame3));
        frame[3] = QcVm.FloatToInt(Fl(edict, F.Frame4));
        start[0] = Fl(edict, F.Frame1Time);
        start[1] = Fl(edict, F.Frame2Time);
        start[2] = Fl(edict, F.Frame3Time);
        start[3] = Fl(edict, F.Frame4Time);
        lerp[1] = Fl(edict, F.LerpFrac);
        lerp[2] = Fl(edict, F.LerpFrac3);
        lerp[3] = Fl(edict, F.LerpFrac4);
        // "assume that the (missing) lerpfrac1 is whatever remains after lerpfrac2+lerpfrac3+lerpfrac4 are summed"
        lerp[0] = 1 - lerp[1] - lerp[2] - lerp[3];

        // r_lerpmodels / r_lerpsprites are client cvars; a dedicated server has their defaults (1 and 0).
        bool noLerp = model.Kind == LegacyModelKind.Sprite;
        bool first = true;
        LegacyAnimScene[]? scenes = model.Scenes;
        int numFrames = model.NumFrames;
        for (int k = 0; k < 4; k++)
        {
            int fr = frame[k];
            if ((uint)fr >= (uint)numFrames) fr = 0;
            double d = lerp[k], share = lerp[k];
            if (!(share > 0)) continue;
            if (noLerp)
            {
                if (!first) continue;
                d = share = 1;
                first = false;
            }
            if (scenes is not null && fr < scenes.Length)
            {
                LegacyAnimScene scene = scenes[fr];
                fr = scene.FirstFrame;
                if (scene.FrameCount > 1)
                {
                    double subLerp = scene.FrameRate * (Time - start[k]);
                    double whole = Math.Floor(subLerp);
                    subLerp -= whole;
                    // The C converts to int here; a time far from the start saturates instead of wrapping.
                    int sub1 = whole >= int.MaxValue - 1 ? int.MaxValue - 1 : whole <= int.MinValue ? int.MinValue : (int)whole;
                    int sub2 = sub1 + 1;
                    if (subLerp < 1.0 / 65536.0) subLerp = 0;
                    if (subLerp > 65535.0 / 65536.0) subLerp = 1;
                    if (noLerp) subLerp = 0;
                    if (scene.Loop)
                    {
                        sub1 %= scene.FrameCount;
                        sub2 %= scene.FrameCount;
                    }
                    fr = Math.Clamp(sub1, 0, scene.FrameCount - 1) + scene.FirstFrame;
                    sub2 = Math.Clamp(sub2, 0, scene.FrameCount - 1) + scene.FirstFrame;
                    d = subLerp * share;
                    // two framelerps produced from one animation
                    if (d > 0) AddBlend(blend, sub2, (float)d);
                    d = (1 - subLerp) * share;
                }
            }
            if (d > 0) AddBlend(blend, fr, (float)d);
        }

        static void AddBlend(Span<FrameBlend> blend, int subFrame, float lerp)
        {
            for (int i = 0; i < blend.Length; i++)
                if (blend[i].Lerp <= 0 || blend[i].SubFrame == subFrame)
                {
                    blend[i].SubFrame = subFrame;
                    blend[i].Lerp += lerp;
                    return;
                }
        }
    }

    // SV_GetEntityLocalTagMatrix / Mod_Alias_GetTagMatrix: a tag relative to its entity, in the
    // entity's current animation. Skeleton objects (.skeletonindex) are not consulted: the server
    // builds none (skel_build is not ported).
    private int EntityLocalTagMatrix(int edict, int tag, out BoneMatrix matrix)
    {
        matrix = BoneMatrix.Identity;
        if (tag < 0 || TagModelOf(edict) is not { Scenes: not null } model) return 0;
        Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
        FrameBlends(edict, model, blend);
        if (model.NumBones > 0)
        {
            if (tag >= model.NumBones) return 4;
            BoneMatrix blended = BoneMatrix.Zero;
            for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
            {
                BoneMatrix bone = model.BonePose(blend[i].SubFrame, tag);
                for (int parent = model.BoneParents[tag]; parent >= 0; parent = model.BoneParents[parent])
                    bone = BoneMatrix.Concat(model.BonePose(blend[i].SubFrame, parent), bone);
                blended = blended.Accumulate(bone, blend[i].Lerp);
            }
            matrix = blended;
        }
        else if (model.NumTags > 0)
        {
            if (tag >= model.NumTags) return 4;
            BoneMatrix blended = BoneMatrix.Zero;
            for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
                blended = blended.Accumulate(model.TagPose(blend[i].SubFrame, tag), blend[i].Lerp);
            matrix = blended;
        }
        return 0;
    }

    // Matrix4x4_CreateFromQuakeEntity, by way of the collision library's port of it.
    private static BoneMatrix FromQuakeEntity(QcVector origin, QcVector angles, float scale)
    {
        EntityMatrix m = EntityMatrix.FromQuakeEntity(SvWorld.V(origin), SvWorld.V(angles));
        return new BoneMatrix(new Vector3(m.M00, m.M10, m.M20) * scale, new Vector3(m.M01, m.M11, m.M21) * scale,
            new Vector3(m.M02, m.M12, m.M22) * scale, new Vector3(m.M03, m.M13, m.M23));
    }

    // SV_GetEntityMatrix: the entity's own placement, or - for the entity a view model hangs on -
    // its eye position and view angles. An alias model's pitch is negated (SV_GetPitchSign).
    private BoneMatrix EntityMatrixOf(int edict, bool viewMatrix)
    {
        float scale = Fl(edict, F.Scale);
        if (scale == 0) scale = 1.0f;
        QcVector origin = Vec(edict, F.Origin);
        if (viewMatrix)
        {
            origin.Z += Vec(edict, F.ViewOfs).Z;
            // "scale * cl_viewmodel_scale.value": the cvar is the client's, default 1.
            return FromQuakeEntity(origin, Vec(edict, F.VAngle), scale);
        }
        QcVector angles = Vec(edict, F.Angles);
        int modelIndex = QcVm.FloatToInt(Fl(edict, F.ModelIndex));
        bool hasModel = modelIndex > 0 && modelIndex < ModelCount;
        // "model ? model->type == mod_alias : (pflags & PFLAGS_FULLDYNAMIC)"
        bool negatePitch = hasModel ? !ModelIsBrush(modelIndex) && TagModelOf(edict) is { Kind: LegacyModelKind.Alias }
            : ((int)Fl(edict, F.PFlags) & 128) != 0;
        if (negatePitch) angles.X = -angles.X;
        return FromQuakeEntity(origin, angles, scale);
    }

    /// <summary>
    /// SV_GetTagMatrix: where a tag of an entity is in the world, following the chain of
    /// .tag_entity / .tag_index attachments up to an unattached entity. Returns the C's code: 0 ok,
    /// 1 world entity, 2 free entity, 3 no model, 4 no such tag, 5 attachment loop.
    /// "warnings and errors return identical matrix".
    /// </summary>
    internal int TagMatrix(int ent, int tagIndex, out BoneMatrix matrix)
    {
        matrix = BoneMatrix.Identity;
        if (ent == 0) return 1;
        if (!IsLive(ent)) return 2;
        int modelIndex = QcVm.FloatToInt(Fl(ent, F.ModelIndex));
        if (modelIndex <= 0 || modelIndex >= ModelCount) return 3;

        BoneMatrix result = BoneMatrix.Identity;
        for (int attachLoop = 0; ; attachLoop++)
        {
            if (attachLoop >= 256) return 5;   // prevent runaway looping
            // apply transformation by child's tagindex on parent entity and then by parent entity itself
            int code = EntityLocalTagMatrix(ent, tagIndex - 1, out BoneMatrix attach);
            if (code != 0 && attachLoop == 0) return code;
            result = BoneMatrix.Concat(EntityMatrixOf(ent, false), BoneMatrix.Concat(attach, result));
            // next iteration we process the parent entity
            int parent = Int(ent, F.TagEntity);
            if (parent == 0) break;
            tagIndex = QcVm.FloatToInt(Fl(ent, F.TagIndex));
            // PRVM_EDICT_NUM would fault on an entity number outside the array; the chain ends instead.
            if (!IsLive(parent)) break;
            ent = parent;
        }

        // RENDER_VIEWMODEL magic
        int viewer = Int(ent, F.ViewModelForClient);
        if (viewer != 0 && IsLive(viewer)) result = BoneMatrix.Concat(EntityMatrixOf(viewer, true), result);
        matrix = result;
        return 0;
    }

    // #452 vector(entity ent, float tagindex) gettaginfo
    private void GetTagInfo(QcVm vm)
    {
        Parms(2, 2, "VM_SV_gettaginfo");
        int e = vm.ArgEdict(0), tagIndex = QcVm.FloatToInt(vm.ArgFloat(1));
        int code = TagMatrix(e, tagIndex, out BoneMatrix matrix);
        vm.GlobalVector(G.VForward) = SvWorld.Q(matrix.Fwd);
        vm.GlobalVector(G.VRight) = SvWorld.Q(-matrix.Left);
        vm.GlobalVector(G.VUp) = SvWorld.Q(matrix.Up);
        vm.ReturnVector(SvWorld.Q(matrix.Origin));

        // SV_GetExtendedTagInfo: the tag's parent, name and transform relative to that parent.
        int parentIndex = 0;
        string? tagName = null;
        BoneMatrix local = BoneMatrix.Identity;
        if (tagIndex >= 0 && IsLive(e) && e != 0 && TagModelOf(e) is { Scenes: not null } model)
        {
            Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
            FrameBlends(e, model, blend);
            int tag = tagIndex - 1;
            if (model.NumBones > 0)
            {
                if ((uint)tag < (uint)model.NumBones)
                {
                    parentIndex = model.BoneParents[tag] + 1;
                    tagName = model.BoneNames[tag];
                    local = BoneMatrix.Zero;
                    for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
                        local = local.Accumulate(model.BonePose(blend[i].SubFrame, tag), blend[i].Lerp);
                }
            }
            else if (model.NumTags > 0 && (uint)tag < (uint)model.NumTags)
            {
                // "*parentindex = -1", then one is added on success.
                tagName = model.TagNames[tag];
                local = BoneMatrix.Zero;
                for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
                    local = local.Accumulate(model.TagPose(blend[i].SubFrame, tag), blend[i].Lerp);
            }
        }
        if (G.GetTagInfoParent >= 0) vm.GlobalFloat(G.GetTagInfoParent) = parentIndex;
        if (G.GetTagInfoName >= 0) vm.GlobalInt(G.GetTagInfoName) = tagName is null ? 0 : vm.TempString(tagName);
        if (G.GetTagInfoForward >= 0) vm.GlobalVector(G.GetTagInfoForward) = SvWorld.Q(local.Fwd);
        if (G.GetTagInfoRight >= 0) vm.GlobalVector(G.GetTagInfoRight) = SvWorld.Q(-local.Left);
        if (G.GetTagInfoUp >= 0) vm.GlobalVector(G.GetTagInfoUp) = SvWorld.Q(local.Up);
        if (G.GetTagInfoOffset >= 0) vm.GlobalVector(G.GetTagInfoOffset) = SvWorld.Q(local.Origin);

        switch (code)
        {
            case 1: Warning("gettagindex: can't affect world entity\n"); break;
            case 2: Warning("gettagindex: can't affect free entity\n"); break;
        }
    }
}
