// Port of the per-entity waypoint-sprite back-links the gametype/mutator producers keep on their
// objective and pickup edicts so they can later FIND and KILL the sprite they spawned.
//
// In QuakeC a producer stashed the WaypointSprite_Spawn return on the entity itself (e.g. the buff
// pickup's .buff_waypoint, the CTF flag's .wps_flagcarrier on its carrier) and read it back to call
// WaypointSprite_Kill / WaypointSprite_UpdateHealth. The C# entity-model (ADR-0007) promotes those
// flat .fields to typed members; Entity is declared `partial`, so — exactly like
// Mutators/EntityMutatorState.cs and GameTypes/EntityGametypeState.cs — we add them here in a NEW file
// without editing the owning state files. These are headless-sim references (the WaypointSprite record
// is the Godot-free model the server networks via ServerNet.SendWaypoints); they default null and are
// set/cleared by their owning producer (BuffsMutator for the buff marker, Ctf for the carrier bar).

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // --- buffs (sv_buffs.qc, on a buff pickup entity) ---
        /// <summary>
        /// QC the buff pickup's waypoint sprite (buffs.qc WaypointSprite_Spawn on a spawned buff item):
        /// the per-item radar/world marker that shows where an uncollected buff sits. Lives next to
        /// <see cref="BuffDef"/>/<see cref="BuffActive"/> on the pickup entity (a normal hideable
        /// <c>WaypointSprite</c>, radar icon 1). Set when <c>BuffsMutator.SpawnBuffWaypoint</c> attaches it,
        /// cleared (WaypointSprite_Kill) when the buff is taken or relocated. Null = no marker.
        /// </summary>
        public XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite? BuffWaypoint;

        // --- CTF (sv_ctf.qc, on a flag entity / its carrier) ---
        /// <summary>
        /// QC the flag-carrier waypoint sprite (sv_ctf.qc <c>.wps_flagcarrier</c>, spawned via
        /// WaypointSprites.AttachCarrier when a player takes the flag): the carrier marker whose
        /// already-networked Health/HelpmeUntil fields drive the over-head health bar. Lives next to the
        /// CTF flag entity's status/carrier fields (<see cref="GtStatus"/>/<see cref="GtCarrier"/>). Set by
        /// <c>Ctf</c> on pickup, cleared (WaypointSprite_Kill) on drop/score/return. Null = no carrier marker.
        /// </summary>
        public XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite? CarrierWaypoint;
    }
}
