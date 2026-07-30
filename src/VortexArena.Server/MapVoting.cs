using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// One candidate in the end-of-match map vote — the C# successor to the parallel QC arrays
/// <c>mapvote_maps[i]</c> / <c>mapvote_selections[i]</c> / <c>mapvote_maps_flags[i]</c> / <c>mapvote_rng[i]</c>
/// (server/mapvoting.qc). Collapsed into a small object so a candidate carries its own tally and tiebreak.
/// </summary>
public sealed class MapVoteCandidate
{
    /// <summary>The map name on the ballot (QC <c>mapvote_maps[i]</c>).</summary>
    public string MapName { get; }

    /// <summary>Whether this option can still win (QC <c>GTV_AVAILABLE</c> in mapvote_maps_flags).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Running vote count (QC <c>mapvote_selections[i]</c>).</summary>
    public int Votes { get; internal set; }

    /// <summary>
    /// Per-candidate random tiebreak value assigned at ballot build (QC <c>mapvote_rng[i] = random()</c>).
    /// When two options tie on votes, the one with the higher value goes first (QC <c>MapVote_ranked_cmp</c>).
    /// </summary>
    public float Rng { get; internal set; }

    /// <summary>The "don't care"/abstain slot (QC the <c>MapVote_AddVotable(-2)</c> appended option).</summary>
    public bool IsAbstain { get; }

    /// <summary>Name of the player who suggested this map, if any (QC <c>mapvote_maps_suggesters</c>); "" otherwise.</summary>
    public string Suggester { get; internal set; } = "";

    public MapVoteCandidate(string mapName, bool isAbstain = false)
    {
        MapName = mapName;
        IsAbstain = isAbstain;
    }
}

/// <summary>
/// A faithful end-of-match map vote — the structure + core of server/mapvoting.qc
/// (<c>MapVote_Init</c> / <c>MapVote_AddVotable</c> / the per-client tally in <c>MapVote_Tick</c> /
/// <c>MapVote_CheckRules_count</c> / <c>MapVote_CheckRules_decide</c> / <c>MapVote_Finished</c>). After
/// intermission the host builds the ballot from player suggestions + the map rotation (+ an optional abstain
/// slot), players cast one vote each (by candidate index, QC the client's <c>.mapvote</c> set from impulses),
/// and the winner is the most-voted available candidate — ties broken by each candidate's stored
/// <see cref="MapVoteCandidate.Rng"/> value (QC <c>mapvote_rng</c>).
///
/// Modeled here: abstain handling (<see cref="Abstain"/>, QC the abstain ballot slot and its exclusion from
/// the real-voter count), the mid-vote ballot **reduce / keep-two** prune
/// (<c>g_maplist_votable_reduce_time</c> / <c>g_maplist_votable_reduce_count</c>, QC
/// <c>MapVote_CheckRules_decide</c>'s reduce loop + <c>MapVote_TouchMask</c>), the bit-faithful
/// "leader is unbeatable" early finish (QC <c>(voters_real - running_total) &lt; votes_recent</c>), the all-
/// abstained finish (QC <c>voters_real == 0</c>), the 0.5 s think throttle (QC <c>mapvote_nextthink</c>), and
/// the 1 s winner→changelevel delay (QC <c>mapvote_winner_time + 1</c>, surfaced as
/// <see cref="ReadyToChangeLevel"/> for the host's <c>Map_Goto</c>).
///
/// Deferred (cross-file): the network ballot UI (<c>ENT_CLIENT_MAPVOTE</c>), gametype voting
/// (<c>GameTypeVote_*</c>), the per-client impulse plumbing that drives <see cref="CastVote"/>/<see cref="Abstain"/>
/// (host <c>DispatchImpulse</c> drops impulses during intermission), and the 2342 sentinel-health/scoreboard-hide.
/// The <c>:vote:reduce</c>/<c>:vote:finished</c>/<c>:vote:suggestion_accepted</c> event-log lines ARE emitted via
/// the <see cref="EventLogEcho"/> seam (wired by the host to <c>GameLog.Echo</c>, gated on <c>sv_eventlog</c>).
/// </summary>
public sealed class MapVoting
{
    private readonly List<MapVoteCandidate> _candidates = new();
    private readonly Dictionary<object, int> _votes = new(); // voter key -> candidate index
    private readonly HashSet<object> _abstained = new();     // voters who explicitly abstained
    private Random _rng;

    // QC mapvote_nextthink: throttle the decide loop to ~2 Hz instead of every server frame.
    private const float ThinkCadenceSeconds = 0.5f;
    private float _nextThink;

    // QC mapvote_winner_time + 1: hold the result for 1 s before the map actually changes.
    private const float WinnerToChangeLevelDelaySeconds = 1f;

    // QC mapvote_reduce_time / mapvote_reduce_count: mid-vote ballot prune.
    private float _reduceTime;   // absolute sim time the reduce fires; 0 = disabled/done
    private int _reduceCount;    // keep exactly this many top options when >= 2

    /// <summary>True once <see cref="Start"/> built a ballot and the vote is accepting votes (QC <c>mapvote_run</c>).</summary>
    public bool Running { get; private set; }

    /// <summary>True once <see cref="Finish"/> picked a winner (QC <c>alreadychangedlevel</c>).</summary>
    public bool Finished { get; private set; }

    /// <summary>The chosen map after <see cref="Finish"/> (QC MapVote_Winner), or "" while the vote runs.</summary>
    public string WinningMap { get; private set; } = "";

    /// <summary>
    /// Absolute sim time the winner was picked (QC <c>mapvote_winner_time</c>), or 0 while the vote runs.
    /// The host should not change level until <see cref="ReadyToChangeLevel"/> (winner time + 1 s).
    /// </summary>
    public float WinnerTime { get; private set; }

    /// <summary>
    /// QC <c>MapVote_Think</c>'s <c>time &gt; mapvote_winner_time + 1</c> gate: true once the 1 s
    /// winner-reveal delay has elapsed, so the host may apply the map change. Until then the result is shown
    /// but the level does not change. Always true for a degenerate (auto-picked) ballot.
    /// </summary>
    public bool ReadyToChangeLevel =>
        Finished && (WinnerTime <= 0f || Now >= WinnerTime + WinnerToChangeLevelDelaySeconds);

    /// <summary>Absolute sim time the vote auto-closes (QC <c>mapvote_timeout</c>); 0 = no timeout.</summary>
    public float Timeout { get; private set; }

    /// <summary>The ballot (read-only) — each option with its live tally.</summary>
    public IReadOnlyList<MapVoteCandidate> Candidates => _candidates;

    /// <summary>How many distinct voters have cast a vote so far (QC the sum of mapvote_selections).</summary>
    public int VotesCast => _votes.Count;

    /// <summary>Number of voters expected (QC <c>mapvote_voters</c>), set at <see cref="Start"/> — drives the early finish.</summary>
    public int ExpectedVoters { get; private set; }

    /// <summary>
    /// QC <c>gametypevote</c>: true when this ballot is a gametype vote (QC <c>GameTypeVote_Start</c>) rather
    /// than a map vote. The panel draws a wider aspect-ratio cell and the title reads "Decide the gametype".
    /// </summary>
    public bool IsGametypeVote { get; private set; }

    /// <summary>
    /// QC <c>voted_gametype_string</c>: the winning gametype name after a gametype vote finishes (e.g. "dm",
    /// "tdm", "ca", "ctf"), or "" if the vote has not finished or this is a map vote. The host reads this to
    /// apply the gametype switch before starting the map vote.
    /// </summary>
    public string VotedGametype { get; private set; } = "";

    /// <summary>
    /// QC <c>GameLogEcho(...)</c> seam for the vote event-log lines (<c>:vote:reduce</c>,
    /// <c>:vote:finished</c>, <c>:vote:suggestion_accepted</c>) — server/mapvoting.qc:524/537/539/676/695.
    /// The host wires this to <c>GameLog.Echo</c>, gating on <c>sv_eventlog</c> at the call site (same pattern
    /// as Domination/Keepaway's <c>EventLogEcho</c>). When null (headless / tests) the emission is a no-op.
    /// </summary>
    public Action<string>? EventLogEcho { get; set; }

    public MapVoting(int seed = 0x5EED) => _rng = new Random(seed);

    /// <summary>Reseed the tiebreak RNG (determinism support for headless runs).</summary>
    public void Reseed(int seed) => _rng = new Random(seed);

    /// <summary>
    /// QC <c>MapVote_Init</c> + <c>MapVote_AddVotableMaps</c> + <c>MapVote_AddVotable</c>: build the ballot from
    /// <paramref name="mapNames"/> (deduped, ballot order preserved), record how many voters to expect, assign
    /// each candidate a random tiebreak value, append the abstain slot when <c>g_maplist_votable_abstain</c> is
    /// set, arm the reduce/timeout timers, and (optionally) arm the vote timeout. A vote with 0 or 1 candidate
    /// finishes immediately to the only/empty option.
    /// </summary>
    public void Start(IEnumerable<string> mapNames, int expectedVoters, float voteDurationSeconds = 0f)
        => Start(mapNames, expectedVoters, voteDurationSeconds, null);

    /// <summary>
    /// Overload that also seeds player <paramref name="suggestions"/> ahead of the rotation maps
    /// (QC <c>MapVote_AddVotableMaps</c> seeds <c>smax = min3(nmax, g_maplist_votable_suggestions,
    /// mapvote_suggestion_ptr)</c> suggested maps first). Each suggestion is a (map, suggester) pair; the
    /// suggester is surfaced on the candidate when <c>g_maplist_votable_show_suggester</c> is set.
    /// </summary>
    public void Start(
        IEnumerable<string> mapNames,
        int expectedVoters,
        float voteDurationSeconds,
        IReadOnlyList<(string Map, string Suggester)>? suggestions)
    {
        _candidates.Clear();
        _votes.Clear();
        _abstained.Clear();
        Finished = false;
        WinningMap = "";
        WinnerTime = 0f;
        _nextThink = 0f;
        IsGametypeVote = false;
        VotedGametype = "";

        bool abstain = Cvars.Bool("g_maplist_votable_abstain");
        int votable = Cvars.Int("g_maplist_votable");
        // QC nmax = min(MAPVOTE_COUNT-(abstain?1:0), g_maplist_votable): leave room for the abstain slot.
        int nmax = votable > 0 ? votable : int.MaxValue;
        if (abstain && nmax != int.MaxValue)
            nmax = System.Math.Max(nmax - 1, 0);
        bool showSuggester = Cvars.FloatOr("g_maplist_votable_show_suggester", 1f) != 0f;

        // QC MapVote_AddVotableMaps: seed the suggested maps first (limited by g_maplist_votable_suggestions),
        // then fill the rest from the rotation. Both pass through the same dedup.
        int suggestCap = Cvars.Int("g_maplist_votable_suggestions");
        if (suggestions is not null && suggestCap > 0)
        {
            int seeded = 0;
            foreach ((string map, string suggester) in suggestions)
            {
                if (seeded >= suggestCap)
                    break;
                if (_candidates.Count >= nmax)
                    break;
                if (AddVotable(map) is { } cand)
                {
                    if (showSuggester)
                        cand.Suggester = suggester ?? "";
                    seeded++;
                }
            }
        }

        foreach (string name in mapNames)
        {
            if (_candidates.Count >= nmax)
                break;
            AddVotable(name);
        }

        // QC: if abstain is enabled, append the "don't care" slot last (MapVote_AddVotable(-2)).
        if (abstain && _candidates.Count > 0)
            _candidates.Add(new MapVoteCandidate("", isAbstain: true));

        ExpectedVoters = System.Math.Max(expectedVoters, 0);
        Running = true;
        Timeout = voteDurationSeconds > 0f ? Now + voteDurationSeconds : 0f;

        // QC arm the reduce timers: mapvote_reduce_time = time + g_maplist_votable_reduce_time,
        // mapvote_reduce_count = g_maplist_votable_reduce_count. Reduce is disabled when there are fewer than
        // 3 real (non-abstain) options or the reduce delay is non-positive.
        _reduceCount = Cvars.Int("g_maplist_votable_reduce_count");
        float reduceDelay = Cvars.FloatOr("g_maplist_votable_reduce_time", 15f);
        _reduceTime = reduceDelay > 0f ? Now + reduceDelay : 0f;
        if (RealCandidateCount < 3 || _reduceTime <= Now)
            _reduceTime = 0f;

        // Degenerate ballots finish straight away (QC: a single votable map is chosen without a vote).
        if (RealCandidateCount <= 1)
            Finish();
    }

    /// <summary>
    /// QC <c>MapVote_AddVotable</c>: add a single map to the ballot (deduped by name, ballot order preserved),
    /// assigning its <see cref="MapVoteCandidate.Rng"/> tiebreak value. Returns the new candidate, or null if it
    /// was a duplicate / empty.
    /// </summary>
    private MapVoteCandidate? AddVotable(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        for (int i = 0; i < _candidates.Count; i++)
            if (string.Equals(_candidates[i].MapName, name, StringComparison.OrdinalIgnoreCase))
                return null;
        var cand = new MapVoteCandidate(name) { Rng = (float)_rng.NextDouble() };
        _candidates.Add(cand);
        return cand;
    }

    /// <summary>
    /// QC <c>GameTypeVote_Start</c>: build a pre-map gametype ballot from <c>sv_vote_gametype_options</c>
    /// (space-separated gametype NetNames), arm the gametype-vote timers, and open the vote. An option is only
    /// added to the ballot when it resolves to a known gametype — a real one (<see cref="GameTypes.ByName"/>) or
    /// a custom alias via <c>sv_vote_gametype_&lt;name&gt;_type</c> — matching QC <c>GameTypeVote_AddVotable</c>,
    /// which drops an unresolvable name entirely rather than showing it dimmed.
    ///
    /// <para>If <c>sv_vote_gametype_default_current</c> is set, the current gametype (identified by
    /// <paramref name="currentGametypeName"/>) wins any 0-vote tiebreak: this is modelled by giving it a
    /// <see cref="MapVoteCandidate.Rng"/> of <c>1f</c> (above the [0,1) range that random candidates get),
    /// faithful to QC's <c>mapvote_ranked_cmp</c> special-case for <c>current_gametype_index</c>.</para>
    ///
    /// <para>0 or 1 available options → the vote finishes immediately (QC short-circuits in those cases).</para>
    /// </summary>
    public void StartGametype(int expectedVoters, string currentGametypeName)
    {
        _candidates.Clear();
        _votes.Clear();
        _abstained.Clear();
        Finished = false;
        WinningMap = "";
        WinnerTime = 0f;
        _nextThink = 0f;
        IsGametypeVote = true;
        VotedGametype = "";

        // QC: mapvote_abstain = false (gametype vote never has the abstain slot).
        // QC: parse sv_vote_gametype_options space-separated, cap at MAPVOTE_COUNT (20).
        const int MapvoteCount = 20;
        string options = Cvars.String("sv_vote_gametype_options");
        bool defaultCurrent = Cvars.Bool("sv_vote_gametype_default_current");
        int reallyAvailable = 0;

        foreach (string name in options.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_candidates.Count >= MapvoteCount)
                break;
            if (string.IsNullOrEmpty(name))
                continue;
            // Dedup.
            bool dup = false;
            foreach (MapVoteCandidate c in _candidates)
                if (string.Equals(c.MapName, name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (dup)
                continue;

            // QC GameTypeVote_AddVotable: an option is only ADDED when GameTypeVote_Type_FromString resolves it —
            // either a real gametype name, or a custom alias via the sv_vote_gametype_<name>_type indirection. An
            // unresolvable name is skipped entirely (QC returns false), NOT shown on the ballot dimmed.
            bool isRealType = GameTypes.ByName(name) is not null;
            // Custom gametype: sv_vote_gametype_<name>_type names the real gametype this alias maps to.
            bool isCustomType = !isRealType
                && GameTypes.ByName(Cvars.String($"sv_vote_gametype_{name}_type")) is not null;
            if (!isRealType && !isCustomType)
                continue; // QC GameTypeVote_AddVotable returns false for an unknown gametype — drop it.

            // QC GameTypeVote_AvailabilityStatus: a resolvable type is GTV_AVAILABLE (the port has no MapInfo
            // next-map support filter, so a resolved type is always considered available — the documented gap).
            var cand = new MapVoteCandidate(name) { Rng = (float)_rng.NextDouble(), Available = true };
            reallyAvailable++;
            // QC sv_vote_gametype_default_current: current gametype gets the top tiebreak Rng (1f > any [0,1)).
            if (defaultCurrent && string.Equals(name, currentGametypeName, StringComparison.OrdinalIgnoreCase))
                cand.Rng = 1f;
            _candidates.Add(cand);
        }

        ExpectedVoters = System.Math.Max(expectedVoters, 0);
        float timeout = Cvars.FloatOr("sv_vote_gametype_timeout", 20f);
        Running = true;
        Timeout = timeout > 0f ? Now + timeout : 0f;

        // QC arm the reduce timers (sv_vote_gametype_reduce_time / _reduce_count).
        _reduceCount = Cvars.Int("sv_vote_gametype_reduce_count");
        float reduceDelay = Cvars.FloatOr("sv_vote_gametype_reduce_time", 10f);
        _reduceTime = reduceDelay > 0f ? Now + reduceDelay : 0f;
        if (RealCandidateCount < 3 || _reduceTime <= Now)
            _reduceTime = 0f;

        // QC: 0 available → finish to current gametype; 1 available → finish to that one.
        if (reallyAvailable <= 1)
            Finish();
    }

    /// <summary>
    /// Cast (or change) <paramref name="voter"/>'s vote to candidate <paramref name="candidateIndex"/>
    /// (QC client <c>.mapvote</c> set from an impulse). Out-of-range or unavailable candidates are ignored
    /// (QC clears invalid votes). If the chosen index is the abstain slot the vote is routed to
    /// <see cref="Abstain"/>. The <paramref name="voter"/> key is any stable per-voter object (the
    /// <see cref="Common.Gameplay.Player"/>). Returns true if the vote was recorded.
    /// </summary>
    public bool CastVote(object voter, int candidateIndex)
    {
        if (!Running || Finished)
            return false;
        if (candidateIndex < 0 || candidateIndex >= _candidates.Count)
            return false;
        if (!_candidates[candidateIndex].Available)
            return false;
        if (_candidates[candidateIndex].IsAbstain)
            return Abstain(voter);

        // move the voter's tally from any previous choice to the new one (clearing any prior abstain).
        _abstained.Remove(voter);
        if (_votes.TryGetValue(voter, out int prev) && prev != candidateIndex)
            _candidates[prev].Votes--;
        _votes[voter] = candidateIndex;
        // recount from the map (cheap, and avoids double counting on a repeat of the same choice).
        RecountFromBallots();
        return true;
    }

    /// <summary>
    /// QC the abstain ballot (g_maplist_votable_abstain): <paramref name="voter"/> declines to pick a map.
    /// An abstaining voter still counts toward "everyone voted" (so the vote can finish) but adds no tally to
    /// any candidate, and is excluded from the real-voter count used by the early-finish check
    /// (QC <c>mapvote_voters_real</c>). Clears any prior map choice by this voter. Returns true while the vote
    /// is open.
    /// </summary>
    public bool Abstain(object voter)
    {
        if (!Running || Finished)
            return false;
        if (_votes.Remove(voter))
            RecountFromBallots();
        _abstained.Add(voter);
        return true;
    }

    /// <summary>The given voter's current candidate index (QC client <c>.mapvote</c>), or -1 if they have not
    /// voted (or abstained). Surfaced for the client map-vote HUD's own-vote highlight (QC <c>mv_ownvote</c>).</summary>
    public int SelectionOf(object? voter)
        => voter is not null && _votes.TryGetValue(voter, out int idx) ? idx : -1;

    /// <summary>Withdraw a voter (e.g. on disconnect) so their vote no longer counts.</summary>
    public void RemoveVoter(object voter)
    {
        bool changed = _votes.Remove(voter);
        changed |= _abstained.Remove(voter);
        if (changed)
            RecountFromBallots();
    }

    /// <summary>
    /// Drive the vote (QC <c>MapVote_Tick</c> → <c>MapVote_CheckRules_count</c> → <c>_decide</c>). Throttled to
    /// the 0.5 s think cadence (QC <c>mapvote_nextthink</c>), snapping to the timeout so the vote never overruns
    /// it. Finishes the vote when every expected voter has voted, the leader becomes unbeatable, everyone
    /// abstained, or the timeout elapses; otherwise applies the mid-vote ballot reduce. No-op once
    /// finished/not running.
    /// </summary>
    public void Tick()
    {
        if (!Running || Finished)
            return;

        // QC mapvote_nextthink: only re-evaluate every 0.5 s, but never skip past the timeout.
        float now = Now;
        if (now < _nextThink)
            return;
        _nextThink = now + ThinkCadenceSeconds;
        if (Timeout > 0f && _nextThink > Timeout)
            _nextThink = Timeout + 0.001f;

        if (CheckRulesDecide())
            return;
    }

    /// <summary>
    /// QC <c>MapVote_CheckRules_decide</c>: decide whether to finish the vote (timeout / leader unbeatable /
    /// all abstained / everyone voted) and, failing that, apply the mid-vote ballot reduce. Returns true if the
    /// vote finished.
    /// </summary>
    private bool CheckRulesDecide()
    {
        // QC: mapvote_count_real == 1 → finish to option 0 (also covered at Start, but re-checked after reduce).
        if (RealCandidateCount == 1)
        {
            Finish();
            return true;
        }

        // QC mapvote_voters_real = mapvote_voters - abstainers.
        int votersReal = ExpectedVoters - _abstained.Count;
        if (votersReal < 0)
            votersReal = 0;

        // QC heapsort into mapvote_ranked; here a stable descending rank by votes then per-candidate rng.
        List<MapVoteCandidate> ranked = RankCandidates();

        int votesRecent = ranked.Count > 0 ? ranked[0].Votes : 0;
        int votesRunningTotal = votesRecent;

        // everyone (incl. abstainers) has voted → decide now.
        int ballots = _votes.Count + _abstained.Count;
        bool allVoted = ExpectedVoters > 0 && ballots >= ExpectedVoters;

        // QC the three finish conditions: timeout, leader unbeatable, or all abstained.
        if (allVoted
            || (Timeout > 0f && Now >= Timeout)
            || (votersReal - votesRunningTotal) < votesRecent
            || votersReal == 0)
        {
            Finish(ranked.Count > 0 ? ranked[0] : null);
            return true;
        }

        // QC the reduce loop: walk down the ranked list accumulating the running total until we reach the
        // first option that would be removed (keep_exactly ? idx >= reduce_count : votes <= 0).
        bool keepExactly = _reduceCount >= 2;
        int ri = 1;
        for (; ri < ranked.Count; ri++)
        {
            if (ReduceRemoveThis(keepExactly, ri, ranked))
                break;
            votesRecent = ranked[ri].Votes;
            votesRunningTotal += votesRecent;
        }

        if (_reduceTime > 0f
            && ((Now > _reduceTime && (keepExactly || ri >= 2))
                || (votersReal - votesRunningTotal) < votesRecent))
        {
            // QC MapVote_TouchMask: strip GTV_AVAILABLE from every option past the reduce cutoff.
            _reduceTime = 0f;
            // QC the :vote:reduce event-log line (mapvoting.qc:676-695): walk the ranked list emitting
            // "<map>:<votes>" for each, insert a "::" separator at the reduce cutoff, end with ":didn't vote:N".
            string result = ":vote:reduce";
            int didntVote = ExpectedVoters;
            bool emittedSeparator = false;
            bool remove = false;
            for (int idx = 0; idx < ranked.Count; idx++)
            {
                didntVote -= ranked[idx].Votes;
                result += ":" + ranked[idx].MapName + ":" + ranked[idx].Votes;
                if (!remove && ReduceRemoveThis(keepExactly, idx, ranked))
                {
                    if (!emittedSeparator) { result += "::"; emittedSeparator = true; } // QC: kept|removed separator
                    remove = true;
                }
                if (remove && !ranked[idx].IsAbstain)
                    ranked[idx].Available = false;
            }
            result += ":didn't vote:" + didntVote;
            EventLogEcho?.Invoke(result);
            // recount so a now-unavailable option drops voters back to "no vote" on the next pass.
            RecountFromBallots();
        }

        return false;
    }

    /// <summary>
    /// QC <c>REDUCE_REMOVE_THIS(idx)</c>: in keep-exactly mode an option is removed once its rank index reaches
    /// <c>reduce_count</c>; otherwise it's removed when it has no votes.
    /// </summary>
    private bool ReduceRemoveThis(bool keepExactly, int rankIndex, List<MapVoteCandidate> ranked)
        => keepExactly
            ? rankIndex >= _reduceCount
            : ranked[rankIndex].Votes <= 0;

    /// <summary>
    /// QC <c>MapVote_ranked_cmp</c>: rank the available candidates by descending votes, ties broken by the
    /// higher per-candidate <see cref="MapVoteCandidate.Rng"/> (QC <c>mapvote_rng</c>). Unavailable options sort
    /// to the end. The abstain slot is excluded from ranking.
    /// </summary>
    private List<MapVoteCandidate> RankCandidates()
    {
        var ranked = new List<MapVoteCandidate>(_candidates.Count);
        foreach (MapVoteCandidate c in _candidates)
            if (!c.IsAbstain)
                ranked.Add(c);

        ranked.Sort((a, b) =>
        {
            if (a.Available != b.Available)
                return a.Available ? -1 : 1; // available first
            if (!a.Available && !b.Available)
                return 0;
            if (a.Votes != b.Votes)
                return b.Votes - a.Votes; // descending votes
            return b.Rng > a.Rng ? 1 : (b.Rng < a.Rng ? -1 : 0); // higher rng first
        });
        return ranked;
    }

    /// <summary>
    /// True when the leading available candidate's lead exceeds what every remaining unvoted ballot could
    /// give the runner-up — bit-faithful to QC's <c>(voters_real - votes_running_total) &lt; votes_recent</c>.
    /// Kept public for the host's early-finish display; the live decision is made in <see cref="CheckRulesDecide"/>.
    /// </summary>
    public bool LeaderIsUnbeatable()
    {
        int votersReal = ExpectedVoters - _abstained.Count;
        if (votersReal < 0)
            votersReal = 0;
        List<MapVoteCandidate> ranked = RankCandidates();
        if (ranked.Count == 0)
            return false;
        int votesRecent = ranked[0].Votes;
        return (votersReal - votesRecent) < votesRecent || votersReal == 0;
    }

    /// <summary>
    /// Close the vote and pick the winner (QC <c>MapVote_Finished</c> → <c>MapVote_Winner</c>): the available
    /// candidate with the most votes, ties broken by the per-candidate <see cref="MapVoteCandidate.Rng"/>
    /// (QC <c>mapvote_rng</c>). Sets <see cref="WinningMap"/>, <see cref="WinnerTime"/> and <see cref="Finished"/>.
    /// Safe to call directly.
    /// </summary>
    public void Finish() => Finish(null);

    private void Finish(MapVoteCandidate? preRanked)
    {
        if (Finished)
            return;
        Finished = true;
        Running = false;
        WinnerTime = Now;

        // Prefer the already-ranked top option from the decide pass; otherwise rank now.
        MapVoteCandidate? winner = preRanked;
        if (winner is null || !winner.Available)
        {
            List<MapVoteCandidate> ranked = RankCandidates();
            winner = ranked.Count > 0 && ranked[0].Available ? ranked[0] : null;
        }

        WinningMap = winner?.MapName ?? "";

        // QC MapVote_Finished (mapvoting.qc:522-540): emit the :vote:finished event-log line. Format:
        //   :vote:finished:<winner>:<winnervotes>::[<map>:<votes>...]:didn't vote:<N>
        // listing every still-available option (the winner first via the "::", then the rest), where
        // "didn't vote" = voters minus all available-option selections.
        if (winner is not null && EventLogEcho is not null)
        {
            string result = ":vote:finished:" + winner.MapName + ":" + winner.Votes + "::";
            int didntVote = ExpectedVoters;
            foreach (MapVoteCandidate c in _candidates)
            {
                if (!c.Available || c.IsAbstain)
                    continue;
                didntVote -= c.Votes;
                if (!ReferenceEquals(c, winner))
                    result += ":" + c.MapName + ":" + c.Votes;
            }
            result += ":didn't vote:" + didntVote;
            EventLogEcho.Invoke(result);
            // QC: a non-gametype vote whose winner was a player suggestion logs :vote:suggestion_accepted.
            if (!IsGametypeVote && !string.IsNullOrEmpty(winner.Suggester))
                EventLogEcho.Invoke(":vote:suggestion_accepted:" + winner.MapName);
        }

        // QC GameTypeVote_Finished: record the winning gametype name for the host to apply the switch.
        if (IsGametypeVote)
            VotedGametype = WinningMap;
    }

    /// <summary>Mark a candidate unavailable (QC clearing GTV_AVAILABLE), e.g. the map that just played.</summary>
    public void Disqualify(string mapName)
    {
        for (int i = 0; i < _candidates.Count; i++)
            if (string.Equals(_candidates[i].MapName, mapName, StringComparison.OrdinalIgnoreCase))
                _candidates[i].Available = false;
    }

    /// <summary>Count of real (non-abstain) ballot options (QC <c>mapvote_count_real</c>).</summary>
    private int RealCandidateCount
    {
        get
        {
            int n = 0;
            foreach (MapVoteCandidate c in _candidates)
                if (!c.IsAbstain && c.Available)
                    n++;
            return n;
        }
    }

    private void RecountFromBallots()
    {
        for (int i = 0; i < _candidates.Count; i++)
            _candidates[i].Votes = 0;
        foreach (int idx in _votes.Values)
            if (idx >= 0 && idx < _candidates.Count && _candidates[idx].Available)
                _candidates[idx].Votes++;
    }

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
