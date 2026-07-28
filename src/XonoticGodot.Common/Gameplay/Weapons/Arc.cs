using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Arc — port of common/weapons/weapon/arc.{qh,qc}. A hitscan weapon whose primary is a continuous
/// lightning beam that sweeps to follow the player's aim (curving toward the crosshair, limited by a max
/// angle) and deals damage-per-second to whatever it touches, while heating up toward an overheat limit.
/// Its secondary depends on the <c>bolt</c> cvar: with bolt enabled (the default) it fires a short burst of
/// bouncing energy bolts that explode with radius damage; with bolt disabled, secondary is a higher-damage
/// "burst" variant of the beam.
///
/// Identity/attributes from arc.qh; balance from bal-wep-xonotic.cfg (g_balance_arc_*).
/// This port covers the per-frame beam DPS, the curving beam direction (beam_dir blended toward the aim,
/// limited by beam_maxangle/returnspeed), the heat / overheat-jam / cooldown model, exponential distance
/// falloff, teammate healing (health + armor), the burst-beam variant, the bouncing bolt secondary with
/// shoot-down, and the splash damage. The curving beam body is traced as the same quadratic-bezier multi-segment
/// sweep the Base server runs (control point pulled toward wantdir by beam_tightness, segment count from
/// beam_degreespersegment/distancepersegment, capped at ARC_MAX_SEGMENTS); only the CSQC beam *rendering* (the
/// Draw_ArcBeam networked entity) and the warpzone re-projection half of the antilag trace are left out.
/// </summary>
[Weapon]
public sealed class Arc : Weapon
{
    /// <summary>Beam (primary) balance — QC WEP_CVAR(WEP_ARC, beam_* / burst_* / heat/cooldown).</summary>
    public struct BeamBalance
    {
        public float Ammo;             // beam_ammo (cells per second)
        public float Animtime;         // beam_animtime
        public float Damage;           // beam_damage (per second vs players)
        public float DegreesPerSegment;// beam_degreespersegment
        public float DistancePerSegment;// beam_distancepersegment
        public float FalloffHalflifeDist;// beam_falloff_halflifedist
        public float FalloffMaxDist;   // beam_falloff_maxdist
        public float FalloffMinDist;   // beam_falloff_mindist
        public float Force;            // beam_force (per second)
        public float HealingAmax;      // beam_healing_amax
        public float HealingAps;       // beam_healing_aps
        public float HealingHmax;      // beam_healing_hmax
        public float HealingHps;       // beam_healing_hps
        public float Heat;             // beam_heat (heat/sec)
        public float MaxAngle;         // beam_maxangle
        public float NonPlayerDamage;  // beam_nonplayerdamage (per second vs non-players)
        public float Range;            // beam_range
        public float Refire;           // beam_refire
        public float ReturnSpeed;      // beam_returnspeed
        public float Tightness;        // beam_tightness

        public float BurstAmmo;        // burst_ammo
        public float BurstDamage;      // burst_damage (per second, secondary beam mode)
        public float BurstHealingAps;  // burst_healing_aps
        public float BurstHealingHps;  // burst_healing_hps
        public float BurstHeat;        // burst_heat

        public float Cooldown;         // cooldown
        public float CooldownRelease;  // cooldown_release
        public float OverheatMax;      // overheat_max
        public float OverheatMin;      // overheat_min
    }

    /// <summary>Bolt (secondary, when bolt=1) balance — QC WEP_CVAR(WEP_ARC, bolt_*).</summary>
    public struct BoltBalance
    {
        public int   BounceCount;      // bolt_bounce_count
        public float BounceExplode;    // bolt_bounce_explode
        public float BounceLifetime;   // bolt_bounce_lifetime
        public int   Count;            // bolt_count (bolts per burst)
        public float DamageForceScale; // bolt_damageforcescale
        public float Damage;           // bolt_damage
        public float EdgeDamage;       // bolt_edgedamage
        public float Force;            // bolt_force
        public float Health;           // bolt_health (shootable bolt hp)
        public float Lifetime;         // bolt_lifetime
        public float Radius;           // bolt_radius
        public float Refire;           // bolt_refire
        public float Refire2;          // bolt_refire2
        public float Speed;            // bolt_speed
        public float Spread;           // bolt_spread
        public float Ammo;             // bolt_ammo (cells per burst)
    }

    public BeamBalance Beam;
    public BoltBalance Bolt;

    /// <summary>g_balance_arc_bolt — when 1 (default), secondary fires bolts; when 0, secondary is burst beam.</summary>
    public bool BoltEnabled = true;


    public Arc()
    {
        NetName = "arc";
        BotPickupBaseValue = 8000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Arc";
        Impulse = 3;
        // WEP_FLAG_MUTATORBLOCKED | WEP_TYPE_HITSCAN
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.TypeHitscan;
        Color = new Vector3(0.463f, 0.612f, 0.886f);
        ViewModel = "h_arc.iqm";  // MDL_ARC_VIEW
        WorldModel = "v_arc.md3"; // MDL_ARC_WORLD
        ItemModel = "g_arc.md3";  // MDL_ARC_ITEM
    }

    // arc.qh: ATTRIB(Arc, w_crosshair, "gfx/crosshairhlac"); ATTRIB(Arc, w_crosshair_size, 0.7). The per-weapon
    // crosshair the CSQC draws when crosshair_per_weapon is on (read client-side by the crosshair panel, the same
    // seam Hook/Porto use). Closes the crosshair half of the identity.attributes presentation gap.
    public override string? Crosshair => "gfx/crosshairhlac";
    public override float CrosshairSize => 0.7f;

    public override void Configure()
    {
        Beam.Ammo = Bal("g_balance_arc_beam_ammo", 6f);
        Beam.Animtime = Bal("g_balance_arc_beam_animtime", 0.1f);
        Beam.Damage = Bal("g_balance_arc_beam_damage", 100f);
        Beam.DegreesPerSegment = Bal("g_balance_arc_beam_degreespersegment", 1f);
        Beam.DistancePerSegment = Bal("g_balance_arc_beam_distancepersegment", 0f);
        Beam.FalloffHalflifeDist = Bal("g_balance_arc_beam_falloff_halflifedist", 0f);
        Beam.FalloffMaxDist = Bal("g_balance_arc_beam_falloff_maxdist", 0f);
        Beam.FalloffMinDist = Bal("g_balance_arc_beam_falloff_mindist", 0f);
        Beam.Force = Bal("g_balance_arc_beam_force", 600f);
        Beam.HealingAmax = Bal("g_balance_arc_beam_healing_amax", 0f);
        Beam.HealingAps = Bal("g_balance_arc_beam_healing_aps", 50f);
        Beam.HealingHmax = Bal("g_balance_arc_beam_healing_hmax", 150f);
        Beam.HealingHps = Bal("g_balance_arc_beam_healing_hps", 50f);
        Beam.Heat = Bal("g_balance_arc_beam_heat", 0f);
        Beam.MaxAngle = Bal("g_balance_arc_beam_maxangle", 10f);
        Beam.NonPlayerDamage = Bal("g_balance_arc_beam_nonplayerdamage", 80f);
        Beam.Range = Bal("g_balance_arc_beam_range", 1500f);
        Beam.Refire = Bal("g_balance_arc_beam_refire", 0.25f);
        Beam.ReturnSpeed = Bal("g_balance_arc_beam_returnspeed", 8f);
        Beam.Tightness = Bal("g_balance_arc_beam_tightness", 0.6f);

        Beam.BurstAmmo = Bal("g_balance_arc_burst_ammo", 15f);
        Beam.BurstDamage = Bal("g_balance_arc_burst_damage", 250f);
        Beam.BurstHealingAps = Bal("g_balance_arc_burst_healing_aps", 100f);
        Beam.BurstHealingHps = Bal("g_balance_arc_burst_healing_hps", 100f);
        Beam.BurstHeat = Bal("g_balance_arc_burst_heat", 5f);

        Beam.Cooldown = Bal("g_balance_arc_cooldown", 2.5f);
        Beam.CooldownRelease = Bal("g_balance_arc_cooldown_release", 0f);
        Beam.OverheatMax = Bal("g_balance_arc_overheat_max", 5f);
        Beam.OverheatMin = Bal("g_balance_arc_overheat_min", 3f);

        Bolt.BounceCount = BalInt("g_balance_arc_bolt_bounce_count", 0);
        Bolt.BounceExplode = Bal("g_balance_arc_bolt_bounce_explode", 0f);
        Bolt.BounceLifetime = Bal("g_balance_arc_bolt_bounce_lifetime", 0f);
        Bolt.Count = BalInt("g_balance_arc_bolt_count", 1);
        Bolt.DamageForceScale = Bal("g_balance_arc_bolt_damageforcescale", 0f);
        Bolt.Damage = Bal("g_balance_arc_bolt_damage", 25f);
        Bolt.EdgeDamage = Bal("g_balance_arc_bolt_edgedamage", 12.5f);
        Bolt.Force = Bal("g_balance_arc_bolt_force", 120f);
        Bolt.Health = Bal("g_balance_arc_bolt_health", 15f);
        Bolt.Lifetime = Bal("g_balance_arc_bolt_lifetime", 5f);
        Bolt.Radius = Bal("g_balance_arc_bolt_radius", 65f);
        Bolt.Refire = Bal("g_balance_arc_bolt_refire", 0.16667f);
        Bolt.Refire2 = Bal("g_balance_arc_bolt_refire2", 0.16667f);
        Bolt.Speed = Bal("g_balance_arc_bolt_speed", 2300f);
        Bolt.Spread = Bal("g_balance_arc_bolt_spread", 0f);
        Bolt.Ammo = Bal("g_balance_arc_bolt_ammo", 1f);

        BoltEnabled = BalBool("g_balance_arc_bolt", true);
    }

    // METHOD(Arc, wr_think) — common/weapons/weapon/arc.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // The Arc beam is CONTINUOUS (fires/heats every tick while the button is held), so it does not use the
        // refire-gate (PrepareAttack); it reads the held button (st.ButtonAttack/2) directly, exactly as QC's
        // W_Arc_Beam keys off PHYS_INPUT_BUTTON_ATCK. The bolt secondary, by contrast, is a discrete shot and
        // IS refire-gated.
        //
        // The driver calls WrThink(Primary) every tick (for upkeep). We do the beam/cooldown decision in that
        // Primary call based on which fire button is down:
        if (fire == FireMode.Primary)
        {
            // QC wr_think (arc.qc:566) runs Arc_Smoke every tick BEFORE the fire decision: heat-fraction smoke
            // while heating, overheat-fire + the overheat loop while jammed and firing. Run it once per tick on
            // the (upkeep) Primary call, mirroring the per-tick wr_think head.
            ArcSmoke(actor, st);

            bool beamPrimary = st.ButtonAttack;                       // primary held -> normal beam
            // QC arc.qc:205-209 latches beam_bursting on the beam entity once a burst beam starts; the beam KEEPS
            // bursting after ATCK2 is released until the beam itself ends. So burst is `(ATCK2 && !bolt) || latch`,
            // not a fresh per-tick read — once latched it stays burst-typed (damage/heat/ammo) for this beam's life.
            bool beamBurst = (st.ButtonAttack2 && !BoltEnabled) || st.BeamBursting;
            if (beamPrimary || beamBurst)
            {
                if (beamBurst && !beamPrimary)
                    st.BeamBursting = true; // latch (arc.qc:207-208) — survives ATCK2 release until the beam ends
                BeamTick(actor, slot, st, burst: beamBurst && !beamPrimary);
            }
            else
            {
                // Neither beam button held: cool the barrel down (cooldown/sec toward 0) and end the beam. On the
                // firing→release EDGE (the beam was live last tick, st.ArcBeam set) stop its loop and play the
                // release cue — QC arc.qc:632 sound(actor, CH_WEAPON_A, SND_ARC_STOP). Gated on the edge so idle
                // ticks stay silent (and don't re-stop a loop that's already gone).
                if (st.ArcBeam is not null)
                {
                    Api.Sound.Stop(actor, SoundChannel.Weapon);
                    Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/arc_stop.wav");
                }
                BeamCooldown(st, burst: false);
                st.ArcBeam = null;
                st.BeamBursting = false; // beam ended: clear the burst latch (QC deletes the beam entity)
            }
        }
        else if (fire == FireMode.Secondary && BoltEnabled)
        {
            // QC arc.qc:599-627 wr_think (fire & 2): gate on weapon_prepareattack(..., true, 0) — the shared
            // ATTACK_FINISHED is advanced by 0; the after-burst cooldown (bolt_refire2) is set by the burst loop
            // itself once the last bolt fires. Then ammo-scale to_shoot, decrease ammo once, load
            // misc_bulletcounter = -to_shoot (counts UP to 0), and fire the FIRST bolt; W_Arc_Attack_Bolt
            // self-reschedules every bolt_refire for each remaining bolt.
            if (PrepareAttack(actor, slot, fire, attackTime: 0f))
            {
                bool unlimited = actor.UnlimitedAmmo || (actor.Items & (1 << 0)) != 0; // IT_UNLIMITED_AMMO
                // QC arc.qc:603-609: re-check ammo at the gate; bail (switch away) if dry and not unlimited.
                if (!CheckAmmoSecondary(actor) && !unlimited)
                {
                    st.State = WeaponFireState.Ready;
                    return;
                }

                // QC arc.qc:610-622: bolt_ammo is the cost of the WHOLE burst. Scale to_shoot down to the
                // affordable fraction (floor(bolt_count * min(1, ammo/bolt_ammo))) so a near-empty mag fires
                // fewer bolts instead of mag-dumping, and decrease ammo once by min(bolt_ammo, ammo).
                int toShoot = Bolt.Count;
                if (!unlimited)
                {
                    float ammoAvailable = actor.GetResource(AmmoType);
                    if (Bolt.Ammo > 0f)
                    {
                        float burstFraction = MathF.Min(1f, ammoAvailable / Bolt.Ammo);
                        toShoot = (int)MathF.Floor(toShoot * burstFraction);
                    }
                    float toUse = MathF.Min(Bolt.Ammo, ammoAvailable);
                    actor.TakeResource(AmmoType, toUse);
                }

                // Bursting counts up to 0 from a negative (QC arc.qc:625 misc_bulletcounter = -to_shoot).
                st.MiscBulletCounter = -toShoot;
                if (toShoot > 0)
                    AttackBolt(actor, slot, st);
                else
                    // to_shoot == 0 (custom balance where the burst can't afford even one bolt): wr_checkammo2
                    // would normally have blocked this; don't enter AttackBolt (counter 0 would never re-reach 0
                    // → infinite self-reschedule). Park READY after the fire anim.
                    WeaponFireDriver.ScheduleThink(st, Bolt.Refire * WeaponRateFactor(actor), static (pl, sl) =>
                    {
                        WeaponSlotState s2 = pl.WeaponState(sl);
                        if (s2.State == WeaponFireState.InUse)
                            s2.State = WeaponFireState.Ready;
                    });
            }
        }
    }

    // Cool the barrel when the beam isn't active (W_Arc_Beam_Think's release branch, arc.qc:223-258). QC picks a
    // per-state cooldown_speed: full `cooldown` while still hot (heat > overheat_min), a heat-proportional
    // `heat/beam_refire` once below overheat_min (so the last sliver bleeds off in ~beam_refire), or 0 while the
    // burst beam is held; that speed is then drained from the heat each frame.
    private void BeamCooldown(WeaponSlotState st, bool burst)
    {
        if (Beam.Cooldown <= 0f)
        {
            st.BeamInitialized = false;
            return;
        }
        float cooldownSpeed = CooldownSpeed(st, burst);
        // QC arc.qc:240-245: when the beam ends with any cooldown_speed, latch arc_cooldown (the HUD heat-% scale)
        // and — if cooldown_release is set OR we ended via overheat — latch arc_overheat to the time the heat
        // takes to bleed off at that speed. cooldown_release lets the heat ring/refire-gate linger even on a clean
        // release (dormant at stock cooldown_release 0; the overheat case is handled at the BeamTick jam site).
        if (cooldownSpeed != 0f)
        {
            if (Beam.CooldownRelease != 0f)
                st.ArcOverheat = Api.Clock.Time + st.BeamHeat / cooldownSpeed;
            st.ArcCooldown = cooldownSpeed;
        }
        if (cooldownSpeed > 0f && st.BeamHeat > 0f)
            st.BeamHeat = MathF.Max(0f, st.BeamHeat - cooldownSpeed * Api.Clock.FrameTime);
        st.BeamInitialized = false;
    }

    // QC arc.qc:225-231 cooldown_speed selection.
    private float CooldownSpeed(WeaponSlotState st, bool burst)
    {
        if (st.BeamHeat > Beam.OverheatMin && Beam.Cooldown > 0f)
            return Beam.Cooldown;
        if (!burst)
            return Beam.Refire > 0f ? st.BeamHeat / Beam.Refire : 0f;
        return 0f;
    }

    // Arc_GetHeat_Percent (arc.qc:55-68): the barrel heat as a [0,1] fraction — beam_heat/overheat_max while the
    // beam is live, else (arc_overheat-time)/overheat_max*arc_cooldown while cooling from a jam. Used both for the
    // HUD ring (NetGame) and the smoke probability below.
    private float HeatPercent(WeaponSlotState st)
    {
        if (Beam.OverheatMax <= 0f || Beam.OverheatMin <= 0f)
            return 0f;
        if (st.ArcBeam is not null)
            return st.BeamHeat / Beam.OverheatMax;
        if (st.ArcOverheat > Api.Clock.Time)
            return (st.ArcOverheat - Api.Clock.Time) / Beam.OverheatMax * st.ArcCooldown;
        return 0f;
    }

    // Arc_Smoke (arc.qc:513-554) — per-tick muzzle smoke/fire + the overheat loop sound. While the barrel is
    // jammed (arc_overheat in the future) it emits EFFECT_ARC_SMOKE at random (prob = heat %); if also firing it
    // emits EFFECT_ARC_OVERHEAT_FIRE and starts the SND_ARC_LOOP_OVERHEAT loop. While the beam is live and heat
    // is past overheat_min it emits warning smoke (prob = heat fraction past overheat_min). The loop is stopped
    // once the jam clears, the player stops firing, or they switch away. Called every tick at the head of WrThink.
    private void ArcSmoke(Entity actor, WeaponSlotState st)
    {
        // QC computes a rough muzzle origin from v_angle + the weapon movedir tag; headless we approximate from
        // the eye + view forward (as Tuba's smoke does), plus the one-frame velocity lead QC adds.
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        Vector3 shotOrg = actor.Origin + actor.ViewOfs + forward;
        Vector3 smokeOrigin = shotOrg + actor.Velocity * Api.Clock.FrameTime;

        bool firing = st.ButtonAttack || st.ButtonAttack2;

        if (st.ArcOverheat > Api.Clock.Time)
        {
            if (Prandom.Float() < HeatPercent(st))
                EffectEmitter.Emit("ARC_SMOKE", smokeOrigin, Vector3.Zero, 1);
            if (firing)
            {
                EffectEmitter.Emit("ARC_OVERHEAT_FIRE", smokeOrigin, forward, 1);
                if (!st.ArcSmokeSound)
                {
                    st.ArcSmokeSound = true;
                    Api.Sound.Play(actor, SoundChannel.Item, "weapons/arc_loop_overheat.wav", loop: true);
                }
            }
        }
        else if (st.ArcBeam is not null && Beam.OverheatMax > 0f && st.BeamHeat > Beam.OverheatMin)
        {
            float fracToMax = (st.BeamHeat - Beam.OverheatMin) / (Beam.OverheatMax - Beam.OverheatMin);
            if (Prandom.Float() < fracToMax)
                EffectEmitter.Emit("ARC_SMOKE", smokeOrigin, Vector3.Zero, 1);
        }

        // Stop the overheat loop once the jam clears / firing stops / we switch away (arc.qc:547-553). The
        // switch-away half is covered by WrSetup clearing ArcSmokeSound, but stop here too for the live cases.
        bool stopSmokeSound = st.ArcOverheat <= Api.Clock.Time || !firing;
        if (st.ArcSmokeSound && stopSmokeSound)
        {
            st.ArcSmokeSound = false;
            Api.Sound.Stop(actor, SoundChannel.Item);
        }
    }

    // W_Arc_Beam_Think (full DPS core) — one frame of the beam: accumulate heat (jamming on overheat), curve
    // the beam direction toward the aim (limited by beam_maxangle/returnspeed), trace, and apply
    // distance-falloff damage to enemies / healing to teammates. arc.qc
    private void BeamTick(Entity actor, WeaponSlot slot, WeaponSlotState st, bool burst)
    {
        // Overheat jam: while overheated (heat at max), the beam can't fire and just cools down.
        if (Beam.OverheatMax > 0f && st.BeamHeat >= Beam.OverheatMax)
        {
            if (st.ArcOverheat == 0f)
            {
                // QC arc.qc:240-244: while overheated, cooldown_speed == `cooldown` (heat is at max, above
                // overheat_min) and the jam lasts `time + heat / cooldown_speed` — i.e. as long as it takes to bleed
                // the accumulated heat back off at the cooldown rate (~overheat_max/cooldown), NOT a fixed
                // overheat_max seconds. Falls back to overheat_max only if cooldown is disabled (no bleed rate).
                // arc_cooldown is latched too (QC own.arc_cooldown = cooldown_speed) — it scales the HUD heat ring.
                float cooldownSpeed = CooldownSpeed(st, burst);
                st.ArcOverheat = Api.Clock.Time + (cooldownSpeed > 0f ? st.BeamHeat / cooldownSpeed : Beam.OverheatMax);
                if (cooldownSpeed != 0f) st.ArcCooldown = cooldownSpeed;
                // QC arc.qc:236 — burst of overheat particles at the muzzle (beam_start / beam_wantdir) when the
                // barrel jams. Headless, approximate the muzzle origin from the eye + view forward (as Arc_Smoke does).
                QMath.AngleVectors(actor.Angles, out Vector3 jamFwd, out _, out _);
                EffectEmitter.Emit("ARC_OVERHEAT", actor.Origin + actor.ViewOfs, jamFwd, 1);
                Api.Sound.Stop(actor, SoundChannel.Weapon);          // end the beam loop before the stop cue
                Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/arc_stop.wav"); // QC arc.qc:237 SND_ARC_STOP
            }
            BeamCooldown(st, burst);
            st.ArcBeam = null;
            st.BeamBursting = false; // overheat ends the beam (QC deletes arc_beam): clear the burst latch
            return;
        }
        st.ArcOverheat = 0f;

        // coefficient = frametime, clamped by remaining ammo (rootammo per second).
        float coefficient = Api.Clock.FrameTime;
        float rootAmmo = burst ? Beam.BurstAmmo : Beam.Ammo;
        if (rootAmmo > 0f)
        {
            float cur = actor.GetResource(AmmoType);
            coefficient = MathF.Min(coefficient, cur / rootAmmo);
            actor.SetResource(AmmoType, MathF.Max(0f, cur - rootAmmo * Api.Clock.FrameTime));
        }

        // Heat builds while firing.
        float heatSpeed = burst ? Beam.BurstHeat : Beam.Heat;
        st.BeamHeat = MathF.Min(Beam.OverheatMax > 0f ? Beam.OverheatMax : float.MaxValue,
            st.BeamHeat + heatSpeed * Api.Clock.FrameTime);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, Beam.Range);
        Vector3 wantDir = shot.Dir;

        // Curving beam: beam_dir lags wantDir, blending toward it but turning no faster than beam_maxangle
        // per the returnspeed rate (so whipping the aim drags the beam around smoothly).
        if (!st.BeamInitialized)
        {
            st.BeamDir = wantDir;
            st.BeamInitialized = true;
        }
        // QC arc.qc:318: segments default to 1; the curve only segments when beam_dir is actually lagging wantdir.
        float segments = 1f;
        if (st.BeamDir != wantDir)
        {
            float angle = (wantDir - st.BeamDir).Length() * QMath.Rad2Deg;
            if (angle < 0.01f)
            {
                st.BeamDir = wantDir;
            }
            else
            {
                float maxBlend = 1f;
                if (angle > Beam.MaxAngle) maxBlend = Beam.MaxAngle / angle;
                float blend = QMath.Clamp(1f - Beam.ReturnSpeed * Api.Clock.FrameTime, 0f, maxBlend);
                st.BeamDir = QMath.Normalize(wantDir * (1f - blend) + st.BeamDir * blend);

                // QC arc.qc:337-349: how many bezier segments to break the curved body into. Capped at
                // ARC_MAX_SEGMENTS (20); beam_distancepersegment lowers the cap by path length, beam_degreespersegment
                // sets the count from the turn angle (clamped to beam_maxangle). At stock degrees/distance defaults
                // (1 / 0) a curving beam segments by its angle, smoothing the swept body around a corner.
                const float ArcMaxSegments = 20f;
                float maxAllowedSegments = ArcMaxSegments;
                if (Beam.DistancePerSegment != 0f)
                {
                    // QC arc.qc:341 uses vlen(w_shotdir) (a unit vector ~= 1) — reproduce verbatim.
                    maxAllowedSegments = 1f + wantDir.Length() / Beam.DistancePerSegment;
                    maxAllowedSegments = QMath.Clamp(maxAllowedSegments, 1f, ArcMaxSegments);
                }
                if (Beam.DegreesPerSegment != 0f)
                {
                    segments = MathF.Min(angle, Beam.MaxAngle) / Beam.DegreesPerSegment;
                    segments = QMath.Clamp(segments, 1f, maxAllowedSegments);
                }
            }
        }

        // QC arc.qc:352-354: a quadratic bezier from w_shotorg through a control point pulled toward wantdir by
        // beam_tightness, ending at w_shotorg + beam_dir*range. With segments==1 this collapses to the endpoint
        // (a straight trace along beam_dir); a curving beam traces the bowed body segment-by-segment so it can clip
        // geometry near a swept corner the straight endpoint would miss.
        Vector3 beamEndPos = shot.Origin + st.BeamDir * Beam.Range;
        float controlPointDist = Beam.Range * QMath.Clamp(1f - Beam.Tightness, 0.001f, 1f);
        Vector3 controlPoint = shot.Origin + wantDir * controlPointDist;

        // QC arc.qc:360-460: trace the bezier segment-by-segment from w_shotorg; the first segment that hits
        // terminates the beam. last_origin_prev feeds the damage force direction (normalize(new_origin - prev)).
        Vector3 lastOrigin = shot.Origin;
        Vector3 lastOriginPrev = Vector3.Zero;
        Vector3 end = beamEndPos; // straight-beam fallback for the force dir + trail endpoint
        TraceResult tr = default;
        bool hitSomething = false;
        // QC arc.qc:375 traces through WarpZone_traceline_antilag at ANTILAG_LATENCY: rewind other players to the
        // shooter's view-time so a high-ping player's beam connects on a strafing target. Bracket the whole swept
        // trace (no-op on a client / bot-only server / test where no lag-comp provider is installed).
        LagComp.Begin(actor);
        try
        {
            // QC arc.qc:360 loops `for (i = 1; i <= segments; ++i)` with a FLOAT segments and t = i/segments — so
            // the iteration count is floor(segments) and, when segments is fractional, the last t is < 1 (the trace
            // intentionally stops just shy of the full endpoint). Reproduce both: floor for the count, the float
            // divisor for t.
            int segCount = (int)MathF.Floor(segments);
            if (segCount < 1) segCount = 1;
            for (int i = 1; i <= segCount; ++i)
            {
                Vector3 newOrigin = BezierQuadratic(shot.Origin, controlPoint, beamEndPos, i / segments);
                tr = Api.Trace.Trace(lastOrigin, Vector3.Zero, Vector3.Zero, newOrigin, MoveFilter.Normal, actor);
                lastOriginPrev = lastOrigin;
                lastOrigin = tr.EndPos;
                end = newOrigin; // the swept direction for this segment (matches QC's per-segment new_origin)
                if (tr.Fraction < 1f) { hitSomething = true; break; }
            }
        }
        finally { LagComp.End(); }

        // QC's per-hit force direction is normalize(new_origin - last_origin_prev) (the swept segment dir), not
        // the whole-beam dir; fall back to beam_dir when there is no prior segment (single straight segment).
        Vector3 segDir = hitSomething && (end - lastOriginPrev).LengthSquared() > 1e-6f
            ? QMath.Normalize(end - lastOriginPrev)
            : st.BeamDir;

        Entity? hit = hitSomething ? tr.Ent : null;
        // QC arc.qc:420 sets beam_type = ARC_BT_HEAL when the beam connects to a teammate with healing
        // configured; the CSQC trail then uses the heal-beam variant (ARC_BEAM_HEAL). Track it so the emitted
        // trail matches the gameplay outcome instead of always emitting the damage trail.
        bool healed = false;
        if (hit is not null && hit.TakeDamage != DamageMode.No)
        {
            bool isPlayer = (hit.Flags & EntFlags.Client) != 0
                || hit.ClassName == "body" || (hit.Flags & EntFlags.Monster) != 0;

            // Teammates get healed (health + armor) instead of damaged.
            if (!ReferenceEquals(hit, actor) && actor.Team != 0f && hit.Team == actor.Team)
            {
                float hps = burst ? Beam.BurstHealingHps : Beam.HealingHps;
                float aps = burst ? Beam.BurstHealingAps : Beam.HealingAps;
                if (hps > 0f || aps > 0f)
                    healed = true; // QC: new_beam_type = ARC_BT_HEAL when (roothealth || rootarmor)
                if (hps > 0f)
                {
                    // QC Heal(trace_ent, own, hps*coef, hplimit) — GiveResourceWithLimit(Health) which CLAMPS the
                    // post-give total to the limit (players: healing_hmax; non-players: RES_LIMIT_NONE = uncapped).
                    // Previously this DROPPED the heal entirely once it would push past hmax instead of topping up
                    // to it — a real divergence right at the cap.
                    float hpLimit = isPlayer ? Beam.HealingHmax : Resources.LimitNone;
                    // QC Heal() dispatches to trace_ent.event_heal when set: a non-player OBJECTIVE (Onslaught
                    // generator / control-point icon) tracks its HP in GtObjHealth, NOT the Health resource, so
                    // route it through Combat.Heal (→ event_heal) instead of writing the wrong field. Players +
                    // ordinary creatures still use the direct resource give (their event_heal isn't modeled here).
                    if (!isPlayer && hit.GtEventHeal is not null)
                        XonoticGodot.Common.Gameplay.Damage.Combat.Heal(hit, actor, hps * coefficient, hpLimit);
                    else
                        hit.GiveResourceWithLimit(ResourceType.Health, hps * coefficient, hpLimit);
                }
                if (isPlayer && aps > 0f && hit.GetResource(ResourceType.Armor) <= Beam.HealingAmax)
                {
                    hit.GiveResourceWithLimit(ResourceType.Armor, aps * coefficient, Beam.HealingAmax);
                    // QC arc.qc:417 — refresh the armor-rot pause so the just-given armor doesn't immediately
                    // start rotting back off.
                    hit.PauseRotArmorFinished = MathF.Max(hit.PauseRotArmorFinished,
                        Api.Clock.Time + Bal("g_balance_pause_armor_rot", 1f));
                }
            }
            else
            {
                float perSec = isPlayer ? (burst ? Beam.BurstDamage : Beam.Damage) : Beam.NonPlayerDamage;
                // Exponential distance falloff (beam_falloff_*).
                float falloff = (Beam.FalloffHalflifeDist > 0f)
                    ? WeaponFiring.ExponentialFalloff(Beam.FalloffMinDist, Beam.FalloffMaxDist,
                        Beam.FalloffHalflifeDist, (tr.EndPos - shot.Origin).Length())
                    : 1f;
                float damage = perSec * coefficient * falloff;
                // QC arc.qc:452-455: force direction is the swept segment direction (new_dir), not the whole-beam dir.
                Vector3 force = segDir * (Beam.Force * coefficient * falloff);
                WeaponFiring.ApplyDamage(hit, actor, damage, RegistryId, force: force, hitLoc: tr.EndPos);
            }
        }

        // Sweep the beam trail origin->endpos. QC nets beam_type (MISS/WALL/HIT use the normal beam; HEAL uses
        // the heal-beam variant) — emit ARC_BEAM_HEAL on the heal branch, ARC_BEAM otherwise.
        EffectEmitter.Emit(healed ? "ARC_BEAM_HEAL" : "ARC_BEAM", shot.Origin, tr.EndPos, 0);
        // QC Draw_ArcBeam draws the cylindric beam LINE (Draw_CylindricLine) in ADDITION to the trailparticles
        // above. Emit the drawn line origin->hit-point (count 0, end in velocity — the Beam route convention).
        // Tint: the damage beam uses the Arc's m_color blue (arc.qh '0.463 0.612 0.886'); the heal beam uses the
        // arc_beam_heal green (effectinfo 0x20FF20). The per-beam_type thickness is now broadcast by selecting the
        // wider burst-line variant (Draw_CylindricLine thickness 8 normal / 14 burst) when this beam is the burst
        // type, so all clients read the Base thickness. The spinning muzzle-flash entity and the hit/muzzle dynamic
        // lights remain CSQC render plumbing not modelled here (residual weapon-arc.beam.visual render gap).
        Vector3 beamColor = healed ? new Vector3(0.125f, 1f, 0.125f) : Color;
        EffectEmitter.Emit(Effects.ByName(healed ? "ARC_BEAM_LINE_HEAL" : (burst ? "ARC_BEAM_LINE_BURST" : "ARC_BEAM_LINE")),
            shot.Origin, tr.EndPos, 0, beamColor, beamColor);

        st.ArcBeam = actor; // mark the beam as live this frame
        // Looping beam sound on (actor, CH_WEAPON) — DP loopsound(beam, CH_SHOTS_SINGLE, SND_ARC_LOOP). Emitted
        // every think while firing, but loop:true makes the client KEEP the one existing loop (no stacking) and
        // follow the player; Stop() on release/overheat ends it (below / in WrThink).
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/arc_loop.wav", loop: true);
    }

    // W_Arc_Attack_Bolt (arc.qc:142-192) — fire ONE bouncing energy bolt that explodes with radius damage, then
    // self-reschedule every bolt_refire while misc_bulletcounter counts up to 0; the bolt that reaches 0 (burst
    // done) parks the after-burst cooldown (bolt_refire2) and becomes READY after bolt_refire, otherwise it
    // re-schedules the next bolt after bolt_refire. The caller (wr_think fire&2) sets misc_bulletcounter = -to_shoot
    // and fires the first bolt; each subsequent bolt re-samples the aim so the burst tracks the player mid-spray.
    private void AttackBolt(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 2f); // QC arc.qc:144 W_SetupShot recoil 2

        // QC arc.qc:146 W_MuzzleFlash(thiswep, ...) — the Arc reuses the electro muzzle flash
        // (EFFECT_ARC_MUZZLEFLASH -> electro_muzzleflash). Emitted per bolt, matching Base.
        EffectEmitter.Emit("ARC_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "missile";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.BounceMissile;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        missile.DamageForceScale = Bolt.DamageForceScale;
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(missile, shot.Origin);

        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = Bolt.Health;

        // W_SetupProjVelocity_PRE(bolt_): velocity = w_shotdir * speed (with bolt_spread, normally 0).
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Bolt.Speed, 0f, 0f, Bolt.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC arc.qc:150-152 — flag the energy bolt as a dodgeable hazard (rating = bolt damage). (The sweeping
        // beam is traced per-frame with no persistent entity in this port, so only the bolt is flaggable.)
        missile.BotDodge = true;
        missile.BotDodgeRating = Bolt.Damage;

        missile.Count = 0; // QC .cnt = bounce counter
        missile.Touch = (self, other) => BoltTouch(self, other);
        missile.Think = self => ExplodeBolt(self); // adaptor_think2use_hittype_splash at lifetime
        missile.ProjectileDamage = (self, _) => ExplodeBolt(self); // W_Arc_Bolt_Damage -> W_PrepareExplosionByDamage
        missile.NextThink = Api.Clock.Time + Bolt.Lifetime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — fired per bolt (arc.qc W_Arc_Attack_Bolt).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        // [W1-projectile-net] Route incoming damage through the shoot-down shim (QC W_Arc_Bolt_Damage):
        // it runs the g_projectiles_damage gate, subtracts the damage from the bolt's RES_HEALTH, and only
        // fires ProjectileDamage (ExplodeBolt) once HP <= 0 — so a partial-damage graze leaves the bolt
        // alive instead of detonating it on any hit. The Arc bolt is not combo-able (exception -1, default).
        Projectiles.MakeShootable(missile);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire2.wav"); // QC SND_ARC_BOLT_FIRE (per bolt)

        // QC arc.qc:184-191: the burst counter counts UP to 0. When it reaches 0 the burst is done — enforce the
        // after-burst cooldown (bolt_refire2) and return to READY after bolt_refire; otherwise re-schedule the
        // next bolt after bolt_refire (one bolt per bolt_refire until the burst is spent).
        float rate = WeaponRateFactor(actor);
        ++st.MiscBulletCounter;
        if (st.MiscBulletCounter == 0)
        {
            st.AttackFinished = Api.Clock.Time + Bolt.Refire2 * rate;
            WeaponFireDriver.ScheduleThink(st, Bolt.Refire * rate, static (pl, sl) =>
            {
                WeaponSlotState s2 = pl.WeaponState(sl);
                if (s2.State == WeaponFireState.InUse)
                    s2.State = WeaponFireState.Ready;
            });
        }
        else
        {
            WeaponFireDriver.ScheduleThink(st, Bolt.Refire * rate, (pl, sl) =>
                AttackBolt(pl, sl, pl.WeaponState(sl)));
        }
    }

    // W_Arc_Bolt_Touch — explode on a damageable target or when bounces run out; otherwise bounce. arc.qc
    private void BoltTouch(Entity self, Entity other)
    {
        // QC arc.qc:116 keys solely on toucher.takedamage == DAMAGE_AIM (anything that aim-takes damage —
        // players, bodies, monsters, shootable projectiles), not on the Client flag specifically.
        bool hitAimTarget = other.TakeDamage == DamageMode.Aim;
        if (self.Count >= Bolt.BounceCount || Bolt.BounceCount == 0 || hitAimTarget)
        {
            ExplodeBolt(self);
            return;
        }
        // Survived a bounce (engine MOVETYPE_BOUNCEMISSILE reflects velocity): count it and re-aim.
        ++self.Count;
        self.Angles = QMath.VecToAngles(self.Velocity);
        if (Bolt.BounceExplode != 0f)
        {
            WeaponSplash.RadiusDamage(self, self.Origin, Bolt.Damage, Bolt.EdgeDamage, Bolt.Radius,
                self.Owner, RegistryId, Bolt.Force);
        }
        if (self.Count == 1 && Bolt.BounceLifetime != 0f)
            self.NextThink = Api.Clock.Time + Bolt.BounceLifetime;
    }

    // W_Arc_Bolt_Explode — radius damage + knockback, then remove. arc.qc
    private void ExplodeBolt(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        WeaponSplash.RadiusDamage(self, self.Origin, Bolt.Damage, Bolt.EdgeDamage, Bolt.Radius,
            self.Owner, RegistryId, Bolt.Force);
        WeaponSplash.ImpactSound(self, "weapons/electro_impact.wav"); // QC SND_ARC_BOLT_IMPACT (wr_impacteffect)
        // arc.qc: pointparticles(EFFECT_ELECTRO_IMPACT, org2, w_backoff * 1000, 1) — spray back along the impact
        // normal; the reversed bolt flight direction is DP's w_backoff fallback (-force_dir).
        Vector3 backoff = self.Velocity.LengthSquared() > 1e-6f ? -QMath.Normalize(self.Velocity) : Vector3.Zero;
        EffectEmitter.Emit("ELECTRO_IMPACT", self.Origin, backoff * 1000f);
        Api.Entities.Remove(self);
    }

    // METHOD(Arc, wr_resetplayer / wr_playerdeath) — arc.qc:701-716: clear the overheat jam + cooldown latch (and
    // the ATCK-prev edge) when the player respawns / dies, so a fresh life never starts mid-jam. The port has no
    // respawn-wide weapon-reset hook, so — like Vortex/Rifle/OkNex's per-slot seed (see Rifle.WrSetup) — this runs
    // in wr_setup (switch-in, live-dispatched by WeaponFireDriver on raise/spawn). Heat is per-slot here, so a
    // switch away+back also re-clears (benign: the beam is gone while the weapon is holstered anyway). Base also
    // migrates arc_overheat/arc_cooldown across a dropped/picked-up Arc (wr_drop/wr_pickup); the port has no
    // weapon-throw/heat-carry path yet (WeaponThrowing.cs notes no weapon implements wr_drop), so that half stays
    // unported and is tracked under weapon-arc.heat_persistence.
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        st.ArcOverheat = 0f;
        st.ArcCooldown = 0f;
        st.BeamHeat = 0f;
        st.BeamBursting = false;
        st.ArcBeam = null;
        st.BeamInitialized = false;
        // Arc_Smoke (arc.qc:549) stops the overheat loop when the weapon is switched away; WrSetup runs on the
        // switch-in/raise, so clear the latch here so a fresh raise never inherits a stale loop flag.
        if (st.ArcSmokeSound)
        {
            st.ArcSmokeSound = false;
            Api.Sound.Stop(actor, SoundChannel.Item);
        }
    }

    // QC the Arc has no _primary_/_secondary_ refire cvars: the primary is the continuous beam (beam_refire,
    // only used if the beam ever went through the gate — it does not), the secondary bolt uses bolt_refire.
    // No separate bolt animtime cvar, so the refire doubles as the fire-anim length.
    public override float RefireFor(FireMode fire)
        => fire == FireMode.Secondary ? Bolt.Refire : Beam.Refire;
    public override float AnimtimeFor(FireMode fire)
        => fire == FireMode.Secondary ? Bolt.Refire : Beam.Animtime;

    // METHOD(Arc, wr_checkammo1) — arc.qc:664-667. The continuous beam only needs ANY cells to start
    // (`!beam_ammo || cells > 0`), NOT a full per-second `beam_ammo` worth — the per-tick drain in BeamTick
    // already scales by whatever fraction of a tick's ammo remains. Gating on `cells >= beam_ammo (6)` was a
    // real bug: a player with 1-5 cells should fire the beam (draining what's left) but was auto-switched away.
    public bool CheckAmmoPrimary(Entity actor)
        => Beam.Ammo == 0f || actor.GetResource(AmmoType) > 0f;

    // METHOD(Arc, wr_checkammo2) — arc.qc:668-679. Bolt secondary needs `cells >= bolt_ammo (1)`; the burst-beam
    // secondary (bolt disabled) needs only `overheat_max > 0 && (!burst_ammo || cells > 0)` — same "any cells"
    // rule as the primary beam, not a full `burst_ammo (15)` worth.
    public bool CheckAmmoSecondary(Entity actor)
    {
        float cells = actor.GetResource(AmmoType);
        if (BoltEnabled)
            return cells >= Bolt.Ammo;
        return Beam.OverheatMax > 0f && (Beam.BurstAmmo == 0f || cells > 0f);
    }

    // METHOD(Arc, wr_aim) — arc.qc:556-562. The Arc bot always presses the PRIMARY beam (never the secondary):
    // QC's wr_aim sets PHYS_INPUT_BUTTON_ATCK from bot_aim and never touches ATCK2. With the stock
    // beam_botaimspeed 0 it uses the hitscan fallback bot_aim(1000000, 0, 0.001) — "always in range, no spread
    // tolerance, instant lead" — the same near-zero-lead aim the generic BotBrain already produces for any
    // WEP_TYPE_HITSCAN weapon (BotBrain.CurrentShotSpeed returns 0 for hitscan -> aim straight; BotAimAccurate
    // null -> hitscan is accurate). So the generic path reproduces the fallback exactly; this override only
    // makes the "never secondary" half explicit (matching the Rifle/Vaporizer pattern). The non-stock
    // beam_botaimspeed>0 lead-tuning branch is dormant at stock and not separately modeled (it would need a
    // per-weapon bot-aim-speed seam the brain doesn't expose for a hitscan weapon).
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx) => false;

    // lib/math.qh bezier_quadratic_getpoint(a, b, c, t) = (c - 2b + a)*t^2 + (b - a)*2t + a. Used to walk the
    // curving beam body in BeamTick (arc.qc:368).
    private static Vector3 BezierQuadratic(Vector3 a, Vector3 b, Vector3 c, float t)
        => (c - 2f * b + a) * (t * t) + (b - a) * (2f * t) + a;
}
