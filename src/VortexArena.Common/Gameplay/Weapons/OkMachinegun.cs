// Port of common/mutators/mutator/overkill/okmachinegun.{qh,qc}

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill MachineGun — port of common/mutators/mutator/overkill/okmachinegun.{qh,qc}. The Overkill
/// loadout's bullet weapon: primary auto-fires fast, accumulating spread (one bullet per refire, with
/// wall-penetration); the secondary is the shared Overkill "blaster jump" — a damage/force-less Blaster
/// shot on its OWN refire timer (<c>actor.jump_interval</c>), used to launch yourself around. Only granted
/// by the Overkill mutator (WEP_FLAG_HIDDEN | WEP_FLAG_MUTATORBLOCKED), never a world pickup; the
/// OverkillMutator resolves it by NetName "okmachinegun".
///
/// Identity/attributes from okmachinegun.qh; balance from bal-wep-xonotic.cfg (g_balance_okmachinegun_*).
/// Ported: the auto-fire bullet (W_OverkillMachineGun_Attack_Auto — accumulating spread
/// <c>bound(spread_min, spread_min + spread_add*misc_bulletcounter, spread_max)</c>, fireBullet_falloff with
/// solidpenetration+force), the forced reload, and the secondary blaster-jump on the jump_interval timer
/// (the deathtype is this weapon's RegistryId via FireBullet). Casings / muzzle flash / recoil-PRNG and the
/// damagefalloff cvars (all 0 in xonotic balance) are render/no-op details.
/// </summary>
[Weapon]
public sealed class OkMachinegun : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_OVERKILL_MACHINEGUN, *).</summary>
    public struct Balance
    {
        public float Ammo;              // g_balance_okmachinegun_primary_ammo
        public float Damage;            // g_balance_okmachinegun_primary_damage
        public float Force;             // g_balance_okmachinegun_primary_force
        public float Refire;            // g_balance_okmachinegun_primary_refire
        public float SolidPenetration;  // g_balance_okmachinegun_primary_solidpenetration
        public float SpreadAdd;         // g_balance_okmachinegun_primary_spread_add
        public float SpreadMax;         // g_balance_okmachinegun_primary_spread_max
        public float SpreadMin;         // g_balance_okmachinegun_primary_spread_min
        public int   SecondaryRefireType; // g_balance_okmachinegun_secondary_refire_type (1 = own jump_interval timer)
        public float ReloadAmmo;        // g_balance_okmachinegun_reload_ammo
        public float ReloadTime;        // g_balance_okmachinegun_reload_time
        public float DamageFalloffHalflife;      // g_balance_okmachinegun_damagefalloff_halflife
        public float DamageFalloffMinDist;       // g_balance_okmachinegun_damagefalloff_mindist
        public float DamageFalloffMaxDist;       // g_balance_okmachinegun_damagefalloff_maxdist
        public float DamageFalloffForceHalflife; // g_balance_okmachinegun_damagefalloff_forcehalflife
    }

    public Balance Cvars;

    public OkMachinegun()
    {
        NetName = "okmachinegun";
        AmmoType = ResourceType.Bullets;   // QC ammo_type RES_BULLETS
        DisplayName = "Overkill MachineGun";
        Impulse = 3;
        // WEP_FLAG_HIDDEN | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_PENETRATEWALLS | WEP_FLAG_MUTATORBLOCKED
        SpawnFlags = WeaponFlags.Hidden | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.PenetrateWalls | WeaponFlags.MutatorBlocked;
        Color = new Vector3(0.678f, 0.886f, 0.267f);
        ViewModel = "h_ok_mg.iqm";   // MDL_OK_MG_VIEW
        WorldModel = "v_ok_mg.md3";  // MDL_OK_MG_WORLD
        ItemModel = "g_ok_mg.md3";   // MDL_OK_MG_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_okmachinegun_primary_ammo", 1f);
        Cvars.Damage = Bal("g_balance_okmachinegun_primary_damage", 25f);
        Cvars.Force = Bal("g_balance_okmachinegun_primary_force", 5f);
        Cvars.Refire = Bal("g_balance_okmachinegun_primary_refire", 0.1f);
        Cvars.SolidPenetration = Bal("g_balance_okmachinegun_primary_solidpenetration", 100f);
        Cvars.SpreadAdd = Bal("g_balance_okmachinegun_primary_spread_add", 0.012f);
        Cvars.SpreadMax = Bal("g_balance_okmachinegun_primary_spread_max", 0.05f);
        Cvars.SpreadMin = Bal("g_balance_okmachinegun_primary_spread_min", 0f);
        Cvars.SecondaryRefireType = BalInt("g_balance_okmachinegun_secondary_refire_type", 1);
        Cvars.ReloadAmmo = Bal("g_balance_okmachinegun_reload_ammo", 30f);
        Cvars.ReloadTime = Bal("g_balance_okmachinegun_reload_time", 1.5f);
        // damagefalloff cvars — all 0 in bal-wep-xonotic.cfg (no falloff), but ported for fidelity to fireBullet_falloff.
        Cvars.DamageFalloffHalflife = Bal("g_balance_okmachinegun_damagefalloff_halflife", 0f);
        Cvars.DamageFalloffMinDist = Bal("g_balance_okmachinegun_damagefalloff_mindist", 0f);
        Cvars.DamageFalloffMaxDist = Bal("g_balance_okmachinegun_damagefalloff_maxdist", 0f);
        Cvars.DamageFalloffForceHalflife = Bal("g_balance_okmachinegun_damagefalloff_forcehalflife", 0f);
    }

    // METHOD(OverkillMachineGun, wr_think)
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Secondary blaster-jump on the dedicated jump_interval timer (refire_type 1). QC okmachinegun.qc:107
        // passes `true` (secondary ammo check) on the refire_type==0 path.
        OkWeapons.FireSecondaryBlasterJump(this, actor, slot, fire, Cvars.SecondaryRefireType, FireMode.Secondary);

        // forced reload: if reloadable and the clip is below a primary shot, reload.
        if (Cvars.ReloadAmmo != 0f && st.ClipLoad < Cvars.Ammo)
        {
            WrReload(actor, slot);
            return;
        }

        if (fire == FireMode.Primary)
        {
            // weapon_prepareattack(thiswep, actor, weaponentity, false, 0): the auto-fire schedules its own
            // ATTACK_FINISHED via the refire below; the shared gate is advanced by 0.
            if (PrepareAttack(actor, slot, fire, attackTime: 0f))
            {
                st.MiscBulletCounter = 0;
                AttackAuto(actor, slot, st);
            }
        }
    }

    // QC the MachineGun-style refire doubles as the fire-anim length (weapon_thinkf with refire).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Refire;

    // W_OverkillMachineGun_Attack_Auto — one auto-fire bullet with accumulating spread; self-reschedules.
    private void AttackAuto(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        // W_DecreaseAmmo(WEP_OVERKILL_MACHINEGUN, actor, ammo) — clip-aware (WEP_FLAG_RELOADABLE): drains the
        // magazine so the wr_think forced-reload branch (clip_load < ammo) engages. okmachinegun.qc:21 + :91-95.
        DecreaseAmmo(actor, slot, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, WeaponFiring.CurrentMaxShotDistance, penetrateWalls: true);
        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/uzi_fire.wav");

        // QC okmachinegun.qc:24-28 — punchangle recoil PRNG unless g_norecoil: sets ONLY punchangle_x/_y
        // (pitch/yaw), leaving the roll (Z) untouched. Read-modify-write to preserve Z (matching QC + Machinegun.Recoil).
        if ((actor.Flags & EntFlags.Client) != 0 && Api.Cvars.GetFloat("g_norecoil") == 0f)
        {
            Vector3 p = actor.PunchAngle;
            p.X = Prandom.Float() - 0.5f;
            p.Y = Prandom.Float() - 0.5f;
            actor.PunchAngle = p;
        }

        // okmachinegun_spread = bound(spread_min, spread_min + spread_add * misc_bulletcounter, spread_max)
        float spread = QMath.Clamp(Cvars.SpreadMin + Cvars.SpreadAdd * st.MiscBulletCounter,
            Cvars.SpreadMin, Cvars.SpreadMax);

        // fireBullet_falloff with the EFFECT_RIFLE tracer trail (okmachinegun.qc:36-48).
        WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.CurrentMaxShotDistance, Cvars.Damage,
            RegistryId, spread, Cvars.SolidPenetration,
            Cvars.DamageFalloffHalflife, Cvars.DamageFalloffMinDist, Cvars.DamageFalloffMaxDist,
            force: Cvars.Force, falloffForceHalflife: Cvars.DamageFalloffForceHalflife,
            tracerEffect: "RIFLE");

        // QC wr_impacteffect (okmachinegun.qc:146-152): EFFECT_MACHINEGUN_IMPACT puff at the surface + the
        // throttled SND_RIC_RANDOM ricochet (unless the shot hit sky / nothing). Trace the surface for w_backoff.
        Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        TraceResult impTr = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, impEnd).Trace; // [T45] warpzone-aware: impact FX land on the far side of a portal
        Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
        bool silent = (impTr.DpHitQ3SurfaceFlags & WeaponFiring.Q3SurfaceFlagSky) != 0 || impTr.Fraction >= 1f;
        WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "MACHINEGUN_IMPACT", silent);

        ++st.MiscBulletCounter;

        // W_MuzzleFlash(thiswep, ...) — okmachinegun.qh m_muzzleeffect = EFFECT_MACHINEGUN_MUZZLEFLASH (okmachinegun.qc:52).
        EffectEmitter.Emit("MACHINEGUN_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
        // casing (okmachinegun.qc:54-58): eject a brass casing at g_casings >= 2 (gate inside).
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Bullet);

        // ATTACK_FINISHED + weapon_thinkf(WFRAME_FIRE1, refire, Attack_Auto): self-reschedule while held.
        float rate = WeaponRateFactor(actor);
        st.AttackFinished = Api.Clock.Time + Cvars.Refire * rate;
        WeaponFireDriver.ScheduleThink(st, Cvars.Refire * rate, (pl, sl) =>
        {
            WeaponSlotState s2 = pl.WeaponState(sl);
            if (s2.State != WeaponFireState.InUse) return;
            // QC's loop re-enters wr_think's auto branch only while ATCK is held; mirror by re-firing when held.
            if (s2.ButtonAttack && pl.GetResource(AmmoType) >= Cvars.Ammo)
                AttackAuto(pl, sl, s2);
            else
                s2.State = WeaponFireState.Ready;
        });
    }

    // METHOD(OverkillMachineGun, wr_aim) — okmachinegun.qc:64-68. Bots only press ATCK when the enemy is within
    // a skill-scaled range (3000 - bound(0, skill, 10) * 200); beyond it they hold fire entirely (the bullet
    // spread makes long-range fire wasteful). The shot itself is the generic accurate-hitscan aim.
    public override bool BotForbidsFire(float enemyDistance, float skill)
        => enemyDistance >= 3000f - QMath.Clamp(skill, 0f, 10f) * 200f;

    // METHOD(OverkillMachineGun, wr_checkammo1)
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(OverkillMachineGun, wr_checkammo2) — Blaster secondary is unlimited.
    public bool CheckAmmoSecondary(Entity actor) => true;

    public override float ReloadingAmmo() => Cvars.ReloadAmmo;
    public override float ReloadingTime() => Cvars.ReloadTime;
}
