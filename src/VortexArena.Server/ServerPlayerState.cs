using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// Server-only per-player bookkeeping that QuakeC kept as flat fields on the client edict but which has no
/// home on the (sealed, Common-side) <see cref="Player"/> — the regen/rot pause timers, the drown air
/// timer, the contents-damage cadence, and the weapon refire gate. Held in a side table
/// (<see cref="ServerPlayerStates"/>) keyed by <see cref="Player"/> so the server core can read/write it
/// without modifying Common. Mirrors the QC <c>.pauseregen_finished</c> / <c>.pauserothealth_finished</c> /
/// <c>STAT(AIR_FINISHED)</c> / <c>.pain_finished</c> / <c>.contents_damagetime</c> / <c>.watersound_finished</c>
/// fields (server/client.qc, server/main.qc).
/// </summary>
public sealed class ServerPlayerState
{
    // NOTE: the regen/rot pause timers (QC .pauseregen_finished / .pauserot{health,armor,fuel}_finished) used to
    // live here, but the damage path / pickup path / spawn path all write the Entity-side copies
    // (DamageEntityState.PauseRegenFinished etc.). The regen tick now reads those, so the duplicates were removed
    // to fix the storage split that left damage unable to pause regen (REGEN1/REGEN2/REGEN3).

    // ---- drowning (QC STAT(AIR_FINISHED) + .pain_finished gate) ----
    public float AirFinished;            // QC STAT(AIR_FINISHED): time the player runs out of air (0 = breathing)
    public float PainFinished;           // QC .pain_finished: next time drown/pain damage may tick

    // ---- contents (water/lava/slime) damage cadence (QC main.qc CreatureFrame) ----
    public float ContentsDamageTime;     // QC .contents_damagetime
    public float WaterSoundFinished;     // QC .watersound_finished
    public bool InWater;                 // QC FL_INWATER tracked per-player here

    // ---- fall-damage bookkeeping (QC .oldvelocity captured each CreatureFrame) ----
    public System.Numerics.Vector3 OldVelocity;

    // ---- name enforcement (QC CS(this).netname_previous, server/client.qc:2901-2921) ----
    // The last name we accepted for this client; PlayerFrame re-runs the nameless/too-long/invisible
    // check only when the netname changed from this. "" = never checked yet (first frame after connect).
    public string NetnamePrevious = "";

    // ---- idle detection (QC ecs/systems/sv_physics.qc parm_idlesince + server/client.qc PlayerFrame idle block) ----
    // parm_idlesince: the sim time of the player's last detectable input (buttons change, movement change, or
    // view-angle change while NOT typing).  0 = not yet set (treated as "just moved").
    public float IdleSince;            // QC CS(this).parm_idlesince
    public float IdleKickLastTimeLeft; // QC CS(this).idlekick_lasttimeleft — the countdown second last played/printed
    // Previous-frame input state so we can detect changes (QC buttons_old / movement_old / v_angle_old).
    public int ButtonsOld;
    public System.Numerics.Vector3 MovementOld;
    public System.Numerics.Vector3 VAngleOld;

    // ---- `kill` / team-change countdown (QC server/clientkill.qc killindicator.cnt / KillIndicator_Think) ----
    // KillCntdownActive mirrors the presence of QC .killindicator; KillCntdownCnt is the indicator's `cnt`
    // (whole seconds remaining); KillCntdownNextThink is the absolute sim time of the next per-second think
    // (QC .nextthink). The port models the presentation-relevant subset of the kill indicator (announcer
    // NUM_KILL + the CENTER_TEAMCHANGE countdown print) — the floating digit entity is not networked.
    public bool KillCntdownActive;
    public int KillCntdownCnt;
    public float KillCntdownNextThink;
    // QC .float clientkill_nexttime — the anti-spam carry-forward floor: a repeat `kill` raises killtime by
    // (clientkill_nexttime - time) so mashing the command extends the countdown rather than restarting it.
    public float KillCntdownNextTime;
    // QC .killindicator.count == 1 — the indicator is "silent" (no announcer / center-print / digit), used by the
    // CTS finish silent kill. A silent indicator with killtime<=0 also enables the instant-kill shortcut.
    public bool KillCntdownSilent;
    // QC .int killindicator_teamchange — the deferred intent the countdown resolves on expiry: 0 = just die,
    // -2 = spectate, >0 = move to that team. ClientKill_Now branches on this instead of the plain self-kill.
    public int KillCntdownTeamChange;

    /// <summary>Reset the transient timers on (re)spawn (QC PutPlayerInServer clears these). The regen/rot
    /// pause timers are primed on the Entity by SpawnSystem.PutPlayerInServer (REGEN3), not here.</summary>
    public void OnSpawn()
    {
        AirFinished = 0f;
        PainFinished = 0f;
        ContentsDamageTime = 0f;
        WaterSoundFinished = 0f;
        InWater = false;
        OldVelocity = System.Numerics.Vector3.Zero;
        KillCntdownActive = false;
        KillCntdownCnt = 0;
        KillCntdownNextThink = 0f;
        KillCntdownSilent = false;
        KillCntdownTeamChange = 0;
        // NOTE: KillCntdownNextTime (QC .clientkill_nexttime) is intentionally NOT reset here — the anti-spam
        // carry-forward is a client-edict field in Base that persists across deaths/respawns, so mashing `kill`
        // through a respawn still extends the next allowed kill.
        // NOTE: IdleSince (QC parm_idlesince) is intentionally NOT reset here — it's on the client state in Base
        // and persists across deaths/respawns (only connect/level-change resets it). IdleKickLastTimeLeft resets
        // on the next active-input tick (idleDuration < 1s branch in PlayerFrameIdleAll) so no spawn reset needed.
    }
}

/// <summary>
/// The side table mapping a live <see cref="Player"/> to its <see cref="ServerPlayerState"/>. Owned by
/// <see cref="GameWorld"/>; entries are created on demand and dropped on disconnect.
/// </summary>
public sealed class ServerPlayerStates
{
    private readonly Dictionary<Player, ServerPlayerState> _states = new();

    /// <summary>Get (creating if needed) the server-side state for a player.</summary>
    public ServerPlayerState Of(Player p)
    {
        if (!_states.TryGetValue(p, out var s))
        {
            s = new ServerPlayerState();
            _states[p] = s;
        }
        return s;
    }

    /// <summary>Drop a player's state (on disconnect).</summary>
    public void Remove(Player p) => _states.Remove(p);
}
