// Port of DarkPlaces' SV_NudgeOutOfSolid (sv_phys.c:1549), exposed to the gameplay layer as QC's
// `nudgeoutofsolid` builtin / `nudgeoutofsolid_OrFallback` (common/checkextension.qc:44). QC calls it wherever an
// entity is placed at an origin that may already be inside world geometry — the loot spawn (items.qc:1089), the
// dropped CTF flag (sv_ctf.qc:507), the Porto soft-fail drop (porto.qc) — because those bodies are wider than the
// player hull that positioned them.

using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Extricate an entity that has been placed embedded in solid, by searching nearby for the closest free
/// position for its own bbox — the shared C# successor to DP's <c>SV_NudgeOutOfSolid</c> (QC
/// <c>nudgeoutofsolid_OrFallback</c>).
/// </summary>
public static class NudgeOutOfSolid
{
    /// <summary>How far (units) we will push a body to free it. Beyond this the placement is left alone rather
    /// than teleported somewhere unrelated. Matches the player-physics nudge budget (a little over a step).</summary>
    public const float MaxNudge = 38f;

    /// <summary>
    /// If <paramref name="e"/> begins embedded in solid, move it to the nearest free position and re-link.
    /// Candidate directions are tried in priority order — straight UP first (so a dropped item ends resting on
    /// the surface it was stuck in), then the four cardinals, then down — at increasing distances, so the first
    /// free spot found is the minimal displacement. <see cref="MoveFilter.NoMonsters"/> so we push out of the
    /// world / brush models only, never off other entities.
    /// </summary>
    /// <returns>True if the entity is free (either it was never stuck, or it was successfully freed).</returns>
    public static bool Apply(Entity e)
    {
        if (Api.Services is null || e.IsFreed)
            return true;
        if (!IsStuckAt(e, e.Origin))
            return true;

        Vector3 origin = e.Origin;
        System.ReadOnlySpan<Vector3> dirs = stackalloc Vector3[]
        {
            new(0f, 0f, 1f),                                                 // up — prefer resting on top
            new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 1f, 0f), new(0f, -1f, 0f),
            new(0f, 0f, -1f),                                                // down — last resort
        };

        for (float dist = 1f; dist <= MaxNudge; dist += dist < 8f ? 1f : 4f)
        {
            for (int d = 0; d < dirs.Length; d++)
            {
                Vector3 cand = origin + dirs[d] * dist;
                if (IsStuckAt(e, cand))
                    continue;
                Api.Entities.SetOrigin(e, cand);
                e.OldOrigin = cand;
                if (Log.WillTrace)
                    Log.Trace($"[nudge] freed {e.ClassName}: {origin} -> {cand} (+{dirs[d] * dist})");
                return true;
            }
        }

        Log.Trace($"[nudge] could NOT free {e.ClassName} at {origin} (mins {e.Mins}, maxs {e.Maxs})");
        return false;
    }

    /// <summary>True when the entity's own hull is embedded in solid at <paramref name="pos"/>. MOVE_NOMONSTERS
    /// so only the world / brush models count.</summary>
    private static bool IsStuckAt(Entity e, Vector3 pos)
        => Api.Trace.Trace(pos, e.Mins, e.Maxs, pos, MoveFilter.NoMonsters, e).StartSolid;
}
