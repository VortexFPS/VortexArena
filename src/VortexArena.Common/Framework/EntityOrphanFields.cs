namespace XonoticGodot.Common.Framework;

/// <summary>
/// Shared entity fields that two concurrently-written port files each assumed the other would declare.
/// Consolidated here once to keep the partial <see cref="Entity"/> consistent.
/// </summary>
public partial class Entity
{
    /// <summary>QC <c>.respawntimejitter</c> — random spread added to item/breakable respawn timing.</summary>
    public float RespawnTimeJitter;
}
