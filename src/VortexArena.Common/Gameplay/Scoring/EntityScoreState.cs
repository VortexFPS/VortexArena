using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// The per-entity scorekeeper state — the Godot-free essence of the QuakeC <c>scorekeeper</c> edict's
/// <c>.(scores(i))</c> slots (server/scores.qc). Promoted onto the partial <see cref="Entity"/> (ADR-0007,
/// same pattern as <see cref="EntityGametypeState"/>) so a player carries its networked score columns without
/// a side table. The columns are indexed by <see cref="ScoreField.RegistryId"/>; the running match SCORE now
/// lives here (column SP_SCORE) rather than overloading the engine <see cref="Frags"/> field — which reverts
/// to its QC meaning as the player STATUS sentinel (FRAGS_PLAYER / FRAGS_SPECTATOR / …), reset on each spawn.
/// </summary>
public partial class Entity
{
    /// <summary>
    /// The dense score columns (QC <c>scorekeeper._scores[MAX_SCORE]</c>), indexed by
    /// <see cref="ScoreField.RegistryId"/>. Lazily allocated on first write by <see cref="Scoring.GameScores"/>.
    /// </summary>
    public int[]? ScoreColumns;

    /// <summary>
    /// The networking change-mask (QC <c>scorekeeper.SendFlags |= BIT(field.m_id % 16)</c>): a bit per changed
    /// column, consumed and cleared by the scoreboard net serializer. 32-bit here (the field count fits).
    /// </summary>
    public uint ScoreDirty;

    /// <summary>Whether a scorekeeper is attached (QC PlayerScore_Attach ran). Cleared on detach/disconnect.</summary>
    public bool ScoreAttached;
}
