using XonoticGodot.Common.Math;

namespace XonoticGodot.Common.Gameplay;

// Server-side rules for Tic Tac Toe — port of common/minigames/minigame/ttt.qc (ttt_server_event,
// ttt_move, ttt_winning_piece, ttt_valid_tile) AND the singleplayer AI (ttt_ai_choose_simple /
// ttt_ai_block3 / ttt_ai_1of3 / ttt_ai_random / ttt_aimove). 3x3 board, two teams alternate placing a
// piece; first to complete a row/column/diagonal wins; a full board with no line is a draw.
//
// Tile ids follow MinigameTiles: column 'a'..'c' (0..2) x row 1..3 (0..2 internally), e.g. "b2" is centre.
// Omitted vs QC: CSQC board/status drawing and entity networking (SendFlags) only.

public sealed class TicTacToe : Minigame
{
    public const int Size = 3;       // QC TTT_LET_CNT == TTT_NUM_CNT == 3

    public override string NetName => "ttt";
    public override string DisplayName => "Tic Tac Toe";
    public override int MaxPlayers => 2;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this) { Board = new MinigameBoard(Size, Size) };
        Start(s);
        return s;
    }

    // QC "start": minigame_flags = TTT_TURN_PLACE | TTT_TURN_TEAM1
    public override int Start(MinigameSession session)
    {
        session.Board ??= new MinigameBoard(Size, Size);
        session.Board.Clear();
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        // session.State doubles as ttt_nexteam (the team that starts the next match)
        session.State = 1;
        return 1;
    }

    // QC "join": single-player-vs-AI guard omitted; assign team 1 then 2, else spectator.
    public override int Join(MinigameSession session, MinigamePlayer player)
    {
        if (session.PlayerCount >= MaxPlayers) return MinigameSession.SpectatorTeam;
        if (session.Players.Count > 0)
            return MinigameSession.NextTeam(session.Players[^1].Team, MaxPlayers);
        return 1;
    }

    // QC ttt_server_event "cmd" switch (ttt.qc:175): dispatch on the first token — "move <tile>" places (the
    // client board click sends this), "next" starts a fresh match, "singleplayer" seats the AI. A bare tile id
    // (no leading verb) is also accepted as a placement for direct/test callers.
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        if (string.IsNullOrEmpty(move)) return MoveResult.Invalid;
        // QC: spectators can't act (player.team == TTT_SPECTATOR_TEAM).
        if (player.Team == MinigameSession.SpectatorTeam) return MoveResult.Invalid;

        var parts = move.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0] : "";

        // QC "next": ttt_next_match (only meaningful once the game is over).
        if (verb == "next")
        {
            if (!session.IsOver) return MoveResult.Invalid;
            NextMatch(session);
            return MoveResult.Accepted;
        }
        // QC "singleplayer": seat the AI when exactly one human has joined.
        if (verb == "singleplayer")
        {
            if (AiTeam(session) != 0 || session.PlayerCount != 1) return MoveResult.Invalid;
            EnableSinglePlayer(session);
            return MoveResult.Accepted;
        }
        // QC "move <tile>": placement. Accept "move b2" (the cmd form) or a bare "b2" (direct callers).
        string tile = verb == "move" ? (parts.Length >= 2 ? parts[1] : "") : move;
        return Place(session, player, tile);
    }

    // QC ttt_move: must be the placing phase, the mover's turn, a valid empty tile.
    private MoveResult Place(MinigameSession session, MinigamePlayer player, string move)
    {
        var board = session.Board;
        if (board is null) return MoveResult.Invalid;
        if (session.TurnType != MinigameTurn.Play) return MoveResult.Invalid;
        if (player.Team != session.CurrentTeam) return MoveResult.Invalid;
        if (!ValidTile(move) || !board.IsEmpty(move)) return MoveResult.Invalid;

        board.Place(move, player.Team);
        session.State = MinigameSession.NextTeam(player.Team, MaxPlayers); // ttt_nexteam

        if (IsWinningPiece(board, move, player.Team))
        {
            player.Score++; // won-match counter (QC ++player.minigame_flags)
            session.TurnType = MinigameTurn.Win;
            session.Winner = player.Team;
            session.CurrentTeam = player.Team;
            return MoveResult.GameOver;
        }

        if (board.PieceCount >= Size * Size)
        {
            session.TurnType = MinigameTurn.Draw;
            session.CurrentTeam = 0;
            return MoveResult.GameOver;
        }

        session.CurrentTeam = session.State; // hand turn to the next team

        // QC ttt_make_move (single-player): after the HUMAN places, the AI moves immediately (QC runs it
        // client-side; the port keeps one server-side rules path — recon's chosen approach). Guard on the mover
        // being human (player.IsAi false) so the AI's own placement below doesn't recurse.
        if (!player.IsAi && AiTeam(session) == session.CurrentTeam)
            AiMove(session);

        return MoveResult.Accepted;
    }

    // QC ttt_next_match (single-player branch): reset board, swap starting team.
    /// <summary>Start a fresh match after a win/draw (QC "next"). Resets the board; next match's first team
    /// is the loser/other side (QC ttt_nexteam).</summary>
    public void NextMatch(MinigameSession session)
    {
        if (!session.IsOver) return;
        session.Board?.Clear();
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = session.State == 0 ? 1 : session.State;
        session.Winner = 0;
    }

    public override MinigameTurn CheckEnd(MinigameSession session) => session.TurnType;

    /// <summary>QC ttt_valid_tile: within the 3x3 grid.</summary>
    public static bool ValidTile(string? tile)
    {
        if (string.IsNullOrEmpty(tile)) return false;
        int number = MinigameTiles.Number(tile);
        int letter = MinigameTiles.Letter(tile);
        return number >= 0 && number < Size && letter >= 0 && letter < Size;
    }

    // QC ttt_winning_piece: the just-placed piece completes a line if its whole column, whole row, or
    // (when on a diagonal) that diagonal is all the same team.
    private static bool IsWinningPiece(MinigameBoard b, string tile, int team)
    {
        int number = MinigameTiles.Number(tile);
        int letter = MinigameTiles.Letter(tile);

        // full column (fixed letter, all numbers)
        if (b.TeamAt(letter, 0) == team && b.TeamAt(letter, 1) == team && b.TeamAt(letter, 2) == team)
            return true;

        // full row (fixed number, all letters)
        if (b.TeamAt(0, number) == team && b.TeamAt(1, number) == team && b.TeamAt(2, number) == team)
            return true;

        // main diagonal (a1-b2-c3): cells where letter == number
        if (number == letter &&
            b.TeamAt(0, 0) == team && b.TeamAt(1, 1) == team && b.TeamAt(2, 2) == team)
            return true;

        // anti-diagonal (a3-b2-c1): cells where number == 2 - letter
        if (number == 2 - letter &&
            b.TeamAt(0, 2) == team && b.TeamAt(1, 1) == team && b.TeamAt(2, 0) == team)
            return true;

        return false;
    }

    // =====================================================================================
    //  Single-player AI opponent — port of ttt.qc (CSQC) ttt_ai_* + ttt_aimove.
    // =====================================================================================
    //
    // Board piece bitmask layout (exactly QC's ttt_aimove): iterate column i=0..2 (outer), row j=0..2
    // (inner), with the flag doubling each cell, so bit index = i*3 + j:
    //   .---.---.---.
    //   | 4 | 32|256|  row 3 (j=2)
    //   | 2 | 16|128|  row 2 (j=1)
    //   | 1 | 8 | 64|  row 1 (j=0)
    //   '---'---'---'
    //     A   B   C
    // ttt_ai_block3 scans the 8 lines for "two of mine + one free" (the third flag), ttt_ai_random picks a
    // random set bit. ttt_ai_choose_simple: win if possible, else block the opponent, else random.

    /// <summary>Session.Extra key holding the AI's team number (QC minigame.ttt_ai; 0 = two-human game).</summary>
    public const string AiKey = "ttt_ai";

    private const int A1 = 0x001, A2 = 0x002, A3 = 0x004;
    private const int B1 = 0x008, B2 = 0x010, B3 = 0x020;
    private const int C1 = 0x040, C2 = 0x080, C3 = 0x100;

    // QC ttt_ai_piece_flag2pos: single-bit flag -> tile id.
    private static string? FlagToPos(int flag) => flag switch
    {
        A1 => "a1", A2 => "a2", A3 => "a3",
        B1 => "b1", B2 => "b2", B3 => "b3",
        C1 => "c1", C2 => "c2", C3 => "c3",
        _ => null,
    };

    private static bool CheckMask(int piecemask, int checkflags)
        => checkflags != 0 && (piecemask & checkflags) == checkflags;

    // QC ttt_ai_1of3: if the mask has exactly two of the three line flags, return the missing third.
    private static int OneOf3(int piecemask, int flag1, int flag2, int flag3)
    {
        if (CheckMask(piecemask, flag1 | flag2 | flag3)) return 0; // already all three
        if (CheckMask(piecemask, flag1 | flag2)) return flag3;
        if (CheckMask(piecemask, flag3 | flag2)) return flag1;
        if (CheckMask(piecemask, flag3 | flag1)) return flag2;
        return 0;
    }

    // QC ttt_ai_random: choose a random set bit in the 9-bit mask (deterministic via Prandom).
    private static int RandomFlag(int piecemask)
    {
        if (piecemask == 0) return 0;
        int count = 0;
        int f = 1;
        for (int i = 0; i < 9; i++) { if ((piecemask & f) != 0) count++; f <<= 1; }
        int pick = Prandom.RangeInt(0, count); // 0..count-1
        f = 1;
        for (int i = 0; i < 9; i++)
        {
            if ((piecemask & f) != 0)
            {
                if (pick == 0) return f;
                pick--;
            }
            f <<= 1;
        }
        return 0;
    }

    // QC ttt_ai_block3: among all 8 lines, the third-cell flags completing a 2-in-a-row, masked to free cells.
    private static int Block3(int piecemask, int piecemaskFree)
    {
        int r = 0;
        // columns
        r |= OneOf3(piecemask, A1, A2, A3);
        r |= OneOf3(piecemask, B1, B2, B3);
        r |= OneOf3(piecemask, C1, C2, C3);
        // rows
        r |= OneOf3(piecemask, A1, B1, C1);
        r |= OneOf3(piecemask, A2, B2, C2);
        r |= OneOf3(piecemask, A3, B3, C3);
        // diagonals
        r |= OneOf3(piecemask, A1, B2, C3);
        r |= OneOf3(piecemask, A3, B2, C1);
        r &= piecemaskFree;
        return RandomFlag(r);
    }

    // QC ttt_ai_choose_simple: 1) win if possible, 2) block opponent's win, 3) random free cell.
    private static string? ChooseSimple(int self, int opponent, int free)
    {
        int move = Block3(self, free);
        if (move != 0) return FlagToPos(move);          // winning move
        move = Block3(opponent, free);
        if (move != 0) return FlagToPos(move);          // block the opponent
        return FlagToPos(RandomFlag(free));             // random
    }

    /// <summary>QC ttt_ai (minigame field): the AI's team number, or 0 if this isn't a single-player game.</summary>
    public static int AiTeam(MinigameSession session)
        => session.Extra.TryGetValue(AiKey, out var o) && o is int t ? t : 0;

    /// <summary>
    /// QC "singleplayer" command: enable the AI opponent when exactly one human has joined. The AI takes the
    /// team after the lone player's (minigame_next_team), and immediately moves if it's its turn.
    /// </summary>
    public void EnableSinglePlayer(MinigameSession session)
    {
        if (session.PlayerCount != 1) return;
        int aiTeam = MinigameSession.NextTeam(session.Players[0].Team, MaxPlayers);
        session.Extra[AiKey] = aiTeam;
        // seat an AI player on that team (QC spawns a minigame_player with slot 0).
        if (session.PlayerByTeam(aiTeam) is null)
            session.Players.Add(new MinigamePlayer(aiTeam, slot: 0));
        AiMove(session);
    }

    /// <summary>
    /// QC ttt_aimove: if it's the AI's turn in the placing phase, build the three bitmasks (self/opponent/
    /// free) and play the chosen move via <see cref="Move"/>. No-op if it isn't the AI's turn.
    /// </summary>
    public void AiMove(MinigameSession session)
    {
        int ai = AiTeam(session);
        if (ai == 0) return;
        if (session.TurnType != MinigameTurn.Play || session.CurrentTeam != ai) return;
        var board = session.Board;
        if (board is null) return;
        var aiPlayer = session.PlayerByTeam(ai);
        if (aiPlayer is null) return;

        int self = 0, opponent = 0, free = 0, flag = 1;
        for (int i = 0; i < Size; i++)        // column (letter)
        for (int j = 0; j < Size; j++)        // row (number)
        {
            int team = board.TeamAt(i, j);
            if (team == ai) self |= flag;
            else if (team != 0) opponent |= flag;
            else free |= flag;
            flag <<= 1;
        }

        string? pos = ChooseSimple(self, opponent, free);
        if (pos is not null) Move(session, aiPlayer, pos);
    }
}
