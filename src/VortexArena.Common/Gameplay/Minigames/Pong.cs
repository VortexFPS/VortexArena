using System.Numerics;

namespace XonoticGodot.Common.Gameplay;

// Server-side state + physics for Pong — port of common/minigames/minigame/pong.qc (pong_server_event,
// pong_ball_think, pong_paddle_think, pong_paddle_hit/bounce, pong_goal, pong_add_score). Up to 4 players,
// each owning one paddle on a side of a unit-square board [0,1]^2; one or more balls bounce around; missing
// a ball scores against the receiving side. This is real-time, not turn-based, so the rules live in a
// PongState advanced by Tick(dt) rather than in Minigame.Move.
//
// Geometry (QC pong_team_to_paddlepos / pong_team_to_box_halfsize):
//   team 1 -> right  wall (x≈0.99), paddle is vertical,   moves along y
//   team 2 -> left   wall (x≈0.01), paddle is vertical,   moves along y
//   team 3 -> bottom wall (y≈0.01), paddle is horizontal, moves along x
//   team 4 -> top    wall (y≈0.99), paddle is horizontal, moves along x
//
// Includes the AI paddle controller (pong_ai_think): an AI paddle tracks the nearest approaching ball,
// predicts its position one think-step ahead, and drives toward it within a tolerance. Omitted vs QC:
// CSQC rendering + networking only.
// A deterministic RNG is injected (Func<float> 0..1) so ball throws stay reproducible without depending on
// the engine; defaults to System.Random.

/// <summary>Direction a paddle is being driven this tick (QC PONG_KEY_* bitset, decoded).</summary>
public enum PongMove
{
    None = 0,
    /// <summary>Move toward higher coordinate (down/right). QC PONG_KEY_INCREASE.</summary>
    Increase = 1,
    /// <summary>Move toward lower coordinate (up/left). QC PONG_KEY_DECREASE.</summary>
    Decrease = 2,
}

/// <summary>A paddle — successor to QC's <c>pong_paddle</c> entity. Position is the centre in board space.</summary>
public sealed class PongPaddle
{
    public int Team;
    public Vector2 Origin;
    /// <summary>Half-length of the paddle along its travel axis (QC <c>pong_length</c> = paddle_size).</summary>
    public float Length;
    /// <summary>Current drive input (set by player/AI each tick).</summary>
    public PongMove Input;
    /// <summary>True if this paddle is AI-controlled (QC: realowner.classname == "pong_ai").</summary>
    public bool IsAi;
    /// <summary>Seconds until the next AI decision (QC pong_ai_think's <c>nextthink</c> countdown). The AI
    /// re-decides its drive only every sv_minigames_pong_ai_thinkspeed and HOLDS <see cref="Input"/> between
    /// decisions, like pong_ai_think setting <c>pong_keys</c> on its own think while pong_paddle_think moves
    /// every sys_ticrate. Starts at 0 so the first tick decides immediately (QC pong_ai_spawn: nextthink=time).</summary>
    public float AiThinkTimer;

    public PongPaddle(int team, Vector2 origin, float length)
    {
        Team = team;
        Origin = origin;
        Length = length;
    }

    /// <summary>AABB half-extents in board space (QC pong_team_to_box_halfsize): long axis = Length, short = 1/16.</summary>
    public Vector2 HalfSize => Team > 2
        ? new Vector2(Length, 1f / 16f)   // horizontal paddle (teams 3,4)
        : new Vector2(1f / 16f, Length);  // vertical paddle   (teams 1,2)

    public Vector2 Min => Origin - HalfSize;
    public Vector2 Max => Origin + HalfSize;
}

/// <summary>A ball — successor to QC's <c>pong_ball</c> entity.</summary>
public sealed class PongBall
{
    public Vector2 Origin = new(0.5f, 0.5f);
    public Vector2 Velocity;
    /// <summary>Radius in board space (QC <c>pong_length</c> = ball_radius).</summary>
    public float Radius;
    /// <summary>Last team to touch the ball (the "thrower"); 0 = none. QC ball.team.</summary>
    public int LastTouch;
    /// <summary>Seconds until the ball is thrown again after a reset/goal (QC nextthink delay).</summary>
    public float ThrowTimer;
    public bool InPlay;
}

/// <summary>
/// Whole Pong simulation for one session — paddles, balls, scores, tunables. Stored in
/// <see cref="MinigameSession.Extra"/> under "pong" by <see cref="Pong"/>. Call <see cref="Throw"/> to start
/// a round, then <see cref="Tick"/> each frame.
/// </summary>
public sealed class PongState
{
    public const int MaxPlayers = 4; // QC PONG_MAX_PLAYERS

    // tunables (QC autocvar_sv_minigames_pong_*); defaults from the shipped cfg.
    public float PaddleSize = 0.3f;
    public float PaddleSpeed = 1f;
    public float BallWait = 1f;
    public float BallSpeed = 1f;
    public float BallRadius = 0.03125f;
    public int BallNumber = 1;

    // AI tunables (QC autocvar_sv_minigames_pong_ai_*); defaults from the shipped minigames.cfg.
    public float AiThinkSpeed = 0.1f;   // seconds per AI decision (QC sv_minigames_pong_ai_thinkspeed = 0.1)
    public float AiTolerance = 0.33f;   // dead-zone multiplier so the AI doesn't jitter (QC ..._ai_tolerance = 0.33)

    public readonly PongPaddle?[] Paddles = new PongPaddle?[MaxPlayers];
    public readonly List<PongBall> Balls = new();
    public readonly int[] Scores = new int[MaxPlayers]; // by team-1

    public bool Playing;

    private readonly Func<float> _rng;

    public PongState(Func<float>? rng = null)
    {
        var r = new Random();
        _rng = rng ?? (() => (float)r.NextDouble());
    }

    /// <summary>Centre position of a team's paddle (QC pong_team_to_paddlepos).</summary>
    public static Vector2 PaddlePos(int team) => team switch
    {
        1 => new Vector2(0.99f, 0.5f),
        2 => new Vector2(0.01f, 0.5f),
        3 => new Vector2(0.5f, 0.01f),
        4 => new Vector2(0.5f, 0.99f),
        _ => Vector2.Zero,
    };

    /// <summary>Seat a paddle for a team (QC pong_paddle_spawn). team is 1-based.</summary>
    public PongPaddle SpawnPaddle(int team, bool ai = false)
    {
        var paddle = new PongPaddle(team, PaddlePos(team), PaddleSize) { IsAi = ai };
        Paddles[team - 1] = paddle;
        return paddle;
    }

    /// <summary>First free paddle slot (1-based team), or 0 if full. QC join loop.</summary>
    public int FirstFreeSlot()
    {
        for (int i = 0; i < MaxPlayers; i++)
            if (Paddles[i] is null) return i + 1;
        return 0;
    }

    /// <summary>Begin a round: spawn the configured number of balls and reset them (QC "throw").</summary>
    public void Throw()
    {
        if (Playing) return;
        Playing = true;
        Balls.Clear();
        for (int i = 0; i < System.Math.Max(1, BallNumber); i++)
        {
            var ball = new PongBall { Radius = BallRadius };
            ResetBall(ball);
            Balls.Add(ball);
        }
    }

    // QC pong_ball_reset: park at centre, schedule a throw after BallWait.
    private void ResetBall(PongBall ball)
    {
        ball.Velocity = Vector2.Zero;
        ball.Origin = new Vector2(0.5f, 0.5f);
        ball.LastTouch = 0;
        ball.InPlay = false;
        ball.ThrowTimer = BallWait;
    }

    // QC pong_ball_throw: launch in a random direction not too close to either axis.
    private void ThrowBall(PongBall ball)
    {
        float angle;
        do
        {
            angle = _rng() * (2f * MathF.PI);
        }
        while (MathF.Abs(MathF.Sin(angle)) < 0.17f || MathF.Abs(MathF.Cos(angle)) < 0.17f);

        ball.Velocity = new Vector2(MathF.Cos(angle) * BallSpeed, MathF.Sin(angle) * BallSpeed);
        ball.LastTouch = 0;
        ball.InPlay = true;
    }

    /// <summary>Advance the simulation by <paramref name="dt"/> seconds. Drives AI inputs, paddles and balls.</summary>
    public void Tick(float dt)
    {
        if (!Playing) return;

        // AI paddles re-decide their drive on their OWN think schedule (QC pong_ai_think: nextthink =
        // time + thinkspeed), not every frame — pong_paddle_think then moves them every sys_ticrate using the
        // last decided pong_keys. Mirror that: count down each AI paddle's think timer by dt and only re-run
        // AiDecide when it elapses, HOLDING the previous Input between decisions (so the AI isn't twitchier than
        // Base when sys_ticrate < thinkspeed). Total displacement is unchanged either way; only the decision
        // frequency differs.
        for (int t = 1; t <= MaxPlayers; t++)
        {
            var p = Paddles[t - 1];
            if (p is not { IsAi: true }) continue;
            p.AiThinkTimer -= dt;
            if (p.AiThinkTimer <= 0f)
            {
                p.Input = AiDecide(p);
                p.AiThinkTimer += AiThinkSpeed; // QC: nextthink = time + thinkspeed (carry the remainder)
            }
        }

        // paddles (QC pong_paddle_think): move along the paddle's axis, clamped to the board.
        foreach (var paddle in Paddles)
        {
            if (paddle is null || paddle.Input == PongMove.None) continue;
            float move = PaddleSpeed * dt * (paddle.Input == PongMove.Decrease ? -1f : 1f);
            float half = paddle.Length / 2f;
            if (paddle.Team > 2)
                paddle.Origin.X = System.Math.Clamp(paddle.Origin.X + move, half, 1f - half);
            else
                paddle.Origin.Y = System.Math.Clamp(paddle.Origin.Y + move, half, 1f - half);
        }

        // balls (QC pong_ball_think)
        foreach (var ball in Balls)
        {
            if (!ball.InPlay)
            {
                ball.ThrowTimer -= dt;
                if (ball.ThrowTimer <= 0f) ThrowBall(ball);
                continue;
            }

            ball.Origin += ball.Velocity * dt;

            // paddle collisions first (QC checks all paddles, bounces off the first hit)
            bool bounced = false;
            for (int t = 1; t <= MaxPlayers; t++)
            {
                if (PaddleHit(ball, t))
                {
                    PaddleBounce(ball, t);
                    ball.LastTouch = t;
                    bounced = true;
                    break;
                }
            }
            if (bounced) continue;

            // walls / goals: bottom(3), top(4), left(2), right(1)
            if (ball.Origin.Y <= ball.Radius)
            {
                if (!Goal(ball, 3)) { ball.Origin.Y = ball.Radius; ball.Velocity.Y *= -1; }
            }
            else if (ball.Origin.Y >= 1f - ball.Radius)
            {
                if (!Goal(ball, 4)) { ball.Origin.Y = 1f - ball.Radius; ball.Velocity.Y *= -1; }
            }

            if (ball.Origin.X <= ball.Radius)
            {
                if (!Goal(ball, 2)) { ball.Origin.X = ball.Radius; ball.Velocity.X *= -1; }
            }
            else if (ball.Origin.X >= 1f - ball.Radius)
            {
                if (!Goal(ball, 1)) { ball.Origin.X = 1f - ball.Radius; ball.Velocity.X *= -1; }
            }
        }
    }

    // QC box_nearest + pong_paddle_hit: nearest point on the paddle AABB within ball radius.
    private bool PaddleHit(PongBall ball, int team)
    {
        var paddle = Paddles[team - 1];
        if (paddle is null) return false;
        Vector2 min = paddle.Min, max = paddle.Max;
        Vector2 near = new(
            System.Math.Clamp(ball.Origin.X, min.X, max.X),
            System.Math.Clamp(ball.Origin.Y, min.Y, max.Y));
        return (near - ball.Origin).Length() <= ball.Radius;
    }

    // QC pong_paddle_bounce: reflect away from the paddle's wall, then jitter the angle a little.
    private void PaddleBounce(PongBall ball, int team)
    {
        switch (team)
        {
            case 1: ball.Velocity.X = -MathF.Abs(ball.Velocity.X); break; // right wall -> go left
            case 2: ball.Velocity.X = MathF.Abs(ball.Velocity.X); break;  // left wall  -> go right
            case 3: ball.Velocity.Y = MathF.Abs(ball.Velocity.Y); break;  // bottom     -> go up
            case 4: ball.Velocity.Y = -MathF.Abs(ball.Velocity.Y); break; // top        -> go down
        }

        float angle = MathF.Atan2(ball.Velocity.Y, ball.Velocity.X);
        angle += (_rng() - 0.5f) * 2f * (MathF.PI / 6f);
        float speed = ball.Velocity.Length();
        ball.Velocity = new Vector2(speed * MathF.Cos(angle), speed * MathF.Sin(angle));
    }

    // QC pong_goal: if the side has a paddle but it didn't catch the ball, score and reset.
    private bool Goal(PongBall ball, int team)
    {
        var paddle = Paddles[team - 1];
        if (paddle is null) return false;       // open side: ball just bounces (QC returns false)
        if (PaddleHit(ball, team)) return false; // caught -> not a goal

        AddScore(ball.LastTouch, team, 1);
        ResetBall(ball);
        return true;
    }

    // QC pong_add_score: a goal credits the thrower; an own-goal (thrower == receiver, or no thrower)
    // subtracts from the receiver instead.
    private void AddScore(int thrower, int receiver, int delta)
    {
        if (thrower == 0) thrower = receiver;
        if (thrower == receiver) delta = -delta;
        int idx = thrower - 1;
        if (idx >= 0 && idx < MaxPlayers) Scores[idx] += delta;
    }

    /// <summary>
    /// Decide an AI paddle's drive input — a port of QC <c>pong_ai_think</c>. Finds the nearest ball that is
    /// either close (&lt; 0.5) or approaching (its next-step distance is smaller), targets that ball's
    /// predicted coordinate one think-step ahead on the paddle's travel axis, and moves toward it once the
    /// gap exceeds a tolerance dead-zone (so it doesn't jitter on top of the ball).
    /// </summary>
    public PongMove AiDecide(PongPaddle paddle)
    {
        float thinkSpeed = AiThinkSpeed;
        bool vertical = paddle.Team <= 2; // teams 1,2 move along y; teams 3,4 along x

        // pick the nearest relevant ball (QC: distance < min && (distance < 0.5 || next < distance)).
        PongBall? chosen = null;
        float minDistance = 1f;
        foreach (var ball in Balls)
        {
            if (!ball.InPlay) continue;
            float distance = (ball.Origin - paddle.Origin).Length();
            float nextDistance = (ball.Origin + ball.Velocity - paddle.Origin).Length();
            if (distance < minDistance && (distance < 0.5f || nextDistance < distance))
            {
                minDistance = distance;
                chosen = ball;
            }
        }

        // target the predicted coordinate (QC: ball.origin + ball.velocity*thinkspeed) on the paddle axis.
        float target = 0.5f;
        float myPos = vertical ? paddle.Origin.Y : paddle.Origin.X;
        if (chosen is not null)
            target = vertical
                ? chosen.Origin.Y + chosen.Velocity.Y * thinkSpeed
                : chosen.Origin.X + chosen.Velocity.X * thinkSpeed;

        // dead-zone (QC: pong_length/2 * tolerance + paddle_speed * thinkspeed).
        float dead = paddle.Length / 2f * AiTolerance + PaddleSpeed * thinkSpeed;

        if (target < myPos - dead) return PongMove.Decrease;
        if (target > myPos + dead) return PongMove.Increase;
        return PongMove.None;
    }
}

/// <summary>
/// Pong game descriptor. Real-time: <see cref="Move"/> only handles the discrete commands (throw / drive a
/// paddle); the continuous simulation runs via <see cref="Tick"/> on the <see cref="PongState"/> stored in
/// the session's <see cref="MinigameSession.Extra"/> bag under "pong".
/// </summary>
public sealed class Pong : Minigame
{
    public const string StateKey = "pong";

    public override string NetName => "pong";
    public override string DisplayName => "Pong";
    public override int MaxPlayers => PongState.MaxPlayers;

    public override MinigameSession CreateSession()
    {
        var s = new MinigameSession(this);
        Start(s);
        return s;
    }

    // QC "start": status WAIT.
    public override int Start(MinigameSession session)
    {
        session.TurnType = MinigameTurn.Waiting;
        session.CurrentTeam = 0;
        session.Winner = 0;
        session.Extra[StateKey] = new PongState();
        return 1;
    }

    /// <summary>The Pong sub-state for a session (null if not a Pong session).</summary>
    public static PongState? StateOf(MinigameSession session)
        => session.Extra.TryGetValue(StateKey, out var o) ? o as PongState : null;

    // QC "join": can't join once playing; otherwise take the first free paddle slot.
    public override int Join(MinigameSession session, MinigamePlayer player)
    {
        var st = StateOf(session);
        if (st is null || st.Playing) return MinigameSession.SpectatorTeam;
        int slot = st.FirstFreeSlot();
        if (slot == 0) return MinigameSession.SpectatorTeam;
        st.SpawnPaddle(slot);
        return slot;
    }

    // QC "part": when a player leaves a paddle, replace them with an AI (pong_ai_spawn) keeping their score.
    public override void Part(MinigameSession session, MinigamePlayer player)
    {
        var st = StateOf(session);
        if (st is not null && player.Team >= 1 && player.Team <= PongState.MaxPlayers)
        {
            var paddle = st.Paddles[player.Team - 1];
            if (paddle is not null) paddle.IsAi = true; // AI now drives this paddle via PongState.AiDecide
        }
        session.Players.Remove(player);
    }

    /// <summary>
    /// Discrete commands (QC "cmd"): "throw" starts the round; "move &lt;n&gt;" sets a paddle's drive from a
    /// PONG_KEY bitmask; "+movei"/"-movei"/"+moved"/"-moved" toggle the increase/decrease inputs.
    /// </summary>
    public override MoveResult Move(MinigameSession session, MinigamePlayer player, string move)
    {
        var st = StateOf(session);
        if (st is null || player.Team == MinigameSession.SpectatorTeam) return MoveResult.Invalid;

        var parts = move.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return MoveResult.Invalid;
        var paddle = (player.Team >= 1 && player.Team <= PongState.MaxPlayers) ? st.Paddles[player.Team - 1] : null;

        switch (parts[0])
        {
            case "throw":
                if (!st.Playing)
                {
                    st.Throw();
                    session.TurnType = MinigameTurn.Play;
                }
                return MoveResult.Accepted;
            case "move":
                if (paddle is not null && parts.Length >= 2 && int.TryParse(parts[1], out int keys))
                    paddle.Input = DecodeKeys(keys);
                return MoveResult.Accepted;
            case "+movei": if (paddle is not null) paddle.Input = Add(paddle.Input, PongMove.Increase); return MoveResult.Accepted;
            case "-movei": if (paddle is not null) paddle.Input = Remove(paddle.Input, PongMove.Increase); return MoveResult.Accepted;
            case "+moved": if (paddle is not null) paddle.Input = Add(paddle.Input, PongMove.Decrease); return MoveResult.Accepted;
            case "-moved": if (paddle is not null) paddle.Input = Remove(paddle.Input, PongMove.Decrease); return MoveResult.Accepted;
            case "pong_aimore": return AddAi(st) ? MoveResult.Accepted : MoveResult.Invalid;
            case "pong_ailess": return RemoveAi(st) ? MoveResult.Accepted : MoveResult.Invalid;
            default: return MoveResult.Invalid;
        }
    }

    // QC "pong_aimore": while waiting, seat an AI paddle on the first free slot.
    private static bool AddAi(PongState st)
    {
        if (st.Playing) return false;
        int slot = st.FirstFreeSlot();
        if (slot == 0) return false;
        st.SpawnPaddle(slot, ai: true);
        return true;
    }

    // QC "pong_ailess": while waiting, remove the highest-slot AI paddle.
    private static bool RemoveAi(PongState st)
    {
        if (st.Playing) return false;
        for (int j = PongState.MaxPlayers - 1; j >= 0; j--)
        {
            var paddle = st.Paddles[j];
            if (paddle is { IsAi: true })
            {
                st.Paddles[j] = null;
                return true;
            }
        }
        return false;
    }

    /// <summary>Advance the session's Pong simulation (call each server frame). No-op for non-Pong sessions.</summary>
    public static void Tick(MinigameSession session, float dt) => StateOf(session)?.Tick(dt);

    // QC PONG_KEY bitset (1=increase, 2=decrease, 3=both -> cancels to None per pong_paddle_think).
    private static PongMove DecodeKeys(int keys) => (keys & 0x3) switch
    {
        0x1 => PongMove.Increase,
        0x2 => PongMove.Decrease,
        _ => PongMove.None, // 0 or both
    };

    private static PongMove Add(PongMove cur, PongMove flag) => DecodeKeys((int)cur | (int)flag);
    private static PongMove Remove(PongMove cur, PongMove flag) => DecodeKeys((int)cur & ~(int)flag);

    // AI paddle controller (pong_ai_think) lives in PongState.AiDecide and runs each Tick; add/remove-AI
    // commands are handled above (pong_aimore/pong_ailess). CSQC rendering + entity networking are the only
    // remaining QC pieces and are out of scope for the server-side port.
}
