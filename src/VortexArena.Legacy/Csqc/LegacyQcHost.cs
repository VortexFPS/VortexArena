using System.Diagnostics;
using System.Text.RegularExpressions;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// The engine services a server-supplied QuakeC program runs against: the client's cvar store, a
/// console command queue, the virtual filesystem for reads, and one directory for writes.
///
/// This is where a downloaded program's reach is bounded. It reads only what is mounted in the virtual
/// filesystem, writes only under <see cref="WriteRoot"/>, cannot see cvars on the private list, and its
/// console commands are queued for the host to run (and filter) rather than executed from inside a
/// builtin - which is also what DarkPlaces does: localcmd appends to the command buffer.
/// </summary>
public sealed class LegacyQcHost : IQcHost
{
    // DarkPlaces cvar_type flags (prvm_cmds.c VM_cvar_type).
    private const int TypeExists = 1, TypeSaved = 2, TypePrivate = 4, TypeEngine = 8, TypeHasDescription = 16, TypeReadOnly = 32;

    private readonly CvarService _cvars;
    private readonly VirtualFileSystem _vfs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _pendingCommands = new();
    private readonly HashSet<string> _programCvars = new(StringComparer.Ordinal);
    private long _bytesWritten;

    /// <summary>Directory the program may write under (DarkPlaces' user directory + <c>data/</c>), or null to refuse all writes.</summary>
    public string? WriteRoot { get; init; }

    /// <summary>Total bytes the program may write in one session, across all files.</summary>
    public long WriteBudgetBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Cvars the program is told do not exist and cannot set: credentials, and anything that would let a
    /// server reconfigure the client outside the game (DarkPlaces marks these CF_PRIVATE or guards them
    /// in Cvar_Set; a prefix match is used here so a new credential cvar is covered by default).
    /// </summary>
    public Func<string, bool> IsPrivateCvar { get; init; } = DefaultIsPrivate;

    public Action<string> PrintSink { get; init; } = _ => { };
    public Action<string> WarningSink { get; init; } = _ => { };

    public LegacyQcHost(CvarService cvars, VirtualFileSystem vfs)
    {
        _cvars = cvars;
        _vfs = vfs;
        _onCvarChanged = name => CvarChanged?.Invoke(name);
        _cvars.Changed += _onCvarChanged;
    }

    private readonly Action<string> _onCvarChanged;

    /// <summary>
    /// Lets go of the cvar store. A session started from the Xonotic menu runs on the MENU's store, which
    /// outlives it: while this host stays subscribed there, the store keeps the host alive, the host keeps
    /// its print sink (the session) alive, and with the session its program, its level and every picture of
    /// it - a whole game held for as long as the menu is open. Call it when the session ends; a host that
    /// owns its store (a private session, a server, the menu itself) need not.
    /// </summary>
    public void Detach()
    {
        _cvars.Changed -= _onCvarChanged;
        CvarChanged = null;
    }

    /// <summary>A cvar's value changed, by whatever route (the console, the program, the host). What keeps a
    /// program's autocvar globals current (cvar.c Cvar_UpdateAutoCvar).</summary>
    public event Action<string>? CvarChanged;

    /// <summary>Whether a game file exists, by the same rules as <see cref="OpenRead"/> but without opening it.</summary>
    public bool FileExists(string path) => IsSafePath(path) && _vfs.Exists(path);

    /// <summary>A whole game file, or null if it does not exist or cannot be read.</summary>
    public byte[]? ReadFile(string path)
    {
        if (!IsSafePath(path) || !_vfs.Exists(path)) return null;
        try { return _vfs.ReadBytes(path); }
        catch (IOException) { return null; }
    }

    private static bool DefaultIsPrivate(string name) =>
        name.StartsWith("rcon_", StringComparison.Ordinal)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("crypto_", StringComparison.Ordinal)
        || name is "sv_curl_serverpackages" or "cl_curl_enabled";

    public void Print(string text) => PrintSink(text);
    public void Warning(string text) => WarningSink(text);
    public bool Developer => _cvars.GetFloat("developer") != 0f;
    public bool Utf8Enabled => !_cvars.Has("utf8_enable") || _cvars.GetFloat("utf8_enable") != 0f;
    public double RealTime => RealTimeSource?.Invoke() ?? _clock.Elapsed.TotalSeconds;

    /// <summary>Replaces the wall clock behind <see cref="RealTime"/> (cltime, gettime, entity reuse
    /// delays). Null (the default): a stopwatch started with this host. Set only by runs that must be
    /// repeatable - a simulated-clock harness comparing two builds.</summary>
    public Func<double>? RealTimeSource { get; set; }

    // ---- cvars -------------------------------------------------------------------------------------

    // Whether a name is on the private list is a matter of its spelling alone, and the test (three
    // prefix checks and a case-blind search for "password") costs more than the cvar lookup it guards;
    // a program reads the same few dozen names every frame.
    private readonly Dictionary<string, bool> _privateNames = new(StringComparer.Ordinal);

    private bool IsPrivate(string name)
    {
        if (_privateNames.TryGetValue(name, out bool isPrivate)) return isPrivate;
        isPrivate = IsPrivateCvar(name);
        if (_privateNames.Count >= 8192) _privateNames.Clear();
        _privateNames[name] = isPrivate;
        return isPrivate;
    }

    private bool Visible(string name) => name.Length > 0 && !IsPrivate(name) && _cvars.Has(name);

    public bool CvarExists(string name) => Visible(name);
    public string CvarString(string name) => Visible(name) ? _cvars.GetString(name) : "";
    public float CvarFloat(string name) => Visible(name) ? _cvars.GetFloat(name) : 0f;
    public string CvarDefaultString(string name) => Visible(name) ? _cvars.GetDefault(name) : "";
    public string CvarDescription(string name) => Visible(name) ? _cvars.GetDescription(name) : "";

    public int CvarTypeFlags(string name)
    {
        if (!Visible(name)) return 0;
        int flags = TypeExists;
        if (_cvars.IsArchived(name)) flags |= TypeSaved;
        if (!_programCvars.Contains(name)) flags |= TypeEngine;
        if (_cvars.GetDescription(name).Length > 0) flags |= TypeHasDescription;
        return flags;
    }

    public void CvarSet(string name, string value)
    {
        if (name.Length == 0 || IsPrivateCvar(name))
        {
            WarningSink($"cvar_set: \"{name}\" may not be changed by game code");
            return;
        }
        if (!_cvars.Has(name))
        {
            // DarkPlaces: VM_cvar_set on an unknown cvar warns and does nothing.
            WarningSink($"cvar_set: variable {name} not found");
            return;
        }
        _cvars.Set(name, value);
    }

    public bool RegisterCvar(string name, string value, int flags)
    {
        if (name.Length == 0 || IsPrivateCvar(name) || _cvars.Has(name)) return false;
        _cvars.Register(name, value, CvarFlags.None);
        _programCvars.Add(name);
        return true;
    }

    public IEnumerable<string> CvarNames(string prefix, string antiPrefix)
    {
        foreach (string name in _cvars.Names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (antiPrefix.Length > 0 && name.StartsWith(antiPrefix, StringComparison.Ordinal)) continue;
            if (IsPrivateCvar(name)) continue;
            yield return name;
        }
    }

    // ---- console -----------------------------------------------------------------------------------

    public void LocalCommand(string text)
    {
        // Bounded: a program that queues commands in a loop is stopped by the VM's runaway counter long
        // before memory matters, but the queue should not be the thing that grows without limit.
        if (_pendingCommands.Count < 65536) _pendingCommands.Add(text);
    }

    /// <summary>Removes and returns the console text queued by localcmd since the last call, in order.</summary>
    public IReadOnlyList<string> TakePendingCommands()
    {
        if (_pendingCommands.Count == 0) return Array.Empty<string>();
        string[] taken = _pendingCommands.ToArray();
        _pendingCommands.Clear();
        return taken;
    }

    // ---- files -------------------------------------------------------------------------------------

    public Stream? OpenRead(string path)
    {
        if (!IsSafePath(path)) return null;
        // A file the program wrote this session (or in an earlier one) is found before packaged data,
        // as DarkPlaces' search order puts the user directory first.
        // The program opens files constantly just to learn whether they exist (hundreds per level for
        // models and sounds), so neither answer may cost a system call or a read: what is under the
        // write root is listed once and kept current by OpenWrite, and packaged data is read only if the
        // program actually reads from the handle.
        string? written = WritePath(path);
        if (written is not null && WrittenFiles().Contains(written))
        {
            try { return new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!_vfs.Exists(path)) return null;
        return new DeferredReadStream(_vfs, path);
    }

    private HashSet<string>? _writtenFiles;

    private HashSet<string> WrittenFiles()
    {
        if (_writtenFiles is not null) return _writtenFiles;
        _writtenFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (WriteRoot is not null && Directory.Exists(WriteRoot))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(Path.GetFullPath(WriteRoot), "*", SearchOption.AllDirectories))
                    _writtenFiles.Add(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return _writtenFiles;
    }

    /// <summary>A packaged file whose bytes are fetched on the first read, not when it is opened.</summary>
    private sealed class DeferredReadStream : Stream
    {
        private readonly VirtualFileSystem _vfs;
        private readonly string _path;
        private MemoryStream? _data;

        public DeferredReadStream(VirtualFileSystem vfs, string path) { _vfs = vfs; _path = path; }

        private MemoryStream Data
        {
            get
            {
                if (_data is not null) return _data;
                byte[] bytes;
                try { bytes = _vfs.ReadBytes(_path); }
                catch (IOException) { bytes = Array.Empty<byte>(); }
                return _data = new MemoryStream(bytes, writable: false);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Data.Length;
        public override long Position { get => Data.Position; set => Data.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Data.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => Data.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => Data.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public Stream? OpenWrite(string path, bool append)
    {
        string? full = IsSafePath(path) ? WritePath(path) : null;
        if (full is null) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            WrittenFiles().Add(full);
            // fs.c FS_SysOpenFiledesc opens with _SH_DENYNO and FS_Write goes straight to the descriptor:
            // what a program has written is in the file at once, and a file it never closed can be opened
            // again. Xonotic's CampaignSaveCvar depends on both - it writes campaign.cfg and never calls
            // fclose, then (on the last level) reads and rewrites the same file a second time. So: no
            // buffer of our own, and nobody is locked out.
            FileStream file = new(full, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            return new BudgetedStream(file, this);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string? WritePath(string path)
    {
        if (WriteRoot is null) return null;
        string root = Path.GetFullPath(WriteRoot);
        string full = Path.GetFullPath(Path.Combine(root, path));
        // Belt and braces after IsSafePath: the resolved file must still be inside the root.
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    public IReadOnlyList<string> Search(string pattern, bool caseInsensitive, string? packFile)
    {
        if (!IsSafePath(pattern.Replace('*', 'x').Replace('?', 'x'))) return Array.Empty<string>();
        int wildcard = pattern.IndexOfAny(new[] { '*', '?' });
        string prefix = wildcard < 0 ? pattern : pattern[..wildcard];
        // The virtual filesystem's keys are already lower-case, so matching is case-insensitive either way.
        // DarkPlaces' wildcards stop at a directory separator (fs.c FS_Search matches name by name):
        // "maps/*.bsp" must not find "maps/_init/_init.bsp", which Xonotic would then reject as a map name.
        Regex glob = new("^" + Regex.Escape(pattern.ToLowerInvariant()).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        List<string> matches = new();
        foreach (string key in _vfs.Find(prefix.ToLowerInvariant()))
        {
            if (!glob.IsMatch(key)) continue;
            matches.Add(key);
            if (matches.Count >= 16384) break;
        }
        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    // Not exposed by the virtual filesystem; the stock client uses whichpack only to label map sources.
    public string WhichPack(string path) => "";

    /// <summary>
    /// DarkPlaces' FS_CheckNastyPath, tightened: relative, forward slashes, no parent or current-directory
    /// components, no drive or stream separators, no control characters.
    /// </summary>
    public static bool IsSafePath(string path)
    {
        if (path.Length is 0 or > 1024 || path[0] is '/' or '\\') return false;
        foreach (char c in path)
            if (c is ':' or '\\' or '"' or '<' or '>' or '|' || char.IsControl(c)) return false;
        foreach (string part in path.Split('/'))
            if (part is "." or ".." || (part.Length == 0 && !path.EndsWith('/'))) return false;
        return !path.Contains("//", StringComparison.Ordinal);
    }

    private sealed class BudgetedStream : Stream
    {
        private readonly FileStream _inner;
        private readonly LegacyQcHost _host;
        public BudgetedStream(FileStream inner, LegacyQcHost host) { _inner = inner; _host = host; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_host._bytesWritten + count > _host.WriteBudgetBytes) throw new IOException("the game code's write budget is exhausted");
            _host._bytesWritten += count;
            _inner.Write(buffer, offset, count);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
