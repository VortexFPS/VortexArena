using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The HLAC (Heavy Laser Assault Cannon) — port of common/weapons/weapon/hlac.{qh,qc}. A splash weapon
/// firing fast-moving energy bolts (like the Blaster's, but consuming cells). Primary rapid-fires single
/// bolts whose spread grows the longer fire is held; secondary fires a burst of <c>shots</c> randomly
/// scattered bolts at once. Each bolt flies straight (MOVETYPE_FLY) and bursts with radius damage on
/// contact or at end of lifetime.
///
/// Identity/attributes from hlac.qh; balance from bal-wep-xonotic.cfg (g_balance_hlac_*).
/// This port covers the primary single bolt with held-fire spread accumulation (misc_bulletcounter ->
/// spread_add, capped at spread_max), the random per-bolt secondary burst, the crouch spread modifier
/// (spread *= spread_crouchmod when ducked AND grounded), recoil punchangle, and the splash damage.
/// </summary>
[Weapon]
public sealed class Hlac : Weapon
{
    /// <summary>Primary-fire balance — QC WEP_CVAR_PRI(WEP_HLAC, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;            // g_balance_hlac_primary_ammo (cells per bolt)
        public float Animtime;        // g_balance_hlac_primary_animtime
        public float Damage;          // g_balance_hlac_primary_damage
        public float EdgeDamage;      // g_balance_hlac_primary_edgedamage
        public float Force;           // g_balance_hlac_primary_force
        public float Lifetime;        // g_balance_hlac_primary_lifetime
        public float Radius;          // g_balance_hlac_primary_radius
        public float Refire;          // g_balance_hlac_primary_refire
        public float Speed;           // g_balance_hlac_primary_speed
        public float SpreadAdd;       // g_balance_hlac_primary_spread_add (per held shot)
        public float SpreadCrouchmod; // g_balance_hlac_primary_spread_crouchmod
        public float SpreadMax;       // g_balance_hlac_primary_spread_max
        public float SpreadMin;       // g_balance_hlac_primary_spread_min
    }

    /// <summary>Secondary-fire (burst) balance — QC WEP_CVAR_SEC(WEP_HLAC, *).</summary>
    public struct SecondaryBalance
    {
        public float Ammo;            // g_balance_hlac_secondary_ammo (cells for the whole burst)
        public float Animtime;        // g_balance_hlac_secondary_animtime
        public float Damage;          // g_balance_hlac_secondary_damage
        public float EdgeDamage;      // g_balance_hlac_secondary_edgedamage
        public float Force;           // g_balance_hlac_secondary_force
        public float Lifetime;        // g_balance_hlac_secondary_lifetime
        public float Radius;          // g_balance_hlac_secondary_radius
        public float Refire;          // g_balance_hlac_secondary_refire
        public int   Shots;           // g_balance_hlac_secondary_shots
        public float Speed;           // g_balance_hlac_secondary_speed
        public float Spread;          // g_balance_hlac_secondary_spread
        public float SpreadCrouchmod; // g_balance_hlac_secondary_spread_crouchmod
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    /// <summary>g_balance_hlac_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;


    public Hlac()
    {
        NetName = "hlac";
        BotPickupBaseValue = 4000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Heavy Laser Assault Cannon";
        Impulse = 6;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.506f, 0.945f, 0.239f);
        ViewModel = "h_hlac.iqm";  // MDL_HLAC_VIEW
        WorldModel = "v_hlac.md3"; // MDL_HLAC_WORLD
        ItemModel = "g_hlac.md3";  // MDL_HLAC_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_hlac_primary_ammo", 1f);
        Primary.Animtime = Bal("g_balance_hlac_primary_animtime", 0.03125f);
        Primary.Damage = Bal("g_balance_hlac_primary_damage", 10f);
        Primary.EdgeDamage = Bal("g_balance_hlac_primary_edgedamage", 5f);
        Primary.Force = Bal("g_balance_hlac_primary_force", 15f);
        Primary.Lifetime = Bal("g_balance_hlac_primary_lifetime", 5f);
        Primary.Radius = Bal("g_balance_hlac_primary_radius", 60f);
        Primary.Refire = Bal("g_balance_hlac_primary_refire", 0.078125f);
        Primary.Speed = Bal("g_balance_hlac_primary_speed", 6000f);
        Primary.SpreadAdd = Bal("g_balance_hlac_primary_spread_add", 0.001953125f);
        Primary.SpreadCrouchmod = Bal("g_balance_hlac_primary_spread_crouchmod", 0.25f);
        Primary.SpreadMax = Bal("g_balance_hlac_primary_spread_max", 0.03125f);
        Primary.SpreadMin = Bal("g_balance_hlac_primary_spread_min", 0f);

        Secondary.Ammo = Bal("g_balance_hlac_secondary_ammo", 6f);
        Secondary.Animtime = Bal("g_balance_hlac_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_hlac_secondary_damage", 15f);
        Secondary.EdgeDamage = Bal("g_balance_hlac_secondary_edgedamage", 7.5f);
        Secondary.Force = Bal("g_balance_hlac_secondary_force", 30f);
        Secondary.Lifetime = Bal("g_balance_hlac_secondary_lifetime", 5f);
        Secondary.Radius = Bal("g_balance_hlac_secondary_radius", 60f);
        Secondary.Refire = Bal("g_balance_hlac_secondary_refire", 1f);
        Secondary.Shots = BalInt("g_balance_hlac_secondary_shots", 6);
        Secondary.Speed = Bal("g_balance_hlac_secondary_speed", 6000f);
        Secondary.Spread = Bal("g_balance_hlac_secondary_spread", 0.15f);
        Secondary.SpreadCrouchmod = Bal("g_balance_hlac_secondary_spread_crouchmod", 0.5f);

        SecondaryEnabled = BalBool("g_balance_hlac_secondary", true);
    }

    // METHOD(HLAC, wr_think) — common/weapons/weapon/hlac.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // QC hlac.qc:163-167 forced reload: if reloading is enabled (reload_ammo != 0) and the clip has dropped
        // below the cheapest per-shot cost, reload before doing anything else. ReloadingAmmo() resolves
        // g_balance_hlac_reload_ammo (default 0); since HLAC ships un-clipped, this branch is dormant at default.
        if (ReloadingAmmo() != 0f && st.ClipLoad < MathF.Min(Primary.Ammo, Secondary.Ammo))
        {
            WrReload(actor, slot);
            return;
        }

        if (fire == FireMode.Primary)
        {
            // QC wr_think (hlac.qc:169-176): on a FRESH primary press (weapon_prepareattack succeeds only
            // from the READY state), reset misc_bulletcounter = 0 so the first bolt of the trigger pull is
            // dead-accurate, fire once, then hand off to the self-rescheduling W_HLAC_Attack_Frame loop which
            // (while ATCK stays held) re-fires and ++misc_bulletcounter each refire so spread grows. Releasing
            // primary ends the loop; the next fresh press resets again — the weapon's feather-the-trigger
            // mechanic. (Mirrors OkMachinegun's auto-fire: reset in wr_think, accumulate in the think loop.)
            if (PrepareAttack(actor, slot, fire))
            {
                st.MiscBulletCounter = 0;
                Attack(actor, slot, st);
                ScheduleAttackFrame(actor, slot);
            }
        }
        else if (fire == FireMode.Secondary && SecondaryEnabled)
        {
            if (PrepareAttack(actor, slot, fire))
            {
                st.MiscBulletCounter = 0; // pressing secondary breaks the primary hold streak
                Attack2(actor, slot);
            }
        }
    }

    // W_HLAC_Attack_Frame (hlac.qc:129-154) — the held-fire self-reschedule loop. While the primary button
    // stays held (and ammo allows), it re-fires every refire and increments misc_bulletcounter so spread
    // climbs toward spread_max; the moment the button releases it settles back to READY (no counter reset
    // here — that only happens on the NEXT fresh wr_think press), so re-tapping restores accuracy.
    private void ScheduleAttackFrame(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        float rate = WeaponRateFactor(actor);
        st.AttackFinished = Api.Clock.Time + Primary.Refire * rate;
        WeaponFireDriver.ScheduleThink(st, Primary.Refire * rate, (pl, sl) =>
        {
            WeaponSlotState s2 = pl.WeaponState(sl);
            if (s2.State != WeaponFireState.InUse) return;
            // QC re-enters W_HLAC_Attack only while ATCK is held and ammo (or unlimited) is available
            // (W_HLAC_Attack_Frame's wr_checkammo1 / IT_UNLIMITED_AMMO guard, hlac.qc:139-145).
            bool unlimited = pl.UnlimitedAmmo || (pl.Items & (1 << 0)) != 0; // IT_UNLIMITED_AMMO
            if (s2.ButtonAttack && (pl.GetResource(AmmoType) >= Primary.Ammo || unlimited))
            {
                ++s2.MiscBulletCounter;
                Attack(pl, sl, s2);
                ScheduleAttackFrame(pl, sl);
            }
            else
            {
                s2.State = WeaponFireState.Ready;
            }
        });
    }

    // METHOD(Hlac, wr_aim) — common/weapons/weapon/hlac.qc:156-159:wr_aim. QC bot_aim only presses the primary
    // (PHYS_INPUT_BUTTON_ATCK); it never alt-fires, so the bot always uses the rapid primary. QC leads at the
    // projectile speed (primary == secondary == 6000), so this hook just routes the button (always primary).
    // Non-lobbed, so the straight-line lead applies.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
        => false; // QC wr_aim (hlac.qc:156-159) only presses primary; no secondary alt-fire in Base

    // Both HLAC modes launch at the primary projectile speed (matching the QC bot_aim call).
    public override float BotAimShotSpeed(float defaultSpeed) => Primary.Speed;

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // QC wr_reload (hlac.qc:202-205): W_Reload(actor, weaponentity, min(WEP_CVAR_PRI(ammo), WEP_CVAR_SEC(ammo)),
    // SND_RELOAD) — the reload's per-shot ammo floor is the cheaper of the two modes' costs, not the generic 1.
    protected override float ReloadingAmmoMin() => MathF.Min(Primary.Ammo, Secondary.Ammo);

    /// <summary>
    /// QC IS_DUCKED(actor) &amp;&amp; IS_ONGROUND(actor) → spread *= spread_crouchmod (hlac.qc:33-34 primary,
    /// 79-80 secondary). Mirrors Machinegun.CrouchSpreadMod (machinegun.cs ~261-262); takes the per-mode
    /// crouchmod (Primary.SpreadCrouchmod / Secondary.SpreadCrouchmod) so one helper serves both call sites.
    /// </summary>
    private static float CrouchSpreadMod(Entity actor, float crouchmod)
        => (actor.IsDucked && actor.OnGround) ? crouchmod : 1f;

    // W_HLAC_Attack — one rapid bolt; spread grows with the held-fire counter. hlac.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        actor.TakeResource(AmmoType, Primary.Ammo);

        // spread = spread_min + spread_add*misc_bulletcounter, capped at spread_max.
        float spread = MathF.Min(Primary.SpreadMin + Primary.SpreadAdd * st.MiscBulletCounter, Primary.SpreadMax);
        // QC hlac.qc:33-34 — if (IS_DUCKED && IS_ONGROUND) spread *= spread_crouchmod, AFTER all other
        // spread calc and before projectile setup.
        spread *= CrouchSpreadMod(actor, Primary.SpreadCrouchmod);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);
        Recoil(actor);

        SpawnBolt(actor, shot.Origin, shot.Dir, Primary.Speed, Primary.Lifetime,
            Primary.Damage, Primary.EdgeDamage, Primary.Radius, Primary.Force, spread, isSecondary: false);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/lasergun_fire.wav");
        EmitMuzzleFlash(shot.Origin, shot.Dir * 1000f, actor);
    }

    // W_HLAC_Attack2 — fire a burst of `shots` randomly-scattered bolts at once. hlac.qc
    private void Attack2(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Secondary.Ammo);

        // spread = WEP_CVAR_SEC(spread); QC hlac.qc:79-80 — if (IS_DUCKED && IS_ONGROUND) spread *=
        // spread_crouchmod, before the per-bolt projectile loop (the gate is actor-state, not per-bolt).
        float spread = Secondary.Spread;
        spread *= CrouchSpreadMod(actor, Secondary.SpreadCrouchmod);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);
        Recoil(actor);

        int shots = Secondary.Shots;
        if (shots < 1) shots = 1;
        for (int j = 0; j < shots; ++j)
        {
            // Each bolt is an independent W_SetupProjVelocity_Basic with the (crouch-adjusted) secondary spread.
            SpawnBolt(actor, shot.Origin, shot.Dir, Secondary.Speed, Secondary.Lifetime,
                Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius, Secondary.Force, spread, isSecondary: true);
        }

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/lasergun_fire.wav");
        EmitMuzzleFlash(shot.Origin, shot.Dir * 1000f, actor);
    }

    /// <summary>Spawn an HLAC laser bolt that bursts (radius damage) on touch or lifetime. hlac.qc.</summary>
    private void SpawnBolt(Entity actor, Vector3 origin, Vector3 dir, float speed, float lifetime,
        float damage, float edge, float radius, float force, float spread, bool isSecondary)
    {
        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "hlacbolt";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(missile, origin);

        // W_SetupProjVelocity_Basic(missile, speed, spread) — random per-bolt spread (deterministic PRNG).
        missile.Velocity = WeaponFiring.ProjectileVelocity(dir, Vector3.UnitZ, speed, 0f, 0f, spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC hlac.qc:46-48,91-93 — flag the bolt as a dodgeable hazard (rating = this mode's damage).
        missile.BotDodge = true;
        missile.BotDodgeRating = damage;

        int deathType = RegistryId;
        // QC hlac.qc:113 — the secondary burst ORs HITTYPE_SECONDARY into projectiledeathtype (W_HLAC_Attack
        // leaves the plain weapon id). The int path can't pack the bit, so carry it as a string deathTag for
        // secondary bolts; primary keeps the legacy int RegistryId path (resolved damage is identical either way).
        string? deathTag = isSecondary
            ? DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Secondary)
            : null;
        missile.Touch = (self, other) => Explode(self, damage, edge, radius, force, deathType, deathTag);
        missile.Think = self => Api.Entities.Remove(self); // SUB_Remove at lifetime
        missile.NextThink = Api.Clock.Time + lifetime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) — fired per bolt (hlac.qc W_HLAC_Attack / Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);
    }

    // QC recoil: punchangle gets a small random kick on each shot, gated by !autocvar_g_norecoil
    // (hlac.qc:38-42 primary, 121-125 secondary). With g_norecoil 1 the kick is suppressed entirely.
    private static void Recoil(Entity actor)
    {
        if (Api.Services is not null && Api.Cvars.GetFloat("g_norecoil") != 0f)
            return;
        Vector3 p = actor.PunchAngle;
        p.X = Prandom.Float() - 0.5f;
        p.Y = Prandom.Float() - 0.5f;
        actor.PunchAngle = p;
    }

    // W_HLAC_Touch — radius damage + knockback at the impact point, then remove. hlac.qc
    private void Explode(Entity self, float damage, float edge, float radius, float force, int deathType,
        string? deathTag)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        WeaponSplash.RadiusDamage(self, self.Origin, damage, edge, radius, self.Owner, deathType, force,
            deathTag: deathTag);

        WeaponSplash.ImpactSound(self, "weapons/laserimpact.wav"); // QC SND_LASERIMPACT (wr_impacteffect)
        // QC: pointparticles(eff, org2, w_backoff * 1000, 1) — the impact sprays back out of the surface
        // along w_backoff (the impact plane normal). The bolt flew INTO the wall, so the reversed flight
        // direction is the faithful w_backoff fallback (same reconstruction as Blaster.Explode).
        Vector3 backoff = self.Velocity.LengthSquared() > 1e-6f ? -QMath.Normalize(self.Velocity) : Vector3.Zero;
        EmitImpactEffect(self.Origin, backoff * 1000f);
        Api.Entities.Remove(self);
    }

    /// <summary>
    /// Emit muzzle flash with runtime green->blaster fallback (hlac.qc:m_muzzleeffect,
    /// hlac.qc:225-228). If GREEN_HLAC_MUZZLEFLASH is not registered (v0.8.6 compat), falls back
    /// to BLASTER_MUZZLEFLASH. Both effects are pre-registered in the port, but this maintains
    /// Base parity for a hypothetical missing-asset build.
    /// </summary>
    private static void EmitMuzzleFlash(Vector3 origin, Vector3 velocity, Entity except)
    {
        var effect = Effects.ByName("GREEN_HLAC_MUZZLEFLASH");
        if (effect == null)
            effect = Effects.ByName("BLASTER_MUZZLEFLASH"); // v0.8.6 compat fallback
        EffectEmitter.Emit(effect, origin, velocity, 1, except);
    }

    /// <summary>
    /// Emit impact effect with runtime green->blaster fallback (hlac.qc:wr_impacteffect,
    /// hlac.qc:224-228). If GREEN_HLAC_IMPACT is not registered (v0.8.6 compat), falls back to
    /// BLASTER_IMPACT. Both effects are pre-registered in the port, but this maintains Base
    /// parity for a hypothetical missing-asset build.
    /// </summary>
    private static void EmitImpactEffect(Vector3 origin, Vector3 velocity)
    {
        var effect = Effects.ByName("GREEN_HLAC_IMPACT");
        if (effect == null)
            effect = Effects.ByName("BLASTER_IMPACT"); // v0.8.6 compat fallback
        EffectEmitter.Emit(effect, origin, velocity);
    }

    // METHOD(HLAC, wr_checkammo1) — hlac.qc:188-192: have ammo if the shared pool OR the persistent magazine
    // (weapon_load[id], reloadable) can cover one primary shot. Returns true if reserve >= ammo OR clip >= ammo.
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= Primary.Ammo
        || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Primary.Ammo;

    // METHOD(HLAC, wr_checkammo2) — hlac.qc:195-199: pool OR magazine, for the secondary shot cost.
    public bool CheckAmmoSecondary(Entity actor)
        => actor.GetResource(AmmoType) >= Secondary.Ammo
        || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Secondary.Ammo;
}
