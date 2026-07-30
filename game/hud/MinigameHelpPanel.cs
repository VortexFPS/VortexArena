using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Common.Services;     // CvarFlags
using VortexArena.Engine.Simulation;   // CvarService (RegisterDefaults)

namespace VortexArena.Game.Hud;

/// <summary>
/// Minigame help / rules text — the C# successor to QuakeC's <c>HUD_MinigameHelp</c>
/// (Base/.../qcsrc/common/minigames/cl_minigames_hud.qc:573). The QC panel drew
/// <c>active_minigame.message</c> (the active session's help blurb) word-wrapped and centered, only while a
/// board minigame was running and the minigame menu was open; otherwise it drew nothing.
///
/// This port keeps that contract: it shows the help/rules for the <see cref="ActiveMinigame"/> game and
/// <b>self-blanks</b> whenever there is no active minigame. The visibility table (luma
/// <see cref="HudLayoutDefaults"/>) already gates the panel to <see cref="PanelShow.Minigame"/>, so the panel
/// only enters the draw set during a board game — the data setter is the last gate (no game → no text → no draw).
///
/// QC stored the help string on the networked session entity; the seven shipped minigames just rendered it.
/// Since the rules text is static per game, the port supplies a faithful built-in blurb per game id
/// (ttt/pong/nmm/c4/pp/ps/bd) — the same set registered by <c>REGISTER_MINIGAME</c> — so the panel needs no
/// extra net plumbing to show useful help. The net/match layer feeds the active game's id via
/// <see cref="ActiveMinigame"/> (the session netname, e.g. <c>"ttt"</c> or <c>"ttt_3"</c>); an owner that has
/// composed its own help text may instead push it verbatim with <see cref="HelpOverride"/>.
///
/// Rendering matches QC <c>minigame_drawcolorcodedstring_wrapped</c>: Xolonium text, word-wrapped to the inner
/// width, supporting Xonotic <c>^N</c>/<c>^xRGB</c> color codes (via <see cref="HudText"/>), with a configurable
/// horizontal alignment (QC passed <c>align 0.5</c> = centered).
/// </summary>
public partial class MinigameHelpPanel : HudPanel
{
    // -------------------------------------------------------------------------------------------------
    //  Public data surface (driven by the net/match layer — see the integration owner, not this file)
    // -------------------------------------------------------------------------------------------------

    private string? _activeMinigame;

    /// <summary>
    /// The active minigame's id (QC <c>active_minigame.descriptor.netname</c>). May be a bare id (<c>"ttt"</c>)
    /// or a session netname that begins with one (<c>"ttt_3"</c>); both resolve to the same help text. Set to
    /// <c>null</c>/empty when no minigame is running — the panel then draws nothing. Setting it triggers a redraw.
    /// </summary>
    public string? ActiveMinigame
    {
        get => _activeMinigame;
        set
        {
            string? norm = Normalize(value);
            if (norm == _activeMinigame) return;
            _activeMinigame = norm;
            QueueRedraw();
        }
    }

    private string? _helpOverride;

    /// <summary>
    /// Optional verbatim help text (QC the literal <c>active_minigame.message</c>). When non-empty this is shown
    /// instead of the built-in per-game blurb — for an owner that composes its own help/status line. May carry
    /// <c>^N</c>/<c>^xRGB</c> color codes. Empty/null falls back to the built-in text for <see cref="ActiveMinigame"/>.
    /// </summary>
    public string? HelpOverride
    {
        get => _helpOverride;
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (v == _helpOverride) return;
            _helpOverride = v;
            QueueRedraw();
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Built-in per-game help text (faithful to the REGISTER_MINIGAME set + each game's rules)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The static rules/help blurb for each shipped minigame, keyed by the <c>REGISTER_MINIGAME</c> id. The
    /// first line is the game title (yellow <c>^3</c>); the body explains the goal + controls. Color codes match
    /// the engine palette so they render the same as QC's <c>drawcolorcodedstring</c>.
    /// </summary>
    private static readonly Dictionary<string, string> HelpByGame = new()
    {
        // Tic Tac Toe — ttt.qc (REGISTER_MINIGAME(ttt, _("Tic Tac Toe")))
        ["ttt"] =
            "^3Tic Tac Toe\n" +
            "Get three of your pieces in a row — horizontally, vertically or diagonally — to win. " +
            "Click an empty cell on the 3x3 board to place a piece. Players take turns.",

        // Pong — pong.qc (REGISTER_MINIGAME(pong, _("Pong")))
        ["pong"] =
            "^3Pong\n" +
            "Bounce the ball past your opponent's paddle to score. Move your paddle with the " +
            "^5arrow keys^7. Press ^1Start Match^7 in the menu to begin; add AI players to fill empty seats.",

        // Nine Men's Morris — nmm.qc (REGISTER_MINIGAME(nmm, _("Nine Men's Morris")))
        ["nmm"] =
            "^3Nine Men's Morris\n" +
            "Place your nine pieces, then slide them along the lines to form a mill (three in a row). " +
            "Each mill you make lets you remove one of the opponent's pieces. Reduce your opponent to " +
            "two pieces, or block all their moves, to win.",

        // Connect Four — c4.qc (REGISTER_MINIGAME(c4, _("Connect Four")))
        ["c4"] =
            "^3Connect Four\n" +
            "Drop your pieces into the columns and be the first to line up four in a row — " +
            "horizontally, vertically or diagonally. Click a column to drop a piece into it. Players take turns.",

        // Push-Pull — pp.qc (REGISTER_MINIGAME(pp, _("Push-Pull")))
        ["pp"] =
            "^3Push-Pull\n" +
            "A reversi-style game: outflank a line of the opponent's pieces between two of yours to flip " +
            "them to your color. Click an empty cell to place a piece. The player with the most pieces wins.",

        // Peg Solitaire — ps.qc (REGISTER_MINIGAME(ps, _("Peg Solitaire")))
        ["ps"] =
            "^3Peg Solitaire\n" +
            "Jump one peg over an adjacent peg into an empty hole, removing the peg you jumped. " +
            "Click a peg then an empty hole two cells away to move. Clear the board down to a single peg to win.",

        // Bulldozer — bd.qc (REGISTER_MINIGAME(bd, _("Bulldozer")))
        ["bd"] =
            "^3Bulldozer\n" +
            "Push every box onto a target tile to clear the level. Move with the ^5arrow keys^7; " +
            "you can only push one box at a time and never pull. Use the menu to restart or pick a new level.",
    };

    // -------------------------------------------------------------------------------------------------
    //  Behaviour cvars (QC HUD_MinigameHelp_Export — aesthetic tunables saved into hud skin files)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Register this panel's behaviour-cvar defaults (invoked by reflection from <see cref="HudConfig"/>).
    /// QC drew the help text with <c>align = 0.5</c> (centered) at the panel font size; the port exposes that
    /// horizontal alignment as a tunable so a skin can left-align it instead.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        // 0 = left, 0.5 = centered (the QC default), 1 = right. Saved into the hud skin file like the QC export.
        c.Register("hud_panel_minigamehelp_align", "0.5", CvarFlags.Save);
    }

    // -------------------------------------------------------------------------------------------------
    //  Draw (QC HUD_MinigameHelp body)
    // -------------------------------------------------------------------------------------------------

    protected override void DrawPanel()
    {
        // QC: if ( !help_message ) return;  — self-blank when there's nothing to show.
        string? help = ResolveHelp();
        if (string.IsNullOrEmpty(help)) return;

        // Skin 9-slice frame (QC HUD_Panel_DrawBg). No-op when the luma bg is "0".
        DrawBackground();

        // QC: pos/mySize shrink by panel_bg_padding before drawing the text.
        float pad = Cfg.Padding;
        var inner = new Rect2(pad, pad, Mathf.Max(8f, Size2.X - pad * 2f), Mathf.Max(8f, Size2.Y - pad * 2f));

        int size = Cfg.FontSize;
        float align = Mathf.Clamp(CvarF("align", 0.5f), 0f, 1f); // QC minigame_drawcolorcodedstring_wrapped align

        DrawWrappedColored(inner, help!, FgColor, size, align);
    }

    /// <summary>Pick the text to show: the verbatim override, else the built-in blurb for the active game.</summary>
    private string? ResolveHelp()
    {
        if (!string.IsNullOrWhiteSpace(_helpOverride))
            return _helpOverride;
        if (string.IsNullOrEmpty(_activeMinigame))
            return null;
        return HelpByGame.TryGetValue(_activeMinigame!, out string? text) ? text : null;
    }

    /// <summary>
    /// Word-wrap <paramref name="text"/> to the inner width and draw each line color-coded, aligned by
    /// <paramref name="align"/> (0 left / 0.5 center / 1 right) — the port of QC
    /// <c>minigame_drawcolorcodedstring_wrapped</c>. Honors embedded newlines as hard breaks.
    /// </summary>
    private void DrawWrappedColored(Rect2 area, string text, Color baseColor, int size, float align)
    {
        float lineH = size + 3f;
        float y = area.Position.Y;
        float maxW = area.Size.X;

        foreach (string rawParagraph in text.Split('\n'))
        {
            // External override text may use CRLF; drop the stray CR so it never renders as a control glyph.
            string paragraph = rawParagraph.TrimEnd('\r');
            // QC carries the active color code across a wrap (find_last_color_code → prepend to remainder), so a
            // color set on one wrapped line continues onto the next. We replicate that per paragraph.
            string carry = "";
            foreach (string line in WrapLine(paragraph, maxW, size))
            {
                if (y + lineH > area.Position.Y + area.Size.Y) return; // clip to panel (QC stops at panel_size)
                string toDraw = carry + line;
                DrawColoredLine(new Vector2(area.Position.X, y), maxW, toDraw, baseColor, size, align);
                carry = FindLastColorCode(toDraw);
                y += lineH;
            }
        }
    }

    /// <summary>
    /// Greedy word-wrap a single (color-coded) line to <paramref name="maxW"/>, measuring on the visible glyphs
    /// only (QC <c>stringwidth_colors</c> ignores the codes). Returns at least one line so an unwrappable long
    /// word still shows.
    /// </summary>
    private static List<string> WrapLine(string line, float maxW, int size)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) { result.Add(""); return result; }

        string[] words = line.Split(' ');
        var cur = new StringBuilder();
        foreach (string word in words)
        {
            string candidate = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length > 0 && MeasureText(HudText.Strip(candidate), size) > maxW)
            {
                result.Add(cur.ToString());
                cur.Clear();
                cur.Append(word);
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
    /// Return the trailing <c>^N</c>/<c>^xRGB</c> color code still in effect at the end of <paramref name="s"/>
    /// (QC <c>find_last_color_code</c>), or "" if none — so the next wrapped line can resume that color.
    /// </summary>
    private static string FindLastColorCode(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] != '^') continue;
            // Count consecutive carets so an escaped run (^^) is not mistaken for a code.
            int carets = 1;
            while (i - carets >= 0 && s[i - carets] == '^') carets++;
            if ((carets & 1) == 0) { i -= carets - 1; continue; } // all carets escaped → keep scanning before them

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
    /// Draw one already-wrapped line of color-coded text. We measure the visible width, place the origin for the
    /// requested <paramref name="align"/>, then lay the <c>^N</c> runs left-to-right (Godot can't align a
    /// multi-color string in one call).
    /// </summary>
    private void DrawColoredLine(Vector2 origin, float width, string line, Color baseColor, int size, float align)
    {
        List<HudText.Run> runs = HudText.Parse(line, baseColor);
        if (runs.Count == 0) return;

        float visW = MeasureText(HudText.Strip(line), size);
        // QC's drawcolorcodedstring_wrapped keeps text inside the panel; a single word wider than the inner
        // width (or any align>0 on such a line) would otherwise push the centered/aligned start LEFT of the
        // panel's left edge and spill text off-panel over neighbours. Never start left of the panel.
        float slack = width - visW;
        if (slack < 0f) slack = 0f;
        float cx = origin.X + slack * align;
        foreach (HudText.Run run in runs)
        {
            DrawText(new Vector2(cx, origin.Y), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    /// <summary>
    /// Reduce a raw id/session-netname to its base game id (QC's descriptor netname). Sessions are named
    /// <c>"&lt;game&gt;_&lt;n&gt;"</c>, so a leading known-game token wins; a bare id passes through. Returns
    /// null for empty/unknown input.
    /// </summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw!.Trim().ToLowerInvariant();
        if (HelpByGame.ContainsKey(s)) return s;

        // Session netname like "ttt_3" → "ttt"; otherwise keep the raw token (lets HelpOverride still drive draw).
        int us = s.IndexOf('_');
        if (us > 0)
        {
            string head = s[..us];
            if (HelpByGame.ContainsKey(head)) return head;
        }
        return s;
    }
}
