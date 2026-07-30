using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Hud;

/// <summary>
/// Kill feed — faithful port of Base/.../qcsrc/client/hud/panel/notify.qc (HUD panel #4, <c>HUD_Notify</c>).
/// The QC version keeps a ring of the most-recent obituary lines, each
/// "<c>attacker [weapon/death icon] victim</c>", pushed by <c>HUD_Notify_Push(icon, attacker, victim)</c>
/// from the MSG_INFO notification path, and fades each line out independently after
/// <c>hud_panel_notify_time</c> over <c>hud_panel_notify_fadetime</c>. This port reproduces the same ring +
/// per-line fade + layout:
/// <list type="bullet">
///   <item>The drawable <b>entry count</b> is derived from the panel aspect each frame —
///         <c>bound(1, floor(MAX * size.y/size.x), MAX)</c> — exactly like the QC (a tall panel shows more
///         lines, a wide one fewer).</item>
///   <item>The weapon/death icon sits <b>between</b> attacker and victim, horizontally centered, sized
///         <c>vec2(icon_aspect, 1) * entry_height</c> (<c>hud_panel_notify_icon_aspect</c>).</item>
///   <item>The attacker name is right-aligned to the left half, the victim name left-aligned to the right
///         half; both are <b>team-/^N-colored</b> via <see cref="HudText"/> and shortened to the half width.</item>
///   <item><c>hud_panel_notify_flip</c> orders lines top-down (newest at top) vs bottom-up (newest at bottom).</item>
///   <item>Body text scales with the panel via <c>0.5 * entry_height * hud_panel_notify_fontsize</c>
///         (luma <c>_fontsize 0.8</c>).</item>
/// </list>
///
/// Pipeline: the notification/net layer calls <see cref="Push"/> with attacker/victim names (which may carry
/// <c>^N</c>/<c>^xRGB</c> color codes) and an icon name, or <see cref="PushDeath"/> with the resolved MSG_INFO
/// death <see cref="Notification"/> (its <see cref="Notification.Icon"/> is the QC kill-notify icon, e.g.
/// <c>notify_death</c>, <c>notify_selfkill</c>, <c>weaponvortex</c>). The icon is resolved to a texture via
/// <see cref="TextureCache"/> (kill-notify art under <c>gfx/hud/&lt;skin&gt;/&lt;icon&gt;</c>, default-skin
/// fallback); when it is missing we fall back to a "»" separator glyph so the line is never broken.
/// </summary>
public partial class NotifyPanel : HudPanel
{
    /// <summary>QC <c>NOTIFY_MAX_ENTRIES</c> — the ring capacity (max lines ever kept/shown).</summary>
    private const int MaxEntries = 10;

    /// <summary>QC <c>NOTIFY_ICON_MARGIN</c> — gap between the centered icon and each name, as a fraction of
    /// the panel width.</summary>
    private const float IconMargin = 0.02f;

    /// <summary>Luma default for <c>hud_panel_notify_time</c> — how long a line stays fully opaque (seconds).
    /// Kept as a public constant for back-compat; the live value is read from the cvar each frame.</summary>
    public const float HoldTime = 10f;

    /// <summary>Luma default for <c>hud_panel_notify_fadetime</c> — fade-out duration after the hold.</summary>
    private const float FadeTime = 3f;

    private sealed class Entry
    {
        public string Attacker = "";
        public string Victim = "";
        public string Icon = "";
        public double Time;
    }

    private readonly List<Entry> _entries = new();
    private double _now;

    /// <summary>Register the panel's behaviour-cvar defaults (luma values from hud_luma.cfg). Auto-invoked by
    /// reflection from <c>HudConfig.RegisterDefaults</c>.</summary>
    public static void RegisterDefaults(VortexArena.Engine.Simulation.CvarService c)
    {
        const VortexArena.Common.Services.CvarFlags save = VortexArena.Common.Services.CvarFlags.Save;
        c.Register("hud_panel_notify_flip", "0", save);
        c.Register("hud_panel_notify_fontsize", "0.8", save);
        c.Register("hud_panel_notify_time", "10", save);
        c.Register("hud_panel_notify_fadetime", "3", save);
        c.Register("hud_panel_notify_icon_aspect", "1", save);
    }

    /// <summary>
    /// Push a kill-feed line (QC <c>HUD_Notify_Push(icon, attacker, victim)</c>). A null/empty
    /// <paramref name="attacker"/> denotes a non-kill death (suicide / environment): the victim is shown alone
    /// on the victim side (the QC swaps attacker→victim in that case; here we simply leave attacker empty and
    /// only draw the victim). Names may contain <c>^N</c>/<c>^xRGB</c> codes.
    /// </summary>
    public void Push(string? attacker, string victim, string icon = "")
    {
        if (string.IsNullOrEmpty(victim)) return;
        _entries.Insert(0, new Entry
        {
            Attacker = attacker ?? "",
            Victim = victim,
            Icon = icon,
            Time = _now,
        });
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        QueueRedraw();
    }

    /// <summary>
    /// Push a death using a MSG_INFO death <see cref="Notification"/> (carries the kill-notify
    /// <see cref="Notification.Icon"/>). The attacker/victim names come from the notification's s1/s2 args,
    /// supplied by the caller (the net layer resolves entcs names; they may carry ^N color codes).
    /// </summary>
    public void PushDeath(Notification death, string? attacker, string victim)
        => Push(attacker, victim, death?.Icon ?? "");

    public override void _Process(double delta)
    {
        // delta can be NaN/Inf on a stalled frame; never poison the clock (would freeze fades / wedge expiry).
        if (double.IsFinite(delta) && delta > 0d) _now += delta;
        if (_entries.Count == 0) return;

        // Expire fully-faded lines (QC drops them off the ring once alpha hits 0).
        // total is always finite here (LiveHoldTime/LiveFadeTime sanitize cvar input); a NaN would make the
        // `>=` always false and leak un-expiring lines.
        float total = LiveHoldTime() + LiveFadeTime();
        int before = _entries.Count;
        _entries.RemoveAll(e => _now - e.Time >= total);
        if (_entries.Count != before) QueueRedraw();
    }

    /// <summary>Coerce a cvar-derived value to a finite, non-negative number so malformed cvars
    /// (e.g. <c>set hud_panel_notify_time nan</c> / a negative / Infinity) can never produce NaN/Inf
    /// geometry or fade math in the every-frame draw path.</summary>
    private static float Finite(float v, float fallback) =>
        float.IsFinite(v) ? Mathf.Max(0f, v) : Mathf.Max(0f, fallback);

    // QC: float fade_start = max(0, autocvar_hud_panel_notify_time);
    private float LiveHoldTime() => Finite(CvarF("time", HoldTime), HoldTime);

    // QC: float fade_time = max(0, autocvar_hud_panel_notify_fadetime);
    private float LiveFadeTime() => Finite(CvarF("fadetime", FadeTime), FadeTime);

    protected override void DrawPanel()
    {
        // Self-blank: nothing queued → draw nothing (QC `notify_count == 0` early-out).
        if (_entries.Count == 0) return;

        float w = Size2.X;
        float h = Size2.Y;
        // Self-blank on a degenerate / non-finite panel rect (Size2 comes from the cvar layout resolver).
        if (!(w > 0f) || !(h > 0f) || !float.IsFinite(w) || !float.IsFinite(h)) return;

        float fadeStart = LiveHoldTime();
        float fadeTime = LiveFadeTime();
        // Sanitize cvar-driven geometry: a NaN/Inf/negative icon_aspect or fontsize would propagate into every
        // coordinate below (icon size, name widths, font px) and draw garbage or off-panel. QC: max(1, aspect).
        float iconAspect = Mathf.Max(1f, Finite(CvarF("icon_aspect", 1f), 1f));
        float fontMul = Finite(CvarF("fontsize", 0.8f), 0.8f);
        bool flip = CvarBool("flip");

        // QC: entry_count = bound(1, floor(NOTIFY_MAX_ENTRIES * size.y / size.x), NOTIFY_MAX_ENTRIES).
        // FloorToInt of a huge ratio (very narrow panel) can overflow to a negative int; the Clamp re-floors it
        // to 1, but compute the ratio first so the float math is well-defined (w > 0 guaranteed above).
        float ratio = MaxEntries * h / w;
        int entryCount = float.IsFinite(ratio)
            ? Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ratio, MaxEntries)), 1, MaxEntries)
            : 1;
        float entryHeight = h / entryCount;

        float panelWidthHalf = w * 0.5f;
        float iconWidthHalf = entryHeight * iconAspect * 0.5f;
        float nameMaxWidth = panelWidthHalf - iconWidthHalf - w * IconMargin;
        if (nameMaxWidth < 1f) nameMaxWidth = 1f;

        // QC font_size = '0.5 0.5 0' * entry_height * fontsize → a px height we feed the Xolonium font.
        int fontPx = Mathf.Max(6, Mathf.RoundToInt(0.5f * entryHeight * fontMul));

        // QC icon_size = vec2(icon_aspect, 1) * entry_height
        var iconSize = new Vector2(iconAspect * entryHeight, entryHeight);
        float iconLeftX = panelWidthHalf - iconWidthHalf;     // icon box left edge
        float attackerRightX = nameMaxWidth;                  // attacker name ends here
        float victimLeftX = w - nameMaxWidth;                 // victim name begins here

        float fgAlpha = LiveFgAlpha;

        // QC ordering: flip → top-down from the newest (i=0..entry_count); else bottom-up (i=entry_count-1..0).
        // _entries[0] is the newest. We iterate the newest `entryCount` lines and place each at row `slot`.
        for (int idx = 0; idx < _entries.Count && idx < entryCount; idx++)
        {
            Entry e = _entries[idx];

            float age = (float)(_now - e.Time);
            if (!float.IsFinite(age) || age < 0f) age = 0f; // never feed NaN/negative into the fade math
            float alpha;
            if (age <= fadeStart)
            {
                alpha = 1f;
            }
            else if (fadeTime > 0f)
            {
                // QC: bound(0, (times + fade_start + fade_time - time) / fade_time, 1)
                alpha = Mathf.Clamp((fadeStart + fadeTime - age) / fadeTime, 0f, 1f);
                if (alpha <= 0f) break; // older lines are even more faded
            }
            else
            {
                break; // no fade time → line vanishes the instant the hold ends
            }
            if (alpha <= 0.003f) continue;

            // Row slot: newest at top when flipped, newest at bottom otherwise.
            int slot = flip ? idx : (entryCount - 1 - idx);
            float rowTop = slot * entryHeight;
            DrawObituaryLine(e, rowTop, entryHeight, fontPx, alpha * fgAlpha,
                iconSize, iconLeftX, attackerRightX, victimLeftX, nameMaxWidth);
        }
    }

    /// <summary>
    /// Draw one obituary line at <paramref name="rowTop"/>: the attacker name right-aligned into the left half,
    /// the weapon/death icon centered, the victim name left-aligned into the right half. The icon is the
    /// kill-notify texture (QC <c>drawpic_aspect_skin</c>); when the art is missing we substitute a "»" glyph.
    /// Names render with their ^N/^xRGB color codes, shortened to the half width like the QC.
    /// </summary>
    private void DrawObituaryLine(Entry e, float rowTop, float entryHeight, int fontPx, float alpha,
        Vector2 iconSize, float iconLeftX, float attackerRightX, float victimLeftX, float nameMaxWidth)
    {
        var baseColor = new Color(1f, 1f, 1f, alpha);

        // QC: name_top = 0.5 * (entry_height - font_size.y) — vertically center the text in the row.
        // DrawText draws a baseline `size` px below the supplied top, so feed it the text-box top.
        float textTopY = rowTop + 0.5f * (entryHeight - fontPx);

        Texture2D? icon = TextureCache.GetFirst(IconPaths(e.Icon));

        // Icon between the two names (QC draws it whenever icon != "" && victim != "").
        if (icon is not null)
        {
            var iconPos = new Vector2(iconLeftX, rowTop + 0.5f * (entryHeight - iconSize.Y));
            DrawTextureRect(icon, new Rect2(iconPos, iconSize), tile: false, new Color(1f, 1f, 1f, alpha));
        }
        else if (!string.IsNullOrEmpty(e.Attacker))
        {
            // Fallback separator glyph centered in the icon box (only meaningful when there are two names).
            float sepW = MeasureText("»", fontPx);
            float sepX = iconLeftX + 0.5f * (iconSize.X - sepW);
            DrawText(new Vector2(sepX, textTopY), "»", baseColor, fontPx);
        }

        // Victim: left-aligned at victimLeftX, shortened to the half width (QC textShortenToWidth).
        List<HudText.Run> victim = ShortenRuns(HudText.Parse(e.Victim, baseColor), nameMaxWidth, fontPx);
        DrawRuns(victim, victimLeftX, textTopY, fontPx, alpha);

        // Attacker: right-aligned to attackerRightX, shortened to the half width. Omitted for lone deaths.
        if (!string.IsNullOrEmpty(e.Attacker))
        {
            List<HudText.Run> attacker = ShortenRuns(HudText.Parse(e.Attacker, baseColor), nameMaxWidth, fontPx);
            float attW = RunsWidth(attacker, fontPx);
            DrawRuns(attacker, attackerRightX - attW, textTopY, fontPx, alpha);
        }
    }

    /// <summary>Draw a list of colored runs left-to-right starting at <paramref name="x"/>, returning the
    /// advanced x. Each run keeps its parsed color but is forced to the line alpha.</summary>
    private float DrawRuns(List<HudText.Run> runs, float x, float y, int size, float alpha)
    {
        foreach (HudText.Run r in runs)
        {
            var c = r.Color; c.A = alpha;
            DrawText(new Vector2(x, y), r.Text, c, size);
            x += MeasureText(r.Text, size);
        }
        return x;
    }

    private static float RunsWidth(List<HudText.Run> runs, int size)
    {
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        return total;
    }

    /// <summary>
    /// Trim a parsed name to fit <paramref name="maxWidth"/> px, appending "…" when truncated (the modern
    /// stand-in for QC <c>textShortenToWidth</c>). Color codes are preserved per surviving run.
    /// </summary>
    private static List<HudText.Run> ShortenRuns(List<HudText.Run> runs, float maxWidth, int size)
    {
        if (maxWidth <= 0f) return runs;
        if (RunsWidth(runs, size) <= maxWidth) return runs;

        const string ell = "…";
        float ellW = MeasureText(ell, size);
        float budget = Mathf.Max(0f, maxWidth - ellW);

        var outRuns = new List<HudText.Run>(runs.Count);
        float used = 0f;
        foreach (HudText.Run r in runs)
        {
            if (used >= budget) break;
            float runW = MeasureText(r.Text, size);
            if (used + runW <= budget)
            {
                outRuns.Add(r);
                used += runW;
                continue;
            }
            // Partially fit this run, char by char.
            var sb = new System.Text.StringBuilder();
            float w = used;
            foreach (char ch in r.Text)
            {
                float cw = MeasureText(ch.ToString(), size);
                if (w + cw > budget) break;
                sb.Append(ch);
                w += cw;
            }
            if (sb.Length > 0)
                outRuns.Add(new HudText.Run(sb.ToString(), r.Color));
            break;
        }

        // Append the ellipsis tinted like the last surviving run (or white).
        Color tail = outRuns.Count > 0 ? outRuns[^1].Color : new Color(1f, 1f, 1f, 1f);
        outRuns.Add(new HudText.Run(ell, tail));
        return outRuns;
    }

    /// <summary>The HUD skin whose kill-notify art is preferred (QC <c>hud_skin</c>); default-skin is the fallback.
    /// Kept as a public back-compat hook; the LIVE skin (the one <see cref="HudManager"/> updates when the
    /// <c>hud_skin</c> cvar changes) is the static <c>HudSkin.SkinName</c>, which <see cref="IconPaths"/> prefers
    /// so the kill feed tracks skin switches like every other panel.</summary>
    public static string HudSkin { get; set; } = "luma";

    /// <summary>
    /// Candidate texture paths for a kill-notify icon name (QC <c>icon</c>, e.g. "notify_death",
    /// "weaponvortex"), best-first. The bare names resolve the REAL Xonotic kill-notify art from the mounted
    /// game data via <see cref="TextureCache"/>'s VFS resolver (<c>gfx/hud/&lt;skin&gt;/&lt;icon&gt;</c>), with a
    /// <c>res://</c> project override last. The active skin is the manager-updated
    /// <c>VortexArena.Game.Hud.HudSkin.SkinName</c> (the type — shadowed inside this class by the
    /// <see cref="HudSkin"/> back-compat property, so it must be fully qualified here); the back-compat property
    /// is consulted too in case external code pinned a different preferred skin.
    /// </summary>
    private static string[] IconPaths(string icon)
    {
        if (string.IsNullOrEmpty(icon)) return System.Array.Empty<string>();
        string liveSkin = VortexArena.Game.Hud.HudSkin.SkinName; // manager-updated active skin (e.g. "luma")
        string pinned = HudSkin;                                   // back-compat override property
        bool pinDiffers = !string.IsNullOrEmpty(pinned) && pinned != liveSkin;
        return pinDiffers
            ? new[]
            {
                $"gfx/hud/{pinned}/{icon}",      // VFS: explicitly pinned skin (back-compat)
                $"gfx/hud/{liveSkin}/{icon}",    // VFS: live active skin
                $"gfx/hud/default/{icon}",       // VFS: default-skin fallback
                $"res://art/hud/notify/{icon}.png", // project art override
            }
            : new[]
            {
                $"gfx/hud/{liveSkin}/{icon}",    // VFS: live active skin
                $"gfx/hud/default/{icon}",       // VFS: default-skin fallback
                $"res://art/hud/notify/{icon}.png", // project art override
            };
    }
}
