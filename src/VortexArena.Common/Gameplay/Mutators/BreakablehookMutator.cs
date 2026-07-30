// Port of common/mutators/mutator/breakablehook/sv_breakablehook.qc

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Breakable Hook mutator — port of common/mutators/mutator/breakablehook/sv_breakablehook.qc. Makes the
/// grappling-hook chain shootable: shooting an enemy's hook chain destroys it (and deals a little splash
/// damage to its owner), while your own hook (and a teammate's) is left alone unless
/// <c>g_breakablehook_owner</c> lets you break your own. Enabled by the <c>g_breakablehook</c> cvar.
///
/// Ported (the Damage_Calculate handler): when the damaged entity is the grapple chain
/// (<c>classname == "grapplinghook"</c>), zero the damage if <c>!g_breakablehook</c> or (your own hook AND
/// !g_breakablehook_owner); and if the attacker is on a DIFFERENT team from the hook's owner, hurt the owner
/// for 5 (WEP_HOOK | HITTYPE_SPLASH) and remove the hook. The port's grapple IS a shootable
/// <c>"grapplinghook"</c> entity (Hook.cs: classname "grapplinghook", takedamage Aim, a ProjectileDamage
/// callback), so this handler is live; the hook is removed by triggering its own ProjectileDamage callback
/// (the C# successor to QC's RemoveHook, which Hook.cs keeps private).
/// </summary>
[Mutator]
public sealed class BreakablehookMutator : MutatorBase
{
    /// <summary>QC autocvar_g_breakablehook — read live each Damage_Calculate (the .qc comment "allow toggling
    /// mid match?" makes the live cadence explicit).</summary>
    public bool Breakable => Api.Services is not null && Api.Cvars.GetFloat("g_breakablehook") != 0f;
    /// <summary>QC autocvar_g_breakablehook_owner — allow breaking your OWN hook; read live each
    /// Damage_Calculate so mid-match toggling takes effect immediately, as in Base.</summary>
    public bool BreakableOwner => Api.Services is not null && Api.Cvars.GetFloat("g_breakablehook_owner") != 0f;

    public BreakablehookMutator() => NetName = "breakablehook";

    // QC: REGISTER_MUTATOR(breakablehook, cvar("g_breakablehook"));
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_breakablehook") != 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
    }

    // MUTATOR_HOOKFUNCTION(breakablehook, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity target = args.Target;
        if (target.ClassName != "grapplinghook") return false;

        Entity? attacker = args.Attacker;
        Entity? owner = target.RealOwner;

        // Zero the damage if breaking is off, or if it's your own hook and owner-breaking is off.
        if (!Breakable || (!BreakableOwner && attacker is not null && ReferenceEquals(attacker, owner)))
            args.Damage = 0f;

        // Hurt the owner of the hook (and remove it) when the attacker is on a DIFFERENT team.
        // QC DIFF_TEAM(a,b) = teamplay ? (a.team != b.team) : (a != b); the FFA branch (a != b)
        // skips a self-hit (owner shooting own hook), so this punish never fires on yourself in FFA.
        if (attacker is not null && owner is not null && DiffTeam(attacker, owner))
        {
            // Damage(hook.realowner, attacker, attacker, 5, WEP_HOOK | HITTYPE_SPLASH, ..., owner.origin, '0 0 0')
            string dt = DeathTypes.WithHitType(DeathTypes.FromWeapon("hook"), DeathTypes.Splash);
            Combat.Damage(owner, attacker, attacker, 5f, dt, owner.Origin, Vector3.Zero);

            // RemoveHook(frag_target): the port's Hook keeps RemoveHook private, but the grapple installs a
            // ProjectileDamage callback that drops the chain when it's shot down — fire it to remove the hook.
            target.ProjectileDamage?.Invoke(target, attacker);
        }
        return false;
    }

    // QC common/teams.qh DIFF_TEAM(a,b): teamplay ? (a.team != b.team) : (a != b).
    //
    // We must NOT key this off the static GameScores.Teamplay flag: that is the CLIENT scoreboard
    // global (only written by the dead ScoreInfoBlock.Apply deserialise) and is ALWAYS false on the
    // server, which made a TEAMMATE's hook-break wrongly punish the teammate (the FFA branch a != b
    // is true for two distinct allies). Instead derive both DIFF_TEAM cases from the live Entity.Team
    // value via Teams.SameTeam (the same authoritative server-side team check the damage pipeline uses
    // for friendly-fire, DamageSystem.cs:177). In a team game Team is nonzero, so SameTeam handles the
    // same-team case (no punish) and entity-inequality handles self; in FFA every player is Team 0, so
    // SameTeam is always false and this reduces to the QC FFA branch (a != b). This reproduces all four
    // DIFF_TEAM truth-table rows (teamplay same/diff, FFA enemy/self) without the dead static.
    private static bool DiffTeam(Entity a, Entity b) =>
        !ReferenceEquals(a, b) && !Teams.SameTeam(a, b);
}
