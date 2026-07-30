using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// FLAC Cannon Turret — port of common/turrets/turret/flac.{qh,qc} (+ flac_weapon.qc). An anti-projectile flak
/// turret: it targets ONLY enemy missiles/projectiles (mortar grenades, electro balls, rockets…) and lobs
/// timed flak shells that air-burst near them to shoot them down. Identity/hitbox from flac.qh; balance from
/// turrets.cfg (<c>g_turrets_unit_flac_*</c>).
///
/// Mechanic: a fast splash projectile (TUR_FLAG_SPLASH | TUR_FLAG_FASTPROJ | TUR_FLAG_MISSILE) that explodes
/// on a short fuse near the predicted intercept (flac_weapon.qc turret_flac_projectile_think_explode). The
/// timed-fuse air-burst nudge toward the target is summarized as an impact-radius blast here.
/// </summary>
[Turret]
public sealed class FlacTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_flac_*) ---
    // Internal so FlacWeapon (the hidden player form in the same file) can share the same balance constants
    // without duplicating them (QC shares them via the same wr_think method branching on IS_PLAYER).
    internal const float PubShotDamage = 20f;
    internal const float PubShotRadius = 100f;
    internal const float PubShotSpeed  = 9000f;
    internal const float PubShotForce  = 25f;
    internal const float PubShotSpread = 0.02f;
    private const float ShotDamage = PubShotDamage;
    private const float ShotRadius = PubShotRadius;
    private const float ShotSpeed  = PubShotSpeed;
    private const float ShotForce  = PubShotForce;
    private const float ShotSpread = PubShotSpread;
    private const float ShotRefire = 0.1f;
    private const float TargetRange = 4000f;
    private const float TargetRangeMin = 500f;
    private const float TargetRangeOptimal = 1250f;
    private const float AmmoMax = 1000f;
    private const float AmmoRecharge = 100f;
    private const float AimSpeed = 200f;
    private const float AimMaxPitch = 35f;
    private const float AimMaxRot = 360f;
    private const float FireTolerance = 150f;
    private const float RespawnTime = 90f;

    // QC target_select_*bias (turrets.cfg:73-77): rangebias 0.25, samebias 1, anglebias 0.5,
    // playerbias 0 (moot — MISSILESONLY rejects players), missilebias 1.
    private const float RangeBias = 0.25f;
    private const float SameBias = 1f;
    private const float AngleBias = 0.5f;
    private const float PlayerBias = 0f;
    private const float MissileBias = 1f;

    // QC track_* (turrets.cfg:87-90): track_type 3 (fluid-inertia), accel_pitch 0.5, accel_rot 0.7, blendrate 0.2.
    private const float TrackAccelPitch = 0.5f;
    private const float TrackAccelRot = 0.7f;
    private const float TrackBlendRate = 0.2f;
    // QC flac sets tur_impacttime = 10 default; the fuse is the predicted traveltime + a small jitter.

    // QC flac.qc tr_setup adds NOTURRETS | MISSILESONLY on top of the sv_turrets default (range/team/missiles).
    private const int Select = TurretAI.SelectRangeLimits | TurretAI.SelectTeamCheck
                             | TurretAI.SelectMissiles | TurretAI.SelectMissilesOnly
                             | TurretAI.SelectNoTurrets;

    public FlacTurret()
    {
        NetName = "flac";
        DisplayName = "FLAC Cannon";
        Model = "models/turrets/base.md3";
        // QC flac.qh: head_model = strcat("models/turrets/", "flac.md3") — the separate head-bone model carried
        // on top of the base body (the 4-frame cycle in Attack drives its frame). Carried as identity data
        // (matches Base flac.qh:12 + the FusionReactorTurret.HeadModel pattern); no client turret render
        // integrates the head bone yet (whole-turret-family presentation gap), but the head model is now set.
        HeadModel = "models/turrets/flac.md3";
        StartHealth = 700f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        // QC flac.qc tr_setup: `it.damage_flags |= TFL_DMG_HEADSHAKE` — a hit jolts the head off-aim by ±take on
        // pitch+yaw (TurretAI.Damage applies it, gated on st.HeadShake; same as machinegun/plasma/mlrs turrets).
        => TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 64f),
            AmmoMax, AmmoRecharge, shotVolly: 0, respawnTime: RespawnTime, energyAmmo: false, headShake: true);

    public override void Think(Entity e)
    {
        // No splash AIM (the shell air-bursts itself at the intercept); just lead + shot-time compensate so the
        // fuse meets a fast projectile target (QC flac.qc tr_setup aim_flags = TFL_AIM_LEAD | TFL_AIM_SHOTTIMECOMPENSATE
        // — NO zPredict). Per-unit scoring biases + track-motor accel/blend from turrets.cfg.
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true,
            rangeBias: RangeBias, sameBias: SameBias, angleBias: AngleBias, missileBias: MissileBias, playerBias: PlayerBias,
            trackType: TurretAI.TrackFluidInertia, trackAccelPitch: TrackAccelPitch,
            trackAccelRot: TrackAccelRot, trackBlendRate: TrackBlendRate);
        TurretAI.RunCombat(e, in p, Attack);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // METHOD(FlacAttack, wr_think) — flac_weapon.qc: a flak shell on a timed fuse that air-bursts near the
    // target. RadiusDamage(... shot_dmg, shot_dmg ...) means full damage to the rim (edge == core) vs missiles.
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // QC flac_weapon.qc:26 networks the shell as PROJECTILE_HAGAR (NOT PROJECTILE_FLAC — that id is the
        // Seeker's, seeker.qc:304). The shared TurretSpawn.Projectile gives every turret missile the generic
        // className "turret_projectile", which the client ProjectileCatalog.Classify can't key on; the projType
        // arg "hagar" makes the networked catalog key resolve to ProjectileType.Hagar so it draws the
        // HAGAR_ROCKET trail at scale 0.75, exactly as Base does (the helper stamps it, QC's _proj_type).
        Entity shell = TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 5f, health: 0f,
            ShotDamage, edgeDamage: ShotDamage, ShotRadius, ShotForce, DeathTypes.TurretFlac, spread: ShotSpread,
            projType: "hagar");

        // Timed fuse: detonate at the predicted intercept (QC nextthink = time + tur_impacttime + jitter). At
        // detonation, if the enemy is within shot_radius*3, snap the burst point onto it + a random offset so
        // the air-burst actually catches the dodging projectile (turret_flac_projectile_think_explode).
        //
        // tur_impacttime was computed this think by turret_do_updates (TurretAI.UpdateImpact): a forward tracebox
        // from the muzzle along the actual head forward to the aimpos distance, then vlen(shotorg - trace_endpos)
        // / shot_speed (sv_turrets.qc:519-523). Geometry between the muzzle and the aim point shortens the fuse to
        // where the shell actually detonates; a clear path yields DistAimPos / ShotSpeed.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float jitter = Prandom.Float() * 0.01f - Prandom.Float() * 0.01f;
        shell.NextThink = now + st.ImpactTime + jitter;

        var owner = turret;
        shell.Think = self =>
        {
            if (self.Enemy is not null && !self.Enemy.IsFreed
                && (self.Origin - self.Enemy.Origin).Length() < ShotRadius * 3f
                && Api.Services is not null)
            {
                Api.Entities.SetOrigin(self, self.Enemy.Origin + Prandom.Vec() * ShotRadius);
            }
            self.Touch = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, ShotDamage, ShotDamage, ShotRadius, owner,
                0, ShotForce, deathTag: DeathTypes.TurretFlac);
            if (Api.Services is not null) Api.Entities.Remove(self);
            TurretAI.Forget(self);
        };

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");

        // QC flac_weapon.qc:30 — Send_Effect(EFFECT_BLASTER_MUZZLEFLASH, tur_shotorg, tur_shotdir_updated*1000, 1).
        EffectEmitter.Emit("BLASTER_MUZZLEFLASH", st.ShotOrg, dir * 1000f, 1);

        // QC flac_weapon.qc:32-37 (turret-AI branch only): cycle the head model frame each shot —
        // ++tur_head.frame; if (frame >= 4) frame = 0. The port has no separate tur_head sub-entity, so — like
        // PlasmaTurret — the cycle runs on the turret edict's own networked Entity.Frame (4-frame loop 0..3).
        turret.Frame += 1f;
        if (turret.Frame >= 4f)
            turret.Frame = 0f;
    }
}

/// <summary>
/// Hidden / dev player-fired FLAC weapon — port of <c>common/turrets/turret/flac_weapon.qh</c>
/// (<c>CLASS(FlacAttack, PortoLaunch)</c>) + the <c>IS_PLAYER</c> branch of
/// <c>flac_weapon.qc:METHOD(FlacAttack, wr_think)</c>.
///
/// <para>QC class attributes: <c>WEP_FLAG_SPECIALATTACK | WEP_FLAG_HIDDEN</c>, <c>impulse 5</c>,
/// netname <c>"turret_flac"</c>, base class <c>PortoLaunch</c> (no ammo cost). Low impact: not reachable
/// through any default weapon arena; only obtainable via <c>impulse 5</c> console command.</para>
///
/// <para>IS_PLAYER fire sequence (flac_weapon.qc:8-22, 24-37):
/// <list type="number">
///   <item><c>turret_initparams(actor)</c> — seeds tur_* fields (no-op here; values captured as constants).</item>
///   <item><c>W_SetupShot_Dir(v_forward)</c> → muzzle origin + direction from player view.</item>
///   <item>Sets <c>tur_impacttime = 10</c> — fixed 10 s fuse (unlike the turret's forward-traced traveltime).</item>
///   <item><c>weapon_thinkf(WFRAME_FIRE1, 0.5, w_ready)</c> → 0.5 s fire animation (animtime/refire).</item>
///   <item>Shared: <c>turret_projectile(…, PROJECTILE_HAGAR, …)</c>, fuse think override, fire sound.</item>
/// </list></para>
/// </summary>
// WEP_TUR_FLAC — REGISTER_WEAPON(FlacAttack) (flac_weapon.qh): WEP_FLAG_SPECIALATTACK | WEP_FLAG_HIDDEN,
// impulse 5. The [Weapon] attribute is now ON, making it impulse-selectable via `impulse 5` exactly like Base.
// FlacWeapon carries WeaponFlags.SpecialAttack, so the by-id ordering (WeaponOrder.IsImpulseSkipped) skips it —
// the by-id indices + menu weapon order are unaffected. Impulse-5 group cycling is safe: GetCycleWeapon filters
// Hidden unowned weapons, and the Raptor vehicles_impulse switch only handles 1/2/10/11/12 (not 5).
// WeaponByIdTests.ByIdOrder_Covers_Every_ImpulseReachable_Weapon_Exactly_Once reflects the new skipped-weapon
// count (ball-stealer + HunterKiller + FlacWeapon = 3). The FLAC TURRET itself fires via TurretSpawn.
[Weapon]
public sealed class FlacWeapon : Weapon
{
    // Shared balance (turrets.cfg g_turrets_unit_flac_*) — same constants as FlacTurret, exposed via
    // the Pub* aliases so this class doesn't duplicate magic numbers.
    private const float ShotDamage = FlacTurret.PubShotDamage;
    private const float ShotRadius = FlacTurret.PubShotRadius;
    private const float ShotSpeed  = FlacTurret.PubShotSpeed;
    private const float ShotForce  = FlacTurret.PubShotForce;
    private const float ShotSpread = FlacTurret.PubShotSpread;

    /// <summary>QC IS_PLAYER branch: <c>actor.tur_impacttime = 10</c> — fixed 10 s fuse.</summary>
    private const float PlayerFuse = 10f;

    public FlacWeapon()
    {
        NetName     = "turret_flac";    // QC flac_weapon.qh netname "turret_flac"
        DisplayName = "FLAC";           // QC flac_weapon.qh m_name _("FLAC")
        Impulse     = 5;                // QC: impulse 5
        // WEP_FLAG_SPECIALATTACK | WEP_FLAG_HIDDEN (flac_weapon.qh)
        SpawnFlags  = WeaponFlags.SpecialAttack | WeaponFlags.Hidden;
        // No AmmoType — PortoLaunch carries no ammo (like Porto: WrCheckAmmo returns true by default).
    }

    /// <summary>
    /// IS_PLAYER branch of <c>METHOD(FlacAttack, wr_think)</c>: fires a flak shell on a fixed 10 s fuse.
    /// The QC flow is: <c>weapon_prepareattack → setup_shot → turret_projectile → override nextthink →
    /// Send_Effect (BLASTER muzzle flash) → no head-frame (isPlayer branch skips it)</c>.
    /// </summary>
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire != FireMode.Primary) return;

        // weapon_prepareattack(thiswep, actor, weaponentity, false, 1) — args are (secondary=false, attacktime=1):
        // the literal 1 is the ATTACKTIME (one full second), NOT a boolean. So ATTACK_FINISHED advances by 1 s and
        // that is what gates the real refire rate. PrepareAttack handles the WS_READY gate + ATTACK_FINISHED clock;
        // passing NaN uses RefireFor (=1 s here, matching the attacktime). The SEPARATE weapon_thinkf(WFRAME_FIRE1,
        // 0.5, w_ready) — AnimtimeFor (=0.5 s) — schedules the return-to-ready animation, shorter than the refire.
        if (!PrepareAttack(actor, slot, fire, attackTime: float.NaN)) return;

        // W_SetupShot_Dir(actor, weaponentity, v_forward, false, 0, SND_FlacAttack_FIRE, CH_WEAPON_B, 0, …)
        // derives muzzle origin (w_shotorg) and aim direction (w_shotdir) from the player's view.
        // QC makevectors(actor.v_angle) → v_forward; port convention: actor.Angles is the view angle (QC v_angle).
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        // turret_tag_fire_update → no-op for IS_PLAYER (QC runs it AFTER the isPlayer block, but for a
        // player actor the tag_fire is the view muzzle — already captured by SetupShot above).

        // turret_projectile(actor, SND_FlacAttack_FIRE, size 5, health 0, DEATH_TURRET_FLAC, …)
        // actor.Enemy may be null (player has no acquired missile target); the snap-to-enemy branch in
        // the fuse think is guarded by `enemy != null` so null is safe.
        // QC flac_weapon.qc:26 networks the shell as PROJECTILE_HAGAR — projType "hagar" makes the client
        // ProjectileCatalog resolve the HAGAR_ROCKET trail (see the turret branch in FlacTurret.Attack).
        Entity shell = TurretSpawn.Projectile(actor, shot.Origin, shot.Dir, ShotSpeed,
            size: 5f, health: 0f,
            ShotDamage, edgeDamage: ShotDamage, ShotRadius, ShotForce,
            DeathTypes.TurretFlac, spread: ShotSpread, projType: "hagar");

        // actor.tur_impacttime = 10 (fixed fuse, QC flac_weapon.qc:20).
        // QC: proj.nextthink = time + actor.tur_impacttime + (random()*0.01 - random()*0.01)
        float now    = Api.Services is not null ? Api.Clock.Time : 0f;
        float jitter = Prandom.Float() * 0.01f - Prandom.Float() * 0.01f;
        shell.NextThink = now + PlayerFuse + jitter;

        // Override Think to turret_flac_projectile_think_explode: snap to enemy if close, then RadiusDamage.
        Entity owner = actor;
        shell.Think = self =>
        {
            if (self.Enemy is not null && !self.Enemy.IsFreed
                && (self.Origin - self.Enemy.Origin).Length() < ShotRadius * 3f
                && Api.Services is not null)
            {
                Api.Entities.SetOrigin(self, self.Enemy.Origin + Prandom.Vec() * ShotRadius);
            }
            self.Touch      = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, ShotDamage, ShotDamage, ShotRadius, owner,
                0, ShotForce, deathTag: DeathTypes.TurretFlac);
            if (Api.Services is not null) Api.Entities.Remove(self);
            TurretAI.Forget(self);
        };

        // Play fire sound (SND_FlacAttack_FIRE = W_Sound("hagar_fire"), CH_WEAPON_B/CH_WEAPON_A).
        if (Api.Services is not null)
            Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/hagar_fire.wav");

        // QC flac_weapon.qc:30 — Send_Effect(EFFECT_BLASTER_MUZZLEFLASH, tur_shotorg, tur_shotdir_updated*1000, 1).
        // This Send_Effect runs unconditionally (after the isPlayer block), so the player form flashes too; only
        // the head-frame cycle (qc:32-37) is gated `if (!isPlayer)` and is correctly omitted here. Base uses the
        // plain Send_Effect (NOT Send_Effect_Except) → it networks to ALL clients including the firing player, so
        // a player firing their own hidden weapon sees their own muzzle flash. No `except` to match.
        EffectEmitter.Emit("BLASTER_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1);
    }

    /// <summary>QC <c>weapon_thinkf(…, WFRAME_FIRE1, 0.5, w_ready)</c> — return to READY after the 0.5 s animation.</summary>
    public override float AnimtimeFor(FireMode fire) => 0.5f;

    /// <summary>QC <c>weapon_prepareattack(thiswep, actor, weaponentity, false, 1)</c> — the attacktime literal
    /// <c>1</c> advances ATTACK_FINISHED by one second, so the real refire is 1 s/shot (longer than the 0.5 s
    /// fire animation).</summary>
    public override float RefireFor(FireMode fire)   => 1f;
}
