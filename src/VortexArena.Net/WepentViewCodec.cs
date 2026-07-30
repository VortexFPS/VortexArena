namespace XonoticGodot.Net;

/// <summary>
/// The WIRE codec for <see cref="WepentViewState"/> — the single Write/Read pair that is the source of truth for
/// the block's on-wire layout. It lives in the .Net assembly (the one place that can see <see cref="BitWriter"/>/
/// <see cref="BitReader"/>); the <see cref="WepentViewState"/> DATA struct lives in .Common because its
/// authoritative producer (<c>WepentResolver</c>) is engine-free Common code that cannot reference .Net.
///
/// <para>Split so the two halves can never drift: the server append (<c>EntityStateCodec.WriteDelta</c> via
/// <see cref="Write"/>) and the client read (<c>EntityStateCodec.ReadDelta</c> via <see cref="Read"/>) are this
/// same pair. A field added to Write but not Read shifts every block after it for a real remote client — exactly
/// the desync class <c>WepentViewStateTests</c> locks down.</para>
/// </summary>
public static class WepentViewCodec
{
    /// <summary>Append the block in the EXACT wire order (13 bytes). Mirror of <see cref="Read"/>. The four
    /// charge/pool values + arc heat are coded ×255 into a byte (clamped [0,255]); clip load/size are signed
    /// shorts (so the -1 reload sentinel survives); the rest are bytes.</summary>
    public static void Write(BitWriter w, in WepentViewState v)
    {
        w.WriteByte(WepentViewState.EncodeUnit(v.VortexCharge));
        w.WriteByte(WepentViewState.EncodeUnit(v.OknexCharge));
        w.WriteByte(WepentViewState.EncodeUnit(v.VortexChargePool));
        w.WriteByte(WepentViewState.EncodeUnit(v.OknexChargePool));
        w.WriteShort(v.ClipLoad);
        w.WriteShort(v.ClipSize);
        w.WriteByte(v.HagarLoad);
        w.WriteByte(v.MinelayerMines);
        w.WriteByte(WepentViewState.EncodeUnit(v.ArcHeat));
        w.WriteByte(v.ViewmodelFrame);
        w.WriteByte(v.BeamState);
    }

    /// <summary>Read the block in the same order <see cref="Write"/> appended it. Bytes coded ×255 decode back
    /// to floats via /255. Always consumes exactly 13 bytes to keep the delta aligned (BitReader zero-fills on
    /// underflow).</summary>
    public static WepentViewState Read(ref BitReader r) => new()
    {
        VortexCharge = r.ReadByte() / 255f,
        OknexCharge = r.ReadByte() / 255f,
        VortexChargePool = r.ReadByte() / 255f,
        OknexChargePool = r.ReadByte() / 255f,
        ClipLoad = r.ReadShort(),
        ClipSize = r.ReadShort(),
        HagarLoad = r.ReadByte(),
        MinelayerMines = r.ReadByte(),
        ArcHeat = r.ReadByte() / 255f,
        ViewmodelFrame = r.ReadByte(),
        BeamState = r.ReadByte(),
    };
}
