using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// The server browser's list widget — a port of <c>XonoticServerList</c>
/// (qcsrc/menu/xonotic/serverlist.qc), sitting on the <see cref="MenuListBox"/> port of the QC listbox it
/// derives from.
///
/// <para>Everything this draws per row comes straight from <c>XonoticServerList_drawListBoxItem</c>: the
/// category headings that break the list up (taller rows, drawn in SKINCOLOR_SERVERLIST_CATEGORY), the icon
/// strip on the left (IP version, mod, purity, stats), the ping-derived row colour interpolated between the
/// three <c>hud_panel_scoreboard_ping_*</c> thresholds, the alpha rules that dim a full or empty server and
/// lift a bookmarked one, and the five text columns laid out by <c>resizeNotify</c>'s character-cell
/// arithmetic.</para>
///
/// <para><b>Not ported:</b> the AES encryption-level icon and the "impossible to connect" tint that goes with
/// it. Both are decided by <c>crypto_getencryptlevel</c> against the <c>crypto_aeslevel</c> cvar, and this port
/// has no d0_blind_id crypto layer for that to report on. Drawing a padlock we cannot actually substantiate
/// would be worse than drawing none, so the column is simply one icon narrower until the crypto layer exists.</para>
/// </summary>
public partial class ServerListBox : MenuListBox
{
    /// <summary>One visible line: a server, optionally the first of its category (which makes the row taller).</summary>
    private readonly struct Row
    {
        public readonly ServerEntry Server;
        public readonly ServerCategory? Heading; // non-null when this row opens a new category

        public Row(ServerEntry server, ServerCategory? heading)
        {
            Server = server;
            Heading = heading;
        }
    }

    private readonly List<Row> _rows = new();

    /// <summary>QC <c>categoriesHeight</c>: how much taller (in base rows) a heading makes its row.</summary>
    private const float CategoriesHeight = 1.25f;

    /// <summary>
    /// QC <c>serversHeight</c>, set every frame in XonoticServerList_draw: 1 row when categories are on,
    /// otherwise the (taller) heading height — with only one heading to show, the rows get the room instead.
    /// </summary>
    private float ServersHeight => MenuState.Cvars.GetFloat("menu_slist_categories") > 0f ? 1f : CategoriesHeight;

    /// <summary>QC <c>iconsSizeFactor</c>.</summary>
    private const float IconsSizeFactor = 0.85f;

    /// <summary>
    /// QC <c>lockedSelectedItem</c>: until the user picks a row, row 0 is "selected" only so that the address
    /// box has something to show — it is deliberately NOT highlighted, and the list does not scroll to follow it.
    /// </summary>
    public bool SelectionLocked { get; private set; } = true;

    /// <summary>The row currently selected, or null when the list is empty.</summary>
    public ServerEntry? SelectedServer =>
        SelectedItem >= 0 && SelectedItem < _rows.Count ? _rows[SelectedItem].Server : null;

    /// <summary>Raised on right-click / middle-click over a row (QC keyDown's K_MOUSE2 / K_MOUSE3 branches).</summary>
    public event Action<ServerEntry>? InfoRequested;
    public event Action<ServerEntry>? FavoriteToggleRequested;

    // --- Column geometry, recomputed on resize exactly as XonoticServerList_resizeNotify does. Public so the
    //     sort-button header can line its five buttons up with the columns, which is what positionSortButton
    //     does in the QC (the buttons live outside the listbox there too).

    public float ColumnIconsOrigin { get; private set; }
    public float ColumnIconsSize { get; private set; }
    public float ColumnPingOrigin { get; private set; }
    public float ColumnPingSize { get; private set; }
    public float ColumnNameOrigin { get; private set; }
    public float ColumnNameSize { get; private set; }
    public float ColumnMapOrigin { get; private set; }
    public float ColumnMapSize { get; private set; }
    public float ColumnTypeOrigin { get; private set; }
    public float ColumnTypeSize { get; private set; }
    public float ColumnPlayersOrigin { get; private set; }
    public float ColumnPlayersSize { get; private set; }

    /// <summary>Raised after the columns are recomputed, so the header bar can follow them.</summary>
    public event Action? ColumnsChanged;

    public ServerListBox()
    {
        ItemHeight = MenuSkin.BodySize * 1.6f; // QC: rowsPerItem * fontSize, with the skin's line height
        Resized += RecomputeColumns;
    }

    public override void _Ready()
    {
        base._Ready();
        RecomputeColumns();
    }

    // -----------------------------------------------------------------------------------------------------
    //  Contents
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replace the visible rows with <paramref name="servers"/> (already filtered and sorted by the screen),
    /// inserting a heading wherever the category changes. Honours
    /// <c>menu_slist_categories_onlyifmultiple</c>: a single heading over the whole list is noise, so the QC
    /// drops it (serverlist.qc:533). Keeps the previously selected server selected if it is still present,
    /// which is what stops rows jumping out from under the cursor as ping replies trickle in.
    /// </summary>
    public void SetServers(IReadOnlyList<ServerEntry> servers)
    {
        string previous = SelectedServer?.Address ?? "";

        _rows.Clear();
        ServerCategory? running = null;
        int headings = 0;
        foreach (ServerEntry s in servers)
        {
            ServerCategory cat = ServerBrowser.CategoryOverride(s.Category);
            bool opens = running is null || running.Value != cat;
            if (opens)
                headings++;
            _rows.Add(new Row(s, opens ? cat : null));
            running = cat;
        }

        bool onlyIfMultiple = MenuState.Cvars.GetFloat("menu_slist_categories_onlyifmultiple") != 0f;
        if (headings <= 1 && onlyIfMultiple)
        {
            for (int i = 0; i < _rows.Count; i++)
                _rows[i] = new Row(_rows[i].Server, null);
        }

        ItemCount = _rows.Count;
        // Gaining or losing the scrollbar changes the width the columns divide up, and that happens when the
        // row count crosses the page height — i.e. here, not on a resize.
        RecomputeColumns();

        // Follow the selected server rather than the selected index (QC XonoticServerList_draw's `found` walk).
        if (previous.Length > 0)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Server.Address != previous)
                    continue;
                SetSelectedSilent(i); // the pick did not change, only its index — don't scroll, don't re-notify
                return;
            }
        }
        QueueRedraw();
    }

    /// <summary>QC <c>XonoticServerList_setSelected</c>: a real pick unlocks the initial no-op selection.</summary>
    public override void SetSelected(int i)
    {
        SelectionLocked = false;
        base.SetSelected(i);
    }

    /// <inheritdoc/>
    protected override bool ShowSelection => !SelectionLocked;

    /// <summary>Jump back to the top (QC setSortOrder resets selectedItem to 0 when the order changes).</summary>
    public void SetScrollTop()
    {
        SetScrollImmediate(0);
        SetSelectedSilent(0);
        SelectionLocked = true;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Variable row heights (QC getTotalHeight / getItemAtPos / getItemStart / getItemHeight)
    // -----------------------------------------------------------------------------------------------------

    private float HeadingCount()
    {
        int n = 0;
        foreach (Row r in _rows)
            if (r.Heading is not null)
                n++;
        return n;
    }

    public override float GetTotalHeight() => ItemHeight * (ServersHeight * _rows.Count + CategoriesHeight * HeadingCount());

    public override float GetItemHeight(int i)
        => ItemHeight * (i >= 0 && i < _rows.Count && _rows[i].Heading is not null
            ? CategoriesHeight + ServersHeight
            : ServersHeight);

    public override float GetItemStart(int i)
    {
        // Linear rather than the QC's reverse scan over a fixed category array: the row list is right here, and
        // a browser list is at most a few thousand rows drawn a couple of dozen at a time.
        float y = 0f;
        int n = Math.Min(i, _rows.Count);
        for (int k = 0; k < n; k++)
            y += GetItemHeight(k);
        return y;
    }

    public override int GetItemAtPos(double pos)
    {
        if (pos < 0 || _rows.Count == 0)
            return 0;
        double y = 0;
        for (int k = 0; k < _rows.Count; k++)
        {
            y += GetItemHeight(k);
            if (pos < y)
                return k;
        }
        return _rows.Count - 1;
    }

    /// <summary>The selection/hover fill covers only the server half of a heading row (QC SET_YRANGE).</summary>
    protected override Rect2 HighlightRect(int index, Rect2 rect)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].Heading is null)
            return rect;
        float headingH = ItemHeight * CategoriesHeight;
        return new Rect2(rect.Position.X, rect.Position.Y + headingH, rect.Size.X, rect.Size.Y - headingH);
    }

    // -----------------------------------------------------------------------------------------------------
    //  Layout (QC XonoticServerList_resizeNotify)
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The QC lays the columns out in character cells: ping 3, map 10, type 4, players 5, the icon strip 5
    /// scaled by iconsSizeFactor, one cell of gap between each, and the hostname takes whatever is left.
    /// </summary>
    private void RecomputeColumns()
    {
        float cw = CellWidth();
        float width = ContentWidth;

        ColumnIconsOrigin = 0f;
        ColumnIconsSize = cw * 5f * IconsSizeFactor;
        ColumnPingSize = cw * 3f;
        ColumnMapSize = cw * 10f;
        ColumnTypeSize = cw * 4f;
        ColumnPlayersSize = cw * 5f;
        ColumnNameSize = Math.Max(cw * 4f,
            width - ColumnPlayersSize - ColumnMapSize - ColumnPingSize - ColumnIconsSize - ColumnTypeSize - 4f * cw);

        // No gap between icons and ping: in practice the icon strip's own padding already separates them.
        ColumnPingOrigin = ColumnIconsOrigin + ColumnIconsSize;
        ColumnNameOrigin = ColumnPingOrigin + ColumnPingSize + cw;
        ColumnMapOrigin = ColumnNameOrigin + ColumnNameSize + cw;
        ColumnTypeOrigin = ColumnMapOrigin + ColumnMapSize + cw;
        ColumnPlayersOrigin = ColumnTypeOrigin + ColumnTypeSize + cw;

        ColumnsChanged?.Invoke();
        QueueRedraw();
    }

    /// <summary>
    /// The width of one character cell. DP's menu font draws on a fixed grid whose cell equals the font size,
    /// so the QC can size a column in "characters"; Godot's Xolonium is proportional, so measure a digit —
    /// every column sized this way (ping, players, map, type) holds digits or gets shortened to fit anyway.
    /// </summary>
    private float CellWidth()
    {
        Font? font = GetThemeDefaultFont();
        int size = GetThemeDefaultFontSize();
        if (font is null || size <= 0)
            return MenuSkin.BodySize * 0.6f;
        return font.GetStringSize("0", HorizontalAlignment.Left, -1, size).X;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Input (QC XonoticServerList_keyDown)
    // -----------------------------------------------------------------------------------------------------

    protected override bool HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Enter or Key.KpEnter:
                if (SelectedServer is not null)
                    Activate(SelectedItem);
                return true;
            // Space and right-click both open the server-info popup for the highlighted row.
            case Key.Space:
                if (SelectedServer is { } info)
                    InfoRequested?.Invoke(info);
                return true;
            // Insert bookmarks it (the keyboard twin of the middle-click below).
            case Key.Insert:
                if (SelectedServer is { } fav)
                    FavoriteToggleRequested?.Invoke(fav);
                return true;
            default:
                return base.HandleKey(key);
        }
    }

    protected override void OnAlternateClick(InputEventMouseButton mb)
    {
        // QC: K_MOUSE2 (or ctrl+MOUSE1) opens the info dialog, K_MOUSE3 toggles the bookmark. Both act on the
        // row under the cursor, so select it first.
        int index = GetItemAtPos(ScrollPos + mb.Position.Y);
        if (index < 0 || index >= _rows.Count)
            return;
        SetSelected(index);
        ServerEntry server = _rows[index].Server;
        if (mb.ButtonIndex == MouseButton.Right || (mb.ButtonIndex == MouseButton.Left && mb.CtrlPressed))
            InfoRequested?.Invoke(server);
        else if (mb.ButtonIndex == MouseButton.Middle)
            FavoriteToggleRequested?.Invoke(server);
    }

    // -----------------------------------------------------------------------------------------------------
    //  Row drawing (QC XonoticServerList_drawListBoxItem)
    // -----------------------------------------------------------------------------------------------------

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _rows.Count)
            return;
        Row row = _rows[index];
        Font? font = GetThemeDefaultFont();
        int fontSize = GetThemeDefaultFontSize();
        if (font is null)
            return;

        float y = rect.Position.Y;
        if (row.Heading is { } heading)
        {
            float headingH = ItemHeight * CategoriesHeight;
            // The QC prints the heading over the hostname column, with a trailing colon.
            DrawText(font, fontSize, new Rect2(ColumnNameOrigin, y, ColumnNameSize, headingH),
                ServerBrowser.CategoryTitle(heading) + ":", MenuSkin.ServerListCategory, HorizontalAlignment.Left);
            y += headingH;
        }
        var body = new Rect2(rect.Position.X, y, rect.Size.X, ItemHeight * ServersHeight);

        ServerEntry s = row.Server;
        float alpha = RowAlpha(s);
        Color color = RowColor(s, ref alpha);

        DrawIcons(s, body);

        // ping — right-aligned against the end of its column
        DrawText(font, fontSize, new Rect2(ColumnPingOrigin, body.Position.Y, ColumnPingSize, body.Size.Y),
            s.PingText, MenuSkin.Fade(color, alpha), HorizontalAlignment.Right);

        // hostname — left-aligned, cut to the column (colour codes stripped: the row's colour is what carries
        // its ping/bookmark state, and a name's own colours would fight with that, exactly as in Base where
        // draw_Text is called with allowColorCodes = 0).
        DrawText(font, fontSize, new Rect2(ColumnNameOrigin, body.Position.Y, ColumnNameSize, body.Size.Y),
            MenuColorCodes.Strip(s.Name), MenuSkin.Fade(color, alpha), HorizontalAlignment.Left);

        DrawText(font, fontSize, new Rect2(ColumnMapOrigin, body.Position.Y, ColumnMapSize, body.Size.Y),
            s.Map, MenuSkin.Fade(color, alpha), HorizontalAlignment.Center);
        DrawText(font, fontSize, new Rect2(ColumnTypeOrigin, body.Position.Y, ColumnTypeSize, body.Size.Y),
            s.Gametype, MenuSkin.Fade(color, alpha), HorizontalAlignment.Center);
        DrawText(font, fontSize, new Rect2(ColumnPlayersOrigin, body.Position.Y, ColumnPlayersSize, body.Size.Y),
            s.PlayersText, MenuSkin.Fade(color, alpha), HorizontalAlignment.Center);
    }

    /// <summary>
    /// QC row alpha: a server you cannot connect to at all is dimmed hardest, one you can connect to but not
    /// yet play on (duel, g_maxplayers) or that nobody is on is dimmed less, everything else is full.
    /// </summary>
    private static float RowAlpha(ServerEntry s)
    {
        if (s.FreeSlots <= 0)
            return MenuSkin.ServerListFullAlpha;
        if (s.QcFreeSlots == 0 || s.Humans == 0)
            return MenuSkin.ServerListEmptyAlpha;
        return 1f;
    }

    /// <summary>
    /// QC row colour: the ping thresholds are the scoreboard's (<c>hud_panel_scoreboard_ping_low/medium/high</c>,
    /// "also applies to server list" per _hud_common.cfg), interpolated between the three skin colours; past
    /// the high threshold the row turns red and additionally fades out. A bookmark then blends the result
    /// toward COLOR_SERVERLIST_FAVORITE so it stands out from the rows around it.
    /// </summary>
    private static Color RowColor(ServerEntry s, ref float alpha)
    {
        float low = MenuState.Cvars.GetFloat("hud_panel_scoreboard_ping_low");
        float med = MenuState.Cvars.GetFloat("hud_panel_scoreboard_ping_medium");
        float high = MenuState.Cvars.GetFloat("hud_panel_scoreboard_ping_high");
        if (med <= low) med = low + 1f;
        if (high <= med) high = med + 1f;

        Color color;
        // A row nobody has heard back from yet has ping 0 in the QC's host cache, and drawListBoxItem colours
        // it from that like any other — so it comes out in the low-ping green until its real ping lands.
        float ping = s.PingOrZero;
        if (ping < low)
            color = MenuSkin.ServerListLowPing;
        else if (ping < med)
            color = MenuSkin.ServerListLowPing.Lerp(MenuSkin.ServerListMedPing, (ping - low) / (med - low));
        else if (ping < high)
            color = MenuSkin.ServerListMedPing.Lerp(MenuSkin.ServerListHighPing, (ping - med) / (high - med));
        else
        {
            color = new Color(1f, 0f, 0f);
            alpha *= 1f + (MenuSkin.ServerListHighPingAlpha - 1f) * Math.Min(1f, (ping - high) / high);
        }

        if (s.Favorite)
        {
            float k = MenuSkin.ServerListFavoriteAlpha;
            color = color.Lerp(MenuSkin.ServerListFavorite, k);
            if (s.FreeSlots > 0)
                alpha = alpha * (1f - k) + k;
        }
        return color;
    }

    /// <summary>
    /// The icon strip: IP version (only once both families are present in the list, so a homogeneous list
    /// isn't cluttered), the mod, and the stats flag — the QC's RENDER ICONS block minus the AES entry this
    /// port has no crypto layer to fill (see the class remarks).
    /// </summary>
    private void DrawIcons(ServerEntry s, Rect2 body)
    {
        float iconSize = MenuSkin.BodySize * IconsSizeFactor;
        bool mixedFamilies = SeenIPv4 && SeenIPv6;
        float slots = mixedFamilies ? 3f : 2f;
        float x = ColumnIconsOrigin + (ColumnIconsSize - slots * iconSize) * 0.5f;
        float y = body.Position.Y + (body.Size.Y - iconSize) * 0.5f;

        void Icon(string name, float a = 1f)
        {
            if (MenuSkin.SkinImage(name) is { } tex)
                DrawTextureRect(tex, new Rect2(x, y, iconSize, iconSize), false, new Color(1f, 1f, 1f, a));
        }

        if (mixedFamilies)
        {
            if (s.IsIPv6) Icon("icon_ipv6");
            else if (s.IsIPv4) Icon("icon_ipv4");
            x += iconSize;
        }

        if (s.ModName == "xonotic")
        {
            // On stock Xonotic the purity flag should always be reported; if it isn't, treat that as impure
            // and draw nothing (the QC does the same).
            if (s.PureAvailable && s.Pure)
                Icon("icon_pure1");
        }
        else if (s.ModName.Length > 0)
        {
            string icon = "icon_mod_" + s.ModName;
            if (MenuSkin.SkinImage(icon) is null)
                icon = "icon_mod_";
            // For a mod, a missing purity report means the mod doesn't implement the check — not impurity.
            Icon(icon, s.PureAvailable && !s.Pure ? MenuSkin.ServerListIconNonPureAlpha : 1f);
        }
        x += iconSize;

        if (s.HasPlayerStats)
            Icon(s.HasCustomStatsServer ? "icon_mod_" : "icon_stats1");
    }

    /// <summary>Whether both IP families appear in the list — QC <c>seenIPv4</c>/<c>seenIPv6</c>.</summary>
    private bool SeenIPv4, SeenIPv6;

    /// <summary>Recount which IP families the current rows use; called by the screen after every rebuild.</summary>
    public void RefreshSeenAddressFamilies()
    {
        SeenIPv4 = SeenIPv6 = false;
        foreach (Row r in _rows)
        {
            SeenIPv4 |= r.Server.IsIPv4;
            SeenIPv6 |= r.Server.IsIPv6;
        }
    }

    /// <summary>
    /// The tooltip the QC shows while the cursor is over the icon strip, describing what the icons mean for
    /// this row (serverlist.qc:1050).
    /// </summary>
    public string IconTooltip(ServerEntry s)
    {
        var parts = new List<string>();
        if (SeenIPv4 && SeenIPv6)
            parts.Add(s.IsIPv6 ? "IPv6" : "IPv4");
        string mod = s.ModName is "" or "xonotic" ? Localization.Tr("Default") : s.ModName;
        if (s.PureAvailable)
            mod += s.Pure ? $" ({Localization.Tr("official settings")})" : $" ({Localization.Tr("modified settings")})";
        parts.Add($"{Localization.Tr("mod")}: {mod}");
        parts.Add(Localization.Tr(s.HasPlayerStats ? "stats enabled" : "stats disabled"));
        return string.Join(", ", parts);
    }

    /// <summary>True when <paramref name="localX"/> falls inside the icon strip (QC <c>mouseOverIcons</c>).</summary>
    public bool IsOverIcons(float localX)
    {
        float iconSize = MenuSkin.BodySize * IconsSizeFactor;
        float slots = SeenIPv4 && SeenIPv6 ? 3f : 2f;
        float pad = (ColumnIconsSize - slots * iconSize) * 0.5f;
        return localX >= ColumnIconsOrigin + pad && localX <= ColumnIconsOrigin + ColumnIconsSize - pad;
    }

    /// <summary>
    /// QC <c>XonoticServerList_mouseMove</c> + <c>focusedItemChangeNotify</c>: the icon strip explains itself
    /// on hover, and the tooltip is dropped the moment the cursor leaves it (or moves to another row).
    /// </summary>
    protected override void OnMouseMoved(Vector2 pos)
    {
        int index = GetItemAtPos(ScrollPos + pos.Y);
        TooltipText = IsOverIcons(pos.X) && index >= 0 && index < _rows.Count
            ? IconTooltip(_rows[index].Server)
            : "";
    }

    // -----------------------------------------------------------------------------------------------------
    //  Text helper
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Draw one column's text, vertically centred in <paramref name="cell"/> and cut to its width (the QC's
    /// <c>draw_TextShortenToWidth</c>, which truncates rather than eliding).
    /// </summary>
    private void DrawText(Font font, int fontSize, Rect2 cell, string text, Color color, HorizontalAlignment align)
    {
        if (string.IsNullOrEmpty(text))
            return;
        text = ShortenToWidth(font, fontSize, text, cell.Size.X);
        // GetStringSize gives the full line box; centring on the ascent puts the glyphs' optical middle on the
        // row's middle, which is what the QC's realUpperMargin does.
        float baseline = cell.Position.Y + (cell.Size.Y + font.GetAscent(fontSize) - font.GetDescent(fontSize)) * 0.5f;
        DrawString(font, new Vector2(cell.Position.X, baseline), text, align, cell.Size.X, fontSize, color);
    }

    private static string ShortenToWidth(Font font, int fontSize, string text, float maxWidth)
    {
        if (maxWidth <= 0f)
            return "";
        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X <= maxWidth)
            return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (font.GetStringSize(text[..mid], HorizontalAlignment.Left, -1, fontSize).X <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }
        return text[..lo];
    }

    /// <summary>Fire the activation event for a row (double-click / Enter, both meaning "join").</summary>
    private void Activate(int index) => EmitActivate(index);
}
