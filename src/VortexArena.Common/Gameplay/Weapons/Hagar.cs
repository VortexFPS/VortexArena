using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Hagar — port of common/weapons/weapon/hagar.{qh,qc}. A splash weapon firing small rapid rockets.
/// Primary rapid-fires straight rockets (MOVETYPE_FLY) that burst on impact or at end of lifetime.
/// Secondary is a "load" mode: hold to charge up to load_max rockets, release to fire them all at once in
/// a spread. Every rocket is shootable.
///
/// Identity/attributes from hagar.qh; balance from bal-wep-xonotic.cfg (g_balance_hagar_*).
/// This port covers the primary rapid rocket, the loaded secondary salvo (correct load_spread fan +
/// per-shot bias spread), the non-loaded single bouncing secondary, the splash damage, and shoot-down. The
/// incremental hold-to-load state machine (load one rocket per <c>load_speed</c> tick while ATCK2 is held,
/// per-rocket load tick + last-rocket/abort/warning beeps, auto-release after <c>load_hold</c>, abort-on-primary
/// give-back, and the loadblock must-release gate) is ported faithfully in <see cref="AttackLoad"/>, driven each
/// frame from <see cref="WrThink"/> via the driver's per-tick held-button state (QC W_Hagar_Attack2_Load "must
/// always run each frame"). The loaded-rocket count is published to the crosshair load ring through
/// <c>WeaponSlotState.HagarLoad</c>.
/// </summary>
[Weapon]
public sealed class Hagar : Weapon
{
    /// <summary>Primary-fire balance — QC WEP_CVAR_PRI(WEP_HAGAR, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;        // g_balance_hagar_primary_ammo (rockets)
        public float Damage;      // g_balance_hagar_primary_damage
        public float EdgeDamage;  // g_balance_hagar_primary_edgedamage
        public float Force;       // g_balance_hagar_primary_force
        public float Health;      // g_balance_hagar_primary_health (shootable rocket hp)
        public float Lifetime;    // g_balance_hagar_primary_lifetime
        public float Radius;      // g_balance_hagar_primary_radius
        public float Refire;      // g_balance_hagar_primary_refire
        public float Speed;       // g_balance_hagar_primary_speed
    }

    /// <summary>Secondary-fire (load) balance — QC WEP_CVAR_SEC(WEP_HAGAR, *).</summary>
    public struct SecondaryBalance
    {
        public float Ammo;            // g_balance_hagar_secondary_ammo (per loaded rocket)
        public float Damage;          // g_balance_hagar_secondary_damage
        public float EdgeDamage;      // g_balance_hagar_secondary_edgedamage
        public float Force;           // g_balance_hagar_secondary_force
        public float Health;          // g_balance_hagar_secondary_health
        public float LifetimeMin;     // g_balance_hagar_secondary_lifetime_min
        public float LifetimeRand;    // g_balance_hagar_secondary_lifetime_rand
        public float LoadMax;         // g_balance_hagar_secondary_load_max (rockets per full load)
        public float LoadSpeed;       // g_balance_hagar_secondary_load_speed (seconds per loaded rocket)
        public float LoadHold;        // g_balance_hagar_secondary_load_hold (auto-release hold time; <0 = never)
        public bool  LoadAbort;       // g_balance_hagar_secondary_load_abort (primary press unloads + aborts)
        public float LoadSpread;      // g_balance_hagar_secondary_load_spread
        public float LoadSpreadBias;  // g_balance_hagar_secondary_load_spread_bias
        public float Radius;          // g_balance_hagar_secondary_radius
        public float Refire;          // g_balance_hagar_secondary_refire
        public float Speed;           // g_balance_hagar_secondary_speed
        public float Spread;          // g_balance_hagar_secondary_spread
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    /// <summary>g_balance_hagar_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;

    /// <summary>g_balance_hagar_secondary_load — whether the secondary is the loadable burst (vs a single bouncing rocket).</summary>
    public bool SecondaryLoad = true;

    /// <summary>QC <c>IT_UNLIMITED_AMMO</c> = BIT(0) (common/items/item.qh).</summary>
    private const int ItUnlimitedAmmo = 1 << 0;


    public Hagar()
    {
        NetName = "hagar";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Hagar";
        Impulse = 8;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.CanClimb | WeaponFlags.TypeSplash;
        Color = new Vector3(0.886f, 0.545f, 0.345f);
        ViewModel = "h_hagar.iqm";  // MDL_HAGAR_VIEW
        WorldModel = "v_hagar.md3"; // MDL_HAGAR_WORLD
        ItemModel = "g_hagar.md3";  // MDL_HAGAR_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_hagar_primary_ammo", 1f);
        Primary.Damage = Bal("g_balance_hagar_primary_damage", 25f);
        Primary.EdgeDamage = Bal("g_balance_hagar_primary_edgedamage", 12.5f);
        Primary.Force = Bal("g_balance_hagar_primary_force", 100f);
        Primary.Health = Bal("g_balance_hagar_primary_health", 15f);
        Primary.Lifetime = Bal("g_balance_hagar_primary_lifetime", 5f);
        Primary.Radius = Bal("g_balance_hagar_primary_radius", 65f);
        Primary.Refire = Bal("g_balance_hagar_primary_refire", 0.16667f);
        Primary.Speed = Bal("g_balance_hagar_primary_speed", 2200f);

        Secondary.Ammo = Bal("g_balance_hagar_secondary_ammo", 1f);
        Secondary.Damage = Bal("g_balance_hagar_secondary_damage", 35f);
        Secondary.EdgeDamage = Bal("g_balance_hagar_secondary_edgedamage", 17.5f);
        Secondary.Force = Bal("g_balance_hagar_secondary_force", 75f);
        Secondary.Health = Bal("g_balance_hagar_secondary_health", 15f);
        Secondary.LifetimeMin = Bal("g_balance_hagar_secondary_lifetime_min", 10f);
        Secondary.LifetimeRand = Bal("g_balance_hagar_secondary_lifetime_rand", 0f);
        Secondary.LoadMax = Bal("g_balance_hagar_secondary_load_max", 4f);
        Secondary.LoadSpeed = Bal("g_balance_hagar_secondary_load_speed", 0.5f);
        Secondary.LoadHold = Bal("g_balance_hagar_secondary_load_hold", 4f);
        Secondary.LoadAbort = BalBool("g_balance_hagar_secondary_load_abort", true);
        Secondary.LoadSpread = Bal("g_balance_hagar_secondary_load_spread", 0.075f);
        Secondary.LoadSpreadBias = Bal("g_balance_hagar_secondary_load_spread_bias", 0.5f);
        Secondary.Radius = Bal("g_balance_hagar_secondary_radius", 80f);
        Secondary.Refire = Bal("g_balance_hagar_secondary_refire", 0.5f);
        Secondary.Speed = Bal("g_balance_hagar_secondary_speed", 2000f);
        Secondary.Spread = Bal("g_balance_hagar_secondary_spread", 0f);

        SecondaryEnabled = BalBool("g_balance_hagar_secondary", true);
        SecondaryLoad = BalBool("g_balance_hagar_secondary_load", true);
    }

    // METHOD(Hagar, wr_think) — common/weapons/weapon/hagar.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        bool loadableSecondary = SecondaryLoad && SecondaryEnabled;

        if (fire == FireMode.Primary)
        {
            // QC wr_think: W_Hagar_Attack2_Load(...) "must always run each frame". The driver calls
            // WrThink(Primary) every tick (the per-tick upkeep path) with the held buttons recorded on the
            // slot state, so the loadable-secondary charge machine runs here — it reads ATCK/ATCK2 itself.
            if (loadableSecondary)
                AttackLoad(actor, slot);

            // QC: the primary only fires while NOT loading / awaiting reset (the hagar_load / hagar_loadblock
            // gate), so a charged secondary can't be interrupted by a primary tap (which instead aborts).
            WeaponSlotState pst = actor.WeaponState(slot);
            if (pst.HagarLoad == 0 && !pst.HagarLoadBlock)
            {
                // W_Hagar_Attack_Auto: one rocket per refire tick (QC weapon_prepareattack, primary refire).
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot);
            }
        }
        else if (fire == FireMode.Secondary && SecondaryEnabled && !loadableSecondary)
        {
            // W_Hagar_Attack2: a single bouncing rocket (used when g_balance_hagar_secondary_load is off).
            // QC routes ATCK2 here only when the secondary is NOT the loadable variant; otherwise the charge
            // machine (above, on the upkeep path) owns the alt-fire button.
            if (PrepareAttack(actor, slot, fire))
                AttackBounce(actor, slot);
        }
    }

    // METHOD(Hagar, wr_aim) — hagar.qc:wr_aim. random()>0.15 → primary (the rapid rocket); else (15%) the
    // secondary burst. QC leads BOTH branches at the PRIMARY speed (it deliberately reuses primary_speed for
    // the secondary "since these are only 15% and should cause some ricochets without re-aiming"). Non-lobbed
    // (bot_aim grav=false), so the brain's straight-line lead applies. This hook owns only the button pick.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
        => ctx.Random01 <= 0.15f;

    // Both modes lead at the primary projectile speed (matching the QC bot_aim call).
    public override float BotAimShotSpeed(float defaultSpeed) => Primary.Speed;

    // Refire from the (cvar-seeded) balance blocks; the Hagar has no separate animtime cvar, so the refire
    // doubles as the fire-anim length (return 0 animtime so only the refire timer gates).
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => 0f;

    // W_Hagar_Attack — a single straight rapid rocket that bursts on impact. hagar.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Primary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 2f);

        Entity missile = SpawnRocket(actor, shot.Origin, "missile", Primary.Health, MoveType.Fly);
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Primary.Speed);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC hagar.qc:109-110 — flag the rocket as a dodgeable hazard (rating = primary damage).
        missile.BotDodge = true;
        missile.BotDodgeRating = Primary.Damage;

        float deathTime = Api.Clock.Time + Primary.Lifetime;
        missile.Touch = (self, other) => Explode(self, Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force);
        missile.Think = self => Explode(self, Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force);
        missile.NextThink = deathTime;
        // W_Hagar_Damage: shot down -> burst. Projectiles.MakeShootable installs the QC W_CheckProjectileDamage
        // gate + RES_HEALTH subtraction and only invokes this callback once hp hits 0 (W_PrepareExplosionByDamage),
        // so a non-lethal graze no longer detonates the rocket. exception=1: the Hagar is combo-able regardless of
        // g_projectiles_damage (the rocket can be shot down even under the stock -2 ladder).
        missile.ProjectileDamage = (self, attacker) => Explode(self, Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force);
        Projectiles.MakeShootable(missile, exception: 1f);

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (hagar.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_fire.wav");
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Hagar_Attack2 — a single bouncing rocket (the non-loaded secondary). hagar.qc
    private void AttackBounce(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Secondary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 2f);

        Entity missile = SpawnRocket(actor, shot.Origin, "missile", Secondary.Health, MoveType.BounceMissile);
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Secondary.Speed, 0f, 0f, Secondary.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);
        missile.Count = 0; // bounce counter

        // QC hagar.qc:154-155 — flag the secondary rocket as a dodgeable hazard (rating = secondary damage).
        missile.BotDodge = true;
        missile.BotDodgeRating = Secondary.Damage;

        // QC missile.projectiledeathtype = m_id | HITTYPE_SECONDARY (a bounced rocket later ORs HITTYPE_BOUNCE).
        string secDeath = Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Secondary);

        float deathTime = Api.Clock.Time + Secondary.LifetimeMin + Prandom.Float() * Secondary.LifetimeRand;
        missile.Touch = (self, other) => BounceTouch(self, other);
        missile.Think = self => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, BounceDeath(self, secDeath));
        missile.NextThink = deathTime;
        // W_Hagar_Damage shoot-down (same gate as the primary). exception=1: combo-able under g_projectiles_damage.
        missile.ProjectileDamage = (self, attacker) => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, BounceDeath(self, secDeath));
        Projectiles.MakeShootable(missile, exception: 1f);

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (hagar.qc W_Hagar_Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_fire.wav");
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // QC W_Hagar_Touch2 ORs HITTYPE_BOUNCE onto the secondary deathtype once the rocket has bounced (self.Count>0)
    // so the obituary distinguishes a bounce kill (matches Mortar/Crylink/Electro bounce-kill tagging).
    private static string BounceDeath(Entity self, string secDeath)
        => self.Count > 0 ? Damage.DeathTypes.WithHitType(secDeath, Damage.DeathTypes.Bounce) : secDeath;

    // W_Hagar_Touch2 — bounce once then explode on the next contact / on a player. hagar.qc
    private void BounceTouch(Entity self, Entity other)
    {
        // QC missile.projectiledeathtype = m_id | HITTYPE_SECONDARY (+ HITTYPE_BOUNCE once bounced).
        string secDeath = Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Secondary);

        // Base keys ONLY on toucher.takedamage == DAMAGE_AIM (the extra EntFlags.Client OR was a port divergence).
        if (self.Count > 0 || other.TakeDamage == DamageMode.Aim)
        {
            Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, BounceDeath(self, secDeath));
            return;
        }
        ++self.Count; // first bounce: keep going (engine MOVETYPE_BOUNCEMISSILE reflects the velocity)
        // QC Send_Effect(EFFECT_HAGAR_BOUNCE, this.origin, this.velocity, 1) — the bounce spark particle.
        EffectEmitter.Emit("HAGAR_BOUNCE", self.Origin, self.Velocity, 1);
        self.Angles = QMath.VecToAngles(self.Velocity);
        self.Owner = null; // QC this.owner = NULL: a bounced rocket can hurt its firer.
        // QC this.projectiledeathtype |= HITTYPE_BOUNCE — applied at explode time via BounceDeath(self.Count>0).
    }

    // W_Hagar_Attack2_Load — the incremental hold-to-charge state machine (hagar.qc). "Must always run each
    // frame": driven from WrThink(Primary) every tick. Loads one rocket per load_speed while ATCK2 is held (up
    // to load_max), plays the per-rocket load tick + last/abort/warning beeps, auto-releases after load_hold,
    // gives back ammo + aborts on a primary tap (load_abort), and gates re-loading on releasing ATCK2 (loadblock).
    private void AttackLoad(Entity actor, WeaponSlot slot)
    {
        WeaponSlotState st = actor.WeaponState(slot);

        // QC game_starttime / race_penalty / timeout gates: the driver already withholds the fire buttons while
        // the round hasn't started / the player is frozen / weapon-use is forbidden (WeaponFireDriver.Frame), so
        // the reachable gate here is none beyond that — the buttons below carry that state.
        bool atck2 = st.ButtonAttack2;
        bool atck = st.ButtonAttack;

        bool unlimited = actor.UnlimitedAmmo || (actor.Items & ItUnlimitedAmmo) != 0;
        bool loaded = st.HagarLoad >= (int)Secondary.LoadMax;
        // stock g_balance_hagar_reload_ammo=0 → check the live ammo pool (the reload-clip branch is latent here).
        bool enoughAmmo = unlimited || actor.GetResource(AmmoType) >= Secondary.Ammo;
        bool stopped = loaded || !enoughAmmo;

        float rate = WeaponRateFactor(actor);
        float now = Api.Clock.Time;

        if (atck2)
        {
            if (atck && Secondary.LoadAbort)
            {
                if (st.HagarLoad > 0)
                {
                    // QC: pressed primary while loading → unload all rockets, give back the ammo, and abort.
                    st.State = WeaponFireState.Ready;
                    if (!unlimited)
                        actor.GiveResource(AmmoType, Secondary.Ammo * st.HagarLoad); // W_DecreaseAmmo(-ammo*load)
                    st.HagarLoad = 0;
                    Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_beep.wav", SoundLevels.VolBase, SoundLevels.AttenNorm);
                    // pause until we can load again, once the alt-fire button is re-pressed.
                    st.HagarLoadStep = now + Secondary.LoadSpeed * rate;
                    st.HagarLoadBlock = true; // require letting go of ATCK2 before loading again
                }
            }
            else
            {
                // can we load another rocket this tick?
                if (!stopped && !st.HagarLoadBlock && st.HagarLoadStep < now)
                {
                    if (!unlimited)
                        actor.TakeResource(AmmoType, Secondary.Ammo); // W_DecreaseAmmo
                    st.State = WeaponFireState.InUse;
                    ++st.HagarLoad;
                    // SND_HAGAR_LOAD per-rocket tick (VOL_BASE*0.8 — "too loud according to most"), CH_WEAPON_B.
                    Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_load.wav", SoundLevels.VolBase * 0.8f, SoundLevels.AttenNorm);

                    if (st.HagarLoad >= (int)Secondary.LoadMax)
                        stopped = true;
                    else
                        st.HagarLoadStep = now + Secondary.LoadSpeed * rate;
                }
                // last rocket loaded (or can't load more): single beep to notify, then arm the hold timer.
                if (stopped && !st.HagarLoadBeep && st.HagarLoad > 0)
                {
                    Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_beep.wav", SoundLevels.VolBase, SoundLevels.AttenNorm);
                    st.HagarLoadBeep = true;
                    st.HagarLoadStep = now + Secondary.LoadHold * rate;
                }
            }
        }
        else if (st.HagarLoadBlock)
        {
            // ATCK2 released → re-enable loading (the must-release gate is satisfied).
            st.HagarLoadBlock = false;
        }

        if (st.HagarLoad > 0)
        {
            // about-to-auto-release warning beep (load_hold >= 0 → the hold is finite).
            if (stopped && st.HagarLoadStep - 0.5f < now && Secondary.LoadHold >= 0f && !st.HagarWarning)
            {
                Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_beep.wav", SoundLevels.VolBase, SoundLevels.AttenNorm);
                st.HagarWarning = true;
            }

            // release if the player let go of ATCK2, or held it past the auto-release hold time.
            if (!atck2 || (stopped && st.HagarLoadStep < now && Secondary.LoadHold >= 0f))
            {
                st.State = WeaponFireState.Ready;
                AttackLoadRelease(actor, slot);
            }
        }
        else
        {
            st.HagarLoadBeep = false;
            st.HagarWarning = false;
        }
    }

    // W_Hagar_Attack2_Load_Release — release the rockets we've charged, all at once in a spread. hagar.qc
    private void AttackLoadRelease(Entity actor, WeaponSlot slot)
    {
        WeaponSlotState st = actor.WeaponState(slot);

        // QC: if (!hagar_load) return; — nothing to release.
        if (st.HagarLoad <= 0)
            return;

        int shots = st.HagarLoad; // QC: shots = hagar_load (the actual charged count, NOT load_max)

        // QC weapon_prepareattack_do(actor, weaponentity, true, refire): commit the alt-fire — drop the spawn
        // shield, mark WS_INUSE, and advance the shared refire timer (the load already paid the ammo per rocket
        // during charging, so no ammo is taken here).
        st.State = WeaponFireState.InUse;
        if (actor.SpawnShieldExpire > Api.Clock.Time)
            actor.SpawnShieldExpire = 0f;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 2f);

        // QC missile.projectiledeathtype = m_id | HITTYPE_SECONDARY (a burst kill reads WEAPON_HAGAR_MURDER_BURST).
        string secDeath = Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Secondary);

        // per-shot base spread: more shots -> less spread, biased by load_spread_bias.
        float perShot = (Secondary.LoadMax > 1f) ? (shots - 1) / (Secondary.LoadMax - 1f) : 0f;
        perShot = 1f - perShot * Secondary.LoadSpreadBias;
        perShot = Secondary.Spread * perShot;

        for (int i = 0; i < shots; ++i)
        {
            Entity missile = SpawnRocket(actor, shot.Origin, "missile", Secondary.Health, MoveType.Fly);

            // s = W_CalculateSpreadPattern(1, …) * load_spread; dir = w_shotdir + right*s.y + up*s.z, then
            // W_SetupProjVelocity adds the per-shot random jitter (perShot, which shrinks as shots grow).
            Vector3 s = WeaponFiring.CalculateSpreadPattern(i, shots) * (Secondary.LoadSpread * WeaponFiring.WeaponSpreadFactor);
            Vector3 dir = QMath.Normalize(shot.Dir + right * s.Y + up * s.Z);
            missile.Velocity = WeaponFiring.ProjectileVelocity(dir, up, Secondary.Speed, 0f, 0f, perShot);
            missile.Angles = QMath.VecToAngles(missile.Velocity);

            // QC hagar.qc:214-215 — flag each loaded-salvo rocket as a dodgeable hazard (rating = secondary damage).
            missile.BotDodge = true;
            missile.BotDodgeRating = Secondary.Damage;

            float deathTime = Api.Clock.Time + Secondary.LifetimeMin + Prandom.Float() * Secondary.LifetimeRand;
            // QC settouch(missile, W_Hagar_Touch) — "not bouncy": the loaded salvo flies straight and bursts on
            // contact (no bounce), so the deathtype stays HITTYPE_SECONDARY without HITTYPE_BOUNCE.
            missile.Touch = (self, other) => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, secDeath);
            missile.Think = self => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, secDeath);
            missile.NextThink = deathTime;
            // W_Hagar_Damage shoot-down per loaded rocket. exception=1: combo-able under g_projectiles_damage.
            missile.ProjectileDamage = (self, attacker) => Explode(self, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, secDeath);
            Projectiles.MakeShootable(missile, exception: 1f);

            // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — fired per loaded rocket (hagar.qc load-release loop).
            var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
            MutatorHooks.EditProjectile.Call(ref ep);
        }

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/hagar_fire.wav");
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // QC: schedule WFRAME_FIRE2 for load_animtime then w_ready, set hagar_loadstep = time + refire*rate, clear load.
        float rate = WeaponRateFactor(actor);
        WeaponFireDriver.ScheduleThink(st, Secondary.Refire * rate, static (pl, sl) =>
        {
            WeaponSlotState s2 = pl.WeaponState(sl);
            if (s2.State == WeaponFireState.InUse)
                s2.State = WeaponFireState.Ready;
        });
        st.HagarLoadStep = Api.Clock.Time + Secondary.Refire * rate;
        st.HagarLoad = 0;
    }

    /// <summary>Spawn a shootable Hagar rocket with the common fields set (shared by primary + load volley).</summary>
    private Entity SpawnRocket(Entity actor, Vector3 origin, string className, float health, MoveType moveType)
    {
        Entity missile = Api.Entities.Spawn();
        missile.ClassName = className;
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = moveType;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = health;
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(missile, origin);
        return missile;
    }

    // W_Hagar_Explode / Explode2 — radius damage + knockback, then remove. hagar.qc
    // deathTag carries the weapon NetName + HITTYPE bits (HITTYPE_SECONDARY for the loaded burst / bouncing
    // secondary, plus HITTYPE_BOUNCE once a secondary rocket has bounced); it is captured per-projectile when
    // the rocket spawns (QC this.projectiledeathtype) so the central obituary selector
    // (DeathMessages.SelectKillMessage / SelectSuicideMessage) picks WEAPON_HAGAR_MURDER_BURST vs _SPRAY and
    // WEAPON_HAGAR_SUICIDE. A null tag is the primary spray (the int RegistryId path resolves "hagar" + no HITTYPE).
    private void Explode(Entity self, float damage, float edge, float radius, float force, string? deathTag = null)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null; // QC this.event_damage = func_null

        WeaponSplash.RadiusDamage(self, self.Origin, damage, edge, radius, self.Owner, RegistryId, force,
            deathTag: deathTag);

        // QC SND_HAGEXP_RANDOM (wr_impacteffect): one of hagexp1..3.
        WeaponSplash.ImpactSound(self, (SoundVariantGroups.FlacExp()?.Sample ?? "weapons/hagexp1") + ".wav");
        EffectEmitter.Emit("HAGAR_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // METHOD(Hagar, wr_setup) — hagar.qc:432-440
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        // QC: actor.(weaponentity).hagar_loadblock = false; — clear the must-release gate.
        WeaponSlotState st = actor.WeaponState(slot);
        st.HagarLoadBlock = false;
        // QC: if (hagar_load) { W_DecreaseAmmo(..., -ammo*load, ...) → give back ammo }
        if (st.HagarLoad > 0)
        {
            actor.GiveResource(AmmoType, Secondary.Ammo * st.HagarLoad);
            st.HagarLoad = 0;
        }
    }

    // METHOD(Hagar, wr_gonethink) — hagar.qc:422-430
    // Called when the player switches AWAY from the Hagar with rockets loaded: release the charged salvo.
    public override void WrGoneThink(Entity actor, WeaponSlot slot)
    {
        WeaponSlotState st = actor.WeaponState(slot);
        if (st.HagarLoad > 0)
        {
            // QC: actor.(weaponentity).state = WS_READY; W_Hagar_Attack2_Load_Release(...)
            st.State = WeaponFireState.Ready;
            AttackLoadRelease(actor, slot);
        }
    }

    // METHOD(Hagar, wr_playerdeath) — hagar.qc:465-470
    // Called when the player dies with rockets loaded (if load_releasedeath=1): release the charged salvo.
    public override void WrPlayerDeath(Entity actor, WeaponSlot slot)
    {
        // QC: if (load_releasedeath) W_Hagar_Attack2_Load_Release(...) — default load_releasedeath is 0.
        float loadReleaseDeath = Bal("g_balance_hagar_secondary_load_releasedeath", 0f);
        if (loadReleaseDeath != 0f)
        {
            // QC wr_playerdeath calls the release directly (NO state = WS_READY first, unlike wr_gonethink);
            // AttackLoadRelease sets WS_INUSE itself, so no pre-set is needed.
            WeaponSlotState st = actor.WeaponState(slot);
            if (st.HagarLoad > 0)
                AttackLoadRelease(actor, slot);
        }
    }

    // METHOD(Hagar, wr_resetplayer) — hagar.qc:456-463
    // Called when the player is reset (round restart, etc.): clear loaded rockets on this slot.
    public override void WrResetPlayer(Entity actor, WeaponSlot slot)
    {
        // QC: actor.(weaponentity).hagar_load = 0; — clear the load counter on reset.
        WeaponSlotState st = actor.WeaponState(slot);
        st.HagarLoad = 0;
    }

    // METHOD(Hagar, wr_checkammo1) — hagar.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Hagar, wr_checkammo2) — hagar.qc
    public bool CheckAmmoSecondary(Entity actor) => actor.GetResource(AmmoType) >= Secondary.Ammo;
}
