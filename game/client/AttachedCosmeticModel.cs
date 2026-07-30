using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Md3;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Client;

/// <summary>
/// A generic client-side <b>attached cosmetic model</b>: a small <see cref="Node3D"/> that owns a single
/// built model child (MD3 or IQM/other) at a fixed QC follow-offset, scale, held frame, and colormod — the
/// Godot successor to the CSQC <c>setmodel</c>/<c>self.frame</c>/<c>colormod</c> bits that decorate a player
/// or an entity (the frozen-ice shell '<c>0 0 16</c>' held on 1-of-20 frames, the buff_* glow at scale 0.7,
/// the nade spawn-loc stardust marker).
///
/// <para>This networks nothing of its own — the host (<c>CosmeticModelLayer</c>) reads the already-networked
/// StatusEffects blob / entity delta and parents one of these under the entity's render root. It is a pure
/// presentation helper: it builds the child once, positions/scales/tints/holds-a-frame, and otherwise just
/// rides the parent's transform. There is intentionally <b>no per-frame <c>_Process</c></b> — the parent node
/// already moves it, and a held frame is set once.</para>
///
/// <para>MD3 with more than one frame is built through <see cref="ModelAnimator"/> so a single frame can be
/// HELD (<see cref="ModelAnimator.SetRawFrame"/> with <see cref="ModelAnimator.FollowEntityFrame"/> off) —
/// the 1-of-20 ice look. A single-frame MD3 is a plain <see cref="ModelLoader.BuildModel"/>; an IQM / other
/// path is the textured <see cref="AssetSystem.LoadModel"/> <see cref="Node3D"/>.</para>
/// </summary>
public sealed partial class AttachedCosmeticModel : Node3D
{
    // The built model child (the single child of this node). Null only when resolution failed.
    private Node3D? _model;
    // Set only when the child is a frame-holding ModelAnimator (multi-frame MD3) — so SetRawFrame can re-hold.
    private ModelAnimator? _animator;
    // Cached flattened mesh list under the child (built once) for the colormod re-apply path.
    private readonly List<MeshInstance3D> _meshes = new();

    /// <summary>True once the model child resolved and was added (resolution can fail on a missing asset).</summary>
    public bool IsValid => _model is not null;

    /// <summary>
    /// Build a cosmetic model node.
    /// <list type="number">
    /// <item>Resolve the model: a multi-frame <c>.md3</c> is built via <see cref="ModelAnimator"/> (so a fixed
    /// frame can be held), a single-frame <c>.md3</c> via <see cref="ModelLoader.BuildModel"/>, and an
    /// <c>.iqm</c>/other path via <see cref="AssetSystem.LoadModel"/> (textured <see cref="Node3D"/>).</item>
    /// <item><see cref="Node3D.Position"/> = the QC follow offset (<paramref name="localOffsetQuake"/> in Quake
    /// convention, converted once).</item>
    /// <item><see cref="Node3D.Scale"/> = uniform <paramref name="scale"/> (buff glow 0.7).</item>
    /// <item>If <paramref name="rawFrame"/> &gt;= 0 and the child is a <see cref="ModelAnimator"/>, hold that
    /// raw frame (<see cref="ModelAnimator.FollowEntityFrame"/> off + <see cref="ModelAnimator.SetRawFrame"/>).</item>
    /// <item>If <paramref name="colormod"/> is given, tint the whole child once.</item>
    /// </list>
    /// Returns null when no <see cref="AssetSystem"/> was supplied or the model failed to resolve.
    /// </summary>
    public static AttachedCosmeticModel? Create(
        AssetSystem? assets,
        string modelPath,
        int skin = 0,
        int rawFrame = -1,
        Vector3 localOffsetQuake = default,
        float scale = 1f,
        Color? colormod = null,
        string name = "Cosmetic",
        Func<string, Md3Data?>? md3Loader = null,
        Func<string, int, Node3D?>? modelLoader = null)
    {
        if (string.IsNullOrEmpty(modelPath))
            return null;

        var node = new AttachedCosmeticModel { Name = string.IsNullOrEmpty(name) ? "Cosmetic" : name };
        Node3D? model = BuildChild(assets, modelPath, skin, rawFrame, name, md3Loader, modelLoader, out ModelAnimator? animator);
        if (model is null)
            return null;

        node._model = model;
        node._animator = animator;
        model.Name = "Model";
        node.AddChild(model);

        // (2) QC follow offset. localOffsetQuake is in Quake convention (e.g. ice '0 0 16'); the API surfaces it
        // as a Godot.Vector3, so re-pack its components into a Quake System.Numerics vector to convert once.
        node.Position = Coords.ToGodot(new System.Numerics.Vector3(localOffsetQuake.X, localOffsetQuake.Y, localOffsetQuake.Z));

        // (3) Uniform scale (buff glow 0.7).
        node.Scale = new Vector3(scale, scale, scale);

        // Flatten the child's meshes once for the colormod path (and any later re-apply).
        node.CollectMeshes(model);

        // (4) Hold a fixed raw frame on a multi-frame MD3 (1-of-20 ice look). Single-frame / IQM models have no
        // animator, so rawFrame is a no-op there (the built frame is already the held look).
        if (rawFrame >= 0 && animator is not null)
        {
            animator.FollowEntityFrame = false;
            animator.SetRawFrame(rawFrame);
        }

        // (5) Initial colormod tint.
        if (colormod is { } c)
            node.ApplyColormod(c);

        return node;
    }

    /// <summary>
    /// Re-apply a colormod tint to the whole child (team change / re-color). Walks the cached mesh list once:
    /// player-skin surfaces take it through the <c>colormod</c> instance-uniform (<see cref="ModelTint.SetColormod"/>),
    /// and a plain MD3 <see cref="StandardMaterial3D"/> surface (no such uniform) falls back to an
    /// <see cref="StandardMaterial3D.AlbedoColor"/> multiply via a per-surface override.
    /// </summary>
    public void SetColormod(Color colormod) => ApplyColormod(colormod);

    /// <summary>
    /// Re-select the held raw frame (no-op unless the child is a multi-frame MD3 <see cref="ModelAnimator"/>).
    /// </summary>
    public void SetRawFrame(int rawFrame)
    {
        if (rawFrame < 0 || _animator is null)
            return;
        _animator.FollowEntityFrame = false;
        _animator.SetRawFrame(rawFrame);
    }

    // =================================================================================================
    //  Internals
    // =================================================================================================

    /// <summary>
    /// Resolve + build the single model child. A multi-frame MD3 returns a <see cref="ModelAnimator"/> (so a
    /// frame can be held) and reports it via <paramref name="animator"/>; everything else returns a plain node.
    /// </summary>
    private static Node3D? BuildChild(
        AssetSystem? assets, string modelPath, int skin, int rawFrame, string name,
        Func<string, Md3Data?>? md3Loader, Func<string, int, Node3D?>? modelLoader, out ModelAnimator? animator)
    {
        animator = null;
        if (assets is null)
            return null;

        if (modelPath.EndsWith(".md3", StringComparison.OrdinalIgnoreCase))
        {
            // Parse the raw MD3 through the host-wired loader (AssetLoader.LoadMd3, cached). Without a loader
            // there is no way to read the file, so resolution fails (the cosmetic is simply not drawn).
            Md3Data? md3 = md3Loader?.Invoke(modelPath);
            if (md3 is null)
                return null;

            // Multi-frame MD3 → animator (so a single frame can be HELD); single-frame → plain built mesh.
            if (md3.FrameCount > 1)
            {
                ModelAnimator a = ModelAnimator.Create(md3, name, assets);
                animator = a;
                return a;
            }
            return ModelLoader.BuildModel(md3, 0, assets);
        }

        // .iqm / .dpm / other → the textured Node3D from the asset pipeline (AssetLoader.LoadModel).
        return modelLoader?.Invoke(modelPath, skin);
    }

    /// <summary>Depth-first collect every <see cref="MeshInstance3D"/> under <paramref name="root"/> (once).</summary>
    private void CollectMeshes(Node root)
    {
        _meshes.Clear();
        Walk(root, _meshes);

        static void Walk(Node node, List<MeshInstance3D> into)
        {
            if (node is MeshInstance3D mi)
                into.Add(mi);
            foreach (Node child in node.GetChildren())
                Walk(child, into);
        }
    }

    /// <summary>
    /// Apply <paramref name="colormod"/> across the cached mesh list. The player-skin <c>colormod</c>
    /// instance-uniform is set on every mesh (harmless on surfaces without it). For a non-player MD3 surface
    /// backed by a <see cref="StandardMaterial3D"/>, fall back to an albedo multiply through a per-surface
    /// override (a duplicated material so the shared/cached source material is never mutated).
    /// </summary>
    private void ApplyColormod(Color colormod)
    {
        if (_meshes.Count == 0)
            return;

        // Player-skin path: the colormod instance-uniform (no-op where the shader has no such uniform).
        ModelTint.SetColormod(_meshes, colormod);

        // Plain-MD3 fallback: multiply AlbedoColor on a per-surface StandardMaterial3D override.
        foreach (MeshInstance3D mi in _meshes)
        {
            if (mi.Mesh is null)
                continue;
            int count = mi.Mesh.GetSurfaceCount();
            for (int s = 0; s < count; s++)
            {
                // Prefer an existing override (a previous SetColormod), else the surface's own material.
                Material? src = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                if (src is not StandardMaterial3D std)
                    continue;
                var tinted = (StandardMaterial3D)std.Duplicate();
                tinted.AlbedoColor = colormod;
                mi.SetSurfaceOverrideMaterial(s, tinted);
            }
        }
    }
}
