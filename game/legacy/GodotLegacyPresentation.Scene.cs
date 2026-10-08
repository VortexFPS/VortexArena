// Port of Base/darkplaces/clvm_cmds.c VM_CL_R_ClearScene, VM_CL_R_AddEntity, VM_CL_R_SetView,
// VM_CL_R_RenderScene, VM_CL_R_AddDynamicLight, VM_CL_R_PolygonBegin/Vertex/End (the 3D case),
// VM_CL_project / VM_CL_unproject and VM_CL_V_CalcRefdef; csprogs.c CSQC_AddRenderEdict (what the
// renderer does with an entity's fields) and CSQC_RelinkAllEntities; cl_main.c CL_UpdateNetworkEntity and
// CL_RelinkStaticEntities for the entities the engine itself adds to the scene.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Math;
using VortexArena.Formats.Md3;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Game.Loaders.Models;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // csprogs.h RF_* and protocol.h EF_*.
    private const int RfViewModel = 1, RfExternalModel = 2, RfDepthHack = 4, RfAdditive = 8, RfUseAxis = 16, RfFullBright = 256, RfNoShadow = 512;
    private const int EfNoDraw = 16, EfAdditive = 32, EfFullBright = 512, EfNoShadow = 4096, EfNoDepthTest = 8192, EfDoubleSided = 32768;

    // Proxy key spaces. The program's edicts are below MAX_EDICTS; the rest are made up here.
    private const int NetworkKeyBase = 0x10000, StaticKeyBase = 0x20000, OneOffKeyBase = 0x30000;

    // Bounds on what one frame of a stranger's program may ask of the renderer.
    private const int MaxProxies = 2048;
    private const int MaxOneOffsPerFrame = 512;
    private const int MaxModelBuildsPerFrame = 6;
    private const double ModelBuildBudgetSeconds = 0.008;
    private const int MaxDynamicLights = 32;         // DarkPlaces: MAX_DLIGHTS 256; a Forward+ frame affords far fewer
    private const int MaxPolygonsPerFrame = 256;
    private const int MaxPolygonVerticesPerFrame = 16384;
    private const int MaxStaticEntities = 1024;
    private const int MaxMaterialVariants = 512;
    private const int MaxBones = 256;

    // Render-state bits a proxy's materials are rebuilt for.
    private const int BitAdditive = 1, BitFullBright = 2, BitDoubleSided = 8;

    private sealed class Proxy
    {
        public Node3D? Node;
        public bool Built, Failed;
        public Skeleton3D? Skeleton;
        public int[]? BoneParents;
        public ModelAnimator? Animator;
        public int LastFrame = int.MinValue;
        public readonly List<GeometryInstance3D> Geometry = new();
        public List<(MeshInstance3D Mesh, int Surface, Material? Original)>? Surfaces;
        public float Alpha = 1;
        public int Bits;
        public bool CastsShadow = true;
        public List<MeshInstance3D>? Meshes;
        public bool DepthHack, IsSubmodel;
        public int DepthHackAsserted;
        public (Color ColorMod, Color GlowMod, Color Shirt, Color Pants)? Tint;
        public int LastFrameB = int.MinValue;
        public float LastLerp = -1;
    }

    private readonly Dictionary<int, Proxy> _proxies = new();
    private readonly LegacySceneLedger _ledger = new() { Capacity = MaxProxies, ReleaseAfterFrames = 180 };
    private readonly List<int> _hide = new(), _release = new();
    private readonly List<LegacyDynamicLight> _lights = new();
    private readonly List<OmniLight3D> _lightPool = new();
    private readonly List<EntityState> _staticEntities = new();
    private readonly Dictionary<(ulong Material, int Bits), Material> _materialVariants = new();
    private readonly Dictionary<(string Texture, int Flags), StandardMaterial3D> _polygonMaterials = new();
    private readonly string?[] _lightStyles = new string?[DpProtocol.MaxLightStyles];
    private readonly LegacyRefdef _refdef = new();
    private ImmediateMesh _polygonMesh = null!;
    private MeshInstance3D _polygonNode = null!;
    private int _proxyNodes, _oneOffs, _buildsThisFrame, _polygonsThisFrame, _polygonVertices, _submittedThisFrame, _viewsThisFrame;
    private bool _mainRendered;
    private double _buildSecondsThisFrame;

    private void InitializeScene()
    {
        _polygonMesh = new ImmediateMesh();
        _polygonNode = new MeshInstance3D { Name = "LegacyPolygons", Mesh = _polygonMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _sceneRoot.AddChild(_polygonNode);
    }

    private static readonly bool s_debugEntities = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_DUMP"));
    private readonly List<string> _debugEntities = new();

    private void BeginSceneFrame()
    {
        _debugEntities.Clear();
        _mainRendered = false;
        _viewsThisFrame = 0;
        _oneOffs = 0;
        _buildsThisFrame = 0;
        _buildSecondsThisFrame = 0;
        _submittedThisFrame = 0;
        _polygonsThisFrame = 0;
        _polygonVertices = 0;
        _lights.Clear();
        _polygonMesh.ClearSurfaces();
        _ledger.BeginFrame();
        _refdef.BeginFrame();
        _listenerOverridden = false;
    }

    private void EndSceneFrame()
    {
        // A frame in which the program never called renderscene (it faulted, or is between levels) leaves
        // the last scene standing rather than blanking it.
        if (!_mainRendered) return;
        LastSceneEntities = _submittedThisFrame;
        _ledger.Sweep(_hide, _release);
        foreach (int key in _hide)
            if (_proxies.TryGetValue(key, out Proxy? hidden) && hidden.Node is { } node) node.Visible = false;
        foreach (int key in _release) FreeProxy(key);
    }

    private void FreeProxy(int key)
    {
        if (!_proxies.Remove(key, out Proxy? proxy)) return;
        if (proxy.Node is { } node && GodotObject.IsInstanceValid(node))
        {
            node.QueueFree();
            _proxyNodes--;
        }
    }

    private void ReleaseAllProxies()
    {
        _ledger.Clear(_release);
        foreach (int key in _release) FreeProxy(key);
        // Anything the ledger did not know (it should know all of them).
        foreach (int key in new List<int>(_proxies.Keys)) FreeProxy(key);
        _proxyNodes = 0;
        // The variants hold duplicates of the level's materials; a new level makes its own.
        _materialVariants.Clear();
    }

    // ---- ILegacyScene ---------------------------------------------------------------------------------

    // Only the first view of a frame is drawn (see RenderScene); what is submitted for a later one is counted.
    private bool Collecting => !_mainRendered;

    void ILegacyScene.ClearScene()
    {
        View.Reset();
        if (!Collecting) return;
        _lights.Clear();
    }

    /// <summary>
    /// CSQC_RelinkAllEntities: the engine's own entities. The world is drawn by RenderScene when
    /// VF_DRAWWORLD says so; static entities always; the server's networked (non-CSQC) entities with
    /// ENTMASK_ENGINE. Beams and engine-side effect entities are the particle system's business, and
    /// the engine's own view model (ENTMASK_ENGINEVIEWMODELS) does not exist: Xonotic draws its own.
    /// </summary>
    void ILegacyScene.AddEngineEntities(int drawMask)
    {
        if (!Collecting || _state is not { } state) return;

        for (int i = 0; i < _staticEntities.Count; i++)
            SubmitNetworkState(StaticKeyBase + i, _staticEntities[i], state);

        if ((drawMask & 1) == 0 || state.NetworkEntities is not { } table) return;
        int count = Math.Min(table.Count, DpProtocol.MaxEdicts);
        for (int number = 1; number < count; number++)
        {
            ref readonly EntityState entity = ref table.Current(number);
            if (!entity.IsActive || entity.ModelIndex == 0) continue;
            // The view entity is RENDER_EXTERIORMODEL: seen in mirrors and shadows, not through its own eyes.
            if (number == state.ViewEntity) continue;
            SubmitNetworkState(NetworkKeyBase + number, entity, state);
        }
    }

    // CL_UpdateNetworkEntity, without the interpolation between the last two states: the entity is drawn
    // where the newest frame put it.
    private void SubmitNetworkState(int key, in EntityState entity, CsqcClientState state)
    {
        if ((entity.Effects & EfNoDraw) != 0) return;
        if (state.ModelNameForIndex(entity.ModelIndex) is not { Length: > 0 } model) return;
        _submittedThisFrame++;
        if (model[0] == '*') SubmodelSubmissions++;

        QcVector origin = new(entity.Origin.X, entity.Origin.Y, entity.Origin.Z);
        QcVector angles = new(entity.Angles.X, entity.Angles.Y, entity.Angles.Z);
        float scale = entity.Scale / 16f;
        if (!(scale > 0)) scale = 1;
        if (ModelData.KindOf(model) == LegacyModelKind.Alias) angles.X = -angles.X;   // CL_GetPitchSign
        QcCoreBuiltins.AngleVectors(angles, out QcVector forward, out QcVector right, out QcVector up);
        BoneMatrix placement = new(N(forward) * scale, N(right) * -scale, N(up) * scale, N(origin));

        if (Touch(key, model, entity.Skin) is not { Node: { } } proxy) return;
        if (s_debugEntities && model[0] == '*' && _debugEntities.Count < 300)
            _debugEntities.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"net {key - NetworkKeyBase,4} {model} ef {entity.Effects} a {entity.Alpha} org {origin.X:0.#} {origin.Y:0.#} {origin.Z:0.#} ang {angles.X:0.#} {angles.Y:0.#} {angles.Z:0.#} meshes {proxy.Geometry.Count}"));
        if (!ApplyPlacement(proxy, placement)) return;
        ApplyRenderState(proxy, entity.Alpha / 255f, entity.Effects, 0);
        // EntityState colormod / glowmod are bytes at 32 = 1.0 (protocol.h); zero-length means "not set".
        ApplyTint(proxy, entity.Colormap,
            new QcVector(entity.ColorMod0 / 32f, entity.ColorMod1 / 32f, entity.ColorMod2 / 32f),
            new QcVector(entity.GlowMod0 / 32f, entity.GlowMod1 / 32f, entity.GlowMod2 / 32f));
        if (proxy.Animator is { } animator && proxy.LastFrame != entity.Frame)
        {
            animator.SetRawFrame(entity.Frame);
            proxy.LastFrame = entity.Frame;
        }
    }

    /// <summary>
    /// CSQC_AddRenderEdict. The entity's placement is CL_GetTagMatrix on itself - its own origin, angles
    /// (pitch negated for an alias model) and scale, or its v_forward/v_right/v_up axes with RF_USEAXIS,
    /// carried through any chain of tag attachments, and relative to the view with RF_VIEWMODEL - which
    /// <see cref="FormatLegacyModels.TagMatrix"/> already computes from the same fields.
    /// </summary>
    bool ILegacyScene.AddEntity(in LegacyRenderEntity entity)
    {
        if (string.IsNullOrEmpty(entity.Model)) return false;
        if (!Collecting) return true;
        _submittedThisFrame++;
        string model = entity.Model;
        if (model[0] == '*') SubmodelSubmissions++;
        if (model == "null") return false;

        int key;
        if (entity.Edict > 0 && entity.Edict < DpProtocol.MaxEdicts) key = entity.Edict;
        else
        {
            if (_oneOffs >= MaxOneOffsPerFrame) return false;
            key = OneOffKeyBase + _oneOffs++;
        }

        BoneMatrix placement;
        if (key < DpProtocol.MaxEdicts && ModelData.TagMatrix(entity.Edict, 0, out BoneMatrix matrix) == 0) placement = matrix;
        else
        {
            // addentity with no persistent edict (or one the tag walk refused): its own fields only.
            float scale = entity.Scale == 0 ? 1 : entity.Scale;
            if ((entity.RenderFlags & RfUseAxis) != 0)
                placement = new BoneMatrix(N(entity.AxisForward) * scale, N(entity.AxisRight) * -scale, N(entity.AxisUp) * scale, N(entity.Origin));
            else
            {
                QcVector angles = entity.Angles;
                if (ModelData.KindOf(model) == LegacyModelKind.Alias) angles.X = -angles.X;
                QcCoreBuiltins.AngleVectors(angles, out QcVector forward, out QcVector right, out QcVector up);
                placement = new BoneMatrix(N(forward) * scale, N(right) * -scale, N(up) * scale, N(entity.Origin));
            }
        }

        if (Touch(key, model, entity.Skin) is not { } proxy) return false;
        if (s_debugEntities && _debugEntities.Count < 300)
            _debugEntities.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ent {entity.Edict,4} {model} rf {entity.RenderFlags} ef {entity.Effects} a {entity.Alpha:0.##} sc {entity.Scale:0.##} cm {entity.ColorMap} skin {entity.Skin} fr {entity.Frame:0} tag {entity.TagEntity}/{entity.TagIndex} org {entity.Origin.X:0.#} {entity.Origin.Y:0.#} {entity.Origin.Z:0.#} ang {entity.Angles.X:0.#} {entity.Angles.Y:0.#} {entity.Angles.Z:0.#} -> at {placement.Origin.X:0.#} {placement.Origin.Y:0.#} {placement.Origin.Z:0.#} |fwd| {placement.Fwd.Length():0.###} colormod {entity.ColorMod.X:0.##} {entity.ColorMod.Y:0.##} {entity.ColorMod.Z:0.##} glow {entity.GlowMod.X:0.##} {entity.GlowMod.Y:0.##} {entity.GlowMod.Z:0.##} node {(proxy.Node is null ? "none" : "ok")} failed {proxy.Failed}"));
        if (proxy.Node is null) return !proxy.Failed;
        if (!ApplyPlacement(proxy, placement)) return true;

        // "if (!entrender->alpha) entrender->alpha = 1"
        ApplyRenderState(proxy, entity.Alpha == 0 ? 1 : entity.Alpha, entity.Effects, entity.RenderFlags);
        ApplyTint(proxy, entity.ColorMap, entity.ColorMod, entity.GlowMod);
        ApplyPose(proxy, entity);
        return true;
    }

    // The proxy for a submission: built on first sight, rebuilt when its model or skin changes. At most a
    // few models are built per frame, so a level's first frames spread the loading instead of stalling on it.
    private Proxy? Touch(int key, string model, int skin)
    {
        LegacySceneLedger.TouchResult result = _ledger.Touch(key, model, skin);
        if (result == LegacySceneLedger.TouchResult.Refused) return null;
        if (!_proxies.TryGetValue(key, out Proxy? proxy))
        {
            proxy = new Proxy();
            _proxies[key] = proxy;
        }
        else if (result is LegacySceneLedger.TouchResult.Create or LegacySceneLedger.TouchResult.Rebuild)
        {
            FreeProxy(key);
            proxy = new Proxy();
            _proxies[key] = proxy;
        }
        // One build always; more only while the frame has spent little on them. A level's first frames have
        // seventy models to build, and six a frame made each of those frames take half a second.
        if (!proxy.Built && !proxy.Failed && _buildsThisFrame < MaxModelBuildsPerFrame && (_buildsThisFrame == 0 || _buildSecondsThisFrame < ModelBuildBudgetSeconds))
        {
            _buildsThisFrame++;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Build(proxy, model, skin);
            _buildSecondsThisFrame += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        }
        return proxy;
    }

    private void Build(Proxy proxy, string model, int skin)
    {
        proxy.Built = true;
        if (model[0] == '*')
        {
            BuildSubmodel(proxy, model);
            return;
        }
        // The name came from the server or its program: a path inside the session's game data, or nothing.
        if (!LegacyQcHost.IsSafePath(model) || !_vfs.Exists(model))
        {
            proxy.Failed = true;
            return;
        }
        Node3D? node = null;
        try
        {
            // A vertex-animated model with more than one frame gets the morphing animator, so .frame shows;
            // everything else (IQM/DPM skeletal, single-frame MD3, MDL, sprites) is the asset pipeline's node.
            Md3Data? md3 = skin == 0 && model.EndsWith(".md3", StringComparison.OrdinalIgnoreCase) ? _assets.LoadMd3(model) : null;
            if (md3 is { FrameCount: > 1 })
            {
                ModelAnimator animator = ModelAnimator.Create(md3, null, _assets.Assets);
                animator.SetRawFrame(0);
                proxy.Animator = animator;
                node = animator;
            }
            else node = _assets.LoadModel(model, Math.Clamp(skin, 0, 255));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _note($"model \"{model}\" could not be built: {e.GetType().Name}: {e.Message}");
        }
        if (node is null)
        {
            proxy.Failed = true;
            return;
        }
        node.Visible = false;
        _sceneRoot.AddChild(node);
        _proxyNodes++;
        proxy.Node = node;
        CollectGeometry(node, proxy.Geometry);
        // DarkPlaces lights every model from the map's light grid; the request is honoured while a grid is bound.
        ModelTint.EnableGridLight(node, true);

        proxy.Skeleton = IqmBuilder.FindSkeleton(node);
        if (proxy.Skeleton is { } skeleton)
        {
            // The program poses the skeleton (skeleton objects, or .frame blends); the clip player the
            // builder starts would fight it.
            foreach (Node child in node.GetChildren())
                if (child is AnimationPlayer player)
                {
                    player.Stop();
                    player.Active = false;
                }
            int bones = skeleton.GetBoneCount();
            proxy.BoneParents = new int[bones];
            for (int i = 0; i < bones; i++) proxy.BoneParents[i] = skeleton.GetBoneParent(i);
        }
    }

    // ---- map submodels ("*N") ------------------------------------------------------------------------

    private VortexArena.Formats.Bsp.BspData? _levelBsp;
    private string _levelName = "";
    private static readonly System.Reflection.MethodInfo? s_memberwiseClone =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.PropertyInfo? s_bspFaces = typeof(VortexArena.Formats.Bsp.BspData).GetProperty(nameof(VortexArena.Formats.Bsp.BspData.Faces));

    /// <summary>Submodels built since the level began, and the time that took.</summary>
    public int SubmodelsBuilt { get; private set; }
    public double SubmodelBuildSeconds { get; private set; }

    /// <summary>
    /// The render geometry of one inline brush model, as its own node. The map loader builds whatever faces a
    /// map has, so it is handed a view of the level in which every face outside this model's range
    /// [FirstFace, FirstFace + FaceCount) is a flare (a face type with no geometry): same vertices, same
    /// textures, same lightmap pages, one model's worth of triangles. The vertices are where the map compiler
    /// left them, so the entity's origin and angles (zero for a door at rest) move it from there, as in
    /// DarkPlaces. The world's visibility culling and occluder are not wanted on a thing that moves.
    /// </summary>
    private void BuildSubmodel(Proxy proxy, string model)
    {
        if (_levelBsp is not { } bsp || s_memberwiseClone is null || s_bspFaces is null
            || !int.TryParse(model.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            || index < 1 || index >= bsp.Models.Length)
        {
            proxy.Failed = true;
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Node3D? node = null;
        try
        {
            VortexArena.Formats.Bsp.BspModel range = bsp.Models[index];
            VortexArena.Formats.Bsp.BspFace[] faces = new VortexArena.Formats.Bsp.BspFace[bsp.Faces.Length];
            int first = Math.Max(0, range.FirstFace), end = (int)Math.Min((long)range.FirstFace + range.FaceCount, faces.Length);
            for (int i = 0; i < faces.Length; i++)
                faces[i] = i >= first && i < end ? bsp.Faces[i] : bsp.Faces[i] with { Type = VortexArena.Formats.Bsp.BspFaceType.Flare, IndexCount = 0, VertexCount = 0 };
            if (end > first)
            {
                VortexArena.Formats.Bsp.BspData view = (VortexArena.Formats.Bsp.BspData)s_memberwiseClone.Invoke(bsp, null)!;
                s_bspFaces.SetValue(view, faces);
                node = MapLoader.BuildMap(view, _assets.Assets, _levelName);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _note($"map submodel {model} could not be built: {e.GetType().Name}: {e.Message}");
        }
        if (node is null)
        {
            proxy.Failed = true;
            return;
        }
        foreach (Node child in node.GetChildren())
            if (child is WorldPvsCuller or WorldOcclusion)
            {
                node.RemoveChild(child);
                child.QueueFree();
            }
        node.Name = "Submodel" + index;
        node.Visible = false;
        _sceneRoot.AddChild(node);
        _proxyNodes++;
        proxy.Node = node;
        proxy.IsSubmodel = true;
        CollectGeometry(node, proxy.Geometry);
        foreach (GeometryInstance3D geometry in proxy.Geometry) geometry.Visible = true;
        SubmodelsBuilt++;
        SubmodelBuildSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private static void CollectGeometry(Node node, List<GeometryInstance3D> into)
    {
        if (node is GeometryInstance3D geometry) into.Add(geometry);
        foreach (Node child in node.GetChildren()) CollectGeometry(child, into);
    }

    // A Quake-space placement (columns forward, left, up; then the origin) onto a node whose mesh was
    // built in Godot space: the similarity transform M * W * M^-1 the skeletal poser uses.
    private static bool ApplyPlacement(Proxy proxy, in BoneMatrix placement)
    {
        if (proxy.Node is not { } node) return false;
        if (!Finite(placement.Fwd) || !Finite(placement.Left) || !Finite(placement.Up) || !Finite(placement.Origin))
        {
            node.Visible = false;
            return false;
        }
        node.Transform = IqmBuilder.ConjugateQuakeWorldToGodot(ToTransform(placement));
        return true;
    }

    private static Transform3D ToTransform(in BoneMatrix m) =>
        new(new Basis(new Vector3(m.Fwd.X, m.Fwd.Y, m.Fwd.Z), new Vector3(m.Left.X, m.Left.Y, m.Left.Z), new Vector3(m.Up.X, m.Up.Y, m.Up.Z)),
            new Vector3(m.Origin.X, m.Origin.Y, m.Origin.Z));

    // The flags of CSQC_AddRenderEdict that change how an entity is drawn. RF_EXTERNALMODEL is "in mirrors
    // but not in the normal view": with one view and no mirrors, hidden. RF_VIEWMODEL's depth-range hack
    // and RF_DEPTHHACK are not reproduced (a view model can poke into a wall it is pressed against).
    private void ApplyRenderState(Proxy proxy, float alpha, int effects, int renderFlags)
    {
        if (proxy.Node is not { } node) return;
        bool hidden = (renderFlags & RfExternalModel) != 0 || (effects & EfNoDraw) != 0 || !(alpha > 0);
        node.Visible = !hidden;
        if (hidden) return;

        alpha = Math.Clamp(alpha, 0f, 1f);
        if (MathF.Abs(alpha - proxy.Alpha) > 0.004f)
        {
            proxy.Alpha = alpha;
            foreach (GeometryInstance3D geometry in proxy.Geometry) geometry.Transparency = 1f - alpha;
        }

        bool castsShadow = (renderFlags & (RfViewModel | RfNoShadow)) == 0 && (effects & (EfNoShadow | EfAdditive | EfNoDepthTest)) == 0 && alpha >= 1;
        if (castsShadow != proxy.CastsShadow)
        {
            proxy.CastsShadow = castsShadow;
            foreach (GeometryInstance3D geometry in proxy.Geometry)
                geometry.CastShadow = castsShadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        }

        int bits = 0;
        if ((effects & EfAdditive) != 0 || (renderFlags & RfAdditive) != 0) bits |= BitAdditive;
        if ((effects & EfFullBright) != 0 || (renderFlags & RfFullBright) != 0) bits |= BitFullBright;
        if ((effects & EfDoubleSided) != 0) bits |= BitDoubleSided;

        // RENDER_VIEWMODEL and RENDER_NODEPTHTEST (RF_VIEWMODEL, RF_DEPTHHACK, EF_NODEPTHTEST) are one thing to
        // DarkPlaces' model renderer: MATERIALFLAG_SHORTDEPTHRANGE, GL_DepthRange(0, 0.0625) with the depth
        // test still on (gl_rmain.c:6768). The entity keeps its own self-occlusion and always beats the world.
        bool depthHack = (renderFlags & (RfViewModel | RfDepthHack)) != 0 || (effects & EfNoDepthTest) != 0;
        if (depthHack != proxy.DepthHack)
        {
            proxy.DepthHack = depthHack;
            proxy.DepthHackAsserted = 0;
            // Leaving the state means giving back the gun-owned materials: the surface overrides are dropped
            // and the instance returns to the default layer.
            if (!depthHack)
            {
                foreach (GeometryInstance3D geometry in proxy.Geometry)
                {
                    geometry.Layers = 1;
                    geometry.IgnoreOcclusionCulling = false;
                    if (geometry is MeshInstance3D { Mesh: { } mesh } instance)
                        for (int s = 0, n = Math.Min(mesh.GetSurfaceCount(), 64); s < n; s++) instance.SetSurfaceOverrideMaterial(s, null);
                }
                proxy.Surfaces = null;
                proxy.Bits = 0;
            }
        }
        // A morphing model swaps its mesh on the first frame it is posed, so the state is asserted again for a
        // few frames after it is first wanted (the call is idempotent and walks only this entity's surfaces).
        if (depthHack && proxy.DepthHackAsserted < 8)
        {
            proxy.DepthHackAsserted++;
            ViewModelRenderFx.Apply(node);
        }
        if (bits != proxy.Bits)
        {
            proxy.Bits = bits;
            ApplyMaterialBits(proxy, bits);
        }
    }

    // ---- colormap, colormod, glowmod ---------------------------------------------------------------------

    private Color[]? _shirtPalette, _pantsPalette;

    // palette.c Palette_Load: r_colormap_palette (gfx/colormap_palette.lmp) is 16 shirt colours, 16 scoreboard
    // shirt colours, 16 pants colours, 16 scoreboard pants colours, three bytes each. Xonotic ships one. Without
    // it DarkPlaces derives the rows from the Quake palette; Xonotic's own table (lib/color.qh) stands in here.
    private void LoadColormapPalette()
    {
        _shirtPalette = new Color[16];
        _pantsPalette = new Color[16];
        for (int i = 0; i < 16; i++)
        {
            System.Numerics.Vector3 c = VortexArena.Common.Gameplay.Teams.ColormapPaletteColor(i == 15 ? 0 : i, 0);
            _shirtPalette[i] = _pantsPalette[i] = new Color(c.X, c.Y, c.Z);
        }
        string name = _cvars.Has("r_colormap_palette") && _cvars.GetString("r_colormap_palette") is { Length: > 0 } set ? set : "gfx/colormap_palette.lmp";
        try
        {
            if (!LegacyQcHost.IsSafePath(name) || !_vfs.Exists(name)) return;
            byte[] file = _vfs.ReadBytes(name);
            if (file.Length >= 96)
                for (int i = 0; i < 16; i++) _shirtPalette[i] = _pantsPalette[i] = new Color(file[i * 3] / 255f, file[i * 3 + 1] / 255f, file[i * 3 + 2] / 255f);
            if (file.Length >= 192)
                for (int i = 0; i < 16; i++) _pantsPalette[i] = new Color(file[96 + i * 3] / 255f, file[96 + i * 3 + 1] / 255f, file[96 + i * 3 + 2] / 255f);
        }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException) { }
    }

    // CSQC_AddRenderEdict's colormap rule and CL_SetEntityColormapColors: nothing for 0 or less, a player's
    // scoreboard colours for a client number, otherwise the value itself (Xonotic passes 1024 + colours).
    // "if (!VectorLength2(colormod)) VectorSet(colormod, 1, 1, 1)", and the same for glowmod.
    private void ApplyTint(Proxy proxy, int colormap, QcVector colorMod, QcVector glowMod)
    {
        if (proxy.Node is null || proxy.IsSubmodel) return;
        if (_shirtPalette is null) LoadColormapPalette();
        Color shirt = ModelTint.Black, pants = ModelTint.Black;
        if (colormap > 0)
        {
            int colors = colormap;
            if (_state is { } state && colormap <= state.MaxClients) colors = state.Scores[colormap - 1].Colors;
            pants = _pantsPalette![colors & 15];
            shirt = _shirtPalette![(colors >> 4) & 15];
        }
        Color cm = Tint(colorMod), gm = Tint(glowMod);
        (Color, Color, Color, Color) tint = (cm, gm, shirt, pants);
        if (proxy.Tint is { } old && old == tint) return;
        proxy.Tint = tint;
        if (proxy.Meshes is null)
        {
            proxy.Meshes = new List<MeshInstance3D>();
            foreach (GeometryInstance3D geometry in proxy.Geometry)
                if (geometry is MeshInstance3D mesh) proxy.Meshes.Add(mesh);
        }
        ModelTint.Apply(proxy.Meshes, cm, gm, shirt, pants);

        // Not set means white; a negative component (Xonotic's ghost-item colour is -1 -1 -1) draws as black.
        static Color Tint(QcVector v) =>
            !Finite(v) || (v.X == 0 && v.Y == 0 && v.Z == 0) ? ModelTint.White : new Color(Math.Clamp(v.X, 0f, 8f), Math.Clamp(v.Y, 0f, 8f), Math.Clamp(v.Z, 0f, 8f));
    }

    // EF_ADDITIVE / EF_FULLBRIGHT / EF_DOUBLESIDED: a variant of each surface's material,
    // shared between every entity that needs the same one. Only Godot's standard materials can be varied
    // this way; a surface drawn by a custom shader (the player skin shader) keeps its own.
    private void ApplyMaterialBits(Proxy proxy, int bits)
    {
        if (proxy.Surfaces is null)
        {
            proxy.Surfaces = new List<(MeshInstance3D, int, Material?)>();
            foreach (GeometryInstance3D geometry in proxy.Geometry)
            {
                if (geometry is not MeshInstance3D { Mesh: { } mesh } instance) continue;
                int surfaces = Math.Min(mesh.GetSurfaceCount(), 64);
                for (int s = 0; s < surfaces; s++) proxy.Surfaces.Add((instance, s, instance.GetSurfaceOverrideMaterial(s)));
            }
        }
        foreach ((MeshInstance3D instance, int surface, Material? original) in proxy.Surfaces)
        {
            if (!GodotObject.IsInstanceValid(instance) || instance.Mesh is not { } mesh || surface >= mesh.GetSurfaceCount()) continue;
            if (bits == 0)
            {
                instance.SetSurfaceOverrideMaterial(surface, original);
                continue;
            }
            Material? source = original ?? mesh.SurfaceGetMaterial(surface);
            if (source is not BaseMaterial3D standard) continue;
            (ulong, int) variantKey = (standard.GetInstanceId(), bits);
            if (!_materialVariants.TryGetValue(variantKey, out Material? variant))
            {
                if (_materialVariants.Count >= MaxMaterialVariants) continue;
                BaseMaterial3D copy = (BaseMaterial3D)standard.Duplicate();
                if ((bits & BitAdditive) != 0)
                {
                    copy.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                    copy.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                }
                if ((bits & BitFullBright) != 0) copy.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                if ((bits & BitDoubleSided) != 0) copy.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                variant = copy;
                _materialVariants[variantKey] = variant;
            }
            instance.SetSurfaceOverrideMaterial(surface, variant);
        }
    }

    // VM_GenerateFrameGroupBlend + VM_FrameBlendFromFrameGroupBlend + VM_UpdateEdictSkeleton, as the model
    // file's parser already computes them for the tag builtins; here the result is pushed onto the node.
    private void ApplyPose(Proxy proxy, in LegacyRenderEntity entity)
    {
        bool persistent = entity.Edict > 0;
        if (proxy.Animator is { } animator)
        {
            // frame / frame2 / lerpfrac as VM_FrameBlendFromFrameGroupBlend resolved them: the two strongest
            // poses and the weight of the second.
            int frameA = (int)entity.Frame, frameB = frameA;
            float weight = 0;
            if (persistent && ModelData.RenderFrames(entity.Edict, out int a, out int b, out float lerp))
            {
                frameA = a;
                frameB = b;
                weight = float.IsFinite(lerp) ? Math.Clamp(lerp, 0f, 1f) : 0f;
            }
            if (frameA != proxy.LastFrame || frameB != proxy.LastFrameB || MathF.Abs(weight - proxy.LastLerp) > 0.004f)
            {
                animator.SetRawFrameBlend(frameA, frameB, weight);
                proxy.LastFrame = frameA;
                proxy.LastFrameB = frameB;
                proxy.LastLerp = weight;
            }
            return;
        }
        if (!persistent || proxy.Skeleton is not { } skeleton || proxy.BoneParents is not { } parents) return;

        Span<BoneMatrix> absolute = stackalloc BoneMatrix[MaxBones];
        int bones = ModelData.RenderBones(entity.Edict, absolute);
        // The Skeleton3D was built from the same file in the same bone order; a count that differs means it was not.
        if (bones == 0 || bones != parents.Length) return;
        Span<Transform3D> world = stackalloc Transform3D[MaxBones];
        for (int i = 0; i < bones; i++)
        {
            ref readonly BoneMatrix bone = ref absolute[i];
            if (!Finite(bone.Fwd) || !Finite(bone.Left) || !Finite(bone.Up) || !Finite(bone.Origin)) return;
            Transform3D pose = IqmBuilder.ConjugateQuakeWorldToGodot(ToTransform(bone));
            world[i] = pose;
            int parent = parents[i];
            Transform3D local = parent >= 0 && parent < i ? world[parent].AffineInverse() * pose : pose;
            skeleton.SetBonePosePosition(i, local.Origin);
            skeleton.SetBonePoseRotation(i, local.Basis.GetRotationQuaternion());
        }
    }

    bool ILegacyScene.SetProperty(int property, QcVector a, QcVector b) => View.Set(property, a, b);
    bool ILegacyScene.GetProperty(int property, out QcVector value) => View.Get(property, out value);

    /// <summary>
    /// VM_CL_R_RenderScene. Godot draws the tree once, after the frame's script work, so "render" here
    /// means "this is the view": the first call of a frame fixes the camera, the world's visibility and
    /// the lights. Later calls in the same frame (a second view into a sub-rectangle) are counted and
    /// not drawn - there is one camera.
    /// </summary>
    void ILegacyScene.RenderScene()
    {
        _viewsThisFrame++;
        if (_mainRendered)
        {
            ExtraViewsSkipped++;
            return;
        }
        _mainRendered = true;

        if (Finite(View.Origin) && Finite(View.Angles))
        {
            // r_refdef.view.matrix. A Godot camera looks along -Z with +Y up: columns right, up, -forward.
            QcCoreBuiltins.AngleVectors(View.Angles, out QcVector forward, out QcVector right, out QcVector up);
            _camera.Transform = new Transform3D(new Basis(G(right), G(up), -G(forward)), G(View.Origin));
            if (View.UsePerspective)
            {
                if (_camera.Projection != Camera3D.ProjectionType.Perspective) _camera.Projection = Camera3D.ProjectionType.Perspective;
                _camera.Fov = Math.Clamp(View.VerticalFovDegrees, 1f, 179f);
            }
            else
            {
                // VF_PERSPECTIVE 0: the two "field of view" numbers are the half-width and half-height of the box in
                // world units (gl_rmain.c R_View_SetFrustum, "ortho_x abused as angle by VM_CL_R_SetView").
                if (_camera.Projection != Camera3D.ProjectionType.Orthogonal) _camera.Projection = Camera3D.ProjectionType.Orthogonal;
                _camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
                _camera.Size = Math.Clamp(View.FovY * 2f, 1f, 131072f);
            }
        }
        if (_mapRoot is not null) _mapRoot.Visible = View.DrawWorld;
        ApplyProgramFog();

        LastDynamicLights = _lights.Count;
        for (int i = 0; i < _lights.Count; i++)
        {
            if (i >= _lightPool.Count)
            {
                OmniLight3D made = new() { Name = "LegacyDynamicLight", ShadowEnabled = false, Visible = false };
                _sceneRoot.AddChild(made);
                LightBudget.Register(made, LightBudget.Role.Dynamic, noShadow: true);
                _lightPool.Add(made);
            }
            LegacyDynamicLight light = _lights[i];
            OmniLight3D node = _lightPool[i];
            float brightest = MathF.Max(light.Color.X, MathF.Max(light.Color.Y, light.Color.Z));
            node.Position = G(light.Origin);
            node.OmniRange = light.Radius;
            node.LightColor = brightest > 0 ? new Color(light.Color.X / brightest, light.Color.Y / brightest, light.Color.Z / brightest) : Colors.Black;
            node.LightEnergy = Math.Clamp(brightest, 0f, 16f);
            node.Visible = true;
        }
        for (int i = _lights.Count; i < _lightPool.Count; i++) _lightPool[i].Visible = false;
    }

    // The VF_FOG_* keys: the program overrides the level's fog for the frame (Xonotic's Fog_Force and its
    // underwater tint). The conversion to Godot's exponential fog is MapLoader.ApplyFog's, with the program's
    // numbers in place of worldspawn's: DarkPlaces holds fog at 1 - exp(-density * 0.004 * range) * alpha
    // from fogrange = clamp(2048 / density + start, start, end) on. A frame that sets no fog key leaves the
    // level's fog as it was.
    private bool _programFog;
    private (bool On, Color Color, float Density) _levelFog;

    private void ApplyProgramFog()
    {
        if (_environment?.Environment is not { } env) return;
        if (!View.FogTouched)
        {
            if (_programFog)
            {
                _programFog = false;
                env.FogEnabled = _levelFog.On;
                env.FogLightColor = _levelFog.Color;
                env.FogDensity = _levelFog.Density;
            }
            return;
        }
        if (!_programFog)
        {
            _programFog = true;
            _levelFog = (env.FogEnabled, env.FogLightColor, env.FogDensity);
        }
        float density = View.FogDensity, alpha = Math.Clamp(View.FogAlpha, 0f, 1f), start = MathF.Max(0, View.FogStart), end = MathF.Max(start, View.FogEnd);
        if (!(density > 0) || !(alpha > 0) || !float.IsFinite(density) || !Finite(View.FogColor))
        {
            env.FogEnabled = false;
            return;
        }
        float range = Math.Clamp(2048f / density + start, start, end);
        float maxFog = (1f - MathF.Exp(-density * 0.004f * MathF.Max(1f, range - start))) * alpha;
        if (maxFog < 0.002f || !(range > 0))
        {
            env.FogEnabled = false;
            return;
        }
        env.FogEnabled = true;
        env.FogLightColor = new Color(Math.Clamp(View.FogColor.X, 0f, 1f), Math.Clamp(View.FogColor.Y, 0f, 1f), Math.Clamp(View.FogColor.Z, 0f, 1f));
        env.FogDensity = -MathF.Log(1f - MathF.Min(maxFog, 0.98f)) / range;
    }

    /// <summary>VM_CL_R_AddDynamicLight: "if we've run out of dlights, just return". The style, cubemap and corona are not used.</summary>
    void ILegacyScene.AddDynamicLight(in LegacyDynamicLight light)
    {
        if (!Collecting || _lights.Count >= MaxDynamicLights) return;
        if (!Finite(light.Origin) || !Finite(light.Color) || !(light.Radius > 0) || light.Radius > 65536) return;
        _lights.Add(light);
    }

    /// <summary>
    /// R_BeginPolygon .. R_EndPolygon. A 2D polygon joins the 2D draw list, in order with the pictures
    /// around it; a 3D one becomes a triangle fan in this frame's immediate mesh.
    /// </summary>
    void ILegacyScene.DrawPolygon(string texture, int drawFlags, bool is2D, ReadOnlySpan<LegacyPolygonVertex> vertices)
    {
        if (vertices.Length < 3) return;
        if (is2D)
        {
            DrawList.Polygon(texture, drawFlags, vertices);
            return;
        }
        if (!Collecting || _polygonsThisFrame >= MaxPolygonsPerFrame) return;
        int triangles = vertices.Length - 2;
        if (_polygonVertices + triangles * 3 > MaxPolygonVerticesPerFrame) return;
        foreach (ref readonly LegacyPolygonVertex vertex in vertices)
            if (!Finite(vertex.Position)) return;

        _polygonMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, PolygonMaterial(texture, drawFlags));
        for (int i = 1; i + 1 < vertices.Length; i++)
        {
            Emit(vertices[0]);
            Emit(vertices[i]);
            Emit(vertices[i + 1]);
        }
        _polygonMesh.SurfaceEnd();
        _polygonsThisFrame++;
        _polygonVertices += triangles * 3;
        PolygonsDrawn++;

        void Emit(in LegacyPolygonVertex vertex)
        {
            _polygonMesh.SurfaceSetColor(new Color(vertex.Color.X, vertex.Color.Y, vertex.Color.Z, vertex.Alpha));
            _polygonMesh.SurfaceSetUV(new Vector2(vertex.TexCoord.X, vertex.TexCoord.Y));
            _polygonMesh.SurfaceAddVertex(G(vertex.Position));
        }
    }

    // One unlit, vertex-coloured, two-sided material per texture and draw flag.
    private StandardMaterial3D PolygonMaterial(string texture, int drawFlags)
    {
        (string, int) key = (texture, drawFlags);
        if (_polygonMaterials.TryGetValue(key, out StandardMaterial3D? material)) return material;
        material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BlendMode = drawFlags == 1 ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            AlbedoTexture = texture == "$whiteimage" ? null : LoadPictureTexture(texture),
        };
        // Past the bound the material is made but not kept: correct, just not shared.
        if (_polygonMaterials.Count < 256) _polygonMaterials[key] = material;
        return material;
    }

    QcVector ILegacyScene.Unproject(QcVector screen) => View.Unproject(screen);
    QcVector ILegacyScene.Project(QcVector world) => View.Project(world);

    /// <summary>
    /// VM_CL_V_CalcRefdef: the engine's first-person view from the entity the program names, written
    /// into the view origin and angles (cl.csqc_vieworigin / cl.csqc_viewangles). The entity's origin is
    /// the origin of its render matrix; for an unattached entity that is its origin field.
    /// </summary>
    void ILegacyScene.CalcRefdef(in LegacyRefdefInput input)
    {
        LegacyRefdefSettings settings = LegacyRefdefSettings.Default;
        if (_cvars.Has("cl_stairsmoothspeed")) settings.StairSmoothSpeed = _cvars.GetFloat("cl_stairsmoothspeed");
        if (_cvars.Has("cl_smoothviewheight")) settings.SmoothViewHeight = _cvars.GetFloat("cl_smoothviewheight");
        if (_cvars.Has("sv_stepheight")) settings.StepHeight = _cvars.GetFloat("sv_stepheight");
        if (_cvars.Has("v_deathtilt")) settings.DeathTilt = _cvars.GetFloat("v_deathtilt") != 0;
        if (_cvars.Has("v_deathtiltangle")) settings.DeathTiltAngle = _cvars.GetFloat("v_deathtiltangle");
        if (_state is { } state)
        {
            settings.PunchAngle = state.PunchAngle;
            settings.PunchVector = state.PunchVector;
        }
        _refdef.Calculate(input, View.Angles, _time, _oldTime, settings, out QcVector origin, out QcVector angles);
        View.Set(LegacyViewState.VfOrigin, origin, default);
        View.Set(LegacyViewState.VfAngles, angles, default);
    }

    /// <summary>#35 lightstyle and svc_lightstyle: remembered. The map's baked light is not re-animated from them.</summary>
    void ILegacyScene.SetLightStyle(int style, string map)
    {
        if ((uint)style < (uint)_lightStyles.Length) _lightStyles[style] = map is { Length: <= 64 } ? map : null;
    }

    /// <summary>
    /// #92 getlight (R_CompleteLightPoint). The map's light grid is on the GPU for the model shaders and
    /// is not sampled here: the answer is full ambient light from above, the same as a map without
    /// light data. Xonotic uses the result to tint a few effects.
    /// </summary>
    QcVector ILegacyScene.GetLight(QcVector point, int flags, out QcVector ambient, out QcVector diffuse, out QcVector direction)
    {
        ambient = new QcVector(1, 1, 1);
        diffuse = default;
        direction = new QcVector(0, 0, 1);
        return ambient;
    }

    // ---- engine entities that arrive outside the entity frames -----------------------------------------

    /// <summary>svc_spawnstatic: an entity that never changes (a torch). Kept for the level and drawn with every scene.</summary>
    void IDpClientHandler.OnSpawnStatic(in EntityState state)
    {
        if (_staticEntities.Count < MaxStaticEntities) _staticEntities.Add(state);
    }
}
