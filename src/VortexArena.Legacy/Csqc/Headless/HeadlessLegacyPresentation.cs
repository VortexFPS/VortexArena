// Port of nothing new: this composes the ports in this directory (BspLegacyWorld for cl_collision.c,
// FormatLegacyModels for the model and skeleton queries of clvm_cmds.c, LegacyPictureCatalog for
// gl_draw.c's picture cache) with NullLegacyPresentation for everything that is output. The one piece
// of DarkPlaces behaviour it adds is clvm_cmds.c VM_CL_R_ClearScene / VM_CL_R_SetView keeping
// r_refdef.view.matrix, which CL_GetTagMatrix reads for view models.
using VortexArena.Formats.Vfs;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// A presentation with no renderer and no sound device that nevertheless answers every question
/// truthfully from the game's data: traces and point contents against the real map and the program's
/// own solid entities, model boxes, tags, bones and animation lengths from the model files, picture
/// sizes from the image headers. With it a client program computes with real numbers - its movement
/// prediction collides, its attachments resolve, its HUD measures - which is the difference between
/// "the program runs" and "the program behaves".
///
/// What it does not do is produce anything: drawing, sound, particles and scene rendering are counted
/// and dropped, exactly as <see cref="NullLegacyPresentation"/> drops them. The three objects that do
/// the answering are public, because a presentation that does render delegates the same questions to
/// the same objects.
/// </summary>
public sealed class HeadlessLegacyPresentation : ILegacyPresentation, ILegacyCallCounts, ILegacyScene, ILegacyDraw
{
    private const int VfOrigin = 11, VfOriginX = 12, VfOriginZ = 14, VfAngles = 15, VfAnglesX = 16, VfAnglesZ = 18; // csprogs.h
    private const int StatViewHeight = 16;

    private readonly NullLegacyPresentation _null;
    private CsqcHost? _host;
    private QcVector _viewOrigin, _viewAngles;

    /// <param name="files">The game data: the level's map, the models and the pictures are read from it.</param>
    /// <param name="viewSize">The size the scene properties report as the window; 1024 x 768 if not given.</param>
    public HeadlessLegacyPresentation(VirtualFileSystem files, QcVector? viewSize = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        _null = new NullLegacyPresentation { FileExists = path => LegacyQcHost.IsSafePath(path) && files.Exists(path), ViewSize = viewSize ?? new QcVector(1024, 768, 0) };
        Map = new BspLegacyWorld(files, _null.Calls);
        ModelData = new FormatLegacyModels(files, Map, _null.Calls) { View = () => (_viewOrigin, _viewAngles) };
        Pictures = new LegacyPictureCatalog(files);
    }

    /// <summary>The level: collision, visibility, the area grid of the program's entities.</summary>
    public BspLegacyWorld Map { get; }
    /// <summary>Model files, parsed: bounds, tags, bones, scenes, skeleton objects.</summary>
    public FormatLegacyModels ModelData { get; }
    /// <summary>The 2D picture cache, as far as existence and size go.</summary>
    public LegacyPictureCatalog Pictures { get; }

    public ILegacyScene Scene => this;
    public ILegacyDraw Draw => this;
    public ILegacySound Sound => _null.Sound;
    public ILegacyWorld World => Map;
    public ILegacyModels Models => ModelData;
    public ILegacyEffects Effects => _null.Effects;

    /// <summary>Calls received, by interface member name - the questions answered here included.</summary>
    public IReadOnlyDictionary<string, long> Calls => _null.Calls;
    public void ResetCounts() => _null.ResetCounts();

    /// <summary>Entities submitted since the last clearscene.</summary>
    public int SceneEntities => _null.SceneEntities;
    /// <summary>The view as the program last set it (VF_ORIGIN / VF_ANGLES): where it decided the eye is this frame.</summary>
    public QcVector ViewOrigin => _viewOrigin;
    public QcVector ViewAngles => _viewAngles;

    public void Attach(CsqcHost host)
    {
        _host = host;
        _null.Attach(host);
        Map.Attach(host);
        ModelData.Attach(host);
    }

    /// <summary>
    /// A level begins: its map is loaded and its collision built before the program for it starts,
    /// since the program is told the world's size as it is created. A level whose map the client does
    /// not have plays in an empty world (<see cref="BspLegacyWorld.LoadError"/> says so).
    /// </summary>
    public void BeginLevel(CsqcClientState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // The submodels "*1".. are the new map's.
        ModelData.ClearCache();
        if (Map.MapName != state.WorldModel || Map.Bsp is null) Map.LoadMap(state.WorldModel);
    }

    private void Count(string member) => _null.Calls[member] = _null.Calls.GetValueOrDefault(member) + 1;

    // ---- scene: forwarded, with the view remembered -------------------------------------------------

    void ILegacyScene.ClearScene()
    {
        _null.Scene.ClearScene();
        // "reset the view to the engine's": the eye of the view entity (V_CalcRefdef's starting point).
        if (_host is { } host)
        {
            _viewOrigin = host.State.ViewEntityOrigin;
            _viewOrigin.Z += host.State.Stats[StatViewHeight];
            _viewAngles = host.State.ViewAngles;
        }
    }

    bool ILegacyScene.SetProperty(int property, QcVector a, QcVector b)
    {
        // VM_CL_R_SetView: VF_ORIGIN and VF_ANGLES, whole or one component at a time.
        if (property == VfOrigin) _viewOrigin = a;
        else if (property == VfAngles) _viewAngles = a;
        else if (property is >= VfOriginX and <= VfOriginZ) Component(ref _viewOrigin, property - VfOriginX) = a.X;
        else if (property is >= VfAnglesX and <= VfAnglesZ) Component(ref _viewAngles, property - VfAnglesX) = a.X;
        return _null.Scene.SetProperty(property, a, b);

        static ref float Component(ref QcVector v, int index)
        {
            if (index == 0) return ref v.X;
            if (index == 1) return ref v.Y;
            return ref v.Z;
        }
    }

    void ILegacyScene.AddEngineEntities(int drawMask) => _null.Scene.AddEngineEntities(drawMask);
    bool ILegacyScene.AddEntity(in LegacyRenderEntity entity) => _null.Scene.AddEntity(entity);
    bool ILegacyScene.GetProperty(int property, out QcVector value) => _null.Scene.GetProperty(property, out value);
    void ILegacyScene.RenderScene() => _null.Scene.RenderScene();
    void ILegacyScene.AddDynamicLight(in LegacyDynamicLight light) => _null.Scene.AddDynamicLight(light);
    void ILegacyScene.DrawPolygon(string texture, int drawFlags, bool is2D, ReadOnlySpan<LegacyPolygonVertex> vertices) => _null.Scene.DrawPolygon(texture, drawFlags, is2D, vertices);
    QcVector ILegacyScene.Unproject(QcVector screen) => _null.Scene.Unproject(screen);
    QcVector ILegacyScene.Project(QcVector world) => _null.Scene.Project(world);
    void ILegacyScene.CalcRefdef(in LegacyRefdefInput input) => _null.Scene.CalcRefdef(input);
    void ILegacyScene.SetLightStyle(int style, string map) => _null.Scene.SetLightStyle(style, map);
    QcVector ILegacyScene.GetLight(QcVector point, int flags, out QcVector ambient, out QcVector diffuse, out QcVector direction) =>
        _null.Scene.GetLight(point, flags, out ambient, out diffuse, out direction);

    // ---- 2D: the questions answered from file headers, the drawing forwarded --------------------------

    bool ILegacyDraw.PictureExists(string name)
    {
        Count(nameof(ILegacyDraw.PictureExists));
        return Pictures.Precache(name);
    }

    void ILegacyDraw.DefinePicture(string name, ReadOnlySpan<byte> jpeg)
    {
        Count(nameof(ILegacyDraw.DefinePicture));
        Pictures.Define(name, jpeg);
    }

    QcVector ILegacyDraw.ImageSize(string name)
    {
        Count(nameof(ILegacyDraw.ImageSize));
        (int width, int height) = Pictures.Size(name);
        return new QcVector(width, height, 0);
    }

    void ILegacyDraw.FreePicture(string name)
    {
        Count(nameof(ILegacyDraw.FreePicture));
        Pictures.Free(name);
    }

    void ILegacyDraw.Line(float width, QcVector from, QcVector to, QcVector color, float alpha, int flags) => _null.Draw.Line(width, from, to, color, alpha, flags);
    QcVector ILegacyDraw.Text(in LegacyText text) => _null.Draw.Text(text);
    void ILegacyDraw.Picture(in LegacyPicture picture) => _null.Draw.Picture(picture);
    void ILegacyDraw.Fill(QcVector position, QcVector size, QcVector color, float alpha, int flags) => _null.Draw.Fill(position, size, color, alpha, flags);
    void ILegacyDraw.SetClipArea(float x, float y, float width, float height) => _null.Draw.SetClipArea(x, y, width, height);
    void ILegacyDraw.ResetClipArea() => _null.Draw.ResetClipArea();
    // No renderer-free source of font metrics is at hand (DarkPlaces measures with FreeType, or with
    // the widths baked into a bitmap font's .width file), so text keeps the null presentation's fixed
    // advance of one character cell.
    float ILegacyDraw.StringWidth(string text, bool ignoreColorCodes, QcVector scale, int font, QcVector fontScale) =>
        _null.Draw.StringWidth(text, ignoreColorCodes, scale, font, fontScale);
    int ILegacyDraw.FindFont(string name) => _null.Draw.FindFont(name);
    int ILegacyDraw.LoadFont(string name, string files, string sizes, int slot, float scale, float verticalOffset) =>
        _null.Draw.LoadFont(name, files, sizes, slot, scale, verticalOffset);
}
