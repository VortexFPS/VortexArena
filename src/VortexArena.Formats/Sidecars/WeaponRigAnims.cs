namespace XonoticGodot.Formats.Sidecars;

/// <summary>
/// Base's weapon hand-rig animation slot convention — <c>CL_WeaponEntity_SetModel</c> (all.qc:373-376) maps
/// the <c>h_*</c> view-model rig's frame groups by FIXED INDEX, never by name:
/// <code>
///   anim_fire1  = animfixfps(this, '0 1 0.01', ...);   // group 0
///   anim_fire2  = animfixfps(this, '1 1 0.01', ...);   // group 1
///   anim_idle   = animfixfps(this, '2 1 0.01', ...);   // group 2
///   anim_reload = animfixfps(this, '3 1 0.01', ...);   // group 3
/// </code>
/// The shipped <c>h_*.iqm.framegroups</c> sidecars carry those ranges NAMELESSLY (the "// fire" trailers are
/// comments, stripped by <see cref="FrameGroups.Parse"/>), so a name-driven clip player would otherwise see
/// synthesized names (the player-model canonical list starts "idle, run, …" — which put the FIRE group under
/// the name "idle"). This helper stamps the slot names onto nameless groups so the host's
/// idle/fire/reload clip lookups address the ranges Base plays by index. The names match the rigs' own
/// authored IQM anim names (h_shotgun.iqm internally names them fire/fire2/idle/reload) — the sidecar only
/// exists to fix their fps/loop flags.
/// </summary>
public static class WeaponRigAnims
{
    /// <summary>The slot-indexed clip names (Base all.qc:373-376 + the rigs' authored IQM anim names).</summary>
    private static readonly string[] SlotNames = { "fire", "fire2", "idle", "reload" };

    /// <summary>
    /// Whether a normalized model vpath is a first-person weapon HAND RIG (<c>models/weapons/h_*</c>) —
    /// the only models the slot convention applies to.
    /// </summary>
    public static bool IsHandRigPath(string modelKey)
        => modelKey.StartsWith("models/weapons/h_", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stamp the slot names onto NAMELESS groups of a weapon hand rig (index 0-3 → fire/fire2/idle/reload;
    /// extra groups and groups with an authored 5th-token name are left untouched). Returns the input list
    /// unchanged (same instance) when it is not a hand-rig path, is null/empty, or nothing needed renaming.
    /// </summary>
    public static List<FrameGroup>? NameGroups(string modelKey, List<FrameGroup>? groups)
    {
        if (groups is null || groups.Count == 0 || !IsHandRigPath(modelKey))
            return groups;
        for (int i = 0; i < groups.Count && i < SlotNames.Length; i++)
        {
            FrameGroup g = groups[i];
            if (!string.IsNullOrEmpty(g.Name))
                continue;
            groups[i] = new FrameGroup(g.FirstFrame, g.FrameCount, g.Fps, g.Loop, SlotNames[i]);
        }
        return groups;
    }
}
