using System.Globalization;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Hud;

/// <summary>
/// Speedometer / physics panel — feature-complete port of
/// Base/.../qcsrc/client/hud/panel/physics.qc (HUD panel #15). The QC version reads
/// <c>csqcplayer.velocity</c> each frame and draws, per the <c>hud_panel_physics_*</c> + global
/// <c>hud_speed_unit</c> / <c>hud_progressbar_*</c> cvars:
/// <list type="bullet">
///   <item>the player's current speed (2D or 3D via <c>_speed_vertical</c>), as a number and/or a skinned
///         speed <b>progressbar</b> filled to <c>speed / speed_max</c> (the <c>progressbar</c> art).</item>
///   <item>the <b>acceleration</b> as a fraction of gravity (via <c>ACCEL2GRAV</c>, with the same per-frame
///         finite-difference + optional moving-average smoothing), as a number and/or a skinned
///         <b>accelbar</b> centered on zero (<c>_acceleration_progressbar_mode</c>), with the
///         <c>_acceleration_progressbar_scale</c> / <c>_nonlinear</c> options and the slick-aware
///         <c>_acceleration_max_slick</c> selection.</item>
///   <item>the fading <b>top-speed</b> peak text + bar marker (<c>_topspeed</c>/<c>_topspeed_time</c>) and the
///         fading <b>jump-speed</b> text (<c>_jumpspeed</c>/<c>_jumpspeed_time</c>), both eased by
///         <c>cos(time_frac * M_PI_2)</c>, sharing the spot under the speed number with the speed-unit label
///         per the QC text-priority bits.</item>
///   <item>the auto/horizontal/vertical <b>layout</b> (<c>_force_layout</c> + the panel-aspect heuristic), the
///         <c>_flip</c> swap, the <c>_baralign</c> bar/text alignment, the <c>_text</c> / <c>_progressbar</c>
///         mode toggles, the colored speed text (<c>_speed_colored</c>), and the unit conversion + label
///         (qu/s, m/s, km/h, mph, knots).</item>
/// </list>
///
/// Real data: speed/accel are read live from the local <see cref="Player"/> (<c>velocity</c>). The
/// slick/jump-held inputs the QC accel-max selection needs come from the player physics, which aren't all on
/// the entity here, so (mirroring <c>StrafeHudPanel</c>) the net/demo layer feeds <see cref="JumpHeld"/> /
/// <see cref="OnSlick"/> / <see cref="AllSlick"/>; the panel falls back to <see cref="Common.Framework.Entity.OnGround"/>
/// otherwise. The match clock comes from <see cref="Api"/> when wired, else the panel's own wall clock.
///
/// The behaviour cvars (<see cref="RegisterDefaults"/>) are read LIVE from the shared store so console/menu
/// edits move the panel immediately; the pre-existing public properties (set by the net layer) act as the
/// fallback when the matching cvar is unset, preserving that path.
/// </summary>
public partial class PhysicsPanel : HudPanel
{
    // QC PHYSICS_* enums (physics.qh).
    private const int LayoutAutomatic = 0, LayoutHorizontal = 1, LayoutVertical = 2;
    private const int BaralignLeft = 0, BaralignRight = 1, BaralignOnlyLeft = 2, BaralignOnlyRight = 3, BaralignCenter = 4;
    private const int ProgressbarNone = 0, ProgressbarBoth = 1, ProgressbarSpeed = 2, ProgressbarAccel = 3;
    private const int TextNone = 0, TextBoth = 1, TextSpeed = 2, TextAccel = 3;

    // QC ACCEL2GRAV (physics.qh): 0.0254 / 9.80665 — converts qu/s^2 to a fraction of real-world gravity.
    private const double Accel2Grav = 0.00259007918096393775;

    /// <summary>The local player; speed/accel are read from <see cref="Common.Framework.Entity.Velocity"/> each frame.</summary>
    public Player? Player { get; set; }

    // -------------------------------------------------------------------------------------------------
    //  Public data setters (preserved + extended). The net/demo layer feeds these; live cvars override
    //  the matching ones when set so console/menu edits win, otherwise these are the fallback.
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>hud_panel_physics_speed_vertical</c>: include vertical velocity in the speed readout.</summary>
    public bool SpeedVertical { get; set; }

    /// <summary>QC <c>hud_panel_physics_speed_max</c>: speed (in qu/s) the speed bar treats as full.</summary>
    public float SpeedMax { get; set; } = 1800f;

    /// <summary>QC <c>hud_panel_physics_acceleration_max</c>: |accel| (fraction of gravity) the accel bar treats as full.</summary>
    public float AccelMax { get; set; } = 1.5f;

    /// <summary>QC <c>hud_panel_physics_topspeed</c>: show the fading top-speed peak text.</summary>
    public bool ShowTopSpeed { get; set; } = true;

    /// <summary>QC <c>hud_panel_physics_topspeed_time</c>: seconds the top-speed peak holds before fading.</summary>
    public float TopSpeedHold { get; set; } = 4f;

    /// <summary>
    /// QC <c>hud_speed_unit</c>: 1=qu/s, 2=m/s, 3=km/h, 4=mph, 5=knots. Selects the conversion factor + label
    /// (QC <c>GetSpeedUnitFactor</c> / <c>GetSpeedUnit</c>, client/main.qc).
    /// </summary>
    public int SpeedUnit { get; set; } = 1;

    /// <summary>QC <c>hud_panel_physics_speed_unit_show</c>: draw the unit label under the speed number.</summary>
    public bool ShowUnit { get; set; } = true;

    /// <summary>QC <c>StrafeHUD_DetermineJumpHeld</c>: the jump key is held (so the player isn't really on the
    /// ground for the accel-max selection). Fed by the net/demo layer; defaults false.</summary>
    public bool JumpHeld { get; set; }

    /// <summary>QC <c>StrafeHUD_DetermineOnSlick</c>: the player is standing on a slick (low-friction) surface.
    /// Fed by the net/demo layer; selects <c>_acceleration_max_slick</c>.</summary>
    public bool OnSlick { get; set; }

    /// <summary>QC <c>PHYS_FRICTION(strafeplayer) == 0</c>: friction is globally disabled (all surfaces slick).
    /// Fed by the net/demo layer; defaults false.</summary>
    public bool AllSlick { get; set; }

    /// <summary>QC <c>PHYS_SLICKACCELERATE &gt;= PHYS_ACCELERATE</c>: only then does the slick accel-max apply.
    /// Fed by the net/demo layer; defaults true (the stock balance has slick accel ≥ ground accel).</summary>
    public bool SlickAccelExceeds { get; set; } = true;

    // -------------------------------------------------------------------------------------------------
    //  Per-frame state (QC file-scope statics in HUD_Physics).
    // -------------------------------------------------------------------------------------------------

    // QC acc_prev_vel / acc_prev_time / acc_avg: state for the per-frame acceleration computation.
    private NVec3 _accPrevVel;
    private double _accPrevTime;
    private float _accAvg;
    private bool _haveAccPrev;

    // QC top_speed / top_speed_time: peak speed and when it was set.
    private float _topSpeed;
    private double _topSpeedTime;

    // QC jump_speed / jump_speed_time + prev_vel_z / prev_speed2d: jump-speed peak.
    private float _jumpSpeed;
    private double _jumpSpeedTime;
    private float _prevVelZ;
    private float _prevSpeed2d;
    private bool _haveJumpPrev;

    // QC physics_update_time / discrete_*: the on-screen numbers update only every _update_interval.
    private double _physicsUpdateTime;
    private float _discreteSpeed, _discreteTopSpeed, _discreteAccel;

    private double _localClock;

    /// <summary>Register the panel's behaviour-cvar defaults (luma). Auto-invoked by reflection from
    /// <c>HudConfig.RegisterDefaults</c>. The global <c>hud_speed_unit</c> / <c>hud_progressbar_*</c> cvars are
    /// registered by <c>HudConfig</c> itself, so they are NOT (re-)registered here.</summary>
    public static void RegisterDefaults(VortexArena.Engine.Simulation.CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        c.Register("hud_panel_physics_speed_unit_show", "1", save);
        c.Register("hud_panel_physics_speed_max", "1800", save);
        c.Register("hud_panel_physics_speed_vertical", "0", save);
        c.Register("hud_panel_physics_speed_colored", "0", save);
        c.Register("hud_panel_physics_topspeed", "1", save);
        c.Register("hud_panel_physics_topspeed_time", "4", save);
        c.Register("hud_panel_physics_jumpspeed", "0", save);
        c.Register("hud_panel_physics_jumpspeed_time", "1", save);
        c.Register("hud_panel_physics_acceleration_max", "1.5", save);
        c.Register("hud_panel_physics_acceleration_max_slick", "-1", save);
        c.Register("hud_panel_physics_acceleration_vertical", "0", save);
        c.Register("hud_panel_physics_acceleration_movingaverage", "1", save);
        c.Register("hud_panel_physics_acceleration_progressbar_mode", "0", save);
        c.Register("hud_panel_physics_acceleration_progressbar_scale", "1", save);
        c.Register("hud_panel_physics_acceleration_progressbar_nonlinear", "0", save);
        c.Register("hud_panel_physics_flip", "0", save);
        c.Register("hud_panel_physics_baralign", "0", save);
        c.Register("hud_panel_physics_progressbar", "1", save);
        c.Register("hud_panel_physics_text", "1", save);
        c.Register("hud_panel_physics_text_scale", "0.7", save);
        c.Register("hud_panel_physics_force_layout", "0", save);
        c.Register("hud_panel_physics_update_interval", "0.015625", save);
    }

    /// <summary>QC <c>HUD_Physics</c> top gate: <c>hud_panel_physics</c> is a multi-value show-mode
    /// (0 off / 1 on / 2 on incl. observing / 3 Race·CTS only / 4 Race·CTS incl. observing). Default 3, so the
    /// speedo only appears in Race/CTS unless the player opts in. (Base physics.qc:HUD_Physics.)</summary>
    public override bool ResolveVisible(in HudShowContext ctx)
        => ctx.Configuring || ResolveShowMode(ShowModeCvar(), ctx);

    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private double CurrentTime()
    {
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    /// <summary>QC <c>GetSpeedUnitFactor</c> (client/main.qc): qu/s -> selected unit.</summary>
    private static float UnitFactor(int unit) => unit switch
    {
        2 => 0.0254f,
        3 => 0.0254f * 3.6f,
        4 => 0.0254f * 3.6f * 0.6213711922f,
        5 => 0.0254f * 1.943844492f,
        _ => 1.0f, // 1 / default: qu/s
    };

    /// <summary>QC <c>GetSpeedUnit</c> (client/main.qc): unit label.</summary>
    private static string UnitLabel(int unit) => unit switch
    {
        2 => "m/s",
        3 => "km/h",
        4 => "mph",
        5 => "knots",
        _ => "qu/s",
    };

    // ---- live cvar reads (shared store) with the public-property fallback, so both paths work ----

    private bool VertSpeed => CvarBool("speed_vertical") || (string.IsNullOrWhiteSpace(CvarStr("speed_vertical")) && SpeedVertical);
    private bool VertAccel => CvarBool("acceleration_vertical");
    private float SpeedMaxCvar => CvarF("speed_max", SpeedMax);
    private float AccelMaxCvar => CvarF("acceleration_max", AccelMax);
    private float AccelMaxSlick => CvarF("acceleration_max_slick", -1f);
    private bool TopSpeedOn => CvarBool("topspeed") || (string.IsNullOrWhiteSpace(CvarStr("topspeed")) && ShowTopSpeed);
    private float TopSpeedTimeCvar => CvarF("topspeed_time", TopSpeedHold);
    private bool JumpSpeedOn => CvarBool("jumpspeed");
    private float JumpSpeedTimeCvar => CvarF("jumpspeed_time", 1f);
    private bool UnitShow => CvarBool("speed_unit_show") || (string.IsNullOrWhiteSpace(CvarStr("speed_unit_show")) && ShowUnit);
    private bool SpeedColored => CvarBool("speed_colored");
    private int Progressbar => (int)CvarF("progressbar", ProgressbarBoth);
    private int TextMode => (int)CvarF("text", TextBoth);
    private float TextScale { get { float t = CvarF("text_scale", 0.7f); return t <= 0f ? 1f : Mathf.Min(t, 1f); } }
    private int ForceLayout => (int)CvarF("force_layout", LayoutAutomatic);
    private bool Flip => CvarBool("flip");
    private int BaralignCvar => (int)CvarF("baralign", BaralignLeft);
    private int AccelProgressbarMode => (int)CvarF("acceleration_progressbar_mode", 0);
    private float AccelProgressbarScaleCvar => CvarF("acceleration_progressbar_scale", 1f);
    private bool AccelProgressbarNonlinear => CvarBool("acceleration_progressbar_nonlinear");
    private float AccelMovingAverage => CvarF("acceleration_movingaverage", 1f);
    private float UpdateInterval { get { float i = CvarF("update_interval", 0.015625f); return i > 0f ? i : 0.015625f; } }
    private int Unit => (int)GlobalF("hud_speed_unit", SpeedUnit);

    private static Color GlobalColor(string name, Color fallback)
        => TryParseRgb(GlobalStr(name), out Color c) ? c : fallback;

    protected override void DrawPanel()
    {
        if (Player is null) return;
        if (Player.GetResource(ResourceType.Health) <= 0f) return; // QC: nothing to show while dead/observing

        // Velocity can momentarily go NaN/Inf in pathological physics states (stuck/blowup recovery). Left
        // unsanitized it poisons every derived value (speed, accel, _topSpeed, _accAvg) and sprays non-finite
        // geometry into the renderer (mirrors the StrafeHudPanel float.IsFinite hardening). Drop to zero instead.
        NVec3 vel = Player.Velocity;
        if (!float.IsFinite(vel.X) || !float.IsFinite(vel.Y) || !float.IsFinite(vel.Z))
            vel = NVec3.Zero;
        int unit = Unit;
        float conv = UnitFactor(unit);
        double now = CurrentTime();
        bool vertSpeed = VertSpeed;

        // QC accel_progressbar_scale: only active when progressbar drawn AND scale > 1 (allows the accel bar to
        // overflow the panel bounds).
        float accelProgressbarScale = 0f;
        if (Progressbar != ProgressbarNone && AccelProgressbarScaleCvar > 1f)
            accelProgressbarScale = AccelProgressbarScaleCvar;

        // ---- compute speed (QC) ----
        float maxSpeed = Mathf.Floor(SpeedMaxCvar * conv + 0.5f);
        if (maxSpeed < 1f) maxSpeed = 1f;
        float speed2d = Mathf.Floor(Length2D(vel) * conv + 0.5f);
        float speed = vertSpeed ? Mathf.Floor(vel.Length() * conv + 0.5f) : speed2d;

        // ---- compute acceleration (QC finite-difference → fraction of gravity, optional moving-average) ----
        float accel = 0f;
        if (_haveAccPrev)
        {
            float f = (float)(now - _accPrevTime);
            float dv = VertAccel
                ? vel.Length() - _accPrevVel.Length()
                : Length2D(vel) - Length2D(_accPrevVel);
            accel = dv * (1f / Mathf.Max(0.0001f, f)) * (float)Accel2Grav;

            float ma = AccelMovingAverage;
            if (ma > 0f)
            {
                f = Mathf.Clamp(f * 10f / ma, 0f, 1f);
                _accAvg = _accAvg * (1f - f) + accel * f;
                accel = _accAvg;
            }
        }
        _accPrevVel = vel;
        _accPrevTime = now;
        _haveAccPrev = true;

        // ---- compute layout (QC compute layout block) ----
        // Panel content rect: QC pos += '1 1 0'*pad; size -= '2 2 0'*pad.
        float pad = Cfg.Padding;
        var panelPos = new Vector2(pad, pad);
        var panelSize = new Vector2(Size2.X - pad * 2f, Size2.Y - pad * 2f);
        if (panelSize.X <= 0f || panelSize.Y <= 0f) return;

        float panelAr = panelSize.X / Mathf.Max(panelSize.Y, 1f);
        var speedOffset = Vector2.Zero;
        var accelOffset = Vector2.Zero;
        bool horizontal = ForceLayout == LayoutHorizontal
            || (ForceLayout != LayoutVertical && panelAr >= 5f && accelProgressbarScale == 0f);
        if (horizontal)
        {
            panelSize.X *= 0.5f;
            if (Flip) speedOffset.X = panelSize.X; else accelOffset.X = panelSize.X;
        }
        else // speed and accel stacked vertically (vvv) rather than side by side (^^^)
        {
            panelSize.Y *= 0.5f;
            if (Flip) speedOffset.Y = panelSize.Y; else accelOffset.Y = panelSize.Y;
        }

        // ---- baralign (QC baralign block) ----
        int baralign = BaralignCvar;
        int speedBaralign, accelBaralign;
        if (baralign == BaralignRight)
            accelBaralign = speedBaralign = 1;
        else if (baralign == BaralignCenter)
            accelBaralign = speedBaralign = 2;
        else if (Flip)
        {
            accelBaralign = baralign == BaralignOnlyLeft ? 1 : 0;
            speedBaralign = baralign == BaralignOnlyRight ? 1 : 0;
        }
        else
        {
            speedBaralign = baralign == BaralignOnlyLeft ? 1 : 0;
            accelBaralign = baralign == BaralignOnlyRight ? 1 : 0;
        }
        if (AccelProgressbarMode == 0)
            accelBaralign = 3; // override: acceleration bar is always centered on zero

        DrawBackground();

        float pbAlpha = GlobalF("hud_progressbar_alpha", 0.6f) * LiveFgAlpha;
        Color speedColor = GlobalColor("hud_progressbar_speed_color", new Color(0.77f, 0.67f, 0f));
        Color accelColor = GlobalColor("hud_progressbar_acceleration_color", new Color(0.2f, 0.65f, 0.93f));
        Color accelNegColor = GlobalColor("hud_progressbar_acceleration_neg_color", new Color(0.86f, 0.35f, 0f));

        int progressbar = Progressbar;
        int textMode = TextMode;
        float textScale = TextScale;

        const float speedSize = 0.75f;
        // text_bits: BIT(0)=topspeed, BIT(1)=jumpspeed, BIT(2)=speed unit. Ordered by decreasing priority —
        // the speed unit isn't drawn if the other two are; topspeed takes the lower spot over jumpspeed.
        int textBits = 0;

        // ---- draw speed progressbar ----
        if (speed > 0f && (progressbar == ProgressbarBoth || progressbar == ProgressbarSpeed))
            DrawSkinProgressBar(panelPos + speedOffset, panelSize, "progressbar", speed / maxSpeed,
                speedBaralign, speedColor, pbAlpha);

        // ---- accel_max (QC: slick-aware selection) ----
        bool realOnGround = Player.OnGround;
        bool onground = realOnGround && !JumpHeld;
        bool realOnSlick = onground && OnSlick;
        float accelMax = (AccelMaxSlick != -1f
            && ((AllSlick && realOnGround) || realOnSlick)
            && SlickAccelExceeds)
            ? AccelMaxSlick
            : AccelMaxCvar;
        if (accelMax == 0f) accelMax = AccelMaxCvar;
        if (accelMax == 0f) accelMax = 1f;

        // ---- compute speed text color (QC speed_text_color block) ----
        Color speedTextColor = new(1f, 1f, 1f, LiveFgAlpha);
        if ((textMode == TextBoth || textMode == TextSpeed) && SpeedColored)
        {
            // QC: width the accel/decel bar WOULD be (so the text only colors if something shows on the bar),
            // which also dodges flicker from tiny float errors.
            float accelPixelsDrawn = accel / accelMax * accelProgressbarScale * panelSize.X;
            if (accelPixelsDrawn > 1f) speedTextColor = WithAlpha(accelColor, LiveFgAlpha);
            else if (accelPixelsDrawn < -1f) speedTextColor = WithAlpha(accelNegColor, LiveFgAlpha);
        }

        // ---- jumpspeed (QC) ----
        float jumpSpeedF = 0f;
        if (JumpSpeedOn && (textMode == TextBoth || textMode == TextSpeed))
        {
            textBits |= 1 << 1;
            if (_haveJumpPrev && Mathf.RoundToInt(vel.Z) > Mathf.RoundToInt(_prevVelZ))
            {
                // NOTE (QC): also fires on landing, swimming, ramps, jetpack, bouncepads — anywhere vel.z rises.
                _jumpSpeed = _prevSpeed2d;
                _jumpSpeedTime = now;
            }
            _prevVelZ = vel.Z;
            _haveJumpPrev = true;
            float tf = (float)((now - _jumpSpeedTime) / Mathf.Max(1f, JumpSpeedTimeCvar));
            jumpSpeedF = tf > 1f ? 0f : Mathf.Cos(tf * Mathf.Pi * 0.5f);
        }
        else
        {
            _prevVelZ = vel.Z;
            _haveJumpPrev = true;
        }
        _prevSpeed2d = speed2d;

        // ---- topspeed (QC: peak + fade + bar marker) ----
        float topSpeedF = 0f;
        if (TopSpeedOn && (textMode == TextBoth || textMode == TextSpeed))
        {
            textBits |= 1 << 0;
            if (speed >= _topSpeed)
            {
                _topSpeed = speed;
                _topSpeedTime = now;
            }
            if (_topSpeed == 0f)
                topSpeedF = 0f;
            else
            {
                float tf = (float)((now - _topSpeedTime) / Mathf.Max(1f, TopSpeedTimeCvar));
                topSpeedF = tf > 1f ? 0f : Mathf.Cos(tf * Mathf.Pi * 0.5f);
            }

            // top-speed progressbar peak marker(s)
            if (topSpeedF > 0f)
            {
                if (speed < _topSpeed
                    && (progressbar == ProgressbarBoth || progressbar == ProgressbarSpeed))
                {
                    float peakOffsetX;
                    if (speedBaralign == 0)
                        peakOffsetX = Mathf.Min(_topSpeed, maxSpeed) / maxSpeed * panelSize.X;
                    else if (speedBaralign == 1)
                        peakOffsetX = (1f - Mathf.Min(_topSpeed, maxSpeed) / maxSpeed) * panelSize.X;
                    else // speedBaralign == 2 (center)
                        peakOffsetX = Mathf.Min(_topSpeed, maxSpeed) / maxSpeed * panelSize.X * 0.5f;
                    float peakW = Mathf.Floor(panelSize.X * 0.01f + 1.5f);
                    float peakH = panelSize.Y;
                    Color peak = WithAlpha(speedColor, topSpeedF * pbAlpha);
                    Vector2 baseAt = panelPos + speedOffset;
                    if (speedBaralign == 2) // two peaks, mirrored across the center
                    {
                        DrawRect(new Rect2(baseAt.X + 0.5f * panelSize.X + peakOffsetX - peakW, baseAt.Y, peakW, peakH), peak);
                        DrawRect(new Rect2(baseAt.X + 0.5f * panelSize.X - peakOffsetX + peakW, baseAt.Y, peakW, peakH), peak);
                    }
                    else
                        DrawRect(new Rect2(baseAt.X + peakOffsetX - peakW, baseAt.Y, peakW, peakH), peak);
                }
            }
            else
                _topSpeed = 0f;
        }

        // ---- discretize the on-screen numbers (QC physics_update_time block) ----
        if (now > _physicsUpdateTime)
        {
            _discreteSpeed = speed;
            _discreteTopSpeed = _topSpeed;
            _discreteAccel = accel;
            // QC workaround: ftos_decimals returns a negative zero for tiny negatives.
            if (_discreteAccel > -0.005f && _discreteAccel < 0f) _discreteAccel = 0f;
            _physicsUpdateTime += UpdateInterval;
            if (_physicsUpdateTime < now) _physicsUpdateTime = now + UpdateInterval;
        }

        // ---- draw speed text ----
        if (textMode == TextBoth || textMode == TextSpeed)
        {
            float tw = panelSize.X * speedSize;
            float th = panelSize.Y * textScale;
            float tx = speedBaralign != 0 ? panelSize.X * (1f - speedSize) : 0f;
            float ty = (panelSize.Y - th) * 0.5f;
            DrawAspectString(panelPos + speedOffset + new Vector2(tx, ty), new Vector2(tw, th),
                Mathf.RoundToInt(_discreteSpeed).ToString(), speedTextColor);

            if (UnitShow) textBits |= 1 << 2;
        }

        // ---- draw acceleration progressbar ----
        if (accel != 0f && (progressbar == ProgressbarBoth || progressbar == ProgressbarAccel))
        {
            Color barColor = accel < 0f ? accelNegColor : accelColor;
            float f = accel / accelMax;
            if (AccelProgressbarNonlinear)
                f = f >= 0f ? Mathf.Sqrt(f) : -Mathf.Sqrt(-f);

            Vector2 tmpSize, tmpOffset;
            if (accelProgressbarScale > 0f) // allow the bar to overflow the panel bounds
            {
                tmpSize = new Vector2(accelProgressbarScale * panelSize.X, panelSize.Y);
                float ox = accelBaralign == 1 ? panelSize.X - tmpSize.X
                    : (accelBaralign == 2 || accelBaralign == 3) ? (panelSize.X - tmpSize.X) * 0.5f
                    : 0f;
                tmpOffset = new Vector2(ox, 0f);
            }
            else
            {
                tmpSize = panelSize;
                tmpOffset = Vector2.Zero;
            }

            DrawSkinProgressBar(panelPos + accelOffset + tmpOffset, tmpSize, "accelbar", f,
                accelBaralign, barColor, pbAlpha);
        }

        // ---- draw acceleration text ----
        if (textMode == TextBoth || textMode == TextAccel)
        {
            float th = panelSize.Y * textScale;
            float ty = (panelSize.Y - th) * 0.5f;
            DrawAspectString(panelPos + accelOffset + new Vector2(0f, ty), new Vector2(panelSize.X, th),
                _discreteAccel.ToString("0.00", CultureInfo.InvariantCulture) + "g", new Color(1f, 1f, 1f, LiveFgAlpha));
        }

        // ---- draw the secondary texts: speed unit / jumpspeed / topspeed (QC text_bits priority block) ----
        DrawSecondaryTexts(panelPos + speedOffset, panelSize, speedBaralign, textScale, textBits,
            unit, _jumpSpeed, jumpSpeedF, _discreteTopSpeed, topSpeedF);
    }

    /// <summary>QC tail block: lay out the speed-unit, jumpspeed and topspeed texts in the side column of the
    /// speed cell, honoring the BIT(0/1/2) priority (can't draw all 3 at once; speed unit is lowest).</summary>
    private void DrawSecondaryTexts(Vector2 speedPos, Vector2 panelSize, int speedBaralign, float textScale,
        int textBits, int unit, float jumpSpeed, float jumpSpeedF, float topSpeed, float topSpeedF)
    {
        const float speedSize = 0.75f;
        float colW = panelSize.X * (1f - speedSize);
        float colX = speedBaralign != 0 ? 0f : panelSize.X * speedSize;
        float topSpeedY = 0f, jumpSpeedY = 0f, mainTextSize;
        const int bitTop = 1 << 0, bitJump = 1 << 1, bitUnit = 1 << 2, bitsAll = bitTop | bitJump | bitUnit;

        // draw speed unit text
        if ((textBits & bitUnit) != 0 && textBits != bitsAll)
        {
            float offY;
            if (textBits == bitUnit) // it's the only text — make it large + centered
            {
                mainTextSize = 1f - 0.8f; // main text small so the unit (non-main) is large
                offY = panelSize.Y * mainTextSize * 0.5f;
            }
            else
            {
                mainTextSize = 0.6f; // main text slightly larger (more important)
                offY = 0f;
                topSpeedY = panelSize.Y * (1f - mainTextSize);
                jumpSpeedY = topSpeedY;
            }
            float uh = panelSize.Y * (1f - mainTextSize) * textScale;
            offY += (panelSize.Y * (1f - mainTextSize) - uh) * 0.5f;
            DrawAspectString(speedPos + new Vector2(colX, offY), new Vector2(colW, uh),
                UnitLabel(unit), new Color(1f, 1f, 1f, LiveFgAlpha));
        }
        else if (textBits == bitTop || textBits == bitJump) // only one text — large + centered
        {
            mainTextSize = 0.8f;
            topSpeedY = panelSize.Y * (1f - mainTextSize) * 0.5f;
            jumpSpeedY = topSpeedY;
        }
        else // both topspeed and jumpspeed are drawn
        {
            mainTextSize = 0.5f; // equal size
            topSpeedY = panelSize.Y * (1f - mainTextSize); // topspeed in the lower slot
            jumpSpeedY = 0f;                               // jumpspeed in the upper slot
        }

        float mh = panelSize.Y * mainTextSize * textScale;

        // draw jumpspeed text
        if ((textBits & bitJump) != 0 && jumpSpeedF > 0f)
        {
            jumpSpeedY += (panelSize.Y * mainTextSize - mh) * 0.5f;
            DrawAspectString(speedPos + new Vector2(colX, jumpSpeedY), new Vector2(colW, mh),
                Mathf.RoundToInt(jumpSpeed).ToString(), new Color(0f, 1f, 0f, jumpSpeedF * LiveFgAlpha));
        }
        // draw topspeed text
        if ((textBits & bitTop) != 0 && topSpeedF > 0f)
        {
            topSpeedY += (panelSize.Y * mainTextSize - mh) * 0.5f;
            DrawAspectString(speedPos + new Vector2(colX, topSpeedY), new Vector2(colW, mh),
                Mathf.RoundToInt(topSpeed).ToString(), new Color(1f, 0f, 0f, topSpeedF * LiveFgAlpha));
        }
    }

    // =================================================================================================
    //  Draw primitives
    // =================================================================================================

    /// <summary>Port of QC <c>HUD_Panel_DrawProgressBar</c> (horizontal path) — fill the bar per
    /// <paramref name="baralign"/> using the skin art <paramref name="art"/> (<c>progressbar</c> /
    /// <c>accelbar</c>) tinted by <paramref name="color"/>. Faithfully reproduces all four QC alignments:
    /// 0 = left, 1 = right, 2 = symmetric-centered (the value must be non-negative — negatives are dropped,
    /// like the QC <c>length_ratio &lt; 0 → return</c>), 3 = SIGNED-centered (the accel bar: positive fills the
    /// right half, negative the left half — the only mode that accepts a negative ratio). Falls back to a plain
    /// rect when the art is missing so the bar is never invisible.</summary>
    private void DrawSkinProgressBar(Vector2 origin, Vector2 size, string art, float lengthRatio,
        int baralign, Color color, float alpha)
    {
        // Delegate to the shared HudPanel.DrawProgressBar primitive (the faithful QC HUD_Panel_DrawProgressBar
        // port — all four baralign modes + skin art + fallback). This panel always draws horizontal bars.
        DrawProgressBar(new Rect2(origin, size), art, lengthRatio, vertical: false, baralign, color, alpha);

        // A faint center tick for the signed (acceleration) bar so zero is readable even at flat accel
        // (a port-extra readability aid, drawn only for baralign 3 and only when the bar would render).
        if (baralign == 3 && alpha > 0f && size.X > 0f && size.Y > 0f && float.IsFinite(lengthRatio))
        {
            float cx = origin.X + size.X * 0.5f;
            DrawRect(new Rect2(cx - 0.5f, origin.Y, 1f, size.Y), new Color(1f, 1f, 1f, alpha * 0.4f));
        }
    }

    /// <summary>QC <c>drawstring_aspect</c>: draw a string fit into <paramref name="box"/> (a 1-line cell),
    /// horizontally centered, scaling the font down to fit the box width/height — the modern analogue of the
    /// engine's aspect-fit string draw.</summary>
    private void DrawAspectString(Vector2 pos, Vector2 box, string text, Color color)
    {
        if (string.IsNullOrEmpty(text) || box.X <= 0f || box.Y <= 0f) return;
        // Guard non-finite geometry: FloorToInt(NaN)/centering on a NaN pos would push garbage into DrawString.
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(box.X) || !float.IsFinite(box.Y)) return;
        int size = Mathf.Max(8, Mathf.FloorToInt(box.Y));
        float w = MeasureText(text, size);
        if (w > box.X && w > 0f)
            size = Mathf.Max(8, Mathf.FloorToInt(size * box.X / w));
        float ty = pos.Y + (box.Y - size) * 0.5f;
        DrawTextCentered(new Vector2(pos.X, ty), box.X, text, color, size);
    }

    /// <summary>QC <c>vlen(vec2(v))</c>: planar (XY) speed.</summary>
    private static float Length2D(NVec3 v) => Mathf.Sqrt(v.X * v.X + v.Y * v.Y);

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, Mathf.Clamp(a, 0f, 1f));
}
