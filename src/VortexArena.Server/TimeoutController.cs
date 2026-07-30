using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The timeout / timein pause system — the Godot-free essence of the timeout slice of
/// server/command/common.qc (<c>CommonCommand_timeout</c> / <c>timein</c> / <c>timeout_handler_think</c>). A
/// player may pause the match (<c>sv_timeout</c>): after a short lead-time countdown the game freezes for up to
/// <c>sv_timeout_length</c> seconds; the caller can resume early with <c>timein</c>. Each player has a limited
/// number of timeouts (<c>sv_timeout_number</c>).
///
/// Faithful to QC: the LEADTIME→ACTIVE→resume state machine, the per-player allowance, the guard chain (no
/// timeout during a vote, before the match starts, or too late in the match), and the early-resume shortening
/// to <c>sv_timeout_resumetime</c>. The pause is modeled both as <see cref="IsPaused"/> (which the world honors
/// by freezing scoring/round flow like intermission) and as the Base <c>slowmo</c> extreme slow-motion: when
/// the pause begins this core fires <see cref="ApplySlowmo"/> with <see cref="SlowmoValue"/> (0.0001) and
/// restores the captured original slowmo when it ends, so the host can drive the engine slowmo cvar and let
/// players inch during the pause exactly as Base does (<c>TIMEOUT_SLOWMO_VALUE</c>).
///
/// The per-second center-print countdown (QC <c>CENTER_TIMEOUT_BEGINNING</c>/<c>CENTER_TIMEOUT_ENDING</c>) and
/// the <c>ANNCE_PREPARE</c> warning cue are carried as host-wired sinks (<see cref="CenterPrintBeginning"/>,
/// <see cref="CenterPrintEnding"/>, <see cref="AnnouncePrepare"/>) so this Godot-free core drives the same
/// presentation timeline as <c>timeout_handler_think</c> while leaving the notification encoding to the host.
/// </summary>
public sealed class TimeoutController
{
    public const int Inactive = 0, LeadTime = 1, ActivePause = 2;

    /// <summary>QC <c>TIMEOUT_SLOWMO_VALUE</c> (common.qh): the extreme slow-motion the pause runs at.</summary>
    public const float SlowmoValue = 0.0001f;

    private readonly Dictionary<Player, int> _allowed = new();

    /// <summary>QC <c>timeout_status</c>: INACTIVE / LEADTIME / ACTIVE.</summary>
    public int Status { get; private set; }

    /// <summary>The player who called the timeout (QC <c>timeout_caller</c>), or null for a console call.</summary>
    public Player? Caller { get; private set; }

    /// <summary>QC <c>timeout_time</c>: seconds remaining in the active pause.</summary>
    public float Time { get; private set; }

    /// <summary>QC <c>timeout_leadtime</c>: seconds remaining before the pause begins.</summary>
    public float LeadTimeRemaining { get; private set; }

    /// <summary>True while the match is frozen (the pause is active) — the world treats this like game_stopped.</summary>
    public bool IsPaused => Status == ActivePause;

    /// <summary>True whenever a timeout is pending or active (QC <c>timeout_status</c> != INACTIVE).</summary>
    public bool Active => Status != Inactive;

    /// <summary>Broadcast sink (QC bprint). Host-wires to the chat/console broadcast.</summary>
    public Action<string>? Broadcast { get; set; }

    /// <summary>
    /// QC <c>Send_Notification(MSG_CENTER, CENTER_TIMEOUT_BEGINNING, n)</c>: per-second lead-in countdown
    /// center-print, fired once per remaining whole second of the lead-time. Host re-encodes into the
    /// per-client center-print stream.
    /// </summary>
    public Action<int>? CenterPrintBeginning { get; set; }

    /// <summary>
    /// QC <c>Send_Notification(MSG_CENTER, CENTER_TIMEOUT_ENDING, n)</c>: per-second pause-ending countdown
    /// center-print, fired once per remaining whole second of the active pause.
    /// </summary>
    public Action<int>? CenterPrintEnding { get; set; }

    /// <summary>
    /// QC <c>Send_Notification(MSG_ANNCE, ANNCE_PREPARE)</c>: the warning announcer cue played when only
    /// <c>sv_timeout_resumetime</c> seconds remain on the active pause.
    /// </summary>
    public Action? AnnouncePrepare { get; set; }

    /// <summary>
    /// QC <c>cvar_set("slowmo", ...)</c>: host sink to drive the engine slowmo cvar. Called with
    /// <see cref="SlowmoValue"/> when the pause begins and with the captured original slowmo when it ends.
    /// </summary>
    public Action<float>? ApplySlowmo { get; set; }

    /// <summary>
    /// QC <c>autocvar_slowmo</c> read into <c>orig_slowmo</c> (main.qc:352): the slowmo value to restore once
    /// the timeout finishes. Defaults to the live <c>slowmo</c> cvar; captured at pause-begin.
    /// </summary>
    public Func<float> OrigSlowmo { get; set; } = static () => Cvars.FloatOr("slowmo", 1f);

    private float _restoreSlowmo = 1f;

    // ---- host-wired predicates ----
    public Func<bool> VoteActive { get; set; } = static () => false;
    public Func<bool> Warmup { get; set; } = static () => false;
    public Func<float> GameStartTime { get; set; } = static () => 0f;
    public Func<Player, bool> IsPlayerOf { get; set; } = static _ => true;

    /// <summary>The time source (QC <c>time</c>); defaults to the ambient sim clock. Overridable for tests.</summary>
    public Func<float> Clock { get; set; } = static () => Api.Services is not null ? Api.Clock.Time : 0f;

    private float _lastTick;

    // The last whole-second value handed to a per-second center-print, so each integer second fires exactly
    // once even when Think() is pumped with a multi-second dt (QC fires one center-print per --counter step).
    private int _lastCountdownSecond = -1;

    private float Now => Clock();

    /// <summary>QC <c>this.allowed_timeouts</c>: remaining timeouts for a player (default <c>sv_timeout_number</c>).</summary>
    public int AllowedOf(Player p)
        => _allowed.TryGetValue(p, out int n) ? n : Cvars.Int("sv_timeout_number");

    /// <summary>Reset a player's allowance (QC on connect/spawn).</summary>
    public void ResetAllowance(Player p) => _allowed[p] = Cvars.Int("sv_timeout_number");

    public void Remove(Player p) => _allowed.Remove(p);

    /// <summary>
    /// QC <c>CommonCommand_timeout</c>: try to start a timeout. Returns true on success; on failure writes the
    /// reason to <paramref name="error"/>. <paramref name="caller"/> null = server console (bypasses the
    /// per-player allowance + the sv_timeout gate).
    /// </summary>
    public bool CallTimeout(Player? caller, out string error)
    {
        error = "";
        if (caller is not null && !Cvars.Bool("sv_timeout")) { error = "^1Timeouts are not allowed."; return false; }
        if (Active) { error = "^1A timeout is already active."; return false; }
        if (VoteActive()) { error = "^1You can not call a timeout while a vote is active."; return false; }
        if (Warmup() && !Cvars.Bool("g_warmup_allow_timeout")) { error = "^1Timeouts are not allowed during warmup."; return false; }
        if (Now < GameStartTime()) { error = "^1You can not call a timeout while the map is being restarted."; return false; }
        if (caller is not null && AllowedOf(caller) <= 0) { error = "^1You already used all your allowed timeouts."; return false; }
        if (caller is not null && !IsPlayerOf(caller)) { error = "^1Only players can call a timeout."; return false; }

        // QC "too late" guard: can't timeout within leadtime+1s of the time limit.
        float timelimit = Cvars.Float("timelimit");
        if (timelimit > 0f && (timelimit * 60f - Cvars.Float("sv_timeout_leadtime") - 1f) < Now - GameStartTime())
        { error = "^1It is too late to call a timeout now."; return false; }

        if (caller is not null) _allowed[caller] = AllowedOf(caller) - 1;
        Status = LeadTime;
        Caller = caller;
        Time = Cvars.FloatOr("sv_timeout_length", 120f);
        LeadTimeRemaining = Cvars.FloatOr("sv_timeout_leadtime", 4f);
        _lastTick = Now;
        _lastCountdownSecond = -1;
        Broadcast?.Invoke($"^2* {(caller?.NetName ?? "server")} called a timeout"
            + (caller is not null ? $" ({AllowedOf(caller)} left)" : ""));
        return true;
    }

    /// <summary>
    /// QC <c>CommonCommand_timein</c>: abort the lead-time, or shorten an active pause to
    /// <c>sv_timeout_resumetime</c>. Only the original caller (or console) may resume. Returns true on success.
    /// </summary>
    public bool CallTimein(Player? caller, out string error)
    {
        error = "";
        if (!Active) { error = "^1No active timeout."; return false; }
        if (caller is not null && !ReferenceEquals(caller, Caller)) { error = "^1You are not allowed to resume the timeout."; return false; }

        if (Status == LeadTime)
        {
            Reset();
            Broadcast?.Invoke("^2* the timeout was aborted");
        }
        else // ACTIVE
        {
            Time = Cvars.FloatOr("sv_timeout_resumetime", 3f);
            _lastCountdownSecond = -1; // re-arm the ending countdown from the shortened time
            Broadcast?.Invoke("^2* the game will resume");
        }
        return true;
    }

    /// <summary>
    /// QC <c>timeout_handler_think</c>: advance the timeout one frame. LEADTIME counts down then freezes
    /// (Status→ACTIVE); ACTIVE counts down then resumes (Status→INACTIVE). Real wall-clock seconds, so the
    /// pause length is honored even though <see cref="IsPaused"/> freezes the sim.
    /// </summary>
    public void Think()
    {
        if (!Active) return;
        float now = Now;
        float dt = System.Math.Max(0f, now - _lastTick);
        _lastTick = now;

        if (Status == LeadTime)
        {
            // QC CENTER_TIMEOUT_BEGINNING: one center-print per whole second of lead-time remaining.
            EmitCountdown((int)System.Math.Ceiling(LeadTimeRemaining), CenterPrintBeginning);
            LeadTimeRemaining -= dt;
            if (LeadTimeRemaining <= 0f)
            {
                Status = ActivePause;
                LeadTimeRemaining = 0f;
                _lastCountdownSecond = -1; // re-arm for the ENDING countdown
                // QC: cvar_set("slowmo", TIMEOUT_SLOWMO_VALUE) — drop to extreme slow-motion so players can
                // still inch during the pause (orig_slowmo captured here to restore on resume).
                _restoreSlowmo = OrigSlowmo();
                ApplySlowmo?.Invoke(SlowmoValue);
                Broadcast?.Invoke("^2* the game is paused");
            }
        }
        else if (Status == ActivePause)
        {
            // QC CENTER_TIMEOUT_ENDING + ANNCE_PREPARE warning at sv_timeout_resumetime seconds left.
            int secondsLeft = (int)System.Math.Ceiling(Time);
            EmitCountdown(secondsLeft, CenterPrintEnding,
                resumeWarnAt: (int)Cvars.FloatOr("sv_timeout_resumetime", 3f));
            Time -= dt;
            if (Time <= 0f)
            {
                // QC: cvar_set("slowmo", orig_slowmo) — restore normal time flow.
                ApplySlowmo?.Invoke(_restoreSlowmo);
                Broadcast?.Invoke("^2* the game has resumed");
                Reset();
            }
        }
    }

    /// <summary>
    /// Fire <paramref name="sink"/> once for every whole second from the last emitted boundary down to
    /// <paramref name="secondsLeft"/> (inclusive), so a multi-second <c>dt</c> still produces one center-print
    /// per second exactly as the QC per-frame <c>--counter</c> would. When <paramref name="resumeWarnAt"/> is
    /// crossed, also play the ANNCE_PREPARE cue (QC fires it the tick <c>timeout_time == resumetime</c>).
    /// </summary>
    private void EmitCountdown(int secondsLeft, Action<int>? sink, int resumeWarnAt = -1)
    {
        if (secondsLeft < 1) return;
        int from = _lastCountdownSecond < 0 ? secondsLeft : _lastCountdownSecond - 1;
        for (int n = from; n >= secondsLeft; n--)
        {
            if (n < 1) break;
            sink?.Invoke(n);
            if (resumeWarnAt > 0 && n == resumeWarnAt) AnnouncePrepare?.Invoke();
        }
        _lastCountdownSecond = secondsLeft;
    }

    /// <summary>
    /// QC <c>Shutdown</c> (world.qc:2627-2628): <c>if (timeout_status == TIMEOUT_ACTIVE) cvar_set("slowmo",
    /// ftos(orig_slowmo))</c>. On world teardown while a pause is ACTIVE, restore the captured original slowmo so
    /// the engine doesn't keep running at <see cref="SlowmoValue"/> after the match is gone. A no-op when no
    /// pause is active (LEADTIME/INACTIVE leave slowmo untouched, exactly like Base which only checks ACTIVE).
    /// </summary>
    public void ResetSlowmoOnShutdown()
    {
        if (Status == ActivePause)
            ApplySlowmo?.Invoke(_restoreSlowmo);
    }

    private void Reset()
    {
        Status = Inactive;
        Caller = null;
        Time = 0f;
        LeadTimeRemaining = 0f;
        _lastCountdownSecond = -1;
    }
}
