using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Client;

/// <summary>
/// Phase 2 of the loading-speed work (planning/loading-speed-background-precache-2026-07-06.md): warm the
/// MAP-INDEPENDENT eager asset set — every weapon view-model + its hand rig, the stock player-model roster, and
/// the combat sounds — into MenuState's process-lifetime <see cref="AssetLoader"/> NOW, while the player sits at
/// the menu, instead of at the first map load. Combined with the persistent shared cache (Phase 1), the first
/// match then finds these already parsed + GPU-uploaded, so its precache collapses to cache hits and the map
/// loads fast. It is the "precache weapons and sounds at game load, not map load" the feature asks for.
///
/// <para><b>What actually gets warmed (2026-07-31 rework).</b> The caches the first match reads are the model
/// PARSE cache, the material cache and the GPU texture cache — so those are what this warms, directly. It no
/// longer builds a throwaway Godot node per model: <c>IqmBuilder.Build</c>'s <c>Skeleton3D</c> + skinned
/// <c>ArrayMesh</c> are per-instance and nothing caches them, so building one only to <c>QueueFree</c> it was
/// pure main-thread waste — and it was the single most expensive thing here (~900 ms/frame of <c>iqm.mesh</c>
/// on a Debug capture). The materials and textures it incidentally resolved along the way are warmed
/// explicitly instead, which is both cheaper and more complete.</para>
///
/// <para><b>Why the menu used to freeze.</b> The old shape put a whole cold model load on the main thread per
/// frame — read + parse + texture decode + GPU upload — under a "1.5 ms budget" that could not bind, because
/// the drain always ran at least one item and one item cost hundreds of ms. Measured on a Debug release-mode
/// capture: <b>7 frames in the first 8.8 s</b> (p50 840 ms), then spikes to ~490 ms until t≈13 s. The frame
/// profiler reported "no hitches" throughout — with every frame equally slow there is no rolling median to
/// stand out from, which is why this went unnoticed.</para>
///
/// <para><b>The shape now: the model warm never touches the main thread.</b> Every stage runs on the
/// <see cref="BackgroundAssetStreamer"/>'s worker lane, in three waves per model:
/// <list type="bullet">
///   <item><see cref="AssetLoader.PrepareModel"/> — VFS read + format parse + sidecars (pure C#), which also
///   hands back the material names the build will resolve. Player models take
///   <see cref="AssetLoader.ParseSkeletalModel"/> instead, since that is the cache they render from.</item>
///   <item>One job per TEXTURE: <see cref="AssetSystem.WarmTextureOffThread"/> — read, decode AND GPU upload.</item>
///   <item>One job per MATERIAL: <see cref="AssetSystem.ResolveMaterial"/> — material + generated-shader
///   construction, over textures that are already resident.</item>
/// </list>
/// The last two are Godot resource construction, which this codebase otherwise keeps on the main thread; see
/// <see cref="AssetSystem.WarmTextureOffThread"/> for why this caller is the one place that is safe to do it
/// from. They were also the entire residual cost — a single big player-model diffuse uploads in 25–45 ms and a
/// single generated-shader material compiles in ~20 ms even on release, neither of which any per-frame budget
/// can subdivide. Moving them is what took the menu from "a few paced hitches" to a flat
/// <b>p50 6.9 / p95 6.9 ms with zero asset-build hitches</b>. What is left on the main thread is the small
/// sound-decode queue below (budgeted) and nothing else.</para>
///
/// <para><b>The one cost that did NOT move</b> is managed allocation: the worker decodes allocate, and a GC
/// they trigger still pauses the main thread. That is why models are fed through a small in-flight window
/// (<see cref="MaxModelsInFlight"/>) rather than fanned out at once.</para>
///
/// <para>Pipeline (PSO) compilation is deliberately NOT done here: it is viewport/World3D-variant specific
/// (see the godot-pipeline-compile-internals notes / <see cref="GpuWarmPass"/>), so the menu's world would compile
/// the wrong variant. Only the map-independent parse/decode/upload is hoisted to the menu; the per-match
/// GpuWarmPass still compiles pipelines against the live match world (cheap, and it now renders cache-hit models).</para>
/// </summary>
public partial class MenuAssetWarmer : Node
{
    /// <summary>Per-frame budget (ms) for the main-thread work this node owns directly — the sound decodes.
    /// Model work runs on the <see cref="BackgroundAssetStreamer"/> and is paced by ITS budget. Overshoot is
    /// carried as debt (see <see cref="_debtMs"/>) so an item bigger than the budget is paid back by skipping
    /// frames rather than silently ignored.</summary>
    [Export] public double BudgetMs { get; set; } = 1.5;

    /// <summary>The stock player models to warm — the roster a bot or a joining human picks from (the local
    /// player's own <c>_cl_playermodel</c> is added on top when set). Mirrors NetGame's eager roster + idle-warm
    /// list so the menu warm covers the same set the per-map precache would.</summary>
    private static readonly string[] StockPlayerModels =
    {
        "models/player/erebus.iqm", "models/player/megaerebus.iqm", "models/player/nyx.iqm",
        "models/player/pyria.iqm", "models/player/seraphina.iqm", "models/player/umbra.iqm",
    };

    /// <summary>
    /// How many models may be warming at once. The warm has no deadline — the player is sitting in a menu —
    /// so it is fed through a window rather than fanned out all at once. Releasing all ~54 models together put
    /// every worker on a texture decode simultaneously and allocated over 100 MB inside the first second, which
    /// showed up on the MAIN thread as GC pauses and 30–80 ms frames even though none of that work was main-
    /// thread work. A small window keeps the decode rate (and so the allocation rate) flat.
    /// </summary>
    private const int MaxModelsInFlight = 2;

    private readonly AssetLoader _assets;
    private readonly string _localModel;
    private readonly Queue<Action> _foreground = new();     // sound decodes — main-thread, budgeted
    private readonly Queue<(string Model, bool Skeletal)> _pendingModels = new();
    private BackgroundAssetStreamer _streamer = null!;       // model parse + texture decode OFF the main thread
    private int _modelsQueued, _modelsWarmed, _modelsInFlight;
    private bool _foregroundDoneLogged, _modelsDoneLogged;

    /// <summary>Unpaid main-thread overshoot, in ms — see <see cref="BackgroundAssetStreamer"/>'s <c>_debtMs</c>
    /// for the rationale. A sound decode that costs 6 ms against a 1.5 ms budget buys the next ~3 frames off.</summary>
    private double _debtMs;

    public MenuAssetWarmer(AssetLoader assets, string localModel = "")
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _localModel = localModel ?? "";
    }

    public override void _Ready()
    {
        // A gentler budget than the in-match default (2.0): a single GPU texture upload is indivisible and
        // costs 25-45 ms for a big player-model diffuse even on release, so the budget can only control HOW
        // OFTEN one lands, not how big it is. Halving it roughly halves the hitch rate, at the cost of the warm
        // taking about twice as long — the right trade here, because nothing is waiting on it: this is pure
        // prefetch during menu dwell, and whatever is still cold when a match starts is simply picked up by
        // that match's own precache, exactly as it was before any warm existed.
        _streamer = new BackgroundAssetStreamer { Name = "MenuWarmStreamer", BudgetMs = 1.0 };
        AddChild(_streamer);

        // Build the shared white/black/checkerboard textures + fallback material HERE, on the main thread,
        // before any worker can reach them through a missing-texture path — see PrimeSharedSingletons.
        _assets.Assets.PrimeSharedSingletons();

        AssetLoader assets = _assets;

        // --- models: every weapon v_ view-model + its sibling h_ hand rig, and the stock player roster. ---
        // WeaponVModelPath is NetGame's shared key so the later real load hits the SAME cache entry; the
        // v_→h_ rewrite mirrors PrecacheWeaponModelsAsync.
        var weaponModels = new List<string>();
        foreach (VortexArena.Common.Gameplay.Weapon w in VortexArena.Common.Gameplay.Weapons.All)
        {
            string vModel = VortexArena.Game.Net.NetGame.WeaponVModelPath(w);
            if (string.IsNullOrEmpty(vModel))
                continue;
            string hModel = vModel.Replace("/v_", "/h_").Replace(".md3", ".iqm");
            if (!weaponModels.Contains(vModel)) weaponModels.Add(vModel);
            if (hModel != vModel && !weaponModels.Contains(hModel)) weaponModels.Add(hModel);
        }

        var playerModels = new List<string>(StockPlayerModels);
        if (!string.IsNullOrEmpty(_localModel) && !playerModels.Contains(_localModel))
            playerModels.Add(_localModel);

        foreach (string m in weaponModels)
            _pendingModels.Enqueue((m, false));
        foreach (string m in playerModels)
            _pendingModels.Enqueue((m, true));
        _modelsQueued = _pendingModels.Count;
        int weapons = weaponModels.Count, players = playerModels.Count;
        StartPendingModels();

        // --- combat sounds (sound/weapons/*): decode into the shared sound cache so the first fire/impact
        //     doesn't stall decoding its OGG. AudioStream creation is a Godot resource build, so this stays on
        //     the main thread — but each one is small, and the budget below is now enforced. ---
        int sounds = 0;
        foreach (VortexArena.Common.Gameplay.GameSound s in VortexArena.Common.Gameplay.Sounds.All)
        {
            string sample = s.Sample;
            if (string.IsNullOrEmpty(sample)
                || !sample.StartsWith("weapons/", StringComparison.OrdinalIgnoreCase))
                continue;
            _foreground.Enqueue(() => assets.LoadSound(sample));
            sounds++;
        }

        Log.Info($"[MenuWarmer] warming {weapons} weapon models + {players} player models + " +
                 $"{sounds} combat sounds into the shared cache (background, menu-time).");
    }

    /// <summary>
    /// Warm one WEAPON view-model / hand rig. Weapons render through <see cref="AssetLoader.LoadModel"/>, whose
    /// parse cache <see cref="AssetLoader.PrepareModel"/> fills — off-thread — while handing back the material
    /// names the eventual build will resolve.
    /// </summary>
    private void WarmWeaponModel(AssetLoader assets, string model)
        => _streamer.Request(
            // Worker: read + parse + sidecars. Boxed because the streamer drops a null result silently, and an
            // empty list (a miss, or a main-thread-only MDL) must still reach the main phase to be counted.
            () => new MaterialList(assets.PrepareModel(model, 0)),
            box => WarmMaterials(assets, model, box.Materials),
            BackgroundAssetStreamer.Priority.Low, $"menu-warm {model}");

    /// <summary>
    /// Warm one PLAYER model. Players render through <see cref="AssetLoader.ParseSkeletalModel"/> — a DIFFERENT
    /// cache from the weapon path (it holds the parsed IQM plus the pre-built <c>AnimationLibrary</c>, the
    /// 100–360 ms burst behind the bot-spawn hitch storm), so that is the one warmed here. What is deliberately
    /// NOT done any more is <c>BuildSkeletalModel</c>: its <c>Skeleton3D</c> + skinned <c>ArrayMesh</c> are
    /// per-instance and cached nowhere, so building one only to free it was the largest single cost in this
    /// node and bought nothing. Its materials/textures — the part that DID persist — are warmed below instead.
    /// </summary>
    private void WarmPlayerModel(AssetLoader assets, string model)
        => _streamer.Request(
            () => new SkeletalParseBox(assets.ParseSkeletalModel(model, 0)),   // worker: IQM + sidecars + anims
            box => WarmMaterials(assets, model,
                box.Parse is null ? Array.Empty<string>() : AssetLoader.EffectiveMaterials(box.Parse)),
            BackgroundAssetStreamer.Priority.Low, $"menu-warm {model}");

    /// <summary>
    /// The shared tail of both warms, in two waves. First one streamer job PER TEXTURE, entirely off the main
    /// thread (read + decode + GPU upload — see <see cref="AssetSystem.WarmTextureOffThread"/>). Then, once
    /// every texture has landed, one job PER MATERIAL whose main phase resolves exactly one material.
    ///
    /// <para>The per-material second wave is not cosmetic granularity. Resolving a material is Godot resource
    /// construction — a <c>ShaderMaterial</c> plus, for a Q3 shader stage, a generated <c>Shader</c> whose code
    /// Godot compiles — and it is the one step still on the main thread. Resolving a model's whole material set
    /// in a single callback cost up to 20 ms in one frame; one per job puts each under the streamer's budget,
    /// where the debt pacing can spread them.</para>
    /// </summary>
    private void WarmMaterials(AssetLoader assets, string model, IReadOnlyList<string> materials)
    {
        var textures = new List<string>();
        foreach (string m in materials)
            foreach (string t in assets.Assets.EnumerateMaterialTextureNames(m))
                if (!textures.Contains(t))
                    textures.Add(t);

        if (textures.Count == 0)
        {
            WarmMaterialsWave(assets, model, materials);
            return;
        }

        int remaining = textures.Count;   // only ever touched from the main thread (streamer main phases)
        foreach (string tex in textures)
        {
            string t = tex;
            _streamer.Request(
                // Worker: VFS read + decode + GPU upload. The upload is the step Godot would normally reserve
                // for the main thread, and it was the dominant residual cost — a single big player-model
                // diffuse costs 25-45 ms even on release, which no per-frame budget can subdivide.
                () => { assets.Assets.WarmTextureOffThread(t); return t; },
                _ =>
                {
                    if (--remaining == 0)
                        WarmMaterialsWave(assets, model, materials);
                },
                BackgroundAssetStreamer.Priority.Low, $"menu-warm {model} tex {t}");
        }
    }

    /// <summary>
    /// Second wave: one streamer job per material, each resolving exactly ONE material on the main thread (the
    /// textures it samples are already uploaded by now, so the work is material/shader construction only). The
    /// last one to land counts the model warmed.
    /// </summary>
    private void WarmMaterialsWave(AssetLoader assets, string model, IReadOnlyList<string> materials)
    {
        if (materials.Count == 0)
        {
            NoteModelWarmed();
            return;
        }

        int remaining = materials.Count;
        foreach (string mat in materials)
        {
            string m = mat;
            _streamer.Request(
                // Worker: build the material. This is Godot resource construction — a ShaderMaterial plus, for
                // a Q3 shader stage, a generated Shader whose code Godot compiles — and it was the LAST thing
                // the warm still did on the main thread, at up to 22 ms for a single material. Its textures are
                // already uploaded by the wave above, so this is pure material/shader work. Safe for the same
                // reason the texture upload is (AssetSystem.WarmTextureOffThread's remarks); the shared lazy
                // singletons it can reach are primed on the main thread before any of this starts.
                () => { assets.Assets.ResolveMaterial(m); return m; },
                _ =>
                {
                    if (--remaining == 0)
                        NoteModelWarmed();
                },
                BackgroundAssetStreamer.Priority.Low, $"menu-warm {model} mat {m}");
        }
    }

    /// <summary>Non-null off-thread wrapper for a model's material work-list — the streamer drops a null result
    /// silently, so a model with no materials would never reach its main phase or be counted as warmed.</summary>
    private sealed record MaterialList(IReadOnlyList<string> Materials);

    /// <summary>Non-null wrapper for a (possibly failed) skeletal parse — same reason as
    /// <see cref="MaterialList"/>, and the same shape NetGame's live player-model stream uses.</summary>
    private sealed record SkeletalParseBox(AssetLoader.SkeletalModelParse? Parse);

    /// <summary>Open the warm window up to <see cref="MaxModelsInFlight"/>. Main-thread only (called from
    /// <c>_Ready</c> and from a streamer main phase), so the counters need no locking.</summary>
    private void StartPendingModels()
    {
        while (_modelsInFlight < MaxModelsInFlight && _pendingModels.Count > 0)
        {
            (string model, bool skeletal) = _pendingModels.Dequeue();
            _modelsInFlight++;
            if (skeletal)
                WarmPlayerModel(_assets, model);
            else
                WarmWeaponModel(_assets, model);
        }
    }

    private void NoteModelWarmed()
    {
        _modelsInFlight--;
        _modelsWarmed++;
        StartPendingModels();
        if (_modelsWarmed < _modelsQueued || _modelsDoneLogged)
            return;
        _modelsDoneLogged = true;
        Log.Info($"[MenuWarmer] model warm done ({_modelsWarmed}).");
    }

    public override void _Process(double delta)
    {
        if (_foreground.Count == 0)
        {
            if (!_foregroundDoneLogged)
            {
                _foregroundDoneLogged = true;
                Log.Info("[MenuWarmer] combat-sound warm done.");
            }
            SetProcess(false);   // foreground queue drained — go quiet (all items were enqueued up front in _Ready)
            return;
        }

        // Pay off any overshoot from earlier frames first, so an item that costs more than one budget is
        // amortised across frames instead of running one-per-frame regardless of cost.
        if (_debtMs > 0.0)
        {
            _debtMs -= BudgetMs;
            return;
        }

        using var _scope = Prof.Sample("menu.warm");
        var sw = Stopwatch.StartNew();
        // Always run at least one item so a tiny budget still drains; stop once the budget is spent, and bank
        // the overshoot as debt so the budget bounds the AVERAGE per-frame cost.
        do
        {
            Action work = _foreground.Dequeue();
            try { work(); }
            catch (Exception ex) { GD.PrintErr($"[MenuWarmer] warm item failed: {ex.Message}"); }
        }
        while (_foreground.Count > 0 && sw.Elapsed.TotalMilliseconds < BudgetMs);

        if (sw.Elapsed.TotalMilliseconds > BudgetMs)
            _debtMs = Math.Min(sw.Elapsed.TotalMilliseconds - BudgetMs, MaxDebtFrames * BudgetMs);
    }

    /// <summary>Ceiling on accumulated debt in whole skipped frames — see BackgroundAssetStreamer.MaxDebtFrames.</summary>
    private const int MaxDebtFrames = 60;
}
