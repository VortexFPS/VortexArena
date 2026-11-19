using Godot;
using VortexArena.Common.Gameplay;     // Teams.Red/Blue/Yellow/Pink (NUM_TEAM_* palette values)
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Net;
using VortexArena.Net;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Hud;

/// <summary>
/// Draws the tactical minimap and networked player, waypoint and link markers.
/// The maximized Onslaught picker reuses the draw transform for its world-coordinate click.
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
    //  HUD_Radar_Mouse / HUD_Radar_Clickable, radar.qc). The ordinary maximized view is separate from mouse
    //  mode, which Onslaught uses as its spawn and teleport location selector.
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
    private Rect2 _normalRect;

    /// <summary>The transient mouse mode set by <c>cl_cmd hud clickradar</c>.</summary>
    public bool MouseMode { get; private set; }

    /// <summary>While dead, holding the scoreboard temporarily hides the click radar.</summary>
    public bool TemporarilyHidden { get; set; }

    public bool Clickable => _maximized && MouseMode && !TemporarilyHidden;

    /// <summary>True while maximized (QC <c>hud_panel_radar_maximized</c>).</summary>
    public bool Maximized => _maximized;

    /// <summary>Set the overview and optionally enter mouse mode. Hiding clears both modes.</summary>
    public void SetMaximized(bool on, bool clickable = false)
    {
        if (on != _maximized)
        {
            if (on) _normalRect = new Rect2(Position, Size);
            else
            {
                Position = _normalRect.Position;
                Size = _normalRect.Size;
            }
        }
        if (_maximized != on || clickable && !MouseMode)
            _clickValid = false;
        _maximized = on;
        MouseMode = on && (clickable || MouseMode);
        TemporarilyHidden = false;
        if (on) LayoutMaximized();
        QueueRedraw();
    }

    private void LayoutMaximized()
    {
        Vector2 screen = GetViewportRect().Size;
        Size = screen * ParseMaximizedSize();
        Position = (screen - Size) * 0.5f;
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

    /// <summary>Radar world bounds before the padded square texture projection.</summary>
    public Vector2 MapMinXY { get; set; }
    public Vector2 MapMaxXY { get; set; }

    private bool HasMapBounds => MapMaxXY.X > MapMinXY.X && MapMaxXY.Y > MapMinXY.Y;

    // DRAWFLAG_SCREEN uses (ONE_MINUS_DST_COLOR, ONE). RGB carries the radar foreground setting.
    private const string MinimapShader = """
        shader_type canvas_item;
        render_mode blend_disabled, unshaded;
        uniform sampler2D backdrop : hint_screen_texture, filter_nearest, repeat_disable;
        void fragment() {
            vec3 dst = texture(backdrop, SCREEN_UV).rgb;
            COLOR = vec4(dst + COLOR.rgb * (1.0 - dst), 1.0);
        }
        """;

    private Control _minimapBackdrop = null!;
    private BackBufferCopy _minimapCopy = null!;
    private Control _minimapLayer = null!;
    private bool _showBackdrop;
    private (Texture2D Texture, Vector2[] Points, Vector2[] Uvs, float Shade)? _minimapDraw;

    private Control _pingLayer = null!;
    private readonly System.Collections.Generic.List<(Rect2 Rect, Color Color)> _pingDraws = new();

    public override void _Ready()
    {
        ClipContents = true;
        _minimapBackdrop = new Control { MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true };
        _minimapBackdrop.Draw += () =>
        {
            if (_showBackdrop)
                DrawBackground(_minimapBackdrop);
        };
        AddChild(_minimapBackdrop);
        _minimapCopy = new BackBufferCopy { ShowBehindParent = true, Visible = false };
        AddChild(_minimapCopy);
        _minimapLayer = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ShowBehindParent = true,
            Material = new ShaderMaterial { Shader = new Shader { Code = MinimapShader } },
        };
        _minimapLayer.Draw += DrawMinimap;
        AddChild(_minimapLayer);
        _pingLayer = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        _pingLayer.Draw += DrawPingPulses;
        AddChild(_pingLayer);
    }

    // ---- visual constants (the modernized stand-ins for the QC hud_panel_radar_* skin cvars) ----

    private static readonly Color BackgroundColor = new(0.05f, 0.06f, 0.08f, 0.45f);
    private static readonly Color BorderColor = new(1f, 1f, 1f, 0.18f);

    // QC HUD_Radar_GetZoomFactor / scale constants. hud_panel_radar_scale is pixels-per (mi_scale) world unit in
    // the QC; without the loaded minimap bounds we treat _scale as "how many world units fill the radar width"
    // via a reference divisor so the cvar still meaningfully zooms. Larger _scale = wider world coverage.
    private const float ScaleReferenceDivisor = 4f; // tuned so the code default 4096 ≈ 1024u radius

    /// <summary>The radar reads live net state, so it must repaint every frame.</summary>
    public override void _Process(double delta)
    {
        if (_maximized) LayoutMaximized();
        QueueRedraw();
    }

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
        _showBackdrop = false;
        _minimapDraw = null;
        _minimapCopy.Hide();
        _minimapBackdrop.QueueRedraw();
        _minimapLayer.QueueRedraw();
        _pingDraws.Clear();
        _pingLayer?.QueueRedraw();
        ClientNet? net = Net;
        _clickValid = false;
        if (net is null || TemporarilyHidden)
            return; // self-blank: no data → draw nothing

        // ---- panel enable + team gating (QC HUD_Radar, radar.qc:191-194) ----
        // hud_panel_radar: 0 = disabled, 1 = enabled (team modes only), 2 = also enabled in non-team modes.
        // The standalone radar isn't driven by the HUD show-mode machinery, so honour the cvar here directly.
        int enable = Mathf.RoundToInt(GlobalF("hud_panel_radar", 1f));
        if (!_maximized && (enable == 0 || enable != 2 && !VortexArena.Common.Gameplay.Scoring.GameScores.Teamplay))
            return;

        // Draw in the live control size so this works both as a discovered panel (Size == Cfg.SizePx, set by
        // LoadConfig) and standalone (Size set manually by NetGame). panel-local space: origin = top-left.
        Vector2 size = Size;
        Vector2 center = size * 0.5f;

        float radius = Mathf.Min(size.X, size.Y) * 0.5f;
        if (radius <= 0f)
            return;

        // ---- skin border frame (QC HUD_Panel_DrawBg → draw_BorderPicture). No-op when the panel's bg is "0". ----
        _showBackdrop = !IsConfiguring;

        // Foreground alpha (QC hud_panel_radar_foreground_alpha * panel_fg_alpha). When discovered it folds the
        // HUD/scoreboard fade in via LiveFgAlpha; standalone LiveFgAlpha defaults to ~0.9, which is fine.
        // QC teamradar_loadcvars: a foreground_alpha of 0 (or unset) defaults to 0.8 — match that faithfully so a
        // skin that zeroes it still shows the radar rather than blanking. NaN/Inf cvars collapse to the default.
        float fgCvar = CvarF("foreground_alpha", 1f);
        if (!float.IsFinite(fgCvar) || fgCvar <= 0f)
            fgCvar = 0.8f;
        float panelAlpha = Mathf.Clamp(LiveFgAlpha, 0f, 1f);
        float fgAlpha = Mathf.Clamp(fgCvar, 0f, 1f) * panelAlpha;
        if (fgAlpha <= 0.001f)
            return;

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
        float scale = CvarF("scale", 4096f);
        if (_maximized)
        {
            float maximizedScale = CvarF("maximized_scale", 8192f);
            if (float.IsFinite(maximizedScale) && maximizedScale > 0f)
                scale = maximizedScale;
        }
        if (!float.IsFinite(scale) || scale <= 0f) scale = 4096f;
        float zoomCvar = _maximized ? CvarF("maximized_zoommode", 3f) : CvarF("zoommode", 0f);
        int zoommode = float.IsFinite(zoomCvar) ? (int)zoomCvar : 0;
        float zoomFactor = Mathf.Clamp(GetZoomFactor(zoommode), 0f, 1f);
        // QC HUD_Radar: the clickable spawn-selector forces the fit-arena view so the whole map is reachable and the
        // click→world inverse is well-defined (no per-player pan/zoom). Override after the zoommode resolve.
        if (Clickable)
            zoomFactor = 1f;

        Vector2 mapSpan = MapMaxXY - MapMinXY;
        // QC mi_picmin/mi_picmax extend the shorter side to a square, then pad both sides by 1/64.
        float pictureSpan = Mathf.Max(mapSpan.X, mapSpan.Y) * (1f + 2f / 64f);
        float worldToPixels;
        if (HasMapBounds)
        {
            // QC applies scale2d = pictureSpan in its fit, then divides by that same span in 3D->UV.
            // The factors cancel for world-to-screen scaling. They still affect pulse size and image UVs.
            Vector2 rotatedSpan = new(
                Mathf.Abs(cos) * mapSpan.X + Mathf.Abs(sin) * mapSpan.Y,
                Mathf.Abs(sin) * mapSpan.X + Mathf.Abs(cos) * mapSpan.Y);
            float bigsize = rotation == 0
                ? Mathf.Min(size.X, size.Y) / (1.05f * mapSpan.Length())
                : Mathf.Min(size.X / rotatedSpan.X, size.Y / rotatedSpan.Y) / 1.05f;
            float normalsize = Mathf.Max(bigsize, Mathf.Max(size.X, size.Y) / scale);
            worldToPixels = Mathf.Lerp(normalsize, bigsize, zoomFactor);
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
        // by the zoom factor. Retain camera height for the Onslaught click coordinates.
        NVec3 selfOrigin = viewOrigin;
        if (HasMapBounds && zoomFactor > 0f)
        {
            float cx = (MapMinXY.X + MapMaxXY.X) * 0.5f;
            float cy = (MapMinXY.Y + MapMaxXY.Y) * 0.5f;
            selfOrigin.X = zoomFactor * cx + (1f - zoomFactor) * viewOrigin.X;
            selfOrigin.Y = zoomFactor * cy + (1f - zoomFactor) * viewOrigin.Y;
        }
        // A non-finite radar center would poison the map and marker transforms.
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

        if (selfFinite && HasMapBounds && !string.IsNullOrEmpty(MapName))
        {
            Texture2D? mini = TextureCache.GetFirst($"gfx/{MapName}_radar", $"gfx/{MapName}_mini");
            if (mini is null)
            {
                DrawRadarFallback(size, fgAlpha);
                return;
            }

            // Draw just the raw map rectangle using its cropped UVs in the padded square image.
            Vector2 margin = (Vector2.One - mapSpan / pictureSpan) * 0.5f;
            var pts = new[]
            {
                WorldToScreen(MapMinXY.X, MapMinXY.Y), WorldToScreen(MapMaxXY.X, MapMinXY.Y),
                WorldToScreen(MapMaxXY.X, MapMaxXY.Y), WorldToScreen(MapMinXY.X, MapMaxXY.Y),
            };
            var uvs = new[]
            {
                new Vector2(margin.X, 1f - margin.Y), Vector2.One - margin,
                new Vector2(1f - margin.X, margin.Y), margin,
            };
            if (vFlipped)
            {
                System.Array.Reverse(pts);
                System.Array.Reverse(uvs);
            }
            if (AllFinite(pts))
            {
                _minimapDraw = (mini, pts, uvs, fgAlpha);
                _minimapCopy.Rect = new Rect2(Vector2.Zero, size);
                _minimapCopy.Show();
            }
        }

        else
        {
            DrawRadarFallback(size, fgAlpha);
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
                if (netId == localId || netId == net.SpectatingNetId)
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
            float pulseSize = HasMapBounds ? worldToPixels * pictureSpan : iconPx;
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

                foreach (float stamp in net.RadarPingTimes(wp.Id))
                {
                    float age = now - stamp;
                    if (age <= 0f || age >= 1f)
                        continue;
                    Vector2 extent = Vector2.One * (2f * pulseSize * age);
                    _pingDraws.Add((new Rect2(op - extent * 0.5f, extent), new Color(1f, 1f, 1f, (1f - age) * panelAlpha)));
                }
            }
        }

        // ---- maximized clickable spawn-selector overlay (QC HUD_Radar_Mouse, radar.qc): a hover-glow on the
        // nearest waypoint under the cursor + the "Click to select spawn location" prompt. Only in the clickable
        // mouse mode. ----
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
            string prompt = net.Health <= 0 ? "Click to select spawn location" : "Click to select teleport destination";
            DrawTextCentered(new Vector2(0, 4f), size.X, prompt, new Color(1f, 1f, 1f, fgAlpha));
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
        NVec3 playerOrigin = net.PredictedOrigin;
        if (net.SpectatingNetId != 0 && net.SampleRemote(net.SpectatingNetId, now, out NVec3 spectated, out _))
            playerOrigin = spectated;
        Vector2 localPos = center;
        if (selfFinite && float.IsFinite(playerOrigin.X) && float.IsFinite(playerOrigin.Y))
        {
            Vector2 lp = WorldToScreen(playerOrigin.X, playerOrigin.Y);
            if (float.IsFinite(lp.X) && float.IsFinite(lp.Y))
                localPos = lp;
        }
        DrawPlayerArrow(localPos, localYaw, radarYawDeg, LocalColorTint, fgAlpha, isLocal: true, flipX);

        // Rectangular frame edge (subtle), drawn last inside the clip so it reads over the map image.
        DrawRect(new Rect2(Vector2.One, size - new Vector2(2f, 2f)),
            WithAlpha(BorderColor, BorderColor.A * fgAlpha / 0.9f), filled: false, width: 1.5f);
    }

    private void DrawRadarFallback(Vector2 size, float alpha)
    {
        DrawRect(new Rect2(Vector2.Zero, size), WithAlpha(BackgroundColor, BackgroundColor.A * alpha / 0.9f));
    }

    private void DrawMinimap()
    {
        if (_minimapDraw is not { } draw)
            return;
        Color shade = new(draw.Shade, draw.Shade, draw.Shade);
        _minimapLayer.DrawPolygon(draw.Points, new[] { shade, shade, shade, shade }, draw.Uvs, draw.Texture);
    }

    private void DrawPingPulses()
    {
        if (_pingDraws.Count == 0 || TextureCache.Get("gfx/teamradar_ping") is not { } texture)
            return;
        foreach (var (rect, color) in _pingDraws)
            _pingLayer.DrawTextureRect(texture, rect, false, color);
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
        if (!Clickable || !_clickValid)
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
