using Godot;
using XonoticGodot.Common.Framework;   // MoveFilter
using XonoticGodot.Common.Math;        // QMath, Coords
using XonoticGodot.Common.Services;    // Api, TraceResult, PointContents
using NVec3 = System.Numerics.Vector3;
using GVec3 = Godot.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Port of qcsrc/client/view.qc (CSQC_UpdateView: zoom, eventchase, FOV) — the SINGLE first-person view
/// driver shared by BOTH the local <see cref="XonoticGodot.Game.PlayerController"/> (the GameDemo path) and the
/// networked <see cref="XonoticGodot.Game.Net.NetGame"/> (the menu Start / Create-Game play path).
///
/// Xonotic has ONE first-person path; the port had drifted into two partial re-implementations (the FULL
/// view logic in PlayerController + a STRIPPED copy in NetGame). This factors the view subsystem out so both
/// hosts feed it the same code: the zoom state machine (<c>GetCurrentFov</c>'s <c>current_viewzoom</c> integration
/// + <c>current_zoomfraction</c> + the spawn-zoom latch + <c>setsensitivityscale</c>), the FOV math with the
/// <c>*0.75</c> 4:3 aspect normalization and live zoom, the death/event-chase camera
/// (<c>View_EventChase</c> / <c>WantEventchase</c>), the exact <c>AngleVectors</c> → <c>Coords.ToGodot</c> columns →
/// <c>Basis(right, up, -forward)</c> camera placement (a negated-yaw Euler silently flips handedness, so this is
/// the proven vector path), and the eye-contents sample at the FINAL render origin (<c>HUD_Contents</c>).
///
/// <para>It is a plain helper (not a <see cref="Node"/>): the host owns the <see cref="Camera3D"/> and the
/// player/predicted state, and each frame hands this a small <see cref="ViewState"/> snapshot (origin/velocity/
/// view angles/dead) — NOT an <see cref="Entity"/> — so an Entity-backed source (PlayerController) and a
/// ClientNet-predicted source (NetGame) both plug in.</para>
/// </summary>
public sealed class FirstPersonView
{
    /// <summary>
    /// The per-frame view-state source the shared view reads (the abstraction over an owned
    /// <see cref="Entity"/> vs ClientNet-predicted state). Mirrors the inputs <c>CSQC_UpdateView</c> drives the
    /// view from: the RAW player origin (Quake; the eye/chase pivot is derived from it), the velocity (Quake;
    /// the <c>cl_eventchase_death == 2</c> settle gate reads it), whether the player is dead
    /// (<c>STAT(HEALTH) &lt;= 0</c>), and the view angles (deg, Quake pitch/yaw/roll).
    /// </summary>
    public struct ViewState
    {
        /// <summary>RAW player origin in Quake space (the eye = origin + (0,0,EyeHeight); the chase pivot is
        /// this origin lifted by cl_eventchase_viewoffset — NOT the eye, view.qc:807,823-828).</summary>
        public NVec3 OriginQuake;

        /// <summary>Player velocity in Quake space — the cl_eventchase_death==2 gate waits for it to settle to 0.</summary>
        public NVec3 VelocityQuake;

        /// <summary>View angles in DEGREES, Quake convention (X = pitch down-positive, Y = yaw, Z = roll).</summary>
        public NVec3 ViewAnglesQuake;

        /// <summary>True while the local player is dead (drives the death fade + the event-chase cam, QC
        /// STAT(HEALTH) &lt;= 0). The host decides the predicate: GameDemo uses Health&lt;=0 &amp;&amp; MaxHealth&gt;0
        /// (no fresh-spawn-at-0 false death); the net path has no MaxHealth so it is Health&lt;=0.</summary>
        public bool IsDead;

        /// <summary>Live eye height above the origin, Quake units (QC STAT(PL_VIEW_OFS) / PL_CROUCH_VIEW_OFS — drops
        /// from ~35 to ~20 while crouched, set by PlayerPhysics.UpdateCrouch). 0 = unset → fall back to the static
        /// <see cref="EyeHeight"/> (e.g. a placement call before the carrier/player view offset is known).</summary>
        public float EyeHeightZ;

        /// <summary>True while the local player is Freeze-Tag frozen (QC STAT(FROZEN)). With cl_eventchase_frozen set
        /// this engages the event-chase third-person camera (QC MUTATOR_HOOKFUNCTION(cl_ft, WantEventchase)).</summary>
        public bool IsFrozen;

        /// <summary>True while the local player is in Nexball and is NOT carrying the ball (QC
        /// <c>cl_nexball.qc:WantEventchase</c> — <c>cl_eventchase_nexball</c> engages the third-person cam when the
        /// player is a nexball participant but not the current ball-carrier). Set each frame by
        /// <see cref="XonoticGodot.Game.Net.NetGame"/> from the decoded NexBall status block.</summary>
        public bool IsNexBallNonCarrier;

        /// <summary>The view-origin recoil kick in Quake space (QC <c>view_punchvector</c>, decayed 30u/s). Added to
        /// the rendered eye in FIRST PERSON ONLY (<c>vieworg += view_punchvector</c>, cl_player.qc:570 — inside the
        /// non-chase branch), so it kicks the camera without skewing the aim. Suppressed while chase is active.</summary>
        public NVec3 PunchOriginQuake;

        /// <summary>True while the player is standing on the ground (QC <c>IS_ONGROUND</c>). Drives the horizontal
        /// view-bob smooth ramp (<c>cl_bob2</c>) and the fall-bob swing trigger (<c>cl_bobfall</c>).</summary>
        public bool OnGround;

        /// <summary>True while the jump button is held this frame (QC <c>input_buttons &amp; BIT(1)</c>). Blocks the
        /// horizontal-bob smooth ramp so bunny-hopping doesn't twitch the view (cl_player.qc:380).</summary>
        public bool JumpHeld;
    }

    /// <summary>
    /// Chase-camera mode the view runs in (QC <c>chase_active</c> / spectator cam). <see cref="ChaseMode.None"/>
    /// is the normal first-person eye; the others pull the camera back from the player along the view, tracing
    /// against the world so it doesn't clip. On local death the event-chase engages automatically regardless of
    /// this (QC <c>cl_eventchase_death</c>).
    /// </summary>
    public enum ChaseMode { None, Chase, SpectatorFollow, Vehicle }

    /// <summary>The host-requested chase mode (QC user <c>chase_active</c> / spectator cam). Default first-person eye.</summary>
    public ChaseMode CameraMode { get; set; } = ChaseMode.None;

    /// <summary>True while the local client is spectating/following another player (QC <c>spectatee_status &gt; 0</c>).
    /// Gates the <c>chase_front</c> frontal selfie cam, which Base allows ONLY while spectating
    /// (<c>CSQCPlayer_ApplyChase</c>: <c>if (autocvar_chase_front &amp;&amp; spectatee_status)</c>). The host sets it each
    /// frame from the follow state so the spectator third-person camera can flip to the frontal view.</summary>
    public bool Spectating { get; set; }

    /// <summary>True while the local player is seated in a vehicle (QC <c>hud != HUD_NORMAL</c> / the
    /// TE_CSQC_VEHICLESETUP cockpit). Set each frame by <see cref="XonoticGodot.Game.Net.NetGame"/> from the
    /// decoded <c>VehicleViewState.IsActive</c>. With <c>cl_eventchase_vehicle</c> set this engages the vehicle
    /// chase/cockpit camera (QC <c>WantEventchase</c> vehicle branch, <see cref="ChaseCamera.ApplyVehicle"/>);
    /// when off the view stays the seated first-person eye (the seated origin already sits in the cockpit, ViewOfs
    /// is 0). The transition true→false resets the smoothed chase distance so the next board restarts the pull-back.</summary>
    public bool InVehicle
    {
        get => _inVehicle;
        set
        {
            if (_inVehicle && !value)
                _eventChase = default;   // restart the smoothed pull-back distance on the next board (QC resets it)
            _inVehicle = value;
        }
    }
    private bool _inVehicle;

    /// <summary>Set each frame by the host from the <c>+zoom</c> bind's held state (QC <c>button_zoom</c>). The
    /// host is responsible for suppressing it when dead / paused / the console is open, so the view zooms out on
    /// death (QC <c>cl_unpress_zoom_on_death</c>).</summary>
    public bool ZoomHeld { get; set; }

    /// <summary>Eye height above the entity origin, in Quake units (Xonotic PL_VIEW_OFS ~ '0 0 35').</summary>
    public float EyeHeight { get; set; } = 35f;

    /// <summary>Unzoomed vertical field of view base in degrees (Xonotic `fov 100`). The rendered fov is this
    /// scaled by the live zoom (GetCurrentFov); the `fov` cvar overrides it when set.</summary>
    public float BaseFov { get; set; } = 100f;

    /// <summary>The live zoom fraction, 0 = no zoom (full fov) .. 1 = fully zoomed (QC <c>current_zoomfraction</c>).</summary>
    public float ZoomFraction { get; private set; }

    /// <summary>The look-sensitivity multiplier the host applies to mouse-look while zoomed (QC
    /// <c>setsensitivityscale</c>: <c>current_viewzoom ** (1 - cl_zoomsensitivity)</c>). 1 when unzoomed.</summary>
    public float SensitivityScale { get; private set; } = 1f;

    /// <summary>The SUPERCONTENTS bitmask sampled at the FINAL render origin this frame (QC
    /// <c>pointcontents(view_origin)</c>, view.qc:1176) — the host feeds it to the liquid screen-tint. 0 in air.</summary>
    public int EyeContents { get; private set; }

    /// <summary>True while the event/death chase camera is engaged this frame (so the host can hide the
    /// first-person viewmodel while chase is active — view.qc viewmodel_draw masks the gun when chase_active).</summary>
    public bool ChaseActive { get; private set; }

    /// <summary>True when the eye straddles a warpzone seam this frame (QC <c>WarpZone_FixView</c> rotated the view).
    /// The host uses it to hide the LOCAL exterior player model (so the player doesn't see the back of their own
    /// head poking through the seam) and the matching near-clip push-out (<see cref="WarpzoneFixView.NearClipMultiplier"/>)
    /// is applied to the camera. Listen-host / demo only — IDLE on a pure remote client where the zone transforms
    /// aren't networked (no <c>WarpzoneTrace.AmbientManager</c>).</summary>
    public bool InsideWarpzone { get; private set; }

    // --- the FINAL rendered view this frame (QC view_origin / view_forward) — the authoritative eye/forward the
    // Phase-2 crosshair-chase consumer reads to trace true-aim from exactly where the camera ended up (post chase
    // pull-back, post view-lock). Stashed at the tail of UpdateCamera once camQuake + viewAngles are resolved. ---
    private NVec3 _renderedEyeQuake;
    private NVec3 _renderedForwardQuake;
    private GVec3 _renderedEyeGodot;

    /// <summary>The FINAL rendered eye/camera origin in Quake space (QC <c>view_origin</c>) — the chase pull-back
    /// origin during a chase, the eye otherwise, after view-lock. The crosshair true-aim trace starts here.</summary>
    public NVec3 RenderedEyeQuake => _renderedEyeQuake;

    /// <summary>The FINAL rendered view forward in Quake space (QC <c>view_forward</c>) — derived from the resolved
    /// view angles (post chase/overhead/front rewrite, death-tilt, roll, idle-wave, view-lock).</summary>
    public NVec3 RenderedForwardQuake => _renderedForwardQuake;

    /// <summary>The FINAL rendered camera origin in Godot space (= <c>camera.GlobalPosition</c>) — the same view
    /// origin as <see cref="RenderedEyeQuake"/>, in engine coords, for screen-projecting the crosshair impact.</summary>
    public GVec3 RenderedEyeGodot => _renderedEyeGodot;

    // --- zoom state (view.qc GetCurrentFov: current_viewzoom drives the rendered fov) ---
    // current_viewzoom in (0,1]: 1 = unzoomed, 1/zoomfactor = fully zoomed. Lerped each frame toward the target.
    private float _currentViewZoom = 1f;

    // Spawn-zoom (view.qc:512-520 + spawnpoints.qc:82-86): on local spawn current_viewzoom is snapped to
    // 1/cl_spawnzoom_factor and zoomin_effect latches; GetCurrentFov then eases the zoom back out to 1, until
    // it arrives (clears the latch) or the player manually zooms (which cancels it via IsZooming → zoomin_effect=0).
    private bool _zoomInEffect;

    // QC view.qc MAX_ZOOMFACTOR.
    private const float MaxZoomFactor = 30f;

    // --- death / event-chase camera state (view.qc View_EventChase: eventchase_current_distance) ---
    // The smoothed pull-back distance + the run latch now live in a ChaseCamera.EventState that ApplyEvent
    // owns and mutates (replacing the old _eventChaseDistance/_eventChaseRunning fields).
    private ChaseCamera.EventState _eventChase;

    // --- view-effect clock (QC `time`): accumulated from dt so the sin-cycle bob / idle-wave advance in real time
    // even though FirstPersonView is a plain helper with no engine clock of its own. ---
    private float _viewTime;

    // --- bob / fall-bob / death-tilt state (cl_player.qc CSQCPlayer_ApplyBobbing / ApplyDeathTilt) ---
    private float _bob2Smooth;    // QC bob2_smooth: 1 while grounded, decays to 0 when airborne (smooths cl_bob2 out)
    private float _bobfallSwing;  // QC bobfall_swing
    private float _bobfallSpeed;  // QC bobfall_speed
    private float _deathTime = -1f; // QC death_time analogue: _viewTime when death began (-1 = alive)
    private bool _wasDead;          // previous-frame dead state, to detect the death edge for _deathTime

    // --- velocity-zoom averaged speed (QC GetCurrentFov:487 `avgspeed`) ---
    private float _avgSpeed;

    // --- zoom-scroll (QC view.qc ZoomScroll): the mousewheel-driven zoom factor while zoomed ---
    private float _zoomScrollFactor;        // QC zoomscroll_factor (the eased current factor)
    private float _zoomScrollFactorTarget;  // QC zoomscroll_factor_target (the wheel target)

    // --- zoomscript auto-zoom (QC View_CheckButtonStatus: autocvar_fov <= 59.5 -> +button9) ---
    private bool _zoomScriptCaught;
    /// <summary>True while the low-fov auto-zoom (QC <c>zoomscript_caught</c>, set when <c>fov &lt;= 59.5</c>) is
    /// engaged — the reticle uses the full-alpha (1.0) branch then, not the fade (crosshair.qc:704).</summary>
    public bool ZoomScriptCaught => _zoomScriptCaught;

    /// <summary>
    /// Arm the spawn-zoom effect (QC spawnpoints.qc:82-86, the local-spawn branch). Snaps
    /// <c>current_viewzoom</c> to <c>1/cl_spawnzoom_factor</c> and latches the spawn-zoom effect; the next
    /// <see cref="UpdateView"/> eases the zoom back out to 1. No-op when <c>cl_spawnzoom</c> is off. Called by the
    /// host on the (re)spawn edge — GameDemo from <c>PlayerController.Teleport</c>, NetGame from a Health 0→&gt;0 edge.
    /// </summary>
    public void TriggerSpawnZoom()
    {
        if (Cvar("cl_spawnzoom", 1f) == 0f)
            return;
        // spawnpoints.qc:85 seeds current_viewzoom = 1/bound(1, cl_spawnzoom_factor, 16) (16, not MAX_ZOOMFACTOR).
        float spawnzoomfactor = Mathf.Clamp(Cvar("cl_spawnzoom_factor", 2f), 1f, 16f);
        _zoomInEffect = true;
        _currentViewZoom = 1f / spawnzoomfactor;
    }

    /// <summary>
    /// The CSQC_UpdateView per-frame view step (view.qc): step the zoom toward its target, place + orient the
    /// camera (with the event-chase pull-back on death / third-person), apply the live zoom to the rendered fov,
    /// and sample the eye contents at the FINAL render origin. The host calls this each physics frame after
    /// setting <see cref="ZoomHeld"/> / <see cref="CameraMode"/>, then reads back <see cref="ZoomFraction"/> /
    /// <see cref="EyeContents"/> / <see cref="SensitivityScale"/> / <see cref="ChaseActive"/>.
    /// </summary>
    public void UpdateView(Camera3D camera, in ViewState st, float dt)
    {
        if (camera is null)
            return;

        _lastDt = dt;
        _viewTime += dt;
        UpdateZoom(dt);
        UpdateCamera(camera, st);
    }

    /// <summary>
    /// QC <c>View_InputEvent</c> / <c>ZoomScroll</c> (view.qc:450-485): the host calls this on a mousewheel
    /// up/down event. Only takes effect while <c>cl_zoomscroll</c> is on and the player is actively zooming
    /// (held +zoom / weapon zoom). Scrolls the zoom factor target by <c>1 + min(|cl_zoomscroll_scale|, 1)</c>,
    /// clamped to <c>[1, MAX_ZOOMFACTOR]</c>; the zoom then eases toward it in <see cref="UpdateZoom"/>.
    /// <paramref name="wheelUp"/> = K_MWHEELUP (zoom in, before the scale-sign flip).
    /// </summary>
    public void NotifyZoomScroll(bool wheelUp)
    {
        float scale = Cvar("cl_zoomscroll_scale", 0.2f);
        if (Cvar("cl_zoomscroll", 1f) == 0f || scale == 0f)
            return;            // zoom scroll disabled
        if (!ZoomHeld)
            return;            // QC IsZooming(true): only while actively zooming

        bool zoomin = wheelUp;
        if (scale < 0f) zoomin = !zoomin;
        // biggest change allowed is +100%
        float zoomscrollScale = 1f + Mathf.Min(Mathf.Abs(scale), 1f);
        if (_zoomScrollFactorTarget <= 0f) // seed before the first frame initialises it
            _zoomScrollFactorTarget = ResolveZoomFactor();
        if (zoomin)
            _zoomScrollFactorTarget = Mathf.Min(MaxZoomFactor, _zoomScrollFactorTarget * zoomscrollScale);
        else
            _zoomScrollFactorTarget = Mathf.Max(1f, _zoomScrollFactorTarget / zoomscrollScale);
    }

    // QC GetCurrentFov: cl_zoomfactor clamped 1..MAX_ZOOMFACTOR else 2.5.
    private static float ResolveZoomFactor()
    {
        float zoomfactor = Cvar("cl_zoomfactor", 2.5f);
        if (zoomfactor < 1f || zoomfactor > MaxZoomFactor) zoomfactor = 2.5f;
        return zoomfactor;
    }

    /// <summary>
    /// Step the zoom toward its target each frame — the port of view.qc <c>GetCurrentFov</c>'s
    /// <c>current_viewzoom</c> integration. Holding the zoom button (<see cref="ZoomHeld"/>) lerps the zoom IN at
    /// <c>cl_zoomspeed</c> toward <c>1/cl_zoomfactor</c>; releasing lerps back OUT to 1. Negative
    /// <c>cl_zoomspeed</c> snaps instantly. The rendered fov + look sensitivity follow in <see cref="UpdateCamera"/>.
    /// </summary>
    private void UpdateZoom(float dt)
    {
        // Zoomscript auto-zoom (QC View_CheckButtonStatus, view.qc:1492-1507): a very low fov (autocvar_fov <= 59.5)
        // auto-engages +button9 (zoomscript_caught), which folds into the zoom exactly like holding +zoom. Latch the
        // caught flag (for the reticle's full-alpha branch) and treat it as held-zoom for the integration below.
        float fovForScript = Cvar("fov", BaseFov);
        _zoomScriptCaught = fovForScript > 0f && fovForScript <= 59.5f;

        bool zoomdir = ZoomHeld || _zoomScriptCaught;

        // QC view.qc:505-506: manually zooming (button_zoom) cancels an in-progress spawn-zoom.
        if (zoomdir)
            _zoomInEffect = false;

        float zoomfactor = Cvar("cl_zoomfactor", 2.5f);
        if (zoomfactor < 1f || zoomfactor > MaxZoomFactor) zoomfactor = 2.5f;
        float zoomspeed = Cvar("cl_zoomspeed", 3.5f);
        if (zoomspeed >= 0f && (zoomspeed < 0.5f || zoomspeed > 16f)) zoomspeed = 3.5f;

        // Spawn-zoom takes precedence over the normal/instant zoom while latched (QC view.qc:512-520: the spawn
        // branch sits ABOVE the normal zoom in GetCurrentFov's else-if chain). Ease current_viewzoom back out
        // toward 1 from the 1/spawnzoom_factor it was snapped to on spawn, then clear the latch when it arrives.
        if (Cvar("cl_spawnzoom", 1f) != 0f && _zoomInEffect)
        {
            float spawnzoomfactor = Mathf.Clamp(Cvar("cl_spawnzoom_factor", 2f), 1f, MaxZoomFactor);
            float spawnzoomspeed = Cvar("cl_spawnzoom_speed", 1f);
            _currentViewZoom += spawnzoomspeed * (spawnzoomfactor - _currentViewZoom) * dt;
            _currentViewZoom = Mathf.Clamp(_currentViewZoom, 1f / spawnzoomfactor, 1f);
            if (_currentViewZoom == 1f)
                _zoomInEffect = false;
        }
        else
        {
            // Zoom-scroll (QC GetCurrentFov:523-544): while cl_zoomscroll is on, the mousewheel-driven
            // zoomscroll_factor_target eases zoomscroll_factor toward it (frametime-independent averaging), and
            // that eased value REPLACES zoomfactor for this frame. Reset to the base factor when fully zoomed out.
            float zoomscrollScale = Cvar("cl_zoomscroll_scale", 0.2f);
            if (Cvar("cl_zoomscroll", 1f) != 0f && zoomscrollScale != 0f)
            {
                // initialise on the first frame / reset to the base factor when fully zoomed out.
                if (_zoomScrollFactor == 0f || (_currentViewZoom == 1f && !zoomdir))
                {
                    _zoomScrollFactorTarget = zoomfactor;
                    _zoomScrollFactor = zoomfactor;
                }
                if (_zoomScrollFactor != _zoomScrollFactorTarget)
                {
                    float zoomscrollSpeed = Cvar("cl_zoomscroll_speed", 16f);
                    if (Mathf.Abs(_zoomScrollFactor - _zoomScrollFactorTarget) < 0.001f || zoomscrollSpeed < 0f)
                        _zoomScrollFactor = _zoomScrollFactorTarget;
                    else if (zoomscrollSpeed != 0f)
                    {
                        float avgTime = 1f / zoomscrollSpeed;
                        float frac = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, avgTime));
                        _zoomScrollFactor = frac * _zoomScrollFactorTarget + (1f - frac) * _zoomScrollFactor;
                    }
                }
                zoomfactor = _zoomScrollFactor;
            }

            if (zoomspeed < 0f) // instant zoom
            {
                _currentViewZoom = zoomdir ? 1f / zoomfactor : 1f;
            }
            else if (zoomdir)
            {
                // zoom in: drive 1/current_viewzoom up toward zoomfactor, bounded.
                _currentViewZoom = 1f / Mathf.Clamp(
                    1f / _currentViewZoom + dt * zoomspeed * (zoomfactor - 1f), 1f, zoomfactor);
            }
            else
            {
                // zoom out: drive current_viewzoom up toward 1, bounded below by 1/zoomfactor.
                _currentViewZoom = Mathf.Clamp(
                    _currentViewZoom + dt * zoomspeed * (1f - 1f / zoomfactor), 1f / zoomfactor, 1f);
            }
        }

        // current_zoomfraction: 0 at full fov, 1 fully zoomed (QC GetCurrentFov tail).
        if (zoomfactor == 1f || _currentViewZoom > 0.999f)
            ZoomFraction = 0f;
        else if (Mathf.Abs(_currentViewZoom - 1f / zoomfactor) < 1e-4f)
            ZoomFraction = 1f;
        else
            ZoomFraction = (_currentViewZoom - 1f) / (1f / zoomfactor - 1f);

        // Scale mouse sensitivity while zoomed (QC setsensitivityscale: current_viewzoom^(1-zoomsensitivity)).
        float zoomsensitivity = Cvar("cl_zoomsensitivity", 0f);
        SensitivityScale = zoomsensitivity < 1f
            ? Mathf.Pow(_currentViewZoom, 1f - zoomsensitivity)
            : 1f;
    }

    /// <summary>
    /// The pure FOV computation (no camera) — exposed for headless testing of the zoom math. Xonotic's `fov` cvar
    /// is HORIZONTAL at a 4:3 reference; Godot's Camera3D.Fov is the VERTICAL fov (KeepHeight). QC: frustumy =
    /// tan(fov/2) * 0.75 * current_viewzoom (the 0.75 = 3/4 aspect normalization), then fovy = atan(frustumy)*2.
    /// Without the 0.75 the scene is ~16° too wide (fov 100 → 100° vertical instead of Xonotic's ~83.6°).
    /// </summary>
    public static float ComputeVerticalFov(float baseFovDegrees, float currentViewZoom)
    {
        float frustumy = Mathf.Tan(Mathf.DegToRad(baseFovDegrees) * 0.5f) * 0.75f * currentViewZoom;
        return Mathf.RadToDeg(Mathf.Atan(frustumy) * 2f);
    }

    /// <summary>The live <c>current_viewzoom</c> (1 = unzoomed, 1/zoomfactor = fully zoomed) — exposed for tests.</summary>
    public float CurrentViewZoom => _currentViewZoom;

    /// <summary>Place + orient the camera from the player's origin (eye height) and the view angles, applying the
    /// event-chase pull-back on death / third-person and the live zoom to the rendered fov.</summary>
    private void UpdateCamera(Camera3D camera, in ViewState st)
    {
        float eyeZ = st.EyeHeightZ > 0f ? st.EyeHeightZ : EyeHeight;
        NVec3 eyeQuake = st.OriginQuake + new NVec3(0f, 0f, eyeZ);

        // QC cl_player.qc CalcRefdef: `if(autocvar_chase_active) vieworg = CSQCPlayer_ApplyChase(...)` runs the
        // classic user third-person cam (chase_back/up/front/overhead/pitchangle). It takes precedence over the
        // first-person + punch/bob branch, BUT the death/frozen event-chase cam (engaged via st.IsDead/IsFrozen)
        // owns those special cases — so only run the classic chase when the host requested it AND no event-chase.
        bool eventChaseWanted = WantEventChase(st);
        bool classicChase = CameraMode == ChaseMode.Chase && !eventChaseWanted;

        // QC: while seated in a vehicle (hud != HUD_NORMAL) the vehicle chase/cockpit cam owns the frame
        // (cl_eventchase_vehicle). It runs BEFORE the classic/event split — and like them sets ChaseActive so the
        // shared first-person extras (punch/bob/idle-in-first-person and the warpzone seam fix) are suppressed. When
        // cl_eventchase_vehicle is 0 we fall through to the normal first-person eye: the seated origin already places
        // the eye in the cockpit (ViewOfs is 0) so no special handling is needed there.
        bool vehicleChase = InVehicle && Cvar("cl_eventchase_vehicle", 1f) != 0f;

        // The classic chase may rewrite the view angles (chase_overhead forces pitch = chase_pitchangle;
        // chase_front flips the view to look back at the player). Resolve the final angles first, then build the
        // camera basis from them so the orientation matches the chosen perspective.
        NVec3 viewAngles = st.ViewAnglesQuake;
        NVec3 camQuake;
        if (vehicleChase)
        {
            ChaseActive = true;
            // Pull the camera back from the seated pivot along the view forward; ApplyVehicle owns the pivot lift +
            // smoothed distance + world box-trace (port of View_EventChase's vehicle branch) and mutates _eventChase.
            QMath.AngleVectors(viewAngles, out NVec3 forward, out _, out _);
            camQuake = ChaseCamera.ApplyVehicle(st.OriginQuake, forward, _lastDt, ref _eventChase);
        }
        else if (classicChase)
        {
            ChaseActive = true;
            _eventChase = default;   // QC eventchase reset: clear distance + run latch while the classic cam owns the frame
            camQuake = ApplyClassicChase(eyeQuake, ref viewAngles);
        }
        else
        {
            // QC cl_player.qc CalcRefdef non-chase branch (lines 564-571), in order:
            //   CSQCPlayer_ApplyDeathTilt  -> view_angles.z += deathtilt roll while dead
            //   view_angles += view_punchangle (already folded into st.ViewAnglesQuake by the host)
            //   view_angles.z += CSQCPlayer_CalcRoll -> bank when strafing (cl_rollangle)
            //   vieworg += view_punchvector (origin recoil kick, first-person only)
            //   vieworg = CSQCPlayer_ApplyBobbing -> vertical/horizontal/fall view-bob
            // Mostly off by default in stock Xonotic (cl_bob 0 / cl_bob2 0 / cl_rollangle 0 / v_deathtilt 0) —
            // the exception is the landing fall-bob (cl_bobfall 0.05, shipped ON). ApplyDeathTilt/CalcRoll mutate
            // the rendered angles BEFORE the event-chase forward is taken so the pull-back direction matches the
            // rendered view.
            ApplyDeathTilt(st, ref viewAngles);
            viewAngles.Z += CalcRoll(st);

            // Eye position in Quake space; the event-chase camera pulls back from the RAW player origin (lifted by
            // cl_eventchase_viewoffset, not the eye) along -forward on death (or when the host forces third person).
            // Returns the eye in first-person. Uses the (unmodified) view forward for the pull-back direction.
            QMath.AngleVectors(viewAngles, out NVec3 efq, out _, out _);
            camQuake = ApplyEventChase(st, eyeQuake, efq);

            // QC cl_player.qc:570 `vieworg += view_punchvector` — the origin recoil kick is applied in the
            // NON-chase (first-person) branch only, so a pulled-back third-person/death cam is never jolted.
            // The view-bob likewise only applies in first person (it's inside the same non-chase branch, and
            // CSQCPlayer_ApplyBobbing early-outs when dead/in-vehicle).
            if (!ChaseActive)
            {
                camQuake += st.PunchOriginQuake;
                camQuake = ApplyBobbing(st, camQuake, viewAngles);
            }
        }

        // QC CSQCPlayer_ApplyIdleScaling runs AFTER the chase/non-chase split (cl_player.qc:573) — the idle
        // sin-wave of pitch/yaw/roll applies in both first-person and chase. Off by default (v_idlescale 0).
        ApplyIdleScaling(ref viewAngles);

        // QC View_Lock (view.qc:1395, called from CSQC_UpdateView:1733 AFTER the event-chase camera "so that it
        // still applies whenever necessary"): cl_lockview freezes the final rendered view. lock_type >= 1 pins the
        // origin to the captured freeze_org; lock_type == 1 ALSO pins the angles to freeze_ang (2 = origin-only, so
        // you can still look around a frozen point). lock_type 0 continuously captures the live origin/angles so the
        // freeze starts from wherever the view was when it's next enabled. The UI-forced lock_type=1 conditions
        // (hud_configure / clickable radar / minigame menu / quickmenu) are not on the net play path, so only the
        // cl_lockview cvar is honored here. Applied to the final Quake values before they build the camera basis.
        ApplyViewLock(ref camQuake, ref viewAngles);

        // QC WarpZone_FixView (warpzonelib/client.qc): when the eye straddles a warpzone seam, rotate the eye origin
        // and the view angles through the seam transform (and roll-kill) so the rendered view continues seamlessly
        // out the far side. First-person only — a chase pull-back origin already sits outside the body, so the seam
        // straddle test doesn't apply to it. No-op when there's no ambient zone manager (a pure remote client / a
        // non-warpzone map), exactly like the already-live LaserRenderer / PortalRenderer ambient consumers.
        bool insideZone = false;
        if (!ChaseActive)
            WarpzoneFixView.Apply(
                XonoticGodot.Common.Gameplay.WarpzoneTrace.AmbientManager,
                ref camQuake, ref viewAngles, _lastDt, out insideZone);
        InsideWarpzone = insideZone;

        // QC WarpZone_FixNearClip: while straddling a seam, push the near-clip plane out so the geometry on the
        // near side of the portal doesn't poke through. Capture the authored near once so it restores cleanly.
        if (!_baseNearCaptured)
        {
            _baseNear = camera.Near;
            _baseNearCaptured = true;
        }
        camera.Near = _baseNear * (insideZone ? WarpzoneFixView.NearClipMultiplier : 1f);

        // Orientation: derive the camera basis from the SAME Quake view vectors the sim and aiming use
        // (QMath.AngleVectors), converted axis-by-axis into Godot space. This guarantees the camera looks
        // exactly where movement goes and where AimForwardQuake shoots. Building it from a separate negated-yaw
        // Euler silently disagreed by a handedness flip (camera yaw and the wishdir yaw counter-rotated), so
        // WASD/aim drifted from the view — aligned only near a 45° heading. (Godot Basis(x,y,z) takes the
        // basis COLUMNS; the camera looks down its local -Z, so Z column = -forward.)
        QMath.AngleVectors(viewAngles, out NVec3 fq, out NVec3 rq, out NVec3 uq);
        GVec3 right = Coords.ToGodot(rq);
        GVec3 up = Coords.ToGodot(uq);
        GVec3 fwd = Coords.ToGodot(fq);
        camera.GlobalBasis = new Basis(right, up, -fwd);

        camera.GlobalPosition = Coords.ToGodot(camQuake);

        // Stash the FINAL rendered view (QC view_origin / view_forward): camQuake is the authoritative render origin
        // (post chase pull-back + view-lock), fq the forward built from the resolved view angles, and the Godot eye
        // is exactly where the camera was placed. The Phase-2 crosshair-chase consumer reads these to trace true-aim.
        _renderedEyeQuake = camQuake;
        _renderedForwardQuake = fq;
        _renderedEyeGodot = camera.GlobalPosition;

        // QC HUD_Contents samples pointcontents(view_origin) at the FINAL render origin (view.qc:1176) — the
        // pulled-back cam during a chase, the eye otherwise — so the liquid tint reflects where the camera is.
        EyeContents = Api.Services is not null ? Api.Trace.PointContents(camQuake) : 0;

        // Apply the live zoom to the rendered fov (QC GetCurrentFov). See ComputeVerticalFov for the *0.75 note.
        // The velocity-zoom frustum multiplier (off by default, cl_velocityzoom_enabled 0) folds into the zoom.
        float baseFov = Cvar("fov", BaseFov);
        if (baseFov < 1f) baseFov = BaseFov;
        float velocityZoom = ComputeVelocityZoom(st, dtFor: _lastDt);
        camera.Fov = ComputeVerticalFov(baseFov, _currentViewZoom * velocityZoom);
    }

    /// <summary>
    /// Velocity-based FOV zoom — port of <c>GetCurrentFov</c>'s velocityzoom block (view.qc:574-601). When
    /// <c>cl_velocityzoom_enabled</c> and <c>cl_velocityzoom_type</c> are set, the frustum is multiplied by an
    /// exponential of the low-passed player speed so the view widens (or narrows) as you accelerate. Returns 1
    /// (no effect) when disabled — the shipped default (<c>cl_velocityzoom_enabled 0</c>), so a stock match is
    /// unchanged. The averaged speed (<see cref="_avgSpeed"/>) keeps integrating even while disabled is avoided by
    /// only touching it inside the enabled branch, matching QC (it sets velocityzoom = 1 in the else).
    /// </summary>
    private float ComputeVelocityZoom(in ViewState st, float dtFor)
    {
        // cl_velocityzoom_type 0 disables velocity zoom too (QC view.qc:574 comment).
        if (Cvar("cl_velocityzoom_enabled", 0f) == 0f || Cvar("cl_velocityzoom_type", 3f) == 0f)
            return 1f;

        // QC: curspeed from the velocity projected on the view forward (type 3 = max(0, fwd·v); type 2 = fwd·v;
        // type 1/default = |v|). The intermission / spectator-2 zero branch is not on the net first-person path.
        QMath.AngleVectors(st.ViewAnglesQuake, out NVec3 forward, out _, out _);
        NVec3 v = st.VelocityQuake;
        int type = (int)Cvar("cl_velocityzoom_type", 3f);
        float curspeed = type switch
        {
            3 => Mathf.Max(0f, QMath.Dot(forward, v)),
            2 => QMath.Dot(forward, v),
            _ => v.Length(),
        };

        float vzSpeed = Cvar("cl_velocityzoom_speed", 1000f);
        float vzFactor = Cvar("cl_velocityzoom_factor", 0f);
        float vzTime = Cvar("cl_velocityzoom_time", 0.2f);

        float adapt = Mathf.Clamp(dtFor / Mathf.Max(0.000000001f, vzTime), 0f, 1f);
        _avgSpeed = _avgSpeed * (1f - adapt) + (curspeed / vzSpeed) * adapt;
        // QC float2range11(f) = f / (|f| + 1); velocityzoom = exp(float2range11(avgspeed * -factor)).
        float f = _avgSpeed * -vzFactor;
        float range11 = f / (Mathf.Abs(f) + 1f);
        return Mathf.Exp(range11);
    }

    /// <summary>
    /// Death tilt — port of <c>CSQCPlayer_ApplyDeathTilt</c> (cl_player.qc:278-287). Rolls the view over the first
    /// second of death up to <c>v_deathtiltangle</c>. Off by default (<c>v_deathtilt 0</c>) and QC-skipped under
    /// <c>cl_eventchase_death 2</c> (the shipped default) since the death-cam pulls back instead — so it only
    /// shows when a player opts into v_deathtilt AND turns the death-cam off.
    /// </summary>
    private void ApplyDeathTilt(in ViewState st, ref NVec3 viewAngles)
    {
        // Track the death edge so (time - death_time) measures real seconds since death.
        if (st.IsDead && !_wasDead)
            _deathTime = _viewTime;
        else if (!st.IsDead)
            _deathTime = -1f;
        _wasDead = st.IsDead;

        if (!st.IsDead || Cvar("v_deathtilt", 0f) == 0f)
            return;
        // QC: incompatible with cl_eventchase_death 2 (tilt is applied while the corpse is airborne then snapped
        // off on landing), so it bails when the default death-cam is active.
        if (Cvar("cl_eventchase_death", 2f) == 2f)
            return;
        float since = _deathTime >= 0f ? _viewTime - _deathTime : 0f;
        viewAngles.Z += Mathf.Min(since * 8f, 1f) * Cvar("v_deathtiltangle", 80f);
    }

    /// <summary>
    /// Idle view-wave — port of <c>CSQCPlayer_ApplyIdleScaling</c> (cl_player.qc:296-303). Sin-waves pitch/yaw/roll
    /// by <c>v_idlescale</c>. Off by default (<c>v_idlescale 0</c>).
    /// </summary>
    private void ApplyIdleScaling(ref NVec3 viewAngles)
    {
        float idlescale = Cvar("v_idlescale", 0f);
        if (idlescale == 0f)
            return;
        float t = _viewTime;
        viewAngles.X += idlescale * Mathf.Sin(t * Cvar("v_ipitch_cycle", 1f)) * Cvar("v_ipitch_level", 0.3f);
        viewAngles.Y += idlescale * Mathf.Sin(t * Cvar("v_iyaw_cycle", 2f)) * Cvar("v_iyaw_level", 0.3f);
        viewAngles.Z += idlescale * Mathf.Sin(t * Cvar("v_iroll_cycle", 0.5f)) * Cvar("v_iroll_level", 0.1f);
    }

    /// <summary>
    /// View roll when strafing — port of <c>CSQCPlayer_CalcRoll</c> (cl_player.qc:432-447). Banks the view toward
    /// the side velocity, ramping to <c>cl_rollangle</c> past <c>cl_rollspeed</c>. Off by default
    /// (<c>cl_rollangle 0</c>). Returns the roll degrees to add to view_angles.z.
    /// </summary>
    private float CalcRoll(in ViewState st)
    {
        float rollangle = Cvar("cl_rollangle", 0f);
        if (rollangle == 0f)
            return 0f;
        float rollspeed = Cvar("cl_rollspeed", 200f);
        QMath.AngleVectors(st.ViewAnglesQuake, out _, out NVec3 right, out _);
        float side = QMath.Dot(st.VelocityQuake, right);
        float sign = side < 0f ? -1f : 1f;
        side = Mathf.Abs(side);
        if (side < rollspeed)
            side *= rollangle / rollspeed;
        else
            side = rollangle;
        return side * sign;
    }

    /// <summary>
    /// View bobbing — port of <c>CSQCPlayer_ApplyBobbing</c> (cl_player.qc:321-428): vertical bob (<c>cl_bob</c>),
    /// horizontal bob (<c>cl_bob2</c>) and fall-bob (<c>cl_bobfall</c>). The walk bobs are off by default
    /// (<c>cl_bob 0</c>, <c>cl_bob2 0</c>) but the fall-bob is ON in stock Xonotic (<c>cl_bobfall 0.05</c>,
    /// xonotic-client.cfg:151) — the view dips and swings back up when you land from a jump. Adds the bob offset
    /// to the eye/render origin <paramref name="v"/> in Quake space. The <c>cl_bob_limit_heightcheck</c> trace
    /// path (default 0) is omitted.
    /// </summary>
    private NVec3 ApplyBobbing(in ViewState st, NVec3 v, in NVec3 viewAngles)
    {
        // QC bails when dead / in a vehicle / not a player model — the net first-person path here is always a
        // live local player (the dead case is handled by the caller's !ChaseActive gate + event-chase pull-back).
        if (st.IsDead)
            return v;

        const float M_PI = Mathf.Pi;
        float velLimit = Cvar("cl_bob_velocity_limit", 400f);

        // vertical view bobbing
        float clBob = Cvar("cl_bob", 0f);
        float bobcycle = Cvar("cl_bobcycle", 0.5f);
        if (clBob != 0f && bobcycle != 0f)
        {
            float bobLimit = Cvar("cl_bob_limit", 7f);
            float bobup = Cvar("cl_bobup", 0.5f);
            // LordHavoc's bobup: the time at which the sin is at 180deg (lets the peak/valley be stretched).
            float cycle = _viewTime / bobcycle;
            cycle -= Mathf.Round(cycle);
            if (cycle < bobup)
                cycle = Mathf.Sin(M_PI * cycle / bobup);
            else
                cycle = Mathf.Sin(M_PI + M_PI * (cycle - bobup) / (1f - bobup));
            // bob proportional to xy-plane speed (ignore Z so jumping doesn't pump it).
            float xyspeed = Mathf.Clamp(
                Mathf.Sqrt(st.VelocityQuake.X * st.VelocityQuake.X + st.VelocityQuake.Y * st.VelocityQuake.Y),
                0f, velLimit);
            float bob = xyspeed * clBob;
            bob = Mathf.Clamp(bob, 0f, bobLimit);
            bob = bob * 0.3f + bob * 0.7f * cycle;
            v.Z += bob;
        }

        // horizontal view bobbing
        float clBob2 = Cvar("cl_bob2", 0f);
        float bob2cycle = Cvar("cl_bob2cycle", 1f);
        if (clBob2 != 0f && bob2cycle != 0f)
        {
            float cycle = _viewTime / bob2cycle;
            cycle -= Mathf.Round(cycle);
            if (cycle < 0.5f)
                cycle = Mathf.Cos(M_PI * cycle / 0.5f);
            else
                cycle = Mathf.Cos(M_PI + M_PI * (cycle - 0.5f) / 0.5f);
            float bob = clBob2 * cycle;

            // bob2_smooth eases 1->0 when we stop touching ground (also blocked while jumping, to avoid bhop twitches).
            if (st.OnGround && !st.JumpHeld)
                _bob2Smooth = 1f;
            else if (_bob2Smooth > 0f)
                _bob2Smooth -= Mathf.Clamp(Cvar("cl_bob2smooth", 0.05f), 0f, 1f);
            else
                _bob2Smooth = 0f;

            QMath.AngleVectors(viewAngles, out NVec3 fwd, out NVec3 right, out _);
            float side = Mathf.Clamp(QMath.Dot(st.VelocityQuake, right) * _bob2Smooth, -velLimit, velLimit);
            float front = Mathf.Clamp(QMath.Dot(st.VelocityQuake, fwd) * _bob2Smooth, -velLimit, velLimit);
            fwd *= bob;
            right *= bob;
            // side with forward and front with right, so the bob goes sideways when walking forward.
            v.X += side * fwd.X + front * right.X;
            v.Y += side * fwd.Y + front * right.Y;
        }

        // fall bobbing: swing the view down and back up on landing. UNLIKE cl_bob/cl_bob2 this is ON by
        // default — Base ships `cl_bobfall 0.05` in xonotic-client.cfg (the menu checkbox toggles 0.05/0).
        float clBobfall = Cvar("cl_bobfall", 0.05f);
        float bobfallcycle = Cvar("cl_bobfallcycle", 3f);
        if (clBobfall != 0f && bobfallcycle != 0f)
        {
            if (!st.OnGround)
            {
                _bobfallSpeed = Mathf.Clamp(st.VelocityQuake.Z, -400f, 0f) * Mathf.Clamp(clBobfall, 0f, 0.1f);
                _bobfallSwing = st.VelocityQuake.Z < -Cvar("cl_bobfallminspeed", 200f) ? 1f : 0f;
            }
            else
            {
                _bobfallSwing = Mathf.Max(0f, _bobfallSwing - bobfallcycle * (_lastDt > 0f ? _lastDt : 0.0166667f));
                v.Z += Mathf.Sin(M_PI * _bobfallSwing) * _bobfallSpeed;
            }
        }

        return v;
    }

    /// <summary>
    /// The death / event-chase camera — port of view.qc <c>View_EventChase</c> + <c>WantEventchase</c>. When the
    /// local player is dead (QC <c>cl_eventchase_death</c>), or the host requests third person
    /// (<see cref="CameraMode"/>), pull the camera back along <c>-forward</c> from a pivot at the RAW player
    /// origin lifted by <c>cl_eventchase_viewoffset</c> (NOT the eye — view.qc:807,823-828), growing the distance
    /// smoothly (QC <c>eventchase_current_distance</c>) and box-tracing against the world so the camera stops
    /// short of geometry rather than clipping through it. Returns the eye unchanged in normal first-person.
    /// </summary>
    /// <summary>QC <c>WantEventchase</c> (view.qc): true when the death-cam (<c>cl_eventchase_death</c>) or the
    /// Freeze-Tag frozen chase (<c>cl_eventchase_frozen</c>) should engage the pull-back camera. Separated from the
    /// classic user <c>chase_active</c> cam so the two third-person geometries don't both run on the same frame.</summary>
    private bool WantEventChase(in ViewState st)
    {
        // cl_eventchase_death == 1 engages immediately on death; == 2 (shipped default) waits until the corpse stops
        // sliding (velocity == '0 0 0'), then LATCHES until respawn; == 0 disables the death chase.
        float chaseDeath = Cvar("cl_eventchase_death", 2f);
        bool deathChase = st.IsDead && (
            chaseDeath == 1f
            || (chaseDeath == 2f && (st.VelocityQuake == NVec3.Zero || _eventChase.Running)));
        // QC MUTATOR_HOOKFUNCTION(cl_ft, WantEventchase): while frozen in Freeze Tag, cl_eventchase_frozen pulls the
        // camera to third person so the encased player can see around themselves.
        bool frozenChase = st.IsFrozen && Cvar("cl_eventchase_frozen", 0f) != 0f;
        // QC cl_nexball.qc WantEventchase: cl_eventchase_nexball=1 (default) -> third-person cam when the local player
        // is in nexball but is NOT the ball-carrier. Disabled while dead (the death-cam already owns the frame).
        bool nexballChase = !st.IsDead && st.IsNexBallNonCarrier && Cvar("cl_eventchase_nexball", 1f) != 0f;
        return deathChase || frozenChase || nexballChase;
    }

    private NVec3 ApplyEventChase(in ViewState st, NVec3 eyeQuake, NVec3 forwardQuake)
    {
        bool wantChase = WantEventChase(st);
        ChaseActive = wantChase;

        if (!wantChase)
        {
            // reset so the next death starts the pull-back from 0 (QC sets eventchase_current_distance = 0).
            _eventChase = default;
            return eyeQuake;
        }

        // Delegate the pivot lift + smoothed pull-back box-trace to ChaseCamera.ApplyEvent (port of View_EventChase).
        // It reads the RAW player origin (lifts it by cl_eventchase_viewoffset itself — NOT the eye) and mutates the
        // run-latch / smoothed distance in _eventChase. dt comes from the latest UpdateView step.
        float dt = _lastDt > 0f ? _lastDt : 0.0166667f;
        return ChaseCamera.ApplyEvent(st.OriginQuake, forwardQuake, dt, ref _eventChase);
    }

    /// <summary>
    /// The classic user third-person camera — port of <c>CSQCPlayer_ApplyChase</c> (cl_player.qc:453), engaged when
    /// <c>chase_active</c> is set and the host requested <see cref="ChaseMode.Chase"/>. Pulls the camera back along
    /// the view by <c>chase_back</c> and up by <c>chase_up</c> (the default branch), or — when
    /// <c>chase_overhead</c> is set — flattens the pitch and drops an overhead cam sampling the lowest of a 5×5
    /// trace grid. <c>chase_front</c> flips it to a frontal selfie view (only while spectating). Traces the world so
    /// the camera stops short of geometry. Mutates <paramref name="viewAngles"/> for the overhead/front variants so
    /// the caller's basis matches. Operates on the eye position <paramref name="v"/> (= QC vieworg post-smoothing).
    /// </summary>
    private NVec3 ApplyClassicChase(NVec3 v, ref NVec3 viewAngles)
        => ChaseCamera.ApplyClassic(v, ref viewAngles, Spectating);

    // The dt of the latest UpdateView, so ApplyEventChase advances the smoothed distance against the same step.
    private float _lastDt;

    // QC r_nearclip baseline (WarpZone_FixNearClip): the camera's authored near-clip plane, captured once on the
    // first UpdateCamera so the warpzone near-clip push-out (* NearClipMultiplier) restores cleanly to it.
    private float _baseNear;
    private bool _baseNearCaptured;

    // QC View_Lock freeze_org / freeze_ang (view.qc:641): the captured render origin/angles cl_lockview pins the
    // view to. Continuously refreshed while the lock is off so it engages from the live view; held while on.
    private NVec3 _freezeOrg;
    private NVec3 _freezeAng;

    /// <summary>
    /// Port of <c>View_Lock</c> (view.qc:1395). <c>cl_lockview</c> 0 = no lock (continuously capture the live
    /// origin/angles into freeze_org/freeze_ang); &gt;= 1 = pin the origin to the captured freeze_org; == 1 = ALSO
    /// pin the angles to freeze_ang (== 2 freezes only the origin, so you can still look around). Operates on the
    /// final Quake render origin + view angles.
    /// </summary>
    private void ApplyViewLock(ref NVec3 camQuake, ref NVec3 viewAngles)
    {
        int lockType = (int)Cvar("cl_lockview", 0f);

        if (lockType >= 1)
            camQuake = _freezeOrg;
        else
            _freezeOrg = camQuake;

        if (lockType == 1)
            viewAngles = _freezeAng;
        else
            _freezeAng = viewAngles;
    }

    /// <summary>
    /// Read a float cvar live, falling back to <paramref name="fallback"/> only when the cvar is genuinely unset
    /// (its string value is empty). An explicit <c>0</c> is honored — important for toggle cvars like
    /// <c>cl_eventchase_death 0</c> where 0 must not be reinterpreted as "use the default".
    /// </summary>
    internal static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
