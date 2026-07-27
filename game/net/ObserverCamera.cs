using System;
using XonoticGodot.Common.Diagnostics;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// Dev/CI free-camera capture (the "--observe" flag): keep the local listen-host client an OBSERVER (the
/// [#44] auto-join is skipped) and pin the rendered camera at a fixed Quake-space point/orientation, so a
/// windowed <c>--screenshot</c> can frame ANY spot on a map — an item pickup, a doorway, a lightmap seam —
/// without scripting player movement to walk there. No player spawns, so no viewmodel/body intrudes and
/// nothing perturbs the world (bots can still be requested via <c>--bots</c> to observe live combat).
///
/// <para>CLI: <c>--observe "&lt;x y z&gt; [yaw pitch]"</c> — the camera POINT in Quake map coordinates (the
/// values in a map's entity lump / <c>viewpos</c>), plus an optional yaw/pitch (deg, Quake convention:
/// positive pitch looks down). Add <c>--look-at "&lt;x y z&gt;"</c> to aim the camera at a target point
/// instead of giving angles (the usual way to frame an entity: <c>--observe</c> a nearby vantage,
/// <c>--look-at</c> the entity's origin). Values may be space- or comma-separated. Static single-shot boot
/// wiring, like <see cref="CameraTrace"/> / the SDF baker. See docs/RUNNING.md "Visual capture".</para>
/// </summary>
public static class ObserverCamera
{
    public static bool Active { get; private set; }

    /// <summary>The camera point (Quake space) — the EYE lands exactly here.</summary>
    public static NVec3 OriginQuake { get; private set; }

    /// <summary>View angles (deg): (pitch, yaw, roll), Quake convention (positive pitch = down).</summary>
    public static NVec3 AnglesQuake { get; private set; }

    /// <summary>
    /// Parse and arm from the CLI values. <paramref name="pos"/> is "x y z [yaw pitch]";
    /// <paramref name="lookAt"/> (optional "x y z") overrides yaw/pitch by aiming at that point.
    /// Returns false (inactive) on a malformed vector.
    /// </summary>
    public static bool Configure(string pos, string? lookAt)
    {
        if (!XonoticGodot.Common.Framework.VecParse.TryParseFloats(pos, min: 3, out float[] p))
        {
            Log.Severe($"[observe] bad --observe value '{pos}' — expected \"x y z [yaw pitch]\"");
            return false;
        }
        OriginQuake = new NVec3(p[0], p[1], p[2]);
        float yaw = p.Length >= 4 ? p[3] : 0f;
        float pitch = p.Length >= 5 ? p[4] : 0f;

        if (!string.IsNullOrWhiteSpace(lookAt))
        {
            if (!XonoticGodot.Common.Framework.VecParse.TryParseFloats(lookAt, min: 3, out float[] t))
            {
                Log.Severe($"[observe] bad --look-at value '{lookAt}' — expected \"x y z\"");
                return false;
            }
            NVec3 d = new NVec3(t[0], t[1], t[2]) - OriginQuake;
            float horiz = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            yaw = MathF.Atan2(d.Y, d.X) * (180f / MathF.PI);
            pitch = -MathF.Atan2(d.Z, horiz) * (180f / MathF.PI); // Quake pitch: positive looks DOWN
        }

        AnglesQuake = new NVec3(pitch, yaw, 0f);
        Active = true;
        Log.Info($"[observe] camera pinned at ({OriginQuake.X} {OriginQuake.Y} {OriginQuake.Z}) "
                 + $"pitch {AnglesQuake.X:0.#} yaw {AnglesQuake.Y:0.#}");
        return true;
    }

    /// <summary>
    /// Disarm on game-session teardown (Shell.TeardownGame): --observe is a boot-time capture for ONE session.
    /// Without this the latch would outlive the capture — a later menu-created game in the same process would
    /// silently never auto-join its host and keep the camera pinned at the OLD map's coordinates.
    /// </summary>
    public static void Disarm()
    {
        if (!Active)
            return;
        Active = false;
        Log.Info("[observe] disarmed (game session ended)");
    }

}
