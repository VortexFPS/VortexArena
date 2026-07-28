using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The T.A.G. Seeker — port of common/weapons/weapon/seeker.{qh,qc}. A splash weapon with two layouts
/// (g_balance_seeker_type). In the default type 0: primary fires a "tag" dart that, on striking a player,
/// spawns a controller which launches a barrage of homing missiles at them; secondary fires FLAC — a rapid
/// spray of short-lived scattered explosives. (In type 1 the roles invert: primary fires homing missiles at
/// already-tagged targets, secondary fires the tag.) Missiles, tags and FLAC bolts are all shootable.
///
/// Identity/attributes from seeker.qh; balance from bal-wep-xonotic.cfg (g_balance_seeker_*).
/// This port covers the homing missile (turnrate steering toward the tagged enemy + accel/decel speed
/// clamp), the FLAC spray (f_diff muzzle cycle + random spread), the tag dart, the tag-tracker volley
/// controller (firing missile_count missiles missile_delay apart), the type-1 tracker/closest-target
/// selection, shoot-down, and the splash damage. Only smart-trace world avoidance, proxy detonation and
/// waypoint sprites are left out.
/// </summary>
[Weapon]
public sealed class Seeker : Weapon
{
    /// <summary>Homing-missile balance — QC WEP_CVAR(WEP_SEEKER, missile_*).</summary>
    public struct MissileBalance
    {
        public float Accel;            // missile_accel
        public float Ammo;             // missile_ammo (rockets per missile)
        public float Animtime;         // missile_animtime
        public int   Count;            // missile_count (missiles launched per tag)
        public float DamageForceScale; // missile_damageforcescale
        public float Damage;           // missile_damage
        public float Decel;            // missile_decel
        public float Delay;            // missile_delay (between volley shots)
        public float EdgeDamage;       // missile_edgedamage
        public float Force;            // missile_force
        public float Health;           // missile_health (shootable missile hp)
        public float Lifetime;         // missile_lifetime
        public float Radius;           // missile_radius
        public float Refire;           // missile_refire
        public float Speed;            // missile_speed (launch speed)
        public float SpeedMax;         // missile_speed_max
        public float SpeedUp;          // missile_speed_up
        public float TurnRate;         // missile_turnrate
        public bool  Smart;            // missile_smart (world-avoidance steering, default ON)
        public float SmartMinDist;     // missile_smart_mindist
        public float SmartTraceMin;    // missile_smart_trace_min
        public float SmartTraceMax;    // missile_smart_trace_max
    }

    /// <summary>FLAC (secondary in type 0) balance — QC WEP_CVAR(WEP_SEEKER, flac_*).</summary>
    public struct FlacBalance
    {
        public float Ammo;         // flac_ammo
        public float Animtime;     // flac_animtime
        public float Damage;       // flac_damage
        public float EdgeDamage;   // flac_edgedamage
        public float Force;        // flac_force
        public float Lifetime;     // flac_lifetime
        public float LifetimeRand; // flac_lifetime_rand
        public float Radius;       // flac_radius
        public float Refire;       // flac_refire
        public float Speed;        // flac_speed (forward launch speed)
        public float SpeedUp;      // flac_speed_up
        public float Spread;       // flac_spread
    }

    /// <summary>Tag-dart (primary in type 0) balance — QC WEP_CVAR(WEP_SEEKER, tag_*).</summary>
    public struct TagBalance
    {
        public float Ammo;            // tag_ammo
        public float Animtime;        // tag_animtime
        public float DamageForceScale;// tag_damageforcescale
        public float Health;          // tag_health (shootable dart hp)
        public float Lifetime;        // tag_lifetime
        public float Refire;          // tag_refire
        public float Speed;           // tag_speed (launch speed)
        public float Spread;          // tag_spread
        public float TrackerLifetime; // tag_tracker_lifetime
    }

    public MissileBalance Missile;
    public FlacBalance Flac;
    public TagBalance Tag;

    /// <summary>g_balance_seeker_type — 0: tag(primary)/flac(secondary); 1: missiles(primary)/tag(secondary).</summary>
    public int Type;


    public Seeker()
    {
        NetName = "seeker";
        BotPickupBaseValue = 5000;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Rockets;   // QC ammo_type
        DisplayName = "T.A.G. Seeker";
        Impulse = 8;
        // WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.MutatorBlocked | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.957f, 0.439f, 0.533f);
        ViewModel = "h_seeker.iqm";  // MDL_SEEKER_VIEW
        WorldModel = "v_seeker.md3"; // MDL_SEEKER_WORLD
        ItemModel = "g_seeker.md3";  // MDL_SEEKER_ITEM
    }

    public override void Configure()
    {
        Missile.Accel = Bal("g_balance_seeker_missile_accel", 1400f);
        Missile.Ammo = Bal("g_balance_seeker_missile_ammo", 2f);
        Missile.Animtime = Bal("g_balance_seeker_missile_animtime", 0.2f);
        Missile.Count = BalInt("g_balance_seeker_missile_count", 3);
        Missile.DamageForceScale = Bal("g_balance_seeker_missile_damageforcescale", 4f);
        Missile.Damage = Bal("g_balance_seeker_missile_damage", 30f);
        Missile.Decel = Bal("g_balance_seeker_missile_decel", 1400f);
        Missile.Delay = Bal("g_balance_seeker_missile_delay", 0.25f);
        Missile.EdgeDamage = Bal("g_balance_seeker_missile_edgedamage", 10f);
        Missile.Force = Bal("g_balance_seeker_missile_force", 150f);
        Missile.Health = Bal("g_balance_seeker_missile_health", 5f);
        Missile.Lifetime = Bal("g_balance_seeker_missile_lifetime", 15f);
        Missile.Radius = Bal("g_balance_seeker_missile_radius", 80f);
        Missile.Refire = Bal("g_balance_seeker_missile_refire", 0.5f);
        Missile.Speed = Bal("g_balance_seeker_missile_speed", 700f);
        Missile.SpeedMax = Bal("g_balance_seeker_missile_speed_max", 1300f);
        Missile.SpeedUp = Bal("g_balance_seeker_missile_speed_up", 300f);
        Missile.TurnRate = Bal("g_balance_seeker_missile_turnrate", 0.65f);
        Missile.Smart = Bal("g_balance_seeker_missile_smart", 1f) != 0f;
        Missile.SmartMinDist = Bal("g_balance_seeker_missile_smart_mindist", 800f);
        Missile.SmartTraceMin = Bal("g_balance_seeker_missile_smart_trace_min", 1000f);
        Missile.SmartTraceMax = Bal("g_balance_seeker_missile_smart_trace_max", 2500f);

        Flac.Ammo = Bal("g_balance_seeker_flac_ammo", 1f);
        Flac.Animtime = Bal("g_balance_seeker_flac_animtime", 0.1f);
        Flac.Damage = Bal("g_balance_seeker_flac_damage", 15f);
        Flac.EdgeDamage = Bal("g_balance_seeker_flac_edgedamage", 10f);
        Flac.Force = Bal("g_balance_seeker_flac_force", 50f);
        Flac.Lifetime = Bal("g_balance_seeker_flac_lifetime", 0.1f);
        Flac.LifetimeRand = Bal("g_balance_seeker_flac_lifetime_rand", 0.05f);
        Flac.Radius = Bal("g_balance_seeker_flac_radius", 100f);
        Flac.Refire = Bal("g_balance_seeker_flac_refire", 0.1f);
        Flac.Speed = Bal("g_balance_seeker_flac_speed", 3000f);
        Flac.SpeedUp = Bal("g_balance_seeker_flac_speed_up", 1000f);
        Flac.Spread = Bal("g_balance_seeker_flac_spread", 0.4f);

        Tag.Ammo = Bal("g_balance_seeker_tag_ammo", 1f);
        Tag.Animtime = Bal("g_balance_seeker_tag_animtime", 0.2f);
        Tag.DamageForceScale = Bal("g_balance_seeker_tag_damageforcescale", 4f);
        Tag.Health = Bal("g_balance_seeker_tag_health", 5f);
        Tag.Lifetime = Bal("g_balance_seeker_tag_lifetime", 15f);
        Tag.Refire = Bal("g_balance_seeker_tag_refire", 0.75f);
        Tag.Speed = Bal("g_balance_seeker_tag_speed", 5000f);
        Tag.Spread = Bal("g_balance_seeker_tag_spread", 0f);
        Tag.TrackerLifetime = Bal("g_balance_seeker_tag_tracker_lifetime", 10f);

        Type = BalInt("g_balance_seeker_type", 0);
    }

    // METHOD(Seeker, wr_think) — common/weapons/weapon/seeker.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        // Each mode is refire-gated (QC weapon_prepareattack with the missile/tag/flac refire per type).
        if (Type == 1)
        {
            // type 1: primary launches missiles at the closest tagged target, secondary fires the tag.
            if (fire == FireMode.Primary) { if (PrepareAttack(actor, slot, fire)) SeekerAttack(actor, slot); }
            else if (fire == FireMode.Secondary) { if (PrepareAttack(actor, slot, fire)) FireTag(actor, slot); }
        }
        else
        {
            // type 0 (default): primary fires the tag, secondary fires FLAC.
            if (fire == FireMode.Primary) { if (PrepareAttack(actor, slot, fire)) FireTag(actor, slot); }
            else if (fire == FireMode.Secondary) { if (PrepareAttack(actor, slot, fire)) FireFlac(actor, slot); }
        }
    }

    // W_Seeker_Attack (type 1) — fire a homing missile at the nearest tagged target with line of sight. seeker.qc
    private void SeekerAttack(Entity actor, WeaponSlot slot)
    {
        Entity? closest = null;
        foreach (var tag in _trackers)
        {
            if (!ReferenceEquals(tag.Owner, actor) || tag.IsFreed || tag.Enemy is null) continue;
            if (closest is null
                || (actor.Origin - tag.Enemy.Origin).LengthSquared() < (actor.Origin - closest.Origin).LengthSquared())
                closest = tag.Enemy;
        }
        if (closest is not null)
        {
            // LOS check: if a wall blocks the target, fire un-targeted.
            Vector3 eye = actor.Origin + actor.ViewOfs;
            TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, closest.Origin, MoveFilter.NoMonsters, actor);
            if (tr.Fraction < 1f && !ReferenceEquals(tr.Ent, closest)) closest = null;
        }
        FireMissile(actor, slot, Vector3.Zero, closest);
    }

    // W_Seeker_Fire_Missile — a homing missile that steers toward its tagged target. seeker.qc
    private void FireMissile(Entity actor, WeaponSlot slot, Vector3 offset, Entity? target)
    {
        actor.TakeResource(AmmoType, Missile.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4), recoil: 2f);

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "seeker_missile";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.Enemy = target; // homing target (steered toward in W_Seeker_Missile_Think)
        missile.MoveType = MoveType.FlyMissile;
        missile.Solid = Solid.BBox;
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(missile, shot.Origin + offset);

        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = Missile.Health;
        missile.DamageForceScale = Missile.DamageForceScale;

        // W_SetupProjVelocity_UP_PRE: velocity = normalize(w_shotdir + up*(speed_up/speed)) * speed.
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Missile.Speed, Missile.SpeedUp);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC seeker.qc:178-179 — flag the seeker missile as a dodgeable hazard (rating = missile damage).
        missile.BotDodge = true;
        missile.BotDodgeRating = Missile.Damage;

        float deathTime = Api.Clock.Time + Missile.Lifetime;
        // QC this.wait — the per-missile adaptive smart-trace length (seeded at trace_max), kept in a closure
        // cell so it persists between this missile's MissileThink calls (the C# successor to the QC .wait field).
        float[] waitCell = { Missile.SmartTraceMax };
        missile.Touch = (self, other) => ExplodeMissile(self);
        missile.Think = self => MissileThink(self, deathTime, waitCell);
        missile.ProjectileDamage = (self, attacker) => ExplodeMissile(self);
        missile.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (seeker.qc W_Seeker_Fire_Missile).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        // W_Seeker_Missile_Damage shoot-down (W1-projectile-net): route RadiusDamage onto ProjectileDamage so a
        // player can shoot the missile out of the air. No exception (W_CheckProjectileDamage(...,-1)), per Base.
        // Self-damage scaling: if the firer shoots their own missile, damage is multiplied by 0.25 (QC base:
        // "if (this.realowner == attacker) TakeResource(this, RES_HEALTH, (damage * 0.25))").
        Projectiles.MakeShootable(missile, exception: -1f,
            damageScale: (self, attacker, damage) =>
            {
                if (attacker is not null && ReferenceEquals(self.Owner, attacker))
                    return damage * 0.25f;
                return damage;
            });

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/seeker_fire.wav");
        EffectEmitter.Emit("SEEKER_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Seeker_Missile_Think — accel/decel speed clamp + turnrate homing toward the tagged enemy. seeker.qc
    private void MissileThink(Entity self, float deathTime, float[] waitCell)
    {
        if (Api.Clock.Time > deathTime)
        {
            ExplodeMissile(self);
            return;
        }
        self.NextThink = Api.Clock.Time;

        float ft = Api.Clock.FrameTime;
        // spd = bound(spd - decel*ft, speed_max, spd + accel*ft) — accelerate toward speed_max.
        float spd = self.Velocity.Length();
        spd = QMath.Bound(spd - Missile.Decel * ft, Missile.SpeedMax, spd + Missile.Accel * ft);

        // Drop the target if it's gone/dead.
        Entity? enemy = self.Enemy;
        if (enemy is not null && (enemy.TakeDamage != DamageMode.Aim || enemy.DeadState != DeadFlag.No))
            enemy = self.Enemy = null;

        if (enemy is not null)
        {
            // newdir = normalize(olddir + desireddir * turnrate); velocity = newdir * spd.
            Vector3 eorg = 0.5f * (enemy.AbsMin + enemy.AbsMax);
            if (eorg == Vector3.Zero) eorg = enemy.Origin + (enemy.Mins + enemy.Maxs) * 0.5f;
            Vector3 desiredDir = QMath.Normalize(eorg - self.Origin);
            Vector3 oldDir = QMath.Normalize(self.Velocity);
            float dist = (eorg - self.Origin).Length();

            // Smart world-avoidance (missile_smart, default ON): trace ahead and blend the obstacle's surface
            // normal into the desired direction so the missile curves around walls/corners instead of clipping
            // them. QC W_Seeker_Missile_Think evasive-maneuvers block (seeker.qc).
            if (Missile.Smart && dist > Missile.SmartMinDist)
            {
                // Trace the shorter of: ahead by the adaptive trace length, or straight to the target.
                Vector3 traceEnd = (self.Origin + oldDir * waitCell[0] - self.Origin).Length() < dist
                    ? self.Origin + oldDir * waitCell[0]
                    : eorg;
                TraceResult tr = Api.Trace.Trace(self.Origin, Vector3.Zero, Vector3.Zero, traceEnd,
                    MoveFilter.Normal, self);

                // Adaptive trace length: bound(trace_min, dist-to-hit, trace_max).
                waitCell[0] = QMath.Bound(Missile.SmartTraceMin, (self.Origin - tr.EndPos).Length(),
                    Missile.SmartTraceMax);

                // Weight the turn by how close/imminent the obstacle is (1 - fraction), blended with the enemy dir.
                desiredDir = QMath.Normalize(
                    (tr.PlaneNormal * (1f - tr.Fraction) + desiredDir * tr.Fraction) * 0.5f);
            }

            Vector3 newDir = QMath.Normalize(oldDir + desiredDir * Missile.TurnRate);
            self.Velocity = newDir * spd;
        }
        else
        {
            self.Velocity = QMath.Normalize(self.Velocity) * spd;
        }
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_Seeker_Missile_Explode — radius damage + knockback, then remove. seeker.qc
    private void ExplodeMissile(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        WeaponSplash.RadiusDamage(self, self.Origin, Missile.Damage, Missile.EdgeDamage, Missile.Radius,
            self.Owner, RegistryId, Missile.Force);
        // QC wr_impacteffect: a missile explosion has no HITTYPE_BOUNCE, so it takes the else branch and plays
        // SND_SEEKEREXP_RANDOM (seekerexp1/2/3) — NOT tag_impact (which is reserved for the tag dart's strike).
        WeaponSplash.ImpactSound(self, SeekerExpSample());
        EffectEmitter.Emit("HAGAR_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // SND_SEEKEREXP_RANDOM — seekerexp1/2/3 (the missile/FLAC explosion cue; no SEEKEREXP group is registered in
    // the sound system yet, so pick the random variant by name here, matching the QC m_id+floor(prandom()*3)).
    private static string SeekerExpSample() => $"weapons/seekerexp{Prandom.RangeInt(0, 3) + 1}.wav";

    // W_Seeker_Fire_Flac — a single short-lived scattered explosive (the burst comes from rapid refire). seeker.qc
    private void FireFlac(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Flac.Ammo);
        var st = actor.WeaponState(slot);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-2, -2, -2), new Vector3(2, 2, 2), recoil: 2f);

        // f_diff: a 4-position muzzle offset cycle (bulletcounter % 4) so the spray fans out side-to-side.
        Vector3 fdiff = (st.MiscBulletCounter & 3) switch
        {
            0 => new Vector3(-1.25f, -3.75f, 0f),
            1 => new Vector3(1.25f, -3.75f, 0f),
            2 => new Vector3(-1.25f, 3.75f, 0f),
            _ => new Vector3(1.25f, 3.75f, 0f),
        };
        ++st.MiscBulletCounter;
        Vector3 origin = shot.Origin + right * fdiff.X + forward * fdiff.Y + up * fdiff.Z;

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "missile";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        missile.Solid = Solid.BBox;
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, new Vector3(-2, -2, -2), new Vector3(2, 2, 2));
        Api.Entities.SetOrigin(missile, origin);

        // W_SetupProjVelocity_UP_PRE(flac_): w_shotdir*speed + up*speed_up, with random per-shot spread.
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Flac.Speed, Flac.SpeedUp, 0f, Flac.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC seeker.qc:281-282 — flag the flac projectile as a dodgeable hazard (rating = flac damage).
        missile.BotDodge = true;
        missile.BotDodgeRating = Flac.Damage;

        float deathTime = Api.Clock.Time + Flac.Lifetime + Prandom.Float() * Flac.LifetimeRand;
        missile.Touch = (self, other) => ExplodeFlac(self);
        missile.Think = self => ExplodeFlac(self); // adaptor_think2use_hittype_splash at lifetime
        missile.NextThink = deathTime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (seeker.qc W_Seeker_Fire_Flac).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/flac_fire.wav");
        // QC W_Seeker_Fire_Flac: "uses hagar effects!" — W_MuzzleFlash(WEP_HAGAR,...), not the seeker flash.
        EffectEmitter.Emit("HAGAR_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Seeker_Flac_Explode — radius damage + knockback, then remove. seeker.qc
    private void ExplodeFlac(Entity self)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;
        WeaponSplash.RadiusDamage(self, self.Origin, Flac.Damage, Flac.EdgeDamage, Flac.Radius,
            self.Owner, RegistryId, Flac.Force);
        // QC wr_impacteffect: the FLAC explode deathtype is HITTYPE_SECONDARY with NO HITTYPE_BOUNCE, so it falls
        // to the else branch and plays SND_SEEKEREXP_RANDOM (seekerexp1/2/3), not tag_impact.
        WeaponSplash.ImpactSound(self, SeekerExpSample());
        EffectEmitter.Emit("HAGAR_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // Active tag trackers (the C# successor to the QC g_seeker_trackers intrusive list).
    private readonly List<Entity> _trackers = new();

    // Per-tagged-player waypoint sprites (type-1 only) — the C# successor to the QC toucher.wps_tag_tracker field.
    // Key = the tagged player; value = the live WP_Seeker sprite following them. Entries are removed when the tracker
    // suicides (target died / owner switched away / lifetime expired) or when the tag refreshes (old sprite killed,
    // new one spawned). Only populated in g_balance_seeker_type=1 layouts.
    private readonly Dictionary<Entity, Waypoints.WaypointSprite> _tagWaypoints = new();

    // W_Seeker_Tagged_Info — the tracker (volley controller in type 0 / lifetime tracker in type 1) by which
    // <paramref name="owner"/> already tags <paramref name="target"/>, or null. Both tracker kinds store the
    // tagged target in .Enemy (QC keys on it.tag_target == istarget && it.realowner == isowner).
    private Entity? FindTaggedInfo(Entity owner, Entity target)
    {
        foreach (var t in _trackers)
            if (!t.IsFreed && ReferenceEquals(t.Owner, owner) && ReferenceEquals(t.Enemy, target))
                return t;
        return null;
    }

    // W_Seeker_Fire_Tag — fire a tag dart that marks the player it hits for a homing-missile volley. seeker.qc
    private void FireTag(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Tag.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-2, -2, -2), new Vector3(2, 2, 2), recoil: 2f);

        Entity missile = Api.Entities.Spawn();
        missile.ClassName = "seeker_tag";
        missile.Owner = actor;
        missile.NetName = NetName;
        missile.MoveType = MoveType.Fly;
        missile.Solid = Solid.BBox;
        missile.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(missile, new Vector3(-2, -2, -2), new Vector3(2, 2, 2));
        Api.Entities.SetOrigin(missile, shot.Origin);

        missile.TakeDamage = DamageMode.Yes; // shootable
        missile.Health = Tag.Health;
        missile.DamageForceScale = Tag.DamageForceScale;

        // W_SetupProjVelocity_PRE(tag_): velocity = w_shotdir * speed (with tag_spread, normally 0).
        missile.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Tag.Speed, 0f, 0f, Tag.Spread);
        missile.Angles = QMath.VecToAngles(missile.Velocity);

        // QC seeker.qc:498-499 — flag the tag missile as a dodgeable hazard (rating = hardcoded 50).
        missile.BotDodge = true;
        missile.BotDodgeRating = 50f;

        missile.Touch = (self, other) => TagTouch(self, other, slot);
        missile.Think = self => Api.Entities.Remove(self);     // SUB_Remove at lifetime
        // W_Seeker_Tag_Damage -> W_Seeker_Tag_Explode: the shot-down dart plays the HITTYPE_BOUNCE tag_impact cue
        // (the CSQC wr_impacteffect bounce branch) then deletes.
        missile.ProjectileDamage = (self, attacker) =>
        {
            WeaponSplash.ImpactSound(self, "weapons/tag_impact.wav"); // SND_TAG_IMPACT (wr_impacteffect bounce)
            Api.Entities.Remove(self);
        };
        missile.NextThink = Api.Clock.Time + Tag.Lifetime;

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (seeker.qc W_Seeker_Fire_Tag).
        var ep = new MutatorHooks.EditProjectileArgs(actor, missile);
        MutatorHooks.EditProjectile.Call(ref ep);

        // W_Seeker_Tag_Damage shoot-down (W1-projectile-net): route RadiusDamage onto ProjectileDamage so a
        // player can shoot the dart down. No exception, matching Base W_CheckProjectileDamage (implicit -1).
        Projectiles.MakeShootable(missile, exception: -1f);

        Api.Sound.Play(actor, SoundChannel.WeaponAuto, "weapons/tag_fire.wav");
    }

    // W_Seeker_Tag_Touch — on a player, spawn a tag tracker whose volley controller fires missile_count
    // homing missiles at the tagged target over time (missile_delay between shots). seeker.qc
    private void TagTouch(Entity self, Entity other, WeaponSlot slot)
    {
        // W_Seeker_Tag_Touch: emit the tag-strike cue (the CSQC wr_impacteffect HITTYPE_BOUNCE|SECONDARY tag_impact
        // sound) and the te_knightspike point-effect spark (Base: te_knightspike(findbetterlocation(this.origin,8));
        // the port emits at self.Origin — findbetterlocation's nudge is cosmetic, not ported separately).
        WeaponSplash.ImpactSound(self, "weapons/tag_impact.wav");
        // QC te_knightspike(org2): a DP builtin that fires the TE_KNIGHTSPIKE point-effect (a decal + a shower of
        // 128 reddish-orange sparks; effectinfo.txt). This is a DISTINCT effect from the TR_KNIGHTSPIKE *trail*
        // (a moving-entity particle trail) — emit the point burst, not the trail. Base nudges the origin via
        // findbetterlocation(this.origin, 8) (push 8u off the nearest surface); that nudge is cosmetic and not
        // ported separately, so the burst is emitted at the touch origin.
        EffectEmitter.Emit("TE_KNIGHTSPIKE", self.Origin);

        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;
        if (hitPlayer && self.Owner is not null && other.DeadState == DeadFlag.No)
        {
            Entity owner = self.Owner;

            // W_Seeker_Tagged_Info: if this owner already tags this target, refresh the existing tracker's expiry
            // instead of spawning a second volley controller / tracker (which would double the barrage).
            Entity? existing = FindTaggedInfo(owner, other);
            if (existing is not null)
            {
                if (Type == 1)
                {
                    existing.MaxHealth = Api.Clock.Time + Tag.TrackerLifetime; // refresh tag_time
                    // type 1: kill the old WP_Seeker sprite and re-spawn a fresh one with a new lifetime
                    // (QC seeker.qc:449: "don't attach another waypointsprite without killing the old one first").
                    if (_tagWaypoints.TryGetValue(other, out Waypoints.WaypointSprite? oldSprite))
                    {
                        Waypoints.WaypointSprites.Kill(oldSprite);
                        _tagWaypoints.Remove(other);
                    }
                    Waypoints.WaypointSprite refreshed = Waypoints.WaypointSprites.Spawn(
                        "Seeker", Tag.TrackerLifetime, 0f,
                        other, new Vector3(0f, 0f, 64f), Vector3.Zero,
                        0, Waypoints.WaypointRegistry.Get("Seeker").Color,
                        1 /* RADARICON_TAGGED */, Waypoints.SpriteRule.Default, hideable: true);
                    Waypoints.WaypointSprites.UpdateRule(refreshed, 0, Waypoints.SpriteRule.Default);
                    _tagWaypoints[other] = refreshed;
                }
                Api.Entities.Remove(self);
                return;
            }

            if (Type == 1)
            {
                // type 1: the tag just registers a tracker the primary fires missiles at (no auto-volley).
                // Spawn a WP_Seeker waypoint sprite that follows the tagged player (RADARICON_TAGGED, tag_tracker_lifetime).
                // QC: WaypointSprite_Spawn(WP_Seeker, tag_tracker_lifetime, 0, toucher, '0 0 64', realowner, 0,
                //       toucher, wps_tag_tracker, true, RADARICON_TAGGED) + WaypointSprite_UpdateRule(…, SPRITERULE_DEFAULT).
                Waypoints.WaypointSprite wp = Waypoints.WaypointSprites.Spawn(
                    "Seeker", Tag.TrackerLifetime, 0f,
                    other, new Vector3(0f, 0f, 64f), Vector3.Zero,
                    0, Waypoints.WaypointRegistry.Get("Seeker").Color,
                    1 /* RADARICON_TAGGED */, Waypoints.SpriteRule.Default, hideable: true);
                Waypoints.WaypointSprites.UpdateRule(wp, 0, Waypoints.SpriteRule.Default);
                _tagWaypoints[other] = wp;

                Entity tracker = Api.Entities.Spawn();
                tracker.ClassName = "tag_tracker";
                tracker.Owner = owner;
                tracker.Enemy = other;
                tracker.MaxHealth = Api.Clock.Time + Tag.TrackerLifetime; // tracker expiry
                _trackers.Add(tracker);
                tracker.Think = t =>
                {
                    // W_Seeker_Tracker_Think (seeker.qc:388-405): suicide if the owner dies, the tagged target
                    // dies, the owner switches away from the seeker, OR the tracker lifetime is up. QC also kills
                    // toucher.wps_tag_tracker (the sprite); the port kills via _tagWaypoints.
                    Entity? own = t.Owner;
                    if (t.Enemy is null || t.Enemy.DeadState != DeadFlag.No
                        || own is null || own.DeadState != DeadFlag.No || own.ActiveWeaponId != RegistryId
                        || Api.Clock.Time > t.MaxHealth)
                    {
                        if (t.Enemy is not null && _tagWaypoints.TryGetValue(t.Enemy, out Waypoints.WaypointSprite? spr))
                        {
                            Waypoints.WaypointSprites.Kill(spr);
                            _tagWaypoints.Remove(t.Enemy);
                        }
                        _trackers.Remove(t);
                        Api.Entities.Remove(t);
                        return;
                    }
                    t.NextThink = Api.Clock.Time;
                };
                tracker.NextThink = Api.Clock.Time;
            }
            else
            {
                // type 0: spawn a volley controller that fires missile_count missiles, missile_delay apart.
                Entity ctrl = Api.Entities.Spawn();
                ctrl.ClassName = "tag_tracker";
                ctrl.Owner = owner;
                ctrl.Enemy = other;
                ctrl.Count = (int)MathF.Max(1f, Missile.Count); // shots remaining
                _trackers.Add(ctrl);
                ctrl.Think = c => VolleyControllerThink(c, slot);
                ctrl.NextThink = Api.Clock.Time;
            }
        }
        Api.Entities.Remove(self);
    }

    // W_Seeker_Vollycontroller_Think — fire one missile per tick at the tagged target until the count runs out.
    private void VolleyControllerThink(Entity ctrl, WeaponSlot slot)
    {
        --ctrl.Count;
        Entity? owner = ctrl.Owner;
        // QC self-destruct: out-of-ammo (unless IT_UNLIMITED_AMMO) OR count<=-1 OR owner dead OR owner switched
        // away from the seeker. (Base does NOT stop if the tagged target dies mid-barrage, and does NOT require a
        // live target — the missiles just coast; so there is intentionally no enemy-dead/null gate here.)
        bool unlimited = owner is not null && (owner.UnlimitedAmmo || (owner.Items & (1 << 0)) != 0);
        bool outOfAmmo = owner is not null && !unlimited && owner.GetResource(AmmoType) < Missile.Ammo;
        if (owner is null || ctrl.Count < 0 || owner.DeadState != DeadFlag.No
            || outOfAmmo || owner.ActiveWeaponId != RegistryId)
        {
            _trackers.Remove(ctrl);
            Api.Entities.Remove(ctrl);
            return;
        }
        // 4-position muzzle offset cycle, like FLAC's f_diff.
        Vector3 fdiff = (ctrl.Count & 3) switch
        {
            0 => new Vector3(-1.25f, -3.75f, 0f),
            1 => new Vector3(1.25f, -3.75f, 0f),
            2 => new Vector3(-1.25f, 3.75f, 0f),
            _ => new Vector3(1.25f, 3.75f, 0f),
        };
        FireMissile(owner, slot, fdiff, ctrl.Enemy);
        // QC nextthink = time + missile_delay * W_WeaponRateFactor(realowner).
        ctrl.NextThink = Api.Clock.Time + Missile.Delay * WeaponRateFactor(owner);
    }

    // QC the Seeker refire/animtime are sub-weapon-specific (missile/tag/flac), and which sub-weapon a mode
    // uses depends on g_balance_seeker_type: type 1 -> primary=missile, secondary=tag; type 0 -> primary=tag,
    // secondary=flac. Mirror that selection here (the default _primary_/_secondary_ cvar convention misses it).
    public override float RefireFor(FireMode fire)
    {
        bool secondary = fire == FireMode.Secondary;
        if (Type == 1) return secondary ? Tag.Refire : Missile.Refire;
        return secondary ? Flac.Refire : Tag.Refire;
    }
    public override float AnimtimeFor(FireMode fire)
    {
        bool secondary = fire == FireMode.Secondary;
        if (Type == 1) return secondary ? Tag.Animtime : Missile.Animtime;
        return secondary ? Flac.Animtime : Tag.Animtime;
    }

    // METHOD(Seeker, wr_checkammo1) — seeker.qc (type 0: tag; type 1: missile).
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= (Type == 1 ? Missile.Ammo : Tag.Ammo);

    // METHOD(Seeker, wr_checkammo2) — seeker.qc (type 0: flac; type 1: tag).
    public bool CheckAmmoSecondary(Entity actor)
        => actor.GetResource(AmmoType) >= (Type == 1 ? Tag.Ammo : Flac.Ammo);

    // METHOD(Seeker, wr_aim) — common/weapons/weapon/seeker.qc:wr_aim. The bot presses PRIMARY: in the default
    // type 0 that is the tag dart (lead at tag_speed) which paints the enemy and auto-launches the homing missiles;
    // in type 1 the missiles ARE the primary (lead at missile_speed). The flac/secondary is not bot-driven (the
    // homing missiles do the work), so the default primary-only press stands; this hook only supplies the per-type
    // lead speed. Non-lobbed, so the brain's straight-line lead applies.
    public override float BotAimShotSpeed(float defaultSpeed)
        => Type == 1 ? Missile.Speed : Tag.Speed;
}
