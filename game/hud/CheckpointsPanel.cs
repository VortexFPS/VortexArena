using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;     // CvarFlags
using VortexArena.Engine.Simulation;   // CvarService (RegisterDefaults)

namespace VortexArena.Game.Hud;

/// <summary>
/// Checkpoints panel — port of Base/.../qcsrc/client/hud/panel/checkpoints.qc (HUD panel #27). The QC version
/// draws the persistent list of stored checkpoint split lines for the current race run — the
/// <c>race_checkpoint_splits[]</c> array, one colored string per checkpoint passed (built by the race timer's
/// <c>MakeRaceString</c> + speed text and stashed via <c>StoreCheckpointSplits</c>) — stacked top-to-bottom
/// (or bottom-up when <c>hud_panel_checkpoints_flip</c>), word-wrapped to the panel width with an optional
/// alignment, at <c>hud_panel_checkpoints_fontscale</c>.
///
/// The split strings are produced server-side / by the race net path, so the net layer pushes them here via
/// <see cref="SetSplits"/> / <see cref="StoreSplit"/> (the analogue of QC <c>StoreCheckpointSplits</c>),
/// indexed by checkpoint number (0/255 = finish). The panel renders the non-empty entries highest-cp-first
/// (QC <c>for (j = end; j &gt;= 0; --j)</c>), honoring <see cref="Flip"/>, <see cref="Align"/> and
/// <see cref="FontScale"/>, faithfully word-wrapping each line to the panel width (QC
/// <c>Checkpoints_drawstring</c> → <c>getWrappedLine</c>) and color-coding the embedded <c>^N</c> delta codes
/// baked into each split by <c>MakeRaceString</c> (^1 slower / ^2 faster / ^3 equal).
/// </summary>
public partial class CheckpointsPanel : HudPanel
{
    // QC checkpoints.qc:19-20 — the layout constants.
    private const float CheckpointsSpacing = 0.25f;   // QC CHECKPOINTS_SPACING (× fontsize.y between lines)
    private const float CheckpointsBaseScale = 0.75f; // QC CHECKPOINTS_BASE_SIZE (splits font = 0.75 of hud font)

    // ----------------------------------------------------------------------------------------------
    //  Behaviour cvars (QC autocvar_hud_panel_checkpoints_* — saved by HUD_Checkpoints_Export).
    //  Backed by the shared store so console/menu changes are live; the public properties below
    //  PRESERVE the original setter API (the net layer may set them directly) while ALSO mirroring the
    //  cvar so either path works.
    // ----------------------------------------------------------------------------------------------

    /// <summary>QC <c>hud_panel_checkpoints_flip</c>: stack the splits bottom-up instead of top-down.</summary>
    public bool Flip
    {
        get => CvarBool("flip");
        set => MenuState_SetBool("flip", value);
    }

    /// <summary>QC <c>hud_panel_checkpoints_align</c>: 0 = left, 1 = right (fraction in between).</summary>
    public float Align
    {
        get => Mathf.Clamp(CvarF("align", 1f), 0f, 1f);
        set => MenuState_SetFloat("align", Mathf.Clamp(value, 0f, 1f));
    }

    /// <summary>QC <c>hud_panel_checkpoints_fontscale</c>: text-size multiplier (relative to the base size). QC
    /// treats values &lt;= 0.125 as "off" → fall back to the unscaled base size. Fallback matches the luma skin
    /// default (1.1).</summary>
    public float FontScale
    {
        get => CvarF("fontscale", 1.1f);
        set => MenuState_SetFloat("fontscale", value);
    }

    // QC race_checkpoint_splits[256]: one stored split string per checkpoint (null/empty = unused). We keep a
    // sparse list of (index, text) so iteration is cheap; index ordering matches QC's "highest cp first" walk.
    private readonly SortedDictionary<int, string> _splits = new();

    // Wrap cache — recomputed lazily whenever the splits, panel width, font size, flip or align change. QC
    // re-wraps every frame; we only re-wrap on a relevant change to keep DrawPanel allocation-free in the
    // steady state (the panel is non-dynamic). startsBlock marks the first wrapped line of each split (used to
    // apply the QC CHECKPOINTS_SPACING gap between splits, but not within a wrapped split).
    private readonly List<(string text, float width, bool startsBlock)> _wrapped = new();
    private float _wrapW = -1f;
    private int _wrapSize = -1;
    private float _wrapAlign = -1f;
    private bool _wrapFlip;
    private bool _wrapDirty = true;

    /// <summary>This panel only changes when splits are pushed; no per-frame animation.</summary>
    public override bool IsDynamic => false;

    // ==============================================================================================
    //  Behaviour-cvar registration (auto-invoked by HudConfig via reflection)
    // ==============================================================================================

    /// <summary>Register this panel's behaviour cvars (QC <c>HUD_Checkpoints_Export</c> saves these aesthetic
    /// cvars). Defaults mirror the checkpoints.qc autocvar declarations + _hud_common.cfg.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        c.Register("hud_panel_checkpoints_align", "1", CvarFlags.Save);
        c.Register("hud_panel_checkpoints_flip", "0", CvarFlags.Save);
        c.Register("hud_panel_checkpoints_fontscale", "1.1", CvarFlags.Save);
    }

    // Shared-store writers backing the legacy property setters. We write straight to MenuState.Cvars (the same
    // store CvarStr/CvarF read) so a property set and a console `set` are interchangeable and stay live.
    private void MenuState_SetFloat(string suffix, float v)
    {
        VortexArena.Game.Menu.MenuState.Cvars.Set($"hud_panel_{PanelId}_{suffix}",
            v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _wrapDirty = true;
        QueueRedraw();
    }

    private void MenuState_SetBool(string suffix, bool v) => MenuState_SetFloat(suffix, v ? 1f : 0f);

    // ==============================================================================================
    //  Public data feed (the net/race layer pushes splits here — analogue of StoreCheckpointSplits)
    // ==============================================================================================

    /// <summary>
    /// Replace the whole stored-splits set (QC: the race code repopulating <c>race_checkpoint_splits</c>).
    /// Keys are checkpoint numbers; pass an empty/null value to clear a slot.
    /// </summary>
    public void SetSplits(IEnumerable<KeyValuePair<int, string>>? splits)
    {
        _splits.Clear();
        if (splits is not null)
            foreach (var kv in splits)
                // QC race_checkpoint_splits[256]: only slots 0..255 exist. Drop out-of-range keys so a stray
                // index can't pollute the panel (and never deref a null value).
                if (!string.IsNullOrEmpty(kv.Value) && kv.Key >= 0 && kv.Key < 256)
                    _splits[kv.Key] = kv.Value;
        _wrapDirty = true;
        QueueRedraw();
    }

    /// <summary>
    /// Store one checkpoint's split line (QC <c>StoreCheckpointSplits</c>): checkpoint 0 maps to the finish
    /// slot (255), matching the QC <c>race_checkpoint ? race_checkpoint : 255</c> indexing.
    /// </summary>
    public void StoreSplit(int checkpoint, string text)
    {
        int slot = checkpoint != 0 ? checkpoint : 255;
        // QC race_checkpoint_splits[256]: clamp to the valid array range so an out-of-range checkpoint can't
        // wedge a garbage slot into the panel (the QC array indexing would simply be invalid).
        if (slot < 0 || slot >= 256) return;
        if (string.IsNullOrEmpty(text)) _splits.Remove(slot);
        else _splits[slot] = text;
        _wrapDirty = true;
        QueueRedraw();
    }

    /// <summary>
    /// Store one checkpoint's split with an explicit time delta, building the QC <c>MakeRaceString</c> delta
    /// color in front of the line (^1 slower / ^3 equal / ^2 faster — checkpoints.qc:33-45 "goal hit" branch).
    /// Convenience for callers that have a numeric delta rather than a pre-colored string; the existing
    /// <see cref="StoreSplit(int,string)"/> (already-colored strings) is unchanged.
    /// </summary>
    public void StoreSplit(int checkpoint, string label, float delta)
    {
        if (!float.IsFinite(delta)) delta = 0f;                              // never render "NaN"/"Inf"
        label ??= string.Empty;                                             // tolerate a null label from a caller
        string sign = delta > 0f ? "+" : delta < 0f ? "-" : "+";
        string col = delta > 0f ? "^1" : delta < 0f ? "^2" : "^3";          // QC: slower red / faster green / equal yellow
        string timestr = delta == 0f ? "+0.0" : sign + Mathf.Abs(delta).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        StoreSplit(checkpoint, $"{col}{label} ({timestr})");
    }

    /// <summary>Clear all stored splits (QC <c>ClearCheckpointSplits</c>).</summary>
    public void ClearSplits()
    {
        _splits.Clear();
        _wrapDirty = true;
        QueueRedraw();
    }

    // ==============================================================================================
    //  Draw (QC HUD_Checkpoints / Checkpoints_Draw / Checkpoints_drawstring)
    // ==============================================================================================

    protected override void DrawPanel()
    {
        if (_splits.Count == 0) return; // self-blank: nothing stored yet

        // Self-blank on a degenerate panel rect (zero/negative/NaN size) so nothing draws off-panel or NaN.
        if (!(Size2.X > 0f) || !(Size2.Y > 0f)) return;

        DrawBackground(); // QC HUD_Panel_DrawBg (luma bg is "0" → no-op unless a cfg opts a frame in)

        // QC HUD_Checkpoints: inset by the bg padding (panel_pos += pad; panel_size -= 2*pad).
        float pad = Mathf.Max(0f, Cfg.Padding);
        float left = pad;
        float top = pad;
        float bottom = Mathf.Max(top + 1f, Size2.Y - pad);
        float width = Mathf.Max(1f, Size2.X - 2f * pad);

        // QC: rs_fontsize = hud_fontsize * CHECKPOINTS_BASE_SIZE; fontsize = scale>0.125 ? rs*scale : rs.
        float baseFont = Mathf.Max(8f, Cfg.FontSize * CheckpointsBaseScale);
        float scale = FontScale;
        // Clamp the user-facing fontscale: a console `set ... nan/inf/1e9` must not blow size up to a
        // screen-filling / pathologically-slow glyph. QC treats <= 0.125 as "off" (use the base size).
        if (!float.IsFinite(scale) || scale <= 0.125f) scale = 1f;
        else scale = Mathf.Min(scale, 8f);
        float fontF = baseFont * scale;
        int size = Mathf.Clamp(Mathf.RoundToInt(fontF), 8, 256);
        float lineH = size; // QC advances pos.y by fontsize.y per wrapped line
        float gap = CheckpointsSpacing * fontF; // QC CHECKPOINTS_SPACING * fontsize.y between splits
        float rsLineH = Mathf.Max(8f, baseFont); // QC rs_fontsize.y, used in the "fits?" check

        bool flip = Flip;
        float align = Align;

        // (Re)build the wrapped line list (QC re-wraps each frame; cached here since the panel is non-dynamic).
        EnsureWrapped(width, size, align, flip);
        if (_wrapped.Count == 0) return;

        // QC Checkpoints_Draw: pos starts at panel top, or bottom edge when flipping; each split is a block of
        // wrapped lines, and CHECKPOINTS_SPACING is added between consecutive splits (not within a split's
        // wrapped lines). We walk the wrapped lines in render order (highest-cp-first, top-down) and place them
        // either downward or upward.
        var col = FgColor;

        // QC hanging indent: the FIRST wrapped line of a split starts at offset 0, every continuation line is
        // indented by fontsize.x (checkpoints.qc:38,54 `offset = fontsize.x;`). For a square-cell HUD font
        // fontsize.x == fontsize.y, so we indent continuation lines by the line height.
        float indent = lineH;

        if (!flip)
        {
            float y = top;
            for (int i = 0; i < _wrapped.Count; i++)
            {
                var (text, lineW, startsBlock) = _wrapped[i];
                if (startsBlock && i > 0) y += gap; // QC inter-split spacing (skip before the first split)
                if (y > bottom - rsLineH) break;    // QC: stop once the next line won't fit (uses rs_fontsize.y)
                float lineLeft = startsBlock ? left : left + indent;
                float lineWidthAvail = startsBlock ? width : Mathf.Max(1f, width - indent);
                DrawAlignedColored(lineLeft, lineWidthAvail, y, text, col, size, align, lineW);
                y += lineH;
            }
        }
        else
        {
            // QC flip: pos starts at the bottom, each line drawn moving upward. We render the list reversed so
            // the most-recent split still reads at the same end; the QC code pre-walks to find the top, then
            // draws downward — visually equivalent to drawing the reversed list upward, line by line. The
            // inter-split gap precedes the FIRST line of a split, i.e. (in reverse) follows its last line.
            float y = bottom - lineH;
            for (int i = _wrapped.Count - 1; i >= 0; i--)
            {
                var (text, lineW, startsBlock) = _wrapped[i];
                if (y < top) break;                 // QC: stop once we run past the panel top
                float lineLeft = startsBlock ? left : left + indent;
                float lineWidthAvail = startsBlock ? width : Mathf.Max(1f, width - indent);
                DrawAlignedColored(lineLeft, lineWidthAvail, y, text, col, size, align, lineW);
                y -= lineH;
                if (startsBlock && i > 0) y -= gap; // QC inter-split spacing (between this split and the next-up)
            }
        }
    }

    // ==============================================================================================
    //  Word-wrap (QC Checkpoints_drawstring → getWrappedLine, with CHECKPOINTS_SPACING gaps)
    // ==============================================================================================

    /// <summary>
    /// Build the flat, wrapped, render-ordered line list from the stored splits. Highest checkpoint first (QC
    /// <c>for (j = end; j &gt;= 0; --j)</c>); each split's text is word-wrapped to <paramref name="width"/> at
    /// <paramref name="size"/>. The first wrapped line of each split is tagged <c>startsBlock</c> so the draw
    /// walk can apply the QC CHECKPOINTS_SPACING gap between (but not within) splits. Cached against the wrap
    /// inputs so the steady-state draw is allocation-free.
    /// </summary>
    private void EnsureWrapped(float width, int size, float align, bool flip)
    {
        if (!_wrapDirty && width == _wrapW && size == _wrapSize && align == _wrapAlign && flip == _wrapFlip)
            return;

        _wrapped.Clear();
        _wrapW = width;
        _wrapSize = size;
        _wrapAlign = align;
        _wrapFlip = flip;
        _wrapDirty = false;

        // QC walks j = end..0 (highest checkpoint first). _splits is ascending → reverse the key order.
        var ordered = new List<string>(_splits.Values);
        ordered.Reverse();

        foreach (string split in ordered)
        {
            if (string.IsNullOrEmpty(split)) continue; // QC: skip "" entries
            bool first = true;
            foreach (string wl in WrapLine(split, width, size))
            {
                _wrapped.Add((wl, MeasuredColoredWidth(wl, size), first));
                first = false;
            }
        }
    }

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
                // Word doesn't fit on the current (non-empty) line: flush the line, then start the word fresh.
                result.Add(cur.ToString());
                cur.Clear();
                // If the word alone is wider than the line, hard-break it now so it never draws off-panel
                // (the post-loop emit would otherwise add the whole over-wide word verbatim).
                if (MeasuredColoredWidth(word, size) > maxWidth)
                {
                    var pieces = HardBreak(word, maxWidth, size);
                    for (int p = 0; p < pieces.Count - 1; p++) result.Add(pieces[p]);
                    if (pieces.Count > 0) cur.Append(pieces[pieces.Count - 1]); // carry the tail for the next word
                }
                else
                {
                    cur.Append(word);
                }
            }
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        if (result.Count == 0) result.Add("");
        return result;
    }

    /// <summary>Hard-break a single token wider than the line into width-bounded chunks (QC: the engine breaks
    /// anywhere when no space fits — see take_wrapped_line_until).</summary>
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

    /// <summary>Width of a color-coded string with codes stripped (QC <c>stringwidth_colors</c>).</summary>
    private static float MeasuredColoredWidth(string text, int size) => MeasureText(HudText.Strip(text), size);

    /// <summary>
    /// Draw a (possibly <c>^N</c> color-coded) split line, aligned within <paramref name="width"/> by
    /// <paramref name="align"/> (0 = left, 1 = right) — QC's <c>Checkpoints_drawstring</c> alignment offset
    /// <c>(sz.x - stringwidth_colors(s) - offset) * align</c>. The leading delta color baked in by
    /// <c>MakeRaceString</c> (^1/^2/^3) is honored via <see cref="HudText"/>.
    /// </summary>
    private void DrawAlignedColored(float left, float width, float y, string line, Color baseColor, int size,
        float align, float lineWidth)
    {
        var runs = HudText.Parse(line, baseColor);
        if (runs.Count == 0) return;

        // QC alignment: nudge the whole line right by (free space) * align (left at 0, right at 1).
        float cx = left + (width - lineWidth) * align;
        if (cx < left) cx = left;
        foreach (HudText.Run r in runs)
        {
            var c = r.Color; c.A = baseColor.A;
            DrawText(new Vector2(cx, y), r.Text, c, size);
            cx += MeasureText(r.Text, size);
        }
    }
}
