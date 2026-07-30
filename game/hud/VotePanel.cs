using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Vote panel — port of Base/.../qcsrc/client/hud/panel/vote.qc (HUD panel #9). While a callvote is active
/// (<c>vote_active</c>) the QC panel draws the called-vote text ("A vote has been called for: …") and two
/// progress bars — Yes and No — each filled to <c>count / vote_needed</c> using the skin art
/// (<c>voteprogress_back</c> + <c>voteprogress_voted</c> highlight + <c>voteprogress_prog</c> clipped to the
/// count fraction), with the bound keys for <c>vyes</c>/<c>vno</c>; it fades the whole panel in/out over 0.5s
/// when the vote starts/ends (<c>vote_alpha = bound(0,(time-vote_change)*2,1)</c>) and dims it once the local
/// player has voted (<c>vote_highlighted</c> → <c>hud_panel_vote_alreadyvoted_alpha</c>).
///
/// FAITHFUL LAYOUT: the QC forces a 3:1 aspect; if the panel is too thin it falls back to 3:2 and draws the Yes
/// bar on top of the No bar (<c>yes_on_top</c>) instead of side-by-side. This port reproduces both layouts, the
/// per-side clip-to-fraction progress fill (via <see cref="DrawSkinPicFraction"/>), the voted-side highlight art,
/// and the F1/F2 key hints. A drawn-bar fallback is used whenever the voteprogress art fails to resolve so the
/// counts are never invisible.
///
/// The vote state is a server/net concern, so the net layer drives it through the settable members here
/// (<see cref="Active"/>, <see cref="CalledVote"/>, <see cref="YesCount"/>/<see cref="NoCount"/>/
/// <see cref="Needed"/>, <see cref="Highlighted"/>, and the optional <see cref="EndTime"/> for the
/// time-remaining readout) — the same injection model as <see cref="RaceTimerPanel"/>. The fade is computed
/// locally from when <see cref="Active"/> last changed.
/// </summary>
public partial class VotePanel : HudPanel
{
    /// <summary>QC <c>vote_active</c>: a callvote is currently running. Drives the fade in/out.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value == _active) return;
            _active = value;
            _voteChange = CurrentTime();
        }
    }
    private bool _active;

    /// <summary>QC <c>vote_called_vote</c>: the description of the called vote (may carry <c>^N</c> colors).</summary>
    public string CalledVote { get; set; } = "";

    /// <summary>QC <c>vote_yescount</c>.</summary>
    public int YesCount { get; set; }

    /// <summary>QC <c>vote_nocount</c>.</summary>
    public int NoCount { get; set; }

    /// <summary>QC <c>vote_needed</c>: votes required to pass (bar denominator).</summary>
    public int Needed { get; set; }

    /// <summary>
    /// QC <c>vote_highlighted</c>: the local player's choice — +1 = voted Yes, -1 = voted No, 0 = not voted.
    /// A non-zero value dims the whole panel (already-voted alpha) and highlights the chosen side.
    /// </summary>
    public int Highlighted { get; set; }

    /// <summary>QC <c>hud_panel_vote_alreadyvoted_alpha</c>: panel alpha multiplier once you've voted. Default
    /// from the active (luma) skin (hud_luma.cfg:201 = 0.7).</summary>
    public float AlreadyVotedAlpha { get; set; } = 0.7f;

    /// <summary>The keybind hint for Yes (QC <c>getcommandkey_forcename(_("Yes"), "vyes")</c>).</summary>
    public string YesKey { get; set; } = "F1";

    /// <summary>The keybind hint for No (QC <c>getcommandkey_forcename(_("No"), "vno")</c>).</summary>
    public string NoKey { get; set; } = "F2";

    /// <summary>
    /// Optional absolute time (sim seconds) the vote expires (QC the server's vote timeout). When &gt; 0 and
    /// later than now, a small "N seconds left" readout is drawn under the title. ≤ 0 hides it. This is a port
    /// extension faithful to the contract §6 "time remaining" item — stock draws no countdown in this panel.
    /// </summary>
    public double EndTime { get; set; } = -1.0;

    /// <summary>
    /// QC the uid2name dialog override: when set, the panel re-uses the larger centered position + asks the
    /// privacy question instead of showing the called vote. The net/menu layer toggles this. Optional; the
    /// stock client-side uid2name dialog is otherwise deprecated.
    /// </summary>
    public bool Uid2NameDialog { get; set; }

    /// <summary>The panel only draws while live (dynamic), but its fade requires per-frame repaints.</summary>
    public override bool IsDynamic => true;

    private double _voteChange = -1.0;
    private double _localClock;

    // ---- behaviour cvars (registered into the shared store; QC HUD_Vote_Export) ----

    /// <summary>HudConfig invokes this by reflection to seed the panel's behaviour-cvar defaults. Layout/look
    /// defaults come from the luma table; this only owns the QC aesthetic tunables for the vote panel.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // QC HUD_Vote_Export writes hud_panel_vote_alreadyvoted_alpha into the skin; default from hud_luma.cfg:201.
        c.Register("hud_panel_vote_alreadyvoted_alpha", "0.7", save);
        // QC the dynamic-hud scale flag (port reads it as a behaviour cvar, like the other panels).
        c.Register("hud_panel_vote_dynamichud", "1", save);
    }

    public override void _Process(double delta)
    {
        _localClock += delta;
        if (NeedsRedraw())
            QueueRedraw();
    }

    private bool _wasAnimating = true;

    /// <summary>True while the vote is shown (active) or fading in/out — plus one final redraw on the transition
    /// to fully-idle so the last animated frame is cleared (3.2-3). The whole panel repaints every frame it is
    /// shown, exactly as QC's <c>HUD_Vote</c> runs each frame — so net-driven changes to the counts, highlight or
    /// called-vote text (auto-property setters that don't themselves queue a redraw) are always reflected.</summary>
    public override bool NeedsRedraw()
    {
        double now = CurrentTime();
        bool fading = _voteChange >= 0.0 && (now - _voteChange) < 0.5; // 0.5s fade window (×2 ⇒ saturates at 0.5)
        // Redraw every frame the panel is visible: while Active the counts/highlight/text can change at any time
        // (net setters), and during the fade-down (Active just went false) the alpha is still ramping.
        bool animating = Active || fading;
        if (animating)
        {
            _wasAnimating = true;
            return true;
        }
        if (_wasAnimating)
        {
            _wasAnimating = false; // one last redraw to settle the final frame, then go quiet
            return true;
        }
        return false;
    }

    private double CurrentTime()
    {
        // Prefer the sim clock, but never let a partially-wired Api (non-null Services with a null Clock) throw
        // from the per-frame _Process/DrawPanel path — fall back to the monotonic local clock instead.
        if (Api.Services is not null)
        {
            try { return Api.Clock.Time; }
            catch { /* fall through to the local clock */ }
        }
        return _localClock;
    }

    /// <summary>Read the live alreadyvoted alpha — prefer the cvar (so menu/console edits take effect) but fall
    /// back to the injected <see cref="AlreadyVotedAlpha"/> property the net layer may set.</summary>
    private float LiveAlreadyVotedAlpha()
    {
        string s = MenuState_GetVoteAlpha();
        return string.IsNullOrWhiteSpace(s) ? AlreadyVotedAlpha
            : Mathf.Clamp(GlobalF("hud_panel_vote_alreadyvoted_alpha", AlreadyVotedAlpha), 0f, 1f);
    }

    private static string MenuState_GetVoteAlpha() => GlobalStr("hud_panel_vote_alreadyvoted_alpha");

    protected override void DrawPanel()
    {
        double now = CurrentTime();

        // QC vote_alpha: fade up while active, down once it ends (× 2 ⇒ a 0.5s ramp).
        float fade;
        if (_voteChange < 0.0)
            fade = Active ? 1f : 0f;
        else if (Active)
            fade = Mathf.Clamp((float)(now - _voteChange) * 2f, 0f, 1f);
        else
            fade = Mathf.Clamp(1f - (float)(now - _voteChange) * 2f, 0f, 1f);

        // QC: once you've voted the whole panel dims to alreadyvoted_alpha.
        float a = fade * (Highlighted != 0 ? LiveAlreadyVotedAlpha() : 1f);
        a *= LiveFgAlpha;            // honour hud_panel_fg_alpha + the HUD menu fade
        // Self-blank when there's nothing to show. NaN must not slip past this gate (NaN <= 0 is false), or the
        // whole panel would draw with a garbage alpha — clamp it out so the early-return holds.
        if (!(a > 0f) || !float.IsFinite(a)) return;
        a = Mathf.Clamp(a, 0f, 1f);

        DrawBackground();

        float pad = Cfg.Padding;     // QC panel_bg_padding (live), not the compile-time fallback
        float px = pad, py = pad;
        float mw = Mathf.Max(1f, Size2.X - pad * 2f);
        float mh = Mathf.Max(1f, Size2.Y - pad * 2f);

        // QC: force a 3:1 aspect; if the panel is thin force 3:2 and draw yes on top of no.
        float nw, nh;
        bool yesOnTop;
        if (mw / mh > 3f)
        {
            nw = 3f * mh; nh = mh;
            px += (mw - nw) * 0.5f;
            yesOnTop = false;
        }
        else if (mw / mh > 1.5f) // 3/2
        {
            nh = (1f / 3f) * mw; nw = mw;
            py += (mh - nh) * 0.5f;
            yesOnTop = false;
        }
        else
        {
            nh = (2f / 3f) * mw; nw = mw;
            py += (mh - nh) * 0.5f;
            yesOnTop = true;
        }
        float x = px, y = py;
        float w = nw, h = nh;

        // === title line (QC: in the top 2/8) ===
        string title = Uid2NameDialog
            ? "Allow servers to store and display your name?"
            : "A vote has been called for:";
        int titleSz = (int)Mathf.Clamp(h * (2f / 8f) * 0.85f, 9f, 26f);
        DrawTextAspect(new Vector2(x, y), w, h * (2f / 8f), title, new Color(1f, 1f, 1f, a), titleSz);

        // === the called-vote description (color-coded), under the title (QC 2/8 → ~3.75/8) ===
        string desc = string.IsNullOrEmpty(CalledVote) ? "" : CalledVote;
        int descSz = (int)Mathf.Clamp(h * (1f / 8f) * 0.95f, 9f, 28f);
        DrawColoredAspect(new Vector2(x, y + (2f / 8f) * h), w, (1.75f / 8f) * h, desc,
            new Color(1f, 1f, 1f, a), descSz);

        // Optional time-remaining readout (port extension), tucked into the top-right corner of the title band so
        // it never overlaps the centered title or the description row below it. Off by default (EndTime <= 0).
        if (Active && EndTime > 0.0 && EndTime > now)
        {
            int secs = (int)Mathf.Ceil((float)(EndTime - now));
            int tsz = (int)Mathf.Clamp(h * (1f / 8f) * 0.7f, 8f, 18f);
            string t = secs == 1 ? "1 second left" : $"{secs} seconds left";
            DrawTextRight(x + w, y, w, t, new Color(1f, 1f, 0.6f, a), tsz);
        }

        // === yes/no key + count headers (QC the "Yes (3)" / "No (2)" line at 4/8) ===
        float headerH = (yesOnTop ? 1.3f / 8f : 1.5f / 8f) * h;
        int hdrSz = (int)Mathf.Clamp(headerH * 0.85f, 8f, 22f);
        string yesHdr = $"^2{YesKey} ^7({YesCount})";
        string noHdr = $"^1{NoKey} ^7({NoCount})";
        // QC draws both headers on one line, yes in the left half and no in the right half (regardless of layout;
        // only the header-box height differs between yes_on_top and side-by-side, handled by headerH above).
        DrawColoredAspect(new Vector2(x, y + (4f / 8f) * h), w * 0.5f, headerH, yesHdr,
            new Color(1f, 1f, 1f, a), hdrSz);
        DrawColoredAspect(new Vector2(x + w * 0.5f, y + (4f / 8f) * h), w * 0.5f, headerH, noHdr,
            new Color(1f, 1f, 1f, a), hdrSz);

        // === the progress bars (QC voteprogress_back + voteprogress_voted + voteprogress_prog) ===
        // QC: the bars start at 5.2/8 of mySize.y and run to the bottom.
        float barsTop = y + (5.2f / 8f) * h;
        // QC gates the prog fill on `vote_yescount && vote_needed` — i.e. no fill at all until the denominator
        // (vote_needed) is known. When Needed <= 0 leave both fractions at 0 (don't let a single vote read 100%);
        // the denom guard also keeps the division safe.
        float yesFrac = Needed > 0 ? Mathf.Clamp(YesCount / (float)Needed, 0f, 1f) : 0f;
        float noFrac = Needed > 0 ? Mathf.Clamp(NoCount / (float)Needed, 0f, 1f) : 0f;
        var artMod = new Color(1f, 1f, 1f, a);

        if (yesOnTop)
        {
            // two stacked full-width bars (QC: yes on top, no underneath; each takes the full panel width).
            float gap = (h - (5.2f / 8f) * h) * 0.06f;
            float barH = ((h + py - barsTop) - gap) * 0.5f;
            var yesRect = new Rect2(x, barsTop, w, barH);
            var noRect = new Rect2(x, barsTop + barH + gap, w, barH);

            DrawVoteBar(yesRect, yesFrac, Highlighted == 1, fillFromRight: false, artMod,
                new Color(0.25f, 0.9f, 0.25f, a));
            DrawVoteBar(noRect, noFrac, Highlighted == -1, fillFromRight: false, artMod,
                new Color(0.9f, 0.25f, 0.2f, a));
        }
        else
        {
            // side-by-side bars (QC: split the width in half; both in one line). No fills from the outer edge
            // toward the center is not stock here (stock fills both left→right), but QC's `no` bar clips from the
            // right edge inward to mirror it. Keep the visual symmetry: yes fills left→right, no fills right→left.
            float barH = (h + py) - barsTop;
            float gap = w * 0.02f;
            float halfW = (w - gap) * 0.5f;
            var yesRect = new Rect2(x, barsTop, halfW, barH);
            var noRect = new Rect2(x + halfW + gap, barsTop, halfW, barH);

            DrawVoteBar(yesRect, yesFrac, Highlighted == 1, fillFromRight: false, artMod,
                new Color(0.25f, 0.9f, 0.25f, a));
            DrawVoteBar(noRect, noFrac, Highlighted == -1, fillFromRight: true, artMod,
                new Color(0.9f, 0.25f, 0.2f, a));
        }
    }

    /// <summary>
    /// Draw one vote progress bar: the <c>voteprogress_back</c> backdrop, the <c>voteprogress_voted</c> highlight
    /// (when this is the side you voted for), and the <c>voteprogress_prog</c> fill clipped to
    /// <paramref name="fraction"/>. Falls back to a drawn bar (<see cref="DrawBar"/>) + an outline highlight when
    /// the art is missing, so the counts are never invisible.
    /// </summary>
    private void DrawVoteBar(Rect2 rect, float fraction, bool voted, bool fillFromRight, Color artMod, Color fillFallback)
    {
        bool haveArt = DrawSkinPic("voteprogress_back", rect, artMod);
        if (haveArt)
        {
            if (voted)
                DrawSkinPic("voteprogress_voted", rect, artMod);
            if (fraction > 0f)
                DrawSkinPicFraction("voteprogress_prog", rect, artMod, fraction, fillFromRight);
        }
        else
        {
            // Drawn-primitive fallback (QC's bars become flat colored bars when the skin art is absent).
            if (fillFromRight)
            {
                // Mirror the fill so the "no" bar grows from the outer (right) edge inward (QC clips from the
                // right). Draw the track + a right-anchored fill ourselves (DrawBar only fills left→right).
                DrawRect(rect, new Color(0f, 0f, 0f, artMod.A * 0.35f));
                if (fraction > 0f)
                {
                    float fw = rect.Size.X * Mathf.Clamp(fraction, 0f, 1f);
                    DrawRect(new Rect2(rect.Position.X + rect.Size.X - fw, rect.Position.Y, fw, rect.Size.Y),
                        fillFallback);
                }
                DrawRect(rect, new Color(1f, 1f, 1f, artMod.A * 0.15f), filled: false, width: 1f);
            }
            else
            {
                DrawBar(rect, fraction, fillFallback);
            }
            if (voted)
                DrawRect(rect, new Color(1f, 1f, 1f, artMod.A * 0.6f), filled: false, width: 2f);
        }
    }

    /// <summary>
    /// Draw a skin pic clipped to a horizontal <paramref name="fraction"/> of <paramref name="rect"/> — the port
    /// stand-in for QC's <c>drawsetcliparea(...) ; drawpic_skin(...)</c>. Because the art is a horizontal bar, a
    /// clip to the leading fraction equals drawing the matching sub-region of the texture, which Godot's
    /// immediate-mode canvas does directly via <see cref="CanvasItem.DrawTextureRectRegion"/>. When
    /// <paramref name="fromRight"/> the visible slice is anchored to the right edge (the QC "no" bar fills inward
    /// from the right).
    /// </summary>
    private void DrawSkinPicFraction(string bareName, Rect2 rect, Color modulate, float fraction, bool fromRight)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f) return;
        Texture2D? tex = TextureCache.GetFirst(
            $"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");
        if (tex is null)
        {
            // last-ditch: a flat slice so the fill still reads even without the prog art.
            float fw0 = rect.Size.X * fraction;
            float fx0 = fromRight ? rect.Position.X + rect.Size.X - fw0 : rect.Position.X;
            DrawRect(new Rect2(fx0, rect.Position.Y, fw0, rect.Size.Y), modulate);
            return;
        }

        Vector2 ts = tex.GetSize();
        float visW = rect.Size.X * fraction;
        float dstX = fromRight ? rect.Position.X + rect.Size.X - visW : rect.Position.X;
        var dstRect = new Rect2(dstX, rect.Position.Y, visW, rect.Size.Y);

        float srcW = ts.X * fraction;
        float srcX = fromRight ? ts.X - srcW : 0f;
        var srcRect = new Rect2(srcX, 0f, srcW, ts.Y);

        DrawTextureRectRegion(tex, dstRect, srcRect, modulate);
    }

    /// <summary>Draw plain text fitted within an aspect box (QC <c>drawstring_aspect</c>): centered horizontally
    /// and vertically, shrunk so it fits <paramref name="boxW"/>×<paramref name="boxH"/> at most.</summary>
    private void DrawTextAspect(Vector2 pos, float boxW, float boxH, string text, Color color, int size)
    {
        if (string.IsNullOrEmpty(text)) return;
        size = FitSize(text, boxW, boxH, size);
        float ty = pos.Y + (boxH - size) * 0.5f;
        DrawTextCentered(new Vector2(pos.X, ty), boxW, text, color, size);
    }

    /// <summary>Draw a (possibly <c>^N</c>/<c>^xRGB</c> color-coded) line fitted within an aspect box, centered
    /// horizontally + vertically (QC <c>drawcolorcodedstring_aspect</c>).</summary>
    private void DrawColoredAspect(Vector2 pos, float boxW, float boxH, string line, Color baseColor, int size)
    {
        if (string.IsNullOrEmpty(line)) return;
        string stripped = HudText.Strip(line);
        size = FitSize(stripped, boxW, boxH, size);

        var runs = HudText.Parse(line, baseColor);
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        float cx = pos.X + (boxW - total) * 0.5f;
        if (cx < pos.X) cx = pos.X;
        float ty = pos.Y + (boxH - size) * 0.5f;
        foreach (HudText.Run r in runs)
        {
            DrawText(new Vector2(cx, ty), r.Text, r.Color, size);
            cx += MeasureText(r.Text, size);
        }
    }

    /// <summary>Shrink a font size until the (already color-stripped) text fits in the box (QC the aspect-string
    /// shrink-to-fit). Never grows above the requested size; floors at 8px so it stays legible.</summary>
    private static int FitSize(string text, float boxW, float boxH, int size)
    {
        if (string.IsNullOrEmpty(text)) return size;
        size = (int)Mathf.Min(size, Mathf.Max(8f, boxH));
        float wpx = MeasureText(text, size);
        if (wpx > boxW && wpx > 0f)
            size = (int)Mathf.Max(8f, size * (boxW / wpx));
        return Mathf.Max(8, size);
    }
}
