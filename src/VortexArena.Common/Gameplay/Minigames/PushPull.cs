namespace XonoticGodot.Common.Gameplay;

// Server-side rules for Push-Pull — port of common/minigames/minigame/pp.qc (pp_server_event, pp_move,
// pp_valid_move, pp_winning_piece, pp_setup_pieces, pp_valid_tile, pp_next_match). Two teams on a 7x7
// board. Pieces start on the edges (team 1 on the top+bottom rows, team 2 on the left+right columns). A
// move places a new piece of the mover's team on a tile adjacent (8-connected) to the "current piece" (the
// last piece placed); the very first move may go anywhere. If a piece already occupies the target it is
// captured — the team owning the captured piece scores — and the previously-current piece is "disabled"
// (team 5). The game ends when the just-placed piece is fully surrounded (every neighbour is off-board or a
// disabled team-5 piece); the higher score wins, equal scores draw.
//
// Tile ids follow MinigameTiles: column 'a'..'g' (0..6) x row 1..7 (0..6 internally).
// Omitted vs QC: CSQC rendering + entity networking only.
//
// We reuse MinigameBoard but encode the per-piece "disabled" state in the cell Team (5) and the per-piece
// "is current" via PpState.CurrentTile. The two team scores live in PpState (QC pp_team1_score/2_score).

/// <summary>The Push-Pull session sub-state (QC minigame fields pp_curr_piece / pp_team*_score / pp_nexteam).</summary>
public sealed class PpState
{
    /// <summary>Disabled-piece team marker (QC: piece.team == 5).</summary>
    public const int Disabled = 5;

    /// <summary>Tile id of the current target piece (QC pp_curr_piece), or null at match start.</summary>
    public string? CurrentTile;

    /// <summary>Score for team 1 / team 2 (QC pp_team1_score / pp_team2_score).</summary>
    public int Team1Score;
    public int Team2Score;

    /// <summary>The team that moves next (QC pp_nexteam).</summary>
    public int NextTeam = 1;
}

public sealed class PushPull : Minigame
{
    public const int Size = 7;       // QC PP_LET_CNT == PP_NUM_CNT == 7
    public const string StateKey = "pp";

    public override string NetName => "pp";
    public override string DisplayName => "Push-Pull";
    public override int MaxPlayers => 2;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this) { Board = new MinigameBoard(Size, Size) };
        Start(s);
        return s;
    }

    public static PpState StateOf(MinigameSession session)
    {
        if (session.Extra.TryGetValue(StateKey, out var o) && o is PpState st) return st;
        var fresh = new PpState();
        session.Extra[StateKey] = fresh;
        return fresh;
    }

    // QC "start": flags = PP_TURN_PLACE | PP_TURN_TEAM1; pp_setup_pieces.
    public override int Start(MinigameSession session)
    {
        session.Board ??= new MinigameBoard(Size, Size);
        var st = StateOf(session);
        st.CurrentTile = null;
        st.Team1Score = 0;
        st.Team2Score = 0;
        st.NextTeam = 1;
        SetupPieces(session.Board, st);
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        return 1;
    }

    // QC "join": team 1 then 2, else spectator.
    public override int Join(MinigameSession session, MinigamePlayer player)
    {
        if (session.PlayerCount >= MaxPlayers) return MinigameSession.SpectatorTeam;
        if (session.Players.Count > 0)
            return MinigameSession.NextTeam(session.Players[^1].Team, MaxPlayers);
        return 1;
    }

    // QC pp_setup_pieces: team 1 on the top/bottom rows (number 0 or 6, letter 1..5), team 2 on the
    // left/right columns (letter 0 or 6, number 1..6).
    private static void SetupPieces(MinigameBoard board, PpState st)
    {
        board.Clear();
        for (int i = 0; i < Size; i++)        // letter (column)
        for (int t = 0; t < Size; t++)        // number (row)
        {
            bool team2 = (i == 0 || i == Size - 1) && t > 0 && t < Size - 1;
            bool team1 = i > 0 && i < Size - 1 && (t == 0 || t == Size - 1);
            if (team1 || team2)
                board.Place(MinigameTiles.BuildName(i, t), team1 ? 1 : 2);
        }
        st.CurrentTile = null;
    }

    /// <summary>QC pp_valid_tile: within the 7x7 grid.</summary>
    public static bool ValidTile(string? tile)
    {
        if (string.IsNullOrEmpty(tile)) return false;
        int number = MinigameTiles.Number(tile);
        int letter = MinigameTiles.Letter(tile);
        return number >= 0 && number < Size && letter >= 0 && letter < Size;
    }

    // QC pp_valid_move: target must be valid, not a disabled (team-5) piece, and adjacent to the current
    // piece (or anywhere if there is no current piece).
    private static bool ValidMove(MinigameBoard board, PpState st, string pos)
    {
        if (!ValidTile(pos)) return false;
        if (board.TeamAt(pos) == PpState.Disabled) return false;
        if (st.CurrentTile is null) return true; // first move: anywhere

        int number = MinigameTiles.Number(pos);
        int letter = MinigameTiles.Letter(pos);
        // 8-connected adjacency to the current tile.
        foreach (var (dx, dy) in Neighbors8)
            if (MinigameTiles.BuildName(letter + dx, number + dy) == st.CurrentTile)
                return true;
        return false;
    }

    private static readonly (int dx, int dy)[] Neighbors8 =
    {
        (-1, 0), (1, 0), (0, -1), (0, 1), (1, 1), (-1, -1), (1, -1), (-1, 1),
    };

    // QC pp_move: place the mover's piece (capturing any occupant), disable the previous current piece,
    // make this the new current piece, then test for a win (the placed piece fully surrounded).
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var board = session.Board;
        if (board is null || string.IsNullOrEmpty(move)) return MoveResult.Invalid;
        if (session.TurnType != MinigameTurn.Play) return MoveResult.Invalid;
        if (player.Team != session.CurrentTeam) return MoveResult.Invalid;

        var st = StateOf(session);
        if (!ValidMove(board, st, move)) return MoveResult.Invalid;

        // capture an existing piece (its owner scores).
        int existing = board.TeamAt(move);
        if (existing == 1) st.Team1Score++;
        else if (existing == 2) st.Team2Score++;

        // disable the previous current piece (team 5).
        if (st.CurrentTile is not null && board.Find(st.CurrentTile) is { } prev)
            prev.Team = PpState.Disabled;

        // place the mover's piece and make it current.
        board.Place(move, player.Team);
        st.CurrentTile = move;
        st.NextTeam = MinigameSession.NextTeam(player.Team, MaxPlayers);

        if (IsWinningPiece(board, move))
        {
            if (st.Team1Score == st.Team2Score)
            {
                session.TurnType = MinigameTurn.Draw;
                session.CurrentTeam = 0;
            }
            else
            {
                int winner = st.Team1Score > st.Team2Score ? 1 : 2;
                session.TurnType = MinigameTurn.Win;
                session.Winner = winner;
                session.CurrentTeam = winner;
                var p = session.PlayerByTeam(winner);
                if (p is not null) p.Score++;
            }
            return MoveResult.GameOver;
        }

        session.CurrentTeam = st.NextTeam;
        return MoveResult.Accepted;
    }

    // QC pp_winning_piece: the placed piece is surrounded — every one of its 8 neighbours is either
    // off-board (an invalid tile) or a disabled (team-5) piece.
    private static bool IsWinningPiece(MinigameBoard board, string tile)
    {
        int letter = MinigameTiles.Letter(tile);
        int number = MinigameTiles.Number(tile);
        foreach (var (dx, dy) in Neighbors8)
        {
            string n = MinigameTiles.BuildName(letter + dx, number + dy);
            bool blocked = !ValidTile(n) || board.TeamAt(n) == PpState.Disabled;
            if (!blocked) return false; // an open/active neighbour -> not surrounded
        }
        return true;
    }

    // QC pp_next_match (single-player branch): reset board + scores, swap starting team.
    /// <summary>Start a fresh match after a win/draw (QC "next").</summary>
    public void NextMatch(MinigameSession session)
    {
        if (!session.IsOver) return;
        var board = session.Board;
        if (board is null) return;
        var st = StateOf(session);
        st.Team1Score = 0;
        st.Team2Score = 0;
        SetupPieces(board, st);
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = st.NextTeam == 0 ? 1 : st.NextTeam;
        session.Winner = 0;
    }

    public override MinigameTurn CheckEnd(MinigameSession session) => session.TurnType;
}
