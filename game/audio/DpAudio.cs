using System;
using System.IO;
using System.Threading;
using Godot;
using VortexArena.Engine.Audio;

namespace VortexArena.Game.Audio;

/// <summary>
/// The Godot end of DarkPlaces' sound system (<see cref="DpSoundSystem"/>, src/VortexArena.Engine/Audio):
/// one node for the process, holding the channel table and one <see cref="AudioStreamPlayer"/> on the Master
/// bus that plays what the mixer paints.
/// Every sound of the native game and of legacy compatibility mode is a channel in that table; the engine
/// sees a single stereo stream at its own rate, so none of its spatialisation (panning law, distance
/// low-pass, Doppler, reverb, attenuation curves, polyphony limits, resampler) is involved.
///
/// The mixer is called by the engine's audio thread, as DarkPlaces' is called by the device
/// (snd_sdl.c Buffer_Callback, "snd_usethreadedmixing"): <see cref="DpMixerPlayback"/> is the stream, and
/// the audio server asks it for each block (512 frames) at the moment it mixes that block. Nothing is queued
/// in between, so a sound started now is in the very next block the server mixes. A stalled game frame does
/// not starve it either: the audio thread is not the game's.
///
/// The previous arrangement is kept as the fallback (<see cref="Direct"/> false): an
/// <see cref="AudioStreamGenerator"/> fed by a thread of our own that keeps <see cref="TargetFrames"/> frames
/// (32 ms and more) queued ahead of the audio server. It is used when the engine turns out not to call a C#
/// stream's mixer (checked at start: no call within two seconds), and when VORTEX_AUDIO_QUEUE=1 asks for it
/// (the other arm of a latency comparison).
///
/// Why a mixer of our own rather than AudioStreamPlayer3D nodes with everything switched off: DarkPlaces'
/// output differs from the engine's in things a node cannot be configured into - the stereo law (each ear
/// 0.5 + 0.5 * the source's direction towards it, so a sound straight ahead is 6 dB below one at an ear),
/// linear-interpolation resampling, the limiter Xonotic turns on (snd_softclip), the under-water filter,
/// the start offset given to identical sounds begun together, the sharing of one voice between static
/// sounds of one sample, and sample-exact loop points.
///
/// <c>snd_darkplaces 0</c> (native game only, read when the game starts) keeps the previous engine-node
/// path for one release.
/// </summary>
public sealed partial class DpAudio : Node
{
    private static DpAudio? s_instance;
    private static readonly string? s_capturePath = NonEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_CAPTURE"));
    private static readonly string? s_dumpPath = NonEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_MIXDUMP"));
    /// <summary>VORTEX_AUDIO_ENGINE_NODES=1: both stacks play through engine nodes as they did before (the other arm of a comparison).</summary>
    public static readonly bool ForceEngineNodes = NonEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_ENGINE_NODES")) is not null;

    /// <summary>
    /// VORTEX_AUDIO_OFFLINE=&lt;frames&gt;: nothing is played; the game mixes that many frames each time its clock
    /// advances (<see cref="MixOffline"/>) into the VORTEX_AUDIO_MIXDUMP file. With a recording stepped one
    /// message a frame this is what DarkPlaces' own capture does (cl_capturevideo: a fixed number of frames of
    /// sound per picture), so the two files can be compared sample for sample.
    /// </summary>
    public static readonly int OfflineFrames = int.TryParse(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_OFFLINE"), out int offline) && offline > 0 ? offline : 0;

    /// <summary>
    /// VORTEX_AUDIO_QUEUE=1 (or a queue depth in frames): the queued generator instead of mixing on the engine's
    /// audio thread. VORTEX_AUDIO_QUEUE=fallback behaves like an engine that never calls the stream, to exercise
    /// the check that then returns to the queue.
    /// </summary>
    private static readonly bool s_noCallback = System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_QUEUE") is "fallback";
    private static readonly bool s_forceQueue = !s_noCallback && NonEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_QUEUE")) is not null;
    /// <summary>
    /// VORTEX_AUDIO_SILENT=1: everything is mixed and measured as usual, but what is handed to the engine is
    /// silence and the Master bus is muted. For timing a real output device without making a sound.
    /// </summary>
    private static readonly bool s_silent = NonEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_SILENT")) is not null;
    /// <summary>
    /// VORTEX_AUDIO_LATENCY_TEST=1: once a second a click is started on the mixer (right side only) and the
    /// same click on a plain engine player (left side only) in the same instant. In a capture of the Master
    /// bus the distance between the two is what this path adds to the engine's own shortest path.
    /// </summary>
    private static readonly bool s_latencyTest = NonEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_LATENCY_TEST")) is not null;

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>The process's sound system, attached to the scene tree's root on first use.</summary>
    public static DpAudio Instance
    {
        get
        {
            if (s_instance is { } existing && IsInstanceValid(existing)) return existing;
            DpAudio made = new() { Name = "DpAudio", ProcessMode = ProcessModeEnum.Always };
            s_instance = made;
            if (Godot.Engine.GetMainLoop() is SceneTree tree) tree.Root.CallDeferred(Node.MethodName.AddChild, made);
            return made;
        }
    }

    /// <summary>Start the capture of the Master bus if a run asked for one, whichever path plays the sound.</summary>
    public static void EnsureCapture()
    {
        if (s_capturePath is not null) _ = Instance;
    }

    public DpSoundSystem Sound { get; } = new();
    /// <summary>The audio server's rate: what the mixer paints at ("snd_speed").</summary>
    public int MixRate { get; private set; } = 48000;
    /// <summary>
    /// snd_blocked: the window is inactive and snd_mutewhenidle says to be silent. Nothing is mixed and
    /// nothing advances, as in DarkPlaces (its callback writes silence without calling the mixer).
    /// </summary>
    public volatile bool Blocked;
    /// <summary>The process frame on which a game (legacy session or native client) last ran the per-frame update; a menu behind it leaves the listener alone.</summary>
    public ulong GameFrame { get; set; } = ulong.MaxValue;
    public bool GameIsUpdating => GameFrame != ulong.MaxValue && Godot.Engine.GetProcessFrames() - GameFrame <= 2;

    // What the audio thread did, for the cost report: calls, frames and ticks spent inside the mixer.
    private long _mixCalls, _mixFrames, _mixTicks, _mixWorstTicks;
    public long MixCalls => Interlocked.Read(ref _mixCalls);
    public long MixFrames => Interlocked.Read(ref _mixFrames);
    public double MixSeconds => Interlocked.Read(ref _mixTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
    public double MixWorstMilliseconds => Interlocked.Read(ref _mixWorstTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private AudioStreamPlayer? _player, _clock;

    /// <summary>
    /// Seconds of sound the audio server has mixed since the capture began, or -1 when no capture is running.
    /// A test scene keeps time by this rather than by the wall clock: the engine's Dummy driver (no device)
    /// takes its frames more slowly than real time, and what is being recorded is the server's time.
    /// </summary>
    public double CaptureClock => _clock is { Playing: true } clock ? clock.GetPlaybackPosition() : -1;
    private AudioStreamGeneratorPlayback? _playback;
    private AudioEffectRecord? _record;
    private Stream? _dump;
    private byte[] _dumpBytes = Array.Empty<byte>();
    private readonly object _dumpLock = new();
    private System.Threading.Thread? _thread;
    private volatile bool _running;
    private const int Block = 256;
    private int _capacity;
    private long _underruns;

    /// <summary>
    /// Frames kept queued ahead of the audio server. It takes its input in bursts of a device period
    /// (about 1024 frames at the default 15 ms output latency), so less than that under-runs; the target
    /// starts one block above a period and grows by a block whenever the server reports a skip.
    /// </summary>
    public int TargetFrames { get; private set; } = BaseTargetFrames;
    /// <summary>Times the audio server found the queue empty.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);
    public int QueueCapacity => _capacity;
    private long _loopLast, _loopGapTicks;
    /// <summary>The longest the mixer thread went between two passes since this was last read, in milliseconds.</summary>
    public double TakeWorstLoopGapMilliseconds() => Interlocked.Exchange(ref _loopGapTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public override void _Ready()
    {
        MixRate = Math.Max(8000, (int)MathF.Round(AudioServer.GetMixRate()));
        if (s_capturePath is not null)
        {
            _record = new AudioEffectRecord { Format = AudioStreamWav.FormatEnum.Format16Bits };
            AudioServer.AddBusEffect(0, _record);
            _record.SetRecordingActive(true);
        }
        if (s_capturePath is not null)
        {
            // A capture's clock: a stream nobody feeds, whose position is how much the audio server has mixed.
            _clock = new AudioStreamPlayer { Name = "DpClock", Bus = "Master", Stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.05f }, ProcessMode = ProcessModeEnum.Always };
            AddChild(_clock);
            _clock.Play();
        }
        if (ForceEngineNodes) return;
        if (s_dumpPath is not null)
        {
            try { _dump = new BufferedStream(new FileStream(s_dumpPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read), 1 << 16); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _dump = null; }
        }
        if (s_silent)
        {
            // Three ways silent: the mixer hands over zeros (MixInto), the Master bus ends in an 80 dB cut
            // (after the capture's recorder, so a capture still sees engine-node sounds), and the bus is
            // muted - again every frame, because applying the audio settings un-mutes it.
            AudioServer.AddBusEffect(0, new AudioEffectAmplify { VolumeDb = -80f });
            AudioServer.SetBusMute(0, true);
        }
        if (s_latencyTest) MakeLatencyTest();
        if (OfflineFrames > 0 || s_forceQueue) StartQueue();
        else StartDirect();
    }

    /// <summary>True while the engine's audio thread calls the mixer itself; false on the queued generator.</summary>
    public bool Direct { get; private set; }
    private DpMixerStream? _directStream;
    private bool _directConfirmed;
    private long _directStarted, _directCalls, _directLast, _directGapTicks, _directLate25, _directLate50;
    /// <summary>Calls the engine's audio thread has made to the mixer.</summary>
    public long DirectCalls => Interlocked.Read(ref _directCalls);

    // The stream is the mixer: the audio server calls DpMixerPlayback._mix for each block it mixes.
    private void StartDirect()
    {
        _directStream = new DpMixerStream { Owner = this };
        _player = new AudioStreamPlayer { Name = "DpMixer", Bus = "Master", Stream = _directStream, ProcessMode = ProcessModeEnum.Always, VolumeDb = 0 };
        AddChild(_player);
        Direct = true;
        _directStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _player.Play();
    }

    /// <summary>
    /// The audio thread's call: paint <paramref name="frames"/> stereo frames into the server's own buffer.
    /// Nothing here allocates; a collection that stops managed threads holds this call up for its length,
    /// and the output device then plays what it still has buffered.
    /// </summary>
    internal int MixDirect(IntPtr buffer, int frames)
    {
        if (frames <= 0 || buffer == IntPtr.Zero || s_noCallback) return 0;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long last = _directLast;
        _directLast = now;
        if (last != 0)
        {
            long gap = now - last, second = System.Diagnostics.Stopwatch.Frequency;
            if (gap > Interlocked.Read(ref _directGapTicks)) Interlocked.Exchange(ref _directGapTicks, gap);
            if (gap > second / 40) Interlocked.Increment(ref _directLate25);
            if (gap > second / 20) Interlocked.Increment(ref _directLate50);
        }
        if (_directFirstCount < _directFirst.Length) _directFirst[_directFirstCount++] = now;
        Interlocked.Increment(ref _directCalls);
        // Painted into an array of our own and copied across: the project builds without unsafe code.
        for (int done = 0; done < frames;)
        {
            int block = Math.Min(frames - done, DirectBlock);
            MixInto(_directBuffer, block, 0);
            System.Runtime.InteropServices.Marshal.Copy(_directBuffer, 0, buffer + done * 2 * sizeof(float), block * 2);
            done += block;
        }
        return frames;
    }

    private const int DirectBlock = 2048;
    // When the audio thread's first calls came (ticks): how the output takes its frames - a burst that fills
    // the device's buffer, then one call a period.
    private readonly long[] _directFirst = new long[96];
    private volatile int _directFirstCount;
    private bool _directFirstTold;
    private readonly float[] _directBuffer = new float[DirectBlock * 2];

    // The engine did not call the stream (an engine whose script dispatch does not reach a C# "_mix"): the queue.
    private void FallBackToQueue()
    {
        Direct = false;
        if (_player is { } old)
        {
            old.Stop();
            old.QueueFree();
            _player = null;
        }
        _directStream = null;
        GD.Print("[audio] the engine does not call a C# stream's mixer; using the queued generator (about 32 ms more latency)");
        StartQueue();
    }

    private void StartQueue()
    {
        // The generator runs at the server's own rate: its resampler then steps exactly one frame at a time
        // and hands the mixer's frames on unchanged.
        AudioStreamGenerator generator = new() { MixRate = MixRate, BufferLength = 0.2f };
        _player = new AudioStreamPlayer { Name = "DpMixerQueue", Bus = "Master", Stream = generator, ProcessMode = ProcessModeEnum.Always, VolumeDb = 0 };
        AddChild(_player);
        _player.Play();
        _playback = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (_playback is null || OfflineFrames > 0) return;
        _capacity = _playback.GetFramesAvailable();
        _running = true;
        _thread = new System.Threading.Thread(MixLoop) { IsBackground = true, Name = "DpMixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // ---------------------------------------------------------------------------------------------------
    //  Measuring (VORTEX_AUDIO_TRACE, VORTEX_AUDIO_LATENCY_TEST)
    // ---------------------------------------------------------------------------------------------------

    private long _probeSeen, _probeCount, _probeSumTicks, _probeMaxTicks;
    private long _gcPauseTicksSeen;
    private int _gc0Seen, _gc1Seen, _gc2Seen;

    /// <summary>
    /// What the output path did since this was last called, for the once-a-second trace line: which path,
    /// the longest wait between two calls of the audio thread and how many exceeded 25 and 50 ms, the time
    /// from a sound's start to the block that first carries it reaching the audio server (count, mean, worst),
    /// the queue's under-runs and depth, and the collections the runtime ran with their total pause.
    /// </summary>
    public string TraceText()
    {
        double perMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        long n = Interlocked.Exchange(ref _probeCount, 0), sum = Interlocked.Exchange(ref _probeSumTicks, 0), worst = Interlocked.Exchange(ref _probeMaxTicks, 0);
        long gap = Interlocked.Exchange(ref _directGapTicks, 0), late25 = Interlocked.Exchange(ref _directLate25, 0), late50 = Interlocked.Exchange(ref _directLate50, 0);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long pause = GC.GetTotalPauseDuration().Ticks;
        string text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"path {(Direct ? "direct" : "queue")} calls {DirectCalls} gapmax {gap * perMs:0.0} late25 {late25} late50 {late50} latency n {n} mean {(n > 0 ? sum * perMs / n : 0):0.0} max {worst * perMs:0.0} underruns {Underruns} target {TargetFrames} of {QueueCapacity} loopgap {TakeWorstLoopGapMilliseconds():0.0} mix ms {MixSeconds * 1000:0.0} worst {MixWorstMilliseconds:0.00} gc {g0 - _gc0Seen}/{g1 - _gc1Seen}/{g2 - _gc2Seen} pause {(pause - _gcPauseTicksSeen) / (double)TimeSpan.TicksPerMillisecond:0.00}");
        if (!_directFirstTold && _directFirstCount == _directFirst.Length)
        {
            _directFirstTold = true;
            System.Text.StringBuilder first = new(" first calls at ms");
            for (int i = 0; i < _directFirst.Length; i++)
                first.Append(' ').Append(((_directFirst[i] - _directFirst[0]) * perMs).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            text += first.ToString();
        }
        _gc0Seen = g0;
        _gc1Seen = g1;
        _gc2Seen = g2;
        _gcPauseTicksSeen = pause;
        return text;
    }

    private AudioStreamPlayer? _clickPlayer;
    private DpSfx? _clickSfx;
    private long _clickNext;

    private void MakeLatencyTest()
    {
        // 256 frames at half scale: on the left for the engine's player, on the right for the mixer.
        const int length = 256;
        short[] right = new short[length * 2];
        byte[] left = new byte[length * 4];
        for (int i = 0; i < length; i++)
        {
            right[i * 2 + 1] = 16384;
            left[i * 4 + 1] = 0x40;
        }
        _clickSfx = new DpSfx("*latencyclick", right, 2, MixRate);
        AudioStreamWav wav = new() { Format = AudioStreamWav.FormatEnum.Format16Bits, Stereo = true, MixRate = MixRate, Data = left };
        _clickPlayer = new AudioStreamPlayer { Name = "DpLatencyClick", Bus = "Master", Stream = wav, ProcessMode = ProcessModeEnum.Always, VolumeDb = 0 };
        AddChild(_clickPlayer);
        _clickNext = System.Diagnostics.Stopwatch.GetTimestamp() + 3 * System.Diagnostics.Stopwatch.Frequency;
    }

    // VORTEX_AUDIO_QUEUE=<frames> (256 and up) sets the depth the queue starts from, for measuring how short it can be.
    private static readonly int BaseTargetFrames = int.TryParse(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_QUEUE"), out int asked) && asked >= 256 ? asked : 1536;
    // An output that takes its frames in large bites needs more queued than one bite: the engine's Dummy driver
    // (no device; headless runs and captures) takes 4096 at a time.
    private const int MaxTargetFrames = 12288;

    // Windows' Sleep(1) lasts a whole scheduler tick (15.6 ms) unless something in the process has asked for a
    // finer one, and a mixer that wakes that rarely has to keep three times as much queued. A high-resolution
    // waitable timer (Windows 10 1803 and later) wakes on time without changing the system's timer.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr argument, int resume);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int CloseHandle(IntPtr handle);

    private static IntPtr CreatePreciseTimer()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        try { return CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return IntPtr.Zero; }
    }

    private void MixLoop()
    {
        float[] mixed = new float[Block * 2];
        Vector2[] frames = new Vector2[Block];
        AudioStreamGeneratorPlayback playback = _playback!;
        IntPtr timer = CreatePreciseTimer();
        int lastSkips = -1, floor = BaseTargetFrames;
        long quietSince = System.Diagnostics.Stopwatch.GetTimestamp(), floorSince = quietSince;
        long second = System.Diagnostics.Stopwatch.Frequency;
        while (_running)
        {
            try
            {
                int guard = 64;
                long loopNow = System.Diagnostics.Stopwatch.GetTimestamp();
                long gapTicks = _loopLast != 0 ? loopNow - _loopLast : 0;
                if (gapTicks > Interlocked.Read(ref _loopGapTicks)) Interlocked.Exchange(ref _loopGapTicks, gapTicks);
                _loopLast = loopNow;
                // The queue's size is the most free space ever seen (the ring is empty when the player has just started or has run dry).
                int free = playback.GetFramesAvailable();
                if (free > _capacity) _capacity = free;
                int queued;
                while (_running && guard-- > 0 && (queued = _capacity - playback.GetFramesAvailable()) < TargetFrames && playback.CanPushBuffer(Block))
                {
                    MixInto(mixed, Block, queued);
                    for (int i = 0; i < Block; i++) frames[i] = new Vector2(mixed[i * 2], mixed[i * 2 + 1]);
                    playback.PushBuffer(frames);
                }
                // The server found the queue empty. If this thread was running on time, the queue is too short
                // for the bites the output takes: keep a block more, and do not come back below that for two
                // minutes. If this thread itself was held up (a collection, a machine out of cores during a
                // level load) a longer queue is not the cure, and nothing changes. Skips before the first fill
                // (the player starts before this thread does) are not counted.
                int skips = playback.GetSkips();
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (lastSkips < 0) lastSkips = skips;
                else if (skips != lastSkips)
                {
                    Interlocked.Add(ref _underruns, Math.Max(1, skips - lastSkips));
                    lastSkips = skips;
                    quietSince = now;
                    if (gapTicks < second / 50 && TargetFrames < _capacity - 2 * Block && TargetFrames < MaxTargetFrames)
                    {
                        TargetFrames += Block;
                        floor = TargetFrames;
                        floorSince = now;
                    }
                }
                else
                {
                    if (floor > BaseTargetFrames && now - floorSince > 120 * second) floor = BaseTargetFrames;
                    if (TargetFrames > floor && now - quietSince > 3 * second)
                    {
                        TargetFrames -= Block;
                        quietSince = now;
                    }
                }
            }
            catch (ObjectDisposedException) { break; }
            if (timer != IntPtr.Zero)
            {
                long due = -10000;   // 1 ms, relative, in 100 ns units
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, 0) == 0 || WaitForSingleObject(timer, 100) != 0) System.Threading.Thread.Sleep(1);
            }
            else System.Threading.Thread.Sleep(1);
        }
        if (timer != IntPtr.Zero) CloseHandle(timer);
    }

    private float[] _offline = Array.Empty<float>();

    /// <summary>Offline capture: mix <paramref name="frames"/> frames now, on the caller's thread, into the dump.</summary>
    public void MixOffline(int frames)
    {
        if (OfflineFrames <= 0 || frames <= 0) return;
        if (_offline.Length < frames * 2) _offline = new float[frames * 2];
        MixInto(_offline, frames, 0);
    }

    // queuedAhead: frames already waiting between this block and the audio server (0 when the server itself asks).
    private void MixInto(Span<float> output, int frames, int queuedAhead)
    {
        if (Blocked)
        {
            output.Clear();
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // A sound started since the last block is in this one: from its start to here, plus what is queued ahead.
        long started = Sound.LastStartTicks;
        if (started != _probeSeen)
        {
            _probeSeen = started;
            long waited = t0 - started + queuedAhead * System.Diagnostics.Stopwatch.Frequency / MixRate;
            if (started != 0 && waited >= 0)
            {
                Interlocked.Increment(ref _probeCount);
                Interlocked.Add(ref _probeSumTicks, waited);
                if (waited > Interlocked.Read(ref _probeMaxTicks)) Interlocked.Exchange(ref _probeMaxTicks, waited);
            }
        }
        Sound.Mix(output, frames, MixRate);
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        Interlocked.Increment(ref _mixCalls);
        Interlocked.Add(ref _mixFrames, frames);
        Interlocked.Add(ref _mixTicks, ticks);
        if (ticks > Interlocked.Read(ref _mixWorstTicks)) Interlocked.Exchange(ref _mixWorstTicks, ticks);
        if (_dump is { } dump)
        {
            int bytes = frames * 2 * sizeof(float);
            if (_dumpBytes.Length < bytes) _dumpBytes = new byte[bytes];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(output[..(frames * 2)]).CopyTo(_dumpBytes);
            lock (_dumpLock) dump.Write(_dumpBytes, 0, bytes);
        }
        if (s_silent) output[..(frames * 2)].Clear();
    }

    public override void _ExitTree()
    {
        _running = false;
        _thread?.Join(500);
        SaveCapture();
    }

    /// <summary>Write what a capture run recorded. Safe to call more than once.</summary>
    public void SaveCapture()
    {
        if (_dump is { } dump)
            lock (_dumpLock) dump.Flush();
        if (_record is null || s_capturePath is null) return;
        try
        {
            if (_record.GetRecording() is { } recording) recording.SaveToWav(s_capturePath);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) SaveCapture();
    }

    // The mixer's own dump is flushed every few seconds (cheap). The recording of the Master bus is written
    // when the tree goes down: writing a growing WAV file during the run stalls the frame it is written in.
    private double _sinceSave;
    public override void _Process(double delta)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (s_silent && !AudioServer.IsBusMute(0)) AudioServer.SetBusMute(0, true);
        if (Direct && !_directConfirmed)
        {
            if (DirectCalls > 0) _directConfirmed = true;
            else if (now - _directStarted > 2 * System.Diagnostics.Stopwatch.Frequency) FallBackToQueue();
        }
        if (_clickPlayer is { } click && _clickSfx is { } sfx && now >= _clickNext)
        {
            _clickNext = now + System.Diagnostics.Stopwatch.Frequency;
            click.Play();
            Sound.StartSound(DpSoundSystem.MaxEdicts, 0, sfx, System.Numerics.Vector3.Zero, 1f, 0f, 0f, DpSoundSystem.ChannelFlagFullVolume, 1f);
        }
        if (_dump is null) return;
        _sinceSave += delta;
        if (_sinceSave < 2.0) return;
        _sinceSave = 0;
        lock (_dumpLock) _dump.Flush();
    }
}
