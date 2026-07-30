using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Hidden / dev player-fired HK weapon — port of <c>common/turrets/turret/hk_weapon.qh</c>
/// (<c>CLASS(HunterKillerAttack, PortoLaunch)</c>) + the <c>IS_PLAYER</c> branch of
/// <c>hk_weapon.qc:METHOD(HunterKillerAttack, wr_think)</c>.
///
/// <para>QC class attributes: <c>WEP_FLAG_SPECIALATTACK | WEP_FLAG_HIDDEN</c>, <c>impulse 9</c>,
/// netname <c>"turret_hk"</c>, base class <c>PortoLaunch</c> (no ammo cost). Low gameplay impact: not
/// reachable through any default weapon arena; only obtainable via <c>impulse 9</c> console command or
/// server-side <c>give weapon_turret_hk</c> (the same slot as Devastator in the weapon groups).</para>
///
/// <para>IS_PLAYER fire sequence (<c>hk_weapon.qc:14-43</c>):
/// <list type="number">
///   <item><c>weapon_prepareattack(thiswep, actor, weaponentity, false, 1)</c> — refire gate (attacktime = 1 s).</item>
///   <item><c>turret_initparams(actor)</c> — seeds the bare PLAYER's tur_* fields. Because a player has none of
///     those fields set, this falls through to the GENERIC turret defaults (dmg 50 / radius 25 / force 37.5 /
///     speed 2500), NOT the HK turret balance — see the constant block below.</item>
///   <item><c>W_SetupShot_Dir(actor, weaponentity, v_forward, …)</c> → muzzle origin + view direction (w_shotorg / w_shotdir).</item>
///   <item><c>actor.tur_shotdir_updated = w_shotdir; actor.tur_shotorg = w_shotorg; actor.tur_head = actor</c>.</item>
///   <item><c>weapon_thinkf(…, WFRAME_FIRE1, 0.5, w_ready)</c> — 0.5 s fire animation, return to READY.</item>
///   <item>Shared with the turret branch: <c>turret_projectile(…, PROJECTILE_ROCKET, …)</c>, <c>te_explosion</c> muzzle
///     flash, <c>setthink(missile, turret_hk_missile_think)</c>, <c>nextthink = time + 0.25</c>, <c>MOVETYPE_BOUNCEMISSILE</c>,
///     <c>velocity = tur_shotdir_updated * (shot_speed * 0.75)</c>, <c>cnt = time + 30</c>,
///     <c>missile_flags = MIF_SPLASH | MIF_PROXY | MIF_GUIDED_AI</c>.</item>
///   <item>The head-frame kick <c>(++tur_head.frame)</c> is skipped for IS_PLAYER (QC line 40: <c>if (!isPlayer …)</c>).</item>
/// </list></para>
///
/// <para>Guidance is identical to the turret branch: the missile follows <see cref="GuidedProjectile.HkThink"/>
/// (obstacle-avoiding 5-ray funnel steering with accel/decel/panic) for up to 30 s of fuel, then goes inert
/// (<see cref="MoveType.Bounce"/>). The player has no pre-acquired enemy, so <c>missile.Enemy</c> starts null
/// and the re-seek logic in <see cref="GuidedProjectile.HkThink"/> will acquire the nearest valid target within
/// 5000u on the first think.</para>
/// </summary>
// The hidden player-fired HK weapon (WEP_HK / HunterKillerAttack). WEP_FLAG_SPECIALATTACK | WEP_FLAG_HIDDEN
// (hk_weapon.qh) — like every other SpecialAttack weapon it is auto-skipped from the by-id / weapon-priority
// order (WeaponOrder.ByIdOrder / fixPriorityList already filter SpecialAttack), so registering it only makes
// the impulse-9 / `give weapon_turret_hk` fire path live through WeaponFireDriver; it never enters the normal
// weapon cycle. The WeaponByIdTests `skipped` count is updated in lockstep (ball-stealer + this HK = 2).
[Weapon]
public sealed class HunterKillerWeapon : Weapon
{
    // IMPORTANT — the player weapon does NOT use the HK *turret* balance.
    //
    // QC wr_think's IS_PLAYER branch calls turret_initparams(actor) on a bare PLAYER (hk_weapon.qc:22). The
    // player has none of the tur_* fields set (those are seeded by hk.qc tr_setup ONLY on a turret edict), so
    // turret_initparams (sv_turrets.qc:1183 — `TRY(x) ? x : default`) falls through to its GENERIC defaults:
    //     shot_refire = 1   shot_dmg = shot_refire*50 = 50   shot_radius = shot_dmg*0.5 = 25
    //     shot_speed  = 2500   shot_force = shot_dmg*0.5 + shot_radius*0.5 = 37.5
    // The missile detonation (turret_projectile_explode) reads actor.shot_dmg/shot_radius/shot_force, and the
    // launch velocity is actor.tur_shotdir_updated * (actor.shot_speed * 0.75) — all the ACTOR's fields — so a
    // player-fired HK rocket is a weak, very fast dud, distinct from the turret's tuned 120/200/600 @ 375.
    //
    // The guidance speed BAND (max/turnrate/accel/decel) is a separate story: turret_hk_missile_think reads the
    // autocvar_g_turrets_unit_hk_shot_speed_* cvars DIRECTLY (not actor fields), so the band stays the HK cvars
    // (500/1000/0.25/1.025/1.05/0.9) for both the turret and the player rocket.
    private const float ShotSpeed    = 2500f;  // turret_initparams default (NOT g_turrets_unit_hk_shot_speed)
    private const float ShotSpeedMax = 1000f;  // g_turrets_unit_hk_shot_speed_max (guidance band, autocvar in missile_think)
    private const float ShotTurnRate = 0.25f;  // g_turrets_unit_hk_shot_speed_turnrate (guidance band)
    private const float ShotAccel    = 1.025f; // g_turrets_unit_hk_shot_speed_accel (guidance band)
    private const float ShotAccel2   = 1.05f;  // g_turrets_unit_hk_shot_speed_accel2 (guidance band)
    private const float ShotDecel    = 0.9f;   // g_turrets_unit_hk_shot_speed_decel (guidance band)
    private const float ShotDamage   = 50f;    // turret_initparams default: shot_refire(1) * 50
    private const float ShotRadius   = 25f;    // turret_initparams default: shot_dmg(50) * 0.5
    private const float ShotForce    = 37.5f;  // turret_initparams default: shot_dmg*0.5 + shot_radius*0.5

    public HunterKillerWeapon()
    {
        NetName     = "turret_hk";           // QC hk_weapon.qh: ATTRIB(HunterKillerAttack, netname, string, "turret_hk")
        DisplayName = "Hunter-Killer";       // QC hk_weapon.qh: ATTRIB(HunterKillerAttack, m_name, string, _("Hunter-Killer"))
        Impulse     = 9;                     // QC hk_weapon.qh: ATTRIB(HunterKillerAttack, impulse, int, 9)
        // WEP_FLAG_HIDDEN | WEP_FLAG_SPECIALATTACK (hk_weapon.qh)
        SpawnFlags  = WeaponFlags.Hidden | WeaponFlags.SpecialAttack;
        // No AmmoType — PortoLaunch base (like Porto/Hook) carries no ammo; WrCheckAmmo returns true.
    }

    /// <summary>
    /// IS_PLAYER branch of <c>METHOD(HunterKillerAttack, wr_think)</c> (hk_weapon.qc:14-43).
    ///
    /// QC flow: <c>weapon_prepareattack → turret_initparams (no-op here) → W_SetupShot_Dir →
    /// weapon_thinkf(WFRAME_FIRE1, 0.5, w_ready) → turret_projectile → te_explosion → setthink →
    /// MOVETYPE_BOUNCEMISSILE → velocity * 0.75 → cnt = now + 30 → missile_flags</c>.
    ///
    /// The enemy is null at launch (the player has no acquired target); the HkThink re-seek will pick it up.
    /// </summary>
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire != FireMode.Primary) return;

        // weapon_prepareattack(thiswep, actor, weaponentity, false, 1): attacktime literal = 1 s. ATTACK_FINISHED
        // advances by 1 s. PrepareAttack gates the held-fire-button check + WS_READY check + ATTACK_FINISHED;
        // weapon_thinkf(WFRAME_FIRE1, 0.5, w_ready) is wired via AnimtimeFor (= 0.5 s) inside PrepareAttack.
        if (!PrepareAttack(actor, slot, fire, attackTime: float.NaN)) return;

        // W_SetupShot_Dir(actor, weaponentity, v_forward, false, 0, SND_HunterKillerAttack_FIRE, CH_WEAPON_B, 0, …)
        // derives the muzzle origin (w_shotorg) and shot direction (w_shotdir) from the player's view.
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        // turret_projectile(actor, SND, size=6, health=10, DEATH_TURRET_HK, PROJECTILE_ROCKET, false, false)
        // then the shared fire block (velocity, movetype, cnt, flags, think).
        // actor.Enemy is null (player has no pre-acquired target); HkThink re-seek picks one up on first think.
        Entity missile = GuidedProjectile.Launch(actor, enemy: null, shot.Origin, shot.Dir,
            GuidedProjectile.Mode.Hk,
            launchSpeed: ShotSpeed * 0.75f,   // QC: velocity = tur_shotdir_updated * (actor.shot_speed * 0.75) = 2500*0.75 = 1875
            speedMax:    ShotSpeedMax,
            speedGain:   1f,
            turnRate:    ShotTurnRate,
            size:        6f,                   // QC turret_projectile size 6
            health:      10f,                  // QC turret_projectile health 10 (FLAC-shootable)
            ShotDamage, ShotRadius, ShotForce,
            DeathTypes.TurretHk,
            ttl:         30f,                  // QC cnt = time + 30 (fuel window and sole lifecycle gate)
            accel:       ShotAccel,
            accel2:      ShotAccel2,
            decel:       ShotDecel,
            fuelTime:    30f);

        // QC hk_weapon.qc:30: te_explosion(missile.origin) — muzzle flash at the spawn origin.
        // QC hk_weapon.qc:40-42: if (!isPlayer && actor.tur_head.frame == 0) ++actor.tur_head.frame —
        // the IS_PLAYER branch explicitly skips the head-frame kick, so we do NOT touch actor.Frame here.
        if (Api.Services is not null)
        {
            EffectEmitter.TeExplosion(missile.Origin);
            // W_Sound("rocket_fire") on CH_WEAPON_B (SND_HunterKillerAttack_FIRE).
            Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/rocket_fire.wav");
        }
    }

    /// <summary>
    /// QC <c>weapon_thinkf(…, WFRAME_FIRE1, 0.5, w_ready)</c> — return to READY after the 0.5 s fire animation.
    /// This is the animtime arm of the QC fire gate (the refire arm is 1 s via <see cref="RefireFor"/>).
    /// </summary>
    public override float AnimtimeFor(FireMode fire) => 0.5f;

    /// <summary>
    /// QC <c>weapon_prepareattack(thiswep, actor, weaponentity, false, 1)</c> — the literal <c>1</c> is the
    /// <c>attacktime</c> (not a boolean), so ATTACK_FINISHED advances by 1 s per shot. This matches the turret's
    /// <c>shot_refire = 5</c> being the TURRET cadence; the player weapon uses the literal-1 refire from the QC
    /// call, which is the per-shot attacktime the weaponsystem applies.
    /// </summary>
    public override float RefireFor(FireMode fire) => 1f;
}
