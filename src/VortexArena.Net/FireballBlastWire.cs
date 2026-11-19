using VortexArena.Common.Gameplay;

namespace VortexArena.Net;

/// <summary>A single native Fireball blast event. The model and timeline are shared constants.</summary>
public static class FireballBlastWire
{
    public static void Write(BitWriter writer, FireballBlastEffect effect)
    {
        writer.WriteVector(effect.Origin, NetPrecision.Float);
        writer.WriteFloat(effect.Radius);
    }

    public static FireballBlastEffect? Read(ref BitReader reader)
    {
        if (reader.Remaining != 4 * sizeof(float))
            return null;

        var origin = reader.ReadVector(NetPrecision.Float);
        return FireballBlastEffect.Create(origin, reader.ReadFloat());
    }
}
