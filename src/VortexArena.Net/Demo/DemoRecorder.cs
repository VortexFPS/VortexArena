using System;
using System.Collections.Generic;
using System.IO;

namespace VortexArena.Net.Demo;

/// <summary>
/// The server's per-tick demo tap (planning/specs/demo-replay-and-spectator.md §4): <c>ServerNet</c> exposes a
/// single optional <c>DemoSink</c> and, when one is installed, hands it the data it already assembled each
/// tick — the omniscient entity set right after <c>BuildEntitySet</c>, and each broadcast event bundle in its
/// exact wire framing. Null sink = not recording; the live path pays one null check.
/// </summary>
public interface IDemoSink
{
    /// <summary>Record this tick's full networked entity set (called once per advanced server frame, right
    /// after the set is built and before the per-client encode). The dictionary is the server's reused
    /// scratch — copy what you keep.</summary>
    void RecordTick(float serverTime, IReadOnlyDictionary<int, NetEntityState> entities);

    /// <summary>Record a broadcast server→client event packet (effect/sound/notification bundle) verbatim,
    /// including its leading NetControl byte. <paramref name="reliable"/> mirrors the channel it went out on
    /// so playback re-broadcasts it identically.</summary>
    void RecordEventPacket(ReadOnlySpan<byte> packet, bool reliable);
}

/// <summary>
/// Records a match to a <c>.xgd</c> demo file (T62, planning/specs/demo-replay-and-spectator.md §4-5): one
/// entity frame per server tick (72 Hz — a keyframe with the FULL state every
/// <see cref="DefaultKeyframeIntervalSeconds"/> so playback can seek, a delta against the previous frame
/// otherwise), plus every broadcast event bundle verbatim. The file is self-contained (map/gametype/roster in
/// the header) and playable without a live server. Pure C# — no Godot types; the Godot host only opens the
/// stream and wires the sink.
/// </summary>
public sealed class DemoRecorder : IDemoSink, IDisposable
{
    /// <summary>Keyframe cadence in seconds (T62 acceptance: a full-state keyframe every 5 s = every 360
    /// ticks at 72 Hz). Denser keyframes = faster seeks but a larger file.</summary>
    public const float DefaultKeyframeIntervalSeconds = 5f;

    private readonly DemoFormat.Writer _writer;
    private readonly DemoHeaderInfo _header; // live: roster catch-up appends here, serialized in the Finish trailer
    private readonly float _keyframeInterval;
    private readonly float _tickRate;

    // The previous frame's entity set — the delta baseline (reused; RecordTick copies into it after encoding).
    private readonly Dictionary<int, NetEntityState> _previous = new();

    private bool _hasFrame;               // a first (key)frame was written — event packets need a time anchor
    private float _lastKeyframeTime;
    private float _lastServerTime;
    private uint _lastTick;
    private bool _stopped;

    /// <summary>Where this demo is being written (diagnostics/console feedback); "" for a bare stream.</summary>
    public string Path { get; }

    /// <summary>True until <see cref="Stop"/> finalizes the file.</summary>
    public bool Recording => !_stopped;

    /// <summary>Frames written so far.</summary>
    public uint FrameCount => _writer.FrameCount;

    /// <summary>Seconds of match time captured so far.</summary>
    public float DurationSeconds => _writer.DurationSeconds;

    public DemoRecorder(Stream stream, DemoHeaderInfo header,
        float keyframeIntervalSeconds = DefaultKeyframeIntervalSeconds, bool leaveOpen = false, string path = "")
    {
        if (header is null) throw new ArgumentNullException(nameof(header));
        _keyframeInterval = keyframeIntervalSeconds > 0f ? keyframeIntervalSeconds : DefaultKeyframeIntervalSeconds;
        header.KeyframeIntervalSeconds = _keyframeInterval;
        _tickRate = header.TickRate > 0f ? header.TickRate : 72f;
        _header = header;
        _writer = new DemoFormat.Writer(stream, header, leaveOpen);
        Path = path;
    }

    /// <summary>
    /// Roster catch-up (spec §5): add a client who connected AFTER recording started. The header roster was
    /// written before the first frame and cannot grow, so late entries accumulate here and are serialized as
    /// the FINAL roster in the file trailer at <see cref="Stop"/> (a crash falls back to the header roster).
    /// Deduplicated by net id — reconnects and the record-start players are ignored.
    /// </summary>
    public void AddRosterEntry(DemoRosterEntry entry)
    {
        if (_stopped)
            return;
        for (int i = 0; i < _header.Roster.Count; i++)
            if (_header.Roster[i].NetId == entry.NetId)
                return;
        _header.Roster.Add(entry);
    }

    /// <summary>Open <paramref name="path"/> for writing (creating its directory) and start a recorder on it.</summary>
    public static DemoRecorder CreateFile(string path, DemoHeaderInfo header,
        float keyframeIntervalSeconds = DefaultKeyframeIntervalSeconds)
    {
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        return new DemoRecorder(stream, header, keyframeIntervalSeconds, leaveOpen: false, path: path);
    }

    /// <inheritdoc />
    public void RecordTick(float serverTime, IReadOnlyDictionary<int, NetEntityState> entities)
    {
        if (_stopped)
            return;

        // Tick number derived from the recorded server clock (BroadcastSnapshots runs once per advanced host
        // frame, which may span several 72 Hz sim ticks on a catch-up frame — the timestamp, not a frame
        // counter, is the authoritative timeline).
        uint tick = (uint)Math.Max(0, (long)MathF.Round(serverTime * _tickRate));

        bool keyframe = !_hasFrame || serverTime - _lastKeyframeTime >= _keyframeInterval;
        if (keyframe)
        {
            _writer.WriteKeyframe(tick, serverTime, entities);
            _lastKeyframeTime = serverTime;
        }
        else
        {
            _writer.WriteSnapshotDelta(tick, serverTime, entities, _previous);
        }

        _previous.Clear();
        foreach (KeyValuePair<int, NetEntityState> kv in entities)
            _previous[kv.Key] = kv.Value;

        _hasFrame = true;
        _lastServerTime = serverTime;
        _lastTick = tick;
    }

    /// <inheritdoc />
    public void RecordEventPacket(ReadOnlySpan<byte> packet, bool reliable)
    {
        // Events are flushed after the snapshot each tick, so the last recorded tick is their timestamp. A
        // packet arriving before any entity frame has no time anchor on the demo timeline — drop it (only
        // possible in the sub-tick window between attaching the sink and the first broadcast).
        if (_stopped || !_hasFrame || packet.IsEmpty)
            return;
        _writer.WriteEventPacket(_lastTick, _lastServerTime, packet, reliable);
    }

    /// <summary>Finalize the demo (write the keyframe index, patch duration/frame count, close the file).
    /// Idempotent; the host calls it on the <c>stop</c> command, at match boundaries, and on shutdown.</summary>
    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;
        _writer.Dispose();
    }

    public void Dispose() => Stop();
}
