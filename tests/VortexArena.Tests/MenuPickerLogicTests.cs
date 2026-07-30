using System;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="MenuPickerMath"/> — the Godot-free port of the crosshair picker cell↔index math, the
/// charmap cell→char lookup, and the HSL colorpicker math. No registry/Api dependency, so no GlobalState.
/// </summary>
public class MenuPickerLogicTests
{
    [Fact]
    public void Crosshair_Cell_Index_Roundtrip_Over_The_3x12_Grid()
    {
        for (int y = 0; y < MenuPickerMath.CrosshairRows; y++)
            for (int x = 0; x < MenuPickerMath.CrosshairColumns; x++)
            {
                var cell = new PickerCell(x, y);
                int index = MenuPickerMath.CellToCrosshairIndex(cell);
                Assert.InRange(index, MenuPickerMath.CrosshairFirstIndex, MenuPickerMath.CrosshairEndIndex - 1);
                Assert.Equal(cell, MenuPickerMath.CrosshairIndexToCell(index));        // inverse
                Assert.Equal(cell, MenuPickerMath.CrosshairIndexToCell(index.ToString())); // string overload
            }

        // Corners: index 31 is (0,0); index 66 is (11,2).
        Assert.Equal(31, MenuPickerMath.CellToCrosshairIndex(new PickerCell(0, 0)));
        Assert.Equal(66, MenuPickerMath.CellToCrosshairIndex(new PickerCell(11, 2)));
    }

    [Fact]
    public void Crosshair_OutOfRange_Maps_To_Invalid_Sentinel()
    {
        // Default crosshair 16 is NOT in the 31..66 picker range → invalid (the picker shows no selection).
        Assert.Equal(PickerCell.Invalid, MenuPickerMath.CrosshairIndexToCell("16"));
        Assert.Equal(PickerCell.Invalid, MenuPickerMath.CrosshairIndexToCell(16));
        // One past the end (67) and below the start (30) are invalid.
        Assert.Equal(PickerCell.Invalid, MenuPickerMath.CrosshairIndexToCell(67));
        Assert.Equal(PickerCell.Invalid, MenuPickerMath.CrosshairIndexToCell(30));
        // A fractional crosshair value is invalid (QC: crosshair - floor(crosshair) > 0).
        Assert.Equal(PickerCell.Invalid, MenuPickerMath.CrosshairIndexToCell("40.5"));
        // Out-of-grid cells return index -1 (QC's "" sentinel).
        Assert.Equal(-1, MenuPickerMath.CellToCrosshairIndex(new PickerCell(12, 0)));
        Assert.Equal(-1, MenuPickerMath.CellToCrosshairIndex(new PickerCell(0, 3)));
        Assert.Equal(-1, MenuPickerMath.CellToCrosshairIndex(PickerCell.Invalid));
    }

    [Fact]
    public void Charmap_Has_Exactly_140_Cells_And_Known_Glyphs()
    {
        Assert.Equal(MenuPickerMath.CharmapRows * MenuPickerMath.CharmapColumns, MenuPickerMath.Charmap.Length);
        Assert.Equal(140, MenuPickerMath.Charmap.Length);

        // Top-left cell = the first glyph (★, U+2605).
        Assert.Equal("★", MenuPickerMath.CharmapCellToChar(new PickerCell(0, 0)));
        // First emoji (row 1, col 0) = 🌍 (U+1F30D) — an astral codepoint, must survive as a 2-char string.
        string globe = MenuPickerMath.CharmapCellToChar(new PickerCell(0, 1));
        Assert.Equal("\U0001F30D", globe);
        Assert.Equal(2, globe.Length); // surrogate pair preserved (the parity gotcha)

        // A Quake private-use glyph (row 5, col 0) = U+E0E1.
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(new PickerCell(0, 5)));
    }

    [Fact]
    public void Charmap_Empty_Cells_And_OutOfRange_Return_Empty()
    {
        // Rows 3 and 4 end with space cells (the QC space sentinel maps to empty).
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(new PickerCell(13, 3)));
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(new PickerCell(12, 4)));
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(new PickerCell(13, 4)));
        // Out of range maps to empty.
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(new PickerCell(14, 0)));
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(new PickerCell(0, 10)));
        Assert.Equal("", MenuPickerMath.CharmapCellToChar(PickerCell.Invalid));
    }

    [Fact]
    public void Hsl_Color_Roundtrip_Saturated_And_Grey()
    {
        var margin = (0.05f, 0.05f);

        // Saturated region: several positions below the grey-bar threshold.
        foreach ((float, float) pos in new[] { (0.2f, 0.3f), (0.5f, 0.5f), (0.8f, 0.7f), (0.35f, 0.6f) })
        {
            (float r, float g, float b) = MenuPickerMath.HslImageColor(pos, margin);
            (float x, float y) = MenuPickerMath.ColorHslImage((r, g, b), margin);
            Assert.True(MathF.Abs(x - pos.Item1) < 1e-3f, $"x {x} vs {pos.Item1}");
            Assert.True(MathF.Abs(y - pos.Item2) < 1e-3f, $"y {y} vs {pos.Item2}");
        }

        // Grey bar (v.y > 0.875): saturation collapses to 0, so the inverse lands back on the grey bar row
        // (y = 0.875 + 0.07 in normalized coords), and the x (lightness) round-trips.
        var greyPos = (0.4f, 0.95f);
        (float gr, float gg, float gb) = MenuPickerMath.HslImageColor(greyPos, margin);
        Assert.True(MathF.Abs(gr - gg) < 1e-4f && MathF.Abs(gg - gb) < 1e-4f, "grey bar yields a grey color");
        (float gx, float gy) = MenuPickerMath.ColorHslImage((gr, gg, gb), margin);
        Assert.True(MathF.Abs(gx - greyPos.Item1) < 1e-3f, $"grey x {gx} vs {greyPos.Item1}");
        // The grey-bar y is fixed at (0.875+0.07) inside the margin box.
        float expectedGreyY = margin.Item2 + (0.875f + 0.07f) * (1 - 2 * margin.Item2);
        Assert.True(MathF.Abs(gy - expectedGreyY) < 1e-3f, $"grey y {gy} vs {expectedGreyY}");
    }

    [Fact]
    public void RgbToHexColor_Matches_QC_Quantization()
    {
        // QC rgb_to_hexcolor: each component floor(c*15 + 0.5) → hex digit over "0123456789abcdef".
        Assert.Equal("^xfff", MenuPickerMath.RgbToHexColor((1f, 1f, 1f)));   // 15,15,15
        Assert.Equal("^x000", MenuPickerMath.RgbToHexColor((0f, 0f, 0f)));   // 0,0,0
        Assert.Equal("^xf00", MenuPickerMath.RgbToHexColor((1f, 0f, 0f)));   // red
        // 0.5 → floor(7.5+0.5)=8
        Assert.Equal("^x888", MenuPickerMath.RgbToHexColor((0.5f, 0.5f, 0.5f)));
        // clamps out-of-range
        Assert.Equal("^xf00", MenuPickerMath.RgbToHexColor((2f, -1f, 0f)));
    }
}
