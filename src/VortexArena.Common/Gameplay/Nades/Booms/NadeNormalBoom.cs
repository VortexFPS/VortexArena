// Port of qcsrc/common/mutators/mutator/nades/nade/normal.qc (nade_normal_boom).
//
// The normal nade explosion: a single RadiusDamage at the nade's origin. This is also the FALLBACK boom
// the dispatcher routes to for the Null/random sentinel and for any nade destroyed before it detonated
// (sv_nades.qc nade_boom:121-126), and the ice/darkness "explode again" option (g_nades_ice_explode /
// g_nades_darkness_explode) reuses nade_normal_boom — so the actual radius-damage helper here is shared
// across the other boom files via <see cref="NadeBlast.RadiusDamage"/>.
//
// QC calls RadiusDamage(this, this.realowner, damage, edgedamage, radius, this, NULL, force,
// this.projectiledeathtype, DMG_NOWEP, this.enemy). The port's WeaponSplash.RadiusDamage only accepts an
// INT weapon-id deathtype (mapped to a weapon NetName), but a nade's deathtype is a NON-weapon string tag
// (NadeDeathTypes.Nade). To keep the obituary faithful we route through Damage.Combat.Damage directly with
// the nade's string deathtype, replicating WeaponSplash's falloff/knockback math (which itself ports
// RadiusDamageForSource). This file deliberately does NOT edit WeaponSplash.cs (not owned by this task).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The normal nade detonation — port of <c>nade_normal_boom</c>.</summary>
public sealed class NadeNormalBoom : INadeBoom
{
    public string NadeNetName => "normal";

    /// <summary>
    /// Port of <c>nade_normal_boom(entity this)</c>: RadiusDamage at the nade origin with the normal-nade
    /// balance (g_nades_nade_damage/edgedamage/radius/force, mutators.cfg:209-212). Credit goes to the nade's
    /// realowner; the direct-hit entity is the nade's <c>.enemy</c> (the toucher that triggered the boom).
    /// </summary>
    public void Boom(Entity nade)
    {
        NadeBlast.RadiusDamage(nade,
            NadeProjectile.Cvar("g_nades_nade_damage", 225f),
            NadeProjectile.Cvar("g_nades_nade_edgedamage", 90f),
            NadeProjectile.Cvar("g_nades_nade_radius", 300f),
            NadeProjectile.Cvar("g_nades_nade_force", 650f));
    }
}

/// <summary>
/// The shared nade blast helper — a faithful copy of <see cref="WeaponSplash.RadiusDamage"/>'s falloff +
/// knockback math, but routing the nade's <b>string</b> deathtype (<see cref="NadeDeathTypes"/>) through
/// <see cref="Combat.Damage"/> so the obituary names the nade (rather than degrading to a weapon/Generic
/// tag through the int-only WeaponSplash signature). Used by the normal boom and by the ice/darkness
/// "explode again" option.
/// </summary>
internal static class NadeBlast
{
    /// <summary>
    /// QC <c>RadiusDamage(this, this.realowner, dmg, edge, radius, this, NULL, force, projectiledeathtype,
    /// DMG_NOWEP, this.enemy)</c>: damage everything in <paramref name="radius"/> of the nade, interpolated
    /// from <paramref name="damage"/> at the center to <paramref name="edgeDamage"/> at the rim, with an
    /// outward knockback that tracks the same falloff. The nade's <c>.enemy</c> is the direct-hit target
    /// (skips the LOS reduction, as QC does). The nade's deathtype defaults to <see cref="NadeDeathTypes.Nade"/>.
    /// </summary>
    public static void RadiusDamage(Entity nade, float damage, float edgeDamage, float radius, float force,
        string? deathType = null)
    {
        if (Api.Services is null || radius <= 0f) return;

        string dt = deathType ?? NadeDeathTypes.Nade;
        Entity? attacker = nade.RealOwner;
        Entity? directHit = nade.Enemy;

        float throughFloorDmg = Cvar("g_throughfloor_damage", 0.5f);
        float throughFloorForce = Cvar("g_throughfloor_force", 0.7f);
        float denom = MathF.Max(damage, edgeDamage);

        foreach (Entity e in Api.Entities.FindInRadius(nade.Origin, radius).ToList())
        {
            if (e.TakeDamage == DamageMode.No) continue;

            // Nearest-point-on-bbox distance (full damage at contact range), knockback aimed at bbox center.
            Vector3 targetCenter = e.Origin + (e.Mins + e.Maxs) * 0.5f;
            Vector3 nearest = Vector3.Clamp(nade.Origin, e.Origin + e.Mins, e.Origin + e.Maxs);
            float dist = (nearest - nade.Origin).Length();
            if (dist > radius) continue;

            float frac = 1f - dist / radius;
            float finalDmg = damage * frac + edgeDamage * (1f - frac);
            if (finalDmg <= 0f) continue;

            Vector3 forceVec = Vector3.Zero;
            if (force != 0f && denom > 0f)
            {
                Vector3 dirDelta = targetCenter - nade.Origin;
                float dirLen = dirDelta.Length();
                Vector3 dir = dirLen > 0f ? dirDelta / dirLen : Vector3.UnitZ;
                forceVec = dir * ((finalDmg / denom) * force);
            }

            if (!ReferenceEquals(e, directHit))
            {
                TraceResult los = Api.Trace.Trace(nade.Origin, Vector3.Zero, Vector3.Zero, targetCenter,
                    MoveFilter.NoMonsters, nade);
                bool blocked = los.Fraction < 1f && !ReferenceEquals(los.Ent, e);
                if (blocked)
                {
                    finalDmg *= throughFloorDmg;
                    forceVec *= throughFloorForce;
                }
            }

            // self-damage scaling is applied once in DamageSystem.Apply (not here) — see WeaponSplash (DMG1).
            Combat.Damage(e, nade, attacker, finalDmg, dt, targetCenter, forceVec);
        }
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
