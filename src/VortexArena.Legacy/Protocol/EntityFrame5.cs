// Port of Base/darkplaces/cl_ents5.c EntityState5_ReadUpdate and EntityFrame5_CL_ReadFrame, the
// entity_state_t of protocol.h, the defaultstate of protocol.c, and cl_input.c CL_NewFrameReceived.
using System.Numerics;

namespace VortexArena.Legacy.Protocol;

/// <summary>One of the four blended animations of an entity (framegroupblend_t).</summary>
public struct DpFrameGroupBlend
{
    public int Frame;
    public float Lerp;
    /// <summary>How long before the client's current time the animation started, in seconds.
    /// DarkPlaces stores <c>cl.time - age</c>; the decoder has no clock, so the age is kept instead.</summary>
    public float StartAge;
}

/// <summary>
/// What the server tells a client about one entity (entity_state_t, minus the fields marked "not sent
/// to client"). Field meanings and units are DarkPlaces': alpha is 0..255, scale is in sixteenths,
/// colormod and glowmod are in thirty-seconds, light is colour*256 and a radius.
/// </summary>
public struct EntityState
{
    public Vector3 Origin;
    public Vector3 Angles;
    public int Effects;
    public ushort Number;
    public ushort ModelIndex;
    public ushort Frame;
    public ushort TagEntity;
    public ushort TrailEffectNum;
    public ushort Light0, Light1, Light2, Light3;
    /// <summary><see cref="DpProtocol.ActiveNot"/> or <see cref="DpProtocol.ActiveNetwork"/>.</summary>
    public byte Active;
    public byte LightStyle;
    public byte LightPFlags;
    public byte Colormap;
    public byte Skin;
    public byte Alpha;
    public byte Scale;
    public byte GlowSize;
    public byte GlowColor;
    /// <summary>RENDER_* bits (protocol.h).</summary>
    public byte Flags;
    public byte TagIndex;
    public byte ColorMod0, ColorMod1, ColorMod2;
    public byte GlowMod0, GlowMod1, GlowMod2;
    public DpFrameGroupBlend Blend0, Blend1, Blend2, Blend3;
    /// <summary>E5_COMPLEXANIMATION type 4: the model the skeleton is for.</summary>
    public int SkeletonModelIndex;
    /// <summary>E5_COMPLEXANIMATION type 4: seven shorts per bone (origin xyz and quaternion xyz w, both
    /// scaled; Matrix4x4_FromBonePose7s turns them into a transform with origin scale 1/64). Null
    /// when the entity has no networked skeleton. Shared between copies of the state, as the C
    /// shares the skeleton pointer.</summary>
    public short[]? SkeletonPose7s;

    public readonly bool IsActive => Active == DpProtocol.ActiveNetwork;

    /// <summary>protocol.c defaultstate: everything zero except alpha 255, scale 16, glowcolor 254 and
    /// colormod/glowmod 32 (which is 1.0).</summary>
    public static EntityState Default => new()
    {
        Alpha = 255,
        Scale = 16,
        GlowColor = 254,
        ColorMod0 = 32, ColorMod1 = 32, ColorMod2 = 32,
        GlowMod0 = 32, GlowMod1 = 32, GlowMod2 = 32,
    };
}

/// <summary>What one svc_entities message contained.</summary>
public readonly struct DpEntityFrame
{
    /// <summary>The frame number to echo back in clc_ackframe.</summary>
    public int FrameNumber { get; init; }
    /// <summary>The sequence of the newest client input the server had applied (cls.servermovesequence).</summary>
    public int ServerMoveSequence { get; init; }
    /// <summary>Entities updated or removed by this frame, in wire order.</summary>
    public IReadOnlyList<int> Changed { get; init; }
}

/// <summary>
/// The client's table of networked entities and the decoder for svc_entities in DP5 and later
/// ("EntityFrame5"). The format is a stream of deltas against whatever the client currently holds:
///
/// <code>
/// long  framenum
/// long  servermovesequence        (DP7 and later)
/// repeat:
///   ushort n                      0x8000 ends the list
///   if n &amp; 0x8000: entity n&amp;0x7FFF is removed
///   else: 1 to 4 flag bytes (E5_EXTEND1/2/3 chain them), then the flagged fields in fixed order
/// </code>
///
/// Because the deltas are against the client's own state, a lost frame would corrupt it; the client
/// therefore acknowledges every frame number (clc_ackframe) and the server re-sends what was in an
/// unacknowledged frame. <see cref="CollectAcks"/> produces that list.
/// </summary>
public sealed class DpEntityTable
{
    private EntityState[] _current = new EntityState[256];
    private EntityState[] _previous = new EntityState[256];
    private EntityState[] _baseline = new EntityState[256];
    private readonly List<int> _changed = new();

    // cl.latestframenums / cl.latestsendnums: the last 32 frame numbers received, each with the input
    // sequence current at the time, so acks can be repeated across a few input packets.
    private readonly int[] _latestFrameNums = new int[DpProtocol.LatestFrameNums];
    private readonly uint[] _latestSendNums = new uint[DpProtocol.LatestFrameNums];
    private int _latestPosition;

    public DpEntityTable()
    {
        Clear();
    }

    /// <summary>One past the highest entity number seen (cl.num_entities).</summary>
    public int Count { get; private set; } = 1;

    /// <summary>The state of entity <paramref name="number"/> after the latest frame; the default state for one never mentioned.</summary>
    public ref readonly EntityState Current(int number) => ref Slot(_current, number);
    /// <summary>Its state before the latest update that touched it (state_previous), for interpolation.</summary>
    public ref readonly EntityState Previous(int number) => ref Slot(_previous, number);
    /// <summary>Its svc_spawnbaseline state. EntityFrame5 does not delta from it; DP keeps it for static entities and older protocols.</summary>
    public ref readonly EntityState Baseline(int number) => ref Slot(_baseline, number);

    private static readonly EntityState DefaultState = EntityState.Default;

    private static ref readonly EntityState Slot(EntityState[] array, int number)
    {
        if ((uint)number < (uint)array.Length)
            return ref array[number];
        return ref DefaultState;
    }

    /// <summary>CL_ClearState, as far as entities go: a new svc_serverinfo wipes the table.</summary>
    public void Clear()
    {
        Array.Fill(_current, EntityState.Default);
        Array.Fill(_previous, EntityState.Default);
        Array.Fill(_baseline, EntityState.Default);
        Array.Clear(_latestFrameNums);
        Array.Clear(_latestSendNums);
        _latestPosition = 0;
        Count = 1;
    }

    // CL_ExpandEntities. The caller has already checked number < MAX_EDICTS.
    private void Expand(int number)
    {
        if (number < _current.Length)
            return;
        int old = _current.Length;
        int size = Math.Min(DpProtocol.MaxEdicts, Math.Max(old * 2, number + 1));
        Array.Resize(ref _current, size);
        Array.Resize(ref _previous, size);
        Array.Resize(ref _baseline, size);
        Array.Fill(_current, EntityState.Default, old, size - old);
        Array.Fill(_previous, EntityState.Default, old, size - old);
        Array.Fill(_baseline, EntityState.Default, old, size - old);
    }

    /// <summary>CL_ParseBaseline's last line: the baseline becomes the entity's current and previous state too.</summary>
    public void SetBaseline(int number, in EntityState baseline)
    {
        if ((uint)number >= DpProtocol.MaxEdicts)
            return;
        Expand(number);
        _baseline[number] = baseline;
        _current[number] = baseline;
        _previous[number] = baseline;
    }

    /// <summary>
    /// EntityFrame5_CL_ReadFrame. On success <paramref name="frame"/> describes what changed. Returns
    /// false with <paramref name="error"/> set if the data cannot be an entity frame; running out of
    /// message is reported through <see cref="DpMessageReader.BadRead"/> like any other short read.
    /// <paramref name="moveSequence"/> is the client's current input sequence, remembered with the
    /// frame number for <see cref="CollectAcks"/>.
    /// </summary>
    public bool ReadFrame(DpMessageReader reader, uint moveSequence, out DpEntityFrame frame, out string? error)
    {
        error = null;
        _changed.Clear();
        int frameNumber = reader.ReadLong();
        // CL_NewFrameReceived
        _latestFrameNums[_latestPosition] = frameNumber;
        _latestSendNums[_latestPosition] = moveSequence;
        _latestPosition = (_latestPosition + 1) % DpProtocol.LatestFrameNums;
        int serverMoveSequence = reader.ReadLong();

        // read entity numbers until we find a 0x8000
        // (which would be remove world entity, but is actually a terminator)
        int n;
        while ((n = reader.ReadUShort()) != 0x8000 && !reader.BadRead)
        {
            int number = n & 0x7FFF; // 15 bits, so always below MAX_EDICTS
            Expand(number);
            if (Count <= number)
                Count = number + 1;
            // slide the current into the previous slot
            _previous[number] = _current[number];
            ref EntityState s = ref _current[number];
            if ((n & 0x8000) != 0)
                s = EntityState.Default; // remove entity
            else if (!ReadUpdate(reader, ref s, out error))
                break;
            // fix the number (it gets wiped occasionally by copying from defaultstate)
            s.Number = (ushort)number;
            _changed.Add(number);
        }
        frame = new DpEntityFrame
        {
            FrameNumber = frameNumber,
            ServerMoveSequence = serverMoveSequence,
            Changed = _changed.ToArray(),
        };
        return error is null;
    }

    /// <summary>
    /// EntityState5_ReadUpdate: one delta applied to <paramref name="s"/>. The order of the reads is
    /// the wire format and does not follow the order of the bits.
    /// </summary>
    public static bool ReadUpdate(DpMessageReader r, ref EntityState s, out string? error)
    {
        error = null;
        uint bits = (uint)r.ReadByte() & 0xFF;
        if ((bits & DpProtocol.E5Extend1) != 0)
        {
            bits |= ((uint)r.ReadByte() & 0xFF) << 8;
            if ((bits & DpProtocol.E5Extend2) != 0)
            {
                bits |= ((uint)r.ReadByte() & 0xFF) << 16;
                if ((bits & DpProtocol.E5Extend3) != 0)
                    bits |= ((uint)r.ReadByte() & 0xFF) << 24;
            }
        }
        if (r.BadRead)
            return true; // the caller sees BadRead; no field below may be trusted

        if ((bits & DpProtocol.E5FullUpdate) != 0)
        {
            s = EntityState.Default;
            s.Active = DpProtocol.ActiveNetwork;
        }
        if ((bits & DpProtocol.E5Flags) != 0)
            s.Flags = (byte)r.ReadByte();
        if ((bits & DpProtocol.E5Origin) != 0)
        {
            if ((bits & DpProtocol.E5Origin32) != 0)
                s.Origin = r.ReadVector();
            else
            {
                float x = r.ReadCoord13i(), y = r.ReadCoord13i(), z = r.ReadCoord13i();
                s.Origin = new Vector3(x, y, z);
            }
        }
        if ((bits & DpProtocol.E5Angles) != 0)
        {
            if ((bits & DpProtocol.E5Angles16) != 0)
                s.Angles = r.ReadAngles();
            else
            {
                float x = r.ReadAngle8i(), y = r.ReadAngle8i(), z = r.ReadAngle8i();
                s.Angles = new Vector3(x, y, z);
            }
        }
        if ((bits & DpProtocol.E5Model) != 0)
            s.ModelIndex = (bits & DpProtocol.E5Model16) != 0 ? (ushort)r.ReadShort() : (ushort)(r.ReadByte() & 0xFF);
        if ((bits & DpProtocol.E5Frame) != 0)
            s.Frame = (bits & DpProtocol.E5Frame16) != 0 ? (ushort)r.ReadShort() : (ushort)(r.ReadByte() & 0xFF);
        if ((bits & DpProtocol.E5Skin) != 0)
            s.Skin = (byte)r.ReadByte();
        if ((bits & DpProtocol.E5Effects) != 0)
        {
            if ((bits & DpProtocol.E5Effects32) != 0)
                s.Effects = r.ReadLong();
            else if ((bits & DpProtocol.E5Effects16) != 0)
                s.Effects = r.ReadUShort();
            else
                s.Effects = r.ReadByte() & 0xFF;
        }
        if ((bits & DpProtocol.E5Alpha) != 0)
            s.Alpha = (byte)r.ReadByte();
        if ((bits & DpProtocol.E5Scale) != 0)
            s.Scale = (byte)r.ReadByte();
        if ((bits & DpProtocol.E5Colormap) != 0)
            s.Colormap = (byte)r.ReadByte();
        if ((bits & DpProtocol.E5Attachment) != 0)
        {
            s.TagEntity = (ushort)r.ReadShort();
            s.TagIndex = (byte)r.ReadByte();
        }
        if ((bits & DpProtocol.E5Light) != 0)
        {
            s.Light0 = (ushort)r.ReadShort();
            s.Light1 = (ushort)r.ReadShort();
            s.Light2 = (ushort)r.ReadShort();
            s.Light3 = (ushort)r.ReadShort();
            s.LightStyle = (byte)r.ReadByte();
            s.LightPFlags = (byte)r.ReadByte();
        }
        if ((bits & DpProtocol.E5Glow) != 0)
        {
            s.GlowSize = (byte)r.ReadByte();
            s.GlowColor = (byte)r.ReadByte();
        }
        if ((bits & DpProtocol.E5ColorMod) != 0)
        {
            s.ColorMod0 = (byte)r.ReadByte();
            s.ColorMod1 = (byte)r.ReadByte();
            s.ColorMod2 = (byte)r.ReadByte();
        }
        if ((bits & DpProtocol.E5GlowMod) != 0)
        {
            s.GlowMod0 = (byte)r.ReadByte();
            s.GlowMod1 = (byte)r.ReadByte();
            s.GlowMod2 = (byte)r.ReadByte();
        }
        if ((bits & DpProtocol.E5ComplexAnimation) != 0 && !ReadComplexAnimation(r, ref s, out error))
            return false;
        if ((bits & DpProtocol.E5TrailEffectNum) != 0)
            s.TrailEffectNum = (ushort)r.ReadShort();
        return true;
    }

    // Types 0-3 are 1-4 blended frame groups: all the frames, then all the start ages (ms), then, when
    // there is more than one, all the weights. Type 4 is a whole skeleton.
    private static bool ReadComplexAnimation(DpMessageReader r, ref EntityState s, out string? error)
    {
        error = null;
        int type = r.ReadByte();
        if (r.BadRead)
            return true;
        if (type is >= 0 and <= 3)
        {
            int count = type + 1;
            Span<DpFrameGroupBlend> blend = stackalloc DpFrameGroupBlend[4];
            blend.Clear();
            for (int i = 0; i < count; i++)
                blend[i].Frame = r.ReadShort();
            for (int i = 0; i < count; i++)
                blend[i].StartAge = r.ReadUShort() * (1.0f / 1000.0f);
            if (count == 1)
                blend[0].Lerp = 1;
            else
                for (int i = 0; i < count; i++)
                    blend[i].Lerp = (r.ReadByte() & 0xFF) * (1.0f / 255.0f);
            s.Blend0 = blend[0];
            s.Blend1 = blend[1];
            s.Blend2 = blend[2];
            s.Blend3 = blend[3];
            return true;
        }
        if (type == 4)
        {
            s.SkeletonModelIndex = r.ReadShort();
            int bones = r.ReadByte();
            if (r.BadRead)
                return true;
            // The bone count is a byte, so at most 255 * 14 bytes: bounded, but still not worth
            // allocating for if the message cannot hold them. DarkPlaces also rejects a count that
            // disagrees with the model; the decoder has no models, so that check belongs to the caller.
            if (bones * 14 > r.Remaining)
            {
                r.ReadSpan(bones * 14); // consumes what is left and sets BadRead
                return true;
            }
            var pose = new short[bones * 7];
            for (int i = 0; i < pose.Length; i++)
                pose[i] = (short)r.ReadShort();
            s.SkeletonPose7s = pose;
            return true;
        }
        error = $"E5_COMPLEXANIMATION: unknown type {type}";
        return false;
    }

    /// <summary>
    /// The clc_ackframe list for the next input packet (cl_input.c:2121-2141): every remembered frame
    /// that arrived at or after input <paramref name="sequence"/> minus <paramref name="repeat"/>.
    /// Acks are repeated over a few packets on purpose, so that losing one input packet does not make
    /// the server re-send entities. Appends to <paramref name="frameNumbers"/>, oldest first.
    /// </summary>
    public void CollectAcks(uint sequence, int repeat, List<int> frameNumbers)
    {
        uint delta = (uint)Math.Clamp(repeat, 1, 3);
        uint oldSequence = sequence > delta ? sequence - delta : 1;
        for (int i = 0; i < DpProtocol.LatestFrameNums; i++)
        {
            int j = (_latestPosition + i) % DpProtocol.LatestFrameNums;
            if (_latestSendNums[j] >= oldSequence)
                frameNumbers.Add(_latestFrameNums[j]);
        }
    }
}
