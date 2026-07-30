using System;
using System.Collections.Generic;
using Godot;

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
    private Tree _serverTree = null!;
    private LineEdit _filterEdit = null!;
    private LineEdit _addressEdit = null!;
    private Button _favoriteButton = null!;
    private Button _leaveButton = null!;

    private bool _refreshedOnce;
    private int _renderedRevision = -1;
    private string _renderedFilterKey = "";

    // Sort state (QC: default ping ascending — serverlist.qc draw: setSortOrder(SLIST_FIELD_PING, +1)).
    private enum SortField { Ping, Name, Map, Type, Players }
    private SortField _sortField = SortField.Ping;
    private int _sortOrder = +1;
    private readonly Button[] _sortButtons = new Button[5];

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
            "Show servers grouped by category (not supported yet — uncategorised list)");
        categories.Toggled += _ => InvalidateRender();
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
        var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 4);
        string[] titles = { "Ping", "Hostname", "Map", "Type", "Players" };
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            var b = new Button
            {
                Text = Localization.Tr(titles[i]),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = ColumnRatio(i),
                CustomMinimumSize = new Vector2(0, 28),
                FocusMode = FocusModeEnum.None,
            };
            b.Pressed += () => OnSortClicked((SortField)idx);
            _sortButtons[i] = b;
            header.AddChild(b);
        }
        col.AddChild(header);

        _serverTree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Columns = 5,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            FocusMode = FocusModeEnum.None,
        };
        for (int i = 0; i < 5; i++)
        {
            _serverTree.SetColumnExpand(i, true);
            _serverTree.SetColumnExpandRatio(i, Mathf.RoundToInt(ColumnRatio(i) * 100));
        }
        _serverTree.ItemActivated += OnConnect;     // double-click / Enter = Join (QC doubleClick)
        _serverTree.ItemSelected += OnRowSelected;  // echo the address into the box (QC setSelected)
        col.AddChild(_serverTree);

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

        _favoriteButton = MakeButton("Bookmark", OnToggleFavorite);
        _favoriteButton.SizeFlagsStretchRatio = 1.1f;
        addr.AddChild(_favoriteButton);

        var info = MakeButton("Info...", OnInfo);
        info.TooltipText = Localization.Tr("Show more information about the currently highlighted server");
        info.SizeFlagsStretchRatio = 1.1f;
        addr.AddChild(info);
        col.AddChild(addr);

        // --- bottom row: Leave current match | Join! (QC last row) --------------------------------------
        _leaveButton = MakeButton("Leave current match", () => MenuCommand.Run("disconnect"));
        var join = MakeButton("Join!", OnConnect);
        col.AddChild(MakeButtonBar(_leaveButton, join));

        return col;
    }

    private void AddFilterCheck(HBoxContainer row, string cvar, string label, string tooltip)
    {
        var cb = Widgets.CheckBox(cvar, label, tooltip);
        cb.Toggled += _ => InvalidateRender();
        row.AddChild(cb);
    }

    /// <summary>QC column proportions (serverlist.qc resizeNotify): ping 3ch, name the rest, map 10ch, type 4ch, players 5ch.</summary>
    private static float ColumnRatio(int column) => column switch
    {
        0 => 0.09f,  // Ping
        1 => 0.50f,  // Hostname
        2 => 0.20f,  // Map
        3 => 0.09f,  // Type
        4 => 0.12f,  // Players
        _ => 0.1f,
    };

    // -------------------------------------------------------------------------------------------------
    //  List rendering: filter + sort the browser rows into the Tree
    // -------------------------------------------------------------------------------------------------

    private void InvalidateRender() => _renderedRevision = -1;

    /// <summary>
    /// Pump the browser's async master/server replies each frame and re-render when rows changed (or the
    /// filter/sort changed). Also auto-refreshes ONCE when the tab first becomes visible — the QC list
    /// refreshes when it first draws, so the user never stares at an empty pane.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!IsVisibleInTree())
            return;

        if (!_refreshedOnce)
        {
            _refreshedOnce = true;
            OnRefresh();
        }

        bool paused = MenuState.Cvars.GetFloat("net_slist_pause") != 0f;
        if (!paused)
            Browser.Poll();

        _leaveButton.Disabled = MenuCommand.InMatch is null || !MenuCommand.InMatch();

        string filterKey = FilterKey();
        if (Browser.Revision != _renderedRevision || filterKey != _renderedFilterKey)
            RenderServers(filterKey);
    }

    private string FilterKey() =>
        $"{_filterEdit.Text}|{MenuState.Cvars.GetFloat("menu_slist_showempty")}|{MenuState.Cvars.GetFloat("menu_slist_showfull")}|" +
        $"{MenuState.Cvars.GetFloat("menu_slist_showlaggy")}|{(int)_sortField}|{_sortOrder}";

    private void OnRefresh()
    {
        GD.Print("[Menu] Refreshing server list (favorites + LAN + internet master query).");
        Browser.Refresh();
        InvalidateRender();
    }

    private void OnSortClicked(SortField field)
    {
        // QC setSortOrder: clicking the active column flips the order, a new column starts ascending.
        if (_sortField == field) _sortOrder = -_sortOrder;
        else { _sortField = field; _sortOrder = +1; }
        InvalidateRender();
    }

    /// <summary>Filter + sort + pour the rows into the Tree (the QC drawListBoxItem columns).</summary>
    private void RenderServers(string filterKey)
    {
        _renderedRevision = Browser.Revision;
        _renderedFilterKey = filterKey;

        string selectedAddress = SelectedRowAddress() ?? "";

        var rows = new List<ServerEntry>();
        bool showEmpty = MenuState.Cvars.GetFloat("menu_slist_showempty") != 0f;
        bool showFull = MenuState.Cvars.GetFloat("menu_slist_showfull") != 0f;
        bool showLaggy = MenuState.Cvars.GetFloat("menu_slist_showlaggy") != 0f;
        float maxPing = MenuState.Cvars.GetFloat("menu_slist_maxping");
        if (maxPing <= 0) maxPing = 300;
        string needle = _filterEdit.Text.Trim();

        foreach (ServerEntry s in Browser.Servers)
        {
            int humans = Math.Max(0, s.Players - s.Bots);
            if (!showEmpty && humans == 0 && !s.Favorite && !s.IsLan) continue;
            if (!showFull && s.MaxPlayers > 0 && s.Players >= s.MaxPlayers) continue;
            if (!showLaggy && s.Ping > maxPing) continue;
            if (needle.Length > 0
                && !Contains(s.Name, needle) && !Contains(s.Map, needle) && !Contains(s.Gametype, needle))
                continue;
            rows.Add(s);
        }

        rows.Sort((a, b) => _sortOrder * Compare(a, b));

        _serverTree.Clear();
        TreeItem rootItem = _serverTree.CreateItem();
        TreeItem? reselect = null;
        foreach (ServerEntry s in rows)
        {
            TreeItem item = _serverTree.CreateItem(rootItem);
            item.SetText(0, s.PingText);
            item.SetCustomColor(0, PingColor(s.Ping));
            item.SetText(1, (s.Favorite ? "★ " : s.IsLan ? "LAN " : "") + MenuColorCodes.Strip(s.Name));
            item.SetText(2, s.Map);
            item.SetText(3, s.Gametype);
            item.SetText(4, s.PlayersText);
            item.SetTextAlignment(0, HorizontalAlignment.Right);
            item.SetTextAlignment(3, HorizontalAlignment.Center);
            item.SetTextAlignment(4, HorizontalAlignment.Center);
            item.SetMetadata(0, s.Address);
            if (s.Address == selectedAddress)
                reselect = item;
        }
        reselect?.Select(0);
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private int Compare(ServerEntry a, ServerEntry b) => _sortField switch
    {
        // Unknown pings sort to the bottom of the ascending list (QC: unqueried servers trail).
        SortField.Ping => (a.Ping < 0 ? int.MaxValue : a.Ping).CompareTo(b.Ping < 0 ? int.MaxValue : b.Ping),
        SortField.Name => string.Compare(MenuColorCodes.Strip(a.Name), MenuColorCodes.Strip(b.Name), StringComparison.OrdinalIgnoreCase),
        SortField.Map => string.Compare(a.Map, b.Map, StringComparison.OrdinalIgnoreCase),
        SortField.Type => string.Compare(a.Gametype, b.Gametype, StringComparison.OrdinalIgnoreCase),
        SortField.Players => (a.Players - a.Bots).CompareTo(b.Players - b.Bots),
        _ => 0,
    };

    /// <summary>The QC ping tint: green when snappy, fading to red as latency climbs.</summary>
    private static Color PingColor(int ping) => ping switch
    {
        < 0 => new Color(0.7f, 0.7f, 0.7f, 0.6f),
        < 70 => new Color(0.45f, 0.95f, 0.45f),
        < 140 => new Color(0.95f, 0.95f, 0.45f),
        < 200 => new Color(0.95f, 0.65f, 0.30f),
        _ => new Color(0.95f, 0.35f, 0.30f),
    };

    // -------------------------------------------------------------------------------------------------
    //  Row/address actions
    // -------------------------------------------------------------------------------------------------

    /// <summary>The address of the selected Tree row, or null when nothing is selected.</summary>
    private string? SelectedRowAddress()
    {
        TreeItem? sel = _serverTree.GetSelected();
        if (sel is null) return null;
        Variant meta = sel.GetMetadata(0);
        return meta.VariantType == Variant.Type.String ? meta.AsString() : null;
    }

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
        string? target = Browser.Connect(TargetAddress());
        if (target is null)
            GD.Print("[Menu] Join: no address entered or selected.");
        else
            GD.Print($"[Menu] Connecting to {target}.");
    }

    private void OnInfo()
    {
        string address = TargetAddress();
        if (address.Length > 0)
            Menu?.Push(new DialogServerInfo(ServerBrowser.NormalizeAddress(address)));
    }

    /// <summary>QC ServerList_Favorite_Click + Update_favoriteButton: one button toggling bookmark state.</summary>
    private void OnToggleFavorite()
    {
        string address = ServerBrowser.NormalizeAddress(TargetAddress());
        if (address.Length == 0)
            return;
        if (Browser.IsFavorite(address)) Browser.RemoveFavorite(address);
        else Browser.AddFavorite(address);
        Browser.Refresh();
        UpdateFavoriteButton();
        InvalidateRender();
    }

    private void UpdateFavoriteButton()
    {
        string address = ServerBrowser.NormalizeAddress(TargetAddress());
        _favoriteButton.Text = Localization.Tr(
            address.Length > 0 && Browser.IsFavorite(address) ? "Unbookmark" : "Bookmark");
    }
}
