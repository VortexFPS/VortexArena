using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Mine Layer — port of common/weapons/weapon/minelayer.{qh,qc}. A splash weapon: primary lobs a mine
/// (MOVETYPE_TOSS) that sticks where it lands and arms; it detonates when an enemy comes within proximity
/// range (sooner the closer they get), at the end of its lifetime, or when remote-detonated. Secondary
/// remote-detonates all of this player's placed mines. Mines are shootable and limited in number.
///
/// Identity/attributes from minelayer.qh; balance from bal-wep-xonotic.cfg (g_balance_minelayer_*).
/// This port covers the mine projectile, surface-sticking (W_MineLayer_Stick lock-in-place), the lifetime
/// countdown, proximity-trigger detonation with the team-safety protection delay, owner-death auto-detonate,
/// mine shoot-down (damageforcescale knock-loose + destroy), the spawnshield/team-safety gated remote
/// detonate, and the splash damage. The mine is modelled (models/mine.md3), shoot-down is live via
/// Projectiles.MakeShootable, the proximity team-filter keys off the owner's team, the over-limit press
/// gives the WEAPON_MINELAYER_LIMIT feedback, and the bot wr_aim (BotWantsDetonate) implements the full
/// skill-gated detonation-desirability math. A stuck mine now re-faces against the real surface normal
/// (vectoangles(-trace_plane_normal), re-probed like Porto) and rides a moving bmodel via SetMovetypeFollow.
/// Only the airshot achievement, the out-of-ammo forced weapon switch, and the separate CSQC in-flight mine
/// networking channel are left out (render/online/HUD).
/// </summary>
[Weapon]
public sealed class Minelayer : Weapon
{
    /// <summary>Balance block — QC WEP_CVAR(WEP_MINE_LAYER, *) (single block, no PRI/SEC split).</summary>
    public struct Balance
    {
        public float Ammo;               // g_balance_minelayer_ammo (rockets per mine)
        public float Animtime;           // g_balance_minelayer_animtime
        public float DamageForceScale;   // g_balance_minelayer_damageforcescale
        public float Damage;             // g_balance_minelayer_damage
        public float DetonateDelay;      // g_balance_minelayer_detonatedelay (<0 = no remote-detonate gate)
        public float EdgeDamage;         // g_balance_minelayer_edgedamage
        public float Force;              // g_balance_minelayer_force
        public float Health;             // g_balance_minelayer_health (shootable mine hp)
        public float Lifetime;           // g_balance_minelayer_lifetime
        public float LifetimeCountdown;  // g_balance_minelayer_lifetime_countdown
        public int   Limit;              // g_balance_minelayer_limit (max simultaneous mines)
        public float Protection;         // g_balance_minelayer_protection (default 0 = no team-safety hold)
        public float ProximityRadius;    // g_balance_minelayer_proximity_radius
        public float ProximityTimeCore;  // g_balance_minelayer_proximity_time_core
        public float ProximityTimeEdge;  // g_balance_minelayer_proximity_time_edge
        public float Radius;             // g_balance_minelayer_radius
        public float Refire;             // g_balance_minelayer_refire
        public float RemoteDamage;       // g_balance_minelayer_remote_damage
        public float RemoteEdgeDamage;   // g_balance_minelayer_remote_edgedamage
        public float RemoteForce;        // g_balance_minelayer_remote_force
        public float RemoteRadius;       // g_balance_minelayer_remote_radius
        public float Speed;              // g_balance_minelayer_speed (launch speed)
    }

    public Balance Cvars;


    public Minelayer()
    {
        NetName = "minelayer";
        BotPickupBaseValue = 7000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "Mine Layer";
        Impulse = 4;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.988f, 0.514f, 0.392f);
        ViewModel = "h_minelayer.iqm";  // MDL_MINELAYER_VIEW
        WorldModel = "v_minelayer.md3"; // MDL_MINELAYER_WORLD
        ItemModel = "g_minelayer.md3";  // MDL_MINELAYER_ITEM
    }

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_minelayer_ammo", 4f);
        Cvars.Animtime = Bal("g_balance_minelayer_animtime", 0.4f);
        Cvars.DamageForceScale = Bal("g_balance_minelayer_damageforcescale", 1.25f);
        Cvars.Damage = Bal("g_balance_minelayer_damage", 40f);
        Cvars.DetonateDelay = Bal("g_balance_minelayer_detonatedelay", -1f);
        Cvars.EdgeDamage = Bal("g_balance_minelayer_edgedamage", 20f);
        Cvars.Force = Bal("g_balance_minelayer_force", 250f);
        Cvars.Health = Bal("g_balance_minelayer_health", 15f);
        Cvars.Lifetime = Bal("g_balance_minelayer_lifetime", 30f);
        Cvars.LifetimeCountdown = Bal("g_balance_minelayer_lifetime_countdown", 0.5f);
        Cvars.Limit = BalInt("g_balance_minelayer_limit", 4);
        Cvars.Protection = Bal("g_balance_minelayer_protection", 0f);
        Cvars.ProximityRadius = Bal("g_balance_minelayer_proximity_radius", 125f);
        Cvars.ProximityTimeCore = Bal("g_balance_minelayer_proximity_time_core", 0.1f);
        Cvars.ProximityTimeEdge = Bal("g_balance_minelayer_proximity_time_edge", 0.3f);
        Cvars.Radius = Bal("g_balance_minelayer_radius", 175f);
        Cvars.Refire = Bal("g_balance_minelayer_refire", 1f);
        Cvars.RemoteDamage = Bal("g_balance_minelayer_remote_damage", 40f);
        Cvars.RemoteEdgeDamage = Bal("g_balance_minelayer_remote_edgedamage", 20f);
        Cvars.RemoteForce = Bal("g_balance_minelayer_remote_force", 300f);
        Cvars.RemoteRadius = Bal("g_balance_minelayer_remote_radius", 200f);
        Cvars.Speed = Bal("g_balance_minelayer_speed", 800f);
    }

    // METHOD(MineLayer, wr_think) — common/weapons/weapon/minelayer.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire == FireMode.Primary)
        {
            // QC checks the per-player mine limit before prepareattack, so an over-limit press doesn't burn
            // the refire timer; then gates the lay on the refire (weapon_prepareattack). The over-limit press
            // gives feedback (W_MineLayer_Attack, minelayer.qc:299-305): a center-print + the UNAVAILABLE cue.
            // The refire delay throttles the spam, matching the comment in Base.
            if (Cvars.Limit > 0 && CountMines(actor) >= Cvars.Limit)
            {
                NotifyOverLimit(actor);
                return;
            }
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
        }
        else if (fire == FireMode.Secondary)
        {
            // Remote-detonate the actor's mines on the secondary press (not a refire-gated shot).
            RemoteDetonate(actor);
        }
    }

    // Refire/animtime from the (cvar-seeded) balance block — the Mine Layer has a single lay-mine fire mode
    // (the secondary remote-detonate is a press action, not a refire-gated shot).
    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // W_MineLayer_Attack — lay a mine that arms and proximity-detonates. minelayer.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // QC enforces the per-player mine limit before firing.
        if (Cvars.Limit > 0 && CountMines(actor) >= Cvars.Limit)
            return;

        actor.TakeResource(AmmoType, Cvars.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4), recoil: 5f);
        // W_MuzzleFlash(thiswep, ...) — Base emits EFFECT_ROCKET_MUZZLEFLASH (minelayer.qc:310).
        EffectEmitter.Emit("ROCKET_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        Entity mine = Api.Entities.Spawn();
        mine.ClassName = "mine";
        mine.Owner = actor;
        mine.NetName = NetName;
        // mine.realowner = actor (minelayer.qc:315). The proximity/team-safety checks key off the OWNER's team,
        // so seed the mine's Team from the firer — without this a placed mine arms on a TEAMMATE in team modes.
        mine.Team = actor.Team;
        mine.MoveType = MoveType.Toss;
        Projectiles.MakeTrigger(mine); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        mine.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(mine, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        // setmodel(mine, MDL_MINELAYER_MINE) — make the in-flight/placed mine visible (minelayer.qc:25 sticks the
        // same model; the in-flight mine is a CSQCProjectile in Base, here a plain server model). Headless-safe.
        if (Api.Services is not null)
        {
            mine.Model = "models/mine.md3";
            Api.Entities.SetModel(mine, mine.Model);
        }
        // setorigin(mine, w_shotorg - v_forward * 4) so it hits the wall at the right point.
        Api.Entities.SetOrigin(mine, shot.Origin - shot.Dir * 4f);

        mine.TakeDamage = DamageMode.Yes; // shootable
        mine.Health = Cvars.Health;
        mine.DamageForceScale = Cvars.DamageForceScale;

        // QC mine.spawnshieldtime (minelayer.qc:316-319): detonatedelay >= 0 sets a spawnshield TIMER gating
        // remote detonation; < 0 = proximity-safety based (mines default to -1, so proximity). Stored on
        // Entity.ProjectileDetonateTime (the shared rocket/mine gate field) — NOT mine.LTime — so the Rocket
        // Flying mutator's EditProjectile write reaches the gate W_MineLayer_RemoteExplode reads. Seeded BEFORE
        // the EditProjectile call below so the mutator's clear wins (QC: seed at :316, MUTATOR_CALLHOOK at :357).
        mine.ProjectileDetonateTime = Cvars.DetonateDelay >= 0f ? Api.Clock.Time + Cvars.DetonateDelay : -1f;

        // W_SetupProjVelocity_Basic(mine, speed, 0).
        mine.Velocity = shot.Dir * Cvars.Speed;
        mine.Angles = QMath.VecToAngles(mine.Velocity);

        // QC minelayer.qc:321-322 — flag the mine as a dodgeable hazard; rating = damage * 2 ("* 2 because it can
        // detonate inflight which makes it even more dangerous"). Consumed by BotBrain.HavocbotDodge.
        mine.BotDodge = true;
        mine.BotDodgeRating = Cvars.Damage * 2f;

        // cnt = (lifetime - lifetime_countdown) + time is the forced-detonation deadline.
        float deathTime = Api.Clock.Time + (Cvars.Lifetime - Cvars.LifetimeCountdown);
        mine.Count = 0;       // bounce counter for re-grounding after a damage knock-off
        mine.MaxHealth = 0f;  // QC .mine_time: scheduled proximity-explosion time (0 = not yet armed)

        mine.Think = self => OnThink(self, deathTime);
        mine.NextThink = Api.Clock.Time;
        mine.Touch = (self, other) => OnTouch(self, other);
        // W_MineLayer_Damage: knock the mine loose (damageforcescale) and/or destroy it when its HP is gone.
        mine.ProjectileDamage = (self, attacker) => OnMineDamage(self, attacker);
        // event_damage = W_MineLayer_Damage (minelayer.qc:327): install the shoot-down shim so the damage
        // pipeline subtracts hp + fires ProjectileDamage. The onHit side effect carries the damageforcescale
        // knock-loose, which QC runs on EVERY surviving hit BEFORE the gate/TakeResource — the hp<=0 detonation
        // stays in ProjectileDamage (OnMineDamage).
        //
        // exception per hit = (is_from_enemy ? 1 : -1), is_from_enemy = inflictor.realowner != this.realowner
        // (minelayer.qc:284-286). Under stock g_projectiles_damage -2 this means an ENEMY can shoot a mine down
        // (exception 1 passes the gate) but the mine OWNER cannot shoot their own mine (exception -1 fails) — the
        // mine is a hazard the owner can't disarm by shooting it. A fixed exception:1 would wrongly let the owner
        // destroy their own mines; pass the inflictor-dependent exception instead.
        Projectiles.MakeShootable(mine, exception: -1f, onHit: OnMineHit,
            exceptionFn: (self, inflictor) =>
                (inflictor is not null && ReferenceEquals(inflictor.RealOwner, self.RealOwner)) ? -1f : 1f);

        // MUTATOR_CALLHOOK(EditProjectile, actor, mine) (minelayer.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, mine);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/mine_fire.wav");
    }

    // W_MineLayer_Damage knock-loose (the hp>0 part) — runs on EVERY surviving hit, BEFORE the g_projectiles_damage
    // gate and the hp subtraction (Projectiles.ShootDown invokes this via the MakeShootable onHit seam). The force
    // can detach a stuck mine so it bounces and re-sticks; a mine's own blast (inflictor classname "mine") never
    // knocks another mine loose. minelayer.qc:272-282
    private void OnMineHit(Entity self, Entity? inflictor, Entity? attacker, Vector3 force)
    {
        if (Cvars.DamageForceScale == 0f) return;
        if (inflictor is not null && inflictor.ClassName == "mine") return;

        self.MoveType = MoveType.Bounce;        // not TOSS: so a direct perpendicular hit can move the mine
        self.MaxHealth = 0f;                     // disarm proximity until it re-grounds
        self.LTime = Api.Clock.Time + 0.0625f;   // .wait: after this it reattaches instead of bouncing
        self.Touch = (s, o) => OnTouch(s, o);    // settouch(this, W_MineLayer_Touch)
        self.AVelocity += force * -Cvars.DamageForceScale; // spin from the impulse
        // QC this.angles = vectoangles(this.velocity) (after the gate/subtract). Re-face the mine along its travel.
        if (self.Velocity.LengthSquared() > 0f)
            self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_MineLayer_Damage tail (hp<=0): the depleted mine detonates. Projectiles.ShootDown fires the per-weapon
    // ProjectileDamage callback only once hp has reached 0 (QC W_PrepareExplosionByDamage). minelayer.qc
    private void OnMineDamage(Entity self, Entity? attacker)
    {
        Explode(self, null);
    }

    // W_MineLayer_Think — lifetime countdown, owner-death auto-detonate, and proximity-trigger logic. minelayer.qc
    private void OnThink(Entity self, float deathTime)
    {
        self.NextThink = Api.Clock.Time;

        // QC W_MineLayer_Think:189-193 — a mine glued to a moving bmodel (SetMovetypeFollow) that lost its
        // platform (LostMovetypeFollow: aiment gone) reverts to MOVETYPE_NONE in place. The port tracks only the
        // null/freed-aiment case (no aiment_classname/deadflag fields); PhysicsFollow already freezes a
        // null-aiment follower, this just restores the locked state so the rest of the think runs normally.
        if (self.MoveType == MoveType.Follow && (self.Aiment is null || self.Aiment.IsFreed))
        {
            self.MoveType = MoveType.None;
            self.Aiment = null;
        }

        // Lifetime reached its countdown point: arm the final countdown (a warning beep + super-aggressive).
        if (Api.Clock.Time > deathTime && self.MaxHealth == 0f && self.Frame == 0f)
        {
            if (Cvars.LifetimeCountdown > 0f)
                Api.Sound.Play(self, SoundChannel.Body, "weapons/mine_trigger.wav");
            self.MaxHealth = Api.Clock.Time + Cvars.LifetimeCountdown;
            self.Frame = 1f; // mine_explodeanyway: ignore team-safety once the countdown is running
        }

        // A player's mines detonate if the owner disconnects, dies, or is frozen (QC W_MineLayer_Think:206).
        Entity? owner = self.Owner;
        if (owner is null || owner.IsFreed || owner.DeadState != DeadFlag.No
            || (owner.Flags & EntFlags.Client) == 0 || IsFrozen(owner))
        {
            // projectiledeathtype |= HITTYPE_BOUNCE (minelayer.qc:208): tag the auto-detonate as a bounce kill.
            Explode(self, null, bounce: true);
            return;
        }

        // Proximity: if an enemy is within proximity_radius, schedule an explosion (sooner the closer they
        // are — scaled from time_edge at the rim to time_core at the center). MaxHealth holds that time.
        if (Api.Services is not null)
        {
            float proxRad = Cvars.ProximityRadius;
            foreach (Entity head in Api.Entities.FindInRadius(self.Origin, proxRad))
            {
                if (head.TakeDamage == DamageMode.No) continue;
                if ((head.Flags & EntFlags.Client) == 0) continue;   // players only
                if (head.DeadState != DeadFlag.No) continue;
                if (IsFrozen(head)) continue;                        // QC !STAT(FROZEN, head)
                if (ReferenceEquals(head, self.Owner)) continue;     // not the owner
                // DIFF_TEAM(head, realowner): self.Team now carries the owner's team (set in Attack), so a
                // teammate (same non-zero team) never arms the mine. In FFA self.Team==0 -> everyone is a foe.
                if (self.Team != 0f && head.Team == self.Team) continue;

                if (self.MaxHealth == 0f)
                    Api.Sound.Play(self, SoundChannel.Body, "weapons/mine_trigger.wav");
                float dist = (head.Origin - self.Origin).Length();
                float newTime = Api.Clock.Time + Cvars.ProximityTimeCore;
                if (Cvars.ProximityTimeEdge != Cvars.ProximityTimeCore && proxRad > 0f)
                    newTime += (Cvars.ProximityTimeEdge - Cvars.ProximityTimeCore) * (dist / proxRad);
                if (self.MaxHealth == 0f || newTime < self.MaxHealth)
                    self.MaxHealth = newTime; // choose the earliest explosion time
            }
        }

        if (self.MaxHealth != 0f && Api.Clock.Time >= self.MaxHealth)
            ProximityExplode(self);

        // re-ground after a damage knock-off: a bouncing mine that lands re-sticks on the next touch.
    }

    // W_MineLayer_ProximityExplode — team-safety: if a friend is in the blast radius, delay until clear
    // (unless the lifetime countdown made the mine super-aggressive). minelayer.qc
    private void ProximityExplode(Entity self)
    {
        // QC W_MineLayer_ProximityExplode:166 — the team-safety hold is gated on WEP_CVAR(protection) (default
        // 0 = disabled) AND mine_explodeanyway == 0. With protection off (stock) the mine never holds for a
        // friend; with it on, a friend inside the blast radius delays detonation.
        if (Cvars.Protection != 0f && self.Frame == 0f && self.Team != 0f && Api.Services is not null)
        {
            foreach (Entity head in Api.Entities.FindInRadius(self.Origin, Cvars.Radius))
                if (head.Team == self.Team && (head.Flags & EntFlags.Client) != 0 && !ReferenceEquals(head, self.Owner))
                    return; // a friend is too close — hold
        }
        self.MaxHealth = 0f;
        Explode(self, null);
    }

    // W_MineLayer_Touch / W_MineLayer_Stick — stick to the BSP surface it lands on, facing against the
    // surface normal, and lock in place. minelayer.qc
    private void OnTouch(Entity self, Entity other)
    {
        if (self.MoveType is MoveType.None or MoveType.Follow) return; // already stuck (QC W_MineLayer_Touch:257-259)
        if (Api.Clock.Time <= self.LTime) return;   // .wait: a knock-loose mine won't reattach for one tick window

        // Stick ONLY to BSP (QC W_MineLayer_Touch:265 — toucher.solid == SOLID_BSP). The earlier non-client
        // broadening could stick a mine to other solids; dropped for faithfulness.
        if (other.Solid != Solid.Bsp) return;

        // W_MineLayer_Stick: freeze the mine in place on the surface (QC: angles = vectoangles(-trace_plane_normal),
        // movedir = -trace_plane_normal, MOVETYPE_NONE). The engine's bouncemissile touch doesn't carry the
        // collision globals to this callback, so — exactly like Porto's OnTouch does — re-probe the impact face
        // with a short worldonly traceline to recover the TRUE plane normal (instead of approximating it from the
        // negated velocity). The mine faces against the surface (vectoangles(-normal)) and stores -normal in movedir
        // (PunchVector) so the remote-explode can set a non-zero .velocity for its fx/decal.
        Vector3 intoWall = ProbeStickNormal(self); // = -trace_plane_normal (faces into the surface)
        self.PunchVector = intoWall;                // movedir = -trace_plane_normal
        if (intoWall.LengthSquared() > 0f)
            self.Angles = QMath.VecToAngles(intoWall); // angles = vectoangles(-trace_plane_normal)
        Api.Sound.Play(self, SoundChannel.Body, "weapons/mine_stick.wav");
        self.Velocity = Vector3.Zero;
        self.MoveType = MoveType.None; // lock in place (disables gravity)
        self.Gravity = 0f;

        // QC W_MineLayer_Stick tail: if (to) SetMovetypeFollow(newmine, to) — glue the mine to a MOVING bmodel
        // (func_plat/func_door) so it rides the platform. In QC the static world is entity 0 (`if(to)` is false),
        // so this only fires for a real brush entity. The port substitutes a "worldspawn" sentinel for static-world
        // hits, so skip those; attach only to a real, non-pushing-aside bmodel that can move.
        if (other.MoveType != MoveType.None && other.ClassName != "worldspawn" && !ReferenceEquals(other, self))
            SetMovetypeFollow(self, other);
    }

    // SetMovetypeFollow(ent, e) (common/util.qc:2129) — make a stuck mine ride a moving bmodel via MOVETYPE_FOLLOW.
    // Mirrors follow_sameorigin (relative origin/angle bookkeeping the PhysicsFollow integrator replays each tick),
    // plus solid=NOT (a FOLLOW entity is always non-solid).
    private static void SetMovetypeFollow(Entity ent, Entity e)
    {
        ent.MoveType = MoveType.Follow;
        ent.Solid = Solid.Not;
        ent.Aiment = e;
        ent.PunchAngle = e.Angles;            // the original angles of the bmodel
        ent.ViewOfs = ent.Origin - e.Origin;  // relative origin
        ent.VAngle = ent.Angles - e.Angles;   // relative angles
    }

    // Recover -trace_plane_normal at the stick point by re-probing the impact face with a short worldonly
    // traceline (Base reads trace_plane_normal from the collision that produced the touch; the engine callback
    // doesn't carry it). Mirrors Porto.ProbeImpact. Returns the into-the-wall direction (= -plane_normal); falls
    // back to the negated incoming velocity when no solid is within reach (the previous approximation).
    private Vector3 ProbeStickNormal(Entity self)
    {
        Vector3 vel = self.Velocity;
        Vector3 fallback = vel.LengthSquared() > 0.0001f ? -Vector3.Normalize(vel) : new Vector3(0f, 0f, -1f);
        if (Api.Services is null || vel.LengthSquared() <= 0.0001f)
            return fallback;

        Vector3 into = -Vector3.Normalize(vel); // points into the wall the mine struck
        Vector3 start = self.Origin - into * 4f;
        Vector3 end = self.Origin + into * 16f;
        TraceResult tr = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.WorldOnly, self);
        if (tr.Fraction >= 1f || tr.PlaneNormal.LengthSquared() <= 0.0001f)
            return fallback;
        return -Vector3.Normalize(tr.PlaneNormal); // -trace_plane_normal
    }

    // W_MineLayer_Explode — radius damage + knockback, then remove. minelayer.qc
    // (bounce = QC projectiledeathtype |= HITTYPE_BOUNCE for the owner-death/remote auto-detonate; the port's
    // int weapon-id deathtype has no bounce flag and the obituary picks the same splash-murder string either
    // way, so the tag is carried only as documentation — kept as a param to mark the faithful call sites.)
    private void Explode(Entity self, Entity? directHit, bool bounce = false)
    {
        // QC airshot achievement (minelayer.qc:60-63): a flying enemy DIRECTLY struck mid-air earns the owner the
        // airshot announce. Tested before the entity is removed. NOTE: every minelayer caller of W_MineLayer_Explode
        // passes directhitentity = NULL (W_MineLayer_Explode_think and W_MineLayer_ProximityExplode both pass NULL),
        // so this branch is unreachable for the mine in Base too — ported for structural fidelity (it would fire if a
        // direct-hit path were ever added).
        if (directHit is not null && self.Owner is { } airOwner
            && directHit.TakeDamage == DamageMode.Aim && (directHit.Flags & EntFlags.Client) != 0
            && !Teams.SameTeam(directHit, airOwner) && !ReferenceEquals(directHit, airOwner) // QC DIFF_TEAM
            && directHit.DeadState == DeadFlag.No && IsFlying(directHit))
            NotificationSystem.Announce(airOwner, "ACHIEVEMENT_AIRSHOT");

        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        WeaponSplash.RadiusDamage(self, self.Origin, Cvars.Damage, Cvars.EdgeDamage, Cvars.Radius,
            self.Owner, RegistryId, Cvars.Force, directHit: directHit);

        WeaponSplash.ImpactSound(self, "weapons/mine_exp.wav"); // QC SND_MINE_EXP (wr_impacteffect)
        EffectEmitter.Emit("ROCKET_EXPLODE", self.Origin);

        // QC W_MineLayer_Explode tail (minelayer.qc:80-92): if the owner is still holding the Mine Layer and is now
        // out of ammo (and not unlimited), force a switch to the best other weapon. This is the same out-of-ammo
        // auto-switch every other weapon runs via WeaponFireGate; the mine carries no slot, so locate the slot
        // currently holding the Mine Layer on the owner.
        ForceSwitchIfOutOfAmmo(self.Owner);

        Api.Entities.Remove(self);
    }

    // W_MineLayer_Explode/DoRemoteExplode tail (minelayer.qc:80-92, :122-134): after the last mine's blast, if the
    // owner still wields the Mine Layer and the rocket resource is now depleted (and not IT_UNLIMITED_AMMO), force a
    // switch to the best other owned weapon. SwitchToOtherWeapon (WeaponFireGate) ports w_getbestweapon + the
    // ATTACK_FINISHED/m_switchweapon assignment.
    private void ForceSwitchIfOutOfAmmo(Entity? owner)
    {
        if (owner is null || owner.IsFreed) return;
        if (owner.UnlimitedAmmo || (owner.Items & (1 << 0)) != 0) return; // IT_UNLIMITED_AMMO = BIT(0)
        if (CheckAmmoPrimary(owner)) return; // still has ammo for another mine — no switch

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

    // bool IsFlying(entity) — common/physics/player.qc, the airshot test: airborne, not swimming, and at least
    // 24u of clearance below (so a player skimming the ground doesn't count). Mirrors Devastator.IsFlying.
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    // W_MineLayer_RemoteExplode (secondary) — remote-detonate this actor's mines, gated by the spawnshield
    // timer (detonatedelay) and a team-safety radius check (don't blow up near a friend). minelayer.qc
    //
    // Sound semantics (Base minelayer.qc:483-487 / W_MineLayer_PlacedMines):
    //   Base FIRST calls W_MineLayer_PlacedMines(detonate=true) which iterates ALL mines and flags
    //   minelayer_detonate=true on each; it plays SND_MINE_DET as soon as ANY mine was newly flagged —
    //   regardless of whether any mine will actually pass the spawnshield/team-safety gate this tick.
    //   With detonatedelay=-1 (proximity safety), the sound fires even if every mine is still inside the
    //   self-safety radius and nothing actually detonates.  The per-mine gates run later, in W_MineLayer_Think.
    //   Reproduce by: play the sound if ANY mine belonging to this actor exists (= W_MineLayer_PlacedMines
    //   found at least one mine to flag), then apply the gates to decide which ones actually fire.
    private void RemoteDetonate(Entity actor)
    {
        // Snapshot first: the blast removes mines and RadiusDamage re-enumerates the world.
        var mines = Api.Entities.FindByClass("mine")
            .Where(e => ReferenceEquals(e.Owner, actor) && !e.IsFreed).ToList();

        // QC W_MineLayer_PlacedMines: plays mine_det as soon as ANY mine was newly flagged (before the gates).
        // The sound fires whenever the actor has at least one placed mine — matches Base wr_think:483-487.
        if (mines.Count > 0)
            Api.Sound.Play(actor, SoundChannel.Body, "weapons/mine_det.wav");

        bool anyDetonated = false;
        foreach (Entity e in mines)
        {
            // spawnshield gate (QC W_MineLayer_RemoteExplode, minelayer.qc:142-145): detonatedelay >= 0 requires
            // the timer to elapse; < 0 is a proximity safety. Reads the mine's Entity.ProjectileDetonateTime so
            // the Rocket Flying mutator's clear (= time) opens remote detonation immediately.
            if (e.ProjectileDetonateTime >= 0f)
            {
                if (Api.Clock.Time < e.ProjectileDetonateTime) continue; // timer not yet elapsed
            }
            else
            {
                // proximity safety: don't remote-detonate while still inside our own blast radius.
                float d = (e.Origin - (actor.Origin + (actor.Mins + actor.Maxs) * 0.5f)).Length();
                if (d <= Cvars.RemoteRadius) continue;
            }

            // team-safety: skip if a friend is within the remote blast radius.
            if (actor.Team != 0f && Api.Services is not null)
            {
                bool friendNear = false;
                foreach (Entity head in Api.Entities.FindInRadius(e.Origin, Cvars.RemoteRadius))
                    if (head.Team == actor.Team && (head.Flags & EntFlags.Client) != 0) { friendNear = true; break; }
                if (friendNear) continue;
            }

            e.Touch = null;
            e.Think = null;
            e.TakeDamage = DamageMode.No;
            e.ProjectileDamage = null;
            // QC W_MineLayer_DoRemoteExplode:106-108 — a stuck mine has zero velocity, so .velocity = movedir
            // (stored on stick) to give the blast fx/decal a direction.
            if (e.MoveType == MoveType.None && e.PunchVector.LengthSquared() > 0f)
                e.Velocity = e.PunchVector;
            // remote_damage | HITTYPE_BOUNCE (inert tag in the port — see Explode).
            WeaponSplash.RadiusDamage(e, e.Origin, Cvars.RemoteDamage, Cvars.RemoteEdgeDamage,
                Cvars.RemoteRadius, actor, RegistryId, Cvars.RemoteForce);
            Api.Entities.Remove(e);
            anyDetonated = true;
        }

        // QC W_MineLayer_DoRemoteExplode tail (minelayer.qc:122-134): same out-of-ammo forced weapon switch as the
        // proximity/lifetime explode. Runs once after the blasts (per-mine in QC, but idempotent once switched away).
        if (anyDetonated)
            ForceSwitchIfOutOfAmmo(actor);
    }

    // MineLayer.wr_aim (minelayer.qc:379-461) — bot detonation AI.
    // Over-limit: suppress primary fire (bot already has max mines, no point laying another).
    // skill>=2: estimate self/team/enemy damage from each placed mine vs each bot target;
    //   set ATCK2 (remote-detonate) when desirable damage >= 0.75*coredamage (group-detonate threshold) or
    //   when the bot is tracking an enemy and the mine is roughly aimed at them (0.1*coredamage threshold);
    //   cancel if self-damage would kill the bot (skill>6.5 veto). Suppress primary when detonating.
    // Plugs into BotBrain.BotWantsDetonate (the brain calls this regardless of primary-fire and, when it
    // returns true, suppresses primary + sets ATCK2 — exactly the QC "don't fire at the same time" pattern).
    public override bool BotWantsDetonate(
        Entity actor, WeaponSlot slot, float skill,
        System.Collections.Generic.IEnumerable<Entity> targets,
        System.Func<Entity, Entity, bool> shouldAttack)
    {
        if (Api.Services is null) return false;
        if (skill < 2f) return false; // skill 0/1 bots won't detonate mines (QC wr_aim:387)

        // Collect this actor's placed mines (QC IL_EACH(g_mines, it.realowner == actor)).
        var mines = new System.Collections.Generic.List<Entity>();
        foreach (Entity m in Api.Entities.FindByClass("mine"))
        {
            if (!m.IsFreed && ReferenceEquals(m.Owner, actor)) mines.Add(m);
        }
        if (mines.Count == 0) return false;

        // QC wr_aim: suppress primary fire when at the mine limit so the bot doesn't waste ammo.
        // (The primary-suppress path is handled by WrThink's over-limit gate; here we only decide ATCK2.)

        // Compute damage scores: same approach as the QC wr_aim but with stationary mines.
        // QC uses a simpler linear damage model: d = bound(0, edgedamage + (coredamage - edgedamage) *
        //   sqrt(1 - d * reciprocalEdgeRadius), 10000) — note sqrt(1-dist/radius) approximation.
        float edgedamage = Cvars.EdgeDamage;
        float coredamage = Cvars.Damage;
        float edgeradius = Cvars.Radius;
        float reciprocalEdgeRadius = edgeradius > 0f ? 1f / edgeradius : 0f;

        float selfdamage = 0f, teamdamage = 0f, enemydamage = 0f;

        foreach (Entity mine in mines)
        {
            foreach (Entity it in targets)
            {
                if (it.IsFreed) continue;
                bool isSelf = ReferenceEquals(it, actor);
                bool isTeam = !isSelf && Teams.SameTeam(it, actor);
                bool isEnemy = !isSelf && !isTeam && shouldAttack(actor, it);
                if (!isSelf && !isTeam && !isEnemy) continue;

                // QC: target_pos = origin + (mins + maxs) * 0.5 (the entity's world-space box center).
                Vector3 targetPos = it.Origin + (it.Mins + it.Maxs) * 0.5f;
                float dist = (targetPos - mine.Origin).Length();
                // QC damage formula: bound(0, edgedamage + (coredamage - edgedamage) * sqrt(1 - dist/radius), 10000)
                // Only applies inside the blast radius.
                float dmg = 0f;
                if (dist < edgeradius)
                {
                    float ratio = MathF.Max(0f, 1f - dist * reciprocalEdgeRadius);
                    dmg = QMath.Bound(0f, edgedamage + (coredamage - edgedamage) * MathF.Sqrt(ratio), 10000f);
                }

                if (isSelf) selfdamage += dmg;
                else if (isTeam) teamdamage += dmg;
                else enemydamage += dmg;
            }
        }

        // QC desirabledamage = enemydamage - selfdamage*selfdamagepercent (shield) - teamdamage (teamplay).
        float desirabledamage = enemydamage;
        // QC: if actor has shield (not spawnshield), subtract selfdamage * g_balance_selfdamagepercent.
        var shieldEffect = StatusEffectsCatalog.ByName("shield");
        var spawnShield = StatusEffectsCatalog.ByName("spawnshield");
        bool hasShield = shieldEffect is not null && StatusEffectsCatalog.Has(actor, shieldEffect);
        bool hasSpawnShield = spawnShield is not null && StatusEffectsCatalog.Has(actor, spawnShield);
        if (hasShield && !hasSpawnShield)
        {
            float pct = Api.Cvars.GetFloat("g_balance_selfdamagepercent");
            desirabledamage -= selfdamage * pct;
        }
        if (GameScores.Teamplay && actor.Team != 0f)
            desirabledamage -= teamdamage;

        // QC wr_aim:423-460 — the per-mine tracking check (skill <= 9) vs the fast skill>9 path.
        // Port uses makevectors(actor.v_angle) -> v_forward to find the forward direction.
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);

        bool wantDetonate = false;

        foreach (Entity mine in mines)
        {
            // QC skill > 9: only detonate if mine is roughly between the bot and the tracked target AND
            // desirabledamage >= 0.1 * coredamage (minelayer.qc:427-434).
            if (skill > 9f)
            {
                foreach (Entity it in targets)
                {
                    if (it.IsFreed) continue;
                    if (!shouldAttack(actor, it)) continue;
                    Vector3 toMineFromTarget = QMath.Normalize(mine.Origin - it.Origin);
                    if (Vector3.Dot(forward, toMineFromTarget) < 0.1f && desirabledamage >= 0.1f * coredamage)
                        wantDetonate = true;
                }
            }
            else
            {
                // QC skill <= 9: "bots are assumed to use the mine light to see if the mine gets near a player"
                // (minelayer.qc:439-448): detonate if the bot is looking roughly toward the mine's direction
                // from the enemy position, enemy is a real player, desirabledamage threshold met, and a random
                // distance/skill gate passes (lower skill = lower chance at distance, QC random()/distance*300
                // vs frametime*bound(0,(10-skill)*0.2,1)).
                Entity? enemy = actor.Enemy;
                if (enemy is not null && !enemy.IsFreed)
                {
                    Vector3 toMineFromEnemy = QMath.Normalize(mine.Origin - enemy.Origin);
                    if ((enemy.Flags & EntFlags.Client) != 0 // IS_PLAYER
                        && Vector3.Dot(forward, toMineFromEnemy) < 0.1f
                        && desirabledamage >= 0.1f * coredamage)
                    {
                        float distance = QMath.Bound(300f, (actor.Origin - enemy.Origin).Length(), 30000f);
                        float frameTime = 0.05f; // approximate; the QC frametime per bot think interval
                        float threshold = frameTime * QMath.Bound(0f, (10f - skill) * 0.2f, 1f);
                        if ((float)Random.Shared.NextDouble() / distance * 300f > threshold)
                            wantDetonate = true;
                    }
                }
            }
        }

        // QC wr_aim:453-454: if desirabledamage >= 0.75*coredamage, detonate regardless of tracking.
        // "this should do group damage in rare fortunate events"
        if (desirabledamage >= 0.75f * coredamage)
            wantDetonate = true;

        // QC wr_aim:455-456: skill>6.5 self-preservation veto — don't detonate if it would kill us.
        if (skill > 6.5f && selfdamage > actor.GetResource(ResourceType.Health))
            wantDetonate = false;

        return wantDetonate;
    }

    /// <summary>Count this actor's live mines (QC W_MineLayer_Count over the g_mines list).</summary>
    private static int CountMines(Entity actor)
    {
        if (Api.Services is null) return 0;
        int count = 0;
        foreach (Entity e in Api.Entities.FindByClass("mine"))
            if (ReferenceEquals(e.Owner, actor) && !e.IsFreed)
                ++count;
        return count;
    }

    // METHOD(MineLayer, wr_checkammo1) — minelayer.qc
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(MineLayer, wr_checkammo2) — minelayer.qc (secondary available when mines are placed).
    public bool CheckAmmoSecondary(Entity actor) => CountMines(actor) > 0;

    /// <summary>
    /// Over-limit press feedback (QC W_MineLayer_Attack:301-304): a center-print telling the player the cap, plus
    /// the UNAVAILABLE cue. The refire delay throttles the spam (the press is rejected before prepareattack).
    /// </summary>
    private void NotifyOverLimit(Entity actor)
    {
        if (Api.Services is null) return;
        // Send_Notification(NOTIF_ONE, actor, MSG_MULTI, WEAPON_MINELAYER_LIMIT, limit) — f1 = the mine cap.
        NotificationSystem.Send(NotifBroadcast.One, actor, MsgType.Multi, "WEAPON_MINELAYER_LIMIT", (float)Cvars.Limit);
        // QC minelayer.qc:303 play2(actor, SND(UNAVAILABLE)) — a per-recipient 2D cue on CH_INFO at
        // VOL_BASE 0.7 / ATTEN_NONE 0 (NOT a positional weapon-channel emit). SoundSystem.Play2 resolves the
        // registered UNAVAILABLE sample and plays it 2D, matching Base's volume/attenuation/channel.
        SoundSystem.Play2(actor, "UNAVAILABLE");
    }

    /// <summary>QC STAT(FROZEN, e): the gametype freeze stat OR the Frozen status effect (cf. WeaponFireDriver).</summary>
    private static bool IsFrozen(Entity e)
        => e.FrozenStat != 0 || (StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(e, fz));
}
