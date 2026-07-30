using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Ball Stealer (WEP_NEXBALL) — port of
/// <c>common/gametypes/gametype/nexball/{weapon.qh,sv_weapon.qc}</c>.
///
/// <para>A special weapon granted to basketball carriers in Nexball. It has no ammo and is
/// mutator-blocked (only usable in Nexball basketball mode). Impulse 0 = not in the normal weapon cycle.</para>
///
/// <para><b>Primary fire</b> (<c>wr_think fire&amp;1</c>, refire 0.7 s): shoot the carried ball as a
/// projectile. When <c>g_nexball_basketball_meter</c>=1 (default), the first press starts the charge
/// meter (<c>NB_METERSTART = time</c>); on RELEASE the ball launches with a triangle-wave power
/// multiplier between minpower (0.5) and maxpower (1.2) over <c>g_nexball_meter_period</c>. Without the
/// meter, fires immediately at full power. The ball is drop-launched via the Nexball gametype's
/// <see cref="Nexball.LaunchBall"/> at <c>shotdir * primary_speed * mul</c> (QC
/// <c>W_Nexball_Attack</c>). Plays SND_NB_SHOOT1.</para>
///
/// <para><b>Secondary fire</b> (<c>wr_think fire&amp;2</c>, refire 0.6 s): when carrying and a
/// safe-pass lock is active (ball.enemy set by the PreThink crosshair trace), fires the ball in an arc
/// toward the locked teammate with a homing <c>W_Nexball_Think</c> (steers toward target each frame at
/// <c>g_nexball_safepass_turnrate</c>=0.1). Without a lock, fires a <em>ballstealer</em> tackle
/// projectile (speed 3000, lifetime 0.15 s, <c>EF_BRIGHTFIELD</c>, electro-trail) whose touch shoves
/// the hit carrier and — if conditions allow — steals the ball via
/// <see cref="Nexball.StealBall"/>. Plays SND_NB_SHOOT2.</para>
///
/// <para>QC identity: <c>CLASS(BallStealer, PortoLaunch)</c>; <c>REGISTER_WEAPON(NEXBALL, …)</c>;
/// spawnflags = <c>WEP_TYPE_OTHER | WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_NOTRUEAIM</c>;
/// impulse 0; netname "ballstealer".</para>
/// </summary>
[Weapon]
public sealed class BallStealer : Weapon
{
    // ----- primary balance (QC g_balance_nexball_primary_*) -----
    private const float DefaultPrimarySpeed    = 1000f; // g_balance_nexball_primary_speed
    private const float DefaultPrimaryRefire   = 0.7f;  // g_balance_nexball_primary_refire
    private const float DefaultPrimaryAnimtime = 0.3f;  // g_balance_nexball_primary_animtime

    // ----- secondary balance (QC g_balance_nexball_secondary_*) -----
    private const float DefaultSecondarySpeed    = 3000f;  // g_balance_nexball_secondary_speed
    private const float DefaultSecondaryLifetime = 0.15f;  // g_balance_nexball_secondary_lifetime
    private const float DefaultSecondaryForce    = 500f;   // g_balance_nexball_secondary_force
    private const float DefaultSecondaryRefire   = 0.6f;   // g_balance_nexball_secondary_refire
    private const float DefaultSecondaryAnimtime = 0.3f;   // g_balance_nexball_secondary_animtime

    // ----- power-meter cvars (QC g_nexball_basketball_meter_*) -----
    private const float DefaultMeterMinpower = 0.5f;  // g_nexball_basketball_meter_minpower
    private const float DefaultMeterMaxpower = 1.2f;  // g_nexball_basketball_meter_maxpower

    // ----- DP effect bits -----
    private const int EfBrightField  = 1;           // QC EF_BRIGHTFIELD (DP)
    private const int EfLowPrecision = 4194304;     // QC EF_LOWPRECISION

    // Live-read balance (seeded by Configure / re-seeded on balance reload)
    public float PrimarySpeed;
    public float PrimaryRefire;
    public float PrimaryAnimtime;
    public float SecondarySpeed;
    public float SecondaryLifetime;
    public float SecondaryForce;
    public float SecondaryRefire;
    public float SecondaryAnimtime;

    public BallStealer()
    {
        NetName     = "ballstealer";
        DisplayName = "Ball Stealer";
        Impulse     = 0;
        // QC weapon.qh: WEP_TYPE_OTHER | WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_NOTRUEAIM
        SpawnFlags  = WeaponFlags.TypeOther | WeaponFlags.MutatorBlocked | WeaponFlags.NoTrueAim;
        // Base's MENU program never registers WEP_NEXBALL, so its weapons list can neither show nor append
        // it (see Weapon.MenuRegistered). Without this, opening Settings→Game→Weapons appended "ballstealer"
        // to cl_weaponpriority and archived it into the user's config.cfg forever.
        MenuRegistered = false;
        // QC weapon.qh: no model ATTRIBs (a utility weapon, never dropped as a pickup)
    }

    public override void Configure()
    {
        PrimarySpeed    = Bal("g_balance_nexball_primary_speed",      DefaultPrimarySpeed);
        PrimaryRefire   = Bal("g_balance_nexball_primary_refire",     DefaultPrimaryRefire);
        PrimaryAnimtime = Bal("g_balance_nexball_primary_animtime",   DefaultPrimaryAnimtime);

        SecondarySpeed    = Bal("g_balance_nexball_secondary_speed",    DefaultSecondarySpeed);
        SecondaryLifetime = Bal("g_balance_nexball_secondary_lifetime", DefaultSecondaryLifetime);
        SecondaryForce    = Bal("g_balance_nexball_secondary_force",    DefaultSecondaryForce);
        SecondaryRefire   = Bal("g_balance_nexball_secondary_refire",   DefaultSecondaryRefire);
        SecondaryAnimtime = Bal("g_balance_nexball_secondary_animtime", DefaultSecondaryAnimtime);
    }

    public override float RefireFor(FireMode fire)
        => fire == FireMode.Secondary ? SecondaryRefire : PrimaryRefire;

    // QC wr_think: primary thinkf uses primary_animtime (0.3); secondary thinkf uses secondary_animtime (0.3)
    // — NOT the refire (the refire 0.6/0.7 gates re-fire via weapon_prepareattack's ATTACK_FINISHED).
    public override float AnimtimeFor(FireMode fire)
        => fire == FireMode.Secondary ? SecondaryAnimtime : PrimaryAnimtime;

    // wr_checkammo: always true — ball launcher uses no ammo (QC wr_checkammo1/2 both return true).
    // (PrepareAttack calls CheckAmmo internally; base Weapon.CheckAmmo returns true for ResourceType.None.)

    /// <summary>
    /// QC <c>METHOD(BallStealer, wr_think)</c> (sv_weapon.qc): the combined primary/secondary logic.
    ///
    /// <para>Primary (fire&amp;1, refire 0.7 s):
    ///   If the basketball meter is enabled AND the player is carrying a ball AND no meter start is set:
    ///   record <c>NB_METERSTART = time</c> (begin charge). If primary is NOT held and the meter has
    ///   started, fire with the elapsed charge time (release-fire). Otherwise (no meter) fire immediately
    ///   with mul=1.</para>
    ///
    /// <para>Secondary (fire&amp;2, refire 0.6 s):
    ///   Safe-pass if a pass target is locked, else tackle projectile if <c>g_nexball_tackling</c>!=0.</para>
    /// </summary>
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        float now = Api.Services is null ? 0f : Api.Clock.Time;

        // Resolve the active Nexball gametype instance so we can call LaunchBall / StealBall.
        Nexball? nb = NexballGame();
        if (nb is null)
            return; // not a nexball server — shouldn't be equipped

        // ===== PRIMARY =====
        if (fire == FireMode.Primary)
        {
            bool meterEnabled = nb.MeterEnabled;
            bool carrying     = nb.BallEntity is not null && nb.BallEntity.GtCarrier is not null
                                && ReferenceEquals(actor, nb.BallEntity.GtCarrier);

            if (PrepareAttack(actor, slot, fire))
            {
                // QC: if (meter && carrying && !NB_METERSTART) → start the meter; else schedule thinkf only
                if (meterEnabled && carrying && st.NbMeterStart <= 0f)
                {
                    // Start the charge meter — record the start time.
                    st.NbMeterStart = now;
                    // QC just falls through the if without calling w_ready — the meter has started but no shot
                    // fires yet. Schedule ready so the driver keeps re-calling WrThink each tick.
                    WeaponFireDriver.ScheduleThink(st, 0f, static (pl, sl) => pl.WeaponState(sl).State = WeaponFireState.Ready);
                }
                else if (!meterEnabled)
                {
                    // No meter: immediate full-power shot (mul = -1 → 1.0).
                    DoLaunch(actor, slot, st, nb, t: -1f);
                    WeaponFireDriver.ScheduleThink(st, PrimaryAnimtime,
                        static (pl, sl) => pl.WeaponState(sl).State = WeaponFireState.Ready);
                }
                else
                {
                    // Meter enabled but already carrying — re-schedule ready so the
                    // "not held + meter started" branch below can fire.
                    WeaponFireDriver.ScheduleThink(st, 0f,
                        static (pl, sl) => pl.WeaponState(sl).State = WeaponFireState.Ready);
                }
            }

            // QC: if (!(fire & 1) && NB_METERSTART && carrying) → release-fire with elapsed time.
            // In the port the driver calls WrThink(Primary) every tick. When PRIMARY is NOT held
            // (st.ButtonAttack == false) and a meter is running, fire with the elapsed charge.
            if (!st.ButtonAttack && st.NbMeterStart > 0f && carrying)
            {
                float elapsed = now - st.NbMeterStart;
                st.NbMeterStart = 0f; // QC: DropBall/steal clears it, but also clear on the shot path
                DoLaunch(actor, slot, st, nb, t: elapsed);
                WeaponFireDriver.ScheduleThink(st, PrimaryAnimtime,
                    static (pl, sl) => pl.WeaponState(sl).State = WeaponFireState.Ready);
            }
        }

        // ===== SECONDARY =====
        if (fire == FireMode.Secondary)
        {
            // QC: weapon_prepareattack(secondary_refire); on success W_Nexball_Attack2 + weapon_thinkf(FIRE2,
            // secondary_animtime, w_ready). PrepareAttack already schedules the w_ready return at
            // AnimtimeFor(Secondary) (= secondary_animtime 0.3), so no extra ScheduleThink is needed (and the
            // prior one used SecondaryRefire 0.6, which diverged from QC's animtime).
            if (PrepareAttack(actor, slot, fire))
                DoSecondary(actor, slot, st, nb, now);
        }
    }

    // -----------------------------------------------------------------------
    //  W_Nexball_Attack — primary launch
    // -----------------------------------------------------------------------

    /// <summary>
    /// QC <c>W_Nexball_Attack</c>: launch the carried ball from the shot origin at
    /// <c>w_shotdir * primary_speed * mul</c>. <paramref name="t"/>=−1 fires with mul=1 (immediate,
    /// no meter). <paramref name="t"/>≥0 computes the triangle-wave power multiplier.
    /// Aborts silently when the shot origin is inside solid (QC tracebox guard). Plays SND_NB_SHOOT1.
    /// </summary>
    private void DoLaunch(Entity actor, WeaponSlot slot, WeaponSlotState st, Nexball nb, float t)
    {
        if (nb.BallEntity is null) return; // sanity: no ball to shoot

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward,
            new Vector3(-16f, -16f, -16f), new Vector3(16f, 16f, 16f));

        // QC: tracebox(w_shotorg, BALL_MINS, BALL_MAXS, w_shotorg, MOVE_WORLDONLY, NULL).
        // Abort if the muzzle is inside solid (ball can't leave).
        if (Api.Services is not null)
        {
            var tr = Api.Trace.Trace(shot.Origin, new Vector3(-16, -16, -16),
                new Vector3(16, 16, 16), shot.Origin, MoveFilter.WorldOnly, null);
            if (tr.StartSolid)
            {
                st.NbMeterStart = 0f; // QC: "Shot failed, hide the power meter"
                return;
            }
        }

        // ----- power multiplier (QC the triangle-wave over meter_period) -----
        float mul;
        if (t < 0f)
        {
            mul = 1f; // no meter / immediate
        }
        else
        {
            float mi = MeterMinpower();
            float ma = MathF.Max(mi, MeterMaxpower()); // avoid confusion when max < min
            float period = nb.MeterPeriod;
            // QC: mul = 2*(t % period)/period; if (mul > 1) mul = 2 - mul; mul = mi + (ma-mi)*mul.
            float phase = 2f * (t % period) / period;
            if (phase > 1f) phase = 2f - phase;
            mul = mi + (ma - mi) * phase;
        }

        // QC: DropBall(ball, w_shotorg, W_CalculateProjectileVelocity(actor, actor.velocity,
        // w_shotdir*speed*mul, /*forceAbsolute*/false)). Every projectile in this port routes velocity through
        // WeaponFiring.ProjectileVelocity, which models g_projectiles_newton_style as OFF (no owner-velocity
        // inheritance) — the same convention the secondary tackle uses below. Use it here too so the primary
        // launch is internally consistent with the secondary and the rest of the port (plain shotdir * speed*mul).
        Vector3 projVel = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, PrimarySpeed * mul);

        // QC: play shoot sound, then DropBall at w_shotorg with the computed velocity.
        SoundSystem.PlayOn(actor, Sounds.ByName("NB_SHOOT1"), SoundChannel.WeaponAuto,
            SoundLevels.VolBase, SoundLevels.AttenNorm);

        nb.LaunchBall(actor, shot.Origin, projVel);
        st.NbMeterStart = 0f; // ensure meter is cleared (DropBall also clears it via Nexball.DropBall)
    }

    // -----------------------------------------------------------------------
    //  W_Nexball_Attack2 — secondary: safe-pass or tackle projectile
    // -----------------------------------------------------------------------

    /// <summary>
    /// QC <c>W_Nexball_Attack2</c>: if a safe-pass lock is active, drop the ball in a lofted arc toward
    /// the locked teammate (W_Nexball_Think homing). Otherwise if <c>g_nexball_tackling</c>≠0, fire a
    /// fast tackle projectile that can steal the ball on hit.
    /// </summary>
    private void DoSecondary(Entity actor, WeaponSlot slot, WeaponSlotState st, Nexball nb, float now)
    {
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        // QC: if (actor.ballcarried.enemy) → safe-pass arc toward the locked target.
        if (nb.BallSafePassTarget is Entity passTarget)
        {
            SoundSystem.PlayOn(actor, Sounds.ByName("NB_SHOOT1"), SoundChannel.WeaponAuto,
                SoundLevels.VolBase, SoundLevels.AttenNorm);
            // QC: DropBall(_ball, w_shotorg, trigger_push_calculatevelocity(_ball.origin, _ball.enemy, 32, _ball)).
            // The velocity is solved from the ball's CURRENT (carried) origin toward the locked teammate, arcing
            // 32u over the apex — Jumppads.CalculateVelocity is the faithful port of trigger_push_calculatevelocity.
            // The homing W_Nexball_Think (Nexball.HomingThink) then corrects the course each frame.
            Vector3 ballOrg = nb.BallEntity?.Origin ?? shot.Origin;
            Vector3 arcVel = Jumppads.CalculateVelocity(ballOrg, passTarget, 32f, nb.BallEntity ?? actor);
            nb.LaunchBall(actor, shot.Origin, arcVel, homingTarget: passTarget);
            return;
        }

        // QC: if (!autocvar_g_nexball_tackling) return.
        if (!TacklingEnabled())
            return;

        // QC: fire a ballstealer projectile (speed 3000, lifetime 0.15 s, EF_BRIGHTFIELD|EF_LOWPRECISION,
        // electro-trail; W_Nexball_Touch on hit shoves the carrier and tries to steal).
        SoundSystem.PlayOn(actor, Sounds.ByName("NB_SHOOT2"), SoundChannel.WeaponAuto,
            SoundLevels.VolBase, SoundLevels.AttenNorm);

        if (Api.Services is null)
            return; // headless: no entity spawner

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "ballstealer";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(missile);
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(missile, shot.Origin);

        // QC W_SetupProjVelocity_Basic(missile, secondary_speed, 0): velocity = shotdir * speed.
        missile.Velocity = shot.Dir * SecondarySpeed;
        missile.Angles = QMath.VecToAngles(missile.Velocity);
        missile.Effects = EfBrightField | EfLowPrecision; // QC EF_BRIGHTFIELD | EF_LOWPRECISION
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE

        // QC: settouch(missile, W_Nexball_Touch); setthink(missile, SUB_Remove);
        // nextthink = time + secondary_lifetime.
        missile.Touch = (self, other) => TackleTouch(self, other, nb);
        missile.Think = self => Api.Entities.Remove(self);
        missile.NextThink = now + SecondaryLifetime;
    }

    // -----------------------------------------------------------------------
    //  W_Nexball_Touch — tackle projectile hit
    // -----------------------------------------------------------------------

    /// <summary>
    /// QC <c>W_Nexball_Touch</c> (sv_weapon.qc): the tackle projectile hit. Shoves the hit carrier; if the
    /// attacker isn't already carrying a ball (and isn't dead), steal it via <see cref="Nexball.StealBall"/>
    /// and play SND_NB_STEAL. Teamkill-complain debounce matches QC (CS(attacker).teamkill_complain, 5 s).
    /// </summary>
    private static void TackleTouch(Entity self, Entity other, Nexball nb)
    {
        // QC PROJECTILE_TOUCH: ignore dead/freed targets and world non-solids.
        if (other is null || other.IsFreed || other.TakeDamage == DamageMode.No)
        {
            if (other?.Solid != Solid.Bsp) // not the world → skip this touch
            {
                Api.Entities.Remove(self);
                return;
            }
        }

        Entity? attacker = self.Owner;
        if (attacker is null || attacker.IsFreed)
        {
            Api.Entities.Remove(self);
            return;
        }

        // QC: the hit entity must be carrying the nexball basketball.
        // QC: (attacker.team != toucher.team || g_nexball_basketball_teamsteal) && ball=toucher.ballcarried
        bool sameTeam = attacker is Player ap && other is Player op && ap.Team == op.Team;
        bool teamStealEnabled = Api.Services is null
            ? false
            : Api.Cvars.GetFloat("g_nexball_basketball_teamsteal") != 0f;

        if (!sameTeam || teamStealEnabled)
        {
            // Check the hit player is carrying the nexball ball (QC: ball = toucher.ballcarried).
            if (ReferenceEquals(other.GtCarried, nb.BallEntity) && nb.BallEntity is not null
                && other is Player victim && !victim.IsDead)
            {
                // QC: shove the carrier (secondary_force * damageforcescale).
                float force = ShoveForceCvar();
                Vector3 shoveDir = Vector3.Normalize(self.Velocity);
                victim.Velocity += shoveDir * force;
                victim.Flags &= ~EntFlags.OnGround; // UNSET_ONGROUND

                // QC: if (!attacker.ballcarried && !IS_DEAD(attacker)) → steal + play STEAL sound.
                if (attacker is Player atkPlayer && !atkPlayer.IsDead
                    && atkPlayer.GtCarried is null)
                {
                    SoundSystem.PlayOn(victim, Sounds.ByName("NB_STEAL"),
                        SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);

                    // QC: teamkill-complain debounce (5 s) for same-team steals.
                    if (sameTeam && teamStealEnabled
                        && Api.Services is not null
                        && Api.Clock.Time > atkPlayer.TeamKillComplainTime)
                    {
                        atkPlayer.TeamKillComplainTime = Api.Clock.Time + 5f;
                    }

                    nb.StealBall(atkPlayer);
                }
            }
        }

        Api.Entities.Remove(self);
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    /// <summary>Read the live Nexball gametype instance from the active gametype registry.</summary>
    private static Nexball? NexballGame()
        => GameTypes.ByName("nb") as Nexball;

    private static float MeterMinpower()
        => Api.Services is null ? DefaultMeterMinpower
            : (Api.Cvars.GetString("g_nexball_basketball_meter_minpower") is { Length: > 0 } s
                ? Api.Cvars.GetFloat("g_nexball_basketball_meter_minpower")
                : DefaultMeterMinpower);

    private static float MeterMaxpower()
        => Api.Services is null ? DefaultMeterMaxpower
            : (Api.Cvars.GetString("g_nexball_basketball_meter_maxpower") is { Length: > 0 }
                ? Api.Cvars.GetFloat("g_nexball_basketball_meter_maxpower")
                : DefaultMeterMaxpower);

    private static bool TacklingEnabled()
        => Api.Services is null ? true : Api.Cvars.GetFloat("g_nexball_tackling") != 0f;

    private static float ShoveForceCvar()
    {
        // QC: normalize(missile.velocity) * toucher.damageforcescale * secondary_force.
        // damageforcescale is not tracked per-entity in this port's headless player model;
        // use the secondary_force cvar directly (matches Base for the common case where damageforcescale==1).
        if (Api.Services is null) return DefaultSecondaryForce;
        string s = Api.Cvars.GetString("g_balance_nexball_secondary_force");
        return string.IsNullOrEmpty(s) ? DefaultSecondaryForce : Api.Cvars.GetFloat("g_balance_nexball_secondary_force");
    }
}
