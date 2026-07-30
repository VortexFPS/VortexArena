using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The per-player score fields — the C# successor to the QuakeC SP_* PlayerScoreField registry
/// (server/scores_rules.qc <c>ScoreRules_basics</c>: SP_KILLS / SP_DEATHS / SP_SUICIDES / SP_TEAMKILLS /
/// SP_SCORE / SP_DMG / SP_DMGTAKEN). QC reaches these through a field-pointer indirection on the
/// scorekeeper edict; here they are dense columns in <see cref="PlayerScoreRow"/> keyed by this enum.
/// </summary>
public enum ScoreField
{
    /// <summary>SP_KILLS — enemy frags (QC "kills").</summary>
    Kills = 0,
    /// <summary>SP_DEATHS — times died (QC "deaths", SFL_LOWER_IS_BETTER).</summary>
    Deaths,
    /// <summary>SP_SUICIDES — self/world deaths (QC "suicides", SFL_LOWER_IS_BETTER).</summary>
    Suicides,
    /// <summary>SP_TEAMKILLS — teammates killed (QC "teamkills", team games only).</summary>
    TeamKills,
    /// <summary>SP_SCORE — the sortable match score (QC "score", the primary sort key).</summary>
    Score,
    /// <summary>SP_DMG — damage dealt (QC "dmg", cosmetic).</summary>
    Damage,
    /// <summary>SP_DMGTAKEN — damage taken (QC "dmgtaken", SFL_LOWER_IS_BETTER).</summary>
    DamageTaken,
}

/// <summary>
/// One player's score columns — the Godot-free essence of a QC scorekeeper edict (its
/// <c>.(scores(i))</c> slots, server/scores.qc). Kept as a small mutable object the <see cref="Scores"/>
/// table owns.
///
/// IMPORTANT ownership rule: the SP_SCORE column is NOT a private counter — it is a read-through projection
/// of the live <see cref="Player.ScoreFrags"/> (backed by <see cref="Entity.Frags"/>). The active gametype
/// is the authoritative frag-scorer (Deathmatch/TDM/CA write <c>ScoreFrags</c> in their obituary handlers),
/// so this row reflects that single source of truth instead of maintaining a second tally that would fight
/// it (which would double-count when both the gametype and this table subscribe to the death bus). The
/// auxiliary columns (kills/deaths/suicides/teamkills/dmg/dmgtaken) ARE owned here, because the gametypes
/// don't track them. When <see cref="Scores"/> is run as the SOLE scorer (no gametype handler), it writes
/// SP_SCORE through this projection via <see cref="SetScore"/>, which updates <c>ScoreFrags</c> directly.
/// </summary>
public sealed class PlayerScoreRow
{
    /// <summary>The player this row belongs to.</summary>
    public Player Player { get; }

    public PlayerScoreRow(Player player) => Player = player;

    // Map the dense Server-side enum onto the shared GameScores SP_* columns — the single source of truth, so
    // every column (incl. SP_SCORE == Player.ScoreFrags) is one networked value, not a private projection.
    internal static Common.Gameplay.Scoring.ScoreField Col(ScoreField f) => f switch
    {
        ScoreField.Kills => GameScores.Kills,
        ScoreField.Deaths => GameScores.Deaths,
        ScoreField.Suicides => GameScores.Suicides,
        ScoreField.TeamKills => GameScores.TeamKills,
        ScoreField.Score => GameScores.Score,
        ScoreField.Damage => GameScores.Dmg,
        ScoreField.DamageTaken => GameScores.DmgTaken,
        _ => GameScores.Score,
    };

    /// <summary>Read a field — every column lives in the unified <see cref="GameScores"/> store.</summary>
    public int Get(ScoreField f) => GameScores.Get(Player, Col(f));

    /// <summary>SP_* convenience accessors.</summary>
    public int Kills => GameScores.Get(Player, GameScores.Kills);
    public int Deaths => GameScores.Get(Player, GameScores.Deaths);
    public int Suicides => GameScores.Get(Player, GameScores.Suicides);
    public int TeamKills => GameScores.Get(Player, GameScores.TeamKills);
    public int Score => Player.ScoreFrags;              // == GameScores SP_SCORE (the authoritative match score)
    public int DamageDealt => GameScores.Get(Player, GameScores.Dmg);
    public int DamageTaken => GameScores.Get(Player, GameScores.DmgTaken);

    /// <summary>Add to a column (QC PlayerScore_Add). For SP_SCORE the gametype owns it — write via the table's
    /// sole-scorer path; here we route every column through the unified store so it networks.</summary>
    internal int AddAux(ScoreField f, int delta) => GameScores.AddToPlayer(Player, Col(f), delta);

    /// <summary>Write SP_SCORE through to the authoritative <see cref="Player.ScoreFrags"/> (sole-scorer mode).</summary>
    internal void SetScore(int value) => Player.ScoreFrags = value;

    /// <summary>Reset every column to 0 (QC PlayerScore_Clear), including the score, + accuracy/streaks.</summary>
    internal void Clear()
    {
        GameScores.ClearPlayer(Player);
        Accuracy.Clear();
        AccuracyGeneration++; // the bytes all dropped to 0 — force a wire resend
        KillStreak = 0;
        BestKillStreak = 0;
        MultiKill = 0;
        MultiKillBest = 0;
        HandicapAvgGivenSum = 0f;
        HandicapAvgTakenSum = 0f;
        _lastKillTime = 0f;
    }

    /// <summary>
    /// QC <c>PlayerScore_Clear</c> on a team change (KillPlayerForTeamChange): reset the auxiliary score
    /// columns + accuracy/streak counters so a mid-match team move doesn't carry kills/deaths to the new
    /// team. Deliberately leaves SP_SCORE (the frag total) alone. The DEATH_TEAMCHANGE suicide that follows
    /// in <see cref="Teamplay.KillPlayerForTeamChange"/> negates exactly one frag (the gametype's suicide
    /// branch / Obituary SUICIDE −1), matching Base where DEATH_TEAMCHANGE (unlike AUTOTEAMCHANGE) is NOT in
    /// the GiveFrags-skip exception (server/damage.qc:304). Note: Base's own SP_SCORE wipe lives in
    /// PlayerScore_Clear and is gated on <c>g_score_resetonjoin</c> (default 0 → no-op), so by default Base
    /// also keeps SP_SCORE; this port unconditionally clears only the aux columns (a separate scores-parity
    /// concern), but the frag-negation result here matches stock Base.
    /// </summary>
    internal void ClearForTeamChange()
    {
        int keepScore = Player.ScoreFrags;
        GameScores.ClearPlayer(Player);
        Player.ScoreFrags = keepScore;
        Accuracy.Clear();
        AccuracyGeneration++;
        KillStreak = 0;
        MultiKill = 0;
    }

    // ---------------------------------------------------------------------------------------------
    // per-weapon accuracy + kill-streak / multikill medals (QC accuracy.qc + the spree counters)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Per-weapon accuracy tallies (QC the <c>.accuracy</c> sub-entity's fired/hit/real damage arrays +
    /// cnt/frag counters), keyed by weapon NetName. Filled by the <see cref="WeaponAccuracyEvents"/> bus
    /// (the accuracy_add credits at SetupShot / the hit sites / PlayerDamage) once
    /// <see cref="Scores.SubscribeToDeaths"/> wired it, plus the frag credit in <c>RecordWeaponKill</c>.
    /// </summary>
    public readonly WeaponAccuracy Accuracy = new();

    /// <summary>
    /// [T57] Bumped whenever any weapon's networked accuracy_byte changes (QC's <c>SendFlags |= BIT(...)</c>
    /// change detection, accuracy.qc:125-127) or the row is cleared — the snapshot composer resends the
    /// accuracy block only when this moves (see <see cref="Scores.AccuracyGeneration"/>).
    /// </summary>
    public int AccuracyGeneration { get; internal set; }

    /// <summary>QC the current kill spree (<c>.killcount</c>): consecutive kills without dying.</summary>
    public int KillStreak { get; internal set; }

    /// <summary>The best kill spree this match (for the end-of-match medal).</summary>
    public int BestKillStreak { get; internal set; }

    /// <summary>QC the rolling multikill count (kills within the multikill window).</summary>
    public int MultiKill { get; internal set; }

    /// <summary>The best multikill this match (double/triple/... medal).</summary>
    public int MultiKillBest { get; internal set; }

    /// <summary>
    /// QC <c>.handicap_avg_given_sum</c> / <c>.handicap_avg_taken_sum</c> (server/handicap.qh:71-72): the
    /// damage-weighted running sums for the XonStat <c>handicapgiven</c>/<c>handicaptaken</c> report events.
    /// Accumulated each frame the player deals/takes damage as <c>realdmg * Handicap_GetTotalHandicap(...)</c>
    /// (QC PlayerFrame, server/client.qc:2865/2871); divided by the total DMG/DMGTAKEN score column at report
    /// time to yield the average handicap over the match.
    /// </summary>
    public float HandicapAvgGivenSum { get; internal set; }
    public float HandicapAvgTakenSum { get; internal set; }

    internal float _lastKillTime;
}

/// <summary>
/// Per-weapon accuracy bookkeeping — the Godot-free essence of the QC <c>accuracy</c> sub-entity
/// (server/weapons/accuracy.qc), keyed by weapon NetName. [T57] reworked from shot COUNTS to the QC DAMAGE
/// columns: <c>fired</c> = total potential damage of every shot, <c>hit</c> = total damage dealt (may
/// exceed fired — networked as 255), <c>real</c> = damage dealt minus overkill excess, plus the per-shot
/// <c>cnt_fired</c>/<c>cnt_hit</c> counters (once per server frame, accuracy.qc:115-123) and the per-weapon
/// frag count (player.qc:493-495). Accuracy% = hit damage / fired damage, exactly QC's accuracy_byte ratio.
/// </summary>
public sealed class WeaponAccuracy
{
    private sealed class Cols
    {
        public float Fired, Hit, Real;
        public int CntFired, CntHit, Frags;
    }

    private readonly Dictionary<string, Cols> _cols = new(StringComparer.Ordinal);

    // QC .fired_time / STAT(HIT_TIME) — the once-per-server-frame cnt guards (shared across weapons).
    internal float FiredTime = -1f;
    internal float HitTime = -1f;

    private Cols Get(string weapon)
    {
        if (!_cols.TryGetValue(weapon, out Cols? c))
            _cols[weapon] = c = new Cols();
        return c;
    }

    /// <summary>accuracy_fired += maxdamage (potential damage of a shot).</summary>
    public void AddFired(string weapon, float damage)
    {
        if (string.IsNullOrEmpty(weapon)) return;
        Get(weapon).Fired += damage;
    }

    /// <summary>accuracy_hit += damage dealt (pre-excess; may exceed fired).</summary>
    public void AddHit(string weapon, float damage)
    {
        if (string.IsNullOrEmpty(weapon)) return;
        Get(weapon).Hit += damage;
    }

    /// <summary>accuracy_real += post-excess damage (the PlayerDamage real credit).</summary>
    public void AddReal(string weapon, float damage)
    {
        if (string.IsNullOrEmpty(weapon)) return;
        Get(weapon).Real += damage;
    }

    /// <summary>++accuracy_cnt_fired (the caller enforces the once-per-frame guard via <see cref="FiredTime"/>).</summary>
    public void IncCntFired(string weapon) { if (!string.IsNullOrEmpty(weapon)) Get(weapon).CntFired++; }

    /// <summary>++accuracy_cnt_hit (the caller enforces the once-per-frame guard via <see cref="HitTime"/>).</summary>
    public void IncCntHit(string weapon) { if (!string.IsNullOrEmpty(weapon)) Get(weapon).CntHit++; }

    /// <summary>++accuracy_frags (QC player.qc:495 — the kill credit, kept apart from the hit columns).</summary>
    public void AddFrag(string weapon) { if (!string.IsNullOrEmpty(weapon)) Get(weapon).Frags++; }

    public float FiredDamage(string weapon) => _cols.TryGetValue(weapon, out Cols? c) ? c.Fired : 0f;
    public float HitDamage(string weapon) => _cols.TryGetValue(weapon, out Cols? c) ? c.Hit : 0f;
    public float RealDamage(string weapon) => _cols.TryGetValue(weapon, out Cols? c) ? c.Real : 0f;
    public int CntFired(string weapon) => _cols.TryGetValue(weapon, out Cols? c) ? c.CntFired : 0;
    public int CntHit(string weapon) => _cols.TryGetValue(weapon, out Cols? c) ? c.CntHit : 0;
    public int Frags(string weapon) => _cols.TryGetValue(weapon, out Cols? c) ? c.Frags : 0;

    /// <summary>The hit/fired DAMAGE accuracy fraction (0..1; can exceed 1 like QC's 255 byte), 0 when never fired.</summary>
    public float Fraction(string weapon)
    {
        float fired = FiredDamage(weapon);
        return fired > 0f ? HitDamage(weapon) / fired : 0f;
    }

    /// <summary>Weapon NetNames that have any recorded activity (for a scoreboard column).</summary>
    public IEnumerable<string> Weapons()
    {
        foreach (var kv in _cols)
            if (kv.Value.Fired != 0f || kv.Value.Hit != 0f || kv.Value.Real != 0f || kv.Value.Frags != 0)
                yield return kv.Key;
    }

    public void Clear()
    {
        _cols.Clear();
        FiredTime = -1f;
        HitTime = -1f;
    }
}

/// <summary>
/// The unified score table — the C# successor to the server scoring layer
/// (server/scores.qc <c>PlayerScore_Add</c> / <c>TeamScore_Add</c> / <c>Score_ClearAll</c> +
/// the obituary <c>GiveFrags</c> classification in server/damage.qc). It owns a
/// <see cref="PlayerScoreRow"/> per player and exposes one <see cref="Obituary"/>/<see cref="GiveFrags"/> API
/// for the per-kill bookkeeping (kills/deaths/suicides/teamkills) the gametypes don't already do.
///
/// Ownership (see <see cref="PlayerScoreRow"/>): the active gametype is the authoritative frag-scorer — it
/// writes <see cref="Player.ScoreFrags"/> and (for team modes) its own per-team totals. To avoid
/// double-counting, this table by default tracks ONLY the auxiliary columns and projects SP_SCORE / the
/// team score read-through from the gametype. It can ALSO run as the sole scorer (<see cref="OwnsScore"/> =
/// true) when no gametype handler is active — then it writes SP_SCORE (and its own team totals) using the
/// QC DM matrix (enemy +1, suicide/world −1, teamkill −1).
///
/// Two ways to drive it:
///  - <see cref="SubscribeToDeaths"/> hooks <see cref="Combat.Death"/> directly, so the table fills itself
///    from the damage pipeline; or
///  - a gametype/controller calls <see cref="Obituary"/> / <see cref="GiveFrags"/> explicitly.
///
/// Now tracked too: per-weapon accuracy (the <see cref="WeaponAccuracyEvents"/> bus feeds the
/// fired/hit/real damage columns; the kill path adds the separate frag credit in <see cref="Obituary"/>),
/// kill-spree + multikill medals
/// (<see cref="PlayerScoreRow.BestKillStreak"/>/<see cref="PlayerScoreRow.MultiKillBest"/>), and the
/// AddPlayerScore hook (<see cref="AddPlayerScoreHook"/>).
///
/// The network scoreboard (ScoreInfoBlock / ScoreboardBlock), playerstats reporting (PlayerStats),
/// and the SFL_* sort-flag metadata are all implemented.
/// </summary>
public sealed class Scores
{
    private readonly Dictionary<Player, PlayerScoreRow> _rows = new();
    private readonly Dictionary<int, int> _teamScores = new(); // team color code -> running score (sole-scorer mode)

    /// <summary>
    /// True when this table is the authoritative scorer (no gametype owns SP_SCORE / team totals). Then
    /// <see cref="Obituary"/> writes SP_SCORE via the DM matrix and maintains its own team totals. False
    /// (the default under <see cref="GameWorld"/>) means a gametype owns the frag score and this table only
    /// records the aux columns + reads through. Set by <see cref="SubscribeToDeaths"/>.
    /// </summary>
    public bool OwnsScore { get; private set; }

    /// <summary>
    /// QC <c>checkrules_firstblood</c> (server/damage.qc): the once-per-match first-blood latch. Set true the
    /// first time an enemy frag lands outside warmup, so that frag (and only that frag) renders the
    /// "First blood!"/"First victim!" spree banner (kill_count_to_attacker = -1 / target = -2). Reset by
    /// <see cref="ClearAll"/> on a match restart. T40.
    /// </summary>
    private bool _checkrulesFirstblood;

    /// <summary>
    /// Optional read-through for team scores when a gametype owns them (its per-team dict). When set,
    /// <see cref="TeamScore"/> returns the gametype's value rather than this table's local total — so the
    /// scoreboard reflects the authoritative team score without this table double-counting. Null in
    /// sole-scorer mode (then the local <c>_teamScores</c> totals are authoritative).
    /// </summary>
    public Func<int, int>? TeamScoreSource { get; set; }

    /// <summary>True once <see cref="SubscribeToDeaths"/> wired the obituary handler (mirrors QC scores_initialized).</summary>
    public bool Subscribed { get; private set; }

    /// <summary>
    /// [A5 #8] Per-recipient MSG_CHOICE resolution source — the C# successor to QC reading
    /// <c>CS(recipient).msg_choice_choices[idx]</c> inside <c>Send_Notification_Core</c>'s per-client send loop.
    /// The host wires this to <see cref="Commands.GetChoiceState"/> so each <see cref="NotifBroadcast.One"/>
    /// frag/typefrag centerprint resolves option A (terse) vs option B (verbose) from THAT recipient's replicated
    /// <c>notification_CHOICE_*</c> preference rather than a single global value. Null (standalone tests / an
    /// unwired host) → the dispatch falls back to <see cref="NotificationSystem.DefaultChoiceValue"/>.
    /// </summary>
    public Func<Player, NotificationChoiceState?>? ChoiceStateProvider { get; set; }

    /// <summary>
    /// QC <c>GameLogEcho</c> seam for the kill event-log line — the host wires this to <c>GameLog.Echo</c>
    /// (gated on <c>sv_eventlog</c> at the call site, the QC pattern). When set, <see cref="Obituary"/> emits
    /// the <c>:kill:&lt;mode&gt;:&lt;killer&gt;:&lt;killed&gt;:type=&lt;deathtype&gt;:items=…</c> line (the port's
    /// <c>LogDeath</c>, server/damage.qc:100). Null (a standalone test / unwired host) → no kill log emitted.
    /// </summary>
    public Action<string>? LogDeath { get; set; }

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>
    /// [A5 #8] Send a MSG_CHOICE notification to a single recipient, resolving its option A/B from THAT
    /// recipient's replicated choice preferences first — QC <c>Send_Notification_Core</c> reads
    /// <c>CS(recipient).msg_choice_choices[idx]</c> per recipient inside the FOREACH_CLIENT send loop. The static
    /// <see cref="NotificationSystem"/> dispatch keys its <see cref="NotificationSystem.ChoiceValues"/> map by the
    /// choice's RegistryName, so prime that map from the recipient's <see cref="NotificationChoiceState"/> (via
    /// <see cref="ChoiceStateProvider"/>) immediately before the <see cref="NotifBroadcast.One"/> Send. When no
    /// per-client state exists the map is cleared, so the dispatch falls back to
    /// <see cref="NotificationSystem.DefaultChoiceValue"/> (option A) — the pre-A5#8 behavior.
    /// </summary>
    private void SendChoiceToOne(Player recipient, string name, params object[] args)
    {
        NotificationSystem.ChoiceValues.Clear();
        ChoiceStateProvider?.Invoke(recipient)?.ApplyTo(NotificationSystem.ChoiceValues);
        NotificationSystem.Send(NotifBroadcast.One, recipient, MsgType.Choice, name, args);
    }

    // ---------------------------------------------------------------------------------------------
    // roster registration (QC PlayerScore_Attach / _Detach)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Register a player so they have a score row (QC PlayerScore_Attach). Idempotent.</summary>
    public PlayerScoreRow Register(Player p)
    {
        if (!_rows.TryGetValue(p, out var row))
        {
            row = new PlayerScoreRow(p);
            _rows[p] = row;
        }
        return row;
    }

    /// <summary>Drop a player's score row (QC PlayerScore_Detach, on disconnect).</summary>
    public void Unregister(Player p) => _rows.Remove(p);

    /// <summary>The score row for a player (created on demand — QC implicitly attaches on join).</summary>
    public PlayerScoreRow Row(Player p) => Register(p);

    /// <summary>Snapshot of all rows (scoreboard order is the caller's concern; see <see cref="Sorted"/>).</summary>
    public IReadOnlyCollection<PlayerScoreRow> Rows => _rows.Values;

    // ---------------------------------------------------------------------------------------------
    // the unified GiveFrags / Obituary API (QC server/damage.qc)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// QC <c>PlayerScore_Add(player, field, delta)</c> — add to one of a player's score columns. For SP_SCORE
    /// this only takes effect in sole-scorer mode (<see cref="OwnsScore"/>); otherwise it is a no-op for that
    /// field because the gametype owns the frag score (it is read through from <see cref="Player.ScoreFrags"/>).
    /// Returns the field's resulting value.
    /// </summary>
    public int Add(Player p, ScoreField field, int delta)
    {
        // QC MUTATOR_CALLHOOK(AddPlayerScore, ...): let a host/mutator veto or rewrite a score change.
        if (AddPlayerScoreHook is not null)
        {
            var (allow, newDelta) = AddPlayerScoreHook(p, field, delta);
            if (!allow)
                return Row(p).Get(field);
            delta = newDelta;
        }

        PlayerScoreRow row = Row(p);
        if (field == ScoreField.Score)
        {
            if (OwnsScore)
                row.SetScore(row.Score + delta);
            return row.Score;
        }
        return row.AddAux(field, delta);
    }

    /// <summary>
    /// QC the <c>AddPlayerScore</c> mutator hook: invoked before any <see cref="Add"/> applies, the handler
    /// returns (allow, delta) — return allow=false to veto the change, or a rewritten delta to scale it
    /// (e.g. a double-score powerup). Null = no hook (every change applies as-is).
    /// </summary>
    public Func<Player, ScoreField, int, (bool allow, int delta)>? AddPlayerScoreHook { get; set; }

    // ---------------------------------------------------------------------------------------------
    // per-weapon accuracy (QC server/weapons/accuracy.qc accuracy_add via the WeaponAccuracyEvents bus)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// [T57] The <see cref="WeaponAccuracyEvents.Added"/> handler — the tally half of QC
    /// <c>accuracy_add</c> (accuracy.qc:102-129): the fired/hit/real damage columns, the once-per-server-
    /// frame cnt_fired/cnt_hit counters (the fired_time / STAT(HIT_TIME) guards), and the accuracy_byte
    /// change detection that drives the wire resend (the QC SendFlags bit).
    /// </summary>
    private void OnAccuracyAdd(Entity attacker, Weapon weapon, float fired, float hit, float real)
    {
        if (attacker is not Player p || !_rows.TryGetValue(p, out PlayerScoreRow? row))
            return; // only score registered players (QC: no accuracy entity attached)

        WeaponAccuracy acc = row.Accuracy;
        string key = weapon.NetName;
        int before = WeaponAccuracyEvents.AccuracyByte(acc.HitDamage(key), acc.FiredDamage(key));

        if (real != 0f) acc.AddReal(key, real);
        if (hit != 0f) acc.AddHit(key, hit);
        if (fired != 0f) acc.AddFired(key, fired);

        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (hit != 0f && acc.HitTime != now) // only run this once per frame (accuracy.qc:115)
        {
            acc.IncCntHit(key);
            acc.HitTime = now;
        }
        if (fired != 0f && acc.FiredTime != now) // only run this once per frame (accuracy.qc:120)
        {
            acc.IncCntFired(key);
            acc.FiredTime = now;
        }

        if (before != WeaponAccuracyEvents.AccuracyByte(acc.HitDamage(key), acc.FiredDamage(key)))
            row.AccuracyGeneration++; // QC SendFlags |= BIT(wepid % 24) — networked-byte change
    }

    /// <summary>
    /// [T57] The accuracy wire payload for one player — one QC <c>accuracy_byte</c> per weapon RegistryId
    /// (ENT_CLIENT_ACCURACY's per-weapon byte array): 0 = never fired, 1..101 = accuracy% + 1, 255 = &gt;100%.
    /// </summary>
    public byte[] AccuracyBytes(Player p)
    {
        WeaponAccuracy acc = Row(p).Accuracy;
        var bytes = new byte[Registry<Weapon>.Count];
        for (int id = 0; id < bytes.Length; id++)
        {
            string name = Registry<Weapon>.ById(id).NetName;
            bytes[id] = (byte)WeaponAccuracyEvents.AccuracyByte(acc.HitDamage(name), acc.FiredDamage(name));
        }
        return bytes;
    }

    /// <summary>[T57] The player's accuracy change counter — resend the wire block when this moves.</summary>
    public int AccuracyGeneration(Player p) => Row(p).AccuracyGeneration;

    /// <summary>
    /// QC <c>GiveFrags(attacker, targ, f)</c> reduced to its scoreboard effect: award <paramref name="amount"/>
    /// SP_SCORE to <paramref name="attacker"/> (and the attacker's team in a team game). A null attacker is a
    /// no-op (world deaths credit nobody). Only mutates the score in sole-scorer mode (<see cref="OwnsScore"/>);
    /// under a gametype this is the call a gametype would route through, but the bundled gametypes already
    /// write <see cref="Player.ScoreFrags"/> themselves, so the table runs in read-through mode.
    /// </summary>
    public void GiveFrags(Player? attacker, Player victim, int amount, bool teamGame)
    {
        if (attacker is null || amount == 0)
            return;
        if (OwnsScore)
        {
            Add(attacker, ScoreField.Score, amount);
            if (teamGame)
                AddTeamScore((int)attacker.Team, amount);
        }
        _ = victim; // (kept in the signature to mirror QC; victim-side bookkeeping is in Obituary)
    }

    /// <summary>
    /// The unified obituary — the Godot-free essence of QC <c>Obituary</c> (server/damage.qc): classify the
    /// kill and update the AUX columns it owns (the victim's deaths/suicides, the attacker's kills/teamkills).
    /// SP_SCORE / team totals are only touched in sole-scorer mode (otherwise the gametype owns them). This
    /// keeps the scoreboard's kill/death/etc. columns consistent across every gametype in one place.
    /// <paramref name="teamGame"/> selects the team-aware matrix.
    /// </summary>
    public void Obituary(Player? attacker, Player victim, string deathType, bool teamGame, Entity? inflictor = null)
    {
        // QC reads CS(targ).killcount (the victim's spree) for the obituary's spree_end/spree_lost arg BEFORE it
        // resets it to 0 (server/damage.qc:480). Capture it now so EmitObituary renders "ending their N frag
        // spree" / "losing their N frag spree" before the reset below zeroes it. T40.
        int victimStreakBefore = Row(victim).KillStreak;

        Row(victim).AddAux(ScoreField.Deaths, 1); // QC: victim always gets a death

        bool teamKill = teamGame && attacker is not null && !ReferenceEquals(attacker, victim)
                        && Teams.SameTeam(attacker, victim);

        if (attacker is null || ReferenceEquals(attacker, victim))
        {
            // SUICIDE / world death (QC "SUICIDE"/"ACCIDENT-TRAP"): +1 suicide; −1 frag (only if we own score).
            Row(victim).AddAux(ScoreField.Suicides, 1);
            // QC server/damage.qc:304 special-cases DEATH_AUTOTEAMCHANGE (an auto-balance team move): the suicide
            // frag is NOT negated. A manual DEATH_TEAMCHANGE still negates. (In the live path OwnsScore is false —
            // each gametype owns its own suicide −1 and applies the same skip — so this gate guards the sole-scorer
            // configuration; it is the central mirror of damage.qc:304.)
            if (OwnsScore && !DeathTypes.IsAutoTeamChange(deathType))
            {
                Add(victim, ScoreField.Score, -1);
                if (teamGame) AddTeamScore((int)victim.Team, -1);
            }
        }
        else if (teamKill)
        {
            // TEAMKILL (QC "FRAG"/team, server/damage.qc:GiveFrags): +1 teamkill; base −1 frag. When
            // autocvar_g_teamkill_punishing (default 0/off) is set, the penalty escalates with the attacker's
            // running teamkill count: f -= (teamkills * (teamkills - 1)) * 0.5 → −1, −2, −4, −7, −11, … as the
            // total reaches 1, 2, 3, 4, 5, … (AddAux returns the new running total, mirroring the value QC reads
            // back from GameRules_scoring_add(attacker, TEAMKILLS, 1)). The curve term is always integral.
            int teamkills = Row(attacker).AddAux(ScoreField.TeamKills, 1);
            int penalty = -1;
            if (Api.Services is not null && Api.Cvars.GetFloat("g_teamkill_punishing") != 0f)
                penalty -= (int)((teamkills * (teamkills - 1)) * 0.5f);
            if (OwnsScore)
            {
                Add(attacker, ScoreField.Score, penalty);
                AddTeamScore((int)attacker.Team, penalty);
            }
        }
        else
        {
            // ENEMY FRAG (QC "MURDER"): attacker +1 kill, +1 score (the latter only if we own score).
            Row(attacker).AddAux(ScoreField.Kills, 1);

            // QC GiveFrags fires MUTATOR_CALLHOOK(GiveFragsForKill, attacker, targ, f, deathtype, weaponentity)
            // and reads the (possibly-adjusted) delta back via f = M_ARGV(2, float) when the hook handled it
            // (server/damage.qc:72). Mirror that here: the base frag award is +1, but instagib / weaponarena_random
            // rewrite it through this chain. The attacker weapon entity isn't carried on this scoring path, so the
            // weaponentity slot is null (latent: no bundled subscriber today, but this wires the dormant site).
            int frag = 1;
            var gfk = new MutatorHooks.GiveFragsForKillArgs(attacker, victim, fragScore: 1f, deathType, weaponEntity: null);
            if (MutatorHooks.GiveFragsForKill.Call(ref gfk))
                frag = (int)gfk.FragScore;
            GiveFrags(attacker, victim, frag, teamGame);

            // QC the spree / medal + accuracy logging, all keyed off the kill's deathtype.
            RecordKillStreakAndMedals(attacker);
            RecordWeaponKill(attacker, victim, deathType);
        }

        // QC LogDeath (server/damage.qc:303/315/329/468): the structured :kill: event-log line, emitted from
        // INSIDE Obituary for each scoring branch. Mode mirrors QC: self-death -> "suicide", world/trap (no
        // attacker) -> "accident", teamkill -> "tk", enemy frag -> "frag".
        if (LogDeath is not null)
        {
            string mode = ReferenceEquals(attacker, victim) ? "suicide"
                        : attacker is null ? "accident"
                        : teamKill ? "tk"
                        : "frag";
            // QC LogDeath(mode, deathtype, killer, killed): killer = attacker (== targ for suicide/accident).
            Player killer = attacker ?? victim;
            EmitLogDeath(mode, deathType, killer, victim);
        }

        // QC Obituary's notification + sound emission (server/damage.qc:268-477): kill feed, frag/typefrag/
        // teamkill centerprints, the killstreak announcer, and first blood. Runs AFTER the scoring above so the
        // attacker's KillStreak reflects this kill (the 3rd kill announces KILLSTREAK_03). T40.
        EmitObituary(attacker, victim, deathType, teamGame, victimStreakBefore, inflictor);

        // QC: a kill resets the victim's kill spree (they died).
        if (!ReferenceEquals(attacker, victim))
        {
            var vrow = Row(victim);
            vrow.KillStreak = 0;
            vrow.MultiKill = 0;
        }
    }

    /// <summary>
    /// Port of QC <c>LogDeath</c> (server/damage.qc:100): build the colon-delimited <c>:kill:</c> event-log
    /// line and hand it to the <see cref="LogDeath"/> sink. Format (QC):
    /// <c>:kill:&lt;mode&gt;:&lt;killerid&gt;:&lt;killedid&gt;:type=&lt;deathtype&gt;:items=&lt;killer codes&gt;</c>,
    /// plus <c>:victimitems=&lt;killed codes&gt;</c> when killer != killed.
    /// </summary>
    private void EmitLogDeath(string mode, string deathType, Player killer, Player killed)
    {
        // QC strcat(":kill:", mode, ":", playerid(killer), ":", playerid(killed), ":type=", Deathtype_Name, ":items=").
        // The port's deathType IS the deathtype name (dual int/string deathtype), so it maps to Deathtype_Name directly.
        string s = $":kill:{mode}:{killer.PlayerId}:{killed.PlayerId}:type={deathType}:items=";
        s = AppendItemcodes(s, killer);
        if (!ReferenceEquals(killed, killer))
        {
            s += ":victimitems=";
            s = AppendItemcodes(s, killed);
        }
        LogDeath?.Invoke(s);
    }

    /// <summary>
    /// Port of QC <c>AppendItemcodes</c> (server/damage.qc:81): append a player's death-log item codes — the
    /// per-slot held weapon id (falling back to the previous weapon when none is current; slot 0 always emits
    /// even when 0), a trailing "T" when chatting (typing), then the <c>LogDeath_AppendItemCodes</c> mutator
    /// chain (powerups "S"/"I", etc.).
    /// </summary>
    private static string AppendItemcodes(string s, Player player)
    {
        for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; ++slot)
        {
            var st = player.WeaponState(new WeaponSlot(slot));
            int w = st.CurrentWeaponId;
            if (w <= 0)
                w = st.PrevWeaponId; // QC: previous weapon (.cnt)
            if (w > 0 || slot == 0)
                s += (w > 0 ? w : 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // QC: if(PHYS_INPUT_BUTTON_CHAT(player)) s = strcat(s, "T");
        if (player.ButtonChat)
            s += "T";
        // QC: MUTATOR_CALLHOOK(LogDeath_AppendItemCodes, player, s) → S/I (powerups), F (ctf), …
        s = MutatorActivation.LogDeathAppendItemCodes(player, s);

        // QC the per-GAMETYPE LogDeath_AppendItemCodes hook (ctf/sv_ctf.qc appends "F" for a flag carrier). The
        // MutatorActivation chain above only iterates mutators; CTF is a GameType, so dispatch the active mode's
        // virtual here. GameScores.Gametype is the live mode name and GameTypes.ByName returns the activated
        // singleton, so its live flag-carrier state (CarriedBy) is correct; non-CTF modes return s unchanged.
        if (GameTypes.ByName(GameScores.Gametype) is { } activeMode)
            s = activeMode.LogDeathAppendItemCodes(player, s);

        return s;
    }

    /// <summary>
    /// Port of the notification + announcer emission inside QC <c>Obituary</c> (server/damage.qc:268-477):
    /// the kill feed (MSG_MULTI to the victim + MSG_INFO to everyone else — <c>Obituary_WeaponDeath</c> /
    /// <c>Obituary_SpecialDeath</c>), the frag/typefrag/teamkill centerprints (MSG_CHOICE / MSG_CENTER), the
    /// killstreak announcer (KILL_SPREE_LIST), and the first-blood latch. Runs on the server right after the
    /// kill is scored. Reads the attacker's now-incremented <see cref="PlayerScoreRow.KillStreak"/> (the QC
    /// <c>CS(attacker).killcount</c>) and the victim's pre-reset streak (<paramref name="victimStreakBefore"/>,
    /// QC <c>CS(targ).killcount</c>). Mirrors the SUICIDE / MURDER(team vs enemy) / ACCIDENT branch order.
    /// </summary>
    private void EmitObituary(Player? attacker, Player victim, string deathType, bool teamGame, int victimStreakBefore, Entity? inflictor = null)
    {
        // QC: deathlocation = autocvar_notification_server_allows_location ? NearestLocation(...) : "". The cvar
        // ships 0, so location is off; the s2loc/s3loc token collapses to "" for an empty string. T40.
        const string deathloc = "";
        string victimName = victim.NetName;
        bool weapon = DeathTypes.IsWeapon(deathType);

        // =======  SUICIDE / ACCIDENT-TRAP (QC: targ == attacker, or no player attacker)  =======
        if (attacker is null || ReferenceEquals(attacker, victim))
        {
            string baseType = DeathTypes.BaseOf(deathType);

            // QC Obituary_SpecialDeath (server/damage.qc:137): `if (g_cts && deathtype == DEATH_KILL.m_id) return;`
            // — a /kill in CTS prints no obituary at all (the run is invalidated silently, no "died" line). QC's
            // g_cts is the IS_GAMETYPE(CTS) macro, NOT a cvar; the port never stamps a g_cts cvar (Handicap.cs:26)
            // and instead selects the gametype as the active GameScores.Gametype string.
            if (baseType == DeathTypes.Kill && GameScores.Gametype == "cts")
                return;

            // QC SUICIDE switch (server/damage.qc:271-273): TEAMCHANGE / AUTOTEAMCHANGE route to their own self
            // line with f1 = targ.team (the death_team float arg the DEATH_SELF_*TEAMCHANGE rows render as the
            // destination team name). The frag-negation skip for AUTOTEAMCHANGE is applied in the scoring branch
            // above (and in each gametype's own suicide handler, e.g. Tdm.OnDeath) — here we only emit the
            // correct obituary self-line.
            if (baseType == DeathTypes.TeamChange || baseType == DeathTypes.AutoTeamChange)
            {
                string tcName = baseType == DeathTypes.AutoTeamChange
                    ? "DEATH_SELF_AUTOTEAMCHANGE" : "DEATH_SELF_TEAMCHANGE";
                // row args (selector "s1 death_team s2loc"): SplitArgs takes ALL strings first (s1=victim,
                // s2loc=location), then ALL floats (death_team=victim.team). QC passes the same: Obituary_SpecialDeath
                // (server/damage.qc:272) -> s1=targ.netname, s2=deathlocation, ..., f1=targ.team.
                NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Multi, tcName,
                    victimName, deathloc, victim.Team);
                NotificationSystem.Send(NotifBroadcast.AllExcept, victim, MsgType.Info, tcName,
                    victimName, deathloc, victim.Team);
                return;
            }

            // QC DEATH_CUSTOM (server/damage.qc:454-461, ACCIDENT branch): a mapper-supplied deathmessage routes
            // to DEATH_SELF_CUSTOM ("You were %s"). The string is formatted with a leading "%s " when it has no
            // own format specifier (QC strstrofs(deathmessage,"%",0) < 0 ? strcat("%s ", deathmessage) : ...).
            // The mapper string rides on the inflictor's .Message (the same plumbing as the VOID_ENT path).
            if (baseType == DeathTypes.Custom)
            {
                string deathmessage = inflictor?.Message ?? "";
                string s2 = deathmessage.IndexOf('%') < 0 ? "%s " + deathmessage : deathmessage;
                // DEATH_SELF_CUSTOM row: (s1=victim, s2=formatted message, s3loc=location, spree_lost=killcount).
                NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Multi, "DEATH_SELF_CUSTOM",
                    victimName, s2, deathloc, victimStreakBefore);
                NotificationSystem.Send(NotifBroadcast.AllExcept, victim, MsgType.Info, "DEATH_SELF_CUSTOM",
                    victimName, s2, deathloc, victimStreakBefore);
                EmitBotlikeAchievement(attacker, victim);
                return;
            }

            // QC HURTTRIGGER msg_from_ent (server/damage.qc:281-290,442-451): a trigger_hurt (DEATH_VOID) that
            // carries a custom inflictor.message routes to DEATH_SELF_VOID_ENT with the mapper string as s2 (the
            // generic "you ended up in the wrong place" line is replaced). msg_from_ent = inflictor.message != "".
            string? entMessage = MsgFromEnt(deathType, inflictor, murder: false);
            if (entMessage is not null)
            {
                // DEATH_SELF_VOID_ENT row: (s1=victim, s2=mapper message, s3loc=location, spree_lost=killcount).
                NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Multi, "DEATH_SELF_VOID_ENT",
                    victimName, entMessage, deathloc, victimStreakBefore);
                NotificationSystem.Send(NotifBroadcast.AllExcept, victim, MsgType.Info, "DEATH_SELF_VOID_ENT",
                    victimName, entMessage, deathloc, victimStreakBefore);
                EmitBotlikeAchievement(attacker, victim);
                return;
            }

            // QC SUICIDE shape: WEAPON_*_SUICIDE / DEATH_SELF_* with (s1=netname, s2loc=location, spree_lost=killcount).
            string self = weapon
                ? DeathMessages.SelectSuicideMessage(DeathTypes.WeaponNetNameOf(deathType), deathType)
                : DeathMessages.SelectSpecial(deathType, murder: false);
            BroadcastObituary(self, victim, victimName, deathloc, "", victimStreakBefore, 0, suicide: true);
            EmitBotlikeAchievement(attacker, victim);
            return;
        }

        // =======  TEAMKILL (QC: IS_PLAYER(attacker) && SAME_TEAM)  =======
        if (teamGame && Teams.SameTeam(attacker, victim))
        {
            // QC: CS(attacker).killcount = 0 (a teamkill breaks the attacker's spree). T40.
            Row(attacker).KillStreak = 0;

            string attackerName = attacker.NetName;
            // QC: CENTER to the attacker + the victim, INFO_DEATH_TEAMKILL (team-keyed) to everyone. No weapon line.
            NotificationSystem.Send(NotifBroadcast.One, attacker, MsgType.Center, "DEATH_TEAMKILL_FRAG", victimName);
            NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Center, "DEATH_TEAMKILL_FRAGGED", attackerName);
            // QC APP_TEAM_NUM(targ.team, INFO_DEATH_TEAMKILL): the team suffix is keyed on the VICTIM's team.
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info,
                "DEATH_TEAMKILL_" + TeamSuffix(victim.Team), victimName, attackerName, deathloc, victimStreakBefore);
            return;
        }

        // =======  ENEMY FRAG (QC: the else of SAME_TEAM)  =======
        {
            string attackerName = attacker.NetName;
            int attackerStreak = Row(attacker).KillStreak;   // QC CS(attacker).killcount (already ++'d)

            // QC the killstreak announcer switch over CS(attacker).killcount (KILL_SPREE_LIST). T40.
            string? streak = KillStreakAnnce(attackerStreak);
            if (streak is not null)
                NotificationSystem.Send(NotifBroadcast.One, attacker, MsgType.Annce, streak);

            // QC first blood (server/damage.qc:354): the first enemy frag outside warmup flips the latch and
            // makes THIS frag render "First blood!"/"First victim!" via kill_count -1 / -2.
            int killCountToAttacker, killCountToTarget;
            if (!NotificationSystem.WarmupStage && !_checkrulesFirstblood)
            {
                _checkrulesFirstblood = true;
                killCountToAttacker = -1;
                killCountToTarget = -2;
            }
            else
            {
                killCountToAttacker = attackerStreak;
                killCountToTarget = 0;
            }

            // QC frag/typefrag centerprints (server/damage.qc:371). Ping isn't tracked per-player in this port;
            // pass -1 for a bot (renders "(Bot)") and 0 otherwise (the QC CS(x).ping value is unavailable). T40.
            int attackerHealth = (int)MathF.Max(attacker.GetResource(ResourceType.Health), 0f);
            int attackerArmor = (int)MathF.Max(attacker.GetResource(ResourceType.Armor), 0f);
            float victimPing = victim.IsBot ? -1f : 0f;
            float attackerPing = attacker.IsBot ? -1f : 0f;

            // Each centerprint is a MSG_CHOICE resolved PER RECIPIENT (A5 #8): the attacker's line from the
            // attacker's notification_CHOICE_* preference, the victim's from the victim's (QC reads
            // CS(recipient).msg_choice_choices[idx] for each). SendChoiceToOne primes the dispatch accordingly.
            if (victim.IsTypeFrag)
            {
                // CHOICE_TYPEFRAG to attacker (s1=victim, f1=spree_cen=kill_count_to_attacker, f2=ping).
                SendChoiceToOne(attacker, "TYPEFRAG", victimName, killCountToAttacker, victimPing);
                // CHOICE_TYPEFRAGGED to victim (s1=attacker, f1=spree_cen=kill_count_to_target, f2=health, f3=armor, f4=ping).
                SendChoiceToOne(victim, "TYPEFRAGGED", attackerName, killCountToTarget, attackerHealth, attackerArmor, attackerPing);
            }
            else if (DeathTypes.BaseOf(deathType) == DeathTypes.Fire)
            {
                // QC frag_centermessage_override: DEATH_FIRE -> CHOICE_FRAG_FIRE / CHOICE_FRAGGED_FIRE.
                SendChoiceToOne(attacker, "FRAG_FIRE", victimName, killCountToAttacker, victimPing);
                SendChoiceToOne(victim, "FRAGGED_FIRE", attackerName, killCountToTarget, attackerHealth, attackerArmor, attackerPing);
            }
            else
            {
                // QC Obituary (server/damage.qc:371) fires MUTATOR_CALLHOOK(FragCenterMessage, attacker, targ,
                // deathtype, ...) so a gametype can swap/suppress the personal frag centerprint. Freeze Tag swaps
                // FRAG/FRAGGED for FRAG_FREEZE/FRAGGED_FREEZE ("You froze X" / "You were frozen by Y") and
                // suppresses both when the target was already frozen (a void/lava re-kill). Scores' obituary
                // handler runs BEFORE FreezeTag.OnDeath (SubscribeToDeaths is added to Combat.Death before the
                // gametype Activate), so at hook time IsFrozen(victim) still reflects the PRE-death state, exactly
                // matching QC's STAT(FROZEN, frag_target) test.
                var fcm = new MutatorHooks.FragCenterMessageArgs(attacker, victim, deathType);
                MutatorHooks.FragCenterMessage.Call(ref fcm);
                if (!fcm.Suppress)
                {
                    SendChoiceToOne(attacker, fcm.AttackerChoice, victimName, killCountToAttacker, victimPing);
                    SendChoiceToOne(victim, fcm.TargetChoice, attackerName, killCountToTarget, attackerHealth, attackerArmor, attackerPing);
                }
            }

            // QC HURTTRIGGER msg_from_ent (server/damage.qc:420-426): an enemy-credited trigger_hurt (DEATH_VOID)
            // carrying a custom inflictor.message2 routes to DEATH_MURDER_VOID_ENT with the mapper string as s3
            // (s4loc=location). msg_from_ent = (DEATH_ENT == HURTTRIGGER && inflictor.message2 != "").
            string? entMurder = MsgFromEnt(deathType, inflictor, murder: true);
            if (entMurder is not null)
            {
                // DEATH_MURDER_VOID_ENT row: (s1=victim, s2=attacker, s3=mapper message2, s4loc=location,
                // f1=spree_end=victimStreak, f2=spree_inf=killCountToAttacker).
                NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Multi, "DEATH_MURDER_VOID_ENT",
                    victimName, attackerName, entMurder, deathloc, victimStreakBefore, killCountToAttacker);
                NotificationSystem.Send(NotifBroadcast.AllExcept, victim, MsgType.Info, "DEATH_MURDER_VOID_ENT",
                    victimName, attackerName, entMurder, deathloc, victimStreakBefore, killCountToAttacker);
                return;
            }

            // QC the kill feed (server/damage.qc:418): Obituary_WeaponDeath, falling back to Obituary_SpecialDeath.
            string murder = weapon
                ? DeathMessages.SelectKillMessage(DeathTypes.WeaponNetNameOf(deathType), deathType)
                : DeathMessages.SelectSpecial(deathType, murder: true);
            BroadcastObituary(murder, victim, victimName, attackerName, deathloc, victimStreakBefore, killCountToAttacker, suicide: false);
        }
    }

    /// <summary>
    /// QC the <c>Obituary_WeaponDeath</c> / <c>Obituary_SpecialDeath</c> kill-feed send pair: MSG_MULTI to the
    /// victim (their personal info + any center sub) and MSG_INFO to everyone else (the global kill feed).
    /// For a suicide the arg shape is (s1=victim, s2loc=location, spree_lost=victimStreak); for a murder it is
    /// (s1=victim, s2=attacker, s3loc=location, spree_inf via f2, spree_end via f1). T40.
    /// </summary>
    private static void BroadcastObituary(string bareName, Player victim, string s1, string s2OrAttacker,
        string s3Location, int victimStreak, int killCountToAttacker, bool suicide)
    {
        if (suicide)
        {
            // (2 strings, 1 float): s1, s2loc, spree_lost.
            NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Multi, bareName, s1, s2OrAttacker, victimStreak);
            NotificationSystem.Send(NotifBroadcast.AllExcept, victim, MsgType.Info, bareName, s1, s2OrAttacker, victimStreak);
        }
        else
        {
            // (3 strings, 2 floats): s1=victim, s2=attacker, s3loc, f1=victimStreak (spree_end), f2=killCountToAttacker (spree_inf).
            NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Multi, bareName,
                s1, s2OrAttacker, s3Location, victimStreak, killCountToAttacker);
            NotificationSystem.Send(NotifBroadcast.AllExcept, victim, MsgType.Info, bareName,
                s1, s2OrAttacker, s3Location, victimStreak, killCountToAttacker);
        }
    }

    /// <summary>
    /// QC the ACCIDENT/TRAP BOTLIKE achievement (server/damage.qc:466-471): when a world/accident death (NO
    /// player attacker — <paramref name="attacker"/> is null) drives the victim's running SP_SCORE to exactly
    /// −5, announce ANNCE_ACHIEVEMENT_BOTLIKE to them. A genuine self-suicide (<c>targ == attacker</c>, a
    /// non-null attacker) is NOT eligible — QC only checks this in the no-attacker ACCIDENT branch.
    /// NOTE (live-path ordering caveat): QC reads SP_SCORE *after* its <c>GiveFrags(targ,targ,-1)</c>. In
    /// sole-scorer mode (<see cref="OwnsScore"/>=true) the central <see cref="Obituary"/> applies that −1 before
    /// this runs, so the read matches QC. In read-through mode (the live DM path, OwnsScore=false) the gametype's
    /// own death handler applies <c>ScoreFrags-=1</c> *after* <see cref="Scores"/> (it subscribes to Combat.Death
    /// later), so <c>Row(victim).Score</c> here still excludes this death's −1 — the −5 medal can therefore latch
    /// one accident death late on that path. Cosmetic-only (the rare bot-like self-destruct medal); a faithful fix
    /// would need the Scores/gametype death-handler ordering reconciled, which is out of this unit's scope.
    /// </summary>
    private void EmitBotlikeAchievement(Player? attacker, Player victim)
    {
        if (attacker is not null)
            return; // QC: only the ACCIDENT/TRAP branch (no player attacker) announces BOTLIKE.
        if (Row(victim).Score == -5)
            NotificationSystem.Send(NotifBroadcast.One, victim, MsgType.Annce, "ACHIEVEMENT_BOTLIKE");
    }

    /// <summary>
    /// QC the KILL_SPREE_LIST switch in Obituary (server/damage.qc:346): the ANNCE_KILLSTREAK_## announcer for a
    /// milestone kill count (3/5/10/15/20/25/30), or null for a non-milestone. The bare name maps the milestone
    /// to its zero-padded suffix (3 -> KILLSTREAK_03, 5 -> KILLSTREAK_05, …). T40.
    /// </summary>
    private static string? KillStreakAnnce(int killcount) => killcount switch
    {
        3 => "KILLSTREAK_03",
        5 => "KILLSTREAK_05",
        10 => "KILLSTREAK_10",
        15 => "KILLSTREAK_15",
        20 => "KILLSTREAK_20",
        25 => "KILLSTREAK_25",
        30 => "KILLSTREAK_30",
        _ => null,
    };

    /// <summary>
    /// QC <c>msg_from_ent</c> (server/damage.qc:283,420,444): a <c>trigger_hurt</c> (the HURTTRIGGER /
    /// <see cref="DeathTypes.Void"/> deathtype) routes the obituary through the <c>DEATH_*_VOID_ENT</c> rows when
    /// its inflictor carries a mapper-authored death string — <c>.message</c> for the self/accident line, or
    /// <c>.message2</c> for the murder line (an enemy who took the trigger over). Returns the custom string, or
    /// <c>null</c> to fall back to the fixed <c>DEATH_*_VOID</c> line (non-void deathtype, no inflictor, or empty
    /// field). Only HURTTRIGGER gets this treatment in QC; the port carries trigger_hurt as DEATH_VOID.
    /// </summary>
    private static string? MsgFromEnt(string deathType, Entity? inflictor, bool murder)
    {
        if (inflictor is null || DeathTypes.BaseOf(deathType) != DeathTypes.Void)
            return null;
        string msg = murder ? inflictor.Message2 : inflictor.Message;
        return string.IsNullOrEmpty(msg) ? null : msg;
    }

    /// <summary>QC APP_TEAM_NUM(team, …): the team color suffix (RED/BLUE/YELLOW/PINK) for a team color code.</summary>
    private static string TeamSuffix(float team) => (int)team switch
    {
        Teams.Red => "RED",
        Teams.Blue => "BLUE",
        Teams.Yellow => "YELLOW",
        Teams.Pink => "PINK",
        _ => "RED", // QC APP_TEAM_NUM clamps; a teamkill always has a real team.
    };

    /// <summary>
    /// QC the kill-spree + multikill tracking (server/damage.qc Obituary's <c>.killcount</c> bump +
    /// the multikill window): increment the attacker's current streak (tracking the match best), and the
    /// rolling multikill count when kills land within the multikill window (default 2s). Drives the
    /// end-of-match SPREE/MULTI medals.
    /// </summary>
    private void RecordKillStreakAndMedals(Player attacker)
    {
        PlayerScoreRow row = Row(attacker);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        row.KillStreak++;
        if (row.KillStreak > row.BestKillStreak)
            row.BestKillStreak = row.KillStreak;

        // QC multikill window (g_multikill_*; default ~2s): consecutive kills within the window chain up.
        const float multiKillWindow = 2f;
        if (now - row._lastKillTime <= multiKillWindow && row._lastKillTime > 0f)
            row.MultiKill++;
        else
            row.MultiKill = 1;
        if (row.MultiKill > row.MultiKillBest)
            row.MultiKillBest = row.MultiKill;
        row._lastKillTime = now;
    }

    /// <summary>
    /// [T57] QC the per-weapon FRAG credit on a kill (server/player.qc:493-495:
    /// <c>++accuracy_frags[w.m_id-1]</c> when DEATH_WEAPONOF != WEP_Null &amp;&amp; accuracy_isgooddamage):
    /// counted in its own column — the HIT columns are fed by the damage-path credits, so crediting kills
    /// as hits here (the old behavior) would double-count.
    /// </summary>
    private void RecordWeaponKill(Player attacker, Player victim, string deathType)
    {
        if (!DeathTypes.IsWeapon(deathType))
            return;
        string weapon = DeathTypes.WeaponNetNameOf(deathType);
        if (Weapons.ByName(weapon) is null)
            return; // an unresolvable weapon tag (non-weapon source) — QC's WEP_Null
        if (!WeaponAccuracyEvents.IsGoodDamage(attacker, victim))
            return;
        Row(attacker).Accuracy.AddFrag(weapon);
    }

    /// <summary>
    /// Accumulate the per-frame damage-dealt / damage-taken columns (QC PlayerFrame: score_frame_dmg →
    /// GameRules_scoring_add(DMG, ...)). Cosmetic; does not affect the win condition.
    /// </summary>
    public void AddDamageDealt(Player p, float amount)
    {
        if (amount > 0f) Row(p).AddAux(ScoreField.Damage, (int)amount);
    }

    public void AddDamageTaken(Player p, float amount)
    {
        if (amount > 0f) Row(p).AddAux(ScoreField.DamageTaken, (int)amount);
    }

    /// <summary>
    /// QC common/playerstats.qc:262-265 — the damage-weighted handicap averages for the XonStat report:
    /// <c>given&lt;=0 ? 1 : handicap_avg_given_sum / given</c> (and likewise for taken), where the denominators
    /// are the player's total DMG / DMGTAKEN score columns. Returns (1, 1) for an unregistered player.
    /// </summary>
    public (float given, float taken) HandicapReportAverages(Player p)
    {
        if (!_rows.TryGetValue(p, out PlayerScoreRow? row))
            return (1f, 1f);
        float given = row.DamageDealt;
        float taken = row.DamageTaken;
        return (given <= 0f ? 1f : row.HandicapAvgGivenSum / given,
                taken <= 0f ? 1f : row.HandicapAvgTakenSum / taken);
    }

    // ---------------------------------------------------------------------------------------------
    // team scores (QC TeamScore_AddToTeam / teamscorekeepers)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// QC <c>TeamScore_AddToTeam</c>: add to a team's running score (no-op for the neutral team). Only mutates
    /// the local total when this table owns the score; when a gametype owns team totals, team scoring goes
    /// through the gametype and this table reads through via <see cref="TeamScoreSource"/>.
    /// </summary>
    public int AddTeamScore(int team, int delta)
    {
        if (team == Teams.None)
            return TeamScore(team);
        if (!OwnsScore)
            return TeamScore(team); // gametype-owned; don't double-count
        int v = (_teamScores.TryGetValue(team, out int cur) ? cur : 0) + delta;
        _teamScores[team] = v;
        return v;
    }

    /// <summary>
    /// Read a team's running score: the gametype's value via <see cref="TeamScoreSource"/> when set
    /// (read-through), else this table's local total (sole-scorer mode). 0 if never scored.
    /// </summary>
    public int TeamScore(int team)
    {
        if (TeamScoreSource is not null)
            return TeamScoreSource(team);
        return _teamScores.TryGetValue(team, out int v) ? v : 0;
    }

    /// <summary>Ensure each active team has a (zeroed) score slot so the leader scan is stable from frame 0.</summary>
    public void SeedTeams(int teamCount)
    {
        foreach (int t in Teams.Active(teamCount))
            if (!_teamScores.ContainsKey(t))
                _teamScores[t] = 0;
    }

    // ---------------------------------------------------------------------------------------------
    // queries (QC PlayerScore_Sort / winning-team checks)
    // ---------------------------------------------------------------------------------------------

    /// <summary>The leader by the active gametype's primary key (QC the top of PlayerScore_Sort) — falls back to
    /// SP_SCORE in a plain DM — or null when no one is registered. Spectators (FRAGS_SPECTATOR) are skipped
    /// (QC PlayerScore_Sort's <c>nospectators</c>): a stale spectator score row must not be returned as leader.</summary>
    public Player? Leader
    {
        get
        {
            Player? best = null;
            foreach (var row in _rows.Values)
            {
                if (row.Player.FragsStatus == Player.FragsSpectator) continue;
                if (best is null || GameScores.ComparePlayers(row.Player, best) > 0)
                    best = row.Player;
            }
            return best;
        }
    }

    /// <summary>
    /// The leading team's color code by running team score, or <see cref="Teams.None"/> if no team scored.
    /// Reads through <see cref="TeamScore"/> so it reflects the gametype's totals when a gametype owns them.
    /// </summary>
    public int LeaderTeam
    {
        get
        {
            int bestTeam = Teams.None, bestScore = int.MinValue;
            // scan the seeded slots (read-through resolves each to the authoritative value).
            foreach (int team in _teamScores.Keys)
            {
                int score = TeamScore(team);
                if (score > bestScore) { bestScore = score; bestTeam = team; }
            }
            return bestTeam;
        }
    }

    /// <summary>
    /// Rows in scoreboard order — QC <c>PlayerScore_Sort</c> via <see cref="GameScores.ComparePlayers"/>: the
    /// active gametype's primary key (CTF_CAPS / RACE_FASTEST / DOM_TICKS / LMS_RANK / …), then secondary, then
    /// the registration-order column tiebreaks (so a plain DM still falls back to SP_SCORE then deaths). This
    /// makes the scoreboard sort per-mode rather than always by SP_SCORE. Allocates; for display, not the hot path.
    /// </summary>
    public List<PlayerScoreRow> Sorted()
    {
        // QC PlayerScore_Sort's nospectators guard: exclude FRAGS_SPECTATOR rows so a stale spectator doesn't rank.
        var list = new List<PlayerScoreRow>();
        foreach (var row in _rows.Values)
            if (row.Player.FragsStatus != Player.FragsSpectator) list.Add(row);
        // GameScores.ComparePlayers(a, b) > 0 ⇒ a ranks ahead; List.Sort wants negative when a should come first.
        list.Sort(static (a, b) => -GameScores.ComparePlayers(a.Player, b.Player));
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // lifecycle (QC Score_ClearAll / scores_initialized)
    // ---------------------------------------------------------------------------------------------

    /// <summary>QC <c>Score_ClearAll</c>: zero every player row and every team score (e.g. on map reset).</summary>
    public void ClearAll()
    {
        foreach (var row in _rows.Values)
            row.Clear();
        foreach (int team in new List<int>(_teamScores.Keys))
            _teamScores[team] = 0;
        _checkrulesFirstblood = false; // QC: a match restart re-arms first blood. T40.
    }

    /// <summary>
    /// Subscribe this table to the obituary bus (QC the implicit GiveFrags inside Obituary). After this, the
    /// table fills itself from <see cref="Combat.Death"/> for any registered player. <paramref name="teamGame"/>
    /// fixes the matrix for the active gametype. <paramref name="ownsScore"/> makes this table the
    /// authoritative frag-scorer (writes SP_SCORE + team totals); leave it false (the default) when a gametype
    /// already scores frags so the table only records the aux columns and reads SP_SCORE through. Idempotent.
    /// The handler ignores deaths of non-players and of unregistered players (e.g. monsters), matching QC's
    /// "only score player edicts" guard.
    /// </summary>
    public void SubscribeToDeaths(bool teamGame, bool ownsScore = false)
    {
        if (_deathHandler is not null)
            return;
        _teamGameForBus = teamGame;
        OwnsScore = ownsScore;
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
        Subscribed = true;

        // [T57] the accuracy bus (QC accuracy_add) + the per-frame damage score columns (score_frame_dmg /
        // score_frame_dmgtaken, player.qc:443-446) come alive with the same subscription — GameWorld's
        // existing SubscribeToDeaths call is the single wiring point. Idempotent via _deathHandler above.
        WeaponAccuracyEvents.Added += OnAccuracyAdd;
        WeaponAccuracyEvents.DamageDealtScored += OnDamageDealtScored;
        WeaponAccuracyEvents.DamageTakenScored += OnDamageTakenScored;

        // QC Create_Notification_Entity_Choice sets each MSG_CHOICE's arg counts to max(optionA, optionB) so a
        // CHOICE_FRAG/FRAGGED/TYPEFRAG Send (1s + spree_cen + ping/health/armor) validates; the C# Choice()
        // builder doesn't, so back-fill those counts here before any obituary emits (idempotent). T40.
        DeathMessages.EnsureChoiceArgCounts();
    }

    /// <summary>Unsubscribe from the obituary bus (QC teardown on map end).</summary>
    public void UnsubscribeFromDeaths()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        Subscribed = false;

        WeaponAccuracyEvents.Added -= OnAccuracyAdd;
        WeaponAccuracyEvents.DamageDealtScored -= OnDamageDealtScored;
        WeaponAccuracyEvents.DamageTakenScored -= OnDamageTakenScored;
    }

    // [T57] QC attacker.score_frame_dmg / this.score_frame_dmgtaken → the SP_DMG / SP_DMGTAKEN columns
    // (QC accumulates per frame and flushes in PlayerFrame; the integer truncation in AddDamageDealt is the
    // same rounding QC's GameRules_scoring_add ends up with on whole-number balance damage).
    private void OnDamageDealtScored(Entity attacker, float realdmg)
    {
        if (attacker is Player p && _rows.TryGetValue(p, out PlayerScoreRow? row))
        {
            // QC server/client.qc:2865 — damage-weighted avg-sum for the handicapgiven report, BEFORE the column add.
            row.HandicapAvgGivenSum += realdmg * DamageSystem.GetTotalHandicap(p, receiving: false);
            AddDamageDealt(p, realdmg);
        }
    }

    private void OnDamageTakenScored(Entity victim, float realdmg)
    {
        if (victim is Player p && _rows.TryGetValue(p, out PlayerScoreRow? row))
        {
            // QC server/client.qc:2871 — damage-weighted avg-sum for the handicaptaken report.
            row.HandicapAvgTakenSum += realdmg * DamageSystem.GetTotalHandicap(p, receiving: true);
            AddDamageTaken(p, realdmg);
        }
    }

    private bool _teamGameForBus;

    private bool OnDeath(ref DeathEvent ev)
    {
        // QC Obituary sanity: only score player victims that this table knows about.
        if (ev.Victim is not Player victim || !_rows.ContainsKey(victim))
            return false; // not consumed — other Death subscribers (gametype, mutators) still run

        Player? attacker = ev.Attacker as Player;
        // Only credit a registered attacker (an unknown attacker scores no frag, like a world death).
        if (attacker is not null && !_rows.ContainsKey(attacker))
            attacker = null;

        Obituary(attacker, victim, ev.DeathType, _teamGameForBus, ev.Inflictor);
        return false;
    }
}
