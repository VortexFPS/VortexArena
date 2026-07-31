using System;
using System.Collections.Generic;
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
/// <b>p50 6.9 / p95 6.9 ms with zero asset-build hitches</b>. Sounds, loose HUD/particle textures and the
/// world-model set take the same lane, so this node owns NO main-thread work at all — it has no
/// <c>_Process</c>. Its only main-thread cost is the streamer's own drain, already scoped as
/// <c>stream.build</c>; if main-thread work is ever added back here it needs its own <c>Prof.Sample</c> and a
/// <c>FrameProfiler.TopLevelNodeScopes</c> entry (house rule).</para>
///
/// <para><b>What is left is contention, not work.</b> With the main thread idle, the warm's remaining cost
/// reaches it indirectly — a burst of concurrent decodes and GPU uploads makes the frame block in present. In
/// the capture that showed as frames of 13–25 ms whose time was almost entirely <c>rest</c>, with
/// <c>proc</c>/<c>rcpu</c>/<c>gpu</c> all near zero. (One of them was even classified GC-PAUSE because a gen2
/// collection happened to land in it; the GC pause itself was 0.2 ms of the 24.9.) Two things keep that in
/// check: uploads are serialized behind <see cref="AssetSystem"/>'s upload gate so the driver gets a trickle
/// rather than four concurrent streams, and models are fed through a small in-flight window
/// (<see cref="MaxModelsInFlight"/>) rather than fanned out at once. The streamer's worker threads also run at
/// <c>BelowNormal</c> priority, so the OS preempts prefetch rather than the render loop. What survives all of
/// that is a single ~13 ms frame — one dropped frame at 144 fps — while two workers decode concurrently.</para>
///
/// <para>Pipeline (PSO) compilation is deliberately NOT done here: it is viewport/World3D-variant specific
/// (see the godot-pipeline-compile-internals notes / <see cref="GpuWarmPass"/>), so the menu's world would compile
/// the wrong variant. Only the map-independent parse/decode/upload is hoisted to the menu; the per-match
/// GpuWarmPass still compiles pipelines against the live match world (cheap, and it now renders cache-hit models).</para>
/// </summary>
public partial class MenuAssetWarmer : Node
{
    /// <summary>The stock player models to warm — the roster a bot or a joining human picks from (the local
    /// player's own <c>_cl_playermodel</c> is added on top when set). Mirrors NetGame's eager roster + idle-warm
    /// list so the menu warm covers the same set the per-map precache would.</summary>
    private static readonly string[] StockPlayerModels =
    {
        "models/player/erebus.iqm", "models/player/megaerebus.iqm", "models/player/nyx.iqm",
        "models/player/pyria.iqm", "models/player/seraphina.iqm", "models/player/umbra.iqm",
    };

    /// <summary>
    /// How many warm units — one model, one loose texture, or one sound — may be in flight at once. The warm
    /// has no deadline (the player is sitting in a menu), so it is fed through a window rather than fanned out.
    ///
    /// <para>ONE, with <see cref="Chain"/> serialising the jobs inside a unit as well — so the whole warm is a
    /// single asset at a time. That is not caution for its own sake; it is what four release captures of the
    /// same asset set measured (window 1 chained / 1 fanned-out / 2 / 3):</para>
    /// <list type="bullet">
    ///   <item>window 2, fanned out — 1 ASSET-BUILD + 3 GC-PAUSE, p95 8.6 ms, 63 MB/s allocated</item>
    ///   <item>window 1, fanned out — 1 GC-PAUSE + 2 VSYNC, p95 9.1 ms, 38 MB/s</item>
    ///   <item>window 3, chained — 2 ASSET-BUILD + 2 VSYNC + 1 MIXED, p95 10.6 ms, 41 MB/s</item>
    ///   <item><b>window 1, chained — NO hitches, p95 6.9 ms, 26 MB/s</b></item>
    /// </list>
    /// <para>None of that work is on the main thread; it reaches the frame through the allocator and the
    /// driver. A GC has to suspend every thread that is allocating, so N parallel decoders make an N-scaled
    /// pause (17.4 ms for a gen0+gen1 with four workers busy), and N parallel uploads saturate the driver's
    /// ingest so the frame blocks in present. Serial costs wall-clock only: the warm drains in ~21 s instead of
    /// ~6 s, and nothing waits on it — a match started mid-warm just loads the remainder itself, exactly as it
    /// did before any warm existed.</para>
    /// </summary>
    private const int MaxUnitsInFlight = 1;

    private readonly AssetLoader _assets;
    private readonly string _localModel;
    /// <summary>Every queued warm unit, as "start me and call this when I'm done". A unit is one model (which
    /// itself expands into many streamer jobs), one loose texture, or one sound.</summary>
    private readonly Queue<Action<Action>> _pending = new();
    private BackgroundAssetStreamer _streamer = null!;       // model parse + texture decode OFF the main thread
    private int _unitsQueued, _unitsWarmed, _unitsInFlight;
    private bool _warmDoneLogged;
    private ulong _startedMsec;   // for the completion log — how long the warm actually took to drain

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

        // --- world models the client can spawn on ANY map: the gib set and every registered pickup. Both were
        //     previously warmed at map load (ModelGibs/BuildItemWarmupInstances feeding the GPU warm pass) even
        //     though neither depends on the map. ModelGibs.AllModelPaths is the same list the load-time pass
        //     iterates; the item paths are built with StartItem.ResolveModelPath exactly as a live spawn does,
        //     so the warm fills the cache entry the real load will ask for. ---
        var worldModels = new List<string>();
        foreach (string g in VortexArena.Game.Client.ModelGibs.AllModelPaths)
            if (!worldModels.Contains(g))
                worldModels.Add(g);
        foreach (VortexArena.Common.Gameplay.Pickup def in
                 VortexArena.Common.Framework.Registry<VortexArena.Common.Gameplay.Pickup>.All)
        {
            string? path = VortexArena.Common.Gameplay.StartItem.ResolveModelPath(def);
            if (!string.IsNullOrEmpty(path) && !worldModels.Contains(path!))
                worldModels.Add(path!);
        }

        foreach (string m in weaponModels)
            { string v = m; _pending.Enqueue(done => WarmModel(assets, v, skeletal: false, done)); }
        foreach (string m in worldModels)
            { string v = m; _pending.Enqueue(done => WarmModel(assets, v, skeletal: false, done)); }
        foreach (string m in playerModels)
            { string v = m; _pending.Enqueue(done => WarmModel(assets, v, skeletal: true, done)); }
        int weapons = weaponModels.Count, players = playerModels.Count, world = worldModels.Count;

        // --- loose textures with no model to hang off: the particle atlas and the HUD art. Both are
        //     map-independent and were paid at map load / first draw. They need no parse, so each is a single
        //     texture job (worker: read + decode + upload). ---
        int textures = 0;
        foreach (string t in MapIndependentTextures())
        {
            string name = t;
            _pending.Enqueue(done => _streamer.Request(
                () => { assets.Assets.WarmTextureOffThread(name); return name; },
                _ => done(),
                BackgroundAssetStreamer.Priority.Low, $"menu-warm tex {name}"));
            textures++;
        }

        // --- sounds: the WHOLE registered set (announcer, pickup, voices, combat), not just sound/weapons/*.
        //     All of it is map-independent, and it used to be left to the IN-MATCH idle warmer. Decoded on the
        //     worker lane (see AssetLoader.WarmSoundOffThread — container parse only, no renderer), so 200-odd
        //     samples cost the menu nothing. ---
        int sounds = 0;
        foreach (VortexArena.Common.Gameplay.GameSound s in VortexArena.Common.Gameplay.Sounds.All)
        {
            string sample = s.Sample;
            if (string.IsNullOrEmpty(sample))
                continue;
            _pending.Enqueue(done => _streamer.Request(
                () => { assets.WarmSoundOffThread(sample); return sample; },
                _ => done(),
                BackgroundAssetStreamer.Priority.Low, $"menu-warm snd {sample}"));
            sounds++;
        }

        _unitsQueued = _pending.Count;
        _startedMsec = Time.GetTicksMsec();
        StartPendingUnits();

        Log.Info($"[MenuWarmer] warming {weapons} weapon models + {players} player models + {world} world " +
                 $"models + {textures} textures + {sounds} sounds into the shared cache (background, menu-time).");
    }

    /// <summary>
    /// The map-independent loose textures worth holding before the first match: the DP particle atlas (every
    /// explosion/impact/decal samples it) and the HUD art the in-match overlay draws on its first frame —
    /// per-weapon icons, the configured crosshair plus its ring, the ammo icon, the nametag bar and the
    /// progress bar. All are extension-agnostic base names resolved through the same
    /// <see cref="AssetSystem.LoadTexture"/> path the HUD's <see cref="Hud.TextureCache"/> uses, so warming
    /// them here makes that first draw a cache hit. A name that does not resolve is a cheap no-op.
    /// </summary>
    private IEnumerable<string> MapIndependentTextures()
    {
        var names = new List<string> { ParticleFont.AtlasVPath };

        foreach (VortexArena.Common.Gameplay.Weapon w in VortexArena.Common.Gameplay.Weapons.All)
            foreach (string p in Hud.WeaponHud.IconPaths(w.NetName))
                if (!p.StartsWith("res://", StringComparison.Ordinal))   // project overrides aren't VFS art
                    names.Add(p);

        string skin = Hud.HudSkin.SkinName;
        names.Add($"gfx/hud/{skin}/weapon_ammo");
        names.Add("gfx/hud/default/weapon_ammo");
        names.Add($"gfx/hud/{skin}/nametag_statusbar");
        names.Add("gfx/hud/default/nametag_statusbar");
        names.Add($"gfx/hud/{skin}/progressbar");
        names.Add("gfx/hud/default/progressbar");
        names.Add("gfx/crosshair_ring");

        // The crosshair the player actually has selected (crosshairs.cfg default is 16). Only that one — the
        // full gfx/crosshair1..N set is dozens of textures the player will never draw.
        string cross = VortexArena.Game.Menu.MenuState.Cvars.GetString("crosshair");
        if (!string.IsNullOrWhiteSpace(cross) && cross != "0")
            names.Add($"gfx/crosshair{cross}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in names)
            if (!string.IsNullOrWhiteSpace(n) && seen.Add(n))
                yield return n;
    }

    /// <summary>
    /// Warm one WEAPON view-model / hand rig. Weapons render through <see cref="AssetLoader.LoadModel"/>, whose
    /// parse cache <see cref="AssetLoader.PrepareModel"/> fills — off-thread — while handing back the material
    /// names the eventual build will resolve.
    /// </summary>
    private void WarmModel(AssetLoader assets, string model, bool skeletal, Action done)
    {
        if (skeletal)
        {
            // Players render through ParseSkeletalModel — a DIFFERENT cache from the weapon/world path (it holds
            // the parsed IQM plus the pre-built AnimationLibrary, the 100-360 ms burst behind the bot-spawn hitch
            // storm), so that is the one warmed for them.
            _streamer.Request(
                () => new SkeletalParseBox(assets.ParseSkeletalModel(model, 0)),  // worker: IQM + sidecars + anims
                box => WarmMaterials(assets, model,
                    box.Parse is null ? Array.Empty<string>() : AssetLoader.EffectiveMaterials(box.Parse), done),
                BackgroundAssetStreamer.Priority.Low, $"menu-warm {model}");
            return;
        }
        // Worker: read + parse + sidecars. Boxed because the streamer drops a null result silently, and an
        // empty list (a miss, or a main-thread-only MDL) must still reach the main phase to be counted.
        _streamer.Request(
            () => new MaterialList(assets.PrepareModel(model, 0)),
            box => WarmMaterials(assets, model, box.Materials, done),
            BackgroundAssetStreamer.Priority.Low, $"menu-warm {model}");
    }

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
    private void WarmMaterials(AssetLoader assets, string model, IReadOnlyList<string> materials, Action done)
    {
        var textures = new List<string>();
        foreach (string m in materials)
            foreach (string t in assets.Assets.EnumerateMaterialTextureNames(m))
                if (!textures.Contains(t))
                    textures.Add(t);

        if (textures.Count == 0)
        {
            WarmMaterialsWave(assets, model, materials, done);
            return;
        }

        // ONE AT A TIME, chained: each job's main phase starts the next. Issuing a model's whole texture set at
        // once put all four workers on a decode simultaneously, and a GC has to suspend every one of them — a
        // gen0+gen1 collection measured 17.4 ms that way, which is a dropped frame even though the main thread
        // did nothing. Serialising costs the warm wall-clock time it does not need, and nothing waits on it.
        Chain(textures, t => () => assets.Assets.WarmTextureOffThread(t),
              $"menu-warm {model} tex", () => WarmMaterialsWave(assets, model, materials, done));
    }

    /// <summary>
    /// Second wave: one streamer job per material, each resolving exactly ONE material on the main thread (the
    /// textures it samples are already uploaded by now, so the work is material/shader construction only). The
    /// last one to land counts the model warmed.
    /// </summary>
    private void WarmMaterialsWave(AssetLoader assets, string model, IReadOnlyList<string> materials, Action done)
    {
        if (materials.Count == 0)
        {
            done();
            return;
        }

        // Worker: build the material. This is Godot resource construction — a ShaderMaterial plus, for a Q3
        // shader stage, a generated Shader whose code Godot compiles — and it was the LAST thing the warm still
        // did on the main thread, at up to 22 ms for a single material. Its textures are already uploaded by the
        // wave above, so this is pure material/shader work. Safe for the same reason the texture upload is (see
        // AssetSystem.WarmTextureOffThread); the shared lazy singletons it can reach are primed on the main
        // thread before any of this starts. Chained one at a time for the same reason the texture wave is.
        Chain(materials, m => () => assets.Assets.ResolveMaterial(m), $"menu-warm {model} mat", done);
    }

    /// <summary>
    /// Run one streamer job per item, STRICTLY ONE AT A TIME: each job's main phase issues the next, and
    /// <paramref name="done"/> fires after the last. <paramref name="makeWork"/> returns the worker-phase body
    /// for an item.
    ///
    /// <para>The point is to bound how many workers are allocating at once. The main thread never runs any of
    /// this, but a GC must suspend every thread that is, so N parallel decoders turn into an N-scaled pause on
    /// the frame — measured at 17.4 ms for a gen0+gen1 collection with four workers busy. A background prefetch
    /// with no deadline has no reason to buy parallelism at that price.</para>
    /// </summary>
    private void Chain(IReadOnlyList<string> items, Func<string, Action> makeWork, string label, Action done)
    {
        if (items.Count == 0)
        {
            done();
            return;
        }
        void Step(int i)
        {
            if (i >= items.Count)
            {
                done();
                return;
            }
            string item = items[i];
            Action work = makeWork(item);
            _streamer.Request(
                () => { work(); return item; },
                _ => Step(i + 1),
                BackgroundAssetStreamer.Priority.Low, $"{label} {item}");
        }
        Step(0);
    }

    /// <summary>Non-null off-thread wrapper for a model's material work-list — the streamer drops a null result
    /// silently, so a model with no materials would never reach its main phase or be counted as warmed.</summary>
    private sealed record MaterialList(IReadOnlyList<string> Materials);

    /// <summary>Non-null wrapper for a (possibly failed) skeletal parse — same reason as
    /// <see cref="MaterialList"/>, and the same shape NetGame's live player-model stream uses.</summary>
    private sealed record SkeletalParseBox(AssetLoader.SkeletalModelParse? Parse);

    /// <summary>Open the warm window up to <see cref="MaxUnitsInFlight"/>. Main-thread only (called from
    /// <c>_Ready</c> and from a streamer main phase), so the counters need no locking. Each unit calls back
    /// exactly once when it finishes, which both frees its slot and pulls the next one in.</summary>
    private void StartPendingUnits()
    {
        while (_unitsInFlight < MaxUnitsInFlight && _pending.Count > 0)
        {
            Action<Action> start = _pending.Dequeue();
            _unitsInFlight++;
            bool counted = false;
            start(() => { if (!counted) { counted = true; NoteUnitWarmed(); } });
        }
    }

    private void NoteUnitWarmed()
    {
        _unitsInFlight--;
        _unitsWarmed++;
        StartPendingUnits();
        if (_unitsWarmed < _unitsQueued || _warmDoneLogged)
            return;
        _warmDoneLogged = true;
        Log.Info($"[MenuWarmer] warm done ({_unitsWarmed} units in " +
                 $"{(Time.GetTicksMsec() - _startedMsec) / 1000.0:0.0}s).");
    }

}
