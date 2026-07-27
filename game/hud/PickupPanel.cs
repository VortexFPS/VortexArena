using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Recent item-pickup feed (HUD panel #26) — port of
/// Base/.../qcsrc/client/hud/panel/pickup.qc. The QC version keeps a SINGLE "most recently picked up"
/// slot (<c>last_pickup_item</c> + <c>last_pickup_count</c>): each pickup of the same item within
/// <c>hud_panel_pickup_time</c> accumulates the count (rendered as <c>(xN)</c>); picking up a different
/// item — or the window expiring — resets the count. The slot is drawn bottom-left as
/// <c>[timer] [icon] name (xN)</c> and fades out over <c>hud_panel_pickup_fade_out</c> seconds at the tail
/// of the display window, then self-blanks.
///
/// Pipeline: the net/match layer calls <see cref="Push"/> when the local player picks something up,
/// supplying the human item name (may carry <c>^N</c> color codes), the bare HUD icon art name (QC the
/// item's <c>model2</c>/<c>m_icon</c>, resolved via <see cref="TextureCache"/> over the skin→default
/// chain), and how many were picked up this event (ammo bundles etc.). The panel animates + expires the
/// slot entirely client-side, so it works on a pure <c>--connect</c> client with no local server entity.
///
/// Faithful to the reference:
/// <list type="bullet">
///   <item>display window = <c>min(5, hud_panel_pickup_time)</c> (luma default 3);</item>
///   <item>fade window = <c>min(display, hud_panel_pickup_fade_out)</c> (luma default 0.15);</item>
///   <item>icon height = <c>panel_size.y * hud_panel_pickup_iconsize</c> (luma 1.5), aspect-preserved,
///         vertically centered against the text;</item>
///   <item>optional leading timer (<c>hud_panel_pickup_showtimer</c>: 1 = always, 2 = spectating only).</item>
/// </list>
/// </summary>
public partial class PickupPanel : HudPanel
{
    // Luma defaults (registered below; read live each frame so console/menu edits take effect).
    private const float DefaultTime = 3f;
    private const float DefaultFadeOut = 0.15f;
    private const float DefaultIconSize = 1.5f;

    /// <summary>The current pickup slot (QC <c>last_pickup_item</c> + <c>last_pickup_count</c>).</summary>
    private string _name = "";
    private string _icon = "";
    private int _count;
    private double _pickupTime;     // QC STAT(LAST_PICKUP) — when this slot was last touched
    private double _now;            // monotonic client time (QC time)

    /// <summary>
    /// Whether the local client is spectating. Drives the <c>hud_panel_pickup_showtimer == 2</c> behaviour
    /// (timer shown only while spectating) — fed by the net/match layer. Default false (playing).
    /// </summary>
    public bool Spectating { get; set; }

    /// <summary>
    /// The frozen match-clock string for the live slot — the game time the pickup HAPPENED (QC
    /// <c>seconds_tostring(HUD_Pickup_Time(last_pickup_time))</c>: constant while displayed, NOT a running
    /// age). Stamped by <see cref="Push"/> from <see cref="MatchTimeProvider"/>; assignable directly by an
    /// integration layer that prefers to feed its own string. Null/empty falls back to the panel-clock freeze.
    /// </summary>
    public string? TimerText { get; set; }

    /// <summary>
    /// Supplies the CURRENT match-clock string in the timer panel's display convention (count-up elapsed, or
    /// count-down remaining when a timelimit runs — QC <c>HUD_Pickup_Time</c>). Sampled ONCE inside
    /// <see cref="Push"/> so the readout freezes at the event. Wired by the host to
    /// <see cref="TimerPanel.PickupTimeString"/>; null (unwired / timer unfed) → the panel-clock fallback.
    /// </summary>
    public System.Func<string?>? MatchTimeProvider { get; set; }

    /// <summary>
    /// Record a pickup (QC <c>Pickup_Update</c>). If it is the same item as the live slot AND the display
    /// window has not lapsed, the count accumulates and the fade timer restarts; otherwise the slot is
    /// replaced and the count starts fresh. <paramref name="itemName"/> may carry <c>^N</c> color codes;
    /// <paramref name="icon"/> is the bare HUD art name (skin→default resolved); <paramref name="count"/> is
    /// how many were granted by this event (clamped to ≥ 1).
    /// </summary>
    public void Push(string itemName, string icon, int count = 1)
    {
        if (string.IsNullOrEmpty(itemName) && string.IsNullOrEmpty(icon)) return;
        if (count < 1) count = 1;

        // QC Pickup_Update: the accumulation window is the RAW hud_panel_pickup_time (NOT the min(5,…)
        // clamp used for the on-screen display window). A user who sets time > 5 still accumulates count
        // for the full duration even though the slot only displays for 5s.
        float accumWindow = CvarF("time", DefaultTime);
        if (!float.IsFinite(accumWindow) || accumWindow < 0f) accumWindow = DefaultTime;

        // QC: reset the count when the item changed or the previous slot already expired.
        bool sameItem = _count > 0 && itemName == _name && icon == _icon;
        bool expired = _now - _pickupTime > accumWindow;
        if (!sameItem || expired)
            _count = 0;

        _name = itemName;
        _icon = icon;
        // Saturating add: a runaway/hostile pickup stream must never overflow _count into a negative value
        // (which would silently self-blank the panel via the _count > 0 checks).
        _count = (int)System.Math.Min((long)_count + count, int.MaxValue);
        _pickupTime = _now;
        // Freeze the match clock at THIS event (QC last_pickup_time is a fixed STAT; the drawn string never
        // ticks). Sampled here — not at draw — so the readout is the game time the pickup happened.
        TimerText = MatchTimeProvider?.Invoke();
        QueueRedraw();
    }

    /// <summary>Immediately clear the slot (e.g. on respawn / map change). Self-blanks next frame.</summary>
    public void Clear()
    {
        _count = 0;
        _name = "";
        _icon = "";
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        // Guard against a NaN/negative frame delta (paused/odd clock) corrupting our monotonic clock.
        if (double.IsFinite(delta) && delta > 0d) _now += delta;
        // Expire the slot once the full display window has lapsed so the panel self-blanks.
        if (_count > 0)
        {
            float displayTime = DisplayTime();
            if (_now - _pickupTime > displayTime)
            {
                _count = 0;
                QueueRedraw();
            }
        }
    }

    /// <summary>The on-screen display window = <c>min(5, hud_panel_pickup_time)</c>, clamped finite &amp; ≥ 0
    /// so a user-set NaN/Inf/negative cvar can never poison the fade-alpha or rect-size math downstream.</summary>
    private float DisplayTime()
    {
        float t = CvarF("time", DefaultTime);
        if (!float.IsFinite(t) || t < 0f) t = DefaultTime;
        return Mathf.Min(5f, t);
    }

    protected override void DrawPanel()
    {
        if (_count <= 0 || (string.IsNullOrEmpty(_name) && string.IsNullOrEmpty(_icon))) return; // self-blank

        float displayTime = DisplayTime();
        if (displayTime <= 0f) return; // window collapsed (hud_panel_pickup_time 0) → nothing to draw
        float age = (float)(_now - _pickupTime);
        if (!float.IsFinite(age)) return;       // clock corruption — draw nothing rather than garbage
        if (age < 0f) age = 0f;                  // clock went backwards (e.g. clamp/reset): treat as fresh
        if (age >= displayTime) return; // expired (guards a redraw between _Process ticks)

        DrawBackground(); // luma pickup bg is "0" → no-op; honours a user-set frame.

        // QC: fontsize = panel height; icon = fontsize * iconsize.
        float padding = Cfg.Padding;
        float topY = padding;
        float availW = Mathf.Max(0f, Size2.X - padding * 2f);
        float fontH = Mathf.Max(8f, Size2.Y - padding * 2f);
        int size = Mathf.Max(8, Mathf.RoundToInt(fontH));
        float iconSizeMulRaw = CvarF("iconsize", DefaultIconSize);
        if (!float.IsFinite(iconSizeMulRaw)) iconSizeMulRaw = DefaultIconSize; // NaN/Inf cvar → safe default
        float iconSizeMul = Mathf.Max(0.01f, iconSizeMulRaw);
        float iconH = fontH * iconSizeMul;

        // QC: a = 1 until the fade window, then ramps to 0 at the end of the display window.
        float fadeOutCvar = CvarF("fade_out", DefaultFadeOut);
        if (!float.IsFinite(fadeOutCvar)) fadeOutCvar = DefaultFadeOut; // NaN/Inf cvar → safe default
        // fadeOut is strictly positive (>= 0.0001) AND <= displayTime (>0), so the divide below can't be /0.
        float fadeOut = Mathf.Min(displayTime, Mathf.Max(0.0001f, fadeOutCvar));
        float a = age < displayTime - fadeOut ? 1f : Mathf.Clamp((displayTime - age) / fadeOut, 0f, 1f);
        float alpha = LiveFgAlpha * a;
        if (!(alpha > 0.001f)) return; // also bails on NaN alpha (NaN > x is false)

        var white = new Color(1f, 1f, 1f, alpha);
        float x = padding;

        // ---- optional leading match timer (QC hud_panel_pickup_showtimer) ----
        // QC: outer `if (showtimer)` then `(showtimer == 1 && !(serverflags & SERVERFLAG_FORBID_PICKUPTIMER))
        // || spectatee_status` — i.e. show when showtimer is nonzero AND (it is 1 AND the server doesn't forbid
        // the timer, OR we are spectating). 2 = "spectating only"; the forbid serverflag forces that behavior.
        // serverflags is the server's published bitfield (GameWorld.ReadLevelCvars → the `serverflags` cvar,
        // read here from the shared store on a listen server).
        bool forbidTimer = (Mathf.RoundToInt(GlobalF("serverflags", 0f))
            & XonoticGodot.Common.Gameplay.ServerFlags.ForbidPickupTimer) != 0;
        float showTimerRaw = CvarF("showtimer", 1f);
        int showTimer = float.IsFinite(showTimerRaw) ? Mathf.RoundToInt(showTimerRaw) : 1;
        if (showTimer != 0 && ((showTimer == 1 && !forbidTimer) || Spectating))
        {
            string timer = ResolveTimerText();
            if (!string.IsNullOrEmpty(timer))
            {
                DrawText(new Vector2(x, topY), timer, white, size);
                x += MeasureText(timer, size) + size * 0.25f;
            }
        }

        // ---- icon (aspect-preserved, vertically centered against the text) ----
        Texture2D? tex = TextureCache.GetFirst(IconPaths(_icon));
        if (tex is not null)
        {
            Vector2 ts = tex.GetSize();
            // aspect: guard a zero/NaN texture height so iconW can never be NaN/Inf.
            float aspect = ts.Y > 0f && float.IsFinite(ts.X) && float.IsFinite(ts.Y) ? ts.X / ts.Y : 1f;
            if (!float.IsFinite(aspect) || aspect <= 0f) aspect = 1f;
            float iconW = iconH * aspect;
            float iconY = topY - (iconH - fontH) * 0.5f; // QC: pos - eY*(iconsize.y - fontsize.y)*0.5
            if (float.IsFinite(iconW) && float.IsFinite(iconH) && iconW > 0f && iconH > 0f)
            {
                DrawTextureRect(tex, new Rect2(new Vector2(x, iconY), new Vector2(iconW, iconH)), tile: false, white);
                x += iconW + size * 0.25f;
            }
        }
        else if (!string.IsNullOrEmpty(_icon))
        {
            // Art missing — draw a small placeholder box so the pickup still reads (never invisible).
            float boxH = fontH;
            DrawRect(new Rect2(new Vector2(x, topY), new Vector2(boxH, boxH)),
                new Color(1f, 1f, 1f, alpha * 0.4f));
            x += boxH + size * 0.25f;
        }

        // ---- name (+ "(xN)" when more than one was picked up), color-coded, width-shortened ----
        // QC: sprintf("%s (x%d)", item.m_name, …) — the name keeps its ^N color codes (do NOT strip),
        // the trailing " (xN)" inherits the run color in effect at the end of the name.
        string label = _count > 1 ? $"{_name} (x{_count})" : _name;
        float remaining = Mathf.Max(0f, Size2.X - x - padding);
        if (remaining > 0f && !string.IsNullOrEmpty(label))
            DrawNameRuns(label, new Vector2(x, topY), remaining, size, alpha);
    }

    // Defensive cap on the per-frame name length: textShortenToWidth-style truncation is O(n²) in the
    // worst run, and the name is fed by the net layer, so bound the input. ~256 chars dwarfs any real
    // item name yet keeps a pathological/hostile string from spiking the draw path every frame.
    private const int MaxLabelChars = 256;

    /// <summary>
    /// Draw the (possibly color-coded) item name left-to-right, truncated with "…" to <paramref name="maxW"/>
    /// (QC <c>textShortenToWidth</c>) so a long name never spills past the panel edge.
    /// </summary>
    private void DrawNameRuns(string label, Vector2 pos, float maxW, int size, float alpha)
    {
        if (maxW <= 0f) return;
        if (label.Length > MaxLabelChars) label = label.Substring(0, MaxLabelChars);
        var baseColor = new Color(1f, 1f, 1f, alpha);
        List<HudText.Run> runs = HudText.Parse(label, baseColor);

        float x = pos.X;
        float ellipsisW = MeasureText("…", size);
        foreach (HudText.Run r in runs)
        {
            string text = r.Text;
            var c = r.Color; c.A = alpha;
            float w = MeasureText(text, size);
            if (x + w <= pos.X + maxW)
            {
                DrawText(new Vector2(x, pos.Y), text, c, size);
                x += w;
                continue;
            }

            // This run overflows: shorten it char-by-char to fit (room for the ellipsis).
            for (int n = text.Length; n > 0; n--)
            {
                string cut = text.Substring(0, n);
                if (x + MeasureText(cut, size) + ellipsisW <= pos.X + maxW)
                {
                    DrawText(new Vector2(x, pos.Y), cut + "…", c, size);
                    return;
                }
            }
            if (x + ellipsisW <= pos.X + maxW)
                DrawText(new Vector2(x, pos.Y), "…", c, size);
            return; // nothing more fits
        }
    }

    /// <summary>
    /// Resolve the leading-timer string: the frozen <see cref="TimerText"/> when present (QC
    /// <c>seconds_tostring(HUD_Pickup_Time(last_pickup_time))</c>), else the panel's OWN clock reading at the
    /// pickup instant — also frozen. Never the running age: the readout is the game time the event happened.
    /// (The panel clock starts at 0 with the HUD, so the fallback approximates the count-up match clock.)
    /// </summary>
    private string ResolveTimerText()
    {
        if (!string.IsNullOrEmpty(TimerText)) return TimerText!;
        return SecondsToString((float)_pickupTime);
    }

    /// <summary>
    /// Candidate texture paths for a pickup icon name (QC the item's <c>model2</c>/<c>m_icon</c>, e.g.
    /// "armor_mega", "ammo_rockets"), best-first: preferred HUD skin → default skin → a <c>res://</c>
    /// project override. Resolved through <see cref="TextureCache"/>'s VFS resolver.
    /// </summary>
    private static string[] IconPaths(string icon)
    {
        if (string.IsNullOrEmpty(icon)) return System.Array.Empty<string>();
        return new[]
        {
            $"gfx/hud/{HudSkin.SkinName}/{icon}",
            $"gfx/hud/default/{icon}",
            $"res://art/hud/pickup/{icon}.png",
        };
    }

    /// <summary>
    /// Register this panel's behaviour-cvar defaults (QC <c>hud_panel_pickup_*</c> from the luma config).
    /// Invoked once at boot by <see cref="HudConfig.RegisterDefaults"/> via reflection. <c>Register</c> is
    /// idempotent so any exec'd cfg / user <c>seta</c> wins.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        c.Register("hud_panel_pickup_time", "3", CvarFlags.Save);
        c.Register("hud_panel_pickup_fade_out", "0.15", CvarFlags.Save);
        c.Register("hud_panel_pickup_iconsize", "1.5", CvarFlags.Save);
        c.Register("hud_panel_pickup_showtimer", "1", CvarFlags.Save);
    }
}
