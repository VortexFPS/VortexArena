using Godot;
using VortexArena.Common.Services;          // CvarFlags
using VortexArena.Engine.Console;            // BindTable — the live +/- held-button state (PRESSED_KEYS source)
using VortexArena.Engine.Simulation;         // CvarService (RegisterDefaults)

namespace VortexArena.Game.Hud;

/// <summary>
/// Pressed-keys panel — port of Base/.../qcsrc/client/hud/panel/pressedkeys.qc (HUD panel #11). The QC version
/// drew the movement-key cluster (WASD + jump/crouch, plus an optional attack row) as the skinned
/// <c>key_*</c> / <c>key_*_inv</c> pics, lighting up the <c>_inv</c> ("inverted", i.e. highlighted) variant for
/// each key currently held in <c>STAT(PRESSED_KEYS)</c>. It is the spectator/demo "input cam" overlay — in QC it
/// only drew while spectating (or, with the cvar set to 2, also for the local player), reading the watched
/// player's button bits off the network stat.
///
/// <para>This port keeps the faithful look + layout math and drives it client-side from
/// <see cref="BindTable"/> — the C# successor to <c>cl_input.c</c>'s <c>kbutton_t</c> press/release state. Those
/// bools are set by the same <c>+forward</c>/<c>+back</c>/<c>+moveleft</c>/<c>+moveright</c>/<c>+jump</c>/
/// <c>+crouch</c>/<c>+attack</c>/<c>+attack2</c> binds that feed the movement sampler, so the cluster follows the
/// player's real (rebindable) keys, not a hardcoded WASD assumption, and goes dark the moment the console grabs
/// input (<c>BindTable.ReleaseAll</c>). Reading the local bind state is purely client-side — no net stat needed
/// for the local player; a separate integration task can later feed a spectated player's bits via
/// <see cref="PressedKeysOverride"/>.</para>
///
/// <para>Faithful layout (pressedkeys.qc):
/// <list type="bullet">
///   <item><b>Aspect</b> — <c>hud_panel_pressedkeys_aspect</c> (default 1.8) forces a fixed width:height so the
///         key cluster keeps its shape regardless of the panel's authored size; the cluster is centred in the
///         leftover space (letterboxed on the long axis).</item>
///   <item><b>Grid</b> — key cells are <c>mySize.x / 3.5</c> wide and <c>mySize.y / rows</c> tall, positioned in
///         quarter-cell steps exactly as the QC <c>eX * (n/4 * keysize.x)</c> offsets.</item>
///   <item><b>Attack row</b> — <c>hud_panel_pressedkeys_attack</c> (default 0) toggles the extra atck1/atck2 row
///         on top; when off the grid is 2 rows tall instead of 3.</item>
/// </list></para>
///
/// <para>Self-blank: with nothing to show (no skin pics resolve, or — for the local player — there is no
/// override and we're simply rendering live binds) the panel still only paints the chrome + the (unhighlighted)
/// key cluster; the QC behaviour is to always show the cluster while the panel is active, so "no key held" draws
/// the resting <c>key_*</c> pics, not a blank panel.</para>
/// </summary>
public partial class PressedKeysPanel : HudPanel
{
    // QC pressedkeys.qh KEY_* bits — the order/values match the network PRESSED_KEYS stat so an override fed by
    // the net layer (a spectated player's bits) lines up bit-for-bit.
    private const int KEY_FORWARD = 1 << 0;
    private const int KEY_BACKWARD = 1 << 1;
    private const int KEY_LEFT = 1 << 2;
    private const int KEY_RIGHT = 1 << 3;
    private const int KEY_JUMP = 1 << 4;
    private const int KEY_CROUCH = 1 << 5;
    private const int KEY_ATCK = 1 << 6;
    private const int KEY_ATCK2 = 1 << 7;

    /// <summary>
    /// Optional externally-supplied pressed-key bitmask (QC <c>STAT(PRESSED_KEYS)</c> layout: the KEY_* bits
    /// above). The net/spectate layer sets this to a watched player's button bits; while it is non-null the panel
    /// renders those instead of the local <see cref="BindTable"/> state. Left null on the normal local-player /
    /// demo path, so the cluster reflects this client's own held keys. Set to null to return to live local binds.
    /// </summary>
    public int? PressedKeysOverride { get; set; }

    /// <summary>
    /// QC <c>spectatee_status</c>: 0 = playing locally, &gt;0 = chasing a player (that player's entity number),
    /// -1 = free-fly observer. Set by the net/spectate layer each frame. Drives the QC enable gate
    /// (<c>HUD_PressedKeys</c>): a free-fly observer (&lt; 0) never shows the cluster; while merely playing
    /// (&lt;= 0) it shows only when <c>hud_panel_pressedkeys</c> is 2 ("always"); chasing a player (&gt; 0) shows
    /// it for any non-zero enable. Default 0 (the local-player path) so older feeders keep working.
    /// </summary>
    public int SpectateeStatus { get; set; }

    /// <summary>True while the HUD editor is open (QC <c>autocvar__hud_configure</c>): the QC bypasses the
    /// spectatee gate so the panel is visible/positionable in the editor. Default false.</summary>
    public bool Configuring { get; set; }

    /// <summary>QC <c>hud_panel_pressedkeys_aspect</c>: forced width:height of the key cluster (0 = use the
    /// panel's own aspect). Default 1.8 (the luma default). Read live so a console <c>set</c> retunes it.</summary>
    private float Aspect => CvarF("aspect", 1.8f);

    /// <summary>QC <c>hud_panel_pressedkeys_attack</c>: draw the extra atck1/atck2 row (default 0 = hidden).</summary>
    private bool ShowAttack => CvarBool("attack");

    /// <summary>Always animating — the highlighted key changes as the player moves, so redraw each frame.</summary>
    public override bool IsDynamic => true;

    /// <summary>Repaint each frame WHILE SHOWN so the highlighted key tracks the held buttons live. Skips the
    /// redraw when the integration layer has hidden the panel (the QC <c>spectatee_status</c> / value-2 visibility
    /// gate is applied externally by toggling <see cref="Godot.Node.Visible"/>), matching the other always-on
    /// panels (PingPanel/FpsPanel) instead of churning a redraw on a hidden panel.</summary>
    public override void _Process(double delta)
    {
        if (Visible)
            QueueRedraw();
    }

    /// <summary>Register this panel's behaviour cvars (QC <c>HUD_PressedKeys_Export</c> saved these into hud skins).
    /// Invoked by reflection from <see cref="HudConfig.RegisterDefaults"/>; idempotent (never clobbers a cfg/seta).</summary>
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        c.Register("hud_panel_pressedkeys_aspect", "1.8", save);
        c.Register("hud_panel_pressedkeys_attack", "0", save);
        // QC HUD_Panel default: hud_panel_pressedkeys 0 = off, 1 = spectated player only, 2 = also the local
        // player ("always"). The DrawPanel spectatee gate enforces these against the live SpectateeStatus. The
        // generic enable cvar is seeded by HudConfig from the luma table ("1"); re-asserting here keeps the
        // panel's tunables co-located but stays idempotent.
        c.Register("hud_panel_pressedkeys", "1", save);
    }

    protected override void DrawPanel()
    {
        // QC HUD_PressedKeys: `if (!autocvar_hud_panel_pressedkeys) return;` — when the panel is disabled via
        // `hud_panel_pressedkeys 0` it draws NOTHING (not even the skin frame). The manager only gates on
        // Visible (which nothing flips for the enable cvar), so enforce the disable toggle here for a true
        // self-blank. Cfg.Enabled already resolves `hud_panel_pressedkeys != 0` (the panel is CanBeOff).
        if (!Cfg.Enabled)
            return;

        // QC HUD_PressedKeys spectatee gate (pressedkeys.qc:18-24), bypassed in the HUD editor:
        //   - a free-fly observer (spectatee_status < 0) never shows the cluster;
        //   - while playing / not chasing a player (spectatee_status <= 0) it shows only when
        //     hud_panel_pressedkeys >= 2 ("always"); chasing a player (> 0) shows it for any non-zero enable.
        if (!Configuring)
        {
            if (SpectateeStatus < 0)
                return;
            // QC autocvar_hud_panel_pressedkeys (the 0/1/2 enable cvar, NOT a _<suffix> tunable): read the
            // panel's enable cvar by its full name. Cfg.Enabled above already covers the != 0 / value-0 case.
            if (SpectateeStatus <= 0 && GlobalF($"hud_panel_{PanelId}", 1f) < 2f)
                return;
        }

        // Resolve the pressed-key bitmask: an explicit override (spectated player, fed by the net layer) wins;
        // otherwise sample the local +/- button state (the same source the movement sampler reads).
        int pressed = PressedKeysOverride ?? LocalPressedKeys();

        // QC HUD_Panel_DrawBg — the skin 9-slice frame (border_default for this panel). No-op when bg is "0".
        DrawBackground();

        // QC: pos/mySize start at the panel rect, then inset by the bg padding. We work in panel-LOCAL space
        // (origin = top-left), so the inset origin is just (padding, padding).
        float pad = Cfg.Padding;
        Vector2 pos = new(pad, pad);
        Vector2 mySize = new(Mathf.Max(0f, Size2.X - 2f * pad), Mathf.Max(0f, Size2.Y - 2f * pad));
        if (mySize.X <= 0f || mySize.Y <= 0f)
            return; // padding ate the whole panel — nothing to draw

        // QC: force a custom aspect — letterbox the cluster to keep its shape, centred in the leftover space.
        // The aspect comes from a user cvar; reject non-finite / non-positive values (NaN, +/-Inf, 0, garbage)
        // so a hostile/typo `set hud_panel_pressedkeys_aspect ...` can't collapse mySize to zero (Inf → newSize
        // ~0) or feed non-finite geometry into keysize / the DrawRect fallback below. QC's `if (aspect)` is just
        // a non-zero test; here `IsFinite && > 0` is the safe superset (a bad value falls through to the panel's
        // own aspect, exactly like aspect 0).
        float aspect = Aspect;
        if (float.IsFinite(aspect) && aspect > 0f)
        {
            Vector2 newSize = Vector2.Zero;
            if (mySize.X / mySize.Y > aspect)
            {
                newSize.X = aspect * mySize.Y;
                newSize.Y = mySize.Y;
                pos.X += (mySize.X - newSize.X) * 0.5f;
            }
            else
            {
                newSize.Y = 1f / aspect * mySize.X;
                newSize.X = mySize.X;
                pos.Y += (mySize.Y - newSize.Y) * 0.5f;
            }
            mySize = newSize;
        }

        bool attack = ShowAttack;

        // QC: keysize = vec2(mySize.x / (14/4), mySize.y / (3 - !attack)). 14/4 = 3.5 columns; rows = 3 with the
        // attack row, else 2.
        int rows = attack ? 3 : 2;
        Vector2 keysize = new(mySize.X / (14f / 4f), mySize.Y / rows);
        // Defensive: with mySize guarded >0 above and a finite aspect, keysize is finite & positive; bail on any
        // degenerate cell anyway so a zero/non-finite size never reaches DrawTextureRect/DrawRect (self-blank).
        if (!(keysize.X > 0f) || !(keysize.Y > 0f) || !float.IsFinite(keysize.X) || !float.IsFinite(keysize.Y))
            return;
        float kx = keysize.X;
        float fg = LiveFgAlpha; // QC panel_fg_alpha × hud fade
        var white = new Color(1f, 1f, 1f, fg);

        // --- optional attack row (top), then crouch/forward/jump row, then left/back/right row ---
        if (attack)
        {
            DrawKey(pos + new Vector2(3f / 4f * kx, 0f), keysize, "key_atck", (pressed & KEY_ATCK) != 0, white);
            DrawKey(pos + new Vector2(7f / 4f * kx, 0f), keysize, "key_atck2", (pressed & KEY_ATCK2) != 0, white);
            pos.Y += keysize.Y;
        }

        DrawKey(pos + new Vector2(0f, 0f), keysize, "key_crouch", (pressed & KEY_CROUCH) != 0, white);
        DrawKey(pos + new Vector2(5f / 4f * kx, 0f), keysize, "key_forward", (pressed & KEY_FORWARD) != 0, white);
        DrawKey(pos + new Vector2(10f / 4f * kx, 0f), keysize, "key_jump", (pressed & KEY_JUMP) != 0, white);
        pos.Y += keysize.Y;
        DrawKey(pos + new Vector2(1f / 4f * kx, 0f), keysize, "key_left", (pressed & KEY_LEFT) != 0, white);
        DrawKey(pos + new Vector2(5f / 4f * kx, 0f), keysize, "key_backward", (pressed & KEY_BACKWARD) != 0, white);
        DrawKey(pos + new Vector2(9f / 4f * kx, 0f), keysize, "key_right", (pressed & KEY_RIGHT) != 0, white);
    }

    /// <summary>Draw one key cell: the highlighted <c>&lt;base&gt;_inv</c> pic when held, else the resting
    /// <c>&lt;base&gt;</c> pic. Falls back to a drawn primitive if the skin art is missing so a key is never
    /// invisible (the contract's "fall back to a drawn primitive" rule).</summary>
    private void DrawKey(Vector2 at, Vector2 size, string baseName, bool held, Color modulate)
    {
        // QC drew these with drawpic_aspect_skin, which PRESERVES the pic's aspect inside the cell (no stretch).
        // The key_* art is square (128x128), so fit a square of side min(cell.x, cell.y) centred in the cell —
        // the faithful equivalent — instead of stretching the glyph when the cell isn't square (e.g. a non-default
        // hud_panel_pressedkeys_aspect). The fallback glyph uses the same fitted rect so both align.
        Rect2 rect = FitCentered(at, size);

        string pic = held ? baseName + "_inv" : baseName;
        if (DrawSkinPic(pic, rect, modulate))
            return;

        // Art miss: draw a simple key glyph — a filled box (brighter when held) with a 1px border — so the cluster
        // still reads. Inset slightly inside the fitted square like the skin pics' transparent margins.
        var inner = new Rect2(rect.Position + rect.Size * 0.06f, rect.Size * 0.88f);
        Color fill = held
            ? new Color(0.9f, 0.85f, 0.2f, modulate.A)   // highlighted (the _inv look)
            : new Color(0.25f, 0.28f, 0.34f, modulate.A); // resting
        DrawRect(inner, fill);
        DrawRect(inner, new Color(1f, 1f, 1f, modulate.A * 0.5f), filled: false, width: 1f);
    }

    /// <summary>Aspect-preserving fit of a square pic inside a cell: a square of side min(width,height) centred in
    /// the cell rect. The C# stand-in for QC's <c>drawpic_aspect_skin</c> centring (the key art is 1:1).</summary>
    private static Rect2 FitCentered(Vector2 at, Vector2 size)
    {
        float side = Mathf.Min(size.X, size.Y);
        var fitted = new Vector2(side, side);
        return new Rect2(at + (size - fitted) * 0.5f, fitted);
    }

    /// <summary>Assemble the PRESSED_KEYS bitmask from the local <see cref="BindTable"/> held-button state — the
    /// C# stand-in for the server packing <c>STAT(PRESSED_KEYS)</c> from the player's button bits. Goes fully dark
    /// when the console grabs input (BindTable.ReleaseAll clears every bool).</summary>
    private static int LocalPressedKeys()
    {
        int k = 0;
        if (BindTable.Forward > 0f) k |= KEY_FORWARD;
        if (BindTable.Forward < 0f) k |= KEY_BACKWARD;
        if (BindTable.Side < 0f) k |= KEY_LEFT;
        if (BindTable.Side > 0f) k |= KEY_RIGHT;
        if (BindTable.JumpHeld) k |= KEY_JUMP;
        if (BindTable.CrouchHeld) k |= KEY_CROUCH;
        if (BindTable.AttackHeld) k |= KEY_ATCK;
        if (BindTable.Attack2Held) k |= KEY_ATCK2;
        return k;
    }
}
