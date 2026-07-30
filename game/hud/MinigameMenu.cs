using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Hud;

/// <summary>
/// The in-game minigame menu — the Godot successor to CSQC's <c>HUD_MinigameMenu_*</c>
/// (Base/.../qcsrc/common/minigames/cl_minigames_hud.qc). A collapsible list panel that lets the player
/// Create a new game, Join an existing session, or act on the Current Game (the per-game actions + Quit), and
/// otherwise Exit the menu — exactly the entries QC's <c>HUD_MinigameMenu_Open</c>/<c>_CurrentButton</c> build.
///
/// Each leaf click emits a <see cref="SendCommand"/> with the bare arguments for a <c>cmd minigame …</c> line
/// (the analogue of QC <c>minigame_cmd</c>, a <c>localcmd("cmd minigame "+…)</c>); <see cref="MinigameClient"/>
/// prefixes "minigame " and hands it to the net layer:
/// <list type="bullet">
///   <item>Create → <c>create &lt;netname&gt;</c> for each <see cref="Minigames.All"/> (QC ClickCreate_Entry).</item>
///   <item>Join → <c>join &lt;netname&gt;</c> for each known live session (QC ClickJoin_Entry).</item>
///   <item>Current Game → the per-game actions (QC each game's <c>menu_show</c> CustomEntry list) + <c>end</c>
///         (Quit; QC ClickQuit = deactivate + minigame_cmd("end")).</item>
/// </list>
///
/// The per-game action lists are the exact QC <c>menu_show</c> entries (pong.qc:699 / ttt.qc:699 / bd.qc:1434 /
/// pp.qc:613; C4 has none) mapped through each game's <c>menu_click</c> to the server cmd token:
///   Pong: Start Match→<c>throw</c>, Add AI→<c>pong_aimore</c>, Remove AI→<c>pong_ailess</c>;
///   TTT: Next Match→<c>next</c>, Single Player→<c>singleplayer</c>;
///   PP: Next Match→<c>next</c>;  BD: Next Level→<c>next</c>, Restart→<c>restart</c>, Editor→<c>edit</c>, Save→<c>save</c>.
/// </summary>
public partial class MinigameMenu : Control
{
    /// <summary>Emits the bare arguments for a <c>cmd minigame …</c> line (QC minigame_cmd). The net layer
    /// prefixes "minigame ". E.g. "create pong", "join ttt_3", "throw", "end".</summary>
    public event Action<string>? SendCommand;

    /// <summary>Raised when the Join submenu opens, so the client can refresh the live-session list from the
    /// server (QC: the Join menu walks the networked sessions; here we round-trip <c>cmd minigame list-sessions</c>).</summary>
    public event Action? RequestSessionList;

    /// <summary>Supplies the known live session netnames for the Join submenu (wired by <see cref="MinigameClient"/>).</summary>
    public Func<IReadOnlyCollection<string>>? LiveSessions { get; set; }

    private MinigameSession? _active;

    // One row in the flat, indentable list (QC the hud_minigamemenu_entry/subentry edicts).
    private sealed class Row
    {
        public string Label = "";
        public Action? Click;
        public int Indent;             // 0 = top-level, 1 = a sub-entry
        public bool Expandable;        // a collapsible header (Create/Join/Current Game)
        public bool Expanded;
        public string Group = "";      // which expandable owns this (so opening one collapses the others)
    }

    private readonly List<Row> _rows = new();
    private int _hover = -1;

    private const float RowHeight = 26f;
    private const float PanelWidth = 240f;
    private const int FontSize = 16;

    public override void _Ready()
    {
        // A fixed panel on the left edge (QC the MINIGAMEMENU HUD panel). Hidden until toggled open.
        Position = new Vector2(24f, 120f);
        Size = new Vector2(PanelWidth, 480f);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    /// <summary>QC <c>HUD_MinigameMenu_IsOpened</c>.</summary>
    public bool IsOpen => Visible;

    /// <summary>Record the locally-active session so the "Current Game" entry can offer its per-game actions
    /// (QC tracks active_minigame). Rebuilds the menu if it's open.</summary>
    public void SetActive(MinigameSession? session)
    {
        _active = session;
        if (Visible)
            Rebuild();
    }

    /// <summary>QC the <c>minigame</c> bind: toggle the menu open/closed (HUD_MinigameMenu_Open/Close).</summary>
    public void Toggle()
    {
        if (Visible) Close();
        else Open();
    }

    /// <summary>QC <c>HUD_MinigameMenu_Open</c>: show the menu and build the top-level entries.</summary>
    public void Open()
    {
        Visible = true;
        MouseFilter = MouseFilterEnum.Stop;
        Rebuild();
    }

    /// <summary>QC <c>HUD_MinigameMenu_Close</c>: hide the menu and drop its entries.</summary>
    public void Close()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        _rows.Clear();
        _hover = -1;
        QueueRedraw();
    }

    // =====================================================================================
    //  Entry construction (QC HUD_MinigameMenu_Open / _CurrentButton / the Click* expanders)
    // =====================================================================================

    private void Rebuild()
    {
        // Preserve which expandable groups were open across a rebuild (so a snapshot tick doesn't collapse them).
        var open = new HashSet<string>();
        foreach (Row r in _rows)
            if (r is { Expandable: true, Expanded: true })
                open.Add(r.Group);

        _rows.Clear();

        // QC: Create + Join are always present.
        AddExpandable("Create", "create", open.Contains("create"), BuildCreateChildren);
        AddExpandable("Join", "join", open.Contains("join"), BuildJoinChildren);

        // QC HUD_MinigameMenu_CurrentButton: a "Current Game" header iff active, else an "Exit Menu" entry.
        if (_active is not null)
            AddExpandable("Current Game", "current", open.Contains("current"), BuildCurrentChildren);
        else
            _rows.Add(new Row { Label = "Exit Menu", Indent = 0, Click = Close });

        QueueRedraw();
    }

    private void AddExpandable(string label, string group, bool expanded, Action<List<Row>> buildChildren)
    {
        var header = new Row { Label = label, Indent = 0, Expandable = true, Expanded = expanded, Group = group };
        header.Click = () => ToggleGroup(header);
        _rows.Add(header);
        if (expanded)
        {
            // QC ClickJoin fires a fresh session walk each open; mirror it by asking for a list refresh.
            if (group == "join")
                RequestSessionList?.Invoke();
            buildChildren(_rows);
        }
    }

    private void ToggleGroup(Row header)
    {
        bool willOpen = !header.Expanded;
        // QC Click_ExpandCollapse: opening one top-level expandable collapses the others.
        if (willOpen)
            foreach (Row r in _rows)
                if (r is { Expandable: true })
                    r.Expanded = false;
        header.Expanded = willOpen;
        Rebuild();
    }

    // QC ClickCreate: one sub-entry per registered minigame → "create <netname>".
    private void BuildCreateChildren(List<Row> rows)
    {
        foreach (Minigame m in Minigames.All)
        {
            string net = m.NetName;
            rows.Add(new Row { Label = m.DisplayName, Indent = 1, Click = () => SendCommand?.Invoke($"create {net}") });
        }
    }

    // QC ClickJoin: one sub-entry per live session (other than the active one) → "join <netname>".
    private void BuildJoinChildren(List<Row> rows)
    {
        IReadOnlyCollection<string> sessions = LiveSessions?.Invoke() ?? System.Array.Empty<string>();
        bool any = false;
        foreach (string net in sessions)
        {
            if (_active is not null && string.Equals(net, _active.NetName, StringComparison.Ordinal))
                continue;
            any = true;
            rows.Add(new Row { Label = net, Indent = 1, Click = () => SendCommand?.Invoke($"join {net}") });
        }
        if (!any)
            rows.Add(new Row { Label = "(no sessions)", Indent = 1 });
    }

    // QC ClickCurrentGame: the per-game menu_show CustomEntry list + Quit (deactivate + minigame_cmd("end")).
    private void BuildCurrentChildren(List<Row> rows)
    {
        foreach ((string label, string token) in CurrentGameActions(_active!.Game.NetName))
            rows.Add(new Row { Label = label, Indent = 1, Click = () => SendCommand?.Invoke(token) });
        // QC ClickQuit: deactivate_minigame() (client hides the board on the next empty snapshot) + "end".
        rows.Add(new Row { Label = "Quit", Indent = 1, Click = () => SendCommand?.Invoke("end") });
    }

    /// <summary>The per-game "Current Game" actions — the exact QC <c>menu_show</c> entries mapped through each
    /// game's <c>menu_click</c> to the server cmd token. C4 has no menu_show (only Quit/Invite).</summary>
    private static IEnumerable<(string Label, string Token)> CurrentGameActions(string game) => game switch
    {
        "pong" => new (string, string)[]
        {
            ("Start Match", "throw"),       // QC pong menu_click "pong_throw" → minigame_cmd("throw")
            ("Add AI player", "pong_aimore"),
            ("Remove AI player", "pong_ailess"),
        },
        "ttt" => new (string, string)[]
        {
            ("Next Match", "next"),
            ("Single Player", "singleplayer"),
        },
        "pp" => new (string, string)[] { ("Next Match", "next") },
        "bd" => new (string, string)[]
        {
            ("Next Level", "next"),
            ("Restart", "restart"),
            ("Editor", "edit"),
            ("Save", "save"),
        },
        _ => System.Array.Empty<(string, string)>(),
    };

    // =====================================================================================
    //  Draw + input (QC HUD_MinigameMenu draw + HUD_MinigameMenu_MouseInput)
    // =====================================================================================

    public override void _Draw()
    {
        if (!Visible || _rows.Count == 0)
            return;

        float h = HeaderHeight + _rows.Count * RowHeight + 8f;
        var panel = new Rect2(Vector2.Zero, new Vector2(PanelWidth, h));
        DrawRect(panel, new Color(0.08f, 0.1f, 0.14f, 0.85f));
        DrawRect(panel, new Color(1f, 1f, 1f, 0.35f), filled: false, width: 2f);

        // QC HUD_MinigameMenu draws the "Minigames" title.
        DrawString(ThemeDB.FallbackFont, new Vector2(8f, 22f), "Minigames",
            HorizontalAlignment.Left, -1f, 20, new Color(0.25f, 0.47f, 0.72f));

        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            float y = HeaderHeight + i * RowHeight;
            if (i == _hover && r.Click is not null)
                DrawRect(new Rect2(2f, y, PanelWidth - 4f, RowHeight), new Color(1f, 1f, 1f, 0.10f));

            string prefix = r.Expandable ? (r.Expanded ? "- " : "+ ") : "";
            float x = 10f + r.Indent * 16f;
            Color c = r.Expandable ? new Color(0.7f, 0.84f, 1f)
                    : r.Click is null ? new Color(0.6f, 0.6f, 0.6f)
                    : new Color(0.9f, 0.9f, 0.9f);
            DrawString(ThemeDB.FallbackFont, new Vector2(x, y + 18f), prefix + r.Label,
                HorizontalAlignment.Left, PanelWidth - x - 4f, FontSize, c);
        }
    }

    private const float HeaderHeight = 30f;

    public override void _GuiInput(InputEvent @event)
    {
        if (!Visible)
            return;

        if (@event is InputEventMouseMotion mm)
        {
            int idx = RowAt(mm.Position);
            if (idx != _hover) { _hover = idx; QueueRedraw(); }
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            int idx = RowAt(mb.Position);
            if (idx >= 0 && idx < _rows.Count)
            {
                _rows[idx].Click?.Invoke();
                AcceptEvent();
            }
        }
    }

    private int RowAt(Vector2 pos)
    {
        if (pos.X < 0f || pos.X > PanelWidth)
            return -1;
        float local = pos.Y - HeaderHeight;
        if (local < 0f)
            return -1;
        int idx = (int)(local / RowHeight);
        return idx >= 0 && idx < _rows.Count ? idx : -1;
    }
}
