using VortexArena.Common.Gameplay;

namespace VortexArena.Net;

/// <summary>Reliable state for the generator destruction presentation, including a round reset.</summary>
public static class OnslaughtDestructionWire
{
    public static void Write(BitWriter writer, OnslaughtDestruction? state, bool showCamera)
    {
        writer.WriteByte((byte)(state is null ? 0 : showCamera ? 2 : 1));
        if (state is not { } destruction)
            return;
        writer.WriteVector(destruction.Origin, NetPrecision.Float);
        writer.WriteFloat(destruction.Yaw);
        writer.WriteFloat(destruction.Time);
    }

    public static bool TryRead(ref BitReader reader, out OnslaughtDestruction? state, out bool showCamera)
    {
        state = null;
        showCamera = false;
        if (reader.Remaining < 1)
            return false;
        int mode = reader.ReadByte();
        if (mode == 0)
            return reader.Remaining == 0;
        if (mode > 2 || reader.Remaining != 5 * sizeof(float))
            return false;

        var origin = reader.ReadVector(NetPrecision.Float);
        float yaw = reader.ReadFloat();
        float time = reader.ReadFloat();
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z)
            || !float.IsFinite(yaw) || !float.IsFinite(time))
            return false;
        state = new OnslaughtDestruction(origin, yaw, time);
        showCamera = mode == 2;
        return true;
    }
}
