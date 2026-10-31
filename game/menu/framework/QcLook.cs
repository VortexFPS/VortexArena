using System;
using System.Runtime.CompilerServices;
using Godot;
using VortexArena.Game.Text;
using VortexArena.Legacy.Presentation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The QC menu's drawing primitives in design units (menu/draw.qc): <c>draw_ButtonPicture</c>,
/// <c>draw_VertButtonPicture</c>, <c>draw_Picture</c> and a Label's text placement (item/label.qc
/// <c>recalcPositionWithText</c>: aligned within the keepspace margins, vertically centred, and condensed
/// horizontally rather than overflowing). Fonts are the DarkPlaces font maps the rest of the menu uses
/// (<see cref="DpBitmapFont"/>), so a string measured here is the width Xonotic measures.
/// </summary>
public static class QcDraw
{
    /// <summary>A skin picture by its SKINGFX name (<c>"button_n"</c>, <c>"checkbox_f1"</c>); null without data.</summary>
    public static Texture2D? Skin(string name) => MenuSkin.SkinImage(name);

    /// <summary>draw_ButtonPicture: square end caps the height of the box, the middle half stretched.</summary>
    public static void ButtonPicture(CanvasItem ci, Rect2 r, Texture2D? tex, Color modulate)
    {
        if (tex is null || r.Size.X <= 0f || r.Size.Y <= 0f) return;
        Vector2 ts = tex.GetSize();
        float w = r.Size.X, h = r.Size.Y;
        if (w <= h * 2f)
        {
            float half = w * 0.5f, bw = 0.25f * w / (h * 2f);
            ci.DrawTextureRectRegion(tex, new Rect2(r.Position, new Vector2(half, h)), new Rect2(0f, 0f, bw * ts.X, ts.Y), modulate);
            ci.DrawTextureRectRegion(tex, new Rect2(r.Position + new Vector2(half, 0f), new Vector2(half, h)),
                new Rect2((1f - bw) * ts.X, 0f, bw * ts.X, ts.Y), modulate);
            return;
        }
        ci.DrawTextureRectRegion(tex, new Rect2(r.Position, new Vector2(h, h)), new Rect2(0f, 0f, 0.25f * ts.X, ts.Y), modulate);
        ci.DrawTextureRectRegion(tex, new Rect2(r.Position + new Vector2(h, 0f), new Vector2(w - 2f * h, h)),
            new Rect2(0.25f * ts.X, 0f, 0.5f * ts.X, ts.Y), modulate);
        ci.DrawTextureRectRegion(tex, new Rect2(r.Position + new Vector2(w - h, 0f), new Vector2(h, h)),
            new Rect2(0.75f * ts.X, 0f, 0.25f * ts.X, ts.Y), modulate);
    }

    /// <summary>The font map for <paramref name="menuPx"/>-high text (SKINFONTSIZE_*), and the size to ask it for.</summary>
    public static (Font? Font, int Key) FontFor(float menuPx, bool bold)
    {
        int slot = bold ? DpText.MenuBold : DpText.Menu;
        int key = Math.Max(1, Mathf.RoundToInt(menuPx * MenuSkin.BodySize / MenuMetrics.FontNormal));
        if (MenuState.Vfs != null && DpText.Faces(slot).Count > 0)
            return (DpBitmapFont.Menu(slot, menuPx, key), key);
        return (bold ? MenuSkin.BoldFont : MenuSkin.BodyFont, key);
    }

    /// <summary>draw_TextWidth in design units: DrawQ_TextWidth's own measurement (hinted advances of the font map
    /// at the window's real pixel size, kerning included), so a label is placed where Xonotic places it.</summary>
    public static float TextWidth(string? text, float menuPx = MenuMetrics.FontNormal, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        int slot = bold ? DpText.MenuBold : DpText.Menu;
        if (MenuState.Vfs != null && DpText.Faces(slot).Count > 0 && Godot.Engine.GetMainLoop() is SceneTree tree && tree.Root.Size.Y > 0)
        {
            // Real pixels -> design units: the menu's design space is DesignHeight units high.
            float realPerDesign = tree.Root.Size.Y / DpBitmapFont.MenuDesignHeight;
            return DpText.Measure(slot, text, menuPx * DpText.PixelsPerUnit) / realPerDesign;
        }
        (Font? font, int key) = FontFor(menuPx, bold);
        return font?.GetStringSize(text, HorizontalAlignment.Left, -1, key).X ?? 0f;
    }

    /// <summary>
    /// draw_Text: <paramref name="text"/> with the top of its character cell at <paramref name="topLeft"/>,
    /// squeezed horizontally by <paramref name="condense"/> (a Label's condenseFactor).
    /// </summary>
    public static void Text(CanvasItem ci, Vector2 topLeft, string? text, Color color, float menuPx = MenuMetrics.FontNormal,
        bool bold = false, float condense = 1f, bool snapX = true)
    {
        if (string.IsNullOrEmpty(text) || color.A <= 0f) return;
        int slot = bold ? DpText.MenuBold : DpText.Menu;
        if (MenuState.Vfs != null && DpText.Faces(slot).Count > 0)
        {
            // DrawQ_String itself (DpText), in SCREEN pixels: it snaps the start to a whole pixel and lays the
            // glyphs out with their hinted advances from there. Drawn in the menu's scaled design units instead,
            // Godot rounds each glyph's position to a whole design unit - a fraction of a screen pixel - and the
            // text comes out half a pixel off and blurred. Colours here already carry the r_text* lift that
            // DrawQ_String applies, so it is taken off again first.
            Transform2D xf = ci.GetGlobalTransformWithCanvas();
            LegacyTextLook look = DpText.Look;
            float k = look.Contrast != 0f ? 1f / look.Contrast : 1f;
            var raw = new Color((color.R - look.Brightness) * k, (color.G - look.Brightness) * k, (color.B - look.Brightness) * k, color.A);
            // A scaled-down dialog (a nexposee preview) draws smaller text, as draw_scale makes it in the QC.
            float scale = Godot.Engine.GetMainLoop() is SceneTree t && t.Root.Size.Y > 0
                ? xf.Scale.Y / (t.Root.Size.Y / DpBitmapFont.MenuDesignHeight)
                : 1f;
            ci.DrawSetTransformMatrix(xf.AffineInverse());
            DpText.Draw(ci, slot, xf * topLeft, text, menuPx * DpText.PixelsPerUnit * scale, raw, Vector2.Zero, condense, snapX: snapX);
            ci.DrawSetTransformMatrix(Transform2D.Identity);
            return;
        }
        // No game data (headless): Godot's own text at the fallback font.
        (Font? font, int key) = FontFor(menuPx, bold);
        if (font is null) return;
        ci.DrawSetTransform(topLeft, 0f, new Vector2(condense, 1f));
        ci.DrawString(font, new Vector2(0f, font.GetAscent(key)), text, HorizontalAlignment.Left, -1, key, color);
        ci.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// <summary>
    /// Label_recalcPositionWithText for a <paramref name="box"/>: where the text starts and how much it is condensed.
    /// <paramref name="keepLeft"/>/<paramref name="keepRight"/> are design units. With <paramref name="allowCut"/>
    /// a text too wide is left at full width (the caller clips it) instead of being condensed.
    /// </summary>
    public static (Vector2 TopLeft, float Condense) Place(Rect2 box, string? text, float align, float keepLeft = 0f,
        float keepRight = 0f, float menuPx = MenuMetrics.FontNormal, bool bold = false, bool allowCut = false)
    {
        float avail = box.Size.X - keepLeft - keepRight;
        float used = TextWidth(text, menuPx, bold);
        float x, condense = 1f;
        if (used <= avail) x = align * (avail - used) + keepLeft;
        else
        {
            x = keepLeft;
            if (!allowCut && used > 0f) condense = Math.Max(0f, avail) / used;
        }
        float y = 0.5f * (box.Size.Y - MenuMetrics.U(menuPx));
        return (box.Position + new Vector2(x, y), condense);
    }

    /// <summary>draw_TextShortenToWidth (common/util.qc textShortenToWidth): as much as fits, with "..." after it.</summary>
    public static string ShortenToWidth(string text, float width, float menuPx = MenuMetrics.FontNormal, bool bold = false)
    {
        if (TextWidth(text, menuPx, bold) <= width) return text;
        float room = width - TextWidth("...", menuPx, bold);
        int n = text.Length;
        while (n > 0 && TextWidth(text[..n], menuPx, bold) > room) n--;
        return text[..n] + "...";
    }

    /// <summary>draw_TextWidth with allowColorCodes: the colour codes take no room.</summary>
    public static float TextWidthColors(string? text, float menuPx = MenuMetrics.FontNormal, bool bold = false)
        => TextWidth(MenuColorCodes.Strip(text), menuPx, bold);

    /// <summary>
    /// draw_Text with allowColorCodes: each run in its code's colour times <paramref name="color"/> (^7: just
    /// <paramref name="color"/>). <paramref name="color"/> is the raw colour, before DrawQ_String's r_text* lift.
    /// </summary>
    public static void ColoredText(CanvasItem ci, Vector2 topLeft, string? text, Color color, float menuPx = MenuMetrics.FontNormal)
    {
        // DrawQ_String snaps the start of the string once; the runs after the first carry on from the unsnapped pen.
        Transform2D xf = ci.GetGlobalTransformWithCanvas();
        float gx = (xf * topLeft).X;
        float snapped = xf.X.X != 0f ? (LegacyTextLayout.SnapX(gx) - gx) / xf.X.X : 0f;
        float x = 0f;
        bool first = true;
        foreach ((string run, Color c) in MenuColorCodes.Segments(text, color))
        {
            Text(ci, topLeft + new Vector2(first ? 0f : snapped + x, 0f), run, MenuSkin.TextLift(c), menuPx, snapX: first);
            x += TextWidth(run, menuPx);
            first = false;
        }
    }

    /// <summary>
    /// getWrappedLine (common/util.qc): takes the next line, at most <paramref name="width"/> wide, off the front of
    /// <paramref name="remaining"/>, broken after its last space (the space dropped) or mid-word when it has none;
    /// <paramref name="remaining"/> becomes null once nothing is left. With <paramref name="colors"/>
    /// (draw_TextWidth_WithColors) colour codes take no room and the next line starts with the colour in force.
    /// </summary>
    public static string WrappedLine(ref string? remaining, float width, bool colors = false, Func<string, float>? measure = null)
    {
        string s = remaining ?? "";
        if (width <= 0f) { remaining = null; return s; }
        measure ??= t => TextWidth(t);
        Func<string, float> w = colors ? t => measure(MenuColorCodes.Strip(t)) : measure;
        int take = LengthUpToWidth(s, width, w, colors);
        if (take <= 0 || take >= s.Length) { remaining = null; return s; }

        // take_wrapped_line_until: back from the cut to the last ascii space.
        int i = take, skip = 0;
        while (i > 0)
        {
            --i;
            if (s[i] == ' ') { skip = 1; break; }
        }
        if (i != 0) take = i;
        string rest = s[Math.Min(s.Length, take + skip)..];
        remaining = rest.Length == 0 ? null : colors ? LastColorCode(s[..take]) + rest : rest;
        return s[..take];
    }

    /// <summary>
    /// draw_TextWidth as a list's resizeNotify measures it (rewrapCampaign, wrapGuideText). m_draw calls
    /// main.resizeNotify right after draw_reset_cropped, so draw_scale is the whole menu canvas then, not the item: the
    /// font size, relative to an item of <paramref name="itemSize"/>, is turned into a size as many times larger as the
    /// canvas is than the item, measured with that much larger font map, and the width scaled back down linearly.
    /// The hinted advances of the large map are narrower or wider than those the text is drawn with, so the wrap
    /// falls where Xonotic's falls only when measured this way.
    /// </summary>
    public static float TextWidthAtResize(string? text, Vector2 itemSize, float menuPx = MenuMetrics.FontNormal)
    {
        if (string.IsNullOrEmpty(text) || itemSize.X <= 0f || itemSize.Y <= 0f) return 0f;
        if (MenuState.Vfs == null || DpText.Faces(DpText.Menu).Count == 0 || Godot.Engine.GetMainLoop() is not SceneTree tree || tree.Root.Size.Y <= 0)
            return TextWidth(text, menuPx);
        float u = MenuMetrics.DesignUnit;
        float realPerDesign = tree.Root.Size.Y / DpBitmapFont.MenuDesignHeight;
        float conW = MenuMetrics.ConWidth * u, conH = MenuMetrics.ConHeight * u;
        float h = MenuMetrics.U(menuPx) * conH / itemSize.Y, w = MenuMetrics.U(menuPx) * conW / itemSize.X;
        float px = h * realPerDesign;
        float atCon = DpText.Measure(DpText.Menu, text, px) / realPerDesign * (w / h); // real px in and out
        return atCon * itemSize.X / conW;
    }

    /// <summary>textLengthUpToWidth: the longest prefix no wider than <paramref name="width"/>, never cutting a
    /// colour code in two (checkColorCode).</summary>
    private static int LengthUpToWidth(string s, float width, Func<string, float> w, bool colors)
    {
        if (w(s) <= width) return s.Length;
        int left = 0, right = s.Length;
        do
        {
            int middle = (left + right) / 2;
            int end = colors ? Math.Min(s.Length, middle + ColorCodeOverhang(s, middle)) : middle;
            if (w(s[..end]) <= width) left = end;
            else right = middle;
        }
        while (left < right - 1);
        return left;
    }

    /// <summary>How far past <paramref name="at"/> the colour code it falls inside runs (0 outside one).</summary>
    private static int ColorCodeOverhang(string s, int at)
    {
        for (int p = Math.Max(0, at - 4); p < at; p++)
        {
            if (s[p] != '^' || p + 1 >= s.Length || (p > 0 && s[p - 1] == '^')) continue;
            int len = char.IsAsciiDigit(s[p + 1]) ? 2 : s[p + 1] == 'x' && p + 4 < s.Length ? 5 : 0;
            if (len > 0 && at < p + len) return p + len - at;
        }
        return 0;
    }

    /// <summary>find_last_color_code: the last ^N or ^xRGB code in <paramref name="s"/>, or "".</summary>
    private static string LastColorCode(string s)
    {
        for (int p = s.Length - 2; p >= 0; p--)
        {
            if (s[p] != '^' || (p > 0 && s[p - 1] == '^')) continue;
            if (char.IsAsciiDigit(s[p + 1])) return s.Substring(p, 2);
            if (s[p + 1] == 'x' && p + 4 < s.Length && Uri.IsHexDigit(s[p + 2]) && Uri.IsHexDigit(s[p + 3]) && Uri.IsHexDigit(s[p + 4]))
                return s.Substring(p, 5);
        }
        return "";
    }

    /// <summary>A label's text drawn in <paramref name="box"/> the way <see cref="QcLabel"/> draws it.</summary>
    public static void LabelText(CanvasItem ci, Rect2 box, string? text, Color color, float align, float keepLeft = 0f,
        float keepRight = 0f, float menuPx = MenuMetrics.FontNormal, bool bold = false)
    {
        (Vector2 at, float condense) = Place(box, text, align, keepLeft, keepRight, menuPx, bold);
        Text(ci, at, text, color, menuPx, bold, condense);
    }
}

/// <summary>
/// Gives stock Godot controls the QC menu's look: the control keeps its behaviour (pressing, toggling, editing,
/// focus, its cvar binding) and its own drawing is blanked, then the picture and text are drawn the way
/// item/button.qc, item/checkbox.qc and item/inputbox.qc draw them. This is how the existing cvar widgets
/// (<see cref="CvarCheckBox"/>, <see cref="CvarLineEdit"/>, plain buttons) become pixel-faithful without a
/// second widget set.
///
/// <para>QC focus follows the mouse: the hovered item is the focused one, which is what picks the <c>_f</c>
/// picture. Keyboard focus counts too, but only while the keyboard is being used to move it, so a clicked
/// button does not stay lit after the pointer leaves it.</para>
/// </summary>
public static class QcLook
{
    private sealed class State
    {
        public bool Held;
        public Func<bool>? ForcePressed;
        public string Image = "button";
        public float Align = 0.5f;
    }

    private static readonly ConditionalWeakTable<Control, State> States = new();
    private static Texture2D? _blank;

    /// <summary>True while focus is being moved with the keyboard (set by <see cref="NoteInput"/>).</summary>
    public static bool KeyboardNavigating { get; private set; }

    /// <summary>Feed every menu input event here so keyboard focus only lights a widget while it is in use.</summary>
    public static void NoteInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true }) KeyboardNavigating = true;
        else if (e is InputEventMouseButton { Pressed: true } or InputEventMouseMotion) KeyboardNavigating = false;
    }

    private static bool Focused(Control c) => c.IsVisibleInTree() && ((c is BaseButton b ? b.IsHovered() : c.GetGlobalRect().HasPoint(c.GetGlobalMousePosition()))
        || (KeyboardNavigating && c.HasFocus()));

    private static Texture2D Blank()
    {
        if (_blank is null)
        {
            Image img = Godot.Image.CreateEmpty(1, 1, false, Godot.Image.Format.Rgba8);
            img.Fill(Colors.Transparent);
            _blank = ImageTexture.CreateFromImage(img);
        }
        return _blank;
    }

    private static State Track(Control c)
    {
        State s = States.GetOrCreateValue(c);
        c.SetMeta(MetaKey, true);
        c.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                s.Held = mb.Pressed;
                c.QueueRedraw();
            }
        };
        c.MouseExited += () => { s.Held = false; c.QueueRedraw(); };
        c.MouseEntered += c.QueueRedraw;
        c.FocusEntered += c.QueueRedraw;
        c.FocusExited += c.QueueRedraw;
        return s;
    }

    private static void Blank(Control c, params string[] styleboxes)
    {
        var empty = new StyleBoxEmpty();
        foreach (string name in styleboxes) c.AddThemeStyleboxOverride(name, empty);
        foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color",
                     "font_hover_pressed_color", "font_disabled_color", "font_outline_color", "font_uneditable_color",
                     "font_placeholder_color", "font_selected_color", "caret_color" })
            c.AddThemeColorOverride(name, Colors.Transparent);
    }

    /// <summary>
    /// A XonoticButton (makeXonoticButton): the 4:1 <paramref name="image"/> picture in its n/f/c/d state and the
    /// text centred between half-character margins, condensed when it does not fit. <paramref name="tint"/> makes it
    /// a coloured button (<c>makeXonoticButton(text, '0 1 0')</c>): the gray art, tinted in every state but disabled
    /// (xonotic/button.qc configureXonoticButton sets color, colorC and colorF). <paramref name="forcePressed"/>
    /// holds the clicked picture (the showing tab, the active sort column).
    /// </summary>
    public static T Button<T>(T b, Color? tint = null, Func<bool>? forcePressed = null, string image = "button") where T : BaseButton
    {
        State s = Track(b);
        s.ForcePressed = forcePressed;
        // A QC item is exactly its cell: no minimum size of its own (MakeButton's would make it taller).
        b.CustomMinimumSize = Vector2.Zero;
        s.Image = tint is null ? image : image + "gray";
        if (b is Button btn)
        {
            btn.ClipText = true;
            btn.Flat = false;
            Blank(btn, "normal", "hover", "pressed", "disabled", "focus", "hover_pressed", "normal_mirrored",
                "hover_mirrored", "pressed_mirrored", "disabled_mirrored", "hover_pressed_mirrored");
        }
        b.Draw += () =>
        {
            var r = new Rect2(Vector2.Zero, b.Size);
            string text = (b as Button)?.Text ?? "";
            if (b.Disabled)
                QcDraw.ButtonPicture(b, r, QcDraw.Skin(s.Image + "_d"), MenuSkin.Fade(MenuSkin.ButtonDisabledTint, MenuSkin.DisabledAlpha));
            else if (s.ForcePressed?.Invoke() == true || s.Held)
                QcDraw.ButtonPicture(b, r, QcDraw.Skin(s.Image + "_c"), tint ?? MenuSkin.ButtonClicked);
            else if (Focused(b))
                QcDraw.ButtonPicture(b, r, QcDraw.Skin(s.Image + "_f"), tint ?? MenuSkin.ButtonFocused);
            else
                QcDraw.ButtonPicture(b, r, QcDraw.Skin(s.Image + "_n"), tint ?? MenuSkin.ButtonNormal);
            // xonotic/button.qh: marginLeft = marginRight = SKINMARGIN_BUTTON (0.5) characters.
            float margin = 0.5f * MenuMetrics.FontNormalU;
            Color color = b.Disabled ? MenuSkin.Fade(MenuSkin.Text, MenuSkin.DisabledAlpha) : MenuSkin.Text;
            QcDraw.LabelText(b, r, text, color, s.Align, margin, margin);
        };
        return b;
    }

    /// <summary>
    /// A XonoticCheckBox / XonoticRadioButton: the font-size square picture centred in the first row-height of
    /// the box (Button_draw's non-multi branch with keepspaceLeft = height / width), the label after it.
    /// <paramref name="image"/> is <c>"checkbox"</c> or <c>"radiobutton"</c>; <paramref name="pictureTint"/> is the
    /// picture's <c>color</c>/<c>colorC</c>/<c>colorF</c>/<c>colorD</c> when a dialog sets them (the cvar list's
    /// SKINCOLOR_CVARLIST_CONTROLS) — the label keeps its own colour.
    /// </summary>
    public static T CheckBox<T>(T c, string image = "checkbox", Color? pictureTint = null) where T : Button
    {
        State s = Track(c);
        s.Image = image;
        c.CustomMinimumSize = Vector2.Zero;
        c.ClipText = true;
        Blank(c, "normal", "hover", "pressed", "disabled", "focus", "hover_pressed", "normal_mirrored",
            "hover_mirrored", "pressed_mirrored", "disabled_mirrored", "hover_pressed_mirrored");
        foreach (string icon in new[] { "checked", "unchecked", "checked_disabled", "unchecked_disabled", "radio_checked",
                     "radio_unchecked", "radio_checked_disabled", "radio_unchecked_disabled", "checked_mirrored",
                     "unchecked_mirrored", "radio_checked_mirrored", "radio_unchecked_mirrored" })
            c.AddThemeIconOverride(icon, Blank());
        c.Draw += () =>
        {
            float h = c.Size.Y, fs = MenuMetrics.FontNormalU;
            float keep = Math.Min(0.8f * c.Size.X, h);
            string suffix = c.ButtonPressed ? "1" : "0";
            string state = c.Disabled ? "_d" : s.Held ? "_c" : Focused(c) ? "_f" : "_n";
            Color picture = pictureTint ?? Colors.White;
            Color tint = c.Disabled ? MenuSkin.Fade(picture, MenuSkin.DisabledAlpha) : picture;
            if (QcDraw.Skin(s.Image + state + suffix) is { } tex)
                c.DrawTextureRect(tex, new Rect2(0.5f * (keep - fs), 0.5f * (h - fs), fs, fs), false, tint);
            Color color = c.Disabled ? MenuSkin.Fade(MenuSkin.Text, MenuSkin.DisabledAlpha) : MenuSkin.Text;
            QcDraw.LabelText(c, new Rect2(Vector2.Zero, c.Size), c.Text, color, 0f, keep);
        };
        return c;
    }

    /// <summary>
    /// A XonoticSlider (item/slider.qc <c>Slider_draw</c>) on a cvar slider row (<see cref="IQcSlider"/>): the
    /// slider_s track over the left 1 - SKINWIDTH_SLIDERTEXT of the box, the square knob (n/f/c/d) at the value,
    /// and the value's text centred in the right part. Godot's slider still takes the input; its look is blanked.
    /// </summary>
    public static T Slider<T>(T host, float valueSpace = -1f) where T : BoxContainer, IQcSlider
    {
        HSlider slider = host.Slider;
        Label readout = host.Readout;
        State s = Track(slider);
        host.SetMeta(MetaKey, true);
        host.AddThemeConstantOverride("separation", 0);
        // setValueSpace: the share of the width the text takes; SKINWIDTH_SLIDERTEXT unless the dialog changes it.
        valueSpace = Math.Clamp(valueSpace < 0f ? DefaultValueSpace : valueSpace, 0f, 1f);
        slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        slider.SizeFlagsVertical = Control.SizeFlags.Fill;
        slider.SizeFlagsStretchRatio = Math.Max(0.001f, 1f - valueSpace);
        slider.CustomMinimumSize = Vector2.Zero;
        readout.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        readout.SizeFlagsStretchRatio = Math.Max(0.001f, valueSpace);
        readout.CustomMinimumSize = Vector2.Zero;
        readout.Visible = valueSpace > 0f;
        readout.ClipText = true;
        readout.AddThemeColorOverride("font_color", Colors.Transparent);
        var empty = new StyleBoxEmpty();
        foreach (string name in new[] { "slider", "grabber_area", "grabber_area_highlight" })
            slider.AddThemeStyleboxOverride(name, empty);
        foreach (string name in new[] { "grabber", "grabber_highlight", "grabber_disabled", "tick" })
            slider.AddThemeIconOverride(name, Blank());
        slider.ValueChanged += _ => host.QueueRedraw();
        slider.MouseEntered += host.QueueRedraw;
        slider.MouseExited += host.QueueRedraw;
        slider.GuiInput += _ => host.QueueRedraw();
        readout.Draw += host.QueueRedraw;
        host.Draw += () =>
        {
            float w = host.Size.X, h = host.Size.Y, trackW = w * (1f - valueSpace);
            bool disabled = !slider.Editable;
            float a = disabled ? MenuSkin.DisabledAlpha : 1f;
            QcDraw.ButtonPicture(host, new Rect2(0f, 0f, trackW, h), QcDraw.Skin("slider_s"), MenuSkin.Fade(Colors.White, a));
            if (slider.MaxValue > slider.MinValue && host.ShownValue is { } shown)
            {
                float frac = (float)((shown - slider.MinValue) / (slider.MaxValue - slider.MinValue));
                float x = Math.Clamp(frac, 0f, 1f) * (trackW - h);
                string state = disabled ? "_d" : s.Held ? "_c" : Focused(slider) ? "_f" : "_n";
                if (QcDraw.Skin("slider" + state) is { } knob)
                    host.DrawTextureRect(knob, new Rect2(x, 0f, h, h), false, MenuSkin.Fade(Colors.White, a));
            }
            Color color = disabled ? MenuSkin.Fade(MenuSkin.Text, MenuSkin.DisabledAlpha) : MenuSkin.Text;
            if (valueSpace > 0f)
                QcDraw.LabelText(host, new Rect2(trackW, 0f, w - trackW, h), readout.Text, color, 0.5f);
        };
        return host;
    }

    /// <summary>SKINWIDTH_SLIDERTEXT: a slider's text share of its width.</summary>
    public static float DefaultValueSpace => Math.Clamp(MenuSkin.SkinNumber("WIDTH_SLIDERTEXT", 1f / 3f), 0f, 1f);

    /// <summary>
    /// InputBox_draw with <c>editColorCodes</c> (the Profile name box): the text one character at a time in the colour
    /// the codes so far give it, each code drawn as its own characters over a white box (brighter when the cursor
    /// is inside it), "^^" as a boxed caret, and an unfinished ^x code over a red, green or blue box for the first
    /// missing component.
    /// </summary>
    private static void DrawColorCodes(CanvasItem ci, Vector2 p, string text, int cursor, float alpha)
    {
        float fs = MenuMetrics.FontNormalU;
        Color color = Colors.White;
        float colorAlpha = 1f;
        void Box(float w, Color c, float a) => ci.DrawRect(new Rect2(p, new Vector2(w, fs)), new Color(c.R, c.G, c.B, a * alpha));
        void Draw(Vector2 at, string s, Color c, float a) => QcDraw.Text(ci, at, s, MenuSkin.TextLift(new Color(c.R, c.G, c.B, a * alpha)));
        static int Hex(char ch) => ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;

        for (int i = 0; i < text.Length; ++i)
        {
            char ch = text[i];
            if (ch != '^')
            {
                Draw(p, ch.ToString(), color, colorAlpha);
                p.X += QcDraw.TextWidth(ch.ToString());
                continue;
            }
            string ch2 = i + 1 < text.Length ? text[i + 1].ToString() : "";
            float w = QcDraw.TextWidth("^" + ch2);
            float fill = 0.4f;
            bool cursorIn(int n) => cursor > i && cursor <= i + n;
            if (ch2 == "^")
            {
                if (cursorIn(2)) fill = 0.6f;
                Box(w, Colors.White, fill);
                Draw(p + new Vector2(0.25f * w, 0f), "^", color, colorAlpha);
            }
            else if (ch2.Length == 1 && char.IsAsciiDigit(ch2[0]))
            {
                colorAlpha = 1f;
                color = (ch2[0] - '0') switch
                {
                    0 => new Color(0, 0, 0), 1 => new Color(1, 0, 0), 2 => new Color(0, 1, 0), 3 => new Color(1, 1, 0),
                    4 => new Color(0, 0, 1), 5 => new Color(0, 1, 1), 6 => new Color(1, 0, 1), 9 => new Color(0.5f, 0.5f, 0.5f),
                    _ => Colors.White,
                };
                if (ch2[0] == '8') colorAlpha = 0.5f;
                if (cursorIn(2)) fill = 0.6f;
                Box(w, Colors.White, fill);
                Draw(p, "^" + ch2, color, colorAlpha);
            }
            else if (ch2 == "x")
            {
                color = Colors.White;
                int r = i + 2 < text.Length ? Hex(text[i + 2]) : -1;
                int g = i + 3 < text.Length ? Hex(text[i + 3]) : -1;
                int b = i + 4 < text.Length ? Hex(text[i + 4]) : -1;
                if (r >= 0 && g >= 0 && b >= 0)
                {
                    color = new Color(r / 15f, g / 15f, b / 15f);
                    w = QcDraw.TextWidth(text.Substring(i, 5));
                    if (cursorIn(5)) fill = 0.8f;
                    Box(w, Colors.White, fill);
                    Draw(p, text.Substring(i, 5), color, 1f);
                    i += 3;
                }
                else if (r >= 0 && g >= 0)
                {
                    w = QcDraw.TextWidth(text.Substring(i, 4));
                    Box(w, new Color(0, 0, 1), fill); // blue missing
                    Draw(p, text.Substring(i, 4), Colors.White, colorAlpha);
                    i += 2;
                }
                else if (r >= 0)
                {
                    w = QcDraw.TextWidth(text.Substring(i, 3));
                    Box(w, new Color(0, 1, 0), fill); // green missing
                    Draw(p, text.Substring(i, 3), Colors.White, colorAlpha);
                    ++i;
                }
                else
                {
                    Box(w, new Color(1, 0, 0), fill); // red missing
                    Draw(p, "^x", Colors.White, colorAlpha);
                }
            }
            else
            {
                Box(w, Colors.White, fill);
                Draw(p, "^" + ch2, color, colorAlpha);
            }
            p.X += w;
            ++i;
        }
    }

    /// <summary>Set on every control given the QC look: it draws its own disabled state (SKINALPHA_DISABLED), so
    /// <see cref="CvarUi.SetEnabledRecursive"/> must not fade it a second time.</summary>
    public const string MetaKey = "qc_look";

    /// <summary>
    /// A XonoticInputBox: the inputbox picture (focused or not), the text one character in from each side and
    /// clear of the clear button, white, with the <c>_</c> cursor - steady while the box is unfocused, blinking
    /// while it is being typed in (item/inputbox.qc <c>InputBox_draw</c>). Godot still does the editing.
    /// <paramref name="pictureTint"/> is the box's <c>color</c>/<c>colorF</c> and the clear button's <c>cb_color*</c>
    /// when a dialog sets them (the cvar list's SKINCOLOR_CVARLIST_CONTROLS).
    /// </summary>
    public static LineEdit InputBox(LineEdit e, bool clearButton = true, bool editColorCodes = false, Color? pictureTint = null)
    {
        State s = Track(e);
        s.Image = "inputbox";
        Blank(e, "normal", "focus", "read_only");
        e.AddThemeColorOverride("selection_color", new Color(1f, 1f, 1f, 0.25f));
        e.ClearButtonEnabled = false;
        // Godot's caret is hidden (Blank), but its blink keeps the box redrawing while it is focused, which the
        // QC's blinking "_" cursor needs.
        e.CaretBlink = true;
        double lastChange = 0;
        e.TextChanged += _ => lastChange = Time.GetTicksMsec() / 1000.0;

        float KeepLeft() => MenuSkin.InputBoxMarginChars * MenuMetrics.FontNormalU;
        float KeepRight() => KeepLeft() + (clearButton ? e.Size.Y : 0f);

        // The text is clipped to the area between the margins (draw_SetClipRect); a child with ClipContents is
        // the clip, so the picture and the clear button outside it are not cut.
        var clip = new Control { Name = "QcText", ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        e.AddChild(clip, false, Node.InternalMode.Front);
        void Fit()
        {
            clip.Position = new Vector2(KeepLeft(), 0f);
            clip.Size = new Vector2(Math.Max(0f, e.Size.X - KeepLeft() - KeepRight()), e.Size.Y);
        }
        e.Resized += Fit;
        Fit();
        clip.Draw += () =>
        {
            float fs = MenuMetrics.FontNormalU, avail = clip.Size.X;
            bool focused = e.HasFocus() && e.Editable;
            string text = e.Text ?? "";
            float cursorX = QcDraw.TextWidth(text[..Math.Clamp(e.CaretColumn, 0, text.Length)]);
            float total = QcDraw.TextWidth(text + "_");
            // InputBox_draw keeps the cursor inside the box and never scrolls past the end of the text.
            float scroll = Math.Max(0f, Math.Min(cursorX - 0.875f * avail, total - avail));
            float y = 0.5f * (clip.Size.Y - fs);
            Color white = e.Editable ? Colors.White : MenuSkin.Fade(Colors.White, MenuSkin.DisabledAlpha);
            if (editColorCodes)
                DrawColorCodes(clip, new Vector2(-scroll, y), text, e.CaretColumn, e.Editable ? 1f : MenuSkin.DisabledAlpha);
            else
                QcDraw.Text(clip, new Vector2(-scroll, y), text, white);
            double since = Time.GetTicksMsec() / 1000.0 - lastChange;
            if (!focused || since - Math.Floor(since) < 0.5)
                QcDraw.Text(clip, new Vector2(cursorX - scroll, y), "_", white);
        };
        e.Draw += () =>
        {
            float h = e.Size.Y;
            bool focused = e.HasFocus() && e.Editable;
            QcDraw.ButtonPicture(e, new Rect2(Vector2.Zero, e.Size), QcDraw.Skin(focused ? "inputbox_f" : "inputbox_n"), pictureTint ?? Colors.White);
            if (clearButton && e.Text.Length > 0)
            {
                bool over = Focused(e) && e.GetLocalMousePosition().X >= e.Size.X - h;
                string state = focused && over ? (s.Held ? "_c" : "_f") : "_n";
                if (QcDraw.Skin("clearbutton" + state) is { } cb)
                    e.DrawTextureRect(cb, new Rect2(e.Size.X - h, 0f, h, h), false, pictureTint ?? Colors.White);
            }
            clip.QueueRedraw();
        };
        if (clearButton)
            e.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb
                    && mb.Position.X >= e.Size.X - e.Size.Y && e.Text.Length > 0)
                {
                    e.Text = "";
                    e.EmitSignal(LineEdit.SignalName.TextChanged, "");
                    e.AcceptEvent();
                }
            };
        return e;
    }
}

/// <summary>
/// A XonoticTextLabel (makeXonoticTextLabel(align, text)) or header label: text in SKINCOLOR_TEXT at
/// SKINALPHA_TEXT, placed by item/label.qc's rules — aligned, vertically centred, condensed if it is too wide.
/// </summary>
public partial class QcLabel : Control
{
    private string _text = "";

    public QcLabel(string text = "", float align = 0f)
    {
        _text = Localization.Tr(text);
        Align = align;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; QueueRedraw(); } }
    }

    /// <summary>0 = left, 0.5 = centre, 1 = right (QC <c>align</c>).</summary>
    public float Align { get; set; }
    public bool Bold { get; set; }
    public float FontSize { get; set; } = MenuMetrics.FontNormal;
    /// <summary>The text colour with its alpha; null = SKINCOLOR_TEXT @ SKINALPHA_TEXT, read when drawn (the skin
    /// and the r_text* lift are not loaded yet when a dialog is built).</summary>
    public Color? Color { get; set; }
    public bool Disabled { get; set; }
    /// <summary>QC <c>marginLeft</c>/<c>marginRight</c>, in characters.</summary>
    public float MarginLeftChars { get; set; }
    public float MarginRightChars { get; set; }
    /// <summary>QC <c>allowColors</c>: draw ^-colour codes as colours instead of as text.</summary>
    public bool AllowColors { get; set; }
    /// <summary>QC <c>allowWrap</c>: the text wrapped (getWrappedLine) to the label's width, left-aligned, the block
    /// centred vertically.</summary>
    public bool AllowWrap { get; set; }
    /// <summary>QC <c>allowWrap_spacing</c>: the gap between wrapped lines, in line heights.</summary>
    public float WrapSpacing { get; set; }

    public override void _Draw()
    {
        Color color = Color ?? MenuSkin.Text;
        Color c = Disabled ? MenuSkin.Fade(color, MenuSkin.DisabledAlpha) : color;
        float ch = MenuMetrics.U(FontSize);
        if (AllowWrap && !AllowColors)
        {
            // Label_recalcPositionWithText / Label_draw: count the lines, centre the block, draw line by line.
            float left = MarginLeftChars * ch, width = Size.X - left - MarginRightChars * ch;
            var lines = new System.Collections.Generic.List<string>();
            string? remaining = _text;
            while (remaining is not null)
                lines.Add(QcDraw.WrappedLine(ref remaining, width, measure: t => QcDraw.TextWidth(t, FontSize, Bold)));
            float height = lines.Count * ch + (lines.Count - 1) * ch * WrapSpacing;
            var o = new Vector2(left, 0.5f * (Size.Y - height));
            foreach (string line in lines)
            {
                QcDraw.Text(this, o, line, c, FontSize, Bold);
                o.Y += ch * (1f + WrapSpacing);
            }
            return;
        }
        if (!AllowColors)
        {
            QcDraw.LabelText(this, new Rect2(Vector2.Zero, Size), _text, c, Align, MarginLeftChars * ch, MarginRightChars * ch, FontSize, Bold);
            return;
        }
        // Placed by the width of what is drawn (the codes take no room), then drawn run by run in its colours.
        (Vector2 at, float condense) = QcDraw.Place(new Rect2(Vector2.Zero, Size), MenuColorCodes.Strip(_text), Align,
            MarginLeftChars * ch, MarginRightChars * ch, FontSize, Bold);
        // Here the colour is the raw one DrawQ_String starts from (the codes replace it), lifted per run below.
        Color raw = Color ?? Colors.White;
        if (Disabled) raw = MenuSkin.Fade(raw, MenuSkin.DisabledAlpha);
        foreach ((string run, Color runColor) in MenuColorCodes.Segments(_text, raw))
        {
            QcDraw.Text(this, at, run, MenuSkin.TextLift(runColor), FontSize, Bold, condense);
            at.X += QcDraw.TextWidth(run, FontSize, Bold) * condense;
        }
    }

    public override Vector2 _GetMinimumSize() => Vector2.Zero;
}
