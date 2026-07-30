using System;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Hud;

/// <summary>
/// Draws an active minigame board and routes board clicks back as moves — the Godot successor to CSQC's
/// <c>cl_minigames</c> board draw (Base/.../qcsrc/common/minigames/cl_minigames.qc + each game's
/// <c>&lt;id&gt;_hud_board</c>/<c>_hud_status</c>). The server runs the rules (the ported <see cref="Minigame"/>
/// framework); this renders the networked <see cref="MinigameSession"/> — the board square, each occupied
/// tile's team piece, the win-line glow, and a status line (whose turn / winner / draw) — and turns a click on
/// an empty tile into a move command (<see cref="OnMove"/>), the client→server <c>minigame_cmd</c>.
///
/// Tile geometry matches the QC framework exactly (minigames.qc): a tile id is "&lt;letter&gt;&lt;number&gt;"
/// with letter = column (a=0) and number = the 1-based row counting from the BOTTOM. The board is laid out as
/// a centered square (QC <c>minigame_hud_fitsqare</c>); row 0 draws at the bottom.
///
/// Art (board + <c>piece&lt;team&gt;</c> + <c>winglow</c>) resolves the real Xonotic minigame pics from the
/// mounted game data via <see cref="TextureCache"/> (<c>gfx/hud/&lt;skin&gt;/minigames/&lt;game&gt;/…</c>), with
/// colored-token fallbacks. <see cref="MouseFilterEnum.Stop"/> so the board captures clicks while shown.
/// </summary>
public partial class MinigameRenderer : Control
{
    /// <summary>The session being rendered (null = no active minigame → nothing drawn, input ignored).</summary>
    public MinigameSession? Session { get; private set; }

    /// <summary>Raised when the local player clicks a board tile (the move id, e.g. "b2"); the net layer sends it.</summary>
    public event Action<string>? OnMove;

    /// <summary>The HUD skin whose minigame art is preferred; default-skin is the fallback.</summary>
    public string HudSkin { get; set; } = "luma";

    /// <summary>The local player's team (so it only submits moves on the player's turn). 0 = allow any.</summary>
    public int LocalTeam { get; set; }

    private double _time;

    public override void _Ready()
    {
        // Fill the screen; the board draws centered. Capture clicks only while a session is shown.
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    /// <summary>Show a session's board (QC: the player activated a minigame). Pass null to hide.</summary>
    public void Show(MinigameSession? session)
    {
        Session = session;
        Visible = session is not null;
        MouseFilter = session is not null ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (Visible) QueueRedraw(); // animate the turn pulse / win glow
    }

    // =====================================================================================
    //  Layout
    // =====================================================================================

    /// <summary>The centered board square in screen pixels (QC minigame_hud_fitsqare).</summary>
    private Rect2 BoardSquare()
    {
        Vector2 vp = Size;
        float side = Mathf.Min(vp.X, vp.Y) * 0.7f;
        return new Rect2((vp.X - side) * 0.5f, (vp.Y - side) * 0.5f, side, side);
    }

    /// <summary>The pixel rect of a tile (letter,number) within the board square; row 0 is the bottom row.</summary>
    private Rect2 CellRect(Rect2 board, int columns, int rows, int letter, int number)
    {
        float cw = board.Size.X / columns;
        float ch = board.Size.Y / rows;
        float x = board.Position.X + letter * cw;
        float y = board.Position.Y + (rows - 1 - number) * ch; // number counts from the bottom
        return new Rect2(x + cw * 0.06f, y + ch * 0.06f, cw * 0.88f, ch * 0.88f);
    }

    // =====================================================================================
    //  Draw
    // =====================================================================================

    public override void _Draw()
    {
        if (Session is not { } s) return;

        // Dim the world behind the board so it reads as a focused overlay.
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0f, 0f, 0f, 0.35f));

        Rect2 board = BoardSquare();
        string game = s.Game.NetName;

        if (s.Board is { } grid)
            DrawGrid(s, grid, board, game);
        else
            DrawBespoke(s, board); // Pong et al. — bespoke state, status-only here

        DrawStatus(s, board);
    }

    private void DrawGrid(MinigameSession s, MinigameBoard grid, Rect2 board, string game)
    {
        // Board backdrop (the game's board pic), else a framed dark square + grid lines.
        Texture2D? boardPic = Art(game, "board");
        if (boardPic is not null)
            DrawTextureRect(boardPic, board, tile: false, Colors.White);
        else
            DrawBoardFallback(board, grid.Columns, grid.Rows);

        // Pieces.
        foreach (var kv in grid.Cells)
        {
            int letter = MinigameTiles.Letter(kv.Key);
            int number = MinigameTiles.Number(kv.Key);
            if (letter < 0 || number < 0) continue;
            Rect2 cell = CellRect(board, grid.Columns, grid.Rows, letter, number);
            DrawPiece(game, kv.Value.Team, cell, kv.Value.Flags);
        }
    }

    private void DrawPiece(string game, int team, Rect2 cell, int flags)
    {
        Texture2D? piece = Art(game, $"piece{team}");
        if (piece is not null)
        {
            DrawTextureRect(piece, cell, tile: false, Colors.White);
            // Win-line glow on winning pieces (QC winglow flagged pieces).
            if (flags != 0 && Art(game, "winglow") is { } glow)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin((float)_time * 6f);
                DrawTextureRect(glow, cell, tile: false, new Color(1f, 1f, 1f, pulse));
            }
            return;
        }

        // Fallback token: a team-colored disc (or ring for team 2) so the board reads without art.
        Color c = TeamColor(team);
        Vector2 center = cell.Position + cell.Size * 0.5f;
        float r = Mathf.Min(cell.Size.X, cell.Size.Y) * 0.38f;
        if (team == 2)
            DrawArc(center, r, 0f, Mathf.Tau, 32, c, 4f);
        else
            DrawCircle(center, r, c);
    }

    private void DrawBoardFallback(Rect2 board, int columns, int rows)
    {
        DrawRect(board, new Color(0.12f, 0.12f, 0.16f, 0.9f));
        var line = new Color(1f, 1f, 1f, 0.25f);
        for (int x = 1; x < columns; x++)
        {
            float px = board.Position.X + board.Size.X * x / columns;
            DrawLine(new Vector2(px, board.Position.Y), new Vector2(px, board.Position.Y + board.Size.Y), line, 2f);
        }
        for (int y = 1; y < rows; y++)
        {
            float py = board.Position.Y + board.Size.Y * y / rows;
            DrawLine(new Vector2(board.Position.X, py), new Vector2(board.Position.X + board.Size.X, py), line, 2f);
        }
        DrawRect(board, new Color(1f, 1f, 1f, 0.4f), filled: false, width: 2f);
    }

    private void DrawBespoke(MinigameSession s, Rect2 board)
    {
        // Real-time games (Pong, Snake) keep bespoke state in Session.Extra. Backdrop, then the per-game draw.
        Texture2D? boardPic = Art(s.Game.NetName, "board");
        if (boardPic is not null)
            DrawTextureRect(boardPic, board, tile: false, Colors.White);
        else
            DrawRect(board, new Color(0.08f, 0.1f, 0.12f, 0.9f));
        DrawRect(board, new Color(1f, 1f, 1f, 0.4f), filled: false, width: 2f);

        switch (s.Game.NetName)
        {
            case "pong": DrawPong(s, board); break;
            case "snake": DrawSnake(s, board); break;
        }
    }

    /// <summary>Draw the Pong paddles + balls + scores (board space [0,1]², y up) from the session state.</summary>
    private void DrawPong(MinigameSession s, Rect2 board)
    {
        PongState? st = Pong.StateOf(s);
        if (st is null) return;

        // board space (y up) → screen (y down).
        System.Func<System.Numerics.Vector2, Vector2> toScreen = b =>
            new Vector2(board.Position.X + b.X * board.Size.X, board.Position.Y + (1f - b.Y) * board.Size.Y);

        // center divider.
        DrawLine(new Vector2(board.Position.X + board.Size.X * 0.5f, board.Position.Y),
            new Vector2(board.Position.X + board.Size.X * 0.5f, board.Position.Y + board.Size.Y),
            new Color(1f, 1f, 1f, 0.15f), 1f);

        // paddles (a team-colored bar from the board-space AABB).
        foreach (PongPaddle? p in st.Paddles)
        {
            if (p is null) continue;
            Vector2 tl = toScreen(new System.Numerics.Vector2(p.Min.X, p.Max.Y)); // y flips → Max.Y is top
            Vector2 br = toScreen(new System.Numerics.Vector2(p.Max.X, p.Min.Y));
            DrawRect(new Rect2(tl, br - tl), TeamColor(p.Team));
        }

        // balls in play.
        foreach (PongBall ball in st.Balls)
        {
            if (!ball.InPlay) continue;
            DrawCircle(toScreen(ball.Origin), ball.Radius * board.Size.X, new Color(1f, 1f, 1f, 0.95f));
        }

        // per-team scores around the board edges (1 right, 2 left, 3 bottom, 4 top).
        for (int t = 1; t <= PongState.MaxPlayers; t++)
        {
            if (st.Paddles[t - 1] is null) continue;
            string txt = st.Scores[t - 1].ToString();
            Vector2 at = t switch
            {
                1 => new Vector2(board.Position.X + board.Size.X - 28f, board.Position.Y + board.Size.Y * 0.5f),
                2 => new Vector2(board.Position.X + 12f, board.Position.Y + board.Size.Y * 0.5f),
                3 => new Vector2(board.Position.X + board.Size.X * 0.5f, board.Position.Y + board.Size.Y - 26f),
                _ => new Vector2(board.Position.X + board.Size.X * 0.5f, board.Position.Y + 6f),
            };
            DrawString(ThemeDB.FallbackFont, at, txt, HorizontalAlignment.Left, -1f, 20, TeamColor(t));
        }
    }

    /// <summary>Draw the Snake body + food + score (grid space, row 0 bottom) from the session state.</summary>
    private void DrawSnake(MinigameSession s, Rect2 board)
    {
        SnakeState? st = Snake.StateOf(s);
        if (st is null) return;

        float cw = board.Size.X / st.Columns;
        float ch = board.Size.Y / st.Rows;
        Rect2 Cell(int x, int y, float inset) => new(
            board.Position.X + x * cw + inset, board.Position.Y + (st.Rows - 1 - y) * ch + inset,
            cw - 2 * inset, ch - 2 * inset);

        // food.
        DrawRect(Cell(st.Food.X, st.Food.Y, cw * 0.18f), new Color(1f, 0.3f, 0.3f));

        // body (head last → brighter).
        for (int i = 0; i < st.Body.Count; i++)
        {
            bool head = i == st.Body.Count - 1;
            Color c = head ? new Color(0.6f, 1f, 0.5f) : new Color(0.3f, 0.8f, 0.4f);
            DrawRect(Cell(st.Body[i].X, st.Body[i].Y, 1f), c);
        }

        // score.
        DrawString(ThemeDB.FallbackFont, new Vector2(board.Position.X + 6f, board.Position.Y + 20f),
            $"Score: {st.Score}", HorizontalAlignment.Left, -1f, 18, new Color(1f, 1f, 1f, 0.9f));
    }

    private void DrawStatus(MinigameSession s, Rect2 board)
    {
        string status = s.TurnType switch
        {
            MinigameTurn.Win => s.Winner > 0 ? $"Player {s.Winner} wins!" : "Game over",
            MinigameTurn.Draw => "Draw",
            MinigameTurn.Play => s.CurrentTeam > 0 ? $"Player {s.CurrentTeam}'s turn" : "",
            _ => "Waiting for players…",
        };
        if (status.Length == 0) return;

        Color c = s.TurnType == MinigameTurn.Win ? TeamColor(s.Winner)
                : s.TurnType == MinigameTurn.Play ? TeamColor(s.CurrentTeam)
                : new Color(1f, 1f, 1f, 0.9f);
        if (s.TurnType == MinigameTurn.Play)
            c.A = 0.6f + 0.4f * Mathf.Sin((float)_time * 4f); // pulse on the active turn

        var pos = new Vector2(board.Position.X, board.Position.Y - 30f);
        DrawString(ThemeDB.FallbackFont, pos + new Vector2(0f, 22f), status,
            HorizontalAlignment.Center, board.Size.X, 22, c);
    }

    // =====================================================================================
    //  Input — click a tile to move (client→server minigame_cmd)
    // =====================================================================================

    public override void _GuiInput(InputEvent @event)
    {
        if (Session is not { } s || s.Board is not { } grid)
            return;
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
            return;
        // Only act on our turn (QC: the move command is rejected server-side otherwise, but gate it here too).
        if (LocalTeam != 0 && (s.TurnType != MinigameTurn.Play || s.CurrentTeam != LocalTeam))
            return;

        Rect2 board = BoardSquare();
        if (!board.HasPoint(mb.Position))
            return;

        int letter = (int)((mb.Position.X - board.Position.X) / (board.Size.X / grid.Columns));
        int rowFromTop = (int)((mb.Position.Y - board.Position.Y) / (board.Size.Y / grid.Rows));
        int number = grid.Rows - 1 - rowFromTop; // back to bottom-counted row
        if (letter < 0 || letter >= grid.Columns || number < 0 || number >= grid.Rows)
            return;

        string tile = MinigameTiles.BuildName(letter, number);
        OnMove?.Invoke(tile);
        AcceptEvent();
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    /// <summary>Resolve a minigame art pic, preferred skin → default skin → project override.</summary>
    private Texture2D? Art(string game, string name)
        => TextureCache.GetFirst(
            $"gfx/hud/{HudSkin}/minigames/{game}/{name}",
            $"gfx/hud/default/minigames/{game}/{name}",
            $"res://art/hud/minigames/{game}/{name}.png");

    /// <summary>Xonotic team colors (team 1 red, 2 blue, 3 yellow, 4 pink); white for none.</summary>
    private static Color TeamColor(int team) => team switch
    {
        1 => new Color(1f, 0.3f, 0.3f),
        2 => new Color(0.3f, 0.5f, 1f),
        3 => new Color(1f, 0.9f, 0.3f),
        4 => new Color(1f, 0.4f, 0.9f),
        _ => new Color(0.9f, 0.9f, 0.9f),
    };
}
