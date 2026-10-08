// Port of Base/darkplaces/prvm_offsets.h (the PRVM_DECLARE_client* entries) and the lookup half of
// prvm_edict.c PRVM_Prog_Load that resolves them.
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// Cell offsets of the entity fields the engine reads and writes. DarkPlaces appends any of these a
/// program does not declare (cl_reqfields), so after construction every one is valid; the exception is
/// <see cref="ViewOfs"/>, which the engine only reads if the program has it.
/// </summary>
public sealed class CsqcFieldOffsets
{
    // cl_reqfields: clientfieldedict, clientfieldfloat, clientfieldfunction, clientfieldstring, clientfieldvector.
    private static readonly (string Name, QcType Type)[] Required =
    {
        ("aiment", QcType.Entity), ("chain", QcType.Entity), ("enemy", QcType.Entity), ("groundentity", QcType.Entity),
        ("owner", QcType.Entity), ("tag_entity", QcType.Entity),
        ("alpha", QcType.Float), ("bouncefactor", QcType.Float), ("bouncestop", QcType.Float), ("colormap", QcType.Float),
        ("clipgroup", QcType.Float), ("dphitcontentsmask", QcType.Float), ("drawmask", QcType.Float), ("effects", QcType.Float),
        ("entnum", QcType.Float), ("flags", QcType.Float), ("frame", QcType.Float), ("frame1time", QcType.Float),
        ("frame2", QcType.Float), ("frame2time", QcType.Float), ("frame3", QcType.Float), ("frame3time", QcType.Float),
        ("frame4", QcType.Float), ("frame4time", QcType.Float), ("gravity", QcType.Float), ("ideal_yaw", QcType.Float),
        ("idealpitch", QcType.Float), ("geomtype", QcType.Float), ("jointtype", QcType.Float), ("forcetype", QcType.Float),
        ("lerpfrac", QcType.Float), ("lerpfrac3", QcType.Float), ("lerpfrac4", QcType.Float), ("mass", QcType.Float),
        ("massofs", QcType.Vector), ("friction", QcType.Float), ("maxcontacts", QcType.Float), ("erp", QcType.Float),
        ("modelindex", QcType.Float), ("movetype", QcType.Float), ("nextthink", QcType.Float), ("pitch_speed", QcType.Float),
        ("pmove_flags", QcType.Float), ("renderflags", QcType.Float), ("scale", QcType.Float), ("modelscale_vec", QcType.Vector),
        ("shadertime", QcType.Float), ("skeletonindex", QcType.Float), ("skin", QcType.Float), ("solid", QcType.Float),
        ("tag_index", QcType.Float), ("userwavefunc_param0", QcType.Float), ("userwavefunc_param1", QcType.Float),
        ("userwavefunc_param2", QcType.Float), ("userwavefunc_param3", QcType.Float), ("yaw_speed", QcType.Float),
        ("blocked", QcType.Function), ("camera_transform", QcType.Function), ("predraw", QcType.Function),
        ("think", QcType.Function), ("touch", QcType.Function), ("use", QcType.Function),
        ("classname", QcType.String), ("message", QcType.String), ("model", QcType.String), ("netname", QcType.String),
        ("absmax", QcType.Vector), ("absmin", QcType.Vector), ("angles", QcType.Vector), ("avelocity", QcType.Vector),
        ("colormod", QcType.Vector), ("glowmod", QcType.Vector), ("maxs", QcType.Vector), ("mins", QcType.Vector),
        ("movedir", QcType.Vector), ("oldorigin", QcType.Vector), ("origin", QcType.Vector), ("size", QcType.Vector),
        ("velocity", QcType.Vector), ("modellight_ambient", QcType.Vector), ("modellight_diffuse", QcType.Vector),
        ("modellight_dir", QcType.Vector),
    };

    public readonly int Chain, GroundEntity, TagEntity;
    public readonly int Alpha, ColorMap, DpHitContentsMask, DrawMask, Effects, EntNum, Flags, Gravity, ModelIndex,
        NextThink, PmoveFlags, RenderFlags, Scale, ShaderTime, SkeletonIndex, Skin, Solid, TagIndex;
    public readonly int Frame, Frame2, Frame3, Frame4, Frame1Time, Frame2Time, Frame3Time, Frame4Time, LerpFrac, LerpFrac3, LerpFrac4;
    public readonly int Predraw, Think, CameraTransform;
    public readonly int ClassName, Message, Model;
    public readonly int AbsMax, AbsMin, Angles, AVelocity, ColorMod, GlowMod, Maxs, Mins, Origin, Size, Velocity;
    public readonly int ModelLightAmbient, ModelLightDiffuse, ModelLightDir;
    /// <summary>Not a required field: -1 if the program has no .view_ofs.</summary>
    public readonly int ViewOfs;

    /// <summary>Resolves every offset, appending the fields the program lacks. Must run before the
    /// first entity is spawned (<see cref="QcVm.EnsureField"/>).</summary>
    public CsqcFieldOffsets(QcVm vm)
    {
        foreach ((string name, QcType type) in Required) vm.EnsureField(name, type);
        int F(string name) => vm.FindField(name)!.Offset;

        Chain = F("chain"); GroundEntity = F("groundentity"); TagEntity = F("tag_entity");
        Alpha = F("alpha"); ColorMap = F("colormap"); DpHitContentsMask = F("dphitcontentsmask"); DrawMask = F("drawmask");
        Effects = F("effects"); EntNum = F("entnum"); Flags = F("flags"); Gravity = F("gravity"); ModelIndex = F("modelindex");
        NextThink = F("nextthink"); PmoveFlags = F("pmove_flags"); RenderFlags = F("renderflags"); Scale = F("scale");
        ShaderTime = F("shadertime"); SkeletonIndex = F("skeletonindex"); Skin = F("skin"); Solid = F("solid"); TagIndex = F("tag_index");
        Frame = F("frame"); Frame2 = F("frame2"); Frame3 = F("frame3"); Frame4 = F("frame4");
        Frame1Time = F("frame1time"); Frame2Time = F("frame2time"); Frame3Time = F("frame3time"); Frame4Time = F("frame4time");
        LerpFrac = F("lerpfrac"); LerpFrac3 = F("lerpfrac3"); LerpFrac4 = F("lerpfrac4");
        Predraw = F("predraw"); Think = F("think"); CameraTransform = F("camera_transform");
        ClassName = F("classname"); Message = F("message"); Model = F("model");
        AbsMax = F("absmax"); AbsMin = F("absmin"); Angles = F("angles"); AVelocity = F("avelocity");
        ColorMod = F("colormod"); GlowMod = F("glowmod"); Maxs = F("maxs"); Mins = F("mins"); Origin = F("origin");
        Size = F("size"); Velocity = F("velocity");
        ModelLightAmbient = F("modellight_ambient"); ModelLightDiffuse = F("modellight_diffuse"); ModelLightDir = F("modellight_dir");

        // A vector field needs three cells; a program that declares view_ofs as something smaller at
        // the very end of the entity would otherwise put two of them past it.
        QcDef? viewOfs = vm.FindField("view_ofs");
        ViewOfs = viewOfs is not null && viewOfs.Offset + 3 <= vm.EntityFields ? viewOfs.Offset : -1;
    }
}

/// <summary>
/// Cell offsets of the globals the engine reads and writes, each -1 if the program does not declare
/// it. (DarkPlaces allocates the missing ones; here a missing global is simply not written, which the
/// program cannot tell apart since it has no name for the cell.)
/// </summary>
public sealed class CsqcGlobalOffsets
{
    public readonly int Self, Other, Time, FrameTime, ClTime;
    public readonly int ServerCommandFrame, ClientCommandFrame, ServerTime, ServerPrevTime, ServerDeltaTime;
    public readonly int PlayerLocalEntNum, PlayerLocalNum, MaxClients, Intermission, SbShowScores, Coop, Deathmatch, MapName;
    public readonly int ViewAngles, ViewPunchAngle, ViewPunchVector;
    public readonly int PmoveOrg, PmoveVel, PmoveOnGround, PmoveInWater, PmoveMins, PmoveMaxs;
    public readonly int InputAngles, InputButtons, InputMoveValues, InputTimeLength;
    public readonly int VForward, VRight, VUp;
    public readonly int TraceAllSolid, TraceStartSolid, TraceFraction, TraceInWater, TraceInOpen, TraceEndPos,
        TracePlaneNormal, TracePlaneDist, TraceEnt, TraceDpStartContents, TraceDpHitContents,
        TraceDpHitQ3SurfaceFlags, TraceDpHitTextureName, TraceNetworkEntity;
    public readonly int DmgTake, DmgSave, DmgOrigin;
    public readonly int DrawFont, DrawFontScale;
    public readonly int GetTagInfoParent, GetTagInfoName, GetTagInfoForward, GetTagInfoRight, GetTagInfoUp, GetTagInfoOffset;
    public readonly int GetLightAmbient, GetLightDiffuse, GetLightDir;
    public readonly int ParticlesAlphaMin, ParticlesAlphaMax, ParticlesColorMin, ParticlesColorMax, ParticlesFade;
    public readonly int SoundStartTime;

    public CsqcGlobalOffsets(QcVm vm)
    {
        // One cell for a scalar, three for a vector: the offset is kept only if all of them exist, so
        // no later write can land outside the globals whatever the program's defs claim.
        int S(string name) => vm.FindGlobal(name) is { } d && d.Offset >= ProgsFile.ReservedOfs && d.Offset < vm.NumGlobals ? d.Offset : -1;
        int V(string name) => vm.FindGlobal(name) is { } d && d.Offset >= ProgsFile.ReservedOfs && d.Offset + 3 <= vm.NumGlobals ? d.Offset : -1;

        Self = S("self"); Other = S("other"); Time = S("time"); FrameTime = S("frametime"); ClTime = S("cltime");
        ServerCommandFrame = S("servercommandframe"); ClientCommandFrame = S("clientcommandframe");
        ServerTime = S("servertime"); ServerPrevTime = S("serverprevtime"); ServerDeltaTime = S("serverdeltatime");
        PlayerLocalEntNum = S("player_localentnum"); PlayerLocalNum = S("player_localnum"); MaxClients = S("maxclients");
        Intermission = S("intermission"); SbShowScores = S("sb_showscores"); Coop = S("coop"); Deathmatch = S("deathmatch");
        MapName = S("mapname");
        ViewAngles = V("view_angles"); ViewPunchAngle = V("view_punchangle"); ViewPunchVector = V("view_punchvector");
        PmoveOrg = V("pmove_org"); PmoveVel = V("pmove_vel");
        // Declared as vectors in prvm_offsets.h but written as single floats by CSQC_SetGlobals.
        PmoveOnGround = S("pmove_onground"); PmoveInWater = S("pmove_inwater");
        PmoveMins = V("pmove_mins"); PmoveMaxs = V("pmove_maxs");
        InputAngles = V("input_angles"); InputButtons = S("input_buttons"); InputMoveValues = V("input_movevalues");
        InputTimeLength = S("input_timelength");
        VForward = V("v_forward"); VRight = V("v_right"); VUp = V("v_up");
        TraceAllSolid = S("trace_allsolid"); TraceStartSolid = S("trace_startsolid"); TraceFraction = S("trace_fraction");
        TraceInWater = S("trace_inwater"); TraceInOpen = S("trace_inopen"); TraceEndPos = V("trace_endpos");
        TracePlaneNormal = V("trace_plane_normal"); TracePlaneDist = S("trace_plane_dist"); TraceEnt = S("trace_ent");
        TraceDpStartContents = S("trace_dpstartcontents"); TraceDpHitContents = S("trace_dphitcontents");
        TraceDpHitQ3SurfaceFlags = S("trace_dphitq3surfaceflags"); TraceDpHitTextureName = S("trace_dphittexturename");
        TraceNetworkEntity = S("trace_networkentity");
        DmgTake = S("dmg_take"); DmgSave = S("dmg_save"); DmgOrigin = V("dmg_origin");
        DrawFont = S("drawfont"); DrawFontScale = V("drawfontscale");
        GetTagInfoParent = S("gettaginfo_parent"); GetTagInfoName = S("gettaginfo_name");
        GetTagInfoForward = V("gettaginfo_forward"); GetTagInfoRight = V("gettaginfo_right");
        GetTagInfoUp = V("gettaginfo_up"); GetTagInfoOffset = V("gettaginfo_offset");
        GetLightAmbient = V("getlight_ambient"); GetLightDiffuse = V("getlight_diffuse"); GetLightDir = V("getlight_dir");
        ParticlesAlphaMin = S("particles_alphamin"); ParticlesAlphaMax = S("particles_alphamax");
        ParticlesColorMin = V("particles_colormin"); ParticlesColorMax = V("particles_colormax"); ParticlesFade = S("particles_fade");
        SoundStartTime = S("sound_starttime");
    }
}
