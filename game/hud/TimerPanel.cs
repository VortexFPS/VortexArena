using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Match timer — port of Base/.../qcsrc/client/hud/panel/timer.qc (HUD panel #5). The QC version read
/// <c>STAT(GAMESTARTTIME)</c>, <c>STAT(TIMELIMIT)</c>, <c>STAT(WARMUP_TIMELIMIT)</c>, the round stats
/// (<c>STAT(ROUNDSTARTTIME)</c>/<c>STAT(ROUND_TIMELIMIT)</c>), <c>STAT(OVERTIMES)</c> /
/// <c>STAT(TIMEOUT_STATUS)</c> and the current time to show either elapsed time (count up) or time
/// remaining (count down), coloring it white → yellow → red as the limit approached
/// (<see cref="HUD_Timer_Color"/>), plus a subtext line ("Warmup"/"Timeout"/"Sudden Death"/"Overtime"/
/// "Overtime #N") and a secondary round subtimer.
///
/// Match state is fed by the net/match layer, not carried on a player: it sets <see cref="MatchStartTime"/>
/// and <see cref="TimeLimitSeconds"/> (QC GAMESTARTTIME / TIMELIMIT*60), the warmup/overtime flags, and an
/// optional round subtimer via <see cref="SetRound"/>. The panel renders the main timer (count up/down with
/// the warning colors), the subtext line, and the round timer when active. The clock comes from
/// <see cref="Now"/> if set, else the sim clock (<see cref="Api.Clock"/>), else its own ticker.
///
/// Behavior is cvar-driven exactly like the QC original (<c>hud_panel_timer_*</c>): <c>increment</c>
/// (count up), <c>unbound</c> (show pre-match negative/over-limit seconds), <c>secondary</c> (0 hide / 1
/// show / 2 show swapped), and the <c>warning_*</c> family for the yellow/red thresholds (absolute seconds
/// or relative to the timelimit). Defaults are registered in <see cref="RegisterDefaults"/>.
/// </summary>
public partial class TimerPanel : HudPanel
{
    // QC stats.qh: const int OVERTIME_SUDDENDEATH = BITS(24) = 16777216 — the sentinel STAT(OVERTIMES) value.
    private const int OvertimeSuddenDeath = 1 << 24;

    // Self-blank gate: until the match/net layer feeds ANY state, the panel draws NOTHING (contract §6 — new
    // and auto-discovered panels must not clutter the screen before they are wired). Every public data setter
    // raises this; QC always had live STATs so it always drew, but in the port the panel is auto-discovered
    // into _fullHud/GameDemo and may never receive data on a pure --connect client (NetHud draws there).
    private bool _fed;

    private double _matchStartTime;
    /// <summary>Absolute time (seconds, same clock as <see cref="Now"/>) the match started. QC GAMESTARTTIME.</summary>
    public double MatchStartTime { get => _matchStartTime; set { _matchStartTime = value; _fed = true; } }

    private float _timeLimitSeconds;
    /// <summary>Match time limit in seconds; &lt;= 0 means no limit (count up). QC TIMELIMIT * 60.</summary>
    public float TimeLimitSeconds { get => _timeLimitSeconds; set { _timeLimitSeconds = value; _fed = true; } }

    /// <summary>
    /// Warmup time limit in seconds (QC STAT(WARMUP_TIMELIMIT)); &lt;= 0 means warmup has no time limit. Only
    /// used during <see cref="WarmupStage"/>: when &gt; 0 the timer counts down against it and the subtext is
    /// "Warmup"; when &lt;= 0 the timer counts up and the subtext is the "no time limit"/nag variant.
    /// </summary>
    private float _warmupTimeLimitSeconds;
    public float WarmupTimeLimitSeconds { get => _warmupTimeLimitSeconds; set { _warmupTimeLimitSeconds = value; _fed = true; } }

    /// <summary>Force count-up even when a limit is set (QC hud_panel_timer_increment). Mirrors the cvar; the
    /// live cvar (when set) takes precedence in <see cref="DrawPanel"/>.</summary>
    public bool CountUp { get; set; }

    private bool _warmupStage;
    /// <summary>True during warmup (QC warmup_stage): shows the warmup subtext and counts against the warmup
    /// limit (or up when there is none).</summary>
    public bool WarmupStage { get => _warmupStage; set { _warmupStage = value; _fed = true; } }

    /// <summary>
    /// True once the match has gone into overtime (QC overtime): shows "Overtime" and the main timer counts
    /// up past the limit. The match layer sets this when it grants overtime. Equivalent to
    /// <see cref="Overtimes"/> == 1; setting it raises <see cref="Overtimes"/> to at least 1.
    /// </summary>
    public bool Overtime
    {
        get => _overtimes >= 1;
        set
        {
            if (value) { if (_overtimes < 1) _overtimes = 1; }
            else if (_overtimes != OvertimeSuddenDeath) _overtimes = 0;
            _fed = true;
        }
    }

    private int _overtimes;

    /// <summary>
    /// QC STAT(OVERTIMES): 0 = none, 1 = "Overtime", N&gt;=2 = "Overtime #N", or the special
    /// <c>OVERTIME_SUDDENDEATH</c> sentinel = "Sudden Death". The match layer sets this directly; it supersedes
    /// the boolean <see cref="Overtime"/>. Use <see cref="SetSuddenDeath"/> for the sentinel.
    /// </summary>
    public int Overtimes
    {
        get => _overtimes;
        set { _overtimes = value < 0 ? 0 : value; _fed = true; }
    }

    /// <summary>True when STAT(OVERTIMES) holds the sudden-death sentinel (subtext "Sudden Death").</summary>
    public bool SuddenDeath => _overtimes == OvertimeSuddenDeath;

    /// <summary>Enter (or leave) sudden death (QC overtimes == OVERTIME_SUDDENDEATH).</summary>
    public void SetSuddenDeath(bool on) { _overtimes = on ? OvertimeSuddenDeath : 0; _fed = true; }

    /// <summary>
    /// QC STAT(TIMEOUT_STATUS): 2 = a timeout is active → subtext "Timeout" (takes precedence over overtime).
    /// 0/1 = not paused. The match layer feeds it; defaults to 0.
    /// </summary>
    private int _timeoutStatus;
    public int TimeoutStatus { get => _timeoutStatus; set { _timeoutStatus = value; _fed = true; } }

    private string _subtext = "";
    /// <summary>Optional explicit subtext under the timer (QC subtext). Overrides the warmup/overtime label.</summary>
    public string Subtext { get => _subtext; set { _subtext = value ?? ""; _fed = true; } }

    /// <summary>
    /// QC <c>intermission_time</c>: when &gt; 0 the timer freezes the displayed time at this absolute moment
    /// (the match ended) instead of using the live clock, and warning colors are suppressed. 0 = live. The
    /// match layer sets this when intermission begins.
    /// </summary>
    private double _intermissionTime;
    public double IntermissionTime { get => _intermissionTime; set { _intermissionTime = value; _fed = true; } }

    private double _now = -1.0;
    /// <summary>
    /// The current time on the same clock as <see cref="MatchStartTime"/>. If &lt; 0 (default), the panel
    /// uses the sim clock when available, else its own wall clock. Set it to slave to the match clock.
    /// </summary>
    public double Now { get => _now; set { _now = value; _fed = true; } }

    // --- round subtimer (QC the second smaller timer for round-based gametypes) ---
    // _roundCantStart maps QC STAT(ROUNDSTARTTIME) == -1 ("Round can't start" -> "--:--" in red).
    private bool _roundActive;
    private bool _roundCantStart;
    private double _roundStartTime;   // absolute time the round started (QC ROUNDSTARTTIME)
    private float _roundLimit;        // round time limit in seconds (QC ROUND_TIMELIMIT)
    private string _roundLabel = "";

    private double _localClock;

    // Compile-time fallbacks (used only if the cvar store has not been seeded yet); the live values come from
    // the hud_panel_timer_warning_* cvars (QC autocvars), read each frame so console/menu edits apply.
    private const float DefWarnYellow = 300f;
    private const float DefWarnRed = 60f;

    /// <summary>
    /// Set the round subtimer to a normal running round (QC ROUNDSTARTTIME + ROUND_TIMELIMIT).
    /// <paramref name="startTime"/> is the absolute time (same clock as <see cref="Now"/>) the round started;
    /// <paramref name="limitSeconds"/> is the round time limit (&lt;= 0 = count up). Pass a label like
    /// "Round". Call <see cref="ClearRound"/> to hide it.
    /// </summary>
    public void SetRound(double startTime, float limitSeconds, string label = "Round")
    {
        _roundActive = true;
        _roundCantStart = false;
        _roundStartTime = startTime;
        _roundLimit = limitSeconds;
        _roundLabel = label ?? "";
        _fed = true;
    }

    /// <summary>
    /// Back-compat overload: set the round subtimer by its absolute end time (the old contract). Internally
    /// stored as a count-down from now to <paramref name="endTime"/>; the displayed value updates live.
    /// </summary>
    public void SetRound(double endTime, string label = "Round")
    {
        _roundActive = true;
        _roundCantStart = false;
        // Re-express as start + limit so the live count-down stays correct as the clock advances.
        double now = CurrentTime();
        float left = (float)System.Math.Max(0.0, endTime - now);
        _roundStartTime = now;
        _roundLimit = left;
        _roundLabel = label ?? "";
        _fed = true;
    }

    /// <summary>Show the secondary timer as "round can't start" (QC ROUNDSTARTTIME == -1 → red "--:--").</summary>
    public void SetRoundCantStart(string label = "Round")
    {
        _roundActive = true;
        _roundCantStart = true;
        _roundLabel = label ?? "";
        _fed = true;
    }

    /// <summary>Hide the round subtimer (QC: round not running).</summary>
    public void ClearRound() => _roundActive = false;

    /// <summary>
    /// Register the behavior cvar defaults (QC timer.qh autocvars). Invoked once at boot by
    /// <c>HudConfig.RegisterDefaults</c> via reflection; <c>Register</c> is idempotent so re-running is safe.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        c.Register("hud_panel_timer_dynamichud", "1", CvarFlags.Save);
        c.Register("hud_panel_timer_increment", "0", CvarFlags.Save);
        c.Register("hud_panel_timer_secondary", "1", CvarFlags.Save);
        c.Register("hud_panel_timer_unbound", "0", CvarFlags.Save);
        c.Register("hud_panel_timer_warning_relative", "0", CvarFlags.Save);
        c.Register("hud_panel_timer_warning_relative_red", "1.2", CvarFlags.Save);
        c.Register("hud_panel_timer_warning_relative_yellow", "3", CvarFlags.Save);
        c.Register("hud_panel_timer_warning_red", "60", CvarFlags.Save);
        c.Register("hud_panel_timer_warning_yellow", "300", CvarFlags.Save);
    }

    public override void _Process(double delta)
    {
        _localClock += delta;
        // Redraw only when the displayed second (or layout/fade) changes — the timer text ticks at 1 Hz, so
        // re-recording it every rendered frame was wasted work (3.2-3).
        if (NeedsRedraw())
            QueueRedraw();
    }

    // Last-drawn snapshot for the change gate (alloc-free primitive compare).
    private int _lMain = int.MinValue, _lRound = int.MinValue, _lFlags = -1, _lSub, _lAlpha = -1, _lW, _lH;

    /// <summary>True only when the drawn timer second, round second, state flags, subtext, fade alpha, or
    /// viewport size changed since the last draw — gating the per-frame redraw to ~1×/s (3.2-3).</summary>
    public override bool NeedsRedraw()
    {
        // Unfed → self-blank; nothing to redraw until the first data setter raises _fed (which itself triggers
        // the next redraw via the changed snapshot). _lFlags starts at -1 so the first fed frame always draws.
        if (!_fed) return false;

        double current = CurrentTime();
        bool increment = CountUp || GlobalBool("hud_panel_timer_increment");
        bool unbound = GlobalBool("hud_panel_timer_unbound");
        float timelimit = ActiveTimeLimit();
        bool countUp = increment || timelimit <= 0f;

        int mainSec = countUp
            ? SafeInt(TimeElapsed(current, MatchStartTime, unbound))
            : SafeInt(TimeLeft(current, MatchStartTime, timelimit, unbound));

        int roundSec;
        if (!_roundActive) roundSec = int.MinValue + 1;
        else if (_roundCantStart) roundSec = int.MinValue + 2;
        else
        {
            double rcur = current;
            bool rUp = increment || _roundLimit <= 0f;
            roundSec = rUp
                ? SafeInt(TimeElapsed(rcur, _roundStartTime, unbound))
                : SafeInt(TimeLeft(rcur, _roundStartTime, _roundLimit, unbound));
        }

        // NB: do NOT pack _overtimes into this int by shifting — OVERTIME_SUDDENDEATH (1<<24) would shift past
        // bit 31 and overflow. The exact overtime count is folded into subHash below; the SuddenDeath/Overtime
        // booleans here cover the cheap state changes.
        int flags = (WarmupStage ? 1 : 0) | (Overtime ? 2 : 0) | (countUp ? 4 : 0) | (_roundActive ? 8 : 0)
                  | (SuddenDeath ? 16 : 0) | (TimeoutStatus == 2 ? 32 : 0) | (IntermissionTime > 0 ? 64 : 0)
                  | (_roundCantStart ? 128 : 0) | (Secondary() << 8);
        int subHash = System.HashCode.Combine(Subtext, _roundLabel, _overtimes);
        int alpha = SafeInt(FgColor.A * 255f);   // carries the scoreboard fade — redraw through a fade
        int w = SafeInt(Size2.X), h = SafeInt(Size2.Y);
        if (mainSec == _lMain && roundSec == _lRound && flags == _lFlags && subHash == _lSub
            && alpha == _lAlpha && w == _lW && h == _lH)
            return false;
        _lMain = mainSec; _lRound = roundSec; _lFlags = flags; _lSub = subHash; _lAlpha = alpha; _lW = w; _lH = h;
        return true;
    }

    private double CurrentTime()
    {
        // QC: curtime = (intermission_time ? intermission_time : time)
        if (IntermissionTime > 0.0) return IntermissionTime;
        if (Now >= 0.0) return Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    /// <summary>
    /// QC <c>HUD_Pickup_Time</c> (pickup.qc): the match-clock reading RIGHT NOW in this panel's own display
    /// convention — <c>floor(time − gamestarttime)</c> when incrementing or no timelimit, else the count-down
    /// <c>ceil(timelimit + gamestarttime − time)</c> — formatted <c>seconds_tostring</c>. The pickup panel
    /// samples this ONCE per pickup and freezes it into the slot, so the readout is the game time the event
    /// HAPPENED, not a running age. Null while the panel has no net feed yet (lets the caller fall back).
    /// </summary>
    public string? PickupTimeString()
    {
        if (!_fed) return null;
        double current = CurrentTime();
        float timelimit = ActiveTimeLimit();
        bool increment = CountUp || GlobalBool("hud_panel_timer_increment");
        float v = increment || timelimit <= 0f
            ? (float)System.Math.Floor(current - MatchStartTime)
            : (float)System.Math.Ceiling(timelimit + MatchStartTime - current);
        return FormatSeconds(v);
    }

    /// <summary>QC timelimit selection: warmup uses WARMUP_TIMELIMIT, otherwise TIMELIMIT*60 (seconds).</summary>
    private float ActiveTimeLimit() => WarmupStage ? WarmupTimeLimitSeconds : TimeLimitSeconds;

    // QC HUD_Timer_TimeElapsed: floor(curtime - starttime), clamped to >= 0 unless unbound.
    private static float TimeElapsed(double curtime, double starttime, bool unbound)
    {
        double e = curtime - starttime;
        if (!unbound && e < 0d) e = 0d;
        return (float)System.Math.Floor(e);
    }

    // QC HUD_Timer_TimeLeft: ceil(timelimit + starttime - curtime), clamped to [0, timelimit] unless unbound.
    private static float TimeLeft(double curtime, double starttime, float timelimit, bool unbound)
    {
        double left = timelimit + starttime - curtime;
        if (!unbound)
            left = System.Math.Clamp(left, 0d, timelimit);
        return (float)System.Math.Ceiling(left);
    }

    // QC seconds_tostring handles negatives by prefixing "-"; SecondsToString clamps at 0, so handle the sign
    // here for the unbound pre-match / over-limit case.
    private static string FormatSeconds(float seconds)
    {
        // A non-finite value (junk net feed → NaN/Inf timeleft) must not reach SecondsToString, where (int)NaN
        // would render garbage like "-35791394:-08". Coerce to a sane 0:00.
        if (!float.IsFinite(seconds)) return SecondsToString(0f);
        if (seconds < 0f)
            return "-" + SecondsToString(-seconds);
        return SecondsToString(seconds);
    }

    // Cast a float to int for the redraw change-gate without an out-of-range/NaN cast (a non-finite or
    // huge value from a junk net feed would otherwise produce a platform-dependent/garbage int and thrash
    // the gate). NaN/±Inf → sentinels; finite values clamp to the int range.
    private static int SafeInt(float v)
    {
        if (float.IsNaN(v)) return int.MinValue;
        if (v >= int.MaxValue) return int.MaxValue;
        if (v <= int.MinValue) return int.MinValue;
        return (int)v;
    }

    private static bool GlobalBool(string name) => GlobalF(name, 0f) != 0f;

    private int Secondary() => (int)GlobalF("hud_panel_timer_secondary", 1f);

    /// <summary>
    /// Port of QC <c>HUD_Timer_Color</c>: white normally, yellow under the yellow threshold, red under the
    /// red threshold. Thresholds are absolute seconds (warning_relative 0) or relative to the reference
    /// (warning_relative 1 = the timelimit; 2 = CTS server record, not wired here → falls back to absolute).
    /// </summary>
    private Color TimerColor(float timeleft, float timelimit)
    {
        float limitRed, limitYellow;
        int relative = (int)GlobalF("hud_panel_timer_warning_relative", 0f);
        if (relative == 1 && timelimit > 0f)
        {
            limitRed = timelimit * GlobalF("hud_panel_timer_warning_relative_red", 1.2f);
            limitYellow = timelimit * GlobalF("hud_panel_timer_warning_relative_yellow", 3f);
        }
        else
        {
            // relative 0 (absolute), or relative 2 (CTS server record — unavailable client-side here, so QC's
            // own fall-through to the absolute values applies).
            limitRed = GlobalF("hud_panel_timer_warning_red", DefWarnRed);
            limitYellow = GlobalF("hud_panel_timer_warning_yellow", DefWarnYellow);
        }

        if (timeleft <= limitRed) return new Color(1f, 0f, 0f, FgColor.A);      // red  '1 0 0'
        if (timeleft <= limitYellow) return new Color(1f, 1f, 0f, FgColor.A);   // yellow '1 1 0'
        return FgColor;                                                          // white '1 1 1'
    }

    protected override void DrawPanel()
    {
        // Self-blank: draw NOTHING (not even the skin frame) until the match/net layer feeds state. Prevents a
        // bogus count-up clock on a pure --connect client / GameDemo where the panel is auto-discovered but
        // never wired (contract §6).
        if (!_fed) return;

        // Safety: a degenerate panel rect (zero/negative/NaN size from a transient bad viewport or junk
        // hud_panel_timer_size cvar) would feed NaN through every /3 layout divisor and draw text far off-panel.
        // Bail rather than render garbage (self-blank §2/§3). Size2 components are normally bounded >= 8.
        if (!(float.IsFinite(Size2.X) && float.IsFinite(Size2.Y)) || Size2.X <= 0f || Size2.Y <= 0f) return;

        // QC HUD_Panel_DrawBg paints the skin frame (border_plain_north under luma); no-op when bg "0".
        DrawBackground();

        double current = CurrentTime();
        bool intermission = IntermissionTime > 0.0;
        bool increment = CountUp || GlobalBool("hud_panel_timer_increment");
        bool unbound = GlobalBool("hud_panel_timer_unbound");
        float timelimit = ActiveTimeLimit();

        // --- main timer text + color (QC HUD_Timer body) ---
        float timeleft = TimeLeft(current, MatchStartTime, timelimit, unbound);

        Color timerColor = FgColor;
        if (!intermission && !WarmupStage && timelimit > 0f)
            timerColor = TimerColor(timeleft, timelimit);

        bool mainCountUp = increment || timelimit <= 0f;
        float mainValue = mainCountUp ? TimeElapsed(current, MatchStartTime, unbound) : timeleft;
        string timerStr = FormatSeconds(mainValue);

        // --- secondary round subtimer (QC the ROUNDSTARTTIME block) ---
        int secondary = Secondary();
        bool wantRound = _roundActive && secondary != 0;
        bool swap = secondary == 2 && _roundActive;   // QC: swap = (secondary == 2 && ROUNDSTARTTIME)

        string? subtimerStr = null;
        Color subtimerColor = FgColor;
        if (wantRound)
        {
            if (_roundCantStart)
            {
                subtimerStr = "--:--";
                subtimerColor = new Color(1f, 0f, 0f, FgColor.A);
            }
            else
            {
                float roundLeft = TimeLeft(current, _roundStartTime, _roundLimit, unbound);
                if (!intermission && _roundLimit > 0f)
                    subtimerColor = TimerColor(roundLeft, _roundLimit);

                bool roundCountUp = increment || _roundLimit <= 0f;
                float roundValue = roundCountUp ? TimeElapsed(current, _roundStartTime, unbound) : roundLeft;
                subtimerStr = FormatSeconds(roundValue);
            }
        }

        // --- subtext (QC overtimes / timeout / warmup enumeration) ---
        string subtext = ResolveSubtext();
        bool hasSub = !string.IsNullOrEmpty(subtext);

        // ----- layout (QC: subtext is a third of the height; subtimer is a third of the width) -----
        float subtextH = hasSub ? Size2.Y / 3f : 0f;
        float timerAreaH = Size2.Y - subtextH;

        float timerAreaW = Size2.X;
        float subtimerW = 0f;
        if (subtimerStr is not null)
        {
            subtimerW = Size2.X / 3f;
            timerAreaW = Size2.X - subtimerW;
        }

        // Main timer fills the left/whole timer area. drawstring_aspect centres within the cell; mirror with a
        // height-driven font clamp + centring inside the cell. When swapped, the small timer text occupies the
        // main cell and vice versa (QC swap ternaries).
        string mainCell = swap && subtimerStr is not null ? subtimerStr : timerStr;
        Color mainCellColor = swap && subtimerStr is not null ? subtimerColor : timerColor;
        DrawCellText(new Rect2(0f, 0f, timerAreaW, timerAreaH), mainCell, mainCellColor, 16, 40);

        if (subtimerStr is not null)
        {
            // QC insets the subtimer by 0.2 of its height top & bottom.
            float pad = (Size2.Y / 3f) * 0.2f;
            var sub = new Rect2(timerAreaW, pad, subtimerW, timerAreaH - 2f * pad);
            string smallCell = swap ? timerStr : subtimerStr;
            Color smallColor = swap ? timerColor : subtimerColor;
            DrawCellText(sub, smallCell, smallColor, 10, 22);
        }

        if (hasSub)
        {
            // QC draws the subtext green ('0 1 0'); the warmup-nag / overtime variants stay green like the
            // reference (only the main timer changes color).
            var area = new Rect2(0f, timerAreaH, Size2.X, subtextH);
            DrawCellText(area, subtext, new Color(0f, 1f, 0f, FgColor.A), 9, 22);
        }
    }

    /// <summary>
    /// Port of the QC subtext enumeration block: explicit override wins, then warmup (with the
    /// no-limit/too-few-players/unbalanced nag variants), then timeout, then the overtime ladder
    /// (sudden death / "Overtime" / "Overtime #N").
    /// </summary>
    private string ResolveSubtext()
    {
        if (!string.IsNullOrEmpty(Subtext)) return Subtext;

        if (WarmupStage)
        {
            // QC: with a warmup timelimit it's just "Warmup"; otherwise the no-time-limit message. The richer
            // "too few players"/"teams unbalanced" nags need server stats we don't feed here, so we use the
            // common "no time limit" form (faithful to the timelimit<=0 branch's default).
            return WarmupTimeLimitSeconds > 0f ? "Warmup" : "Warmup: no time limit";
        }

        if (TimeoutStatus == 2) return "Timeout";

        if (_overtimes == OvertimeSuddenDeath) return "Sudden Death";
        if (_overtimes == 1) return "Overtime";
        if (_overtimes >= 2) return $"Overtime #{_overtimes}";

        return "";
    }

    /// <summary>
    /// Center a single line of text inside a cell, picking a font size from the cell height (a stand-in for
    /// QC <c>drawstring_aspect</c>, which scales the string to the cell). The size is clamped to
    /// [<paramref name="minSize"/>, <paramref name="maxSize"/>] and shrunk so the text fits the cell width.
    /// </summary>
    private void DrawCellText(Rect2 cell, string text, Color color, int minSize, int maxSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Reject a degenerate / non-finite cell (NaN width or height would yield a NaN font size and an
        // off-panel draw position). Also normalize a swapped clamp range so Mathf.Clamp can't be fed min>max.
        if (!(float.IsFinite(cell.Size.X) && float.IsFinite(cell.Size.Y)
              && float.IsFinite(cell.Position.X) && float.IsFinite(cell.Position.Y))
            || cell.Size.X <= 0f || cell.Size.Y <= 0f) return;
        if (minSize < 1) minSize = 1;
        if (maxSize < minSize) maxSize = minSize;

        int size = (int)Mathf.Clamp(cell.Size.Y * 0.75f, minSize, maxSize);
        if (size < 1) size = 1;
        // Shrink to fit the cell width (aspect behavior) so long strings like "Overtime #12" don't clip.
        float w = MeasureText(text, size);
        if (w > cell.Size.X && float.IsFinite(w) && w > 0f)
        {
            size = System.Math.Max(minSize, (int)(size * (cell.Size.X / w)));
            if (size < 1) size = 1;
        }

        // Center vertically, but never let a too-tall line spill above the cell top (keeps the draw inside the
        // panel rect — _Draw is not clipped to the Control's bounds).
        float y = cell.Position.Y + System.Math.Max(0f, (cell.Size.Y - size) * 0.5f);
        DrawTextCentered(new Vector2(cell.Position.X, y), cell.Size.X, text, color, size);
    }
}
