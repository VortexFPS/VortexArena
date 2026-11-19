using System.Numerics;

namespace VortexArena.Net;

/// <summary>An optional, length-framed native EventBundle record. Older clients skip its unregistered effect id.</summary>
public readonly record struct AttachedDamageWire(int TargetId, int WeaponId, Vector3 HitOffset, byte Damage,
    float SnapshotTime, bool SpawnParity)
{
    public const int EffectRecordId = 0xfffe;
    public const int BodyLength = 2 + 2 + 12 + 1 + 4 + 1;

    public void Write(BitWriter writer)
    {
        writer.WriteUShort(EffectRecordId);
        writer.WriteUShort(BodyLength);
        writer.WriteUShort(TargetId);
        writer.WriteUShort(WeaponId);
        writer.WriteVector(HitOffset, NetPrecision.Float);
        writer.WriteByte(Damage);
        writer.WriteFloat(SnapshotTime);
        writer.WriteBool(SpawnParity);
    }

    public static AttachedDamageWire? Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodyLength)
            return null;

        var reader = new BitReader(body);
        int target = reader.ReadUShort();
        int weapon = reader.ReadUShort();
        Vector3 offset = reader.ReadVector(NetPrecision.Float);
        byte damage = (byte)reader.ReadByte();
        float snapshotTime = reader.ReadFloat();
        if (target == 0 || damage == 0 || !float.IsFinite(snapshotTime)
            || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z))
            return null;

        return new(target, weapon, offset, damage, snapshotTime, reader.ReadBool());
    }
}
