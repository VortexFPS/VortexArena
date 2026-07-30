// Port of the shared map-object infrastructure from
//   qcsrc/common/mapobjects/subs.qc      (SUB_CalcMove / SUB_CalcAngleMove / InitTrigger / SetMovedir)
//   qcsrc/common/mapobjects/triggers.qc  (SUB_UseTargets / isPushable / DelayThink)
//   qcsrc/common/mapobjects/platforms.qc (generic_plat_blocked)
//   qcsrc/common/mapobjects/defs.qh      (STATE_* / ACTIVE_* / spawnflag bits)
//
// In QuakeC a moving brush entity (MOVETYPE_PUSH) is animated by setting `.velocity` toward a
// destination and scheduling a think (`.nextthink`, measured against the entity's local time `.ltime`)
// that snaps it to the exact destination and fires the follow-up. The engine's PushMove integrator does
// the actual sweeping/blocking. This file reproduces that driver headlessly: SUB_CalcMove sets
// Velocity + a Think over the mover's local clock; the deterministic simulation (XonoticGodot.Engine) runs the
// MOVETYPE_PUSH sweep exactly as DP/QC did (specs/determinism-and-physics.md).
//
// QC's flat-field movers carried a pile of extra fields (.pos1/.pos2/.state/.finaldest/.think1/...).
// Entity is `partial`, so — like Items/EntityResources.cs — we add those amounts here in a NEW file
// without editing Entity.cs.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    /// <summary>
    /// The extra entity fields the QuakeC map-objects (mapobjects/*) stored as flat <c>.field</c>s on the
    /// edict. Promoted here onto the partial <see cref="Entity"/> (ADR-0007) so doors/plats/buttons/trains
    /// and the triggers can keep their state without a side table. Engine fields stay out of this file.
    /// </summary>
    public partial class Entity
    {
        // --- mover geometry / motion (QC .pos1 .pos2 .mangle .finaldest .finalangle .movedir .dest) ---
        public Vector3 Pos1;          // QC .pos1 — closed/down/start position
        public Vector3 Pos2;          // QC .pos2 — open/up/end position
        public Vector3 MAngle;        // QC .mangle — stored angles for plats/teleport dests (angles cleared on init)
        public Vector3 FinalDest;     // QC .finaldest — SUB_CalcMove target snapped to on arrival
        public Vector3 FinalAngle;    // QC .finalangle — SUB_CalcAngleMove target
        public Vector3 MoveDir;       // QC .movedir — normalized motion direction (from .angles / "angle")
        public Vector3 DestVec;       // QC .destvec — bobbing center / generic stored vector

        // --- mover scalars (QC .speed .lip .wait .height .dmg .dmgtime .dmgtime2 .state .count .cnt) ---
        public float Speed;           // QC .speed
        public float Lip;             // QC .lip
        public float Wait;            // QC .wait
        public float Height;          // QC .height — plat/jumppad/bobbing travel
        public float Dmg;             // QC .dmg — crush/blocked damage
        // NOTE: named CrushInterval/CrushNextTime (not DmgTime) because the vehicle partial
        // (Vehicles/VehicleCommon.cs) already promotes QC .dmg_time onto Entity as DmgTime.
        public float CrushInterval;   // QC .dmgtime — min interval between crush hits
        public float CrushNextTime;   // QC .dmgtime2 — next allowed crush-hit time
        public int MoverState;        // QC .state — STATE_TOP/BOTTOM/UP/DOWN (avoid clashing with player state)
        public int Cnt;              // QC .cnt as an int — dest weight / door distance / particle effect id
        public float MoverCnt;        // QC .cnt as a float — bobbing timescale (360/speed) / pendulum rest roll
        public float Phase;           // QC .phase — bobbing/pendulum phase offset
        public float Freq;            // QC .freq — pendulum frequency
        public float MaxHealthMover;  // QC .max_health for movers (door/button/breakable) — kept distinct from player MaxHealth
        public int CounterCnt;        // QC .counter_cnt — trigger_counter progress
        public float RespawnTimeMover;// QC .respawntime — breakable/counter re-enable delay

        // --- targeting (QC .target/.target2/.target3/.target4 .targetname .killtarget .delay) ---
        // Target2 is the shared field on Entity.cs; Target3/Target4 are mapobject-only extras:
        public string Target3 = "";   // QC .target3
        public string Target4 = "";   // QC .target4
        public string KillTarget = "";// QC .killtarget
        public float Delay;           // QC .delay — SUB_UseTargets delayed fire

        // --- activation (QC .active + the chain links doors use) ---
        public int Active = MapMover.ActiveActive; // QC .active (ACTIVE_*)

        // --- sounds (QC .noise .noise1 .noise2 .noise3 .volume .atten) ---
        public string Noise = "";     // QC .noise — moving / trigger sound
        public string Noise1 = "";    // QC .noise1 — stop sound
        public string Noise2 = "";    // QC .noise2 — start/move sound
        public float Volume;          // QC .volume — sound volume (0-1, default set by spawnfunc)
        public float Atten;           // QC .atten — sound attenuation (0 = global, higher = tighter radius)

        // --- legacy plat sound overrides (QC .sound1 .sound2 — backwards-compat for old maps, plat.qc) ---
        // QC plat_spawn: `if (this.sound1) this.noise = this.sound1; if (this.sound2) this.noise1 = this.sound2;`
        // ("backwards compatibility because people don't use already existing fields"). The `sounds` selector
        // itself reuses the existing Entity.Sounds (EntityMapObjectStateExtra.cs).
        public string Sound1 = "";    // QC .sound1 — legacy plat move-sound override
        public string Sound2 = "";    // QC .sound2 — legacy plat stop-sound override

        // --- per-corner / per-mover ease curve string (QC .platmovetype — "start end [force]") ---
        // Parsed by SUB_SetPlatMoveType into PlatMoveStart/PlatMoveEnd (path_corner, func_train, func_plat).
        public string Platmovetype = ""; // QC .platmovetype — raw spawn-key string ("" = default linear)

        // NOTE: QC .pushltime (jumppad/teleport effect debounce) is promoted on the partial Entity by the
        // damage pipeline (Damage/DamageEntityState.cs PushLTime) — it is the SAME QC field reused for the
        // credited-attacker window; we share it rather than redeclare it here.

        // --- per-toucher hurt/heal throttles (QC .triggerhurttime / .triggerhealtime) ---
        public float TriggerHurtTime; // QC .triggerhurttime — next allowed trigger_hurt hit on this toucher
        public float TriggerHealTime; // QC .triggerhealtime — next allowed trigger_heal tick on this toucher

        // --- teleport bookkeeping (QC .lastteleporttime / .lastteleport_origin) ---
        public float LastTeleportTime;   // QC .lastteleporttime
        public Vector3 LastTeleportOrigin; // QC .lastteleport_origin

        // --- forced view set (QC .fixangle) ---
        // QC sets player.fixangle = true on teleport/respawn so the engine snaps the client's VIEW to the new
        // facing (the CSQC side does setproperty(VF_CL_VIEWANGLES)). The client predictor sets these on the
        // local prediction carrier when it predicts a teleport; the net host reads FixAngle after a prediction
        // tick to snap its accumulated view angles to FixAngleAngles (the destination mangle). See
        // TriggerTouch.PredictTeleportsAmbient + NetGame's per-tick view-snap.
        public bool FixAngle;            // QC .fixangle — the view was forcibly set this frame
        public Vector3 FixAngleAngles;   // the angles to snap the view to (destination mangle)

        // --- think1: the follow-up SUB_CalcMove calls after it snaps to the destination (QC .think1) ---
        public EntityThink? Think1;   // QC .think1

        // --- SUB_CalcMove bezier controller (QC .move_controller + the controller's .destvec/.destvec2/...) ---
        public Entity? MoveController;     // QC .move_controller — the easing sub-entity driving this mover
        public Vector3 DestVec2;           // QC .destvec2 — bezier quadratic term (controller only)
        public Vector3 FinalDestCtl;       // QC controller .finaldest — overshoot target
        public float AnimStartTime;        // QC .animstate_starttime — bezier start (controller only)
        public float AnimEndTime;          // QC .animstate_endtime — bezier end (controller only)
        // QC .platmovetype_start / .platmovetype_end are uninitialized floats — default 0. With 0/0 the
        // (start==1 && end==1) linear shortcut in SUB_CalcMove FAILS, so a long (>=0.15s) mover runs the
        // bezier branch with cubic_speedfunc(0,0,t) = -2t^3+3t^2 (smoothstep ease-in-out). An explicit
        // "platmovetype" key (set_platmovetype) or "1 1" selects the linear branch. Matching Base's default
        // here is what makes every stock door/plat/train S-curve eased rather than constant-velocity linear.
        // QC .float platmovetype_start/_end — fractional ease factors ARE valid (cubic_speedfunc_is_sane
        // documents (0.5, [0..3.8]), (1.5, [0..3.9]) etc.), so these must be float, not int — stof() in
        // set_platmovetype yields the raw float and feeds cubic_speedfunc directly.
        public float PlatMoveStart;        // QC .platmovetype_start — ease curve factor (0 = smoothstep default)
        public float PlatMoveEnd;          // QC .platmovetype_end   — ease curve factor (0 = smoothstep default)
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Shared helpers for the BSP map-object families (doors, plats, buttons, trains, triggers). The
    /// Godot-free port of the common bits of <c>subs.qc</c> / <c>triggers.qc</c> / <c>platforms.qc</c>:
    /// the SUB_CalcMove mover driver (linear branch + the cubic_speedfunc bezier controller), SUB_UseTargets
    /// target firing, the targetname index, FindConnectedComponent, and the state/active constants.
    ///
    /// Still genuinely client/networking-only (not gameplay state): warpzones and the CSQC trigger
    /// networking (trigger_common_write/read).
    /// </summary>
    public static class MapMover
    {
        // ---- QC defs.qh STATE_* ----
        public const int StateTop = 0;
        public const int StateBottom = 1;
        public const int StateUp = 2;
        public const int StateDown = 3;

        // ---- QC defs.qh ACTIVE_* ----
        public const int ActiveNot = 0;
        public const int ActiveActive = 1;
        public const int ActiveIdle = 2;
        public const int ActiveToggle = 3;

        // ---- QC defs.qh shared spawnflag bits (BIT(n)) ----
        public const int SpawnStartEnabled = 1 << 0;   // START_ENABLED / START_DISABLED
        public const int SpawnAllEntities = 1 << 1;    // ALL_ENTITIES / ON_MAPLOAD
        public const int SpawnInvertTeams = 1 << 2;    // INVERT_TEAMS
        public const int SpawnCrush = 1 << 2;          // CRUSH (alias of INVERT_TEAMS bit)
        public const int SpawnNoSplash = 1 << 8;       // NOSPLASH
        public const int SpawnOnlyPlayers = 1 << 14;   // ONLY_PLAYERS
        public const int SpawnNoMessage = 1 << 0;      // SPAWNFLAG_NOMESSAGE
        public const int SpawnNoTouch = 1 << 0;        // SPAWNFLAG_NOTOUCH

        /// <summary>QC TSPEED_* selector for SUB_CalcMove (we implement LINEAR + TIME; START/END collapse to LINEAR here).</summary>
        public enum SpeedType { Start, End, Linear, Time }

        /// <summary>
        /// QC threads a third <c>trigger</c> argument to every <c>.use</c> (<c>t.use(t, actor, this)</c> in
        /// SUB_UseTargets) — the entity that fired the target. The port's <see cref="Framework.EntityUse"/>
        /// delegate is 2-arg, so to keep the 78 .use sites unchanged we stash the firing trigger here for the
        /// duration of the dispatch. Only <c>door_use</c> reads it (the BIDIR rotating-door reverse path); every
        /// other path leaves it null, matching QC's <c>NULL</c> trigger on touch/damage/blocked opens.
        /// </summary>
        public static Framework.Entity? CurrentUseTrigger;

        /// <summary>
        /// QC <c>iscreature</c>: players and monsters (but never vehicles/turrets). In QC <c>.iscreature</c>
        /// is set true on the player and monster spawn paths and left false on everything else, including the
        /// vehicle/turret entities — which in this entity model carry neither <see cref="EntFlags.Client"/>
        /// nor <see cref="EntFlags.Monster"/>, so the flag test is the faithful equivalent.
        /// </summary>
        public static bool IsCreature(Entity e)
            => (e.Flags & (EntFlags.Client | EntFlags.Monster)) != 0;

        /// <summary>QC IS_DEAD(e).</summary>
        public static bool IsDead(Entity e) => e.DeadState != DeadFlag.No;

        /// <summary>
        /// QC <c>isPushable(e)</c> (triggers.qc): who jumppads/conveyors/impulse-fields move. Players and
        /// monsters, dropped loot items and dead bodies, and projectiles (anything with a projectile
        /// deathtype); vehicles and antilagged "bullet" tracers are excluded.
        /// </summary>
        public static bool IsPushable(Entity e)
        {
            // QC isPushable (triggers.qc:5): `if (e.pushable) return true;` is checked FIRST, before the
            // IS_VEHICLE exclusion — so an entity that opts in (the spiderbot's vr_setup sets pushable=true)
            // rides jumppads/conveyors despite being a vehicle.
            if (e.PushableFlag)
                return true;
            // QC IS_VEHICLE(e) -> false. Vehicles carry neither Client nor Monster here.
            if (e.ClassName is "vehicle" || e.ClassName.StartsWith("vehicle_", System.StringComparison.Ordinal))
                return false;
            if (IsCreature(e))
                return true;
            // QC ITEM_IS_LOOT(e): a dropped pickup. Loot items are flagged EntFlags.Item with the "item" class.
            if ((e.Flags & EntFlags.Item) != 0)
                return true;
            if (e.ClassName == "bullet") // antilagged bullets can't be pushed (QC)
                return false;
            if (e.ClassName == "body")   // dead-body ragdoll
                return true;
            // QC: e.projectiledeathtype — a live projectile. Projectiles set EntFlags.Item (see weapons) or
            // carry one of these short classnames the weapon code spawns.
            return e.ClassName is "rocket" or "grenade" or "plasma" or "missile"
                or "casing" or "spike" or "electro_bolt" or "hagarbomb" or "mortargrenade";
        }

        // ====================================================================
        //  Targeting — SUB_UseTargets / FindByTargetName / killtarget
        // ====================================================================

        // ----- targetname index (replaces the union-of-classnames scan with an O(1) lookup) -----
        //
        // QC resolves targetname links with find(world, targetname, s), an engine walk of every edict. The
        // headless facade has no all-entities iterator, so we keep our own index: targetname -> entities.
        // Every map-object setup calls IndexRegister(this); the index is also kept honest when a map object
        // renames its .target/.targetname at runtime (trains advance .target each leg) — those are .target,
        // not .targetname, so they don't invalidate the index. Renames of .targetname are rare (LinkDoors
        // rewrites linked doors' targetname) and go through ReindexTargetName below.

        private static readonly Dictionary<string, List<Entity>> _byTargetName = new(StringComparer.Ordinal);
        private static readonly Dictionary<Entity, string> _indexedName = new();

        /// <summary>Drop the whole index (QC implicitly clears it on map (re)load).</summary>
        public static void ClearIndex()
        {
            _byTargetName.Clear();
            _indexedName.Clear();
        }

        /// <summary>
        /// Add (or refresh) <paramref name="e"/> in the targetname index under its current
        /// <see cref="Entity.TargetName"/>. Idempotent; called by every map-object spawnfunc and by any code
        /// that assigns a new targetname. Entities with an empty targetname are simply removed from the index.
        /// </summary>
        public static void IndexRegister(Entity e)
        {
            if (_indexedName.TryGetValue(e, out string? prev))
            {
                if (prev == e.TargetName)
                    return; // unchanged
                RemoveFromBucket(prev, e);
            }

            if (string.IsNullOrEmpty(e.TargetName))
            {
                _indexedName.Remove(e);
                return;
            }

            if (!_byTargetName.TryGetValue(e.TargetName, out List<Entity>? bucket))
                _byTargetName[e.TargetName] = bucket = new List<Entity>();
            if (!bucket.Contains(e))
                bucket.Add(e);
            _indexedName[e] = e.TargetName;
        }

        /// <summary>Assign a new targetname and keep the index in sync (QC sets <c>.targetname = …</c>).</summary>
        public static void SetTargetName(Entity e, string name)
        {
            e.TargetName = name ?? "";
            IndexRegister(e);
        }

        /// <summary>Drop <paramref name="e"/> from the index entirely (call when removing/freeing it).</summary>
        public static void IndexRemoveEntity(Entity e)
        {
            if (_indexedName.TryGetValue(e, out string? prev))
            {
                RemoveFromBucket(prev, e);
                _indexedName.Remove(e);
            }
        }

        private static void RemoveFromBucket(string name, Entity e)
        {
            if (_byTargetName.TryGetValue(name, out List<Entity>? bucket))
            {
                bucket.Remove(e);
                if (bucket.Count == 0)
                    _byTargetName.Remove(name);
            }
        }

        /// <summary>
        /// Find every entity whose <see cref="Entity.TargetName"/> equals <paramref name="name"/> (QC
        /// <c>find(world, targetname, s)</c>). O(1) via the targetname index; freed/renamed entries are
        /// filtered out lazily. Returns an empty sequence for null/empty names.
        /// </summary>
        public static IEnumerable<Entity> FindByTargetName(string? name)
        {
            if (string.IsNullOrEmpty(name))
                yield break;

            if (_byTargetName.TryGetValue(name, out List<Entity>? bucket))
            {
                // Snapshot: callers' .use can spawn/remove, mutating the bucket mid-iteration.
                foreach (Entity e in bucket.ToArray())
                {
                    if (e.IsFreed) { IndexRemoveEntity(e); continue; }
                    if (e.TargetName != name) { IndexRegister(e); continue; } // stale rename
                    yield return e;
                }
            }

            // Players / projectiles can carry a targetname too but are spawned outside this module, so they
            // are not in the index — pick them up via the facade. (Rare; QC's find() saw them naturally.)
            if (Api.Services is not null)
            {
                foreach (string cls in NonMapTargetableClassNames)
                    foreach (Entity e in Api.Entities.FindByClass(cls))
                        if (!e.IsFreed && e.TargetName == name && !_indexedName.ContainsKey(e))
                            yield return e;
            }
        }

        /// <summary>First entity with the given targetname, or null (QC <c>find(NULL, targetname, s)</c>).</summary>
        public static Entity? FindFirstByTargetName(string? name)
        {
            foreach (Entity e in FindByTargetName(name))
                return e;
            return null;
        }

        /// <summary>Classnames the index does NOT own but that can still be a targetname destination.</summary>
        private static readonly string[] NonMapTargetableClassNames = { "player", "info_notnull" };

        /// <summary>
        /// QC <c>FOREACH_ENTITY_STRING(target, name, …)</c> restricted to teleporter classnames: every
        /// <c>trigger_teleport</c> / <c>target_teleporter</c> whose <c>.target</c> equals <paramref name="name"/>.
        /// Used by <c>target_teleporter_checktarget</c> to decide whether a target-less target_teleporter is in
        /// fact a teleport destination (something teleports TO it). Scans only the two teleporter classes — the
        /// only classnames the disambiguation inspects — since the facade has no global entity enumerator.
        /// </summary>
        public static IEnumerable<Entity> FindEntitiesTargeting(string? name)
        {
            if (string.IsNullOrEmpty(name) || Api.Services is null)
                yield break;
            foreach (string cls in new[] { "trigger_teleport", "target_teleporter" })
                foreach (Entity e in Api.Entities.FindByClass(cls))
                    if (!e.IsFreed && e.Target == name)
                        yield return e;
        }

        /// <summary>
        /// Every spawned entity of <paramref name="className"/> (QC <c>find(world, classname, s)</c>). Used by
        /// LinkDoors to walk the door set. Goes through the facade; freed entities are skipped.
        /// </summary>
        public static IEnumerable<Entity> AllByClass(string className)
        {
            if (Api.Services is null)
                yield break;
            foreach (Entity e in Api.Entities.FindByClass(className))
                if (!e.IsFreed)
                    yield return e;
        }

        /// <summary>QC bit flags for the <paramref name="skipTargets"/> mask of <see cref="UseTargetsEx"/>.</summary>
        public const int SkipTarget1 = 1 << 1; // QC BIT(1) skips .target
        public const int SkipTarget2 = 1 << 2; // QC BIT(2) skips .target2
        public const int SkipTarget3 = 1 << 3; // QC BIT(3) skips .target3
        public const int SkipTarget4 = 1 << 4; // QC BIT(4) skips .target4

        /// <summary>Port of <c>SUB_UseTargets</c> (the common, reuse-allowed, all-targets variant).</summary>
        public static void UseTargets(Entity self, Entity? actor, Entity? trigger)
            => UseTargetsEx(self, actor, trigger, preventReuse: false, skipTargets: 0);

        /// <summary>Port of <c>SUB_UseTargets_PreventReuse</c>: each target's <c>.use</c> fires at most once per frame.</summary>
        public static void UseTargetsPreventReuse(Entity self, Entity? actor, Entity? trigger)
            => UseTargetsEx(self, actor, trigger, preventReuse: true, skipTargets: 0);

        /// <summary>
        /// Faithful port of <c>SUB_UseTargets_Ex</c> (triggers.qc): play the activator's talk sound + print
        /// the message, remove killtargets, then fire the <c>.use</c> of every entity matching
        /// target/target2/target3/target4 — or, when <see cref="Entity.TargetRandom"/> is set, exactly one of
        /// them chosen by weighted random. If <c>.delay</c> is set, the whole fire is deferred via a DelayedUse
        /// think entity (QC DelayThink). <paramref name="preventReuse"/> latches each target's last-fire frame;
        /// <paramref name="skipTargets"/> is the QC bitmask (<see cref="SkipTarget1"/>…) that suppresses slots.
        /// </summary>
        public static void UseTargetsEx(Entity self, Entity? actor, Entity? trigger, bool preventReuse, int skipTargets)
        {
            // --- delay: defer the whole fire via a throwaway think entity (QC DelayThink) ---
            if (self.Delay > 0f && Api.Services is not null)
            {
                Entity t = Api.Entities.Spawn();
                t.ClassName = "DelayedUse";
                t.NextThink = Api.Clock.Time + self.Delay;
                t.Enemy = actor;
                t.Message = self.Message;
                t.KillTarget = self.KillTarget;
                t.TargetRandom = self.TargetRandom;
                t.Target = (skipTargets & SkipTarget1) == 0 ? self.Target : "";
                t.Target2 = (skipTargets & SkipTarget2) == 0 ? self.Target2 : "";
                t.Target3 = (skipTargets & SkipTarget3) == 0 ? self.Target3 : "";
                t.Target4 = (skipTargets & SkipTarget4) == 0 ? self.Target4 : "";
                t.AntiwallFlag = self.AntiwallFlag; // relay the func_clientwall toggle through the delay (triggers.qc:266)
                t.Think = DelayThink;
                IndexRegister(t);
                return;
            }

            // --- message: QC centerprints to a real client + plays the talk sound when no custom noise ---
            // (triggers.qc SUB_UseTargets: centerprint(activator, this.message); if (this.noise == "")
            // play2(activator, SND(TALK))). The centerprint TEXT is routed to the activator via the raw-centerprint
            // notification channel (→ CenterPrintPanel.Add); the audible half plays play2(actor, SND(TALK)).
            if (actor is not null && (actor.Flags & EntFlags.Client) != 0 && !string.IsNullOrEmpty(self.Message))
            {
                MapMover.Centerprint(actor, self.Message);
                if (string.IsNullOrEmpty(self.Noise))
                    Play2(actor, "misc/talk.wav"); // QC triggers.qc:282 play2(actor, SND(TALK)) — 2D VOL_BASE/ATTEN_NONE
            }

            // --- killtargets: delete everything they name (QC) ---
            if (!string.IsNullOrEmpty(self.KillTarget))
            {
                foreach (Entity k in FindByTargetName(self.KillTarget).ToList())
                    RemoveEntity(k);
            }

            // --- fire targets (target_random: collect, then fire exactly one by weight) ---
            Entity? chosen = null;
            float chosenWeightSum = 0f;
            float now = Now();

            for (int i = 0; i < 4; i++)
            {
                int skipBit = 1 << (i + 1);
                if ((skipTargets & skipBit) != 0)
                    continue;
                string s = i switch { 0 => self.Target, 1 => self.Target2, 2 => self.Target3, _ => self.Target4 };
                if (string.IsNullOrEmpty(s))
                    continue;

                foreach (Entity t in FindByTargetName(s).ToList())
                {
                    if (ReferenceEquals(t, self) || t.Use is null)
                        continue;
                    if (preventReuse && t.SubTargetUsed == now)
                        continue;

                    if (self.TargetRandom)
                    {
                        // QC RandomSelection_AddEnt(t, 1, 0): each candidate weight 1, reservoir pick.
                        chosenWeightSum += 1f;
                        if (Prandom.Float() * chosenWeightSum <= 1f)
                            chosen = t;
                    }
                    else
                    {
                        // QC: t.use(t, actor, this) — `this` (self) is the firing trigger. Stash it so a
                        // BIDIR rotating door can read .trigger_reverse off it (door_use), then restore.
                        Entity? savedTrigger = MapMover.CurrentUseTrigger;
                        MapMover.CurrentUseTrigger = self;
                        try { t.Use(t, actor ?? self); }
                        finally { MapMover.CurrentUseTrigger = savedTrigger; }
                        if (preventReuse)
                            t.SubTargetUsed = now;
                    }
                }
            }

            if (self.TargetRandom && chosen is not null)
            {
                Entity? savedTrigger = MapMover.CurrentUseTrigger;
                MapMover.CurrentUseTrigger = self;
                try { chosen.Use!(chosen, actor ?? self); }
                finally { MapMover.CurrentUseTrigger = savedTrigger; }
                if (preventReuse)
                    chosen.SubTargetUsed = now;
            }
        }

        /// <summary>QC <c>DelayThink</c>: the deferred SUB_UseTargets fired by a DelayedUse entity, then it removes itself.</summary>
        private static void DelayThink(Entity self)
        {
            // self carries the captured target list + activator (self.Enemy).
            self.Delay = 0f; // already delayed; fire immediately now
            UseTargets(self, self.Enemy, null);
            RemoveEntity(self);
        }

        /// <summary>Remove an entity through the facade AND drop it from the targetname index (QC delete()).</summary>
        public static void RemoveEntity(Entity e)
        {
            IndexRemoveEntity(e);
            if (Api.Services is not null)
                Api.Entities.Remove(e);
            else
                e.IsFreed = true;
        }

        // ====================================================================
        //  SetMovedir / InitTrigger
        // ====================================================================

        /// <summary>
        /// Port of <c>SetMovedir</c> (subs.qc): if a movedir was given use it normalized, otherwise derive
        /// it from the "angle"/angles via makevectors forward; then clear angles. Doors/buttons/jumppads use
        /// the result for their travel direction.
        /// </summary>
        public static void SetMovedir(Entity e)
        {
            if (e.MoveDir != Vector3.Zero)
            {
                e.MoveDir = QMath.Normalize(e.MoveDir);
            }
            else
            {
                e.MoveDir = QMath.Forward(e.Angles);
            }
            e.Angles = Vector3.Zero;
        }

        /// <summary>
        /// Port of <c>InitTrigger</c> (subs.qc): SetMovedir, mark it a trigger volume (SOLID_TRIGGER,
        /// MOVETYPE_NONE), and — via QC's <c>SetBrushEntityModel</c> — pull the volume's bbox from its inline
        /// brush model. Map triggers reference their brush as <c>"model" "*N"</c>; <c>setmodel("*N")</c> is what
        /// copies that brush's mins/maxs onto the edict (and links AbsMin/AbsMax) so the touch-area-grid test
        /// has a real volume to overlap. Without it an inline-model trigger is zero-size and never fires — which
        /// is exactly what kept jump-pads / teleporters / hurt volumes silent. A trigger that instead carries an
        /// explicit hand-set bbox (no model) keeps its mins/maxs as given.
        /// </summary>
        public static void InitTrigger(Entity e)
        {
            SetMovedir(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.None;
            // QC SetBrushEntityModel: resolve the inline brush model's bounds (mins/maxs) + relink. Only when a
            // model is present and a facade is installed; a bare bbox trigger (test/headless) is left untouched.
            if (Api.Services is not null && !string.IsNullOrEmpty(e.Model))
                Api.Entities.SetModel(e, e.Model);
        }

        /// <summary>
        /// Port of <c>InitMovingBrushTrigger</c> (subs.qc): a solid, MOVETYPE_PUSH brush model (doors,
        /// plats, trains, rotators). Returns false if it has no brushes (no model) like the QC objerror path.
        /// </summary>
        public static bool InitMovingBrushTrigger(Entity e)
        {
            e.Solid = Solid.Bsp;
            e.MoveType = MoveType.Push;
            if (Api.Services is not null && !string.IsNullOrEmpty(e.Model))
                Api.Entities.SetModel(e, e.Model);
            return true;
        }

        // ====================================================================
        //  SUB_CalcMove — the mover driver (linear branch + bezier easing controller)
        // ====================================================================

        /// <summary>
        /// Port of <c>SUB_CalcMove</c> (subs.qc): drive a mover from its origin to <paramref name="dest"/> at
        /// <paramref name="speed"/>, running <paramref name="onArrive"/> when it lands. Short or explicitly
        /// linear movers (<c>platmovetype 1 1</c>) use the straight delta*(1/traveltime) velocity; longer
        /// eased movers run through the <see cref="CalcMoveControllerThink"/> bezier controller, exactly as QC
        /// switches between them. Movers schedule against their local clock <see cref="Entity.LTime"/>.
        /// </summary>
        public static void CalcMove(Entity e, Vector3 dest, SpeedType speedType, float speed, EntityThink onArrive)
        {
            e.Think1 = onArrive;
            e.FinalDest = dest;
            e.Think = CalcMoveDone;

            if (dest == e.Origin)
            {
                e.Velocity = Vector3.Zero;
                e.NextThink = e.LTime + 0.1f;
                return;
            }

            Vector3 delta = dest - e.Origin;
            float travelTime = speedType == SpeedType.Time ? speed : QMath.VLen(delta) / speed;

            // Q3/DP fallback for non-positive traveltime.
            if (travelTime <= 0f)
                travelTime = 0.001f;

            // QC: very short animations, or an explicitly-linear platmovetype, use plain linear motion.
            if (travelTime < 0.15f || (e.PlatMoveStart == 1 && e.PlatMoveEnd == 1))
            {
                e.Velocity = delta * (1f / travelTime);   // QC: delta * (1/traveltime)
                e.NextThink = e.LTime + travelTime;
                return;
            }

            // Otherwise run it like a bezier curve through the midpoint control (QC SUB_CalcMove fallthrough).
            CalcMoveBezier(e, (e.Origin + dest) * 0.5f, dest, speedType, speed, onArrive);
        }

        /// <summary>
        /// Port of <c>SUB_CalcMove_Bezier</c> (subs.qc): spawn a controller sub-entity that re-evaluates the
        /// quadratic bezier (org -&gt; control -&gt; dest) every physics frame through
        /// <see cref="CalcMoveControllerThink"/> with cubic_speedfunc easing, driving the mover's velocity (and,
        /// for a TRAIN_TURN train, its angular velocity to face along the curve). Used by func_train curves.
        /// </summary>
        public static void CalcMoveBezier(Entity e, Vector3 control, Vector3 dest, SpeedType speedType, float speed, EntityThink onArrive)
        {
            e.Think1 = onArrive;
            e.FinalDest = dest;
            e.Think = CalcMoveDone;

            float travelTime = speedType switch
            {
                SpeedType.Start => 2f * QMath.VLen(control - e.Origin) / speed,
                SpeedType.End   => 2f * QMath.VLen(control - dest)     / speed,
                SpeedType.Time  => speed,
                _               => QMath.VLen(dest - e.Origin)         / speed, // Linear
            };

            if (travelTime < 0.1f)
            {
                e.Velocity = Vector3.Zero;
                e.NextThink = e.LTime + 0.1f;
                return;
            }

            // Replace any in-flight controller so changing target midway isn't glitchy (QC delete()).
            if (e.MoveController is not null)
            {
                RemoveEntity(e.MoveController);
                e.MoveController = null;
            }

            Entity ctl = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
            ctl.ClassName = "SUB_CalcMove_controller";
            ctl.MoveType = MoveType.None;
            ctl.Owner = e;
            e.MoveController = ctl;
            ctl.PlatMoveStart = e.PlatMoveStart;
            ctl.PlatMoveEnd = e.PlatMoveEnd;

            // setbezier(controller, org, control, dest): destvec = 2*control, destvec2 = dest - 2*control,
            // all relative to org (QC SUB_CalcMove_controller_setbezier).
            Vector3 c = control - e.Origin;
            Vector3 d = dest - e.Origin;
            ctl.Origin = e.Origin;
            ctl.DestVec = 2f * c;
            ctl.DestVec2 = d - 2f * c;
            ctl.FinalDestCtl = dest + new Vector3(0f, 0f, 0.125f); // slight overshoot
            ctl.AnimStartTime = Now();
            ctl.AnimEndTime = Now() + travelTime;
            ctl.Think = CalcMoveControllerThink;
            ctl.Think1 = e.Think; // the mover's own think (SUB_CalcMoveDone), restored on completion

            // The thinking is now done by the controller; park the mover for PushMove.
            e.Think = NullThink;
            e.NextThink = e.LTime + travelTime;

            CalcMoveControllerThink(ctl); // invoke immediately (QC getthink(controller)(controller))
        }

        /// <summary>QC <c>cubic_speedfunc(start, end, spd)</c> — the platmovetype ease curve.</summary>
        public static float CubicSpeedFunc(float startSpeedFactor, float endSpeedFactor, float spd)
            => (((startSpeedFactor + endSpeedFactor - 2f) * spd - 2f * startSpeedFactor - endSpeedFactor + 3f) * spd + startSpeedFactor) * spd;

        /// <summary>QC <c>SUB_NullThink</c>: a do-nothing think (kept so PushMove still simulates the mover).</summary>
        public static void NullThink(Entity e) { }

        /// <summary>
        /// Port of <c>SUB_CalcMove_controller_think</c> (subs.qc): walk the parent along the bezier with
        /// cubic_speedfunc easing, setting its velocity (and avelocity when the parent turns) so it arrives at
        /// the sampled point next physics frame. When the move completes it hands the parent its real think
        /// back, frees itself, and runs that think.
        /// </summary>
        /// <summary>
        /// Port of <c>SUB_CalcMovePause</c> (subs.qc): when an EASED (bezier-controller) mover is blocked but is
        /// NOT going to reverse (a wait&lt;0 / TOGGLE door blocked mid-swing), QC slides the controller's
        /// animstate start/end times forward by the elapsed-since-last-controller-think so the cubic_speedfunc
        /// phase stays put — the mover "pauses" on its easing curve instead of advancing while wedged, then
        /// resumes smoothly when the blocker clears. A linear (no-controller) mover, or one that reverses (which
        /// spawns a fresh controller via <see cref="CalcMoveBezier"/>), doesn't need this and is a no-op here.
        /// </summary>
        public static void CalcMovePause(Entity e)
        {
            Entity? ctl = e.MoveController;
            if (ctl is null || !ReferenceEquals(ctl.Owner, e))
                return; // linear mover (no controller) — nothing to pause

            // QC: animstate_starttime/endtime += time - move_controller.nextthink + sys_frametime. ctl.NextThink
            // is when the controller WOULD next sample; the gap (now - nextthink + frametime) is the paused span,
            // shifting both endpoints keeps phasePos = (nexttick - start)/(end - start) unchanged on resume.
            float pausedSpan = Now() - ctl.NextThink + FrameTime();
            ctl.AnimStartTime += pausedSpan;
            ctl.AnimEndTime += pausedSpan;
        }

        public static void CalcMoveControllerThink(Entity ctl)
        {
            Entity own = ctl.Owner!;
            float dt = FrameTime();
            float now = Now();

            if (now < ctl.AnimEndTime)
            {
                float nextTick = now + dt;
                float travelTime = ctl.AnimEndTime - ctl.AnimStartTime;
                float phasePos = (nextTick - ctl.AnimStartTime) / travelTime;           // [0,1]
                phasePos = CubicSpeedFunc(ctl.PlatMoveStart, ctl.PlatMoveEnd, phasePos);
                Vector3 nextPos = ctl.Origin + ctl.DestVec * phasePos + ctl.DestVec2 * (phasePos * phasePos);

                if (own.PlatMoveTurn)
                {
                    // derivative direction -> face angles (QC: fixedvectoangles = flip pitch, then shortest-path yaw/pitch/roll).
                    Vector3 destAngleVec = ctl.DestVec + 2f * ctl.DestVec2 * phasePos;
                    Vector3 destAngle = QMath.FixedVecToAngles(destAngleVec);

                    Vector3 v = own.Angles;
                    v.X -= 360f * MathF.Floor((v.X - destAngle.X) / 360f + 0.5f);
                    v.Y -= 360f * MathF.Floor((v.Y - destAngle.Y) / 360f + 0.5f);
                    v.Z -= 360f * MathF.Floor((v.Z - destAngle.Z) / 360f + 0.5f);
                    own.Angles = v;
                    own.AVelocity = (destAngle - own.Angles) * (1f / dt);
                }

                Vector3 veloc = nextTick < ctl.AnimEndTime ? nextPos - own.Origin : ctl.FinalDestCtl - own.Origin;
                own.Velocity = veloc * (1f / dt);
                ctl.NextThink = nextTick;
            }
            else
            {
                EntityThink? ownThink = ctl.Think1; // the mover's queued SUB_CalcMoveDone
                own.Think = ownThink;
                own.MoveController = null;
                RemoveEntity(ctl);
                ownThink?.Invoke(own);
            }
        }

        /// <summary>QC <c>SUB_CalcMoveDone</c>: snap to FinalDest, stop, then run the queued think1.</summary>
        public static void CalcMoveDone(Entity e)
        {
            if (Api.Services is not null)
                Api.Entities.SetOrigin(e, e.FinalDest);
            else
                e.Origin = e.FinalDest;
            e.Velocity = Vector3.Zero;
            e.NextThink = -1f;
            EntityThink? next = e.Think1;
            if (next is not null && next != CalcMoveDone)
                next(e);
        }

        /// <summary>
        /// Port of the linear <c>SUB_CalcAngleMove</c> (subs.qc): rotate to <paramref name="destAngle"/> at
        /// <paramref name="speed"/> via <see cref="Entity.AVelocity"/>, snapping on arrival. Used by rotating
        /// doors and turning trains. Shortest-path angle wrap is applied as in QC.
        /// </summary>
        public static void CalcAngleMove(Entity e, Vector3 destAngle, SpeedType speedType, float speed, EntityThink onArrive)
        {
            // shortest distance for the angles (QC subtracts whole turns)
            Vector3 a = e.Angles;
            a.X -= 360f * MathF.Floor((a.X - destAngle.X) / 360f + 0.5f);
            a.Y -= 360f * MathF.Floor((a.Y - destAngle.Y) / 360f + 0.5f);
            a.Z -= 360f * MathF.Floor((a.Z - destAngle.Z) / 360f + 0.5f);
            e.Angles = a;

            Vector3 delta = destAngle - e.Angles;
            float travelTime = speedType == SpeedType.Time ? speed : QMath.VLen(delta) / speed;

            e.Think1 = onArrive;
            e.FinalAngle = destAngle;
            e.Think = CalcAngleMoveDone;

            if (travelTime < 0.1f)
            {
                e.AVelocity = Vector3.Zero;
                e.NextThink = e.LTime + 0.1f;
                return;
            }

            e.AVelocity = delta * (1f / travelTime);
            e.NextThink = e.LTime + travelTime;
        }

        /// <summary>QC <c>SUB_CalcAngleMoveDone</c>: snap to FinalAngle, stop spin, run think1.</summary>
        public static void CalcAngleMoveDone(Entity e)
        {
            e.Angles = e.FinalAngle;
            e.AVelocity = Vector3.Zero;
            e.NextThink = -1f;
            EntityThink? next = e.Think1;
            if (next is not null && next != CalcAngleMoveDone)
                next(e);
        }

        // ====================================================================
        //  Blocked / crush damage (generic_plat_blocked, plat_crush, door_blocked)
        // ====================================================================

        /// <summary>
        /// Port of <c>generic_plat_blocked</c> (platforms.qc): when a mover with <c>.dmg</c> is blocked by a
        /// damageable entity, bite it for <c>.dmg</c> on a <c>.dmgtime</c> cooldown, and gib already-dead
        /// blockers. Used by trains/rotating/bobbing/pendulum as their Blocked handler.
        /// </summary>
        public static void GenericPlatBlocked(Entity self, Entity blocker)
        {
            if (self.Dmg > 0f && blocker.TakeDamage != DamageMode.No)
            {
                if (self.CrushNextTime < Now())
                {
                    Combat.Damage(blocker, self, self, self.Dmg, DeathTypes.Void, blocker.Origin, Vector3.Zero);
                    self.CrushNextTime = Now() + self.CrushInterval;
                }
                if (IsDead(blocker))
                    Combat.Damage(blocker, self, self, 10000f, DeathTypes.Void, blocker.Origin, Vector3.Zero);
            }
        }

        /// <summary>Current sim time, or 0 when running without an installed facade (test/headless construction).</summary>
        public static float Now() => Api.Services is null ? 0f : Api.Clock.Time;

        /// <summary>Frame delta, or a 125fps default when no facade is installed.</summary>
        public static float FrameTime() => Api.Services is null ? 1f / 125f : Api.Clock.FrameTime;

        /// <summary>Play a sound through the facade if both a sample and the facade are present (QC _sound guard).</summary>
        public static void Sound(Entity e, SoundChannel ch, string? sample)
        {
            if (Api.Services is not null && !string.IsNullOrEmpty(sample))
                Api.Sound.Play(e, ch, sample);
        }

        /// <summary>
        /// Faithful QC <c>play2(recipient, sample)</c> (common/sounds/all.qc:116-120) for the toucher-recipient
        /// map-object cues (keylock / keys "unlocked"/"need key" beeps). QC <c>play2</c> is a per-recipient 2D send
        /// at <b>CH_INFO / VOL_BASE 0.7 / ATTEN_NONE 0</b> — a UI-style cue heard at full volume regardless of
        /// distance — NOT a positional emit. The generic <see cref="Sound"/> helper above plays at the facade
        /// default vol=1 / atten=1 (ATTEN_LARGE), so it spatializes the beep; this routes through
        /// <see cref="SoundSystem.Play2Raw"/> so the volume/attenuation match Base byte-for-byte.
        /// (Per-recipient MSG_ONE targeting is still the documented broadcast approximation; this only fixes the mix.)
        /// </summary>
        public static void Play2(Entity recipient, string? sample)
        {
            if (Api.Services is not null && !string.IsNullOrEmpty(sample))
                SoundSystem.Play2Raw(recipient, sample);
        }

        // VOL_BASE / ATTEN_IDLE (common/sounds/sound.qh:32,36) — the looping-ambient volume + attenuation the
        // func_bobbing/pendulum/fourier/vectormamamam soundto(MSG_INIT) ambients use.
        public const float VolBase = 0.7f;
        public const float AttenIdle = 2f;

        /// <summary>
        /// Port of the func_* looping ambient <c>soundto(MSG_INIT, this, CH_TRIGGER_SINGLE, .noise, VOL_BASE,
        /// ATTEN_IDLE, 0)</c> (bobbing/pendulum/fourier; vectormamamam's per-player resend): start a PERSISTENT
        /// loop keyed by <c>(entity, CH_TRIGGER_SINGLE)</c> on the mover. Because the facade keeps the loop as part
        /// of the entity's sound state (idempotent re-emit, replayed to clients via entity state), a late-joining
        /// player still hears an already-running mover's ambient — the headless analogue of QC's MSG_INIT /
        /// <c>init_for_player</c> per-player resend (no separate client roster needed at this layer).
        /// </summary>
        public static void LoopAmbient(Entity e, string? sample)
            => LoopAmbient(e, sample, SoundChannel.Item); // CH_TRIGGER_SINGLE = 3 = SoundChannel.Item.

        /// <summary>
        /// Channel-explicit overload of <see cref="LoopAmbient(Entity,string)"/>: func_rotating's looping ambient
        /// rides <c>CH_AMBIENT_SINGLE</c> (channel 9 = <see cref="SoundChannel.AmbientSingle"/>) rather than
        /// CH_TRIGGER_SINGLE (rotating.qc: <c>_sound(this, CH_AMBIENT_SINGLE, ...)</c>).
        /// </summary>
        public static void LoopAmbient(Entity e, string? sample, SoundChannel channel)
        {
            if (Api.Services is not null && !string.IsNullOrEmpty(sample))
                Api.Sound.Play(e, channel, sample, VolBase, AttenIdle, loop: true);
        }

        /// <summary>Stop the looping ambient started by <see cref="LoopAmbient(Entity,string)"/> (QC <c>stopsound(this,
        /// CH_TRIGGER_SINGLE)</c>) — used by func_vectormamamam's setactive when it goes inactive.</summary>
        public static void StopAmbient(Entity e) => StopAmbient(e, SoundChannel.Item);

        /// <summary>Channel-explicit overload (func_rotating stops its loop on <c>CH_AMBIENT_SINGLE</c>).</summary>
        public static void StopAmbient(Entity e, SoundChannel channel)
        {
            if (Api.Services is not null)
                Api.Sound.Stop(e, channel);
        }

        // ====================================================================
        //  Centerprint seam (QC centerprint(client, s) builtin #73)
        // ====================================================================

        /// <summary>
        /// OPTIONAL extra host hook for map-object free-text centerprints (door <c>.message</c>,
        /// jumppad/secret/trigger <c>.message</c>, target_items, keylock "Unlocked!"). The actual delivery is
        /// already done inside <see cref="Centerprint"/> by pushing the text down the <see cref="MsgType.CenterRaw"/>
        /// notification channel (→ CenterPrintPanel.Add) — the same path chat /tell uses — so the player sees the
        /// message on the live path with no host wiring required. This delegate is fired in addition, for any host
        /// that wants to observe or augment those centerprints (e.g. logging/tests); leave it null otherwise.
        /// </summary>
        public static System.Action<Entity /*client*/, string /*message*/>? CenterprintHandler;

        /// <summary>
        /// Port of the QC <c>centerprint(actor, this.message)</c> map-object call: middle-print
        /// <paramref name="message"/> on <paramref name="actor"/> when it is a real client and the message is
        /// non-empty. Routes through the host-wired <see cref="CenterprintHandler"/> (the centerprint
        /// networking seam); a no-op when no client / no message / no handler. Every func_/trigger_/target_
        /// map object that QC centerprints a free-text <c>.message</c> calls this one method.
        /// </summary>
        public static void Centerprint(Entity? actor, string? message)
        {
            if (actor is null || string.IsNullOrEmpty(message))
                return;
            if ((actor.Flags & EntFlags.Client) == 0)
                return;
            // Route the free-text centerprint to that client via the raw-centerprint notification channel
            // (→ CenterPrintPanel.Add). NOTIF_ONE_ONLY targets exactly the activator, matching the engine
            // centerprint(client, s) builtin. The optional host handler still fires for any extra wiring/tests.
            NotificationSystem.SendCenterRaw(NotifBroadcast.OneOnly, actor, message);
            CenterprintHandler?.Invoke(actor, message);
        }

        // ====================================================================
        //  Event-damage seam (QC .event_damage — per-entity damage callback)
        // ====================================================================

        // The mechanism (Entity.GtEventDamage, dispatched by DamageSystem.EventDamage for non-player edicts)
        // already exists; these are the shared install/clear helpers so every shootable map object
        // (func_button shootable, func_breakable, func_door_secret, multi_eventdamage, …) wires it uniformly
        // — keeping Wave-2 unit code one line and the field name out of each call site.

        /// <summary>QC <c>.event_damage</c> callback shape: (self, inflictor, attacker, deathType, damage, hitloc, force).</summary>
        public delegate void EventDamageHandler(
            Entity self, Entity? inflictor, Entity? attacker, string deathType, float damage, Vector3 hitLoc, Vector3 force);

        /// <summary>
        /// Install a per-entity event-damage handler (QC <c>this.event_damage = …</c>). The damage pipeline
        /// (<c>DamageSystem.EventDamage</c>) invokes it for any non-player target that has one set, so a
        /// shootable brush (button/breakable/secret door) reacts to being hit without going through the player
        /// kill path. Pass <see cref="MarkDamageable"/> first if the entity must also be <c>SOLID</c> + take
        /// damage to receive hits.
        /// </summary>
        public static void InstallEventDamage(Entity e, EventDamageHandler handler)
            => e.GtEventDamage = (self, infl, atk, dt, dmg, loc, frc) => handler(self, infl, atk, dt, dmg, loc, frc);

        /// <summary>Remove an entity's event-damage handler (QC clears <c>.event_damage</c> on death/disable).</summary>
        public static void ClearEventDamage(Entity e) => e.GtEventDamage = null;

        /// <summary>
        /// QC's shootable-brush damage setup: a high <c>.health</c> and <c>.takedamage = DAMAGE_AIM</c> so the
        /// brush is hittable (its event_damage decides what a hit does). <paramref name="health"/> defaults to
        /// the QC convention 10000 (effectively unkillable — the brush opens/fires on any hit instead of dying).
        /// </summary>
        public static void MarkDamageable(Entity e, float health = 10000f)
        {
            e.Health = health;
            e.MaxHealthMover = health;
            e.TakeDamage = DamageMode.Aim;
        }

        // ====================================================================
        //  sounds-key soundpack selection (QC .sounds / .sound1 / .sound2)
        // ====================================================================

        /// <summary>
        /// Port of the func_door soundpack selection (door.qc:693): <c>sounds &gt; 0</c> (or a Q3-imported door,
        /// which always has sounds) selects the medieval/metal medplat pack onto <c>.noise2</c> (move) and
        /// <c>.noise1</c> (stop). With <c>sounds == 0</c> the caller's defaults (the spawnfunc's own
        /// noise1/noise2) are left untouched. CPMA/QL <c>sound_start/sound_end</c> overrides are map-pack file
        /// probes the headless layer can't do and are skipped (documented gap).
        /// </summary>
        public static void ApplyDoorSounds(Entity e, bool q3compat = false)
        {
            if (e.Sounds > 0 || q3compat)
            {
                e.Noise2 = "plats/medplat1.wav";
                e.Noise1 = "plats/medplat2.wav";
            }
        }

        /// <summary>
        /// Port of the func_plat soundpack selection (plat.qc:80-97): <c>sounds == 1</c> -&gt; plat1/plat2,
        /// <c>sounds == 2</c> (or Q3-imported) -&gt; medplat1/medplat2, onto <c>.noise</c> (move) + <c>.noise1</c>
        /// (stop); then the legacy <c>.sound1</c>/<c>.sound2</c> overrides win if present. The spawnfunc's own
        /// plat1/plat2 default stands when <c>sounds == 0</c> and no override.
        /// </summary>
        public static void ApplyPlatSounds(Entity e, bool q3compat = false)
        {
            if (e.Sounds == 1)
            {
                e.Noise = "plats/plat1.wav";
                e.Noise1 = "plats/plat2.wav";
            }
            if (e.Sounds == 2 || q3compat)
            {
                e.Noise = "plats/medplat1.wav";
                e.Noise1 = "plats/medplat2.wav";
            }
            // QC backwards-compat: explicit legacy overrides take precedence over the pack.
            if (!string.IsNullOrEmpty(e.Sound1))
                e.Noise = e.Sound1;
            if (!string.IsNullOrEmpty(e.Sound2))
                e.Noise1 = e.Sound2;
        }

        /// <summary>
        /// Port of the func_door_secret soundpack selection (door_secret.qc): <c>sounds &gt; 0</c> picks the
        /// medieval/metal pack (move/stop on <c>.noise2</c>/<c>.noise1</c>) — same shape as the door pack.
        /// </summary>
        public static void ApplySecretSounds(Entity e)
        {
            if (e.Sounds > 0)
            {
                e.Noise2 = "plats/medplat1.wav";
                e.Noise1 = "plats/medplat2.wav";
            }
        }

        // ====================================================================
        //  set_platmovetype (platforms.qc) + cubic_speedfunc_is_sane (lib/math.qh)
        // ====================================================================

        /// <summary>
        /// Port of <c>set_platmovetype</c> (platforms.qc:209): parse the <c>.platmovetype</c> spawn-key string
        /// ("start end [force]") into <see cref="Entity.PlatMoveStart"/>/<see cref="Entity.PlatMoveEnd"/>. One
        /// token sets both ends; a 3rd "force" token skips the sanity check. Returns false (and leaves the
        /// values parsed) on an insane reverse curve — QC <c>objerror</c>s there; the headless caller treats a
        /// false return as "reject this mover / keep its default curve". A null/empty string leaves the
        /// port's default (PlatMoveStart/End = 0, the smoothstep ease — same as QC's uninitialized float
        /// fields and set_platmovetype's n==0 branch), so stock movers ease exactly as Base.
        /// </summary>
        public static bool SetPlatMoveType(Entity e, string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return true; // no key — Base's n==0 path also yields 0/0 (already the field default)

            string[] argv = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            int n = argv.Length;

            // QC stof() yields 0 for an unparsable token. platmovetype_start/_end are .float — keep the
            // fractional value (0.5/1.5/2.5 ease factors are valid per cubic_speedfunc_is_sane), don't truncate.
            e.PlatMoveStart = n > 0 ? StofFloat(argv[0]) : 0f;
            e.PlatMoveEnd = n > 1 ? StofFloat(argv[1]) : e.PlatMoveStart;

            if (n > 2 && argv[2] == "force")
                return true; // no checking, return immediately (QC)

            if (!CubicSpeedFuncIsSane(e.PlatMoveStart, e.PlatMoveEnd))
                return false; // QC objerror: "platform would go in reverse"

            return true;
        }

        /// <summary>QC stof(): parse a leading float, 0 on failure (used by the platmovetype tokenizer).</summary>
        private static float StofFloat(string s)
            => float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

        /// <summary>
        /// Port of <c>cubic_speedfunc_is_sane</c> (lib/math.qh:138): reject ease curves whose speed function
        /// would reverse the mover (negative speed factors, or a curve whose first-derivative zeros fall inside
        /// 0..1). Used by <see cref="SetPlatMoveType"/> to validate a mapper's platmovetype.
        /// </summary>
        public static bool CubicSpeedFuncIsSane(float startSpeedFactor, float endSpeedFactor)
        {
            if (startSpeedFactor < 0f || endSpeedFactor < 0f)
                return false;

            // The possible zeros of the first derivative are outside 0..1 (QC "better" check).
            if (startSpeedFactor <= 3f && endSpeedFactor <= 3f)
                return true;

            // Otherwise the first derivative must have no zeros at all (an ellipse condition, QC).
            float se = startSpeedFactor + endSpeedFactor;
            float s_e = startSpeedFactor - endSpeedFactor;
            if (3f * (se - 4f) * (se - 4f) + s_e * s_e <= 12f)
                return true;

            return false;
        }

        /// <summary>setorigin through the facade when present, else a plain assignment (headless tests).</summary>
        public static void SetOrigin(Entity e, Vector3 origin)
        {
            if (Api.Services is not null)
                Api.Entities.SetOrigin(e, origin);
            else
                e.Origin = origin;
        }

        /// <summary>
        /// Port of QC <c>RandomSelection</c> (lib/random.qc): weighted reservoir pick with a priority tier —
        /// candidates at the highest priority win, ties broken by weight via the deterministic
        /// <see cref="Prandom"/>. Used by teleporters (telefrag-avoid priority + cnt weight), jumppads, and
        /// target_random. Seed it with <see cref="Reset"/>, feed candidates with <see cref="Add"/>, read
        /// <see cref="Chosen"/>.
        /// </summary>
        public struct RandomSelection
        {
            private float _bestPriority;
            private float _totalWeight;
            public Entity? Chosen;

            public void Reset() { _bestPriority = 0f; _totalWeight = 0f; Chosen = null; }

            public void Add(Entity e, float weight, float priority)
            {
                if (priority > _bestPriority)
                {
                    _bestPriority = priority;
                    Chosen = e;
                    _totalWeight = weight;
                }
                else if (priority == _bestPriority)
                {
                    _totalWeight += weight;
                    if (Prandom.Float() * _totalWeight <= weight)
                        Chosen = e;
                }
            }
        }

        /// <summary>setsize through the facade when present (headless tests just assign mins/maxs).</summary>
        public static void SetSize(Entity e, Vector3 mins, Vector3 maxs)
        {
            if (Api.Services is not null)
                Api.Entities.SetSize(e, mins, maxs);
            else
            {
                e.Mins = mins; e.Maxs = maxs; e.Size = maxs - mins;
            }
        }

        // ====================================================================
        //  FindConnectedComponent (util.qc) — used by LinkDoors
        // ====================================================================

        /// <summary>Visit the next candidate neighbor of <paramref name="cur"/> (QC findNextEntityNearFunction).</summary>
        public delegate Entity? FindNextNear(Entity? cursor, Entity near, Entity pass);

        /// <summary>Are two entities connected for the component walk (QC isConnectedFunction)?</summary>
        public delegate bool IsConnected(Entity a, Entity b, Entity pass);

        /// <summary>
        /// Port of <c>FindConnectedComponent</c> (util.qc): breadth-first flood from <paramref name="start"/>
        /// over a graph defined by <paramref name="next"/> (neighbor candidates) and <paramref name="iscon"/>
        /// (connection test), chaining the visited entities through the <paramref name="setLink"/>/<paramref
        /// name="getLink"/> field accessors into a NULL-terminated list (LinkDoors then closes it into a loop).
        /// We expose the link field as accessor delegates since C# has no <c>.entity field</c> pointers.
        /// </summary>
        public static void FindConnectedComponent(
            Entity start, Action<Entity, Entity?> setLink, Func<Entity, Entity?> getLink,
            FindNextNear next, IsConnected iscon, Entity pass)
        {
            Entity queueStart = start, queueEnd = start;
            setLink(queueEnd, null);
            queueEnd.FccProcessing = true;

            for (Entity? cursor = queueStart; cursor is not null; cursor = getLink(cursor))
            {
                Entity? t = null;
                while ((t = next(t, cursor, pass)) is not null)
                {
                    if (t.FccProcessing)
                        continue;
                    if (iscon(t, cursor, pass))
                    {
                        setLink(queueEnd, t);
                        queueEnd = t;
                        setLink(queueEnd, null);
                        queueEnd.FccProcessing = true;
                    }
                }
            }

            // unmark (QC second pass clears FindConnectedComponent_processing).
            for (Entity? t = start; t is not null; t = getLink(t))
                t.FccProcessing = false;
        }

        // ====================================================================
        //  SUB_SetFade (subs.qc) — used by breakable debris
        // ====================================================================

        /// <summary>
        /// Port of <c>SUB_SetFade</c> (subs.qc): fade <paramref name="ent"/>'s alpha out starting at
        /// <paramref name="vanishTime"/>, taking <paramref name="fadingTime"/> seconds, then remove it
        /// (non-clients). Drives <see cref="SubSetFadeThink"/>.
        /// </summary>
        public static void SubSetFade(Entity ent, float vanishTime, float fadingTime)
        {
            if (fadingTime <= 0f)
                fadingTime = 0.01f;
            ent.FadeRate = 1f / fadingTime;
            ent.Think = SubSetFadeThink;
            ent.NextThink = vanishTime;
        }

        /// <summary>QC <c>SUB_SetFade_Think</c>: decrement alpha each frame; vanish/remove at the floor.</summary>
        public static void SubSetFadeThink(Entity ent)
        {
            if (ent.Alpha == 0f)
                ent.Alpha = 1f;
            ent.Think = SubSetFadeThink;
            ent.NextThink = Now();
            ent.Alpha -= FrameTime() * ent.FadeRate;
            if (ent.Alpha < 0.01f)
            {
                if ((ent.Flags & EntFlags.Client) != 0)
                {
                    ent.Alpha = -1f;     // QC SUB_VanishOrRemove: clients just go invisible
                    ent.Effects = 0;
                }
                else
                {
                    RemoveEntity(ent);   // non-clients are removed
                }
            }
        }
    }
}
