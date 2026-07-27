using System.Globalization;
using Godot;
using XonoticGodot.Game.Menu;   // MenuState.Cvars — the shared menu/console store (live console `set` reaches it)
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Base for every on-screen HUD panel — the C# successor to QuakeC's <c>HUDPanel</c> class plus the
/// <c>REGISTER_HUD_PANEL</c> machinery (Base/.../qcsrc/client/hud/hud.qh + panel/*.qc). A panel is a Godot
/// <see cref="Control"/> whose <see cref="_Draw"/> issues immediate-mode draw calls in panel-local space
/// (origin = the panel's top-left), exactly as the QC panels added <c>panel_pos</c> to every draw call.
///
/// This is now skin- and cvar-aware (the C# port of the luma HUD skin + the <c>hud_panel_&lt;id&gt;_*</c> cvar
/// layout system, see planning/HUD_PARITY_CONTRACT.md):
/// <list type="bullet">
///   <item>Each panel <b>self-declares</b> its identity + look via virtuals (<see cref="PanelId"/>,
///         <see cref="DefaultLayout"/>, <see cref="ShowFlags"/>, <see cref="DefaultBg"/>…) that DEFAULT to the
///         luma reference table (<see cref="HudLayoutDefaults"/>). A panel needs zero extra code to land at the
///         correct Xonotic position with the correct skin frame — it is discovered by <see cref="HudRegistry"/>
///         and laid out from its cvars automatically.</item>
///   <item><see cref="LoadConfig"/> (called once per frame by <see cref="Hud"/>) resolves a <see cref="Cfg"/>
///         snapshot — pixel pos/size, bg image/color/alpha/border, padding, fg alpha, font size — from the
///         <c>hud_panel_&lt;id&gt;_*</c> cvars (with the QC "" = inherit / "0" = off rules) and pushes
///         <c>Position</c>/<c>Size</c>. Throttled: it only re-resolves when a <c>hud_*</c> cvar changed or the
///         viewport resized, so a live console <c>set</c> moves the panel immediately.</item>
///   <item><see cref="DrawBackground()"/> paints the skin 9-slice frame (<see cref="HudSkin"/>); text uses the
///         Xolonium <see cref="HudFont"/>.</item>
/// </list>
/// </summary>
/// <summary>The live HUD show context the manager resolves once per frame and feeds to
/// <see cref="HudPanel.ResolveVisible"/>, so panels can honour the gametype- and observer-sensitive show-modes
/// (the physics/strafehud <c>hud_panel_&lt;id&gt;</c> 1/2/3/4 modes).</summary>
/// <param name="RaceOrCts">The active gametype is Race or CTS (QC <c>ISGAMETYPE(RACE) || ISGAMETYPE(CTS)</c>).</param>
/// <param name="Observing">The local view is a free-fly observer (QC <c>spectatee_status == -1</c>).</param>
/// <param name="Configuring">The HUD configure editor is active (QC <c>autocvar__hud_configure</c>).</param>
public readonly record struct HudShowContext(bool RaceOrCts, bool Observing, bool Configuring);

public abstract partial class HudPanel : Control
{
    // ---- font (Xolonium), wired by the host where TextureCache.VfsResolver is set ----

    /// <summary>The Xolonium HUD font (QC the engine's <c>FONT_USER</c>). Set by the host (NetGame/GameDemo);
    /// falls back to Godot's default font until then so nothing crashes.</summary>
    public static Font? HudFont { get; set; }

    /// <summary>The font panels draw with — Xolonium when wired, else the engine fallback.</summary>
    protected static Font Font => HudFont ?? ThemeDB.FallbackFont;

    // ---- compile-time defaults kept for method signatures (the live values come from Cfg) ----

    /// <summary>Default text size in px used as the <c>DrawText(... , size = FontSize)</c> signature default.
    /// Live body text should prefer <see cref="PanelConfig2.FontSize"/> (<see cref="Cfg"/>).</summary>
    protected const int FontSize = 16;

    /// <summary>Legacy padding constant (compile-time default). Live code reads <see cref="PanelConfig2.Padding"/>.</summary>
    protected const float Padding = 6f;

    /// <summary>Live foreground/text color (white at the resolved fg alpha × fade). Was a constant; now tracks
    /// <see cref="LiveFgAlpha"/> so all panels honour <c>hud_panel_fg_alpha</c> and the HUD fade.</summary>
    protected Color FgColor => new(1f, 1f, 1f, LiveFgAlpha);

    /// <summary>Live panel background color (the resolved skin bg color at the resolved bg alpha × fade).</summary>
    protected Color BgColor => new(Cfg.BgColor.R, Cfg.BgColor.G, Cfg.BgColor.B, LiveBgAlpha);

    // =================================================================================================
    //  Self-declaration surface (defaults look themselves up in the luma table by PanelId)
    // =================================================================================================

    private string? _panelId;

    /// <summary>QC <c>panel.panel_name</c>. The cvar prefix is <c>hud_panel_</c> + this. Defaults to the class
    /// name minus a trailing <c>Panel</c>/<c>Hud</c>, lowercased (HealthArmorPanel → "healtharmor").</summary>
    public virtual string PanelId => _panelId ??= HudLayoutDefaults.DeriveId(GetType());

    /// <summary>QC <c>PANEL_SHOW_*</c> — the contexts this panel may draw in. Defaults to the luma table.</summary>
    public virtual PanelShow ShowFlags => HudLayoutDefaults.For(PanelId).Show;

    /// <summary>QC <c>PANEL_CONFIG_*</c> — whether the user may disable / the editor may move it.</summary>
    public virtual PanelConfig ConfigFlags => HudLayoutDefaults.For(PanelId).Config;

    /// <summary>The integer value of this panel's master <c>hud_panel_&lt;id&gt;</c> show cvar (0 = off). For the
    /// physics/strafehud panels this is a multi-value show-mode (see <see cref="ResolveShowMode"/>); for the rest
    /// it is a plain 0/1 toggle.</summary>
    protected int ShowModeCvar() => Mathf.RoundToInt(GlobalF("hud_panel_" + PanelId, 0f));

    /// <summary>
    /// QC per-panel show gate — does <c>hud_panel_&lt;id&gt;</c> (plus any multi-value show-mode) permit this panel
    /// to draw right now? The <see cref="Hud"/> manager calls this each frame for user-toggleable panels
    /// (<see cref="PanelConfig.CanBeOff"/>) and drives <see cref="CanvasItem.Visible"/> from it, so a console/menu
    /// edit hides or shows the panel live. Default: the plain on/off master toggle (always-permitted for
    /// non-CANBEOFF panels); the HUD configure editor (<c>_hud_configure</c>) forces every panel on so it can be
    /// dragged. <see cref="PhysicsPanel"/> and <see cref="StrafeHudPanel"/> override this to add the QC race/cts +
    /// observing show-modes.
    /// </summary>
    public virtual bool ResolveVisible(in HudShowContext ctx)
    {
        if (ctx.Configuring) return true;
        if (!ConfigFlags.HasFlag(PanelConfig.CanBeOff)) return true;
        return ShowModeCvar() != 0;
    }

    /// <summary>
    /// The QC physics.qc / strafehud.qc <c>hud_panel_&lt;id&gt;</c> multi-value show-mode: 0 = off; 1 = on (hidden
    /// while a free-fly observer); 2 = on, including while observing; 3 = Race/CTS only (hidden while observing);
    /// 4 = Race/CTS only, including while observing. Any other positive value is treated as a plain "on".
    /// </summary>
    protected static bool ResolveShowMode(int mode, in HudShowContext ctx) => mode switch
    {
        0 => false,
        1 => !ctx.Observing,
        2 => true,
        3 => ctx.RaceOrCts && !ctx.Observing,
        4 => ctx.RaceOrCts,
        _ => mode != 0,
    };

    /// <summary>Normalized 0..1 default pos/size (QC <c>hud_panel_&lt;id&gt;_pos/_size</c>). Defaults to luma.</summary>
    public virtual PanelLayoutDefault DefaultLayout(Vector2 viewport)
    {
        HudLayoutDefaults.Entry e = HudLayoutDefaults.For(PanelId);
        return new PanelLayoutDefault(e.Pos, e.Size);
    }

    /// <summary>Per-panel background-image name default ("" = inherit hud_panel_bg, "0" = none, else border_*).</summary>
    public virtual string DefaultBg => HudLayoutDefaults.For(PanelId).Bg;
    public virtual string DefaultBgColor => HudLayoutDefaults.For(PanelId).BgColor;
    public virtual string DefaultBgAlpha => HudLayoutDefaults.For(PanelId).BgAlpha;
    public virtual string DefaultBgBorder => HudLayoutDefaults.For(PanelId).Border;
    public virtual string DefaultEnable => HudLayoutDefaults.For(PanelId).Enable;

    /// <summary>Whether contents change every frame (health/ammo/timer/crosshair/killfeed). Unchanged.</summary>
    public virtual bool IsDynamic => true;

    /// <summary>(R8) This panel renders with its OWN alpha (e.g. the crosshair's <c>crosshair_alpha</c>) rather
    /// than the scoreboard panel-fade, so the manager must NOT stop re-recording it when the scoreboard is up.
    /// Default false: ordinary panels fade with the scoreboard and can be skipped once fully transparent.</summary>
    public virtual bool DrawsWithOwnAlpha => false;

    // ---- HUD configure-mode (editor) flags — set by HudConfigEditor, read for the editor's overlay draw ----

    /// <summary>QC <c>panel == highlightedPanel</c>: this panel is the one the editor's cursor/keyboard is acting
    /// on (drawn with the highlight border + center-line guide). Set live by <see cref="HudConfigEditor"/>; the
    /// editor renders the chrome itself, so this is the queryable flag a panel/test can read.</summary>
    public bool IsHighlighted { get; set; }

    /// <summary>QC <c>panel == tab_panel</c>: this panel is the current Ctrl+Tab cycle candidate (drawn with the
    /// dim fill preview before Ctrl is released to commit the selection). Set live by <see cref="HudConfigEditor"/>.</summary>
    public bool IsTabSelected { get; set; }

    /// <summary>
    /// Whether this panel's DISPLAYED content actually changed since its last draw (3.2-3). A dynamic panel is
    /// re-recorded (QueueRedraw) every frame by default, which re-runs <see cref="DrawPanel"/> (re-formatting
    /// strings + re-recording canvas commands) even when nothing visible changed — wasteful for readouts whose
    /// value updates ~1×/s (fps, timer, ping). Such panels override this to compare a cheap, alloc-free snapshot
    /// of what they would draw against the last frame's, returning false to skip the redraw. The default (true)
    /// preserves the every-frame redraw for panels that genuinely animate (health/ammo/crosshair/killfeed).
    /// </summary>
    public virtual bool NeedsRedraw() => true;

    // =================================================================================================
    //  Resolved per-frame config (QC HUD_Panel_LoadCvars → current_panel_*)
    // =================================================================================================

    /// <summary>The resolved, pre-fade per-panel state. Pixel space.</summary>
    protected readonly record struct PanelConfig2(
        Vector2 PosPx, Vector2 SizePx,
        string Bg, Color BgColor, float BgAlpha, float BgBorder, float Padding,
        float FgAlpha, int FontSize, bool Enabled);

    /// <summary>The current pre-fade snapshot (resolved by <see cref="LoadConfig"/>).</summary>
    protected PanelConfig2 Cfg { get; private set; } = new(
        Vector2.Zero, new Vector2(64, 64), "0", new Color(0.10f, 0.14f, 0.25f), 1f, 2f, 3f, 0.9f, 16, true);

    /// <summary>Post-fade background alpha for this frame (<c>Cfg.BgAlpha × hud_fade × panel_fade</c>).</summary>
    protected float LiveBgAlpha { get; private set; } = 0.45f;

    /// <summary>Post-fade foreground alpha for this frame (<c>Cfg.FgAlpha × hud_fade × panel_fade</c>).</summary>
    protected float LiveFgAlpha { get; private set; } = 0.9f;

    /// <summary>The panel's screen rectangle in absolute pixels. Mirrors QC <c>panel_pos</c>/<c>panel_size</c>.</summary>
    public Rect2 PanelRect { get; private set; }

    /// <summary>The panel's local content size (its rect size). Convenience for draw code.</summary>
    protected Vector2 Size2 => Cfg.SizePx;

    private bool _configDirty = true;
    private Vector2 _lastViewport = new(-1, -1);
    private bool _subscribed;

    public override void _EnterTree()
    {
        // React to live cvar edits (console `set hud_*` / menu sliders) by marking the snapshot dirty so the
        // next LoadConfig re-resolves immediately. Leak-safe: unsubscribed in _ExitTree.
        if (!_subscribed)
        {
            MenuState.Cvars.Changed += OnCvarChanged;
            _subscribed = true;
        }
    }

    public override void _ExitTree()
    {
        if (_subscribed)
        {
            MenuState.Cvars.Changed -= OnCvarChanged;
            _subscribed = false;
        }
    }

    private void OnCvarChanged(string name)
    {
        // `_hud_configure` doesn't share the hud_ prefix but flips the configure-mode resolve overrides
        // (forced bg / min-alpha / disabled grey-out), so it must dirty the snapshot too.
        if (name.StartsWith("hud_", System.StringComparison.Ordinal) || name == "_hud_configure")
            _configDirty = true;
    }

    /// <summary>QC <c>autocvar__hud_configure</c> — the HUD configure-mode editor is active. Only editor-mode
    /// resolve/draw paths key off this (never normal-draw layout logic).</summary>
    protected static bool IsConfiguring => MenuState.Cvars.GetFloat("_hud_configure") != 0f;

    /// <summary>Force the next <see cref="LoadConfig"/> to fully re-resolve (e.g. on viewport resize).</summary>
    public void InvalidateConfig() => _configDirty = true;

    /// <summary>
    /// Resolve pos/size/bg/alpha/font from the <c>hud_panel_&lt;id&gt;_*</c> cvars (throttled) and push
    /// <c>Position</c>/<c>Size</c>; then re-apply the HUD fade to the live alphas every frame so fades animate
    /// between throttled reloads. <paramref name="fadeAlpha"/> = global HUD fade (menu), <paramref name="panelFade"/>
    /// = this panel's extra fade (scoreboard cross-fade).
    /// </summary>
    public void LoadConfig(Vector2 viewport, float fadeAlpha, float panelFade)
    {
        if (_configDirty || viewport != _lastViewport)
        {
            Cfg = Resolve(viewport);
            _lastViewport = viewport;
            _configDirty = false;

            PanelRect = new Rect2(Cfg.PosPx, Cfg.SizePx);
            Position = Cfg.PosPx;
            Size = Cfg.SizePx;
        }

        float fade = Mathf.Clamp(fadeAlpha * panelFade, 0f, 1f);
        LiveBgAlpha = Cfg.BgAlpha * fade;
        LiveFgAlpha = Cfg.FgAlpha * fade;
    }

    private PanelConfig2 Resolve(Vector2 viewport)
    {
        PanelLayoutDefault def = DefaultLayout(viewport);

        Vector2 posF = ParseVec2(CvarStr("pos"), def.PosFraction);
        Vector2 sizeF = ParseVec2(CvarStr("size"), def.SizeFraction);
        Vector2 posPx = new(posF.X * viewport.X, posF.Y * viewport.Y);
        Vector2 sizePx = new(Mathf.Max(8f, sizeF.X * viewport.X), Mathf.Max(8f, sizeF.Y * viewport.Y));

        // bg name: "" inherits the global hud_panel_bg; "0" = no frame.
        string bg = CvarStr("bg");
        if (string.IsNullOrWhiteSpace(bg)) bg = GlobalStr("hud_panel_bg");
        if (string.IsNullOrWhiteSpace(bg)) bg = "0";

        // bg color: "" inherits hud_panel_bg_color; the QC HUD_Panel_GetColor literals "shirt"/"pants"/"team"
        // resolve to the local player's colormap shirt/pants color (colormapPaletteColor) / team color when fed by
        // the manager — else they fall back to inheriting the global bg color (no local player yet).
        string bgColStr = CvarStr("bg_color");
        Color bgCol;
        if (bgColStr == "shirt" && LocalShirtColor is { } shirt) bgCol = shirt;
        else if (bgColStr == "pants" && LocalPantsColor is { } pants) bgCol = pants;
        else if (bgColStr == "team" && LocalTeamColor is { } team) bgCol = team;
        else
        {
            if (string.IsNullOrWhiteSpace(bgColStr) || bgColStr == "shirt" || bgColStr == "pants" || bgColStr == "team")
                bgColStr = GlobalStr("hud_panel_bg_color");
            bgCol = TryParseRgb(bgColStr, out Color c) ? c : new Color(0f, 0.14f, 0.25f);
        }

        float bgAlpha = CvarF("bg_alpha", GlobalF("hud_panel_bg_alpha", 1f));
        float bgBorder = CvarF("bg_border", GlobalF("hud_panel_bg_border", 2f));
        float padding = CvarF("bg_padding", GlobalF("hud_panel_bg_padding", 3f));
        padding = Mathf.Min(Mathf.Min(sizePx.X, sizePx.Y) * 0.5f - 5f, padding);
        if (padding < 0f) padding = 0f;

        float fgAlpha = CvarF("fg_alpha", GlobalF("hud_panel_fg_alpha", 1f));

        bool enabled = !ConfigFlags.HasFlag(PanelConfig.CanBeOff) ||
                       MenuState.Cvars.GetFloat("hud_panel_" + PanelId) != 0f;

        // ---- HUD configure-mode overrides (the QC hud.qh GetBg / GetBgAlpha / GetFgAlpha / GetColor
        // configure branches) — the editor must SEE every panel: a "0"/inherited-0 background falls back to
        // the skin border_default drawn at hud_configure_bg_minalpha (GetBg forces panel_bg_alpha_str "0",
        // which the min-alpha floor then lifts), an enabled panel's bg alpha is floored at that minalpha, and
        // a DISABLED panel draws bg+fg at a fixed 0.25 so it reads as greyed out. Editor-mode only: none of
        // this touches the normal-draw resolve. ----
        if (IsConfiguring)
        {
            if (bg == "0")
            {
                bg = "border_default"; // QC GetBg: "we probably want to see a background in config mode"
                bgAlpha = 0f;          // QC GetBg: panel_bg_alpha_str = "0" for a "0" bg (floored below)
            }
            if (!enabled)
            {
                bgAlpha = 0.25f; // QC GetBgAlpha: disabled panels at a fixed 0.25
                fgAlpha = 0.25f; // QC GetFgAlpha: "ALWAYS show disabled panels at 0.25 alpha"
            }
            else
            {
                bgAlpha = Mathf.Max(GlobalF("hud_configure_bg_minalpha", 0.25f), bgAlpha);
            }

            // QC GetColor: hud_configure_teamcolorforced + a positive team-color factor → the red test tint
            // ('1 0 0' * panel_bg_color_team, the dialogs' "Test team color in configure mode" checkbox).
            float bgColorTeam = CvarF("bg_color_team", GlobalF("hud_panel_bg_color_team", 0f));
            if (bgColorTeam > 0f && GlobalF("hud_configure_teamcolorforced", 0f) != 0f)
                bgCol = new Color(Mathf.Clamp(bgColorTeam, 0f, 1f), 0f, 0f);
        }

        // QC HUD sizing: the HUD is drawn in the vid_conwidth × vid_conheight virtual canvas (Base defaults
        // 800×600; vid_conwidthauto widens conwidth for aspect, conheight stays fixed) and the engine scales that
        // canvas UNIFORMLY to the screen — so a hud_fontsize-tall glyph renders at hud_fontsize × screenH /
        // vid_conheight (HEIGHT-locked). The port previously scaled by viewport.X / hud_width (WIDTH-based, and
        // hud_width defaults to 560), which over-scaled the HUD font on wide/high-res screens — the centerprint
        // read far too large (playtest-bugs #3). Match Base: height-lock the font to vid_conheight (default 600).
        float baseFont = CvarF("fontsize", GlobalF("hud_fontsize", 11f));
        float conHeight = GlobalF("vid_conheight", 600f);
        if (conHeight <= 0f) conHeight = 600f;
        int fontPx = Mathf.Max(8, Mathf.RoundToInt(baseFont * viewport.Y / conHeight));

        return new PanelConfig2(posPx, sizePx, bg, bgCol, bgAlpha, bgBorder, padding, fgAlpha, fontPx, enabled);
    }

    // -------------------------------------------------------------------------------------------------
    //  Live cvar accessors (MenuState.Cvars — same store the console + menu dialogs write)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Read <c>hud_panel_&lt;id&gt;_&lt;suffix&gt;</c> as a raw string ("" when unset).</summary>
    protected string CvarStr(string suffix) => MenuState.Cvars.GetString($"hud_panel_{PanelId}_{suffix}");

    /// <summary>Read <c>hud_panel_&lt;id&gt;_&lt;suffix&gt;</c> as a float (unset → <paramref name="fallback"/>).</summary>
    protected float CvarF(string suffix, float fallback)
    {
        string s = MenuState.Cvars.GetString($"hud_panel_{PanelId}_{suffix}");
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat($"hud_panel_{PanelId}_{suffix}");
    }

    /// <summary>Read <c>hud_panel_&lt;id&gt;_&lt;suffix&gt;</c> as a bool (non-zero).</summary>
    protected bool CvarBool(string suffix) => CvarF(suffix, 0f) != 0f;

    /// <summary>Read a global cvar by full name.</summary>
    protected static string GlobalStr(string name) => MenuState.Cvars.GetString(name);

    /// <summary>Read a global cvar as float (unset → <paramref name="fallback"/>).</summary>
    protected static float GlobalF(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }

    /// <summary>Parse a space-separated "r g b" (0..1) cvar string.</summary>
    /// <summary>Public wrapper over the shared "r g b" parser (QC <c>stov</c>) so non-panel HUD draws
    /// (e.g. <see cref="HudDock"/>) can resolve a color cvar with the same rules.</summary>
    public static bool TryParseRgbColor(string s, out Color c) => TryParseRgb(s, out c);

    protected static bool TryParseRgb(string s, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return false;
        if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return false;
        if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)) return false;
        if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) return false;
        c = new Color(r, g, b);
        return true;
    }

    private static Vector2 ParseVec2(string s, Vector2 fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return fallback;
        if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return fallback;
        if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return fallback;
        return new Vector2(x, y);
    }

    // -------------------------------------------------------------------------------------------------
    //  Legacy layout shim (NetGame.cs still calls _scoreboard.Configure; LoadConfig wins the next frame).
    // -------------------------------------------------------------------------------------------------

    /// <summary>Place + size the panel directly (legacy path; <see cref="LoadConfig"/> overrides it next frame
    /// for cvar-driven panels). Kept so external callers (the net layer's scoreboard sizing) still compile.
    /// The standalone scoreboard is placed ONLY through here (it is never <see cref="LoadConfig"/>'d), so this
    /// must also update <see cref="Cfg"/> — otherwise <see cref="Size2"/> (which the draw code reads) keeps its
    /// 64×64 default and the panel renders as a tiny box in the top-left while its Control rect (input hit-test)
    /// is the real, larger rect.</summary>
    public void Configure(Rect2 rect)
    {
        PanelRect = rect;
        Position = rect.Position;
        Size = rect.Size;
        Cfg = Cfg with { PosPx = rect.Position, SizePx = rect.Size };
        QueueRedraw();
    }

    /// <summary>Guards the configure-mode pre-pass against a double frame paint (see <see cref="_Draw"/>).</summary>
    private bool _configureBgDrawn;

    public override void _Draw()
    {
        // HUD configure-mode pre-pass: in the editor EVERY MAIN panel must show its frame (Base forces a
        // border_default bg at hud_configure_bg_minalpha — the Resolve configure branch), but many panels bail
        // out of DrawPanel before their own DrawBackground() when they have no live data (vote / racetimer /
        // modicons / …). Paint the forced frame first; DrawBackground() then no-ops for this draw so panels
        // that DO reach their own call don't double-paint. Editor-mode only — normal draws are unchanged.
        _configureBgDrawn = false;
        if (IsConfiguring && ConfigFlags.HasFlag(PanelConfig.Main))
        {
            DrawBackgroundRect(new Rect2(Vector2.Zero, Size2), LiveBgAlpha);
            _configureBgDrawn = true;
        }
        DrawPanel();
        _configureBgDrawn = false;
    }

    /// <summary>Draw the panel contents in panel-local space (origin = top-left). Successor to each QC
    /// <c>HUD_&lt;Name&gt;(bool should_draw)</c> body. Bail early when there's nothing to show.</summary>
    protected abstract void DrawPanel();

    // -------------------------------------------------------------------------------------------------
    //  Shared draw helpers (the modernized stand-ins for draw.qh's drawpic/drawfill/drawstring family).
    //  All take panel-local coordinates and draw with the Xolonium font.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Paint the panel chrome: the skin 9-slice border frame (QC <c>HUD_Panel_DrawBg</c>) around the
    /// whole panel. No-op when the resolved bg is "0"/inherit-of-0 — or when the configure-mode pre-pass
    /// (<see cref="_Draw"/>) already painted the full-panel frame this draw.</summary>
    protected void DrawBackground()
    {
        if (_configureBgDrawn) return;
        DrawBackgroundRect(new Rect2(Vector2.Zero, Size2), LiveBgAlpha);
    }

    /// <summary>Paint the skin 9-slice border frame (QC <c>HUD_Panel_DrawBg</c>) around an explicit panel-local
    /// <paramref name="panelRect"/> at <paramref name="bgAlpha"/>. The full-panel <see cref="DrawBackground()"/>
    /// passes <c>(0,0,Size2)</c>; the weapons panel passes the shrunk owned-weapon grid rect so the frame
    /// auto-sizes to its contents (playtest-bugs #11). Flat translucent fallback only if the border texture is missing.</summary>
    protected void DrawBackgroundRect(Rect2 panelRect, float bgAlpha)
    {
        string bg = Cfg.Bg;
        if (string.IsNullOrEmpty(bg) || bg == "0") return; // luma: no frame for this panel
        var col = new Color(Cfg.BgColor.R, Cfg.BgColor.G, Cfg.BgColor.B);
        float t = Cfg.BgBorder;
        // QC HUD_Panel_DrawBg (hud.qh): the bg rect expands by panel_bg_border, but the 9-slice corner/edge
        // tiles are sliced at BORDER_MULTIPLIER(=4) × panel_bg_border — the beveled corner art fills a 4×-larger
        // cell. Passing the raw border as the slice size crushed the bevel into a near-square corner (playtest-bugs #10).
        const float borderMultiplier = 4f;
        var outer = new Rect2(panelRect.Position.X - t, panelRect.Position.Y - t,
                              panelRect.Size.X + 2f * t, panelRect.Size.Y + 2f * t);
        if (!HudSkin.DrawBorderPicture(this, outer, bg, col, bgAlpha, t * borderMultiplier))
            DrawRect(panelRect, new Color(col.R, col.G, col.B, bgAlpha * 0.7f));
    }

    /// <summary>Paint a flat translucent fill over an arbitrary local rect (inner sub-region backing — the
    /// legacy behavior, NOT the skin frame). Tracks the live bg alpha.</summary>
    protected void DrawBackground(Rect2 local)
        => DrawRect(local, new Color(0.10f, 0.10f, 0.12f, 0.45f * Mathf.Clamp(LiveBgAlpha / 0.45f, 0f, 1f)));

    /// <summary>Draw a skin/art pic by bare VFS name (skin → default fall-through). Returns false on miss.</summary>
    protected bool DrawSkinPic(string bareName, Rect2 local, Color modulate)
        => DrawSkinPicFirst(local, modulate,
            $"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");

    /// <summary>Draw the first art pic in <paramref name="candidates"/> that resolves. Returns false on miss.</summary>
    protected bool DrawSkinPicFirst(Rect2 local, Color modulate, params string?[] candidates)
    {
        Texture2D? tex = TextureCache.GetFirst(candidates);
        if (tex is null) return false;
        DrawTextureRect(tex, local, false, modulate);
        return true;
    }

    /// <summary>
    /// The shared progress-bar primitive — faithful port of QC <c>HUD_Panel_DrawProgressBar</c>
    /// (hud.qc:269-372). Fills <paramref name="area"/> to <paramref name="lengthRatio"/> using the skin art
    /// <paramref name="art"/> (<c>progressbar</c> / <c>progressbar_vertical</c> / <c>accelbar</c>), tinted by
    /// <paramref name="color"/> at <paramref name="alpha"/>, honoring all four QC alignments:
    /// 0 = left/top, 1 = right/bottom, 2 = symmetric-centered (non-negative only — a negative ratio is dropped,
    /// like the QC <c>length_ratio &lt; 0 → return</c>), 3 = SIGNED-centered (positive fills the right/bottom
    /// half, negative the left/top half — the only mode that accepts a negative ratio, used by the accel bar).
    /// <paramref name="vertical"/> selects the vertical art + fill axis. Falls back to a plain rect when the art
    /// is missing so a bar is never invisible.
    /// </summary>
    protected void DrawProgressBar(Rect2 area, string art, float lengthRatio, bool vertical, int baralign,
        Color color, float alpha)
    {
        Vector2 origin = area.Position;
        Vector2 size = area.Size;

        // QC: if (!length_ratio || !theAlpha) return;
        if (alpha <= 0f || size.X <= 0f || size.Y <= 0f || lengthRatio == 0f) return;
        // A non-finite ratio/geometry bypasses every comparison below (NaN compares false) and would spray a
        // NaN rect into the renderer; bail before any clamp runs.
        if (!float.IsFinite(lengthRatio) || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y)
            || !float.IsFinite(size.X) || !float.IsFinite(size.Y)) return;

        // QC: clamp positive overflow; only baralign 3 may go negative (clamped to -1), the rest drop a negative.
        if (lengthRatio > 1f) lengthRatio = 1f;
        if (baralign == 3) { if (lengthRatio < -1f) lengthRatio = -1f; }
        else if (lengthRatio < 0f) return;

        // The skin art name (vertical gets the _vertical suffix, matching QC's strcat).
        string skinArt = vertical ? art + "_vertical" : art;
        var tint = new Color(color.R, color.G, color.B, Mathf.Clamp(alpha, 0f, 1f));

        // Resolve the skin art once (skin → default → bare default), so the 3-slice cap split can blit
        // sub-regions of the *same* texture (QC's drawsubpic). Null → flat-rect fallback.
        Texture2D? tex = TextureCache.GetFirst(
            $"gfx/hud/{HudSkin.SkinName}/{skinArt}", $"gfx/hud/default/{skinArt}",
            vertical ? "gfx/hud/default/progressbar_vertical" : "gfx/hud/default/progressbar");

        if (vertical)
        {
            float oy = origin.Y;
            float h = size.Y;
            switch (baralign)
            {
                case 1: oy += (1f - lengthRatio) * size.Y; break;              // bottom align
                case 2: oy += 0.5f * (1f - lengthRatio) * size.Y; break;       // center align
                case 3:                                                        // signed center (down +, up −)
                    h = size.Y * 0.5f;
                    if (lengthRatio > 0f) oy += h;
                    else { oy += (1f + lengthRatio) * h; lengthRatio = -lengthRatio; }
                    break;
            }
            h *= lengthRatio;
            if (h <= 0f) return;

            if (tex is null) { DrawRect(new Rect2(origin.X, oy, size.X, h), tint); return; }
            // QC vertical 3-slice (hud.qc:315-329): cap UVs are y[0,.25]/[.25,.75]/[.75,1].
            if (h <= size.X * 2f)
            {
                // not tall enough → just top + bottom halves, src y cropped to bH around the ends.
                float half = h * 0.5f;
                float bH = 0.25f * h / (size.X * 2f);
                Blit3(tex, tint, new Rect2(origin.X, oy, size.X, half), new Rect2(0f, 0f, 1f, bH));
                Blit3(tex, tint, new Rect2(origin.X, oy + half, size.X, half), new Rect2(0f, 1f - bH, 1f, bH));
            }
            else
            {
                float sq = size.X; // square chunk = bar width
                Blit3(tex, tint, new Rect2(origin.X, oy, size.X, sq), new Rect2(0f, 0f, 1f, 0.25f));
                Blit3(tex, tint, new Rect2(origin.X, oy + sq, size.X, h - 2f * sq), new Rect2(0f, 0.25f, 1f, 0.5f));
                Blit3(tex, tint, new Rect2(origin.X, oy + h - sq, size.X, sq), new Rect2(0f, 0.75f, 1f, 0.25f));
            }
        }
        else
        {
            float ox = origin.X;
            float w = size.X;
            switch (baralign)
            {
                case 1: ox += (1f - lengthRatio) * size.X; break;             // right align
                case 2: ox += 0.5f * (1f - lengthRatio) * size.X; break;      // center align
                case 3:                                                       // signed center (right +, left −)
                    w = size.X * 0.5f;
                    if (lengthRatio > 0f) ox += w;
                    else { ox += (1f + lengthRatio) * w; lengthRatio = -lengthRatio; }
                    break;
            }
            w *= lengthRatio;
            if (w <= 0f) return;

            if (tex is null) { DrawRect(new Rect2(ox, origin.Y, w, size.Y), tint); return; }
            // QC horizontal 3-slice (hud.qc:351-365): cap UVs are x[0,.25]/[.25,.75]/[.75,1].
            if (w <= size.Y * 2f)
            {
                // not wide enough → just left + right halves, src x cropped to bW around the ends.
                float half = w * 0.5f;
                float bW = 0.25f * w / (size.Y * 2f);
                Blit3(tex, tint, new Rect2(ox, origin.Y, half, size.Y), new Rect2(0f, 0f, bW, 1f));
                Blit3(tex, tint, new Rect2(ox + half, origin.Y, half, size.Y), new Rect2(1f - bW, 0f, bW, 1f));
            }
            else
            {
                float sq = size.Y; // square chunk = bar height
                Blit3(tex, tint, new Rect2(ox, origin.Y, sq, size.Y), new Rect2(0f, 0f, 0.25f, 1f));
                Blit3(tex, tint, new Rect2(ox + sq, origin.Y, w - 2f * sq, size.Y), new Rect2(0.25f, 0f, 0.5f, 1f));
                Blit3(tex, tint, new Rect2(ox + w - sq, origin.Y, sq, size.Y), new Rect2(0.75f, 0f, 0.25f, 1f));
            }
        }
    }

    /// <summary>Blit a UV sub-region (QC <c>drawsubpic</c>) of <paramref name="tex"/> into the panel-local
    /// dest <paramref name="dst"/>. <paramref name="srcUv"/> is in 0..1 UV space (origin + size); converted to
    /// the texture's pixel rect. Used by the 3-slice progress-bar cap render.</summary>
    private void Blit3(Texture2D tex, Color tint, Rect2 dst, Rect2 srcUv)
    {
        if (dst.Size.X <= 0f || dst.Size.Y <= 0f) return;
        Vector2 ts = tex.GetSize();
        var srcPx = new Rect2(srcUv.Position.X * ts.X, srcUv.Position.Y * ts.Y,
                              srcUv.Size.X * ts.X, srcUv.Size.Y * ts.Y);
        DrawTextureRectRegion(tex, dst, srcPx, tint);
    }

    /// <summary>Draw a horizontal, left-aligned progress bar (QC <c>HUD_Panel_DrawProgressBar</c>, baralign 0).
    /// Convenience wrapper over <see cref="DrawProgressBar"/> that first lays a dark track + a thin frame (the
    /// port's existing look) then fills it with the skin <c>progressbar</c> art (flat-rect fallback). Kept so the
    /// existing 3-arg callers (ammo / vehicle / vote) are unchanged.</summary>
    protected void DrawBar(Rect2 area, float fraction, Color fill)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        DrawRect(area, new Color(0f, 0f, 0f, 0.35f));
        DrawProgressBar(area, "progressbar", fraction, vertical: false, baralign: 0, fill, fill.A);
        DrawRect(area, new Color(1f, 1f, 1f, 0.15f), filled: false, width: 1f);
    }

    /// <summary>Godot <c>DrawString</c>'s Y is the BASELINE. These helpers take a TOP-of-text Y (QC drawstring
    /// semantics: pos = the char box's top-left), so the baseline offset must be the font's ASCENT for the size —
    /// NOT the full font size. Xolonium's ascent is ~0.75-0.8 × size, so the old <c>pos.Y + size</c> rendered all
    /// HUD text ~20% of the font size too LOW in its box (playtest #26: health/armor numbers visibly below
    /// center; systemic to every panel using these helpers).</summary>
    private float TextBaseline(int size) => Font.GetAscent(size);

    /// <summary>Draw left-aligned text at a panel-local top-left position (with a subtle drop shadow so it
    /// reads over the world the way Xonotic's outlined HUD font does).</summary>
    protected void DrawText(Vector2 pos, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = pos + new Vector2(0f, TextBaseline(size));
        DrawString(Font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Left, -1f, size, ShadowOf(color));
        DrawString(Font, at, text, HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>Draw text horizontally centered within <paramref name="width"/> (QC align 0.5).</summary>
    protected void DrawTextCentered(Vector2 pos, float width, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = pos + new Vector2(0f, TextBaseline(size));
        DrawString(Font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Center, width, size, ShadowOf(color));
        DrawString(Font, at, text, HorizontalAlignment.Center, width, size, color);
    }

    /// <summary>Draw text right-aligned to end at <paramref name="rightX"/> (panel-local).</summary>
    protected void DrawTextRight(float rightX, float topY, float width, string text, Color color, int size = FontSize)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = new(rightX - width, topY + TextBaseline(size));
        DrawString(Font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Right, width, size, ShadowOf(color));
        DrawString(Font, at, text, HorizontalAlignment.Right, width, size, color);
    }

    private static Color ShadowOf(Color c) => new(0f, 0f, 0f, c.A * 0.7f);

    /// <summary>Measure a string's pixel width at the given size (QC <c>stringwidth</c>).</summary>
    protected static float MeasureText(string text, int size = FontSize)
        => string.IsNullOrEmpty(text)
            ? 0f
            : Font.GetStringSize(text, HorizontalAlignment.Left, -1f, size).X;

    // ---- color helpers (QC HUD_Get_Num_Color: tint a value by how low it is) ----

    /// <summary>The local player's resolved SHIRT (top-colormap) color (QC <c>HUD_Panel_GetColor</c>'s
    /// <c>"shirt"</c> literal = <c>colormapPaletteColor(floor(_cl_color/16), false)</c>), or null when there is no
    /// local player (pre-spawn / pure --connect). Fed once per frame by <see cref="Hud._Process"/>. Lets a panel's
    /// <c>hud_panel_&lt;id&gt;_bg_color shirt</c> (and the dock's <c>hud_dock_color shirt</c>) tint to the player's
    /// own upper color, matching Base; in a team game the engine forces both colormap nibbles to the team color, so
    /// this naturally resolves to the team tint there.</summary>
    public static Color? LocalShirtColor { get; set; }

    /// <summary>The local player's resolved PANTS (bottom-colormap) color (QC <c>"pants"</c> literal =
    /// <c>colormapPaletteColor(_cl_color % 16, true)</c>), or null when there is no local player. Fed once per frame
    /// by <see cref="Hud._Process"/>. See <see cref="LocalShirtColor"/>.</summary>
    public static Color? LocalPantsColor { get; set; }

    /// <summary>The local player's TEAM color (QC <c>myteamcolors</c> — used by the <c>"team"</c> bg-color literal),
    /// or null when not on a team / not teamplay. Fed once per frame by <see cref="Hud._Process"/>.</summary>
    public static Color? LocalTeamColor { get; set; }

    /// <summary>The shared HUD clock (QC <c>time</c>), fed once per frame by <see cref="Hud._Process"/>. Drives
    /// the <see cref="NumColor"/> blink/pulse (and any other wall-clock HUD animation). Defaults to 0 so unit
    /// tests that never tick the manager see a stable, deterministic color.</summary>
    public static float HudClock { get; set; }

    /// <summary>
    /// Color a resource number by its fraction of max — the faithful port of QC <c>HUD_Get_Num_Color</c>
    /// (hud.qc:123-163): a 5-stop ramp green→lightgreen→white→lightyellow→red by percent, with (when
    /// <paramref name="blink"/>) a sine flash at ≥100% and a stronger pulse below 25%. <paramref name="blink"/>
    /// defaults to true to match every QC panel caller (healtharmor / engineinfo); the crosshair passes false.
    /// </summary>
    protected Color NumColor(float value, float max, bool blink = true)
    {
        if (max <= 0f) return FgColor;

        // QC const vectors (hud.qc:125-129).
        var color100 = new NVec3(0f, 1f, 0f);    // green
        var color75 = new NVec3(0.4f, 0.9f, 0f); // lightgreen
        var color50 = new NVec3(1f, 1f, 1f);     // white
        var color25 = new NVec3(1f, 1f, 0.2f);   // lightyellow
        var color10 = new NVec3(1f, 0f, 0f);     // red

        float pct = value / max * 100f;
        NVec3 c;
        if (pct > 100f) c = color100;
        else if (pct > 75f) c = Between(color75, color100, pct, 75f, 100f);
        else if (pct > 50f) c = Between(color50, color75, pct, 50f, 75f);
        else if (pct > 25f) c = Between(color25, color50, pct, 25f, 50f);
        else if (pct > 10f) c = Between(color10, color25, pct, 10f, 25f);
        else c = color10;

        if (blink)
        {
            if (pct >= 100f) // hud.qc:148-154 — sine flash; fill the zero channels with sin(2πt).
            {
                float f = Mathf.Sin(2f * Mathf.Pi * HudClock);
                if (c.X == 0f) c.X = f;
                if (c.Y == 0f) c.Y = f;
                if (c.Z == 0f) c.Z = f;
            }
            else if (pct < 25f) // hud.qc:155-159 — low-health pulse (stronger as pct→0).
            {
                float f = (1f - pct / 25f) * Mathf.Sin(2f * Mathf.Pi * HudClock);
                c *= 1f - f;
            }
        }

        return new Color(Mathf.Clamp(c.X, 0f, 1f), Mathf.Clamp(c.Y, 0f, 1f), Mathf.Clamp(c.Z, 0f, 1f),
            LiveFgAlpha);

        static NVec3 Between(NVec3 lo, NVec3 hi, float pctv, float min, float max2)
            => lo + (hi - lo) * ((pctv - min) / (max2 - min));
    }

    /// <summary>Convert a sim-side (System.Numerics) color vector to a Godot <see cref="Color"/>.</summary>
    protected static Color ToColor(NVec3 c, float alpha = 1f) => new(c.X, c.Y, c.Z, alpha);

    /// <summary>Format a whole-seconds duration as M:SS (QC <c>seconds_tostring</c>).</summary>
    protected static string SecondsToString(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:D2}";
    }
}
