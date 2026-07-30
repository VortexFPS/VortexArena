using Godot;
using VortexArena.Common.Gameplay;     // Teams.Red/Blue/Yellow/Pink (NUM_TEAM_* palette values)
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Net;
using VortexArena.Net;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Hud;

/// <summary>
/// Top-down radar / minimap — the C# successor to QuakeC's <c>hud_panel_radar</c> +
/// <c>client/teamradar.qc</c> (Base/.../qcsrc/client/hud/panel/radar.qc). In QC the radar walked the
/// <c>entcs</c> entities — the lightweight position+team+health slice the server replicates for <i>every</i>
/// player so they show on the map even when out of view (<c>draw_teamradar_player</c>) — and stamped a
/// team-colored, facing-oriented arrow per player onto a rotated, player-centered minimap, framed by the skin
/// border and the optional minimap foreground image (<c>draw_teamradar_background</c>).
///
/// Here that same data arrives through <see cref="ClientNet"/>: the predicted local origin
/// (<see cref="ClientNet.PredictedOrigin"/>) is the radar center, and every other networked entity is sampled
/// by id (<see cref="ClientNet.RemoteIds"/> → <see cref="ClientNet.SampleRemote"/> for the interpolated world
/// pose, <see cref="ClientNet.TryGetRemoteState"/> for its kind/team/flags). We render the contract faithfully:
/// the skin border frame, the QC <b>rotation</b> modes (<c>_rotation</c> 0 = player-aligned facing-up, nonzero =
/// fixed cardinal lock), the <b>zoom/scale</b> (<c>_scale</c> / <c>_zoommode</c>) that the QC blends between a
/// "fit whole arena" big size and a fixed pixels-per-unit normal size, the team-colored player arrows, and the
/// foreground alpha (<c>_foreground_alpha</c>). The full minimap texture (<c>minimapname</c>) is heavy and not
/// loaded here, so we render the framed disc + blips + the same rotation/zoom/team-color behaviour, which is the
/// parity target the contract sets for phase 1 (HUD_PARITY_CONTRACT §6 radar).
///
/// <para>As of the HUD parity refactor this is a real <see cref="HudPanel"/> so it is auto-discovered into the
/// full HUD (laid out at the luma top-left corner with the <c>border_corner_northwest</c> frame) AND still works
/// STANDALONE the way <see cref="VortexArena.Game.Net.NetGame"/> uses it — set directly (<see cref="Net"/>,
/// <see cref="Control.Position"/>/<see cref="Control.Size"/>, <see cref="LocalYawDegrees"/> each frame) without
/// a manager driving <c>LoadConfig</c>. The draw code keys off the live <see cref="Control.Size"/> so it honours
/// whichever path placed the panel. The standalone NetGame radar keeps its manual 200×200 at (24,24).</para>
///
/// <para><b>Coordinate mapping.</b> Quake's ground plane is X (forward/east) and Y (left/north); a blip's world
/// delta <c>(d = entity.XY − PredictedOrigin.XY)</c> is rotated by the radar angle into screen space, then mapped
/// to pixels at the current pixels-per-world scale, with screen-Y negated because Godot screen-Y grows downward
/// while Quake +Y is north. The radar angle is the QC <c>HUD_Radar_GetAngle</c>: player-aligned (<c>_rotation</c>
/// 0) counter-rotates the field by the player's yaw so their facing points up; a fixed <c>_rotation</c> locks the
/// map to a cardinal.</para>
/// </summary>
public partial class RadarPanel : HudPanel
{
    /// <summary>
    /// The network source. The radar reads the local player's predicted position and every remote
    /// entity's interpolated pose/state from this. <c>null</c> until the host wires up a live session;
    /// the panel draws nothing while it is.
    /// </summary>
    public ClientNet? Net { get; set; }

    /// <summary>
    /// World radius (Quake units) the radar circle covers from its center to its edge when no live scale is
    /// resolvable (the fallback for the standalone path / unset cvars). Entities farther than this are clamped
    /// onto the rim (so off-radar contacts still show a bearing). The live <c>hud_panel_radar_scale</c> /
    /// <c>_zoommode</c> override this when set. 2500 is a reasonable arena-scale default.
    /// </summary>
    [Export] public float RangeUnits { get; set; } = 2500f;

    /// <summary>
    /// The local player's view yaw (Quake yaw, degrees) — set by the host each frame so the radar rotates
    /// with the player and the direction they face points "up" in the player-aligned rotation mode
    /// (<c>hud_panel_radar_rotation</c> == 0). Ignored when a fixed rotation is selected.
    /// </summary>
    [Export] public float LocalYawDegrees { get; set; }

    /// <summary>
    /// The local player's world origin override (Quake units). When set (non-null) it is used as the radar
    /// center instead of <see cref="ClientNet.PredictedOrigin"/> — handy for the standalone path or when
    /// spectating. Left null the radar centers on the net predicted origin.
    /// </summary>
    public NVec3? CenterOrigin { get; set; }

    // =================================================================================================
    //  Maximized fullscreen / clickable tactical-overview mode (QC HUD_Radar maximized path +
    //  HUD_Radar_Mouse / HUD_Radar_Clickable, radar.qc). Toggled by the host (keybind +showscores-like);
    //  in Onslaught with hud_panel_radar_mouse it becomes a clickable spawn-location selector. None of this
    //  touches the small-radar path: every maximized override is gated behind _maximized below.
    // =================================================================================================

    /// <summary>True while the radar is shown maximized over the whole screen (QC <c>hud_panel_radar_maximized</c>
    /// state). Set via <see cref="SetMaximized"/>. When false the panel renders exactly as the small radar.</summary>
    private bool _maximized;

    /// <summary>Last mouse position in this panel's local space (QC <c>mousepos</c> mapped into the radar rect),
    /// fed by the host each frame while maximized so the clickable spawn-selector can hover-glow the nearest
    /// waypoint. Stored via <see cref="SetMousePosition"/>.</summary>
    private Vector2 _mousePanelPos;

    // ---- cached WorldToScreen parameters from the last maximized DrawPanel, so HandleClick can invert the exact
    //      same transform the user clicked on (QC HUD_Radar_Mouse reads the live teamradar state). Only meaningful
    //      while _maximized; refreshed every maximized frame. ----
    private Vector2 _clickCenter;     // panel-local radar centre (center)
    private Vector2 _clickSize;       // maximized rect size (for the in-rect hit test)
    private float _clickWorldToPixels; // worldToPixels (px per world unit)
    private float _clickYawRad;       // yawRad (the radar angle; cos/sin used -yawRad in the forward transform)
    private float _clickFlipX = 1f;   // v_flipped X sign
    private NVec3 _clickSelfOrigin;   // selfOrigin (radar centre in world XY; Z carried for the spawn pick)
    private bool _clickValid;         // true when the cached transform is finite/usable

    /// <summary>Whether the current gametype is Onslaught — gates the clickable spawn-selector (QC
    /// <c>HUD_Radar_Clickable</c> requires <c>autocvar_g_onslaught</c>). Set by the host.</summary>
    public bool IsOnslaught { get; set; }

    /// <summary>True when the maximized radar is acting as the Onslaught spawn-location picker — QC
    /// <c>HUD_Radar_Clickable</c> = maximized AND Onslaught AND <c>hud_panel_radar_mouse</c>. In this mode the
    /// view is forced to the fit-arena zoom and clicks resolve to a world point (<see cref="HandleClick"/>).</summary>
    public bool Clickable => _maximized && IsOnslaught && GlobalF("hud_panel_radar_mouse", 0f) != 0f;

    /// <summary>True while maximized (QC <c>hud_panel_radar_maximized</c>). The host reads this to know whether the
    /// radar is capturing the mouse / full-screen.</summary>
    public bool Maximized => _maximized;

    /// <summary>The console-command sink used to emit the Onslaught spawn pick (<c>cmd ons_spawn x y z</c>) when the
    /// player clicks the clickable radar — wired by the host to <see cref="ClientNet.SendStringCommand"/>. The
    /// server's <c>ons_spawn</c> command parses argv 1..3 as the world x/y/z. <c>null</c> standalone (no-op).</summary>
    public System.Action<string>? SendServerCommand { get; set; }

    /// <summary>Toggle the maximized fullscreen overview (QC opening/closing the maximized radar). Flips the state
    /// and forces a repaint.</summary>
    public void SetMaximized(bool on)
    {
        _maximized = on;
        QueueRedraw();
    }

    /// <summary>Store the current mouse position in panel-local space (QC feeds the radar <c>mousepos</c>). Only the
    /// clickable maximized path reads it (for the hover-glow); the small radar ignores it.</summary>
    public void SetMousePosition(Vector2 panelLocalPos) => _mousePanelPos = panelLocalPos;

    /// <summary>The local player's facing arrow color (QC draws the own player white). Public so the integration
    /// layer can tint it (e.g. to the local team color) if desired.</summary>
    public Color LocalColorTint { get; set; } = new(0.95f, 0.95f, 0.95f, 1f);

    /// <summary>The live first-person zoom fraction (QC <c>current_zoomfraction</c>): 0 = not zoomed, 1 = fully
    /// zoomed. Fed by the host each frame. Drives the radar zoommode 0 (= current_zoomfraction) and 1
    /// (= 1 - current_zoomfraction) so the radar follows the player's +zoom the way Base does.</summary>
    [Export] public float ZoomFraction { get; set; }

    // ---- the real minimap image (QC minimapname + mi_min/mi_max) ----

    /// <summary>The short map name (e.g. "stormkeep"). Used to resolve the minimap image
    /// <c>gfx/&lt;map&gt;_radar</c> → <c>gfx/&lt;map&gt;_mini</c> (QC <c>minimapname</c>) through the VFS — the
    /// real Xonotic minimaps ship inside each map's pk3 as <c>gfx/&lt;map&gt;_mini.jpg</c>.</summary>
    public string? MapName { get; set; }

    /// <summary>Map world XY bounds (QC <c>mi_min</c>/<c>mi_max</c> = the BSP worldspawn model mins/maxs). The
    /// minimap image covers exactly this rect; the image and the blips share the same world→screen transform so
    /// they stay aligned as the radar rotates/zooms.</summary>
    public Vector2 MapMinXY { get; set; }
    public Vector2 MapMaxXY { get; set; }

    private bool HasMapBounds => MapMaxXY.X > MapMinXY.X && MapMaxXY.Y > MapMinXY.Y;

    public override void _Ready()
        => ClipContents = true; // QC drawsetcliparea(panel rect): clip the minimap + blips to the radar rectangle

    // ---- visual constants (the modernized stand-ins for the QC hud_panel_radar_* skin cvars) ----

    private static readonly Color BackgroundColor = new(0.05f, 0.06f, 0.08f, 0.45f);
    private static readonly Color BorderColor = new(1f, 1f, 1f, 0.18f);

    // QC HUD_Radar_GetZoomFactor / scale constants. hud_panel_radar_scale is pixels-per (mi_scale) world unit in
    // the QC; without the loaded minimap bounds we treat _scale as "how many world units fill the radar width"
    // via a reference divisor so the cvar still meaningfully zooms. Larger _scale = wider world coverage.
    private const float ScaleReferenceDivisor = 4f; // tuned so the code default 4096 ≈ 1024u radius

    /// <summary>The radar reads live net state, so it must repaint every frame.</summary>
    public override void _Process(double delta) => QueueRedraw();

    // =================================================================================================
    //  Identity / layout (luma defaults: radar sits in the top-left corner with a corner frame)
    // =================================================================================================

    /// <summary>QC <c>panel.panel_name</c> = "radar".</summary>
    public override string PanelId => "radar";

    // The luma table already carries radar's pos/size/bg (0 0 / 0.2 0.25 / border_corner_northwest), so we do
    // NOT override DefaultLayout/DefaultBg — the base virtuals look them up. The standalone NetGame path sets
    // Position/Size manually and never calls LoadConfig, so those defaults only apply to the discovered panel.

    // -------------------------------------------------------------------------------------------------
    //  Behaviour-cvar defaults (HudConfig invokes this by reflection). Mirrors radar.qh + teamradar defaults.
    // -------------------------------------------------------------------------------------------------
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags S = CvarFlags.Save;
        const string P = "hud_panel_radar_";

        // QC ships these cvars as "" in _hud_descriptions.cfg and teamradar_loadcvars fills the CODE defaults
        // when they read 0/unset: scale → 4096, foreground_alpha → 0.8 (* panel_fg_alpha). Register those code
        // defaults so a user who never touches the cvar (and loads no skin) gets the faithful Base values, not
        // the luma-skin overrides (8192 / 1). See teamradar.qc:182-185.
        c.Register(P + "foreground_alpha", "0.8", S); // teamradar_loadcvars: 0 → 0.8
        c.Register(P + "rotation", "0", S);            // 0 = player-aligned, 1=west 2=south 3=east 4=north
        c.Register(P + "zoommode", "0", S);            // 0 = current_zoomfraction, 1 inverse-zoom, 2 normal, 3 big
        c.Register(P + "scale", "4096", S);            // teamradar_loadcvars: 0 → 4096
        c.Register(P + "maximized_scale", "8192", S);  // luminos code-default (luma skin overrides to 5120)
        c.Register(P + "maximized_size", "0.5 0.5", S);
        c.Register(P + "maximized_rotation", "1", S);  // skin default (luma/luminos all ship 1)
        c.Register(P + "maximized_zoommode", "3", S);  // skin default (all ship 3 = always big)
        c.Register(P + "dynamichud", "1", S);
    }

    protected override void DrawPanel()
    {
        ClientNet? net = Net;
        if (net is null)
            return; // self-blank: no data → draw nothing

        // ---- panel enable + team gating (QC HUD_Radar, radar.qc:191-194) ----
        // hud_panel_radar: 0 = disabled, 1 = enabled (team modes only), 2 = also enabled in non-team modes.
        // The standalone radar isn't driven by the HUD show-mode machinery, so honour the cvar here directly.
        int enable = Mathf.RoundToInt(GlobalF("hud_panel_radar", 1f));
        if (enable == 0)
            return;
        if (enable != 2 && !VortexArena.Common.Gameplay.Scoring.GameScores.Teamplay)
            return;

        // Draw in the live control size so this works both as a discovered panel (Size == Cfg.SizePx, set by
        // LoadConfig) and standalone (Size set manually by NetGame). panel-local space: origin = top-left.
        Vector2 size = Size;
        Vector2 center = size * 0.5f;

        // ---- maximized fullscreen overview (QC HUD_Radar maximized branch) ----
        // When maximized the radar is sized as a fraction of the SCREEN (hud_panel_radar_maximized_size, QC bound
        // 0.2..1 per axis) and centred on it, rather than honouring the small control rect. We keep drawing in this
        // panel's local space, so the maximized rect's centre is the screen centre expressed local to this control
        // (screenCentre - GlobalPosition). Everything below (WorldToScreen, blips, arrows) then uses the override
        // size/center transparently. The small-radar path is untouched when _maximized is false.
        if (_maximized)
        {
            Vector2 vp = GetViewportRect().Size;
            Vector2 frac = ParseMaximizedSize();
            Vector2 maxSize = new(vp.X * frac.X, vp.Y * frac.Y);
            size = maxSize;
            Vector2 screenCenter = vp * 0.5f;
            center = screenCenter - GlobalPosition; // panel-local coords of the screen centre
        }

        float radius = Mathf.Min(size.X, size.Y) * 0.5f;
        if (radius <= 0f)
            return;

        // ---- skin border frame (QC HUD_Panel_DrawBg → draw_BorderPicture). No-op when the panel's bg is "0". ----
        DrawBackground();

        // Foreground alpha (QC hud_panel_radar_foreground_alpha * panel_fg_alpha). When discovered it folds the
        // HUD/scoreboard fade in via LiveFgAlpha; standalone LiveFgAlpha defaults to ~0.9, which is fine.
        // QC teamradar_loadcvars: a foreground_alpha of 0 (or unset) defaults to 0.8 — match that faithfully so a
        // skin that zeroes it still shows the radar rather than blanking. NaN/Inf cvars collapse to the default.
        float fgCvar = CvarF("foreground_alpha", 1f);
        if (!float.IsFinite(fgCvar) || fgCvar <= 0f)
            fgCvar = 0.8f;
        float fgAlpha = Mathf.Clamp(fgCvar, 0f, 1f) * Mathf.Clamp(LiveFgAlpha, 0f, 1f);
        if (fgAlpha <= 0.001f)
            return;

        // Background playfield (rectangular, like the QC radar panel). The minimap image draws over it; areas
        // outside the map (or before the image loads) stay this dark fill.
        DrawRect(new Rect2(Vector2.Zero, size), WithAlpha(BackgroundColor, BackgroundColor.A * fgAlpha / 0.9f));

        // ---- rotation (QC HUD_Radar_GetAngle) ----
        // _rotation 0  → player-aligned: counter-rotate by the player's yaw so facing points up. QC returns
        //                (view_angles.y - 90)·DEG2RAD — the -90 makes a player facing north (Quake yaw 90)
        //                leave the map un-rotated, so their facing ends up "up" after the screen-Y flip below.
        // _rotation !0 → fixed: lock the map to a cardinal (90°·rotation), independent of the player's facing.
        // The view yaw is host-supplied each frame and could be uninitialised / NaN before the first net update;
        // a NaN yaw would poison cos/sin and turn every blip into NaN. Sanitise it to a finite angle.
        float localYaw = float.IsFinite(LocalYawDegrees) ? LocalYawDegrees : 0f;
        // QC teamradar_loadcvars: the maximized radar reads its own _maximized_rotation/_scale/_zoommode overrides.
        float rotationCvar = _maximized ? CvarF("maximized_rotation", 1f) : CvarF("rotation", 0f);
        int rotation = float.IsFinite(rotationCvar) ? (int)rotationCvar : 0;
        float radarYawDeg = rotation != 0 ? 90f * rotation : localYaw - 90f;

        // QC v_flipped (mirrored view): the radar X axis is mirrored too (teamradar_texcoord_to_2dcoord:
        // `if(v_flipped) out.x = -out.x`, and draw_teamradar_player flips forward.x/right.x). We fold it into a
        // ±1 X sign applied after the screen-Y flip so the map + every blip + every arrow stay consistent.
        bool vFlipped = GlobalF("v_flipped", 0f) != 0f;
        float flipX = vFlipped ? -1f : 1f;
        float yawRad = Mathf.DegToRad(radarYawDeg);
        float cos = Mathf.Cos(-yawRad);
        float sin = Mathf.Sin(-yawRad);

        // ---- zoom / scale (QC HUD_Radar_GetZoomFactor + teamradar_size blend, radar.qc:260-294) ----
        // Base blends teamradar_size (PIXELS per world unit) between a "big" size (fit the whole arena in the
        // radar) and a "normal" size (a fixed scale) by the zoom factor. Our world→screen transform multiplies
        // the rotated world delta by `worldToPixels`, which IS Base's `teamradar_size`, so we reproduce the
        // bigsize/normalsize math directly in px-per-unit rather than the old RangeUnits heuristic.
        // QC teamradar_loadcvars maximized overrides: _maximized_scale / _maximized_zoommode replace the small ones.
        float scale = _maximized ? CvarF("maximized_scale", 8192f) : CvarF("scale", 4096f); // teamradar code defaults
        float scaleFallback = _maximized ? 8192f : 4096f;
        if (!float.IsFinite(scale) || scale <= 0f) scale = scaleFallback; // teamradar_loadcvars: 0 → code default
        float zoomCvar = _maximized ? CvarF("maximized_zoommode", 3f) : CvarF("zoommode", 0f);
        int zoommode = float.IsFinite(zoomCvar) ? (int)zoomCvar : 0;
        float zoomFactor = Mathf.Clamp(GetZoomFactor(zoommode), 0f, 1f);
        // QC HUD_Radar: the clickable spawn-selector forces the fit-arena view so the whole map is reachable and the
        // click→world inverse is well-defined (no per-player pan/zoom). Override after the zoommode resolve.
        if (Clickable)
            zoomFactor = 1f;

        float worldToPixels;
        if (HasMapBounds)
        {
            // Base derives the endpoints from mi_scale (world XY span) and scale2d. The port feeds the radar the
            // raw BSP worldspawn bounds, i.e. the "non-clever" gfx/<map>_mini texcoord path where mi_picmin=(0,0)
            // and mi_picmax=(1,1) — so scale2d = vlen_maxnorm2d(mi_picmax-mi_picmin) = 1. teamradar_size2d is the
            // panel pixel size (mySize). mi_scale = mi_max - mi_min.
            const float scale2d = 1f;
            float miScaleX = MapMaxXY.X - MapMinXY.X;
            float miScaleY = MapMaxXY.Y - MapMinXY.Y;
            float sizeMin = Mathf.Min(size.X, size.Y); // vlen_minnorm2d of a positive (w,h)
            float sizeMax = Mathf.Max(size.X, size.Y); // vlen_maxnorm2d of a positive (w,h)

            float bigsize;
            if (rotation == 0)
            {
                // max-min distance must fit the radar in ANY rotation: divide by the world-space diagonal.
                // bigsize = vlen_minnorm2d(size) * scale2d / (1.05 * vlen(vec2(mi_scale)))
                float diag = Mathf.Sqrt(miScaleX * miScaleX + miScaleY * miScaleY);
                bigsize = sizeMin * scale2d / (1.05f * Mathf.Max(diag, 0.0001f));
            }
            else
            {
                // Fixed rotation: fit the rotated arena bbox in x=x, y=y. Rotate the 4 world corners of
                // [mi_min, mi_max] by teamradar_angle and take the axis-aligned span (radar.qc:274-287).
                // teamradar_angle in the port is yawRad (the same -DEG2RAD radar angle fed into cos/sin above).
                float rc = Mathf.Cos(yawRad), rs = Mathf.Sin(yawRad);
                Vector2 R(float x, float y) => new(x * rc - y * rs, x * rs + y * rc);
                Vector2 p0 = R(MapMinXY.X, MapMinXY.Y);
                Vector2 p1 = R(MapMaxXY.X, MapMaxXY.Y);
                Vector2 p2 = R(MapMinXY.X, MapMaxXY.Y);
                Vector2 p3 = R(MapMaxXY.X, MapMinXY.Y);
                float spanX = Mathf.Max(Mathf.Max(p0.X, p1.X), Mathf.Max(p2.X, p3.X)) -
                              Mathf.Min(Mathf.Min(p0.X, p1.X), Mathf.Min(p2.X, p3.X));
                float spanY = Mathf.Max(Mathf.Max(p0.Y, p1.Y), Mathf.Max(p2.Y, p3.Y)) -
                              Mathf.Min(Mathf.Min(p0.Y, p1.Y), Mathf.Min(p2.Y, p3.Y));
                bigsize = Mathf.Min(
                    size.X * scale2d / (1.05f * Mathf.Max(spanX, 0.0001f)),
                    size.Y * scale2d / (1.05f * Mathf.Max(spanY, 0.0001f)));
            }

            // normalsize = vlen_maxnorm2d(size) * scale2d / scale, floored to bigsize.
            float normalsize = sizeMax * scale2d / scale;
            if (bigsize > normalsize)
                normalsize = bigsize;

            worldToPixels = zoomFactor * bigsize + (1f - zoomFactor) * normalsize;
            if (!float.IsFinite(worldToPixels) || worldToPixels <= 0f)
                worldToPixels = radius / Mathf.Max(RangeUnits, 1f);
        }
        else
        {
            // No map bounds (degenerate / non-standalone): fall back to the RangeUnits world-coverage heuristic.
            float normalRange = scale / ScaleReferenceDivisor;
            float bigRange = Mathf.Max(RangeUnits, normalRange);
            float rangeUnits = Mathf.Lerp(normalRange, bigRange, zoomFactor);
            if (!float.IsFinite(rangeUnits))
                rangeUnits = bigRange;
            rangeUnits = Mathf.Max(rangeUnits, 1f);
            worldToPixels = radius / rangeUnits;
        }
        NVec3 viewOrigin = CenterOrigin ?? net.PredictedOrigin;
        // QC HUD_Radar: the radar center blends between the player origin (normal) and the MAP center (big zoom)
        // by the zoom factor — `teamradar_origin3d_in_texcoord = zoom*mi_center + (1-zoom)*view_origin`. So at
        // full zoom-out (fit-arena) the view recenters on the whole map, not on the player. mi_center = the BSP
        // bounds midpoint. Z is irrelevant to the top-down transform; only XY recenter.
        NVec3 selfOrigin = viewOrigin;
        if (HasMapBounds && zoomFactor > 0f)
        {
            float cx = (MapMinXY.X + MapMaxXY.X) * 0.5f;
            float cy = (MapMinXY.Y + MapMaxXY.Y) * 0.5f;
            selfOrigin.X = zoomFactor * cx + (1f - zoomFactor) * viewOrigin.X;
            selfOrigin.Y = zoomFactor * cy + (1f - zoomFactor) * viewOrigin.Y;
        }
        // A non-finite radar center would turn every blip delta into NaN; if it is bad, skip the contacts entirely
        // and only draw the (origin-independent) center arrow below.
        bool selfFinite = float.IsFinite(selfOrigin.X) && float.IsFinite(selfOrigin.Y);
        float now = net.LatestServerTime;
        if (!float.IsFinite(now))
            now = 0f;
        int localId = net.LocalNetId;

        // World ground-plane XY → radar screen — the shared transform (QC teamradar_texcoord_to_2dcoord, but in
        // world units): delta from the radar center, Rotate by the radar angle, screen-Y flip, scale, recenter.
        // The minimap image AND every blip go through this, so they always stay aligned.
        Vector2 WorldToScreen(float wx, float wy)
        {
            float ddx = wx - selfOrigin.X, ddy = wy - selfOrigin.Y;
            float rrx = ddx * cos - ddy * sin, rry = ddx * sin + ddy * cos;
            return center + new Vector2(rrx * flipX, -rry) * worldToPixels;
        }

        // ---- the real minimap image (QC draw_teamradar_background): a rotated, player-centered textured quad
        // covering the map's world bounds [mi_min, mi_max]. The corners map through WorldToScreen; the UVs match
        // QC's yinvert (image Y is top-down, world Y bottom-up). Clipped to the panel rect by ClipContents. ----
        // QC HUD_Radar (radar.qc:203): the SMALL (non-maximized) radar is hard-gated on minimapname != "" — if no
        // gfx/<map>_radar / gfx/<map>_mini image resolves, the whole panel early-returns (no playfield, no blips).
        // The standalone NetGame radar is fed MapName + BSP bounds, so the image usually resolves; this only blanks
        // the radar on maps that genuinely ship no minimap art, matching Base. (Guarded behind HasMapBounds + MapName
        // so the degenerate no-bounds fallback path is unaffected.)
        Texture2D? mini = (selfFinite && HasMapBounds && !string.IsNullOrEmpty(MapName))
            ? TextureCache.GetFirst($"gfx/{MapName}_radar", $"gfx/{MapName}_mini")
            : null;
        if (HasMapBounds && !string.IsNullOrEmpty(MapName) && mini is null)
            return; // QC minimapname == "" → no radar at all
        if (selfFinite && HasMapBounds && !string.IsNullOrEmpty(MapName))
        {
            if (mini is not null)
            {
                // QC get_mi_min_max_texcoords (util.qc:770-790): the shipped minimap image is a SQUARE that
                // letterboxes the (usually non-square) map — it covers [mi_picmin, mi_picmax] = the raw map bounds
                // extended on the SHORTER axis to a centered square, then padded 1/64 on each side. The image maps
                // [0,1] onto THAT square (radarmap.qc:227, image.mins = mi_picmin), so draw the full-[0,1] quad over
                // the square PIC bounds — not the raw map bounds. Drawing the whole image over the raw (non-square)
                // bounds stretched the art on the short axis and drifted it off the blips (playtest-bugs #18). The
                // blips keep using the raw bounds (their world→pixel scale is already correct), so the two now agree.
                float exX = MapMaxXY.X - MapMinXY.X, exY = MapMaxXY.Y - MapMinXY.Y;
                float sqHalf = Mathf.Max(exX, exY) * (0.5f + 1f / 64f); // square half-span + QC's 1/64 padding
                float mcx = (MapMinXY.X + MapMaxXY.X) * 0.5f, mcy = (MapMinXY.Y + MapMaxXY.Y) * 0.5f;
                float x0 = mcx - sqHalf, y0 = mcy - sqHalf, x1 = mcx + sqHalf, y1 = mcy + sqHalf;
                var pts = new[] { WorldToScreen(x0, y0), WorldToScreen(x1, y0), WorldToScreen(x1, y1), WorldToScreen(x0, y1) };
                if (AllFinite(pts))
                {
                    var uvs = new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) };
                    Color mc = new(1f, 1f, 1f, fgAlpha);
                    DrawPolygon(pts, new[] { mc, mc, mc, mc }, uvs, mini);
                }
            }
        }

        // ---- Onslaught radar links (QC draw_teamradar_link over g_radarlinks, radar.qc:301): the control-point /
        // generator power-connection lines, drawn UNDER the player blips + objective icons (QC draws links before
        // the entcs players + icons). Each link is a quad between the two endpoints, colored at each end by that
        // end's owning team (QC colormapPaletteColor per end — reproduced here by TeamColor, which already maps a
        // NUM_TEAM_* code to the faithful Team_ColorRGB). Off in any non-Onslaught mode (the list is empty). ----
        if (selfFinite)
        {
            foreach (ClientNet.RadarLink link in net.RadarLinks)
            {
                if (!float.IsFinite(link.A.X) || !float.IsFinite(link.A.Y) ||
                    !float.IsFinite(link.B.X) || !float.IsFinite(link.B.Y))
                    continue;
                Vector2 a = WorldToScreen(link.A.X, link.A.Y);
                Vector2 b = WorldToScreen(link.B.X, link.B.Y);
                if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
                    continue;
                Color ca = WithAlpha(TeamColor(link.TeamA), fgAlpha);
                Color cb = WithAlpha(TeamColor(link.TeamB), fgAlpha);
                DrawLinkQuad(a, b, ca, cb);
            }
        }

        // Snapshot the remote-id set before iterating: RemoteIds is a live view over the net layer's dictionary,
        // and a snapshot/forget arriving mid-frame (or the public ForgetRemote) would otherwise throw
        // "collection was modified" out of this every-frame draw path. ToArray copies the keys defensively.
        if (selfFinite)
        {
            int[] ids = System.Linq.Enumerable.ToArray(net.RemoteIds);
            foreach (int netId in ids)
            {
                if (netId == localId)
                    continue;
                if (!net.SampleRemote(netId, now, out NVec3 origin, out NVec3 angles))
                    continue;
                if (!net.TryGetRemoteState(netId, out NetEntityState state))
                    continue;
                // QC: the radar draws ONLY players (draw_teamradar_player) here + gametype-objective icons
                // (draw_teamradar_icon over g_radaricons — CTF flags / DOM points / KH keys) drawn separately
                // below from the networked RadarObjectives. It NEVER shows regular items, projectiles or gibs.
                if (state.Kind != NetEntityKind.Player && state.Kind != NetEntityKind.Nameplate)
                    continue;
                if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y))
                    continue; // bad interpolated pose — skip rather than draw a NaN blip

                // Same transform as the minimap image (QC: blips clip to the panel rect, no rim-clamp).
                Vector2 pos = WorldToScreen(origin.X, origin.Y);
                if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
                    continue; // never hand a NaN coordinate to the drawing primitives
                DrawBlip(pos, state, angles, radarYawDeg, fgAlpha, flipX);
            }

            // ---- waypoint objective icons (QC draw_teamradar_icon over g_radaricons): CTF flags / DOM points /
            // KH keys / player pings — networked as waypoint sprites + team/rule-filtered server-side. A small
            // teamradar sprite at each waypoint's world position, tinted by its networked radar color, drawn UNDER
            // the local arrow so the player's own marker stays on top. ----
            // QC draw_teamradar_icon: a FIXED 8x8 px sprite centered on the icon (drawpic coord-'4 4 0', '8 8 0').
            // Every core REGISTER_RADARICON resolves m_radaricon to 0/1, so the only sprite Base ever draws is
            // gfx/teamradar_icon_1 — there is no id-2 sprite. Size is NOT radius-relative.
            const float iconPx = 8f;
            Texture2D? icon = TextureCache.Get("gfx/teamradar_icon_1");
            foreach (VortexArena.Common.Gameplay.Waypoints.WaypointNet wp in net.Waypoints)
            {
                if (wp.RadarIcon <= 0)
                    continue; // not a radar waypoint (some pings are 3D-only)
                if (!float.IsFinite(wp.Origin.X) || !float.IsFinite(wp.Origin.Y))
                    continue;
                Vector2 op = WorldToScreen(wp.Origin.X, wp.Origin.Y);
                if (!float.IsFinite(op.X) || !float.IsFinite(op.Y))
                    continue;
                Color oc = new(wp.Color.X, wp.Color.Y, wp.Color.Z, fgAlpha * Mathf.Clamp(wp.Fade, 0f, 1f));
                var orect = new Rect2(op - new Vector2(iconPx, iconPx) * 0.5f, new Vector2(iconPx, iconPx));
                if (icon is not null)
                    DrawTextureRect(icon, orect, false, oc);
                else
                    DrawRect(orect, oc); // fallback marker if the teamradar sprite is missing

                // ---- ping pulse rings (QC draw_teamradar_icon ping loop over teamradar_times) ----
                // A ping stamps a timestamp; for ~1s after, draw gfx/teamradar_ping as an expanding additive ring
                // of size '2 2 0'*teamradar_size*dt, alpha (1-dt)*a. We model teamradar_size by the icon size
                // (8px) so the ring grows from the icon to ~8px radius over its lifetime, matching the QC feel.
                DrawPingPulse(wp.Id, op, iconPx, fgAlpha * Mathf.Clamp(wp.Fade, 0f, 1f), now);
            }
        }

        // ---- maximized clickable spawn-selector overlay (QC HUD_Radar_Mouse, radar.qc): a hover-glow on the
        // nearest waypoint under the cursor + the "Click to select spawn location" prompt. Only in the clickable
        // (maximized + Onslaught + hud_panel_radar_mouse) state; the small radar and the non-clickable maximized
        // view skip it entirely. ----
        if (Clickable && selfFinite)
        {
            Texture2D? glow = TextureCache.Get("gfx/teamradar_icon_glow");
            const float glowPx = 16f;
            foreach (VortexArena.Common.Gameplay.Waypoints.WaypointNet wp in net.Waypoints)
            {
                if (!float.IsFinite(wp.Origin.X) || !float.IsFinite(wp.Origin.Y))
                    continue;
                Vector2 op = WorldToScreen(wp.Origin.X, wp.Origin.Y);
                if (!float.IsFinite(op.X) || !float.IsFinite(op.Y))
                    continue;
                // QC: highlight the waypoint within 8px of the cursor (mouse hover pick radius).
                if ((_mousePanelPos - op).Length() > 8f)
                    continue;
                // QC draws gfx/teamradar_icon_glow at 1.5x-brightened wp color (the selected highlight).
                Color gc = new(
                    Mathf.Min(wp.Color.X * 1.5f, 1f),
                    Mathf.Min(wp.Color.Y * 1.5f, 1f),
                    Mathf.Min(wp.Color.Z * 1.5f, 1f),
                    fgAlpha);
                var grect = new Rect2(op - new Vector2(glowPx, glowPx) * 0.5f, new Vector2(glowPx, glowPx));
                if (glow is not null)
                    DrawTextureRect(glow, grect, false, gc);
                else
                    DrawRect(grect, gc); // fallback highlight if the glow sprite is missing
            }

            // QC HUD_Radar: the centred call-to-action prompt across the radar rect.
            DrawTextCentered(new Vector2(center.X - size.X * 0.5f, center.Y - radius - FontSize - 4f),
                size.X, "Click to select spawn location", new Color(1f, 1f, 1f, fgAlpha));
        }

        // Cache the live WorldToScreen parameters so HandleClick can invert the exact transform the user sees.
        if (_maximized)
        {
            _clickCenter = center;
            _clickSize = size;
            _clickWorldToPixels = worldToPixels;
            _clickYawRad = yawRad;
            _clickFlipX = flipX;
            _clickSelfOrigin = selfOrigin;
            _clickValid = selfFinite && float.IsFinite(worldToPixels) && worldToPixels > 0f;
        }
        else
        {
            _clickValid = false;
        }

        // Local player last, on top: a facing arrow (QC draws own player white at view_angles). QC draws it at the
        // player's WORLD origin through the same transform, so once the big-zoom recentres on the map center the
        // own arrow drifts off the panel center too. WorldToScreen(viewOrigin) reproduces that; with no
        // recentering it lands exactly on center. Fall back to the panel center if the origin is non-finite.
        Vector2 localPos = center;
        if (selfFinite && float.IsFinite(viewOrigin.X) && float.IsFinite(viewOrigin.Y))
        {
            Vector2 lp = WorldToScreen(viewOrigin.X, viewOrigin.Y);
            if (float.IsFinite(lp.X) && float.IsFinite(lp.Y))
                localPos = lp;
        }
        DrawPlayerArrow(localPos, localYaw, radarYawDeg, LocalColorTint, fgAlpha, isLocal: true, flipX);

        // Rectangular frame edge (subtle), drawn last inside the clip so it reads over the map image.
        DrawRect(new Rect2(Vector2.One, size - new Vector2(2f, 2f)),
            WithAlpha(BorderColor, BorderColor.A * fgAlpha / 0.9f), filled: false, width: 1.5f);
    }

    /// <summary>QC draw_teamradar_icon ping loop: for ~1s after a ping, draw gfx/teamradar_ping as an expanding
    /// additive ring of size '2 2 0'*teamradar_size*dt, alpha (1-dt)*a (teamradar.qc:126-138). dt = time - stamp.
    /// teamradar_size is the QC 2D scale factor; here we use the icon size (<paramref name="iconPx"/>) so the ring
    /// grows from a point to ~2·iconPx over its lifetime, the same visual the QC produces around an 8px icon.</summary>
    private void DrawPingPulse(int waypointId, Vector2 op, float iconPx, float a, float now)
    {
        ClientNet? net = Net;
        if (net is null || a <= 0.001f)
            return;
        float stamp = net.RadarPingTime(waypointId);
        if (stamp <= 0f)
            return;
        float dt = now - stamp;
        if (dt <= 0f || dt >= 1f)
            return;
        Texture2D? ping = TextureCache.Get("gfx/teamradar_ping");
        float sz = 2f * iconPx * dt;            // '2 2 0' * teamradar_size * dt
        float alpha = (1f - dt) * a;             // (1 - dt) * a
        var rect = new Rect2(op - new Vector2(sz, sz) * 0.5f, new Vector2(sz, sz));
        // QC DRAWFLAG_ADDITIVE: additive blend so the ring brightens the map underneath.
        if (ping is not null)
            DrawTextureRect(ping, rect, false,
                new Color(1f, 1f, 1f, Mathf.Clamp(alpha, 0f, 1f)));
    }

    /// <summary>QC draw_teamradar_link: a connection line between two radar points as a thin 4-vertex quad, each
    /// END tinted by its own team color (QC builds the quad with the two per-end colors). A ~2px-wide band along
    /// the A→B direction so it reads on the rotated minimap; degenerate (zero-length) links are skipped.</summary>
    private void DrawLinkQuad(Vector2 a, Vector2 b, Color colorA, Color colorB)
    {
        Vector2 dir = b - a;
        float len = dir.Length();
        if (len < 0.001f)
            return;
        Vector2 perp = new Vector2(-dir.Y, dir.X) / len; // half-width 1px → ~2px band (QC link thickness)
        var pts = new Vector2[]
        {
            a - perp, b - perp, b + perp, a + perp,
        };
        // Two verts per end carry that end's color so the line gradients between the two team colors, like QC's
        // per-end colormapPaletteColor quad.
        var cols = new Color[] { colorA, colorB, colorB, colorA };
        DrawPolygon(pts, cols);
    }

    /// <summary>True when every point is finite (guards the textured-quad / polygon draw against a NaN vertex).</summary>
    private static bool AllFinite(Vector2[] pts)
    {
        foreach (Vector2 p in pts)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                return false;
        return true;
    }

    // QC HUD_Radar_GetZoomFactor (radar.qc:160-169). Mode 0 follows the live +zoom (current_zoomfraction),
    // mode 1 inverts it, mode 2 is always normal (0), mode 3 always big (1). ZoomFraction is fed by the host.
    private float GetZoomFactor(int zoommode)
    {
        float zf = float.IsFinite(ZoomFraction) ? Mathf.Clamp(ZoomFraction, 0f, 1f) : 0f;
        return zoommode switch
        {
            1 => 1f - zf,  // QC 1 - current_zoomfraction
            2 => 0f,       // always normal (fixed scale)
            3 => 1f,       // always big (fit arena)
            _ => zf,       // QC current_zoomfraction (follow the player's zoom)
        };
    }

    /// <summary>Draw one remote player contact as a team-colored facing arrow (QC <c>draw_teamradar_player</c>),
    /// dimmed when dead. Only players reach here (the loop filters everything else, like QC).</summary>
    private void DrawBlip(Vector2 pos, in NetEntityState state, NVec3 angles, float radarYawDeg, float fgAlpha, float flipX)
    {
        bool dead = (state.Flags & NetEntityFlags.Dead) != 0;
        Color color = TeamColor(state.Colormap);
        float a = fgAlpha * (dead ? 0.35f : 1f);
        DrawPlayerArrow(pos, angles.Y, radarYawDeg, color, a, isLocal: false, flipX);
    }

    /// <summary>
    /// Draw a player marker as a facing-oriented arrow — the C# port of QC <c>draw_teamradar_player</c>. The QC
    /// draws two stacked quads: a slightly larger contrast quad (black behind a colored player, white behind the
    /// white own-player) and the colored arrow on top, both rotated by the entity's yaw relative to the radar
    /// angle. <paramref name="yawDeg"/> is the entity's Quake yaw; <paramref name="radarYawDeg"/> is the radar's
    /// rotation so the arrow points the right way on a rotated map.
    /// </summary>
    private void DrawPlayerArrow(Vector2 pos, float yawDeg, float radarYawDeg, Color color, float alpha, bool isLocal, float flipX = 1f)
    {
        if (alpha <= 0.001f)
            return;
        // Defensive: the marker origin and the entity yaw both originate from interpolated net state and could be
        // non-finite; either would feed NaN into the polygon vertices below and corrupt the draw call.
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
            return;
        if (!float.IsFinite(yawDeg)) yawDeg = 0f;
        if (!float.IsFinite(radarYawDeg)) radarYawDeg = 0f;

        // QC draw_teamradar_player: MAKE_VECTORS(pangles - radar_angle), then forward.y *= -1 (screen-Y flip)
        // and right = (-forward.y, forward.x). With Quake yaw 0 = +X (east) and pitch 0, AngleVectors gives
        // forward = (cos(eyaw), sin(eyaw)); after the y-flip → (cos(eyaw), -sin(eyaw)) and right = (sin(eyaw),
        // cos(eyaw)). eyaw = entity yaw minus the radar angle. The local player (yaw - radarYaw == 90) thus
        // points straight up, exactly as QC draws own-player at view_angles on a (yaw-90)-rotated map.
        float screenYaw = Mathf.DegToRad(yawDeg - radarYawDeg);
        float c = Mathf.Cos(screenYaw), sn = Mathf.Sin(screenYaw);
        var forward = new Vector2(c, -sn);
        var right = new Vector2(sn, c);
        // QC v_flipped: draw_teamradar_player negates forward.x and right.x (the X axis is mirrored).
        if (flipX < 0f) { forward.X = -forward.X; right.X = -right.X; }

        // QC draws every player (incl. the local one) at the SAME fixed pixel size — no per-player scaling.
        // Contrast backing quad (QC rgb2: black behind a colored arrow, white behind the white own-player), then
        // the colored arrow on top. Exact QC vertex offsets: backing (3, 4, 2.5, 2), colored (2, 3, 2, 1).
        Color contrast = IsNearWhite(color)
            ? new Color(0f, 0f, 0f, alpha)
            : new Color(1f, 1f, 1f, alpha);
        DrawArrowQuad(pos, forward, right, 3f, 4f, 2.5f, 2f, contrast);
        DrawArrowQuad(pos, forward, right, 2f, 3f, 2f, 1f, WithAlpha(color, alpha));
    }

    /// <summary>The four-vertex kite the QC builds for a radar player: tip ahead, two wings to the sides-back, a
    /// tail behind — a chevron/arrowhead pointing along <paramref name="forward"/>.</summary>
    private void DrawArrowQuad(Vector2 c, Vector2 forward, Vector2 right,
        float tip, float wing, float wingBack, float tail, Color color)
    {
        // Mirrors draw_teamradar_player's 4 R_PolygonVertex calls:
        //   coord + forward*tip            (front tip)
        //   coord + right*wing - forward*wingBack   (right wing, set back)
        //   coord - forward*tail           (tail)
        //   coord - right*wing - forward*wingBack   (left wing, set back)
        var pts = new Vector2[]
        {
            c + forward * tip,
            c + right * wing - forward * wingBack,
            c - forward * tail,
            c - right * wing - forward * wingBack,
        };
        DrawColoredPolygon(pts, color);
    }

    /// <summary>
    /// Map a player's <see cref="NetEntityState.Colormap"/> to its radar tint — the C# port of QC
    /// <c>Team_ColorRGB(entcs_GetTeam(...))</c> (teamradar.qc <c>draw_teamradar_player</c>). The server writes
    /// <c>Colormap = (int)p.Team</c> (ServerNet), so the low byte carries the real NUM_TEAM_* palette code
    /// (<see cref="Teams.Red"/>=4, <see cref="Teams.Blue"/>=13, <see cref="Teams.Yellow"/>=12,
    /// <see cref="Teams.Pink"/>=9) — NOT a 1..4 index — so we switch on those constants and return the faithful
    /// <c>Team_ColorRGB</c> RGBs (common/teams.qh:76). Anything else (FFA / unknown) is white.
    /// </summary>
    private static Color TeamColor(int colormap)
    {
        int team = colormap & 0xFF;
        return team switch
        {
            Teams.Red    => new Color(1f, 0.0625f, 0.0625f, 1f),  // Team_ColorRGB NUM_TEAM_1 (0xFF0F0F)
            Teams.Blue   => new Color(0.0625f, 0.0625f, 1f, 1f),  // NUM_TEAM_2 (0x0F0FFF)
            Teams.Yellow => new Color(1f, 1f, 0.0625f, 1f),       // NUM_TEAM_3 (0xFFFF0F)
            Teams.Pink   => new Color(1f, 0.0625f, 1f, 1f),       // NUM_TEAM_4 (0xFF0FFF)
            _            => new Color(1f, 1f, 1f, 1f),            // white (no team / FFA)
        };
    }

    /// <summary>Parse <c>hud_panel_radar_maximized_size</c> ("w h", each a SCREEN fraction) and clamp to the QC
    /// 0.2..1 bound per axis (radar.qc teamradar_loadcvars). Defaults to 0.5 0.5 when unset / malformed.</summary>
    private Vector2 ParseMaximizedSize()
    {
        string s = CvarStr("maximized_size");
        float w = 0.5f, h = 0.5f;
        if (!string.IsNullOrWhiteSpace(s))
        {
            string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 &&
                float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pw) &&
                float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ph))
            {
                w = pw;
                h = ph;
            }
        }
        if (!float.IsFinite(w)) w = 0.5f;
        if (!float.IsFinite(h)) h = 0.5f;
        return new Vector2(Mathf.Clamp(w, 0.2f, 1f), Mathf.Clamp(h, 0.2f, 1f));
    }

    /// <summary>
    /// Invert <c>WorldToScreen</c> for the clickable maximized radar — the C# port of QC <c>HUD_Radar_Mouse</c>'s
    /// screen→world unproject used by the Onslaught spawn picker. Given a panel-local click point, undoes the
    /// recentre, the px-per-unit scale, the v_flipped/screen-Y flips, and the radar rotation (inverse uses
    /// <c>+yawRad</c>) to recover the world ground-plane (x, y); the world Z is taken from the radar centre origin
    /// (the server's <c>NearestControlPoint2D</c> only uses XY). Returns <c>false</c> (and leaves
    /// <paramref name="worldPoint"/> default) when the click falls outside the maximized rect or the cached
    /// transform is not usable.
    /// </summary>
    public bool HandleClick(Vector2 panelLocalPos, out NVec3 worldPoint)
    {
        worldPoint = default;
        if (!_maximized || !_clickValid)
            return false;

        // In-rect hit test: the maximized rect is centred on _clickCenter with _clickSize extent (QC ignores clicks
        // outside the radar rectangle).
        Vector2 half = _clickSize * 0.5f;
        if (panelLocalPos.X < _clickCenter.X - half.X || panelLocalPos.X > _clickCenter.X + half.X ||
            panelLocalPos.Y < _clickCenter.Y - half.Y || panelLocalPos.Y > _clickCenter.Y + half.Y)
            return false;

        // Undo (p - center) and the px-per-unit scale.
        float sx = (panelLocalPos.X - _clickCenter.X) / _clickWorldToPixels;
        float sy = (panelLocalPos.Y - _clickCenter.Y) / _clickWorldToPixels;
        // Undo the v_flipped X mirror (±1, self-inverse) and the screen-Y negate to recover the rotated world delta.
        float rrx = sx * _clickFlipX;
        float rry = -sy;
        // Inverse rotation: the forward rotated by -yawRad, so undo with +yawRad.
        float c = Mathf.Cos(_clickYawRad), s = Mathf.Sin(_clickYawRad);
        float ddx = rrx * c - rry * s;
        float ddy = rrx * s + rry * c;
        float wx = ddx + _clickSelfOrigin.X;
        float wy = ddy + _clickSelfOrigin.Y;
        if (!float.IsFinite(wx) || !float.IsFinite(wy))
            return false;

        // Server NearestControlPoint2D uses only XY; carry the radar-centre Z so the emitted point is well-formed.
        worldPoint = new NVec3(wx, wy, _clickSelfOrigin.Z);
        return true;
    }

    private static bool IsNearWhite(Color c) => c.R > 0.85f && c.G > 0.85f && c.B > 0.85f;

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, Mathf.Clamp(a, 0f, 1f));
}
