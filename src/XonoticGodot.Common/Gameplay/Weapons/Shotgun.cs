using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Shotgun — port of common/weapons/weapon/shotgun.{qh,qc}. A hitscan weapon: primary fire sprays a
/// fan of pellets (each an independent <see cref="WeaponFiring.FireBullet"/> trace) that fall off with
/// distance; secondary is a short-range melee "slap" that sweeps a few traces in front of the actor.
///
/// Identity/attributes from shotgun.qh; balance from bal-wep-xonotic.cfg (g_balance_shotgun_*).
/// This port covers the full pellet fan (spread, solid penetration, distance falloff, knockback force),
/// the melee swing (swing-arc damage scaling + multi-hit dedupe, HITTYPE_SECONDARY slap obituary, per-trace
/// woosh fx), shell-casing eject, the impact ricochet ping, and the out-of-ammo auto-melee fallback.
/// The melee swing trace is antilag-bracketed (LagComp around the sweep, QC WarpZone_traceline_antilag,
/// shotgun.qc:124). The melee now runs as Base does: a scheduled meleetemp think after the melee_delay (0.25s)
/// wind-up that spreads its melee_traces across melee_time (0.15s), aborting on melee_no_doubleslap if the owner
/// dies mid-swing. The alt triple-shot (secondary==2) fires three staged blasts: initial blast in WrThink,
/// then Frame1 and Frame2 each scheduled at alt_animtime intervals via WeaponFireDriver.ScheduleThink, with
/// per-blast ammo checks + W_SwitchWeapon_Force (faithful to W_Shotgun_Attack3_Frame1/2, shotgun.qc:207-263).
/// </summary>
[Weapon]
public sealed class Shotgun : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_SHOTGUN, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;              // g_balance_shotgun_primary_ammo (shells per shot)
        public float Animtime;          // g_balance_shotgun_primary_animtime
        public float Bullets;           // g_balance_shotgun_primary_bullets (pellet count)
        public float Damage;            // g_balance_shotgun_primary_damage (per pellet)
        public float Force;             // g_balance_shotgun_primary_force
        public float Refire;            // g_balance_shotgun_primary_refire
        public float SolidPenetration;  // g_balance_shotgun_primary_solidpenetration
        public float Spread;            // g_balance_shotgun_primary_spread
    }

    /// <summary>Secondary-fire (melee) balance block — QC WEP_CVAR_SEC(WEP_SHOTGUN, *).</summary>
    public struct SecondaryBalance
    {
        public int   Secondary;            // g_balance_shotgun_secondary (0=off, 1=melee, 2=triple-shot)
        public float Animtime;             // g_balance_shotgun_secondary_animtime
        public float AltAnimtime;          // g_balance_shotgun_secondary_alt_animtime (triple-shot)
        public float Damage;               // g_balance_shotgun_secondary_damage (vs players)
        public float Force;                // g_balance_shotgun_secondary_force
        public float Refire;               // g_balance_shotgun_secondary_refire
        public float AltRefire;            // g_balance_shotgun_secondary_alt_refire (triple-shot)
        public bool  MeleeBlockedByFiring; // g_balance_shotgun_secondary_melee_blockedbyfiring (default 0)
        public float MeleeDelay;           // g_balance_shotgun_secondary_melee_delay
        public float MeleeMultihit;        // g_balance_shotgun_secondary_melee_multihit
        public bool  MeleeNoDoubleslap;    // g_balance_shotgun_secondary_melee_no_doubleslap (default 1)
        public float MeleeNonplayerDamage; // g_balance_shotgun_secondary_melee_nonplayerdamage
        public float MeleeRange;           // g_balance_shotgun_secondary_melee_range
        public float MeleeSwingSide;       // g_balance_shotgun_secondary_melee_swing_side
        public float MeleeSwingUp;         // g_balance_shotgun_secondary_melee_swing_up
        public float MeleeTime;            // g_balance_shotgun_secondary_melee_time
        public float MeleeTraces;          // g_balance_shotgun_secondary_melee_traces
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    // METHOD(Shotgun, describe) — common/weapons/weapon/shotgun.qc:416-428 (MENUQC).
    // Four PAR() paragraphs describing primary fan, secondary slap, ammo, and range limitations.
    // Name substitutions (%s COLORED_NAME) are pre-filled with literals (same convention as Electro/Tuba).
    // The trailing W_Guide_Keybinds and W_Guide_DPS_primaryMultishot lines are owned by the guide page
    // and not reproduced here (those helpers aren't ported to Common).
    public override string? GuideDescription =>
        "The Shotgun is one of the two main default weapons, firing a single shotgun round which spreads "
      + "into multiple pellets upon exiting the barrel.\n\n"
      + "The secondary fire swings the Shotgun, slapping players close enough with the head of the weapon. "
      + "Since the slap takes a moment to land, timing this well is difficult.\n\n"
      + "The primary fire consumes Shells ammo, although if you spawn in with the Shotgun you will already have some.\n\n"
      + "The Shotgun damage drops off quickly as the range increases, so it is only useful for close combat "
      + "or sometimes medium range combat.";

    /// <summary>QC IT_UNLIMITED_AMMO item bit (mirrors the local const used by WeaponFireGate/Vortex).</summary>
    private const int ItUnlimitedAmmo = 1 << 0;


    public Shotgun()
    {
        NetName = "shotgun";
        BotPickupBaseValue = 6000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Shells;   // QC ammo_type
        DisplayName = "Shotgun";
        Impulse = 2;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_TYPE_MELEE_SEC | WEP_FLAG_BLEED
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.TypeMeleeSec | WeaponFlags.Bleed;
        Color = new Vector3(0.518f, 0.608f, 0.659f);
        ViewModel = "h_shotgun.iqm";  // MDL_SHOTGUN_VIEW
        WorldModel = "v_shotgun.md3"; // MDL_SHOTGUN_WORLD
        ItemModel = "g_shotgun.md3";  // MDL_SHOTGUN_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_shotgun_primary_ammo", 1f);
        Primary.Animtime = Bal("g_balance_shotgun_primary_animtime", 0.2f);
        Primary.Bullets = Bal("g_balance_shotgun_primary_bullets", 12f);
        Primary.Damage = Bal("g_balance_shotgun_primary_damage", 4f);
        Primary.Force = Bal("g_balance_shotgun_primary_force", 15f);
        Primary.Refire = Bal("g_balance_shotgun_primary_refire", 0.75f);
        Primary.SolidPenetration = Bal("g_balance_shotgun_primary_solidpenetration", 3.8f);
        Primary.Spread = Bal("g_balance_shotgun_primary_spread", 0.12f);

        Secondary.Secondary = BalInt("g_balance_shotgun_secondary", 1);
        Secondary.Animtime = Bal("g_balance_shotgun_secondary_animtime", 1.15f);
        Secondary.AltAnimtime = Bal("g_balance_shotgun_secondary_alt_animtime", 0.2f);
        Secondary.Damage = Bal("g_balance_shotgun_secondary_damage", 70f);
        Secondary.Force = Bal("g_balance_shotgun_secondary_force", 200f);
        Secondary.Refire = Bal("g_balance_shotgun_secondary_refire", 1.25f);
        Secondary.AltRefire = Bal("g_balance_shotgun_secondary_alt_refire", 1.2f);
        Secondary.MeleeBlockedByFiring = BalBool("g_balance_shotgun_secondary_melee_blockedbyfiring", false);
        Secondary.MeleeDelay = Bal("g_balance_shotgun_secondary_melee_delay", 0.25f);
        Secondary.MeleeMultihit = Bal("g_balance_shotgun_secondary_melee_multihit", 1f);
        Secondary.MeleeNoDoubleslap = BalBool("g_balance_shotgun_secondary_melee_no_doubleslap", true);
        Secondary.MeleeNonplayerDamage = Bal("g_balance_shotgun_secondary_melee_nonplayerdamage", 40f);
        Secondary.MeleeRange = Bal("g_balance_shotgun_secondary_melee_range", 120f);
        Secondary.MeleeSwingSide = Bal("g_balance_shotgun_secondary_melee_swing_side", 120f);
        Secondary.MeleeSwingUp = Bal("g_balance_shotgun_secondary_melee_swing_up", 30f);
        Secondary.MeleeTime = Bal("g_balance_shotgun_secondary_melee_time", 0.15f);
        Secondary.MeleeTraces = Bal("g_balance_shotgun_secondary_melee_traces", 10f);
    }

    // METHOD(Shotgun, wr_think) — common/weapons/weapon/shotgun.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        float rate = WeaponRateFactor(actor);

        if (fire == FireMode.Primary)
        {
            // QC handles the refire SEPARATELY from the shared ATTACK_FINISHED so a melee can follow a primary
            // blast straight away: the primary passes ANIMTIME (not refire) into weapon_prepareattack — so the
            // shared ATTACK_FINISHED is held for only ~0.2s — and parks the REAL 0.75 refire in
            // shotgun_primarytime, a private gate it checks first.
            //   if (time >= shotgun_primarytime)
            //   if (weapon_prepareattack(..., false, WEP_CVAR_PRI(animtime))) { ...; shotgun_primarytime = ...; }
            if (Api.Clock.Time >= st.ShotgunPrimaryTime
                && PrepareAttack(actor, slot, fire, attackTime: Primary.Animtime))
            {
                Attack(actor, slot);
                st.ShotgunPrimaryTime = Api.Clock.Time + Primary.Refire * rate;
            }
        }
        else if (fire == FireMode.Secondary)
        {
            if (Secondary.Secondary == 2)
            {
                // secondary==2 triple-shot: same private-timer pattern as primary. QC passes the SEPARATE
                // alt_animtime (0.2) into weapon_prepareattack and parks alt_refire (1.2) in shotgun_primarytime
                // (shotgun.qc:316,334) — NOT the melee refire/animtime (1.25/1.15).
                //
                // After the first blast, QC schedules W_Shotgun_Attack3_Frame1 via weapon_thinkf(alt_animtime)
                // (shotgun.qc:335): Frame1 fires a second blast and schedules Frame2; Frame2 fires a third and
                // returns to ready. Each Frame checks ammo and calls W_SwitchWeapon_Force if dry. The staged
                // schedule uses the same WeaponFireDriver.ScheduleThink seam that weapon_thinkf uses — the last
                // write wins, so scheduling Frame1 here OVERWRITES the w_ready that PrepareAttack just parked
                // (PrepareAttack schedules animtime for ready; we replace it with animtime for Frame1).
                if (Api.Clock.Time >= st.ShotgunPrimaryTime
                    && PrepareAttack(actor, slot, fire, attackTime: Secondary.AltAnimtime))
                {
                    Attack(actor, slot);
                    st.ShotgunPrimaryTime = Api.Clock.Time + Secondary.AltRefire * rate;
                    // Schedule Frame1 (overwrites the w_ready PrepareAttack parked — QC weapon_thinkf last-wins).
                    float frameDelay = Secondary.AltAnimtime * rate;
                    WeaponFireDriver.ScheduleThink(st, frameDelay,
                        (pl, sl) => Attack3Frame1(pl, sl));
                }
            }
        }

        // Melee (secondary == 1): QC routes it after the primary/triple-shot block, gated by its OWN
        // weapon_prepareattack(..., true, WEP_CVAR_SEC(refire)) on the shared ATTACK_FINISHED — which the
        // primary only held for animtime — plus the melee_blockedbyfiring guard (default 0, so NOT gated by
        // shotgun_primarytime). That is what lets the slap land immediately after a blast.
        //
        // QC also auto-melees on an EMPTY primary press (shotgun.qc:343): a non-bot whose shells AND clip are
        // exhausted (and has no IT_UNLIMITED_AMMO) slaps instead of dry-firing. So the gate triggers on EITHER
        // a secondary press OR an out-of-ammo primary press.
        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        bool emptyPrimaryAutoMelee = fire == FireMode.Primary
            && actor is not Player { IsBot: true }
            && actor.GetResource(AmmoType) <= 0f
            && st.ClipLoad == 0
            && !unlimited;
        if (Secondary.Secondary == 1
            && (fire == FireMode.Secondary || emptyPrimaryAutoMelee)
            && (!Secondary.MeleeBlockedByFiring || Api.Clock.Time >= st.ShotgunPrimaryTime)
            // QC weapon_prepareattack(thiswep, actor, weaponentity, true, refire): the `true` selects the
            // SECONDARY ammo check (wr_checkammo2 -> melee -> always available) + the secondary refire, while
            // the button was already gated upstream by the `fire` bitmask. So commit the secondary MODE but
            // gate the held-button on whichever button actually triggered this (an empty PRIMARY press must
            // satisfy the gate via ATK1, not ATK2).
            && PrepareAttack(actor, slot, FireMode.Secondary, attackTime: float.NaN, buttonFire: fire))
        {
            Melee(actor, slot);
        }
        // Multi-frame scheduling of the triple-shot (W_Shotgun_Attack3_Frame1/2) is driven via
        // WeaponFireDriver.ScheduleThink above (secondary==2 path schedules Frame1 after each blast).
    }

    // METHOD(Shotgun, wr_aim) — shotgun.qc:267-273. A bot presses ATCK2 (the melee slap) when the enemy is
    // within melee_range, otherwise ATCK (the pellet fan). The brain has already decided the shot via the
    // generic BotAim; this hook only owns the primary-vs-secondary button pick. Returning true routes the shot
    // onto ATCK2.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
        => enemyDistance <= Secondary.MeleeRange;

    // Refire/animtime from the (cvar-seeded) balance blocks (primary fan vs secondary melee/triple).
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // W_Shotgun_Attack — fire `bullets` hitscan pellets in a spread pattern. shotgun.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // W_DecreaseAmmo(thiswep, actor, ammo)
        actor.TakeResource(AmmoType, Primary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        // fired credit: damage * bullets — the whole volley's potential (QC shotgun.qc:19-20).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward,
            wep: this, maxDamage: Primary.Damage * Primary.Bullets, recoil: 5f);

        int deathType = RegistryId;
        int pellets = (int)Primary.Bullets;

        // QC: when spread_pattern_scale > 0, pellets lay out via W_CalculateSpreadPattern (a deterministic
        // fan); otherwise each pellet gets independent W_CalculateSpread random spread. We use the random
        // per-pellet path (g_balance_shotgun_primary_spread_pattern defaults off in xonotic balance).
        for (int i = 0; i < pellets; ++i)
        {
            WeaponFiring.FireBullet(actor, shot.Origin, shot.Dir, WeaponFiring.CurrentMaxShotDistance, Primary.Damage,
                deathType, Primary.Spread, Primary.SolidPenetration, force: Primary.Force);
            Vector3 impEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
            TraceResult impTr = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, impEnd).Trace; // [T45] warpzone-aware: impact FX land on the far side of a portal
            // w_backoff = the impact surface normal (trace_plane_normal), -force_dir fallback when no hit.
            Vector3 backoff = impTr.PlaneNormal.LengthSquared() > 1e-6f ? impTr.PlaneNormal : -shot.Dir;
            // QC wr_impacteffect (CSQC shotgun.qc:400-410): EFFECT_SHOTGUN_IMPACT at w_org + w_backoff*2 plus
            // the 5%-chance / 0.25s-throttled SND_RIC_RANDOM ricochet — both routed through the shared seam now
            // (BulletImpactFx owns the impact puff + the previously-dead SoundSystem.PlayRic).
            WeaponFiring.BulletImpactFx(actor, impTr.EndPos, backoff, "SHOTGUN_IMPACT");
        }

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/shotgun_fire.wav");
        EffectEmitter.Emit("SHOTGUN_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // Casing eject — QC W_Shotgun_Attack (shotgun.qc:78-83): SpawnCasing when g_casings>=1 (the shotgun
        // shell gate; default 2). The shared seam applies the per-casingtype gate (Shell => >=1) and the QC
        // shotgun view-frame eject velocity (up (30 - rand*5), shotgun.qc:82), then routes the Shell casing to
        // the client's EffectSystem.SpawnCasing (a real bouncing brass shell).
        WeaponFiring.EjectCasing(actor, shot.Origin, WeaponFiring.CasingType.Shell);
    }

    // W_Shotgun_Attack3_Frame1 — second blast of the alt triple-shot sequence. shotgun.qc:236-263.
    // Called by the weapon-think scheduled after the first blast fires. Checks ammo (wr_checkammo2),
    // force-switches to the best weapon if dry, fires a second blast, then schedules Frame2.
    private void Attack3Frame1(Entity actor, WeaponSlot slot)
    {
        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        // QC wr_checkammo2 on secondary==2: pool OR clip >= primary_ammo.
        if (!CheckAmmoSecondary(actor) && !unlimited)
        {
            // W_SwitchWeapon_Force: switch to the best usable weapon (QC w_getbestweapon).
            SwitchToOtherWeapon(actor, slot);
            // QC: weapon_thinkf + w_ready -> become ready so the new weapon can raise.
            WeaponSlotState st2 = actor.WeaponState(slot);
            if (st2.State == WeaponFireState.InUse)
                st2.State = WeaponFireState.Ready;
            return;
        }

        Attack(actor, slot);

        // Schedule Frame2 after alt_animtime (QC: weapon_thinkf(..., alt_animtime, W_Shotgun_Attack3_Frame2)).
        float rate = WeaponRateFactor(actor);
        WeaponFireDriver.ScheduleThink(actor.WeaponState(slot), Secondary.AltAnimtime * rate,
            (pl, sl) => Attack3Frame2(pl, sl));
    }

    // W_Shotgun_Attack3_Frame2 — third (final) blast of the alt triple-shot. shotgun.qc:207-234.
    // Checks ammo, force-switches if dry, fires the last blast, then returns the slot to READY.
    // QC passes is_primary=true to trick the last shot into playing the full reload sound.
    private void Attack3Frame2(Entity actor, WeaponSlot slot)
    {
        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        if (!CheckAmmoSecondary(actor) && !unlimited)
        {
            SwitchToOtherWeapon(actor, slot);
            WeaponSlotState st2 = actor.WeaponState(slot);
            if (st2.State == WeaponFireState.InUse)
                st2.State = WeaponFireState.Ready;
            return;
        }

        Attack(actor, slot);

        // QC: weapon_thinkf(actor, weaponentity, WFRAME_FIRE1, alt_animtime, w_ready) — return to ready.
        float rate = WeaponRateFactor(actor);
        WeaponFireDriver.ScheduleThink(actor.WeaponState(slot), Secondary.AltAnimtime * rate,
            static (pl, sl) =>
            {
                WeaponSlotState s3 = pl.WeaponState(sl);
                if (s3.State == WeaponFireState.InUse)
                    s3.State = WeaponFireState.Ready;
            });
    }

    // W_Shotgun_Attack2 — start a melee swing. shotgun.qc:193-204. Plays the swing sound and schedules the
    // W_Shotgun_Melee_Think to begin after melee_delay, spreading its melee_traces over melee_time.
    private void Melee(Entity actor, WeaponSlot slot)
    {
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/shotgun_melee.wav");

        float rate = WeaponRateFactor(actor);

        // QC spawns a `meleetemp` think entity (shotgun.qc:198-203): realowner = actor, think =
        // W_Shotgun_Melee_Think, nextthink = time + melee_delay * rate. The think then runs over melee_time,
        // doing a batch of swing traces each server frame so a target can move out of a partially-completed
        // swing and the slap lands after the 0.25s wind-up rather than instantly.
        Entity meleetemp = Api.Entities.Spawn();
        meleetemp.ClassName = "meleetemp";
        meleetemp.Owner = actor;          // QC realowner
        meleetemp.MoveType = MoveType.None; // only runs its think (SimulationLoop MOVETYPE_NONE path)
        meleetemp.Solid = Solid.Not;

        // Swing state carried across the multi-frame think (QC .cnt / .swing_prev / .swing_alreadyhit). Held in
        // a closure-captured holder rather than on the shared Entity so no scratch fields leak onto every entity.
        var swing = new MeleeSwingState();
        meleetemp.Think = self => MeleeThink(self, actor, rate, swing);
        meleetemp.NextThink = Api.Clock.Time + Secondary.MeleeDelay * rate;
    }

    /// <summary>Swing state for the multi-frame melee think (QC <c>.cnt</c>/<c>.swing_prev</c>/<c>.swing_alreadyhit</c>).</summary>
    private sealed class MeleeSwingState
    {
        public float Cnt;            // swing start time (0 until the first think run sets it)
        public float SwingPrev;      // the next trace index to start from
        public Entity? AlreadyHit;   // multihit dedupe across frames
    }

    // W_Shotgun_Melee_Think — shotgun.qc:88-191. Runs each server frame across melee_time, performing the
    // batch of swing-arc traces due this frame (swing_prev..f) and applying swing-scaled damage; re-schedules
    // itself until melee_time elapses, then frees the think entity.
    private void MeleeThink(Entity self, Entity actor, float rate, MeleeSwingState swing)
    {
        float now = Api.Clock.Time;

        if (swing.Cnt == 0f) // QC: if (!this.cnt) — set the swing start + play the strength sound
        {
            swing.Cnt = now;
            MutatorHooks.FireWPlayStrengthSound(actor);
        }

        // QC: give up now if the owner died mid-swing (melee_no_doubleslap, shotgun.qc:104-108).
        if (actor.DeadState != DeadFlag.No && Secondary.MeleeNoDoubleslap)
        {
            Api.Entities.Remove(self);
            return;
        }

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 eye = actor.Origin + actor.ViewOfs;

        // swing percentage based on elapsed time (shotgun.qc:99-101): the swing fans the traces out over
        // melee_time so later frames do later (lower-index) traces.
        float meleetime = Secondary.MeleeTime * rate;
        float swingPct = QMath.Clamp((swing.Cnt + meleetime - now) / meleetime, 0f, 10f);
        float f = (1f - swingPct) * Secondary.MeleeTraces;

        // QC tags every melee Damage() with WEP_SHOTGUN.m_id | HITTYPE_SECONDARY (shotgun.qc:152), which selects
        // WEAPON_SHOTGUN_MURDER_SLAP over WEAPON_SHOTGUN_MURDER in wr_killmessage. The int-deathtype ApplyDamage
        // seam can't carry the HITTYPE bit, so the slap routes through Combat.Damage directly with the
        // secondary-tagged deathtype, mirroring how Crylink's secondary threads HITTYPE_SECONDARY.
        string slapDeathType = DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Secondary);

        Vector3 meleePath = up * Secondary.MeleeSwingUp + right * Secondary.MeleeSwingSide;

        // [sv-antilag.melee.secondary] Base traces each swing-arc segment through WarpZone_traceline_antilag
        // (shotgun.qc:124, at ANTILAG_LATENCY for a client owner). Bracket this frame's batch in the shared
        // LagComp facade — no-op on a client/test/bot.
        LagComp.Begin(actor);
        try
        {
            float i = swing.SwingPrev;
            for (; i < f; ++i)
            {
                float swingFactor = (1f - (i / Secondary.MeleeTraces)) * 2f - 1f;
                Vector3 targPos = eye + meleePath * swingFactor + forward * Secondary.MeleeRange;

                TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, targPos, MoveFilter.Normal, actor);
                // QC Send_Effect(EFFECT_SHOTGUN_WOOSH, trace_endpos, -melee_path, 1) per swing trace.
                WeaponFiring.MeleeWoosh(actor, tr.EndPos, -meleePath, "SHOTGUN_WOOSH", swingSound: null);

                Entity? victim = tr.Ent;
                if (tr.Fraction < 1f && victim is not null && victim.TakeDamage != DamageMode.No
                    && !ReferenceEquals(victim, swing.AlreadyHit))
                {
                    // is_player ? damage : melee_nonplayerdamage, scaled by min(1, swing_factor + 1). QC's
                    // is_player = IS_PLAYER || classname=="body" || IS_MONSTER (shotgun.qc:134).
                    bool isPlayer = (victim.Flags & EntFlags.Client) != 0 || victim.IsCorpse
                                  || (victim.Flags & EntFlags.Monster) != 0;
                    float baseDmg = isPlayer ? Secondary.Damage : Secondary.MeleeNonplayerDamage;
                    if (!isPlayer && Secondary.MeleeNonplayerDamage == 0f) continue; // QC nonplayer gate
                    float swingDamage = baseDmg * MathF.Min(1f, swingFactor + 1f);

                    Vector3 force = forward * Secondary.Force;
                    Combat.Damage(victim, actor, actor, swingDamage, slapDeathType, eye, force);

                    if (Secondary.MeleeMultihit != 0f)
                    {
                        swing.AlreadyHit = victim; // allow multiple hits per swing, but never the same target twice
                        continue;
                    }
                    // single hit per swing -> done
                    Api.Entities.Remove(self);
                    return;
                }
            }
            swing.SwingPrev = i;
        }
        finally
        {
            LagComp.End();
        }

        if (now >= swing.Cnt + meleetime)
        {
            Api.Entities.Remove(self); // swing finished
            return;
        }

        // set up next frame (QC: this.nextthink = time) — the SimulationLoop re-runs us next tick.
        self.NextThink = now;
    }

    // METHOD(Shotgun, wr_checkammo1) — shotgun.qc:354-359: have ammo if the shared pool OR the persistent
    // magazine (weapon_load[m_id], reloadable) can cover one primary shot (reload_ammo defaults 0 so the clip
    // term is moot for default play, but it must be present for a reloadable shotgun ruleset).
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= Primary.Ammo
        || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Primary.Ammo;

    // METHOD(Shotgun, wr_checkammo2) — shotgun.qc:361-376. 1 = melee (no ammo), 2 = triple-shot (pool OR clip),
    // else secondary unavailable.
    public bool CheckAmmoSecondary(Entity actor) => Secondary.Secondary switch
    {
        1 => true,
        2 => actor.GetResource(AmmoType) >= Primary.Ammo
          || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Primary.Ammo,
        _ => false,
    };
}
