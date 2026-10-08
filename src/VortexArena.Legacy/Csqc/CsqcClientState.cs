// Port of the parts of Base/darkplaces/client.h client_state_t that the client program can observe
// through builtins and engine globals, with the cl_parse.c code that maintains them (CL_ParseServerInfo,
// CL_ParseClientdata, CL_NetworkTimeReceived, the svc_setview / svc_updatename / svc_updatefrags /
// svc_updatecolors / svc_updatestat cases) and cl_input.c CL_RotateMoves.
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>One input command as the program sees it through getinputstate (usercmd_t).</summary>
public struct CsqcUserCommand
{
    /// <summary>The move's sequence number: what getinputstate is asked for.</summary>
    public uint Sequence;
    public QcVector ViewAngles;
    public float ForwardMove, SideMove, UpMove;
    public int Buttons;
    /// <summary>Seconds this command covers (input_timelength).</summary>
    public float FrameTime;
    public bool Crouch;
}

/// <summary>A scoreboard row (scoreboard_t): what getplayerkeyvalue reads.</summary>
public struct CsqcPlayerInfo
{
    public string Name;
    public int Frags, Colors;
    public int Ping, PacketLoss, MovementLoss;
    public float EnterTime;
}

/// <summary>
/// The engine-side client state a client program reads: stats, the scoreboard, the server's precache
/// lists, the clock, the view entity and the recent input commands. It outlives any one program -
/// the server describes the level before the program can be started - so it is a separate object
/// that <see cref="CsqcMessageHandler"/> keeps current and <see cref="CsqcHost"/> reads.
///
/// Every table is fixed-size and every index is checked here, because the indices come from the
/// server or from the program.
/// </summary>
public sealed class CsqcClientState
{
    /// <summary>CL_MAX_USERCMDS: how many input commands are remembered.</summary>
    public const int MaxUserCommands = 128;

    // qstats.h
    public const int StatItems = 15, StatViewHeight = 16, StatViewZoom = 21;

    /// <summary>cl.stats: raw 32-bit values; getstatf reinterprets the bits as a float.</summary>
    public int[] Stats { get; } = new int[DpProtocol.MaxClStats];

    /// <summary>cl.scores, <see cref="MaxClients"/> rows in use.</summary>
    public CsqcPlayerInfo[] Scores { get; } = new CsqcPlayerInfo[DpProtocol.MaxScoreboard];
    public int MaxClients { get; private set; }
    /// <summary>GAME_COOP (0) or GAME_DEATHMATCH (1).</summary>
    public int GameType { get; private set; }

    /// <summary>cl.model_name: the server's model list. Index 0 is unused, 1 is the map.</summary>
    public string?[] ModelNames { get; } = new string?[DpProtocol.MaxModels];
    /// <summary>cl.sound_name.</summary>
    public string?[] SoundNames { get; } = new string?[DpProtocol.MaxSounds];
    /// <summary>cl.csqc_model_precache: models the program precached itself. Entry i is model index -(i+1).</summary>
    public List<string> CsqcModels { get; } = new();

    /// <summary>cl.worldmessage: the map's title.</summary>
    public string WorldMessage { get; private set; } = "";
    /// <summary>cl.worldname: "maps/x.bsp", or empty before a level is known.</summary>
    public string WorldModel { get; private set; } = "";
    /// <summary>Set for a level whose server announced package downloads ahead of svc_serverinfo: the
    /// presentation leaves the world and the precache alone in BeginLevel and loads them in
    /// LevelFilesArrived, when the packages are mounted.</summary>
    public bool LevelLoadDeferred { get; set; }
    /// <summary>cl.worldnamenoextension: "maps/x".</summary>
    public string WorldNameNoExtension { get; private set; } = "";
    /// <summary>cl.worldbasename: "x". The program's mapname global.</summary>
    public string WorldBaseName { get; private set; } = "";

    /// <summary>cl.viewentity: the server entity the view is attached to.</summary>
    public int ViewEntity { get; private set; }
    /// <summary>cl.realplayerentity: the first view entity the server named.</summary>
    public int RealPlayerEntity { get; private set; }
    /// <summary>cl.playerentity: the last view entity that was a player slot.</summary>
    public int PlayerEntity { get; private set; }

    /// <summary>cl.time: the client's clock, in server time.</summary>
    public double Time { get; set; }
    /// <summary>cl.mtime[0] and [1]: the timestamps of the last two server messages.</summary>
    public double ServerTime { get; private set; }
    public double ServerPrevTime { get; private set; }
    /// <summary>cls.signon.</summary>
    public int Signon { get; set; }
    /// <summary>cls.demoplayback.</summary>
    public bool IsDemo { get; set; }
    /// <summary>sv.active: this process is also the server.</summary>
    public bool IsServer { get; set; }
    /// <summary>cl.islocalgame: a single-player game, the only case setpause acts in.</summary>
    public bool IsLocalGame { get; set; }
    public bool Paused { get; set; }
    public int Intermission { get; set; }

    // svc_clientdata
    public bool OnGround { get; private set; }
    public bool InWater { get; private set; }
    public QcVector PunchAngle { get; private set; }
    public QcVector PunchVector { get; private set; }
    /// <summary>cl.movement_velocity. Without engine-side prediction this is the velocity the server sent.</summary>
    public QcVector Velocity { get; set; }

    /// <summary>cl.viewangles.</summary>
    public QcVector ViewAngles { get; set; }
    /// <summary>The view entity's origin as the engine has it (the origin of its render matrix).</summary>
    public QcVector ViewEntityOrigin { get; set; }

    /// <summary>cls.servermovesequence: the last input command the server acknowledged.</summary>
    public uint ServerMoveSequence { get; set; }
    /// <summary>cl.movecmd: newest first, as DarkPlaces keeps it.</summary>
    public CsqcUserCommand[] MoveCommands { get; } = new CsqcUserCommand[MaxUserCommands];

    // cl.playerstandmins and friends (CL_ClearState).
    public QcVector PlayerStandMins { get; set; } = new(-16, -16, -24);
    public QcVector PlayerStandMaxs { get; set; } = new(16, 16, 24);
    public QcVector PlayerCrouchMins { get; set; } = new(-16, -16, -24);
    public QcVector PlayerCrouchMaxs { get; set; } = new(16, 16, 24);

    /// <summary>cl.qw_serverinfo: an info string serverkey reads. A DP7 server never fills it.</summary>
    public string ServerInfoString { get; set; } = "";

    // Mouse state behind setcursormode / getmousepos / setsensitivityscale.
    public bool WantsMouseMove { get; set; }
    public float SensitivityScale { get; set; } = 1;
    /// <summary>The pointer in virtual-screen pixels, as getmousepos reports it.</summary>
    public QcVector MousePosition { get; set; }
    /// <summary>key_dest == key_game and the console is closed: getmousepos answers zero otherwise.</summary>
    public bool GameHasKeyFocus { get; set; } = true;

    /// <summary>cl.movevars_gravity, for tracetoss.</summary>
    public float Gravity { get; set; } = 800;

    /// <summary>cl.entities, as far as getentity (#504) needs it. Null until something supplies one.</summary>
    public DpEntityTable? NetworkEntities { get; set; }

    /// <summary>
    /// CL_ParseServerInfo: a level starts. Clears what CL_ClearState clears (stats, scoreboard, the
    /// precache lists, the view entity) and records the new lists.
    /// </summary>
    public void ApplyServerInfo(DpServerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Array.Clear(Stats);
        Array.Clear(Scores);
        Array.Clear(ModelNames);
        Array.Clear(SoundNames);
        Array.Clear(MoveCommands);
        CsqcModels.Clear();
        // CL_ClearState: "set up the float version of the stats array for easier access to float stats"
        // has no counterpart here; viewzoom starts at 255 (no zoom) and the items at zero.
        Stats[StatViewZoom] = 255;
        ViewEntity = RealPlayerEntity = PlayerEntity = 0;
        Intermission = 0;
        OnGround = InWater = false;
        PunchAngle = PunchVector = Velocity = default;
        ServerTime = ServerPrevTime = Time = 0;
        Signon = 0;

        MaxClients = Math.Clamp(info.MaxClients, 0, DpProtocol.MaxScoreboard);
        GameType = info.GameType;
        WorldMessage = info.WorldMessage;
        for (int i = 1; i < info.Models.Count && i < ModelNames.Length; i++) ModelNames[i] = info.Models[i];
        for (int i = 1; i < info.Sounds.Count && i < SoundNames.Length; i++) SoundNames[i] = info.Sounds[i];

        // "set the base name for level-specific things": strip the extension, then the directory.
        WorldModel = info.WorldModel;
        int dot = WorldModel.LastIndexOf('.');
        int slash = WorldModel.LastIndexOf('/');
        WorldNameNoExtension = dot > slash ? WorldModel[..dot] : WorldModel;
        WorldBaseName = WorldNameNoExtension[(WorldNameNoExtension.LastIndexOf('/') + 1)..];
    }

    /// <summary>svc_precache: a model or sound the server added after the level started.</summary>
    public void ApplyPrecache(int index, bool isSound, string name)
    {
        string?[] table = isSound ? SoundNames : ModelNames;
        if (index > 0 && index < table.Length) table[index] = name;
    }

    /// <summary>svc_updatestat / svc_updatestatubyte.</summary>
    public void SetStat(int index, int value)
    {
        if ((uint)index < (uint)Stats.Length) Stats[index] = value;
    }

    /// <summary>svc_setview.</summary>
    public void SetView(int entity)
    {
        if ((uint)entity >= DpProtocol.MaxEdicts) return;
        ViewEntity = entity;
        // "assume first setview recieved is the real player entity"
        if (RealPlayerEntity == 0) RealPlayerEntity = entity;
        // "update cl.playerentity to this one if it is a valid player"
        if (entity >= 1 && entity <= MaxClients) PlayerEntity = entity;
    }

    /// <summary>
    /// svc_time (CL_NetworkTimeReceived), for the two cases that need no wall clock: before the client
    /// is fully connected and during demo playback, where the client's time is the server's.
    /// A live connection interpolates <see cref="Time"/> towards these between messages; that belongs to
    /// whoever owns the frame loop, which sets <see cref="Time"/> itself.
    /// </summary>
    public void NetworkTimeReceived(double newTime)
    {
        ServerPrevTime = ServerTime;
        ServerTime = newTime;
        if (ServerPrevTime == ServerTime || Signon < DpProtocol.Signons)
            Time = ServerPrevTime = newTime;
        else if (IsDemo)
        {
            // "when time falls behind during demo playback it means the cl.mtime[1] was altered due to a
            // large time gap, so treat it as an instant change in time"
            if (Time < newTime - 0.1) ServerPrevTime = Time = newTime;
        }
    }

    /// <summary>svc_clientdata. For DP7 only the view height, items and zoom stats travel here; the
    /// rest of the stats come as svc_updatestat.</summary>
    public void ApplyClientData(in DpClientData data)
    {
        if (data.HasViewHeight) Stats[StatViewHeight] = data.ViewHeight;
        if (data.HasItems) Stats[StatItems] = data.Items;
        if (data.HasViewZoom) Stats[StatViewZoom] = data.ViewZoom;
        OnGround = data.OnGround;
        InWater = data.InWater;
        PunchAngle = new QcVector(data.PunchAngle.X, data.PunchAngle.Y, data.PunchAngle.Z);
        PunchVector = new QcVector(data.PunchVector.X, data.PunchVector.Y, data.PunchVector.Z);
        Velocity = new QcVector(data.Velocity.X, data.Velocity.Y, data.Velocity.Z);
    }

    public void SetPlayerName(int client, string name)
    {
        if ((uint)client < (uint)MaxClients) Scores[client].Name = name;
    }

    public void SetPlayerFrags(int client, int frags)
    {
        if ((uint)client < (uint)MaxClients) Scores[client].Frags = frags;
    }

    public void SetPlayerColors(int client, int colors)
    {
        if ((uint)client < (uint)MaxClients) Scores[client].Colors = colors;
    }

    /// <summary>
    /// Sbar_SortFrags + Sbar_GetSortedPlayerIndex: the scoreboard row of the player in
    /// <paramref name="rank"/>th place (0 best), or -1. Rows without a name are not ranked.
    /// </summary>
    public int SortedPlayerIndex(int rank)
    {
        if (rank < 0) return -1;
        Span<int> order = stackalloc int[DpProtocol.MaxScoreboard];
        int count = 0;
        for (int i = 0; i < MaxClients; i++)
            if (!string.IsNullOrEmpty(Scores[i].Name)) order[count++] = i;
        if (rank >= count) return -1;
        // The C is a bubble sort that swaps only when the earlier row has strictly fewer frags, so
        // equal scores keep slot order. An insertion sort with the same comparison is equivalent.
        for (int i = 1; i < count; i++)
        {
            int row = order[i];
            int j = i - 1;
            while (j >= 0 && Scores[order[j]].Frags < Scores[row].Frags) { order[j + 1] = order[j]; j--; }
            order[j + 1] = row;
        }
        return order[rank];
    }

    /// <summary>CL_GetModelByIndex: the name behind a model index (negative: the program's own
    /// precaches), or null.</summary>
    public string? ModelNameForIndex(int index)
    {
        if (index == 0) return null;
        if (index < 0)
        {
            int slot = -(index + 1);
            return slot >= 0 && slot < CsqcModels.Count ? CsqcModels[slot] : null;
        }
        return index < ModelNames.Length ? ModelNames[index] : null;
    }

    /// <summary>
    /// Records a new input command at the head of the ring (what CL_Input does with cl.movecmd), so
    /// that getinputstate can replay it.
    /// </summary>
    public void PushMoveCommand(in CsqcUserCommand command)
    {
        Array.Copy(MoveCommands, 0, MoveCommands, 1, MoveCommands.Length - 1);
        MoveCommands[0] = command;
    }

    /// <summary>
    /// CL_RotateMoves: turns the view angles of every command the server has not yet acknowledged by
    /// the rotation whose axes are <paramref name="forward"/>, <paramref name="left"/> and
    /// <paramref name="up"/> - how a warpzone keeps predicted movement continuous through a portal.
    /// </summary>
    public void RotateMoves(QcVector forward, QcVector left, QcVector up)
    {
        for (int i = 0; i < MoveCommands.Length; i++)
        {
            if (MoveCommands[i].Sequence <= ServerMoveSequence) continue;
            QcCoreBuiltins.AngleVectors(MoveCommands[i].ViewAngles, out QcVector f, out _, out QcVector u);
            QcVector newForward = Transform(f), newUp = Transform(u);
            // The shared helper is AnglesFromVectors with flippitch set (what vectoangles wants); this
            // caller passes false, so undo the flip: both forms are normalised to [0, 360).
            QcVector angles = QcCoreBuiltins.AnglesFromVectors(newForward, newUp);
            if (angles.X != 0) angles.X = 360 - angles.X;
            MoveCommands[i].ViewAngles = angles;
        }

        // Matrix4x4_Transform with a zero translation: the vector's components along the three axes.
        QcVector Transform(QcVector v) => new(
            v.X * forward.X + v.Y * left.X + v.Z * up.X,
            v.X * forward.Y + v.Y * left.Y + v.Z * up.Y,
            v.X * forward.Z + v.Y * left.Z + v.Z * up.Z);
    }
}
