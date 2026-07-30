using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Map-placement spawnfuncs for the vehicle family — the thin <c>spawnfunc(vehicle_X)</c> entries each per-type
/// .qc defines (common/vehicles/vehicle/*.qc). Every one is
/// <c>spawnfunc(vehicle_X){ if (!autocvar_g_vehicle_X || !vehicle_initialize(this, VEH_X, false)) delete(this); }</c>,
/// so they all funnel through the shared setup: gate on the per-vehicle master cvar, resolve the
/// <see cref="Vehicle"/> from the <see cref="Vehicles"/> registry by net name, record the spawn point (QC
/// <c>pos1</c>/<c>pos2</c>), then run the descriptor's <see cref="Vehicle.Spawn"/> (= QC
/// <c>vehicle_initialize</c> + the deferred <c>vehicles_spawn</c>→<c>vr_spawn</c>, which itself wires the
/// per-frame think). <see cref="MapObjectsRegistry"/> registers these into <see cref="SpawnFuncs"/> so the BSP
/// entity-lump loader instantiates hand-placed vehicles on stock maps.
/// </summary>
public static class VehicleSpawnFuncs
{
    /// <summary>
    /// The shared body of every <c>spawnfunc(vehicle_X)</c>: gate on <c>g_vehicle_&lt;netName&gt;</c> (QC
    /// <c>autocvar_g_vehicle_X</c>) and the resolved type; on failure delete the edict (QC
    /// <c>delete(this); return;</c>). Otherwise record the spawn return point (QC <c>vehicle_initialize</c>:
    /// <c>pos1 = origin; pos2 = angles</c>) and run the descriptor's Spawn, which (via
    /// <see cref="VehicleCommon.SpawnVehicle"/>) reads those back and arms the per-frame think.
    /// <paramref name="netName"/> is the QC <c>info.netname</c> (after <c>vehicle_</c>).
    /// </summary>
    private static void Spawn(Entity e, string netName)
    {
        Vehicle? def = Vehicles.ByName(netName);
        // QC: if (!autocvar_g_vehicle_X || !vehicle_initialize(...)) { delete(this); return; }
        // g_vehicle_X is a boolean master switch (one per vehicle): an explicit 0 disables it, unset defaults
        // on (vehicles.cfg sets each to 1). Use the absent-vs-0 gate, not a 0-as-unset fallback.
        if (def is null || !MasterSwitchEnabled("g_vehicle_" + netName))
        {
            if (Api.Services is not null)
                Api.Entities.Remove(e);
            return;
        }

        // QC vehicle_initialize: pos1 = this.origin; pos2 = this.angles — the return point a destroyed vehicle
        // respawns at. SpawnVehicle (called by the descriptor's Spawn) reads these, so set them first.
        e.SpawnAngles = e.Angles;
        e.SpawnPos = e.Origin;

        // QC vehicle_initialize:1203-1204: clear a map-authored .team when the gametype isn't teamplay OR
        // g_vehicles_teams is off (default 1). Without this a vehicle carrying a leftover team key in a non-team
        // gametype would wrongly read as team-owned (blocking enemy boarding / steal, mis-coloring the HUD). The
        // targetname-controller branch that ASSIGNS .team is still unported, but this clamp is independent of it.
        if (e.Team != 0f && (!Scoring.GameScores.Teamplay || !TeamsEnabled()))
            e.Team = 0f;

        // QC vehicle_initialize + the deferred vehicles_spawn → vr_spawn: stamp the vehicle (model/hitbox/health/
        // shield/energy + capability flags) and arm its think. The port folds all of that into the descriptor's
        // Spawn (which calls VehicleCommon.SpawnVehicle and sets e.Think/e.NextThink itself). After this the hull
        // (e.Mins/e.Maxs) is known, which the drop-to-floor tracebox below needs.
        def.Spawn(e);

        // QC vehicle_initialize:1265-1270 (nodrop == false for every map spawnfunc): settle the vehicle onto the
        // ground with a downward world-only box trace from origin+'0 0 100' down 10000 using the vehicle hull, so
        // a vehicle placed loosely above the floor by a mapper drops onto it instead of hovering at its mapper
        // origin. QC does this BEFORE recording pos1, so the dropped position becomes the persistent return point —
        // update both the live origin AND SpawnPos so respawns land on the floor too.
        if (Api.Services is not null)
        {
            System.Numerics.Vector3 from = e.Origin + new System.Numerics.Vector3(0f, 0f, 100f);
            System.Numerics.Vector3 to = e.Origin - new System.Numerics.Vector3(0f, 0f, 10000f);
            TraceResult drop = Api.Trace.Trace(from, e.Mins, e.Maxs, to, MoveFilter.WorldOnly, e);
            // QC droptofloor only relocates the entity when it actually finds ground below (a real map always has
            // a floor under a vehicle spawn). If the trace hits nothing (Fraction == 1, e.g. a spawn over a void or
            // a headless test world with no floor), leave the authored origin rather than teleporting it 10000 down.
            if (drop.Fraction < 1f)
            {
                Api.Entities.SetOrigin(e, drop.EndPos);
                e.SpawnPos = drop.EndPos;
            }
        }

        // QC vehicle_initialize:1278-1280 — the g_vehicles_delayspawn first-think branch: a map-placed vehicle
        // does not activate immediately but at `time + respawntime + random()*g_vehicles_delayspawn_jitter`,
        // staggering a row of vehicles instead of popping them in together. def.Spawn already ran (the FOLDED
        // vehicles_spawn — needed for the drop-to-floor hull above AND to set e.RespawnTime, which the jitter
        // formula reads) and armed the per-frame Think; ScheduleDelayedSpawn re-parks the vehicle hidden
        // (EF_NODRAW) + inert (SOLID_NOT / DAMAGE_NO / MOVETYPE_NONE) and re-arms the descriptor Spawn as the
        // delayed think, so the FIRST live activation honours the jitter. Defaults: g_vehicles_delayspawn 1,
        // g_vehicles_delayspawn_jitter 10 (vehicles.cfg). Headless (no clock) keeps the immediate activation.
        if (Api.Services is not null && DelaySpawnEnabled())
        {
            float jitter = string.IsNullOrEmpty(Api.Cvars.GetString("g_vehicles_delayspawn_jitter"))
                ? 10f : Api.Cvars.GetFloat("g_vehicles_delayspawn_jitter");
            VehicleCommon.ScheduleDelayedSpawn(e, jitter);
        }

        // QC vehicle_initialize:1178-1201 (targetname controller / vehicle_use) and the ACTIVE_NOT active-state
        // gating remain DEFERRED: they need the map-scripted-activation subsystem the port does not yet have.
        // Specifically the QC controller is resolved with find(NULL, target, this.targetname) — the entity whose
        // .target equals this vehicle's .targetname — and there is no general find-by-.target facade enumerator
        // (only the teleporter-restricted one); the .use trigger plumbing that lets that controller fire
        // vehicle_use does not exist. Without a controller every map vehicle is ACTIVE_ACTIVE and runs
        // immediately (modulo the delayspawn stagger above), which is the common stock-map case. See registry
        // vehicle-framework.init.initialize gaps.

        // QC vehicle_initialize tail (sv_vehicles.qc:1283): if (MUTATOR_CALLHOOK(VehicleInit, this)) return false;
        // — a mutator may veto this vehicle's one-time init, and the spawnfunc deletes the edict on a false return.
        if (VehicleCommon.InitVehicle(e) && Api.Services is not null)
            Api.Entities.Remove(e);
    }

    // Per-type spawnfuncs (each per-vehicle .qc: spawnfunc(vehicle_X){ ... vehicle_initialize(this, VEH_X, false); }).
    public static void Racer(Entity e) => Spawn(e, "racer");             // vehicle/racer.qc:520
    public static void Raptor(Entity e) => Spawn(e, "raptor");           // vehicle/raptor.qc:587
    public static void Spiderbot(Entity e) => Spawn(e, "spiderbot");     // vehicle/spiderbot.qc:529
    public static void Bumblebee(Entity e) => Spawn(e, "bumblebee");     // vehicle/bumblebee.qc:740

    /// <summary>
    /// Evaluate a <c>g_vehicle_X</c> boolean master switch the QC way (<c>if (!autocvar_g_vehicle_X)</c>):
    /// DISABLED only when the cvar is explicitly 0, ENABLED when unset/absent (vehicles.cfg defaults each to
    /// 1). Distinguishes "absent" from "present-and-0" via the raw string (an unset cvar reads as ""), so a
    /// server that sets <c>g_vehicle_racer 0</c> actually suppresses that vehicle instead of falling back to 1.
    /// </summary>
    private static bool MasterSwitchEnabled(string name)
    {
        if (Api.Services is null) return true; // no cvar store (headless): default-on like the cfg
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return true; // unset → enabled (cfg default is 1)
        return Api.Cvars.GetFloat(name) != 0f;     // present → 0 disables (QC !autocvar)
    }

    /// <summary>
    /// QC <c>autocvar_g_vehicles_delayspawn</c> (sv_vehicles.qh, default 1): whether map-placed vehicles stagger
    /// their first activation by <c>respawntime + random()*g_vehicles_delayspawn_jitter</c>. Absent/unset reads as
    /// ON (the vehicles.cfg default); an explicit 0 makes every vehicle activate immediately at placement.
    /// </summary>
    private static bool DelaySpawnEnabled()
    {
        if (Api.Services is null) return false; // headless: no clock to schedule against, keep the immediate spawn
        string s = Api.Cvars.GetString("g_vehicles_delayspawn");
        if (string.IsNullOrEmpty(s)) return true; // unset → enabled (cfg default is 1)
        return Api.Cvars.GetFloat("g_vehicles_delayspawn") != 0f;
    }

    /// <summary>
    /// QC <c>autocvar_g_vehicles_teams</c> (sv_vehicles.qh, default 1): whether vehicles honor team ownership.
    /// Absent/unset reads as ON (the cfg default); an explicit 0 disables team-bound vehicles.
    /// </summary>
    private static bool TeamsEnabled()
    {
        if (Api.Services is null) return true; // no cvar store (headless): default-on like the cfg
        string s = Api.Cvars.GetString("g_vehicles_teams");
        if (string.IsNullOrEmpty(s)) return true; // unset → enabled (cfg default is 1)
        return Api.Cvars.GetFloat("g_vehicles_teams") != 0f;
    }
}
