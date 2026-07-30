// Port of qcsrc/menu/xonotic/charmap.qc (XonoticCharmap).
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// The character map — a faithful C# port of <c>XonoticCharmap</c> (qcsrc/menu/xonotic/charmap.qc): a 10×14
/// grid of glyphs (the fixed 140-cell <see cref="MenuPickerMath.Charmap"/> string) that, when a cell is picked,
/// inserts that glyph into a bound name input box (<see cref="CvarLineEdit.InsertAtCaret"/>). Empty (space)
/// cells are not pickable (QC cellIsValid). The <c>\uE0xx</c> Quake-font private-use glyphs and the emoji only
/// render with a font that carries them (the Xolonium menu font / engine atlas); on a font without them they
/// show as tofu — an acceptable fallback, but the cell→char mapping itself is exact.
/// </summary>
public partial class CharmapPicker : PickerGrid
{
    private readonly CvarLineEdit _inputBox;

    public CharmapPicker(CvarLineEdit inputBox)
    {
        _inputBox = inputBox;
        Rows = MenuPickerMath.CharmapRows;
        Columns = MenuPickerMath.CharmapColumns;
        CustomMinimumSize = new Vector2(0, 140);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    protected override bool CellIsValid(PickerCell cell)
        => MenuPickerMath.CharmapCellToChar(cell) != "";

    protected override void OnCellSelect(PickerCell cell)
    {
        string ch = MenuPickerMath.CharmapCellToChar(cell);
        if (ch == "") return;
        SetSelected(cell);
        _inputBox.InsertAtCaret(ch); // QC: inputBox.enterText(character)
    }

    protected override void DrawCell(PickerCell cell, Rect2 rect)
    {
        string ch = MenuPickerMath.CharmapCellToChar(cell);
        if (ch == "") return;

        Font font = MenuSkin.BoldFont ?? ThemeDB.FallbackFont;
        int fontSize = Mathf.Max(8, (int)(rect.Size.Y * 0.7f));
        // Center the glyph in the cell (QC draw_CenterText at the char offset).
        Vector2 textSize = font.GetStringSize(ch, HorizontalAlignment.Left, -1, fontSize);
        Vector2 at = rect.Position + (rect.Size - textSize) * 0.5f + new Vector2(0, font.GetAscent(fontSize));
        DrawString(font, at, ch, HorizontalAlignment.Left, -1, fontSize, MenuSkin.Text);
    }
}
