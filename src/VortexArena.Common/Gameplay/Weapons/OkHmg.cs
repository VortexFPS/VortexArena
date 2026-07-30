// Port of common/mutators/mutator/overkill/okhmg.{qh,qc}

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill Heavy MachineGun — port of common/mutators/mutator/overkill/okhmg.{qh,qc}. A SUPERWEAPON
/// version of the Overkill MachineGun: primary auto-fires very fast harmful bullets (W_OverkillHeavyMachineGun_Attack_Auto
/// — accumulating spread, wall-penetration), but only while the Superweapon status effect is active (or with
/// IT_UNLIMITED_SUPERWEAPONS); the secondary is the shared Overkill blaster jump (on <c>actor.jump_interval</c>).
/// Only granted by the Overkill mutator (WEP_FLAG_HIDDEN | WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_SUPERWEAPON);
/// resolved by NetName "okhmg".
///
/// Identity/attributes from okhmg.qh; balance from bal-wep-xonotic.cfg (g_balance_okhmg_*). Ported: the
/// auto-fire bullet (fireBullet with solidpenetration+force), the superweapon gate, the forced reload, and
/// the secondary blaster-jump. The deathtype is this weapon's RegistryId (via FireBullet). QC carries a
/// legacy deprecated netname "hmg" for kill-message routing — kept in mind (parity risk §11) but the registry
/// NetName is "okhmg". The okhmg_nadesupport Nade_Damage hook (scaling nade self-damage to 10%) is a niche
/// nade interaction deferred per recon §9 (the nade-throw input is a carried major).
/// </summary>
[Weapon]
public sealed class OkHmg : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_OVERKILL_HMG, *).</summary>
    public struct Balance
    {
        public float Ammo;              // g_balance_okhmg_primary_ammo
        public float Damage;            // g_balance_okhmg_primary_damage
        public float Force;             // g_balance_okhmg_primary_force
        public float Refire;            // g_balance_okhmg_primary_refire
        public float SolidPenetration;  // g_balance_okhmg_primary_solidpenetration
        public float SpreadAdd;         // g_balance_okhmg_primary_spread_add
        public float SpreadMax;         // g_balance_okhmg_primary_spread_max
        public float SpreadMin;         // g_balance_okhmg_primary_spread_min
        public int   SecondaryRefireType; // g_balance_okhmg_secondary_refire_type (1 = own jump_interval timer)
        public float ReloadAmmo;        // g_balance_okhmg_reload_ammo
        public float ReloadTime;        // g_balance_okhmg_reload_time
    }

    public Balance Cvars;

    public OkHmg()
    {
        NetName = "okhmg";
        AmmoType = ResourceType.Bullets;   // QC ammo_type RES_BULLETS
        DisplayName = "Overkill Heavy MachineGun";
        Impulse = 3;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_HIDDEN | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_SUPERWEAPON | WEP_FLAG_PENETRATEWALLS
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Hidden | WeaponFlags.Reloadable
                   | WeaponFlags.TypeHitscan | WeaponFlags.SuperWeapon | WeaponFlags.PenetrateWalls;
        Color = new Vector3(0.992f, 0.471f, 0.396f);
        ViewModel = "h_ok_hmg.iqm";   // MDL_HMG_VIEW
        WorldModel = "v_ok_hmg.md3";  // MDL_HMG_WORLD
        ItemModel = "g_ok_hmg.md3";   // MDL_HMG_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_okhmg_primary_ammo", 1f);
        Cvars.Damage = Bal("g_balance_okhmg_primary_damage", 30f);
        Cvars.Force = Bal("g_balance_okhmg_primary_force", 10f);
        Cvars.Refire = Bal("g_balance_okhmg_primary_refire", 0.05f);
        Cvars.SolidPenetration = Bal("g_balance_okhmg_primary_solidpenetration", 127f);
        Cvars.SpreadAdd = Bal("g_balance_okhmg_primary_spread_add", 0.005f);
        Cvars.SpreadMax = Bal("g_balance_okhmg_primary_spread_max", 0.06f);
        Cvars.SpreadMin = Bal("g_balance_okhmg_primary_spread_min", 0.01f);
        Cvars.SecondaryRefireType = BalInt("g_balance_okhmg_secondary_refire_type", 1);
        Cvars.ReloadAmmo = Bal("g_balance_okhmg_reload_ammo", 120f);
        Cvars.ReloadTime = Bal("g_balance_okhmg_reload_time", 1f);
    }

    // METHOD(OverkillHeavyMachineGun, wr_think)
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Secondary blaster-jump: refire_type 1 = own jump_interval; refire_type 0 = shared ATTACK_FINISHED.
        OkWeapons.FireSecondaryBlasterJump(this, actor, slot, fire, Cvars.SecondaryRefireType);

        // forced reload
        if (Cvars.ReloadAmmo != 0f && st.ClipLoad < Cvars.Ammo)
        {
            WrReload(actor, slot);
            return;
        }

        if (fire == FireMode.Primary)
        {
            // QC the superweapon gate (okhmg.qc:22-23) lives in the attack; enforce it before committing a shot
            // so a player without the Superweapon status switches away rather than parking an empty attack.
            if (SuperweaponGate(actor, slot, st))
                return;

            // weapon_prepareattack(thiswep, actor, weaponentity, false, 0)
            if (PrepareAttack(actor, slot, fire, attackTime: 0f))
            {
                st.MiscBulletCounter = 0;
                AttackAuto(actor, slot, st);
            }
        }
    }

    /// <summary>
    /// QC the superweapon gate from W_OverkillHeavyMachineGun_Attack_Auto (okhmg.qc:22-23): the HMG fires
    /// NOTHING unless the Superweapon status effect is active OR the player has IT_UNLIMITED_SUPERWEAPONS —
    /// it switches away (W_SwitchWeapon_Force) and returns to ready. This lives in the ATTACK path (not in
    /// wr_checkammo1, which is ammo-only) because Overkill grants IT_UNLIMITED_AMMO, which short-circuits the
    /// shared ammo/auto-switch gate BEFORE wr_checkammo runs. Returns true to halt the shot.
    /// </summary>
    private bool SuperweaponGate(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        if (HasSuperweapon(actor))
            return false;
        // QC: W_SwitchWeapon_Force(actor, w_getbestweapon(...)); w_ready(...) — switch away, fire nothing.
        SwitchToOtherWeapon(actor, slot);
        st.State = WeaponFireState.Ready;
        return true;
    }

    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Refire;

    /// <summary>
    /// QC the superweapon gate (okhmg.qc:22-23): the HMG can only fire while the Superweapon status effect is
    /// active OR the player has unlimited superweapons (IT_UNLIMITED_SUPERWEAPONS).
    /// </summary>
    private static bool HasSuperweapon(Entity actor)
    {
        if ((actor.Items & (1 << 1)) != 0) return true; // IT_UNLIMITED_SUPERWEAPONS (ItemFlag.UnlimitedSuperweapons)
        return StatusEffectsCatalog.Superweapon is { } sw && StatusEffectsCatalog.Has(actor, sw);
    }

    // W_OverkillHeavyMachineGun_Attack_Auto
    private void AttackAuto(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        // QC W_OverkillHeavyMachineGun_Attack_Auto (okhmg.qc:22-23): superweapon gate at attack entry — if the
        // Superweapon status is gone (and no IT_UNLIMITED_SUPERWEAPONS), switch away and fire nothing.
        if (SuperweaponGate(actor, slot, st))
            return;

        // W_DecreaseAmmo(WEP_OVERKILL_HMG, actor, ammo) — clip-aware (WEP_FLAG_RELOADABLE): drains the magazine
        // so the wr_think forced-reload branch (clip_load < ammo) engages. okhmg.qc:30 (ammo) + :92-96 (reload).
        DecreaseAmmo(actor, slot, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, WeaponFiring.CurrentMaxShotDistance, penetrateWalls: true);
        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/uzi_fire.wav");

        // QC okhmg.qc:34-38 — punchangle recoil PRNG unless g_norecoil: random()-0.5 on pitch+yaw.
        if ((actor.Flags & EntFlags.Client) != 0 && Api.Cvars.GetFloat("g_norecoil") == 0f)
            actor.PunchAngle = new Vector3(Prandom.Float() - 0.5f, Prandom.Float() - 0.5f, 0f);

        // okhmg_spread = bound(spread_min, spread_min + spread_add * misc_bulletcounter, spread_max)
        float spread = QMath.Clamp(Cvars.SpreadMin + Cvars.SpreadAdd * st.MiscBulletCounter,
            Cvars.SpreadMin, Cvars.SpreadMax);

        // QC okhmg.qc:41-49 — plain fireBullet (no falloff) WITH the EFFECT_RIFLE tracer trail.
        WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.CurrentMaxShotDistance, Cvars.Damage,
            RegistryId, spread, Cvars.SolidPenetration, force: Cvars.Force, tracerEffect: "RIFLE");

        // QC wr_impacteffect (okhmg.qc:150-156): EFFECT_MACHINEGUN_IMPACT puff + throttled SND_RIC_RANDOM ricochet.
        Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        TraceResult impTr = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, impEnd).Trace; // [T45] warpzone-aware: impact FX land on the far side of a portal
        Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
        bool silent = (impTr.DpHitQ3SurfaceFlags & WeaponFiring.Q3SurfaceFlagSky) != 0 || impTr.Fraction >= 1f;
        WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "MACHINEGUN_IMPACT", silent);

        ++st.MiscBulletCounter;

        // W_MuzzleFlash(thiswep, ...) — okhmg.qh m_muzzleeffect = EFFECT_MACHINEGUN_MUZZLEFLASH (okhmg.qc:53).
        EffectEmitter.Emit("MACHINEGUN_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
        // casing (okhmg.qc:55-59): SpawnCasing type 3 (bullet) at g_casings >= 2 (gate inside the seam).
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Bullet);

        float rate = WeaponRateFactor(actor);
        st.AttackFinished = Api.Clock.Time + Cvars.Refire * rate;
        WeaponFireDriver.ScheduleThink(st, Cvars.Refire * rate, (pl, sl) =>
        {
            WeaponSlotState s2 = pl.WeaponState(sl);
            if (s2.State != WeaponFireState.InUse) return;
            // QC W_OverkillHeavyMachineGun_Attack_Auto: re-fire only while ATCK is held (else w_ready) and ammo
            // remains; the superweapon gate is enforced at AttackAuto's entry (it switches away in QC), so route
            // the re-fire through AttackAuto rather than duplicating the gate here.
            if (s2.ButtonAttack && pl.GetResource(AmmoType) >= Cvars.Ammo)
                AttackAuto(pl, sl, s2);
            else
                s2.State = WeaponFireState.Ready;
        });
    }

    // METHOD(OverkillHeavyMachineGun, wr_aim) — okhmg.qc:65-69. Same skill-scaled range gate as the OK MachineGun:
    // bots only press ATCK within 3000 - bound(0, skill, 10) * 200 units, and hold fire entirely beyond it.
    public override bool BotForbidsFire(float enemyDistance, float skill)
        => enemyDistance >= 3000f - QMath.Clamp(skill, 0f, 10f) * 200f;

    // METHOD(OverkillHeavyMachineGun, wr_checkammo1) — AMMO-ONLY (okhmg.qc:116-122). The superweapon gate is
    // NOT here: QC's wr_checkammo1 only tests bullets; the superweapon requirement is enforced inside the
    // attack (see SuperweaponGate) because Overkill's IT_UNLIMITED_AMMO short-circuits the shared ammo gate.
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(OverkillHeavyMachineGun, wr_checkammo2) — secondary (blaster) checks blaster ammo (unlimited here).
    public bool CheckAmmoSecondary(Entity actor) => true;

    public override float ReloadingAmmo() => Cvars.ReloadAmmo;
    public override float ReloadingTime() => Cvars.ReloadTime;
}
