using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;     // CvarFlags
using VortexArena.Engine.Simulation;   // CvarService (RegisterDefaults)

namespace VortexArena.Game.Hud;

/// <summary>
/// Center-of-screen timed messages — port of Base/.../qcsrc/client/hud/panel/centerprint.qc (HUD panel
/// #16). The QC version keeps a small ring of messages (each with an id, start time, duration and an
/// optional countdown), fading them in/out and stacking them vertically centered (or bottom-up when
/// <c>flip</c>), with an optional title line on top. It is fed by <c>centerprint_generic</c> from the
/// MSG_CENTER notification path (frag messages, countdowns, the MOTD, the duel title).
///
/// This port reproduces that pipeline: the notification layer calls <see cref="Push"/> with a resolved
/// MSG_CENTER <see cref="Notification"/> (or a plain string), an optional message <em>id</em> for
/// replace/kill semantics (QC <c>cpid</c>), and an optional <em>count</em> for the "^COUNT" countdown
/// token. The panel renders Xonotic <c>^N</c> color codes (via <see cref="HudText"/>), the per-line
/// <c>^BOLD</c> operator (drawn with <see cref="HudSkin.BoldFont"/> at the bold font scale), the title /
/// duel title, and live-substitutes the COUNT token, decrementing it so a single "Game starts in ^COUNT"
/// message counts down. Layout (align/flip/font scales/fades) tracks the <c>hud_panel_centerprint_*</c>
/// cvars live, exactly like QC's <c>autocvar_*</c> reads.
/// </summary>
public partial class CenterPrintPanel : HudPanel
{
    /// <summary>Default on-screen duration when a caller does not specify one (QC hud_panel_centerprint_time).</summary>
    public const float DefaultDuration = 3f;

    /// <summary>The QC "^COUNT" placeholder substituted with the live countdown number.</summary>
    public const string CountToken = "^COUNT";

    /// <summary>QC <c>BOLD_OPERATOR</c> (common/notifications/all.qh) — a per-line prefix selecting the bold font.</summary>
    public const string BoldOperator = "^BOLD";

    // QC: CENTERPRINT_MAX_MSGS / spacing constants (centerprint.qc:34-38).
    private const int MaxMessages = 10;
    private const float BaseScale = 1.3f;          // QC CENTERPRINT_BASE_SIZE
    private const float Spacing = 0.3f;            // QC CENTERPRINT_SPACING (× cp_fontsize.y)
    private const float TitleSpacing = 0.35f;      // QC CENTERPRINT_TITLE_SPACING

    private sealed class Message
    {
        public string Text = "";
        public string Id = "";          // QC cpid group; non-empty ids replace same-id messages
        public double StartTime;
        public double Duration;         // QC centerprint_time[]: requested lifetime (<0 = sticky/forced)
        public double ExpireTime;       // QC centerprint_expire_time[]; double.PositiveInfinity for sticky
        public int Count = -1;          // QC centerprint_countdown_num[]: countdown remaining; < 0 = no COUNT token
    }

    private readonly List<Message> _messages = new();
    private double _now;

    // ---- title (QC centerprint_title / _title_left / _title_right) ----
    private string _title = "";
    private string _titleLeft = "";
    private string _titleRight = "";

    /// <summary>Add a message shown for <see cref="DefaultDuration"/> seconds (QC centerprint_AddStandard).</summary>
    public void Add(string text) => AddTimed(text, DefaultDuration);

    /// <summary>
    /// Push a fully-formed centerprint line — the entry point the notification/net layer calls for a
    /// MSG_CENTER notification (after it has localized + arg-expanded the format string). Supports the
    /// "^COUNT" countdown token (pass <paramref name="count"/> ≥ 0) and an <paramref name="id"/> for QC
    /// replace/kill semantics: a new push with the same non-empty id replaces the existing one. A
    /// non-positive <paramref name="duration"/> makes the message sticky/forced (QC duration &lt; 0).
    /// </summary>
    public void Push(string text, float duration = DefaultDuration, string id = "", int count = -1)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // QC cpid: an id of an existing message replaces it in place (used by countdowns/announcements).
        if (!string.IsNullOrEmpty(id))
            _messages.RemoveAll(m => m.Id == id);

        // QC centerprint_Add: duration == 0 → the configured default time (min 1).
        double dur = duration;
        // Guard against a NaN/Inf duration (a malformed hud_panel_centerprint_time cvar or caller value): it would
        // seed ExpireTime = _now + NaN, which never compares true in _Process/MessageAlpha → a message that is
        // stuck on screen forever and never fades. Treat any non-finite duration as "use the default time".
        if (float.IsNaN(duration) || float.IsInfinity(duration)) dur = 0f;
        if (dur == 0d)
        {
            float t = TimeCvar;
            dur = (float.IsNaN(t) || float.IsInfinity(t)) ? DefaultDuration : System.Math.Max(1f, t);
        }

        var msg = new Message
        {
            Text = StripEdgeNewlines(text),
            Id = id ?? "",
            StartTime = _now,
            Duration = dur,
            // QC defers expire_time to the draw loop; sticky/forced (duration<0) never expires by time.
            ExpireTime = dur > 0f ? _now + dur : double.PositiveInfinity,
            Count = count,
        };
        if (string.IsNullOrEmpty(msg.Text) && string.IsNullOrEmpty(msg.Id)) return;

        _messages.Insert(0, msg); // newest on top (QC prepends at cpm_index)
        if (_messages.Count > MaxMessages)
            _messages.RemoveRange(MaxMessages, _messages.Count - MaxMessages);
        QueueRedraw();
    }

    /// <summary>
    /// Push a MSG_CENTER <see cref="Notification"/>'s message. Uses the notification's
    /// <see cref="Notification.Normal"/> format as the text (the caller is expected to have expanded any
    /// s1/s2/f-args already; remaining "^COUNT" is driven by <paramref name="count"/>) and its
    /// <see cref="Notification.Cpid"/> as the replace/kill id.
    /// </summary>
    public void Push(Notification center, string? expandedText = null,
        float duration = DefaultDuration, int count = -1)
    {
        if (center is null) return;
        string text = !string.IsNullOrEmpty(expandedText) ? expandedText! : center.Normal;
        Push(text, duration, center.Cpid ?? "", count);
    }

    /// <summary>
    /// Add a message shown for <paramref name="duration"/> seconds (QC centerprint_Add). A non-positive
    /// duration makes it sticky until <see cref="ClearAll"/> (QC duration &lt; 0).
    /// </summary>
    public void AddTimed(string text, float duration) => Push(text, duration);

    /// <summary>Fade out the message(s) with the given id (QC <c>centerprint_Kill</c> → <c>centerprint_Add(id, "",
    /// 0, 0)</c>, which doesn't delete the line but re-arms it to fade out over <c>fade_out</c> seconds so the
    /// kill is graceful; the faded-out entry is then reaped by <see cref="_Process"/>).</summary>
    public void Kill(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        bool changed = false;
        foreach (Message m in _messages)
        {
            if (m.Id != id) continue;
            // QC: start_time=0, time=min(5,fade_out), expire_time set so it fades from full → 0 over fade_out.
            double fade = System.Math.Min(5.0, System.Math.Max(0.0, FadeOutTime));
            m.Count = -1;                       // stop any countdown so it actually fades
            m.Duration = fade;
            m.StartTime = _now - fade;          // QC start_time=0 → past fade-in, so 'a' is driven by fade-out only
            m.ExpireTime = _now + fade;
            changed = true;
        }
        if (changed) QueueRedraw();
    }

    /// <summary>Remove every message (QC centerprint_KillAll).</summary>
    public void ClearAll()
    {
        _messages.Clear();
        QueueRedraw();
    }

    // =====================================================================================
    //  Title (QC centerprint_SetTitle / centerprint_SetDuelTitle / centerprint_ClearTitle)
    // =====================================================================================

    /// <summary>Set the centerprint title line drawn (bold) above the messages (QC <c>centerprint_SetTitle</c>).
    /// Pass null/empty to clear it. Mutually exclusive with the duel title (setting one clears the other).</summary>
    public void SetTitle(string? title)
    {
        string t = title ?? "";
        if (t == _title && _titleLeft == "" && _titleRight == "") return;
        _title = t;
        _titleLeft = "";
        _titleRight = "";
        QueueRedraw();
    }

    /// <summary>Set the duel title "left ^7vs^7 right" line drawn above the messages (QC
    /// <c>centerprint_SetDuelTitle</c>). Names may carry <c>^N</c> color codes. Clears the plain title.</summary>
    public void SetDuelTitle(string? left, string? right)
    {
        _title = "";
        _titleLeft = left ?? "";
        _titleRight = right ?? "";
        QueueRedraw();
    }

    /// <summary>Clear any title / duel title (QC <c>centerprint_ClearTitle</c>).</summary>
    public void ClearTitle()
    {
        if (_title == "" && _titleLeft == "" && _titleRight == "") return;
        _title = "";
        _titleLeft = "";
        _titleRight = "";
        QueueRedraw();
    }

    private bool HasTitle => _title.Length > 0 || _titleLeft.Length > 0;

    // =====================================================================================
    //  HUD-config live preview (QC HUD_CenterPrint autocvar__hud_configure block, centerprint.qc:179-217)
    // =====================================================================================

    // QC hud_configure_prev (whether the editor was open last frame) + hud_configure_cp_generation_time (the
    // next time a sample line is generated). Tracked here so we reproduce the enter/leave clears and the rotating
    // sample-message cadence the editor relies on to preview the panel.
    private bool _hudConfigurePrev;
    private double _cpGenerationTime;
    private readonly System.Random _previewRng = new();

    /// <summary>Per-frame HUD-editor preview generator — the C# successor to the
    /// <c>autocvar__hud_configure</c> branch of QC <c>HUD_CenterPrint</c> (centerprint.qc:179-217). When the
    /// editor opens, clears the panel; while it is open, emits a rotating sample centerprint (countdown /
    /// multiline / standard when this is the highlighted panel, else a plain "Generic message") plus a "Title"
    /// line, on the same randomized cadence as Base; on close, clears the title + all messages.</summary>
    private void UpdateHudConfigurePreview()
    {
        bool configuring = GlobalF("_hud_configure", 0f) != 0f;

        if (!configuring)
        {
            // QC: leaving the editor (hud_configure_prev) clears the editor-generated title + messages.
            if (_hudConfigurePrev)
            {
                ClearTitle();
                ClearAll();
            }
            _hudConfigurePrev = false;
            return;
        }

        // QC: on the first configure frame, KillAll + show a message immediately (generation_time = time).
        if (!_hudConfigurePrev)
        {
            ClearAll();
            _cpGenerationTime = _now; // show a message immediately
        }
        _hudConfigurePrev = true;

        if (_now <= _cpGenerationTime)
            return;

        if (IsHighlighted)
        {
            // QC centerprint_SetTitle(_("Title")).
            SetTitle("Title");

            float r = (float)_previewRng.NextDouble();
            if (r > 0.8f)
                // QC: countdown sample — id floor(r*1000), duration 1, count 10, "^COUNT" ticking.
                Push($"^3Countdown message at time {SecondsToString((float)_now)}, seconds left: {CountToken}",
                    1f, ((int)(r * 1000f)).ToString(System.Globalization.CultureInfo.InvariantCulture), 10);
            else if (r > 0.55f)
                // QC: multiline sample (id 0, duration 20) with a ^BOLD second line.
                Push($"^1Multiline message at time {SecondsToString((float)_now)} that\n{BoldOperator}lasts longer than normal",
                    20f);
            else
                // QC centerprint_AddStandard (id 0, default time).
                Add($"Message at time {SecondsToString((float)_now)}");

            _cpGenerationTime = _now + 1.0 + _previewRng.NextDouble() * 4.0;
        }
        else
        {
            // QC: non-highlighted panel shows a generic line on a slower cadence (no title).
            Push("Generic message", 10f);
            _cpGenerationTime = _now + 10.0 - _previewRng.NextDouble() * 3.0;
        }
    }

    // =====================================================================================
    //  Behaviour cvars (QC HUD_CenterPrint_Export saves these aesthetic cvars)
    // =====================================================================================

    /// <summary>Register this panel's behaviour cvars (auto-invoked by <see cref="HudConfig"/> via reflection).
    /// Defaults mirror Base/.../_hud_common.cfg + the centerprint.qc autocvar declarations.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        c.Register("hud_panel_centerprint_time", "3", CvarFlags.Save);
        c.Register("hud_panel_centerprint_align", "0.5", CvarFlags.Save);
        c.Register("hud_panel_centerprint_flip", "0", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fontscale", "1", CvarFlags.Save);
        // The shipped HUD skins (hud_luma.cfg / hud_luminos*.cfg:290-291) override the centerprint.qh header
        // defaults (1.4 / 1.8) with 1.2 / 1.3; the port bakes the loaded-skin (luma) values in as the registered
        // defaults so bold message lines + titles render at the stock-skin size.
        c.Register("hud_panel_centerprint_fontscale_bold", "1.2", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fontscale_title", "1.3", CvarFlags.Save);
        // centerprint.qh:6 defaults fade_in to 0.15 (messages ramp in, they don't pop).
        c.Register("hud_panel_centerprint_fade_in", "0.15", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_out", "0.15", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_subsequent", "1", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_subsequent_passone", "3", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_subsequent_passone_minalpha", "0.5", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_subsequent_passtwo", "10", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_subsequent_passtwo_minalpha", "0.5", CvarFlags.Save);
        c.Register("hud_panel_centerprint_fade_minfontsize", "1", CvarFlags.Save);
    }

    // Live cvar reads (QC autocvar_*). Defaults match the centerprint.qc autocvar declarations so the panel
    // behaves correctly even before ClientSettings has registered anything.
    private float TimeCvar => CvarF("time", 3f);
    private float Align => Mathf.Clamp(CvarF("align", 0.5f), 0f, 1f);
    private bool Flip => CvarBool("flip");
    // Font-scale cvars multiply cp_fontsize; a non-finite value would inject NaN into titleFont/yRef and corrupt
    // the pen position for the whole panel. Sanitize to a finite, positive scale (Finite() floors at a tiny eps).
    private float FontScale => Finite(CvarF("fontscale", 1f), 1f);
    // Defaults = the loaded (luma) skin's overrides, matching RegisterDefaults (hud_luma.cfg:290-291: 1.2 / 1.3).
    private float FontScaleBold => Finite(CvarF("fontscale_bold", 1.2f), 1.2f);
    private float FontScaleTitle => Finite(CvarF("fontscale_title", 1.3f), 1.3f);
    private float FadeInTime => CvarF("fade_in", 0.15f);
    private float FadeOutTime => Mathf.Min(5f, CvarF("fade_out", 0.15f));
    private bool FadeSubsequent => CvarF("fade_subsequent", 1f) != 0f;
    private float FadePassOne => CvarF("fade_subsequent_passone", 3f);
    // min-alpha cvars are progressive-fade floors and must stay in [0,1]: they are passed as the LOW bound of
    // Mathf.Clamp(value, min, 1) and Godot's Clamp is undefined when min > max — an out-of-range cvar would
    // otherwise push the per-message alpha above 1 (over-bright + over-sized font) or below 0 (→ Sqrt(NaN)).
    private float FadePassOneMin => Mathf.Clamp(CvarF("fade_subsequent_passone_minalpha", 0.5f), 0f, 1f);
    private float FadePassTwo => CvarF("fade_subsequent_passtwo", 10f);
    private float FadePassTwoMin => Mathf.Clamp(CvarF("fade_subsequent_passtwo_minalpha", 0.5f), 0f, 1f);
    // fade_minfontsize is the fraction the font shrinks DOWN to (sz = minfontsize + a*(1-minfontsize)); it must
    // stay in [0,1] or sz can go negative / blow up (tiny wrap width → degenerate per-char wrapping).
    private float FadeMinFontSize => Mathf.Clamp(CvarF("fade_minfontsize", 1f), 0f, 1f);

    // =====================================================================================
    //  Per-frame update (QC: the expire/countdown bookkeeping done inside the draw loop)
    // =====================================================================================

    public override void _Process(double delta)
    {
        _now += delta;

        // QC HUD_CenterPrint autocvar__hud_configure block (centerprint.qc:179-217): while the HUD editor is open
        // the panel generates rotating sample lines (+ a "Title" when it is the highlighted panel) so it previews
        // live while being positioned, and is cleared on enter/leave. Done here (not in DrawPanel) because _Process
        // already owns the panel clock; it self-gates on _hud_configure exactly like the QC branch.
        UpdateHudConfigurePreview();

        // Walk the live list, applying the QC countdown / expiry machinery. A countdown message re-extends its
        // expiry by its (per-step) duration and decrements the number each time the window passes, removing
        // itself when the number reaches 0; other timed messages just expire when their window passes.
        bool anyShowing = _messages.Count > 0;
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            Message m = _messages[i];
            if (double.IsPositiveInfinity(m.ExpireTime)) continue; // sticky/forced — never time out

            if (_now >= m.ExpireTime)
            {
                if (m.Count >= 0 && m.Duration > 0f)
                {
                    // QC: each elapsed window decrements the countdown and re-extends the expiry.
                    while (_now >= m.ExpireTime && m.Count > 0)
                    {
                        m.Count--;
                        if (m.Count <= 0) break;
                        m.ExpireTime += m.Duration;
                    }
                    if (m.Count <= 0) { _messages.RemoveAt(i); continue; }
                }
                else
                {
                    _messages.RemoveAt(i);
                    continue;
                }
            }
        }
        if (anyShowing) QueueRedraw();
    }

    // =====================================================================================
    //  Draw (QC HUD_CenterPrint)
    // =====================================================================================

    protected override void DrawPanel()
    {
        if (_messages.Count == 0 && !HasTitle) return; // self-blank when nothing to show

        DrawBackground(); // luma bg is "0" so this is a no-op unless a cfg opts a frame in

        // QC: pad inset (HUD_CenterPrint: panel_pos += padding; panel_size -= 2*padding).
        float pad = Cfg.Padding;
        float left = pad;
        float top = pad;
        float width = Mathf.Max(1f, Size2.X - 2f * pad);
        float bottom = Mathf.Max(top + 1f, Size2.Y - pad);

        // QC cp_fontsize = hud_fontsize * CENTERPRINT_BASE_SIZE; we use the resolved body font as hud_fontsize.
        float cpFont = Mathf.Max(8f, Cfg.FontSize * BaseScale);

        bool flip = Flip;
        float align = Align;
        float fgAlpha = LiveFgAlpha;

        // The running pen: top edge when normal, bottom edge when flipped (QC pos.y += panel_size.y).
        float y = flip ? bottom : top;

        // ---- title line (QC: drawn first, above the messages) ----
        if (HasTitle)
            y = DrawTitle(left, width, ref y, cpFont, align, flip, top, bottom, fgAlpha);

        // ---- messages ----
        // QC walks the ring newest-first; `g` counts shown messages for the subsequent-fade passes.
        int g = 0;
        foreach (Message m in _messages)
        {
            float a = MessageAlpha(m, g, fgAlpha);

            // QC: a guaranteed-invisible non-countdown message is skipped; a countdown message is always laid
            // out (a==... it still holds its slot) so positions don't jump while it counts down.
            bool isCountdown = m.Count >= 0;
            if (a <= 0.5f / 255f && !isCountdown) continue;

            // QC: the font shrinks with alpha down to fade_minfontsize.
            float minSz = FadeMinFontSize;
            float sz = minSz + a * (1f - minSz);
            if (sz <= 0f) sz = 0.0001f;

            // Substitute the live countdown, then split on embedded newlines (QC tokenized by '\n').
            string text = isCountdown ? m.Text.Replace(CountToken, Mathf.Max(0, m.Count).ToString()) : m.Text;

            _ = DrawMessage(left, width, ref y, text, cpFont, sz, a, align, flip, top, bottom);

            g++;

            // QC inter-message spacing + the "don't slide newer messages over a fading id-less one" nudge.
            float gap = Spacing * cpFont;
            if (flip)
            {
                y -= gap;
                if (a < 1f && string.IsNullOrEmpty(m.Id)) y += (1f - Mathf.Sqrt(a));
                if (y < top) return; // no room for the next message
            }
            else
            {
                y += gap;
                if (a < 1f && string.IsNullOrEmpty(m.Id)) y -= (1f - Mathf.Sqrt(a));
                if (y > bottom - cpFont) return; // no room for the next message
            }
        }
    }

    /// <summary>Draw the title / duel title (QC the title block at centerprint.qc:269-336). Returns the new pen Y.</summary>
    private float DrawTitle(float left, float width, ref float yRef, float cpFont, float align, bool flip,
        float top, float bottom, float fgAlpha)
    {
        float titleFont = Mathf.Max(8f, cpFont * FontScaleTitle);
        var col = new Color(1f, 1f, 1f, fgAlpha);

        if (flip) yRef -= titleFont; // QC pos.y -= fontsize.y before drawing in flip mode

        if (_titleLeft.Length > 0 || _titleRight.Length > 0)
        {
            // Duel title: "left   vs   right", centered as a block, with an underline under each name.
            // QC centerprint_SetDuelTitle shortens each name to hud_panel_scoreboard_namesize * hud_fontsize.x
            // (textShortenToWidth) so a long name can't overflow the title block.
            float nameBudget = GlobalF("hud_panel_scoreboard_namesize", 15f) * Cfg.FontSize;
            string lName = ShortenColored(_titleLeft, nameBudget, (int)titleFont);
            string rName = ShortenColored(_titleRight, nameBudget, (int)titleFont);
            float pad = MeasureText(" ", (int)titleFont) * 2f;
            float leftW = MeasuredColoredWidth(lName, (int)titleFont);
            float rightW = MeasuredColoredWidth(rName, (int)titleFont);
            float maxRl = Mathf.Max(leftW, rightW);
            float vsW = MeasureText("vs", (int)titleFont);
            float blockW = maxRl * 2f + pad * 6f + vsW;
            float bx = left + (width - blockW) * align;

            float lx = bx + pad + (leftW < rightW ? (maxRl - leftW) * 0.5f : 0f);
            DrawColoredRun(new Vector2(lx, yRef), lName, col, (int)titleFont);

            float vx = bx + maxRl + pad * 3f;
            DrawText(new Vector2(vx, yRef), "vs", col, (int)titleFont);

            float rx = bx + blockW - pad - maxRl + (leftW >= rightW ? (maxRl - rightW) * 0.5f : 0f);
            DrawColoredRun(new Vector2(rx, yRef), rName, col, (int)titleFont);

            // advance, then underline under each name cell
            yRef += flip ? -(cpFont * TitleSpacing) : (titleFont + cpFont * TitleSpacing);
            float underY = yRef + titleFont;
            DrawRect(new Rect2(bx, underY, maxRl + pad * 2f, 1f), col);
            DrawRect(new Rect2(bx + blockW - maxRl - pad * 2f, underY, maxRl + pad * 2f, 1f), col);
            yRef += flip ? -(cpFont * TitleSpacing) : (cpFont * TitleSpacing);
        }
        else
        {
            float w = MeasuredColoredWidth(_title, (int)titleFont);
            float tx = left + (width - w) * align;
            DrawColoredRun(new Vector2(tx, yRef), _title, col, (int)titleFont);

            yRef += flip ? -(cpFont * TitleSpacing) : (titleFont + cpFont * TitleSpacing);
            float underY = yRef + titleFont * 0.0f; // underline sits just under the (advanced) baseline
            DrawRect(new Rect2(tx - MeasureText(" ", (int)titleFont), underY, w + 2f * MeasureText(" ", (int)titleFont), 1f), col);
            yRef += flip ? -(cpFont * TitleSpacing) : (cpFont * TitleSpacing);
        }

        return yRef;
    }

    /// <summary>
    /// Draw one (possibly multi-line, wrapped, ^BOLD, ^N-colored) message, advancing the pen. In flip mode the
    /// message body is laid out top-down within a block whose top edge is pre-computed, then the pen is moved up
    /// past it. Returns the total vertical extent consumed (QC msg_size).
    /// </summary>
    private float DrawMessage(float left, float width, ref float yRef, string text, float cpFont, float sz,
        float a, float align, bool flip, float top, float bottom)
    {
        // Build the wrapped, per-line render list first (so flip mode can pre-measure the block height).
        var lines = new List<(string text, bool bold, float font)>();
        foreach (string raw in text.Split('\n'))
        {
            string s = raw;
            bool bold = s.StartsWith(BoldOperator, System.StringComparison.Ordinal);
            if (bold) s = s.Substring(BoldOperator.Length);
            float lineFont = Mathf.Max(8f, cpFont * (bold ? FontScaleBold : FontScale));
            float wrapW = width / Mathf.Max(0.0001f, sz); // QC wraps to panel_size.x*hud_scale*sz at full font

            foreach (string wl in WrapLine(s, wrapW, (int)lineFont))
                lines.Add((wl, bold, lineFont * sz));
        }
        if (lines.Count == 0) return 0f;

        float startY;
        if (flip)
        {
            // Pre-measure the block height and place its top so the pen lands above the previous content.
            float h = 0f;
            foreach (var ln in lines) h += ln.font;
            yRef -= h;
            startY = yRef;
        }
        else
        {
            startY = yRef;
        }

        float drawY = startY;
        foreach (var (lineText, bold, font) in lines)
        {
            DrawCenteredLine(left, width, drawY, lineText, font, a, align, bold);
            drawY += font;
        }

        if (!flip) yRef = drawY;
        return drawY - startY;
    }

    /// <summary>Draw a single wrapped line, color-coded, aligned by <paramref name="align"/>, optionally bold
    /// (drawn with <see cref="HudSkin.BoldFont"/>). QC <c>drawcolorcodedstring</c> + the bold-font scope.</summary>
    private void DrawCenteredLine(float left, float width, float y, string line, float font, float a,
        float align, bool bold)
    {
        if (a <= 0.5f / 255f) return;
        int size = Mathf.Max(8, Mathf.RoundToInt(font));
        var baseColor = new Color(1f, 1f, 1f, Mathf.Clamp(a, 0f, 1f));
        Font drawFont = bold ? (HudSkin.BoldFont ?? Font) : Font;

        List<HudText.Run> runs = HudText.Parse(line, baseColor);
        if (runs.Count == 0) return;

        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureWith(drawFont, r.Text, size);

        float cx = left + (width - total) * align;
        if (cx < left) cx = left;
        foreach (HudText.Run r in runs)
        {
            var c = r.Color; c.A = baseColor.A;
            DrawWith(drawFont, new Vector2(cx, y), r.Text, c, size);
            cx += MeasureWith(drawFont, r.Text, size);
        }
    }

    // =====================================================================================
    //  Alpha (QC: fade in/out + the subsequent-message progressive fade)
    // =====================================================================================

    private float MessageAlpha(Message m, int g, float fgAlpha)
    {
        float a = 1f;
        bool countdown = m.Count >= 0;

        // Countdowns don't fade (QC: fade_in/out forced to 0 so the number stays crisp while ticking).
        float fadeIn = countdown ? 0f : FadeInTime;
        float fadeOut = countdown ? 0f : FadeOutTime;

        double age = _now - m.StartTime;
        if (fadeIn > 0f && _now < m.StartTime + fadeIn)
            a = (float)(age / fadeIn);                                   // fade in
        else if (double.IsPositiveInfinity(m.ExpireTime) || m.Duration < 0f
                 || _now < m.ExpireTime - fadeOut)
            a = 1f;                                                      // steady / forced
        else if (fadeOut > 0f)
            a = (float)((m.ExpireTime - _now) / fadeOut);               // fade out

        a = Mathf.Clamp(a, 0f, 1f);

        // Subsequent-message progressive fade (QC two-pass: each later message dimmer than the one before).
        if (FadeSubsequent)
        {
            a *= Mathf.Clamp(1f - (g / Mathf.Max(1f, FadePassOne)), FadePassOneMin, 1f);
            a *= Mathf.Clamp(1f - (g / Mathf.Max(1f, FadePassTwo)), FadePassTwoMin, 1f);
        }

        // Final clamp + NaN guard: `a` feeds the font size (sz) and Mathf.Sqrt(a) in DrawPanel. An out-of-range
        // or NaN alpha there would yield a negative/huge font, a NaN pen Y (which slips past every `y > bottom`
        // bound check, since NaN comparisons are false) and thus off-panel/garbage draws every frame.
        float result = Mathf.Clamp(a, 0f, 1f) * Mathf.Clamp(fgAlpha, 0f, 1f);
        return float.IsNaN(result) ? 0f : result;
    }

    // =====================================================================================
    //  Text helpers (word-wrap + arbitrary-font draw/measure for the bold/title fonts)
    // =====================================================================================

    /// <summary>Wrap a (color-coded) line to <paramref name="maxWidth"/> px on word boundaries (QC
    /// <c>getWrappedLine</c>). Color codes don't add width; an over-long single word is hard-broken.</summary>
    private List<string> WrapLine(string line, float maxWidth, int size)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) { result.Add(""); return result; }
        if (maxWidth <= 1f || MeasuredColoredWidth(line, size) <= maxWidth) { result.Add(line); return result; }

        string[] words = line.Split(' ');
        var cur = new System.Text.StringBuilder();
        foreach (string word in words)
        {
            string candidate = cur.Length == 0 ? word : cur + " " + word;
            if (MeasuredColoredWidth(candidate, size) <= maxWidth || cur.Length == 0)
            {
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(word);
                // hard-break a single word that is itself wider than the line
                if (cur.Length == word.Length && MeasuredColoredWidth(cur.ToString(), size) > maxWidth)
                {
                    foreach (string piece in HardBreak(cur.ToString(), maxWidth, size))
                        result.Add(piece);
                    cur.Clear();
                }
            }
            else
            {
                result.Add(cur.ToString());
                cur.Clear();
                cur.Append(word);
            }
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        if (result.Count == 0) result.Add("");
        return result;
    }

    /// <summary>Hard-break a single token wider than the line into width-bounded chunks.</summary>
    private List<string> HardBreak(string word, float maxWidth, int size)
    {
        var pieces = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (char ch in word)
        {
            cur.Append(ch);
            if (MeasuredColoredWidth(cur.ToString(), size) > maxWidth && cur.Length > 1)
            {
                cur.Length -= 1;
                pieces.Add(cur.ToString());
                cur.Clear();
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) pieces.Add(cur.ToString());
        return pieces;
    }

    /// <summary>Width of a color-coded string with the default font, codes stripped (QC <c>stringwidth_colors</c>).</summary>
    private float MeasuredColoredWidth(string text, int size) => MeasureText(HudText.Strip(text), size);

    /// <summary>
    /// Truncate a color-coded name to <paramref name="maxWidth"/> px, appending an ellipsis — QC
    /// <c>textShortenToWidth</c> as used by <c>centerprint_SetDuelTitle</c>. Color codes (<c>^N</c>/<c>^xRGB</c>)
    /// are copied through and do not count toward the visible width, so the surviving colors stay intact.
    /// </summary>
    private string ShortenColored(string text, float maxWidth, int size)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return text;
        if (MeasuredColoredWidth(text, size) <= maxWidth) return text;

        const string ell = "…";
        float budget = Mathf.Max(0f, maxWidth - MeasureText(ell, size));
        var sb = new System.Text.StringBuilder(text.Length + 1);
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            // Copy a color code verbatim without charging width (^^ literal, ^N digit, ^xRGB hex triplet).
            if (text[i] == '^' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '^') { sb.Append("^^"); i++; continue; }
                if (char.IsDigit(n)) { sb.Append('^').Append(n); i++; continue; }
                if (n == 'x' && i + 4 < text.Length && IsHex(text[i + 2]) && IsHex(text[i + 3]) && IsHex(text[i + 4]))
                { sb.Append(text, i, 5); i += 4; continue; }
            }
            float cw = MeasureText(text[i].ToString(), size);
            if (w + cw > budget) break;
            sb.Append(text[i]);
            w += cw;
        }
        sb.Append(ell);
        return sb.ToString();
    }

    private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>Draw left-aligned text with an arbitrary font + drop shadow (mirrors the base <c>DrawText</c>
    /// but lets us swap to the bold font). Panel-local top-left origin.</summary>
    private void DrawWith(Font font, Vector2 pos, string text, Color color, int size)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 at = pos + new Vector2(0f, size);
        var shadow = new Color(0f, 0f, 0f, color.A * 0.7f);
        DrawString(font, at + new Vector2(1f, 1f), text, HorizontalAlignment.Left, -1f, size, shadow);
        DrawString(font, at, text, HorizontalAlignment.Left, -1f, size, color);
    }

    /// <summary>Measure a plain string with an arbitrary font.</summary>
    private static float MeasureWith(Font font, string text, int size)
        => string.IsNullOrEmpty(text) ? 0f : font.GetStringSize(text, HorizontalAlignment.Left, -1f, size).X;

    /// <summary>Draw a single (color-coded) run at a point with the default font, laid out left-to-right.</summary>
    private void DrawColoredRun(Vector2 pos, string text, Color baseColor, int size)
    {
        float cx = pos.X;
        foreach (HudText.Run r in HudText.Parse(text, baseColor))
        {
            var c = r.Color; c.A = baseColor.A;
            DrawText(new Vector2(cx, pos.Y), r.Text, c, size);
            cx += MeasureText(r.Text, size);
        }
    }

    /// <summary>QC: strip leading + trailing newlines from a message (centerprint_Add edge trimming).</summary>
    private static string StripEdgeNewlines(string s) => s.Trim('\n');

    /// <summary>Return <paramref name="v"/> if it is a finite positive number, else <paramref name="fallback"/>.
    /// Keeps a malformed (NaN/Inf/non-positive) font-scale cvar from injecting NaN into the per-frame draw math.</summary>
    private static float Finite(float v, float fallback)
        => (float.IsNaN(v) || float.IsInfinity(v) || v <= 0f) ? fallback : v;
}
