using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Hellion Missile Turret — port of common/turrets/turret/hellion.{qh,qc} (+ hellion_weapon.qc). Launches
/// homing missiles (similar to the Devastator) from two cannons that accelerate over their flight. Long range,
/// 2-shot volley. Identity/hitbox from hellion.qh; balance from turrets.cfg (<c>g_turrets_unit_hellion_*</c>).
///
/// Mechanic: a heat-seeking splash missile (TUR_FLAG_SPLASH | TUR_FLAG_FASTPROJ | TUR_FLAG_MISSILE).
/// Aim is TFL_AIM_SIMPLE (the turret points at the target's current pos; the missile does the homing). The
/// guidance is fully ported via <see cref="GuidedProjectile"/> (hellion_weapon.qc track-to-enemy think:
/// predict the lead point, blend the heading, accelerate by shot_speed_gain up to shot_speed_max, detonate on
/// intercept). Only the rocket trail/smoke is client render.
/// </summary>
[Turret]
public sealed class HellionTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_hellion_*) ---
    private const float ShotDamage = 50f;
    private const float ShotRadius = 80f;         // turrets.cfg hellion_shot_radius (blast + missile proximity/stray basis)
    private const float ShotSpread = 0.08f;       // turrets.cfg hellion_shot_spread (launch scatter cone)
    private const float ShotSpeed = 650f;        // launch speed; accelerates to ShotSpeedMax via homing think
    private const float ShotSpeedMax = 4000f;
    private const float ShotSpeedGain = 1.01f;
    private const float ShotForce = 250f;
    private const float ShotRefire = 0.2f;
    private const int ShotVolly = 2;
    private const float ShotVollyRefire = 4f;
    private const float TargetRange = 6000f;
    private const float TargetRangeMin = 150f;
    private const float TargetRangeOptimal = 4500f;   // turrets.cfg hellion_target_range_optimal
    private const float AmmoMax = 200f;
    private const float AmmoRecharge = 50f;
    private const float AimSpeed = 100f;
    private const float AimMaxPitch = 20f;       // turrets.cfg hellion_aim_maxpitch
    private const float AimMaxRot = 360f;        // turrets.cfg hellion_aim_maxrot
    private const float FireTolerance = 200f;
    private const float RespawnTime = 90f;       // turrets.cfg hellion_respawntime

    // turrets.cfg hellion_target_select_* bias weights (rangebias/samebias/anglebias/missilebias/playerbias).
    private const float RangeBias = 0.7f;
    private const float SameBias = 0.01f;
    private const float AngleBias = 0.01f;
    private const float MissileBias = 0f;        // Base gives missiles ZERO score in target selection.
    private const float PlayerBias = 1f;

    // turrets.cfg hellion_track_* fluid-inertia tracker dynamics.
    private const float TrackAccelPitch = 0.25f;
    private const float TrackAccelRot = 0.6f;
    private const float TrackBlendRate = 0.25f;

    // QC hellion.qc tr_setup: LOS, players, range-limited, team-checked. Missiles added by TUR_FLAG_MISSILE.
    // Base hellion tr_setup does NOT set ANGLELIMITS (it relies on the head simply not pointing rather than
    // rejecting the target at acquisition), so it is not included here.
    private const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectMissiles;

    // QC hellion.qc:22 firecheck_flags = DEAD | DISTANCES | TEAMCHECK | REFIRE | AFF | AMMO_OWN. Notably there is
    // NO AIMDIST (it fires once range/cool/ammo allow, even without a clear muzzle line — the homing missile finds
    // its own way) and NO LOS in the fire gate; it DOES add AFF (withhold a shot that would land on a teammate).
    private const int FireCheck = TurretAI.FireCheckDead | TurretAI.FireCheckDistances | TurretAI.FireCheckTeamCheck
                                | TurretAI.FireCheckRefire | TurretAI.FireCheckAff | TurretAI.FireCheckAmmoOwn;

    public HellionTurret()
    {
        NetName = "hellion";
        DisplayName = "Hellion Missile Turret";
        Model = "models/turrets/base.md3";
        HeadModel = "models/turrets/hellion.md3";   // QC hellion.qh head_model ATTRIB (the spinning-launcher head)
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, ShotVolly, respawnTime: RespawnTime, energyAmmo: false);

    public override void Think(Entity e)
    {
        // tr_think (hellion.qc:11-17): once the launcher is spinning (kicked by a shot), cycle the head frame 1..6
        // continuously, wrapping back to 0 at >= 7. Server-authoritative (QC nets tur_head.frame); the visual head
        // bone is client-render. Run every think before the combat brain, as QC's turret_think does. Base drives a
        // separate tur_head entity's frame; the port has no head bone entity so the cycle runs on the turret edict's
        // own frame (the same pattern HkTurret uses).
        if (e.Frame != 0f) e.Frame += 1f;
        if (e.Frame >= 7f) e.Frame = 0f;

        // TFL_AIM_SIMPLE -> aim at the target's current pos (the missile homes). Two-shot volley, long refire.
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: false, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            rangeBias: RangeBias, sameBias: SameBias, angleBias: AngleBias, missileBias: MissileBias, playerBias: PlayerBias,
            aimSimple: true, trackType: TurretAI.TrackFluidInertia,
            trackAccelPitch: TrackAccelPitch, trackAccelRot: TrackAccelRot, trackBlendRate: TrackBlendRate,
            fireCheckFlags: FireCheck);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // turret_hellion_missile_think (hellion_weapon.qc): a heat-seeking missile that predicts the lead point and
    // accelerates each frame (speed *= gain, capped at max) until it intercepts.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // QC turret_projectile: launch dir is scattered by shot_spread — normalize(dir + randomvec() * 0.08).
        dir = QMath.Normalize(dir + Prandom.Vec() * ShotSpread);

        // QC: the turret path uses owner.shot_radius (80) for the blast + missile proximity/stray basis. The
        // 500 value is the player-only actor.shot_radius=500 branch in wr_think, which never runs for a turret.
        Entity missile = GuidedProjectile.Launch(turret, enemy, st.ShotOrg, dir, GuidedProjectile.Mode.Hellion,
            launchSpeed: ShotSpeed, speedMax: ShotSpeedMax, speedGain: ShotSpeedGain, turnRate: 0.35f,
            size: 6f, health: 10f, ShotDamage, radius: ShotRadius, ShotForce, DeathTypes.TurretHellion, ttl: 9f);

        if (Api.Services is not null)
        {
            // hellion_weapon.qc:35: te_explosion(missile.origin) — a launch puff at the muzzle (the missile's
            // spawn origin = st.ShotOrg). A networked temp-entity emitted server-side so all viewers see it
            // identically (same convention WalkerTurret.FireRocket uses for its own te_explosion launch flash).
            EffectEmitter.TeExplosion(missile.Origin);
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/rocket_fire.wav");
        }

        // hellion_weapon.qc:41-42 (turret branch): ++actor.tur_head.frame — kick the launcher spin on each shot,
        // which starts the tr_think frame cycle (idle frame 0 -> spinning 1..6). Unconditional (unlike HK's
        // start-once kick); Base nets this to clients for the spinning-launcher animation. Server-authoritative.
        turret.Frame += 1f;

        // NOTE (client-render, still gaps): the two-cannon muzzle-tag alternation (hellion_weapon.qc:28-31 selects
        // tag_fire when tur_head.frame != 0 else tag_fire2) and the PROJECTILE_ROCKET trail/smoke. Both need the
        // attached head bone / model tags + the CSQC projectile-type trail (same as the Devastator rocket, which
        // also networks no server-side trail), neither of which the headless port models — the muzzle origin uses
        // the single computed st.ShotOrg. The te_explosion launch puff (above) and the server-side frame state are
        // now faithful.
    }
}
