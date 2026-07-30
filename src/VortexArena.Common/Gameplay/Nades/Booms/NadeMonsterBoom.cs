// Port of qcsrc/common/mutators/mutator/nades/nade/monster.qc (nade_monster_boom).
//
// The "pokenade" monster nade spawns one INSANE-skill monster of the thrower's chosen type at the detonation
// point, owned by (and on the team of) the thrower. Gated by g_monsters — when monsters are off the nade type
// already falls back to NORMAL via NadeRegistry.CheckType (RequiresMonsters), so reaching this boom implies
// monsters are enabled; the guard is kept for parity. Uses the existing MonsterAI.SpawnMonster (the port's
// spawnmonster()).
//
// monster_lifetime (g_nades_pokenade_monster_lifetime, 150s) is set on the spawned monster's MonsterState so
// MonsterAI.RunThink suicides it when the clock runs out (the port's Monster_Think monster_lifetime kill);
// the INSANE skill + owner/team are applied faithfully.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The monster ("pokenade") nade detonation — port of <c>nade_monster_boom</c>.</summary>
public sealed class NadeMonsterBoom : INadeBoom
{
    public string NadeNetName => "pokenade";

    /// <summary>
    /// Port of <c>nade_monster_boom(entity this)</c> (monster.qc:7): if g_monsters, spawn a single non-aligned
    /// (no drop-to-floor) monster of <see cref="Entity.PokenadeType"/> at the nade origin, owned by + following
    /// the thrower, at MONSTER_SKILL_INSANE.
    /// </summary>
    public void Boom(Entity nade)
    {
        if (Api.Services is null) return;
        if (Api.Cvars.GetFloat("g_monsters") == 0f)
            return;

        Entity owner = nade.RealOwner ?? nade;

        // QC: entity e = spawn(); e.noalign = true; e = spawnmonster(e, pokenade_type, MON_Null, realowner,
        // realowner, origin, false, false, 1). Set noalign + the INSANE skill BEFORE the spawn (the port reads
        // monster_skill while building the monster state during SpawnMonster).
        Entity e = Api.Entities.Spawn();
        e.NoAlign = true;                       // QC e.noalign = true (don't drop to floor)
        e.MonsterSkill = MonsterSkill.Insane;   // QC e.monster_skill = MONSTER_SKILL_INSANE

        Entity? mon = MonsterAI.SpawnMonster(e, nade.PokenadeType ?? "", null, owner, owner,
            nade.Origin, respawn: false, removeIfInvalid: false, moveFlags: 1);
        if (mon is null)
            return; // monster failed to be spawned

        // QC: if (autocvar_g_nades_pokenade_monster_lifetime > 0) e.monster_lifetime = time + lifetime;
        //     e.monster_skill = MONSTER_SKILL_INSANE;
        float lifetime = Api.Cvars.GetFloat("g_nades_pokenade_monster_lifetime");
        if (lifetime > 0f && MonsterAI.StateOf(mon) is { } st)
            st.Lifetime = Api.Clock.Time + lifetime;
        mon.MonsterSkill = MonsterSkill.Insane;
    }
}
