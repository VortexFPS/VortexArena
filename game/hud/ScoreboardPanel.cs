using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Scoreboard overlay — port of the core of Base/.../qcsrc/client/hud/panel/scoreboard.qc (HUD panel #25).
/// The QC scoreboard is a CONFIGURABLE column grid driven by the networked <c>scores</c> stat fields: the
/// active <c>scoreboard_columns</c> (else SCOREBOARD_DEFAULT_COLUMNS) selects which SP_* columns show, filtered
/// per-gametype (Cmd_Scoreboard_SetFields), each value formatted by its SFL_* flags (Scoreboard_GetField /
/// ScoreString), rows sorted by the per-mode primary/secondary keys (Scoreboard_ComparePlayerScores), grouped
/// into team sections with per-team totals in teamplay. We port that faithfully (pragmatically): a per-mode
/// column list, value formatting, per-mode sort, team panels, plus the map-stats / respawn / fraglimit header
/// and (stubbed) accuracy + rankings blocks.
///
/// Data source: other players' columns/name/team are a networked thing. The net layer pushes rows via
/// <see cref="SetWireRows"/> (the decoded <see cref="VortexArena.Net.ScoreboardWire"/> — full column values per
/// player) which is the column-driven path, or via <see cref="SetRows"/> (a <see cref="ScoreRow"/> per player)
/// for the simpler #/name/score/deaths/ping view. The active per-mode column layout comes from the networked
/// <see cref="GameScores"/> labels/flags (set by the ScoreInfo block, QC ENT_CLIENT_SCORES_INFO).
/// </summary>
public partial class ScoreboardPanel : HudPanel
{
    // The scoreboard only repaints when its data changes (or it's toggled), not every frame.
    public override bool IsDynamic => false;

    /// <summary>
    /// QC <c>Scoreboard_Draw</c>'s own geometry (scoreboard.qc:2477-2483). The scoreboard is
    /// <c>PANEL_CONFIG_NO</c>: Base still runs <c>HUD_Panel_LoadCvars()</c> for its skin/alpha/padding, then
    /// OVERWRITES the horizontal placement unconditionally, every draw:
    /// <code>
    ///   excess = max(0, max_namesize - hud_panel_scoreboard_namesize * hud_fontsize.x)
    ///   width  = bound(conwidth * hud_panel_scoreboard_minwidth, conwidth - excess, conwidth * 0.93)
    ///   left   = 0.5 * (conwidth - width)
    ///   panel_pos.y = max(con_notify * con_notifysize, panel_pos.y)   // don't overlap con_notify
    /// </code>
    /// So the board is a horizontally CENTERED slab 93% of the viewport wide in the normal case, narrowing only
    /// when a player name overruns its column — not the old fixed <c>min(80%, 1100px)</c> rect, and above all
    /// not a rect computed once at setup: the owner re-applies this every frame so the board tracks the live
    /// viewport instead of keeping a boot-time rect and drifting into the corner after a resize/fullscreen.
    /// </summary>
    public Rect2 BaseGeometry(Vector2 viewport)
    {
        int fs = Mathf.Max(6, Cfg.FontSize);
        float minWidth = Mathf.Clamp(CvarF("minwidth", 0.6f), 0.05f, 0.93f);

        // QC Scoreboard_FixColumnWidth (scoreboard.qc:1254):
        //   max_namesize = max(name_title_width, vid_conwidth - used_space)
        // where used_space is EVERY other column's width plus a hud_fontsize.x gutter each, plus the name cell's
        // own margin + icon allowance. That is the width the name column WOULD get if the board spanned the
        // whole screen. The board is then shrunk by however much that overruns the name allowance:
        //   excess = max(0, max_namesize - hud_panel_scoreboard_namesize * hud_fontsize.x)
        //   width  = bound(conwidth * minwidth, conwidth - excess, conwidth * 0.93)
        // Substituting, the common case is `width = used_space + namesize * hud_fontsize.x` — i.e. the board is
        // only as wide as its numeric columns plus a 15-character name column, floored at 60% of the screen.
        // It reaches the 93% ceiling only when the columns genuinely need that much. (Sizing it at a flat 93%
        // made the board far too wide for a normal column set.)
        EnsureColumns();
        float usedSpace = 0f;
        float nameTitleWidth = 0f;
        foreach (Column c in _columns)
        {
            if (c.Kind == ColumnKind.Separator) continue;
            if (c.Kind == ColumnKind.Name) { nameTitleWidth = MeasureText(c.Title, fs); continue; }
            usedSpace += MeasureNumericColumn(c, fs, viewport.X) + fs;
        }
        // QC the name cell's own reservations: the margin space + the player-colour icon + the two extra icons.
        usedSpace += MeasureText(" ", fs) + fs * (NameIconCells + ExtraIconCells);

        float maxNameSize = Mathf.Max(nameTitleWidth, viewport.X - usedSpace);
        float excess = Mathf.Max(0f, maxNameSize - CvarF("namesize", 15f) * fs);

        float width = Mathf.Clamp(viewport.X - excess, viewport.X * minWidth, viewport.X * 0.93f);
        float left = (viewport.X - width) * 0.5f;

        float top = Mathf.Max(GlobalF("con_notify", 0f) * GlobalF("con_notifysize", 8f), viewport.Y * 0.06f);
        float height = Mathf.Max(viewport.Y * 0.05f, viewport.Y - top - viewport.Y * 0.02f);

        return new Rect2(left, top, width, height);
    }

    /// <summary>QC <c>sbt_fixcolumnwidth_iconlen_playercolor</c> — the name cell reserves one font-width cell for
    /// the player-colour swatch.</summary>
    private const float NameIconCells = 1f;
    /// <summary>QC <c>sbt_fixcolumnwidth_iconlen_extra[0..1]</c> — the ready / handicap icon cells.</summary>
    private const float ExtraIconCells = 2f;

    /// <summary>
    /// QC <c>Scoreboard_FixColumnWidth</c>'s non-name branch: a column is as wide as the WIDER of its title
    /// (capped at <c>..._table_fieldtitle_maxwidth</c> of the screen, above which the title is condensed rather
    /// than widening the column) and its widest value across the current rows. This content measurement is what
    /// gives Base its tight, readable table instead of a row of uniform boxes.
    /// </summary>
    private float MeasureNumericColumn(Column c, int fs, float viewportW)
        => MeasureNumericColumn(c, fs, viewportW, out _);

    /// <summary>
    /// As <see cref="MeasureNumericColumn(Column,int,float)"/>, also yielding QC's
    /// <c>sbt_field_title_condense_factor[i]</c> (scoreboard.qc:1275-1283):
    /// <code>
    ///   condense = 0;
    ///   if (title_width > sbt_field_size[i]) {
    ///       real_maxwidth = sbt_field_size[i];
    ///       if (title_width > title_maxwidth) real_maxwidth = max(sbt_field_size[i], title_maxwidth);
    ///       condense = real_maxwidth / title_width;
    ///   }
    /// </code>
    /// A title wider than its column is SQUEEZED horizontally (QC scales <c>drawfontscale.x</c> around the draw)
    /// rather than clipped or allowed to widen the table — so "suicides" over a 1-digit column still reads as
    /// "suicides", just narrower. 1 = no squeeze.
    /// </summary>
    private float MeasureNumericColumn(Column c, int fs, float viewportW, out float condenseFactor)
    {
        float titleMax = Mathf.Clamp(CvarF("table_fieldtitle_maxwidth", 0.07f), 0.01f, 0.1f) * viewportW;
        float titleW = MeasureText(c.Title, fs);

        // QC init: size starts at the title width, capped at title_maxwidth.
        float w = titleW > 0f ? Mathf.Min(titleW, titleMax) : 0f;
        // QC per-row: grow to fit the widest value.
        foreach (ScoreRow r in _rows)
        {
            FieldText ft = GetField(r, c);
            if (!string.IsNullOrEmpty(ft.Text)) w = Mathf.Max(w, MeasureText(ft.Text, fs));
        }

        condenseFactor = 1f;
        if (titleW > w && titleW > 0f)
        {
            float realMaxWidth = titleW > titleMax ? Mathf.Max(w, titleMax) : w;
            condenseFactor = Mathf.Clamp(realMaxWidth / titleW, 0.2f, 1f);
        }
        return w;
    }

    /// <summary>
    /// One scoreboard row — the networked per-player score record (QC scores[player] + the entcs name/team
    /// slice). <see cref="Columns"/> holds the full registry-indexed column values (QC <c>pl.(scores(field))</c>)
    /// when fed from the wire; the simple <see cref="SetRows"/>/<see cref="SetPlayers"/> path leaves it null and
    /// only the #/name/score/deaths/ping view renders.
    /// </summary>
    public readonly struct ScoreRow
    {
        /// <summary>Display name, may contain ^N color codes (QC entcs name).</summary>
        public readonly string Name;
        /// <summary>Team color code (QC NUM_TEAM_*; <see cref="Teams.None"/> for FFA). 0 = no team.</summary>
        public readonly int Team;
        /// <summary>Match score / frags (QC SP_SCORE).</summary>
        public readonly int Score;
        /// <summary>Deaths (QC SP_DEATHS); &lt; 0 = unknown/not networked.</summary>
        public readonly int Deaths;
        /// <summary>Ping in ms (QC SP_PING); &lt; 0 = unknown (bot / not networked).</summary>
        public readonly int Ping;
        /// <summary>QC <c>pl.ping_packetloss</c> (SP_PL): the player's packet loss as a 0..1 fraction
        /// (de-quantized from the networked byte). 0 = no loss (or bot / unknown) → the SP_PL cell is blank.</summary>
        public readonly float PacketLoss;
        /// <summary>True if this row is the local player (highlighted). Set by the feeder or matched by name.</summary>
        public readonly bool IsLocal;
        /// <summary>True if this player is eliminated for the round (QC <c>pl.eliminated</c>, the networked
        /// eliminatedPlayers bitfield: CA dead / FT frozen-or-dead / Survival out) — greys the row.</summary>
        public readonly bool Eliminated;
        /// <summary>Full registry-indexed column values (QC scores(field)) when fed from the wire; else null.</summary>
        public readonly int[]? Columns;
        /// <summary>QC <c>.handicap_level</c> (entcs, scoreboard.qc:1003): 0..16; 0 = no handicap. When nonzero the
        /// row draws the <c>player_handicap</c> icon tinted white@1 → red@16 next to the name.</summary>
        public readonly int HandicapLevel;
        /// <summary>QC <c>pl.sv_entnum</c> stand-in: the player's stable net id, used for the playerid '#N' name
        /// prefix (Scoreboard_AddPlayerId) and the final sort tiebreak. 0 = none.</summary>
        public readonly int NetId;

        public ScoreRow(string name, int score, int team = 0, int deaths = -1, int ping = -1,
            bool isLocal = false, int[]? columns = null, bool eliminated = false, float packetLoss = 0f,
            int handicapLevel = 0, int netId = 0)
        {
            Name = name ?? "";
            Score = score;
            Team = team;
            Deaths = deaths;
            Ping = ping;
            PacketLoss = packetLoss;
            IsLocal = isLocal;
            Eliminated = eliminated;
            Columns = columns;
            HandicapLevel = handicapLevel;
            NetId = netId;
        }

        /// <summary>QC <c>pl.(scores(field))</c>: read a column value (0 when not networked / no column data).</summary>
        public int Col(ScoreField f) => (Columns is not null && (uint)f.RegistryId < Columns.Length) ? Columns[f.RegistryId] : 0;
    }

    /// <summary>The local player, whose row is highlighted (set by <see cref="Hud"/>).</summary>
    public Player? LocalPlayer { get; set; }

    /// <summary>The match title shown atop the table (e.g. "Deathmatch"). Settable by the owner.</summary>
    public string Title { get; set; } = "Scoreboard";

    /// <summary>QC <c>MapInfo_Type_ToText(gametype)</c>: the gametype name banner drawn big at the top-right of the
    /// game-info section (e.g. "Deathmatch", "Capture the Flag"). Falls back to <see cref="Title"/> when empty.</summary>
    public string GametypeName { get; set; } = "";

    /// <summary>QC <c>GET_NEXTMAP()</c>: the next map shown ("Next map: …") above the gametype banner; "" = none.</summary>
    public string NextMap { get; set; } = "";

    /// <summary>QC <c>numplayers</c> / <c>srv_maxplayers</c>: the "N/M players" line in the game-info footer
    /// (the map-info line). 0/0 = hidden (e.g. campaign).</summary>
    public int PlayerCount { get; set; }
    public int MaxPlayerCount { get; set; }

    /// <summary>True when the active gametype is teamplay (groups rows into team sections + shows team totals).</summary>
    public bool TeamPlay { get; set; }

    /// <summary>QC <c>gametype.m_hidelimits</c> (mapinfo.qh:50, GAMETYPE_FLAG_HIDELIMITS; scoreboard.qc:2551):
    /// when set the fraglimit + leadlimit terms are suppressed from the game-info limits line (only the timelimit
    /// shows). The only stock gametype that sets it is LMS (lms.qh:11). Fed by the match layer.</summary>
    public bool HideLimits { get; set; }

    /// <summary>QC global <c>campaign</c> (scoreboard.qc:2574): in a single-player campaign the "N/M players" map
    /// line is suppressed (the count is meaningless). Fed by the match layer.</summary>
    public bool Campaign { get; set; }

    // ---- spectator list (QC Scoreboard_Spectators_Draw) ----

    /// <summary>One spectator entry (QC the NUM_SPECTATOR rows: name + ping). Fed via <see cref="SetSpectators"/>.</summary>
    public readonly struct SpectatorRow
    {
        public readonly string Name;   // may carry ^N color codes
        public readonly int Ping;      // ms; &lt; 0 = unknown (bot / not networked)
        public SpectatorRow(string name, int ping = -1) { Name = name ?? ""; Ping = ping; }
    }

    private readonly List<SpectatorRow> _spectators = new();

    // ---- fade in/out (QC scoreboard_fade_alpha + fadeinspeed/fadeoutspeed) ----

    /// <summary>QC <c>scoreboard_active</c>: the owner sets this true while the scoreboard key is held (or on
    /// the death/intermission scoreboard); the panel fades <see cref="_fadeAlpha"/> in/out toward it and hides
    /// itself once fully faded out. Replaces the raw <see cref="Godot.CanvasItem.Visible"/> toggle so the
    /// scoreboard cross-fades like QC instead of popping. The owner may still set Visible directly (legacy).</summary>
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; if (value) Visible = true; QueueRedraw(); } }
    }
    private bool _active;
    private float _fadeAlpha;        // 0..1 current fade (QC scoreboard_fade_alpha)

    /// <summary>The current fade level 0..1 (QC scoreboard_fade_alpha) so the owner can also drive the manager's
    /// non-scoreboard panel cross-fade if it wants. Read-only.</summary>
    public float FadeAlpha => _fadeAlpha;

    // =================================================================================================
    //  Interactive scoreboard UI — QC scoreboard_ui_enabled / Scoreboard_UI_Enable /
    //  HUD_Scoreboard_InputEvent (scoreboard.qc:182-505). Two modes:
    //    1 = navigation: arrow keys pick a PLAYER (Enter = spectate them, Ctrl+T = tell, Ctrl+K = vote-kick)
    //        or, with the Rankings panel selected, scroll the record columns; TAB cycles panels.
    //    2 = team selection: arrows pick a TEAM, Space/Enter joins it (Shift = auto).
    //  Opened by TAB+Escape (mode 1) or by the server asking for a team pick (mode 2).
    // =================================================================================================

    /// <summary>QC <c>SB_PANEL_SCOREBOARD</c> / <c>SB_PANEL_RANKINGS</c> (scoreboard.qh:50-53, 1-based).</summary>
    public const int PanelScoreboard = 1;
    public const int PanelRankings = 2;

    /// <summary>QC <c>scoreboard_ui_enabled</c>: 0 = off, 1 = navigation, 2 = team selection.</summary>
    public int UiMode { get; private set; }

    /// <summary>QC <c>scoreboard_ui_disabling</c>: the UI is closing and fading out; input is already ignored,
    /// and the state is torn down once the fade reaches 0 (QC Scoreboard_WouldDraw).</summary>
    public bool UiDisabling { get; private set; }

    /// <summary>QC <c>scoreboard_selected_panel</c> — which sub-panel the keys act on.</summary>
    public int SelectedPanel { get; private set; }

    /// <summary>QC <c>scoreboard_selected_player</c>, as the row's stable net id (-1 = none).</summary>
    public int SelectedPlayerNetId { get; private set; } = -1;

    /// <summary>QC <c>scoreboard_selected_team</c> (0 = none/auto).</summary>
    public int SelectedTeam { get; private set; }

    /// <summary>QC <c>rankings_start_column</c>: horizontal scroll offset of the record table.</summary>
    public int RankingsStartColumn { get; private set; }

    /// <summary>QC <c>scoreboard_selected_panel_time</c> — drives the selected-panel highlight fade.</summary>
    private float _selectedPanelTime;

    /// <summary>QC <c>scoreboard_selected_columns_layout</c> — the Ctrl+C column-preset cycle position.</summary>
    private int _selectedColumnsLayout;

    /// <summary>Where the UI's console commands go (QC <c>localcmd</c>). Wired by the host to the console/server
    /// command bus; unset makes the actions inert (the navigation itself still works).</summary>
    public System.Action<string>? CommandSink { get; set; }

    /// <summary>DP <c>commandmode &lt;prefill&gt;</c>: open the console command prompt with a half-typed line for
    /// the player to finish (QC's Ctrl+T <c>tell</c>). Wired by the host to the chat/command prompt.</summary>
    public System.Action<string>? OpenCommandPrompt { get; set; }

    /// <summary>Number of record columns currently laid out — QC <c>rankings_columns</c>, needed to clamp the
    /// Left/Right scroll. Refreshed by <see cref="DrawRankings"/>.</summary>
    private int _rankingsColumns = 1;
    private int _rankingsRows = 1;

    /// <summary>
    /// QC <c>Scoreboard_UI_Enable(mode)</c> (scoreboard.qc:198): open the interactive scoreboard. Mode 1 (team
    /// selection) is refused outside teamplay / at intermission / when already in it, exactly like QC.
    /// </summary>
    public void UiEnable(int mode)
    {
        if (mode == 1)
        {
            // QC gates on the `teamplay` global, which is live from map load. The panel's own TeamPlay flag only
            // arrives with the first scoreboard frame, so fall back to the shared score state — otherwise a
            // team-pick request that beats the first frame (exactly when the server asks: at join) is dropped.
            if (UiMode == 2 || !(TeamPlay || GameScores.Teamplay) || MatchIntermission) return;
            UiMode = 2;
            SelectedPanel = PanelScoreboard;
        }
        else
        {
            if (UiMode == 1) return;
            UiMode = 1;
            SelectedPanel = PanelScoreboard;
        }
        UiDisabling = false;
        SelectedPlayerNetId = -1;
        SelectedTeam = 0;
        _selectedPanelTime = UiNow();
        Active = true;
        QueueRedraw();
    }

    /// <summary>QC <c>HUD_Scoreboard_UI_Disable</c>: begin the fade-out (the state survives until it completes,
    /// so the board doesn't pop).</summary>
    public void UiDisable()
    {
        if (UiMode == 0) return;
        UiDisabling = true;
        // QC HUD_Scoreboard_UI_Disable also clears sb_showscores: without it, closing the UI with the scoreboard
        // key still held keeps the board up, so the fade never reaches 0 and the teardown never runs.
        VortexArena.Engine.Console.BindTable.ReleaseShowScores();
        Active = false;
        QueueRedraw();
    }

    /// <summary>
    /// The Escape edge, routed from the host's in-match Escape chain rather than from <c>_UnhandledInput</c>:
    /// Godot dispatches <c>_UnhandledKeyInput</c> BEFORE <c>_UnhandledInput</c>, and the Shell's handler marks
    /// both Escape edges handled while a match runs — so an Escape branch inside the play path's
    /// <c>_UnhandledInput</c> is unreachable. Mirrors QC, where closing the UI is owned by
    /// <c>HUD_Scoreboard_InputEvent</c> (scoreboard.qc:249) and the TAB+ESC OPEN lives in the generic Escape
    /// handler ahead of the menu (main.qc:545-551). Returns true when it consumed the key.
    /// </summary>
    public bool HandleEscape()
    {
        if (UiMode != 0 && !UiDisabling) { UiDisable(); return true; }
        // QC main.qc:547 — `if (hudShiftState & S_TAB)`: Escape while the scoreboard key is held opens the
        // interactive UI instead of the pause menu.
        if (UiMode == 0 && VortexArena.Engine.Console.BindTable.ShowScores) { UiEnable(0); return true; }
        return false;
    }

    /// <summary>QC <c>HUD_Scoreboard_UI_Disable_Instantly</c>: drop the whole UI state now.</summary>
    public void UiDisableInstantly()
    {
        UiDisabling = false;
        UiMode = 0;
        SelectedPanel = 0;
        SelectedPlayerNetId = -1;
        SelectedTeam = 0;
        QueueRedraw();
    }

    /// <summary>QC the <c>Scoreboard_WouldDraw</c> UI branch (scoreboard.qc:1766-1780): while the UI is up the
    /// board always draws; a closing UI tears itself down once faded out, and the team picker closes itself at
    /// intermission. Called once per frame by the owner before it decides visibility.</summary>
    public bool UiTick()
    {
        if (UiMode == 0) return false;
        if (UiDisabling)
        {
            if (_fadeAlpha == 0f) UiDisableInstantly();
            return false;
        }
        if (MatchIntermission && UiMode == 2)
        {
            UiDisableInstantly();
            return false;
        }
        return true;
    }

    /// <summary>QC <c>intermission</c> — the match has ended (the team picker closes itself then). Fed by the host.</summary>
    public bool MatchIntermission { get; set; }

    private float UiNow() => VortexArena.Common.Services.Api.Services?.Clock?.Time ?? 0f;

    /// <summary>
    /// QC <c>HUD_Scoreboard_InputEvent</c> (scoreboard.qc:231-505) — the key half (QC's <c>bInputType</c> 3
    /// mouse-position branch only tracks <c>mousepos</c>, which nothing in the scoreboard reads, so it is
    /// deliberately not modelled). Returns true when the key was consumed.
    /// </summary>
    public bool UiHandleKey(Key key, bool shift, bool ctrl)
    {
        if (UiMode == 0 || UiDisabling) return false;

        switch (key)
        {
            // NOTE: Escape is NOT handled here — the host's in-match Escape chain owns it (see HandleEscape).
            case Key.Tab:
                // QC: in team-selection mode TAB IS the up/down step; otherwise it cycles the sub-panels.
                if (UiMode == 2) { MoveSelection(shift ? -1 : +1); return true; }
                CyclePanel(shift ? -1 : +1);
                return true;

            case Key.Down: MoveSelection(+1); return true;
            case Key.Up:   MoveSelection(-1); return true;

            case Key.Right:
                // QC: only the Rankings panel scrolls horizontally.
                if (SelectedPanel == PanelRankings)
                    RankingsStartColumn = Mathf.Min(RankingsStartColumn + 1,
                        Mathf.Max(0, Mathf.CeilToInt(_rankings.Count / (float)Mathf.Max(1, _rankingsRows)) - _rankingsColumns));
                return true;
            case Key.Left:
                if (SelectedPanel == PanelRankings)
                    RankingsStartColumn = Mathf.Max(RankingsStartColumn - 1, 0);
                return true;

            case Key.Enter:
            case Key.KpEnter:
            case Key.Space:
                Activate(shift);
                return true;

            case Key.C when ctrl:
                // QC Ctrl+C: cycle scoreboard_columns → default → all.
                if (UiMode == 1 && SelectedPanel == PanelScoreboard) CycleColumnsLayout();
                return true;

            case Key.R when ctrl:
                // QC Ctrl+R: toggle the per-round score view.
                if (SelectedPanel == PanelScoreboard)
                    CommandSink?.Invoke("toggle hud_panel_scoreboard_scores_per_round");
                return true;

            case Key.T when ctrl:
                // QC Ctrl+T: `commandmode tell "<name>^7"` — open the command prompt PREFILLED with a tell to the
                // selected player (the player types the message and hits enter), then close the UI.
                if (SelectedPanel == PanelScoreboard && SelectedRow() is { } tell)
                {
                    OpenCommandPrompt?.Invoke($"tell \"{HudText.Strip(tell.Name)}\" ");
                    UiDisable();
                }
                return true;

            case Key.K when ctrl:
                // QC Ctrl+K: `vcall kick "<name>^7"` — call a kick vote on the selected player (the UI stays
                // open). Base's `vcall` alias expands to `vote call`, which is the port's verb.
                if (SelectedPanel == PanelScoreboard && SelectedRow() is { } kick)
                    CommandSink?.Invoke($"cmd vote call kick \"{HudText.Strip(kick.Name)}\"");
                return true;
        }
        return false;
    }

    /// <summary>
    /// QC <c>HUD_Scoreboard_InputEvent</c> acts on the key PRESS and returns true for the matching RELEASE too
    /// (every branch opens with <c>if (!key_pressed) return true;</c>), so the release never falls through to a
    /// gameplay bind — e.g. the Enter that spectated a player must not also re-trigger anything on release.
    /// This mirrors that: true for exactly the keys <see cref="UiHandleKey"/> claims.
    /// </summary>
    public bool UiConsumesRelease(Key key, bool ctrl)
    {
        if (UiMode == 0 || UiDisabling) return false;
        return key switch
        {
            Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right
                or Key.Enter or Key.KpEnter or Key.Space => true,
            Key.C or Key.R or Key.T or Key.K => ctrl,
            _ => false,
        };
    }

    /// <summary>QC the TAB panel cycle (scoreboard.qc:288-311): wraps, and SKIPS the Rankings panel when there
    /// are no records to scroll.</summary>
    private void CyclePanel(int dir)
    {
        int p = SelectedPanel;
        p += dir;
        if (p == PanelRankings && _rankings.Count == 0) p += dir;
        if (p < PanelScoreboard) p = PanelRankings;
        if (p > PanelRankings) p = PanelScoreboard;
        if (p == PanelRankings && _rankings.Count == 0) p = PanelScoreboard;
        SelectedPanel = p;
        _selectedPanelTime = UiNow();
        QueueRedraw();
    }

    /// <summary>QC the Up/Down arrow bodies (scoreboard.qc:318-408): step the selected TEAM (mode 2) or the
    /// selected PLAYER (mode 1) through the sorted list, with QC's "off the end = nothing selected" wrap.</summary>
    private void MoveSelection(int dir)
    {
        if (SelectedPanel != PanelScoreboard) return;

        if (UiMode == 2)
        {
            var teams = SortedTeams();
            if (teams.Count == 0) return;
            int i = teams.IndexOf(SelectedTeam);
            // QC: from "nothing selected" a step forward lands on the first entry, back on the last.
            int next = i < 0 ? (dir > 0 ? 0 : teams.Count - 1) : i + dir;
            SelectedTeam = (next < 0 || next >= teams.Count) ? 0 : teams[next];
        }
        else
        {
            if (_rows.Count == 0) return;
            int i = _rows.FindIndex(r => r.NetId == SelectedPlayerNetId);
            int next = i < 0 ? (dir > 0 ? 0 : _rows.Count - 1) : i + dir;
            SelectedPlayerNetId = (next < 0 || next >= _rows.Count) ? -1 : _rows[next].NetId;
        }
        QueueRedraw();
    }

    /// <summary>The teams present, in the same flag-aware order the tables are drawn in.</summary>
    private List<int> SortedTeams()
    {
        var teams = new List<int>();
        foreach (ScoreRow r in _rows)
            if (r.Team != Teams.None && !teams.Contains(r.Team)) teams.Add(r.Team);
        foreach (var kv in _teamScores)
            if (kv.Key != Teams.None && !teams.Contains(kv.Key)) teams.Add(kv.Key);
        teams.Sort((a, b) => CompareTeamTotals(b, a));
        return teams;
    }

    private ScoreRow? SelectedRow()
    {
        if (SelectedPlayerNetId < 0) return null;
        foreach (ScoreRow r in _rows) if (r.NetId == SelectedPlayerNetId) return r;
        return null;
    }

    /// <summary>QC the Enter/Space body (scoreboard.qc:421-437): mode 2 joins the picked team (Shift or no
    /// selection = auto) and closes; mode 1 spectates the picked player.</summary>
    private void Activate(bool shift)
    {
        if (SelectedPanel != PanelScoreboard) return;

        if (UiMode == 2)
        {
            // QC localcmd("cmd join <team>"). The port's equivalent client command is `selectteam`, which takes
            // the same colour names plus "auto".
            string team = (SelectedTeam == 0 || shift) ? "auto" : Teams.Name(SelectedTeam).ToLowerInvariant();
            CommandSink?.Invoke($"cmd selectteam {team}");
            UiDisable();
        }
        else if (SelectedRow() is { } row)
        {
            // QC localcmd("spectate <entnum+1>"). The port's `spectate` takes a NAME (or #id) — the scoreboard
            // row carries the name, and that is what the server's lookup matches on.
            CommandSink?.Invoke($"cmd spectate \"{HudText.Strip(row.Name)}\"");
        }
    }

    /// <summary>QC the Ctrl+C body (scoreboard.qc:443-465): cycle the user's saved column set → default → all.</summary>
    private void CycleColumnsLayout()
    {
        switch (_selectedColumnsLayout)
        {
            case 0:
                string saved = GlobalStr("scoreboard_columns");
                if (!string.IsNullOrEmpty(saved) && saved != "all" && saved != "default")
                {
                    CommandSink?.Invoke("scoreboard_columns_set");
                    _selectedColumnsLayout = 1;
                    break;
                }
                goto case 1;
            case 1:
                CommandSink?.Invoke("scoreboard_columns_set default");
                _selectedColumnsLayout = 2;
                break;
            default:
                CommandSink?.Invoke("scoreboard_columns_set all");
                _selectedColumnsLayout = 0;
                break;
        }
    }

    /// <summary>QC the selected-panel highlight alpha (scoreboard.qc:1751-1757 / 2288-2292): a white wash over the
    /// panel the keys act on — a steady 0.2 in team-selection mode, else a 0.3 flash that fades over half a second
    /// after each TAB so you can see where focus went.</summary>
    private float SelectedPanelHighlight(int panel)
    {
        if (UiMode == 0 || UiDisabling || SelectedPanel != panel) return 0f;
        if (UiMode == 2) return 0.2f;
        return 0.3f * Mathf.Max(0f, 1f - (UiNow() - _selectedPanelTime) * 2f);
    }

    // ---- header / footer settable surfaces (QC fraglimit/timelimit + map stats + respawn; networked) ----

    /// <summary>QC the fraglimit / pointlimit header value (Scoreboard_Fraglimit_Draw); 0 = none. Settable by the match layer.</summary>
    public int FragLimit { get; set; }
    /// <summary>QC TIMELIMIT (minutes); 0 = none. Settable by the match layer.</summary>
    public int TimeLimitMinutes { get; set; }
    /// <summary>QC <c>STAT(LEADLIMIT)</c> (scoreboard.qc:2546): the lead limit shown as the "^2+N" header term
    /// (Scoreboard_Fraglimit_Draw is_leadlimit=true). 0 = none. Drawn only when ll &gt; 0 &amp;&amp; (ll &lt; fl || fl &lt;= 0).</summary>
    public int LeadLimit { get; set; }
    /// <summary>QC <c>STAT(LEADLIMIT_AND_FRAGLIMIT)</c> (autocvar_leadlimit_and_fraglimit, scoreboard.qc:2547,2564):
    /// when set (and fraglimit &gt; 0) the lead/frag delimiter is "^7 &amp; " (both required) instead of "^7 / ".</summary>
    public bool LeadAndFragLimit { get; set; }
    /// <summary>QC the map name shown in the footer (Scoreboard footer "<map>"). Settable by the match layer.</summary>
    public string MapName { get; set; } = "";

    // Map stats (QC Scoreboard_MapStats_Draw STAT(MONSTERS_*/SECRETS_*)); -1 totals = no row. Networked → settable.
    public int MonstersKilled { get; set; } = -1;
    public int MonstersTotal { get; set; } = -1;
    public int SecretsFound { get; set; } = -1;
    public int SecretsTotal { get; set; } = -1;

    /// <summary>QC <c>STAT(RESPAWN_TIME)</c> (scoreboard.qc:2764) as networked to the owner
    /// (ClientNet.RespawnTimeStat): 0 = alive (no respawn line); otherwise the absolute respawn time, NEGATED
    /// while a respawn is imminent (DEAD_RESPAWNING). Fed by the match layer each frame; drives the three-state
    /// respawn line. Counted down against <see cref="RespawnServerTime"/> (the networked server time).</summary>
    public float RespawnStat { get; set; }

    /// <summary>The latest networked server time (ClientNet.LatestServerTime) to count <see cref="RespawnStat"/>
    /// down against (QC <c>time</c>). Fed alongside <see cref="RespawnStat"/>.</summary>
    public float RespawnServerTime { get; set; }

    /// <summary>QC <c>getcommandkey(_("jump"), "+jump")</c>: the key bound to +jump, shown in the "press X to
    /// respawn" line. Fed by the match layer (keybind lookup); defaults to "jump".</summary>
    public string RespawnJumpKey { get; set; } = "jump";

    // Accuracy grid (QC Scoreboard_AccuracyStats_Draw weapon_accuracy[]): per-weapon-id hit percentage [0..100],
    // -1 = the weapon was never fired (skipped). Networking the local player's accuracy is a follow-up
    // (HudManager's WeaponsPanel.SetAccuracy seam); until then this is empty and the grid is hidden.
    private readonly Dictionary<int, int> _accuracy = new();

    // Rankings (QC Scoreboard_Rankings_Draw race/CTS grecordtime/grecordholder). Race record networking is its
    // own data source (not present yet) — so this is left empty and the block is gated on race modes + data.
    private readonly List<(int timeEncoded, string holder)> _rankings = new();

    private readonly List<ScoreRow> _rows = new();
    private readonly Dictionary<int, int> _teamScores = new(); // team color code -> team score (QC team scores)

    // The parsed column layout (QC sbt_field[]), rebuilt when the layout generation changes.
    private readonly List<Column> _columns = new();
    private int _columnsForLayoutGen = -1;
    private string _columnsForSpec = "";

    /// <summary>Hidden by default; the owner toggles it (QC: held while the scoreboard key is down).</summary>
    public ScoreboardPanel() => Visible = false;

    /// <summary>
    /// QC <c>autocvar_scoreboard_columns</c>: an explicit column spec ("ping pl name | score …"); empty selects
    /// the built-in SCOREBOARD_DEFAULT_COLUMNS. Settable by the owner (the user's cvar). Triggers a relayout.
    /// </summary>
    public string ColumnSpec
    {
        get => _columnSpec;
        set { _columnSpec = value ?? ""; _columnsForLayoutGen = -1; QueueRedraw(); }
    }
    private string _columnSpec = "";

    // =====================================================================================
    //  Feed paths
    // =====================================================================================

    /// <summary>
    /// THE net path: replace the rows from a decoded <see cref="VortexArena.Net.ScoreboardWire"/> (full per-player
    /// columns + the entcs name/team slice). Resolves netId→local via <paramref name="localNetId"/>, hydrates
    /// each row's full column array, sets <see cref="TeamPlay"/> from <see cref="GameScores.Teamplay"/>, and
    /// applies the team totals. This is what makes the networked columns actually render (QC the scores stats →
    /// the scoreboard grid). Maps the wire columns (in <see cref="GameScores.NetworkedFields"/> order) back to a
    /// registry-indexed array so <see cref="ScoreRow.Col"/> reads the right field.
    /// </summary>
    public void SetWireRows(VortexArena.Net.ScoreboardWire wire, int localNetId,
        IReadOnlyCollection<int>? eliminatedNetIds = null)
    {
        _rows.Clear();
        _spectators.Clear();
        if (wire is not null)
        {
            IReadOnlyList<ScoreField> netFields = GameScores.NetworkedFields;
            int fieldCount = GameScores.FieldCount;
            ScoreField? scoreF = GameScores.Field("SCORE");
            ScoreField? deathsF = GameScores.Field("DEATHS");
            foreach (VortexArena.Net.ScoreRowWire wr in wire.Rows)
            {
                // QC Scoreboard_Spectators_Draw (scoreboard.qc:2369): a spectator/observer is NOT a score-table
                // row — list it in the spectator block instead. The wire carries the flag (the port has no
                // NUM_SPECTATOR team sentinel). Feed the networked per-row ping so spectators_showping renders it.
                if (wr.IsSpectator)
                {
                    _spectators.Add(new SpectatorRow(wr.Name, ping: wr.PingMs));
                    continue;
                }

                // expand the wire's NetworkedFields-ordered columns into a registry-indexed array. fieldCount can
                // be 0 before the score registry is populated, and wr.Columns may be null (the ScoreRowWire ctor
                // doesn't guard it) — both would crash the foreach below, so clamp/null-coalesce here.
                var cols = new int[System.Math.Max(0, fieldCount)];
                int[] wireCols = wr.Columns ?? System.Array.Empty<int>();
                int m = System.Math.Min(wireCols.Length, netFields.Count);
                for (int i = 0; i < m; i++)
                {
                    int rid = netFields[i].RegistryId;
                    if ((uint)rid < cols.Length) cols[rid] = wireCols[i];
                }

                int score = scoreF is not null && (uint)scoreF.RegistryId < cols.Length ? cols[scoreF.RegistryId] : 0;
                int deaths = deathsF is not null && (uint)deathsF.RegistryId < cols.Length ? cols[deathsF.RegistryId] : -1;
                _rows.Add(new ScoreRow(wr.Name, score, wr.Team, deaths, ping: wr.PingMs,
                    isLocal: wr.NetId == localNetId, columns: cols,
                    // QC pl.ping_packetloss: de-quantize the networked 0..255 loss byte to a 0..1 fraction.
                    packetLoss: wr.PacketLossByte / 255f,
                    // QC pl.eliminated (NET_HANDLE ENT_CLIENT_ELIMINATEDPLAYERS, client/main.qc:819): flag the
                    // rows the round-status block marked eliminated so DrawRow greys them.
                    eliminated: eliminatedNetIds is not null && eliminatedNetIds.Contains(wr.NetId),
                    // QC entcs handicap_level (scoreboard.qc:1003): the player_handicap icon level (0 = none).
                    handicapLevel: wr.HandicapLevel,
                    // QC pl.sv_entnum: the stable id for the playerid '#N' prefix + the final sort tiebreak.
                    netId: wr.NetId));
            }

            _teamScores.Clear();
            foreach ((int team, int sc) in wire.Teams)
                if (team != Teams.None) _teamScores[team] = sc;
        }
        TeamPlay = GameScores.Teamplay || _teamScores.Count > 0;
        SortRows();
        QueueRedraw();
    }

    /// <summary>
    /// Replace the scoreboard rows from the simple networked score records. The list is copied and sorted for
    /// display. Kept for callers that only have the #/name/score/deaths/ping slice.
    /// </summary>
    public void SetRows(IEnumerable<ScoreRow> rows)
    {
        _rows.Clear();
        if (rows is not null) _rows.AddRange(rows);
        SortRows();
        QueueRedraw();
    }

    /// <summary>
    /// Convenience: build rows from local <see cref="Player"/> actors. Name/score/team come from the entity
    /// (QC .netname/.frags/.team); deaths/ping are left unknown (only the server knows them) until the net
    /// layer feeds full rows via <see cref="SetRows"/>. The row matching <see cref="LocalPlayer"/> is flagged.
    /// </summary>
    public void SetPlayers(IEnumerable<Player> players)
    {
        _rows.Clear();
        if (players is not null)
            foreach (Player p in players)
            {
                if (p is null) continue;
                string name = string.IsNullOrEmpty(p.NetName) ? p.ClassName : p.NetName;
                // deaths/ping are server-only; leave unknown (-1) until full rows arrive via SetRows.
                _rows.Add(new ScoreRow(name, p.ScoreFrags, (int)p.Team,
                    deaths: -1, ping: -1, isLocal: ReferenceEquals(p, LocalPlayer)));
            }
        SortRows();
        QueueRedraw();
    }

    /// <summary>
    /// Set per-team scores for the team-panel totals (QC team scores), keyed by team color code
    /// (<see cref="Teams.Red"/> etc.). Implies <see cref="TeamPlay"/> when non-empty.
    /// </summary>
    public void SetTeamScores(IReadOnlyDictionary<int, int> teamScores)
    {
        _teamScores.Clear();
        if (teamScores is not null)
            foreach (var kv in teamScores) _teamScores[kv.Key] = kv.Value;
        TeamPlay = _teamScores.Count > 0;
        QueueRedraw();
    }

    /// <summary>QC <c>weapon_accuracy[]</c>: set the local player's per-weapon hit % (0..100; -1 = never fired)
    /// for the accuracy grid. Keyed by weapon registry id. The match layer feeds it when accuracy is networked.</summary>
    /// <summary>
    /// QC <c>g_inventory.inv_items[]</c> (common/items/inventory.qh): how many of each item the local player has
    /// picked up this match, keyed by the item's HUD icon name (QC <c>it.m_icon</c>, e.g. "health_mega",
    /// "armor_big", "ammo_rockets"). Feeds <see cref="DrawItemStats"/>. Empty hides the block.
    /// </summary>
    public void SetItemStats(IReadOnlyDictionary<string, int> counts)
    {
        _itemStats.Clear();
        if (counts is null) return;
        foreach (var kv in counts)
            if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key)) _itemStats[kv.Key] = kv.Value;
        QueueRedraw();
    }

    private readonly Dictionary<string, int> _itemStats = new();

    public void SetAccuracy(IReadOnlyDictionary<int, int> accuracy)
    {
        _accuracy.Clear();
        if (accuracy is not null) foreach (var kv in accuracy) _accuracy[kv.Key] = kv.Value;
        QueueRedraw();
    }

    /// <summary>QC <c>Scoreboard_Spectators_Draw</c> source: replace the spectator list (NUM_SPECTATOR players —
    /// the entcs slice with no/forfeit scores). Fed by the net layer; empty hides the section.</summary>
    public void SetSpectators(IEnumerable<SpectatorRow> spectators)
    {
        _spectators.Clear();
        if (spectators is not null) _spectators.AddRange(spectators);
        QueueRedraw();
    }

    /// <summary>Convenience overload: feed spectator names only (ping unknown). Kept for simple callers.</summary>
    public void SetSpectators(IEnumerable<string> names)
    {
        _spectators.Clear();
        if (names is not null) foreach (string n in names) _spectators.Add(new SpectatorRow(n));
        QueueRedraw();
    }

    /// <summary>QC the race/CTS rankings (Scoreboard_Rankings_Draw): an ordered best-time list (encoded
    /// hundredths + holder name). Networking records is a follow-up; until then this is empty.</summary>
    public void SetRankings(IEnumerable<(int timeEncoded, string holder)> rankings)
    {
        _rankings.Clear();
        if (rankings is not null) _rankings.AddRange(rankings);
        QueueRedraw();
    }

    // ---- race/CTS speed award (QC scoreboard.qc:2731 race_speedaward / _alltimebest) ----
    private int _speedAward;
    private string _speedAwardHolder = "";
    private int _speedAwardBest;
    private string _speedAwardBestHolder = "";

    /// <summary>QC the race/CTS speed award (Scoreboard_MainPanel scoreboard.qc:2731): the round-best (qu/s, rounded)
    /// + holder and the persisted all-time best + holder, shown as a line above the rankings in race/CTS modes.
    /// All zero/empty hides the line (QC <c>if (race_speedaward_alltimebest)</c>).</summary>
    public void SetSpeedAward(int speed, string holder, int best, string bestHolder)
    {
        _speedAward = speed;
        _speedAwardHolder = holder ?? "";
        _speedAwardBest = best;
        _speedAwardBestHolder = bestHolder ?? "";
        QueueRedraw();
    }

    // =====================================================================================
    //  Sorting (QC Scoreboard_ComparePlayerScores)
    // =====================================================================================

    private void SortRows()
    {
        // QC Scoreboard_ComparePlayerScores: by team (in team modes), then the per-mode primary, secondary, then
        // the remaining registry-order columns; spectators last. We sort by the networked primary/secondary keys
        // when the rows carry full columns; else fall back to score-desc then fewer-deaths.
        ScoreField? primary = GameScores.Primary;
        ScoreField? secondary = GameScores.Secondary;
        bool haveColumns = _rows.Count > 0 && _rows[0].Columns is not null;

        _rows.Sort((a, b) =>
        {
            if (!haveColumns)
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                int ad = a.Deaths < 0 ? int.MaxValue : a.Deaths;
                int bd = b.Deaths < 0 ? int.MaxValue : b.Deaths;
                return ad.CompareTo(bd);
            }
            // ComparePlayers>0 means the first arg ranks ahead; we want the better row FIRST (negative).
            int cmp = -CompareRows(a, b, primary, secondary);
            if (cmp != 0) return cmp;
            // QC Scoreboard_ComparePlayerScores final tiebreak (scoreboard.qc:1300): equal scores fall to
            // sv_entnum so the order is stable frame-to-frame (List.Sort is not a stable sort in .NET).
            return a.NetId.CompareTo(b.NetId);
        });
    }

    /// <summary>QC <c>Scoreboard_ComparePlayerScores</c> core (sans the team split, which the team grouping
    /// handles): primary, then secondary, then registry-order columns. Positive => <paramref name="a"/> ahead.</summary>
    private static int CompareRows(in ScoreRow a, in ScoreRow b, ScoreField? primary, ScoreField? secondary)
    {
        if (primary is not null)
        {
            int r = GameScores.CompareValues(a.Col(primary), b.Col(primary), primary.Flags);
            if (r != 0) return r;
        }
        if (secondary is not null && !ReferenceEquals(secondary, primary))
        {
            int r = GameScores.CompareValues(a.Col(secondary), b.Col(secondary), secondary.Flags);
            if (r != 0) return r;
        }
        foreach (ScoreField f in GameScores.Fields)
        {
            if (f.ClientOnly || f.Label.Length == 0) continue;
            if ((f.Flags & ScoreFlags.NotSortable) != 0) continue;
            if (ReferenceEquals(f, primary) || ReferenceEquals(f, secondary)) continue;
            int r = GameScores.CompareValues(a.Col(f), b.Col(f), f.Flags);
            if (r != 0) return r;
        }
        return 0;
    }

    // =====================================================================================
    //  Column layout (QC Cmd_Scoreboard_SetFields + SCOREBOARD_DEFAULT_COLUMNS)
    // =====================================================================================

    /// <summary>One scoreboard column (QC sbt_field[i] + sbt_field_title[i]).</summary>
    private readonly struct Column
    {
        public readonly ColumnKind Kind;     // the special-field kind (or Label for a SP_* field)
        public readonly ScoreField? Field;   // the backing SP_* field for Kind==Label / sort keys
        public readonly string Title;        // the header label
        public Column(ColumnKind kind, ScoreField? field, string title) { Kind = kind; Field = field; Title = title; }
    }

    private enum ColumnKind { Label, Name, Separator, Ping, Pl, Kdratio, Sum, Frags }

    /// <summary>
    /// QC <c>SCOREBOARD_DEFAULT_COLUMNS</c> (scoreboard.qc:748) — carried VERBATIM for fidelity. The token list
    /// is filtered per-gametype by <see cref="IsGametypeInFilter"/>; a token may carry a leading '?' (no warn)
    /// and a "+/-pattern/field" gametype filter.
    /// </summary>
    private const string DefaultColumns =
        "ping pl fps skill name |" +
        " -teams,rc,cts,surv,inv,lms/kills +ft,tdm,tmayhem/kills ?+rc,inv/kills" +
        " -teams,surv,lms/deaths +ft,tdm,tmayhem/deaths" +
        " +tdm/sum" +
        " -teams,lms,rc,cts,surv,inv,ka/suicides +ft,tdm,tmayhem/suicides ?+rc,inv/suicides" +
        " -cts,dm,tdm,surv,ka,ft,mayhem,tmayhem/frags" +
        " +tdm,ft,dom,ons,as,tmayhem/teamkills" +
        " -rc,cts,surv,nb/dmg -rc,cts,surv,nb/dmgtaken" +
        " +surv/survivals +surv/hunts" +
        " +ctf/pickups +ctf/fckills +ctf/returns +ctf/caps +ons/takes +ons/caps" +
        " +lms/lives +lms/rank" +
        " +kh/kckills +kh/losses +kh/caps" +
        " ?+rc/laps ?+rc/time +rc,cts/fastest" +
        " +as/objectives +nb/faults +nb/goals" +
        " +ka,tka/pickups +ka,tka/bckills +ka,tka/bctime +ft/revivals" +
        " +dom/ticks +dom/takes" +
        " -lms,rc,cts,inv,nb/score";

    /// <summary>QC <c>Cmd_Scoreboard_SetFields</c> (scoreboard.qc:767): parse the active column spec into the
    /// concrete column list for the current gametype. Rebuilt only when the layout generation or the spec
    /// changes (cheap "did the layout move?" gate, like NetworkedFields).</summary>
    private void EnsureColumns()
    {
        string gametype = GameScores.Gametype;
        bool teamplay = GameScores.Teamplay;
        // QC autocvar_scoreboard_columns: the live user cvar drives the layout (the settable ColumnSpec property
        // still wins when a host pushes one explicitly). Read here so `scoreboard_columns_set` — including the
        // interactive UI's Ctrl+C cycle — takes effect on the next draw with no extra plumbing.
        string effective = string.IsNullOrWhiteSpace(_columnSpec) ? GlobalStr("scoreboard_columns") : _columnSpec;
        effective ??= "";
        if (_columnsForLayoutGen == GameScores.LayoutGeneration && _columnsForSpec == effective && _columns.Count > 0)
            return;
        _columnsForLayoutGen = GameScores.LayoutGeneration;
        _columnsForSpec = effective;
        _columns.Clear();

        ScoreField? primary = GameScores.Primary;
        ScoreField? secondary = GameScores.Secondary;

        string spec = string.IsNullOrWhiteSpace(effective) ? DefaultColumns : effective;
        if (spec == "default" || spec == "expand_default") spec = DefaultColumns;
        // QC Cmd_Scoreboard_SetFields "all": every registered score field, after the standard identity columns.
        if (spec == "all")
        {
            var sb = new System.Text.StringBuilder("ping pl name |");
            foreach (ScoreField f in GameScores.NetworkedFields)
                if (!string.IsNullOrEmpty(f.Label)) sb.Append(' ').Append(f.Label);
            spec = sb.ToString();
        }

        bool haveName = false, haveSeparator = false, havePrimary = false, haveSecondary = false;
        foreach (string rawTok in spec.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string str = rawTok;
            if (str.StartsWith('?')) str = str[1..]; // nocomplain prefix (we never warn, so just strip it)

            int slash = str.IndexOf('/');
            if (slash >= 0)
            {
                string pattern = str[..slash];
                str = str[(slash + 1)..];
                if (!IsGametypeInFilter(gametype, teamplay, pattern))
                    continue;
            }

            str = str.ToLowerInvariant();
            switch (str)
            {
                case "ping": _columns.Add(new Column(ColumnKind.Ping, null, "Ping")); break;
                case "pl":   _columns.Add(new Column(ColumnKind.Pl, null, "Pl")); break;
                case "name":
                case "nick": _columns.Add(new Column(ColumnKind.Name, null, "Name")); haveName = true; break;
                case "|":    _columns.Add(new Column(ColumnKind.Separator, null, "")); haveSeparator = true; break;
                case "kd":
                case "kdr":
                case "kdratio": _columns.Add(new Column(ColumnKind.Kdratio, null, "K/D")); break;
                case "sum":
                case "diff":
                case "k-d":  _columns.Add(new Column(ColumnKind.Sum, null, "+/-")); break;
                case "frags": _columns.Add(new Column(ColumnKind.Frags, null, "Frags")); break;
                default:
                {
                    if (str == "damage") str = "dmg";
                    if (str == "damagetaken") str = "dmgtaken";
                    ScoreField? f = FieldByLabel(str);
                    if (f is null) continue; // unknown / server-disabled (fps/skill) — skip (we don't warn)
                    _columns.Add(new Column(ColumnKind.Label, f, f.Label));
                    if (ReferenceEquals(f, primary)) havePrimary = true;
                    if (ReferenceEquals(f, secondary)) haveSecondary = true;
                    break;
                }
            }
            if (_columns.Count >= MaxColumns) break;
        }

        // QC: auto-insert any missing name / separator / primary / secondary (the have_* fixups).
        if (primary is not null && (primary.Flags & ScoreFlags.AllowHide) != 0) havePrimary = true;
        if (secondary is null || ReferenceEquals(secondary, primary)) haveSecondary = true;
        else if ((secondary.Flags & ScoreFlags.AllowHide) != 0) haveSecondary = true;

        if (!haveName)
        {
            _columns.Insert(0, new Column(ColumnKind.Name, null, "Name"));
            if (!haveSeparator) { _columns.Insert(1, new Column(ColumnKind.Separator, null, "")); haveSeparator = true; }
        }
        else if (!haveSeparator)
        {
            _columns.Add(new Column(ColumnKind.Separator, null, "")); haveSeparator = true;
        }
        if (!haveSecondary && secondary is not null)
            _columns.Add(new Column(ColumnKind.Label, secondary, secondary.Label));
        if (!havePrimary && primary is not null)
            _columns.Add(new Column(ColumnKind.Label, primary, primary.Label));
    }

    private const int MaxColumns = 24; // QC MAX_SBT_FIELDS

    /// <summary>QC <c>FOREACH(Scores, str == strtolower(scores_label(it)))</c>: find a column by its (active) label.</summary>
    private static ScoreField? FieldByLabel(string label)
    {
        foreach (ScoreField f in GameScores.Fields)
            if (f.Label.Length != 0 && f.Label.ToLowerInvariant() == label) return f;
        return null;
    }

    /// <summary>
    /// QC <c>isGametypeInFilter(gt, teamplay, teamspawns=false, pattern)</c> (common/util.qc:1187): does the
    /// active gametype pass a "+/-pattern" include/exclude list? The pattern is a comma list of mode NetNames
    /// plus the pseudo-gametypes "teams"/"noteams" (and "race" for rc/cts). A leading '-' excludes; '+' (or no
    /// prefix) includes. Faithful to the QC comma-delimited substring matching.
    /// </summary>
    private static bool IsGametypeInFilter(string gametype, bool teamplay, string pattern)
    {
        string sub = "," + gametype + ",";
        string sub2 = teamplay ? ",teams," : ",noteams,";
        string sub4 = (gametype == "rc" || gametype == "cts") ? ",race," : null!;

        if (pattern.StartsWith('-'))
        {
            string p = "," + pattern[1..] + ",";
            if (p.Contains(sub)) return false;
            if (p.Contains(sub2)) return false;
            if (sub4 is not null && p.Contains(sub4)) return false;
            return true;
        }
        else
        {
            string body = pattern.StartsWith('+') ? pattern[1..] : pattern;
            string p = "," + body + ",";
            // QC: pass if the gametype OR teams/noteams (OR race) is present.
            if (p.Contains(sub)) return true;
            if (p.Contains(sub2)) return true;
            if (sub4 is not null && p.Contains(sub4)) return true;
            return false;
        }
    }

    // =====================================================================================
    //  Per-field value formatting (QC Scoreboard_GetField)
    // =====================================================================================

    /// <summary>The result of formatting one field: the display string + its color (QC sbt_field_rgb).</summary>
    private readonly struct FieldText
    {
        public readonly string Text;
        public readonly Color Color;
        public FieldText(string text, Color color) { Text = text; Color = color; }
    }

    /// <summary>QC <c>Scoreboard_GetField</c> (scoreboard.qc:1029): format a column's value for a row, honoring
    /// SP_PING colorization, SP_FRAGS=kills-suicides, SP_KDRATIO, SP_SUM=kills-deaths, SP_DMG/DMGTAKEN ('N.N k'),
    /// and the default <see cref="GameScores.ScoreString"/> for a labeled column (TIME/RANK/HIDE_ZERO aware).</summary>
    private FieldText GetField(in ScoreRow r, in Column col)
    {
        Color white = new(1f, 1f, 1f, 1f);
        var inv0 = System.Globalization.CultureInfo.InvariantCulture;
        // QC scoreboard.qc:1047-1049: when scores_per_round is on, the count-style columns (frags/kdr/sum/dmg/score)
        // are divided by the player's SP_ROUNDS_PL; rounds_played==0 disables averaging for that row/cell.
        int roundsPlayed = ScoresPerRound() ? ColOf(r, "ROUNDS_PL") : 0;
        switch (col.Kind)
        {
            case ColumnKind.Ping:
                // QC SP_PING (scoreboard.qc:1060): the networked per-row ping, colorized by the ping bands. A
                // negative value (unknown / bot, not networked) shows a neutral dash rather than the QC ">>>"
                // no-scores glyph (which has a specific meaning); 0 = connecting → "N/A".
                if (r.Ping < 0) return new FieldText("-", new Color(1f, 1f, 1f, 0.5f));
                if (r.Ping == 0) return new FieldText("N/A", white);
                return new FieldText(r.Ping.ToString(), PingColor(r.Ping));

            case ColumnKind.Pl:
            {
                // QC SP_PL (scoreboard.qc:1070-1082): blank when there's no loss; else show ceil(pl*100),
                // red-tinted by severity ('1 0.5 0.5' - '0 0.5 0.5' * bound(0, pl/0.2, 1); 20% loss = full red).
                // The port doesn't track movement loss, so only packet loss contributes (QC's tmp == 0 branch).
                float pl = r.PacketLoss;
                if (pl <= 0f) return new FieldText("", white);
                int v = Mathf.CeilToInt(pl * 100f);
                float sev = Mathf.Clamp(pl / 0.2f, 0f, 1f);
                Color c = new(1f, 0.5f - 0.5f * sev, 0.5f - 0.5f * sev, 1f);
                return new FieldText(v.ToString(), c);
            }

            case ColumnKind.Name:
            {
                // QC Scoreboard_AddPlayerId (scoreboard.qc:1216): with hud_panel_scoreboard_playerid set, the
                // name cell is prefixed with the player's id, e.g. "#3 " (playerid_prefix + sv_entnum + suffix).
                // Off (0) by default → just the name. Read live so a console toggle takes effect.
                if (CvarF("playerid", 0f) != 0f && r.NetId > 0)
                {
                    string pre = CvarStr("playerid_prefix"); if (pre.Length == 0) pre = "#";
                    string suf = CvarStr("playerid_suffix"); if (suf.Length == 0) suf = " ";
                    return new FieldText($"^7{pre}{r.NetId}{suf}{r.Name}", white);
                }
                return new FieldText(r.Name, white);
            }

            case ColumnKind.Separator:
                return new FieldText("", white);

            case ColumnKind.Frags:
            {
                // QC SP_FRAGS (scoreboard.qc:1090): kills - suicides; per-round → "%.1f" of f/rounds_played.
                int frags = ColOf(r, "KILLS") - ColOf(r, "SUICIDES");
                if (roundsPlayed != 0)
                    return new FieldText((frags / (float)roundsPlayed).ToString("0.0", inv0), white);
                return new FieldText(frags.ToString(), white);
            }

            case ColumnKind.Kdratio:
            {
                // QC SP_KDRATIO (scoreboard.qc:1096): three branches.
                //  denom==0 → green, raw kills (per-round: "%.1f" of num/rounds_played)
                //  num<=0   → red,   "%.1f" num/denom (per-round: "%.2f" num/(denom*rounds_played))
                //  else     → white, "%.1f" num/denom (per-round: "%.2f" num/(denom*rounds_played))
                int num = ColOf(r, "KILLS"), denom = ColOf(r, "DEATHS");
                if (denom == 0)
                {
                    string s = roundsPlayed != 0
                        ? (num / (float)roundsPlayed).ToString("0.0", inv0)
                        : num.ToString(inv0);
                    return new FieldText(s, new Color(0f, 1f, 0f, 1f));
                }
                bool red = num <= 0;
                string str = roundsPlayed != 0
                    ? (num / (float)(denom * roundsPlayed)).ToString("0.00", inv0)
                    : (num / (float)denom).ToString("0.0", inv0);
                return new FieldText(str, red ? new Color(1f, 0f, 0f, 1f) : white);
            }

            case ColumnKind.Sum:
            {
                // QC SP_SUM (scoreboard.qc:1125): kills - deaths; green>0 / white==0 / red<0; per-round → "%.1f".
                int sum = ColOf(r, "KILLS") - ColOf(r, "DEATHS");
                Color c = sum > 0 ? new Color(0f, 1f, 0f, 1f) : sum == 0 ? white : new Color(1f, 0f, 0f, 1f);
                if (roundsPlayed != 0)
                    return new FieldText((sum / (float)roundsPlayed).ToString("0.0", inv0), c);
                return new FieldText(sum.ToString(), c);
            }

            default: // ColumnKind.Label
            {
                ScoreField? f = col.Field;
                if (f is null) return new FieldText("", white);
                int v = r.Col(f);

                // QC SP_SKILL (scoreboard.qc:1138): -1 → "...", -2 → "N/A", else the int. (Networked only when
                // sv_showskill enables the column; the value rides the row columns once present.)
                if (f.Name == "SKILL")
                    return new FieldText(v == -1 ? "..." : v == -2 ? "N/A" : v.ToString(inv0), white);

                // QC SP_FPS (scoreboard.qc:1147): 0 → "N/A" (0 ping = connecting/bot) or "..." white; else the int
                // colored red≤32 / yellow 64-96 / white≥128 via sbt_field_rgb.{y,z} = bound(0,(fps-32)*0.03125,1)
                // and bound(0,(fps-96)*0.03125,1) — x stays 1.
                if (f.Name == "FPS")
                {
                    if (v == 0)
                        return new FieldText(r.Ping == 0 ? "N/A" : "...", white);
                    float g = Mathf.Clamp((v - 32) * 0.03125f, 0f, 1f);
                    float b = Mathf.Clamp((v - 96) * 0.03125f, 0f, 1f);
                    return new FieldText(v.ToString(inv0), new Color(1f, g, b, 1f));
                }

                // QC SP_DMG/SP_DMGTAKEN (scoreboard.qc:1165): "%.1f k" of v/1000 (per-round: "%.2f k" of
                // v/(1000*rounds_played)).
                if (f.Name == "DMG" || f.Name == "DMGTAKEN")
                {
                    string s = roundsPlayed != 0
                        ? (v / (1000f * roundsPlayed)).ToString("0.00", inv0) + " k"
                        : (v / 1000f).ToString("0.0", inv0) + " k";
                    return new FieldText(s, white);
                }

                Color c = ReferenceEquals(f, GameScores.Primary) ? new Color(1f, 1f, 0f, 1f)
                        : ReferenceEquals(f, GameScores.Secondary) ? new Color(0f, 1f, 1f, 1f) : white;
                // QC default/SP_SCORE: ScoreString honors per-round averaging directly.
                return new FieldText(GameScores.ScoreString(f.Flags, v, roundsPlayed), c);
            }
        }
    }

    private static int ColOf(in ScoreRow r, string fieldName)
    {
        ScoreField? f = GameScores.Field(fieldName);
        return f is null ? 0 : r.Col(f);
    }

    /// <summary>QC SP_PING colorization (scoreboard.qc:1060-1067): green→yellow→red by the ping bands
    /// <c>hud_panel_scoreboard_ping_low=20</c> / <c>ping_medium=80</c> / <c>ping_high=200</c> with the QC default
    /// band colors COLOR_LOW='0 1 0', COLOR_MED='1 1 0', COLOR_HIGH='1 0 0'. Read live from the shared store so
    /// console/menu edits take effect (was previously hardcoded 75/200/500, an unintended value gap).</summary>
    private Color PingColor(int ping)
    {
        int low = (int)CvarF("ping_low", 20f);
        int med = (int)CvarF("ping_medium", 80f);
        int high = (int)CvarF("ping_high", 200f);
        Color cLow = new(0f, 1f, 0f, 1f), cMed = new(1f, 1f, 0f, 1f), cHigh = new(1f, 0f, 0f, 1f);
        if (ping < low) return cLow;
        // QC lerps use the band deltas directly; guard against degenerate (equal) bands.
        if (ping < med) return cLow.Lerp(cMed, med > low ? (ping - low) / (float)(med - low) : 1f);
        if (ping < high) return cMed.Lerp(cHigh, high > med ? (ping - med) / (float)(high - med) : 1f);
        return cHigh;
    }

    // =====================================================================================
    //  Draw
    // =====================================================================================

    /// <summary>QC <c>scoreboard_fade_alpha</c> step: ramp the fade toward <see cref="Active"/> using the
    /// fadein/fadeout speeds (per second), self-driving via <see cref="_Process"/> so the cross-fade animates
    /// even though the panel is not <see cref="IsDynamic"/>. Hides the panel once fully faded out.</summary>
    public override void _Process(double delta)
    {
        // QC: fade in at fadeinspeed when active, out at fadeoutspeed when not (0 speed => instant).
        float target = _active ? 1f : 0f;
        if (!Mathf.IsEqualApprox(_fadeAlpha, target))
        {
            float dt = (float)delta;
            float speed = _active ? GlobalF("hud_panel_scoreboard_fadeinspeed", 10f)
                                  : GlobalF("hud_panel_scoreboard_fadeoutspeed", 5f);
            if (speed <= 0f || dt <= 0f) _fadeAlpha = target;
            else if (_active) _fadeAlpha = Mathf.Min(1f, _fadeAlpha + dt * speed);
            else _fadeAlpha = Mathf.Max(0f, _fadeAlpha - dt * speed);

            if (_fadeAlpha <= 0f && !_active) Visible = false;
            QueueRedraw();
        }
        else if (_active)
        {
            // Faded fully in and stable. Reveal if needed, and — since the panel is NOT IsDynamic — force a repaint
            // each frame while the local respawn countdown is live so DrawRespawn re-runs and the seconds actually
            // tick (#23: the countdown was frozen because nothing re-drew the otherwise-static board between
            // score-version changes; RespawnStat/RespawnServerTime are fed every frame but their setters don't
            // QueueRedraw). Cheap — only while the local player is dead with the board up.
            if (!Visible) { Visible = true; QueueRedraw(); }
            else if (RespawnStat != 0f) QueueRedraw();
        }

        // QC scoreboard_acc_fade_alpha / scoreboard_itemstats_fade_alpha (scoreboard.qc:1807, 1980):
        //   fade = min(scoreboard_fade_alpha, fade + frametime * 10)
        // These MUST advance here, not in the draw: the panel is not IsDynamic, so once the board has settled it
        // stops repainting — a ramp stepped inside DrawPanel would freeze partway and leave the accuracy / item
        // grids permanently dimmed. Stepping them in _Process (where the delta is a real frame time) and
        // repainting while they move keeps the fade-in animating and then costs nothing.
        float statTarget = _active ? PanelFade() : 0f;
        float before = _accFade + _itemFade;
        float step = (float)delta * 10f;
        _accFade = _active ? Mathf.Min(statTarget, _accFade + step) : 0f;
        _itemFade = _active ? Mathf.Min(statTarget, _itemFade + step) : 0f;
        if (!Mathf.IsEqualApprox(before, _accFade + _itemFade))
            QueueRedraw();

        // The interactive UI animates (the focused-panel flash decays over ~0.5 s) and must feel responsive to
        // the arrow keys, so it repaints every frame while it is up. Only while it is up.
        if (UiMode != 0)
            QueueRedraw();
    }

    /// <summary>The effective panel alpha this frame: the HUD fade × the scoreboard's own fade-in/out. When the
    /// owner never sets <see cref="Active"/> (legacy Visible-toggle callers) <see cref="_fadeAlpha"/> stays 0, so
    /// we treat a visible-but-never-faded panel as fully opaque (1) — i.e. fade is opt-in.</summary>
    private float PanelFade()
    {
        float sb = _everActive ? _fadeAlpha : 1f;
        return Mathf.Clamp(LiveFgAlpha / Mathf.Max(0.0001f, Cfg.FgAlpha), 0f, 1f) * sb;
    }
    private bool _everActive;

    // =================================================================================================
    //  Base metrics (QC hud_fontsize + panel_bg_padding/panel_bg_border). Every distance in the scoreboard
    //  is expressed in these units — Base has NO hardcoded pixel sizes, which is why its layout holds at any
    //  resolution. Cached at the top of each draw so the helpers below can read them.
    // =================================================================================================

    private int _fs = 11;         // QC hud_fontsize.x/.y (square) — the resolved, height-locked body font px
    private float _rowH = 14f;    // QC 1.25 * hud_fontsize.y — the universal row pitch
    private float _bgPad = 3f;    // QC panel_bg_padding
    private float _bgBorder = 2f; // QC panel_bg_border
    private bool _hasBg;          // QC panel.current_panel_bg != "0"

    /// <summary>QC the <c>rgb</c> threaded through every block: <c>panel_bg_color</c> in FFA (luma
    /// "0 0.3 0.5"), the TEAM color for a team's own table. Drives the row highlights + header tint.</summary>
    private Color _blockRgb = new(0f, 0.3f, 0.5f);

    /// <summary>QC <c>sbt_bg_alpha</c> = <c>..._table_bg_alpha * panel_fg_alpha</c> (luma 0 = the tiled
    /// <c>gfx/scoreboard/scoreboard_bg</c> backing is off).</summary>
    private float SbtBgAlpha(float fade) => Mathf.Clamp(CvarF("table_bg_alpha", 0f), 0f, 1f) * Cfg.FgAlpha * fade;

    private void CacheMetrics()
    {
        _fs = Mathf.Max(6, Cfg.FontSize);
        _rowH = 1.25f * _fs;
        _bgPad = Mathf.Max(0f, Cfg.Padding);
        _bgBorder = Mathf.Max(0f, Cfg.BgBorder);
        _hasBg = !string.IsNullOrEmpty(Cfg.Bg) && Cfg.Bg != "0";
        _blockRgb = Cfg.BgColor;
    }

    /// <summary>
    /// QC the block preamble every scoreboard sub-panel shares (Scoreboard_MakeTable / _AccuracyStats_Draw /
    /// _ItemStats_Draw / _MapStats_Draw / _Rankings_Draw all open with the identical five lines):
    /// <code>
    ///   drawstring(pos + eX * panel_bg_padding, title, hud_fontsize, '1 1 1', panel_fg_alpha, …);
    ///   pos.y += 1.25 * hud_fontsize.y;
    ///   if (panel.current_panel_bg != "0") pos.y += panel_bg_border;
    ///   panel_pos = pos; panel_size.y = contentHeight + panel_bg_padding * 2;
    ///   HUD_Panel_DrawBg();
    ///   end_pos = panel_pos + eY * (panel_size.y + 0.5 * hud_fontsize.y);
    ///   if (panel.current_panel_bg != "0") end_pos.y += panel_bg_border * 2;
    ///   panel_pos += '1 1 0' * panel_bg_padding; panel_size -= '2 2 0' * panel_bg_padding;
    /// </code>
    /// Draws the (optional) title and the skin frame, then hands back the PADDED content rect to draw into and
    /// the y the caller should continue from. This frame is the single biggest piece of the Xonotic scoreboard
    /// look the port was missing — the board is otherwise transparent, so without it there is no chrome at all.
    /// </summary>
    private Rect2 BeginBlock(float x, float w, ref float y, float contentHeight, float fade,
        string? title, Color? frameRgb, float alphaMul, out float endY)
    {
        if (!string.IsNullOrEmpty(title))
        {
            DrawText(new Vector2(x + _bgPad, y), title!, new Color(1f, 1f, 1f, Cfg.FgAlpha * fade * alphaMul), _fs);
            y += _rowH;
            if (_hasBg) y += _bgBorder;
        }

        var frame = new Rect2(x, y, w, contentHeight + _bgPad * 2f);
        DrawBackgroundRect(frame, LiveBgAlpha * alphaMul, frameRgb);

        endY = frame.Position.Y + frame.Size.Y + 0.5f * _fs;
        if (_hasBg) endY += _bgBorder * 2f;

        return new Rect2(frame.Position.X + _bgPad, frame.Position.Y + _bgPad,
                         Mathf.Max(0f, frame.Size.X - _bgPad * 2f), Mathf.Max(0f, frame.Size.Y - _bgPad * 2f));
    }

    /// <summary>QC <c>drawpic_tiled(pos, "gfx/scoreboard/scoreboard_bg", bg_size, size, rgb, sbt_bg_alpha, …)</c>
    /// — the tiled table backing, scaled by <c>..._table_bg_scale</c> (luma 0.25). No-op when sbt_bg_alpha is 0
    /// (the shipped luma default), which is why the stock board reads as transparent.</summary>
    private void DrawTableBackingTiled(Rect2 rect, Color rgb, float sbtBgAlpha)
    {
        if (sbtBgAlpha <= 0.001f || rect.Size.X <= 0f || rect.Size.Y <= 0f) return;
        Texture2D? tex = TextureCache.Get("gfx/scoreboard/scoreboard_bg");
        var tint = new Color(rgb.R, rgb.G, rgb.B, sbtBgAlpha);
        if (tex is null) { DrawRect(rect, tint); return; }
        float scale = CvarF("table_bg_scale", 0.25f);
        if (scale <= 0f) scale = 0.25f;
        // Godot tiles at the texture's native size; emulate QC's bg_size (imagesize * scale) by drawing the
        // tile grid explicitly so the pattern density matches Base.
        Vector2 tile = tex.GetSize() * scale;
        if (tile.X < 1f || tile.Y < 1f) { DrawRect(rect, tint); return; }
        for (float ty = 0f; ty < rect.Size.Y; ty += tile.Y)
            for (float tx = 0f; tx < rect.Size.X; tx += tile.X)
            {
                var cell = new Rect2(rect.Position.X + tx, rect.Position.Y + ty,
                    Mathf.Min(tile.X, rect.Size.X - tx), Mathf.Min(tile.Y, rect.Size.Y - ty));
                DrawTextureRectRegion(tex, cell,
                    new Rect2(Vector2.Zero, tex.GetSize() * new Vector2(cell.Size.X / tile.X, cell.Size.Y / tile.Y)),
                    tint);
            }
    }

    protected override void DrawPanel()
    {
        if (_active) _everActive = true;
        float fade = PanelFade();
        if (fade <= 0f) return; // QC: scoreboard_fade_alpha <= 0 → draw nothing

        EnsureColumns();
        CacheMetrics();
        _overflowRows = 0; // QC Scoreboard_DrawOthers: reset the dropped-row counter for this draw

        // NO full-panel background. Base's Scoreboard_Draw (scoreboard.qc:2455+) never calls HUD_Panel_DrawBg for
        // the board as a whole — the scoreboard is transparent and the chrome comes from the per-BLOCK skin
        // frames drawn by Scoreboard_MakeTable / _AccuracyStats_Draw / _ItemStats_Draw / _MapStats_Draw /
        // _Rankings_Draw around their own content (see BeginBlock), plus the per-row highlight fills.
        // (HUD configure mode still gets its forced frame from HudPanel._Draw's pre-pass.)

        float x = 0f;
        float w = Size2.X;
        float y = 0f;

        // A too-small panel (resolved size clamps to 8px) yields a non-positive content width; laying out
        // columns against it produces off-panel garbage, so stop here.
        if (w <= 1f) return;

        // QC scoreboard.qc:2497 — `if (scoreboard_ui_enabled) drawfill('0 0 0', eX * vid_conwidth + eY *
        // vid_conheight, '0 0 0', 0.7 * panel_fade_alpha)`: the interactive board dims the whole world behind it
        // so the keyboard focus reads as a modal overlay. Panel-local coords, so offset by our own position to
        // cover the viewport.
        if (UiMode != 0 && !UiDisabling)
        {
            Vector2 vp = GetViewportRect().Size;
            DrawRect(new Rect2(-Position, vp), new Color(0f, 0f, 0f, 0.7f * fade));
        }

        y = DrawGameInfoHeader(x, w, y, fade);

        // QC scoreboard.qc:2583-2585: space between the Game Info Section and the score table.
        y += _fs * 0.3f;
        if (_hasBg) y += _bgBorder;

        // ---- the score table(s) (QC Scoreboard_MakeTable, per team in teamplay, once in FFA) ----
        y = DrawTables(x, w, y, fade);

        // QC scoreboard.qc:2711-2760 block order:
        //   spectators (position 0) → accuracy → item stats → spectators (1) → speed award + rankings →
        //   spectators (2) → map stats → spectators (3) → respawn line.
        int specPos = (int)Mathf.Clamp(CvarF("spectators_position", 1f), 0f, 3f);

        if (specPos == 0) y = DrawSpectators(x, w, y, fade);
        y = DrawAccuracy(x, w, y, fade);
        y = DrawItemStats(x, w, y, fade);
        if (specPos == 1) y = DrawSpectators(x, w, y, fade);
        y = DrawSpeedAward(x, w, y, fade);
        y = DrawRankings(x, w, y, fade);
        if (specPos == 2) y = DrawSpectators(x, w, y, fade);
        y = DrawMapStats(x, w, y, fade);
        if (specPos == 3) y = DrawSpectators(x, w, y, fade);
        DrawRespawn(x, w, y, fade);
    }

    /// <summary>QC the Game Info Section (scoreboard.qc:2502-2581): "Next map: …", the big gametype banner
    /// (right-aligned bold), then the limits line (right) + the "Map: … N/M players" line (left).</summary>
    private float DrawGameInfoHeader(float x, float w, float y, float fade)
    {
        // QC scoreboard.qc:2508-2515 sb_gameinfo_type_fontsize = hud_fontsize * 2.5,
        //                            sb_gameinfo_detail_fontsize = hud_fontsize * 1.3.
        int typeSize = Mathf.RoundToInt(_fs * 2.5f);
        int detailSize = Mathf.RoundToInt(_fs * 1.3f);
        var fg = new Color(1f, 1f, 1f, Cfg.FgAlpha * fade);

        // QC scoreboard.qc:2517-2521: "Next map: X" is drawn BEFORE the title (so a long map name doesn't
        // cover it), at typeSize - 1.25 fontsize down from the top.
        if (!string.IsNullOrEmpty(NextMap))
            DrawColored(new Vector2(x + _fs * 0.5f, y + typeSize - _fs * 1.25f),
                $"^7Next map: ^9{NextMap}", fg, _fs);

        // Gametype banner — QC scoreboard.qc:2525:
        //   draw_beginBoldFont();
        //   drawcolorcodedstring(pos + '0.5 0 0' * (panel_size.x - stringwidth(str, ...)), str,
        //                        sb_gameinfo_type_fontsize /* = hud_fontsize * 2.5 */, ...);
        //   draw_endBoldFont();
        // The '0.5 0 0' offset CENTERS the banner across the panel (contrast the limits line just below, which
        // Base offsets by '1 0 0' — that one really is right-aligned). The port had read this as right-aligned,
        // which parked the gametype name in the top-right corner under the kill feed instead of heading the
        // board. Bold, and 2.5× the body font like Base.
        // QC scoreboard.qc:2519-2523: the team picker replaces the gametype name with "Team Selection".
        string banner = UiMode == 2 ? "Team Selection"
            : (!string.IsNullOrEmpty(GametypeName) ? GametypeName : Title);
        DrawTextCentered2Bold(new Vector2(x, y), w, banner, fg, typeSize);
        y += typeSize; // QC: pos.y += sb_gameinfo_type_fontsize.y

        // QC scoreboard.qc:2531-2541: in team-selection mode the limits/map lines are replaced by the two key
        // hints, both centered at the detail font size.
        if (UiMode == 2)
        {
            string l1 = SelectedTeam != 0
                ? "^7Press ^3SPACE^7 to join the selected team"
                : "^7Press ^3SPACE^7 to auto-select a team and join";
            DrawTextCentered2(new Vector2(x, y), w, l1, fg, detailSize);
            y += detailSize + _fs * 0.3f;
            DrawTextCentered2(new Vector2(x, y), w, "^7Press ^3TAB ^7to select a specific team", fg, detailSize);
            return y + detailSize;
        }

        // QC scoreboard.qc:2573: the limits line, RIGHT-aligned ('1 0 0' offset), at the detail font size.
        string limits = BuildLimitsHeader();
        if (limits.Length != 0)
            DrawColoredRight(x + w, y, w, limits, fg, detailSize);

        // QC scoreboard.qc:2574-2579: "Map: <name>    N/M players", LEFT-aligned, same detail font size.
        string mapLine = "";
        if (!string.IsNullOrEmpty(MapName)) mapLine = $"^7Map: ^2{MapName}";
        // QC: if (campaign) str = "" — the player-count is meaningless in single-player.
        if (!Campaign && (PlayerCount > 0 || MaxPlayerCount > 0))
        {
            int max = MaxPlayerCount > 0 ? MaxPlayerCount : PlayerCount;
            mapLine = (mapLine.Length != 0 ? mapLine + "    " : "") + $"^5{PlayerCount}^7/^5{max} ^7players";
        }
        if (mapLine.Length != 0)
            DrawColored(new Vector2(x, y), mapLine, fg, detailSize);

        return y + detailSize;
    }

    /// <summary>QC the limits line (scoreboard.qc:2542-2572): "^3&lt;minutes&gt;" then "^7 / " then the
    /// <c>Scoreboard_Fraglimit_Draw</c> "^5&lt;limit&gt; &lt;label&gt;" (label = "points"/"" for score/fastest).
    /// Color-coded for the right-aligned game-info line. Empty when no limits.</summary>
    private string BuildLimitsHeader()
    {
        // QC scoreboard.qc:2544-2571: tl / fl / ll / ll_and_fl. m_hidelimits suppresses fl + ll entirely
        // (only the timelimit shows); the only stock gametype that sets it is LMS.
        string str = "";
        if (TimeLimitMinutes > 0) str = $"^3{TimeLimitMinutes}";

        // QC scoreboard.qc:2551: if (!gametype.m_hidelimits) — skip the whole frag/lead block when hidden.
        if (!HideLimits)
        {
            int fl = FragLimit;
            int ll = LeadLimit;
            if (fl > 0)
            {
                if (str.Length != 0) str += "^7 / ";   // QC delimiter
                str += FraglimitDraw(fl, isLeadLimit: false);
            }
            // QC: ll > 0 && (ll < fl || fl <= 0) — don't show a lead limit that can never be reached before fraglimit.
            if (ll > 0 && (ll < fl || fl <= 0))
            {
                if (TimeLimitMinutes > 0 || fl > 0)
                    // QC: "^7 & " when leadlimit_and_fraglimit (both needed) and fl > 0, else "^7 / ".
                    str += (LeadAndFragLimit && fl > 0) ? "^7 & " : "^7 / ";
                str += FraglimitDraw(ll, isLeadLimit: true);
            }
        }
        return str;
    }

    /// <summary>QC <c>Scoreboard_Fraglimit_Draw</c> (scoreboard.qc:2392): format one limit using the primary key's
    /// label/flags. The lead-limit term reads "^2+&lt;N&gt; &lt;label&gt;"; the frag/point limit reads
    /// "^5&lt;N&gt; &lt;label&gt;". The label is "points" for "score", "" for "fastest", else the label itself.</summary>
    private string FraglimitDraw(int limit, bool isLeadLimit)
    {
        ScoreField? primary = GameScores.Primary;
        string label = TeamPlay ? GameScores.TeamLabel(GameScores.TeamPrimarySlot) : (primary?.Label ?? "score");
        ScoreFlags flags = TeamPlay ? GameScores.TeamFlagsPrimary : (primary?.Flags ?? ScoreFlags.None);
        string limitStr = GameScores.ScoreString(flags, limit);
        string unit = label == "score" ? "points" : label == "fastest" ? "" : label;
        string prefix = isLeadLimit ? "^2+" : "^5";
        return $"{prefix}{limitStr} {unit}".TrimEnd();
    }

    /// <summary>
    /// QC <c>TeamScore_Compare</c> over the panel's OWN networked team totals (not the static GameScores team
    /// state — that is only mirrored server-side, so a remote client's would be empty). Honors the primary team
    /// slot's SFL_LOWER_IS_BETTER. Positive => team <paramref name="a"/> ranks ahead. <see cref="_teamScores"/>
    /// holds the primary slot's total (the wire ships the primary team score).
    /// </summary>
    private int CompareTeamTotals(int a, int b)
    {
        int va = _teamScores.TryGetValue(a, out int x) ? x : 0;
        int vb = _teamScores.TryGetValue(b, out int y) ? y : 0;
        int r = GameScores.CompareValues(va, vb, GameScores.TeamFlagsPrimary);
        return r != 0 ? r : a - b; // QC the final team-id tiebreak
    }

    // =================================================================================================
    //  The score table(s) — QC Scoreboard_MakeTable (scoreboard.qc:1657) + the teamplay loop that wraps it
    //  (scoreboard.qc:2594-2706).
    // =================================================================================================

    /// <summary>
    /// QC the table section of <c>Scoreboard_Draw</c>: in teamplay, one framed table PER TEAM (each tinted with
    /// that team's colour, with the team's score drawn bold at 1.5x font beside the table); in FFA a single
    /// table in the panel's own bg colour.
    /// </summary>
    private float DrawTables(float x, float w, float y, float fade)
    {
        if (!TeamPlay)
        {
            _blockRgb = Cfg.BgColor;
            return DrawOneTable(x, w, y, fade, _rows, Teams.None, _blockRgb);
        }

        var teamsSeen = new List<int>();
        foreach (ScoreRow r in _rows)
            if (r.Team != Teams.None && !teamsSeen.Contains(r.Team)) teamsSeen.Add(r.Team);
        foreach (var kv in _teamScores)
            if (kv.Key != Teams.None && !teamsSeen.Contains(kv.Key)) teamsSeen.Add(kv.Key);
        teamsSeen.Sort((a, b) => CompareTeamTotals(b, a));

        // QC scoreboard.qc:2621-2629 — team size total (sum over all non-spectator teams), for the "N/M" string.
        int teamSizePos = (int)CvarF("team_size_position", 0f);
        int teamSizeTotal = 0;
        if (teamSizePos != 0)
            foreach (int t in teamsSeen) teamSizeTotal += TeamRowCount(t);

        // QC ..._bg_teams_color_team: tint each team table's FRAME by team colour x factor (0 = the raw colour).
        float teamBgFactor = TeamBgColorFactor();

        foreach (int team in teamsSeen)
        {
            Color raw = TeamColor(team, 1f);
            Color frameRgb = teamBgFactor > 0f
                ? new Color(raw.R * teamBgFactor, raw.G * teamBgFactor, raw.B * teamBgFactor)
                : raw;
            _blockRgb = raw;

            var rows = new List<ScoreRow>();
            foreach (ScoreRow r in _rows) if (r.Team == team) rows.Add(r);

            DrawTeamScoreBeside(x, w, y, team, raw, teamSizePos, teamSizeTotal, fade);
            y = DrawOneTable(x, w, y, fade, rows, team, frameRgb);
        }

        // Any team-less rows (shouldn't happen in teamplay, but never silently drop players).
        var loose = new List<ScoreRow>();
        foreach (ScoreRow r in _rows) if (r.Team == Teams.None) loose.Add(r);
        if (loose.Count > 0)
        {
            _blockRgb = Cfg.BgColor;
            y = DrawOneTable(x, w, y, fade, loose, Teams.None, Cfg.BgColor);
        }
        return y;
    }

    /// <summary>
    /// QC <c>Scoreboard_MakeTable</c>: frame + rounded header pic + tiled backing + the header row and the player
    /// rows, all on the 1.25x hud_fontsize pitch. <c>..._maxheight</c> caps the visible rows; the remainder is
    /// reported through <see cref="_overflowRows"/> (QC Scoreboard_DrawOthers).
    /// </summary>
    private float DrawOneTable(float x, float w, float y, float fade, List<ScoreRow> rows, int team, Color frameRgb)
    {
        // QC scoreboard.qc:1659-1677 — max_players from ..._maxheight, a fraction of vid_conheight (the VIEWPORT
        // height, not this panel's), less the block padding.
        int maxPlayers = 999;
        float maxHeightFrac = CvarF("maxheight", 0.6f);
        if (maxHeightFrac > 0f)
        {
            float height = maxHeightFrac * GetViewportRect().Size.Y - _bgPad * 2f;
            maxPlayers = Mathf.Max(1, Mathf.FloorToInt(height / _rowH));
            if (maxPlayers == rows.Count) maxPlayers = 999;
        }
        // QC Scoreboard_MakeTable: when the list overflows, the LAST visible slot is spent on the
        // Scoreboard_DrawOthers summary row rather than a player — so the summary sits inside the table, not
        // below the frame. (Drawing it after all `shown` rows put it outside the block entirely.)
        bool others = rows.Count > maxPlayers;
        int shown = others ? Mathf.Max(1, maxPlayers - 1) : rows.Count;
        if (others) _overflowRows += rows.Count - shown;

        // QC: panel_size.y = 1.25 * hud_fontsize.y * (1 + bound(1, team_size, max_players)) — the header row plus
        // the visible player rows, plus the others-summary row when the list overflowed.
        float contentH = _rowH * (1 + Mathf.Max(1, shown) + (others ? 1 : 0));
        float blockTop = y;
        Rect2 content = BeginBlock(x, w, ref y, contentH, fade, null, frameRgb, 1f, out float endY);
        if (content.Size.X <= 1f) return endY;

        // QC scoreboard.qc:1748-1757 — the interactive UI washes the FOCUSED sub-panel white. In team-selection
        // mode only the table of the currently picked team lights up (that IS the selection); in navigation mode
        // the whole scoreboard panel flashes when TAB moves focus to it.
        float selHl = SelectedPanelHighlight(PanelScoreboard);
        if (selHl > 0f && (UiMode != 2 || SelectedTeam == team))
            DrawRect(new Rect2(x, blockTop, w, endY - blockTop),
                new Color(1f, 1f, 1f, selHl * Cfg.FgAlpha * fade));

        float sbtBg = SbtBgAlpha(fade);

        // QC scoreboard.qc:1703-1704 — the rounded header pic, tinted rgb + '0.5 0.5 0.5'.
        var headerRect = new Rect2(content.Position, new Vector2(content.Size.X, _rowH));
        if (sbtBg > 0.001f)
        {
            var hTint = new Color(Mathf.Min(1f, frameRgb.R + 0.5f), Mathf.Min(1f, frameRgb.G + 0.5f),
                                  Mathf.Min(1f, frameRgb.B + 0.5f), sbtBg);
            Texture2D? hdr = TextureCache.Get("gfx/scoreboard/scoreboard_tableheader");
            if (hdr is not null) DrawTextureRect(hdr, headerRect, false, hTint);
            else DrawRect(headerRect, hTint);

            // QC scoreboard.qc:1709-1711 — the tiled table body backing under the rows.
            DrawTableBackingTiled(new Rect2(content.Position.X, content.Position.Y + _rowH,
                content.Size.X, content.Size.Y - _rowH), frameRgb, sbtBg);
        }

        Layout layout = ComputeLayout(content.Position.X, content.Size.X);
        layout.TableTop = content.Position.Y;
        float ty = content.Position.Y;
        DrawTableHeaderRow(layout, ref ty, fade, frameRgb, content.Size.Y);

        if (rows.Count == 0)
        {
            DrawTextCentered(new Vector2(content.Position.X, ty + (_rowH - _fs) * 0.5f), content.Size.X,
                "(no players)", new Color(1f, 1f, 1f, 0.4f * fade), _fs);
            return endY;
        }

        for (int i = 0; i < shown; i++)
            DrawRow(layout, rows[i], i + 1, ref ty, fade, i, team);

        // QC Scoreboard_DrawOthers: the compressed "and N more" row in the last slot.
        if (others)
        {
            string more = $"... and {rows.Count - shown} more";
            DrawText(new Vector2(layout.X + _fs * 0.5f, ty + (_rowH - _fs) * 0.5f), more,
                new Color(1f, 1f, 1f, 0.5f * fade), _fs);
        }
        return endY;
    }

    /// <summary>
    /// QC scoreboard.qc:2632-2686: the team's primary score drawn BOLD at <c>hud_fontsize * 1.5</c> just outside
    /// the table (left by default, right when <c>..._team_size_position == 1</c>), with the optional
    /// "size/total" head-count on the opposite side.
    /// </summary>
    private void DrawTeamScoreBeside(float x, float w, float y, int team, Color rgb,
        int teamSizePos, int teamSizeTotal, float fade)
    {
        int score = _teamScores.TryGetValue(team, out int s) ? s : 0;
        int big = Mathf.RoundToInt(_fs * 1.5f);
        var col = new Color(rgb.R, rgb.G, rgb.B, Cfg.FgAlpha * fade);
        // The table frame starts one title-row below y (BeginBlock advances y), so line the score up with it.
        float lineY = y + (_hasBg ? _bgBorder : 0f) + _fs;

        string str = score.ToString();
        bool scoreOnLeft = teamSizePos != 1;
        float gap = _fs * 0.5f + (_hasBg ? _bgBorder : 0f);
        if (scoreOnLeft)
            DrawTextRightBold(x - gap, lineY, str, col, big);
        else
            DrawTextBold(new Vector2(x + w + gap + _fs, lineY), str, col, big);

        if (teamSizePos != 0)
        {
            string sizeStr = TeamRowCount(team).ToString();
            string totalStr = $"/{teamSizeTotal}";
            if (teamSizePos == 1)
            {
                float rx = x - gap;
                DrawTextRight(rx, lineY, w, totalStr, new Color(1f, 1f, 1f, Cfg.FgAlpha * fade), _fs);
                DrawTextRightBold(rx - MeasureText(totalStr, _fs), lineY, sizeStr, col, big);
            }
            else
            {
                float sx = x + w + gap + _fs;
                DrawTextBold(new Vector2(sx, lineY), sizeStr, col, big);
                DrawText(new Vector2(sx + MeasureBold(sizeStr, big), lineY + _fs * 0.5f), totalStr,
                    new Color(1f, 1f, 1f, Cfg.FgAlpha * fade), _fs);
            }
        }
    }

    /// <summary>QC <c>tm.team_size</c>: the count of (non-spectator) score rows on a team, computed locally from
    /// the fed rows (the wire ships per-team totals but not the per-team head-count separately).</summary>
    private int TeamRowCount(int team)
    {
        int n = 0;
        foreach (ScoreRow r in _rows) if (r.Team == team) n++;
        return n;
    }

    /// <summary>
    /// QC <c>Scoreboard_DrawHeader</c> (scoreboard.qc:1319): the column-title row. Titles are drawn at
    /// <c>rgb * 1.5</c> (the block colour brightened, not a fixed blue), and every ODD column gets a black
    /// <c>sbt_highlight_alpha</c> fill running the FULL height of the table — that vertical column banding is a
    /// signature part of the Xonotic table and the port had none of it.
    /// </summary>
    private void DrawTableHeaderRow(Layout layout, ref float y, float fade, Color rgb, float tableHeight)
    {
        // QC rgb * 1.5 (clamped by the renderer).
        var titleCol = new Color(Mathf.Min(1f, rgb.R * 1.5f), Mathf.Min(1f, rgb.G * 1.5f),
                                 Mathf.Min(1f, rgb.B * 1.5f), SbtFgAlpha() * fade);
        // A pure-black/very dark block colour would render invisible titles; Base's luma colour is bright
        // enough, but keep a floor so a user-picked dark bg_color still reads.
        if (titleCol.R + titleCol.G + titleCol.B < 0.6f)
            titleCol = new Color(0.75f, 0.85f, 1f, titleCol.A);

        bool hl = TableHighlight();
        float hlA = TableHighlightAlpha() * fade;
        float textY = y + (_rowH - _fs) * 0.5f; // QC text_offset = eY * (1.25 - 1) / 2 * hud_fontsize.y

        // Rank gutter (the port's extra "#" column — Base folds the rank into the row order, but the gutter is
        // already part of this port's Layout, so title it consistently).
        DrawText(new Vector2(layout.RankX + _fs * 0.5f, textY), "#", titleCol, _fs);

        for (int i = 0; i < _columns.Count; i++)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Separator) continue;

            // QC scoreboard.qc:1333-1334 / 1359-1361: alternating column bands, full table height.
            if (hl && (i % 2) != 0)
            {
                float cx = layout.ColX[i];
                float cw = layout.ColW[i];
                DrawRect(new Rect2(cx - _fs * 0.5f, y, cw + _fs, tableHeight - (y - layout.TableTop)),
                    new Color(0f, 0f, 0f, hlA));
            }

            if (c.Kind == ColumnKind.Name)
                DrawText(new Vector2(layout.ColX[i], textY), c.Title, titleCol, _fs);
            else
                DrawTextRightCondensed(layout.ColRight[i], textY, c.Title, titleCol, _fs, layout.ColTitleCondense[i]);
        }
        y += _rowH;
    }

    /// <summary>QC <c>sbt_fg_alpha</c> = <c>..._table_fg_alpha * panel_fg_alpha</c>.</summary>
    private float SbtFgAlpha() => Mathf.Clamp(CvarF("table_fg_alpha", 0.9f), 0f, 1f) * Cfg.FgAlpha;

    // ---- live behavior cvars (QC autocvar_hud_panel_scoreboard_table_*; shared store via the base CvarF) ----

    /// <summary>QC <c>..._table_highlight_alpha_eliminated</c> (scoreboard.qc:77, luma default 0.6): the
    /// eliminated-row grey-out strength, read live from the shared store with the shipped default.</summary>
    private float EliminatedAlpha() => Mathf.Clamp(CvarF("table_highlight_alpha_eliminated", 0.6f), 0f, 1f);

    /// <summary>QC <c>..._table_highlight</c> (default on): alternate-row striping enabled.</summary>
    private bool TableHighlight() => CvarF("table_highlight", 1f) != 0f;
    /// <summary>QC <c>..._table_highlight_alpha</c> (0.2): alternate-row stripe strength.</summary>
    private float TableHighlightAlpha() => Mathf.Clamp(CvarF("table_highlight_alpha", 0.2f), 0f, 1f);
    /// <summary>QC <c>..._table_highlight_alpha_self</c> (0.4): the local player's row highlight strength.</summary>
    private float SelfHighlightAlpha() => Mathf.Clamp(CvarF("table_highlight_alpha_self", 0.4f), 0f, 1f);
    /// <summary>QC <c>..._table_fg_alpha_self</c> (1): the local player's row text alpha (vs ..._table_fg_alpha 0.9).</summary>
    private float SelfFgAlpha() => Mathf.Clamp(CvarF("table_fg_alpha_self", 1f), 0f, 1f);
    /// <summary>QC <c>..._table_fg_alpha</c> (0.9): the non-self row text alpha.</summary>
    private float RowFgAlpha() => Mathf.Clamp(CvarF("table_fg_alpha", 0.9f), 0f, 1f);
    /// <summary>QC <c>..._bg_teams_color_team</c> (0): tint a team section's bg by the team color × this factor.</summary>
    private float TeamBgColorFactor() => CvarF("bg_teams_color_team", 0f);
    /// <summary>QC <c>..._respawntime_decimals</c> (1): decimals shown in the respawn countdown (0 = whole sec).</summary>
    private int RespawnDecimals() => (int)Mathf.Clamp(CvarF("respawntime_decimals", 1f), 0f, 3f);
    /// <summary>QC <c>..._accuracy</c> (true): show the accuracy stats block.</summary>
    private bool AccuracyEnabled() => CvarF("accuracy", 1f) != 0f;

    /// <summary>QC <c>Scoreboard_AccuracyStats_WouldDraw</c> warmup gate (scoreboard.qc:1864): the accuracy block
    /// is hidden during warmup (the stats aren't meaningful until the match proper). Fed by the match layer.</summary>
    public bool MatchWarmup { get; set; }
    /// <summary>QC <c>..._spectators_showping</c> (true): show ping next to spectator names.</summary>
    private bool SpectatorsShowPing() => CvarF("spectators_showping", 1f) != 0f;
    /// <summary>QC <c>autocvar_hud_panel_scoreboard_scores_per_round</c> (scoreboard.qc:105, default 0): when set,
    /// frags/kdr/sum/dmg/score are shown as per-round averages (divided by SP_ROUNDS_PL). Toggled by Ctrl+R in the
    /// interactive UI (not yet ported) — read live so a console "toggle" still takes effect.</summary>
    private bool ScoresPerRound() => CvarF("scores_per_round", 0f) != 0f;

    /// <summary>QC <c>Scoreboard_DrawOthers</c> (scoreboard.qc:1571): how many rows were dropped because the
    /// panel filled up, so the table can draw the "... and N more" overflow line. Reset each draw.</summary>
    private int _overflowRows;

    /// <summary>QC <c>Scoreboard_DrawItem</c> (scoreboard.qc:1388): one player row — highlight fill, the
    /// self-indicator, then every column's value. All metrics on the 1.25x hud_fontsize pitch.</summary>
    private void DrawRow(Layout layout, in ScoreRow r, int rank, ref float y, float fade, int rowParity, int team)
    {
        float rowH = _rowH;

        // QC scoreboard.qc:1396-1405: while the navigation UI owns the scoreboard panel, the ONLY row highlight
        // is the keyboard selection (at 0.44) — the self/alternating fills are suppressed so the cursor is
        // unambiguous. Otherwise: self > alternating stripe, both in the block `rgb` (the TEAM colour in
        // teamplay, panel_bg_color in FFA — Base does NOT use white here).
        Color rgb = team != Teams.None ? TeamColor(team, 1f) : _blockRgb;
        bool navSelecting = UiMode == 1 && !UiDisabling && SelectedPanel == PanelScoreboard;
        if (navSelecting)
        {
            if (r.NetId == SelectedPlayerNetId && SelectedPlayerNetId >= 0)
                DrawRect(new Rect2(layout.X, y, layout.W, rowH),
                    new Color(rgb.R, rgb.G, rgb.B, 0.44f * Cfg.FgAlpha * fade));
        }
        else if (r.IsLocal)
            DrawRect(new Rect2(layout.X, y, layout.W, rowH),
                new Color(rgb.R, rgb.G, rgb.B, SelfHighlightAlpha() * fade));
        else if (TableHighlight() && (rowParity % 2) == 0)
            DrawRect(new Rect2(layout.X, y, layout.W, rowH),
                new Color(rgb.R, rgb.G, rgb.B, TableHighlightAlpha() * fade));

        // QC scoreboard.qc:1519-1520: grey out an eliminated player's row (the eliminatedPlayers bitfield)
        // with a BLACK fill at hud_panel_scoreboard_table_highlight_alpha_eliminated (shipped luma skin: 0.6).
        if (r.Eliminated)
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), new Color(0f, 0f, 0f, EliminatedAlpha() * fade));

        // QC ..._table_fg_alpha / _self: self rows are brighter than the rest.
        float rowAlpha = (r.IsLocal ? SelfFgAlpha() : RowFgAlpha()) * Cfg.FgAlpha * fade;
        Color rowFg = new(1f, 1f, 1f, rowAlpha);
        float textY = y + (rowH - _fs) * 0.5f; // QC: center text vertically in the 1.25x row

        // QC scoreboard.qc:1411-1412: a "self indicator" beside the self row — U+25C0 BLACK LEFT-POINTING
        // TRIANGLE just outside the table's right edge, in the row colour.
        if (r.IsLocal)
            DrawText(new Vector2(layout.X + layout.W + _fs * 0.5f, textY), "◀",
                new Color(rgb.R, rgb.G, rgb.B, Cfg.FgAlpha * fade), _fs);

        DrawText(new Vector2(layout.RankX + _fs * 0.5f, textY), rank.ToString(), rowFg, _fs);

        for (int i = 0; i < _columns.Count; i++)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Separator) continue;
            FieldText ft = GetField(r, c);
            if (c.Kind == ColumnKind.Name)
            {
                float nameX = layout.ColX[i];
                // QC scoreboard.qc:1003-1009 — the player_handicap extra icon (a 32x32 square drawn next to the
                // name) when handicap_level != 0, tinted '1 0 0' + '0 1 1' * ((16 - lvl) / 15): white at level 1,
                // red at level 16. Draw the REAL gfx/scoreboard/player_handicap art from the mounted game data
                // (sbt_field_icon_extra[1]) tinted with the EXACT Base formula; fall back to a flat colored
                // square if the texture can't be resolved. Offset the name so it doesn't overlap either way.
                if (r.HandicapLevel != 0)
                {
                    int lvl = r.HandicapLevel;
                    float t = (16f - lvl) / 15f; // 1.0 @ lvl 1 (white) → 0.0 @ lvl 16 (red)
                    Color hc = new(1f, t, t, rowAlpha); // '1 0 0' + '0 1 1' * t
                    float sq = _fs;
                    var iconRect = new Rect2(nameX, textY, sq, sq);
                    Texture2D? icon = TextureCache.Get("gfx/scoreboard/player_handicap");
                    if (icon is not null)
                        DrawTextureRect(icon, iconRect, false, hc);
                    else
                        DrawRect(iconRect, hc);
                    nameX += sq + _fs * 0.25f;
                }
                DrawColored(new Vector2(nameX, textY), ft.Text, rowFg, _fs);
            }
            else
                DrawTextRight(layout.ColRight[i], textY, layout.ColW[i],
                    ft.Text, new Color(ft.Color.R, ft.Color.G, ft.Color.B, ft.Color.A * rowAlpha), _fs);
        }

        y += rowH;
    }

    // ---- spectators / map stats / respawn / accuracy / rankings footer (QC the footer draws) ----

    /// <summary>QC <c>Scoreboard_Spectators_Draw</c> (scoreboard.qc:2364): a "Spectators (N)" bold header then
    /// the spectator names (with ping when ..._spectators_showping), wrapped to the panel width.</summary>
    private float DrawSpectators(float x, float w, float y, float fade)
    {
        if (_spectators.Count == 0) return y;
        if (y > Size2.Y - _rowH * 2f) return y;

        // QC scoreboard.qc:2375-2378: the "Spectators (N)" header is drawn in the BOLD font.
        DrawTextBold(new Vector2(x, y), $"Spectators ({_spectators.Count})",
            new Color(1f, 1f, 1f, Cfg.FgAlpha * fade), _fs);
        y += _rowH;

        bool showPing = SpectatorsShowPing();
        float cx = x + _fs * 0.5f;
        float rowAlpha = SbtFgAlpha() * fade;
        float gap = _fs * 1.5f;
        foreach (SpectatorRow sp in _spectators)
        {
            // ping prefix (QC SP_PING field shown before the name when aligned-off / inline otherwise).
            string pingTxt = (showPing && sp.Ping >= 0) ? (sp.Ping == 0 ? "N/A" : sp.Ping.ToString()) : "";
            float pingW = pingTxt.Length != 0 ? MeasureText(pingTxt, _fs) + _fs * 0.5f : 0f;
            float nameW = MeasureText(HudText.Strip(sp.Name), _fs);

            if (cx + pingW + nameW + gap > x + w && cx > x + _fs * 0.5f)
            {
                cx = x + _fs * 0.5f; y += _rowH;
                if (y > Size2.Y - _rowH) break;
            }
            if (pingTxt.Length != 0)
            {
                DrawText(new Vector2(cx, y), pingTxt,
                    new Color(PingColor(sp.Ping).R, PingColor(sp.Ping).G, PingColor(sp.Ping).B, rowAlpha), _fs);
                cx += pingW;
            }
            DrawColored(new Vector2(cx, y), sp.Name, new Color(1f, 1f, 1f, rowAlpha), _fs);
            cx += nameW + gap;
        }
        // QC: pos.y += 1.25 * hud_fontsize.y; then + 0.5 * hud_fontsize.y after the block.
        return y + _rowH + _fs * 0.5f;
    }

    /// <summary>QC <c>Scoreboard_MapStats_Draw</c> (scoreboard.qc:2094): a framed "Map stats:" block with the
    /// monsters-killed / secrets-found key-value rows (key left, value right-aligned in the block).</summary>
    private float DrawMapStats(float x, float w, float y, float fade)
    {
        bool hasMonsters = MonstersTotal > 0;
        bool hasSecrets = SecretsTotal > 0;
        int rows = (hasMonsters ? 1 : 0) + (hasSecrets ? 1 : 0);
        if (rows == 0) return y;
        if (y > Size2.Y - _rowH * (rows + 2)) return y;

        Rect2 c = BeginBlock(x, w, ref y, _fs * rows, fade, "Map stats:", _blockRgb, 1f, out float endY);
        DrawTableBackingTiled(c, _blockRgb, SbtBgAlpha(fade));

        var body = new Color(1f, 1f, 1f, SbtFgAlpha() * fade);
        float ry = c.Position.Y;
        if (hasMonsters) ry = MapStatsKeyValue(c, ry, "Monsters killed:", $"{MonstersKilled}/{MonstersTotal}", body);
        if (hasSecrets) ry = MapStatsKeyValue(c, ry, "Secrets found:", $"{SecretsFound}/{SecretsTotal}", body);
        return endY;
    }

    /// <summary>QC <c>MapStats_DrawKeyValue</c> (scoreboard.qc:2081): key at +0.25 font from the left, value
    /// right-aligned to the block's right edge less the same margin.</summary>
    private float MapStatsKeyValue(Rect2 block, float y, string key, string value, Color col)
    {
        DrawText(new Vector2(block.Position.X + _fs * 0.25f, y), key, col, _fs);
        DrawTextRight(block.Position.X + block.Size.X - _fs * 0.25f, y, block.Size.X, value, col, _fs);
        return y + _fs;
    }

    /// <summary>QC the respawn-status line (scoreboard.qc:2763-2796): "^1Respawning in ^3N^1..." (awaiting),
    /// "You are dead, wait ^3N^7 before respawning" (cooldown), or "press jump to respawn" (ready). The decimals
    /// shown follow ..._respawntime_decimals (QC count_seconds_decs vs count_seconds(ceil)).</summary>
    private float DrawRespawn(float x, float w, float y, float fade)
    {
        // QC scoreboard.qc:2763-2796: float respawn_time = STAT(RESPAWN_TIME); the line shows only when not in
        // intermission and respawn_time != 0. The stat is the absolute respawn time, NEGATED while awaiting respawn.
        float respawnTime = RespawnStat;
        if (respawnTime == 0f) return y;
        if (y > Size2.Y - 24f) return y;
        float now = RespawnServerTime;

        string s;
        if (respawnTime < 0f)
        {
            // QC: a negative number means we are awaiting respawn (time value still the same); un-mark it.
            respawnTime = -respawnTime;
            if (respawnTime < now)
                s = ""; // QC: a few frames while the server is respawning — empty so the height doesn't jump
            else
                s = $"^1Respawning in ^3{FormatRespawnSeconds(respawnTime - now)}^1...";
        }
        else if (now < respawnTime)
        {
            // QC: "You are dead, wait N before respawning" (cooldown before a respawn is even allowed).
            s = $"^7You are dead, wait ^3{FormatRespawnSeconds(respawnTime - now)}^7 before respawning";
        }
        else
        {
            // QC: time >= respawn_time → "You are dead, press JUMP to respawn".
            s = $"^7You are dead, press ^2{RespawnJumpKey}^7 to respawn";
        }

        // QC scoreboard.qc:2795-2796: pos.y += 1.2 * hud_fontsize.y, then the line is CENTERED across the panel.
        y += 1.2f * _fs;
        if (s.Length == 0) return y; // keep the height stable (QC draws an empty string for one frame)
        DrawTextCentered2(new Vector2(x, y), w, s, new Color(1f, 1f, 1f, Cfg.FgAlpha * fade), _fs);
        return y + _rowH;
    }

    /// <summary>QC <c>count_seconds_decs(s, respawntime_decimals)</c> vs <c>count_seconds(ceil(s))</c>: the
    /// respawn countdown number, with the configured decimals (..._respawntime_decimals) or whole-second ceil.</summary>
    private string FormatRespawnSeconds(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int dec = RespawnDecimals();
        return dec > 0
            ? seconds.ToString("0." + new string('0', dec), System.Globalization.CultureInfo.InvariantCulture)
            : Mathf.CeilToInt(seconds).ToString();
    }

    private float DrawAccuracy(float x, float w, float y, float fade)
    {
        if (!AccuracyEnabled()) return y;             // QC ..._accuracy gate
        if (MatchWarmup) return y;                    // QC Scoreboard_AccuracyStats_WouldDraw: hidden in warmup
        // QC cl_invasion.qc MUTATOR_HOOKFUNCTION(cl_inv, DrawScoreboardItemStats) returns ISGAMETYPE(INVASION):
        // the item-stats (weapon accuracy) panel is hidden in Invasion because monsters are not valid accuracy
        // targets (sv_invasion.qc AccuracyTargetValid → MUT_ACCADD_INVALID), so the stats would be meaningless.
        if (GameScores.Gametype == "inv") return y;
        // QC cl_nexball.qc MUTATOR_HOOKFUNCTION(cl_nb, DrawScoreboardAccuracy) / DrawScoreboardItemStats: both return
        // false in Nexball — there is no firing (football mode) or only the BallStealer (basketball), so per-weapon
        // accuracy stats are irrelevant. QC DrawScoreboardAccuracy returns false unconditionally for nexball.
        if (GameScores.Gametype == "nb") return y;
        if (_accuracy.Count == 0) return y;           // not networked yet — block hidden (QC gates on data)

        // QC scoreboard.qc:1810-1831 — which weapons get a cell:
        //   * WEP_TYPE_OTHER (turrets, vehicle guns, the ball stealer …) is ALWAYS skipped ("not for damaging
        //     people"), and so are WEP_FLAG_HIDDEN / WEP_FLAG_MUTATORBLOCKED weapons with no stat;
        //   * a weapon with no stat is skipped unless the player OWNS it or it exists in the map
        //     (weapons_stat / weapons_inmap) — so an all-weapons grid never appears on a map that has three guns.
        // Registry order keeps the grid stable frame to frame.
        _accCells.Clear();
        foreach (Weapon wep in Weapons.All)
        {
            if (wep is null) continue;
            if ((wep.SpawnFlags & WeaponFlags.TypeOther) != 0) continue;
            if (!_accuracy.TryGetValue(wep.RegistryId, out int pct)) continue;
            if (pct < 0)
            {
                // No stat: only show it if it is a weapon the player could realistically use here.
                if ((wep.SpawnFlags & (WeaponFlags.Hidden | WeaponFlags.MutatorBlocked)) != 0) continue;
                if (!OwnedOrInMap(wep)) continue;
            }
            _accCells.Add((wep, pct));
        }
        int weaponCnt = _accCells.Count;
        if (weaponCnt == 0) return y;

        // QC scoreboard.qc:1836-1841: rows/columns + the cell metrics.
        //   weapon_height = hud_fontsize.y * 2.3 / hud_panel_weapons_aspect;  height = weapon_height + fontsize.y
        int rows = 1;
        if (CvarF("accuracy_doublerows", 0f) != 0f && weaponCnt >= Mathf.FloorToInt(Weapons.Count * 0.5f))
            rows = 2;
        int columns = Mathf.Max(1, Mathf.CeilToInt(weaponCnt / (float)rows));
        float aspect = Mathf.Max(0.001f, GlobalF("hud_panel_weapons_aspect", 1f));
        float weaponH = _fs * 2.3f / aspect;
        float cellH = weaponH + _fs;

        if (y > Size2.Y - (cellH * rows + _rowH * 2f)) return y;

        // QC scoreboard.qc:1948: average = floor(sum * 100 / weapons_with_stats + 0.5) over the FIRED weapons.
        int sum = 0, n = 0;
        foreach ((Weapon _, int pct) in _accCells) if (pct >= 0) { sum += pct; n++; }
        int avg = n > 0 ? Mathf.FloorToInt((float)sum / n + 0.5f) : 0;

        // The ramp itself is stepped in _Process (see there); read it here.
        float a = _accFade;

        Rect2 c = BeginBlock(x, w, ref y, cellH * rows, fade,
            $"Accuracy stats (average {avg}%)", _blockRgb, a / Mathf.Max(0.0001f, fade), out float endY);
        if (c.Size.X <= 1f) return endY;

        DrawTableBackingTiled(c, _blockRgb, SbtBgAlpha(fade) * a / Mathf.Max(0.0001f, fade));
        DrawStatGridChrome(c, columns, rows, cellH, weaponH, a);

        // QC scoreboard.qc:1894-1895: with accuracy_nocolors the % is drawn flat white, bypassing the ramp.
        bool noColors = CvarF("accuracy_nocolors", 0f) != 0f;
        DrawStatGrid(c, columns, rows, cellH, weaponH, a, _accCells.Count, (i, cellRect, textY) =>
        {
            (Weapon wep, int pct) = _accCells[i];
            // QC: an owned-but-never-fired weapon draws its icon at 0.2 * sbt_fg_alpha and NO number.
            float iconAlpha = (pct >= 0 ? SbtFgAlpha() : 0.2f * SbtFgAlpha()) * a;
            DrawWeaponIcon(wep, new Rect2(cellRect.Position, new Vector2(cellRect.Size.X, weaponH)), iconAlpha);
            if (pct < 0) return;
            string s = $"{pct}%";
            Color col = noColors ? new Color(1f, 1f, 1f, 1f) : AccuracyColor(pct);
            float padX = (cellRect.Size.X - MeasureText(s, _fs)) * 0.5f;
            DrawText(new Vector2(cellRect.Position.X + padX, textY), s,
                new Color(col.R, col.G, col.B, SbtFgAlpha() * a), _fs);
        });
        return endY;
    }

    private readonly List<(Weapon Weapon, int Pct)> _accCells = new();

    /// <summary>
    /// QC <c>WepSet_GetFromStat()</c> / <c>WepSet_GetFromStat_InMap()</c> (scoreboard.qc:1808-1809): the weapons
    /// the local player currently carries, plus every weapon that exists on this map. Fed by the match layer
    /// (<see cref="SetOwnedWeapons"/> / <see cref="SetWeaponsInMap"/>); with neither fed, every weapon counts as
    /// available so the grid degrades to "show them all" rather than to nothing.
    /// </summary>
    private bool OwnedOrInMap(Weapon w)
    {
        if (_ownedWeapons.Count == 0 && _weaponsInMap.Count == 0) return true;
        return _ownedWeapons.Contains(w.RegistryId) || _weaponsInMap.Contains(w.RegistryId);
    }

    private readonly HashSet<int> _ownedWeapons = new();
    private readonly HashSet<int> _weaponsInMap = new();

    /// <summary>QC <c>WepSet_GetFromStat()</c>: the local player's carried weapon set (registry ids).</summary>
    public void SetOwnedWeapons(IEnumerable<int>? ids)
    {
        _ownedWeapons.Clear();
        if (ids is not null) foreach (int id in ids) _ownedWeapons.Add(id);
    }

    /// <summary>QC <c>WepSet_GetFromStat_InMap()</c>: every weapon that spawns on the loaded map.</summary>
    public void SetWeaponsInMap(IEnumerable<int>? ids)
    {
        _weaponsInMap.Clear();
        if (ids is not null) foreach (int id in ids) _weaponsInMap.Add(id);
    }
    private float _accFade;
    private float _itemFade;

    /// <summary>
    /// QC <c>Scoreboard_ItemStats_Draw</c> (scoreboard.qc:1978): the same icon grid as the accuracy block, but
    /// over ITEMS the player picked up this match, with the pickup count under each icon. Fed from the
    /// networked per-player pickup tally (<see cref="SetItemStats"/>).
    /// </summary>
    private float DrawItemStats(float x, float w, float y, float fade)
    {
        if (CvarF("itemstats", 1f) == 0f) return y;   // QC hud_panel_scoreboard_itemstats
        if (MatchWarmup) return y;                    // QC Scoreboard_ItemStats_WouldDraw warmup gate
        if (GameScores.Gametype == "nb") return y;    // QC cl_nexball DrawScoreboardItemStats -> false
        if (_itemStats.Count == 0) return y;

        _itemCells.Clear();
        foreach (var kv in _itemStats)
            if (kv.Value > 0 && !IsItemFiltered(kv.Key)) _itemCells.Add((kv.Key, kv.Value));
        int n = _itemCells.Count;
        if (n == 0) return y;

        // QC scoreboard.qc:1997-2002: columns = max(6, ceil(n / rows)); item_height = hud_fontsize.y * 2.3
        // (NOTE: unlike the accuracy grid, the item grid does NOT divide by the weapons aspect).
        int rows = (CvarF("itemstats_doublerows", 0f) != 0f && n >= Mathf.FloorToInt(_itemStats.Count / 2f)) ? 2 : 1;
        int columns = Mathf.Max(6, Mathf.CeilToInt(n / (float)rows));
        float itemH = _fs * 2.3f;
        float cellH = itemH + _fs;

        if (y > Size2.Y - (cellH * rows + _rowH * 2f)) return y;

        float a = _itemFade;

        Rect2 c = BeginBlock(x, w, ref y, cellH * rows, fade, "Item stats", _blockRgb,
            a / Mathf.Max(0.0001f, fade), out float endY);
        if (c.Size.X <= 1f) return endY;

        DrawTableBackingTiled(c, _blockRgb, SbtBgAlpha(fade) * a / Mathf.Max(0.0001f, fade));
        DrawStatGridChrome(c, columns, rows, cellH, itemH, a);

        var white = new Color(1f, 1f, 1f, Cfg.FgAlpha * a);
        DrawStatGrid(c, columns, rows, cellH, itemH, a, _itemCells.Count, (i, cellRect, textY) =>
        {
            (string icon, int count) = _itemCells[i];
            DrawItemIcon(icon, new Rect2(cellRect.Position, new Vector2(cellRect.Size.X, itemH)), white);
            string s = count.ToString();
            float padX = (cellRect.Size.X - MeasureText(s, _fs)) * 0.5f;
            DrawText(new Vector2(cellRect.Position.X + padX, textY), s, white, _fs);
        });
        return endY;
    }

    private readonly List<(string Icon, int Count)> _itemCells = new();

    /// <summary>QC <c>is_item_filtered</c> (scoreboard.qc:1954): <c>..._itemstats_filter</c> +
    /// <c>..._itemstats_filter_mask</c> hide the small/medium/big health+armor tiers (ones digit) and all ammo
    /// (tens digit) from the item-stats grid. The mask is cumulative: 4 hides mega and everything below.</summary>
    private bool IsItemFiltered(string icon)
    {
        if (CvarF("itemstats_filter", 1f) == 0f) return false;
        int mask = (int)CvarF("itemstats_filter_mask", 0f);
        if (mask <= 0) return false;

        bool isHealthArmor = icon.StartsWith("health", System.StringComparison.Ordinal)
                          || icon.StartsWith("armor", System.StringComparison.Ordinal);
        if (isHealthArmor)
        {
            int tier = icon.EndsWith("mega", System.StringComparison.Ordinal) ? 4
                     : icon.EndsWith("big", System.StringComparison.Ordinal) ? 3
                     : icon.EndsWith("medium", System.StringComparison.Ordinal) ? 2
                     : icon.EndsWith("small", System.StringComparison.Ordinal) ? 1 : 0;
            return tier > 0 && tier <= (mask % 10);
        }
        if (icon.StartsWith("ammo", System.StringComparison.Ordinal))
            return ((mask / 10) % 10) == 1;
        return false;
    }

    /// <summary>
    /// QC the shared chrome of the two icon grids (scoreboard.qc:1875-1886 / 2036-2047): a black
    /// <c>sbt_highlight_alpha</c> band down every EVEN column, and an rgb band across the number strip of every
    /// row. This banding is what visually separates the cells — without it the icons float on nothing.
    /// </summary>
    private void DrawStatGridChrome(Rect2 c, int columns, int rows, float cellH, float iconH, float a)
    {
        if (!TableHighlight()) return;
        float cellW = c.Size.X / columns / rows;
        float hlA = TableHighlightAlpha() * a;
        for (int i = 0; i < columns; i++)
            if ((i % 2) == 0)
                DrawRect(new Rect2(c.Position.X + cellW * rows * i, c.Position.Y,
                    Mathf.Min(cellW * rows, c.Size.X - cellW * rows * i), cellH * rows),
                    new Color(0f, 0f, 0f, hlA));
        for (int i = 0; i < rows; i++)
            DrawRect(new Rect2(c.Position.X, c.Position.Y + iconH + cellH * i, c.Size.X, _fs),
                new Color(_blockRgb.R, _blockRgb.G, _blockRgb.B, hlA));
    }

    /// <summary>QC the shared cell walk of the two icon grids: left→right, wrapping to the second row after
    /// <c>columns</c> cells (with the half-cell indent Base applies when rows == 2).</summary>
    private void DrawStatGrid(Rect2 c, int columns, int rows, float cellH, float iconH, float a, int count,
        System.Action<int, Rect2, float> drawCell)
    {
        float cellW = c.Size.X / columns / rows;
        float x0 = c.Position.X + (rows == 2 ? cellW * 0.5f : 0f);
        float cx = x0, cy = c.Position.Y;
        int column = 0;
        for (int i = 0; i < count; i++)
        {
            drawCell(i, new Rect2(cx, cy, cellW, cellH), cy + iconH);
            cx += cellW * rows;
            if (rows == 2 && column == columns - 1) { cx = x0; cy += cellH; }
            ++column;
        }
    }

    /// <summary>Draw a weapon's HUD icon fitted into <paramref name="cell"/> (QC
    /// <c>drawpic_aspect_skin(tmpos, it.model2, …)</c>), reusing the same skin lookup the weapons panel uses.</summary>
    private void DrawWeaponIcon(Weapon wep, Rect2 cell, float alpha)
    {
        Texture2D? icon = WeaponHud.Icon(wep);
        var tint = new Color(1f, 1f, 1f, alpha);
        if (icon is null)
        {
            // Fallback: the weapon's colour swatch + its short name, so the grid still reads.
            DrawRect(cell, WeaponHud.ColorOf(wep, alpha * 0.35f));
            int fs = Mathf.Max(7, (int)(Mathf.Min(cell.Size.X, cell.Size.Y) * 0.4f));
            DrawTextCentered(new Vector2(cell.Position.X, cell.Position.Y + (cell.Size.Y - fs) * 0.5f),
                cell.Size.X, wep.NetName, new Color(1f, 1f, 1f, alpha), fs);
            return;
        }
        DrawTextureRect(icon, FitAspect(icon, cell), false, tint);
    }

    /// <summary>Draw an item's HUD icon (QC <c>it.m_icon</c>) fitted into <paramref name="cell"/>.</summary>
    private void DrawItemIcon(string bareName, Rect2 cell, Color tint)
    {
        Texture2D? icon;
        // A weapon pickup's icon key is "weapon<netname>" (WeaponPickup.Icon). The HUD ART name is NOT the
        // netname for the renamed weapons — mortar's art is weapongrenadelauncher, vortex's is weaponnex, and so
        // on — so route it through WeaponHud's mapping table rather than resolving the raw key, which silently
        // missed for every legacy-named gun.
        if (bareName.StartsWith("weapon", System.StringComparison.Ordinal) && bareName.Length > 6)
            icon = TextureCache.GetFirst(WeaponHud.IconPaths(bareName[6..]));
        else
            icon = TextureCache.GetFirst($"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");

        if (icon is null) { DrawRect(cell, new Color(tint.R, tint.G, tint.B, tint.A * 0.25f)); return; }
        DrawTextureRect(icon, FitAspect(icon, cell), false, tint);
    }

    /// <summary>QC <c>drawpic_aspect_skin</c>: letterbox the texture inside the cell, preserving its aspect.</summary>
    private static Rect2 FitAspect(Texture2D tex, Rect2 cell)
    {
        Vector2 ts = tex.GetSize();
        if (ts.X <= 0f || ts.Y <= 0f) return cell;
        float fit = Mathf.Min(cell.Size.X / ts.X, cell.Size.Y / ts.Y);
        var draw = new Vector2(ts.X * fit, ts.Y * fit);
        return new Rect2(cell.Position.X + (cell.Size.X - draw.X) * 0.5f,
                         cell.Position.Y + (cell.Size.Y - draw.Y) * 0.5f, draw.X, draw.Y);
    }

    /// <summary>QC accuracy color ramp (red→yellow→green by hit %).</summary>
    private static Color AccuracyColor(int pct)
    {
        float f = Mathf.Clamp(pct / 100f, 0f, 1f);
        return f < 0.5f
            ? new Color(1f, Mathf.Lerp(0f, 1f, f * 2f), 0f, 1f)
            : new Color(Mathf.Lerp(1f, 0f, (f - 0.5f) * 2f), 1f, 0f, 1f);
    }

    /// <summary>
    /// QC the race/CTS speed award (Scoreboard_MainPanel, scoreboard.qc:2731): in race/CTS, draw
    /// "Speed award: N unit (holder) / All-time fastest: N unit (holder)" above the rankings. Only drawn when the
    /// all-time best exists (QC <c>if (race_speedaward_alltimebest)</c>); the round-best half is dropped if 0.
    /// The qu/s values from the wire are converted to the configured <c>hud_speed_unit</c> (QC GetSpeedUnitFactor).
    /// </summary>
    private float DrawSpeedAward(float x, float w, float y, float fade)
    {
        if (GameScores.Gametype != "rc" && GameScores.Gametype != "cts") return y;
        if (_speedAwardBest == 0) return y; // QC: if (race_speedaward_alltimebest)
        if (y > Size2.Y - 20f) return y;

        int unit = (int)GlobalF("hud_speed_unit", 1f);
        float factor = SpeedUnitFactor(unit);
        string lbl = SpeedUnitLabel(unit);
        var body = new Color(1f, 1f, 1f, RowFgAlpha() * fade);

        string str = "";
        if (_speedAward != 0) // QC: if (race_speedaward)
        {
            string name = HudText.Strip(_speedAwardHolder);
            str = $"Speed award: {(int)(_speedAward * factor)}{lbl} ({name})";
            str += " / ";
        }
        string bestName = HudText.Strip(_speedAwardBestHolder);
        str += $"All-time fastest: {(int)(_speedAwardBest * factor)}{lbl} ({bestName})";
        DrawText(new Vector2(x, y), str, body, 14);
        return y + 20f;
    }

    /// <summary>QC <c>GetSpeedUnitFactor</c> (client/main.qc): qu/s -> the selected unit's factor.</summary>
    private static float SpeedUnitFactor(int unit) => unit switch
    {
        2 => 0.0254f,
        3 => 0.0254f * 3.6f,
        4 => 0.0254f * 3.6f * 0.6213711922f,
        5 => 0.0254f * 1.943844492f,
        _ => 1.0f,
    };

    /// <summary>QC <c>GetSpeedUnit</c> (client/main.qc): the selected unit's label.</summary>
    private static string SpeedUnitLabel(int unit) => unit switch
    {
        2 => "m/s",
        3 => "km/h",
        4 => "mph",
        5 => "knots",
        _ => "qu/s",
    };

    /// <summary>
    /// QC <c>Scoreboard_Rankings_Draw</c> (scoreboard.qc:2164): the framed race-record table — ordinal, time and
    /// holder in fixed sub-columns (3 / 5 / namesize font widths), laid out in as many COLUMNS as the block width
    /// allows, with the gold/silver/bronze ordinal colours and the self/alternating row highlights.
    /// </summary>
    private float DrawRankings(float x, float w, float y, float fade)
    {
        // QC Scoreboard_Rankings_Draw is race/CTS only; gate on the mode + data (the networked rankings table).
        if (_rankings.Count == 0) return y;
        if (GameScores.Gametype != "rc" && GameScores.Gametype != "cts") return y;

        // QC scoreboard.qc:2191-2213 — sub-column widths, then how many record columns fit across the block.
        float nameSize = 0f;
        foreach ((int _, string holder) in _rankings)
            nameSize = Mathf.Max(nameSize, MeasureText(HudText.Strip(holder), _fs));
        float nameCap = CvarF("namesize", 15f) * _fs;
        bool cut = nameSize > nameCap;
        if (cut) nameSize = nameCap;

        float rankSize = 3f * _fs;
        float timeSize = 5f * _fs;
        float colW = rankSize + timeSize + nameSize + _fs;
        int columns = Mathf.Max(1, Mathf.FloorToInt((w - 2f * _bgPad) / Mathf.Max(1f, colW)));
        columns = Mathf.Min(columns, _rankings.Count);
        int rows = Mathf.CeilToInt(_rankings.Count / (float)columns);

        if (y > Size2.Y - (rows * _rowH + _rowH * 2f)) return y;

        // Publish the grid shape so the Left/Right scroll can clamp itself (QC rankings_columns/rankings_rows).
        _rankingsColumns = columns;
        _rankingsRows = rows;
        int maxStart = Mathf.Max(0, Mathf.CeilToInt(_rankings.Count / (float)rows) - columns);
        if (RankingsStartColumn > maxStart) RankingsStartColumn = maxStart;

        float blockTop = y;
        Rect2 c = BeginBlock(x, w, ref y, rows * _rowH, fade, "Rankings:", _blockRgb, 1f, out float endY);
        if (c.Size.X <= 1f) return endY;
        DrawTableBackingTiled(c, _blockRgb, SbtBgAlpha(fade));

        // QC scoreboard.qc:2288-2292 — the focused-panel wash, sized to include the block's title row.
        float selHl = SelectedPanelHighlight(PanelRankings);
        if (selHl > 0f)
            DrawRect(new Rect2(x, blockTop, w, endY - blockTop),
                new Color(1f, 1f, 1f, selHl * Cfg.FgAlpha * fade));

        // QC hl_rgb = rgb + '0.5 0.5 0.5' — the row highlight is the block colour brightened.
        var hl = new Color(Mathf.Min(1f, _blockRgb.R + 0.5f), Mathf.Min(1f, _blockRgb.G + 0.5f),
                           Mathf.Min(1f, _blockRgb.B + 0.5f));
        var body = new Color(1f, 1f, 1f, SbtFgAlpha() * fade);
        float cellW = c.Size.X / columns;
        float textOfs = (_rowH - _fs) * 0.5f; // QC: center text vertically in the 1.25x row
        string selfName = HudText.Strip(RankingsSelfName);

        // QC scoreboard.qc:2246 — `start_item = rankings_start_column * rankings_rows`: the Left/Right arrows
        // scroll the record table by whole columns, so a long record list is browsable in place.
        int startItem = RankingsStartColumn * rows;
        int visible = Mathf.Min(_rankings.Count - startItem, rows * columns);
        for (int k = 0; k < visible; k++)
        {
            int i = startItem + k;
            int column = k / rows, j = k % rows;
            float rx = c.Position.X + column * cellW;
            float ry = c.Position.Y + j * _rowH;
            (int t, string holder) = _rankings[i];

            bool isSelf = selfName.Length != 0 && HudText.Strip(holder) == selfName;
            if (isSelf)
                DrawRect(new Rect2(rx, ry, colW, _rowH), new Color(hl.R, hl.G, hl.B, SelfHighlightAlpha() * fade));
            else if (TableHighlight() && ((j + column) & 1) == 0)
                DrawRect(new Rect2(rx, ry, colW, _rowH), new Color(hl.R, hl.G, hl.B, TableHighlightAlpha() * fade));

            // QC scoreboard.qc:2256-2262: gold / silver / bronze for the top 3, white otherwise.
            Color rankColor = i switch
            {
                0 => new Color(0.933f, 0.733f, 0.200f, body.A),
                1 => new Color(0.667f, 0.667f, 0.667f, body.A),
                2 => new Color(0.800f, 0.467f, 0.267f, body.A),
                _ => body,
            };
            float tx = rx + _fs * 0.5f, ty = ry + textOfs;
            DrawText(new Vector2(tx, ty), Ordinal(i + 1), rankColor, _fs);
            DrawText(new Vector2(tx + rankSize, ty), GameScores.TimeEncodedToString(t, compact: false), body, _fs);
            DrawColored(new Vector2(tx + rankSize + timeSize, ty), holder, body, _fs);
        }
        return endY;
    }

    /// <summary>QC <c>count_ordinal(n)</c>: 1st / 2nd / 3rd / 4th … (with the 11th-13th exception).</summary>
    private static string Ordinal(int n)
    {
        int mod100 = n % 100;
        if (mod100 is >= 11 and <= 13) return $"{n}th";
        return (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };
    }

    /// <summary>QC <c>strdecolorize(entcs_GetName(player_localnum))</c>: the local player's name, used to highlight
    /// the player's own row in the rankings block. Fed by the match layer; "" disables the self-highlight.</summary>
    public string RankingsSelfName { get; set; } = "";

    /// <summary>Draw a possibly color-coded string left-to-right starting at <paramref name="pos"/>.</summary>
    private void DrawColored(Vector2 pos, string text, Color baseColor, int size)
    {
        float cx = pos.X;
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, pos.Y), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    /// <summary>Draw a color-coded string ending at <paramref name="rightX"/> (right-aligned colored text).</summary>
    private void DrawColoredRight(float rightX, float topY, float width, string text, Color baseColor, int size)
    {
        float total = 0f;
        foreach (HudText.Run run in HudText.Parse(text, baseColor)) total += MeasureText(run.Text, size);
        float cx = rightX - total;
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, topY), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    /// <summary>
    /// Draw a color-coded string horizontally centered within <paramref name="width"/> in the BOLD HUD font —
    /// QC's <c>draw_beginBoldFont()</c> / <c>draw_endBoldFont()</c> pair, which the scoreboard wraps around the
    /// gametype banner (and Base's other headline strings). Falls back to the regular font if the bold face
    /// isn't loaded.
    /// </summary>
    private void DrawTextCentered2Bold(Vector2 pos, float width, string text, Color baseColor, int size)
    {
        Font face = HudSkin.BoldFont ?? Font;
        var runs = new List<HudText.Run>(HudText.Parse(text, baseColor));
        float total = 0f;
        foreach (HudText.Run run in runs)
            total += face.GetStringSize(run.Text, HorizontalAlignment.Left, -1f, size).X;

        float cx = pos.X + (width - total) * 0.5f;
        float baseline = pos.Y + face.GetAscent(size);
        foreach (HudText.Run run in runs)
        {
            var shadow = new Color(0f, 0f, 0f, run.Color.A * 0.6f);
            DrawString(face, new Vector2(cx + 1f, baseline + 1f), run.Text, HorizontalAlignment.Left, -1f, size, shadow);
            DrawString(face, new Vector2(cx, baseline), run.Text, HorizontalAlignment.Left, -1f, size, run.Color);
            cx += face.GetStringSize(run.Text, HorizontalAlignment.Left, -1f, size).X;
        }
    }

    /// <summary>
    /// Draw a column title right-aligned at <paramref name="rightX"/>, horizontally SQUEEZED by
    /// <paramref name="condense"/> — QC's <c>drawfontscale.x *= sbt_field_title_condense_factor[i]</c> around the
    /// header <c>drawstring</c> (scoreboard.qc:1336-1343 / 1365-1374). Godot has no per-axis font scale, so the
    /// equivalent is a canvas transform anchored at the text's left edge for the duration of the draw.
    /// <paramref name="condense"/> &gt;= 1 draws normally (no transform, no cost).
    /// </summary>
    private void DrawTextRightCondensed(float rightX, float topY, string text, Color color, int size, float condense)
    {
        if (string.IsNullOrEmpty(text)) return;
        float full = MeasureText(text, size);
        if (condense >= 0.999f)
        {
            DrawText(new Vector2(rightX - full, topY), text, color, size);
            return;
        }
        // Anchor the squeeze at the title's LEFT edge so it still ends flush with the column's right edge.
        float left = rightX - full * condense;
        DrawSetTransform(new Vector2(left, 0f), 0f, new Vector2(condense, 1f));
        DrawText(new Vector2(0f, topY), text, color, size);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// <summary>QC <c>draw_beginBoldFont()</c> … <c>draw_endBoldFont()</c> around a left-aligned plain string
    /// (the spectators header, the team scores). Falls back to the regular face if the bold one is missing.</summary>
    private void DrawTextBold(Vector2 pos, string text, Color color, int size)
    {
        if (string.IsNullOrEmpty(text)) return;
        Font face = HudSkin.BoldFont ?? Font;
        float baseline = pos.Y + face.GetAscent(size);
        DrawString(face, new Vector2(pos.X + 1f, baseline + 1f), text, HorizontalAlignment.Left, -1f, size,
            new Color(0f, 0f, 0f, color.A * 0.6f));
        DrawString(face, new Vector2(pos.X, baseline), text, HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>Bold text ending at <paramref name="rightX"/> (QC's team score is placed by subtracting its own
    /// measured width from the anchor).</summary>
    private void DrawTextRightBold(float rightX, float topY, string text, Color color, int size)
        => DrawTextBold(new Vector2(rightX - MeasureBold(text, size), topY), text, color, size);

    /// <summary>Width of <paramref name="text"/> in the bold HUD face (QC stringwidth under draw_beginBoldFont).</summary>
    private static float MeasureBold(string text, int size)
    {
        Font face = HudSkin.BoldFont ?? Font;
        return string.IsNullOrEmpty(text) ? 0f
            : face.GetStringSize(text, HorizontalAlignment.Left, -1f, size).X;
    }

    /// <summary>Draw a color-coded string horizontally centered within <paramref name="width"/>.</summary>
    private void DrawTextCentered2(Vector2 pos, float width, string text, Color baseColor, int size)
    {
        float total = 0f;
        foreach (HudText.Run run in HudText.Parse(text, baseColor)) total += MeasureText(run.Text, size);
        float cx = pos.X + (width - total) * 0.5f;
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, pos.Y), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    private static Color TeamColor(int team, float alpha) => team switch
    {
        Teams.Red    => new Color(1f, 0.35f, 0.35f, alpha),
        Teams.Blue   => new Color(0.4f, 0.55f, 1f, alpha),
        Teams.Yellow => new Color(1f, 0.95f, 0.35f, alpha),
        Teams.Pink   => new Color(1f, 0.45f, 0.85f, alpha),
        _ => new Color(0.8f, 0.8f, 0.8f, alpha),
    };

    // ---- column geometry ----

    /// <summary>Computed pixel geometry for the current column list (QC the sbt_field_size[] widths).</summary>
    private sealed class Layout
    {
        public float X, W, RankX, NumW;
        /// <summary>Y of the table's first (header) row — so a header column band can run the full table height.</summary>
        public float TableTop;
        public float[] ColX = System.Array.Empty<float>();      // left edge of each column (used by Name)
        public float[] ColRight = System.Array.Empty<float>();  // right edge (numeric columns are right-aligned)
        public float[] ColW = System.Array.Empty<float>();      // QC sbt_field_size[i] — the content-measured width
        public float[] ColTitleCondense = System.Array.Empty<float>(); // QC sbt_field_title_condense_factor[i]
    }

    /// <summary>
    /// Lay the columns out left→right: a rank gutter, then the Name column takes the slack while every other
    /// column gets a fixed numeric width (QC's column sizing is content-measured; we approximate with a uniform
    /// numeric width + an elastic name column, which is enough for a readable grid).
    /// </summary>
    private Layout ComputeLayout(float x, float w)
    {
        var l = new Layout { X = x, W = w, RankX = x };
        int n = _columns.Count;
        l.ColX = new float[n];
        l.ColRight = new float[n];
        l.ColW = new float[n];
        l.ColTitleCondense = new float[n];
        for (int i = 0; i < n; i++) l.ColTitleCondense[i] = 1f;

        int fs = Mathf.Max(6, Cfg.FontSize);
        float rankW = 2.5f * fs;                 // the rank gutter, in font widths like every other metric
        l.NumW = 0f;

        // QC sbt_field_size[]: each non-name column is content-measured (title vs widest value); the NAME column
        // then takes "all remaining space" (Scoreboard_FixColumnWidth:1238-1245).
        float numericTotal = 0f;
        for (int i = 0; i < n; i++)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Name || c.Kind == ColumnKind.Separator) continue;
            l.ColW[i] = MeasureNumericColumn(c, fs, Size2.X, out float condense);
            l.ColTitleCondense[i] = condense;
            numericTotal += l.ColW[i] + fs;
            l.NumW = Mathf.Max(l.NumW, l.ColW[i]);
        }

        float nameX = x + rankW;
        float nameW = Mathf.Max(4f * fs, w - rankW - numericTotal);

        // Numerics pack against the right edge, right-to-left, so the LAST column sits at the far right.
        float cursorRight = x + w;
        for (int i = n - 1; i >= 0; i--)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Name || c.Kind == ColumnKind.Separator) continue;
            l.ColRight[i] = cursorRight;
            l.ColX[i] = cursorRight - l.ColW[i];
            cursorRight -= l.ColW[i] + fs;
        }
        // Name column gets the gutter→first-numeric span.
        for (int i = 0; i < n; i++)
        {
            if (_columns[i].Kind == ColumnKind.Name)
            {
                l.ColX[i] = nameX;
                l.ColRight[i] = nameX + nameW;
                l.ColW[i] = nameW;
            }
        }
        return l;
    }

    // =====================================================================================
    //  Behavior-cvar defaults (QC autocvar_hud_panel_scoreboard_*; HudConfig invokes this by reflection)
    // =====================================================================================

    /// <summary>Register the scoreboard's behavior-cvar defaults into the shared store (QC the
    /// <c>autocvar_hud_panel_scoreboard_*</c> initializers, scoreboard.qc:66-105). Idempotent — keeps any
    /// cfg/user value. Read live by the draw code so console/menu edits take effect immediately.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        // QC autocvar_scoreboard_columns (scoreboard.qh:7) — the user's saved column set; "" = the built-in
        // default, "default" / "all" are the two presets the Ctrl+C cycle in the interactive UI walks through.
        c.Register("scoreboard_columns", "", CvarFlags.Save);
        // QC autocvar__scoreboard_team_selection (scoreboard.qh:41): the server raises this to ask the client to
        // open the interactive team picker; the client clears it on open. Not saved (a transient request).
        c.Register("_scoreboard_team_selection", "0", CvarFlags.None);
        // fade in/out (scoreboard.qc:66-67)
        c.Register("hud_panel_scoreboard_fadeinspeed", "10", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_fadeoutspeed", "5", CvarFlags.Save);
        // respawn timer decimals (scoreboard.qc:68)
        c.Register("hud_panel_scoreboard_respawntime_decimals", "1", CvarFlags.Save);
        // table look (scoreboard.qc:69-77)
        c.Register("hud_panel_scoreboard_table_bg_alpha", "0", CvarFlags.Save);
        // QC scoreboard.qc:70 + the Scoreboard_Draw_Export skin-cvar list (scoreboard.qc:29): the table bg
        // texture scale (border_default 9-slice). Registered so it round-trips through the HUD-skin export.
        c.Register("hud_panel_scoreboard_table_bg_scale", "0.25", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_fg_alpha", "0.9", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_fg_alpha_self", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight_alpha", "0.2", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight_alpha_self", "0.4", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight_alpha_eliminated", "0.6", CvarFlags.Save);
        // team bg tint (scoreboard.qc:78)
        c.Register("hud_panel_scoreboard_bg_teams_color_team", "0", CvarFlags.Save);
        // accuracy block (scoreboard.qc:82-84)
        c.Register("hud_panel_scoreboard_accuracy", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_accuracy_doublerows", "0", CvarFlags.Save);
        // QC scoreboard.qc:84 + the Scoreboard_Draw_Export skin-cvar list (scoreboard.qc:38): draw accuracy cells
        // without the per-weapon color ramp. Registered so it round-trips through the HUD-skin export.
        c.Register("hud_panel_scoreboard_accuracy_nocolors", "0", CvarFlags.Save);
        // item-stats block (scoreboard.qc:88-89)
        c.Register("hud_panel_scoreboard_itemstats", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_itemstats_doublerows", "0", CvarFlags.Save);
        // spectator list (scoreboard.qc:80,99)
        c.Register("hud_panel_scoreboard_spectators_position", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_spectators_showping", "1", CvarFlags.Save);
        // per-round score averaging (scoreboard.qc:105)
        c.Register("hud_panel_scoreboard_scores_per_round", "0", CvarFlags.Save);
        // playerid name prefix (scoreboard.qc:91-93): show "#<entnum> " before each name when enabled.
        c.Register("hud_panel_scoreboard_playerid", "0", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_playerid_prefix", "#", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_playerid_suffix", " ", CvarFlags.Save);
        // accuracy show-delay (scoreboard.qc:83) — registered for the HUD-skin round-trip (the warmup gate is wired;
        // the time-since-start show-delay is a documented residual).
        c.Register("hud_panel_scoreboard_accuracy_showdelay", "2", CvarFlags.Save);
        // ping color bands (scoreboard.qc:1017-1019)
        c.Register("hud_panel_scoreboard_ping_low", "20", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_ping_medium", "80", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_ping_high", "200", CvarFlags.Save);
        // team-size side display position (scoreboard.qc:79)
        c.Register("hud_panel_scoreboard_team_size_position", "0", CvarFlags.Save);
    }
}
