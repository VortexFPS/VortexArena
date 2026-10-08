// Port of Base/darkplaces/clvm_cmds.c (VM_CL_R_*, VM_draw*, VM_CL_sound, VM_CL_trace*, VM_CL_gettaginfo,
// VM_CL_skel_*), prvm_cmds.c (VM_getsurface*, VM_SetTraceGlobals) and csprogs.c (CSQC_AddRenderEdict):
// not their bodies but the calls they make into the renderer, the sound system and the collision
// world, gathered into one interface at the level the builtins make them.
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// What a server-supplied client program needs that is not arithmetic on its own memory. The host
/// (<see cref="CsqcHost"/>) owns the VM, the network state and the console; this owns pixels, samples
/// and geometry. A later stage implements it on Godot and the collision library; the test suite runs
/// against <see cref="NullLegacyPresentation"/>.
///
/// Vectors are in Quake coordinates and Quake units throughout. Entity numbers are the program's own
/// (edict numbers), not the server's.
/// </summary>
public interface ILegacyPresentation
{
    ILegacyScene Scene { get; }
    ILegacyDraw Draw { get; }
    ILegacySound Sound { get; }
    ILegacyWorld World { get; }
    ILegacyModels Models { get; }
    ILegacyEffects Effects { get; }

    /// <summary>Called once, when a program is loaded on this presentation, before CSQC_Init. An
    /// implementation that needs entity fields beyond what a call passes (an attachment chain, a
    /// skeleton's frame blend) reads them through <paramref name="host"/>.</summary>
    void Attach(CsqcHost host);

    /// <summary>Called when svc_serverinfo names a new level, before the program for it is loaded:
    /// the moment DarkPlaces loads the world model (cl_parse.c CL_BeginDownloads). An implementation
    /// with a world loads it here, because <see cref="ILegacyWorld.Bounds"/> is asked for as the
    /// program is created. The default does nothing.</summary>
    void BeginLevel(CsqcClientState state) { }

    /// <summary>Called once per level when its program has been loaded and CSQC_Init has returned (or the
    /// server named no program): the end of cl_parse.c CL_BeginDownloads, by which point DarkPlaces has every
    /// model and sound of the level in memory. An implementation that draws finishes loading what the server
    /// and the program precached here, so that the first sight of a thing during play does not stop the game
    /// to load it. The default does nothing.</summary>
    void EndLevelLoad(CsqcClientState state) { }
}

/// <summary>A presentation that counts the calls it receives, for the reports of a headless run.</summary>
public interface ILegacyCallCounts
{
    /// <summary>Calls received, by interface member name.</summary>
    IReadOnlyDictionary<string, long> Calls { get; }
    void ResetCounts();
}

/// <summary>One entity submitted for this frame: the fields CSQC_AddRenderEdict reads.</summary>
public struct LegacyRenderEntity
{
    /// <summary>Entity number, or 0 for a one-off submitted with addentity (R_AddEntity), which has no
    /// persistent render state.</summary>
    public int Edict;
    public string Model;
    public QcVector Origin, Angles;
    /// <summary>v_forward, v_right and v_up as the entity's predraw left them; they replace
    /// <see cref="Angles"/> when <see cref="RenderFlags"/> has RF_USEAXIS (16).</summary>
    public QcVector AxisForward, AxisRight, AxisUp;
    public int Skin, RenderFlags, Effects, ColorMap;
    /// <summary>0 means opaque and unscaled (the C replaces 0 with 1).</summary>
    public float Alpha, Scale;
    public QcVector ColorMod, GlowMod;
    public float Frame, Frame2, Frame3, Frame4, LerpFrac, LerpFrac3, LerpFrac4;
    public float Frame1Time, Frame2Time, Frame3Time, Frame4Time;
    public int SkeletonIndex;
    public int TagEntity, TagIndex;
    public float ShaderTime;
    public QcVector ModelLightAmbient, ModelLightDiffuse, ModelLightDir;
}

/// <summary>adddynamiclight's arguments, with the orientation the C takes from v_forward/v_right/v_up.</summary>
public struct LegacyDynamicLight
{
    public QcVector Origin, Color;
    public float Radius;
    /// <summary>Light style, or -1.</summary>
    public int Style;
    public string? Cubemap;
    /// <summary>PFLAGS_*: 1 no shadow, 2 corona, 128 full dynamic.</summary>
    public int Flags;
    public QcVector Forward, Right, Up;
}

/// <summary>One R_PolygonVertex.</summary>
public struct LegacyPolygonVertex
{
    public QcVector Position, TexCoord, Color;
    public float Alpha;
}

/// <summary>V_CalcRefdef's inputs, read from the entity the program names.</summary>
public struct LegacyRefdefInput
{
    public int Edict;
    public QcVector Origin, Angles, Velocity;
    public float ViewHeight;
    public bool OnGround, Teleported, Jumping, Dead, Intermission;
}

public interface ILegacyScene
{
    /// <summary>#300 clearscene: empty the entity and light lists and reset the view to the engine's.</summary>
    void ClearScene();
    /// <summary>#301 addentities, first step (CSQC_RelinkAllEntities): add the engine's own entities -
    /// the world, static entities, beams and effects always; the server's network entities if
    /// <paramref name="drawMask"/> has ENTMASK_ENGINE (1), and the view model with ENTMASK_ENGINEVIEWMODELS (2).</summary>
    void AddEngineEntities(int drawMask);
    /// <summary>#301 addentities (once per drawn entity, after its think and predraw ran) and #302
    /// addentity. False if the entity has no drawable model or the scene is full.</summary>
    bool AddEntity(in LegacyRenderEntity entity);
    /// <summary>#303 setproperty. <paramref name="property"/> is a VF_* number (csprogs.h); a float
    /// property is in <c>a.X</c>, VF_VIEWPORT takes both vectors. False for a property it does not know.</summary>
    bool SetProperty(int property, QcVector a, QcVector b);
    /// <summary>#309 getproperty (and #303 with one argument). False for an unknown property.</summary>
    bool GetProperty(int property, out QcVector value);
    /// <summary>#304 renderscene: draw what was added, with the view as set.</summary>
    void RenderScene();
    /// <summary>#305 adddynamiclight.</summary>
    void AddDynamicLight(in LegacyDynamicLight light);
    /// <summary>#306-#308 R_BeginPolygon / R_PolygonVertex / R_EndPolygon, delivered whole.</summary>
    void DrawPolygon(string texture, int drawFlags, bool is2D, ReadOnlySpan<LegacyPolygonVertex> vertices);
    /// <summary>#310 cs_unproject: a point in virtual-screen pixels plus depth, to world space.</summary>
    QcVector Unproject(QcVector screen);
    /// <summary>#311 cs_project: the inverse; Z of the result is the depth.</summary>
    QcVector Project(QcVector world);
    /// <summary>#640 V_CalcRefdef: have the engine compute its usual first-person view from an entity.</summary>
    void CalcRefdef(in LegacyRefdefInput input);
    /// <summary>#35 lightstyle.</summary>
    void SetLightStyle(int style, string map);
    /// <summary>#92 getlight: returns ambient + diffuse/2, and the three parts separately.</summary>
    QcVector GetLight(QcVector point, int flags, out QcVector ambient, out QcVector diffuse, out QcVector direction);
}

/// <summary>A drawstring, drawcolorcodedstring or drawcharacter call.</summary>
public struct LegacyText
{
    public QcVector Position, Scale, Color;
    public string Text;
    public float Alpha;
    public int Flags;
    /// <summary>The drawfont global: a font slot.</summary>
    public int Font;
    /// <summary>The drawfontscale global, '1 1 0' when unset.</summary>
    public QcVector FontScale;
    /// <summary>False for drawcolorcodedstring, which interprets ^-codes; true for the other two.</summary>
    public bool IgnoreColorCodes;
}

/// <summary>A drawpic, drawsubpic or drawrotpic call.</summary>
public struct LegacyPicture
{
    public string Name;
    public QcVector Position, Size, Color;
    public float Alpha;
    public int Flags;
    /// <summary>drawsubpic: the source rectangle as fractions of the picture. Whole picture: 0,0 and 1,1.</summary>
    public QcVector SourcePosition, SourceSize;
    /// <summary>drawrotpic: the pivot within the picture, and the angle in degrees.</summary>
    public QcVector RotationOrigin;
    public float Angle;
}

public interface ILegacyDraw
{
    /// <summary>#317 precache_pic and #501 ReadPicture: whether a picture of that name can be loaded.</summary>
    bool PictureExists(string name);
    /// <summary>#501 ReadPicture, when no such picture exists: a low-quality JPEG the server sent in its place.</summary>
    void DefinePicture(string name, ReadOnlySpan<byte> jpeg);
    /// <summary>#318 draw_getimagesize: width and height in X and Y, '0 0 0' if it cannot be loaded.</summary>
    QcVector ImageSize(string name);
    /// <summary>#319 freepic.</summary>
    void FreePicture(string name);
    /// <summary>#315 drawline.</summary>
    void Line(float width, QcVector from, QcVector to, QcVector color, float alpha, int flags);
    /// <summary>#320 drawcharacter, #321 drawstring, #326 drawcolorcodedstring. Returns the colour in
    /// effect at the end of the text (DrawQ_Color), which the six-argument drawcolorcodedstring returns.</summary>
    QcVector Text(in LegacyText text);
    /// <summary>#322 drawpic, #328 drawsubpic, #329 drawrotpic.</summary>
    void Picture(in LegacyPicture picture);
    /// <summary>#323 drawfill.</summary>
    void Fill(QcVector position, QcVector size, QcVector color, float alpha, int flags);
    /// <summary>#324 drawsetcliparea, already clamped to the virtual screen.</summary>
    void SetClipArea(float x, float y, float width, float height);
    /// <summary>#325 drawresetcliparea.</summary>
    void ResetClipArea();
    /// <summary>#327 stringwidth: DrawQ_TextWidth for a character cell of <paramref name="scale"/>.</summary>
    float StringWidth(string text, bool ignoreColorCodes, QcVector scale, int font, QcVector fontScale);
    /// <summary>#356 findfont: the slot of a loaded font, or -1.</summary>
    int FindFont(string name);
    /// <summary>#357 loadfont. <paramref name="slot"/> is -1 to pick one. Returns the slot, or -1.</summary>
    int LoadFont(string name, string files, string sizes, int slot, float scale, float verticalOffset);
}

public interface ILegacySound
{
    /// <summary>#19/#76 precache_sound. False if the sample cannot be loaded (a warning, nothing more).</summary>
    bool Precache(string sample);
    /// <summary>#8 sound (and #483 pointsound with entity 0, and the engine's own effect sounds with -1).
    /// <paramref name="speed"/> is a playback-rate multiplier, 1 normal.</summary>
    void Start(int edict, int channel, string sample, QcVector origin, float volume, float attenuation, float startPosition, int flags, float speed);
    /// <summary>#74 ambientsound: a looping sound fixed in the world.</summary>
    void StartStatic(QcVector origin, string sample, float volume, float attenuation);
    /// <summary>#177 localsound. False if it could not be played.</summary>
    bool Local(string sample, int channel, float volume);
    /// <summary>#351 SetListener: for this frame the listener is here rather than at the view.</summary>
    void SetListener(QcVector origin, QcVector forward, QcVector right, QcVector up);
    /// <summary>#533 getsoundtime: seconds into the sample playing on that entity channel, or -1.</summary>
    float ChannelPosition(int edict, int channel);
    /// <summary>#534 soundlength: seconds, or -1 if the sample cannot be loaded.</summary>
    float Length(string sample);
}

/// <summary>The result of a trace: everything VM_SetTraceGlobals copies into the trace_* globals.</summary>
public struct LegacyTrace
{
    public bool AllSolid, StartSolid, InWater, InOpen;
    public float Fraction;
    public QcVector EndPos, PlaneNormal;
    public float PlaneDist;
    /// <summary>The entity hit, or 0 for the world or nothing.</summary>
    public int Entity;
    /// <summary>The server entity hit when it was a networked one the program has no entity for (trace_networkentity), else 0.</summary>
    public int NetworkEntity;
    public int StartContents, HitContents, HitQ3SurfaceFlags;
    public string? HitTextureName;
}

public interface ILegacyWorld
{
    /// <summary>The map's bounds, for the world entity's mins and maxs (cl.world.mins / maxs).</summary>
    void Bounds(out QcVector mins, out QcVector maxs);
    /// <summary>How far droptofloor looks down: 4096 on a Quake 3 map, 256 on Quake, 128 on Quake 2.</summary>
    float DropToFloorDistance { get; }
    /// <summary>#16 traceline (<paramref name="isLine"/>, zero box) and #90 tracebox; also every trace
    /// #64 tracetoss, #34 droptofloor and #40 checkbottom make. <paramref name="moveType"/> is MOVE_*
    /// (0 normal, 1 no monsters, 2 missile, 3 world only); <paramref name="hitContentsMask"/> is
    /// CL_GenericHitSuperContentsMask of the ignored entity.</summary>
    LegacyTrace Trace(QcVector start, QcVector mins, QcVector maxs, QcVector end, int moveType, int ignoreEdict, int hitContentsMask, bool isLine);
    /// <summary>#41 pointcontents (and #40): the SUPERCONTENTS_* bits at a point.</summary>
    int PointSuperContents(QcVector point);
    /// <summary>#240 checkpvs: 1 if a box is potentially visible from a point, 0 if not, 2 if the
    /// point is outside the map, 3 if the map has no visibility data.</summary>
    int CheckPvs(QcVector viewPosition, QcVector mins, QcVector maxs);
    /// <summary>#32 walkmove (CL_movestep): step an entity along the ground. Returns whether it moved.</summary>
    bool MoveStep(int edict, QcVector move, bool setTrace);
    /// <summary>CL_LinkEdict: an entity's box changed (setorigin, setsize, setmodel, copyentity).</summary>
    void LinkEdict(int edict, QcVector absMin, QcVector absMax);
    /// <summary>CLVM_free_edict: an entity is being removed.</summary>
    void UnlinkEdict(int edict);
}

/// <summary>gettaginfo's results (CL_GetTagMatrix and CL_GetExtendedTagInfo).</summary>
public struct LegacyTagInfo
{
    /// <summary>The tag in world space.</summary>
    public QcVector Origin, Forward, Right, Up;
    /// <summary>The tag relative to its parent bone.</summary>
    public QcVector LocalOffset, LocalForward, LocalRight, LocalUp;
    public int Parent;
    public string? Name;
}

/// <summary>A bone transform as skel_get_bonerel / skel_set_bone exchange it: origin plus three axes.</summary>
public struct LegacyBoneTransform
{
    public QcVector Origin, Forward, Right, Up;
}

public interface ILegacyModels
{
    /// <summary>Whether a model can be loaded, and its normal (unrotated) bounds. An implementation
    /// that knows the model exists but not its size answers true with zero bounds.</summary>
    bool TryGetBounds(string model, out QcVector mins, out QcVector maxs);

    /// <summary>#451 gettagindex, #443 setattachment, #268 skel_find_bone: 1-based, 0 if there is none.</summary>
    int TagIndex(string model, int skin, string tagName);
    /// <summary>#452 gettaginfo. Returns CL_GetTagMatrix's code: 0 ok, 1 world entity, 2 free entity,
    /// 3 no model, 4 no such tag, 5 attachment loop.</summary>
    int TagInfo(int edict, int tagIndex, out LegacyTagInfo info);

    // #434-#439, #486, #628, #629: DP_QC_GETSURFACE. Points are in world space, so the entity is named.
    int SurfaceNumPoints(int edict, string model, int surface);
    QcVector SurfacePoint(int edict, string model, int surface, int point);
    /// <summary>#486: attribute 0 position, 1 S tangent, 2 T tangent, 3 normal, 4 texcoord, 5 lightmap texcoord, 6 colour.</summary>
    QcVector SurfacePointAttribute(int edict, string model, int surface, int point, int attribute);
    QcVector SurfaceNormal(int edict, string model, int surface);
    string? SurfaceTexture(int edict, string model, int surface);
    /// <summary>The surface nearest a point, or -1.</summary>
    int SurfaceNearPoint(int edict, string model, QcVector point);
    QcVector SurfaceClippedPoint(int edict, string model, int surface, QcVector point);
    int SurfaceNumTriangles(int edict, string model, int surface);
    QcVector SurfaceTriangle(int edict, string model, int surface, int triangle);

    // #263-#277: FTE_CSQC_SKELETONOBJECTS. Skeleton and bone numbers are the program's (1-based; 0 fails).
    int SkelCreate(string model);
    /// <summary>#264 skel_build: blend the entity's current animation into bones first..last. Returns the skeleton, or 0.</summary>
    int SkelBuild(int skeleton, int edict, string model, float retainFraction, int firstBone, int lastBone);
    int SkelNumBones(int skeleton);
    string? SkelBoneName(int skeleton, int bone);
    int SkelBoneParent(int skeleton, int bone);
    int SkelFindBone(int skeleton, string name);
    /// <summary>#269 skel_get_bonerel (relative to the parent) and #270 skel_get_boneabs (model space).</summary>
    bool SkelGetBone(int skeleton, int bone, bool absolute, out LegacyBoneTransform transform);
    /// <summary>#271 skel_set_bone, or #272 skel_mul_bone when <paramref name="multiply"/>.</summary>
    void SkelSetBone(int skeleton, int bone, in LegacyBoneTransform transform, bool multiply);
    /// <summary>#273 skel_mul_bones.</summary>
    void SkelMulBones(int skeleton, int firstBone, int lastBone, in LegacyBoneTransform transform);
    /// <summary>#274 skel_copybones.</summary>
    void SkelCopyBones(int destination, int source, int firstBone, int lastBone);
    /// <summary>#275 skel_delete.</summary>
    void SkelDelete(int skeleton);
    /// <summary>#276 frameforname: the frame group of that name, or -1.</summary>
    int FrameForName(string model, string name);
    /// <summary>#277 frameduration: seconds, 0 if the frame does not exist.</summary>
    float FrameDuration(string model, int frame);
}

/// <summary>boxparticles' optional tint, read from the particles_* globals.</summary>
public struct LegacyParticleTint
{
    public QcVector ColorMin, ColorMax;
    public float AlphaMin, AlphaMax, Fade;
}

public interface ILegacyEffects
{
    /// <summary>#337 pointparticles, #48 particle (effect 35, SVC_PARTICLE) and svc_pointparticles:
    /// CL_ParticleEffect over the box <paramref name="originMin"/>..<paramref name="originMax"/>.
    /// <paramref name="effect"/> is a <see cref="CsqcEffectInfo"/> number.</summary>
    void ParticleEffect(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, int paletteColor);
    /// <summary>#336 trailparticles and #502 boxparticles in trail mode: CL_ParticleTrail.</summary>
    void ParticleTrail(int effect, float count, QcVector start, QcVector end, QcVector velocityMin, QcVector velocityMax, int paletteColor, in LegacyParticleTint tint);
    /// <summary>#502 boxparticles: CL_ParticleBox.</summary>
    void ParticleBox(int effect, float count, QcVector originMin, QcVector originMax, QcVector velocityMin, QcVector velocityMax, in LegacyParticleTint tint);
    /// <summary>The te_* builtins (#405-#431, #433, #457) and the engine's own svc_temp_entity effects:
    /// particles, a light flash, a sound or a beam, depending on the type.</summary>
    void TempEntity(in DpTempEntity effect);
    /// <summary>#404 effect: a sprite model played once at a point.</summary>
    void SpriteEffect(QcVector origin, string model, int startFrame, int frameCount, float frameRate);
}
