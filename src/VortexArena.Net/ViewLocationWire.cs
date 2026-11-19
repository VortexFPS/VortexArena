using VortexArena.Common.Gameplay;

namespace VortexArena.Net;

/// <summary>The view player's active region, carried with the authoritative snapshot.</summary>
public static class ViewLocationWire
{
    public static void Write(BitWriter writer, ViewLocationState? view)
    {
        writer.WriteBool(view.HasValue);
        if (view is not { } state)
            return;

        writer.WriteLong(state.TriggerId);
        writer.WriteByte((byte)state.Flags);
        writer.WriteBool(state.SideScroll);
        writer.WriteVector(state.Start, NetPrecision.Float);
        if (state.SideScroll)
            writer.WriteVector(state.End, NetPrecision.Float);
        writer.WriteVector(state.AnchorAngles, NetPrecision.Float);
    }

    public static ViewLocationState? Read(ref BitReader reader)
    {
        if (!reader.ReadBool())
            return null;

        int id = reader.ReadLong();
        var flags = (ViewLocationFlags)reader.ReadByte();
        bool sideScroll = reader.ReadBool();
        var start = reader.ReadVector(NetPrecision.Float);
        var end = sideScroll ? reader.ReadVector(NetPrecision.Float) : start;
        var angles = reader.ReadVector(NetPrecision.Float);
        return new(id, flags, sideScroll, start, end, angles);
    }
}
