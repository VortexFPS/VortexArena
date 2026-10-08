// Port of Base/darkplaces/clvm_cmds.c CL_GetTagIndex, CL_GetExtendedTagInfo, CL_GetPitchSign,
// CL_GetEntityMatrix, CL_GetEntityLocalTagMatrix, CL_GetTagMatrix and VM_CL_skel_create ... VM_CL_frameduration;
// prvm_cmds.c VM_GenerateFrameGroupBlend, VM_FrameBlendFromFrameGroupBlend, VM_UpdateEdictSkeleton;
// model_alias.c Mod_Alias_GetTagMatrix and Mod_Alias_GetExtendedTagInfoForIndex.
using System.Numerics;
using VortexArena.Common.Math;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// <see cref="ILegacyModels"/> from the model files themselves: boxes, tags, bones, animation scenes
/// and skeleton objects, computed on the CPU from what the format readers parse. Nothing is drawn, so
/// a Godot presentation answers these same questions with this same object.
///
/// Models are parsed once and kept by name in a bounded cache. A model that is missing, too large or
/// malformed is "no such model" - remembered, so it is not parsed again - and no query throws.
/// </summary>
/// <remarks>
/// Not done, and answered as "none": the nine getsurface* queries (#434-#439, #486, #628, #629).
/// Stock Xonotic declares them and its client does not call them while playing.
/// Poses are kept in full float precision; DarkPlaces rounds each to seven 16-bit numbers.
/// </remarks>
public sealed class FormatLegacyModels : ILegacyModels
{
    /// <summary>Parsed models kept at once. A level names a few hundred; the least recently used go first.</summary>
    public const int MaxCachedModels = 512;
    /// <summary>prog->skeletons is MAX_EDICTS long.</summary>
    public const int MaxSkeletons = DpProtocol.MaxEdicts;
    private const int MaxFrameBlends = 8;        // MAX_FRAMEBLENDS
    private const int RfViewModel = 1, RfUseAxis = 16;

    private readonly VirtualFileSystem _files;
    private readonly BspLegacyWorld? _world;
    private readonly Dictionary<string, long>? _calls;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private long _useCounter;
    private CsqcHost? _host;
    private Skeleton?[] _skeletons = new Skeleton?[64];

    private sealed class CacheEntry
    {
        public LegacyModel? Model;
        public long LastUse;
    }

    private sealed class Skeleton
    {
        public required LegacyModel Model;
        public required BoneMatrix[] Relative;
    }

    // frameblend_t: a pose and its share.
    private struct FrameBlend { public int SubFrame; public float Lerp; }

    /// <param name="files">The game data models are read from.</param>
    /// <param name="world">The level, for the "*N" submodels; null if there is none.</param>
    /// <param name="calls">Where to count the calls received, by member name; null for no counting.</param>
    public FormatLegacyModels(VirtualFileSystem files, BspLegacyWorld? world = null, Dictionary<string, long>? calls = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _world = world;
        _calls = calls;
    }

    /// <summary>
    /// r_refdef.view.matrix as an origin and angles: where the view is, for entities drawn relative to
    /// it (RF_VIEWMODEL). Null, or a null answer, means "at the world origin, looking along +X".
    /// </summary>
    public Func<(QcVector Origin, QcVector Angles)>? View { get; set; }

    /// <summary>
    /// Called before each model file is read and parsed - the slow part of starting a level, several
    /// seconds for a stock one. It is where DarkPlaces' loaders call CL_KeepaliveMessage; an owner
    /// with a connection hooks its keepalive here.
    /// </summary>
    public Action? Working { get; set; }

    /// <summary>Models parsed since construction (cache misses that found a file).</summary>
    public int ModelsParsed { get; private set; }
    public int CachedModels => _cache.Count;
    public int LiveSkeletons { get; private set; }

    /// <summary>The program whose entities and skeletons these are. A new program starts with no skeletons.</summary>
    public void Attach(CsqcHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Array.Clear(_skeletons);
        LiveSkeletons = 0;
    }

    private void Count(string member)
    {
        if (_calls is not null) _calls[member] = _calls.GetValueOrDefault(member) + 1;
    }

    // ---- loading -----------------------------------------------------------------------------------

    /// <summary>Bytes of model files read, and the time reading and parsing them took, since this object was made.</summary>
    public long ModelBytesRead { get; private set; }
    public double ModelLoadSeconds { get; private set; }

    /// <summary>The model of that name, or null if it cannot be loaded. "*N" is a submodel of the level.</summary>
    public LegacyModel? Load(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_cache.TryGetValue(name, out CacheEntry? entry))
        {
            entry.LastUse = ++_useCounter;
            return entry.Model;
        }

        LegacyModel? model = null;
        // "null" is a model the engine makes up (Mod_LoadModel): always loaded, nothing in it.
        if (name == "null") model = new LegacyModel { Name = name, Kind = LegacyModelKind.Null, Format = "null" };
        else if (name[0] == '*')
        {
            if (_world is not null && _world.TryGetSubmodel(name, out Vector3 mins, out Vector3 maxs, out _))
                model = LegacyModelLoader.Submodel(name, mins, maxs);
        }
        else if (LegacyQcHost.IsSafePath(name) && _files.Exists(name))
        {
            try
            {
                Working?.Invoke();
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] data = _files.ReadBytes(name);
                string sidecar = name + ".framegroups";
                string? frameGroups = _files.Exists(sidecar) ? _files.ReadText(sidecar) : null;
                model = LegacyModelLoader.Load(name, data, frameGroups);
                ModelsParsed++;
                ModelBytesRead += data.Length;
                ModelLoadSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                model = null;
            }
        }

        if (_cache.Count >= MaxCachedModels) Evict();
        _cache[name] = new CacheEntry { Model = model, LastUse = ++_useCounter };
        return model;
    }

    // Drops the least recently used quarter. Skeleton objects keep their own reference to the model
    // they were made for, so evicting it here cannot pull it out from under one.
    private void Evict()
    {
        long[] uses = _cache.Values.Select(e => e.LastUse).ToArray();
        Array.Sort(uses);
        long threshold = uses[uses.Length / 4];
        foreach (string key in _cache.Where(kv => kv.Value.LastUse <= threshold).Select(kv => kv.Key).ToArray())
            _cache.Remove(key);
    }

    /// <summary>Forgets every parsed model (a new level: the submodels are different ones).</summary>
    public void ClearCache() => _cache.Clear();

    public bool TryGetBounds(string model, out QcVector mins, out QcVector maxs)
    {
        Count(nameof(TryGetBounds));
        mins = maxs = default;
        if (Load(model) is not { } m) return false;
        mins = Q(m.NormalMins);
        maxs = Q(m.NormalMaxs);
        return true;
    }

    // ---- tags --------------------------------------------------------------------------------------

    /// <summary>Mod_Alias_GetTagIndexForName. The skin does not matter to it (the C clamps it and goes on).</summary>
    public int TagIndex(string model, int skin, string tagName)
    {
        Count(nameof(TagIndex));
        return Load(model)?.TagIndexForName(tagName) ?? 0;
    }

    public int TagInfo(int edict, int tagIndex, out LegacyTagInfo info)
    {
        Count(nameof(TagInfo));
        int code = TagMatrix(edict, tagIndex, out BoneMatrix matrix);
        info = default;
        info.Origin = Q(matrix.Origin);
        info.Forward = Q(matrix.Fwd);
        info.Right = Q(-matrix.Left);
        info.Up = Q(matrix.Up);

        // CL_GetExtendedTagInfo: the tag's parent, name and transform relative to that parent.
        BoneMatrix local = BoneMatrix.Identity;
        if (_host is { } host && tagIndex >= 0 && (uint)edict < (uint)host.Vm.NumEdicts && LoadFor(host, edict) is { Scenes: not null } model)
        {
            Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
            FrameBlends(host, edict, model, blend);
            Skeleton? skeleton = EdictSkeleton(host, edict, model);
            int tag = tagIndex - 1;
            if (skeleton is not null)
            {
                if ((uint)tag < (uint)skeleton.Model.NumBones)
                {
                    info.Parent = skeleton.Model.BoneParents[tag] + 1;
                    info.Name = skeleton.Model.BoneNames[tag];
                    local = skeleton.Relative[tag];
                }
            }
            else if (model.NumBones > 0)
            {
                if ((uint)tag < (uint)model.NumBones)
                {
                    info.Parent = model.BoneParents[tag] + 1;
                    info.Name = model.BoneNames[tag];
                    local = BoneMatrix.Zero;
                    for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
                        local = local.Accumulate(model.BonePose(blend[i].SubFrame, tag), blend[i].Lerp);
                }
            }
            else if (model.NumTags > 0 && (uint)tag < (uint)model.NumTags)
            {
                // "*parentindex = -1", then one is added on success.
                info.Parent = 0;
                info.Name = model.TagNames[tag];
                local = BoneMatrix.Zero;
                for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
                    local = local.Accumulate(model.TagPose(blend[i].SubFrame, tag), blend[i].Lerp);
            }
        }
        info.LocalOffset = Q(local.Origin);
        info.LocalForward = Q(local.Fwd);
        info.LocalRight = Q(-local.Left);
        info.LocalUp = Q(local.Up);
        return code;
    }

    /// <summary>
    /// CL_GetTagMatrix: where a tag of an entity is in the world, following the chain of
    /// .tag_entity / .tag_index attachments up to an unattached entity. Returns the C's code:
    /// 0 ok, 1 world entity, 2 free entity, 3 no model, 4 no such tag, 5 attachment loop.
    /// "warnings and errors return identical matrix".
    /// </summary>
    public int TagMatrix(int edict, int tagIndex, out BoneMatrix matrix)
    {
        matrix = BoneMatrix.Identity;
        if (_host is not { } host) return 3;
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;
        if (edict == 0) return 1;
        if ((uint)edict >= (uint)vm.NumEdicts || vm.IsFree(edict)) return 2;
        if (LoadFor(host, edict) is null) return 3;

        BoneMatrix result = BoneMatrix.Identity;
        int attachLoop = 0;
        for (;;)
        {
            if (attachLoop >= 256) { matrix = BoneMatrix.Identity; return 5; }
            // apply transformation by child's tagindex on parent entity and then by parent entity itself
            int code = EntityLocalTagMatrix(host, edict, tagIndex - 1, out BoneMatrix attach);
            if (code != 0 && attachLoop == 0) return code;
            BoneMatrix entity = EntityPlacement(host, edict);
            result = BoneMatrix.Concat(entity, BoneMatrix.Concat(attach, result));
            // next iteration we process the parent entity
            int parent = vm.FieldInt(edict, f.TagEntity);
            if (parent == 0) break;
            tagIndex = QcVm.FloatToInt(vm.FieldFloat(edict, f.TagIndex));
            // PRVM_EDICT_NUM would fault on an entity number outside the array; the chain ends instead.
            if ((uint)parent >= (uint)vm.NumEdicts) break;
            edict = parent;
            attachLoop++;
        }

        // "RENDER_VIEWMODEL magic": the root of the chain is drawn relative to the view.
        if ((QcVm.FloatToInt(vm.FieldFloat(edict, f.RenderFlags)) & RfViewModel) != 0)
        {
            (QcVector origin, QcVector angles) = View?.Invoke() ?? default;
            result = BoneMatrix.Concat(FromQuakeEntity(V(origin), V(angles), 1), result);
        }
        matrix = result;
        return 0;
    }

    // CL_GetModelFromEdict, loaded.
    private LegacyModel? LoadFor(CsqcHost host, int edict) => host.ModelNameOf(edict) is { } name ? Load(name) : null;

    // CL_GetEntityLocalTagMatrix: a tag relative to its entity, in the entity's current animation.
    private int EntityLocalTagMatrix(CsqcHost host, int edict, int tag, out BoneMatrix matrix)
    {
        matrix = BoneMatrix.Identity;
        if (tag < 0 || LoadFor(host, edict) is not { Scenes: not null } model) return 0;
        Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
        FrameBlends(host, edict, model, blend);
        Skeleton? skeleton = EdictSkeleton(host, edict, model);

        // Mod_Alias_GetTagMatrix.
        if (skeleton is not null)
        {
            if (tag >= skeleton.Model.NumBones) return 4;
            matrix = skeleton.Relative[tag];
            // The parents are the entity's model's, as in the C; both have the same number of bones.
            for (int parent = model.BoneParents[tag]; parent >= 0; parent = model.BoneParents[parent])
                matrix = BoneMatrix.Concat(skeleton.Relative[parent], matrix);
        }
        else if (model.NumBones > 0)
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
        // "if(!mod_alias_supporttagscale.integer) Matrix4x4_Normalize3": the cvar defaults to 1, so
        // an MD3 tag's scale is kept.
        if (host.Services.CvarExists("mod_alias_supporttagscale") && host.Services.CvarFloat("mod_alias_supporttagscale") == 0)
            matrix = matrix.Normalize3();
        return 0;
    }

    // CL_GetEntityMatrix (viewmatrix false): the entity's own placement. An alias model's pitch is
    // negated (CL_GetPitchSign) - the oldest quirk in Quake, kept by every engine since.
    private BoneMatrix EntityPlacement(CsqcHost host, int edict)
    {
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;
        float scale = vm.FieldFloat(edict, f.Scale);
        if (scale == 0) scale = 1;
        Vector3 origin = V(vm.FieldVector(edict, f.Origin));
        if ((QcVm.FloatToInt(vm.FieldFloat(edict, f.RenderFlags)) & RfUseAxis) != 0)
        {
            CsqcGlobalOffsets g = host.Globals;
            Vector3 forward = g.VForward >= 0 ? V(vm.GlobalVector(g.VForward)) : Vector3.UnitX;
            Vector3 right = g.VRight >= 0 ? V(vm.GlobalVector(g.VRight)) : -Vector3.UnitY;
            Vector3 up = g.VUp >= 0 ? V(vm.GlobalVector(g.VUp)) : Vector3.UnitZ;
            return new BoneMatrix(forward * scale, right * -scale, up * scale, origin);
        }
        Vector3 angles = V(vm.FieldVector(edict, f.Angles));
        if (LoadFor(host, edict) is { Kind: LegacyModelKind.Alias }) angles.X = -angles.X;
        return FromQuakeEntity(origin, angles, scale);
    }

    // Matrix4x4_CreateFromQuakeEntity, by way of the collision library's port of it.
    private static BoneMatrix FromQuakeEntity(Vector3 origin, Vector3 angles, float scale)
    {
        EntityMatrix m = EntityMatrix.FromQuakeEntity(origin, angles);
        return new BoneMatrix(new Vector3(m.M00, m.M10, m.M20) * scale, new Vector3(m.M01, m.M11, m.M21) * scale,
            new Vector3(m.M02, m.M12, m.M22) * scale, new Vector3(m.M03, m.M13, m.M23));
    }

    // ---- animation ---------------------------------------------------------------------------------

    // VM_GenerateFrameGroupBlend + VM_FrameBlendFromFrameGroupBlend: the entity's .frame .. .frame4,
    // their start times and .lerpfrac .. .lerpfrac4, turned into up to eight weighted poses.
    private static void FrameBlends(CsqcHost host, int edict, LegacyModel model, Span<FrameBlend> blend)
    {
        blend.Clear();
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;
        Span<int> frame = stackalloc int[4];
        Span<float> start = stackalloc float[4], lerp = stackalloc float[4];
        frame[0] = QcVm.FloatToInt(vm.FieldFloat(edict, f.Frame));
        frame[1] = QcVm.FloatToInt(vm.FieldFloat(edict, f.Frame2));
        frame[2] = QcVm.FloatToInt(vm.FieldFloat(edict, f.Frame3));
        frame[3] = QcVm.FloatToInt(vm.FieldFloat(edict, f.Frame4));
        start[0] = vm.FieldFloat(edict, f.Frame1Time);
        start[1] = vm.FieldFloat(edict, f.Frame2Time);
        start[2] = vm.FieldFloat(edict, f.Frame3Time);
        start[3] = vm.FieldFloat(edict, f.Frame4Time);
        lerp[1] = vm.FieldFloat(edict, f.LerpFrac);
        lerp[2] = vm.FieldFloat(edict, f.LerpFrac3);
        lerp[3] = vm.FieldFloat(edict, f.LerpFrac4);
        // "assume that the (missing) lerpfrac1 is whatever remains after lerpfrac2+lerpfrac3+lerpfrac4 are summed"
        lerp[0] = 1 - lerp[1] - lerp[2] - lerp[3];

        string lerpCvar = model.Kind == LegacyModelKind.Sprite ? "r_lerpsprites" : "r_lerpmodels";
        bool noLerp = host.Services.CvarExists(lerpCvar) ? host.Services.CvarFloat(lerpCvar) == 0 : model.Kind == LegacyModelKind.Sprite;
        bool first = true;
        double time = host.State.Time;
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
                    // "this code path is only used on .zym models and torches" - and on every model
                    // with a .framegroups file, which is all of Xonotic's animated ones.
                    double subLerp = scene.FrameRate * (time - start[k]);
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
                        // C's %: the sign of the dividend, so a negative time stays negative and is clamped below.
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

    // VM_UpdateEdictSkeleton: the skeleton object an entity names in .skeletonindex, if it fits the
    // entity's model ("custom skeleton controlled by the game"); otherwise the model's own animation.
    private Skeleton? EdictSkeleton(CsqcHost host, int edict, LegacyModel model)
    {
        if (model.NumBones == 0) return null;
        int index = QcVm.FloatToInt(host.Vm.FieldFloat(edict, host.Fields.SkeletonIndex)) - 1;
        return (uint)index < (uint)_skeletons.Length && _skeletons[index] is { } skeleton && skeleton.Model.NumBones == model.NumBones ? skeleton : null;
    }

    // ---- what a renderer needs of an entity's pose ----------------------------------------------------

    /// <summary>
    /// The entity's skeleton as Mod_Skeletal_BuildTransforms would pose it: for each bone its
    /// transform in model space, from the skeleton object the entity names in .skeletonindex or,
    /// without one, from its .frame/.lerpfrac animation blend. Returns the number of bones written
    /// (0 if the entity is free, has no skeletal model, or <paramref name="absolute"/> is too small).
    /// </summary>
    public int RenderBones(int edict, Span<BoneMatrix> absolute)
    {
        if (_host is not { } host || edict <= 0 || (uint)edict >= (uint)host.Vm.NumEdicts || host.Vm.IsFree(edict)) return 0;
        if (LoadFor(host, edict) is not { NumBones: > 0 } model || model.NumBones > absolute.Length) return 0;
        Skeleton? skeleton = EdictSkeleton(host, edict, model);
        Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
        if (skeleton is null) FrameBlends(host, edict, model, blend);
        for (int bone = 0; bone < model.NumBones; bone++)
        {
            BoneMatrix relative;
            if (skeleton is not null) relative = skeleton.Relative[bone];
            else
            {
                relative = BoneMatrix.Zero;
                for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
                    relative = relative.Accumulate(model.BonePose(blend[i].SubFrame, bone), blend[i].Lerp);
            }
            int parent = model.BoneParents[bone];
            // Parents precede their children in every skeletal format read here; one that does not is posed unparented.
            absolute[bone] = parent >= 0 && parent < bone ? BoneMatrix.Concat(absolute[parent], relative) : relative;
        }
        return model.NumBones;
    }

    /// <summary>
    /// The two strongest poses of the entity's animation blend, for a vertex-animated model (MD3, MDL):
    /// pose <paramref name="frameA"/> blended towards <paramref name="frameB"/> by <paramref name="lerp"/>.
    /// False if the entity is free or has no animated model.
    /// </summary>
    public bool RenderFrames(int edict, out int frameA, out int frameB, out float lerp)
    {
        frameA = frameB = 0;
        lerp = 0;
        if (_host is not { } host || edict <= 0 || (uint)edict >= (uint)host.Vm.NumEdicts || host.Vm.IsFree(edict)) return false;
        if (LoadFor(host, edict) is not { Scenes: not null } model) return false;
        Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
        FrameBlends(host, edict, model, blend);
        int best = -1, second = -1;
        for (int i = 0; i < MaxFrameBlends && blend[i].Lerp > 0; i++)
        {
            if (best < 0 || blend[i].Lerp > blend[best].Lerp) { second = best; best = i; }
            else if (second < 0 || blend[i].Lerp > blend[second].Lerp) second = i;
        }
        if (best < 0) return false;
        frameA = blend[best].SubFrame;
        frameB = second >= 0 ? blend[second].SubFrame : frameA;
        float total = blend[best].Lerp + (second >= 0 ? blend[second].Lerp : 0);
        lerp = second >= 0 && total > 0 ? blend[second].Lerp / total : 0;
        return true;
    }

    /// <summary>The kind of a model by name (alias, brush, sprite), or null if it cannot be loaded.</summary>
    public LegacyModelKind? KindOf(string model) => Load(model)?.Kind;

    // ---- skeleton objects (FTE_CSQC_SKELETONOBJECTS) -------------------------------------------------

    private Skeleton? SkeletonAt(int number) => (uint)(number - 1) < (uint)_skeletons.Length ? _skeletons[number - 1] : null;

    public int SkelCreate(string model)
    {
        Count(nameof(SkelCreate));
        if (Load(model) is not { NumBones: > 0 } m) return 0;
        int slot = Array.IndexOf(_skeletons, null);
        if (slot < 0)
        {
            if (_skeletons.Length >= MaxSkeletons) return 0;
            slot = _skeletons.Length;
            Array.Resize(ref _skeletons, Math.Min(MaxSkeletons, _skeletons.Length * 2));
        }
        // initialize to identity matrices
        BoneMatrix[] relative = new BoneMatrix[m.NumBones];
        Array.Fill(relative, BoneMatrix.Identity);
        _skeletons[slot] = new Skeleton { Model = m, Relative = relative };
        LiveSkeletons++;
        return slot + 1;
    }

    public int SkelBuild(int skeleton, int edict, string model, float retainFraction, int firstBone, int lastBone)
    {
        Count(nameof(SkelBuild));
        if (_host is not { } host || SkeletonAt(skeleton) is not { } skel) return 0;
        if (Load(model) is not { NumBones: > 0 } m) return 0;
        if ((uint)edict >= (uint)host.Vm.NumEdicts) return 0;
        int first = Math.Max(0, firstBone - 1);
        int last = Math.Min(Math.Min(lastBone - 1, m.NumBones - 1), skel.Model.NumBones - 1);
        Span<FrameBlend> blend = stackalloc FrameBlend[MaxFrameBlends];
        FrameBlends(host, edict, m, blend);
        // "for (numblends = 0; numblends < MAX_FRAMEBLENDS && frameblend[numblends].lerp; numblends++)"
        int blends = 0;
        while (blends < MaxFrameBlends && blend[blends].Lerp != 0) blends++;
        for (int bone = first; bone <= last; bone++)
        {
            BoneMatrix sum = BoneMatrix.Zero;
            for (int i = 0; i < blends; i++)
                sum = sum.Accumulate(m.BonePose(blend[i].SubFrame, bone), blend[i].Lerp);
            // retainfrac: "0 replaces entirely, 1 does nothing, 0.5 blends half"
            skel.Relative[bone] = BoneMatrix.Interpolate(sum.Normalize3(), skel.Relative[bone], retainFraction);
        }
        return skeleton;
    }

    public int SkelNumBones(int skeleton)
    {
        Count(nameof(SkelNumBones));
        return SkeletonAt(skeleton)?.Model.NumBones ?? 0;
    }

    public string? SkelBoneName(int skeleton, int bone)
    {
        Count(nameof(SkelBoneName));
        return SkeletonAt(skeleton) is { } skel && (uint)(bone - 1) < (uint)skel.Model.NumBones ? skel.Model.BoneNames[bone - 1] : null;
    }

    public int SkelBoneParent(int skeleton, int bone)
    {
        Count(nameof(SkelBoneParent));
        return SkeletonAt(skeleton) is { } skel && (uint)(bone - 1) < (uint)skel.Model.NumBones ? skel.Model.BoneParents[bone - 1] + 1 : 0;
    }

    public int SkelFindBone(int skeleton, string name)
    {
        Count(nameof(SkelFindBone));
        return SkeletonAt(skeleton)?.Model.TagIndexForName(name) ?? 0;
    }

    public bool SkelGetBone(int skeleton, int bone, bool absolute, out LegacyBoneTransform transform)
    {
        Count(nameof(SkelGetBone));
        transform = default;
        if (SkeletonAt(skeleton) is not { } skel || (uint)(bone - 1) >= (uint)skel.Model.NumBones) return false;
        int b = bone - 1;
        BoneMatrix matrix = skel.Relative[b];
        if (absolute)
            // convert to absolute
            for (int parent = skel.Model.BoneParents[b]; parent >= 0; parent = skel.Model.BoneParents[parent])
                matrix = BoneMatrix.Concat(skel.Relative[parent], matrix);
        transform = new LegacyBoneTransform { Origin = Q(matrix.Origin), Forward = Q(matrix.Fwd), Right = Q(-matrix.Left), Up = Q(matrix.Up) };
        return true;
    }

    private static BoneMatrix Matrix(in LegacyBoneTransform t) => new(V(t.Forward), -V(t.Right), V(t.Up), V(t.Origin));

    public void SkelSetBone(int skeleton, int bone, in LegacyBoneTransform transform, bool multiply)
    {
        Count(nameof(SkelSetBone));
        if (SkeletonAt(skeleton) is not { } skel || (uint)(bone - 1) >= (uint)skel.Model.NumBones) return;
        BoneMatrix matrix = Matrix(transform);
        // skel_mul_bone: "Matrix4x4_Concat(&relativetransforms[bonenum], &matrix, &temp)"
        skel.Relative[bone - 1] = multiply ? BoneMatrix.Concat(matrix, skel.Relative[bone - 1]) : matrix;
    }

    public void SkelMulBones(int skeleton, int firstBone, int lastBone, in LegacyBoneTransform transform)
    {
        Count(nameof(SkelMulBones));
        if (SkeletonAt(skeleton) is not { } skel) return;
        BoneMatrix matrix = Matrix(transform);
        int first = Math.Max(0, firstBone - 1), last = Math.Min(lastBone - 1, skel.Model.NumBones - 1);
        for (int bone = first; bone <= last; bone++)
            skel.Relative[bone] = BoneMatrix.Concat(matrix, skel.Relative[bone]);
    }

    public void SkelCopyBones(int destination, int source, int firstBone, int lastBone)
    {
        Count(nameof(SkelCopyBones));
        if (SkeletonAt(destination) is not { } to || SkeletonAt(source) is not { } from) return;
        int first = Math.Max(0, firstBone - 1);
        int last = Math.Min(Math.Min(lastBone - 1, to.Model.NumBones - 1), from.Model.NumBones - 1);
        for (int bone = first; bone <= last; bone++) to.Relative[bone] = from.Relative[bone];
    }

    public void SkelDelete(int skeleton)
    {
        Count(nameof(SkelDelete));
        if (SkeletonAt(skeleton) is null) return;
        _skeletons[skeleton - 1] = null;
        LiveSkeletons--;
    }

    public int FrameForName(string model, string name)
    {
        Count(nameof(FrameForName));
        if (Load(model)?.Scenes is not { } scenes) return -1;
        for (int i = 0; i < scenes.Length; i++)
            if (string.Equals(scenes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public float FrameDuration(string model, int frame)
    {
        Count(nameof(FrameDuration));
        if (Load(model)?.Scenes is not { } scenes || (uint)frame >= (uint)scenes.Length) return 0;
        return scenes[frame].FrameRate != 0 ? scenes[frame].FrameCount / scenes[frame].FrameRate : 0;
    }

    // ---- surfaces: not done ------------------------------------------------------------------------

    public int SurfaceNumPoints(int edict, string model, int surface) { Count(nameof(SurfaceNumPoints)); return 0; }
    public QcVector SurfacePoint(int edict, string model, int surface, int point) { Count(nameof(SurfacePoint)); return default; }
    public QcVector SurfacePointAttribute(int edict, string model, int surface, int point, int attribute) { Count(nameof(SurfacePointAttribute)); return default; }
    public QcVector SurfaceNormal(int edict, string model, int surface) { Count(nameof(SurfaceNormal)); return default; }
    public string? SurfaceTexture(int edict, string model, int surface) { Count(nameof(SurfaceTexture)); return null; }
    public int SurfaceNearPoint(int edict, string model, QcVector point) { Count(nameof(SurfaceNearPoint)); return -1; }
    public QcVector SurfaceClippedPoint(int edict, string model, int surface, QcVector point) { Count(nameof(SurfaceClippedPoint)); return default; }
    public int SurfaceNumTriangles(int edict, string model, int surface) { Count(nameof(SurfaceNumTriangles)); return 0; }
    public QcVector SurfaceTriangle(int edict, string model, int surface, int triangle) { Count(nameof(SurfaceTriangle)); return default; }

    private static Vector3 V(QcVector v) => new(v.X, v.Y, v.Z);
    private static QcVector Q(Vector3 v) => new(v.X, v.Y, v.Z);
}
