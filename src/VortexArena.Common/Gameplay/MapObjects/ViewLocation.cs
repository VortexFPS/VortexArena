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
//  * INITPRIO_FINDTARGET (anchor resolve) is queued at spawn and drained in MapObjectsRegistry.RunPostSpawn.
//  * Native snapshots carry the active region's anchors and flags to prediction and the viewed camera.

using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;

// NOTE: QC player .viewloc is already promoted on the partial Entity by the player-physics port
// (Physics/PlayerPhysicsState.cs Entity.ViewLoc — the swim/ladder code reads it). We reuse that field here
// (the server stamps it each tick), so this file declares NO new Entity field.

namespace VortexArena.Common.Gameplay
{
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

            // WarpZoneLib_ExactTrigger_Init keeps angles for the brush transform and treats
            // authored bounds as an explicit request for a box.
            if (this_.MoveDir == Vector3.Zero && this_.Angles != Vector3.Zero)
                this_.MoveDir = Math.QMath.Forward(this_.Angles);
            Vector3 mins = this_.Mins;
            Vector3 maxs = this_.Maxs;
            bool useBounds = mins != Vector3.Zero || maxs != Vector3.Zero;
            this_.Solid = Solid.Trigger;
            this_.MoveType = MoveType.None;
            if (Api.Services is not null)
            {
                if (!string.IsNullOrEmpty(this_.Model))
                    Api.Entities.SetModel(this_, this_.Model);
                if (!useBounds)
                {
                    mins = this_.Mins;
                    maxs = this_.Maxs;
                }
                float scale = this_.ScaleFactor == 0f ? 1f : this_.ScaleFactor;
                Api.Entities.SetSize(this_, mins * scale, maxs * scale);
            }
            MapMover.IndexRegister(this_);

            // The deferred anchor resolution schedules the first think.
            this_.Think = trigger => ViewLocThink(trigger, useBounds);
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

            this_.NextThink = MathF.Max(MapMover.Now(), float.Epsilon);
        }

        /// <summary>
        /// Port of <c>viewloc_think</c> (viewloc.qc:16-48): clear .viewloc off every player that pointed at this
        /// trigger, then re-stamp it onto every player currently overlapping the volume. Runs every frame (NOT
        /// touch — touch can't "untouch" with several clients in one trigger).
        /// </summary>
        private static void ViewLocThink(Entity this_, bool useBounds)
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
                    if (ExactTriggerTouch(this_, it, useBounds))
                        it.ViewLoc = this_;
                }
            }

            this_.NextThink = MathF.Max(MapMover.Now(), float.Epsilon);
        }

        /// <summary>QC <c>WarpZoneLib_ExactTrigger_Touch</c> bounds check followed by the brush clip.</summary>
        private static bool ExactTriggerTouch(Entity trig, Entity ent, bool useBounds)
        {
            bool quake3Compat = CompatRemaps.IsQ3Compat;
            Vector3 padding = quake3Compat ? Vector3.Zero : Vector3.One;
            Vector3 min = ent.AbsMin - padding;
            Vector3 max = ent.AbsMax + padding;
            if (min.X > trig.AbsMax.X || max.X < trig.AbsMin.X ||
                min.Y > trig.AbsMax.Y || max.Y < trig.AbsMin.Y ||
                min.Z > trig.AbsMax.Z || max.Z < trig.AbsMin.Z)
                return false;

            return useBounds || trig.ModelIndex == 0 ||
                Api.Trace.BoxTouchesBrushModel(trig, ent, ent.Mins - padding, ent.Maxs + padding, quake3Compat);
        }

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
            MapMover.IndexRegister(this_);
        }
    }
}
