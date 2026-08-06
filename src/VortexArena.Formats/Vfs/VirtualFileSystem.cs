using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VortexArena.Formats.Vfs;

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
///
/// <para><b>Rescanning.</b> <see cref="Rescan"/> (the <c>fs_rescan</c> console command) rebuilds the whole
/// search path so content dropped in while the game is running becomes visible. It works by REPLAYING the
/// recorded mount calls rather than by patching the existing list, so a rescanned search path is the same
/// path a fresh process would have built.</para>
/// </summary>
public sealed class VirtualFileSystem : IDisposable
{
    // Immutable snapshot of the search path: index 0 = highest priority (last mounted).
    // Replaced wholesale on every mount; never mutated in place. Readers grab the reference once.
    private volatile IReadOnlyList<IMount> _mounts = Array.Empty<IMount>();
    private readonly object _mountLock = new();
    private bool _disposed;

    public VirtualFileSystem() => ResolveCachesDirty += OnResolveCachesDirty;

    // The mount CALLS that built the current search path, in call order. Recorded so Rescan() can replay
    // them; a replay is what makes a rescanned path identical to a freshly-booted one instead of an
    // approximation of it. Guarded by _mountLock (written on mount, read on rescan).
    private readonly List<(MountSource Kind, string Path)> _sources = new();

    /// <summary>Which public mount entry point produced a recorded source (so <see cref="Rescan"/> replays
    /// it through the same code path it originally took).</summary>
    private enum MountSource
    {
        /// <summary><see cref="Mount(string)"/> — one archive or directory.</summary>
        Single,
        /// <summary><see cref="MountGameDir(string)"/> — a base dir and the packs directly inside it.</summary>
        GameDir,
        /// <summary><see cref="MountContentRoot(string)"/> — <c>&lt;root&gt;/maps</c> then <c>&lt;root&gt;</c>.</summary>
        ContentRoot,
    }

    /// <summary>What one <see cref="Rescan"/> did to the search path. <see cref="Added"/> counts mounts built
    /// fresh (new packs, plus every directory mount, which is always rebuilt), <see cref="Reused"/> the pack
    /// mounts carried over untouched, and <see cref="Removed"/> the ones that dropped out and were disposed.</summary>
    public readonly record struct RescanResult(int Mounts, int Added, int Removed, int Reused);

    // Resolved-path + negative lookup caches for the two hot extension-search paths (A4). Exists() linearly
    // probes every mount, and ResolveImage() probes up to 11 candidate vpaths × every mount per call — many
    // of which MISS by design (the _norm/_gloss/_glow/_reflect material-companion probes), repeating the full
    // scan every time. These cache the result (a null/false value is a cached MISS so a known-absent name is
    // never re-scanned), keyed by the normalized vpath (Exists) / stem (ResolveImage). Mounts happen at startup
    // while reads are hot + concurrent thereafter, so a plain ConcurrentDictionary (lock-free reads) cleared on
    // every mount change is sufficient and matches the class's stated threading model.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _existsCache = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _resolveImageCache = new(StringComparer.Ordinal);

    /// <summary>Subscribed to <see cref="ResolveCachesDirty"/> so a PreferDds flip re-resolves every stem.</summary>
    private void OnResolveCachesDirty()
    {
        _resolveImageCache.Clear();
        _existsCache.Clear();
    }

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

        lock (_mountLock)
        {
            _sources.Add((MountSource.Single, path));
            PrependLocked(new[] { mount });
        }
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

        List<IMount> built = BuildGameDirMounts(dir, reuse: null);

        // Prepend the whole batch atomically, preserving relative order (last element = top).
        lock (_mountLock)
        {
            _sources.Add((MountSource.GameDir, dir));
            PrependLocked(built);
        }
        return true;
    }

    /// <summary>
    /// The mounts <see cref="MountGameDir"/> produces for <paramref name="dir"/>, lowest priority first
    /// (packs in name order, then the directory itself on top). Empty when the directory does not exist.
    /// Shared with <see cref="Rescan"/> so a rescanned gamedir cannot order itself differently than a
    /// freshly mounted one.
    ///
    /// <para><paramref name="reuse"/>, when given, maps a pack's full path to a LIVE mount for it that the
    /// caller has already established is still valid; a hit is carried over instead of re-opened. Only pack
    /// files are reused — a directory mount is a plain re-walk with no handles to conserve.</para>
    /// </summary>
    private static List<IMount> BuildGameDirMounts(string dir, IReadOnlyDictionary<string, IMount>? reuse)
    {
        var built = new List<IMount>();
        if (!Directory.Exists(dir))
            return built;

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
        built.Capacity = entries.Count + 1;
        foreach (string entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    built.Add(new DirectoryMount(entry));
                    continue;
                }
                built.Add(reuse is not null && reuse.TryGetValue(Path.GetFullPath(entry), out IMount? live)
                    ? live
                    : new Pk3Mount(entry));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A corrupt/locked pack must not abort the whole gamedir mount; skip it like DP,
                // which logs "unable to load pak" and continues.
            }
        }
        built.Add(new DirectoryMount(dir));
        return built;
    }

    /// <summary>
    /// Mount a content root the way the game does: the per-map packages under <c>&lt;root&gt;/maps</c>
    /// first, then <paramref name="dataRoot"/> itself. Returns whether the root mounted.
    /// </summary>
    /// <remarks>
    /// Two calls are needed because <see cref="MountGameDir(string)"/> only enumerates packs DIRECTLY
    /// inside the directory it is handed, and compiled maps live one level down in per-map
    /// <c>.pk3dir</c> packages — they are fetched per <c>data/maps.lock.json</c> rather than committed.
    /// <para>
    /// Order is load-bearing and must be maps-first. Each call PREPENDS its batch, so the LAST call ends
    /// up highest in the search path. Mounting maps first leaves the data root above it, preserving the
    /// precedence the old bundled layout had, where core data outranked the compiled map packs
    /// (<c>xonotic-20230620-maps.pk3</c> sorts before <c>xonotic-data.pk3dir</c>). Reversing it would let
    /// any one map's <c>textures/foo</c> shadow core's.
    /// </para>
    /// <para>
    /// This lives here rather than at the call sites so the game and the tests cannot drift apart on it.
    /// They did once: the tests mounted only the root, so they saw 50 of the 185 shader scripts and the
    /// material-count assertions failed while the content was in fact all present.
    /// </para>
    /// <para>
    /// <b>Several roots layer.</b> Each call prepends its whole block, so a LATER root outranks an earlier one
    /// entirely — which is how the per-user gamedir is stacked over the shipped content (Darkplaces'
    /// <c>FS_Init</c> adds the basedir gamedir and then the userdir one, so a player's own pack wins). The
    /// consequence to be aware of: a pack in the later root can shadow core content from the earlier root,
    /// including a map pack's <c>textures/</c> shadowing core's. That is what makes an override pack possible
    /// and it is DP's behaviour, but it means a careless user pack can restyle the whole game.
    /// </para>
    /// </remarks>
    public bool MountContentRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);

        // Built outside the lock (a pack's central directory + symlink pass is the slow part), swapped in
        // under it. <root>/maps is absent on a checkout that has not fetched maps, and on a user gamedir the
        // player has not put anything in yet — BuildGameDirMounts answers with an empty batch either way.
        List<IMount> mapsBatch = BuildGameDirMounts(Path.Combine(dataRoot, "maps"), reuse: null);
        List<IMount> rootBatch = BuildGameDirMounts(dataRoot, reuse: null);
        bool rootExists = Directory.Exists(dataRoot);

        lock (_mountLock)
        {
            // Recorded even when the root does not exist yet, so a later Rescan() picks it up once the player
            // creates it — the whole point of the command is to not need a restart.
            _sources.Add((MountSource.ContentRoot, dataRoot));
            PrependLocked(mapsBatch);
            PrependLocked(rootBatch);
        }
        return rootExists;
    }

    /// <summary>
    /// Rebuild the entire search path from the recorded mount calls, so content added, replaced or removed on
    /// disk since boot takes effect — the <c>fs_rescan</c> console command (DP <c>FS_Rescan_f</c>).
    ///
    /// <para><b>Unchanged packs are carried over, not re-opened.</b> Opening a <c>.pk3</c> reads its central
    /// directory and runs the symlink pass over it (which reads entry bodies — <c>shared.pk3</c> alone has
    /// ~974), and a stock tree is half a gigabyte of packs. A pack whose size and mtime still match what they
    /// were at mount keeps its existing mount object; only new/changed ones are opened. Directory mounts are
    /// always rebuilt — a directory index is a plain re-walk and holds no handles to conserve.</para>
    ///
    /// <para><b>What this does NOT do</b> — the same line DP draws: it refreshes where files are FOUND, not
    /// what has already been loaded from them. Textures, models and sounds already decoded this session keep
    /// their loaded copies (they are owned by caches above this class, and by the live scene). Callers that
    /// hold VFS-derived state of their own must invalidate it themselves; the game's <c>fs_rescan</c> does that
    /// for the shader table and the menu's map/preview caches.</para>
    ///
    /// <para><b>Concurrency.</b> Safe to call while reads are in flight: the new search path is swapped in
    /// atomically, and a reader holding the old snapshot keeps using it. A mount that dropped out IS disposed
    /// here, so a read that is mid-flight on a pack whose file was deleted or replaced fails with an
    /// <see cref="AssetParseException"/> ("has been disposed") rather than returning wrong bytes — the
    /// failure callers already handle by skipping the asset. Disposal takes each mount's own read gate, so it
    /// never tears down underneath an active read.</para>
    /// </summary>
    public RescanResult Rescan()
    {
        lock (_mountLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VirtualFileSystem));

            IReadOnlyList<IMount> old = _mounts;

            // Belt and braces: a mount path that forgot to record its source would otherwise have every mount
            // silently disposed here. Nothing to replay = nothing to do.
            if (_sources.Count == 0)
                return new RescanResult(old.Count, 0, 0, old.Count);

            // Only mounts still backed by the same bytes on disk are eligible to be carried over.
            var reuse = new Dictionary<string, IMount>(StringComparer.Ordinal);
            foreach (IMount m in old)
            {
                if (m.IsCurrent())
                    reuse.TryAdd(m.SourcePath, m);
            }

            var next = new List<IMount>();
            foreach ((MountSource kind, string path) in _sources)
            {
                switch (kind)
                {
                    case MountSource.Single:
                        if (BuildSingleMount(path, reuse) is { } single)
                            PrependInto(next, new[] { single });
                        break;
                    case MountSource.GameDir:
                        PrependInto(next, BuildGameDirMounts(path, reuse));
                        break;
                    case MountSource.ContentRoot:
                        PrependInto(next, BuildGameDirMounts(Path.Combine(path, "maps"), reuse));
                        PrependInto(next, BuildGameDirMounts(path, reuse));
                        break;
                }
            }

            _mounts = next;
            ClearLookupCaches(); // the whole point: a cached MISS may now be a hit (and vice-versa)

            // Dispose whatever fell out of the path. Reference identity is the right test — a carried-over
            // mount is literally the same object — and neither mount type overrides Equals, so the default
            // comparer IS reference equality.
            var kept = new HashSet<IMount>(next);
            var before = new HashSet<IMount>(old);
            int removed = 0;
            foreach (IMount m in old)
            {
                if (kept.Contains(m))
                    continue;
                removed++;
                m.Dispose();
            }

            int added = 0;
            foreach (IMount m in next)
            {
                if (!before.Contains(m))
                    added++;
            }
            return new RescanResult(next.Count, added, removed, next.Count - added);
        }
    }

    /// <summary>Rebuild one <see cref="MountSource.Single"/> source during a rescan. Unlike
    /// <see cref="Mount(string)"/> this swallows a bad pack: a rescan reports on a search path, and one
    /// archive that has gone corrupt since boot must not take the other mounts down with it.</summary>
    private static IMount? BuildSingleMount(string path, IReadOnlyDictionary<string, IMount> reuse)
    {
        try
        {
            if (Directory.Exists(path))
                return new DirectoryMount(path);
            if (!File.Exists(path))
                return null;
            return reuse.TryGetValue(Path.GetFullPath(path), out IMount? live) ? live : new Pk3Mount(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prepend a batch to the live search path, where the LAST item of <paramref name="batch"/> becomes the
    /// highest priority — matching the order in which <see cref="BuildGameDirMounts"/> appends (packs, then
    /// the dir). Must be called under <see cref="_mountLock"/>.
    /// </summary>
    private void PrependLocked(IReadOnlyList<IMount> batch)
    {
        if (batch.Count == 0)
            return;
        var next = new List<IMount>(batch.Count + _mounts.Count);
        PrependInto(next, batch);
        next.AddRange(_mounts);
        _mounts = next;
        ClearLookupCaches(); // new mounts can satisfy previously-cached MISSes — invalidate
    }

    /// <summary>The one place the "last appended = highest priority" reversal lives, so the live mount path
    /// and <see cref="Rescan"/> cannot disagree about it.</summary>
    private static void PrependInto(List<IMount> list, IReadOnlyList<IMount> batch)
    {
        if (batch.Count == 0)
            return;
        var head = new List<IMount>(batch.Count);
        for (int i = batch.Count - 1; i >= 0; i--) // reverse: last appended = front = top priority
            head.Add(batch[i]);
        list.InsertRange(0, head);
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
                if (m.Contains(candidate))
                {
                    // Return where the BYTES are, not the name that was asked for. Xonotic's build-time
                    // dedup turns duplicate textures into links, and shared.pk3 carries ~900 of them whose
                    // symlink bit was lost when the pack was zipped — so without this a link and its target
                    // are two distinct strings for one file, and every cache keyed on the result (notably
                    // AssetSystem's texture cache, which exists precisely so "two names that resolve to the
                    // same file share one GPU texture") decodes and uploads it twice.
                    resolved = m.ResolveLink(candidate);
                    break;
                }
            }
            if (resolved != null)
                break;
        }
        _resolveImageCache[stem] = resolved;
        return resolved;
    }

    /// <summary>
    /// DarkPlaces <c>r_texture_dds_load</c>: prefer a pre-compressed <c>dds/&lt;name&gt;.dds</c> over the
    /// original .tga/.png/.jpg when both exist.
    ///
    /// <para>This is the difference between using Xonotic's shipped compression and redoing it. The stock maps
    /// pack carries 3,207 files under <c>dds/</c> — already block-compressed, ready to hand straight to the
    /// GPU — but the probe order below put .tga first, so the uncompressed original won every time and the
    /// engine then spent ~366 ms per texture re-encoding what it had just declined to use. It is also what
    /// makes the write-side cache (r_texture_dds_save) visible on the next launch.</para>
    ///
    /// <para>A plain static because the resolver is static and the value is a process-wide render setting;
    /// the setter clears the resolve cache, since flipping it changes what every stem resolves to.</para>
    /// </summary>
    public static bool PreferDds
    {
        get => _preferDds;
        set
        {
            if (_preferDds == value)
                return;
            _preferDds = value;
            ResolveCachesDirty?.Invoke();
        }
    }

    private static bool _preferDds = true;

    /// <summary>Raised when <see cref="PreferDds"/> changes so live instances can drop their resolve caches.</summary>
    public static event Action? ResolveCachesDirty;

    /// <summary>The ordered candidate vpaths <see cref="ResolveImage"/> probes, for a normalized stem.</summary>
    private static IEnumerable<string> ImageCandidates(string stem)
    {
        // Pre-compressed first when r_texture_dds_load is on — see PreferDds for why this ordering matters.
        if (_preferDds)
        {
            yield return "dds/" + stem + ".dds";
            yield return stem + ".dds";
            yield return stem + ".tga.dds";
        }
        // override/ takes absolute priority (DP imageformats_other / _textures lead with it).
        yield return "override/" + stem + ".tga";
        yield return "override/" + stem + ".png";
        yield return "override/" + stem + ".jpg";

        // Normal raster formats, DP order: tga, png, jpg.
        yield return stem + ".tga";
        yield return stem + ".png";
        yield return stem + ".jpg";

        // Precompressed DDS forms Xonotic uses (dds/ cache dir, bare .dds, and the ".tga.dds"
        // convention where DDS is appended to the original extension — see gl_textures.c). Already emitted
        // above when PreferDds is on; repeating them here would only cost a duplicate miss.
        if (!_preferDds)
        {
            yield return "dds/" + stem + ".dds";
            yield return stem + ".dds";
            yield return stem + ".tga.dds";
        }

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
        ResolveCachesDirty -= OnResolveCachesDirty;
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

        /// <summary>
        /// True when this mount still reflects what is on disk, so <see cref="VirtualFileSystem.Rescan"/> can
        /// carry it over instead of rebuilding it. Defaults to FALSE — a mount type opts in only when it can
        /// answer cheaply and exactly, and the safe answer is to rebuild.
        /// </summary>
        bool IsCurrent() => false;

        /// <summary>(perf 2026-07-03) Read into a caller-owned, grow-only buffer (see
        /// <see cref="VirtualFileSystem.ReadBytesInto"/>). Returns the byte count actually read.</summary>
        int ReadBytesInto(string key, ref byte[]? buffer);

        /// <summary>Reads the entry; <paramref name="key"/> must already be normalized and present.</summary>
        byte[] ReadBytes(string key);

        /// <summary>
        /// Follow any link this mount records for <paramref name="key"/> to the entry that actually holds
        /// the bytes, or return it unchanged. Exposed so <see cref="VirtualFileSystem.ResolveImage"/> can
        /// hand callers the FINAL vpath: a link and its target must resolve to the same string, or every
        /// cache keyed on the result stores the same file twice under two names.
        /// </summary>
        string ResolveLink(string key) => key;
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
        // Size + mtime as they were when this archive was opened. Rescan compares them against the file on
        // disk to decide whether this mount can be carried over (see IsCurrent). Cheap and exact enough: a
        // rewritten pack changes at least one of the two, and the cost of being wrong is bounded by the fact
        // that a pack is only ever REPLACED wholesale, never edited in place.
        private readonly long _stampLength;
        private readonly DateTime _stampWriteUtc;

        public Pk3Mount(string path)
        {
            _path = Path.GetFullPath(path);
            _index = new Dictionary<string, string>(StringComparer.Ordinal);
            _symlinks = new Dictionary<string, string>(StringComparer.Ordinal);

            var stamp = new FileInfo(_path);
            _stampLength = stamp.Length;
            _stampWriteUtc = stamp.LastWriteTimeUtc;

            // FileShare.Delete matters on Windows, where a share mode without it makes an open pack
            // UNDELETABLE: a player trying to remove a map pack while the game runs gets "in use by another
            // process" from their file manager, and fs_rescan can never observe a removal. With it the unlink
            // succeeds immediately (the name is released once this mount is disposed, which is what a rescan
            // does), and reads through this handle keep working against the now-unlinked file in the meantime.
            // POSIX already behaves this way; this makes Windows match.
            _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
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

            // (key, entry, declared) — `declared` distinguishes an entry that still carries its S_IFLNK bit
            // from one merely SHAPED like a stripped link. The second pass trusts the former and demands
            // more of the latter.
            List<(string key, ZipArchiveEntry entry, bool declared)>? symlinkEntries = null;
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
                bool declared = IsSymlink(entry);
                if (declared || LooksLikeStrippedSymlink(entry))
                    (symlinkEntries ??= new List<(string, ZipArchiveEntry, bool)>()).Add((key, entry, declared));
            }

            // Second pass: resolve symlink entries against the now-complete index. A link's body is its
            // target path (relative to the link's directory); register key -> target only when the target
            // is a real entry in THIS pk3. Otherwise the entry is left as a plain file, so behaviour is
            // unchanged for links we can't follow. The target may itself be a symlink — the read-time loop
            // follows the chain.
            if (symlinkEntries != null)
            {
                foreach ((string key, ZipArchiveEntry entry, bool declared) in symlinkEntries)
                {
                    if (entry.Length is <= 0 or > 4096) // a path target is tiny; never a real file's size
                        continue;
                    string target;
                    try { target = ReadEntryText(entry).Trim(); }
                    catch { continue; }
                    string? targetKey = ResolveSymlinkTarget(key, target);
                    if (targetKey is null || !_index.TryGetValue(targetKey, out string? targetEntryName))
                        continue;

                    // An UNDECLARED candidate must point at something that could actually BE the image it
                    // claims to be. Content alone cannot separate "a stripped symlink" from "a small file
                    // that happens to contain a path" — they are byte-identical — so the discriminator is
                    // the target: a real link points at a real image, and a DDS header alone is 128 bytes
                    // (TGA's is 18). Without this, a regular file whose body reads as a sibling name gets
                    // followed, which VfsSymlinkTests guards against deliberately. A DECLARED symlink skips
                    // the check: the archive says it is a link, and that is not ours to second-guess.
                    if (!declared && (archiveEntryLength(targetEntryName) < 18))
                        continue;

                    _symlinks[key] = targetKey;
                }

                long archiveEntryLength(string entryName)
                    => _archive.GetEntry(entryName)?.Length ?? 0;
            }
        }

        public string SourcePath => _path;
        public IEnumerable<string> Keys => _index.Keys;
        public bool Contains(string key) => _index.ContainsKey(key);

        /// <summary>Still the same archive we indexed? A disposed mount, a deleted file, or one whose size or
        /// mtime moved answers false and gets rebuilt by the rescan.</summary>
        public bool IsCurrent()
        {
            lock (_gate)
            {
                if (_archive is null)
                    return false;
            }
            try
            {
                var now = new FileInfo(_path);
                return now.Exists && now.Length == _stampLength && now.LastWriteTimeUtc == _stampWriteUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // can't tell → rebuild, which will report the real problem
            }
        }

        public byte[] ReadBytes(string key)
        {
            key = FollowLinks(key);   // Xonotic dedup links, and ones whose S_IFLNK bit was stripped

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
            key = FollowLinks(key);   // Xonotic dedup links, and ones whose S_IFLNK bit was stripped

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

        /// <summary>
        /// A symlink whose S_IFLNK bit did not survive being zipped: a tiny regular entry whose CONTENT is
        /// a sibling filename. <c>shared.pk3</c> alone carries <b>974</b> of them, 903 pointing at a real
        /// DDS — verified by reading the zip, whose external attributes are a plain <c>0o600</c>, so
        /// nothing downstream can tell except by looking at the bytes.
        ///
        /// <para>This is only a CANDIDATE filter; the second pass is what decides. That pass reads the
        /// body, resolves it relative to the link's directory, rejects anything escaping the archive root,
        /// and registers the link ONLY when the target is a real entry in THIS pk3. So a false positive
        /// needs a genuine image file under 120 bytes whose entire content is a valid relative path to
        /// another entry in the same archive — and even then the bytes it resolves to are a real image.</para>
        ///
        /// <para>Deciding this once at mount, from the central directory, is why the per-load alias probe
        /// that used to live in <c>AssetSystem</c> could be deleted: that one read every image in full
        /// before decoding it, just to discover it was not a stub.</para>
        /// </summary>
        private static bool LooksLikeStrippedSymlink(ZipArchiveEntry entry)
        {
            // A DDS header alone is 128 bytes and TGA's is 18; nothing legitimate is this small. Bounded to
            // image entries so the extra body reads at mount time stay proportional to the problem.
            if (entry.Length is <= 0 or > 120)
                return false;
            string name = entry.Name;
            int dot = name.LastIndexOf('.');
            if (dot < 0) return false;
            string ext = name[(dot + 1)..].ToLowerInvariant();
            return ext is "dds" or "tga" or "png" or "jpg" or "jpeg";
        }

        /// <summary>
        /// Follow the link chain for <paramref name="key"/>, guarding against a malformed pack pointing two
        /// entries at each other. Shared by <see cref="ReadBytes"/>, <see cref="ReadBytesInto"/> and
        /// <see cref="ResolveLink"/> so the three cannot disagree about where a name ends up.
        /// </summary>
        private string FollowLinks(string key)
        {
            for (int hops = 0; _symlinks.TryGetValue(key, out string? target); hops++)
            {
                if (hops >= 8)
                    throw new AssetParseException($"symlink chain too deep starting at \"{key}\" in \"{_path}\".");
                key = target;
            }
            return key;
        }

        public string ResolveLink(string key) => _symlinks.Count == 0 ? key : FollowLinks(key);

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
