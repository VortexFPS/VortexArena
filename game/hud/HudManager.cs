using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The CSQC on-screen HUD root — the C# successor to QuakeC's <c>HUD_Main</c> / <c>HUD_Draw</c>
/// (Base/.../qcsrc/client/hud/hud.qc), which each frame walked the registered <c>hud_panels</c> and called
/// every panel's draw. Here a <see cref="CanvasLayer"/> hosts one child <see cref="HudPanel"/> per panel; Godot
/// composites them over the 3D view, so this manager creates the panels (now via reflection discovery —
/// <see cref="HudRegistry"/>), hands them the local <see cref="Player"/>, resolves each panel's cvar-driven
/// layout/skin each frame (<see cref="HudPanel.LoadConfig"/>), and pokes the dynamic ones to redraw.
///
/// Discovery replaces the old hand-maintained <c>new XxxPanel()</c> list: a panel exists by being a
/// <see cref="HudPanel"/> subclass, so new panels drop in without editing this file. Strongly-typed handles the
/// net/match layer feeds (<see cref="Scoreboard"/>, <see cref="CenterPrint"/>, …) are resolved from the
/// discovered set via <see cref="Get{T}"/>.
///
/// Layout/look come from the <c>hud_panel_&lt;id&gt;_*</c> cvars (luma defaults baked in <see cref="HudLayoutDefaults"/>)
/// resolved per-frame, NOT a hardcoded anchor table — so a console <c>set</c> or the menu HUD dialogs move/skin
/// panels live.
///
/// Port extras kept: <see cref="FpsPanel"/>/<see cref="PingPanel"/> (self-managing their own visibility — exempt
/// from the gating loop), <see cref="MinigameRenderer"/>/<see cref="MinigameMenu"/> (click-capturing, added
/// directly, not panels), the Pong key-drive, and the <c>FrameProfiler</c> scope.
/// </summary>
public partial class Hud : CanvasLayer
{
    /// <summary>The local player whose state the HUD reflects (QC <c>spectatee</c> / the view player).</summary>
    public Player? Player { get; private set; }

    // Strongly-typed handles the net/match layer commonly feeds data into (resolved from the registry).
    public HealthArmorPanel HealthArmor { get; private set; } = null!;
    public AmmoPanel Ammo { get; private set; } = null!;
    public WeaponsPanel Weapons { get; private set; } = null!;
    public PowerupsPanel Powerups { get; private set; } = null!;
    public ScoreboardPanel Scoreboard { get; private set; } = null!;
    public CenterPrintPanel CenterPrint { get; private set; } = null!;
    public NotifyPanel Notify { get; private set; } = null!;
    public CrosshairPanel Crosshair { get; private set; } = null!;
    public TimerPanel Timer { get; private set; } = null!;
    public InfoMessagesPanel InfoMessages { get; private set; } = null!;
    public VehicleHud Vehicle { get; private set; } = null!;
    public RaceTimerPanel RaceTimer { get; private set; } = null!;
    public CheckpointsPanel Checkpoints { get; private set; } = null!;
    public VotePanel Vote { get; private set; } = null!;
    public ModIconsPanel ModIcons { get; private set; } = null!;
    public ItemsTimePanel ItemsTime { get; private set; } = null!;
    public PhysicsPanel Physics { get; private set; } = null!;
    public MapVotePanel MapVote { get; private set; } = null!;
    public FpsPanel Fps { get; private set; } = null!;
    public PingPanel Ping { get; private set; } = null!;
    public PositionPanel Position { get; private set; } = null!;

    /// <summary>The active-minigame board overlay (CSQC minigame board draw + click-to-move). Not a HudPanel.</summary>
    public MinigameRenderer Minigame { get; private set; } = null!;

    /// <summary>The in-game minigame menu (CSQC HUD_MinigameMenu_*). Not a HudPanel.</summary>
    public MinigameMenu MinigameMenu { get; private set; } = null!;

    /// <summary>The HUD configure-mode editor overlay (CSQC <c>hud_config.qc</c>). Active while
    /// <c>_hud_configure 1</c>; drives panel drag/resize/keyboard editing + the grid/highlight draw. Not a
    /// HudPanel (it reads input via <see cref="HudConfigEditor.HandleInput"/>, routed from the net layer).</summary>
    public HudConfigEditor ConfigEditor { get; private set; } = null!;

    /// <summary>Read-only view of the live panels (for the configure-mode editor, which hit-tests + drag/resizes
    /// them via their <c>hud_panel_&lt;id&gt;_*</c> cvars). Mirrors QC's <c>hud_panels</c> registry walk.</summary>
    public IReadOnlyList<HudPanel> Panels => _panels;

    /// <summary>Scoreboard cross-fade (0 = not shown … 1 = fully up). The net/match layer drives it; non-WITH_SB
    /// panels fade their alpha by <c>1 − ScoreboardFade</c> (QC <c>panel_fade_alpha</c>), without losing Visible.</summary>
    public float ScoreboardFade { get; set; }

    /// <summary>Global HUD fade (QC <c>hud_fade_alpha</c>; 1 = opaque). Settable by the host if it wants the HUD
    /// to dim under a menu; defaults to fully on.</summary>
    public float HudFadeAlpha { get; set; } = 1f;

    /// <summary>QC <c>intermission</c> — true at the end-of-match screen. Fed by the host; gates off new damage
    /// shakes (QC <c>Hud_Dynamic_Frame</c> only triggers a shake when <c>!intermission</c>). Defaults false
    /// (normal play), so the effect works without any host wire-up.</summary>
    public bool Intermission { get; set; }

    /// <summary>QC <c>spectatee_status == -1</c>: the local view is a free-fly observer (not playing, not
    /// following anyone). Fed by the host each frame; gates the physics/strafehud "even observing" show-modes
    /// (their <c>hud_panel_&lt;id&gt;</c> 1/3 hide while observing, 2/4 keep showing). Defaults false (normal play).</summary>
    public bool Observing { get; set; }

    /// <summary>The damage-keyed whole-HUD shake (QC <c>Hud_Dynamic_Frame</c> shake block + <c>Hud_Shake_Update</c>).
    /// Driven each frame in <see cref="_Process"/> from the local player's health + the HUD clock.</summary>
    private readonly HudDynamicShake _dynamicShake = new();

    /// <summary>The viewmodel's current <c>calc_followmodel_ofs</c> result (QC <c>cl_followmodel_ofs</c>,
    /// view-space fwd/right/up), fed each frame by the host from the live <c>ViewModel.LastFollowOffset</c>. Drives
    /// the <c>hud_dynamic_follow</c> half of <c>Hud_Dynamic_Frame</c> — the HUD shift/scale with the weapon bob.
    /// Zero (the default) leaves the HUD un-swayed, so the effect is inert until both fed and the cvar is on.</summary>
    public System.Numerics.Vector3 FollowModelOffset { get; set; }

    /// <summary>The HUD clock (QC <c>time</c>) the shake decays against. The full HUD has no slaved sim clock
    /// here, so — as <see cref="HealthArmorPanel"/> does for its timed effects — we accumulate real frame delta.</summary>
    private double _shakeClock;

    private readonly List<HudPanel> _panels = new();

    // Panels that drive their OWN Visible + redraw in their own _Process (port extras) — the manager only
    // refreshes their cvar layout, it never sets their Visible (else it would fight them frame-to-frame).
    private static readonly HashSet<Type> SelfManaged = new() { typeof(FpsPanel), typeof(PingPanel), typeof(PositionPanel) };

    // Panels conditionally shown by the net/match layer or by a gameplay event (kept hidden until shown), and
    // the newly-discovered Radar (no data wiring yet). Matches the old manager's `{ Visible = false }` set.
    // NOTE: "physics" is NOT here — it is a cvar-gated always-evaluated panel (QC HUD_Physics), so the per-frame
    // loop below owns its Visible via ResolveVisible (hud_panel_physics show-mode), not the net layer.
    private static readonly HashSet<string> StartHiddenIds = new()
    {
        "racetimer", "checkpoints", "vote", "modicons", "itemstime", "mapvote",
        "vehicle", "fps", "ping", "position", "radar",
    };

    private string _skin = "luma";

    /// <summary>Last frame's <c>_hud_configure</c> state — detects the configure-mode entry/exit edge so the
    /// panels get one full re-resolve + repaint and the event-gated panels restore their hidden state on exit.</summary>
    private bool _configuringPrev;

    /// <summary>The full-screen HUD dock background (QC HUD_Main dock-draw block). Added first so it composites
    /// behind every panel; self-blanks unless <c>hud_dock</c> is enabled. Driven each frame from the HUD fade.</summary>
    public HudDock Dock { get; private set; } = null!;

    public override void _Ready()
    {
        // The HUD dock background draws behind every panel (QC draws it before the panel walk). Add it FIRST so
        // it's the lowest-z child; it self-blanks while hud_dock is off (the default).
        Dock = new HudDock { Name = "Dock" };
        AddChild(Dock);

        // Discover + create every panel (QC registered them at load). Reflection runs once (HudRegistry).
        foreach (Type t in HudRegistry.PanelTypes)
            Add(HudRegistry.Create(t));

        // Resolve the strongly-typed handles the net/match layer feeds.
        HealthArmor  = Get<HealthArmorPanel>();
        Ammo         = Get<AmmoPanel>();
        Weapons      = Get<WeaponsPanel>();
        Powerups     = Get<PowerupsPanel>();
        Scoreboard   = Get<ScoreboardPanel>();
        CenterPrint  = Get<CenterPrintPanel>();
        Notify       = Get<NotifyPanel>();
        Crosshair    = Get<CrosshairPanel>();
        Timer        = Get<TimerPanel>();
        InfoMessages = Get<InfoMessagesPanel>();
        Vehicle      = Get<VehicleHud>();
        RaceTimer    = Get<RaceTimerPanel>();
        Checkpoints  = Get<CheckpointsPanel>();
        Vote         = Get<VotePanel>();
        ModIcons     = Get<ModIconsPanel>();
        ItemsTime    = Get<ItemsTimePanel>();
        Physics      = Get<PhysicsPanel>();
        MapVote      = Get<MapVotePanel>();
        Fps          = Get<FpsPanel>();
        Ping         = Get<PingPanel>();
        Position     = Get<PositionPanel>();

        // Apply initial visibility (the conditionally-shown panels start hidden; net layer shows them).
        foreach (HudPanel p in _panels)
            if (StartHiddenIds.Contains(p.PanelId))
                p.Visible = false;

        // The minigame board overlay + menu aren't HudPanels (they capture clicks/keys) — add them directly.
        Minigame = new MinigameRenderer { Name = "Minigame" };
        AddChild(Minigame);
        MinigameMenu = new MinigameMenu { Name = "MinigameMenu" };
        AddChild(MinigameMenu);

        SyncSkin();

        // The HUD configure-mode editor overlay (QC hud_config.qc). Added last so it draws over every panel
        // (QC HUD_Configure_PostDraw runs after the panel draws). Input is routed in from NetGame._UnhandledInput.
        ConfigEditor = new HudConfigEditor(this);
        AddChild(ConfigEditor);

        ApplyPlayer();

        GetViewport().SizeChanged += OnViewportResized;
    }

    /// <summary>Point the HUD at the local player actor. Pass <c>null</c> to blank the HUD (pre-spawn/observing).</summary>
    public void SetPlayer(Player? player)
    {
        // A view change (respawn / spectatee switch) jumps health discontinuously; QC suppressed the shake for the
        // next frame in that case (HUD_Reset / main.qc:750 set hud_dynamic_shake_factor = -1). Mirror that so the
        // jump from 0/old-health to the new player's health doesn't trigger a spurious whole-HUD shake.
        if (!ReferenceEquals(player, Player))
            _dynamicShake.RequestReset();
        Player = player;
        ApplyPlayer();
    }

    /// <summary>Sink for Pong's paddle drive command (wired by the net layer). Bits = PONG_KEY bitset.</summary>
    public System.Action<int>? PongMoveSink { get; set; }

    private int _pongKeys;
    private int _pongKeysOld = -1;

    public override void _Process(double delta)
    {
        using var _hudScope = XonoticGodot.Game.Client.FrameProfiler.Scope("hud.mgr");

        SyncSkin();
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float fade = Mathf.Clamp(HudFadeAlpha, 0f, 1f);
        float sbFade = Mathf.Clamp(ScoreboardFade, 0f, 1f);

        // Drive the HUD dock background (QC HUD_Main dock block): it draws at hud_dock_alpha * hud_fade_alpha,
        // tinted by the local team color when hud_dock_color_team + teamplay. Self-blanks while hud_dock is off.
        if (Dock is not null)
        {
            Dock.HudFadeAlpha = fade;
            Dock.TeamColor = Player is { } dp && XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay
                ? HudDock.TeamRgb((int)dp.Team)
                : (Color?)null;
        }

        // Resolve the local player's colormap shirt/pants + team colors once per frame (QC HUD_Panel_GetColor's
        // "shirt"/"pants"/"team" literals read _cl_color = clientcolors = 16*shirt+pants). Fed to the static so any
        // panel's hud_panel_<id>_bg_color and the dock's hud_dock_color can tint to the player's own colors. Null
        // when there is no local player (pre-spawn / pure --connect) — the literals then fall back to inherit.
        if (Player is { } cp)
        {
            int cm = cp.ClientColors;
            (float r, float g, float b) sh = XonoticGodot.Engine.Simulation.CsqcModelAppearance.ColormapPaletteColor(
                (cm >> 4) & 0x0F, isPants: false, (float)_shakeClock);
            (float r, float g, float b) pa = XonoticGodot.Engine.Simulation.CsqcModelAppearance.ColormapPaletteColor(
                cm & 0x0F, isPants: true, (float)_shakeClock);
            HudPanel.LocalShirtColor = new Color(sh.r, sh.g, sh.b);
            HudPanel.LocalPantsColor = new Color(pa.r, pa.g, pa.b);
            HudPanel.LocalTeamColor = XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay
                ? HudDock.TeamRgb((int)cp.Team) : (Color?)null;
        }
        else
        {
            HudPanel.LocalShirtColor = HudPanel.LocalPantsColor = HudPanel.LocalTeamColor = null;
        }

        // Damage-keyed whole-HUD shake (QC Hud_Dynamic_Frame): nudge the HUD root by the per-frame shake offset.
        // QC added hud_dynamic_shake_realofs to hud_shift_current, which every panel's draw applied to its origin;
        // here the equivalent is shifting the whole CanvasLayer via its Offset (one move, all panels follow).
        // Read STAT(HEALTH) off the local player (HealthArmorPanel reads the same RES_HEALTH); with no player
        // (pre-spawn/observing) pass 0 — RequestReset() on (re)spawn masks the resulting health jump.
        _shakeClock += delta;
        // Publish the shared HUD clock (QC `time`) so every panel's NumColor blink/pulse (and any other
        // wall-clock HUD animation) advances in lockstep with the shake clock.
        HudPanel.HudClock = (float)_shakeClock;
        float health = Player is { } pl ? pl.GetResource(ResourceType.Health) : 0f;
        System.Numerics.Vector2 shake = _dynamicShake.Update(
            health, (float)_shakeClock, new System.Numerics.Vector2(vp.X, vp.Y), Intermission);

        // hud_dynamic_follow (QC Hud_Dynamic_Frame follow block, hud.qc:595-617): sway/scale the whole HUD with
        // the viewmodel's followmodel offset. hud_shift_current/hud_scale_current map to the CanvasLayer's
        // Offset/Scale (one move/scale, all panels follow). Combined with the shake offset above.
        ComputeDynamicFollow(vp, out Vector2 followShift, out Vector2 followScale);

        Offset = new Vector2(shake.X, shake.Y) + followShift;
        Scale = followScale;

        // hud_panel_<id> show gate (QC HUD_Panel_CheckFlags + each panel's top-of-draw enable test): resolve the
        // live gametype/observer/configure context once, then let each user-toggleable panel decide whether its
        // cvar permits drawing. The manager OWNS Visible for the always-on panels — so `set hud_panel_<id> 0` or
        // the physics/strafehud Race/CTS show-modes hide/show them live — while net/event-gated panels
        // (StartHiddenIds) keep their data-driven Visible but are still force-hidden when their cvar disables them.
        string gametype = XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype;
        var showCtx = new HudShowContext(
            RaceOrCts: gametype is "rc" or "cts",
            Observing: Observing,
            Configuring: global::XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat("_hud_configure") != 0f);

        // HUD configure-mode entry/exit edge (QC HUD_Configure_Frame pokes every panel's update_time on entry):
        // re-resolve + repaint every panel so the editor-mode bg/alpha overrides (HudPanel.Resolve) apply the
        // frame the mode flips — and on exit, drop the net/event-gated MAIN panels back to hidden (the net
        // layer re-shows them when their data next demands it). Editor-gated only; no normal-draw behavior.
        if (showCtx.Configuring != _configuringPrev)
        {
            foreach (HudPanel p in _panels)
            {
                p.InvalidateConfig();
                p.QueueRedraw();
                if (!showCtx.Configuring && StartHiddenIds.Contains(p.PanelId)
                    && p.ConfigFlags.HasFlag(PanelConfig.Main))
                    p.Visible = false;
            }
            _configuringPrev = showCtx.Configuring;
        }

        foreach (HudPanel p in _panels)
        {
            // Port extras own their visibility/redraw — just keep their cvar layout fresh (full-viewport).
            if (SelfManaged.Contains(p.GetType()))
            {
                p.LoadConfig(vp, 1f, 1f);
                continue;
            }

            // Apply the cvar show gate to user-toggleable panels (PANEL_CONFIG_CANBEOFF).
            if (p.ConfigFlags.HasFlag(PanelConfig.CanBeOff))
            {
                if (!p.ResolveVisible(showCtx))
                {
                    if (p.Visible) p.Visible = false; // cvar / show-mode says off → hide, regardless of data
                    continue;
                }
                // cvar permits showing: assert Visible for the always-on panels; net/event-gated panels keep the
                // net layer's data-driven Visible (it runs this frame in the parent's _Process, before this child's).
                if (!StartHiddenIds.Contains(p.PanelId) && !p.Visible)
                    p.Visible = true;
                // HUD configure mode shows EVERY editable panel (Base draws all registered MAIN panels with
                // forced backgrounds while _hud_configure) — assert per frame because the net layer re-hides
                // its event panels (no vote/race running) each frame in the parent's _Process.
                else if (showCtx.Configuring && !p.Visible && StartHiddenIds.Contains(p.PanelId)
                         && p.ConfigFlags.HasFlag(PanelConfig.Main))
                    p.Visible = true;
            }

            if (!p.Visible) continue;

            // Non-scoreboard panels fade out as the scoreboard fades in (QC panel_fade_alpha).
            float panelFade = p.ShowFlags.HasFlag(PanelShow.WithScoreboard) ? 1f : 1f - sbFade;

            // (R8) While the scoreboard is fully up a non-WithScoreboard panel renders at ~0 alpha. Each panel's
            // NeedsRedraw carries the fade alpha, so it has already drawn its final near-transparent frame on the
            // way down; stop re-resolving (LoadConfig) and re-recording it entirely until the scoreboard begins
            // closing (panelFade rises). Crosshair-style panels draw with their OWN alpha (crosshair_alpha), not
            // panelFade, so they must keep updating — never skip those.
            if (panelFade <= 0.004f && !p.DrawsWithOwnAlpha)
                continue;

            p.LoadConfig(vp, fade, panelFade);

            // HUD configure mode: repaint every frame so the forced-frame pre-pass (HudPanel._Draw) tracks the
            // live drag/resize/alpha edits regardless of each panel's own NeedsRedraw throttle (editor-only
            // path; the throttle resumes the frame the editor exits).
            if (showCtx.Configuring)
            {
                p.QueueRedraw();
                continue;
            }

            // Re-record the canvas item only when the panel's displayed content actually changed (3.2-3) — most
            // dynamic panels return true (animate every frame), but the value readouts (timer/race timer/health/
            // armor/ammo/weapons) gate on an actual change so we don't re-run DrawPanel 160×/s for static content.
            if (p.IsDynamic && p.NeedsRedraw())
                p.QueueRedraw();
        }

        // Tick the configure-mode editor (QC HUD_Configure_Frame + HUD_Panel_Mouse + the PostDraw scheduling).
        // Runs after the panel loop so it sees this frame's resolved layout; it self-gates on _hud_configure.
        ConfigEditor.Update(vp, fade);

        DrivePongKeys();
    }

    // -------------------------------------------------------------------------------------------------
    //  Internals
    // -------------------------------------------------------------------------------------------------

    // hud_dynamic_follow cvar defaults (Base _hud_common.cfg:282-284).
    private const float DefFollow = 0f;
    private const float DefFollowScale = 0.01f;

    /// <summary>
    /// QC <c>Hud_Dynamic_Frame</c> follow block (hud.qc:595-617): turn the viewmodel's <c>cl_followmodel_ofs</c>
    /// (view-space fwd/right/up, here <see cref="FollowModelOffset"/>) into the HUD's <c>hud_shift_current</c>
    /// (pixel offset) + <c>hud_scale_current</c>. Returns identity (no shift, unit scale) when
    /// <c>hud_dynamic_follow</c> is off (the Base default) so the effect is inert until enabled.
    /// </summary>
    private void ComputeDynamicFollow(Vector2 viewport, out Vector2 shift, out Vector2 scale)
    {
        shift = Vector2.Zero;
        scale = Vector2.One;

        if (CvarOr("hud_dynamic_follow", DefFollow) == 0f)
            return;

        float followScale = CvarOr("hud_dynamic_follow_scale", DefFollowScale);
        System.Numerics.Vector3 scaleXyz = CvarVec3("hud_dynamic_follow_scale_xyz", System.Numerics.Vector3.One);

        // QC: ofs = -cl_followmodel_ofs * scale; then per-axis remap by scale_xyz (z→x, x→y, y→z).
        System.Numerics.Vector3 src = FollowModelOffset;
        float ox = -src.X * followScale * scaleXyz.Z;
        float oy = -src.Y * followScale * scaleXyz.X;
        float oz = -src.Z * followScale * scaleXyz.Y;

        // QC: snap tiny values to 0, then bound each axis to ±0.1.
        if (MathF.Abs(ox) < 0.001f) ox = 0f;
        if (MathF.Abs(oy) < 0.001f) oy = 0f;
        if (MathF.Abs(oz) < 0.001f) oz = 0f;
        ox = Mathf.Clamp(ox, -0.1f, 0.1f);
        oy = Mathf.Clamp(oy, -0.1f, 0.1f);
        oz = Mathf.Clamp(oz, -0.1f, 0.1f);

        // QC: hud_shift_current.x = ofs.y * conwidth; .y = ofs.z * conheight; .z = ofs.x (the scale term).
        float shiftZ = ox;
        shift = new Vector2(oy * viewport.X, oz * viewport.Y);
        // QC: hud_scale_current.x = hud_scale_current.y = 1 + hud_shift_current.z.
        float s = 1f + shiftZ;
        scale = new Vector2(s, s);
    }

    private static float CvarOr(string name, float def)
    {
        string raw = global::XonoticGodot.Game.Menu.MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(raw) ? def : global::XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat(name);
    }

    private static System.Numerics.Vector3 CvarVec3(string name, System.Numerics.Vector3 def)
    {
        string raw = global::XonoticGodot.Game.Menu.MenuState.Cvars.GetString(name);
        if (string.IsNullOrWhiteSpace(raw)) return def;
        string[] p = raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return def;
        if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            return def;
        return new System.Numerics.Vector3(x, y, z);
    }

    /// <summary>Apply the <c>hud_skin</c> cvar live: on change, point the skin folder at it + clear the texture
    /// cache so panels reload art, and re-resolve every panel's config.</summary>
    private void SyncSkin()
    {
        string skin = global::XonoticGodot.Game.Menu.MenuState.Cvars.GetString("hud_skin");
        if (string.IsNullOrWhiteSpace(skin)) skin = "luma";
        if (skin == _skin) return;
        _skin = skin;
        HudSkin.SkinName = skin;
        TextureCache.Clear();
        foreach (HudPanel p in _panels) p.InvalidateConfig();
    }

    private void OnViewportResized()
    {
        foreach (HudPanel p in _panels) p.InvalidateConfig();
    }

    private T Get<T>() where T : HudPanel
    {
        foreach (HudPanel p in _panels)
            if (p is T match) return match;
        return null!;
    }

    /// <summary>Public accessor for a discovered panel by type (the net/match layer feeds the newer panels —
    /// Score/Pickup/Chat/StrafeHud/QuickMenu/PressedKeys/MinigameHelp — that have no dedicated typed handle).
    /// Returns null if no such panel was discovered.</summary>
    public T? GetPanel<T>() where T : HudPanel
    {
        foreach (HudPanel p in _panels)
            if (p is T match) return match;
        return null;
    }

    /// <summary>Force a panel visible/hidden regardless of the StartHidden default (net/match layer gate). The
    /// per-frame loop preserves whatever Visible state callers set (it never seizes Visible for normal panels).</summary>
    public void SetPanelVisible<T>(bool visible) where T : HudPanel
    {
        T? p = GetPanel<T>();
        if (p is not null) p.Visible = visible;
    }

    private void Add(HudPanel panel)
    {
        panel.MouseFilter = Control.MouseFilterEnum.Ignore; // HUD never eats input (QC hud_cursormode off)
        AddChild(panel);
        _panels.Add(panel);
    }

    private void ApplyPlayer()
    {
        if (HealthArmor is null) return; // not built yet (SetPlayer called before _Ready)
        HealthArmor.Player = Player;
        Ammo.Player        = Player;
        Weapons.Player     = Player;
        Powerups.Player    = Player;
        InfoMessages.Player = Player;
        Crosshair.Player   = Player;
        Physics.Player     = Player;
        Scoreboard.LocalPlayer = Player;
    }

    private void DrivePongKeys()
    {
        bool pongShown = PongMoveSink is not null
            && Minigame is { Visible: true, Session: { } s } && s.Game.NetName == "pong"
            && (MinigameMenu is null || !MinigameMenu.Visible);

        int keys = 0;
        if (pongShown)
        {
            if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.Left)) keys |= 0x02;
            if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.Right)) keys |= 0x01;
        }
        _pongKeys = keys;
        if (_pongKeys != _pongKeysOld)
        {
            if (pongShown || _pongKeysOld > 0)
                PongMoveSink?.Invoke(_pongKeys);
            _pongKeysOld = _pongKeys;
        }
    }
}
