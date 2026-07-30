// Port of common/mutators/mutator/vampirehook/sv_vampirehook.qc

using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vampire Hook mutator — port of common/mutators/mutator/vampirehook/sv_vampirehook.qc. While a player is
/// hooked to an ENEMY with the grappling hook, the hook drains the victim's health every
/// <c>g_vampirehook_damagerate</c> seconds and heals the hook's owner. (With <c>g_vampirehook_teamheal</c> a
/// teammate can be hooked to heal them, draining the owner instead.) Enabled by the <c>g_vampirehook</c> cvar.
///
/// Ported: the GrappleHookThink hook, the three entry gates (damage non-zero, per-hook <c>last_dmg</c> debounce,
/// and the <c>time &lt; game_starttime</c> pre-match block), the owner/aiment + frozen + team + alive predicate,
/// the <c>Damage(..WEP_HOOK..)</c> drain on the chosen damage entity, the <c>Heal(...)</c> on the chosen heal
/// target (faithful <c>Heal</c>/<c>PlayerHeal</c> guards + dead-target floor, see <see cref="HealPlayer"/>),
/// and the team-heal self-drain. The hooked-player <see cref="Entity.Aiment"/> is set on any direct player latch
/// by GrapplingHookTouch (Hook.cs, fixed in Wave-2), so the drain is reachable on the live path.
///
/// Known omission: <c>hitsound_damage_dealt</c> is a HUD/stats accumulator the port doesn't carry, so the
/// owner's per-tick hit-confirm "ding" is omitted (cosmetic). The QC <c>DMG_NOWEP</c> weaponentity-slot flag on
/// the Damage call has no analogue in the port's deathtype-string model (no weaponentity is threaded through
/// <c>Combat.Damage</c>), so the drain kill is attributed as a normal hook-weapon kill.
/// </summary>
[Mutator]
public sealed class VampireHookMutator : MutatorBase
{
    /// <summary>QC autocvar_g_vampirehook_teamheal — hooking a teammate heals them (drains the owner).</summary>
    public bool TeamHeal;

    /// <summary>QC autocvar_g_vampirehook_damage — health drained per tick.</summary>
    public float Damage;

    /// <summary>QC autocvar_g_vampirehook_damagerate — seconds between drain ticks (mutators.cfg:427 default 0.2).</summary>
    public float DamageRate = 0.2f;

    /// <summary>QC autocvar_g_vampirehook_health_steal — health the owner gains per tick.</summary>
    public float HealthSteal;

    public VampireHookMutator() => NetName = "vampirehook";

    // QC: REGISTER_MUTATOR(vh, expr_evaluate(autocvar_g_vampirehook)) — g_vampirehook is a string.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_vampirehook"));

    // Per-hook last_dmg debounce (QC .float last_dmg on the hook edict). Keyed by the hook entity (GC-safe).
    private static readonly ConditionalWeakTable<Entity, float[]> _lastDmg = new();

    private HookHandler<MutatorHooks.GrappleHookThinkArgs>? _onGrappleThink;

    public override void Hook()
    {
        _onGrappleThink ??= OnGrappleHookThink;
        MutatorHooks.GrappleHookThink.Add(_onGrappleThink);

        if (Api.Services is not null)
        {
            TeamHeal = Api.Cvars.GetFloat("g_vampirehook_teamheal") != 0f;
            Damage = Api.Cvars.GetFloat("g_vampirehook_damage");
            float dr = Api.Cvars.GetFloat("g_vampirehook_damagerate");
            if (dr != 0f) DamageRate = dr;
            HealthSteal = Api.Cvars.GetFloat("g_vampirehook_health_steal");
        }
    }

    public override void Unhook()
    {
        if (_onGrappleThink is not null) MutatorHooks.GrappleHookThink.Remove(_onGrappleThink);
    }

    // MUTATOR_HOOKFUNCTION(vh, GrappleHookThink)
    private bool OnGrappleHookThink(ref MutatorHooks.GrappleHookThinkArgs args)
    {
        if (Api.Services is null) return false;
        Entity thehook = args.Hook;
        float time = Api.Clock.Time;

        float[] slot = _lastDmg.GetValue(thehook, static _ => new float[1]);

        // QC: if (!autocvar_g_vampirehook_damage || thehook.last_dmg > time || time < game_starttime) return;
        // game_starttime = the match-start clock (drain is blocked during the pre-match warmup/countdown, NOT at
        // match end). Sourced from the host-wired StartItem.GameStartTimeProvider (the same seam DamageSystem uses
        // for its own game_starttime gate); 0 when unwired, so the gate is a no-op outside a live match.
        float gameStartTime = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
        if (Damage == 0f || slot[0] > time || time < gameStartTime) return false;

        Entity? owner = thehook.RealOwner;
        Entity? aiment = thehook.Aiment; // set to the latched player by GrapplingHookTouch on a direct hit (Hook.cs).
        if (owner is null || aiment is null) return false;

        // QC: if (hook_owner != hook_aiment && IS_PLAYER(hook_aiment) && !STAT(FROZEN, hook_aiment)
        //         && (DIFF_TEAM(hook_owner, hook_aiment) || teamheal) && GetResource(hook_aiment, RES_HEALTH) > 0)
        bool aimentIsPlayer = (aiment.Flags & EntFlags.Client) != 0;
        bool aimentFrozen = StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(aiment, fz);
        bool diffTeam = !Teams.SameTeam(owner, aiment);
        if (ReferenceEquals(owner, aiment) || !aimentIsPlayer || aimentFrozen
            || !(diffTeam || TeamHeal) || aiment.GetResource(ResourceType.Health) <= 0f)
            return false;

        // QC: thehook.last_dmg = time + damagerate;
        slot[0] = time + DamageRate;

        // QC: dmgent = (SAME_TEAM(owner, aiment) && teamheal) ? hook_owner : hook_aiment;
        bool sameTeam = Teams.SameTeam(owner, aiment);
        Entity dmgent = (sameTeam && TeamHeal) ? owner : aiment;

        // QC: Damage(dmgent, thehook, hook_owner, damage, WEP_HOOK.m_id, DMG_NOWEP, thehook.origin, '0 0 0');
        Combat.Damage(dmgent, thehook, owner, Damage, DeathTypes.FromWeapon("hook"), thehook.Origin, Vector3.Zero);

        // QC: targ = SAME_TEAM(owner, aiment) ? hook_aiment : hook_owner; Heal(targ, owner, health_steal, healthsmall_max);
        Entity targ = sameTeam ? aiment : owner;
        float healthSmallMax = Cvar("g_pickup_healthsmall_max", 5f);
        HealPlayer(targ, HealthSteal, healthSmallMax);

        // QC: if(dmgent == hook_owner) TakeResource(dmgent, RES_HEALTH, damage); // FIXME: friendly fire?!
        if (ReferenceEquals(dmgent, owner))
            owner.TakeResource(ResourceType.Health, Damage);
        return false;
    }

    /// <summary>
    /// QC <c>Heal(targ, owner, amount, limit)</c> (server/damage.qc:948) → <c>event_heal = PlayerHeal</c>
    /// (server/player.qc:615) for a player target. Reproduces both the <c>Heal</c> guards
    /// (<c>game_stopped</c> / spectator / FROZEN / IS_DEAD) AND <c>PlayerHeal</c>'s
    /// <c>hlth &lt;= 0 || hlth &gt;= limit</c> floor, then the capped give — instead of a raw
    /// GiveResourceWithLimit (which would heal a dead/frozen/spectating target). The non-player /
    /// objective <c>event_heal</c> branch isn't reachable here (the heal target is always a player), so
    /// it's not modelled.
    /// </summary>
    private static void HealPlayer(Entity targ, float amount, float limit)
    {
        // QC Heal(): if (game_stopped || (IS_CLIENT && killcount == FRAGS_SPECTATOR) || FROZEN || IS_DEAD) return false;
        if (VehicleCommon.GameStopped) return;
        if (targ is Player p && p.FragsStatus == Player.FragsSpectator) return;
        if (StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(targ, fz)) return;
        if (targ.DeadState != DeadFlag.No) return;

        // QC PlayerHeal(): if (hlth <= 0 || hlth >= limit) return false; — no-op on a dead target or one already
        // at/above the small-health cap (the dead-target floor the raw give lacked).
        float hlth = targ.GetResource(ResourceType.Health);
        if (hlth <= 0f || hlth >= limit) return;

        // QC GiveResourceWithLimit(targ, RES_HEALTH, amount, limit) — caps the post-give total at limit.
        targ.GiveResourceWithLimit(ResourceType.Health, amount, limit);
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
