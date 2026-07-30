using System;
using System.Collections.Generic;
using VortexArena.Game.Client;   // EffectInfoEmitter

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  EffectStyleRegistry — the router-facing view of the authoring overlay (planning/particles-dual-system.md
//  §D.1). It wraps an EffectInfoOverlay and answers two questions the ParticleRouter asks at spawn:
//
//    GetStyle(name)             -> the per-effect routing record (style + optional preset id). Default Auto.
//    TryGetOverlayBlocks(name)  -> the overlay-defined fallback/modern emitter blocks for a modern-only
//                                  effect (so mode 0 can render something faithful-shaped via §D.2).
//
//  Keeping this thin wrapper separate from the parser means the router depends only on this small surface,
//  and the host wires loading once (Load(textLoader)) the same way EffectSystem wires EffectInfo.
// =====================================================================================================

/// <summary>
/// Per-effect style + overlay-block lookup, backed by <see cref="EffectInfoOverlay"/>. Construct once, call
/// <see cref="Load"/> after the VFS text loader is available, then query at spawn. Safe before loading:
/// every lookup returns the Auto default / no blocks until the overlay parses.
/// </summary>
public sealed class EffectStyleRegistry
{
    private readonly EffectInfoOverlay _overlay = new();

    /// <summary>The wrapped overlay (exposed for tests / introspection — e.g. <c>Overlay.Count</c>).</summary>
    public EffectInfoOverlay Overlay => _overlay;

    /// <summary>True once the overlay has parsed at least one override or block.</summary>
    public bool Loaded => _overlay.Loaded;

    /// <summary>Number of effects the overlay declares a routing record for.</summary>
    public int Count => _overlay.Count;

    /// <summary>
    /// Load the <c>effectinfo_xg.txt</c> overlay using <paramref name="textLoader"/> (the host VFS reader,
    /// e.g. <c>VirtualFileSystem.ReadText</c>). Null loader → falls back to the on-disk content tree. Returns
    /// true if the overlay parsed (benign false when no overlay is shipped). Idempotent: re-parses.
    /// </summary>
    public bool Load(Func<string, string?>? textLoader, string vpath = EffectInfoOverlay.DefaultVPath)
    {
        _overlay.TextLoader = textLoader;
        return _overlay.Load(vpath);
    }

    /// <summary>Parse overlay text directly (no I/O) — the unit-test entry point.</summary>
    public void Parse(string text) => _overlay.Parse(text);

    /// <summary>
    /// The authored routing record for <paramref name="effectName"/>, or <see cref="EffectStyleEntry.Default"/>
    /// (Auto, no preset) when the overlay says nothing. Case-insensitive (effects are referenced by either the
    /// EFFECT_* spelling or the lower-case effectinfo name).
    /// </summary>
    public EffectStyleEntry GetStyle(string effectName) => _overlay.GetStyle(effectName);

    /// <summary>True if the overlay declares anything (style and/or block body) for <paramref name="effectName"/>.</summary>
    public bool Has(string effectName) => _overlay.Has(effectName);

    /// <summary>
    /// The overlay-defined fallback/modern emitter blocks for <paramref name="name"/> (a modern-only effect's
    /// faithful-shaped body), or false with an empty list when the overlay declared a style-only override (no
    /// body). The router/translation layer uses these to override the auto-derived fallback in mode 0 (§D.2).
    /// </summary>
    public bool TryGetOverlayBlocks(string name, out IReadOnlyList<EffectInfoEmitter> blocks)
        => _overlay.TryGetBlocks(name, out blocks);
}
