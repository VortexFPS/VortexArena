using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Server Info" — <c>XonoticServerInfoDialog_fill</c> (qcsrc/menu/xonotic/dialog_multiplayer_join_serverinfo.qc) on
/// its 19 x 2 grid, 0.96 of the menu wide: the "Status" and "Terms of Service" tab buttons on row 0, the tab
/// controller over rows 1..17 (<see cref="DialogServerInfoTab"/>, <see cref="DialogServerToSTab"/>), then "Close" and
/// a green "Join!" (<c>connect &lt;address&gt;</c>) sharing the last row. The QC's bookmark button is commented out
/// ("TODO: Add bookmark button here"), so there is none.
///
/// <c>loadServerInfo</c> reads the selected row of the server browser — here the <see cref="ServerEntry"/> found by
/// address in <see cref="MultiplayerScreen.Browser"/> (the C# host cache). Its qcstatus is parsed the QC way
/// (gametype, version, P/S/F/M/T tokens). What the port's host cache does not carry stays as the QC would show it
/// without that data: the game directory is taken to be "data" (no mod suffix), the crypto library is absent
/// ("N/A (auth library missing)", ID and Key "N/A"), the player list is empty (no SLIST_FIELD_PLAYERS roster), and a
/// server ToS URL is not downloaded (the box stays empty, as Xonotic's does until the fetch returns).
/// </summary>
public partial class DialogServerInfo : MenuScreen, IQcDialog
{
    // dialog_multiplayer_join_serverinfo.qh
    public string QcTitle => Localization.Tr("Server Info");
    public float QcIntendedWidth => 0.96f;
    public float QcRows => 19;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_MULTIPLAYER", 1f, 1f, 1f);

    // me.currentServerCName: what Join_Click connects to.
    private readonly string _serverAddress;
    private readonly ServerEntry? _entry;

    public DialogServerInfo() : this("") { }

    /// <summary>Open the info for a specific server address (QC: the selected row's CNAME).</summary>
    public DialogServerInfo(string serverAddress)
    {
        _serverAddress = serverAddress ?? "";
        _entry = MultiplayerScreen.Browser.FindByAddress(_serverAddress);
    }

    protected override void BuildUi()
    {
        Name = "DialogServerInfo";

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        var info = new DialogServerInfoTab(Load(_entry, _serverAddress));
        var tos = new DialogServerToSTab(ToSText(_entry));

        var mc = new QcTabController();
        me.TR();
            me.TD(1, 1, mc.MakeTabButton("Status", info));
            me.TD(1, 1, mc.MakeTabButton("Terms of Service", tos));

        me.TR();
            me.TD(me.Rows - 2, me.Columns, mc);

        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns / 2, Qc.Button("Close", GoBack));
            me.TD(1, me.Columns / 2, Qc.Button("Join!", OnJoin, tint: new Color(0, 1, 0)));
    }

    /// <summary>QC <c>Join_Click</c>: <c>localcmd("connect ", me.currentServerCName, "\n")</c>.</summary>
    private void OnJoin()
    {
        string address = _entry?.Address ?? _serverAddress;
        if (!string.IsNullOrEmpty(address))
            MenuCommand.Run($"connect {address}");
    }

    /// <summary>The ToS box text loadServerInfo sets: the QC default unless the server names a ToS URL.</summary>
    private static string ToSText(ServerEntry? e)
    {
        string url = "";
        bool specified = false;
        foreach ((char k, string v) in StatusTokens(e?.QcStatus ?? "", out _, out _))
            if (k == 'T') { specified = true; url = v; }
        if (!specified || url == "INVALID")
            return Localization.Tr("No Terms of Service specified");
        return ""; // AdditionalServerInfo_OnGet fills it once downloaded; the port does not fetch it
    }

    /// <summary>The qcstatus tokens after "gametype:version", up to the first empty one, as (key, value).</summary>
    private static List<(char Key, string Value)> StatusTokens(string qcstatus, out string? type, out string? version)
    {
        var list = new List<(char, string)>();
        type = version = null;
        string[] argv = qcstatus.Length > 0 ? qcstatus.Split(':') : Array.Empty<string>();
        if (argv.Length >= 2)
        {
            type = argv[0];
            version = argv[1];
        }
        for (int j = 2; j < argv.Length; ++j)
        {
            if (argv[j].Length == 0) break;
            list.Add((argv[j][0], argv[j][1..]));
        }
        return list;
    }

    /// <summary>XonoticServerInfoDialog_loadServerInfo: the texts of the Status tab's value labels.</summary>
    private static DialogServerInfoTab.Values Load(ServerEntry? e, string address)
    {
        var v = new DialogServerInfoTab.Values { CName = e?.Address ?? address };
        if (e is null) return v;

        v.Name = e.Name;
        string na = Localization.Tr("N/A");
        bool pureAvailable = false;
        float pureViolations = -1, freeslots = -1, sflags = -1;
        string modname = "";
        List<(char Key, string Value)> tokens = StatusTokens(e.QcStatus, out string? type, out string? version);
        string typestr = type ?? (e.Gametype.Length > 0 ? e.Gametype : na);
        string versionstr = version ?? (e.Version.Length > 0 ? e.Version : na);
        foreach ((char k, string val) in tokens)
        {
            switch (k)
            {
                case 'P': pureAvailable = true; pureViolations = Stof(val); break;
                case 'S': freeslots = Stof(val); break;
                case 'F': sflags = Stof(val); break;
                case 'M': modname = val; break;
            }
        }
        // SLIST_FIELD_MOD (the game directory) is not in the port's host cache: taken to be "data", no suffix.

        foreach (GameType gt in GameTypes.All) // MapInfo_Type_FromString → MapInfo_Type_ToText
            if (string.Equals(gt.NetName, typestr, StringComparison.OrdinalIgnoreCase) && gt.DisplayName.Length > 0)
            {
                typestr = gt.DisplayName;
                break;
            }
        v.Type = typestr;
        v.Map = e.Map;

        int numh = e.Humans, maxp = e.MaxPlayers, numb = e.Bots;
        v.NumPlayers = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", numh, maxp);
        v.NumBots = numb.ToString(CultureInfo.InvariantCulture);
        if (freeslots < 0) freeslots = maxp - numh - numb;
        v.NumFreeSlots = QcFormat.MinDecimals(freeslots);
        v.Mod = modname == "Xonotic" ? Localization.CtxTr("MOD^Default") : modname;
        v.Version = versionstr;
        v.Pure = !pureAvailable ? na
            : pureViolations == 0 ? Localization.Tr("Official")
            : Localization.Tr("%d modified").Replace("%d", ((int)pureViolations).ToString(CultureInfo.InvariantCulture));

        // crypto_getencryptlevel: the port has no crypto library.
        CvarService cvars = MenuState.Cvars;
        v.Encrypt = cvars.GetFloat("crypto_aeslevel") >= 3
            ? Localization.Tr("N/A (auth library missing, can't connect)")
            : Localization.Tr("N/A (auth library missing)");
        v.Id = na;
        v.Key = na;

        int flags = (int)sflags;
        v.Stats = sflags >= 0 && (flags & 4) != 0 // SERVERFLAG_PLAYERSTATS
            ? (flags & 8) != 0 ? Localization.Tr("custom stats server") : Localization.Tr("stats enabled") // _CUSTOM
            : Localization.Tr("stats disabled");
        return v;
    }

    private static float Stof(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
}

/// <summary>
/// The "Status" tab — <c>XonoticServerInfoTab_fill</c> (qcsrc/menu/xonotic/dialog_multiplayer_join_serverinfotab.qc)
/// on its 17 x 6.2 grid (intendedWidth 0.8): Hostname (SKINCOLOR_SERVERINFO_NAME) and Address
/// (SKINCOLOR_SERVERINFO_IP) centred across 4.6 columns; Gametype / Map / Mod / Version / Settings and Players / Bots /
/// Free slots in the left block; Encryption / ID / Key / Stats on the last four rows; and from column 2.2 the
/// "Players:" label over the player list. Every value label cuts its text to fit (allowCut).
/// </summary>
public partial class DialogServerInfoTab : QcTab
{
    /// <summary>The texts loadServerInfo puts in the value labels.</summary>
    public sealed class Values
    {
        public string Name = "", CName = "", Type = "", Map = "", Mod = "", Version = "", Pure = "";
        public string NumPlayers = "", NumBots = "", NumFreeSlots = "", Encrypt = "", Id = "", Key = "", Stats = "";
    }

    private readonly Values _values;

    public DialogServerInfoTab(Values values) : base(rows: 17, columns: 6.2f, intendedWidth: 0.8f)
    {
        _values = values;
    }

    protected override void Fill()
    {
        Values v = _values;
        float alpha = MenuSkin.Text.A;
        TR();
            TD(1, 0.8f, Qc.Label("Hostname:"));
            TD(1, 4.6f, Value(v.Name, 0.5f, MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_SERVERINFO_NAME", 1f, 1f, 1f), alpha))));
        TR();
            TD(1, 0.8f, Qc.Label("Address:"));
            TD(1, 4.6f, Value(v.CName, 0.5f, MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_SERVERINFO_IP", 0.4f, 0.4f, 0.7f), alpha))));

        TR();
        TR();
            TD(1, 0.8f, Qc.Label("Gametype:"));
            TD(1, 1.2f, Value(v.Type));
        TR();
            TD(1, 0.8f, Qc.Label("Map:"));
            TD(1, 1.2f, Value(v.Map));
        TR();
            TD(1, 0.8f, Qc.Label("Mod:"));
            TD(1, 1.2f, Value(v.Mod));
        TR();
            TD(1, 0.8f, Qc.Label("Version:"));
            TD(1, 1.2f, Value(v.Version));
        TR();
            TD(1, 0.8f, Qc.Label("Settings:"));
            TD(1, 1.2f, Value(v.Pure));

        TR();
        TR();
            TD(1, 0.8f, Qc.Label("Players:"));
            TD(1, 1.2f, Value(v.NumPlayers));
        TR();
            TD(1, 0.8f, Qc.Label("Bots:"));
            TD(1, 1.2f, Value(v.NumBots));
        TR();
            TD(1, 0.8f, Qc.Label("Free slots:"));
            TD(1, 1.2f, Value(v.NumFreeSlots));

        GotoRC(Rows - 5, 0);
            TD(1, 0.8f, Qc.Label("Encryption:"));
            TD(1, 5.4f, Value(v.Encrypt)).TooltipText = Localization.Tr("Use the `crypto_aeslevel` cvar to change your preferences");
        TR();
            TD(1, 0.8f, Qc.Label("ID:"));
            TD(1, 5.4f, Value(v.Key)); // keyLabel, as the QC places it
        TR();
            TD(1, 0.8f, Qc.Label("Key:"));
            TD(1, 5.4f, Value(v.Id));  // idLabel
        TR();
            TD(1, 0.8f, Qc.Label("Stats:"));
            TD(1, 5.4f, Value(v.Stats));

        GotoRC(2, 2.2f); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Label("Players:"));
        TR();
            TD(Rows - 8, 4, new QcPlayerList());
    }

    /// <summary>makeXonoticTextLabel(align, "") with allowCut, its text set by loadServerInfo.</summary>
    private static QcTextLabel Value(string text, float align = 0f, Color? color = null)
    {
        var l = new QcTextLabel("", align) { AllowCut = true, Text = text };
        if (color is { } c) l.Color = c;
        // A value label is a plain (non-interactive) label in the QC, but the Encryption one carries a tooltip.
        l.MouseFilter = MouseFilterEnum.Pass;
        return l;
    }
}

/// <summary>
/// The "Terms of Service" tab — <c>XonoticServerToSTab_fill</c> (qcsrc/menu/xonotic/dialog_multiplayer_join_termsofservice.qc)
/// on its 17 x 6.2 grid: one colour-coded text box over the whole tab.
/// </summary>
public partial class DialogServerToSTab : QcTab
{
    private readonly string _text;

    public DialogServerToSTab(string text) : base(rows: 17, columns: 6.2f, intendedWidth: 0.8f)
    {
        _text = text;
    }

    protected override void Fill()
    {
        TR();
        TD(Rows, Columns, new QcTextBox { AllowColors = true, Text = _text });
    }
}

/// <summary>
/// xonotic/playerlist.qc: one row per player of the server's SLIST_FIELD_PLAYERS ("score ping [team] name" lines) —
/// the score right-aligned in a five-character column after a one-character margin, coloured by team (red 4,
/// blue 13, yellow 12, pink 9 of the colormap palette; gray "spectator"), and the colour-coded name after it,
/// "^9(bot)^7" in front for a zero ping. Rows are drawn at the top of their item (realUpperMargin2 is never set).
/// Nothing is ever highlighted (selectionDoesntMatter).
/// </summary>
public partial class QcPlayerList : MenuListBox
{
    private const int FragsSpectator = -666;

    private readonly List<(string Score, string Ping, string Team, string Name)> _players = new();

    public QcPlayerList()
    {
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
        SelectionDoesntMatter = true;
    }

    protected override bool DrawsOwnHighlight => true;

    /// <summary>XonoticPlayerList_setPlayerList: the newline-separated roster.</summary>
    public void SetPlayerList(string plist)
    {
        _players.Clear();
        string text = plist.EndsWith('\n') ? plist[..^1] : plist;
        if (text.Length > 0)
            foreach (string line in text.Split('\n'))
            {
                List<string> argv = TokenizeConsole(line);
                string Arg(int i) => i < argv.Count ? argv[i] : "";
                _players.Add(argv.Count == 4
                    ? (Arg(0), Arg(1), Arg(2), Arg(3))
                    : (Arg(0), Arg(1), "-1", Arg(2)));
            }
        ItemCount = _players.Count;
        QueueRedraw();
    }

    /// <summary>tokenize_console: whitespace-separated, double quotes group.</summary>
    private static List<string> TokenizeConsole(string s)
    {
        var argv = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            if (s[i] == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                argv.Add(s[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                argv.Add(s[start..i]);
            }
        }
        return argv;
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _players.Count) return;
        (string score, string ping, string team, string name) = _players[index];
        float ch = MenuMetrics.FontNormalU, w = rect.Size.X;
        float scoreSize = 5f * ch, nameSize = w - 3f * ch - scoreSize;
        float scoreOrigin = ch, nameOrigin = scoreOrigin + scoreSize + ch;

        float.TryParse(team, NumberStyles.Float, CultureInfo.InvariantCulture, out float t);
        Color rgb = t switch
        {
            1 => Palette(4),
            2 => Palette(13),
            3 => Palette(12),
            4 => Palette(9),
            _ => MenuSkin.SkinColor("COLOR_TEXT", 0.96f, 0.99f, 1f),
        };

        bool isBot = ping == "0", isSpectator = false;
        if (score.EndsWith(":spectator", StringComparison.Ordinal))
            isSpectator = true;
        else
        {
            int cut = score.IndexOf(':');
            if (cut >= 0) score = score[..cut];
            cut = score.IndexOf(',');
            if (cut >= 0) score = score[..cut];
            if (float.TryParse(score, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s == FragsSpectator)
                isSpectator = true;
        }
        if (isBot) name = "^9(bot)^7" + name;
        if (isSpectator)
        {
            score = Localization.Tr("spectator");
            rgb = new Color(0.3f, 0.3f, 0.3f);
        }

        name = ShortenColored(name, nameSize);
        QcDraw.ColoredText(this, rect.Position + new Vector2(nameOrigin, 0f), name, Colors.White);

        score = QcDraw.ShortenToWidth(score, scoreSize);
        float scoreOfs = scoreSize - QcDraw.TextWidth(score);
        QcDraw.Text(this, rect.Position + new Vector2(scoreOrigin + scoreOfs, 0f), score, MenuSkin.TextLift(rgb));
    }

    private static Color Palette(int index)
    {
        (float r, float g, float b) = CsqcModelAppearance.ColormapPaletteColor(index, false, 0f);
        return new Color(r, g, b);
    }

    /// <summary>draw_TextShortenToWidth with colour codes: the codes take no room.</summary>
    private static string ShortenColored(string text, float width)
    {
        if (QcDraw.TextWidthColors(text) <= width) return text;
        float room = width - QcDraw.TextWidth("...");
        int n = text.Length;
        while (n > 0 && QcDraw.TextWidthColors(text[..n]) > room) n--;
        return text[..n] + "...";
    }
}
