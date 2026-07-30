// Port of qcsrc/common/mapobjects/trigger/viewloc.qc — SVQC half.
//   trigger_viewlocation        — a volume that, while a player is inside, sets that player's .viewloc to a
//                                 start/end camera pair (the 2.5D "side-scroller" camera regions).
//   target_viewlocation_start   — the camera anchor #1 (.cnt = 1), passive findable ref.
//   target_viewlocation_end     — the camera anchor #2 (.cnt = 2), passive findable ref.
//   target_viewlocation         — compat alias for target_viewlocation_start.
//
// Server contract: the trigger resolves its start/end anchors at INITPRIO_FINDTARGET, then a per-frame think
// (NOT .touch — QC notes touch can't "untouch" with multiple clients in one trigger) clears+re-stamps each
// inside player's .viewloc. The anchors carry an .angles (with the single-float .angle anglehack folded into
// angles_y) for the camera orientation.
//
// Port notes:
//  * WarpZoneLib_ExactTrigger_Init/Touch are mirrored by MapMover.InitTrigger (SOLID_TRIGGER + bbox) and a
//    box-overlap test, exactly as MapVolumes does for trigger_conveyor/func_ladder.
//  * INITPRIO_FINDTARGET (anchor resolve) is queued at spawn and drained in MapObjectsRegistry.RunPostSpawn.
//  * There is NO client camera consumer yet (the CSQC side rotates the view to the start→end line; the port's
//    client is free-look only). So the trigger spawns, ticks, and stamps player.ViewLoc faithfully, but the
//    camera does not yet lock to the region. Documented in followups. (The Net_LinkEntity/send halves are
//    client networking — out of scope for the server port.)

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

// NOTE: QC player .viewloc is already promoted on the partial Entity by the player-physics port
// (Physics/PlayerPhysicsState.cs Entity.ViewLoc — the swim/ladder code reads it). We reuse that field here
// (the server stamps it each tick), so this file declares NO new Entity field.

namespace XonoticGodot.Common.Gameplay
{
    using XonoticGodot.Common.Framework;

    /// <summary><c>trigger_viewlocation</c> + <c>target_viewlocation_start/end</c>. Registered by <see cref="MapObjectsRegistry"/>.</summary>
    public static class ViewLocation
    {
        // The INITPRIO_FINDTARGET deferred anchor-resolve queue (drained by MapObjectsRegistry.RunPostSpawn).
        private static readonly List<Entity> _pendingInit = new();

        // ===================================================================
        //  trigger_viewlocation
        // ===================================================================

        /// <summary><c>spawnfunc(trigger_viewlocation)</c> (viewloc.qc:92-99).</summary>
        public static void TriggerViewLocationSetup(Entity this_)
        {
            this_.ClassName = "trigger_viewlocation";

            // QC: if(this.target == "") { LOG_INFO("^1FAIL!"); delete(this); return; }
            if (string.IsNullOrEmpty(this_.Target))
            {
                MapMover.RemoveEntity(this_);
                return;
            }

            MapMover.InitTrigger(this_); // QC WarpZoneLib_ExactTrigger_Init(this, false): SOLID_TRIGGER + bbox.
            MapMover.IndexRegister(this_);

            // QC: InitializeEntity(this, viewloc_init, INITPRIO_FINDTARGET);
            Queue(this_);
        }

        private static void Queue(Entity e)
        {
            if (!_pendingInit.Contains(e))
                _pendingInit.Add(e);
        }

        /// <summary>
        /// Resolve every queued trigger's start/end anchors — the headless analogue of QC's INITPRIO_FINDTARGET
        /// pass (run by <see cref="MapObjectsRegistry.RunPostSpawn"/>). Safe to call repeatedly.
        /// </summary>
        public static void RunDeferredInit()
        {
            if (_pendingInit.Count == 0)
                return;
            Entity[] batch = _pendingInit.ToArray();
            _pendingInit.Clear();
            foreach (Entity e in batch)
                if (!e.IsFreed)
                    ViewLocInit(e);
        }

        /// <summary>Port of <c>viewloc_init</c> (viewloc.qc:65-90): bind the start/end anchors, then start ticking.</summary>
        private static void ViewLocInit(Entity this_)
        {
            // QC: find the start anchor among .target matches, the end anchor among .target2 matches.
            foreach (Entity e in MapMover.FindByTargetName(this_.Target))
                if (e.ClassName == "target_viewlocation_start")
                {
                    this_.Enemy = e;
                    break;
                }
            foreach (Entity e in MapMover.FindByTargetName(this_.Target2))
                if (e.ClassName == "target_viewlocation_end")
                {
                    this_.GoalEntity = e;
                    break;
                }

            // QC: if(!this.enemy) { LOG_INFO("^1FAIL!"); delete(this); return; }
            if (this_.Enemy is null)
            {
                MapMover.RemoveEntity(this_);
                return;
            }

            // QC: if(!this.goalentity) this.goalentity = this.enemy; // make them match so CSQC knows what to do
            if (this_.GoalEntity is null)
                this_.GoalEntity = this_.Enemy;

            // QC Net_LinkEntity(trigger_viewloc_send) is client networking — the shared-entity facade carries it.

            this_.Think = ViewLocThink;
            this_.NextThink = MapMover.Now();
        }

        /// <summary>
        /// Port of <c>viewloc_think</c> (viewloc.qc:16-48): clear .viewloc off every player that pointed at this
        /// trigger, then re-stamp it onto every player currently overlapping the volume. Runs every frame (NOT
        /// touch — touch can't "untouch" with several clients in one trigger).
        /// </summary>
        private static void ViewLocThink(Entity this_)
        {
            if (Api.Services is not null)
            {
                // QC: FOREACH_CLIENT(IS_PLAYER(it) && it.viewloc == this, it.viewloc = NULL);
                foreach (Entity it in Api.Entities.FindByClass("player"))
                    if (ReferenceEquals(it.ViewLoc, this_))
                        it.ViewLoc = null;

                // QC: FOREACH_CLIENT(!it.viewloc && IS_PLAYER(it), if(ExactTrigger_Touch(this,it)) it.viewloc = this);
                foreach (Entity it in Api.Entities.FindByClass("player"))
                {
                    if (it.ViewLoc is not null)
                        continue;
                    if ((it.Flags & EntFlags.Client) == 0)
                        continue;
                    if (ExactTriggerTouch(this_, it))
                        it.ViewLoc = this_;
                }
            }

            this_.NextThink = MapMover.Now();
        }

        /// <summary>
        /// QC <c>WarpZoneLib_ExactTrigger_Touch(trig, ent, …)</c> server-side reduction: an AABB overlap of the
        /// toucher's box against the trigger's volume (warpzone-clip refinement is rendering-only). Matches the
        /// box test MapVolumes/Teleporters use for their trigger volumes.
        /// </summary>
        private static bool ExactTriggerTouch(Entity trig, Entity ent)
            => ent.AbsMin.X <= trig.AbsMax.X && ent.AbsMax.X >= trig.AbsMin.X
            && ent.AbsMin.Y <= trig.AbsMax.Y && ent.AbsMax.Y >= trig.AbsMin.Y
            && ent.AbsMin.Z <= trig.AbsMax.Z && ent.AbsMax.Z >= trig.AbsMin.Z;

        // ===================================================================
        //  target_viewlocation_start / _end (+ compat alias)
        // ===================================================================

        /// <summary><c>spawnfunc(target_viewlocation_start)</c> (viewloc.qc:122-126): camera anchor #1 (cnt = 1).</summary>
        public static void StartSetup(Entity this_)
        {
            this_.ClassName = "target_viewlocation_start";
            this_.Cnt = 1; // QC: this.cnt = 1;
            ViewLocLink(this_);
        }

        /// <summary><c>spawnfunc(target_viewlocation_end)</c> (viewloc.qc:127-131): camera anchor #2 (cnt = 2).</summary>
        public static void EndSetup(Entity this_)
        {
            this_.ClassName = "target_viewlocation_end";
            this_.Cnt = 2; // QC: this.cnt = 2;
            ViewLocLink(this_);
        }

        /// <summary><c>spawnfunc(target_viewlocation)</c> (viewloc.qc:134-137): compat alias → _start.</summary>
        public static void CompatSetup(Entity this_) => StartSetup(this_);

        /// <summary>Port of <c>viewloc_link</c> (viewloc.qc:115-120): fold the single-float .angle into angles_y, index it.</summary>
        private static void ViewLocLink(Entity this_)
        {
            // QC: .float angle; if(this.angle) this.angles_y = this.angle;
            // The single-float `angle` anglehack is already folded into Angles.Y by the field-parse layer
            // (MapObjectFieldsExtra / GameWorld), so Angles already carries it — no extra step needed here.
            // QC Net_LinkEntity(viewloc_send) is client networking — carried by the shared-entity facade.
            MapMover.IndexRegister(this_);
        }
    }
}
