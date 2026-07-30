namespace XonoticGodot.Common.Gameplay;

// Server-side rules for Connect Four — port of common/minigames/minigame/c4.qc (c4_server_event, c4_move,
// c4_get_lowest_tile, c4_winning_piece, c4_valid_tile). 7 columns x 6 rows, two teams; a move names a
// column and the piece falls to the lowest empty cell; first to line up four (horizontal, vertical, or
// either diagonal) wins; a full board is a draw.
//
// Tile ids: column 'a'..'g' (0..6) x row 1..6 (0..5 internally), row 0 at the bottom.
// The QC win-check walks to an extreme cell then counts back; here it is rewritten as the equivalent
// "count consecutive same-team cells in both directions along each of the 4 axes" (clearer, same result).
// Omitted vs QC: CSQC drawing + networking.

public sealed class ConnectFour : Minigame
{
    public const int Columns = 7; // QC C4_LET_CNT
    public const int Rows = 6;    // QC C4_NUM_CNT
    public const int WinCount = 4; // QC C4_WIN_CNT
    public const int MaxTiles = 42; // QC C4_MAX_TILES (7*6)

    public override string NetName => "c4";
    public override string DisplayName => "Connect Four";
    public override int MaxPlayers => 2;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this) { Board = new MinigameBoard(Columns, Rows) };
        Start(s);
        return s;
    }

    // QC "start": minigame_flags = C4_TURN_PLACE | C4_TURN_TEAM1
    public override int Start(MinigameSession session)
    {
        session.Board ??= new MinigameBoard(Columns, Rows);
        session.Board.Clear();
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        session.State = 1; // c4_nexteam
        return 1;
    }

    // QC c4_server_event "cmd" → c4_move: the client sends "move <col>" (a board click) or a bare column id for
    // direct callers; the requested column is resolved to its lowest free cell, then placed if it's the placing
    // phase, the mover's turn, and the (resolved) tile is valid and empty.
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var board = session.Board;
        if (board is null || string.IsNullOrEmpty(move)) return MoveResult.Invalid;
        // QC: spectators can't act (player.team == C4_SPECTATOR_TEAM).
        if (player.Team == MinigameSession.SpectatorTeam) return MoveResult.Invalid;
        if (session.TurnType != MinigameTurn.Play) return MoveResult.Invalid;
        if (player.Team != session.CurrentTeam) return MoveResult.Invalid;

        // Strip a leading "move " verb (the cmd form); a bare column id passes through.
        var parts = move.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string col = parts.Length >= 2 && parts[0] == "move" ? parts[1]
                   : parts.Length == 1 && parts[0] == "move" ? ""
                   : move;
        if (string.IsNullOrEmpty(col)) return MoveResult.Invalid;

        string pos = LowestTile(board, col);
        if (!ValidTile(pos) || !board.IsEmpty(pos)) return MoveResult.Invalid;

        board.Place(pos, player.Team);
        session.State = MinigameSession.NextTeam(player.Team, MaxPlayers);

        if (IsWinningPiece(board, pos, player.Team))
        {
            player.Score++;
            session.TurnType = MinigameTurn.Win;
            session.Winner = player.Team;
            session.CurrentTeam = player.Team;
            return MoveResult.GameOver;
        }

        if (board.PieceCount >= MaxTiles)
        {
            session.TurnType = MinigameTurn.Draw;
            session.CurrentTeam = 0;
            return MoveResult.GameOver;
        }

        session.CurrentTeam = session.State;
        return MoveResult.Accepted;
    }

    public override MinigameTurn CheckEnd(MinigameSession session) => session.TurnType;

    /// <summary>QC c4_valid_tile: within the 7x6 grid.</summary>
    public static bool ValidTile(string? tile)
    {
        if (string.IsNullOrEmpty(tile)) return false;
        int number = MinigameTiles.Number(tile);
        int letter = MinigameTiles.Letter(tile);
        return number >= 0 && number < Rows && letter >= 0 && letter < Columns;
    }

    // QC c4_get_lowest_tile: in the column of `s`, find the lowest empty cell (the first empty cell from the
    // bottom whose cell below is occupied, or the very bottom). Returns the tile id in that column.
    private static string LowestTile(MinigameBoard board, string s)
    {
        int letter = MinigameTiles.Letter(s);
        for (int i = 0; i < Rows; i++)
        {
            if (board.IsEmpty(MinigameTiles.BuildName(letter, i)))
                return MinigameTiles.BuildName(letter, i);
        }
        // column full: return the top cell (move will be rejected as occupied)
        return MinigameTiles.BuildName(letter, Rows - 1);
    }

    // QC c4_winning_piece: four-in-a-row through the placed piece along any of the 4 axes.
    private static bool IsWinningPiece(MinigameBoard b, string tile, int team)
    {
        int letter = MinigameTiles.Letter(tile);
        int number = MinigameTiles.Number(tile);

        return Line(b, letter, number, 1, 0, team)   // horizontal  ( - )
            || Line(b, letter, number, 0, 1, team)   // vertical    ( | )
            || Line(b, letter, number, 1, 1, team)   // diagonal up ( / )
            || Line(b, letter, number, 1, -1, team); // diagonal dn ( \ )
    }

    // Count the placed cell plus consecutive same-team cells in +(dx,dy) and -(dx,dy); >= WinCount wins.
    private static bool Line(MinigameBoard b, int letter, int number, int dx, int dy, int team)
    {
        int count = 1;
        count += Run(b, letter, number, dx, dy, team);
        count += Run(b, letter, number, -dx, -dy, team);
        return count >= WinCount;
    }

    private static int Run(MinigameBoard b, int letter, int number, int dx, int dy, int team)
    {
        int found = 0;
        int l = letter + dx, n = number + dy;
        while (l >= 0 && l < Columns && n >= 0 && n < Rows && b.TeamAt(l, n) == team)
        {
            found++;
            l += dx;
            n += dy;
        }
        return found;
    }

    // The only QC pieces not ported are CSQC rendering + entity networking (out of scope for the
    // server-side port). Connect Four has no AI opponent in QC, so there is nothing further to port.
}
