// Port of Base/darkplaces/sv_ents5.c EntityFrame5_AllocDatabase, EntityFrame5_FreeDatabase,
// EntityFrame5_ExpandEdicts, EntityState5_Priority, EntityState5_DeltaBits, EntityState5_WriteUpdate,
// EntityFrame5_WriteFrame, EntityFrame5_LostFrame, EntityFrame5_AckFrame (and its anim_reducetime),
// the entity_state_t fields of protocol.h that are not sent to the client, and protocol.c
// Protocol_UpdateClientStats.
//
// The protocol is always DP7 (PROTOCOL_DARKPLACES7, wire number 3504). Where the C switches on
// sv.protocol only the DP7 branch is kept: stat updates travel inside the entity frame, and the
// frame header carries the move sequence.
using System.Numerics;
using VortexArena.Legacy.Protocol;

namespace VortexArena.Legacy.Server;

/// <summary>
/// The server's picture of one entity for one client (entity_state_t): the networked part the client
/// decoder also has (<see cref="Net"/>), plus the fields protocol.h marks "! not sent to client",
/// which only decide what is sent and how.
/// </summary>
public struct SvEntityState
{
    /// <summary>The part that goes on the wire. <see cref="EntityState.Number"/> is the entity number;
    /// <see cref="EntityState.Active"/> must be <see cref="DpProtocol.ActiveNetwork"/> for an entity
    /// that is to be sent. The decoder-only members (the blends' <c>StartAge</c> and
    /// <c>SkeletonModelIndex</c>) are ignored here: see <see cref="BlendStart0"/>.</summary>
    public EntityState Net;
    /// <summary>time: when this state was built.</summary>
    public double Time;
    /// <summary>netcenter: the centre of the bounding box, which the priority rule measures distance from.</summary>
    public Vector3 NetCenter;
    /// <summary>customizeentityforclient: the QuakeC function, if any.</summary>
    public uint CustomizeEntityForClient;
    /// <summary>specialvisibilityradius: larger if it has effects or a light.</summary>
    public ushort SpecialVisibilityRadius;
    public ushort ViewModelForClient;
    /// <summary>exteriormodelforclient: not shown to this client in first person.</summary>
    public ushort ExteriorModelForClient;
    public ushort NoDrawToClient;
    public ushort DrawOnlyToClient;
    /// <summary>internaleffects (INTEF_*).</summary>
    public byte InternalEffects;
    /// <summary>framegroupblend[n].start: the server time each of the four blended animations began.
    /// The client struct keeps an age instead (it has no server clock), so the absolute times the
    /// encoder compares and converts live here.</summary>
    public double BlendStart0, BlendStart1, BlendStart2, BlendStart3;
}

/// <summary>What <see cref="SvEntityFrame5Database"/> needs from the rest of the server.</summary>
public interface ISvEntityFrame5Host
{
    /// <summary>svs.maxclients. Entities 1..MaxClients are players: sent sooner and never with low-precision origins.</summary>
    int MaxClients { get; }
    /// <summary>prog->max_edicts: how many entity slots the database must at least hold.</summary>
    int MaxEdicts { get; }
    /// <summary>sv.time, for the animation ages of E5_COMPLEXANIMATION.</summary>
    double Time { get; }
    /// <summary>PRVM_serveredictfunction(edict, SendEntity) != 0: the entity is networked by the game's
    /// QuakeC (svc_csqcentities), so EntityState5_WriteUpdate writes nothing for it.</summary>
    bool HasSendEntity(int entityNumber);
    /// <summary>anim_frameduration(SV_GetModelByIndex(modelIndex), frame): framecount / framerate of
    /// that animation scene in seconds, or 0 when there is no such model, scene or rate.</summary>
    double FrameDuration(int modelIndex, int frame);
}

/// <summary>
/// One client's svc_entities encoder (entityframe5_database_t). It holds what the client is believed
/// to know about every entity and, per entity, which fields changed since they were last sent
/// (deltabits) and how urgent that is (priorities). Each frame the changed entities are queued by
/// priority and written until the packet is full; what did not fit keeps its bits and ages into a
/// higher priority.
///
/// Because the stream is deltas against the client's own state, every frame sent is logged with the
/// bits it carried. The client acknowledges frame numbers (clc_ackframe): an acknowledged log is
/// dropped, and a log the client skipped over puts its bits back so the fields are sent again.
/// </summary>
public sealed class SvEntityFrame5Database
{
    public const int MaxPacketLogs = 64;   // ENTITYFRAME5_MAXPACKETLOGS
    public const int MaxStates = 1024;     // ENTITYFRAME5_MAXSTATES: entities per frame, and per priority level
    public const int PriorityLevels = 32;  // ENTITYFRAME5_PRIORITYLEVELS

    // protocol.h RENDER_*, the three entity_state_t.flags bits the encoder looks at.
    public const int RenderViewModel = 4;
    public const int RenderLowPrecision = 16;
    public const int RenderComplexAnimation = 128;

    /// <summary>"unsigned char data[128]" in EntityFrame5_WriteFrame: the most one entity update may take.</summary>
    public const int MaxUpdateSize = 128;

    private const int StatsBytes = (DpProtocol.MaxClStats + 7) / 8;
    private const uint ImportantBits = DpProtocol.E5FullUpdate | DpProtocol.E5Attachment | DpProtocol.E5Model | DpProtocol.E5Colormap;

    // protocol.c defaultstate. Immutable, so sharing it between databases holds no state.
    private static readonly SvEntityState DefaultState = new() { Net = EntityState.Default };

    private readonly ISvEntityFrame5Host _host;

    private int _latestFrameNum;
    private int _viewEntNum;

    // entityframe5_packetlog_t[ENTITYFRAME5_MAXPACKETLOGS], as parallel arrays. packetnumber 0 marks a
    // free slot (frame numbers start at 1). A slot's state list is allocated the first time it is used.
    private readonly int[] _logPacketNumber = new int[MaxPacketLogs];
    private readonly int[] _logNumStates = new int[MaxPacketLogs];
    private readonly ushort[]?[] _logNumbers = new ushort[MaxPacketLogs][];
    private readonly uint[]?[] _logBits = new uint[MaxPacketLogs][];
    private readonly byte[] _logStatsDeltaBits = new byte[MaxPacketLogs * StatsBytes];

    private int _maxEdicts;
    private uint[] _deltaBits = Array.Empty<uint>();
    private byte[] _priorities = Array.Empty<byte>();
    private int[] _updateFrameNum = Array.Empty<int>();
    private SvEntityState[] _states = Array.Empty<SvEntityState>();
    private byte[] _visibleBits = Array.Empty<byte>();

    private readonly int[] _priorityChainCounts = new int[PriorityLevels];
    private readonly ushort[] _priorityChains = new ushort[PriorityLevels * MaxStates];

    // EntityFrame5_LostFrame's "static int deltabits[MAX_EDICTS]", per database so nothing is shared.
    private uint[] _lostDeltaBits = Array.Empty<uint>();
    private readonly byte[] _lostStatsDeltaBits = new byte[StatsBytes];

    private readonly DpMessageWriter _buf = new(MaxUpdateSize, MaxUpdateSize);

    // client_t.stats / statsdeltabits. They live here because DP7 sends stats through this encoder and
    // logs them in its packet log, and because SV_SendServerinfo clears them with the database.
    private readonly int[] _stats = new int[DpProtocol.MaxClStats];
    private readonly byte[] _statsDeltaBits = new byte[StatsBytes];

    /// <summary>EntityFrame5_AllocDatabase.</summary>
    public SvEntityFrame5Database(ISvEntityFrame5Host host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>latestframenum: the number of the last svc_entities written; the next is one higher.
    /// A clc_ackframe above this cannot be a frame this database sent.</summary>
    public int LatestFrameNumber => _latestFrameNum;

    /// <summary>maxedicts: how many entity slots are allocated. Never above <see cref="DpProtocol.MaxEdicts"/>.</summary>
    public int AllocatedEdicts => _maxEdicts;

    /// <summary>How many packet logs are waiting for an acknowledgement.</summary>
    public int PendingPacketLogs
    {
        get
        {
            int n = 0;
            foreach (int p in _logPacketNumber)
                if (p != 0) n++;
            return n;
        }
    }

    /// <summary>The fields of entity <paramref name="number"/> still waiting to be sent (deltabits), 0 if out of range.</summary>
    public uint PendingBits(int number) => (uint)number < (uint)_maxEdicts ? _deltaBits[number] : 0;

    /// <summary>Its queue priority, 0 when nothing is pending.</summary>
    public int PendingPriority(int number) => (uint)number < (uint)_maxEdicts ? _priorities[number] : 0;

    /// <summary>client_t.stats as last given to <see cref="UpdateStats"/>.</summary>
    public ReadOnlySpan<int> Stats => _stats;

    /// <summary>
    /// EntityFrame5_FreeDatabase followed by EntityFrame5_AllocDatabase, and the two memsets beside
    /// them in sv_main.c SV_SendServerinfo: DarkPlaces throws the database away and starts a new one
    /// every time it sends svc_serverinfo (connect, reconnect, level change), because the client wipes
    /// its entity table then. Frame numbers start again at 1.
    /// </summary>
    public void Reset()
    {
        _latestFrameNum = 0;
        _viewEntNum = 0;
        Array.Clear(_logPacketNumber);
        Array.Clear(_logNumStates);
        Array.Clear(_logStatsDeltaBits);
        _maxEdicts = 0;
        _deltaBits = Array.Empty<uint>();
        _priorities = Array.Empty<byte>();
        _updateFrameNum = Array.Empty<int>();
        _states = Array.Empty<SvEntityState>();
        _visibleBits = Array.Empty<byte>();
        _lostDeltaBits = Array.Empty<uint>();
        Array.Clear(_priorityChainCounts);
        Array.Clear(_stats);
        Array.Clear(_statsDeltaBits);
    }

    /// <summary>Protocol_UpdateClientStats: remember the client's stats and flag the ones that changed,
    /// so the next <see cref="WriteFrame"/> sends them. Entries beyond MAX_CL_STATS are ignored.</summary>
    public void UpdateStats(ReadOnlySpan<int> stats)
    {
        int count = Math.Min(stats.Length, DpProtocol.MaxClStats);
        for (int i = 0; i < count; i++)
        {
            if (_stats[i] != stats[i])
            {
                _statsDeltaBits[i >> 3] |= (byte)(1 << (i & 7));
                _stats[i] = stats[i];
            }
        }
    }

    // EntityFrame5_ExpandEdicts. The C trusts its caller; here the size is capped at the protocol's
    // MAX_EDICTS, so no input can make the database grow past that. New slots are zero, as Mem_Alloc
    // leaves them: not the default state, but inactive, which is all the encoder reads of a slot that
    // has never been filled.
    private void ExpandEdicts(int newMax)
    {
        newMax = Math.Min(newMax, DpProtocol.MaxEdicts);
        if (_maxEdicts >= newMax)
            return;
        Array.Resize(ref _deltaBits, newMax);
        Array.Resize(ref _priorities, newMax);
        Array.Resize(ref _updateFrameNum, newMax);
        Array.Resize(ref _states, newMax);
        Array.Resize(ref _visibleBits, (newMax + 7) / 8);
        _maxEdicts = newMax;
    }

    private static int Bound(int min, int value, int max) => value < min ? min : value > max ? max : value;

    // EntityState5_Priority.
    private int Priority(int stateIndex)
    {
        // if it is the player, update urgently
        if (stateIndex == _viewEntNum)
            return PriorityLevels - 1;
        // priority increases each frame no matter what happens
        int priority = _priorities[stateIndex] + 1;
        // players get an extra priority boost
        if (stateIndex <= _host.MaxClients)
            priority++;
        // remove dead entities very quickly because they are just 2 bytes
        if (_states[stateIndex].Net.Active != DpProtocol.ActiveNetwork)
        {
            priority++;
            return Bound(1, priority, PriorityLevels - 1);
        }
        // certain changes are more noticable than others
        if ((_deltaBits[stateIndex] & (DpProtocol.E5FullUpdate | DpProtocol.E5Attachment | DpProtocol.E5Model | DpProtocol.E5Flags | DpProtocol.E5Colormap)) != 0)
            priority++;
        // find the root entity this one is attached to, and judge relevance by it
        // (limited to 256 links, so entities attached in a ring cannot hang the server)
        int examined = stateIndex;
        for (int limit = 0; limit < 256; limit++)
        {
            examined = stateIndex;
            ref readonly SvEntityState s = ref _states[stateIndex];
            int next;
            if ((s.Net.Flags & RenderViewModel) != 0)
                next = _viewEntNum;
            else if (s.Net.TagEntity != 0)
                next = s.Net.TagEntity;
            else
                break;
            // Deviation: tagentity is 16 bits, so it can name a slot beyond MAX_EDICTS, which the C
            // would allocate for (and it tests "maxedicts < stateindex", one short, so an index equal
            // to maxedicts reads past the arrays). Such a link is treated as the end of the chain.
            if (next >= DpProtocol.MaxEdicts)
                break;
            if (next >= _maxEdicts)
                ExpandEdicts((next + 256) & ~255);
            stateIndex = next;
        }
        // now that we have the parent entity we can make some decisions based on
        // distance from the player
        if (Vector3.Distance(_states[_viewEntNum].NetCenter, _states[examined].NetCenter) < 1024.0f)
            priority++;
        return Bound(1, priority, PriorityLevels - 1);
    }

    private static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    // memcmp(o->framegroupblend, n->framegroupblend): bitwise, so a NaN equals itself and does not
    // make the entity resend every frame.
    private static bool SameBlend(in DpFrameGroupBlend a, double aStart, in DpFrameGroupBlend b, double bStart) =>
        a.Frame == b.Frame && SameBits(a.Lerp, b.Lerp) && SameBits(aStart, bStart);

    /// <summary>
    /// EntityState5_DeltaBits: which E5_* groups differ between what the client has
    /// (<paramref name="o"/>) and what it should have (<paramref name="n"/>). An entity appearing or
    /// disappearing is E5_FULLUPDATE. The width bits (E5_ORIGIN32 and so on) are not decided here;
    /// <see cref="WriteUpdate"/> adds them from the values.
    /// </summary>
    public static uint DeltaBits(in SvEntityState o, in SvEntityState n)
    {
        uint bits = 0;
        ref readonly EntityState os = ref o.Net;
        ref readonly EntityState ns = ref n.Net;
        if (ns.Active == DpProtocol.ActiveNetwork)
        {
            if (os.Active != DpProtocol.ActiveNetwork)
                bits |= DpProtocol.E5FullUpdate;
            if (os.Origin != ns.Origin)
                bits |= DpProtocol.E5Origin;
            if (os.Angles != ns.Angles)
                bits |= DpProtocol.E5Angles;
            if (os.ModelIndex != ns.ModelIndex)
                bits |= DpProtocol.E5Model;
            if (os.Frame != ns.Frame)
                bits |= DpProtocol.E5Frame;
            if (os.Skin != ns.Skin)
                bits |= DpProtocol.E5Skin;
            if (os.Effects != ns.Effects)
                bits |= DpProtocol.E5Effects;
            if (os.Flags != ns.Flags)
                bits |= DpProtocol.E5Flags;
            if (os.Alpha != ns.Alpha)
                bits |= DpProtocol.E5Alpha;
            if (os.Scale != ns.Scale)
                bits |= DpProtocol.E5Scale;
            if (os.Colormap != ns.Colormap)
                bits |= DpProtocol.E5Colormap;
            if (os.TagEntity != ns.TagEntity || os.TagIndex != ns.TagIndex)
                bits |= DpProtocol.E5Attachment;
            if (os.Light0 != ns.Light0 || os.Light1 != ns.Light1 || os.Light2 != ns.Light2 || os.Light3 != ns.Light3 || os.LightStyle != ns.LightStyle || os.LightPFlags != ns.LightPFlags)
                bits |= DpProtocol.E5Light;
            if (os.GlowSize != ns.GlowSize || os.GlowColor != ns.GlowColor)
                bits |= DpProtocol.E5Glow;
            if (os.ColorMod0 != ns.ColorMod0 || os.ColorMod1 != ns.ColorMod1 || os.ColorMod2 != ns.ColorMod2)
                bits |= DpProtocol.E5ColorMod;
            if (os.GlowMod0 != ns.GlowMod0 || os.GlowMod1 != ns.GlowMod1 || os.GlowMod2 != ns.GlowMod2)
                bits |= DpProtocol.E5GlowMod;
            if ((ns.Flags & RenderComplexAnimation) != 0)
            {
                // Deviation: the C state points at the edict's skeleton matrices and converts them to
                // seven shorts per bone as it writes. Here the state carries the shorts already
                // (EntityState.SkeletonPose7s, null for "no skeleton"), so what is compared is the
                // quantised pose. Two states sharing one array compare equal without a look, exactly
                // as the C's memcmp of one buffer against itself does.
                short[]? op = os.SkeletonPose7s, np = ns.SkeletonPose7s;
                if ((op is not null) != (np is not null))
                    bits |= DpProtocol.E5ComplexAnimation;
                else if (op is not null && np is not null)
                {
                    if (os.ModelIndex != ns.ModelIndex)
                        bits |= DpProtocol.E5ComplexAnimation;
                    else if (op.Length != np.Length)
                        bits |= DpProtocol.E5ComplexAnimation;
                    else if (!ReferenceEquals(op, np) && !op.AsSpan().SequenceEqual(np))
                        bits |= DpProtocol.E5ComplexAnimation;
                }
                else if (!SameBlend(os.Blend0, o.BlendStart0, ns.Blend0, n.BlendStart0)
                    || !SameBlend(os.Blend1, o.BlendStart1, ns.Blend1, n.BlendStart1)
                    || !SameBlend(os.Blend2, o.BlendStart2, ns.Blend2, n.BlendStart2)
                    || !SameBlend(os.Blend3, o.BlendStart3, ns.Blend3, n.BlendStart3))
                {
                    bits |= DpProtocol.E5ComplexAnimation;
                }
            }
            if (os.TrailEffectNum != ns.TrailEffectNum)
                bits |= DpProtocol.E5TrailEffectNum;
        }
        else if (os.Active == DpProtocol.ActiveNetwork)
            bits |= DpProtocol.E5FullUpdate;
        return bits;
    }

    // anim_reducetime: an animation's age has 16 bits of milliseconds on the wire. An age beyond that
    // is brought back into range by whole loops of the animation, so the client still shows the right
    // phase; that only works if at least two loops fit.
    private static double AnimReduceTime(double t, double frameDuration, double maxTime)
    {
        if (t < 0) // clamp to non-negative
            return 0;
        if (t <= maxTime) // time can be represented normally
            return t;
        if (frameDuration == 0) // don't like dividing by zero
            return t;
        if (maxTime <= 2 * frameDuration) // if two frames don't fit, we better not do this
            return t;
        t -= frameDuration * Math.Ceiling((t - maxTime) / frameDuration);
        // now maxtime - frameduration < t <= maxtime
        return t;
    }

    private static int AnimAgeMs(ISvEntityFrame5Host host, int modelIndex, int frame, double start) =>
        (int)(AnimReduceTime(host.Time - start, host.FrameDuration(modelIndex, frame), 65.535) * 1000.0);

    /// <summary>
    /// EntityState5_WriteUpdate: one entity's record. An entity that is not active is the two-byte
    /// removal (number with bit 15 set). Otherwise the number, one to four flag bytes and the flagged
    /// fields, in the order the client reads them, which is not the order of the bits. Nothing at all
    /// is written for an entity the game's QuakeC networks itself.
    /// </summary>
    public static void WriteUpdate(int number, in SvEntityState state, uint changedBits, DpMessageWriter msg, ISvEntityFrame5Host host)
    {
        ref readonly EntityState s = ref state.Net;
        if (s.Active != DpProtocol.ActiveNetwork)
        {
            msg.WriteShort(number | 0x8000);
            return;
        }
        if (host.HasSendEntity(s.Number))
            return;

        uint bits = changedBits;
        // A short holds an eighth of a unit over -4096..4096, so low precision is only for entities
        // that asked for it, are inside that box, and are not something the player looks along or
        // from (players, attachments, view and exterior models), where an eighth of a unit shows.
        // possible values:
        //   negative origin:
        //     (int)(f * 8 - 0.5) >= -32768
        //          (f * 8 - 0.5) >  -32769
        //           f            >  -4096.0625
        //   positive origin:
        //     (int)(f * 8 + 0.5) <=  32767
        //          (f * 8 + 0.5) <   32768
        //           f            <   4095.9375
        if ((bits & DpProtocol.E5Origin) != 0 && ((s.Flags & RenderLowPrecision) == 0 || state.ExteriorModelForClient != 0 || s.TagEntity != 0 || state.ViewModelForClient != 0
            || (s.Number >= 1 && s.Number <= host.MaxClients)
            || s.Origin.X <= -4096.0625f || s.Origin.X >= 4095.9375f || s.Origin.Y <= -4096.0625f || s.Origin.Y >= 4095.9375f || s.Origin.Z <= -4096.0625f || s.Origin.Z >= 4095.9375f))
            bits |= DpProtocol.E5Origin32;
        if ((bits & DpProtocol.E5Angles) != 0 && (s.Flags & RenderLowPrecision) == 0)
            bits |= DpProtocol.E5Angles16;
        if ((bits & DpProtocol.E5Model) != 0 && s.ModelIndex >= 256)
            bits |= DpProtocol.E5Model16;
        if ((bits & DpProtocol.E5Frame) != 0 && s.Frame >= 256)
            bits |= DpProtocol.E5Frame16;
        if ((bits & DpProtocol.E5Effects) != 0)
        {
            if ((s.Effects & 0xFFFF0000) != 0)
                bits |= DpProtocol.E5Effects32;
            else if ((s.Effects & 0xFFFFFF00) != 0)
                bits |= DpProtocol.E5Effects16;
        }
        if (bits >= 256)
            bits |= DpProtocol.E5Extend1;
        if (bits >= 65536)
            bits |= DpProtocol.E5Extend2;
        if (bits >= 16777216)
            bits |= DpProtocol.E5Extend3;

        msg.WriteShort(number);
        msg.WriteByte((int)(bits & 0xFF));
        if ((bits & DpProtocol.E5Extend1) != 0)
            msg.WriteByte((int)((bits >> 8) & 0xFF));
        if ((bits & DpProtocol.E5Extend2) != 0)
            msg.WriteByte((int)((bits >> 16) & 0xFF));
        if ((bits & DpProtocol.E5Extend3) != 0)
            msg.WriteByte((int)((bits >> 24) & 0xFF));
        if ((bits & DpProtocol.E5Flags) != 0)
            msg.WriteByte(s.Flags);
        if ((bits & DpProtocol.E5Origin) != 0)
        {
            if ((bits & DpProtocol.E5Origin32) != 0)
            {
                msg.WriteFloat(s.Origin.X);
                msg.WriteFloat(s.Origin.Y);
                msg.WriteFloat(s.Origin.Z);
            }
            else
            {
                msg.WriteCoord13i(s.Origin.X);
                msg.WriteCoord13i(s.Origin.Y);
                msg.WriteCoord13i(s.Origin.Z);
            }
        }
        if ((bits & DpProtocol.E5Angles) != 0)
        {
            if ((bits & DpProtocol.E5Angles16) != 0)
            {
                msg.WriteAngle16i(s.Angles.X);
                msg.WriteAngle16i(s.Angles.Y);
                msg.WriteAngle16i(s.Angles.Z);
            }
            else
            {
                msg.WriteAngle8i(s.Angles.X);
                msg.WriteAngle8i(s.Angles.Y);
                msg.WriteAngle8i(s.Angles.Z);
            }
        }
        if ((bits & DpProtocol.E5Model) != 0)
        {
            if ((bits & DpProtocol.E5Model16) != 0)
                msg.WriteShort(s.ModelIndex);
            else
                msg.WriteByte(s.ModelIndex);
        }
        if ((bits & DpProtocol.E5Frame) != 0)
        {
            if ((bits & DpProtocol.E5Frame16) != 0)
                msg.WriteShort(s.Frame);
            else
                msg.WriteByte(s.Frame);
        }
        if ((bits & DpProtocol.E5Skin) != 0)
            msg.WriteByte(s.Skin);
        if ((bits & DpProtocol.E5Effects) != 0)
        {
            if ((bits & DpProtocol.E5Effects32) != 0)
                msg.WriteLong(s.Effects);
            else if ((bits & DpProtocol.E5Effects16) != 0)
                msg.WriteShort(s.Effects);
            else
                msg.WriteByte(s.Effects);
        }
        if ((bits & DpProtocol.E5Alpha) != 0)
            msg.WriteByte(s.Alpha);
        if ((bits & DpProtocol.E5Scale) != 0)
            msg.WriteByte(s.Scale);
        if ((bits & DpProtocol.E5Colormap) != 0)
            msg.WriteByte(s.Colormap);
        if ((bits & DpProtocol.E5Attachment) != 0)
        {
            msg.WriteShort(s.TagEntity);
            msg.WriteByte(s.TagIndex);
        }
        if ((bits & DpProtocol.E5Light) != 0)
        {
            msg.WriteShort(s.Light0);
            msg.WriteShort(s.Light1);
            msg.WriteShort(s.Light2);
            msg.WriteShort(s.Light3);
            msg.WriteByte(s.LightStyle);
            msg.WriteByte(s.LightPFlags);
        }
        if ((bits & DpProtocol.E5Glow) != 0)
        {
            msg.WriteByte(s.GlowSize);
            msg.WriteByte(s.GlowColor);
        }
        if ((bits & DpProtocol.E5ColorMod) != 0)
        {
            msg.WriteByte(s.ColorMod0);
            msg.WriteByte(s.ColorMod1);
            msg.WriteByte(s.ColorMod2);
        }
        if ((bits & DpProtocol.E5GlowMod) != 0)
        {
            msg.WriteByte(s.GlowMod0);
            msg.WriteByte(s.GlowMod1);
            msg.WriteByte(s.GlowMod2);
        }
        if ((bits & DpProtocol.E5ComplexAnimation) != 0)
            WriteComplexAnimation(state, msg, host);
        if ((bits & DpProtocol.E5TrailEffectNum) != 0)
            msg.WriteShort(s.TrailEffectNum);
    }

    // Type 4 is a whole skeleton. Types 0-3 are one to four blended animations, chosen by the last
    // blend with any weight: all the frames, then all the ages in milliseconds, then (when there is
    // more than one) all the weights.
    private static void WriteComplexAnimation(in SvEntityState state, DpMessageWriter msg, ISvEntityFrame5Host host)
    {
        ref readonly EntityState s = ref state.Net;
        if (s.SkeletonPose7s is { } pose)
        {
            // The count is one byte on the wire. The C writes numbones & 255 and then every bone, which
            // the client cannot read back; here the bones beyond 255 are left off instead. (Either way
            // more than 8 bones do not fit the 128-byte update buffer: see WriteFrame.)
            int numBones = Math.Min(pose.Length / 7, 255);
            msg.WriteByte(4);
            msg.WriteShort(s.ModelIndex);
            msg.WriteByte(numBones);
            for (int i = 0; i < numBones * 7; i++)
                msg.WriteShort(pose[i]);
            return;
        }
        int model = s.ModelIndex;
        if (s.Blend3.Lerp > 0)
        {
            msg.WriteByte(3);
            msg.WriteShort(s.Blend0.Frame);
            msg.WriteShort(s.Blend1.Frame);
            msg.WriteShort(s.Blend2.Frame);
            msg.WriteShort(s.Blend3.Frame);
            msg.WriteShort(AnimAgeMs(host, model, s.Blend0.Frame, state.BlendStart0));
            msg.WriteShort(AnimAgeMs(host, model, s.Blend1.Frame, state.BlendStart1));
            msg.WriteShort(AnimAgeMs(host, model, s.Blend2.Frame, state.BlendStart2));
            msg.WriteShort(AnimAgeMs(host, model, s.Blend3.Frame, state.BlendStart3));
            msg.WriteByte((int)(s.Blend0.Lerp * 255.0f));
            msg.WriteByte((int)(s.Blend1.Lerp * 255.0f));
            msg.WriteByte((int)(s.Blend2.Lerp * 255.0f));
            msg.WriteByte((int)(s.Blend3.Lerp * 255.0f));
        }
        else if (s.Blend2.Lerp > 0)
        {
            msg.WriteByte(2);
            msg.WriteShort(s.Blend0.Frame);
            msg.WriteShort(s.Blend1.Frame);
            msg.WriteShort(s.Blend2.Frame);
            msg.WriteShort(AnimAgeMs(host, model, s.Blend0.Frame, state.BlendStart0));
            msg.WriteShort(AnimAgeMs(host, model, s.Blend1.Frame, state.BlendStart1));
            msg.WriteShort(AnimAgeMs(host, model, s.Blend2.Frame, state.BlendStart2));
            msg.WriteByte((int)(s.Blend0.Lerp * 255.0f));
            msg.WriteByte((int)(s.Blend1.Lerp * 255.0f));
            msg.WriteByte((int)(s.Blend2.Lerp * 255.0f));
        }
        else if (s.Blend1.Lerp > 0)
        {
            msg.WriteByte(1);
            msg.WriteShort(s.Blend0.Frame);
            msg.WriteShort(s.Blend1.Frame);
            msg.WriteShort(AnimAgeMs(host, model, s.Blend0.Frame, state.BlendStart0));
            msg.WriteShort(AnimAgeMs(host, model, s.Blend1.Frame, state.BlendStart1));
            msg.WriteByte((int)(s.Blend0.Lerp * 255.0f));
            msg.WriteByte((int)(s.Blend1.Lerp * 255.0f));
        }
        else
        {
            msg.WriteByte(0);
            msg.WriteShort(s.Blend0.Frame);
            msg.WriteShort(AnimAgeMs(host, model, s.Blend0.Frame, state.BlendStart0));
        }
    }

    private bool IsVisible(int num) => (_visibleBits[num >> 3] & (1 << (num & 7))) != 0;

    // "if the entity used to exist, clear it"
    private void MarkRemoved(int num)
    {
        if (!IsVisible(num))
            return;
        _visibleBits[num >> 3] &= (byte)~(1 << (num & 7));
        _deltaBits[num] = DpProtocol.E5FullUpdate;
        _priorities[num] = Math.Max(_priorities[num], (byte)8); // removal is cheap
        _states[num] = DefaultState;
        _states[num].Net.Number = (ushort)num;
    }

    // "add packetlog entry now that we have something for it"
    private void BeginPacketLog(int slot, int frameNum)
    {
        _logPacketNumber[slot] = frameNum;
        _logNumStates[slot] = 0;
        _logStatsDeltaBits.AsSpan(slot * StatsBytes, StatsBytes).Clear();
    }

    /// <summary>
    /// EntityFrame5_WriteFrame: take this frame's list of entities visible to the client, work out
    /// what changed, and append to <paramref name="msg"/> any changed stats (svc_updatestat /
    /// svc_updatestatubyte) and one svc_entities carrying as many changed entities as fit below
    /// <paramref name="maxSize"/>, most urgent first:
    ///
    /// <code>
    /// byte  svc_entities (57)
    /// long  framenum                  LatestFrameNumber after the call
    /// long  movesequence              the newest client input the server has applied
    /// repeat: one entity record       see WriteUpdate
    /// short 0x8000                    terminator
    /// </code>
    ///
    /// <paramref name="states"/> must be in rising entity-number order, numbers from 1; an entity
    /// missing from the list is one the client must remove. Returns false, having written nothing,
    /// when there is nothing to say and <paramref name="needEmpty"/> does not ask for an empty frame
    /// (the caller wants one whenever the client's move sequence advanced, when it wrote CSQC entities
    /// for this frame number, and every 16th skipped frame).
    ///
    /// The caller must leave room for the 11 bytes of an empty frame: only the entity records are
    /// measured against <paramref name="maxSize"/>, as in the C.
    /// </summary>
    public bool WriteFrame(DpMessageWriter msg, int maxSize, ReadOnlySpan<SvEntityState> states, int viewEntityNumber, uint moveSequence, bool needEmpty)
    {
        ArgumentNullException.ThrowIfNull(msg);
        // sizebuf_t would Host_Error on a write past its own end; DpMessageWriter drops the write. Neither
        // is wanted, so the budget never exceeds what the message can hold.
        maxSize = Math.Min(maxSize, msg.MaxSize);

        int hostMaxEdicts = Math.Min(_host.MaxEdicts, DpProtocol.MaxEdicts);
        if (hostMaxEdicts > _maxEdicts)
            ExpandEdicts(hostMaxEdicts);

        int frameNum = unchecked(_latestFrameNum + 1);
        // The C indexes states[viewentnum] unchecked (it is always a client slot there). An entity
        // number that cannot exist becomes the world, which is at the origin and never sent.
        if ((uint)viewEntityNumber >= DpProtocol.MaxEdicts)
            viewEntityNumber = 0;
        if (viewEntityNumber >= _maxEdicts)
            ExpandEdicts((viewEntityNumber + 256) & ~255);
        _viewEntNum = viewEntityNumber;

        // if packet log is full, mark all frames as lost, this will cause
        // it to send the lost data again
        int packetLogNumber;
        for (packetLogNumber = 0; packetLogNumber < MaxPacketLogs; packetLogNumber++)
            if (_logPacketNumber[packetLogNumber] == 0)
                break;
        if (packetLogNumber == MaxPacketLogs)
        {
            LostFrame(frameNum);
            packetLogNumber = 0;
        }

        // detect changes in states
        int num = 1;
        foreach (ref readonly SvEntityState n in states)
        {
            int number = n.Net.Number;
            // Deviation: the C trusts the list to be sorted and in range, and would file an
            // out-of-order entity under the wrong number. Such an entry is skipped here.
            if (number < num || number >= DpProtocol.MaxEdicts)
                continue;
            if (number >= _maxEdicts)
                ExpandEdicts((number + 256) & ~255);
            // mark gaps in entity numbering as removed entities
            for (; num < number; num++)
                MarkRemoved(num);
            // update the entity state data
            if (!IsVisible(num))
            {
                // entity just spawned in, don't let it completely hog priority
                // because of being ancient on the first frame
                _updateFrameNum[num] = frameNum;
                // initial priority is a bit high to make projectiles send on the
                // first frame, among other things
                _priorities[num] = Math.Max(_priorities[num], (byte)4);
            }
            _visibleBits[num >> 3] |= (byte)(1 << (num & 7));
            _deltaBits[num] |= DeltaBits(_states[num], n);
            _priorities[num] = Math.Max(_priorities[num], (byte)1);
            _states[num] = n;
            _states[num].Net.Number = (ushort)num;
            // advance to next entity so the next iteration doesn't immediately remove it
            num++;
        }
        // all remaining entities are dead
        for (; num < _maxEdicts; num++)
            MarkRemoved(num);

        // "if there isn't at least enough room for an empty svc_entities, don't bother trying": the C
        // makes this test against its 128-byte scratch buffer, where it can never be true. It is made
        // against the message here, which is the only place it can mean anything.
        if (msg.Length + 11 > msg.MaxSize)
            return false;

        // build lists of entities by priority level
        Array.Clear(_priorityChainCounts);
        bool anything = false;
        for (num = 0; num < _maxEdicts; num++)
        {
            if (_priorities[num] == 0)
                continue;
            if (_deltaBits[num] != 0)
            {
                // an entity already at the top level stays there without being judged again
                if (_priorities[num] < PriorityLevels - 1)
                    _priorities[num] = (byte)Priority(num);
                anything = true;
                int priority = _priorities[num];
                if (_priorityChainCounts[priority] < MaxStates)
                    _priorityChains[priority * MaxStates + _priorityChainCounts[priority]++] = (ushort)num;
            }
            else
                _priorities[num] = 0;
        }

        bool logStarted = false;
        // write stat updates (DP6 and later; older protocols send them as reliable messages instead).
        // Each takes at most 6 bytes, and room is kept for the empty svc_entities that must follow.
        for (int i = 0; i < DpProtocol.MaxClStats && msg.Length + 6 + 11 <= maxSize; i++)
        {
            int mask = 1 << (i & 7);
            if ((_statsDeltaBits[i >> 3] & mask) == 0)
                continue;
            _statsDeltaBits[i >> 3] &= (byte)~mask;
            // add packetlog entry now that we have something for it
            if (!logStarted)
            {
                BeginPacketLog(packetLogNumber, frameNum);
                logStarted = true;
            }
            _logStatsDeltaBits[packetLogNumber * StatsBytes + (i >> 3)] |= (byte)mask;
            if (_stats[i] >= 0 && _stats[i] < 256)
            {
                msg.WriteByte((int)Svc.UpdateStatUByte);
                msg.WriteByte(i);
                msg.WriteByte(_stats[i]);
            }
            else
            {
                msg.WriteByte((int)Svc.UpdateStat);
                msg.WriteByte(i);
                msg.WriteLong(_stats[i]);
            }
            anything = true;
        }

        // only send empty svc_entities frame if needed
        if (!anything && !needEmpty)
            return false;

        // add packetlog entry now that we have something for it
        if (!logStarted)
            BeginPacketLog(packetLogNumber, frameNum);
        ushort[] logNumbers = _logNumbers[packetLogNumber] ??= new ushort[MaxStates];
        uint[] logBits = _logBits[packetLogNumber] ??= new uint[MaxStates];
        int numStates = 0;

        // write state updates
        _latestFrameNum = frameNum;
        msg.WriteByte((int)Svc.Entities);
        msg.WriteLong(frameNum);
        msg.WriteLong(unchecked((int)moveSequence)); // DP7 and later
        for (int priority = PriorityLevels - 1; priority >= 0 && numStates < MaxStates; priority--)
        {
            for (int i = 0; i < _priorityChainCounts[priority] && numStates < MaxStates; i++)
            {
                num = _priorityChains[priority * MaxStates + i];
                ref readonly SvEntityState n = ref _states[num];
                // A full update resets the client's copy to the default state first, so what must be
                // sent with it is everything that differs from the default, not from what was there.
                if ((_deltaBits[num] & DpProtocol.E5FullUpdate) != 0)
                    _deltaBits[num] = DpProtocol.E5FullUpdate | DeltaBits(DefaultState, n);
                _buf.Clear();
                WriteUpdate(num, n, _deltaBits[num], _buf, _host);
                // Deviation: an update over 128 bytes (only a skeleton of more than 8 bones can be) is a
                // Host_Error in the C, which takes the whole server down. Here the entity is passed
                // over; it keeps its bits and is tried, and passed over, again.
                if (_buf.Overflowed)
                    continue;
                // if the entity won't fit, try the next one
                if (msg.Length + _buf.Length + 2 > maxSize)
                    continue;
                // write entity to the packet
                msg.WriteBytes(_buf.WrittenSpan);
                // mark age on entity for prioritization
                _updateFrameNum[num] = frameNum;
                // log entity so deltabits can be restored later if lost
                logNumbers[numStates] = (ushort)num;
                logBits[numStates] = _deltaBits[num];
                numStates++;
                // clear deltabits and priority so it won't be sent again
                _deltaBits[num] = 0;
                _priorities[num] = 0;
            }
        }
        _logNumStates[packetLogNumber] = numStates;
        msg.WriteShort(0x8000);
        return true;
    }

    /// <summary>
    /// EntityFrame5_LostFrame: the client never received frame <paramref name="frameNum"/> (it
    /// acknowledged a later one). Every logged frame up to and including it is given up: the fields and
    /// stats it carried are flagged for sending again, except those a later frame, which may still
    /// arrive, has carried since. Any value is safe to pass; sv_user.c only calls this for frame
    /// numbers up to <see cref="LatestFrameNumber"/>.
    /// </summary>
    public void LostFrame(int frameNum)
    {
        if (_lostDeltaBits.Length < _maxEdicts)
            _lostDeltaBits = new uint[_maxEdicts];
        uint[] deltaBits = _lostDeltaBits;
        Array.Clear(deltaBits, 0, _maxEdicts);
        Span<byte> statsDeltaBits = _lostStatsDeltaBits;
        statsDeltaBits.Clear();

        // The C qsorts the logs by frame number first, so that every lost frame is counted before the
        // later frames subtract what they re-sent. But its qsort call has the element count and size
        // swapped and its comparator reads the pointer array as if it were a log, so the "sort" compares
        // slot addresses and leaves the slots in order. What a stock server does is therefore walk the
        // slots as they lie, and so does this. The difference is only ever towards re-sending more: a
        // later frame visited before a lost one has nothing to subtract from yet.
        for (int i = 0; i < MaxPacketLogs; i++)
        {
            if (_logPacketNumber[i] == 0)
                continue;
            int numStates = _logNumStates[i];
            ushort[]? numbers = _logNumbers[i];
            uint[]? bits = _logBits[i];
            Span<byte> logStats = _logStatsDeltaBits.AsSpan(i * StatsBytes, StatsBytes);
            if (_logPacketNumber[i] <= frameNum)
            {
                if (numbers is not null && bits is not null)
                    for (int j = 0; j < numStates; j++)
                        deltaBits[numbers[j]] |= bits[j];
                for (int l = 0; l < StatsBytes; l++)
                    statsDeltaBits[l] |= logStats[l];
                _logPacketNumber[i] = 0;
            }
            else
            {
                if (numbers is not null && bits is not null)
                    for (int j = 0; j < numStates; j++)
                        deltaBits[numbers[j]] &= ~bits[j];
                for (int l = 0; l < StatsBytes; l++)
                    statsDeltaBits[l] &= (byte)~logStats[l];
            }
        }

        for (int i = 0; i < _maxEdicts; i++)
        {
            uint bits = deltaBits[i] & ~_deltaBits[i];
            if (bits == 0)
                continue;
            _deltaBits[i] |= bits;
            // if it was a very important update, set priority higher
            if ((bits & ImportantBits) != 0)
                _priorities[i] = Math.Max(_priorities[i], (byte)4);
            else
                _priorities[i] = Math.Max(_priorities[i], (byte)1);
        }

        // no need to mask out the already-set bits here, as we do not
        // do that priorities stuff
        for (int l = 0; l < StatsBytes; l++)
            _statsDeltaBits[l] |= statsDeltaBits[l];
    }

    /// <summary>
    /// EntityFrame5_AckFrame: the client has frame <paramref name="frameNum"/>, so that frame's log and
    /// every older one are finished with. (The older ones were either acknowledged already or have just
    /// been handed to <see cref="LostFrame"/>.) A client that acknowledges frames it was never sent only
    /// discards its own re-sends.
    /// </summary>
    public void AckFrame(int frameNum)
    {
        // scan for packets made obsolete by this ack and delete them
        for (int i = 0; i < MaxPacketLogs; i++)
            if (_logPacketNumber[i] <= frameNum)
                _logPacketNumber[i] = 0;
    }
}
