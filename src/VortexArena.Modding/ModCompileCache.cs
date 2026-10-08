using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VortexArena.Modding;

/// <summary>
/// The on-disk store of COMPILED mod modules, so that the second time a client meets a mod it skips
/// the compiler (about 200 ms for a C# guest) and only loads the result.
///
/// A compiled module is native machine code, and Wasmtime runs one without checking it: handing
/// <c>Module.Deserialize</c> bytes an attacker chose is handing them the process. So this store is
/// built so that a server never chooses those bytes:
///
/// - It is a directory of its own. Nothing a server sends is ever written into it - downloaded files
///   go to <see cref="ModCache"/>, a different directory - and no path in it comes from a server: a
///   file's name is a hash this client computed.
/// - An entry is looked up by the SHA-256 of the WebAssembly bytes the client is about to run, which
///   the caller has already verified against the manifest. A cached entry is only ever the output of
///   this client's own compiler for exactly those bytes.
/// - Every entry carries a keyed checksum (HMAC-SHA-256) made with a random key that is generated on
///   this machine and never leaves it. An entry without a valid checksum is deleted and the module is
///   compiled again. That does not stop someone who can already read and write the player's files -
///   nothing can - but it does stop a bug that only lets a stranger WRITE a file (an archive that
///   escapes its directory, say) from becoming code execution through this cache.
/// - The key also covers the Wasmtime version, the CPU and operating system, and the engine settings
///   the module was compiled for; and Wasmtime itself refuses an artifact from another version or
///   configuration. Either refusal is treated as a miss.
///
/// Everything here fails closed and quietly: an unreadable directory, a full disk, a damaged entry -
/// the module is compiled as if the cache did not exist.
///
/// Safe to use from any thread; two threads storing the same entry both write the same bytes.
/// </summary>
public sealed class ModCompileCache
{
    private const string Extension = ".cwasm";
    private const string KeyFileName = "install.key";
    private const int MacBytes = 32;
    private static readonly byte[] s_magic = "VXCWASM1"u8.ToArray();

    private readonly string _root;
    private readonly object _keyLock = new();
    private byte[]? _installKey;

    /// <summary>Entries kept; the least recently used go first. A handful is plenty: one per mod the player meets.</summary>
    public int MaxEntries { get; init; } = 16;

    /// <summary>Largest compiled module stored or read back. A C# guest compiles to under 10 MiB.</summary>
    public long MaxEntryBytes { get; init; } = 128L << 20;

    public ModCompileCache(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory => _root;

    /// <summary>
    /// The name an entry for these WebAssembly bytes, compiled for this engine configuration, is stored
    /// under. <paramref name="engineFingerprint"/> describes every engine setting that changes the
    /// generated code (see <c>WasmModSandbox</c>).
    /// </summary>
    public static string KeyFor(ReadOnlySpan<byte> wasm, string engineFingerprint)
    {
        string wasmSha = Convert.ToHexString(SHA256.HashData(wasm)).ToLowerInvariant();
        string material = string.Join('\n', "vortex-cwasm-1", wasmSha, RuntimeVersion, RuntimeInformation.RuntimeIdentifier, engineFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>The Wasmtime package version this process is running; part of every key.</summary>
    public static string RuntimeVersion { get; } = ReadRuntimeVersion();

    /// <summary>The compiled module stored under <paramref name="key"/>, or false when there is none that can be trusted.</summary>
    public bool TryRead(string key, out byte[] compiled)
    {
        compiled = Array.Empty<byte>();
        if (!IsKey(key)) return false;
        string path = PathFor(key);
        try
        {
            FileInfo file = new(path);
            if (!file.Exists) return false;
            if (file.Length < s_magic.Length + MacBytes + 1 || file.Length > MaxEntryBytes + s_magic.Length + MacBytes)
            {
                Delete(path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            int payloadLength = bytes.Length - s_magic.Length - MacBytes;
            if (payloadLength <= 0 || !bytes.AsSpan(0, s_magic.Length).SequenceEqual(s_magic)
                || InstallKey(create: false) is not { } installKey
                || !CryptographicOperations.FixedTimeEquals(Mac(installKey, key, bytes.AsSpan(s_magic.Length, payloadLength)), bytes.AsSpan(bytes.Length - MacBytes)))
            {
                Delete(path);
                return false;
            }

            compiled = bytes.AsSpan(s_magic.Length, payloadLength).ToArray();
            try { file.LastWriteTimeUtc = DateTime.UtcNow; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Stores a module this process has just compiled. Failures are swallowed: the cache is an optimisation.</summary>
    public void Write(string key, ReadOnlySpan<byte> compiled)
    {
        if (!IsKey(key) || compiled.IsEmpty || compiled.Length > MaxEntryBytes) return;
        try
        {
            if (InstallKey(create: true) is not { } installKey) return;
            string path = PathFor(key);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(s_magic);
                stream.Write(compiled);
                stream.Write(Mac(installKey, key, compiled));
            }
            File.Move(temporary, path, overwrite: true);
            Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
        }
    }

    /// <summary>Forgets one entry (used when Wasmtime refuses what was stored).</summary>
    public void Remove(string key)
    {
        if (IsKey(key)) Delete(PathFor(key));
    }

    /// <summary>Number of compiled modules currently stored.</summary>
    public int Count
    {
        get
        {
            try { return Directory.Exists(_root) ? Directory.GetFiles(_root, "*" + Extension).Length : 0; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    // ------------------------------------------------------------------------------------------------

    private string PathFor(string key) => Path.Combine(_root, key + Extension);

    private static bool IsKey(string key) => ModManifest.IsSha256(key);

    private static byte[] Mac(byte[] installKey, string key, ReadOnlySpan<byte> payload)
    {
        using IncrementalHash mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, installKey);
        mac.AppendData(Encoding.ASCII.GetBytes(key));
        mac.AppendData(s_magic);
        mac.AppendData(payload);
        return mac.GetHashAndReset();
    }

    /// <summary>
    /// The per-installation secret behind the checksums: 32 random bytes in a file beside the entries.
    /// Losing it only costs a recompile, so a missing or malformed one is replaced (when storing) and
    /// makes every existing entry a miss.
    /// </summary>
    private byte[]? InstallKey(bool create)
    {
        lock (_keyLock)
        {
            if (_installKey is not null) return _installKey;
            string path = Path.Combine(_root, KeyFileName);
            try
            {
                if (File.Exists(path))
                {
                    byte[] existing = File.ReadAllBytes(path);
                    if (existing.Length == 32) return _installKey = existing;
                }
                if (!create) return null;

                Directory.CreateDirectory(_root);
                byte[] fresh = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(path, fresh);
                return _installKey = fresh;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private void Trim()
    {
        FileInfo[] files = new DirectoryInfo(_root).GetFiles("*" + Extension);
        if (files.Length <= MaxEntries) return;
        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (int i = 0; i < files.Length - MaxEntries; i++) Delete(files[i].FullName);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ReadRuntimeVersion()
    {
        System.Reflection.Assembly assembly = typeof(Wasmtime.Engine).Assembly;
        object[] attributes = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        string? informational = attributes.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion : null;
        return informational ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
