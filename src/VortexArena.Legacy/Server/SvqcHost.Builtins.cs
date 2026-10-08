// Port of Base/darkplaces/svvm_cmds.c: vm_sv_builtins[] (the registration) and the server's own
// builtins - VM_SV_setorigin, SetMinMaxSize, VM_SV_setsize, VM_SV_setmodel, VM_SV_sprint,
// VM_SV_centerprint, VM_SV_particle, VM_SV_ambientsound, VM_SV_sound, VM_SV_pointsound,
// VM_SV_traceline, VM_SV_tracebox, SV_Trace_Toss, VM_SV_tracetoss, VM_SV_checkclient, VM_SV_checkpvs,
// VM_SV_stuffcmd, VM_SV_findradius, VM_SV_findbox, VM_SV_precache_sound, VM_SV_precache_model,
// VM_SV_walkmove, VM_SV_droptofloor, VM_SV_lightstyle, VM_SV_checkbottom, VM_SV_pointcontents,
// VM_SV_aim, WriteDest and VM_SV_Write*, VM_SV_makestatic, VM_SV_setspawnparms, VM_SV_getlight,
// VM_SV_AddStat, VM_SV_copyentity, VM_SV_setcolor, VM_SV_effect, the VM_SV_te_* family,
// VM_SV_clientcommand, VM_SV_setattachment, VM_SV_gettagindex, VM_SV_dropclient, VM_SV_spawnclient,
// VM_SV_clienttype, VM_SV_setmodelindex, VM_SV_modelnameforindex, VM_SV_particleeffectnum,
// VM_SV_trailparticles, VM_SV_pointparticles, VM_SV_registercommand, VM_SV_setpause,
// VM_SV_frameforname, VM_SV_frameduration; prvm_cmds.c VM_bprint, VM_changelevel, VM_SetTraceGlobals;
// sv_phys.c SV_GenericHitSuperContentsMask, SV_LinkEdict, SV_LinkEdict_TouchAreaGrid; sv_send.c
// SV_StartParticle, SV_StartEffect, SV_StartSound, SV_StartPointSound; model_brush.c
// Mod_Q1BSP_NativeContentsFromSuperContents.
using System.Buffers;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvqcHost
{
    // bspfile.h CONTENTS_*: what pointcontents returns, and what .watertype holds.
    public const int ContentsEmpty = -1, ContentsSolidNative = -2, ContentsWater = -3, ContentsSlime = -4, ContentsLava = -5, ContentsSky = -6;
    private const int MsgBroadcast = 0, MsgOne = 1, MsgAll = 2, MsgInit = 3, MsgEntity = 5;
    private const int ChannelFlagReliable = 1, ChannelFlagForceLoop = 2, ChannelFlagPaused = 8, ChannelFlagFullVolume = 16;
    private const int MinVmStat = 32, MaxVmStat = 220;

    /// <summary>vm_customstats: the stats the program registered with addstat. Type 0 is unused.</summary>
    internal readonly (byte Type, int Field)[] CustomStats = new (byte, int)[DpProtocol.MaxClStats];
    internal int CustomStatsLast = -1;

    /// <summary>vm_customstats[index]: the type addstat registered (1 string, 2 integer, 8 float; 0 unused) and the entity field it reads.</summary>
    public (byte Type, int Field) GetCustomStat(int index) => (uint)index < (uint)CustomStats.Length ? CustomStats[index] : default;

    private readonly HashSet<string> _qcCommands = new(StringComparer.OrdinalIgnoreCase);
    private double _lastCheckTime;
    private int _lastCheck = 1, _checkCluster = -1;
    private bool _checkHasCluster;

    /// <summary>Number and DarkPlaces name of every server builtin this class installs.</summary>
    public IEnumerable<(int Number, string Name)> RegisteredBuiltins => BuiltinTable().Select(e => (e.Number, e.Name));

    private void RegisterBuiltins()
    {
        foreach ((int number, string _, QcBuiltin builtin) in BuiltinTable()) Vm.RegisterBuiltin(number, builtin);
    }

    private (int Number, string Name, QcBuiltin Builtin)[] BuiltinTable() => new (int, string, QcBuiltin)[]
    {
        (2, "setorigin", SetOrigin), (3, "setmodel", SetModel), (4, "setsize", SetSize), (8, "sound", Sound),
        (16, "traceline", TraceLine), (17, "checkclient", CheckClient), (19, "precache_sound", PrecacheSound),
        (20, "precache_model", PrecacheModel), (21, "stuffcmd", StuffCmd), (22, "findradius", FindRadius),
        (23, "bprint", BPrint), (24, "sprint", SPrint), (32, "walkmove", WalkMove), (34, "droptofloor", DropToFloor),
        (35, "lightstyle", LightStyle), (40, "checkbottom", CheckBottomBuiltin), (41, "pointcontents", PointContents),
        (44, "aim", Aim), (48, "particle", Particle), (52, "WriteByte", WriteByte), (53, "WriteChar", WriteChar),
        (54, "WriteShort", WriteShort), (55, "WriteLong", WriteLong), (56, "WriteCoord", WriteCoord),
        (57, "WriteAngle", WriteAngle), (58, "WriteString", WriteString), (59, "WriteEntity", WriteEntity),
        (64, "tracetoss", TraceToss), (67, "movetogoal", MoveToGoal), (69, "makestatic", MakeStatic),
        (70, "changelevel", ChangeLevel), (73, "centerprint", CenterPrint), (74, "ambientsound", AmbientSound),
        (75, "precache_model2", PrecacheModel), (76, "precache_sound2", PrecacheSound), (78, "setspawnparms", SetSpawnParms),
        (90, "tracebox", TraceBox), (92, "getlight", GetLight), (232, "addstat", AddStat), (240, "checkpvs", CheckPvs),
        (263, "skel_create", SkelCreate), (265, "skel_get_numbones", SkelGetNumBones), (266, "skel_get_bonename", SkelGetBoneName),
        (267, "skel_get_boneparent", SkelGetBoneParent), (268, "skel_find_bone", SkelFindBone), (275, "skel_delete", SkelDelete),
        (276, "frameforname", FrameForName), (277, "frameduration", FrameDuration),
        (335, "particleeffectnum", ParticleEffectNum), (336, "trailparticles", TrailParticles),
        (337, "pointparticles", PointParticles), (352, "registercommand", RegisterCommand), (400, "copyentity", CopyEntity),
        (401, "setcolor", SetColor), (404, "effect", Effect),
        (405, "te_blood", TeBlood), (406, "te_bloodshower", TeBloodShower), (407, "te_explosionrgb", TeExplosionRgb),
        (408, "te_particlecube", TeParticleCube), (409, "te_particlerain", vm => TeParticleRainSnow(vm, TempEntityType.ParticleRain)),
        (410, "te_particlesnow", vm => TeParticleRainSnow(vm, TempEntityType.ParticleSnow)), (411, "te_spark", TeSpark),
        (412, "te_gunshotquad", vm => TePoint(vm, TempEntityType.GunshotQuad)), (413, "te_spikequad", vm => TePoint(vm, TempEntityType.SpikeQuad)),
        (414, "te_superspikequad", vm => TePoint(vm, TempEntityType.SuperSpikeQuad)),
        (415, "te_explosionquad", vm => TePoint(vm, TempEntityType.ExplosionQuad)),
        (416, "te_smallflash", vm => TePoint(vm, TempEntityType.SmallFlash)), (417, "te_customflash", TeCustomFlash),
        (418, "te_gunshot", vm => TePoint(vm, TempEntityType.Gunshot)), (419, "te_spike", vm => TePoint(vm, TempEntityType.Spike)),
        (420, "te_superspike", vm => TePoint(vm, TempEntityType.SuperSpike)), (421, "te_explosion", vm => TePoint(vm, TempEntityType.Explosion)),
        (422, "te_tarexplosion", vm => TePoint(vm, TempEntityType.TarExplosion)), (423, "te_wizspike", vm => TePoint(vm, TempEntityType.WizSpike)),
        (424, "te_knightspike", vm => TePoint(vm, TempEntityType.KnightSpike)), (425, "te_lavasplash", vm => TePoint(vm, TempEntityType.LavaSplash)),
        (426, "te_teleport", vm => TePoint(vm, TempEntityType.Teleport)), (427, "te_explosion2", TeExplosion2),
        (428, "te_lightning1", vm => TeBeam(vm, TempEntityType.Lightning1)), (429, "te_lightning2", vm => TeBeam(vm, TempEntityType.Lightning2)),
        (430, "te_lightning3", vm => TeBeam(vm, TempEntityType.Lightning3)), (431, "te_beam", vm => TeBeam(vm, TempEntityType.Beam)),
        (433, "te_plasmaburn", vm => TePoint(vm, TempEntityType.PlasmaBurn)), (434, "getsurfacenumpoints", GetSurfaceNumPoints), (435, "getsurfacepoint", GetSurfacePoint),
        (436, "getsurfacenormal", GetSurfaceNormal), (437, "getsurfacetexture", GetSurfaceTexture), (440, "clientcommand", ClientCommandBuiltin),
        (443, "setattachment", SetAttachment), (451, "gettagindex", GetTagIndex), (452, "gettaginfo", GetTagInfo),
        (453, "dropclient", DropClientBuiltin), (454, "spawnclient", SpawnClient), (455, "clienttype", ClientType),
        (456, "WriteUnterminatedString", WriteUnterminatedString), (457, "te_flamejet", TeFlameJet), (483, "pointsound", PointSound),
        (501, "WritePicture", WritePicture), (513, "uri_get", UriGet), (531, "setpause", SetPause), (566, "findbox", FindBox), (567, "nudgeoutofsolid", NudgeOutOfSolidBuiltin),
        (624, "getextresponse", GetExtResponse),
        (628, "getsurfacenumtriangles", GetSurfaceNumTriangles), (629, "getsurfacetriangle", GetSurfaceTriangle),
    };

    // ---- helpers ---------------------------------------------------------------------------------------

    private QcRuntimeException Fault(string message) => new($"server: {message}");

    private void Parms(int min, int max, string name)
    {
        if (Vm.ArgCount < min || Vm.ArgCount > max)
            throw Fault(min == max ? $"{name} wrong parameter count {Vm.ArgCount} ({min} expected ) !" : $"{name} wrong parameter count {Vm.ArgCount} ({min} to {max} expected ) !");
    }

    // VM_VarString: arguments from `first` on, concatenated and cut at the tempstring size.
    private string VarString(int first)
    {
        int count = Math.Min(Vm.ArgCount, ProgsFile.MaxParms);
        if (first >= count) return "";
        if (first == count - 1)
        {
            string only = Vm.ArgString(first);
            return only.Length < Vm.MaxStringLength ? only : only[..(Vm.MaxStringLength - 1)];
        }
        StringBuilder text = new();
        for (int i = first; i < count && text.Length < Vm.MaxStringLength - 1; i++) text.Append(Vm.ArgString(i));
        if (text.Length > Vm.MaxStringLength - 1) text.Length = Vm.MaxStringLength - 1;
        return text.ToString();
    }

    // The entity argument of a builtin that modifies it: neither the world nor a freed entity.
    private bool Modifiable(int edict, string name)
    {
        if (edict == 0)
        {
            Warning($"{name}: can not modify world entity\n");
            return false;
        }
        if (Vm.IsFree(edict) || edict >= Vm.NumEdicts)
        {
            Warning($"{name}: can not modify free entity\n");
            return false;
        }
        return true;
    }

    private static bool IsNaN(QcVector v) => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z);
    private static QcVector Add(QcVector a, QcVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static QcVector Sub(QcVector a, QcVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static QcVector Scale(QcVector a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    private static QcVector MA(QcVector a, float s, QcVector b) => new(a.X + s * b.X, a.Y + s * b.Y, a.Z + s * b.Z);
    private static float Dot(QcVector a, QcVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static bool Same(QcVector a, QcVector b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    /// <summary>SV_GenericHitSuperContentsMask: what a move made by this entity stops on.</summary>
    public int GenericHitSuperContentsMask(int passEdict)
    {
        if (passEdict > 0 && IsLive(passEdict))
        {
            int mask = QcVm.FloatToInt(Fl(passEdict, F.DpHitContentsMask));
            if (mask != 0) return mask;
            float solid = Fl(passEdict, F.Solid);
            if (solid == SolidSlideBox)
                return (IntFlags(passEdict) & FlMonster) != 0
                    ? SvWorld.ContentsSolid | SvWorld.ContentsBody | SvWorld.ContentsMonsterClip
                    : SvWorld.ContentsSolid | SvWorld.ContentsBody | SvWorld.ContentsPlayerClip;
            if (solid == SolidCorpse || solid == SolidTrigger) return SvWorld.ContentsSolid | SvWorld.ContentsBody;
        }
        return SvWorld.ContentsSolid | SvWorld.ContentsBody | SvWorld.ContentsCorpse;
    }

    /// <summary>SV_TraceBox with the mask of the entity that moves, lengthened by collision_extendmovelength
    /// as every trace the engine makes for its own moves is.</summary>
    internal SvTrace Trace(QcVector start, QcVector mins, QcVector maxs, QcVector end, int type, int passEdict) =>
        World.Trace(start, mins, maxs, end, type, passEdict, GenericHitSuperContentsMask(passEdict), _cv.ExtendMoveLength);

    /// <summary>The same with the lengthening of a builtin (collision_extendtracelinelength / collision_extendtraceboxlength).</summary>
    internal SvTrace Trace(QcVector start, QcVector mins, QcVector maxs, QcVector end, int type, int passEdict, float extend) =>
        World.Trace(start, mins, maxs, end, type, passEdict, GenericHitSuperContentsMask(passEdict), extend);

    /// <summary>VM_SetTraceGlobals.</summary>
    internal void SetTraceGlobals(in SvTrace trace)
    {
        Vm.GlobalFloat(G.TraceAllSolid) = trace.AllSolid ? 1 : 0;
        Vm.GlobalFloat(G.TraceStartSolid) = trace.StartSolid ? 1 : 0;
        Vm.GlobalFloat(G.TraceFraction) = trace.Fraction;
        // Neither is ever set on a Quake 3 map: only the Quake 1 hull code writes them.
        Vm.GlobalFloat(G.TraceInWater) = 0;
        Vm.GlobalFloat(G.TraceInOpen) = 0;
        Vm.GlobalVector(G.TraceEndPos) = trace.EndPos;
        Vm.GlobalVector(G.TracePlaneNormal) = trace.PlaneNormal;
        Vm.GlobalFloat(G.TracePlaneDist) = trace.PlaneDist;
        Vm.GlobalInt(G.TraceEnt) = trace.Ent > 0 ? trace.Ent : 0;
        if (G.TraceDpStartContents >= 0) Vm.GlobalFloat(G.TraceDpStartContents) = trace.StartContents;
        if (G.TraceDpHitContents >= 0) Vm.GlobalFloat(G.TraceDpHitContents) = trace.HitContents;
        if (G.TraceDpHitQ3SurfaceFlags >= 0) Vm.GlobalFloat(G.TraceDpHitQ3SurfaceFlags) = trace.HitQ3SurfaceFlags;
        if (G.TraceDpHitTextureName >= 0) Vm.GlobalInt(G.TraceDpHitTextureName) = trace.HitTextureName is { Length: > 0 } name ? Vm.TempString(name) : 0;
    }

    /// <summary>Mod_Q1BSP_NativeContentsFromSuperContents.</summary>
    public static int NativeContents(int superContents)
    {
        if ((superContents & (SvWorld.ContentsSolid | SvWorld.ContentsBody)) != 0) return ContentsSolidNative;
        if ((superContents & SvWorld.ContentsSky) != 0) return ContentsSky;
        if ((superContents & SvWorld.ContentsLava) != 0) return ContentsLava;
        if ((superContents & SvWorld.ContentsSlime) != 0) return ContentsSlime;
        if ((superContents & SvWorld.ContentsWater) != 0) return ContentsWater;
        return ContentsEmpty;
    }

    // ---- linking ---------------------------------------------------------------------------------------

    /// <summary>
    /// SV_LinkEdict: work out the entity's absolute box from its origin, size and (for a brush model
    /// that turns) its model, store it in absmin / absmax, and put the entity in the area grid.
    /// </summary>
    public void LinkEdict(int edict)
    {
        if (edict <= 0 || !IsLive(edict)) return;   // don't add the world, or free entities
        int modelIndex = QcVm.FloatToInt(Fl(edict, F.ModelIndex));
        if (modelIndex < 0 || modelIndex >= DpProtocol.MaxModels) modelIndex = 0;
        QcVector origin = Vec(edict, F.Origin), mins, maxs;
        float solid = Fl(edict, F.Solid);
        if (solid == SolidBsp && ModelBounds(modelIndex) is { } model)
        {
            QcVector angles = Vec(edict, F.Angles), avelocity = Vec(edict, F.AVelocity);
            if (angles.X != 0 || angles.Z != 0 || avelocity.X != 0 || avelocity.Z != 0)
            {
                mins = Add(origin, model.RotatedMins);
                maxs = Add(origin, model.RotatedMaxs);
            }
            else if (angles.Y != 0 || avelocity.Y != 0)
            {
                mins = Add(origin, model.YawMins);
                maxs = Add(origin, model.YawMaxs);
            }
            else
            {
                mins = Add(origin, model.NormalMins);
                maxs = Add(origin, model.NormalMaxs);
            }
        }
        else
        {
            // (MOVETYPE_PHYSICS, the ODE movetype, would rotate the box here; ODE is not built.)
            mins = Add(origin, Vec(edict, F.Mins));
            maxs = Add(origin, Vec(edict, F.Maxs));
        }

        // to make items easier to pick up and allow them to be grabbed off of shelves, the abs sizes
        // are expanded
        if (_cv.LegacyBBoxExpand)
        {
            if ((IntFlags(edict) & FlItem) != 0)
            {
                mins.X -= 15; mins.Y -= 15; mins.Z -= 1;
                maxs.X += 15; maxs.Y += 15; maxs.Z += 1;
            }
            else
            {
                // because movement is clipped an epsilon away from an actual edge, we must fully check
                // even when bounding boxes don't quite touch
                mins.X -= 1; mins.Y -= 1; mins.Z -= 1;
                maxs.X += 1; maxs.Y += 1; maxs.Z += 1;
            }
        }
        Vec(edict, F.AbsMin) = mins;
        Vec(edict, F.AbsMax) = maxs;
        World.LinkEdict(edict, mins, maxs, solid == SolidNot);
    }

    /// <summary>
    /// SV_LinkEdict_TouchAreaGrid: run the touch function of every trigger the entity's box now overlaps.
    /// </summary>
    internal void TouchAreaGrid(int edict)
    {
        if (edict <= 0 || !IsLive(edict) || Fl(edict, F.Solid) == SolidNot) return;
        if (!World.TryGetArea(edict, out QcVector areaMins, out QcVector areaMaxs)) return;
        // A touch function may move things and so query the grid again: the list is this call's own.
        int[] list = ArrayPool<int>.Shared.Rent(SvWorld.MaxTouchedEdicts);
        try
        {
            int count = World.EntitiesInBox(areaMins, areaMaxs, list.AsSpan(0, SvWorld.MaxTouchedEdicts));
            int oldSelf = Self, oldOther = Other;
            for (int i = 0; i < count; i++)
            {
                int touch = list[i];
                if (touch == edict || !IsLive(touch) || (int)Fl(touch, F.Solid) != SolidTrigger || Int(touch, F.Touch) == 0) continue;
                TouchAreaGridCall(touch, edict);
                if (!IsLive(edict)) break;
            }
            Self = oldSelf;
            Other = oldOther;
        }
        finally { ArrayPool<int>.Shared.Return(list); }
    }

    // SV_LinkEdict_TouchAreaGrid_Call
    private void TouchAreaGridCall(int touch, int edict)
    {
        Self = touch;
        Other = edict;
        SetTime(Time);
        Vm.GlobalFloat(G.TraceAllSolid) = 0;
        Vm.GlobalFloat(G.TraceStartSolid) = 0;
        Vm.GlobalFloat(G.TraceFraction) = 1;
        Vm.GlobalFloat(G.TraceInWater) = 0;
        Vm.GlobalFloat(G.TraceInOpen) = 1;
        Vm.GlobalVector(G.TraceEndPos) = Vec(touch, F.Origin);
        Vm.GlobalVector(G.TracePlaneNormal) = new QcVector(0, 0, 1);
        Vm.GlobalFloat(G.TracePlaneDist) = 0;
        Vm.GlobalInt(G.TraceEnt) = edict;
        if (G.TraceDpStartContents >= 0) Vm.GlobalFloat(G.TraceDpStartContents) = 0;
        if (G.TraceDpHitContents >= 0) Vm.GlobalFloat(G.TraceDpHitContents) = 0;
        if (G.TraceDpHitQ3SurfaceFlags >= 0) Vm.GlobalFloat(G.TraceDpHitQ3SurfaceFlags) = 0;
        if (G.TraceDpHitTextureName >= 0) Vm.GlobalInt(G.TraceDpHitTextureName) = 0;
        Exec(Int(touch, F.Touch), "QC function self.touch is missing");
    }

    // ---- placement -------------------------------------------------------------------------------------

    // #2 void(entity e, vector o) setorigin: "the only valid way to move an object without using the
    // physics of the world... directly changing origin will not set internal links correctly".
    private void SetOrigin(QcVm vm)
    {
        Parms(2, 2, "VM_SV_setorigin");
        int e = vm.ArgEdict(0);
        if (!Modifiable(e, "setorigin")) return;
        Vec(e, F.Origin) = vm.ArgVector(1);
        ref EdictPrivate priv = ref Priv(e);
        if (priv.Mark == MarkWaitForSetOrigin) priv.Mark = MarkSetOriginCaught;
        LinkEdict(e);
    }

    private void SetMinMaxSize(int e, QcVector min, QcVector max)
    {
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) throw Fault("SetMinMaxSize: backwards mins/maxs");
        Vec(e, F.Mins) = min;
        Vec(e, F.Maxs) = max;
        Vec(e, F.Size) = Sub(max, min);
        LinkEdict(e);
    }

    // #4 void(entity e, vector min, vector max) setsize
    private void SetSize(QcVm vm)
    {
        Parms(3, 3, "VM_SV_setsize");
        int e = vm.ArgEdict(0);
        if (!Modifiable(e, "setsize")) return;
        SetMinMaxSize(e, vm.ArgVector(1), vm.ArgVector(2));
    }

    // #3 void(entity e, string m) setmodel
    private void SetModel(QcVm vm)
    {
        Parms(2, 2, "VM_SV_setmodel");
        int e = vm.ArgEdict(0);
        if (!Modifiable(e, "setmodel")) return;
        ApplyModel(e, ModelIndex(vm.ArgString(1), 1));
    }

    // The tail VM_SV_setmodel and VM_SV_setmodelindex share. An alias model would get Quake's
    // 32-unit cube unless sv_gameplayfix_setmodelrealbox (default 1) is set; with it every model
    // gets its own box.
    private void ApplyModel(int e, int index)
    {
        Int(e, F.Model) = index > 0 ? Vm.EngineString(_modelPrecache[index]) : 0;
        Fl(e, F.ModelIndex) = index;
        if (ModelBounds(index) is { } model)
        {
            if (ModelIsBrush(index) || _cv.SetModelRealBox) SetMinMaxSize(e, model.NormalMins, model.NormalMaxs);
            else SetMinMaxSize(e, new QcVector(-16, -16, -16), new QcVector(16, 16, 16));
        }
        else SetMinMaxSize(e, default, default);
    }

    // #400 void(entity from, entity to) copyentity
    private void CopyEntity(QcVm vm)
    {
        Parms(2, 2, "VM_SV_copyentity");
        int from = vm.ArgEdict(0), to = vm.ArgEdict(1);
        if (from == 0) { Warning("copyentity: can not read world entity\n"); return; }
        if (!IsLive(from)) { Warning("copyentity: can not read free entity\n"); return; }
        if (!Modifiable(to, "copyentity")) return;
        for (int i = 0; i < vm.EntityFields; i++) vm.FieldInt(to, i) = vm.FieldInt(from, i);
        LinkEdict(to);
    }

    // ---- traces ----------------------------------------------------------------------------------------

    // #16 void(vector v1, vector v2, float tryents, entity ignoreentity) traceline
    private void TraceLine(QcVm vm)
    {
        Parms(4, 8, "VM_SV_traceline");
        QcVector v1 = vm.ArgVector(0), v2 = vm.ArgVector(1);
        int move = QcVm.FloatToInt(vm.ArgFloat(2)), ent = vm.ArgEdict(3);
        if (IsNaN(v1) || IsNaN(v2)) throw Fault($"NAN errors detected in traceline({v1}, {v2}, {move}, entity {ent})");
        SetTraceGlobals(Trace(v1, default, default, v2, move, ent, _cv.ExtendTraceLineLength));
    }

    // #90 void(vector v1, vector min, vector max, vector v2, float nomonsters, entity forent) tracebox
    private void TraceBox(QcVm vm)
    {
        Parms(6, 8, "VM_SV_tracebox");
        QcVector v1 = vm.ArgVector(0), m1 = vm.ArgVector(1), m2 = vm.ArgVector(2), v2 = vm.ArgVector(3);
        int move = QcVm.FloatToInt(vm.ArgFloat(4)), ent = vm.ArgEdict(5);
        if (IsNaN(v1) || IsNaN(v2)) throw Fault($"NAN errors detected in tracebox({v1}, {m1}, {m2}, {v2}, {move}, entity {ent})");
        SetTraceGlobals(Trace(v1, m1, m2, v2, move, ent, _cv.ExtendTraceBoxLength));
    }

    // SV_Trace_Toss: where the entity would land if tossed now, in 0.05 s steps for at most 10 s.
    private SvTrace TraceTossInternal(int tossEnt)
    {
        QcVector originalOrigin = Vec(tossEnt, F.Origin), originalVelocity = Vec(tossEnt, F.Velocity);
        QcVector originalAngles = Vec(tossEnt, F.Angles), originalAVelocity = Vec(tossEnt, F.AVelocity);
        float gravity = Fl(tossEnt, F.Gravity);
        if (gravity == 0) gravity = 1.0f;
        gravity *= _cv.Gravity * 0.025f;
        SvTrace trace = default;
        for (int i = 0; i < 200; i++)   // LadyHavoc: sanity check; never trace more than 10 seconds
        {
            CheckVelocity(tossEnt);
            Vec(tossEnt, F.Velocity).Z -= gravity;
            Vec(tossEnt, F.Angles) = MA(Vec(tossEnt, F.Angles), 0.05f, Vec(tossEnt, F.AVelocity));
            QcVector origin = Vec(tossEnt, F.Origin);
            QcVector end = Add(origin, Scale(Vec(tossEnt, F.Velocity), 0.05f));
            trace = Trace(origin, Vec(tossEnt, F.Mins), Vec(tossEnt, F.Maxs), end, SvWorld.MoveNormal, tossEnt);
            Vec(tossEnt, F.Origin) = trace.EndPos;
            Vec(tossEnt, F.Velocity).Z -= gravity;
            if (trace.Fraction < 1) break;
        }
        Vec(tossEnt, F.Origin) = originalOrigin;
        Vec(tossEnt, F.Velocity) = originalVelocity;
        Vec(tossEnt, F.Angles) = originalAngles;
        Vec(tossEnt, F.AVelocity) = originalAVelocity;
        return trace;
    }

    // #64 void(entity ent, entity ignore) tracetoss
    private void TraceToss(QcVm vm)
    {
        Parms(2, 2, "VM_SV_tracetoss");
        int ent = vm.ArgEdict(0);
        if (ent == 0)
        {
            Warning("tracetoss: can not use world entity\n");
            return;
        }
        if (!IsLive(ent)) return;
        SetTraceGlobals(TraceTossInternal(ent));
    }

    // #41 float(vector v) pointcontents
    private void PointContents(QcVm vm)
    {
        Parms(1, 1, "VM_SV_pointcontents");
        vm.ReturnFloat(NativeContents(World.PointSuperContents(vm.ArgVector(0))));
    }

    // #240 float(vector viewpos, entity viewee) checkpvs
    private void CheckPvs(QcVm vm)
    {
        Parms(2, 2, "VM_SV_checkpvs");
        int viewee = vm.ArgEdict(1);
        if (!IsLive(viewee))
        {
            Warning("checkpvs: can not check free entity\n");
            vm.ReturnFloat(4);
            return;
        }
        vm.ReturnFloat(World.CheckPvs(vm.ArgVector(0), Vec(viewee, F.AbsMin), Vec(viewee, F.AbsMax)));
    }

    // VM_SV_newcheckclient: the next live, targetable player after `check`, and the visibility
    // cluster of its eyes.
    private int NewCheckClient(int check)
    {
        check = Math.Clamp(check, 1, Clients.Length);
        int i = check == Clients.Length ? 1 : check + 1;
        for (; ; i++)
        {
            if (i == Clients.Length + 1) i = 1;
            if (i == check) break;   // didn't find anything else
            if (Vm.IsFree(i) || Fl(i, F.Health) <= 0 || (IntFlags(i) & FlNoTarget) != 0) continue;
            break;   // anything that is a client, or has a client as an enemy
        }
        _checkCluster = World.ClusterOf(Add(Vec(i, F.Origin), Vec(i, F.ViewOfs)));
        _checkHasCluster = _checkCluster >= 0;
        return i;
    }

    // #17 entity() checkclient: "Returns a client (or object that has a client enemy) that would be
    // a valid target. If there is more than one valid option, they are cycled each frame."
    private void CheckClient(QcVm vm)
    {
        Parms(0, 0, "VM_SV_checkclient");
        if (Time - _lastCheckTime >= 0.1)
        {
            _lastCheck = NewCheckClient(_lastCheck);
            _lastCheckTime = Time;
        }
        int ent = _lastCheck;
        if (!IsLive(ent) || Fl(ent, F.Health) <= 0)
        {
            vm.ReturnInt(0);
            return;
        }
        // if current entity can't possibly see the check entity, return 0
        int self = Self;
        if (IsLive(self) && _checkHasCluster)
        {
            QcVector view = Add(Vec(self, F.Origin), Vec(self, F.ViewOfs));
            if (!World.BoxVisibleFrom(_checkCluster, view, view))
            {
                vm.ReturnInt(0);
                return;
            }
        }
        vm.ReturnInt(ent);
    }

    // ---- finding ---------------------------------------------------------------------------------------

    private int ChainFieldArg(int argument, string name)
    {
        int field = Vm.ArgCount == 3 ? Vm.ArgInt(argument) : F.Chain;
        if ((uint)field >= (uint)Vm.EntityFields) throw Fault($"{name}: field offset {field} is out of bounds");
        return field;
    }

    // #22 entity(vector org, float rad[, .entity chainfield]) findradius: "Returns a chain of
    // entities that have origins within a spherical area". Only entities in the area grid are found.
    private void FindRadius(QcVm vm)
    {
        Parms(2, 3, "VM_SV_findradius");
        int chainField = ChainFieldArg(2, "VM_SV_findradius");
        QcVector org = vm.ArgVector(0);
        float radius = vm.ArgFloat(1), radius2 = radius * radius;
        QcVector mins = new(org.X - (radius + 1), org.Y - (radius + 1), org.Z - (radius + 1));
        QcVector maxs = new(org.X + (radius + 1), org.Y + (radius + 1), org.Z + (radius + 1));
        int[] list = ArrayPool<int>.Shared.Rent(DpProtocol.MaxEdicts);
        try
        {
            int count = World.EntitiesInBox(mins, maxs, list.AsSpan(0, DpProtocol.MaxEdicts));
            int chain = 0;
            bool distanceToBox = _cv.FindRadiusDistanceToBox, includeNonSolid = _cv.BlowUpFallenZombies;
            for (int i = 0; i < count; i++)
            {
                int ent = list[i];
                if (Fl(ent, F.Solid) == SolidNot && !includeNonSolid) continue;
                QcVector eorg = Sub(org, Vec(ent, F.Origin)), emins = Vec(ent, F.Mins), emaxs = Vec(ent, F.Maxs);
                if (distanceToBox)
                {
                    // the distance to the nearest point of the box, not to its centre
                    eorg.X -= Math.Clamp(eorg.X, emins.X, MathF.Max(emins.X, emaxs.X));
                    eorg.Y -= Math.Clamp(eorg.Y, emins.Y, MathF.Max(emins.Y, emaxs.Y));
                    eorg.Z -= Math.Clamp(eorg.Z, emins.Z, MathF.Max(emins.Z, emaxs.Z));
                }
                else eorg = new QcVector(eorg.X - 0.5f * (emins.X + emaxs.X), eorg.Y - 0.5f * (emins.Y + emaxs.Y), eorg.Z - 0.5f * (emins.Z + emaxs.Z));
                if (Dot(eorg, eorg) < radius2)
                {
                    Int(ent, chainField) = chain;
                    chain = ent;
                }
            }
            vm.ReturnInt(chain);
        }
        finally { ArrayPool<int>.Shared.Return(list); }
    }

    // #566 entity(vector mins, vector maxs[, .entity chainfield]) findbox
    private void FindBox(QcVm vm)
    {
        Parms(2, 3, "VM_SV_findbox");
        int chainField = ChainFieldArg(2, "VM_SV_findbox");
        int[] list = ArrayPool<int>.Shared.Rent(DpProtocol.MaxEdicts);
        try
        {
            int count = World.EntitiesInBox(vm.ArgVector(0), vm.ArgVector(1), list.AsSpan(0, DpProtocol.MaxEdicts));
            int chain = 0;
            for (int i = 0; i < count; i++)
            {
                Int(list[i], chainField) = chain;
                chain = list[i];
            }
            vm.ReturnInt(chain);
        }
        finally { ArrayPool<int>.Shared.Return(list); }
    }

    // ---- precaches, models, sounds ---------------------------------------------------------------------

    // #19 string(string s) precache_sound. (The stock Xonotic program declares it as returning the index.)
    private void PrecacheSound(QcVm vm)
    {
        Parms(1, 1, "VM_SV_precache_sound");
        vm.ReturnFloat(SoundIndex(vm.ArgString(0), 2));
    }

    // #20 string(string s) precache_model: returns its argument.
    private void PrecacheModel(QcVm vm)
    {
        Parms(1, 1, "VM_SV_precache_model");
        ModelIndex(vm.ArgString(0), 2);
        vm.ReturnInt(vm.ArgInt(0));
    }

    /// <summary>
    /// SV_StartSound: "Each entity can have eight independant sound sources, like voice, weapon,
    /// feet, etc. Channel 0 is an auto-allocate channel, the others override anything already running
    /// on that entity/channel pair."
    /// </summary>
    public void StartSound(int entity, int channel, string sample, int volume, float attenuation, bool reliable, float speed)
    {
        if (volume < 0 || volume > 255 || attenuation < 0 || attenuation > 4 || channel < -128 || channel > 127) return;
        if (Datagram.Length > DpProtocol.MaxPacketFragment - 21) return;
        int soundNum = SoundIndex(sample, 1);
        if (soundNum == 0 || !IsLive(entity)) return;
        DpMessageWriter dest = reliable ? ReliableDatagram : Datagram;
        int speed4000 = (int)MathF.Floor(speed * 4000.0f + 0.5f);
        int fieldMask = 0;
        if (volume != DpProtocol.DefaultSoundPacketVolume) fieldMask |= DpProtocol.SndVolume;
        if (attenuation != DpProtocol.DefaultSoundPacketAttenuation) fieldMask |= DpProtocol.SndAttenuation;
        if (speed4000 != 0 && speed4000 != 4000) fieldMask |= DpProtocol.SndSpeedUShort4000;
        if (entity >= 8192 || channel < 0 || channel > 7) fieldMask |= DpProtocol.SndLargeEntity;
        if (soundNum >= 256) fieldMask |= DpProtocol.SndLargeSound;

        dest.WriteByte((int)Svc.Sound);
        dest.WriteByte(fieldMask);
        if ((fieldMask & DpProtocol.SndVolume) != 0) dest.WriteByte(volume);
        if ((fieldMask & DpProtocol.SndAttenuation) != 0) dest.WriteByte((int)(attenuation * 64));
        if ((fieldMask & DpProtocol.SndSpeedUShort4000) != 0) dest.WriteShort(speed4000);
        if ((fieldMask & DpProtocol.SndLargeEntity) != 0)
        {
            dest.WriteShort(entity);
            dest.WriteChar(channel);
        }
        else dest.WriteShort((entity << 3) | channel);
        if ((fieldMask & DpProtocol.SndLargeSound) != 0) dest.WriteShort(soundNum);
        else dest.WriteByte(soundNum);
        QcVector origin = Vec(entity, F.Origin), mins = Vec(entity, F.Mins), maxs = Vec(entity, F.Maxs);
        dest.WriteCoord(origin.X + 0.5f * (mins.X + maxs.X));
        dest.WriteCoord(origin.Y + 0.5f * (mins.Y + maxs.Y));
        dest.WriteCoord(origin.Z + 0.5f * (mins.Z + maxs.Z));
        // TODO do we have to do anything here when dest is &sv.reliable_datagram? (the C asks too)
        if (!reliable) FlushBroadcastMessages();
    }

    // #8 void(entity e, float chan, string samp, float volume[, float atten[, float pitchchange[, float flags]]]) sound
    private void Sound(QcVm vm)
    {
        Parms(4, 7, "VM_SV_sound");
        int entity = vm.ArgEdict(0), channel = QcVm.FloatToInt(vm.ArgFloat(1));
        string sample = vm.ArgString(2);
        int volume = QcVm.FloatToInt(vm.ArgFloat(3) * 255);
        float attenuation = vm.ArgCount < 5 ? 1 : vm.ArgFloat(4);
        float pitchChange = vm.ArgCount < 6 ? 0 : vm.ArgFloat(5) * 0.01f;
        int flags;
        if (vm.ArgCount < 7)
        {
            flags = 0;
            if (channel >= 8 && channel <= 15)   // weird QW feature
            {
                flags |= ChannelFlagReliable;
                channel -= 8;
            }
        }
        else flags = QcVm.FloatToInt(vm.ArgFloat(6)) & (ChannelFlagReliable | ChannelFlagForceLoop | ChannelFlagPaused | ChannelFlagFullVolume);
        if (volume < 0 || volume > 255) { Warning("SV_StartSound: volume must be in range 0-1\n"); return; }
        if (attenuation < 0 || attenuation > 4) { Warning("SV_StartSound: attenuation must be in range 0-4\n"); return; }
        if (channel < -128 || channel > 127) { Warning("SV_StartSound: channel must be in range 0-127\n"); return; }
        StartSound(entity, channel, sample, volume, attenuation, (flags & ChannelFlagReliable) != 0, pitchChange);
    }

    // #483 void(vector origin, string sample, float volume, float attenuation[, float pitchchange]) pointsound
    private void PointSound(QcVm vm)
    {
        Parms(4, 5, "VM_SV_pointsound");
        QcVector org = vm.ArgVector(0);
        string sample = vm.ArgString(1);
        int volume = QcVm.FloatToInt(vm.ArgFloat(2) * 255);
        float attenuation = vm.ArgFloat(3), pitchChange = vm.ArgCount < 5 ? 0 : vm.ArgFloat(4) * 0.01f;
        if (volume < 0 || volume > 255) { Warning("SV_StartPointSound: volume must be in range 0-1\n"); return; }
        if (attenuation < 0 || attenuation > 4) { Warning("SV_StartPointSound: attenuation must be in range 0-4\n"); return; }
        if (Datagram.Length > DpProtocol.MaxPacketFragment - 21) return;
        int soundNum = SoundIndex(sample, 1);
        if (soundNum == 0) return;
        // (The C really does scale by 40 here and by 4000 in SV_StartSound.)
        int speed4000 = (int)(pitchChange * 40.0f);
        int fieldMask = 0;
        if (volume != DpProtocol.DefaultSoundPacketVolume) fieldMask |= DpProtocol.SndVolume;
        if (attenuation != DpProtocol.DefaultSoundPacketAttenuation) fieldMask |= DpProtocol.SndAttenuation;
        if (soundNum >= 256) fieldMask |= DpProtocol.SndLargeSound;
        if (speed4000 != 0 && speed4000 != 4000) fieldMask |= DpProtocol.SndSpeedUShort4000;
        Datagram.WriteByte((int)Svc.Sound);
        Datagram.WriteByte(fieldMask);
        if ((fieldMask & DpProtocol.SndVolume) != 0) Datagram.WriteByte(volume);
        if ((fieldMask & DpProtocol.SndAttenuation) != 0) Datagram.WriteByte((int)(attenuation * 64));
        if ((fieldMask & DpProtocol.SndSpeedUShort4000) != 0) Datagram.WriteShort(speed4000);
        // Always write entnum 0 for the world entity
        Datagram.WriteShort(0);
        if ((fieldMask & DpProtocol.SndLargeSound) != 0) Datagram.WriteShort(soundNum);
        else Datagram.WriteByte(soundNum);
        WriteVector(Datagram, org);
        FlushBroadcastMessages();
    }

    // #74 void(vector pos, string samp, float vol, float atten) ambientsound: a looping sound every
    // client is told of when it signs on.
    private void AmbientSound(QcVm vm)
    {
        Parms(4, 4, "VM_SV_ambientsound");
        QcVector pos = vm.ArgVector(0);
        float volume = vm.ArgFloat(2), attenuation = vm.ArgFloat(3);
        // check to see if samp was properly precached
        int soundNum = SoundIndex(vm.ArgString(1), 1);
        if (soundNum == 0) return;
        bool large = soundNum >= 256;
        // add an svc_spawnambient command to the level signon packet
        Signon.WriteByte((int)(large ? Svc.SpawnStaticSound2 : Svc.SpawnStaticSound));
        WriteVector(Signon, pos);
        if (large) Signon.WriteShort(soundNum);
        else Signon.WriteByte(soundNum);
        Signon.WriteByte(QcVm.FloatToInt(volume * 255));
        Signon.WriteByte(QcVm.FloatToInt(attenuation * 64));
    }

    // #35 void(float style, string value) lightstyle
    private void LightStyle(QcVm vm)
    {
        Parms(2, 2, "VM_SV_lightstyle");
        int style = QcVm.FloatToInt(vm.ArgFloat(0));
        string value = vm.ArgString(1);
        if ((uint)style >= (uint)LightStyles.Length) throw Fault($"PF_lightstyle: style: {style} >= 64");
        // char lightstyles[MAX_LIGHTSTYLES][64]
        LightStyles[style] = value.Length < 64 ? value : value[..63];
        // send message to all clients on this server
        if (State != SvState.Active) return;
        foreach (SvClient client in Clients)
        {
            if (!client.Active || client.Connection is not { } connection) continue;
            connection.Message.WriteChar((int)Svc.LightStyle);
            connection.Message.WriteChar(style);
            connection.Message.WriteString(value);
        }
    }

    // #92 vector(vector org) getlight. DarkPlaces samples the map's light grid; this reads no
    // lighting at all and answers black, as DarkPlaces does on a map without one.
    private void GetLight(QcVm vm)
    {
        Parms(1, 3, "VM_SV_getlight");
        vm.ReturnVector(default);
    }

    // #276 float(float modlindex, string framename) frameforname
    private void FrameForName(QcVm vm)
    {
        Parms(2, 2, "VM_SV_frameforname");
        string model = ModelName(QcVm.FloatToInt(vm.ArgFloat(0)));
        vm.ReturnFloat(model.Length == 0 || model[0] == '*' ? -1 : Models.FrameForName(model, vm.ArgString(1)));
    }

    // #277 float(float modlindex, float framenum) frameduration
    private void FrameDuration(QcVm vm)
    {
        Parms(2, 2, "VM_SV_frameduration");
        string model = ModelName(QcVm.FloatToInt(vm.ArgFloat(0)));
        vm.ReturnFloat(model.Length == 0 || model[0] == '*' ? 0 : Models.FrameDuration(model, QcVm.FloatToInt(vm.ArgFloat(1))));
    }

    // #263 float(float modlindex) skel_create, and the queries of a skeleton that need no entity.
    // skel_build and the bone setters (#264, #269-#274) are not registered: nothing on the server
    // blends animations into skeleton objects yet, and a call to them is reported, not faked.
    private void SkelCreate(QcVm vm)
    {
        Parms(1, 1, "VM_SV_skel_create");
        string model = ModelName(QcVm.FloatToInt(vm.ArgFloat(0)));
        vm.ReturnFloat(model.Length == 0 || model[0] == '*' ? 0 : Models.SkelCreate(model));
    }
    private void SkelGetNumBones(QcVm vm) => vm.ReturnFloat(Models.SkelNumBones(QcVm.FloatToInt(vm.ArgFloat(0))));
    private void SkelGetBoneName(QcVm vm)
    {
        string? name = Models.SkelBoneName(QcVm.FloatToInt(vm.ArgFloat(0)), QcVm.FloatToInt(vm.ArgFloat(1)));
        vm.ReturnInt(name is null ? 0 : vm.TempString(name));
    }
    private void SkelGetBoneParent(QcVm vm) => vm.ReturnFloat(Models.SkelBoneParent(QcVm.FloatToInt(vm.ArgFloat(0)), QcVm.FloatToInt(vm.ArgFloat(1))));
    private void SkelFindBone(QcVm vm) => vm.ReturnFloat(Models.SkelFindBone(QcVm.FloatToInt(vm.ArgFloat(0)), vm.ArgString(1)));
    private void SkelDelete(QcVm vm) => Models.SkelDelete(QcVm.FloatToInt(vm.ArgFloat(0)));

    // #443 void(entity e, entity tagentity, string tagname) setattachment
    private void SetAttachment(QcVm vm)
    {
        Parms(3, 3, "VM_SV_setattachment");
        int e = vm.ArgEdict(0), tagEntity = vm.ArgEdict(1);
        string tagName = vm.ArgString(2);
        if (!Modifiable(e, "setattachment")) return;
        int tagIndex = 0;
        if (tagEntity != 0 && tagName.Length > 0 && IsLive(tagEntity))
        {
            string model = ModelName(QcVm.FloatToInt(Fl(tagEntity, F.ModelIndex)));
            if (model.Length > 0 && model[0] != '*') tagIndex = Models.TagIndex(model, QcVm.FloatToInt(Fl(tagEntity, F.Skin)), tagName);
        }
        Int(e, F.TagEntity) = tagEntity;
        Fl(e, F.TagIndex) = tagIndex;
    }

    // #451 float(entity ent, string tagname) gettagindex
    private void GetTagIndex(QcVm vm)
    {
        Parms(2, 2, "VM_SV_gettagindex");
        int ent = vm.ArgEdict(0);
        if (ent == 0) { Warning($"VM_SV_gettagindex(entity #{ent}): can't affect world entity\n"); return; }
        if (!IsLive(ent)) { Warning($"VM_SV_gettagindex(entity #{ent}): can't affect free entity\n"); return; }
        string model = ModelName(QcVm.FloatToInt(Fl(ent, F.ModelIndex)));
        int index = model.Length > 0 && model[0] != '*' ? Models.TagIndex(model, QcVm.FloatToInt(Fl(ent, F.Skin)), vm.ArgString(1)) : 0;
        vm.ReturnFloat(index);
    }

    // #335 float(string effectname) particleeffectnum
    private void ParticleEffectNum(QcVm vm)
    {
        Parms(1, 1, "VM_SV_particleeffectnum");
        int i = ParticleEffectIndex(vm.ArgString(0));
        vm.ReturnFloat(i == 0 ? -1 : i);
    }

    // ---- messages to clients ---------------------------------------------------------------------------

    private static void WriteVector(DpMessageWriter msg, QcVector v)
    {
        msg.WriteCoord(v.X);
        msg.WriteCoord(v.Y);
        msg.WriteCoord(v.Z);
    }

    // The client an entity argument names, for the builtins that speak to one; null (after the C's
    // warning) for anything that is not a connected player's entity.
    private SvClient? ClientArg(int index, string warning)
    {
        int entNum = Vm.ArgInt(index);
        if (entNum < 1 || entNum > Clients.Length || !Clients[entNum - 1].Active)
        {
            Warning(warning);
            return null;
        }
        return Clients[entNum - 1];
    }

    // #24 void(entity client, string s, ...) sprint: "single print to a specific client"
    private void SPrint(QcVm vm)
    {
        Parms(2, 8, "VM_SV_sprint");
        string text = VarString(1);
        if (vm.ArgInt(0) == 0)
        {
            Print(text);
            return;
        }
        if (ClientArg(0, "tried to centerprint to a non-client\n") is { } client) ClientPrint(client, text);
    }

    // #73 void(entity client, string s, ...) centerprint
    private void CenterPrint(QcVm vm)
    {
        Parms(2, 8, "VM_SV_centerprint");
        if (ClientArg(0, "tried to centerprint to a non-client\n") is not { Connection: { } connection }) return;
        connection.Message.WriteChar((int)Svc.CenterPrint);
        connection.Message.WriteString(VarString(1));
    }

    // #23 void(string s, ...) bprint
    private void BPrint(QcVm vm) => BroadcastPrint(VarString(0));

    // #21 void(entity client, string s, ...) stuffcmd: "Sends text over to the client's execution buffer"
    private void StuffCmd(QcVm vm)
    {
        Parms(2, 8, "VM_SV_stuffcmd");
        if (ClientArg(0, "Can't stuffcmd to a non-client\n") is { } client) ClientCommands(client, VarString(1));
    }

    // WriteDest: #define MSG_BROADCAST 0 (unreliable to all), MSG_ONE 1 (reliable to one, msg_entity),
    // MSG_ALL 2 (reliable to all), MSG_INIT 3 (write to the init string), MSG_ENTITY 5.
    private DpMessageWriter WriteDest()
    {
        int dest = QcVm.FloatToInt(Vm.ArgFloat(0));
        switch (dest)
        {
            case MsgBroadcast:
                return Datagram;
            case MsgOne:
            {
                int entNum = Vm.GlobalInt(G.MsgEntity);
                if (entNum < 1 || entNum > Clients.Length) Warning("WriteDest: tried to write to non-client\n");
                else if (!Clients[entNum - 1].Active) Warning("WriteDest: tried to write to a disconnected client\n");
                else if (Clients[entNum - 1].Connection is not { } connection) Warning("WriteDest: tried to write to a bot client\n");
                else return connection.Message;
                return ReliableDatagram;
            }
            case MsgAll:
                return ReliableDatagram;
            case MsgInit:
                return Signon;
            case MsgEntity:
                // Outside a SendEntity call there is no entity message; the write goes nowhere.
                return EntityMessage ?? _nullMessage;
            default:
                Warning("WriteDest: bad destination\n");
                return ReliableDatagram;
        }
    }

    private readonly DpMessageWriter _nullMessage = new(16);

    private void WriteByte(QcVm vm) => WriteDest().WriteByte(QcVm.FloatToInt(vm.ArgFloat(1)));
    private void WriteChar(QcVm vm) => WriteDest().WriteChar(QcVm.FloatToInt(vm.ArgFloat(1)));
    private void WriteShort(QcVm vm) => WriteDest().WriteShort(QcVm.FloatToInt(vm.ArgFloat(1)));
    private void WriteLong(QcVm vm) => WriteDest().WriteLong(QcVm.FloatToInt(vm.ArgFloat(1)));
    private void WriteAngle(QcVm vm) => WriteDest().WriteAngle(vm.ArgFloat(1));
    private void WriteCoord(QcVm vm) => WriteDest().WriteCoord(vm.ArgFloat(1));
    private void WriteString(QcVm vm) => WriteDest().WriteString(vm.ArgString(1));
    private void WriteUnterminatedString(QcVm vm) => WriteDest().WriteBytes(Encoding.UTF8.GetBytes(vm.ArgString(1)));
    private void WriteEntity(QcVm vm) => WriteDest().WriteShort(vm.ArgInt(1));

    // #501 void(float to, string s, float sz) WritePicture: the image name, then a compressed copy
    // of it for clients that lack the file. Compressing images is not ported, so the copy is always
    // the empty one (size 0) - what DarkPlaces itself writes when it cannot compress the image.
    private void WritePicture(QcVm vm)
    {
        Parms(3, 3, "VM_SV_WritePicture");
        DpMessageWriter dest = WriteDest();
        dest.WriteString(vm.ArgString(1));
        dest.WriteShort(0);
    }

    // #69 void(entity e) makestatic: write the entity to the signon buffer and remove it.
    private void MakeStatic(QcVm vm)
    {
        Parms(0, 1, "VM_SV_makestatic");
        int ent = vm.ArgCount >= 1 ? vm.ArgEdict(0) : Self;
        if (!Modifiable(ent, "makestatic")) return;
        int modelIndex = QcVm.FloatToInt(Fl(ent, F.ModelIndex)), frame = QcVm.FloatToInt(Fl(ent, F.Frame));
        if (modelIndex >= 256 || frame >= 256)
        {
            Signon.WriteByte((int)Svc.SpawnStatic2);
            Signon.WriteShort(modelIndex);
            Signon.WriteShort(frame);
        }
        else
        {
            Signon.WriteByte((int)Svc.SpawnStatic);
            Signon.WriteByte(modelIndex);
            Signon.WriteByte(frame);
        }
        Signon.WriteByte(QcVm.FloatToInt(Fl(ent, F.ColorMap)));
        Signon.WriteByte(QcVm.FloatToInt(Fl(ent, F.Skin)));
        QcVector origin = Vec(ent, F.Origin), angles = Vec(ent, F.Angles);
        Signon.WriteCoord(origin.X); Signon.WriteAngle(angles.X);
        Signon.WriteCoord(origin.Y); Signon.WriteAngle(angles.Y);
        Signon.WriteCoord(origin.Z); Signon.WriteAngle(angles.Z);
        // throw the entity away now
        _core.FreeEdict(ent);
    }

    // #48 void(vector o, vector d, float color, float count) particle
    private void Particle(QcVm vm)
    {
        Parms(4, 4, "VM_SV_particle");
        QcVector org = vm.ArgVector(0), dir = vm.ArgVector(1);
        if (Datagram.Length > DpProtocol.MaxPacketFragment - 18) return;
        Datagram.WriteByte((int)Svc.Particle);
        WriteVector(Datagram, org);
        Datagram.WriteChar((int)Math.Clamp(dir.X * 16, -128, 127));
        Datagram.WriteChar((int)Math.Clamp(dir.Y * 16, -128, 127));
        Datagram.WriteChar((int)Math.Clamp(dir.Z * 16, -128, 127));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(3)));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(2)));
        FlushBroadcastMessages();
    }

    // #404 void(vector org, string modelname, float startframe, float endframe, float framerate) effect
    private void Effect(QcVm vm)
    {
        Parms(5, 5, "VM_SV_effect");
        string model = vm.ArgString(1);
        if (model.Length == 0) { Warning("effect: no model specified\n"); return; }
        int index = ModelIndex(model, 1);
        if (index == 0) { Warning("effect: model not precached\n"); return; }
        if (vm.ArgFloat(3) < 1) { Warning("effect: framecount < 1\n"); return; }
        if (vm.ArgFloat(4) < 1) { Warning("effect: framerate < 1\n"); return; }
        QcVector org = vm.ArgVector(0);
        int startFrame = QcVm.FloatToInt(vm.ArgFloat(2)), frameCount = QcVm.FloatToInt(vm.ArgFloat(3)), frameRate = QcVm.FloatToInt(vm.ArgFloat(4));
        if (index >= 256 || startFrame >= 256)
        {
            if (Datagram.Length > DpProtocol.MaxPacketFragment - 19) return;
            Datagram.WriteByte((int)Svc.Effect2);
            WriteVector(Datagram, org);
            Datagram.WriteShort(index);
            Datagram.WriteShort(startFrame);
        }
        else
        {
            if (Datagram.Length > DpProtocol.MaxPacketFragment - 17) return;
            Datagram.WriteByte((int)Svc.Effect);
            WriteVector(Datagram, org);
            Datagram.WriteByte(index);
            Datagram.WriteByte(startFrame);
        }
        Datagram.WriteByte(frameCount);
        Datagram.WriteByte(frameRate);
        FlushBroadcastMessages();
    }

    // #336 void(entity ent, float effectnum, vector start, vector end) trailparticles
    private void TrailParticles(QcVm vm)
    {
        Parms(4, 4, "VM_SV_trailparticles");
        // (The C tests the entity argument's cell as a float here; an entity number is never negative.)
        Datagram.WriteByte((int)Svc.TrailParticles);
        Datagram.WriteShort(vm.ArgInt(0));
        Datagram.WriteShort(QcVm.FloatToInt(vm.ArgFloat(1)));
        WriteVector(Datagram, vm.ArgVector(2));
        WriteVector(Datagram, vm.ArgVector(3));
        FlushBroadcastMessages();
    }

    // #337 void(float effectnum, vector origin, vector dir, float count) pointparticles
    private void PointParticles(QcVm vm)
    {
        Parms(4, 8, "VM_SV_pointparticles");
        int effectNum = QcVm.FloatToInt(vm.ArgFloat(0));
        if (effectNum < 0) return;
        QcVector org = vm.ArgVector(1), vel = vm.ArgVector(2);
        int count = Math.Clamp(QcVm.FloatToInt(vm.ArgFloat(3)), 0, 65535);
        if (count == 1 && Dot(vel, vel) == 0)
        {
            // 1+2+12=15 bytes
            Datagram.WriteByte((int)Svc.PointParticles1);
            Datagram.WriteShort(effectNum);
            WriteVector(Datagram, org);
        }
        else
        {
            // 1+2+12+12+2=29 bytes
            Datagram.WriteByte((int)Svc.PointParticles);
            Datagram.WriteShort(effectNum);
            WriteVector(Datagram, org);
            WriteVector(Datagram, vel);
            Datagram.WriteShort(count);
        }
        FlushBroadcastMessages();
    }

    // The temp-entity builtins (DP_TE_STANDARDEFFECTBUILTINS and friends). Each writes one
    // svc_temp_entity to the broadcast datagram.
    private void TeBegin(TempEntityType type)
    {
        Datagram.WriteByte((int)Svc.TempEntity);
        Datagram.WriteByte((int)type);
    }

    // te_gunshot, te_spike, te_explosion ... : just a position.
    private void TePoint(QcVm vm, TempEntityType type)
    {
        TeBegin(type);
        WriteVector(Datagram, vm.ArgVector(0));
        FlushBroadcastMessages();
    }

    // #405 void(vector org, vector velocity, float howmany) te_blood; #411 te_spark has the same layout
    private void TeBlood(QcVm vm) => TeBloodSpark(vm, TempEntityType.Blood);
    private void TeSpark(QcVm vm) => TeBloodSpark(vm, TempEntityType.Spark);
    private void TeBloodSpark(QcVm vm, TempEntityType type)
    {
        if (vm.ArgFloat(2) < 1) return;
        QcVector velocity = vm.ArgVector(1);
        TeBegin(type);
        WriteVector(Datagram, vm.ArgVector(0));
        Datagram.WriteChar(Math.Clamp(QcVm.FloatToInt(velocity.X), -128, 127));
        Datagram.WriteChar(Math.Clamp(QcVm.FloatToInt(velocity.Y), -128, 127));
        Datagram.WriteChar(Math.Clamp(QcVm.FloatToInt(velocity.Z), -128, 127));
        Datagram.WriteByte(Math.Clamp(QcVm.FloatToInt(vm.ArgFloat(2)), 0, 255));
        FlushBroadcastMessages();
    }

    // #406 void(vector mincorner, vector maxcorner, float explosionspeed, float howmany) te_bloodshower
    private void TeBloodShower(QcVm vm)
    {
        if (vm.ArgFloat(3) < 1) return;
        TeBegin(TempEntityType.BloodShower);
        WriteVector(Datagram, vm.ArgVector(0));
        WriteVector(Datagram, vm.ArgVector(1));
        Datagram.WriteCoord(vm.ArgFloat(2));
        Datagram.WriteShort((int)Math.Clamp(vm.ArgFloat(3), 0, 65535));
        FlushBroadcastMessages();
    }

    // #407 void(vector org, vector color) te_explosionrgb
    private void TeExplosionRgb(QcVm vm)
    {
        QcVector color = vm.ArgVector(1);
        TeBegin(TempEntityType.ExplosionRgb);
        WriteVector(Datagram, vm.ArgVector(0));
        Datagram.WriteByte(Math.Clamp(QcVm.FloatToInt(color.X * 255), 0, 255));
        Datagram.WriteByte(Math.Clamp(QcVm.FloatToInt(color.Y * 255), 0, 255));
        Datagram.WriteByte(Math.Clamp(QcVm.FloatToInt(color.Z * 255), 0, 255));
        FlushBroadcastMessages();
    }

    // #408 void(vector mincorner, vector maxcorner, vector vel, float howmany, float color, float gravityflag, float randomveljitter) te_particlecube
    private void TeParticleCube(QcVm vm)
    {
        if (vm.ArgFloat(3) < 1) return;
        TeBegin(TempEntityType.ParticleCube);
        WriteVector(Datagram, vm.ArgVector(0));
        WriteVector(Datagram, vm.ArgVector(1));
        WriteVector(Datagram, vm.ArgVector(2));
        Datagram.WriteShort((int)Math.Clamp(vm.ArgFloat(3), 0, 65535));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(4)));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(5)) != 0 ? 1 : 0);
        Datagram.WriteCoord(vm.ArgFloat(6));
        FlushBroadcastMessages();
    }

    // #409 / #410 void(vector mincorner, vector maxcorner, vector vel, float howmany, float color) te_particlerain / te_particlesnow
    private void TeParticleRainSnow(QcVm vm, TempEntityType type)
    {
        if (vm.ArgFloat(3) < 1) return;
        TeBegin(type);
        WriteVector(Datagram, vm.ArgVector(0));
        WriteVector(Datagram, vm.ArgVector(1));
        WriteVector(Datagram, vm.ArgVector(2));
        Datagram.WriteShort((int)Math.Clamp(vm.ArgFloat(3), 0, 65535));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(4)));
        FlushBroadcastMessages();
    }

    // #417 void(vector org, float radius, float lifetime, vector color) te_customflash
    private void TeCustomFlash(QcVm vm)
    {
        if (vm.ArgFloat(1) < 8 || vm.ArgFloat(2) < (1.0f / 256.0f)) return;
        QcVector color = vm.ArgVector(3);
        TeBegin(TempEntityType.CustomFlash);
        WriteVector(Datagram, vm.ArgVector(0));
        Datagram.WriteByte((int)Math.Clamp(vm.ArgFloat(1) / 8 - 1, 0, 255));
        Datagram.WriteByte((int)Math.Clamp(vm.ArgFloat(2) * 256 - 1, 0, 255));
        Datagram.WriteByte((int)Math.Clamp(color.X * 255, 0, 255));
        Datagram.WriteByte((int)Math.Clamp(color.Y * 255, 0, 255));
        Datagram.WriteByte((int)Math.Clamp(color.Z * 255, 0, 255));
        FlushBroadcastMessages();
    }

    // #427 void(vector org, float colorstart, float colorlength) te_explosion2
    private void TeExplosion2(QcVm vm)
    {
        TeBegin(TempEntityType.Explosion2);
        WriteVector(Datagram, vm.ArgVector(0));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(1)));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(2)));
        FlushBroadcastMessages();
    }

    // #428-#431 void(entity own, vector start, vector end) te_lightning1/2/3, te_beam
    private void TeBeam(QcVm vm, TempEntityType type)
    {
        TeBegin(type);
        Datagram.WriteShort(vm.ArgInt(0));
        WriteVector(Datagram, vm.ArgVector(1));
        WriteVector(Datagram, vm.ArgVector(2));
        FlushBroadcastMessages();
    }

    // #457 void(vector org, vector vel, float howmany) te_flamejet
    private void TeFlameJet(QcVm vm)
    {
        TeBegin(TempEntityType.FlameJet);
        WriteVector(Datagram, vm.ArgVector(0));
        WriteVector(Datagram, vm.ArgVector(1));
        Datagram.WriteByte(QcVm.FloatToInt(vm.ArgFloat(2)));
        FlushBroadcastMessages();
    }

    // ---- clients ---------------------------------------------------------------------------------------

    // #78 void(entity e) setspawnparms
    private void SetSpawnParms(QcVm vm)
    {
        Parms(1, 1, "VM_SV_setspawnparms");
        int i = vm.ArgInt(0);
        if (i < 1 || i > Clients.Length || !Clients[i - 1].Active)
        {
            Print("tried to setspawnparms on a non-client\n");
            return;
        }
        // copy spawn parms out of the client_t
        SvClient client = Clients[i - 1];
        for (int j = 0; j < SvClient.NumSpawnParms; j++) vm.GlobalFloat(G.Parm1 + j) = client.SpawnParms[j];
    }

    // #232 void(float index, float type, .void field) SV_AddStat
    private void AddStat(QcVm vm)
    {
        Parms(3, 3, "VM_SV_AddStat");
        int i = QcVm.FloatToInt(vm.ArgFloat(0)), type = QcVm.FloatToInt(vm.ArgFloat(1)), offset = vm.ArgInt(2);
        if (type != 1 && type != 2 && type != 8)
        {
            Warning($"PF_SV_AddStat: unrecognized type {type} - supported types are 1 (string up to 16 bytes, takes 4 stat slots), 2 (truncate to int32), 8 (send as float)");
            return;
        }
        if (i < 0) { Warning($"PF_SV_AddStat: index ({i}) may not be less than {MinVmStat}\n"); return; }
        if (i >= DpProtocol.MaxClStats) { Warning($"PF_SV_AddStat: index ({i}) >= MAX_CL_STATS ({DpProtocol.MaxClStats}), not supported by protocol\n"); return; }
        if (i > DpProtocol.MaxClStats - 4 && type == 1) { Warning($"PF_SV_AddStat: index ({i}) > (MAX_CL_STATS ({DpProtocol.MaxClStats}) - 4) with string type won't fit in the protocol\n"); return; }
        // A field offset from the program: it indexes entity memory every frame from now on.
        if ((uint)offset >= (uint)vm.EntityFields) { Warning($"PF_SV_AddStat: field offset {offset} is out of bounds\n"); return; }
        if (i < MinVmStat) Warning($"PF_SV_AddStat: index ({i}) < MIN_VM_STAT ({MinVmStat}) may conflict with engine stats - allowed, but this may break things\n");
        else if (i >= MaxVmStat && Cvars.GetFloat("sv_qcstats") == 0) Warning($"PF_SV_AddStat: index ({i}) >= MAX_VM_STAT ({MaxVmStat}) conflicts with engine stats - allowed, but this may break slowmo and stuff\n");
        CustomStats[i] = ((byte)type, offset);
        if (CustomStatsLast < i) CustomStatsLast = i;
    }

    // #401 void(entity client, float colors) setcolor
    private void SetColor(QcVm vm)
    {
        Parms(2, 2, "VM_SV_setcolor");
        int entNum = vm.ArgInt(0), colors = QcVm.FloatToInt(vm.ArgFloat(1));
        if (entNum < 1 || entNum > Clients.Length || !Clients[entNum - 1].Active)
        {
            Print("tried to setcolor a non-client\n");
            return;
        }
        SvClient client = Clients[entNum - 1];
        Fl(client.Edict, F.ClientColors) = colors;
        Fl(client.Edict, F.Team) = (colors & 15) + 1;
        client.Colors = colors;
        if (client.OldColors != client.Colors)
        {
            client.OldColors = client.Colors;
            // send notification to all clients
            ReliableDatagram.WriteByte((int)Svc.UpdateColors);
            ReliableDatagram.WriteByte(client.Index);
            ReliableDatagram.WriteByte(client.Colors);
        }
    }

    // #453 void(entity clent) dropclient
    private void DropClientBuiltin(QcVm vm)
    {
        Parms(1, 1, "VM_SV_dropclient");
        int clientNum = vm.ArgInt(0) - 1;
        if (clientNum < 0 || clientNum >= Clients.Length) { Warning("dropclient: not a client\n"); return; }
        if (!Clients[clientNum].Active) { Warning("dropclient: that client slot is not connected\n"); return; }
        int self = Self, other = Other;
        DropClient(Clients[clientNum], "Client dropped");
        Self = self;
        Other = other;
    }

    // #454 entity() spawnclient: a bot's slot. "the caller is responsible for setting up the client"
    private void SpawnClient(QcVm vm)
    {
        Parms(0, 0, "VM_SV_spawnclient");
        int ed = 0;
        for (int i = 0; i < Clients.Length; i++)
        {
            if (Clients[i].Active) continue;
            // SV_ConnectClient runs SetNewParms, which may leave self and the parms globals changed:
            // exactly what the C leaves behind too.
            ConnectClient(i, null);
            // this has to be set or else ClientDisconnect won't be called; we assume the qc will
            // call ClientConnect...
            Clients[i].ClientConnectCalled = true;
            ed = i + 1;
            break;
        }
        vm.ReturnInt(ed);
    }

    // #455 float(entity clent) clienttype
    private void ClientType(QcVm vm)
    {
        Parms(1, 1, "VM_SV_clienttype");
        int clientNum = vm.ArgInt(0) - 1;
        float type;
        if (clientNum < 0 || clientNum >= Clients.Length) type = 3;   // CLIENTTYPE_NOTACLIENT
        else if (!Clients[clientNum].Active) type = 0;                // CLIENTTYPE_DISCONNECTED
        else if (Clients[clientNum].Connection is not null) type = 1; // CLIENTTYPE_REAL
        else type = 2;                                                // CLIENTTYPE_BOT
        vm.ReturnFloat(type);
    }

    /// <summary>
    /// Executes a command line as if the client had sent it (clc_stringcmd, and the clientcommand
    /// builtin). Set by the network layer, which owns the client command table; with none, a command
    /// goes straight to the program's SV_ParseClientCommand, as an unknown command does in DarkPlaces.
    /// </summary>
    public Action<SvClient, string>? ClientCommandHandler { get; set; }

    // #440 void(entity e, string s) clientcommand (KRIMZON_SV_PARSECLIENTCOMMAND)
    private void ClientCommandBuiltin(QcVm vm)
    {
        Parms(2, 2, "VM_SV_clientcommand");
        int i = vm.ArgInt(0) - 1;
        if (i < 0 || i >= Clients.Length || !Clients[i].Active)
        {
            Print("PF_clientcommand: entity is not a client\n");
            return;
        }
        string text = vm.ArgString(1);
        if (ClientCommandHandler is { } handler) handler(Clients[i], text);
        else ParseClientCommand(Clients[i], text);
    }

    /// <summary>The program's SV_ParseClientCommand for a client (self) and a command line. Runs inside whatever is executing; a fault propagates.</summary>
    internal void ParseClientCommand(SvClient client, string text)
    {
        if (Fn.SvParseClientCommand == 0) return;
        int saveSelf = Self;
        SetTime(Time);
        Self = client.Edict;
        Vm.SetArgInt(0, Vm.TempString(text));
        Vm.Execute(Fn.SvParseClientCommand, 1);
        Self = saveSelf;
    }

    // ---- the engine around the program -----------------------------------------------------------------

    // #70 void(string s) changelevel
    private void ChangeLevel(QcVm vm)
    {
        Parms(1, 1, "VM_changelevel");
        // make sure we don't issue two changelevels
        if (ChangeLevelIssued) return;
        ChangeLevelIssued = true;
        Services.LocalCommand($"changelevel {vm.ArgString(0)}\n");
    }

    // #352 void(string cmdname) registercommand: typing the command calls ConsoleCmd with the line.
    private void RegisterCommand(QcVm vm)
    {
        Parms(1, 1, "VM_SV_registercommand");
        RegisterQcCommand(vm.ArgString(0));
    }

    // #531 void(float pause) setpause
    private void SetPause(QcVm vm)
    {
        Parms(1, 1, "VM_SV_setpause");
        if (QcVm.FloatToInt(vm.ArgFloat(0)) != 0)
        {
            Paused = true;
            PausedStart = RealTime;
        }
        else if (Paused)
        {
            Paused = false;
            PausedStart = 0;
        }
        // send notification to all clients
        ReliableDatagram.WriteByte((int)Svc.SetPause);
        ReliableDatagram.WriteByte(Paused ? 1 : 0);
    }

    // #513 float(string uri, float id[, string post_contenttype, ...]) uri_get: no HTTP here, so the
    // request is refused, as DarkPlaces refuses it without libcurl (and DP_QC_URI_GET is not advertised).
    private void UriGet(QcVm vm) => vm.ReturnFloat(0);

    // #624 string() getextresponse: the next "extResponse" packet a master or other server sent us.
    // None is ever received: the connectionless layer does not queue them.
    private void GetExtResponse(QcVm vm) => vm.ReturnInt(0);
}
