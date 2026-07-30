using System.Collections.Generic;

namespace XonoticGodot.Common.Gameplay;

// Server-side rules for Nine Men's Morris — port of common/minigames/minigame/nmm.qc (nmm_server_event,
// the move handler, nmm_in_mill / nmm_tile_adjacent / nmm_tile_canmove, nmm_count_pieces, the tile/mill
// construction nmm_spawn_tile[_square] / nmm_tile_build_hmill / nmm_tile_build_vmill).
//
// The board is three concentric squares on a 7x7 grid, addressed by tile ids (column 'a'..'g', row 1..7).
// The squares are built recursively: outer (positions in {0,3,6}, minus the centre 3,3), middle ({1,3,5}),
// inner ({2,3,4}); each square's tiles record a "distance" (3, 2, 1 respectively) used for adjacency and
// the mill lines. Each team has 7 pieces (QC spawns 7 per side) that progress HOME -> BOARD -> DEAD.
//
// Turn phases (decoded onto MinigameTurn + per-phase enum): PLACE (drop a home piece), MOVE (slide to an
// adjacent empty tile), FLY (move anywhere when reduced to 3 pieces), TAKE (after forming a mill, capture
// an enemy piece — preferring non-mill pieces unless all enemy pieces are in mills). Reducing a side below
// 3 pieces, or leaving it with no legal move, wins.
//
// Omitted vs QC: CSQC rendering + entity networking only. This is a self-contained server-side model (it
// does not use MinigameBoard, which can't express the tile graph / mills / distances).

/// <summary>NMM piece placement state (QC NMM_PIECE_*).</summary>
public enum NmmPieceState
{
    /// <summary>NMM_PIECE_DEAD — captured by the enemy.</summary>
    Dead = 0,
    /// <summary>NMM_PIECE_HOME — not yet placed.</summary>
    Home = 1,
    /// <summary>NMM_PIECE_BOARD — placed on the board.</summary>
    Board = 2,
}

/// <summary>The fine-grained turn phase of an NMM session (the high bits of QC minigame_flags).</summary>
public enum NmmPhase
{
    Place,    // NMM_TURN_PLACE
    Move,     // NMM_TURN_MOVE
    Fly,      // NMM_TURN_FLY
    Take,     // NMM_TURN_TAKE
    TakeAny,  // NMM_TURN_TAKE | NMM_TURN_TAKEANY (may take a mill piece)
    Win,      // NMM_TURN_WIN
}

/// <summary>One NMM piece (QC minigame_board_piece): a team and a placement state.</summary>
public sealed class NmmPiece
{
    public int Team;
    public NmmPieceState State;
    /// <summary>Tile id this piece occupies while on the board, else null.</summary>
    public string? Tile;
    public NmmPiece(int team) { Team = team; State = NmmPieceState.Home; }
}

/// <summary>One NMM tile (QC minigame_nmm_tile): its id, distance, occupant, and the two mill lines.</summary>
public sealed class NmmTile
{
    public string Id = "";
    public int Distance;
    public NmmPiece? Piece;
    /// <summary>Tile ids that, together with this one, form a horizontal mill (QC nmm_tile_hmill).</summary>
    public string[] HMill = System.Array.Empty<string>();
    /// <summary>Tile ids forming a vertical mill (QC nmm_tile_vmill).</summary>
    public string[] VMill = System.Array.Empty<string>();
}

/// <summary>The whole NMM session state (QC: the tile + piece entities owned by the minigame).</summary>
public sealed class NmmState
{
    public readonly Dictionary<string, NmmTile> Tiles = new(System.StringComparer.Ordinal);
    public readonly List<NmmPiece> Pieces = new();
    public NmmPhase Phase = NmmPhase.Place;
    public int Turn = 1;            // whose turn (team 1/2)

    public NmmTile? Tile(string id) => Tiles.TryGetValue(id, out var t) ? t : null;
}

public sealed class NineMensMorris : Minigame
{
    public const int Size = 7;        // 7x7 grid
    public const int PiecesPerTeam = 7; // QC spawns 7 per side
    public const string StateKey = "nmm";

    public override string NetName => "nmm";
    public override string DisplayName => "Nine Men's Morris";
    public override int MaxPlayers => 2;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this);
        Start(s);
        return s;
    }

    public static NmmState StateOf(MinigameSession session)
    {
        if (session.Extra.TryGetValue(StateKey, out var o) && o is NmmState st) return st;
        var fresh = new NmmState();
        session.Extra[StateKey] = fresh;
        return fresh;
    }

    // QC "start": flags = PLACE|TEAM1, init tiles, spawn 7 home pieces per team.
    public override int Start(MinigameSession session)
    {
        var st = StateOf(session);
        st.Tiles.Clear();
        st.Pieces.Clear();
        InitTiles(st);
        for (int i = 0; i < PiecesPerTeam; i++)
        {
            st.Pieces.Add(new NmmPiece(1));
            st.Pieces.Add(new NmmPiece(2));
        }
        st.Phase = NmmPhase.Place;
        st.Turn = 1;
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        return 1;
    }

    // QC "join": team 1 then 2, else spectator.
    public override int Join(MinigameSession session, MinigamePlayer player)
    {
        if (session.PlayerCount >= 2) return MinigameSession.SpectatorTeam;
        if (session.Players.Count > 0 && session.Players[0].Team == 1) return 2;
        return 1;
    }

    // =====================================================================================
    //  Tile / mill construction (nmm_spawn_tile_square + nmm_tile_build_hmill/vmill)
    // =====================================================================================

    private static void InitTiles(NmmState st) => SpawnSquare(st, 0, 2);

    // QC nmm_spawn_tile_square: a 3x3 ring of tiles at (offset + k*(skip+1)) for k=0,1,2 (skipping the
    // centre of the ring), then recurse inward with offset+1, skip-1. distance = skip+1.
    private static void SpawnSquare(NmmState st, int offset, int skip)
    {
        int step = skip + 1;
        int letter = offset;
        for (int i = 0; i < 3; i++)
        {
            int number = offset;
            for (int j = 0; j < 3; j++)
            {
                if (i != 1 || j != 1)
                    SpawnTile(st, MinigameTiles.BuildName(letter, number), step);
                number += step;
            }
            letter += step;
        }
        if (skip > 0) SpawnSquare(st, offset + 1, skip - 1);
    }

    private static void SpawnTile(NmmState st, string id, int distance)
    {
        var tile = new NmmTile { Id = id, Distance = distance };
        tile.HMill = BuildHMill(id, distance);
        tile.VMill = BuildVMill(id, distance);
        st.Tiles[id] = tile;
    }

    // QC nmm_tile_build_hmill: the 3 tile ids of the horizontal mill through `id`.
    private static string[] BuildHMill(string id, int distance)
    {
        int number = MinigameTiles.Number(id);
        int letter = MinigameTiles.Letter(id);
        if (number == letter || number + letter == 6)
        {
            int add = letter < 3 ? 1 : -1;
            return new[]
            {
                id,
                MinigameTiles.BuildName(letter + add * distance, number),
                MinigameTiles.BuildName(letter + add * 2 * distance, number),
            };
        }
        if (letter == 3)
            return new[]
            {
                MinigameTiles.BuildName(letter - distance, number),
                id,
                MinigameTiles.BuildName(letter + distance, number),
            };
        if (letter < 3)
            return new[] { MinigameTiles.BuildName(0, number), MinigameTiles.BuildName(1, number), MinigameTiles.BuildName(2, number) };
        return new[] { MinigameTiles.BuildName(4, number), MinigameTiles.BuildName(5, number), MinigameTiles.BuildName(6, number) };
    }

    // QC nmm_tile_build_vmill: the 3 tile ids of the vertical mill through `id`.
    private static string[] BuildVMill(string id, int distance)
    {
        int letter = MinigameTiles.Letter(id);
        int number = MinigameTiles.Number(id);
        if (letter == number || letter + number == 6)
        {
            int add = number < 3 ? 1 : -1;
            return new[]
            {
                id,
                MinigameTiles.BuildName(letter, number + add * distance),
                MinigameTiles.BuildName(letter, number + add * 2 * distance),
            };
        }
        if (number == 3)
            return new[]
            {
                MinigameTiles.BuildName(letter, number - distance),
                id,
                MinigameTiles.BuildName(letter, number + distance),
            };
        if (number < 3)
            return new[] { MinigameTiles.BuildName(letter, 0), MinigameTiles.BuildName(letter, 1), MinigameTiles.BuildName(letter, 2) };
        return new[] { MinigameTiles.BuildName(letter, 4), MinigameTiles.BuildName(letter, 5), MinigameTiles.BuildName(letter, 6) };
    }

    // =====================================================================================
    //  Queries (adjacency, mills, counts)
    // =====================================================================================

    // QC nmm_tile_adjacent: tiles are adjacent if they share a row/column and differ by 1 or by `distance`.
    public static bool Adjacent(NmmTile a, NmmTile b)
    {
        int dn = System.Math.Abs(MinigameTiles.Number(a.Id) - MinigameTiles.Number(b.Id));
        int dl = System.Math.Abs(MinigameTiles.Letter(a.Id) - MinigameTiles.Letter(b.Id));
        return (dn == 0 && (dl == 1 || dl == a.Distance)) ||
               (dl == 0 && (dn == 1 || dn == a.Distance));
    }

    // QC nmm_tile_canmove: at least one adjacent tile is free.
    private static bool CanMove(NmmState st, NmmTile tile)
    {
        foreach (var other in st.Tiles.Values)
            if (other.Piece is null && Adjacent(other, tile))
                return true;
        return false;
    }

    // QC nmm_in_mill_string: every tile in the line is occupied by this tile's piece's team.
    private static bool InMillLine(NmmState st, NmmTile tile, string[] line)
    {
        if (tile.Piece is null) return false;
        foreach (var id in line)
        {
            var t = st.Tile(id);
            if (t?.Piece is null || t.Piece.Team != tile.Piece.Team) return false;
        }
        return true;
    }

    // QC nmm_in_mill: the tile's piece completes a horizontal or vertical mill.
    public static bool InMill(NmmState st, NmmTile tile)
        => tile.Piece is not null && (InMillLine(st, tile, tile.HMill) || InMillLine(st, tile, tile.VMill));

    // QC nmm_count_pieces: pieces of `team` matching any of the given states.
    private static int CountPieces(NmmState st, int team, NmmPieceState a, NmmPieceState b)
    {
        int n = 0;
        foreach (var p in st.Pieces)
            if (p.Team == team && (p.State == a || p.State == b)) n++;
        return n;
    }

    private static NmmPiece? FindHomePiece(NmmState st, int team)
    {
        foreach (var p in st.Pieces)
            if (p.Team == team && p.State == NmmPieceState.Home) return p;
        return null;
    }

    // =====================================================================================
    //  Move handler (nmm_server_event "cmd" "move")
    // =====================================================================================

    /// <summary>
    /// Apply a move (QC "move &lt;tile&gt; [tile2]"). In PLACE: <paramref name="move"/> is the destination
    /// tile for a home piece. In MOVE/FLY: "&lt;from&gt; &lt;to&gt;". In TAKE: the enemy tile to capture.
    /// </summary>
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var st = StateOf(session);
        if (session.TurnType != MinigameTurn.Play) return MoveResult.Invalid;
        if (player.Team != st.Turn) return MoveResult.Invalid;
        if (st.Phase == NmmPhase.Win) return MoveResult.Invalid;

        var parts = move.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return MoveResult.Invalid;

        var tile = st.Tile(parts[0]);
        if (tile is null) return MoveResult.Invalid;

        bool moveOk = false;
        NmmTile resultTile = tile; // the tile to test for a mill after a placement/move

        switch (st.Phase)
        {
            case NmmPhase.Place:
            {
                var piece = FindHomePiece(st, player.Team);
                if (tile.Piece is null && piece is not null)
                {
                    tile.Piece = piece;
                    piece.State = NmmPieceState.Board;
                    piece.Tile = tile.Id;
                    moveOk = true;
                }
                break;
            }
            case NmmPhase.Move:
            case NmmPhase.Fly:
            {
                if (parts.Length < 2) return MoveResult.Invalid;
                var dest = st.Tile(parts[1]);
                if (dest is null) return MoveResult.Invalid;
                if (tile.Piece is not null && tile.Piece.Team == player.Team && dest.Piece is null
                    && (st.Phase == NmmPhase.Fly || Adjacent(tile, dest)))
                {
                    var piece = tile.Piece;
                    tile.Piece = null;
                    dest.Piece = piece;
                    piece.Tile = dest.Id;
                    resultTile = dest;
                    moveOk = true;
                }
                break;
            }
            case NmmPhase.Take:
            case NmmPhase.TakeAny:
            {
                var piece = tile.Piece;
                if (piece is not null && piece.Team != player.Team)
                {
                    // QC only forbids taking mill pieces unless TAKEANY (the CSQC selection enforces it; the
                    // server trusts the request). We honour the same: in Take (not TakeAny) skip mill pieces.
                    if (st.Phase == NmmPhase.Take && InMill(st, tile)) return MoveResult.Invalid;
                    tile.Piece = null;
                    piece.State = NmmPieceState.Dead;
                    piece.Tile = null;
                    moveOk = true;
                }
                break;
            }
        }

        if (!moveOk) return MoveResult.Invalid;
        return ResolveTurn(session, st, player.Team, resultTile);
    }

    // The post-move turn transition (QC nmm_server_event after a successful move): check for a loss, a mill
    // (-> TAKE), or hand the turn to the next team (PLACE / MOVE / FLY), else the mover wins.
    private MoveResult ResolveTurn(MinigameSession session, NmmState st, int team, NmmTile placed)
    {
        int nextTeam = team % 2 + 1;
        int nextPieces = CountPieces(st, nextTeam, NmmPieceState.Home, NmmPieceState.Board);

        // reducing the opponent below 3 pieces wins immediately.
        if (nextPieces < 3)
            return WinFor(session, st, team);

        // forming a (new) mill lets the mover capture, unless we were already in the TAKE phase.
        if (st.Phase != NmmPhase.Take && st.Phase != NmmPhase.TakeAny && InMill(st, placed))
        {
            // can we take a non-mill enemy piece? if all enemy pieces are in mills, allow taking any.
            bool anyNonMill = false;
            foreach (var t in st.Tiles.Values)
                if (t.Piece is not null && t.Piece.Team == nextTeam && !InMill(st, t)) { anyNonMill = true; break; }
            st.Phase = anyNonMill ? NmmPhase.Take : NmmPhase.TakeAny;
            // turn stays with the same team for the capture.
            SyncSession(session, st);
            return MoveResult.Accepted;
        }

        // hand the turn to the next team in the appropriate phase.
        if (FindHomePiece(st, nextTeam) is not null)
            st.Phase = NmmPhase.Place;
        else if (nextPieces == 3)
            st.Phase = NmmPhase.Fly;
        else
        {
            // MOVE phase — but only if the next team actually has a legal move; otherwise the mover wins.
            st.Phase = NmmPhase.Win;
            foreach (var t in st.Tiles.Values)
                if (t.Piece is not null && t.Piece.Team == nextTeam && CanMove(st, t))
                {
                    st.Phase = NmmPhase.Move;
                    break;
                }
            if (st.Phase == NmmPhase.Win)
                return WinFor(session, st, team);
        }

        st.Turn = nextTeam;
        SyncSession(session, st);
        return MoveResult.Accepted;
    }

    private MoveResult WinFor(MinigameSession session, NmmState st, int team)
    {
        st.Phase = NmmPhase.Win;
        session.TurnType = MinigameTurn.Win;
        session.Winner = team;
        session.CurrentTeam = team;
        var p = session.PlayerByTeam(team);
        if (p is not null) p.Score++;
        return MoveResult.GameOver;
    }

    // mirror the NMM phase/turn onto the generic session fields.
    private static void SyncSession(MinigameSession session, NmmState st)
    {
        session.CurrentTeam = st.Turn;
        session.TurnType = st.Phase == NmmPhase.Win ? MinigameTurn.Win : MinigameTurn.Play;
    }

    /// <summary>The fine-grained phase of an NMM session (for the driver/UI). QC minigame_flags turn type.</summary>
    public static NmmPhase Phase(MinigameSession session) => StateOf(session).Phase;

    public override MinigameTurn CheckEnd(MinigameSession session)
        => StateOf(session).Phase == NmmPhase.Win ? MinigameTurn.Win : MinigameTurn.Play;
}
