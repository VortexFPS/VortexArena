using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay.Scoring;

/// <summary>
/// The per-field sort/display flags — the C# successor to the QuakeC <c>SFL_*</c> bitset
/// (common/scores.qh). Drives scoreboard column behavior (hide-zero, rank/time formatting) and the
/// winning-condition comparison (lower-is-better, primary/secondary sort priority). Values match the QC
/// <c>BIT(n)</c> constants so a server↔client mapping stays bit-faithful.
/// </summary>
[System.Flags]
public enum ScoreFlags
{
    None = 0,
    /// <summary>SFL_LOWER_IS_BETTER — BIT(0): a smaller value ranks higher (deaths/suicides/dmgtaken).</summary>
    LowerIsBetter = 1 << 0,
    /// <summary>SFL_HIDE_ZERO — BIT(1): don't show a 0 value in the column.</summary>
    HideZero = 1 << 1,
    /// <summary>SFL_SORT_PRIO_SECONDARY — BIT(2): the secondary sort key (below value must be &lt; primary's).</summary>
    SortPrioSecondary = 1 << 2,
    /// <summary>SFL_SORT_PRIO_PRIMARY — BIT(3): the primary sort key (also the fraglimit key).</summary>
    SortPrioPrimary = 1 << 3,
    /// <summary>SFL_ALLOW_HIDE — BIT(4): the column may be hidden even if it is a sort key.</summary>
    AllowHide = 1 << 4,
    /// <summary>SFL_RANK — BIT(5): display as a rank (1st/2nd/3rd/…).</summary>
    Rank = 1 << 5,
    /// <summary>SFL_TIME — BIT(6): display as mm:ss.s; stored as tenths; 0 is the WORST possible value.</summary>
    Time = 1 << 6,
    /// <summary>SFL_NOT_SORTABLE — BIT(7): never sort by this field (SKILL/FPS).</summary>
    NotSortable = 1 << 7,
}

/// <summary>
/// Convenience: the priority mask + the QC <c>SFL_ZERO_IS_WORST</c> alias (== SFL_TIME).
/// </summary>
public static class ScoreFlagsExtensions
{
    /// <summary>QC SFL_SORT_PRIO_MASK = PRIMARY | SECONDARY.</summary>
    public const ScoreFlags SortPrioMask = ScoreFlags.SortPrioPrimary | ScoreFlags.SortPrioSecondary;

    /// <summary>QC SFL_ZERO_IS_WORST (aliased to SFL_TIME): a 0 sorts as the worst value.</summary>
    public static bool ZeroIsWorst(this ScoreFlags f) => (f & ScoreFlags.Time) != 0;
    public static bool LowerIsBetter(this ScoreFlags f) => (f & ScoreFlags.LowerIsBetter) != 0;
}

/// <summary>
/// One networked score column — the C# successor to a QuakeC <c>PlayerScoreField</c> (REGISTER_SP, an SP_*
/// id with <c>m_id</c>, a <c>m_name</c> label and <c>m_flags</c>). Registered into the <see cref="GameScores"/>
/// catalog in declaration order (NOT alphabetically sorted: QC uses registration order as the player-sort
/// tiebreak priority after primary/secondary). A field with an empty <see cref="Label"/> is not shown on the
/// scoreboard and not networked (QC <c>scores_label(i) == ""</c>).
/// </summary>
public sealed class ScoreField : IRegistered
{
    public int RegistryId { get; set; }

    /// <summary>The SP_* stable id, e.g. "SCORE" / "CTF_CAPS" — the registry key + content-hash source.</summary>
    public string Name = "";
    public string RegistryName => Name;

    /// <summary>QC <c>scores_label</c>: the scoreboard column header; "" = hidden + not networked.</summary>
    public string Label;

    /// <summary>QC <c>scores_flags</c>: the SFL_* bitset for this column.</summary>
    public ScoreFlags Flags;

    /// <summary>True for the client-only display fields after SP_END (PING/PL/NAME/SEPARATOR/KDRATIO/SUM/FRAGS) — not stored server-side.</summary>
    public bool ClientOnly;

    /// <summary>The registration-time label/flags (the baseline the field reverts to when a fresh ScoreRules run
    /// blanks then re-declares columns). Lets <see cref="GameScores.RegisterAll"/> restore the default registry
    /// state idempotently without recreating the field objects (so existing references stay valid).</summary>
    public readonly string DefaultLabel;
    public readonly ScoreFlags DefaultFlags;

    public ScoreField(string name, string label, ScoreFlags flags = ScoreFlags.None, bool clientOnly = false)
    {
        Name = name;
        Label = label;
        Flags = flags;
        ClientOnly = clientOnly;
        DefaultLabel = label;
        DefaultFlags = flags;
    }

    /// <summary>Revert this field to its registration-time label/flags (used by the default-state reset).</summary>
    public void ResetToDefault() { Label = DefaultLabel; Flags = DefaultFlags; }

    public override string ToString() => $"SP_{Name}({Label})";
}
