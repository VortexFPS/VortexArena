namespace XonoticGodot.Net;

/// <summary>
/// The WIRE codec for <see cref="VehicleViewState"/> — the single Write/Read pair that is the source of truth for
/// the block's on-wire layout (the vehicle counterpart of <see cref="WepentViewCodec"/>). It lives in the .Net
/// assembly (the one place that can see <see cref="BitWriter"/>/<see cref="BitReader"/>); the
/// <see cref="VehicleViewState"/> DATA struct lives in .Common because its authoritative producer
/// (<c>VehicleViewResolver</c>) is engine-free Common code that cannot reference .Net.
///
/// <para>Split so the two halves can never drift: the server append (<c>EntityStateCodec.WriteDelta</c> via
/// <see cref="Write"/>) and the client read (<c>EntityStateCodec.ReadDelta</c> via <see cref="Read"/>) are this
/// same pair. A field added to Write but not Read shifts every block after it for a real remote client — and
/// because the vehicle block is appended LAST in the delta, a length mismatch desyncs the whole tail. Keep
/// Write and Read in lock-step.</para>
/// </summary>
public static class VehicleViewCodec
{
    /// <summary>Append the block in the EXACT wire order (11 bytes). Mirror of <see cref="Read"/>. The seven
    /// pool/charge values (Health, Shield, Energy, Ammo1, Ammo2, Reload1, Reload2) plus LockStrength are coded
    /// ×255 into a byte via <see cref="VehicleViewState.EncodeUnit"/> (clamped [0,255]); VehKind and W2Mode are
    /// raw bytes; the trailing flags byte packs LockTargetValid (bit0) and DropmarkPredictReady (bit1), bits 2-7
    /// reserved 0.</summary>
    public static void Write(BitWriter w, in VehicleViewState v)
    {
        w.WriteByte(VehicleViewState.EncodeUnit(v.Health));
        w.WriteByte(VehicleViewState.EncodeUnit(v.Shield));
        w.WriteByte(VehicleViewState.EncodeUnit(v.Energy));
        w.WriteByte(VehicleViewState.EncodeUnit(v.Ammo1));
        w.WriteByte(VehicleViewState.EncodeUnit(v.Ammo2));
        w.WriteByte(VehicleViewState.EncodeUnit(v.Reload1));
        w.WriteByte(VehicleViewState.EncodeUnit(v.Reload2));
        w.WriteByte(v.VehKind);
        w.WriteByte(v.W2Mode);
        w.WriteByte(VehicleViewState.EncodeUnit(v.LockStrength));
        int flags = (v.LockTargetValid ? 1 : 0) | (v.DropmarkPredictReady ? 2 : 0);
        w.WriteByte(flags);
    }

    /// <summary>Read the block in the same order <see cref="Write"/> appended it. Bytes coded ×255 decode back
    /// to floats via /255. VehKind / W2Mode are raw bytes; the flags byte unpacks LockTargetValid (bit0) and
    /// DropmarkPredictReady (bit1). Always consumes exactly 11 bytes to keep the delta aligned (BitReader
    /// zero-fills on underflow).</summary>
    public static VehicleViewState Read(ref BitReader r)
    {
        float health = r.ReadByte() / 255f;
        float shield = r.ReadByte() / 255f;
        float energy = r.ReadByte() / 255f;
        float ammo1 = r.ReadByte() / 255f;
        float ammo2 = r.ReadByte() / 255f;
        float reload1 = r.ReadByte() / 255f;
        float reload2 = r.ReadByte() / 255f;
        byte vehKind = (byte)r.ReadByte();
        byte w2Mode = (byte)r.ReadByte();
        float lockStrength = r.ReadByte() / 255f;
        int flags = r.ReadByte();
        return new VehicleViewState
        {
            Health = health,
            Shield = shield,
            Energy = energy,
            Ammo1 = ammo1,
            Ammo2 = ammo2,
            Reload1 = reload1,
            Reload2 = reload2,
            VehKind = vehKind,
            W2Mode = w2Mode,
            LockStrength = lockStrength,
            LockTargetValid = (flags & 1) != 0,
            DropmarkPredictReady = (flags & 2) != 0,
        };
    }
}
