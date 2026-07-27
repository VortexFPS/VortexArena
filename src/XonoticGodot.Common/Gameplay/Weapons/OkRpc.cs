// Port of common/mutators/mutator/overkill/okrpc.{qh,qc}

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill Rocket Propelled Chainsaw — port of common/mutators/mutator/overkill/okrpc.{qh,qc}. A
/// SUPERWEAPON: primary launches an accelerating, shootable missile (health 25, MOVETYPE_FLY) that, on each
/// think, traces forward and — if it passes THROUGH a player — deals the per-pass chainsaw damage
/// (<c>damage2</c> = 500) to them; on contact with the world/an entity (or at end of lifetime) it explodes
/// with the splash <c>damage</c> (150). The secondary is the shared Overkill blaster jump (on
/// <c>actor.jump_interval</c>). Only granted by the Overkill mutator (WEP_FLAG_HIDDEN | WEP_FLAG_MUTATORBLOCKED
/// | WEP_FLAG_SUPERWEAPON | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH); resolved by NetName "okrpc".
///
/// Identity/attributes from okrpc.qh; balance from bal-wep-xonotic.cfg (g_balance_okrpc_*). Ported: the
/// missile spawn (W_OverkillRocketPropelledChainsaw_Attack), its forward acceleration + per-frame chainsaw
/// pass-through damage (W_OverkillRocketPropelledChainsaw_Think), the shoot-down (event_damage → explode),
/// the contact/lifetime explosion (RadiusDamage 150 core / 50 edge / 300 radius / 400 force), and the
/// secondary blaster-jump. Deathtype = this weapon's RegistryId. The accuracy-statistics bookkeeping (QC
/// accuracy_add / accuracy_isgooddamage) and CSQC projectile networking are online/stat-only and omitted; the
/// chainsaw damages any living damageable player it passes through (the faithful gameplay subset). QC carries
/// a legacy deprecated netname "rpc" for kill-message routing (parity risk §11).
/// </summary>
[Weapon]
public sealed class OkRpc : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_OVERKILL_RPC, *).</summary>
    public struct Balance
    {
        public float Ammo;              // g_balance_okrpc_primary_ammo
        public float Animtime;          // g_balance_okrpc_primary_animtime
        public float Damage;            // g_balance_okrpc_primary_damage (explosion core)
        public float Damage2;           // g_balance_okrpc_primary_damage2 (chainsaw pass-through per player)
        public float DamageForceScale;  // g_balance_okrpc_primary_damageforcescale
        public float EdgeDamage;        // g_balance_okrpc_primary_edgedamage
        public float Force;             // g_balance_okrpc_primary_force
        public float Health;            // g_balance_okrpc_primary_health (shootable missile hp)
        public float Lifetime;          // g_balance_okrpc_primary_lifetime
        public float Radius;            // g_balance_okrpc_primary_radius
        public float Refire;            // g_balance_okrpc_primary_refire
        public float Speed;             // g_balance_okrpc_primary_speed (launch speed)
        public float SpeedAccel;        // g_balance_okrpc_primary_speedaccel
        public int   SecondaryRefireType; // g_balance_okrpc_secondary_refire_type (1 = own jump_interval timer)
        public float ReloadAmmo;        // g_balance_okrpc_reload_ammo
        public float ReloadTime;        // g_balance_okrpc_reload_time
    }

    public Balance Cvars;

    public OkRpc()
    {
        NetName = "okrpc";
        BotPickupBaseValue = 10000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Rockets;   // QC ammo_type RES_ROCKETS
        DisplayName = "Overkill Rocket Propelled Chainsaw";
        Impulse = 9;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_HIDDEN | WEP_FLAG_CANCLIMB | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH | WEP_FLAG_SUPERWEAPON
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Hidden | WeaponFlags.CanClimb
                   | WeaponFlags.Reloadable | WeaponFlags.TypeSplash | WeaponFlags.SuperWeapon;
        Color = new Vector3(0.914f, 0.745f, 0.341f);
        ViewModel = "h_ok_rl.iqm";   // MDL_RPC_VIEW
        WorldModel = "v_ok_rl.md3";  // MDL_RPC_WORLD
        ItemModel = "g_ok_rl.md3";   // MDL_RPC_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_okrpc_primary_ammo", 10f);
        Cvars.Animtime = Bal("g_balance_okrpc_primary_animtime", 1f);
        Cvars.Damage = Bal("g_balance_okrpc_primary_damage", 150f);
        Cvars.Damage2 = Bal("g_balance_okrpc_primary_damage2", 500f);
        Cvars.DamageForceScale = Bal("g_balance_okrpc_primary_damageforcescale", 2f);
        Cvars.EdgeDamage = Bal("g_balance_okrpc_primary_edgedamage", 50f);
        Cvars.Force = Bal("g_balance_okrpc_primary_force", 400f);
        Cvars.Health = Bal("g_balance_okrpc_primary_health", 25f);
        Cvars.Lifetime = Bal("g_balance_okrpc_primary_lifetime", 30f);
        Cvars.Radius = Bal("g_balance_okrpc_primary_radius", 300f);
        Cvars.Refire = Bal("g_balance_okrpc_primary_refire", 1f);
        Cvars.Speed = Bal("g_balance_okrpc_primary_speed", 2500f);
        Cvars.SpeedAccel = Bal("g_balance_okrpc_primary_speedaccel", 5000f);
        Cvars.SecondaryRefireType = BalInt("g_balance_okrpc_secondary_refire_type", 1);
        Cvars.ReloadAmmo = Bal("g_balance_okrpc_reload_ammo", 10f);
        Cvars.ReloadTime = Bal("g_balance_okrpc_reload_time", 1f);
    }

    // METHOD(OverkillRocketPropelledChainsaw, wr_think)
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Secondary blaster-jump: refire_type 1 = own jump_interval; refire_type 0 = shared ATTACK_FINISHED.
        OkWeapons.FireSecondaryBlasterJump(this, actor, slot, fire, Cvars.SecondaryRefireType);

        // forced reload
        if (Cvars.ReloadAmmo != 0f && st.ClipLoad < Cvars.Ammo)
        {
            WrReload(actor, slot);
            return;
        }

        if (fire == FireMode.Primary)
        {
            // weapon_prepareattack(thiswep, actor, weaponentity, false, refire)
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
        }
    }

    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_OverkillRocketPropelledChainsaw_Attack — spawn the accelerating, shootable chainsaw missile.
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // W_DecreaseAmmo(thiswep, actor, ammo) — clip-aware (WEP_FLAG_RELOADABLE): drains the magazine so the
        // wr_think forced-reload branch (clip_load < ammo) engages. okrpc.qc:104 (ammo) + :170-174 (reload).
        DecreaseAmmo(actor, slot, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "okrpc_missile";
        missile.Owner = actor;            // missile.owner = missile.realowner = actor
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(missile); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        missile.Flags = EntFlags.Item;    // QC FL_PROJECTILE
        missile.DamageForceScale = Cvars.DamageForceScale;

        // setsize '-3 -3 -3'..'3 3 3' so it can be shot down.
        Api.Entities.SetSize(missile, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        // setorigin(missile, w_shotorg - v_forward * 3) — back it off so it hits the wall at the right point.
        Api.Entities.SetOrigin(missile, shot.Origin - shot.Dir * 3f);

        // takedamage = DAMAGE_YES; SetResourceExplicit(missile, RES_HEALTH, health).
        missile.TakeDamage = DamageMode.Yes;
        missile.Health = Cvars.Health;

        // QC okrpc.qc: missile.bot_dodge = true; missile.bot_dodgerating = WEP_CVAR_PRI(damage) * 2; IL_PUSH on the
        // g_bot_dodge danger list — bots swerve away from the in-flight chainsaw (consumed by the havocbot dodge
        // scan, BotBrain/Waypoint, which iterates every non-client edict flagged BotDodge; no list-push needed).
        missile.BotDodge = true;
        missile.BotDodgeRating = Cvars.Damage * 2f;

        // QC okrpc.qc: IL_PUSH(g_damagedbycontents, missile) — the flying chainsaw takes lava/slime contents
        // damage and detonates (via its shoot-down shim, installed below). Consumed by the per-frame
        // g_damagedbycontents sweep (GameWorld → PlayerFrameLogic.ProjectileContentsDamage), which scans the
        // entity table for this flag. Inert in dry maps; correctly NOT gated by g_projectiles_damage (that gates
        // only the shoot-down) — lava/slime use g_balance_contents_projectiledamage, matching Base.
        missile.DamagedByContents = true;

        // W_SetupProjVelocity_Basic(missile, speed, 0) — launch straight along shotdir at speed.
        missile.Velocity = shot.Dir * Cvars.Speed;
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // cnt = time + lifetime; m_chainsaw_damage = 0 (reuse Frags as the accumulated-damage flag).
        float deathTime = Api.Clock.Time + Cvars.Lifetime;
        missile.Frags = 0f; // QC missile.m_chainsaw_damage (0 = no pass-through hit yet)

        missile.Touch = (self, other) => Explode(self, other);
        missile.Think = self => OnThink(self, deathTime);
        // event_damage = W_OverkillRocketPropelledChainsaw_Damage → explode when shot down.
        missile.ProjectileDamage = (self, attacker) => Explode(self, null);
        missile.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile)
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        // QC missile.event_damage = W_OverkillRocketPropelledChainsaw_Damage (+ DAMAGE_YES + RES_HEALTH already set
        // above). Install the GtEventDamage shoot-down shim the damage pipeline actually dispatches; without this the
        // missile's ProjectileDamage callback is dead (DamageSystem.EventDamage only routes a non-player victim via
        // GtEventDamage), so the chainsaw cannot be shot out of the air. No exception (-1), matching okrpc.qc:50.
        Projectiles.MakeShootable(missile, exception: -1f);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/rocket_fire.wav");

        // W_MuzzleFlash(thiswep, ...) — okrpc.qh m_muzzleeffect = EFFECT_ROCKET_MUZZLEFLASH (okrpc.qc:106).
        EffectEmitter.Emit("ROCKET_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        if (Api.Clock.Time >= missile.NextThink)
            missile.Think(missile);
    }

    // W_OverkillRocketPropelledChainsaw_Think — accelerate + per-frame chainsaw pass-through, until lifetime ends.
    private void OnThink(Entity self, float deathTime)
    {
        if (self.IsFreed) return;
        // QC: if (this.cnt <= time) delete(this);
        if (deathTime <= Api.Clock.Time)
        {
            Api.Entities.Remove(self);
            return;
        }

        float myspeed = self.Velocity.Length();
        float myspeedAccelStep = myspeed * Api.Clock.FrameTime;
        Vector3 mydir = QMath.Normalize(self.Velocity);
        if (mydir == Vector3.Zero) mydir = QMath.Forward(self.Angles);

        // tracebox(origin, mins, maxs, origin + mydir * (2 * myspeed_accel), MOVE_NORMAL, this)
        Vector3 end = self.Origin + mydir * (2f * myspeedAccelStep);
        TraceResult tr = Api.Trace.Trace(self.Origin, self.Mins, self.Maxs, end, MoveFilter.Normal, self);
        Entity? hit = tr.Ent;

        // if (IS_PLAYER(trace_ent)) { ... chainsaw damage ... }
        Entity? owner = self.Owner;
        if (hit is not null && (hit.Flags & EntFlags.Client) != 0 && hit.TakeDamage != DamageMode.No)
        {
            self.Frags += Cvars.Damage2; // QC this.m_chainsaw_damage += damage2 (the "first hit" accuracy bookkeeping is stat-only)

            // Damage(trace_ent, this, this.realowner, damage2, projectiledeathtype, ..., origin,
            //        normalize(origin - trace_ent.origin) * force)
            Vector3 force = QMath.Normalize(self.Origin - hit.Origin) * Cvars.Force;
            Combat.Damage(hit, self, owner, Cvars.Damage2, DeathTypes.FromWeapon(NetName), self.Origin, force);
        }

        // this.velocity = mydir * (myspeed + speedaccel * frametime)
        self.Velocity = mydir * (myspeed + Cvars.SpeedAccel * Api.Clock.FrameTime);

        self.NextThink = Api.Clock.Time;
    }

    // W_OverkillRocketPropelledChainsaw_Explode — RadiusDamage(150 core / 50 edge / 300 radius / 400 force) then remove.
    private void Explode(Entity self, Entity? directHit)
    {
        if (self.IsFreed) return;
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null;
        self.GtEventDamage = null; // QC W_..._Explode: this.event_damage = func_null

        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            self.Owner, RegistryId, Cvars.Force, directHit: directHit);

        // QC wr_impacteffect (okrpc.qc:232-238): EFFECT_ROCKET_EXPLODE puff + SND_ROCKET_IMPACT at the blast point.
        WeaponSplash.ImpactSound(self, "weapons/rocket_impact.wav"); // QC SND_ROCKET_IMPACT
        EffectEmitter.Emit("ROCKET_EXPLODE", self.Origin);

        Api.Entities.Remove(self);
    }

    // METHOD(OverkillRocketPropelledChainsaw, wr_aim) — okrpc.qc:144-147. The chainsaw is a slow projectile, so
    // bots lead the target by its launch speed (WEP_CVAR_PRI speed = 2500) — QC passes that speed as bot_aim's
    // lead argument. Without this override the brain would lead by the generic default speed and mis-aim the
    // flying chainsaw. (The QC lifetime arg only widens the lead-prediction window; the brain already caps the
    // lead by the weapon's primary speed, which is the dominant term.)
    public override float BotAimShotSpeed(float defaultSpeed) => Cvars.Speed;

    // METHOD(OverkillRocketPropelledChainsaw, wr_checkammo1)
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(OverkillRocketPropelledChainsaw, wr_checkammo2) — secondary (blaster) unlimited.
    public bool CheckAmmoSecondary(Entity actor) => true;

    public override float ReloadingAmmo() => Cvars.ReloadAmmo;
    public override float ReloadingTime() => Cvars.ReloadTime;
}
