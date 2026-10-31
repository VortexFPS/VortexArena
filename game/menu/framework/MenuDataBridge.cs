using System;
using Godot;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Godot-side bridge from the (Godot-free) menu <see cref="DataSource"/> backends to the mounted asset
/// VFS and the shared cvar store. The data sources in <c>VortexArena.Common</c> deliberately don't
/// reference Godot (ADR-0008), so each list backend takes these seams; this is the single place that wires
/// them to <see cref="MenuState"/>. Reused by the screenshot / music / skin dialogs.
/// </summary>
public static class MenuDataBridge
{
    /// <summary>
    /// An <see cref="IFileEnumerator"/> over <see cref="MenuState.Vfs"/> — the C# stand-in for the engine
    /// <c>search_begin</c> the QC list backends use. Empty (no matches) when the VFS isn't mounted, so a list
    /// shown before <see cref="MenuState.Boot"/> just renders empty rather than crashing.
    /// </summary>
    public sealed class VfsFiles : IFileEnumerator
    {
        public System.Collections.Generic.IEnumerable<string> Find(string prefix, string? extension = null)
            => MenuState.Vfs?.Find(prefix, extension) ?? System.Array.Empty<string>();
    }

    /// <summary>The shared file enumerator (singleton — the VFS is process-wide).</summary>
    public static readonly VfsFiles Files = new();

    /// <summary>Read a cvar's current string value from the shared store (CvarStringSource seam).</summary>
    public static string CvarString(string name) => MenuState.Cvars.GetString(name);

    /// <summary>Read a UTF-8 text file from the VFS, or null if absent (skinvalues.txt / .mapinfo seam).</summary>
    public static string? ReadText(string vpath)
    {
        var vfs = MenuState.Vfs;
        if (vfs is null || !vfs.Exists(vpath))
            return null;
        try { return vfs.ReadText(vpath); }
        catch { return null; }
    }

    /// <summary>
    /// Does a preview image exist for the (extension-agnostic) base name? Mirrors the QC
    /// <c>draw_PictureExists</c> / <c>draw_PictureSize != '0 0 0'</c> probes (skin preview, map preview). Uses
    /// the VFS image-resolution search (the DP extension precedence). Tolerates a leading "/".
    /// </summary>
    public static bool ImageExists(string vpathBase)
    {
        var vfs = MenuState.Vfs;
        if (vfs is null)
            return false;
        string baseName = vpathBase.StartsWith('/') ? vpathBase[1..] : vpathBase;
        try { return vfs.ResolveImage(baseName) is not null; }
        catch { return false; }
    }
}
