using System.Numerics;
using VortexArena.Common.Gameplay;

namespace VortexArena.Net;

/// <summary>Complete authoritative overlay for monitor poses, keyed by BSP inline model number.</summary>
public static class WarpzoneCameraWire
{
    // A full chunk takes 1,299 bytes including the control byte, below ENet's usual payload budget.
    public const int EntriesPerChunk = 48;
    private const byte Begin = 1;
    private const byte End = 2;

    public readonly record struct Entry(int Model, WarpzoneCamera.Pose? Pose);

    // Entries are sorted in strictly increasing model order before sending. One empty chunk clears the overlay.
    public static void WriteChunk(BitWriter writer, ReadOnlySpan<Entry> entries, int start)
    {
        int count = Math.Min(entries.Length - start, EntriesPerChunk);
        byte flags = 0;
        if (start == 0)
            flags |= Begin;
        if (start + count == entries.Length)
            flags |= End;
        writer.WriteByte(flags);
        writer.WriteByte((byte)count);
        foreach (Entry entry in entries.Slice(start, count))
        {
            writer.WriteUShort(entry.Model);
            writer.WriteByte(entry.Pose is null ? (byte)0 : (byte)1);
            if (entry.Pose is { } pose)
            {
                writer.WriteVector(pose.Origin, NetPrecision.Float);
                writer.WriteVector(pose.Angles, NetPrecision.Float);
            }
        }
    }

    /// <summary>Stages ordered reliable chunks and publishes a replacement only after the final valid chunk.</summary>
    public sealed class Receiver
    {
        private Dictionary<int, WarpzoneCamera.Pose?>? _pending;
        private int _lastModel;

        public IReadOnlyDictionary<int, WarpzoneCamera.Pose?> Poses { get; private set; } =
            new Dictionary<int, WarpzoneCamera.Pose?>();

        public void Clear()
        {
            _pending = null;
            _lastModel = 0;
            Poses = new Dictionary<int, WarpzoneCamera.Pose?>();
        }

        public bool Receive(ref BitReader reader)
        {
            if (!ReadChunk(ref reader, out byte flags, out Entry[] entries))
            {
                _pending = null;
                return false;
            }

            if ((flags & Begin) != 0)
            {
                _pending = new();
                _lastModel = 0;
            }
            // Strict model ordering across chunks also caps the aggregate at 65,535 possible models.
            if (_pending is null || (entries.Length > 0 && entries[0].Model <= _lastModel))
            {
                _pending = null;
                return false;
            }

            foreach (Entry entry in entries)
            {
                _pending.Add(entry.Model, entry.Pose);
                _lastModel = entry.Model;
            }

            if ((flags & End) != 0)
            {
                Poses = _pending;
                _pending = null;
            }
            return true;
        }
    }

    private static bool ReadChunk(ref BitReader reader, out byte flags, out Entry[] entries)
    {
        flags = 0;
        entries = Array.Empty<Entry>();
        if (reader.Remaining < 2)
            return false;
        flags = (byte)reader.ReadByte();
        int count = reader.ReadByte();
        if ((flags & ~(Begin | End)) != 0 || count > EntriesPerChunk || reader.Remaining < count * 3
            || ((flags & End) == 0 && count != EntriesPerChunk)
            || (count == 0 && flags != (Begin | End)))
            return false;

        var decoded = new Entry[count];
        int lastModel = 0;
        for (int i = 0; i < count; i++)
        {
            if (reader.Remaining < 3)
                return false;
            int model = reader.ReadUShort();
            int present = reader.ReadByte();
            if (model <= lastModel || present > 1)
                return false;
            lastModel = model;

            WarpzoneCamera.Pose? pose = null;
            if (present == 1)
            {
                if (reader.Remaining < 6 * sizeof(float))
                    return false;
                Vector3 origin = reader.ReadVector(NetPrecision.Float);
                Vector3 angles = reader.ReadVector(NetPrecision.Float);
                if (!IsFinite(origin) || !IsFinite(angles))
                    return false;
                pose = new WarpzoneCamera.Pose(origin, angles);
            }
            decoded[i] = new Entry(model, pose);
        }

        if (reader.Remaining != 0)
            return false;
        entries = decoded;
        return true;
    }

    public static bool IsFinite(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
