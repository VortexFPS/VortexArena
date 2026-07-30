using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Machinegun Turret — port of common/turrets/turret/machinegun.{qh,qc} (+ machinegun_weapon.qc). A hitscan
/// bullet emplacement: targets players in line of sight, leads them, and rapid-fires bullets in bursts. The
/// cheapest, weakest turret. Identity/hitbox from machinegun.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_machinegun_*</c>).
///
/// Mechanic: hitscan (TUR_FLAG_HITSCAN) — each shot is an instant <see cref="Api.Trace"/> + damage, no
/// projectile (machinegun_weapon.qc fireBullet). Volley of 5 with a 0.5s pause between bursts.
/// </summary>
[Turret]
public sealed class MachinegunTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_machinegun_*) ---
    private const float ShotDamage = 10f;
    private const float ShotForce = 20f;
    private const float ShotSpread = 0.015f;
    private const float ShotSpeed = 34920f;      // near-hitscan; drives lead compensation
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 5;
    private const float ShotVollyRefire = 0.5f;
    private const float TargetRange = 4500f;
    private const float TargetRangeMin = 2f;
    private const float TargetRangeOptimal = 1000f;
    private const float AmmoMax = 1500f;
    private const float AmmoRecharge = 75f;
    private const float AimSpeed = 120f;
    private const float AimMaxPitch = 25f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 25f;

    // QC machinegun scoring biases (turrets.cfg): range 0.25, same 0.25, angle 0.5, player 1, missile 0.
    private const float RangeBias = 0.25f, SameBias = 0.25f, AngleBias = 0.5f, PlayerBias = 1f, MissileBias = 0f;

    // QC machinegun.qc tr_setup: target_select_flags = PLAYERS | RANGELIMITS | TEAMCHECK. Note Base does NOT
    // set TFL_TARGETSELECT_ANGLELIMITS — the machinegun acquires out-of-cone targets at selection time and
    // then slews to them; the angle gate at validate_target:780 only fires when that flag is present.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck;

    public MachinegunTurret()
    {
        NetName = "machinegun";
        DisplayName = "Machinegun Turret";
        Model = "models/turrets/base.md3";
        // machinegun.qh head_model ATTRIB — the separate machinegun.md3 head-bone model on top of the base body.
        // No client turret render attaches it at tag_head yet (whole-family presentation gap), but carrying it as
        // data matches Base identity AND feeds the death-gib toss (TurretAI.Die tosses the head_model as a gib).
        HeadModel = "models/turrets/machinegun.md3";
        StartHealth = 256f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        // machinegun.qc tr_setup: damage_flags |= TFL_DMG_HEADSHAKE (a hit jitters the head off-aim).
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, ShotVolly, energyAmmo: false, headShake: true);

    public override void Think(Entity e)
    {
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            // machinegun.qc tr_setup aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE. No TFL_AIM_ZPREDICT,
            // so the machinegun does NOT lead the gravity arc of airborne targets (zPredict stays false).
            shotTimeCompensate: true, zPredict: false,
            rangeBias: RangeBias, sameBias: SameBias, angleBias: AngleBias,
            missileBias: MissileBias, playerBias: PlayerBias,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: 0.4f, trackAccelRot: 0.9f, trackBlendRate: 0.2f);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // METHOD(MachineGunTurretAttack, wr_think) — machinegun_weapon.qc: fireBullet along the muzzle dir with
    // deterministic spread + knockback force. Only the muzzle flash / tracer / headshake are client-render.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // machinegun_weapon.qc:22 — fireBullet(..., EFFECT_BULLET) with DEATH_TURRET_MACHINEGUN. The
        // EFFECT_BULLET tracer is OUTSIDE the `if (isPlayer)` gate, so it is emitted on the turret path
        // (turret-visible bullet trail). Server-side emit via the same EffectEmitter seam the player
        // Machinegun uses (tracerEffect "BULLET" = EFFECT_BULLET), networked to clients.
        TurretCombat.FireBullet(turret, st.ShotOrg, dir, ShotSpread, ShotDamage, ShotForce,
            DeathTypes.TurretMachinegun, tracerEffect: "BULLET");

        // machinegun_weapon.qc:23 — W_MuzzleFlash_Model(MDL_MACHINEGUN_MUZZLEFLASH). Also OUTSIDE the
        // `if (isPlayer)` gate, so the turret shows a muzzle flash at the fire tag. Base attaches a muzzle
        // model at tag_fire; we have no tag_head/tag_fire sub-entity, so emit the flash effect at the muzzle
        // origin along the shot dir — the same MACHINEGUN_MUZZLEFLASH the player path emits.
        EffectEmitter.Emit("MACHINEGUN_MUZZLEFLASH", st.ShotOrg, dir * 1000f, 1);

        // NO fire sound on the turret path: in machinegun_weapon.qc wr_think the only sound emitter
        // (W_SetupShot_Dir → SND_MachineGunTurretAttack_FIRE) is inside the `if (isPlayer)` block, and a
        // turret actor is not a player, so Base fires SILENTLY. fireBullet/W_MuzzleFlash_Model emit none.

        // NOTE (client-render, still deferred): head-bone frame anim (no tur_head sub-entity / tag_fire).
    }
}
