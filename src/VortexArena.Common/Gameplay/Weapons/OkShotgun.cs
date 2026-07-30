// Port of common/mutators/mutator/overkill/okshotgun.{qh,qc}

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill Shotgun — port of common/mutators/mutator/overkill/okshotgun.{qh,qc}. The Overkill
/// loadout's close-range weapon: primary sprays a fan of pellets (W_Shotgun_Attack — 10 hitscan pellets,
/// spread 0.07, with solid penetration + knockback force); the secondary is the shared Overkill blaster
/// jump (on <c>actor.jump_interval</c>). Only granted by the Overkill mutator (WEP_FLAG_HIDDEN |
/// WEP_FLAG_MUTATORBLOCKED); resolved by NetName "okshotgun".
///
/// Identity/attributes from okshotgun.qh; balance from bal-wep-xonotic.cfg (g_balance_okshotgun_*).
/// Ported: the pellet fan (per-pellet random spread — g_balance_okshotgun_primary_spread_pattern defaults
/// off, so the random path matches QC), the forced reload, and the secondary blaster-jump. The deathtype is
/// this weapon's RegistryId (via FireBullet). The damagefalloff cvars are 0 in xonotic balance, so falloff
/// is a no-op.
/// </summary>
[Weapon]
public sealed class OkShotgun : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_OVERKILL_SHOTGUN, *).</summary>
    public struct Balance
    {
        public float Ammo;              // g_balance_okshotgun_primary_ammo
        public float Animtime;          // g_balance_okshotgun_primary_animtime
        public float Bullets;           // g_balance_okshotgun_primary_bullets (pellet count)
        public float Damage;            // g_balance_okshotgun_primary_damage
        public float Force;             // g_balance_okshotgun_primary_force
        public float Refire;            // g_balance_okshotgun_primary_refire
        public float SolidPenetration;  // g_balance_okshotgun_primary_solidpenetration
        public float Spread;            // g_balance_okshotgun_primary_spread
        public float BotRange;          // g_balance_okshotgun_primary_bot_range (bot ATCK1/ATCK2 switch distance)
        public int   SecondaryRefireType; // g_balance_okshotgun_secondary_refire_type (1 = own jump_interval timer)
        public float ReloadAmmo;        // g_balance_okshotgun_reload_ammo
        public float ReloadTime;        // g_balance_okshotgun_reload_time
    }

    public Balance Cvars;

    public OkShotgun()
    {
        NetName = "okshotgun";
        AmmoType = ResourceType.Shells;   // QC ammo_type RES_SHELLS
        DisplayName = "Overkill Shotgun";
        Impulse = 2;
        // WEP_FLAG_HIDDEN | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_MUTATORBLOCKED
        SpawnFlags = WeaponFlags.Hidden | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.MutatorBlocked;
        Color = new Vector3(0.518f, 0.608f, 0.659f);
        ViewModel = "h_ok_shotgun.iqm";   // MDL_OK_SHOTGUN_VIEW
        WorldModel = "v_ok_shotgun.md3";  // MDL_OK_SHOTGUN_WORLD
        ItemModel = "g_ok_shotgun.md3";   // MDL_OK_SHOTGUN_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_okshotgun_primary_ammo", 3f);
        Cvars.Animtime = Bal("g_balance_okshotgun_primary_animtime", 0.65f);
        Cvars.Bullets = Bal("g_balance_okshotgun_primary_bullets", 10f);
        Cvars.Damage = Bal("g_balance_okshotgun_primary_damage", 17f);
        Cvars.Force = Bal("g_balance_okshotgun_primary_force", 80f);
        Cvars.Refire = Bal("g_balance_okshotgun_primary_refire", 0.75f);
        Cvars.SolidPenetration = Bal("g_balance_okshotgun_primary_solidpenetration", 3.8f);
        Cvars.Spread = Bal("g_balance_okshotgun_primary_spread", 0.07f);
        Cvars.BotRange = Bal("g_balance_okshotgun_primary_bot_range", 512f);
        Cvars.SecondaryRefireType = BalInt("g_balance_okshotgun_secondary_refire_type", 1);
        Cvars.ReloadAmmo = Bal("g_balance_okshotgun_reload_ammo", 24f);
        Cvars.ReloadTime = Bal("g_balance_okshotgun_reload_time", 2f);
    }

    // METHOD(OverkillShotgun, wr_think)
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
            // weapon_prepareattack(thiswep, actor, weaponentity, false, refire)
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
        }
    }

    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_Shotgun_Attack(thiswep, actor, weaponentity, true, ammo, damage, ..., bullets, spread, ..., solidpenetration, force, ...)
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // W_DecreaseAmmo(thiswep, actor, ammo) — clip-aware (WEP_FLAG_RELOADABLE): drains the magazine so the
        // wr_think forced-reload branch (clip_load < ammo) engages. okshotgun.qc:43-44 (ammo) + :33-37 (reload).
        DecreaseAmmo(actor, slot, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        int pellets = (int)Cvars.Bullets;
        for (int i = 0; i < pellets; ++i)
        {
            // W_Shotgun_Attack passes EFFECT_RIFLE_WEAK as the per-pellet tracer (okshotgun.qc:57).
            WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.CurrentMaxShotDistance, Cvars.Damage,
                RegistryId, Cvars.Spread, Cvars.SolidPenetration, force: Cvars.Force, tracerEffect: "RIFLE_WEAK");
            // QC wr_impacteffect (okshotgun.qc:103-113): EFFECT_SHOTGUN_IMPACT puff + the 5%/0.25s-throttled
            // SND_RIC_RANDOM ricochet (the seam owns the throttle so a 10-pellet blast yields at most one ric).
            Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
            TraceResult impTr = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, impEnd).Trace; // [T45] warpzone-aware: impact FX land on the far side of a portal
            Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
            // QC wr_impacteffect guards the ricochet with !w_issilent — suppress the puff+ric on a sky/miss hit,
            // matching the okmachinegun/okhmg impact path.
            bool silent = (impTr.DpHitQ3SurfaceFlags & WeaponFiring.Q3SurfaceFlagSky) != 0 || impTr.Fraction >= 1f;
            WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "SHOTGUN_IMPACT", silent);
        }
        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/shotgun_fire.wav");

        // W_MuzzleFlash inside W_Shotgun_Attack — EFFECT_SHOTGUN_MUZZLEFLASH (matching the base Shotgun port).
        EffectEmitter.Emit("SHOTGUN_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
        // Casing eject — W_Shotgun_Attack SpawnCasing (shotgun shell, type 1) when g_casings >= 1 (gate inside).
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Shell);
    }

    // METHOD(OverkillShotgun, wr_aim) — okshotgun.qc:4-9. Beyond bot_range the bot presses the SECONDARY
    // (the no-damage blaster, used as a ranged poke / mobility tool); within range it presses the pellet
    // primary. Returning true routes the already-decided shot onto ATCK2 (BotBrain), matching QC's range split.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
        => enemyDistance > Cvars.BotRange;

    // METHOD(OverkillShotgun, wr_checkammo1)
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(OverkillShotgun, wr_checkammo2) — Blaster secondary is unlimited.
    public bool CheckAmmoSecondary(Entity actor) => true;

    public override float ReloadingAmmo() => Cvars.ReloadAmmo;
    public override float ReloadingTime() => Cvars.ReloadTime;
}
