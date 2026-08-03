using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The root menu screen — a faithful port of Xonotic's <c>Nexposee</c> (item/nexposee.qc + xonotic/mainwindow.qc).
/// All six top-level dialogs are instantiated at once and shown as live, scaled-down preview panels in a fan over
/// the space backdrop, with the XONOTIC logo top-left. Clicking a preview animates that dialog from its small fan
/// slot up to its full centered size (the others fade out); its Back/close button, Escape, or a click outside
/// collapse it back. Credits and Quit are <em>pulled</em> so they start minimised to just their title bar at the
/// bottom corners (mainwindow.qc <c>pullNexposee</c>), expanding when chosen.
///
/// Sizing/positioning follow the QC exactly (see <see cref="MenuMetrics"/>): each dialog has its own
/// <c>intendedWidth</c> + <c>rows</c> (so the panels are genuinely different sizes), all centred when open; the
/// fan scale is the largest that keeps the previews from overlapping (Nexposee_calc); the small position is
/// <c>(initial − scaleCenter)·s + scaleCenter</c> toward each dialog's skin <c>POSITION_DIALOG_*</c> centre. Each
/// panel draws a proper Xonotic frame: a border whose thickness is the title-bar height, the title centred in the
/// top border, and a close (X) button in the top-right corner.
/// </summary>
public partial class MainMenu : MenuScreen
{
    /// <summary>Dev/CI: a panel title (e.g. "Settings") to open instantly on boot for a screenshot of the
    /// expanded state. Consumed once. Set by <see cref="Shell"/> from <c>--menu-screen nexposee:&lt;Title&gt;</c>.</summary>
    public static string? AutoOpen;

    private sealed class Tile
    {
        public string Title = "";
        public float IntendedWidth;
        public float Rows;
        public Vector2 ScaleCenter;   // POSITION_DIALOG_* (normalized canvas coords; may be outside 0..1)
        public bool Pull;             // minimized to title-bar-only (Credits/Quit)

        public Control Slot = null!;
        public Panel Frame = null!;
        public Label TitleLabel = null!;
        public Button Close = null!;
        public Control ContentHolder = null!;
        public Button Catcher = null!;

        public Vector2 InitialOrigin;  // centered top-left (normalized canvas coords)
        public Vector2 SmallOrigin;    // fan slot top-left (after scale calc + pull)
        public Vector2 SizeNorm;       // (intendedWidth, dialogHeight/conHeight)
    }

    private const float ClosedAlpha = 0.85f; // SKINALPHAS_MAINMENU preview alpha
    private const float AnimSpeed = 6.0f;

    private readonly List<Tile> _tiles = new();
    private Tile? _active;
    private bool _opening;
    private float _factor;
    private float _scale = 0.5f;          // fan preview scale (Nexposee_calc result)
    private Control _brand = null!;
    private TextureRect? _logo;
    private float _lastFit = -1f;
    private float _lastWidth = -1f;
    private StyleBox? _frameStyle;

    protected override void BuildUi()
    {
        Name = "MainMenu";
        MouseFilter = MouseFilterEnum.Pass;

        // Each dialog: intendedWidth + rows (the real QC values) + its skin scale-centre; Credits/Quit pulled.
        AddTile("Singleplayer", new SingleplayerScreen(), 0.80f, 24, new Vector2(0.15f, 0.40f), false);
        AddTile("Multiplayer", new MultiplayerScreen(), 0.96f, 24, new Vector2(0.85f, 0.40f), false);
        AddTile("Media", new MediaScreen(), 0.96f, 18, new Vector2(0.15f, 1.00f), false);
        AddTile("Settings", new DialogSettings(), 0.96f, 18, new Vector2(0.85f, 1.00f), false);
        AddTile("Credits", new CreditsScreen(), 0.50f, 20, new Vector2(-0.05f, 1.20f), true);
        AddTile("Quit", new QuitDialog { Embedded = true }, 0.50f, 3, new Vector2(1.05f, 1.20f), true);

        ComputeFanScale();

        // Brand logo top-left (added last → above the panels).
        var brand = new VBoxContainer { Name = "Brand", MouseFilter = MouseFilterEnum.Ignore };
        brand.AddThemeConstantOverride("separation", 0);
        if (MenuSkin.Logo is { } logoTex)
        {
            _logo = new TextureRect
            {
                Texture = logoTex,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            brand.AddChild(_logo);
        }
        else
        {
            var wordmark = new Label { Text = "XONOTIC", MouseFilter = MouseFilterEnum.Ignore };
            wordmark.AddThemeFontSizeOverride("font_size", MenuSkin.BrandSize);
            wordmark.AddThemeColorOverride("font_color", MenuSkin.Accent);
            if (MenuSkin.BoldFont is { } bold) wordmark.AddThemeFontOverride("font", bold);
            brand.AddChild(wordmark);
        }
        var sub = new Label { Text = "VortexArena", HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = MouseFilterEnum.Ignore };
        sub.AddThemeColorOverride("font_color", MenuSkin.Text);
        brand.AddChild(sub);
        _brand = brand;
        AddChild(_brand);

        SetProcess(true);

        if (AutoOpen != null)
        {
            foreach (Tile t in _tiles)
                if (t.Title.Equals(AutoOpen, StringComparison.OrdinalIgnoreCase))
                {
                    _active = t; _opening = true; _factor = 1f;
                    MoveChild(t.Slot, GetChildCount() - 2);
                    break;
                }
            AutoOpen = null;
        }
    }

    private void AddTile(string title, Control dialog, float intendedWidth, float rows, Vector2 scaleCenter, bool pull)
    {
        var t = new Tile { Title = title, IntendedWidth = intendedWidth, Rows = rows, ScaleCenter = scaleCenter, Pull = pull };

        var slot = new Control { Name = title + "Slot", MouseFilter = MouseFilterEnum.Pass, PivotOffset = Vector2.Zero, ClipContents = true };

        var frame = new Panel { Name = "Frame", MouseFilter = MouseFilterEnum.Ignore };
        frame.SetAnchorsPreset(LayoutPreset.FullRect);
        slot.AddChild(frame);

        // The dialog content lives in a holder inset by the title bar + margins; it keeps its MenuRoot for sub-nav.
        var holder = new Control { Name = "Content", MouseFilter = MouseFilterEnum.Pass };
        if (dialog is IMenuScreen ms) ms.Menu = Menu;
        if (dialog is MenuScreen msc) msc.HostProvidesTitle = true; // the frame draws the title bar
        dialog.SetAnchorsPreset(LayoutPreset.FullRect);
        holder.AddChild(dialog);
        slot.AddChild(holder);

        // Title bar: the dialog name centred in the top border.
        var titleLabel = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        titleLabel.AddThemeColorOverride("font_color", MenuSkin.Bright);
        if (MenuSkin.BoldFont is { } tb) titleLabel.AddThemeFontOverride("font", tb);
        slot.AddChild(titleLabel);

        // Close (X) button, top-right corner of the title bar.
        var close = new Button { Name = "Close", MouseFilter = MouseFilterEnum.Stop, Flat = true };
        if (MenuSkin.CloseButton is { } cb)
        {
            close.Icon = cb;
            close.ExpandIcon = true;
            var e = new StyleBoxEmpty();
            close.AddThemeStyleboxOverride("normal", e);
            close.AddThemeStyleboxOverride("hover", e);
            close.AddThemeStyleboxOverride("pressed", e);
            close.AddThemeStyleboxOverride("focus", e);
        }
        else
        {
            close.Text = "X";
        }
        close.Pressed += CloseActivePanel;
        slot.AddChild(close);

        // Whole-panel catcher: opens this dialog when clicked in the fan.
        var catcher = new Button { Name = "Open", MouseFilter = MouseFilterEnum.Stop };
        catcher.SetAnchorsPreset(LayoutPreset.FullRect);
        var empty = new StyleBoxEmpty();
        catcher.AddThemeStyleboxOverride("normal", empty);
        catcher.AddThemeStyleboxOverride("disabled", empty);
        catcher.AddThemeStyleboxOverride("pressed", empty);
        var glow = new StyleBoxFlat { BgColor = new Color(MenuSkin.Accent.R, MenuSkin.Accent.G, MenuSkin.Accent.B, 0.14f) };
        glow.SetCornerRadiusAll(6);
        catcher.AddThemeStyleboxOverride("hover", glow);
        catcher.AddThemeStyleboxOverride("focus", glow);
        catcher.Pressed += () => OpenPanel(t);
        slot.AddChild(catcher);

        t.Slot = slot; t.Frame = frame; t.TitleLabel = titleLabel; t.Close = close; t.ContentHolder = holder; t.Catcher = catcher;
        _tiles.Add(t);
        AddChild(slot);
    }

    /// <summary>Nexposee_calc: the largest preview scale that keeps the fan from overlapping, then ×0.95.</summary>
    private void ComputeFanScale()
    {
        foreach (Tile t in _tiles)
        {
            t.SizeNorm = new Vector2(t.IntendedWidth, MenuMetrics.DialogHeight(t.Rows) / MenuMetrics.ConHeight);
            t.InitialOrigin = new Vector2(0.5f - t.SizeNorm.X * 0.5f, 0.5f - t.SizeNorm.Y * 0.5f);
        }

        float s = 0.70f;
        for (int guard = 0; guard < 200; guard++, s *= 0.99f)
        {
            CalcSmall(s);
            if (!AnyOverlap(s))
                break;
            if (s < 0.2f)
                break;
        }
        _scale = s; // the largest non-overlapping scale (panels just touch) → fan as large/tight as Base
        CalcSmall(_scale);
    }

    private void CalcSmall(float s)
    {
        float alignY = MenuMetrics.TitleHeight / MenuMetrics.ConHeight;
        foreach (Tile t in _tiles)
        {
            float x = (t.InitialOrigin.X - t.ScaleCenter.X) * s + t.ScaleCenter.X;
            float y = t.Pull ? 1f - alignY * s : (t.InitialOrigin.Y - t.ScaleCenter.Y) * s + t.ScaleCenter.Y;
            t.SmallOrigin = new Vector2(x, y);
        }
    }

    private bool AnyOverlap(float s)
    {
        for (int i = 0; i < _tiles.Count; i++)
            for (int j = i + 1; j < _tiles.Count; j++)
            {
                Tile a = _tiles[i], b = _tiles[j];
                Vector2 amin = a.SmallOrigin, amax = amin + a.SizeNorm * s;
                Vector2 bmin = b.SmallOrigin, bmax = bmin + b.SizeNorm * s;
                bool xo = (bmin.X - amax.X) * (amin.X - bmax.X) > 0f;
                bool yo = (bmin.Y - amax.Y) * (amin.Y - bmax.Y) > 0f;
                if (xo && yo)
                    return true;
            }
        return false;
    }

    public override void _Process(double delta)
    {
        // (perf 2026-08-03) The menu tree stays INSTANTIATED during a match — Shell only sets
        // _menu.Visible = false — and Shell runs ProcessModeEnum.Always, so this kept running the full
        // LayoutFrame() every frame while completely invisible: ~40 marshalled property writes (per-tile
        // Lerp + Scale/Position/Modulate/Visible/MouseFilter, plus the brand modulate) for pixels nobody
        // renders. Nothing here has an effect while hidden, and the layout is recomputed on the frame it
        // reappears anyway (the animation restarts from _factor).
        if (!IsVisibleInTree())
            return;
        if (_active != null)
        {
            float dir = _opening ? 1f : -1f;
            _factor = Mathf.Clamp(_factor + dir * AnimSpeed * (float)delta, 0f, 1f);
            if (!_opening && _factor <= 0f)
                _active = null;
        }
        LayoutFrame();
    }

    private void LayoutFrame()
    {
        Vector2 vp = Size;
        if (vp.X <= 0 || vp.Y <= 0)
            return;

        float fit = MenuMetrics.FitScale(vp.Y);
        float canvasW = MenuMetrics.ConWidth * fit;
        float canvasH = MenuMetrics.ConHeight * fit;
        float canvasLeft = (vp.X - canvasW) * 0.5f;
        float canvasTop = (vp.Y - canvasH) * 0.5f;

        // Relayout when the fit OR the canvas width changes. The menu now lays out in a fixed-height design space
        // (MenuRoot scales the whole host to the viewport), so `fit` is effectively constant and only the design
        // width varies — when the viewport ASPECT changes. Track both so the brand/logo and slot sizes follow.
        if (!Mathf.IsEqualApprox(fit, _lastFit) || !Mathf.IsEqualApprox(vp.X, _lastWidth))
        {
            _lastFit = fit;
            _lastWidth = vp.X;
            RelayoutSlots(fit, canvasW, canvasH);
            float bw = 0.30f * vp.X;
            _brand.Position = new Vector2(canvasLeft + MenuMetrics.MarginLeft * fit, MenuMetrics.MarginTop * fit);
            if (_logo != null) _logo.CustomMinimumSize = new Vector2(bw, bw / 2.933f);
        }

        foreach (Tile t in _tiles)
        {
            Vector2 openSize = new(t.SizeNorm.X * canvasW, t.SizeNorm.Y * canvasH);
            Vector2 openOrigin = new(canvasLeft + (canvasW - openSize.X) * 0.5f, canvasTop + (canvasH - openSize.Y) * 0.5f);
            Vector2 closedOrigin = new(canvasLeft + t.SmallOrigin.X * canvasW, canvasTop + t.SmallOrigin.Y * canvasH);

            bool isActive = t == _active;
            float scale, alpha; Vector2 origin;
            if (isActive)
            {
                scale = Mathf.Lerp(_scale, 1f, _factor);
                origin = closedOrigin.Lerp(openOrigin, _factor);
                alpha = Mathf.Lerp(ClosedAlpha, 1f, _factor);
                t.Catcher.MouseFilter = _factor > 0.5f ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
                t.Close.Visible = _factor > 0.85f;
                t.Slot.Visible = true;
            }
            else
            {
                scale = _scale;
                origin = closedOrigin;
                alpha = _active == null ? ClosedAlpha : ClosedAlpha * (1f - _factor);
                t.Catcher.MouseFilter = _active == null ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
                t.Close.Visible = false;
                t.Slot.Visible = alpha > 0.02f;
            }

            t.Slot.Scale = new Vector2(scale, scale);
            t.Slot.Position = origin;
            t.Slot.Modulate = new Color(1f, 1f, 1f, alpha);
        }

        _brand.Modulate = new Color(1f, 1f, 1f, _active == null ? 1f : 1f - _factor);
        _brand.Visible = _brand.Modulate.A > 0.02f;
        MouseFilter = (_active != null && _factor > 0.5f) ? MouseFilterEnum.Stop : MouseFilterEnum.Pass;
    }

    /// <summary>Lay out each slot's children at the open size (screen px). Called on first frame + viewport resize.</summary>
    private void RelayoutSlots(float fit, float canvasW, float canvasH)
    {
        int borderPx = Mathf.Max(1, Mathf.RoundToInt(MenuMetrics.TitleHeight * fit));
        _frameStyle = MenuSkin.DialogFrame(borderPx);

        float th = MenuMetrics.TitleHeight * fit;
        float ml = MenuMetrics.MarginLeft * fit, mr = MenuMetrics.MarginRight * fit;
        float mt = MenuMetrics.MarginTop * fit, mb = MenuMetrics.MarginBottom * fit;

        foreach (Tile t in _tiles)
        {
            Vector2 openSize = new(t.SizeNorm.X * canvasW, t.SizeNorm.Y * canvasH);
            t.Slot.Size = openSize;
            t.Frame.Size = openSize;
            if (_frameStyle != null)
                t.Frame.AddThemeStyleboxOverride("panel", _frameStyle);

            t.TitleLabel.Position = new Vector2(th, 0);
            t.TitleLabel.Size = new Vector2(Mathf.Max(1, openSize.X - 2 * th), th);
            t.TitleLabel.AddThemeFontSizeOverride("font_size", Mathf.Max(8, Mathf.RoundToInt(MenuMetrics.FontTitle * fit)));

            t.Close.Position = new Vector2(openSize.X - th, 0);
            t.Close.Size = new Vector2(th, th);

            t.ContentHolder.Position = new Vector2(ml, th + mt);
            t.ContentHolder.Size = new Vector2(Mathf.Max(1, openSize.X - ml - mr), Mathf.Max(1, openSize.Y - th - mt - mb));
        }
    }

    private void OpenPanel(Tile t)
    {
        if (_active != null) return;
        _active = t;
        _opening = true;
        MoveChild(t.Slot, GetChildCount() - 2); // below the brand, above the rest
    }

    /// <summary>Begin collapsing the open dialog back to its fan slot. Safe when nothing is open.</summary>
    public void CloseActivePanel()
    {
        if (_active != null)
        {
            _opening = false;
            // Persist archived cvars when a panel collapses — the nexposee panels never leave the tree, so a
            // settings/profile edit would otherwise only be written at shutdown (DP saves config on menu exit).
            MenuState.SaveUserConfig();
        }
    }

    /// <summary>True while a dialog is open (or animating) — lets the host route Back/Escape to a collapse.</summary>
    public bool HasOpenPanel => _active != null;

    public override void _GuiInput(InputEvent @event)
    {
        if (_active != null && _factor > 0.5f && @event is InputEventMouseButton { Pressed: true } mb
            && mb.ButtonIndex == MouseButton.Left)
        {
            CloseActivePanel();
            AcceptEvent();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_active != null && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            CloseActivePanel();
            AcceptEvent();
        }
    }
}
