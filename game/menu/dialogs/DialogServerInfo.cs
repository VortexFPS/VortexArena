using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Server-info popup — a faithful C# port of <c>XonoticServerInfoDialog</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_join_serverinfo.qc) together with its two tabs,
/// <c>XonoticServerInfoTab</c> (dialog_multiplayer_join_serverinfotab.qc) and <c>XonoticServerToSTab</c>
/// (dialog_multiplayer_join_termsofservice.qc).
///
/// The QC dialog shows the details of the server currently selected in the server browser: it is filled by
/// <c>XonoticServerInfoDialog_loadServerInfo</c>, which reads every field from the engine's host-cache
/// (<c>gethostcachestring</c>/<c>gethostcachenumber</c> over SLIST_FIELD_*) for the highlighted row. VortexArena
/// has no server browser / host-cache backend wired in yet, so there is no live "selected server" to read:
/// the labelled detail rows are rendered with empty placeholder values plus an honest note, and the player
/// list / ToS text are rendered empty. The layout, headers, tab split, and the Close / Join! buttons are
/// reproduced faithfully.
///
/// Bottom buttons mirror the QC: "Close" (QC <c>Dialog_Close</c>) and "Join!" (QC <c>Join_Click</c> →
/// <c>localcmd("connect ", me.currentServerCName)</c>). The favorite/bookmark button is commented out in the
/// QC ("// TODO: Add bookmark button here") so it is reproduced here as a disabled, noted placeholder.
/// This dialog binds no cvars (it is a static info layout).
/// </summary>
public partial class DialogServerInfo : MenuScreen
{
    // The address of the selected server. QC stores this in me.currentServerCName (host-cache SLIST_FIELD_CNAME)
    // and Join_Click runs `connect <that address>`. Populated from the create/multiplayer screen's selected row.
    private readonly string _serverAddress;

    // The selected server's row, resolved from the shared ServerBrowser model by address (the C# stand-in for
    // the QC host-cache entry the serverinfo dialog reads via gethostcachestring/number). Null when the address
    // was typed manually (never queried), or no server browser is active — then the rows show placeholders.
    private readonly ServerEntry? _entry;

    public DialogServerInfo() : this("") { }

    /// <summary>Open the info popup for a specific server address (QC: the selected row's CNAME).</summary>
    public DialogServerInfo(string serverAddress)
    {
        _serverAddress = serverAddress ?? "";
        // Read the selected row from the shared browser model (append-only lookup). This is the
        // VortexArena.Net ServerEntry the LAN/master probes already filled in (PopulateFromInfo).
        _entry = MultiplayerScreen.Browser.FindByAddress(_serverAddress);
    }

    protected override void BuildUi()
    {
        Name = "DialogServerInfo";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Server Information"));

        // Note reflects whether a live row was resolved from the server browser. When a row IS present the
        // detail rows below carry its real fields (name/address/gametype/map/player counts).
        var note = MakeLabel(_entry is not null
            ? $"(showing details for the selected server: {_serverAddress})"
            : "(no selected server row — type-an-address path: details show placeholders until the server is queried)");
        note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        root.AddChild(note);

        // QC builds a tab controller with two tab buttons: "Status" and "Terms of Service".
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(tabs);

        var status = BuildStatusTab();
        status.Name = "Status";
        tabs.AddChild(status);

        var tos = BuildToSTab();
        tos.Name = "Terms of Service";
        tabs.AddChild(tos);

        // Bottom row: QC has "Close" (left half) and "Join!" (right half); a bookmark/favorite button is
        // present-but-commented-out in the QC, reproduced here as a disabled placeholder.
        var favorite = MakeButton("Add to favorites", AddFavorite);
        favorite.Disabled = true; // QC: button is commented out ("TODO: Add bookmark button here").
        favorite.TooltipText = "Bookmark this server (favorites backend pending)";

        var join = MakeButton("Join!", OnJoin);
        join.TooltipText = "Connect to this server (QC: connect <address>)";

        root.AddChild(MakeButtonBar(
            MakeButton("Close", GoBack),
            favorite,
            join));
    }

    // -----------------------------------------------------------------------------------------------------
    //  "Status" tab — port of XonoticServerInfoTab_fill (dialog_multiplayer_join_serverinfotab.qc).
    //  Each QC row is a "Label:" text-label in column 1 and an (empty) value text-label beside it. The right
    //  half of the QC tab carries the crypto/stats rows and a player list; we lay them in the same order.
    // -----------------------------------------------------------------------------------------------------
    private Control BuildStatusTab()
    {
        // The tab page is a plain Control hosting a ScrollContainer (the page itself is the bounded parent of
        // the scroll, NOT the other way round — we never put the TabContainer inside a ScrollContainer).
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };

        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 16);
        scroll.AddChild(pad);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        pad.AddChild(box);

        // Resolve the displayable fields from the selected ServerEntry (the QC gethostcache* reads). Fields the
        // browser doesn't carry (mod/version/encryption/identity/stats) stay as honest placeholders.
        ServerEntry? e = _entry;
        int humans = e is null ? 0 : System.Math.Max(0, e.Players - e.Bots);
        int freeSlots = e is null ? 0 : System.Math.Max(0, e.MaxPlayers - e.Players);

        // Left block of the QC tab: hostname, address, then a gap, then gametype/map/mod/version/settings,
        // a gap, then player counts.
        box.AddChild(DetailRow("Hostname:", "nameLabel", Decolorize(e?.Name)));         // SLIST_FIELD_NAME
        box.AddChild(DetailRow("Address:", "cnameLabel", e?.Address ?? _serverAddress)); // SLIST_FIELD_CNAME
        box.AddChild(Ui.Spacer(8));
        box.AddChild(DetailRow("Gametype:", "typeLabel", e?.Gametype));                  // QC MapInfo_Type_ToText(typestr)
        box.AddChild(DetailRow("Map:", "mapLabel", e?.Map));                             // SLIST_FIELD_MAP
        box.AddChild(DetailRow("Mod:", "modLabel"));                                     // SLIST_FIELD_MOD — not carried
        box.AddChild(DetailRow("Version:", "versionLabel"));                            // QCSTATUS version field — not carried
        box.AddChild(DetailRow("Settings:", "pureLabel"));                              // QCSTATUS pure flag — not carried
        box.AddChild(Ui.Spacer(8));
        box.AddChild(DetailRow("Players:", "numPlayersLabel",
            e is null ? "" : $"{humans}/{e.MaxPlayers}"));                               // sprintf("%d/%d", numh, maxp)
        box.AddChild(DetailRow("Bots:", "numBotsLabel", e is null ? "" : e.Bots.ToString())); // SLIST_FIELD_NUMBOTS
        box.AddChild(DetailRow("Free slots:", "numFreeSlotsLabel",
            e is null ? "" : freeSlots.ToString()));                                    // maxp - numh - numb
        box.AddChild(Ui.Spacer(8));

        // Right block of the QC tab (encryption/identity/stats). The browser doesn't carry the crypto / stats
        // fields (they come from QCSTATUS / crypto_get* in the QC), so these stay as placeholders.
        box.AddChild(DetailRow("Encryption:", "encryptLabel", null,
            "Use the `crypto_aeslevel` cvar to change your preferences")); // QC setZonedTooltip
        box.AddChild(DetailRow("ID:", "keyLabel"));   // QC labels these crossed: keyLabel under "ID:"
        box.AddChild(DetailRow("Key:", "idLabel"));   // and idLabel under "Key:" (faithful to the QC source).
        box.AddChild(DetailRow("Stats:", "statsLabel"));

        // QC also shows a "Players:" header + a live player list (makeXonoticPlayerList, from SLIST_FIELD_PLAYERS).
        // The browser carries only counts, not the per-player roster, so this remains an honest note.
        box.AddChild(Ui.Spacer(8));
        box.AddChild(Ui.Header("Players"));
        var playerList = MakeLabel("(per-player roster — SLIST_FIELD_PLAYERS not carried by the browser model)");
        playerList.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        box.AddChild(playerList);

        return scroll;
    }

    /// <summary>
    /// A "Label:" + value row mirroring one QC <c>TR</c> of the info tab (a TextLabel followed by a value
    /// TextLabel). <paramref name="value"/> carries the selected row's field (QC <c>gethostcache*</c>); null/empty
    /// leaves it blank (the field the browser doesn't carry). <paramref name="fieldName"/> names the value node
    /// after the QC <c>me.*Label</c> field for traceability.
    /// </summary>
    private static HBoxContainer DetailRow(string label, string fieldName, string? value = null, string tooltip = "")
    {
        var valueLabel = MakeLabel(value ?? "");
        valueLabel.Name = fieldName; // e.g. "nameLabel" — traceability to the QC entity field.
        valueLabel.TooltipText = tooltip;
        return MakeRow(label, valueLabel);
    }

    /// <summary>Strip Quake <c>^</c> color codes from a server name for the plain Label (QC names are decolorized in the UI).</summary>
    private static string Decolorize(string? s) => MenuColorCodes.Strip(s);

    // -----------------------------------------------------------------------------------------------------
    //  "Terms of Service" tab — port of XonoticServerToSTab_fill (dialog_multiplayer_join_termsofservice.qc).
    //  QC is a single full-size text box that color-renders the ToS text fetched from the server's download
    //  URL (url_single_fopen). With no host-cache / urllib backend the default QC text is shown.
    // -----------------------------------------------------------------------------------------------------
    private Control BuildToSTab()
    {
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 16);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        pad.AddChild(box);

        // QC default when no ToS is specified by the server (XonoticServerInfoDialog_loadServerInfo).
        var text = new TextEdit
        {
            Text = "No Terms of Service specified",
            Editable = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        box.AddChild(text);

        var note = MakeLabel("(Terms of Service text is fetched per-server over the network — fetch backend pending)");
        note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        box.AddChild(note);

        return pad;
    }

    // -----------------------------------------------------------------------------------------------------
    //  Actions
    // -----------------------------------------------------------------------------------------------------

    /// <summary>QC <c>Join_Click</c>: <c>localcmd("connect ", me.currentServerCName, "\n")</c>.</summary>
    private void OnJoin()
    {
        // With no selected server there is no live address; only issue the command when one was supplied.
        if (!string.IsNullOrEmpty(_serverAddress))
            MenuCommand.Run($"connect {_serverAddress}");
        else
            GD.Print("[DialogServerInfo] Join!: no server address (server browser backend pending) — inert.");
    }

    /// <summary>QC bookmark button (commented out): would run ServerList_Favorite_Click on the selected row.</summary>
    private void AddFavorite()
    {
        // Inert: favorites backend pending; the QC button itself is commented out.
    }
}
