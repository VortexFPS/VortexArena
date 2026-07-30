using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;

namespace VortexArena.Game.Hud;

/// <summary>
/// Map-vote panel — port of Base/.../qcsrc/client/mapvoting.qc <c>MapVote_Draw</c> (HUD panel #MAPVOTE,
/// registered in hud.qh to <c>MapVote_Draw</c>). At the end of a match (intermission) the server offers a set
/// of candidate maps (or gametypes) and the clients draw a grid of cells — one per candidate, each with its
/// map name, current vote count and a thumbnail (the real Xonotic level-shot) — plus a big bold title
/// ("Vote for a map") and a green seconds-remaining countdown (<c>mv_timeout - time</c>). Your own vote
/// (<c>mv_ownvote</c>) is highlighted with a colored border (<c>hud_panel_mapvote_highlight_border</c>), the
/// currently-selected cell pulses, and when voting ends the winner (<c>mv_winner</c>) is revealed (the winning
/// thumbnail grows to the center) while the rest fade out (<c>mv_winner_alpha</c>).
///
/// The whole vote state is server-driven (the <c>mapvote</c> net entity), so the net/match layer pushes it
/// here via <see cref="SetVote"/> and updates the live bits (<see cref="OwnVote"/>, <see cref="Selection"/>,
/// <see cref="Winner"/>, <see cref="SetVotes"/>) — the same injection model as the other match panels. The
/// panel computes the grid (best item aspect ratio, like QC <c>HUD_GetTableSize_BestItemAR</c>), draws each
/// cell with its level-shot + name + count + highlight (QC <c>MapVote_DrawMapItem</c> /
/// <c>GameTypeVote_DrawGameTypeItem</c>), the optional abstain "Don't care" line (QC
/// <c>MapVote_DrawAbstain</c>), and the countdown.
///
/// Thumbnails are loaded by map pic name through <see cref="TextureCache"/> (the host's VfsResolver does the
/// extension search + decode, exactly like the menu's map previews); a missing level-shot falls back to the
/// shipped <c>nopreview_map</c> art, and finally to a colored placeholder swatch so a cell is never blank.
/// </summary>
public partial class MapVotePanel : HudPanel
{
    /// <summary>One vote candidate (QC <c>mv_entries[id]</c> / <c>mv_votes[id]</c> entry).</summary>
    public readonly struct Candidate
    {
        /// <summary>Display name (QC <c>mv_entries[id]</c> — the map filename or the gametype name).</summary>
        public readonly string Name;
        /// <summary>Vote count (QC <c>mv_votes[id]</c>; -1 means "not available / hidden").</summary>
        public readonly int Votes;
        /// <summary>Map pic base name (QC <c>mv_pics[id]</c>, e.g. <c>maps/stormkeep</c>) for the level-shot,
        /// or the gametype icon pic (<c>gfx/menu/&lt;skin&gt;/gametype_*</c>). "" → placeholder swatch.</summary>
        public readonly string Pic;
        /// <summary>Human-readable name shown in the label (QC <c>mv_data[id]</c>: the map's title / the
        /// gametype's pretty name). Falls back to <see cref="Name"/> when unset.</summary>
        public readonly string Data;
        /// <summary>Multi-line description (QC <c>mv_desc[id]</c>) — gametype votes only; "" otherwise.</summary>
        public readonly string Description;
        /// <summary>QC <c>mv_flags[id] &amp; GTV_AVAILABLE</c>: the option is selectable. Unavailable
        /// gametype-vote options are dimmed and never highlighted/voted.</summary>
        public readonly bool Available;
        /// <summary>QC <c>mv_suggester[id]</c>: who suggested this map ("" = nobody).</summary>
        public readonly string Suggester;

        public Candidate(string name, int votes)
            : this(name, votes, "", "", "", true, "") { }

        public Candidate(string name, int votes, string pic, string data = "", string description = "",
            bool available = true, string suggester = "")
        {
            Name = name;
            Votes = votes;
            Pic = pic ?? "";
            Data = string.IsNullOrEmpty(data) ? (name ?? "") : data;
            Description = description ?? "";
            Available = available;
            Suggester = suggester ?? "";
        }

        /// <summary>Copy with a new vote count (QC incremental <c>mv_votes</c> update).</summary>
        public Candidate WithVotes(int votes) =>
            new(Name, votes, Pic, Data, Description, Available, Suggester);

        /// <summary>Copy with a new availability flag (QC <c>MapVote_UpdateMask</c> top-2 reveal).</summary>
        public Candidate WithAvailable(bool available) =>
            new(Name, Votes, Pic, Data, Description, available, Suggester);
    }

    private readonly List<Candidate> _candidates = new();

    /// <summary>QC <c>MAPVOTE_COUNT</c>: the hard cap on candidates the server may offer. Extra entries from an
    /// (adversarial/buggy) feed are dropped so the grid + per-frame draw loop stay bounded.</summary>
    private const int MaxCandidates = 30;

    /// <summary>QC <c>mv_active</c>: a map vote is in progress (the panel only draws while true).</summary>
    public bool Active { get; set; }

    /// <summary>QC <c>gametypevote</c>: this is a gametype vote (title becomes "Decide the gametype").</summary>
    public bool GametypeVote { get; set; }

    /// <summary>QC <c>mv_timeout</c>: absolute time the vote closes; the countdown is <c>mv_timeout - now</c>.</summary>
    public double Timeout { get; set; }

    /// <summary>QC <c>mv_ownvote</c>: index of the candidate the local player voted for (-1 = none).</summary>
    public int OwnVote { get; set; } = -1;

    /// <summary>QC <c>mv_selection</c>: index of the cell currently under the cursor/keyboard (-1 = none).</summary>
    public int Selection { get; set; } = -1;

    /// <summary>QC <c>mv_num_maps</c>: the number of map cells on the grid (excludes the abstain row). Stamped
    /// each frame by <see cref="DrawPanel"/> so the input handler (QC <c>MapVote_Move*</c>) can navigate.</summary>
    public int MapCount => _candidates.Count;

    /// <summary>QC <c>mv_columns</c>: the grid column count chosen by the best-aspect layout, stamped each frame
    /// by <see cref="DrawPanel"/> so the keyboard up/down navigation matches what's drawn.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>QC <c>mv_winner</c>: 1-based index of the winning candidate once decided (0 = not yet).</summary>
    public int Winner { get; set; }

    /// <summary>QC <c>mv_abstain</c>: an extra "Don't care" option is offered.</summary>
    public bool Abstain { get; set; }

    /// <summary>QC <c>mv_detail</c>: the server reveals per-option vote counts (the " (N votes)" suffix). When
    /// false the panel shows names only, like a blind vote.</summary>
    public bool Detail { get; set; } = true;

    /// <summary>QC <c>mv_tie_winner</c>: index of the (potential) winner to tint <c>^5</c> in the label;
    /// -2 = "tint whichever option(s) currently lead" (highest vote count). -1 = none.</summary>
    public int TieWinner { get; set; } = -1;

    /// <summary>QC <c>mapvote_chosenmap</c>: in a gametype vote that has already picked a map, the chosen map
    /// name shown as a subtitle under the title. "" = none.</summary>
    public string ChosenMap { get; set; } = "";

    /// <summary>QC <c>mv_winner_time</c>: the sim time the winner was revealed (drives the grow/fade reveal).
    /// 0 = not set; <see cref="Winner"/> being &gt; 0 still triggers the reveal even without a stamped time.</summary>
    public double WinnerTime { get; set; }

    /// <summary>Slave the countdown to this clock; &lt; 0 uses the sim clock (else its own ticker).</summary>
    public double Now { get; set; } = -1.0;

    /// <summary>QC <c>MV_FADETIME</c> (0.2s): the selection-highlight / post-select color ease duration.</summary>
    private const float FadeTime = 0.2f;

    /// <summary>QC <c>mv_select_lasttime[id]</c>: the sim time each cell was last the selection, driving the
    /// <c>(time - lasttime) / MV_FADETIME</c> fade of its highlight fill + its settle from yellow to base.</summary>
    private readonly double[] _selectLastTime = new double[MaxCandidates];

    /// <summary>QC <c>mv_top2_time</c>: when the ballot was reduced to the top 2 (the unavailable options start
    /// fading then). 0 = no reduce has happened.</summary>
    private double _top2Time;

    /// <summary>QC <c>mv_top2_alpha = max(0.2, 1 - (time - mv_top2_time)^2)</c>: the fading alpha for options
    /// that just lost availability in the mid-vote reduce. 0 ⇔ no reduce in effect (full dim 0.2 fallback).</summary>
    private float Top2Alpha(double now)
    {
        if (_top2Time <= 0.0 || !double.IsFinite(_top2Time)) return 0f;
        float dt = (float)(now - _top2Time);
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        return Mathf.Max(0.2f, 1f - dt * dt);
    }

    /// <summary>QC <c>mv_suggester_cache</c>: the last non-empty suggester name shown, kept so the
    /// "Suggested by: …" line fades out (over <see cref="FadeTime"/>) instead of vanishing on deselect.</summary>
    private string _suggesterCache = "";

    /// <summary>QC <c>mv_suggester_cachetime</c>: sim time the suggester cache was last refreshed.</summary>
    private double _suggesterCacheTime;

    public override bool IsDynamic => true;

    private double _localClock;
    private int _lastStampedSelection = -1;
    public override void _Process(double delta)
    {
        _localClock += delta;

        // QC MapVote_Selection stamps mv_select_lasttime[mv_selection] = time while a cell is selected, so the
        // moment it stops being the selection its highlight + color fade out over MV_FADETIME. Selection is fed
        // externally here, so detect the change and keep stamping the current selection each frame.
        int sel = Selection;
        if (sel != _lastStampedSelection)
            _lastStampedSelection = sel;
        if (sel >= 0 && sel < _selectLastTime.Length)
            _selectLastTime[sel] = CurrentTime();

        QueueRedraw();
    }

    private double CurrentTime()
    {
        // Prefer an explicit external clock, then the sim clock, then the local accumulator — but never let a
        // NaN/Inf value (Now is a public setter; the clock may be half-initialised) leak into the per-frame
        // draw math, where it would propagate to alpha/countdown and corrupt the whole panel.
        if (Now >= 0.0 && double.IsFinite(Now)) return Now;
        if (Api.Services is not null)
        {
            try
            {
                float t = Api.Clock.Time;
                if (float.IsFinite(t)) return t;
            }
            catch { /* services container present but clock not yet ready */ }
        }
        return _localClock;
    }

    // -------------------------------------------------------------------------------------------------
    //  Behavior cvars (defaults registered into the shared store; HudConfig invokes this by reflection).
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>MapVote_Draw_Export</c> exports <c>hud_panel_mapvote_highlight_border</c> — the pixel
    /// thickness of the colored border drawn around the own-vote / selected cell. Registered into the SHARED
    /// store so console/menu edits change it live.</summary>
    public static void RegisterDefaults(VortexArena.Engine.Simulation.CvarService c)
    {
        const VortexArena.Common.Services.CvarFlags save = VortexArena.Common.Services.CvarFlags.Save;
        // Base ships hud_panel_mapvote_highlight_border "1" in every HUD skin (hud_luma.cfg etc.);
        // _hud_descriptions.cfg ships it empty (""). Match the shipped skin default of 1.
        c.Register("hud_panel_mapvote_highlight_border", "1", save);
    }

    /// <summary>QC <c>autocvar_hud_panel_mapvote_highlight_border</c> (live; clamped non-negative).</summary>
    private float HighlightBorder()
    {
        float b = GlobalF("hud_panel_mapvote_highlight_border", 1f);
        // A garbage cvar value (NaN/Inf, or an absurd width) would feed DrawRect/DrawBorderLines bad geometry
        // every frame; clamp to a sane, finite, non-negative pixel range.
        if (!float.IsFinite(b) || b < 0f) return 0f;
        return b > 64f ? 64f : b;
    }

    // -------------------------------------------------------------------------------------------------
    //  Data feed (QC: the mapvote net entity populating mv_entries/mv_votes/mv_pics/...).
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replace the candidate set (QC: the <c>mapvote</c> net entity populating the <c>mv_*</c> arrays).
    /// </summary>
    public void SetVote(IEnumerable<Candidate>? candidates, double timeout, bool gametypeVote = false, bool abstain = false)
    {
        _candidates.Clear();
        if (candidates is not null)
            foreach (Candidate cand in candidates)
            {
                if (_candidates.Count >= MaxCandidates) break;
                _candidates.Add(cand);
            }
        Timeout = timeout;
        GametypeVote = gametypeVote;
        Abstain = abstain;
        Winner = 0;
        WinnerTime = 0.0;
        // Reset the per-cell fade state for the new ballot (QC zeroes mv_select_lasttime / the reduce + suggester
        // caches when a fresh mapvote entity arrives).
        System.Array.Clear(_selectLastTime, 0, _selectLastTime.Length);
        _top2Time = 0.0;
        _suggesterCache = "";
        _suggesterCacheTime = 0.0;
        _lastStampedSelection = -1;
        Active = _candidates.Count > 0;
        QueueRedraw();
    }

    /// <summary>QC <c>MapVote_TouchMask</c> reduce: mark the mid-vote moment the ballot was cut to the top
    /// options so the now-unavailable candidates fade away (QC <c>mv_top2_time</c> / <c>mv_top2_alpha</c>)
    /// instead of snapping to the flat 0.2 dim. Pass the per-option availability for the reduced ballot.</summary>
    public void SetReduce(IReadOnlyList<bool> available)
    {
        if (available is null) return;
        for (int i = 0; i < _candidates.Count && i < available.Count; i++)
            _candidates[i] = _candidates[i].WithAvailable(available[i]);
        _top2Time = CurrentTime();
        QueueRedraw();
    }

    /// <summary>Update the live vote counts in place (QC <c>MapVote_UpdateVotes</c>: incremental
    /// <c>mv_votes</c> updates; a count of -1 marks the option unavailable).</summary>
    public void SetVotes(IReadOnlyList<int> votes)
    {
        if (votes is null) return;
        for (int i = 0; i < _candidates.Count && i < votes.Count; i++)
        {
            int v = votes[i];
            // QC MapVote_UpdateVotes: mv_votes[i] == -1 ⇔ !(mv_flags[i] & GTV_AVAILABLE); >= 0 ⇔ available.
            _candidates[i] = _candidates[i].WithVotes(v).WithAvailable(v >= 0);
        }
        QueueRedraw();
    }

    /// <summary>Update the per-option availability mask (QC <c>MapVote_UpdateMask</c> / the top-2 reveal). If any
    /// option newly loses availability this stamps the reduce clock so it fades (QC <c>mv_top2_alpha</c>).</summary>
    public void SetAvailability(IReadOnlyList<bool> available)
    {
        if (available is null) return;
        bool newlyRemoved = false;
        for (int i = 0; i < _candidates.Count && i < available.Count; i++)
        {
            if (_candidates[i].Available && !available[i]) newlyRemoved = true;
            _candidates[i] = _candidates[i].WithAvailable(available[i]);
        }
        if (newlyRemoved && _top2Time <= 0.0) _top2Time = CurrentTime();
        QueueRedraw();
    }

    /// <summary>Reveal the winner (QC <c>sf &amp; BIT(3)</c>: <c>mv_winner = ReadByte(); mv_winner_time = time</c>).
    /// <paramref name="winner1Based"/> is 1-based (0 = clear).</summary>
    public void SetWinner(int winner1Based)
    {
        Winner = winner1Based;
        WinnerTime = winner1Based > 0 ? CurrentTime() : 0.0;
        QueueRedraw();
    }

    // -------------------------------------------------------------------------------------------------
    //  Draw (QC MapVote_Draw)
    // -------------------------------------------------------------------------------------------------

    protected override void DrawPanel()
    {
        if (!Active || _candidates.Count == 0) return;

        DrawBackground();

        double now = CurrentTime();
        float pad = Cfg.Padding;
        float w = Size2.X - pad * 2f;
        if (w <= 0f) return;

        // --- title (QC: bold, hud_fontsize*2, hidden once a winner is shown) ---
        float y = pad;
        int titleSize = (int)Mathf.Clamp(Size2.Y * 0.06f, 16f, 34f);
        if (Winner == 0)
        {
            string title = GametypeVote ? "Decide the gametype" : "Vote for a map";
            DrawTextCentered(new Vector2(0f, y), Size2.X, title, FgColor, titleSize);
            y += titleSize + Size2.Y * 0.012f;

            // QC: in a gametype vote that already chose a map, show it as a subtitle (hud_fontsize*1.5).
            if (!string.IsNullOrEmpty(ChosenMap))
            {
                int chosenSize = (int)Mathf.Clamp(titleSize * 0.75f, 12f, 26f);
                DrawTextCentered(new Vector2(0f, y), Size2.X, ChosenMap, FgColor, chosenSize);
                y += chosenSize + Size2.Y * 0.008f;
            }
        }

        // --- countdown / winner line (QC: green '0 1 0', hud_fontsize*1.5) ---
        int subSize = (int)Mathf.Clamp(Size2.Y * 0.045f, 14f, 26f);
        string sub;
        if (Winner > 0 && Winner <= _candidates.Count)
            sub = StripName(_candidates[Winner - 1].Name);
        else
        {
            // QC: i = ceil(max(1, mv_timeout - time)); never shows 0 even for a frame. Timeout is a public
            // setter, so guard against a NaN/Inf remaining-time producing a garbage CeilToInt; clamp to a sane
            // upper bound so the readout can't show an absurd value.
            float remaining = (float)(Timeout - now);
            if (!float.IsFinite(remaining)) remaining = 1f;
            remaining = Mathf.Clamp(remaining, 1f, 359999f); // up to ~99h59m59s
            int secs = Mathf.CeilToInt(remaining);
            sub = CountSeconds(secs);
        }
        DrawTextCentered(new Vector2(0f, y), Size2.X, sub, new Color(0.3f, 1f, 0.3f, LiveFgAlpha), subSize);
        y += subSize + Size2.Y * 0.02f;

        // --- the candidate grid (QC HUD_GetTableSize_BestItemAR: pick the column count that best fits) ---
        int n = _candidates.Count;
        float gridTop = y;
        // Reserve a row at the bottom for the abstain "Don't care" line (QC abstain_spacing).
        int abstainSize = (int)Mathf.Clamp(Size2.Y * 0.04f, 12f, 22f);
        float abstainReserve = Abstain ? abstainSize + Size2.Y * 0.02f : 0f;
        float gridH = Size2.Y - pad - gridTop - abstainReserve;
        if (gridH <= 0f) return;

        // QC item_aspect: gametype items are wider (3:1) than map items (5:3) to fit the description text.
        float itemAspect = GametypeVote ? 3f / 1f : 5f / 3f;
        int columns = BestColumns(n, w, gridH, itemAspect);
        Columns = columns; // QC mv_columns: expose the live layout to the input handler's up/down navigation.
        int rows = Mathf.CeilToInt(n / (float)columns);

        float cellW = w / columns;
        float cellH = gridH / rows;

        // Cache the grid geometry so a mouse click can hit-test the drawn cells (QC mv_mouse_selection).
        _gridOrigin = new Vector2(pad, gridTop);
        _cellSize = new Vector2(cellW, cellH);
        _gridColumns = columns;

        // QC mv_winner_alpha = max(0.2, 1 - sqrt(max(0, time - mv_winner_time))): losers fade back on reveal.
        float winnerAlpha = 1f;
        if (Winner > 0)
        {
            double since = WinnerTime > 0.0 && double.IsFinite(WinnerTime) ? now - WinnerTime : 0.0;
            float sinceF = (float)since;
            if (!float.IsFinite(sinceF) || sinceF < 0f) sinceF = 0f;
            winnerAlpha = Mathf.Max(0.2f, 1f - Mathf.Sqrt(sinceF));
        }

        // QC most_votes: when tie_winner == -2 the leader(s) get the ^5 tint.
        int mostVotes = -1;
        if (TieWinner == -2)
            for (int i = 0; i < n; i++)
                if (_candidates[i].Votes > mostVotes) mostVotes = _candidates[i].Votes;

        for (int i = 0; i < n; i++)
        {
            int col = i % columns;
            int row = i / columns;
            var cellPos = new Vector2(pad + col * cellW, gridTop + row * cellH);
            DrawCandidate(cellPos, new Vector2(cellW, cellH), i, winnerAlpha, mostVotes);
        }

        // --- abstain "Don't care" line (QC MapVote_DrawAbstain), centered below the grid ---
        if (Abstain)
        {
            int abstainId = n; // QC: abstain is the (mv_num_maps)th option.
            int abstainVotes = -1; // The abstain count isn't part of the candidate list; show name only.
            float abstainAlpha = (Winner > 0 ? winnerAlpha : 1f) * LiveFgAlpha;
            Color rgb = AbstainColor(abstainId, abstainAlpha);
            string label = FormatMapItem(abstainId, "Don't care", abstainVotes, false, -1);
            DrawTextCentered(new Vector2(pad, Size2.Y - pad - abstainSize), w, label, rgb, abstainSize);
        }

        // --- "Suggested by: <name>" line (QC MapVote_DrawSuggester), centered at the very bottom ---
        DrawSuggester(pad, w, abstainSize);
    }

    /// <summary>
    /// QC <c>MapVote_DrawSuggester</c>: show who suggested the winning (or currently-selected) map, with a
    /// cache that fades the line out over <see cref="FadeTime"/> when the selection leaves a suggested map
    /// (so it eases away instead of snapping). Drawn only when <see cref="Detail"/> reveals suggesters and a
    /// suggester string is available.
    /// </summary>
    private void DrawSuggester(float pad, float w, int size)
    {
        // QC: id = mv_winner ? mv_winner - 1 : mv_selection.
        int id = Winner > 0 ? Winner - 1 : Selection;

        float theAlpha = 0f;
        if (id >= 0 && id < _candidates.Count && _candidates[id].Available)
        {
            string sug = _candidates[id].Suggester;
            if (string.IsNullOrEmpty(_suggesterCache) && string.IsNullOrEmpty(sug))
                return; // nothing to show and nothing cached
            if (!string.IsNullOrEmpty(sug)) // refresh the cache to the current suggester, full alpha
            {
                _suggesterCache = sug;
                _suggesterCacheTime = CurrentTime();
                theAlpha = 1f;
            }
        }

        // QC: a cached-but-no-longer-current suggester fades out over MV_FADETIME, then clears.
        if (!string.IsNullOrEmpty(_suggesterCache) && theAlpha != 1f)
        {
            float frac = (float)((CurrentTime() - _suggesterCacheTime) / FadeTime);
            if (!float.IsFinite(frac) || frac >= 1f) { _suggesterCache = ""; return; }
            theAlpha = 1f - Mathf.Sqrt(frac < 0f ? 0f : frac);
        }

        if (theAlpha <= 0f || string.IsNullOrEmpty(_suggesterCache)) return;

        string label = $"Suggested by: {StripName(_suggesterCache)}";
        var color = new Color(1f, 1f, 1f, theAlpha * LiveFgAlpha);
        // Sit just above the bottom padding (below the abstain row when present).
        float yBottom = Size2.Y - pad - (Abstain ? size + Size2.Y * 0.02f + size : 0f) - size;
        if (yBottom < 0f) yBottom = Size2.Y - pad - size;
        DrawColorCoded(new Vector2(pad, yBottom), w, label, color, size);
    }

    /// <summary>
    /// Draw one candidate cell: the level-shot (QC <c>MapVote_DrawMapPicture</c>), the formatted label
    /// (QC <c>MapVote_FormatMapItem</c>), and the own-vote / selection highlight (QC borderlines + fill). For a
    /// gametype vote it also draws the multi-line description.
    /// </summary>
    private void DrawCandidate(Vector2 pos, Vector2 size, int id, float winnerAlpha, int mostVotes)
    {
        Candidate c = _candidates[id];

        // QC rect_margin = hud_fontsize.y * 0.5, then inset by the highlight border on every side.
        float rectMargin = Mathf.Clamp(size.Y * 0.06f, 2f, 8f);
        float hb = HighlightBorder();
        float inset = rectMargin + hb;
        var inner = new Rect2(pos.X + inset, pos.Y + inset,
            Mathf.Max(1f, size.X - inset * 2f), Mathf.Max(1f, size.Y - inset * 2f));

        // QC alpha: dim unavailable options (top-2 reveal), dim losers on winner reveal.
        float a;
        if (!c.Available)
        {
            // QC: !(mv_flags[id] & GTV_AVAILABLE) && mv_top2_alpha ⇒ fade with mv_top2_alpha; else flat 0.2.
            float top2 = Top2Alpha(CurrentTime());
            a = top2 > 0f ? top2 : 0.2f;
        }
        else if (Winner > 0)
            a = (Winner - 1 == id) ? 1f : winnerAlpha;
        else
            a = 1f;
        a *= LiveFgAlpha;

        Color rgb = CandidateColor(id, a);

        // QC highlight: own vote => filled tint + colored borderlines; selected/recently-selected => soft fill.
        var rectFill = new Rect2(pos.X + rectMargin, pos.Y + rectMargin,
            Mathf.Max(1f, size.X - rectMargin * 2f), Mathf.Max(1f, size.Y - rectMargin * 2f));
        if (Winner == 0 && c.Available)
        {
            if (id == OwnVote)
            {
                DrawRect(rectFill, new Color(rgb.R, rgb.G, rgb.B, 0.1f * a));
                DrawBorderLines(rectFill, new Color(rgb.R, rgb.G, rgb.B, a), hb);
            }
            else
            {
                // QC: drawfill('1 1 1', 0.1 * (1 - sqrt(time_frac)) * panel_fg_alpha) — the selection-highlight
                // box stays solid for the selected cell (time_frac = 0) and fades out over MV_FADETIME after.
                float frac = id == Selection ? 0f : SelectFraction(id);
                if (frac < 1f)
                    DrawRect(rectFill, new Color(1f, 1f, 1f, 0.1f * (1f - Mathf.Sqrt(frac)) * LiveFgAlpha));
            }
        }
        else if (Winner > 0 && Winner - 1 == id)
        {
            // Frame the winning cell so the reveal is obvious.
            DrawRect(rectFill, new Color(rgb.R, rgb.G, rgb.B, 0.18f * a));
            DrawBorderLines(rectFill, new Color(rgb.R, rgb.G, rgb.B, a), Mathf.Max(hb, 2f));
        }

        // QC label: "N. <name> (M votes)" colored by the candidate rgb; reserve its row at the cell bottom.
        int labelSize = (int)Mathf.Clamp(inner.Size.Y * 0.16f, 9f, 20f);
        float labelH = labelSize + 2f;

        // Description (gametype votes only) sits between the image and the label; reserve its band.
        bool hasDesc = GametypeVote && !string.IsNullOrEmpty(c.Description);
        int descSize = (int)Mathf.Clamp(labelSize * 0.82f, 8f, 16f);

        // Level-shot / gametype icon. QC fits the image to the cell minus the label row; keep its aspect.
        float imgAreaH = inner.Size.Y - labelH;
        if (imgAreaH > 4f)
        {
            // QC: map items use img_ar = 4/3 (MapVote_DrawMapItem); gametype icons are square
            // ('3 3 0' * gtv_text_size.x, clamped to '1 1 0' * maxh in GameTypeVote_DrawGameTypeItem).
            float imgAspect = GametypeVote ? 1f : 4f / 3f;
            Rect2 imgBox = FitAspect(new Rect2(inner.Position, new Vector2(inner.Size.X, imgAreaH)), imgAspect);
            DrawMapPicture(c.Pic, imgBox, a);

            // Gametype description: a couple of centered lines under the icon (QC getWrappedLine layout).
            if (hasDesc)
                DrawDescription(c.Description, new Rect2(inner.Position.X, imgBox.Position.Y + imgBox.Size.Y,
                    inner.Size.X, Mathf.Max(0f, inner.Position.Y + imgAreaH - (imgBox.Position.Y + imgBox.Size.Y))),
                    descSize, a);
        }

        string label = FormatMapItem(id, c.Data, c.Votes, c.Available, mostVotes);
        Color labelColor = new(rgb.R, rgb.G, rgb.B, a);
        DrawColorCoded(new Vector2(inner.Position.X, inner.Position.Y + inner.Size.Y - labelH),
            inner.Size.X, label, labelColor, labelSize);
    }

    // -------------------------------------------------------------------------------------------------
    //  QC MapVote_DrawMapPicture: the level-shot with the nopreview_map → swatch fallback.
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>MapVote_DrawMapPicture</c>: draw the candidate's level-shot. Empty pic → a grey fill
    /// (QC <c>drawfill('0.5 0.5 0.5')</c>); a pic that fails to resolve → the shipped <c>nopreview_map</c>
    /// art; finally a colored swatch so the cell is never blank.</summary>
    private void DrawMapPicture(string pic, Rect2 box, float alpha)
    {
        var white = new Color(1f, 1f, 1f, alpha);

        if (!string.IsNullOrEmpty(pic))
        {
            // Level-shot by map pic name (e.g. "maps/stormkeep") or gametype icon — host VfsResolver decodes it.
            Texture2D? tex = TextureCache.Get(pic);
            if (tex is not null) { DrawTextureRect(tex, box, false, white); return; }

            // QC: a known-but-not-yet-downloaded preview draws the "nopreview_map" placeholder art.
            if (DrawSkinPicFirst(box, white,
                    $"gfx/hud/{HudSkin.SkinName}/nopreview_map",
                    "gfx/hud/default/nopreview_map",
                    $"gfx/menu/{HudSkin.SkinName}/nopreview_map",
                    "gfx/menu/luma/nopreview_map",
                    "gfx/menu/default/nopreview_map"))
                return;
        }

        // Empty pic or no art at all: a grey backing + a small colored corner swatch so it reads as a cell.
        DrawRect(box, new Color(0.5f, 0.5f, 0.5f, 0.7f * alpha));
    }

    // -------------------------------------------------------------------------------------------------
    //  QC MapVote_FormatMapItem + count_seconds (label formatting)
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>MapVote_FormatMapItem</c>: <c>"N. " + name</c> plus, when <see cref="Detail"/> and the
    /// option is available, the <c>" (M vote[s])"</c> suffix — <c>^5</c>-tinted when this option is the
    /// (potential) tie winner. The leading number is 1-based.</summary>
    private string FormatMapItem(int id, string name, int count, bool available, int mostVotes)
    {
        string pre = $"{id + 1}. ";
        string post = "";
        if (Detail)
        {
            if (count == 1)
                post = " (1 vote)";
            else if (count >= 0 && available)
                post = $" ({count} votes)";

            if (post != "" && available &&
                (TieWinner == id || (TieWinner == -2 && count == mostVotes)))
                post = "^5" + post; // QC: tint the leader's count cyan.
        }
        return pre + StripName(name) + post;
    }

    /// <summary>Strip any inline color codes from a map/gametype name for the title/label (QC names are plain,
    /// but a server could embed codes; the label colors the whole run by the candidate rgb).</summary>
    private static string StripName(string s) => HudText.Strip(s);

    /// <summary>QC <c>count_seconds</c>: a localized "N second(s)" string (singular at 1).</summary>
    private static string CountSeconds(int seconds)
        => seconds == 1 ? "1 second" : $"{seconds} seconds";

    // -------------------------------------------------------------------------------------------------
    //  Description wrapping (gametype votes) — a compact stand-in for QC getWrappedLine.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Draw up to a few centered, wrapped description lines inside <paramref name="area"/>
    /// (QC <c>getWrappedLine</c> loop in <c>GameTypeVote_DrawGameTypeItem</c>).</summary>
    private void DrawDescription(string desc, Rect2 area, int size, float alpha)
    {
        if (string.IsNullOrEmpty(desc) || area.Size.Y < size || area.Size.X < 4f) return;
        var color = new Color(1f, 1f, 1f, alpha);
        float lineH = size + 1f;
        int maxLines = Mathf.Max(1, (int)(area.Size.Y / lineH));

        // Only ever process a bounded prefix: the description is server-controlled, and we draw at most a few
        // lines anyway, so a pathologically long string (e.g. megabytes of newlines) can't make the per-frame
        // split + wrap loop expensive.
        if (desc.Length > 1024) desc = desc.Substring(0, 1024);

        // Split on explicit newlines first (QC tokenizebyseparator(thelabel, "\n")), then word-wrap each.
        var lines = new List<string>();
        foreach (string raw in desc.Replace("\r", "").Split('\n'))
        {
            WrapInto(raw.Trim(), area.Size.X, size, lines, maxLines);
            if (lines.Count >= maxLines) break;
        }

        for (int i = 0; i < lines.Count && i < maxLines; i++)
            DrawTextCentered(new Vector2(area.Position.X, area.Position.Y + i * lineH),
                area.Size.X, lines[i], color, size);
    }

    /// <summary>Greedy word-wrap (QC getWrappedLine stand-in): pack words up to <paramref name="maxWidth"/>.</summary>
    private static void WrapInto(string text, float maxWidth, int size, List<string> into, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return;
        string[] words = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        string cur = "";
        foreach (string word in words)
        {
            string trial = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length > 0 && MeasureText(trial, size) > maxWidth)
            {
                into.Add(cur);
                if (into.Count >= maxLines) return;
                cur = word;
            }
            else cur = trial;
        }
        if (cur.Length > 0 && into.Count < maxLines) into.Add(cur);
    }

    // -------------------------------------------------------------------------------------------------
    //  Geometry + color helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>Aspect-fit-center a content box inside <paramref name="cell"/> at <paramref name="aspect"/>
    /// (width/height) — the QC image-fit math in <c>MapVote_DrawMapItem</c>.</summary>
    private static Rect2 FitAspect(Rect2 cell, float aspect)
    {
        Vector2 pos = cell.Position, size = cell.Size;
        if (size.X / Mathf.Max(1f, size.Y) > aspect)
        {
            float ww = aspect * size.Y;
            pos.X += (size.X - ww) * 0.5f;
            size.X = ww;
        }
        else
        {
            float hh = size.X / Mathf.Max(0.0001f, aspect);
            pos.Y += (size.Y - hh) * 0.5f;
            size.Y = hh;
        }
        return new Rect2(pos, size);
    }

    /// <summary>QC <c>drawborderlines</c>: draw a hollow rectangle frame <paramref name="thickness"/> px wide
    /// around <paramref name="rect"/> (four filled edge quads, like the engine's border draw).</summary>
    private void DrawBorderLines(Rect2 rect, Color color, float thickness)
    {
        if (thickness <= 0f)
        {
            DrawRect(rect, color, filled: false, width: 1f);
            return;
        }
        float t = thickness;
        // top, bottom, left, right (corners doubled, matching the engine's 4-quad border).
        DrawRect(new Rect2(rect.Position.X - t, rect.Position.Y - t, rect.Size.X + 2f * t, t), color);
        DrawRect(new Rect2(rect.Position.X - t, rect.Position.Y + rect.Size.Y, rect.Size.X + 2f * t, t), color);
        DrawRect(new Rect2(rect.Position.X - t, rect.Position.Y, t, rect.Size.Y), color);
        DrawRect(new Rect2(rect.Position.X + rect.Size.X, rect.Position.Y, t, rect.Size.Y), color);
    }

    /// <summary>Draw a label with inline <c>^N</c>/<c>^x</c> color codes, centered within <paramref name="width"/>
    /// (QC <c>drawcolorcodedstring</c>); the base color tints the uncoded leading run + supplies the alpha.</summary>
    private void DrawColorCoded(Vector2 pos, float width, string text, Color baseColor, int size)
    {
        List<HudText.Run> runs = HudText.Parse(text, baseColor);
        if (runs.Count == 0) return;

        // If there are no embedded codes, a single centered draw keeps the label crisp.
        if (runs.Count == 1)
        {
            DrawTextCentered(pos, width, runs[0].Text, baseColor, size);
            return;
        }

        // Multiple runs: lay them out left-to-right, centered as a block.
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        float x = pos.X + (width - total) * 0.5f;
        foreach (HudText.Run r in runs)
        {
            DrawText(new Vector2(x, pos.Y), r.Text, r.Color, size);
            x += MeasureText(r.Text, size);
        }
    }

    /// <summary>
    /// Pick the column count that best fills the area at the target item aspect ratio — a compact stand-in for
    /// QC <c>HUD_GetTableSize_BestItemAR</c>: try every column count and keep the one whose resulting cell
    /// aspect is closest to <paramref name="itemAspect"/>.
    /// </summary>
    private static int BestColumns(int count, float areaW, float areaH, float itemAspect)
    {
        int best = 1;
        float bestErr = float.MaxValue;
        for (int cols = 1; cols <= count; cols++)
        {
            int rows = Mathf.CeilToInt(count / (float)cols);
            float cellAr = (areaW / cols) / (areaH / rows);
            float err = Mathf.Abs(cellAr - itemAspect);
            if (err < bestErr) { bestErr = err; best = cols; }
        }
        return best;
    }

    /// <summary>QC <c>MapVote_RGB</c>: white for unavailable options, green for the own vote, yellow for the
    /// selection, else a fading-to-yellow base. The alpha is supplied by the caller.</summary>
    private Color CandidateColor(int id, float alpha)
    {
        if (!_candidates[id].Available)
            return new Color(1f, 1f, 1f, alpha);      // QC '1 1 1'
        if (id == OwnVote)
            return new Color(0f, 1f, 0f, alpha);       // QC '0 1 0'
        if (id == Selection)
            return new Color(1f, 1f, 0f, alpha);       // QC '1 1 0'
        // QC: '1 1 0' + eZ * (time_frac >= 1 ? 1 : sqrt(time_frac)) — the blue channel ramps in as the cell
        // settles from a recent selection (yellow) back to the resting cream over MV_FADETIME.
        float blue = SelectFraction(id) >= 1f ? 1f : Mathf.Sqrt(SelectFraction(id));
        return new Color(1f, 1f, blue, alpha);
    }

    /// <summary>QC <c>(time - mv_select_lasttime[id]) / MV_FADETIME</c>, clamped to [0,1] (1 = fully settled).</summary>
    private float SelectFraction(int id)
    {
        if (id < 0 || id >= _selectLastTime.Length) return 1f;
        double last = _selectLastTime[id];
        if (last <= 0.0) return 1f;
        float frac = (float)((CurrentTime() - last) / FadeTime);
        if (!float.IsFinite(frac) || frac >= 1f) return 1f;
        return frac < 0f ? 0f : frac;
    }

    /// <summary>QC <c>MapVote_RGB</c> for the abstain option id (no availability flag, so it reads as own/sel).</summary>
    private Color AbstainColor(int abstainId, float alpha)
    {
        if (abstainId == OwnVote) return new Color(0f, 1f, 0f, alpha);
        if (abstainId == Selection) return new Color(1f, 1f, 0f, alpha);
        return new Color(1f, 1f, 0.55f, alpha);
    }

    // =====================================================================================
    //  Input (QC client/mapvoting.qc MapVote_InputEvent + MapVote_Move* + MapVote_SendChoice)
    // =====================================================================================

    /// <summary>Set by the host (NetGame) to forward a chosen 0-based ballot index to the server as
    /// <c>impulse N+1</c> — the port's <c>MapVote_SendChoice</c> (QC <c>localcmd("impulse ", index+1)</c>).</summary>
    public System.Action<int>? CastChoice { get; set; }

    /// <summary>True when this option index is available (QC <c>mv_flags[i] &amp; GTV_AVAILABLE</c>).</summary>
    private bool IsAvailable(int i) => i >= 0 && i < _candidates.Count && _candidates[i].Available;

    // QC MapVote_MoveLeft/Right/Up/Down: move the selection, skipping unavailable cells (except the own vote).
    private int MoveLeft(int pos)
    {
        int n = MapCount;
        if (n <= 0) return -1;
        int imp = pos < 0 ? n - 1 : (pos < 1 ? n - 1 : pos - 1);
        if (!IsAvailable(imp) && imp != OwnVote) imp = MoveLeft(imp);
        return imp;
    }
    private int MoveRight(int pos)
    {
        int n = MapCount;
        if (n <= 0) return -1;
        int imp = pos < 0 ? 0 : (pos >= n - 1 ? 0 : pos + 1);
        if (!IsAvailable(imp) && imp != OwnVote) imp = MoveRight(imp);
        return imp;
    }
    private int MoveUp(int pos)
    {
        int n = MapCount;
        if (n <= 0) return -1;
        int cols = System.Math.Max(1, Columns);
        int imp;
        if (pos < 0) imp = n - 1;
        else
        {
            imp = pos - cols;
            if (imp < 0)
            {
                int rows = Mathf.CeilToInt(n / (float)cols);
                imp = imp == -cols ? cols * rows - 1 : imp + cols * rows - 1; // pos == 0 wraps to last cell
            }
        }
        if (imp >= n) imp = n - 1; // a ragged last row can leave imp past the cells
        if (!IsAvailable(imp) && imp != OwnVote) imp = MoveUp(imp);
        return imp;
    }
    private int MoveDown(int pos)
    {
        int n = MapCount;
        if (n <= 0) return -1;
        int cols = System.Math.Max(1, Columns);
        int imp;
        if (pos < 0) imp = 0;
        else
        {
            imp = pos + cols;
            if (imp >= n)
            {
                int col = imp % cols;
                imp = col == cols - 1 ? 0 : col + 1; // wrap to the next column's top
            }
        }
        if (imp >= n) imp = 0;
        if (!IsAvailable(imp) && imp != OwnVote) imp = MoveDown(imp);
        return imp;
    }

    /// <summary>QC <c>MapVote_SendChoice(index)</c>: cast the 0-based ballot option (forwarded as impulse N+1).</summary>
    private void SendChoice(int index)
    {
        if (index < 0 || index >= MapCount) return;
        CastChoice?.Invoke(index);
    }

    private int _firstDigit;

    /// <summary>
    /// QC <c>MapVote_InputEvent</c> (client/mapvoting.qc:930): move the selection (arrows), cast the selection
    /// (enter/space), cast by number (digits, Ctrl+digit for two-digit indices), or cast/select by mouse click.
    /// Returns true if the event was consumed (so the host can mark it handled). No-op once a winner is shown or
    /// while the panel isn't active (QC <c>!mv_active</c>).
    /// </summary>
    public bool HandleVoteKey(Key key, bool ctrl)
    {
        if (!Active || MapCount <= 0) return false;
        if (Winner > 0) return true; // a winner is being revealed — swallow but do nothing (QC `if (mv_winner) return true`).

        switch (key)
        {
            case Key.Right: Selection = MoveRight(Selection); return true;
            case Key.Left:  Selection = MoveLeft(Selection);  return true;
            case Key.Down:  Selection = MoveDown(Selection);  return true;
            case Key.Up:    Selection = MoveUp(Selection);    return true;
            case Key.Enter:
            case Key.KpEnter:
            case Key.Space:
                if (Selection >= 0) SendChoice(Selection);
                return true;
        }

        // QC digit cast: '1'..'9' → 1..9, '0' → 10. Keypad digits map the same.
        int imp = DigitImpulse(key);
        if (imp == 0) return false;

        // QC Ctrl+digit two-digit entry: first digit is buffered, second completes the index.
        if (ctrl)
        {
            if (_firstDigit == 0) { _firstDigit = imp % 10; return true; }
            imp = _firstDigit * 10 + (imp % 10);
            _firstDigit = 0;
        }

        if (imp <= MapCount)
            SendChoice(imp - 1); // QC localcmd impulse imp (1-based) → 0-based choice
        return true;
    }

    /// <summary>QC the mouse path in <c>MapVote_InputEvent</c>: a left-click on the hovered cell casts it.</summary>
    public bool HandleVoteClick(Vector2 localPos)
    {
        if (!Active || MapCount <= 0 || Winner > 0) return false;
        int hit = CellAt(localPos);
        if (hit < 0) return false;
        Selection = hit;
        SendChoice(hit);
        return true;
    }

    /// <summary>QC <c>mv_mouse_selection</c>: mouse hover moves the selection (highlight) without casting.</summary>
    public void HandleVoteHover(Vector2 localPos)
    {
        if (!Active || MapCount <= 0 || Winner > 0) return;
        int hit = CellAt(localPos);
        if (hit >= 0) Selection = hit;
    }

    /// <summary>Map a digit/keypad key to its 1-based ballot impulse (QC the '1'..'0' / K_KP_* cases).</summary>
    private static int DigitImpulse(Key key) => key switch
    {
        Key.Key1 or Key.Kp1 => 1,
        Key.Key2 or Key.Kp2 => 2,
        Key.Key3 or Key.Kp3 => 3,
        Key.Key4 or Key.Kp4 => 4,
        Key.Key5 or Key.Kp5 => 5,
        Key.Key6 or Key.Kp6 => 6,
        Key.Key7 or Key.Kp7 => 7,
        Key.Key8 or Key.Kp8 => 8,
        Key.Key9 or Key.Kp9 => 9,
        Key.Key0 or Key.Kp0 => 10,
        _ => 0,
    };

    // Cached grid geometry from the last DrawPanel so a click can be hit-tested against the drawn cells.
    private Vector2 _gridOrigin;
    private Vector2 _cellSize;
    private int _gridColumns = 1;

    /// <summary>Hit-test a panel-local point against the drawn candidate grid; returns the 0-based cell index or -1.</summary>
    private int CellAt(Vector2 localPos)
    {
        if (_cellSize.X <= 0f || _cellSize.Y <= 0f) return -1;
        float rx = localPos.X - _gridOrigin.X;
        float ry = localPos.Y - _gridOrigin.Y;
        if (rx < 0f || ry < 0f) return -1;
        int col = (int)(rx / _cellSize.X);
        int row = (int)(ry / _cellSize.Y);
        if (col < 0 || col >= _gridColumns) return -1;
        int idx = row * _gridColumns + col;
        return idx >= 0 && idx < MapCount ? idx : -1;
    }
}
