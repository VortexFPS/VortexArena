namespace XonoticGodot.Common.Gameplay;

// Snake — a single-player grid game. NOTE: Xonotic ships no Snake minigame (the QC set is nmm/ttt/c4/pong/
// ps/pp/bd); this is an ORIGINAL implementation written against the same MinigameSession framework as the
// ported games, satisfying the "grid + growth + collision" deliverable. It is deliberately faithful to the
// *framework conventions* (tile-less integer grid here for clarity, real-time stepping like Pong) rather
// than to any QC source. No CSQC/network code (none exists to port).
//
// Rules: the snake occupies a queue of cells on a Columns x Rows grid and advances one cell per step in its
// current heading. Eating the food cell grows it by one and spawns new food. Running into a wall or its own
// body ends the game (Draw == "game over" with a final Score == food eaten). The player issues "up"/"down"/
// "left"/"right" to change heading (a 180° reversal is ignored, as is conventional).

/// <summary>A grid cell (column x, row y). Row 0 is the bottom, matching the rest of the framework.</summary>
public readonly record struct GridPos(int X, int Y)
{
    public static GridPos operator +(GridPos a, GridPos b) => new(a.X + b.X, a.Y + b.Y);
}

/// <summary>Heading of the snake. The vector points in (dx, dy) grid units.</summary>
public enum SnakeDir { Up, Down, Left, Right }

/// <summary>
/// The Snake simulation for one session — stored in <see cref="MinigameSession.Extra"/> under "snake".
/// Advance it with <see cref="Step"/> (one grid cell) or <see cref="Tick"/> (accumulates real time at
/// <see cref="StepInterval"/>).
/// </summary>
public sealed class SnakeState
{
    public int Columns { get; }
    public int Rows { get; }

    /// <summary>Body cells, head last. The whole snake including the head.</summary>
    public List<GridPos> Body { get; } = new();

    public GridPos Food { get; private set; }
    public SnakeDir Heading { get; private set; } = SnakeDir.Right;
    public bool Alive { get; private set; } = true;
    public int Score { get; private set; }

    /// <summary>Seconds between automatic steps when driven by <see cref="Tick"/>.</summary>
    public float StepInterval = 0.15f;
    private float _accum;

    private SnakeDir _pendingHeading = SnakeDir.Right;
    private readonly Func<int, int> _rng; // rng(n) -> [0,n)

    public SnakeState(int columns, int rows, Func<int, int>? rng = null)
    {
        Columns = columns;
        Rows = rows;
        var r = new Random();
        _rng = rng ?? (n => r.Next(n));
        Reset();
    }

    public GridPos Head => Body[^1];

    /// <summary>Start a new game: a length-3 snake centred, heading right, with one food placed.</summary>
    public void Reset()
    {
        Body.Clear();
        int cy = Rows / 2;
        int cx = Columns / 2;
        // tail -> head, so the snake faces right
        Body.Add(new GridPos(cx - 1, cy));
        Body.Add(new GridPos(cx, cy));
        Body.Add(new GridPos(cx + 1, cy));
        Heading = SnakeDir.Right;
        _pendingHeading = SnakeDir.Right;
        Alive = true;
        Score = 0;
        _accum = 0f;
        PlaceFood();
    }

    private static GridPos Delta(SnakeDir d) => d switch
    {
        SnakeDir.Up => new GridPos(0, 1),
        SnakeDir.Down => new GridPos(0, -1),
        SnakeDir.Left => new GridPos(-1, 0),
        _ => new GridPos(1, 0),
    };

    private static bool Opposite(SnakeDir a, SnakeDir b) =>
        (a == SnakeDir.Up && b == SnakeDir.Down) ||
        (a == SnakeDir.Down && b == SnakeDir.Up) ||
        (a == SnakeDir.Left && b == SnakeDir.Right) ||
        (a == SnakeDir.Right && b == SnakeDir.Left);

    /// <summary>Queue a heading change for the next step. A direct reversal is ignored.</summary>
    public void SetHeading(SnakeDir dir)
    {
        if (!Opposite(dir, Heading)) _pendingHeading = dir;
    }

    /// <summary>Advance one grid cell. Returns false once the snake has died (further steps are no-ops).</summary>
    public bool Step()
    {
        if (!Alive) return false;

        Heading = _pendingHeading;
        GridPos next = Head + Delta(Heading);

        // wall collision
        if (next.X < 0 || next.X >= Columns || next.Y < 0 || next.Y >= Rows)
        {
            Alive = false;
            return false;
        }

        bool eating = next == Food;

        // self collision — when not eating, the tail cell is about to vacate, so it doesn't count.
        int startIdx = eating ? 0 : 1;
        for (int i = startIdx; i < Body.Count; i++)
        {
            if (Body[i] == next)
            {
                Alive = false;
                return false;
            }
        }

        Body.Add(next); // advance head
        if (eating)
        {
            Score++;
            PlaceFood();
        }
        else
        {
            Body.RemoveAt(0); // move tail forward (constant length)
        }
        return true;
    }

    /// <summary>Accumulate real time and step whenever a full <see cref="StepInterval"/> has elapsed.</summary>
    public void Tick(float dt)
    {
        if (!Alive) return;
        _accum += dt;
        while (_accum >= StepInterval && Alive)
        {
            _accum -= StepInterval;
            Step();
        }
    }

    // Place food on a random unoccupied cell. If the board is full (a perfect win) leave food on the head.
    private void PlaceFood()
    {
        int free = Columns * Rows - Body.Count;
        if (free <= 0) { Food = Head; return; }

        int target = _rng(free);
        int seen = 0;
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
        {
            var p = new GridPos(x, y);
            if (Occupies(p)) continue;
            if (seen == target) { Food = p; return; }
            seen++;
        }
    }

    /// <summary>Does the snake's body occupy this cell?</summary>
    public bool Occupies(GridPos p)
    {
        for (int i = 0; i < Body.Count; i++)
            if (Body[i] == p) return true;
        return false;
    }
}

/// <summary>
/// Snake game descriptor. Single player, real-time. <see cref="Move"/> handles heading commands; the
/// simulation advances via <see cref="Tick"/>/<see cref="Step"/> on the <see cref="SnakeState"/> stored in
/// the session's <see cref="MinigameSession.Extra"/> bag under "snake".
/// </summary>
public sealed class Snake : Minigame
{
    public const string StateKey = "snake";
    public const int DefaultColumns = 16;
    public const int DefaultRows = 16;

    public override string NetName => "snake";
    public override string DisplayName => "Snake";
    public override int MaxPlayers => 1;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this);
        Start(s);
        return s;
    }

    public override int Start(MinigameSession session)
    {
        session.TurnType = MinigameTurn.Play;
        session.CurrentTeam = 1;
        session.Winner = 0;
        session.Extra[StateKey] = new SnakeState(DefaultColumns, DefaultRows);
        return 1;
    }

    /// <summary>The Snake sub-state for a session (null if not a Snake session).</summary>
    public static SnakeState? StateOf(MinigameSession session)
        => session.Extra.TryGetValue(StateKey, out var o) ? o as SnakeState : null;

    public override int Join(MinigameSession session, MinigamePlayer player)
        => session.PlayerCount >= 1 ? MinigameSession.SpectatorTeam : 1;

    /// <summary>Heading command: "up" / "down" / "left" / "right".</summary>
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var st = StateOf(session);
        if (st is null || !st.Alive) return MoveResult.Invalid;

        switch (move.Trim().ToLowerInvariant())
        {
            case "up": st.SetHeading(SnakeDir.Up); return MoveResult.Accepted;
            case "down": st.SetHeading(SnakeDir.Down); return MoveResult.Accepted;
            case "left": st.SetHeading(SnakeDir.Left); return MoveResult.Accepted;
            case "right": st.SetHeading(SnakeDir.Right); return MoveResult.Accepted;
            default: return MoveResult.Invalid;
        }
    }

    /// <summary>Advance the session's Snake simulation; flips the session to game-over on death.</summary>
    public static void Tick(MinigameSession session, float dt)
    {
        var st = StateOf(session);
        if (st is null) return;
        bool wasAlive = st.Alive;
        st.Tick(dt);
        if (wasAlive && !st.Alive) EndGame(session, st);
    }

    /// <summary>Advance exactly one grid cell; flips the session to game-over on death.</summary>
    public static void Step(MinigameSession session)
    {
        var st = StateOf(session);
        if (st is null) return;
        bool wasAlive = st.Alive;
        st.Step();
        if (wasAlive && !st.Alive) EndGame(session, st);
    }

    private static void EndGame(MinigameSession session, SnakeState st)
    {
        // Single-player survival: "game over" maps to Draw (no opponent to beat); Score holds food eaten.
        session.TurnType = MinigameTurn.Draw;
        session.CurrentTeam = 0;
        var p = session.PlayerByTeam(1);
        if (p is not null) p.Score = st.Score;
    }

    public override MinigameTurn CheckEnd(MinigameSession session)
        => StateOf(session) is { Alive: false } ? MinigameTurn.Draw : MinigameTurn.Play;
}
