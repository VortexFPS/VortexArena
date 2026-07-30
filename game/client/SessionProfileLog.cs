using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using VortexArena.Common.Diagnostics;

namespace VortexArena.Game.Client;

/// <summary>
/// One per-frame scalar row for the parallel CSV. A plain struct so the main thread enqueues it without any heap
/// allocation or string work — the writer thread does ALL formatting. The scope detail of a hitch lives in the
/// human-readable <c>.log</c>; this CSV is the per-frame numeric timeline for offline plotting (pandas/Excel).
/// </summary>
public readonly struct FrameSample
{
    public readonly long Frame;
    public readonly double TimeS;       // session seconds (monotonic, from the collector's stopwatch)
    public readonly double Ms, ProcMs, RcpuMs, GpuMs, PhysMs, RestMs;
    public readonly long AllocBytes;
    public readonly int G0, G1, G2;
    public readonly double GcPauseMs, DrawCalls;
    public readonly long PipeCompiles, PipeUber;
    public readonly string? Top1;       // dominant scope name (interned literal ⇒ no alloc to reference)
    public readonly double Top1Ms;
    public readonly double LateMs;      // the iteration's deferred+present gap (P2; see Prof.LastLateMs)

    public FrameSample(long frame, double timeS, double ms, double procMs, double rcpuMs, double gpuMs,
        double physMs, double restMs, long allocBytes, int g0, int g1, int g2, double gcPauseMs,
        double drawCalls, long pipeCompiles, long pipeUber, string? top1, double top1Ms, double lateMs)
    {
        Frame = frame; TimeS = timeS; Ms = ms; ProcMs = procMs; RcpuMs = rcpuMs; GpuMs = gpuMs;
        PhysMs = physMs; RestMs = restMs; AllocBytes = allocBytes; G0 = g0; G1 = g1; G2 = g2;
        GcPauseMs = gcPauseMs; DrawCalls = drawCalls; PipeCompiles = pipeCompiles; PipeUber = pipeUber;
        Top1 = top1; Top1Ms = top1Ms; LateMs = lateMs;
    }
}

/// <summary>
/// The recorded session log: a per-launch <c>session-YYYYMMDD-HHMMSS.log</c> (human-readable hitch/snapshot/
/// summary lines) plus a parallel <c>.csv</c> (the per-frame numeric timeline) under <c>&lt;userdir&gt;/logs/</c>.
///
/// <para><b>Zero main-thread cost.</b> The game thread only ever <see cref="WriteLine"/>s a (rare) text line or
/// <see cref="WriteFrame"/>s a value-type <see cref="FrameSample"/> — both are a lock-free
/// <see cref="ConcurrentQueue{T}"/> enqueue, no I/O, no flush, and (for the per-frame path) no string formatting.
/// A dedicated writer thread drains the queue, formats, and flushes both files on a ~1 s cadence. The old
/// <c>frameprofile.log</c> flushed every line on the main thread — which did disk I/O on the exact frame that was
/// already hitching; this removes that entirely.</para>
///
/// <para>The queue is bounded: under backpressure (writer falling behind) the high-frequency per-frame CSV rows
/// are dropped first and counted, while the rare text lines are never dropped — so a slow disk degrades the CSV's
/// resolution, never the game's frame time. Files are kept per session (no pruning), flushed periodically, and
/// closed cleanly on <see cref="Stop"/>; the per-line stamp is derived from the launch wall-clock + session
/// seconds, so there is no <c>DateTime.Now</c> on the hot path.</para>
/// </summary>
public sealed class SessionProfileLog
{
    private const int QueueCapacity = 8192;      // ~a minute of frames; bounds memory if the disk stalls
    private const int FlushIntervalMs = 1000;    // periodic flush cadence

    private readonly ConcurrentQueue<object> _queue = new();   // boxed FrameSample or string; drained off-thread
    private int _approxCount;
    private long _droppedFrames;

    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private volatile bool _stopping;

    private StreamWriter? _log;
    private StreamWriter? _csv;
    private DateTime _launchWall;
    private string _logPath = "", _csvPath = "";

    public string LogPath => _logPath;
    public bool Active => _thread is not null;

    /// <summary>Open the two files (named with the launch time) and spin up the writer thread. Best-effort: a
    /// failure to open just disables recording (logged once); the in-game profiler keeps working regardless.</summary>
    public void Start(DateTime launchWall, string envBanner, string columnsLegend)
    {
        if (_thread is not null)
            return;
        _launchWall = launchWall;
        string stamp = launchWall.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            _logPath = UserPaths.Resolve($"logs/session-{stamp}.log");
            _csvPath = UserPaths.Resolve($"logs/session-{stamp}.csv");
            _log = new StreamWriter(new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                                    new UTF8Encoding(false));
            _csv = new StreamWriter(new FileStream(_csvPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                                    new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn($"[frameprofiler] session log disabled (couldn't open {_logPath}): {ex.Message}");
            _log = null; _csv = null;
            return;
        }

        // Header block (synchronous — runs once at start before the thread, so it's always first in the file).
        _log.WriteLine($"# VortexArena session profile — launched {launchWall:yyyy-MM-dd HH:mm:ss}");
        _log.WriteLine($"# {envBanner}");
        _log.WriteLine($"# {columnsLegend}");
        _log.WriteLine($"# columns key: t=SECONDS = session seconds; the HH:MM:SS stamp is wall-clock.");
        _log.WriteLine();
        _csv.WriteLine("frame,time_s,ms,proc_ms,rcpu_ms,gpu_ms,phys_ms,rest_ms,alloc_kb,gc0,gc1,gc2," +
                       "gc_pause_ms,draw_calls,pipe_compiles,pipe_uber,top1,top1_ms,late_ms");

        _stopping = false;
        _thread = new Thread(WriterLoop)
        {
            Name = "frameprofiler-log",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,   // never compete with the game threads for a core
        };
        _thread.Start();
    }

    /// <summary>Enqueue a human-readable line for the <c>.log</c> (stamped with wall-clock + session seconds by the
    /// writer). Never dropped. Cheap: a single concurrent-queue enqueue, no I/O on the caller.</summary>
    public void WriteLine(double sessionSeconds, string text)
    {
        if (_log is null)
            return;
        Interlocked.Increment(ref _approxCount);
        _queue.Enqueue(new TextLine(sessionSeconds, text));
        _signal.Set();
    }

    /// <summary>Enqueue a per-frame CSV row. Dropped (and counted) first under backpressure so a slow disk can
    /// never stall the game. Allocation-free on the caller (value-type sample boxed once into the queue).</summary>
    public void WriteFrame(in FrameSample s)
    {
        if (_csv is null)
            return;
        if (Volatile.Read(ref _approxCount) >= QueueCapacity)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }
        Interlocked.Increment(ref _approxCount);
        _queue.Enqueue(s);   // boxes the struct; ConcurrentQueue segments amortize the allocation
        // No Set() per frame — the writer wakes on its periodic timeout and drains in bulk (avoids a syscall/frame).
    }

    /// <summary>Flush + close both files and join the writer thread. Called on exit; idempotent.</summary>
    public void Stop()
    {
        if (_thread is null)
            return;
        _stopping = true;
        _signal.Set();
        _thread.Join(2000);
        _thread = null;
        DrainOnce();
        try { _log?.Flush(); _log?.Dispose(); } catch { /* shutdown best-effort */ }
        try { _csv?.Flush(); _csv?.Dispose(); } catch { /* shutdown best-effort */ }
        _log = null; _csv = null;
    }

    private void WriterLoop()
    {
        PruneOldSessions();   // on the writer thread — retention never touches the game thread
        while (!_stopping)
        {
            _signal.WaitOne(FlushIntervalMs);
            DrainOnce();
            try { _log?.Flush(); _csv?.Flush(); } catch { /* keep draining; a transient flush error shouldn't kill the thread */ }
        }
    }

    /// <summary>(P11) Retention: keep the newest <see cref="KeepSessions"/> session pairs and delete the rest —
    /// the folder had accumulated 258 files / ~90 MB in 18 days with nothing pruning it. The stamp-named files
    /// sort chronologically by name; the pair being written right now is the newest, so it always survives.
    /// Best-effort (a locked/undeletable file is skipped silently).</summary>
    private const int KeepSessions = 50;
    private void PruneOldSessions()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrEmpty(dir))
                return;
            var stems = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string f in Directory.EnumerateFiles(dir, "session-*.*"))
            {
                string ext = Path.GetExtension(f);
                if (ext is ".log" or ".csv")
                    stems.Add(Path.GetFileNameWithoutExtension(f));
            }
            if (stems.Count <= KeepSessions)
                return;
            int toDelete = stems.Count - KeepSessions, deleted = 0;
            foreach (string stem in stems)   // ascending = oldest first
            {
                if (deleted >= toDelete)
                    break;
                try { File.Delete(Path.Combine(dir, stem + ".log")); } catch { /* best-effort */ }
                try { File.Delete(Path.Combine(dir, stem + ".csv")); } catch { /* best-effort */ }
                deleted++;
            }
            if (deleted > 0)
                WriteTextLine(new TextLine(0.0, $"[frameprofiler] pruned {deleted} old session log pair(s) (keeping newest {KeepSessions})"));
        }
        catch { /* retention is a nicety — never let it break recording */ }
    }

    private void DrainOnce()
    {
        while (_queue.TryDequeue(out object? item))
        {
            Interlocked.Decrement(ref _approxCount);
            if (item is TextLine t)
                WriteTextLine(t);
            else if (item is FrameSample s)
                WriteCsvRow(in s);
        }
    }

    private void WriteTextLine(TextLine t)
    {
        if (_log is null)
            return;
        DateTime wall = _launchWall.AddSeconds(t.SessionSeconds);
        _log.Write(wall.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        _log.Write(" (t=");
        _log.Write(t.SessionSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        _log.Write(") ");
        _log.WriteLine(Log.StripColors(t.Text));
    }

    private void WriteCsvRow(in FrameSample s)
    {
        if (_csv is null)
            return;
        var sb = _csvScratch;
        sb.Clear();
        var ci = CultureInfo.InvariantCulture;
        sb.Append(s.Frame).Append(',')
          .Append(s.TimeS.ToString("0.000", ci)).Append(',')
          .Append(s.Ms.ToString("0.00", ci)).Append(',')
          .Append(s.ProcMs.ToString("0.00", ci)).Append(',')
          .Append(s.RcpuMs.ToString("0.00", ci)).Append(',')
          .Append(s.GpuMs.ToString("0.00", ci)).Append(',')
          .Append(s.PhysMs.ToString("0.00", ci)).Append(',')
          .Append(s.RestMs.ToString("0.00", ci)).Append(',')
          .Append(s.AllocBytes / 1024).Append(',')
          .Append(s.G0).Append(',').Append(s.G1).Append(',').Append(s.G2).Append(',')
          .Append(s.GcPauseMs.ToString("0.00", ci)).Append(',')
          .Append(s.DrawCalls.ToString("0", ci)).Append(',')
          .Append(s.PipeCompiles).Append(',').Append(s.PipeUber).Append(',')
          .Append(Csv(s.Top1)).Append(',')
          .Append(s.Top1Ms.ToString("0.00", ci)).Append(',')
          .Append(s.LateMs.ToString("0.00", ci));
        _csv.WriteLine(sb.ToString());
    }

    private readonly StringBuilder _csvScratch = new(256);   // writer-thread only

    /// <summary>How many per-frame CSV rows were dropped under backpressure (0 in normal operation).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private sealed class TextLine
    {
        public readonly double SessionSeconds;
        public readonly string Text;
        public TextLine(double sessionSeconds, string text) { SessionSeconds = sessionSeconds; Text = text; }
    }
}
