// Extra per-entity map-object state that the deep behaviors (LinkDoors chains, key locks, the
// trigger_gravity exit-checker, func_train turning/curves, trigger_impulse accel zones, breakable debris,
// telefrag boxes) need but the first MapObjectsCommon pass did not promote.
//
// Like MapObjectsCommon.cs / EntityResources.cs this extends the `partial Entity` in a NEW file (ADR-0007)
// so no engine file is touched. Names are prefixed/disambiguated so they never collide with the mover
// fields already added in MapObjectsCommon.cs (Pos1/Pos2/Speed/Wait/MoverState/Cnt/…) or the vehicle
// partial (DmgTime).

using System.Numerics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    /// <summary>
    /// QC <c>.setactive</c> function pointer (mapobjects/triggers.qc <c>generic_setactive</c> / the per-family
    /// <c>*_setactive</c>): set an entity's ACTIVE_* state to <paramref name="astate"/> (ACTIVE_TOGGLE flips).
    /// relay_activators (relay_activate/_deactivate/_activatetoggle) and generic_setactive dispatch through it.
    /// Distinct from <see cref="EntityUse"/> (2-arg) because <c>.setactive</c> takes an ACTIVE_* int, not an
    /// activator. Most movers leave it null (QC: <c>if (trg.setactive) … else generic_setactive(trg, …)</c>).
    /// </summary>
    public delegate void EntityUseActive(Entity self, int astate);

    public partial class Entity
    {
        // ---- .setactive function pointer (QC mapobjects/triggers.qc) ----
        /// <summary>
        /// QC <c>.setactive</c> — the active-state setter relay_activators and generic_setactive dispatch to.
        /// Null on most movers; callers fall back to <see cref="XonoticGodot.Common.Gameplay.LogicGates.GenericSetActive"/>.
        /// </summary>
        public EntityUseActive? SetActive;

        // ---- logic-gate latch (QC .state on flipflop/monoflop/multivibrator) ----
        // Distinct from MoverState (STATE_TOP/BOTTOM for doors/buttons, MapObjectsCommon.cs): the gates reuse
        // QC's .state purely as an on/off latch, so a separate field avoids clobbering door/button state.
        /// <summary>QC <c>.state</c> as a logic-gate on/off latch (flipflop/monoflop/multivibrator).</summary>
        public int GateState;

        // ---- trigger_relay_teamcheck saved team (QC .team_saved, IL g_saved_team) ----
        /// <summary>QC <c>.team_saved</c> — the entity's spawn-time <c>.team</c>, restored by the relay_teamcheck
        /// reset (relay_teamcheck.qc:33). Stamped at spawn so a round restart re-asserts the original team gate.</summary>
        public float TeamSaved;

        // ---- target_kill / func_door_secret second obituary message (QC .message2) ----
        /// <summary>QC <c>.message2</c> — the "by &lt;attacker&gt;" obituary half (target_kill, door_secret).</summary>
        public string Message2 = "";

        // ---- func_door_secret slide distances (QC .t_width / .t_length) ----
        /// <summary>QC <c>.t_width</c> — secret-door back/down slide distance (auto from size if 0).</summary>
        public float TWidth;
        /// <summary>QC <c>.t_length</c> — secret-door sideways slide distance (auto from size if 0).</summary>
        public float TLength;
        // NOTE: door_secret's `.mangle` reuses the existing MAngle (MapObjectsCommon.cs); `.oldorigin` reuses
        // the engine OldOrigin field; `.speed` reuses Speed.

        // ---- trigger_warpzone trigger sizing (QC .scale / .warpzone_isboxy, lib/warpzone) ----
        /// <summary>QC <c>.scale</c> resolved for a warpzone trigger (server.qc:662 — modelscale then 1); the
        /// trigger volume was sized mins*scale..maxs*scale. 1 for the common case.</summary>
        public float WarpzoneScale = 1f;
        /// <summary>QC <c>.warpzone_isboxy</c> (util_server.qc) — box trigger (no inline model, or mapper-overridden
        /// bounds) so Base skips the exact-surface touch match.</summary>
        public bool WarpzoneIsBoxy;

        // ---- target_changelevel keys (QC .chmap / .gametype / .chlevel_targ) ----
        /// <summary>QC <c>.chmap</c> — the map target_changelevel switches to ("" = end match).</summary>
        public string ChMap = "";
        /// <summary>QC <c>.gametype</c> — optional next-map gametype for target_changelevel.</summary>
        public string ChLevelGameType = "";
        /// <summary>QC <c>.chlevel_targ</c> — the changelevel a player has voted/triggered (multiplayer fraction).</summary>
        public Entity? ChLevelTarg;

        // ---- target_spawnpoint (QC .spawnpoint_targ) ----
        /// <summary>QC <c>.spawnpoint_targ</c> — a forced spawnpoint set on a player by target_spawnpoint.</summary>
        public Entity? SpawnPointTarg;
        // ---- door key locks (QC .itemkeys / .key_door_messagetime) ----
        public int ItemKeys;              // QC .itemkeys — bitfield of keys this door requires / this player holds
        public float KeyDoorMessageTime;  // QC .key_door_messagetime — re-print throttle for "need a key"

        // ---- extra door/keylock sounds (QC .noise3) ----
        public string Noise3 = "";        // QC .noise3 — door "still locked" / keylock missing-key sound

        // ---- soundpack selector (QC .sounds) ----
        public int Sounds;                // QC .sounds — trigger_multiple/secret/keylock noise selector

        // ---- bidirectional rotating-door reverse marker (QC .trigger_reverse, subs.qh) ----
        // Map-settable on a trigger_multiple that fires a func_door_rotating: a trigger with trigger_reverse=1
        // opens a BIDIR rotating door in the reversed direction (door_use's DOOR_ROTATING_BIDIR path). Declared
        // but never assigned in QC (it is purely a map key on the firing trigger).
        public int TriggerReverse;        // QC .trigger_reverse

        // ---- door touch debounce + LinkDoors connected-component marker ----
        public float DoorFinished;        // QC .door_finished — door_touch message/throttle window
        public bool FccProcessing;        // QC .FindConnectedComponent_processing — BFS visited flag

        // ---- impulse/conveyor push throttle (QC .lastpushtime) ----
        public float LastPushTime;        // QC .lastpushtime — trigger_impulse per-toucher accel timestep

        // ---- velocity-jumppad de-dupe (QC .last_pushed) ----
        public Entity? LastPushedPad;     // QC .last_pushed — the velocity pad that last pushed this toucher

        // ---- trigger_swamp (QC .swampslug / .swamp_interval / .swamp_slowdown) ----
        public Entity? SwampSlug;         // QC .swampslug — the swamp the toucher is currently standing in
        public float SwampNextTime;       // QC .swamp_interval used as a per-toucher next-hit clock
        public float SwampInterval;       // QC .swamp_interval — seconds between swamp damage ticks (on the trigger)
        public float SwampSlowdown;       // QC .swamp_slowdown — movement multiplier applied while swamped

        // ---- trigger_impulse fields (QC .strength / .falloff / .radius reused as a float) ----
        public float Strength;            // QC .strength — impulse force per second / accel factor
        public int Falloff;               // QC .falloff — FALLOFF_NONE/LINEAR/LINEAR_INV
        public float ImpulseRadius;       // QC .radius — spherical impulse radius (0 => directional/accel)

        // ---- func_train turning/curve bookkeeping (QC .future_target / .train_wait_turning / .platmovetype_turn) ----
        public Entity? FutureTarget;      // QC .future_target — the path_corner queued after the current one
        public bool TrainWaitTurning;     // QC .train_wait_turning — mid-turn flag during the dwell
        public bool PlatMoveTurn;         // QC .platmovetype_turn — TRAIN_TURN: orient toward the next corner
        public string CurveTarget = "";   // QC .curvetarget — a path_corner's bezier control point targetname
        public bool TargetRandom;         // QC .target_random — pick the next corner at random

        // ---- trigger_gravity exit-checker companion (QC .trigger_gravity_check) ----
        public Entity? GravityCheck;      // QC .trigger_gravity_check — the per-toucher zone-exit watchdog
        public float GravityRestore;      // QC checker .gravity — the gravity to restore when the zone is left

        // ---- breakable debris / blast (QC .dmg_edge .dmg_radius .dmg_force .debris* .respawntimejitter) ----
        public float DmgEdge;             // QC .dmg_edge — radius-damage at the rim
        public float DmgRadius;           // QC .dmg_radius — radius-damage reach
        public float DmgForce;            // QC .dmg_force — radius-damage knockback
        public string Debris = "";        // QC .debris — space-separated debris model list
        public string MdlDead = "";       // QC .mdl_dead — wreck model (or "" to hide)
        public Vector3 DebrisVelocity;    // QC .debrisvelocity
        public Vector3 DebrisVelocityJitter; // QC .debrisvelocityjitter
        public Vector3 DebrisAVelocityJitter;// QC .debrisavelocityjitter
        public float DebrisTime;          // QC .debristime — debris lifetime
        public float DebrisTimeJitter;    // QC .debristimejitter
        public float DebrisFadeTime;      // QC .debrisfadetime
        public MoveType DebrisMoveType;   // QC .debrismovetype
        public Solid DebrisSolid;         // QC .debrissolid
        // NOTE: QC .respawntimejitter is promoted by the items port (Items/EntityItemState.cs); shared.
        // NOTE: QC .alpha is promoted by the damage pipeline (Damage/DamageEntityState.cs Alpha); shared.

        // ---- generic fade-out (QC SUB_SetFade: .fade_rate) ----
        public float FadeRate;            // QC .fade_rate — alpha units/second while fading

        // ---- button setactive bookkeeping (QC .wait_remaining / .activation_time) ----
        public float WaitRemaining = -1f; // QC .wait_remaining — time left in the press when deactivated
        public float ActivationTime = -1f;// QC .activation_time — when the press began (for wait_remaining)

        // ---- SUB_UseTargets reuse latch + target_random (QC .sub_target_used / .target_random) ----
        public float SubTargetUsed = -1f; // QC .sub_target_used — last time a preventReuse fire hit this target

        // ---- trigger_multiple CTS per-client wait buffers (QC .triggertimes buf_create/bufstr_*) ----
        // In CTS/Race each client gets an independent re-trigger time keyed by their entity slot index
        // (QC etof(enemy)). Allocated by MultipleSetup only when IS_GAMETYPE(CTS); null on non-CTS triggers.
        // Dictionary<clientIndex, lastTriggerTime> — index == Entity.Index, time == server sim time.
        /// <summary>
        /// QC <c>.triggertimes</c> string-buffer (multi.qc): per-client last-trigger time keyed by
        /// <see cref="Entity.Index"/>. Allocated in <see cref="XonoticGodot.Common.Gameplay.Triggers.MultipleSetup"/>
        /// only when the gametype is CTS (mirrors <c>buf_create()</c> in <c>spawnfunc(trigger_multiple)</c>).
        /// Null on non-CTS triggers (the shared <see cref="Entity.NextThink"/> path is used instead).
        /// </summary>
        public System.Collections.Generic.Dictionary<int, float>? CtsTriggerTimes;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Process-wide map-object counters/registries that QC kept as globals or entity-lists
    /// (<c>secrets_found</c>/<c>secrets_total</c>, <c>g_swamped</c>, <c>g_counters</c>) plus the central
    /// all-entities/targetname index that replaces the union-of-classnames scan.
    /// </summary>
    public static class MapObjectsState
    {
        /// <summary>QC <c>secrets_total</c> — number of trigger_secrets on the map.</summary>
        public static int SecretsTotal;

        /// <summary>QC <c>secrets_found</c> — number the players have discovered.</summary>
        public static int SecretsFound;

        /// <summary>
        /// Per-player trigger_counter progress entities (QC <c>g_counters</c> IL). Keyed by (counter, player).
        /// Kept here rather than scanning so COUNTER_PER_PLAYER works without a global entity iterator.
        /// </summary>
        public static readonly List<Framework.Entity> Counters = new();

        /// <summary>
        /// QC <c>g_locations</c> IL — the HUD location-name volumes (target_location / info_location). Each
        /// carries its label in <see cref="Framework.Entity.NetName"/>; the HUD picks the nearest to a player.
        /// </summary>
        public static readonly List<Framework.Entity> Locations = new();

        /// <summary>Reset all map-object globals (QC does this implicitly on map (re)load).</summary>
        public static void Reset()
        {
            SecretsTotal = 0;
            SecretsFound = 0;
            Counters.Clear();
            Locations.Clear();
            MapVolumes.ResetTracking(); // drop per-producer conveyed/laddered lists (g_conveyed/g_ladderents)
        }
    }
}
