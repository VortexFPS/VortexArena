using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Client;

/// <summary>
/// The offscreen GPU pipeline warm pass (planning/PERFORMANCE_REPORT.md §1.1 Plan A2). Godot compiles a material's
/// shader + render pipeline lazily on its FIRST draw — so the first rocket / explosion / gib / projectile of a
/// match hitches while the GPU stalls compiling. This parents one hidden instance of every runtime-created
/// material family into a tiny isolated 64×64 <see cref="SubViewport"/> (its OWN <c>World3D</c>, so nothing
/// touches the main scene — no visible flash) and lets it render for a few frames, forcing those compiles up
/// front. The first real effect in play then hits a warm pipeline.
///
/// The warm instances reference the SAME cached materials/meshes real spawns use (the A1 resource caches in
/// <see cref="EffectSystem"/> / <see cref="ProjectileRenderer"/>), so a single render frame compiles the exact
/// pipelines play will need. Self-managing: it counts a few rendered frames (one to let the one-shot particles
/// emit + draw, a couple to flush) then frees itself — and with it the SubViewport and every warm instance. The
/// cached Resources survive (the live renderers hold their own references); only this pass's extra refs drop.
/// </summary>
public partial class GpuWarmPass : Node
{
    // Frames to keep the warm viewport alive: one for the one-shot particles to emit + first-draw (compile), the
    // rest to flush. Counted down in _Process (robust whether the load was synchronous or frame-yielding).
    private int _framesLeft = 4;
    private List<Node3D> _instances = new();

    /// <summary>
    /// Build + run a warm pass under <paramref name="parent"/>. Gathers warm instances from the effect and
    /// projectile renderers and renders them offscreen for a few frames. No-op-safe when a subsystem is null or
    /// has nothing to warm (a missing particle atlas / empty catalog simply yields fewer nodes). Call once at
    /// map load, AFTER <see cref="EffectSystem.Warmup"/> / <see cref="ProjectileRenderer.WarmupTrails"/> (which
    /// populate the resource caches this renders).
    /// </summary>
    public static GpuWarmPass Run(Node parent, EffectSystem? effects, ProjectileRenderer? projectiles,
        IReadOnlyList<Node3D>? extra = null)
    {
        var pass = new GpuWarmPass { Name = "GpuWarmPass" };
        // Headless (dedicated server / CI): there is no GPU and no pipelines to warm, and the dummy renderer
        // must not be handed a SubViewport render job. Gather nothing so _Ready drops the pass immediately.
        if (DisplayServer.GetName() != "headless")
        {
            // Gather the instances before entering the tree; building them is pure CPU (uses the populated caches).
            if (effects is not null) pass._instances.AddRange(effects.BuildWarmupInstances());
            if (projectiles is not null) pass._instances.AddRange(projectiles.BuildWarmupInstances());
            // (engine-perf 2026-06-16) Caller-supplied warm instances — e.g. the map-item / pickup MD3 models the
            // host builds from the AssetLoader + item registry (NetGame.BuildItemWarmupInstances). Warmed alongside
            // the effect/projectile families so an item first-seen mid-match doesn't compile its pipeline that frame.
            if (extra is not null) pass._instances.AddRange(extra);
        }
        parent.AddChild(pass);
        return pass;
    }

    /// <summary>
    /// (engine-perf 2026-06-16) Return <paramref name="node"/> with a per-instance ALPHA-transparent surface
    /// override on each MeshInstance3D surface, so rendering it offscreen compiles the alpha-blend pipeline
    /// variant. Gibs + casings flip their (shared) material to <see cref="BaseMaterial3D.TransparencyEnum.Alpha"/>
    /// during their final-second fade-out (ModelGibs/ShellCasings ApplyAlpha) — a DISTINCT Vulkan PSO from the
    /// opaque first-draw, otherwise compiled mid-match on the FIRST fade (~7s after the first death; confirmed as
    /// the residual surface compile by fade-timed live captures). The alpha is cloned onto a surface OVERRIDE so
    /// the shared opaque material the live mesh draws with is never mutated. For warm instances only (then freed).
    /// </summary>
    public static Node3D AlphaWarm(Node3D node)
    {
        ApplyAlphaOverride(node);
        return node;
    }

    private static void ApplyAlphaOverride(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh is { } mesh)
        {
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is StandardMaterial3D mat)
                {
                    var clone = (StandardMaterial3D)mat.Duplicate();
                    clone.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    mi.SetSurfaceOverrideMaterial(s, clone);   // per-instance — the shared mesh material is untouched
                }
            }
        }
        foreach (Node child in node.GetChildren())
            ApplyAlphaOverride(child);
    }

    /// <summary>
    /// (§12.6) Warm arbitrary already-built nodes by rendering them offscreen for a few frames, then invoke
    /// <paramref name="onDone"/> (which owns their lifetime — the pass does NOT free them; they're removed
    /// from the warm viewport first). Used by the idle model warmer: a built-then-immediately-freed model
    /// never rendered, so its material variants' pipelines never compiled — and the FIRST player wearing
    /// that model on screen paid the compile (`pipe +N` mid-fight). Headless: onDone runs immediately.
    /// </summary>
    public static GpuWarmPass WarmNodes(Node parent, List<Node3D> nodes, Action onDone)
    {
        var pass = new GpuWarmPass { Name = "GpuWarmNodes", _onDone = onDone, _returnInstances = true };
        if (DisplayServer.GetName() != "headless")
            pass._instances.AddRange(nodes);
        parent.AddChild(pass);
        return pass;
    }

    private Action? _onDone;
    private bool _returnInstances;

    public override void _Ready()
    {
        // Nothing to warm (no atlas mounted / headless) — drop immediately. A WarmNodes caller still gets
        // its completion callback (it owns the nodes' lifetime).
        if (_instances.Count == 0)
        {
            _onDone?.Invoke();
            QueueFree();
            return;
        }

        var vp = new SubViewport
        {
            Name = "WarmViewport",
            Size = new Vector2I(64, 64),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        // (engine-perf 2026-06-15) The Vulkan graphics-pipeline key includes the MULTISAMPLE state AND the enabled
        // render-feature set (glow, Sky ambient) AND the directional shadow/PSSM mode. Warming in an isolated empty
        // OwnWorld3D therefore compiled the WRONG variant — the live first-draw recompiled the glow+PSSM SURFACE
        // variant mid-match (the residual 105-137ms hitches, confirmed via the split RenderingInfo counters to be
        // SURFACE compiles). Fix: SHARE the live World3D so the warm pass inherits the in-match WorldEnvironment +
        // the Sun's EXACT config (a hand-built generic light regressed before — wrong PSSM split variant; sharing the
        // live world makes the warmed variant byte-identical by construction), and match the main viewport's
        // MSAA/AA/scaling (also part of the key — docs: differing MSAA levels across viewports cause stutter).
        Viewport? mainVp = GetViewport();
        if (mainVp is not null)
        {
            vp.World3D = mainVp.World3D;        // inherit the live WorldEnvironment + Sun → the SURFACE-variant fix
            vp.Msaa3D = mainVp.Msaa3D;
            vp.ScreenSpaceAA = mainVp.ScreenSpaceAA;
            vp.UseTaa = mainVp.UseTaa;
            vp.UseDebanding = mainVp.UseDebanding;
            vp.Scaling3DMode = mainVp.Scaling3DMode;
        }
        else
        {
            vp.OwnWorld3D = true;               // headless / no main viewport: isolated fallback
        }
        AddChild(vp);

        // Because we now SHARE the live World3D, the warm instances live in the same 3D scenario as the gameplay
        // camera. Park the whole warm scene FAR outside any playable area (Quake maps span well under this) so the
        // live camera never sees it — no on-screen flash — while the warm camera (in this SubViewport) still renders
        // it. The Sun is directional (positionless) + glow/ambient are global, so the compiled variant is identical
        // wherever the instances sit; only the cull-from-the-live-view matters.
        var root = new Node3D { Name = "WarmRoot", Position = new Vector3(1_000_000f, 1_000_000f, 1_000_000f) };
        vp.AddChild(root);

        var cam = new Camera3D { Name = "WarmCam", Position = new Vector3(0f, 20f, 140f), Current = true };
        root.AddChild(cam);
        cam.LookAt(root.GlobalPosition, Vector3.Up);

        // Park every warm instance in front of the camera. One render frame compiles each referenced pipeline;
        // the one-shot particles (Explosiveness near 1) emit + draw within the first couple of frames.
        foreach (Node3D n in _instances)
            if (GodotObject.IsInstanceValid(n))
                root.AddChild(n);
    }

    public override void _Process(double delta)
    {
        if (--_framesLeft > 0)
            return;
        VortexArena.Common.Diagnostics.Prof.Event($"warm: GPU warm pass done ({_instances.Count} instances)");
        if (_returnInstances)
        {
            // WarmNodes mode: hand the (now pipeline-warm) nodes back to the caller instead of freeing them.
            foreach (Node3D n in _instances)
                if (GodotObject.IsInstanceValid(n) && n.GetParent() is { } p)
                    p.RemoveChild(n);
            _onDone?.Invoke();
        }
        QueueFree(); // frees the SubViewport + camera (and, in the classic mode, every warm instance)
    }
}
