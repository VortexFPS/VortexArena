// Port of qcsrc/common/monsters/sv_monsters.qc — the per-entity `.reset` field (the round-restart hook).
//
// In QuakeC every entity may carry a `.reset` callback (`void(entity this) reset`); the round handler fires
// every entity's `.reset` on a round restart. Monster_Spawn (sv_monsters.qc:1469) installs
// `this.reset = Monster_Reset`, and Monster_Dead (1068) clears it (`this.reset = func_null`) so a corpse
// no longer round-resets. The base port `Entity` had no such delegate (the turret/vehicle/monster ports
// deferred it — T14), so this new partial adds it, mirroring how MonsterSpawnFuncs added .monster_skill
// (ADR-0007, entity-model.md: extend the partial Entity in a NEW file, never edit Framework/Entity.cs).

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        /// <summary>
        /// QC <c>.reset</c>: the round-restart hook. The round handler invokes <c>this.reset(this)</c> on every
        /// entity when a round restarts (e.g. <c>MonsterAI.Reset</c> for a monster: SPAWNED monsters are removed,
        /// natural ones restored to their spawn point at full health). Null = the entity has no reset behaviour.
        /// </summary>
        public System.Action<Entity>? Reset;

        /// <summary>
        /// QC <c>.reset2</c>: the SECOND-pass round-restart hook. <c>reset_map</c> (server/command/vote.qc:404)
        /// fires every non-client entity's <c>.reset2</c> AFTER the whole <c>.reset</c> pass has completed, so the
        /// "come last" objects (waypoint sprites and the Assault round-start trigger) re-arm only once the movers /
        /// spawnpoints they depend on have already been reset. Null = the entity has no second-pass reset.
        /// </summary>
        public System.Action<Entity>? Reset2;
    }
}
