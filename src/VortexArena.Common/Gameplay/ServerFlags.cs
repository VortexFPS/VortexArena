// Port of the QC `serverflags` global + its SERVERFLAG_* bit table (qcsrc/common/constants.qh:14-20),
// set by readlevelcvars (qcsrc/server/world.qc:2189-2195) and read by the client to gate fullbright player
// rendering (client/view.qc:1115) and the item-pickup timer (client/hud/panel/pickup.qc:91).
//
// In QC `serverflags` is an engine-networked server global (the engine does NOT clear it on gotomap). The
// port has no engine `serverflags`, so the world-rules layer publishes the computed value here AND mirrors it
// into the `serverflags` cvar so a shared-store listen-server client reads the same value its server set
// (the established shared-store seam — see MEMORY graphics-settings-wiring-reality). The two bits the
// world-rules unit owns (ALLOW_FULLBRIGHT / FORBID_PICKUPTIMER) come straight from sv_allow_fullbright /
// sv_forbid_pickuptimer; the other bits (TEAMPLAY / PLAYERSTATS*) are set elsewhere in Base and are defined
// here for completeness so a future port of those reads the same table.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// QC <c>serverflags</c> (server/world.qc) + the <c>SERVERFLAG_*</c> bit table (common/constants.qh). The
/// world-rules <c>readlevelcvars</c> port (GameWorld.ReadLevelCvars) sets <see cref="Value"/> from the
/// <c>sv_allow_fullbright</c> / <c>sv_forbid_pickuptimer</c> cvars and mirrors it into the <c>serverflags</c>
/// cvar so the client (sharing the store on a listen server) can gate fullbright rendering and the pickup timer.
/// </summary>
public static class ServerFlags
{
    /// <summary>QC <c>SERVERFLAG_ALLOW_FULLBRIGHT = BIT(0)</c>: the server permits client-side fullbright player rendering.</summary>
    public const int AllowFullbright = 1 << 0;

    /// <summary>QC <c>SERVERFLAG_TEAMPLAY = BIT(1)</c>.</summary>
    public const int Teamplay = 1 << 1;

    /// <summary>QC <c>SERVERFLAG_PLAYERSTATS = BIT(2)</c>.</summary>
    public const int PlayerStats = 1 << 2;

    /// <summary>QC <c>SERVERFLAG_PLAYERSTATS_CUSTOM = BIT(3)</c>.</summary>
    public const int PlayerStatsCustom = 1 << 3;

    /// <summary>QC <c>SERVERFLAG_FORBID_PICKUPTIMER = BIT(4)</c>: the server hides the HUD item-pickup timer.</summary>
    public const int ForbidPickupTimer = 1 << 4;

    /// <summary>
    /// The live server-flags bitfield (QC <c>serverflags</c>). Published by the world-rules layer at map init
    /// (and re-published on a level-cvar change). Client render/HUD code reads it via the cvar mirror; server
    /// code can read this directly.
    /// </summary>
    public static int Value { get; set; }
}
