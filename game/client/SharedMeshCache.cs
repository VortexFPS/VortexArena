using Godot;
using System;
using System.Collections.Generic;

namespace XonoticGodot.Game.Client;

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
/// Main-thread only; failed builds are cached as null so a missing model never re-parses per spawn.
/// </summary>
public static class SharedMeshCache
{
    private static readonly Dictionary<string, (Mesh? Mesh, Transform3D Xform)> _cache = new();

    /// <summary>A fresh MeshInstance3D sharing the cached mesh for <paramref name="key"/>, or null
    /// when the builder produced no usable mesh (caller falls back to its generated prop).</summary>
    public static MeshInstance3D? Instantiate(string key, Func<Node3D?> build)
    {
        if (!_cache.TryGetValue(key, out (Mesh? Mesh, Transform3D Xform) v))
        {
            v = Extract(build);
            _cache[key] = v;
        }
        return v.Mesh is null ? null : new MeshInstance3D { Mesh = v.Mesh, Transform = v.Xform };
    }

    private static (Mesh?, Transform3D) Extract(Func<Node3D?> build)
    {
        Node3D? root;
        try { root = build(); }
        catch { return (null, Transform3D.Identity); }
        if (root is null)
            return (null, Transform3D.Identity);

        (MeshInstance3D? mi, Transform3D xform) = FindFirstMesh(root, Transform3D.Identity);
        Mesh? mesh = mi?.Mesh;
        if (mi is not null)
            mi.Mesh = null; // keep the extracted mesh alive past the tree free
        root.Free();        // never entered the tree: immediate main-thread free is safe
        return (mesh, xform);
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
