using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Dual Plasma Cannon Turret — port of common/turrets/turret/plasma_dual.{qh,qc}. The stronger sibling of the
/// <see cref="PlasmaTurret"/> (QC <c>CLASS(DualPlasmaTurret, PlasmaTurret)</c>): same electric-ball splash
/// projectile and damage, but two cannons firing at nearly double the rate (refire 0.35 vs 0.6). Identity from
/// plasma_dual.qh; balance from turrets.cfg (<c>g_turrets_unit_plasma_dual_*</c>). Inherits the plasma weapon
/// (<c>SUPER(PlasmaTurret).tr_attack</c>) but overrides the per-unit tunables that diverge from single plasma.
/// </summary>
[Turret]
public sealed class PlasmaDualTurret : PlasmaTurret
{
    // --- balance overrides (turrets.cfg g_turrets_unit_plasma_dual_*, lines 359-394) ---
    // Same shot_dmg/radius/speed/force as single Plasma (reuse the base consts); these per-unit values differ:
    private const float DualRefire = 0.35f;           // shot_refire
    private const float DualShotSpread = 0.015f;      // shot_spread (single plasma uses 0.0125)
    private const float DualTargetRange = 3000f;      // target_range
    private const float DualTargetRangeMin = 80f;     // target_range_min
    private const float DualTargetRangeOptimal = 1000f; // target_range_optimal (single plasma's is 500)
    private const float DualAmmoMax = 640f;           // ammo_max
    private const float DualAmmoRecharge = 40f;       // ammo_recharge
    private const float DualAimSpeed = 100f;          // aim_speed
    private const float DualFireTolerance = 200f;     // aim_firetolerance_dist

    // target_select_*bias (single plasma leaves these at the 1/1/1/1/1 ctor defaults; dual sets them explicitly).
    private const float DualRangeBias = 0.2f;
    private const float DualSameBias = 0.4f;
    private const float DualAngleBias = 0.4f;
    private const float DualPlayerBias = 1f;
    private const float DualMissileBias = 0f;

    // track motor (track_type 3 = FLUIDINERTIA). Single plasma leaves accel_rot/blendrate at ctor defaults;
    // dual overrides accel_rot (0.7 vs default 0.5) and blendrate (0.2 vs default 0.35). accel_pitch matches.
    private const float DualTrackAccelPitch = 0.5f;
    private const float DualTrackAccelRot = 0.7f;
    private const float DualTrackBlendRate = 0.2f;

    public PlasmaDualTurret()
    {
        NetName = "plasma_dual";
        DisplayName = "Dual Plasma Cannon";
        Model = "models/turrets/base.md3";
        // QC plasma_dual.qh overrides head_model = "models/turrets/plasmad.md3" (the dual-cannon head bone, vs
        // single plasma's plasma.md3). The port has no client tur_head render yet, but head_model is consumed
        // server-side by the death-gib toss: TurretAI.TossDeathGibs (cl_turrets.qc turret_die) spawns the head as
        // an exploding gib carrying the head's last angles + spin. Without it, plasma_dual death tosses only the
        // base-gibs and silently skips its head gib (TossGib no-ops on an empty model) — every other standard
        // turret already carries its HeadModel, so this matches Base identity + the family death-gib behaviour.
        HeadModel = "models/turrets/plasmad.md3";
        StartHealth = 500f;
        Range = DualTargetRange;
    }

    public override void Spawn(Entity e)
        // headShake: true — DualPlasmaTurret inherits PlasmaTurret.tr_setup (plasma.qc:51), which sets
        // `damage_flags |= TFL_DMG_HEADSHAKE`. turret_damage (sv_turrets.qc:226-234) then jolts the head
        // ±damage on pitch+yaw each hit; TurretAI.EventDamage honours this when the flag is set.
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            DualAmmoMax, DualAmmoRecharge, shotVolly: 0, headShake: true);

    public override void Think(Entity e)
    {
        // tr_think head-frame wheel (plasma_dual.qc:39-44): the dual cannon's spin animation. The 0..6 wheel
        // idles at BOTH 0 and 3 (distinct from single plasma's 0..5 / idle-at-0) and only advances once a shot
        // has kicked it off frame 0/3. Server-authoritative (QC nets tur_head.frame via TNSF_*); the port has no
        // separate head-bone entity, so — like HellionTurret/HkTurret — the cycle runs on the turret edict's own
        // frame field. Runs before the combat brain each think, as QC turret_think calls tr_think every frame.
        if (e.Frame != 0f && e.Frame != 3f) e.Frame += 1f;
        if (e.Frame > 6f) e.Frame = 0f;

        // Build the dual params directly (rather than the shared single-plasma MakeParams) so the per-unit
        // overrides above are honored: target_range_optimal 1000, the scoring biases, and the FLUIDINERTIA
        // track accel/blend — all of which single plasma leaves at defaults that diverge from plasma_dual.cfg.
        var p = new TurretParams(Select, DualTargetRangeMin, DualTargetRange, ShotDamage, DualRefire,
            DualAimSpeed, DualFireTolerance, lead: true,
            rangeOptimal: DualTargetRangeOptimal, shotSpeed: ShotSpeed,
            aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            // QC plasma tr_setup aim_flags = LEAD | SHOTTIMECOMPENSATE | SPLASH; it does NOT set TFL_AIM_ZPREDICT,
            // and DualPlasmaTurret has no tr_setup override so it inherits those flags (zPredict stays false —
            // matches the single plasma's MakeParams; airborne targets aren't mis-led down a gravity arc).
            shotTimeCompensate: true, zPredict: false, aimSplash: true,
            rangeBias: DualRangeBias, sameBias: DualSameBias, angleBias: DualAngleBias,
            missileBias: DualMissileBias, playerBias: DualPlayerBias,
            trackType: TurretAI.TrackFluidInertia,
            trackAccelPitch: DualTrackAccelPitch, trackAccelRot: DualTrackAccelRot,
            trackBlendRate: DualTrackBlendRate,
            // Inherits the single-plasma firecheck set (plasma.qc:51 adds TFL_FIRECHECK_AFF to the framework
            // default); plasma_dual.qc has no firecheck override of its own.
            fireCheckFlags: TurretAI.FireCheckDefault | TurretAI.FireCheckAff);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, DualTargetRangeMin, DualTargetRange);

    // METHOD(DualPlasmaTurret, tr_attack) — plasma_dual.qc. The instagib branch is identical to single plasma
    // (FireRailgunBullet 800 dmg); the plasma-ball branch is the same SUPER(PlasmaTurret).tr_attack but fires
    // with the dual unit's wider shot_spread (0.015 vs single 0.0125). Re-emit it here with the dual spread so
    // the cone matches plasma_dual.cfg, then advance the head wheel (++tur_head.frame), as the QC does.
    protected override void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        if (InstagibEnabled())
        {
            // plasma_dual.qc tr_attack instagib path (inlined, NOT via SUPER): an instant railgun beam. Because
            // QC does not call SUPER here, the single-plasma `if (frame == 0) frame = 1` start-kick does NOT run
            // on the instagib branch — only the dual's own `++frame` below applies. Call the shared beam helper
            // directly (rather than base.Attack, which would bundle that start-kick).
            FireInstagibBeam(turret, st.ShotOrg, dir);
        }
        else
        {
            // Plasma ball with the dual unit's shot_spread (0.015). Same damage/radius/speed/force as single.
            // QC plasma_weapon.qc (shared by plasma_dual): PROJECTILE_ELECTRO_BEAM → blue plasma client trail
            // (the helper stamps the projType, matching QC's _proj_type arg).
            TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
                ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretPlasma, spread: DualShotSpread,
                projType: "electro_bolt");

            if (Api.Services is not null)
            {
                Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");
                // QC plasma_weapon.qc:23 (shared PlasmaAttack.wr_think, used by dual via SUPER): the blaster
                // muzzle flash, emitted unconditionally after the projectile.
                EffectEmitter.Emit("BLASTER_MUZZLEFLASH", st.ShotOrg, dir * 1000f, 1);
            }

            // SUPER(PlasmaTurret).tr_attack contains `if (frame == 0) frame = 1` (plasma.qc:37-38); it runs ONLY
            // on the non-instagib branch (the instagib branch inlines its own code and never calls SUPER).
            if (turret.Frame == 0f) turret.Frame = 1f;
        }

        // DualPlasmaTurret.tr_attack `++it.tur_head.frame` (plasma_dual.qc:36) — runs unconditionally after the
        // if/else. The port has no separate head-bone entity, so the wheel state lives on the turret edict's own
        // networked Entity.Frame (the HellionTurret/HkTurret pattern); tr_think (in Think) advances + wraps it.
        turret.Frame += 1f;

        // The muzzle/beam effects are now emitted (BLASTER_MUZZLEFLASH on the ball branch; VORTEX_MUZZLEFLASH +
        // team-coloured VAPORIZER_BEAM on the instagib branch via FireInstagibBeam). Remaining client-render gap:
        // the two-barrel tag_fire alternation on plasmad.md3 needs an attached head-bone model, so shots leave the
        // single computed ShotOrg muzzle. The server-authoritative frame state above is faithful (HellionTurret pattern).
    }
}
