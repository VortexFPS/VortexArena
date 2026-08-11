using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VortexArena.Common.Framework;
using VortexArena.Engine.Collision;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Server-wide owner of everything the neural bots share: the policy weights, the map's baked navigation
/// field, and the map feature list. One instance per <see cref="GameWorld"/>.
///
/// <para><b>Failure is always a fallback, never an exception.</b> A missing weight file, a stale field, a
/// bake that has not finished: each of these leaves <see cref="Ready"/> false and the bots on the classic
/// steer. A neural bot feature that can stop a match from running is worse than no neural bots.</para>
/// </summary>
public sealed class NeuralBotService
{
    /// <summary>Where a weight file lives when <c>bot_neural_weights</c> is a bare name.</summary>
    public const string DefaultWeightsPath = "data/neural/policy.vxpw";

    private PolicyNetwork? _net;
    private NavField? _field;
    private MapFeatures? _features;
    private Task? _bakeTask;
    private CancellationTokenSource? _bakeCancel;
    private string? _loadedWeightsPath;
    private bool _loadFailureLogged;
    private string _mapName = "";

    /// <summary>Diagnostics for the <c>bot_neural_status</c> console command.</summary>
    public string StatusLine { get; private set; } = "not initialised";

    /// <summary>True when a policy is loaded AND its geometry is available: the bots may use it.</summary>
    public bool Ready => _net is not null && _field is not null;

    /// <summary>The shared policy, or null.</summary>
    public PolicyNetwork? Network => _net;

    /// <summary>The map's baked field, or null while a bake is in flight.</summary>
    public NavField? Field => _field;

    /// <summary>The map's furniture list, or null.</summary>
    public MapFeatures? Features => _features;

    /// <summary>Set when the bake ran at load rather than coming off disk, for the status report.</summary>
    public bool FieldWasBaked { get; private set; }

    /// <summary>Wall-clock milliseconds the bake took, 0 when it was loaded from disk.</summary>
    public double BakeMilliseconds { get; private set; }

    /// <summary>
    /// Reads a file from the game's virtual filesystem. The host sets this so a field cached inside a
    /// <c>.pk3</c> is found by the same reader the waypoint loader uses; null falls back to the real
    /// filesystem.
    /// </summary>
    public Func<string, byte[]?>? VfsReader;

    /// <summary>Console sink. Null routes to nothing, which is what the headless tests want.</summary>
    public Action<string>? Log;

    /// <summary>
    /// A service holding resources the caller already has, with nothing to load and nothing to bake.
    ///
    /// <para>For the training environment and the eval harness, which build a course, bake its field inline
    /// and then need a service to hand the brain. Going through <see cref="BeginMap"/> would mean an
    /// off-thread bake racing the first step, which is exactly the nondeterminism a trainer must not have.</para>
    /// </summary>
    public static NeuralBotService ForPreparedMap(PolicyNetwork net, NavField field, MapFeatures features, string mapName)
    {
        var svc = new NeuralBotService
        {
            _net = net,
            _field = field,
            _features = features,
            _mapName = mapName,
            _loadedWeightsPath = "(supplied)",
        };
        svc.UpdateStatus();
        return svc;
    }

    /// <summary>
    /// Load or bake everything for a map. Returns immediately; a bake that has to run happens on the thread
    /// pool and <see cref="Ready"/> flips when it lands.
    /// </summary>
    public void BeginMap(string mapName, CollisionWorld world, IReadOnlyList<Entity> entities, string weightsPath,
        bool allowBake = true)
    {
        CancelBake();
        _mapName = mapName;
        _field = null;
        FieldWasBaked = false;
        BakeMilliseconds = 0;

        _features = new MapFeatures();
        _features.Build(entities);

        LoadWeights(weightsPath);

        ulong hash = NavFieldIo.GeometryHash(world);

        // Prefer a cached field. Reading one is a few milliseconds; baking is seconds.
        NavField? cached = TryReadCachedField(mapName);
        if (cached is not null && cached.GeometryHash == hash)
        {
            _field = cached;
            UpdateStatus();
            return;
        }
        if (cached is not null)
            Log?.Invoke($"neural: cached nav field for {mapName} was baked against different geometry; re-baking");

        if (!allowBake)
        {
            UpdateStatus();
            Log?.Invoke($"neural: no compatible cached nav field for {mapName}; bot_neural_bake is 0");
            return;
        }

        // Bake off-thread. Bots run the classic steer until this lands; see NavFieldBaker's cost note and
        // parity finding D1 for why this must not block the load.
        var cts = new CancellationTokenSource();
        _bakeCancel = cts;
        // Snapshot the entity list: the bake threads must not walk a collection the sim is mutating.
        var snapshot = new List<Entity>(entities);
        _bakeTask = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            NavField baked = NavFieldBaker.BakeParallel(world, mapName, hash, snapshot);
            sw.Stop();
            if (cts.IsCancellationRequested) return;
            BakeMilliseconds = sw.Elapsed.TotalMilliseconds;
            FieldWasBaked = true;
            _field = baked;
            UpdateStatus();
            Log?.Invoke($"neural: baked nav field for {mapName} in {sw.Elapsed.TotalMilliseconds:F0} ms " +
                        $"({baked.OccupiedColumns} columns, {baked.SpanCount} spans, {baked.ApproxBytes / 1024} KB)");
            TryWriteCachedField(mapName, baked);
        }, cts.Token);

        UpdateStatus();
    }

    /// <summary>Stop an in-flight bake (map change, shutdown).</summary>
    public void CancelBake()
    {
        _bakeCancel?.Cancel();
        _bakeCancel = null;
        _bakeTask = null;
    }

    /// <summary>Block until an in-flight bake completes. For tests and the offline baker, not the server.</summary>
    public void WaitForBake(TimeSpan timeout)
    {
        Task? t = _bakeTask;
        if (t is null) return;
        try { t.Wait(timeout); }
        catch (AggregateException e) { Log?.Invoke($"neural: bake failed: {e.InnerException?.Message ?? e.Message}"); }
    }

    /// <summary>
    /// (Re)load the policy weights. A failure logs once and leaves the previous network in place, so a typo
    /// in the cvar does not silently disarm bots that were working a second ago.
    /// </summary>
    public bool LoadWeights(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = DefaultWeightsPath;
        if (_net is not null && string.Equals(path, _loadedWeightsPath, StringComparison.OrdinalIgnoreCase))
            return true;

        PolicyNetwork? net = PolicyNetwork.Load(path, out string? error);
        if (net is null)
        {
            if (!_loadFailureLogged)
            {
                Log?.Invoke($"neural: {error} — bots stay on the classic steer");
                _loadFailureLogged = true;
            }
            UpdateStatus();
            return false;
        }

        if (net.InputSize != NeuralObservation.Size || net.OutputSize != ActionSpace.Size)
        {
            Log?.Invoke($"neural: {path} is {net.InputSize}x{net.OutputSize}, this build needs " +
                        $"{NeuralObservation.Size}x{ActionSpace.Size} — refusing to load");
            UpdateStatus();
            return false;
        }

        _net = net;
        _loadedWeightsPath = path;
        _loadFailureLogged = false;
        Log?.Invoke($"neural: loaded policy '{net.Label}' ({net.ParameterCount} parameters) from {path}");
        UpdateStatus();
        return true;
    }

    private NavField? TryReadCachedField(string mapName)
    {
        string vfsPath = NavFieldIo.FileNameFor(mapName);
        try
        {
            if (VfsReader?.Invoke(vfsPath) is { } bytes)
            {
                using var ms = new MemoryStream(bytes, writable: false);
                return NavFieldIo.Read(ms);
            }
            string disk = Path.Combine("data", vfsPath);
            if (File.Exists(disk))
            {
                using FileStream fs = File.OpenRead(disk);
                return NavFieldIo.Read(fs);
            }
        }
        catch (IOException) { /* fall through to a bake */ }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private void TryWriteCachedField(string mapName, NavField field)
    {
        // Best effort. A read-only content tree (the normal case for a shipped install) just means the next
        // boot bakes again, which is a few seconds off the sim thread, not a failure.
        try
        {
            string disk = Path.Combine("data", NavFieldIo.FileNameFor(mapName));
            string? dir = Path.GetDirectoryName(disk);
            if (dir is null || !Directory.Exists(dir)) return;
            using FileStream fs = File.Create(disk);
            NavFieldIo.Write(fs, field);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void UpdateStatus()
    {
        string weights = _net is null ? "no policy" : $"policy '{_net.Label}' ({_net.ParameterCount} params)";
        string geo = _field is null
            ? (_bakeTask is { IsCompleted: false } ? "field baking" : "no field")
            : $"field {_field.OccupiedColumns} cols / {_field.SpanCount} spans" +
              (FieldWasBaked ? $" (baked {BakeMilliseconds:F0} ms)" : " (cached)");
        string feats = _features is null ? "no features" : $"{_features.Count} features";
        StatusLine = $"{(Ready ? "ready" : "inactive")}: {weights}; {geo}; {feats}; map {_mapName}";
    }
}
