// Port of qcsrc/common/mutators/mutator/nades/sv_nades.qc — the thrown-nade PHYSICS + lifecycle:
//   toss_nade (321), nade_touch (192), nade_beep (239), nade_damage (246), nade_pickup (179),
//   nade_timer_think (27, render-only — omitted).
//
// The held-nade priming/charge (spawn_held_nade / nade_prime / nades_CheckThrow) lives in NadeThrow.cs;
// this file is what happens once a nade leaves the hand: a MOVETYPE_BOUNCE FL_PROJECTILE that bounces,
// can be shot (event_damage → nade_damage → nade_boom), can be picked up by another player, and detonates
// on a non-owner solid impact. Modelled on the Mortar projectile pattern (Touch/Think) but routing damage
// through the live GtEventDamage seam so nade_damage gets the full (attacker/deathtype/damage/force) args.
//
// Detonation itself is NadeBoom.Detonate (the type dispatcher). The one deferred interaction: a PURE-FORCE,
// zero-damage hit (the blaster launch) doesn't reach event_damage in this port (the DamageSystem only
// dispatches event_damage when damage>0 or the target has a non-zero damageforcescale, and the nade keeps
// damageforcescale 0 to avoid a double knockback) — so blaster-launching a nade is a documented gap; every
// damage-dealing hit (vortex/shotgun/machinegun launch + shoot-down-to-detonate) is faithful.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades;

/// <summary>The thrown-nade projectile — its launch (<see cref="Toss"/>) and touch/shot-down/pickup behaviour.</summary>
public static class NadeProjectile
{
    // mutators.cfg defaults (the ones the projectile path reads).
    private const float DefHealth = 25f;          // g_nades_nade_health
    private const float DefRefire = 6f;           // g_nades_nade_refire
    private const float DefPickupTime = 2f;       // g_nades_pickup_time

    /// <summary>
    /// Port of <c>toss_nade(entity e, bool set_owner, vector _velocity, float _time)</c> (sv_nades.qc:321):
    /// detach the held nade from <paramref name="thrower"/>, place it at the throw origin, give it the
    /// MOVETYPE_BOUNCE projectile physics + shootable health, set its launch velocity per the newton-style
    /// cvar, and schedule its detonation (immediately at <paramref name="time"/> if non-zero, else it lives
    /// out its primed timer / explodes on impact). Mirrors the QC field setup.
    /// </summary>
    public static void Toss(Entity thrower, bool setOwner, Vector3 velocity, float time)
    {
        if (thrower.Nade is null) return;
        if (Api.Services is null) return;

        Entity nade = thrower.Nade;
        thrower.Nade = null;

        if (thrower.FakeNade is not null)
        {
            Api.Entities.Remove(thrower.FakeNade);
            thrower.FakeNade = null;
        }

        // QC: makevectors(e.v_angle); W_SetupShot(...); origin = w_shotorg + throw_offset. The headless muzzle
        // is the thrower eye + the configured throw offset rotated into the view basis.
        Vector3 viewAngles = thrower.ViewAngles == Vector3.Zero ? thrower.Angles : thrower.ViewAngles;
        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 off = ThrowOffset();
        Vector3 origin = thrower.Origin + thrower.ViewOfs + forward * off.X + right * off.Y + up * off.Z;
        Api.Entities.SetOrigin(nade, origin);

        // QC: size 16 (small) or 32, MOVETYPE_BOUNCE.
        float size = thrower.NadesSmall ? 16f : 32f;
        Vector3 half = new Vector3(0.5f, 0.5f, 0.5f) * size;
        Api.Entities.SetSize(nade, -half, half);
        nade.MoveType = MoveType.Bounce;

        // tracebox at the origin: if it would start in solid, drop it on the thrower's origin (QC startsolid).
        TraceResult tb = Api.Trace.Trace(origin, nade.Mins, nade.Maxs, origin, MoveFilter.NoMonsters, nade);
        if (tb.StartSolid)
            Api.Entities.SetOrigin(nade, thrower.Origin);

        // QC velocity selection: straight-up dribble while looking up + crouching, else newton-style.
        int newton = (int)Cvar("g_nades_nade_newton_style", 0f);
        bool lookingUpCrouch = viewAngles.X >= 70f && viewAngles.X <= 110f && thrower.ButtonCrouch;
        if (lookingUpCrouch)
            nade.Velocity = new Vector3(0f, 0f, 100f);
        else if (newton == 1)
            nade.Velocity = thrower.Velocity + velocity;
        else if (newton == 2)
            nade.Velocity = velocity;
        else
            // QC W_CalculateProjectileVelocity(e, e.velocity, _velocity, true): newton style 0 = absolute,
            // i.e. just the throw velocity (the port's ProjectileVelocity with no inheritance).
            nade.Velocity = velocity;

        if (setOwner)
            nade.Owner = thrower; // QC realowner = e (RealOwner aliases Owner)

        nade.Touch = OnTouch;
        nade.NadePickupShieldTime = Now() + 0.1f; // prevent instantly picking up again
        nade.SetResource(ResourceType.Health, Cvar("g_nades_nade_health", DefHealth));
        nade.MaxHealth = nade.GetResource(ResourceType.Health);
        nade.TakeDamage = DamageMode.Aim;
        // QC: this.event_damage = nade_damage. The DamageSystem dispatches a non-player damageable entity's
        // event_damage via GtEventDamage (the live seam), giving us the full (inflictor/attacker/deathtype/
        // damage/force) args QC's nade_damage needs. We keep DamageForceScale at 0 so the generic knockback
        // no-ops and nade_damage does the QC manual `velocity += force` itself (no double push).
        nade.GtEventDamage = NadeDamage;
        nade.Gravity = 1f;
        nade.Angles = QMath.VecToAngles(nade.Velocity);
        nade.Flags = EntFlags.Item; // QC FL_PROJECTILE
        nade.NadeTossTime = Now();
        // QC PROJECTILE_MAKETRIGGER (sv_nades.qc:347): SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE. The
        // SOLID_CORPSE alone (above) left the dphitcontentsmask unset, so the thrown nade is transparent to the
        // thrower's movement but its OWN trace still needs the explicit mask to keep hitting bodies/corpses.
        Projectiles.MakeTrigger(nade);

        if (time != 0f)
        {
            nade.Think = NadeBoom.Detonate;
            nade.NextThink = time;
        }

        // QC: e.nade_refire = time + autocvar_g_nades_nade_refire; STAT(NADE_TIMER, e) = 0;
        thrower.NadeRefire = Now() + Cvar("g_nades_nade_refire", DefRefire);
        thrower.NadeTimer = 0f;
    }

    /// <summary>
    /// Port of <c>nade_touch(entity this, entity toucher)</c> (sv_nades.qc:192): owner pass-through, optional
    /// pickup by another live player, bounce sound at full health, else detonate. (The needkill content-trace
    /// and CSQC update are render/engine concerns; the gameplay core is the pickup + impact-detonate.)
    /// </summary>
    public static void OnTouch(Entity nade, Entity toucher)
    {
        if (ReferenceEquals(toucher, nade.Owner))
            return; // no owner impacts

        // pickup: g_nades_pickup, past the re-pickup shield, toucher has no nade, nade is at full (unshot)
        // health, the toucher can throw a nade, and is a real client.
        if (Cvar("g_nades_pickup", 0f) != 0f
            && Now() >= nade.NadePickupShieldTime
            && toucher.Nade is null
            && nade.GetResource(ResourceType.Health) == nade.MaxHealth
            && NadeThrow.CanThrowNade(toucher)
            && (toucher.Flags & EntFlags.Client) != 0)
        {
            Pickup(toucher, nade);
            if (Api.Services is not null)
                Api.Sound.Play(nade, SoundChannel.Auto, "misc/null.wav");
            Api.Entities.Remove(nade);
            return;
        }

        // QC: a full-health nade just bounces (with a bounce sound); a damaged one detonates on contact.
        if (nade.GetResource(ResourceType.Health) == nade.MaxHealth)
        {
            if (Api.Services is not null)
                // QC spamsound(this, CH_SHOTS, SND_GRENADE_BOUNCE_RANDOM(), VOL_BASE, ATTEN_NORM): pick one
                // of grenade_bounce1..6 at random (SND_GRENADE_BOUNCE1.m_id + rint(random()*5)). spamsound
                // rate-limits to once per sim step via e.spamtime so a multi-solid-touch on the same frame
                // doesn't over-emit. CH_SHOTS (-4) is the stacking auto channel, NOT CH_SHOTS_SINGLE (4).
                SoundSystem.SpamSoundRaw(nade, $"weapons/grenade_bounce{Prandom.RangeInt(1, 7)}.wav",
                    Api.Clock.Time, SoundChannel.ShotsAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
            return;
        }

        nade.Enemy = toucher;
        NadeBoom.Detonate(nade);
    }

    /// <summary>
    /// Port of <c>nade_beep(entity this)</c> (sv_nades.qc:239): the pre-detonation beep, then schedule the
    /// boom at the nade's wait time. Used by spawn_held_nade as the held nade's think.
    /// </summary>
    public static void Beep(Entity nade)
    {
        if (Api.Services is not null)
            Api.Sound.Play(nade, SoundChannel.Auto, "overkill/grenadebip.ogg");
        nade.Think = NadeBoom.Detonate;
        // QC: this.nextthink = max(this.wait, time) — the boom fires at the nade's wait (lifetime) deadline.
        nade.NextThink = MathF.Max(nade.NadeWait, Now());
    }

    /// <summary>
    /// Port of <c>nade_damage</c> (sv_nades.qc:246) — the nade's event_damage (installed on GtEventDamage).
    /// Translocate/spawn nades ignore damage (can't be launched across the map). Otherwise the per-weapon
    /// force-launch interactions (blaster ×1.5 force, vortex/vaporizer big launch + chunk damage, machinegun/
    /// shotgun chip) adjust damage+force; the force is added to the nade's velocity; a first hit at full
    /// health arms the beep + extends the fuse; HP is subtracted; at HP&lt;=0 the spawn/translocate
    /// DestroyDamage handlers run (which may suppress the boom) before the nade detonates.
    /// </summary>
    private static void NadeDamage(Entity nade, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        // QC: translocate/spawn nades can't be launched (prevents teleporting/spawn-moving across the map).
        if (nade.NadeBonusType == (NadeRegistry.Translocate?.Id ?? -1)
            || nade.NadeBonusType == (NadeRegistry.Spawn?.Id ?? -1))
            return;

        // QC adjusts damage & force per attacking weapon. The blaster launches with no damage;
        // vortex/vaporizer launch hard + chunk; machinegun/shotgun chip. The deathtype carries the
        // weapon NetName (DeathTypes.WeaponNetNameOf).
        string wep = Damage.DeathTypes.WeaponNetNameOf(deathType);
        bool secondary = Damage.DeathTypes.HasHitType(deathType, Damage.DeathTypes.Secondary);

        // QC sv_nades.qc:259-277 is a single if/else-if chain headed by the Nade_Damage mutator hook:
        //   if (MUTATOR_CALLHOOK(Nade_Damage, ...)) {}        // hook overrode damage/force -> skip defaults
        //   else if (BLASTER) { force *= 1.5; damage = 0; }
        //   else if (VORTEX|VAPORIZER|OVERKILL_NEX) { ... }
        //   else if (MACHINEGUN|OVERKILL_MACHINEGUN) { ... }
        //   else if (SHOTGUN|OVERKILL_SHOTGUN) { ... }
        // So the hook fires FIRST (on the un-adjusted force/damage) and, when it returns true, the
        // per-weapon defaults are skipped entirely. okhmg_nadesupport (OverkillMutator.OnNadeDamage)
        // returns true for the OK HMG and rewrites damage to max_health*0.1.
        var nadeDamageArgs = new MutatorHooks.NadeDamageArgs(nade, wep, damage, force);
        if (MutatorHooks.FireNadeDamage(ref nadeDamageArgs))
        {
            // A mutator consumed the hook (returned true): use its adjusted damage/force, skip defaults.
            damage = nadeDamageArgs.Damage;
            force = nadeDamageArgs.Force;
        }
        else
        {
            switch (wep)
            {
                case "blaster":
                    force *= 1.5f; damage = 0f; break;
                case "vortex":
                case "vaporizer":
                case "oknex": // QC WEP_OVERKILL_NEX (NetName "oknex")
                    force *= 6f; damage = nade.MaxHealth * 0.55f; break;
                case "machinegun":
                case "okmachinegun":
                    damage = nade.MaxHealth * 0.1f; break;
                case "shotgun":
                case "okshotgun":
                    if (!secondary) damage = nade.MaxHealth * 1.15f; break;
            }
        }

        // QC melee slaps (sv_nades.qc:279-285): a weapon whose attack is flagged WEP_TYPE_MELEE_PRI (primary)
        // or WEP_TYPE_MELEE_SEC (secondary, e.g. the Shotgun's melee bash) slaps the nade hard (force ×10) and
        // chips it for 10% of its max health.
        if (Weapons.ByName(wep) is { } deathWep)
        {
            int meleeFlag = secondary ? WeaponFlags.TypeMeleeSec : WeaponFlags.TypeMeleePri;
            if ((deathWep.SpawnFlags & meleeFlag) != 0)
            {
                force *= 10f;
                damage = nade.MaxHealth * 0.1f;
            }
        }

        // QC: this.velocity += force (the nade is pushed; DamageForceScale is 0 so the generic knockback didn't).
        nade.Velocity += force;

        // QC: damage <= 0 (pure-force launch) or grounded-and-hit-by-a-player -> no health loss.
        if (damage <= 0f || (nade.OnGround && attacker is not null && (attacker.Flags & EntFlags.Client) != 0))
            return;

        float hp = nade.GetResource(ResourceType.Health);
        if (hp == nade.MaxHealth)
        {
            // first damage at full health: arm the beep + extend the fuse to the full lifetime.
            nade.NextThink = MathF.Max(Now() + nade.NadeLifetime, Now());
            nade.Think = Beep;
        }

        hp -= damage;
        nade.SetResource(ResourceType.Health, hp);

        // QC: credit the shooter (except for translocate/spawn, already returned above).
        if (attacker is not null && (attacker.Flags & EntFlags.Client) != 0)
            nade.Owner = attacker;

        if (hp <= 0f)
        {
            nade.TakeDamage = DamageMode.No;
            // QC: spawn/translocate DestroyDamage run first; if either consumed the destruction, don't boom.
            if (DestroyDamageFor(NadeRegistry.Spawn, nade, attacker)) return;
            if (DestroyDamageFor(NadeRegistry.Translocate, nade, attacker)) return;
            NadeBoom.Detonate(nade);
        }
    }

    /// <summary>Look up a nade type's <see cref="INadeDestroyDamage"/> boom (part B) and run it, if present.</summary>
    private static bool DestroyDamageFor(NadeDef? def, Entity nade, Entity? attacker)
    {
        if (def is null) return false;
        return NadeBoomRegistry.Get(def.NetName) is INadeDestroyDamage dd && dd.DestroyDamage(nade, attacker);
    }

    /// <summary>
    /// Port of <c>nade_pickup(entity this, entity thenade)</c> (sv_nades.qc:179): hand the toucher a fresh
    /// held nade of the picked-up type (with a short fuse), and arm their refire so they can't double up.
    /// </summary>
    private static void Pickup(Entity picker, Entity thenade)
    {
        NadeThrow.SpawnHeldNade(picker, thenade.Owner, Cvar("g_nades_pickup_time", DefPickupTime),
            (NadeRegistry.ById(thenade.NadeBonusType) ?? NadeRegistry.Null).NetName, thenade.PokenadeType);

        picker.NadeRefire = Now() + Cvar("g_nades_nade_refire", DefRefire);
        picker.NadeTimer = 0f;

        if (picker.Nade is not null)
            picker.Nade.NadeTimePrimed = thenade.NadeTimePrimed;
    }

    /// <summary>QC <c>autocvar_g_nades_throw_offset</c> (default "0 -25 0").</summary>
    internal static Vector3 ThrowOffset()
    {
        if (Api.Services is null) return new Vector3(0f, -25f, 0f);
        string s = Api.Cvars.GetString("g_nades_throw_offset");
        if (string.IsNullOrEmpty(s)) return new Vector3(0f, -25f, 0f);
        string[] p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) return new Vector3(0f, -25f, 0f);
        static float F(string x) => float.TryParse(x, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
        return new Vector3(F(p[0]), F(p[1]), F(p[2]));
    }

    internal static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    private static float Now() => Api.Services is not null ? Api.Clock.Time : 0f;
}

/// <summary>
/// Optional extension a boom handler implements when its nade has special shoot-down behaviour
/// (QC <c>nade_spawn_DestroyDamage</c> / <c>nade_translocate_DestroyDamage</c>). The spawn nade damages the
/// owner; the translocate nade damages the attacker and self-detonates (returning true to suppress the
/// normal boom). Part B's spawn/translocate boom files implement this alongside <see cref="INadeBoom"/>.
/// </summary>
public interface INadeDestroyDamage
{
    /// <summary>
    /// QC <c>nade_&lt;type&gt;_DestroyDamage(this, attacker)</c>: react to the nade being shot to death.
    /// Returns true if it consumed the destruction (the caller must NOT then run the normal boom).
    /// </summary>
    bool DestroyDamage(Entity nade, Entity? attacker);
}
