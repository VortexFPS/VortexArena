using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Vfs;

namespace VortexArena.Game.Loaders;

/// <summary>
/// Loads TrueType / OpenType UI fonts out of the virtual filesystem (the <c>font-*.pk3dir</c> packs Xonotic
/// ships — Xolonium for the HUD/menu, plus DejaVu / Nimbus Sans / Unifont fallbacks) into Godot
/// <see cref="FontFile"/> resources, with a name → font cache so a font is parsed once and shared.
///
/// <para>Godot can only build a dynamic font from a real <c>res://</c>/<c>user://</c> path or from raw bytes.
/// Our fonts live inside pk3dir mounts that Godot's resource loader knows nothing about, so we pull the
/// bytes through the <see cref="VirtualFileSystem"/> and hand them to <see cref="FontFile"/> via its
/// <see cref="FontFile.Data"/> buffer (the in-memory load path — no temp file needed).</para>
///
/// <para>Lookup is by a short logical name (e.g. <c>"xolonium"</c>, <c>"xolonium-bold"</c>) resolved against
/// a built-in table of the canonical pack paths, with a generic <c>fonts/&lt;name&gt;</c> probe and an
/// extension search (.otf then .ttf) as a fallback so an arbitrary font path also works. The default UI font
/// is Xolonium (regular), matching the stock Xonotic menu/HUD font.</para>
/// </summary>
public sealed class FontLoader
{
    private readonly VirtualFileSystem _vfs;

    // Logical name (lower-case) -> resolved FontFile. Null entries cache a known miss so we don't re-probe.
    private readonly Dictionary<string, FontFile?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Canonical logical names → the vpaths they live at in the stock font packs. The first existing path
    /// wins; the generic fallback search covers anything not listed here.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xolonium"] = new[] { "fonts/xolonium-regular.otf", "fonts/xolonium-regular.ttf", "fonts/xolonium.otf" },
        ["xolonium-regular"] = new[] { "fonts/xolonium-regular.otf", "fonts/xolonium-regular.ttf" },
        ["xolonium-bold"] = new[] { "fonts/xolonium-bold.otf", "fonts/xolonium-bold.ttf" },
        ["dejavu"] = new[] { "fonts/DejaVuSans.ttf", "fonts/dejavusans.ttf" },
        ["dejavusans"] = new[] { "fonts/DejaVuSans.ttf", "fonts/dejavusans.ttf" },
        // The console face. DP renders the drop-down console in a fixed-width font (FONT_CONSOLE), which is what
        // makes its column-formatted completion and cvarlist output line up; Xonotic ships this one for it.
        ["dejavusansmono"] = new[] { "fonts/DejaVuSansMono.ttf", "fonts/dejavusansmono.ttf" },
        ["mono"] = new[] { "fonts/DejaVuSansMono.ttf", "fonts/dejavusansmono.ttf" },
        ["nimbussansl"] = new[] { "fonts/nimbussansl.otf", "fonts/NimbusSansL.otf" },
        ["unifont"] = new[] { "fonts/unifont.ttf", "fonts/Unifont.ttf" },
    };

    /// <summary>The default UI font logical name (Xonotic uses Xolonium for the menu/HUD).</summary>
    public const string DefaultFontName = "xolonium";

    public FontLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
    }

    /// <summary>
    /// Return the font for <paramref name="name"/>, loading and caching it on first use. <paramref name="name"/>
    /// may be a known logical name (<c>"xolonium"</c>, <c>"xolonium-bold"</c>, …) or a direct vpath to a
    /// <c>.ttf</c>/<c>.otf</c> inside a mount. Returns <c>null</c> if the font cannot be found or parsed.
    /// </summary>
    public FontFile? GetFont(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultFontName;

        if (_cache.TryGetValue(name, out FontFile? cached))
            return cached;

        FontFile? font = LoadFontUncached(name);
        _cache[name] = font; // cache hits AND misses (null) so a missing font is probed once
        return font;
    }

    /// <summary>Convenience accessor for the default Xolonium UI font.</summary>
    public FontFile? GetUiFont() => GetFont(DefaultFontName);

    /// <summary>
    /// Load a font from an explicit virtual path (no logical-name resolution). Cached under the path.
    /// Useful for menu themes that reference a font by its data path.
    /// </summary>
    public FontFile? GetFontFromVPath(string vpath)
    {
        if (string.IsNullOrWhiteSpace(vpath))
            return null;
        if (_cache.TryGetValue(vpath, out FontFile? cached))
            return cached;

        FontFile? font = TryLoadVPath(vpath);
        _cache[vpath] = font;
        return font;
    }

    // ---------------------------------------------------------------------------------------------

    private FontFile? LoadFontUncached(string name)
    {
        // 1) Known logical name: try each canonical path in order.
        if (KnownFonts.TryGetValue(name, out string[]? candidates))
        {
            foreach (string vpath in candidates)
            {
                FontFile? f = TryLoadVPath(vpath);
                if (f is not null)
                    return f;
            }
        }

        // 2) Treat the name as (or as the stem of) a direct path. Probe the name verbatim, then under
        //    fonts/, with .otf preferred over .ttf (Xonotic ships .otf for Xolonium/Nimbus).
        foreach (string vpath in ProbePaths(name))
        {
            FontFile? f = TryLoadVPath(vpath);
            if (f is not null)
                return f;
        }

        GD.PrintErr($"[FontLoader] font '{name}' not found in any mount.");
        return null;
    }

    /// <summary>The ordered vpaths a bare/logical name is probed at when it isn't in <see cref="KnownFonts"/>.</summary>
    private static IEnumerable<string> ProbePaths(string name)
    {
        string ext = AssetPaths.GetExtension(name);
        bool hasFontExt = ext is "ttf" or "otf";

        if (hasFontExt)
        {
            // Already a concrete font filename: verbatim, then under fonts/.
            yield return name;
            yield return "fonts/" + name;
            yield break;
        }

        // No extension: try .otf then .ttf, both verbatim and under fonts/.
        yield return name + ".otf";
        yield return name + ".ttf";
        yield return "fonts/" + name + ".otf";
        yield return "fonts/" + name + ".ttf";
    }

    /// <summary>
    /// Read the font bytes from the VFS (if present) and build a <see cref="FontFile"/> from them. Returns
    /// null when the path is absent or the bytes don't parse as a dynamic font.
    /// </summary>
    private FontFile? TryLoadVPath(string vpath)
    {
        string norm = AssetPaths.Normalize(vpath);
        if (norm.Length == 0 || !_vfs.Exists(norm))
            return null;

        try
        {
            byte[] data = _vfs.ReadBytes(norm);
            return FromBytes(data, norm);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[FontLoader] failed to load font '{norm}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Build a <see cref="FontFile"/> from raw TTF/OTF bytes by assigning <see cref="FontFile.Data"/> (Godot's
    /// in-memory dynamic-font load path — no temp file). Returns null if the bytes don't parse as a font
    /// (zero faces). The resource name is set to the source path for diagnostics.
    /// </summary>
    public static FontFile? FromBytes(byte[] data, string sourceName = "")
    {
        if (data is null || data.Length == 0)
            return null;

        var font = new FontFile { ResourceName = sourceName };
        // Assigning Data makes Godot treat the resource as a dynamic font backed by these bytes.
        font.Data = data;

        // Sanity: a parsed dynamic font reports at least one face. If not, it isn't a usable font.
        if (font.GetFaceCount() <= 0)
        {
            GD.PrintErr($"[FontLoader] '{sourceName}' did not parse as a dynamic font (0 faces).");
            return null;
        }
        return font;
    }
}
