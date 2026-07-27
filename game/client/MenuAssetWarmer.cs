using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Game.Loaders;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Phase 2 of the loading-speed work (planning/loading-speed-background-precache-2026-07-06.md): warm the
/// MAP-INDEPENDENT eager asset set — every weapon view-model + its hand rig, the stock player-model roster, and
/// the combat sounds — into MenuState's process-lifetime <see cref="AssetLoader"/> NOW, while the player sits at
/// the menu, instead of at the first map load. Combined with the persistent shared cache (Phase 1), the first
/// match then finds these already parsed + GPU-uploaded, so its precache collapses to cache hits and the map
/// loads fast. It is the "precache weapons and sounds at game load, not map load" the feature asks for.
///
/// <para>All work is budgeted so the menu never hitches. The heavy skeletal IQM parse (the ~100–360 ms/model
/// burst) runs OFF the main thread on the shared <see cref="BackgroundAssetStreamer"/> lane; only the Godot
/// resource build (mesh/material/texture upload) and the small weapon/sound loads run on the main thread, under a
/// per-frame millisecond budget. Everything is best-effort and Low priority — a match started mid-warm just
/// finishes whatever is left as cache hits.</para>
///
/// <para>Pipeline (PSO) compilation is deliberately NOT done here: it is viewport/World3D-variant specific
/// (see the godot-pipeline-compile-internals notes / <see cref="GpuWarmPass"/>), so the menu's world would compile
/// the wrong variant. Only the map-independent parse/decode/upload is hoisted to the menu; the per-match
/// GpuWarmPass still compiles pipelines against the live match world (cheap, and it now renders cache-hit models).</para>
/// </summary>
public partial class MenuAssetWarmer : Node
{
    /// <summary>Per-frame budget (ms) for the main-thread foreground drain (weapon builds + sound decodes). One
    /// item always runs so a tiny budget still drains; the loop stops once the budget is spent, keeping the warm
    /// work well under a frame so the menu stays smooth.</summary>
    [Export] public double BudgetMs { get; set; } = 1.5;

    /// <summary>The stock player models to warm — the roster a bot or a joining human picks from (the local
    /// player's own <c>_cl_playermodel</c> is added on top when set). Mirrors NetGame's eager roster + idle-warm
    /// list so the menu warm covers the same set the per-map precache would.</summary>
    private static readonly string[] StockPlayerModels =
    {
        "models/player/erebus.iqm", "models/player/megaerebus.iqm", "models/player/nyx.iqm",
        "models/player/pyria.iqm", "models/player/seraphina.iqm", "models/player/umbra.iqm",
    };

    private readonly AssetLoader _assets;
    private readonly string _localModel;
    private readonly Queue<Action> _foreground = new();     // weapon builds + sound decodes — main-thread, budgeted
    private BackgroundAssetStreamer _streamer = null!;       // player-model IQM parse OFF the main thread
    private int _playersQueued, _playersBuilt;
    private bool _foregroundDoneLogged;

    public MenuAssetWarmer(AssetLoader assets, string localModel = "")
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _localModel = localModel ?? "";
    }

    public override void _Ready()
    {
        _streamer = new BackgroundAssetStreamer { Name = "MenuWarmStreamer" };
        AddChild(_streamer);

        AssetLoader assets = _assets;
        int weapons = 0, sounds = 0;

        // Weapons: warm each v_ view-model + its sibling h_ hand rig on the budgeted foreground drain, freeing the
        // throwaway build node (the parse/material/texture caches are what persist). Weapon models are small, and
        // the budget caps a single build so it can't spike the menu. WeaponVModelPath is NetGame's shared key so
        // the later real load hits the SAME cache entry; the v_→h_ rewrite mirrors PrecacheWeaponModelsAsync.
        foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
        {
            string vModel = XonoticGodot.Game.Net.NetGame.WeaponVModelPath(w);
            if (string.IsNullOrEmpty(vModel))
                continue;
            string hModel = vModel.Replace("/v_", "/h_").Replace(".md3", ".iqm");
            _foreground.Enqueue(() =>
            {
                if (assets.LoadModel(vModel) is { } v) v.QueueFree();
                if (hModel != vModel && assets.LoadModel(hModel) is { } h) h.QueueFree();
            });
            weapons++;
        }

        // Combat sounds (sound/weapons/*): decode into the shared sound cache so the first fire/impact doesn't
        // stall decoding its OGG. Main-thread (AudioStream creation), and cheap. Mirrors the per-map precache's set.
        foreach (XonoticGodot.Common.Gameplay.GameSound s in XonoticGodot.Common.Gameplay.Sounds.All)
        {
            string sample = s.Sample;
            if (string.IsNullOrEmpty(sample)
                || !sample.StartsWith("weapons/", StringComparison.OrdinalIgnoreCase))
                continue;
            _foreground.Enqueue(() => assets.LoadSound(sample));
            sounds++;
        }

        // Player models: the heavy skeletal IQMs. Parse OFF the main thread on the shared streamer lane, then build
        // + free on the streamer's budgeted main drain — so the multi-hundred-ms parse never touches the menu
        // frame. Exactly the idle-warmer pattern (StartIdleWarmup), pointed at the shared loader at the menu.
        var players = new HashSet<string>(StockPlayerModels, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_localModel))
            players.Add(_localModel);
        _playersQueued = players.Count;
        foreach (string m in players)
        {
            string model = m;
            _streamer.Request(
                () => assets.ParseSkeletalModel(model, 0),                    // off-thread: IQM + sidecars + anims
                parse =>
                {
                    // Build on the main thread to fill the mesh/material/texture caches, then free the throwaway
                    // node — the caches persist into the first match. (No offscreen render → no PSO warm here; the
                    // per-match GpuWarmPass owns that against the live world.)
                    if (assets.BuildSkeletalModel(parse)?.Root is { } root)
                        root.QueueFree();
                    if (++_playersBuilt >= _playersQueued)
                        Log.Info($"[MenuWarmer] player-model warm done ({_playersBuilt}).");
                },
                BackgroundAssetStreamer.Priority.Low, $"menu-warm {model}");
        }

        Log.Info($"[MenuWarmer] warming {weapons} weapon models + {_playersQueued} player models + " +
                 $"{sounds} combat sounds into the shared cache (background, menu-time).");
    }

    public override void _Process(double delta)
    {
        if (_foreground.Count == 0)
        {
            if (!_foregroundDoneLogged)
            {
                _foregroundDoneLogged = true;
                Log.Info("[MenuWarmer] weapon + sound warm done.");
            }
            SetProcess(false);   // foreground queue drained — go quiet (all items were enqueued up front in _Ready)
            return;
        }

        var sw = Stopwatch.StartNew();
        // Always run at least one item so a tiny budget still drains; stop once the budget is spent so a build
        // can't spill past the frame.
        do
        {
            Action work = _foreground.Dequeue();
            try { work(); }
            catch (Exception ex) { GD.PrintErr($"[MenuWarmer] warm item failed: {ex.Message}"); }
        }
        while (_foreground.Count > 0 && sw.Elapsed.TotalMilliseconds < BudgetMs);
    }
}
