using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// [T41] Server-side announcer driver — the port of <c>qcsrc/client/announcer.qc</c> (<c>Announcer_Time</c>
/// and the <c>Announcer_Countdown</c>/<c>Announcer_Gamestart</c> number picker). The port has no CSQC
/// announcer timer, so the announcer runs on the server and BROADCASTS to all clients through
/// <see cref="NotificationSystem"/> (which the host re-encodes into the per-client notification stream).
///
/// <para><b>Division of labour with T40.</b> The pre-match / round-start countdown (3-2-1-prepare, the QC
/// <c>Announcer_Countdown</c> CNT_GAMESTART/CNT_ROUNDSTART number announcements + <c>ANNCE_PREPARE</c>) is
/// ALREADY emitted by <see cref="WarmupController.OnCountdownTick"/> / <see cref="RoundHandler.OnCountdownTick"/>
/// (mapped in <c>GameWorld.BroadcastGameStartCountdown</c>). This controller therefore drives only the piece
/// T40 did not cover: <c>Announcer_Time</c> — the "5 minutes remain" / "1 minute remains" map-time
/// announcements with the QC hysteresis latches (<c>announcer_5min</c>/<c>announcer_1min</c>). The countdown
/// number picker (<see cref="PickCountdownNumber"/>) is ported here too as a pure, tested helper so the full
/// <c>Announcer_Countdown</c> schedule has a faithful home, but it is NOT broadcast from <see cref="Tick"/>
/// (that would double-fire T40's countdown).</para>
///
/// QC <c>Announcer_Time</c> (announcer.qc:188-226): after the match goes live it computes the time left
/// (warmup uses <c>STAT(WARMUP_TIMELIMIT)</c>, the match uses <c>STAT(TIMELIMIT)*60</c>) and, gated on the
/// per-client <c>cl_announcer_maptime</c> mode, fires the remaining-minute announcement the instant the
/// counter crosses below <c>minute*60</c> (and re-arms once it climbs back above it — the
/// <c>ANNOUNCER_CHECKMINUTE</c> macro). The macro's window <c>timeleft &lt; m*60 &amp;&amp; timeleft &gt; m*60 - 1</c>
/// is preserved exactly; the latch persists across frames so it fires once per crossing, never per frame.
/// </summary>
public sealed class AnnouncerController
{
    // QC client globals announcer_5min / announcer_1min — the per-minute hysteresis latches. Set true once the
    // remaining time crosses below the threshold (announce fired), cleared once it rises back above it.
    private bool _announced5Min;
    private bool _announced1Min;

    // QC Announcer_Time's static `warmup_stage_prev`: a warmup<->match transition re-arms the latches and skips
    // one tick (so the announcer doesn't fire on the stage flip itself).
    private bool _warmupStagePrev;
    private bool _haveWarmupPrev;

    // === context sinks (the host wires these so the controller stays Godot-free + unit-testable) ===

    /// <summary>Current sim time (QC <c>time</c>).</summary>
    public System.Func<float> Now { get; set; } = static () => 0f;

    /// <summary>QC <c>STAT(GAMESTARTTIME)</c> — the absolute sim time the match goes live.</summary>
    public System.Func<float> GameStartTime { get; set; } = static () => 0f;

    /// <summary>QC <c>warmup_stage</c> — true during the pre-match warmup phase.</summary>
    public System.Func<bool> WarmupStage { get; set; } = static () => false;

    /// <summary>QC <c>STAT(WARMUP_TIMELIMIT)</c> — the warmup time limit in seconds (≤ 0 = no warmup time limit).</summary>
    public System.Func<float> WarmupTimeLimitSeconds { get; set; } = static () => 0f;

    /// <summary>QC <c>STAT(TIMELIMIT)</c> — the match time limit in MINUTES (0 = no limit).</summary>
    public System.Func<float> TimeLimitMinutes { get; set; } = static () => 0f;

    /// <summary>QC <c>intermission</c> — the match is in its end-of-game intermission (no announcements).</summary>
    public System.Func<bool> Intermission { get; set; } = static () => false;

    /// <summary>
    /// QC <c>autocvar_cl_announcer_maptime</c> (shipped 3): 0 = off, 1 = 1-minute only, 2 = 5-minute only,
    /// 3 = both. Per-client in QC; here it is the server's config value (the broadcast is global). Defaults to
    /// 3 (both) when the cvar is unset.
    /// </summary>
    public System.Func<int> AnnouncerMapTime { get; set; } = static () => 3;

    /// <summary>
    /// Fires a remaining-minutes announcement to everyone (QC <c>Local_Notification(MSG_ANNCE,
    /// ANNCE_REMAINING_MIN_n)</c>). Default sink broadcasts through <see cref="NotificationSystem"/>; tests can
    /// swap it to record the calls without a live notification subsystem.
    /// </summary>
    public System.Action<int> AnnounceRemainingMin { get; set; } = static minute =>
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Annce, "REMAINING_MIN_" + minute);

    /// <summary>
    /// QC <c>Announcer_Time</c> (announcer.qc:188-226), driven once per server frame from
    /// <c>GameWorld.OnStartFrame</c>. No-op during intermission; re-arms the latches across a warmup/match
    /// stage flip; computes the remaining time and fires the 5-/1-minute announcements via the
    /// <c>ANNOUNCER_CHECKMINUTE</c> hysteresis. The countdown announcer is T40's job (see the class summary).
    /// </summary>
    public void Tick()
    {
        if (Intermission())
            return;

        bool warmup = WarmupStage();

        // QC: a warmup<->match stage change re-arms both latches and returns (don't announce on the flip).
        if (!_haveWarmupPrev || warmup != _warmupStagePrev)
        {
            _announced5Min = _announced1Min = false;
            _warmupStagePrev = warmup;
            _haveWarmupPrev = true;
            return;
        }

        float now = Now();
        float startTime = GameStartTime();

        // QC: before the match goes live, clear the latches (so the announcer re-arms for the live phase).
        if (now < startTime)
        {
            _announced5Min = _announced1Min = false;
            return;
        }

        // QC: warmup uses WARMUP_TIMELIMIT (0 => no time-bounded warmup => timeleft 0); the match uses
        // TIMELIMIT*60. Both offset from game_starttime and floor at 0.
        float timeLeft;
        if (warmup)
        {
            float warmupLimit = WarmupTimeLimitSeconds();
            timeLeft = warmupLimit > 0f ? System.Math.Max(0f, warmupLimit + startTime - now) : 0f;
        }
        else
        {
            timeLeft = System.Math.Max(0f, TimeLimitMinutes() * 60f + startTime - now);
        }

        int mode = AnnouncerMapTime();

        // QC: cl_announcer_maptime >= 2 enables the 5-minute announcement.
        if (mode >= 2)
            CheckMinute(5, timeLeft, ref _announced5Min);

        // QC: cl_announcer_maptime == 1 || == 3 enables the 1-minute announcement.
        if (mode == 1 || mode == 3)
            CheckMinute(1, timeLeft, ref _announced1Min);
    }

    /// <summary>
    /// Port of the QC <c>ANNOUNCER_CHECKMINUTE(minute)</c> macro (announcer.qc:176-186): if the latch is set,
    /// clear it once the remaining time rises back above <c>minute*60</c>; otherwise fire the announcement (and
    /// set the latch) the frame the remaining time falls into <c>(minute*60 - 1, minute*60)</c>. The narrow
    /// 1-second arming window means a single frame inside it fires once — exactly the QC behaviour.
    /// </summary>
    private void CheckMinute(int minute, float timeLeft, ref bool announced)
    {
        float threshold = minute * 60f;
        if (announced)
        {
            if (timeLeft > threshold)
                announced = false;
        }
        else if (timeLeft < threshold && timeLeft > threshold - 1f)
        {
            announced = true;
            AnnounceRemainingMin(minute);
        }
    }

    /// <summary>Re-arm both remaining-minute latches and the warmup-stage tracking (call on a map/match reset).</summary>
    public void Reset()
    {
        _announced5Min = _announced1Min = false;
        _haveWarmupPrev = false;
        _warmupStagePrev = false;
    }

    // =============================================================================================
    //  Countdown number picker (QC Announcer_PickNumber / Announcer_Countdown) — pure helper.
    //  The actual countdown broadcast is owned by T40 (WarmupController/RoundHandler.OnCountdownTick →
    //  GameWorld.BroadcastGameStartCountdown). This is the faithful schedule so the 3-2-1-prepare logic
    //  has a tested home; it is NOT called from Tick() (that would double-fire T40's countdown).
    // =============================================================================================

    /// <summary>The countdown phase a number announcement belongs to (QC <c>CNT_GAMESTART</c>/<c>CNT_ROUNDSTART</c>).</summary>
    public enum CountdownKind
    {
        /// <summary>QC CNT_GAMESTART — the pre-match game-start countdown (NUM_GAMESTART_n, shipped enabled n≤5).</summary>
        GameStart,
        /// <summary>QC CNT_ROUNDSTART — the per-round countdown (NUM_ROUNDSTART_n, shipped enabled n≤3).</summary>
        RoundStart,
    }

    /// <summary>
    /// Port of QC <c>Announcer_Countdown</c>'s rounding (announcer.qc:72-73): the visible seconds remaining is
    /// <c>floor(0.5 + countdown)</c>, where <c>countdown</c> is the time until the (round)start. Counts down to
    /// 0 (the BEGIN tick). The host's countdown driver advances this once per whole second.
    /// </summary>
    public static int CountdownRounded(float secondsLeft)
        => (int)System.Math.Floor(0.5 + System.Math.Max(0f, secondsLeft));

    /// <summary>
    /// QC <c>Announcer_PickNumber(cnt, num)</c> for the countdown families (announcer.qc, the GAMESTART/
    /// ROUNDSTART branches): returns the bare announcer notification name for a given visible second (e.g.
    /// "NUM_GAMESTART_3"), or null when the second is out of the 1..10 announced range or is the terminal 0
    /// (which is the BEGIN tick, not a number). Does NOT consult the registry's Enabled flag — the caller gates
    /// on the shipped default (NUM_GAMESTART n≤5 / NUM_ROUNDSTART n≤3) exactly like
    /// <c>GameWorld.AnnceIfEnabled</c>.
    /// </summary>
    public static string? PickCountdownNumber(CountdownKind kind, int secondsLeft)
    {
        if (secondsLeft < 1 || secondsLeft > 10)
            return null;
        return kind == CountdownKind.GameStart
            ? "NUM_GAMESTART_" + secondsLeft
            : "NUM_ROUNDSTART_" + secondsLeft;
    }
}
