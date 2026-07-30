// Port of common/gametypes/sv_rules.{qh,qc}: GameRules_spawning_teams / GameRules_teams.
//
// QC's GameRules_spawning_teams(value) sets the global `have_team_spawns = value ? -1 : 0` — i.e. it records
// whether the active gametype REQUESTS team spawns (-1 "requested, none found yet") or not (0). GameRules_teams(true)
// (sv_rules.qc) always calls GameRules_spawning_teams(true), so the always-team modes (CTF/Domination/Onslaught/
// Nexball/Assault) request team spawns unconditionally. A handful of modes instead gate the request on a per-mode
// cvar called AFTER GameRules_teams(true) — e.g. sv_tdm.qc does `GameRules_spawning_teams(autocvar_g_tdm_team_spawns)`
// — and those modes override RequestsTeamSpawns to read that cvar (with the stock default).
//
// This is the per-gametype value the host feeds to SpawnSystem.RequestTeamSpawns(...) (the C# stand-in for the
// `have_team_spawns` global). Lives in a partial of GameType (declared `partial` in GameplayBases.cs) so the
// team-spawn hook ships alongside the other GameTypes/* wiring without editing the base-class file.

namespace XonoticGodot.Common.Gameplay;

public abstract partial class GameType
{
    /// <summary>
    /// QC <c>GameRules_spawning_teams(value)</c>: whether this gametype requests team-only spawnpoints (so a
    /// player spawns from a spawnpoint tagged with their own team when the map provides them). Defaults to
    /// <see cref="TeamGame"/> — the always-team modes (CTF/Domination/Onslaught/Nexball/Assault) request team
    /// spawns unconditionally via <c>GameRules_teams(true)</c>. Modes that gate the request on a per-mode cvar
    /// (TDM/CA/FreezeTag/KeyHunt/TeamKeepaway — <c>g_*_team_spawns</c>) override this. FFA modes inherit
    /// <c>false</c>. Read by the host into <see cref="SpawnSystem.RequestTeamSpawns"/> when the mode activates.
    /// </summary>
    public virtual bool RequestsTeamSpawns => TeamGame;
}
