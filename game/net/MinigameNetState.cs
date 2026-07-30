using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Net;

namespace VortexArena.Game.Net;

/// <summary>
/// Serializes a <see cref="MinigameSession"/> for the wire — the C# successor to the minigame entity
/// networking the libs flag as missing (QC <c>minigame_server_event(…, "network_send")</c> / the MSLE
/// read/write in sv_minigames.qc + cl_minigames.qc, the <c>minigame_board_piece</c> / <c>minigame_player</c>
/// SendEntity hooks). The server runs the rules and <see cref="Encode"/>s the board snapshot to every client
/// watching that board; the client <see cref="Decode"/>s it back into a session the <c>MinigameRenderer</c>
/// draws. Moves travel the other way as a single tile-id string (the client→server <c>minigame_cmd</c>).
///
/// Snapshot layout: the game's registry id, the turn/current-team/winner bytes, then the grid board (columns ×
/// rows + each occupied tile as id+team+flags) and the player roster (team/slot/score). Compact and
/// reconstruction-complete — everything the renderer needs without re-deriving rules on the client.
/// </summary>
public static class MinigameNetState
{
    /// <summary>Write a full board snapshot for <paramref name="session"/> (the MSLE send).</summary>
    public static void Encode(BitWriter w, MinigameSession session)
    {
        w.WriteUShort(session.Game.RegistryId);
        w.WriteByte((int)session.TurnType);
        w.WriteByte(session.CurrentTeam & 0xFF);
        w.WriteByte(session.Winner & 0xFF);

        // grid board (absent for bespoke games like Pong — they network their own state).
        MinigameBoard? board = session.Board;
        w.WriteBool(board is not null);
        if (board is not null)
        {
            w.WriteByte(board.Columns & 0xFF);
            w.WriteByte(board.Rows & 0xFF);
            w.WriteUShort(board.PieceCount);
            foreach (var kv in board.Cells)
            {
                w.WriteString(kv.Key);            // tile id ("b2")
                w.WriteByte(kv.Value.Team & 0xFF);
                w.WriteShort(kv.Value.Flags);
            }
        }

        // player roster.
        w.WriteByte(session.Players.Count & 0xFF);
        foreach (MinigamePlayer p in session.Players)
        {
            w.WriteByte(p.Team & 0xFF);
            w.WriteByte(p.Slot & 0xFF);
            w.WriteShort(p.Score);
        }

        // bespoke real-time state (Pong paddles/balls/scores). Snake is single-player → not networked.
        PongState? pong = Pong.StateOf(session);
        w.WriteByte(pong is not null ? BespokePong : BespokeNone);
        if (pong is not null)
            EncodePong(w, pong);
    }

    private const int BespokeNone = 0;
    private const int BespokePong = 1;

    private static void EncodePong(BitWriter w, PongState st)
    {
        w.WriteBool(st.Playing);
        // 4 paddle slots: presence + center/length/ai.
        for (int i = 0; i < PongState.MaxPlayers; i++)
        {
            PongPaddle? p = st.Paddles[i];
            w.WriteBool(p is not null);
            if (p is null) continue;
            w.WriteFloat(p.Origin.X);
            w.WriteFloat(p.Origin.Y);
            w.WriteFloat(p.Length);
            w.WriteBool(p.IsAi);
        }
        // balls.
        w.WriteByte(st.Balls.Count & 0xFF);
        foreach (PongBall b in st.Balls)
        {
            w.WriteFloat(b.Origin.X);
            w.WriteFloat(b.Origin.Y);
            w.WriteFloat(b.Radius);
            w.WriteByte(b.LastTouch & 0xFF);
            w.WriteBool(b.InPlay);
        }
        // scores.
        for (int i = 0; i < PongState.MaxPlayers; i++)
            w.WriteShort(st.Scores[i]);
    }

    /// <summary>
    /// Reconstruct a session from a snapshot (the MSLE receive). Returns null on a bad read or an unknown game
    /// id. The reconstructed session is render-complete (board + turn + winner + players); it is NOT a rules
    /// engine — the client only draws it.
    /// </summary>
    public static MinigameSession? Decode(ref BitReader r)
    {
        int gameId = r.ReadUShort();
        if (r.BadRead || gameId < 0 || gameId >= Minigames.Count)
            return null;
        Minigame game = Minigames.ById(gameId);

        var session = new MinigameSession(game)
        {
            TurnType = (MinigameTurn)r.ReadByte(),
            CurrentTeam = r.ReadByte(),
            Winner = r.ReadByte(),
        };

        if (r.ReadBool())
        {
            int cols = r.ReadByte();
            int rows = r.ReadByte();
            int count = r.ReadUShort();
            if (r.BadRead) return null;
            var board = new MinigameBoard(cols, rows);
            for (int i = 0; i < count; i++)
            {
                string tile = r.ReadString();
                int team = r.ReadByte();
                int flags = r.ReadShort();
                if (r.BadRead) return null;
                board.Place(tile, team, flags);
            }
            session.Board = board;
        }

        int players = r.ReadByte();
        if (r.BadRead) return null;
        for (int i = 0; i < players; i++)
        {
            int team = r.ReadByte();
            int slot = r.ReadByte();
            int score = r.ReadShort();
            if (r.BadRead) return null;
            session.Players.Add(new MinigamePlayer(team, slot) { Score = score });
        }

        int bespoke = r.ReadByte();
        if (r.BadRead) return null;
        if (bespoke == BespokePong)
        {
            PongState? pong = DecodePong(ref r);
            if (pong is null) return null;
            session.Extra[Pong.StateKey] = pong;
        }

        return r.BadRead ? null : session;
    }

    private static PongState? DecodePong(ref BitReader r)
    {
        var st = new PongState { Playing = r.ReadBool() };
        for (int i = 0; i < PongState.MaxPlayers; i++)
        {
            if (!r.ReadBool()) continue;
            float ox = r.ReadFloat();
            float oy = r.ReadFloat();
            float len = r.ReadFloat();
            bool ai = r.ReadBool();
            if (r.BadRead) return null;
            PongPaddle p = st.SpawnPaddle(i + 1, ai);
            p.Origin = new System.Numerics.Vector2(ox, oy);
            p.Length = len;
        }
        int balls = r.ReadByte();
        if (r.BadRead) return null;
        for (int i = 0; i < balls; i++)
        {
            var b = new PongBall
            {
                Origin = new System.Numerics.Vector2(r.ReadFloat(), r.ReadFloat()),
                Radius = r.ReadFloat(),
                LastTouch = r.ReadByte(),
                InPlay = r.ReadBool(),
            };
            if (r.BadRead) return null;
            st.Balls.Add(b);
        }
        for (int i = 0; i < PongState.MaxPlayers; i++)
            st.Scores[i] = r.ReadShort();
        return r.BadRead ? null : st;
    }

    /// <summary>Encode a client's move (the tile id) for the client→server <c>minigame_cmd</c>.</summary>
    public static void EncodeMove(BitWriter w, string tile) => w.WriteString(tile);

    /// <summary>Decode a client move tile id (server side).</summary>
    public static string DecodeMove(ref BitReader r) => r.ReadString();

    // =====================================================================================
    //  S2C envelope (the per-peer minigame snapshot push)
    // =====================================================================================
    //
    // QC networks each minigame ENTITY individually and CSQC activates the local player_pointer on arrival
    // (cl_minigames.qc: the slot whose team-index == player_localnum+1 → activate_minigame). The port pushes a
    // WHOLE-SESSION snapshot to the participating peer instead, so the envelope must carry what the per-entity
    // model gave the client for free: the session's unique netname (so the menu can list/track it) and THIS
    // peer's own team in the session (so the renderer gates moves on the local turn). A LEAVE/end is signalled
    // by an empty netname (the client then hides the board), mirroring activate/deactivate_minigame.

    /// <summary>
    /// Write a per-peer session snapshot envelope (the S2C <see cref="VortexArena.Game.Net.NetControl.MinigameState"/>
    /// body): the session netname, this peer's team in it, then the full session via <see cref="Encode"/>. Pass a
    /// null <paramref name="session"/> to signal "you are no longer in a minigame" (an empty netname → the client
    /// hides the board). <paramref name="localTeam"/> is the receiving peer's team
    /// (<see cref="MinigameSession.SpectatorTeam"/> for a watcher, 0 if not applicable).
    /// </summary>
    public static void EncodeEnvelope(BitWriter w, MinigameSession? session, int localTeam)
    {
        if (session is null)
        {
            w.WriteString("");   // empty netname = "no active minigame"
            return;
        }
        w.WriteString(session.NetName);
        w.WriteByte(localTeam & 0xFF);
        Encode(w, session);
    }

    /// <summary>The decoded S2C envelope: the session (null = "you left / no active minigame"), the session's
    /// unique netname, and the local peer's team within it.</summary>
    public readonly struct Envelope
    {
        public readonly MinigameSession? Session;
        public readonly string NetName;
        public readonly int LocalTeam;
        public Envelope(MinigameSession? session, string netName, int localTeam)
        { Session = session; NetName = netName; LocalTeam = localTeam; }
    }

    /// <summary>
    /// Read a per-peer session snapshot envelope (the inverse of <see cref="EncodeEnvelope"/>). An empty netname
    /// yields <see cref="Envelope.Session"/> == null (the client hides the board). A bad read yields a null
    /// session too (the client treats it as "no change worth applying" — it never throws).
    /// </summary>
    public static Envelope DecodeEnvelope(ref BitReader r)
    {
        string netName = r.ReadString();
        if (r.BadRead || string.IsNullOrEmpty(netName))
            return new Envelope(null, "", 0);
        int localTeam = r.ReadByte();
        MinigameSession? session = Decode(ref r);
        if (session is not null)
            session.NetName = netName;
        return new Envelope(session, netName, localTeam);
    }
}
