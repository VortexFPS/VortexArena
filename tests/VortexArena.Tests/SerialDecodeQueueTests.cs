// SerialDecodeQueue (src/VortexArena.Engine/Audio): every engine Vorbis decode runs on one thread, one at a
// time, urgent ones first.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using VortexArena.Engine.Audio;
using Xunit;

namespace VortexArena.Tests;

public class SerialDecodeQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Jobs_from_many_threads_run_on_one_thread_never_two_at_once()
    {
        var queue = new SerialDecodeQueue("test-decode");
        var threads = new ConcurrentDictionary<int, bool>();
        int running = 0, overlaps = 0, done = 0;
        const int Posters = 8, PerPoster = 50;
        using var all = new CountdownEvent(Posters * PerPoster);

        var posters = new List<Thread>();
        for (int p = 0; p < Posters; p++)
        {
            bool urgent = p % 2 == 0;
            var poster = new Thread(() =>
            {
                for (int i = 0; i < PerPoster; i++)
                    queue.Post(() =>
                    {
                        if (Interlocked.Increment(ref running) != 1) Interlocked.Increment(ref overlaps);
                        threads[Environment.CurrentManagedThreadId] = true;
                        Thread.SpinWait(200);
                        Interlocked.Decrement(ref running);
                        Interlocked.Increment(ref done);
                        all.Signal();
                    }, urgent);
            });
            posters.Add(poster);
            poster.Start();
        }
        foreach (Thread poster in posters) poster.Join();

        Assert.True(all.Wait(Wait), $"only {Volatile.Read(ref done)} of {Posters * PerPoster} jobs ran");
        Assert.Equal(0, overlaps);
        Assert.Single(threads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads.Keys);
    }

    [Fact]
    public void Urgent_jobs_run_before_background_jobs_already_queued()
    {
        var queue = new SerialDecodeQueue("test-decode");
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var all = new CountdownEvent(5);
        var order = new ConcurrentQueue<string>();

        // Hold the thread on a first job so the rest are all queued before any of them can be taken.
        queue.Post(() => { started.Set(); gate.Wait(); all.Signal(); }, urgent: false);
        Assert.True(started.Wait(Wait));
        queue.Post(() => { order.Enqueue("bg1"); all.Signal(); }, urgent: false);
        queue.Post(() => { order.Enqueue("bg2"); all.Signal(); }, urgent: false);
        queue.Post(() => { order.Enqueue("now1"); all.Signal(); }, urgent: true);
        queue.Post(() => { order.Enqueue("now2"); all.Signal(); }, urgent: true);
        gate.Set();

        Assert.True(all.Wait(Wait));
        Assert.Equal(new[] { "now1", "now2", "bg1", "bg2" }, order.ToArray());
    }

    [Fact]
    public void A_job_that_throws_is_reported_and_the_next_job_still_runs()
    {
        var failures = new ConcurrentQueue<Exception>();
        var queue = new SerialDecodeQueue("test-decode", failures.Enqueue);
        using var after = new ManualResetEventSlim();

        queue.Post(() => throw new InvalidOperationException("bad file"), urgent: true);
        queue.Post(after.Set, urgent: false);

        Assert.True(after.Wait(Wait));
        Exception failure = Assert.Single(failures);
        Assert.Equal("bad file", failure.Message);
    }
}
