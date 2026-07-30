using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>
/// Wire identifier for a network message, one byte on the wire (port of the <c>m_id</c> tag from
/// <c>net.qh</c>'s <c>LinkedEntities</c> / <c>TempEntities</c> / <c>C2S_Protocol</c> registries).
///
/// In QuakeC these are three separate registries whose ids are renumbered at startup
/// (LinkedEntities → 1.., TempEntities → 80.., C2S → 0..). Per <see href="ADR-0011"/> we own the
/// layout, so we keep a single flat byte space, partitioned by <see cref="NetChannel"/> range to
/// preserve the Linked / Temp / C2S distinction without three lookup tables. Ids here are illustrative
/// samples; the real set is generated from <c>[Register]</c> message types later (ADR-0003) and the
/// ordering is content-hashed for the build-parity gate (see <see cref="XonoticGodot.Common.Framework.Registry{T}"/>).
/// </summary>
public enum NetMessageId : byte
{
    None = 0,

    // --- C2S (client → server): client commands / input. QC C2S_Protocol. Range [1, 31]. ---
    C2S_Input = 1,

    // --- Linked (server → client, stateful, delta'd entities). QC LinkedEntities. Range [32, 159]. ---
    Ent_PlayerState = 32,

    // --- Temp (server → client, one-shot fire-and-forget events). QC TempEntities. Range [160, 255]. ---
    Temp_Explosion = 160,
}

/// <summary>
/// The conceptual channel a message belongs to, mirroring the three QC registries. Determines delivery
/// semantics: Linked = stateful (delta-compressed, kept in sync); Temp = one-shot event (fire & forget);
/// C2S = client→server input/command stream.
/// </summary>
public enum NetChannel : byte
{
    /// <summary>Client → server (input frames, commands). QC <c>C2S_Protocol</c>.</summary>
    ClientToServer = 0,
    /// <summary>Server → client stateful synchronized entity. QC <c>LinkedEntities</c>.</summary>
    Linked = 1,
    /// <summary>Server → client one-shot event. QC <c>TempEntities</c>.</summary>
    Temp = 2,
}

/// <summary>
/// Range partitioning of <see cref="NetMessageId"/> into <see cref="NetChannel"/>s, replacing QC's
/// three renumbered registries with one byte space. Centralised so the dispatcher and any future
/// source generator agree.
/// </summary>
public static class NetMessageRanges
{
    public const byte C2SFirst = 1, C2SLast = 31;
    public const byte LinkedFirst = 32, LinkedLast = 159;
    public const byte TempFirst = 160, TempLast = 255;

    public static NetChannel ChannelOf(NetMessageId id)
    {
        byte b = (byte)id;
        if (b >= TempFirst) return NetChannel.Temp;
        if (b >= LinkedFirst) return NetChannel.Linked;
        return NetChannel.ClientToServer;
    }
}

/// <summary>
/// Base for a typed network message: knows its <see cref="Id"/> and can round-trip its payload
/// (the C# successor to a QC message handler's matched <c>Write*</c>/<c>Read*</c> pair —
/// <c>CSQC_Ent_Update</c> / <c>CSQC_Parse_TempEntity</c>).
///
/// Convention: <see cref="Serialize"/> writes the payload only (the dispatcher writes the
/// <see cref="Id"/> header). <see cref="Deserialize"/> reads the payload back into <c>this</c>.
/// Concrete messages are mutable structs-of-fields wrapped as classes so the dispatcher can pool/reuse
/// one instance per id and refill it on read (avoiding per-message heap allocation on the parse path).
/// </summary>
public abstract class NetMessage
{
    /// <summary>The wire id for this message type.</summary>
    public abstract NetMessageId Id { get; }

    /// <summary>The channel (Linked/Temp/C2S) this message belongs to.</summary>
    public NetChannel Channel => NetMessageRanges.ChannelOf(Id);

    /// <summary>Write this message's payload (without the id header). Mirrors a QC <c>SendEntity</c> body.</summary>
    public abstract void Serialize(BitWriter w);

    /// <summary>Read this message's payload (without the id header) into <c>this</c>. Mirrors a QC <c>m_read</c> body.</summary>
    public abstract void Deserialize(ref BitReader r);
}

// =====================================================================================
// Sample messages — concrete proof of the round-trip (one Linked, one Temp).
// =====================================================================================

/// <summary>
/// Linked (stateful) entity-state update: the minimal core of a CSQC model update
/// (<c>ALLPROPERTIES</c> in <c>csqcmodel/common.qh</c>) — a network entity number, an origin, and
/// Euler angles, plus the "teleport" bit that cancels interpolation
/// (<c>CSQCMODEL_PROPERTY_TELEPORTED</c>). Origin uses the 13i coord path, angles the 8i path, matching
/// the property table's <c>WriteVector</c>/<c>WriteAngle</c> quantization. A real entity would also carry
/// a delta-changed-fields bitmask (<c>SendFlags</c>); omitted here to keep the sample focused on the
/// quantized round-trip.
/// </summary>
public sealed class EntityStateMessage : NetMessage
{
    /// <summary>Network entity id (QC <c>entnum</c>); 16-bit on the wire.</summary>
    public int EntNum;

    /// <summary>World position (13i-quantized).</summary>
    public Vector3 Origin;

    /// <summary>Euler angles in degrees, pitch/yaw/roll (8i-quantized).</summary>
    public Vector3 Angles;

    /// <summary>The teleport bit: when set, the client snaps instead of interpolating
    /// (<c>CSQCMODEL_PROPERTY_TELEPORTED</c> / <c>IFLAG_TELEPORTED</c>).</summary>
    public bool Teleported;

    public override NetMessageId Id => NetMessageId.Ent_PlayerState;

    public override void Serialize(BitWriter w)
    {
        w.WriteUShort(EntNum);
        w.WriteVector(Origin, NetPrecision.Low);  // 13i coords
        w.WriteAngles(Angles, NetPrecision.Low);  // 8i angles
        w.WriteBool(Teleported);
    }

    public override void Deserialize(ref BitReader r)
    {
        EntNum = r.ReadUShort();
        Origin = r.ReadVector(NetPrecision.Low);
        Angles = r.ReadAngles(NetPrecision.Low);
        Teleported = r.ReadBool();
    }
}

/// <summary>
/// Temp (one-shot) "explosion at point" event — the canonical <c>CSQC_Parse_TempEntity</c> case: a
/// world position (13i) plus a radius. No entity state is kept; the client spawns a visual/sound and
/// forgets it. Radius is sent as a byte (whole units, 0..255) like the compact effect params in the
/// QC temp-entity handlers.
/// </summary>
public sealed class ExplosionMessage : NetMessage
{
    /// <summary>Where the explosion happens (13i-quantized).</summary>
    public Vector3 Origin;

    /// <summary>Effect radius in world units (0..255).</summary>
    public int Radius;

    public override NetMessageId Id => NetMessageId.Temp_Explosion;

    public override void Serialize(BitWriter w)
    {
        w.WriteVector(Origin, NetPrecision.Low);
        w.WriteByte(Radius);
    }

    public override void Deserialize(ref BitReader r)
    {
        Origin = r.ReadVector(NetPrecision.Low);
        Radius = r.ReadByte();
    }
}
