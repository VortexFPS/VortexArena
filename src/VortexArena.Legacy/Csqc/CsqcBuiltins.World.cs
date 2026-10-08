// Port of Base/darkplaces/clvm_cmds.c VM_CL_setorigin, SetMinMaxSize, VM_CL_setmodel, VM_CL_setsize,
// VM_CL_precache_model, VM_CL_setmodelindex, VM_CL_modelnameforindex, VM_CL_traceline, VM_CL_tracebox,
// CL_Trace_Toss, VM_CL_tracetoss, VM_CL_findradius, VM_CL_findbox, VM_CL_droptofloor, VM_CL_checkbottom,
// VM_CL_pointcontents, VM_CL_walkmove, VM_CL_checkpvs; prvm_cmds.c VM_precache_sound, VM_SetTraceGlobals;
// cl_collision.c CL_GenericHitSuperContentsMask; model_brush.c Mod_Q1BSP_NativeContentsFromSuperContents.
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed partial class CsqcBuiltins
{
    // server.h / world.h / bspfile.h
    private const float SolidNot = 0, SolidTrigger = 1, SolidSlideBox = 3, SolidCorpse = 5;
    private const int FlFly = 1, FlSwim = 2, FlMonster = 32, FlOnGround = 512;
    private const int MoveNormal = 0;
    private const int SuperSolid = 0x1, SuperWater = 0x2, SuperSlime = 0x4, SuperLava = 0x8, SuperSky = 0x10,
        SuperBody = 0x20, SuperCorpse = 0x40, SuperPlayerClip = 0x100, SuperMonsterClip = 0x200;

    private readonly int[] _touched = new int[DpProtocol.MaxEdicts];

    private void RegisterWorld()
    {
        Here(2, "setorigin", SetOrigin);
        Here(3, "setmodel", SetModel);
        Here(4, "setsize", SetSize);
        Forward(16, "traceline", TraceLine);
        Here(19, "precache_sound", PrecacheSound);
        Here(20, "precache_model", PrecacheModel);
        Here(22, "findradius", FindRadius);
        Forward(32, "walkmove", WalkMove);
        Forward(34, "droptofloor", DropToFloor);
        Forward(40, "checkbottom", CheckBottom);
        Forward(41, "pointcontents", PointContents);
        Forward(64, "tracetoss", TraceToss);
        Here(75, "precache_model2", PrecacheModel);
        Here(76, "precache_sound2", PrecacheSound);
        Forward(90, "tracebox", TraceBox);
        Forward(240, "checkpvs", CheckPvs);
        Here(333, "setmodelindex", SetModelIndex);
        Here(334, "modelnameforindex", ModelNameForIndex);
        Here(566, "findbox", FindBox);
    }

    // ---- placing entities --------------------------------------------------------------------------

    // #2 void(entity e, vector o) setorigin
    private void SetOrigin(QcVm vm)
    {
        Parms(2, "VM_CL_setorigin");
        int e = vm.ArgEdict(0);
        if (e == 0) { Warning("setorigin: can not modify world entity\n"); return; }
        if (vm.IsFree(e)) { Warning("setorigin: can not modify free entity\n"); return; }
        vm.FieldVector(e, _f.Origin) = vm.ArgVector(1);
        _host.LinkEdict(e);
    }

    // SetMinMaxSize: mins, maxs and the derived size, then relink.
    private void SetMinMaxSize(int e, QcVector min, QcVector max)
    {
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) throw Fault("SetMinMaxSize: backwards mins/maxs");
        _vm.FieldVector(e, _f.Mins) = min;
        _vm.FieldVector(e, _f.Maxs) = max;
        _vm.FieldVector(e, _f.Size) = new QcVector(max.X - min.X, max.Y - min.Y, max.Z - min.Z);
        _host.LinkEdict(e);
    }

    // #3 void(entity e, string m) setmodel. The model must have been precached - by the program
    // (a negative model index) or by the server (a positive one). Unlike setorigin, the C makes no
    // check for the world or a free entity here.
    private void SetModel(QcVm vm)
    {
        Parms(2, "VM_CL_setmodel");
        int e = vm.ArgEdict(0);
        vm.FieldFloat(e, _f.ModelIndex) = 0;
        vm.FieldInt(e, _f.Model) = 0;

        string name = vm.ArgString(1);
        int index = 0;
        int slot = _state.CsqcModels.IndexOf(name);
        if (slot >= 0) index = -(slot + 1);
        else
        {
            // cl.model_precache[0] is never loaded, so the search effectively starts at 1.
            string?[] names = _state.ModelNames;
            for (int i = 1; i < names.Length; i++)
                if (names[i] is { } candidate && string.Equals(candidate, name, StringComparison.Ordinal)) { index = i; break; }
        }

        if (index != 0)
        {
            vm.FieldInt(e, _f.Model) = vm.EngineString(name);
            vm.FieldFloat(e, _f.ModelIndex) = index;
            // "setmodel must do setsize or else the qc can't find out the model size"
            _host.ModelBounds(name, out QcVector mins, out QcVector maxs);
            SetMinMaxSize(e, mins, maxs);
        }
        else
        {
            SetMinMaxSize(e, default, default);
            Warning($"setmodel: model '{name}' not precached\n");
        }
    }

    // #4 void(entity e, vector min, vector max) setsize
    private void SetSize(QcVm vm)
    {
        Parms(3, "VM_CL_setsize");
        int e = vm.ArgEdict(0);
        if (e == 0) { Warning("setsize: can not modify world entity\n"); return; }
        if (vm.IsFree(e)) { Warning("setsize: can not modify free entity\n"); return; }
        SetMinMaxSize(e, vm.ArgVector(1), vm.ArgVector(2));
    }

    // #333 void(entity e, float mdlindex) setmodelindex
    private void SetModelIndex(QcVm vm)
    {
        Parms(2, "VM_CL_setmodelindex");
        int e = vm.ArgEdict(0);
        int index = ArgInt(1);
        vm.FieldInt(e, _f.Model) = 0;
        vm.FieldFloat(e, _f.ModelIndex) = 0;
        if (index == 0) return;
        string? name = _state.ModelNameForIndex(index);
        if (name is null)
        {
            Warning("VM_CL_setmodelindex: null model\n");
            return;
        }
        vm.FieldInt(e, _f.Model) = vm.EngineString(name);
        vm.FieldFloat(e, _f.ModelIndex) = index;
        _host.ModelBounds(name, out QcVector mins, out QcVector maxs);
        SetMinMaxSize(e, mins, maxs);
    }

    // #334 string(float mdlindex) modelnameforindex
    private void ModelNameForIndex(QcVm vm)
    {
        Parms(1, "VM_CL_modelnameforindex");
        string? name = _state.ModelNameForIndex(ArgInt(0));
        vm.ReturnInt(name is null ? 0 : vm.EngineString(name));
    }

    // #20, #75 float(string s) precache_model: the program's own model list. Returns the (negative)
    // model index, or 0 if the model cannot be loaded.
    private void PrecacheModel(QcVm vm)
    {
        Parms(1, "VM_CL_precache_model");
        string name = vm.ArgString(0);
        int slot = _state.CsqcModels.IndexOf(name);
        if (slot >= 0)
        {
            vm.ReturnFloat(-(slot + 1));
            return;
        }
        vm.ReturnFloat(0);
        if (!_host.ModelBounds(name, out _, out _))
        {
            Warning($"VM_CL_precache_model: model \"{name}\" not found\n");
            return;
        }
        if (_state.CsqcModels.Count >= DpProtocol.MaxModels)
        {
            Warning("VM_CL_precache_model: no free models\n");
            return;
        }
        _state.CsqcModels.Add(name);
        vm.ReturnFloat(-_state.CsqcModels.Count);
    }

    // #19, #76 string(string s) precache_sound: returns its argument, loaded or not.
    private void PrecacheSound(QcVm vm)
    {
        Parms(1, "VM_precache_sound");
        string name = vm.ArgString(0);
        vm.ReturnInt(vm.ArgInt(0));
        if (!_presentation.Sound.Precache(name)) Warning($"VM_precache_sound: Failed to load {name} !\n");
    }

    // ---- traces ------------------------------------------------------------------------------------

    internal int HitContentsMask(int ignore)
    {
        int mask = Int(_vm.FieldFloat(ignore, _f.DpHitContentsMask));
        if (mask != 0) return mask;
        float solid = _vm.FieldFloat(ignore, _f.Solid);
        if (solid == SolidSlideBox)
            return (Int(_vm.FieldFloat(ignore, _f.Flags)) & FlMonster) != 0
                ? SuperSolid | SuperBody | SuperMonsterClip
                : SuperSolid | SuperBody | SuperPlayerClip;
        if (solid == SolidCorpse || solid == SolidTrigger) return SuperSolid | SuperBody;
        return SuperSolid | SuperBody | SuperCorpse;
    }

    // VM_SetTraceGlobals + trace_networkentity (CL_VM_SetTraceGlobals).
    internal void SetTraceGlobals(in LegacyTrace trace)
    {
        _host.SetFloat(_g.TraceAllSolid, trace.AllSolid ? 1 : 0);
        _host.SetFloat(_g.TraceStartSolid, trace.StartSolid ? 1 : 0);
        _host.SetFloat(_g.TraceFraction, trace.Fraction);
        _host.SetFloat(_g.TraceInWater, trace.InWater ? 1 : 0);
        _host.SetFloat(_g.TraceInOpen, trace.InOpen ? 1 : 0);
        _host.SetVector(_g.TraceEndPos, trace.EndPos);
        _host.SetVector(_g.TracePlaneNormal, trace.PlaneNormal);
        _host.SetFloat(_g.TracePlaneDist, trace.PlaneDist);
        // The presentation names an entity; the program must never be handed one outside its array.
        _host.SetInt(_g.TraceEnt, (uint)trace.Entity < (uint)_vm.NumEdicts ? trace.Entity : 0);
        _host.SetFloat(_g.TraceDpStartContents, trace.StartContents);
        _host.SetFloat(_g.TraceDpHitContents, trace.HitContents);
        _host.SetFloat(_g.TraceDpHitQ3SurfaceFlags, trace.HitQ3SurfaceFlags);
        _host.SetInt(_g.TraceDpHitTextureName, trace.HitTextureName is null ? 0 : _vm.TempString(trace.HitTextureName));
        _host.SetFloat(_g.TraceNetworkEntity, trace.NetworkEntity);
    }

    // #16 void(vector v1, vector v2, float movetype, entity ignore) traceline
    private void TraceLine(QcVm vm)
    {
        Parms(4, "VM_CL_traceline");
        QcVector start = vm.ArgVector(0), end = vm.ArgVector(1);
        int move = ArgInt(2);
        int ignore = vm.ArgEdict(3);
        if (IsNaN(start) || IsNaN(end))
            throw Fault($"NAN errors detected in traceline('{start.X} {start.Y} {start.Z}', '{end.X} {end.Y} {end.Z}', {move}, entity {ignore})");
        SetTraceGlobals(_presentation.World.Trace(start, default, default, end, move, ignore, HitContentsMask(ignore), isLine: true));
    }

    // #90 void(vector v1, vector mins, vector maxs, vector v2, float movetype, entity ignore) tracebox
    private void TraceBox(QcVm vm)
    {
        Parms(6, 8, "VM_CL_tracebox"); // "allow more parameters for future expansion"
        QcVector start = vm.ArgVector(0), mins = vm.ArgVector(1), maxs = vm.ArgVector(2), end = vm.ArgVector(3);
        int move = ArgInt(4);
        int ignore = vm.ArgEdict(5);
        if (IsNaN(start) || IsNaN(end))
            throw Fault($"NAN errors detected in tracebox('{start.X} {start.Y} {start.Z}', ..., '{end.X} {end.Y} {end.Z}', {move}, entity {ignore})");
        SetTraceGlobals(_presentation.World.Trace(start, mins, maxs, end, move, ignore, HitContentsMask(ignore), isLine: false));
    }

    // #64 void(entity e, entity ignore) tracetoss: where would this entity land if left to fall?
    // Simulates up to ten seconds in 0.05 s steps on the entity's own fields, then puts them back.
    private void TraceToss(QcVm vm)
    {
        Parms(2, "VM_CL_tracetoss");
        int e = vm.ArgEdict(0);
        if (e == 0) { Warning("tracetoss: can not use world entity\n"); return; }
        vm.ArgEdict(1); // the ignore entity is validated and, as in the C, not otherwise used

        QcVector originalOrigin = vm.FieldVector(e, _f.Origin), originalVelocity = vm.FieldVector(e, _f.Velocity);
        QcVector originalAngles = vm.FieldVector(e, _f.Angles), originalAVelocity = vm.FieldVector(e, _f.AVelocity);

        float gravity = vm.FieldFloat(e, _f.Gravity);
        if (gravity == 0) gravity = 1.0f;
        gravity *= _state.Gravity * 0.05f;

        LegacyTrace trace = default;
        int mask = HitContentsMask(e);
        for (int i = 0; i < 200; i++) // "sanity check; never trace more than 10 seconds"
        {
            ref QcVector velocity = ref vm.FieldVector(e, _f.Velocity);
            velocity.Z -= gravity;
            ref QcVector angles = ref vm.FieldVector(e, _f.Angles);
            QcVector spin = vm.FieldVector(e, _f.AVelocity);
            angles = new QcVector(angles.X + 0.05f * spin.X, angles.Y + 0.05f * spin.Y, angles.Z + 0.05f * spin.Z);
            QcVector start = vm.FieldVector(e, _f.Origin);
            QcVector end = new(start.X + velocity.X * 0.05f, start.Y + velocity.Y * 0.05f, start.Z + velocity.Z * 0.05f);
            trace = _presentation.World.Trace(start, vm.FieldVector(e, _f.Mins), vm.FieldVector(e, _f.Maxs), end, MoveNormal, e, mask, isLine: false);
            vm.FieldVector(e, _f.Origin) = trace.EndPos;
            if (trace.Fraction < 1) break;
        }

        vm.FieldVector(e, _f.Origin) = originalOrigin;
        vm.FieldVector(e, _f.Velocity) = originalVelocity;
        vm.FieldVector(e, _f.Angles) = originalAngles;
        vm.FieldVector(e, _f.AVelocity) = originalAVelocity;
        // svent is initialised to 0 and CL_Trace_Toss passes NULL for it: no network entity.
        trace.NetworkEntity = 0;
        SetTraceGlobals(trace);
    }

    // #34 float() droptofloor: move self straight down onto whatever is below it.
    private void DropToFloor(QcVm vm)
    {
        Parms(0, 2, "VM_CL_droptofloor"); // "allow 2 parameters because the id1 defs.qc had an incorrect prototype"
        vm.ReturnFloat(0);
        int e = Self;
        if (e == 0) { Warning("droptofloor: can not modify world entity\n"); return; }
        if (vm.IsFree(e)) { Warning("droptofloor: can not modify free entity\n"); return; }

        QcVector start = vm.FieldVector(e, _f.Origin);
        QcVector end = new(start.X, start.Y, start.Z - _presentation.World.DropToFloorDistance);
        LegacyTrace trace = _presentation.World.Trace(start, vm.FieldVector(e, _f.Mins), vm.FieldVector(e, _f.Maxs), end, MoveNormal, e, HitContentsMask(e), isLine: false);
        if (trace.Fraction != 1)
        {
            vm.FieldVector(e, _f.Origin) = trace.EndPos;
            vm.FieldFloat(e, _f.Flags) = Int(vm.FieldFloat(e, _f.Flags)) | FlOnGround;
            vm.FieldInt(e, _f.GroundEntity) = (uint)trace.Entity < (uint)vm.NumEdicts ? trace.Entity : 0;
            vm.ReturnFloat(1);
        }
    }

    // #40 float(entity e) checkbottom: is there ground under all four corners of the entity's box?
    private void CheckBottom(QcVm vm)
    {
        Parms(1, "VM_CL_checkbottom");
        int e = vm.ArgEdict(0);
        vm.ReturnFloat(0);
        QcVector origin = vm.FieldVector(e, _f.Origin), entMins = vm.FieldVector(e, _f.Mins), entMaxs = vm.FieldVector(e, _f.Maxs);
        QcVector mins = new(origin.X + entMins.X, origin.Y + entMins.Y, origin.Z + entMins.Z);
        QcVector maxs = new(origin.X + entMaxs.X, origin.Y + entMaxs.Y, origin.Z + entMaxs.Z);
        ILegacyWorld world = _presentation.World;

        // "if all of the points under the corners are solid world, don't bother with the tougher checks"
        bool easy = true;
        for (int x = 0; x <= 1 && easy; x++)
            for (int y = 0; y <= 1 && easy; y++)
            {
                QcVector corner = new(x != 0 ? maxs.X : mins.X, y != 0 ? maxs.Y : mins.Y, mins.Z - 1);
                if ((world.PointSuperContents(corner) & (SuperSolid | SuperBody)) == 0) easy = false;
            }
        if (easy) { vm.ReturnFloat(1); return; }

        // "check it for real": the midpoint must be within 2 steps of the bottom...
        float stepHeight = _host.Services.CvarExists("sv_stepheight") ? _host.Services.CvarFloat("sv_stepheight") : 18;
        int mask = HitContentsMask(e);
        QcVector start = new((mins.X + maxs.X) * 0.5f, (mins.Y + maxs.Y) * 0.5f, mins.Z);
        QcVector stop = new(start.X, start.Y, start.Z - 2 * stepHeight);
        LegacyTrace trace = world.Trace(start, default, default, stop, MoveNormal, e, mask, isLine: true);
        if (trace.Fraction == 1.0f) return;
        float mid = trace.EndPos.Z, bottom = mid;

        // ...and "the corners must be within 16 of the midpoint".
        for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            {
                start.X = stop.X = x != 0 ? maxs.X : mins.X;
                start.Y = stop.Y = y != 0 ? maxs.Y : mins.Y;
                trace = world.Trace(start, default, default, stop, MoveNormal, e, mask, isLine: true);
                if (trace.Fraction != 1.0f && trace.EndPos.Z > bottom) bottom = trace.EndPos.Z;
                if (trace.Fraction == 1.0f || mid - trace.EndPos.Z > stepHeight) return;
            }
        vm.ReturnFloat(1);
    }

    // #41 float(vector v) pointcontents: the Quake CONTENTS_* number of a point.
    private void PointContents(QcVm vm)
    {
        Parms(1, "VM_CL_pointcontents");
        int super = _presentation.World.PointSuperContents(vm.ArgVector(0));
        int native = (super & (SuperSolid | SuperBody)) != 0 ? -2   // CONTENTS_SOLID
            : (super & SuperSky) != 0 ? -6
            : (super & SuperLava) != 0 ? -5
            : (super & SuperSlime) != 0 ? -4
            : (super & SuperWater) != 0 ? -3
            : -1;                                                    // CONTENTS_EMPTY
        vm.ReturnFloat(native);
    }

    // #32 float(float yaw, float dist[, float settrace]) walkmove
    private void WalkMove(QcVm vm)
    {
        Parms(2, 3, "VM_CL_walkmove");
        vm.ReturnFloat(0);
        int e = Self;
        if (e == 0) { Warning("walkmove: can not modify world entity\n"); return; }
        if (vm.IsFree(e)) { Warning("walkmove: can not modify free entity\n"); return; }
        float yaw = vm.ArgFloat(0), distance = vm.ArgFloat(1);
        bool setTrace = vm.ArgCount >= 3 && vm.ArgFloat(2) != 0;
        if ((Int(vm.FieldFloat(e, _f.Flags)) & (FlOnGround | FlFly | FlSwim)) == 0) return;

        double radians = yaw * Math.PI * 2 / 360;
        QcVector move = new((float)(Math.Cos(radians) * distance), (float)(Math.Sin(radians) * distance), 0);
        // "save program state, because CL_movestep may call other progs" (a touch function).
        int oldSelf = _host.GetInt(_g.Self);
        bool moved = _presentation.World.MoveStep(e, move, setTrace);
        _host.SetInt(_g.Self, oldSelf);
        vm.ReturnFloat(moved ? 1 : 0);
    }

    // #240 float(vector viewpos, entity viewee) checkpvs
    private void CheckPvs(QcVm vm)
    {
        Parms(2, "VM_CL_checkpvs");
        int viewee = vm.ArgEdict(1);
        if (vm.IsFree(viewee))
        {
            Warning("checkpvs: can not check free entity\n");
            vm.ReturnFloat(4);
            return;
        }
        QcVector origin = vm.FieldVector(viewee, _f.Origin), mins = vm.FieldVector(viewee, _f.Mins), maxs = vm.FieldVector(viewee, _f.Maxs);
        vm.ReturnFloat(_presentation.World.CheckPvs(vm.ArgVector(0),
            new QcVector(origin.X + mins.X, origin.Y + mins.Y, origin.Z + mins.Z),
            new QcVector(origin.X + maxs.X, origin.Y + maxs.Y, origin.Z + maxs.Z)));
    }

    // ---- area queries ------------------------------------------------------------------------------

    private int ChainField(int argIndex, string name)
    {
        int field = _vm.ArgCount == argIndex + 1 ? _vm.ArgInt(argIndex) : _f.Chain;
        if ((uint)field >= (uint)_vm.EntityFields) throw Fault($"{name}: field offset {field} is out of bounds");
        return field;
    }

    // #22 entity(vector org, float rad[, .entity chainfield]) findradius: the entities within a
    // radius, linked through .chain, last found first.
    private void FindRadius(QcVm vm)
    {
        Parms(2, 3, "VM_CL_findradius");
        int chainField = ChainField(2, "VM_CL_findradius");
        QcVector org = vm.ArgVector(0);
        float radius = vm.ArgFloat(1);
        float radius2 = radius * radius;
        QcVector mins = new(org.X - (radius + 1), org.Y - (radius + 1), org.Z - (radius + 1));
        QcVector maxs = new(org.X + (radius + 1), org.Y + (radius + 1), org.Z + (radius + 1));
        int count = _host.EntitiesInBox(mins, maxs, _touched);

        IQcHost services = _host.Services;
        bool includeNonSolid = !services.CvarExists("sv_gameplayfix_blowupfallenzombies") || services.CvarFloat("sv_gameplayfix_blowupfallenzombies") != 0;
        bool distanceToBox = !services.CvarExists("sv_gameplayfix_findradiusdistancetobox") || services.CvarFloat("sv_gameplayfix_findradiusdistancetobox") != 0;

        int chain = 0;
        for (int i = 0; i < count; i++)
        {
            int e = _touched[i];
            // "Quake did not return non-solid entities but darkplaces does"
            if (!includeNonSolid && vm.FieldFloat(e, _f.Solid) == SolidNot) continue;
            QcVector origin = vm.FieldVector(e, _f.Origin), emins = vm.FieldVector(e, _f.Mins), emaxs = vm.FieldVector(e, _f.Maxs);
            float x = org.X - origin.X, y = org.Y - origin.Y, z = org.Z - origin.Z;
            if (distanceToBox)
            {
                // "compare against bounding box rather than center so it doesn't miss large objects"
                x -= Bound(emins.X, x, emaxs.X);
                y -= Bound(emins.Y, y, emaxs.Y);
                z -= Bound(emins.Z, z, emaxs.Z);
            }
            else
            {
                x -= 0.5f * (emins.X + emaxs.X);
                y -= 0.5f * (emins.Y + emaxs.Y);
                z -= 0.5f * (emins.Z + emaxs.Z);
            }
            if (x * x + y * y + z * z < radius2)
            {
                vm.FieldInt(e, chainField) = chain;
                chain = e;
            }
        }
        vm.ReturnInt(chain);

        // mathlib.h bound(): the lower limit is tested first, which decides the answer for a reversed box.
        static float Bound(float min, float value, float max) => value < min ? min : value > max ? max : value;
    }

    // #566 entity(vector mins, vector maxs[, .entity tofield]) findbox
    private void FindBox(QcVm vm)
    {
        Parms(2, 3, "VM_CL_findbox");
        int chainField = ChainField(2, "VM_CL_findbox");
        int count = _host.EntitiesInBox(vm.ArgVector(0), vm.ArgVector(1), _touched);
        int chain = 0;
        for (int i = 0; i < count; i++)
        {
            vm.FieldInt(_touched[i], chainField) = chain;
            chain = _touched[i];
        }
        vm.ReturnInt(chain);
    }
}
