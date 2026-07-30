// Port of server/bot/default/havocbot/havocbot.qc havocbot_checkdanger (:401-444)
// + the bot slice of tracebox_hits_trigger_hurt (server/bot/default/navigation.qh / t_swamp-style box test).
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// The "is the ground ahead of me deadly?" probe a bot runs every think while moving — the C# port of QC
/// <c>havocbot_checkdanger</c>. Looks from the eye toward a point ahead (<c>dst_ahead</c>), and if that line
/// is clear, traces 3000qu straight down from it and classifies what the bot would land on:
///
///   0 = safe; 1 = SKY below (a void fall); 2 = a drop &gt;100qu below both the feet and the goal (a cliff);
///   3 = LAVA or SLIME at the landing point; 4 = a <c>trigger_hurt</c> volume in the fall column.
///
/// The caller (<see cref="BotBrain"/>, mirroring havocbot.qc:1136-1182) treats 1-3 as "danger ahead" (brake),
/// and 4 as danger too unless the goal is above jump height — then the goal is unreachable (clear the route).
/// </summary>
public static class BotDanger
{
    /// <summary>QC <c>dst_down = dst_ahead - '0 0 3000'</c>: how far below the look-ahead point we probe.</summary>
    private const float DownProbe = 3000f;

    /// <summary>
    /// QC <c>havocbot_checkdanger(this, dst_ahead)</c>. <paramref name="eye"/> is origin + view_ofs;
    /// <paramref name="goalZ"/> the current goal's height (QC this.goalcurrent.origin.z);
    /// <paramref name="moving"/> stands in for QC's (AI_STATUS_RUNNING|AI_STATUS_ROAMING) "I'm under way"
    /// status bits; <paramref name="committed"/> for the jumppad/hardwired-link/JUMP-waypoint skips (the bot
    /// has deliberately committed to a flight it must not brake out of).
    /// </summary>
    public static int CheckDanger(Entity bot, Vector3 eye, Vector3 dstAhead, float goalZ,
        Vector3 mins, Vector3 maxs, bool onGround, bool jumpHeld, bool moving, bool committed)
    {
        Vector3 dstDown = dstAhead - new Vector3(0f, 0f, DownProbe);

        // QC traceline(origin + view_ofs, dst_ahead, true, NULL): only when the look-ahead line is CLEAR do we
        // ask what's below it (a wall ahead means no fall to worry about).
        var ahead = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, dstAhead, MoveFilter.NoMonsters, bot);
        if (ahead.Fraction < 1f || committed)
            return 0;
        if (!(onGround || moving || jumpHeld))
            return 0;

        // Look downwards (QC traceline(dst_ahead, dst_down, true, NULL)).
        var down = Api.Trace.Trace(dstAhead, Vector3.Zero, Vector3.Zero, dstDown, MoveFilter.NoMonsters, bot);
        float feetZ = bot.Origin.Z + mins.Z;
        if (down.EndPos.Z >= feetZ)
            return 0; // floor at/above foot level: walkable

        // 1) sky surface below = a void fall (QC trace_dphitq3surfaceflags & Q3SURFACEFLAG_SKY).
        if ((down.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0)
            return 1;

        // 2) a drop more than 100qu below both the feet and the goal (QC min(feet, goalcurrent.origin.z) - 100).
        if (down.EndPos.Z < MathF.Min(feetZ, goalZ) - 100f)
            return 2;

        // 3) the landing point is in lava/slime (QC pointcontents(trace_endpos + '0 0 1')).
        int contents = Api.Trace.PointContents(down.EndPos + new Vector3(0f, 0f, 1f));
        if ((contents & Engine.Collision.SuperContents.Solid) == 0)
        {
            if ((contents & (Engine.Collision.SuperContents.Lava | Engine.Collision.SuperContents.Slime)) != 0)
                return 3;

            // 4) a trigger_hurt volume in the fall column (QC tracebox_hits_trigger_hurt(dst_ahead, mins, maxs,
            //    trace_endpos)). QC optimizes with a line test then re-runs the same test after a confirming
            //    tracebox down to dst_down (which only tightens trace_endpos); the port runs the exact swept-box
            //    slab test once against the already-resolved fall endpoint — same accept set, no second trace.
            if (HitsTriggerHurt(dstAhead, mins, maxs, down.EndPos))
                return 4;
        }
        return 0;
    }

    /// <summary>QC Q3SURFACEFLAG_SKY (matches WeaponFiring.Q3SurfaceFlagSky).</summary>
    private const int Q3SurfaceFlagSky = 0x4;

    /// <summary>
    /// QC <c>tracebox_hits_trigger_hurt(start, mins, maxs, end)</c> (common/mapobjects/trigger/hurt.qc:78): does
    /// the box <paramref name="mins"/>/<paramref name="maxs"/> swept from <paramref name="start"/> to
    /// <paramref name="end"/> overlap any <c>trigger_hurt</c> volume? QC walks the trigger_hurt linked list calling
    /// <c>tracebox_hits_box</c> (a swept-AABB vs box slab test); the port finds them by classname (trigger_hurt
    /// brushes keep their edicts + brush bounds — MapObjectsRegistry registers the spawnfunc) and runs the exact
    /// same slab math, so a sweep that only clips a trigger's bounding box corner diagonally is correctly rejected.
    /// </summary>
    public static bool HitsTriggerHurt(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end)
    {
        if (Api.Services is null)
            return false;
        foreach (Entity e in Api.Entities.FindByClass("trigger_hurt"))
        {
            if (e.IsFreed) continue;
            if (e.AbsMin == e.AbsMax) continue; // unlinked/degenerate volume
            // QC tracebox_hits_box(start, mins, maxs, end, absmin, absmax)
            //   = trace_hits_box(start, end, absmin - maxs, absmax - mins)
            // (Minkowski-expand the trigger box by our own box, then do a swept-ray-vs-box slab test).
            if (TraceHitsBox(start, end, e.AbsMin - maxs, e.AbsMax - mins))
                return true;
        }
        return false;
    }

    /// <summary>
    /// QC <c>trace_hits_box(start, end, thmi, thma)</c> (common/util.qc:2219): does the ray start→end cross the
    /// axis-aligned box [thmi, thma]? A standard slab clip — the port mirrors QC's per-axis
    /// <c>trace_hits_box_1d</c> exactly, including its degenerate-axis (end component == 0) early-out.
    /// </summary>
    private static bool TraceHitsBox(Vector3 start, Vector3 end, Vector3 thmi, Vector3 thma)
    {
        end -= start;
        thmi -= start;
        thma -= start;
        // now it is a trace from 0 to end
        float a0 = 0f, a1 = 1f;
        if (!HitsBox1D(end.X, thmi.X, thma.X, ref a0, ref a1)) return false;
        if (!HitsBox1D(end.Y, thmi.Y, thma.Y, ref a0, ref a1)) return false;
        if (!HitsBox1D(end.Z, thmi.Z, thma.Z, ref a0, ref a1)) return false;
        return true;
    }

    /// <summary>QC <c>trace_hits_box_1d</c> (common/util.qc:2197): one-axis slab clamp of the [a0,a1] interval.</summary>
    private static bool HitsBox1D(float end, float thmi, float thma, ref float a0, ref float a1)
    {
        if (end == 0f)
        {
            // just check if 0 is in range
            if (0f < thmi) return false;
            if (0f > thma) return false;
        }
        else
        {
            // 0 -> end has to stay in thmi -> thma
            a0 = MathF.Max(a0, MathF.Min(thmi / end, thma / end));
            a1 = MathF.Min(a1, MathF.Max(thmi / end, thma / end));
            if (a0 > a1) return false;
        }
        return true;
    }
}
