using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Multiplayer dialog — C# successor to <c>XonoticMultiplayerDialog</c> (dialog_multiplayer.qc): one
/// full-width row of three tabs, <b>Servers / Create / Profile</b>, over the frameless tab body.
///
/// The Servers tab is the faithful port of <c>XonoticServerListTab_fill</c> (dialog_multiplayer_join.qc):
/// filter row (Categories, Filter box, Empty/Full/Laggy, Refresh, Pause), the five sort-header buttons
/// (Ping/Hostname/Map/Type/Players) over a real columned list, the Address + Bookmark + Info row, and the
/// bottom Leave-match/Join! row. Rows come from the shared <see cref="ServerBrowser"/> model (favorites +
/// LAN sweep + the master-server query) and refresh automatically when the tab first shows, like the QC
/// list does. The Create tab embeds the full <see cref="CreateGameScreen"/> (the QC create tab) and Profile
/// embeds <see cref="DialogMultiplayerProfile"/>.
/// </summary>
public partial class MultiplayerScreen : MenuScreen
{
    // The browser model is process-wide so favorites persist across opening/closing the screen, and the
    // net layer can attach its ConnectRequested handler once.
    public static readonly ServerBrowser Browser = new();

    private XonoticTabs _tabs = null!;
    private ServerListBox _serverList = null!;
    private LineEdit _filterEdit = null!;
    private LineEdit _addressEdit = null!;
    private Button _favoriteButton = null!;
    private Button _leaveButton = null!;
    private Button _joinButton = null!;
    private Button _infoButton = null!;
    private Control _headerBar = null!;

    private int _renderedRevision = -1;
    private string _renderedFilterKey = "";

    /// <summary>
    /// QC <c>nextRefreshTime</c>: opening the Servers tab re-queries the masters, but no more often than every
    /// 10 seconds, so flipping between tabs doesn't hammer them (serverlist.qc <c>focusEnter</c>).
    /// </summary>
    private ulong _nextRefreshMsec;
    private const ulong RefreshCooldownMsec = 10_000;
    private bool _wasVisible;

    // Sort state. The QC default is ping ascending (serverlist.qc draw: setSortOrder(SLIST_FIELD_PING, +1));
    // each column button carries its OWN initial direction, which is why they are listed here rather than all
    // starting ascending. "Type" is deliberately absent: its header button is not a sort at all — see
    // OnTypeClicked.
    private enum SortField { Ping, Name, Map, Players }
    private SortField _sortField = SortField.Ping;
    private int _sortOrder = +1;
    private readonly Button[] _sortButtons = new Button[5];

    /// <summary>The direction a column starts in when you first click it (QC ServerList_*Sort_Click).</summary>
    private static int InitialSortOrder(SortField field) => field == SortField.Ping ? +1 : -1;

    /// <summary>Select a tab by title ("Servers"/"Create"/"Profile"); no-op if not found. Dev/CI capture.</summary>
    public void SelectTab(string title) => _tabs.SelectByTitle(title);

    protected override void BuildUi()
    {
        Name = "MultiplayerScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 18);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Multiplayer"));

        // QC: one row of three equal tab buttons (each 4/3 of 4 columns), then the tab body.
        _tabs = new XonoticTabs();
        _tabs.AddRow();
        _tabs.AddTab("Servers", BuildServersTab());

        var create = new CreateGameScreen { Embedded = true, Menu = Menu, HostProvidesTitle = true };
        _tabs.AddTab("Create", create);

        var profile = new DialogMultiplayerProfile { Embedded = true, Menu = Menu, HostProvidesTitle = true };
        _tabs.AddTab("Profile", profile);

        root.AddChild(_tabs);
    }

    // -------------------------------------------------------------------------------------------------
    //  Servers tab — faithful XonoticServerListTab_fill layout
    // -------------------------------------------------------------------------------------------------

    private Control BuildServersTab()
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);

        // --- filter row: Categories | Filter: [box] | Empty Full Laggy | Refresh | Pause ---------------
        var filter = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        filter.AddThemeConstantOverride("separation", 10);

        var categories = Widgets.CheckBox("menu_slist_categories", "Categories",
            "Group the server list under category headings");
        categories.Toggled += _ =>
        {
            // QC ServerList_Categories_Click also clears the address box, since the row you had picked is
            // about to move.
            _addressEdit.Text = "";
            InvalidateRender();
        };
        filter.AddChild(categories);

        var filterLabel = MakeLabel("Filter:");
        filterLabel.VerticalAlignment = VerticalAlignment.Center;
        filter.AddChild(filterLabel);

        _filterEdit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2f };
        _filterEdit.TextChanged += _ => InvalidateRender();
        filter.AddChild(_filterEdit);

        AddFilterCheck(filter, "menu_slist_showempty", "Empty", "Show empty servers");
        AddFilterCheck(filter, "menu_slist_showfull", "Full", "Show full servers that have no slots available");
        AddFilterCheck(filter, "menu_slist_showlaggy", "Laggy", "Show high latency servers");

        var refresh = MakeButton("Refresh", OnRefresh);
        refresh.TooltipText = Localization.Tr("Reload the server list");
        refresh.SizeFlagsHorizontal = SizeFlags.Fill; // compact, like the QC 0.8-column button
        refresh.CustomMinimumSize = new Vector2(110, 30);
        filter.AddChild(refresh);

        var pause = Widgets.CheckBox("net_slist_pause", "Pause",
            "Pause updating the server list to prevent servers from \"jumping around\"");
        filter.AddChild(pause);

        col.AddChild(filter);

        // --- the five sort-header buttons over the columned list (QC sortButton1..5) -------------------
        // Not an HBox: the QC positions each button to line up exactly with the column it heads
        // (positionSortButton), including the leading gap for the icon strip, so this mirrors that — a bare
        // Control whose children are placed from the list's own column geometry.
        _headerBar = new Control { CustomMinimumSize = new Vector2(0, 28) };
        string[] titles = { "Ping", "Hostname", "Map", "Type", "Players" };
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            // ClipText: a Godot Button refuses to lay out narrower than its label, and "Players" is wider than
            // the five character cells its column gets — without this the last two headers overlap each other
            // instead of sitting over their columns. The QC condenses the label instead (Label_recalcPos);
            // clipping is the closest Godot equivalent and keeps the alignment, which is the point of the row.
            var b = new Button
            {
                Text = Localization.Tr(titles[i]),
                FocusMode = FocusModeEnum.None,
                ClipText = true,
            };
            b.Pressed += () => OnHeaderClicked(idx);
            _sortButtons[i] = b;
            _headerBar.AddChild(b);
        }
        _sortButtons[3].TooltipText = Localization.Tr("Cycle the gametype filter through the game modes");
        _headerBar.Resized += LayoutSortButtons;
        col.AddChild(_headerBar);

        _serverList = new ServerListBox { SizeFlagsVertical = SizeFlags.ExpandFill };
        _serverList.ItemActivated += _ => OnConnect();        // double-click / Enter = Join (QC doubleClick)
        _serverList.ItemSelected += _ => OnRowSelected();      // echo the address into the box (QC setSelected)
        _serverList.InfoRequested += server => ShowInfo(server.Address);
        _serverList.FavoriteToggleRequested += server => ToggleFavorite(server.Address);
        _serverList.ColumnsChanged += LayoutSortButtons;
        col.AddChild(_serverList);

        // --- Address: [box] [Bookmark] [Info...] (QC rows-2) --------------------------------------------
        var addr = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        addr.AddThemeConstantOverride("separation", 10);
        var addrLabel = MakeLabel("Address:");
        addrLabel.VerticalAlignment = VerticalAlignment.Center;
        addr.AddChild(addrLabel);
        _addressEdit = new LineEdit
        {
            PlaceholderText = "ip:port",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2.2f,
        };
        _addressEdit.TextChanged += _ => UpdateFavoriteButton();
        _addressEdit.TextSubmitted += _ => OnConnect(); // QC onEnter = Connect
        addr.AddChild(_addressEdit);

        _favoriteButton = MakeButton("Favorite", OnToggleFavorite);
        _favoriteButton.SizeFlagsStretchRatio = 1.1f;
        addr.AddChild(_favoriteButton);

        _infoButton = MakeButton("Info...", OnInfo);
        _infoButton.TooltipText = Localization.Tr("Show more information about the currently highlighted server");
        _infoButton.SizeFlagsStretchRatio = 1.1f;
        addr.AddChild(_infoButton);
        col.AddChild(addr);

        // --- bottom row: Leave current match | Join! (QC last row) --------------------------------------
        _leaveButton = MakeButton("Leave current match", () => MenuCommand.Run("disconnect"));
        _joinButton = MakeButton("Join!", OnConnect);
        col.AddChild(MakeButtonBar(_leaveButton, _joinButton));

        UpdateFavoriteButton();
        UpdateSortButtons();
        return col;
    }

    /// <summary>
    /// Place each header button over the column it labels — the port of
    /// <c>XonoticServerList_positionSortButton</c>, which does the same arithmetic in the dialog's coordinate
    /// space. Re-run whenever the list recomputes its columns (i.e. on every resize).
    /// </summary>
    private void LayoutSortButtons()
    {
        if (_headerBar is null || _serverList is null)
            return;
        float h = _headerBar.Size.Y;
        (float Origin, float Width)[] columns =
        {
            (_serverList.ColumnPingOrigin, _serverList.ColumnPingSize),
            (_serverList.ColumnNameOrigin, _serverList.ColumnNameSize),
            (_serverList.ColumnMapOrigin, _serverList.ColumnMapSize),
            (_serverList.ColumnTypeOrigin, _serverList.ColumnTypeSize),
            (_serverList.ColumnPlayersOrigin, _serverList.ColumnPlayersSize),
        };
        for (int i = 0; i < _sortButtons.Length; i++)
        {
            _sortButtons[i].Position = new Vector2(columns[i].Origin, 0f);
            _sortButtons[i].Size = new Vector2(columns[i].Width, h);
        }
    }

    private void AddFilterCheck(HBoxContainer row, string cvar, string label, string tooltip)
    {
        var cb = Widgets.CheckBox(cvar, label, tooltip);
        cb.Toggled += _ => InvalidateRender();
        row.AddChild(cb);
    }

    // -------------------------------------------------------------------------------------------------
    //  List rendering: filter + sort the browser rows into the listbox
    // -------------------------------------------------------------------------------------------------

    private void InvalidateRender() => _renderedRevision = -1;

    /// <summary>
    /// Pump the browser's async master/server replies each frame and re-render when rows changed (or the
    /// filter/sort changed). Also re-queries the masters whenever the tab becomes visible, rate-limited to
    /// once per <see cref="RefreshCooldownMsec"/> — the port of <c>XonoticServerList_focusEnter</c>, which is
    /// what makes the list fill itself in without the user ever pressing Refresh.
    /// </summary>
    public override void _Process(double delta)
    {
        // The refresh is on the visibility EDGE, not on a timer: Refresh() rebuilds the list from scratch, so
        // re-running it while the tab is open would blank the rows (and throw away their measured pings) every
        // few seconds. QC hangs it off focusEnter for the same reason.
        bool visible = IsVisibleInTree();
        bool becameVisible = visible && !_wasVisible;
        _wasVisible = visible;
        if (!visible)
            return;

        if (becameVisible && Time.GetTicksMsec() >= _nextRefreshMsec)
            OnRefresh();

        bool paused = MenuState.Cvars.GetFloat("net_slist_pause") != 0f;
        if (!paused)
            Browser.Poll();

        _leaveButton.Disabled = MenuCommand.InMatch is null || !MenuCommand.InMatch();

        // QC XonoticServerList_draw: Join and Favorite need an address, Info needs the address to actually be
        // the selected row's (`owned`) — you can't show details for a server nobody has queried.
        bool hasAddress = !string.IsNullOrWhiteSpace(_addressEdit.Text);
        _joinButton.Disabled = !hasAddress;
        _favoriteButton.Disabled = !hasAddress;
        _infoButton.Disabled = !hasAddress
                               || Browser.FindByAddress(ServerBrowser.NormalizeAddress(_addressEdit.Text)) is null;

        string filterKey = FilterKey();
        if (Browser.Revision != _renderedRevision || filterKey != _renderedFilterKey)
            RenderServers(filterKey);
    }

    private string FilterKey() =>
        $"{_filterEdit.Text}|{MenuState.Cvars.GetFloat("menu_slist_showempty")}|{MenuState.Cvars.GetFloat("menu_slist_showfull")}|" +
        $"{MenuState.Cvars.GetFloat("menu_slist_showlaggy")}|{MenuState.Cvars.GetFloat("menu_slist_categories")}|" +
        $"{MenuState.Cvars.GetString("menu_slist_modfilter")}|{(int)_sortField}|{_sortOrder}";

    private void OnRefresh()
    {
        _nextRefreshMsec = Time.GetTicksMsec() + RefreshCooldownMsec;
        GD.Print("[Menu] Refreshing server list (favorites + LAN + internet master query).");
        Browser.Refresh();
        InvalidateRender();
    }

    /// <summary>
    /// A column header was clicked. Four of the five set the sort order; "Type" (index 3) does not sort at all
    /// — in the QC it is <c>ServerList_TypeSort_Click</c>, which cycles the gametype prefix of the FILTER box
    /// through the registered game modes, and its button is the one <c>setSortOrder</c> always leaves
    /// un-pressed (serverlist.qc:710).
    /// </summary>
    private void OnHeaderClicked(int index)
    {
        if (index == 3)
        {
            OnTypeClicked();
            return;
        }
        SortField field = index switch
        {
            0 => SortField.Ping,
            1 => SortField.Name,
            2 => SortField.Map,
            _ => SortField.Players,
        };
        // QC setSortOrder: clicking the active column flips it; a new column starts in its own direction.
        _sortOrder = _sortField == field ? -_sortOrder : InitialSortOrder(field);
        _sortField = field;
        _serverList.SetScrollTop();
        UpdateSortButtons();
        InvalidateRender();
    }

    /// <summary>
    /// QC <c>ServerList_TypeSort_Click</c>: step the filter box's <c>gametype:</c> prefix to the next
    /// registered game mode (wrapping), keeping whatever text follows the colon.
    /// </summary>
    private void OnTypeClicked()
    {
        (string current, string rest) = ServerListInfo.SplitFilter(_filterEdit.Text);

        var types = new List<string>();
        foreach (GameType gt in GameTypes.All)
            types.Add(gt.NetName);
        if (types.Count == 0)
            return;

        int at = types.FindIndex(t => string.Equals(t, current, StringComparison.OrdinalIgnoreCase));
        // Not currently filtering by type (or on the last one) → start over at the first.
        string next = at < 0 || at + 1 >= types.Count ? types[0] : types[at + 1];

        _filterEdit.Text = rest.Length > 0 ? $"{next}:{rest}" : $"{next}:";
        _filterEdit.CaretColumn = _filterEdit.Text.Length;
        InvalidateRender();
    }

    /// <summary>
    /// Repaint the header buttons so the active sort column reads as pressed. The QC does this with
    /// <c>forcePressed</c>, which makes Button_draw pick the CLICKED graphic — the same look this gives it.
    /// </summary>
    private void UpdateSortButtons()
    {
        int active = _sortField switch
        {
            SortField.Ping => 0,
            SortField.Name => 1,
            SortField.Map => 2,
            _ => 4,
        };
        for (int i = 0; i < _sortButtons.Length; i++)
        {
            // Index 3 (Type) is never marked: it is a filter cycler, not a sort.
            if (i == active && i != 3)
                _sortButtons[i].AddThemeStyleboxOverride("normal", MenuSkin.ButtonPicture("c"));
            else
                _sortButtons[i].RemoveThemeStyleboxOverride("normal");
        }
    }

    /// <summary>
    /// Filter + sort the browser's rows and hand them to the listbox, which inserts the category headings.
    /// The filter set is the QC's (serverlist.qc <c>refreshServerList</c>): the three visibility checkboxes,
    /// the <c>menu_slist_modfilter</c> cvar, a <c>gametype:</c> prefix, and free text matched against the
    /// hostname, map, gametype OR the connected players' names.
    /// </summary>
    private void RenderServers(string filterKey)
    {
        _renderedRevision = Browser.Revision;
        _renderedFilterKey = filterKey;

        bool showEmpty = MenuState.Cvars.GetFloat("menu_slist_showempty") != 0f;
        bool showFull = MenuState.Cvars.GetFloat("menu_slist_showfull") != 0f;
        bool showLaggy = MenuState.Cvars.GetFloat("menu_slist_showlaggy") != 0f;
        float maxPing = MenuState.Cvars.GetFloat("menu_slist_maxping");
        string modFilter = MenuState.Cvars.GetString("menu_slist_modfilter").Trim();
        (string typeFilter, string needle) = ServerListInfo.SplitFilter(_filterEdit.Text);

        var rows = new List<ServerEntry>();
        foreach (ServerEntry s in Browser.Servers)
        {
            // A bookmark or a server on this LAN is always listed: it is there because the player put it there
            // or because it is one hop away, and hiding it behind the "empty servers" filter helps nobody.
            bool alwaysShow = s.Favorite || s.IsLan;

            // Drop the rows the master named but that never answered our getinfo probe. They are not servers
            // that are merely slow: measured against the live dpmaster list, 42% of what it returns never
            // replies at all — stale registrations, hosts behind a firewall, machines long gone — and
            // re-probing the silent ones recovers exactly none of them. All such a row can show is its own IP
            // address (no name, map, gametype or player count), and it cannot be joined, so it is noise that
            // buries the real entries. Everything that DOES answer is back inside ~200 ms (p90), so this
            // costs the list nothing but the first moment after a refresh. A bookmark is exempt: the player
            // asked for it by name, and "your saved server is not answering" is worth showing.
            if (!s.Queried && !s.Favorite) continue;

            if (!showFull && s.FreeSlots < 1) continue;
            if (!showEmpty && s.Humans < 1 && !alwaysShow) continue;
            if (!showLaggy && maxPing > 0 && s.Ping > maxPing) continue;
            if (typeFilter.Length > 0 && !string.Equals(s.Gametype, typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (modFilter.Length > 0)
            {
                bool negated = modFilter.StartsWith('!');
                string want = negated ? modFilter[1..] : modFilter;
                bool same = string.Equals(s.ModName, want, StringComparison.OrdinalIgnoreCase);
                if (same == negated) continue;
            }
            if (needle.Length > 0
                && !Contains(s.Name, needle) && !Contains(s.Map, needle)
                && !Contains(s.Gametype, needle) && !Contains(s.PlayerNames, needle))
                continue;
            rows.Add(s);
        }

        // sethostcachesort(field, SLSF_CATEGORIES | …): the category is the PRIMARY key, so the headings come
        // out contiguous whatever column the player is sorting by.
        rows.Sort((a, b) =>
        {
            int byCategory = ServerBrowser.CategoryOverride(a.Category)
                .CompareTo(ServerBrowser.CategoryOverride(b.Category));
            return byCategory != 0 ? byCategory : _sortOrder * Compare(a, b);
        });

        _serverList.SetServers(rows);
        _serverList.RefreshSeenAddressFamilies();
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private int Compare(ServerEntry a, ServerEntry b) => _sortField switch
    {
        // An unqueried row sorts as ping 0 — so a fresh list starts with everything at the top and each row
        // drops into place as its reply lands. That churn IS what Base does (the host cache has no
        // "unmeasured" state), and it is why net_slist_pause exists.
        SortField.Ping => a.PingOrZero.CompareTo(b.PingOrZero),
        SortField.Name => string.Compare(MenuColorCodes.Strip(a.Name), MenuColorCodes.Strip(b.Name), StringComparison.OrdinalIgnoreCase),
        SortField.Map => string.Compare(a.Map, b.Map, StringComparison.OrdinalIgnoreCase),
        SortField.Players => a.Humans.CompareTo(b.Humans),
        _ => 0,
    };

    // -------------------------------------------------------------------------------------------------
    //  Row/address actions
    // -------------------------------------------------------------------------------------------------

    /// <summary>The address of the selected row, or null when the list is empty.</summary>
    private string? SelectedRowAddress() => _serverList.SelectedServer?.Address;

    private void OnRowSelected()
    {
        // QC: selecting a row loads its address into the box (setSelected → ipAddressBox.setText).
        if (SelectedRowAddress() is { } address)
        {
            _addressEdit.Text = address;
            UpdateFavoriteButton();
        }
    }

    /// <summary>The address to act on: the typed field if non-empty, else the selected row's address.</summary>
    private string TargetAddress()
        => !string.IsNullOrWhiteSpace(_addressEdit.Text) ? _addressEdit.Text : SelectedRowAddress() ?? "";

    private void OnConnect()
    {
        string address = TargetAddress();

        // Stop before the connect, not after: the browser's internet list comes from the shared Xonotic
        // masters, so most rows are Darkplaces servers this build cannot speak to. Letting the attempt
        // proceed would spend the connection timeout and then report a generic failure, which tells the
        // player nothing about why. Only a row we actually queried can be judged — a typed address, or a
        // bookmark that hasn't answered yet, still goes through.
        if (Browser.FindByAddress(ServerBrowser.NormalizeAddress(address)) is { IsIncompatibleXonotic: true } server)
        {
            GD.Print($"[Menu] Join refused: {server.Address} is a Xonotic server (no VortexArena protocol tag).");
            Menu?.Push(new DialogIncompatibleServer(server.Name, server.Address));
            return;
        }

        string? target = Browser.Connect(address);
        if (target is null)
            GD.Print("[Menu] Join: no address entered or selected.");
        else
            GD.Print($"[Menu] Connecting to {target}.");
    }

    private void OnInfo() => ShowInfo(TargetAddress());

    private void ShowInfo(string address)
    {
        if (address.Length > 0)
            Menu?.Push(new DialogServerInfo(ServerBrowser.NormalizeAddress(address)));
    }

    /// <summary>QC ServerList_Favorite_Click + Update_favoriteButton: one button toggling bookmark state.</summary>
    private void OnToggleFavorite() => ToggleFavorite(TargetAddress());

    private void ToggleFavorite(string rawAddress)
    {
        string address = ServerBrowser.NormalizeAddress(rawAddress);
        if (address.Length == 0)
            return;
        // Toggling in place rather than re-querying: the star changed, not the server. (Re-querying also threw
        // away every ping the list had collected, which is why the row order used to jump on a bookmark.)
        Browser.ToggleFavorite(address);
        UpdateFavoriteButton();
        InvalidateRender();
    }

    private void UpdateFavoriteButton()
    {
        string address = ServerBrowser.NormalizeAddress(TargetAddress());
        bool favorite = address.Length > 0 && Browser.IsFavorite(address);
        _favoriteButton.Text = Localization.Tr(favorite ? "Remove favorite" : "Favorite");
        _favoriteButton.TooltipText = Localization.Tr(favorite
            ? "Remove the currently highlighted server from bookmarks"
            : "Bookmark the currently highlighted server so that it's faster to find in the future");
    }
}
