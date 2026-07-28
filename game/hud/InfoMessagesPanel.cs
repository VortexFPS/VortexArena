using System.Collections.Generic;
using System.Text;
using Godot;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;     // CvarFlags
using XonoticGodot.Engine.Simulation;   // CvarService (RegisterDefaults)

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Status / info messages — port of Base/.../qcsrc/client/hud/panel/infomessages.qc (HUD panel #14). The
/// QC version drew transient state hints in the top-left: "Observing" / "Spectating: name", "Press X to
/// join", warmup notices, the "Game starts in N seconds" countdown, team-unbalanced nags, and the
/// spectator list.
///
/// This port renders that full set from a mix of local + networked state. The dead/respawn line is computed
/// from the local <see cref="Player"/> (<see cref="Player.IsDead"/> + <see cref="Player.RespawnTime"/>).
/// The match/spectator state is networked, so it is fed via settable members the net layer drives:
/// <see cref="IsSpectating"/>/<see cref="SpectatingName"/>, <see cref="WarmupStage"/>,
/// <see cref="CountdownSeconds"/>, the team-balance nag (<see cref="TeamBalanceWarning"/>), the spectator
/// list (<see cref="SetSpectators"/>), and the join/keybind hint (<see cref="JoinHint"/>). Names render with
/// their <c>^N</c> color codes.
///
/// Faithful additions (this pass): right-align / wrap via <c>hud_panel_infomessages_flip</c> (luma default 1),
/// the QC <c>InfoMessages_drawstring</c> word-wrap that flips per line; blinking nag color
/// (<c>time%1 &gt;= 0.5 ? ^1 : ^3</c>) for warmup ready-up / missing-team / team-balance prompts; the bold
/// (1.125×, <see cref="HudSkin.BoldFont"/>) "Teams are unbalanced!" line; the "Press jump to join/spawn",
/// observer/chase-cam hint cycle, ready-up, players-needed, missing-teams, queued-to-join, and "press team
/// selection to adjust" lines. All hint text is color-coded with <c>^N</c>.
/// </summary>
public partial class InfoMessagesPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>); drives the dead/respawn line.</summary>
    public Player? Player { get; set; }

    /// <summary>True while observing/spectating (QC spectatee_status). Shows the observing hint.</summary>
    public bool IsSpectating { get; set; }

    /// <summary>
    /// QC <c>spectatee_status</c> proper: 0 = playing, -1 = free-fly observer, &gt;0 = chasing a player (the
    /// spectated entity number). Set by the net layer; the legacy <see cref="IsSpectating"/> bool stays in sync
    /// (non-zero ⇒ spectating). When this is left at its default (0) but <see cref="IsSpectating"/> is true, the
    /// panel infers a generic spectating state so the older feeders keep working.
    /// </summary>
    public int SpectateeStatus { get; set; }

    /// <summary>Name of the player being spectated, if any (QC entcs_GetName(current_player)). May carry ^N.</summary>
    public string SpectatingName { get; set; } = "";

    /// <summary>True during the warmup stage (QC warmup_stage). Shows the warmup notice.</summary>
    public bool WarmupStage { get; set; }

    /// <summary>
    /// Seconds until the match starts; &gt; 0 shows the "Game starts in N" countdown (QC GAMESTARTTIME -
    /// time). Set to 0 once the match is live. The owner updates this from the match clock.
    /// </summary>
    public float CountdownSeconds { get; set; }

    /// <summary>
    /// Team-balance nag text (QC the "teams are unbalanced" / "press to switch teams" line), or empty for
    /// none. The team logic on the net side fills this when teams need rebalancing. When set, it overrides the
    /// computed unbalanced line and renders BOLD + blinking, matching QC's <c>draw_beginBoldFont</c> path.
    /// </summary>
    public string TeamBalanceWarning { get; set; } = "";

    /// <summary>
    /// The join / key-bind hint shown while spectating or dead (QC the keybind-substituted "press jump to
    /// join" line). Settable so the keybind layer can substitute the real bound key. Empty falls back to a
    /// generic "Press Fire to join/respawn".
    /// </summary>
    public string JoinHint { get; set; } = "";

    /// <summary>
    /// Suppresses the "Press jump to join" line. Set in the editor gametype, where jump means FLY UP and
    /// joining is a deliberate PLAYTEST toggle — so the stock hint tells the mapper to press a key that does
    /// something else entirely, and they reasonably conclude the editor is broken.
    /// </summary>
    public bool SuppressJoinHint { get; set; }

    /// <summary>
    /// The current sim/match time, used to compute the respawn countdown against
    /// <see cref="Player.RespawnTime"/> and to drive the blinking nag color (QC <c>time % 1</c>). If left
    /// &lt; 0 the respawn line shows without a countdown and the blink falls back to wall-clock time.
    /// </summary>
    public double Now { get; set; } = -1.0;

    /// <summary>
    /// QC <c>STAT(RESPAWN_TIME)</c> as networked to the owner (ClientNet.RespawnTimeStat): 0 = alive; otherwise
    /// the absolute respawn time, NEGATED while a respawn is imminent. Drives the dead/respawn line WITHOUT a
    /// local <see cref="Player"/> actor — so the countdown shows on a pure remote client too. Paired with
    /// <see cref="NetServerTime"/> for the remaining seconds.
    /// </summary>
    public float RespawnStat { get; set; }

    /// <summary>The latest networked server time (ClientNet.LatestServerTime), to count <see cref="RespawnStat"/> down against.</summary>
    public float NetServerTime { get; set; }

    // ---- networked match/team state (fed by the net/match layer; all default to "no nag") --------------

    /// <summary>True for team gametypes (QC <c>teamplay</c>). Gates the team-balance + queued-to-join lines.</summary>
    public bool TeamPlay { get; set; }

    /// <summary>
    /// QC <c>entcs_GetWantsJoin(current_player)</c>: &gt;0 = queued to join team N (1-based team index), &lt;0 =
    /// queued for any team, 0 = not queued. Shown while spectating in teamplay.
    /// </summary>
    public int WantsJoinTeam { get; set; }

    /// <summary>
    /// QC <c>srv_minplayers - numplayers</c> resolved by the net layer: how many more players are needed for the
    /// match to start (only meaningful in warmup with no warmup time limit). 0 hides the line.
    /// </summary>
    public int PlayersNeeded { get; set; }

    /// <summary>
    /// QC <c>STAT(MISSING_TEAMS_MASK)</c>: in teamplay a 4-bit mask of teams lacking active players; in FFA the
    /// number of players still needed. 0 = nobody missing.
    /// </summary>
    public int MissingTeamsMask { get; set; }

    /// <summary>
    /// QC <c>ready_waiting</c> — a ready-up vote is in progress (warmup). Shows the "press ready to end warmup"
    /// prompt; combined with <see cref="ReadyWaitingForMe"/> to nag the local player specifically.
    /// </summary>
    public bool ReadyWaiting { get; set; }

    /// <summary>QC <c>ready_waiting_for_me</c> — the local player still has to ready up. Blinks the ready prompt.</summary>
    public bool ReadyWaitingForMe { get; set; }

    /// <summary>
    /// QC <c>(ts_max - ts_min) &gt;= teamnagger</c> resolved by the net layer: teams are unbalanced enough to nag.
    /// Renders the bold blinking "Teams are unbalanced!" line (only in teamplay). Ignored if
    /// <see cref="TeamBalanceWarning"/> is set (that wins, verbatim).
    /// </summary>
    public bool TeamsUnbalanced { get; set; }

    /// <summary>
    /// QC: the local player is on the larger team (<c>tm.team_size == ts_max</c>), so they can fix the imbalance —
    /// the unbalanced line then appends "Press [team selection] to adjust". The keybind layer may override the
    /// substituted key via <see cref="TeamSelectHint"/>.
    /// </summary>
    public bool CanAdjustTeams { get; set; }

    /// <summary>The substituted "Press X to adjust" key for the team-selection nag (QC getcommandkey "team selection").</summary>
    public string TeamSelectHint { get; set; } = "team selection";

    /// <summary>The substituted "ready" key for the end-warmup prompt (QC getcommandkey "ready").</summary>
    public string ReadyHint { get; set; } = "ready";

    /// <summary>
    /// QC <c>observe_blocked</c>: the server forbids switching from chase-cam to free-fly observe
    /// (<c>sv_spectate</c> off / observe blocked). When true the chasing-player hint (group0 case 1) drops the
    /// "Press secondary fire to observe" half and only offers the drop-weapon camera-mode toggle, matching the
    /// QC. Fed by the net layer; default false. Only affects the chasing (spectatee_status &gt; 0) hint.
    /// </summary>
    public bool ObserveBlocked { get; set; }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(cl_lms, DrawInfoMessages): the local LMS player is eliminated (has an LMS rank,
    /// <c>scores(ps_primary) &gt; 0</c>), so the "^1You have no more lives left" line is shown on this panel. Fed
    /// each frame by the net layer from the local scoreboard row's LMS_RANK column. False for every other gametype.
    /// </summary>
    public bool LmsNoLives { get; set; }

    /// <summary>
    /// True while the HUD editor is open (QC <c>autocvar__hud_configure</c>). When set the panel draws the QC
    /// editor help text instead of live state, so the panel is visible/positionable in the editor. Default false.
    /// </summary>
    public bool Configuring { get; set; }

    private readonly List<string> _extra = new();
    private readonly List<string> _spectators = new();

    /// <summary>Hard cap on owner-pushed extra lines so a feeder that forgets to <see cref="ClearLines"/> can't
    /// grow the list (and the every-frame draw loop) without bound.</summary>
    private const int MaxExtraLines = 32;

    /// <summary>Push an extra info line shown below the computed ones (QC InfoMessage). Replaces nothing.</summary>
    public void AddLine(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Defensive: drop the oldest if a feeder pushes without clearing — bounds the draw-path loop.
            if (_extra.Count >= MaxExtraLines) _extra.RemoveAt(0);
            _extra.Add(text);
        }
        QueueRedraw();
    }

    /// <summary>Clear the owner-pushed extra lines.</summary>
    public void ClearLines()
    {
        _extra.Clear();
        QueueRedraw();
    }

    /// <summary>
    /// Set the spectator list (QC the "Spectators: a, b, c" footer), by display name (names may carry ^N).
    /// Pass null/empty to hide it.
    /// </summary>
    public void SetSpectators(IEnumerable<string>? names)
    {
        _spectators.Clear();
        if (names is not null)
            foreach (string n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                _spectators.Add(n);
                // QC clamps the rendered list to MAX_SPECTATORS; bound the draw-path loop the same way.
                if (_spectators.Count >= MaxSpectators) break;
            }
        QueueRedraw();
    }

    /// <summary>QC <c>MAX_SPECTATORS</c> — the spectator list is rendered (and stored) capped to this many names.</summary>
    private const int MaxSpectators = 16;

    // -------------------------------------------------------------------------------------------------
    //  Behaviour cvars (QC HUD_InfoMessages_Export — saved into hud skin files)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Register this panel's behaviour-cvar defaults (invoked by reflection from <see cref="HudConfig"/>).
    /// QC's export saved only <c>hud_panel_infomessages_flip</c> (luma default <c>1</c> = right-align + wrap).
    /// The QC hint-cycle tunables (<c>group0</c>, <c>group_time</c>, <c>group_fadetime</c>) are exposed too so a
    /// skin can disable the rotating spectator hints or retune the cadence.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        if (c is null) return;
        const CvarFlags save = CvarFlags.Save;
        // luma: infomessages flips to the right edge and right-aligns its wrapped text.
        c.Register("hud_panel_infomessages_flip", "1", save);
        // QC img-group hint cycle (the rotating "press X to spectate / observe / gametype info" lines).
        c.Register("hud_panel_infomessages_group0", "1", save);
        c.Register("hud_panel_infomessages_group_time", "6", save);
        c.Register("hud_panel_infomessages_group_fadetime", "0.4", save);
    }

    // -------------------------------------------------------------------------------------------------
    //  Hint-cycle state (QC img_select / img_cur_msg / img_time / img_fade — one group, IMG_COUNT == 1)
    // -------------------------------------------------------------------------------------------------

    private int _imgCurMsg;       // QC img_cur_msg[0]
    private float _imgTime;       // QC img_time[0] — next switch time
    private float _imgFade = 1f;  // QC img_fade[0] — current group alpha

    /// <summary>
    /// Port of QC <c>img_select</c>: advance the spectator-hint cycle by frame time, fading the current message
    /// out then stepping to the next every <c>group_time</c> seconds. Returns the current message index. Uses
    /// <see cref="Now"/> (sim time) when available, else the engine wall clock, so the cadence runs either way.
    /// </summary>
    private int ImgSelect(float now, float frameTime)
    {
        // Defensive: a NaN frameTime (bad clock feed) would poison _imgFade and freeze the cycle forever.
        if (float.IsNaN(frameTime) || frameTime < 0f) frameTime = 0f;
        float fadetime = Mathf.Max(0.001f, CvarF("group_fadetime", 0.4f));
        if (now > _imgTime)
        {
            _imgFade = Mathf.Max(0f, _imgFade - frameTime / fadetime);
            if (_imgFade <= 0f)
            {
                // Keep modular so it never overflows over a very long session (caller takes % 3).
                _imgCurMsg = (_imgCurMsg + 1) % 3;
                _imgTime = Mathf.Floor(now) + CvarF("group_time", 6f);
            }
        }
        else
        {
            _imgFade = Mathf.Min(1f, _imgFade + frameTime / fadetime);
        }
        return _imgCurMsg;
    }

    protected override void DrawPanel()
    {
        // "Press primary fire to spectate" is advice for a match. A mapper flying their own level is not
        // waiting to join anything, and the prompt sits exactly where the editor readout belongs.
        if (EditorSession) return;

        // QC __hud_configure branch: in the HUD editor show the static help text so the panel is visible.
        if (Configuring)
        {
            DrawConfigureHelp();
            return;
        }

        float now = ResolveNow();
        // QC blinkcolor = (time % 1 >= 0.5) ? "^1" : "^3"  — alternating red/yellow nag prefix.
        string blink = (now - Mathf.Floor(now)) >= 0.5f ? "^1" : "^3";

        // Gather the (possibly color-coded) lines to draw this frame, paired with a "bold" flag.
        var lines = new List<Line>();

        bool spectating = IsSpectating || SpectateeStatus != 0;
        int specStatus = SpectateeStatus != 0 ? SpectateeStatus : (IsSpectating ? -1 : 0);

        if (spectating)
        {
            // QC: ^1Observing  /  ^1Spectating: ^7%s
            if (specStatus == -1 && string.IsNullOrEmpty(SpectatingName))
                lines.Add(Line.Of("^1Observing"));
            else
                lines.Add(Line.Of(string.IsNullOrEmpty(SpectatingName)
                    ? "^1Observing"
                    : $"^1Spectating: ^7{SpectatingName}"));

            // QC group0: a rotating hint (3 variants) depending on observer vs chasing.
            if (CvarBool("group0"))
            {
                int sel = ((ImgSelect(now, ResolveFrameTime()) % 3) + 3) % 3;
                lines.Add(Line.Of(SpectateHint(specStatus, sel, ObserveBlocked), _imgFade));
            }

            // QC: if not already queued, "^1Press ^3jump^1 to join".
            if (WantsJoinTeam == 0 && !SuppressJoinHint)
                lines.Add(Line.Of(BuildJoinHint()));
        }

        // QC wants_join queued-to-join lines (teamplay only in QC; harmless otherwise).
        if (WantsJoinTeam > 0)
            lines.Add(Line.Of(BuildQueuedTeamLine(WantsJoinTeam)));
        else if (WantsJoinTeam < 0)
            lines.Add(Line.Of("^2You're queued to join any available team"));

        // Networked respawn timer (QC STAT(RESPAWN_TIME)) — works even with no local Player actor (pure client).
        if (!spectating)
        {
            if (RespawnStat != 0f)
            {
                float remaining = Mathf.Abs(RespawnStat) - NetServerTime;
                if (remaining > 0.05f)
                    lines.Add(Line.Of($"^1Respawning in ^3{CeilSeconds(remaining)}^1..."));
                else
                    lines.Add(Line.Of(BuildRespawnHint()));
            }
            else if (Player is not null && Player.IsDead)
            {
                if (Now >= 0.0 && Player.RespawnTime > 0f)
                {
                    float secs = Mathf.Max(0f, (float)(Player.RespawnTime - Now));
                    lines.Add(Line.Of($"^1Respawning in ^3{CeilSeconds(secs)}^1..."));
                }
                else
                {
                    lines.Add(Line.Of(BuildRespawnHint()));
                }
            }
        }

        // QC MUTATOR_HOOKFUNCTION(cl_lms, DrawInfoMessages): a locally-eliminated LMS player (has an LMS rank) is
        // told they're out for the match.
        if (LmsNoLives)
            lines.Add(Line.Of("^1You have no more lives left, you have to wait until the next game"));

        // QC: if (time < GAMESTARTTIME) "^1Game starts in ^3%d^1 seconds".
        if (CountdownSeconds > 0f)
            lines.Add(Line.Of($"^1Game starts in ^3{CeilSeconds(CountdownSeconds)}^1 seconds"));

        if (WarmupStage)
        {
            lines.Add(Line.Of("^2Currently in ^1warmup^2 stage!"));

            // QC: players-needed nag wins; else (not spectating) the ready-up prompt.
            if (PlayersNeeded > 0)
            {
                lines.Add(Line.Of(PlayersNeeded == 1
                    ? "^31^2 more player is needed for the match to start."
                    : $"^3{PlayersNeeded}^2 more players are needed for the match to start."));
            }
            else if (TeamPlay && TeamsUnbalanced)
            {
                // QC: ready won't end warmup while teams are unbalanced; the bold nag is drawn below.
            }
            else if (!spectating)
            {
                if (ReadyWaiting)
                {
                    if (ReadyWaitingForMe)
                        lines.Add(Line.Of($"{blink}Press ^3{ReadyHint}{blink} to end warmup"));
                    else
                        lines.Add(Line.Of("^2Waiting for others to ready up to end warmup..."));
                }
                else
                {
                    lines.Add(Line.Of($"^2Press ^3{ReadyHint}^2 to end warmup"));
                }
            }
        }

        // QC: MISSING_TEAMS_MASK nag (blinking).
        if (MissingTeamsMask != 0)
        {
            if (TeamPlay)
            {
                lines.Add(Line.Of($"{blink}Need active players for: {TeamMaskList(MissingTeamsMask, blink)}"));
            }
            else
            {
                lines.Add(Line.Of(MissingTeamsMask == 1
                    ? $"{blink}Waiting for 1 player to join..."
                    : $"{blink}Waiting for {MissingTeamsMask} players to join..."));
            }
        }

        // QC: teamplay && numplayers>1 && teamnagger && unbalanced → BOLD 1.125× blinking line.
        if (!string.IsNullOrEmpty(TeamBalanceWarning))
        {
            lines.Add(Line.Bold(TeamBalanceWarning));
        }
        else if (TeamPlay && TeamsUnbalanced)
        {
            string s = blink + "Teams are unbalanced!";
            if (CanAdjustTeams)
                s += $" Press ^3{TeamSelectHint}{blink} to adjust";
            lines.Add(Line.Bold(s));
        }

        // Owner-pushed extra lines (QC mutator DrawInfoMessages additions).
        foreach (string e in _extra) lines.Add(Line.Of(e));

        // QC cl_showspectators: "Spectating this player:" / "Spectating you:" then each spectator name.
        if (_spectators.Count > 0)
        {
            lines.Add(Line.Of(spectating ? "^1Spectating this player:" : "^1Spectating you:"));
            foreach (string n in _spectators) lines.Add(Line.Of($"^7{n}"));
        }

        if (lines.Count == 0) return; // self-blank: nothing to show

        DrawBackground();

        bool flip = CvarBool("flip"); // luma default 1 = right-align + flip wrap

        // QC: pos/mySize shrink by panel_bg_padding before drawing.
        float pad = Cfg.Padding;
        var inner = new Rect2(pad, pad, Mathf.Max(8f, Size2.X - pad * 2f), Mathf.Max(8f, Size2.Y - pad * 2f));

        // QC fontsize = 0.2 * mySize.y (the panel sizes its text to ~5 lines tall). Clamp to a sane minimum.
        int fontsize = Mathf.Max(8, Mathf.RoundToInt(inner.Size.Y * 0.2f));

        float y = inner.Position.Y;
        foreach (Line line in lines)
        {
            // QC: the unbalanced line bumps fontsize *= 1.125 and switches to the bold font.
            int sz = line.IsBold ? Mathf.RoundToInt(fontsize * 1.125f) : fontsize;
            Font font = line.IsBold ? (HudSkin.BoldFont ?? Font) : Font;
            Color baseCol = new(1f, 1f, 1f, LiveFgAlpha * line.Alpha);

            y = DrawWrappedLine(inner, y, line.Text, baseCol, sz, font, flip);
            if (y > inner.Position.Y + inner.Size.Y) break; // clip to panel
        }
    }

    /// <summary>One info line: its (color-coded) text, a fade alpha (hint cycle), and whether it draws bold.</summary>
    private readonly struct Line
    {
        public readonly string Text;
        public readonly float Alpha;
        public readonly bool IsBold;
        private Line(string text, float alpha, bool bold) { Text = text; Alpha = alpha; IsBold = bold; }
        public static Line Of(string text, float alpha = 1f) => new(text ?? "", alpha, false);
        public static Line Bold(string text) => new(text ?? "", 1f, true);
    }

    // -------------------------------------------------------------------------------------------------
    //  Drawing: QC InfoMessages_drawstring — word-wrap; when flip, right-align each wrapped piece.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Draw one (logical, color-coded) info line word-wrapped to the inner width and return the Y of the next
    /// line. Port of QC <c>InfoMessages_drawstring</c>: continuation lines indent by one font width, and when
    /// <paramref name="flip"/> is set every wrapped piece is right-aligned to the inner right edge. Adds the QC
    /// 0.25-line gap after the wrapped block.
    /// </summary>
    private float DrawWrappedLine(Rect2 area, float y, string text, Color baseColor, int size, Font font, bool flip)
    {
        float lineH = size;
        float maxW = area.Size.X;
        float left = area.Position.X;
        float right = area.Position.X + area.Size.X;
        float bottom = area.Position.Y + area.Size.Y;

        // QC: continuation pieces lose one font-width of room (offset = fontsize.x) so wrapped text indents.
        float charW = MeasureText(" ", size);
        bool first = true;
        string carry = ""; // color carried across a wrap (QC stringwidth_colors keeps the active color)

        foreach (string piece in WrapColored(text, maxW, size, charW))
        {
            if (y + lineH > bottom) return y + lineH; // QC stops at panel_size
            string toDraw = carry + piece;
            float visW = MeasureText(HudText.Strip(toDraw), size);

            float x;
            if (flip)
                x = Mathf.Max(left, right - visW);      // right-align; never start left of the panel (over-wide word)
            else
                x = left + (first ? 0f : charW);        // left, continuation lines indent one char

            DrawColoredRuns(new Vector2(x, y), toDraw, baseColor, size, font);
            carry = FindLastColorCode(toDraw);
            y += lineH;
            first = false;
        }

        // QC: pos.y += fontsize.y * 0.25 — a small gap between logical messages.
        y += lineH * 0.25f;
        return y;
    }

    /// <summary>
    /// Word-wrap a color-coded line to <paramref name="maxW"/>, measuring on visible glyphs only (codes are
    /// zero-width, QC <c>stringwidth_colors</c>). Continuation lines have <paramref name="indent"/> less room.
    /// Returns at least one piece so a long unbreakable word still shows.
    /// </summary>
    private static List<string> WrapColored(string line, float maxW, int size, float indent)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) { result.Add(""); return result; }

        string[] words = line.Split(' ');
        var cur = new StringBuilder();
        bool firstLine = true;

        float Budget() => firstLine ? maxW : Mathf.Max(8f, maxW - indent);

        foreach (string word in words)
        {
            string candidate = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length > 0 && MeasureText(HudText.Strip(candidate), size) > Budget())
            {
                result.Add(cur.ToString());
                cur.Clear();
                cur.Append(word);
                firstLine = false;
            }
            else
            {
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(word);
            }
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        if (result.Count == 0) result.Add(line);
        return result;
    }

    /// <summary>
    /// Lay out the <c>^N</c> color runs of <paramref name="line"/> left-to-right from <paramref name="origin"/>,
    /// drawing with <paramref name="font"/> (regular or bold). Godot can't color a multi-run string in one call,
    /// so we draw each run with a manual drop shadow (matching the base <see cref="DrawText"/> look).
    /// </summary>
    private void DrawColoredRuns(Vector2 origin, string line, Color baseColor, int size, Font font)
    {
        float cx = origin.X;
        foreach (HudText.Run run in HudText.Parse(line, baseColor))
        {
            if (string.IsNullOrEmpty(run.Text)) continue;
            Vector2 at = new(cx, origin.Y + size);
            var shadow = new Color(0f, 0f, 0f, run.Color.A * 0.7f);
            DrawString(font, at + new Vector2(1f, 1f), run.Text, HorizontalAlignment.Left, -1f, size, shadow);
            DrawString(font, at, run.Text, HorizontalAlignment.Left, -1f, size, run.Color);
            cx += font.GetStringSize(run.Text, HorizontalAlignment.Left, -1f, size).X;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Hint / line builders (QC sprintf strings with keybind substitution where applicable)
    // -------------------------------------------------------------------------------------------------

    /// <summary>The rotating spectator hint (QC group0 switch, 3 variants), observer vs chasing. When chasing a
    /// player and <paramref name="observeBlocked"/> is set (QC <c>observe_blocked</c>), the case-1 hint drops the
    /// "secondary fire to observe" half (the server forbids switching to free-fly), matching the QC.</summary>
    private static string SpectateHint(int specStatus, int sel, bool observeBlocked)
    {
        bool observing = specStatus == -1;
        return sel switch
        {
            0 => observing
                ? "^1Press ^3primary fire^1 to spectate"
                : "^1Press ^3next weapon^1 or ^3previous weapon^1 for next or previous player",
            1 => observing
                ? "^1Use ^3next weapon^1 or ^3previous weapon^1 to change the speed"
                : observeBlocked
                    ? "^1Press ^3drop weapon^1 to change camera mode"
                    : "^1Press ^3secondary fire^1 to observe, ^3drop weapon^1 to change camera mode",
            _ => "^1Press ^3server info^1 for gametype info",
        };
    }

    /// <summary>The "press jump to join" line (QC sprintf with getcommandkey "jump"), keybind-substitutable.</summary>
    private string BuildJoinHint()
    {
        if (!string.IsNullOrWhiteSpace(JoinHint))
            return JoinHint;
        return "^1Press ^3jump^1 to join";
    }

    /// <summary>The respawn prompt when the timer has elapsed (QC fire-to-respawn), keybind-substitutable.</summary>
    private string BuildRespawnHint()
    {
        if (!string.IsNullOrWhiteSpace(JoinHint))
            return JoinHint;
        return "^1Press ^3jump^1 to respawn";
    }

    /// <summary>"^2You're queued to join the [color][name]^2 team" (QC Team_ColorCode/Team_ColorName).</summary>
    private static string BuildQueuedTeamLine(int teamIndex)
    {
        (string code, string name) = TeamColorAndName(teamIndex);
        return $"^2You're queued to join the {code}{name}^2 team";
    }

    /// <summary>
    /// Build the comma-joined colored team list for a 4-bit team mask (QC <c>get_team_list</c>); each name is
    /// suffixed with the blink color so the joiner text resumes the nag color after a team name.
    /// </summary>
    private static string TeamMaskList(int mask, string suffixColor)
    {
        var sb = new StringBuilder();
        for (int bit = 0; bit < 4; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            (string code, string name) = TeamColorAndName(bit + 1);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(code).Append(name).Append(suffixColor);
        }
        return sb.ToString();
    }

    /// <summary>Map a 1-based team index to its ^N color code + display name (QC NUM_TEAM_1..4).</summary>
    private static (string Code, string Name) TeamColorAndName(int teamIndex) => teamIndex switch
    {
        1 => ("^1", "Red"),
        2 => ("^4", "Blue"),
        3 => ("^6", "Pink"),
        4 => ("^3", "Yellow"),
        _ => ("^7", "team"),
    };

    /// <summary>
    /// Return the trailing <c>^N</c>/<c>^xRGB</c> color code still in effect at the end of <paramref name="s"/>
    /// (QC <c>find_last_color_code</c>), or "" — so the next wrapped piece resumes that color.
    /// </summary>
    private static string FindLastColorCode(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] != '^') continue;
            int carets = 1;
            while (i - carets >= 0 && s[i - carets] == '^') carets++;
            if ((carets & 1) == 0) { i -= carets - 1; continue; } // escaped run → keep scanning

            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (n >= '0' && n <= '9') return s.Substring(i, 2);                 // ^N
            if ((n == 'x' || n == 'X') && i + 4 < s.Length
                && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]))
                return s.Substring(i, 5);                                        // ^xRGB
        }
        return "";
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Ceil a seconds value to a sane displayable whole number. Guards against NaN/Inf or an absurd networked
    /// value producing <c>int.MinValue</c> garbage in a countdown string (<c>Mathf.CeilToInt(NaN)</c> overflows).
    /// </summary>
    private static int CeilSeconds(float seconds)
    {
        if (float.IsNaN(seconds) || seconds < 0f) return 0;
        if (seconds > 86400f) return 86400; // cap at a day; anything larger is a bad feed
        return Mathf.CeilToInt(seconds);
    }

    /// <summary>The match/sim time for blink + hint cadence: <see cref="Now"/> when fed, else the engine clock.
    /// Guards against a NaN/Inf <see cref="Now"/> from the net layer — feeding NaN through would permanently
    /// poison <see cref="_imgFade"/>/<see cref="_imgTime"/>/<see cref="_lastNow"/> and the blink color.</summary>
    private float ResolveNow()
    {
        if (Now >= 0.0 && !double.IsNaN(Now) && !double.IsInfinity(Now))
            return (float)Now;
        return (float)(Time.GetTicksMsec() / 1000.0);
    }

    private float _lastNow = -1f;

    /// <summary>Frame time for the hint fade (QC <c>frametime</c>): delta of <see cref="ResolveNow"/> between draws.</summary>
    private float ResolveFrameTime()
    {
        float now = ResolveNow();
        float dt = _lastNow < 0f ? 0f : Mathf.Clamp(now - _lastNow, 0f, 0.25f);
        _lastNow = now;
        return dt;
    }

    /// <summary>The QC <c>__hud_configure</c> editor help text (shown only while the HUD editor is open).</summary>
    private void DrawConfigureHelp()
    {
        DrawBackground();
        float pad = Cfg.Padding;
        var inner = new Rect2(pad, pad, Mathf.Max(8f, Size2.X - pad * 2f), Mathf.Max(8f, Size2.Y - pad * 2f));
        int fontsize = Mathf.Max(8, Mathf.RoundToInt(inner.Size.Y * 0.2f));
        bool flip = CvarBool("flip");

        var help = new[]
        {
            "^7Press ^3ESC ^7to show HUD options.",
            "^3Double-click ^7a panel for panel-specific options.",
            "^3CTRL ^7to disable collision testing, ^3SHIFT ^7and",
            "^3ALT ^7+ ^3ARROW KEYS ^7for fine adjustments.",
        };
        float y = inner.Position.Y;
        Color baseCol = new(1f, 1f, 1f, LiveFgAlpha);
        foreach (string s in help)
        {
            y = DrawWrappedLine(inner, y, s, baseCol, fontsize, Font, flip);
            if (y > inner.Position.Y + inner.Size.Y) break;
        }
    }
}
