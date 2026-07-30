using System.Collections.Generic;

namespace XonoticGodot.Common.Gameplay;

// Server-side rules for Bulldozer — port of common/minigames/minigame/bd.qc (bd_server_event, bd_move,
// bd_move_dozer, bd_check_winner, bd_get_dir / bd_dir_fromname, bd_editor_place / bd_do_fill, the level
// load/save). A single-player Sokoban: a bulldozer pushes boulders onto target tiles on a 20x20 grid.
// Bricks (8 kinds, cosmetic walls) block the dozer and boulders; a boulder can be pushed into a free,
// non-brick cell; the level is solved when every target tile is covered by a boulder.
//
// QC stores bricks on per-column "controller" arrays and the dozer/boulder/target as board pieces purely
// for networking; here the whole grid is one tile-type array (BdTile[,]) since that split is irrelevant to
// the rules. The QC level load/save uses external storage files (out of scope); we provide an in-memory
// LoadLevel(piece list) + editor placement instead, which exercises the identical move/win logic.
//
// Omitted vs QC: CSQC rendering + ENT_CLIENT_BD_CONTROLLER networking + on-disk level files.

/// <summary>Bulldozer tile types (QC BD_TILE_*). 0 = empty.</summary>
public enum BdTile
{
    Empty = 0,
    Dozer = 1,    // BD_TILE_DOZER
    Target = 2,   // BD_TILE_TARGET
    Boulder = 3,  // BD_TILE_BOULDER
    Brick1 = 4, Brick2 = 5, Brick3 = 6, Brick4 = 7,
    Brick5 = 8, Brick6 = 9, Brick7 = 10, Brick8 = 11, // BD_TILE_BRICK1..8
}

/// <summary>Bulldozer move direction (QC BD_DIR_*).</summary>
public enum BdDir { Up = 0, Down = 1, Left = 2, Right = 3 }

/// <summary>The Bulldozer session sub-state: the grid, the dozer position/heading, and the move count.</summary>
public sealed class BdState
{
    public const int Size = 20; // QC BD_LET_CNT == BD_NUM_CNT == 20

    /// <summary>Solid (non-target) tiles by [letter, number] (dozer/boulder/brick). Targets are tracked separately.</summary>
    public readonly BdTile[,] Solid = new BdTile[Size, Size];
    /// <summary>Target tiles (QC the BD_TILE_TARGET pieces, which coexist with a boulder on the same cell).</summary>
    public readonly bool[,] Target = new bool[Size, Size];

    public int DozerX = -1, DozerY = -1; // dozer cell (letter, number); -1 = not placed
    public BdDir DozerDir = BdDir.Down;
    public int Moves;
    public bool Editing;

    public BdTile At(int x, int y) => InBounds(x, y) ? Solid[x, y] : BdTile.Brick1; // OOB acts solid
    public static bool InBounds(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size;

    public void Clear()
    {
        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++) { Solid[x, y] = BdTile.Empty; Target[x, y] = false; }
        DozerX = DozerY = -1;
        DozerDir = BdDir.Down;
        Moves = 0;
    }
}

public sealed class Bulldozer : Minigame
{
    public const string StateKey = "bd";

    public override string NetName => "bd";
    public override string DisplayName => "Bulldozer";
    public override int MaxPlayers => 1; // QC BD_TEAMS == 1

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this);
        Start(s);
        return s;
    }

    public static BdState StateOf(MinigameSession session)
    {
        if (session.Extra.TryGetValue(StateKey, out var o) && o is BdState st) return st;
        var fresh = new BdState();
        session.Extra[StateKey] = fresh;
        return fresh;
    }

    // QC "start": setup pieces (load the start level), flags = BD_TURN_MOVE.
    public override int Start(MinigameSession session)
    {
        var st = StateOf(session);
        st.Clear();
        st.Editing = false;
        // No on-disk level here; the host loads a level via LoadLevel(). The session starts in MOVE.
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        return 1;
    }

    public override int Join(MinigameSession session, MinigamePlayer player)
        => session.PlayerCount >= 1 ? MinigameSession.SpectatorTeam : 1;

    // =====================================================================================
    //  Direction helpers (bd_get_dir / bd_dir_fromname)
    // =====================================================================================

    private static (int dx, int dy) DirDelta(BdDir d) => d switch
    {
        BdDir.Up => (0, 1),
        BdDir.Down => (0, -1),
        BdDir.Left => (-1, 0),
        BdDir.Right => (1, 0),
        _ => (0, -1),
    };

    /// <summary>QC bd_dir_fromname: parse "up"/"u"/"down"/"dn"/"d"/"left"/"l"/"right"/"r" (default Down).</summary>
    public static BdDir DirFromName(string? s) => (s?.Trim().ToLowerInvariant()) switch
    {
        "up" or "u" => BdDir.Up,
        "down" or "dn" or "d" => BdDir.Down,
        "left" or "lt" or "l" => BdDir.Left,
        "right" or "rt" or "r" => BdDir.Right,
        _ => BdDir.Down,
    };

    private static bool IsBrick(BdTile t) => t >= BdTile.Brick1 && t <= BdTile.Brick8;

    // =====================================================================================
    //  Move handler (bd_move / bd_move_dozer)
    // =====================================================================================

    /// <summary>
    /// Apply a move command (QC "move &lt;dir&gt;"). In MOVE phase, steers the dozer one cell in
    /// <paramref name="move"/>'s direction, pushing a boulder if one is directly ahead. Returns Accepted
    /// when the dozer moved (or attempted to), GameOver when the level is solved.
    /// </summary>
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var st = StateOf(session);
        if (st.Editing || session.TurnType != MinigameTurn.Play) return MoveResult.Invalid;
        if (string.IsNullOrEmpty(move)) return MoveResult.Invalid;

        // the move arg may be "<dir>" (the rest are editor args, ignored in play).
        var parts = move.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        st.DozerDir = DirFromName(parts[0]);

        if (st.DozerX < 0) return MoveResult.Invalid; // no dozer placed
        bool moved = MoveDozer(st);
        if (moved) st.Moves++;

        if (CheckWinner(st))
        {
            session.TurnType = MinigameTurn.Win;
            session.Winner = player.Team;
            session.CurrentTeam = player.Team;
            var p = session.PlayerByTeam(player.Team);
            if (p is not null) p.Score = st.Moves; // record solving move count
            return MoveResult.GameOver;
        }
        return MoveResult.Accepted;
    }

    // QC bd_move_dozer: step the dozer; if a boulder is ahead, push it (the cell two ahead must be free and
    // not a brick); the destination cell must not be a brick. Returns true if the dozer moved.
    private static bool MoveDozer(BdState st)
    {
        var (dx, dy) = DirDelta(st.DozerDir);
        int nx = st.DozerX + dx, ny = st.DozerY + dy;
        if (!BdState.InBounds(nx, ny)) return false;

        BdTile hit = st.Solid[nx, ny];

        if (IsBrick(hit) || hit == BdTile.Dozer) return false; // bricks block (dozer-on-dozer can't happen)

        if (hit == BdTile.Boulder)
        {
            int bx = nx + dx, by = ny + dy;
            if (!BdState.InBounds(bx, by)) return false;
            BdTile beyond = st.Solid[bx, by];
            if (beyond != BdTile.Empty) return false; // boulder can't be pushed into anything occupied
            // push the boulder.
            st.Solid[bx, by] = BdTile.Boulder;
            st.Solid[nx, ny] = BdTile.Empty;
        }
        else if (hit != BdTile.Empty)
        {
            return false; // any other non-empty (e.g. another dozer) blocks
        }

        // move the dozer (targets are separate, so the dozer/boulder can sit on a target cell).
        st.Solid[st.DozerX, st.DozerY] = BdTile.Empty;
        st.Solid[nx, ny] = BdTile.Dozer;
        st.DozerX = nx;
        st.DozerY = ny;
        return true;
    }

    // QC bd_check_winner: every target cell is covered by a boulder.
    private static bool CheckWinner(BdState st)
    {
        int total = 0, covered = 0;
        for (int x = 0; x < BdState.Size; x++)
        for (int y = 0; y < BdState.Size; y++)
            if (st.Target[x, y])
            {
                total++;
                if (st.Solid[x, y] == BdTile.Boulder) covered++;
            }
        return total > 0 && covered >= total;
    }

    // =====================================================================================
    //  Level loading / editing (in-memory; replaces QC's on-disk storage files)
    // =====================================================================================

    /// <summary>One placed level element (QC a saved piece line "&lt;tile&gt; &lt;type&gt; &lt;dir&gt;").</summary>
    public readonly record struct BdPlacement(int X, int Y, BdTile Tile, BdDir Dir = BdDir.Down);

    /// <summary>
    /// Load a level from an in-memory placement list (QC bd_load_level + bd_setup_pieces, sans file I/O).
    /// Clears the grid, then places each element; a Dozer placement also sets the dozer position/heading.
    /// </summary>
    public static void LoadLevel(MinigameSession session, IEnumerable<BdPlacement> placements)
    {
        var st = StateOf(session);
        st.Clear();
        foreach (var p in placements)
            Place(st, p.X, p.Y, p.Tile, p.Dir);
        session.TurnType = MinigameTurn.Play;
        session.Winner = 0;
        session.CurrentTeam = 1;
    }

    // QC bd_editor_place / bd_load_piece core: place a tile (Target coexists with Solid; Dozer tracks pos).
    private static void Place(BdState st, int x, int y, BdTile tile, BdDir dir)
    {
        if (!BdState.InBounds(x, y)) return;
        if (tile == BdTile.Target) { st.Target[x, y] = true; return; }
        if (tile == BdTile.Empty) { st.Solid[x, y] = BdTile.Empty; return; }
        st.Solid[x, y] = tile;
        if (tile == BdTile.Dozer)
        {
            st.DozerX = x;
            st.DozerY = y;
            st.DozerDir = dir;
        }
    }

    /// <summary>Enter the level editor (QC "edit"): pauses MOVE rules; placements use <see cref="EditorPlace"/>.</summary>
    public static void ActivateEditor(MinigameSession session)
    {
        var st = StateOf(session);
        st.Editing = true;
        st.Moves = 0;
        session.TurnType = MinigameTurn.Play;
    }

    /// <summary>Place/clear a tile in the editor (QC bd_editor_place). No-op outside the editor.</summary>
    public static void EditorPlace(MinigameSession session, int x, int y, BdTile tile, BdDir dir = BdDir.Down)
    {
        var st = StateOf(session);
        if (!st.Editing) return;
        Place(st, x, y, tile, dir);
    }

    /// <summary>Leave the editor back to play (QC "save"/bd_close_editor): requires a dozer to be placed.</summary>
    public static bool CloseEditor(MinigameSession session)
    {
        var st = StateOf(session);
        if (st.DozerX < 0) return false; // QC: need a bulldozer on the level
        st.Editing = false;
        session.TurnType = MinigameTurn.Play;
        return true;
    }

    /// <summary>Restart the current level (QC "restart"): reset the dozer/move count to the loaded state.</summary>
    public void RestartMatch(MinigameSession session)
    {
        // A full reload would need the original placements; here we just clear the move count and unstick the
        // win state, matching the visible effect (the host re-LoadLevels for a true reset).
        var st = StateOf(session);
        st.Moves = 0;
        session.TurnType = MinigameTurn.Play;
        session.Winner = 0;
    }

    public override MinigameTurn CheckEnd(MinigameSession session)
        => session.TurnType == MinigameTurn.Win ? MinigameTurn.Win : MinigameTurn.Play;
}
