using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Gameplay;
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
    ///
    /// <para>This is the per-host memory dial, and per-host memory is what caps the host count -- which is
    /// the most valuable axis in the system, since distinct map geometries per gradient batch is the single
    /// biggest measured effect. Tunable at runtime via <c>bot_neural_map_cache</c> so host count can be
    /// traded against cache depth without a rebuild.</para>
    /// </summary>
    public int MaxResidentMaps = (int)Cvars.FloatOr("bot_neural_map_cache", 3f);

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

        // Before the work, not after. Hosts on the Windows box wedge CPU-pinned at ~100 MB during map
        // loading, and a completion-only log cannot name the map they wedged in -- the last line shows what
        // finished, never what is stuck. This line is the difference between "it hangs somewhere in map
        // prepare" and knowing which map.
        Log?.Invoke($"[maps] preparing {name}...");
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
            field = ReadOrBakeField(name, hash, built.World);

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

    // =============================================================================================
    // course acceptance
    //
    // Measured on stage 2 with --stuck-report: of 396 agents that timed out, 11.6% were standing at
    // geodesic distance 0 from a target several hundred qu over their heads, and 27.3% were somewhere the
    // goal was no longer reachable from at all. Both are unwinnable by construction, and together they were
    // about a fifth of every agent-episode on the stage. A policy cannot train its way out of either.
    // =============================================================================================

    /// <summary>
    /// How many candidates each acceptance test threw out, and how many routes survived.
    /// </summary>
    /// <remarks>
    /// Reported rather than assumed. A filter that silently never fires looks exactly like a filter that
    /// works, and the downstream arrival rate cannot tell them apart: run-to-run spread on an 800
    /// agent-episode bench is about 2.5 points, which is larger than the effect any one of these should have.
    /// </remarks>
    public long RejectedUntouchable, RejectedStepUp, RejectedExposed, AcceptedRoutes;

    /// <summary>
    /// Whether the acceptance tests run at all. Off restores the pre-filter course pool exactly, so the
    /// filters can be A/B'd as a single binary difference over the same seeds rather than across builds.
    /// </summary>
    public bool FiltersEnabled = true;

    /// <summary>One line of <see cref="RejectedUntouchable"/> and friends, for a bench summary.</summary>
    public string FilterStats()
    {
        long seen = RejectedUntouchable + RejectedStepUp + RejectedExposed + AcceptedRoutes;
        if (seen == 0) return "course filters: no candidates seen";
        float Pct(long v) => v * 100f / seen;
        return $"course filters: accepted {AcceptedRoutes} ({Pct(AcceptedRoutes):F1}%), "
             + $"rejected untouchable-target {RejectedUntouchable} ({Pct(RejectedUntouchable):F1}%), "
             + $"step-up {RejectedStepUp} ({Pct(RejectedStepUp):F1}%), "
             + $"exposed-route {RejectedExposed} ({Pct(RejectedExposed):F1}%); "
             + $"draws by tier [strict {RelaxedDraws[0]}, no-step-bound {RelaxedDraws[1]}, "
             + $"no-exposure {RelaxedDraws[2]}, unfiltered {RelaxedDraws[3]}]";
    }

    /// <summary>
    /// Can a player standing on the surface the route floods from actually touch this target?
    /// </summary>
    /// <remarks>
    /// Targets are anchored to map entities, and plenty of them sit on geometry the baker never marked
    /// standable -- a railing, a thin ledge, a crate. The flood then roots at the floor <i>underneath</i>,
    /// so the bot navigates correctly to distance 0, stands there, and fails the arrival test forever
    /// because that is a 3D radius check against a point 350 qu overhead.
    ///
    /// <para>Standing directly beneath the target spends none of the radius horizontally, so the whole of
    /// it is available vertically. That makes this the most generous form of the test the runtime will
    /// actually apply: anything this rejects was genuinely impossible.</para>
    /// </remarks>
    private static bool TargetIsTouchable(NavField field, Vector3 target)
    {
        if (!field.TrySampleBelow(target, out FloorSpan span)) return false;
        float originZ = span.FloorZ - SpawnSystem.PlayerMins.Z;
        return MathF.Abs(target.Z - originZ) <= TrainingEnv.ArriveRadius;
    }

    /// <summary>Spacing of the walk along a candidate route, in qu. Finer than a nav cell diagonal.</summary>
    private const float RouteSampleSpacing = 48f;

    /// <summary>Cap on route samples, so a pathological descent cannot spin. 96 x 48 qu covers 4600 qu.</summary>
    private const int RouteSampleLimit = 96;

    /// <summary>
    /// The tallest upward step along the route, in qu, or 0 for a route that only ever descends or stays level.
    /// </summary>
    /// <remarks>
    /// Walks the same greedy descent the observation's corridor uses, so this measures the route the bot is
    /// actually being pointed down rather than the straight line to the target.
    /// </remarks>
    private static float MaxStepUpAlongRoute(NavField field, NavDistanceField dist, Vector3 spawn)
    {
        float worst = 0f;
        Vector3 cur = spawn;
        float prevFloor = field.GroundHeight(cur, cur.Z);

        for (int i = 0; i < RouteSampleLimit; i++)
        {
            Vector3 next = dist.PointAlongRoute(cur, RouteSampleSpacing);
            // PointAlongRoute returns its input when the position is off-graph, and the goal itself once the
            // route is shorter than the look-ahead. Either way there is nothing further to walk.
            if ((next - cur).LengthSquared() < 1f) break;

            float floor = field.GroundHeight(next, next.Z);
            worst = MathF.Max(worst, floor - prevFloor);
            prevFloor = floor;
            cur = next;
        }
        return worst;
    }

    /// <summary>A drop this deep or deeper cannot be climbed back up without a jump pad or a weapon.</summary>
    private const float UnrecoverableDrop = 128f;

    /// <summary>Lateral probe distance when testing what a route runs alongside, in qu (one nav cell).</summary>
    private const float LedgeProbe = 32f;

    /// <summary>
    /// Reject if more than this share of route samples sit beside space the goal is unreachable from.
    /// </summary>
    /// <remarks>
    /// Not zero: real arenas are full of railed walkways over pits, and a route is allowed to pass one. What
    /// this rejects is a route that spends much of its length on the lip of one, where a single missed input
    /// ends the episode with 45 s left on the clock and no path back.
    /// </remarks>
    private const float MaxExposedShare = 0.25f;

    /// <summary>
    /// Does this route spend much of its length beside a drop the goal is not reachable from?
    /// </summary>
    private static bool RouteHugsUnrecoverableDrop(NavField field, NavDistanceField dist, Vector3 spawn)
    {
        ReadOnlySpan<float> dxs = stackalloc float[] { 1f, 0.707f, 0f, -0.707f, -1f, -0.707f, 0f, 0.707f };
        ReadOnlySpan<float> dys = stackalloc float[] { 0f, 0.707f, 1f, 0.707f, 0f, -0.707f, -1f, -0.707f };

        Vector3 cur = spawn;
        int samples = 0, exposed = 0;

        for (int i = 0; i < RouteSampleLimit; i++)
        {
            float floor = field.GroundHeight(cur, cur.Z);
            samples++;

            for (int d = 0; d < 8; d++)
            {
                var probe = new Vector3(cur.X + dxs[d] * LedgeProbe, cur.Y + dys[d] * LedgeProbe, cur.Z);
                // No floor at all beneath the probe is the void: falling there is a death, not a strand.
                if (!field.TrySampleBelow(probe, out FloorSpan span)) continue;
                if (floor - span.FloorZ < UnrecoverableDrop) continue;

                // Deep enough to be one-way. Only counts if you also cannot route to the goal from down there.
                var landing = new Vector3(probe.X, probe.Y, span.FloorZ + 1f);
                if (dist.DistanceAt(landing) >= NavDistanceField.Unreachable) { exposed++; break; }
            }

            Vector3 next = dist.PointAlongRoute(cur, RouteSampleSpacing);
            if ((next - cur).LengthSquared() < 1f) break;
            cur = next;
        }
        return samples > 0 && exposed / (float)samples > MaxExposedShare;
    }

    /// <summary>How many draws each relaxation tier had to serve. Tier 0 is the stage's real constraints.</summary>
    public readonly long[] RelaxedDraws = new long[RelaxTiers];

    /// <summary>Strict, then without the step-up bound, then without the exposure test, then unfiltered.</summary>
    private const int RelaxTiers = 4;

    /// <summary>
    /// Draw an episode, relaxing the acceptance tests only as far as it takes to find one.
    /// </summary>
    /// <remarks>
    /// A training run must not die because one draw was unlucky, and v15 did exactly that: it ran 19.4M
    /// steps and then threw "no map in the pool produced a reachable A/B pair" out of a host reset. The
    /// step-up bound rejects about 70% of candidates on stage 2, so twelve map draws by twenty-four spawn
    /// picks eventually all miss. A forty-episode bench never sees it; thousands of resets across
    /// thirty-two hosts do.
    ///
    /// <para>Relaxing beats failing, but silently relaxing would be worse than either -- a stage whose
    /// constraints never actually bind is not the stage it claims to be. <see cref="RelaxedDraws"/> counts
    /// what each tier served so the log can show it.</para>
    /// </remarks>
    public (PreparedMap Map, Vector3 Spawn, Vector3 Target, NavDistanceField Distance)? NextEpisode(
        Random rng, float minRouteLength = 700f, float maxRouteLength = float.PositiveInfinity,
        float maxStepUp = float.PositiveInfinity)
    {
        for (int tier = 0; tier < RelaxTiers; tier++)
        {
            var draw = TryDraw(rng, minRouteLength, maxRouteLength,
                               stepUp: tier >= 1 ? float.PositiveInfinity : maxStepUp,
                               testExposure: tier < 2,
                               testTouchable: tier < 3);
            if (draw is not null)
            {
                RelaxedDraws[tier]++;
                return draw;
            }
        }
        return null;
    }

    private (PreparedMap Map, Vector3 Spawn, Vector3 Target, NavDistanceField Distance)? TryDraw(
        Random rng, float minRouteLength, float maxRouteLength,
        float stepUp, bool testExposure, bool testTouchable)
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
            // F1: unwinnable however well it navigates.
            if (FiltersEnabled && testTouchable && !TargetIsTouchable(map.Field, target))
            { RejectedUntouchable++; continue; }
            NavDistanceField dist = NavDistanceField.Build(map.Field, target);
            if (dist.ReachedSpans < 32) continue;   // the target sits somewhere the field cannot route to

            for (int pick = 0; pick < 24; pick++)
            {
                Vector3 spawn = map.Anchors[rng.Next(map.Anchors.Count)];
                float d = dist.DistanceAt(spawn);
                if (d >= NavDistanceField.Unreachable || d < minRouteLength || d > maxRouteLength) continue;
                // Route shape last: both walk the descent, so they only run on a candidate that already
                // passed the cheap length test.
                if (FiltersEnabled && MaxStepUpAlongRoute(map.Field, dist, spawn) > stepUp) { RejectedStepUp++; continue; }
                if (FiltersEnabled && testExposure && RouteHugsUnrecoverableDrop(map.Field, dist, spawn))
                { RejectedExposed++; continue; }
                AcceptedRoutes++;
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
    /// <summary>
    /// A scratch distance buffer, reused across probe attempts.
    ///
    /// <para>Every rejected attempt used to allocate and discard a float[spans] on the Large Object Heap,
    /// and with per-agent routes this runs up to eight times per agent -- 160 floods an episode at 20
    /// agents. Measured before this: a 120-episode bench peaked at 5.6 GB resident and every training run
    /// on the box ended in an out-of-memory kill. Only the accepted route allocates now.</para>
    /// </summary>
    private float[]? _probeBuffer;

    public (Vector3 Spawn, Vector3 Target, NavDistanceField Distance)? NextRouteOn(
        PreparedMap map, Random rng, float minRouteLength = 700f,
        float maxRouteLength = float.PositiveInfinity,
        float maxStepUp = float.PositiveInfinity)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector3 target = map.Anchors[rng.Next(map.Anchors.Count)];
            if (FiltersEnabled && !TargetIsTouchable(map.Field, target)) { RejectedUntouchable++; continue; }
            NavDistanceField probe = NavDistanceField.Build(map.Field, target, _probeBuffer);
            _probeBuffer = probe.Buffer;
            if (probe.ReachedSpans < 32) continue;

            for (int pick = 0; pick < 24; pick++)
            {
                Vector3 spawn = map.Anchors[rng.Next(map.Anchors.Count)];
                float d = probe.DistanceAt(spawn);
                if (d >= NavDistanceField.Unreachable || d < minRouteLength || d > maxRouteLength) continue;
                if (FiltersEnabled && MaxStepUpAlongRoute(map.Field, probe, spawn) > maxStepUp) { RejectedStepUp++; continue; }
                if (FiltersEnabled && RouteHugsUnrecoverableDrop(map.Field, probe, spawn)) { RejectedExposed++; continue; }
                AcceptedRoutes++;
                // The caller keeps this one, so it needs its own buffer; the probe buffer goes on being
                // reused by the next agent's draw.
                return (spawn, target, probe.DetachCopy());
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

    private NavField ReadOrBakeField(string name, ulong hash, CollisionWorld world)
    {
        // Host processes share this cache. The old "racy by design" path was tolerable for small fields,
        // but a migrated/stale real-map field made eight evaluators bake the same geometry concurrently.
        // They saturated the box for minutes and could overwrite one another's result. FileShare.None is a
        // cross-process lease; a crashed baker releases it automatically even though the tiny lock file stays.
        Directory.CreateDirectory(CacheDir);
        string lockPath = Path.Combine(CacheDir, name + ".navfield.lock");
        FileStream? lease = null;
        bool announced = false;
        while (lease is null)
        {
            try
            {
                lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (!announced)
                {
                    Log?.Invoke($"[maps] waiting for another process to bake {name}...");
                    announced = true;
                }
                Thread.Sleep(1000);
            }
        }

        using (lease)
        {
            // The process that held the lease may have completed while we waited.
            NavField? cached = TryReadCachedField(name, hash);
            if (cached is not null)
            {
                if (announced) Log?.Invoke($"[maps] loaded {name} after shared bake completed");
                return cached;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log?.Invoke($"[maps] baking {name} nav field...");
            using var heartbeat = new Timer(
                _ => Log?.Invoke($"[maps] baking {name} nav field... {sw.Elapsed.TotalSeconds:F0}s"),
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            NavField baked = NavFieldBaker.BakeParallel(world, name, hash, threads: BakeThreads);
            sw.Stop();
            TryWriteCachedField(name, baked);
            Log?.Invoke($"[maps] baked {name} nav field in {sw.Elapsed.TotalSeconds:F1}s");
            return baked;
        }
    }

    private void TryWriteCachedField(string name, NavField field)
    {
        // Best effort. Callers hold the per-map bake lease, so only one process writes this path at a time.
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
