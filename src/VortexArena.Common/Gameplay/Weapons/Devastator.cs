using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Devastator (Nexuiz "Rocket Launcher") — port of common/weapons/weapon/devastator.{qh,qc}. A splash
/// weapon: primary fire launches a single rocket that accelerates from speedstart toward speed, detonates
/// on contact (or at end of lifetime) dealing radius damage + knockback; secondary fire remote-detonates
/// rockets already in flight. The rocket is damageable (can be shot down).
///
/// Identity/attributes from devastator.qh; balance from bal-wep-xonotic.cfg (g_balance_devastator_*).
/// This port covers the rocket projectile, its acceleration, laser guiding (W_Devastator_SteerTo steering
/// toward the owner's aim, gated by guidedelay/guiderate + the rl_release latch), the spawnshield/proximity
/// remote-detonation gate, shoot-down (event_damage -> W_PrepareExplosionByDamage), the airshot achievement,
/// the HITTYPE_BOUNCE kill-message split, and the splash damage. Only the rocket-jump remote variant
/// (remote_jump*, stock-off), per-weapon bot aim (wr_aim) and CSQC fly-sound networking are left out.
/// </summary>
[Weapon]
public sealed class Devastator : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_DEVASTATOR, *) (single fire mode, no PRI/SEC split).</summary>
    public struct Balance
    {
        public float Ammo;            // g_balance_devastator_ammo (rockets per shot)
        public float Animtime;        // g_balance_devastator_animtime
        public float Damage;          // g_balance_devastator_damage
        public float DamageForceScale;// g_balance_devastator_damageforcescale
        public float DetonateDelay;   // g_balance_devastator_detonatedelay (>=0 timer, <0 proximity)
        public float EdgeDamage;      // g_balance_devastator_edgedamage
        public float Force;           // g_balance_devastator_force
        public float ForceXyScale;    // g_balance_devastator_force_xyscale
        public float GuideDelay;      // g_balance_devastator_guidedelay (delay before steering starts)
        public float GuideGoal;       // g_balance_devastator_guidegoal (goal point distance along the aim ray)
        public float GuideRate;       // g_balance_devastator_guiderate
        public float GuideRateDelay;  // g_balance_devastator_guideratedelay (ramp-in duration for the turn rate)
        public float Health;          // g_balance_devastator_health (shootable rocket hp)
        public float Lifetime;        // g_balance_devastator_lifetime
        public float Radius;          // g_balance_devastator_radius
        public float Refire;          // g_balance_devastator_refire
        public float RemoteDamage;    // g_balance_devastator_remote_damage
        public float RemoteEdgeDamage;// g_balance_devastator_remote_edgedamage
        public float RemoteForce;     // g_balance_devastator_remote_force
        public float RemoteRadius;    // g_balance_devastator_remote_radius
        public float RemoteJump;          // g_balance_devastator_remote_jump (0/1: enable the rocket-jump variant)
        public float RemoteJumpDamage;    // g_balance_devastator_remote_jump_damage
        public float RemoteJumpForce;     // g_balance_devastator_remote_jump_force
        public float RemoteJumpRadius;    // g_balance_devastator_remote_jump_radius
        public float RemoteJumpVelocityZAdd; // g_balance_devastator_remote_jump_velocity_z_add
        public float RemoteJumpVelocityZMin; // g_balance_devastator_remote_jump_velocity_z_min
        public float RemoteJumpVelocityZMax; // g_balance_devastator_remote_jump_velocity_z_max
        public float Speed;           // g_balance_devastator_speed (target/top speed)
        public float SpeedAccel;      // g_balance_devastator_speedaccel
        public float SpeedStart;      // g_balance_devastator_speedstart (launch speed)
    }

    public Balance Cvars;


    public Devastator()
    {
        NetName = "devastator";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Devastator";
        Impulse = 9;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.CanClimb | WeaponFlags.TypeSplash;
        Color = new Vector3(0.914f, 0.745f, 0.341f);
        ViewModel = "h_rl.iqm";  // MDL_DEVASTATOR_VIEW
        WorldModel = "v_rl.md3"; // MDL_DEVASTATOR_WORLD
        ItemModel = "g_rl.md3";  // MDL_DEVASTATOR_ITEM
    }

    /// <summary>
    /// METHOD(Devastator, describe) — common/weapons/weapon/devastator.qc:583-599. The in-menu weapon-guide
    /// prose: the remote-controlled / hold-to-guide rocket, the secondary remote detonation, the heavy ammo
    /// drain (alternate with the Vortex), the all-round high-damage role, and "rocket flying" (best with the
    /// Rocket Flying mutator). The QC builds it with PAR() paragraphs and COLORED_NAME(%s) substitutions (this
    /// weapon, ITEM_Rockets, WEP_VORTEX, MUTATOR_rocketflying); the names are pre-filled with their literals
    /// here. The trailing W_Guide_Keybinds + W_Guide_DPS_onlyOne_unnamed helper lines are not reproduced (those
    /// helpers aren't ported), matching the rest of the ported weapon-guide entries.
    /// </summary>
    public override string? GuideDescription =>
        "The Devastator launches a remote controlled rocket, dealing significant damage when it explodes on "
      + "impact. If the primary fire is held, the rocket can be guided by the user's aim, allowing steering it "
      + "towards enemies.\n\n"
      + "The secondary fire can be used to immediately detonate rockets, allowing dealing damage to enemies even "
      + "if the rocket barely missed colliding with them.\n\n"
      + "It consumes a bunch of Rockets ammo for each rocket, which can end up being depleted quickly, so often "
      + "players alternate with another weapon like the Vortex.\n\n"
      + "Due to its high damage output, the Devastator is one of the most commonly used weapons. It can be used "
      + "in almost any scenario, working best in medium range combat. In close range combat, the large splash "
      + "radius often results in rockets damaging both you and your enemy.\n\n"
      + "Due to the ability to remotely detonate rockets, a common usage is \"rocket flying,\" where you fire a "
      + "rocket and immediately detonate it to boost yourself while mid-air, much more effective with the Rocket "
      + "Flying mutator enabled.";

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_devastator_ammo", 4f);
        Cvars.Animtime = Bal("g_balance_devastator_animtime", 0.4f);
        Cvars.Damage = Bal("g_balance_devastator_damage", 80f);
        Cvars.DamageForceScale = Bal("g_balance_devastator_damageforcescale", 0f);
        Cvars.DetonateDelay = Bal("g_balance_devastator_detonatedelay", 0.02f);
        Cvars.EdgeDamage = Bal("g_balance_devastator_edgedamage", 40f);
        Cvars.Force = Bal("g_balance_devastator_force", 400f);
        Cvars.ForceXyScale = Bal("g_balance_devastator_force_xyscale", 1f);
        Cvars.GuideDelay = Bal("g_balance_devastator_guidedelay", 0.2f);
        Cvars.GuideGoal = Bal("g_balance_devastator_guidegoal", 512f);
        Cvars.GuideRate = Bal("g_balance_devastator_guiderate", 90f);
        Cvars.GuideRateDelay = Bal("g_balance_devastator_guideratedelay", 0.01f);
        Cvars.Health = Bal("g_balance_devastator_health", 30f);
        Cvars.Lifetime = Bal("g_balance_devastator_lifetime", 10f);
        Cvars.Radius = Bal("g_balance_devastator_radius", 110f);
        Cvars.Refire = Bal("g_balance_devastator_refire", 1.1f);
        Cvars.RemoteDamage = Bal("g_balance_devastator_remote_damage", 70f);
        Cvars.RemoteEdgeDamage = Bal("g_balance_devastator_remote_edgedamage", 35f);
        Cvars.RemoteForce = Bal("g_balance_devastator_remote_force", 300f);
        Cvars.RemoteRadius = Bal("g_balance_devastator_remote_radius", 110f);
        Cvars.RemoteJump = Bal("g_balance_devastator_remote_jump", 0f);
        Cvars.RemoteJumpDamage = Bal("g_balance_devastator_remote_jump_damage", 70f);
        Cvars.RemoteJumpForce = Bal("g_balance_devastator_remote_jump_force", 450f);
        Cvars.RemoteJumpRadius = Bal("g_balance_devastator_remote_jump_radius", 100f);
        Cvars.RemoteJumpVelocityZAdd = Bal("g_balance_devastator_remote_jump_velocity_z_add", 0f);
        Cvars.RemoteJumpVelocityZMin = Bal("g_balance_devastator_remote_jump_velocity_z_min", 400f);
        Cvars.RemoteJumpVelocityZMax = Bal("g_balance_devastator_remote_jump_velocity_z_max", 1500f);
        Cvars.Speed = Bal("g_balance_devastator_speed", 1300f);
        Cvars.SpeedAccel = Bal("g_balance_devastator_speedaccel", 1300f);
        Cvars.SpeedStart = Bal("g_balance_devastator_speedstart", 1000f);
    }

    // METHOD(Devastator, wr_think) — common/weapons/weapon/devastator.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        if (fire == FireMode.Primary)
        {
            // rl_release latch: the primary must be re-pressed between launches (otherwise holding fire
            // would just guide the rocket). guidestop disables guiding and so disables the latch too. The
            // latch is reset whenever the primary button is NOT held (QC the !PHYS_INPUT_BUTTON_ATCK branch).
            if (!st.ButtonAttack)
            {
                st.RlRelease = true; // primary released -> next press may launch again
            }
            else
            {
                bool guidestop = Api.Services is not null && Api.Cvars.GetFloat("g_balance_devastator_guidestop") != 0f;
                // Launch only on a fresh press (latch set) or when guiding is disabled, AND when the refire
                // gate + ammo allow (QC weapon_prepareattack(..., refire)).
                if ((st.RlRelease || guidestop) && PrepareAttack(actor, slot, fire))
                {
                    Attack(actor, slot, st, secondary: false);
                    st.RlRelease = false;
                }
            }
        }

        if (fire == FireMode.Secondary)
        {
            // QC wr_think (devastator.qc): the secondary remote-detonate runs only inside
            // `if (fire & 2) { if (actor.(weaponentity).m_switchweapon == thiswep) { ... } }` — you can only
            // remote-detonate while the Devastator is still the slot's committed weapon. Once a switch AWAY is
            // in progress (m_switchweapon != m_weapon == thiswep) the detonate is suppressed. Mirror the QC gate
            // with the established "switching" idiom (Machinegun.cs:365): skip when the slot is switching to a
            // different weapon. Then flag every live rocket this actor owns (gated by spawnshield/proximity in
            // the rocket's think, W_Devastator_RemoteExplode). This is a press action, not a refire-gated shot.
            bool switchingAway = st.SwitchWeaponId >= 0 && st.SwitchWeaponId != RegistryId;
            if (!switchingAway)
                RemoteDetonate(actor);
        }
    }

    // METHOD(Devastator, wr_setup) — arm the rl_release latch on equip so the first primary press launches a
    // rocket (instead of being eaten as a "still guiding the previous one" hold). devastator.qc:491-494.
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        actor.WeaponState(slot).RlRelease = true;
    }

    // Refire/animtime from the (cvar-seeded) balance block — the Devastator has a single fire mode (the
    // secondary remote-detonate is a press action, not a refire-gated shot).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_Devastator_Attack — spawn an accelerating, guidable, detonatable rocket. devastator.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, bool secondary)
    {
        // QC W_Devastator_Attack: W_DecreaseAmmo (devastator.qc:286) — NOT a raw resource drain. The shared
        // DecreaseAmmo honors IT_UNLIMITED_AMMO (g_weaponarena) and the reload clip; the old direct TakeResource
        // kept draining under unlimited ammo, driving the rocket count NEGATIVE in the ammo panel (playtest #25).
        DecreaseAmmo(actor, slot, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-3, -3, -3), new Vector3(3, 3, 3), recoil: 5f);

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "rocket";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        // QC PROJECTILE_MAKETRIGGER: SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE so the rocket is
        // transparent to player movement (no CORPSE in a player's move mask) — this is what stops the rocket
        // colliding with / detonating on its firer — yet still hits the world, bodies and corpses.
        Projectiles.MakeTrigger(missile);
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        missile.DamageForceScale = Cvars.DamageForceScale;

        // QC missile.damagedbycontents = true; IL_PUSH(g_damagedbycontents, missile) (devastator.qc:285) — the
        // rocket explodes when it enters lava/slime. GameWorld.CreatureFrameAll sweeps every entity flagged
        // Entity.DamagedByContents through PlayerFrameLogic.ProjectileContentsDamage (the same infra Electro orbs
        // use); a NULL-inflictor contents hit then routes through the rocket's MakeShootable shoot-down shim ->
        // ProjectileDamage (Explode) at hp<=0. Without this flag the rocket is excluded from the sweep.
        missile.DamagedByContents = true;

        // setsize '-3 -3 -3'..'3 3 3' so the rocket can be shot down.
        Api.Entities.SetSize(missile, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        // setorigin(missile, w_shotorg - v_forward * 3) — back it off so it hits the wall at the right point.
        Api.Entities.SetOrigin(missile, shot.Origin - shot.Dir * 3f);

        // Shootable rocket (event_damage -> W_Devastator_Damage): give it health + DAMAGE_YES.
        missile.TakeDamage = DamageMode.Yes;
        missile.Health = Cvars.Health;

        // W_SetupProjVelocity_Basic(missile, speedstart, 0) — launches at speedstart, the think accelerates it.
        missile.Velocity = shot.Dir * Cvars.SpeedStart;
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC devastator.qc:301-302 — flag the rocket as a dodgeable hazard; rating = damage * 2 ("* 2 because it
        // can be detonated inflight which makes it even more dangerous"). Consumed by BotBrain.HavocbotDodge.
        missile.BotDodge = true;
        missile.BotDodgeRating = Cvars.Damage * 2f;

        float deathTime = Api.Clock.Time + Cvars.Lifetime;
        // QC missile.spawnshieldtime (devastator.qc:295-298): detonatedelay >= 0 sets a remote-detonation TIMER
        // gate; < 0 = proximity-safety based. Stored on the entity (Entity.ProjectileDetonateTime) — NOT a
        // closure local — so the Rocket Flying mutator's EditProjectile write (proj.spawnshieldtime = time)
        // can reach the gate W_Devastator_RemoteExplode reads. Seeded BEFORE the EditProjectile call below so
        // the mutator's clear wins (QC ordering: seed at :295, MUTATOR_CALLHOOK at :333).
        missile.ProjectileDetonateTime = Cvars.DetonateDelay >= 0f ? Api.Clock.Time + Cvars.DetonateDelay : -1f;
        // pushltime: the guide-delay before steering kicks in (reuse LTime).
        missile.LTime = Api.Clock.Time + Cvars.GuideDelay;
        missile.Count = 0; // guide-active sound guard

        // Register this as the actor's guided rocket; rl_detonate_later from secondary edge at launch.
        st.LastRocket = missile;
        if (secondary) missile.DeadState = DeadFlag.Dying; // rl_detonate_later latch (reuse DeadState)

        missile.Touch = (self, other) => OnTouch(self, other);
        missile.Think = self => OnThink(self, actor, slot, deathTime);
        // W_Devastator_Damage -> W_PrepareExplosionByDamage: the shoot-down callback only fires once HP hits 0
        // (Projectiles.MakeShootable runs the W_CheckProjectileDamage gate + RES_HEALTH subtraction first), so a
        // non-lethal graze no longer detonates the rocket. A shot-down rocket carries HITTYPE_BOUNCE (no direct hit).
        missile.ProjectileDamage = (self, attacker) => Explode(self, null, bounced: true);
        missile.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (devastator.qc) — before the rocket's first think,
        // so invincibleproj zeroes the rocket's health before it can be shot down.
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        // QC missile.event_damage = W_Devastator_Damage (+ DAMAGE_YES + RES_HEALTH already set above). Install the
        // GtEventDamage shoot-down shim the damage pipeline actually dispatches; without this the rocket's
        // ProjectileDamage callback is dead (no live invoker on the flying-rocket path). No exception (-1): the
        // rocket is destructible by whoever the g_projectiles_damage ladder permits. Called AFTER EditProjectile so
        // an invincibleproj-zeroed rocket is already at 0 hp and the first hit detonates it (faithful).
        Projectiles.MakeShootable(missile, exception: -1f);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/rocket_fire.wav");
        EffectEmitter.Emit("ROCKET_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        if (Api.Clock.Time >= missile.NextThink)
            missile.Think(missile);
    }

    // W_Devastator_Think — accelerate toward top speed, laser-guide toward the owner's aim, and handle
    // remote detonation; detonate at end of lifetime. devastator.qc
    private void OnThink(Entity self, Entity owner, WeaponSlot slot, float deathTime)
    {
        self.NextThink = Api.Clock.Time;
        if (Api.Clock.Time > deathTime)
        {
            // QC W_Devastator_Think: this.projectiledeathtype |= HITTYPE_BOUNCE on a lifetime-timeout detonation
            // so wr_killmessage classifies it as a SPLASH (not DIRECT) kill.
            Explode(self, null, bounced: true);
            return;
        }

        // accelerate: velspeed = speed*W_WeaponSpeedFactor - (velocity . forward); add up to
        // speedaccel*W_WeaponSpeedFactor*frametime along forward (QC W_Devastator_Think:198-200).
        float speedFactor = WeaponSpeedFactor();
        Vector3 forward = QMath.Normalize(self.Velocity);
        if (forward == Vector3.Zero) forward = QMath.Forward(self.Angles);
        float velAlong = QMath.Dot(self.Velocity, forward);
        float velSpeed = Cvars.Speed * speedFactor - velAlong;
        if (velSpeed > 0f)
            self.Velocity += forward * MathF.Min(Cvars.SpeedAccel * speedFactor * Api.Clock.FrameTime, velSpeed);

        // Laser guiding (W_Devastator_Think): while this is the owner's active rocket, the primary is still HELD
        // (rl_release latch clear — releasing fire stops guiding and re-arms a launch), the guide-delay has passed,
        // and the owner is alive, steer the rocket toward the owner's aim, capped at guiderate deg/s.
        var st = owner.WeaponState(slot);
        if (ReferenceEquals(st.LastRocket, self) && !st.RlRelease && !st.ButtonAttack2 && Cvars.GuideRate > 0f
            && Api.Clock.Time > self.LTime && owner.DeadState == DeadFlag.No)
        {
            // guideratedelay ramp: the turn rate eases in over guideratedelay after steering starts.
            float f = Cvars.GuideRateDelay > 0f
                ? QMath.Clamp((Api.Clock.Time - self.LTime) / Cvars.GuideRateDelay, 0f, 1f) : 1f;

            float curSpeed = self.Velocity.Length();
            QMath.AngleVectors(owner.Angles, out Vector3 desiredDir, out _, out _); // owner view forward (v_angle)
            Vector3 desiredOrigin = owner.Origin + owner.ViewOfs;                   // aim-ray origin (no dual-wield offset)
            Vector3 olddir = QMath.Normalize(self.Velocity);

            // QC goal-point steering: project the rocket onto the aim ray and target a point guidegoal units
            // further along it, then steer toward that goal — so the rocket curves ONTO the aim line (lead),
            // not merely parallel to it (the old pure-direction approximation never converged to the crosshair).
            Vector3 goal = desiredOrigin
                + (QMath.Dot(self.Origin - desiredOrigin, desiredDir) + Cvars.GuideGoal) * desiredDir;
            Vector3 newdir = SteerTo(olddir, QMath.Normalize(goal - self.Origin),
                MathF.Cos(Cvars.GuideRate * f * Api.Clock.FrameTime * QMath.Deg2Rad));

            self.Velocity = newdir * curSpeed;
            self.Angles = QMath.VecToAngles(self.Velocity);

            if (self.Count == 0)
            {
                // QC Send_Effect(EFFECT_ROCKET_GUIDE, ...) on the first guide tick (devastator.qc:243).
                EffectEmitter.Emit("ROCKET_GUIDE", self.Origin, self.Velocity, 1);
                Api.Sound.Play(owner, SoundChannel.Body, "weapons/rocket_mode.wav");
                self.Count = 1;
            }
        }
        else
        {
            self.Angles = QMath.VecToAngles(self.Velocity);
        }

        // rl_detonate_later: explode once the spawnshield timer (or proximity safety) clears.
        if (self.DeadState == DeadFlag.Dying && ReferenceEquals(st.LastRocket, self))
            RemoteExplode(self, owner);
    }

    /// <summary>
    /// Port of W_Devastator_SteerTo (devastator.qc): rotate <paramref name="thisdir"/> toward
    /// <paramref name="goaldir"/> by at most the angle whose cosine is <paramref name="maxTurnCos"/>. Solves
    /// the same quadratic QC does for the blend factor; refuses to guide when nearly antiparallel.
    /// </summary>
    private static Vector3 SteerTo(Vector3 thisdir, Vector3 goaldir, float maxTurnCos)
    {
        float f = QMath.Dot(thisdir, goaldir);
        if (f > maxTurnCos) return goaldir;       // already within the cone
        if (f < -0.9998f) return thisdir;          // ~antiparallel: refuse (avoid numerical blow-up)

        float m2 = maxTurnCos * maxTurnCos;
        // 0 = (m2 - f^2) x^2 + (2 f (m2 - 1)) x + (m2 - 1) ; take the larger root.
        float a = m2 - f * f, b = 2f * f * (m2 - 1f), c = m2 - 1f;
        float x;
        if (MathF.Abs(a) < 1e-6f)
        {
            x = (MathF.Abs(b) < 1e-6f) ? 0f : -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return goaldir;
            float sd = MathF.Sqrt(disc);
            x = (-b + sd) / (2f * a); // larger solution
        }
        return QMath.Normalize(thisdir + goaldir * x);
    }

    // W_Devastator_RemoteExplode — only detonate once the spawnshield timer / proximity safety allows. The gate
    // is the rocket's own Entity.ProjectileDetonateTime (QC this.spawnshieldtime), so the Rocket Flying mutator's
    // EditProjectile write (= time) reaches it: time >= the just-set timer => detonation opens immediately.
    private void RemoteExplode(Entity self, Entity owner)
    {
        if (owner.DeadState != DeadFlag.No) return;
        bool allowed;
        if (self.ProjectileDetonateTime >= 0f)
            allowed = Api.Clock.Time >= self.ProjectileDetonateTime; // timer gate (QC time >= spawnshieldtime)
        else
            allowed = (self.Origin - (owner.Origin + (owner.Mins + owner.Maxs) * 0.5f)).Length() > Cvars.RemoteRadius; // safety
        if (allowed)
            DoRemoteExplode(self, owner);
    }

    // W_Devastator_DoRemoteExplode — the actual remote (secondary) blast: remote_* balance, with the optional
    // dedicated rocket-jump self-boost (remote_jump_*) the Rocket Flying mutator forces on. devastator.qc:65-138.
    private void DoRemoteExplode(Entity self, Entity owner)
    {
        // QC W_Devastator_Unregister — clear lastrocket on ALL slots (the port slot-0-only clear ignored the
        // rocket's actual weaponentity slot, leaving a stale lastrocket reference in dual-wield play).
        Unregister(self);

        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null;

        // QC OR's HITTYPE_BOUNCE onto the remote blast deathtype (devastator.qc:123) so wr_killmessage classifies
        // a remote detonation as a SPLASH kill.
        string deathType = Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Bounce);

        // QC devastator.qc:72-76: seed allow_rocketjump from the weapon's remote_jump cvar, then let the
        // AllowRocketJumping hook chain override it (the Rocket Flying mutator forces it true).
        bool allowRocketjump = MutatorHooks.FireAllowRocketJumping(Cvars.RemoteJump != 0f);

        // QC devastator.qc:78-114: when rocket-jumping is allowed and remote_jump_radius is set, if the owner is
        // within remote_jump_radius this becomes a dedicated rocket-jump of the owner — optional vertical-velocity
        // shaping (gated by velocity_z_add) plus a remote_jump_* damage/force blast aimed at the owner — instead of
        // the plain remote_* blast.
        bool handledAsRocketjump = false;
        if (allowRocketjump && Cvars.RemoteJumpRadius != 0f
            && owner.TakeDamage != DamageMode.No
            && (self.Origin - (owner.Origin + (owner.Mins + owner.Maxs) * 0.5f)).Length() <= Cvars.RemoteJumpRadius)
        {
            handledAsRocketjump = true;

            // QC devastator.qc:89-98: modify velocity (only when velocity_z_add is set — default 0 = no-op).
            if (Cvars.RemoteJumpVelocityZAdd != 0f)
            {
                Vector3 v = owner.Velocity;
                v.X *= 0.9f;
                v.Y *= 0.9f;
                v.Z = QMath.Bound(Cvars.RemoteJumpVelocityZMin,
                    v.Z + Cvars.RemoteJumpVelocityZAdd, Cvars.RemoteJumpVelocityZMax);
                owner.Velocity = v;
            }

            // QC devastator.qc:101-111: the dedicated rocket-jump blast (remote_jump_damage as both core and edge,
            // remote_jump_force over remote_jump_radius), attacker=owner. QC passes head as the forceintersect arg
            // (force application target), NOT as a direct-hit/LOS-skip; the port's RadiusDamage has no
            // forceintersect param, and at point-blank rocket-jump range LOS reduction does not engage anyway.
            WeaponSplash.RadiusDamage(self, self.Origin, Cvars.RemoteJumpDamage, Cvars.RemoteJumpDamage,
                Cvars.RemoteJumpRadius, owner, RegistryId, Cvars.RemoteJumpForce,
                accuracyWeapon: this, deathTag: deathType);
        }

        // QC's plain remote blast goes through the RadiusDamage wrapper (forcexyzscale '1 1 1') — only the CONTACT
        // explosion (W_Devastator_Explode) applies force_xyscale, so no force shaping here. (QC's forceintersect
        // arg = handled_as_rocketjump ? head : NULL is a force-application refinement not modeled by the port's
        // RadiusDamage; the rocket-jump branch above already delivered the owner's dedicated push.)
        _ = handledAsRocketjump; // QC's forceintersect distinction (head vs NULL) has no port RadiusDamage analog.
        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.RemoteDamage, Cvars.RemoteEdgeDamage,
            Cvars.RemoteRadius, owner, RegistryId, Cvars.RemoteForce, accuracyWeapon: this, deathTag: deathType);
        WeaponSplash.ImpactSound(self, "weapons/rocket_impact.wav"); // QC SND_ROCKET_IMPACT (wr_impacteffect)
        EffectEmitter.Emit("ROCKET_EXPLODE", self.Origin);

        // QC W_Devastator_DoRemoteExplode tail (devastator.qc:128-136): out-of-rockets auto-switch after the blast.
        ForceSwitchIfOutOfAmmo(owner);
        Api.Entities.Remove(self);
    }

    // W_Devastator_Touch — explode on contact with the world or an entity. devastator.qc
    private void OnTouch(Entity self, Entity other) => Explode(self, other, bounced: false);

    // W_Devastator_Explode — radius damage + knockback (force_xyscale shaping) at impact, then remove. devastator.qc
    private void Explode(Entity self, Entity? directHit, bool bounced)
    {
        // QC W_Devastator_Unregister (devastator.qc:7-15): clear owner.lastrocket on EVERY weapon slot so a dead
        // rocket can never be guided/remote-detonated again (and the obituary doesn't chase a freed entity).
        Unregister(self);

        // QC airshot achievement (devastator.qc:21-24): a flying enemy directly struck mid-air earns the owner the
        // airshot announce. Tested BEFORE the entity is removed.
        if (directHit is not null && self.Owner is { } owner
            && directHit.TakeDamage == DamageMode.Aim && (directHit.Flags & EntFlags.Client) != 0
            && !Teams.SameTeam(directHit, owner) && !ReferenceEquals(directHit, owner) // QC DIFF_TEAM
            && directHit.DeadState == DeadFlag.No && IsFlying(directHit))
            NotificationSystem.Announce(owner, "ACHIEVEMENT_AIRSHOT");

        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null;

        // QC wr_killmessage keys on HITTYPE_BOUNCE|HITTYPE_SPLASH to pick MURDER_SPLASH vs MURDER_DIRECT. A
        // timed-out / shot-down rocket carries HITTYPE_BOUNCE (no direct hit); a contact hit does not (the splash
        // tag is added per-victim by RadiusDamage so the DIRECT victim stays DIRECT).
        string deathType = Damage.DeathTypes.FromWeapon(NetName);
        if (bounced) deathType = Damage.DeathTypes.WithHitType(deathType, Damage.DeathTypes.Bounce);

        // QC scales the HORIZONTAL knockback by force_xyscale (force_xyzscale.x/.y, devastator.qc:29-31;
        // Z stays 1); the direct-hit entity skips the LOS reduction.
        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            self.Owner, RegistryId, Cvars.Force,
            forceScale: new Vector3(Cvars.ForceXyScale, Cvars.ForceXyScale, 1f),
            directHit: directHit, accuracyWeapon: this, deathTag: deathType);

        WeaponSplash.ImpactSound(self, "weapons/rocket_impact.wav"); // QC SND_ROCKET_IMPACT (wr_impacteffect)
        EffectEmitter.Emit("ROCKET_EXPLODE", self.Origin);

        // QC W_Devastator_Explode tail (devastator.qc:47-56): out-of-rockets auto-switch after the contact blast.
        ForceSwitchIfOutOfAmmo(self.Owner);
        Api.Entities.Remove(self);
    }

    // W_Devastator_Explode / W_Devastator_DoRemoteExplode tail (devastator.qc:47-56, :128-136): after the blast, if
    // the owner is still wielding the Devastator and the rocket resource is now depleted (and not IT_UNLIMITED_AMMO),
    // force a switch to the best other owned weapon. QC: `cnt = WEP_DEVASTATOR.m_id; ATTACK_FINISHED(actor) = time;
    // actor.(weaponentity).m_switchweapon = w_getbestweapon(actor)`. SwitchToOtherWeapon (WeaponFireGate) ports
    // w_getbestweapon + the ATTACK_FINISHED/m_switchweapon assignment; the rocket carries no slot, so locate the
    // slot currently holding the Devastator on the owner. Mirrors Minelayer.ForceSwitchIfOutOfAmmo.
    private void ForceSwitchIfOutOfAmmo(Entity? owner)
    {
        if (owner is null || owner.IsFreed) return;
        if (owner.UnlimitedAmmo || (owner.Items & (1 << 0)) != 0) return; // IT_UNLIMITED_AMMO = BIT(0)
        if (CheckAmmoPrimary(owner)) return; // still has rockets for another shot — no switch

        for (int i = 0; i < MutatorConstants.MaxWeaponSlots; ++i)
        {
            var slot = new WeaponSlot(i);
            if (owner.WeaponState(slot).CurrentWeaponId == RegistryId)
            {
                SwitchToOtherWeapon(owner, slot);
                return;
            }
        }
    }

    // W_Devastator_Unregister (devastator.qc:7-15) — clear owner.lastrocket on ALL weapon slots that point at this
    // rocket (the QC scans weaponentities[0..MAX_WEAPONSLOTS); the port slot-0-only clear missed the 2nd slot).
    private static void Unregister(Entity self)
    {
        if (self.Owner is not { } owner) return;
        for (int i = 0; i < MutatorConstants.MaxWeaponSlots; ++i)
            if (owner.WeaponState(new WeaponSlot(i)) is { } s && ReferenceEquals(s.LastRocket, self))
                s.LastRocket = null;
    }

    // QC W_WeaponSpeedFactor: g_weaponspeedfactor (default 1) — multiplies the rocket's target speed + per-tick
    // acceleration. Mirrors the WeaponImpulses reader (only positive values take effect).
    private static float WeaponSpeedFactor()
    {
        if (Api.Services is null) return 1f;
        float f = Api.Cvars.GetFloat("g_weaponspeedfactor");
        return f > 0f ? f : 1f;
    }

    // bool IsFlying(entity) — common/physics/player.qc, the airshot test: airborne, not swimming, and at least
    // 24u of clearance below (so a player skimming the ground doesn't count).
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    // wr_think secondary — flag every live rocket this actor owns to remote-detonate (rl_detonate_later).
    // The rocket's own think then blasts it once its spawnshield/proximity safety gate allows (so you can't
    // instantly detonate a rocket that's still inside your own blast radius). devastator.qc
    private void RemoteDetonate(Entity actor)
    {
        bool any = false;
        foreach (Entity e in Api.Entities.FindByClass("rocket"))
        {
            if (!ReferenceEquals(e.Owner, actor) || e.IsFreed) continue;
            if (e.NetName != NetName) continue;
            if (e.DeadState != DeadFlag.Dying) // not yet flagged
            {
                e.DeadState = DeadFlag.Dying; // rl_detonate_later latch
                any = true;
            }
        }
        if (any)
            Api.Sound.Play(actor, SoundChannel.Body, "weapons/rocket_det.wav");
    }

    // METHOD(Devastator, wr_resetplayer) — common/weapons/weapon/devastator.qc:wr_resetplayer ("no revenge from
    // the grave"): on (re)spawn / round reset, clear this slot's still-live rocket reference and re-arm the
    // rl_release latch so the first primary press after respawn launches (instead of trying to guide a rocket
    // that belonged to a previous life). QC clears lastrocket + rl_release on the weaponentity; the port dispatches
    // WrResetPlayer per populated slot (SpawnSystem), so the per-slot clear here is the full reset. (Explode/
    // DoRemoteExplode already call Unregister per-rocket, and OnThink's owner-alive gate stops a dead owner's
    // guiding — this adds the proactive respawn-time clear QC does.)
    public override void WrResetPlayer(Entity actor, WeaponSlot slot)
    {
        WeaponSlotState st = actor.WeaponState(slot);
        st.LastRocket = null;
        st.RlRelease = true;
    }

    // METHOD(Devastator, wr_checkammo1) — devastator.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // --- bot aim / auto-detonation (METHOD(Devastator, wr_aim) — devastator.qc:339-451) ----------------------

    // QC devastator.qc:351-355: "simulate rocket guide by calculating rocket trajectory with higher speed —
    // 20 times faster at 90 degrees guide rate." Lead the target as if the rocket flew this much faster, so the
    // bot fires ahead the way a guided rocket can actually curve onto a moving target. The defaultSpeed the brain
    // hands us is ignored (QC uses WEP_CVAR speed, not the brain's projectile speed). 9.489 ~= sqrt(90).
    public override float BotAimShotSpeed(float defaultSpeed)
    {
        float spd = Cvars.Speed;
        if (Cvars.GuideRate > 0f)
            spd *= MathF.Sqrt(Cvars.GuideRate) * (20f / 9.489f);
        return spd;
    }

    // QC devastator.qc:356-357: bots needn't fire with high accuracy at long range when the rocket can be guided.
    public override bool? BotAimAccurate() => Cvars.GuideRate < 50f;

    // QC devastator.qc:341-348 + 360-450: decide whether to remote-detonate the bot's in-flight rockets.
    public override bool BotWantsDetonate(
        Entity actor, WeaponSlot slot, float skill,
        System.Collections.Generic.IEnumerable<Entity> targets,
        System.Func<Entity, Entity, bool> shouldAttack)
    {
        if (Api.Services is null) return false;
        // QC skill 0/1 bots won't detonate rockets at all.
        if (skill < 2f) return false;

        // Gather this actor's live rockets once (QC IL_EACH(g_projectiles, realowner == actor && "rocket")).
        // We must materialize because both the fire-again gate and the damage loop iterate them.
        var rockets = new System.Collections.Generic.List<Entity>();
        foreach (Entity r in Api.Entities.FindByClass("rocket"))
        {
            if (r.IsFreed || !ReferenceEquals(r.Owner, actor) || r.NetName != NetName) continue;
            rockets.Add(r);
        }
        if (rockets.Count == 0) return false;

        // QC pred_time = bound(0.02, 0.02 + (8 - skill) * 0.01, 0.1): a short skill-scaled look-ahead.
        float predTime = QMath.Bound(0.02f, 0.02f + (8f - skill) * 0.01f, 0.1f);

        float edgedamage = Cvars.EdgeDamage;
        float coredamage = Cvars.Damage;
        float edgeradius = Cvars.Radius;

        float selfdamage = 0f, teamdamage = 0f, enemydamage = 0f;
        float predSelfdamage = 0f, predTeamdamage = 0f, predEnemydamage = 0f;

        var strength = StatusEffectsCatalog.ByName("strength");
        var shield = StatusEffectsCatalog.ByName("shield");
        float strengthMul = Api.Cvars.GetFloat("g_balance_powerup_strength_damage");
        float invincibleMul = Api.Cvars.GetFloat("g_balance_powerup_invincible_takedamage");

        foreach (Entity rocket in rockets)
        {
            foreach (Entity it in targets)
            {
                if (it.IsFreed) continue;
                // QC g_bot_targets membership ~= a damageable bot target; the same-self / same-team / enemy
                // classification below mirrors the QC branch order (self -> team -> bot_shouldattack enemy).
                bool isSelf = ReferenceEquals(it, actor);
                bool isTeam = !isSelf && Teams.SameTeam(it, actor);
                bool isEnemy = !isSelf && !isTeam && shouldAttack(actor, it);
                if (!isSelf && !isTeam && !isEnemy) continue;

                // QC: target_pos = origin + (maxs - mins) * 0.5 (the box centre offset, NOT the true centre —
                // faithful to the QC which adds the size, matching its bot damage estimate).
                Vector3 targetPos = it.Origin + (it.Maxs - it.Mins) * 0.5f;

                float dist = (targetPos - rocket.Origin).Length();
                float dmg = 0f;
                if (dist <= edgeradius)
                {
                    float f = edgeradius > 0f ? MathF.Max(0f, 1f - dist / edgeradius) : 1f;
                    dmg = coredamage * f + edgedamage * (1f - f);
                }

                float predDist = (targetPos + it.Velocity * predTime
                    - (rocket.Origin + rocket.Velocity * predTime)).Length();
                float predDmg = 0f;
                if (predDist <= edgeradius)
                {
                    float f = edgeradius > 0f ? MathF.Max(0f, 1f - predDist / edgeradius) : 1f;
                    predDmg = coredamage * f + edgedamage * (1f - f);
                }

                if (isSelf)
                {
                    // QC: strength boosts OWN damage dealt; shield reduces damage taken.
                    if (strength is not null && StatusEffectsCatalog.Has(it, strength)) dmg *= strengthMul;
                    if (shield is not null && StatusEffectsCatalog.Has(it, shield)) dmg *= invincibleMul;
                    selfdamage += dmg;
                    predSelfdamage += predDmg;
                }
                else if (isTeam)
                {
                    if (shield is not null && StatusEffectsCatalog.Has(it, shield)) dmg *= invincibleMul;
                    teamdamage += dmg;
                    predTeamdamage += predDmg;
                }
                else // enemy
                {
                    if (shield is not null && StatusEffectsCatalog.Has(it, shield)) dmg *= invincibleMul;
                    enemydamage += dmg;
                    predEnemydamage += predDmg;
                }
            }
        }

        // QC devastator.qc:423-432: self-damage scaled by g_balance_selfdamagepercent; if the SHOOTER has
        // Strength, all of its (team/enemy) damage dealt is boosted.
        float selfdamagepercent = Api.Cvars.GetFloat("g_balance_selfdamagepercent");
        selfdamage *= selfdamagepercent;
        predSelfdamage *= selfdamagepercent;
        if (strength is not null && StatusEffectsCatalog.Has(actor, strength))
        {
            teamdamage *= strengthMul;
            predTeamdamage *= strengthMul;
            enemydamage *= strengthMul;
            predEnemydamage *= strengthMul;
        }

        float goodDamage = enemydamage;
        float predGoodDamage = predEnemydamage;
        float badDamage = selfdamage + teamdamage;
        float predBadDamage = predSelfdamage + predTeamdamage;

        // QC devastator.qc:441-442: detonate if the predicted good damage is about to drop (current good damage is
        // the maximum) or the predicted bad damage is getting too high, AND the current good damage is worthwhile
        // and outweighs the bad by 1.5x.
        bool detonate = goodDamage > coredamage * 0.1f && goodDamage > badDamage * 1.5f
            && (predGoodDamage < goodDamage + 2f || predGoodDamage < predBadDamage * 1.5f);

        // QC devastator.qc:444-445: a skill >= 7 bot won't detonate a rocket that would kill itself.
        if (skill >= 7f && selfdamage > actor.GetResource(ResourceType.Health))
            detonate = false;

        return detonate;
    }
}
