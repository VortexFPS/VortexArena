// Port of Base/darkplaces/cl_input.c CL_SendMove (the PROTOCOL_DARKPLACES7 case, lines 2074-2156),
// cl_parse.c CL_SendPlayerInfo / CL_ForwardToServer (clc_stringcmd) and cl_main.c CL_DisconnectEx
// (clc_disconnect).
using System.Numerics;

namespace VortexArena.Legacy.Protocol;

/// <summary>One frame of player input as DP7 sends it (the networked part of usercmd_t).</summary>
public struct DpUserCmd
{
    /// <summary>The netchan's outgoing unreliable sequence when the command was built. The server
    /// echoes the newest one it has applied in every entity frame.</summary>
    public uint Sequence;
    /// <summary>Whether the client predicts its own movement. When false the sequence is sent as 0,
    /// which tells the server not to expect prediction for this command.</summary>
    public bool Predicted;
    /// <summary>The client's game time (cl.time): the time of the last server update it has shown.</summary>
    public float Time;
    public Vector3 ViewAngles;
    public float ForwardMove;
    public float SideMove;
    public float UpMove;
    /// <summary>Button bits: 1 attack, 2 jump, 4 attack2, 8 zoom, 16 crouch, ... (cl_input.c).</summary>
    public int Buttons;
    public byte Impulse;
    /// <summary>PRYDON_CLIENTCURSOR: cursor position in -1..1 screen space.</summary>
    public Vector2 CursorScreen;
    public Vector3 CursorStart;
    public Vector3 CursorImpact;
    public ushort CursorEntity;
}

/// <summary>A received svc_downloaddata block to acknowledge (cl_downloadack_t).</summary>
public readonly record struct DpDownloadAck(int Start, int Size);

/// <summary>Writers for every client-to-server message of DP7.</summary>
public static class DpClientMessages
{
    /// <summary>Bytes one clc_move occupies in DP7.</summary>
    public const int MoveSize = 56;

    public static void WriteNop(DpMessageWriter w) => w.WriteByte((int)Clc.Nop);

    /// <summary>clc_disconnect. DP7 sends no reason (DP8 appends a string).</summary>
    public static void WriteDisconnect(DpMessageWriter w) => w.WriteByte((int)Clc.Disconnect);

    /// <summary>clc_stringcmd: a console command for the server ("prespawn", "say hi", ...). Goes on
    /// the reliable stream.</summary>
    public static void WriteStringCommand(DpMessageWriter w, string command)
    {
        w.WriteByte((int)Clc.StringCmd);
        w.WriteString(command);
    }

    /// <summary>clc_move, 56 bytes.</summary>
    public static void WriteMove(DpMessageWriter w, in DpUserCmd cmd)
    {
        w.WriteByte((int)Clc.Move);
        w.WriteLong(cmd.Predicted ? (int)cmd.Sequence : 0);
        w.WriteFloat(cmd.Time); // last server packet time
        w.WriteAngle16i(cmd.ViewAngles.X);
        w.WriteAngle16i(cmd.ViewAngles.Y);
        w.WriteAngle16i(cmd.ViewAngles.Z);
        w.WriteCoord16i(cmd.ForwardMove);
        w.WriteCoord16i(cmd.SideMove);
        w.WriteCoord16i(cmd.UpMove);
        w.WriteLong(cmd.Buttons);
        w.WriteByte(cmd.Impulse);
        // The C casts float to short, which truncates toward zero. An out-of-range value is undefined
        // there; here it saturates.
        w.WriteShort((short)Math.Clamp(cmd.CursorScreen.X * 32767.0f, short.MinValue, short.MaxValue));
        w.WriteShort((short)Math.Clamp(cmd.CursorScreen.Y * 32767.0f, short.MinValue, short.MaxValue));
        w.WriteVector(cmd.CursorStart);
        w.WriteVector(cmd.CursorImpact);
        w.WriteShort(cmd.CursorEntity);
    }

    /// <summary>clc_ackframe: "I have applied entity frame <paramref name="frameNumber"/>".</summary>
    public static void WriteAckFrame(DpMessageWriter w, int frameNumber)
    {
        w.WriteByte((int)Clc.AckFrame);
        w.WriteLong(frameNumber);
    }

    /// <summary>clc_ackdownloaddata: an exact echo of the start and size of a received block. The
    /// server does the loss handling: an echo that is not the block it expects makes it seek back.</summary>
    public static void WriteAckDownloadData(DpMessageWriter w, int start, int size)
    {
        w.WriteByte((int)Clc.AckDownloadData);
        w.WriteLong(start);
        w.WriteShort(size);
    }

    /// <summary>
    /// The unreliable part of one input packet, in the order CL_SendMove writes it: the recent moves
    /// oldest first, then entity frame acks, then download acks.
    ///
    /// Moves are repeated on purpose: the server ignores the ones it already has, and uses them when
    /// the packet that first carried them was lost. A repeat whose sequence is already behind what
    /// the server reported (<paramref name="serverMoveSequence"/>) is left out.
    /// </summary>
    /// <param name="moves">Moves to send, oldest first (at most cl_netrepeatinput + 1 of them, 3 at most in DarkPlaces).</param>
    public static void WriteInputPacket(DpMessageWriter w, ReadOnlySpan<DpUserCmd> moves, uint serverMoveSequence,
        ReadOnlySpan<int> ackFrames, ReadOnlySpan<DpDownloadAck> downloadAcks)
    {
        foreach (ref readonly DpUserCmd cmd in moves)
        {
            // don't repeat any stale moves
            if (cmd.Sequence != 0 && cmd.Sequence < serverMoveSequence)
                continue;
            WriteMove(w, cmd);
        }
        foreach (int frame in ackFrames)
            WriteAckFrame(w, frame);
        foreach (DpDownloadAck ack in downloadAcks)
            WriteAckDownloadData(w, ack.Start, ack.Size);
    }
}
