using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Race timer panel — port of Base/.../qcsrc/client/hud/panel/racetimer.qc (HUD panel #8). The QC version is
/// the on-screen race split timer: while you run a Race/CTS lap it shows the running lap time (big, bold,
/// count-up), an "anticipation" split for the upcoming checkpoint (your live delta vs the next-checkpoint
/// record, colored green ahead / red behind), and on every checkpoint pass it flashes a "Checkpoint N
/// (+/-delta vs the record)" split (built by <c>MakeRaceString</c>) for ~2s plus the expanding split time
/// (<c>forcetime</c>) and the penalty line. It reads a pile of networked race stats (<c>race_checkpoint</c>,
/// <c>race_time</c>, <c>race_laptime</c>, <c>race_checkpointtime</c>, the previous-best/next-best/my-best
/// times+names, the per-checkpoint speeds, the penalty fields) that arrive via the <c>TE_CSQC_RACE</c> message.
///
/// Those race stats are a server/net concern, so the net layer drives them through the settable members here
/// (the <c>Race*</c> properties), the same injection model as <see cref="TimerPanel"/>/<see cref="VotePanel"/>.
/// The panel renders, faithfully to racetimer.qc:
/// <list type="bullet">
///   <item>the big bold running lap time (count up from <see cref="RaceLapTime"/>, +penalty accumulator);</item>
///   <item>the most-recent checkpoint split (faded over 2s via <see cref="RaceCheckpointTime"/>), with the
///         speed-at-checkpoint readout (<see cref="ShowCheckpointSpeed"/>, QC <c>cl_race_cptimes_showspeed</c>)
///         colored by the accel/decel progressbar colors;</item>
///   <item>the next-checkpoint anticipation split (your live time vs the next-checkpoint record), only while NOT
///         frozen on a just-passed checkpoint (QC the <c>else</c> branch);</item>
///   <item>the expanding "forcetime" split time (your absolute split, scaling up over 0.5s);</item>
///   <item>the penalty line, faded over its window;</item>
///   <item>the race-award medal flash (<c>race_new*</c> art: fail / newtime PB / new-rank green or yellow /
///         server record), faded over the <see cref="RaceStatusTime"/> window — the icons the contract §6 calls
///         the "checkpoint flash";</item>
///   <item>a pre-race count-down-to-start (<see cref="RaceStartTime"/>), when fed.</item>
/// </list>
/// The faithful <see cref="MakeRaceString"/> split formatter is ported in full (start/finish/checkpoint naming,
/// +/- coloring, lap deltas, opponent name). Times are kept in plain seconds here rather than the QC encoded
/// hundredths, so the encode/decode helpers collapse away; the string formatting still matches QC's
/// <c>TIME_DECIMALS=2</c> deltas (e.g. "+15.42") and <c>mmssth</c> M:SS.HH absolute times.
/// </summary>
public partial class RaceTimerPanel : HudPanel
{
    // ---- networked race stats (QC racetimer.qh globals, set from TE_CSQC_RACE) ----

    /// <summary>QC <c>race_checkpoint</c>: the checkpoint just reached (254 = start, 255/0 = finish).</summary>
    public int RaceCheckpoint { get; set; }

    /// <summary>QC <c>race_laptime</c>: absolute time the current lap started; 0 if not lapping.</summary>
    public double RaceLapTime { get; set; }

    /// <summary>QC <c>race_checkpointtime</c>: absolute time the last checkpoint was hit (drives the split fade).</summary>
    public double RaceCheckpointTime { get; set; }

    /// <summary>QC <c>race_time</c>: your split time (seconds) at the last checkpoint.</summary>
    public float RaceTime { get; set; }

    /// <summary>QC <c>race_previousbesttime</c>: the record split (seconds) at that checkpoint.</summary>
    public float RacePreviousBestTime { get; set; }

    /// <summary>QC <c>race_mypreviousbesttime</c>: your personal-best split (seconds) at that checkpoint.</summary>
    public float RaceMyPreviousBestTime { get; set; }

    /// <summary>QC <c>race_previousbestname</c>: the record holder's name (may carry <c>^N</c>).</summary>
    public string RacePreviousBestName { get; set; } = "";

    /// <summary>QC <c>race_penaltytime</c>: penalty magnitude in tenths of a second; 0 = none.</summary>
    public float RacePenaltyTime { get; set; }

    /// <summary>QC <c>race_penaltyeventtime</c>: absolute time the penalty was applied (drives its fade).</summary>
    public double RacePenaltyEventTime { get; set; }

    /// <summary>QC <c>race_penaltyreason</c>: human-readable penalty reason.</summary>
    public string RacePenaltyReason { get; set; } = "";

    /// <summary>QC <c>spectatee_status == -1</c> guard: while observing, the race timer hides.</summary>
    public bool Observing { get; set; }

    /// <summary>Force count-up display even with no live lap (so the panel can still show a frozen time).</summary>
    public double Now { get; set; } = -1.0;

    // ---- NEW networked race stats for the anticipation split / speed / accumulator / medal flash ----

    /// <summary>QC <c>race_penaltyaccumulator</c>: total accrued penalty time (tenths of a second) added into
    /// the running lap clock. 0 = none.</summary>
    public float RacePenaltyAccumulator { get; set; }

    /// <summary>QC <c>race_nextcheckpoint</c>: the checkpoint you're heading toward (254 = none/finished).</summary>
    public int RaceNextCheckpoint { get; set; }

    /// <summary>QC <c>race_nextbesttime</c>: the record split (seconds) at the NEXT checkpoint; 0 if unknown.</summary>
    public float RaceNextBestTime { get; set; }

    /// <summary>QC <c>race_nextbestname</c>: the holder of the next-checkpoint record (may carry <c>^N</c>).</summary>
    public string RaceNextBestName { get; set; } = "";

    /// <summary>QC <c>race_mybesttime</c>: your personal-best split (seconds) at the NEXT checkpoint; 0 if none.</summary>
    public float RaceMyBestTime { get; set; }

    /// <summary>QC <c>race_timespeed</c>: your speed (qu/s) when you crossed the last checkpoint.</summary>
    public float RaceCheckpointSpeed { get; set; }

    /// <summary>QC <c>race_checkpoint_splits_speed[cp]</c>: the stored best speed at the last checkpoint, used as
    /// the comparison baseline for the +/- speed delta. 0 = no baseline yet.</summary>
    public float RaceCheckpointBestSpeed { get; set; }

    // QC race_status / race_status_name / race_status_time — the race-award medal flash (race_new* art). The
    // net layer raises a status event; we hold+fade it for RaceStatusHold seconds (QC adds 5s to race_status_time).

    /// <summary>QC <c>race_status</c>: -1 none, 0 fail, 1 new personal best (newtime), 2 new rank
    /// (green = you took/kept the rank, yellow = someone else's), 3 new server record.</summary>
    public int RaceStatus { get; set; } = -1;

    /// <summary>QC <c>race_status_name</c>: the name shown under the medal (record holder / you).</summary>
    public string RaceStatusName { get; set; } = "";

    /// <summary>QC <c>count_ordinal(rank)</c> text shown under the medal ("1st", "2nd", …); "" hides it.</summary>
    public string RaceStatusRank { get; set; } = "";

    /// <summary>Absolute time the medal flash expires (QC <c>race_status_time</c> = event-time + 5). Drives the
    /// last-second fade; while <c>RaceStatusTime &gt; now</c> the medal is shown.</summary>
    public double RaceStatusTime { get; set; }

    /// <summary>QC rank-color resolution result: when <see cref="RaceStatus"/> == 2, true = green medal
    /// (you set/kept the rank), false = yellow (someone else). Set by the net layer.</summary>
    public bool RaceStatusRankIsMine { get; set; } = true;

    // ---- countdown-to-start (port helper; not in stock racetimer but the contract §6 asks for it) ----

    /// <summary>Absolute time the race lap is allowed to start (e.g. game-start time). While
    /// <c>now &lt; RaceStartTime</c> the panel shows a big count-down number instead of the lap clock. ≤0 = off.</summary>
    public double RaceStartTime { get; set; } = -1.0;

    private double _localClock;

    // ---- behaviour cvars (registered/read from the shared store; QC cl_race_cptimes_* / hud_*) ----

    /// <summary>HudConfig invokes this by reflection to seed the panel's behaviour-cvar defaults (QC
    /// cl_race_cptimes_* and the racetimer dynamichud flag). Layout/look defaults come from the luma table.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // QC client/main.qh AUTOCVAR_SAVE block — the checkpoint-time display options.
        c.Register("cl_race_cptimes_showspeed", "0", save);       // show speed at the checkpoint
        c.Register("cl_race_cptimes_showspeed_unit", "1", save);  // append the speed unit
        c.Register("cl_race_cptimes_showself", "0", save);        // also show your own time alongside the record
        c.Register("cl_race_cptimes_namesize", "10", save);       // max record-holder name length (chars)
        // QC racetimer.qh — the panel's dynamic-hud scale flag (port reads it as a behaviour cvar).
        c.Register("hud_panel_racetimer_dynamichud", "1", save);
    }

    public override void _Process(double delta)
    {
        _localClock += delta;
        // Redraw every frame WHILE something is animating (the running lap time ticks at millisecond precision;
        // a checkpoint/penalty split fades over ~2s; the medal flash + countdown change every frame), but skip
        // the redraw when the panel is idle — no lap, no fade, nothing changing (3.2-3). In non-race play this
        // panel still ticks _Process, so the idle gate is the win.
        if (NeedsRedraw())
            QueueRedraw();
    }

    private bool _wasAnimating = true;

    /// <summary>True while the lap time, anticipation/checkpoint split, penalty line, medal flash, or countdown
    /// is changing/fading — plus one final redraw on the transition to idle so the last animated frame is
    /// cleared (3.2-3).</summary>
    public override bool NeedsRedraw()
    {
        double now = CurrentTime();
        bool countingDown = !Observing && RaceStartTime > 0.0 && now < RaceStartTime;       // big number ticks
        bool lapRunning = !Observing && RaceLapTime > 0.0 && RaceCheckpoint != 255;         // ms ticks per frame
        bool splitFading = !Observing && RaceCheckpointTime > 0.0
            && (now - RaceCheckpointTime) < 2.0;                                            // alpha fades per frame
        bool anticipating = !Observing && RaceLapTime > 0.0 && RaceNextBestTime != 0f
            && RaceNextCheckpoint != 254;                                                   // live delta vs record
        bool penaltyFading = !Observing && RacePenaltyTime != 0f && (now - RacePenaltyEventTime) < 2.0;
        bool medalFading = !Observing && RaceStatus >= 0 && RaceStatusTime > now;           // race_new* flash
        bool animating = countingDown || lapRunning || splitFading || anticipating || penaltyFading || medalFading;
        if (animating)
        {
            _wasAnimating = true;
            return true;
        }
        if (_wasAnimating)
        {
            _wasAnimating = false; // one last redraw to clear the final animated frame, then go quiet
            return true;
        }
        return false;
    }

    private double CurrentTime()
    {
        if (Now >= 0.0) return Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    // QC TIME_DECIMALS (common/util.qh): deltas print with 2 decimals (centiseconds).
    private const string DeltaFmt = "0.00";

    /// <summary>
    /// Port of racetimer.qc <c>MakeRaceString</c>: build the colored split string for a checkpoint.
    /// <paramref name="cp"/> is the checkpoint number (254=start, 255=finish), <paramref name="mytime"/> the
    /// delta vs the record (negative = ahead), <paramref name="theirtime"/> &lt; 0 means "no comparison yet"
    /// and &gt; 0 means the record is still being anticipated, <paramref name="lapdelta"/> the lap difference,
    /// and <paramref name="theirname"/> the record holder. Times are seconds.
    /// </summary>
    public static string MakeRaceString(int cp, float mytime, float theirtime, float othertime,
        int lapdelta, string theirname)
    {
        string lapstr = "", timestr = "", col = "^7", othercol = "^7", othertimestr = "";
        // QC racetimer.qc:26 — drop the personal-time comparison when there's no record name OR the
        // cl_race_cptimes_showself option is off (the anticipation call site passes mybesttime unconditionally
        // and relies on this gate, mirroring the QC).
        if (string.IsNullOrEmpty(theirname) || !CvarBoolG("cl_race_cptimes_showself"))
            othertime = 0;

        // Sanitize networked deltas: a NaN/Infinity stat would otherwise render "NaN"/"∞" garbage in the
        // panel (and a non-finite value passed to FormatRaceTime is clamped there). Treat non-finite as 0.
        if (!float.IsFinite(mytime)) mytime = 0f;
        if (!float.IsFinite(theirtime)) theirtime = 0f;
        if (!float.IsFinite(othertime)) othertime = 0f;

        if (theirtime == 0f) // goal hit: show the +/- delta colored by sign (QC racetimer.qc:29-72)
        {
            if (mytime > 0f) { timestr = "+" + mytime.ToString(DeltaFmt); col = "^1"; }
            else if (mytime == 0f) { timestr = "+0.0"; col = "^3"; }
            else { timestr = "-" + (-mytime).ToString(DeltaFmt); col = "^2"; }

            if (othertime > 0f) { othertimestr = "+" + othertime.ToString(DeltaFmt); othercol = "^1"; }
            else if (othertime == 0f) { othertimestr = "+0.0"; othercol = "^3"; }
            else { othertimestr = "-" + (-othertime).ToString(DeltaFmt); othercol = "^2"; }

            if (lapdelta > 0) { lapstr = $" (-{lapdelta}L)"; col = "^2"; }
            else if (lapdelta < 0) { lapstr = $" (+{-lapdelta}L)"; col = "^1"; }
        }
        else if (theirtime > 0f) // anticipation: either you're already behind, or the record still ticks
        {
            timestr = mytime >= theirtime
                ? "+" + (mytime - theirtime).ToString(DeltaFmt)
                : FormatRaceTime(theirtime);
            othertimestr = mytime >= othertime
                ? "+" + (mytime - othertime).ToString(DeltaFmt)
                : FormatRaceTime(othertime);
            col = "^3";
            othercol = "^7";
        }

        string cpname = cp switch
        {
            254 => "Start line",
            255 => "Finish line",
            0 => "Finish line",
            _ => $"Checkpoint {cp}",
        };

        // QC racetimer.qc:98-102 shortens an oversized record-holder name (port: a char cap via
        // cl_race_cptimes_namesize, a faithful stand-in for textShortenToWidth) — only when comparing.
        if (!string.IsNullOrEmpty(theirname) && theirtime >= 0f)
            theirname = ShortenName(theirname, (int)GlobalF("cl_race_cptimes_namesize", 10f));

        if (theirtime < 0f)
            return col + cpname;
        if (string.IsNullOrEmpty(theirname))
            return $"{col}{cpname} ({timestr})";
        // QC racetimer.qc:108-111 — when a personal-best comparison exists, show it (othercol) before the
        // record delta; otherwise just the record delta + holder.
        if (othertime != 0f)
            return $"{col}{cpname} {othercol}({othertimestr}){col} ({timestr} {theirname}{col}{lapstr})";
        return $"{col}{cpname} ({timestr} {theirname}{col}{lapstr})";
    }

    /// <summary>Trim a (possibly <c>^N</c>-coded) record-holder name to at most <paramref name="maxChars"/>
    /// visible characters (QC <c>textShortenToWidth</c> approximation), preserving color codes.</summary>
    private static string ShortenName(string name, int maxChars)
    {
        if (maxChars <= 0) return name;
        if (HudText.Strip(name).Length <= maxChars) return name;
        var sb = new System.Text.StringBuilder();
        int visible = 0;
        for (int i = 0; i < name.Length && visible < maxChars; i++)
        {
            char ch = name[i];
            if (ch == '^' && i + 1 < name.Length)
            {
                char n = name[i + 1];
                if (n == '^') { sb.Append("^^"); i++; visible++; continue; }
                if (n >= '0' && n <= '9') { sb.Append('^').Append(n); i++; continue; }
                if ((n == 'x' || n == 'X') && i + 4 < name.Length
                    && IsHex(name[i + 2]) && IsHex(name[i + 3]) && IsHex(name[i + 4]))
                { sb.Append(name, i, 5); i += 4; continue; }
            }
            sb.Append(ch);
            visible++;
        }
        sb.Append("...");
        return sb.ToString();
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    protected override void DrawPanel()
    {
        if (Observing) return; // QC spectatee_status == -1 guard

        DrawBackground();

        double now = CurrentTime();
        float pad = Cfg.Padding; // QC panel_bg_padding (live), not the compile-time fallback

        // QC forces a 4:1 aspect inside the panel and centers it; mirror that so text rows line up with stock.
        Rect2 inner = AspectInner(pad);
        float x = inner.Position.X;
        float y = inner.Position.Y;
        float w = inner.Size.X;
        float h = inner.Size.Y;

        // --- pre-race count-down-to-start (port helper): big number until the lap is armed ---
        if (RaceStartTime > 0.0 && now < RaceStartTime && RaceLapTime <= 0.0)
        {
            int secs = (int)Mathf.Ceil((float)(RaceStartTime - now));
            if (secs < 0) secs = 0;
            int bigc = (int)Mathf.Clamp(h * 0.6f, 18f, 64f);
            string label = secs > 0 ? secs.ToString() : "GO!";
            Color cc = secs > 0 ? new Color(1f, 1f, 0.3f, LiveFgAlpha) : new Color(0.3f, 1f, 0.3f, LiveFgAlpha);
            DrawTextCentered(new Vector2(x, y + h * 0.2f), w, label, cc, bigc);
            DrawMedalFlash(now, inner); // a medal can flash even during countdown (e.g. server record on lap end)
            return;
        }

        int splitSz = (int)Mathf.Clamp(h * 0.2f, 9f, 22f);   // QC the split rows use 0.2*mySize.y
        float forceFade = 1f;                                 // expanding-split alpha (the big absolute time)
        string forcetime = "";

        if (RaceCheckpointTime > 0.0)
        {
            // ===== FROZEN branch: a checkpoint was just passed — show its split + speed (QC race_checkpointtime) =====
            float a = Mathf.Clamp(2f - (float)(now - RaceCheckpointTime), 0f, 1f);
            string split = "";
            if (a > 0f && RaceCheckpoint != 254 && RaceTime != 0f)
            {
                split = (RacePreviousBestTime != 0f)
                    ? MakeRaceString(RaceCheckpoint, RaceTime - RacePreviousBestTime, 0f,
                        CvarBoolG("cl_race_cptimes_showself") && RaceMyPreviousBestTime != 0f
                            ? RaceTime - RaceMyPreviousBestTime : 0f,
                        0, RacePreviousBestName)
                    : MakeRaceString(RaceCheckpoint, 0f, -1f, 0f, 0, RacePreviousBestName);

                if (CvarBoolG("cl_race_cptimes_showspeed"))
                    split += FormatCheckpointSpeed();

                forcetime = FormatRaceTime(RaceTime);
            }

            if (split != "" && a > 0f)
                DrawColoredCentered(new Vector2(x, y + h * 0.6f - splitSz * 0.5f), w, split,
                    new Color(1f, 1f, 1f, FgColor.A * a), splitSz);

            // QC: the forcetime split expands in over 0.5s after the checkpoint.
            if (forcetime != "")
                forceFade = Mathf.Clamp((float)(now - RaceCheckpointTime) / 0.5f, 0f, 1f);
        }
        else
        {
            // ===== ANTICIPATION branch: live delta vs the next-checkpoint record (QC the else block) =====
            if (RaceLapTime > 0.0 && RaceNextCheckpoint != 254 && RaceNextBestTime != 0f)
            {
                double penaltyNow = RacePenaltyAccumulator * 0.1; // accumulator is in tenths
                float a = Mathf.Clamp(
                    2f - (float)((RaceLapTime + RaceNextBestTime) - (now + penaltyNow)), 0f, 1f);
                float a2 = RaceMyBestTime != 0f
                    ? Mathf.Clamp(2f - (float)((RaceLapTime + RaceMyBestTime) - (now + penaltyNow)), 0f, 1f)
                    : 0f;
                if (a > 0f)
                {
                    float myLap = (float)((now + penaltyNow) - RaceLapTime);
                    string split = MakeRaceString(RaceNextCheckpoint, myLap, RaceNextBestTime,
                        a2 > 0f ? RaceMyBestTime : 0f, 0, RaceNextBestName);
                    DrawColoredCentered(new Vector2(x, y + h * 0.6f - splitSz * 0.5f), w, split,
                        new Color(1f, 1f, 1f, FgColor.A * a), splitSz);
                }
            }
        }

        // --- penalty line, faded over its window (QC: a = bound(0, 2 - (time - race_penaltyeventtime), 1)) ---
        if (RacePenaltyTime != 0f)
        {
            float a = Mathf.Clamp(2f - (float)(now - RacePenaltyEventTime), 0f, 1f);
            if (a > 0f)
            {
                string s = $"^1PENALTY: {(RacePenaltyTime * 0.1f):0.0} ({RacePenaltyReason})";
                DrawColoredCentered(new Vector2(x, y + h * 0.8f - splitSz * 0.5f), w, s,
                    new Color(1f, 1f, 1f, FgColor.A * a), splitSz);
            }
        }

        // --- the expanding "forcetime" absolute split (QC drawstring_expanding, 0.6*mySize.y, scales in 0.5s) ---
        if (forcetime != "")
        {
            int sz = (int)Mathf.Clamp(h * 0.6f, 12f, 56f);
            // expanding = grow from ~0.7x to 1.0x while fading in (approximation of drawstring_expanding).
            int sz2 = (int)(sz * Mathf.Lerp(0.7f, 1f, forceFade));
            DrawTextCentered(new Vector2(x, y + (h - sz2) * 0.5f), w, forcetime,
                new Color(1f, 1f, 1f, FgColor.A * forceFade), sz2);
        }
        // --- running lap time, big and bold (QC: TIME from time + accumulator - race_laptime) ---
        else if (RaceLapTime > 0.0 && RaceCheckpoint != 255)
        {
            float lap = (float)((now + RacePenaltyAccumulator * 0.1) - RaceLapTime);
            if (lap < 0f) lap = 0f;
            int big = (int)Mathf.Clamp(h * 0.6f, 14f, 56f);
            DrawTextCentered(new Vector2(x, y + (h - big) * 0.5f), w, FormatRaceTime(lap), FgColor, big);
        }

        // --- race-award medal flash (race_new* art): the contract §6 "checkpoint flash" ---
        DrawMedalFlash(now, inner);
    }

    /// <summary>QC the 4:1-aspect inner rect: shrink the padded panel to a 4:1 box, centered (racetimer.qc:186).</summary>
    private Rect2 AspectInner(float pad)
    {
        float px = pad, py = pad;
        float mw = Mathf.Max(1f, Size2.X - pad * 2f);
        float mh = Mathf.Max(1f, Size2.Y - pad * 2f);
        float nw, nh;
        if (mw / mh > 4f) { nw = 4f * mh; nh = mh; px += (mw - nw) * 0.5f; }
        else { nh = 0.25f * mw; nw = mw; py += (mh - nh) * 0.5f; }
        return new Rect2(px, py, nw, nh);
    }

    /// <summary>QC racetimer.qc: the speed-at-checkpoint suffix " ^7&lt;speed&gt;&lt;unit&gt; &lt;col&gt;(±delta&lt;unit&gt;)".
    /// The delta is colored by the accel (faster) / accel-neg (slower) progressbar colors, yellow when equal.</summary>
    private string FormatCheckpointSpeed()
    {
        int unit = (int)GlobalF("hud_speed_unit", 1f);
        float conv = SpeedUnitFactor(unit);
        string unitText = CvarBoolG("cl_race_cptimes_showspeed_unit") ? SpeedUnitLabel(unit) : "";
        // Guard the draw path against a non-finite networked speed: a NaN/Infinity here would render a garbage
        // (or min-int) speed readout. Treat non-finite as 0.
        float curSpeed = float.IsFinite(RaceCheckpointSpeed) ? RaceCheckpointSpeed : 0f;
        float bestSpeed = float.IsFinite(RaceCheckpointBestSpeed) ? RaceCheckpointBestSpeed : 0f;
        int speed = (int)(curSpeed * conv);

        if (bestSpeed != 0f && RaceTime != 0f && RacePreviousBestTime != 0f)
        {
            int diff = Mathf.RoundToInt(curSpeed - bestSpeed);
            string col = diff > 0
                ? RgbToHex(GlobalStr("hud_progressbar_acceleration_color"), 0.2f, 0.65f, 0.93f)
                : diff < 0
                    ? RgbToHex(GlobalStr("hud_progressbar_acceleration_neg_color"), 0.86f, 0.35f, 0f)
                    : "^3";
            int dconv = (int)(diff * conv);
            string sign = dconv >= 0 ? "+" : "";
            return $" ^7{speed}{unitText} {col}({sign}{dconv}{unitText})";
        }
        return $" ^7{speed}{unitText}";
    }

    /// <summary>Draw the race-award medal flash (QC cl_race.qc: race_new* art, faded over the last second).
    /// The medal sits in the upper-right square of the inner box, name + ordinal under it.</summary>
    private void DrawMedalFlash(double now, Rect2 inner)
    {
        if (RaceStatus < 0 || RaceStatusTime <= now) return;
        float a = Mathf.Clamp((float)(RaceStatusTime - now), 0f, 1f); // QC bound(0, race_status_time - time, 1)
        if (a <= 0f) return;

        // medal art name (QC race_status: 0 fail, 1 newtime, 2 rank green/yellow, 3 server record).
        string art = RaceStatus switch
        {
            0 => "race_newfail",
            1 => "race_newtime",
            2 => RaceStatusRankIsMine ? "race_newrankgreen" : "race_newrankyellow",
            3 => "race_newrecordserver",
            _ => "",
        };
        if (art == "") return;

        float sq = Mathf.Min(inner.Size.Y, inner.Size.X * 0.25f);
        if (sq < 8f) sq = 8f;
        // upper-right of the inner box.
        var rect = new Rect2(inner.Position.X + inner.Size.X - sq, inner.Position.Y, sq, sq);
        var mod = new Color(1f, 1f, 1f, LiveFgAlpha * a);
        if (!DrawSkinPic(art, rect, mod))
        {
            // fallback so the flash never vanishes silently: a colored medal disc.
            Color disc = RaceStatus switch
            {
                0 => new Color(0.8f, 0.2f, 0.1f, mod.A),
                3 => new Color(1f, 0.55f, 0f, mod.A),
                2 => RaceStatusRankIsMine ? new Color(0.3f, 1f, 0.3f, mod.A) : new Color(1f, 0.9f, 0.2f, mod.A),
                _ => new Color(0.6f, 0.8f, 1f, mod.A),
            };
            DrawCircleFill(rect.GetCenter(), sq * 0.4f, disc);
        }

        // name + ordinal under the medal (QC drawcolorcodedstring_aspect / drawstring_aspect).
        int sz = (int)Mathf.Clamp(sq * 0.18f, 8f, 18f);
        if (!string.IsNullOrEmpty(RaceStatusName))
            DrawColoredCentered(new Vector2(rect.Position.X, rect.Position.Y + sq * 0.78f), sq,
                RaceStatusName, new Color(1f, 1f, 1f, LiveFgAlpha * a), sz);
        if (!string.IsNullOrEmpty(RaceStatusRank))
            DrawColoredCentered(new Vector2(rect.Position.X, rect.Position.Y + sq * 0.12f), sq,
                RaceStatusRank, new Color(1f, 1f, 1f, LiveFgAlpha * a), sz);
    }

    private void DrawCircleFill(Vector2 c, float r, Color col) => DrawCircle(c, r, col);

    /// <summary>QC <c>TIME_ENCODED_TOSTRING</c> (mmssth, non-compact): M:SS.HH (hundredths, 2 decimals).</summary>
    private static string FormatRaceTime(float seconds)
    {
        // Guard the draw path: NaN/Infinity (a bad networked stat or a delta computed off an uninitialized
        // time) would overflow RoundToInt / the int math below and throw every frame. Clamp to a sane window
        // (max ~99:59.99 so the int multiply can't overflow), and treat non-finite / negative as 0.
        if (!float.IsFinite(seconds) || seconds < 0f) return "0:00.00";
        if (seconds > 5999.99f) seconds = 5999.99f;
        int hundredths = Mathf.RoundToInt(seconds * 100f);
        int minutes = hundredths / 6000;
        int rem = hundredths - minutes * 6000;
        int s = rem / 100;
        int hh = rem % 100;
        return $"{minutes}:{s:D2}.{hh:D2}";
    }

    /// <summary>QC <c>GetSpeedUnitFactor</c> (client/main.qc): qu/s -> selected unit (1=qu/s..5=knots).</summary>
    private static float SpeedUnitFactor(int unit) => unit switch
    {
        2 => 0.0254f,
        3 => 0.0254f * 3.6f,
        4 => 0.0254f * 3.6f * 0.6213711922f,
        5 => 0.0254f * 1.943844492f,
        _ => 1.0f,
    };

    /// <summary>QC <c>GetSpeedUnit</c> (client/main.qc): the unit label (with leading space, as QC concatenates).</summary>
    private static string SpeedUnitLabel(int unit) => unit switch
    {
        2 => " m/s",
        3 => " km/h",
        4 => " mph",
        5 => " knots",
        _ => " qu/s",
    };

    /// <summary>Read a global bool cvar from the shared store (the unset-vs-0 guard via <see cref="GlobalF"/>).</summary>
    private static bool CvarBoolG(string name) => GlobalF(name, 0f) != 0f;

    /// <summary>Parse an "r g b" cvar (0..1) into a Godot color-code escape (QC <c>rgb_to_hexcolor</c> equivalent:
    /// emit a <c>^xRGB</c> hex-digit color tag HudText understands), falling back to the given default rgb.</summary>
    private static string RgbToHex(string s, float dr, float dg, float db)
    {
        float r = dr, g = dg, b = db;
        if (TryParseRgb(s, out Color c)) { r = c.R; g = c.G; b = c.B; }
        int HexDigit(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 15f), 0, 15);
        return $"^x{HexDigit(r):X}{HexDigit(g):X}{HexDigit(b):X}";
    }

    /// <summary>Draw a (possibly <c>^N</c>/<c>^xRGB</c> color-coded) line horizontally centered within
    /// <paramref name="width"/> (QC drawcolorcodedstring centered).</summary>
    private void DrawColoredCentered(Vector2 pos, float width, string line, Color baseColor, int size)
    {
        if (string.IsNullOrEmpty(line)) return;
        var runs = HudText.Parse(line, baseColor);
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        float cx = pos.X + (width - total) * 0.5f;
        if (cx < pos.X) cx = pos.X;
        foreach (HudText.Run r in runs)
        {
            DrawText(new Vector2(cx, pos.Y), r.Text, r.Color, size);
            cx += MeasureText(r.Text, size);
        }
    }
}
