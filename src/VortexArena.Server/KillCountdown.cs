using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Engine.Simulation;

namespace VortexArena.Server;

/// <summary>
/// Drives the suicide and team-change countdowns, including the attached digit, announcements,
/// staggered clone countdowns and the final kill or team change (server/clientkill.qc).
/// </summary>
public sealed class KillCountdown
{
    private float _nextIndicatorStart;
    private readonly List<CloneCountdown> _clones = new();
    private sealed class CloneCountdown(Entity body, int count, float start)
    {
        public readonly Entity Body = body;
        public int Count = count;
        public float NextThink = start;
    }

    public System.Func<IReadOnlyList<Entity>> Entities { get; set; } = static () => System.Array.Empty<Entity>();

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
            st.KillCntdownCnt = (int)System.Math.Ceiling(killtime);
            st.KillCntdownDigit = 0;
            st.KillCntdownSilent = st.KillCntdownCnt == 1; // QC stores the bounded initial count as its silent flag.
            float start = System.Math.Max(Now(), _nextIndicatorStart);
            st.KillCntdownNextThink = start + player.Lip * 0.05f;
            _nextIndicatorStart = st.KillCntdownNextThink + 0.05f;
            foreach (Entity body in Entities())
            {
                if (!body.IsFreed && body.ClassName == PlayerBody.ClassName && body.Enemy == player
                    && (body.Effects & CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST) == 0
                    && !_clones.Exists(c => c.Body == body))
                    _clones.Add(new(body, st.KillCntdownCnt, start + PlayerBody.GetCloneOrdinal(body) * 0.05f));
            }
            player.Lip = 0;
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
        st.KillCntdownNextThink = Now() + player.Lip * 0.05f;
        st.KillCntdownDigit = 0;
        player.Lip = 0;
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
        if (GameStopped() || player.IsDead || player.IsObserver || (player.Alpha < 0 && player.Vehicle is null))
        {
            st.KillCntdownActive = false;
            return;
        }

        float now = Now();
        // SV_RunThink clamps a past deadline to current simulation time before invoking QC.
        if (now < st.KillCntdownNextThink)
            return;

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
        if (!st.KillCntdownSilent && st.KillCntdownCnt <= 10)
        {
            st.KillCntdownDigit = (byte)st.KillCntdownCnt;
            if (IsRealClient(player))
                AnnceIfEnabled(player, "NUM_KILL_" + st.KillCntdownCnt);
        }

        st.KillCntdownNextThink = now + 1f;
        st.KillCntdownCnt--;
    }

    public void TickClones()
    {
        for (int i = _clones.Count - 1; i >= 0; i--)
        {
            var c = _clones[i];
            Entity body = c.Body;
            if (!body.IsFreed && !GameStopped() && body.Alpha >= 0)
            {
                if (Now() < c.NextThink) continue;
                if (c.Count > 0)
                {
                    if (c.Count <= 10) body.CountdownIndicator = new((byte)c.Count, 0);
                    c.Count--;
                    c.NextThink = Now() + 1f;
                    continue;
                }
                Combat.Damage(body, body, body, 100000f, DeathTypes.Kill, body.Origin, System.Numerics.Vector3.Zero);
            }
            body.CountdownIndicator = default;
            _clones.RemoveAt(i);
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
    // team index → APP_TEAM_NUM(CENTER_TEAMCHANGE)). The digit uses the same team intent.
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
