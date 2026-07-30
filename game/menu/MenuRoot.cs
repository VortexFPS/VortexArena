using Godot;
using System;
using System.Collections.Generic;

namespace VortexArena.Game.Menu;

/// <summary>
/// The menu host. Owns the full-rect background and a stack of screens, and is the single point every
/// screen talks to in order to navigate.
///
/// This is the C# successor to QuakeC's <c>MainWindow</c> / <c>Nexposee</c> / <c>ModalController</c>
/// trio (qcsrc/menu/xonotic/mainwindow.qc, item/nexposee.qc, item/modalcontroller.qc). The QC version
/// is a full retained-mode widget toolkit that draws itself every frame; here we lean on Godot's
/// <see cref="Control"/> scene graph instead — each screen is a Control subtree we show/hide.
///
/// Navigation model: a simple screen stack.
///  * <see cref="ShowScreen"/> replaces the whole stack with a single root screen (used for the main menu).
///  * <see cref="Push"/> overlays a new screen on top (e.g. opening Settings or the Quit dialog) and
///    remembers what was underneath.
///  * <see cref="Pop"/> closes the top screen and re-shows the one beneath it (the universal "Back").
///
/// Only the topmost screen is visible & interactive; the rest are hidden but kept alive so their state
/// (selected tab, typed text, …) survives a round-trip.
/// </summary>
public partial class MenuRoot : Control
{
    private Control _background = null!;
    private Control _screenHost = null!;

    // The live screen stack. Each entry pairs the actual screen with its display Host: a root screen
    // (ShowScreen) is its own host and fills the viewport; a pushed sub-dialog (Push) is wrapped in a
    // centered, bordered DialogFrame so it looks like a Xonotic dialog panel. The last entry is on top.
    // ResumeOnClose marks a dialog opened by `menu_cmd directmenu` (QC m_goto(name, true)): closing it while a
    // match is live should DISMISS the whole menu (resume the match), not just pop back to the pause menu —
    // mirroring the QC "hide the menu when this dialog is closed" semantics. See OpenDialog in MenuCommand.
    private readonly List<(Control Host, Control Screen, bool ResumeOnClose)> _stack = new();

    /// <summary>The screen currently on top of the stack (null before the first ShowScreen).</summary>
    public Control? Current => _stack.Count > 0 ? _stack[^1].Screen : null;

    /// <summary>True when there's a screen above the root that <see cref="Pop"/> can close (drives ESC behavior).</summary>
    public bool CanPop => _stack.Count > 1;

    /// <summary>
    /// Raised by the in-game pause menu's "Resume" (the <see cref="Shell"/> hides the menu and recaptures the
    /// mouse). Lets a pushed screen ask the host to dismiss the whole menu rather than just pop one level.
    /// </summary>
    public event Action? ResumeRequested;

    /// <summary>Raised by the in-game pause menu's "Disconnect" (the <see cref="Shell"/> tears the match down).</summary>
    public event Action? DisconnectRequested;

    /// <summary>Fire <see cref="ResumeRequested"/> (a screen can only invoke an event on its declaring type).</summary>
    public void RequestResume() => ResumeRequested?.Invoke();

    /// <summary>Fire <see cref="DisconnectRequested"/>.</summary>
    public void RequestDisconnect() => DisconnectRequested?.Invoke();

    public override void _Ready()
    {
        Name = "MenuRoot";
        // Top-left anchors (not full-rect): a Control under a CanvasLayer isn't auto-sized to the viewport, so
        // we drive Position+Size ourselves via FitToViewport. Equal opposite anchors also avoid Godot's
        // "size overridden after _ready" warning that full-rect anchors + an explicit Size would trip.
        SetAnchorsPreset(LayoutPreset.TopLeft);
        // Eat input across the whole rect so clicks never fall through to anything behind the menu.
        MouseFilter = MouseFilterEnum.Stop;

        // Apply the Xonotic skin (luma) theme to the whole front-end. A Godot theme cascades to every
        // descendant Control, so this one assignment restyles all screens + cvar-bound widgets at once to the
        // authentic border-image / Xolonium / blue-white look (see MenuSkin). Built lazily from the asset VFS.
        Theme = MenuSkin.Theme;

        // The skinned mouse cursor (cursor.tga + its hotspot), like the engine's draw_setMousePointer. Global,
        // so it persists across the menu; in a match the mouse is captured/hidden so it doesn't show there.
        ApplySkinCursor();

        // Size to the window now and on every resize, so the full-rect children (background + screens) fill it.
        FitToViewport();
        GetViewport().SizeChanged += FitToViewport;

        BuildBackground();

        // A dedicated host node keeps the screen subtrees separate from the background panel,
        // so z-ordering is trivial (host is always drawn above the background). It is NOT full-rect: it is laid
        // out in a fixed-height "design" space and uniformly scaled to the viewport (see LayoutScreenHost), so
        // the whole front-end — fonts, control sizes, paddings and all — scales as one with the resolution
        // instead of staying at a fixed pixel size while the panels shrink. (Top-left anchors; size driven by us.)
        _screenHost = new Control { Name = "ScreenHost" };
        _screenHost.SetAnchorsPreset(LayoutPreset.TopLeft);
        _screenHost.MouseFilter = MouseFilterEnum.Pass;
        _screenHost.PivotOffset = Vector2.Zero;
        AddChild(_screenHost);
        LayoutScreenHost();

        // Boot straight into the main menu.
        ShowScreen(new MainMenu());
    }

    public override void _ExitTree()
    {
        // Release the skinned custom mouse cursor installed in _Ready/Restart BEFORE the RenderingServer is
        // torn down. The DisplayServer holds the cursor image's texture independently of the scene tree, so it
        // outlives every node we free; left set, its texture RID is released only after RenderingServer::finish(),
        // which the engine reports on quit as "N RIDs of type Texture were leaked at exit" /
        // "~ImageTexture: RenderingServer::get_singleton() is null" (godotengine/godot#98806). Resetting to the
        // default cursor here — _ExitTree fires during SceneTree teardown on EVERY quit path, server still alive —
        // frees it cleanly, whether the game is closed from the window button, the menu, or a direct CLI launch.
        Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
    }

    /// <summary>
    /// QC <c>menu_restart</c> — rebuild the front-end so a skin or language change takes effect: discard the
    /// cached skin theme (<see cref="MenuSkin.Reload"/>), re-apply the freshly-built <see cref="Theme"/> (which
    /// cascades the new skin colours to every widget), refresh the skinned cursor + backdrop, and re-show the
    /// main menu (a fresh build re-translates every label through <see cref="Localization"/>). Mirrors
    /// <c>m_init_delayed</c> re-running on a restart; the QC "Set language"/"Set skin" buttons then re-open the
    /// User settings via the trailing <c>menu_cmd languageselect</c>/<c>skinselect</c>.
    /// </summary>
    /// <summary>
    /// Install the skinned pointer, falling back to the OS arrow when the skin has no usable cursor image.
    /// SetCustomMouseCursor with a zero-sized texture installs an INVISIBLE cursor rather than failing, so a
    /// missing or broken cursor.tga otherwise leaves the menu with no visible pointer at all.
    /// </summary>
    private static void ApplySkinCursor()
    {
        if (MenuSkin.Cursor is { } cursor && cursor.GetWidth() > 0 && cursor.GetHeight() > 0)
            Input.SetCustomMouseCursor(cursor, Input.CursorShape.Arrow, MenuSkin.CursorHotspot);
        else
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
    }

    public void Restart()
    {
        MenuSkin.Reload();
        Theme = MenuSkin.Theme; // re-built lazily from the (possibly new) skin; cascades to all descendants

        ApplySkinCursor();

        // Rebuild the backdrop (the skin's background.tga may have changed) and re-show the main menu, which
        // reconstructs every screen label through the (possibly new) active language catalog.
        _background?.QueueFree();
        BuildBackground();
        // Keep the background behind the screen host (BuildBackground appends as the last child).
        if (_background is not null)
            MoveChild(_background, 0);

        ShowScreen(new MainMenu());
    }

    /// <summary>Match the menu's rect to the current viewport size (called on ready + every window resize).</summary>
    private void FitToViewport()
    {
        // The SizeChanged signal can fire during teardown (window mode flip on quit) after we've left the tree;
        // guard so the handler never touches a gone viewport.
        if (!IsInsideTree())
            return;
        Viewport? vp = GetViewport();
        if (vp is null)
            return;
        Position = Vector2.Zero;
        Size = vp.GetVisibleRect().Size;
        LayoutScreenHost();
    }

    /// <summary>
    /// The menu's "design height" in px. Every menu screen is laid out as if the viewport were this tall and then
    /// uniformly scaled to the real viewport (<see cref="LayoutScreenHost"/>). The skin's widget metrics — font
    /// sizes, control min-heights, paddings — are tuned for this height (~1080p), so laying out at it and scaling
    /// keeps the front-end proportioned identically at every resolution (the Xonotic menu scales as one whole).
    /// </summary>
    private const float DesignHeight = 1080f;

    /// <summary>
    /// Lay the screen host out in a fixed <see cref="DesignHeight"/>-tall design space and uniformly scale it to
    /// fill the current viewport. Without this, the dialog panels (whose size tracks the viewport) shrink at lower
    /// resolutions while their child widgets keep a fixed pixel size, so controls overflow and get clipped — the
    /// "settings dialog is unusable at small resolutions" bug. Scaling the whole host fixes every screen at once.
    /// </summary>
    private void LayoutScreenHost()
    {
        if (_screenHost is null)
            return;
        Vector2 vp = Size;
        if (vp.X <= 0f || vp.Y <= 0f)
            return;
        float k = vp.Y / DesignHeight;
        if (k <= 0f)
            k = 1f;
        // Design space is as wide as the viewport's aspect demands (so pillarboxing is computed in design coords)
        // and exactly DesignHeight tall; scaling by k = vp.Y/DesignHeight maps it back to fill the real viewport.
        _screenHost.Position = Vector2.Zero;
        _screenHost.Scale = new Vector2(k, k);
        _screenHost.Size = new Vector2(vp.X / k, DesignHeight);
    }

    private void BuildBackground()
    {
        // The Xonotic menu backdrop: the luma skin's space/earth photo (gfx/menu/luma/background.tga), scaled to
        // cover the viewport (the skin's ALIGN_BACKGROUND "c5h5" = crop-centre, fit-to-height) and darkened a
        // little so the full-bleed dialog text stays legible over it. The full QC menu also crossfades rotating
        // map screenshots in front of this (nexposee.qc) — an animation polish item — but the skinned space
        // backdrop itself is the signature look, and it's real here.
        Texture2D? space = MenuSkin.Background;
        if (space != null)
        {
            var bg = new TextureRect
            {
                Name = "Background",
                Texture = space,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = MouseFilterEnum.Stop,
                // Near-full brightness like Base — the nexposee/dialog panels supply their own dark backing, so
                // the space backdrop is meant to read vivid rather than dimmed (only a faint cool tint).
                Modulate = new Color(0.92f, 0.94f, 1.0f, 1f),
            };
            bg.SetAnchorsPreset(LayoutPreset.FullRect);
            _background = bg;
            AddChild(_background);
            return;
        }

        // Fallback (no content repo / headless): a skinned vertical gradient in the same palette.
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(0.10f, 0.12f, 0.16f)); // top
        gradient.SetColor(1, new Color(0.04f, 0.05f, 0.07f)); // bottom

        var texture = new GradientTexture2D
        {
            Gradient = gradient,
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0.5f, 0f),
            FillTo = new Vector2(0.5f, 1f),
            Width = 16,
            Height = 256,
        };

        var rect = new TextureRect
        {
            Name = "Background",
            Texture = texture,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Stop,
        };
        rect.SetAnchorsPreset(LayoutPreset.FullRect);
        _background = rect;
        AddChild(_background);
    }

    // -------------------------------------------------------------------------------------------------
    //  Screen-stack navigation
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replace the entire stack with a single root screen. Frees any screens currently in the stack.
    /// Use this for top-level entry (the main menu); use <see cref="Push"/> to layer dialogs on top.
    /// </summary>
    public void ShowScreen(Control screen)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
            _stack[i].Host.QueueFree();
        _stack.Clear();

        AddScreen(screen, framed: false);
    }

    /// <summary>
    /// Overlay <paramref name="screen"/> on top of the current one, framed as a Xonotic dialog panel. The
    /// screen underneath is hidden (but kept) so <see cref="Pop"/> can restore it. The new screen is given a
    /// reference back to this host (via <see cref="MenuScreen"/>) when it implements it, so it can navigate on.
    /// </summary>
    public void Push(Control screen) => AddScreen(screen, framed: true);

    /// <summary>
    /// Push <paramref name="screen"/> framed (like <see cref="Push"/>), but mark it so that closing it while a
    /// match is live resumes the match (fires <see cref="ResumeRequested"/>) rather than popping back to the
    /// pause menu. This is the C# successor to QC <c>m_goto(name, true)</c> — the <c>menu_cmd directmenu</c>
    /// "hide the menu when this dialog is closed" behavior (dialog_gamemenu's Servers/Profile/Input buttons).
    /// </summary>
    public void PushResumeOnClose(Control screen) => AddScreen(screen, framed: true, resumeOnClose: true);

    /// <summary>
    /// Add a screen to the stack. A root screen (<paramref name="framed"/> false) fills the viewport; a
    /// pushed sub-dialog is wrapped in a centered, bordered <see cref="PanelContainer"/> (the skin's border.tga
    /// dialog frame, via the menu theme) so every dialog gets the authentic translucent-blue Xonotic panel.
    /// </summary>
    private void AddScreen(Control screen, bool framed, bool resumeOnClose = false)
    {
        if (_stack.Count > 0)
            _stack[^1].Host.Visible = false;

        if (screen is IMenuScreen ms)
            ms.Menu = this;

        Control host;
        // A dialog that draws its own modal frame (its own dim backdrop + centered panel, e.g. the quit
        // confirm) opts out of the outer frame so it isn't double-framed into a tiny panel-in-a-panel.
        if (framed && screen is not ISelfFramedDialog)
        {
            // A centered panel inset from the viewport edges; the PanelContainer lays the screen out to fill
            // its content area, so the screen's own layout reflows inside the frame with no per-dialog changes.
            var frame = new PanelContainer { Name = "DialogFrame", MouseFilter = MouseFilterEnum.Stop };
            frame.AnchorLeft = 0.045f;
            frame.AnchorTop = 0.05f;
            frame.AnchorRight = 0.955f;
            frame.AnchorBottom = 0.95f;
            frame.OffsetLeft = frame.OffsetTop = frame.OffsetRight = frame.OffsetBottom = 0;
            frame.AddChild(screen);
            host = frame;
        }
        else
        {
            screen.SetAnchorsPreset(LayoutPreset.FullRect);
            host = screen;
        }

        _stack.Add((host, screen, resumeOnClose));
        _screenHost.AddChild(host);
        host.Visible = true;
    }

    /// <summary>
    /// Close the top screen and re-show the one beneath it. No-op (keeps the last screen) if only the
    /// root remains, so the menu can never be left empty. This is the universal "Back" / "Cancel".
    /// </summary>
    public void Pop()
    {
        if (_stack.Count <= 1)
        {
            // At the root: if it's the nexposee with an open dialog, "Back" collapses it to the fan (the
            // top-level dialogs live inside the nexposee, not on the stack, so there's nothing to pop).
            if (Current is MainMenu nexposee)
                nexposee.CloseActivePanel();
            return; // never pop the root screen away
        }

        bool resumeOnClose = _stack[^1].ResumeOnClose;
        Control top = _stack[^1].Host;
        _stack.RemoveAt(_stack.Count - 1);
        top.QueueFree();

        if (_stack.Count > 0)
            _stack[^1].Host.Visible = true;

        // QC m_goto(name, true): a directmenu-opened dialog hides the whole menu on close, dropping the player
        // back into the live match. The Shell's ResumeRequested handler no-ops when no match is running, so this
        // is safe at the main menu (it just leaves the previous screen showing).
        if (resumeOnClose)
            ResumeRequested?.Invoke();
    }
}

/// <summary>
/// Implemented by every screen so it can reach back to its host for navigation. Screens get their
/// <see cref="Menu"/> assigned by <see cref="MenuRoot.Push"/> right before they enter the tree.
/// (Mirrors how QC dialogs hold a reference to <c>main</c>.)
/// </summary>
public interface IMenuScreen
{
    MenuRoot? Menu { get; set; }
}

/// <summary>
/// Marker for a screen that draws its OWN modal frame — a dim backdrop plus a small centered panel sized to
/// its content (the confirm-style dialogs). Such a screen is shown full-rect, bypassing <see cref="MenuRoot"/>'s
/// standard large bordered dialog frame, so a small prompt isn't stranded inside a big empty panel.
/// </summary>
public interface ISelfFramedDialog { }
