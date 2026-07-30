// Port of qcsrc/common/mutators/mutator/nades/nade/spawn.qc
//   (nade_spawn_boom + nade_spawn_SetSpawnHealth + nade_spawn_DestroyDamage).
//
// The spawn nade plants a relocation marker (nade_spawn_loc) at its detonation point and stores it on the
// thrower; the thrower then respawns at that marker for the next g_nades_spawn_count deaths. The actual
// respawn relocation + spawn-health override is consumed by part-A's NadesMutator PlayerSpawn handler (which
// reads Entity.NadeSpawnLoc / NadeSpawnCount and g_nades_spawn_health_respawn) — so this file only PLANTS the
// marker and supplies the shot-down DestroyDamage (which hurts the owner).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The spawn nade detonation — port of <c>nade_spawn_boom</c> + its DestroyDamage.</summary>
public sealed class NadeSpawnBoom : INadeBoom, INadeDestroyDamage
{
    public string NadeNetName => "spawn";

    /// <summary>
    /// Port of <c>nade_spawn_boom(entity this)</c> (spawn.qc:4): create a SOLID_NOT, MOVETYPE_NONE
    /// spawn-loc marker at the nade origin sized to the player hull, with <c>cnt = g_nades_spawn_count</c>
    /// spawns remaining, and hang it on the thrower (replacing any prior marker).
    /// </summary>
    public void Boom(Entity nade)
    {
        if (Api.Services is null) return;
        Entity player = nade.RealOwner ?? nade;

        Entity spawnloc = Api.Entities.Spawn();
        spawnloc.ClassName = "nade_spawn_loc";
        Api.Entities.SetOrigin(spawnloc, nade.Origin);
        // QC: setsize(spawnloc, player.mins, player.maxs).
        Vector3 mins = player.Mins == Vector3.Zero ? new Vector3(-16, -16, -24) : player.Mins;
        Vector3 maxs = player.Maxs == Vector3.Zero ? new Vector3(16, 16, 45) : player.Maxs;
        Api.Entities.SetSize(spawnloc, mins, maxs);
        spawnloc.MoveType = MoveType.None;
        spawnloc.Solid = Solid.Not;
        spawnloc.Effects = EffectFlags.Stardust; // QC EF_STARDUST (drawonlytoclient is render-only)
        // Base's nade_spawn_loc (spawn.qc) is SOLID_NOT with EF_STARDUST and no real mesh — the stardust IS the
        // visible. But ServerNet.Classify drops a non-projectile that has no model (ServerNet.cs:2187 "nothing to
        // render"), which would starve EF_STARDUST off the wire. Plant a tiny near-invisible marker so the feed
        // keeps the entity; the mesh is incidental, EF_STARDUST does the rendering. (Owner-only is a follow-up.)
        spawnloc.Model = "models/marker.md3";
        spawnloc.NadeSpawnCount = (int)NadeProjectile.Cvar("g_nades_spawn_count", 3f); // QC .cnt

        if (player.NadeSpawnLoc is not null)
            Api.Entities.Remove(player.NadeSpawnLoc);
        player.NadeSpawnLoc = spawnloc;
    }

    /// <summary>
    /// Port of <c>nade_spawn_DestroyDamage(entity this, entity attacker)</c> (spawn.qc:28): when a spawn nade
    /// is shot to death, hurt its OWNER (g_nades_spawn_destroy_damage). Returns false — QC lets the normal
    /// boom still run (so the nade also explodes), unlike translocate.
    /// </summary>
    public bool DestroyDamage(Entity nade, Entity? attacker)
    {
        if (Api.Services is null) return false;
        float dmg = NadeProjectile.Cvar("g_nades_spawn_destroy_damage", 25f);
        if (nade.NadeBonusType == (NadeRegistry.Spawn?.Id ?? -1) && dmg > 0f)
        {
            Entity owner = nade.RealOwner ?? nade;
            // QC DEATH_TOUCHEXPLODE — the "died in an accident" obituary (no DeathTypes constant; use the tag).
            Combat.Damage(owner, attacker, attacker, dmg, "touchexplode", owner.Origin, Vector3.Zero);
        }
        return false; // QC returns false: the normal explosion still happens.
    }
}
