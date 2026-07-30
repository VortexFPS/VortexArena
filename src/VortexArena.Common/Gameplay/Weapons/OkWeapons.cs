// Port of the shared Overkill-weapon secondary "blaster jump" (the common wr_think tail of
// common/mutators/mutator/overkill/ok{machinegun,shotgun,nex,hmg,rpc}.qc).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared helper for the five Overkill weapons. Every ok* weapon's <c>wr_think</c> opens with the same
/// secondary-fire branch: <c>if (refire_type == 1 &amp;&amp; (fire &amp; 2) &amp;&amp; time &gt;= actor.jump_interval)
/// { actor.jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) * W_WeaponRateFactor(actor);
/// W_Blaster_Attack(actor, weaponentity); ... }</c> — the "Overkill blaster jump": a damage/force-less
/// Blaster shot fired on a dedicated per-PLAYER refire timer (<c>actor.jump_interval</c>), used to launch
/// yourself around. (The Blaster's damage/force are nullified by the Overkill mutator's Damage_Calculate.)
///
/// This factors that shared tail into one faithful place so the five weapon files stay 1:1 with their .qc and
/// don't each re-implement it. The blaster is fired ungated via <see cref="Blaster.FirePrimaryDirect"/> (it
/// owns its own timer here, exactly like the OK weapons own <c>jump_interval</c>); the gate is
/// <c>actor.JumpInterval</c> (QC's <c>actor.jump_interval</c>, the player edict field) advanced by the
/// blaster's primary refire scaled by the weapon rate factor.
/// </summary>
internal static class OkWeapons
{
    /// <summary>
    /// Run the shared Overkill secondary blaster-jump for <paramref name="actor"/> on this tick. Handles both
    /// <paramref name="secondaryRefireType"/> == 1 (blaster on the dedicated per-player jump_interval timer) and
    /// <paramref name="secondaryRefireType"/> == 0 (blaster on the shared ATTACK_FINISHED timer via PrepareAttack).
    ///
    /// <para>QC source — each ok*.qc wr_think head (all five weapons share this pattern):</para>
    /// <code>
    /// // refire_type 1: own jump_interval
    /// if (refire_type == 1 &amp;&amp; (fire &amp; 2) &amp;&amp; time &gt;= actor.jump_interval) {
    ///     actor.jump_interval = time + blaster_refire * W_WeaponRateFactor;
    ///     W_Blaster_Attack(actor, weaponentity);
    ///     animdecide_setaction(actor, ANIMACTION_SHOOT, true);   // latches the SHOOT torso overlay
    /// }
    /// // refire_type 0: shared ATTACK_FINISHED
    /// if ((fire &amp; 2) &amp;&amp; refire_type == 0) {
    ///     if (!weapon_prepareattack(thiswep, actor, weaponentity, false, blaster_refire)) return;
    ///     W_Blaster_Attack(actor, weaponentity);
    ///     weapon_thinkf(actor, weaponentity, WFRAME_FIRE2, blaster_animtime, w_ready);
    /// }
    /// </code>
    ///
    /// The QC default is 1 for all five OK weapons at stock balance, so the refire_type==0 path is NOT
    /// used at stock balance. <c>animdecide_setaction(actor, ANIMACTION_SHOOT, true)</c> is ported on the
    /// refire_type==1 path (the SHOOT torso overlay is latched via <see cref="AnimDecide.SetAction"/>); on the
    /// refire_type==0 path the overlay is latched by <c>PrepareAttack</c>. The networked, expiry-resolved
    /// projection of that action drives the remote third-person fire pose (the same overlay used by primary fire).
    /// </summary>
    /// <param name="refireType0AmmoCheck">
    /// QC's <c>secondary</c> arg of <c>weapon_prepareattack</c> on the refire_type==0 path: okmachinegun and
    /// oknex pass <c>true</c> (secondary/blaster ammo check), okhmg/okrpc/okshotgun pass <c>false</c> (primary
    /// ammo check). Only consulted when <paramref name="secondaryRefireType"/> == 0 (non-default).
    /// </param>
    public static void FireSecondaryBlasterJump(Weapon caller, Entity actor, WeaponSlot slot, FireMode fire,
        int secondaryRefireType, FireMode refireType0AmmoCheck = FireMode.Primary)
    {
        if (Api.Services is null) return;
        if (fire != FireMode.Secondary) return;

        // QC: (fire & 2) is decided by the driver calling WrThink(Secondary) only when ATCK2 is held; mirror by
        // checking the per-slot secondary button so a non-pressing tick (the per-frame upkeep call) doesn't fire.
        if (!actor.WeaponState(slot).ButtonAttack2) return;

        if (Weapons.ByName("blaster") is not Blaster blaster) return;
        float blasterRefire = BlasterRefire(blaster);

        if (secondaryRefireType == 1)
        {
            // QC: if (refire_type == 1 && (fire & 2) && time >= actor.jump_interval) { ... }
            float now = Api.Clock.Time;
            if (now < actor.JumpInterval) return;

            // actor.jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) * W_WeaponRateFactor(actor);
            actor.JumpInterval = now + blasterRefire * WeaponRate();

            // makevectors(actor.v_angle); W_Blaster_Attack(actor, weaponentity);
            blaster.FirePrimaryDirect(actor, slot);

            // animdecide_setaction(actor, ANIMACTION_SHOOT, true) — latch the torso SHOOT overlay so
            // third-person observers see a fire animation on the secondary blaster-jump. FireSecondaryBlasterJump
            // bypasses PrepareAttack on this path, so the overlay (which PrepareAttack latches for the primary
            // fire) must be set here explicitly. restart:true mirrors QC restartanim=true: a rapid jump-spam
            // restarts the 0.2s SHOOT window each shot, matching held-trigger primary behaviour.
            var (jumpAct, jumpStart) = AnimDecide.SetAction(
                actor.AnimUpperAction, actor.AnimActionStart,
                AnimDecide.AnimUpperAction.Shoot, now, restart: true);
            actor.AnimUpperAction = jumpAct;
            actor.AnimActionStart = jumpStart;
        }
        else if (secondaryRefireType == 0)
        {
            // QC: if ((fire & 2) && refire_type == 0) {
            //   if (!weapon_prepareattack(thiswep, actor, weaponentity, <secondary>, blaster_refire)) return;
            //   W_Blaster_Attack(actor, weaponentity);
            //   weapon_thinkf(actor, weaponentity, WFRAME_FIRE2, blaster_animtime, w_ready);
            // }
            // weapon_prepareattack runs on the CALLER (the OK weapon), not the blaster itself: it advances the
            // caller's ATTACK_FINISHED timer by WEP_CVAR_PRI(WEP_BLASTER, refire) * rate. The QC trigger is
            // (fire & 2) — the SECONDARY button — so the held-button gate must be Secondary even though the
            // ammo-check MODE (refireType0AmmoCheck) is per-weapon (okmg/oknex secondary, others primary).
            if (!caller.PrepareAttack(actor, slot, refireType0AmmoCheck, attackTime: blasterRefire,
                    buttonFire: FireMode.Secondary))
                return;

            // W_Blaster_Attack(actor, weaponentity) — the blaster jump shot (damage/force zeroed by mutator).
            blaster.FirePrimaryDirect(actor, slot);
            // weapon_thinkf(actor, weaponentity, WFRAME_FIRE2, blaster_animtime, w_ready) — the animtime
            // return-to-ready is already handled by PrepareAttack above (it schedules the w_ready callback),
            // and PrepareAttack also latches the SHOOT torso overlay for the WFRAME_FIRE2 third-person pose.
        }
    }

    private static float BlasterRefire(Blaster blaster)
    {
        float r = blaster.Primary.Refire;
        return r > 0f ? r : 0.7f; // g_balance_blaster_primary_refire default
    }

    private static float WeaponRate()
    {
        if (Api.Services is null) return 1f;
        float f = Api.Cvars.GetFloat("g_weaponratefactor");
        return f > 0f ? 1f / f : 1f;
    }
}
