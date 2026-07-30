using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Plasma Cannon Turret — port of common/turrets/turret/plasma.{qh,qc} (+ plasma_weapon.qc). Fires slow
/// electric plasma balls that burst on impact for splash damage (like the Electro), aiming for the ground at
/// the target's feet (TFL_AIM_SPLASH). Identity/hitbox from plasma.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_plasma_*</c>).
///
/// Mechanic: a single splash projectile per shot (TUR_FLAG_SPLASH | TUR_FLAG_MEDPROJ) via
/// <see cref="TurretSpawn.Projectile"/>, OR — when mutator_instagib is enabled — an instant instakill
/// railgun beam (plasma.qc tr_attack, 1e10 dmg / 800 force). The muzzle / hit-beam / head-frame effects are client-render.
/// </summary>
[Turret]
public class PlasmaTurret : Turret  // non-sealed: PlasmaDualTurret derives from it (QC CLASS(DualPlasmaTurret, PlasmaTurret))
{
    // --- balance (turrets.cfg g_turrets_unit_plasma_*) ---
    protected const float ShotDamage = 80f;
    protected const float ShotRadius = 150f;
    protected const float ShotSpeed = 2000f;
    protected const float ShotForce = 100f;
    protected const float ShotSpread = 0.015f;  // turrets.cfg g_turrets_unit_plasma_shot_spread (was 0.0125 initparams fallback)
    private const float ShotRefire = 0.6f;
    protected const float TargetRange = 3500f;
    protected const float TargetRangeMin = 200f;
    protected const float TargetRangeOptimal = 500f;
    private const float AmmoMax = 640f;
    private const float AmmoRecharge = 40f;
    private const float AimSpeed = 200f;
    protected const float AimMaxPitch = 30f;
    protected const float AimMaxRot = 360f;
    private const float FireTolerance = 120f;

    // QC sv_turrets default target_select: LOS, team-checked, range + players + angle limits. TUR_FLAG_SPLASH
    // adds TFL_AIM_SPLASH (aim at the feet); the default aim is LEAD | SHOTTIMECOMPENSATE.
    protected const int Select = TurretAI.SelectLos | TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                               | TurretAI.SelectTeamCheck | TurretAI.SelectAngleLimits;

    // --- target_select biases + track motor tuning (turrets.cfg g_turrets_unit_plasma_*) ---
    // Base tr_setup aim_flags = LEAD | SHOTTIMECOMPENSATE | SPLASH (NO ZPREDICT — see MakeParams).
    protected const float SelectRangeBias = 0.5f;   // target_select_rangebias
    protected const float SelectSameBias = 0.01f;   // target_select_samebias
    protected const float SelectAngleBias = 0.25f;  // target_select_anglebias
    protected const float SelectPlayerBias = 1f;    // target_select_playerbias
    protected const float SelectMissileBias = 0f;   // target_select_missilebias
    protected const float TrackAccelPitch = 0.5f;   // track_accel_pitch
    protected const float TrackAccelRot = 0.7f;     // track_accel_rot
    protected const float TrackBlendRate = 0.2f;    // track_blendrate

    /// <summary>
    /// Build the shared plasma combat params (reused by the dual variant with overridden balance).
    /// The select-bias / track-tuning params default to the plasma cfg values; the dual variant overrides
    /// them (rangebias 0.2 / samebias 0.4 / anglebias 0.4) by passing its own.
    /// </summary>
    protected static TurretParams MakeParams(float rangeMin, float rangeMax, float refire, float aimSpeed,
        float fireTolerance, float rangeBias = SelectRangeBias, float sameBias = SelectSameBias,
        float angleBias = SelectAngleBias, float missileBias = SelectMissileBias, float playerBias = SelectPlayerBias,
        float trackAccelRot = TrackAccelRot, float trackBlendRate = TrackBlendRate)
        // QC plasma tr_setup aim_flags = LEAD | SHOTTIMECOMPENSATE | SPLASH; it does NOT set TFL_AIM_ZPREDICT
        // (zPredict stays false so airborne targets aren't mis-led down their gravity arc).
        => new(Select, rangeMin, rangeMax, ShotDamage, refire, aimSpeed, fireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: false, aimSplash: true,
            rangeBias: rangeBias, sameBias: sameBias, angleBias: angleBias, missileBias: missileBias,
            playerBias: playerBias,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: TrackAccelPitch, trackAccelRot: trackAccelRot,
            trackBlendRate: trackBlendRate,
            // QC plasma.qc:51 `firecheck_flags |= TFL_FIRECHECK_AFF` — the plasma adds avoid-friendly-fire on top
            // of the framework default (DEAD|DISTANCES|LOS|AIMDIST|TEAMCHECK|AMMO_OWN|REFIRE). The dual variant
            // inherits this (no firecheck override of its own).
            fireCheckFlags: TurretAI.FireCheckDefault | TurretAI.FireCheckAff);

    public PlasmaTurret()
    {
        NetName = "plasma";
        DisplayName = "Plasma Cannon";
        Model = "models/turrets/base.md3";
        HeadModel = "models/turrets/plasma.md3";  // QC plasma.qh head_model ATTRIB (the spinning plasma-cannon head)
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        // QC plasma.qc tr_setup: `it.damage_flags |= TFL_DMG_HEADSHAKE` — a hit jitters the head off-aim by
        // ±damage on pitch+yaw (TurretAI.EventDamage applies it; the dual inherits this, no tr_setup override).
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0, headShake: true);

    public override void Think(Entity e)
    {
        // QC plasma.qc tr_think: the head spin cosmetic kicked off by each shot. Once a shot sets frame=1
        // (in Attack), advance it every think and wrap 1..5 back to 0 (a 5-frame spin per shot). Base drives a
        // separate tur_head entity's frame; the port has no head bone entity, so — like Hk/Hellion turrets — the
        // cycle runs on the turret edict's own networked Entity.Frame.
        if (e.Frame != 0f)
            e.Frame += 1f;
        if (e.Frame > 5f)
            e.Frame = 0f;

        var p = MakeParams(TargetRangeMin, TargetRange, ShotRefire, AimSpeed, FireTolerance);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // METHOD(PlasmaTurret, tr_attack) — plasma.qc: when instagib is on, fire an instant railgun beam
    // (FireRailgunBullet, 1e10 dmg / 800 force — an instakill) instead of the plasma ball; otherwise one
    // splash plasma ball forward (plasma_weapon.qc, MIF_SPLASH) with the deterministic shot_spread cone.
    protected virtual void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        if (InstagibEnabled())
            FireInstagibBeam(turret, st.ShotOrg, dir);
        else
        {
            // QC plasma_weapon.qc: turret_projectile(..., PROJECTILE_ELECTRO_BEAM, ...). The client classifies a
            // model-less turret bolt by its networked classname+netname (ProjectileCatalog.Classify keys
            // "electro_bolt"/"elaser" onto PROJECTILE_ELECTRO_BEAM); the projType gives it the blue plasma trail
            // (the helper stamps it, matching QC's _proj_type arg).
            TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
                ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretPlasma, spread: ShotSpread,
                projType: "electro_bolt");

            if (Api.Services is not null)
            {
                Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");
                // QC plasma_weapon.qc:23 (PlasmaAttack.wr_think): Send_Effect(EFFECT_BLASTER_MUZZLEFLASH,
                // tur_shotorg, tur_shotdir_updated * 1000, 1) — emitted unconditionally after the projectile.
                EffectEmitter.Emit("BLASTER_MUZZLEFLASH", st.ShotOrg, dir * 1000f, 1);
            }
        }

        // QC plasma.qc tr_attack: `if (it.tur_head.frame == 0) it.tur_head.frame = 1` — kick off the 5-frame head
        // spin (advanced + wrapped each think in Think). Runs after both the instagib and plasma-ball branches.
        if (turret.Frame == 0f)
            turret.Frame = 1f;

        // The PROJECTILE_ELECTRO_BEAM CSQC trail (projType "electro_bolt") and EFFECT_BLASTER_MUZZLEFLASH
        // (emitted above) are now wired. Remaining client-render gap: the head frame is networked via
        // Entity.Frame, but the static base.md3 body has no spin frames — the real head model plasma.md3 needs a
        // tur_head sub-entity / CSQC turret net layer to render it (no turret net layer exists in the port yet).
    }

    /// <summary>
    /// QC instagib <c>tr_attack</c> beam: <c>FireRailgunBullet(it, ..., 1e10 damage, false, 800 force, ...)</c> —
    /// an instant instakill hitscan with no fire sound (Base passes an empty snd_fire). Factored out so the dual
    /// variant's inlined instagib branch (plasma_dual.qc, which does NOT call SUPER) can reuse it WITHOUT the
    /// single-plasma head-frame start-kick that lives in <see cref="Attack"/>.
    /// </summary>
    protected static void FireInstagibBeam(Entity turret, Vector3 shotOrg, Vector3 dir)
    {
        TurretCombat.FireBullet(turret, shotOrg, dir, spread: 0f,
            damage: InstagibRailDamage, force: InstagibRailForce, DeathTypes.TurretPlasma, out Vector3 endPos);

        // QC plasma.qc tr_attack instagib branch (after FireRailgunBullet):
        //   Send_Effect(EFFECT_VORTEX_MUZZLEFLASH, tur_shotorg, tur_shotdir_updated * 1000, 1);
        //   vector v = WarpZone_UnTransformOrigin(WarpZone_trace_transform, trace_endpos);
        //   vector rgb = Team_ColorRGB(it.team);
        //   Send_Effect_Except(EFFECT_VAPORIZER_BEAM, tur_shotorg, v, 1, rgb, rgb, NULL);
        // The muzzle flash, then the team-coloured rail beam swept muzzle -> trace impact. (Warpzone untransform
        // is a no-op without warpzones — endPos is already the world impact point.) VAPORIZER_BEAM is a trail
        // effect, so its Velocity arg is the beam end and the count is ignored (matching the Vaporizer weapon).
        if (Api.Services is not null)
        {
            EffectEmitter.Emit("VORTEX_MUZZLEFLASH", shotOrg, dir * 1000f, 1);
            Vector3 rgb = Teams.ColorRgb((int)turret.Team);
            var beam = Effects.ByName("VAPORIZER_BEAM");
            EffectEmitter.Emit(beam, shotOrg, endPos, 0, rgb, rgb, except: null);
        }
    }

    /// <summary>QC FireRailgunBullet damage for the instagib plasma turret (plasma.qc tr_attack: 1e10, an
    /// instakill). Base passes 1e10 as the damage arg and 800 as the force arg.</summary>
    protected const float InstagibRailDamage = 10000000000f;  // 1e10

    /// <summary>QC FireRailgunBullet force for the instagib plasma turret (plasma.qc tr_attack: 800).</summary>
    protected const float InstagibRailForce = 800f;

    /// <summary>QC <c>MUTATOR_IS_ENABLED(mutator_instagib)</c> — resolves to <c>g_instagib != 0</c>.</summary>
    private protected static bool InstagibEnabled()
        => Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;
}
