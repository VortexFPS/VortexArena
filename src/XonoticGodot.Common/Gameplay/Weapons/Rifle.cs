using System;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Rifle (Nexuiz "Camping Rifle"/"Sniper Rifle") — port of common/weapons/weapon/rifle.{qh,qc}. A
/// hitscan weapon whose bullets traverse the map instantly and pierce walls. Primary fires one powerful
/// bullet; secondary fires a few weaker bullets at once with a little scatter. Bullets penetrate solids
/// (WEP_FLAG_PENETRATEWALLS) and fall off with distance.
///
/// Identity/attributes from rifle.qh; balance from bal-wep-xonotic.cfg (g_balance_rifle_*).
/// This port covers both fire modes' bullet fans (spread, solid-penetration multi-hit, distance falloff,
/// knockback force, the per-target headshot bbox via <see cref="WeaponFiring.Headshot"/>), the
/// rifle_accumulator/burstcost budget that gates rapid fire, AND the held-fire bullethail continuation
/// (W_Rifle_BulletHail/_Continue — keeps auto-firing while held until the burst budget runs out; OFF by
/// default in stock balance), the clip-aware magazine reload (forced + secondary-flag reload through the
/// 80-round clip via DecreaseAmmo/WrReload), and the signature FX (tr_rifle tracer, impact ricochet, brass
/// casing). Left to client input: the zoom-from-the-eye reaim (needs the zoom button, not part of the
/// headless server input).
/// </summary>
[Weapon]
public sealed class Rifle : Weapon
{
    /// <summary>Per-fire-mode balance block — QC WEP_CVAR_BOTH(WEP_RIFLE, is_primary, *).</summary>
    public struct ModeBalance
    {
        public float Ammo;             // *_ammo (bullets per trigger pull)
        public float Animtime;         // *_animtime
        public float Bullethail;       // *_bullethail (0/1: continue firing a hail while held)
        public float Burstcost;        // *_burstcost
        public float Damage;           // *_damage (per bullet)
        public float Force;            // *_force
        public float HeadshotMultiplier;// *_headshot_multiplier
        public float Refire;           // *_refire
        public int   Shots;            // *_shots (bullets per trigger pull)
        public float SolidPenetration; // *_solidpenetration
        public float Spread;           // *_spread
        public float Tracer;           // *_tracer
    }

    public ModeBalance Primary;
    public ModeBalance Secondary;

    // Shared (non-PRI/SEC) — g_balance_rifle_bursttime.
    public float BurstTime; // g_balance_rifle_bursttime

    /// <summary>g_balance_rifle_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;

    /// <summary>g_balance_rifle_secondary_reload — when set, ATCK2 triggers a reload instead of firing
    /// (QC WEP_CVAR_SEC(WEP_RIFLE, reload), rifle.qc:142-143). 0 in stock balance.</summary>
    public bool SecondaryReload;


    public Rifle()
    {
        NetName = "rifle";
        BotPickupBaseValue = 7000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Bullets;   // QC ammo_type
        DisplayName = "Rifle";
        Impulse = 7;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_PENETRATEWALLS
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.PenetrateWalls;
        Color = new Vector3(0.886f, 0.620f, 0.353f);
        ViewModel = "h_campingrifle.iqm";  // MDL_RIFLE_VIEW
        WorldModel = "v_campingrifle.md3"; // MDL_RIFLE_WORLD
        ItemModel = "g_campingrifle.md3";  // MDL_RIFLE_ITEM
    }

    public override void Configure()
    {
        BurstTime = Bal("g_balance_rifle_bursttime", 0f);

        Primary.Ammo = Bal("g_balance_rifle_primary_ammo", 10f);
        Primary.Animtime = Bal("g_balance_rifle_primary_animtime", 0.4f);
        Primary.Bullethail = Bal("g_balance_rifle_primary_bullethail", 0f);
        Primary.Burstcost = Bal("g_balance_rifle_primary_burstcost", 0f);
        Primary.Damage = Bal("g_balance_rifle_primary_damage", 80f);
        Primary.Force = Bal("g_balance_rifle_primary_force", 100f);
        Primary.HeadshotMultiplier = Bal("g_balance_rifle_primary_headshot_multiplier", 0f);
        Primary.Refire = Bal("g_balance_rifle_primary_refire", 1.2f);
        Primary.Shots = BalInt("g_balance_rifle_primary_shots", 1);
        Primary.SolidPenetration = Bal("g_balance_rifle_primary_solidpenetration", 62.2f);
        Primary.Spread = Bal("g_balance_rifle_primary_spread", 0f);
        Primary.Tracer = Bal("g_balance_rifle_primary_tracer", 1f);

        Secondary.Ammo = Bal("g_balance_rifle_secondary_ammo", 10f);
        Secondary.Animtime = Bal("g_balance_rifle_secondary_animtime", 0.3f);
        Secondary.Bullethail = Bal("g_balance_rifle_secondary_bullethail", 0f);
        Secondary.Burstcost = Bal("g_balance_rifle_secondary_burstcost", 0f);
        Secondary.Damage = Bal("g_balance_rifle_secondary_damage", 20f);
        Secondary.Force = Bal("g_balance_rifle_secondary_force", 50f);
        Secondary.HeadshotMultiplier = Bal("g_balance_rifle_secondary_headshot_multiplier", 0f);
        Secondary.Refire = Bal("g_balance_rifle_secondary_refire", 0.9f);
        Secondary.Shots = BalInt("g_balance_rifle_secondary_shots", 4);
        Secondary.SolidPenetration = Bal("g_balance_rifle_secondary_solidpenetration", 15.5f);
        Secondary.Spread = Bal("g_balance_rifle_secondary_spread", 0.04f);
        Secondary.Tracer = Bal("g_balance_rifle_secondary_tracer", 0f);

        SecondaryEnabled = BalBool("g_balance_rifle_secondary", true);
        SecondaryReload = BalBool("g_balance_rifle_secondary_reload", false);
    }

    // QC wr_reload passes min(WEP_CVAR_PRI ammo, WEP_CVAR_SEC ammo) as W_Reload's sent_ammo_min (rifle.qc:179):
    // the cheapest per-shot cost is the floor below which a reload is pointless. Both modes cost 10 here.
    protected override float ReloadingAmmoMin() => MathF.Min(Primary.Ammo, Secondary.Ammo);

    // METHOD(Rifle, wr_resetplayer) — common/weapons/weapon/rifle.qc: on (re)spawn reset the burst budget so a
    // fresh life starts with a full window (rifle_accumulator = time - bursttime). QC resets it across every
    // weapon slot; the port has no respawn-wide reset hook, so — like Vortex/OkNex's charge seed — this runs in
    // wr_setup (switch-in, live-dispatched by WeaponFireDriver on raise/spawn). The accumulator is per-slot and
    // self-corrects via the Bound() at the top of WrThink, so seeding the current slot here is faithful (inert
    // at stock bursttime 0, where time - 0 == time already bounds it). A switch away+back re-seeds (benign).
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        actor.WeaponState(slot).RifleAccumulator = Api.Clock.Time - BurstTime;
    }

    // METHOD(Rifle, wr_aim) — common/weapons/weapon/rifle.qc. The "riflemooth" bot toggle: bots mostly fire the
    // single hard-hitting primary, but occasionally flip to the 4-bullet scatter secondary at closer ranges.
    //   * beyond 1000 units -> force primary (the secondary's spread is useless at range): clear the toggle.
    //   * while preferring primary  -> 1% chance per decision to flip to secondary (random() < 0.01).
    //   * while preferring secondary -> 3% chance per decision to flip back to primary (random() < 0.03).
    // QC drives bot_aim with (1000000, 0, 0.001) — effectively "always in range, no spread tolerance, tiny
    // think time"; the shot-aim itself is handled by the generic BotAim, so here we only own the pri/sec pick.
    // QC presses the button for the CURRENT toggle state, then rolls the flip for the NEXT decision — so the
    // returned button reflects the pre-flip state and the random() only changes which mode the bot prefers
    // going forward. Returning true routes the shot onto the secondary button (BotBrain dispatch).
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        if (enemyDistance > 1000f)
            ctx.SecondaryToggle = false; // vdist(... > 1000) -> bot_secondary_riflemooth = false

        bool fireSecondary = ctx.SecondaryToggle; // the button QC presses THIS frame (pre-flip)
        if (!ctx.SecondaryToggle)
        {
            if (ctx.Random01 < 0.01f) // random() < 0.01 -> start preferring secondary next time
                ctx.SecondaryToggle = true;
        }
        else
        {
            if (ctx.Random01 < 0.03f) // random() < 0.03 -> go back to preferring primary next time
                ctx.SecondaryToggle = false;
        }
        return fireSecondary;
    }

    // METHOD(Rifle, wr_think) — common/weapons/weapon/rifle.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Forced reload (rifle.qc:125-129): if reloading is enabled (reload_ammo != 0) and the clip has dropped
        // below the cheapest per-shot cost, reload before doing anything else. ReloadingAmmo() resolves
        // g_balance_rifle_reload_ammo (80); the clip is seeded full by WeaponFireDriver on raise and drained by
        // FireBullets->DecreaseAmmo, so this branch is what finally fires the rifle's 2s magazine reload.
        if (ReloadingAmmo() != 0f && st.ClipLoad < MathF.Min(Primary.Ammo, Secondary.Ammo))
        {
            WrReload(actor, slot);
            return;
        }

        // rifle_accumulator: a burst budget that fills over `bursttime`. Each shot costs `burstcost`; you
        // can't fire faster than the budget allows (this is what lets a few rapid shots then forces a pause).
        st.RifleAccumulator = QMath.Bound(Api.Clock.Time - BurstTime, st.RifleAccumulator, Api.Clock.Time);

        if (fire == FireMode.Primary)
        {
            // Two gates, both faithful to QC: the refire (weapon_prepareattack) AND the burst-budget
            // accumulator (you can't fire faster than the budget refills). The accumulator test MUST stay
            // FIRST so PrepareAttack doesn't advance the refire timer when the burst budget is exhausted
            // (QC rifle.qc:133-138 runs the accumulator gate between prepareattack_check and _do).
            if (Api.Clock.Time >= st.RifleAccumulator + Primary.Burstcost && PrepareAttack(actor, slot, fire))
            {
                FireBullets(actor, slot, Primary, secondary: false);
                st.RifleAccumulator += Primary.Burstcost;
                // bullethail (W_Rifle_BulletHail): when set, keep auto-firing while held until the budget
                // runs out (QC rifle.qc:137 passes WEP_CVAR_PRI bullethail). When off, this volley is it.
                BulletHail(actor, slot, secondary: false);
            }
        }
        else if (fire == FireMode.Secondary && SecondaryEnabled)
        {
            // QC rifle.qc:142-143: when secondary `reload` is set, ATCK2 triggers a manual reload instead of
            // firing. 0 in stock balance (secondary fires), so this is normally skipped.
            if (SecondaryReload)
            {
                WrReload(actor, slot);
            }
            else if (Api.Clock.Time >= st.RifleAccumulator + Secondary.Burstcost && PrepareAttack(actor, slot, fire))
            {
                FireBullets(actor, slot, Secondary, secondary: true);
                st.RifleAccumulator += Secondary.Burstcost;
                // QC rifle.qc:148: the SECONDARY hail uses the SECONDARY bullethail flag but the PRIMARY
                // refire (preserved as a quirk inside BulletHailContinue's refire choice).
                BulletHail(actor, slot, secondary: true);
            }
        }
    }

    // W_Rifle_BulletHail (common/weapons/weapon/rifle.qc:77-92): after the first volley has fired, if the
    // mode's `bullethail` is set, schedule the continuation think so the weapon keeps firing while the button
    // is held. When `bullethail` is 0 this is a no-op (the base PrepareAttack already scheduled the normal
    // return-to-READY think, == QC's `weapon_thinkf(..., w_ready)` else-branch).
    private void BulletHail(Entity actor, WeaponSlot slot, bool secondary)
    {
        ModeBalance bal = secondary ? Secondary : Primary;
        if (bal.Bullethail == 0f)
            return;

        var st = actor.WeaponState(slot);
        // weapon_thinkf(actor, fr, animtime, W_Rifle_BulletHail_Continue): replace the PrepareAttack-scheduled
        // become-READY think with the hail continuation, fired after this shot's animtime (rifle.qc:88).
        // `this` is the process-lifetime Rifle singleton, safe to capture in the scheduled think. weapon_thinkf
        // scales the delay by the weapon rate factor (weaponsystem.qc:394), so do the same.
        WeaponFireDriver.ScheduleThink(st, bal.Animtime * WeaponRateFactor(actor),
            (pl, sl) => BulletHailContinue(pl, sl, secondary));
    }

    // W_Rifle_BulletHail_Continue (common/weapons/weapon/rifle.qc:59-75): runs as the scheduled weapon think
    // each tick (the driver invokes it with the live fire buttons set). Re-fires the volley while the button
    // is held and the refire + burst budget allow, otherwise restores the saved ATTACK_FINISHED so the last
    // shot still enforces the refire time and lets the weapon return to READY.
    //
    // Adapts the QC field-mutation trick to the port's driver (see T16 spec gotchas):
    //   * QC sets m_switchweapon = m_weapon so weapon_prepareattack_check doesn't abort mid-switch; in the
    //     port the switch state machine already ran for this tick before this think (WeaponFireDriver.Frame),
    //     and PrepareAttack doesn't itself consult the switch target — so we instead gate on the slot still
    //     holding the rifle (st.CurrentWeaponId == RegistryId), which is the same intent.
    //   * QC's continuation REPLACES w_ready, so it must itself perform the "animtime elapsed -> READY"
    //     transition before PrepareAttack's WS_READY gate; we set State = Ready here for that reason.
    //   * The burst-budget accumulator gates the hail length here too (so a held hail fires until the budget
    //     is exhausted, then pauses until it refills). QC's continuation doesn't re-check the accumulator,
    //     but the burst budget is the documented mechanism that limits hail length — mirroring its intent.
    private void BulletHailContinue(Entity actor, WeaponSlot slot, bool secondary)
    {
        var st = actor.WeaponState(slot);

        // Only continue while this slot still holds the rifle (QC m_switchweapon = m_weapon guard intent).
        if (st.CurrentWeaponId != RegistryId)
            return;

        ModeBalance bal = secondary ? Secondary : Primary;
        // QC quirk (rifle.qc:148): the secondary hail uses the PRIMARY refire for the continuation timer.
        float refire = Primary.Refire;

        // The animtime elapsed -> the weapon is READY again (this think replaces w_ready).
        st.State = WeaponFireState.Ready;

        // refresh the burst budget window (QC rifle.qc:131 bound at the top of wr_think).
        st.RifleAccumulator = QMath.Bound(Api.Clock.Time - BurstTime, st.RifleAccumulator, Api.Clock.Time);
        bool budgetOk = Api.Clock.Time >= st.RifleAccumulator + bal.Burstcost;

        float af = st.AttackFinished;               // save (rifle.qc:62)
        st.AttackFinished = Api.Clock.Time;         // rifle.qc:64
        // weapon_prepareattack(..., frame==WFRAME_FIRE2, refire) — gated on the button still held + ammo +
        // refire elapsed. Use the explicit-refire overload so the secondary keeps the primary-refire quirk.
        bool fired = budgetOk
            && PrepareAttack(actor, slot, secondary ? FireMode.Secondary : FireMode.Primary, attackTime: refire);

        if (fired)
        {
            FireBullets(actor, slot, bal, secondary);
            st.RifleAccumulator += bal.Burstcost;
            // weapon_thinkf(..., W_Rifle_BulletHail_Continue): keep the hail going (rifle.qc:71).
            WeaponFireDriver.ScheduleThink(st, bal.Animtime * WeaponRateFactor(actor),
                (pl, sl) => BulletHailContinue(pl, sl, secondary));
        }
        else
        {
            // didn't fire: restore ATTACK_FINISHED so the last shot enforces the refire time (rifle.qc:74).
            st.AttackFinished = af;
        }
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks. (The rifle's burst budget is an
    // additional, independent gate enforced in WrThink.)
    public override float RefireFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Refire;
    public override float AnimtimeFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Animtime;

    // W_Rifle_FireBullet (shared by W_Rifle_Attack / _Attack2) — fire `shots` piercing hitscan bullets.
    private void FireBullets(Entity actor, WeaponSlot slot, ModeBalance bal, bool secondary)
    {
        int shots = bal.Shots;
        if (shots < 1) shots = 1;

        // W_DecreaseAmmo(thiswep, actor, ammo): clip-aware (rifle is WEP_FLAG_RELOADABLE with reload_ammo 80),
        // so this drains the magazine (clip_load) — which is what makes the wr_think forced-reload branch fire.
        // (Previously this called actor.TakeResource, draining the shared pool and pinning the clip at 80.)
        DecreaseAmmo(actor, slot, bal.Ammo);

        // Penetrate-walls trueaim: aim at the body behind glass/walls (rifle has WEP_FLAG_PENETRATEWALLS).
        // Pass wep/maxDamage so the accuracy FIRED denominator grows (QC W_SetupShot maxdamage = dmg*shots,
        // rifle.qc:12); without it the rifle never registered an accuracy% shot.
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, WeaponFiring.CurrentMaxShotDistance, penetrateWalls: true,
            wep: this, maxDamage: bal.Damage * shots, recoil: 2f);

        // QC fires W_MuzzleFlash (rifle.qc:14) BEFORE the zoom re-aim block, so the muzzle flash always plays at
        // the gun barrel (pre-zoom w_shotorg / w_shotdir*2), not the re-aimed eye origin. Capture them now.
        Vector3 flashOrigin = shot.Origin;
        Vector3 flashDir = shot.Dir;

        // Zoom-from-eye re-aim (rifle.qc:16-20): while the +zoom bind is held, shoot straight from the eye so a
        // long-range scoped shot is pixel-accurate. QC:
        //   w_shotdir = v_forward;
        //   w_shotorg = origin + view_ofs + ((w_shotorg - origin - view_ofs) * v_forward) * v_forward;
        // i.e. the muzzle origin is projected back onto the eye->forward axis (the shot leaves the eye, not the
        // offset gun barrel) and aims dead ahead. The +zoom button now arrives over the net (InputButtons.Zoom ->
        // ServerNet -> ButtonZoom on the slot). Matches Base: the secondary-zoom path stays off at stock secondary 1.
        if (actor.WeaponState(slot).ButtonZoom)
        {
            Vector3 eye = actor.Origin + actor.ViewOfs;
            Vector3 fwd = QMath.Normalize(forward);
            Vector3 reorg = eye + Vector3.Dot(shot.Origin - eye, fwd) * fwd;
            shot = new ShotInfo(reorg, fwd, reorg + fwd * WeaponFiring.CurrentMaxShotDistance);
        }

        int deathType = RegistryId;
        // QC W_Rifle_Attack2 (rifle.qc:49): m_id | HITTYPE_SECONDARY — the secondary flag is what drives
        // DeathMessages' HAIL branch (rifle.qc:187 / DeathMessages.cs:82-85). The int deathType can't pack
        // HITTYPE_SECONDARY bits, so carry it as a string deathTag (same pattern as MachineGun.FireOne's snipe
        // secondary, Machinegun.cs:364-365) so ApplyDamage's DeathTypes override path routes the kill message.
        string? deathTag = secondary
            ? Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Secondary)
            : null;
        // QC: tracer ? EFFECT_RIFLE : EFFECT_RIFLE_WEAK (rifle.qc:34). The primary's signature visible tracer.
        string tracerEffect = bal.Tracer != 0f ? "RIFLE" : "RIFLE_WEAK";
        for (int i = 0; i < shots; ++i)
        {
            // fireBullet_falloff: per-bullet spread, solid penetration multi-hit, distance falloff, force, tracer.
            // Capture the first damageable entity hit (QC's fireBullet_trace_callback / Damage_DamageInfo species
            // feed) so we can reproduce the impact-effect player-hit suppression below.
            Entity? hit = WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.CurrentMaxShotDistance,
                bal.Damage, deathType, bal.Spread, bal.SolidPenetration, force: bal.Force,
                headshotMultiplier: bal.HeadshotMultiplier, tracerEffect: tracerEffect, deathTag: deathTag);
            Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
            TraceResult impTr = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, impEnd).Trace; // [T45] warpzone-aware: impact FX land on the far side of a portal
            // w_backoff = the impact surface normal (trace_plane_normal), -force_dir fallback when no hit.
            Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
            // wr_impacteffect (rifle.qc:213-218): EFFECT_RIFLE_IMPACT puff + a random ricochet ping (CH_SHOTS).
            // Base gates the impact in the CSQC damageinfo handler (damageeffects.qc:439-460):
            //   * DEATH_ISSPECIAL is skipped here (rifle is a normal weapon).
            //   * `if (!hitplayer || rad)` — for a hitscan bullet (rad == 0) the ground-impact puff + ricochet are
            //     SUPPRESSED when the shot actually hit a player model (you don't get a wall-spark + ric off flesh).
            //     The port emits server-side per bullet, so reproduce that gate here: skip the FX when this bullet
            //     hit a damageable player. A wall/nothing hit still puffs + rics.
            //   * a sky surface (or no surface) is silent — matches the MachineGun sibling's `silent` gate.
            bool hitPlayer = hit is not null && (hit.Flags & EntFlags.Client) != 0;
            bool silent = (impTr.DpHitQ3SurfaceFlags & WeaponFiring.Q3SurfaceFlagSky) != 0 || impTr.Fraction >= 1f;
            if (!hitPlayer)
                WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "RIFLE_IMPACT", silent);
        }

        // QC: SpawnCasing (casing type 3) when g_casings >= 2 (rifle.qc:38-42); EjectCasing applies the gate.
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Bullet);

        // SND_RIFLE_FIRE (primary) / SND_RIFLE_FIRE2 (secondary) — rifle.qc:47/52, on CH_WEAPON_A.
        Api.Sound.Play(actor, SoundChannel.Weapon,
            secondary ? "weapons/campingrifle_fire2.wav" : "weapons/campingrifle_fire.wav");
        // QC W_MuzzleFlash(thiswep, actor, w_shotorg, w_shotdir * 2) at rifle.qc:14 — the PRE-zoom origin/dir.
        EffectEmitter.Emit("RIFLE_MUZZLEFLASH", flashOrigin, flashDir * 1000f, 1, except: actor);
    }

    // METHOD(Rifle, wr_checkammo1) — rifle.qc:154-159: have ammo if the shared pool OR the persistent magazine
    // (weapon_load[id], reloadable) can cover one primary shot. The clip term was missing, so a player with a
    // loaded magazine but a drained pool wrongly read as out-of-ammo.
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= Primary.Ammo
        || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Primary.Ammo;

    // METHOD(Rifle, wr_checkammo2) — rifle.qc:161-166: pool OR magazine, for the secondary shot cost.
    public bool CheckAmmoSecondary(Entity actor)
        => actor.GetResource(AmmoType) >= Secondary.Ammo
        || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Secondary.Ammo;
}
