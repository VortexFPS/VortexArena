using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;          // CvarFlags
using VortexArena.Engine.Simulation;        // CvarService
using VortexArena.Game.Menu;                // MenuState.Cvars (live console/menu store)

namespace VortexArena.Game.Hud;

/// <summary>
/// QuickMenu panel — the C# successor to QuakeC's <c>HUD_QuickMenu</c> (the #QUICKMENU HUD panel,
/// Base/.../qcsrc/client/hud/panel/quickmenu.qc). It is the in-game "quick chat" nested list: pressing the
/// <c>quickmenu</c> bind pops up a small list of chat macros / settings toggles / vote calls organized into
/// submenus, navigable by the number keys (1-9 pick a row, 0 pages forward) or by mouse hover + click. Picking
/// a leaf row runs its console command; picking a submenu row descends into it.
///
/// Faithful pieces ported from the QC:
/// <list type="bullet">
///   <item>The built-in <b>default menu tree</b> (<see cref="BuildDefault"/>, QC <c>QuickMenu_Default</c>):
///         Chat / Team chat / private message / Settings (View-HUD, Sound, Fullscreen) / Call a vote, each a
///         submenu of chat-macro or <c>toggle</c>/<c>vcall</c> commands.</item>
///   <item>The <b>paging</b> model (QC <c>QuickMenu_Page_Load</c>): at most <see cref="MaxLines"/> rows per page;
///         a non-final page shows only <c>MaxLines-2</c> rows plus a blank row and a "Continue..." row so 0
///         pages forward.</item>
///   <item>The <b>command/submenu/special</b> coloring (QC <c>HUD_QuickMenu</c>): submenu rows ^4, command rows
///         ^3, "special" rows ^6, the "Continue..." row ^5; an activated row briefly flashes green; the
///         hovered row highlights.</item>
///   <item><c>toggle</c> rows draw a <b>checkbox</b> (checked / empty / undefined) reflecting the live cvar
///         value (QC <c>QM_PCT_TOGGLE</c>), and <c>KEEP_OPEN</c> / toggle rows keep the menu open after firing.</item>
///   <item>The <c>_align</c> (row text alignment 0..1) and <c>_translatecommands</c> / <c>_server_is_default</c>
///         behaviour cvars + the <c>_time</c> idle timeout.</item>
/// </list>
///
/// Wiring contract (the integration layer does this later, NOT this file): the host binds the <c>quickmenu</c>
/// key to <see cref="Toggle"/>, points <see cref="CommandSink"/> at its console (the analogue of QC
/// <c>localcmd</c>), and may set <see cref="Teamplay"/>/<see cref="SpectateeStatus"/> so the tree shows the
/// right submenus. Until opened the panel draws nothing (self-blank, per the contract for new panels).
/// </summary>
public partial class QuickMenuPanel : HudPanel
{
    /// <summary>QC <c>QUICKMENU_MAXLINES</c> — visible rows per page (must be ≤ 10).</summary>
    public const int MaxLines = 10;

    // QuickMenu_Buffer entry tags (QC QM_TAG_*). A submenu open/close is one tagged token; a command is a
    // Title token immediately followed by a command token tagged C/K/special.
    private enum Tag { Title, Submenu, Command, KCommand }

    // The per-row command kind (QC QM_PCT_*): NONE fires-and-closes, KEEP keeps the menu open, TOGGLE flips a
    // cvar and keeps open (+ draws a checkbox).
    private enum CmdType { None, Toggle, Keep }

    /// <summary>One token in the flat menu buffer (QC the strings in <c>QuickMenu_Buffer</c>).</summary>
    private readonly struct Token
    {
        public readonly Tag Tag;
        public readonly string Text;     // submenu name, or a row title, or a command string
        public readonly bool Special;    // QC "special" command (leading "\n") → ^6 color
        public Token(Tag tag, string text, bool special = false) { Tag = tag; Text = text; Special = special; }
    }

    /// <summary>One drawn page row (QC <c>QuickMenu_Page_*</c> parallel arrays, 1-based in the QC).</summary>
    private sealed class PageEntry
    {
        public string Description = "";  // QC QuickMenu_Page_Description[i]
        public string Command = "";      // QC QuickMenu_Page_Command[i] ("" → it's a submenu row)
        public bool Special;             // ^6 special command
        public CmdType Type = CmdType.None;
    }

    // ---- the loaded menu (built once on Open) ----
    private readonly List<Token> _buffer = new();

    // ---- the current page (QC QuickMenu_Page_*) ----
    private readonly List<PageEntry> _page = new();   // index 0 == QC entry 1
    private string _currentSubMenu = "";
    private int _pageIndex;
    private bool _isLastPage = true;
    private bool _isOpen;

    // ---- interaction state ----
    private int _hover = -1;                            // hovered row (0-based into _page; -1 = none)
    private double _activatedTime;                      // QC QuickMenu_Page_ActivatedEntry_Time (flash window)
    private int _activatedRow = -1;                     // 0-based row that flashed
    private bool _pendingClose;                         // QC QuickMenu_Page_ActivatedEntry_Close (close after flash)
    private double _timeOut;                            // QC QuickMenu_TimeOut (absolute; 0 = never)
    private double _clock;                              // self-contained clock (driven by _Process)

    // ---- host-supplied context (so the tree matches the match; default = sensible singleplayer) ----

    /// <summary>QC <c>quickmenu</c> bind sink: the analogue of <c>localcmd</c> — the host points this at its
    /// console so a picked row's command actually runs. Receives the raw command string (may contain ';').</summary>
    public Action<string>? CommandSink { get; set; }

    /// <summary>QC <c>teamplay</c>: when true the "Team chat" submenu (and team-only vote rows) appear.</summary>
    public bool Teamplay { get; set; }

    /// <summary>QC <c>spectatee_status</c>: 0 playing, &gt;0 spectating a player, -1 free-fly observer. Gates the
    /// spectator-camera rows + the "Spectate a player" submenu.</summary>
    public int SpectateeStatus { get; set; }

    /// <summary>QC <c>STAT(TIMELIMIT) &gt; 0</c>: a match time limit exists (adds the reduce/extend vote rows).</summary>
    public bool HasTimeLimit { get; set; } = true;

    /// <summary>Names of the other players (QC the entcs player list) so the per-player submenus
    /// ("Send private message to", "Spectate a player") can be populated. Optional; empty = those rows omitted.</summary>
    public IReadOnlyList<string> PlayerNames { get; set; } = Array.Empty<string>();

    /// <summary>QC <c>QuickMenu_IsOpened</c> — the panel is showing its list.</summary>
    public bool IsOpen => _isOpen && _page.Count > 0;

    public override bool IsDynamic => true;

    // =====================================================================================
    //  Lifecycle
    // =====================================================================================

    public override void _Process(double delta)
    {
        // Advance the self-contained clock. Guard against a non-finite / negative delta (a pause-resume hitch or
        // a debugger step can hand Godot a garbage frame time) — a NaN here would poison every timeout/flash
        // comparison below and freeze the menu permanently.
        if (double.IsFinite(delta) && delta > 0)
            _clock += delta;

        // Nothing to do (and nothing to redraw) while closed — the panel self-blanks.
        if (!_isOpen)
            return;

        // QC HUD_QuickMenu_Forbidden: an idle timeout auto-closes the menu.
        if (_timeOut > 0 && _clock > _timeOut)
        {
            Close();
            return;
        }

        UpdateHover();

        // Resolve any pending activation flash (QC HUD_QuickMenu end-of-draw): once the 0.1s flash window has
        // elapsed, clear the flashed row and, if the activated command asked to close, finally close the menu.
        if (_activatedRow >= 0 && _clock >= _activatedTime)
        {
            _activatedRow = -1;
            _activatedTime = 0;
            if (_pendingClose)
            {
                _pendingClose = false;
                Close();
                return;
            }
        }

        QueueRedraw();
    }

    /// <summary>QC <c>quickmenu</c> bind toggles the menu (open if closed, close if open).</summary>
    public void Toggle()
    {
        if (_isOpen) Close();
        else Open();
    }

    /// <summary>QC <c>QuickMenu_Open(default)</c> — build the default tree and show page 0 of the root menu.
    /// Optionally open straight into a named submenu (QC <c>quickmenu default &lt;submenu&gt;</c>).</summary>
    public bool Open(string submenu = "")
    {
        BuildDefault();
        if (_buffer.Count == 0) return false;

        _isOpen = true;
        if (!LoadPage(submenu, newPage: false))
        {
            Close();
            return false;
        }
        // Capture input while shown (HudManager left MouseFilter=Ignore; we eat clicks only while open). The menu
        // is modal while open, so grab keyboard focus too — otherwise the digit/Esc keys never reach _GuiInput
        // (a Control receives mouse events by rect but key events only while focused). Released again in Close().
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        GrabFocus();
        ResetTimeout();
        QueueRedraw();
        return true;
    }

    /// <summary>QC <c>QuickMenu_Close</c> — hide the menu and drop its state. Self-blanks the panel.</summary>
    public void Close()
    {
        _isOpen = false;
        _page.Clear();
        _buffer.Clear();
        _currentSubMenu = "";
        _pageIndex = 0;
        _isLastPage = true;
        _hover = -1;
        _activatedRow = -1;
        _activatedTime = 0;
        _pendingClose = false;
        _timeOut = 0;
        MouseFilter = MouseFilterEnum.Ignore;
        if (HasFocus()) ReleaseFocus();
        FocusMode = FocusModeEnum.None;
        QueueRedraw();
    }

    private void ResetTimeout()
    {
        float t = CvarF("time", 0f);
        _timeOut = t > 0 ? _clock + t : 0;
    }

    // =====================================================================================
    //  Default menu tree (QC QuickMenu_Default)
    // =====================================================================================

    // QC QUICKMENU_SMENU: a submenu is delimited by two identical Submenu tokens (open … close).
    private void SMenu(string name) => _buffer.Add(new Token(Tag.Submenu, name));

    // QC QUICKMENU_ENTRY: a Title token followed by a Command token.
    private void Entry(string title, string command)
    {
        _buffer.Add(new Token(Tag.Title, title));
        _buffer.Add(new Token(Tag.Command, command));
    }

    // QC QUICKMENU_ENTRY_TAG(…, QM_TAG_KCOMMAND): a KEEP_OPEN command.
    private void EntryKeep(string title, string command)
    {
        _buffer.Add(new Token(Tag.Title, title));
        _buffer.Add(new Token(Tag.KCommand, command));
    }

    // QC QUICKMENU_ENTRY_SPECIAL: shown with a different (^6) color.
    private void EntrySpecial(string title, string command)
    {
        _buffer.Add(new Token(Tag.Title, title));
        _buffer.Add(new Token(Tag.Command, command, special: true));
    }

    /// <summary>Build the default quick-menu into <see cref="_buffer"/> — a faithful transcription of QC
    /// <c>QuickMenu_Default</c> (the say/say_team macros, the settings toggles, the vote calls), gated on the
    /// host-supplied <see cref="Teamplay"/>/<see cref="SpectateeStatus"/>/<see cref="HasTimeLimit"/> context.</summary>
    private void BuildDefault()
    {
        _buffer.Clear();

        // --- Chat --- (QC: the "Send public message to" player-list submenu is the FIRST entry inside Chat,
        //     emitted by QUICKMENU_SMENU_PL as a self-contained open…title…command…close run.)
        SMenu("Chat");
            PlayerSubMenu("Send public message to", "commandmode say %s:^7 ");
            Entry("nice one", "say :-) / nice one");
            Entry("good game", "say good game");
            Entry("hi / good luck", "say hi / good luck and have fun");
        SMenu("Chat");

        // --- Team chat (teamplay only) ---
        if (Teamplay)
        {
            SMenu("Team chat");
                Entry("strength soon", "say_team strength soon");
                Entry("free item, icon", "say_team free item %x^7 (l:%y^7); waypoint_here_crosshair");
                Entry("took item, icon", "say_team took item (l:%l^7); waypoint_here_here");
                Entry("negative", "say_team negative");
                Entry("positive", "say_team positive");
                Entry("need help, icon",
                    "say_team need help (l:%l^7) (h:%h^7 a:%a^7 w:%w^7); waypoint_here_follow; cmd voice needhelp");
                Entry("enemy seen, icon",
                    "say_team enemy seen (l:%y^7); waypoint_danger_crosshair; cmd voice incoming");
                Entry("flag seen, icon",
                    "say_team flag seen (l:%y^7); waypoint_here_crosshair; cmd voice seenflag");
                Entry("defending, icon",
                    "say_team defending (l:%l^7) (h:%h^7 a:%a^7 w:%w^7); waypoint_here_here");
                Entry("roaming, icon",
                    "say_team roaming (l:%l^7) (h:%h^7 a:%a^7 w:%w^7); waypoint_here_here");
                Entry("attacking, icon",
                    "say_team attacking (l:%l^7) (h:%h^7 a:%a^7 w:%w^7); waypoint_here_here");
                Entry("killed flagcarrier, icon",
                    "say_team killed flagcarrier (l:%y^7); waypoint_here_crosshair");
                Entry("dropped flag, icon",
                    "say_team dropped flag (l:%d^7); waypoint_here_death");
                Entry("drop weapon, icon",
                    "say_team dropped gun %w^7 (l:%l^7); waypoint_here_here; wait; weapon_drop");
                Entry("drop flag/key, icon",
                    "say_team dropped flag/key %w^7 (l:%l^7); waypoint_here_here; wait; use");
            SMenu("Team chat");
        }

        // QC: top-level per-player "Send private message to" (after Team chat).
        PlayerSubMenu("Send private message to", "commandmode tell \"%s^7\" ");

        // --- Settings ---
        SMenu("Settings");
            SMenu("View/HUD settings");
                Entry("3rd person view", "toggle chase_active");
                Entry("Player models like mine", "toggle cl_forceplayermodels");
                Entry("Names above players", "toggle hud_shownames");
                Entry("Crosshair per weapon", "toggle crosshair_per_weapon");
                Entry("FPS", "toggle hud_panel_engineinfo");
                Entry("Net graph", "toggle shownetgraph");
            SMenu("View/HUD settings");

            SMenu("Sound settings");
                Entry("Hit sound", "toggle cl_hitsound");
                Entry("Chat sound", "toggle con_chatsound");
            SMenu("Sound settings");

            if (SpectateeStatus > 0)
            {
                EntryKeep("Change spectator camera", "weapon_drop");
            }
            else if (SpectateeStatus == -1)
            {
                SMenu("Observer camera");
                    EntryKeep("Increase speed", "weapnext");
                    EntryKeep("Decrease speed", "weapprev");
                    Entry("Wall collision", "toggle cl_clippedspectating");
                SMenu("Observer camera");
            }

            Entry("Fullscreen", "toggle vid_fullscreen; vid_restart");
        SMenu("Settings");

        // --- Call a vote ---
        SMenu("Call a vote");
            Entry("Restart the map", "vcall restart");
            Entry("End match", "vcall endmatch");
            if (HasTimeLimit)
            {
                Entry("Reduce match time", "vcall reducematchtime");
                Entry("Extend match time", "vcall extendmatchtime");
            }
            if (Teamplay)
                Entry("Shuffle teams", "vcall shuffleteams");
        SMenu("Call a vote");

        // QC: spectate-a-player submenu when not actively playing.
        if (SpectateeStatus != 0)
            PlayerSubMenu("Spectate a player", "spectate \"%s^7\"");
    }

    // QC QUICKMENU_SMENU_PL: a self-contained per-player submenu (open … one Entry per player … close). The
    // <paramref name="commandTemplate"/> is a command containing the literal token "%s" (QC's sprintf "%s" arg)
    // which is substituted with the player name. Omitted entirely when no player list has been fed (QC: an empty
    // player list yields an empty submenu, which QuickMenu_Page_Load then drops — matching our self-blank contract).
    private void PlayerSubMenu(string name, string commandTemplate)
    {
        if (PlayerNames.Count == 0) return;
        SMenu(name);
            foreach (string n in PlayerNames)
                Entry("^7" + n, commandTemplate.Replace("%s", n, StringComparison.Ordinal));
        SMenu(name);
    }

    // =====================================================================================
    //  Page loading (QC QuickMenu_Page_Load + QuickMenu_skip_submenu)
    // =====================================================================================

    /// <summary>Load one page of <paramref name="targetSubMenu"/> (QC <c>QuickMenu_Page_Load</c>). When
    /// <paramref name="newPage"/> is false the page resets to 0; otherwise it advances one page.</summary>
    private bool LoadPage(string targetSubMenu, bool newPage)
    {
        _activatedRow = -1;
        _pageIndex = newPage ? _pageIndex + 1 : 0;
        _currentSubMenu = targetSubMenu ?? "";
        _isLastPage = true;
        _page.Clear();

        int idx = 0;

        // Skip to the open tag of the target submenu (QC: scan for the matching Submenu token).
        if (_currentSubMenu != "")
        {
            for (; idx < _buffer.Count; idx++)
            {
                if (_buffer[idx].Tag == Tag.Submenu && _buffer[idx].Text == _currentSubMenu)
                {
                    idx++;
                    break;
                }
            }
        }

        // QC: only the LAST page may hold up to MaxLines rows; every earlier page holds exactly MaxLines-2 data
        // entries, then a blank line, then a "Continue..." row on the bottom line. (QC counts a 1-based
        // QuickMenu_Page_Entries; at ==MaxLines it CLEARS entry MaxLines-1 and breaks, so the net visible count
        // is MaxLines-2 — and first_entry advances by exactly MaxLines-2 per page, giving no overlap and no gap.)
        // _page.Count here is the running count of already-added visible rows, so we overflow once it reaches
        // MaxLines-2 and the submenu still has more entries to come.
        int firstEntry = _pageIndex * (MaxLines - 2);
        int entryNum = 0;             // counts entries inside the target submenu

        for (; idx < _buffer.Count; idx++)
        {
            Token t = _buffer[idx];

            // QC: a Submenu token matching the current name closes the submenu.
            if (_currentSubMenu != "" && t.Tag == Tag.Submenu && t.Text == _currentSubMenu)
                break;

            bool visible = entryNum >= firstEntry;

            // Overflow BEFORE loading the (MaxLines-1)th visible entry: this page is full (MaxLines-2 rows), so
            // mark it non-final and stop. The remaining entries roll onto the next page (QC QuickMenu_IsLastPage).
            if (visible && _page.Count == MaxLines - 2)
            {
                _isLastPage = false;
                break;
            }

            if (t.Tag == Tag.Submenu)
            {
                if (visible)
                    _page.Add(new PageEntry { Description = t.Text, Command = "" }); // submenu row
                idx = SkipSubMenu(idx, t.Text);
            }
            else if (t.Tag == Tag.Title)
            {
                // The next token is the command (QC: Title immediately followed by a Command/KCommand token).
                if (idx + 1 < _buffer.Count)
                {
                    Token cmdTok = _buffer[idx + 1];
                    idx++;
                    if (visible)
                    {
                        var pe = new PageEntry
                        {
                            Description = t.Text,
                            Command = cmdTok.Text,
                            Special = cmdTok.Special,
                            Type = cmdTok.Tag == Tag.KCommand ? CmdType.Keep : CmdType.None,
                        };
                        // QC: a leading "toggle " command becomes a toggle row (draws a checkbox + keeps open).
                        if (IsToggleCommand(cmdTok.Text))
                            pe.Type = CmdType.Toggle;
                        _page.Add(pe);
                    }
                }
            }

            entryNum++;
        }

        if (_page.Count == 0)
            return false;

        ResetTimeout();
        return true;
    }

    /// <summary>QC <c>QuickMenu_skip_submenu</c>: advance past a nested submenu's contents, returning the buffer
    /// index of its closing token (the caller's loop then ++'s past it). <paramref name="depth"/> caps recursion
    /// so a malformed/adversarial buffer (deeply nested or never-closed submenus) can't overflow the stack.</summary>
    private int SkipSubMenu(int openIdx, string name, int depth = 0)
    {
        if (depth > MaxLines) return _buffer.Count; // give up: treat as run-to-end (caller's ++ exits the loop)
        for (int i = openIdx + 1; i < _buffer.Count; i++)
        {
            Token t = _buffer[i];
            if (t.Tag != Tag.Submenu) continue;
            if (t.Text == name) return i;             // matching close tag
            i = SkipSubMenu(i, t.Text, depth + 1);    // a deeper nested submenu
        }
        return _buffer.Count; // no close tag: run to end; caller's ++ then exits the loop
    }

    private static bool IsToggleCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        string head = command;
        int semi = head.IndexOf(';');
        if (semi >= 0) head = head.Substring(0, semi);
        head = head.TrimStart();
        return head.StartsWith("toggle ", StringComparison.Ordinal);
    }

    // =====================================================================================
    //  Activation (QC QuickMenu_ActionForNumber / QuickMenu_Page_ActiveEntry)
    // =====================================================================================

    /// <summary>QC <c>QuickMenu_ActionForNumber</c> — act on the row chosen by a number key. <paramref name="num"/>
    /// is the QC 1-based row number (0 = "Continue..."/next page). Returns true if the menu should close.</summary>
    private bool ActionForNumber(int num)
    {
        if (!_isLastPage)
        {
            if (num < 0 || num >= MaxLines) return false;
            if (num == MaxLines - 1) return false;
            if (num == 0)
            {
                LoadPage(_currentSubMenu, newPage: true);
                return false;
            }
        }
        else if (num <= 0 || num > _page.Count)
            return false;

        // Defensive bounds (QC reads a fixed-size array so an out-of-range number is a harmless empty entry;
        // our _page is a List, so guard before indexing — e.g. pressing '9' on an 8-row non-final page).
        if (num <= 0 || num > _page.Count)
            return false;

        PageEntry pe = _page[num - 1]; // QC entries are 1-based

        if (pe.Command != "")
        {
            // QC: special commands carry a leading "\n"; strip it before running.
            string cmd = pe.Command;
            if (cmd.StartsWith("\n", StringComparison.Ordinal))
                cmd = cmd.Substring(1);

            _activatedTime = _clock + 0.1;
            // The sink is host-supplied (the console interpreter): isolate its exceptions so a bad row command
            // can't tear down input handling. QC's localcmd is fire-and-forget; mirror that here.
            try { CommandSink?.Invoke(cmd); }
            catch (Exception ex) { VortexArena.Common.Diagnostics.Log.Warn($"[QuickMenu] command failed: {ex.Message}"); }
            ResetTimeout();
            // Toggle / keep-open rows stay; plain commands close.
            return pe.Type != CmdType.Toggle && pe.Type != CmdType.Keep;
        }

        // A submenu row: descend.
        if (pe.Description != "")
            LoadPage(pe.Description, newPage: false);
        return false;
    }

    /// <summary>QC <c>QuickMenu_Page_ActiveEntry</c> — flash the row then run its action; close if the action
    /// requested it. <paramref name="row0"/> is 0-based into the visible page (use -1 for the "Continue..." row).</summary>
    private void Activate(int row0)
    {
        // Map the 0-based page row to the QC 1-based number (Continue... = 0).
        int num = row0 < 0 ? 0 : row0 + 1;
        _activatedRow = row0;
        _activatedTime = _clock + 0.1;
        // QC runs the command immediately (localcmd) but flashes the row green for 0.1s before closing — so the
        // close is DEFERRED to _Process once the flash window elapses (matching HUD_QuickMenu's end-of-draw close).
        // A submenu descent (LoadPage) reloads the page and clears _activatedRow, so it never lingers as a flash.
        _pendingClose = ActionForNumber(num);
    }

    // =====================================================================================
    //  Input (QC QuickMenu_InputEvent + QuickMenu_Mouse)
    // =====================================================================================

    public override void _GuiInput(InputEvent @event)
    {
        if (!IsOpen) return;

        if (@event is InputEventMouseMotion mm)
        {
            int idx = RowAt(mm.Position);
            if (idx != _hover) { _hover = idx; ResetTimeout(); QueueRedraw(); }
            return;
        }

        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Right) { Close(); AcceptEvent(); return; }
            if (mb.ButtonIndex == MouseButton.Left)
            {
                int idx = RowAt(mb.Position);
                if (idx == ContinueRowIndex) { Activate(-1); AcceptEvent(); return; }
                if (idx >= 0 && idx < _page.Count) { Activate(idx); AcceptEvent(); }
            }
            return;
        }

        if (@event is InputEventKey { Pressed: true } k)
        {
            if (k.Keycode == Key.Escape) { Close(); AcceptEvent(); return; }
            int digit = DigitOf(k.Keycode);
            if (digit >= 0)
            {
                // QC: digit selects the QC 1-based row; 0 = Continue/next page.
                Activate(digit == 0 ? -1 : digit - 1);
                AcceptEvent();
            }
        }
    }

    private static int DigitOf(Key key) => key switch
    {
        >= Key.Key0 and <= Key.Key9 => (int)(key - Key.Key0),
        >= Key.Kp0 and <= Key.Kp9 => (int)(key - Key.Kp0),
        _ => -1,
    };

    /// <summary>Recompute the hovered row from the live mouse position (QC <c>QuickMenu_Mouse</c> hover), so a
    /// purely mouse-driven open still highlights without needing a motion event.</summary>
    private void UpdateHover()
    {
        int idx = RowAt(GetLocalMousePosition());
        if (idx != _hover) { _hover = idx; QueueRedraw(); }
    }

    // =====================================================================================
    //  Layout helpers (shared by draw + hit-test, mirroring QC's panel_size/QUICKMENU_MAXLINES math)
    // =====================================================================================

    // A non-final page draws its MaxLines-1 entries from the top and a "Continue..." row on the bottom line
    // (QC HUD_QuickMenu: drawn at panel_pos + eY*(panel_size.y - fontsize.y)). RowAt returns this sentinel for it.
    private int ContinueRowIndex => -2;

    private float Pad => Cfg.Padding;

    private float RowHeight => InnerSize.Y / MaxLines;

    private Vector2 InnerOrigin => new(Pad, Pad);

    private Vector2 InnerSize => new(Mathf.Max(1f, Size2.X - Pad * 2f), Mathf.Max(1f, Size2.Y - Pad * 2f));

    // Top Y of the first drawn row. On the last page the rows are vertically centered (QC nudges panel_pos.y);
    // otherwise they start at the top so the "Continue..." row sits on the bottom line.
    private float FirstRowY()
    {
        float rh = RowHeight;
        if (_isLastPage)
            return InnerOrigin.Y + (MaxLines - _page.Count) * rh * 0.5f;
        return InnerOrigin.Y;
    }

    /// <summary>Map a panel-local point to a row: 0..n-1 for a list row, <see cref="ContinueRowIndex"/> for the
    /// "Continue..." row, or -1 for none (QC <c>QuickMenu_Mouse</c> hit-test).</summary>
    private int RowAt(Vector2 pos)
    {
        float rh = RowHeight;
        if (pos.X < InnerOrigin.X || pos.X > InnerOrigin.X + InnerSize.X) return -1;

        // "Continue..." row (non-final pages) sits on the very last line.
        if (!_isLastPage)
        {
            float contY = InnerOrigin.Y + InnerSize.Y - rh;
            if (pos.Y >= contY && pos.Y <= contY + rh) return ContinueRowIndex;
        }

        float first = FirstRowY();
        if (pos.Y < first) return -1;
        int row = (int)((pos.Y - first) / rh);
        if (row < 0 || row >= _page.Count) return -1;
        return row;
    }

    // =====================================================================================
    //  Draw (QC HUD_QuickMenu + HUD_Quickmenu_DrawEntry)
    // =====================================================================================

    protected override void DrawPanel()
    {
        if (!IsOpen) return; // self-blank when closed

        DrawBackground();

        float rh = RowHeight;
        int fontPx = Mathf.Clamp((int)(rh * 0.8f), 9, 28);
        float first = FirstRowY();

        // "Continue..." row on the bottom line (QC ^5).
        if (!_isLastPage)
        {
            float contY = InnerOrigin.Y + InnerSize.Y - rh;
            if (_hover == ContinueRowIndex)
                DrawHover(contY, rh);
            // QC draws "0: ^5Continue..." (sprintf("%d: %s%s", 0, "^5", _("Continue..."))).
            DrawEntry(new Vector2(InnerOrigin.X, contY), 0, "0: ^5Continue...", null, fontPx);
        }

        for (int i = 0; i < _page.Count; i++)
        {
            PageEntry pe = _page[i];
            if (pe.Description == "") break;

            float y = first + i * rh;

            // Hover / activation highlight (QC drawfill over the row).
            if (_hover == i)
                DrawHover(y, rh);
            if (_activatedRow == i && _clock < _activatedTime)
                DrawRect(new Rect2(InnerOrigin.X, y, InnerSize.X, rh), new Color(0.5f, 1f, 0.5f, 0.2f * LiveFgAlpha));

            // Row color: submenu ^4, special ^6, command ^3 (QC HUD_QuickMenu).
            string color;
            string? checkbox = null;
            if (pe.Command == "")
                color = "^4";
            else
            {
                color = pe.Special ? "^6" : "^3";
                if (pe.Type == CmdType.Toggle)
                    checkbox = ToggleCheckbox(pe.Command);
            }

            DrawEntry(new Vector2(InnerOrigin.X, y), i + 1, $"{color}{pe.Description}", checkbox, fontPx);
        }
    }

    private void DrawHover(float y, float rh)
        => DrawRect(new Rect2(InnerOrigin.X, y, InnerSize.X, rh), new Color(1f, 1f, 1f, 0.2f * LiveFgAlpha));

    /// <summary>Draw one row: "N: text" colored, optionally with a right-aligned checkbox icon (QC
    /// <c>HUD_Quickmenu_DrawEntry</c>), honoring <c>_align</c> for the text alignment.</summary>
    private void DrawEntry(Vector2 pos, int number, string coloredText, string? checkbox, int fontPx)
    {
        float rowW = InnerSize.X;
        float descW = rowW;
        float iconSize = fontPx * 0.8f;

        // Right-aligned checkbox for toggle rows (QC draws the option pic at the right edge).
        if (checkbox is not null)
        {
            var iconRect = new Rect2(
                pos.X + rowW - iconSize,
                pos.Y + (RowHeight - iconSize) * 0.5f,
                iconSize, iconSize);
            if (!DrawSkinPic(checkbox, iconRect, new Color(1f, 1f, 1f, LiveFgAlpha)))
            {
                // Fallback primitive so the toggle state is never invisible.
                DrawRect(iconRect, new Color(1f, 1f, 1f, 0.25f * LiveFgAlpha), filled: false, width: 1f);
                if (checkbox == "checkbox_checked")
                    DrawRect(iconRect.Grow(-2f), new Color(0.4f, 1f, 0.4f, 0.6f * LiveFgAlpha));
            }
            descW -= iconSize + fontPx * 0.25f;
        }
        // A very narrow panel (or a wide checkbox gap) could drive descW non-positive — clamp so the text
        // never gets a negative draw width and the alignment math below can't go NaN/negative.
        if (descW < 1f) descW = 1f;

        // "N: " prefix then the colored description (QC sprintf("%d: %s%s", i, color, desc)).
        string full = number > 0 ? $"{number}: {coloredText}" : coloredText;

        var runs = HudText.Parse(full, FgColor);

        // _align: 0 left, 1 right; the text is shifted by align × leftover within descW (QC _align clamp 0..1).
        float align = Mathf.Clamp(CvarF("align", 0f), 0f, 1f);
        float textW = 0f;
        foreach (HudText.Run r in runs) textW += MeasureText(r.Text, fontPx);
        float offset = align > 0f ? Mathf.Max(0f, (descW - textW) * align) : 0f;

        // Clip the text to its row column [pos.X, pos.X+descW) so a long row title / player name (QC
        // textShortenToWidth) never bleeds off the panel rect or under the checkbox. Stop once we run out.
        // DrawString with HorizontalAlignment.Left + a positive width clips the glyphs to that width.
        float rightEdge = pos.X + descW;
        float y = pos.Y + (RowHeight - fontPx) * 0.5f + fontPx;
        float x = pos.X + offset;
        foreach (HudText.Run r in runs)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;
            if (x >= rightEdge) break;
            float avail = rightEdge - x;
            var at = new Vector2(x, y);
            var shadow = new Color(0f, 0f, 0f, r.Color.A * 0.7f);
            DrawString(Font, at + new Vector2(1f, 1f), r.Text, HorizontalAlignment.Left, avail, fontPx, shadow);
            DrawString(Font, at, r.Text, HorizontalAlignment.Left, avail, fontPx, r.Color);
            x += MeasureText(r.Text, fontPx);
        }
    }

    /// <summary>Pick the checkbox icon for a <c>toggle &lt;cvar&gt; [on] [off]</c> row from the live cvar value
    /// (QC: checked / empty / undefined). Reads the shared menu/console store.</summary>
    private static string ToggleCheckbox(string command)
    {
        // Use the first ';'-delimited clause (QC tokenizes only up to the first ';').
        string head = command;
        int semi = head.IndexOf(';');
        if (semi >= 0) head = head.Substring(0, semi);

        string[] argv = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length < 2 || argv[0] != "toggle") return "checkbox_undefined";

        string cvarName = argv[1];
        float onValue = 1f, offValue = 0f;
        if (argv.Length >= 3 && float.TryParse(argv[2],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float on))
            onValue = on;
        if (argv.Length >= 4 && float.TryParse(argv[3],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float off))
            offValue = off;
        else
            offValue = onValue != 0f ? 0f : 1f;

        float value = MenuState.Cvars.GetFloat(cvarName);
        if (Mathf.IsEqualApprox(value, onValue)) return "checkbox_checked";
        if (Mathf.IsEqualApprox(value, offValue)) return "checkbox_empty";
        return "checkbox_undefined";
    }

    // =====================================================================================
    //  Behaviour cvar defaults (QC autocvar_hud_panel_quickmenu_*)
    // =====================================================================================

    /// <summary>Register the <c>hud_panel_quickmenu_*</c> behaviour cvars (QC the autocvars in quickmenu.qc:
    /// <c>_align</c>, <c>_translatecommands</c>, <c>_server_is_default</c>, <c>_time</c>). Invoked by reflection
    /// from <see cref="HudConfig"/>; idempotent (CvarService.Register keeps an existing value).</summary>
    public static void RegisterDefaults(CvarService c)
    {
        if (c is null) return;
        const CvarFlags save = CvarFlags.Save;
        c.Register("hud_panel_quickmenu_align", "0", save);
        c.Register("hud_panel_quickmenu_translatecommands", "0", save);
        c.Register("hud_panel_quickmenu_server_is_default", "0");
        // Base default is 5 (_hud_common.cfg:124: seta hud_panel_quickmenu_time 5) — the page idle-timeout in
        // seconds. The port had 0 ("never expire"), a parity deviation; restore the shipped 5s timeout.
        c.Register("hud_panel_quickmenu_time", "5", save);
    }
}
