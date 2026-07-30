// Port of qcsrc/menu/xonotic/picker.qc (XonoticPicker) — the base for the crosshair picker and the charmap.
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// The clickable rows×columns grid the crosshair picker and the charmap are built on — a faithful C# port of
/// <c>XonoticPicker</c> (qcsrc/menu/xonotic/picker.qc). Tracks a hovered (focused) cell and a selected cell,
/// gates cells through <see cref="CellIsValid"/>, draws a selection/focus fill behind the focused/selected cell
/// using the skin's listbox colors, and lets subclasses paint each cell (<see cref="DrawCell"/>) and react to a
/// pick (<see cref="OnCellSelect"/>). Mouse: hover focuses, press+release on the same cell selects (QC
/// mousePress/mouseRelease pressedCell). Keys: arrows move focus (wrapping, skipping invalid cells, QC
/// moveFocus), Enter/Insert select (QC keyDown).
/// </summary>
public abstract partial class PickerGrid : Control
{
    /// <summary>Grid dimensions — subclass sets these in its ctor (QC me.rows / me.columns).</summary>
    protected int Rows { get; init; }
    protected int Columns { get; init; }

    private PickerCell _focused = PickerCell.Invalid;
    private PickerCell _selected = PickerCell.Invalid;

    protected PickerCell Selected => _selected;

    protected PickerGrid()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
    }

    // ---- subclass hooks (QC virtuals) --------------------------------------------------------------------

    /// <summary>QC <c>cellIsValid</c>: is this cell pickable (in the picker base, always true)?</summary>
    protected virtual bool CellIsValid(PickerCell cell) => true;

    /// <summary>QC <c>cellDraw(cell, cellPos)</c>: paint cell content inside <paramref name="rect"/> (px).</summary>
    protected abstract void DrawCell(PickerCell cell, Rect2 rect);

    /// <summary>QC <c>cellSelect</c>: the user picked <paramref name="cell"/>. Base records it as selected.</summary>
    protected virtual void OnCellSelect(PickerCell cell) => _selected = cell;

    /// <summary>Set the selected cell without firing <see cref="OnCellSelect"/> (QC SUPER.cellSelect on init).</summary>
    protected void SetSelected(PickerCell cell) => _selected = cell;

    // ---- geometry --------------------------------------------------------------------------------------

    /// <summary>The pixel size of one cell (QC realCellSize × box size).</summary>
    private Vector2 CellSize => new(Size.X / Mathf.Max(1, Columns), Size.Y / Mathf.Max(1, Rows));

    /// <summary>The top-left pixel of a cell.</summary>
    protected Rect2 CellRect(PickerCell cell)
    {
        Vector2 cs = CellSize;
        return new Rect2(cell.X * cs.X, cell.Y * cs.Y, cs.X, cs.Y);
    }

    private PickerCell CellAt(Vector2 pos)
    {
        Vector2 cs = CellSize;
        if (cs.X <= 0 || cs.Y <= 0) return PickerCell.Invalid;
        int cx = (int)Mathf.Floor(pos.X / cs.X);
        int cy = (int)Mathf.Floor(pos.Y / cs.Y);
        if (cx < 0 || cy < 0 || cx >= Columns || cy >= Rows) return PickerCell.Invalid;
        return new PickerCell(cx, cy);
    }

    // ---- input (QC mouseMove / mousePress / mouseRelease / keyDown) --------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion mm:
                _focused = CellAt(mm.Position);
                QueueRedraw();
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb:
                GrabFocus();
                PickerCell hit = CellAt(mb.Position);
                if (hit.IsValid && CellIsValid(hit))
                {
                    OnCellSelect(hit);     // QC: press+release on the same cell; we select on press (simpler, same result)
                    QueueRedraw();
                    AcceptEvent();
                }
                break;

            case InputEventKey { Pressed: true, Echo: false } k:
                if (HandleKey(k.Keycode))
                {
                    QueueRedraw();
                    AcceptEvent();
                }
                break;
        }
    }

    private bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Left:  MoveFocus(-1, 0); return true;
            case Key.Right: MoveFocus(1, 0); return true;
            case Key.Up:    MoveFocus(0, -1); return true;
            case Key.Down:  MoveFocus(0, 1); return true;
            case Key.Home:  _focused = new PickerCell(Columns - 1, 0); MoveFocus(1, 0); return true;
            case Key.End:   _focused = new PickerCell(0, Rows - 1); MoveFocus(-1, 0); return true;
            case Key.Enter:
            case Key.KpEnter:
            case Key.Insert:
                if (_focused.IsValid && CellIsValid(_focused)) OnCellSelect(_focused);
                return true;
        }
        return false;
    }

    /// <summary>QC <c>moveFocus</c>: step + wrap mod columns/rows, recurse over invalid cells (break on the initial cell).</summary>
    private void MoveFocus(int dx, int dy)
    {
        PickerCell initial = _focused.IsValid ? _focused : new PickerCell(0, 0);
        PickerCell cur = initial;
        for (int guard = 0; guard < Rows * Columns + 1; guard++)
        {
            int x = Mod(cur.X + dx, Columns);
            int y = Mod(cur.Y + dy, Rows);
            cur = new PickerCell(x, y);
            if (cur == initial) break;     // wrapped all the way around (recursion break)
            if (CellIsValid(cur)) break;   // landed on a valid cell
        }
        _focused = cur;
    }

    private static int Mod(int a, int m) => m <= 0 ? 0 : ((a % m) + m) % m;

    // ---- draw (QC draw) --------------------------------------------------------------------------------

    public override void _Draw()
    {
        bool focused = HasFocus();
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                var cell = new PickerCell(x, y);
                if (!CellIsValid(cell)) continue;
                Rect2 rect = CellRect(cell);

                if (cell == _selected)
                    DrawRect(rect, new Color(MenuSkin.Selection, 0.85f)); // SKINCOLOR_LISTBOX_SELECTED fill
                else if (cell == _focused && focused)
                    DrawRect(rect, new Color(MenuSkin.Accent, 0.30f));    // SKINCOLOR_LISTBOX_FOCUSED fill

                DrawCell(cell, rect);
            }
    }
}
