// Port of Base/darkplaces/cl_parse.c CL_BeginDownloads as far as it loads a level's precached models and
// sounds ("cl.model_precache[cl.loadmodel_current] = Mod_ForName(...)", "cl.sound_precache[...] =
// S_PrecacheSound(...)") before the client says "begin": DarkPlaces has every model and sound the server and
// the client program named in memory before the first frame of the level is drawn. Here the same work is
// spread over worker threads and started as early as the names can be guessed, so that it costs the
// loading screen as little as possible.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // How many of a level's precached models are built ahead at most, and how many sounds decoded: bounds on
    // what a server's precache lists may ask of this machine's memory before the level has begun.
    private const int MaxPrecachedModels = 1024;
    private const int MaxPrecachedSounds = 2048;
    private const int MaxPrecachedSkins = 8;

    // The reading, parsing and decoding of a level's files is spread over this many threads while the main
    // thread builds the map and starts the client program.
    private static int PrecacheWorkers => Math.Clamp(System.Environment.ProcessorCount / 2, 2, 8);

    // Developer aid, as the other VORTEX_LEGACY_* variables: VORTEX_LEGACY_NOPRECACHE turns the worker threads,
    // the nodes built ahead and the pipeline pass off, so that everything loads on first use as it did before
    // them - the other arm of a memory or hitch comparison. An environment variable, so no server can set it.
    private static readonly bool s_noPrecache = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_NOPRECACHE"));

    private static bool Headless => s_noPrecache || DisplayServer.GetName() == "headless";

    /// <summary>
    /// One level's file work. Its threads are its own, not the thread pool's: the texture and model readers
    /// keep a file-sized scratch buffer per thread (a 2048 x 2048 skin is 16 MB), and on pool threads those
    /// would outlive the load by the life of the process. These threads end with the load.
    /// </summary>
    private sealed class PrecacheRun
    {
        public required AssetLoader Loader;
        public readonly CancellationTokenSource Cancel = new();
        public readonly BlockingCollection<(bool Sound, string Name)> Work = new();
        /// <summary>Models whose parse, textures and materials are in memory: the main thread builds their nodes.</summary>
        public readonly ConcurrentQueue<string> Ready = new();
        // A texture or a material is shared by many models (a player model's three levels of detail): the
        // first model to need one does the work, the others wait for that same answer.
        public readonly ConcurrentDictionary<string, Lazy<bool>> Textures = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, Lazy<bool>> Materials = new(StringComparer.Ordinal);
        /// <summary>Models queued and not yet ready.</summary>
        public int PendingModels;
        public readonly List<Thread> Threads = new();
        // Main thread only: what has been queued already.
        public readonly HashSet<string> Models = new(StringComparer.Ordinal), Sounds = new(StringComparer.Ordinal);
    }

    private PrecacheRun? _run;
    private readonly List<string> _precacheModels = new(), _precacheSounds = new();
    // One built node per precached model, waiting for the first entity that shows it (skin 0 only).
    private readonly Dictionary<string, PrebuiltModel> _prebuilt = new(StringComparer.Ordinal);
    private readonly List<Node3D> _prebuiltUnwarmed = new();
    private GpuWarmPass? _warmPass;
    private bool _warmPending, _preloadStarted;
    private string? _preloadedWorld;
    private int _levelsBegun, _framesWithoutBuilds;

    private sealed class PrebuiltModel
    {
        public required Node3D Node;
        public ModelAnimator? Animator;
    }

    /// <summary>Models built ahead for the level and not yet shown, and how long the end of the load took.</summary>
    public int PrebuiltModels => _prebuilt.Count;
    public double PrecacheSeconds { get; private set; }

    /// <summary>
    /// False while the level's loading is still under way on this side: pipelines being compiled for the
    /// models built ahead, or entities of the first frames still being given their models. The loading
    /// screen stays up until it is true (or a few seconds have passed), so that work is not seen as stutter.
    /// </summary>
    public bool SceneSettled => !_warmPending && _framesWithoutBuilds >= 3;

    /// <summary>Set while the loading screen covers the scene: entities are given their models without the
    /// per-frame bound that keeps a frame short during play.</summary>
    public bool Loading { get; set; }

    // ---- starting early --------------------------------------------------------------------------------

    /// <summary>
    /// Called once, as soon as a session knows it is going to load a level - for a local game that is while
    /// the server is still starting on its own thread, seconds before it can name a single model. Two things
    /// are begun here that would otherwise wait for the server's first message:
    ///
    /// <para>The file work, from a guess. A Xonotic server precaches nearly the same few hundred models and
    /// sounds for every level, so the lists of the last level entered (kept in a small text file under the
    /// legacy user directory) are started on the worker threads now. What the guess got wrong is put right
    /// when the real lists arrive (<see cref="BeginLevel"/>): missing names are added, extra ones were read
    /// for nothing.</para>
    ///
    /// <para>The map itself, when its name is known (<paramref name="worldModel"/>, a local game's): parsed,
    /// its collision built and its render geometry made, on this thread, while the server thread is busy
    /// with its own copy. <see cref="BeginLevel"/> finds it done.</para>
    /// </summary>
    public void BeginPreload(string? worldModel)
    {
        if (_preloadStarted || Headless) return;
        _preloadStarted = true;
        long started = LegacyPerfLog.Stamp();
        (List<string> models, List<string> sounds) = LegacyPrecacheHint.Load();
        if (models.Count > 0 || sounds.Count > 0)
        {
            PrecacheRun run = EnsureRun();
            ModelData.Preparse(models, PrecacheWorkers);
            QueueModels(run, models);
            QueueSounds(run, sounds);
        }
        LegacyPerfLog.Event($"preload: {models.Count} models and {sounds.Count} sounds queued from the last level's lists", started);

        if (worldModel is not { Length: > 0 } map || !LegacyQcHost.IsSafePath(map) || !map.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) || !_vfs.Exists(map)) return;
        started = LegacyPerfLog.Stamp();
        int dot = map.LastIndexOf('.');
        if (LoadWorld(map, map[..dot])) _preloadedWorld = map;
        LegacyPerfLog.Event("preload: map " + map, started);
    }

    // ---- the worker side -------------------------------------------------------------------------------

    private PrecacheRun EnsureRun()
    {
        if (_run is { } existing) return existing;
        // The shared white, black and fallback resources and the generated shaders exist before a worker asks.
        _assets.Assets.PrimeSharedSingletons();
        PrecacheRun run = new() { Loader = _assets };
        for (int i = 0, n = PrecacheWorkers; i < n; i++)
        {
            Thread thread = new(() => PrecacheWorker(run)) { IsBackground = true, Name = "legacy-precache-" + i };
            run.Threads.Add(thread);
            thread.Start();
        }
        _run = run;
        return run;
    }

    private static void QueueModels(PrecacheRun run, IEnumerable<string> models)
    {
        if (run.Work.IsAddingCompleted) return;
        foreach (string model in models)
        {
            if (run.Models.Count >= MaxPrecachedModels || !run.Models.Add(model)) continue;
            Interlocked.Increment(ref run.PendingModels);
            run.Work.Add((false, model));
        }
    }

    private static void QueueSounds(PrecacheRun run, IEnumerable<string> sounds)
    {
        if (run.Work.IsAddingCompleted) return;
        foreach (string sound in sounds)
            if (run.Sounds.Count < MaxPrecachedSounds && run.Sounds.Add(sound)) run.Work.Add((true, sound));
    }

    private static int s_liveWorkers;

    /// <summary>Precache worker threads alive in this process right now, over every session. A diagnostic, as
    /// <see cref="VortexArena.Legacy.Local.LegacyLocalServer.LiveThreads"/>: back at the menu it has to read 0.</summary>
    public static int LivePrecacheWorkers => Volatile.Read(ref s_liveWorkers);

    private static void PrecacheWorker(PrecacheRun run)
    {
        Interlocked.Increment(ref s_liveWorkers);
        try
        {
            foreach ((bool sound, string name) in run.Work.GetConsumingEnumerable(run.Cancel.Token))
            {
                if (sound)
                {
                    run.Loader.WarmSoundOffThread(name);
                    continue;
                }
                try { WarmModel(run, name); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
                finally
                {
                    run.Ready.Enqueue(name);
                    Interlocked.Decrement(ref run.PendingModels);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { Interlocked.Decrement(ref s_liveWorkers); }
    }

    // One model's files, off the main thread: the parse its node will be built from, each texture its
    // materials name decoded, mipped and uploaded, and the materials themselves. Best effort: whatever
    // fails here is loaded the ordinary way when the node is built.
    private static void WarmModel(PrecacheRun run, string model)
    {
        AssetLoader loader = run.Loader;
        long started = LegacyPerfLog.Stamp();
        List<string> materials = new(loader.PrepareModel(model, 0));
        // The model's other skins ("<model>_1.skin", ...: a gib's blood colours, a team's flag): their parse
        // and textures too, so the first entity to wear one does not stop the game to decode them. No node
        // is built ahead for them.
        for (int skin = 1; skin <= MaxPrecachedSkins && loader.Vfs.Exists(model + "_" + skin.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".skin"); skin++)
            foreach (string material in loader.PrepareModel(model, skin))
                if (!materials.Contains(material)) materials.Add(material);
        LegacyPerfLog.Event("warm parse " + model, started);
        foreach (string material in materials)
        {
            if (run.Cancel.IsCancellationRequested) return;
            foreach (string texture in loader.Assets.EnumerateMaterialTextureNames(material))
                _ = run.Textures.GetOrAdd(texture, static (name, assets) => new Lazy<bool>(() =>
                {
                    long began = LegacyPerfLog.Stamp();
                    assets.WarmTextureForLoad(name);
                    LegacyPerfLog.Event("warm texture " + name, began);
                    return true;
                }), loader.Assets).Value;
            _ = run.Materials.GetOrAdd(material, static (name, assets) => new Lazy<bool>(() =>
            {
                long began = LegacyPerfLog.Stamp();
                try { assets.ResolveModelMaterial(name); }
                catch (Exception e) when (e is not OutOfMemoryException) { return false; }
                LegacyPerfLog.Event("warm material " + name, began);
                return true;
            }), loader.Assets).Value;
        }
    }

    private void CancelPrecache()
    {
        if (_run is not { } run) return;
        _run = null;
        run.Cancel.Cancel();
        if (!run.Work.IsAddingCompleted) run.Work.CompleteAdding();
        // The workers finish the file they are on; a level's loader must not be used after its session.
        long deadline = System.Environment.TickCount64 + 10_000;
        foreach (Thread thread in run.Threads)
            thread.Join((int)Math.Max(1, deadline - System.Environment.TickCount64));
    }

    // ---- the level's own lists -------------------------------------------------------------------------

    /// <summary>
    /// svc_serverinfo has named the level's models and sounds. Whatever of them is not already queued (all of
    /// them, without <see cref="BeginPreload"/>) goes to the worker threads now, to be carried while the main
    /// thread builds the map and runs CSQC_Init: every model file parsed for the program's own questions
    /// (<see cref="FormatLegacyModels.Preparse"/>) and for drawing, the textures of its materials decoded
    /// and handed to the renderer, every sound's file read.
    /// </summary>
    private void BeginPrecache(CsqcClientState state)
    {
        _precacheModels.Clear();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? candidate in state.ModelNames)
            if (candidate is { Length: > 0 and <= 200 } name && name != "null" && name[0] != '*' && LegacyQcHost.IsSafePath(name)
                && _precacheModels.Count < MaxPrecachedModels && seen.Add(name))
                _precacheModels.Add(name);
        ModelData.Preparse(_precacheModels, PrecacheWorkers);
        if (Headless) return;   // nothing is drawn or heard: the program's own model data is all that is needed

        _precacheSounds.Clear();
        foreach (string? candidate in state.SoundNames)
            if (candidate is { Length: > 0 and <= 200 } name && LegacyQcHost.IsSafePath(name) && _precacheSounds.Count < MaxPrecachedSounds) _precacheSounds.Add(name);
        PrecacheRun run = EnsureRun();
        QueueModels(run, _precacheModels);
        QueueSounds(run, _precacheSounds);
    }

    // ---- the main thread's share -----------------------------------------------------------------------

    /// <summary>
    /// Builds the nodes of models whose files the workers have finished, for at most
    /// <paramref name="budgetSeconds"/>. Called from the frames spent waiting (for a local server to start),
    /// so that time is not idle, and without a bound from <see cref="EndLevelLoad"/>. Returns how many were built.
    /// </summary>
    public int PrebuildReady(double budgetSeconds)
    {
        if (_run is not { } run || Headless) return 0;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        int built = 0;
        while (run.Ready.TryDequeue(out string? model))
        {
            if (!_prebuilt.ContainsKey(model))
            {
                long started = LegacyPerfLog.Stamp();
                Node3D? node = CreateModelNode(model, 0, out ModelAnimator? animator);
                LegacyPerfLog.Event("prebuild " + model, started);
                if (node is not null)
                {
                    _prebuilt[model] = new PrebuiltModel { Node = node, Animator = animator };
                    _prebuiltUnwarmed.Add(node);
                    built++;
                }
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds >= budgetSeconds) break;
        }
        return built;
    }

    /// <summary>
    /// The end of the level's load, once the client program has started: the last names go to the workers
    /// (the program's own precaches), the main thread builds one node for every precached model as its files
    /// become ready, and the nodes are handed to the renderer once, out of sight, so that their pipelines
    /// exist. The nodes are kept: the first entity to show a model takes the one built here instead of
    /// building it in the middle of a frame.
    /// </summary>
    public void EndLevelLoad(CsqcClientState state)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (Headless || _run is not { } run) return;
            long started = LegacyPerfLog.Stamp();
            // The program's own lists (precache_model and precache_sound inside CSQC_Init) name things the server's do not.
            List<string> extra = new();
            foreach (string name in state.CsqcModels)
                if (name is { Length: > 0 and <= 200 } && name != "null" && name[0] != '*' && LegacyQcHost.IsSafePath(name)) extra.Add(name);
            QueueModels(run, extra);
            List<string> sounds = new();
            foreach (KeyValuePair<string, bool> answer in _precached)
                if (answer.Value) sounds.Add(answer.Key);
            QueueSounds(run, sounds);
            run.Work.CompleteAdding();

            // Build what is ready; wait for what is not (CL_KeepaliveMessage while the files are still being read).
            int built = 0, waits = 0;
            for (;;)
            {
                built += PrebuildReady(0.05);
                if (!run.Ready.IsEmpty) continue;
                if (Volatile.Read(ref run.PendingModels) <= 0 && run.Ready.IsEmpty) break;
                Thread.Sleep(1);
                if ((++waits & 31) == 0) ModelData.Working?.Invoke();
            }
            // The sounds are the tail of the queue; the threads end when it is empty.
            foreach (Thread thread in run.Threads)
                while (!thread.Join(50)) ModelData.Working?.Invoke();
            LegacyPerfLog.Event($"precache: {built} models built here; {_prebuilt.Count} in all; {run.Models.Count} models and {run.Sounds.Count} sounds read", started);
            _run = null;
            run.Work.Dispose();
            run.Cancel.Dispose();

            // What the next session can start on before its server has said anything.
            started = LegacyPerfLog.Stamp();
            List<string> allModels = new(_precacheModels), allSounds = new(_precacheSounds);
            allModels.AddRange(extra);
            allSounds.AddRange(sounds);
            LegacyPrecacheHint.Save(allModels, allSounds);
            LegacyPerfLog.Event("precache: lists kept for the next level", started);

            // The effect catalogue and the particle atlas (DarkPlaces reads effectinfo.txt at client start),
            // and one hidden frame of every effect family and every model built above: Godot compiles a
            // material's pipeline on its first draw, which is otherwise the first rocket of the match.
            started = LegacyPerfLog.Stamp();
            _effects.Warmup();
            LegacyPerfLog.Event("precache: effect catalogue", started);
            started = LegacyPerfLog.Stamp();
            GpuWarmPass.Run(_sceneRoot, _effects, null);
            if (_prebuiltUnwarmed.Count > 0)
            {
                _warmPending = true;
                List<Node3D> warm = new(_prebuiltUnwarmed);
                _prebuiltUnwarmed.Clear();
                _warmPass = GpuWarmPass.WarmNodes(_sceneRoot, warm, () =>
                {
                    // Handed back out of the tree: they wait in _prebuilt, hidden, for an entity to take them.
                    _warmPending = false;
                    _warmPass = null;
                });
            }
            LegacyPerfLog.Event("precache: pipeline passes started", started);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _note($"the level's precache was cut short ({e.GetType().Name}: {e.Message}); what is missing loads on first use");
        }
        finally
        {
            PrecacheSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;
            _framesWithoutBuilds = 0;
        }
    }

    // A precached model's node, if one is waiting: taken out of the pool, so each is handed out once.
    private Node3D? TakePrebuilt(string model, int skin, out ModelAnimator? animator)
    {
        animator = null;
        // (Nothing is taken while the pipeline pass still holds the nodes: Touch waits for it.)
        if (skin != 0 || _warmPending || !_prebuilt.Remove(model, out PrebuiltModel? prebuilt)) return null;
        if (!GodotObject.IsInstanceValid(prebuilt.Node)) return null;
        _prebuiltUnwarmed.Remove(prebuilt.Node);
        animator = prebuilt.Animator;
        return prebuilt.Node;
    }

    private void ReleasePrebuilt()
    {
        foreach (PrebuiltModel prebuilt in _prebuilt.Values)
            if (GodotObject.IsInstanceValid(prebuilt.Node))
            {
                if (prebuilt.Node.GetParent() is { } parent) parent.RemoveChild(prebuilt.Node);
                prebuilt.Node.QueueFree();
            }
        _prebuilt.Clear();
        _prebuiltUnwarmed.Clear();
        if (_warmPass is { } pass && GodotObject.IsInstanceValid(pass)) pass.QueueFree();
        _warmPass = null;
        _warmPending = false;
    }
}

/// <summary>
/// The model and sound names of the last level entered, kept under the legacy user directory so that the
/// next session can start reading files before its server has named any (see
/// <see cref="GodotLegacyPresentation.BeginPreload"/>). It is a guess and is treated as one: every name is
/// checked again as a path inside the game data before anything is read, the counts are bounded, and a
/// missing or damaged file means only that nothing is started early.
/// </summary>
internal static class LegacyPrecacheHint
{
    private const int MaxNames = 4096, MaxFileBytes = 1 << 20;
    private static string? s_lastWritten;

    private static string FilePath => System.IO.Path.Combine(LegacyData.UserRoot, "precache-hint.txt");

    public static (List<string> Models, List<string> Sounds) Load()
    {
        List<string> models = new(), sounds = new();
        try
        {
            System.IO.FileInfo file = new(FilePath);
            if (!file.Exists || file.Length > MaxFileBytes) return (models, sounds);
            foreach (string line in System.IO.File.ReadLines(file.FullName))
            {
                if (line.Length is < 3 or > 202 || line[1] != ' ') continue;
                string name = line[2..];
                if (!LegacyQcHost.IsSafePath(name) || name[0] == '*') continue;
                if (line[0] == 'm' && models.Count < MaxNames) models.Add(name);
                else if (line[0] == 's' && sounds.Count < MaxNames) sounds.Add(name);
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return (models, sounds);
    }

    public static void Save(IReadOnlyList<string> models, IReadOnlyList<string> sounds)
    {
        try
        {
            System.Text.StringBuilder text = new((models.Count + sounds.Count) * 40);
            for (int i = 0; i < models.Count && i < MaxNames; i++) text.Append("m ").Append(models[i]).Append('\n');
            for (int i = 0; i < sounds.Count && i < MaxNames; i++) text.Append("s ").Append(sounds[i]).Append('\n');
            string content = text.ToString();
            if (content == s_lastWritten) return;
            string path = FilePath;
            if (s_lastWritten is null && System.IO.File.Exists(path) && System.IO.File.ReadAllText(path) == content)
            {
                s_lastWritten = content;
                return;
            }
            System.IO.File.WriteAllText(path, content);
            s_lastWritten = content;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
