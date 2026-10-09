// Port of the parts of Base/darkplaces/model_shared.h model_t that the client program can ask about,
// and of how each loader fills them: model_alias.c Mod_IDP3_Load (MD3), Mod_INTERQUAKEMODEL_Load
// (IQM), Mod_DARKPLACESMODEL_Load (DPM), Mod_IDP0_Load (MDL) and Mod_Alias_CalculateBoundingBox;
// model_sprite.c Mod_IDSP_Load (the bounds); model_shared.c Mod_LoadModel (which loader, and the
// .framegroups file applied on top: Mod_FrameGroupify).
using System.Numerics;
using VortexArena.Common.Math;
using VortexArena.Formats;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Dpm;
using VortexArena.Formats.Iqm;
using VortexArena.Formats.Md3;
using VortexArena.Formats.Mdl;
using VortexArena.Formats.Sidecars;
using VortexArena.Formats.Sprites;

namespace VortexArena.Legacy.Csqc;

/// <summary>model_t.type.</summary>
public enum LegacyModelKind { Null, Alias, Brush, Sprite }

/// <summary>animscene_t: one entry of what QuakeC calls a model's frames - a named run of poses
/// played at a rate. (A "frame" number in <c>.frame</c> indexes these, not the poses.)</summary>
public readonly record struct LegacyAnimScene(string Name, int FirstFrame, int FrameCount, float FrameRate, bool Loop);

/// <summary>
/// What is known about one model without drawing it: its box, its bones or tags and where each is in
/// every pose, and its animation scenes. Immutable once loaded.
/// </summary>
public sealed class LegacyModel
{
    public string Name { get; init; } = "";
    public LegacyModelKind Kind { get; init; }
    /// <summary>The file format: "md3", "iqm", "dpm", "mdl", "sprite", "bsp", or "unknown" for a
    /// file that exists but that nothing here can read.</summary>
    public string Format { get; init; } = "unknown";

    /// <summary>model_t.normalmins / normalmaxs: the box of the unrotated model, which is what setmodel assigns.</summary>
    public Vector3 NormalMins { get; init; }
    public Vector3 NormalMaxs { get; init; }

    /// <summary>data_bones: names and parents (-1 for a root; always less than the bone's own index).</summary>
    public string[] BoneNames { get; init; } = Array.Empty<string>();
    public int[] BoneParents { get; init; } = Array.Empty<int>();
    /// <summary>data_tags: MD3 attachment points. A model has bones or tags, never both.</summary>
    public string[] TagNames { get; init; } = Array.Empty<string>();

    /// <summary>num_poses: how many poses <see cref="BonePose"/> and <see cref="TagPose"/> index.</summary>
    public int NumPoses { get; init; }

    /// <summary>
    /// model_t.effects: EF_* bits the model itself asks for, which DarkPlaces ORs into the effects of every
    /// entity that shows it (cl_main.c CL_UpdateNetworkEntity, csprogs.c CSQC_AddRenderEdict). Only a Quake
    /// <c>.mdl</c> has any: its header flags, the low byte moved to the top (MF_ROCKET becomes EF_ROCKET: a
    /// trail; MF_ROTATE becomes EF_ROTATE: a spinning item). See <see cref="VortexArena.Formats.Mdl.MdlFlags"/>.
    /// </summary>
    public uint Effects { get; init; }

    /// <summary>
    /// model_t.numskins as the file gives it: what an entity's <c>.skin</c> is checked against ("if
    /// (skinnum >= numskins) skinnum = 0"). A skin group counts once. 0 for a format whose skins are
    /// <c>.skin</c> files (not counted here). DarkPlaces adds one for every further
    /// <c>&lt;model&gt;_&lt;N&gt;</c> picture found beside a <c>.mdl</c>; those are the drawing side's to find.
    /// </summary>
    public int SkinCount { get; init; }

    /// <summary>
    /// model_t.yawmins / yawmaxs and rotatedmins / rotatedmaxs, as radii: the largest horizontal distance and
    /// the largest distance of any vertex from the origin. 0 where a loader does not compute them.
    /// </summary>
    public float YawRadius { get; init; }
    public float Radius { get; init; }
    /// <summary>animscenes, or null for a model that has none (a brush model). numframes is its length.</summary>
    public LegacyAnimScene[]? Scenes { get; init; }
    public int NumFrames => Scenes?.Length ?? 0;
    public int NumBones => BoneNames.Length;
    public int NumTags => TagNames.Length;

    // Bone poses relative to the parent bone, [pose * NumBones + bone]: either origin and quaternion
    // (seven floats each, as DarkPlaces' data_poses7s keeps them) or whole matrices.
    internal float[]? Pose7 { get; init; }
    internal BoneMatrix[]? PoseMatrices { get; init; }
    // Tag transforms in model space, [pose * NumTags + tag].
    internal BoneMatrix[]? TagMatrices { get; init; }

    /// <summary>Matrix4x4_FromBonePose7s of data_poses7s: a bone relative to its parent in one pose.
    /// Out-of-range indices answer identity.</summary>
    public BoneMatrix BonePose(int pose, int bone)
    {
        if ((uint)bone >= (uint)NumBones || (uint)pose >= (uint)NumPoses) return BoneMatrix.Identity;
        int i = pose * NumBones + bone;
        if (PoseMatrices is not null) return PoseMatrices[i];
        if (Pose7 is null) return BoneMatrix.Identity;
        ReadOnlySpan<float> p = Pose7.AsSpan(i * 7, 7);
        return BoneMatrix.FromTRS(new Vector3(p[0], p[1], p[2]), new Quaternion(p[3], p[4], p[5], p[6]), Vector3.One);
    }

    /// <summary>data_tags[pose * num_tags + tag].matrixgl.</summary>
    public BoneMatrix TagPose(int pose, int tag)
    {
        if (TagMatrices is null || (uint)tag >= (uint)NumTags || (uint)pose >= (uint)NumPoses) return BoneMatrix.Identity;
        return TagMatrices[pose * NumTags + tag];
    }

    /// <summary>Mod_Alias_GetTagIndexForName: bones first, then tags, names compared without case. 1-based; 0 if none.</summary>
    public int TagIndexForName(string name)
    {
        for (int i = 0; i < BoneNames.Length; i++)
            if (string.Equals(BoneNames[i], name, StringComparison.OrdinalIgnoreCase)) return i + 1;
        for (int i = 0; i < TagNames.Length; i++)
            if (string.Equals(TagNames[i], name, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }
}

/// <summary>Reads a model file into a <see cref="LegacyModel"/>. Every failure is a null, never an exception.</summary>
public static class LegacyModelLoader
{
    /// <summary>Files larger than this are not models anybody ships; they are refused unread.</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;
    /// <summary>CL_MAX... there is no limit in DarkPlaces; this one bounds what a hostile .framegroups file can allocate.</summary>
    public const int MaxScenes = 65536;

    /// <summary>
    /// Mod_LoadModel for a file's bytes: the loader is chosen by the magic number at the start,
    /// as in the C's loader table, and a <c>.framegroups</c> file next to it replaces the scenes.
    /// </summary>
    /// <param name="frameGroups">The text of "<paramref name="name"/>.framegroups", or null if there is none.</param>
    public static LegacyModel? Load(string name, ReadOnlySpan<byte> data, string? frameGroups)
    {
        if (data.Length < 8 || data.Length > MaxFileBytes) return null;
        try
        {
            LegacyModel? model;
            if (data.StartsWith("IDP3"u8)) model = FromMd3(name, Md3Reader.Read(data));
            else if (data.StartsWith("INTERQUAKEMODEL\0"u8)) model = FromIqm(name, IqmReader.Read(data));
            else if (data.StartsWith("DARKPLACESMODEL\0"u8)) model = FromDpm(name, DpmReader.Read(data));
            else if (data.StartsWith("IDPO"u8)) model = FromMdl(name, MdlReader.Read(data));
            else if (data.StartsWith("IDSP"u8) || data.StartsWith("IDS2"u8)) model = FromSprite(name, SpriteReader.Read(data));
            else if (data.StartsWith("IBSP"u8)) model = FromBsp(name, BspReader.Read(data));
            // Quake's ammunition and health boxes are small maps ("maps/b_bh25.bsp")
            else if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) && Q1BspReader.IsQ1Format(data)) model = FromQ1Bsp(name, Q1BspReader.Read(data));
            // A format DarkPlaces loads and this does not (ZYM, PSK, OBJ, MD2): it exists, its size is unknown.
            else model = new LegacyModel { Name = name, Kind = LegacyModelKind.Alias };
            return model is not null && frameGroups is not null ? FrameGroupify(model, frameGroups) : model;
        }
        catch (Exception e) when (e is AssetParseException or ArgumentException or IndexOutOfRangeException or InvalidDataException
            or OverflowException or EndOfStreamException or FormatException or NullReferenceException or InvalidOperationException)
        {
            // The readers reject what they detect; whatever else a damaged file trips is the same answer.
            return null;
        }
    }

    /// <summary>A map submodel ("*N"): Mod_Q3BSP_Load gives each its own model with the bounds from the models lump.</summary>
    public static LegacyModel Submodel(string name, Vector3 mins, Vector3 maxs) =>
        new() { Name = name, Kind = LegacyModelKind.Brush, Format = "bsp", NormalMins = mins, NormalMaxs = maxs };

    // Mod_FrameGroupify: "firstframe framecount [fps] [loop] [name]" per line replaces the scenes.
    private static LegacyModel FrameGroupify(LegacyModel model, string text)
    {
        if (model.Kind != LegacyModelKind.Alias || model.NumPoses <= 0) return model;
        List<FrameGroup> groups = FrameGroups.Parse(text);
        // "no scene found in framegroups file, aborting"
        if (groups.Count == 0) return model;
        int count = Math.Min(groups.Count, MaxScenes);
        LegacyAnimScene[] scenes = new LegacyAnimScene[count];
        for (int i = 0; i < count; i++)
        {
            FrameGroup g = groups[i];
            int first = Math.Clamp(g.FirstFrame, 0, model.NumPoses - 1);
            scenes[i] = new LegacyAnimScene(g.Name.Length != 0 ? g.Name : $"groupified_{i}_anim", first,
                Math.Clamp(g.FrameCount, 1, model.NumPoses - first), MathF.Max(1, g.Fps), g.Loop);
        }
        return new LegacyModel
        {
            Name = model.Name, Kind = model.Kind, Format = model.Format, NormalMins = model.NormalMins, NormalMaxs = model.NormalMaxs,
            BoneNames = model.BoneNames, BoneParents = model.BoneParents, TagNames = model.TagNames, NumPoses = model.NumPoses,
            Pose7 = model.Pose7, PoseMatrices = model.PoseMatrices, TagMatrices = model.TagMatrices, Scenes = scenes,
            Effects = model.Effects, SkinCount = model.SkinCount, YawRadius = model.YawRadius, Radius = model.Radius,
        };
    }

    // One scene per frame: "animscenes[i].firstframe = i; framecount = 1; framerate = 10; loop = true".
    private static LegacyAnimScene[] ScenePerFrame(int count, Func<int, string> name)
    {
        LegacyAnimScene[] scenes = new LegacyAnimScene[count];
        for (int i = 0; i < count; i++) scenes[i] = new LegacyAnimScene(name(i), i, 1, 10, true);
        return scenes;
    }

    private struct Box
    {
        public Vector3 Mins, Maxs;
        public bool Any;
        public void Add(Vector3 v)
        {
            if (!Any) { Mins = Maxs = v; Any = true; return; }
            Mins = Vector3.Min(Mins, v);
            Maxs = Vector3.Max(Maxs, v);
        }
    }

    // Mod_IDP3_Load. The box is Mod_Alias_CalculateBoundingBox: every vertex of every frame.
    private static LegacyModel FromMd3(string name, Md3Data md3)
    {
        Box box = default;
        foreach (Md3Surface surface in md3.Surfaces)
            foreach (Md3Vertex[] frame in surface.FrameVertices)
                foreach (Md3Vertex vertex in frame)
                    box.Add(vertex.Position);

        int frames = md3.Frames.Length;
        string[] tags = md3.Tags.Select(t => t.Name).ToArray();
        BoneMatrix[] matrices = new BoneMatrix[frames * tags.Length];
        for (int t = 0; t < tags.Length; t++)
        {
            Md3TagTransform[] transforms = md3.Tags[t].Transforms;
            for (int f = 0; f < frames; f++)
                // matrixgl: the three axes, then the origin.
                matrices[f * tags.Length + t] = f < transforms.Length
                    ? new BoneMatrix(transforms[f].AxisX, transforms[f].AxisY, transforms[f].AxisZ, transforms[f].Origin)
                    : BoneMatrix.Identity;
        }
        return new LegacyModel
        {
            Name = name, Kind = LegacyModelKind.Alias, Format = "md3", NormalMins = box.Mins, NormalMaxs = box.Maxs,
            TagNames = tags, TagMatrices = matrices, NumPoses = frames,
            Scenes = ScenePerFrame(frames, i => md3.Frames[i].Name),
        };
    }

    // Mod_INTERQUAKEMODEL_Load.
    private static LegacyModel FromIqm(string name, IqmData iqm)
    {
        int bones = iqm.Joints.Length;
        // "loadmodel->num_poses = max(header.num_frames, 1)": a model with no animation has one
        // pose, its joints' rest transforms.
        int poses = Math.Max(iqm.Frames.Length, 1);
        float[] pose7 = new float[checked(poses * bones * 7)];
        for (int p = 0; p < poses; p++)
            for (int b = 0; b < bones; b++)
            {
                IqmBonePose pose = iqm.Frames.Length > 0 && b < iqm.Frames[p].Bones.Length ? iqm.Frames[p].Bones[b] : iqm.Joints[b].Local;
                Span<float> o = pose7.AsSpan((p * bones + b) * 7, 7);
                o[0] = pose.Translate.X; o[1] = pose.Translate.Y; o[2] = pose.Translate.Z;
                // DarkPlaces keeps origin and a normalised quaternion per pose and drops the scale.
                Quaternion q = pose.Rotate.LengthSquared() > 0 ? Quaternion.Normalize(pose.Rotate) : Quaternion.Identity;
                o[3] = q.X; o[4] = q.Y; o[5] = q.Z; o[6] = q.W;
            }

        LegacyAnimScene[] scenes;
        if (iqm.Anims.Length > 0)
        {
            scenes = new LegacyAnimScene[iqm.Anims.Length];
            for (int i = 0; i < scenes.Length; i++)
            {
                IqmAnim a = iqm.Anims[i];
                scenes[i] = new LegacyAnimScene(a.Name, a.FirstFrame, a.FrameCount, a.FrameRate, a.Loop);
            }
        }
        else scenes = new[] { new LegacyAnimScene("static", 0, 1, 10, true) };

        // "load bounding box data": the union of the per-frame boxes the file carries. A file
        // without them gets Mod_Alias_CalculateBoundingBox in DarkPlaces - every vertex in every
        // pose - where this takes the vertices as they stand in the file (the rest pose).
        Box box = default;
        if (iqm.Bounds is { Length: > 0 } bounds)
            foreach (IqmBounds b in bounds) { box.Add(b.Mins); box.Add(b.Maxs); }
        else
            foreach (Vector3 v in iqm.Positions) box.Add(v);

        return new LegacyModel
        {
            Name = name, Kind = LegacyModelKind.Alias, Format = "iqm", NormalMins = box.Mins, NormalMaxs = box.Maxs,
            BoneNames = iqm.Joints.Select(j => j.Name).ToArray(), BoneParents = SafeParents(iqm.Joints.Select(j => j.Parent)),
            Pose7 = pose7, NumPoses = poses, Scenes = scenes,
        };
    }

    // Mod_DARKPLACESMODEL_Load.
    private static LegacyModel FromDpm(string name, DpmData dpm)
    {
        int bones = dpm.Bones.Length, frames = dpm.Frames.Length;
        int[] parents = SafeParents(dpm.Bones.Select(b => b.Parent));
        BoneMatrix[] matrices = new BoneMatrix[checked(frames * bones)];
        for (int f = 0; f < frames; f++)
            for (int b = 0; b < bones; b++)
            {
                if (b >= dpm.Frames[f].BonePoses.Length) { matrices[f * bones + b] = BoneMatrix.Identity; continue; }
                DpmBonePose p = dpm.Frames[f].BonePoses[b];
                // float matrix[3][4] row-major (Matrix4x4_FromArray12FloatD3D): the reader's Right,
                // Up and Forward are its first, second and third columns. "normalize rotation matrix".
                matrices[f * bones + b] = new BoneMatrix(p.Right, p.Up, p.Forward, p.Origin).Normalize3();
            }

        // Mod_Alias_CalculateBoundingBox ("we blow this away later"): every vertex, skinned, in every frame.
        Box box = default;
        BoneMatrix[] absolute = new BoneMatrix[bones];
        for (int f = 0; f < frames; f++)
        {
            for (int b = 0; b < bones; b++)
                absolute[b] = parents[b] >= 0 ? BoneMatrix.Concat(absolute[parents[b]], matrices[f * bones + b]) : matrices[f * bones + b];
            foreach (DpmMesh mesh in dpm.Meshes)
                foreach (DpmVertex vertex in mesh.Vertices)
                {
                    Vector3 position = Vector3.Zero;
                    foreach (DpmBoneWeight w in vertex.Weights)
                        // The stored origin is already multiplied by the influence.
                        if ((uint)w.BoneNum < (uint)bones) position += absolute[w.BoneNum].Rotate(w.Origin) + absolute[w.BoneNum].Origin * w.Influence;
                    box.Add(position);
                }
        }
        if (!box.Any) { box.Add(dpm.Mins); box.Add(dpm.Maxs); }

        return new LegacyModel
        {
            Name = name, Kind = LegacyModelKind.Alias, Format = "dpm", NormalMins = box.Mins, NormalMaxs = box.Maxs,
            BoneNames = dpm.Bones.Select(b => b.Name).ToArray(), BoneParents = parents, PoseMatrices = matrices, NumPoses = frames,
            Scenes = ScenePerFrame(frames, i => dpm.Frames[i].Name),
        };
    }

    // Mod_IDP0_Load. The reader has already done the C's work: the box is Mod_Alias_CalculateBoundingBox over
    // the vertices a triangle uses, and a frame group (a torch's flame) is ONE frame number whose poses the
    // engine plays by itself at the group's rate - so .frame indexes Scenes, not the poses.
    private static LegacyModel FromMdl(string name, MdlData mdl)
    {
        LegacyAnimScene[] scenes = new LegacyAnimScene[mdl.Scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
        {
            MdlScene s = mdl.Scenes[i];
            scenes[i] = new LegacyAnimScene(s.Name, s.First, s.Count, s.FrameRate, s.Loop);
        }
        return new LegacyModel
        {
            Name = name, Kind = LegacyModelKind.Alias, Format = "mdl", NormalMins = mdl.Mins, NormalMaxs = mdl.Maxs,
            NumPoses = mdl.Frames.Length, Scenes = scenes,
            Effects = mdl.Effects, SkinCount = mdl.SkinScenes.Length, YawRadius = mdl.YawRadius, Radius = mdl.Radius,
        };
    }

    // Mod_IDSP_Load / Mod_IDS2_Load: a cube of the largest corner distance of any frame, and one frame number
    // per slot of the file - a group of pictures is one, played at the group's rate.
    private static LegacyModel FromSprite(string name, SpriteData sprite)
    {
        LegacyAnimScene[] scenes = new LegacyAnimScene[sprite.Scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
        {
            SpriteScene s = sprite.Scenes[i];
            scenes[i] = new LegacyAnimScene(s.Name, s.FirstFrame, s.FrameCount, s.FrameRate, true);
        }
        float radius = sprite.Radius;
        return new LegacyModel
        {
            Name = name, Kind = LegacyModelKind.Sprite, Format = "sprite",
            NormalMins = new Vector3(-radius), NormalMaxs = new Vector3(radius),
            NumPoses = sprite.Frames.Length, Scenes = scenes, YawRadius = radius, Radius = radius,
        };
    }

    // A whole .bsp used as a model: its model 0.
    private static LegacyModel FromBsp(string name, BspData bsp) => new()
    {
        Name = name, Kind = LegacyModelKind.Brush, Format = "bsp",
        NormalMins = bsp.Models.Length > 0 ? bsp.Models[0].Mins : default,
        NormalMaxs = bsp.Models.Length > 0 ? bsp.Models[0].Maxs : default,
    };

    private static LegacyModel FromQ1Bsp(string name, Q1BspData bsp) => new()
    {
        Name = name, Kind = LegacyModelKind.Brush, Format = "q1bsp",
        NormalMins = bsp.Models[0].Mins, NormalMaxs = bsp.Models[0].Maxs,
    };

    // "always less than this joint's own index" is the format's promise; a file that breaks it would
    // make every walk up the hierarchy a loop. Such a parent is cut off.
    private static int[] SafeParents(IEnumerable<int> parents)
    {
        int[] result = parents.ToArray();
        for (int i = 0; i < result.Length; i++)
            if (result[i] >= i || result[i] < -1) result[i] = -1;
        return result;
    }
}
