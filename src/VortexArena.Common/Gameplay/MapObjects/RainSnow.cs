// Port of qcsrc/common/mapobjects/func/rainsnow.qc — SVQC half (func_rain / func_snow).
//
// An invisible brush volume inside which rain or snow falls: .velocity (moved to .dest at spawn) is the
// fall direction, .cnt the particle palette colorbase (default 12 — white), .count the density ("this
// many particles fall every second for a 1024x1024 area", default 2000, clamped 1..65535). The drawing
// (DP te_particlerain/te_particlesnow per frame) is client-side — see game/client/WeatherSystem.cs.
//
// NOTE: no shipped Xonotic stock map uses func_rain/func_snow (the recon census scanned all 31 BSPs),
// so this family is parity-complete but exercised only by tests/custom maps.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_rain</c> / <c>func_snow</c> weather volumes. Registered by <see cref="MapObjectsRegistry"/>.</summary>
public static class RainSnow
{
    // ---- QC rainsnow.qh — note SNOW is 0 and RAIN is 1 ----
    public const int StateSnow = 0;  // RAINSNOW_SNOW
    public const int StateRain = 1;  // RAINSNOW_RAIN

    /// <summary>The shipped client draw-distance default (cl_rainsnow_maxdrawdist, xonotic-client.cfg).</summary>
    public const float DefaultMaxDrawDist = 1000f;

    /// <summary><c>spawnfunc(func_rain)</c> (rainsnow.qc:31-57).</summary>
    public static void RainSetup(Entity this_)
        => Setup(this_, "func_rain", new Vector3(0f, 0f, -700f), StateRain);

    /// <summary><c>spawnfunc(func_snow)</c> (rainsnow.qc:71-97).</summary>
    public static void SnowSetup(Entity this_)
        => Setup(this_, "func_snow", new Vector3(0f, 0f, -300f), StateSnow);

    private static void Setup(Entity this_, string className, Vector3 defaultDest, int state)
    {
        this_.ClassName = className;

        // dest = the velocity key; the entity itself never moves.
        this_.Dest = this_.Velocity;
        this_.Velocity = Vector3.Zero;
        if (this_.Dest == Vector3.Zero)
            this_.Dest = defaultDest;

        this_.Angles = Vector3.Zero;
        this_.MoveType = MoveType.None;
        this_.Solid = Solid.Not;

        // SetBrushEntityModel: the inline brush gives the volume its bounds.
        if (Api.Services is not null && !string.IsNullOrEmpty(this_.Model))
            Api.Entities.SetModel(this_, this_.Model);

        if (this_.Cnt == 0)
            this_.Cnt = 12; // palette colorbase, default white

        if (this_.ParticleCount == 0f)
            this_.ParticleCount = 2000f;
        if (this_.ParticleCount < 1f)
            this_.ParticleCount = 1f;
        if (this_.ParticleCount > 65535f)
            this_.ParticleCount = 65535f;

        // QC .state — reused via MoverState exactly as QC reuses the shared .state field.
        this_.MoverState = state;

        MapMover.IndexRegister(this_);
    }

    /// <summary>
    /// The CSQC per-frame particle budget (Draw_RainSnow, rainsnow.qc:110):
    /// <c>bound(1, 0.1 * count * (sx/1024) * (sy/1024), 65535)</c> — particles per second for the clipped
    /// effect box. Pure function so the client node and the tests share it.
    /// </summary>
    public static float DrawCount(float count, float sizeX, float sizeY)
        => QMath.Bound(1f, 0.1f * count * (sizeX / 1024f) * (sizeY / 1024f), 65535f);
}
