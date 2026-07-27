using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The MachineGun (Nexuiz "Uzi") — port of common/weapons/weapon/machinegun.{qh,qc}. A hitscan
/// bullet weapon that fires fast with a small, accumulating spread. Two fire modes (g_balance_machinegun_mode):
/// mode 1 = sustained auto + burst secondary, mode 0 = single "first" shot + burst-of-one secondary.
/// Bullets can penetrate walls (WEP_FLAG_PENETRATEWALLS).
///
/// Identity/attributes from machinegun.qh; balance from bal-wep-xonotic.cfg (g_balance_machinegun_*).
/// This phase ports the per-shot damage/spread selection and the spread-accumulation + heat model;
/// the multi-frame auto/burst scheduling loop is summarized (single shot per WrThink call).
/// </summary>
[Weapon]
public sealed class Machinegun : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_MACHINEGUN, *).</summary>
    public struct Balance
    {
        public int   Mode;              // g_balance_machinegun_mode (0 or 1)
        public bool  First;             // g_balance_machinegun_first (mode-0 secondary "snipe" enabled; else ATCK2 zooms)

        public float SustainedDamage;   // g_balance_machinegun_sustained_damage
        public float SustainedSpread;   // g_balance_machinegun_sustained_spread
        public float SustainedRefire;   // g_balance_machinegun_sustained_refire
        public float SustainedForce;    // g_balance_machinegun_sustained_force
        public float SustainedAmmo;     // g_balance_machinegun_sustained_ammo

        public float FirstDamage;       // g_balance_machinegun_first_damage
        public float FirstSpread;       // g_balance_machinegun_first_spread
        public float FirstRefire;       // g_balance_machinegun_first_refire
        public float FirstForce;        // g_balance_machinegun_first_force
        public float FirstAmmo;         // g_balance_machinegun_first_ammo

        public float SpreadMin;         // g_balance_machinegun_spread_min
        public float SpreadMax;         // g_balance_machinegun_spread_max
        public float SpreadAdd;         // g_balance_machinegun_spread_add

        public float SolidPenetration;  // g_balance_machinegun_solidpenetration
        public float ReloadAmmo;        // g_balance_machinegun_reload_ammo
        public float ReloadTime;        // g_balance_machinegun_reload_time

        public float Burst;             // g_balance_machinegun_burst (rounds per secondary burst)
        public float BurstAmmo;         // g_balance_machinegun_burst_ammo
        public float BurstRefire;       // g_balance_machinegun_burst_refire (between burst shots)
        public float BurstRefire2;      // g_balance_machinegun_burst_refire2 (after a burst)
        public float BurstAnimtime;     // g_balance_machinegun_burst_animtime (fire-anim after the last round)
        public float BurstSpread;       // g_balance_machinegun_burst_spread
        public float SpreadDecay;       // g_balance_machinegun_spread_decay (time-based decay; 0 = legacy)
        public float SpreadCrouchmod;   // g_balance_machinegun_spread_crouchmod
    }

    public Balance Cvars;


    public Machinegun()
    {
        NetName = "machinegun";
        BotPickupBaseValue = 7000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Bullets;   // QC ammo_type
        DisplayName = "MachineGun";
        Impulse = 3;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_PENETRATEWALLS | WEP_FLAG_BLEED
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.PenetrateWalls | WeaponFlags.Bleed;
        Color = new Vector3(0.678f, 0.886f, 0.267f);
        ViewModel = "h_uzi.iqm";   // MDL_MACHINEGUN_VIEW
        WorldModel = "v_uzi.md3";  // MDL_MACHINEGUN_WORLD
        ItemModel = "g_uzi.md3";   // MDL_MACHINEGUN_ITEM
    }

    public override void Configure()
    {
        Cvars.Mode = BalInt("g_balance_machinegun_mode", 1);
        Cvars.First = BalBool("g_balance_machinegun_first", true); // bal-wep-xonotic.cfg: 1

        Cvars.SustainedDamage = Bal("g_balance_machinegun_sustained_damage", 10f);
        Cvars.SustainedSpread = Bal("g_balance_machinegun_sustained_spread", 0.03f);
        Cvars.SustainedRefire = Bal("g_balance_machinegun_sustained_refire", 0.1f);
        Cvars.SustainedForce = Bal("g_balance_machinegun_sustained_force", 3f);
        Cvars.SustainedAmmo = Bal("g_balance_machinegun_sustained_ammo", 1f);

        Cvars.FirstDamage = Bal("g_balance_machinegun_first_damage", 14f);
        Cvars.FirstSpread = Bal("g_balance_machinegun_first_spread", 0.03f);
        Cvars.FirstRefire = Bal("g_balance_machinegun_first_refire", 0.125f);
        Cvars.FirstForce = Bal("g_balance_machinegun_first_force", 3f);
        Cvars.FirstAmmo = Bal("g_balance_machinegun_first_ammo", 1f);

        Cvars.SpreadMin = Bal("g_balance_machinegun_spread_min", 0.02f);
        Cvars.SpreadMax = Bal("g_balance_machinegun_spread_max", 0.05f);
        Cvars.SpreadAdd = Bal("g_balance_machinegun_spread_add", 0.012f);

        Cvars.SolidPenetration = Bal("g_balance_machinegun_solidpenetration", 13.1f);
        Cvars.ReloadAmmo = Bal("g_balance_machinegun_reload_ammo", 0f);
        Cvars.ReloadTime = Bal("g_balance_machinegun_reload_time", 2f);

        Cvars.Burst = Bal("g_balance_machinegun_burst", 3f);
        Cvars.BurstAmmo = Bal("g_balance_machinegun_burst_ammo", 3f);
        Cvars.BurstRefire = Bal("g_balance_machinegun_burst_refire", 0.06f);
        Cvars.BurstRefire2 = Bal("g_balance_machinegun_burst_refire2", 0.45f);
        Cvars.BurstAnimtime = Bal("g_balance_machinegun_burst_animtime", 0.3f);
        Cvars.BurstSpread = Bal("g_balance_machinegun_burst_spread", 0f);       // bal-wep-xonotic.cfg: 0 (no-spread burst)
        Cvars.SpreadDecay = Bal("g_balance_machinegun_spread_decay", 0.048f);   // bal-wep-xonotic.cfg: 0.048 (time-decay model)
        Cvars.SpreadCrouchmod = Bal("g_balance_machinegun_spread_crouchmod", 1f); // bal-wep-xonotic.cfg: 1
    }

    // METHOD(MachineGun, wr_think) — common/weapons/weapon/machinegun.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // QC machinegun.qc:280-286 — forced-reload head of wr_think: if reloading is enabled
        // (reload_ammo != 0) and the clip dropped below the cheapest per-shot cost, reload before firing —
        // BUT not while a burst is running (misc_bulletcounter < 0). Uses the shared reload subsystem
        // (WrReload/WReload/clip_load), matching the Rifle/Crylink/Hlac siblings. Dead at stock balance
        // (reload_ammo defaults 0) but genuinely live when a server enables it.
        if (ReloadingAmmo() != 0f && st.ClipLoad < ReloadingAmmoMin() && st.MiscBulletCounter >= 0)
        {
            WrReload(actor, slot);
            return;
        }

        if (Cvars.Mode == 1)
        {
            if (fire == FireMode.Primary)
            {
                // W_MachineGun_Attack_Auto: sustained auto, gated by sustained_refire (QC weapon_prepareattack).
                // The QC multi-frame loop is summarized to one bullet per fire tick; the spread/heat
                // accumulator persists across calls so held-fire spread still grows.
                if (PrepareAttack(actor, slot, fire))
                {
                    st.MiscBulletCounter = 0; // QC machinegun.qc:293 resets the counter on each auto trigger pull.
                    AttackAuto(actor, slot, st);
                }
            }
            else if (fire == FireMode.Secondary && Cvars.Burst > 0f)
            {
                // W_MachineGun_Attack_Burst: QC gates the burst on weapon_prepareattack(..., true, 0) — the
                // shared ATTACK_FINISHED is advanced by 0, the after-burst cooldown is set by the burst loop
                // itself. It then loads misc_bulletcounter with -to_shoot (counts UP to 0) and fires the FIRST
                // round; W_MachineGun_Attack_Burst self-reschedules every burst_refire for each remaining round.
                if (PrepareAttack(actor, slot, fire, attackTime: 0f))
                {
                    // QC machinegun.qc:301-307 — wr_checkammo2 gate: if secondary is zoom or not enough ammo
                    // for a burst, bail out. At default balance (mode 1, burst 3), ZoomOnSecondary is false
                    // (burst > 0), so this mostly gates on ammo.
                    if (!CheckAmmoSecondary(actor)
                        && !(actor.UnlimitedAmmo || (actor.Items & (1 << 0)) != 0)) // !IT_UNLIMITED_AMMO
                    {
                        // Not enough ammo: switch away (QC W_SwitchWeapon_Force) and return to ready.
                        // The port skips forced switch (handled by higher-level AI); just mark ready.
                        st.State = WeaponFireState.Ready;
                        return;
                    }

                    // QC machinegun.qc:309-311: ammo_available = clip_load when reload is enabled, else the
                    // resource pool. Identical to the resource at stock balance (reload_ammo=0).
                    float available = ReloadingAmmo() != 0f ? st.ClipLoad : actor.GetResource(AmmoType);
                    int toShoot = (int)MathF.Max(0f, Cvars.Burst);
                    bool unlimited = actor.UnlimitedAmmo || (actor.Items & (1 << 0)) != 0; // IT_UNLIMITED_AMMO
                    if (!unlimited)
                    {
                        // Don't mag-dump: scale rounds to a <=1 fraction of the magazine, and only use what's
                        // there (QC burst_fraction + to_use).
                        float burstFraction = Cvars.BurstAmmo > 0f ? MathF.Min(1f, available / Cvars.BurstAmmo) : 1f;
                        toShoot = (int)MathF.Floor(toShoot * burstFraction);
                        // QC to_use = min(burst_ammo, ammo_available), drained via W_DecreaseAmmo (clip-aware).
                        DecreaseAmmo(actor, slot, MathF.Min(Cvars.BurstAmmo, available));
                    }
                    // Bursting counts up to 0 from a negative (QC misc_bulletcounter = -to_shoot).
                    st.MiscBulletCounter = -toShoot;
                    if (toShoot > 0)
                        AttackBurst(actor, slot, st);
                    else
                    {
                        // toShoot==0 (custom balance where sustained_ammo < burst_ammo/burst): QC's wr_checkammo2
                        // would have blocked the burst. Don't enter AttackBurst (counter 0 would never re-reach 0
                        // → infinite self-reschedule); just return the slot to READY after the fire anim.
                        float rate = WeaponRateFactor(actor);
                        WeaponFireDriver.ScheduleThink(st, Cvars.BurstAnimtime * rate, static (pl, sl) =>
                        {
                            WeaponSlotState s2 = pl.WeaponState(sl);
                            if (s2.State == WeaponFireState.InUse)
                                s2.State = WeaponFireState.Ready;
                        });
                    }
                }
            }
        }
        else
        {
            // mode 0: primary = a held-fire stream whose FIRST round is the more-powerful "first" shot and whose
            // held continuation rounds are sustained shots; secondary (if first enabled) = a single first-type snipe.
            if (fire == FireMode.Primary)
            {
                if (PrepareAttack(actor, slot, fire))
                {
                    // QC wr_think mode-0 primary: misc_bulletcounter=1 (=> first shot), W_MachineGun_Attack sets
                    // ATTACK_FINISHED via first_refire, then weapon_thinkf(..., sustained_refire, ..._Attack_Frame)
                    // hands the held-fire continuation to the self-rescheduling frame think below.
                    st.MiscBulletCounter = 1;
                    AttackSingle(actor, slot, st, secondary: false);
                    float rate = WeaponRateFactor(actor);
                    WeaponFireDriver.ScheduleThink(st, Cvars.SustainedRefire * rate,
                        (pl, sl) => AttackFrame(pl, sl, pl.WeaponState(sl)));
                }
            }
            else if (fire == FireMode.Secondary && Cvars.First)
            {
                // QC wr_think mode-0 secondary gate: (fire & 2) && WEP_CVAR(first). When first==0 the ATCK2 is a
                // zoom, not a fire mode (handled by ZoomOnSecondary), so the snipe shot is suppressed here.
                if (PrepareAttack(actor, slot, fire))
                {
                    st.MiscBulletCounter = 1;
                    AttackSingle(actor, slot, st, secondary: true);
                }
            }
        }
    }

    // METHOD(MachineGun, wr_zoom/wr_zoomdir) — common/weapons/weapon/machinegun.qc:406/431. ATTACK2 is a zoom
    // (not a fire mode) when the active mode's secondary fire is disabled: mode 1 with burst==0, or mode 0 with
    // first==0. At the stock balance (mode 1, burst 3) this is false — ATTACK2 fires the burst.
    public override bool ZoomOnSecondary
        => (Cvars.Mode == 1 && Cvars.Burst <= 0f) || (Cvars.Mode == 0 && !Cvars.First);

    // METHOD(MachineGun, wr_aim) — common/weapons/weapon/machinegun.qc:269. Bots press the long-range no-spread
    // burst SECONDARY beyond a skill-scaled switch distance, and the spread-prone primary inside it:
    //   close  (dist <  3000 - bound(0,skill,10)*200) -> ATCK  (primary)
    //   far    (dist >= 3000 - bound(0,skill,10)*200) -> ATCK2 (secondary burst)
    // Returning true routes the already-decided shot onto the secondary button (BotBrain).
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        float switchDistance = 3000f - QMath.Clamp(skill, 0f, 10f) * 200f;
        return enemyDistance >= switchDistance;
    }

    // QC the MachineGun refire is mode/burst-specific (no _primary_/_secondary_ cvar naming): mode 1 primary
    // = sustained_refire, secondary (burst) = burst_refire2; mode 0 = first_refire. There is no separate
    // animtime cvar — the refire doubles as the fire-anim length (weapon_thinkf(..., refire, ...)).
    public override float RefireFor(FireMode fire)
    {
        if (Cvars.Mode == 1)
            return fire == FireMode.Secondary ? Cvars.BurstRefire2 : Cvars.SustainedRefire;
        return Cvars.FirstRefire;
    }
    public override float AnimtimeFor(FireMode fire) => RefireFor(fire);

    // W_MachineGun_Attack_Auto — one sustained-fire bullet with accumulating spread + barrel-heat damage scaling.
    private void AttackAuto(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        // fired credit: QC machinegun.qc:164 passes the raw sustained_damage (heat scaling applies to the
        // dealt damage only, not the accuracy denominator).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, WeaponFiring.CurrentMaxShotDistance, penetrateWalls: true,
            wep: this, maxDamage: Cvars.SustainedDamage);
        Recoil(actor);

        UpdateSpread(st);
        float spreadAccum = st.MachinegunSpreadAccumulation;
        float heat = Heat(spreadAccum);

        // spread_accuracy = spread_min + accum (or spread_min - accum if min > max, "inverted").
        float spread = (Cvars.SpreadMin < Cvars.SpreadMax)
            ? Cvars.SpreadMin + spreadAccum
            : Cvars.SpreadMin - spreadAccum;

        // Crouch reduces spread (QC: IS_DUCKED && IS_ONGROUND -> spread_accuracy *= spread_crouchmod).
        spread *= CrouchSpreadMod(actor);

        FireOne(actor, shot, Cvars.SustainedDamage * heat, spread, Cvars.SustainedForce);

        st.MachinegunSpreadAccumulation = spreadAccum + Cvars.SpreadAdd;
        ++st.MiscBulletCounter;
        // QC W_DecreaseAmmo: drains the clip when reload is enabled, else the resource pool (identical at
        // stock reload_ammo=0). Was a bare TakeResource (correct only with reload off).
        DecreaseAmmo(actor, slot, Cvars.SustainedAmmo);
    }

    // W_MachineGun_Attack_Burst — fires ONE burst round (fresh aim + recoil each round), then self-reschedules
    // every burst_refire while misc_bulletcounter counts up to 0; the round that reaches 0 parks the
    // after-burst cooldown (burst_refire2) and becomes READY after burst_animtime. machinegun.qc
    private void AttackBurst(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        // Re-sample the shot each round (QC W_SetupShot per call) so aim tracks the player mid-burst.
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        // fired credit per burst round: sustained_damage (QC machinegun.qc:217).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, WeaponFiring.CurrentMaxShotDistance, penetrateWalls: true,
            wep: this, maxDamage: Cvars.SustainedDamage);
        Recoil(actor); // per-round punchangle kick (QC sets it every W_MachineGun_Attack_Burst call)

        UpdateSpread(st);
        float heat = Heat(st.MachinegunSpreadAccumulation);

        // burst_spread, reduced when crouched+grounded (QC spread_crouchmod).
        float spread = Cvars.BurstSpread * CrouchSpreadMod(actor);
        FireOne(actor, shot, Cvars.SustainedDamage * heat, spread, Cvars.SustainedForce);

        st.MachinegunSpreadAccumulation += Cvars.SpreadAdd;

        float rate = WeaponRateFactor(actor);
        ++st.MiscBulletCounter;
        if (st.MiscBulletCounter == 0)
        {
            // Last round fired: enforce the after-burst cooldown and return to READY after the fire anim.
            st.AttackFinished = Api.Clock.Time + Cvars.BurstRefire2 * rate;
            WeaponFireDriver.ScheduleThink(st, Cvars.BurstAnimtime * rate, static (pl, sl) =>
            {
                WeaponSlotState s2 = pl.WeaponState(sl);
                if (s2.State == WeaponFireState.InUse)
                    s2.State = WeaponFireState.Ready;
            });
        }
        else
        {
            // More rounds to go: fire the next one after burst_refire (QC weapon_thinkf(..., burst_refire,
            // W_MachineGun_Attack_Burst)).
            WeaponFireDriver.ScheduleThink(st, Cvars.BurstRefire * rate, (pl, sl) =>
                AttackBurst(pl, sl, pl.WeaponState(sl)));
        }
    }

    /// <summary>QC <c>spread_crouchmod</c>: spread multiplier while ducked AND on the ground (else 1).</summary>
    private float CrouchSpreadMod(Entity actor)
        => (actor.IsDucked && actor.OnGround) ? Cvars.SpreadCrouchmod : 1f;

    // W_MachineGun_Attack (mode 0) — one shot whose values are the powerful "first" round when
    // misc_bulletcounter == 1 (the trigger pull / snipe) and the weaker sustained round otherwise (a held-fire
    // continuation round, machinegun.qc:59/74-103). The caller sets misc_bulletcounter before calling.
    private void AttackSingle(Entity actor, WeaponSlot slot, WeaponSlotState st, bool secondary)
    {
        bool isFirst = st.MiscBulletCounter == 1;
        float damage = isFirst ? Cvars.FirstDamage : Cvars.SustainedDamage;
        float spread = isFirst ? Cvars.FirstSpread : Cvars.SustainedSpread;
        float force = isFirst ? Cvars.FirstForce : Cvars.SustainedForce;
        float ammo = isFirst ? Cvars.FirstAmmo : Cvars.SustainedAmmo;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        // fired credit: the selected per-shot damage (QC W_SetupShot uses the same first/sustained pick).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, WeaponFiring.CurrentMaxShotDistance, penetrateWalls: true,
            wep: this, maxDamage: damage);
        Recoil(actor);

        // QC W_MachineGun_Attack applies spread_crouchmod to the first/sustained spread when ducked+grounded.
        // The mode-0 secondary "snipe" ORs HITTYPE_SECONDARY into the bullet deathtype (QC wr_think:
        // thiswep.m_id | HITTYPE_SECONDARY) so the kill reads MURDER_SNIPE; primary stays the plain tag.
        FireOne(actor, shot, damage, spread * CrouchSpreadMod(actor), force, secondary: secondary);
        DecreaseAmmo(actor, slot, ammo); // QC W_DecreaseAmmo (clip-aware; resource pool at stock reload_ammo=0)

        // QC W_MachineGun_Attack (machinegun.qc:68): every mode-0 round re-parks ATTACK_FINISHED at
        // first_refire — the after-stream cooldown floor the held-fire frame loop runs above.
        st.AttackFinished = Api.Clock.Time + Cvars.FirstRefire * WeaponRateFactor(actor);
    }

    // W_MachineGun_Attack_Frame (machinegun.qc:121) — the mode-0 primary held-fire self-continuation. While the
    // player keeps ATCK held and has ammo, increment misc_bulletcounter (so all rounds after the first use the
    // sustained values) and fire a sustained-type round every sustained_refire; otherwise return to READY.
    private void AttackFrame(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        float rate = WeaponRateFactor(actor);
        // QC W_MachineGun_Attack_Frame: abort immediately if switching weapons (m_weapon != m_switchweapon).
        bool switching = st.SwitchWeaponId >= 0 && st.SwitchWeaponId != st.CurrentWeaponId;
        // QC: abort to ready if no longer holding fire (st.ButtonAttack is set live by the driver each tick).
        bool held = st.ButtonAttack;
        bool hasAmmo = actor.UnlimitedAmmo || (actor.Items & (1 << 0)) != 0 || CheckAmmoPrimary(actor);
        if (switching || !held || !hasAmmo)
        {
            // QC weapon_thinkf(..., sustained_refire, w_ready): settle to READY after the fire anim.
            WeaponFireDriver.ScheduleThink(st, Cvars.SustainedRefire * rate, static (pl, sl) =>
            {
                WeaponSlotState s2 = pl.WeaponState(sl);
                if (s2.State == WeaponFireState.InUse)
                    s2.State = WeaponFireState.Ready;
            });
            return;
        }

        ++st.MiscBulletCounter;            // QC: ++misc_bulletcounter (>1 now => sustained values)
        AttackSingle(actor, slot, st, secondary: false);
        // QC weapon_thinkf(..., sustained_refire, W_MachineGun_Attack_Frame): keep streaming.
        WeaponFireDriver.ScheduleThink(st, Cvars.SustainedRefire * rate,
            (pl, sl) => AttackFrame(pl, sl, pl.WeaponState(sl)));
    }

    // fireBullet_falloff with the machinegun's solid penetration + force.
    private void FireOne(Entity actor, ShotInfo shot, float damage, float spread, float force, bool secondary = false)
    {
        // mode-0 secondary snipe carries HITTYPE_SECONDARY (QC m_id | HITTYPE_SECONDARY) so the obituary reads
        // MURDER_SNIPE; the int deathType path can't pack the bit, so override the deathtag for that one path.
        string? deathTag = secondary
            ? Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Secondary)
            : null;
        WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.CurrentMaxShotDistance, damage,
            RegistryId, spread, Cvars.SolidPenetration, force: force, tracerEffect: "BULLET", deathTag: deathTag);
        Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        TraceResult impTr = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, impEnd).Trace; // [T45] warpzone-aware: impact FX land on the far side of a portal
        // QC: w_backoff * 1000 = the impact surface normal (trace_plane_normal), falling back to -force_dir when
        // no surface was hit. impTr.PlaneNormal IS that surface normal — far more faithful than -shot.Dir for
        // angled hits (the impact sprays off the wall, not straight back at the shooter).
        Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
        // QC wr_impacteffect (machinegun.qc:417-421): emit the impact puff AND, unless silent, the random
        // ricochet ping (SND_RIC_RANDOM). The shared seam wires SoundSystem.PlayRic — bare EffectEmitter.Emit
        // played no ric. Hitting a sky surface is silent (the bullet passes through it).
        bool silent = (impTr.DpHitQ3SurfaceFlags & WeaponFiring.Q3SurfaceFlagSky) != 0 || impTr.Fraction >= 1f;
        WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "MACHINEGUN_IMPACT", silent);
        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/uzi_fire.wav");
        EffectEmitter.Emit("MACHINEGUN_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // QC casing code (machinegun.qc:108-112/205-209/250-254): eject a brass bullet casing per shot when
        // g_casings >= 2 (the seam gates that internally and computes the QC view-frame eject velocity).
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Bullet);
    }

    // Port of MachineGun_Update_Spread (machinegun.qc): time-based decay model, or the legacy
    // counter-based accumulation when spread_decay is 0 (Nexuiz balance, our default).
    private void UpdateSpread(WeaponSlotState st)
    {
        float spectrum = MathF.Abs(Cvars.SpreadMax - Cvars.SpreadMin);
        float accum;
        if (Cvars.SpreadDecay > 0f)
        {
            float dt = Api.Clock.Time - st.SpreadUpdateTime;
            accum = QMath.Clamp(st.MachinegunSpreadAccumulation - dt * Cvars.SpreadDecay, 0f, spectrum);
        }
        else
        {
            accum = QMath.Clamp(Cvars.SpreadAdd * st.MiscBulletCounter, Cvars.SpreadMin, Cvars.SpreadMax);
        }
        st.MachinegunSpreadAccumulation = accum;
        st.SpreadUpdateTime = Api.Clock.Time;
    }

    // QC recoil: punchangle gets a small random kick, gated by !autocvar_g_norecoil (machinegun.qc:61,165,218).
    // With g_norecoil 1 the kick is suppressed entirely. Deterministic PRNG.
    private static void Recoil(Entity actor)
    {
        if (Api.Services is not null && Api.Cvars.GetFloat("g_norecoil") != 0f)
            return;
        Vector3 p = actor.PunchAngle;
        p.X = Prandom.Float() - 0.5f;
        p.Y = Prandom.Float() - 0.5f;
        actor.PunchAngle = p;
    }

    /// <summary>
    /// Port of MachineGun_Heat (machinegun.qc): converts the current spread accumulation into a damage
    /// multiplier (cold barrel vs hot barrel), using the optional cold/heat multiplier cvars. The
    /// multipliers default off so this returns ~1.0; reading the live cvars keeps it faithful when set.
    /// </summary>
    public float Heat(float spreadAccum)
    {
        float coldMultiplier = Api.Services is null ? 0f : Api.Cvars.GetFloat("g_balance_machinegun_spread_cold_damagemultiplier");
        float heatMultiplier = Api.Services is null ? 0f : Api.Cvars.GetFloat("g_balance_machinegun_spread_heat_damagemultiplier");

        float heatPct = 0.5f, coldPct = 0.5f;
        float spectrum = MathF.Abs(Cvars.SpreadMax - Cvars.SpreadMin);
        if (spectrum > 0f)
        {
            heatPct = spreadAccum / spectrum;
            coldPct = 1f - heatPct;
        }
        return (coldMultiplier != 0f ? coldPct * coldMultiplier : coldPct)
             + (heatMultiplier != 0f ? heatPct * heatMultiplier : heatPct);
    }

    // METHOD(MachineGun, wr_checkammo1) — machinegun.qc
    public bool CheckAmmoPrimary(Entity actor)
    {
        float need = Cvars.Mode == 1 ? Cvars.SustainedAmmo : Cvars.FirstAmmo;
        return actor.GetResource(AmmoType) >= need;
    }

    // METHOD(MachineGun, wr_checkammo2) — machinegun.qc:366. Returns false if secondary is a zoom
    // (not a fire mode), or if not enough ammo for a burst round. The check uses per-round ammo
    // (burst_ammo / burst) for mode 1 burst secondary.
    public bool CheckAmmoSecondary(Entity actor)
    {
        // QC machinegun.qc:368: want_zoom = (mode 1 && !burst) || (mode 0 && !first).
        // If the secondary slot is a zoom, it's not a fire mode and wr_checkammo2 returns false.
        bool wantZoom = (Cvars.Mode == 1 && Cvars.Burst <= 0f) || (Cvars.Mode == 0 && !Cvars.First);
        if (wantZoom)
            return false;

        // Secondary fire mode exists: check ammo. QC computes per-shot: burst_ammo_per_shot = burst_ammo / burst.
        if (Cvars.Mode == 1)
        {
            // Mode 1 burst secondary: need burst_ammo_per_shot
            float burstPerShot = Cvars.Burst > 0f ? Cvars.BurstAmmo / Cvars.Burst : Cvars.BurstAmmo;
            return actor.GetResource(AmmoType) >= burstPerShot;
        }
        else
        {
            // Mode 0 secondary snipe: need first_ammo (same as primary attack)
            return actor.GetResource(AmmoType) >= Cvars.FirstAmmo;
        }
    }

    // QC wr_reload (machinegun.qc:390) passes W_Reload's sent_ammo_min = min(max(sustained_ammo, first_ammo),
    // burst_ammo): the cheapest per-shot floor below which a reload is pointless. Also the threshold the
    // forced-reload gate in WrThink compares clip_load against (machinegun.qc:281).
    protected override float ReloadingAmmoMin()
        => MathF.Min(MathF.Max(Cvars.SustainedAmmo, Cvars.FirstAmmo), Cvars.BurstAmmo);

    // METHOD(MachineGun, wr_reload) — machinegun.qc:386. Runs the shared reload pipeline, but FIRST honors the
    // MachineGun's weapon-specific precondition: don't interrupt a running burst (misc_bulletcounter < 0 means a
    // burst is counting up to 0). Dead at stock balance (reload_ammo defaults 0 -> base WReload early-outs).
    public override void WrReload(Entity actor, WeaponSlot slot)
    {
        // QC machinegun.qc:388: if (actor.(weaponentity).misc_bulletcounter < 0) return;
        if (actor.WeaponState(slot).MiscBulletCounter < 0)
            return;
        base.WrReload(actor, slot);
    }
}
