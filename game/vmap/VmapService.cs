using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// Console front-end for the editable map format: converts a shipped <c>.bsp</c> or an authored <c>.map</c>
/// into a <c>.vmap</c> package, and reports on an existing one.
///
/// <code>
///   vmap_import &lt;name&gt; [--zip]   maps/&lt;name&gt;.bsp (or .map) -> user://vmaps/&lt;name&gt;.vmap
///   vmap_info   &lt;name&gt;           summarize an imported package
///   vmap_list                     list imported packages
/// </code>
///
/// Client-side (registered on the shared <see cref="ConfigInterpreter"/> like <c>screenshot</c>): importing is
/// a local authoring action with no multiplayer effect, so it never routes to the server.
/// </summary>
public sealed class VmapService
{
    /// <summary>Where imported packages are written. Godot user data, so it survives reinstalls and is writable.</summary>
    public const string VmapUserDir = "user://vmaps";

    private readonly VirtualFileSystem _vfs;
    private readonly AssetSystem? _assets;

    /// <param name="vfs">Search path used to locate the source map.</param>
    /// <param name="assets">
    /// Optional material facade. When present, a <c>.map</c> import resolves each shader's real pixel size so
    /// Radiant's texel-based texdefs convert at the correct scale; without it the importer falls back to
    /// 64x64 and textures on non-64px materials come out at the wrong scale.
    /// </param>
    public VmapService(VirtualFileSystem vfs, AssetSystem? assets = null)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _assets = assets;
    }

    /// <summary>Register the vmap commands on the shared interpreter.</summary>
    public void RegisterCommands(ConfigInterpreter interp)
    {
        ArgumentNullException.ThrowIfNull(interp);
        interp.RegisterCommand("vmap_import", OnImport);
        interp.RegisterCommand("vmap_info", OnInfo);
        interp.RegisterCommand("vmap_list", OnList);
    }

    // =============================================================================================
    //  vmap_import
    // =============================================================================================

    private void OnImport(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2)
        {
            Log.Help("usage: vmap_import <mapname> [--zip]   (imports maps/<mapname>.bsp or .map)");
            return;
        }

        string name = SanitizeName(argv[1]);
        if (string.IsNullOrEmpty(name))
        {
            Log.Warn("vmap_import: invalid map name");
            return;
        }

        bool zip = false;
        for (int i = 2; i < argv.Count; i++)
            if (string.Equals(argv[i], "--zip", StringComparison.OrdinalIgnoreCase))
                zip = true;

        try
        {
            var sw = Stopwatch.StartNew();
            VmapDocument doc = ImportByName(name, out string sourceVpath);
            sw.Stop();

            string outPath = OutputPath(name, zip);
            if (zip)
                VmapPackage.WriteToZip(doc, outPath);
            else
                VmapPackage.WriteToDirectory(doc, outPath);

            Log.Info($"vmap_import: {sourceVpath} -> {outPath}");
            Log.Info($"  {doc.Brushes.Count} brushes, {doc.Patches.Count} patches, {doc.Entities.Count} entities " +
                     $"({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex) when (ex is AssetParseException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"vmap_import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Locate and import <paramref name="name"/>, preferring an authored <c>.map</c> over a compiled
    /// <c>.bsp</c>: the source file carries real texture alignment and func_group/detail structure that
    /// compilation discards, so it is strictly the better input when both exist.
    /// </summary>
    public VmapDocument ImportByName(string name, out string sourceVpath)
    {
        foreach (string candidate in MapCandidates(name, ".map"))
        {
            if (!_vfs.Exists(candidate))
                continue;
            sourceVpath = candidate;
            byte[] bytes = _vfs.ReadBytes(candidate);
            string text = _vfs.ReadText(candidate);
            var warnings = new List<string>();
            VmapDocument doc = MapSourceReader.Read(
                text, name, candidate, VmapPackage.HashBytes(bytes), TextureSizeResolver(), warnings);
            ReportWarnings(warnings);
            return doc;
        }

        foreach (string candidate in MapCandidates(name, ".bsp"))
        {
            if (!_vfs.Exists(candidate))
                continue;
            sourceVpath = candidate;
            byte[] bytes = _vfs.ReadBytes(candidate);
            BspData bsp = BspReader.Read(bytes);
            return BspToVmap.Import(bsp, name, candidate, VmapPackage.HashBytes(bytes));
        }

        throw new AssetParseException($"no maps/{name}.map or maps/{name}.bsp found on the search path");
    }

    /// <summary>
    /// Accepted virtual paths for a map name, mirroring how <c>NetGame</c> resolves a BSP: a bare name, a
    /// name already carrying the extension, and both with the <c>maps/</c> prefix.
    /// </summary>
    private static IEnumerable<string> MapCandidates(string name, string extension)
    {
        bool hasExt = name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        string bare = hasExt ? name[..^extension.Length] : name;

        yield return $"maps/{bare}{extension}";
        yield return $"{bare}{extension}";
    }

    /// <summary>
    /// Resolve a shader name to its texture's pixel size for texdef conversion, or null when no asset system
    /// is available (the importer then assumes 64x64, matching q3map2's own missing-image fallback).
    /// </summary>
    private Func<string, (int Width, int Height)>? TextureSizeResolver()
    {
        if (_assets is null)
            return null;

        var cache = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        return shader =>
        {
            if (cache.TryGetValue(shader, out (int, int) size))
                return size;

            (int, int) resolved = (MapSourceReader.DefaultTextureSize, MapSourceReader.DefaultTextureSize);
            try
            {
                Image? image = _assets.LoadImage(shader);
                if (image is not null && image.GetWidth() > 0 && image.GetHeight() > 0)
                    resolved = (image.GetWidth(), image.GetHeight());
            }
            catch (Exception ex)
            {
                Log.Warn($"vmap_import: texture size lookup failed for '{shader}': {ex.Message}");
            }

            cache[shader] = resolved;
            return resolved;
        };
    }

    private static void ReportWarnings(List<string> warnings)
    {
        const int maxShown = 10;
        for (int i = 0; i < warnings.Count && i < maxShown; i++)
            Log.Warn($"  {warnings[i]}");
        if (warnings.Count > maxShown)
            Log.Warn($"  ... and {warnings.Count - maxShown} more warnings");
    }

    // =============================================================================================
    //  vmap_info / vmap_list
    // =============================================================================================

    private void OnInfo(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2)
        {
            Log.Help("usage: vmap_info <mapname>");
            return;
        }

        string name = SanitizeName(argv[1]);
        string? path = FindPackage(name);
        if (path is null)
        {
            Log.Warn($"vmap_info: no imported package named '{name}' (run vmap_import {name})");
            return;
        }

        try
        {
            VmapDocument doc = VmapPackage.Read(path);
            int faces = 0, detail = 0;
            foreach (VmapBrush b in doc.Brushes)
            {
                faces += b.Faces.Count;
                if (b.IsDetail)
                    detail++;
            }

            Log.Info($"{path}");
            Log.Info($"  format v{doc.FormatVersion}, imported from {doc.Manifest.SourceKind} " +
                     $"'{doc.Manifest.SourcePath}' (hash {doc.Manifest.SourceHash})");
            Log.Info($"  {doc.Brushes.Count} brushes ({detail} detail, {faces} faces), " +
                     $"{doc.Patches.Count} patches, {doc.Entities.Count} entities");
        }
        catch (Exception ex) when (ex is AssetParseException or IOException)
        {
            Log.Warn($"vmap_info failed: {ex.Message}");
        }
    }

    private void OnList(IReadOnlyList<string> argv)
    {
        string dir = ProjectSettings.GlobalizePath(VmapUserDir);
        if (!Directory.Exists(dir))
        {
            Log.Info("no imported vmap packages (use vmap_import <mapname>)");
            return;
        }

        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*" + VmapPackage.Extension))
        {
            Log.Info($"  {Path.GetFileName(entry)}{(Directory.Exists(entry) ? "/" : "")}");
            count++;
        }
        if (count == 0)
            Log.Info("no imported vmap packages (use vmap_import <mapname>)");
    }

    /// <summary>Path of an imported package, preferring the editable directory layout over a packed zip.</summary>
    public static string? FindPackage(string name)
    {
        string dir = ProjectSettings.GlobalizePath(VmapUserDir);
        string asDirectory = Path.Combine(dir, name + VmapPackage.Extension);
        if (Directory.Exists(asDirectory))
            return asDirectory;
        return File.Exists(asDirectory) ? asDirectory : null;
    }

    /// <summary>
    /// Where the editor writes files it authors (the .vmap package, the mapinfo). The user data directory, not
    /// the mounted asset tree: the source content may be inside a .pk3, and an editor that reaches into shipped
    /// game data to overwrite a file is doing something the mapper did not ask for.
    /// </summary>
    public static string EditorOutputDirectory()
    {
        string dir = ProjectSettings.GlobalizePath(VmapUserDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string OutputPath(string name, bool zip)
    {
        string dir = ProjectSettings.GlobalizePath(VmapUserDir);
        Directory.CreateDirectory(dir);
        _ = zip; // both layouts use the same name; the writer decides file vs directory
        return Path.Combine(dir, name + VmapPackage.Extension);
    }

    /// <summary>
    /// Strip path separators and traversal from a user-supplied name so a console argument cannot write
    /// outside the vmap directory.
    /// </summary>
    private static string SanitizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        string name = raw.Trim().Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), string.Empty);
        return name.Trim('.');
    }
}
