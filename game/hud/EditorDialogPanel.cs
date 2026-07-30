using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// The map editor's dialog surface (design doc §11.9): the entity palette, the key inspector, the shader
/// browser and the patch dialogs, all through ONE panel.
///
/// Shared rather than one panel per dialog, because they are the same few shapes underneath. A BROWSER is a
/// grouped list plus a description of whatever is highlighted plus a confirm; a GALLERY is that browser laid
/// out as a grid of pictures; a PROPERTY GRID is a list of rows you edit in place. Bespoke dialogs would be
/// several times the input handling, several sets of keyboard conventions to remember, and several places for
/// them to drift apart.
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
        /// <summary>Grouped list + description + confirm. The palette, patch create.</summary>
        Browser,

        /// <summary>Rows edited in place. The entity key inspector, the light dialog.</summary>
        Properties,

        /// <summary>
        /// A browser laid out as a THUMBNAIL grid — same confirm and close semantics, a two-dimensional
        /// cursor, one picture per entry (backlog T6). Only the shader browser wants it: nobody picks a wall
        /// texture from a list of strings, and nothing else the editor lists has a picture to show.
        /// </summary>
        Gallery,
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

    /// <summary>
    /// One drawn line: either a group heading, or a run of cells (one in a list, up to <c>_cols</c> in a
    /// gallery).
    ///
    /// Scrolling and hit-testing work in THIS space, and that is the point of having it. The draw loop always
    /// gave a heading its own vertical line while the hit test computed the row as if every entry were exactly
    /// one line, so a click selected the wrong entry — off by the number of headings above it, growing as you
    /// scrolled — and the last page could not be reached at all. In the shader browser, where every entry
    /// carries a group, that is every click.
    /// </summary>
    private readonly record struct VisualRow(string Heading, int First, int Count);

    // ---- state ----
    private readonly List<DialogRow> _rows = new();
    private readonly List<int> _visible = new();     // indices into _rows after filtering
    private readonly List<VisualRow> _lines = new(); // scroll and hit-test space
    private readonly List<int> _lineOfCell = new();  // parallel to _visible: cell -> line index
    private int _cols = 1;                           // 1 in Browser/Properties, N in Gallery
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

    /// <summary>
    /// Thumbnails for <see cref="DialogKind.Gallery"/>, wired once by the host. Null draws swatches, which is
    /// what a session with no asset system gets and is still a usable grid.
    /// </summary>
    public EditorThumbnailCache? Thumbnails { get; set; }

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
            VortexArena.Common.Diagnostics.Log.Info($"{_title}: nothing to show");
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
        if (!show)
            return;

        if (IsGallery && Thumbnails is { } thumbs)
        {
            // Pushed rather than read at the point of use: SetSize discards the cache, so it has to happen
            // once a frame at a known point and not in the middle of a draw pass.
            thumbs.SetSize((int)GlobalF(ThumbSizeCvar, 96f));
            thumbs.Capacity = (int)Mathf.Clamp(GlobalF(ThumbCacheCvar, 512f), 32f, 4096f);
        }

        QueueRedraw();
    }

    /// <summary>Mirrors <c>EditorController.CvarThumbSize</c>; game/hud does not reference game/vmap.</summary>
    private const string ThumbSizeCvar = "cl_editor_thumb_size";

    /// <summary>Mirrors <c>EditorController.CvarThumbCache</c>.</summary>
    private const string ThumbCacheCvar = "cl_editor_thumb_cache";

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
        BuildLines();
        ClampScroll();
    }

    /// <summary>
    /// Flatten the visible entries into drawn lines: a heading whenever the group changes, then the run of
    /// entries chunked <c>_cols</c> wide. Cheap (one pass over what survived the filter) and re-run only when
    /// the filter or the column count actually changes.
    /// </summary>
    private void BuildLines()
    {
        _lines.Clear();
        _lineOfCell.Clear();

        string lastGroup = "";
        int i = 0;
        while (i < _visible.Count)
        {
            string group = _rows[_visible[i]].Group;
            if (group.Length > 0 && group != lastGroup)
            {
                _lines.Add(new VisualRow(group, 0, 0));
                lastGroup = group;
            }

            // A run is the entries sharing this group; it wraps at _cols and stops at the next group, so a
            // heading always starts a fresh line rather than appearing mid-row.
            int run = 0;
            while (i + run < _visible.Count && run < _cols
                   && _rows[_visible[i + run]].Group == group)
                run++;

            int line = _lines.Count;
            for (int c = 0; c < run; c++)
                _lineOfCell.Add(line);
            _lines.Add(new VisualRow("", i, run));
            i += run;
        }
    }

    /// <summary>Which drawn line a cell sits on; 0 when the model has not been built yet.</summary>
    private int LineOf(int cell)
        => cell >= 0 && cell < _lineOfCell.Count ? _lineOfCell[cell] : 0;

    private void ClampScroll()
    {
        int page = PageLines;
        int line = LineOf(_cursor);
        if (line < _scroll)
            _scroll = line;
        else if (line >= _scroll + page)
            _scroll = line - page + 1;
        _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, _lines.Count - page)));
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

        // The column count follows the frame, which follows the viewport, so it can change under a resize
        // with no other event. Cheap to re-derive and it keeps input and drawing in one geometry.
        SyncColumns();

        if (@event is InputEventMouseButton { Pressed: true } mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Left:
                {
                    int row = CellAt(mb.Position);
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
                    _scroll = Math.Min(Math.Max(0, _lines.Count - PageLines), _scroll + 3);
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

            // Up/Down move by a whole ROW of cells, which in a list is one entry and in a gallery is _cols of
            // them. Left/Right step one cell, and only in a gallery — in a list they would duplicate Up/Down.
            case Key.Up:
                MoveCursor(-_cols);
                AcceptEvent();
                return;

            case Key.Down:
                MoveCursor(+_cols);
                AcceptEvent();
                return;

            case Key.Left when _kind == DialogKind.Gallery:
                MoveCursor(-1);
                AcceptEvent();
                return;

            case Key.Right when _kind == DialogKind.Gallery:
                MoveCursor(+1);
                AcceptEvent();
                return;

            case Key.Pageup:
                MoveCursor(-PageLines * _cols);
                AcceptEvent();
                return;

            case Key.Pagedown:
                MoveCursor(+PageLines * _cols);
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

    private void MoveCursor(int by)
    {
        _cursor = Math.Clamp(_cursor + by, 0, Math.Max(0, _visible.Count - 1));
        ClampScroll();
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

        // A property grid stays open — you are usually setting several keys — while a browser (list or grid)
        // has done its job the moment you picked something.
        if (_kind != DialogKind.Properties)
            Close();
        else
            row.Value = value;
    }

    // =====================================================================================
    //  Layout + draw
    // =====================================================================================

    private int FontPx => (int)Mathf.Clamp(Size2.Y * 0.018f, 11f, 22f);

    private float RowH => FontPx + 7f;

    private bool IsGallery => _kind == DialogKind.Gallery;

    /// <summary>Drawn edge of a thumbnail, in pixels. Follows the viewport so the grid reads at any size.</summary>
    private float ThumbPx => Mathf.Clamp(Size2.Y * 0.105f, 48f, 128f);

    private float CellW => ThumbPx + 10f;

    /// <summary>Image plus one label line under it.</summary>
    private float CellH => ThumbPx + RowH + 6f;

    /// <summary>Height of one drawn line — a grid cell in a gallery, a text row otherwise.</summary>
    private float LineH => IsGallery ? CellH : RowH;

    private Rect2 Frame()
    {
        float w = IsGallery
            ? Mathf.Clamp(Size2.X * 0.74f, 560f, 1400f)
            : Mathf.Clamp(Size2.X * 0.52f, 380f, 900f);
        float h = HeaderH + (IsGallery ? 6f * CellH : PageRows * RowH) + DetailH + FooterH + 16f;
        h = MathF.Min(h, Size2.Y * 0.9f);
        return new Rect2((Size2.X - w) * 0.5f, (Size2.Y - h) * 0.5f, w, h);
    }

    private float HeaderH => RowH * 1.6f;

    private float DetailH => RowH * 3f;

    private float FooterH => RowH * 1.3f;

    /// <summary>Height available for lines, between the header and the detail pane.</summary>
    private float ListH
    {
        get
        {
            Rect2 f = Frame();
            return MathF.Max(LineH, f.Size.Y - HeaderH - DetailH - FooterH - 8f);
        }
    }

    /// <summary>How many drawn lines fit. Replaces the fixed <see cref="PageRows"/> once a gallery reflows.</summary>
    private int PageLines => Math.Max(1, (int)(ListH / LineH));

    /// <summary>
    /// Re-derive the column count from the current frame, rebuilding the line model if it changed. Called
    /// from both draw and input so the two never disagree about where a cell is.
    /// </summary>
    private void SyncColumns()
    {
        int cols = IsGallery
            ? Math.Max(1, (int)((Frame().Size.X - 24f) / CellW))
            : 1;
        if (cols == _cols)
            return;
        _cols = cols;
        BuildLines();
        ClampScroll();
    }

    /// <summary>
    /// Visible-entry index under a panel-local point, or -1 for empty space or a group heading.
    ///
    /// Resolved through the line model rather than dividing the offset by a row height: headings occupy a
    /// line without being selectable, which is exactly what the old arithmetic could not express.
    /// </summary>
    private int CellAt(Vector2 pos)
    {
        Rect2 f = Frame();
        float top = f.Position.Y + HeaderH;
        if (pos.X < f.Position.X || pos.X > f.Position.X + f.Size.X || pos.Y < top)
            return -1;

        int line = (int)((pos.Y - top) / LineH) + _scroll;
        if (line < 0 || line >= _lines.Count || line >= _scroll + PageLines)
            return -1;

        VisualRow row = _lines[line];
        if (row.Count == 0)
            return -1;                                     // a heading: no entry under the pointer

        if (!IsGallery)
            return row.First;

        int col = (int)((pos.X - (f.Position.X + 12f)) / CellW);
        if (col < 0 || col >= row.Count)
            return -1;
        return row.First + col;
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

    private static readonly Color ThumbBg = new(0.10f, 0.11f, 0.13f, 0.9f);
    private static readonly Color ThumbEdge = new(1f, 1f, 1f, 0.10f);
    private static readonly Color FieldBg = new(0f, 0f, 0f, 0.35f);
    private static readonly Color FieldEdge = new(1f, 1f, 1f, 0.14f);

    protected override void DrawPanel()
    {
        if (!_open || ShowModeCvar() == 0)
            return;

        SyncColumns();

        Rect2 f = Frame();
        int px = FontPx;
        float rh = RowH;

        DrawRect(f, Bg);
        DrawRect(new Rect2(f.Position.X, f.Position.Y, f.Size.X, 1f), Edge);
        DrawRect(new Rect2(f.Position.X, f.Position.Y + f.Size.Y - 1f, f.Size.X, 1f), Edge);
        DrawRect(new Rect2(f.Position.X, f.Position.Y, 1f, f.Size.Y), Edge);
        DrawRect(new Rect2(f.Position.X + f.Size.X - 1f, f.Position.Y, 1f, f.Size.Y), Edge);

        // --- header: title, a real search field, and how much of the list survived it ---
        //
        // The keyboard side already worked; it just had nowhere to show. A grid of two thousand textures is
        // unusable without search, and a filter that only appears once you have guessed it exists is not a
        // feature a mapper will find.
        DrawText(new Vector2(f.Position.X + 12f, f.Position.Y + 6f), _title, TitleColor, px);
        float fieldX = f.Position.X + 12f + MeasureText(_title, px) + 16f;
        float fieldW = MathF.Max(80f, f.Size.X * 0.42f);
        if (fieldX + fieldW < f.Position.X + f.Size.X - 90f)
        {
            var box = new Rect2(fieldX, f.Position.Y + 5f, fieldW, rh - 2f);
            DrawRect(box, FieldBg);
            DrawRect(box, FieldEdge, filled: false, width: 1f);
            DrawText(box.Position + new Vector2(6f, 2f),
                _filter.Length > 0 ? _filter + "_" : "type to search",
                _filter.Length > 0 ? LabelColor : GroupColor, px - 1);
        }
        string count = _filter.Length > 0 ? $"{_visible.Count}/{_rows.Count}" : $"{_rows.Count}";
        DrawText(new Vector2(f.Position.X + f.Size.X - 12f - MeasureText(count, px), f.Position.Y + 6f),
            count, GroupColor, px);

        // --- lines: headings and entries, in the same space the hit test uses ---
        //
        // Before the loop, so every Peek below counts as "used this frame" and the eviction order reflects
        // what is actually on screen.
        if (IsGallery)
            Thumbnails?.BeginFrame();

        float y = f.Position.Y + HeaderH;
        float lineH = LineH;
        for (int l = _scroll; l < _lines.Count && l < _scroll + PageLines; l++, y += lineH)
        {
            VisualRow line = _lines[l];

            if (line.Count == 0)
            {
                // Group headings drawn inline as the list crosses into a new one, so a 186-row palette reads
                // as sections without needing a second column or a tree widget.
                DrawText(new Vector2(f.Position.X + 12f, y + 3f), line.Heading.ToUpperInvariant(),
                    GroupColor, px - 1);
                continue;
            }

            if (IsGallery)
            {
                DrawGalleryLine(f, line, y, px);
                continue;
            }

            int i = line.First;
            DialogRow r = _rows[_visible[i]];
            if (i == _cursor)
                DrawRect(new Rect2(f.Position.X + 1f, y, f.Size.X - 2f, rh), SelBg);

            DrawText(new Vector2(f.Position.X + 22f, y + 3f), r.Label, LabelColor, px);

            bool editingThis = _editing && i == _cursor;
            string value = editingThis ? _edit + "_" : r.Value;
            if (value.Length > 0)
                DrawText(new Vector2(f.Position.X + f.Size.X - 12f - MeasureText(value, px), y + 3f),
                    value, editingThis ? EditColor : ValueColor, px);
        }

        // Ask for one line beyond each edge as well, so an unhurried scroll never runs into empty cells. The
        // request pass is idempotent and capped in flight, so overshooting costs nothing.
        if (IsGallery && Thumbnails is { } thumbs)
        {
            Prefetch(thumbs, _scroll - 1);
            Prefetch(thumbs, _scroll + PageLines);
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

    /// <summary>
    /// One row of thumbnail cells: the picture, a frame, and the leaf of the shader path under it.
    ///
    /// A cell is NEVER blank. A missing thumbnail draws a dark swatch, and a material that is known to have
    /// no resolvable image draws a "?" instead of being asked for again — a grid that silently retries a
    /// thousand misses every frame would spend the whole in-flight budget on them.
    /// </summary>
    private void DrawGalleryLine(Rect2 f, VisualRow line, float y, int px)
    {
        float thumb = ThumbPx;
        float cellW = CellW;

        for (int c = 0; c < line.Count; c++)
        {
            int vi = line.First + c;
            if (vi >= _visible.Count)
                break;
            DialogRow r = _rows[_visible[vi]];

            var cell = new Rect2(f.Position.X + 12f + c * cellW, y, cellW - 4f, CellH - 4f);
            var img = new Rect2(cell.Position.X + 2f, cell.Position.Y + 2f, thumb, thumb);

            if (vi == _cursor)
                DrawRect(cell, SelBg);

            Texture2D? tex = Thumbnails?.Peek(r.Label);
            if (tex is not null)
            {
                DrawTextureRect(tex, img, false, Colors.White);
            }
            else
            {
                DrawRect(img, ThumbBg);
                if (Thumbnails is null || Thumbnails.IsMiss(r.Label))
                    DrawTextCentered(img.Position + new Vector2(0f, img.Size.Y * 0.38f), img.Size.X, "?",
                        GroupColor, px - 2);
                else
                    Thumbnails.Request(r.Label);   // lazy: only what is on screen, only once
            }
            DrawRect(img, ThumbEdge, filled: false, width: 1f);

            DrawTextCentered(new Vector2(cell.Position.X, img.Position.Y + thumb + 2f), cell.Size.X,
                Leaf(r.Label), vi == _cursor ? LabelColor : GroupColor, px - 2);
        }
    }

    /// <summary>Queue the thumbnails for one off-screen line, drawing nothing.</summary>
    private void Prefetch(EditorThumbnailCache thumbs, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count)
            return;
        VisualRow line = _lines[lineIndex];
        for (int c = 0; c < line.Count && line.First + c < _visible.Count; c++)
        {
            string label = _rows[_visible[line.First + c]].Label;
            if (!thumbs.IsMiss(label) && thumbs.Peek(label) is null)
                thumbs.Request(label);
        }
    }

    /// <summary>The last path segment — a cell is too narrow for <c>textures/facility114x/…</c>.</summary>
    private static string Leaf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
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
