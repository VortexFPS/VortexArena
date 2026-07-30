using System;
using System.Collections.Generic;
using System.Globalization;
using XonoticGodot.Formats.Bsp;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// Per-gametype filtering of the map's brush entities — the Godot-free port of DP's
/// <c>SV_OnEntityPreSpawnFunction</c> gate (server/main.qc): a map entity carrying a
/// <c>gametypefilter</c> that doesn't match the active gametype is <c>delete()</c>d before its spawnfunc
/// runs. Plus the Q3/QL compat keys (<c>gametype</c>/<c>not_gametype</c>/<c>notteam</c>/<c>notfree</c>/
/// <c>notsingle</c>) ported from <c>DoesQ3ARemoveThisEntity</c> (server/compat/quake3.qc).
///
/// <para>Why the Engine layer cares: many of these filtered entities are <em>brush</em> entities
/// (<c>func_wall</c>/<c>func_clientwall</c>/<c>func_clientillusionary</c>/…) that own an inline
/// <c>"*N"</c> brush model — both render faces and collision brushes. Stormkeep, for instance, hides a
/// set of Race-only barriers (<c>gametypefilter "+rc"</c>) and a set of non-Race barriers
/// (<c>gametypefilter "-rc"</c>). Before this, the host rendered + (when spawned SOLID) clipped ALL of
/// them regardless of gametype. This class resolves which <c>"*N"</c> submodels belong to filtered-out
/// brush entities so the collision builder skips their brushes and the render builder skips their faces.</para>
///
/// <para>This is the deterministic, headless-testable essence: it reads only the parsed
/// <see cref="BspData.Entities"/> + the active gametype context (short name, teamplay, team-spawns).
/// The full spawn path (server <c>SpawnMapEntities</c>) can reuse <see cref="ShouldKeepEntity"/> directly
/// to honor the filter for non-brush entities (items/triggers) too.</para>
/// </summary>
public static class MapEntityFilter
{
    /// <summary>
    /// The active gametype context the filter resolves against — the C# stand-in for the trio
    /// <c>MapInfo_LoadedGametype, teamplay, have_team_spawns</c> that DP passes to <c>isGametypeInFilter</c>.
    /// </summary>
    /// <param name="ShortName">The gametype's short code (QC <c>Gametype.mdl</c> / <c>GameType.NetName</c>),
    /// e.g. <c>"dm"</c>, <c>"ctf"</c>, <c>"rc"</c>. Compared case-sensitively as DP does.</param>
    /// <param name="TeamPlay">QC <c>teamplay</c>: is this a team game (drives the <c>,teams,</c>/<c>,noteams,</c>
    /// subpattern and the Q3 <c>notteam</c>/<c>notfree</c> keys)?</param>
    /// <param name="HaveTeamSpawns">QC <c>have_team_spawns &gt; 0</c>: does the map carry team-tagged spawn
    /// points (drives the <c>,teamspawns,</c>/<c>,noteamspawns,</c> subpattern)?</param>
    public readonly record struct GametypeContext(string ShortName, bool TeamPlay, bool HaveTeamSpawns)
    {
        /// <summary>True for the Race/CTS family, which additionally matches the <c>,race,</c> subpattern.</summary>
        public bool IsRace => ShortName is "rc" or "cts";
    }

    /// <summary>
    /// Resolve which inline <c>"*N"</c> brush-model indices belong to brush entities that the active gametype
    /// filters out, so the caller drops their collision + render geometry. Returns a set of model indices
    /// <c>N</c> (≥1; model 0 is worldspawn and is never filtered). An entity without a <c>"*N"</c> model
    /// reference contributes nothing here (it has no inline geometry to drop — handle it via
    /// <see cref="ShouldKeepEntity"/> at spawn time instead).
    /// </summary>
    public static HashSet<int> DroppedSubmodels(BspData bsp, GametypeContext gt)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        var dropped = new HashSet<int>();
        foreach (IReadOnlyDictionary<string, string> ent in bsp.Entities)
        {
            if (ShouldKeepEntity(ent, gt))
                continue;
            if (TryGetSubmodelIndex(ent, out int n))
                dropped.Add(n);
        }
        return dropped;
    }

    /// <summary>
    /// QC <c>SV_OnEntityPreSpawnFunction</c> + <c>DoesQ3ARemoveThisEntity</c> (the Godot-free slice): would
    /// this map entity survive the pre-spawn gametype gate? Honors the Xonotic-native <c>gametypefilter</c>
    /// key and the Q3/QL compat keys (<c>gametype</c>/<c>not_gametype</c>/<c>notteam</c>/<c>notfree</c>/
    /// <c>notsingle</c>). Cvar filters (<c>cvarfilter</c>) and mutator hooks are deferred (they need the live
    /// cvar/mutator state and aren't what gates the map's static brush entities).
    /// </summary>
    public static bool ShouldKeepEntity(IReadOnlyDictionary<string, string> ent, GametypeContext gt)
    {
        ArgumentNullException.ThrowIfNull(ent);

        // 1) Xonotic-native gametypefilter (server/main.qc).
        if (ent.TryGetValue("gametypefilter", out string? filter) && !string.IsNullOrEmpty(filter))
        {
            if (!IsGametypeInFilter(gt, filter))
                return false;
        }

        // 2) Q3/QL compat filters (server/compat/quake3.qc DoesQ3ARemoveThisEntity). These apply whenever the
        //    map ships the keys, regardless of q3compat mode, because the keys are inert in Xonotic maps and
        //    present only in imported Q3/QL maps — so honoring them is always correct and never over-filters.
        if (DoesQ3CompatRemove(ent, gt))
            return false;

        return true;
    }

    /// <summary>
    /// Faithful port of <c>isGametypeInFilter(gt, teamplay, have_team_spawns, pattern)</c> (common/util.qc).
    /// The pattern is a comma/space-free list of tokens; a leading <c>-</c> inverts (exclude if matched), a
    /// leading <c>+</c> is the (optional) explicit include form. Tokens checked: the gametype short name, the
    /// <c>teams</c>/<c>noteams</c> + <c>teamspawns</c>/<c>noteamspawns</c> pseudo-tokens, and <c>race</c> for
    /// the Race/CTS family.
    /// </summary>
    public static bool IsGametypeInFilter(GametypeContext gt, string pattern)
    {
        // DP builds ",<token>," and substring-searches ",<pattern>,". We mirror that exactly (so a token can't
        // partially match a longer token — ",dm," won't match ",tdm,").
        string subName  = $",{gt.ShortName},";
        string subTeams = gt.TeamPlay ? ",teams," : ",noteams,";
        string subSpawn = gt.HaveTeamSpawns ? ",teamspawns," : ",noteamspawns,";
        string? subRace = gt.IsRace ? ",race," : null;

        static bool Contains(string pat, string needle) => ("," + pat + ",").Contains(needle, StringComparison.Ordinal);

        if (pattern.Length > 0 && pattern[0] == '-')
        {
            string pat = pattern[1..];
            if (Contains(pat, subName)) return false;
            if (Contains(pat, subTeams)) return false;
            if (Contains(pat, subSpawn)) return false;
            if (subRace is not null && Contains(pat, subRace)) return false;
            return true;
        }
        else
        {
            string pat = (pattern.Length > 0 && pattern[0] == '+') ? pattern[1..] : pattern;
            if (!Contains(pat, subName) && !Contains(pat, subTeams) && !Contains(pat, subSpawn))
            {
                if (subRace is null) return false;
                if (!Contains(pat, subRace)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Port of the always-applicable Q3/QL field filters from <c>DoesQ3ARemoveThisEntity</c>
    /// (server/compat/quake3.qc): <c>notteam</c> (drop in team games), <c>notfree</c> (drop in non-team
    /// games), <c>notsingle</c> (drop in single-player DM — never true for a normal match here), and the
    /// <c>gametype</c>/<c>not_gametype</c> token lists. The physics-mode keys (<c>notcpm</c>/<c>notvq3</c>)
    /// and Team-Arena <c>notta</c> are intentionally NOT honored here — they depend on the chosen
    /// <c>g_mod_physics</c>/balance which the demo doesn't model, and they only ship in imported maps.
    /// </summary>
    private static bool DoesQ3CompatRemove(IReadOnlyDictionary<string, string> ent, GametypeContext gt)
    {
        if (ReadBool(ent, "notteam") && gt.TeamPlay)
            return true;
        if (ReadBool(ent, "notfree") && !gt.TeamPlay)
            return true;

        bool hasGt = ent.TryGetValue("gametype", out string? gametype) && !string.IsNullOrEmpty(gametype);
        bool hasNotGt = ent.TryGetValue("not_gametype", out string? notGametype) && !string.IsNullOrEmpty(notGametype);
        if (hasGt || hasNotGt)
        {
            // Map our short name onto the Q3/QL token vocabulary DoesQ3ARemoveThisEntity tests with strstr().
            (string q3, string ql) = Q3QlNames(gt);
            if (hasGt)
            {
                if (gametype!.IndexOf(q3, StringComparison.Ordinal) < 0 &&
                    gametype!.IndexOf(ql, StringComparison.Ordinal) < 0)
                    return true;
            }
            if (hasNotGt)
            {
                if (notGametype!.IndexOf(ql, StringComparison.Ordinal) >= 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The Q3 and QL gametype token our active gametype maps to (subset of <c>DoesQ3ARemoveThisEntity</c>'s
    /// table — the cases reachable without single-player/oneflag state). Team games default to
    /// <c>team</c>/<c>tdm</c>, FFA to <c>ffa</c>, with overrides for the gametypes that have a distinct token.
    /// </summary>
    private static (string q3, string ql) Q3QlNames(GametypeContext gt)
    {
        string q3 = gt.TeamPlay ? "team" : "ffa";
        string ql = gt.TeamPlay ? "tdm" : "ffa";
        switch (gt.ShortName)
        {
            case "ctf":  q3 = "ctf"; ql = "ctf"; break;
            case "duel": q3 = "tournament"; ql = "duel"; break;
            case "ca":   ql = "ca"; break;
            case "ft":   ql = "ft"; break;
            case "dom":  ql = "dom"; break;
            case "rc":
            case "cts":  ql = "race"; break;
        }
        return (q3, ql);
    }

    /// <summary>
    /// Resolve an entity's inline brush-model index from its <c>"model"</c> key when it is a <c>"*N"</c>
    /// reference (the only models the collision/render builders split out). Returns false for a missing model,
    /// an external model path, or a malformed reference.
    /// </summary>
    private static bool TryGetSubmodelIndex(IReadOnlyDictionary<string, string> ent, out int index)
    {
        index = 0;
        if (!ent.TryGetValue("model", out string? model) || string.IsNullOrEmpty(model))
            return false;
        if (model[0] != '*')
            return false;
        return int.TryParse(model.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 1;
    }

    /// <summary>Read an entity key as a QC boolean (<c>stof(...) != 0</c>): any non-zero number is true.</summary>
    private static bool ReadBool(IReadOnlyDictionary<string, string> ent, string key)
    {
        if (!ent.TryGetValue(key, out string? s) || string.IsNullOrEmpty(s))
            return false;
        // QC stof: parse leading number, default 0. A bare "1"/"1.0" etc. is the convention.
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f != 0f;
    }
}
