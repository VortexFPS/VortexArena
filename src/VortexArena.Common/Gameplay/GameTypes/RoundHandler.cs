// Port of qcsrc/server/round_handler.qc — the shared round-elimination state machine used by the
// round-based gametypes (Clan Arena, Freeze Tag, and the round-based variants of others). In QuakeC this
// was a single `round_handler` edict whose think function (round_handler_Think) cycled through:
//   wait-for-players  → countdown  → round-in-progress  → end-delay  → (next round)
// driven by three callbacks the gametype installs (round_handler_Spawn): canRoundStart, canRoundEnd,
// roundStart. We reproduce that driver headlessly as a plain object the gametype owns and ticks; it is
// Godot-free and depends only on the engine clock via the gametype's own time source.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The round-elimination driver — the Godot-free port of QuakeC's <c>round_handler</c> (server/round_handler.qc).
/// A gametype creates one and ticks it every frame; it sequences the wait/countdown/round/end-delay phases and
/// invokes the supplied callbacks:
///  - <see cref="CanRoundStart"/> (QC canRoundStart): may the countdown proceed? (every team has players);
///  - <see cref="CanRoundEnd"/> (QC canRoundEnd): is the round decided? (a team won / stalemate) — returns true
///    when the round is over;
///  - <see cref="OnRoundStart"/> (QC roundStart): called once when the countdown hits zero and the round begins.
/// The countdown length, end-delay, and per-round time limit mirror the QC <c>round_handler_Init</c> args.
///
/// Parity (2026-06-22, W1-round-handler seam): the think loop now reproduces the QC <c>round_handler_Think</c>
/// cadence faithfully — the <see cref="GameStartTime"/> pre-game hold (<c>time &lt; game_starttime</c>), the
/// <see cref="IntermissionRunning"/> idle, the <b>1 Hz</b> countdown granularity (QC <c>nextthink = time + 1</c>,
/// so the countdown ticks once a second, not once per frame), the per-second <see cref="OnCountdownTick"/>
/// announcer edge, and the once-per-round <see cref="OnRoundCounted"/> (QC the <c>ROUNDS_PL</c> score bump) and
/// <see cref="OnRoundReset"/> (QC <c>reset_map(false)</c> on the next round). <see cref="RoundEndTime"/> /
/// <see cref="RoundsPlayed"/> are also publicly settable so a host that drives a separate live handler can mirror
/// the round timing into the gametype's own handler (which the gametype's <c>CheckWinner</c> reads).
/// </summary>
public sealed class RoundHandler
{
    /// <summary>
    /// Global host seam for QC <c>round_handler_IsActive() &amp;&amp; !round_handler_IsRoundStarted()</c> — true when a
    /// round-based gametype is running but the current round has NOT yet started (the inter-round / pre-round
    /// grace window). The per-gametype <see cref="RoundHandler"/> instance is owned by the active gametype and is
    /// not reachable from the Common gameplay layer, so the host (which DOES hold the live handler) wires this
    /// each match. Consumers in Common (e.g. RandomGravityMutator's pre-round suppression gate) read it through
    /// <see cref="RoundGateBlocks"/>. Unwired (no round gametype / headless) it is null, so the gate is inert —
    /// matching QC where <c>round_handler_IsActive()</c> is false in non-round modes.
    /// </summary>
    public static Func<bool>? RoundNotStartedProvider;

    /// <summary>
    /// QC <c>round_handler_IsActive() &amp;&amp; !round_handler_IsRoundStarted()</c>: true when the active round-based
    /// gametype is in its pre-round grace window (round armed but not yet live), so callers should suppress
    /// per-frame round-gated behavior. False when no round gametype is active (provider unwired).
    /// </summary>
    public static bool RoundGateBlocks() => RoundNotStartedProvider?.Invoke() ?? false;

    /// <summary>The phase the handler is in (QC round_handler think branches).</summary>
    public enum RoundPhase
    {
        /// <summary>Awaiting the countdown start gate (QC wait == true between rounds).</summary>
        WaitingToStart,
        /// <summary>Counting down to the round start (QC cnt &gt; 0).</summary>
        Countdown,
        /// <summary>The round is live (QC cnt == 0).</summary>
        InProgress,
    }

    /// <summary>QC time accessor (the gametype supplies the sim clock so this stays Godot-free).</summary>
    private readonly Func<float> _now;

    /// <summary>QC canRoundStart: true once a round may begin (every active team has a live player).</summary>
    public Func<bool> CanRoundStart { get; set; } = () => true;

    /// <summary>QC canRoundEnd: true once the current round is decided (a winner or stalemate exists).</summary>
    public Func<bool> CanRoundEnd { get; set; } = () => false;

    /// <summary>QC roundStart: invoked once when the round actually begins (the countdown reached zero).</summary>
    public Action OnRoundStart { get; set; } = () => { };

    /// <summary>QC .count: the countdown length in seconds (default 5).</summary>
    public float CountdownLength = 5f;

    /// <summary>QC .delay: seconds between a round ending and the next countdown starting (default 5).</summary>
    public float EndDelay = 5f;

    /// <summary>QC .round_timelimit: per-round time limit in seconds (0 = none).</summary>
    public float RoundTimeLimit = 180f;

    /// <summary>
    /// QC <c>game_starttime</c>: absolute sim time the pre-game ends. While <c>now &lt; GameStartTime</c> the
    /// handler holds the countdown pinned (QC <c>time &lt; game_starttime → round_handler_Reset(game_starttime)</c>).
    /// Set by the host before/while ticking; 0 (default) means "no pre-game hold" (start immediately).
    /// </summary>
    public float GameStartTime { get; set; }

    /// <summary>
    /// QC <c>intermission_running</c>: while true the handler idles (QC resets + removes the edict; here we just
    /// hold in the wait state). Set by the host each frame.
    /// </summary>
    public bool IntermissionRunning { get; set; }

    /// <summary>
    /// QC the CSQC <c>Announcer_Countdown</c> CNT_ROUNDSTART tick, driven server-side here. Fired once per whole
    /// second of the round-start countdown — the argument is the visible seconds remaining (count..1, then 0 on
    /// the tick the round begins). Fires at the 1 Hz decrement only (not every frame).
    /// </summary>
    public Action<int>? OnCountdownTick { get; set; }

    /// <summary>
    /// QC the cnt==0 <c>FOREACH_CLIENT … GameRules_scoring_add(it, ROUNDS_PL, 1)</c>: fired once when a round
    /// actually begins, so the host can credit each player the per-round ROUNDS_PL point.
    /// </summary>
    public Action? OnRoundCounted { get; set; }

    /// <summary>
    /// QC <c>reset_map(false)</c> on the next round: fired when the next round is armed after the end-delay, so
    /// the host re-spawns players + resets map objects (a "fake round start" that must NOT wipe the match score).
    /// </summary>
    public Action? OnRoundReset { get; set; }

    public RoundPhase Phase { get; private set; } = RoundPhase.WaitingToStart;

    /// <summary>Absolute sim time the countdown completes and the round starts (QC round_starttime).</summary>
    public float RoundStartTime { get; private set; }

    /// <summary>
    /// Absolute sim time the round's time limit expires (QC round_endtime); 0 = none. Publicly settable so a host
    /// driving a separate live handler can mirror its round timing into this (gametype-owned) handler.
    /// </summary>
    public float RoundEndTime { get; set; }

    /// <summary>
    /// 1-based count of rounds that have started (QC rounds_played). Publicly settable for the same mirroring
    /// reason as <see cref="RoundEndTime"/>.
    /// </summary>
    public int RoundsPlayed { get; set; }

    public RoundHandler(Func<float> now) => _now = now;

    /// <summary>QC round_handler_Init: (re)configure the timing for the next round.</summary>
    public void Init(float endDelay, float countdownLength, float roundTimeLimit)
    {
        EndDelay = endDelay > 0f ? endDelay : 0f;
        CountdownLength = MathF.Abs(MathF.Floor(countdownLength));
        RoundTimeLimit = roundTimeLimit > 0f ? roundTimeLimit : 0f;
        // QC: cnt = count + 1 (the visible countdown is re-armed when the timing is (re)initialized).
        _cnt = (int)CountdownLength + 1;
    }

    /// <summary>Begin a fresh wait→countdown cycle (QC the initial wait state).</summary>
    public void Reset()
    {
        Phase = RoundPhase.WaitingToStart;
        RoundStartTime = 0f;
        RoundEndTime = 0f;
        _cnt = (int)CountdownLength + 1;
        _countdownStarted = false;
        _afterFirstRound = false; // QC round_handler_Reset: this.wait = false
    }

    /// <summary>
    /// QC <c>round_handler_Reset(next_think)</c>: re-arm the countdown. When <paramref name="nextStart"/> is at or
    /// before <see cref="GameStartTime"/> the rounds-played count is zeroed (a fresh match), and the round start
    /// time is pushed out by the countdown length.
    /// </summary>
    public void Reset(float nextStart)
    {
        Phase = RoundPhase.WaitingToStart;
        RoundEndTime = 0f;
        if (CountdownLength != 0f && _cnt < (int)CountdownLength + 1)
            _cnt = (int)CountdownLength + 1;
        _countdownStarted = false;
        _afterFirstRound = false; // QC round_handler_Reset: this.wait = false
        if (nextStart != 0f)
        {
            if (nextStart <= GameStartTime)
                RoundsPlayed = 0;
            RoundStartTime = nextStart + CountdownLength;
        }
    }

    /// <summary>True once the round is live (QC round_handler_IsRoundStarted).</summary>
    public bool IsRoundStarted => Phase == RoundPhase.InProgress;

    /// <summary>
    /// Mirror the round-live phase from a separate live handler onto this (gametype-owned) handler — the phase
    /// counterpart to the publicly settable <see cref="RoundEndTime"/> / <see cref="RoundsPlayed"/> mirror seam.
    /// A host that drives the authoritative round handler can push its <c>IsRoundStarted</c> here each frame so
    /// the gametype's own round-phase gates (QC <c>round_handler_IsRoundStarted</c>, e.g. CA's damage2score and
    /// "you are now alone" notify) read the live phase. Only toggles between the live (<see cref="RoundPhase.InProgress"/>)
    /// and not-live (<see cref="RoundPhase.WaitingToStart"/>) states — it does not drive the local countdown.
    /// </summary>
    public void MirrorRoundStarted(bool roundStarted) =>
        Phase = roundStarted ? RoundPhase.InProgress : RoundPhase.WaitingToStart;

    /// <summary>True while the countdown is running (QC round_handler_CountdownRunning).</summary>
    public bool CountdownRunning => Phase == RoundPhase.Countdown;

    /// <summary>True while awaiting the next round (QC round_handler_AwaitingNextRound).</summary>
    public bool AwaitingNextRound => Phase == RoundPhase.WaitingToStart;

    /// <summary>Visible seconds left on the countdown (cnt-1 clamped to ≥0), for HUD/notification use.</summary>
    public int CountdownSecondsLeft => System.Math.Max(_cnt - 1, 0);

    // ---- live countdown state (QC .cnt + the nextthink 1 Hz throttle) ----
    private int _cnt;                 // QC .cnt (count+1 down to 0)
    private bool _countdownStarted;   // tracks the QC wait→countdown arming edge
    private bool _afterFirstRound;    // QC .wait: true once a round has ended (so the next arm runs reset_map)
    private float _nextCountdownStep; // QC nextthink = time + 1 (1 Hz countdown throttle)
    private float _endDelayUntil;     // QC nextthink = time + delay (end-delay timer)

    /// <summary>
    /// Advance the handler one frame (QC round_handler_Think). Returns true on the frame the round actually
    /// ends (so the caller can resolve scoring once), mirroring the QC canRoundEnd → schedule-next-round edge.
    /// </summary>
    public bool Tick()
    {
        float now = _now();

        // QC: intermission_running → reset + (in QC) remove the edict. Here we hold in the wait state.
        if (IntermissionRunning)
        {
            Reset(0f);
            return false;
        }

        // QC: time < game_starttime → hold the countdown pinned at game_starttime.
        if (now < GameStartTime)
        {
            Reset(GameStartTime);
            return false;
        }

        switch (Phase)
        {
            case RoundPhase.WaitingToStart:
                // QC the `this.wait` branch: after a round, hold out the end-delay (.delay) before re-arming.
                // The very first countdown (no round played yet) is armed by Spawn with wait=false, so it does
                // NOT wait and does NOT call reset_map — only the post-round arm runs OnRoundReset.
                if (_afterFirstRound && now < _endDelayUntil)
                    return false;

                // QC: leaving the wait state arms the countdown (cnt = count + 1) and sets round_starttime.
                Phase = RoundPhase.Countdown;
                _cnt = (int)CountdownLength + 1;
                RoundStartTime = now + CountdownLength;
                _nextCountdownStep = now; // first decrement is due immediately (QC nextthink = time)
                _countdownStarted = false;
                if (_afterFirstRound)
                {
                    _afterFirstRound = false;
                    OnRoundReset?.Invoke(); // QC reset_map(false): re-spawn players for the next round.
                }
                return false;

            case RoundPhase.Countdown:
                // QC: the countdown only ticks while canRoundStart holds; otherwise it stalls (reset, can't start).
                if (!CanRoundStart())
                {
                    Reset(0f);
                    RoundStartTime = -1f; // QC: round_starttime = -1 (can't start)
                    _nextCountdownStep = now + 1f;
                    return false;
                }

                // QC: on the first armed second, round_starttime = time + count.
                if (!_countdownStarted)
                {
                    RoundStartTime = now + CountdownLength;
                    _countdownStarted = true;
                }

                // QC throttles the countdown to 1 Hz (nextthink = time + 1); a per-frame caller must wait out the
                // remainder of the current 1-second slice before decrementing.
                if (now < _nextCountdownStep)
                    return false;

                int f = _cnt - 1;
                if (f == 0)
                {
                    // QC cnt==0: the round begins NOW.
                    _cnt = 0;
                    Phase = RoundPhase.InProgress;
                    RoundEndTime = RoundTimeLimit > 0f ? now + RoundTimeLimit : 0f;
                    RoundsPlayed++;
                    OnCountdownTick?.Invoke(0);  // QC CSQC countdown reaching 0 → BEGIN announcer/center.
                    OnRoundCounted?.Invoke();    // QC: per-player ROUNDS_PL score bump.
                    OnRoundStart();              // QC: this.roundStart()
                    return false;
                }
                OnCountdownTick?.Invoke(f);       // QC CSQC NUM_ROUNDSTART_<f> + COUNTDOWN_ROUNDSTART center.
                _cnt--;
                _nextCountdownStep = now + 1f;    // QC: nextthink = time + 1
                return false;

            case RoundPhase.InProgress:
                // QC: once canRoundEnd holds, set wait=true and schedule the next round after the end-delay
                // (nextthink = time + .delay). The wait branch above then re-arms once the delay elapses.
                if (CanRoundEnd())
                {
                    Phase = RoundPhase.WaitingToStart;
                    _afterFirstRound = true; // QC this.wait = true
                    RoundEndTime = 0f;
                    _endDelayUntil = now + EndDelay;
                    RoundStartTime = now + EndDelay;
                    return true; // the round just ended this frame
                }
                return false;
        }
        return false;
    }
}
