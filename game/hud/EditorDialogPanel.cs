using System;
using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The map editor's dialog surface (design doc §11.9): the entity palette, the key inspector, the shader
/// browser and the patch dialogs, all through ONE panel.
///
/// Shared rather than one panel per dialog, because they are the same two shapes underneath. A BROWSER is a
/// grouped list plus a description of whatever is highlighted plus a confirm; a PROPERTY GRID is a list of
/// rows you edit in place. Three bespoke dialogs would be three times the input handling, three sets of
/// keyboard conventions to remember, and three places for them to drift apart.
///
/// Driven the same way the context menu is: rows carry console COMMANDS, so every dialog action is scriptable
/// and the dialog never becomes a second, private half of the editor's API. Where a row needs a value the
/// mapper types, the command carries a <c>%v</c> placeholder that the typed text is substituted into.
///
/// Input follows the menu's conventions so there is one thing to learn: arrows and the mouse move, Enter
/// confirms, Esc closes, and typing filters (in a browser) or edits (in a property grid).
/// </summary>
public partial class EditorDialogPanel : HudPanel
{
    /// <summary>Which of the two shapes this dialog is.</summary>
    public enum DialogKind
    {
        /// <summary>Grouped list + description + confirm. The palette, the shader browser, patch create.</summary>
        Browser,

        /// <summary>Rows edited in place. The entity key inspector.</summary>
        Properties,
    }

    /// <summary>One selectable row.</summary>
    public sealed class DialogRow
    {
        /// <summary>Left-hand text: the item name, or the key name in a property grid.</summary>
        public string Label = "";

        /// <summary>Right-hand text: the current value, or a type hint.</summary>
        public string Value = "";

        /// <summary>Longer text shown in the detail pane when this row is highlighted.</summary>
        public string Detail = "";

        /// <summary>Group heading this row sits under ("" for ungrouped).</summary>
        public string Group = "";

        /// <summary>
        /// Console command fired on confirm. A <c>%v</c> placeholder is replaced with the typed value, which
        /// is what lets one row template serve every key in a property grid.
        /// </summary>
        public string Command = "";

        /// <summary>True when confirming this row should prompt for a value rather than fire immediately.</summary>
        public bool Editable;
    }

    // ---- state ----
    private readonly List<DialogRow> _rows = new();
    private readonly List<int> _visible = new();     // indices into _rows after filtering
    private bool _open;
    private DialogKind _kind = DialogKind.Browser;
    private string _title = "";
    private string _footer = "";
    private int _cursor;
    private int _scroll;
    private string _filter = "";
    private bool _editing;
    private string _edit = "";

    /// <summary>Where confirmed commands go — the shared console interpreter.</summary>
    public Action<string>? CommandSink { get; set; }

    /// <summary>True once the host confirms an editor session; silent until then.</summary>
    public bool IsEditorSession { get; set; }

    /// <summary>Open state, read by the host's cursor-ownership gate.</summary>
    public bool IsOpen => _open;

    /// <summary>Rows drawn at once before scrolling.</summary>
    private const int PageRows = 14;

    public override bool IsDynamic => true;

    // =====================================================================================
    //  Lifecycle
    // =====================================================================================

    /// <summary>
    /// Open a dialog. <paramref name="rows"/> is taken as-is; callers build it from whatever data source the
    /// dialog is about (entities.ent, the shader set, the selected entity's keys).
    /// </summary>
    public void Open(DialogKind kind, string title, IEnumerable<DialogRow> rows, string footer = "")
    {
        ArgumentNullException.ThrowIfNull(rows);

        _kind = kind;
        _title = title ?? "";
        _footer = footer ?? "";
        _rows.Clear();
        _rows.AddRange(rows);
        _filter = "";
        _cursor = 0;
        _scroll = 0;
        _editing = false;
        _edit = "";
        Rebuild();

        if (_rows.Count == 0)
        {
            XonoticGodot.Common.Diagnostics.Log.Info($"{_title}: nothing to show");
            return;
        }

        _open = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        GrabFocus();
        QueueRedraw();
    }

    /// <summary>Close and hand input back to the editor.</summary>
    public void Close()
    {
        _open = false;
        _rows.Clear();
        _visible.Clear();
        _editing = false;
        MouseFilter = MouseFilterEnum.Ignore;
        if (HasFocus())
            ReleaseFocus();
        FocusMode = FocusModeEnum.None;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        bool show = IsEditorSession && _open && ShowModeCvar() != 0;
        if (show != Visible)
            Visible = show;
        if (show)
            QueueRedraw();
    }

    /// <summary>Re-apply the filter and clamp the cursor into whatever survived it.</summary>
    private void Rebuild()
    {
        _visible.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_filter.Length == 0
                || _rows[i].Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || _rows[i].Group.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                _visible.Add(i);
        }

        if (_cursor >= _visible.Count)
            _cursor = Math.Max(0, _visible.Count - 1);
        ClampScroll();
    }

    private void ClampScroll()
    {
        if (_cursor < _scroll)
            _scroll = _cursor;
        else if (_cursor >= _scroll + PageRows)
            _scroll = _cursor - PageRows + 1;
        _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, _visible.Count - PageRows)));
    }

    private DialogRow? Current
        => _cursor >= 0 && _cursor < _visible.Count ? _rows[_visible[_cursor]] : null;

    // =====================================================================================
    //  Input
    // =====================================================================================

    public override void _GuiInput(InputEvent @event)
    {
        if (!_open)
            return;

        if (@event is InputEventMouseButton { Pressed: true } mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Left:
                {
                    int row = RowAt(mb.Position);
                    if (row >= 0)
                    {
                        // A click moves the cursor; a click on the ALREADY selected row confirms it. That makes
                        // a double-click work without needing double-click timing.
                        if (row == _cursor)
                            Confirm();
                        else
                            _cursor = row;
                        ClampScroll();
                    }
                    AcceptEvent();
                    return;
                }
                case MouseButton.WheelUp:
                    _scroll = Math.Max(0, _scroll - 3);
                    AcceptEvent();
                    return;
                case MouseButton.WheelDown:
                    _scroll = Math.Min(Math.Max(0, _visible.Count - PageRows), _scroll + 3);
                    AcceptEvent();
                    return;
            }
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        // While typing a value, the keyboard belongs to the edit field and nothing else.
        if (_editing)
        {
            HandleEditKey(key);
            AcceptEvent();
            return;
        }

        switch (key.Keycode)
        {
            case Key.Escape:
                Close();
                AcceptEvent();
                return;

            case Key.Up:
                _cursor = Math.Max(0, _cursor - 1);
                ClampScroll();
                AcceptEvent();
                return;

            case Key.Down:
                _cursor = Math.Min(Math.Max(0, _visible.Count - 1), _cursor + 1);
                ClampScroll();
                AcceptEvent();
                return;

            case Key.Pageup:
                _cursor = Math.Max(0, _cursor - PageRows);
                ClampScroll();
                AcceptEvent();
                return;

            case Key.Pagedown:
                _cursor = Math.Min(Math.Max(0, _visible.Count - 1), _cursor + PageRows);
                ClampScroll();
                AcceptEvent();
                return;

            case Key.Home:
                _cursor = 0;
                ClampScroll();
                AcceptEvent();
                return;

            case Key.End:
                _cursor = Math.Max(0, _visible.Count - 1);
                ClampScroll();
                AcceptEvent();
                return;

            case Key.Enter or Key.KpEnter:
                Confirm();
                AcceptEvent();
                return;

            case Key.Backspace:
                if (_filter.Length > 0)
                {
                    _filter = _filter[..^1];
                    Rebuild();
                }
                AcceptEvent();
                return;
        }

        // Typing filters the list. In a browser that is the fastest way through 186 entity classes; in a
        // property grid it finds the key you want among thirty.
        if (key.Unicode >= 32 && key.Unicode < 0x10FFFF && _filter.Length < 40)
        {
            _filter += char.ConvertFromUtf32((int)key.Unicode);
            Rebuild();
            AcceptEvent();
        }
    }

    private void HandleEditKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Escape:
                _editing = false;
                _edit = "";
                return;

            case Key.Enter or Key.KpEnter:
            {
                DialogRow? row = Current;
                _editing = false;
                if (row is not null)
                    Fire(row, _edit);
                _edit = "";
                return;
            }

            case Key.Backspace:
                if (_edit.Length > 0)
                    _edit = _edit[..^1];
                return;
        }

        if (key.Unicode >= 32 && key.Unicode < 0x10FFFF && _edit.Length < 128)
            _edit += char.ConvertFromUtf32((int)key.Unicode);
    }

    /// <summary>Act on the highlighted row: start editing it, or fire its command.</summary>
    private void Confirm()
    {
        if (Current is not { } row)
            return;

        if (row.Editable)
        {
            _editing = true;
            _edit = row.Value;
            return;
        }
        Fire(row, "");
    }

    private void Fire(DialogRow row, string value)
    {
        if (row.Command.Length == 0)
            return;

        string cmd = row.Command.Replace("%v", value, StringComparison.Ordinal);
        CommandSink?.Invoke(cmd);

        // A property grid stays open — you are usually setting several keys — while a browser has done its
        // job the moment you picked something.
        if (_kind == DialogKind.Browser)
            Close();
        else
            row.Value = value;
    }

    // =====================================================================================
    //  Layout + draw
    // =====================================================================================

    private int FontPx => (int)Mathf.Clamp(Size2.Y * 0.018f, 11f, 22f);

    private float RowH => FontPx + 7f;

    private Rect2 Frame()
    {
        float w = Mathf.Clamp(Size2.X * 0.52f, 380f, 900f);
        float h = HeaderH + PageRows * RowH + DetailH + FooterH + 16f;
        h = MathF.Min(h, Size2.Y * 0.9f);
        return new Rect2((Size2.X - w) * 0.5f, (Size2.Y - h) * 0.5f, w, h);
    }

    private float HeaderH => RowH * 1.6f;

    private float DetailH => RowH * 3f;

    private float FooterH => RowH * 1.3f;

    /// <summary>Row index under a panel-local point, or -1.</summary>
    private int RowAt(Vector2 pos)
    {
        Rect2 f = Frame();
        float top = f.Position.Y + HeaderH;
        if (pos.X < f.Position.X || pos.X > f.Position.X + f.Size.X)
            return -1;
        if (pos.Y < top)
            return -1;

        int row = (int)((pos.Y - top) / RowH) + _scroll;
        return row >= 0 && row < _visible.Count && row < _scroll + PageRows ? row : -1;
    }

    private static readonly Color Bg = new(0.04f, 0.05f, 0.07f, 0.94f);
    private static readonly Color Edge = new(0.45f, 0.85f, 1f, 0.7f);
    private static readonly Color TitleColor = new(1f, 0.85f, 0.45f);
    private static readonly Color LabelColor = new(0.88f, 0.92f, 0.96f);
    private static readonly Color ValueColor = new(0.55f, 0.82f, 1f);
    private static readonly Color GroupColor = new(0.55f, 0.6f, 0.66f);
    private static readonly Color DetailColor = new(0.62f, 0.68f, 0.74f);
    private static readonly Color SelBg = new(0.2f, 0.45f, 0.65f, 0.55f);
    private static readonly Color EditColor = new(0.45f, 1f, 0.6f);

    protected override void DrawPanel()
    {
        if (!_open || ShowModeCvar() == 0)
            return;

        Rect2 f = Frame();
        int px = FontPx;
        float rh = RowH;

        DrawRect(f, Bg);
        DrawRect(new Rect2(f.Position.X, f.Position.Y, f.Size.X, 1f), Edge);
        DrawRect(new Rect2(f.Position.X, f.Position.Y + f.Size.Y - 1f, f.Size.X, 1f), Edge);
        DrawRect(new Rect2(f.Position.X, f.Position.Y, 1f, f.Size.Y), Edge);
        DrawRect(new Rect2(f.Position.X + f.Size.X - 1f, f.Position.Y, 1f, f.Size.Y), Edge);

        // --- header: title, and the filter as you type it ---
        string head = _filter.Length > 0 ? $"{_title}   filter: {_filter}_" : _title;
        DrawText(new Vector2(f.Position.X + 12f, f.Position.Y + 6f), head, TitleColor, px);
        DrawText(new Vector2(f.Position.X + f.Size.X - 12f - MeasureText($"{_visible.Count}", px),
                f.Position.Y + 6f), $"{_visible.Count}", GroupColor, px);

        // --- rows ---
        float y = f.Position.Y + HeaderH;
        string lastGroup = "";
        for (int i = _scroll; i < _visible.Count && i < _scroll + PageRows; i++)
        {
            DialogRow r = _rows[_visible[i]];

            if (i == _cursor)
                DrawRect(new Rect2(f.Position.X + 1f, y, f.Size.X - 2f, rh), SelBg);

            // Group headings are drawn inline as the list crosses into a new one, so a 186-row palette reads
            // as sections without needing a second column or a tree widget.
            if (r.Group.Length > 0 && r.Group != lastGroup)
            {
                DrawText(new Vector2(f.Position.X + 12f, y + 3f), r.Group.ToUpperInvariant(), GroupColor, px - 1);
                lastGroup = r.Group;
                y += rh;
                if (y > f.Position.Y + HeaderH + PageRows * rh)
                    break;
                if (i == _cursor)
                    DrawRect(new Rect2(f.Position.X + 1f, y, f.Size.X - 2f, rh), SelBg);
            }

            DrawText(new Vector2(f.Position.X + 22f, y + 3f), r.Label, LabelColor, px);

            bool editingThis = _editing && i == _cursor;
            string value = editingThis ? _edit + "_" : r.Value;
            if (value.Length > 0)
                DrawText(new Vector2(f.Position.X + f.Size.X - 12f - MeasureText(value, px), y + 3f),
                    value, editingThis ? EditColor : ValueColor, px);

            y += rh;
        }

        // --- detail pane: whatever the highlighted row documents ---
        float detailY = f.Position.Y + f.Size.Y - FooterH - DetailH;
        DrawRect(new Rect2(f.Position.X + 1f, detailY, f.Size.X - 2f, 1f), new Color(1f, 1f, 1f, 0.12f));
        if (Current is { Detail.Length: > 0 } cur)
            DrawWrapped(cur.Detail, f.Position.X + 12f, detailY + 5f, f.Size.X - 24f, px - 1, DetailColor);

        // --- footer: what the keys do ---
        string footer = _editing
            ? "type a value · Enter applies · Esc cancels"
            : _footer.Length > 0 ? _footer
            : "arrows move · Enter picks · type to filter · Esc closes";
        DrawText(new Vector2(f.Position.X + 12f, f.Position.Y + f.Size.Y - FooterH + 2f), footer, GroupColor, px - 1);
    }

    /// <summary>Word-wrap the detail text into the pane rather than letting it run off the edge.</summary>
    private void DrawWrapped(string text, float x, float y, float width, int px, Color color)
    {
        string[] words = text.Split(' ');
        var line = new System.Text.StringBuilder();
        int drawn = 0;

        foreach (string word in words)
        {
            string candidate = line.Length == 0 ? word : $"{line} {word}";
            if (MeasureText(candidate, px) > width && line.Length > 0)
            {
                DrawText(new Vector2(x, y + drawn * (px + 3f)), line.ToString(), color, px);
                line.Clear();
                line.Append(word);
                if (++drawn >= 3)
                    return;   // the pane is three lines; the rest is in the file, not on screen
            }
            else
            {
                line.Clear();
                line.Append(candidate);
            }
        }
        if (line.Length > 0)
            DrawText(new Vector2(x, y + drawn * (px + 3f)), line.ToString(), color, px);
    }
}
