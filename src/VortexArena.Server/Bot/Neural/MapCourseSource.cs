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
    /// <summary>
    /// How many prepared maps to keep resident. Least-recently-used beyond this are dropped.
    ///
    /// <para>This cache used to be unbounded, which is fine for one process and not fine for twenty. A
    /// prepared map holds its BSP, collision world, submodels and baked nav field, and the training run
    /// puts one of these sources in EVERY host process -- so the footprint is per-host and the pool is the
    /// whole installed map set. A 24-host run was killed by the OOM killer at roughly 650 MB per host
    /// against 16 GB total, and stage 6 is the only stage that loads real maps at all, so the growth
    /// arrives exactly where there is least headroom left.</para>
    ///
    /// <para>Dropping a map costs a re-prepare if it comes back, but the nav field is cached on disk under
    /// <see cref="CacheDir"/>, so that re-prepare is a BSP parse and a field load rather than a re-bake.</para>
    /// </summary>
    public int MaxResidentMaps = 4;

    private readonly Dictionary<string, PreparedMap> _prepared = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Preparation order, oldest first. Used to pick the eviction victim.</summary>
    private readonly List<string> _residentOrder = new();
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
        if (_prepared.TryGetValue(name, out PreparedMap? cached))
        {
            Touch(name);
            return cached;
        }

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
        // Fall back to the map's own spawn points and item pickups when there is no waypoint graph.
        //
        // Both are hand-placed by the mapper at standable, reachable positions -- that is what they are FOR
        // -- so they satisfy the one thing an anchor has to be. They are sparser and less evenly spread than
        // a waypoint graph, which is why they are the fallback rather than the default, but a map with no
        // waypoints was previously skipped outright: eggandbacon, a large room with obstacles and the most
        // useful simple map in the pool, never appeared in training at all.
        //
        // Reachability is not assumed from this. NextEpisode still floods the baked field from the target
        // and rejects any pair it cannot route between, so a badly placed anchor costs a redraw, not a
        // broken episode.
        if (anchors.Count < 4)
        {
            foreach (Dictionary<string, string> e in bsp.Entities)
            {
                if (!e.TryGetValue("classname", out string? cn) || cn is null) continue;
                if (!IsAnchorEntity(cn)) continue;
                if (!e.TryGetValue("origin", out string? os) || !TryVec(os, out Vector3 at)) continue;
                anchors.Add(at + new Vector3(0f, 0f, 24f));
            }
            if (anchors.Count >= 4)
                Log?.Invoke($"[maps] {name} has no waypoint graph; using {anchors.Count} spawn/item anchors");
        }

        if (anchors.Count < 4)
        {
            Log?.Invoke($"[maps] {name} has no usable anchors ({anchors.Count}) — skipping");
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
        Touch(name);
        Evict();
        Log?.Invoke($"[maps] prepared {name} in {sw.Elapsed.TotalMilliseconds:F0} ms " +
                    $"({anchors.Count} anchors, {field.OccupiedColumns} columns, {field.SpanCount} spans, " +
                    $"field {(fromCache ? "cached" : "baked")})");
        return prepared;
    }

    /// <summary>
    /// Entity classes whose origin is a position a player can stand at.
    ///
    /// <para>Spawn points first, because a mapper guarantees those are legal standing positions. Item and
    /// weapon pickups are placed where a player is expected to run, which is the same property an episode
    /// endpoint wants. Deliberately NOT included: projectiles, triggers, lights, and anything whose origin
    /// is a brush centre rather than a floor position.</para>
    /// </summary>
    private static bool IsAnchorEntity(string classname) =>
        classname.StartsWith("info_player_", StringComparison.OrdinalIgnoreCase)
        || classname.StartsWith("item_", StringComparison.OrdinalIgnoreCase)
        || classname.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mark a map as most recently used.</summary>
    private void Touch(string name)
    {
        _residentOrder.Remove(name);
        _residentOrder.Add(name);
    }

    /// <summary>Drop least-recently-used maps until at most <see cref="MaxResidentMaps"/> remain.</summary>
    private void Evict()
    {
        while (_residentOrder.Count > MaxResidentMaps && _residentOrder.Count > 1)
        {
            string victim = _residentOrder[0];
            _residentOrder.RemoveAt(0);
            _prepared.Remove(victim);
            Log?.Invoke($"[maps] evicted {victim} ({_residentOrder.Count} resident)");
        }
    }

    /// <summary>
    /// Draw the next episode: a map, an origin and a target far enough apart to be a route.
    ///
    /// <para>The pair is rejected and redrawn when the target is not reachable from the origin through the
    /// baked field. An unreachable pair is not a hard episode, it is an impossible one, and a curriculum
    /// full of impossible episodes teaches the policy that arriving is not achievable.</para>
    /// </summary>
    /// <summary>
    /// Restrict draws to these maps, or empty for the whole pool. Names only; the held-out split is still
    /// refused even if named here.
    /// </summary>
    public string[] Only = Array.Empty<string>();

    public (PreparedMap Map, Vector3 Spawn, Vector3 Target, NavDistanceField Distance)? NextEpisode(
        Random rng, float minRouteLength = 700f, float maxRouteLength = float.PositiveInfinity)
    {
        // A handful of attempts, then give up for this episode rather than spin: a map whose graph is all
        // short hops has no long routes to find however long we look for one.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            string name = Only.Length > 0
                ? Only[rng.Next(Only.Length)]
                : _pool[rng.Next(_pool.Count)];
            PreparedMap? map = Prepare(name);
            if (map is null) continue;

            Vector3 target = map.Anchors[rng.Next(map.Anchors.Count)];
            NavDistanceField dist = NavDistanceField.Build(map.Field, target);
            if (dist.ReachedSpans < 32) continue;   // the target sits somewhere the field cannot route to

            for (int pick = 0; pick < 24; pick++)
            {
                Vector3 spawn = map.Anchors[rng.Next(map.Anchors.Count)];
                float d = dist.DistanceAt(spawn);
                if (d >= NavDistanceField.Unreachable || d < minRouteLength || d > maxRouteLength) continue;
                return (map, spawn, target, dist);
            }
        }
        return null;
    }

    /// <summary>
    /// Another route through a map that is already prepared and already loaded into a world.
    ///
    /// <para>This is <see cref="NextEpisode"/> without the map draw, so every agent sharing a host can run
    /// its own route. Geometry cannot vary within a host -- one GameWorld holds one map -- but the route
    /// can, and it costs one Dijkstra flood plus a float per navigation cell. At 16 agents that turns 20
    /// hosts into 320 distinct routes rather than 20.</para>
    /// </summary>
    public (Vector3 Spawn, Vector3 Target, NavDistanceField Distance)? NextRouteOn(
        PreparedMap map, Random rng, float minRouteLength = 700f,
        float maxRouteLength = float.PositiveInfinity)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector3 target = map.Anchors[rng.Next(map.Anchors.Count)];
            NavDistanceField dist = NavDistanceField.Build(map.Field, target);
            if (dist.ReachedSpans < 32) continue;

            for (int pick = 0; pick < 24; pick++)
            {
                Vector3 spawn = map.Anchors[rng.Next(map.Anchors.Count)];
                float d = dist.DistanceAt(spawn);
                if (d >= NavDistanceField.Unreachable || d < minRouteLength || d > maxRouteLength) continue;
                return (spawn, target, dist);
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
