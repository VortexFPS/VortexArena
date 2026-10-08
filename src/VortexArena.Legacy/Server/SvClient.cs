// Port of Base/darkplaces/server.h client_t (the per-slot state) and usercmd_t as the server keeps it.
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

/// <summary>usercmd_t: one input command as the server applies it (sv_user.c SV_ReadClientMove).</summary>
public struct SvUserCmd
{
    public QcVector ViewAngles, CursorScreen, CursorStart, CursorEnd, CursorImpact, CursorNormal;
    public float ForwardMove, SideMove, UpMove;
    public float Time, ReceiveTime, FrameTime;
    public int Buttons, Impulse, CursorEntity;
    public uint Sequence;
    public bool ApplyMove, CanJump, Jump, Crouch;
}

/// <summary>
/// One player slot (client_t). Slot <c>i</c> is entity <c>i + 1</c> for as long as the server runs:
/// the slots, not the level's program, own a player's name, colours and spawn parameters, which is
/// how they survive a level change.
/// </summary>
public sealed class SvClient
{
    public const int NumSpawnParms = 16;   // NUM_SPAWN_PARMS
    public const int MaxNameLength = 64;   // sizeof(client->name) is MAX_SCOREBOARDNAME (128); DarkPlaces' name command cuts at 64

    public SvClient(int index) => Index = index;

    /// <summary>Slot number, 0-based. The player's entity is <see cref="Edict"/>.</summary>
    public int Index { get; }
    public int Edict => Index + 1;

    /// <summary>false = empty slot.</summary>
    public bool Active;
    /// <summary>ClientConnect has been called for this player on this level, so ClientDisconnect is owed.</summary>
    public bool ClientConnectCalled;
    /// <summary>Signon progress: "prespawn" received; "spawn" received; "begin" received (in the game).</summary>
    public bool PreSpawned, Spawned, Begun;
    /// <summary>1 = send svc_serverinfo's message and move to 2; 2 = the signon buffer is being drained.</summary>
    public int SendSignon;

    /// <summary>The network connection, or null for a bot (spawnclient) - what clienttype tells apart.</summary>
    public SvConnection? Connection;

    public string Name = "", OldName = "";
    public int Colors, OldColors;
    public int Frags, OldFrags;
    public string PlayerModel = "", OldModel = "", PlayerSkin = "", OldSkin = "";
    public string NetAddress = "";
    public readonly float[] SpawnParms = new float[NumSpawnParms];

    /// <summary>prediction: the input command being applied, and the sequence the client is told was processed.</summary>
    public SvUserCmd Cmd;
    public uint MoveSequence;
    public uint MovementHighestSequenceSeen;
    public readonly int[] MovementCount = new int[64];   // NETGRAPH_PACKETS
    public float MoveFrameTime;
    /// <summary>sv_clmovement_inputtimeout bookkeeping: while positive, the client's own input packets
    /// drive its physics and the server frame does not move it.</summary>
    public float ClMovementInputTimeout;
    public bool ClMovementDisableTimeout;

    /// <summary>fixangle data: set by the frame, sent by SV_WriteClientdataToMessage.</summary>
    public bool FixAngleAnglesSet;
    public QcVector FixAngleAngles;

    public double ConnectTime;
    public float Ping;
    public int Rate = 1_000_000_000, RateBurst;
    /// <summary>Stats as last computed, and which of them the client has not been sent (statsdeltabits).</summary>
    public readonly int[] Stats = new int[DpProtocol.MaxClStats];
    public readonly byte[] StatsDeltaBits = new byte[(DpProtocol.MaxClStats + 7) / 8];
    public int ClientCamera;
    public string WeaponModel = "";
    public int WeaponModelIndex;

    /// <summary>Engine strings the slot's entity fields point at (netname, playermodel, playerskin,
    /// netaddress, clientstatus): one zoned handle each, whose text is replaced in place, so a client
    /// renaming itself a million times costs one string.</summary>
    internal int NetNameHandle, PlayerModelHandle, PlayerSkinHandle, NetAddressHandle;

    /// <summary>SV_ConnectClient's memset: everything back to an empty slot. Spawn parameters are
    /// cleared too (they are kept only across a saved-game load, which is not ported).</summary>
    public void Clear()
    {
        Active = ClientConnectCalled = PreSpawned = Spawned = Begun = false;
        SendSignon = 0;
        Connection = null;
        Name = OldName = PlayerModel = OldModel = PlayerSkin = OldSkin = NetAddress = WeaponModel = "";
        Colors = OldColors = Frags = OldFrags = WeaponModelIndex = ClientCamera = 0;
        Array.Clear(SpawnParms);
        Cmd = default;
        MoveSequence = MovementHighestSequenceSeen = 0;
        Array.Clear(MovementCount);
        MoveFrameTime = ClMovementInputTimeout = 0;
        ClMovementDisableTimeout = FixAngleAnglesSet = false;
        FixAngleAngles = default;
        ConnectTime = 0;
        Ping = 0;
        Rate = 1_000_000_000;
        RateBurst = 0;
        Array.Clear(Stats);
        Array.Clear(StatsDeltaBits);
    }
}

/// <summary>
/// What a connected (non-bot) client has beyond its slot: the reliable message being built for it
/// (netconnection->message) and the unreliable messages queued for its next datagram
/// (client->unreliablemsg). The transport half - channel, address, timeouts - is added by the
/// network layer (<c>SvServer</c>), which subclasses this.
/// </summary>
public class SvConnection
{
    /// <param name="message">The reliable message buffer to write to (a network channel's), or null for one of this object's own.</param>
    public SvConnection(DpMessageWriter? message = null) => Message = message ?? new DpMessageWriter(DpProtocol.NetMaxMessage);

    /// <summary>netconnection->message, with allowoverflow: the server catches the overflow and drops the client.</summary>
    public DpMessageWriter Message { get; }
    /// <summary>client->unreliablemsg and its split points (message boundaries, so a datagram is
    /// never cut in the middle of one).</summary>
    public DpMessageWriter UnreliableMsg { get; } = new(DpProtocol.NetMaxMessage);
    public List<int> UnreliableSplitPoints { get; } = new();
    /// <summary>sizeof(client->unreliablemsg_splitpoint) / sizeof(int).</summary>
    public const int MaxSplitPoints = DpProtocol.NetMaxMessage / 16;
}
