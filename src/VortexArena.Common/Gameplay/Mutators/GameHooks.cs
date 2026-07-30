using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The gameplay hook bus — the C# successor to QuakeC's MUTATOR_HOOKABLE / CALLHOOK chains
/// (common/mutators/base.qh, server/mutators/events.qh). Each chain is a typed
/// <see cref="HookChain{TArgs}"/>; handlers receive a <c>ref</c> args struct, which replaces QC's global
/// M_ARGV(i, ...) in/out slots with real fields (ADR-0003, specs/entity-model.md).
///
/// Mutators <see cref="MutatorBase.Hook"/> on enable and <see cref="MutatorBase.Unhook"/> on disable.
/// </summary>
public static class GameHooks
{
    /// <summary>
    /// Args for the PlayerDamage_SplitHealthArmor hook (server/mutators/events.qh
    /// EV_PlayerDamage_SplitHealthArmor). QC passed inflictor/attacker/target/force as inputs,
    /// damage-take and damage-save as in/out, and deathtype/total-damage as inputs.
    ///
    /// This phase models the fields the sample (vampire) mutator needs: attacker, target, the in/out
    /// health damage (<see cref="DamageTake"/>) and armor damage (<see cref="DamageSave"/>), and deathtype.
    /// </summary>
    public struct PlayerDamageArgs
    {
        // inputs
        public readonly Entity Attacker;   // M_ARGV(1, entity)
        public readonly Entity Target;     // M_ARGV(2, entity)
        public readonly int DeathType;     // M_ARGV(6, float)

        // in/out (handlers may read and rewrite)
        public float DamageTake;           // M_ARGV(4, float) — health damage
        public float DamageSave;           // M_ARGV(5, float) — armor damage

        /// <summary>
        /// QC <c>M_ARGV(3, vector)</c> = <c>damage_force</c>: the knockback impulse for this hit. The
        /// globalforces mutator reads it (and the existing handlers leave it untouched) to spread the
        /// knockback to nearby players. In/out for parity with QC's argv slot, though no current handler
        /// rewrites it.
        /// </summary>
        public Vector3 Force;              // M_ARGV(3, vector) — damage_force

        // ----- Mayhem additions (additive — do NOT reorder/remove the fields above) -----
        // The original ctor passed deathType as a hardcoded 0 (the string→int deathtype map isn't modelled in
        // this port, and no SplitHealthArmor handler read the int slot). Mayhem's scoring handler needs three
        // more QC argv slots that the vampire/buffs/globalforces handlers never used, so they are added here
        // (purely additive) rather than changing the existing readonly fields' meaning:

        /// <summary>
        /// QC <c>M_ARGV(1, entity)</c> = the TRUE frag attacker, which may be <c>NULL</c> for a world/
        /// environmental death (where <see cref="Attacker"/> is coalesced to the target so the legacy handlers
        /// keep a non-null attacker). Mayhem branches on this: a non-null player attacker accrues damage, a null
        /// attacker is an environmental suicide credited against the victim.
        /// </summary>
        public readonly Entity? FragAttacker;

        /// <summary>QC <c>M_ARGV(6, float)</c> = <c>deathtype</c> as the port's string tag (<see cref="DeathType"/>
        /// stays the legacy int slot). Mayhem branches on the environmental deathtypes (kill/drown/void/lava/…).</summary>
        public readonly string FragDeathType;

        /// <summary>QC <c>M_ARGV(7, float)</c> = <c>frag_damage</c>: the full incoming damage BEFORE the
        /// health/armor split (so the handler can compute the overkill excess = damage − take − save).</summary>
        public readonly float FragDamage;

        public PlayerDamageArgs(Entity attacker, Entity target, float damageTake, float damageSave,
            int deathType, Vector3 force = default)
        {
            Attacker = attacker;
            Target = target;
            DamageTake = damageTake;
            DamageSave = damageSave;
            DeathType = deathType;
            Force = force;
            FragAttacker = attacker;
            FragDeathType = Damage.DeathTypes.Generic;
            FragDamage = 0f;
        }

        /// <summary>
        /// The full QC <c>EV_PlayerDamage_SplitHealthArmor</c> argv set: the legacy
        /// (attacker/target/take/save/force) plus the Mayhem slots (the true nullable
        /// <paramref name="fragAttacker"/>, the string <paramref name="fragDeathType"/>, and the pre-split
        /// <paramref name="fragDamage"/>). <paramref name="attacker"/> should be the non-null coalesced attacker
        /// (target for a world death) so the existing handlers keep working.
        /// </summary>
        public PlayerDamageArgs(Entity attacker, Entity target, float damageTake, float damageSave,
            Vector3 force, Entity? fragAttacker, string fragDeathType, float fragDamage)
        {
            Attacker = attacker;
            Target = target;
            DamageTake = damageTake;
            DamageSave = damageSave;
            DeathType = 0;
            Force = force;
            FragAttacker = fragAttacker;
            FragDeathType = fragDeathType ?? Damage.DeathTypes.Generic;
            FragDamage = fragDamage;
        }
    }

    /// <summary>
    /// Fired as a player takes damage, after the health/armor split is computed
    /// (QC PlayerDamage_SplitHealthArmor). Used here by the vampire mutator to heal the attacker.
    /// </summary>
    public static readonly HookChain<PlayerDamageArgs> PlayerDamageSplitHealthArmor = new();
}
