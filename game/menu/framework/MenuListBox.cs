using System;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A faithful port of the Xonotic menu listbox — <c>ListBox</c> (qcsrc/menu/item/listbox.qc) plus the skin
/// wiring of <c>XonoticListBox</c> (qcsrc/menu/xonotic/listbox.qc).
///
/// <para>Godot's <see cref="ItemList"/> and <see cref="Tree"/> cover the easy lists, but they cannot express
/// what a QC listbox does per row: <b>variable row heights</b> (the server browser's category headings are
/// taller than the rows under them), <b>free-form per-row drawing</b> (an icon strip, several independently
/// aligned text columns, colour-coded server names, a per-row alpha), and the <b>hover highlight that fades</b>
/// from SKINALPHA_LISTBOX_FOCUSED to SKINFADEALPHA_LISTBOX_FOCUSED as the cursor settles. So this reproduces
/// the QC control itself: a <see cref="Control"/> that draws its own rows, its own translucent backdrop
/// (COLOR_LISTBOX_BACKGROUND) and its own skinned scrollbar (the <c>scrollbar_s/_n/_f/_c</c> art through
/// <see cref="VertButtonPictureStyleBox"/>, exactly as ListBox_draw does with draw_VertButtonPicture).</para>
///
/// <para><b>Units.</b> QC listbox geometry is normalised: scroll positions and item heights are fractions of
/// the widget's height, so "1" means one full page. This port works in pixels instead — the same arithmetic
/// with <c>1</c> replaced by <see cref="ViewHeight"/> — because everything it has to interoperate with (fonts,
/// textures, mouse events) is in pixels. Every formula below is otherwise the QC one, including the exponential
/// scroll averaging (shared with <see cref="SmoothScroll.Advance"/>) and the minimum-grabber-size correction
/// in <see cref="UpdateControlTopBottom"/>.</para>
///
/// <para>Subclasses supply <see cref="ItemCount"/> and override <see cref="DrawItem"/>; lists with uneven rows
/// additionally override the four geometry methods (<see cref="GetItemHeight"/>, <see cref="GetItemStart"/>,
/// <see cref="GetItemAtPos"/>, <see cref="GetTotalHeight"/>) under the same consistency rules the QC header
/// spells out — <c>GetTotalHeight() == Σ GetItemHeight(i)</c> and <c>GetItemStart(i+1) == GetItemStart(i) +
/// GetItemHeight(i)</c>.</para>
/// </summary>
public abstract partial class MenuListBox : Control
{
    // -----------------------------------------------------------------------------------------------------
    //  State (ListBox's ATTRIBs)
    // -----------------------------------------------------------------------------------------------------

    private double _scrollPos;
    private double _scrollPosTarget;
    private int _selectedItem;
    private int _focusedItem = -1;
    private float _focusedItemAlpha;
    private int _needScrollToItem = -1;

    /// <summary>0 = idle, 1 = dragging the scrollbar grabber, 2 = dragging over items, 3 = just released.</summary>
    private int _pressed;
    private float _pressOffset;
    private double _previousValue;
    private Vector2 _dragScrollPos;
    private float _mouseMoveOffset = -1f;
    private int _lastClickedItem = -1;
    private ulong _lastClickedMsec;

    private double _controlTop;
    private double _controlBottom = 1.0;

    /// <summary>How many rows the list holds. Setting it re-clamps the selection and repaints.</summary>
    public int ItemCount
    {
        get => _itemCount;
        set
        {
            if (_itemCount == value)
                return;
            _itemCount = Math.Max(0, value);
            if (_selectedItem >= _itemCount)
                _selectedItem = Math.Max(0, _itemCount - 1);
            QueueRedraw();
        }
    }
    private int _itemCount;

    /// <summary>Height of one ordinary row, in pixels (QC <c>itemHeight</c>, there in window fractions).</summary>
    public float ItemHeight { get; set; } = 24f;

    /// <summary>The selected row (QC <c>selectedItem</c>). Assigning scrolls it into view, as setSelected does.</summary>
    public int SelectedItem
    {
        get => _selectedItem;
        set => SetSelected(value);
    }

    /// <summary>The row under the cursor, or -1 (QC <c>focusedItem</c>).</summary>
    public int FocusedItem => _focusedItem;

    /// <summary>
    /// QC <c>selectionDoesntMatter</c>: for lists that only scroll (no meaningful active row), the arrow and
    /// page keys move the view instead of the selection.
    /// </summary>
    public bool SelectionDoesntMatter { get; set; }

    /// <summary>True while the view is still catching up with the scroll target (QC <c>isScrolling</c>).</summary>
    public bool IsScrolling => _scrollPos != _scrollPosTarget;

    /// <summary>Current scroll offset in pixels from the top of the content.</summary>
    protected double ScrollPos => _scrollPos;

    /// <summary>The visible height — the QC's "1" (one page).</summary>
    protected float ViewHeight => Size.Y;

    /// <summary>The scrollbar gutter width in pixels (SKINWIDTH_SCROLLBAR), 0 when the list can't scroll.</summary>
    protected float ScrollbarWidth => GetTotalHeight() > ViewHeight ? MenuSkin.ScrollbarWidth : 0f;

    /// <summary>Width available to a row — the control minus the scrollbar gutter.</summary>
    protected float ContentWidth => Size.X - ScrollbarWidth;

    /// <summary>Raised when the selected row changes (QC <c>setSelected</c> hook / clickListBoxItem).</summary>
    public event Action<int>? ItemSelected;

    /// <summary>Raised on double-click or Enter (QC <c>doubleClickListBoxItem</c>).</summary>
    public event Action<int>? ItemActivated;

    /// <summary>Raised when the hovered row changes (QC <c>focusedItemChangeNotify</c>) — e.g. to drop a tooltip.</summary>
    public event Action<int>? FocusedItemChanged;

    protected MenuListBox()
    {
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;      // rows at the bottom edge are cut, not spilled (QC draw_SetClip)
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Geometry — override these together for variable-height rows (see the class remarks)
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Total content height in pixels (QC <c>getTotalHeight</c>).</summary>
    public virtual float GetTotalHeight() => ItemCount * ItemHeight;

    /// <summary>The row containing content offset <paramref name="pos"/> (QC <c>getItemAtPos</c>).</summary>
    public virtual int GetItemAtPos(double pos) => ItemHeight > 0f ? (int)Math.Floor(pos / ItemHeight) : 0;

    /// <summary>Offset of row <paramref name="i"/>'s top edge (QC <c>getItemStart</c>).</summary>
    public virtual float GetItemStart(int i) => ItemHeight * i;

    /// <summary>Height of row <paramref name="i"/> (QC <c>getItemHeight</c>).</summary>
    public virtual float GetItemHeight(int i) => ItemHeight;

    /// <summary>Draw one row into <paramref name="rect"/> (control-local pixels). QC <c>drawListBoxItem</c>.</summary>
    protected abstract void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused);

    private int LastFullyVisibleItemAt(double pos) => GetItemAtPos(pos + ViewHeight - 0.01) - 1;
    private int FirstFullyVisibleItemAt(double pos) => GetItemAtPos(pos + 0.01) + 1;

    /// <summary>The largest legal scroll offset (QC's <c>getTotalHeight() - 1</c>).</summary>
    private double MaxScroll => Math.Max(0.0, GetTotalHeight() - ViewHeight);

    // -----------------------------------------------------------------------------------------------------
    //  Selection + scrolling
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Scroll just far enough to bring row <paramref name="i"/> fully into view, leaving the view alone if it
    /// already is (QC <c>ListBox_scrollToItem</c>). Deferred until the first layout when the row height is
    /// still unknown, the same way the QC defers on <c>itemHeight == 1</c>.
    /// </summary>
    public void ScrollToItem(int i)
    {
        if (ViewHeight <= 0f)
        {
            _needScrollToItem = i;
            return;
        }
        if (ItemCount <= 0)
            return;
        i = Math.Clamp(i, 0, ItemCount - 1);

        if (i < FirstFullyVisibleItemAt(_scrollPos))
            _scrollPosTarget = GetItemStart(i);
        else if (i > LastFullyVisibleItemAt(_scrollPos))
            _scrollPosTarget = i == ItemCount - 1
                ? MaxScroll
                : GetItemStart(i + 1) - ViewHeight;
        _scrollPosTarget = Math.Clamp(_scrollPosTarget, 0.0, MaxScroll);
    }

    /// <summary>QC <c>ListBox_setSelected</c>: clamp, scroll into view, remember — and tell listeners.</summary>
    public virtual void SetSelected(int i)
    {
        if (ItemCount <= 0)
            return;
        i = Math.Clamp(i, 0, ItemCount - 1);
        ScrollToItem(i);
        bool changed = _selectedItem != i;
        _selectedItem = i;
        QueueRedraw();
        if (changed)
            ItemSelected?.Invoke(i);
    }

    /// <summary>
    /// Move the selection without raising <see cref="ItemSelected"/> and without scrolling — for a list that
    /// rebuilt underneath a selection that did not actually change. QC does this by assigning
    /// <c>me.selectedItem</c> directly and says why: following it with setSelected would scroll the view (and
    /// here, would also re-stamp the address box over whatever the user has typed).
    /// </summary>
    protected void SetSelectedSilent(int i)
    {
        if (ItemCount <= 0)
            return;
        _selectedItem = Math.Clamp(i, 0, ItemCount - 1);
        QueueRedraw();
    }

    /// <summary>Raise <see cref="ItemActivated"/> for <paramref name="index"/> (Enter, from a subclass).</summary>
    protected void EmitActivate(int index) => ItemActivated?.Invoke(index);

    /// <summary>
    /// Which part of a row the selection/hover fill covers. Whole row by default; a list whose rows carry a
    /// heading band overrides this so the fill stays off the heading (the QC's SET_YRANGE split).
    /// </summary>
    protected virtual Rect2 HighlightRect(int index, Rect2 rect) => rect;

    /// <summary>
    /// Whether the selected row is actually highlighted. The server browser turns this off while its selection
    /// is still the placeholder one it starts with (QC <c>lockedSelectedItem</c>), so an untouched list does not
    /// show a pick the player never made.
    /// </summary>
    protected virtual bool ShowSelection => true;

    /// <summary>QC <c>ListBox_setFocusedItem</c> — including the alpha kick that makes the hover flash.</summary>
    private void SetFocusedItem(int item)
    {
        int previous = _focusedItem;
        _focusedItem = (item >= 0 && item < ItemCount) ? item : -1;
        if (previous == _focusedItem)
            return;
        FocusedItemChanged?.Invoke(_focusedItem);
        if (_focusedItem >= 0)
            _focusedItemAlpha = MenuSkin.ListFocusedAlpha;
        QueueRedraw();
    }

    /// <summary>Jump the view (no easing) — for a rebuild that should not animate from the old position.</summary>
    protected void SetScrollImmediate(double pos)
    {
        _scrollPos = _scrollPosTarget = Math.Clamp(pos, 0.0, MaxScroll);
        QueueRedraw();
    }

    private void ScrollBy(double delta)
    {
        _scrollPosTarget = Math.Clamp(_scrollPosTarget + delta, 0.0, MaxScroll);
        QueueRedraw();
    }

    // -----------------------------------------------------------------------------------------------------
    //  Input
    // -----------------------------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mb:
                HandleMouseButton(mb);
                break;
            case InputEventMouseMotion mm:
                HandleMouseMotion(mm);
                break;
            case InputEventKey { Pressed: true } key when HandleKey(key):
                AcceptEvent();
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton mb)
    {
        Vector2 pos = mb.Position;
        if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (!mb.Pressed)
                return;
            // ListBox_keyDown K_MWHEELUP/DOWN: half a page per notch.
            ScrollBy((mb.ButtonIndex == MouseButton.WheelUp ? -0.5 : +0.5) * ViewHeight);
            AcceptEvent();
            return;
        }
        if (mb.ButtonIndex != MouseButton.Left)
        {
            if (mb.Pressed)
                OnAlternateClick(mb);
            return;
        }

        if (mb.Pressed)
        {
            GrabFocus();
            // QC treats ctrl+MOUSE1 as a synonym for MOUSE2 (serverlist.qc:1127) — a trackpad affordance.
            if (mb.CtrlPressed)
            {
                OnAlternateClick(mb);
                AcceptEvent();
                return;
            }
            MousePress(pos);
        }
        else
        {
            MouseRelease(pos);
        }
        AcceptEvent();
    }

    /// <summary>Right/middle click on a row. Base does nothing; the server browser opens Info / bookmarks.</summary>
    protected virtual void OnAlternateClick(InputEventMouseButton mb) { }

    /// <summary>QC <c>ListBox_mousePress</c>: scrollbar gutter = page/drag, body = select the row under the cursor.</summary>
    private void MousePress(Vector2 pos)
    {
        if (pos.X < 0 || pos.Y < 0 || pos.X >= Size.X || pos.Y >= Size.Y)
            return;
        _dragScrollPos = pos;
        UpdateControlTopBottom();

        float gutter = Size.X - ScrollbarWidth;
        if (ScrollbarWidth > 0f && pos.X >= gutter)
        {
            double frac = ViewHeight > 0f ? pos.Y / ViewHeight : 0.0;
            if (frac < _controlTop)
                ScrollBy(-ViewHeight);              // page up (QC: -1, i.e. one page)
            else if (frac > _controlBottom)
                ScrollBy(+ViewHeight);              // page down
            else
            {
                _pressed = 1;
                _pressOffset = pos.Y;
                _previousValue = _scrollPos;
            }
            return;
        }

        // Keep selecting while the button is held, even outside the control; the click fires on release.
        _pressed = 2;
        int clicked = GetItemAtPos(_scrollPos + pos.Y);
        SetSelected(clicked);
        SetFocusedItem(clicked);
    }

    /// <summary>QC <c>ListBox_mouseDrag</c>.</summary>
    private void MouseDrag(Vector2 pos)
    {
        UpdateControlTopBottom();
        _dragScrollPos = pos;

        if (_pressed == 1 && GetTotalHeight() > ViewHeight)
        {
            // Map the cursor's travel along the free part of the gutter onto the scrollable range. The QC
            // tolerance box (XonoticListBox: '2 0.2 0') snaps the view back when you drag far enough away.
            float tolX = 2f * ScrollbarWidth, tolY = 0.2f * ViewHeight;
            bool hit = pos.X >= Size.X - ScrollbarWidth - tolX && pos.X < Size.X + tolX
                       && pos.Y >= -tolY && pos.Y < Size.Y + tolY;
            if (hit)
            {
                double span = 1.0 - (_controlBottom - _controlTop);
                double d = span > 0.0
                    ? (pos.Y - _pressOffset) / ViewHeight / span * MaxScroll
                    : 0.0;
                _scrollPosTarget = _previousValue + d;
            }
            else
            {
                _scrollPosTarget = _previousValue;
            }
            _scrollPosTarget = Math.Clamp(_scrollPosTarget, 0.0, MaxScroll);
            QueueRedraw();
        }
        else if (_pressed == 2)
        {
            int clicked = GetItemAtPos(_scrollPos + pos.Y);
            SetSelected(clicked);
            SetFocusedItem(clicked);
            _mouseMoveOffset = -1f;
        }
    }

    /// <summary>QC <c>ListBox_mouseRelease</c>: the click (or double-click, within 0.3 s) lands here.</summary>
    private void MouseRelease(Vector2 pos)
    {
        if (_pressed == 2)
        {
            _pressed = 3; // set before SetSelected, so it can tell the button is already up (QC comment)
            int clicked = GetItemAtPos(_scrollPos + pos.Y);
            SetSelected(clicked);
            SetFocusedItem(clicked);
            if (ItemCount > 0)
            {
                ulong now = Time.GetTicksMsec();
                bool doubleClick = _selectedItem == _lastClickedItem && clicked == _selectedItem
                                   && now < _lastClickedMsec + 300; // QC: time < lastClickedTime + 0.3
                if (doubleClick)
                    ItemActivated?.Invoke(_selectedItem);
                else
                    OnItemClicked(_selectedItem);
                _lastClickedItem = _selectedItem;
                _lastClickedMsec = now;
            }
        }
        _pressed = 0;
    }

    /// <summary>QC <c>clickListBoxItem</c> — a single click on an already-selected row. Base does nothing.</summary>
    protected virtual void OnItemClicked(int index) { }

    private void HandleMouseMotion(InputEventMouseMotion mm)
    {
        if (_pressed is 1 or 2)
        {
            MouseDrag(mm.Position);
            return;
        }
        // QC ListBox_mouseMove: the hovered row is recomputed from this offset every frame, because the list
        // can scroll out from under a stationary cursor.
        Vector2 pos = mm.Position;
        if (pos.X < 0 || pos.Y < 0 || pos.X >= Size.X || pos.Y >= Size.Y)
            return;
        if (ScrollbarWidth > 0f && pos.X >= Size.X - ScrollbarWidth)
        {
            SetFocusedItem(-1);
            _mouseMoveOffset = -1f;
        }
        else
        {
            _mouseMoveOffset = pos.Y;
        }
        OnMouseMoved(pos);
    }

    /// <summary>The cursor moved over the list body (control-local pixels) — for per-column hover behaviour.</summary>
    protected virtual void OnMouseMoved(Vector2 pos) { }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationMouseExit)
        {
            // QC ListBox_focusLeave.
            _pressed = 0;
            SetFocusedItem(-1);
            _mouseMoveOffset = -1f;
        }
    }

    /// <summary>QC <c>ListBox_keyDown</c>. Returns true when the key was consumed.</summary>
    protected virtual bool HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Up:
                if (SelectionDoesntMatter) ScrollBy(-ItemHeight);
                else SetSelected(_selectedItem - 1);
                return true;
            case Key.Down:
                if (SelectionDoesntMatter) ScrollBy(+ItemHeight);
                else SetSelected(_selectedItem + 1);
                return true;
            case Key.Pageup:
                if (SelectionDoesntMatter) { ScrollBy(-0.5 * ViewHeight); return true; }
                SetSelected(StepByPage(-1));
                return true;
            case Key.Pagedown:
                if (SelectionDoesntMatter) { ScrollBy(+0.5 * ViewHeight); return true; }
                SetSelected(StepByPage(+1));
                return true;
            case Key.Home:
                SetSelected(0);
                return true;
            case Key.End:
                SetSelected(ItemCount - 1);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// QC's page-key walk: step away from the selection accumulating row heights until one page is covered,
    /// then back off by one. Written this way (rather than "selection ± rowsPerPage") because rows can differ
    /// in height.
    /// </summary>
    private int StepByPage(int direction)
    {
        int i = _selectedItem;
        float a = GetItemHeight(i);
        while (true)
        {
            i += direction;
            if (i < 0 || i >= ItemCount)
                break;
            a += GetItemHeight(i);
            if (a >= ViewHeight)
                break;
        }
        return i - direction;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Frame update + drawing (QC ListBox_draw)
    // -----------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree())
            return;

        // The hovered row is resolved here, not in the motion handler: the list can scroll under a stationary
        // cursor (wheel, keyboard, an async refresh) and the highlight has to follow.
        if (_mouseMoveOffset >= 0f)
            SetFocusedItem(GetItemAtPos(_scrollPos + _mouseMoveOffset));

        if (_needScrollToItem >= 0 && ViewHeight > 0f)
        {
            ScrollToItem(_needScrollToItem);
            _needScrollToItem = -1;
        }

        if (_scrollPos != _scrollPosTarget)
        {
            _scrollPos = SmoothScroll.Advance(_scrollPos, _scrollPosTarget, delta,
                SmoothScroll.AveragingTime(dragging: _pressed == 1), epsilon: 0.5);
            QueueRedraw();
        }

        // getFadedAlpha: ease the hover fill from ALPHA_LISTBOX_FOCUSED toward FADEALPHA_LISTBOX_FOCUSED at
        // 0.5/s (menu/xonotic/util.qc:816). Keeps repainting while it is still moving.
        if (_focusedItem >= 0)
        {
            float target = MenuSkin.ListFocusedFadeAlpha;
            float start = MenuSkin.ListFocusedAlpha;
            float next = start < target
                ? Math.Min(_focusedItemAlpha + (float)delta * 0.5f, target)
                : Math.Max(_focusedItemAlpha - (float)delta * 0.5f, target);
            if (!Mathf.IsEqualApprox(next, _focusedItemAlpha))
            {
                _focusedItemAlpha = next;
                QueueRedraw();
            }
        }
    }

    /// <summary>
    /// QC <c>ListBox_updateControlTopBottom</c>: where the scrollbar grabber sits, as fractions of the
    /// control's height — including the correction that keeps a very long list's grabber grabbable.
    /// </summary>
    private void UpdateControlTopBottom()
    {
        double total = GetTotalHeight();
        double view = ViewHeight;
        if (total <= view || view <= 0.0)
        {
            _controlTop = 0.0;
            _controlBottom = 1.0;
            _scrollPos = 0.0;
            return;
        }

        _scrollPos = Math.Clamp(_scrollPos, 0.0, total - view);
        _controlTop = Math.Max(0.0, _scrollPos / total);
        _controlBottom = Math.Min((_scrollPos + view) / total, 1.0);

        double minfactor = 2.0 * MenuSkin.ScrollbarWidth / view;
        double f = _controlBottom - _controlTop;
        if (f < minfactor && f < 1.0)
        {
            f = (minfactor - 1.0) / (f - 1.0);
            _controlTop *= f;
            _controlBottom = _controlBottom * f + (1.0 - f);
        }
    }

    public override void _Draw()
    {
        UpdateControlTopBottom();

        float barW = ScrollbarWidth;
        float contentW = Size.X - barW;

        // ListBox_draw: a flat translucent fill behind the rows — no frame. (COLOR_LISTBOX_BACKGROUND.)
        Color bg = MenuSkin.ListBackground;
        if (bg.A > 0f)
            DrawRect(new Rect2(0f, 0f, contentW, Size.Y), bg);

        if (barW > 0f)
            DrawScrollbar(barW);

        // Draw from the first row intersecting the top edge until we run off the bottom.
        int i = Math.Max(0, GetItemAtPos(_scrollPos));
        double y = GetItemStart(i) - _scrollPos;
        for (; i < ItemCount && y < Size.Y; i++)
        {
            float h = GetItemHeight(i);
            var rect = new Rect2(0f, (float)y, contentW, h);
            bool isSelected = i == _selectedItem;
            bool isFocused = i == _focusedItem;

            if (isSelected && ShowSelection)
                DrawRect(HighlightRect(i, rect), MenuSkin.Fade(MenuSkin.Selection, MenuSkin.SelectionAlpha));
            else if (isFocused)
                DrawRect(HighlightRect(i, rect), MenuSkin.Fade(MenuSkin.ListFocused, _focusedItemAlpha));

            DrawItem(i, rect, isSelected, isFocused);
            y += h;
        }
    }

    /// <summary>
    /// The skinned scrollbar: the <c>_s</c> groove down the whole gutter with the <c>_n</c>/<c>_f</c>/<c>_c</c>
    /// grabber over the visible span — the same three-branch choice ListBox_draw makes, drawn through the same
    /// width-square-caps rendering (<see cref="VertButtonPictureStyleBox"/>) the engine's
    /// <c>draw_VertButtonPicture</c> uses.
    /// </summary>
    private void DrawScrollbar(float barW)
    {
        float x = Size.X - barW;
        StyleBox track = MenuSkin.ScrollbarStyle("s");
        track.Draw(GetCanvasItem(), new Rect2(x, 0f, barW, Size.Y));

        if (GetTotalHeight() <= ViewHeight)
            return;
        string state = _pressed == 1 ? "c" : (_focusedItem >= 0 || HasFocus()) ? "f" : "n";
        var grabber = new Rect2(x, (float)(_controlTop * Size.Y), barW,
            (float)((_controlBottom - _controlTop) * Size.Y));
        MenuSkin.ScrollbarStyle(state).Draw(GetCanvasItem(), grabber);
    }
}
