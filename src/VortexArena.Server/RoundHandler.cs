using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The round phases — the C# successor to the implicit state the QC <c>round_handler</c> edict tracks via
/// its <c>wait</c>/<c>cnt</c> fields (server/round_handler.qc). Used by the round-based modes (CA, FreezeTag,
/// LMS, …) to gate respawns and resolve round wins.
/// </summary>
public enum RoundPhase
{
    /// <summary>Pre-game: before <see cref="RoundHandler.GameStartTime"/> (QC <c>time &lt; game_starttime</c>).</summary>
    Warmup = 0,
    /// <summary>Countdown to round start is ticking (QC <c>round_handler_CountdownRunning()</c>, cnt &gt; 0).</summary>
    Countdown,
    /// <summary>A round is live (QC <c>round_handler_IsRoundStarted()</c>, !wait &amp;&amp; !cnt).</summary>
    Round,
    /// <summary>The round resolved; waiting out the end-delay before the next countdown (QC <c>wait</c>).</summary>
    EndDelay,
}

/// <summary>
/// The round flow state machine — a faithful, Godot-free port of <c>round_handler_Think</c> /
/// <c>round_handler_Init</c> / <c>round_handler_Spawn</c> / <c>round_handler_Reset</c>
/// (server/round_handler.qc) plus the round-phase predicates from round_handler.qh. A round-based gametype
/// (ClanArena/FreezeTag/LMS) creates one of these via <see cref="Spawn"/>, supplying:
///   - <c>canRoundStart</c> — QC <c>canRoundStart</c>: enough players present on enough teams to begin;
///   - <c>canRoundEnd</c>   — QC <c>canRoundEnd</c>: the round's win condition is met (one team left, etc.);
///   - <c>onRoundStart</c>  — QC <c>roundStart</c>: arm the round (reset map, spawn players, mark round live).
///
/// <see cref="Think"/> is driven once per server frame from <see cref="GameWorld"/> (the QC edict's
/// per-frame think). It reproduces the QC cadence exactly: hold in Warmup until game start; run a
/// 1-second-granularity countdown while <c>canRoundStart</c> holds (resetting it if it stops holding); fire
/// <c>onRoundStart</c> at cnt==0 and arm the round timelimit; then poll <c>canRoundEnd</c> each frame and,
/// when it fires, wait out <see cref="EndDelay"/> before re-arming the next countdown.
///
/// Per-round map reset is now wired via <see cref="OnRoundReset"/> (QC <c>reset_map(false)</c> on the next
/// round), and the ROUNDS_PL score bump via <see cref="OnRoundCounted"/> — both fired by <see cref="Think"/>
/// and applied by <see cref="GameWorld"/>.
///
/// Deferred: the round timelimit's draw resolution and stalemate prevention, score networking, campaign bot
/// gating, and the FirstThink one-frame delay (we initialize start time eagerly instead).
/// </summary>
public sealed class RoundHandler
{
    /// <summary>QC defaults from <c>round_handler_Spawn</c>: Init(delay=5, count=5, round_timelimit=180).</summary>
    public const float DefaultEndDelay = 5f;
    public const float DefaultCountdown = 5f;
    public const float DefaultRoundTimeLimit = 180f;

    // ---- configuration (QC .delay / .count / .round_timelimit) ----

    /// <summary>QC <c>.delay</c>: seconds from a round's end to the next countdown's start.</summary>
    public float EndDelay { get; private set; } = DefaultEndDelay;

    /// <summary>QC <c>.count</c>: the countdown length in whole seconds (cnt starts at count+1).</summary>
    public int Countdown { get; private set; } = (int)DefaultCountdown;

    /// <summary>QC <c>.round_timelimit</c>: a round's own time limit (0 = none).</summary>
    public float RoundTimeLimit { get; private set; } = DefaultRoundTimeLimit;

    /// <summary>Absolute sim time the pre-game ends (QC <c>game_starttime</c>). Set by the host.</summary>
    public float GameStartTime { get; set; }

    /// <summary>True while the host wants the whole match frozen (QC <c>intermission_running</c>): the handler idles.</summary>
    public bool IntermissionRunning { get; set; }

    // ---- live state (QC .wait / .cnt / .round_endtime / globals) ----

    private bool _wait;        // QC .wait
    private int _cnt;          // QC .cnt (count+1 down to 0)
    private bool _started;     // true once Spawn() initialized us

    /// <summary>The current phase (derived from wait/cnt, like the QC round_handler_* macros).</summary>
    public RoundPhase Phase
    {
        get
        {
            if (!_started) return RoundPhase.Warmup;
            if (_wait) return RoundPhase.EndDelay;
            if (_cnt > 0) return RoundPhase.Countdown;
            return RoundPhase.Round;
        }
    }

    /// <summary>QC <c>round_handler_IsRoundStarted()</c>: a round is live right now.</summary>
    public bool IsRoundStarted => _started && !_wait && _cnt == 0;

    /// <summary>QC <c>round_handler_CountdownRunning()</c>.</summary>
    public bool CountdownRunning => _started && !_wait && _cnt > 0;

    /// <summary>QC <c>round_handler_AwaitingNextRound()</c>.</summary>
    public bool AwaitingNextRound => _started && _wait;

    /// <summary>Seconds left on the visible countdown (cnt-1 clamped to ≥0), for HUD/notification use.</summary>
    public int CountdownSecondsLeft => System.Math.Max(_cnt - 1, 0);

    /// <summary>QC <c>round_starttime</c>: absolute sim time the current/next round begins (−1 = can't start).</summary>
    public float RoundStartTime { get; private set; }

    /// <summary>QC <c>this.round_endtime</c>: absolute time the live round's timelimit expires (0 = none).</summary>
    public float RoundEndTime { get; private set; }

    /// <summary>QC <c>rounds_played</c>: how many rounds have actually started.</summary>
    public int RoundsPlayed { get; private set; }

    // ---- callbacks (QC .canRoundStart / .canRoundEnd / .roundStart) ----

    private Func<bool>? _canRoundStart;
    private Func<bool>? _canRoundEnd;
    private Action? _onRoundStart;

    /// <summary>
    /// QC <c>!(autocvar_g_campaign &amp;&amp; !campaign_bots_may_start)</c> from the countdown gate
    /// (server/round_handler.qc:38): in a campaign the round countdown is held until the human spawns
    /// (campaign_bots_may_start flips true on first real-client spawn). The host wires this to
    /// <c>g_campaign &amp;&amp; !Campaign.BotsMayStart</c>; when it returns true the countdown is frozen at its
    /// current second (it does NOT reset). Unset (non-campaign play) → never holds.
    /// </summary>
    public Func<bool>? CampaignBotHold { get; set; }

    /// <summary>
    /// Fired once when a round actually begins (QC the <c>FOREACH_CLIENT … GameRules_scoring_add(ROUNDS_PL)</c>
    /// at cnt==0). The host adds the per-player ROUNDS_PL score and any "round started" notification here.
    /// </summary>
    public Action? OnRoundCounted { get; set; }

    /// <summary>
    /// Fired when the next round is armed after the end-delay (QC the <c>reset_map(false)</c> the round
    /// handler calls when the wait expires). The host re-spawns players and resets map objects here (a "fake
    /// round start" that must NOT wipe the match score). Wired by <see cref="GameWorld"/> to its
    /// <see cref="GameWorld.ResetMap"/> with <c>fakeRoundStart: true</c>.
    /// </summary>
    public Action? OnRoundReset { get; set; }

    /// <summary>
    /// T40: fired once per whole second of the round-start countdown (QC the CSQC <c>Announcer_Countdown</c>
    /// CNT_ROUNDSTART tick, driven server-side here since the port has no CSQC announcer timer). The argument is
    /// the visible seconds remaining (count..1, then 0 on the tick the round begins) — i.e. the QC
    /// <c>countdown_rounded</c>. <see cref="GameWorld"/> maps it to the broadcasts: <c>NUM_ROUNDSTART_&lt;n&gt;</c>
    /// (the registry self-gates to n≤3) + <c>COUNTDOWN_ROUNDSTART(round, n)</c> for n≥1, and <c>BEGIN</c> at n==0.
    /// Fires at the 1 Hz decrement only (not every frame), so the announcer isn't spammed.
    /// </summary>
    public Action<int>? OnCountdownTick { get; set; }

    /// <summary>
    /// QC <c>round_handler_Spawn</c>: install the predicates + round-start action and arm the first countdown.
    /// Unlike the QC FirstThink one-frame delay, this initializes <see cref="RoundStartTime"/> eagerly from
    /// <see cref="GameStartTime"/> (set <see cref="GameStartTime"/> before calling for an accurate value).
    /// </summary>
    public void Spawn(Func<bool> canRoundStart, Func<bool> canRoundEnd, Action? onRoundStart = null)
    {
        _canRoundStart = canRoundStart;
        _canRoundEnd = canRoundEnd;
        _onRoundStart = onRoundStart;
        _wait = false;
        Init(DefaultEndDelay, DefaultCountdown, DefaultRoundTimeLimit);
        _started = true;
        RoundStartTime = System.Math.Max(Now, GameStartTime) + Countdown;
    }

    /// <summary>QC <c>round_handler_Init</c>: set delay/countdown/round-timelimit and reset cnt to count+1.</summary>
    public void Init(float endDelay, float countdown, float roundTimeLimit)
    {
        EndDelay = endDelay > 0f ? endDelay : 0f;
        Countdown = System.Math.Abs((int)MathF.Floor(countdown));
        _cnt = Countdown + 1;
        RoundTimeLimit = roundTimeLimit > 0f ? roundTimeLimit : 0f;
    }

    /// <summary>QC <c>round_handler_Reset(next_think)</c>: re-arm the countdown (resets RoundsPlayed at warmup).</summary>
    public void Reset(float nextStart)
    {
        _wait = false;
        if (Countdown != 0 && _cnt < Countdown + 1)
            _cnt = Countdown + 1;
        if (nextStart != 0f)
        {
            if (nextStart <= GameStartTime)
                RoundsPlayed = 0;
            RoundStartTime = nextStart + Countdown;
        }
    }

    /// <summary>
    /// Can a round start right now? Combines the configured predicate with the standard gates. Public so a
    /// host/UI can show "waiting for players". Returns false until <see cref="Spawn"/> has run.
    /// </summary>
    public bool CanStart() => _started && (_canRoundStart?.Invoke() ?? false);

    /// <summary>
    /// Drive the state machine one server frame (QC <c>round_handler_Think</c>). Call every frame from
    /// <see cref="GameWorld.Frame"/>. No-op until <see cref="Spawn"/> has been called.
    /// </summary>
    public void Think()
    {
        if (!_started)
            return;

        float now = Now;

        // intermission: idle (QC resets + removes the handler; here we just hold).
        if (IntermissionRunning)
        {
            Reset(0f);
            return;
        }

        // pre-game: hold the countdown pinned until game start.
        if (now < GameStartTime)
        {
            Reset(GameStartTime);
            return;
        }

        if (_wait)
        {
            // we are in the end-delay; the GameWorld schedules Think() each frame, but the QC edict only
            // re-arms after .delay has elapsed. We model that with a timer (see ThinkDue) below; on the
            // frame the delay elapses, fall through to start a fresh countdown.
            if (now < _endDelayUntil)
                return;
            _wait = false;
            _cnt = Countdown + 1;          // QC: cnt = count + 1 (re-init countdown)
            RoundStartTime = now + Countdown;
            // QC calls reset_map(false) here: re-spawn players + reset map objects for the next round (a
            // "fake round start" that preserves the match score). The host wires this to its map reset.
            OnRoundReset?.Invoke();
        }

        if (_cnt > 0)
        {
            // countdown running: only advance while the start predicate holds, and only step at 1 Hz
            // (QC sets nextthink = time + 1, so the countdown ticks once a second — not once per frame).
            // QC round_handler.qc:38 also AND-gates the campaign bot hold: in a campaign the countdown is frozen
            // until the human spawns (campaign_bots_may_start), so bots can't race the round before the player is in.
            if (CanStart() && !(CampaignBotHold?.Invoke() ?? false))
            {
                if (_cnt == Countdown + 1)
                    RoundStartTime = now + Countdown;

                if (!CountdownStepDue)
                    return; // wait out the rest of this 1-second slice before decrementing

                int f = _cnt - 1;
                if (f == 0)
                {
                    // round begins NOW.
                    _cnt = 0;
                    RoundEndTime = RoundTimeLimit > 0f ? now + RoundTimeLimit : 0f;
                    RoundsPlayed++;
                    OnCountdownTick?.Invoke(0);  // T40: QC CSQC countdown reaching 0 -> BEGIN announcer/center.
                    OnRoundCounted?.Invoke();   // QC: ROUNDS_PL score bump per player
                    _onRoundStart?.Invoke();    // QC: this.roundStart()
                    return;
                }
                OnCountdownTick?.Invoke(f);      // T40: QC CSQC NUM_ROUNDSTART_<f> + COUNTDOWN_ROUNDSTART center.
                _cnt--;
                _nextCountdownStep = now + 1f; // QC: nextthink = time + 1
            }
            else
            {
                // can't start (players left, etc.): reset the countdown and mark "can't start".
                Reset(0f);
                RoundStartTime = -1f;
                _nextCountdownStep = now + 1f;
            }
        }
        else
        {
            // round live: poll the end condition every frame.
            if (_canRoundEnd?.Invoke() ?? false)
            {
                // schedule the next round after the end-delay.
                _wait = true;
                _endDelayUntil = now + EndDelay;
            }
        }
    }

    // The QC edict uses nextthink to throttle the countdown to 1 Hz; GameWorld calls Think() every frame,
    // so we gate the decrement on this timer to reproduce the 1-second granularity faithfully.
    private float _nextCountdownStep;
    private float _endDelayUntil;

    /// <summary>True if the 1 Hz countdown step is due (so a per-frame caller doesn't burn the countdown instantly).</summary>
    private bool CountdownStepDue => Now >= _nextCountdownStep;

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
