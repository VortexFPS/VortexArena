// Port of qcsrc/common/mutators/mutator/nades/nade/napalm.qc
//   (nade_napalm_boom + nade_napalm_ball + napalm_ball_think + napalm_fountain_think + napalm_damage).
//
// The napalm nade detonation: launch g_nades_napalm_ball_count bouncing fireballs, plus a stationary
// fountain (MOVETYPE_TOSS) that periodically ejects more fireballs and applies its own fire damage. Both the
// balls and the fountain tick napalm_damage every 0.1s: pick ONE nearby damageable target (RandomSelection
// weighted by 1/(1+d), preferring a not-yet-burning one) and set it alight via Fire_AddDamage — modelled
// here, like the Fireball weapon, as the STATUSEFFECT_Burning status effect whose per-tick burn the
// status-effects loop applies (strength = the distance-scaled damage-per-second).
//
// Render-only omissions: the EFFECT_FIREBALL_LASER trail, the EF_FLAME ball effect, the CSQC projectile.
// The water-contents velocity damping IS ported (WaterDamp, via Api.Trace.PointContents). The QC
// proj.damageforcescale (4) is an inert field on these projectiles — the balls/fountain never set
// takedamage=DAMAGE_AIM, so they can't receive damage events for that force scale to multiply (no victim
// knockback in Base either); it is intentionally not modelled.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The napalm nade detonation — port of <c>nade_napalm_boom</c>.</summary>
public sealed class NadeNapalmBoom : INadeBoom
{
    // SUPERCONTENTS_WATER (DP common contents). QC pointcontents returns CONTENT_WATER for a submerged point.
    private const int SuperContentsWater = 0x00000010;

    public string NadeNetName => "napalm";

    /// <summary>
    /// Port of the napalm.qc water-damping (napalm_ball_think:47 / napalm_fountain_think:112): if the entity's
    /// midpoint is in water, halve its velocity; if 16 units higher is still water, set upward velocity to 200
    /// (so a submerged fireball floats up rather than sinking).
    /// </summary>
    private static void WaterDamp(Entity self)
    {
        if (Api.Services is null) return;
        Vector3 midpoint = self.Origin + (self.Mins + self.Maxs) * 0.5f;
        if ((Api.Trace.PointContents(midpoint) & SuperContentsWater) != 0)
        {
            self.Velocity *= 0.5f;
            if ((Api.Trace.PointContents(midpoint + new Vector3(0f, 0f, 16f)) & SuperContentsWater) != 0)
                self.Velocity = new Vector3(self.Velocity.X, self.Velocity.Y, 200f);
        }
    }

    /// <summary>
    /// Port of <c>nade_napalm_boom(entity this)</c> (napalm.qc:134): spew the initial fireballs, then spawn
    /// the fire fountain (MOVETYPE_TOSS) that keeps ejecting balls + burning for g_nades_napalm_fountain_lifetime.
    /// </summary>
    public void Boom(Entity nade)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        int ballCount = (int)NadeProjectile.Cvar("g_nades_napalm_ball_count", 6f);
        for (int c = 0; c < ballCount; ++c)
            SpawnBall(nade);

        Entity fountain = Api.Entities.Spawn();
        fountain.ClassName = "nade_napalm_fountain";
        fountain.Owner = nade.RealOwner;
        fountain.Team = nade.Team;
        Api.Entities.SetOrigin(fountain, nade.Origin);
        Api.Entities.SetSize(fountain, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        fountain.MoveType = MoveType.Toss;
        fountain.Flags = EntFlags.Item; // QC FL_PROJECTILE
        fountain.NadeOrbExpire = now + NadeProjectile.Cvar("g_nades_napalm_fountain_lifetime", 3f); // QC .ltime
        fountain.NadeSpecialTime = now;
        fountain.Think = FountainThink;
        fountain.NextThink = now;
    }

    /// <summary>
    /// Port of <c>nade_napalm_ball(entity this)</c> (napalm.qc:64): a small MOVETYPE_BOUNCE fireball kicked
    /// out in a random direction, living g_nades_napalm_ball_lifetime seconds, burning nearby foes each tick.
    /// </summary>
    private static void SpawnBall(Entity origin)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;

        Api.Sound.Play(origin, SoundChannel.Auto, "weapons/fireball_fire.wav");

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "grenade";
        proj.Owner = origin.RealOwner;
        proj.Team = origin.Team;
        proj.MoveType = MoveType.Bounce;
        // QC PROJECTILE_MAKETRIGGER (napalm.qc:76): SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE so the
        // napalm ball is transparent to the firer's movement — can't collide with / detonate on its owner.
        Projectiles.MakeTrigger(proj);
        Api.Entities.SetSize(proj, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(proj, origin.Origin);
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        proj.Gravity = 1f;

        float spread = NadeProjectile.Cvar("g_nades_napalm_ball_spread", 500f);
        Vector3 kick = new(
            spread * Prandom.Signed(),
            spread * Prandom.Signed(),
            spread * (Prandom.Float() * 0.5f + 0.5f));
        proj.Velocity = kick;
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        proj.NadeOrbExpire = now + NadeProjectile.Cvar("g_nades_napalm_ball_lifetime", 7f); // QC .pushltime
        proj.Think = BallThink;
        proj.NextThink = now;
    }

    /// <summary>Port of <c>napalm_ball_think</c> (napalm.qc:38): burn nearby foes, retick, expire at pushltime.</summary>
    private static void BallThink(Entity self)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        // QC napalm_ball_think:40 — (round_handler_IsActive() && !round_handler_IsRoundStarted()) || time > pushltime.
        if (RoundHandler.RoundGateBlocks() || now > self.NadeOrbExpire)
        {
            Api.Entities.Remove(self);
            return;
        }
        WaterDamp(self); // QC napalm_ball_think:47 — halve/float velocity while submerged.
        self.Angles = QMath.VecToAngles(self.Velocity);
        NapalmDamage(self,
            NadeProjectile.Cvar("g_nades_napalm_ball_radius", 100f),
            NadeProjectile.Cvar("g_nades_napalm_ball_damage", 40f),
            NadeProjectile.Cvar("g_nades_napalm_ball_damage", 40f),
            NadeProjectile.Cvar("g_nades_napalm_burntime", 0.5f));
        self.NextThink = now + 0.1f;
    }

    /// <summary>
    /// Port of <c>napalm_fountain_think</c> (napalm.qc:103): each 0.1s apply fountain fire damage and, every
    /// g_nades_napalm_fountain_delay, eject another fireball; expire at <c>ltime</c>.
    /// </summary>
    private static void FountainThink(Entity self)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        // QC napalm_fountain_think:105 — (round_handler_IsActive() && !round_handler_IsRoundStarted()) || time >= ltime.
        if (RoundHandler.RoundGateBlocks() || now >= self.NadeOrbExpire)
        {
            Api.Entities.Remove(self);
            return;
        }

        WaterDamp(self); // QC napalm_fountain_think:112 — same water damping as the balls.

        NapalmDamage(self,
            NadeProjectile.Cvar("g_nades_napalm_fountain_radius", 130f),
            NadeProjectile.Cvar("g_nades_napalm_fountain_damage", 50f),
            NadeProjectile.Cvar("g_nades_napalm_fountain_edgedamage", 20f),
            NadeProjectile.Cvar("g_nades_napalm_burntime", 0.5f));

        self.NextThink = now + 0.1f;
        if (now >= self.NadeSpecialTime)
        {
            self.NadeSpecialTime = now + NadeProjectile.Cvar("g_nades_napalm_fountain_delay", 0.5f);
            SpawnBall(self);
        }
    }

    /// <summary>
    /// Port of <c>napalm_damage(entity this, dist, damage, edgedamage, burntime)</c> (napalm.qc:4): of every
    /// damageable target in <paramref name="dist"/> that isn't the owner (unless napalm_selfdamage), isn't a
    /// teammate, and isn't frozen, pick ONE via RandomSelection (weight 1/(1+d), preferring not-yet-burning),
    /// and set it alight for <paramref name="burnTime"/> seconds at a distance-scaled damage-per-second. The
    /// QC random "impact vector" (a random point in the target bbox) is reproduced via <see cref="Prandom"/>.
    /// </summary>
    private static void NapalmDamage(Entity self, float dist, float damage, float edgeDamage, float burnTime)
    {
        if (Api.Services is null || damage < 0f) return;
        var burning = StatusEffectsCatalog.Burning;
        if (burning is null) return;

        Entity? owner = self.RealOwner;
        bool selfDamage = NadeProjectile.Cvar("g_nades_napalm_selfdamage", 1f) != 0f;

        Entity? chosen = null;
        Vector3 chosenImpact = Vector3.Zero;
        float bestWeight = -1f;
        bool chosenBurning = true;

        foreach (Entity e in Api.Entities.FindInRadius(self.Origin, dist).ToList())
        {
            if (e.TakeDamage != DamageMode.Aim) continue;
            if (ReferenceEquals(owner, e) && !selfDamage) continue;
            bool isPlayer = (e.Flags & EntFlags.Client) != 0;
            // QC: (!IS_PLAYER(e) || !this.realowner || DIFF_TEAM(e, this)) — players are only hit if they're
            // not the thrower's team (the fountain/ball carry the thrower's team).
            if (isPlayer && owner is not null && !NadeOrbHelper.DiffTeam(e, self)) continue;
            if (IsFrozen(e)) continue;

            Vector3 p = e.Origin + new Vector3(
                e.Mins.X + Prandom.Float() * (e.Maxs.X - e.Mins.X),
                e.Mins.Y + Prandom.Float() * (e.Maxs.Y - e.Mins.Y),
                e.Mins.Z + Prandom.Float() * (e.Maxs.Z - e.Mins.Z));
            float d = (self.Origin - p).Length();
            if (d >= dist) continue;

            bool isBurning = StatusEffectsCatalog.Has(e, burning);
            float weight = 1f / (1f + d);
            bool better = (!isBurning && chosenBurning) || (isBurning == chosenBurning && weight > bestWeight);
            if (chosen is null || better)
            {
                chosen = e; chosenImpact = p; bestWeight = weight; chosenBurning = isBurning;
            }
        }

        if (chosen is null) return;

        float dd = (self.Origin - chosenImpact).Length();
        dd = damage + (edgeDamage - damage) * (dd / dist);
        // QC napalm.qc:32: Fire_AddDamage(chosen, this.realowner, d * burntime, burntime, this.projectiledeathtype)
        // where projectiledeathtype = DEATH_NADE_NAPALM. Port: route through FireAddDamage so the LEMMA merge
        // applies (stacked napalm burns combine correctly) and the burn ticks carry the nade_napalm deathtype
        // for proper obituary attribution instead of the generic "fire" fallback.
        StatusEffectsCatalog.FireAddDamage(chosen, owner ?? self, dd * burnTime, burnTime, NadeDeathTypes.Napalm);
    }

    private static bool IsFrozen(Entity e)
    {
        var fz = StatusEffectsCatalog.Frozen;
        return (fz is not null && StatusEffectsCatalog.Has(e, fz)) || e.FrozenStat != 0;
    }
}
