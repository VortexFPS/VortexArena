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

        // XonoticCharmap_resizeNotify: a square font as big as the cell's smaller side, but never above
        // SKINFONTSIZE_NORMAL; draw_CenterText at charOffset (horizontal centre, vertically centred).
        float font = System.Math.Min(System.Math.Min(rect.Size.X, rect.Size.Y), MenuMetrics.FontNormalU);
        float menuPx = font / MenuMetrics.DesignUnit;
        float w = QcDraw.TextWidth(ch, menuPx);
        Color color = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_CHARMAP_CHAR", 1f, 1f, 1f),
            MenuSkin.SkinNumber("ALPHA_CHARMAP_CHAR", 0.85f)));
        QcDraw.Text(this, rect.Position + new Vector2(0.5f * (rect.Size.X - w), 0.5f * (rect.Size.Y - font)), ch, color, menuPx);
    }

    public override Vector2 _GetMinimumSize() => Vector2.Zero;
}
