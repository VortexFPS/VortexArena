using Godot;
using VortexArena.Common.Framework;   // MoveFilter
using VortexArena.Common.Gameplay;    // Entity, WeaponFiring
using VortexArena.Common.Services;    // Api, TraceResult
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The reusable crosshair forward world-trace + screen-projection primitive shared by the true-aim
/// classifier (<see cref="VortexArena.Game.Hud.CrosshairPanel"/>) and the <c>crosshair_chase</c>
/// camera variant (QC crosshair.qc:259-301).
///
/// <para>Pure client-side presentation — there is no networking here. Every trace runs against the live
/// <see cref="Api.Trace"/> collision world using the rendered camera eye/forward, and the screen
/// projection mirrors the QC <c>project_3d_to_2d</c> port in <see cref="ShowNamesLayer"/>. No networked
/// field, stat, struct or entity-feed slice is added or read.</para>
/// </summary>
public static class CrosshairTrace
{
    /// <summary>QC <c>max_shot_distance</c> — the canonical forward-trace reach (mirrors
    /// <see cref="WeaponFiring.MaxShotDistance"/>).</summary>
    public const float MaxShotDistance = WeaponFiring.MaxShotDistance;

    /// <summary>
    /// The decoded result of a forward crosshair traceline (QC <c>trace_*</c> globals). <see cref="DidHit"/>
    /// is false (and <see cref="PointQuake"/> = the segment end) when nothing was struck, or when no trace
    /// service is live yet (a pure client before its facade is up).
    /// </summary>
    public readonly record struct Hit(
        NVec3 PointQuake,
        float Fraction,
        Entity? Ent,
        NVec3 PlaneNormal,
        int SurfaceFlags,
        int Contents,
        bool DidHit);

    /// <summary>
    /// QC <c>traceline(eye, eye + forward * range)</c> — a zero-box forward line trace from the rendered eye
    /// along <paramref name="forwardQuake"/> for <paramref name="range"/> units, ignoring <paramref name="ignore"/>
    /// (typically the local player). Decodes the <see cref="TraceResult"/> into a <see cref="Hit"/>; when no trace
    /// service is available the result degrades to a clean miss whose <see cref="Hit.PointQuake"/> is the segment
    /// end (<paramref name="eyeQuake"/> + <paramref name="forwardQuake"/> * <paramref name="range"/>).
    /// </summary>
    public static Hit TraceForward(NVec3 eyeQuake, NVec3 forwardQuake, float range, MoveFilter filter, Entity? ignore)
    {
        NVec3 end = eyeQuake + forwardQuake * range;
        if (Api.Services is null)
            return new Hit(end, 1f, null, NVec3.Zero, 0, 0, false);

        TraceResult tr = Api.Trace.Trace(eyeQuake, NVec3.Zero, NVec3.Zero, end, filter, ignore);
        return new Hit(
            tr.EndPos,
            tr.Fraction,
            tr.Ent,
            tr.PlaneNormal,
            tr.DpHitQ3SurfaceFlags,
            tr.DpHitContents,
            tr.Fraction < 1f);
    }

    /// <summary>
    /// Project a Quake-space world point to screen (QC <c>project_3d_to_2d</c>; mirrors
    /// <see cref="ShowNamesLayer"/>'s <c>Project</c>). Returns false — and leaves <paramref name="screen"/> at
    /// the origin — when the point is behind the camera, so callers treat it as not visible.
    /// </summary>
    public static bool ProjectToScreen(Camera3D camera, NVec3 pointQuake, out Vector2 screen)
    {
        Vector3 g = Coords.ToGodot(pointQuake);
        if (camera.IsPositionBehind(g))
        {
            screen = Vector2.Zero;
            return false;
        }
        screen = camera.UnprojectPosition(g);
        return true;
    }

    /// <summary>
    /// The <c>crosshair_chase</c> impact point (QC crosshair.qc:259-301): from the player origin lifted by
    /// <paramref name="viewHeight"/>, trace forward along <paramref name="forwardQuake"/> against the world only
    /// (MOVE_WORLDONLY) for <see cref="MaxShotDistance"/> and return where it lands. Used to re-anchor the
    /// crosshair on the world surface the player is aiming at rather than at screen center.
    /// </summary>
    public static NVec3 ChaseImpact(NVec3 playerOriginQuake, float viewHeight, NVec3 forwardQuake)
    {
        NVec3 eye = playerOriginQuake + new NVec3(0f, 0f, viewHeight);
        return TraceForward(eye, forwardQuake, MaxShotDistance, MoveFilter.WorldOnly, null).PointQuake;
    }
}
