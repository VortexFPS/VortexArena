// Port of Base/darkplaces/sv_phys.c: SV_CheckContentsTransition, SV_CheckVelocity, SV_RunThink,
// SV_Impact, ClipVelocity, SV_FlyMove, SV_Gravity, SV_NudgeOutOfSolid_PivotIsKnownGood, SV_PushEntity,
// SV_PushMove, SV_Physics_Pusher, SV_UnstickEntity, SV_CheckWater, SV_WallFriction, SV_WalkMove,
// SV_Physics_Follow, SV_CheckWaterTransition, SV_Physics_Toss, SV_Physics_Step, SV_Physics_Entity,
// SV_Physics_ClientEntity_NoThink, SV_Physics_ClientMove, SV_Physics_ClientEntity_PreThink,
// SV_Physics_ClientEntity_PostThink, SV_Physics_ClientEntity, SV_Physics; phys.c
// PHYS_TestEntityPosition, PHYS_UnstickEntityReturnOffset; sv_move.c SV_CheckBottom, SV_movestep,
// SV_StepDirection, SV_NewChaseDir, SV_CloseEnough, VM_SV_MoveToGoal; svvm_cmds.c VM_SV_walkmove,
// VM_SV_droptofloor, VM_SV_checkbottom, VM_SV_aim; sv_user.c SV_PlayerPhysics (the QuakeC branch),
// SV_ApplyClientMove; prvm_cmds.c VM_changeyaw; sv_main.c SV_Frame (the tick accumulator).
using System.Buffers;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvqcHost
{
    private const int MarkWaitForSetOrigin = -1, MarkSetOriginCaught = -2;
    private const int MaxClipPlanes = 5;
    private const float StopEpsilon = 0.1f;

    /// <summary>
    /// The cvars the frame reads, taken once per frame: DarkPlaces reads a cvar_t's cached value, and a
    /// name lookup per entity per frame is not that.
    /// </summary>
    internal struct FrameCvars
    {
        public float Gravity, MaxVelocity, StepHeight, TicRate, TimeScale, FrameRate, Aim;
        public int DelayProjectiles, StepDown;
        public bool JumpStep, NoStep, WallFriction, FreezeNonClients, PlayerPhysicsQc, LegacyBBoxExpand, SetModelRealBox, QcStats,
            FindRadiusDistanceToBox, BlowUpFallenZombies, ConsistentPlayerPreThink, MultipleThinksPerFrame, ImpactBeforeOnGround,
            NoGravityOnGround, GravityUnaffectedByTicRate, EasierWaterJump, DownTraceSupportsOnGroundFlag, StepMultipleTimes,
            UpwardVelocityClearsOnGroundFlag, NoAirbornCorpse, NoAirbornCorpseAllowSuspendedItems, SlideMoveProjectiles,
            GrenadeBounceDownSlopes, FixedCheckWaterTransition, NoSquashEntities, UnstickPlayers, UnstickEntities,
            DropToFloorStartSolid, NoStepMoveOnSteepSlopes, TeamPlay;
        public string SoundLand, SoundWaterSplash;
        public int MaxPhysicsFramesPerServerFrame;
        public float ClMovementInputTimeout, NudgeOutOfSolidSeparation;
        /// <summary>collision_extendmovelength (16), collision_extendtracelinelength (1), collision_extendtraceboxlength (1).</summary>
        public float ExtendMoveLength, ExtendTraceLineLength, ExtendTraceBoxLength;
        public bool NudgeOutOfSolid;
    }

    internal FrameCvars _cv;
    private bool _cvarsDirty = true;
    private double _timer;

    private float Cv(string name, float fallback) => Cvars.Has(name) ? Cvars.GetFloat(name) : fallback;
    private bool Cv(string name, bool fallback) => Cvars.Has(name) ? Cvars.GetFloat(name) != 0 : fallback;

    /// <summary>Re-reads the frame's cvars if any cvar changed since the last read. Defaults are DarkPlaces' (sv_main.c).</summary>
    internal void RefreshCvars()
    {
        if (!_cvarsDirty) return;
        _cvarsDirty = false;
        _cv = new FrameCvars
        {
            Gravity = Cv("sv_gravity", 800), MaxVelocity = Cv("sv_maxvelocity", 2000), StepHeight = Cv("sv_stepheight", 18),
            TicRate = Cv("sys_ticrate", 1f / 72), TimeScale = Cv("host_timescale", 1), FrameRate = Cv("host_framerate", 0),
            Aim = Cv("sv_aim", 2), DelayProjectiles = (int)Cv("sv_gameplayfix_delayprojectiles", 1),
            StepDown = (int)Cv("sv_gameplayfix_stepdown", 0), JumpStep = Cv("sv_jumpstep", false), NoStep = Cv("sv_nostep", false),
            WallFriction = Cv("sv_wallfriction", true), FreezeNonClients = Cv("sv_freezenonclients", false),
            PlayerPhysicsQc = Cv("sv_playerphysicsqc", true), LegacyBBoxExpand = Cv("sv_legacy_bbox_expand", true),
            SetModelRealBox = Cv("sv_gameplayfix_setmodelrealbox", true), QcStats = Cv("sv_qcstats", false),
            FindRadiusDistanceToBox = Cv("sv_gameplayfix_findradiusdistancetobox", true),
            BlowUpFallenZombies = Cv("sv_gameplayfix_blowupfallenzombies", true),
            ConsistentPlayerPreThink = Cv("sv_gameplayfix_consistentplayerprethink", false),
            MultipleThinksPerFrame = Cv("sv_gameplayfix_multiplethinksperframe", true),
            ImpactBeforeOnGround = Cv("sv_gameplayfix_impactbeforeonground", false),
            NoGravityOnGround = Cv("sv_gameplayfix_nogravityonground", false),
            GravityUnaffectedByTicRate = Cv("sv_gameplayfix_gravityunaffectedbyticrate", false),
            EasierWaterJump = Cv("sv_gameplayfix_easierwaterjump", true),
            DownTraceSupportsOnGroundFlag = Cv("sv_gameplayfix_downtracesupportsongroundflag", true),
            StepMultipleTimes = Cv("sv_gameplayfix_stepmultipletimes", false),
            UpwardVelocityClearsOnGroundFlag = Cv("sv_gameplayfix_upwardvelocityclearsongroundflag", true),
            NoAirbornCorpse = Cv("sv_gameplayfix_noairborncorpse", true),
            NoAirbornCorpseAllowSuspendedItems = Cv("sv_gameplayfix_noairborncorpse_allowsuspendeditems", true),
            SlideMoveProjectiles = Cv("sv_gameplayfix_slidemoveprojectiles", true),
            GrenadeBounceDownSlopes = Cv("sv_gameplayfix_grenadebouncedownslopes", true),
            FixedCheckWaterTransition = Cv("sv_gameplayfix_fixedcheckwatertransition", true),
            NoSquashEntities = Cv("sv_gameplayfix_nosquashentities", false),
            UnstickPlayers = Cv("sv_gameplayfix_unstickplayers", false), UnstickEntities = Cv("sv_gameplayfix_unstickentities", true),
            DropToFloorStartSolid = Cv("sv_gameplayfix_droptofloorstartsolid", true),
            NoStepMoveOnSteepSlopes = Cv("sv_gameplayfix_nostepmoveonsteepslopes", false), TeamPlay = Cv("teamplay", false),
            SoundLand = Cvars.GetString("sv_sound_land"), SoundWaterSplash = Cvars.GetString("sv_sound_watersplash"),
            MaxPhysicsFramesPerServerFrame = (int)Cv("sv_maxphysicsframesperserverframe", 10),
            ClMovementInputTimeout = Cv("sv_clmovement_inputtimeout", 0.1f),
            NudgeOutOfSolid = Cv("sv_gameplayfix_nudgeoutofsolid", false),
            NudgeOutOfSolidSeparation = Cv("sv_gameplayfix_nudgeoutofsolid_separation", 0.03125f),
            ExtendMoveLength = Cv("collision_extendmovelength", 16), ExtendTraceLineLength = Cv("collision_extendtracelinelength", 1),
            ExtendTraceBoxLength = Cv("collision_extendtraceboxlength", 1),
        };
    }

    // ---- SV_Frame --------------------------------------------------------------------------------------

    /// <summary>
    /// The physics half of SV_Frame: add <paramref name="elapsed"/> seconds of real time to the tick
    /// accumulator and run the server frames that are due, each sys_ticrate long (at most
    /// sv_maxphysicsframesperserverframe of them; time beyond a tenth of a second is dropped, as in
    /// DarkPlaces - an overloaded server slows down rather than spirals). Returns the frames run.
    /// The caller sends to clients afterwards (SV_SendClientMessages) if any were.
    /// </summary>
    public int Advance(double elapsed)
    {
        FrameBlockRan = false;
        if (!CanRun || State != SvState.Active || !(elapsed >= 0)) return 0;
        RefreshCvars();
        // "if the accumulator hasn't become positive, don't run the frame"
        _timer += elapsed;
        if (_timer < 0) return 0;
        // limit the frametime steps to no more than 100ms each
        if (_timer > 0.1) _timer = 0.1;
        int frameCount = 0;
        if (_timer > 0)
        {
            FrameBlockRan = true;
            double advanceTime;
            int frameLimit = 1;
            if (_cv.TicRate <= 0) advanceTime = _timer;
            else
            {
                advanceTime = _cv.TicRate;
                if (_cv.MaxPhysicsFramesPerServerFrame > 0) frameLimit = _cv.MaxPhysicsFramesPerServerFrame;
            }
            advanceTime = _cv.TimeScale > 0 && _cv.TimeScale < 1 ? Math.Min(advanceTime, 0.1 / _cv.TimeScale) : Math.Min(advanceTime, 0.1);
            double frameTime = advanceTime * _cv.TimeScale;
            if (_cv.FrameRate != 0) frameTime = _cv.FrameRate;
            if (Paused) frameTime = 0;
            for (; frameCount < frameLimit && _timer > 0; frameCount++)
            {
                _timer -= advanceTime;
                if (frameTime != 0) RunFrame(frameTime);
            }
            if (Paused && RealTime > PausedStart && PausedStart > 0 && Fn.SvPausedTic != 0)
            {
                Guard("SV_PausedTic", () =>
                {
                    Vm.SetArgFloat(0, (float)(RealTime - PausedStart));
                    SetTime(Time);
                    Vm.Execute(Fn.SvPausedTic, 1);
                });
            }
        }
        // if there is some time remaining from this frame, reset the timer
        if (_timer >= 0) _timer = 0;
        return frameCount;
    }

    /// <summary>
    /// Whether the last <see cref="Advance"/> got as far as SV_Frame's "if (sv.active &amp;&amp; sv_timer > 0)"
    /// block - the one that runs physics and then SV_SendClientMessages. DarkPlaces sends to clients
    /// only there: once per server tick, not once per turn of its main loop.
    /// </summary>
    public bool FrameBlockRan { get; private set; }

    /// <summary>One server frame of <paramref name="frameTime"/> seconds (SV_Physics), then the console's command buffer.</summary>
    public bool RunFrame(double frameTime)
    {
        if (!CanRun || State == SvState.Dead) return false;
        RefreshCvars();
        FrameTime = frameTime;
        bool ok = Guard("SV_Physics", Physics);
        ExecuteCommands();
        return ok;
    }

    // ---- SV_Physics ------------------------------------------------------------------------------------

    private void Physics()
    {
        Frames++;
        RefreshCvars();
        // let the progs know that a new frame has started
        Self = 0;
        Other = 0;
        SetTime(Time);
        Vm.GlobalFloat(G.FrameTime) = (float)FrameTime;
        Exec(Fn.StartFrame, "QC function StartFrame is missing");

        // if force_retouch, relink all the entities
        if (Vm.GlobalFloat(G.ForceRetouch) > 0)
            for (int i = 1; i < Vm.NumEdicts; i++)
                if (!Vm.IsFree(i)) TouchAreaGrid(i);   // force retouch even for stationary

        int maxClients = Clients.Length;
        if (_cv.ConsistentPlayerPreThink)
        {
            // run physics on the client entities in 3 stages
            for (int i = 1; i <= maxClients; i++) if (!Vm.IsFree(i)) ClientEntityPreThink(Clients[i - 1]);
            for (int i = 1; i <= maxClients; i++) if (!Vm.IsFree(i)) ClientEntity(Clients[i - 1]);
            for (int i = 1; i <= maxClients; i++) if (!Vm.IsFree(i)) ClientEntityPostThink(Clients[i - 1]);
        }
        else
        {
            // run physics on the client entities
            for (int i = 1; i <= maxClients; i++)
            {
                if (Vm.IsFree(i)) continue;
                ClientEntityPreThink(Clients[i - 1]);
                ClientEntity(Clients[i - 1]);
                ClientEntityPostThink(Clients[i - 1]);
            }
        }

        // run physics on all the non-client entities
        if (!_cv.FreezeNonClients)
        {
            for (int i = maxClients + 1; i < Vm.NumEdicts; i++)
                if (!Vm.IsFree(i)) PhysicsEntity(i);
            // make a second pass to see if any ents spawned this frame and make sure they run their
            // move/think
            if (_cv.DelayProjectiles < 0)
                for (int i = maxClients + 1; i < Vm.NumEdicts; i++)
                    if (!Vm.IsFree(i) && !Priv(i).Move) PhysicsEntity(i);
        }

        if (Vm.GlobalFloat(G.ForceRetouch) > 0) Vm.GlobalFloat(G.ForceRetouch) = MathF.Max(0, Vm.GlobalFloat(G.ForceRetouch) - 1);

        // LadyHavoc: endframe support
        if (Fn.EndFrame != 0)
        {
            Self = 0;
            Other = 0;
            SetTime(Time);
            Vm.Execute(Fn.EndFrame);
        }

        // decrement prog->num_edicts if the highest number entities died
        _core.TrimEdicts(maxClients + 1);

        if (!_cv.FreezeNonClients) Time += FrameTime;
    }

    // ---- utility functions -----------------------------------------------------------------------------

    // SV_CheckContentsTransition: "returns true if entity had a valid contentstransition function call"
    private bool CheckContentsTransition(int ent, int nativeContents)
    {
        if (Fl(ent, F.WaterType) == nativeContents) return false;
        int function = Int(ent, F.ContentsTransition);
        if (function == 0) return false;
        // Changed Contents; Valid Function; Execute
        Vm.SetArgFloat(0, Fl(ent, F.WaterType));
        Vm.SetArgFloat(1, nativeContents);
        Self = ent;
        SetTime(Time);
        Vm.Execute(function, 2);
        return true;
    }

    /// <summary>SV_CheckVelocity: NaNs become zero, and speed is capped at sv_maxvelocity.</summary>
    internal void CheckVelocity(int ent)
    {
        ref QcVector velocity = ref Vec(ent, F.Velocity);
        ref QcVector origin = ref Vec(ent, F.Origin);
        // bound velocity
        if (float.IsNaN(velocity.X)) velocity.X = 0;
        if (float.IsNaN(velocity.Y)) velocity.Y = 0;
        if (float.IsNaN(velocity.Z)) velocity.Z = 0;
        if (float.IsNaN(origin.X)) origin.X = 0;
        if (float.IsNaN(origin.Y)) origin.Y = 0;
        if (float.IsNaN(origin.Z)) origin.Z = 0;

        float speed2 = Dot(velocity, velocity);
        // LadyHavoc: a hack to ensure that the (rather silly) id1 quakec player animation/cyclic
        // frame code does not do anything during the body sink
        if (speed2 < 0.0000001f)
        {
            velocity = default;
            return;
        }
        // LadyHavoc: max velocity fix, inspired by Maddes's source fixes, but this is faster
        float max = _cv.MaxVelocity;
        if (speed2 > max * max)
        {
            float scale = max / MathF.Sqrt(speed2);
            velocity.X *= scale;
            velocity.Y *= scale;
            velocity.Z *= scale;
        }
    }

    // SV_RunThink: "Runs thinking code if time. There is some play in the exact time the think
    // function will be called, because it is called before any movement is done in a frame. Not used
    // for pushmove objects, because they must be exact. Returns false if the entity removed itself."
    private bool RunThink(int ent)
    {
        float nextThink = Fl(ent, F.NextThink);
        // don't let things stay in the past. it is possible to start that way by a trigger with a local time.
        if (nextThink <= 0 || nextThink > Time + FrameTime) return true;
        for (int iterations = 0; iterations < 128 && !Vm.IsFree(ent); iterations++)
        {
            float thinkTime = MathF.Max((float)Time, Fl(ent, F.NextThink));
            Vm.GlobalFloat(G.Time) = thinkTime;
            Fl(ent, F.NextThink) = 0;
            Self = ent;
            Other = 0;
            Exec(Int(ent, F.Think), "QC function self.think is missing");
            if (Vm.IsFree(ent)) break;
            // mods often set nextthink to time to cause a think every frame, we don't want to loop
            // in that case, so exit if the new nextthink is in the past/present
            float next = Fl(ent, F.NextThink);
            if (next <= thinkTime || next > Time + FrameTime || !_cv.MultipleThinksPerFrame) break;
        }
        return !Vm.IsFree(ent);
    }

    // SV_Impact: "Two entities have touched, so run their touch functions. Returns true if the push
    // did not result in the entity being teleported by QC code."
    private bool Impact(int e1, in SvTrace trace)
    {
        int e2 = trace.Ent < 0 ? 0 : trace.Ent;
        Priv(e1).Mark = MarkWaitForSetOrigin;   // -2: setorigin running

        int oldSelf = Self, oldOther = Other;
        SetTraceGlobals(trace);

        if (IsLive(e1) && IsLive(e2) && Int(e1, F.Touch) != 0 && Fl(e1, F.Solid) != SolidNot)
        {
            SetTime(Time);
            Self = e1;
            Other = e2;
            Exec(Int(e1, F.Touch), "QC function self.touch is missing");
        }
        if (IsLive(e1) && IsLive(e2) && Int(e2, F.Touch) != 0 && Fl(e2, F.Solid) != SolidNot)
        {
            SetTime(Time);
            Self = e2;
            Other = e1;
            Vm.GlobalVector(G.TraceEndPos) = Vec(e2, F.Origin);
            Vm.GlobalVector(G.TracePlaneNormal) = new QcVector(-trace.PlaneNormal.X, -trace.PlaneNormal.Y, -trace.PlaneNormal.Z);
            Vm.GlobalFloat(G.TracePlaneDist) = -trace.PlaneDist;
            Vm.GlobalInt(G.TraceEnt) = e1;
            if (G.TraceDpStartContents >= 0) Vm.GlobalFloat(G.TraceDpStartContents) = 0;
            if (G.TraceDpHitContents >= 0) Vm.GlobalFloat(G.TraceDpHitContents) = 0;
            if (G.TraceDpHitQ3SurfaceFlags >= 0) Vm.GlobalFloat(G.TraceDpHitQ3SurfaceFlags) = 0;
            if (G.TraceDpHitTextureName >= 0) Vm.GlobalInt(G.TraceDpHitTextureName) = 0;
            Exec(Int(e2, F.Touch), "QC function self.touch is missing");
        }

        Self = oldSelf;
        Other = oldOther;
        ref EdictPrivate priv = ref Priv(e1);
        bool caught = priv.Mark == MarkSetOriginCaught;
        priv.Mark = 0;
        return !caught;
    }

    // ClipVelocity: "Slide off of the impacting object"
    private static QcVector ClipVelocity(QcVector velocity, QcVector normal, float overbounce)
    {
        float backoff = -Dot(velocity, normal) * overbounce;
        QcVector result = MA(velocity, backoff, normal);
        if (result.X > -StopEpsilon && result.X < StopEpsilon) result.X = 0;
        if (result.Y > -StopEpsilon && result.Y < StopEpsilon) result.Y = 0;
        if (result.Z > -StopEpsilon && result.Z < StopEpsilon) result.Z = 0;
        return result;
    }

    private float EntGravity(int ent)
    {
        float gravity = Fl(ent, F.Gravity);
        if (gravity == 0) gravity = 1.0f;
        return gravity * _cv.Gravity * (float)FrameTime;
    }

    private void SetOnGround(int ent, int ground)
    {
        Fl(ent, F.Flags) = IntFlags(ent) | FlOnGround;
        Int(ent, F.GroundEntity) = ground < 0 ? 0 : ground;
    }

    // SV_FlyMove: "The basic solid body movement clip that slides along multiple planes. Returns
    // the clipflags if the velocity was modified (hit something solid): 1 = floor, 2 = wall / step,
    // 4 = dead stop, 8 = teleported by touch method. If stepnormal is not NULL, the plane normal of
    // any vertical wall hit will be stored."
    private int FlyMove(int ent, float time, bool applyGravity, ref QcVector stepNormal, bool wantStepNormal, float stepHeight)
    {
        if (time <= 0) return 0;
        float gravity = 0;
        QcVector restoreVelocity = Vec(ent, F.Velocity);
        if (applyGravity)
        {
            gravity = EntGravity(ent);
            if (!_cv.NoGravityOnGround || (IntFlags(ent) & FlOnGround) == 0)
                Vec(ent, F.Velocity).Z -= _cv.GravityUnaffectedByTicRate ? gravity * 0.5f : gravity;
        }

        int blocked = 0;
        QcVector originalVelocity = Vec(ent, F.Velocity), primalVelocity = originalVelocity;
        Span<QcVector> planes = stackalloc QcVector[MaxClipPlanes];
        int numPlanes = 0;
        float timeLeft = time;
        for (int bumpCount = 0; bumpCount < MaxClipPlanes; bumpCount++)
        {
            QcVector velocity = Vec(ent, F.Velocity);
            if (velocity.X == 0 && velocity.Y == 0 && velocity.Z == 0) break;

            QcVector push = Scale(velocity, timeLeft);
            if (!PushEntity(out SvTrace trace, ent, push, _cv.ImpactBeforeOnGround, true))
            {
                // we got teleported by a touch function, let's abort the move
                blocked |= 8;
                break;
            }
            if (Vm.IsFree(ent)) return blocked;

            // this code is used by MOVETYPE_WALK and MOVETYPE_STEP and SV_UnstickEntity
            if (trace.WorldStartSolid && trace.AllSolid)
            {
                Vec(ent, F.Velocity) = restoreVelocity;
                return 3;
            }
            if (trace.Fraction == 1) break;
            timeLeft *= 1 - trace.Fraction;

            if (trace.PlaneNormal.Z != 0)
            {
                if (trace.PlaneNormal.Z > 0.7f)
                {
                    // floor
                    blocked |= 1;
                    SetOnGround(ent, trace.Ent);
                }
            }
            else if (stepHeight != 0)
            {
                // step - handle it immediately
                QcVector org = Vec(ent, F.Origin);
                push = Scale(Vec(ent, F.Velocity), timeLeft);
                if (!PushEntity(out _, ent, new QcVector(0, 0, stepHeight), false, true)
                    || !PushEntity(out SvTrace stepTrace2, ent, push, false, true)
                    || !PushEntity(out _, ent, new QcVector(0, 0, org.Z - Vec(ent, F.Origin).Z), false, true))
                {
                    blocked |= 8;
                    break;
                }
                QcVector now = Vec(ent, F.Origin);
                if (now.X - org.X != 0 || now.Y - org.Y != 0)
                {
                    // we did make some progress: accept the new position if it made some progress...
                    trace = stepTrace2;
                    trace.EndPos = now;
                    timeLeft *= 1 - trace.Fraction;
                    numPlanes = 0;
                    continue;
                }
                Vec(ent, F.Origin) = org;
            }
            else
            {
                // step - return it to caller
                blocked |= 2;
                // save the trace for player extrafriction
                if (wantStepNormal) stepNormal = trace.PlaneNormal;
            }

            // moved some portion of the total distance
            if (!_cv.ImpactBeforeOnGround)
            {
                if (Fl(ent, F.Solid) >= SolidTrigger && trace.Ent >= 0 && !Impact(ent, trace))
                {
                    blocked |= 8;
                    break;
                }
                if (Vm.IsFree(ent)) return blocked;   // removed by the impact function
            }

            if (trace.Fraction >= 0.001f)
            {
                // actually covered some distance
                originalVelocity = Vec(ent, F.Velocity);
                numPlanes = 0;
            }

            // clipped to another plane
            if (numPlanes >= MaxClipPlanes)
            {
                // this shouldn't really happen
                Vec(ent, F.Velocity) = default;
                blocked = 3;
                break;
            }
            planes[numPlanes++] = trace.PlaneNormal;

            // modify original_velocity so it parallels all of the clip planes
            QcVector newVelocity = default;
            int i;
            for (i = 0; i < numPlanes; i++)
            {
                newVelocity = ClipVelocity(originalVelocity, planes[i], 1);
                int j;
                for (j = 0; j < numPlanes; j++)
                    if (j != i && Dot(newVelocity, planes[j]) < 0) break;   // not ok
                if (j == numPlanes) break;
            }

            if (i != numPlanes)
            {
                // go along this plane
                Vec(ent, F.Velocity) = newVelocity;
            }
            else
            {
                // go along the crease
                if (numPlanes != 2)
                {
                    Vec(ent, F.Velocity) = default;
                    blocked = 7;
                    break;
                }
                QcVector dir = Cross(planes[0], planes[1]);
                // LadyHavoc: thanks to taniwha of QuakeForge for pointing out this fix for slowed
                // falling in corners
                float length = MathF.Sqrt(Dot(dir, dir));
                if (length != 0) dir = Scale(dir, 1 / length);
                Vec(ent, F.Velocity) = Scale(dir, Dot(dir, Vec(ent, F.Velocity)));
            }

            // if current velocity is against the original velocity, stop dead to avoid tiny
            // occilations in sloping corners
            if (Dot(Vec(ent, F.Velocity), primalVelocity) <= 0)
            {
                Vec(ent, F.Velocity) = default;
                break;
            }
        }

        // LadyHavoc: this came from QW and allows you to get out of water more easily
        if (_cv.EasierWaterJump && (IntFlags(ent) & FlWaterJump) != 0 && (blocked & 8) == 0)
            Vec(ent, F.Velocity) = primalVelocity;

        if (applyGravity && _cv.GravityUnaffectedByTicRate && (!_cv.NoGravityOnGround || (IntFlags(ent) & FlOnGround) == 0))
            Vec(ent, F.Velocity).Z -= gravity * 0.5f;

        return blocked;
    }

    private static QcVector Cross(QcVector a, QcVector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    // ---- stuck entities --------------------------------------------------------------------------------

    // PHYS_TestEntityPosition: "returns true if the entity is in solid currently". A position that
    // is clear but not quite where the entity is (the move from the offset stopped short) is taken.
    private bool TestEntityPosition(int ent, QcVector offset)
    {
        QcVector entOrigin = Vec(ent, F.Origin), mins = Vec(ent, F.Mins), maxs = Vec(ent, F.Maxs);
        QcVector org = Add(entOrigin, offset);
        int type = Fl(ent, F.MoveType) == MoveTypeFlyWorldOnly ? SvWorld.MoveWorldOnly : SvWorld.MoveNoMonsters;
        SvTrace trace = Trace(org, mins, maxs, entOrigin, type, ent);
        // "trace.startsupercontents & hitsupercontentsmask": the sweep flags a solid start only for
        // contents the mask names, so this is the same test.
        if (trace.StartSolid) return true;
        QcVector d = Sub(trace.EndPos, entOrigin);
        if (Dot(d, d) >= 0.0001f)
        {
            org = trace.EndPos;
            trace = Trace(org, mins, maxs, org, SvWorld.MoveNoMonsters, ent);
            if (!trace.StartSolid) Vec(ent, F.Origin) = org;
        }
        return false;
    }

    private static readonly QcVector[] UnstickOffsets =
    {
        // 1 quake unit in each direction
        new(0, 0, -1), new(0, 0, 1), new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0),
        new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0), new(1, 1, 0),
    };

    // PHYS_UnstickEntityReturnOffset: 0 = was never stuck (UNSTICK_GOOD), 1 = moved out
    // (UNSTICK_UNSTUCK), 2 = still stuck (UNSTICK_STUCK).
    private int UnstickEntityReturnOffset(int ent)
    {
        if (!TestEntityPosition(ent, default)) return 0;
        foreach (QcVector offset in UnstickOffsets)
            if (!TestEntityPosition(ent, offset)) return 1;
        int maxUnstick = (int)((Vec(ent, F.Maxs).Z - Vec(ent, F.Mins).Z) * 0.36f);
        for (int i = 2; i <= maxUnstick; i++)
        {
            if (!TestEntityPosition(ent, new QcVector(0, 0, -i))) return 1;
            if (!TestEntityPosition(ent, new QcVector(0, 0, i))) return 1;
        }
        return 2;
    }

    /// <summary>
    /// PHYS_NudgeOutOfSolid: move a stuck entity out of whatever brush it is in, along the direction
    /// that brush lets go of it soonest, keeping sv_gameplayfix_nudgeoutofsolid_separation clear.
    /// Returns unstickresult_t: -1 was not stuck (UNSTICK_GOOD), 1 moved out (UNSTICK_UNSTUCK),
    /// 0 still stuck (UNSTICK_STUCK). The first pass looks at the world and brush-model entities,
    /// the second at the world alone.
    /// </summary>
    internal int NudgeOutOfSolid(int ent)
    {
        float separation = _cv.NudgeOutOfSolidSeparation;
        QcVector mins = Vec(ent, F.Mins), maxs = Vec(ent, F.Maxs);
        QcVector stuckMins = new(mins.X - separation, mins.Y - separation, mins.Z - separation);
        QcVector stuckMaxs = new(maxs.X + separation, maxs.Y + separation, maxs.Z + separation);
        int mask = GenericHitSuperContentsMask(ent);
        for (int pass = 0; pass < 2; pass++)
        {
            QcVector testOrigin = Vec(ent, F.Origin);
            for (int bump = 0; bump < 10; bump++)
            {
                World.StartDepth(testOrigin, stuckMins, stuckMaxs, pass != 0, ent, mask, out float depth, out QcVector normal, out bool worldSolid, out bool bmodelSolid);
                // Separation compared here to ensure a good location will be recognised reliably.
                if (-depth <= separation || (!bmodelSolid && !worldSolid) || (pass != 0 && !worldSolid))
                {
                    Vec(ent, F.Origin) = testOrigin;
                    return bump != 0 || pass != 0 ? 1 : -1;
                }
                QcVector target = MA(testOrigin, -depth, normal);
                // Trace to targetorigin so we don't set it out of the world in complex cases.
                SvTrace trace = World.Trace(testOrigin, stuckMins, stuckMaxs, target, pass != 0 ? SvWorld.MoveWorldOnly : SvWorld.MoveNoMonsters, ent, mask, _cv.ExtendMoveLength);
                if (trace.Fraction != 0) testOrigin = trace.EndPos;
                else break;   // Can't move it so no point doing more iterations on this pass.
            }
        }
        return 0;
    }

    // #567 float(entity ent) nudgeoutofsolid (DP_QC_NUDGEOUTOFSOLID)
    private void NudgeOutOfSolidBuiltin(QcVm vm)
    {
        Parms(1, 1, "VM_nudgeoutofsolid");
        int ent = vm.ArgEdict(0);
        vm.ReturnFloat(0);
        if (!Modifiable(ent, "nudgeoutofsolid")) return;
        int result = NudgeOutOfSolid(ent);
        vm.ReturnFloat(result);
        if (result > 0) LinkEdict(ent);
    }

    // SV_UnstickEntity: "This is a big hack to try and fix the rare case of getting stuck in the
    // world clipping hull."
    private bool UnstickEntity(int ent)
    {
        if (_cv.NudgeOutOfSolid && _cv.NudgeOutOfSolidSeparation >= 0) return NudgeOutOfSolid(ent) != 0;
        if (!(ent <= Clients.Length ? _cv.UnstickPlayers : _cv.UnstickEntities)) return false;
        switch (UnstickEntityReturnOffset(ent))
        {
            case 0:
            case 1:
                return true;
            default:
                // try restoring the last position the entity was pushed to
                return !TestEntityPosition(ent, Sub(Vec(ent, F.OldOrigin), Vec(ent, F.Origin)));
        }
    }

    // SV_NudgeOutOfSolid_PivotIsKnownGood: grow a box from a point known to be clear (the pivot) to
    // the entity's own box, one face at a time, shoving the entity away from whatever stops it.
    private bool NudgeOutOfSolidPivotIsKnownGood(int ent, QcVector pivot)
    {
        QcVector stuckOrigin = Vec(ent, F.Origin), stuckMins = Vec(ent, F.Mins), stuckMaxs = Vec(ent, F.Maxs);
        QcVector goodMins = pivot, goodMaxs = pivot;
        for (int bump = 0; bump < 6; bump++)
        {
            int coord = 2 - (bump >> 1);
            bool positive = (bump & 1) != 0;
            for (int subBump = 0; ; subBump++)
            {
                QcVector testOrigin = stuckOrigin;
                if (positive) Axis(ref testOrigin, coord) += Axis(ref stuckMaxs, coord) - Axis(ref goodMaxs, coord);
                else Axis(ref testOrigin, coord) += Axis(ref stuckMins, coord) - Axis(ref goodMins, coord);

                SvTrace stuckTrace = Trace(stuckOrigin, goodMins, goodMaxs, testOrigin, SvWorld.MoveNoMonsters, ent);
                // separation compared here would be too much: the goal position is in solid
                if (stuckTrace.BModelStartSolid) return false;
                if (stuckTrace.Fraction >= 1) break;   // it WORKS!
                if (subBump >= 10) return false;        // too many tries

                // we hit something... let's move out of it
                QcVector move = Sub(stuckTrace.EndPos, testOrigin);
                float nudge = Dot(stuckTrace.PlaneNormal, move) + 0.03125f;   // FIXME cvar this constant
                stuckOrigin = MA(stuckOrigin, nudge, stuckTrace.PlaneNormal);
            }
            if (positive) Axis(ref goodMaxs, coord) = Axis(ref stuckMaxs, coord);
            else Axis(ref goodMins, coord) = Axis(ref stuckMins, coord);
        }
        // WE WIN
        Vec(ent, F.Origin) = stuckOrigin;
        return true;
    }

    private static ref float Axis(ref QcVector v, int axis)
    {
        if (axis == 0) return ref v.X;
        if (axis == 1) return ref v.Y;
        return ref v.Z;
    }

    // ---- pushes ----------------------------------------------------------------------------------------

    private int MoveTypeForPush(int ent)
    {
        int moveType = (int)Fl(ent, F.MoveType), solid = (int)Fl(ent, F.Solid);
        if (moveType == MoveTypeFlyMissile) return SvWorld.MoveMissile;
        if (moveType == MoveTypeFlyWorldOnly) return SvWorld.MoveWorldOnly;
        if (solid == SolidTrigger || solid == SolidNot) return SvWorld.MoveNoMonsters;   // only clip against bmodels
        return SvWorld.MoveNormal;
    }

    // SV_PushEntity: "Does not change the entities velocity at all. The trace struct is filled with
    // the trace that has been done. Returns true if the push did not result in the entity being
    // teleported by QC code."
    private bool PushEntity(out SvTrace trace, int ent, QcVector push, bool doTouch, bool checkStuck)
    {
        QcVector mins = Vec(ent, F.Mins), maxs = Vec(ent, F.Maxs), start = Vec(ent, F.Origin), end = Add(start, push);
        int type = MoveTypeForPush(ent);
        trace = Trace(start, mins, maxs, end, type, ent);
        // abort move if we're stuck in the world (and didn't make it out)
        if (trace.AllSolid && checkStuck)
        {
            if (UnstickEntity(ent))
            {
                // we got out: retry the move from the new position
                start = Vec(ent, F.Origin);
                end = Add(start, push);
                trace = Trace(start, mins, maxs, end, type, ent);
            }
            else if (trace.WorldStartSolid) return true;
        }

        Vec(ent, F.Origin) = trace.EndPos;
        Vec(ent, F.OldOrigin) = trace.EndPos;   // for SV_UnstickEntity()
        LinkEdict(ent);

        if (doTouch)
        {
            TouchAreaGrid(ent);
            if (Vm.IsFree(ent)) return true;
            if (Fl(ent, F.Solid) >= SolidTrigger && trace.Ent >= 0
                && ((IntFlags(ent) & FlOnGround) == 0 || Int(ent, F.GroundEntity) != trace.Ent))
                return Impact(ent, trace);
        }
        return true;
    }

    private static float AngleMod360(float a) => a - 360.0f * MathF.Floor(a * (1.0f / 360.0f));

    // SV_PushMove: move a pusher (door, platform, train) for movetime seconds, carrying or shoving
    // whatever is on or in its way, and undo the whole move if something cannot be pushed aside.
    private void PushMove(int pusher, float moveTime)
    {
        QcVector velocity = Vec(pusher, F.Velocity), avelocity = Vec(pusher, F.AVelocity);
        if (velocity.X == 0 && velocity.Y == 0 && velocity.Z == 0 && avelocity.X == 0 && avelocity.Y == 0 && avelocity.Z == 0)
        {
            Fl(pusher, F.LTime) += moveTime;
            return;
        }

        switch ((int)Fl(pusher, F.Solid))
        {
            // LadyHavoc: valid pusher types
            case SolidBsp:
            case SolidBBox:
            case SolidSlideBox:
            case SolidCorpse:   // LadyHavoc: this would be weird...
                break;
            // LadyHavoc: no collisions
            case SolidNot:
            case SolidTrigger:
            {
                Vec(pusher, F.Origin) = MA(Vec(pusher, F.Origin), moveTime, velocity);
                QcVector a = MA(Vec(pusher, F.Angles), moveTime, avelocity);
                Vec(pusher, F.Angles) = new QcVector(AngleMod360(a.X), AngleMod360(a.Y), AngleMod360(a.Z));
                Fl(pusher, F.LTime) += moveTime;
                LinkEdict(pusher);
                return;
            }
            default:
                Print($"SV_PushMove: entity #{pusher}, unrecognized solid type {Fl(pusher, F.Solid)}\n");
                return;
        }
        int index = (int)Fl(pusher, F.ModelIndex);
        if (index < 1 || index >= Protocol.DpProtocol.MaxModels)
        {
            Print($"SV_PushMove: entity #{pusher} has an invalid modelindex {Fl(pusher, F.ModelIndex)}\n");
            return;
        }
        SvModelBounds model = ModelBounds(index) ?? default;
        int pusherOwner = Int(pusher, F.Owner);
        QcVector angles = Vec(pusher, F.Angles);
        bool rotated = Dot(angles, angles) + Dot(avelocity, avelocity) > 0;

        QcVector move1 = Scale(velocity, moveTime), moveAngle = Scale(avelocity, moveTime), origin = Vec(pusher, F.Origin);
        QcVector boxMins, boxMaxs;
        if (moveAngle.X != 0 || moveAngle.Z != 0) (boxMins, boxMaxs) = (model.RotatedMins, model.RotatedMaxs);
        else if (moveAngle.Y != 0) (boxMins, boxMaxs) = (model.YawMins, model.YawMaxs);
        else (boxMins, boxMaxs) = (model.NormalMins, model.NormalMaxs);
        // find the bounding box
        QcVector mins = new(
            boxMins.X + MathF.Min(move1.X, 0) + origin.X - 1, boxMins.Y + MathF.Min(move1.Y, 0) + origin.Y - 1, boxMins.Z + MathF.Min(move1.Z, 0) + origin.Z - 1);
        QcVector maxs = new(
            boxMaxs.X + MathF.Max(move1.X, 0) + origin.X + 1, boxMaxs.Y + MathF.Max(move1.Y, 0) + origin.Y + 1, boxMaxs.Z + MathF.Max(move1.Z, 0) + origin.Z + 1);

        AngleVectorsFlu(new QcVector(-moveAngle.X, -moveAngle.Y, -moveAngle.Z), out QcVector forward, out QcVector left, out QcVector up);

        QcVector pushOrig = origin, pushAng = angles;
        float pushLTime = Fl(pusher, F.LTime);

        // move the pusher to its final position
        Vec(pusher, F.Origin) = MA(origin, moveTime, velocity);
        Vec(pusher, F.Angles) = MA(angles, moveTime, avelocity);
        Fl(pusher, F.LTime) += moveTime;
        LinkEdict(pusher);

        float saveSolid = Fl(pusher, F.Solid);

        // see if any solid entities are inside the final position
        int numMoved = 0;
        int[] check = ArrayPool<int>.Shared.Rent(SvWorld.MaxTouchedEdicts);
        int[] moved = ArrayPool<int>.Shared.Rent(SvWorld.MaxTouchedEdicts);
        try
        {
            int numCheck = Fl(pusher, F.MoveType) == MoveTypeFakePush   // Tenebrae's MOVETYPE_PUSH variant that doesn't push...
                ? 0
                : World.EntitiesInBox(mins, maxs, check.AsSpan(0, SvWorld.MaxTouchedEdicts));
            for (int e = 0; e < numCheck; e++)
            {
                int ent = check[e];
                if (!IsLive(ent) || ent == pusher) continue;
                int moveType = (int)Fl(ent, F.MoveType);
                if (moveType is MoveTypeNone or MoveTypePush or MoveTypeFollow or MoveTypeNoClip or MoveTypeFlyWorldOnly) continue;
                if (Int(ent, F.Owner) == pusher) continue;
                if (pusherOwner == ent) continue;

                // tell any MOVETYPE_STEP entity that it may need to check for water transitions
                Priv(ent).WaterPositionForceUpdate = true;
                int checkContents = GenericHitSuperContentsMask(ent);

                // if the entity is standing on the pusher, it will definitely be moved; if the entity
                // is not standing on the pusher, but is in the pusher's final position, move it
                if ((IntFlags(ent) & FlOnGround) == 0 || Int(ent, F.GroundEntity) != pusher)
                    if (!World.BoxInsideEntity(pusher, Vec(ent, F.Origin), Vec(ent, F.Mins), Vec(ent, F.Maxs), checkContents))
                        continue;

                QcVector entMins = Vec(ent, F.Mins), entMaxs = Vec(ent, F.Maxs);
                QcVector pivot = new((entMins.X + entMaxs.X) * 0.5f, (entMins.Y + entMaxs.Y) * 0.5f, (entMins.Z + entMaxs.Z) * 0.5f);
                QcVector move;
                if (rotated)
                {
                    QcVector org = Add(Sub(Vec(ent, F.Origin), Vec(pusher, F.Origin)), pivot);
                    QcVector org2 = new(Dot(org, forward), Dot(org, left), Dot(org, up));
                    move = Add(Sub(org2, org), move1);
                }
                else move = move1;

                ref EdictPrivate priv = ref Priv(ent);
                priv.MovedFrom = Vec(ent, F.Origin);
                priv.MovedFromAngles = Vec(ent, F.Angles);
                if (numMoved < moved.Length) moved[numMoved++] = ent;

                // try moving the contacted entity
                Fl(pusher, F.Solid) = SolidNot;
                bool notTeleported = PushEntity(out SvTrace trace, ent, move, true, true);
                if (IsLive(ent)) Vec(ent, F.Angles).Y += trace.Fraction * moveAngle.Y;
                if (IsLive(pusher)) Fl(pusher, F.Solid) = saveSolid;   // was SOLID_BSP
                // entity "check" got teleported: pushed enough
                if (!notTeleported || !IsLive(ent) || !IsLive(pusher)) continue;

                // this trigger_hurt check is for elevators that crush players
                if (Fl(ent, F.MoveType) != MoveTypeWalk && (trace.Fraction < 1 || Int(ent, F.GroundEntity) != pusher))
                    Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;

                // if it is still inside the pusher, block
                if (!World.BoxInsideEntity(pusher, Vec(ent, F.Origin), Vec(ent, F.Mins), Vec(ent, F.Maxs), checkContents)) continue;

                if (NudgeOutOfSolidPivotIsKnownGood(ent, pivot))
                {
                    // hack to invoke all necessary movement triggers
                    PushEntity(out _, ent, default, true, true);
                    // we could fix it
                    continue;
                }

                // still inside pusher, so it's really blocked

                // fail the move
                if (Vec(ent, F.Mins).X == Vec(ent, F.Maxs).X) continue;
                if (Fl(ent, F.Solid) == SolidNot || Fl(ent, F.Solid) == SolidTrigger)
                {
                    // corpse
                    if (!_cv.NoSquashEntities)
                    {
                        ref QcVector squashed = ref Vec(ent, F.Mins);
                        squashed.X = squashed.Y = 0;
                        Vec(ent, F.Maxs) = squashed;
                    }
                    continue;
                }

                Vec(pusher, F.Origin) = pushOrig;
                Vec(pusher, F.Angles) = pushAng;
                Fl(pusher, F.LTime) = pushLTime;
                LinkEdict(pusher);

                // move back any entities we already moved
                for (int i = 0; i < numMoved; i++)
                {
                    int back = moved[i];
                    if (!IsLive(back)) continue;
                    Vec(back, F.Origin) = Priv(back).MovedFrom;
                    Vec(back, F.Angles) = Priv(back).MovedFromAngles;
                    LinkEdict(back);
                }

                // if the pusher has a "blocked" function, call it, otherwise just stay in place until
                // the obstacle is gone
                if (Int(pusher, F.Blocked) != 0)
                {
                    SetTime(Time);
                    Self = pusher;
                    Other = ent;
                    Exec(Int(pusher, F.Blocked), "QC function self.blocked is missing");
                }
                break;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(check);
            ArrayPool<int>.Shared.Return(moved);
        }
        if (!IsLive(pusher)) return;
        QcVector final = Vec(pusher, F.Angles);
        Vec(pusher, F.Angles) = new QcVector(AngleMod360(final.X), AngleMod360(final.Y), AngleMod360(final.Z));
    }

    // mathlib.c AngleVectorsFLU: forward, left, up of Quake angles (pitch, yaw, roll in degrees).
    private static void AngleVectorsFlu(QcVector angles, out QcVector forward, out QcVector left, out QcVector up)
    {
        double yaw = angles.Y * (Math.PI * 2 / 360), pitch = angles.X * (Math.PI * 2 / 360), roll = angles.Z * (Math.PI * 2 / 360);
        double sy = Math.Sin(yaw), cy = Math.Cos(yaw), sp = Math.Sin(pitch), cp = Math.Cos(pitch), sr = Math.Sin(roll), cr = Math.Cos(roll);
        forward = new QcVector((float)(cp * cy), (float)(cp * sy), (float)-sp);
        left = new QcVector((float)(sr * sp * cy + cr * -sy), (float)(sr * sp * sy + cr * cy), (float)(sr * cp));
        up = new QcVector((float)(cr * sp * cy + -sr * -sy), (float)(cr * sp * sy + -sr * cy), (float)(cr * cp));
    }

    // mathlib.c AngleVectors: forward, right, up.
    internal static void AngleVectors(QcVector angles, out QcVector forward, out QcVector right, out QcVector up)
    {
        AngleVectorsFlu(angles, out forward, out QcVector left, out up);
        right = new QcVector(-left.X, -left.Y, -left.Z);
    }

    // SV_Physics_Pusher
    private void PhysicsPusher(int ent)
    {
        float oldLTime = Fl(ent, F.LTime), thinkTime = Fl(ent, F.NextThink), moveTime;
        if (thinkTime < oldLTime + FrameTime)
        {
            moveTime = thinkTime - oldLTime;
            if (moveTime < 0) moveTime = 0;
        }
        else moveTime = (float)FrameTime;

        if (moveTime != 0)
            // advances PRVM_serveredictfloat(ent, ltime) if not blocked
            PushMove(ent, moveTime);
        if (Vm.IsFree(ent)) return;

        if (thinkTime > oldLTime && thinkTime <= Fl(ent, F.LTime))
        {
            Fl(ent, F.NextThink) = 0;
            SetTime(Time);
            Self = ent;
            Other = 0;
            Exec(Int(ent, F.Think), "QC function self.think is missing");
        }
    }

    // ---- water -----------------------------------------------------------------------------------------

    // SV_CheckWater
    private bool CheckWater(int ent)
    {
        QcVector origin = Vec(ent, F.Origin), mins = Vec(ent, F.Mins), maxs = Vec(ent, F.Maxs);
        QcVector point = new(origin.X, origin.Y, origin.Z + mins.Z + 1);
        // DRESK - Support for Entity Contents Transition Event: this will always succeed on the first
        // frame and only if the entity has a valid contentstransition function
        int cont = World.PointSuperContents(point);
        int nativeContents = NativeContents(cont);
        if (Fl(ent, F.WaterType) != 0) CheckContentsTransition(ent, nativeContents);
        if (Vm.IsFree(ent)) return false;

        Fl(ent, F.WaterLevel) = 0;
        Fl(ent, F.WaterType) = ContentsEmpty;
        if ((cont & SvWorld.ContentsLiquidsMask) != 0)
        {
            Fl(ent, F.WaterType) = nativeContents;
            Fl(ent, F.WaterLevel) = 1;
            point.Z = origin.Z + (mins.Z + maxs.Z) * 0.5f;
            if ((World.PointSuperContents(point) & SvWorld.ContentsLiquidsMask) != 0)
            {
                Fl(ent, F.WaterLevel) = 2;
                point.Z = origin.Z + Vec(ent, F.ViewOfs).Z;
                if ((World.PointSuperContents(point) & SvWorld.ContentsLiquidsMask) != 0) Fl(ent, F.WaterLevel) = 3;
            }
        }
        return Fl(ent, F.WaterLevel) > 1;
    }

    // SV_CheckWaterTransition
    private void CheckWaterTransition(int ent)
    {
        // LadyHavoc: bugfixes in this function are keyed to the sv_gameplayfix_fixedcheckwatertransition cvar
        int cont = NativeContents(World.PointSuperContents(Vec(ent, F.Origin)));
        if (Fl(ent, F.WaterType) == 0)
        {
            // just spawned here
            if (!_cv.FixedCheckWaterTransition)
            {
                Fl(ent, F.WaterType) = cont;
                Fl(ent, F.WaterLevel) = 1;
                return;
            }
        }
        // DRESK - Support for Entity Contents Transition Event: check contents transition and play
        // the water sound if the program has no say
        else if (!CheckContentsTransition(ent, cont))
        {
            float waterType = Fl(ent, F.WaterType);
            if (_cv.SoundWaterSplash.Length > 0
                && (waterType == ContentsWater || waterType == ContentsSlime) != (cont == ContentsWater || cont == ContentsSlime))
                StartSound(ent, 0, _cv.SoundWaterSplash, 255, 1, false, 1.0f);
        }
        if (Vm.IsFree(ent)) return;

        if (cont <= ContentsWater)
        {
            Fl(ent, F.WaterType) = cont;
            Fl(ent, F.WaterLevel) = 1;
        }
        else
        {
            Fl(ent, F.WaterType) = ContentsEmpty;
            Fl(ent, F.WaterLevel) = _cv.FixedCheckWaterTransition ? 0 : cont;
        }
    }

    // ---- client movement -------------------------------------------------------------------------------

    // SV_WallFriction
    private void WallFriction(int ent, QcVector stepNormal)
    {
        AngleVectors(Vec(ent, F.VAngle), out QcVector forward, out _, out _);
        float d = Dot(stepNormal, forward) + 0.5f;
        if (d >= 0) return;
        // cut the tangential velocity
        ref QcVector velocity = ref Vec(ent, F.Velocity);
        float i = Dot(stepNormal, velocity);
        QcVector into = Scale(stepNormal, i);
        velocity.X = (velocity.X - into.X) * (1 + d);
        velocity.Y = (velocity.Y - into.Y) * (1 + d);
    }

    // SV_WalkMove: "Only used by players"
    private void WalkMove(int ent)
    {
        if (FrameTime <= 0) return;
        QcVector none = default, stepNormal = default;
        bool applyGravity = !CheckWater(ent) && Fl(ent, F.MoveType) == MoveTypeWalk && (IntFlags(ent) & FlWaterJump) == 0;
        if (Vm.IsFree(ent)) return;
        CheckVelocity(ent);

        // do a regular slide move unless it looks like you ran into a step
        bool oldOnGround = (IntFlags(ent) & FlOnGround) != 0;
        QcVector startOrigin = Vec(ent, F.Origin), startVelocity = Vec(ent, F.Velocity);
        int clip = FlyMove(ent, (float)FrameTime, applyGravity, ref none, false, _cv.StepMultipleTimes ? _cv.StepHeight : 0);
        if (Vm.IsFree(ent)) return;

        if (_cv.DownTraceSupportsOnGroundFlag && (clip & 1) == 0)
        {
            // only try this if there was no floor in the way in the trace (no, this check seems to
            // be not REALLY necessary, because if clip & 1, our trace will hit that thing too)
            QcVector o = Vec(ent, F.Origin);
            SvTrace trace = Trace(new QcVector(o.X, o.Y, o.Z + 1), Vec(ent, F.Mins), Vec(ent, F.Maxs), new QcVector(o.X, o.Y, o.Z - 1), MoveTypeForPush(ent), ent);
            if (trace.Fraction < 1 && trace.PlaneNormal.Z > 0.7f)
            {
                clip |= 1;   // but we HAVE found a floor
                // set groundentity so we get carried when walking onto a mover with sv_gameplayfix_nogravityonground
                Int(ent, F.GroundEntity) = trace.Ent < 0 ? 0 : trace.Ent;
            }
        }

        // if the move did not hit the ground at any point, we're not on ground
        if ((clip & 1) == 0) Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;

        CheckVelocity(ent);
        LinkEdict(ent);
        TouchAreaGrid(ent);
        if (Vm.IsFree(ent)) return;

        if ((clip & 8) != 0) return;   // teleport
        if ((IntFlags(ent) & FlWaterJump) != 0) return;
        if (_cv.NoStep) return;

        QcVector originalMoveOrigin = Vec(ent, F.Origin), originalMoveVelocity = Vec(ent, F.Velocity);
        int originalMoveFlags = IntFlags(ent), originalMoveGroundEntity = Int(ent, F.GroundEntity);

        // if move didn't block on a step, return
        if ((clip & 2) != 0)
        {
            // if move was not trying to move into the step, return
            if (MathF.Abs(startVelocity.X) < 0.03125f && MathF.Abs(startVelocity.Y) < 0.03125f) return;

            if (Fl(ent, F.MoveType) != MoveTypeFly)
            {
                // return if gibbed by a trigger
                if (Fl(ent, F.MoveType) != MoveTypeWalk) return;
                // return if attempting to jump while airborn (unless sv_jumpstep)
                if (!_cv.JumpStep && !oldOnGround && Fl(ent, F.WaterLevel) == 0) return;
            }

            // try moving up and forward to go up a step: back to start pos
            Vec(ent, F.Origin) = startOrigin;
            Vec(ent, F.Velocity) = startVelocity;

            // move up
            // we got teleported when upstepping... must abort the move
            if (!PushEntity(out _, ent, new QcVector(0, 0, _cv.StepHeight), true, true) || Vm.IsFree(ent)) return;

            // move forward
            Vec(ent, F.Velocity).Z = 0;
            clip = FlyMove(ent, (float)FrameTime, applyGravity, ref stepNormal, true, 0);
            if (Vm.IsFree(ent)) return;
            Vec(ent, F.Velocity).Z += startVelocity.Z;
            // we got teleported when upstepping... must abort the move. note that z velocity handling
            // may not be what QC expects here, but we cannot help it
            if ((clip & 8) != 0) return;

            CheckVelocity(ent);
            LinkEdict(ent);
            TouchAreaGrid(ent);
            if (Vm.IsFree(ent)) return;

            // check for stuckness, possibly due to the limited precision of floats in the clipping hulls
            QcVector now = Vec(ent, F.Origin);
            if (clip != 0 && MathF.Abs(originalMoveOrigin.Y - now.Y) < 0.03125f && MathF.Abs(originalMoveOrigin.X - now.X) < 0.03125f)
            {
                // stepping up didn't make any progress, revert to original move
                Vec(ent, F.Origin) = originalMoveOrigin;
                Vec(ent, F.Velocity) = originalMoveVelocity;
                Fl(ent, F.Flags) = originalMoveFlags;
                Int(ent, F.GroundEntity) = originalMoveGroundEntity;
                // now try to unstick if needed
                return;
            }

            // extra friction based on view angle
            if ((clip & 2) != 0 && _cv.WallFriction) WallFriction(ent, stepNormal);
        }
        // don't do the down move if stepdown is disabled, moving upward, not in water, or the move
        // started offground or ended onground
        else if (_cv.StepDown == 0 || Fl(ent, F.WaterLevel) >= 3 || startVelocity.Z >= (1.0f / 32.0f) || !oldOnGround || (IntFlags(ent) & FlOnGround) != 0)
            return;

        // move down
        // we got teleported when downstepping... must abort the move
        if (!PushEntity(out SvTrace downTrace, ent, new QcVector(0, 0, -_cv.StepHeight + startVelocity.Z * (float)FrameTime), true, true) || Vm.IsFree(ent)) return;

        if (!(downTrace.Fraction < 1 && downTrace.PlaneNormal.Z > 0.7f))
        {
            // if the push down didn't end up on good ground, use the move without the step up. This
            // happens near wall / slope combinations, and can cause the player to hop up higher on a
            // slope too steep to climb
            Vec(ent, F.Origin) = originalMoveOrigin;
            Vec(ent, F.Velocity) = originalMoveVelocity;
            Fl(ent, F.Flags) = originalMoveFlags;
            Int(ent, F.GroundEntity) = originalMoveGroundEntity;
        }

        CheckVelocity(ent);
        LinkEdict(ent);
        TouchAreaGrid(ent);
    }

    // SV_Physics_Follow: "Entities that are 'stuck' to another entity"
    private void PhysicsFollow(int ent)
    {
        // LadyHavoc: implemented rotation on MOVETYPE_FOLLOW objects
        int e = EdictField(ent, F.AimEnt);
        QcVector eAngles = Vec(e, F.Angles), punch = Vec(ent, F.PunchAngle), viewOfs = Vec(ent, F.ViewOfs);
        if (Same(eAngles, punch))
        {
            // quick case for no rotation
            Vec(ent, F.Origin) = Add(Vec(e, F.Origin), viewOfs);
        }
        else
        {
            AngleVectors(new QcVector(-punch.X, punch.Y, punch.Z), out QcVector vf, out QcVector vr, out QcVector vu);
            QcVector v = new(
                viewOfs.X * vf.X + viewOfs.Y * vr.X + viewOfs.Z * vu.X,
                viewOfs.X * vf.Y + viewOfs.Y * vr.Y + viewOfs.Z * vu.Y,
                viewOfs.X * vf.Z + viewOfs.Y * vr.Z + viewOfs.Z * vu.Z);
            AngleVectors(new QcVector(-eAngles.X, eAngles.Y, eAngles.Z), out vf, out vr, out vu);
            QcVector eOrigin = Vec(e, F.Origin);
            Vec(ent, F.Origin) = new QcVector(Dot(v, vf) + eOrigin.X, Dot(v, vr) + eOrigin.Y, Dot(v, vu) + eOrigin.Z);
        }
        Vec(ent, F.Angles) = Add(eAngles, Vec(ent, F.VAngle));
        LinkEdict(ent);
    }

    // SV_Physics_Toss: "Toss, bounce, and fly movement. When onground, do nothing."
    private void PhysicsToss(int ent)
    {
        // if onground, return without moving
        if ((IntFlags(ent) & FlOnGround) != 0)
        {
            int ground = EdictField(ent, F.GroundEntity);
            if (Vec(ent, F.Velocity).Z >= (1.0f / 32.0f) && _cv.UpwardVelocityClearsOnGroundFlag)
            {
                // don't stick to ground if onground and moving upward
                Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;
            }
            else if (Int(ent, F.GroundEntity) == 0 || !_cv.NoAirbornCorpse)
            {
                // we can trust FL_ONGROUND if groundentity is world because it never moves
                return;
            }
            else if (Priv(ent).SuspendedInAir && Vm.IsFree(ground))
            {
                // if ent was supported by a brush model on previous frame, and groundentity is now
                // freed, set groundentity to 0 (world) which leaves it suspended in the air
                Int(ent, F.GroundEntity) = 0;
                if (_cv.NoAirbornCorpseAllowSuspendedItems) return;
            }
            else if (!Vm.IsFree(ground) && BoxesOverlap(Vec(ent, F.AbsMin), Vec(ent, F.AbsMax), Vec(ground, F.AbsMin), Vec(ground, F.AbsMax)))
            {
                // don't slide if still touching the groundentity. (The C compares the two entities'
                // cull boxes, which SV_PrepareEntityForSending last computed; the linked boxes are
                // the same test a frame fresher.)
                return;
            }
        }
        Priv(ent).SuspendedInAir = false;

        CheckVelocity(ent);

        // add gravity
        int moveType = (int)Fl(ent, F.MoveType);
        if (moveType == MoveTypeToss || moveType == MoveTypeBounce) Vec(ent, F.Velocity).Z -= EntGravity(ent);

        // move angles
        Vec(ent, F.Angles) = MA(Vec(ent, F.Angles), (float)FrameTime, Vec(ent, F.AVelocity));

        float moveTime = (float)FrameTime;
        for (int bump = 0; bump < MaxClipPlanes && moveTime > 0; bump++)
        {
            // move origin
            QcVector move = Scale(Vec(ent, F.Velocity), moveTime);
            if (!PushEntity(out SvTrace trace, ent, move, true, Fl(ent, F.MoveType) != MoveTypeFly)) return;   // teleported
            if (Vm.IsFree(ent)) return;
            if (trace.Fraction == 1) break;
            moveTime *= 1 - MathF.Min(1, trace.Fraction);
            switch ((int)Fl(ent, F.MoveType))
            {
                case MoveTypeBounceMissile:
                {
                    float bounceFactor = Fl(ent, F.BounceFactor);
                    if (bounceFactor == 0) bounceFactor = 1.0f;
                    Vec(ent, F.Velocity) = ClipVelocity(Vec(ent, F.Velocity), trace.PlaneNormal, 1 + bounceFactor);
                    Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;
                    if (!_cv.SlideMoveProjectiles) moveTime = 0;
                    break;
                }
                case MoveTypeBounce:
                {
                    float bounceFactor = Fl(ent, F.BounceFactor);
                    if (bounceFactor == 0) bounceFactor = 0.5f;
                    float bounceStop = Fl(ent, F.BounceStop);
                    if (bounceStop == 0) bounceStop = 60.0f / 800.0f;
                    Vec(ent, F.Velocity) = ClipVelocity(Vec(ent, F.Velocity), trace.PlaneNormal, 1 + bounceFactor);
                    float entGravity = Fl(ent, F.Gravity);
                    if (entGravity == 0) entGravity = 1.0f;
                    // LadyHavoc: fixed grenades not bouncing when fired down a slope
                    float d = _cv.GrenadeBounceDownSlopes ? MathF.Abs(Dot(trace.PlaneNormal, Vec(ent, F.Velocity))) : Vec(ent, F.Velocity).Z;
                    if (trace.PlaneNormal.Z > 0.7f && d < _cv.Gravity * bounceStop * entGravity)
                    {
                        SetOnGround(ent, trace.Ent);
                        Vec(ent, F.Velocity) = default;
                        Vec(ent, F.AVelocity) = default;
                        moveTime = 0;
                    }
                    else
                    {
                        Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;
                        if (!_cv.SlideMoveProjectiles) moveTime = 0;
                    }
                    break;
                }
                default:
                    Vec(ent, F.Velocity) = ClipVelocity(Vec(ent, F.Velocity), trace.PlaneNormal, 1.0f);
                    if (trace.PlaneNormal.Z > 0.7f)
                    {
                        SetOnGround(ent, trace.Ent);
                        if (trace.Ent >= 0 && IsLive(trace.Ent) && Fl(trace.Ent, F.Solid) == SolidBsp) Priv(ent).SuspendedInAir = true;
                        Vec(ent, F.Velocity) = default;
                        Vec(ent, F.AVelocity) = default;
                        moveTime = 0;
                    }
                    else
                    {
                        Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;
                        if (!_cv.SlideMoveProjectiles) moveTime = 0;
                    }
                    break;
            }
        }

        // check for in water
        CheckWaterTransition(ent);
    }

    private static bool BoxesOverlap(QcVector aMins, QcVector aMaxs, QcVector bMins, QcVector bMaxs) =>
        aMaxs.X >= bMins.X && bMaxs.X >= aMins.X && aMaxs.Y >= bMins.Y && bMaxs.Y >= aMins.Y && aMaxs.Z >= bMins.Z && bMaxs.Z >= aMins.Z;

    // SV_Physics_Step: "Monsters freefall when they don't have a ground entity, otherwise all
    // movement is done with discrete steps. This is also used for objects that have become still on
    // the ground, but will fall if the floor is pulled out from under them."
    private void PhysicsStep(int ent)
    {
        int flags = IntFlags(ent);
        QcVector none = default, backupVelocity = Vec(ent, F.Velocity);
        // DRESK: don't fall at all if fly/swim
        if ((flags & (FlFly | FlSwim)) != 0) return;
        if ((flags & FlOnGround) != 0)
        {
            // freefall if onground and moving upward; freefall if not standing on a world surface
            // (it may be a lift or trap door)
            if (Vec(ent, F.Velocity).Z >= (1.0f / 32.0f) && _cv.UpwardVelocityClearsOnGroundFlag)
            {
                Fl(ent, F.Flags) = flags & ~FlOnGround;
                CheckVelocity(ent);
                FlyMove(ent, (float)FrameTime, true, ref none, false, 0);
                if (Vm.IsFree(ent)) return;
                LinkEdict(ent);
                TouchAreaGrid(ent);
                if (!Vm.IsFree(ent)) Priv(ent).WaterPositionForceUpdate = true;
            }
        }
        else
        {
            // freefall if not onground
            bool hitSound = Vec(ent, F.Velocity).Z < _cv.Gravity * -0.1f;
            CheckVelocity(ent);
            FlyMove(ent, (float)FrameTime, true, ref none, false, 0);
            if (Vm.IsFree(ent)) return;
            LinkEdict(ent);
            TouchAreaGrid(ent);
            if (Vm.IsFree(ent)) return;

            // just hit ground
            if (hitSound && (IntFlags(ent) & FlOnGround) != 0)
            {
                // DRESK - Support for MoveType Step Land Event
                int landEvent = Int(ent, F.MoveTypeStepLandEvent);
                if (landEvent != 0)
                {
                    // Assign Velocity at Impact
                    Vm.SetArgVector(0, backupVelocity);
                    Self = ent;
                    SetTime(Time);
                    Vm.Execute(landEvent, 1);
                }
                else if (_cv.SoundLand.Length > 0) StartSound(ent, 0, _cv.SoundLand, 255, 1, false, 1.0f);   // Standard Quake Land Sound
                if (Vm.IsFree(ent)) return;
            }
            Priv(ent).WaterPositionForceUpdate = true;
        }
    }

    // The water-transition check MOVETYPE_STEP entities make after thinking, when they have moved.
    private void StepWaterCheck(int ent)
    {
        ref EdictPrivate priv = ref Priv(ent);
        if (!priv.WaterPositionForceUpdate && Same(Vec(ent, F.Origin), priv.WaterPositionOrigin)) return;
        priv.WaterPositionForceUpdate = false;
        priv.WaterPositionOrigin = Vec(ent, F.Origin);
        CheckWaterTransition(ent);
    }

    // SV_Physics_Entity
    private void PhysicsEntity(int ent)
    {
        // don't run think/move on newly spawned projectiles as it messes up movement interpolation
        // and rocket trails, and is inconsistent with respect to entities spawned in the same frame
        // (if an ent spawns a higher numbered ent, it moves in the same frame, but if it spawns a
        // lower numbered ent, it doesn't - this never moves ents in the first frame regardless of
        // order). ents in the first frame regardless
        ref EdictPrivate priv = ref Priv(ent);
        bool runMove = priv.Move;
        priv.Move = true;
        if (!runMove && _cv.DelayProjectiles > 0) return;
        int moveType = (int)Fl(ent, F.MoveType);
        switch (moveType)
        {
            case MoveTypePush:
            case MoveTypeFakePush:
                PhysicsPusher(ent);
                break;
            case MoveTypeNone:
                // LadyHavoc: manually inlined the thinktime check here because MOVETYPE_NONE is used often
                if (Fl(ent, F.NextThink) > 0 && Fl(ent, F.NextThink) <= Time + FrameTime) RunThink(ent);
                break;
            case MoveTypeFollow:
                if (RunThink(ent)) PhysicsFollow(ent);
                break;
            case MoveTypeNoClip:
                if (RunThink(ent))
                {
                    CheckWater(ent);
                    if (Vm.IsFree(ent)) break;
                    Vec(ent, F.Origin) = MA(Vec(ent, F.Origin), (float)FrameTime, Vec(ent, F.Velocity));
                    Vec(ent, F.Angles) = MA(Vec(ent, F.Angles), (float)FrameTime, Vec(ent, F.AVelocity));
                }
                if (!Vm.IsFree(ent)) LinkEdict(ent);
                break;
            case MoveTypeStep:
                PhysicsStep(ent);
                // regular thinking
                if (!Vm.IsFree(ent) && RunThink(ent)) StepWaterCheck(ent);
                break;
            case MoveTypeWalk:
                if (RunThink(ent)) WalkMove(ent);
                break;
            case MoveTypeToss:
            case MoveTypeBounce:
            case MoveTypeBounceMissile:
            case MoveTypeFlyMissile:
            case MoveTypeFly:
            case MoveTypeFlyWorldOnly:
                // regular thinking
                if (RunThink(ent)) PhysicsToss(ent);
                break;
            case MoveTypePhysics:
                if (RunThink(ent))
                {
                    LinkEdict(ent);
                    TouchAreaGrid(ent);
                }
                break;
            default:
                // MOVETYPE_USER_FIRST..LAST (DP_USERMOVETYPES): the program moves these itself - they
                // do not even think here, exactly as in the C. Anything else is a bad movetype, which
                // DarkPlaces prints every frame; this counts it instead.
                if (moveType < MoveTypeUserFirst || moveType > MoveTypeUserLast) BadMoveTypes++;
                break;
        }
    }

    /// <summary>Entity-frames whose movetype the engine does not know (SV_Physics: bad movetype).</summary>
    public long BadMoveTypes { get; private set; }

    // SV_PlayerPhysics: apply the client's input to its entity, then let the program move it
    // (SV_PlayerPhysics in QuakeC, when sv_playerphysicsqc is set and the program has one - which is
    // how Xonotic moves players). The engine's own Quake player movement (SV_AirMove, SV_WaterMove,
    // SV_UserFriction ...) is NOT ported: a program without the QuakeC function gets no player movement.
    private void PlayerPhysics(SvClient client)
    {
        int ent = client.Edict;
        ApplyClientMove(client);
        CheckVelocity(ent);
        if (Fn.SvPlayerPhysics != 0 && _cv.PlayerPhysicsQc)
        {
            SetTime(Time);
            Self = ent;
            Vm.Execute(Fn.SvPlayerPhysics);
            CheckVelocity(ent);
            return;
        }
        EnginePlayerPhysicsSkipped++;
    }

    /// <summary>Player frames in which the unported engine player movement would have run.</summary>
    public long EnginePlayerPhysicsSkipped { get; private set; }

    /// <summary>NETGRAPH_PACKETS lost-packet count of a connection, for ping_packetloss; set by the network layer.</summary>
    public Func<SvClient, int>? PacketLossCounter { get; set; }

    // SV_ApplyClientMove: the input command as the program reads it - buttons, view angles, movement.
    internal void ApplyClientMove(SvClient client)
    {
        ref SvUserCmd move = ref client.Cmd;
        if (move.ReceiveTime == 0) return;
        int ent = client.Edict;
        // note: a move can be applied multiple times if the client packets are not coming as often as
        // the physics is executed, and the move must be applied before running qc code
        move.ApplyMove = true;

        // set the edict fields
        int buttons = move.Buttons;
        Fl(ent, F.Button0) = buttons & 1;
        Fl(ent, F.Button2) = (buttons & 2) >> 1;
        if (move.Impulse != 0) Fl(ent, F.Impulse) = move.Impulse;
        // only send the impulse to qc once
        move.Impulse = 0;

        int movementLoss = 0, packetLoss = 0;
        if (client.Connection is not null)
        {
            packetLoss = PacketLossCounter?.Invoke(client) ?? 0;
            foreach (int count in client.MovementCount) if (count < 0) movementLoss++;
        }

        Vec(ent, F.VAngle) = move.ViewAngles;
        Fl(ent, F.Button3) = (buttons >> 2) & 1;
        Fl(ent, F.Button4) = (buttons >> 3) & 1;
        Fl(ent, F.Button5) = (buttons >> 4) & 1;
        Fl(ent, F.Button6) = (buttons >> 5) & 1;
        Fl(ent, F.Button7) = (buttons >> 6) & 1;
        Fl(ent, F.Button8) = (buttons >> 7) & 1;
        Fl(ent, F.Button9) = (buttons >> 11) & 1;
        Fl(ent, F.Button10) = (buttons >> 12) & 1;
        Fl(ent, F.Button11) = (buttons >> 13) & 1;
        Fl(ent, F.Button12) = (buttons >> 14) & 1;
        Fl(ent, F.Button13) = (buttons >> 15) & 1;
        Fl(ent, F.Button14) = (buttons >> 16) & 1;
        Fl(ent, F.Button15) = (buttons >> 17) & 1;
        Fl(ent, F.Button16) = (buttons >> 18) & 1;
        Fl(ent, F.ButtonUse) = (buttons >> 8) & 1;
        Fl(ent, F.ButtonChat) = (buttons >> 9) & 1;
        Fl(ent, F.CursorActive) = (buttons >> 10) & 1;
        Vec(ent, F.Movement) = new QcVector(move.ForwardMove, move.SideMove, move.UpMove);
        Vec(ent, F.CursorScreen) = move.CursorScreen;
        Vec(ent, F.CursorTraceStart) = move.CursorStart;
        Vec(ent, F.CursorTraceEndPos) = move.CursorImpact;
        // The entity number is the client's claim: anything that is not a live entity becomes the world.
        Int(ent, F.CursorTraceEnt) = IsLive(move.CursorEntity) ? move.CursorEntity : 0;
        Fl(ent, F.Ping) = client.Ping * 1000.0f;
        Fl(ent, F.PingPacketLoss) = packetLoss / 64.0f;
        Fl(ent, F.PingMovementLoss) = movementLoss / 64.0f;
    }

    // SV_Physics_ClientEntity_NoThink: the movement half of a client entity's frame, used when its
    // own input packets drive it (SV_Physics_ClientMove).
    private void ClientEntityNoThink(int ent)
    {
        switch ((int)Fl(ent, F.MoveType))
        {
            case MoveTypeFollow:
                PhysicsFollow(ent);
                break;
            case MoveTypeNoClip:
                Vec(ent, F.Origin) = MA(Vec(ent, F.Origin), (float)FrameTime, Vec(ent, F.Velocity));
                Vec(ent, F.Angles) = MA(Vec(ent, F.Angles), (float)FrameTime, Vec(ent, F.AVelocity));
                break;
            case MoveTypeStep:
                PhysicsStep(ent);
                break;
            case MoveTypeWalk:
            case MoveTypeFly:
            case MoveTypeFlyWorldOnly:
                WalkMove(ent);
                break;
            case MoveTypeToss:
            case MoveTypeBounce:
            case MoveTypeBounceMissile:
            case MoveTypeFlyMissile:
                PhysicsToss(ent);
                break;
        }
    }

    /// <summary>
    /// SV_Physics_ClientMove: run one of a client's own input commands as a physics step of
    /// <paramref name="frameTime"/> seconds (DP_SV_PLAYERPHYSICS / sv_clmovement_*: the client's
    /// packets, not the server's ticks, move it). Called by the network layer per received move.
    /// </summary>
    internal void ClientMove(SvClient client, double frameTime)
    {
        int ent = client.Edict;
        double savedFrameTime = FrameTime;
        FrameTime = frameTime;
        try
        {
            // call player physics, this needs the proper frametime
            Vm.GlobalFloat(G.FrameTime) = (float)FrameTime;
            PlayerPhysics(client);

            // call standard client pre-think, with frametime = 0
            SetTime(Time);
            Vm.GlobalFloat(G.FrameTime) = 0;
            Self = ent;
            Exec(Fn.PlayerPreThink, "QC function PlayerPreThink is missing");
            Vm.GlobalFloat(G.FrameTime) = (float)FrameTime;

            // make sure the velocity is sane (not a NaN)
            CheckVelocity(ent);

            // perform movetype behaviour; note: will always be MOVETYPE_WALK if disableclientprediction = 0
            ClientEntityNoThink(ent);
            if (Vm.IsFree(ent)) return;

            // call standard player post-think, with frametime = 0
            SetTime(Time);
            Vm.GlobalFloat(G.FrameTime) = 0;
            Self = ent;
            Exec(Fn.PlayerPostThink, "QC function PlayerPostThink is missing");
            Vm.GlobalFloat(G.FrameTime) = (float)FrameTime;

            if (Fl(ent, F.FixAngle) != 0)
            {
                // angle fixing was requested by physics code...so store the current angles for later use
                client.FixAngleAngles = Vec(ent, F.Angles);
                client.FixAngleAnglesSet = true;
                // and clear fixangle for the next frame
                Fl(ent, F.FixAngle) = 0;
            }
        }
        finally { FrameTime = savedFrameTime; }
    }

    // SV_Physics_ClientEntity_PreThink
    private void ClientEntityPreThink(SvClient client)
    {
        // don't do physics on disconnected clients, FrikBot relies on this
        if (!client.Begun) return;
        int ent = client.Edict;
        // make sure the velocity is sane (not a NaN)
        CheckVelocity(ent);
        // don't run physics here if running asynchronously
        if (client.ClMovementInputTimeout <= 0) PlayerPhysics(client);
        // make sure the velocity is still sane (not a NaN)
        CheckVelocity(ent);
        // call standard client pre-think
        SetTime(Time);
        Self = ent;
        Exec(Fn.PlayerPreThink, "QC function PlayerPreThink is missing");
        // make sure the velocity is still sane (not a NaN)
        if (!Vm.IsFree(ent)) CheckVelocity(ent);
    }

    // SV_Physics_ClientEntity_PostThink
    private void ClientEntityPostThink(SvClient client)
    {
        // don't do physics on disconnected clients, FrikBot relies on this
        if (!client.Begun) return;
        int ent = client.Edict;
        // make sure the velocity is sane (not a NaN)
        CheckVelocity(ent);
        // call standard player post-think
        SetTime(Time);
        Self = ent;
        Exec(Fn.PlayerPostThink, "QC function PlayerPostThink is missing");
        if (Vm.IsFree(ent)) return;
        // make sure the velocity is still sane (not a NaN)
        CheckVelocity(ent);

        if (Fl(ent, F.FixAngle) != 0)
        {
            // angle fixing was requested by physics code...so store the current angles for later use
            client.FixAngleAngles = Vec(ent, F.Angles);
            client.FixAngleAnglesSet = true;
            // and clear fixangle for the next frame
            Fl(ent, F.FixAngle) = 0;
        }

        // decrement the countdown variable used to decide when to go back to synchronous physics
        client.ClMovementInputTimeout = client.ClMovementInputTimeout > FrameTime ? client.ClMovementInputTimeout - (float)FrameTime : 0;
    }

    // SV_Physics_ClientEntity
    private void ClientEntity(SvClient client)
    {
        // don't do physics on disconnected clients, FrikBot relies on this
        if (!client.Begun)
        {
            client.Cmd = default;
            return;
        }
        int ent = client.Edict;
        // make sure the velocity is sane (not a NaN)
        CheckVelocity(ent);
        bool synchronous = client.ClMovementInputTimeout <= 0;   // don't run physics here if running asynchronously

        switch ((int)Fl(ent, F.MoveType))
        {
            case MoveTypePush:
            case MoveTypeFakePush:
                PhysicsPusher(ent);
                break;
            case MoveTypeNone:
                // LadyHavoc: manually inlined the thinktime check here because MOVETYPE_NONE is used often
                if (Fl(ent, F.NextThink) > 0 && Fl(ent, F.NextThink) <= Time + FrameTime) RunThink(ent);
                break;
            case MoveTypeFollow:
                RunThink(ent);
                if (synchronous && !Vm.IsFree(ent)) PhysicsFollow(ent);
                break;
            case MoveTypeNoClip:
                RunThink(ent);
                if (synchronous && !Vm.IsFree(ent))
                {
                    CheckWater(ent);
                    if (Vm.IsFree(ent)) break;
                    Vec(ent, F.Origin) = MA(Vec(ent, F.Origin), (float)FrameTime, Vec(ent, F.Velocity));
                    Vec(ent, F.Angles) = MA(Vec(ent, F.Angles), (float)FrameTime, Vec(ent, F.AVelocity));
                }
                break;
            case MoveTypeStep:
                if (synchronous) PhysicsStep(ent);
                if (!Vm.IsFree(ent) && RunThink(ent)) StepWaterCheck(ent);
                break;
            case MoveTypeWalk:
            case MoveTypeFly:
            case MoveTypeFlyWorldOnly:
                RunThink(ent);
                if (synchronous && !Vm.IsFree(ent)) WalkMove(ent);
                break;
            case MoveTypeToss:
            case MoveTypeBounce:
            case MoveTypeBounceMissile:
            case MoveTypeFlyMissile:
                // regular thinking
                RunThink(ent);
                if (synchronous && !Vm.IsFree(ent)) PhysicsToss(ent);
                break;
            default:
                // MOVETYPE_PHYSICS thinks; the user movetypes (MOVETYPE_QCPLAYER and friends) do
                // nothing at all here - the program moves them from its own think or SV_PlayerPhysics.
                if ((int)Fl(ent, F.MoveType) == MoveTypePhysics) RunThink(ent);
                break;
        }
        if (Vm.IsFree(ent)) return;

        CheckVelocity(ent);
        LinkEdict(ent);
        TouchAreaGrid(ent);
        if (!Vm.IsFree(ent)) CheckVelocity(ent);
    }

    // ---- monster movement (sv_move.c) ------------------------------------------------------------------

    // SV_CheckBottom: "Returns false if any part of the bottom of the entity is off an edge that is
    // not a staircase."
    private bool CheckBottom(int ent)
    {
        QcVector origin = Vec(ent, F.Origin);
        QcVector mins = Add(origin, Vec(ent, F.Mins)), maxs = Add(origin, Vec(ent, F.Maxs));

        // if all of the points under the corners are solid world, don't bother with the tougher checks
        bool easy = true;
        for (int x = 0; x <= 1 && easy; x++)
            for (int y = 0; y <= 1 && easy; y++)
                if ((World.PointSuperContents(new QcVector(x != 0 ? maxs.X : mins.X, y != 0 ? maxs.Y : mins.Y, mins.Z - 1)) & (SvWorld.ContentsSolid | SvWorld.ContentsBody)) == 0)
                    easy = false;
        if (easy) return true;   // we got out easy

        // check it for real: the midpoint must be within 16 of the bottom
        float stepHeight = _cv.StepHeight;
        QcVector start = new((mins.X + maxs.X) * 0.5f, (mins.Y + maxs.Y) * 0.5f, mins.Z);
        QcVector stop = new(start.X, start.Y, start.Z - 2 * stepHeight);
        SvTrace trace = Trace(start, default, default, stop, SvWorld.MoveNoMonsters, ent);
        if (trace.Fraction == 1.0f) return false;
        float mid = trace.EndPos.Z, bottom = mid;

        // the corners must be within 16 of the midpoint
        for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            {
                start.X = stop.X = x != 0 ? maxs.X : mins.X;
                start.Y = stop.Y = y != 0 ? maxs.Y : mins.Y;
                trace = Trace(start, default, default, stop, SvWorld.MoveNoMonsters, ent);
                if (trace.Fraction != 1.0f && trace.EndPos.Z > bottom) bottom = trace.EndPos.Z;
                if (trace.Fraction == 1.0f || mid - trace.EndPos.Z > stepHeight) return false;
            }
        return true;
    }

    // SV_movestep: "Called by monster program code. The move will be adjusted for slopes and stairs,
    // but if the move isn't possible, no move is done and false is returned"
    private bool MoveStep(int ent, QcVector move, bool relink, bool noEnemy, bool setTrace)
    {
        QcVector oldOrg = Vec(ent, F.Origin), newOrg = Add(oldOrg, move), mins = Vec(ent, F.Mins), maxs = Vec(ent, F.Maxs);
        SvTrace trace;

        // flying monsters don't step up
        if ((IntFlags(ent) & (FlSwim | FlFly)) != 0)
        {
            // try one move with vertical motion, then one without
            for (int i = 0; i < 2; i++)
            {
                QcVector origin = Vec(ent, F.Origin);
                newOrg = Add(origin, move);
                int enemy = noEnemy ? 0 : EdictField(ent, F.Enemy);
                if (i == 0 && enemy != 0)
                {
                    float dz = origin.Z - Vec(enemy, F.Origin).Z;
                    if (dz > 40) newOrg.Z -= 8;
                    if (dz < 30) newOrg.Z += 8;
                }
                trace = Trace(origin, mins, maxs, newOrg, SvWorld.MoveNormal, ent);
                if (trace.Fraction == 1)
                {
                    if ((IntFlags(ent) & FlSwim) != 0 && (World.PointSuperContents(trace.EndPos) & SvWorld.ContentsLiquidsMask) == 0)
                        return false;   // swim monster left water
                    Vec(ent, F.Origin) = trace.EndPos;
                    if (relink)
                    {
                        LinkEdict(ent);
                        TouchAreaGrid(ent);
                    }
                    return true;
                }
                if (enemy == 0) break;
            }
            return false;
        }

        // push down from a step height above the wished position
        float stepHeight = _cv.StepHeight;
        newOrg.Z += stepHeight;
        QcVector end = newOrg;
        end.Z -= stepHeight * 2;

        trace = Trace(newOrg, mins, maxs, end, SvWorld.MoveNormal, ent);
        if (setTrace) SetTraceGlobals(trace);
        if (trace.StartSolid)
        {
            newOrg.Z -= stepHeight;
            trace = Trace(newOrg, mins, maxs, end, SvWorld.MoveNormal, ent);
            if (setTrace) SetTraceGlobals(trace);
            if (trace.StartSolid) return false;
        }
        if (trace.Fraction == 1)
        {
            // if monster had the ground pulled out, go ahead and fall
            if ((IntFlags(ent) & FlPartialGround) != 0)
            {
                Vec(ent, F.Origin) = Add(Vec(ent, F.Origin), move);
                if (relink)
                {
                    LinkEdict(ent);
                    TouchAreaGrid(ent);
                }
                if (!Vm.IsFree(ent)) Fl(ent, F.Flags) = IntFlags(ent) & ~FlOnGround;
                return true;
            }
            return false;   // walked off an edge
        }

        // check point traces down for dangling corners
        Vec(ent, F.Origin) = trace.EndPos;
        if (!CheckBottom(ent))
        {
            if ((IntFlags(ent) & FlPartialGround) != 0)
            {
                // entity had floor mostly pulled out from underneath it and is trying to correct
                if (relink)
                {
                    LinkEdict(ent);
                    TouchAreaGrid(ent);
                }
                return true;
            }
            Vec(ent, F.Origin) = oldOrg;
            return false;
        }

        if ((IntFlags(ent) & FlPartialGround) != 0) Fl(ent, F.Flags) = IntFlags(ent) & ~FlPartialGround;

        // gameplayfix: check if reached pretty steep plane and bail
        if (_cv.NoStepMoveOnSteepSlopes && trace.PlaneNormal.Z < 0.5f)
        {
            Vec(ent, F.Origin) = oldOrg;
            return false;
        }

        Int(ent, F.GroundEntity) = trace.Ent < 0 ? 0 : trace.Ent;

        // the move is ok
        if (relink)
        {
            LinkEdict(ent);
            TouchAreaGrid(ent);
        }
        return true;
    }

    // VM_changeyaw on an entity: turn .angles_y toward .ideal_yaw by at most .yaw_speed.
    private void ChangeYaw(int ent)
    {
        float current = AngleMod360(Vec(ent, F.Angles).Y), ideal = Fl(ent, F.IdealYaw), speed = Fl(ent, F.YawSpeed);
        if (current == ideal) return;
        float move = ideal - current;
        if (ideal > current)
        {
            if (move >= 180) move -= 360;
        }
        else if (move <= -180) move += 360;
        if (move > 0)
        {
            if (move > speed) move = speed;
        }
        else if (move < -speed) move = -speed;
        Vec(ent, F.Angles).Y = AngleMod360(current + move);
    }

    // SV_StepDirection: "Turns to the movement direction, and walks the current distance if facing it."
    private bool StepDirection(int ent, float yaw, float dist)
    {
        Fl(ent, F.IdealYaw) = yaw;
        ChangeYaw(ent);
        double radians = yaw * Math.PI * 2 / 360;
        QcVector move = new((float)(Math.Cos(radians) * dist), (float)(Math.Sin(radians) * dist), 0);
        QcVector oldOrigin = Vec(ent, F.Origin);
        bool moved = MoveStep(ent, move, false, false, false);
        if (moved)
        {
            float delta = Vec(ent, F.Angles).Y - Fl(ent, F.IdealYaw);
            // not turned far enough, so don't take the step
            if (delta > 45 && delta < 315) Vec(ent, F.Origin) = oldOrigin;
        }
        LinkEdict(ent);
        TouchAreaGrid(ent);
        return moved;
    }

    // SV_NewChaseDir
    private void NewChaseDir(int actor, int enemy, float dist)
    {
        const float NoDir = -1;
        float oldDir = AngleMod360((int)(Fl(actor, F.IdealYaw) / 45) * 45);
        float turnAround = AngleMod360(oldDir - 180);
        float deltaX = Vec(enemy, F.Origin).X - Vec(actor, F.Origin).X, deltaY = Vec(enemy, F.Origin).Y - Vec(actor, F.Origin).Y;
        float d1 = deltaX > 10 ? 0 : deltaX < -10 ? 180 : NoDir;
        float d2 = deltaY < -10 ? 270 : deltaY > 10 ? 90 : NoDir;
        float tdir;

        // try direct route
        if (d1 != NoDir && d2 != NoDir)
        {
            tdir = d1 == 0 ? (d2 == 90 ? 45 : 315) : (d2 == 90 ? 135 : 215);
            if (tdir != turnAround && StepDirection(actor, tdir, dist)) return;
        }
        // try other directions
        if (((_core.Random.Next() & 3) & 1) != 0 || MathF.Abs(deltaY) > MathF.Abs(deltaX)) (d1, d2) = (d2, d1);
        if (d1 != NoDir && d1 != turnAround && StepDirection(actor, d1, dist)) return;
        if (d2 != NoDir && d2 != turnAround && StepDirection(actor, d2, dist)) return;

        // there is no direct path to the player, so pick another direction
        if (oldDir != NoDir && StepDirection(actor, oldDir, dist)) return;
        if ((_core.Random.Next() & 1) != 0)   // randomly determine direction of search
        {
            for (tdir = 0; tdir <= 315; tdir += 45)
                if (tdir != turnAround && StepDirection(actor, tdir, dist)) return;
        }
        else
        {
            for (tdir = 315; tdir >= 0; tdir -= 45)
                if (tdir != turnAround && StepDirection(actor, tdir, dist)) return;
        }
        if (turnAround != NoDir && StepDirection(actor, turnAround, dist)) return;

        Fl(actor, F.IdealYaw) = oldDir;   // can't move
        // if a bridge was pulled out from underneath a monster, it may not have a valid standing
        // position at all
        if (!CheckBottom(actor)) Fl(actor, F.Flags) = IntFlags(actor) | FlPartialGround;   // SV_FixCheckBottom
    }

    // #67 void(float dist) movetogoal
    private void MoveToGoal(QcVm vm)
    {
        Parms(1, 1, "VM_SV_MoveToGoal");
        int ent = Self;
        if (ent == 0 || !IsLive(ent)) return;
        int goal = EdictField(ent, F.GoalEntity);
        float dist = vm.ArgFloat(0);
        if ((IntFlags(ent) & (FlOnGround | FlFly | FlSwim)) == 0)
        {
            vm.ReturnFloat(0);
            return;
        }
        // if the next step hits the enemy, return immediately
        if (EdictField(ent, F.Enemy) != 0 && World.TryGetArea(goal, out QcVector goalMins, out QcVector goalMaxs)
            && World.TryGetArea(ent, out QcVector entMins, out QcVector entMaxs)
            && !(goalMins.X > entMaxs.X + dist || goalMaxs.X < entMins.X - dist || goalMins.Y > entMaxs.Y + dist || goalMaxs.Y < entMins.Y - dist
                || goalMins.Z > entMaxs.Z + dist || goalMaxs.Z < entMins.Z - dist))
            return;
        // bump around...
        if ((_core.Random.Next() & 3) == 1 || !StepDirection(ent, Fl(ent, F.IdealYaw), dist)) NewChaseDir(ent, goal, dist);
    }

    // #32 float(float yaw, float dist[, float settrace]) walkmove
    private void WalkMove(QcVm vm)
    {
        Parms(2, 3, "VM_SV_walkmove");
        vm.ReturnFloat(0);
        int ent = Self;
        if (!Modifiable(ent, "walkmove")) return;
        float yaw = vm.ArgFloat(0), dist = vm.ArgFloat(1);
        bool setTrace = vm.ArgCount >= 3 && vm.ArgFloat(2) != 0;
        if ((IntFlags(ent) & (FlOnGround | FlFly | FlSwim)) == 0) return;
        double radians = yaw * Math.PI * 2 / 360;
        QcVector move = new((float)(Math.Cos(radians) * dist), (float)(Math.Sin(radians) * dist), 0);
        // save program state, because SV_movestep may call other progs
        int oldSelf = Self;
        bool moved = MoveStep(ent, move, true, false, setTrace);
        // restore program state
        Self = oldSelf;
        vm.ReturnFloat(moved ? 1 : 0);
    }

    // #34 float() droptofloor
    private void DropToFloor(QcVm vm)
    {
        Parms(0, 0, "VM_SV_droptofloor");
        // assume failure if it returns early
        vm.ReturnFloat(0);
        int ent = Self;
        if (!Modifiable(ent, "droptofloor")) return;

        QcVector origin = Vec(ent, F.Origin), mins = Vec(ent, F.Mins), maxs = Vec(ent, F.Maxs);
        QcVector end = origin;
        end.Z -= 4096;   // "if (sv.worldmodel->brush.isq3bsp)": the only maps read here
        // (not SV_GenericHitSuperContentsMask(ent): that made an item touching a monster start solid)
        SvTrace trace = World.Trace(origin, mins, maxs, end, SvWorld.MoveNormal, ent, SvWorld.ContentsSolid, _cv.ExtendMoveLength);
        // droptofloor_bsp_failcond on a Quake 3 map is "trace->startsolid"
        if (trace.StartSolid)
        {
            if (!_cv.DropToFloorStartSolid)
            {
                Warning($"droptofloor at \"{origin.X} {origin.Y} {origin.Z}\": badly placed entity \"{Vm.GetString(Int(ent, F.ClassName))}\", startsolid: {(trace.StartSolid ? 1 : 0)} allsolid: {(trace.AllSolid ? 1 : 0)}\n");
                return;
            }
            // most likely a badly placed item: try a line from the bottom centre of the box instead
            QcVector offset = new(0.5f * (mins.X + maxs.X), 0.5f * (mins.Y + maxs.Y), mins.Z);
            trace = World.Trace(Add(origin, offset), default, default, Add(end, offset), SvWorld.MoveNormal, ent, SvWorld.ContentsSolid, _cv.ExtendMoveLength);
            if (trace.StartSolid)
            {
                Warning($"droptofloor at \"{origin.X} {origin.Y} {origin.Z}\": sv_gameplayfix_droptofloorstartsolid COULD NOT FIX badly placed entity \"{Vm.GetString(Int(ent, F.ClassName))}\"\n");
                return;
            }
            Vec(ent, F.Origin) = Sub(trace.EndPos, offset);
        }
        else Vec(ent, F.Origin) = trace.EndPos;

        LinkEdict(ent);
        SetOnGround(ent, trace.Ent);
        vm.ReturnFloat(1);
        // if support is destroyed, keep suspended (gross hack for floating items in various maps)
        Priv(ent).SuspendedInAir = true;
    }

    // #40 float(entity e) checkbottom
    private void CheckBottomBuiltin(QcVm vm)
    {
        Parms(1, 1, "VM_SV_checkbottom");
        int ent = vm.ArgEdict(0);
        vm.ReturnFloat(IsLive(ent) && CheckBottom(ent) ? 1 : 0);
    }

    // #44 vector(entity e, float speed) aim: "Pick a vector for the player to shoot along"
    private void Aim(QcVm vm)
    {
        Parms(2, 2, "VM_SV_aim");
        QcVector vForward = vm.GlobalVector(G.VForward);
        // assume failure if it returns early
        vm.ReturnVector(vForward);
        // if sv_aim is so high it can't possibly accept anything, skip out early
        if (_cv.Aim >= 1) return;
        int ent = vm.ArgEdict(0);
        if (ent == 0) { Warning("aim: can not use world entity\n"); return; }
        if (!IsLive(ent)) { Warning("aim: can not use free entity\n"); return; }

        QcVector start = Vec(ent, F.Origin);
        start.Z += 20;
        const int mask = SvWorld.ContentsSolid | SvWorld.ContentsBody;
        float team = Fl(ent, F.Team);

        // try sending a trace straight
        QcVector dir = vForward, end = MA(start, 2048, dir);
        SvTrace tr = World.Trace(start, default, default, end, SvWorld.MoveNormal, ent, mask, _cv.ExtendMoveLength);
        if (tr.Ent > 0 && Fl(tr.Ent, F.TakeDamage) == 2 /* DAMAGE_AIM */ && (!_cv.TeamPlay || team <= 0 || team != Fl(tr.Ent, F.Team))) return;

        // try all possible entities
        QcVector bestDir = dir;
        float bestDist = _cv.Aim;
        int bestEnt = 0;
        for (int check = 1; check < Vm.NumEdicts; check++)
        {
            if (Vm.IsFree(check) || Fl(check, F.TakeDamage) != 2 || check == ent) continue;
            if (_cv.TeamPlay && team > 0 && team == Fl(check, F.Team)) continue;   // don't aim at teammate
            QcVector o = Vec(check, F.Origin), cmins = Vec(check, F.Mins), cmaxs = Vec(check, F.Maxs);
            end = new QcVector(o.X + 0.5f * (cmins.X + cmaxs.X), o.Y + 0.5f * (cmins.Y + cmaxs.Y), o.Z + 0.5f * (cmins.Z + cmaxs.Z));
            dir = Sub(end, start);
            float length = MathF.Sqrt(Dot(dir, dir));
            if (length != 0) dir = Scale(dir, 1 / length);
            float dist = Dot(dir, vForward);
            if (dist < bestDist) continue;   // to far to turn
            tr = World.Trace(start, default, default, end, SvWorld.MoveNormal, ent, mask, _cv.ExtendMoveLength);
            if (tr.Ent == check)
            {
                // can shoot at this one
                bestDist = dist;
                bestEnt = check;
            }
        }

        if (bestEnt != 0)
        {
            dir = Sub(Vec(bestEnt, F.Origin), Vec(ent, F.Origin));
            float dist = Dot(dir, vForward);
            end = Scale(vForward, dist);
            end.Z = dir.Z;
            float length = MathF.Sqrt(Dot(end, end));
            if (length != 0) end = Scale(end, 1 / length);
            vm.ReturnVector(end);
        }
        else vm.ReturnVector(bestDir);
    }
}
