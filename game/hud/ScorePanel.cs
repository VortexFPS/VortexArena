using System.Collections.Generic;
using System.Globalization;
using Godot;
using XonoticGodot.Common.Gameplay;     // Teams
using XonoticGodot.Common.Services;     // CvarFlags
using XonoticGodot.Engine.Simulation;   // CvarService (RegisterDefaults)

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Score (#7) — the separate in-game score OVERLAY (NOT the full scoreboard). C# port of
/// Base/.../qcsrc/client/hud/panel/score.qc (HUD_Score / HUD_Score_Rankings). It shows the local player's
/// standing at a glance in the top-right corner, in one of several layouts chosen by gametype + the
/// <c>hud_panel_score_rankings</c> cvar:
/// <list type="bullet">
///   <item><b>Rankings</b> (when <c>_rankings != 0</c>, or while spectating): a small leaderboard — up to
///         SCOREPANEL_MAX_ENTRIES rows of name + score, the local row highlighted by colour (green = first,
///         yellow = mid, red = last). In teamplay the first line is the team scores in team colours
///         (QC <c>HUD_Score_Rankings</c>).</item>
///   <item><b>Race / CTS distribution</b> (non-team time modes): the local player's encoded lap time big on the
///         left, and the signed delta to the leader on the right, green when ahead (faster, '-') / red when
///         behind ('+') (QC the <c>SFL_TIME &amp;&amp; !teamplay</c> branch).</item>
///   <item><b>FFA distribution</b> (non-team frag modes, rankings off): the local score big on the left, and a
///         signed +/- gap to the next player on the right, colour-graded by how big the lead/deficit is
///         (QC the <c>!teamplay</c> branch).</item>
///   <item><b>Team scores</b> (teamplay, rankings off): each team's primary score across the panel in team
///         colours, the leading team highlighted; the local team gets the big left cell, the others a small
///         grid on the right (QC the teamgames branch).</item>
/// </list>
///
/// Data is networked, so this panel is a pure renderer of injected state. The integration task feeds it via the
/// public setters (<see cref="SetMode"/>, <see cref="SetSelf"/>, <see cref="SetTeamScores"/>,
/// <see cref="SetRankings"/>). Per the contract §9, until it is fed it MUST draw nothing — <see cref="HasData"/>
/// gates <see cref="DrawPanel"/>. The luma table already supplies this panel's id/layout/show-flags + the
/// <c>border_corner_northeast</c> skin frame, so no identity virtuals are overridden.
/// </summary>
public partial class ScorePanel : HudPanel
{
    // QC: SCOREPANEL_MAX_ENTRIES / SCOREPANEL_ASPECTRATIO (score.qc:19-20).
    private const int MaxEntries = 6;
    private const float AspectRatio = 2f;
    // QC: the rankings highlight fill strength (score.qc:29).
    private const float HighlightAlpha = 0.2f;

    /// <summary>Which layout to draw — the gametype/spectate selector the QC branches on.</summary>
    public enum ScoreMode
    {
        /// <summary>QC the <c>!teamplay</c> frag branch: own score + signed gap to the next player.</summary>
        FreeForAll,
        /// <summary>QC the teamgames branch: per-team primary scores, leader highlighted.</summary>
        Team,
        /// <summary>QC the <c>SFL_TIME &amp;&amp; !teamplay</c> branch: own lap time + delta to the leader.</summary>
        Race,
    }

    /// <summary>One rankings row (QC a player in <c>HUD_Score_Rankings</c>): coloured name + primary score.</summary>
    public readonly struct RankRow
    {
        /// <summary>Display name, may carry <c>^N</c> colour codes (QC <c>entcs_GetName</c>).</summary>
        public readonly string Name;
        /// <summary>The player's primary score string (already formatted: frags / caps / time, QC <c>ftos(scores)</c>).</summary>
        public readonly string Score;
        /// <summary>Team colour code (QC <c>pl.team</c>; <see cref="Teams.None"/> in FFA). Tints the score cell.</summary>
        public readonly int Team;
        /// <summary>True for the local player's row (highlighted by <see cref="Place"/> colour).</summary>
        public readonly bool IsLocal;

        public RankRow(string name, string score, int team = 0, bool isLocal = false)
        {
            Name = name ?? "";
            Score = score ?? "";
            Team = team;
            IsLocal = isLocal;
        }
    }

    /// <summary>One team's score cell (QC <c>tm.(teamscores(ts_primary))</c> in team colour).</summary>
    public readonly struct TeamScore
    {
        public readonly int Team;        // QC tm.team (Teams.Red/Blue/...)
        public readonly string Score;    // already-formatted primary score
        public readonly bool IsLocal;    // QC tm.team == myteam — the local team gets the big cell

        public TeamScore(int team, string score, bool isLocal = false)
        {
            Team = team;
            Score = score ?? "";
            IsLocal = isLocal;
        }
    }

    // ---- injected state (all set by the net/match integration task) ----

    private ScoreMode _mode = ScoreMode.FreeForAll;
    private bool _spectating;          // QC spectatee_status == -1 → force the rankings leaderboard

    private bool _haveSelf;            // QC: do we have a local-player score to show?
    private string _selfScore = "";    // QC ftos(me.scores(ps_primary)) — frags/caps; or the encoded race time
    private int _selfPlace;           // 1-based place (1 = leading). 0 = unknown.

    // FFA distribution (QC me.scores - pl.scores, the gap to the next player). HasGap gates the +/- badge.
    private bool _haveGap;
    private float _gap;               // numeric gap (frags); sign + magnitude drive the colour/badge

    // Race distribution (QC the time-encoded delta to the leader). HasRaceDelta gates the delta cell.
    private bool _haveRaceDelta;
    private float _raceDeltaSeconds;  // signed seconds: <=0 ahead (green '-'), >0 behind (red '+')

    private readonly List<TeamScore> _teamScores = new();
    private readonly List<RankRow> _rankRows = new();

    // QC: contents change as scores update — but only on data change, so static unless fed.
    public override bool IsDynamic => false;

    /// <summary>True once any displayable score has been injected (QC: there is a local player with scores).
    /// While false the panel self-blanks (draws nothing), per the contract's "self-blank when no data" rule.</summary>
    public bool HasData => _haveSelf || _teamScores.Count > 0 || _rankRows.Count > 0;

    // =====================================================================================
    //  Public feed surface (the integration task wires networked scores into these)
    // =====================================================================================

    /// <summary>Select the layout (QC the gametype branch) and whether we are spectating (QC
    /// <c>spectatee_status == -1</c>, which forces the rankings leaderboard).</summary>
    public void SetMode(ScoreMode mode, bool spectating = false)
    {
        if (_mode == mode && _spectating == spectating) return;
        _mode = mode;
        _spectating = spectating;
        QueueRedraw();
    }

    /// <summary>
    /// Set the local player's standing (QC <c>me.(scores(ps_primary))</c> + the player's place). Pass the
    /// already-formatted primary score (frags/caps for frag modes — use <see cref="SetSelfRace"/> for race times).
    /// <paramref name="place"/> is 1-based (1 = leading); 0 = unknown.
    /// </summary>
    public void SetSelf(string score, int place)
    {
        _haveSelf = !string.IsNullOrEmpty(score);
        _selfScore = score ?? "";
        _selfPlace = place;
        QueueRedraw();
    }

    /// <summary>Convenience numeric overload for frag/cap modes (QC <c>ftos(score)</c>).</summary>
    public void SetSelf(int score, int place) => SetSelf(score.ToString(CultureInfo.InvariantCulture), place);

    /// <summary>
    /// Set the local player's race standing (QC the SFL_TIME branch): <paramref name="lapSeconds"/> is the
    /// encoded lap time shown big on the left; <paramref name="deltaSeconds"/> is the signed gap to the leader
    /// (&lt;= 0 = ahead/faster, drawn green with '-'; &gt; 0 = behind, drawn red with '+'). Pass
    /// <paramref name="hasDelta"/> = false when there is no leader to compare to (QC <c>pl == NULL</c>) so only
    /// the time shows.
    /// </summary>
    public void SetSelfRace(float lapSeconds, float deltaSeconds, bool hasDelta)
    {
        _haveSelf = true;
        _selfScore = RaceTimeToString(lapSeconds);
        _haveRaceDelta = hasDelta;
        _raceDeltaSeconds = deltaSeconds;
        QueueRedraw();
    }

    /// <summary>Set the FFA gap to the next player (QC <c>me.scores - pl.scores</c>) used for the +/- badge and
    /// the colour grade. Pass <paramref name="hasGap"/> = false when the local player is alone (no badge).</summary>
    public void SetSelfGap(float gap, bool hasGap)
    {
        _haveGap = hasGap;
        _gap = gap;
        QueueRedraw();
    }

    /// <summary>Set the per-team primary scores (QC <c>tm.(teamscores(ts_primary))</c>) for the team layout +
    /// the rankings team line. Order is the leader-first sort the caller chose (QC <c>teams.sort_next</c>).</summary>
    public void SetTeamScores(IEnumerable<TeamScore> teams)
    {
        _teamScores.Clear();
        if (teams is not null) _teamScores.AddRange(teams);
        QueueRedraw();
    }

    /// <summary>Set the rankings leaderboard rows (QC <c>players.sort_next</c> slice). Order is the caller's
    /// score-sorted order; the local row is whichever has <see cref="RankRow.IsLocal"/>.</summary>
    public void SetRankings(IEnumerable<RankRow> rows)
    {
        _rankRows.Clear();
        if (rows is not null) _rankRows.AddRange(rows);
        QueueRedraw();
    }

    /// <summary>Clear all injected state so the panel self-blanks (e.g. on disconnect / pre-spawn).</summary>
    public void Clear()
    {
        _haveSelf = _haveGap = _haveRaceDelta = false;
        _selfScore = "";
        _selfPlace = 0;
        _teamScores.Clear();
        _rankRows.Clear();
        QueueRedraw();
    }

    // =====================================================================================
    //  Behaviour cvars (QC HUD_Score_Export saves hud_panel_score_rankings)
    // =====================================================================================

    /// <summary>Register this panel's behaviour cvars (auto-invoked by <see cref="HudConfig"/> via reflection).
    /// QC <c>hud_panel_score_rankings</c>: 0 = off (own-score/team-score distribution), non-zero = the rankings
    /// leaderboard (QC only special-cases <c>== 1</c> for the editor preview, so any non-zero shows rankings).</summary>
    public static void RegisterDefaults(CvarService c)
    {
        c.Register("hud_panel_score_rankings", "1", CvarFlags.Save);
    }

    /// <summary>QC <c>autocvar_hud_panel_score_rankings</c> (0 = off, non-zero = rankings), read live.</summary>
    private int RankingsMode
    {
        get
        {
            float v = CvarF("rankings", 1f);
            return float.IsFinite(v) ? Mathf.RoundToInt(v) : 1; // a corrupt cvar must not yield an undefined int
        }
    }

    // =====================================================================================
    //  Draw (QC HUD_Score)
    // =====================================================================================

    protected override void DrawPanel()
    {
        if (EditorSession) return;   // no standings in an editing session; the editor readout owns this corner
        if (!HasData) return; // self-blank until fed (contract §9)

        DrawBackground(); // skin corner frame (luma border_corner_northeast); no-op when bg is "0"

        // QC: padding inset (HUD_Score: pos += padding, size -= 2*padding).
        // Clamp pad so it can never exceed half the panel (a malformed bg_padding cvar must not invert size).
        float pad = Mathf.Max(0f, Cfg.Padding);
        if (!float.IsFinite(pad)) pad = 0f;
        var pos = new Vector2(pad, pad);
        // size is strictly >= 1 on both axes so every downstream cell division has a finite, positive divisor.
        var size = new Vector2(Mathf.Max(1f, Size2.X - 2f * pad), Mathf.Max(1f, Size2.Y - 2f * pad));

        // QC: rankings view is forced while spectating, or selected by the cvar (score.qc:213, :257).
        bool rankings = _spectating || RankingsMode != 0;

        switch (_mode)
        {
            case ScoreMode.Race:
                DrawRaceDistribution(pos, size);
                break;

            case ScoreMode.Team:
                if (rankings) DrawRankings(pos, size);
                else DrawTeamScores(pos, size);
                break;

            default: // FreeForAll
                if (rankings) DrawRankings(pos, size);
                else DrawFreeForAll(pos, size);
                break;
        }
    }

    // ---- race / CTS distribution (QC: SFL_TIME && !teamplay) ----

    private void DrawRaceDistribution(Vector2 pos, Vector2 size)
    {
        if (!_haveSelf) return;

        // QC: distribution starts at 0; with no leader to compare to it stays 0, so the record cell is treated as
        // "ahead" (the highlight at score.qc:206 fires for distribution <= 0). Only a real delta can flip this.
        bool ahead = !_haveRaceDelta || _raceDeltaSeconds <= 0f;
        if (_haveRaceDelta)
        {
            Color dcol = ahead ? Green() : Red();
            string sign = ahead ? "-" : "+";
            string delta = sign + RaceDeltaToString(Mathf.Abs(_raceDeltaSeconds));
            var deltaCell = new Rect2(pos.X + 0.75f * size.X, pos.Y, 0.25f * size.X, size.Y / 3f);
            DrawAspectString(deltaCell, delta, dcol);
        }

        // QC: highlight the record cell when ahead (distribution <= 0).
        if (ahead)
            DrawHighlight(new Rect2(pos.X, pos.Y, 0.75f * size.X, size.Y));

        // QC: the lap time big on the left, in white.
        DrawAspectString(new Rect2(pos.X, pos.Y, 0.75f * size.X, size.Y), _selfScore, White());
    }

    // ---- FFA distribution (QC: !teamplay frag branch, rankings off) ----

    private void DrawFreeForAll(Vector2 pos, Vector2 size)
    {
        if (!_haveSelf) return;

        // QC: with no other player (pl == NULL) the distribution stays 0 (score.qc:228); only a real gap moves it.
        float gap = _haveGap ? _gap : 0f;

        // QC: distribution_color grading by the gap magnitude (score.qc:234-241).
        Color col;
        if (gap >= 5f) col = Green();
        else if (gap >= 0f) col = White();
        else if (gap >= -5f) col = Yellow();
        else col = Red();

        // QC: the score big on the left; when ahead/level (distribution >= 0) the cell is highlighted (score.qc:245).
        if (gap >= 0f)
            DrawHighlight(new Rect2(pos.X, pos.Y, 0.75f * size.X, size.Y));

        DrawAspectString(new Rect2(pos.X, pos.Y, 0.75f * size.X, size.Y), _selfScore, col);

        // QC always draws the distribution badge on the upper-right (score.qc:252) — "0" when alone (pl == NULL,
        // distribution stays 0), "+N" when ahead, "-N"/"N" otherwise.
        string badge = gap > 0f ? "+" + GapToString(gap) : GapToString(gap);
        var badgeCell = new Rect2(pos.X + 0.75f * size.X, pos.Y, 0.25f * size.X, size.Y / 3f);
        DrawAspectString(badgeCell, badge, col);
    }

    // ---- team scores (QC: teamgames branch, rankings off) ----

    private void DrawTeamScores(Vector2 pos, Vector2 size)
    {
        if (_teamScores.Count == 0) return;

        // QC HUD_Score: the spectator view tiles ALL team cells across the panel; an in-team player gets a big
        // own-team cell on the left + the other teams in a small column on the right. We mirror that split.
        // The leading score is highlighted (QC max_fragcount).
        float maxScore = float.NegativeInfinity;
        bool any = false;
        foreach (TeamScore t in _teamScores)
            if (float.TryParse(t.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            { any = true; if (v > maxScore) maxScore = v; }

        bool LeadingCell(TeamScore t) =>
            any && float.TryParse(t.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= maxScore;

        bool haveLocal = false;
        foreach (TeamScore t in _teamScores) if (t.IsLocal) { haveLocal = true; break; }

        if (_spectating || !haveLocal)
        {
            // QC spectator grid: rows × columns of team cells across the whole panel.
            int count = _teamScores.Count;
            // size.X is guaranteed >= 1 (DrawPanel), but FloorToInt of a non-finite ratio yields a garbage int —
            // compute the raw ratio defensively, then clamp into [1, count] before it can reach the loop bounds.
            float rowsRatio = MaxEntries * size.Y / size.X;
            int rowsRaw = float.IsFinite(rowsRatio) ? Mathf.FloorToInt(rowsRatio) : 1;
            int rows = Mathf.Max(1, Mathf.Clamp(rowsRaw, 1, count));
            int cols = Mathf.Max(1, Mathf.CeilToInt(count / (float)rows));
            float cw = size.X / cols;
            float ch = size.Y / rows;
            for (int i = 0; i < count; i++)
            {
                int col = i / rows;
                int row = i % rows;
                var cell = new Rect2(pos.X + col * cw, pos.Y + row * ch, cw, ch);
                TeamScore t = _teamScores[i];
                if (LeadingCell(t)) DrawHighlight(cell);
                DrawAspectString(cell, t.Score, TeamColor(t.Team));
            }
            return;
        }

        // In-team: big own-team cell on the left; the other teams stacked small on the right.
        var bigCell = new Rect2(pos.X, pos.Y, 0.75f * size.X, size.Y);
        TeamScore local = default;
        var others = new List<TeamScore>();
        foreach (TeamScore t in _teamScores) { if (t.IsLocal) local = t; else others.Add(t); }

        if (LeadingCell(local)) DrawHighlight(bigCell);
        DrawAspectString(bigCell, local.Score, TeamColor(local.Team));

        float smallH = size.Y / 3f;
        for (int i = 0; i < others.Count; i++)
        {
            var cell = new Rect2(pos.X + 0.75f * size.X, pos.Y + i * smallH, 0.25f * size.X, smallH);
            TeamScore t = others[i];
            if (LeadingCell(t)) DrawHighlight(cell);
            DrawAspectString(cell, t.Score, TeamColor(t.Team));
        }
    }

    // ---- rankings leaderboard (QC HUD_Score_Rankings) ----

    private void DrawRankings(Vector2 pos, Vector2 size)
    {
        // QC: entries = bound(1, floor(MAX * size.y/size.x * ASPECT), MAX) (score.qc:21).
        // Guard the ratio: a non-finite value would make FloorToInt return a garbage int that survives the clamp
        // domain check only by luck — normalise to 1 first.
        float entriesRatio = MaxEntries * size.Y / Mathf.Max(1f, size.X) * AspectRatio;
        int entries = Mathf.Clamp(float.IsFinite(entriesRatio) ? Mathf.FloorToInt(entriesRatio) : 1, 1, MaxEntries);
        float lineH = size.Y / entries; // entries in [1, MaxEntries] so lineH is finite and positive
        int fontPx = Mathf.Max(8, Mathf.RoundToInt(lineH));

        // QC: name_size = 0.75 width, spacing = 0.04 width (score.qc:27-28).
        float nameSize = size.X * 0.75f;
        float spacing = size.X * 0.04f;

        float y = pos.Y;
        int firstPl = 0;

        // QC: in teamplay, the first line shows the team scores in team colours.
        bool teamplay = _mode == ScoreMode.Team && _teamScores.Count > 0;
        if (teamplay)
        {
            // QC HUD_Score_Rankings (score.qc:86-95): the first line tiles each team's primary score in team
            // colour; the highlight (drawfill) marks the LOCAL team's cell (tm.team == myteam), NOT the leader.
            float cellW = size.X / _teamScores.Count;
            for (int i = 0; i < _teamScores.Count; i++)
            {
                TeamScore t = _teamScores[i];
                var cell = new Rect2(pos.X + cellW * i, y, cellW, lineH);
                if (t.IsLocal)
                    DrawHighlight(cell, HighlightAlpha);
                DrawAspectString(cell, t.Score, TeamColor(t.Team));
            }
            firstPl = 1;
            y += lineH;
        }

        // QC: the player rows. The local row is highlighted by place colour (green/yellow/red).
        int shown = firstPl;
        for (int idx = 0; idx < _rankRows.Count && shown < entries; idx++, shown++)
        {
            RankRow r = _rankRows[idx];

            if (r.IsLocal)
            {
                // QC: first = green, last = red, otherwise yellow (score.qc:108-127).
                Color hl = RankHighlightColor();
                DrawFill(new Rect2(pos.X, y, size.X, lineH), hl, HighlightAlpha * LiveFgAlpha);
            }

            // QC: the name is right-aligned within name_size; the score follows after the spacing.
            Color scoreCol = teamplay && r.Team != Teams.None ? TeamColor(r.Team) : White();
            DrawColoredRight(pos.X, y, nameSize, r.Name, White(), fontPx);
            DrawText(new Vector2(pos.X + nameSize + spacing, y), r.Score, scoreCol, fontPx);

            y += lineH;
        }
    }

    /// <summary>QC: the local rankings row colour (score.qc:116-125) — green first, red last, else yellow.</summary>
    private Color RankHighlightColor()
    {
        if (_selfPlace <= 1 && _selfPlace > 0) return Green();        // first
        if (_selfPlace >= _rankRows.Count && _rankRows.Count > 0) return Red(); // last
        return Yellow();
    }

    // =====================================================================================
    //  Draw primitives (QC drawstring_aspect / HUD_Panel_DrawHighlight / drawfill)
    // =====================================================================================

    /// <summary>QC <c>drawstring_aspect</c>: a string fit (centered) into a cell, sized to the cell height.</summary>
    private void DrawAspectString(Rect2 cell, string text, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        // A degenerate cell (non-finite or non-positive width/height) draws nothing rather than emitting
        // off-panel / NaN coordinates into the canvas.
        if (!float.IsFinite(cell.Size.X) || !float.IsFinite(cell.Size.Y) ||
            cell.Size.X <= 0f || cell.Size.Y <= 0f) return;
        if (!float.IsFinite(cell.Position.X) || !float.IsFinite(cell.Position.Y)) return;

        int fontPx = Mathf.Max(8, Mathf.RoundToInt(cell.Size.Y * 0.85f));
        // shrink to fit the cell width if needed (QC's aspect fit clamps the size to the box)
        float w = MeasureText(text, fontPx);
        if (w > cell.Size.X && w > 0f)
            fontPx = Mathf.Max(8, Mathf.RoundToInt(fontPx * cell.Size.X / w));
        // vertically center within the cell.
        float ty = cell.Position.Y + (cell.Size.Y - fontPx) * 0.5f;
        DrawTextCentered(new Vector2(cell.Position.X, ty), cell.Size.X, text, color, fontPx);
    }

    /// <summary>QC <c>HUD_Panel_DrawHighlight</c>: a white wash behind the leading/own cell at the fg alpha.</summary>
    private void DrawHighlight(Rect2 cell, float alpha = -1f)
    {
        float a = (alpha < 0f ? LiveFgAlpha : alpha * LiveFgAlpha);
        DrawRect(cell, new Color(1f, 1f, 1f, Mathf.Clamp(a, 0f, 1f) * 0.18f));
    }

    private void DrawFill(Rect2 cell, Color color, float alpha)
        => DrawRect(cell, new Color(color.R, color.G, color.B, Mathf.Clamp(alpha, 0f, 1f)));

    /// <summary>Draw a possibly <c>^N</c>-coloured string right-aligned to end at <paramref name="rightX"/>
    /// (QC: the rankings name is drawn at <c>name_size - stringwidth(name)</c>).</summary>
    private void DrawColoredRight(float leftX, float topY, float width, string text, Color baseColor, int size)
    {
        string plain = HudText.Strip(text);
        float w = MeasureText(plain, size);
        float cx = leftX + Mathf.Max(0f, width - w);
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, topY), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    // =====================================================================================
    //  Colours (QC Team_ColorRGB * 0.8 + the distribution palette) + formatting
    // =====================================================================================

    private Color White() => new(1f, 1f, 1f, LiveFgAlpha);
    private Color Green() => new(0f, 1f, 0f, LiveFgAlpha);   // QC '0 1 0'
    private Color Yellow() => new(1f, 1f, 0f, LiveFgAlpha);  // QC '1 1 0'
    private Color Red() => new(1f, 0f, 0f, LiveFgAlpha);     // QC '1 0 0' / eX

    /// <summary>QC <c>Team_ColorRGB(team) * 0.8</c> — the dimmed team colour the score cells use.</summary>
    private Color TeamColor(int team)
    {
        // The port's chosen team RGB (matches ScoreboardPanel), scaled by QC's 0.8.
        (float r, float g, float b) = team switch
        {
            Teams.Red    => (1f, 0.35f, 0.35f),
            Teams.Blue   => (0.4f, 0.55f, 1f),
            Teams.Yellow => (1f, 0.95f, 0.35f),
            Teams.Pink   => (1f, 0.45f, 0.85f),
            _            => (0.8f, 0.8f, 0.8f),
        };
        return new Color(r * 0.8f, g * 0.8f, b * 0.8f, LiveFgAlpha);
    }

    /// <summary>QC <c>TIME_ENCODED_TOSTRING</c> for the lap time (M:SS.dd).</summary>
    private static string RaceTimeToString(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0f) seconds = 0f; // a NaN/Inf cast to int is undefined
        int total = (int)seconds;
        int m = total / 60;
        int s = total % 60;
        int hundredths = Mathf.RoundToInt((seconds - total) * 100f);
        if (hundredths >= 100) { hundredths -= 100; s += 1; if (s >= 60) { s -= 60; m += 1; } }
        return $"{m}:{s:D2}.{hundredths:D2}";
    }

    /// <summary>QC the race delta formatting (<c>ftos_decimals(fabs(delta), TIME_DECIMALS)</c>) — S.dd.</summary>
    private static string RaceDeltaToString(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0f) seconds = 0f;
        return seconds.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Format the FFA gap (whole frags; QC <c>ftos(distribution)</c>).</summary>
    private static string GapToString(float gap) =>
        (float.IsFinite(gap) ? Mathf.RoundToInt(gap) : 0).ToString(CultureInfo.InvariantCulture);
}
