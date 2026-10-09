// The legacy session's own log file. DarkPlaces writes its console to a file when asked (log_file); here
// the lines a session produces - the connection, the signon stages, downloads, the precache, errors, what
// the server prints - always go to <user directory>/logs/legacy-<stamp>.log, because a game started the
// ordinary way has no standard output anyone can read afterwards.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VortexArena.Game.Legacy;

/// <summary>
/// One file per process, opened on the first line. Lines are queued and written by a pool thread (a frame
/// never waits for the disk), stamped with the wall clock and the seconds since the file was opened, bounded
/// in rate and in total, and anything that looks like a secret is not written at all.
/// </summary>
public static class LegacyLog
{
    private const int MaxLinesPerSecond = 400;
    private const long MaxBytes = 16L << 20;
    private const int KeepFiles = 20;

    private static readonly ConcurrentQueue<string> s_lines = new();
    private static readonly object s_gate = new();
    private static StreamWriter? s_writer;
    private static string? s_path;
    private static bool s_failed;
    private static long s_opened, s_written, s_windowStart;
    private static int s_windowLines, s_suppressed, s_pumping;

    /// <summary>The file being written, or null before the first line (and if it could not be opened).</summary>
    public static string? Path => s_path;

    /// <summary>Queues one line. Safe from any thread.</summary>
    public static void Write(string line)
    {
        if (s_failed || string.IsNullOrEmpty(line)) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (s_gate)
        {
            if (s_opened == 0) s_opened = now;
            // The rate bound: a server that floods prints fills its second and no more.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(s_windowStart, now).TotalSeconds >= 1)
            {
                s_windowStart = now;
                s_windowLines = 0;
            }
            if (++s_windowLines > MaxLinesPerSecond)
            {
                s_suppressed++;
                return;
            }
        }
        if (LooksSecret(line)) line = "(a line that names a password or rcon was not logged)";
        double t = System.Diagnostics.Stopwatch.GetElapsedTime(s_opened, now).TotalSeconds;
        s_lines.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} (t={t:0.00}) {Clean(line)}"));
        if (Interlocked.CompareExchange(ref s_pumping, 1, 0) == 0) System.Threading.Tasks.Task.Run(Pump);
    }

    /// <summary>Writes what is queued now (the end of a session, before the process may exit).</summary>
    public static void Flush()
    {
        SpinWait wait = default;
        for (int i = 0; i < 200 && (!s_lines.IsEmpty || Volatile.Read(ref s_pumping) != 0); i++)
        {
            if (Interlocked.CompareExchange(ref s_pumping, 1, 0) == 0) Pump();
            else wait.SpinOnce();
        }
    }

    // rcon, passwords and keys never reach the file, whoever printed them.
    private static bool LooksSecret(string line) =>
        line.Contains("rcon", StringComparison.OrdinalIgnoreCase) || line.Contains("password", StringComparison.OrdinalIgnoreCase)
        || line.Contains("crypto_", StringComparison.OrdinalIgnoreCase) || line.Contains("d0pk", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string line)
    {
        StringBuilder text = new(Math.Min(line.Length, 2000));
        foreach (char c in line)
        {
            if (text.Length >= 2000) { text.Append("..."); break; }
            text.Append(c == '\n' || c == '\r' ? ' ' : c < ' ' ? '?' : c);
        }
        return text.ToString();
    }

    private static void Pump()
    {
        do
        {
            try
            {
                StreamWriter? writer = Open();
                while (s_lines.TryDequeue(out string? line))
                {
                    if (writer is null || s_written > MaxBytes) continue;
                    int suppressed = Interlocked.Exchange(ref s_suppressed, 0);
                    if (suppressed > 0) writer.WriteLine($"({suppressed} lines were not logged: more than {MaxLinesPerSecond} in a second)");
                    writer.WriteLine(line);
                    s_written += line.Length + 2;
                    if (s_written > MaxBytes) writer.WriteLine($"(the log has reached {MaxBytes >> 20} MB and ends here)");
                }
                writer?.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                s_failed = true;
                while (s_lines.TryDequeue(out _)) { }
            }
            Volatile.Write(ref s_pumping, 0);
        }
        while (!s_lines.IsEmpty && Interlocked.CompareExchange(ref s_pumping, 1, 0) == 0);
    }

    private static StreamWriter? Open()
    {
        if (s_writer is not null || s_failed) return s_writer;
        string directory = UserPaths.Resolve("logs");
        Directory.CreateDirectory(directory);
        Prune(directory);
        s_path = System.IO.Path.Combine(directory, $"legacy-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        s_writer = new StreamWriter(new FileStream(s_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
        s_writer.WriteLine($"# Vortex legacy compatibility mode log - opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local time); t = seconds since then");
        s_writer.WriteLine("# connection, signon stages, downloads, precache, errors, and what the server printed. Never rcon or passwords.");
        return s_writer;
    }

    private static void Prune(string directory)
    {
        try
        {
            string[] files = Directory.GetFiles(directory, "legacy-*.log");
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - (KeepFiles - 1); i++) File.Delete(files[i]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
