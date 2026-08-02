using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Common.Diagnostics;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Materials;
using VortexArena.Formats.Vfs;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The <c>r_missingtextures</c> console command — "which textures in this map will not render, and how much
/// of the map wears them".
///
/// <code>
///   r_missingtextures            audit the map currently loaded
///   r_missingtextures &lt;map&gt;      audit any map in the search path, without loading it
///   r_missingtextures -v         also list the textures that are fine
/// </code>
///
/// <para>There is no equivalent in DarkPlaces: it prints one <c>could not load texture</c> line per shaderless
/// miss during load and leaves you to scroll, and it says nothing at all when a <c>.shader</c> resolves but one
/// of its stage images does not (the pk3-shipped-without-its-textures case). The analysis lives in the
/// Godot-free <see cref="MapTextureAudit"/> so its precedence is unit-tested; this class is the wiring —
/// resolving a map name to a BSP, and formatting.</para>
///
/// <para>Client-side and registered on the shared <see cref="ConfigInterpreter"/> like <c>screenshot</c> and
/// the <c>vmap_*</c> commands: auditing your own asset install has no multiplayer effect, so it never routes to
/// the server.</para>
/// </summary>
public static class MissingTextures
{
    // Deliberately NOT cached between the load-time summary and a command typed straight after it. A scan is a
    // few ms (the VFS resolve caches are already warm by the time either runs), and a report memoized per map
    // would survive an `fs_rescan` — handing back the pre-rescan answer to the one person who just told the
    // engine the content on disk had changed. Cheap and always current beats fast and occasionally lying.

    /// <summary>Register <c>r_missingtextures</c> on the shared interpreter.</summary>
    /// <param name="interp">The console/config interpreter to register on.</param>
    /// <param name="assets">Asset facade — supplies the VFS, the shader table and the BSP reader.</param>
    /// <param name="currentMap">
    /// Returns the map the client is in (empty at the menu), so the no-argument form audits what you are
    /// looking at. Evaluated per invocation, so it is wired at boot before any match exists.
    /// </param>
    public static void RegisterCommand(ConfigInterpreter interp, AssetLoader assets, Func<string> currentMap)
    {
        ArgumentNullException.ThrowIfNull(interp);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(currentMap);

        interp.RegisterCommand("r_missingtextures", argv => Run(argv, assets, currentMap));
    }

    private static void Run(IReadOnlyList<string> argv, AssetLoader assets, Func<string> currentMap)
    {
        bool verbose = false;
        string map = string.Empty;
        for (int i = 1; i < argv.Count; i++)
        {
            string arg = argv[i];
            if (arg is "-v" or "--verbose" or "verbose")
                verbose = true;
            else if (map.Length == 0)
                map = arg;
        }

        if (map.Length == 0)
            map = currentMap();
        if (string.IsNullOrWhiteSpace(map))
        {
            Log.Help("r_missingtextures: no map loaded — use `r_missingtextures <mapname>` to audit one from the search path.");
            return;
        }

        string? vpath = ResolveMapVPath(assets.Vfs, map);
        if (vpath is null)
        {
            Log.Help($"r_missingtextures: no such map '{map}' in the search path.");
            return;
        }

        BspData? bsp = assets.ReadBsp(vpath);
        if (bsp is null)
        {
            Log.Help($"r_missingtextures: '{vpath}' could not be parsed as a BSP.");
            return;
        }

        MapTextureAudit.Report report = Scan(bsp, assets.Assets);
        foreach (string line in Format(report, vpath, verbose))
            Log.Help(line);
    }

    /// <summary>
    /// One line at map load when something is missing, so a broken install announces itself instead of waiting
    /// to be asked. Silent on a clean map — this runs on every load and must not add noise. Called from
    /// <see cref="MapLoader.BuildMap"/>, whose per-map summary it sits beside.
    /// </summary>
    public static void LogLoadSummary(BspData bsp, AssetSystem assets, string mapName)
    {
        try
        {
            MapTextureAudit.Report report = Scan(bsp, assets);
            if (report.Clean)
            {
                // Trace, so `developer 1` can tell "audited, all present" apart from "the audit never ran" —
                // a silent clean result and a silently broken hook look identical otherwise.
                Log.Trace($"[MapLoader] '{mapName}': all {report.TextureCount} textures resolve.");
                return;
            }
            Log.Warn($"[MapLoader] '{mapName}': {Headline(report)} — type `r_missingtextures` for the list.");
        }
        catch (Exception ex)
        {
            // A diagnostic must never be the reason a map fails to load.
            Log.Warn($"[MapLoader] missing-texture audit failed for '{mapName}': {ex.Message}");
        }
    }

    /// <summary>Bind the audit to this install's shader table and search path.</summary>
    private static MapTextureAudit.Report Scan(BspData bsp, AssetSystem assets)
        => MapTextureAudit.Scan(
            bsp,
            assets.GetShader,
            name => assets.Vfs.ResolveImage(name) is not null);

    // =============================================================================================
    //  Formatting
    // =============================================================================================

    private static string Headline(MapTextureAudit.Report r)
    {
        var sb = new StringBuilder();
        if (r.MissingCount > 0 || r.PartialCount > 0)
        {
            sb.Append(r.MissingCount).Append(r.MissingCount == 1 ? " texture missing" : " textures missing");
            if (r.PartialCount > 0)
                sb.Append(", ").Append(r.PartialCount).Append(" partially missing");
            sb.Append(" (").Append(r.FacesAffected)
              .Append(r.FacesAffected == 1 ? " face" : " faces").Append(" affected)");
        }
        if (r.Sky.Broken)
        {
            if (sb.Length > 0)
                sb.Append("; ");
            sb.Append("skybox '").Append(r.Sky.Name).Append("' ")
              .Append(r.Sky.NothingFound ? "not found" : $"missing {r.Sky.MissingFaces.Count} of {SkyboxPaths.Sides} faces");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Format(MapTextureAudit.Report r, string vpath, bool verbose)
    {
        yield return $"r_missingtextures: {vpath} — {r.TextureCount} textures, " +
                     $"{r.MissingCount} missing, {r.PartialCount} partial, {r.NotDrawnCount} not drawn " +
                     $"({r.FacesAffected} of {TotalFaces(r)} faces affected)" +
                     (r.Sky.Broken ? ", skybox BROKEN" : string.Empty);

        foreach (string line in FormatSky(r.Sky, verbose))
            yield return line;

        // The sky is reported above and is not in the entry list, so a broken skybox alone must not send us
        // into a surface listing that has nothing to say.
        if (r.MissingCount == 0 && r.PartialCount == 0 && !verbose)
        {
            yield return "  all surface textures resolve.";
            yield break;
        }

        foreach (MapTextureAudit.Entry e in r.Entries)
        {
            bool listed = e.Status is MapTextureAudit.Status.Missing or MapTextureAudit.Status.Partial;
            if (!listed && !verbose)
                continue;

            string label = e.Status switch
            {
                MapTextureAudit.Status.Missing => "MISSING",
                MapTextureAudit.Status.Partial => "partial",
                MapTextureAudit.Status.NotDrawn => "nodraw ",
                _ => "ok     ",
            };
            yield return $"  {label} {e.FaceCount,6} faces  {e.Name}{(e.HasShader ? "  (shader)" : string.Empty)}";

            // For a shaderless texture the missing image IS the name — repeating it says nothing. For a shader,
            // the stage images are the whole point: they are the files to go and find.
            if (!e.HasShader)
                continue;
            foreach (string image in e.MissingImages)
                yield return $"                        -> {image}";
        }

        if (r.NotDrawnCount > 0 && !verbose)
            yield return $"  ({r.NotDrawnCount} nodraw/sky/common entries skipped)";
    }

    /// <summary>
    /// The skybox line. Always shown when it is broken (a blank void overhead is as loud a defect as a
    /// checkerboard wall, and nothing in the surface listing would mention it); otherwise only under
    /// <c>-v</c>, where "no skybox declared" is genuinely useful — it distinguishes an indoor map from one
    /// whose <c>skyParms</c> never parsed.
    /// </summary>
    private static IEnumerable<string> FormatSky(MapTextureAudit.SkyReport sky, bool verbose)
    {
        if (sky.Broken)
        {
            yield return sky.NothingFound
                ? $"  SKYBOX  '{sky.Name}' — no faces found under any suffix convention"
                : $"  SKYBOX  '{sky.Name}' — missing {sky.MissingFaces.Count} of {SkyboxPaths.Sides} faces "
                  + $"({string.Join("/", sky.Convention)} convention)";
            // Where to put the file. Only the FIRST (canonical `<name>_<suffix>`) form is shown: DP also probes
            // bare-suffix and env/ + gfx/env/ prefixed forms, but it prefixes blindly, so for a name that
            // already starts with env/ those read as `env/env/…` — accurate about what the engine tries, and
            // actively misleading as a suggestion of where to put a file.
            foreach (string suffix in sky.MissingFaces)
                yield return $"                        -> {SkyboxPaths.FaceCandidates(sky.Name, suffix).First()}"
                             + $"  (or the {sky.Name}{suffix} / env/ / gfx/env/ forms)";
            yield break;
        }

        if (!verbose)
            yield break;

        yield return sky.Declared
            ? $"  ok      skybox '{sky.Name}' ({string.Join("/", sky.Convention)} convention)"
            : "  ok      no skybox declared (indoor map, or a drawn sky shader)";
    }

    private static int TotalFaces(MapTextureAudit.Report r)
    {
        int total = 0;
        foreach (MapTextureAudit.Entry e in r.Entries)
            total += e.FaceCount;
        return total;
    }

    // =============================================================================================
    //  Map name -> vpath
    // =============================================================================================

    /// <summary>
    /// Accept the same spellings the <c>map</c> command does — <c>exomorph</c>, <c>maps/exomorph</c>,
    /// <c>maps/exomorph.bsp</c> — and return the first that exists, or null. Mirrors
    /// <c>NetGame.MapVPathCandidates</c>.
    /// </summary>
    private static string? ResolveMapVPath(VirtualFileSystem vfs, string map)
    {
        foreach (string candidate in Candidates(map))
            if (vfs.Exists(candidate))
                return candidate;
        return null;

        static IEnumerable<string> Candidates(string mapPath)
        {
            string p = mapPath.Replace('\\', '/').Trim();
            bool hasBsp = p.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase);
            bool underMaps = p.StartsWith("maps/", StringComparison.OrdinalIgnoreCase);
            yield return p;
            if (!hasBsp) yield return p + ".bsp";
            if (!underMaps)
            {
                yield return "maps/" + p;
                if (!hasBsp) yield return "maps/" + p + ".bsp";
            }
        }
    }
}
