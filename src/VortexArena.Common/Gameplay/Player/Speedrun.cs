using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// QC the <c>.personal</c> (<c>personal_wp</c>) speedrun-checkpoint snapshot (server/cheats.qc CHIMPULSE_SPEEDRUN_INIT
/// / CHIMPULSE_SPEEDRUN). When the player presses <c>waypoint_personal_here</c> (impulse 30) the engine snapshots
/// the player's full restorable state into the <c>.personal</c> entity; pressing <c>SPEEDRUN</c> (impulse 141)
/// teleports the player back to that snapshot and restores the state, re-basing the pause timers / status-effect
/// expiries off the elapsed time (QC <c>this.personal.teleport_time</c>). Modeled as a value snapshot on the player
/// because the QC <c>personal_wp</c> edict is never simulated — it only ever stores fields.
/// </summary>
public sealed class PersonalCheckpoint
{
    public Vector3 Origin;
    public Vector3 ViewAngle;       // QC .v_angle
    public Vector3 Velocity;

    public float Rockets, Bullets, Cells, Shells, Fuel, Health, Armor;

    /// <summary>QC <c>STAT(WEAPONS, this.personal)</c> — the owned-weapon set at snapshot time.</summary>
    public readonly HashSet<string> OwnedWeapons = new(System.StringComparer.Ordinal);

    /// <summary>QC <c>.items</c> — the item bitflags at snapshot time.</summary>
    public int Items;

    /// <summary>QC the four pause-timer finished-times + <c>.teleport_time</c> (the snapshot instant), so the
    /// restore can re-base each remaining pause off the new <c>time</c> (QC: <c>time + finished - teleport_time</c>).</summary>
    public float PauseRotArmorFinished, PauseRotHealthFinished, PauseRotFuelFinished, PauseRegenFinished;
    public float TeleportTime;

    /// <summary>QC <c>.statuseffects</c> snapshot — the active effects copied via <c>StatusEffects_copy(.., 0)</c>
    /// (absolute expiries at snapshot time, re-based on restore by the elapsed offset).</summary>
    public readonly List<ActiveStatusEffect> StatusEffects = new();
}

/// <summary>
/// QC the speedrun-checkpoint cheat half (server/cheats.qc CHIMPULSE_SPEEDRUN_INIT 30 / CHIMPULSE_SPEEDRUN 141).
/// Snapshot/restore of <see cref="Player.PersonalCheckpoint"/>. The impulse-30 snapshot is NOT a cheat by itself
/// (QC's SPEEDRUN_INIT case does not call IS_CHEAT — it shares the <c>waypoint_personal_here</c> key and only
/// stores state); the impulse-141 restore counts a cheat unless <c>g_allow_checkpoints</c> is set.
/// </summary>
public static class Speedrun
{
    private static float Now => Api.Services != null ? Api.Clock.Time : 0f;

    /// <summary>
    /// QC <c>CHIMPULSE_SPEEDRUN_INIT</c> (cheats.qc:144): snapshot the player's restorable state into
    /// <see cref="Player.PersonalCheckpoint"/>. Allocates the checkpoint on first use. Not gated by sv_cheats
    /// (QC's case has no IS_CHEAT — it's the same key as <c>waypoint_personal_here</c>). Health is floored at 1
    /// (QC <c>max(1, GetResource(this, RES_HEALTH))</c>).
    /// </summary>
    public static void SnapshotPersonal(Player p)
    {
        PersonalCheckpoint cp = p.PersonalCheckpoint ??= new PersonalCheckpoint();

        cp.Origin = p.Origin;
        cp.ViewAngle = p.ViewAngles != Vector3.Zero ? p.ViewAngles : p.Angles;
        cp.Velocity = p.Velocity;

        cp.Rockets = p.AmmoRockets;
        cp.Bullets = p.AmmoBullets;
        cp.Cells = p.AmmoCells;
        cp.Shells = p.AmmoShells;
        cp.Fuel = p.AmmoFuel;
        cp.Health = System.MathF.Max(1f, p.Health);
        cp.Armor = p.ArmorValue;

        cp.OwnedWeapons.Clear();
        foreach (string w in p.OwnedWeapons) cp.OwnedWeapons.Add(w);
        cp.Items = p.Items;

        // QC StatusEffects_copy(this.statuseffects, this.personal, 0) — absolute expiries (offset 0).
        cp.StatusEffects.Clear();
        cp.StatusEffects.AddRange(p.StatusEffects);

        cp.PauseRotArmorFinished = p.PauseRotArmorFinished;
        cp.PauseRotHealthFinished = p.PauseRotHealthFinished;
        cp.PauseRotFuelFinished = p.PauseRotFuelFinished;
        cp.PauseRegenFinished = p.PauseRegenFinished;
        cp.TeleportTime = Now; // QC this.personal.teleport_time = time;
    }

    /// <summary>
    /// QC <c>CHIMPULSE_SPEEDRUN</c> (cheats.qc:188): teleport the player back to their personal checkpoint and
    /// restore the snapshotted state, re-basing the pause timers + status-effect expiries off the elapsed time.
    /// Returns true when a checkpoint existed and the restore ran (so the caller can count a cheat when
    /// <c>g_allow_checkpoints</c> is off — matching QC's <c>if(!g_allow_checkpoints) DID_CHEAT()</c>).
    /// <paramref name="log"/> receives the QC sprint() messages. The QC start-solid tracebox guard ("Cannot move
    /// there, cheater") IS modeled: if the snapshot origin would start-solid against the world (a destination that
    /// has since been blocked) the teleport is skipped (and the message printed) while the resources/weapons/items
    /// /timers are still restored unconditionally, as in Base. The <c>AbortSpeedrun</c> mutator hook (which Base
    /// fires only inside the teleport-success branch — CTS consumes it via <c>Cts.AbortSpeedrun</c> to drop the run
    /// clock) is not driven from this restore path yet; that wiring lives in the server-side cheat dispatcher.
    /// </summary>
    public static bool RestorePersonal(Player p, System.Action<string>? log = null)
    {
        PersonalCheckpoint? cp = p.PersonalCheckpoint;
        if (cp is null)
        {
            log?.Invoke(p.IsDead
                ? "UR DEAD AHAHAH))"
                : "No waypoint set, cheater (use waypoint_personal_here to set one)");
            return false;
        }

        p.Speedrunning = true;

        // QC cheats.qc:194 (CHIMPULSE_SPEEDRUN): tracebox the snapshot origin against the WORLD only
        // (MOVE_WORLDONLY) with the player's own box BEFORE teleporting. If it would start-solid the destination
        // is now blocked (e.g. a door/func closed since the snapshot), so Base sprints "Cannot move there, cheater"
        // and does NOT teleport — but it still restores the resources/weapons/items/timers below (only the
        // setorigin/velocity/angles block — and the AbortSpeedrun hook — sit inside the QC else branch). Use the
        // live player box if set, else the stock standing hull the snapshot was taken with.
        bool canTeleport = true;
        if (Api.Services != null)
        {
            Vector3 mins = p.Mins != Vector3.Zero ? p.Mins : SpawnSystem.PlayerMins;
            Vector3 maxs = p.Maxs != Vector3.Zero ? p.Maxs : SpawnSystem.PlayerMaxs;
            TraceResult solid = Api.Trace.Trace(cp.Origin, mins, maxs, cp.Origin, MoveFilter.WorldOnly, p);
            if (solid.StartSolid)
            {
                canTeleport = false;
                log?.Invoke("Cannot move there, cheater - only waypoints set using waypoint_personal_here work");
            }
        }

        // QC (else branch): setorigin(this, this.personal.origin); oldvelocity = velocity = personal.velocity;
        //     angles = personal.v_angle; fixangle = true; MUTATOR_CALLHOOK(AbortSpeedrun, this);
        if (canTeleport)
        {
            MapMover.SetOrigin(p, cp.Origin);
            p.Velocity = cp.Velocity;
            p.OldVelocity = cp.Velocity;
            p.Angles = cp.ViewAngle;
            p.FixAngle = true;
            p.FixAngleAngles = cp.ViewAngle;
        }

        // QC: SetResource(this, RES_*, GetResource(this.personal, RES_*)); STAT(WEAPONS) = personal; items = personal.
        p.AmmoRockets = cp.Rockets;
        p.AmmoBullets = cp.Bullets;
        p.AmmoCells = cp.Cells;
        p.AmmoShells = cp.Shells;
        p.AmmoFuel = cp.Fuel;
        p.Health = cp.Health;
        p.ArmorValue = cp.Armor;

        p.OwnedWeapons.Clear();
        foreach (string w in cp.OwnedWeapons) p.OwnedWeapons.Add(w);
        p.Items = cp.Items;

        // QC: pause_finished = time + personal.pause_finished - personal.teleport_time; (re-base remaining pause)
        float rebase = Now - cp.TeleportTime;
        p.PauseRotArmorFinished = cp.PauseRotArmorFinished + rebase;
        p.PauseRotHealthFinished = cp.PauseRotHealthFinished + rebase;
        p.PauseRotFuelFinished = cp.PauseRotFuelFinished + rebase;
        p.PauseRegenFinished = cp.PauseRegenFinished + rebase;

        // QC: StatusEffects_copy(this.personal, this.statuseffects, this.personal.teleport_time);
        // time_offset = teleport_time => store_time = time + snapshot_expire - teleport_time = snapshot_expire + rebase.
        p.StatusEffects.Clear();
        foreach (ActiveStatusEffect e in cp.StatusEffects)
        {
            ActiveStatusEffect re = e;
            if (re.ExpireTime > 0f) re.ExpireTime = e.ExpireTime + rebase;
            p.StatusEffects.Add(re);
        }

        return true;
    }
}
