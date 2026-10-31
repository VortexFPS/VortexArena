// One thread for the sample decodes that drive the engine's Vorbis decoder outside its audio server.
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace VortexArena.Engine.Audio;

/// <summary>
/// Runs jobs one at a time on a single thread of its own, urgent jobs before background ones. A job that
/// is already running finishes first: an urgent job waits for at most one background job.
///
/// Decoding a compressed sample goes through engine objects (a Vorbis stream, a playback of it, the
/// playback's resampler) that were not written to be driven from several threads at once. Decodes running
/// side by side on pool threads are the likely cause of two native crashes on 2026-10-31: an access violation
/// inside a decode, and one in mesh code on the main thread, the way corrupted native memory surfaces
/// wherever it is next touched. Every such decode goes through one queue, so at most one runs at any moment.
/// </summary>
public sealed class SerialDecodeQueue
{
    private readonly BlockingCollection<Action> _urgent = new();
    private readonly BlockingCollection<Action> _background = new();
    private readonly BlockingCollection<Action>[] _queues;
    private readonly string _name;
    private readonly Action<Exception>? _failed;
    private int _started;

    /// <param name="name">The thread's name.</param>
    /// <param name="failed">Told of an exception a job threw; the thread carries on with the next job.</param>
    public SerialDecodeQueue(string name, Action<Exception>? failed = null)
    {
        _name = name;
        _failed = failed;
        _queues = new[] { _urgent, _background };   // TakeFromAny tries them in this order
    }

    /// <summary>
    /// Queue <paramref name="job"/>. <paramref name="urgent"/>: something is waiting to hear it (a channel
    /// that is to play the sample now); it runs at normal priority, ahead of every background job queued.
    /// Otherwise it runs below normal priority when nothing urgent is waiting.
    /// </summary>
    public void Post(Action job, bool urgent)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureStarted();
        (urgent ? _urgent : _background).Add(job);
    }

    private void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        new Thread(Run) { IsBackground = true, Name = _name, Priority = ThreadPriority.BelowNormal }.Start();
    }

    private void Run()
    {
        Thread self = Thread.CurrentThread;
        while (true)
        {
            int from = BlockingCollection<Action>.TakeFromAny(_queues, out Action? job);
            ThreadPriority priority = from == 0 ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
            if (self.Priority != priority) self.Priority = priority;
            try { job!(); }
            catch (Exception e) when (e is not OutOfMemoryException) { _failed?.Invoke(e); }
        }
    }
}
