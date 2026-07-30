using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Items-time panel — port of Base/.../qcsrc/common/mutators/mutator/itemstime/itemstime.qc, the CSQC
/// <c>HUD_ItemsTime</c> draw (HUD panel #22). The QC version shows respawn countdowns for the "timed" pickups
/// — Mega/Big Health, Mega/Big Armor, the powerups (Strength/Shield/...), and a Superweapons slot — laying
/// out one icon + remaining-seconds per item in a column×row grid. Each item's absolute respawn time arrives
/// over the <c>itemstime</c> net message into <c>ItemsTime_time[]</c>; a value &lt; -1 (the QC negative
/// encoding) means "another copy is available right now". The number is colored red &lt;5s, yellow &lt;10s,
/// white otherwise (QC <c>DrawItemsTimeItem</c>); a blink marks spawned items, and a checkmark/expanding flash
/// marks the moment an item becomes available again.
///
/// The respawn times are server-driven, so the net layer pushes them here via <see cref="SetItemTime"/> /
/// <see cref="SetItemTimes"/>, keyed by item name (the analogue of the QC item registry id). The panel reads
/// the sim clock (<see cref="Api.Clock"/>) for the countdown and lays out the same grid the QC does, honoring:
/// <list type="bullet">
///   <item><c>hud_panel_itemstime</c> = enable mode (0 off / 1 spectating only / 2 also warmup + STAT(ITEMSTIME)==2).</item>
///   <item><c>hud_panel_itemstime_hidespawned</c> (0 show all / 1 hide spawned&amp;blink / 2 hide spawned, no blink).</item>
///   <item><c>hud_panel_itemstime_ratio</c> — target cell width:height (columns vs rows partitioning).</item>
///   <item><c>hud_panel_itemstime_dynamicsize</c> — shrink the panel to avoid spacing the items out.</item>
///   <item><c>hud_panel_itemstime_iconalign</c> — icon on the right vs left of the number.</item>
///   <item><c>hud_panel_itemstime_progressbar</c> / <c>_progressbar_reduced</c> / <c>_progressbar_maxtime</c> /
///         <c>_progressbar_name</c> — per-item countdown bar (QC <c>HUD_Panel_DrawProgressBar</c>).</item>
///   <item><c>hud_panel_itemstime_text</c> — draw the seconds number / checkmark.</item>
/// </list>
/// </summary>
public partial class ItemsTimePanel : HudPanel
{
    /// <summary>One timed item (QC the per-id <c>ItemsTime_time</c> entry plus its icon/color).</summary>
    private readonly struct Item
    {
        public readonly string Name;   // display label, e.g. "MH" (fallback when the skin icon is missing)
        public readonly string Icon;   // bare skin icon name (QC it.m_icon), e.g. "health_mega"
        public readonly Color Color;   // swatch tint (fallback when the skin icon is missing)
        public Item(string name, string icon, Color color) { Name = name; Icon = icon; Color = color; }
    }

    // The timed items shown, in display order (QC Item_ItemsTime_Allow set: mega/big health+armor + powerups +
    // the reserved superweapons slot last). The Icon is the QC m_icon bare skin name — real art exists for all
    // of these under gfx/hud/<skin>/ (health_mega, armor_big, strength, shield, superweapons, ...).
    private static readonly KeyValuePair<string, Item>[] Catalog =
    {
        new("health_mega",  new Item("MH",  "health_mega", new Color(0.3f, 0.4f, 1f, 1f))),
        new("health_big",   new Item("H+",  "health_big",  new Color(0.3f, 0.6f, 1f, 1f))),
        new("armor_mega",   new Item("MA",  "armor_mega",  new Color(1f, 0.3f, 0.3f, 1f))),
        new("armor_big",    new Item("A+",  "armor_big",   new Color(1f, 0.5f, 0.2f, 1f))),
        new("strength",     new Item("STR", "strength",    new Color(0.7f, 0.3f, 1f, 1f))),
        new("shield",       new Item("SHD", "shield",      new Color(1f, 0.85f, 0.2f, 1f))),
        new("superweapons", new Item("SW",  "superweapons", new Color(1f, 0.5f, 0.1f, 1f))),
    };

    // QC ItemsTime_time[]: absolute respawn time per item (seconds, sim clock). A value < -1 (the QC negative
    // encoding) means another copy is available now; -1 means "not on this map" (hidden).
    private readonly Dictionary<string, float> _times = new();

    // QC ItemsTime_availableTime[]: the wall time at which each item LAST became available — drives the
    // 0.5s "just became available" expanding flash (QC f = bound(0, (time - availableTime)*2, 1)).
    private readonly Dictionary<string, float> _availableTime = new();

    /// <summary>QC <c>hud_panel_itemstime_hidespawned</c>: don't list items that are currently spawned.
    /// Port convenience flag mirroring mode 1 — when set, also forces the hidespawned cvar interpretation
    /// to at least mode 1 so existing callers keep their old behavior.</summary>
    public bool HideSpawned { get; set; }

    /// <summary>Slave the countdown to this clock; &lt; 0 uses the sim clock (else its own ticker).</summary>
    public double Now { get; set; } = -1.0;

    private double _localClock;

    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    /// <summary>
    /// Register the items-time panel's behavior cvars (QC <c>HUD_ItemsTime_Export</c> + the
    /// <c>autocvar_hud_panel_itemstime_*</c> defaults). Auto-invoked by HudConfig via reflection. Defaults are
    /// the stock values. The master enable (<c>hud_panel_itemstime</c> = 2) and the layout cvars
    /// (<c>_pos</c>/<c>_size</c>/<c>_bg</c>/...) are registered by HudConfig from the luma table; here we only
    /// register the panel-specific behavior cvars this draw code reads live.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        if (c is null) return;

        // QC autocvar_hud_panel_itemstime_* defaults (itemstime.qc top-of-file + _hud_common.cfg).
        c.Register("hud_panel_itemstime_dynamicsize", "1", CvarFlags.Save);
        c.Register("hud_panel_itemstime_ratio", "2", CvarFlags.Save);
        c.Register("hud_panel_itemstime_iconalign", "0", CvarFlags.Save);
        c.Register("hud_panel_itemstime_progressbar", "0", CvarFlags.Save);
        c.Register("hud_panel_itemstime_progressbar_maxtime", "30", CvarFlags.Save);
        c.Register("hud_panel_itemstime_progressbar_name", "progressbar", CvarFlags.Save);
        c.Register("hud_panel_itemstime_progressbar_reduced", "0", CvarFlags.Save);
        c.Register("hud_panel_itemstime_hidespawned", "1", CvarFlags.Save);
        c.Register("hud_panel_itemstime_hidebig", "0", CvarFlags.Save);
        c.Register("hud_panel_itemstime_text", "1", CvarFlags.Save);

        // Shared progress-bar alpha (global; QC autocvar_hud_progressbar_alpha). Idempotent.
        c.Register("hud_progressbar_alpha", "0.6", CvarFlags.Save);
    }

    private float CurrentTime()
    {
        float t;
        if (Now >= 0.0) t = (float)Now;
        else if (Api.Services is not null) t = Api.Clock.Time;
        else t = (float)_localClock;
        // Never let a NaN/Inf clock (corrupt caller value, uninitialised service) poison the whole draw path:
        // every downstream comparison/subtraction would silently produce garbage geometry.
        return float.IsFinite(t) ? t : 0f;
    }

    /// <summary>
    /// Push one item's absolute respawn time (QC <c>ItemsTime_time[id] = f</c>), keyed by item name. A value
    /// &lt; -1 marks "available now" (another copy up); -1 hides the item.
    /// </summary>
    public void SetItemTime(string itemName, float absoluteTime)
    {
        if (string.IsNullOrEmpty(itemName)) return;
        if (!float.IsFinite(absoluteTime)) return; // reject corrupt net values so the draw path stays finite
        _times[itemName] = absoluteTime;
        QueueRedraw();
    }

    /// <summary>Replace all item respawn times at once (QC: a fresh ItemsTime sync).</summary>
    public void SetItemTimes(IEnumerable<KeyValuePair<string, float>>? times)
    {
        _times.Clear();
        if (times is not null)
            foreach (var kv in times)
                if (!string.IsNullOrEmpty(kv.Key) && float.IsFinite(kv.Value))
                    _times[kv.Key] = kv.Value; // skip null keys / non-finite times → never poison the draw path
        QueueRedraw();
    }

    /// <summary>Clear all item times (e.g. on map reset).</summary>
    public void Clear()
    {
        _times.Clear();
        _availableTime.Clear();
        QueueRedraw();
    }

    /// <summary>
    /// QC <c>HUD_ItemsTime</c> enable gate (itemstime.qc:293-296): the panel draws only for spectators (mode 1),
    /// or for spectators + everyone during warmup + (when <c>sv_itemstime == 2</c>) alive players too (mode 2).
    /// With the stock cvars (<c>hud_panel_itemstime = 2</c>, <c>sv_itemstime = 1</c>) an ALIVE player in a normal
    /// round therefore sees NOTHING — only spectators do. <paramref name="panelMode"/> is
    /// <c>hud_panel_itemstime</c>; <paramref name="spectateeStatus"/> is QC <c>spectatee_status</c> (0 = self,
    /// playing); <paramref name="warmup"/> is QC <c>warmup_stage</c>; <paramref name="itemstimeStat"/> is
    /// QC <c>STAT(ITEMSTIME)</c> (= the live <c>sv_itemstime</c> tier 0/1/2).
    /// </summary>
    public static bool ShouldDraw(int panelMode, int spectateeStatus, bool warmup, int itemstimeStat)
    {
        if (panelMode == 1) return spectateeStatus != 0;
        if (panelMode == 2) return spectateeStatus != 0 || warmup || itemstimeStat == 2;
        return false;
    }

    // -------------------------------------------------------------------------------------------------
    //  hidespawned mode resolution (QC autocvar_hud_panel_itemstime_hidespawned: 0/1/2).
    //  HideSpawned (the legacy public flag) forces at least mode 1 so old callers keep their behavior.
    // -------------------------------------------------------------------------------------------------
    private int HideSpawnedMode()
    {
        int mode = (int)CvarF("hidespawned", 1f);
        if (HideSpawned && mode == 0) mode = 1;
        return mode;
    }

    // QC count loop: an item counts toward the grid per the hidespawned mode (and -1 = absent always skips).
    private static bool CountsForGrid(float t, float now, int hideMode)
    {
        if (t == -1f) return false;                       // not on this map (QC: time == -1)
        if (hideMode == 1) return t > now || -t > now;    // hide spawned, count blink window
        if (hideMode == 2) return t > now;                // hide spawned, no blink
        return true;                                      // show all
    }

    protected override void DrawPanel()
    {
        float now = CurrentTime();
        int hideMode = HideSpawnedMode();
        // QC autocvar_hud_panel_itemstime_hidebig: when set, suppress Big Health (health_big) and Big Armor
        // (armor_big) from the countdown panel. The server always tracks them; this is a client-only display
        // preference. Default 0 = always show big items. Must be applied identically in both the count pass and
        // the draw pass below so the grid layout uses the same item set it counted.
        bool hideBig = CvarBool("hidebig");

        // --- Count the items to draw this frame (QC FOREACH count loop). ---
        int count = 0;
        foreach (var kv in Catalog)
        {
            // QC hud_panel_itemstime_hidebig: skip health_big / armor_big when the option is on.
            if (hideBig && (kv.Key == "health_big" || kv.Key == "armor_big")) continue;
            if (!_times.TryGetValue(kv.Key, out float t)) continue;
            if (CountsForGrid(t, now, hideMode)) count++;
        }
        if (count == 0) return; // QC: panel draws nothing with no timed items

        DrawBackground();

        // Drawing area inside the panel padding (QC: pos += '1 1' * padding; size -= '2 2' * padding).
        float pad = Cfg.Padding;
        if (!float.IsFinite(pad) || pad < 0f) pad = 0f;
        var pos = new Vector2(pad, pad);
        var size = new Vector2(Size2.X - pad * 2f, Size2.Y - pad * 2f);
        // Reject non-finite / collapsed draw areas (a NaN slips past a plain `<= 0` test).
        if (!(size.X > 0f) || !(size.Y > 0f)) return;

        // --- Column×row partitioning targeting the configured cell aspect (QC ar = max(2,ratio)+1). ---
        // Sanitize the ratio cvar first: a NaN/Inf (corrupt cvar string) through Mathf.Max would propagate to
        // ar and turn every coordinate below into NaN (full-screen / off-panel garbage). ar is always >= 3.
        float ratio = CvarF("ratio", 2f);
        if (!float.IsFinite(ratio)) ratio = 2f;
        float ar = Mathf.Max(2f, ratio) + 1f;

        int rows = GetRowCount(count, size, ar);
        if (rows < 1) rows = 1;                                  // defensive: GetRowCount must never yield 0
        int columns = Mathf.CeilToInt((float)count / rows);
        if (columns < 1) columns = 1;                            // defensive: keep the grid divisors > 0

        var itemSize = new Vector2(size.X / columns, size.Y / rows);
        // size.X/size.Y are both > 0 (guarded above) and columns/rows >= 1, so itemSize is strictly positive;
        // guard anyway so the itemSize.X / itemSize.Y aspect tests below can never divide by zero.
        if (itemSize.X <= 0f || itemSize.Y <= 0f) return;

        var offset = Vector2.Zero;
        bool dynamicSize = CvarBool("dynamicsize");
        if (dynamicSize)
        {
            // Reduce the panel to avoid spacing items out (QC: keep cells at exactly the aspect `ar`).
            if (itemSize.X / itemSize.Y < ar)
            {
                float newSize = rows * itemSize.X / ar;
                pos.Y += (size.Y - newSize) * 0.5f;
                size.Y = newSize;
                itemSize.Y = size.Y / rows;
            }
            else
            {
                float newSize = columns * itemSize.Y * ar;
                pos.X += (size.X - newSize) * 0.5f;
                size.X = newSize;
                itemSize.X = size.X / columns;
            }
        }
        else
        {
            // Center each cell within its grid slot at the aspect `ar`, spacing the rest (QC offset path).
            if (itemSize.X / itemSize.Y > ar)
            {
                float newSize = ar * itemSize.Y;
                offset.X = itemSize.X - newSize;
                pos.X += offset.X * 0.5f;
                itemSize.X = newSize;
            }
            else
            {
                float newSize = (1f / ar) * itemSize.X;
                offset.Y = itemSize.Y - newSize;
                pos.Y += offset.Y * 0.5f;
                itemSize.Y = newSize;
            }
        }

        // --- Draw the items (QC FOREACH draw loop: fills rows-then-columns). ---
        bool progressbar = CvarBool("progressbar");
        bool progressbarReduced = CvarBool("progressbar_reduced");
        float progressbarMaxtime = CvarF("progressbar_maxtime", 30f);
        // <=0 is false for NaN, so guard finiteness too — otherwise frac = t/NaN poisons the bar fill rect.
        if (!float.IsFinite(progressbarMaxtime) || progressbarMaxtime <= 0f) progressbarMaxtime = 30f;
        string progressbarName = CvarStr("progressbar_name");
        if (string.IsNullOrWhiteSpace(progressbarName)) progressbarName = "progressbar";
        bool iconalign = CvarBool("iconalign");
        bool text = CvarBool("text");
        float pbAlpha = GlobalF("hud_progressbar_alpha", 0.6f) * LiveFgAlpha;

        int row = 0, column = 0;
        foreach (var kv in Catalog)
        {
            // QC hud_panel_itemstime_hidebig: same filter as the count pass above — must match exactly.
            if (hideBig && (kv.Key == "health_big" || kv.Key == "armor_big")) continue;
            if (!_times.TryGetValue(kv.Key, out float t)) continue;
            if (t == -1f) continue; // QC FOREACH gate: Item_ItemsTime_GetTime(id) != -1

            // Resolve availability + remaining (QC item_time / item_available decode).
            bool available;
            float itemTime = t;
            if (itemTime < -1f) { available = true; itemTime = -itemTime; } // negative encoding: copy up now
            else available = itemTime <= now;

            // Track the "just became available" wall time → the 0.5s expanding flash (QC availableTime).
            float availableFx = UpdateAvailableTime(kv.Key, t, now);

            // Per-item visibility within the draw loop (QC continue per hidespawned mode).
            if (!CountsForGrid(t, now, hideMode)) continue;

            var itemPos = new Vector2(pos.X + column * (itemSize.X + offset.X),
                                      pos.Y + row * (itemSize.Y + offset.Y));

            DrawItemsTimeItem(itemPos, itemSize, ar, kv.Value, itemTime, available, availableFx, hideMode,
                progressbar, progressbarReduced, progressbarMaxtime, progressbarName, iconalign, text, pbAlpha);

            // Advance (QC: ++row then wrap to ++column).
            if (++row >= rows) { row = 0; ++column; }
        }
    }

    // QC ItemsTime_availableTime bookkeeping → the expanding-flash lerp f in [0,1] (1 = just appeared, 0 = old).
    private float UpdateAvailableTime(string key, float t, float now)
    {
        if (t >= 0f)
        {
            if (now <= t) _availableTime[key] = 0f;                 // still on countdown → reset
            else if (GetAvail(key) == 0f) _availableTime[key] = now; // just spawned → stamp now
        }
        else if (GetAvail(key) == 0f)
        {
            _availableTime[key] = now;                              // negative encoding (copy available) → stamp
        }

        float f = (now - GetAvail(key)) * 2f;
        return f > 1f ? 0f : Mathf.Clamp(f, 0f, 1f);
    }

    private float GetAvail(string key) => _availableTime.TryGetValue(key, out float v) ? v : 0f;

    // -------------------------------------------------------------------------------------------------
    //  Per-item draw (QC DrawItemsTimeItem). Lays the cell into a number box + an icon square ordered by
    //  iconalign; draws the optional progress bar, the seconds (colored by closeness) or the checkmark, and
    //  the icon (with the expanding flash when it just became available, blinking while spawned).
    // -------------------------------------------------------------------------------------------------
    private void DrawItemsTimeItem(Vector2 myPos, Vector2 mySize, float ar, Item item, float itemTime,
        bool itemAvailable, float itemAvailableTime, int hideMode, bool progressbar, bool progressbarReduced,
        float progressbarMaxtime, string progressbarName, bool iconalign, bool text, float pbAlpha)
    {
        // QC: t = floor(item_time - time + 0.999) (seconds remaining, rounded up; <=0 once spawned).
        float now = CurrentTime();
        int t = Mathf.FloorToInt(itemTime - now + 0.999f);

        // Number color by how close the respawn is (QC red <5 / yellow <10 / white).
        Color color = t < 5 ? new Color(0.7f, 0f, 0f)
                    : t < 10 ? new Color(0.7f, 0.7f, 0f)
                    : new Color(1f, 1f, 1f);

        // Icon alpha (QC): hidespawned==2 → always 1; available → blink(0.85, 0.15, 5); else 0.5.
        float picAlpha;
        if (hideMode == 2) picAlpha = 1f;
        else if (itemAvailable) picAlpha = Blink(0.85f, 0.15f, 5f, now);
        else picAlpha = 0.5f;

        // Cell split: a number box on one side and a 1:1 icon square on the other (QC numpos/picpos).
        // The icon square is mySize.y wide; the number box gets the remaining ((ar-1)/ar) * width.
        Vector2 numPos, picPos;
        if (iconalign)
        {
            numPos = myPos;
            picPos = myPos + new Vector2((ar - 1f) * mySize.Y, 0f);
        }
        else
        {
            numPos = myPos + new Vector2(mySize.Y, 0f);
            picPos = myPos;
        }

        var numSize = new Vector2(((ar - 1f) / ar) * mySize.X, mySize.Y);
        var picSize = new Vector2(mySize.Y, mySize.Y); // square icon (QC '1 1 0' * mySize_y)

        // Progress bar (QC: only while counting down). Reduced bar spans just the number box; else full cell.
        if (t > 0 && progressbar)
        {
            Vector2 pPos, pSize;
            if (progressbarReduced) { pPos = numPos; pSize = new Vector2(((ar - 1f) / ar) * mySize.X, mySize.Y); }
            else { pPos = myPos; pSize = mySize; }

            // QC: HUD_Panel_DrawProgressBar(p_pos, p_size, name, t/maxtime, vertical=0, baralign=iconalign, color, alpha).
            // Route through the shared faithful primitive (3-slice cap render + skin art resolve), not a flat fill —
            // length_ratio is NOT pre-clamped here (the primitive clamps >1 internally, matching the QC).
            float frac = t / progressbarMaxtime;
            DrawProgressBar(new Rect2(pPos, pSize), progressbarName, frac, vertical: false,
                baralign: iconalign ? 1 : 0, new Color(color.R, color.G, color.B), pbAlpha);
        }

        // Number / checkmark (QC: text on → seconds while t>0, else the checkmark in the number slot).
        if (text)
        {
            if (t > 0)
            {
                DrawNumberAspect(new Rect2(numPos, numSize), t.ToString(), color, LiveFgAlpha);
            }
            else
            {
                // Spawned: a checkmark over the number slot (QC drawpic_aspect_skin "checkmark"). The icon is
                // drawn full-square below; when checkmark art is missing we fall back to a tinted glyph.
                var checkBox = new Rect2(numPos.X, numPos.Y, (ar - 1f) * mySize.Y, mySize.Y);
                var checkBoxC = CenterSquare(checkBox);
                var checkMod = new Color(1f, 1f, 1f, LiveFgAlpha * picAlpha);
                if (!DrawSkinPic("checkmark", checkBoxC, checkMod))
                    DrawNumberAspect(checkBox, "✓", new Color(0.4f, 1f, 0.4f), LiveFgAlpha * picAlpha);
            }
        }

        // The item icon (QC drawpic_aspect_skin item_icon). Expanding flash overlay when it just appeared.
        var iconBox = CenterSquare(new Rect2(picPos, picSize));
        if (itemAvailableTime > 0f)
        {
            // QC drawpic_aspect_skin_expanding: a fading, growing copy of the icon at the available instant.
            Rect2 grown = Expand(iconBox, itemAvailableTime);
            DrawIcon(grown, item, new Color(1f, 1f, 1f, LiveFgAlpha * picAlpha * (1f - itemAvailableTime)));
        }
        DrawIcon(iconBox, item, new Color(1f, 1f, 1f, LiveFgAlpha * picAlpha));
    }

    /// <summary>Draw the item's skin icon fitted into <paramref name="box"/> (QC drawpic_aspect_skin). When the
    /// skin pic is missing, draws a tinted swatch + the short label so the slot is never invisible.</summary>
    private void DrawIcon(Rect2 box, Item item, Color modulate)
    {
        if (modulate.A <= 0f) return;
        if (DrawSkinPic(item.Icon, box, modulate)) return;

        // Fallback primitive: tinted square + the short label centered.
        var fill = new Color(item.Color.R, item.Color.G, item.Color.B, item.Color.A * modulate.A * 0.9f);
        DrawRect(box, fill);
        DrawRect(box, new Color(1f, 1f, 1f, modulate.A * 0.25f), filled: false, width: 1f);
        DrawNumberAspect(box, item.Name, new Color(1f, 1f, 1f, modulate.A), 0.8f);
    }

    /// <summary>Draw a number/glyph fitted (aspect) into <paramref name="box"/> (QC drawstring_aspect). The
    /// optional <paramref name="heightFrac"/> shrinks the glyph within the box (used for the fallback label).</summary>
    private void DrawNumberAspect(Rect2 box, string s, Color color, float alpha, float heightFrac = 1f)
    {
        if (string.IsNullOrEmpty(s) || alpha <= 0f) return;
        float boxH = box.Size.Y * heightFrac;
        int sz = Mathf.Max(8, Mathf.FloorToInt(boxH));
        float w = MeasureText(s, sz);
        if (w > box.Size.X && box.Size.X > 1f)
            sz = Mathf.Max(8, Mathf.FloorToInt(sz * box.Size.X / w));
        var col = new Color(color.R, color.G, color.B, color.A * alpha);
        float tw = MeasureText(s, sz);
        float tx = box.Position.X + (box.Size.X - tw) * 0.5f;
        float ty = box.Position.Y + (box.Size.Y - sz) * 0.5f;
        DrawText(new Vector2(tx, ty), s, col, sz);
    }

    // -------------------------------------------------------------------------------------------------
    //  Geometry helpers (QC HUD_GetRowCount / aspect-fit / drawpic_*_expanding).
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>HUD_GetRowCount</c> (hud.qc): pick the row count whose resulting cells best match the
    /// target item aspect <paramref name="itemAspect"/> (cell width:height = ar). This is the EXACT QC formula,
    /// not an approximation — it drives the column/row split for &gt;1 items.</summary>
    private static int GetRowCount(int itemCount, Vector2 size, float itemAspect)
    {
        if (itemCount <= 1) return 1;
        if (size.X <= 0f || !float.IsFinite(itemAspect)) return 1;

        // QC: float aspect = size_y / size_x;
        //     return bound(1, floor((sqrt(4*item_aspect*aspect*item_count + aspect*aspect) + aspect + 0.5) * 0.5), item_count);
        float aspect = size.Y / size.X;
        float radicand = 4f * itemAspect * aspect * itemCount + aspect * aspect;
        if (radicand < 0f) radicand = 0f; // sqrt of a (theoretically) negative term would yield NaN
        float rows = Mathf.Floor((Mathf.Sqrt(radicand) + aspect + 0.5f) * 0.5f);
        // Mathf.Clamp does not sanitize NaN; bail to a safe single row if the formula degenerated.
        if (!float.IsFinite(rows)) return 1;
        return (int)Mathf.Clamp(rows, 1f, itemCount);
    }

    /// <summary>Fit a 1:1 square centered inside <paramref name="box"/> (QC drawpic_aspect keeps aspect 1).</summary>
    private static Rect2 CenterSquare(Rect2 box)
    {
        float edge = Mathf.Min(box.Size.X, box.Size.Y);
        if (edge <= 0f) return box;
        var pos = new Vector2(box.Position.X + (box.Size.X - edge) * 0.5f,
                              box.Position.Y + (box.Size.Y - edge) * 0.5f);
        return new Rect2(pos, new Vector2(edge, edge));
    }

    /// <summary>Grow a rect around its center by the QC expanding size factor (draw.qc
    /// <c>expandingbox_sizefactor_from_fadelerp</c> = <c>1.2 / (1.2 - fadelerp)</c>, applied as
    /// <c>theScale * sz</c> with center offset <c>boxsize * 0.5*(1-sz)</c>). The fading copy reads as a brief
    /// "appeared" pulse (alpha × (1-fadelerp) is applied by the caller).</summary>
    private static Rect2 Expand(Rect2 box, float fadelerp)
    {
        fadelerp = Mathf.Clamp(fadelerp, 0f, 1f);
        float sz = 1.2f / (1.2f - fadelerp);
        var newSize = box.Size * sz;
        // QC expandingbox_resize_centered_box_offset: pos += boxsize * 0.5*(1-sz)  (sz>1 → grows around center).
        var newPos = box.Position + box.Size * (0.5f * (1f - sz));
        return new Rect2(newPos, newSize);
    }

    /// <summary>QC <c>blink(base, range, freq)</c> = <c>blink_synced(base, range, freq, 0, 0)</c> (util.qc):
    /// <c>base + range * sin(time * freq)</c> — note freq is the raw sine argument multiplier (NOT cycles/s; no
    /// extra 2π factor), faithful to the QC.</summary>
    private static float Blink(float baseAlpha, float range, float freq, float now)
        => baseAlpha + range * Mathf.Sin(now * freq);
}
