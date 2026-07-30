using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Map-placement spawnfuncs for the monster family — the thin <c>spawnfunc(monster_X)</c> entries each
    /// per-type .qc defines (common/monsters/monster/*.qc) plus the <c>monster_spawner</c> entity
    /// (common/monsters/sv_spawner.qc). Every per-type spawnfunc is just
    /// <c>spawnfunc(monster_X){ Monster_Spawn(this, true, MON_X); }</c>, so they all funnel through the shared
    /// <see cref="MonsterAI.SpawnFromMap"/> driver (= QC <c>Monster_Spawn</c>) with the monster resolved from the
    /// <see cref="Monsters"/> registry by net name. <see cref="MapObjectsRegistry"/> wires these into
    /// <see cref="SpawnFuncs"/> so the BSP entity-lump loader instantiates hand-placed monsters on stock maps.
    /// </summary>
    public static class MonsterSpawnFuncs
    {
        /// <summary>
        /// The shared body of every <c>spawnfunc(monster_X)</c> (QC <c>Monster_Spawn(this, true, MON_X)</c>):
        /// resolve the monster type by net name and drive it through <see cref="MonsterAI.SpawnFromMap"/> with
        /// <c>check_appear=true</c>. A bad/missing type or a disabled-monsters server just leaves the edict for
        /// the driver to remove. <paramref name="netName"/> is the QC <c>mon.netname</c> (after <c>monster_</c>).
        /// </summary>
        private static void Spawn(Entity e, string netName)
            => MonsterAI.SpawnFromMap(Monsters.ByName(netName), e, checkAppear: true);

        // Per-type spawnfuncs (each per-monster .qc: spawnfunc(monster_X){ Monster_Spawn(this, true, MON_X); }).
        public static void Zombie(Entity e) => Spawn(e, "zombie");   // monster/zombie.qc:95
        public static void Golem(Entity e) => Spawn(e, "golem");     // monster/golem.qc:221
        public static void Mage(Entity e) => Spawn(e, "mage");       // monster/mage.qc:416
        public static void Spider(Entity e) => Spawn(e, "spider");   // monster/spider.qc:181
        public static void Wyvern(Entity e) => Spawn(e, "wyvern");   // monster/wyvern.qc:120

        /// <summary>
        /// QC compatibility alias <c>spawnfunc(monster_shambler){ spawnfunc_monster_golem(this); }</c>
        /// (golem.qc:226) — the legacy Quake classname maps onto the Golem.
        /// </summary>
        public static void Shambler(Entity e) => Golem(e);

        // ================================================================================================
        // monster_spawner (common/monsters/sv_spawner.qc) — a triggered monster emitter.
        // ================================================================================================

        /// <summary>
        /// Port of <c>spawnfunc(monster_spawner)</c> (sv_spawner.qc): a map entity that, when triggered, emits a
        /// monster of type <c>.spawnmob</c> up to <c>.count</c> live at once. Requires the <c>g_monsters</c>
        /// master switch and a non-empty <c>spawnmob</c> key, else the edict is deleted. Wires
        /// <c>this.use = spawner_use</c>.
        /// </summary>
        public static void MonsterSpawner(Entity e)
        {
            // QC: if (!autocvar_g_monsters || ...) — an explicit g_monsters 0 disables; unset defaults on.
            if (!MonsterAI.MasterSwitchEnabled("g_monsters") || string.IsNullOrEmpty(e.Spawnmob))
            {
                if (Api.Services is not null)
                    Api.Entities.Remove(e);
                return;
            }

            e.ClassName = "monster_spawner";
            e.Use = (self, _) => SpawnerUse(self);
        }

        /// <summary>
        /// Port of <c>spawner_use</c> (sv_spawner.qc): count this spawner's still-living monsters (QC
        /// <c>IL_EACH(g_monsters, it.realowner == this)</c>) and, while that count is below <c>.count</c>, emit
        /// one more of <c>spawnmob</c> at the spawner's origin via <see cref="MonsterAI.SpawnMonster"/> (QC
        /// <c>spawnmonster</c>, removeifinvalid=true so a bogus name doesn't leak an edict). The new monster
        /// carries the spawner's <c>noalign</c>/<c>angles</c>/<c>monster_skill</c>/<c>skin</c> and is owned by
        /// the spawner so it counts against the cap. An unset <c>.count</c> (0) makes the spawner inert (QC has
        /// no floor — the <c>moncount &gt;= this.count</c> guard is immediately true).
        /// </summary>
        private static void SpawnerUse(Entity spawner)
        {
            if (Api.Services is null)
                return;

            // QC spawner_use: `if (moncount >= this.count) return;` — no floor. An unset .count is 0, so a
            // spawner with no count key is inert (never emits), matching sv_spawner.qc exactly.
            int alive = 0;
            foreach (Entity it in Api.Entities.FindByClass("monster"))
                if (ReferenceEquals(it.Owner, spawner)) alive++;
            if (alive >= spawner.Count)
                return;

            // QC copies these four edict keys from the spawner onto the child (sv_spawner.qc:15-18):
            //   e.noalign = this.noalign; e.angles = this.angles; e.monster_skill = this.monster_skill; e.skin = this.skin;
            Entity child = Api.Entities.Spawn();
            child.NoAlign = spawner.NoAlign;
            child.Angles = spawner.Angles;
            child.MonsterSkill = spawner.MonsterSkill;
            child.Skin = spawner.Skin;

            // QC: spawnmonster(e, this.spawnmob, MON_Null, this, this, this.origin, false, true,
            //                  this.monster_moveflags).  respwn=false (NORESPAWN), removeifinvalid=true;
            //      spawnedby=follow=this spawner.
            MonsterAI.SpawnMonster(child, spawner.Spawnmob, null, spawner, spawner, spawner.Origin,
                respawn: false, removeIfInvalid: true, moveFlags: spawner.MonsterMoveFlags);
        }
    }
}

namespace XonoticGodot.Common.Framework
{
    /// <summary>
    /// QC monster edict keys (<c>common/monsters/sv_spawner.qc</c> + <c>sv_monsters.qc</c>) that have no home
    /// on the base <see cref="Entity"/> and aren't promoted by any other partial. Added in this NEW file
    /// (ADR-0007, the same technique the vehicle/map-object ports use) so no engine file is touched; the host's
    /// map-entity applier copies the matching dict keys onto these. The numeric <c>.count</c> reuses the
    /// existing <see cref="Entity.Count"/> field and <c>.skin</c> the base <see cref="Entity.Skin"/>.
    /// </summary>
    public partial class Entity
    {
        /// <summary>QC monster_spawner <c>.spawnmob</c> — the monster net name this spawner emits ("" = none).</summary>
        public string Spawnmob = "";

        /// <summary>QC <c>.monster_moveflags</c> as set on a spawner — the no-enemy move style handed to emitted monsters.</summary>
        public int MonsterMoveFlags;

        /// <summary>QC <c>.noalign</c> — skip the spawn drop-to-floor when set (carried from a spawner to its child).</summary>
        public bool NoAlign;

        /// <summary>QC <c>.monster_skill</c> — a per-entity skill override (0 = unset → fall back to g_monsters_skill).</summary>
        public int MonsterSkill;
    }
}
