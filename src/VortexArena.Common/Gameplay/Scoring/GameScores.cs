using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Scoring;

/// <summary>
/// The unified, networked score table — the C# successor to QuakeC's scoring layer (common/scores.qh's SP_*
/// registry + server/scores.qc's <c>PlayerScore_Add</c> / <c>TeamScore_AddToTeam</c> / <c>Score_ClearAll</c>
/// / <c>PlayerScore_Clear</c> + the winning-condition comparison). It is the single source of truth for every
/// player's per-column score (the dense columns on <see cref="Entity.ScoreColumns"/>) and the per-team totals,
/// replacing the old "SP_SCORE is a projection of Entity.Frags" arrangement so the QC <c>.frags</c>-as-STATUS
/// semantics (FRAGS_PLAYER reset on respawn) can be restored.
///
/// Static, mirroring QC's global scoring functions (the scorekeeper edicts are global). The
/// <see cref="ScoreField"/> catalog is registered in QC declaration order — NOT alphabetically sorted —
/// because QC uses registration order as the player-sort tiebreak priority after the primary/secondary keys.
/// </summary>
public static class GameScores
{
    public static IReadOnlyList<ScoreField> Fields => Registry<ScoreField>.All;
    public static int FieldCount => Registry<ScoreField>.Count;
    public static ScoreField? Field(string name) => Registry<ScoreField>.ByName(name);

    // The primary / secondary sort keys (QC scores_primary / scores_secondary), set by the active gametype's
    // ScoreRules; default to SP_SCORE primary, no secondary.
    public static ScoreField? Primary { get; private set; }
    public static ScoreField? Secondary { get; private set; }

    // Team scores (QC teamscorekeepers): the QC two-slot team-score model (common/scores.qh MAX_TEAMSCORE=2).
    // Each team carries teamscores(0..1): slot 0 is ST_SCORE; slot 1 is the per-gametype slot (ST_CTF_CAPS /
    // ST_DOM_TICKS|CAPS / ST_KH_CAPS / ST_ONS_GENS / ST_NEXBALL_GOALS / ST_*_ROUNDS / ST_RACE_LAPS /
    // ST_ASSAULT_OBJECTIVES). _teamScores[slot] maps team color code -> that slot's running total. The change
    // set is the teams whose total moved since the last scoreboard send.
    /// <summary>QC ST_SCORE: the always-present team score slot (slot 0).</summary>
    public const int TeamSlotScore = 0;
    /// <summary>QC the per-gametype team score slot (slot 1: ST_CTF_CAPS / ST_DOM_TICKS / ST_KH_CAPS / …).</summary>
    public const int TeamSlotSecondary = 1;
    /// <summary>QC MAX_TEAMSCORE (common/scores.qh): the team-score array length.</summary>
    public const int MaxTeamScore = 2;
    private static readonly Dictionary<int, int>[] _teamScores =
        { new Dictionary<int, int>(), new Dictionary<int, int>() };
    private static readonly HashSet<int> _teamDirty = new();

    // QC teamscores_label[MAX_TEAMSCORE] / teamscores_flags[MAX_TEAMSCORE]: the per-slot label/flags, set by
    // ScoreInfo_SetLabel_TeamScore. An empty label hides + de-networks the slot.
    private static readonly string[] _teamLabel = { "", "" };
    private static readonly ScoreFlags[] _teamFlags = { ScoreFlags.None, ScoreFlags.None };
    // QC teamscores_primary / teamscores_flags_primary: the slot index (0/1) flagged SORT_PRIO_PRIMARY, and its
    // flags. Defaults to slot 0 (ST_SCORE), as in a plain team DM where ScoreRules_basics makes ST_SCORE primary.
    private static int _teamPrimarySlot;
    private static ScoreFlags _teamFlagsPrimary;

    // QC `gametype` / `teamplay` (the client-side globals set by NET_HANDLE(ENT_CLIENT_SCORES_INFO)): the active
    // mode's NetName (dm/ctf/rc/…) and whether it is teamplay. On the SERVER these are the live match's values;
    // the ScoreInfo block carries them so a remote client's scoreboard column-filter (isGametypeInFilter) and
    // per-mode layout match the server (client/main.qc:1052-1054). Defaults to a non-team DM.
    /// <summary>QC client global <c>gametype</c> (the active mode's NetName: dm/ctf/rc/…). Drives the scoreboard
    /// column filter + per-mode primary/secondary. Set server-side by the gametype, networked via ScoreInfo.</summary>
    public static string Gametype { get; set; } = "dm";
    /// <summary>QC client global <c>teamplay</c>: true when the active mode is a team game (column filter
    /// teams/noteams pseudo-gametypes; team-panel rendering).</summary>
    public static bool Teamplay { get; set; }

    /// <summary>
    /// QC the <c>AddPlayerScore</c> mutator hook (MUTATOR_CALLHOOK(AddPlayerScore, ...)): invoked before any
    /// <see cref="AddToPlayer"/> applies; the handler returns (allow, delta, claimed) — allow=false vetoes,
    /// a rewritten delta scales it (e.g. a double-score powerup), and <c>claimed</c> is the QC
    /// <c>mutator_returnvalue</c>: true ONLY when a handler explicitly RETURNS true to claim the write (which
    /// bypasses the game_stopped clamp). A handler that merely rewrites the delta (QC sets <c>M_ARGV(1)</c> but
    /// falls through, e.g. Survival's anonymizer) leaves <c>claimed</c> false, so game_stopped still zeroes it.
    /// Null = no hook.
    /// </summary>
    public static Func<Entity, ScoreField, int, (bool allow, int delta, bool claimed)>? AddPlayerScoreHook { get; set; }

    /// <summary>
    /// QC the <c>AddedPlayerScore</c> mutator hook (<c>MUTATOR_CALLHOOK(AddedPlayerScore, scorefield, score, player)</c>,
    /// server/scores.qc:377): the POST-write event, fired AFTER a non-zero delta has been applied to the column —
    /// distinct from the pre-write <see cref="AddPlayerScoreHook"/> (veto/rewrite). dynamic_handicap subscribes
    /// this to recompute the score-based handicap on every SP_SCORE change (caps/objective/KH/Dom/CTF points, not
    /// just frags). Args: (player, field, applied delta). Null = no subscriber.
    /// </summary>
    public static Action<Entity, ScoreField, int>? AddedPlayerScoreHook { get; set; }

    /// <summary>QC <c>game_stopped</c>: when true, score additions are dropped (warmup end / match over).</summary>
    public static bool GameStopped { get; set; }

    private static bool _registered;

    private static float Now => Api.Services is null ? 0f : Api.Clock.Time;

    // =====================================================================================
    //  Registry — the full SP_* set (common/scores.qh), in QC declaration order.
    // =====================================================================================

    /// <summary>
    /// Register the full SP_* field set (idempotent). Labels + the stable SFL_* flags are baked from QC
    /// scores_rules.qc; the active gametype refines the primary/secondary key + activates its columns via
    /// <see cref="SetLabel"/> / <see cref="ScoreRulesBasics"/>. NOT sorted — registration order is the sort
    /// tiebreak priority.
    /// </summary>
    public static void RegisterAll()
    {
        // Already registered: just restore the canonical default state (labels/flags + SP_SCORE primary). A prior
        // gametype's ScoreRules may have blanked/re-pinned columns; RegisterAll's contract is "registry in its
        // default state", so re-running it (e.g. a test's setup) reverts those without recreating field objects.
        if (_registered && FieldCount > 0) { ResetToDefaults(); return; }
        void R(ScoreField f) => Registry<ScoreField>.Register(f);

        // --- gametype columns (race / assault / ctf / dom / freezetag / keepaway / kh / lms / nexball / ons / tka / survival) ---
        R(new ScoreField("RACE_LAPS", "laps"));
        R(new ScoreField("RACE_TIME", "time", ScoreFlags.LowerIsBetter | ScoreFlags.Time));
        R(new ScoreField("RACE_FASTEST", "fastest", ScoreFlags.LowerIsBetter | ScoreFlags.Time));

        R(new ScoreField("ASSAULT_OBJECTIVES", "objectives"));

        R(new ScoreField("CTF_CAPS", "caps"));
        R(new ScoreField("CTF_FCKILLS", "fckills", ScoreFlags.HideZero));
        R(new ScoreField("CTF_RETURNS", "returns", ScoreFlags.HideZero));
        R(new ScoreField("CTF_DROPS", "drops", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter));
        R(new ScoreField("CTF_PICKUPS", "pickups", ScoreFlags.HideZero));
        R(new ScoreField("CTF_CAPTIME", "captime", ScoreFlags.LowerIsBetter | ScoreFlags.Time)); // QC field(SP_CTF_CAPTIME, "captime", SFL_LOWER_IS_BETTER | SFL_TIME)

        R(new ScoreField("DOM_TAKES", "takes"));
        R(new ScoreField("DOM_TICKS", "ticks"));

        R(new ScoreField("FREEZETAG_REVIVALS", "revivals", ScoreFlags.HideZero));

        R(new ScoreField("KEEPAWAY_BCTIME", "bctime")); // QC field(SP_KEEPAWAY_BCTIME, "bctime", SFL_SORT_PRIO_SECONDARY): sort-prio is per-mode, baseline flags none
        R(new ScoreField("KEEPAWAY_CARRIERKILLS", "bckills", ScoreFlags.HideZero));
        R(new ScoreField("KEEPAWAY_PICKUPS", "pickups", ScoreFlags.HideZero));

        R(new ScoreField("KH_CAPS", "caps"));
        R(new ScoreField("KH_KCKILLS", "kckills", ScoreFlags.HideZero));
        R(new ScoreField("KH_LOSSES", "losses", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter));
        R(new ScoreField("KH_DESTRUCTIONS", "destructions", ScoreFlags.HideZero)); // QC field(SP_KH_DESTRUCTIONS, "destructions", ...)
        R(new ScoreField("KH_PUSHES", "pushes", ScoreFlags.HideZero));
        R(new ScoreField("KH_PICKUPS", "pickups", ScoreFlags.HideZero));

        R(new ScoreField("LMS_RANK", "rank", ScoreFlags.LowerIsBetter | ScoreFlags.Rank | ScoreFlags.AllowHide));
        R(new ScoreField("LMS_LIVES", "lives"));

        R(new ScoreField("NEXBALL_GOALS", "goals"));
        R(new ScoreField("NEXBALL_FAULTS", "faults", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter));

        R(new ScoreField("ONS_CAPS", "caps"));
        R(new ScoreField("ONS_TAKES", "takes", ScoreFlags.HideZero));

        R(new ScoreField("TKA_PICKUPS", "pickups", ScoreFlags.HideZero));
        R(new ScoreField("TKA_BCTIME", "bctime")); // QC field(SP_TKA_BCTIME, "bctime", SFL_SORT_PRIO_SECONDARY): sort-prio per-mode, baseline flags none
        R(new ScoreField("TKA_CARRIERKILLS", "bckills", ScoreFlags.HideZero));

        R(new ScoreField("SURV_SURVIVALS", "survivals")); // QC field(SP_SURV_SURVIVALS, "survivals", 0)
        R(new ScoreField("SURV_HUNTS", "hunts")); // QC field(SP_SURV_HUNTS, "hunts", SFL_SORT_PRIO_SECONDARY): sort-prio per-mode, baseline none

        // --- common columns ---
        R(new ScoreField("SCORE", "score", ScoreFlags.SortPrioPrimary)); // the primary fraglimit key by default
        R(new ScoreField("KILLS", "kills"));
        R(new ScoreField("DEATHS", "deaths", ScoreFlags.LowerIsBetter));
        R(new ScoreField("TEAMKILLS", "teamkills", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter));
        R(new ScoreField("SUICIDES", "suicides", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter));
        R(new ScoreField("DMG", "dmg", ScoreFlags.HideZero | ScoreFlags.AllowHide));
        R(new ScoreField("DMGTAKEN", "dmgtaken", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter | ScoreFlags.AllowHide));

        R(new ScoreField("ROUNDS_PL", "rounds"));

        R(new ScoreField("SKILL", "skill", ScoreFlags.NotSortable));
        R(new ScoreField("FPS", "fps", ScoreFlags.NotSortable));

        // --- the sentinel + client-only display fields (not stored/networked via the score columns) ---
        R(new ScoreField("END", "", ScoreFlags.None, clientOnly: true));
        R(new ScoreField("PING", "ping", ScoreFlags.LowerIsBetter | ScoreFlags.AllowHide, clientOnly: true));
        R(new ScoreField("PL", "pl", ScoreFlags.LowerIsBetter | ScoreFlags.AllowHide, clientOnly: true));
        R(new ScoreField("NAME", "name", ScoreFlags.AllowHide, clientOnly: true));
        R(new ScoreField("SEPARATOR", "", ScoreFlags.None, clientOnly: true));
        R(new ScoreField("KDRATIO", "kd", ScoreFlags.AllowHide, clientOnly: true));
        R(new ScoreField("SUM", "+/-", ScoreFlags.AllowHide, clientOnly: true));
        R(new ScoreField("FRAGS", "frags", ScoreFlags.AllowHide, clientOnly: true));

        _registered = true;
        Primary = Field("SCORE");
        Secondary = null;
    }

    /// <summary>
    /// Restore every column to its registration-time label/flags and reset the sort keys to the default (SP_SCORE
    /// primary, no secondary). The canonical registry state — what a mode's <see cref="ScoreRulesBasics"/> starts
    /// from before declaring its own columns. Keeps the existing <see cref="ScoreField"/> objects (and their
    /// stored per-player column values) intact; only labels/flags revert.
    /// </summary>
    public static void ResetToDefaults()
    {
        foreach (var f in Registry<ScoreField>.All) f.ResetToDefault();
        Primary = Field("SCORE");
        Secondary = null;
        InvalidateLayout();
    }

    /// <summary>Idempotent lazy registration so the table works in unit tests that skip GameInit.Boot.</summary>
    internal static void Ensure() { if (!_registered || FieldCount == 0) RegisterAll(); }

    /// <summary>
    /// QC <c>TIME_ENCODE(t)</c> (common/util.qh, TIME_FACTOR = 100): encode a time in seconds to the integer
    /// hundredths stored in a SFL_TIME column (e.g. SP_RACE_TIME / SP_RACE_FASTEST). <c>floor(t*100 + 0.5)</c>.
    /// </summary>
    public static int TimeEncode(float seconds) => (int)System.MathF.Floor(seconds * 100f + 0.5f);

    /// <summary>QC <c>TIME_DECODE(n)</c> (common/util.qh): the inverse — encoded hundredths back to seconds.</summary>
    public static float TimeDecode(int encoded) => encoded / 100f;

    // =====================================================================================
    //  Display formatting (QC common/util.qc ScoreString + lib/counting.qh count_ordinal +
    //  lib/string.qh clockedtime_tostring) — used by the client scoreboard's per-field text.
    // =====================================================================================

    /// <summary>
    /// QC <c>ScoreString(pFlags, pValue, rounds_played)</c> (common/util.qc:421): format a column value for the
    /// scoreboard honoring its SFL_* flags — empty for a hidden zero (HIDE_ZERO|RANK|TIME), an ordinal for
    /// SFL_RANK, mm:ss.hh for SFL_TIME, a per-round average when <paramref name="roundsPlayed"/>&gt;0, else the
    /// integer. Mirrors the QC switch exactly so a remote client renders identical strings to a listen host.
    /// </summary>
    public static string ScoreString(ScoreFlags flags, int value, int roundsPlayed = 0)
    {
        if (value == 0 && (flags & (ScoreFlags.HideZero | ScoreFlags.Rank | ScoreFlags.Time)) != 0)
            return "";
        if ((flags & ScoreFlags.Rank) != 0)
            return value < 256 ? CountOrdinal(value) : "N/A";
        if ((flags & ScoreFlags.Time) != 0)
            return TimeEncodedToString(value, compact: true);
        if (roundsPlayed != 0)
            return (value / (float)roundsPlayed).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>QC <c>count_ordinal(interval)</c> (lib/counting.qh:66): 1 → "1st", 2 → "2nd", 11 → "11th", … .</summary>
    public static string CountOrdinal(int interval)
    {
        int last2 = ((interval % 100) + 100) % 100; // QC interval % 100 (guarded against negatives)
        if (last2 < 4 || last2 > 20)
            switch (last2 % 10)
            {
                case 1: return interval + "st";
                case 2: return interval + "nd";
                case 3: return interval + "rd";
            }
        return interval + "th";
    }

    /// <summary>QC <c>TIME_ENCODED_TOSTRING(n, true)</c> = <c>clockedtime_tostring(n, true, true)</c>
    /// (lib/string.qh:145): an encoded-hundredths time → a compact mm:ss.hh / ss.hh string.</summary>
    public static string TimeEncodedToString(int tm, bool compact)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (tm < 0) return compact ? "0.00" : "0:00.00";
        const int acc = 6000; // hundredths: 60 * 100
        int minutes = tm / acc;
        int rem = tm - minutes * acc; // 0..5999 (ss.hh as an integer sshh)
        // QC builds a placeholder string "acc*10 + rem" then slices; we format ss + hh directly.
        int seconds = rem / 100, hundredths = rem % 100;
        if (!compact || minutes > 0)
            return $"{minutes}:{seconds:D2}.{hundredths:D2}";
        // compact, no minutes: drop the leading zero on the seconds when < 10.
        return $"{seconds.ToString(inv)}.{hundredths:D2}";
    }

    /// <summary>
    /// QC the "fastest/best time" idiom (server/race.qc SP_RACE_FASTEST + sv_ctf.qc SP_CTF_CAPTIME):
    /// <c>old = scores(p, field); if (!old || val &lt; old) PlayerScore_Add(p, field, val - old);</c> — i.e. set the
    /// column to <paramref name="encoded"/> only when it is currently unset (0) or the new (lower) value beats it.
    /// <paramref name="encoded"/> is the already-TIME_ENCODE'd value. No-op for a non-positive value.
    /// </summary>
    public static void SetBestTime(Entity p, ScoreField field, int encoded)
    {
        if (encoded <= 0) return;
        int old = Get(p, field);
        if (old == 0 || encoded < old) AddToPlayer(p, field, encoded - old);
    }

    // Well-known column accessors (resolved after RegisterAll / Ensure).
    public static ScoreField Score { get { Ensure(); return Field("SCORE")!; } }
    public static ScoreField Kills { get { Ensure(); return Field("KILLS")!; } }
    public static ScoreField Deaths { get { Ensure(); return Field("DEATHS")!; } }
    public static ScoreField Suicides { get { Ensure(); return Field("SUICIDES")!; } }
    public static ScoreField TeamKills { get { Ensure(); return Field("TEAMKILLS")!; } }
    public static ScoreField Dmg { get { Ensure(); return Field("DMG")!; } }
    public static ScoreField DmgTaken { get { Ensure(); return Field("DMGTAKEN")!; } }

    // =====================================================================================
    //  ScoreRules — the gametype sets which columns are active + the sort priority.
    // =====================================================================================

    /// <summary>QC <c>ScoreInfo_SetLabel_PlayerScore</c>: (re)label a column + set its flags, tracking the
    /// primary/secondary sort key. An empty label hides + de-networks the column.</summary>
    public static void SetLabel(ScoreField field, string label, ScoreFlags flags)
    {
        // Only bump the layout generation on an ACTUAL change. An idempotent re-write (the listen-server case,
        // where the client applies the received ScoreInfo onto the SAME static table the server already set) must
        // be a no-op, or LayoutGeneration would tick every frame → the ScoreInfo block would re-send forever
        // (the change-gate compares this stamp). Faithful to QC: assignment is silent; only ScoreInfo_Init's
        // explicit SendFlags|=1 (a real mode switch) forces a resend.
        bool changed = field.Label != label || field.Flags != flags;
        field.Label = label;
        field.Flags = flags;
        if ((flags & ScoreFlagsExtensions.SortPrioMask) == ScoreFlags.SortPrioPrimary) Primary = field;
        else if ((flags & ScoreFlagsExtensions.SortPrioMask) == ScoreFlags.SortPrioSecondary) Secondary = field;
        if (changed) InvalidateLayout();
    }

    /// <summary>
    /// QC <c>ScoreRules_basics</c>: the shared per-match setup. FAITHFUL to QC, this first CLEARS every column's
    /// label/flags (QC's opening <c>FOREACH(Scores, ScoreInfo_SetLabel_PlayerScore(it, "", 0))</c>) — so a mode
    /// switch drops the previous gametype's columns — then re-establishes the common ones: SP_SCORE (the primary
    /// sort/fraglimit key, only when <paramref name="scoreEnabled"/>), kills/deaths/suicides and (team games)
    /// teamkills. The active gametype then re-declares its own columns via <see cref="DeclareColumn"/> and pins
    /// the sort key(s) via <see cref="SetSortKeys"/>. <paramref name="teams"/> selects teamkills; <paramref
    /// name="sprees"/> activates the kills column; <paramref name="scoreEnabled"/> = QC GameRules_score_enabled
    /// (false for Race/CTS/Invasion, which rank by their own columns and have no SP_SCORE).
    /// </summary>
    public static void ScoreRulesBasics(bool teams, bool sprees = true, bool scoreEnabled = true, bool independent = false)
    {
        Ensure();
        // QC ScoreRules_basics opening: blank every player column so only the columns this mode declares survive.
        foreach (var f in Fields)
        {
            if (f.ClientOnly) continue;
            f.Label = "";
            f.Flags = ScoreFlags.None;
        }
        Primary = Secondary = null;

        if (scoreEnabled) SetLabel(Score, "score", ScoreFlags.SortPrioPrimary);
        // QC server/scores_rules.qc: SP_KILLS / SP_SUICIDES / SP_TEAMKILLS are only labelled when NOT
        // INDEPENDENT_PLAYERS — in independent-players modes (Race/CTS qualifying, Invasion) these "useless" PvP
        // columns are dropped, leaving only SP_DEATHS. SP_DEATHS is always present.
        if (sprees && !independent) SetLabel(Kills, "kills", ScoreFlags.None);
        SetLabel(Deaths, "deaths", ScoreFlags.LowerIsBetter);
        if (!independent) SetLabel(Suicides, "suicides", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter);
        if (teams && !independent) SetLabel(TeamKills, "teamkills", ScoreFlags.HideZero | ScoreFlags.LowerIsBetter);
        InvalidateLayout();
    }

    /// <summary>QC <c>ScoreRules_basics_end</c>'s SP_SCORE primary override: make <paramref name="field"/> the
    /// primary fraglimit/sort key (e.g. CTF_CAPS in CTF, RACE_LAPS in race).</summary>
    public static void SetPrimary(ScoreField field, bool lowerIsBetter = false)
        => SetLabel(field, field.Label, (field.Flags & ~ScoreFlagsExtensions.SortPrioMask) | ScoreFlags.SortPrioPrimary
            | (lowerIsBetter ? ScoreFlags.LowerIsBetter : ScoreFlags.None));

    public static void SetSecondary(ScoreField field)
        => SetLabel(field, field.Label, (field.Flags & ~ScoreFlagsExtensions.SortPrioMask) | ScoreFlags.SortPrioSecondary);

    /// <summary>
    /// QC <c>ScoreInfo_SetLabel_PlayerScore</c>'s sort-key effect, set explicitly: pin the primary (and
    /// optionally secondary) sort/fraglimit key for the active gametype (QC <c>scores_primary</c> /
    /// <c>scores_secondary</c>). The supplied fields keep their already-registered flags (HideZero/Time/Rank/…)
    /// — only the SFL_SORT_PRIO_* bits are (re)assigned — so e.g. RACE_FASTEST stays a lower-is-better TIME
    /// column while also becoming primary. A null <paramref name="secondary"/> clears the secondary key.
    /// This is the per-mode hook the task wires: a mode calls it once at match start so the scoreboard sorts
    /// caps/laps/time first instead of SP_SCORE.
    /// </summary>
    public static void SetSortKeys(ScoreField primary, ScoreField? secondary = null)
    {
        Ensure();
        // Clear any stale primary/secondary marks left on other columns from a prior gametype.
        foreach (var f in Fields)
            if ((f.Flags & ScoreFlagsExtensions.SortPrioMask) != 0 && !ReferenceEquals(f, primary) && !ReferenceEquals(f, secondary))
                f.Flags &= ~ScoreFlagsExtensions.SortPrioMask;

        primary.Flags = (primary.Flags & ~ScoreFlagsExtensions.SortPrioMask) | ScoreFlags.SortPrioPrimary;
        Primary = primary;
        if (secondary is not null && !ReferenceEquals(secondary, primary))
        {
            secondary.Flags = (secondary.Flags & ~ScoreFlagsExtensions.SortPrioMask) | ScoreFlags.SortPrioSecondary;
            Secondary = secondary;
        }
        else
        {
            Secondary = null;
        }
        InvalidateLayout();
    }

    /// <summary>
    /// QC the <c>GameRules_scoring(...){ field(SP_X, label, flags); }</c> body, applied per column — i.e. QC
    /// <c>ScoreInfo_SetLabel_PlayerScore(SP_X, label, flags)</c>: (re)label + (re)flag a gametype column so it
    /// shows on the scoreboard and is networked. Resolves the field by its SP_* name and ASSIGNS <paramref
    /// name="flags"/> absolutely (faithful to QC, which sets <c>scores_flags(i) = scoreflags</c>) — the caller
    /// passes the per-mode flags, then <see cref="SetSortKeys"/> stamps the sort-prio bits. No-op for an unknown
    /// name. The label defaults to the registered one (matches QC where each SP_* field has a fixed label).
    /// </summary>
    public static void DeclareColumn(string name, ScoreFlags flags = ScoreFlags.None, string? label = null)
    {
        Ensure();
        ScoreField? f = Field(name);
        if (f is null) return;
        SetLabel(f, label ?? f.Label, flags);
    }

    // =====================================================================================
    //  Per-player columns (QC PlayerScore_Add / _Set / scores(field))
    // =====================================================================================

    private static int[] Columns(Entity e)
    {
        Ensure();
        if (e.ScoreColumns is null || e.ScoreColumns.Length < FieldCount)
        {
            var bigger = new int[FieldCount];
            if (e.ScoreColumns is not null) System.Array.Copy(e.ScoreColumns, bigger, e.ScoreColumns.Length);
            e.ScoreColumns = bigger;
        }
        return e.ScoreColumns;
    }

    /// <summary>QC <c>scores(player, field)</c>: read a column (0 if never set).</summary>
    public static int Get(Entity p, ScoreField field) => Columns(p)[field.RegistryId];

    /// <summary>
    /// QC <c>PlayerScore_Add(player, field, delta)</c>: add to a column (running the AddPlayerScore mutator
    /// hook first), mark it dirty for networking, and return the new value. Dropped when the game is stopped
    /// UNLESS a hook explicitly claimed the write (QC <c>!mutator_returnvalue &amp;&amp; game_stopped</c>).
    /// </summary>
    public static int AddToPlayer(Entity p, ScoreField field, int delta)
    {
        // QC: forced == mutator_returnvalue — true ONLY when a handler explicitly claimed the write. A handler
        // that merely rewrites the delta (Survival's anonymizer) does NOT claim it, so game_stopped still clamps.
        bool forced = false;
        if (AddPlayerScoreHook is not null)
        {
            var (allow, newDelta, claimed) = AddPlayerScoreHook(p, field, delta);
            if (!allow) return Get(p, field);
            forced = claimed;
            delta = newDelta;
        }
        if (GameStopped && !forced) delta = 0;
        if (delta == 0) return Get(p, field);

        int[] cols = Columns(p);
        cols[field.RegistryId] += delta;
        if (field.Label.Length != 0) { p.ScoreDirty |= 1u << (field.RegistryId & 31); Bump(); }
        // QC MUTATOR_CALLHOOK(AddedPlayerScore, scorefield, score, player) (scores.qc:377): post-write event,
        // fired after the column was actually changed (delta != 0). dynamic_handicap recomputes here.
        AddedPlayerScoreHook?.Invoke(p, field, delta);
        return cols[field.RegistryId];
    }

    /// <summary>QC <c>PlayerScore_Set(player, field, value)</c>: set a column absolutely, marking it dirty.</summary>
    public static int SetPlayer(Entity p, ScoreField field, int value)
    {
        int[] cols = Columns(p);
        if (cols[field.RegistryId] != value)
        {
            cols[field.RegistryId] = value;
            if (field.Label.Length != 0) { p.ScoreDirty |= 1u << (field.RegistryId & 31); Bump(); }
        }
        return value;
    }

    // =====================================================================================
    //  Team scores — the QC two-slot model (TeamScore_AddToTeam / ScoreInfo_SetLabel_TeamScore /
    //  TeamScore_Compare, server/scores.qc + common/scores.qh). Slot 0 = ST_SCORE, slot 1 = the per-gametype
    //  slot. GameScores is the source of truth: every team mode routes its standings through here.
    // =====================================================================================

    /// <summary>
    /// QC <c>ScoreInfo_SetLabel_TeamScore(i, label, flags)</c> (server/scores.qc): (re)label + (re)flag one of
    /// the two team-score slots, tracking which slot is the SORT_PRIO_PRIMARY (teamscores_primary). An empty
    /// label hides + de-networks the slot. The active gametype calls this for its ST_* slot at match start.
    /// </summary>
    public static void SetTeamLabel(int slot, string label, ScoreFlags flags)
    {
        if (slot < 0 || slot >= MaxTeamScore) return;
        bool changed = _teamLabel[slot] != label || _teamFlags[slot] != flags;
        _teamLabel[slot] = label;
        _teamFlags[slot] = flags;
        // QC: only PRIMARY is tracked for teamscores (there are only 2 slots, so the "other" is the secondary).
        if ((flags & ScoreFlagsExtensions.SortPrioMask) == ScoreFlags.SortPrioPrimary)
        {
            _teamPrimarySlot = slot;
            _teamFlagsPrimary = flags;
        }
        // A team label/flag change is part of the networked layout (ScoreInfo gates on LayoutGeneration). Bump
        // only on a real change so an idempotent listen-server re-apply doesn't force an endless ScoreInfo resend.
        if (changed) InvalidateLayout();
    }

    public static string TeamLabel(int slot) => (uint)slot < MaxTeamScore ? _teamLabel[slot] : "";
    public static ScoreFlags TeamFlags(int slot) => (uint)slot < MaxTeamScore ? _teamFlags[slot] : ScoreFlags.None;
    /// <summary>QC teamscores_primary: the slot index (0 or 1) flagged SORT_PRIO_PRIMARY.</summary>
    public static int TeamPrimarySlot => _teamPrimarySlot;
    /// <summary>QC teamscores_flags_primary: the primary team slot's flags (drives the win-condition's
    /// lower-is-better / zero-is-worst — WinningConditionHelper).</summary>
    public static ScoreFlags TeamFlagsPrimary => _teamFlagsPrimary;

    /// <summary>QC the basics: blank both team slots, then make ST_SCORE primary when <paramref name="scoreEnabled"/>.
    /// Mirrors ScoreRules_basics' team-side setup (the FOREACH blanking + the conditional ST_SCORE label). A team
    /// mode then declares its ST_* slot via <see cref="SetTeamLabel"/>.</summary>
    public static void TeamRulesBasics(ScoreFlags scorePrio = ScoreFlags.SortPrioPrimary, bool scoreEnabled = true)
    {
        for (int i = 0; i < MaxTeamScore; i++) { _teamLabel[i] = ""; _teamFlags[i] = ScoreFlags.None; }
        _teamPrimarySlot = TeamSlotScore;
        _teamFlagsPrimary = ScoreFlags.None;
        if (scoreEnabled) SetTeamLabel(TeamSlotScore, "score", scorePrio);
    }

    /// <summary>QC <c>TeamScore_AddToTeam(team, scorefield, delta)</c>: add to a team's running total at
    /// <paramref name="slot"/> (no-op for the neutral team / when the game is stopped). Returns the new total.</summary>
    public static int AddToTeam(int team, int slot, int delta)
    {
        if (team == Teams.None || (uint)slot >= MaxTeamScore) return TeamScore(team, slot);
        if (GameStopped) delta = 0;
        var d = _teamScores[slot];
        int v = (d.TryGetValue(team, out int cur) ? cur : 0) + delta;
        d[team] = v;
        // QC: SendFlags |= BIT(scorefield) only when score != 0 AND the slot has a label (drives networking).
        if (delta != 0)
        {
            if (_teamLabel[slot].Length != 0) _teamDirty.Add(team);
            Bump(); // version stamp (port-only cheap "scoreboard changed?" gate) bumps on any total change
        }
        return v;
    }

    /// <summary>QC <c>TeamScore_AddToTeam(team, ST_SCORE, delta)</c>: the ST_SCORE-slot convenience overload.</summary>
    public static int AddToTeam(int team, int delta) => AddToTeam(team, TeamSlotScore, delta);

    /// <summary>QC <c>TeamScore_Add(player, ST_SCORE, ...)</c>: add to the player's team's ST_SCORE total.</summary>
    public static int AddToTeamOf(Entity p, int delta) => AddToTeam((int)p.Team, TeamSlotScore, delta);

    public static int TeamScore(int team, int slot)
        => (uint)slot < MaxTeamScore && _teamScores[slot].TryGetValue(team, out int v) ? v : 0;
    /// <summary>QC <c>teamscores(ST_SCORE)</c>: the slot-0 (ST_SCORE) total for a team.</summary>
    public static int TeamScore(int team) => TeamScore(team, TeamSlotScore);

    public static int SetTeamScore(int team, int slot, int value)
    {
        if (team == Teams.None || (uint)slot >= MaxTeamScore) return 0;
        var d = _teamScores[slot];
        if (!d.TryGetValue(team, out int cur) || cur != value) { _teamDirty.Add(team); Bump(); }
        d[team] = value;
        return value;
    }
    public static int SetTeamScore(int team, int value) => SetTeamScore(team, TeamSlotScore, value);

    /// <summary>Ensure each active team has a zeroed slot in BOTH score slots so leader scans are stable from frame 0.</summary>
    public static void SeedTeams(int teamCount)
    {
        foreach (int t in Teams.Active(teamCount))
            SeedTeam(t);
    }

    /// <summary>Ensure a single team colour has a zeroed slot in BOTH score slots (used when the team set is derived
    /// from map entities, e.g. Nexball's per-goal team seeding, rather than a fixed count).</summary>
    public static void SeedTeam(int team)
    {
        if (team == Teams.None) return;
        for (int s = 0; s < MaxTeamScore; s++)
            if (!_teamScores[s].ContainsKey(team)) _teamScores[s][team] = 0;
    }

    /// <summary>The teams that currently have a score slot (keys of the ST_SCORE dict; both slots share the team set).</summary>
    public static IReadOnlyDictionary<int, int> TeamScores => _teamScores[TeamSlotScore];

    /// <summary>
    /// QC <c>ScoreField_Compare</c> applied to a team slot: compare two teams' values at <paramref name="slot"/>;
    /// positive => <paramref name="a"/> ranks ahead. Honors that slot's SFL_LOWER_IS_BETTER / SFL_ZERO_IS_WORST.
    /// </summary>
    public static int CompareTeamSlot(int a, int b, int slot)
        => CompareValues(TeamScore(a, slot), TeamScore(b, slot), TeamFlags(slot));

    /// <summary>
    /// QC <c>TeamScore_Compare(t1, t2, strict)</c> (server/scores.qc): compare two teams by the primary team
    /// slot, then (when <paramref name="strict"/>) the other slot, then the team id. Positive => team
    /// <paramref name="a"/> ranks ahead of <paramref name="b"/>. <paramref name="a"/>/<paramref name="b"/> are
    /// team color codes; <see cref="Teams.None"/> sorts as absent (QC the <c>(!t2) - !t1</c> guard).
    /// </summary>
    public static int CompareTeams(int a, int b, bool strict)
    {
        bool aMissing = a == Teams.None, bMissing = b == Teams.None;
        if (aMissing || bMissing) return (bMissing ? 1 : 0) - (aMissing ? 1 : 0);

        // QC: i = the primary slot; compare it first, then (strict) the other slot, then the team id.
        int i = _teamPrimarySlot;
        int result = CompareTeamSlot(a, b, i);
        if (result == 0 && strict)
        {
            i = (i + 1) % MaxTeamScore;
            result = CompareTeamSlot(a, b, i);
            if (result == 0) result = a - b;
        }
        return result;
    }

    // =====================================================================================
    //  Clearing (QC PlayerScore_Clear / Score_ClearAll) + the .frags-reset-on-(re)spawn rule
    // =====================================================================================

    /// <summary>
    /// QC <c>PlayerScore_Clear(player)</c>: zero every column except SP_SKILL (the player's persistent skill),
    /// marking each cleared column dirty. Used on (re)join when <c>g_score_resetonjoin</c> is set — the running
    /// score is wiped but skill is preserved.
    /// </summary>
    public static void ClearPlayer(Entity p)
    {
        Ensure();
        int[] cols = Columns(p);
        ScoreField? skill = Field("SKILL");
        bool any = false;
        for (int i = 0; i < cols.Length; i++)
        {
            if (skill is not null && i == skill.RegistryId) continue;
            if (cols[i] != 0 && Fields[i].Label.Length != 0) { p.ScoreDirty |= 1u << (i & 31); any = true; }
            cols[i] = 0;
        }
        if (any) Bump();
    }

    /// <summary>QC <c>autocvar_g_score_resetonjoin</c> (server/scores.qc:28): 0 = keep score on (re)join (the
    /// default), 1 = always wipe, -1 = wipe unless the <c>PreferPlayerScore_Clear</c> mutator hook vetoes.</summary>
    private const string CvarScoreResetOnJoin = "g_score_resetonjoin";

    /// <summary>
    /// QC <c>PlayerScore_Clear(player)</c> (server/scores.qc:286): the (re)join-gated clear. Returns false (a
    /// no-op) when <c>g_score_resetonjoin</c> is 0, or when it is -1 and the <c>PreferPlayerScore_Clear</c> hook
    /// declines the wipe; otherwise it runs the SKILL-preserving <see cref="ClearPlayer"/> and returns true. This
    /// is the missing live arm of the resetonjoin feature: a player who specs out and rejoins keeps their score at
    /// the default 0, but an admin's 1/-1 now takes effect. The server's (re)join path calls this (the
    /// <paramref name="preferClear"/> delegate supplies the QC <c>MUTATOR_CALLHOOK(PreferPlayerScore_Clear)</c>
    /// result; null = the hook is unhandled, i.e. QC's default false).
    /// </summary>
    public static bool ClearPlayerOnJoin(Entity p, System.Func<Entity, bool>? preferClear = null)
    {
        int mode = Api.Services is null ? 0 : (int)Api.Cvars.GetFloat(CvarScoreResetOnJoin);
        // QC server/scores.qc:289 PlayerScore_Clear: the -1 branch is vetoed by
        // MUTATOR_CALLHOOK(PreferPlayerScore_Clear) — a GLOBAL dispatch, not a per-caller delegate. Consult the
        // global hook chain (the gametype that wants to keep score, e.g. TKA, returns true), keeping the optional
        // preferClear delegate as an extra override so either path can veto the wipe.
        bool veto = (preferClear is not null && preferClear(p))
            || XonoticGodot.Common.Gameplay.MutatorHooks.FirePreferPlayerScore_Clear(p);
        if (mode == 0 || (mode == -1 && !veto))
            return false;
        ClearPlayer(p);
        return true;
    }

    /// <summary>QC <c>Score_ClearAll</c>: zero every supplied player + both team slots (e.g. on map reset).</summary>
    public static void ClearAll(IEnumerable<Entity> players)
    {
        foreach (var p in players) ClearPlayer(p);
        for (int s = 0; s < MaxTeamScore; s++)
        {
            var d = _teamScores[s];
            foreach (int t in new List<int>(d.Keys))
            {
                if (d[t] != 0 && _teamLabel[s].Length != 0) _teamDirty.Add(t);
                d[t] = 0;
            }
        }
    }

    /// <summary>Full reset (registry stays; both team slots + labels/flags cleared) — for a fresh match/test.</summary>
    public static void ResetTeams()
    {
        for (int s = 0; s < MaxTeamScore; s++) { _teamScores[s].Clear(); _teamLabel[s] = ""; _teamFlags[s] = ScoreFlags.None; }
        _teamPrimarySlot = TeamSlotScore;
        _teamFlagsPrimary = ScoreFlags.None;
        _teamDirty.Clear();
    }

    // =====================================================================================
    //  Winning condition + player sort (QC PlayerScore_Sort / WinningConditionHelper)
    // =====================================================================================

    /// <summary>QC <c>ScoreField_Compare</c>: compare two values for one field's flags; positive => a ranks
    /// ahead of b. Handles lower-is-better and zero-is-worst (time fields).</summary>
    public static int CompareValues(int a, int b, ScoreFlags flags)
    {
        bool lower = flags.LowerIsBetter();
        if (flags.ZeroIsWorst())
        {
            // 0 is the worst possible value regardless of direction (a never-set time).
            if (a == 0 && b == 0) return 0;
            if (a == 0) return -1;
            if (b == 0) return 1;
        }
        if (a == b) return 0;
        bool aWins = lower ? a < b : a > b;
        return aWins ? 1 : -1;
    }

    /// <summary>QC <c>PlayerScore_Compare(t1, t2)</c>: primary, then secondary, then registration-order columns,
    /// then 0. Returns positive when <paramref name="a"/> ranks ahead of <paramref name="b"/>.</summary>
    public static int ComparePlayers(Entity a, Entity b)
    {
        Ensure();
        if (Primary is not null)
        {
            int r = CompareValues(Get(a, Primary), Get(b, Primary), Primary.Flags);
            if (r != 0) return r;
        }
        if (Secondary is not null)
        {
            int r = CompareValues(Get(a, Secondary), Get(b, Secondary), Secondary.Flags);
            if (r != 0) return r;
        }
        foreach (var f in Fields)
        {
            if (f.ClientOnly || f.Label.Length == 0) continue;
            if ((f.Flags & ScoreFlagsExtensions.SortPrioMask) != 0) continue; // already compared
            if ((f.Flags & ScoreFlags.NotSortable) != 0) continue;
            if (ReferenceEquals(f, Primary) || ReferenceEquals(f, Secondary)) continue;
            int r = CompareValues(Get(a, f), Get(b, f), f.Flags);
            if (r != 0) return r;
        }
        return 0;
    }

    /// <summary>QC FRAGS_SPECTATOR (common/constants.qh): the <c>.frags</c>-STATUS sentinel marking a spectator
    /// (mirrors <c>Player.FragsSpectator</c>, kept here to avoid a Player dependency in the score core).</summary>
    private const int FragsSpectatorSentinel = -666;

    /// <summary>QC <c>PlayerScore_Sort</c>'s <c>nospectators</c> guard: a row whose engine <c>.frags</c> STATUS is
    /// FRAGS_SPECTATOR must not rank / be returned as leader (a stale spectator score row would otherwise sort).</summary>
    public static bool IsSpectator(Entity e) => (int)e.Frags == FragsSpectatorSentinel;

    /// <summary>Players sorted best-first by <see cref="ComparePlayers"/> (QC PlayerScore_Sort). Spectators
    /// (FRAGS_SPECTATOR) are excluded (QC <c>nospectators</c>). Allocates.</summary>
    public static List<Entity> SortPlayers(IEnumerable<Entity> players)
    {
        var list = new List<Entity>();
        foreach (var p in players) if (!IsSpectator(p)) list.Add(p);
        list.Sort((a, b) => ComparePlayers(b, a)); // ComparePlayers>0 => first arg ahead; we want best first
        return list;
    }

    /// <summary>
    /// QC <c>Scoreboard_InitScores</c> (client/hud/panel/scoreboard.qc:514): (re)derive
    /// <see cref="Primary"/>/<see cref="Secondary"/> from the current per-column SFL_SORT_PRIO_* flags, scanning
    /// only sortable columns (skips SFL_NOT_SORTABLE). Falls back to SP_SCORE primary when no column is flagged.
    /// The client calls this after applying a received ScoreInfo so the sort keys match the server's flags even
    /// though the client never ran the gametype's ScoreRules (which is what stamps the keys server-side).
    /// </summary>
    public static void ResolveSortKeys()
    {
        Ensure();
        ScoreField? primary = null, secondary = null;
        foreach (var f in Fields)
        {
            if ((f.Flags & ScoreFlags.NotSortable) != 0) continue;
            ScoreFlags prio = f.Flags & ScoreFlagsExtensions.SortPrioMask;
            if (prio == ScoreFlags.SortPrioPrimary) primary = f;
            else if (prio == ScoreFlags.SortPrioSecondary) secondary = f;
        }
        Primary = primary ?? Field("SCORE");
        // QC: if (ps_secondary == NULL) ps_secondary = ps_primary; — but we keep null internally and let
        // ComparePlayers treat a null secondary as "no extra key" (it then falls through to the registry order,
        // which already includes the primary's value via the first compare). The panel resolves the display
        // secondary == primary itself (Scoreboard_SetFields' have_secondary logic).
        Secondary = (secondary is not null && !ReferenceEquals(secondary, Primary)) ? secondary : null;
    }

    /// <summary>The leader by the primary key (QC the top of PlayerScore_Sort), or null. Spectators are skipped.</summary>
    public static Entity? Leader(IEnumerable<Entity> players)
    {
        Entity? best = null;
        foreach (var p in players)
        {
            if (IsSpectator(p)) continue; // QC nospectators: a spectator never ranks as leader
            if (best is null || ComparePlayers(p, best) > 0) best = p;
        }
        return best;
    }

    /// <summary>The primary-key value used for the fraglimit check (QC WinningConditionHelper_topscore).</summary>
    public static int PrimaryScore(Entity p) { Ensure(); return Primary is null ? 0 : Get(p, Primary); }

    /// <summary>
    /// The leading team by the flag-aware two-slot comparison (QC WinningConditionHelper's
    /// <c>TeamScore_Compare(..., strict=true)</c> winner loop), or <see cref="Teams.None"/>. Honors the primary
    /// team slot's lower-is-better / zero-is-worst, then the other slot, then the team id — so e.g. CTF ranks by
    /// ST_CTF_CAPS (slot 1) while TDM ranks by ST_SCORE (slot 0).
    /// </summary>
    public static int LeaderTeam()
    {
        int bestTeam = Teams.None;
        foreach (int t in _teamScores[TeamSlotScore].Keys)
            if (CompareTeams(t, bestTeam, strict: true) > 0) bestTeam = t;
        return bestTeam;
    }

    // =====================================================================================
    //  Remaining-frags announcer (QC WinningCondition_Scores, server/world.qc:1559-1622): the
    //  "N frags left" / "leadlimit approaching" voice cue. The sibling REMAINING_MIN_{1,5} minutes
    //  announcer is live in AnnouncerController; this is the frags branch, which had no port caller.
    // =====================================================================================

    /// <summary>QC <c>fragsleft_last</c> (server/world.qc:1559): the last announced remaining-frags value, so the
    /// same cue isn't re-announced every frame. <see cref="float.MaxValue"/> = the QC <c>FLOAT_MAX</c> "no limit".</summary>
    private static float _fragsLeftLast = float.MaxValue;

    /// <summary>QC <c>autocvar_leadlimit_and_fraglimit</c>: when set, a finish needs BOTH the frag and lead limits
    /// (so the announcer takes the MAX remaining), else EITHER (the MIN). Mirrors world.qc:1605-1609.</summary>
    private const string CvarLeadAndFrag = "leadlimit_and_fraglimit";

    /// <summary>Reset the remaining-frags announce latch (QC <c>fragsleft_last</c>) at match/round start so the
    /// cue can fire again next match. Call alongside the match reset that clears scores.</summary>
    public static void ResetFragsRemaining() => _fragsLeftLast = float.MaxValue;

    /// <summary>
    /// QC <c>WinningCondition_Scores</c>'s remaining-frags announce block (server/world.qc:1592-1622): compute
    /// how many frags (or lead) are left to the limit and, when that number changes, fire the
    /// ANNCE_REMAINING_FRAG_{1,2,3} announcer ONCE (broadcast to all). <paramref name="limit"/> = fraglimit (0 =
    /// none), <paramref name="leadlimit"/> = leadlimit (0 = none), with <paramref name="topScore"/> /
    /// <paramref name="secondScore"/> the leader's and runner-up's primary-key values
    /// (WinningConditionHelper_topscore / _secondscore — already lower-is-better-negated by the caller, as QC
    /// does at world.qc:1579-1584). <paramref name="suddenDeathEnding"/> = the QC
    /// <c>checkrules_suddendeathend &amp;&amp; time &gt;= checkrules_suddendeathend</c> case (forces fragsleft 1).
    /// Gated by the caller on the QC <c>Scores_CountFragsRemaining</c> mutator hook (only the modes that announce
    /// frags call it); the per-mode hook decision stays with the caller, this just does the count + the cue.
    /// </summary>
    public static void CountFragsRemaining(float limit, float leadlimit, int topScore, int secondScore, bool suddenDeathEnding)
    {
        float fragsleft;
        if (suddenDeathEnding)
        {
            fragsleft = 1;
        }
        else
        {
            fragsleft = float.MaxValue; // QC FLOAT_MAX
            float leadingfragsleft = float.MaxValue;
            if (limit != 0) fragsleft = limit - topScore;
            if (leadlimit != 0) leadingfragsleft = secondScore + leadlimit - topScore;

            bool both = Api.Services is not null && Api.Cvars.GetFloat(CvarLeadAndFrag) != 0f;
            if (limit != 0 && leadlimit != 0 && both)
                fragsleft = System.Math.Max(fragsleft, leadingfragsleft);
            else
                fragsleft = System.Math.Min(fragsleft, leadingfragsleft);
        }

        if (_fragsLeftLast != fragsleft) // QC: do not announce the same remaining frags multiple times
        {
            if (fragsleft == 1)
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Annce, "REMAINING_FRAG_1");
            else if (fragsleft == 2)
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Annce, "REMAINING_FRAG_2");
            else if (fragsleft == 3)
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Annce, "REMAINING_FRAG_3");
            _fragsLeftLast = fragsleft;
        }
    }

    /// <summary>The runner-up team by the same flag-aware compare, or <see cref="Teams.None"/> (for lead-limit checks).</summary>
    public static int SecondTeam()
    {
        int best = LeaderTeam(), second = Teams.None;
        foreach (int t in _teamScores[TeamSlotScore].Keys)
            if (t != best && CompareTeams(t, second, strict: true) > 0) second = t;
        return second;
    }

    // =====================================================================================
    //  Networking change-set (consumed by XonoticGodot.Net's scoreboard codec)
    // =====================================================================================

    /// <summary>
    /// A monotonically-increasing version stamp — bumped whenever any player column or team total changes.
    /// The net snapshot loop compares this against a per-client "last sent" value to decide whether to resend
    /// the scoreboard block (cheap "did the scoreboard change?" gate, like the movevars hash).
    /// </summary>
    public static int Version { get; private set; }
    private static void Bump() => Version++;

    /// <summary>Force the scoreboard block to re-send even without a score change — used for ROSTER/status changes
    /// (a player joining/leaving, observer↔player, a team switch) which alter the networked row set but don't touch
    /// any score value, so they wouldn't otherwise bump <see cref="Version"/> (playtest #22: the scoreboard was
    /// stuck at the pre-join state — "you're a spectator, no bots" — until the first frag bumped the version).</summary>
    public static void MarkDirty() => Bump();

    /// <summary>
    /// The networked column subset, in registry order (QC the non-empty-label, non-client-only fields). Both
    /// server and client derive this identically from the registry so columns line up positionally on the wire.
    /// Cached; rebuilt if the registry size changes.
    /// </summary>
    public static IReadOnlyList<ScoreField> NetworkedFields
    {
        get
        {
            Ensure();
            if (_networked is null || _networkedFor != _layoutGen)
            {
                var list = new List<ScoreField>();
                foreach (var f in Fields)
                    if (!f.ClientOnly && f.Label.Length != 0) list.Add(f);
                _networked = list;
                _networkedFor = _layoutGen;
            }
            return _networked;
        }
    }
    private static List<ScoreField>? _networked;
    private static int _networkedFor = -1;

    // Bumped whenever a column label/flag changes. The networked SET is label-derived, but the registry SIZE
    // (FieldCount) never changes after RegisterAll — so the NetworkedFields cache must key on this generation,
    // not FieldCount, or a per-mode ScoreRules re-label (and a gametype switch) would leave the wire layout
    // frozen at the first-accessed mode's columns (e.g. CTF_CAPS values landing in RACE_LAPS slots).
    private static int _layoutGen;
    private static void InvalidateLayout() => _layoutGen++;

    /// <summary>
    /// A monotonically-increasing stamp bumped on every column/team label or flag change (every
    /// <see cref="SetLabel"/> / <see cref="SetTeamLabel"/> / <see cref="InvalidateLayout"/>). This is the
    /// "did the active label/flag SET change?" signal the ScoreInfo block (QC ENT_CLIENT_SCORES_INFO) gates on,
    /// exactly as <see cref="Version"/> gates the per-value scoreboard block — so the labels are re-networked
    /// only on a gametype/mode switch, not every snapshot. Distinct from <see cref="Version"/> (which bumps on
    /// every score VALUE change). Distinct from <see cref="NetworkedFields"/>'s private cache key only in that
    /// this is public for the net layer.
    /// </summary>
    public static int LayoutGeneration => _layoutGen;

    /// <summary>Capture a player's networked columns in <see cref="NetworkedFields"/> order (for serialization).</summary>
    public static int[] CaptureColumns(Entity p)
    {
        var fields = NetworkedFields;
        int[] cols = Columns(p);
        var v = new int[fields.Count];
        for (int i = 0; i < fields.Count; i++) v[i] = cols[fields[i].RegistryId];
        return v;
    }

    /// <summary>Apply received networked columns (client side) back into a player's column store.</summary>
    public static void ApplyColumns(Entity p, int[] values)
    {
        var fields = NetworkedFields;
        int[] cols = Columns(p);
        int n = System.Math.Min(values.Length, fields.Count);
        for (int i = 0; i < n; i++) cols[fields[i].RegistryId] = values[i];
    }

    /// <summary>Teams whose total changed since the last send (consumed + cleared by the serializer).</summary>
    public static IReadOnlyCollection<int> DirtyTeams => _teamDirty;
    public static void ClearTeamDirty() => _teamDirty.Clear();

    /// <summary>Force a full re-send of a player's columns (QC scorekeeper.SendFlags = 0xFFFFFF on link).</summary>
    public static void MarkAllDirty(Entity p)
    {
        Ensure();
        for (int i = 0; i < FieldCount; i++)
            if (!Fields[i].ClientOnly && Fields[i].Label.Length != 0)
                p.ScoreDirty |= 1u << (i & 31);
        Bump();
    }

    /// <summary>Reset registry + sort keys (tests). Clears registered fields.</summary>
    internal static void ClearRegistry()
    {
        Registry<ScoreField>.Clear();
        _registered = false;
        Primary = Secondary = null;
        ResetTeams();
    }
}
