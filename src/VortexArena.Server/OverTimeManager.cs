// Port of qcsrc/server/world.qc — the CheckRules_World overtime / sudden-death cascade:
//   InitiateSuddenDeath (1467-1497), InitiateOvertime (1499-1508), GetWinningCode (1510-1536),
//   WinningCondition_Scores (1560-1639), and the suddendeath state the cascade in CheckRules_World
//   (1725-1861) drives. The state globals come from qcsrc/server/world.qh (checkrules_overtimesadded /
//   _suddendeathend / _suddendeathwarning / overtimes) and the constants from common/stats.qh
//   (OVERTIME_SUDDENDEATH = BITS(24)) + server/world.qh (WINNING_* codes).
//
// This class owns the per-match checkrules state and the timelimit→overtime→sudden-death decision logic;
// GameWorld.CheckRulesAndIntermission drives it each tick (the CheckRules_World body), and the gametypes
// supply the per-mode equality (tie) report via GameType.ReportsTie (QC WinningConditionHelper_equality).
//
// STAT(OVERTIMES) (the client HUD "Overtime #N" / "Sudden Death" subtext) is networked via the MatchState
// packet (ServerNet.SendMatchState reads Overtimes → ClientNet.MatchOvertimes → TimerPanel.Overtimes); the
// `overtimes` value tracked here is the source. The one-shot overtime/sudden-death CENTER notifications are
// sent separately (OnOvertimeStarted / OnSuddenDeathStarted → NotificationSystem).

using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// QC <c>WINNING_*</c> (server/world.qh:132-135) — the win-condition return codes the score check produces.
/// </summary>
public enum WinningCode
{
    /// <summary>QC <c>WINNING_NO = 0</c>: no winner, but time limits may terminate the game.</summary>
    No = 0,
    /// <summary>QC <c>WINNING_YES = 1</c>: a winner was found.</summary>
    Yes = 1,
    /// <summary>QC <c>WINNING_NEVER = 2</c>: no winner; enter overtime if the time limit is reached.</summary>
    Never = 2,
    /// <summary>QC <c>WINNING_STARTSUDDENDEATHOVERTIME = 3</c>: no winner; enter sudden-death overtime NOW.</summary>
    StartSuddenDeathOvertime = 3,
}

/// <summary>
/// The global overtime / sudden-death win layer — the C# successor to the slice of <c>CheckRules_World</c>
/// (server/world.qc) that turns a <em>tied</em> timed match into overtime or sudden death instead of an
/// instant draw. It holds the per-match checkrules state (QC the <c>checkrules_*</c> world.qh globals) and
/// ports the four decision functions: <see cref="InitiateSuddenDeath"/>, <see cref="InitiateOvertime"/>,
/// <see cref="GetWinningCode"/>, and the limit/equality resolution that QC's
/// <c>WinningCondition_Scores</c> performs (here split into <see cref="ResolveWinningCode"/> driven by the
/// gametype's tie report). The <see cref="GameWorld"/> drives it each tick.
///
/// Race/CTS qualifying (race_StartCompleting / the g_race branches) is deliberately NOT ported here — those
/// modes never reach this layer in the headless port (they latch their own winner). Campaign is gated out
/// exactly as QC does (no overtime, sudden death ends immediately).
/// </summary>
public sealed class OverTimeManager
{
    /// <summary>QC <c>OVERTIME_SUDDENDEATH = BITS(24)</c> (common/stats.qh:77): the special <see cref="Overtimes"/>
    /// value meaning "sudden death". 1 &lt;&lt; 24 == 0x01000000 == 16777216.</summary>
    public const int OvertimeSuddenDeath = 1 << 24;

    // ---- checkrules state (QC server/world.qh:35-38) -------------------------------------------------

    /// <summary>QC <c>checkrules_overtimesadded</c>: how many normal overtimes have been added so far. The
    /// cascade resets it to -1 (the QC sentinel) when it is about to enter the suddendeath decision, so the
    /// "could a normal overtime be added instead?" guard in <see cref="InitiateSuddenDeath"/> sees the fresh
    /// count.</summary>
    public int OvertimesAdded { get; private set; }

    /// <summary>QC <c>checkrules_suddendeathend</c>: absolute sim time at which sudden death ends (0 = not in
    /// sudden death). Latched by <see cref="InitiateSuddenDeath"/>.</summary>
    public float SuddenDeathEnd { get; private set; }

    /// <summary>QC <c>checkrules_suddendeathwarning</c>: whether the one-shot "Overtime has begun!" center
    /// message has already been broadcast for the current sudden death.</summary>
    public bool SuddenDeathWarning { get; private set; }

    /// <summary>QC <c>overtimes</c> (common/stats.qh:86): overtimes added, or the special
    /// <see cref="OvertimeSuddenDeath"/> while in sudden death. Networked as STAT(OVERTIMES) via the MatchState
    /// packet (ServerNet.SendMatchState → ClientNet.MatchOvertimes → TimerPanel) for the HUD overtime subtext.</summary>
    public int Overtimes { get; private set; }

    /// <summary>True while sudden death is armed (QC <c>checkrules_suddendeathend</c> nonzero).</summary>
    public bool InSuddenDeath => SuddenDeathEnd != 0f;

    /// <summary>
    /// QC <c>cvar_set("timelimit", ...)</c> sink — set by the host so <see cref="InitiateOvertime"/> can
    /// extend the live <c>timelimit</c> cvar (the GameWorld wires this to <see cref="Cvars.Set(string,float)"/>).
    /// Kept injectable so unit tests can observe the extension without a live engine cvar store.
    /// </summary>
    public System.Action<float>? SetTimeLimitCvar;

    /// <summary>
    /// QC the overtime center notification (CENTER_OVERTIME_TIME, <c>autocvar_timelimit_overtime * 60</c>) and
    /// the sudden-death warning (CENTER_OVERTIME_FRAG) — set by the host to route to NotificationSystem. Left
    /// null in unit tests (no-op). The float argument is the overtime duration in <em>seconds</em>.
    /// </summary>
    public System.Action<float>? OnOvertimeStarted;
    public System.Action? OnSuddenDeathStarted;

    /// <summary>Reset all checkrules state for a fresh match (QC the globals being zero at worldspawn).</summary>
    public void Reset()
    {
        OvertimesAdded = 0;
        SuddenDeathEnd = 0f;
        SuddenDeathWarning = false;
        Overtimes = 0;
    }

    // ---- cvar reads (QC autocvar_* — world.qh:28-30 + g_campaign) ------------------------------------

    private static bool Campaign => Api.Services is not null && Api.Cvars.GetFloat("g_campaign") != 0f;

    /// <summary>QC <c>autocvar_timelimit_overtime</c> (xonotic-server.cfg ships 2): minutes per added overtime.</summary>
    public static float TimeLimitOvertime => Cvars.FloatOr("timelimit_overtime", 2f);

    /// <summary>QC <c>autocvar_timelimit_overtimes</c> (xonotic-server.cfg ships 0): how many overtimes to add at
    /// most (&lt;0 = unlimited).</summary>
    public static int TimeLimitOvertimes => (int)Cvars.FloatOr("timelimit_overtimes", 0f);

    /// <summary>QC <c>autocvar_timelimit_suddendeath</c> (xonotic-server.cfg ships 5): sudden-death length, minutes.</summary>
    public static float TimeLimitSuddenDeath => Cvars.FloatOr("timelimit_suddendeath", 5f);

    // ---- the ported decision functions ---------------------------------------------------------------

    /// <summary>
    /// QC <c>InitiateSuddenDeath</c> (world.qc:1467-1497). Decide whether a normal overtime can still be added
    /// (returns <c>true</c> — the caller must call <see cref="InitiateOvertime"/> later) or whether to arm
    /// sudden death now (returns <c>false</c>, latching <see cref="SuddenDeathEnd"/> and
    /// <see cref="Overtimes"/>). Campaign never gets overtime and ends sudden death immediately, exactly as QC.
    /// </summary>
    /// <param name="now">Absolute sim time (QC <c>time</c>).</param>
    public bool InitiateSuddenDeath(float now)
    {
        // QC: a normal overtime is still possible when not in campaign, the running count hasn't gone to the
        // -1 sentinel, there are overtimes left (or the limit is "unlimited" via < 0), and timelimit_overtime
        // is enabled. The race/qualifying clause is omitted (those modes don't reach this layer in the port).
        if (!Campaign && OvertimesAdded >= 0
            && (OvertimesAdded < TimeLimitOvertimes || TimeLimitOvertimes < 0)
            && TimeLimitOvertime != 0f)
        {
            return true; // need to call InitiateOvertime later
        }

        if (SuddenDeathEnd == 0f)
        {
            if (Campaign)
            {
                SuddenDeathEnd = now; // no suddendeath in campaign — end it immediately
            }
            else
            {
                SuddenDeathEnd = now + 60f * TimeLimitSuddenDeath;
                Overtimes = OvertimeSuddenDeath;
            }
        }
        return false;
    }

    /// <summary>
    /// QC <c>InitiateOvertime</c> (world.qc:1499-1508) — ONLY call this if <see cref="InitiateSuddenDeath"/>
    /// returned true. Bumps the overtime count and extends the live <c>timelimit</c> cvar by
    /// <c>timelimit_overtime</c> minutes so clients/commands see the extension, then fires the overtime center
    /// message.
    /// </summary>
    /// <param name="currentTimeLimitMinutes">QC <c>autocvar_timelimit</c> (the live timelimit, minutes).</param>
    public void InitiateOvertime(float currentTimeLimitMinutes)
    {
        ++OvertimesAdded;
        // QC NOTE: overtimes can never be < 0 here, so it is safely an unsigned int stat (networking deferred).
        Overtimes = OvertimesAdded;
        // add one more overtime by simply extending the timelimit (QC cvar_set("timelimit", ...)).
        SetTimeLimitCvar?.Invoke(currentTimeLimitMinutes + TimeLimitOvertime);
        // QC Send_Notification(... CENTER_OVERTIME_TIME, autocvar_timelimit_overtime * 60) — seconds.
        OnOvertimeStarted?.Invoke(TimeLimitOvertime * 60f);
    }

    /// <summary>
    /// QC <c>GetWinningCode</c> (world.qc:1510-1536): map (limit-reached × equality) to a <see cref="WinningCode"/>.
    /// Campaign collapses to a plain limit check (no overtime). Otherwise an equal top-two pair yields
    /// <see cref="WinningCode.StartSuddenDeathOvertime"/> at the limit (a tie at the limit must go to overtime)
    /// or <see cref="WinningCode.Never"/> below it.
    /// </summary>
    public static WinningCode GetWinningCode(bool limitReached, bool equality)
    {
        if (Campaign)
            return limitReached ? WinningCode.Yes : WinningCode.No;

        if (equality)
            return limitReached ? WinningCode.StartSuddenDeathOvertime : WinningCode.Never;

        return limitReached ? WinningCode.Yes : WinningCode.No;
    }

    /// <summary>
    /// The score-check slice of QC <c>WinningCondition_Scores</c> (world.qc:1560-1639), reduced to the inputs
    /// the headless port already computes per gametype: whether the win limit is reached (the gametype's
    /// <c>MatchEnded</c> latch) and whether the top two contenders are tied (the gametype's
    /// <see cref="XonoticGodot.Common.Gameplay.GameType.ReportsTie"/>). While sudden death is running, the QC
    /// fragsleft=1 case means any winning condition closes the match — so a non-tie ends it, exactly as
    /// <see cref="GetWinningCode"/> already yields. Returns the same <see cref="WinningCode"/> QC's
    /// <c>GetWinningCode(topscore &amp;&amp; limit_reached, equality)</c> would.
    /// </summary>
    public static WinningCode ResolveWinningCode(bool limitReached, bool equality)
        => GetWinningCode(limitReached, equality);

    /// <summary>
    /// QC the suddendeath warning emission in <c>CheckRules_World</c> (world.qc:1768-1778): the first tick that
    /// sudden death is armed, broadcast the one-shot "Overtime has begun!" center message (CENTER_OVERTIME_FRAG)
    /// and latch <see cref="SuddenDeathWarning"/> so it fires only once.
    /// </summary>
    public void TickSuddenDeathWarning()
    {
        if (!InSuddenDeath || SuddenDeathWarning)
            return;
        SuddenDeathWarning = true;
        OnSuddenDeathStarted?.Invoke();
    }

    /// <summary>QC <c>checkrules_overtimesadded = -1</c> (world.qc:1830): the cascade arms the suddendeath
    /// decision by pushing the count to the sentinel so <see cref="InitiateSuddenDeath"/> takes the
    /// sudden-death branch (a tie at the limit goes straight to sudden death, not another normal overtime).</summary>
    public void ArmSuddenDeathDecision() => OvertimesAdded = -1;

    /// <summary>QC the final-victory revert (world.qc:1850-1857): if a winner is declared in the very tick
    /// sudden death began, undo the sudden-death latch so the match ends cleanly instead of lingering.</summary>
    /// <param name="overtimesBefore">QC <c>overtimes_prev</c> — the value of <see cref="Overtimes"/> at the top
    /// of this tick.</param>
    public void RevertSuddenDeathIfJustBegun(int overtimesBefore)
    {
        if (Overtimes == OvertimeSuddenDeath && Overtimes != overtimesBefore)
        {
            SuddenDeathEnd = 0f;
            Overtimes = overtimesBefore;
        }
    }

    /// <summary>QC <c>checkrules_suddendeathend = 0</c>: clear sudden death (used when a ready-restart or the
    /// race path cancels it). Exposed for parity / host use.</summary>
    public void ClearSuddenDeath() => SuddenDeathEnd = 0f;
}
