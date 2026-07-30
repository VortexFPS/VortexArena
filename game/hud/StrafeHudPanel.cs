using System;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Hud;

/// <summary>
/// Strafe-jump helper bar — the C# successor to Xonotic's StrafeHUD (HUD panel #25, by Juhu), ported from
/// Base/.../qcsrc/client/hud/panel/strafehud.qc (+ strafehud/draw.qc, draw_core.qc, util.qc, extra.qc).
///
/// The panel draws a horizontal angle bar centered on the player. The bar is divided into colored zones that
/// tell the player, for their current speed, which view-angle relative to their velocity will accelerate them:
/// a large <b>neutral</b> band, a thin <b>pre-accel</b> band where speed is preserved, the <b>accel</b> bands
/// (the strafe sweet-spot — turning here gains speed) and the <b>overturn</b> band (turning past here loses
/// speed). A current-angle indicator marks where the player is aiming relative to velocity, a best-angle marker
/// shows the optimal angle, and switch/wturn indicators show where to flip strafe direction. A slick detector
/// frames the bar when standing on a zero-friction surface, and optional fading text indicators show start
/// speed, jump height, vertical angle and strafe efficiency.
///
/// <para>Faithful core kept: the full zone math (ground-friction + airborne + onground modes), the gradient /
/// progressbar / drawfill bar styles, the three projection modes (linear / perspective / panoramic) with the
/// proper non-linear offset+width projection, the velocity- vs view-centered modes, the wishangle/direction/
/// forward-key derivation, the demo (<c>_hud_configure</c>) triangle-wave sweep, and the luma color/alpha
/// defaults. The physics inputs (max speed/accel/friction/etc.) are read from the engine movement cvars
/// (<c>sv_maxspeed</c>, <c>sv_accelerate</c>, …) instead of the per-entity PHYS_* accessors, which the client
/// HUD layer does not have direct access to.</para>
///
/// <para>Data is fed by the integration layer via <see cref="Player"/> (velocity + view angles + onground) and
/// the optional <see cref="WishDir"/> / <see cref="JumpHeld"/> / <see cref="OnSlick"/> setters (the analogue
/// of the local move-values + jump button + slick trace the QC reads). When fed nothing, the panel self-blanks
/// (the QC <c>!strafeplayer</c> early-out). When <see cref="WishDir"/> is unset the panel degrades to the QC
/// non-local behavior (movespeed = maxspeed, wishangle derived as a standard W+A strafe), so it stays useful.</para>
/// </summary>
public partial class StrafeHudPanel : HudPanel
{
    // ===== fed by the integration layer (the QC strafeplayer + STAT/PHYS_INPUT inputs) =====

    /// <summary>The local player whose velocity + view angles drive the bar (QC <c>strafeplayer</c> /
    /// <c>csqcplayer</c>). Null → the panel draws nothing (QC <c>!csqcplayer || !strafeplayer</c> early-out).</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// QC <c>PHYS_INPUT_MOVEVALUES(strafeplayer)</c> — the local player's wish-move (x = forward/back,
    /// y = right/left, in the input scale, sign is what matters). Null means "not available" (spectated / remote),
    /// in which case the panel falls back to the QC non-local path (movespeed = maxspeed, wishangle from a W+A
    /// assumption). Feed this for the local player to get the exact wishangle the QC computes.
    /// </summary>
    public NVec3? WishDir { get; set; }

    /// <summary>QC <c>StrafeHUD_DetermineJumpHeld</c>: jump (or jetpack) held. While true the HUD assumes air
    /// physics even on the ground (avoids flicker landing). Optional; defaults false.</summary>
    public bool JumpHeld { get; set; }

    /// <summary>QC <c>StrafeHUD_DetermineOnSlick</c>: the surface under the player has the SLICK flag (or the map
    /// is all-slick). Optional — the slick trace isn't done client-side here; the integration layer may feed it.</summary>
    public bool OnSlick { get; set; }

    /// <summary>QC <c>VF_FOVX</c> — the current horizontal field of view, used by the <c>_range -1</c> ("use FOV")
    /// mode. Optional; defaults to 90 when unset.</summary>
    public float FovX { get; set; } = 90f;

    /// <summary>QC <c>race_timespeed</c> — speed when crossing the race start trigger, for the start-speed text.
    /// Optional (feed from the race timer); 0 disables the capture.</summary>
    public float RaceStartSpeed { get; set; }

    /// <summary>QC <c>race_checkpointtime</c> — timestamp of the last checkpoint crossing, latches the start-speed
    /// capture. Optional.</summary>
    public float RaceCheckpointTime { get; set; }

    /// <summary>QC <c>hud_speed_unit</c> (1=qu/s … 5=knots) — selects the speed/length unit for the text readouts.</summary>
    public int SpeedUnit { get; set; } = 1;

    // ===== QC enum mirrors (strafehud.qh) =====

    private const int MODE_VIEW_CENTERED = 0, MODE_VELOCITY_CENTERED = 1;
    private const int ONGROUND_OVERTURN = 0, ONGROUND_GROUND = 1, ONGROUND_AIR = 2;
    private const int DIRECTION_NONE = 0, DIRECTION_LEFT = 1, DIRECTION_RIGHT = 2;
    private const int SWITCH_NONE = 0, SWITCH_ACTUAL = 1, SWITCH_NORMAL = 2, SWITCH_SIDESTRAFE = 3;
    private const int WTURN_NONE = 0, WTURN_ONLY = 1, WTURN_NORMAL = 2, WTURN_SIDESTRAFE = 3;
    private const int KEYS_NONE = 0, KEYS_FORWARD = 1, KEYS_BACKWARD = 2;
    private const int STYLE_DRAWFILL = 0, STYLE_PROGRESSBAR = 1, STYLE_GRADIENT = 2, STYLE_SOFT_GRADIENT = 3;
    private const int GRADIENT_NONE = 0, GRADIENT_LEFT = 1, GRADIENT_RIGHT = 2, GRADIENT_BOTH = 3;
    private const int PROJECTION_LINEAR = 0, PROJECTION_PERSPECTIVE = 1, PROJECTION_PANORAMIC = 2;

    private const float DEG2RAD = QMath.Deg2Rad;
    private const float RAD2DEG = QMath.Rad2Deg;
    private const float ACOS_SQRT2_3_DEG = 35.2643896827546543153f; // acos(sqrt(2/3)) * RAD2DEG

    // ===== local frame state (QC statics) =====
    private double _localClock;
    private float _lastTime;
    private float _onGroundLastTime;
    private bool _onSlickLast;
    private bool _turn;
    private float _turnLastTime;
    private float _turnAngle;
    private float _demoPosition = -37f / 55f; // QC demo_position init

    // text-indicator capture state (QC statics)
    private float _startSpeed, _startTime;
    private float _jumpHeight, _jumpTime, _jumpHeightMin, _jumpHeightMax;

    // per-frame projection cache (the QC autocvar read once per draw)
    private int _projection;

    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private float Now() => Api.Services is not null ? (float)Api.Clock.Time : (float)_localClock;

    /// <summary>QC <c>HUD_StrafeHUD</c> top gate: <c>hud_panel_strafehud</c> is a multi-value show-mode
    /// (0 off / 1 on / 2 on incl. observing / 3 Race·CTS only / 4 Race·CTS incl. observing), matching the physics
    /// panel. Default 3 — Race/CTS only. (Base strafehud.qc.)</summary>
    public override bool ResolveVisible(in HudShowContext ctx)
        => ctx.Configuring || ResolveShowMode(ShowModeCvar(), ctx);

    // -------------------------------------------------------------------------------------------------
    //  Behaviour-cvar defaults (HudConfig invokes this by reflection). Mirrors strafehud.qh defaults.
    // -------------------------------------------------------------------------------------------------
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags S = CvarFlags.Save;
        const string P = "hud_panel_strafehud_";

        // primary behaviour (the contract's named cvars)
        c.Register(P + "mode", "0", S);
        c.Register(P + "style", "2", S);
        c.Register(P + "range", "90", S);
        c.Register(P + "range_sidestrafe", "-2", S);
        c.Register(P + "unit_show", "1", S);
        c.Register(P + "projection", "0", S);
        c.Register(P + "onground_mode", "2", S);
        c.Register(P + "onground_friction", "1", S);
        c.Register(P + "dynamichud", "1", S);
        c.Register("_" + "hud_panel_strafehud_demo", "0", S);

        c.Register(P + "timeout_ground", "0.1", S);
        c.Register(P + "timeout_turn", "0.1", S);
        c.Register(P + "antiflicker_angle", "0.01", S);
        c.Register(P + "fps_update", "0.5", S);

        // bar zones (color triples + alpha)
        c.Register(P + "bar_preaccel", "1", S);
        c.Register(P + "bar_preaccel_color", "0 1 0", S);
        c.Register(P + "bar_preaccel_alpha", "0.5", S);
        c.Register(P + "bar_neutral_color", "1 1 1", S);
        c.Register(P + "bar_neutral_alpha", "0.1", S);
        c.Register(P + "bar_accel_color", "0 1 0", S);
        c.Register(P + "bar_accel_alpha", "0.5", S);
        c.Register(P + "bar_overturn_color", "1 0 1", S);
        c.Register(P + "bar_overturn_alpha", "0.5", S);

        // current-angle indicator
        c.Register(P + "angle_alpha", "0.8", S);
        c.Register(P + "angle_preaccel_color", "0 1 1", S);
        c.Register(P + "angle_neutral_color", "1 1 0", S);
        c.Register(P + "angle_accel_color", "0 1 1", S);
        c.Register(P + "angle_overturn_color", "1 0 1", S);
        c.Register(P + "angle_line", "0", S);
        c.Register(P + "angle_line_width", "0.001", S);
        c.Register(P + "angle_line_height", "1", S);
        c.Register(P + "angle_arrow", "1", S);
        c.Register(P + "angle_arrow_size", "0.5", S);

        // best-angle marker
        c.Register(P + "bestangle", "1", S);
        c.Register(P + "bestangle_color", "1 1 1", S);
        c.Register(P + "bestangle_alpha", "0.5", S);
        c.Register(P + "bestangle_line", "0", S);
        c.Register(P + "bestangle_line_width", "0.001", S);
        c.Register(P + "bestangle_line_height", "1", S);
        c.Register(P + "bestangle_arrow", "1", S);
        c.Register(P + "bestangle_arrow_size", "0.5", S);

        // switch indicator
        c.Register(P + "switch", "1", S);
        c.Register(P + "switch_minspeed", "-1", S);
        c.Register(P + "switch_color", "1 1 0", S);
        c.Register(P + "switch_alpha", "0.5", S);
        c.Register(P + "switch_line", "0", S);
        c.Register(P + "switch_line_width", "0.001", S);
        c.Register(P + "switch_line_height", "1", S);
        c.Register(P + "switch_arrow", "1", S);
        c.Register(P + "switch_arrow_size", "0.5", S);

        // w-turn indicator
        c.Register(P + "wturn", "1", S);
        c.Register(P + "wturn_color", "0 0 1", S);
        c.Register(P + "wturn_alpha", "0.5", S);
        c.Register(P + "wturn_proper", "0", S);
        c.Register(P + "wturn_unrestricted", "0", S);
        c.Register(P + "wturn_line", "0", S);
        c.Register(P + "wturn_line_width", "0.001", S);
        c.Register(P + "wturn_line_height", "1", S);
        c.Register(P + "wturn_arrow", "1", S);
        c.Register(P + "wturn_arrow_size", "0.5", S);

        // direction indicator
        c.Register(P + "direction", "0", S);
        c.Register(P + "direction_color", "0 0.5 1", S);
        c.Register(P + "direction_alpha", "1", S);
        c.Register(P + "direction_width", "0.25", S);
        c.Register(P + "direction_length", "0.02", S);

        // slick detector
        c.Register(P + "slickdetector", "1", S);
        c.Register(P + "slickdetector_range", "200", S);          // QC traces this far; here it only gates the display
        c.Register(P + "slickdetector_granularity", "1", S);      // QC trace fan resolution (no trace done in the HUD layer)
        c.Register(P + "slickdetector_color", "0 1 1", S);
        c.Register(P + "slickdetector_alpha", "0.5", S);
        c.Register(P + "slickdetector_height", "0.125", S);

        // text indicators
        c.Register(P + "startspeed", "1", S);
        c.Register(P + "startspeed_fade", "4", S);
        c.Register(P + "startspeed_color", "1 0.75 0", S);
        c.Register(P + "startspeed_pos", "0 -1 0", S);
        c.Register(P + "startspeed_size", "1.5", S);
        c.Register(P + "jumpheight", "0", S);
        c.Register(P + "jumpheight_fade", "4", S);
        c.Register(P + "jumpheight_min", "50", S);
        c.Register(P + "jumpheight_color", "0 1 0.75", S);
        c.Register(P + "jumpheight_pos", "0 -2 0", S);
        c.Register(P + "jumpheight_size", "1.5", S);
        c.Register(P + "vangle", "0", S);
        c.Register(P + "vangle_color", "0.75 0.75 0.75", S);
        c.Register(P + "vangle_pos", "-0.25 1 0", S);
        c.Register(P + "vangle_size", "1", S);
        c.Register(P + "strafeefficiency", "0", S);
        c.Register(P + "strafeefficiency_pos", "0.25 1 0", S);
        c.Register(P + "strafeefficiency_size", "1", S);
    }

    // -------------------------------------------------------------------------------------------------
    //  cvar shorthands (panel-scoped + a couple of engine movement cvars)
    // -------------------------------------------------------------------------------------------------
    private float Cf(string suffix, float fallback) => CvarF(suffix, fallback);
    private bool Cb(string suffix, bool fallback) => CvarF(suffix, fallback ? 1f : 0f) != 0f;
    private NVec3 Ccol(string suffix, NVec3 fallback) =>
        TryParseRgb(CvarStr(suffix), out Color c) ? new NVec3(c.R, c.G, c.B) : fallback;
    private NVec3 Cvec(string suffix, NVec3 fallback)
    {
        string s = CvarStr(suffix);
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string[] p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return fallback;
        return float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
            && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
            && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z)
            ? new NVec3(x, y, z) : fallback;
    }

    // QC PHYS_* successors: read the engine movement cvars from the shared store, with Xonotic defaults.
    private float PhysMaxSpeed() => GlobalF("sv_maxspeed", 800f);  // Xonotic g_balance default
    private float PhysMaxAirSpeed() => GlobalF("sv_maxairspeed", 320f);
    private float PhysAccelerate() => GlobalF("sv_accelerate", 5.5f);
    private float PhysAirAccelerate() => GlobalF("sv_airaccelerate", 1f);
    private float PhysSlickAccelerate() => PhysAirAccelerate();
    private float PhysFriction() => GlobalF("sv_friction", 5f);
    private float PhysFrictionSlick() => GlobalF("sv_friction_slick", 0.5f);
    private float PhysStopSpeed() => GlobalF("sv_stopspeed", 100f);
    private float PhysAirStopAccelerate() => GlobalF("sv_airstopaccelerate", 0f);
    private bool PhysAirStopAccelerateFull() => GlobalF("sv_aircontrol", 0f) == 0f; // unused tail-case, conservative
    private float PhysMaxAirStrafeSpeed() => GlobalF("sv_maxairstrafespeed", 0f);
    private float PhysAirStrafeAccelerate() => GlobalF("sv_airstrafeaccelerate", 0f);
    private float PhysAirControl() => GlobalF("sv_aircontrol", 0f);
    private int PhysAirControlFlags() => (int)GlobalF("sv_aircontrol_flags", 0f);
    private float PhysAirControlPenalty() => GlobalF("sv_aircontrol_penalty", 0f);
    private float PhysAirControlPower() => GlobalF("sv_aircontrol_power", 2f);
    private float PhysAirAccelQw() => GlobalF("sv_airaccel_qw", 1f);
    private float Ticrate() => GlobalF("sys_ticrate", 1f / 60f);
    private bool VFlipped() => GlobalF("v_flipped", 0f) != 0f;
    private bool HudConfigure() => GlobalF("_hud_configure", 0f) != 0f;

    // -------------------------------------------------------------------------------------------------
    //  Main draw (port of HUD_StrafeHUD)
    // -------------------------------------------------------------------------------------------------
    protected override void DrawPanel()
    {
        bool configure = HudConfigure();
        if (Player is null && !configure) { _lastTime = Now(); return; } // QC: nothing to draw

        DrawBackground();

        // The QC works in panel-local space with panel_pos applied; here _Draw is already panel-local
        // (origin = top-left), so panel_pos == 0. panel_size == Size2. panel_fg_alpha == LiveFgAlpha.
        Vector2 size = Size2;
        if (size.X <= 0f || size.Y <= 0f) { _lastTime = Now(); return; }

        _projection = (int)Cf("projection", PROJECTION_LINEAR);
        float time = Now();
        float ftSelf = PhysFriction();

        // ---- player physical state ----
        NVec3 vel = Player?.Velocity ?? NVec3.Zero;
        NVec3 viewAngles = Player?.Angles ?? NVec3.Zero;
        bool realOnGround = Player?.OnGround ?? false;
        bool jumpHeld = JumpHeld;

        bool onground = realOnGround && !jumpHeld;
        bool onslick = OnSlick;
        bool ducked = false; // crouch state not exposed to the HUD layer; QC maxspeed_mod handles it

        // ground timeout for slick ramps (QC static onground_lasttime / onslick_last)
        if (onground)
        {
            onslick = OnSlick;
            _onGroundLastTime = time;
            _onSlickLast = onslick;
        }
        else if (jumpHeld)
            _onGroundLastTime = 0f;

        bool ongroundExpired = _onGroundLastTime == 0f
            || (time - _onGroundLastTime) >= Cf("timeout_ground", 0.1f);

        float maxspeedMod = ducked ? 0.5f : 1f;
        float maxspeedPhys = onground ? PhysMaxSpeed() : PhysMaxAirSpeed();
        float maxspeed = !configure ? maxspeedPhys * maxspeedMod : 320f;
        float maxaccelPhys = onground ? PhysAccelerate() : PhysAirAccelerate();
        float maxaccel = !configure ? maxaccelPhys : 1f;

        if (!onground && !ongroundExpired) // ground timeout not yet expired → use ground physics
        {
            onground = true;
            onslick = _onSlickLast;
            if (!configure)
            {
                maxspeed = PhysMaxSpeed() * maxspeedMod;
                maxaccel = PhysAccelerate();
            }
        }
        else if (onslick)
        {
            if (!configure) maxaccel = PhysSlickAccelerate();
        }

        // ---- wish / movement ----
        NVec3 movement = WishDir ?? NVec3.Zero;
        bool isLocal = WishDir.HasValue;

        float movespeed;
        if (isLocal)
        {
            movespeed = MathF.Min(Len2(movement), maxspeed);
            if (movespeed == 0f) movespeed = maxspeed; // assume maxspeed so the HUD stays useful
        }
        else
        {
            movespeed = maxspeed;
        }

        int keysFwd = DetermineForwardKeys(movement, isLocal);
        float wishangle = DetermineWishAngle(movement, isLocal);
        float absoluteWishangle = MathF.Abs(wishangle);
        bool strafekeys = MathF.Abs(wishangle) > 45f;

        // ---- air-strafe turning detection (QC static turn / turn_lasttime / turnangle) ----
        float strafity = 0f;
        if (!strafekeys || onground || configure)
            _turn = false;
        else
        {
            bool turnExpired = (time - _turnLastTime) >= Cf("timeout_turn", 0.1f);
            if (strafekeys) _turn = true;
            else if (turnExpired) _turn = false;

            if (_turn)
            {
                if (strafekeys) { _turnLastTime = time; _turnAngle = wishangle; }
                else wishangle = _turnAngle;

                strafity = 1f - (90f - MathF.Abs(wishangle)) / 45f;

                if (PhysMaxAirStrafeSpeed() != 0f)
                    maxspeed = MathF.Min(maxspeed, GeomLerp(PhysMaxAirSpeed(), strafity, PhysMaxAirStrafeSpeed()));
                movespeed = MathF.Min(movespeed, maxspeed);
                if (PhysAirStrafeAccelerate() != 0f)
                    maxaccel = GeomLerp(PhysAirAccelerate(), strafity, PhysAirStrafeAccelerate());
            }
        }

        float dt = DetermineFrameTime();
        maxaccel *= dt * movespeed;
        float bestspeed = MathF.Max(movespeed - maxaccel, 0f);

        // ---- velocity / speed ----
        NVec3 strafevelocity = vel;
        float speed = !configure ? Len2(strafevelocity) : 1337f;
        bool moving = speed > 0f;

        // ---- friction speed (QC physics.qc replica) ----
        float frictionspeed, strafespeed;
        if (moving && onground)
        {
            float strafefriction = onslick ? PhysFrictionSlick() : PhysFriction();
            if (strafefriction > 0f)
            {
                float Sstop = PhysStopSpeed();
                const float dtR = 1f / 60f; // PHYS_FRICTION_REPLICA_DT
                float independentGeometric = MathF.Pow(1f - strafefriction * dtR, dt / dtR);
                if (Sstop < speed && speed < Sstop / independentGeometric)
                    strafespeed = Sstop - Sstop * strafefriction * (dt - (dtR * MathF.Log(Sstop / speed)) / MathF.Log(1f - strafefriction * dtR));
                else if (speed >= Sstop)
                    strafespeed = speed * independentGeometric;
                else
                    strafespeed = speed - strafefriction * dt * Sstop;
                strafespeed = MathF.Max(0f, strafespeed);
            }
            else strafespeed = speed;
            frictionspeed = speed - strafespeed;
        }
        else { frictionspeed = 0f; strafespeed = speed; }

        // ---- current strafe angle (QC) ----
        float angle;
        bool fwd;
        if (!configure)
        {
            if (moving)
            {
                float velAngle = QMath.VecToAngles(strafevelocity).Y;
                if (velAngle > 180f) velAngle -= 360f;
                float viewAngle = viewAngles.Y;

                angle = velAngle - viewAngle;
                if (angle > 180f) angle -= 360f;
                else if (angle < -180f) angle += 360f;

                if (MathF.Abs(wishangle) != 90f)
                {
                    if (keysFwd == KEYS_FORWARD) fwd = true;
                    else if (keysFwd == KEYS_BACKWARD) fwd = false;
                    else fwd = MathF.Abs(angle) <= 90f;
                }
                else
                {
                    if (wishangle < 0f) fwd = angle <= -wishangle;
                    else fwd = angle >= -wishangle;
                }

                if (!fwd)
                {
                    if (angle < 0f) angle += 180f;
                    else angle -= 180f;
                }
            }
            else { angle = 0f; fwd = true; }
        }
        else // demo sweep for HUD setup
        {
            const float demoMaxAngle = 55f;
            const float demoTurnSpeed = 40f;
            if (GlobalF("_hud_panel_strafehud_demo", 0f) != 0f)
            {
                float demoDt = time - _lastTime;
                float demoStep = (demoTurnSpeed / demoMaxAngle) * demoDt;
                _demoPosition = ((_demoPosition + demoStep) % 4f + 4f) % 4f;
            }
            if (_demoPosition > 3f) angle = -1f + (_demoPosition - 3f);
            else if (_demoPosition > 1f) angle = +1f - (_demoPosition - 1f);
            else angle = _demoPosition;
            angle *= demoMaxAngle;

            fwd = true;
            wishangle = 45f;
            if (angle < 0f) wishangle *= -1f;
        }

        if (!fwd) wishangle *= -1f;

        if (VFlipped()) { angle *= -1f; wishangle *= -1f; }

        float airstopaccel = PhysAirStopAccelerate();
        if (airstopaccel == 0f || _turn) airstopaccel = 1f;

        // ---- best / pre-best / overturn angles ----
        float bestangle, prebestangle, overturnangle;
        bool ongroundFriction = Cb("onground_friction", true);
        int ongroundMode = (int)Cf("onground_mode", ONGROUND_AIR);
        bool barPreaccel = Cb("bar_preaccel", true);
        if (!moving)
        {
            prebestangle = bestangle = 0f;
            overturnangle = 180f;
        }
        else if (onground && ongroundFriction)
        {
            bestangle = strafespeed > bestspeed ? MathF.Acos(bestspeed / strafespeed) * RAD2DEG : 0f;

            float prebestSqrt = movespeed * movespeed + strafespeed * strafespeed - speed * speed;
            prebestangle = (prebestSqrt > 0f && strafespeed > MathF.Sqrt(prebestSqrt))
                ? MathF.Acos(MathF.Sqrt(prebestSqrt) / strafespeed) * RAD2DEG
                : (prebestSqrt > 0f ? 0f : 90f);

            float overturnNumer = speed * speed - strafespeed * strafespeed - maxaccel * maxaccel;
            float overturnDenom = 2f * maxaccel * strafespeed;
            overturnangle = overturnDenom > MathF.Abs(overturnNumer)
                ? MathF.Acos(overturnNumer / overturnDenom) * RAD2DEG
                : (overturnNumer < 0f ? 180f : 0f);

            if (overturnangle < bestangle || bestangle < prebestangle)
            {
                if (ongroundMode == ONGROUND_OVERTURN)
                {
                    prebestangle = bestangle = 0f;
                    overturnangle = 0f;
                }
                else if (ongroundMode == ONGROUND_GROUND)
                {
                    overturnangle = bestangle;
                    prebestangle = bestangle;
                }
                else
                {
                    bestangle = MathF.Acos(bestspeed / speed) * RAD2DEG;
                    prebestangle = MathF.Acos(movespeed / speed) * RAD2DEG;
                    overturnangle = (airstopaccel == 1f || PhysAirStopAccelerateFull())
                        ? MathF.Acos(-(airstopaccel * maxaccel * 0.5f) / speed) * RAD2DEG
                        : MathF.Acos(-maxaccel / (2f * speed - (airstopaccel - 1f) * maxaccel)) * RAD2DEG;
                }
            }
        }
        else
        {
            bestangle = speed > bestspeed ? MathF.Acos(bestspeed / speed) * RAD2DEG : 0f;
            prebestangle = speed > movespeed ? MathF.Acos(movespeed / speed) * RAD2DEG : 0f;
            overturnangle = speed > airstopaccel * maxaccel * 0.5f
                ? ((airstopaccel == 1f || PhysAirStopAccelerateFull())
                    ? MathF.Acos(-(airstopaccel * maxaccel * 0.5f) / speed) * RAD2DEG
                    : MathF.Acos(-maxaccel / (2f * speed - (airstopaccel - 1f) * maxaccel)) * RAD2DEG)
                : 180f;
        }
        float absoluteBestangle = bestangle;
        float absolutePrebestangle = prebestangle;
        float absoluteOverturnangle = overturnangle;

        // ---- w-turn best angle ----
        float aircontrol = PhysAirControl();
        int aircontrolFlags = PhysAirControlFlags();
        bool isAircontrolKeys = (keysFwd == KEYS_FORWARD)
            || ((aircontrolFlags & 1) != 0 && keysFwd == KEYS_BACKWARD);
        bool isAircontrolDirection = fwd || (aircontrolFlags & 1) != 0;
        bool airaccelQw = PhysAirAccelQw() == 1f;

        bool wturning = (wishangle == 0f) && !onground && isAircontrolKeys;
        bool wturnValid = false;
        float wturnBestangle = 0f;
        int wturnCvar = (int)Cf("wturn", WTURN_ONLY);
        if (wturnCvar != WTURN_NONE && moving
            && aircontrol != 0f && PhysAirControlPenalty() == 0f
            && (airaccelQw || (int)Cf("wturn_unrestricted", 0f) == 1))
        {
            float wturnPower = PhysAirControlPower();
            if (wturnPower == 2f)
            {
                float wturnA = 32f * MathF.Abs(aircontrol) * dt;
                if ((aircontrolFlags & 4) != 0) wturnA *= maxspeed / maxspeedPhys;
                float wturnV = 1f - (wturnA * wturnA) / (speed * speed);
                if (Cb("wturn_proper", false) && wturnA > 1f && wturnV < 1f && wturnV > -1f)
                    wturnBestangle = MathF.Acos(-speed / wturnA * (MathF.Cos((MathF.Acos(wturnV) + MathF.PI * 2f) / 3f) * 2f + 1f)) * RAD2DEG;
                else
                    wturnBestangle = ACOS_SQRT2_3_DEG;
                wturnValid = true;
            }
            else if (!Cb("wturn_proper", false) && wturnPower >= 0f)
            {
                wturnBestangle = MathF.Acos(MathF.Sqrt(wturnPower / (wturnPower + 1f))) * RAD2DEG;
                wturnValid = true;
            }
        }
        float absoluteWturnBestangle = wturnBestangle;

        // ---- normal (W+A) switch indicators while wturning / side strafing ----
        float nBestangle = 0f;
        float absoluteNPrebestangle = 0f;
        int switchCvar = (int)Cf("switch", SWITCH_ACTUAL);
        bool drawNormal = ((switchCvar >= SWITCH_NORMAL && wturning)
            || (switchCvar == SWITCH_SIDESTRAFE && _turn));
        if (drawNormal || wturnValid)
        {
            float nMaxspeed = PhysMaxAirSpeed() * maxspeedMod;
            float nMovespeed = nMaxspeed;
            float nMaxaccel = PhysAirAccelerate() * dt * nMovespeed;
            float nBestspeed = MathF.Max(nMovespeed - nMaxaccel, 0f);
            nBestangle = speed > nBestspeed ? MathF.Acos(nBestspeed / speed) * RAD2DEG - 45f : -45f;
            absoluteNPrebestangle = speed > nMovespeed ? MathF.Acos(nMovespeed / speed) * RAD2DEG : 0f;
        }

        float hudangle = DetermineHudAngle(absoluteWishangle, absoluteOverturnangle, strafity, configure);

        float antiflickerAngle = Mathf.Clamp(Cf("antiflicker_angle", 0.01f), 0f, 180f);
        int direction = DetermineDirection(angle, wishangle, antiflickerAngle);

        if (direction == DIRECTION_LEFT)
        {
            nBestangle *= -1f; bestangle *= -1f; prebestangle *= -1f; overturnangle *= -1f;
        }
        float oppositeBestangle = -bestangle;
        float nOppositeBestangle = -nBestangle;

        bestangle -= wishangle;
        oppositeBestangle -= wishangle;
        nOppositeBestangle -= wishangle;
        prebestangle -= wishangle;
        overturnangle -= wishangle;

        int mode = (int)Cf("mode", MODE_VIEW_CENTERED);
        if (mode < 0 || mode > 1) mode = MODE_VIEW_CENTERED;

        float changeangle = -bestangle;
        float nChangeangle = -nBestangle;
        float nOppositeChangeangle = nOppositeBestangle + nBestangle * 2f;

        float minspeed = Cf("switch_minspeed", -1f);
        if (minspeed < 0f) minspeed = bestspeed + frictionspeed;

        bool oppositeDirection = false;
        float oppositeChangeangle = 0f;
        if ((angle > -wishangle && direction == DIRECTION_LEFT)
            || (angle < -wishangle && direction == DIRECTION_RIGHT))
        {
            oppositeDirection = true;
            oppositeChangeangle = oppositeBestangle + bestangle * 2f;
        }

        float wturnLeftBestangle = wturnBestangle;
        float wturnRightBestangle = -wturnBestangle;

        // view-angle-centered mode shifts the whole bar to follow velocity
        float shiftangle = 0f;
        if (mode == MODE_VIEW_CENTERED)
        {
            shiftangle = -angle;
            bestangle += shiftangle; changeangle += shiftangle;
            oppositeBestangle += shiftangle; oppositeChangeangle += shiftangle;
            nBestangle += shiftangle; nChangeangle += shiftangle;
            nOppositeBestangle += shiftangle; nOppositeChangeangle += shiftangle;
            wturnLeftBestangle += shiftangle; wturnRightBestangle += shiftangle;
        }

        int style = (int)Cf("style", STYLE_GRADIENT);

        // ===== draw the zone bar =====
        DrawStrafeMeter(shiftangle, wishangle, absoluteBestangle, absolutePrebestangle, absoluteOverturnangle,
            moving, hudangle, style, barPreaccel);

        // ===== slick detector frame =====
        bool allSlick = ftSelf == 0f;
        float textOffsetTop = DrawSlickDetector((allSlick && realOnGround) || OnSlick);
        float textOffsetBottom = textOffsetTop;

        if (Cb("direction", false))
            DrawDirectionIndicator(direction, oppositeDirection, fwd);

        // ===== current-angle color + strafe ratio =====
        NVec3 angleNeutral = Ccol("angle_neutral_color", new NVec3(1f, 1f, 0f));
        NVec3 currentangleColor = angleNeutral;
        float strafeRatio = 0f;
        if (moving)
        {
            float moveangle = MathF.Abs(angle + wishangle);
            if (moveangle > 180f) moveangle = 360f - moveangle;

            if (moveangle >= absoluteOverturnangle)
            {
                if (moveangle == absoluteOverturnangle && absoluteOverturnangle == 180f) { }
                else
                {
                    currentangleColor = Ccol("angle_overturn_color", new NVec3(1f, 0f, 1f));
                    strafeRatio = (moveangle - absoluteOverturnangle) / (180f - absoluteOverturnangle);
                    strafeRatio *= -1f;
                }
            }
            else if (moveangle >= absoluteBestangle)
            {
                currentangleColor = Ccol("angle_accel_color", new NVec3(0f, 1f, 1f));
                strafeRatio = (absoluteOverturnangle - moveangle) / (absoluteOverturnangle - absoluteBestangle);
            }
            else if (moveangle >= absolutePrebestangle)
            {
                if (barPreaccel)
                    currentangleColor = Ccol("angle_preaccel_color", new NVec3(0f, 1f, 1f));
                strafeRatio = (moveangle - absolutePrebestangle) / (absoluteBestangle - absolutePrebestangle);
            }

            if (IsGradient(style))
                currentangleColor = MixColors(angleNeutral, currentangleColor, MathF.Abs(strafeRatio));
        }

        float currentangle = 0f;
        if (mode == MODE_VELOCITY_CENTERED)
        {
            if (MathF.Abs(angle) <= 180f - antiflickerAngle) currentangle = angle;
        }

        float maxLineHeight = 0f, maxTopArrow = 0f, maxBottomArrow = 0f;

        // ===== switch indicators =====
        if (switchCvar != SWITCH_NONE && Cf("switch_alpha", 0.5f) > 0f && speed >= minspeed)
        {
            var sz = IndicatorSize("switch", size);
            int numDashes = Mathf.RoundToInt(Cf("switch_line", 0f));
            int arrowMode = (int)Cf("switch_arrow", 1f);
            bool topArrow = arrowMode == 1 || arrowMode >= 3;
            bool botArrow = arrowMode >= 2;
            float arrowSize = MathF.Max(size.Y * MathF.Min(Cf("switch_arrow_size", 0.5f), 10f), 1f);
            if (numDashes > 0) maxLineHeight = MathF.Max(maxLineHeight, sz.Y);
            if (topArrow) maxTopArrow = MathF.Max(maxTopArrow, arrowSize);
            if (botArrow) maxBottomArrow = MathF.Max(maxBottomArrow, arrowSize);

            float currentChange = drawNormal
                ? (oppositeDirection ? nOppositeChangeangle : nChangeangle)
                : (oppositeDirection ? oppositeChangeangle : changeangle);
            float oppositeChange = drawNormal
                ? (oppositeDirection ? nOppositeBestangle : nBestangle)
                : (oppositeDirection ? oppositeBestangle : bestangle);

            NVec3 col = Ccol("switch_color", new NVec3(1f, 1f, 0f));
            float al = Cf("switch_alpha", 0.5f);
            DrawAngleIndicator(currentChange, sz, arrowSize, numDashes, topArrow, botArrow, col, al, hudangle);
            if (direction == DIRECTION_NONE || drawNormal)
                DrawAngleIndicator(oppositeChange, sz, arrowSize, numDashes, topArrow, botArrow, col, al, hudangle);
        }

        // ===== best-angle marker =====
        int bestangleCvar = (int)Cf("bestangle", 1f);
        if (bestangleCvar != 0 && Cf("bestangle_alpha", 0.5f) > 0f
            && (bestangleCvar == 1 || _turn) && direction != DIRECTION_NONE)
        {
            var sz = IndicatorSize("bestangle", size);
            int numDashes = Mathf.RoundToInt(Cf("bestangle_line", 0f));
            int arrowMode = (int)Cf("bestangle_arrow", 1f);
            bool topArrow = arrowMode == 1 || arrowMode >= 3;
            bool botArrow = arrowMode >= 2;
            float arrowSize = MathF.Max(size.Y * MathF.Min(Cf("bestangle_arrow_size", 0.5f), 10f), 1f);
            if (numDashes > 0) maxLineHeight = MathF.Max(maxLineHeight, sz.Y);
            if (topArrow) maxTopArrow = MathF.Max(maxTopArrow, arrowSize);
            if (botArrow) maxBottomArrow = MathF.Max(maxBottomArrow, arrowSize);

            float ghostangle = oppositeDirection ? oppositeBestangle : bestangle;
            DrawAngleIndicator(ghostangle, sz, arrowSize, numDashes, topArrow, botArrow,
                Ccol("bestangle_color", new NVec3(1f, 1f, 1f)), Cf("bestangle_alpha", 0.5f), hudangle);
        }

        // ===== w-turn indicators =====
        if (wturnValid && !onground && isAircontrolDirection
            && Cf("wturn_alpha", 0.5f) > 0f
            && absoluteWturnBestangle < absoluteNPrebestangle
            && ((wturnCvar != WTURN_NONE && wturning)
                || (wturnCvar == WTURN_NORMAL && !_turn)
                || (wturnCvar == WTURN_SIDESTRAFE)))
        {
            var sz = IndicatorSize("wturn", size);
            int numDashes = Mathf.RoundToInt(Cf("wturn_line", 0f));
            int arrowMode = (int)Cf("wturn_arrow", 1f);
            bool topArrow = arrowMode == 1 || arrowMode >= 3;
            bool botArrow = arrowMode >= 2;
            float arrowSize = MathF.Max(size.Y * MathF.Min(Cf("wturn_arrow_size", 0.5f), 10f), 1f);
            if (numDashes > 0) maxLineHeight = MathF.Max(maxLineHeight, sz.Y);
            if (topArrow) maxTopArrow = MathF.Max(maxTopArrow, arrowSize);
            if (botArrow) maxBottomArrow = MathF.Max(maxBottomArrow, arrowSize);

            NVec3 col = Ccol("wturn_color", new NVec3(0f, 0f, 1f));
            float al = Cf("wturn_alpha", 0.5f);
            DrawAngleIndicator(wturnLeftBestangle, sz, arrowSize, numDashes, topArrow, botArrow, col, al, hudangle);
            DrawAngleIndicator(wturnRightBestangle, sz, arrowSize, numDashes, topArrow, botArrow, col, al, hudangle);
        }

        // ===== current-angle indicator =====
        if (Cf("angle_alpha", 0.8f) > 0f)
        {
            var sz = IndicatorSize("angle", size);
            int numDashes = Mathf.RoundToInt(Cf("angle_line", 0f));
            int arrowMode = (int)Cf("angle_arrow", 1f);
            bool topArrow = arrowMode == 1 || arrowMode >= 3;
            bool botArrow = arrowMode >= 2;
            float arrowSize = MathF.Max(size.Y * MathF.Min(Cf("angle_arrow_size", 0.5f), 10f), 1f);
            if (numDashes > 0) maxLineHeight = MathF.Max(maxLineHeight, sz.Y);
            if (topArrow) maxTopArrow = MathF.Max(maxTopArrow, arrowSize);
            if (botArrow) maxBottomArrow = MathF.Max(maxBottomArrow, arrowSize);

            DrawAngleIndicator(currentangle, sz, arrowSize, numDashes, topArrow, botArrow,
                currentangleColor, Cf("angle_alpha", 0.8f), hudangle);
        }

        // offset text by how far the indicators extrude past the bar
        {
            float lineHeightOffset = (maxLineHeight - size.Y) / 2f;
            float angleOffsetTop = lineHeightOffset + maxTopArrow;
            float angleOffsetBottom = lineHeightOffset + maxBottomArrow;
            textOffsetTop = MathF.Max(angleOffsetTop, textOffsetTop);
            textOffsetBottom = MathF.Max(angleOffsetBottom, textOffsetBottom);
        }

        // ===== text indicators =====
        DrawVerticalAngle(viewAngles, textOffsetTop, textOffsetBottom);
        DrawStartSpeed(textOffsetTop, textOffsetBottom);
        DrawStrafeEfficiency(strafeRatio, textOffsetTop, textOffsetBottom);
        DrawJumpHeight(realOnGround, false, textOffsetTop, textOffsetBottom);

        _lastTime = time;
    }

    // -------------------------------------------------------------------------------------------------
    //  util.qc ports
    // -------------------------------------------------------------------------------------------------
    private static float Len2(NVec3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    /// <summary>QC GeomLerp: geometric interpolation a^(1-t) * b^t (used for air-strafe speed/accel blends).</summary>
    private static float GeomLerp(float a, float t, float b)
    {
        if (a <= 0f || b <= 0f) return a + (b - a) * t; // safe fallback
        return a * MathF.Pow(b / a, t);
    }

    // QC: angle / range * panel_size.x. range (hudangle) can legitimately resolve to 0 (e.g. a stationary
    // landed-at-high-speed player where wishangle == overturnangle == 0 makes range_minangle 0), which in QC
    // produces inf/nan but in Godot would spray NaN coordinates into the renderer every frame. Guard the divisor
    // so a degenerate range collapses the bar/indicators to width 0 (drawn as nothing) instead of NaN.
    private static float AngleToWidth(float angle, float range, Vector2 size)
        => range != 0f && float.IsFinite(range) ? angle / range * size.X : 0f;
    private static float AngleToOffset(float angle, float range, Vector2 size) => AngleToWidth(angle, range, size) + size.X * 0.5f;

    private float Project(float ratio, float range, bool reverse)
    {
        range *= DEG2RAD * 0.5f;
        switch (_projection)
        {
            default:
            case PROJECTION_LINEAR:
                return ratio;
            case PROJECTION_PERSPECTIVE:
                if (!reverse) { ratio *= range; ratio = MathF.Tan(ratio) / MathF.Tan(range); }
                else { ratio = MathF.Atan(ratio * MathF.Tan(range)); ratio /= range; }
                break;
            case PROJECTION_PANORAMIC:
                if (!reverse) { ratio *= range; ratio = MathF.Tan(ratio * 0.5f) / MathF.Tan(range * 0.5f); }
                else { ratio = MathF.Atan(ratio * MathF.Tan(range * 0.5f)) * 2f; ratio /= range; }
                break;
        }
        return ratio;
    }

    private float ProjectOffset(float offset, float range, bool reverse, Vector2 size)
    {
        if (_projection == PROJECTION_LINEAR) return offset;
        float ratio = (offset - size.X * 0.5f) / (size.X * 0.5f);
        ratio = Project(ratio, range, reverse);
        return ratio * (size.X * 0.5f) + (size.X * 0.5f);
    }

    private float ProjectWidth(float offset, float width, float range, Vector2 size)
    {
        if (_projection == PROJECTION_LINEAR) return width;
        return ProjectOffset(offset + width, range, false, size) - ProjectOffset(offset, range, false, size);
    }

    private static float GetLengthUnitFactor(int u) => u switch
    {
        2 => 0.0254f,
        3 => 0.0254f * 0.001f,
        4 => 0.0254f * 0.001f * 0.6213711922f,
        5 => 0.0254f * 0.001f * 0.5399568035f,
        _ => 1.0f,
    };
    private static string GetLengthUnit(int u) => u switch { 2 => " m", 3 => " km", 4 => " mi", 5 => " nmi", _ => " qu" };
    private static float GetSpeedUnitFactor(int u) => u switch
    {
        2 => 0.0254f, 3 => 0.0254f * 3.6f, 4 => 0.0254f * 3.6f * 0.6213711922f, 5 => 0.0254f * 1.943844492f, _ => 1.0f,
    };
    private static string GetSpeedUnit(int u) => u switch { 2 => " m/s", 3 => " km/h", 4 => " mph", 5 => " knots", _ => " qu/s" };

    private float DetermineFrameTime()
    {
        // The client-prediction averaging path needs input_timelength which the HUD layer lacks; use ticrate
        // (the QC spectate branch), which is the stable, accurate choice when no per-frame input length is known.
        return Ticrate();
    }

    private float DetermineWishAngle(NVec3 movement, bool isLocal)
    {
        if (isLocal)
        {
            if (movement.Y == 0f) return 0f;
            float wishangle = RAD2DEG * MathF.Atan2(movement.Y, movement.X);
            if (MathF.Abs(wishangle) > 90f)
            {
                if (wishangle < 0f) wishangle += 180f; else wishangle -= 180f;
                wishangle *= -1f;
            }
            return wishangle;
        }
        // non-local: no movement-key info → assume the standard W+A strafe (forward+side at 45°)
        return 45f;
    }

    private int DetermineForwardKeys(NVec3 movement, bool isLocal)
    {
        if (isLocal)
        {
            if (movement.X > 0f) return KEYS_FORWARD;
            if (movement.X < 0f) return KEYS_BACKWARD;
            return KEYS_NONE;
        }
        return KEYS_FORWARD; // non-local: assume forward strafe
    }

    private float DetermineHudAngle(float absoluteWishangle, float absoluteOverturnangle, float strafity, bool configure)
    {
        float rangeMinangle = MathF.Max(absoluteWishangle, absoluteOverturnangle - absoluteWishangle) * 2f;
        float rangeNormal = Cf("range", 90f);
        float rangeSide = Cf("range_sidestrafe", -2f);
        float rangeUsed;
        float hfov = FovX;

        if (float.IsNaN(rangeNormal) || float.IsNaN(rangeSide) || float.IsNaN(hfov)) return 360f;

        if (rangeNormal == 0f) rangeNormal = configure ? 90f : rangeMinangle;
        else if (rangeNormal < 0f) rangeNormal = hfov;

        if (rangeSide < -1f) rangeUsed = rangeNormal;
        else
        {
            if (rangeSide == 0f) rangeSide = configure ? 90f : rangeMinangle;
            else if (rangeSide < 0f) rangeSide = hfov;
            rangeUsed = GeomLerp(rangeNormal, strafity, rangeSide);
        }

        float hudangle = Mathf.Clamp(MathF.Abs(rangeUsed), 0f, 360f);
        switch (_projection)
        {
            case PROJECTION_PERSPECTIVE: hudangle = MathF.Min(hudangle, 170f); break;
            case PROJECTION_PANORAMIC: hudangle = MathF.Min(hudangle, 350f); break;
        }
        return hudangle;
    }

    private int DetermineDirection(float angle, float wishangle, float antiflickerAngle)
    {
        if (wishangle > 0f) return DIRECTION_RIGHT;
        if (wishangle < 0f) return DIRECTION_LEFT;
        if (angle > antiflickerAngle && angle < 180f - antiflickerAngle) return DIRECTION_RIGHT;
        if (angle < -antiflickerAngle && angle > -180f + antiflickerAngle) return DIRECTION_LEFT;
        return DIRECTION_NONE;
    }

    private static NVec3 MixColors(NVec3 a, NVec3 b, float ratio)
    {
        if (ratio <= 0f) return a;
        if (ratio >= 1f) return b;
        return a + (b - a) * ratio;
    }

    private Vector2 CalcTextIndicatorPosition(Vector2 pos, Vector2 size)
        => new(pos.X * size.X * 0.5f, pos.Y * size.Y);

    private Vector2 IndicatorSize(string prefix, Vector2 size)
    {
        float w = MathF.Max(size.X * MathF.Min(Cf(prefix + "_line_width", 0.001f), 10f), 1f);
        float h = MathF.Max(size.Y * MathF.Min(Cf(prefix + "_line_height", 1f), 10f), 1f);
        return new Vector2(w, h);
    }

    private static bool IsGradient(int style) => style == STYLE_GRADIENT || style == STYLE_SOFT_GRADIENT;

    // -------------------------------------------------------------------------------------------------
    //  draw_core.qc ports
    // -------------------------------------------------------------------------------------------------
    private void DrawStrafeMeter(float shiftangle, float wishangle, float absoluteBestangle,
        float absolutePrebestangle, float absoluteOverturnangle, bool moving, float hudangle,
        int style, bool barPreaccel)
    {
        Vector2 size = Size2;
        NVec3 neutralCol = Ccol("bar_neutral_color", new NVec3(1f, 1f, 1f));
        float neutralAlpha = Cf("bar_neutral_alpha", 0.1f);

        if (!moving)
        {
            if (size.X > 0f && size.Y > 0f && neutralAlpha > 0f)
            {
                // both fill + progressbar styles render a solid neutral fill here
                DrawRect(new Rect2(Vector2.Zero, size), Col(neutralCol, neutralAlpha * LiveFgAlpha));
            }
            return;
        }

        float accelOff = absoluteOverturnangle - absoluteBestangle;
        float preaccelOff = MathF.Abs(absoluteBestangle - absolutePrebestangle);
        float overturnOff = 360f - absoluteOverturnangle * 2f;
        if (!barPreaccel) preaccelOff = 0f;

        float cur = 0f;
        float preaccelRight = cur; cur += preaccelOff;
        float accelRight = cur; cur += accelOff;
        float overturnStart = cur; cur += overturnOff;
        float accelLeft = cur; cur += accelOff;
        float preaccelLeft = cur; cur += preaccelOff;
        float neutralStart = cur;
        float neutralOff = 360f - cur;

        shiftangle += neutralOff * 0.5f - wishangle;

        neutralStart += shiftangle;
        accelLeft += shiftangle; accelRight += shiftangle;
        preaccelLeft += shiftangle; preaccelRight += shiftangle;
        overturnStart += shiftangle;

        NVec3 accelCol = Ccol("bar_accel_color", new NVec3(0f, 1f, 0f));
        float accelAlpha = Cf("bar_accel_alpha", 0.5f);
        NVec3 preaccelCol = Ccol("bar_preaccel_color", new NVec3(0f, 1f, 0f));
        float preaccelAlpha = Cf("bar_preaccel_alpha", 0.5f);
        NVec3 overturnCol = Ccol("bar_overturn_color", new NVec3(1f, 0f, 1f));
        float overturnAlpha = Cf("bar_overturn_alpha", 0.5f);

        if (accelOff > 0f)
            DrawStrafeBar(accelLeft, accelOff, accelCol, accelAlpha * LiveFgAlpha, style, GRADIENT_LEFT, hudangle, neutralCol, neutralAlpha);
        if (barPreaccel && preaccelOff > 0f)
            DrawStrafeBar(preaccelLeft, preaccelOff, preaccelCol, preaccelAlpha * LiveFgAlpha, style, GRADIENT_RIGHT, hudangle, neutralCol, neutralAlpha);

        if (accelOff > 0f)
            DrawStrafeBar(accelRight, accelOff, accelCol, accelAlpha * LiveFgAlpha, style, GRADIENT_RIGHT, hudangle, neutralCol, neutralAlpha);
        if (barPreaccel && preaccelOff > 0f)
            DrawStrafeBar(preaccelRight, preaccelOff, preaccelCol, preaccelAlpha * LiveFgAlpha, style, GRADIENT_LEFT, hudangle, neutralCol, neutralAlpha);

        if (overturnOff > 0f)
            DrawStrafeBar(overturnStart, overturnOff, overturnCol, overturnAlpha * LiveFgAlpha, style, GRADIENT_BOTH, hudangle, neutralCol, neutralAlpha);

        if (neutralOff > 0f)
            DrawStrafeBar(neutralStart, neutralOff, neutralCol, neutralAlpha * LiveFgAlpha, style, GRADIENT_NONE, hudangle, neutralCol, neutralAlpha);
    }

    private void DrawAngleIndicator(float angle, Vector2 lineSize, float arrowSize, int numDashes,
        bool hasTopArrow, bool hasBottomArrow, NVec3 color, float alpha, float hudangle)
    {
        if (alpha <= 0f || !float.IsFinite(angle)) return; // a NaN angle (e.g. wturn_proper acos out of range) would spray NaN coords into the renderer
        Vector2 size = Size2;
        angle = Mathf.Clamp(angle, hudangle * -0.5f, hudangle * 0.5f);
        float offset = AngleToOffset(angle, hudangle, size);
        offset = ProjectOffset(offset, hudangle, false, size);
        if (!float.IsFinite(offset)) return;

        DrawAngleIndicatorLine(lineSize, offset, numDashes, color, alpha);
        if (hasTopArrow) DrawAngleIndicatorArrow(arrowSize, offset, lineSize, color, alpha, true);
        if (hasBottomArrow) DrawAngleIndicatorArrow(arrowSize, offset, lineSize, color, alpha, false);
    }

    private void DrawAngleIndicatorLine(Vector2 size, float offset, int numDashes, NVec3 color, float alpha)
    {
        if (numDashes <= 0 || size.X <= 0f || size.Y <= 0f) return;
        Vector2 panel = Size2;
        float segH = size.Y / (Mathf.Clamp(numDashes, 1, (int)MathF.Max(1f, size.Y)) * 2 - 1);
        for (float i = 0f; i < size.Y; i += segH * 2f)
        {
            float thisH = segH;
            if (i + segH * 2f >= size.Y) thisH = size.Y - i;
            var pos = new Vector2(offset - size.X * 0.5f, -((size.Y - panel.Y) * 0.5f - i));
            DrawRect(new Rect2(pos, new Vector2(size.X, thisH)), Col(color, alpha * LiveFgAlpha));
        }
    }

    private void DrawAngleIndicatorArrow(float size, float offset, Vector2 lineSize, NVec3 color, float alpha, bool top)
    {
        if (size <= 0f) return;
        Vector2 panel = Size2;
        if (top)
            DrawStrafeArrow(new Vector2(offset, (panel.Y - lineSize.Y) * 0.5f), size, color, alpha * LiveFgAlpha, true, lineSize.X);
        else
            DrawStrafeArrow(new Vector2(offset, (panel.Y - lineSize.Y) * 0.5f + lineSize.Y), size, color, alpha * LiveFgAlpha, false, lineSize.X);
    }

    private void DrawDirectionIndicator(int direction, bool oppositeDirection, bool fwd)
    {
        Vector2 size = Size2;
        float vWidth = MathF.Max(size.Y * MathF.Min(Cf("direction_width", 0.25f), 1f), 1f);
        float vHeight = size.Y + vWidth * 2f;
        float hWidth = size.X * MathF.Min(Cf("direction_length", 0.02f), 0.5f);
        float hHeight = vWidth;

        if (direction == DIRECTION_NONE || vWidth <= 0f || Cf("direction_alpha", 1f) <= 0f) return;

        bool indicatorDir = direction == DIRECTION_LEFT;
        if (!fwd != oppositeDirection) indicatorDir = !indicatorDir;

        NVec3 col = Ccol("direction_color", new NVec3(0f, 0.5f, 1f));
        float al = Cf("direction_alpha", 1f) * LiveFgAlpha;

        // vertical bar at the appropriate side
        if (vHeight > 0f)
            DrawRect(new Rect2(new Vector2(indicatorDir ? -vWidth : size.X, -hHeight), new Vector2(vWidth, vHeight)), Col(col, al));
        // top horizontal cap
        DrawRect(new Rect2(new Vector2(indicatorDir ? 0f : size.X - hWidth, -hHeight), new Vector2(hWidth, hHeight)), Col(col, al));
        // bottom horizontal cap
        DrawRect(new Rect2(new Vector2(indicatorDir ? 0f : size.X - hWidth, size.Y), new Vector2(hWidth, hHeight)), Col(col, al));
    }

    // -------------------------------------------------------------------------------------------------
    //  draw.qc ports (the bar segment with wrap-around + projection + styles)
    // -------------------------------------------------------------------------------------------------
    private void DrawStrafeBar(float startangle, float offsetangle, NVec3 color, float alpha,
        int style, int gradientType, float range, NVec3 neutralColor, float neutralAlphaCvar)
    {
        Vector2 size = Size2;
        // Degenerate physics edge cases (e.g. the high-speed-landing acos branches) can yield non-finite zone
        // angles; bail rather than push NaN/Inf widths and offsets into the renderer.
        if (!float.IsFinite(startangle) || !float.IsFinite(offsetangle)) return;
        // QC: StrafeHUD_AngleToOffset(startangle % 360, range) — C#'s % keeps the sign like QC's, matching.
        float offset = AngleToOffset(startangle % 360f, range, size);
        float width = AngleToWidth(offsetangle, range, size);
        float mirrorOffset, mirrorWidth;

        if (width <= 0f) return;

        if (IsGradient(style))
        {
            if (gradientType == GRADIENT_NONE)
            {
                style = STYLE_DRAWFILL;
                if (alpha <= 0f) return;
            }
        }
        else if (alpha <= 0f) return;

        float hiddenWidth = (360f - range) / range * size.X;
        float totalWidth = size.X + hiddenWidth;
        float originalWidth = width;

        if (offset < 0f)
        {
            mirrorWidth = MathF.Min(MathF.Abs(offset), width);
            mirrorOffset = offset + totalWidth;
            width += offset;
            offset = 0f;
        }
        else
        {
            mirrorOffset = offset - totalWidth;
            mirrorWidth = MathF.Min(mirrorOffset + width, width);
            if (mirrorOffset < 0f) mirrorOffset = 0f;
        }

        float overflowWidth = offset + width - size.X;
        width = MathF.Max(width, 0f);
        if (overflowWidth > 0f) width = size.X - offset;
        else overflowWidth = 0f;

        Vector2 segSize = new(width, size.Y);
        float originalOffset = offset;

        // QC defers projection for the accelerated STYLE_GRADIENT (it projects per-vertex in R_BeginPolygon).
        // This port renders STYLE_GRADIENT through the same per-segment DrawSoftGradient path as STYLE_SOFT_GRADIENT,
        // so it MUST be projected up-front here like every other style (else the default gradient bar is mis-mapped
        // in perspective/panoramic and its segment ratios are computed in the wrong space).
        if (segSize.X > 0f) segSize.X = ProjectWidth(offset, segSize.X, range, size);
        offset = ProjectOffset(offset, range, false, size);

        if (mirrorOffset < 0f) { mirrorWidth += mirrorOffset; mirrorOffset = 0f; }

        float overflowMirrorWidth = mirrorOffset + mirrorWidth - size.X;
        mirrorWidth = MathF.Max(mirrorWidth, 0f);
        if (overflowMirrorWidth > 0f) mirrorWidth = size.X - mirrorOffset;
        else overflowMirrorWidth = 0f;

        Vector2 mirrorSize = new(mirrorWidth, size.Y);
        float originalMirrorOffset = mirrorOffset;

        // see note above: project for every style (STYLE_GRADIENT included, since it shares the soft path here).
        if (mirrorSize.X > 0f) mirrorSize.X = ProjectWidth(mirrorOffset, mirrorSize.X, range, size);
        mirrorOffset = ProjectOffset(mirrorOffset, range, false, size);

        switch (style)
        {
            default:
            case STYLE_DRAWFILL:
            case STYLE_PROGRESSBAR: // progressbar art unavailable in HUD layer → flat fill (faithful color/alpha)
                if (mirrorSize.X > 0f && mirrorSize.Y > 0f)
                    DrawRect(new Rect2(new Vector2(mirrorOffset, 0f), mirrorSize), Col(color, alpha));
                if (segSize.X > 0f && segSize.Y > 0f)
                    DrawRect(new Rect2(new Vector2(offset, 0f), segSize), Col(color, alpha));
                break;

            case STYLE_GRADIENT:
            case STYLE_SOFT_GRADIENT:
            {
                int gradientStart;
                float gradientOffset, gradientMirrorOffset;

                if (offset == 0f && mirrorOffset == 0f) gradientStart = width > mirrorWidth ? 2 : 1;
                else if (offset == 0f) gradientStart = 2;
                else if (mirrorOffset == 0f) gradientStart = 1;
                else gradientStart = 0;

                switch (gradientStart)
                {
                    default:
                    case 0: gradientOffset = gradientMirrorOffset = 0f; break;
                    case 1: gradientOffset = 0f; gradientMirrorOffset = originalWidth - (mirrorWidth + overflowMirrorWidth); break;
                    case 2: gradientOffset = originalWidth - (width + overflowWidth); gradientMirrorOffset = 0f; break;
                }

                // both gradient styles project per-segment here; the soft variant matches the QC's per-pixel pass,
                // the accelerated variant is approximated by the same segment loop (visually equivalent at HUD sizes)
                if (mirrorSize.X > 0f)
                    DrawSoftGradient(color, neutralColor, mirrorSize, originalWidth, mirrorOffset, originalMirrorOffset,
                        alpha, gradientMirrorOffset, gradientType, range, neutralAlphaCvar);
                if (segSize.X > 0f)
                    DrawSoftGradient(color, neutralColor, segSize, originalWidth, offset, originalOffset,
                        alpha, gradientOffset, gradientType, range, neutralAlphaCvar);
                break;
            }
        }
    }

    private void DrawSoftGradient(NVec3 color1, NVec3 color2, Vector2 size, float originalWidth, float offset,
        float originalOffset, float alpha, float gradientOffset, int gradientType, float range, float neutralAlphaCvar)
    {
        float alpha1 = Mathf.Clamp(alpha, 0f, 1f);
        float alpha2 = Mathf.Clamp(neutralAlphaCvar * LiveFgAlpha, 0f, 1f);
        if (alpha1 == 0f && alpha2 == 0f) return;

        float colorRatio = alpha1 / (alpha1 + alpha2);
        for (int i = 0; i < (int)size.X; ++i)
        {
            float segW = MathF.Min(size.X - i, 1f);
            float segOffset = offset + i;
            float ratioOffset = segOffset + segW / 2f;
            ratioOffset = ProjectOffset(ratioOffset, range, true, Size2);
            ratioOffset += gradientOffset;
            float ratio = (ratioOffset - originalOffset) / originalWidth * (gradientType == GRADIENT_BOTH ? 2f : 1f);
            if (ratio > 1f) ratio = 2f - ratio;
            if (gradientType != GRADIENT_RIGHT) ratio = 1f - ratio;
            float alphaRatio = alpha1 - (alpha1 - alpha2) * ratio;
            float c1 = ratio * (1f - colorRatio);
            float c2 = (1f - ratio) * colorRatio;
            float mix = (c1 + c2) == 0f ? 1f : c1 / (c1 + c2);

            if (alphaRatio > 0f)
                DrawRect(new Rect2(new Vector2(segOffset, 0f), new Vector2(segW, size.Y)),
                    Col(MixColors(color1, color2, mix), alphaRatio));
        }
    }

    private void DrawStrafeArrow(Vector2 origin, float size, NVec3 color, float alpha, bool flipped, float connectionWidth)
    {
        // QC draws this as a filled triangle (R_BeginPolygon). Reproduce with a polygon.
        float width = size * 2f + connectionWidth;
        float height = size;
        if (flipped) origin.Y -= size;

        var pts = new System.Collections.Generic.List<Vector2>();
        if (connectionWidth > 0f)
        {
            pts.Add(origin + new Vector2(connectionWidth / 2f, flipped ? height : 0f));
            pts.Add(origin + new Vector2(-connectionWidth / 2f, flipped ? height : 0f));
        }
        else
            pts.Add(origin + new Vector2(0f, flipped ? height : 0f));

        pts.Add(origin + new Vector2(-width / 2f, flipped ? 0f : height));
        pts.Add(origin + new Vector2(width / 2f, flipped ? 0f : height));

        var col = Col(color, alpha);
        var colors = new Color[pts.Count];
        for (int i = 0; i < colors.Length; i++) colors[i] = col;
        DrawPolygon(pts.ToArray(), colors);
    }

    private void DrawTextIndicator(string text, float height, NVec3 color, float fadetime, float lasttime,
        Vector2 pos, float offsetTop, float offsetBottom)
    {
        if (string.IsNullOrEmpty(text)) return;
        float timeFrac = (Now() - lasttime) / fadetime;
        if (height <= 0f || lasttime <= 0f || fadetime <= 0f || timeFrac > 1f) return;

        float alpha = MathF.Cos(timeFrac * MathF.PI * 0.5f); // QC cos(time_frac * M_PI_2)
        Vector2 size = Size2;
        Vector2 draw;

        if (pos.Y >= 1f)
        {
            float py = pos.Y - 1f;
            var calc = CalcTextIndicatorPosition(new Vector2(pos.X, py), size);
            calc.Y += height + offsetTop;
            calc.Y *= -1f;
            draw = calc;
        }
        else if (pos.Y <= -1f)
        {
            float py = pos.Y + 1f;
            var calc = CalcTextIndicatorPosition(new Vector2(pos.X, py), size);
            calc.Y *= -1f;
            calc.Y += size.Y + offsetBottom;
            draw = calc;
        }
        else return;

        // drawstring_aspect: fit text into (panel_size.x x height) — center it horizontally over the panel.
        int fontSize = Mathf.Clamp(Mathf.RoundToInt(height), 8, 64);
        var c = Col(color, alpha * LiveFgAlpha);
        // draw.x is the left edge in panel space; the box is panel-wide so center within panel width from draw.x
        DrawTextCentered(new Vector2(draw.X, draw.Y), size.X, text, c, fontSize);
    }

    // -------------------------------------------------------------------------------------------------
    //  extra.qc ports (slick detector + text indicators)
    // -------------------------------------------------------------------------------------------------
    private float DrawSlickDetector(bool slickDetected)
    {
        Vector2 size = Size2;
        float h = Mathf.Clamp(Cf("slickdetector_height", 0.125f), 0f, 1f) * size.Y;
        // QC gates the whole detector on slickdetector_range > 0 (range 0 disables it); mirror that here even though
        // the trace fan itself is fed via OnSlick rather than traced client-side.
        if (Cb("slickdetector", true) && Cf("slickdetector_range", 200f) > 0f
            && Cf("slickdetector_alpha", 0.5f) > 0f && h > 0f && size.X > 0f)
        {
            if (slickDetected)
            {
                NVec3 col = Ccol("slickdetector_color", new NVec3(0f, 1f, 1f));
                float al = Cf("slickdetector_alpha", 0.5f) * LiveFgAlpha;
                // top + bottom horizontal bars
                DrawRect(new Rect2(new Vector2(0f, -h), new Vector2(size.X, h)), Col(col, al));
                DrawRect(new Rect2(new Vector2(0f, size.Y), new Vector2(size.X, h)), Col(col, al));
            }
            return h;
        }
        return 0f;
    }

    private void DrawVerticalAngle(NVec3 viewAngles, float offsetTop, float offsetBottom)
    {
        if (!Cb("vangle", false)) return;
        float vangle = -viewAngles.X;
        float height = Cf("vangle_size", 1f) * Size2.Y;
        string text = vangle.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "°";
        DrawTextIndicator(text, height, Ccol("vangle_color", new NVec3(0.75f, 0.75f, 0.75f)), 1f,
            Now(), Cvec2("vangle_pos", new Vector2(-0.25f, 1f)), offsetTop, offsetBottom);
    }

    private void DrawJumpHeight(bool onground, bool swimming, float offsetTop, float offsetBottom)
    {
        float conv = GetLengthUnitFactor(SpeedUnit);
        float originZ = Player?.Origin.Z ?? 0f;
        float velZ = Player?.Velocity.Z ?? 0f;
        bool dead = Player?.IsDead ?? true;
        bool isPlayer = Player is not null;

        if (velZ <= 0f || onground || swimming || dead || !isPlayer)
            _jumpHeightMin = _jumpHeightMax = originZ;
        else if (originZ > _jumpHeightMax)
        {
            _jumpHeightMax = originZ;
            float newH = _jumpHeightMax - _jumpHeightMin;
            if (newH * conv > MathF.Max(Cf("jumpheight_min", 50f), 0f))
            {
                _jumpHeight = newH;
                _jumpTime = Now();
            }
        }

        if (!Cb("jumpheight", false)) return;

        int decimals = SpeedUnit >= 3 && SpeedUnit <= 5 ? 6 : 2;
        float height = Cf("jumpheight_size", 1.5f) * Size2.Y;
        string text = (_jumpHeight * conv).ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        if (Cb("unit_show", true)) text += GetLengthUnit(SpeedUnit);

        DrawTextIndicator(text, height, Ccol("jumpheight_color", new NVec3(0f, 1f, 0.75f)),
            Cf("jumpheight_fade", 4f), _jumpTime, Cvec2("jumpheight_pos", new Vector2(0f, -2f)), offsetTop, offsetBottom);
    }

    private void DrawStrafeEfficiency(float strafeRatio, float offsetTop, float offsetBottom)
    {
        if (!Cb("strafeefficiency", false)) return;
        float height = Cf("strafeefficiency_size", 1f) * Size2.Y;
        string text = (strafeRatio * 100f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
        NVec3 col = MixColors(new NVec3(1f, 1f, 1f), strafeRatio > 0f ? new NVec3(0f, 1f, 0f) : new NVec3(1f, 0f, 0f), MathF.Abs(strafeRatio));
        DrawTextIndicator(text, height, col, 1f, Now(), Cvec2("strafeefficiency_pos", new Vector2(0.25f, 1f)), offsetTop, offsetBottom);
    }

    private void DrawStartSpeed(float offsetTop, float offsetBottom)
    {
        // QC latches on the race start checkpoint; here the integration layer feeds RaceCheckpointTime/RaceStartSpeed.
        if (RaceCheckpointTime > 0f && _startTime != RaceCheckpointTime)
        {
            _startTime = RaceCheckpointTime;
            _startSpeed = RaceStartSpeed;
        }

        if (!Cb("startspeed", true)) return;

        float conv = GetSpeedUnitFactor(SpeedUnit);
        float height = Cf("startspeed_size", 1.5f) * Size2.Y;
        string text = (_startSpeed * conv).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (Cb("unit_show", true)) text += GetSpeedUnit(SpeedUnit);

        DrawTextIndicator(text, height, Ccol("startspeed_color", new NVec3(1f, 0.75f, 0f)),
            Cf("startspeed_fade", 4f), _startTime, Cvec2("startspeed_pos", new Vector2(0f, -1f)), offsetTop, offsetBottom);
    }

    // -------------------------------------------------------------------------------------------------
    //  small helpers
    // -------------------------------------------------------------------------------------------------
    private static Color Col(NVec3 c, float a) => new(c.X, c.Y, c.Z, Mathf.Clamp(a, 0f, 1f));

    private Vector2 Cvec2(string suffix, Vector2 fallback)
    {
        NVec3 v = Cvec(suffix, new NVec3(fallback.X, fallback.Y, 0f));
        return new Vector2(v.X, v.Y);
    }
}
