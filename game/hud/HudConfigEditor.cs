// Port of qcsrc/client/hud/hud_config.qc (+ hud_config.qh) — the CSQC HUD configure-mode editor.
//
// This is the interactive overlay that runs while `_hud_configure 1`: it lets the player drag/resize HUD panels
// with the mouse (grid-BEFORE-collision snapping) or the keyboard (arrows move/resize, Ctrl+Tab cycle, Ctrl+Space
// toggle, Ctrl+Z one-level undo, Ctrl+C/V copy/paste size, Ctrl+S export), and draws the grid + highlight border +
// tab preview + center-line guide. Every edit writes the normalized 0..1 `hud_panel_<id>_pos`/`_size` cvars
// directly — the SAME cvars the live panels resolve their layout from (HudPanel.LoadConfig), so a panel moves the
// instant the cvar changes.
//
// QC seams mirrored here:
//   * HUD_Panel_InputEvent (532) ............... HandleInput(InputEvent)
//   * HUD_Panel_Mouse (953) .................... MouseFrame() (called from Update each frame)
//   * HUD_Configure_Frame (1087) .............. Update() init/exit + grid draw scheduling
//   * HUD_Configure_PostDraw (1160) ........... _Draw() highlight border + tab preview + center line
//   * HUD_Panel_SetPos (169) / SetPosSize (306)  SetPanelPos / SetPanelPosSize (cvar write + normalization)
//   * HUD_Panel_CheckMove (98) / CheckResize (195)  CheckMove / CheckResize (collision snapping)
//   * HUD_Panel_Highlight (872) / Check_Mouse_Pos (807)  Highlight / CheckMousePos (cursor hit-test)
//   * HUD_Panel_Arrow_Action (411) ............. ArrowAction (keyboard move/resize step)
//   * HUD_Panel_FirstInDrawQ (842) ............. FirstInDrawQueue (draw-order promotion + _hud_panelorder)
//
// Mapping notes: QC `vid_conwidth/conheight` → the viewport size; QC `mousepos` → the viewport mouse position;
// QC `panel`/`HUD_Panel_UpdatePosSize()` (which reads `hud_panel_<name>_pos/_size` into globals) → reading those
// cvars per panel here. The pure geometry/normalization/stepping helpers are kept as small static methods so
// HudConfigEditorTests can mirror them exactly (the test project can't reference this Godot assembly).

using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Engine.Simulation; // CvarService (the shared menu/console store)
using XonoticGodot.Game.Menu;          // MenuState.Cvars

namespace XonoticGodot.Game.Hud;

public partial class HudConfigEditor : Control
{
    // ---- QC shift-state bits. mouseClicked uses S_MOUSE* (hud_config.qh:10-12); hudShiftState uses
    // S_SHIFT/S_CTRL/S_ALT (hud.qh:129-131). The two bitsets live in DIFFERENT fields, so the numeric overlap
    // is harmless; values mirror Base exactly. ----
    private const int S_MOUSE1 = 1;
    private const int S_MOUSE2 = 2;
    private const int S_SHIFT = 1; // hud.qh:129
    private const int S_CTRL = 2;  // hud.qh:130
    private const int S_ALT = 4;   // hud.qh:131

    // QC highlightedAction: 0 none, 1 move, 2 resize.
    private const int ActionNone = 0;
    private const int ActionMove = 1;
    private const int ActionResize = 2;

    // QC engine cursor types (cursor_type; CURSOR_* in the CSQC defs): what the pointer would do where it is.
    private const int CursorNormal = 0;
    private const int CursorMove = 1;    // panel interior → drag-move
    private const int CursorResize = 2;  // topleft/bottomright border → "\" diagonal resize
    private const int CursorResize2 = 3; // topright/bottomleft border → "/" diagonal resize

    // ---- the panels the editor operates on (HudManager owns them; only PANEL_CONFIG_MAIN ones are editable) ----
    private readonly Hud _hud;
    private CvarService Cvars => MenuState.Cvars;

    // ---- input/cursor state (QC hud_config.qh globals) ----
    private int _mouseClicked;
    private int _prevMouseClicked;
    private float _prevMouseClickedTime;
    private Vector2 _prevMouseClickedPos;
    private int _hudShiftState;
    private Vector2 _mousePos;

    // ---- highlight / drag state ----
    private HudPanel? _highlightedPanel;
    private int _highlightedAction;
    private int _resizeCorner;                 // 1 TL, 2 TR, 3 BL, 4 BR (QC resizeCorner)
    private Vector2 _panelClickDistance;
    private Vector2 _panelClickResizeOrigin;
    private Vector2 _highlightedInitialPos;
    private Vector2 _highlightedInitialSize;

    // ---- hover state (QC HUD_Panel_Check_Mouse_Pos ran every non-clicked frame → cursor_type + hover fill) ----
    private HudPanel? _hoverPanel;
    private int _hoverCursor;
    private int _cursorShapeApplied = -1;      // last Godot cursor shape pushed (change-latched)

    // ---- undo (one level) + copy/paste size ----
    private Vector2 _panelPosBackup;
    private Vector2 _panelSizeBackup;
    private HudPanel? _highlightedBackup;
    private Vector2 _panelSizeCopied;

    // ---- per-action collision flag (QC global hud_configure_checkcollisions, distinct from the cvar) ----
    private bool _checkCollisions;

    // ---- center-line guide timing ----
    private float _centerlineTime;
    private float _pressedKeyTime;

    // ---- Ctrl+Tab cycle state (QC tab_panels[]/tab_panel/tab_panel_pos/tab_backward) ----
    private readonly HashSet<HudPanel> _tabPanels = new();
    private HudPanel? _tabPanel;
    private Vector2 _tabPanelPos;
    private bool _tabBackward;

    // ---- draw order (QC panel_order[] + _hud_panelorder); index = panel's stable editor id ----
    private List<int> _panelOrder = new();

    // ---- frame/entry bookkeeping ----
    private bool _wasConfiguring;
    private float _menuAlphaPrev;

    private float Time => Godot.Time.GetTicksMsec() / 1000f;

    public HudConfigEditor(Hud hud)
    {
        _hud = hud;
        Name = "HudConfigEditor";
        MouseFilter = MouseFilterEnum.Ignore; // the editor reads input via HandleInput, never via _GuiInput
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = 100; // draw over the panels (QC PostDraw runs after every panel draw)
        Visible = false;
        RegisterEditorCommands();
    }

    /// <summary>
    /// Safety net for teardown mid-configure: the editor's normal exit edge (<see cref="Update"/>'s
    /// <c>else if (_wasConfiguring)</c> branch) only runs while the node is alive and ticking, so disconnecting /
    /// quitting to the menu while <c>_hud_configure</c> is 1 would free this node with the process-global side
    /// effects still latched — leaving the OS pointer freed forever (mouse-look dead in the next match) and the
    /// next match booting straight into the editor. Clear all of them here when the node leaves the tree.
    /// </summary>
    public override void _ExitTree()
    {
        MouseCapture.HudEditorWantsCursor = false; // re-applies capture (the setter calls MouseCapture.Apply)
        SetCursorShape(CursorNormal);              // drop the Move/diagonal OS cursor shape
        // Don't let the transient configure flag survive the match, or the next one boots into the editor.
        if (MenuState.Cvars.GetFloat("_hud_configure") != 0f)
            MenuState.Cvars.Set("_hud_configure", "0");
    }

    /// <summary>
    /// Register the HUD-editor console verbs on the shared interpreter (idempotent — RegisterCommand overwrites
    /// by name, so re-registration on a later match's Hud is harmless). Base ships these as commands.cfg
    /// aliases (<c>menu_showhudexit "menu_cmd directmenu HUDExit"</c>, <c>menu_showhudoptions "menu_cmd
    /// directpanelhudmenu ${* ?}"</c>) plus the client <c>hud</c> command (cl_cmd.qc:288); the port's cfg tree
    /// never execs those aliases, so the editor registers the equivalents itself. The menu_show* verbs route
    /// into <see cref="Menu.MenuCommand"/> (the dialog opener); <c>hud save &lt;name&gt;</c> runs the
    /// <see cref="ExportCfg"/> port. Without these, <see cref="RunLocalCmd"/>'s lines fell through the
    /// interpreter's unknown-command route (a server forward) and ESC / Ctrl+S / double-click did nothing.
    /// </summary>
    private void RegisterEditorCommands()
    {
        var interp = MenuState.Interp;
        if (interp is null) return; // headless/tool contexts without the shared interpreter
        interp.RegisterCommand("menu_showhudexit", _ => Menu.MenuCommand.Run("menu_showhudexit"));
        interp.RegisterCommand("menu_showhudoptions", a => Menu.MenuCommand.Run(
            a.Count > 1 ? "menu_showhudoptions " + a[1] : "menu_showhudoptions"));
        interp.RegisterCommand("hud", a =>
        {
            if (a.Count >= 3 && a[1] == "save")
                ExportCfg(a[2]);
            else
                XonoticGodot.Common.Diagnostics.Log.Info(
                    "Usage: hud save <configname> — export the current HUD layout (QC `hud save`)");
        });
    }

    // =================================================================================================
    //  Public accessors / cvar gate
    // =================================================================================================

    /// <summary>QC <c>autocvar__hud_configure</c>: whether the editor is active.</summary>
    public bool Configuring => Cvars.GetFloat("_hud_configure") != 0f;

    private bool MenuAlphaActive => Cvars.GetFloat("_menu_alpha") != 0f;
    private float MenuAlpha => Cvars.GetFloat("_menu_alpha");

    // =================================================================================================
    //  Per-frame driver (QC HUD_Configure_Frame + HUD_Panel_Mouse + the PostDraw scheduling)
    // =================================================================================================

    /// <summary>
    /// Per-frame tick, called by <see cref="Hud._Process"/>. Handles configure-mode entry/exit init+cleanup
    /// (QC <c>HUD_Configure_Frame</c>), runs the mouse drag/resize (QC <c>HUD_Panel_Mouse</c>), and schedules the
    /// overlay redraw (the grid + highlight borders drawn in <see cref="_Draw"/>, QC <c>HUD_Configure_PostDraw</c>).
    /// </summary>
    public void Update(Vector2 viewport, float fade)
    {
        bool configuring = Configuring;

        // QC HUD_Configure_Frame guard (1091): force-exit if a demo is playing, the match is at final
        // intermission, or the scoreboard is up — the editor can't run over those. We can observe the scoreboard
        // being up (Hud.ScoreboardFade == 1); the editor then exits and the rest of the frame runs the cleanup.
        if (configuring && _hud.ScoreboardFade >= 1f)
        {
            ExitForce();
            configuring = false;
        }

        // QC HUD_Configure_Frame: init on entry.
        if (configuring && !_wasConfiguring)
        {
            _hudShiftState = 0;
            EnsurePanelOrder();
            // (QC also pokes every panel's update_time so common cvars reload; our panels re-resolve on the
            // cvar Changed event, so no explicit poke is needed.)
        }

        if (configuring)
        {
            // NOTE this check mirrors QC: _menu_alpha isn't updated the frame the menu opens.
            float ma = MenuAlpha;
            if (ma != _menuAlphaPrev)
                _menuAlphaPrev = ma;

            Visible = true;
            Size = viewport;
            // Free the OS pointer for the editor (Base shows the engine cursor over the live game here —
            // cursor_type; the net layer's capture reassert doesn't know the editor, so this override does).
            MouseCapture.HudEditorWantsCursor = true;
            MouseFrame(viewport); // QC HUD_Panel_Mouse (mouse drag/resize, run every frame while configuring)
            QueueRedraw();        // schedule the grid + highlight overlay draw (QC HUD_Configure_PostDraw)
        }
        else if (_wasConfiguring)
        {
            // QC HUD_Configure_Frame exit branch: drop highlight/tab state when leaving the editor.
            Visible = false;
            ClearHighlight();
            ResetTabPanels();
            _tabPanel = null;
            _hoverPanel = null;
            MouseCapture.HudEditorWantsCursor = false; // recapture for play (QC Exit_Force → CURSOR_NORMAL)
            SetCursorShape(CursorNormal);
        }

        _prevMouseClicked = _mouseClicked; // QC: prevMouseClicked = mouseClicked at end of frame (view.qc:1108)
        _wasConfiguring = configuring;
    }

    // =================================================================================================
    //  Input event handler (QC HUD_Panel_InputEvent)
    // =================================================================================================

    /// <summary>
    /// Routed from <c>NetGame._UnhandledInput</c>. Port of <c>HUD_Panel_InputEvent</c> (hud_config.qc:532):
    /// intercepts mouse/keyboard while the editor is active. Returns true when the event was consumed (the caller
    /// should mark it handled so it doesn't fall through to gameplay binds). A no-op (false) when not configuring.
    /// </summary>
    public bool HandleInput(InputEvent @event)
    {
        if (!Configuring)
            return false;

        // bInputType 3 (mouse move): update mousepos. QC returns true.
        if (@event is InputEventMouseMotion motion)
        {
            _mousePos = motion.Position;
            return true;
        }

        // Mouse buttons (K_MOUSE1/K_MOUSE2) and keys map to bInputType 0 (press) / 1 (release).
        bool keyPressed;
        bool isMouseButton;
        int primary; // a stable key id in our K_* space (negative for mouse buttons)
        if (@event is InputEventMouseButton mb)
        {
            keyPressed = mb.Pressed;
            isMouseButton = true;
            primary = KeyIdForMouseButton(mb.ButtonIndex);
            _mousePos = mb.Position;
            if (primary == 0) return false; // a button we don't track (wheel etc.)
        }
        else if (@event is InputEventKey k && !k.Echo)
        {
            keyPressed = k.Pressed;
            isMouseButton = false;
            primary = (int)k.Keycode;
        }
        else
        {
            return false;
        }

        // QC: block any input while a menu dialog is fading (don't block mousepos, handled above).
        if (MenuAlphaActive)
        {
            _hudShiftState = 0;
            _mouseClicked = 0;
            return true;
        }

        int hudShiftStatePrev = _hudShiftState;
        int mouseClickedPrev = _mouseClicked;

        // Track modifier + mouse-button state (QC sets/clears S_ALT/S_CTRL/S_SHIFT and S_MOUSE1/2).
        if (isMouseButton)
        {
            int bit = primary == KMouse1 ? S_MOUSE1 : primary == KMouse2 ? S_MOUSE2 : 0;
            if (keyPressed) _mouseClicked |= bit; else _mouseClicked &= ~bit;
        }
        else
        {
            int bit = ShiftBitFor(primary);
            if (bit != 0)
            {
                if (keyPressed) _hudShiftState |= bit; else _hudShiftState &= ~bit;
            }
        }

        // QC: on CTRL release commit the tab-cycle selection.
        if (IsCtrl(primary))
        {
            if (!keyPressed)
            {
                if (_tabPanel is not null)
                {
                    _highlightedPanel = _tabPanel;
                    _highlightedAction = ActionNone;
                    FirstInDrawQueue(EditorId(_highlightedPanel));
                    SyncHighlightFlags();
                }
                _tabPanel = null;
                ResetTabPanels();
            }
        }

        // ESC: open the menu-side exit dialog (QC menu_showhudexit). We toggle the in-game menu / exit.
        if (primary == KEscape)
        {
            if (!keyPressed) return true;
            RunLocalCmd("menu_showhudexit");
            return true;
        }

        // Ctrl+Backspace: force-exit the editor (QC HUD_Configure_Exit_Force → _hud_configure 0).
        if (primary == KBackspace && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed) return true;
            ExitForce();
            return true;
        }

        // Ctrl+Tab: cycle the selected panel (Shift = backward). QC band-ordered cycle.
        if (primary == KTab && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed || _mouseClicked != 0) return true;
            CtrlTabCycle();
            return true;
        }

        // Ctrl+Space: enable/disable the highlighted panel (or toggle the dock when nothing is highlighted).
        if (primary == KSpace && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed || _mouseClicked != 0) return true;
            CtrlSpaceToggle();
            return true;
        }

        // Ctrl+C: copy the highlighted panel's size.
        if (primary == KeyC && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed || _mouseClicked != 0) return true;
            if (_highlightedPanel is not null)
                _panelSizeCopied = PanelSizePx(_highlightedPanel, GetViewportRect().Size);
            return true;
        }

        // Ctrl+V: paste the copied size onto the highlighted panel.
        if (primary == KeyV && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed || _mouseClicked != 0) return true;
            CtrlVPaste();
            return true;
        }

        // Ctrl+Z: one-level undo of the last move/resize/paste.
        if (primary == KeyZ && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed || _mouseClicked != 0) return true;
            UndoLast();
            return true;
        }

        // Ctrl+S: export the HUD config (QC localcmd "hud save myconfig").
        if (primary == KeyS && (_hudShiftState & S_CTRL) != 0)
        {
            if (!keyPressed || _mouseClicked != 0) return true;
            RunLocalCmd("hud save myconfig");
            return true;
        }

        // Arrows: move/resize the highlighted panel (grid-snapped, Ctrl reduces / disables collision).
        if (IsArrow(primary))
        {
            if (!keyPressed)
            {
                _pressedKeyTime = 0f;
                return true;
            }
            if (_pressedKeyTime == 0f)
                _pressedKeyTime = Time;
            if (_mouseClicked == 0)
                ArrowAction(primary);
            return true;
        }

        // Enter/KP-Enter/Space (no Ctrl): open the per-panel options menu for the highlighted panel.
        if (primary == KEnter || primary == KSpace || primary == KKpEnter)
        {
            if (!keyPressed) return true;
            if (_highlightedPanel is not null)
                EnablePanelMenu();
            return true;
        }

        // K_PAUSE must pass through so the `bind PAUSE pause` can pause/resume even while the HUD editor is open
        // (QC hud_config.qc:792-793 `else if (nPrimary == K_PAUSE) return false;`). Without this the editor would
        // swallow the pause key globally.
        if (primary == KPause)
            return false;

        // Otherwise consumed (mirrors QC default `return true`, swallowing other keys while configuring), unless
        // it's a pure modifier-state change that should let a console bind through — but with no live console
        // bind dependency here (the console is gated one level up at NetGame._UnhandledInput), swallow
        // consistently to keep gameplay frozen during edit.
        _ = hudShiftStatePrev;
        _ = mouseClickedPrev;
        return true;
    }

    // =================================================================================================
    //  Mouse drag/resize per-frame (QC HUD_Panel_Mouse)
    // =================================================================================================

    private void MouseFrame(Vector2 viewport)
    {
        if (MenuAlpha == 1f)
            return;

        // Track the live viewport mouse position (QC reads the engine cursor each frame).
        _mousePos = GetViewport()?.GetMousePosition() ?? _mousePos;

        if (_mouseClicked != 0)
        {
            if (_prevMouseClicked == 0)
            {
                if (_tabPanel is not null)
                {
                    _tabPanel = null;
                    ResetTabPanels();
                }
                Highlight(viewport, (_mouseClicked & S_MOUSE1) != 0); // sets _highlightedPanel/action/etc.
                if (_highlightedPanel is not null)
                {
                    _highlightedInitialPos = PanelPosPx(_highlightedPanel, viewport);
                    _highlightedInitialSize = PanelSizePx(_highlightedPanel, viewport);
                }
                // doubleclick → open the panel's options menu.
                if ((_mouseClicked & S_MOUSE1) != 0 && Time - _prevMouseClickedTime < 0.4f
                    && _highlightedPanel is not null && _prevMouseClickedPos == _mousePos)
                {
                    _mouseClicked = 0;
                    EnablePanelMenu();
                }
                else if ((_mouseClicked & S_MOUSE1) != 0)
                {
                    _prevMouseClickedTime = Time;
                    _prevMouseClickedPos = _mousePos;
                }
            }

            if (_highlightedPanel is not null)
            {
                Vector2 curPos = PanelPosPx(_highlightedPanel, viewport);
                Vector2 curSize = PanelSizePx(_highlightedPanel, viewport);
                if (_highlightedInitialPos != curPos || _highlightedInitialSize != curSize)
                {
                    _checkCollisions = (_hudShiftState & S_CTRL) == 0 && CvarCheckCollisions();
                    _panelPosBackup = _highlightedInitialPos;
                    _panelSizeBackup = _highlightedInitialSize;
                    _highlightedBackup = _highlightedPanel;
                }
                else
                {
                    // clicked a panel inside another and not moving it → don't "fix" it (disable collision).
                    _checkCollisions = false;
                }

                if (Time - _prevMouseClickedTime > 0.25f)
                    _centerlineTime = Time + 0.5f;
            }

            if (_highlightedAction == ActionMove)
                SetPanelPos(_mousePos - _panelClickDistance, viewport);
            else if (_highlightedAction == ActionResize)
            {
                Vector2 mySize = ResizeSizeFromMouse(viewport);
                SetPanelPosSize(mySize, viewport);
            }
        }
        else
        {
            if (_prevMouseClicked != 0)
                _highlightedAction = ActionNone;

            // Hover hit-test (QC 1048-1055): every non-clicked frame resolve which panel the cursor is over
            // (interior → move, border → resize) — drives the white hover fill in _Draw + the cursor shape.
            _hoverCursor = CheckMousePos(viewport, allowMove: true, out _hoverPanel);
        }

        // Engine cursor shape (QC cursor_type): while dragging show the active action's shape, else the hover's.
        int cursorType = _mouseClicked != 0
            ? _highlightedAction switch
            {
                ActionMove => CursorMove,
                ActionResize => _resizeCorner is 1 or 4 ? CursorResize : CursorResize2,
                _ => CursorNormal,
            }
            : _hoverCursor;
        SetCursorShape(cursorType);
    }

    /// <summary>
    /// Port of <c>HUD_Panel_Check_Mouse_Pos</c> (807): the pure hover hit-test — which MAIN panel is under the
    /// cursor and what the pointer would do there (move interior / one of the four resize borders), walking the
    /// draw order like <see cref="Highlight"/> but without mutating any selection state. Returns the QC cursor
    /// type; <paramref name="hovered"/> gets the hit panel (QC leaves it in the global <c>panel</c>, which the
    /// caller's hover drawfill reads).
    /// </summary>
    private int CheckMousePos(Vector2 viewport, bool allowMove, out HudPanel? hovered)
    {
        foreach (HudPanel p in PanelsInDrawOrder())
        {
            if (!p.ConfigFlags.HasFlag(PanelConfig.Main))
                continue;
            Vector2 pos = PanelPosPx(p, viewport);
            Vector2 size = PanelSizePx(p, viewport);
            float border = Mathf.Max(8f, PanelBorderPx(p)); // FORCED border (QC: a tiny border stays grabbable)
            Vector2 m = _mousePos;

            hovered = p;
            if (allowMove && m.X > pos.X && m.Y > pos.Y && m.X < pos.X + size.X && m.Y < pos.Y + size.Y)
                return CursorMove;
            if (m.X >= pos.X - border && m.Y >= pos.Y - border
                && m.X <= pos.X + 0.5f * size.X && m.Y <= pos.Y + 0.5f * size.Y)
                return CursorResize;  // topleft border
            if (m.X >= pos.X + 0.5f * size.X && m.Y >= pos.Y - border
                && m.X <= pos.X + size.X + border && m.Y <= pos.Y + 0.5f * size.Y)
                return CursorResize2; // topright border
            if (m.X >= pos.X - border && m.Y >= pos.Y + 0.5f * size.Y
                && m.X <= pos.X + 0.5f * size.X && m.Y <= pos.Y + size.Y + border)
                return CursorResize2; // bottomleft border
            if (m.X >= pos.X + 0.5f * size.X && m.Y >= pos.Y + 0.5f * size.Y
                && m.X <= pos.X + size.X + border && m.Y <= pos.Y + size.Y + border)
                return CursorResize;  // bottomright border
        }
        hovered = null;
        return CursorNormal;
    }

    /// <summary>Push a QC cursor type onto the OS pointer (Base tiled its own cursor art; the OS shapes carry
    /// the same affordance: move arrows over interiors, "\"/"/" diagonals over resize corners). Change-latched
    /// so the DisplayServer isn't poked every frame.</summary>
    private void SetCursorShape(int cursorType)
    {
        if (cursorType == _cursorShapeApplied) return;
        _cursorShapeApplied = cursorType;
        Input.SetDefaultCursorShape(cursorType switch
        {
            CursorMove => Input.CursorShape.Move,
            CursorResize => Input.CursorShape.Fdiagsize,  // "\" (TL/BR corners)
            CursorResize2 => Input.CursorShape.Bdiagsize, // "/" (TR/BL corners)
            _ => Input.CursorShape.Arrow,
        });
    }

    /// <summary>Compute the dragged size from the current mouse position per the active resize corner (QC
    /// HUD_Panel_Mouse resize block, 1022-1042).</summary>
    private Vector2 ResizeSizeFromMouse(Vector2 viewport)
    {
        Vector2 m = _mousePos;
        Vector2 d = _panelClickDistance;
        Vector2 o = _panelClickResizeOrigin;
        return _resizeCorner switch
        {
            1 => new Vector2(o.X - (m.X - d.X), o.Y - (m.Y - d.Y)),
            2 => new Vector2(m.X + d.X - o.X, d.Y + o.Y - m.Y),
            3 => new Vector2(o.X + d.X - m.X, m.Y + d.Y - o.Y),
            _ => new Vector2(m.X - (o.X - d.X), m.Y - (o.Y - d.Y)),
        };
    }

    // =================================================================================================
    //  Highlight / cursor hit-test (QC HUD_Panel_Highlight + HUD_Panel_Check_Mouse_Pos)
    // =================================================================================================

    /// <summary>Port of <c>HUD_Panel_Highlight</c> (872): walk panels in draw order, set the highlighted panel +
    /// action (move / resize-corner) from the mouse position, and seed the click distance / resize origin.</summary>
    private void Highlight(Vector2 viewport, bool allowMove)
    {
        foreach (HudPanel p in PanelsInDrawOrder())
        {
            if (!p.ConfigFlags.HasFlag(PanelConfig.Main))
                continue;
            Vector2 pos = PanelPosPx(p, viewport);
            Vector2 size = PanelSizePx(p, viewport);
            float border = Mathf.Max(8f, PanelBorderPx(p)); // FORCED border so a tiny border is still grabbable

            Vector2 m = _mousePos;
            // move
            if (allowMove && m.X > pos.X && m.Y > pos.Y && m.X < pos.X + size.X && m.Y < pos.Y + size.Y)
            {
                SetHighlighted(p, ActionMove);
                FirstInDrawQueue(EditorId(p));
                _panelClickDistance = m - pos;
                return;
            }
            // resize from topleft border (corner 1)
            if (m.X >= pos.X - border && m.Y >= pos.Y - border && m.X <= pos.X + 0.5f * size.X && m.Y <= pos.Y + 0.5f * size.Y)
            {
                SetHighlighted(p, ActionResize);
                FirstInDrawQueue(EditorId(p));
                _resizeCorner = 1;
                _panelClickDistance = m - pos;
                _panelClickResizeOrigin = pos + size;
                return;
            }
            // resize from topright border (corner 2)
            if (m.X >= pos.X + 0.5f * size.X && m.Y >= pos.Y - border && m.X <= pos.X + size.X + border && m.Y <= pos.Y + 0.5f * size.Y)
            {
                SetHighlighted(p, ActionResize);
                FirstInDrawQueue(EditorId(p));
                _resizeCorner = 2;
                _panelClickDistance = new Vector2(size.X - m.X + pos.X, m.Y - pos.Y);
                _panelClickResizeOrigin = new Vector2(pos.X, pos.Y + size.Y);
                return;
            }
            // resize from bottomleft border (corner 3)
            if (m.X >= pos.X - border && m.Y >= pos.Y + 0.5f * size.Y && m.X <= pos.X + 0.5f * size.X && m.Y <= pos.Y + size.Y + border)
            {
                SetHighlighted(p, ActionResize);
                FirstInDrawQueue(EditorId(p));
                _resizeCorner = 3;
                _panelClickDistance = new Vector2(m.X - pos.X, size.Y - m.Y + pos.Y);
                _panelClickResizeOrigin = new Vector2(pos.X + size.X, pos.Y);
                return;
            }
            // resize from bottomright border (corner 4)
            if (m.X >= pos.X + 0.5f * size.X && m.Y >= pos.Y + 0.5f * size.Y && m.X <= pos.X + size.X + border && m.Y <= pos.Y + size.Y + border)
            {
                SetHighlighted(p, ActionResize);
                FirstInDrawQueue(EditorId(p));
                _resizeCorner = 4;
                _panelClickDistance = size - m + pos;
                _panelClickResizeOrigin = pos;
                return;
            }
        }
        ClearHighlight();
    }

    private void SetHighlighted(HudPanel p, int action)
    {
        _highlightedPanel = p;
        _highlightedAction = action;
        SyncHighlightFlags();
    }

    private void ClearHighlight()
    {
        _highlightedPanel = null;
        _highlightedAction = ActionNone;
        SyncHighlightFlags();
    }

    /// <summary>Push <see cref="HudPanel.IsHighlighted"/>/<see cref="HudPanel.IsTabSelected"/> onto the live
    /// panels so the panels (and tests) can query editor selection state.</summary>
    private void SyncHighlightFlags()
    {
        foreach (HudPanel p in _hud.Panels)
        {
            p.IsHighlighted = ReferenceEquals(p, _highlightedPanel);
            p.IsTabSelected = ReferenceEquals(p, _tabPanel);
        }
    }

    // =================================================================================================
    //  SetPos / SetPosSize — cvar write-back with grid-BEFORE-collision snapping (QC 169 / 306)
    // =================================================================================================

    /// <summary>Port of <c>HUD_Panel_SetPos</c> (169). Grid snaps the pos (BEFORE collision), then collision
    /// snaps, then clamps inside the screen, then writes the normalized <c>_pos</c> cvar.</summary>
    private void SetPanelPos(Vector2 pos, Vector2 viewport)
    {
        if (_highlightedPanel is null) return;
        Vector2 mySize = PanelSizePx(_highlightedPanel, viewport);

        if (CvarGrid())
        {
            Vector2 grid = GridSize();
            Vector2 real = RealGridSize(viewport, grid);
            pos = SnapToGrid(pos, viewport, grid, real); // grid BEFORE collision (QC 178-182)
        }

        if (_checkCollisions)
            pos = CheckMove(pos, mySize, viewport);

        pos.X = Mathf.Clamp(pos.X, 0f, viewport.X - mySize.X);
        pos.Y = Mathf.Clamp(pos.Y, 0f, viewport.Y - mySize.Y);

        WritePosCvar(_highlightedPanel, pos, viewport);
    }

    /// <summary>Port of <c>HUD_Panel_SetPosSize</c> (306). Min-size cap → derive pos from resize corner → clamp to
    /// screen edges → grid snap (BEFORE collision) → collision snap → min-size cap again → re-derive pos → write
    /// the normalized <c>_size</c> + <c>_pos</c> cvars.</summary>
    private void SetPanelPosSize(Vector2 mySize, Vector2 viewport)
    {
        if (_highlightedPanel is null) return;
        Vector2 origin = _panelClickResizeOrigin;

        // minimum panel size cap (QC 314-315)
        mySize.X = Mathf.Max(0.025f * viewport.X, mySize.X);
        mySize.Y = Mathf.Max(0.025f * viewport.Y, mySize.Y);
        // NOTE: QC adds a chat-panel-specific min size (317-321); the chat panel here has no con_chatsize
        // dependency, so the generic cap applies (deliberate minor deviation, documented).

        Vector2 myPos = PosFromCorner(_resizeCorner, origin, mySize);

        // left/top screen edges (349-352)
        if (myPos.X < 0f) mySize.X += myPos.X;
        if (myPos.Y < 0f) mySize.Y += myPos.Y;
        // bottom/right screen edges (355-358)
        if (myPos.X + mySize.X > viewport.X) mySize.X = viewport.X - myPos.X;
        if (myPos.Y + mySize.Y > viewport.Y) mySize.Y = viewport.Y - myPos.Y;

        // grid BEFORE collision (363-368)
        if (CvarGrid())
        {
            Vector2 grid = GridSize();
            Vector2 real = RealGridSize(viewport, grid);
            mySize = SnapSizeToGrid(mySize, viewport, grid, real);
        }

        if (_checkCollisions)
            mySize = CheckResize(mySize, origin, viewport);

        // minimum panel size cap once more (373-375)
        mySize.X = Mathf.Max(0.025f * viewport.X, mySize.X);
        mySize.Y = Mathf.Max(0.025f * viewport.Y, mySize.Y);

        myPos = PosFromCorner(_resizeCorner, origin, mySize); // re-derive pos (377-397)

        WriteSizeCvar(_highlightedPanel, mySize, viewport);
        WritePosCvar(_highlightedPanel, myPos, viewport);
    }

    /// <summary>QC SetPosSize corner→pos derivation (327-346 / 378-397).</summary>
    private static Vector2 PosFromCorner(int corner, Vector2 origin, Vector2 size) => corner switch
    {
        1 => new Vector2(origin.X - size.X, origin.Y - size.Y),
        2 => new Vector2(origin.X, origin.Y - size.Y),
        3 => new Vector2(origin.X - size.X, origin.Y),
        _ => new Vector2(origin.X, origin.Y),
    };

    // =================================================================================================
    //  Collision snapping (QC HUD_Panel_CheckMove 98 / HUD_Panel_CheckResize 195)
    // =================================================================================================

    /// <summary>Port of <c>HUD_Panel_CheckMove</c> (98): if the moved panel would overlap another enabled MAIN
    /// panel (expanded by its border), push it to the nearest non-overlapping edge.</summary>
    private Vector2 CheckMove(Vector2 myPos, Vector2 mySize, Vector2 viewport)
    {
        Vector2 myTarget = myPos;
        foreach (HudPanel p in _hud.Panels)
        {
            if (!p.ConfigFlags.HasFlag(PanelConfig.Main)) continue;
            if (ReferenceEquals(p, _highlightedPanel)) continue;
            if (!PanelEnabled(p)) continue;

            Vector2 pPos = PanelPosPx(p, viewport);
            Vector2 pSize = PanelSizePx(p, viewport);
            float b = PanelBorderPx(p);
            pPos -= new Vector2(b, b);
            pSize += new Vector2(2f * b, 2f * b);

            if (myPos.Y + mySize.Y < pPos.Y) continue;
            if (myPos.Y > pPos.Y + pSize.Y) continue;
            if (myPos.X + mySize.X < pPos.X) continue;
            if (myPos.X > pPos.X + pSize.X) continue;

            // collision: push toward the nearest edge based on relative centers (QC 128-161)
            Vector2 myC = new(myPos.X + 0.5f * mySize.X, myPos.Y + 0.5f * mySize.Y);
            Vector2 tC = new(pPos.X + 0.5f * pSize.X, pPos.Y + 0.5f * pSize.Y);

            if (myC.X < tC.X && myC.Y < tC.Y) // top left of target
            {
                if (myPos.X + mySize.X - pPos.X < myPos.Y + mySize.Y - pPos.Y) myTarget.X = pPos.X - mySize.X;
                else myTarget.Y = pPos.Y - mySize.Y;
            }
            else if (myC.X > tC.X && myC.Y < tC.Y) // top right
            {
                if (pPos.X + pSize.X - myPos.X < myPos.Y + mySize.Y - pPos.Y) myTarget.X = pPos.X + pSize.X;
                else myTarget.Y = pPos.Y - mySize.Y;
            }
            else if (myC.X < tC.X && myC.Y > tC.Y) // bottom left
            {
                if (myPos.X + mySize.X - pPos.X < pPos.Y + pSize.Y - myPos.Y) myTarget.X = pPos.X - mySize.X;
                else myTarget.Y = pPos.Y + pSize.Y;
            }
            else if (myC.X > tC.X && myC.Y > tC.Y) // bottom right
            {
                if (pPos.X + pSize.X - myPos.X < pPos.Y + pSize.Y - myPos.Y) myTarget.X = pPos.X + pSize.X;
                else myTarget.Y = pPos.Y + pSize.Y;
            }
        }
        return myTarget;
    }

    /// <summary>Port of <c>HUD_Panel_CheckResize</c> (195): clamp the resized size so the panel doesn't grow into
    /// another enabled MAIN panel, picking the limiting side by the aspect-ratio test.</summary>
    private Vector2 CheckResize(Vector2 mySize, Vector2 resizeOrigin, Vector2 viewport)
    {
        float ratio = mySize.X / mySize.Y;
        foreach (HudPanel p in _hud.Panels)
        {
            if (!p.ConfigFlags.HasFlag(PanelConfig.Main)) continue;
            if (ReferenceEquals(p, _highlightedPanel)) continue;
            if (!PanelEnabled(p)) continue;

            Vector2 pPos = PanelPosPx(p, viewport);
            Vector2 pSize = PanelSizePx(p, viewport);
            float b = PanelBorderPx(p);
            pPos -= new Vector2(b, b);
            pSize += new Vector2(2f * b, 2f * b);
            Vector2 targEnd = pPos + pSize;

            // resizeorigin inside target → skip this panel (QC 217)
            if (resizeOrigin.X > pPos.X && resizeOrigin.X < targEnd.X
                && resizeOrigin.Y > pPos.Y && resizeOrigin.Y < targEnd.Y)
                continue;

            Vector2 dist;
            switch (_resizeCorner)
            {
                case 1:
                    if (resizeOrigin.X <= pPos.X || resizeOrigin.Y <= pPos.Y) continue;
                    if (targEnd.X <= resizeOrigin.X - mySize.X || targEnd.Y <= resizeOrigin.Y - mySize.Y) continue;
                    dist = new Vector2(resizeOrigin.X - targEnd.X, resizeOrigin.Y - targEnd.Y);
                    break;
                case 2:
                    if (resizeOrigin.X >= targEnd.X || resizeOrigin.Y <= pPos.Y) continue;
                    if (pPos.X >= resizeOrigin.X + mySize.X || targEnd.Y <= resizeOrigin.Y - mySize.Y) continue;
                    dist = new Vector2(pPos.X - resizeOrigin.X, resizeOrigin.Y - targEnd.Y);
                    break;
                case 3:
                    if (resizeOrigin.X <= pPos.X || resizeOrigin.Y >= targEnd.Y) continue;
                    if (targEnd.X <= resizeOrigin.X - mySize.X || pPos.Y >= resizeOrigin.Y + mySize.Y) continue;
                    dist = new Vector2(resizeOrigin.X - targEnd.X, pPos.Y - resizeOrigin.Y);
                    break;
                default: // 4
                    if (resizeOrigin.X >= targEnd.X || resizeOrigin.Y >= targEnd.Y) continue;
                    if (pPos.X >= resizeOrigin.X + mySize.X || pPos.Y >= resizeOrigin.Y + mySize.Y) continue;
                    dist = new Vector2(pPos.X - resizeOrigin.X, pPos.Y - resizeOrigin.Y);
                    break;
            }

            if (dist.Y <= 0f || dist.X / dist.Y > ratio)
                mySize.X = Mathf.Min(mySize.X, dist.X);
            else
                mySize.Y = Mathf.Min(mySize.Y, dist.Y);
        }
        return mySize;
    }

    // =================================================================================================
    //  Keyboard move/resize (QC HUD_Panel_Arrow_Action 411)
    // =================================================================================================

    private void ArrowAction(int primary)
    {
        if (_highlightedPanel is null) return;
        Vector2 viewport = GetViewportRect().Size;

        _checkCollisions = (_hudShiftState & S_CTRL) == 0 && CvarCheckCollisions();

        bool vertical = primary == KUpArrow || primary == KDownArrow;
        float step = ArrowStep(vertical, viewport);

        _highlightedInitialPos = PanelPosPx(_highlightedPanel, viewport);
        _highlightedInitialSize = PanelSizePx(_highlightedPanel, viewport);

        if ((_hudShiftState & S_ALT) != 0) // resize
        {
            _resizeCorner = primary switch
            {
                KUpArrow => 1,
                KRightArrow => 2,
                KLeftArrow => 3,
                _ => 4, // K_DOWNARROW
            };

            // ctrl+arrow reduces the size (and mirrors the corner). QC 468-472.
            if ((_hudShiftState & S_CTRL) != 0)
            {
                step = -step;
                _resizeCorner = 5 - _resizeCorner;
            }

            Vector2 mySize = _highlightedInitialSize;
            _panelClickResizeOrigin = _highlightedInitialPos;
            switch (_resizeCorner)
            {
                case 1:
                    _panelClickResizeOrigin += mySize;
                    mySize.Y += step;
                    break;
                case 2:
                    _panelClickResizeOrigin.Y += mySize.Y;
                    mySize.X += step;
                    break;
                case 3:
                    _panelClickResizeOrigin.X += mySize.X;
                    mySize.X += step;
                    break;
                default: // 4
                    mySize.Y += step;
                    break;
            }
            SetPanelPosSize(mySize, viewport);
        }
        else // move
        {
            Vector2 pos = _highlightedInitialPos;
            switch (primary)
            {
                case KUpArrow: pos.Y -= step; break;
                case KDownArrow: pos.Y += step; break;
                case KLeftArrow: pos.X -= step; break;
                default: pos.X += step; break; // K_RIGHTARROW
            }
            SetPanelPos(pos, viewport);
        }

        Vector2 newPos = PanelPosPx(_highlightedPanel, viewport);
        Vector2 newSize = PanelSizePx(_highlightedPanel, viewport);
        if (_highlightedInitialPos != newPos || _highlightedInitialSize != newSize)
        {
            _panelPosBackup = _highlightedInitialPos;
            _panelSizeBackup = _highlightedInitialSize;
            _highlightedBackup = _highlightedPanel;
            _centerlineTime = Time + 1f;
        }
    }

    /// <summary>QC arrow step sizing (418-446): grid → grid step (×1 with Shift, ×2 without); else
    /// fraction-of-screen with Shift = /256, no-Shift = /64 × accel.</summary>
    private float ArrowStep(bool vertical, Vector2 viewport)
    {
        if (CvarGrid())
        {
            Vector2 real = RealGridSize(viewport, GridSize());
            float g = vertical ? real.Y : real.X;
            return (_hudShiftState & S_SHIFT) != 0 ? g : 2f * g;
        }
        float step = vertical ? viewport.Y : viewport.X;
        if ((_hudShiftState & S_SHIFT) != 0)
            return step / 256f; // more precision
        return (step / 64f) * (1f + 2f * (Time - _pressedKeyTime));
    }

    // =================================================================================================
    //  Ctrl+Tab cycle (QC HUD_Panel_InputEvent K_TAB block 609-697)
    // =================================================================================================

    private void CtrlTabCycle()
    {
        Vector2 viewport = GetViewportRect().Size;
        HudPanel? oldTab = _tabPanel;
        HudPanel? startingPanel;

        if (_tabPanel is null) // first press of TAB
        {
            Vector2 startPos = _highlightedPanel is not null
                ? PanelPosPx(_highlightedPanel, viewport)
                : Vector2.Zero;
            startingPanel = _highlightedPanel;
            _tabPanelPos = startPos;
        }
        else
        {
            bool shift = (_hudShiftState & S_SHIFT) != 0;
            if ((!_tabBackward && shift) || (_tabBackward && !shift)) // direction changed
                ResetTabPanels();
            startingPanel = _tabPanel;
        }
        _tabBackward = (_hudShiftState & S_SHIFT) != 0;

        const int LevelsNum = 4;
        float levelHeight = viewport.Y / LevelsNum;

        // QC LABEL(find_tab_panel) loop, ported as a method we can re-enter once with the old tab as start.
        if (!TryFindTabPanel(viewport, startingPanel, levelHeight, LevelsNum))
        {
            // not found: reset and retry once with old_tab_panel as the starting panel (QC 670-681).
            ResetTabPanels();
            if (oldTab is null)
            {
                _tabPanel = null;
                SyncHighlightFlags();
                return;
            }
            TryFindTabPanel(viewport, oldTab, levelHeight, LevelsNum);
        }

        if (_tabPanel is not null)
            _tabPanels.Add(_tabPanel);
        SyncHighlightFlags();
    }

    private bool TryFindTabPanel(Vector2 viewport, HudPanel? startingPanel, float levelHeight, int levelsNum)
    {
        float level = Mathf.Floor(_tabPanelPos.Y / levelHeight) * levelHeight; // starting level
        float candidateX = !_tabBackward ? viewport.X : 0f;
        Vector2 candidatePos = new(candidateX, 0f);
        float startPosX = _tabPanelPos.X;
        _tabPanel = null;

        int k = 0;
        while (true)
        {
            k++;
            foreach (HudPanel p in _hud.Panels)
            {
                if (!p.ConfigFlags.HasFlag(PanelConfig.Main)) continue;
                if (_tabPanels.Contains(p) || ReferenceEquals(p, startingPanel)) continue;
                Vector2 pPos = PanelPosPx(p, viewport);
                if (!(pPos.Y >= level && (pPos.Y - level) < levelHeight)) continue;

                bool pick;
                if (!_tabBackward)
                    pick = pPos.X >= startPosX && (pPos.X < candidatePos.X
                           || (pPos.X == candidatePos.X && pPos.Y <= candidatePos.Y));
                else
                    pick = pPos.X <= startPosX && (pPos.X > candidatePos.X
                           || (pPos.X == candidatePos.X && pPos.Y >= candidatePos.Y));

                if (pick)
                {
                    _tabPanel = p;
                    _tabPanelPos = candidatePos = pPos;
                }
            }
            if (_tabPanel is not null)
                return true;
            if (k == levelsNum)
                return false;

            if (!_tabBackward)
            {
                level = Mod(level + levelHeight, viewport.Y);
                startPosX = 0f;
                candidatePos.X = viewport.X;
            }
            else
            {
                level = Mod(level - levelHeight, viewport.Y);
                startPosX = viewport.X;
                candidatePos.X = 0f;
            }
        }
    }

    // QuakeC's `%` operator: a - b * trunc(a/b) (truncation toward zero), which C#'s float `%` already matches.
    private static float Mod(float a, float b) => b == 0f ? 0f : a - b * (float)Math.Truncate(a / b);

    // =================================================================================================
    //  Ctrl+Space toggle / Ctrl+V paste / undo / per-panel menu (QC 698-765, 947)
    // =================================================================================================

    private void CtrlSpaceToggle()
    {
        if (_highlightedPanel is not null)
        {
            if (_highlightedPanel.ConfigFlags.HasFlag(PanelConfig.CanBeOff))
            {
                string cv = "hud_panel_" + _highlightedPanel.PanelId;
                float cur = Cvars.GetFloat(cv);
                Cvars.Set(cv, (cur != 0f ? 0f : 1f).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else
        {
            // toggle the dock (QC: "" → "dock", else "").
            string dock = Cvars.GetString("hud_dock");
            Cvars.Set("hud_dock", string.IsNullOrEmpty(dock) ? "dock" : "");
        }
    }

    private void CtrlVPaste()
    {
        if (_panelSizeCopied == Vector2.Zero || _highlightedPanel is null)
            return;
        Vector2 viewport = GetViewportRect().Size;
        Vector2 pos = PanelPosPx(_highlightedPanel, viewport);
        Vector2 size = PanelSizePx(_highlightedPanel, viewport);

        Vector2 tmp = _panelSizeCopied;
        if (pos.X + _panelSizeCopied.X > viewport.X) tmp.X = viewport.X - pos.X;
        if (pos.Y + _panelSizeCopied.Y > viewport.Y) tmp.Y = viewport.Y - pos.Y;

        if (size == tmp) return;

        // backup first (QC 744-747)
        _panelPosBackup = pos;
        _panelSizeBackup = size;
        _highlightedBackup = _highlightedPanel;

        WriteSizeCvar(_highlightedPanel, tmp, viewport);
    }

    private void UndoLast()
    {
        if (_highlightedBackup is null) return;
        Vector2 viewport = GetViewportRect().Size;
        WritePosCvar(_highlightedBackup, _panelPosBackup, viewport);
        WriteSizeCvar(_highlightedBackup, _panelSizeBackup, viewport);
        _highlightedBackup = null;
    }

    private void EnablePanelMenu()
    {
        if (_highlightedPanel is null) return;
        RunLocalCmd("menu_showhudoptions " + _highlightedPanel.PanelId);
    }

    private void ExitForce()
    {
        Cvars.Set("_hud_configure", "0");
    }

    // =================================================================================================
    //  HUD config export (QC HUD_Panel_ExportCfg, hud_config.qc:10 — the `hud save <name>` backend)
    // =================================================================================================

    /// <summary>QC <c>HUD_Write_Cvar</c>: one <c>seta name "value"</c> line. Skipped for cvars the port never
    /// registered (e.g. Base progressbar colors with no port feature yet), so the dump stays honest.</summary>
    private static void WriteCvarLine(System.Text.StringBuilder sb, string name)
    {
        CvarService cvars = MenuState.Cvars;
        if (cvars.Has(name))
            sb.Append("seta ").Append(name).Append(" \"").Append(cvars.GetString(name)).Append("\"\n");
    }

    /// <summary>Reduce a user/cvar-supplied string to a safe filename token: keep only letters, digits, '-' and
    /// '_' (dropping path separators, '.', and everything else), falling back to <paramref name="fallback"/> when
    /// nothing safe remains. Prevents a `hud save ../../evil` (or a poisoned hud_skin) from escaping the data dir.</summary>
    private static string SanitizeToken(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    /// <summary>
    /// Port of <c>HUD_Panel_ExportCfg</c> (hud_config.qc:10): dump the live HUD tuning — skin + global panel-bg
    /// defaults, dock, progressbar colors, panel order, grid — then every panel's cvar block, as <c>seta</c>
    /// lines into <c>hud_&lt;skin&gt;_&lt;cfgname&gt;.cfg</c>. DP wrote into the gamedir's <c>data/</c> (hence
    /// Base's "saved in data/data/" note); the port's writable home is <see cref="UserPaths"/>, so the file
    /// lands under <c>&lt;userdir&gt;/data/</c>. Per panel: the eight common cvars in QC order, then — the
    /// stand-in for QC's per-panel <c>panel_export</c> — the master enable cvar plus every other registered
    /// <c>hud_panel_&lt;id&gt;_*</c> behaviour cvar (sorted for a stable dump). The QC trailer
    /// <c>menu_sync</c> is omitted: the port menu reads the live store, there is no menu VM to resync.
    /// Static (the store is the shared <see cref="MenuState.Cvars"/>) so the menu-side "Save current skin"
    /// button (<see cref="Menu.DialogHudSetupExit"/>) can export without a live editor instance.
    /// </summary>
    public static void ExportCfg(string cfgname)
    {
        CvarService cvars = MenuState.Cvars;
        // Sanitize BOTH the user-supplied config name AND the skin cvar into safe filename tokens: the file goes
        // to `data/hud_<skin>_<name>.cfg` and is written with a raw System.IO path, so an unsanitized `..` / path
        // separator (from the console `hud save`, the menu save box, or an exec'd cfg) would escape the user data
        // dir (UserPaths.Resolve canonicalizes '..' and creates the tree). Keep only filename-safe characters.
        string skin = SanitizeToken(cvars.GetString("hud_skin"), "luma");
        string cfgToken = SanitizeToken(cfgname, "myconfig");
        string filename = $"hud_{skin}_{cfgToken}.cfg";

        var sb = new System.Text.StringBuilder();
        sb.Append("//title \n//author \n\n");
        foreach (string n in new[] { "hud_skin", "hud_panel_bg", "hud_panel_bg_color", "hud_panel_bg_color_team",
            "hud_panel_bg_alpha", "hud_panel_bg_border", "hud_panel_bg_padding", "hud_panel_fg_alpha" })
            WriteCvarLine(sb, n);
        sb.Append('\n');
        foreach (string n in new[] { "hud_dock", "hud_dock_color", "hud_dock_color_team", "hud_dock_alpha" })
            WriteCvarLine(sb, n);
        sb.Append('\n');
        foreach (string n in new[] { "hud_progressbar_alpha", "hud_progressbar_strength_color",
            "hud_progressbar_superweapons_color", "hud_progressbar_shield_color", "hud_progressbar_health_color",
            "hud_progressbar_armor_color", "hud_progressbar_fuel_color", "hud_progressbar_oxygen_color",
            "hud_progressbar_nexball_color", "hud_progressbar_speed_color", "hud_progressbar_acceleration_color",
            "hud_progressbar_acceleration_neg_color", "hud_progressbar_vehicles_ammo1_color",
            "hud_progressbar_vehicles_ammo2_color" })
            WriteCvarLine(sb, n);
        sb.Append('\n');
        WriteCvarLine(sb, "_hud_panelorder");
        sb.Append('\n');
        foreach (string n in new[] { "hud_configure_grid", "hud_configure_grid_xsize", "hud_configure_grid_ysize" })
            WriteCvarLine(sb, n);
        sb.Append('\n');

        // Per-panel blocks (QC walks the hud_panels registry; the port's registry equivalent is the luma table).
        string[] commonSuffixes = { "_pos", "_size", "_bg", "_bg_color", "_bg_color_team", "_bg_alpha",
            "_bg_border", "_bg_padding" };
        foreach (string id in HudLayoutDefaults.Ids)
        {
            string prefix = "hud_panel_" + id;
            foreach (string suf in commonSuffixes)
                WriteCvarLine(sb, prefix + suf);
            // panel_export stand-in: the master toggle + every other registered hud_panel_<id>_* cvar.
            WriteCvarLine(sb, prefix);
            var extras = new List<string>();
            foreach (string name in cvars.Names)
                if (name.StartsWith(prefix + "_", StringComparison.Ordinal)
                    && System.Array.IndexOf(commonSuffixes, name.Substring(prefix.Length)) < 0)
                    extras.Add(name);
            extras.Sort(StringComparer.Ordinal);
            foreach (string name in extras)
                WriteCvarLine(sb, name);
            sb.Append('\n');
        }

        try
        {
            string path = UserPaths.Resolve("data/" + filename);
            System.IO.File.WriteAllText(path, sb.ToString());
            XonoticGodot.Common.Diagnostics.Log.Info(
                $"[HudConfigEditor] Successfully exported to {filename}! (Note: it's saved in {path})");
        }
        catch (Exception ex)
        {
            XonoticGodot.Common.Diagnostics.Log.Warn($"[HudConfigEditor] Couldn't write to {filename}: {ex.Message}");
        }
    }

    // =================================================================================================
    //  Draw-order management (QC HUD_Panel_FirstInDrawQ 842 + _hud_panelorder)
    // =================================================================================================

    private void EnsurePanelOrder()
    {
        int n = _hud.Panels.Count;
        if (_panelOrder.Count == n) return;
        _panelOrder = new List<int>(n);
        for (int i = 0; i < n; i++)
            _panelOrder.Add(i);
    }

    /// <summary>Port of <c>HUD_Panel_FirstInDrawQ</c> (842): move <paramref name="id"/> to the front of the draw
    /// order so it draws last (on top), and persist the order into <c>_hud_panelorder</c>.</summary>
    private void FirstInDrawQueue(int id)
    {
        EnsurePanelOrder();
        int place = _panelOrder.IndexOf(id);
        if (place < 0) place = _panelOrder.Count - 1;
        for (int i = place; i > 0; i--)
            _panelOrder[i] = _panelOrder[i - 1];
        if (_panelOrder.Count > 0) _panelOrder[0] = id;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _panelOrder.Count; i++)
            sb.Append(_panelOrder[i]).Append(' ');
        Cvars.Set("_hud_panelorder", sb.ToString());
    }

    /// <summary>Panels iterated in draw order (QC walks panel_order[]); highlight hit-tests front-to-back.</summary>
    private IEnumerable<HudPanel> PanelsInDrawOrder()
    {
        EnsurePanelOrder();
        var panels = _hud.Panels;
        foreach (int idx in _panelOrder)
            if (idx >= 0 && idx < panels.Count)
                yield return panels[idx];
    }

    private int EditorId(HudPanel p)
    {
        var panels = _hud.Panels;
        for (int i = 0; i < panels.Count; i++)
            if (ReferenceEquals(panels[i], p)) return i;
        return 0;
    }

    private void ResetTabPanels() => _tabPanels.Clear();

    // =================================================================================================
    //  Pure geometry / cvar-normalization helpers (mirrored verbatim by HudConfigEditorTests)
    // =================================================================================================

    /// <summary>QC SetPos grid snap (178-182): quantize a pixel pos to the grid, returning a pixel pos.</summary>
    public static Vector2 SnapToGrid(Vector2 posPx, Vector2 viewport, Vector2 gridSize, Vector2 realGridSize)
    {
        float x = Mathf.Floor((posPx.X / viewport.X) / gridSize.X + 0.5f) * realGridSize.X;
        float y = Mathf.Floor((posPx.Y / viewport.Y) / gridSize.Y + 0.5f) * realGridSize.Y;
        return new Vector2(x, y);
    }

    /// <summary>QC SetPosSize grid snap (364-368): quantize a pixel size to the grid.</summary>
    public static Vector2 SnapSizeToGrid(Vector2 sizePx, Vector2 viewport, Vector2 gridSize, Vector2 realGridSize)
    {
        float x = Mathf.Floor((sizePx.X / viewport.X) / gridSize.X + 0.5f) * realGridSize.X;
        float y = Mathf.Floor((sizePx.Y / viewport.Y) / gridSize.Y + 0.5f) * realGridSize.Y;
        return new Vector2(x, y);
    }

    /// <summary>QC <c>hud_configure_realGridSize = gridSize * vid_con*</c> (1064-1065).</summary>
    public static Vector2 RealGridSize(Vector2 viewport, Vector2 gridSize)
        => new(gridSize.X * viewport.X, gridSize.Y * viewport.Y);

    /// <summary>Normalize a pixel pos/size into the 0..1 cvar string (QC ftos_mindecimals(v/vid_con*)).</summary>
    public static string NormalizePair(Vector2 px, Vector2 viewport)
        => $"{MinDecimals(px.X / viewport.X)} {MinDecimals(px.Y / viewport.Y)}";

    /// <summary>QC <c>ftos_mindecimals</c> (lib/string.qh:502-540): the shortest decimal string with at most 4
    /// decimals, faithfully — tiny magnitudes collapse to "0", near-integers print as the integer, and trailing
    /// zeros (with the dot) are stripped. A faithful port (the old <c>ToString("0.######")</c> diverged on the
    /// &lt;0.0001 and 4-vs-6-decimal edge cases, so a cvar could fail to round-trip).</summary>
    public static string MinDecimals(float number)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // inhibit stupid negative zero (QC: if (fabs(number) < 0.0001) return "0";)
        if (System.Math.Abs(number) < 0.0001f)
            return "0";

        // near-integer → print the integer (QC: rint + fabs(number - rounded) < 0.0001)
        float rounded = Mathf.Round(number);
        if (System.Math.Abs(number - rounded) < 0.0001f)
            return ((int)rounded).ToString(inv);

        // QC: s = sprintf("%.4f", number); then strip trailing zeros (and the dot if all 4 went).
        string s = number.ToString("F4", inv);
        int dot = s.IndexOf('.');
        if (dot < 0)
            return s; // no decimal part (shouldn't happen for F4, but matches QC's guard)

        int i = 0;
        char ch = '0';
        for (; i < 4; ++i)
        {
            ch = s[s.Length - 1 - i];
            if (ch != '0')
                break;
        }

        if (i == 0)
            return s; // no trailing zeros

        // if all 4 decimals were zero (i == 4) the next char is the dot — drop it too (ch == '.' guards short parts)
        if (i == 4 || ch == '.')
            ++i;

        return s.Substring(0, s.Length - i);
    }

    // =================================================================================================
    //  Per-panel cvar reads (QC HUD_Panel_UpdatePosSize reads hud_panel_<name>_pos/_size)
    // =================================================================================================

    private Vector2 PanelPosPx(HudPanel p, Vector2 viewport)
    {
        Vector2 f = ParseVec2(Cvars.GetString($"hud_panel_{p.PanelId}_pos"), DefaultPosFraction(p));
        return new Vector2(f.X * viewport.X, f.Y * viewport.Y);
    }

    private Vector2 PanelSizePx(HudPanel p, Vector2 viewport)
    {
        Vector2 f = ParseVec2(Cvars.GetString($"hud_panel_{p.PanelId}_size"), DefaultSizeFraction(p));
        return new Vector2(f.X * viewport.X, f.Y * viewport.Y);
    }

    private static Vector2 DefaultPosFraction(HudPanel p) => HudLayoutDefaults.For(p.PanelId).Pos;
    private static Vector2 DefaultSizeFraction(HudPanel p) => HudLayoutDefaults.For(p.PanelId).Size;

    /// <summary>Per-panel bg_border in px (QC panel_bg_border): the panel cvar, else the global, else 2.
    /// QC "" inherits the global; an explicit per-panel "0" is a real 0 (border disabled) and is honored.</summary>
    private float PanelBorderPx(HudPanel p)
    {
        string s = Cvars.GetString($"hud_panel_{p.PanelId}_bg_border");
        if (string.IsNullOrWhiteSpace(s)) // "" → inherit the global default
        {
            string g = Cvars.GetString("hud_panel_bg_border");
            return string.IsNullOrWhiteSpace(g) ? 2f : Cvars.GetFloat("hud_panel_bg_border");
        }
        return Cvars.GetFloat($"hud_panel_{p.PanelId}_bg_border");
    }

    /// <summary>QC panel_enabled (whether the panel is shown given its hud_panel_&lt;id&gt; cvar / config flags).</summary>
    private bool PanelEnabled(HudPanel p)
    {
        if (!p.ConfigFlags.HasFlag(PanelConfig.CanBeOff)) return true;
        return Cvars.GetFloat("hud_panel_" + p.PanelId) != 0f;
    }

    private void WritePosCvar(HudPanel p, Vector2 posPx, Vector2 viewport)
        => Cvars.Set($"hud_panel_{p.PanelId}_pos", NormalizePair(posPx, viewport));

    private void WriteSizeCvar(HudPanel p, Vector2 sizePx, Vector2 viewport)
        => Cvars.Set($"hud_panel_{p.PanelId}_size", NormalizePair(sizePx, viewport));

    // =================================================================================================
    //  Grid / collision cvar reads
    // =================================================================================================

    private bool CvarGrid() => Cvars.GetFloat("hud_configure_grid") != 0f;
    private bool CvarCheckCollisions() => Cvars.GetFloat("hud_configure_checkcollisions") != 0f;

    /// <summary>QC HUD_Configure_DrawGrid grid size read, bound to [0.005, 0.2] (1062-1063).</summary>
    private Vector2 GridSize()
    {
        float gx = Mathf.Clamp(Cvars.GetFloat("hud_configure_grid_xsize"), 0.005f, 0.2f);
        float gy = Mathf.Clamp(Cvars.GetFloat("hud_configure_grid_ysize"), 0.005f, 0.2f);
        return new Vector2(gx, gy);
    }

    // =================================================================================================
    //  Drawing (QC HUD_Configure_DrawGrid + HUD_Configure_PostDraw)
    // =================================================================================================

    public override void _Draw()
    {
        if (!Configuring) return;
        Vector2 viewport = GetViewportRect().Size;

        DrawGrid(viewport);

        // White grab/hover wash (QC HUD_Panel_Mouse 999 / 1055): a 0.1-alpha white fill over the grabbed panel
        // while the mouse is down, else over the hovered panel (suppressed while a tab-cycle preview shows, and
        // while a menu dialog fully covers the editor — QC's early return skips the drawfill there).
        if (MenuAlpha != 1f)
        {
            HudPanel? wash = _mouseClicked != 0 ? _highlightedPanel : (_tabPanel is null ? _hoverPanel : null);
            if (wash is not null)
            {
                Vector2 wPos = PanelPosPx(wash, viewport);
                Vector2 wSize = PanelSizePx(wash, viewport);
                float wb = PanelBorderPx(wash);
                DrawRect(new Rect2(wPos - new Vector2(wb, wb), wSize + new Vector2(2f * wb, 2f * wb)),
                    new Color(1f, 1f, 1f, 0.1f));
            }
        }

        // tab preview fill (QC PostDraw 1164-1169)
        if (_tabPanel is not null)
        {
            Vector2 pos = PanelPosPx(_tabPanel, viewport);
            Vector2 size = PanelSizePx(_tabPanel, viewport);
            float b = PanelBorderPx(_tabPanel);
            DrawRect(new Rect2(pos - new Vector2(b, b), size + new Vector2(2f * b, 2f * b)),
                new Color(1f, 1f, 1f, 0.2f));
        }

        // highlight border + center line (QC PostDraw 1170-1176)
        if (_highlightedPanel is not null)
        {
            Vector2 pos = PanelPosPx(_highlightedPanel, viewport);
            Vector2 size = PanelSizePx(_highlightedPanel, viewport);
            float b = PanelBorderPx(_highlightedPanel) * 2f; // QC panel_bg_border * hlBorderSize (hlBorderSize=2)
            float a = 0.4f * (1f - MenuAlpha);
            DrawHighlightBorder(pos, size, b, a);
            DrawCenterLine(pos, size, b, viewport);
        }
    }

    private void DrawGrid(Vector2 viewport)
    {
        if (!CvarGrid()) return;
        float gridAlpha = Cvars.GetFloat("hud_configure_grid_alpha");
        if (gridAlpha <= 0f) return;

        Vector2 grid = GridSize();
        Vector2 real = RealGridSize(viewport, grid);
        var col = new Color(0.5f, 0.5f, 0.5f, gridAlpha);

        // vertical guide lines (QC 1069-1074): hud_configure_vertical_lines tokens.
        foreach (float xr in ParseFloats(Cvars.GetString("hud_configure_vertical_lines")))
            DrawRect(new Rect2(xr * viewport.X - 1f, 0f, 3f, viewport.Y), col);

        // x-axis grid (1076-1078)
        for (int i = 1; i < (int)(1f / grid.X); i++)
            DrawRect(new Rect2(i * real.X, 0f, 1f, viewport.Y), col);
        // y-axis grid (1080-1082)
        for (int i = 1; i < (int)(1f / grid.Y); i++)
            DrawRect(new Rect2(0f, i * real.Y, viewport.X, 1f), col);
    }

    /// <summary>QC HUD_Panel_HlBorder (1125): a blue tinted fill + a 2px outline frame around the panel.</summary>
    private void DrawHighlightBorder(Vector2 pos, Vector2 size, float border, float alpha)
    {
        Vector2 p = pos - new Vector2(border, border);
        Vector2 s = size + new Vector2(2f * border, 2f * border);
        DrawRect(new Rect2(p, s), new Color(0f, 0.5f, 1f, 0.5f * alpha));
        // outline (the QC tiles a border image; we draw a plain 2px frame in the same color/alpha).
        var line = new Color(0f, 0.5f, 1f, alpha);
        const float t = 2f;
        DrawRect(new Rect2(p.X, p.Y, s.X, t), line);
        DrawRect(new Rect2(p.X, p.Y + s.Y - t, s.X, t), line);
        DrawRect(new Rect2(p.X, p.Y, t, s.Y), line);
        DrawRect(new Rect2(p.X + s.X - t, p.Y, t, s.Y), line);
    }

    /// <summary>QC HUD_Panel_HlCenterLine (1136): a blinking red→green guide when the panel center nears a
    /// configured vertical line, for the brief window after a move (hud_configure_centerline_time).</summary>
    private void DrawCenterLine(Vector2 pos, Vector2 size, float border, Vector2 viewport)
    {
        if (Time > _centerlineTime) return;
        float centerX = pos.X + size.X * 0.5f;
        foreach (float xr in ParseFloats(Cvars.GetString("hud_configure_vertical_lines")))
        {
            if (xr <= 0f || xr >= 1f) continue;
            float ofs = Mathf.Abs(centerX / viewport.X - xr);
            if (ofs >= 0.02f) continue;
            float f = Mathf.Clamp((ofs - 0.001f) / (0.01f - 0.001f), 0f, 1f);
            var col = new Color(f, 1f - f, 0f); // red (far) → green (close)
            float a = 0.3f + 0.1f * Mathf.Sin(6f * Time);
            a *= (1f - MenuAlpha) * Mathf.Clamp(_centerlineTime - Time, 0f, 0.5f) * 2f;
            DrawRect(new Rect2(centerX - 1f, pos.Y - border, 3f, size.Y + 2f * border), new Color(col.R, col.G, col.B, a));
        }
    }

    // =================================================================================================
    //  Small parse / key helpers
    // =================================================================================================

    private static Vector2 ParseVec2(string s, Vector2 fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string[] p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return fallback;
        if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)) return fallback;
        if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)) return fallback;
        return new Vector2(x, y);
    }

    private static IEnumerable<float> ParseFloats(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        foreach (string tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (float.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                yield return v;
    }

    private void RunLocalCmd(string line)
    {
        // QC localcmd: fire-and-forget into the shared command interpreter (the menu/console buffer).
        try { MenuState.Interp?.ExecuteLine(line); }
        catch (Exception ex) { XonoticGodot.Common.Diagnostics.Log.Warn($"[HudConfigEditor] localcmd failed '{line}': {ex.Message}"); }
    }

    // ---- our K_* key ids: Godot Key codes for real keys; small negatives for mouse buttons ----
    private const int KMouse1 = -1;
    private const int KMouse2 = -2;
    private const int KEscape = (int)Key.Escape;
    private const int KBackspace = (int)Key.Backspace;
    private const int KTab = (int)Key.Tab;
    private const int KSpace = (int)Key.Space;
    private const int KEnter = (int)Key.Enter;
    private const int KKpEnter = (int)Key.KpEnter;
    private const int KPause = (int)Key.Pause; // QC K_PAUSE — must pass through HandleInput so `bind PAUSE pause` works
    private const int KUpArrow = (int)Key.Up;
    private const int KDownArrow = (int)Key.Down;
    private const int KLeftArrow = (int)Key.Left;
    private const int KRightArrow = (int)Key.Right;
    private const int KeyC = (int)Key.C;
    private const int KeyV = (int)Key.V;
    private const int KeyZ = (int)Key.Z;
    private const int KeyS = (int)Key.S;

    private static int KeyIdForMouseButton(MouseButton b) => b switch
    {
        MouseButton.Left => KMouse1,
        MouseButton.Right => KMouse2,
        _ => 0,
    };

    private static int ShiftBitFor(int primary) => primary switch
    {
        (int)Key.Alt => S_ALT,
        (int)Key.Ctrl => S_CTRL,
        (int)Key.Shift => S_SHIFT,
        _ => 0,
    };

    private static bool IsCtrl(int primary) => primary == (int)Key.Ctrl;
    private static bool IsArrow(int primary)
        => primary == KUpArrow || primary == KDownArrow || primary == KLeftArrow || primary == KRightArrow;
}
