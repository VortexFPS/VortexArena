using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// Server-side port of the <c>kill</c> / team-change countdown from <c>qcsrc/server/clientkill.qc</c>
/// (<c>ClientKill_TeamChange</c> + <c>KillIndicator_Think</c>). QC defers the actual death by
/// <c>g_balance_kill_delay</c> seconds and, each whole second, plays the spoken kill-countdown announcer
/// (<c>Announcer_PickNumber(CNT_KILL, cnt)</c> → <c>ANNCE_NUM_KILL_n</c>) and shows the
/// <c>CENTER_TEAMCHANGE_*</c> countdown center-print (<c>^COUNT</c>) — the two presentation layers this driver
/// makes live. The floating digit entity (QC <c>setmodel(MDL_NUM)</c>) is not networked here; the countdown
/// state lives on <see cref="ServerPlayerState"/> (<c>KillCntdown*</c>, the QC <c>.killindicator</c> /
/// <c>.cnt</c> / <c>.nextthink</c> subset) and is driven once per server frame from
/// <c>GameWorld.OnStartFrame</c>.
///
/// <para>Faithful timing (QC): the indicator's first think fires ~immediately with <c>cnt = ceil(killtime)</c>,
/// announces that number, then re-arms <c>nextthink = time + 1</c> and decrements; when <c>cnt &lt;= 0</c> the
/// real kill fires. So <c>g_balance_kill_delay 2</c> speaks "two", then "one", then kills — total 2 s.</para>
///
/// <para>Scope: this driver covers the suicide/team-change <b>presentation</b> countdown (the two DEAD parity
/// rows sv-clientkill.announcer.number and sv-clientkill.centerprint.teamchange). The kill-indicator entity,
/// the antispam carry-forward, the clones copy, and the ClientKill mutator gate remain out of scope (separate
/// parity rows); the eventual death itself is delegated to <see cref="PerformKill"/>.</para>
/// </summary>
public sealed class KillCountdown
{
    // === context sinks (host-wired so the driver stays Godot-free + unit-testable) ===

    /// <summary>Current sim time (QC <c>time</c>).</summary>
    public System.Func<float> Now { get; set; } = static () => 0f;

    /// <summary>QC <c>autocvar_g_balance_kill_delay</c> (shipped 2): countdown seconds before the death lands.</summary>
    public System.Func<float> KillDelay { get; set; } = static () => 2f;

    /// <summary>QC <c>autocvar_g_balance_kill_antispam</c> (shipped 5; XPM/XDF 0): seconds added to the
    /// next-allowed-kill time on a repeat `kill`, clamped to ≤ 2 s during an unstarted round (clientkill.qc:147-149).</summary>
    public System.Func<float> KillAntispam { get; set; } = static () => 5f;

    /// <summary>QC <c>MUTATOR_CALLHOOK(ClientKill, this, killtime)</c> (clientkill.qc:101): returns true to ABORT the
    /// whole kill (e.g. freezetag returns STAT(FROZEN) so a frozen player can't self-kill); otherwise it may
    /// rewrite <paramref name="killtime"/> via the ref (cts forces 0; race forces 0 while qualifying). Default = no
    /// hook (never aborts, leaves killtime untouched).</summary>
    public KillHook ClientKillHook { get; set; } = static (Player _, ref float _) => false;

    /// <summary>QC <c>ClientKill_Now_TeamChange</c> (clientkill.qc:18): resolve a deferred team-change/spectate intent
    /// (-2 = observer, &gt;0 = move to that team). Wired by the host; the team move itself does the kill.</summary>
    public System.Action<Player, int> PerformTeamChange { get; set; } = static (_, _) => { };

    /// <summary>QC the <c>MUTATOR_CALLHOOK(ClientKill, this, killtime)</c> delegate shape: out-param the (possibly
    /// rewritten) kill delay, return true to forbid the kill entirely.</summary>
    public delegate bool KillHook(Player player, ref float killtime);

    /// <summary>QC the round-active-but-not-started clamp (<c>round_handler_IsActive() &amp;&amp; !round_handler_IsRoundStarted()</c>):
    /// when true the countdown is clamped to ≤ 1 s (clientkill.qc:105-106).</summary>
    public System.Func<bool> RoundActiveNotStarted { get; set; } = static () => false;

    /// <summary>QC <c>game_stopped</c> — no countdown is armed and a live one is torn down (KillIndicator_Think:63).</summary>
    public System.Func<bool> GameStopped { get; set; } = static () => false;

    /// <summary>Per-player countdown state (QC the <c>.killindicator</c>/<c>.cnt</c>/<c>.nextthink</c> fields).</summary>
    public System.Func<Player, ServerPlayerState> StateOf { get; set; } = static _ => new ServerPlayerState();

    /// <summary>The actual death (QC <c>ClientKill_Now</c> → <c>Damage(this,this,this,100000,DEATH_KILL,…)</c>).
    /// The host wires this to the same self-kill the old instant CmdKill ran.</summary>
    public System.Action<Player> PerformKill { get; set; } = static _ => { };

    /// <summary>QC <c>IS_REAL_CLIENT</c> — only real clients get the announcer / center-print (bots are skipped).</summary>
    public System.Func<Player, bool> IsRealClient { get; set; } = static p => !p.IsBot;

    /// <summary>
    /// QC <c>ClientKill_TeamChange</c> presentation subset (clientkill.qc:94-208): begin (or refresh) the
    /// suicide / team-change countdown for <paramref name="player"/>. <paramref name="targetTeam"/> follows the QC
    /// convention 0 = suicide, -2 = spectate, &gt;0 = a specific team (selects the matching CENTER_TEAMCHANGE_*
    /// string). Returns false (and kills immediately via <see cref="PerformKill"/>) when the effective killtime is
    /// ≤ 0, so the caller can rely on the death always being scheduled or already done.
    /// </summary>
    public bool Begin(Player player, int targetTeam = 0)
    {
        // QC clientkill.qc:96 — bail if the match is over.
        if (GameStopped())
            return false;

        float killtime = KillDelay();

        // QC clientkill.qc:101-103: the ClientKill mutator hook. A mutator may forbid the kill entirely (freezetag
        // returns STAT(FROZEN) for a frozen player) or rewrite killtime (cts forces 0; race forces 0 while
        // qualifying). On an abort nothing happens — no countdown, no death.
        if (ClientKillHook(player, ref killtime))
            return false;

        // QC clientkill.qc:105-106: clamp to ≤ 1 s while a round is active but not yet started.
        if (RoundActiveNotStarted())
            killtime = System.Math.Min(killtime, 1f);

        ServerPlayerState st = StateOf(player);

        // QC clientkill.qc:133 — stash the deferred intent the countdown resolves on expiry.
        st.KillCntdownTeamChange = targetTeam;

        // QC clientkill.qc:136-140: if killtime<=0 and a *silent* killindicator already exists (count==1, the CTS
        // finish indicator), kill instantly. This is the only path that resolves a queued silent indicator early.
        if (killtime <= 0f && st.KillCntdownActive && st.KillCntdownSilent)
        {
            PerformKillNow(player, st);
            return false;
        }

        // QC clientkill.qc:142 `if (!this.killindicator)`: only arm a fresh countdown when none is running — a
        // repeated `kill` keeps the existing cnt (it does not restart the clock). The center-print is still
        // (re)sent below from the live cnt, exactly like the unconditional `if (this.killindicator)` block.
        if (!st.KillCntdownActive)
        {
            // QC clientkill.qc:144-151: the anti-spam carry-forward — only while alive. Raise this kill's delay by
            // any still-pending next-allowed-kill window so mashing `kill` extends (never shortcuts) the countdown,
            // then push the next-allowed-kill time out by killtime + the antispam window.
            if (!player.IsDead)
            {
                killtime = System.Math.Max(killtime, st.KillCntdownNextTime - Now());
                float antispam = KillAntispam();
                if (RoundActiveNotStarted())
                    antispam = System.Math.Min(antispam, 2f);
                st.KillCntdownNextTime = Now() + killtime + antispam;
            }

            // QC clientkill.qc:153 — killtime<=0 || not a live player || already dead → kill now (no countdown).
            if (killtime <= 0f || player.IsDead)
            {
                PerformKillNow(player, st);
                return false;
            }

            // QC clientkill.qc:169-170: cnt = ceil(killtime); the indicator's count is bound(0, cnt, 10).
            st.KillCntdownActive = true;
            st.KillCntdownSilent = false;
            st.KillCntdownCnt = (int)System.Math.Ceiling(killtime);
            // QC: nextthink = starttime + lip*0.05 ≈ now (lip staggering is a cosmetic clones detail, not modeled);
            // the first think fires on the next driven tick, announcing the full cnt.
            st.KillCntdownNextThink = Now();
        }

        // QC clientkill.qc:188-207: the CENTER_TEAMCHANGE_* print, sent with the live count when cnt > 0 and the
        // target is a real client. (A silent indicator never reaches Begin's print path in QC.)
        if (IsRealClient(player) && st.KillCntdownCnt > 0)
        {
            string? notif = TeamChangeCenterName(targetTeam);
            if (notif is not null)
                NotificationSystem.Send(NotifBroadcast.OneOnly, player, MsgType.Center, notif, st.KillCntdownCnt);
        }

        return true;
    }

    /// <summary>
    /// QC <c>ClientKill_Silent(this, _delay)</c> (clientkill.qc:212): spawn/reuse a *silent* killindicator
    /// (count==1 → no announcer, center-print, or digit) that ticks <c>ceil(_delay)</c> seconds and then runs the
    /// real kill. Used by the CTS finish to silently kill the runner so they can't keep their speed. A delay ≤ 0
    /// kills on the next tick (QC ceil(0)=0 → cnt<=0 → ClientKill_Now). The caller is responsible for the Base
    /// gate <c>if (autocvar_g_cts_finish_kill_delay)</c> (0 = don't kill at all).
    /// </summary>
    public void BeginSilent(Player player, float delay, int targetTeam = 0)
    {
        if (GameStopped())
            return;
        ServerPlayerState st = StateOf(player);
        st.KillCntdownTeamChange = targetTeam;
        st.KillCntdownActive = true;
        st.KillCntdownSilent = true;            // QC count = 1
        st.KillCntdownCnt = (int)System.Math.Ceiling(delay);
        st.KillCntdownNextThink = Now();
    }

    // QC ClientKill_Now (clientkill.qc:36-60): the lethal step. If a team-change intent is queued, resolve it (the
    // move itself does the kill); otherwise run the self-kill Damage. Clears the indicator either way.
    private void PerformKillNow(Player player, ServerPlayerState st)
    {
        st.KillCntdownActive = false;
        st.KillCntdownSilent = false;
        int target = st.KillCntdownTeamChange;
        st.KillCntdownTeamChange = 0;
        if (target != 0)
            PerformTeamChange(player, target);   // QC ClientKill_Now_TeamChange (-2 spectate / >0 team move)
        else
            PerformKill(player);
    }

    /// <summary>
    /// QC <c>KillIndicator_Think</c> (clientkill.qc:61-89), driven once per server frame for every player with an
    /// armed countdown. On each crossed whole second it plays the spoken kill number (<c>ANNCE_NUM_KILL_n</c>,
    /// gated on the notification's shipped Enabled flag exactly like the game-start countdown), decrements, and on
    /// <c>cnt &lt;= 0</c> fires the real kill. Tears the countdown down if the match stops.
    /// </summary>
    public void Tick(Player player)
    {
        ServerPlayerState st = StateOf(player);
        if (!st.KillCntdownActive)
            return;

        // QC KillIndicator_Think:63 — game_stopped, or the owner already left the live state (QC owner.alpha < 0:
        // dead / observing), tears the indicator down without killing.
        if (GameStopped() || player.IsDead || player.IsObserver)
        {
            st.KillCntdownActive = false;
            return;
        }

        float now = Now();
        // Drive the per-second think(s): QC nextthink = time + 1 each tick. Catch up if multiple seconds elapsed.
        while (st.KillCntdownActive && now >= st.KillCntdownNextThink)
        {
            // QC: cnt <= 0 → ClientKill_Now (the death — or the deferred team change — lands).
            if (st.KillCntdownCnt <= 0)
            {
                PerformKillNow(player, st);
                return;
            }

            // QC clientkill.qc:77 `if (this.count != 1)`: a silent indicator (CTS finish) plays no announcer.
            // Otherwise QC clientkill.qc:79-85: announce the current number (cnt <= 10) to a real client. The
            // announcer is gated on the notification's Enabled flag (NUM_KILL is shipped disabled — N___NEVER in
            // Base — so it stays silent by default but becomes live the instant the player enables it).
            if (!st.KillCntdownSilent && st.KillCntdownCnt <= 10 && IsRealClient(player))
                AnnceIfEnabled(player, "NUM_KILL_" + st.KillCntdownCnt);

            st.KillCntdownNextThink += 1f;
            st.KillCntdownCnt--;
        }
    }

    // QC Announcer_PickNumber(CNT_KILL, cnt) → ANNCE_NUM_KILL_n, gated on the shipped Enabled flag (mirrors
    // GameWorld.AnnceIfEnabled): NotificationSystem.Send does not skip a disabled announcer, so gate here.
    private static void AnnceIfEnabled(Player player, string bareName)
    {
        Notification? n = Notifications.ByName(MsgType.Annce, bareName);
        if (n is { Enabled: true })
            NotificationSystem.Send(NotifBroadcast.OneOnly, player, MsgType.Annce, bareName);
    }

    // QC clientkill.qc:191-205: map targetteam → the CENTER_TEAMCHANGE_* notification (0 suicide / -2 spectate /
    // team index → APP_TEAM_NUM(CENTER_TEAMCHANGE)). The colormod (black/grey/team RGB) is a digit-entity detail
    // not networked here.
    private static string? TeamChangeCenterName(int targetTeam) => targetTeam switch
    {
        0 => "TEAMCHANGE_SUICIDE",
        -2 => "TEAMCHANGE_SPECTATE",
        Teams.Red => "TEAMCHANGE_RED",
        Teams.Blue => "TEAMCHANGE_BLUE",
        Teams.Yellow => "TEAMCHANGE_YELLOW",
        Teams.Pink => "TEAMCHANGE_PINK",
        _ => null,
    };
}
