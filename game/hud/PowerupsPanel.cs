using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Active-powerups strip — port of Base/.../qcsrc/client/hud/panel/powerups.qc (HUD panel #2). The QC
/// version built a list of active powerups (strength, shield, superweapons, the buffs, …) via the
/// <c>HUD_Powerups_add</c> hook, each with a name, icon, color, current time and lifetime, then found the
/// best column×row partitioning of the panel rect (targeting a 6:1 cell aspect), and drew per-cell a
/// countdown progress bar (<c>HUD_Panel_DrawProgressBar</c>) plus an icon+number (<c>DrawNumIcon</c>). When
/// a powerup is permanent (jetpack / unlimited-superweapons) it draws the infinity glyph; when a timed
/// powerup is in its last 5 seconds it pulses with an expanding flash (<c>DrawNumIcon_expanding</c>).
///
/// Data source (QC <c>HUD_Powerups_add</c> → <c>StatusEffects_tick</c> → <c>m_tick</c> → <c>addPowerupItem</c>):
/// the panel walks the local <see cref="Player"/>'s active <see cref="Entity.StatusEffects"/> each frame and
/// adds every NON-hidden effect that has a live timer — the powerups (strength / shield / speed / invisibility /
/// superweapon) and the BuffsMutator buffs — using the effect's QC <c>m_name</c> / <c>m_icon</c> / <c>m_color</c>
/// (carried in this panel's metadata table since the C# <see cref="StatusEffectDef"/> has none) and remaining
/// time. (NOTE: a live player's active powerups live in <see cref="Entity.StatusEffects"/>; the
/// <c>Entity.*Finished</c> fields are world-item carriers and are always 0 on a player.) The persistent
/// item-flag powerups (jetpack / fuel-regen / unlimited-superweapons) on <see cref="Entity.Items"/> are also
/// surfaced as infinity (∞) entries. The current time comes from <see cref="Now"/> if set, else the sim clock
/// (<see cref="Api.Clock"/>). The net/demo layer may still override the whole set via <see cref="Set"/>.
/// </summary>
public partial class PowerupsPanel : HudPanel
{
    /// <summary>One active powerup row (QC powerupItems entry: message/netname/colormod/count/lifetime/cnt).</summary>
    public readonly struct PowerupEntry
    {
        /// <summary>Human-readable name / short label (QC <c>.message</c>).</summary>
        public readonly string Name;
        /// <summary>Skin icon bare name (QC <c>.netname</c>, e.g. "strength"/"shield"/"superweapons").</summary>
        public readonly string Icon;
        /// <summary>Seconds remaining (QC <c>.count</c>).</summary>
        public readonly float TimeLeft;
        /// <summary>Full duration, for the bar fraction (QC <c>.lifetime</c>).</summary>
        public readonly float Lifetime;
        /// <summary>Bar/icon-number tint (QC <c>.colormod</c>).</summary>
        public readonly Color Color;
        /// <summary>Permanent powerup — show the infinity glyph instead of a countdown (QC <c>.cnt</c>).</summary>
        public readonly bool Infinite;

        /// <summary>Full entry (QC addPowerupItem). <paramref name="icon"/> is a bare skin name.</summary>
        public PowerupEntry(string name, string icon, float timeLeft, float lifetime, Color color, bool infinite = false)
        {
            Name = name;
            Icon = icon;
            TimeLeft = timeLeft;
            Lifetime = lifetime;
            Color = color;
            Infinite = infinite;
        }

        /// <summary>Legacy ctor (no explicit icon — derives the icon from the lower-cased name). Kept so
        /// existing callers of <see cref="Set"/> keep compiling.</summary>
        public PowerupEntry(string name, float timeLeft, float lifetime, Color color)
            : this(name, (name ?? "").ToLowerInvariant(), timeLeft, lifetime, color) { }
    }

    /// <summary>The local player (set by <see cref="Hud"/>); powerup timers are read from it each frame.</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// Current time used to compute remaining durations. If &lt; 0 (default) the panel uses the sim clock
    /// (<see cref="Api.Clock"/>) when available, else its own per-frame wall clock. The net/demo layer can
    /// slave it to the match clock.
    /// </summary>
    public double Now { get; set; } = -1.0;

    /// <summary>When true, ignore the player and only show the owner-pushed <see cref="Set"/> entries.</summary>
    public bool ManualOnly { get; set; }

    // QC DESIRED_ASPECT — target cell aspect ratio when partitioning the panel into columns×rows.
    private const float DesiredAspect = 6f;

    // The infinity glyph QC uses for permanent powerups ("\xE2\x88\x9E" = U+221E).
    private const string InfinitySymbol = "∞";

    // QC StatusEffect.m_lifetime default (all.qh ATTRIB m_lifetime, float, 30) — the bar's full-scale duration.
    // The QC powerups panel uses item.lifetime = the effect's m_lifetime (a FIXED 30, NOT the balance cvar),
    // because the on-screen bar is just a normalized "fraction of a nominal window" indicator. None of the
    // powerup status effects override m_lifetime, so 30 is correct for all of them.
    private const float StatusEffectLifetime = 30f;

    // Per-powerup status-effect metadata (QC m_name / m_icon / m_color from powerup/*.qh + superweapons.qh).
    // The C# StatusEffectDef carries no icon/color/lifetime, so the table lives here (driven by QC reference
    // values). Keyed by the catalog effect Name ("strength"/"shield"/"speed"/"invisibility"/"superweapon").
    private readonly struct PowerupMeta
    {
        public readonly string Label;  // QC m_name
        public readonly string Icon;   // QC m_icon (bare skin name)
        public readonly Color Color;   // QC m_color (colormod)
        public PowerupMeta(string label, string icon, Color color) { Label = label; Icon = icon; Color = color; }
    }

    private static bool TryPowerupMeta(string effectName, out PowerupMeta meta)
    {
        switch (effectName)
        {
            // QC StrengthStatusEffect: m_name "Strength", m_icon "strength", m_color '0 0 1'.
            case "strength":     meta = new PowerupMeta("Strength", "strength", new Color(0f, 0f, 1f)); return true;
            // QC ShieldStatusEffect: m_name "Shield", m_icon "shield", m_color '1 0 1'.
            case "shield":       meta = new PowerupMeta("Shield", "shield", new Color(1f, 0f, 1f)); return true;
            // QC SpeedStatusEffect: m_name "Speed", m_icon "buff_speed", m_color '0.1 1 0.84'.
            case "speed":        meta = new PowerupMeta("Speed", "buff_speed", new Color(0.1f, 1f, 0.84f)); return true;
            // QC InvisibilityStatusEffect: m_name "Invisibility", m_icon "buff_invisible", m_color '0.5 0.5 1'.
            case "invisibility": meta = new PowerupMeta("Invisibility", "buff_invisible", new Color(0.5f, 0.5f, 1f)); return true;
            // QC Superweapon: m_name "Superweapons", m_icon "superweapons", m_color default '1 1 1'.
            case "superweapon":  meta = new PowerupMeta("Superweapons", "superweapons", new Color(1f, 1f, 1f)); return true;
            default: meta = default; return false;
        }
    }

    private readonly List<PowerupEntry> _manual = new();
    private readonly List<PowerupEntry> _scratch = new(); // rebuilt each draw from the player

    private double _localClock;

    /// <summary>Replace the owner-pushed powerups (QC resetPowerupItems + addPowerupItem). Forces redraw.</summary>
    public void Set(IEnumerable<PowerupEntry> items)
    {
        _manual.Clear();
        if (items != null) _manual.AddRange(items);
        QueueRedraw();
    }

    /// <summary>Clear the owner-pushed powerups (QC resetPowerupItems).</summary>
    public void Clear()
    {
        _manual.Clear();
        QueueRedraw();
    }

    /// <summary>
    /// Register the powerups panel's behavior cvars (QC <c>hud_panel_powerups_*</c>, see HUD_Powerups_Export +
    /// _hud_common.cfg). Auto-invoked by HudConfig via reflection. Defaults are the luma skin values. NOTE: the
    /// per-powerup colors come from each effect's QC <c>m_color</c> (the powerupItems colormod), NOT from any
    /// <c>hud_progressbar_*_color</c> cvar — so those are not registered here; <c>hud_progressbar_alpha</c> is the
    /// only shared global this panel reads (registered idempotently in case no other panel got to it first).
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        if (c is null) return;

        // hud_panel_powerups_* (luma: iconalign/baralign 3, progressbar/text on, dynamichud on).
        c.Register("hud_panel_powerups_iconalign", "3", CvarFlags.Save);
        c.Register("hud_panel_powerups_baralign", "3", CvarFlags.Save);
        c.Register("hud_panel_powerups_progressbar", "1", CvarFlags.Save);
        c.Register("hud_panel_powerups_text", "1", CvarFlags.Save);
        c.Register("hud_panel_powerups_dynamichud", "1", CvarFlags.Save);
        c.Register("hud_panel_powerups_hide_ondeath", "0", CvarFlags.Save);

        // Shared progress-bar alpha (global; QC autocvar_hud_progressbar_alpha). Idempotent.
        c.Register("hud_progressbar_alpha", "0.6", CvarFlags.Save);
    }

    public override void _Process(double delta) => _localClock += delta;

    private float CurrentTime()
    {
        if (Now >= 0.0) return (float)Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)_localClock;
    }

    /// <summary>Gather the rows to draw this frame: owner-pushed plus the player's live powerup timers.</summary>
    private List<PowerupEntry> BuildRows()
    {
        _scratch.Clear();
        _scratch.AddRange(_manual);

        if (!ManualOnly && Player is not null)
            AppendPlayerPowerups(_scratch);

        return _scratch;
    }

    private void AppendPlayerPowerups(List<PowerupEntry> rows)
    {
        float now = CurrentTime();
        Entity? e = Player; // defensive: Player may have been cleared on another thread between BuildRows and here
        if (e is null) return;
        var items = (ItemFlag)e.Items;
        List<ActiveStatusEffect>? effects = e.StatusEffects;

        // --- Active status effects (QC HUD_Powerups_add -> StatusEffects_tick -> m_tick -> addPowerupItem).
        // The QC client iterates EVERY non-hidden status effect with an active timer (the powerups
        // strength/shield/speed/invisibility/superweapon AND the BuffsMutator buffs) and adds it with the
        // effect's own m_name/m_icon/m_color, the remaining time, and m_lifetime (a fixed 30). PERSISTENT
        // effects (passively granted) show the infinity glyph instead of a countdown.
        //
        // PORT NOTE: in this port a live player's active powerups live in Entity.StatusEffects (each with an
        // absolute ExpireTime) — NOT in the Entity.*Finished fields, which are the world-item carriers that
        // ItemPickupRules.ApplyPowerupTimers reads to APPLY the status effect. So we read the timers off the
        // status-effect list here (the old code read e.StrengthFinished on the player, which is always 0 →
        // powerups never displayed).
        // Index-based iteration (re-reading Count each step) instead of foreach: the per-entity status-effect
        // list is mutated by the sim each tick, and a foreach in the draw path would throw "Collection was
        // modified" if an effect is applied/expired mid-frame — an unhandled exception in DrawPanel spams the
        // log and breaks HUD rendering. Indexed access with a live bounds check never throws on concurrent
        // structural change (worst case it skips/re-reads one shifted entry, which self-corrects next frame).
        IReadOnlyList<StatusEffectDef> catalog = StatusEffectsCatalog.All;
        int catalogCount = catalog.Count;
        for (int si = 0; effects is not null && si < effects.Count; si++)
        {
            ActiveStatusEffect s = effects[si];
            if (s.DefId < 0 || s.DefId >= catalogCount) continue;
            StatusEffectDef? def = catalog[s.DefId];
            if (def is null) continue;
            if (def.Hidden) continue; // QC m_tick: hidden effects (frozen/burning/spawnshield/stunned/webbed) skip

            // Remaining time (QC: bound(0, statuseffect_time - time, 99)).
            float left = s.ExpireTime > 0f ? Mathf.Clamp(s.ExpireTime - now, 0f, 99f) : 0f;
            if (s.ExpireTime > 0f && left <= 0f) continue; // expired this frame
            // QC m_tick: PERSISTENT effects carry the flag and never time out (sv_status_effects.qc:20-22).
            // The flag is set per-tick via the effect's m_persistent check, and networked in the PERSISTENT bit.
            bool infinite = (s.Flags & StatusEffectFlags.Persistent) != 0; // passively granted (QC PERSISTENT)
            float life = def.Lifetime > 0f ? def.Lifetime : StatusEffectLifetime;

            string label, icon;
            Color color;
            if (TryPowerupMeta(def.Name, out PowerupMeta meta))
            {
                label = meta.Label; icon = meta.Icon; color = meta.Color;
            }
            else
            {
                // A buff (or unknown non-hidden effect): derive the label/icon from the def name.
                label = BuffLabel(def.Name);
                icon = def.Name; // buffs use their "buff_<name>" skin icon
                color = new Color(0.4f, 1f, 0.6f); // generic buff tint (no per-buff color in the C# def)
            }

            rows.Add(new PowerupEntry(label, icon, infinite ? 1f : left, life, color, infinite));
        }

        // --- Permanent item-flag powerups (port extension: QC surfaces these via the PERSISTENT status-effect
        // path, but in this port jetpack / fuel-regen / unlimited-superweapons are held .items bits, not status
        // effects). Shown as infinite (∞) entries so the held bit is still visible. Skipped if already covered
        // by an active status effect above (e.g. a finite superweapon timer).
        if ((items & ItemFlag.Jetpack) != 0)
            rows.Add(new PowerupEntry("Jetpack", "jetpack", 1f, StatusEffectLifetime, new Color(0.5f, 0.5f, 0.5f), infinite: true));
        if ((items & ItemFlag.FuelRegen) != 0)
            rows.Add(new PowerupEntry("Fuel regenerator", "fuelregen", 1f, StatusEffectLifetime, new Color(1f, 0.5f, 0f), infinite: true));
        if ((items & ItemFlag.UnlimitedSuperweapons) != 0 && !HasActiveEffect("superweapon"))
            rows.Add(new PowerupEntry("Superweapons", "superweapons", 1f, StatusEffectLifetime, new Color(1f, 1f, 1f), infinite: true));
    }

    /// <summary>True when the player currently has the named status effect with a live (non-expired) timer.</summary>
    private bool HasActiveEffect(string effectName)
    {
        Player? p = Player;
        if (p is null) return false;
        List<ActiveStatusEffect>? effects = p.StatusEffects;
        if (effects is null) return false;
        float now = CurrentTime();
        IReadOnlyList<StatusEffectDef> catalog = StatusEffectsCatalog.All;
        int catalogCount = catalog.Count;
        // Indexed (Count re-read each step) so a concurrent sim-side mutation can't throw in the draw path.
        for (int i = 0; i < effects.Count; i++)
        {
            ActiveStatusEffect s = effects[i];
            if (s.DefId < 0 || s.DefId >= catalogCount) continue;
            StatusEffectDef? def = catalog[s.DefId];
            if (def is null || def.Name != effectName) continue;
            if (s.ExpireTime <= 0f || s.ExpireTime - now > 0f) return true;
        }
        return false;
    }

    /// <summary>"buff_speed" -> "Speed".</summary>
    private static string BuffLabel(string defName)
    {
        string s = defName.StartsWith("buff_") ? defName.Substring(5) : defName;
        return s.Length == 0 ? defName : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    protected override void DrawPanel()
    {
        List<PowerupEntry> items = BuildRows();
        int count = items.Count;
        if (count == 0) return; // QC: panel draws nothing with no active powerups

        DrawBackground();

        // Drawing area inside the panel padding (QC: pos += '1 1' * padding; size -= '2 2' * padding).
        float pad = Cfg.Padding;
        var pos = new Vector2(pad, pad);
        var size = new Vector2(Size2.X - pad * 2f, Size2.Y - pad * 2f);
        if (size.X <= 0f || size.Y <= 0f) return;
        bool isVertical = size.Y > size.X;

        // --- Find the best column×row partitioning of the drawing area (QC do/while, target aspect 6). ---
        float aspect = 0f, a;
        int columns = 1, c;
        int rows = count, r;
        for (int i = 1; i <= count; i++)
        {
            c = Mathf.FloorToInt((float)count / i);
            if (c < 1) c = 1;
            r = Mathf.CeilToInt((float)count / c);
            a = isVertical
                ? (size.Y / r) / (size.X / c)
                : (size.X / c) / (size.Y / r);

            if (i == 1 || Mathf.Abs(DesiredAspect - a) < Mathf.Abs(DesiredAspect - aspect))
            {
                aspect = a;
                columns = c;
                rows = r;
            }
        }

        // Prevent a single item from getting too wide (QC: halve along the long axis, recenter).
        if (count == 1 && aspect > DesiredAspect)
        {
            if (isVertical)
            {
                size.Y *= 0.5f;
                pos.Y += size.Y * 0.5f;
            }
            else
            {
                size.X *= 0.5f;
                pos.X += size.X * 0.5f;
            }
        }

        // --- Draw items from the partitioned grid (QC linked-list loop). ---
        // Guard the cell divisor: the partition loop above always lands columns/rows >= 1, but clamp here so a
        // future edit can never produce a divide-by-zero (→ Infinity cell size → off-screen / NaN draw).
        if (columns < 1) columns = 1;
        if (rows < 1) rows = 1;
        var itemSize = new Vector2(size.X / columns, size.Y / rows);
        bool progressbar = CvarBool("progressbar");
        bool text = CvarBool("text");
        int baralignCvar = (int)CvarF("baralign", 0f);
        int iconalignCvar = (int)CvarF("iconalign", 0f);
        float pbAlpha = GlobalF("hud_progressbar_alpha", 0.6f) * LiveFgAlpha;

        int column = 0, row = 0;
        for (int idx = 0; idx < count; idx++)
        {
            PowerupEntry it = items[idx];
            var itemPos = new Vector2(pos.X + column * itemSize.X, pos.Y + row * itemSize.Y);

            // Draw the per-powerup progress bar (count / lifetime, tinted by colormod).
            if (progressbar)
            {
                int align = ItemAlign(baralignCvar, column, row, columns, rows, isVertical);
                float frac = it.Lifetime > 0f ? it.TimeLeft / it.Lifetime : 1f;
                DrawProgressBar(itemPos, itemSize, frac, isVertical, align,
                    new Color(it.Color.R, it.Color.G, it.Color.B), pbAlpha);
            }

            // Draw the icon + countdown number (with the expanding flash in the last 5 seconds).
            if (text)
            {
                int align = ItemAlign(iconalignCvar, column, row, columns, rows, isVertical);
                int fullSeconds = Mathf.CeilToInt(it.TimeLeft);
                // QC textColor = '0.6 0.6 0.6' + colormod * 0.4.
                var textColor = new Color(
                    0.6f + it.Color.R * 0.4f,
                    0.6f + it.Color.G * 0.4f,
                    0.6f + it.Color.B * 0.4f,
                    1f);

                if (it.Infinite)
                {
                    DrawNumIcon(itemPos, itemSize, InfinitySymbol, it.Icon, isVertical, align, textColor, LiveFgAlpha);
                }
                else
                {
                    if (it.TimeLeft > 1f)
                        DrawNumIcon(itemPos, itemSize, fullSeconds.ToString(), it.Icon, isVertical, align,
                            textColor, LiveFgAlpha);
                    if (it.TimeLeft <= 5f)
                    {
                        float fadelerp = Mathf.Clamp((fullSeconds - it.TimeLeft) * 2f, 0f, 1f);
                        DrawNumIconExpanding(itemPos, itemSize, fullSeconds.ToString(), it.Icon, isVertical, align,
                            textColor, LiveFgAlpha, fadelerp);
                    }
                }
            }

            // Advance to the next cell (QC: vertical fills rows-then-cols, horizontal cols-then-rows).
            if (isVertical)
            {
                if (++column >= columns) { column = 0; ++row; }
            }
            else
            {
                if (++row >= rows) { row = 0; ++column; }
            }
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Per-cell alignment (QC getPowerupItemAlign): align 0/1 are used as-is; 2/3 resolve per edge cell.
    // -------------------------------------------------------------------------------------------------
    private static int ItemAlign(int align, int column, int row, int columns, int rows, bool isVertical)
    {
        if (align < 2) return align;

        bool isTop = isVertical && rows > 1 && row == 0;
        bool isBottom = isVertical && rows > 1 && row == rows - 1;
        bool isLeft = !isVertical && columns > 1 && column == 0;
        bool isRight = !isVertical && columns > 1 && column == columns - 1;

        if (isTop || isLeft) return (align == 2) ? 1 : 0;
        if (isBottom || isRight) return (align == 2) ? 0 : 1;
        return 2;
    }

    // -------------------------------------------------------------------------------------------------
    //  Progress bar with baralign (QC HUD_Panel_DrawProgressBar — horizontal/vertical + 4 align modes).
    //  Renders the "progressbar"/"progressbar_vertical" skin art clipped to the fill rect (QC drawsubpic),
    //  offset per the baralign exactly like the QC; falls back to a tinted rect when the art is missing.
    // -------------------------------------------------------------------------------------------------
    private void DrawProgressBar(Vector2 origin, Vector2 size, float lengthRatio, bool vertical, int baralign,
        Color color, float alpha)
    {
        // Delegate to the shared faithful primitive (HudPanel.DrawProgressBar): the QC 3-slice cap render
        // (drawsubpic square/middle/cap → rounded ends), full baralign 0/1/2/3, the clamp + skin-resolve.
        DrawProgressBar(new Rect2(origin, size), "progressbar", lengthRatio, vertical, baralign, color, alpha);
    }

    // -------------------------------------------------------------------------------------------------
    //  Icon + number (QC DrawNumIcon / DrawNumIcon_expanding). Lays the cell into an icon square + a
    //  number box, ordered by icon_right_align (0 = icon left / number right, 1 = number left / icon
    //  right). The expanding variant scales the icon+number up briefly (QC's last-5-second pulse).
    // -------------------------------------------------------------------------------------------------
    private void DrawNumIcon(Vector2 pos, Vector2 size, string text, string icon, bool vertical,
        int iconRightAlign, Color color, float alpha)
        => DrawNumIconExpanding(pos, size, text, icon, vertical, iconRightAlign, color, alpha, 0f);

    private void DrawNumIconExpanding(Vector2 myPos, Vector2 mySize, string text, string icon, bool vertical,
        int iconRightAlign, Color color, float alpha, float fadelerp)
    {
        if (alpha <= 0f) return;

        Vector2 newPos, newSize;

        if (vertical)
        {
            // Vertical cell: stack icon over number (QC uses a 1:2 square pair).
            if (mySize.Y / Mathf.Max(mySize.X, 0.001f) > 2f)
            {
                newSize = new Vector2(mySize.X, 2f * mySize.X);
                newPos = new Vector2(myPos.X, myPos.Y + (mySize.Y - newSize.Y) * 0.5f);
            }
            else
            {
                newSize = new Vector2(0.5f * mySize.Y, mySize.Y);
                newPos = new Vector2(myPos.X + (mySize.X - newSize.X) * 0.5f, myPos.Y);
            }

            Vector2 picPos, numPos;
            if (iconRightAlign != 0)
            {
                numPos = newPos;
                picPos = newPos + new Vector2(0f, newSize.X);
            }
            else
            {
                picPos = newPos;
                numPos = newPos + new Vector2(0f, newSize.X);
            }

            float half = newSize.Y * 0.5f;
            DrawIconAspect(new Rect2(picPos, new Vector2(newSize.X, half)), icon, color, alpha, fadelerp);
            // Number a touch smaller than the icon (QC reduces y by 30%, recenters).
            var numBox = new Vector2(newSize.X, half * 0.7f);
            var numAt = numPos + new Vector2(0f, half * ((1f - 0.7f) * 0.5f));
            DrawNumberAspect(new Rect2(numAt, numBox), text, color, alpha, fadelerp);
            return;
        }

        // Horizontal cell: a 3:1 strip = icon square + a 2:1 number box (QC).
        if (mySize.X / Mathf.Max(mySize.Y, 0.001f) > 3f)
        {
            newSize = new Vector2(3f * mySize.Y, mySize.Y);
            newPos = new Vector2(myPos.X + (mySize.X - newSize.X) * 0.5f, myPos.Y);
        }
        else
        {
            newSize = new Vector2(mySize.X, mySize.X / 3f);
            newPos = new Vector2(myPos.X, myPos.Y + (mySize.Y - newSize.Y) * 0.5f);
        }

        float unit = newSize.Y;     // icon square edge
        Vector2 picOrigin, numOrigin;
        if (iconRightAlign != 0) // number left, icon right
        {
            numOrigin = newPos;
            picOrigin = newPos + new Vector2(2f * unit, 0f);
        }
        else                      // icon left, number right
        {
            numOrigin = newPos + new Vector2(unit, 0f);
            picOrigin = newPos;
        }

        DrawNumberAspect(new Rect2(numOrigin, new Vector2(2f * unit, unit)), text, color, alpha, fadelerp);
        DrawIconAspect(new Rect2(picOrigin, new Vector2(unit, unit)), icon, color, alpha, fadelerp);
    }

    /// <summary>Draw a skin icon centered+scaled into <paramref name="box"/> (QC drawpic_aspect_skin[_expanding]).
    /// When the skin pic is missing, draws a colored disc so the powerup is never invisible. The expanding
    /// variant scales the box up around its center by the fade lerp.</summary>
    private void DrawIconAspect(Rect2 box, string icon, Color color, float alpha, float fadelerp)
    {
        box = Expand(box, fadelerp);
        alpha = ExpandAlpha(alpha, fadelerp); // QC: theAlpha * (1 - fadelerp)
        var mod = new Color(1f, 1f, 1f, alpha);
        if (!DrawSkinPic(icon, box, mod))
        {
            // Fallback primitive: a tinted rounded square so the slot still reads.
            var fill = new Color(color.R, color.G, color.B, alpha * 0.9f);
            DrawRect(box, fill);
            DrawRect(box, new Color(1f, 1f, 1f, alpha * 0.25f), filled: false, width: 1f);
        }
    }

    /// <summary>Draw the countdown number fitted into <paramref name="box"/> (QC drawstring_aspect[_expanding]).</summary>
    private void DrawNumberAspect(Rect2 box, string text, Color color, float alpha, float fadelerp)
    {
        if (string.IsNullOrEmpty(text)) return;
        box = Expand(box, fadelerp);
        alpha = ExpandAlpha(alpha, fadelerp); // QC: theAlpha * (1 - fadelerp)
        int size = Mathf.Max(8, Mathf.FloorToInt(box.Size.Y));
        // Shrink to fit width if needed (mimics aspect-fit).
        float w = MeasureText(text, size);
        if (w > box.Size.X && box.Size.X > 1f)
            size = Mathf.Max(8, Mathf.FloorToInt(size * box.Size.X / w));
        var col = new Color(color.R, color.G, color.B, color.A * alpha);
        float th = MeasureText(text, size);
        float tx = box.Position.X + (box.Size.X - th) * 0.5f;
        float ty = box.Position.Y + (box.Size.Y - size) * 0.5f;
        DrawText(new Vector2(tx, ty), text, col, size);
    }

    /// <summary>Scale a rect up around its center by the expanding fade lerp — the faithful QC
    /// <c>expandingbox_sizefactor_from_fadelerp</c> (client/draw.qc:50): <c>sz = 1.2 / (1.2 - fadelerp)</c>
    /// (1× at fadelerp 0, growing toward ~6× as fadelerp→1), centered via
    /// <c>expandingbox_resize_centered_box_offset</c> (offset = boxsize·0.5·(1−sz)). The art's alpha is faded
    /// by <c>(1 − fadelerp)</c> separately by the callers (QC <c>theAlpha * (1 - fadelerp)</c>).</summary>
    private static Rect2 Expand(Rect2 box, float fadelerp)
    {
        if (fadelerp <= 0f) return box;
        float sz = 1.2f / (1.2f - Mathf.Clamp(fadelerp, 0f, 1f));
        var newSize = box.Size * sz;
        var newPos = box.Position + box.Size * (0.5f * (1f - sz));
        return new Rect2(newPos, newSize);
    }

    /// <summary>QC expanding alpha fade: <c>theAlpha * (1 - fadelerp)</c> (client/draw.qc:65).</summary>
    private static float ExpandAlpha(float alpha, float fadelerp) => alpha * (1f - Mathf.Clamp(fadelerp, 0f, 1f));
}
