// Port of qcsrc/menu/xonotic/crosshairpicker.qc (cellToCrosshair/crosshairToCell),
// qcsrc/menu/xonotic/charmap.qc (CHARMAP + cellToChar), and qcsrc/menu/xonotic/colorpicker.qc +
// qcsrc/lib/color.qh (hslimage_color/color_hslimage/hsl_to_rgb/rgb_to_hsl/rgb_to_hexcolor).
//
// The Godot-free home for the pure picker + HSL math so the menu picker widgets
// (game/menu/framework/{CrosshairPicker,CharmapPicker,HslColorPicker}.cs) and the unit tests (which cannot
// reference game/) share ONE implementation. No Godot types here — plain math over (col,row) cells and
// 0..1 RGB triplets.
using System;

namespace XonoticGodot.Common.Gameplay;

/// <summary>An integer grid cell (column = x, row = y). <c>(-1,-1)</c> is the QC <c>'-1 -1 0'</c> invalid sentinel.</summary>
public readonly record struct PickerCell(int X, int Y)
{
    /// <summary>QC <c>'-1 -1 0'</c>: the out-of-range / no-selection sentinel.</summary>
    public static readonly PickerCell Invalid = new(-1, -1);

    public bool IsValid => X >= 0 && Y >= 0;
}

/// <summary>
/// Pure math for the crosshair picker, charmap, and HSL colorpicker — faithful ports of the QuakeC widget
/// helpers, factored out of the Godot controls so they're unit-testable.
/// </summary>
public static class MenuPickerMath
{
    // ===========================================================================================================
    //  Crosshair picker — crosshairpicker.qc (rows = 3, columns = 12 → cells map to crosshair indices 31..66)
    // ===========================================================================================================

    public const int CrosshairRows = 3;
    public const int CrosshairColumns = 12;

    /// <summary>The first crosshair index the picker addresses (QC <c>31 + ...</c>).</summary>
    public const int CrosshairFirstIndex = 31;

    /// <summary>One past the last crosshair index (31 + 3*12 = 67, i.e. indices 31..66 inclusive).</summary>
    public const int CrosshairEndIndex = CrosshairFirstIndex + CrosshairRows * CrosshairColumns;

    /// <summary>
    /// Port of <c>crosshairpicker_cellToCrosshair</c> (crosshairpicker.qc:3): the crosshair INDEX for a cell, or
    /// <c>-1</c> if the cell is out of the 3×12 grid (QC returns "" there — callers treat that as invalid).
    /// </summary>
    public static int CellToCrosshairIndex(PickerCell cell)
    {
        if (cell.X < 0 || cell.X >= CrosshairColumns || cell.Y < 0 || cell.Y >= CrosshairRows)
            return -1;
        return CrosshairFirstIndex + cell.Y * CrosshairColumns + cell.X;
    }

    /// <summary>
    /// Port of <c>crosshairpicker_crosshairToCell</c> (crosshairpicker.qc:10): the cell for a crosshair-index
    /// string. Returns <see cref="PickerCell.Invalid"/> when the value is non-integer OR outside the picker's
    /// 31..66 range. (QC's crosshairToCell only rejects the non-integer case explicitly; an out-of-range index
    /// like the default 16 yields a cell that never matches a valid cell during draw, so the picker shows no
    /// selection — we collapse both to the explicit invalid sentinel, which the widget renders as "no selection",
    /// matching the on-screen result. The recon pins this: don't clamp 16 into the grid.)
    /// </summary>
    public static PickerCell CrosshairIndexToCell(string crosshairStr)
    {
        // QC: float crosshair = stof(crosshair_str) - 31; if (crosshair - floor(crosshair) > 0) return invalid;
        if (!float.TryParse(crosshairStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float raw))
            return PickerCell.Invalid;
        if (raw != MathF.Floor(raw)) // non-integer crosshair → invalid (QC: crosshair - floor(crosshair) > 0)
            return PickerCell.Invalid;
        int index = (int)raw;
        return CrosshairIndexToCell(index);
    }

    /// <summary>Integer-index overload of <see cref="CrosshairIndexToCell(string)"/>.</summary>
    public static PickerCell CrosshairIndexToCell(int index)
    {
        if (index < CrosshairFirstIndex || index >= CrosshairEndIndex)
            return PickerCell.Invalid;
        int c = index - CrosshairFirstIndex; // 0..35
        return new PickerCell(c % CrosshairColumns, c / CrosshairColumns);
    }

    // ===========================================================================================================
    //  Charmap — charmap.qc (rows = 10, columns = 14 → 140 cells)
    // ===========================================================================================================

    public const int CharmapRows = 10;
    public const int CharmapColumns = 14;

    /// <summary>
    /// The 140-cell character map from charmap.qc, one entry per cell in row-major order (QC indexes
    /// <c>substring(CHARMAP, cell.y*columns + cell.x, 1)</c> — a per-CODEPOINT lookup). Stored as an array of
    /// single-glyph strings rather than one C# string because 32 of the cells are astral (emoji) codepoints
    /// (U+1F30D etc.): a single C# string would index them as surrogate-pair halves, breaking the per-cell
    /// mapping. <c>" "</c> marks an empty cell (QC's space → "" / invalid). The <c>\uE0xx</c> entries are the
    /// Quake-font private-use glyphs (rendered only by a font that carries them; tofu otherwise — acceptable).
    /// </summary>
    public static readonly string[] Charmap =
    {
        "★", "◆", "■", "▮", "▰", "▬", "◣", "◤", "◥", "◢", "◀", "▲", "▶", "▼",
        "\U0001F30D", "\U0001F30E", "\U0001F30F", "\U0001F680", "\U0001F30C", "\U0001F47D", "\U0001F52B", "⌖", "❇", "❈", "←", "↑", "→", "↓",
        "☠", "☣", "☢", "⚛", "⚡", "⚙", "\U0001F525", "❌", "⚠", "⛔", "❰", "❱", "❲", "❳",
        "\U0001F603", "\U0001F60A", "\U0001F601", "\U0001F604", "\U0001F606", "\U0001F60E", "\U0001F608", "\U0001F607", "\U0001F609", "\U0001F61B", "\U0001F61D", "\U0001F618", "❤", " ",
        "\U0001F610", "\U0001F612", "\U0001F615", "\U0001F62E", "\U0001F632", "\U0001F61E", "\U0001F61F", "\U0001F620", "\U0001F623", "\U0001F62D", "\U0001F635", "\U0001F634", " ", " ",
        "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "", "", "", "", "",
    };

    /// <summary>
    /// Port of <c>charmap_cellToChar</c> (charmap.qc:22): the glyph for a cell, or <c>""</c> when the cell is out
    /// of range or holds the space sentinel (an empty cell). Indexes <see cref="Charmap"/> by
    /// <c>cell.y*columns + cell.x</c>.
    /// </summary>
    public static string CharmapCellToChar(PickerCell cell)
    {
        if (cell.X < 0 || cell.X >= CharmapColumns || cell.Y < 0 || cell.Y >= CharmapRows)
            return "";
        string s = Charmap[cell.Y * CharmapColumns + cell.X];
        return s != " " ? s : "";
    }

    // ===========================================================================================================
    //  HSL colorpicker — colorpicker.qc (hslimage_color/color_hslimage) + lib/color.qh (rgb<->hsl, hexcolor)
    // ===========================================================================================================

    /// <summary>
    /// Port of <c>hslimage_color</c> (colorpicker.qc:24): map a normalized image position (inside the margin box)
    /// to an RGB triplet. Above the <c>v.y &gt; 0.875</c> threshold is the grey bar; below is the saturated HSL
    /// rectangle (hue along x, lightness along y). <paramref name="v"/> and <paramref name="margin"/> are
    /// (x, y) in 0..1 image coords.
    /// </summary>
    public static (float R, float G, float B) HslImageColor((float X, float Y) v, (float X, float Y) margin)
    {
        float x = (v.X - margin.X) / (1 - 2 * margin.X);
        float y = (v.Y - margin.Y) / (1 - 2 * margin.Y);

        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x > 1) x = 1;
        if (y > 1) y = 1;

        if (y > 0.875f) // grey bar: hsl_to_rgb(eZ * x) → lightness = x, saturation/hue = 0
            return HslToRgb(0f, 0f, x);
        // hsl_to_rgb(x*6 * eX + eY + y/0.875 * eZ) → hue = x*6, saturation = 1, lightness = y/0.875
        return HslToRgb(x * 6f, 1f, y / 0.875f);
    }

    /// <summary>
    /// Port of <c>color_hslimage</c> (colorpicker.qc:40): the inverse — the normalized image position for an RGB
    /// triplet. Greyscale colors (saturation 0) land on the grey bar (<c>y = 0.875 + 0.07</c>).
    /// </summary>
    public static (float X, float Y) ColorHslImage((float R, float G, float B) rgb, (float X, float Y) margin)
    {
        (float h, float s, float l) = RgbToHsl(rgb.R, rgb.G, rgb.B);
        float px, py;
        if (s != 0f)
        {
            px = h / 6f;
            py = l * 0.875f;
        }
        else // grey scale
        {
            px = l;
            py = 0.875f + 0.07f;
        }
        px = margin.X + px * (1 - 2 * margin.X);
        py = margin.Y + py * (1 - 2 * margin.Y);
        return (px, py);
    }

    /// <summary>
    /// Port of <c>rgb_to_hexcolor</c> (color.qh:177): an RGB triplet → the DarkPlaces <c>^xRGB</c> 3-hex-digit
    /// color code (each component quantized to one of 16 levels). Inserted into the name box by the name
    /// colorpicker.
    /// </summary>
    public static string RgbToHexColor((float R, float G, float B) rgb)
    {
        return "^x"
            + HexDigit((int)MathF.Floor(Clamp01(rgb.R) * 15 + 0.5f))
            + HexDigit((int)MathF.Floor(Clamp01(rgb.G) * 15 + 0.5f))
            + HexDigit((int)MathF.Floor(Clamp01(rgb.B) * 15 + 0.5f));
    }

    // ---- color.qh hsl/hue helpers (faithful ports) -----------------------------------------------------------

    /// <summary>Port of <c>rgb_mi_ma_to_hue</c> (color.qh:46).</summary>
    private static float RgbMiMaToHue(float r, float g, float b, float mi, float ma)
    {
        if (mi == ma)
            return 0;
        if (ma == r)
            return g >= b ? (g - b) / (ma - mi) : (g - b) / (ma - mi) + 6;
        if (ma == g)
            return (b - r) / (ma - mi) + 2;
        return (r - g) / (ma - mi) + 4; // ma == b
    }

    /// <summary>Port of <c>hue_mi_ma_to_rgb</c> (color.qh:64).</summary>
    private static (float R, float G, float B) HueMiMaToRgb(float hue, float mi, float ma)
    {
        hue -= 6 * MathF.Floor(hue / 6);
        if (hue <= 1) return (ma, hue * (ma - mi) + mi, mi);
        if (hue <= 2) return ((2 - hue) * (ma - mi) + mi, ma, mi);
        if (hue <= 3) return (mi, ma, (hue - 2) * (ma - mi) + mi);
        if (hue <= 4) return (mi, (4 - hue) * (ma - mi) + mi, ma);
        if (hue <= 5) return ((hue - 4) * (ma - mi) + mi, mi, ma);
        return (ma, mi, (6 - hue) * (ma - mi) + mi);
    }

    /// <summary>Port of <c>rgb_to_hsl</c> (color.qh:142). Returns (hue 0..6, saturation 0..1, lightness 0..1).</summary>
    public static (float H, float S, float L) RgbToHsl(float r, float g, float b)
    {
        float mi = MathF.Min(r, MathF.Min(g, b));
        float ma = MathF.Max(r, MathF.Max(g, b));
        float h = RgbMiMaToHue(r, g, b, mi, ma);
        float l = 0.5f * (mi + ma);
        float s;
        if (mi == ma)
            s = 0;
        else if (l <= 0.5f)
            s = (ma - mi) / (2 * l);
        else
            s = (ma - mi) / (2 - 2 * l);
        return (h, s, l);
    }

    /// <summary>Port of <c>hsl_to_rgb</c> (color.qh:162). (hue 0..6, saturation 0..1, lightness 0..1) → RGB.</summary>
    public static (float R, float G, float B) HslToRgb(float h, float s, float l)
    {
        float maminusmi = (l <= 0.5f) ? s * 2 * l : s * (2 - 2 * l);
        float mi = l - 0.5f * maminusmi;
        float ma = l + 0.5f * maminusmi;
        return HueMiMaToRgb(h, mi, ma);
    }

    // ---- small helpers ----------------------------------------------------------------------------------------

    private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    /// <summary>QC <c>DEC_TO_HEXDIGIT</c> over HEXDIGITS_MINSET ("0123456789abcdef").</summary>
    private static char HexDigit(int d) => "0123456789abcdef"[d < 0 ? 0 : (d > 15 ? 15 : d)];
}
