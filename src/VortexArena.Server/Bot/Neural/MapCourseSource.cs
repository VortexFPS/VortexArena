using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Curriculum stage 6: episodes on the game's real maps instead of generated obstacle courses.
///
/// <para><b>Why this stage exists, measured rather than assumed.</b> A policy trained through stages 1 to 5
/// scores 97% on the corridor stage and 71% on the terrain stage, against 22% and 3.5% for a scripted
/// straight-line runner. On stormkeep it finishes 3 routes of 8, where the classic waypoint steer finishes
/// 7. Generated geometry teaches locomotion; it does not contain stairwells, doorways a hull barely fits,
/// railings, or the multi-level loops a real arena is made of. Those have to be trained on, and this is
/// what trains on them.</para>
///
/// <para><b>The held-out split is the point.</b> Risk R-N1 in
/// <c>planning/neural-bots-2026-08-07.md</c> is that the policy memorises maps. Training on all 32 and then
/// reporting on all 32 cannot detect that, so <see cref="HeldOut"/> maps are refused at load: a map named
/// there will not be trained on however it is spelled in the map list. Choose the split before the run,
/// not after seeing the results.</para>
///
/// <para><b>Maps are loaded once and reused.</b> Parsing a BSP, building its collision world and baking its
/// navigation field is roughly half a second; an episode is a couple of seconds. Loading per episode would
/// spend a quarter of training on I/O, so each map is prepared on first use and kept.</para>
/// </summary>
public sealed class MapCourseSource
{
    /// <summary>One prepared map, reused across every episode that draws it.</summary>
    public sealed class PreparedMap
    {
        public string Name = "";
        public BspData Bsp = null!;
        public CollisionWorld World = null!;
        public IReadOnlyList<BspCollisionBuilder.Submodel> Submodels = Array.Empty<BspCollisionBuilder.Submodel>();
        public NavField Field = null!;
        /// <summary>Standable points drawn from the waypoint graph, used as episode origins and targets.</summary>
        public List<Vector3> Anchors = new();
        /// <summary>The map's entity lump, as spawn dictionaries.</summary>
        public List<EntityDict> Entities = new();
    }

    /// <summary>
    /// Maps never trained on, whatever the map list says. The eval split.
    ///
    /// <para>Three maps of the shipped 32, picked for variety rather than convenience: a large open arena,
    /// a tight multi-level one, and one built around a central pit. Change this set deliberately and record
    /// why; silently widening it is how a generalisation claim becomes untrue.</para>
    /// </summary>
    public static readonly string[] HeldOut = { "catharsis", "fuse", "afterslime" };

    /// <summary>
    /// Where baked fields are cached between runs and between host processes.
    ///
    /// <para>Without this every host bakes every map it draws, so a six-host run does the same work six
    /// times and pays it again on the next run. The bake is deterministic given the geometry, and the
    /// stored hash means a recompiled map is re-baked rather than silently reused.</para>
    /// </summary>
    public string CacheDir = Path.Combine("_scratch", "navfields");

    /// <summary>
    /// Bake threads per map. Deliberately small, because several host processes share one machine: at the
    /// default of "all but two cores" a six-host run spawns 132 threads onto 24 and they mostly wait for
    /// each other.
    /// </summary>
    public int BakeThreads = 2;

    /// <summary>Exposes the VFS so a caller can wire GameWorld.ConfigReader to the same mounted content.</summary>
    public VirtualFileSystem Vfs => _vfs;

    private readonly VirtualFileSystem _vfs = new();
    private readonly Dictionary<string, PreparedMap> _prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pool = new();
    private readonly string _dataRoot;

    /// <summary>Console sink; null stays silent.</summary>
    public Action<string>? Log;

    /// <summary>Maps available to train on after the held-out set is removed.</summary>
    public IReadOnlyList<string> Pool => _pool;

    /// <summary>
    /// Mount <paramref name="dataRoot"/> and work out which maps are trainable.
    /// </summary>
    /// <param name="dataRoot">The game's content root (the directory holding <c>maps/</c>).</param>
    /// <param name="mapList">
    /// Comma-separated map names, or empty for "every installed map". Held-out maps are removed either way.
    /// </param>
    public MapCourseSource(string dataRoot, string mapList = "")
    {
        _dataRoot = dataRoot;
        if (!_vfs.MountContentRoot(dataRoot))
            throw new InvalidOperationException($"no content at {dataRoot}");

        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(mapList)
            ? DiscoverInstalledMaps()
            : mapList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string m in candidates)
        {
            if (HeldOut.Contains(m, StringComparer.OrdinalIgnoreCase))
            {
                Log?.Invoke($"[maps] {m} is held out for eval — not training on it");
                continue;
            }
            if (!_vfs.Exists($"maps/{m}.bsp"))
            {
                Log?.Invoke($"[maps] {m} is not installed — skipping");
                continue;
            }
            _pool.Add(m);
        }

        if (_pool.Count == 0)
            throw new InvalidOperationException(
                $"no trainable maps under {dataRoot} (held out: {string.Join(", ", HeldOut)})");
    }

    /// <summary>Every map with a <c>.bsp</c> in the mounted content, by scanning the pack index.</summary>
    private IEnumerable<string> DiscoverInstalledMaps()
    {
        // The VFS has no directory listing, so probe the shipped map names from data/maps/*.pk3, which is
        // how the fetcher installs them (restructure D7). A pack is named for its map.
        string mapsDir = System.IO.Path.Combine(_dataRoot, "maps");
        if (!System.IO.Directory.Exists(mapsDir)) return Array.Empty<string>();
        return System.IO.Directory.EnumerateFiles(mapsDir, "*.pk3")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith('_'))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal);
    }

    /// <summary>
    /// The prepared form of <paramref name="name"/>, building it on first use. Returns null when the map
    /// has no usable waypoint graph to draw episode endpoints from.
    /// </summary>
    public PreparedMap? Prepare(string name)
    {
        if (_prepared.TryGetValue(name, out PreparedMap? cached)) return cached;

        string bspPath = $"maps/{name}.bsp";
        if (!_vfs.Exists(bspPath)) return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        BspData bsp = BspReader.Read(_vfs.ReadBytes(bspPath));
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp);

        // Anchors come from the waypoint graph. It is training SCAFFOLDING and never a policy input: it
        // decides WHERE to run, never how. Using it here is what makes an episode a route a player could
        // actually take, rather than two random points that may be inside a wall.
        var anchors = new List<Vector3>();
        string wpPath = $"maps/{name}.waypoints";
        if (_vfs.Exists(wpPath))
        {
            WaypointNetwork net = WaypointNetwork.LoadFromText(_vfs.ReadText(wpPath));
            foreach (Waypoint w in net.Nodes)
                anchors.Add(w.Center + new Vector3(0f, 0f, 24f));
        }
        if (anchors.Count < 4)
        {
            Log?.Invoke($"[maps] {name} has no usable waypoint graph ({anchors.Count} nodes) — skipping");
            return null;
        }

        ulong hash = NavFieldIo.GeometryHash(built.World);
        NavField? field = TryReadCachedField(name, hash);
        bool fromCache = field is not null;
        if (field is null)
        {
            field = NavFieldBaker.BakeParallel(built.World, name, hash, threads: BakeThreads);
            TryWriteCachedField(name, field);
        }

        var entities = new List<EntityDict>();
        foreach (Dictionary<string, string> e in bsp.Entities)
        {
            if (!e.TryGetValue("classname", out string? cn) || cn is null) continue;
            var d = new EntityDict(cn);
            if (e.TryGetValue("origin", out string? os) && TryVec(os, out Vector3 origin)) d.Origin = origin;
            foreach (KeyValuePair<string, string> kv in e)
                if (!kv.Key.Equals("classname", StringComparison.OrdinalIgnoreCase))
                    d.Fields[kv.Key] = kv.Value;
            entities.Add(d);
        }

        sw.Stop();
        var prepared = new PreparedMap
        {
            Name = name,
            Bsp = bsp,
            World = built.World,
            Submodels = built.Submodels,
            Field = field,
            Anchors = anchors,
            Entities = entities,
        };
        _prepared[name] = prepared;
        Log?.Invoke($"[maps] prepared {name} in {sw.Elapsed.TotalMilliseconds:F0} ms " +
                    $"({anchors.Count} anchors, {field.OccupiedColumns} columns, {field.SpanCount} spans, " +
                    $"field {(fromCache ? "cached" : "baked")})");
        return prepared;
    }

    /// <summary>
    /// Draw the next episode: a map, an origin and a target far enough apart to be a route.
    ///
    /// <para>The pair is rejected and redrawn when the target is not reachable from the origin through the
    /// baked field. An unreachable pair is not a hard episode, it is an impossible one, and a curriculum
    /// full of impossible episodes teaches the policy that arriving is not achievable.</para>
    /// </summary>
    public (PreparedMap Map, Vector3 Spawn, Vector3 Target, NavDistanceField Distance)? NextEpisode(
        Random rng, float minRouteLength = 700f)
    {
        // A handful of attempts, then give up for this episode rather than spin: a map whose graph is all
        // short hops has no long routes to find however long we look for one.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            string name = _pool[rng.Next(_pool.Count)];
            PreparedMap? map = Prepare(name);
            if (map is null) continue;

            Vector3 target = map.Anchors[rng.Next(map.Anchors.Count)];
            NavDistanceField dist = NavDistanceField.Build(map.Field, target);
            if (dist.ReachedSpans < 32) continue;   // the target sits somewhere the field cannot route to

            for (int pick = 0; pick < 24; pick++)
            {
                Vector3 spawn = map.Anchors[rng.Next(map.Anchors.Count)];
                float d = dist.DistanceAt(spawn);
                if (d >= NavDistanceField.Unreachable || d < minRouteLength) continue;
                return (map, spawn, target, dist);
            }
        }
        return null;
    }

    private NavField? TryReadCachedField(string name, ulong hash)
    {
        try
        {
            string path = Path.Combine(CacheDir, name + ".navfield");
            if (!File.Exists(path)) return null;
            using FileStream fs = File.OpenRead(path);
            NavField? f = NavFieldIo.Read(fs);
            if (f is null) return null;
            if (f.GeometryHash == hash) return f;
            Log?.Invoke($"[maps] cached field for {name} was baked against different geometry; re-baking");
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void TryWriteCachedField(string name, NavField field)
    {
        // Best effort, and racy by design: several hosts may bake the same map at once and the last write
        // wins. They are writing identical bytes, so the only cost is duplicated work on the first run.
        try
        {
            Directory.CreateDirectory(CacheDir);
            string tmp = Path.Combine(CacheDir, name + $".navfield.{Environment.ProcessId}.tmp");
            using (FileStream fs = File.Create(tmp)) NavFieldIo.Write(fs, field);
            File.Move(tmp, Path.Combine(CacheDir, name + ".navfield"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryVec(string s, out Vector3 v)
    {
        v = default;
        string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }
}
