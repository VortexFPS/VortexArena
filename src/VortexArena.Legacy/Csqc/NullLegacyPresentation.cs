// Port of Base/darkplaces answers for "nothing is there": collision.c for a trace that touches nothing,
// clvm_cmds.c VM_getimagesize and CL_GetTagMatrix for a missing picture or an untagged model,
// snd_null.c for sound. The headless ILegacyPresentation.
using System.Runtime.CompilerServices;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// A presentation with no renderer, no sound device and an empty world. Everything the program draws
/// or plays is counted and dropped; everything it asks is answered the way DarkPlaces answers when the
/// thing asked about does not exist. That is enough to run a real client program through real network
/// data and see what it asks for, which is what <see cref="Calls"/> records.
/// </summary>
public sealed class NullLegacyPresentation : ILegacyPresentation, ILegacyCallCounts, ILegacyScene, ILegacyDraw, ILegacySound, ILegacyWorld, ILegacyModels, ILegacyEffects
{
    private readonly Dictionary<int, (QcVector A, QcVector B)> _properties = new();
    private readonly Dictionary<string, int> _fonts = new(StringComparer.Ordinal);
    private CsqcHost? _host;

    /// <summary>Calls received, by interface member name.</summary>
    public Dictionary<string, long> Calls { get; } = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, long> ILegacyCallCounts.Calls => Calls;

    /// <summary>Whether a file exists in the game data, for the "can this picture / model be loaded"
    /// questions. Null answers yes to models and no to pictures, the two cheapest plausible answers.</summary>
    public Func<string, bool>? FileExists { get; init; }

    /// <summary>The size setproperty and getproperty treat as the window (vid.mode.width / height).</summary>
    public QcVector ViewSize { get; init; } = new(1024, 768, 0);

    /// <summary>Entities submitted since the last clearscene.</summary>
    public int SceneEntities { get; private set; }

    public ILegacyScene Scene => this;
    public ILegacyDraw Draw => this;
    public ILegacySound Sound => this;
    public ILegacyWorld World => this;
    public ILegacyModels Models => this;
    public ILegacyEffects Effects => this;

    public void Attach(CsqcHost host) => _host = host;

    public void ResetCounts()
    {
        Calls.Clear();
        SpawnedParticles.Clear();
    }

    private void Count([CallerMemberName] string member = "") => Calls[member] = Calls.GetValueOrDefault(member) + 1;

    // ---- scene -------------------------------------------------------------------------------------

    void ILegacyScene.ClearScene()
    {
        Count();
        SceneEntities = 0;
        _properties.Clear();
    }

    void ILegacyScene.AddEngineEntities(int drawMask) => Count();

    bool ILegacyScene.AddEntity(in LegacyRenderEntity entity)
    {
        Count();
        // CSQC_AddRenderEdict: no model, nothing to add.
        if (string.IsNullOrEmpty(entity.Model)) return false;
        SceneEntities++;
        return true;
    }

    private static bool KnownProperty(int property) =>
        property is (>= 1 and <= 21) or (>= 33 and <= 36) or (>= 200 and <= 211) or 400 or 401;

    bool ILegacyScene.SetProperty(int property, QcVector a, QcVector b)
    {
        Count();
        if (!KnownProperty(property)) return false;
        _properties[property] = (a, b);
        return true;
    }

    bool ILegacyScene.GetProperty(int property, out QcVector value)
    {
        Count();
        value = default;
        if (!KnownProperty(property)) return false;
        if (_properties.TryGetValue(property, out (QcVector A, QcVector B) stored)) value = stored.A;
        else if (property == 4) value = ViewSize;               // VF_SIZE
        else if (property == 5) value.X = ViewSize.X;           // VF_SIZE_X
        else if (property == 6) value.X = ViewSize.Y;           // VF_SIZE_Y
        else if (property is 19 or 200) value.X = 1;            // VF_DRAWWORLD, VF_PERSPECTIVE
        return true;
    }

    void ILegacyScene.RenderScene() => Count();
    void ILegacyScene.AddDynamicLight(in LegacyDynamicLight light) => Count();
    void ILegacyScene.DrawPolygon(string texture, int drawFlags, bool is2D, ReadOnlySpan<LegacyPolygonVertex> vertices) => Count();

    // With no view there is no projection. Unprojecting answers the view origin; projecting answers a
    // point behind the viewer (negative depth), which is how the program learns not to draw a label.
    QcVector ILegacyScene.Unproject(QcVector screen)
    {
        Count();
        return _properties.TryGetValue(11, out (QcVector A, QcVector B) origin) ? origin.A : default;
    }

    QcVector ILegacyScene.Project(QcVector world)
    {
        Count();
        return new QcVector(0, 0, -1);
    }

    void ILegacyScene.CalcRefdef(in LegacyRefdefInput input) => Count();
    void ILegacyScene.SetLightStyle(int style, string map) => Count();

    QcVector ILegacyScene.GetLight(QcVector point, int flags, out QcVector ambient, out QcVector diffuse, out QcVector direction)
    {
        Count();
        // R_CompleteLightPoint on a map without light data: full ambient, no directional part.
        ambient = new QcVector(1, 1, 1);
        diffuse = default;
        direction = new QcVector(0, 0, 1);
        return ambient;
    }

    // ---- 2D ----------------------------------------------------------------------------------------

    private static readonly string[] PictureExtensions = { "", ".tga", ".png", ".jpg", ".pcx" };

    bool ILegacyDraw.PictureExists(string name)
    {
        Count();
        if (FileExists is null || name.Length == 0) return false;
        foreach (string extension in PictureExtensions)
            if (FileExists(name + extension)) return true;
        return false;
    }

    void ILegacyDraw.DefinePicture(string name, ReadOnlySpan<byte> jpeg) => Count();

    QcVector ILegacyDraw.ImageSize(string name)
    {
        Count();
        return default;
    }

    void ILegacyDraw.FreePicture(string name) => Count();
    void ILegacyDraw.Line(float width, QcVector from, QcVector to, QcVector color, float alpha, int flags) => Count();

    QcVector ILegacyDraw.Text(in LegacyText text)
    {
        Count();
        return text.Color;
    }

    void ILegacyDraw.Picture(in LegacyPicture picture) => Count();
    void ILegacyDraw.Fill(QcVector position, QcVector size, QcVector color, float alpha, int flags) => Count();
    void ILegacyDraw.SetClipArea(float x, float y, float width, float height) => Count();
    void ILegacyDraw.ResetClipArea() => Count();

    /// <summary>
    /// A fixed advance of one character cell per visible character, which is what DarkPlaces' bitmap
    /// console font measures. Colour codes (^1, ^xRGB) take no room unless told to ignore them.
    /// </summary>
    float ILegacyDraw.StringWidth(string text, bool ignoreColorCodes, QcVector scale, int font, QcVector fontScale)
    {
        Count();
        return VisibleLength(text, ignoreColorCodes) * scale.X;
    }

    internal static int VisibleLength(string text, bool ignoreColorCodes)
    {
        if (ignoreColorCodes) return text.Length;
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length)
            {
                if (char.IsAsciiDigit(text[i + 1])) { i++; continue; }
                if (text[i + 1] == 'x' && i + 4 < text.Length && char.IsAsciiHexDigit(text[i + 2]) && char.IsAsciiHexDigit(text[i + 3]) && char.IsAsciiHexDigit(text[i + 4]))
                {
                    i += 4;
                    continue;
                }
                if (text[i + 1] == '^') i++; // "^^" draws one caret
            }
            count++;
        }
        return count;
    }

    int ILegacyDraw.FindFont(string name)
    {
        Count();
        return _fonts.GetValueOrDefault(name, -1);
    }

    int ILegacyDraw.LoadFont(string name, string files, string sizes, int slot, float scale, float verticalOffset)
    {
        Count();
        // dp_fonts starts with the engine's own slots (default, console, sbar, notify, chat,
        // centerprint, infobar, menu, then eight user slots); new names are appended.
        if (slot < 0 && !_fonts.TryGetValue(name, out slot)) slot = 16 + _fonts.Count;
        if (slot >= 256) return -1;
        _fonts[name] = slot;
        return slot;
    }

    // ---- sound -------------------------------------------------------------------------------------

    bool ILegacySound.Precache(string sample)
    {
        Count();
        return true;
    }

    void ILegacySound.Start(int edict, int channel, string sample, QcVector origin, float volume, float attenuation, float startPosition, int flags, float speed) => Count();
    void ILegacySound.StartStatic(QcVector origin, string sample, float volume, float attenuation) => Count();

    bool ILegacySound.Local(string sample, int channel, float volume)
    {
        Count();
        return true;
    }

    void ILegacySound.SetListener(QcVector origin, QcVector forward, QcVector right, QcVector up) => Count();

    float ILegacySound.ChannelPosition(int edict, int channel)
    {
        Count();
        return -1;
    }

    float ILegacySound.Length(string sample)
    {
        Count();
        return -1;
    }

    // ---- world -------------------------------------------------------------------------------------

    void ILegacyWorld.Bounds(out QcVector mins, out QcVector maxs)
    {
        mins = default;
        maxs = default;
    }

    float ILegacyWorld.DropToFloorDistance => 4096;

    /// <summary>A trace through empty space: Collision_ClipToWorld's initial state, never clipped.</summary>
    LegacyTrace ILegacyWorld.Trace(QcVector start, QcVector mins, QcVector maxs, QcVector end, int moveType, int ignoreEdict, int hitContentsMask, bool isLine)
    {
        Count();
        return new LegacyTrace { Fraction = 1, EndPos = end, InOpen = true };
    }

    int ILegacyWorld.PointSuperContents(QcVector point)
    {
        Count();
        return 0;
    }

    int ILegacyWorld.CheckPvs(QcVector viewPosition, QcVector mins, QcVector maxs)
    {
        Count();
        return 3; // "no PVS support on this worldmodel"
    }

    bool ILegacyWorld.MoveStep(int edict, QcVector move, bool setTrace)
    {
        Count();
        return false;
    }

    void ILegacyWorld.LinkEdict(int edict, QcVector absMin, QcVector absMax) => Count();
    void ILegacyWorld.UnlinkEdict(int edict) => Count();

    // ---- models ------------------------------------------------------------------------------------

    bool ILegacyModels.TryGetBounds(string model, out QcVector mins, out QcVector maxs)
    {
        Count();
        mins = default;
        maxs = default;
        if (model.Length == 0) return false;
        // "*N" is a submodel of the map; it exists if the map does.
        return FileExists is null || model[0] == '*' || FileExists(model);
    }

    int ILegacyModels.TagIndex(string model, int skin, string tagName)
    {
        Count();
        return 0;
    }

    /// <summary>
    /// CL_GetTagMatrix for a model with no tags and no attachment: the entity's own position and
    /// orientation. The codes for the world, a free entity and an entity without a model are the C's.
    /// </summary>
    int ILegacyModels.TagInfo(int edict, int tagIndex, out LegacyTagInfo info)
    {
        Count();
        info = new LegacyTagInfo
        {
            Forward = new QcVector(1, 0, 0), Right = new QcVector(0, -1, 0), Up = new QcVector(0, 0, 1),
            LocalForward = new QcVector(1, 0, 0), LocalRight = new QcVector(0, -1, 0), LocalUp = new QcVector(0, 0, 1),
        };
        if (edict == 0) return 1;
        if (_host is null || _host.Vm.IsFree(edict)) return 2;
        if (_host.ModelNameOf(edict) is null) return 3;
        info.Origin = _host.Vm.FieldVector(edict, _host.Fields.Origin);
        QcCoreBuiltins.AngleVectors(_host.Vm.FieldVector(edict, _host.Fields.Angles), out info.Forward, out info.Right, out info.Up);
        return 0;
    }

    int ILegacyModels.SurfaceNumPoints(int edict, string model, int surface) { Count(); return 0; }
    QcVector ILegacyModels.SurfacePoint(int edict, string model, int surface, int point) { Count(); return default; }
    QcVector ILegacyModels.SurfacePointAttribute(int edict, string model, int surface, int point, int attribute) { Count(); return default; }
    QcVector ILegacyModels.SurfaceNormal(int edict, string model, int surface) { Count(); return default; }
    string? ILegacyModels.SurfaceTexture(int edict, string model, int surface) { Count(); return null; }
    int ILegacyModels.SurfaceNearPoint(int edict, string model, QcVector point) { Count(); return -1; }
    QcVector ILegacyModels.SurfaceClippedPoint(int edict, string model, int surface, QcVector point) { Count(); return default; }
    int ILegacyModels.SurfaceNumTriangles(int edict, string model, int surface) { Count(); return 0; }
    QcVector ILegacyModels.SurfaceTriangle(int edict, string model, int surface, int triangle) { Count(); return default; }

    int ILegacyModels.SkelCreate(string model) { Count(); return 0; }
    int ILegacyModels.SkelBuild(int skeleton, int edict, string model, float retainFraction, int firstBone, int lastBone) { Count(); return 0; }
    int ILegacyModels.SkelNumBones(int skeleton) { Count(); return 0; }
    string? ILegacyModels.SkelBoneName(int skeleton, int bone) { Count(); return null; }
    int ILegacyModels.SkelBoneParent(int skeleton, int bone) { Count(); return 0; }
    int ILegacyModels.SkelFindBone(int skeleton, string name) { Count(); return 0; }

    bool ILegacyModels.SkelGetBone(int skeleton, int bone, bool absolute, out LegacyBoneTransform transform)
    {
        Count();
        transform = default;
        return false;
    }

    void ILegacyModels.SkelSetBone(int skeleton, int bone, in LegacyBoneTransform transform, bool multiply) => Count();
    void ILegacyModels.SkelMulBones(int skeleton, int firstBone, int lastBone, in LegacyBoneTransform transform) => Count();
    void ILegacyModels.SkelCopyBones(int destination, int source, int firstBone, int lastBone) => Count();
    void ILegacyModels.SkelDelete(int skeleton) => Count();
    int ILegacyModels.FrameForName(string model, string name) { Count(); return -1; }
    float ILegacyModels.FrameDuration(string model, int frame) { Count(); return 0; }

    // ---- effects -----------------------------------------------------------------------------------

    /// <summary>The particles of spawnparticle / delayedparticle handed over since the last
    /// <see cref="ResetCounts"/> (the first 4096), for a test to look at.</summary>
    public List<LegacySpawnParticle> SpawnedParticles { get; } = new();

    /// <summary>Answer "no particle was made" to SpawnParticle, as a full pool does.</summary>
    public bool RefuseParticles { get; set; }

    bool ILegacyEffects.SpawnParticle(in LegacySpawnParticle particle)
    {
        Count();
        if (RefuseParticles) return false;
        if (SpawnedParticles.Count < 4096) SpawnedParticles.Add(particle);
        return true;
    }

    void ILegacyEffects.ParticleEffect(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, int paletteColor) => Count();
    void ILegacyEffects.ParticleTrail(int effect, float count, QcVector start, QcVector end, QcVector velocityMin, QcVector velocityMax, int paletteColor, in LegacyParticleTint tint) => Count();
    void ILegacyEffects.ParticleBox(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, in LegacyParticleTint tint) => Count();
    void ILegacyEffects.TempEntity(in DpTempEntity effect) => Count();
    void ILegacyEffects.SpriteEffect(QcVector origin, string model, int startFrame, int frameCount, float frameRate) => Count();
}
