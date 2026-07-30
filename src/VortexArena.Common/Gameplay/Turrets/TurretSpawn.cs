using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared turret spawn + projectile helpers — the headless slice of <c>turret_initialize</c> and
/// <c>turret_projectile</c> (common/turrets/sv_turrets.qc). Centralises the boilerplate every turret's
/// <see cref="Turret.Spawn"/> repeats (health/model/solid/think wiring + seeding the runtime
/// <see cref="TurretState"/> + the damage/use lifecycle hooks) and the generic projectile spawn the
/// plasma/MLRS/flac turrets share. Godot-free: operates only on <see cref="Entity"/> + <see cref="Api"/>.
/// </summary>
public static class TurretSpawn
{
    /// <summary>Detonation closures for shootable turret projectiles, keyed by entity (stands in for the QC per-edict event_damage).</summary>
    private static readonly Dictionary<Entity, Action<Entity>> _shootdown = new();
    private static bool _projDeathHooked;

    /// <summary>Detonate a shootable turret projectile that the damage pipeline destroys (FLAC vs plasma/mlrs balls).</summary>
    private static void EnsureProjectileDeathHook()
    {
        if (_projDeathHooked) return;
        _projDeathHooked = true;
        Combat.Death.Add(static (ref DeathEvent ev) =>
        {
            if (_shootdown.TryGetValue(ev.Victim, out var explode)) explode(ev.Victim);
            return false;
        });
    }

    /// <summary>
    /// Common turret entity setup (<c>turret_initialize</c>/<c>turret_respawn</c>): set className, model,
    /// hitbox, solidity, full health, seed the <see cref="TurretState"/> ammo pool + volley, and wire the
    /// activation (<c>turret_use</c>) + damage/retaliation/death (<c>turret_damage</c>/<c>turret_die</c>)
    /// lifecycle. <paramref name="respawnTime"/> drives the post-death respawn; <paramref name="noRespawn"/>
    /// makes death permanent (TSL_NO_RESPAWN). <paramref name="movable"/> lets damage shove mobile turrets
    /// (TUR_FLAG_MOVE). Skips engine/networking pieces (link, tag_head attachment, manager entity) — those are
    /// client-render / server-frame concerns owned elsewhere.
    /// </summary>
    public static TurretState Init(Turret def, Entity e, Vector3 mins, Vector3 maxs,
        float ammoMax, float ammoRecharge, int shotVolly,
        float respawnTime = 60f, bool noRespawn = false, bool movable = false, bool energyAmmo = true,
        bool headShake = false)
    {
        e.ClassName = "turret_" + def.NetName;
        e.NetName = def.NetName;
        e.MaxHealth = def.StartHealth;
        e.Health = def.StartHealth;
        e.SetResourceExplicit(ResourceType.Health, def.StartHealth);
        e.TakeDamage = DamageMode.Aim;          // QC DAMAGE_AIM
        e.Solid = Solid.BBox;                   // QC SOLID_BBOX
        e.MoveType = MoveType.Noclip;           // QC MOVETYPE_NOCLIP (fixed emplacement)
        e.DeadState = DeadFlag.No;

        // QC: turrets default to a nonzero team (FLOAT_MAX) so SAME_TEAM gating works without teamplay; they
        // are ACTIVE_ACTIVE on spawn. turret_use overrides team+active when triggered.
        if (e.Team == 0f) e.Team = float.MaxValue;

        if (Api.Services is not null)
        {
            if (def.Model is not null) Api.Entities.SetModel(e, def.Model);
            Api.Entities.SetSize(e, mins, maxs);
        }

        TurretState st = TurretAI.State(e);
        st.AmmoMax = ammoMax;
        st.AmmoRecharge = ammoRecharge;
        st.Ammo = ammoMax;
        st.AmmoIsEnergy = energyAmmo;   // QC ammo_flags & TFL_AMMO_ENERGY (fusion reactor only feeds energy recipients)
        st.VollyCounter = shotVolly > 1 ? shotVolly : 1;
        st.AttackFinished = 0f;
        st.Active = true;
        st.RespawnTime = respawnTime;
        st.NoRespawn = noRespawn;
        st.Movable = movable;
        st.HeadShake = headShake;   // QC damage_flags & TFL_DMG_HEADSHAKE
        st.IdleAim = Vector3.Zero;
        st.HeadAngles = Vector3.Zero;
        st.HeadAVelocity = Vector3.Zero;
        st.ShotOrg = TurretAI.ShotOrigin(e);

        // Lifecycle hooks (QC turret_use / turret_damage / turret_die). turret_use swaps the team+active state
        // on trigger. The turret carries its OWN .event_damage (QC turret_damage) as a GtEventDamage shim:
        // DamageSystem.EventDamage routes a non-player edict with a GtEventDamage to it and returns, so a turret
        // victim runs the pre-damage gate (inactive immunity / friendly-fire scaling / MOVE shove)
        // in TurretAI.Damage and its own health subtract — instead of being treated as a player. Lethal hits fire
        // the shared Combat.Death bus, which the EnsureDeathHook subscription (OnAnyDeath -> Die) turns into the
        // death blast + respawn schedule.
        e.Use = (self, activator) => TurretAI.Use(self, activator);
        e.GtEventDamage = TurretAI.EventDamage;

        // QC turret_initialize: this.event_heal = turret_heal (sv_turrets.qc:1338) — the framework heal SINK.
        // A friendly heal source (Arc heal-beam / heal nade / mage / bumblebee healgun) dispatches through
        // Combat.Heal -> target.GtEventHeal, so a damaged-but-alive turret can be repaired by its team toward its
        // max_health. turret_die clears this (event_heal = func_null) so a dead turret cannot be healed back up;
        // turret_respawn re-installs it (the port's Respawn re-runs Spawn-equivalent setup via this Init path).
        e.GtEventHeal = TurretAI.Heal;

        // QC turret_initialize: this.reset = turret_reset (sv_turrets.qc:1342), which runs turret_respawn — the
        // round-restart hook the round handler fires on every map entity (GameWorld.ResetMapObjects ->
        // Entity.Reset). It restores a damaged/dead turret to full setup (health/ammo/volley/head, re-active) at
        // the start of each round in round-based gametypes (CA/LMS/Freezetag/etc.) so turret state never carries
        // across rounds. (Respawn re-installs the per-frame think it recorded above, so a reset turret keeps
        // thinking.) Skip restoring turrets the descriptor marks permanent-death (TSL_NO_RESPAWN) — Base's
        // turret_reset always resets, but those are deleted on death so the question never arises.
        e.Reset = TurretAI.Respawn;

        TurretAI.EnsureDeathHook();

        return st;
    }

    /// <summary>
    /// Port of <c>turret_projectile</c> (sv_turrets.qc): spawn a generic turret missile from the muzzle along
    /// <paramref name="dir"/> at <paramref name="speed"/>, with a deterministic <c>shot_spread</c> cone
    /// (<see cref="Prandom"/>), a touch-explode that does radius damage, and (if <paramref name="health"/> &gt; 0)
    /// a shootable hull that explodes when destroyed (turret_projectile_damage). Used by the plasma / MLRS /
    /// flac / hellion / hk / ewheel turrets (each tweaks the result). Returns the projectile entity.
    ///
    /// <paramref name="projType"/> is the client trail/render type (QC's <c>_proj_type</c> arg, applied inside
    /// the helper by <c>CSQCProjectile(proj, _cli_anim, _proj_type, _cull)</c>, sv_turrets.qc:487). The port has
    /// no CSQC turret edict, so the bolt is classified by its networked <see cref="Entity.NetName"/> via the
    /// shared <c>ProjectileCatalog</c>; stamping that name IN the helper (just as Base stamps the proj_type in
    /// <c>turret_projectile</c> itself) is what gives the bolt its real trail. A turret that doesn't pass a type
    /// gets the empty Generic trail — matching Base, where a missing <c>_proj_type</c> is the generic fallback.
    /// </summary>
    public static Entity Projectile(Entity turret, Vector3 origin, Vector3 dir, float speed, float size,
        float health, float damage, float edgeDamage, float radius, float force, string deathType,
        float spread = 0f, string projType = "")
    {
        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "turret_projectile";
        // QC turret_projectile stamps the client render/trail type itself (CSQCProjectile(.., _proj_type, ..),
        // sv_turrets.qc:487). In the port the networked NetName is the catalog key, so stamp it here — the helper
        // owns the trail, not the callers. Empty -> the Generic trail (matches a missing QC _proj_type).
        if (projType.Length != 0) proj.NetName = projType;
        proj.Owner = turret;
        proj.Enemy = turret.Enemy;
        proj.MoveType = MoveType.FlyMissile;     // QC MOVETYPE_FLYMISSILE
        // QC turret_projectile PROJECTILE_MAKETRIGGER (sv_turrets.qc:477): SOLID_CORPSE + dphitcontentsmask
        // SOLID|BODY|CORPSE so the projectile is transparent to the firing turret's bbox (can't self-collide).
        Projectiles.MakeTrigger(proj);
        proj.Flags = EntFlags.Item;              // QC FL_PROJECTILE stand-in
        proj.Team = turret.Team;                 // inherit team so it doesn't read as an enemy missile to allies

        // Deterministic spread cone (QC: normalize(dir + randomvec() * shot_spread) * shot_speed, ADR-0010).
        Vector3 fireDir = QMath.Normalize(dir);
        if (spread > 0f)
            fireDir = QMath.Normalize(fireDir + Prandom.Vec() * spread);
        proj.Velocity = fireDir * speed;
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        Vector3 half = new Vector3(0.5f, 0.5f, 0.5f) * size;
        Api.Entities.SetSize(proj, -half, half);
        Api.Entities.SetOrigin(proj, origin);

        void Explode(Entity self)
        {
            self.Touch = null;
            self.Think = null;
            self.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(self, self.Origin, damage, edgeDamage, radius, self.Owner, 0, force, deathTag: deathType);
            _shootdown.Remove(self);
            Api.Entities.Remove(self);
            TurretAI.Forget(self);
        }

        if (health > 0f)
        {
            // Shootable hull (FLAC shoots these down). The headless DamageSystem has no per-entity
            // event_damage hook, so detonation-when-destroyed flows through the shared death hook
            // (turret_projectile_damage -> W_PrepareExplosionByDamage).
            proj.TakeDamage = DamageMode.Yes;
            proj.Health = health;
            proj.SetResourceExplicit(ResourceType.Health, health);
            _shootdown[proj] = Explode;
            EnsureProjectileDeathHook();
        }
        else
        {
            proj.TakeDamage = DamageMode.No;
            proj.Flags |= EntFlags.NoTarget;     // QC FL_NOTARGET
        }

        proj.Touch = (self, _) => Explode(self);
        proj.Think = Explode;                    // lifetime fallback
        proj.NextThink = (Api.Services is not null ? Api.Clock.Time : 0f) + 9f; // QC nextthink = time + 9

        return proj;
    }
}
