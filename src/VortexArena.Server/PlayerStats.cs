using System.Text;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// The server player-stats game report — the Godot-free essence of common/playerstats.qc's GameReport
/// pipeline (Init / AddPlayer / AddTeam / AddEvent / Event accumulator / FinalizePlayer / GameReport). It
/// accumulates per-player and per-team event tallies over a match (alivetime, wins, matches, joins, per-weapon
/// accuracy, anticheat, the score columns) keyed by the exact XonStat event-id strings, and serializes them
/// into the "format version 9" report (<see cref="BuildReport"/>). The actual HTTP upload is an engine concern
/// and is intentionally outside this Godot-free core — <see cref="GameReport"/> builds the report and clears
/// <see cref="DelayMapVote"/> synchronously (in QC the async upload callback clears it), so map voting is never
/// blocked.
///
/// Faithful to QC: the DB exists only when <c>g_playerstats_gamereport_uri</c> is non-empty (else the whole
/// pipeline is a no-op — stats off); accumulation is suppressed during warmup; per-player identity is the
/// crypto fingerprint, else <c>bot#&lt;skill&gt;#&lt;name&gt;</c>, with a per-match unique fallback on collision;
/// the <c>joins</c> event only counts players who actually played; event values are floats keyed by
/// <c>&lt;prefix&gt;:&lt;eventid&gt;</c> with a missing key reading 0.
/// </summary>
public sealed class PlayerStats
{
    // ---- the canonical event-id strings (QC the PLAYERSTATS_* constants) ----
    public const string Alivetime = "alivetime";
    public const string AvgLatency = "avglatency";
    public const string Wins = "wins";
    public const string Matches = "matches";
    public const string Joins = "joins";
    public const string ScoreboardValid = "scoreboardvalid";
    public const string ScoreboardPos = "scoreboardpos";
    public const string Rank = "rank";
    public const string TotalPrefix = "total-";       // total-<scorelabel>
    public const string ScoreboardPrefix = "scoreboard-"; // scoreboard-<scorelabel>

    private readonly Dictionary<string, float> _events = new(StringComparer.Ordinal);   // "<prefix>:<eventid>" -> value
    private readonly Dictionary<string, string> _meta = new(StringComparer.Ordinal);     // "<id>:_playerid" etc.
    private readonly List<string> _players = new();     // ordered player ids
    private readonly HashSet<string> _playerSet = new(StringComparer.Ordinal);
    private readonly List<int> _teams = new();          // ordered team numbers
    private readonly List<string> _eventIds = new();    // ordered registered event ids
    private readonly HashSet<string> _eventSet = new(StringComparer.Ordinal);

    private readonly Dictionary<Player, string> _statsId = new();
    private readonly Dictionary<Player, float> _alivetimeStart = new();

    /// <summary>QC <c>PS_GR_OUT_DB &gt;= 0</c>: whether the report DB is live (g_playerstats_gamereport_uri set).</summary>
    public bool Enabled { get; private set; }

    /// <summary>QC <c>PlayerStats_GameReport_DelayMapVote</c>: blocks the map vote until the report is built/sent.</summary>
    public bool DelayMapVote { get; private set; }

    /// <summary>True once a match is in warmup (suppresses accumulation + discards the report). Host-wired.</summary>
    public Func<bool> IsWarmup { get; set; } = static () => false;

    /// <summary>Per-player ping (ms) provider for the latency stat (host-wired; default 0).</summary>
    public Func<Player, float> PingProvider { get; set; } = static _ => 0f;

    /// <summary>
    /// Per-player accuracy provider (QC <c>PlayerStats_GameReport_Accuracy</c>): yields (eventId, value) pairs
    /// like <c>("acc-vortex-hit", 12)</c>. Host-wired to the accuracy subsystem; default yields nothing.
    /// </summary>
    public Func<Player, IEnumerable<(string eventId, float value)>>? AccuracyProvider { get; set; }

    /// <summary>
    /// Per-player anticheat reporter (QC <c>anticheat_report_to_playerstats</c>): invokes the callback with
    /// each (eventId, value). Host-wired to <see cref="AntiCheat.ReportToPlayerStats"/>; default no-op.
    /// </summary>
    public Action<Player, Action<string, double>>? AnticheatReporter { get; set; }

    /// <summary>
    /// QC the damage-weighted handicap averages for the report (common/playerstats.qc:264-265): yields the
    /// already-divided <c>handicapgiven</c> / <c>handicaptaken</c> values (<c>given&lt;=0 ? 1 : avg_given_sum/given</c>).
    /// Host-wired to <see cref="Scores"/>; default null → events stay 0 (suppressed by the != 0 report filter).
    /// </summary>
    public Func<Player, (float given, float taken)>? HandicapReportProvider { get; set; }

    /// <summary>The per-player scoreboard rank/position + score columns at finalize. Host-wired to <see cref="Scores"/>.</summary>
    public Func<Player, int>? ScoreboardPosProvider { get; set; }
    public Func<Player, int>? RankProvider { get; set; }
    public Func<Player, IEnumerable<(string label, int value)>>? ScoreColumnsProvider { get; set; }

    private static float Skill => Cvars.FloatOr("skill", 5f);

    // =============================================================================================
    // lifecycle (QC PlayerStats_GameReport_Init / _Reset_All)
    // =============================================================================================

    /// <summary>
    /// QC <c>PlayerStats_GameReport_Init</c>: enable the report DB when <c>g_playerstats_gamereport_uri</c> is
    /// set, clear the tallies, arm <see cref="DelayMapVote"/>, and pre-register the global event keys.
    /// </summary>
    public void Init()
    {
        _events.Clear(); _meta.Clear();
        _players.Clear(); _playerSet.Clear();
        _teams.Clear();
        _eventIds.Clear(); _eventSet.Clear();
        _alivetimeStart.Clear();

        Enabled = !string.IsNullOrEmpty(Cvars.String("g_playerstats_gamereport_uri"));
        if (!Enabled) { DelayMapVote = false; return; }

        DelayMapVote = true;
        AddEvent(Alivetime); AddEvent(AvgLatency); AddEvent(Wins); AddEvent(Matches); AddEvent(Joins);
        AddEvent(ScoreboardValid); AddEvent(ScoreboardPos); AddEvent(Rank);
        AddEvent("handicapgiven"); AddEvent("handicaptaken");
        // per-weapon accuracy events
        foreach (Weapon w in Weapons.All)
        {
            AddEvent($"acc-{w.NetName}-real");
            AddEvent($"acc-{w.NetName}-hit");
            AddEvent($"acc-{w.NetName}-fired");
            AddEvent($"acc-{w.NetName}-cnt-hit");
            AddEvent($"acc-{w.NetName}-cnt-fired");
            AddEvent($"acc-{w.NetName}-frags");
        }
        // achievements
        foreach (string a in new[] { "kill-spree-3", "kill-spree-5", "kill-spree-10", "kill-spree-15",
                     "kill-spree-20", "kill-spree-25", "kill-spree-30", "botlike", "firstblood", "firstvictim" })
            AddEvent("achievement-" + a);

        // QC anticheat_register_to_playerstats() (playerstats.qc:339): pre-register the anticheat event slots
        // (anticheat-_time + one per detector) so the per-player anticheat feed at FinalizePlayer lands in the
        // game report. Base's PlayerStats_GameReport_Event DROPS unregistered events, so registration is the
        // register-then-emit contract; the port's AddEvent already dedupes/orders, and ReportToPlayerStats emits
        // these exact ids.
        AntiCheat.RegisterPlayerStats(AddEvent);
    }

    /// <summary>QC <c>PlayerStats_GameReport_Reset_All</c>: close + re-init the DB (on match restart).</summary>
    public void ResetAll(IEnumerable<Player>? clients = null, IEnumerable<int>? teams = null)
    {
        Init();
        if (!Enabled) return;
        if (teams is not null) foreach (int t in teams) AddTeam(t);
        if (clients is not null)
            foreach (Player p in clients) { _statsId.Remove(p); AddEvent($"kills-{p.PlayerId}"); AddPlayer(p); }
    }

    // =============================================================================================
    // registration (QC AddEvent / AddPlayer / AddTeam)
    // =============================================================================================

    /// <summary>QC <c>PlayerStats_GameReport_AddEvent</c>: register a global event key (deduped, ordered).</summary>
    public void AddEvent(string eventId)
    {
        if (!Enabled || string.IsNullOrEmpty(eventId)) return;
        if (_eventSet.Add(eventId))
            _eventIds.Add(eventId);
    }

    /// <summary>
    /// QC <c>PlayerStats_GameReport_AddPlayer</c>: assign + register the player's stats id (crypto fingerprint,
    /// else <c>bot#&lt;skill&gt;#&lt;name&gt;</c>, with a per-match-unique fallback on collision). Idempotent.
    /// </summary>
    public void AddPlayer(Player p)
    {
        if (!Enabled || _statsId.ContainsKey(p)) return;
        string s;
        if (!string.IsNullOrEmpty(p.PersistentId))
            s = p.PersistentId;
        else if (p.IsBot)
            s = $"bot#{Skill:0.######}#{p.NetName}";
        else
            s = "";

        if (s == "" || _playerSet.Contains(s))
            s = p.IsBot ? $"bot#{p.PlayerId}" : $"player#{p.PlayerId}";

        _statsId[p] = s;
        if (_playerSet.Add(s))
            _players.Add(s);
    }

    /// <summary>QC <c>PlayerStats_GameReport_AddTeam</c>: register a team number (deduped, ordered).</summary>
    public void AddTeam(int t)
    {
        if (!Enabled) return;
        if (!_teams.Contains(t))
            _teams.Add(t);
    }

    private string IdOf(Player p) => _statsId.TryGetValue(p, out var s) ? s : "";

    // =============================================================================================
    // accumulation (QC PlayerStats_GameReport_Event)
    // =============================================================================================

    /// <summary>QC <c>PlayerStats_GameReport_Event(prefix, eventid, value)</c>: read-add-write; returns the new total.</summary>
    public float Event(string prefix, string eventId, float value)
    {
        if (!Enabled || string.IsNullOrEmpty(prefix)) return 0f;
        string key = prefix + ":" + eventId;
        float v = (_events.TryGetValue(key, out float cur) ? cur : 0f) + value;
        _events[key] = v;
        return v;
    }

    /// <summary>QC <c>PlayerStats_GameReport_Event_Player</c>.</summary>
    public float EventPlayer(Player p, string eventId, float value) => Event(IdOf(p), eventId, value);

    /// <summary>QC <c>PlayerStats_GameReport_Event_Team</c>.</summary>
    public float EventTeam(int team, string eventId, float value) => Event($"team#{team}", eventId, value);

    /// <summary>Read a player's accumulated event total (0 if absent).</summary>
    public float GetPlayer(Player p, string eventId)
        => _events.TryGetValue(IdOf(p) + ":" + eventId, out float v) ? v : 0f;

    /// <summary>QC the alivetime start stamp (PutPlayerInServer): begin counting this player's alive time.</summary>
    public void BeginAlivetime(Player p, float now) { if (Enabled) _alivetimeStart[p] = now; }

    /// <summary>
    /// QC <c>PlayerFrame</c> alivetime AFK-gate (server/client.qc:2858):
    /// <code>if (this.alivetime_start &amp;&amp; time - CS(this).parm_idlesince &gt;= 30) this.alivetime_start += frametime;</code>
    /// In QC <c>alivetime_start</c> is a running ACCUMULATOR (frametime is added when idle); the port tracks a
    /// start TIMESTAMP instead. The same effect — idle time is excluded from the reported alivetime — is achieved by
    /// advancing the start stamp forward by <paramref name="frameTime"/> whenever the player has been idle for at
    /// least 30 seconds. Call once per server frame per live, spawned player.
    /// </summary>
    public void AdvanceAliveStart(Player p, float frameTime)
    {
        if (!Enabled) return;
        if (_alivetimeStart.TryGetValue(p, out float start) && start > 0f)
            _alivetimeStart[p] = start + frameTime;
    }

    /// <summary>QC the alivetime flush (on observe/death/finalize): add elapsed alive time + clear the stamp.</summary>
    public void FlushAlivetime(Player p, float now)
    {
        if (!Enabled) return;
        if (_alivetimeStart.TryGetValue(p, out float start) && start > 0f)
        {
            EventPlayer(p, Alivetime, System.Math.Max(0f, now - start));
            _alivetimeStart[p] = 0f;
        }
    }

    // =============================================================================================
    // finalize + report (QC FinalizePlayer / GameReport / the V9 serializer)
    // =============================================================================================

    /// <summary>
    /// QC <c>PlayerStats_GameReport_FinalizePlayer</c>: flush alivetime, write the player metadata
    /// (playerid/netname/team/ranked), the joins event, the accuracy + anticheat tallies, and the average
    /// latency. Clears the player's stats id so they aren't double-finalized.
    /// </summary>
    public void FinalizePlayer(Player p, float now, bool teamplay)
    {
        if (!Enabled) return;
        string id = IdOf(p);
        if (string.IsNullOrEmpty(id)) return;

        FlushAlivetime(p, now);
        _meta[$"{id}:_playerid"] = p.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _meta[$"{id}:_netname"] = p.NetName;
        if (teamplay)
            _meta[$"{id}:_team"] = ((int)p.Team).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (GetPlayer(p, Alivetime) > 0f)
            EventPlayer(p, Joins, 1);

        // QC common/playerstats.qc:264-265: the damage-weighted handicap averages (already divided by the
        // total DMG/DMGTAKEN by the provider; 1 when no damage was dealt/taken).
        if (HandicapReportProvider is not null)
        {
            var (hGiven, hTaken) = HandicapReportProvider(p);
            EventPlayer(p, "handicapgiven", hGiven);
            EventPlayer(p, "handicaptaken", hTaken);
        }

        if (AccuracyProvider is not null)
            foreach (var (eventId, value) in AccuracyProvider(p))
                EventPlayer(p, eventId, value);

        AnticheatReporter?.Invoke(p, (eventId, value) => EventPlayer(p, eventId, (float)value));

        float pingMs = PingProvider(p);
        if (pingMs > 0f)
        {
            float prev = EventPlayer(p, AvgLatency, 0); // read current
            float updated = prev <= 0f ? pingMs : (prev + pingMs) / 2f;
            EventPlayer(p, AvgLatency, -prev + updated);
        }

        _statsId.Remove(p); // QC strfree(playerstats_id)
    }

    /// <summary>
    /// QC <c>PlayerStats_GameReport(finished)</c>: record each client's rank/scoreboard position + score
    /// columns, finalize them, then (since the HTTP upload is deferred) clear <see cref="DelayMapVote"/> so map
    /// voting may proceed. During warmup or with no URI the report is discarded. Returns the built report
    /// string (the V9 wire form) so a host that DOES implement upload can send it.
    /// </summary>
    public string GameReport(bool finished, IEnumerable<Player> clients, float now, bool teamplay)
    {
        if (!Enabled) { DelayMapVote = false; return ""; }

        foreach (Player p in clients)
        {
            if (RankProvider is not null)
                EventPlayer(p, Rank, RankProvider(p));
            int pos = ScoreboardPosProvider?.Invoke(p) ?? 0;
            if (pos != 0)
            {
                EventPlayer(p, ScoreboardValid, 1);
                EventPlayer(p, ScoreboardPos, pos);
                if (ScoreColumnsProvider is not null)
                    foreach (var (label, value) in ScoreColumnsProvider(p))
                        if (!string.IsNullOrEmpty(label))
                            EventPlayer(p, ScoreboardPrefix + label, value);
                if (finished)
                {
                    if (IsWinner(p)) EventPlayer(p, Wins, 1);
                    EventPlayer(p, Matches, 1);
                }
            }
            FinalizePlayer(p, now, teamplay);
        }

        // QC: warmup discards the report entirely.
        if (IsWarmup())
        {
            DelayMapVote = false;
            Enabled = false;
            return "";
        }

        string report = BuildReport(now);
        // The async HTTP upload is deferred (engine concern); we finish synchronously so the map vote runs.
        DelayMapVote = false;
        return report;
    }

    /// <summary>Whether the player won the match (host-wired via <see cref="WinnerPredicate"/>; default false).</summary>
    public Func<Player, bool> WinnerPredicate { get; set; } = static _ => false;
    private bool IsWinner(Player p) => WinnerPredicate(p);

    /// <summary>
    /// QC the URL_READY_CANWRITE serializer: build the "format version 9" report string. The exact line keys
    /// (V/G/O/M/I/S/D/Q/P/i/n/t/e) match XonStat so a host that uploads it is wire-compatible.
    /// </summary>
    public string BuildReport(float now)
    {
        var sb = new StringBuilder();
        sb.Append("V 9\n");
        sb.Append("G ").Append(Cvars.String("g_playerstats_gametype")).Append('\n');
        sb.Append("O ").Append(Cvars.String("modname")).Append('\n');
        sb.Append("M ").Append(Cvars.String("mapname")).Append('\n');
        sb.Append("S ").Append(Cvars.String("hostname")).Append('\n');
        sb.Append("D ").Append(System.Math.Max(0f, now).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        bool teamplay = Cvars.Bool("teamplay");
        if (teamplay)
            foreach (int t in _teams)
            {
                sb.Append("Q team#").Append(t).Append('\n');
                foreach (string e in _eventIds)
                {
                    if (_events.TryGetValue($"team#{t}:{e}", out float v) && v != 0f)
                        sb.Append("e ").Append(e).Append(' ').Append(Fmt(v)).Append('\n');
                }
            }

        foreach (string p in _players)
        {
            sb.Append("P ").Append(p).Append('\n');
            if (_meta.TryGetValue($"{p}:_playerid", out string? pid) && !string.IsNullOrEmpty(pid))
                sb.Append("i ").Append(pid).Append('\n');
            if (_meta.TryGetValue($"{p}:_netname", out string? nn) && !string.IsNullOrEmpty(nn))
                sb.Append("n ").Append(nn).Append('\n');
            if (teamplay && _meta.TryGetValue($"{p}:_team", out string? tm) && !string.IsNullOrEmpty(tm))
                sb.Append("t ").Append(tm).Append('\n');
            foreach (string e in _eventIds)
            {
                if (_events.TryGetValue($"{p}:{e}", out float v) && v != 0f)
                    sb.Append("e ").Append(e).Append(' ').Append(Fmt(v)).Append('\n');
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string Fmt(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Snapshot of all accumulated (key, value) tallies — for tests/inspection.</summary>
    public IReadOnlyDictionary<string, float> Events => _events;

    /// <summary>The registered player stats ids (ordered) — for tests/inspection.</summary>
    public IReadOnlyList<string> Players => _players;
}
