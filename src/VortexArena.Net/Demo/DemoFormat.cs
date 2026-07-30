using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VortexArena.Net.Demo;

/// <summary>
/// The frame kinds a <c>.xgd</c> demo stream carries (see <see cref="DemoFormat"/> for the container layout).
/// </summary>
public enum DemoFrameType : byte
{
    /// <summary>Full entity-set snapshot (self-contained; the seek anchor). Written every
    /// <see cref="DemoRecorder.DefaultKeyframeIntervalSeconds"/> and on the first frame.</summary>
    Keyframe = 1,

    /// <summary>Delta entity snapshot against the immediately preceding frame's set (removed + changed lists,
    /// per-entity fields via <see cref="EntityStateCodec"/> — the same codec the live wire uses).</summary>
    Snapshot = 2,

    /// <summary>An opaque server→client event packet (effect/sound/notification bundle) recorded verbatim in its
    /// wire framing, re-broadcast by playback when the playhead crosses its timestamp.</summary>
    Event = 3,

    /// <summary>Reserved: a recorded client input command (not written by the server recorder; the format slot
    /// exists so a client-side input recording can share the container without a version bump).</summary>
    Input = 4,
}

/// <summary>One roster entry in the demo header — a player present when recording started (the spectate
/// target list + scoreboard seed for playback).</summary>
public readonly struct DemoRosterEntry
{
    public readonly int NetId;
    public readonly string Name;
    public readonly int Team;
    public readonly string Model;
    public readonly int Colormap;

    public DemoRosterEntry(int netId, string name, int team, string model, int colormap)
    {
        NetId = netId;
        Name = name ?? "";
        Team = team;
        Model = model ?? "";
        Colormap = colormap;
    }
}

/// <summary>
/// The demo file header — everything playback needs to boot a replay host without reading a single frame:
/// the map/gametype to load, the recording build's parity stamp (playback rejects a mismatch), the tick rate,
/// and the player roster. Duration/frame-count/index-offset are patched in by
/// <see cref="DemoFormat.Writer.Finish"/>; a crash leaves them 0 and the reader falls back to a frame scan.
/// </summary>
public sealed class DemoHeaderInfo
{
    public uint BuildParity;
    public float TickRate = 72f;
    public string MapName = "";
    public string Gametype = "";
    public long StartWallclockUnixMs;
    public float KeyframeIntervalSeconds = DemoRecorder.DefaultKeyframeIntervalSeconds;
    public List<DemoRosterEntry> Roster { get; } = new();

    // Patched at Finish (0 while recording / after a crash — the reader then scans the frame stream).
    public uint FrameCount;
    public float DurationSeconds;
}

/// <summary>One decoded demo frame (the reader's unit of consumption).</summary>
public struct DemoFrame
{
    public DemoFrameType Type;
    public uint Tick;
    public float ServerTime;
    public byte[] Payload;
}

/// <summary>
/// The <c>.xgd</c> binary demo container (planning/specs/demo-replay-and-spectator.md §5) — the VortexArena
/// demo format. Own format, deliberately NOT DP <c>.dem</c>-compatible (ADR-0011); it records the server's
/// omniscient per-tick entity set (keyframe + delta via the live wire's <see cref="EntityStateCodec"/>) plus
/// the event bundles, so free-cam replay works everywhere in the map. Pure C# (no Godot types) so a headless
/// round-trip test can assert exactness.
///
/// <code>
/// Header (fixed part at offset 0 — the patch fields sit BEFORE the variable strings so Finish can seek them):
///   0  magic "XGDM"
///   4  ushort formatVersion        (= FormatVersion)
///   6  uint   buildParity          (NetProtocol.BuildParity at record time; playback rejects a mismatch)
///  10  float  tickRate             (72)
///  14  float  keyframeInterval     (seconds)
///  18  uint   frameCount           (patched at Finish; 0 = truncated)
///  22  float  durationSeconds      (patched at Finish)
///  26  long   indexOffset          (patched at Finish; 0 = truncated → reader rebuilds by scanning)
///  34  long   startWallclockUnixMs
///  42  ushort blobLength, then: string mapName, string gametype,
///        byte rosterCount × (ushort netId, string name, sbyte team, string model, byte colormap)
/// Frame stream (one record per entry):
///   byte frameType, uint tick, float serverTime, int payloadLength, payload…
///     Keyframe: ushort count × (ushort entnum + EntityStateCodec delta vs Empty)
///     Snapshot: ushort removed × ushort id; ushort changed × (ushort entnum + delta vs the previous frame)
///     Event:    byte flags (bit0 = reliable channel) + the raw wire packet (leading NetControl byte included)
/// Keyframe index (at indexOffset):
///   magic "XGDX", uint count × (float serverTime, uint tick, long frameOffset)
///   then the FINAL roster (v2): byte count × the header's roster-entry codec. The header roster is only
///   the players present when recording STARTED (it is written before the first frame and cannot grow);
///   clients who join mid-recording are appended to the recorder's roster and land here at Finish. The
///   reader prefers this block when the index is intact; a crash-truncated file falls back to the header
///   roster, exactly like the index falls back to a frame scan.
/// </code>
///
/// Strings are the <see cref="BitWriter.WriteString"/> convention (ushort UTF-8 byte length + bytes); all
/// integers are little-endian.
/// </summary>
public static class DemoFormat
{
    public const uint Magic = 0x4D444758;      // "XGDM" little-endian
    public const uint IndexMagic = 0x58444758; // "XGDX" little-endian
    public const ushort FormatVersion = 2;     // v2: Finish appends the FINAL roster after the keyframe index

    // Fixed header offsets of the fields Finish patches (see the layout above).
    private const long FrameCountOffset = 18;
    private const long DurationOffset = 22;
    private const long IndexOffsetOffset = 26;

    /// <summary>
    /// Recorded entity ids live in the ORIGINAL match's id space; a replay host assigns its own small ids
    /// (1..N) to the live viewers. To keep the two spaces from colliding, playback offsets every recorded id
    /// below <see cref="LiveEntityIdBase"/> (a recorded player) by this at inject time — the remapped range
    /// [8192, 16384) sits above any live viewer id and below the non-player entity id base.
    /// </summary>
    public const int ReplayPlayerIdOffset = 8192;

    /// <summary>Mirror of <c>ServerNet.EntityNetBase</c> — the net-id floor of non-player entities. Recorded ids
    /// at or above it are already collision-free against live viewer ids (a replay world scans no entities of
    /// its own), so only ids below it are remapped.</summary>
    public const int LiveEntityIdBase = 16384;

    /// <summary>One keyframe-index entry: where in the file the keyframe for a given time/tick starts.</summary>
    public readonly struct KeyframeIndexEntry
    {
        public readonly float ServerTime;
        public readonly uint Tick;
        public readonly long Offset;

        public KeyframeIndexEntry(float serverTime, uint tick, long offset)
        {
            ServerTime = serverTime;
            Tick = tick;
            Offset = offset;
        }
    }

    // =====================================================================================
    //  Shared string codec (the BitWriter/BitReader convention, over a raw stream)
    // =====================================================================================

    private static void WriteString(BinaryWriter w, string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            w.Write((ushort)0);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        int n = r.ReadUInt16();
        return n == 0 ? string.Empty : Encoding.UTF8.GetString(r.ReadBytes(n));
    }

    // One roster entry on disk (shared by the header blob and the v2 final-roster trailer).
    private static void WriteRosterEntry(BinaryWriter w, in DemoRosterEntry e)
    {
        w.Write((ushort)e.NetId);
        WriteString(w, e.Name);
        w.Write((sbyte)e.Team);
        WriteString(w, e.Model);
        w.Write((byte)e.Colormap);
    }

    private static DemoRosterEntry ReadRosterEntry(BinaryReader r)
    {
        int netId = r.ReadUInt16();
        string name = ReadString(r);
        int team = r.ReadSByte();
        string model = ReadString(r);
        int colormap = r.ReadByte();
        return new DemoRosterEntry(netId, name, team, model, colormap);
    }

    // =====================================================================================
    //  Entity-section payload codecs (shared by the recorder and playback)
    // =====================================================================================

    /// <summary>Encode a full entity set (a keyframe): every entity as a spawn-delta from Empty.</summary>
    public static void EncodeKeyframe(BitWriter w, IReadOnlyDictionary<int, NetEntityState> set)
    {
        w.WriteUShort(set.Count);
        foreach (KeyValuePair<int, NetEntityState> kv in set)
        {
            w.WriteUShort(kv.Key);
            EntityStateCodec.WriteDelta(w, NetEntityState.Empty(kv.Key), kv.Value);
        }
    }

    /// <summary>Decode a keyframe payload into <paramref name="into"/> (cleared first).</summary>
    public static void DecodeKeyframe(ReadOnlySpan<byte> payload, Dictionary<int, NetEntityState> into)
    {
        into.Clear();
        var r = new BitReader(payload);
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            int entNum = r.ReadUShort();
            NetEntityState s = EntityStateCodec.ReadDelta(ref r, NetEntityState.Empty(entNum));
            if (r.BadRead)
                return; // truncated frame — keep what decoded cleanly
            s.EntNum = entNum;
            into[entNum] = s;
        }
    }

    /// <summary>Encode <paramref name="current"/> as a delta snapshot against <paramref name="previous"/>
    /// (the immediately preceding recorded frame's set): removed ids + changed/spawned entities.</summary>
    public static void EncodeSnapshotDelta(BitWriter w,
        IReadOnlyDictionary<int, NetEntityState> current, IReadOnlyDictionary<int, NetEntityState> previous)
    {
        int removedAt = w.Length;
        w.WriteUShort(0);
        int removed = 0;
        foreach (KeyValuePair<int, NetEntityState> kv in previous)
            if (!current.ContainsKey(kv.Key))
            {
                w.WriteUShort(kv.Key);
                removed++;
            }
        w.PatchUShortAt(removedAt, removed);

        int changedAt = w.Length;
        w.WriteUShort(0);
        int changed = 0;
        foreach (KeyValuePair<int, NetEntityState> kv in current)
        {
            NetEntityState baseline = previous.TryGetValue(kv.Key, out NetEntityState b)
                ? b : NetEntityState.Empty(kv.Key);
            if (NetEntityState.Diff(baseline, kv.Value) == EntityField.None)
                continue; // unchanged — costs nothing (the delta-compression the acceptance criteria require)
            w.WriteUShort(kv.Key);
            EntityStateCodec.WriteDelta(w, baseline, kv.Value);
            changed++;
        }
        w.PatchUShortAt(changedAt, changed);
    }

    /// <summary>Apply a delta-snapshot payload onto <paramref name="state"/> (mutated in place).</summary>
    public static void ApplySnapshotDelta(ReadOnlySpan<byte> payload, Dictionary<int, NetEntityState> state)
    {
        var r = new BitReader(payload);
        int removed = r.ReadUShort();
        for (int i = 0; i < removed; i++)
        {
            int id = r.ReadUShort();
            if (r.BadRead) return;
            state.Remove(id);
        }
        int changed = r.ReadUShort();
        for (int i = 0; i < changed; i++)
        {
            int entNum = r.ReadUShort();
            NetEntityState baseline = state.TryGetValue(entNum, out NetEntityState b)
                ? b : NetEntityState.Empty(entNum);
            NetEntityState s = EntityStateCodec.ReadDelta(ref r, baseline);
            if (r.BadRead) return;
            s.EntNum = entNum;
            state[entNum] = s;
        }
    }

    /// <summary>Decode an Event frame payload: the raw wire packet + which channel to re-broadcast it on.</summary>
    public static (byte[] Packet, bool Reliable) DecodeEventFrame(byte[] payload)
    {
        bool reliable = payload.Length > 0 && (payload[0] & 1) != 0;
        byte[] packet = new byte[Math.Max(0, payload.Length - 1)];
        Array.Copy(payload, 1, packet, 0, packet.Length);
        return (packet, reliable);
    }

    // =====================================================================================
    //  Writer
    // =====================================================================================

    /// <summary>
    /// Streaming demo writer: header up front, frames appended per tick, keyframe index + patched
    /// duration/frame-count on <see cref="Finish"/>. Owns nothing above the stream — cadence (when a keyframe
    /// happens) is the <see cref="DemoRecorder"/>'s job.
    /// </summary>
    public sealed class Writer : IDisposable
    {
        private readonly Stream _stream;
        private readonly BinaryWriter _out;
        private readonly bool _leaveOpen;
        private readonly BitWriter _payload = new(4096);
        private readonly List<KeyframeIndexEntry> _keyframes = new();
        private readonly DemoHeaderInfo _header; // kept live: joins appended to its Roster land in the Finish trailer
        private uint _frameCount;
        private float _firstServerTime = float.NaN;
        private float _lastServerTime;
        private bool _finished;

        public Writer(Stream stream, DemoHeaderInfo header, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek)
                throw new ArgumentException("demo writer needs a seekable stream (Finish patches the header)", nameof(stream));
            _leaveOpen = leaveOpen;
            _out = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            _header = header;
            WriteHeader(header);
        }

        private void WriteHeader(DemoHeaderInfo h)
        {
            _out.Write(Magic);
            _out.Write(FormatVersion);
            _out.Write(h.BuildParity);
            _out.Write(h.TickRate);
            _out.Write(h.KeyframeIntervalSeconds);
            _out.Write(0u);   // frameCount — patched at Finish
            _out.Write(0f);   // durationSeconds — patched at Finish
            _out.Write(0L);   // indexOffset — patched at Finish
            _out.Write(h.StartWallclockUnixMs);

            // variable-length tail behind a ushort byte-length so a reader can skip straight to the frames.
            using var blobMs = new MemoryStream();
            using (var blob = new BinaryWriter(blobMs, Encoding.UTF8, leaveOpen: true))
            {
                WriteString(blob, h.MapName);
                WriteString(blob, h.Gametype);
                blob.Write((byte)Math.Min(h.Roster.Count, byte.MaxValue));
                for (int i = 0; i < h.Roster.Count && i < byte.MaxValue; i++)
                    WriteRosterEntry(blob, h.Roster[i]);
            }
            _out.Write((ushort)blobMs.Length);
            blobMs.WriteTo(_stream);
        }

        /// <summary>Write a full-state keyframe (also records it in the seek index).</summary>
        public void WriteKeyframe(uint tick, float serverTime, IReadOnlyDictionary<int, NetEntityState> entities)
        {
            _payload.Reset();
            EncodeKeyframe(_payload, entities);
            _keyframes.Add(new KeyframeIndexEntry(serverTime, tick, _stream.Position));
            WriteFrame(DemoFrameType.Keyframe, tick, serverTime, _payload.WrittenSpan);
        }

        /// <summary>Write a delta snapshot against the previous frame's entity set.</summary>
        public void WriteSnapshotDelta(uint tick, float serverTime,
            IReadOnlyDictionary<int, NetEntityState> current, IReadOnlyDictionary<int, NetEntityState> previous)
        {
            _payload.Reset();
            EncodeSnapshotDelta(_payload, current, previous);
            WriteFrame(DemoFrameType.Snapshot, tick, serverTime, _payload.WrittenSpan);
        }

        /// <summary>Write a verbatim server→client event packet (effect/sound/notification bundle).</summary>
        public void WriteEventPacket(uint tick, float serverTime, ReadOnlySpan<byte> packet, bool reliable)
        {
            _payload.Reset();
            _payload.WriteByte(reliable ? 1 : 0);
            _payload.WriteBytes(packet);
            WriteFrame(DemoFrameType.Event, tick, serverTime, _payload.WrittenSpan);
        }

        private void WriteFrame(DemoFrameType type, uint tick, float serverTime, ReadOnlySpan<byte> payload)
        {
            if (_finished)
                throw new InvalidOperationException("demo writer already finished");
            if (float.IsNaN(_firstServerTime))
                _firstServerTime = serverTime;
            _lastServerTime = serverTime;
            _frameCount++;

            _out.Write((byte)type);
            _out.Write(tick);
            _out.Write(serverTime);
            _out.Write(payload.Length);
            _out.Write(payload);
        }

        /// <summary>Frames written so far (diagnostics / the recorder's console feedback).</summary>
        public uint FrameCount => _frameCount;

        /// <summary>Seconds of demo time covered so far (0 until two frames exist).</summary>
        public float DurationSeconds => float.IsNaN(_firstServerTime) ? 0f : _lastServerTime - _firstServerTime;

        /// <summary>
        /// Finalize the file: append the keyframe index and patch frameCount/duration/indexOffset into the
        /// header. Idempotent; also run by <see cref="Dispose"/> so a normal teardown always leaves a seekable
        /// file (only a hard crash leaves the index missing, which the reader recovers from by scanning).
        /// </summary>
        public void Finish()
        {
            if (_finished)
                return;
            _finished = true;

            long indexOffset = _stream.Position;
            _out.Write(IndexMagic);
            _out.Write((uint)_keyframes.Count);
            for (int i = 0; i < _keyframes.Count; i++)
            {
                _out.Write(_keyframes[i].ServerTime);
                _out.Write(_keyframes[i].Tick);
                _out.Write(_keyframes[i].Offset);
            }

            // v2: the FINAL roster (header roster + everyone who joined mid-recording — the recorder appends
            // to _header.Roster as clients connect). The header blob was written before the first frame and
            // cannot grow, so the complete list rides the trailer; a crash loses it and the reader falls back
            // to the header roster, mirroring the index's fall-back-to-scan.
            _out.Write((byte)Math.Min(_header.Roster.Count, byte.MaxValue));
            for (int i = 0; i < _header.Roster.Count && i < byte.MaxValue; i++)
                WriteRosterEntry(_out, _header.Roster[i]);

            _stream.Position = FrameCountOffset;
            _out.Write(_frameCount);
            _stream.Position = DurationOffset;
            _out.Write(DurationSeconds);
            _stream.Position = IndexOffsetOffset;
            _out.Write(indexOffset);
            _out.Flush();
        }

        public void Dispose()
        {
            Finish();
            _out.Dispose();
            if (!_leaveOpen)
                _stream.Dispose();
        }
    }

    // =====================================================================================
    //  Reader
    // =====================================================================================

    /// <summary>
    /// Streaming demo reader: header + keyframe index in memory, frames read from the stream on demand
    /// (a 10-minute 72 Hz match is ~43k frames — never all-in-RAM). Rebuilds the index by scanning when the
    /// trailer is missing (a crash-truncated recording).
    /// </summary>
    public sealed class Reader : IDisposable
    {
        private readonly Stream _stream;
        private readonly BinaryReader _in;
        private readonly bool _leaveOpen;
        private readonly List<KeyframeIndexEntry> _keyframes = new();
        private readonly long _framesStart;
        private readonly long _framesEnd; // exclusive: the index trailer (or EOF when truncated)

        public DemoHeaderInfo Header { get; } = new();

        /// <summary>The keyframe seek index (ascending server time).</summary>
        public IReadOnlyList<KeyframeIndexEntry> Keyframes => _keyframes;

        /// <summary>Byte offset of the first frame record.</summary>
        public long FirstFrameOffset => _framesStart;

        /// <summary>Server time of the first recorded frame (the demo's time origin).</summary>
        public float FirstServerTime { get; private set; }

        /// <summary>Server time of the last recorded frame.</summary>
        public float LastServerTime { get; private set; }

        /// <summary>Demo length in seconds.</summary>
        public float DurationSeconds => LastServerTime - FirstServerTime;

        public Reader(Stream stream, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek)
                throw new ArgumentException("demo reader needs a seekable stream", nameof(stream));
            _leaveOpen = leaveOpen;
            _in = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (_in.ReadUInt32() != Magic)
                throw new InvalidDataException("not an XGDM demo file");
            ushort version = _in.ReadUInt16();
            if (version != FormatVersion)
                throw new InvalidDataException($"unsupported demo format version {version} (this build reads {FormatVersion})");

            Header.BuildParity = _in.ReadUInt32();
            Header.TickRate = _in.ReadSingle();
            Header.KeyframeIntervalSeconds = _in.ReadSingle();
            Header.FrameCount = _in.ReadUInt32();
            Header.DurationSeconds = _in.ReadSingle();
            long indexOffset = _in.ReadInt64();
            Header.StartWallclockUnixMs = _in.ReadInt64();

            int blobLength = _in.ReadUInt16();
            long blobEnd = _stream.Position + blobLength;
            Header.MapName = ReadString(_in);
            Header.Gametype = ReadString(_in);
            int rosterCount = _in.ReadByte();
            for (int i = 0; i < rosterCount; i++)
                Header.Roster.Add(ReadRosterEntry(_in));
            _stream.Position = blobEnd; // tolerate a future-minor header tail
            _framesStart = blobEnd;

            if (indexOffset > 0 && TryReadIndex(indexOffset))
            {
                _framesEnd = indexOffset;
            }
            else
            {
                // truncated recording (crash before Finish) — rebuild the index with a frame scan.
                _framesEnd = _stream.Length;
                RebuildIndexByScan();
            }

            ComputeTimeBounds();
        }

        private bool TryReadIndex(long indexOffset)
        {
            if (indexOffset >= _stream.Length)
                return false;
            _stream.Position = indexOffset;
            if (_in.ReadUInt32() != IndexMagic)
                return false;
            uint count = _in.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                float t = _in.ReadSingle();
                uint tick = _in.ReadUInt32();
                long off = _in.ReadInt64();
                _keyframes.Add(new KeyframeIndexEntry(t, tick, off));
            }

            // v2: the FINAL roster follows the index — prefer it over the header roster (which only lists the
            // players present when recording started). Tolerant of a corrupt tail: any misread keeps the
            // header roster, the same degrade-don't-fail stance as the index scan fallback.
            try
            {
                int finalCount = _in.ReadByte();
                var final = new List<DemoRosterEntry>(finalCount);
                for (int i = 0; i < finalCount; i++)
                    final.Add(ReadRosterEntry(_in));
                Header.Roster.Clear();
                Header.Roster.AddRange(final);
            }
            catch (EndOfStreamException)
            {
                // trailer roster truncated — the header (record-start) roster stands.
            }
            return true;
        }

        private void RebuildIndexByScan()
        {
            long offset = _framesStart;
            while (TryPeekFrameHead(offset, out DemoFrameType type, out uint tick, out float serverTime, out long next))
            {
                if (type == DemoFrameType.Keyframe)
                    _keyframes.Add(new KeyframeIndexEntry(serverTime, tick, offset));
                offset = next;
            }
        }

        private void ComputeTimeBounds()
        {
            FirstServerTime = 0f;
            LastServerTime = 0f;
            if (!TryPeekFrameHead(_framesStart, out _, out _, out float first, out _))
                return;
            FirstServerTime = first;

            if (Header.DurationSeconds > 0f)
            {
                LastServerTime = first + Header.DurationSeconds;
                return;
            }
            // truncated file: walk to the last intact frame for the real duration.
            float last = first;
            long offset = _framesStart;
            while (TryPeekFrameHead(offset, out _, out _, out float t, out long next))
            {
                last = t;
                offset = next;
            }
            LastServerTime = last;
        }

        /// <summary>Peek a frame's head (type/tick/time) without decoding its payload; false at end-of-stream
        /// or on a truncated record. <paramref name="nextOffset"/> is where the following frame starts.</summary>
        public bool TryPeekFrameHead(long offset, out DemoFrameType type, out uint tick, out float serverTime, out long nextOffset)
        {
            type = default;
            tick = 0;
            serverTime = 0f;
            nextOffset = offset;
            if (offset < _framesStart || offset + 13 > _framesEnd)
                return false;
            _stream.Position = offset;
            type = (DemoFrameType)_in.ReadByte();
            tick = _in.ReadUInt32();
            serverTime = _in.ReadSingle();
            int payloadLength = _in.ReadInt32();
            if (payloadLength < 0 || _stream.Position + payloadLength > _framesEnd)
                return false;
            nextOffset = _stream.Position + payloadLength;
            return true;
        }

        /// <summary>Read the full frame at <paramref name="offset"/>, advancing it to the next frame.
        /// Returns false at end-of-stream / on truncation.</summary>
        public bool ReadFrameAt(ref long offset, out DemoFrame frame)
        {
            frame = default;
            if (!TryPeekFrameHead(offset, out DemoFrameType type, out uint tick, out float serverTime, out long next))
                return false;
            int payloadLength = (int)(next - offset - 13);
            frame.Type = type;
            frame.Tick = tick;
            frame.ServerTime = serverTime;
            frame.Payload = payloadLength > 0 ? _in.ReadBytes(payloadLength) : Array.Empty<byte>();
            offset = next;
            return frame.Payload.Length == payloadLength;
        }

        /// <summary>The latest keyframe whose server time is ≤ <paramref name="serverTime"/> (the seek anchor);
        /// falls back to the first keyframe. False only when the demo holds no keyframe at all.</summary>
        public bool TryFindKeyframeAtOrBefore(float serverTime, out KeyframeIndexEntry entry)
        {
            entry = default;
            if (_keyframes.Count == 0)
                return false;
            int lo = 0, hi = _keyframes.Count - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_keyframes[mid].ServerTime <= serverTime) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            entry = _keyframes[best];
            return true;
        }

        public void Dispose()
        {
            _in.Dispose();
            if (!_leaveOpen)
                _stream.Dispose();
        }
    }

    /// <summary>Read just the header of a demo file (the menu/`playdemo` peek: map name, parity, duration)
    /// without touching the frame stream. Throws <see cref="InvalidDataException"/> on a non-demo file.</summary>
    public static DemoHeaderInfo ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new Reader(stream, leaveOpen: true);
        // surface the scan-derived duration for a truncated file (header field still 0 there).
        if (reader.Header.DurationSeconds <= 0f)
            reader.Header.DurationSeconds = reader.DurationSeconds;
        return reader.Header;
    }
}
