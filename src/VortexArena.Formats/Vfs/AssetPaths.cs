using System;

namespace XonoticGodot.Formats.Vfs;

/// <summary>
/// Pure-string helpers for Quake/Darkplaces virtual paths ("qpaths").
///
/// A virtual path is the name an asset is referenced by inside a gamedir: forward-slash
/// separated, case-insensitive, rooted at the mount (e.g. <c>"models/player/foo.iqm"</c>,
/// <c>"scripts/x.shader"</c>, <c>"textures/exomorph/exo_floor"</c>). On disk these resolve
/// either to a zip entry inside a <c>.pk3</c> or to a loose file under a <c>.pk3dir</c>/gamedir.
///
/// Darkplaces stores and compares pack entry names already lowercased and slash-normalized
/// (see <c>FS_LoadPackPK3</c> / <c>FS_FindFile</c> binary search with <c>ignorecase</c>), and
/// the engine's <c>FS_SanitizePath</c> collapses back-slashes to forward-slashes. We mirror
/// that here so a single canonical key is used everywhere as the index lookup key.
/// </summary>
public static class AssetPaths
{
    /// <summary>
    /// Canonicalizes a virtual path to the form used as the index key:
    /// lower-cased (invariant), back-slashes → forward-slashes, duplicate slashes collapsed,
    /// any leading <c>"./"</c> / <c>"/"</c> stripped, and <c>"."</c> path segments removed.
    ///
    /// Returns "" for null/blank/"."—a path that can never name a file—so callers can early-out.
    /// Note: ".." segments are NOT resolved here; callers that care about traversal should reject
    /// them (the VFS never produces them because pack/dir indexes only hold real descendant paths).
    /// </summary>
    public static string Normalize(string? vpath)
    {
        if (string.IsNullOrEmpty(vpath))
            return string.Empty;

        Span<char> buf = vpath.Length <= 512 ? stackalloc char[vpath.Length] : new char[vpath.Length];
        int n = 0;
        bool atSegmentStart = true; // start of the whole string, or just after a written '/'

        for (int i = 0; i < vpath.Length; i++)
        {
            char c = vpath[i];
            if (c == '\\') c = '/';

            if (c == '/')
            {
                // Collapse runs of slashes and drop a leading slash: only emit a separator
                // when we already have real content to separate.
                if (n > 0 && buf[n - 1] != '/')
                    buf[n++] = '/';
                atSegmentStart = true;
                continue;
            }

            // Drop a "." that constitutes an entire path segment (leading "./", "/./", trailing "/.").
            if (c == '.' && atSegmentStart)
            {
                char next = i + 1 < vpath.Length ? vpath[i + 1] : '\0';
                if (next == '/' || next == '\\' || next == '\0')
                {
                    // Skip the '.'; the following '/' (if any) is handled by the slash branch,
                    // and we remain at a segment start.
                    continue;
                }
            }

            buf[n++] = char.ToLowerInvariant(c);
            atSegmentStart = false;
        }

        // Trim a trailing slash (a directory-style key is never a file).
        if (n > 0 && buf[n - 1] == '/')
            n--;

        return n == 0 ? string.Empty : new string(buf[..n]);
    }

    /// <summary>
    /// Returns the lower-cased extension WITHOUT the dot (e.g. <c>"tga"</c>), or "" if none.
    /// Matches Darkplaces <c>FS_FileExtension</c>: the part after the last '.' that follows the
    /// last path separator (so <c>"a.b/c"</c> has no extension, and a leading-dot file like
    /// <c>".gitignore"</c> is treated as having no extension).
    /// </summary>
    public static string GetExtension(string vpath)
    {
        if (string.IsNullOrEmpty(vpath))
            return string.Empty;

        int lastSlash = LastSeparator(vpath);
        int dot = vpath.LastIndexOf('.');
        // The dot must come after the last separator (so "a.b/c" has no ext) and must not be the
        // very first character of the file name (so ".gitignore" has no ext).
        if (dot <= lastSlash + 1)
            return string.Empty;
        // A trailing dot ("foo.") names an empty extension.
        if (dot == vpath.Length - 1)
            return string.Empty;

        return vpath[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// Returns the path with its final extension removed (the dot too), mirroring
    /// Darkplaces <c>FS_StripExtension</c>. <c>"foo/bar.tga"</c> → <c>"foo/bar"</c>;
    /// a name with no extension is returned unchanged.
    /// </summary>
    public static string StripExtension(string vpath)
    {
        if (string.IsNullOrEmpty(vpath))
            return vpath ?? string.Empty;

        int lastSlash = LastSeparator(vpath);
        int dot = vpath.LastIndexOf('.');
        if (dot <= lastSlash + 1)
            return vpath; // no extension, or leading-dot file name
        return vpath[..dot];
    }

    /// <summary>
    /// Replaces (or appends) the final extension. <paramref name="newExtension"/> may be given
    /// with or without a leading dot. Passing "" yields the extension-stripped path.
    /// </summary>
    public static string ChangeExtension(string vpath, string newExtension)
    {
        string stem = StripExtension(vpath);
        if (string.IsNullOrEmpty(newExtension))
            return stem;
        return newExtension[0] == '.' ? stem + newExtension : stem + "." + newExtension;
    }

    /// <summary>
    /// Mirrors Darkplaces <c>Image_StripImageExtension</c>: strips the extension ONLY when it is a
    /// known raster image extension (tga/png/jpg/pcx/wal/lmp), otherwise leaves the name intact.
    /// This is what lets a shader reference an extension-agnostic texture name while a name that
    /// merely contains a dot (e.g. <c>"env/sky_1.5"</c>) is left alone.
    /// </summary>
    public static string StripImageExtension(string vpath)
    {
        string ext = GetExtension(vpath);
        return ext is "tga" or "png" or "jpg" or "jpeg" or "pcx" or "wal" or "lmp" or "dds"
            ? StripExtension(vpath)
            : vpath;
    }

    private static int LastSeparator(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
            if (s[i] == '/' || s[i] == '\\')
                return i;
        return -1;
    }
}
