using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace XonoticGodot.Formats.Vfs;

/// <summary>
/// A read-only virtual filesystem that reproduces the Darkplaces gamedir search path over
/// Xonotic's <c>.pk3</c> (zip) archives and <c>.pk3dir</c> (loose-directory) mounts.
///
/// <para><b>Model.</b> Each call to <see cref="Mount(string)"/> pushes one "search path" onto the
/// front of an ordered list. Lookups walk the list front-to-back and the first hit wins, so
/// <b>later mounts take precedence over earlier ones</b> — exactly Darkplaces' <c>fs_searchpaths</c>
/// (which prepends, so the head is the most-recently-added; see <c>FS_FindFile</c> /
/// <c>FS_AddPack_Fullpath</c> in <c>fs.c</c>). <see cref="MountGameDir(string)"/> reproduces
/// <c>FS_AddGameDirectory</c>: it mounts the <c>.pk3</c>/<c>.pk3dir</c> archives inside a base dir
/// first (sorted by name), then the base dir itself on top — so a loose file in the gamedir beats
/// the same path inside a pk3, and a pk3 later in sort order beats an earlier one.</para>
///
/// <para><b>Paths.</b> Virtual paths use forward slashes and are case-insensitive, rooted at the
/// gamedir (e.g. <c>"models/player/foo.iqm"</c>, <c>"scripts/x.shader"</c>). They are canonicalized
/// via <see cref="AssetPaths.Normalize(string?)"/> for both indexing and lookup.</para>
///
/// <para><b>Thread-safety.</b> Mounts are expected to happen during startup; reads are heavy and
/// concurrent. The search-path list is swapped atomically on each mount and never mutated in place,
/// so readers always see a consistent snapshot. Each mount's file index is built once at mount time
/// and is immutable thereafter. Zip access is serialized per mount because a single
/// <see cref="ZipArchive"/> / its underlying stream is not safe for concurrent reads.</para>
/// </summary>
public sealed class VirtualFileSystem : IDisposable
{
    // Immutable snapshot of the search path: index 0 = highest priority (last mounted).
    // Replaced wholesale on every mount; never mutated in place. Readers grab the reference once.
    private volatile IReadOnlyList<IMount> _mounts = Array.Empty<IMount>();
    private readonly object _mountLock = new();
    private bool _disposed;

    // Resolved-path + negative lookup caches for the two hot extension-search paths (A4). Exists() linearly
    // probes every mount, and ResolveImage() probes up to 11 candidate vpaths × every mount per call — many
    // of which MISS by design (the _norm/_gloss/_glow/_reflect material-companion probes), repeating the full
    // scan every time. These cache the result (a null/false value is a cached MISS so a known-absent name is
    // never re-scanned), keyed by the normalized vpath (Exists) / stem (ResolveImage). Mounts happen at startup
    // while reads are hot + concurrent thereafter, so a plain ConcurrentDictionary (lock-free reads) cleared on
    // every mount change is sufficient and matches the class's stated threading model.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _existsCache = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _resolveImageCache = new(StringComparer.Ordinal);

    /// <summary>Mount paths in priority order, highest first. Mainly for diagnostics/logging.</summary>
    public IReadOnlyList<string> MountedPaths => _mounts.Select(m => m.SourcePath).ToList();

    // ---------------------------------------------------------------------------------------------
    // Mounting
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mounts a single archive or directory and gives it priority over everything mounted before it.
    /// A path ending in <c>.pk3</c>/<c>.pk3dir</c>/<c>.zip</c>/<c>.dpk</c>/<c>.dpkdir</c> is detected
    /// by what it is on disk: a real directory is mounted as a loose tree, a file is opened as a zip.
    /// </summary>
    /// <returns>True if mounted; false if the path does not exist.</returns>
    public bool Mount(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        IMount mount;
        if (Directory.Exists(path))
            mount = new DirectoryMount(path);
        else if (File.Exists(path))
            mount = new Pk3Mount(path);
        else
            return false;

        Prepend(mount);
        return true;
    }

    /// <summary>
    /// Mounts a base game directory and every pack inside it, reproducing
    /// <c>FS_AddGameDirectory</c>: the <c>.pk3</c>/<c>.pk3dir</c> archives directly inside
    /// <paramref name="dir"/> are mounted first in case-insensitive name order, then
    /// <paramref name="dir"/> itself is mounted on top so loose files win. The net priority
    /// (high → low) is: loose files in <paramref name="dir"/> &gt; last pack (by name) &gt; … &gt;
    /// first pack &gt; (whatever was mounted before this call).
    /// </summary>
    /// <returns>True if the directory exists and was mounted.</returns>
    public bool MountGameDir(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        if (!Directory.Exists(dir))
            return false;

        // Gather the pack entries (files AND directories) that look like packs, sorted by name.
        // Darkplaces sorts the raw directory listing ascending, then adds .pak before .pk3/.pk3dir;
        // we collapse to a single ordinal-ignore-case sort which matches DP for the .pk3 set Xonotic
        // actually ships (there are no .pak files in a Xonotic install).
        var entries = new List<string>();

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string ext = AssetPaths.GetExtension(sub);
            if (ext is "pk3dir" or "dpkdir")
                entries.Add(sub);
        }
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string ext = AssetPaths.GetExtension(file);
            if (ext is "pk3" or "pak" or "dpk" or "obb")
                entries.Add(file);
        }

        entries.Sort(static (a, b) => string.Compare(
            Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

        // Mount packs lowest-first so later-sorted packs end up higher in the search path,
        // then the plain directory on top (loose files have priority over packed files).
        var built = new List<IMount>(entries.Count + 1);
        foreach (string entry in entries)
        {
            try
            {
                built.Add(Directory.Exists(entry) ? new DirectoryMount(entry) : new Pk3Mount(entry));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A corrupt/locked pack must not abort the whole gamedir mount; skip it like DP,
                // which logs "unable to load pak" and continues.
            }
        }
        built.Add(new DirectoryMount(dir));

        // Prepend the whole batch atomically, preserving relative order (last element = top).
        PrependRange(built);
        return true;
    }

    private void Prepend(IMount mount)
    {
        lock (_mountLock)
        {
            var next = new List<IMount>(_mounts.Count + 1) { mount };
            next.AddRange(_mounts);
            _mounts = next;
            ClearLookupCaches(); // a new mount can satisfy a previously-cached MISS — invalidate
        }
    }

    /// <summary>
    /// Prepend a batch where the LAST item of <paramref name="batch"/> becomes the highest priority,
    /// matching the order in which <see cref="MountGameDir(string)"/> appends (packs, then the dir).
    /// </summary>
    private void PrependRange(IReadOnlyList<IMount> batch)
    {
        if (batch.Count == 0)
            return;
        lock (_mountLock)
        {
            var next = new List<IMount>(batch.Count + _mounts.Count);
            for (int i = batch.Count - 1; i >= 0; i--) // reverse: last appended = front = top priority
                next.Add(batch[i]);
            next.AddRange(_mounts);
            _mounts = next;
            ClearLookupCaches(); // new mounts can satisfy previously-cached MISSes — invalidate
        }
    }

    /// <summary>Drop the resolved-path + negative lookup caches (A4). Called under <see cref="_mountLock"/>
    /// whenever the mount set changes, so a new mount can satisfy a previously-cached miss.</summary>
    private void ClearLookupCaches()
    {
        _existsCache.Clear();
        _resolveImageCache.Clear();
    }

    // ---------------------------------------------------------------------------------------------
    // Lookup / read
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns true if <paramref name="vpath"/> resolves to a file in any mount.</summary>
    public bool Exists(string vpath)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            return false;
        if (_existsCache.TryGetValue(key, out bool cached))
            return cached;
        bool found = false;
        foreach (IMount m in _mounts)
            if (m.Contains(key)) { found = true; break; }
        _existsCache[key] = found; // cache hits AND misses (the hot _norm/_gloss probes miss repeatedly)
        return found;
    }

    /// <summary>
    /// Reads the highest-priority occurrence of <paramref name="vpath"/> as raw bytes.
    /// Throws <see cref="AssetParseException"/> if no mount contains the path, or if the underlying
    /// archive/file read fails (so callers can skip a bad asset instead of crashing).
    /// </summary>
    public byte[] ReadBytes(string vpath)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            throw new AssetParseException($"Invalid (empty) virtual path: \"{vpath}\".");

        foreach (IMount m in _mounts)
        {
            if (!m.Contains(key))
                continue;
            try
            {
                return m.ReadBytes(key);
            }
            catch (Exception ex) when (ex is not AssetParseException)
            {
                throw new AssetParseException(
                    $"Failed to read \"{key}\" from mount \"{m.SourcePath}\": {ex.Message}", ex);
            }
        }

        throw new AssetParseException($"Asset not found in any mount: \"{key}\".");
    }

    /// <summary>
    /// (perf 2026-07-03) Like <see cref="ReadBytes"/> but into a caller-owned, GROW-ONLY buffer: the buffer is
    /// (re)allocated only when too small, and the return value is the byte count actually read — the buffer's
    /// tail beyond it is stale. Exists for the texture/model streaming path, whose per-file `new byte[]`
    /// (4-16 MB per uncompressed TGA / mip-chained DDS, ~11 textures per player model) was the dominant
    /// LOH churn behind the 130-430 MB single-frame allocation storms → gen2 collections at load/join.
    /// Same resolution + error semantics as <see cref="ReadBytes"/>.
    /// </summary>
    public int ReadBytesInto(string vpath, ref byte[]? buffer)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            throw new AssetParseException($"Invalid (empty) virtual path: \"{vpath}\".");

        foreach (IMount m in _mounts)
        {
            if (!m.Contains(key))
                continue;
            try
            {
                return m.ReadBytesInto(key, ref buffer);
            }
            catch (Exception ex) when (ex is not AssetParseException)
            {
                throw new AssetParseException(
                    $"Failed to read \"{key}\" from mount \"{m.SourcePath}\": {ex.Message}", ex);
            }
        }

        throw new AssetParseException($"Asset not found in any mount: \"{key}\".");
    }

    /// <summary>Grow-only capacity helper for <see cref="ReadBytesInto"/> buffers.</summary>
    private static void EnsureCapacity(ref byte[]? buffer, int length)
    {
        if (buffer is null || buffer.Length < length)
            buffer = new byte[length];
    }

    /// <summary>Reads the file as UTF-8 text (BOM-aware). Used for <c>.shader</c>, entity lumps, etc.</summary>
    public string ReadText(string vpath)
    {
        byte[] bytes = ReadBytes(vpath);
        return DecodeText(bytes);
    }

    /// <summary>
    /// Opens the highest-priority occurrence as a readable, seekable stream over an in-memory copy
    /// of the bytes. (The bytes are materialized so the caller owns an independent, thread-safe
    /// stream and the underlying archive stays serialized internally.)
    /// Throws <see cref="AssetParseException"/> when the path is missing, same as <see cref="ReadBytes"/>.
    /// </summary>
    public Stream Open(string vpath)
    {
        byte[] bytes = ReadBytes(vpath);
        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>
    /// Enumerates the distinct virtual paths whose path starts with <paramref name="prefix"/> and
    /// (when <paramref name="extension"/> is given) end with that extension — e.g.
    /// <c>Find("scripts/", "shader")</c> for every shader, <c>Find("maps/", "bsp")</c> for every map.
    ///
    /// Shadowing is honored: a path that exists in several mounts is yielded once, but the result
    /// set is the union across mounts (so a map shipped in its own pk3 still shows up alongside
    /// gamedir content). <paramref name="prefix"/> "" enumerates everything. The <paramref name="extension"/>
    /// may be given with or without a leading dot; null/empty means "any extension".
    /// </summary>
    public IEnumerable<string> Find(string prefix, string? extension = null)
    {
        string normPrefix = NormalizePrefix(prefix);
        string? ext = NormalizeExtFilter(extension);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IMount m in _mounts)
        {
            foreach (string key in m.Keys)
            {
                if (normPrefix.Length != 0 && !key.StartsWith(normPrefix, StringComparison.Ordinal))
                    continue;
                if (ext != null && !KeyHasExtension(key, ext))
                    continue;
                if (seen.Add(key))
                    yield return key;
            }
        }
    }

    /// <summary>
    /// Resolves an extension-agnostic texture base name to a concrete virtual path, reproducing the
    /// Darkplaces image extension-search precedence (<c>loadimagepixelsbgra</c> /
    /// <c>imageformats_*</c> in <c>image.c</c>) plus the DDS variants Xonotic ships:
    /// <list type="number">
    ///   <item><c>override/&lt;name&gt;.tga</c>, <c>override/&lt;name&gt;.png</c>, <c>override/&lt;name&gt;.jpg</c></item>
    ///   <item><c>&lt;name&gt;.tga</c>, <c>.png</c>, <c>.jpg</c></item>
    ///   <item><c>dds/&lt;name&gt;.dds</c>, <c>&lt;name&gt;.dds</c>, <c>&lt;name&gt;.tga.dds</c></item>
    ///   <item><c>&lt;name&gt;.pcx</c>, <c>&lt;name&gt;.wal</c></item>
    /// </list>
    /// The <c>override/</c> directory always wins (that's its whole purpose), then the normal raster
    /// formats in DP's order, then the precompressed DDS forms, then the legacy fallbacks. Any
    /// extension already on <paramref name="baseNameNoExt"/> is stripped first (only if it's a known
    /// image extension, per <c>Image_StripImageExtension</c>), so passing
    /// <c>"textures/foo.tga"</c> or <c>"textures/foo"</c> behaves identically.
    /// Returns the first existing vpath, or <c>null</c> if none of the candidates exist.
    /// </summary>
    public string? ResolveImage(string baseNameNoExt)
    {
        if (string.IsNullOrEmpty(baseNameNoExt))
            return null;

        // Strip a trailing image extension if present, then canonicalize once.
        string stem = AssetPaths.Normalize(AssetPaths.StripImageExtension(baseNameNoExt));
        if (stem.Length == 0)
            return null;
        if (_resolveImageCache.TryGetValue(stem, out string? cachedPath))
            return cachedPath; // a cached null is a known MISS (avoids re-probing 11 candidates × mounts)

        string? resolved = null;
        foreach (string candidate in ImageCandidates(stem))
        {
            foreach (IMount m in _mounts)
            {
                if (m.Contains(candidate)) { resolved = candidate; break; }
            }
            if (resolved != null)
                break;
        }
        _resolveImageCache[stem] = resolved;
        return resolved;
    }

    /// <summary>The ordered candidate vpaths <see cref="ResolveImage"/> probes, for a normalized stem.</summary>
    private static IEnumerable<string> ImageCandidates(string stem)
    {
        // override/ takes absolute priority (DP imageformats_other / _textures lead with it).
        yield return "override/" + stem + ".tga";
        yield return "override/" + stem + ".png";
        yield return "override/" + stem + ".jpg";

        // Normal raster formats, DP order: tga, png, jpg.
        yield return stem + ".tga";
        yield return stem + ".png";
        yield return stem + ".jpg";

        // Precompressed DDS forms Xonotic uses (dds/ cache dir, bare .dds, and the ".tga.dds"
        // convention where DDS is appended to the original extension — see gl_textures.c).
        yield return "dds/" + stem + ".dds";
        yield return stem + ".dds";
        yield return stem + ".tga.dds";

        // DP imageformats_other: a bare, un-pathed model-shader name (e.g. a_shells.md3's "Box01"
        // surface, shader "shellsammo", which has no .shader entry) is also searched under
        // textures/<name> — that is where the real file lives (textures/shellsammo.tga). Only for stems
        // not already rooted at a known asset dir, to avoid a double textures/textures/… probe.
        // (playtest-bugs #2)
        if (!stem.StartsWith("textures/", System.StringComparison.Ordinal)
            && !stem.StartsWith("gfx/", System.StringComparison.Ordinal)
            && !stem.StartsWith("locale/", System.StringComparison.Ordinal))
        {
            yield return "textures/" + stem + ".tga";
            yield return "textures/" + stem + ".png";
            yield return "textures/" + stem + ".jpg";
            yield return "dds/textures/" + stem + ".dds";
        }

        // Legacy fallbacks.
        yield return stem + ".pcx";
        yield return stem + ".wal";
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return string.Empty;
        // Normalize like a vpath, but a trailing slash is meaningful for a directory prefix:
        // "scripts/" should not match "scripts_old/x". Normalize() strips the trailing slash, so
        // re-add it when the caller asked for a directory boundary.
        bool dirBoundary = prefix[^1] is '/' or '\\';
        string norm = AssetPaths.Normalize(prefix);
        if (dirBoundary && norm.Length != 0)
            norm += "/";
        return norm;
    }

    private static string? NormalizeExtFilter(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;
        string e = extension[0] == '.' ? extension[1..] : extension;
        return e.Length == 0 ? null : e.ToLowerInvariant();
    }

    private static bool KeyHasExtension(string key, string lowerExtNoDot)
    {
        // key is already lowercased; compare suffix ".<ext>" and require a real basename before it.
        int need = lowerExtNoDot.Length + 1;
        if (key.Length < need + 1)
            return false;
        if (key[key.Length - need] != '.')
            return false;
        return key.AsSpan(key.Length - lowerExtNoDot.Length).SequenceEqual(lowerExtNoDot);
    }

    private static string DecodeText(byte[] bytes)
    {
        // Honor a UTF-8/UTF-16 BOM if present; otherwise treat as UTF-8 (Xonotic .shader/.cfg are UTF-8).
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_mountLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (IMount m in _mounts)
                m.Dispose();
            _mounts = Array.Empty<IMount>();
        }
    }

    // =============================================================================================
    // Mount implementations
    // =============================================================================================

    /// <summary>One search-path element: an immutable, case-insensitive index of vpath → file.</summary>
    private interface IMount : IDisposable
    {
        /// <summary>The on-disk path this mount was created from (archive file or directory).</summary>
        string SourcePath { get; }

        /// <summary>All normalized vpaths this mount provides.</summary>
        IEnumerable<string> Keys { get; }

        /// <summary><paramref name="key"/> must already be normalized.</summary>
        bool Contains(string key);

        /// <summary>(perf 2026-07-03) Read into a caller-owned, grow-only buffer (see
        /// <see cref="VirtualFileSystem.ReadBytesInto"/>). Returns the byte count actually read.</summary>
        int ReadBytesInto(string key, ref byte[]? buffer);

        /// <summary>Reads the entry; <paramref name="key"/> must already be normalized and present.</summary>
        byte[] ReadBytes(string key);
    }

    /// <summary>A loose directory mount (<c>.pk3dir</c> or a plain gamedir). Files read straight off disk.</summary>
    private sealed class DirectoryMount : IMount
    {
        private readonly string _root;
        // normalized-vpath -> absolute on-disk path (preserves original-case disk name for the OS).
        private readonly Dictionary<string, string> _index;

        public DirectoryMount(string root)
        {
            _root = Path.GetFullPath(root);
            _index = new Dictionary<string, string>(StringComparer.Ordinal);

            // Index every file beneath the root. The vpath is the path relative to root,
            // slash-normalized and lowercased.
            foreach (string full in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(_root, full);
                string key = AssetPaths.Normalize(rel);
                if (key.Length == 0)
                    continue;
                // First writer wins is irrelevant on a case-sensitive FS; on a case-insensitive FS
                // two disk names can normalize to the same key — keep the first (stable enumeration).
                _index.TryAdd(key, full);
            }
        }

        public string SourcePath => _root;
        public IEnumerable<string> Keys => _index.Keys;
        public bool Contains(string key) => _index.ContainsKey(key);

        public byte[] ReadBytes(string key)
        {
            if (!_index.TryGetValue(key, out string? full))
                throw new AssetParseException($"\"{key}\" not present in directory mount \"{_root}\".");
            return File.ReadAllBytes(full);
        }

        public int ReadBytesInto(string key, ref byte[]? buffer)
        {
            if (!_index.TryGetValue(key, out string? full))
                throw new AssetParseException($"\"{key}\" not present in directory mount \"{_root}\".");
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            long len = fs.Length;
            if (len > int.MaxValue)
                throw new AssetParseException($"\"{key}\" is implausibly large ({len} bytes).");
            EnsureCapacity(ref buffer, (int)len);
            fs.ReadExactly(buffer!, 0, (int)len);
            return (int)len;
        }

        public void Dispose() { /* nothing to release */ }
    }

    /// <summary>
    /// A zip-archive mount (<c>.pk3</c>/<c>.zip</c>). The archive is opened once and kept open; the
    /// central directory is indexed up front. <see cref="ZipArchive"/> and its backing stream are not
    /// thread-safe for concurrent reads, so each read is serialized under <see cref="_gate"/>.
    /// </summary>
    private sealed class Pk3Mount : IMount
    {
        private readonly string _path;
        private readonly object _gate = new();
        private FileStream? _stream;
        private ZipArchive? _archive;
        // normalized-vpath -> entry full name (the original entry key into the archive).
        private readonly Dictionary<string, string> _index;
        // normalized-vpath -> normalized target vpath for S_IFLNK (symlink) entries, the product of
        // Xonotic's build-time dedup (symlink-deduplicate.sh). The pk3 stores such an entry with the
        // target path as its body and the Unix S_IFLNK mode in its external attributes; without this a
        // read would return the path-string body instead of the linked file. Built once at mount and
        // immutable thereafter (so concurrent reads are safe); empty for pk3s without symlinks.
        private readonly Dictionary<string, string> _symlinks;

        public Pk3Mount(string path)
        {
            _path = Path.GetFullPath(path);
            _index = new Dictionary<string, string>(StringComparer.Ordinal);
            _symlinks = new Dictionary<string, string>(StringComparer.Ordinal);

            _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            }
            catch
            {
                _stream.Dispose();
                _stream = null;
                throw;
            }

            List<(string key, ZipArchiveEntry entry)>? symlinkEntries = null;
            foreach (ZipArchiveEntry entry in _archive.Entries)
            {
                // A directory entry has an empty Name (full name ends in '/'); skip those.
                if (entry.FullName.Length == 0 || entry.FullName[^1] == '/' || entry.Name.Length == 0)
                    continue;
                string key = AssetPaths.Normalize(entry.FullName); // handles nested-dir entries
                if (key.Length == 0)
                    continue;
                // If the same path appears twice in one zip, the LAST entry wins — that's what unzip
                // tools and Darkplaces' rebuilt sorted table effectively do for a later duplicate.
                _index[key] = entry.FullName;
                if (IsSymlink(entry))
                    (symlinkEntries ??= new List<(string, ZipArchiveEntry)>()).Add((key, entry));
            }

            // Second pass: resolve symlink entries against the now-complete index. A link's body is its
            // target path (relative to the link's directory); register key -> target only when the target
            // is a real entry in THIS pk3. Otherwise the entry is left as a plain file, so behaviour is
            // unchanged for links we can't follow. The target may itself be a symlink — the read-time loop
            // follows the chain.
            if (symlinkEntries != null)
            {
                foreach ((string key, ZipArchiveEntry entry) in symlinkEntries)
                {
                    if (entry.Length is <= 0 or > 4096) // a path target is tiny; never a real file's size
                        continue;
                    string target;
                    try { target = ReadEntryText(entry).Trim(); }
                    catch { continue; }
                    string? targetKey = ResolveSymlinkTarget(key, target);
                    if (targetKey != null && _index.ContainsKey(targetKey))
                        _symlinks[key] = targetKey;
                }
            }
        }

        public string SourcePath => _path;
        public IEnumerable<string> Keys => _index.Keys;
        public bool Contains(string key) => _index.ContainsKey(key);

        public byte[] ReadBytes(string key)
        {
            // Follow symlink redirects (Xonotic dedup) to the real entry, guarding against cycles.
            for (int hops = 0; _symlinks.TryGetValue(key, out string? target); hops++)
            {
                if (hops >= 8)
                    throw new AssetParseException($"symlink chain too deep starting at \"{key}\" in \"{_path}\".");
                key = target;
            }

            if (!_index.TryGetValue(key, out string? entryName))
                throw new AssetParseException($"\"{key}\" not present in pk3 \"{_path}\".");

            lock (_gate)
            {
                ZipArchive archive = _archive
                    ?? throw new AssetParseException($"pk3 \"{_path}\" has been disposed.");
                ZipArchiveEntry? entry = archive.GetEntry(entryName)
                    ?? throw new AssetParseException($"zip entry \"{entryName}\" vanished from \"{_path}\".");

                // ZipArchiveEntry.Length is the uncompressed size; preallocate and fill exactly.
                long len = entry.Length;
                if (len < 0 || len > int.MaxValue)
                    throw new AssetParseException($"zip entry \"{entryName}\" has implausible length {len}.");

                var buffer = new byte[len];
                using Stream es = entry.Open();
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = es.Read(buffer, read, buffer.Length - read);
                    if (n == 0)
                        break;
                    read += n;
                }
                if (read != buffer.Length)
                {
                    // Stored length disagreed with what we could read — return the truncated-but-real bytes.
                    Array.Resize(ref buffer, read);
                }
                return buffer;
            }
        }

        public int ReadBytesInto(string key, ref byte[]? buffer)
        {
            // Follow symlink redirects (Xonotic dedup) to the real entry, guarding against cycles.
            for (int hops = 0; _symlinks.TryGetValue(key, out string? target); hops++)
            {
                if (hops >= 8)
                    throw new AssetParseException($"symlink chain too deep starting at \"{key}\" in \"{_path}\".");
                key = target;
            }

            if (!_index.TryGetValue(key, out string? entryName))
                throw new AssetParseException($"\"{key}\" not present in pk3 \"{_path}\".");

            lock (_gate)
            {
                ZipArchive archive = _archive
                    ?? throw new AssetParseException($"pk3 \"{_path}\" has been disposed.");
                ZipArchiveEntry? entry = archive.GetEntry(entryName)
                    ?? throw new AssetParseException($"zip entry \"{entryName}\" vanished from \"{_path}\".");

                long len = entry.Length;
                if (len < 0 || len > int.MaxValue)
                    throw new AssetParseException($"zip entry \"{entryName}\" has implausible length {len}.");
                EnsureCapacity(ref buffer, (int)len);
                using Stream es = entry.Open();
                int read = 0;
                while (read < (int)len)
                {
                    int n = es.Read(buffer!, read, (int)len - read);
                    if (n == 0)
                        break; // stored length disagreed — the return count reflects the real bytes
                    read += n;
                }
                return read;
            }
        }

        /// <summary>True if a zip entry carries the Unix S_IFLNK mode in its external attributes (a symlink).</summary>
        private static bool IsSymlink(ZipArchiveEntry entry)
            => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000; // S_IFMT mask, S_IFLNK value

        /// <summary>Read a small entry (a symlink's target path) as UTF-8 text.</summary>
        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            using Stream s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Resolve a symlink <paramref name="target"/> (a path relative to the link's directory, possibly
        /// with <c>.</c>/<c>..</c> segments; a leading <c>/</c> roots it at the pk3) to a normalized vpath
        /// key. Returns null when it's empty or escapes the archive root.
        /// </summary>
        private static string? ResolveSymlinkTarget(string linkKey, string target)
        {
            target = target.Replace('\\', '/').Trim();
            if (target.Length == 0)
                return null;

            var segments = new List<string>();
            if (target[0] != '/') // relative: start from the link's own directory
            {
                int lastSlash = linkKey.LastIndexOf('/');
                if (lastSlash > 0)
                    segments.AddRange(linkKey[..lastSlash].Split('/'));
            }

            foreach (string seg in target.Split('/'))
            {
                if (seg.Length == 0 || seg == ".")
                    continue;
                if (seg == "..")
                {
                    if (segments.Count == 0)
                        return null; // would escape the archive root
                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(seg);
                }
            }

            return segments.Count == 0 ? null : AssetPaths.Normalize(string.Join('/', segments));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _archive?.Dispose();
                _archive = null;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
