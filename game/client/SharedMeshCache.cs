using Godot;
using System;
using System.Collections.Generic;

namespace VortexArena.Game.Client;

/// <summary>
/// [crash fix 2026-07-26] Main-thread visual cache for TRANSIENT props (shell casings, gibs, MD3
/// projectile bodies): the model loaders build a fresh node tree — including a fresh
/// <see cref="ArrayMesh"/> — on every call, and each <c>QueueFree</c>d spawn then released that
/// mesh on the .NET FINALIZER thread, whose <c>RenderingServer::free</c> races the main thread's
/// RenderingDevice work (the 0xC0000374 heap-corruption family; measured 10-25 resources/s from
/// casings alone under sustained fire). This cache builds ONE tree per key, extracts its first
/// mesh (+ the composed local transform down to it), frees the rest, and hands every subsequent
/// spawn a bare <see cref="MeshInstance3D"/> SHARING that mesh — per-spawn resource creation and
/// destruction drop to zero.
///
/// Only suitable for props whose visual is a single static mesh (no tags/skeleton needed on the
/// spawned instance — a skinned mesh renders its bind pose, fine for flying brass). Held weapons
/// keep their per-build trees (muzzle-tag attachments) and dispose deterministically instead.
/// Main-thread only. Failed builds retry for a few spawns (a first-resolve failure can be TRANSIENT —
/// early-join before the resolver/VFS is warm) and only then negative-cache, so a genuinely missing
/// model still stops re-parsing per spawn.
/// </summary>
public static class SharedMeshCache
{
    /// <summary>
    /// One cached prop visual. The MATERIALS matter as much as the mesh: builders are split on where they
    /// put them — <c>IqmBuilder</c>/MD3 call <c>ArrayMesh.SurfaceSetMaterial</c> (mesh-level, so extracting
    /// the mesh carries them along), but <c>MdlBuilder</c> deliberately applies the palette-decoded skin as a
    /// <see cref="MeshInstance3D.MaterialOverride"/> on the INSTANCE so a per-instance fade can never mutate
    /// the shared resource. Keeping only the mesh therefore rendered every MDL-sourced prop untextured —
    /// <c>models/casing_shell.mdl</c> (the shotgun casing) and the Quake1 <c>chunk.mdl</c> gibs. Capture both
    /// levels here and re-apply per instance.
    /// </summary>
    private readonly record struct Prop(
        Mesh? Mesh, Transform3D Xform, Material? Override, Material?[]? SurfaceOverrides);

    private static readonly Dictionary<string, Prop> _cache = new();

    // Consecutive failed builds per key — a permanent negative entry only lands after MaxBuildAttempts,
    // so one transient early-session failure can't condemn a model to the placeholder for the whole process.
    private static readonly Dictionary<string, int> _failures = new();
    private const int MaxBuildAttempts = 8;

    /// <summary>A fresh MeshInstance3D sharing the cached mesh + materials for <paramref name="key"/>, or null
    /// when the builder produced no usable mesh (caller falls back to its generated prop).</summary>
    public static MeshInstance3D? Instantiate(string key, Func<Node3D?> build)
    {
        if (!_cache.TryGetValue(key, out Prop v))
        {
            v = Extract(build);
            if (v.Mesh is not null)
            {
                _cache[key] = v;
                _failures.Remove(key);
            }
            else
            {
                int n = _failures.GetValueOrDefault(key) + 1;
                _failures[key] = n;
                if (n >= MaxBuildAttempts)
                    _cache[key] = v; // stubbornly missing — negative-cache so it never re-parses per spawn
                return null;
            }
        }
        if (v.Mesh is null)
            return null;

        var inst = new MeshInstance3D { Mesh = v.Mesh, Transform = v.Xform, MaterialOverride = v.Override };
        if (v.SurfaceOverrides is not null)
        {
            int n = System.Math.Min(v.SurfaceOverrides.Length, inst.GetSurfaceOverrideMaterialCount());
            for (int s = 0; s < n; s++)
                if (v.SurfaceOverrides[s] is not null)
                    inst.SetSurfaceOverrideMaterial(s, v.SurfaceOverrides[s]);
        }
        return inst;
    }

    private static Prop Extract(Func<Node3D?> build)
    {
        Node3D? root;
        try { root = build(); }
        catch { return default; }
        if (root is null)
            return default;

        (MeshInstance3D? mi, Transform3D xform) = FindFirstMesh(root, Transform3D.Identity);
        Mesh? mesh = mi?.Mesh;
        Material? matOverride = null;
        Material?[]? surfaceOverrides = null;
        if (mi is not null)
        {
            // Read the materials BEFORE the tree is freed.
            matOverride = mi.MaterialOverride;
            int count = mi.GetSurfaceOverrideMaterialCount();
            for (int s = 0; s < count; s++)
            {
                Material? m = mi.GetSurfaceOverrideMaterial(s);
                if (m is null) continue;
                surfaceOverrides ??= new Material?[count];
                surfaceOverrides[s] = m;
            }
            mi.Mesh = null; // keep the extracted mesh alive past the tree free
        }
        root.Free();        // never entered the tree: immediate main-thread free is safe
        return new Prop(mesh, xform, matOverride, surfaceOverrides);
    }

    private static (MeshInstance3D?, Transform3D) FindFirstMesh(Node node, Transform3D acc)
    {
        if (node is Node3D n3)
            acc *= n3.Transform;
        if (node is MeshInstance3D mi && mi.Mesh is not null)
            return (mi, acc);
        foreach (Node c in node.GetChildren())
        {
            (MeshInstance3D? found, Transform3D x) = FindFirstMesh(c, acc);
            if (found is not null)
                return (found, x);
        }
        return (null, acc);
    }
}
