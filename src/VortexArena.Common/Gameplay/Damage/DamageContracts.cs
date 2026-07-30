using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay.Damage;

/// <summary>
/// A damage event (QC <c>Damage(targ, inflictor, attacker, damage, deathtype, hitloc, force)</c>,
/// server/damage.qc). <see cref="DeathType"/> is a string tag (e.g. the attacking weapon's NetName,
/// "fall", "drown", "void") — a lightweight stand-in for the QC deathtype registry for now.
/// </summary>
public struct DamageInfo
{
    public Entity Target;
    public Entity? Inflictor;   // the projectile/entity that dealt it
    public Entity? Attacker;    // the player credited
    public float Amount;
    public string DeathType;
    public Vector3 HitLocation;
    public Vector3 Force;       // knockback impulse
}

/// <summary>Fired when an entity is killed (QC obituary / frag scoring hook). Gametypes subscribe to score frags.</summary>
public struct DeathEvent
{
    public Entity Victim;
    public Entity? Attacker;
    public Entity? Inflictor;
    public string DeathType;
}

/// <summary>The damage pipeline (armor/health split, force/knockback, Killed handling). Installed onto <see cref="Combat"/>.</summary>
public interface IDamageSystem
{
    /// <summary>Apply damage. Returns the damage actually dealt after armor/modifiers.</summary>
    float Apply(in DamageInfo info);
}

/// <summary>
/// Ambient access to the damage pipeline + the death hook bus. Weapons/gametypes call
/// <see cref="Damage"/>; the installed <see cref="IDamageSystem"/> fires <see cref="Death"/> on a kill.
/// </summary>
public static class Combat
{
    private sealed class NoDamage : IDamageSystem
    {
        public float Apply(in DamageInfo info) => 0f;  // no-op until installed
    }

    public static IDamageSystem System { get; set; } = new NoDamage();

    /// <summary>The obituary/kill hook (gametypes subscribe to award frags). Fired by the damage system.</summary>
    public static readonly HookChain<DeathEvent> Death = new();

    public static float Damage(Entity target, Entity? inflictor, Entity? attacker,
        float amount, string deathType, Vector3 hitLocation, Vector3 force)
        => System.Apply(new DamageInfo
        {
            Target = target,
            Inflictor = inflictor,
            Attacker = attacker,
            Amount = amount,
            DeathType = deathType,
            HitLocation = hitLocation,
            Force = force,
        });

    /// <summary>
    /// QC <c>Heal(targ, inflictor, amount, limit)</c> (server/damage.qc:948): the central heal dispatcher —
    /// symmetric counterpart to <c>Damage()</c>. Rejects heals when the target is freed, a spectator
    /// (TakeDamage==No), frozen (STAT_FROZEN or STATUSEFFECT_Frozen), or dead. Dispatches to
    /// <see cref="Entity.GtEventHeal"/> when the target carries one (an Onslaught generator / control-point
    /// icon sets it to ons_GeneratorHeal / ons_ControlPoint_Icon_Heal, a func_assault_destructible sets it to
    /// destructible_heal), else returns false (QC: <c>bool healed = targ.event_heal ? … : false</c>).
    /// Returns true if any health was added.
    ///
    /// Note: QC also rejects when <c>game_stopped</c> is true; in this port that gate lives at the gametype
    /// caller layer (the same pragmatic split as the <c>Damage()</c> dispatcher), so it is not checked here.
    /// </summary>
    public static bool Heal(Entity target, Entity? inflictor, float amount, float limit)
    {
        if (target.IsFreed)
            return false;

        // QC damage.qc:951: IS_CLIENT(targ) && CS(targ).killcount == FRAGS_SPECTATOR — spectator/observer guard.
        // Port: a spectator's TakeDamage is set to DamageMode.No by MakePlayerObserver (matching the QC
        // DAMAGE_NO set on observers); this covers the killcount==FRAGS_SPECTATOR case faithfully.
        if (target.TakeDamage == DamageMode.No)
            return false;

        // QC damage.qc:952: STAT(FROZEN, targ) — frozen players cannot receive heals (they must be thawed first).
        bool isFrozen = target.FrozenStat != 0
            || (XonoticGodot.Common.Gameplay.StatusEffectsCatalog.Frozen is { } fz
                && XonoticGodot.Common.Gameplay.StatusEffectsCatalog.Has(target, fz));
        if (isFrozen)
            return false;

        // QC damage.qc:952: IS_DEAD(targ) — dead entities cannot be healed.
        if (target.DeadState != DeadFlag.No)
            return false;

        // QC damage.qc:956-958: bool healed = (targ.event_heal) ? targ.event_heal(targ, inflictor, amount, limit) : false;
        return target.GtEventHeal?.Invoke(target, inflictor, amount, limit) ?? false;
    }
}
