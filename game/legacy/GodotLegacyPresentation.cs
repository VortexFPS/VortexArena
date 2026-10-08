// Port of Base/darkplaces/cl_parse.c CL_BeginDownloads (loading the level's world model before the
// client program starts) and of the frame bracket around csprogs.c CL_VM_UpdateView: what the engine
// sets up before CSQC_UpdateView runs (the view it computed, r_refdef.scene emptied) and what it does
// with the result afterwards. The scene, 2D, sound and effect halves are in the sibling partial files.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Legacy;

/// <summary>
/// <see cref="ILegacyPresentation"/> on Godot: what a server-supplied client program draws and plays.
///
/// Every QUESTION the program asks (traces, model bounds and tags, skeletons, picture sizes) is answered
/// by the same Godot-free classes the headless client uses - <see cref="BspLegacyWorld"/>,
/// <see cref="FormatLegacyModels"/>, <see cref="LegacyPictureCatalog"/> - so the program computes the
/// same numbers with or without a window. What is new here is the OUTPUT: the scene it submits each
/// frame becomes pooled Godot nodes (mark-and-sweep, see GodotLegacyPresentation.Scene.cs), its 2D
/// calls are recorded and replayed by <see cref="LegacyDrawLayer"/>, its sounds and effects go to the
/// existing audio and particle systems.
///
/// The program and everything the server sends are untrusted. Nothing here hands a name to Godot's
/// resource loader: models, pictures and sounds are read through the session's own virtual
/// filesystem, after <see cref="LegacyQcHost.IsSafePath"/>, and every per-frame list is bounded.
/// </summary>
public sealed partial class GodotLegacyPresentation : ILegacyPresentation, ILegacyScene, ILegacySound, ILegacyEffects, IDpClientHandler
{
    private const int StatViewHeight = 16, StatViewZoom = 21;

    private readonly Node3D _sceneRoot;
    private readonly LegacyDrawLayer _drawLayer;
    private readonly VirtualFileSystem _vfs;
    private readonly AssetLoader _assets;
    private readonly CvarService _cvars;
    private readonly Action<string> _note;
    private CsqcHost? _host;
    private CsqcClientState? _state;
    private Node3D? _mapRoot;
    private WorldEnvironment? _environment;
    private readonly Camera3D _camera;
    private readonly EffectSystem _effects;
    private double _time, _oldTime;

    /// <param name="sceneRoot">The node the 3D scene is built under. It must already be in the tree.</param>
    /// <param name="files">The session's own virtual filesystem: Xonotic's game data, nothing of the player's.</param>
    /// <param name="cvars">The session's own cvar store.</param>
    /// <param name="note">Where one-line diagnostics go (a missing map, a model that would not build).</param>
    /// <param name="canvas">The 2D canvas to draw the HUD on; null makes one. A session started from the Xonotic
    /// menu is handed a canvas that already has the fonts the shared configuration loaded.</param>
    public GodotLegacyPresentation(Node3D sceneRoot, LegacyDrawLayer drawLayer, VirtualFileSystem files, AssetLoader assets, CvarService cvars, Action<string> note,
        LegacyCanvas? canvas = null)
    {
        _sceneRoot = sceneRoot ?? throw new ArgumentNullException(nameof(sceneRoot));
        _drawLayer = drawLayer ?? throw new ArgumentNullException(nameof(drawLayer));
        _vfs = files ?? throw new ArgumentNullException(nameof(files));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        _note = note ?? (_ => { });

        // The client program poses every skeleton itself: the clip library a model's builder would make (a
        // hundred milliseconds and tens of megabytes for a player model) would never be played.
        assets.SkipModelAnimations = true;
        Map = new BspLegacyWorld(files);
        ModelData = new FormatLegacyModels(files, Map) { View = () => (View.Origin, View.Angles) };
        Canvas = canvas ?? new LegacyCanvas(files, assets);

        // The view camera. Xonotic's fov is horizontal at 4:3; the view state holds the vertical angle.
        _camera = new Camera3D { Name = "LegacyCamera", Near = 1f, Far = 32768f, Fov = LegacyViewState.VerticalFov(90), Current = true };
        _sceneRoot.AddChild(_camera);

        // (N6) the light arbiter first: the lights made below register with it, and Register is a no-op without one.
        _sceneRoot.AddChild(new LightBudget { Name = "LightBudget" });

        _effects = new EffectSystem { Name = "Effects" };
        _sceneRoot.AddChild(_effects);
        // The particle atlas and effectinfo.txt come from the session's data, so the names the server's
        // program asks for are the names this catalog holds.
        _effects.TextureLoader = name => LegacyQcHost.IsSafePath(name) ? _assets.LoadTexture(name) : null;
        _effects.VfsTextLoader = path =>
        {
            try { return LegacyQcHost.IsSafePath(path) && _vfs.Exists(path) ? _vfs.ReadText(path) : null; }
            catch (Exception e) when (e is System.IO.IOException or InvalidOperationException) { return null; }
        };
        _effects.ModelLoader = path => LegacyQcHost.IsSafePath(path) && _vfs.Exists(path) ? _assets.LoadModel(path) : null;

        InitializeScene();
        InitializeSound();
    }

    /// <summary>The level: collision, visibility, the area grid of the program's entities.</summary>
    public BspLegacyWorld Map { get; }
    /// <summary>Model files, parsed: bounds, tags, bones, scenes, skeleton objects.</summary>
    public FormatLegacyModels ModelData { get; }
    /// <summary>The view as the program has set it for this frame.</summary>
    public LegacyViewState View { get; } = new();

    public ILegacyScene Scene => this;
    public ILegacyDraw Draw => Canvas;
    public ILegacySound Sound => this;
    public ILegacyWorld World => Map;
    public ILegacyModels Models => ModelData;
    public ILegacyEffects Effects => this;

    // ---- the numbers a status line reports -----------------------------------------------------------

    /// <summary>Entities the program submitted in the last frame that was drawn (models that could be shown or not).</summary>
    public int LastSceneEntities { get; private set; }
    /// <summary>Proxy nodes alive: models built and kept for the entities seen recently.</summary>
    public int ProxyNodes => _proxyNodes;
    /// <summary>2D draw commands recorded in the last frame, and how many were refused by the bound.</summary>
    public int LastDrawCommands { get; private set; }
    public long DrawCommandsDropped => DrawList.TotalDropped;
    public long SoundsStarted { get; private set; }
    public long EffectsSpawned { get; private set; }
    /// <summary>Effects asked for that named nothing this client can draw.</summary>
    public long EffectsUnknown { get; private set; }
    public int LastDynamicLights { get; private set; }
    /// <summary>renderscene calls after the first in a frame: views that were counted and not drawn.</summary>
    public long ExtraViewsSkipped { get; private set; }
    /// <summary>Submissions of "*N" map submodels: drawn as part of the static world, wherever the entity is.</summary>
    public long SubmodelSubmissions { get; private set; }
    public long PolygonsDrawn { get; private set; }
    public string? MapError => Map.LoadError;

    public void Attach(CsqcHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Map.Attach(host);
        ModelData.Attach(host);
        _refdef.Reset();
        // A new program numbers its entities afresh: nothing drawn for the old one's edicts stays.
        ReleaseAllProxies();
    }

    /// <summary>
    /// A level begins (svc_serverinfo): its map is parsed once and used three ways - collision for the
    /// program's traces, the render geometry, and the particle system's own collision. This runs inside
    /// the parse of the server's message, before the program for the level is loaded.
    /// </summary>
    public void BeginLevel(CsqcClientState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        Loading = true;
        string map = state.WorldModel;
        // The first level of a session may find its map already loaded (BeginPreload, a local game) and its
        // files already on the worker threads; a later level starts from nothing but the loader's caches.
        bool first = _levelsBegun++ == 0;
        bool preloaded = first && _preloadedWorld is { } ready && ready == map && _mapRoot is not null;
        _preloadedWorld = null;
        if (!first)
        {
            CancelPrecache();
            ReleaseAllProxies();
            ReleasePrebuilt();
            ModelData.ClearCache();
        }
        _staticEntities.Clear();
        StopAllSounds();
        _refdef.Reset();
        if (preloaded)
        {
            BeginPrecache(state);
            _note($"map \"{map}\" was loaded while the server was starting");
            return;
        }
        ReleaseLevelMaps();
        if (_mapRoot is not null)
        {
            _mapRoot.QueueFree();
            _mapRoot = null;
        }

        // The name is the server's. It has to be a plain path inside the game data and nothing else.
        if (!LegacyQcHost.IsSafePath(map) || !map.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) || !_vfs.Exists(map))
        {
            Map.LoadMap(map);   // records why (LoadError) and leaves an empty world
            _note($"map \"{map}\" is not in the Xonotic data: the world is empty ({Map.LoadError})");
            return;
        }

        // The level's models and sounds start loading on worker threads now, under the map build and CSQC_Init.
        BeginPrecache(state);
        LoadWorld(map, state.WorldNameNoExtension);
    }

    // The level's map: parsed once and used three ways (see BeginLevel). False if it could not be drawn - the
    // world is then collision only, or empty, and the reason has been noted.
    private bool LoadWorld(string map, string levelName)
    {
        try
        {
            BspData? bsp = _assets.ReadBsp(map);
            if (bsp is null)
            {
                Map.LoadMap(map);
                _note($"map \"{map}\" could not be parsed: {Map.LoadError}");
                return false;
            }
            // One collision build, the DarkPlaces-exact one (curved surfaces as coarse triangles, as a
            // Xonotic server collides). Building the native slab form here first only had it thrown
            // away and rebuilt inside UseMap.
            Map.UseMap(map, bsp);

            // The client draws the world it loaded locally (VF_DRAWWORLD): model 0 only. Every "*N" submodel (a
            // door, a platform, a gametype-only wall) is an entity's model and is built when one is submitted
            // (BuildSubmodel), so it moves with its entity and is not there when nothing submits it.
            HashSet<int> submodels = new();
            for (int i = 1; i < bsp.Models.Length; i++) submodels.Add(i);
            _levelBsp = bsp;
            _levelMaps.Add(bsp);
            _levelName = levelName;
            _mapRoot = MapLoader.BuildMap(bsp, _assets.Assets, levelName, submodels);
            _sceneRoot.AddChild(_mapRoot);
            // Particles collide with the same world the game does.
            if (Map.Collision is { } collision) _effects.SetCollisionWorld(collision);
            _effects.SetDecalGeometry(bsp);
            ModelLighting.ApplyMap(bsp.LightGrid);
            ApplyEnvironment(bsp);
            _note($"map \"{map}\" loaded: {bsp.Models.Length} models, {bsp.Faces.Length} faces");
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A map from a server's download directory is as untrusted as anything else it sends.
            _note($"map \"{map}\" failed to build ({e.GetType().Name}: {e.Message}); falling back to collision only");
            Map.LoadMap(map);
            return false;
        }
    }

    // The sky, fog and tone mapping of the level: the same settings the native client applies (NetGame.AddLight).
    private void ApplyEnvironment(BspData bsp)
    {
        if (_environment is null)
        {
            _environment = new WorldEnvironment { Name = "WorldEnvironment" };
            _sceneRoot.AddChild(_environment);
            _sceneRoot.AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-50f, -30f, 0f), ShadowEnabled = false });
        }
        Sky sky = SkyboxLoader.TryBuild(bsp, _assets.Assets) ?? new Sky { SkyMaterial = new ProceduralSkyMaterial() };
        Godot.Environment env = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        MapLoader.ApplyFog(env, bsp);
        _environment.Environment = env;
    }

    /// <summary>
    /// Before CSQC_UpdateView: the engine's own view for this frame (what clearscene restores), and
    /// every per-frame list emptied.
    /// </summary>
    /// <param name="pixelSize">The window in pixels (vid.mode.width / height).</param>
    /// <param name="time">cl.time.</param>
    public void BeginFrame(Vector2 pixelSize, double time)
    {
        _oldTime = _time;
        _time = time;
        View.ConWidth = Math.Max(1, _cvars.GetFloat("vid_conwidth"));
        View.ConHeight = Math.Max(1, _cvars.GetFloat("vid_conheight"));
        View.EngineDrawWorld = !_cvars.Has("r_drawworld") || _cvars.GetFloat("r_drawworld") != 0;

        QcVector eye = default, angles = default;
        float zoom = 1;
        if (_state is { } state)
        {
            // V_CalcRefdef's starting point: the view entity's origin raised by STAT_VIEWHEIGHT.
            eye = state.ViewEntityOrigin;
            eye.Z += state.Stats[StatViewHeight];
            angles = state.ViewAngles;
            zoom = state.Stats[StatViewZoom] > 0 ? state.Stats[StatViewZoom] / 255f : 1;
        }
        float fov = _cvars.Has("fov") ? _cvars.GetFloat("fov") : 90;
        View.SetEngineView(pixelSize.X, pixelSize.Y, eye, angles, fov, zoom);
        View.Reset();

        Canvas.ConWidth = View.ConWidth;
        Canvas.ConHeight = View.ConHeight;
        Canvas.PixelWidth = pixelSize.X;
        Canvas.PixelHeight = pixelSize.Y;
        Canvas.ReadTextCvars(name => _cvars.Has(name) ? _cvars.GetFloat(name) : null);
        DrawList.Clear();
        BeginSceneFrame();
        BeginEffectsFrame();
    }

    /// <summary>After CSQC_UpdateView: sweep what was not submitted, hand the 2D list to its canvas, move the listener.</summary>
    public void EndFrame()
    {
        EndSceneFrame();
        LastDrawCommands = DrawList.Count;
        _drawLayer.Present(Canvas);
        UpdateSounds();
    }

    /// <summary>Stops everything audible and drops every node: the session is over.</summary>
    public void Shutdown()
    {
        CancelPrecache();
        StopAllSounds();
        ReleaseAllProxies();
        ReleasePrebuilt();
        ReleaseLevelMaps();
        DrawList.Clear();
        _drawLayer.Present(Canvas);
        ModelLighting.Clear();
        _host = null;
    }

    // The map loader keeps the lightmap pages it uploaded in a process-wide cache keyed by the parsed map - and
    // with them the parsed map. A level this session is done with (and every per-submodel view of it, each of
    // which got pages of its own) is taken out, or each level ever played stays in memory.
    private void ReleaseLevelMaps()
    {
        int pages = 0;
        foreach (BspData map in _levelMaps) pages += MapLoader.ReleaseLightmaps(map);
        if (_levelMaps.Count > 0) _note($"level released: {_levelMaps.Count} map views, {pages} lightmap pages forgotten");
        _levelMaps.Clear();
        _levelBsp = null;
        foreach (Image image in _submodelImages.Values) image.Dispose();
        _submodelImages.Clear();
    }

    private readonly List<BspData> _levelMaps = new();

    // ---- small conversions shared by the partial files ------------------------------------------------

    private static NVec3 N(QcVector v) => new(v.X, v.Y, v.Z);
    private static Vector3 G(QcVector v) => Coords.ToGodot(new NVec3(v.X, v.Y, v.Z));
    private static Vector3 G(NVec3 v) => Coords.ToGodot(v);

    private static bool Finite(QcVector v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(NVec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
