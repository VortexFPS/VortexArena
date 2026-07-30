// Port of qcsrc/common/mutators/mutator/nades/nade/translocate.qc
//   (nade_translocate_boom + nade_translocate_DestroyDamage).
//
// The translocate nade teleports the thrower to its detonation point (saving you from the void on space
// maps). It resizes itself to a big box, nudges out of solid, traces down to a clear standing spot, then
// TeleportPlayer()s the owner there carrying their (reprojected) speed. Shot to death, it instead damages the
// attacker and self-detonates as a normal explosion.
//
// The port has no move_out_of_solid builtin, so a minimal version is implemented inline (nudge upward out of
// a startsolid origin) — enough for the common case (a nade resting on the floor / lobbed into the open).
// MUTATOR_CALLHOOK(PortalTeleport) is omitted (no subscribers in the port). TeleportPlayer is the existing
// Teleporters.TeleportPlayer.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The translocate nade detonation — port of <c>nade_translocate_boom</c> + its DestroyDamage.</summary>
public sealed class NadeTranslocateBoom : INadeBoom, INadeDestroyDamage
{
    // QC PL_MIN_CONST / PL_MAX_CONST (the standing player hull).
    private static readonly Vector3 PlMin = new(-16, -16, -24);
    private static readonly Vector3 PlMax = new(16, 16, 45);

    public string NadeNetName => "translocate";

    /// <summary>
    /// Port of <c>nade_translocate_boom(entity this)</c> (translocate.qc:4): teleport the owner to the
    /// detonation point. Resize to (PL_MIN-16, PL_MAX+16), move out of solid, trace the player hull down to a
    /// clear spot, then TeleportPlayer carrying the owner's speed along their facing.
    /// </summary>
    public void Boom(Entity nade)
    {
        if (Api.Services is null) return;
        Entity owner = nade.RealOwner ?? nade;

        // QC: if (this.realowner.vehicle) return; — vehicles can't translocate.
        if (owner.Vehicle is not null) return;

        Vector3 boxMin = PlMin - new Vector3(16, 16, 16);
        Vector3 boxMax = PlMax + new Vector3(16, 16, 16);
        Api.Entities.SetSize(nade, boxMin, boxMax);

        if (!MoveOutOfSolid(nade))
            return; // QC: couldn't move out of solid -> sprint a warning + bail.

        // QC: locout = origin + '0 0 1' * (1 - realowner.mins.z - 24); tracebox down with the player hull.
        Vector3 ownerMin = owner.Mins == Vector3.Zero ? PlMin : owner.Mins;
        Vector3 ownerMax = owner.Maxs == Vector3.Zero ? PlMax : owner.Maxs;
        Vector3 locout = nade.Origin + new Vector3(0f, 0f, 1f - ownerMin.Z - 24f);
        TraceResult tb = Api.Trace.Trace(locout, ownerMin, ownerMax, locout, MoveFilter.NoMonsters, owner);
        locout = tb.EndPos;

        QMath.AngleVectors(owner.Angles, out Vector3 forward, out _, out _);
        Vector3 outVel = forward * owner.Velocity.Length();

        // Reuse the existing teleporter relocation (origin/angles/velocity + clear ground + telefrag). The nade
        // is the "teleporter" (its Owner == the realowner credits hazard kills, mirroring QC's owner=realowner).
        Teleporters.TeleportPlayer(nade, owner, locout, owner.Angles, outVel);
    }

    /// <summary>
    /// Port of <c>nade_translocate_DestroyDamage(entity this, entity attacker)</c> (translocate.qc:28): a
    /// translocate nade shot to death damages the ATTACKER and self-detonates as a normal explosion (QC
    /// W_PrepareExplosionByDamage(this, realowner, nade_boom)). Returns true so the projectile path does NOT
    /// also run the normal boom (we've already detonated it here).
    /// </summary>
    public bool DestroyDamage(Entity nade, Entity? attacker)
    {
        if (Api.Services is null) return false;
        float dmg = NadeProjectile.Cvar("g_nades_translocate_destroy_damage", 25f);
        if (nade.NadeBonusType == (NadeRegistry.Translocate?.Id ?? -1) && dmg > 0f)
        {
            Entity owner = nade.RealOwner ?? nade;
            // QC Damage(this.realowner, attacker, attacker, ...): the OWNER takes the destroy damage (the shooter
            // is the attacker/inflictor) — DEATH_TOUCHEXPLODE.
            Combat.Damage(owner, attacker, attacker, dmg, "touchexplode", owner.Origin, Vector3.Zero);

            // QC W_PrepareExplosionByDamage(this, realowner, nade_boom): keep the owner, then detonate. The nade
            // already has takedamage cleared (set by NadeProjectile.NadeDamage), so it explodes as NORMAL.
            NadeBoom.Detonate(nade);
            return true; // consumed: the caller must not run the normal boom again.
        }
        return false;
    }

    /// <summary>
    /// Minimal port of <c>move_out_of_solid(entity this)</c>: if the entity's current box isn't embedded in
    /// solid, succeed in place; otherwise nudge it straight up in small steps (the common rest-on-floor case).
    /// Returns false if no clear position is found within the search range.
    /// </summary>
    private static bool MoveOutOfSolid(Entity e)
    {
        if (Api.Services is null) return false;
        Vector3 origin = e.Origin;
        TraceResult tr = Api.Trace.Trace(origin, e.Mins, e.Maxs, origin, MoveFilter.NoMonsters, e);
        if (!tr.StartSolid)
            return true;

        // Nudge upward (DP tries the bbox-height range); 2-unit steps up to the box height.
        float step = 2f;
        float maxRise = (e.Maxs.Z - e.Mins.Z) + 16f;
        for (float dz = step; dz <= maxRise; dz += step)
        {
            Vector3 p = origin + new Vector3(0f, 0f, dz);
            TraceResult t = Api.Trace.Trace(p, e.Mins, e.Maxs, p, MoveFilter.NoMonsters, e);
            if (!t.StartSolid)
            {
                Api.Entities.SetOrigin(e, p);
                return true;
            }
        }
        return false;
    }
}
