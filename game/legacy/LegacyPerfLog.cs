using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace VortexArena.Game.Legacy;

/// <summary>
/// Developer aid for measuring legacy compatibility mode, as the other VORTEX_LEGACY_* variables: with the
/// environment variable VORTEX_LEGACY_PERFLOG naming a file, every frame of the legacy menu and of a legacy
/// game is recorded - when it began, how the frame's time was split, what the collector did - together
/// with one line for each thing that was loaded or built on first use. The file is written once, when the
/// process ends its session, so recording never touches the disk during a frame; a frame costs a few
/// timestamps and one array store, and nothing is allocated once the arrays exist.
///
/// An environment variable rather than a cvar, so nothing a server or a program sends can turn it on.
/// Without it every call here returns at once.
/// </summary>
public static class LegacyPerfLog
{
    private static readonly string? s_path = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_PERFLOG");
    public static readonly bool Enabled = !string.IsNullOrEmpty(s_path);

    /// <summary>What a frame was spent on. Indices into a frame's part array.</summary>
    public const int Server = 0, Receive = 1, Send = 2, Program = 3, Present = 4, Parts = 5;

    private const int MaxFrames = 1 << 20;
    private const int MaxEvents = 1 << 16;

    private struct Frame
    {
        public long Began;
        public float Total, P0, P1, P2, P3, P4;
        public long Allocated, AllocatedAll;
        public int Gc0, Gc1, Gc2;
        public float GcPause;
        public int DrawCalls, Objects;
        public byte State;
    }

    private static Frame[]? s_frames;
    private static int s_count;
    private static readonly List<(long At, string Name, float Milliseconds)> s_events = new();
    private static Frame s_current;
    private static long s_partBegan;
    private static bool s_open;
    private static readonly object s_eventLock = new();

    /// <summary>A frame of the menu (state 2), of a game still loading (0) or of a game being played (1) begins.</summary>
    public static void BeginFrame(int state)
    {
        if (!Enabled) return;
        s_frames ??= new Frame[MaxFrames];
        s_current = default;
        s_current.Began = Stopwatch.GetTimestamp();
        s_current.State = (byte)state;
        s_partBegan = s_current.Began;
        s_open = true;
    }

    /// <summary>The time since the frame began, or since the last call, belongs to <paramref name="part"/>.</summary>
    public static void Part(int part)
    {
        if (!Enabled || !s_open) return;
        long now = Stopwatch.GetTimestamp();
        float ms = (float)((now - s_partBegan) * 1000.0 / Stopwatch.Frequency);
        s_partBegan = now;
        switch (part)
        {
            case 0: s_current.P0 += ms; break;
            case 1: s_current.P1 += ms; break;
            case 2: s_current.P2 += ms; break;
            case 3: s_current.P3 += ms; break;
            case 4: s_current.P4 += ms; break;
        }
    }

    /// <summary>The state of the frame being recorded changed (the game was entered during it).</summary>
    public static void SetState(int state)
    {
        if (Enabled && s_open) s_current.State = (byte)state;
    }

    public static void EndFrame()
    {
        if (!Enabled || !s_open || s_frames is null) return;
        s_open = false;
        s_current.Total = (float)((Stopwatch.GetTimestamp() - s_current.Began) * 1000.0 / Stopwatch.Frequency);
        s_current.Allocated = GC.GetAllocatedBytesForCurrentThread();
        s_current.AllocatedAll = GC.GetTotalAllocatedBytes(false);
        s_current.Gc0 = GC.CollectionCount(0);
        s_current.Gc1 = GC.CollectionCount(1);
        s_current.Gc2 = GC.CollectionCount(2);
        s_current.GcPause = (float)GC.GetTotalPauseDuration().TotalMilliseconds;
        // Asking the renderer waits for the render thread, so it is asked once in 64 frames (0 in between).
        if ((s_count & 63) == 0) s_current.DrawCalls = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        s_current.Objects = (int)Performance.GetMonitor(Performance.Monitor.ObjectCount);
        if (s_count < MaxFrames) s_frames[s_count++] = s_current;
    }

    /// <summary>A timestamp to hand back to <see cref="Event(string, long)"/>; 0 when nothing is recorded.</summary>
    public static long Stamp() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Something was loaded or built on first use, and took from <paramref name="began"/> until now.</summary>
    public static void Event(string name, long began)
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        lock (s_eventLock)
            if (s_events.Count < MaxEvents) s_events.Add((began, name, (float)((now - began) * 1000.0 / Stopwatch.Frequency)));
    }

    /// <summary>A moment worth finding again in the file ("in the game").</summary>
    public static void Mark(string name)
    {
        if (!Enabled) return;
        lock (s_eventLock)
            if (s_events.Count < MaxEvents) s_events.Add((Stopwatch.GetTimestamp(), name, 0));
    }

    /// <summary>Writes what was recorded and starts again. Called when a session or the menu ends.</summary>
    public static void Flush()
    {
        if (!Enabled || s_frames is null) return;
        try
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            double perMs = 1000.0 / Stopwatch.Frequency;
            StringBuilder text = new(s_count * 96 + 4096);
            text.Append("F,t_ms,state,total_ms,server_ms,receive_ms,send_ms,program_ms,present_ms,alloc_main,alloc_all,gc0,gc1,gc2,gc_pause_ms,draw_calls,objects\n");
            for (int i = 0; i < s_count; i++)
            {
                ref Frame f = ref s_frames[i];
                text.Append("F,").Append((f.Began * perMs).ToString("0.000", ci)).Append(',').Append(f.State).Append(',')
                    .Append(f.Total.ToString("0.000", ci)).Append(',').Append(f.P0.ToString("0.000", ci)).Append(',')
                    .Append(f.P1.ToString("0.000", ci)).Append(',').Append(f.P2.ToString("0.000", ci)).Append(',')
                    .Append(f.P3.ToString("0.000", ci)).Append(',').Append(f.P4.ToString("0.000", ci)).Append(',')
                    .Append(f.Allocated).Append(',').Append(f.AllocatedAll).Append(',')
                    .Append(f.Gc0).Append(',').Append(f.Gc1).Append(',').Append(f.Gc2).Append(',')
                    .Append(f.GcPause.ToString("0.00", ci)).Append(',').Append(f.DrawCalls).Append(',').Append(f.Objects).Append('\n');
            }
            lock (s_eventLock)
            {
                foreach ((long at, string name, float ms) in s_events)
                    text.Append("E,").Append((at * perMs).ToString("0.000", ci)).Append(',').Append(ms.ToString("0.000", ci)).Append(',')
                        .Append(name.Replace(',', ';').Replace('\n', ' ')).Append('\n');
                s_events.Clear();
            }
            File.AppendAllText(s_path!, text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        s_count = 0;
    }
}
