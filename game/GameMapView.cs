using System.Collections.Generic;
using System.Globalization;
using VortexArena.Formats.Bsp;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Collision;

namespace VortexArena.Game;

/// <summary>
/// Shared helpers for turning a parsed map (<see cref="BspData"/>) into the per-gametype view both the
/// walkable <see cref="GameDemo"/> and the networked <see cref="VortexArena.Game.Net.NetGame"/> listen server
/// need. Today this is the gametype-conditional inline-brush filter (QC <c>SV_OnEntityPreSpawnFunction</c>),
/// single-sourced here so a map's render geometry and its collision agree on which <c>"*N"</c> brush entities
/// a given gametype drops — they MUST match, or a filtered barrier would render without colliding (or vice
/// versa). <see cref="GameDemo"/> historically owned this privately; it now delegates here.
/// </summary>
public static class GameMapView
{
    /// <summary>
    /// Resolve which inline <c>"*N"</c> brush entities the <paramref name="gametype"/> filters out (QC
    /// <c>SV_OnEntityPreSpawnFunction</c>) — so neither their collision brushes nor their render faces are
    /// built. Mirrors the gametype context the server's <see cref="MapEntityFilter"/> uses: teamplay comes
    /// from the active gametype registry; have-team-spawns from whether any <c>info_player_*</c> carries a
    /// non-zero <c>team</c> key (QC <c>have_team_spawns</c>). Returns null when nothing is filtered (a map with
    /// no <c>gametypefilter</c> entities) so the per-face/per-brush check stays free.
    /// </summary>
    public static IReadOnlySet<int>? ComputeDroppedSubmodels(BspData bsp, string gametype)
    {
        string shortName = string.IsNullOrWhiteSpace(gametype) ? "dm" : gametype.Trim();

        // teamplay: the active gametype's team flag (QC GAMETYPE_FLAG_TEAMPLAY → teamplay). DM / unknown = FFA.
        GameType? gt = GameTypes.ByName(shortName);
        bool teamplay = gt?.TeamGame ?? false;

        // have_team_spawns: does the map ship team-tagged spawn points? (QC have_team_spawns > 0 — set when any
        // info_player_* carries a non-zero "team"). Only meaningful in team games; cheap to compute regardless.
        bool haveTeamSpawns = false;
        foreach (IReadOnlyDictionary<string, string> ent in bsp.Entities)
        {
            if (!ent.TryGetValue("classname", out string? cn) || cn is null ||
                !cn.StartsWith("info_player_", System.StringComparison.Ordinal))
                continue;
            if (ent.TryGetValue("team", out string? tm) && float.TryParse(tm,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float tv) && tv != 0f)
            {
                haveTeamSpawns = true;
                break;
            }
        }

        var ctx = new MapEntityFilter.GametypeContext(shortName, teamplay, haveTeamSpawns);
        IReadOnlySet<int> dropped = MapEntityFilter.DroppedSubmodels(bsp, ctx);
        return dropped.Count == 0 ? null : dropped;
    }
}
