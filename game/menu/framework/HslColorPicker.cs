// Port of qcsrc/menu/xonotic/colorpicker.qc (XonoticColorpicker, the name ^-code picker) +
// qcsrc/menu/xonotic/colorpicker_string.qc (XonoticColorpickerString, the crosshair/model "r g b" picker).
using System;
using System.Globalization;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The HSL image color picker — a faithful C# port of the two QC colorpicker widgets. It draws the
/// SKINGFX_COLORPICKER hue/lightness image (gfx/menu/&lt;skin&gt;/colorpicker.tga) with its image margin and, on
/// click/drag inside the picker area, converts the position to an RGB color via
/// <see cref="MenuPickerMath.HslImageColor"/> (including the grey-bar branch). Two modes:
/// <list type="bullet">
/// <item><see cref="ForStringCvar"/> — colorpicker_string.qc: write the color to a cvar, either as a
///   space-separated <c>"r g b"</c> string, or (when the cvar name ends in <c>_</c>) to its
///   <c>red</c>/<c>green</c>/<c>blue</c> sub-cvars. Shows the current cvar color's position as a cursor.</item>
/// <item><see cref="ForNameBox"/> — colorpicker.qc: insert a <c>^xRGB</c> hex color code at the caret of a bound
///   name input box (via <see cref="CvarLineEdit.InsertAtCaret"/>).</item>
/// </list>
/// Degrades gracefully without the content repo (no image → a generated HSL gradient stand-in), rather than
/// crashing on a null texture.
/// </summary>
public partial class HslColorPicker : Control
{
    private enum PickerMode { StringCvar, NameBox }

    private static CvarService Cvars => MenuState.Cvars;

    // The skin image margin is SKINMARGIN_COLORPICKER = '0 0 0' in every shipped skin (the whole image is the
    // picker area). Kept as a field so the math reads exactly like the QC (which threads imagemargin through).
    private readonly (float X, float Y) _margin = (0f, 0f);

    private PickerMode _mode;
    private string _cvar = "";            // StringCvar: the controlled cvar
    private CvarLineEdit? _nameBox;       // NameBox: the input box to insert a ^xRGB code into
    private (float X, float Y) _cursor = (0.5f, 0.5f);
    private Texture2D? _img;
    private Texture2D? _selected;
    private ImageTexture? _fallback;

    private HslColorPicker()
    {
        CustomMinimumSize = new Vector2(120, 96);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>colorpicker_string.qc: build a picker bound to a "r g b" (or "_" → red/green/blue) string cvar.</summary>
    public static HslColorPicker ForStringCvar(string cvar)
        => new() { _mode = PickerMode.StringCvar, _cvar = cvar };

    /// <summary>colorpicker.qc: build a picker that inserts a ^xRGB code into the given name box.</summary>
    public static HslColorPicker ForNameBox(CvarLineEdit nameBox)
        => new() { _mode = PickerMode.NameBox, _nameBox = nameBox };

    public override void _EnterTree()
    {
        _img = MenuSkin.SkinImage("colorpicker");
        _selected = MenuSkin.SkinImage("colorpicker_selected");
        Cvars.Changed += OnCvarChanged;
        LoadCursor();
    }

    public override void _ExitTree() => Cvars.Changed -= OnCvarChanged;

    private void OnCvarChanged(string name)
    {
        if (_mode == PickerMode.StringCvar && (name == _cvar || name == _cvar + "red"
            || name == _cvar + "green" || name == _cvar + "blue"))
        {
            LoadCursor();
            QueueRedraw();
        }
    }

    /// <summary>colorpicker_string.qc loadCvars: position the cursor from the current cvar color.</summary>
    private void LoadCursor()
    {
        if (_mode != PickerMode.StringCvar) return;
        (float, float, float) rgb;
        if (_cvar.EndsWith("_"))
            rgb = (Cvars.GetFloat(_cvar + "red"), Cvars.GetFloat(_cvar + "green"), Cvars.GetFloat(_cvar + "blue"));
        else
            rgb = ParseRgb(Cvars.GetString(_cvar));
        _cursor = MenuPickerMath.ColorHslImage(rgb, _margin);
    }

    // ---- input (QC mouseDrag / mousePress) -------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        bool drag = @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };
        bool move = @event is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left };
        if (!drag && !move) return;

        Vector2 local = ((InputEventMouse)@event).Position;
        var coords = (X: local.X / MathF.Max(1f, Size.X), Y: local.Y / MathF.Max(1f, Size.Y));
        // QC mouseDrag: only react inside the margin box.
        if (coords.X < _margin.X || coords.Y < _margin.Y || coords.X > 1 - _margin.X || coords.Y > 1 - _margin.Y)
            return;

        (float R, float G, float B) rgb = MenuPickerMath.HslImageColor(coords, _margin);
        if (_mode == PickerMode.StringCvar)
        {
            _cursor = coords;
            SaveStringCvar(rgb);
        }
        else
        {
            _nameBox?.InsertAtCaret(MenuPickerMath.RgbToHexColor(rgb)); // colorpicker.qc: enterText(^xRGB)
        }
        QueueRedraw();
        AcceptEvent();
    }

    /// <summary>colorpicker_string.qc saveCvars: write either the sub-cvars or the "%v" string.</summary>
    private void SaveStringCvar((float R, float G, float B) rgb)
    {
        if (_cvar.EndsWith("_"))
        {
            Cvars.Set(_cvar + "red", Fmt(rgb.R));
            Cvars.Set(_cvar + "green", Fmt(rgb.G));
            Cvars.Set(_cvar + "blue", Fmt(rgb.B));
            Cvars.MarkArchived(_cvar + "red");
            Cvars.MarkArchived(_cvar + "green");
            Cvars.MarkArchived(_cvar + "blue");
        }
        else
        {
            Cvars.Set(_cvar, $"{Fmt(rgb.R)} {Fmt(rgb.G)} {Fmt(rgb.B)}"); // QC sprintf("%v", ...)
            Cvars.MarkArchived(_cvar);
        }
    }

    // ---- draw (QC draw) --------------------------------------------------------------------------------

    public override void _Draw()
    {
        if (_img is not null)
            DrawTextureRect(_img, new Rect2(Vector2.Zero, Size), false);
        else
            DrawTextureRect(FallbackImage(), new Rect2(Vector2.Zero, Size), false);

        if (_mode == PickerMode.StringCvar)
        {
            Vector2 at = new(_cursor.X * Size.X, _cursor.Y * Size.Y);
            if (_selected is not null)
            {
                Vector2 sz = _selected.GetSize();
                DrawTextureRect(_selected, new Rect2(at - sz * 0.5f, sz), false);
            }
            else
            {
                // A simple ring cursor when the "_selected" sprite is missing.
                DrawArc(at, 6f, 0f, Mathf.Tau, 16, Colors.White, 2f);
            }
        }
    }

    /// <summary>A generated HSL gradient stand-in when the colorpicker image asset isn't in the content repo.</summary>
    private Texture2D FallbackImage()
    {
        if (_fallback is not null) return _fallback;
        const int w = 64, h = 48;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var coords = ((float)x / (w - 1), (float)y / (h - 1));
                (float r, float g, float b) = MenuPickerMath.HslImageColor(coords, _margin);
                img.SetPixel(x, y, new Color(r, g, b));
            }
        _fallback = ImageTexture.CreateFromImage(img);
        return _fallback;
    }

    private static (float, float, float) ParseRgb(string s)
    {
        string[] p = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 3
            && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
            && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
            && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            return (r, g, b);
        return (1f, 1f, 1f);
    }

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
