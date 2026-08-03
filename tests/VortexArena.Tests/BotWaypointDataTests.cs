using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VortexArena.Common.Framework;
using VortexArena.Server.Bot;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// The bot waypoint DATA contract: a map pack ships all three waypoint files, and the graph that comes out
/// of them has no dead ends.
///
/// <para>This exists because the whole class of defect was invisible to source-only auditing. Xonotic ships
/// <c>&lt;map&gt;.waypoints</c> (the nodes), <c>.waypoints.cache</c> (the links) and <c>.waypoints.hardwired</c>
/// (the map-author links no tracewalk can derive — gap jumps, drop-downs, teleport exits). VortexMaps'
/// <c>build/split-pack.py</c> classified <c>.cache</c> and <c>.hardwired</c> as q3map2 residue and dropped
/// them, so the port shipped nodes with no links and re-derived what it could by tracewalk at load. That
/// costs ~26% of a map's links, turns ~29% of catharsis's waypoints into nodes a bot can enter and never
/// leave, and takes the graph build from ~35 ms to ~1 s. See planning/bot-ai-parity-2026-08-03.md D1/D2.</para>
/// </summary>
public class BotWaypointDataTests
{
    private readonly ITestOutputHelper _out;

    public BotWaypointDataTests(ITestOutputHelper output) => _out = output;

    private static IEnumerable<string> MapPacks()
    {
        string maps = Path.Combine(TestPaths.Data, "maps");
        if (!Directory.Exists(maps))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(maps, "*.pk3", SearchOption.TopDirectoryOnly)
            .Where(p => !Path.GetFileName(p).StartsWith("_", StringComparison.Ordinal)
                     && Path.GetFileNameWithoutExtension(p) != "shared")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every <c>.waypoints</c> in a shipped pack has its <c>.cache</c> and <c>.hardwired</c> beside it.</summary>
    [Fact]
    public void ShippedMapPacksCarryTheWaypointCompanionFiles()
    {
        if (TestPaths.Maps == TestPaths.MapContent.None)
        {
            _out.WriteLine($"no compiled maps — skipped. {TestPaths.NoMapsReason}");
            return;
        }

        var missing = new List<string>();
        int checkedGraphs = 0;
        foreach (string pack in MapPacks())
        {
            using var zip = ZipFile.OpenRead(pack);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string wp in names.Where(n => n.EndsWith(".waypoints", StringComparison.OrdinalIgnoreCase)))
            {
                checkedGraphs++;
                if (!names.Contains(wp + ".cache")) missing.Add(wp + ".cache");
                if (!names.Contains(wp + ".hardwired")) missing.Add(wp + ".hardwired");
            }
        }

        if (checkedGraphs == 0)
        {
            _out.WriteLine("installed packs carry no .waypoints at all — skipped");
            return;
        }

        _out.WriteLine($"checked {checkedGraphs} waypoint graph(s) across the installed packs");
        Assert.True(missing.Count == 0,
            $"{missing.Count} waypoint companion file(s) missing from the installed map packs, e.g. "
            + string.Join(", ", missing.Take(6))
            + ". These are runtime bot navigation data, not build residue. Fixed in VortexMaps "
            + "build/split-pack.py (SOURCE_EXT); the installed packs need a maps release built with that fix "
            + "and a re-pinned data/maps.lock.json.");
    }

    /// <summary>
    /// Every link the cache declares actually lands in the loaded graph: if the cache names a node as the
    /// source of an outgoing link, that node must not end up a dead end. A node a bot can route to and not
    /// leave is exactly the "stuck in a corner" failure.
    ///
    /// <para>Scoped to sources the cache names on purpose. This harness loads the graph with no map entities,
    /// so the teleporter/jumppad waypoints that <see cref="WaypointNetwork.GenerateTeleporterWaypoints"/>
    /// contributes in-game are absent here, and cache lines pointing at them legitimately cannot bind. Those
    /// are covered by <see cref="TeleporterWaypointsAreGeneratedOnTheFileLoadPath"/> instead.</para>
    /// </summary>
    [Fact]
    public void EveryCacheDeclaredLinkSourceHasOutgoingLinks()
    {
        if (TestPaths.Maps == TestPaths.MapContent.None)
        {
            _out.WriteLine($"no compiled maps — skipped. {TestPaths.NoMapsReason}");
            return;
        }

        var noEntities = Array.Empty<Entity>();
        var offenders = new List<string>();
        int examined = 0;

        foreach (string pack in MapPacks())
        {
            using var zip = ZipFile.OpenRead(pack);
            foreach (var entry in zip.Entries.Where(e =>
                         e.FullName.EndsWith(".waypoints", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string? cache = ReadEntry(zip, entry.FullName + ".cache");
                string? hardwired = ReadEntry(zip, entry.FullName + ".hardwired");
                if (cache is null)
                    continue; // covered by the packaging test above; nothing to assert here

                examined++;
                var net = WaypointNetwork.ForMap(ReadEntry(zip, entry.FullName)!, noEntities, cache, hardwired);

                // Sources the cache declares, restricted to lines where BOTH endpoints resolve to loaded
                // nodes. A line whose destination is an entity-derived waypoint (absent in this harness)
                // cannot bind here and is not evidence of a loader bug — see the doc comment.
                var declaredSources = new HashSet<int>();
                foreach (string line in cache.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("//", StringComparison.Ordinal)) continue;
                    int star = s.IndexOf('*');
                    if (star < 0) continue;
                    var from = net.FindAt(ParseVec(s[..star]));
                    var to = net.FindAt(ParseVec(s[(star + 1)..]));
                    if (from is not null && to is not null) declaredSources.Add(from.Index);
                }

                int broken = net.Nodes.Count(n => n.Links.Count == 0 && declaredSources.Contains(n.Index));
                if (broken > 0)
                    offenders.Add($"{Path.GetFileNameWithoutExtension(entry.FullName)}: "
                        + $"{broken} cache-declared source(s) with no outgoing link / {net.Count} nodes");
            }
        }

        if (examined == 0)
        {
            _out.WriteLine("no installed pack carries a .waypoints.cache yet — skipped");
            return;
        }

        _out.WriteLine($"examined {examined} cached waypoint graph(s)");
        Assert.True(offenders.Count == 0,
            "cache links were dropped on load: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// A map that ships a hand-authored <c>.waypoints</c> file still gets its teleporter/jumppad waypoints
    /// generated from the map entities (QC jumppads.qc:720 / teleporters.qc:260 spawn them independently of
    /// the file). Without this the graph has no edge through any pad and the cache's links to those nodes
    /// bind to nothing. See planning/bot-ai-parity-2026-08-03.md D3.
    /// </summary>
    [Fact]
    public void TeleporterWaypointsAreGeneratedOnTheFileLoadPath()
    {
        // A two-node hand-authored file (records are m1/m2/flags triples), plus a jumppad and its destination.
        const string wpFile = "//WAYPOINT_VERSION 1.04\n"
            + "0 0 0\n0 0 0\n0\n"
            + "512 0 0\n512 0 0\n0\n";
        var pad = new Entity
        {
            ClassName = "trigger_push",
            Origin = new System.Numerics.Vector3(64f, 0f, 0f),
            Mins = new System.Numerics.Vector3(-32f, -32f, -16f),
            Maxs = new System.Numerics.Vector3(32f, 32f, 16f),
            Target = "pad_dest",
        };
        var dest = new Entity
        {
            ClassName = "info_notnull",
            Origin = new System.Numerics.Vector3(512f, 0f, 0f),
            TargetName = "pad_dest",
        };

        var net = WaypointNetwork.ForMap(wpFile, new[] { pad, dest }, linkCacheText: "//empty\n");

        Assert.Equal(4, net.Count); // 2 authored + the pad box + its destination
        var box = net.Nodes.Single(n => n.HasFlag(WaypointFlags.Teleport));
        Assert.True(box.IsBox, "the jumppad waypoint must be a box covering the trigger volume");
        Assert.Single(box.Links);
        // QC waypoint_spawnforteleporter grows the box by the player hull so "origin inside the box" means
        // "the bot's hull is touching the trigger" (waypoints.qc:2066).
        Assert.True(box.AbsMax.X > pad.Origin.X + pad.Maxs.X,
            "the pad box must be grown by the player hull, not sized to the raw brush");
    }

    private static System.Numerics.Vector3 ParseVec(string s)
    {
        string[] p = s.Trim().Trim('\'', '"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length < 3
            ? default
            : new System.Numerics.Vector3(
                float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null) return null;
        using var s = new StreamReader(e.Open());
        return s.ReadToEnd();
    }
}
