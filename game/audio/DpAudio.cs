using System;
using System.IO;
using System.Threading;
using Godot;
using VortexArena.Engine.Audio;

namespace VortexArena.Game.Audio;

/// <summary>
/// The Godot end of DarkPlaces' sound system (<see cref="DpSoundSystem"/>, src/VortexArena.Engine/Audio):
/// one node for the process, holding the channel table and one <see cref="AudioStreamPlayer"/> on the Master
/// bus that plays what the mixer paints (an <see cref="AudioStreamGenerator"/> fed by a thread of its own).
/// Every sound of the native game and of legacy compatibility mode is a channel in that table; the engine
/// sees a single stereo stream at its own rate, so none of its spatialisation (panning law, distance
/// low-pass, Doppler, reverb, attenuation curves, polyphony limits, resampler) is involved.
///
/// The mixer runs on its own thread, not in the game's frame: a stalled frame does not starve it. It keeps
/// <see cref="TargetFrames"/> frames queued ahead of the audio server, which is latency DarkPlaces does not
/// have (its mixer is called by the device); the engine does not let a C# class be the stream itself
/// (AudioStreamPlayback._mix is not bound for C#), which is what would remove it.
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
        // The generator runs at the server's own rate: its resampler then steps exactly one frame at a time
        // and hands the mixer's frames on unchanged.
        AudioStreamGenerator generator = new() { MixRate = MixRate, BufferLength = 0.2f };
        _player = new AudioStreamPlayer { Name = "DpMixer", Bus = "Master", Stream = generator, ProcessMode = ProcessModeEnum.Always, VolumeDb = 0 };
        AddChild(_player);
        _player.Play();
        _playback = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (_playback is null || OfflineFrames > 0) return;
        _capacity = _playback.GetFramesAvailable();
        _running = true;
        _thread = new System.Threading.Thread(MixLoop) { IsBackground = true, Name = "DpMixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private const int BaseTargetFrames = 1536;
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
                while (_running && guard-- > 0 && _capacity - playback.GetFramesAvailable() < TargetFrames && playback.CanPushBuffer(Block))
                {
                    MixInto(mixed, Block);
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
        MixInto(_offline, frames);
    }

    private void MixInto(Span<float> output, int frames)
    {
        if (Blocked)
        {
            output.Clear();
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
        if (_dump is null) return;
        _sinceSave += delta;
        if (_sinceSave < 2.0) return;
        _sinceSave = 0;
        lock (_dumpLock) _dump.Flush();
    }
}
