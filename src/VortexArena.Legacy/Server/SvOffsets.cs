// Port of Base/darkplaces/prvm_offsets.h (the PRVM_DECLARE_server* entries), progdefs.h (entvars_t,
// globalvars_t: which of them a stock program is guaranteed to have) and the lookup half of
// prvm_edict.c PRVM_Prog_Load that resolves them, with sv_main.c sv_reqfields / sv_reqglobals.
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

/// <summary>
/// Cell offsets of the entity fields the server engine reads and writes. DarkPlaces appends any of
/// these a program does not declare (sv_reqfields), so after construction every one is valid.
/// </summary>
public sealed class SvFieldOffsets
{
    private static readonly string[] Floats =
    {
        "SendFlags", "Version", "alpha", "ammo_cells", "ammo_cells1", "ammo_lava_nails", "ammo_multi_rockets", "ammo_nails",
        "ammo_nails1", "ammo_plasma", "ammo_rockets", "ammo_rockets1", "ammo_shells", "ammo_shells1", "armortype", "armorvalue",
        "bouncefactor", "bouncestop", "button0", "button1", "button2", "button3", "button4", "button5", "button6", "button7",
        "button8", "button9", "button10", "button11", "button12", "button13", "button14", "button15", "button16", "buttonchat",
        "buttonuse", "clientcolors", "clipgroup", "colormap", "currentammo", "cursor_active", "deadflag", "disableclientprediction",
        "discardabledemo", "dmg_save", "dmg_take", "dphitcontentsmask", "effects", "fixangle", "flags", "frags", "frame",
        "frame1time", "frame2", "frame2time", "frame3", "frame3time", "frame4", "frame4time", "fullbright", "glow_color",
        "glow_size", "glow_trail", "gravity", "health", "ideal_yaw", "idealpitch", "impulse", "items", "items2", "geomtype",
        "jointtype", "forcetype", "lerpfrac", "lerpfrac3", "lerpfrac4", "light_lev", "ltime", "mass", "friction", "maxcontacts",
        "erp", "max_health", "modelflags", "modelindex", "movetype", "nextthink", "pflags", "ping", "ping_movementloss",
        "ping_packetloss", "pitch_speed", "pmodel", "renderamt", "scale", "sendcomplexanimation", "skeletonindex", "skin", "solid",
        "sounds", "spawnflags", "style", "tag_index", "takedamage", "team", "teleport_time", "traileffectnum", "viewzoom",
        "waterlevel", "watertype", "weapon", "weaponframe", "yaw_speed", "crypto_idfp_signed",
    };
    private static readonly string[] Strings =
    {
        "classname", "clientstatus", "crypto_encryptmethod", "crypto_idfp", "crypto_keyfp", "crypto_mykeyfp", "crypto_signmethod",
        "message", "model", "netaddress", "netname", "noise", "noise1", "noise2", "noise3", "playermodel", "playerskin", "target",
        "targetname", "weaponmodel",
    };
    private static readonly string[] Vectors =
    {
        "massofs", "modelscale_vec", "absmax", "absmin", "angles", "avelocity", "color", "colormod", "cursor_screen",
        "cursor_trace_endpos", "cursor_trace_start", "glowmod", "maxs", "mins", "movedir", "movement", "oldorigin", "origin",
        "punchangle", "punchvector", "size", "v_angle", "velocity", "view_ofs",
    };
    private static readonly string[] Edicts =
    {
        "aiment", "chain", "clientcamera", "cursor_trace_ent", "dmg_inflictor", "drawonlytoclient", "enemy",
        "exteriormodeltoclient", "goalentity", "groundentity", "nodrawtoclient", "owner", "tag_entity", "viewmodelforclient",
    };
    private static readonly string[] Functions =
    {
        "SendEntity", "blocked", "camera_transform", "contentstransition", "customizeentityforclient", "movetypesteplandevent",
        "think", "touch", "use",
    };

    // floats
    public readonly int SendFlags, Version, Alpha, ArmorType, ArmorValue, BounceFactor, BounceStop, Button0, Button1, Button2, Button3,
        Button4, Button5, Button6, Button7, Button8, Button9, Button10, Button11, Button12, Button13, Button14, Button15, Button16,
        ButtonChat, ButtonUse, ClientColors, ClipGroup, ColorMap, CurrentAmmo, CursorActive, DeadFlag, DisableClientPrediction,
        DiscardableDemo, DmgSave, DmgTake, DpHitContentsMask, Effects, FixAngle, Flags, Frags, Frame, Frame1Time, Frame2,
        Frame2Time, Frame3, Frame3Time, Frame4, Frame4Time, FullBright, GlowColor, GlowSize, GlowTrail, Gravity, Health, IdealYaw,
        IdealPitch, Impulse, Items, Items2, LerpFrac, LerpFrac3, LerpFrac4, LightLev, LTime, MaxHealth, ModelFlags, ModelIndex,
        MoveType, NextThink, PFlags, Ping, PingMovementLoss, PingPacketLoss, PitchSpeed, PModel, RenderAmt, Scale,
        SendComplexAnimation, SkeletonIndex, Skin, Solid, Sounds, SpawnFlags, Style, TagIndex, TakeDamage, Team, TeleportTime,
        TrailEffectNum, ViewZoom, WaterLevel, WaterType, Weapon, WeaponFrame, YawSpeed, CryptoIdfpSigned,
        AmmoShells, AmmoNails, AmmoRockets, AmmoCells;
    // strings
    public readonly int ClassName, ClientStatus, CryptoEncryptMethod, CryptoIdfp, CryptoKeyfp, CryptoMyKeyfp, CryptoSignMethod,
        Message, Model, NetAddress, NetName, PlayerModel, PlayerSkin, Target, TargetName, WeaponModel;
    // vectors
    public readonly int AbsMax, AbsMin, Angles, AVelocity, Color, ColorMod, CursorScreen, CursorTraceEndPos, CursorTraceStart, GlowMod,
        Maxs, Mins, MoveDir, Movement, OldOrigin, Origin, PunchAngle, PunchVector, Size, VAngle, Velocity, ViewOfs;
    // entities
    public readonly int AimEnt, Chain, ClientCamera, CursorTraceEnt, DmgInflictor, DrawOnlyToClient, Enemy, ExteriorModelToClient,
        GoalEntity, GroundEntity, NoDrawToClient, Owner, TagEntity, ViewModelForClient;
    // functions
    public readonly int SendEntity, Blocked, CameraTransform, ContentsTransition, CustomizeEntityForClient, MoveTypeStepLandEvent,
        Think, Touch, Use;

    /// <summary>Resolves every offset, appending the fields the program lacks. Must run before the
    /// first entity beyond the world is spawned (<see cref="QcVm.EnsureField"/>).</summary>
    public SvFieldOffsets(QcVm vm)
    {
        foreach (string name in Floats) vm.EnsureField(name, QcType.Float);
        foreach (string name in Strings) vm.EnsureField(name, QcType.String);
        foreach (string name in Vectors) vm.EnsureField(name, QcType.Vector);
        foreach (string name in Edicts) vm.EnsureField(name, QcType.Entity);
        foreach (string name in Functions) vm.EnsureField(name, QcType.Function);

        // A program's own definition of an engine field can be too small to hold it (a float named
        // "origin"): such a field is unusable, and the program is refused rather than run with the
        // engine writing three cells over one.
        int F(string name, int cells = 1)
        {
            QcDef def = vm.FindField(name)!;
            if (def.Offset < 0 || def.Offset + cells > vm.EntityFields)
                throw new QcRuntimeException($"server: field '{name}' does not fit in an entity");
            return def.Offset;
        }
        int V(string name) => F(name, 3);

        SendFlags = F("SendFlags"); Version = F("Version"); Alpha = F("alpha"); ArmorType = F("armortype"); ArmorValue = F("armorvalue");
        BounceFactor = F("bouncefactor"); BounceStop = F("bouncestop");
        Button0 = F("button0"); Button1 = F("button1"); Button2 = F("button2"); Button3 = F("button3"); Button4 = F("button4");
        Button5 = F("button5"); Button6 = F("button6"); Button7 = F("button7"); Button8 = F("button8"); Button9 = F("button9");
        Button10 = F("button10"); Button11 = F("button11"); Button12 = F("button12"); Button13 = F("button13"); Button14 = F("button14");
        Button15 = F("button15"); Button16 = F("button16"); ButtonChat = F("buttonchat"); ButtonUse = F("buttonuse");
        ClientColors = F("clientcolors"); ClipGroup = F("clipgroup"); ColorMap = F("colormap"); CurrentAmmo = F("currentammo");
        CursorActive = F("cursor_active"); DeadFlag = F("deadflag"); DisableClientPrediction = F("disableclientprediction");
        DiscardableDemo = F("discardabledemo"); DmgSave = F("dmg_save"); DmgTake = F("dmg_take");
        DpHitContentsMask = F("dphitcontentsmask"); Effects = F("effects"); FixAngle = F("fixangle"); Flags = F("flags");
        Frags = F("frags"); Frame = F("frame"); Frame1Time = F("frame1time"); Frame2 = F("frame2"); Frame2Time = F("frame2time");
        Frame3 = F("frame3"); Frame3Time = F("frame3time"); Frame4 = F("frame4"); Frame4Time = F("frame4time");
        FullBright = F("fullbright"); GlowColor = F("glow_color"); GlowSize = F("glow_size"); GlowTrail = F("glow_trail");
        Gravity = F("gravity"); Health = F("health"); IdealYaw = F("ideal_yaw"); IdealPitch = F("idealpitch"); Impulse = F("impulse");
        Items = F("items"); Items2 = F("items2"); LerpFrac = F("lerpfrac"); LerpFrac3 = F("lerpfrac3"); LerpFrac4 = F("lerpfrac4");
        LightLev = F("light_lev"); LTime = F("ltime"); MaxHealth = F("max_health"); ModelFlags = F("modelflags");
        ModelIndex = F("modelindex"); MoveType = F("movetype"); NextThink = F("nextthink"); PFlags = F("pflags"); Ping = F("ping");
        PingMovementLoss = F("ping_movementloss"); PingPacketLoss = F("ping_packetloss"); PitchSpeed = F("pitch_speed");
        PModel = F("pmodel"); RenderAmt = F("renderamt"); Scale = F("scale"); SendComplexAnimation = F("sendcomplexanimation");
        SkeletonIndex = F("skeletonindex"); Skin = F("skin"); Solid = F("solid"); Sounds = F("sounds"); SpawnFlags = F("spawnflags");
        Style = F("style"); TagIndex = F("tag_index"); TakeDamage = F("takedamage"); Team = F("team");
        TeleportTime = F("teleport_time"); TrailEffectNum = F("traileffectnum"); ViewZoom = F("viewzoom");
        WaterLevel = F("waterlevel"); WaterType = F("watertype"); Weapon = F("weapon"); WeaponFrame = F("weaponframe");
        YawSpeed = F("yaw_speed"); CryptoIdfpSigned = F("crypto_idfp_signed");
        AmmoShells = F("ammo_shells"); AmmoNails = F("ammo_nails"); AmmoRockets = F("ammo_rockets"); AmmoCells = F("ammo_cells");

        ClassName = F("classname"); ClientStatus = F("clientstatus"); CryptoEncryptMethod = F("crypto_encryptmethod");
        CryptoIdfp = F("crypto_idfp"); CryptoKeyfp = F("crypto_keyfp"); CryptoMyKeyfp = F("crypto_mykeyfp");
        CryptoSignMethod = F("crypto_signmethod"); Message = F("message"); Model = F("model"); NetAddress = F("netaddress");
        NetName = F("netname"); PlayerModel = F("playermodel"); PlayerSkin = F("playerskin"); Target = F("target");
        TargetName = F("targetname"); WeaponModel = F("weaponmodel");

        AbsMax = V("absmax"); AbsMin = V("absmin"); Angles = V("angles"); AVelocity = V("avelocity"); Color = V("color");
        ColorMod = V("colormod"); CursorScreen = V("cursor_screen"); CursorTraceEndPos = V("cursor_trace_endpos");
        CursorTraceStart = V("cursor_trace_start"); GlowMod = V("glowmod"); Maxs = V("maxs"); Mins = V("mins"); MoveDir = V("movedir");
        Movement = V("movement"); OldOrigin = V("oldorigin"); Origin = V("origin"); PunchAngle = V("punchangle");
        PunchVector = V("punchvector"); Size = V("size"); VAngle = V("v_angle"); Velocity = V("velocity"); ViewOfs = V("view_ofs");

        AimEnt = F("aiment"); Chain = F("chain"); ClientCamera = F("clientcamera"); CursorTraceEnt = F("cursor_trace_ent");
        DmgInflictor = F("dmg_inflictor"); DrawOnlyToClient = F("drawonlytoclient"); Enemy = F("enemy");
        ExteriorModelToClient = F("exteriormodeltoclient"); GoalEntity = F("goalentity"); GroundEntity = F("groundentity");
        NoDrawToClient = F("nodrawtoclient"); Owner = F("owner"); TagEntity = F("tag_entity");
        ViewModelForClient = F("viewmodelforclient");

        SendEntity = F("SendEntity"); Blocked = F("blocked"); CameraTransform = F("camera_transform");
        ContentsTransition = F("contentstransition"); CustomizeEntityForClient = F("customizeentityforclient");
        MoveTypeStepLandEvent = F("movetypesteplandevent"); Think = F("think"); Touch = F("touch"); Use = F("use");
    }
}

/// <summary>
/// Cell offsets of the globals the server engine sets and reads, or -1 for one the program does not
/// have. The progdefs.h set (self, other, time, the sixteen parms, the trace results...) is required:
/// a program without them is not a server program, and <see cref="Missing"/> names what is absent.
/// </summary>
public sealed class SvGlobalOffsets
{
    public readonly int Self, Other, World, Time, FrameTime, ForceRetouch, MapName, Deathmatch, Coop, TeamPlay, ServerFlags,
        TotalSecrets, TotalMonsters, FoundSecrets, KilledMonsters, Parm1, VForward, VUp, VRight, TraceAllSolid, TraceStartSolid,
        TraceFraction, TraceEndPos, TracePlaneNormal, TracePlaneDist, TraceEnt, TraceInOpen, TraceInWater, MsgEntity;
    // prvm_offsets.h extras; optional.
    public readonly int GetTagInfoParent, GetTagInfoName, GetTagInfoForward, GetTagInfoOffset, GetTagInfoRight, GetTagInfoUp,
        IntermissionRunning, RequireSpawnFuncPrefix, TraceDpHitContents, TraceDpHitQ3SurfaceFlags, TraceDpStartContents,
        TraceDpHitTextureName, SvInitCmd, WorldStatus;

    /// <summary>Required globals the program lacks. Empty for anything that can run as a server.</summary>
    public IReadOnlyList<string> Missing { get; }

    public SvGlobalOffsets(QcVm vm)
    {
        List<string> missing = new();
        int G(string name, int cells = 1, bool required = false)
        {
            QcDef? def = vm.FindGlobal(name);
            if (def is not null && def.Offset >= 0 && def.Offset + cells <= vm.NumGlobals) return def.Offset;
            if (required) missing.Add(name);
            return -1;
        }
        int R(string name, int cells = 1) => G(name, cells, required: true);

        Self = R("self"); Other = R("other"); World = R("world"); Time = R("time"); FrameTime = R("frametime");
        ForceRetouch = R("force_retouch"); MapName = R("mapname"); Deathmatch = R("deathmatch"); Coop = R("coop");
        TeamPlay = R("teamplay"); ServerFlags = R("serverflags"); TotalSecrets = R("total_secrets");
        TotalMonsters = R("total_monsters"); FoundSecrets = R("found_secrets"); KilledMonsters = R("killed_monsters");
        Parm1 = R("parm1");
        // The sixteen spawn parms are addressed as an array from parm1, as the C does.
        if (Parm1 >= 0 && (vm.FindGlobal("parm16")?.Offset ?? -1) != Parm1 + 15) missing.Add("parm1..parm16 (not contiguous)");
        VForward = R("v_forward", 3); VUp = R("v_up", 3); VRight = R("v_right", 3);
        TraceAllSolid = R("trace_allsolid"); TraceStartSolid = R("trace_startsolid"); TraceFraction = R("trace_fraction");
        TraceEndPos = R("trace_endpos", 3); TracePlaneNormal = R("trace_plane_normal", 3); TracePlaneDist = R("trace_plane_dist");
        TraceEnt = R("trace_ent"); TraceInOpen = R("trace_inopen"); TraceInWater = R("trace_inwater"); MsgEntity = R("msg_entity");

        GetTagInfoParent = G("gettaginfo_parent"); GetTagInfoName = G("gettaginfo_name"); GetTagInfoForward = G("gettaginfo_forward", 3);
        GetTagInfoOffset = G("gettaginfo_offset", 3); GetTagInfoRight = G("gettaginfo_right", 3); GetTagInfoUp = G("gettaginfo_up", 3);
        IntermissionRunning = G("intermission_running"); RequireSpawnFuncPrefix = G("require_spawnfunc_prefix");
        TraceDpHitContents = G("trace_dphitcontents"); TraceDpHitQ3SurfaceFlags = G("trace_dphitq3surfaceflags");
        TraceDpStartContents = G("trace_dpstartcontents"); TraceDpHitTextureName = G("trace_dphittexturename");
        SvInitCmd = G("SV_InitCmd"); WorldStatus = G("worldstatus");
        Missing = missing;
    }
}

/// <summary>Function indices of the entry points the engine calls (0: the program has none).</summary>
public sealed class SvFunctions
{
    public readonly int ClientConnect, ClientDisconnect, ClientKill, ConsoleCmd, EndFrame, GameCommand, PlayerPostThink,
        PlayerPreThink, PutClientInServer, RestoreGame, SvChangeTeam, SvParseClientCommand, SvPausedTic, SvPlayerPhysics,
        SvShutdown, SetChangeParms, SetNewParms, StartFrame, UriGetCallback, Main;

    public SvFunctions(QcVm vm)
    {
        ClientConnect = vm.FindFunction("ClientConnect"); ClientDisconnect = vm.FindFunction("ClientDisconnect");
        ClientKill = vm.FindFunction("ClientKill"); ConsoleCmd = vm.FindFunction("ConsoleCmd"); EndFrame = vm.FindFunction("EndFrame");
        GameCommand = vm.FindFunction("GameCommand"); PlayerPostThink = vm.FindFunction("PlayerPostThink");
        PlayerPreThink = vm.FindFunction("PlayerPreThink"); PutClientInServer = vm.FindFunction("PutClientInServer");
        RestoreGame = vm.FindFunction("RestoreGame"); SvChangeTeam = vm.FindFunction("SV_ChangeTeam");
        SvParseClientCommand = vm.FindFunction("SV_ParseClientCommand"); SvPausedTic = vm.FindFunction("SV_PausedTic");
        SvPlayerPhysics = vm.FindFunction("SV_PlayerPhysics"); SvShutdown = vm.FindFunction("SV_Shutdown");
        SetChangeParms = vm.FindFunction("SetChangeParms"); SetNewParms = vm.FindFunction("SetNewParms");
        StartFrame = vm.FindFunction("StartFrame"); UriGetCallback = vm.FindFunction("URI_Get_Callback");
        Main = vm.FindFunction("main");
    }
}
