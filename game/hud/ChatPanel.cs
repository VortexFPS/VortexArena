using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// In-HUD chat history — port of Base/.../qcsrc/client/hud/panel/chat.qc (HUD panel #12).
///
/// The QC <c>HUD_Chat</c> did NOT render the lines itself: it sized/positioned the engine's built-in chat
/// area by writing the engine cvars <c>con_chatrect</c>/<c>con_chatrect_x/_y</c>/<c>con_chatwidth</c>/
/// <c>con_chat</c> from <c>panel_pos</c>/<c>panel_size</c> and let DarkPlaces draw the recent chat lines
/// (faded after <c>con_chattime</c>, sized by <c>con_chatsize</c>, up to <c>con_chat</c> rows). The port has
/// no engine chat area, so this panel keeps its own ring of recent chat lines and renders them itself with
/// the Xolonium HUD font + Xonotic <c>^N</c> color codes (via <see cref="HudText"/>), faithfully reproducing
/// the engine semantics that QC relied on:
/// <list type="bullet">
///   <item><b>con_chatsize</b> (default 8) — the per-line font size, in luma reference units. We scale it to
///         pixels the same way <see cref="HudPanel"/> scales <c>hud_fontsize</c> (× viewport / hud_width).</item>
///   <item><b>con_chat</b> = <c>floor(panelHeight / con_chatsize - 0.5)</c> — the max number of visible rows
///         (QC computed this from the panel size; we derive the same cap from our pixel height).</item>
///   <item><b>con_chattime</b> (default 30) — seconds a line stays before it fades out and is dropped. A
///         value of 0 means "infinite" (the menu's "Infinite" entry), so lines never expire.</item>
///   <item><b>con_chatsound</b> (default 1) — whether an incoming chat line beeps (the wiring of the actual
///         sound is the net/notification layer's job; we only expose <see cref="ChatSoundEnabled"/>).</item>
/// </list>
///
/// Pipeline: the net/chat layer calls <see cref="AddLine"/> with one already-formatted chat line (it may
/// carry <c>^N</c> color codes, e.g. a team-colored <c>name^7: message</c>, or the engine's team/spectator
/// channel prefixes). The panel self-manages fade + expiry and draws nothing when empty.
/// </summary>
public partial class ChatPanel : HudPanel
{
    /// <summary>Engine cvar default (QC <c>con_chattime</c>): seconds a line shows before fading. 0 = forever.</summary>
    public const float DefaultChatTime = 30f;

    /// <summary>Engine cvar default (QC <c>con_chatsize</c>): per-line font size in luma reference units.</summary>
    public const float DefaultChatSize = 8f;

    /// <summary>Tail of the display window spent fading the line out (engine-style soft expiry), in seconds.</summary>
    private const float FadeTime = 1f;

    /// <summary>Hard cap on retained lines regardless of con_chat (matches the engine's chat scrollback bound).</summary>
    private const int MaxStored = 64;

    /// <summary>Hard cap on a single stored line's character length. Chat lines arrive from the network
    /// (<c>ServerPrint</c>, attacker-controllable) and feed <see cref="MeasureText"/>/<see cref="TruncateToWidth"/>
    /// every frame in the draw path; <see cref="TruncateToWidth"/> is O(n²) in the overflowing run, so an
    /// unbounded line would hang the HUD. The line is clipped to width anyway, so anything past this is unseeable.</summary>
    private const int MaxLineChars = 1024;

    /// <summary>Hard cap on how many rows a single <see cref="AddLine"/> call may contribute (a hostile message
    /// could embed thousands of <c>\n</c>). The ring is bounded by <see cref="MaxStored"/> regardless, but this
    /// bounds the transient allocation/scan for one call.</summary>
    private const int MaxLinesPerAdd = 128;

    private sealed class Line
    {
        public string Text = "";
        public double Time; // _now when added
    }

    // Oldest first, newest last (we render bottom-up so the newest sits at the panel's bottom edge).
    private readonly List<Line> _lines = new();
    private double _now;

    /// <summary>QC <c>con_chattime</c> (engine cvar): seconds before a line fades; 0 = infinite. Read live.</summary>
    private float ChatTime => GlobalF("con_chattime", DefaultChatTime);

    /// <summary>QC <c>con_chatsize</c> (engine cvar): per-line font size in luma units. Read live.</summary>
    private float ChatSize => GlobalF("con_chatsize", DefaultChatSize);

    /// <summary>QC <c>con_chatsound</c> (engine cvar): whether an incoming line should beep. Read live by the
    /// chat/net layer (we don't own the sound device); exposed so the caller can honour it.</summary>
    public static bool ChatSoundEnabled => GlobalF("con_chatsound", 1f) != 0f;

    /// <summary>
    /// Push one chat line into the history (QC the engine's chat ring, fed by <c>Cmd_AddCommand("chat")</c> /
    /// the <c>chat</c> notification path). The text may carry Xonotic <c>^N</c> color codes (e.g. a
    /// team-colored sender name, the spectator channel prefix). Empty input is ignored. The line fades out
    /// after <c>con_chattime</c> and is dropped; the panel self-blanks once the ring is empty.
    /// </summary>
    public void AddLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        bool added = false;

        // Split on embedded newlines so a multi-line message occupies multiple rows (engine behavior).
        // Bound the split count so a hostile message full of '\n' can't allocate/scan an unbounded array.
        if (text.IndexOf('\n') < 0)
        {
            added |= TryAddRow(text);
        }
        else
        {
            string[] rows = text.Split('\n', MaxLinesPerAdd + 1);
            int count = Mathf.Min(rows.Length, MaxLinesPerAdd);
            for (int i = 0; i < count; i++)
                added |= TryAddRow(rows[i]);
        }

        if (!added) return; // every row was blank → nothing to show, don't redraw

        if (_lines.Count > MaxStored)
            _lines.RemoveRange(0, _lines.Count - MaxStored);

        QueueRedraw();
    }

    /// <summary>Normalize and store one row (strip the CR pair, drop blank rows so they don't eat a visible
    /// row slot drawing nothing, and clamp the length so the per-frame text measure/truncate stays bounded).
    /// Returns whether a row was actually added.</summary>
    private bool TryAddRow(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        string row = raw.TrimEnd('\r');
        if (row.Length == 0) return false;          // blank line: skip (engine collapses these too)
        if (row.Length > MaxLineChars)
            row = row.Substring(0, MaxLineChars);    // clip unbounded network text out of the draw path
        _lines.Add(new Line { Text = row, Time = _now });
        return true;
    }

    /// <summary>Drop every chat line (e.g. on disconnect / map change). The panel then draws nothing.</summary>
    public void ClearAll()
    {
        if (_lines.Count == 0) return;
        _lines.Clear();
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _now += delta;

        // Expire faded-out lines (skip entirely when con_chattime is 0 = infinite).
        float life = ChatTime;
        if (life > 0f && _lines.Count > 0)
        {
            double cutoff = _now - (life + FadeTime);
            int drop = 0;
            // Lines are time-ordered oldest-first, so we can drop a contiguous head.
            while (drop < _lines.Count && _lines[drop].Time <= cutoff) drop++;
            if (drop > 0)
            {
                _lines.RemoveRange(0, drop);
                QueueRedraw();
            }
        }
    }

    protected override void DrawPanel()
    {
        if (_lines.Count == 0) return; // self-blank when empty

        DrawBackground(); // skin frame (luma chat default bg is "0" → this is a no-op there)

        float padding = Cfg.Padding;
        float innerW = Size2.X - padding * 2f;
        float innerH = Size2.Y - padding * 2f;
        if (innerW <= 1f || innerH <= 1f) return;

        int fontPx = ResolveFontPx();
        float rowH = fontPx + 2f;

        // QC con_chat: max visible rows = floor(panelHeight / con_chatsize - 0.5).
        int maxRows = Mathf.Max(1, Mathf.FloorToInt(innerH / rowH - 0.5f));

        float life = ChatTime;

        // Render newest at the bottom and walk upward, so the latest message is always anchored to the
        // panel's lower edge (engine chat behavior). Stop once we run out of vertical room or rows.
        float baseY = padding + innerH;
        int shown = 0;

        for (int i = _lines.Count - 1; i >= 0 && shown < maxRows; i--)
        {
            Line ln = _lines[i];
            float alpha = LineAlpha(ln, life);
            if (alpha <= 0.01f) continue;

            float y = baseY - rowH * (shown + 1);
            if (y < padding - 0.5f) break; // would clip above the panel top

            DrawChatLine(ln.Text, new Vector2(padding, y), innerW, fontPx, alpha);
            shown++;
        }
    }

    /// <summary>
    /// Per-line opacity: full while within <c>con_chattime</c>, then a linear fade over <see cref="FadeTime"/>
    /// seconds, then gone. An infinite lifetime (<c>con_chattime</c> 0) keeps every line fully opaque.
    /// The opacity is further scaled by the panel's live fg alpha (hud_panel_fg_alpha × HUD fade).
    /// </summary>
    private float LineAlpha(Line ln, float life)
    {
        float a = 1f;
        if (life > 0f)
        {
            double age = _now - ln.Time;
            if (age >= life)
                a = Mathf.Clamp(1f - (float)(age - life) / FadeTime, 0f, 1f);
        }
        return a * LiveFgAlpha;
    }

    /// <summary>
    /// Draw one chat line left-aligned, honoring its <c>^N</c> color runs and clipping to width (the engine
    /// shortened the line to <c>mySize.x</c> via <c>textShortenToWidth</c>; we trim the run that overflows so
    /// text never bleeds past the panel's right edge instead of stopping the whole line at a run boundary).
    /// </summary>
    private void DrawChatLine(string text, Vector2 pos, float maxWidth, int fontPx, float alpha)
    {
        var baseColor = new Color(1f, 1f, 1f, alpha);
        List<HudText.Run> runs = HudText.Parse(text, baseColor);
        if (runs.Count == 0) return;

        float x = pos.X;
        float limit = pos.X + maxWidth;
        foreach (HudText.Run r in runs)
        {
            if (x >= limit) break;

            Color c = r.Color;
            c.A = alpha;

            string piece = r.Text;
            float w = MeasureText(piece, fontPx);
            if (x + w > limit)
            {
                // This run overflows: trim it character-by-character to what still fits, then stop the line.
                piece = TruncateToWidth(piece, limit - x, fontPx);
                if (piece.Length > 0)
                    DrawText(new Vector2(x, pos.Y), piece, c, fontPx);
                break;
            }

            DrawText(new Vector2(x, pos.Y), piece, c, fontPx);
            x += w;
        }
    }

    /// <summary>Longest leading substring of <paramref name="text"/> whose pixel width fits in
    /// <paramref name="maxWidth"/> (the modernized <c>textShortenToWidth</c> for a single color run).</summary>
    private static string TruncateToWidth(string text, float maxWidth, int fontPx)
    {
        if (maxWidth <= 0f || string.IsNullOrEmpty(text)) return "";
        for (int n = text.Length; n > 0; n--)
        {
            string sub = text.Substring(0, n);
            if (MeasureText(sub, fontPx) <= maxWidth) return sub;
        }
        return "";
    }

    /// <summary>
    /// con_chatsize is authored in the same luma reference units as hud_fontsize, so scale it to device
    /// pixels the same way <see cref="HudPanel"/> resolves the body font (× viewport.X / hud_width, ref 800).
    /// </summary>
    private int ResolveFontPx()
    {
        float refW = GlobalF("hud_width", 0f);
        if (!(refW > 0f)) refW = 800f;            // also rejects NaN
        float vpW = GetViewportRect().Size.X;
        if (!(vpW > 0f)) vpW = 800f;
        float size = ChatSize;
        if (!(size > 0f)) size = DefaultChatSize;  // guard con_chatsize 0 / negative / NaN before scaling
        float scaled = size * vpW / refW;
        if (!float.IsFinite(scaled)) scaled = DefaultChatSize;
        int px = Mathf.RoundToInt(scaled);
        return Mathf.Clamp(px, 8, 64);
    }

    /// <summary>
    /// Register the engine chat cvars this panel reads (QC engine cvars, NOT <c>hud_panel_chat_*</c>).
    /// HudConfig invokes this by reflection. Idempotent: <see cref="CvarService.Register"/> keeps an existing
    /// value, so seeding the stock defaults here never clobbers a user/menu setting.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        if (c is null) return;
        const CvarFlags save = CvarFlags.Save;
        c.Register("con_chatsize", "8", save);
        c.Register("con_chattime", "30", save);
        c.Register("con_chatsound", "1", save);
    }
}
