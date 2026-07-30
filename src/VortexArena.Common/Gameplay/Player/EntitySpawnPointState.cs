// Per-entity spawn-point state for SelectSpawnPoint / Spawn_Score (server/spawnpoints.qc).
//
// QC spawnpoints carry .active (ACTIVE_*) and .restriction on the edict; defs.qh defines them as generic
// map-object fields. This port promotes the two that Spawn_Score reads as `partial Entity` members in their
// own file (ADR-0007) so no engine file is touched and they never collide with the mover .active that
// MapObjectsCommon.cs keeps on its own state object. Defaults match a freshly-spawned info_player_* edict
// (ACTIVE_ACTIVE, no restriction) so an unconfigured spot scores exactly as before this state existed.

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        /// <summary>
        /// QC spawnpoint <c>.active</c> (ACTIVE_*, see <see cref="Gameplay.MapMover"/>). Spawnpoints set
        /// ACTIVE_ACTIVE on spawn; an Onslaught control-point's linked spawns flip to ACTIVE_NOT when the
        /// point is lost. Spawn_Score rejects a spot whose active != ACTIVE_ACTIVE when target-checking.
        /// </summary>
        public int SpawnActive = Gameplay.MapMover.ActiveActive;

        /// <summary>
        /// QC spawnpoint <c>.restriction</c>: 0 = anyone, 1 = bots only (real clients rejected), 2 = real
        /// clients only (bots rejected). Spawn_Score filters on it against IS_REAL_CLIENT(this).
        /// </summary>
        public int SpawnRestriction;
    }
}
