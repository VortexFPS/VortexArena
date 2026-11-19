using VortexArena.Common.Menu;

namespace VortexArena.Net;

/// <summary>The optional ClientInit tail. Each visual uses two bits for absent, off or on.</summary>
public static class MapVisualSuggestionsWire
{
    public static void Write(BitWriter writer, MapClientVisualSettings settings)
    {
        writer.WriteByte(Encode(settings.Bloom) | (Encode(settings.Sky) << 2) | (Encode(settings.Fog) << 4));
    }

    public static MapClientVisualSettings Read(ref BitReader reader)
    {
        if (reader.BadRead || reader.Remaining != 1)
            return default;

        int packed = reader.ReadByte();
        int bloom = packed & 3;
        int sky = (packed >> 2) & 3;
        int fog = (packed >> 4) & 3;
        if (packed > 0x3F || bloom == 3 || sky == 3 || fog == 3)
            return default;

        return new MapClientVisualSettings(Decode(bloom), Decode(sky), Decode(fog));
    }

    private static int Encode(bool? value) => value switch { false => 1, true => 2, null => 0 };
    private static bool? Decode(int value) => value == 0 ? null : value == 2;
}
