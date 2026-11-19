using System.Security.Cryptography;
using System.Text;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Gameplay;

namespace VortexArena.Server;

/// <summary>One native server world's record database lease. Open before simulation and close after the worker stops.</summary>
public sealed class RaceRecordFile
{
    private static readonly UTF8Encoding ReadEncoding = new(true, true);
    private static readonly UTF8Encoding WriteEncoding = new(false, true);
    private readonly string _path;
    private FileStream? _lease;

    private RaceRecordFile(string path) => _path = path;

    public static string ProfileFileName(string sessionId) => sessionId.Length == 0
        ? "race-records.tsv"
        : $"race-records-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant()}.tsv";

    public static RaceRecordFile Open(string path)
    {
        RaceRecords.Clear();
        var session = new RaceRecordFile(path);
        try
        {
            session._lease = AcquireLease(path);
            try
            {
                RaceRecords.Import(ReadSnapshot(path));
                Log.Info($"[RaceRecords] Loaded {path}.");
            }
            catch (FileNotFoundException) { } // First world in a new profile.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException)
        {
            session._lease?.Dispose();
            session._lease = null;
            Log.Warn($"[RaceRecords] Persistence disabled for {path}: {ex.Message}");
        }
        return session;
    }

    public void Close(bool cheated)
    {
        var lease = _lease;
        _lease = null;
        if (lease is null) return;
        try
        {
            if (cheated)
            {
                Log.Info("[RaceRecords] Discarded changes from a world where cheats were used.");
                return;
            }

            string snapshot = RaceRecords.Export();
            if (snapshot.Length == 0 && !File.Exists(_path)) return;
            WriteSnapshot(_path, snapshot);
            Log.Info($"[RaceRecords] Saved {_path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            Log.Warn($"[RaceRecords] Could not save {_path}: {ex.Message}");
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Read and replace the native record file under the same exclusive lease as the running host.</summary>
    public static void UpdateOffline(string path, Func<string, string> update)
    {
        using var lease = AcquireLease(path);
        string original;
        try { original = ReadSnapshot(path); }
        catch (FileNotFoundException) { original = ""; }
        string updated = update(original);
        if (updated != original)
            WriteSnapshot(path, updated);
    }

    private static FileStream AcquireLease(string path) =>
        new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static string ReadSnapshot(string path)
    {
        using var reader = new StreamReader(path, ReadEncoding, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static void WriteSnapshot(string path, string snapshot)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(staging, options))
            {
                using (var writer = new StreamWriter(stream, WriteEncoding, leaveOpen: true))
                    writer.Write(snapshot);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(staging, File.GetUnixFileMode(path) & (UnixFileMode)0x1ff);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }
}
