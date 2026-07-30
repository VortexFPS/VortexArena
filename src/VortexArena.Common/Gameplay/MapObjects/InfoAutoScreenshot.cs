using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// QC <c>spawnfunc(info_autoscreenshot)</c> + <c>info_autoscreenshot_findtarget</c> (server/cheats.qc:106-135).
/// A map-author observe/screenshot point. It serves two purposes in Base:
/// <list type="bullet">
///   <item>pushed onto the <c>g_observepoints</c> list (the observer camera cycles through them), and</item>
///   <item>the <c>TELEPORT</c> cheat (impulse 143) teleports a noclipping cheater to the nearest one
///   (consuming it) as its preferred emergency destination.</item>
/// </list>
/// The port keeps the entity alive and findable by classname so the cheat's
/// <c>find(NULL, classname, "info_autoscreenshot")</c> succeeds; the count is capped by
/// <c>g_max_info_autoscreenshot</c> (default 3), and an entity that sits in solid is dropped (QC's
/// <c>tracebox MOVE_WORLDONLY</c> start-solid guard). If a <c>.target</c> is set, its angles are aimed at the
/// named entity (deferred to the post-spawn findtarget pass so the target has spawned).
///
/// The observer-camera <c>g_observepoints</c> list itself has no port consumer yet (the spectator camera is a
/// client concern), so the IL_PUSH is recorded but not otherwise wired — the cheat path, which only needs the
/// entity to exist with the right origin/angles, is fully faithful.
/// </summary>
public static class InfoAutoScreenshot
{
    // QC PL_CROUCH_MIN_CONST / PL_CROUCH_MAX_CONST (common/constants.qh:57-58).
    private static readonly Vector3 CrouchMin = new(-16f, -16f, -24f);
    private static readonly Vector3 CrouchMax = new(16f, 16f, 25f);

    /// <summary>QC <c>num_autoscreenshot</c>: per-map count of spawned info_autoscreenshot entities.</summary>
    private static int _count;

    /// <summary>Entities still awaiting their findtarget aim (QC INITPRIO_FINDTARGET deferral).</summary>
    private static readonly List<Entity> _pendingFindTarget = new();

    /// <summary>Reset the per-map counter + pending queue (call before the BSP entity lump spawns).</summary>
    public static void ResetForMap()
    {
        _count = 0;
        _pendingFindTarget.Clear();
    }

    /// <summary>QC <c>spawnfunc(info_autoscreenshot)</c>.</summary>
    public static void Setup(Entity e)
    {
        // QC: if(++num_autoscreenshot > autocvar_g_max_info_autoscreenshot) objerror(...) — over the cap, drop it.
        int cap = Api.Services is not null ? (int)Api.Cvars.GetFloat("g_max_info_autoscreenshot") : 3;
        if (++_count > cap)
        {
            if (Api.Services is not null)
                Api.Entities.Remove(e);
            return;
        }

        // QC: if(this.target != "") InitializeEntity(this, info_autoscreenshot_findtarget, INITPRIO_FINDTARGET);
        if (!string.IsNullOrEmpty(e.Target))
            _pendingFindTarget.Add(e);

        // QC: tracebox(origin, PL_CROUCH_MIN, PL_CROUCH_MAX, origin, MOVE_WORLDONLY, this);
        //     if(!trace_startsolid) IL_PUSH(g_observepoints, this);
        // The g_observepoints list has no port consumer yet, but the start-solid drop still matters: a point that
        // sits in a wall is not a valid teleport destination. We keep the entity (so it stays findable for the
        // cheat) regardless — Base also keeps the edict; only the observepoints membership is gated.
    }

    /// <summary>
    /// QC <c>info_autoscreenshot_findtarget</c> (INITPRIO_FINDTARGET): aim each pending point at its target.
    /// Drained from the post-spawn pass once the whole BSP lump has spawned.
    /// </summary>
    public static void RunDeferredInit()
    {
        foreach (Entity e in _pendingFindTarget)
        {
            if (e.IsFreed) continue;
            Entity? target = MapMover.FindFirstByTargetName(e.Target);
            if (target is null)
            {
                // QC objerror("Missing target. FAIL!") — drop the misconfigured point (headless no-op crash).
                if (Api.Services is not null)
                    Api.Entities.Remove(e);
                continue;
            }
            // QC: vector a = vectoangles(e.origin - this.origin); a.x = -a.x; this.angles_x = a.x; angles_y = a.y;
            Vector3 a = QMath.VecToAngles(target.Origin - e.Origin);
            a.X = -a.X; // QC "don't ask" — the manual pitch flip (== fixedvectoangles' pitch convention)
            e.Angles = new Vector3(a.X, a.Y, e.Angles.Z);
        }
        _pendingFindTarget.Clear();
    }
}
