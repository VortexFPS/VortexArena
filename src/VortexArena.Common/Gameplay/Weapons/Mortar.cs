using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Mortar (Nexuiz "Grenade Launcher") — port of common/weapons/weapon/mortar.{qh,qc}. A splash weapon
/// firing bouncing grenades under gravity. Primary (type 0) explodes immediately on impact; secondary
/// (type 1) bounces, then explodes a short time after its first bounce (or at end of lifetime). Both
/// grenades are shootable and use MOVETYPE_BOUNCE with the configured bounce factor/stop.
///
/// Identity/attributes from mortar.qh; balance from bal-wep-xonotic.cfg (g_balance_mortar_*).
/// This phase ports both grenade projectiles, their bounce/lifetime/contact detonation and the splash
/// damage, remote-detonate-primary, sticky (type 2) grenades, shoot-down (live via Projectiles.MakeShootable),
/// the bounce particle (EFFECT_HAGAR_BOUNCE), the HITTYPE_SECONDARY/HITTYPE_BOUNCE kill-message deathtype, and
/// the airshot achievement on an airborne direct hit. Only the CSQC in-flight grenade model/trail remains
/// render-only. No-TrueAim (WEP_FLAG_NOTRUEAIM): grenades launch straight along v_forward.
/// </summary>
[Weapon]
public sealed class Mortar : Weapon
{
    /// <summary>Per-fire-mode balance block — QC WEP_CVAR_PRI/SEC(WEP_MORTAR, *).</summary>
    public struct ModeBalance
    {
        public float Ammo;             // *_ammo (rockets per shot)
        public float Animtime;         // *_animtime
        public float Damage;           // *_damage
        public float DamageForceScale; // *_damageforcescale
        public float EdgeDamage;       // *_edgedamage
        public float Force;            // *_force
        public float Health;           // *_health (shootable grenade hp)
        public float Lifetime;         // *_lifetime
        public float LifetimeBounce;   // *_lifetime_bounce (SEC: fuse after first bounce)
        public float Radius;           // *_radius
        public float Refire;           // *_refire
        public float Speed;            // *_speed (forward launch speed)
        public float SpeedUp;          // *_speed_up (added vertical launch speed)
        public int   Type;             // *_type (0=impact, 1=bounce, 2=stick)
    }

    public ModeBalance Primary;
    public ModeBalance Secondary;

    // Shared (non-PRI/SEC) bounce physics — g_balance_mortar_bounce*.
    public float BounceFactor;  // g_balance_mortar_bouncefactor
    public float BounceStop;    // g_balance_mortar_bouncestop


    public Mortar()
    {
        NetName = "mortar";
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Mortar";
        Impulse = 4;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.CanClimb
                   | WeaponFlags.TypeSplash | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.988f, 0.392f, 0.314f);
        ViewModel = "h_gl.iqm";  // MDL_MORTAR_VIEW
        WorldModel = "v_gl.md3"; // MDL_MORTAR_WORLD
        ItemModel = "g_gl.md3";  // MDL_MORTAR_ITEM
    }

    public override void Configure()
    {
        BounceFactor = Bal("g_balance_mortar_bouncefactor", 0.5f);
        BounceStop = Bal("g_balance_mortar_bouncestop", 0.075f);

        Primary.Ammo = Bal("g_balance_mortar_primary_ammo", 2f);
        Primary.Animtime = Bal("g_balance_mortar_primary_animtime", 0.3f);
        Primary.Damage = Bal("g_balance_mortar_primary_damage", 55f);
        Primary.DamageForceScale = Bal("g_balance_mortar_primary_damageforcescale", 0f);
        Primary.EdgeDamage = Bal("g_balance_mortar_primary_edgedamage", 25f);
        Primary.Force = Bal("g_balance_mortar_primary_force", 250f);
        Primary.Health = Bal("g_balance_mortar_primary_health", 15f);
        Primary.Lifetime = Bal("g_balance_mortar_primary_lifetime", 20f);
        Primary.LifetimeBounce = 0f;
        Primary.Radius = Bal("g_balance_mortar_primary_radius", 120f);
        Primary.Refire = Bal("g_balance_mortar_primary_refire", 0.8f);
        Primary.Speed = Bal("g_balance_mortar_primary_speed", 1900f);
        Primary.SpeedUp = Bal("g_balance_mortar_primary_speed_up", 225f);
        Primary.Type = BalInt("g_balance_mortar_primary_type", 0);

        Secondary.Ammo = Bal("g_balance_mortar_secondary_ammo", 2f);
        Secondary.Animtime = Bal("g_balance_mortar_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_mortar_secondary_damage", 55f);
        Secondary.DamageForceScale = Bal("g_balance_mortar_secondary_damageforcescale", 4f);
        Secondary.EdgeDamage = Bal("g_balance_mortar_secondary_edgedamage", 30f);
        Secondary.Force = Bal("g_balance_mortar_secondary_force", 250f);
        Secondary.Health = Bal("g_balance_mortar_secondary_health", 30f);
        Secondary.Lifetime = Bal("g_balance_mortar_secondary_lifetime", 20f);
        Secondary.LifetimeBounce = Bal("g_balance_mortar_secondary_lifetime_bounce", 0.5f);
        Secondary.Radius = Bal("g_balance_mortar_secondary_radius", 120f);
        Secondary.Refire = Bal("g_balance_mortar_secondary_refire", 0.7f);
        Secondary.Speed = Bal("g_balance_mortar_secondary_speed", 1400f);
        Secondary.SpeedUp = Bal("g_balance_mortar_secondary_speed_up", 150f);
        Secondary.Type = BalInt("g_balance_mortar_secondary_type", 1);

        // WEP_CVAR_SEC(WEP_MORTAR, remote_detonateprimary) / WEP_CVAR_PRI(WEP_MORTAR, remote_minbouncecnt)
        // (mortar.qh:57-58; bal-wep-xonotic.cfg:144,164). Both default 0 (off) — secondary fire remote-detonates
        // the firer's own live primary grenades once each has bounced remote_minbouncecnt times (wr_think / Think1).
        RemoteDetonatePrimary = Bal("g_balance_mortar_secondary_remote_detonateprimary", 0f) != 0f;
        RemoteMinBounceCount = BalInt("g_balance_mortar_primary_remote_minbouncecnt", 0);
    }

    /// <summary>g_balance_mortar_secondary_remote_detonateprimary — secondary remote-detonates primaries.</summary>
    public bool RemoteDetonatePrimary;
    /// <summary>g_balance_mortar_remote_minbouncecnt — bounces a primary must make before remote detonation.</summary>
    public int RemoteMinBounceCount;

    // METHOD(Mortar, wr_think) — common/weapons/weapon/mortar.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire == FireMode.Primary)
        {
            // QC: if (weapon_prepareattack(..., primary refire)) { W_Mortar_Attack(...); weapon_thinkf(...); }
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, Primary, secondary: false);
        }
        else if (fire == FireMode.Secondary)
        {
            if (RemoteDetonatePrimary)
            {
                // Flag every live primary grenade this actor owns to detonate (W_Mortar_Grenade_Think1
                // explodes it once it has bounced remote_minbouncecnt times).
                bool found = false;
                foreach (Entity g in Api.Entities.FindByClass("grenade"))
                {
                    if (!ReferenceEquals(g.Owner, actor) || g.IsFreed) continue;
                    if (g.NetName != NetName) continue;
                    if (g.DeadState != DeadFlag.Dying) // reuse DeadState as the gl_detonate_later latch
                    {
                        g.DeadState = DeadFlag.Dying;
                        found = true;
                    }
                }
                if (found) Api.Sound.Play(actor, SoundChannel.Body, "weapons/grenade_det.wav");
            }
            else
            {
                // QC: if (weapon_prepareattack(..., secondary refire)) { W_Mortar_Attack2(...); ... }
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, Secondary, secondary: true);
            }
        }
    }

    // METHOD(Mortar, wr_aim) — mortar.qc:wr_aim. The bot holds a primary/secondary preference
    // (.bot_secondary_grenademooth, carried here in ctx.SecondaryToggle): while preferring primary it lobs
    // the direct grenade and, on each successful aim, has a 1% chance to flip to the airburst secondary;
    // while preferring secondary it lobs the bounce grenade and has a 2% chance to flip back. Both modes are
    // gravity-lobbed (bot_aim grav=true), so the brain's ballistic arc applies; this hook owns only the
    // primary/secondary button pick + the per-mode lead speed, mirroring the two bot_aim calls.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        if (!ctx.SecondaryToggle)
        {
            if (ctx.Random01 < 0.01f) ctx.SecondaryToggle = true;
            return false; // press primary (the airburst direct grenade)
        }
        if (ctx.Random01 < 0.02f) ctx.SecondaryToggle = false;
        return true; // press secondary (the bouncing grenade)
    }

    // Lead by the mode's launch speed (QC passes WEP_CVAR_PRI/SEC speed to bot_aim per branch).
    public override float BotAimShotSpeed(float defaultSpeed, ref BotAimState ctx)
        => ctx.SecondaryToggle ? Secondary.Speed : Primary.Speed;

    // QC bot_aim grav=true for both mortar branches: the bouncing grenade lobs under world sv_gravity (it has no
    // per-weapon gravity cvar), so the brain must arc the shot rather than aim straight (force-lobbed).
    public override bool? BotAimLobbed => true;

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Refire;
    public override float AnimtimeFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Animtime;

    // W_Mortar_Attack / W_Mortar_Attack2 — launch a bouncing grenade. mortar.qc
    private void Attack(Entity actor, WeaponSlot slot, ModeBalance bal, bool secondary)
    {
        actor.TakeResource(AmmoType, bal.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-3, -3, -3), new Vector3(3, 3, 3), recoil: 4f);
        // w_shotdir = v_forward — no TrueAim for grenades.
        Vector3 dir = forward;

        Entity gren = Api.Entities.Spawn();
        gren.ClassName = "grenade";
        gren.Owner = actor;
        gren.NetName = NetName;
        gren.MoveType = MoveType.Bounce;
        gren.BounceFactor = BounceFactor; // QC gren.bouncefactor / .bouncestop (engine MOVETYPE_BOUNCE)
        gren.BounceStop = BounceStop;
        // CSQCProjectile(gren, ..., type==1 ? PROJECTILE_GRENADE_BOUNCING : PROJECTILE_GRENADE, ...) (mortar.qc:203-207).
        // The bouncing grenade (type 1) renders the sideways-tumbling bouncing model; impact/stick (type 0/2) the plain one.
        gren.CsqcProjectileBouncing = bal.Type == 1;
        Projectiles.MakeTrigger(gren); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        gren.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(gren, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        Api.Entities.SetOrigin(gren, shot.Origin);

        gren.TakeDamage = DamageMode.Yes; // shootable
        gren.Health = bal.Health;
        gren.DamageForceScale = bal.DamageForceScale;

        // W_SetupProjVelocity_UP: velocity = dir*speed + up*speed_up (no per-shot spread for grenades).
        gren.Velocity = WeaponFiring.ProjectileVelocity(dir, up, bal.Speed, bal.SpeedUp);
        gren.Angles = QMath.VecToAngles(gren.Velocity);

        // QC mortar.qc:172-173,223-224 — flag the grenade as a dodgeable hazard (bot_dodge/bot_dodgerating =
        // mode damage + IL_PUSH(g_bot_dodge)). Consumed by the SUPERBOT-gated BotBrain.HavocbotDodge.
        gren.BotDodge = true;
        gren.BotDodgeRating = bal.Damage;

        int type = bal.Type;
        float damage = bal.Damage, edge = bal.EdgeDamage, radius = bal.Radius, force = bal.Force;
        float lifetimeBounce = bal.LifetimeBounce;
        // QC gren.projectiledeathtype = thiswep.m_id (| HITTYPE_SECONDARY for the bouncing secondary). We carry
        // the string deathtype so the bounce-vs-explode kill message (which keys on HITTYPE_SECONDARY) and the
        // dynamically-OR'd HITTYPE_BOUNCE both survive to the obituary; HITTYPE_BOUNCE is added in Explode.
        string deathBase = Damage.DeathTypes.FromWeapon(NetName);
        if (secondary) deathBase = Damage.DeathTypes.WithHitType(deathBase, Damage.DeathTypes.Secondary);
        gren.Count = 0; // QC .gl_bouncecnt — bounce counter lives on the entity (lambdas can't capture a ref)

        // cnt = time + lifetime (forced detonation deadline). Primary thinks each frame; secondary uses a
        // single timed think. We model both with one Think that detonates once the deadline passes.
        float deathTime = Api.Clock.Time + bal.Lifetime;

        gren.Think = self => OnThink(self, deathTime, damage, edge, radius, force, deathBase);
        gren.NextThink = secondary ? deathTime : Api.Clock.Time;

        gren.Touch = (self, other) =>
            OnTouch(self, other, type, lifetimeBounce, deathTime,
                damage, edge, radius, force, deathBase);

        // W_Mortar_Grenade_Damage: shot down -> explode. Projectiles.MakeShootable installs the QC
        // W_CheckProjectileDamage gate + RES_HEALTH subtraction and invokes this callback only once HP hits 0
        // (faithful to W_PrepareExplosionByDamage), so a non-lethal graze no longer detonates the grenade.
        gren.ProjectileDamage = (self, attacker) =>
            Explode(self, damage, edge, radius, force, deathBase, self.Count > 0, directHit: null);

        // QC: gren.takedamage = DAMAGE_YES + SetResource(RES_HEALTH) + event_damage = W_Mortar_Grenade_Damage.
        // No exception (-1): a grenade is destructible by anyone the g_projectiles_damage ladder permits.
        Projectiles.MakeShootable(gren, exception: -1f);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/grenade_fire.wav");
        EffectEmitter.Emit("GRENADE_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // MUTATOR_CALLHOOK(EditProjectile, actor, gren) (mortar.qc) — fired before the primary's first think
        // runs, so invincibleproj's health-zeroing takes effect before the grenade can be shot down.
        var ep = new MutatorHooks.EditProjectileArgs(actor, gren);
        MutatorHooks.EditProjectile.Call(ref ep);

        if (!secondary && Api.Clock.Time >= gren.NextThink)
            gren.Think(gren);
    }

    // W_Mortar_Grenade_Think1 — detonate at the lifetime deadline, or when remote-detonate was requested
    // and the grenade has bounced enough times. mortar.qc
    private void OnThink(Entity self, float deathTime, float damage, float edge, float radius, float force, string deathBase)
    {
        self.NextThink = Api.Clock.Time;
        if (Api.Clock.Time > deathTime)
        {
            // QC Think1: this.projectiledeathtype |= HITTYPE_BOUNCE on a forced (timeout) detonation.
            Explode(self, damage, edge, radius, force, deathBase, bounced: true, directHit: null);
            return;
        }
        // gl_detonate_later (latched in DeadState by the secondary remote-detonate sweep) + min bounce count.
        if (self.DeadState == DeadFlag.Dying && self.Count >= RemoteMinBounceCount)
            Explode(self, damage, edge, radius, force, deathBase, self.Count > 0, directHit: null);
    }

    // W_Mortar_Grenade_Touch1/2 — explode on player/impact, or bounce (type 1). mortar.qc
    private void OnTouch(Entity self, Entity other, int type, float lifetimeBounce,
        float deathTime, float damage, float edge, float radius, float force, string deathBase)
    {
        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;

        if (hitPlayer || type == 0)
        {
            // QC `this.use(this, NULL, toucher)` -> W_Mortar_Grenade_Explode(this, toucher): the toucher is the
            // direct-hit entity (skips the blast LOS reduction + drives the airshot achievement). A grenade that
            // has already bounced carries HITTYPE_BOUNCE.
            Explode(self, damage, edge, radius, force, deathBase, self.Count > 0,
                directHit: hitPlayer ? other : null);
            return;
        }

        if (type == 1) // bounce: the engine MOVETYPE_BOUNCE reflects velocity; we just count + fuse.
        {
            Api.Sound.Play(self, SoundChannel.Body, "weapons/grenade_bounce1.wav");
            EffectEmitter.Emit("HAGAR_BOUNCE", self.Origin, self.Velocity, 1); // QC Send_Effect(EFFECT_HAGAR_BOUNCE)
            ++self.Count; // QC .gl_bouncecnt (also where QC OR's HITTYPE_BOUNCE onto the deathtype)
            if (lifetimeBounce != 0f && self.Count == 1)
            {
                float fuse = Api.Clock.Time + lifetimeBounce;
                if (fuse < deathTime)
                {
                    self.NextThink = fuse;
                    self.Think = s => Explode(s, damage, edge, radius, force, deathBase, bounced: true, directHit: null);
                }
            }
        }
        else if (type == 2) // stick: freeze in place on the surface it lands on.
        {
            Api.Sound.Play(self, SoundChannel.Body, "weapons/mortar_stick.wav");
            self.Velocity = Vector3.Zero;
            self.MoveType = MoveType.None; // also disables gravity
            self.Gravity = 0f;
            self.Solid = Solid.Not;        // do not respond to further touches
        }
    }

    // W_Mortar_Grenade_Explode / _Explode2 — radius damage + knockback, then remove. mortar.qc
    private void Explode(Entity self, float damage, float edge, float radius, float force,
        string deathBase, bool bounced, Entity? directHit)
    {
        // QC W_Mortar_Grenade_Explode: a flying enemy directly struck earns the airshot achievement for the
        // owner. Test runs BEFORE the entity is removed (mortar.qc:7-10 / :40-43).
        if (directHit is not null && self.Owner is { } owner
            && directHit.TakeDamage == DamageMode.Aim && (directHit.Flags & EntFlags.Client) != 0
            && !ReferenceEquals(directHit, owner) && !Teams.SameTeam(directHit, owner) // QC DIFF_TEAM
            && directHit.DeadState == DeadFlag.No && IsFlying(directHit))
            NotificationSystem.Announce(owner, "ACHIEVEMENT_AIRSHOT");

        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        self.ProjectileDamage = null; // QC this.event_damage = func_null

        // QC OR's HITTYPE_BOUNCE onto the deathtype once the grenade has bounced / timed out (kill message).
        string deathType = bounced ? Damage.DeathTypes.WithHitType(deathBase, Damage.DeathTypes.Bounce) : deathBase;

        // deathTag carries the full string deathtype (weapon | HITTYPE_SECONDARY | HITTYPE_BOUNCE) so the
        // obituary picks BOUNCE vs EXPLODE; directHit lets the struck target skip the LOS reduction (QC passes
        // directhitentity through to RadiusDamage); accuracyWeapon credits the splash hit to this weapon.
        WeaponSplash.RadiusDamage(self, self.Origin, damage, edge, radius, self.Owner, RegistryId, force,
            directHit: directHit, accuracyWeapon: this, deathTag: deathType);

        WeaponSplash.ImpactSound(self, "weapons/grenade_impact.wav"); // QC SND_MORTAR_IMPACT (wr_impacteffect)
        EffectEmitter.Emit("GRENADE_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // bool IsFlying(entity) — common/physics/player.qc:843, the airshot test: airborne, not swimming, and at
    // least 24u of clearance below (so a player skimming the ground doesn't count).
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    // METHOD(Mortar, wr_checkammo1) — mortar.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Mortar, wr_checkammo2) — mortar.qc
    public bool CheckAmmoSecondary(Entity actor) => actor.GetResource(AmmoType) >= Secondary.Ammo;
}
