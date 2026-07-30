using System.Collections.Generic;

namespace XonoticGodot.Net;

/// <summary>
/// Server-side delta-compression history for ONE client — the C# successor to DP's per-client entity-delta
/// state (<c>EntityFrame</c>/<c>EntityState</c> baseline tracking). Each tick the server encodes the world's
/// entity set as a delta against the snapshot this client last <em>acked</em> (so a lost datagram never
/// corrupts the stream: the server keeps deltaing against the last confirmed baseline until a newer one is
/// acked). Only entities that spawned, changed a field, or were removed cost bytes — an idle world is nearly
/// free, versus the previous "every entity, every field, every tick" full snapshot.
///
/// Wire layout written by <see cref="EncodeSnapshot"/> (and read by <see cref="ClientSnapshotHistory"/>):
/// <code>
///   ushort snapshotSeq      // this snapshot's sequence (the client acks it)
///   ushort baselineSeq      // the snapshot it was delta'd against (0 = full / no baseline)
///   ushort removedCount; removedCount × ushort entnum            // entities gone since the baseline
///   ushort changedCount; changedCount × (ushort entnum + EntityStateCodec delta)  // spawns + updates
/// </code>
/// </summary>
public sealed class ServerSnapshotHistory
{
    /// <summary>How many recent snapshots to retain as possible baselines (~0.9s at 72 Hz).</summary>
    public const int Capacity = 64;

    private readonly (ushort Seq, Dictionary<int, NetEntityState> Map)[] _ring;
    private ushort _ackedSeq; // the newest snapshot this client confirmed receiving (0 = none yet)

    public ServerSnapshotHistory()
    {
        _ring = new (ushort, Dictionary<int, NetEntityState>)[Capacity];
        for (int i = 0; i < Capacity; i++)
            _ring[i] = (0, new Dictionary<int, NetEntityState>());
    }

    /// <summary>The newest snapshot sequence this client has acked (0 = none — next encode is a full snapshot).</summary>
    public ushort AckedSeq => _ackedSeq;

    /// <summary>Record that the client acked <paramref name="seq"/> (newest wins; stale acks ignored).</summary>
    public void Ack(ushort seq)
    {
        if (seq != 0 && IsNewer(seq, _ackedSeq))
            _ackedSeq = seq;
    }

    private Dictionary<int, NetEntityState>? Lookup(ushort seq)
    {
        if (seq == 0) return null;
        ref var slot = ref _ring[seq % Capacity];
        return slot.Seq == seq ? slot.Map : null;
    }

    /// <summary>
    /// Encode <paramref name="current"/> (the world's networked entities this tick) as a delta against the
    /// client's last-acked snapshot, under the new sequence <paramref name="snapshotSeq"/>, and retain it as a
    /// future baseline. Falls back to a full snapshot when no usable baseline is held.
    /// </summary>
    public void EncodeSnapshot(BitWriter w, IReadOnlyDictionary<int, NetEntityState> current, ushort snapshotSeq, int excludeEntNum = -1)
    {
        Dictionary<int, NetEntityState>? baseline = Lookup(_ackedSeq);
        ushort baselineSeq = baseline is null ? (ushort)0 : _ackedSeq;

        w.WriteUShort(snapshotSeq);
        w.WriteUShort(baselineSeq);

        // removed: present in the baseline but gone now.
        int removedAt = w.Length;
        w.WriteUShort(0);
        int removed = 0;
        if (baseline is not null)
        {
            foreach (KeyValuePair<int, NetEntityState> kv in baseline)
                if (kv.Key != excludeEntNum && !current.ContainsKey(kv.Key))
                {
                    w.WriteUShort(kv.Key);
                    removed++;
                }
        }
        w.PatchUShortAt(removedAt, removed);

        // changed: spawned (absent in baseline) or any field differs.
        int changedAt = w.Length;
        w.WriteUShort(0);
        int changed = 0;
        foreach (KeyValuePair<int, NetEntityState> kv in current)
        {
            if (kv.Key == excludeEntNum)
                continue; // the recipient's own entity — it predicts that locally, never interpolated
            NetEntityState bstate = baseline is not null && baseline.TryGetValue(kv.Key, out NetEntityState b)
                ? b : NetEntityState.Empty(kv.Key);
            EntityField mask = NetEntityState.Diff(bstate, kv.Value);
            if (mask == EntityField.None)
                continue; // unchanged — costs nothing
            w.WriteUShort(kv.Key);
            EntityStateCodec.WriteDelta(w, bstate, kv.Value);
            changed++;
        }
        w.PatchUShortAt(changedAt, changed);

        // retain this snapshot as a future baseline (reuse the ring slot's dict — no per-tick allocation).
        ref var store = ref _ring[snapshotSeq % Capacity];
        store.Seq = snapshotSeq;
        store.Map.Clear();
        foreach (KeyValuePair<int, NetEntityState> kv in current)
            if (kv.Key != excludeEntNum)
                store.Map[kv.Key] = kv.Value;
    }

    /// <summary>Sequence comparison that tolerates 16-bit wraparound (a is newer than b).</summary>
    public static bool IsNewer(ushort a, ushort b) => (ushort)(a - b) < 0x8000;
}

/// <summary>
/// Client-side delta-decompression history — the inverse of <see cref="ServerSnapshotHistory"/>. Keeps a ring
/// of recently decoded snapshots so an incoming delta can be reconstructed against the baseline the server
/// named, then exposes the freshly decoded entity set for rendering/interpolation. Tracks the newest decoded
/// sequence so the client can ack it back to the server (closing the delta loop).
/// </summary>
public sealed class ClientSnapshotHistory
{
    private readonly (ushort Seq, Dictionary<int, NetEntityState> Map)[] _ring;

    public ClientSnapshotHistory()
    {
        _ring = new (ushort, Dictionary<int, NetEntityState>)[ServerSnapshotHistory.Capacity];
        for (int i = 0; i < _ring.Length; i++)
            _ring[i] = (0, new Dictionary<int, NetEntityState>());
    }

    /// <summary>The newest snapshot sequence successfully decoded (0 = none); the client acks this to the server.</summary>
    public ushort LastDecodedSeq { get; private set; }

    private Dictionary<int, NetEntityState>? Lookup(ushort seq)
    {
        if (seq == 0) return null;
        ref var slot = ref _ring[seq % _ring.Length];
        return slot.Seq == seq ? slot.Map : null;
    }

    /// <summary>
    /// Decode one snapshot frame: reconstruct the full entity set from the named baseline + the delta, retain
    /// it in the ring, and return it. The returned dictionary is the ring's own slot — read it this frame; it is
    /// reused after <see cref="ServerSnapshotHistory.Capacity"/> further snapshots. Returns null on a bad read
    /// (truncated/garbled packet) or when the named baseline is no longer held (a desync the server resolves by
    /// sending a full snapshot once this client stops acking the lost baseline).
    /// </summary>
    public IReadOnlyDictionary<int, NetEntityState>? DecodeSnapshot(ref BitReader r)
    {
        ushort snapshotSeq = (ushort)r.ReadUShort();
        ushort baselineSeq = (ushort)r.ReadUShort();
        if (r.BadRead)
            return null;

        Dictionary<int, NetEntityState>? baseline = Lookup(baselineSeq);
        if (baselineSeq != 0 && baseline is null)
            return null; // we no longer hold that baseline — wait for the server to send a full snapshot

        // Build into the target ring slot (reused dict — no per-frame allocation).
        ref var slot = ref _ring[snapshotSeq % _ring.Length];
        Dictionary<int, NetEntityState> result = slot.Map;
        result.Clear();
        if (baseline is not null)
            foreach (KeyValuePair<int, NetEntityState> kv in baseline)
                result[kv.Key] = kv.Value;

        int removed = r.ReadUShort();
        for (int i = 0; i < removed; i++)
        {
            int entnum = r.ReadUShort();
            if (r.BadRead) return null;
            result.Remove(entnum);
        }

        int changed = r.ReadUShort();
        for (int i = 0; i < changed; i++)
        {
            int entnum = r.ReadUShort();
            NetEntityState bstate = baseline is not null && baseline.TryGetValue(entnum, out NetEntityState b)
                ? b : NetEntityState.Empty(entnum);
            NetEntityState s = EntityStateCodec.ReadDelta(ref r, bstate);
            s.EntNum = entnum;
            if (r.BadRead) return null;
            result[entnum] = s;
        }

        slot.Seq = snapshotSeq;
        LastDecodedSeq = snapshotSeq;
        return result;
    }
}
