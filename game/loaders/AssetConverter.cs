using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The offline asset-conversion path from the migration plan: a batch helper that walks every model and map
/// in the mounted VFS, runs it through the live <see cref="AssetLoader"/> (the exact same importers + Godot
/// builders the game uses at runtime), and bakes the result to a Godot <c>.tscn</c> scene on disk via
/// <see cref="PackedScene"/> + <see cref="ResourceSaver"/>.
///
/// <para>This lets the project pre-convert the Xonotic content set once (instead of importing every model
/// from its native format at load time) and then ship/load the lighter Godot scenes. It is intentionally
/// best-effort: each asset is converted inside its own try/catch so one malformed file can't abort the whole
/// batch — failures are logged with <see cref="GD.Print"/>/<see cref="GD.PrintErr"/> and counted.</para>
///
/// <para>Output layout mirrors the source vpath under <paramref name="outDir"/> with the extension swapped to
/// <c>.tscn</c> (e.g. <c>models/player/erebus.iqm</c> → <c>&lt;outDir&gt;/models/player/erebus.tscn</c>), so the
/// converted tree is browsable the same way as the source. <paramref name="outDir"/> may be an absolute OS
/// path or a Godot <c>user://</c> path.</para>
/// </summary>
public static class AssetConverter
{
    /// <summary>
    /// Convert every model and map the loader can enumerate to a <c>.tscn</c> under <paramref name="outDir"/>.
    /// Returns the number of scenes successfully written. Best-effort: errors are logged and skipped.
    /// </summary>
    public static int ConvertAll(AssetLoader loader, string outDir)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (string.IsNullOrWhiteSpace(outDir))
        {
            GD.PrintErr("[AssetConverter] outDir is empty; nothing converted.");
            return 0;
        }

        EnsureDir(outDir);

        int ok = 0, fail = 0;

        GD.Print("[AssetConverter] converting models…");
        foreach (string vpath in loader.EnumerateModels())
        {
            if (TryConvert(() => loader.LoadModel(vpath), vpath, outDir))
                ok++;
            else
                fail++;
        }

        GD.Print("[AssetConverter] converting maps…");
        foreach (string vpath in loader.EnumerateMaps())
        {
            if (TryConvert(() => loader.LoadMap(vpath), vpath, outDir))
                ok++;
            else
                fail++;
        }

        GD.Print($"[AssetConverter] done: {ok} scene(s) written, {fail} skipped, to '{outDir}'.");
        return ok;
    }

    /// <summary>
    /// Convert only the sprites in the VFS (separate entry point because there are many tiny sprites and a
    /// caller may not want them in the same batch as models/maps).
    /// </summary>
    public static int ConvertSprites(AssetLoader loader, string outDir)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (string.IsNullOrWhiteSpace(outDir))
            return 0;

        EnsureDir(outDir);
        int ok = 0, fail = 0;
        foreach (string vpath in loader.EnumerateSprites())
        {
            if (TryConvert(() => loader.LoadSprite(vpath), vpath, outDir))
                ok++;
            else
                fail++;
        }
        GD.Print($"[AssetConverter] sprites: {ok} written, {fail} skipped.");
        return ok;
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Run one <paramref name="build"/> delegate, pack the resulting node into a scene, and save it as a
    /// <c>.tscn</c> derived from <paramref name="vpath"/>. Wrapped so a single failure is logged and skipped.
    /// Returns true on a successful save.
    /// </summary>
    private static bool TryConvert(Func<Node3D?> build, string vpath, string outDir)
    {
        Node3D? node = null;
        try
        {
            node = build();
            if (node is null)
            {
                GD.Print($"[AssetConverter] skip (no node): {vpath}");
                return false;
            }

            string outPath = OutPathFor(outDir, vpath);
            EnsureDir(DirOf(outPath));

            PackedScene scene = PackScene(node);
            Error err = ResourceSaver.Save(scene, outPath);
            if (err != Error.Ok)
            {
                GD.PrintErr($"[AssetConverter] save failed ({err}): {outPath}");
                return false;
            }
            GD.Print($"[AssetConverter] wrote {outPath}");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetConverter] error converting '{vpath}': {ex.Message}");
            return false;
        }
        finally
        {
            // The built node is a throwaway template for packing; free it so the batch doesn't leak nodes.
            node?.QueueFree();
        }
    }

    /// <summary>
    /// Pack <paramref name="root"/> into a <see cref="PackedScene"/>. Every descendant must have its
    /// <see cref="Node.Owner"/> set to the root or <see cref="PackedScene.Pack"/> drops it, so we assign
    /// ownership across the whole subtree first.
    /// </summary>
    private static PackedScene PackScene(Node3D root)
    {
        AssignOwner(root, root);
        var scene = new PackedScene();
        Error err = scene.Pack(root);
        if (err != Error.Ok)
            throw new InvalidOperationException($"PackedScene.Pack returned {err}.");
        return scene;
    }

    /// <summary>Recursively set <see cref="Node.Owner"/> = <paramref name="owner"/> on every child (not the root).</summary>
    private static void AssignOwner(Node node, Node owner)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child != owner)
                child.Owner = owner;
            AssignOwner(child, owner);
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Path helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Map a source vpath to its output <c>.tscn</c> path under <paramref name="outDir"/>.</summary>
    private static string OutPathFor(string outDir, string vpath)
    {
        string rel = StripExtension(vpath) + ".tscn";
        return JoinGodot(outDir, rel);
    }

    /// <summary>Join two path segments with a single forward slash (Godot paths use forward slashes).</summary>
    private static string JoinGodot(string a, string b)
    {
        a = a.TrimEnd('/', '\\');
        b = b.TrimStart('/', '\\');
        return a + "/" + b;
    }

    private static string DirOf(string path)
    {
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash <= 0 ? path : path.Substring(0, slash);
    }

    private static string StripExtension(string path)
    {
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        int dot = path.LastIndexOf('.');
        return (dot > slash) ? path.Substring(0, dot) : path;
    }

    /// <summary>
    /// Make sure <paramref name="dir"/> exists, recursively, using Godot's <see cref="DirAccess"/> so both
    /// <c>user://</c> and absolute OS paths work the same way.
    /// </summary>
    private static void EnsureDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        Error err = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (err != Error.Ok && err != Error.AlreadyExists)
            GD.PrintErr($"[AssetConverter] could not create dir '{dir}': {err}");
    }
}
