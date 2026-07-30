// Port of qcsrc/common/mapobjects/misc/dynlight.qc (dynlight) — SVQC half.
//
// dynlight is a real-time dynamic light source placed in a map. It can: sit still as a plain light; travel
// along a path_corner chain; FOLLOW an entity around (MOVETYPE_FOLLOW); or attach to a named TAG on a target
// model. It can spin in place (avelocity) in every mode, and toggles on/off when triggered.
//
// Port notes:
//  * The deferred enemy/path/tag lookups run at QC's INITPRIO_FINDTARGET (after the whole BSP lump spawns).
//    The port queues each dynlight at spawn and resolves them in MapObjectsRegistry.RunPostSpawn (the headless
//    analogue of the INITPRIO pass, run right after the door-link pass) — same shape as Doors.RunDeferredLinks.
//  * The path-following branch reuses the func_train pathing in QC (setthink(train_next)). The port's
//    TrainNext is private to MovingBrushes, so this file ports the minimal train_next/train_wait corner-walk
//    inline (a dynlight is a zero-size point entity: no bbox, no .view_ofs offset, no TRAIN_CURVE/TRAIN_TURN
//    spawnflags) on top of the public MapMover.CalcMove seam — the light now actually TRAVELS its path_corner
//    chain at .speed, firing each corner's targets and honouring per-corner .wait/.speed. It still toggles
//    and spins via avelocity throughout. (Full func_train parity — curve/turn/needactivation — stays in
//    MovingBrushes; a dynlight QUAKED exposes none of those keys.)
//  * There is NO dlight RENDER consumer yet (no DP-style dynamic-light system; that is T4 territory). So a
//    dynlight spawns, ticks, toggles light_lev, and (when following/attached) rides its target — but renders
//    no visible light. The server contract (light_lev/color/style/attach/follow/toggle) is faithful; the
//    on-screen glow is deferred. Documented in followups.
//  * QC's commented-out pflags (PFLAGS_FULLDYNAMIC / NOSHADOW / START_OFF light_lev=0) are commented in Base
//    too — they are mirrored as comments here, not behavior, to match Base exactly.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // ---- dynlight fields (dynlight.qc:13-16) ----
        /// <summary>QC dynlight <c>.light_lev</c> — light radius (0 = off; default 200).</summary>
        public float LightLev;
        /// <summary>QC dynlight <c>.lefty</c> — the remembered "on" light radius (restored when toggled back on).</summary>
        public float LightLefty;
        /// <summary>QC dynlight <c>.color</c> — light rgb+brightness ('1 1 1' = bright white; default).</summary>
        public Vector3 LightColor;
        /// <summary>QC dynlight <c>.dtagname</c> — the tag on the target model this light attaches to.</summary>
        public string DTagName = "";
        /// <summary>QC <c>.style</c> — lightstyle index (same as static lights).</summary>
        public int LightStyle;
        // NOTE: QC dynlight's follow bookkeeping <c>.v_angle</c> reuses the shared MOVETYPE_FOLLOW relative-angle
        // field <see cref="VAngle"/> (declared in Follow.cs, which ports follow_sameorigin's identical setup).
    }
}

namespace XonoticGodot.Common.Gameplay
{
    using XonoticGodot.Common.Framework;

    /// <summary><c>dynlight</c> — a real-time dynamic light (static / path / follow / tag-attach). Registered by <see cref="MapObjectsRegistry"/>.</summary>
    public static class DynamicLight
    {
        // ---- spawnflags (dynlight.qc:18 QUAKED + :11-12) ----
        public const int StartOff = 1 << 0; // START_OFF
        public const int NoShadow = 1 << 1; // NOSHADOW (= DNOSHADOW, commented in Base)
        public const int Follow = 1 << 2;   // FOLLOW (= DFOLLOW)

        /// <summary>QC dynlight think interval (dynlight.qc:44).</summary>
        private const float ThinkInterval = 0.1f;

        // The INITPRIO_FINDTARGET deferred-resolve queue (run by MapObjectsRegistry.RunPostSpawn). Each entry
        // is a dynlight whose follow/tag/path target must be resolved once the whole BSP lump has spawned.
        private static readonly List<Entity> _pendingInit = new();

        /// <summary><c>spawnfunc(dynlight)</c> (dynlight.qc:112-156).</summary>
        public static void DynlightSetup(Entity this_)
        {
            this_.ClassName = "dynlight";

            if (this_.LightLev == 0f)               // QC: if (!this.light_lev) this.light_lev = 200;
                this_.LightLev = 200f;
            if (this_.LightColor == Vector3.Zero)   // QC: if (!this.color) this.color = '1 1 1';
                this_.LightColor = new Vector3(1f, 1f, 1f);
            this_.LightLefty = this_.LightLev;      // QC: this.lefty = this.light_lev;

            this_.Use = DynlightUse;
            this_.Active = MapMover.ActiveActive;
            this_.SetActive = DynlightSetActive;
            this_.Reset = DynlightReset;

            MapMover.SetSize(this_, Vector3.Zero, Vector3.Zero); // QC: setsize(this, '0 0 0', '0 0 0');
            MapMover.SetOrigin(this_, this_.Origin);             // QC: setorigin(this, this.origin);
            this_.Solid = Solid.Not;                             // QC: this.solid = SOLID_NOT;
            // QC: this.pflags / DNOSHADOW / START_OFF lines are all commented out in Base — mirrored as comments.

            MapMover.IndexRegister(this_);

            // tag attaching (dynlight.qc:134-138): InitializeEntity(dynlight_find_target, INITPRIO_FINDTARGET).
            if (!string.IsNullOrEmpty(this_.DTagName))
            {
                this_.GateState = ModeTag;
                Queue(this_);
                return;
            }

            // entity following (dynlight.qc:141-145): InitializeEntity(dynlight_find_aiment, INITPRIO_FINDTARGET).
            if ((this_.SpawnFlags & Follow) != 0)
            {
                this_.GateState = ModeFollow;
                Queue(this_);
                return;
            }

            // path following (dynlight.qc:147-155): MOVETYPE_NOCLIP + InitializeEntity(dynlight_find_path, ...).
            if (!string.IsNullOrEmpty(this_.Target))
            {
                this_.MoveType = MoveType.Noclip;
                if (this_.Speed == 0f)
                    this_.Speed = 100f;
                this_.GateState = ModePath;
                Queue(this_);
                return;
            }
            // else: a static light. No think; sits still (and may spin via avelocity).
        }

        // GateState mode tags for the deferred-init dispatch (reuses the gate-state latch field, harmless here).
        private const int ModeTag = 1;
        private const int ModeFollow = 2;
        private const int ModePath = 3;

        private static void Queue(Entity e)
        {
            if (!_pendingInit.Contains(e))
                _pendingInit.Add(e);
        }

        /// <summary>
        /// Resolve every queued dynlight's follow/tag/path target — the headless analogue of QC's
        /// INITPRIO_FINDTARGET pass (run by <see cref="MapObjectsRegistry.RunPostSpawn"/> after the door-link
        /// pass). Safe to call repeatedly; the queue drains each call.
        /// </summary>
        public static void RunDeferredInit()
        {
            if (_pendingInit.Count == 0)
                return;
            Entity[] batch = _pendingInit.ToArray();
            _pendingInit.Clear();
            foreach (Entity e in batch)
            {
                if (e.IsFreed)
                    continue;
                switch (e.GateState)
                {
                    case ModeTag:    FindTarget(e); break;
                    case ModeFollow: FindAiment(e); break;
                    case ModePath:   FindPath(e); break;
                }
                e.GateState = 0;
            }
        }

        /// <summary>Port of <c>dynlight_find_aiment</c> (dynlight.qc:46-61): FOLLOW the named entity (MOVETYPE_FOLLOW).</summary>
        private static void FindAiment(Entity this_)
        {
            if (string.IsNullOrEmpty(this_.Target))
                return; // QC objerror; headless: inert

            Entity? targ = MapMover.FindFirstByTargetName(this_.Target);
            if (targ is null)
                return;

            this_.MoveType = MoveType.Follow;       // QC: set_movetype(this, MOVETYPE_FOLLOW);
            this_.Aiment = targ;                     // QC: this.aiment = targ;
            this_.Owner = targ;                      // QC: this.owner = targ;
            this_.PunchAngle = targ.Angles;          // QC: this.punchangle = targ.angles;
            this_.ViewOfs = this_.Origin - targ.Origin; // QC: this.view_ofs = this.origin - targ.origin;
            this_.VAngle = this_.Angles - targ.Angles; // QC: this.v_angle = this.angles - targ.angles;
            this_.Think = DynlightThink;
            this_.NextThink = MapMover.Now() + ThinkInterval;
        }

        /// <summary>Port of <c>dynlight_find_path</c> (dynlight.qc:62-73): ride a path_corner chain.</summary>
        private static void FindPath(Entity this_)
        {
            if (string.IsNullOrEmpty(this_.Target))
                return;

            Entity? targ = MapMover.FindFirstByTargetName(this_.Target);
            if (targ is null)
                return;

            this_.Target = targ.Target;             // QC: this.target = targ.target;
            MapMover.SetOrigin(this_, targ.Origin); // QC: setorigin(this, targ.origin);
            // QC: setthink(this, train_next). The port's MovingBrushes.TrainNext is private + bbox/curve/turn-
            // aware; a dynlight is a zero-size point light with none of those keys, so we walk the corner chain
            // with the minimal train_next/train_wait below (built on the public MapMover.CalcMove seam). Stash
            // the lookahead corner the way func_train_find does, then schedule the first leg.
            this_.FutureTarget = MapMover.FindFirstByTargetName(targ.Target);
            this_.Think = PathNext;
            this_.NextThink = MapMover.Now() + ThinkInterval; // QC: this.nextthink = time + 0.1;
        }

        /// <summary>
        /// Port of <c>train_next</c> (train.qc:83-135) specialised for a dynlight: move to the next path_corner
        /// at .speed, then <see cref="PathWait"/> on arrival. No .view_ofs offset (point light), no TRAIN_CURVE
        /// bezier and no TRAIN_TURN/NEEDACTIVATION (a dynlight exposes none of those keys).
        /// </summary>
        private static void PathNext(Entity this_)
        {
            Entity? targ = this_.FutureTarget;
            if (targ is null)
                return;

            this_.Target = targ.Target;                                  // QC: this.target = targ.target;
            this_.FutureTarget = MapMover.FindFirstByTargetName(targ.Target); // QC: train_next_find(targ)
            this_.GoalEntity = targ;                                     // remember the corner we are heading to
            this_.Wait = targ.Wait != 0f ? targ.Wait : 0.1f;            // QC: this.wait = targ.wait; if(!) 0.1;

            float speed = targ.Speed != 0f ? targ.Speed : this_.Speed;  // QC: per-corner .speed else this.speed
            MapMover.CalcMove(this_, targ.Origin, MapMover.SpeedType.Linear, speed, PathWait);
        }

        /// <summary>
        /// Port of <c>train_wait</c> (train.qc:8-64) specialised for a dynlight: fire the arrived corner's
        /// targets, then advance to the next leg — immediately if .wait &lt; 0, else after the dwell.
        /// </summary>
        private static void PathWait(Entity this_)
        {
            Entity? corner = this_.GoalEntity;
            if (corner is not null)
                MapMover.UseTargets(corner, null, null);  // QC: SUB_UseTargets(this.enemy/corner, ...)
            this_.GoalEntity = null;

            if (this_.Wait < 0f)                          // QC: if(this.wait < 0) train_next(this);
            {
                PathNext(this_);
            }
            else
            {
                this_.Think = PathNext;                   // QC: setthink(this, train_next);
                this_.NextThink = MapMover.Now() + this_.Wait; // QC: this.nextthink = this.ltime + this.wait;
            }
        }

        /// <summary>Port of <c>dynlight_find_target</c> (dynlight.qc:74-85): attach to a TAG on the target model.</summary>
        private static void FindTarget(Entity this_)
        {
            if (string.IsNullOrEmpty(this_.Target))
                return;

            Entity? targ = MapMover.FindFirstByTargetName(this_.Target);
            if (targ is null)
                return;

            if (Api.Services is not null)
                Api.Models.SetAttachment(this_, targ, this_.DTagName); // QC: setattachment(this, targ, this.dtagname);
            this_.Owner = targ;                      // QC: this.owner = targ;
            this_.Think = DynlightThink;
            this_.NextThink = MapMover.Now() + ThinkInterval;
        }

        /// <summary>Port of <c>dynlight_think</c> (dynlight.qc:39-45): die if the owner went away; else reschedule.</summary>
        private static void DynlightThink(Entity this_)
        {
            if (this_.Owner is null)                 // QC: if(!this.owner) delete(this);
                MapMover.RemoveEntity(this_);
            else
                this_.NextThink = MapMover.Now() + ThinkInterval;
        }

        /// <summary>Port of <c>dynlight_use</c> (dynlight.qc:86-94): toggle the light radius on/off when triggered.</summary>
        private static void DynlightUse(Entity this_, Entity actor)
        {
            if (this_.Active != MapMover.ActiveActive) // QC: if(this.active != ACTIVE_ACTIVE) return;
                return;
            this_.LightLev = this_.LightLev == 0f ? this_.LightLefty : 0f;
        }

        /// <summary>Port of <c>dynlight_setactive</c> (dynlight.qc:95-105).</summary>
        private static void DynlightSetActive(Entity this_, int act)
        {
            int old = this_.Active;
            if (act == MapMover.ActiveToggle)
                this_.Active = this_.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
            else
                this_.Active = act;

            if (this_.Active != old)
                this_.LightLev = this_.Active == MapMover.ActiveActive ? this_.LightLefty : 0f;
        }

        /// <summary>Port of <c>dynlight_reset</c> (dynlight.qc:106-111): re-arm on round restart.</summary>
        private static void DynlightReset(Entity this_)
        {
            this_.Active = MapMover.ActiveActive;
            this_.LightLev = this_.LightLefty;
        }
    }
}
