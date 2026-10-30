using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The QC dialog item grid — item/dialog.qc <c>configureDialog</c> + <c>addItemSimple</c> / <c>gotoRC</c> / <c>TR</c> /
/// <c>TD</c> / <c>TDNoMargin</c> / <c>TDempty</c> / <c>setFirstColumn</c> — as a Godot <see cref="Container"/>.
///
/// Every Xonotic dialog and tab places its widgets on a grid of <c>rows</c> x <c>columns</c> cells: a row is
/// SKINFONTSIZE_NORMAL * SKINHEIGHT_NORMAL menu px high with SKINMARGIN_ROWS between rows, and the columns
/// share the width left after the margins and SKINMARGIN_COLUMNS gaps. Positions and spans are fractional
/// (<c>gotoRC(0.5, 0)</c>, <c>TD(1, 0.8, e)</c>), so a dialog's layout is its list of TD calls. This container
/// takes that same list and places each child exactly where the QC would, which is what makes a ported dialog
/// line up with Xonotic's to the pixel instead of approximately (as an HBox/VBox reflow does).
///
/// <para>The arithmetic is the QC's own, fractions of the box included: the cell size is computed against
/// <see cref="IntendedWidth"/> * conwidth (when set) and the height the rows imply, then applied to the box the
/// grid actually has. For a tab that differs from its absolute width (the Servers tab is <c>intendedWidth
/// 0.9</c> inside a 0.96 dialog), and the QC's column gaps scale accordingly; reproducing that is what keeps
/// the columns where Xonotic draws them.</para>
///
/// <para>Metrics are in QC menu px; <see cref="MenuMetrics.DesignUnit"/> converts. The grid's own box should be
/// the dialog's item area: for a top-level dialog that is the nexposee's content holder (already inset by the
/// title bar and margins), so the margins here default to 0 — a tab's, too (xonotic/tab.qh).</para>
/// </summary>
public partial class QcGrid : Container
{
    private readonly struct Cell
    {
        public readonly Control Item;
        public readonly float Row, Column, RowSpan, ColumnSpan;
        public readonly Vector2 NoMargin;

        public Cell(Control item, float row, float column, float rowSpan, float columnSpan, Vector2 noMargin)
        {
            Item = item; Row = row; Column = column; RowSpan = rowSpan; ColumnSpan = columnSpan; NoMargin = noMargin;
        }
    }

    private readonly List<Cell> _cells = new();
    private float _currentRow = -1, _currentColumn = -1, _firstColumn;

    /// <summary>QC <c>rows</c>.</summary>
    public float Rows { get; set; }
    /// <summary>QC <c>columns</c>.</summary>
    public float Columns { get; set; }
    /// <summary>QC <c>intendedWidth</c> (a fraction of the menu's conwidth); 0 = the box's own width.</summary>
    public float IntendedWidth { get; set; }

    // Menu px, as the QC attributes (xonotic/dialog.qh, xonotic/tab.qh).
    public float MarginTop { get; set; }
    public float MarginBottom { get; set; }
    public float MarginLeft { get; set; }
    public float MarginRight { get; set; }
    public float ColumnSpacing { get; set; } = MenuMetrics.ColumnSpacing;
    public float RowSpacing { get; set; } = MenuMetrics.RowSpacing;
    public float RowHeight { get; set; } = MenuMetrics.RowHeight;

    public QcGrid(float rows, float columns, float intendedWidth = 0f)
    {
        Rows = rows;
        Columns = columns;
        IntendedWidth = intendedWidth;
        MouseFilter = MouseFilterEnum.Pass;
    }

    /// <summary>The grid's height in menu px for its rows (configureDialog's absHeight, no title bar).</summary>
    public float AbsHeight => MarginTop + Rows * RowHeight + (Rows - 1) * RowSpacing + MarginBottom;

    public float CurrentRow => _currentRow;
    public float CurrentColumn => _currentColumn;

    /// <summary>QC <c>gotoRC</c>.</summary>
    public void GotoRC(float row, float column)
    {
        _currentRow = row;
        _currentColumn = column;
    }

    /// <summary>QC <c>TR</c>: the next row, back to the first column.</summary>
    public void TR()
    {
        _currentRow++;
        _currentColumn = _firstColumn;
    }

    /// <summary>QC <c>TD</c>: place <paramref name="item"/> at the current cell, then advance by its span.</summary>
    public T TD<T>(float rowSpan, float columnSpan, T item) where T : Control
        => TDNoMargin(rowSpan, columnSpan, item, Vector2.Zero);

    /// <summary>QC <c>TDNoMargin</c>: as <see cref="TD{T}"/>, but growing into the spacing on the axes set in
    /// <paramref name="noMargin"/> (1 = take the full gap on both sides).</summary>
    public T TDNoMargin<T>(float rowSpan, float columnSpan, T item, Vector2 noMargin) where T : Control
    {
        _cells.Add(new Cell(item, _currentRow, _currentColumn, rowSpan, columnSpan, noMargin));
        if (item.GetParent() is null)
            AddChild(item);
        _currentColumn += columnSpan;
        QueueSort();
        return item;
    }

    /// <summary>QC <c>TDempty</c>: skip <paramref name="columnSpan"/> columns.</summary>
    public void TDempty(float columnSpan) => _currentColumn += columnSpan;

    /// <summary>QC <c>setFirstColumn</c>: where <see cref="TR"/> returns to.</summary>
    public void SetFirstColumn(float column) => _firstColumn = column;

    /// <summary>The rectangle, in this grid's coordinates, the QC gives a cell — for children that position
    /// themselves relative to another one (the server list's sort buttons).</summary>
    public Rect2 CellRect(float row, float column, float rowSpan, float columnSpan, Vector2 noMargin = default)
    {
        Vector2 size = Size;
        float absW = IntendedWidth > 0f ? IntendedWidth * MenuMetrics.ConWidth : size.X / MenuMetrics.DesignUnit;
        float absH = AbsHeight;
        if (absW <= 0f || absH <= 0f)
            return new Rect2();

        // configureDialog: itemOrigin / itemSize / itemSpacing as fractions of the dialog's box.
        var origin = new Vector2(MarginLeft / absW, MarginTop / absH);
        var itemSize = new Vector2(
            (1f - (MarginLeft + MarginRight + ColumnSpacing * (Columns - 1f)) / absW) / Columns,
            RowHeight / absH);
        var spacing = itemSize + new Vector2(ColumnSpacing / absW, RowSpacing / absH);

        // addItemSimple.
        Vector2 o = origin + new Vector2(column * spacing.X, row * spacing.Y);
        Vector2 s = itemSize + new Vector2((columnSpan - 1f) * spacing.X, (rowSpan - 1f) * spacing.Y);
        o.X -= 0.5f * (spacing.X - itemSize.X) * noMargin.X;
        s.X += (spacing.X - itemSize.X) * noMargin.X;
        o.Y -= 0.5f * (spacing.Y - itemSize.Y) * noMargin.Y;
        s.Y += (spacing.Y - itemSize.Y) * noMargin.Y;

        return new Rect2(o * size, s * size);
    }

    public override void _Notification(int what)
    {
        if (what != NotificationSortChildren)
            return;
        foreach (Cell c in _cells)
        {
            if (!IsInstanceValid(c.Item) || c.Item.GetParent() != this)
                continue;
            Rect2 r = CellRect(c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.NoMargin);
            // Not FitChildInRect: that grows a child to its minimum size, and a QC item is exactly its cell
            // whatever its contents want (a label too wide for it is condensed, not given more room).
            c.Item.Position = r.Position;
            c.Item.Size = r.Size;
        }
        Laid?.Invoke();
    }

    /// <summary>Raised after every layout pass, for children placed relative to the cells (see <see cref="CellRect"/>).</summary>
    public event System.Action? Laid;

    // The grid never asks for room: it is given its box by whoever hosts the dialog.
    public override Vector2 _GetMinimumSize() => Vector2.Zero;
}
