// Port of qcsrc/menu/xonotic/datasource.qh + datasource.qc
//   (the shared menu DataSource abstraction: getEntry / indexOf / reload / destroy,
//    plus StringSource and CvarStringSource)
//
// This is a faithful C# port of the QuakeC menu's generic list backend. Every Xonotic
// menu list (screenshots, sounds, skins, stats, maps, …) drains a `DataSource`: the UI
// calls reload(filter) to (re)scan, getEntry(i, returns) to read one row's name+icon, and
// indexOf(find) to locate a row by name. The QC class returned the sentinel entities
// DataSource_true / DataSource_false from getEntry (true = "row exists", false = "out of
// bounds"); here we return a plain bool from <see cref="TryGetEntry"/> and surface the
// name/icon via out params, which is the same contract minus the entity bookkeeping.
//
// ADR-0008: XonoticGodot.Common must not reference Godot, so the file-scan sources take an
// <see cref="IFileEnumerator"/> seam (the menu host wires a Godot-VFS adapter; tests pass a
// fake), keeping the whole abstraction headless-testable.

using System;
using System.Collections.Generic;

namespace XonoticGodot.Common.Menu;

/// <summary>
/// One enumerated row, the C# stand-in for the QC <c>returns(string name, string icon)</c> callback
/// (datasource.qh:11). <see cref="Icon"/> is null when the source supplies no icon (QC <c>string_null</c>).
/// </summary>
public readonly struct DataSourceEntry
{
    public DataSourceEntry(string name, string? icon = null)
    {
        Name = name;
        Icon = icon;
    }

    public string Name { get; }
    public string? Icon { get; }
}

/// <summary>
/// The generic menu list backend — C# successor to QC <c>CLASS(DataSource, Object)</c>
/// (datasource.qh:3-18). Subclasses override <see cref="Reload"/> (rescan + report match count),
/// <see cref="TryGetEntry"/> (read one row), and optionally <see cref="IndexOf"/> / <see cref="Destroy"/>.
/// </summary>
public abstract class DataSource
{
    /// <summary>
    /// Get entry <paramref name="i"/>, passing its name+icon out through <paramref name="entry"/>.
    /// Returns false when out of bounds (QC <c>DataSource_false</c>), true otherwise (QC
    /// <c>DataSource_true</c> / an entity). datasource.qh:11.
    /// </summary>
    public abstract bool TryGetEntry(int i, out DataSourceEntry entry);

    /// <summary>Return the index of the first row whose name matches <paramref name="find"/>, or -1. datasource.qh:13 (optional).</summary>
    public virtual int IndexOf(string find) => -1;

    /// <summary>Reload all entries matching <paramref name="filter"/>, returning how many matched. datasource.qh:15.</summary>
    public abstract int Reload(string? filter);

    /// <summary>Cleanup on shutdown. datasource.qh:17 (optional; default no-op).</summary>
    public virtual void Destroy() { }
}

/// <summary>
/// A list backed by a single separator-delimited string — C# successor to QC
/// <c>CLASS(StringSource, DataSource)</c> (datasource.qh:21-27 / datasource.qc:3-20). The QC tokenizes
/// the string with <c>tokenizebyseparator</c> on every getEntry/reload; we do the same eagerly into a
/// list. <see cref="Filter"/> is accepted for contract-compatibility but ignored, exactly like the QC
/// (its reload returns the token count regardless of filter — soundlist/playlist filter elsewhere).
/// </summary>
public class StringSource : DataSource
{
    private readonly string _sep;
    private List<string> _tokens = new();

    /// <param name="str">The delimited source string (QC StringSource_str).</param>
    /// <param name="sep">The separator chars passed to tokenizebyseparator (QC StringSource_sep).</param>
    public StringSource(string? str, string sep)
    {
        _sep = sep ?? "";
        Str = str;
        _tokens = TokenizeBySeparator(str, _sep);
    }

    /// <summary>The current source string (QC StringSource_str). Subclasses (CvarStringSource) refresh it before each read.</summary>
    protected string? Str { get; set; }

    /// <summary>Re-tokenize from <see cref="Str"/> using the configured separator. Called after Str is refreshed.</summary>
    protected void Retokenize() => _tokens = TokenizeBySeparator(Str, _sep);

    // datasource.qc:9-16 — out of bounds → false; otherwise return argv(i), no icon.
    public override bool TryGetEntry(int i, out DataSourceEntry entry)
    {
        if (i < 0 || i >= _tokens.Count)
        {
            entry = default;
            return false;
        }
        entry = new DataSourceEntry(_tokens[i], null);
        return true;
    }

    // datasource.qc:17-20 — reload returns the token count (filter is unused here, faithful to QC).
    public override int Reload(string? filter)
    {
        Retokenize();
        return _tokens.Count;
    }

    /// <summary>First token equal to <paramref name="find"/>, or -1. (QC base indexOf is a no-op; the list backends scan their own buffers — this convenience mirrors that intent for string lists.)</summary>
    public override int IndexOf(string find)
    {
        for (int i = 0; i < _tokens.Count; ++i)
            if (_tokens[i] == find)
                return i;
        return -1;
    }

    /// <summary>
    /// Port of DP <c>tokenizebyseparator</c>: split <paramref name="s"/> wherever a separator from
    /// <paramref name="sep"/> appears. Like the engine builtin, the separator string is a SET of
    /// single-char separators (each char of <paramref name="sep"/> is tried), empty tokens are KEPT
    /// (so "a::b" with ":" → "a","","b"), and a null/empty source yields no tokens.
    /// </summary>
    internal static List<string> TokenizeBySeparator(string? s, string sep)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(s))
            return result;
        if (string.IsNullOrEmpty(sep))
        {
            result.Add(s);
            return result;
        }

        int start = 0;
        for (int i = 0; i < s.Length; ++i)
        {
            if (sep.IndexOf(s[i]) >= 0)
            {
                result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(s.Substring(start));
        return result;
    }
}

/// <summary>
/// A <see cref="StringSource"/> that re-reads its string from a cvar on every access — C# successor to QC
/// <c>CLASS(CvarStringSource, StringSource)</c> (datasource.qh:29-34 / datasource.qc:22-39). The QC reads
/// <c>cvar_string(cvar)</c> into StringSource_str before delegating to the base getEntry/reload; we do the
/// same via an injected cvar reader (so this stays Godot-free). This is the live music-playlist source
/// (<c>music_playlist_list0</c>) — the UI re-reads it each draw and it always reflects the current cvar.
/// </summary>
public class CvarStringSource : StringSource
{
    private readonly string _cvar;
    private readonly Func<string, string?> _cvarString;

    /// <param name="cvar">The cvar name to read (QC CvarStringSource_cvar).</param>
    /// <param name="sep">The separator passed to tokenizebyseparator.</param>
    /// <param name="cvarString">Reads a cvar's current string value (the menu host wires this to the cvar store).</param>
    public CvarStringSource(string cvar, string sep, Func<string, string?> cvarString)
        : base(null, sep)
    {
        _cvar = cvar ?? "";
        _cvarString = cvarString ?? throw new ArgumentNullException(nameof(cvarString));
        RefreshFromCvar();
        Retokenize();
    }

    // datasource.qc:30-31 / 36-37 — s = cvar ? cvar_string(cvar) : string_null; before the base call.
    private void RefreshFromCvar()
        => Str = _cvar.Length > 0 ? _cvarString(_cvar) : null;

    public override bool TryGetEntry(int i, out DataSourceEntry entry)
    {
        RefreshFromCvar();
        Retokenize();
        return base.TryGetEntry(i, out entry);
    }

    public override int Reload(string? filter)
    {
        RefreshFromCvar();
        return base.Reload(filter);
    }
}

/// <summary>
/// File-enumeration seam used by the file-scan list backends — the C# stand-in for the engine's
/// <c>search_begin</c> / <c>search_getfilename</c> (datasource-free, but mirrors the same "scan the VFS
/// for matching paths" intent). The menu host adapts this to the Godot-mounted asset VFS
/// (<c>MenuState.Vfs.Find</c>); tests supply a fake. Returns full virtual paths (e.g.
/// <c>"screenshots/foo.jpg"</c>).
/// </summary>
public interface IFileEnumerator
{
    /// <summary>
    /// Every file whose path starts with <paramref name="prefix"/> and ends with <paramref name="extension"/>
    /// (extension with or without a leading dot; null/empty = any). Mirrors <c>VirtualFileSystem.Find</c>.
    /// </summary>
    IEnumerable<string> Find(string prefix, string? extension = null);
}

/// <summary>
/// Enumerates <c>screenshots/*.{jpg,tga,png}</c> (one subdirectory level too) — C# successor to QC
/// <c>screenshotlist.qc</c> (getScreenshots / getScreenshots_for_ext, 32-85). Strips the
/// <c>"screenshots/"</c> prefix and the extension, decolorizes the stem (QC <c>strdecolorize</c>), applies
/// the optional filter, sorts case-insensitively (QC <c>buf_sort</c>), and lists each name once. The
/// per-subdir color tagging the QC adds (a leading "/" + a skin color) is left to the renderer; the data
/// here is the plain decolorized stem the QC stores in the buffer.
/// </summary>
public sealed class ScreenshotSource : DataSource
{
    // QC scans .jpg, then .tga, then .png (screenshotlist.qc:79-81).
    private static readonly string[] Extensions = { ".jpg", ".tga", ".png" };
    private const string Prefix = "screenshots/";

    private readonly IFileEnumerator _files;
    private readonly List<string> _names = new();

    public ScreenshotSource(IFileEnumerator files)
        => _files = files ?? throw new ArgumentNullException(nameof(files));

    public override bool TryGetEntry(int i, out DataSourceEntry entry)
    {
        if (i < 0 || i >= _names.Count)
        {
            entry = default;
            return false;
        }
        entry = new DataSourceEntry(_names[i], null);
        return true;
    }

    public override int IndexOf(string find)
    {
        for (int i = 0; i < _names.Count; ++i)
            if (_names[i] == find)
                return i;
        return -1;
    }

    // screenshotlist.qc:69-85 — clear, scan jpg/tga/png, then sort.
    public override int Reload(string? filter)
    {
        _names.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string ext in Extensions)
        {
            foreach (string path in _files.Find(Prefix, ext))
            {
                // "screenshots/foo.jpg" → strip "screenshots/" prefix and the ".<ext>" suffix
                // (QC: substring(s, 12, strlen(s) - 12 - 4); 12 == strlen("screenshots/")).
                string stem = path.Substring(Prefix.Length, path.Length - Prefix.Length - ext.Length);
                stem = MenuTextFormat.Decolorize(stem); // QC strdecolorize

                if (filter != null && filter.Length > 0 && !MatchesFilter(stem, filter))
                    continue;
                if (seen.Add(stem))
                    _names.Add(stem);
            }
        }

        // QC buf_sort(…, false): case-insensitive ascending (screenshotlist.qc:84).
        _names.Sort(StringComparer.OrdinalIgnoreCase);
        return _names.Count;
    }

    /// <summary>The decolorized stems currently loaded (renderer / tests).</summary>
    public IReadOnlyList<string> Names => _names;

    // The QC filterString is a glob ("*foo*"/"foo?"); reproduce the wildcard contains-match the UI builds.
    private static bool MatchesFilter(string name, string filter)
        => MenuTextFormat.GlobMatch(name, filter);
}

/// <summary>
/// Enumerates <c>sound/cdtracks/*.ogg</c> — C# successor to QC <c>soundlist.qc</c>
/// (getSounds / soundName, 19-43). Strips the <c>"sound/cdtracks/"</c> prefix and the <c>".ogg"</c>
/// suffix and lists each track stem. (The QC keeps results in engine search order, unsorted; we match
/// that, only de-duplicating across mounts.) The <c>[C]</c>/<c>[D]</c> current/default markers the QC
/// draws are a render concern (they compare against <c>menu_cdtrack</c>), not stored here.
/// </summary>
public sealed class SoundSource : DataSource
{
    private const string Prefix = "sound/cdtracks/";
    private const string Extension = ".ogg";

    private readonly IFileEnumerator _files;
    private readonly List<string> _names = new();

    public SoundSource(IFileEnumerator files)
        => _files = files ?? throw new ArgumentNullException(nameof(files));

    public override bool TryGetEntry(int i, out DataSourceEntry entry)
    {
        if (i < 0 || i >= _names.Count)
        {
            entry = default;
            return false;
        }
        entry = new DataSourceEntry(_names[i], null);
        return true;
    }

    public override int IndexOf(string find)
    {
        for (int i = 0; i < _names.Count; ++i)
            if (_names[i] == find)
                return i;
        return -1;
    }

    // soundlist.qc:27-43 — scan sound/cdtracks/*.ogg (filterString allows a substring), keep search order.
    public override int Reload(string? filter)
    {
        _names.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in _files.Find(Prefix, Extension))
        {
            // "sound/cdtracks/foo.ogg" → strip prefix + ".ogg" (QC substring(s, 15, strlen(s) - 15 - 4)).
            string stem = path.Substring(Prefix.Length, path.Length - Prefix.Length - Extension.Length);

            // QC builds the search pattern "*<filterString>*.ogg", i.e. a plain substring match.
            if (filter != null && filter.Length > 0 &&
                stem.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (seen.Add(stem))
                _names.Add(stem);
        }
        return _names.Count;
    }

    /// <summary>The track stems currently loaded (renderer / tests).</summary>
    public IReadOnlyList<string> Names => _names;
}

/// <summary>
/// One menu-skin entry parsed from a <c>gfx/menu/&lt;name&gt;/skinvalues.txt</c> — C# successor to the per-skin
/// buffer slots QC <c>skinlist.qc</c> stores (NAME / TITLE / AUTHOR / PREVIEW, the SKINPARM_* constants 3-7).
/// </summary>
public sealed class SkinInfo
{
    public string Name = "";     // SKINPARM_NAME — the directory name, the value written to menu_skin
    public string Title = "";    // SKINPARM_TITLE — "title " line, or "<TITLE>" if absent
    public string Author = "";   // SKINPARM_AUTHOR — "author " line, or "<AUTHOR>" if absent
    public string Preview = "";  // SKINPARM_PREVIEW — preview image vpath, or "nopreview_menuskin"
}

/// <summary>
/// Enumerates menu skins from <c>gfx/menu/*/skinvalues.txt</c> — C# successor to QC <c>skinlist.qc</c>
/// (getSkins / skinParameter / loadCvars / saveCvars, 9-101). For each skinvalues.txt it derives the skin
/// NAME from the directory, reads the <c>title</c> / <c>author</c> lines, and resolves the preview image
/// (or the <c>nopreview_menuskin</c> fallback). The current selection (<c>menu_skin</c>) and the apply
/// (<c>menu_skin</c> + <c>menu_restart</c>) are driven by the host dialog, not stored here.
/// </summary>
public sealed class SkinSource : DataSource
{
    private const string Prefix = "gfx/menu/";
    private const string Suffix = "/skinvalues.txt";

    private readonly IFileEnumerator _files;
    private readonly Func<string, string?> _readText; // vpath → text, or null if absent
    private readonly Func<string, bool> _imageExists;  // vpath → does a "skinpreview" image exist
    private readonly List<SkinInfo> _skins = new();

    /// <param name="defaultSkin">cvar_defstring("menu_skin") — its title gets a "(Default)" tag (skinlist.qc:86).</param>
    public SkinSource(IFileEnumerator files, Func<string, string?> readText, Func<string, bool> imageExists, string defaultSkin = "default")
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _imageExists = imageExists ?? (_ => false);
        DefaultSkin = defaultSkin ?? "";
    }

    /// <summary>The default skin name (used for the "(Default)" title tag, skinlist.qc:86-87).</summary>
    public string DefaultSkin { get; }

    public override bool TryGetEntry(int i, out DataSourceEntry entry)
    {
        if (i < 0 || i >= _skins.Count)
        {
            entry = default;
            return false;
        }
        SkinInfo s = _skins[i];
        entry = new DataSourceEntry(s.Title, s.Preview);
        return true;
    }

    // skinlist.qc:loadCvars (23-37) finds the row whose NAME == menu_skin.
    public override int IndexOf(string find)
    {
        for (int i = 0; i < _skins.Count; ++i)
            if (_skins[i].Name == find)
                return i;
        return -1;
    }

    // skinlist.qc:getSkins (49-101) — scan gfx/menu/*/skinvalues.txt, parse title/author/preview.
    public override int Reload(string? filter)
    {
        _skins.Clear();

        foreach (string path in _files.Find(Prefix, "txt"))
        {
            // accept only ".../skinvalues.txt"
            if (!path.EndsWith(Suffix, StringComparison.Ordinal))
                continue;

            // "gfx/menu/<name>/skinvalues.txt" → the <name> part (QC substring(s, 9, strlen(s) - 24);
            // 9 == strlen("gfx/menu/"), 24 == strlen("gfx/menu/") + strlen("/skinvalues.txt") == 9 + 15).
            string name = path.Substring(Prefix.Length, path.Length - Prefix.Length - Suffix.Length);
            if (name.Length == 0)
                continue;

            var info = new SkinInfo
            {
                Name = name,
                Title = "<TITLE>",   // QC _("<TITLE>")
                Author = "<AUTHOR>", // QC _("<AUTHOR>")
            };

            // Preview: "/gfx/menu/<name>/skinpreview" if it exists, else "nopreview_menuskin" (skinlist.qc:71-74).
            string previewPath = "/gfx/menu/" + name + "/skinpreview";
            info.Preview = _imageExists(previewPath) ? previewPath : "nopreview_menuskin";

            string? text = _readText(path);
            if (text != null)
            {
                foreach (string line in SplitLines(text))
                {
                    // skinlist.qc:84-92 — "title " (6) and "author " (7) prefixes.
                    if (line.StartsWith("title ", StringComparison.Ordinal))
                    {
                        string title = line.Substring(6);
                        info.Title = (name == DefaultSkin)
                            ? title + " (Default)" // QC strcat(title, " (", _("Default"), ")")
                            : title;
                    }
                    else if (line.StartsWith("author ", StringComparison.Ordinal))
                    {
                        info.Author = line.Substring(7);
                    }
                }
            }

            _skins.Add(info);
        }

        return _skins.Count;
    }

    /// <summary>The parsed skins currently loaded (renderer / tests).</summary>
    public IReadOnlyList<SkinInfo> Skins => _skins;

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
