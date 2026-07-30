using System;
using System.Collections.Generic;
using System.IO;

namespace VortexArena.Net.Demo;

/// <summary>
/// The replay entity injector (planning/specs/demo-replay-and-spectator.md §6): when installed on
/// <c>ServerNet.ReplaySource</c>, <c>BuildEntitySet</c> uses this INSTEAD of the live world scan — the recorded
/// set becomes the only source of networked entities (all humans on a replay host are observers, which the
/// scan skips anyway), and the normal snapshot broadcast + client interpolation pipeline does the rest.
/// </summary>
public interface IReplayEntitySource
{
    /// <summary>Copy the entity set at the current playhead into <paramref name="into"/> (already cleared by
    /// the caller), remapped into the replay id space so recorded ids never collide with live viewer ids.</summary>
    void CopyEntities(Dictionary<int, NetEntityState> into);
}

/// <summary>One recorded event packet due for re-broadcast (raw wire bytes + the channel it was sent on).</summary>
public readonly struct DemoEventPacket
{
    public readonly byte[] Data;
    public readonly bool Reliable;

    public DemoEventPacket(byte[] data, bool reliable)
    {
        Data = data;
        Reliable = reliable;
    }
}

/// <summary>
/// Plays a <c>.xgd</c> demo back into a replay host (T63, planning/specs/demo-replay-and-spectator.md §3/§6):
/// owns the playhead, reconstructs the recorded entity set at any time (nearest keyframe ≤ target, deltas
/// re-applied forward — the seek contract), and windows the recorded event packets so effects/sounds/kill-feed
/// re-fire exactly when the playhead crosses them (forward only; a seek skips them and flags every entity
/// <see cref="NetEntityFlags.Teleported"/> so clients snap instead of lerping across the discontinuity).
///
/// Two-clock model (§3): the host advances the playhead by <c>realDelta × Speed</c> (<see cref="Advance"/>)
/// while the replay server's own sim clock keeps running at real time — pause freezes the recorded entities
/// but snapshots keep flowing, so spectators keep moving smoothly through a frozen scene.
///
/// Pure C# — no Godot types; the Godot host opens the stream and drives <see cref="Advance"/> per frame.
/// </summary>
public sealed class DemoPlayback : IReplayEntitySource, IDisposable
{
    /// <summary>The supported speed magnitude range (0.25×/0.5×/1×/2×/4× per T63; the clamp admits anything
    /// between). Negative = smooth rewind (§3): the playhead decreases and each frame's state is re-derived
    /// from the keyframe at-or-before it, so clients interpolate smooth reverse motion — no teleport snap.</summary>
    public const float MinSpeed = 0.25f;
    public const float MaxSpeed = 4f;

    private readonly DemoFormat.Reader _reader;

    // The reconstructed entity set at the playhead + the sequential read cursor into the frame stream.
    private readonly Dictionary<int, NetEntityState> _current = new();
    private long _cursor;
    private bool _primed;              // the first frame (a keyframe) has been applied

    private readonly List<DemoEventPacket> _pendingEvents = new();
    private bool _teleportNextCopy;    // set on seek: flag everything Teleported on the next inject
    private float _playhead;           // seconds since the demo's first frame
    private float _speed = 1f;

    // Frames are quantized to ticks, so the nearest frame within HALF a tick of the playhead is the correct
    // one to show — and the slack absorbs the float drift between an accumulated playhead (sum of render
    // deltas) and the recorded per-tick timestamps, which would otherwise leave playback a frame behind at
    // ULP boundaries. Shared by forward play and seek (both go through ApplyForwardTo), so the two stay
    // deterministic against each other.
    private readonly float _frameSlack;

    public DemoPlayback(Stream stream, bool leaveOpen = false)
    {
        _reader = new DemoFormat.Reader(stream, leaveOpen);
        _frameSlack = 0.5f / (_reader.Header.TickRate > 0f ? _reader.Header.TickRate : 72f);
        _cursor = _reader.FirstFrameOffset;
        // Prime the state at time 0 so the first snapshot already carries the recorded world.
        ApplyForwardTo(_reader.FirstServerTime, emitEvents: false);
    }

    /// <summary>Open a demo file for playback. Throws <see cref="InvalidDataException"/> on a non-demo /
    /// incompatible-version file — callers surface that as an honest console message.</summary>
    public static DemoPlayback OpenFile(string path)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    /// <summary>The demo header (map/gametype/parity/roster) — the replay host boots from this.</summary>
    public DemoHeaderInfo Header => _reader.Header;

    /// <summary>Demo length in seconds.</summary>
    public float DurationSeconds => _reader.DurationSeconds;

    /// <summary>The playhead position in seconds from the start of the recording.</summary>
    public float PlayheadSeconds => _playhead;

    /// <summary>Playhead frozen (recorded entities hold still; the live snapshot stream keeps flowing).</summary>
    public bool Paused { get; set; }

    /// <summary>Playhead rate multiplier: magnitude clamped to [<see cref="MinSpeed"/>, <see cref="MaxSpeed"/>];
    /// negative = smooth rewind. Use <see cref="Paused"/> to stop, not 0.</summary>
    public float Speed
    {
        get => _speed;
        set => _speed = Math.Sign(value) switch
        {
            < 0 => -Math.Clamp(-value, MinSpeed, MaxSpeed),
            _ => Math.Clamp(value, MinSpeed, MaxSpeed),
        };
    }

    /// <summary>The playhead reached the end of the recording (it holds on the final state; seek back to resume).</summary>
    public bool EndReached { get; private set; }

    /// <summary>Recorded event packets whose timestamps the playhead crossed since the last drain — the host
    /// re-broadcasts each via the replay server. Cleared by <see cref="ClearPendingEvents"/>.</summary>
    public IReadOnlyList<DemoEventPacket> PendingEvents => _pendingEvents;

    public void ClearPendingEvents() => _pendingEvents.Clear();

    /// <summary>
    /// Advance the playhead by <paramref name="realDelta"/> × <see cref="Speed"/> (no-op while paused), applying
    /// every recorded frame the playhead crossed: entity frames update the current set, event frames queue for
    /// re-broadcast. Call once per host frame BEFORE the replay server's tick so the injected set is fresh.
    /// </summary>
    public void Advance(float realDelta)
    {
        if (Paused || realDelta <= 0f)
            return;

        // Smooth rewind (§3): a decreasing playhead can't be reached by the forward-only cursor, so each step
        // re-derives the state seek-style (keyframe at-or-before the target, deltas re-applied forward) — but
        // WITHOUT the Teleported stamp: successive rewound frames are small per-tick deltas the client should
        // interpolate backward through, not snap across. Crossed events are skipped (they belong to forward time).
        if (_speed < 0f)
        {
            if (_playhead <= 0f)
                return;
            RebuildAt(Math.Max(0f, _playhead + realDelta * _speed), teleport: false);
            return;
        }

        if (EndReached)
            return;
        _playhead = Math.Min(_playhead + realDelta * _speed, DurationSeconds);
        ApplyForwardTo(_reader.FirstServerTime + _playhead, emitEvents: true);
        if (_playhead >= DurationSeconds)
            EndReached = true;
    }

    /// <summary>
    /// Instant seek (T63 acceptance): clamp to [0, duration], jump to the nearest keyframe AT OR BEFORE the
    /// target and re-apply the delta frames forward to the target timestamp. Crossed events are NOT re-fired
    /// (they belong to the skipped-over time), and the next injected set is flagged Teleported so every client
    /// snaps across the discontinuity instead of interpolating.
    /// </summary>
    public void Seek(float seconds) => RebuildAt(seconds, teleport: true);

    /// <summary>Reposition the playhead to an arbitrary time by rebuilding the state from the keyframe
    /// at-or-before it — the shared engine under <see cref="Seek"/> (teleport: clients snap) and smooth
    /// rewind (no teleport: clients interpolate the backward motion).</summary>
    private void RebuildAt(float seconds, bool teleport)
    {
        _playhead = Math.Clamp(seconds, 0f, DurationSeconds);
        float target = _reader.FirstServerTime + _playhead;

        if (!_reader.TryFindKeyframeAtOrBefore(target, out DemoFormat.KeyframeIndexEntry key))
            return; // no keyframe at all — an empty/corrupt demo; hold the current state

        _cursor = key.Offset;
        _current.Clear();
        _primed = false;
        _pendingEvents.Clear();
        ApplyForwardTo(target, emitEvents: false);
        if (teleport)
            _teleportNextCopy = true;
        EndReached = _playhead >= DurationSeconds;
    }

    /// <summary>Apply every frame with serverTime ≤ <paramref name="targetTime"/> from the cursor forward.</summary>
    private void ApplyForwardTo(float targetTime, bool emitEvents)
    {
        while (_reader.TryPeekFrameHead(_cursor, out DemoFrameType type, out _, out float frameTime, out _))
        {
            // The priming frame (the seek anchor keyframe / the very first frame) always applies, whatever its
            // stamp; after that, stop at the first frame beyond the playhead (+ the half-tick slack above).
            if (_primed && frameTime > targetTime + _frameSlack)
                break;

            if (!_reader.ReadFrameAt(ref _cursor, out DemoFrame frame))
                break;

            switch (frame.Type)
            {
                case DemoFrameType.Keyframe:
                    DemoFormat.DecodeKeyframe(frame.Payload, _current);
                    _primed = true;
                    break;
                case DemoFrameType.Snapshot:
                    DemoFormat.ApplySnapshotDelta(frame.Payload, _current);
                    _primed = true;
                    break;
                case DemoFrameType.Event:
                    if (emitEvents)
                    {
                        (byte[] packet, bool reliable) = DemoFormat.DecodeEventFrame(frame.Payload);
                        if (packet.Length > 0)
                            _pendingEvents.Add(new DemoEventPacket(packet, reliable));
                    }
                    break;
                // DemoFrameType.Input (a client-side recording's frames) carries nothing a replay host injects.
            }
        }
    }

    /// <inheritdoc />
    public void CopyEntities(Dictionary<int, NetEntityState> into)
    {
        foreach (KeyValuePair<int, NetEntityState> kv in _current)
        {
            int id = RemapId(kv.Key);
            NetEntityState s = kv.Value;
            s.EntNum = id;
            if (s.Owner > 0)
                s.Owner = RemapId(s.Owner); // owner references live in the same recorded id space
            if (_teleportNextCopy)
                s.Flags |= NetEntityFlags.Teleported;
            into[id] = s;
        }
        _teleportNextCopy = false;
    }

    /// <summary>Recorded → replay id: recorded player ids (below the non-player entity base) are offset into
    /// [8192, 16384) so they can never collide with the replay host's own live viewer ids (§5 "ID namespacing").</summary>
    public static int RemapId(int recordedId)
        => recordedId < DemoFormat.LiveEntityIdBase ? recordedId + DemoFormat.ReplayPlayerIdOffset : recordedId;

    public void Dispose() => _reader.Dispose();
}
