namespace XonoticGodot.Common.Gameplay;

// Server-side rules for Peg Solitaire — port of common/minigames/minigame/ps.qc (ps_server_event, ps_move,
// ps_move_piece, ps_winning_piece, ps_setup_pieces, ps_valid_tile, ps_tile_blacklisted, ps_draw).
//
// Single player. Board is a 7x7 grid with the four 2x2 corners removed (the classic English cross).
// It starts full except the centre cell. A move selects a peg and jumps it orthogonally over an adjacent
// peg into the empty cell two away; the jumped peg is removed. The game ends when no move remains:
//   * exactly the goal reached (a single peg left? QC: any pieces remain => draw, none => win).
//     Concretely QC sets WIN only if the board is empty afterwards, otherwise DRAW. (A "perfect" clear is
//     practically a single centred peg; QC's check is "no pieces remain" via ps_draw.)
//
// A move is encoded as "<from> <to>" (two tile ids), matching QC's `cmd move <piece> <pos>`.
// Omitted vs QC: CSQC rendering + networking.

public sealed class PegSolitaire : Minigame
{
    public const int Size = 7; // QC PS_LET_CNT == PS_NUM_CNT == 7

    public override string NetName => "ps";
    public override string DisplayName => "Peg Solitaire";
    public override int MaxPlayers => 1;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this) { Board = new MinigameBoard(Size, Size) };
        Start(s);
        return s;
    }

    // QC "start": ps_setup_pieces + flags = PS_TURN_MOVE.
    public override int Start(MinigameSession session)
    {
        session.Board ??= new MinigameBoard(Size, Size);
        SetupPieces(session.Board);
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        return 1;
    }

    public override int Join(MinigameSession session, MinigamePlayer player)
        => session.PlayerCount >= 1 ? MinigameSession.SpectatorTeam : 1;

    // QC ps_setup_pieces: fill every valid tile except the centre.
    private static void SetupPieces(MinigameBoard board)
    {
        board.Clear();
        int mid = Size / 2; // floor(7*0.5) == 3
        for (int number = 0; number < Size; number++)
        for (int letter = 0; letter < Size; letter++)
        {
            string tile = MinigameTiles.BuildName(letter, number);
            if (!ValidTile(tile)) continue;
            if (letter == mid && number == mid) continue; // centre starts empty
            board.Place(tile, 1);
        }
    }

    // QC ps_move: validate target empty + valid, and that `from` holds a peg; then test the 4 orthogonal
    // jump directions — the one whose landing cell == `to` removes the middle peg and moves the peg.
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var board = session.Board;
        if (board is null) return MoveResult.Invalid;
        if (session.TurnType != MinigameTurn.Play) return MoveResult.Invalid;

        var parts = move.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return MoveResult.Invalid;
        string from = parts[0], to = parts[1];

        if (!ValidTile(to)) return MoveResult.Invalid;
        if (!board.IsEmpty(to)) return MoveResult.Invalid;
        if (board.Find(from) is null) return MoveResult.Invalid;

        int letter = MinigameTiles.Letter(from);
        int number = MinigameTiles.Number(from);

        // try each direction: (dx,dy) over the adjacent cell into the cell two away
        bool done =
            TryJump(board, from, to, letter, number, -1, 0) ||
            TryJump(board, from, to, letter, number, +1, 0) ||
            TryJump(board, from, to, letter, number, 0, -1) ||
            TryJump(board, from, to, letter, number, 0, +1);

        if (!done) return MoveResult.Invalid;

        // QC: ps_winning_piece == "no valid move remains"; then ps_draw splits win vs draw.
        if (!AnyMoveAvailable(board))
        {
            if (board.PieceCount > 0)
            {
                session.TurnType = MinigameTurn.Draw;
                session.CurrentTeam = 0;
            }
            else
            {
                session.TurnType = MinigameTurn.Win;
                session.Winner = player.Team;
            }
            return MoveResult.GameOver;
        }

        session.TurnType = MinigameTurn.Play;
        return MoveResult.Accepted;
    }

    // One jump direction (QC ps_move_piece): the adjacent cell must hold a peg, and the landing cell two
    // away must equal `to`. On success remove the middle peg and relocate the moving peg.
    private static bool TryJump(MinigameBoard board, string from, string to, int letter, int number, int dx, int dy)
    {
        string mid = MinigameTiles.BuildName(letter + dx, number + dy);
        if (board.Find(mid) is null) return false;

        string landing = MinigameTiles.BuildName(letter + 2 * dx, number + 2 * dy);
        if (!ValidTile(landing) || landing != to) return false;

        board.Remove(mid);             // capture the jumped peg
        board.MovePiece(from, to);     // move the selected peg into the empty cell
        return true;
    }

    public override MinigameTurn CheckEnd(MinigameSession session)
    {
        var board = session.Board;
        if (board is null) return session.TurnType;
        if (AnyMoveAvailable(board)) return MinigameTurn.Play;
        return board.PieceCount > 0 ? MinigameTurn.Draw : MinigameTurn.Win;
    }

    // QC ps_winning_piece (inverted): does ANY peg have a legal jump? Scans every peg, each of the 4
    // directions: an adjacent peg with an empty valid landing cell two away is a move.
    private static bool AnyMoveAvailable(MinigameBoard board)
    {
        foreach (var (tile, _) in board.Cells)
        {
            int letter = MinigameTiles.Letter(tile);
            int number = MinigameTiles.Number(tile);
            if (HasJump(board, letter, number, -1, 0) ||
                HasJump(board, letter, number, +1, 0) ||
                HasJump(board, letter, number, 0, -1) ||
                HasJump(board, letter, number, 0, +1))
                return true;
        }
        return false;
    }

    private static bool HasJump(MinigameBoard board, int letter, int number, int dx, int dy)
    {
        string mid = MinigameTiles.BuildName(letter + dx, number + dy);
        if (board.Find(mid) is null) return false;
        string landing = MinigameTiles.BuildName(letter + 2 * dx, number + 2 * dy);
        return ValidTile(landing) && board.IsEmpty(landing);
    }

    // QC ps_tile_blacklisted: the four 2x2 corner blocks are not part of the cross board.
    public static bool TileBlacklisted(string tile)
    {
        int number = MinigameTiles.Number(tile);
        int letter = MinigameTiles.Letter(tile);
        if (letter < 2)
        {
            if (number < 2) return true;
            if (number > Size - 3) return true;
        }
        if (letter > Size - 3)
        {
            if (number < 2) return true;
            if (number > Size - 3) return true;
        }
        return false;
    }

    // QC ps_valid_tile: in-grid and not blacklisted.
    public static bool ValidTile(string? tile)
    {
        if (string.IsNullOrEmpty(tile)) return false;
        if (TileBlacklisted(tile)) return false;
        int number = MinigameTiles.Number(tile);
        int letter = MinigameTiles.Letter(tile);
        return number >= 0 && number < Size && letter >= 0 && letter < Size;
    }

    // The only QC pieces not ported are CSQC rendering + entity networking (out of scope for the
    // server-side port). Peg Solitaire has no AI opponent in QC, so there is nothing further to port.
}
