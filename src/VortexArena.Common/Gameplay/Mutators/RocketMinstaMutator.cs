// Port of common/mutators/mutator/rocketminsta/sv_rocketminsta.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Rocket Minsta mutator — port of common/mutators/mutator/rocketminsta/sv_rocketminsta.qc. A sub-mode of
/// instagib (REGISTER_MUTATOR(rm, autocvar_g_instagib)) toggled live by the <c>g_rm</c> cvar: the Devastator
/// and (optionally) Electro become instant-gib weapons. Self-damage from the Devastator (and damage to a
/// thrown nade) is nullified so rocket-jumping never hurts; with <c>g_rm_laser</c> the Electro's self-damage
/// and round-not-started damage are likewise nullified; and any Devastator/Electro kill forces a gib by
/// bumping the kill damage to 1000.
///
/// Hooks: Damage_Calculate (zero the relevant self/round damage) and PlayerDies (force the gib). Both early-out
/// on <c>!g_rm</c> so the sub-mode can be flipped mid-match like QC.
///
/// NOTE (round gate): QC's Electro round-not-started branch uses <c>round_handler_IsActive() &amp;&amp;
/// !round_handler_IsRoundStarted()</c>. The port's round handler is an instance the active gametype owns and a
/// mutator can't reach it from here, so the round branch is approximated by the available <c>game_stopped</c>
/// signal (a faithful superset: a not-yet-live round reads as stopped). The self-damage branch is exact.
/// </summary>
[Mutator]
public sealed class RocketMinstaMutator : MutatorBase
{
    public RocketMinstaMutator() => NetName = "rocketminsta";

    // QC: REGISTER_MUTATOR(rm, autocvar_g_instagib) — rm rides on instagib being enabled; the per-hook bodies
    // additionally gate on autocvar_g_rm so it can be toggled during the match.
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerDies.Add(_onPlayerDies);
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
    }

    /// <summary>QC: if(!autocvar_g_rm) return; — rm sub-mode live toggle.</summary>
    private static bool RmActive() => Api.Services is not null && Api.Cvars.GetFloat("g_rm") != 0f;

    // MUTATOR_HOOKFUNCTION(rm, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (!RmActive()) return false;

        string weapon = DeathTypes.WeaponNetNameOf(args.DeathType);

        // QC: if(DEATH_ISWEAPON(.., WEP_DEVASTATOR)) if(self || target is a nade) frag_damage = 0;
        if (weapon == "devastator")
        {
            if (ReferenceEquals(args.Attacker, args.Target) || args.Target.ClassName == "nade")
                args.Damage = 0f;
        }

        // QC: if(autocvar_g_rm_laser) if(DEATH_ISWEAPON(.., WEP_ELECTRO))
        //         if(self || (round active && !round started)) frag_damage = 0;
        if (weapon == "electro" && Api.Cvars.GetFloat("g_rm_laser") != 0f)
        {
            if (ReferenceEquals(args.Attacker, args.Target) || VehicleCommon.GameStopped)
                args.Damage = 0f;
        }

        return false;
    }

    // MUTATOR_HOOKFUNCTION(rm, PlayerDies)
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (!RmActive()) return false;

        // QC: if(DEATH_ISWEAPON(.., WEP_DEVASTATOR) || DEATH_ISWEAPON(.., WEP_ELECTRO)) M_ARGV(4) = 1000;
        string weapon = DeathTypes.WeaponNetNameOf(args.DeathType);
        if (weapon == "devastator" || weapon == "electro")
            args.Damage = 1000f; // always gib if it was a vaporizer death
        return false;
    }
}
