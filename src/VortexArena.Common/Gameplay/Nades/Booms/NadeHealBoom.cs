// Port of qcsrc/common/mutators/mutator/nades/nade/heal.qc (nade_heal_boom + nade_heal_touch).
//
// The heal nade spawns a healing orb (nades_spawn_orb) whose touch heals teammates (and the thrower) and
// harms enemies, plus optionally gives armor to friends. The orb itself is created by the shared
// NadeBoom.SpawnOrb helper (part A); this file supplies the per-frame touch behaviour.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The heal nade detonation — port of <c>nade_heal_boom</c>.</summary>
public sealed class NadeHealBoom : INadeBoom
{
    public string NadeNetName => "heal";

    /// <summary>QC <c>nade_heal_boom</c>: spawn the heal orb and install <see cref="HealTouch"/>.</summary>
    public void Boom(Entity nade)
    {
        Entity orb = NadeBoom.SpawnOrb(nade,
            NadeProjectile.Cvar("g_nades_heal_time", 10f),
            NadeProjectile.Cvar("g_nades_heal_radius", 300f));
        orb.Touch = HealTouch;
    }

    /// <summary>
    /// Port of <c>nade_heal_touch(entity this, entity toucher)</c> (heal.qc:7): heal teammates/self (positive
    /// rate, capped at healthmega), harm enemies (negative rate -> Damage with DEATH_NADE_HEAL), and give
    /// armor to friends/self. <c>frametime * 0.5</c> matches QC (the orb is a SOLID_TRIGGER touched each move).
    /// </summary>
    private static void HealTouch(Entity orb, Entity toucher)
    {
        if (Api.Services is null) return;
        bool isPlayer = (toucher.Flags & EntFlags.Client) != 0;
        bool isMonster = (toucher.Flags & EntFlags.Monster) != 0;
        if ((!isPlayer && !isMonster) || NadeOrbHelper.IsDeadOrFrozen(toucher))
            return;

        Entity? owner = orb.RealOwner;
        float ft = Api.Clock.FrameTime;

        float healthFactor = NadeProjectile.Cvar("g_nades_heal_rate", 15f) * ft * 0.5f;
        if (!ReferenceEquals(toucher, owner))
            healthFactor *= NadeOrbHelper.SameTeam(toucher, owner)
                ? NadeProjectile.Cvar("g_nades_heal_friend", 1f)
                : NadeProjectile.Cvar("g_nades_heal_foe", -4f); // NadeProjectile.Cvar keys on unset, so -4 survives

        if (healthFactor > 0f)
        {
            float maxHealth = isMonster ? toucher.MaxHealth : NadeProjectile.Cvar("g_pickup_healthmega_max", 200f);
            if (toucher.GetResource(ResourceType.Health) < maxHealth)
            {
                // QC: if (this.nade_show_particles) Send_Effect(EFFECT_HEALING, ...) — render-only, omitted.
                toucher.GiveResourceWithLimit(ResourceType.Health, healthFactor, maxHealth);
            }
        }
        else if (healthFactor < 0f)
        {
            Combat.Damage(toucher, orb, owner, -healthFactor, NadeDeathTypes.Heal, toucher.Origin, Vector3.Zero);
        }

        // QC: armor to friends/self only (SAME_TEAM(toucher, this) — note QC checks the orb's team here).
        float armorFactor = NadeProjectile.Cvar("g_nades_heal_armor_rate", 0f) * ft * 0.5f;
        if (!ReferenceEquals(toucher, owner))
            armorFactor *= NadeProjectile.Cvar("g_nades_heal_friend", 1f);

        if (armorFactor > 0f && NadeOrbHelper.SameTeamOrb(toucher, orb))
        {
            float maxArmor = NadeProjectile.Cvar("g_pickup_armormega_max", 200f);
            if (toucher.GetResource(ResourceType.Armor) < maxArmor)
                toucher.GiveResourceWithLimit(ResourceType.Armor, armorFactor, maxArmor);
        }
    }
}
