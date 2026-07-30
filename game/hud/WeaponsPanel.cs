using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;          // Api (sim clock), CvarFlags
using VortexArena.Engine.Simulation;        // CvarService

namespace VortexArena.Game.Hud;

/// <summary>
/// Owned-weapons strip — feature-complete port of Base/.../qcsrc/client/hud/panel/weapons.qc (HUD panel #0).
/// The QC version laid out the weapons (priority-ordered) into a best-aspect table, drawing owned weapons
/// fully (icon <c>it.model2</c> = <c>weapon&lt;icon&gt;</c>) and unowned ones as dim "ghost" icons, with:
///   * a sliding highlight (<c>weapon_current_bg</c>) behind the currently selected weapon that animates
///     toward its target cell at <c>hud_panel_weapons_selection_speed</c>;
///   * an optional per-weapon accuracy tint overlay (<c>weapon_accuracy</c>, <c>hud_panel_weapons_accuracy</c>);
///   * an optional per-weapon ammo bar (<c>weapon_ammo</c>, <c>hud_panel_weapons_ammo</c>) clipped to the
///     current ammo fraction;
///   * a "complaint bubble" (<c>weapon_complainbubble</c>) over a weapon when the player tries to use it while
///     out of ammo / not owning it / it being unavailable, in three configurable colors, fading over
///     <c>_complainbubble_time</c> + <c>_complainbubble_fadetime</c>;
///   * a timeout fade (<c>_timeout</c>/<c>_timeout_effect</c> + in/out speeds + <c>_fadebgmin</c>/<c>_fadefgmin</c>)
///     that fades the panel out when no weapon activity has happened recently;
///   * label modes 0..3 (none / number / bind / name) scaled by <c>_label_scale</c>;
///   * <c>onlyowned</c>, <c>noncurrent</c> alpha/scale and the desired-cell <c>_aspect</c>.
///
/// luma layout is a THIN VERTICAL RIGHT-EDGE strip (~0.035 × 0.77), so the table comes out one column wide and
/// the weapons stack VERTICALLY (rows).
///
/// Ownership comes from the real bitset — <see cref="Entity.OwnedWeaponSet"/> (QC STAT(WEAPONS) WEPSET) — and
/// the selected weapon from <see cref="Inventory.CurrentWeapon"/> (QC STAT(ACTIVEWEAPON)). Each cell draws the
/// weapon's HUD icon via <see cref="WeaponHud.Icon"/> (QC <c>model2</c>), falling back to a color-tinted box +
/// label when the art is missing so nothing is invisible. Per-weapon ammo comes from the player's live
/// resources (<see cref="Resources.GetResource"/>) for the weapon's <see cref="Weapon.AmmoType"/>.
/// </summary>
public partial class WeaponsPanel : HudPanel
{
    // ---------------------------------------------------------------------------------------------
    //  Public surface preserved for the net/match/demo layer (do not remove/rename — external callers)
    // ---------------------------------------------------------------------------------------------

    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>).</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// Optional override for the selected weapon by NetName. Normally null so the panel reads the player's
    /// equipped weapon from the inventory; the net/demo layer may set it (QC switchweapon preview).
    /// </summary>
    public string? CurrentWeapon { get; set; }

    /// <summary>When true (QC <c>hud_panel_weapons_onlyowned</c>), draw only owned weapons; else ghost the rest.
    /// Default mirrors the live cvar but is kept as a settable property for the net/demo layer.</summary>
    public bool OnlyOwned { get; set; } = true;

    /// <summary>QC <c>hud_panel_weapons_accuracy</c>: tint owned cells by per-weapon accuracy when fed.</summary>
    public bool ShowAccuracy { get; set; } = true;

    // Per-weapon accuracy in [0,1], keyed by NetName (QC weapon_accuracy[] from the networked stats).
    private readonly Dictionary<string, float> _accuracy = new();

    /// <summary>
    /// Feed per-weapon accuracy fractions (0..1 by weapon NetName) from the networked accuracy stats
    /// (QC <c>weapon_accuracy</c>). Replaces the whole table; pass an empty/null map to clear.
    /// </summary>
    public void SetAccuracy(IReadOnlyDictionary<string, float>? accuracyByNetName)
    {
        _accuracy.Clear();
        if (accuracyByNetName is not null)
            foreach (var kv in accuracyByNetName) _accuracy[kv.Key] = kv.Value;
        QueueRedraw();
    }

    // ---------------------------------------------------------------------------------------------
    //  NEW public surface — fed by the integration layer
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// QC complaint kinds (<c>complain_weapon_type</c>): 0 = out of ammo, 1 = don't have, 2 = unavailable.
    /// </summary>
    public enum ComplainKind { OutOfAmmo = 0, DontHave = 1, Unavailable = 2 }

    /// <summary>
    /// Raise a complaint bubble over a weapon (QC <c>Weapon_whichtouch</c> / the selection complain path):
    /// the bubble is drawn over the weapon cell for <c>hud_panel_weapons_complainbubble_time</c> seconds then
    /// fades over <c>_complainbubble_fadetime</c>. <paramref name="netName"/> is the weapon to flag (defaults to
    /// the current/last-attempted weapon when null); <paramref name="kind"/> is one of <see cref="ComplainKind"/>.
    /// Also counts as weapon activity, so the panel un-fades (QC sets weapontime on complain).
    /// </summary>
    public void Complain(int kind, string? netName = null)
    {
        _complainKind = (ComplainKind)Mathf.Clamp(kind, 0, 2);
        _complainNetName = string.IsNullOrEmpty(netName) ? SelectedNetName() : netName;
        _complainTime = NowTime();
        NotifyActivity();
        QueueRedraw();
    }

    /// <summary>Convenience strongly-typed overload of <see cref="Complain(int,string?)"/>.</summary>
    public void Complain(ComplainKind kind, string? netName = null) => Complain((int)kind, netName);

    /// <summary>
    /// Mark weapon activity NOW (QC <c>weapontime = time</c>, set on weapon switch / fire / complain). Resets the
    /// timeout fade so the panel re-appears. The net/match layer should call this on any weapon switch or shot.
    /// </summary>
    public void NotifyActivity() => _lastActivity = NowTime();

    /// <summary>
    /// Explicitly set the last-weapon-activity time (QC <c>weapontime</c>) on the panel clock's timebase, for the
    /// timeout fade. Most callers should use <see cref="NotifyActivity"/>; this is for the net layer slaving the
    /// value to a server-side timestamp.
    /// </summary>
    public double LastActivityTime { get => _lastActivity; set => _lastActivity = value; }

    /// <summary>Whether the local client has unlimited ammo (QC <c>IT_UNLIMITED_AMMO</c>) — hides ammo bars when
    /// set. Fed by the net layer; also derived from the player entity when present.</summary>
    public bool InfiniteAmmo { get; set; }

    // ---------------------------------------------------------------------------------------------
    //  Internal animation/fade state (QC statics weapon_pos_current / weapontime / complain_*)
    // ---------------------------------------------------------------------------------------------

    private double _localClock;                 // monotonic fallback clock (QC time when no sim clock)
    private double _lastActivity = double.NegativeInfinity; // QC weapontime (last weapon-use time)
    private bool _activitySeeded;               // seed weapontime to "now" on first live draw (QC weapontime=0@time=0)

    private ComplainKind _complainKind;
    private string? _complainNetName;
    private double _complainTime = double.NegativeInfinity; // QC complain_weapon_time

    // QC weapon_pos_current: the animated top-left of the selection highlight, in panel-local px. -1 = uninit.
    private Vector2 _selPos = new(-1f, -1f);

    public override void _Process(double delta) => _localClock += delta;

    private float NowTime()
    {
        // Prefer the sim clock, but never let a partially-wired Api (null Clock) throw from the per-frame draw
        // path — fall back to the monotonic local clock instead.
        if (Api.Services is not null)
        {
            try { return Api.Clock.Time; }
            catch { /* fall through to the local clock */ }
        }
        return (float)_localClock;
    }

    /// <summary>The currently selected weapon (inventory unless <see cref="CurrentWeapon"/> overrides).</summary>
    private string? SelectedNetName()
    {
        if (!string.IsNullOrEmpty(CurrentWeapon)) return CurrentWeapon;
        Weapon? active = Player is not null ? Inventory.CurrentWeapon(Player) : null;
        return active?.NetName;
    }

    // ---------------------------------------------------------------------------------------------
    //  Draw
    // ---------------------------------------------------------------------------------------------

    protected override void DrawPanel()
    {
        if (Player is null) return;

        var weapons = Weapons.All;
        if (weapons.Count == 0) return;

        // ---- live cvars (read each frame so console/menu edits take effect) ----
        bool onlyOwned = OnlyOwned && CvarF("onlyowned", 1f) != 0f;
        // (when OnlyOwned was explicitly disabled by the net layer, honor that; otherwise the cvar drives it)
        if (!OnlyOwned) onlyOwned = false;

        float aspect = Mathf.Max(0.001f, CvarF("aspect", 1f));
        bool accuracyOn = ShowAccuracy && CvarF("accuracy", 0f) != 0f;
        bool ammoOn = CvarF("ammo", 0f) != 0f;
        int labelMode = (int)CvarF("label", 2f);
        float labelScale = Mathf.Clamp(CvarF("label_scale", 0.3f), 0f, 1f);
        float noncurrentAlpha = Mathf.Clamp(CvarF("noncurrent_alpha", 0.8f), 0f, 1f);
        float noncurrentScale = Mathf.Clamp(CvarF("noncurrent_scale", 0.9f), 0.01f, 1f);
        float selectionSpeed = CvarF("selection_speed", 10f);
        float complainTimeC = Mathf.Max(1f, CvarF("complainbubble_time", 0f));
        float complainFadeC = Mathf.Max(0f, CvarF("complainbubble_fadetime", 1f));
        bool complainOn = CvarF("complainbubble", 1f) != 0f;

        bool infinite = InfiniteAmmo || (Player is not null && Player.UnlimitedAmmo);

        // Seed weapontime to the current clock on the first live draw so the panel appears at FULL alpha when it
        // first has something to show, then times out after `timeout` seconds of no activity — matching the QC
        // initial state (weapontime == 0 == time → not yet timed out). Without this the panel would start pinned
        // at the timed-out min alpha because _lastActivity is -inf.
        if (!_activitySeeded)
        {
            _lastActivity = NowTime();
            _activitySeeded = true;
        }

        // ---- timeout fade (QC Weapons_Fade): pre-fade bg/fg alphas multiplied by the timeout effect ----
        (float fadeBg, float fadeFg) = ComputeTimeoutFade();
        float fgAlpha = LiveFgAlpha * fadeFg;
        float bgAlpha = LiveBgAlpha * fadeBg;
        if (fgAlpha <= 0.001f && bgAlpha <= 0.001f) return; // fully faded out

        // ---- complaint validity (QC: clear complain_weapon when the window lapses) ----
        float now = NowTime();
        bool complainActive = complainOn
            && _complainNetName is not null
            && now - _complainTime < complainTimeC + complainFadeC;

        // ---- which weapons to draw + table layout ----
        string selected = SelectedNetName() ?? "";
        WepSet owned = Player.OwnedWeaponSet;

        // Build the ordered cell list (QC weaponorder: impulse>=0, skip hidden when ghosting, honor onlyowned).
        _cells.Clear();
        foreach (Weapon w in weapons)
        {
            if (w is null) continue;            // registry is trusted, but never deref a null cell in the draw path
            if (w.Impulse < 0) continue;
            bool isOwned = owned.Has(w);
            bool isComplain = complainActive && w.NetName == _complainNetName;
            if (onlyOwned)
            {
                if (!isOwned && !isComplain) continue;
            }
            else
            {
                // QC ghost mode (Weapons_Draw): hide unowned HIDDEN/MUTATORBLOCKED weapons so they don't ghost.
                if (!isOwned && (w.SpawnFlags & (WeaponFlags.Hidden | WeaponFlags.MutatorBlocked)) != 0)
                    continue;
            }
            _cells.Add(w);
        }
        int count = _cells.Count;
        if (count == 0)
        {
            // QC: if onlyowned and nothing to show, the panel draws nothing (no bg either).
            return;
        }

        float pad = Cfg.Padding;
        float innerX = pad, innerY = pad;
        float innerW = Mathf.Max(1f, Size2.X - pad * 2f);
        float innerH = Mathf.Max(1f, Size2.Y - pad * 2f);

        // Table sizing (QC HUD_WEAPONS_GET_FULL_LAYOUT + the onlyowned best-aspect refinement). For the luma
        // thin vertical strip this yields columns≈1, so weapons stack vertically.
        ComputeTable(count, innerW, innerH, aspect, out int columns, out int rows,
            out float cellW, out float cellH, out bool verticalOrder);

        // Center the (possibly smaller) grid within the inner area, like the QC panel re-center.
        float gridW = columns * cellW;
        float gridH = rows * cellH;
        float originX = innerX + (innerW - gridW) * 0.5f;
        float originY = innerY + (innerH - gridH) * 0.5f;

        // Background frame (skin 9-slice; faded by bgAlpha). playtest-bugs #11: draw it hugging the grid, not the
        // full Size2, so the bg auto-sizes to the shown weapons (QC HUD_Weapons onlyowned shrinks panel_size to
        // columns*weapon_size + padding). onlyOwned → the fitted rect; the ghost-all mode keeps the full panel.
        if (bgAlpha > 0.02f)
        {
            Rect2 bgRect = onlyOwned
                ? new Rect2(originX - pad, originY - pad, gridW + 2f * pad, gridH + 2f * pad)
                : new Rect2(Vector2.Zero, Size2);
            DrawBackgroundRect(bgRect, bgAlpha);
        }

        // ---- selection highlight target + slide animation (QC weapon_pos_current) ----
        int selIndex = -1;
        for (int i = 0; i < count; i++) if (_cells[i].NetName == selected) { selIndex = i; break; }

        Vector2 CellPos(int idx)
        {
            int row, col;
            if (verticalOrder) { col = idx % columns; row = idx / columns; }
            else { row = idx % rows; col = idx / rows; }
            return new Vector2(originX + col * cellW, originY + row * cellH);
        }

        if (_selPos.X < 0f) _selPos = new Vector2(originX, originY);
        if (selIndex >= 0)
        {
            Vector2 target = CellPos(selIndex);
            float step = (selectionSpeed <= 0f) ? 999f : (float)GetProcessDeltaTime() * selectionSpeed;
            _selPos = SlideToward(_selPos, target, step);

            // draw the highlight bg behind everything (QC draws it before the icons, while it slides)
            var selRect = new Rect2(_selPos, new Vector2(cellW, cellH));
            if (!DrawSkinPicMod("weapon_current_bg", selRect, new Color(1f, 1f, 1f, fgAlpha)))
                DrawRect(selRect, new Color(1f, 1f, 1f, fgAlpha * 0.18f));
        }

        // ---- draw each weapon cell ----
        for (int i = 0; i < count; i++)
        {
            Weapon w = _cells[i];
            Vector2 cellPos = CellPos(i);
            var cell = new Rect2(cellPos, new Vector2(cellW, cellH));
            bool isOwned = owned.Has(w);
            bool isCurrent = i == selIndex;

            // accuracy overlay (QC drawpic weapon_accuracy tinted by Accuracy_GetColor), owned only.
            if (accuracyOn && isOwned && _accuracy.TryGetValue(w.NetName, out float acc) && acc >= 0f)
            {
                Color accCol = AccuracyColor(acc, fgAlpha);
                if (!DrawSkinPicMod("weapon_accuracy", cell, accCol))
                    DrawRect(cell, new Color(accCol.R, accCol.G, accCol.B, fgAlpha * 0.35f));
            }

            // size/alpha by proximity to the selection (QC noncurrent scaling; here a simple current vs not).
            float cellAlpha = isCurrent ? fgAlpha : fgAlpha * noncurrentAlpha;
            float scale = isCurrent ? 1f : noncurrentScale;
            Rect2 drawCell = Inset(cell, scale);

            DrawWeapon(w, drawCell, isOwned, cellAlpha);

            // label (QC switch over hud_panel_weapons_label) — owned weapons only, like QC.
            if (isOwned && labelMode != 0)
                DrawLabel(w, cellPos, cell.Size, labelMode, labelScale, fgAlpha);

            // per-weapon ammo bar (QC weapon_ammo clipped to a/ammo_full) — owned + has an ammo type.
            if (ammoOn && isOwned && !infinite && w.AmmoType != ResourceType.None)
                DrawAmmoBar(w, cell, aspect, fgAlpha);

            // complaint bubble over the flagged weapon (QC weapon_complainbubble).
            if (complainActive && w.NetName == _complainNetName)
                DrawComplain(cell, now, complainTimeC, complainFadeC, fgAlpha);
        }
    }

    private readonly List<Weapon> _cells = new();

    // ---------------------------------------------------------------------------------------------
    //  Layout (QC HUD_GetTableSize_BestItemAR + the onlyowned aspect refinement)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Choose a (columns, rows) grid for <paramref name="count"/> cells that best fits the panel's aspect and the
    /// desired per-cell aspect ratio, then the cell size + traversal order. Mirrors QC's best-AR table sizing
    /// followed by the onlyowned column/row clamp. For the luma vertical strip this resolves to a single column.
    /// </summary>
    private static void ComputeTable(int count, float panelW, float panelH, float aspect,
        out int columns, out int rows, out float cellW, out float cellH, out bool verticalOrder)
    {
        // QC HUD_GetTableSize_BestItemAR: try every column count, pick the one whose resulting cell AR is closest
        // to `aspect` while filling the panel.
        float bestDiff = float.MaxValue;
        int bestCols = 1, bestRows = count;
        for (int c = 1; c <= count; c++)
        {
            int r = Mathf.CeilToInt(count / (float)c);
            float cw = panelW / c;
            float ch = panelH / r;
            if (ch <= 0f) continue;
            float ar = cw / ch;
            float diff = Mathf.Abs(ar - aspect);
            if (diff < bestDiff - 0.0001f)
            {
                bestDiff = diff;
                bestCols = c;
                bestRows = r;
            }
        }

        columns = bestCols;
        rows = bestRows;
        cellW = panelW / columns;
        cellH = panelH / rows;

        // QC: vertical_order = panel.x / panel.y >= aspect (wide panels fill left-to-right first; tall panels
        // — the luma weapons strip — fill top-to-bottom first, i.e. stack rows).
        verticalOrder = (panelW / panelH) >= aspect;

        // Enlarge cells toward the desired aspect when there's slack (QC's "enlarge a bit when possible"), keeping
        // cells inside the panel and centered (the caller re-centers the grid).
        if (cellW / cellH > aspect) cellW = aspect * cellH;
        else cellH = cellW / aspect;
        // Clamp so the grid never exceeds the panel.
        if (cellW * columns > panelW) cellW = panelW / columns;
        if (cellH * rows > panelH) cellH = panelH / rows;
    }

    private static Vector2 SlideToward(Vector2 cur, Vector2 target, float step)
    {
        // QC weapon_pos_current update: move toward the target by `step` * remaining each axis, clamped so it
        // never overshoots (step >= 1 snaps).
        float nx = cur.X, ny = cur.Y;
        if (cur.X > target.X) nx = Mathf.Max(target.X, cur.X - step * (cur.X - target.X));
        else if (cur.X < target.X) nx = Mathf.Min(target.X, cur.X + step * (target.X - cur.X));
        if (cur.Y > target.Y) ny = Mathf.Max(target.Y, cur.Y - step * (cur.Y - target.Y));
        else if (cur.Y < target.Y) ny = Mathf.Min(target.Y, cur.Y + step * (target.Y - cur.Y));
        return new Vector2(nx, ny);
    }

    private static Rect2 Inset(Rect2 r, float scale)
    {
        if (scale >= 1f) return r;
        var s = new Vector2(r.Size.X * scale, r.Size.Y * scale);
        var p = new Vector2(r.Position.X + (r.Size.X - s.X) * 0.5f, r.Position.Y + (r.Size.Y - s.Y) * 0.5f);
        return new Rect2(p, s);
    }

    // ---------------------------------------------------------------------------------------------
    //  Cell pieces
    // ---------------------------------------------------------------------------------------------

    /// <summary>Draw the weapon icon (QC <c>it.model2</c>) aspect-fit into the cell; ghost-dim when unowned.
    /// Falls back to a color-tinted box + short name so the cell is never invisible.</summary>
    private void DrawWeapon(Weapon w, Rect2 cell, bool owned, float alpha)
    {
        Texture2D? icon = WeaponHud.Icon(w);
        if (icon is not null)
        {
            Vector2 ts = icon.GetSize();
            if (ts.X > 0f && ts.Y > 0f)
            {
                float fit = Mathf.Min(cell.Size.X / ts.X, cell.Size.Y / ts.Y);
                var draw = new Vector2(ts.X * fit, ts.Y * fit);
                var at = new Vector2(
                    cell.Position.X + (cell.Size.X - draw.X) * 0.5f,
                    cell.Position.Y + (cell.Size.Y - draw.Y) * 0.5f);
                // QC ghost-weapon icon: '0.2 0.2 0.2' tint at half alpha when not owned.
                Color mod = owned ? new Color(1f, 1f, 1f, alpha) : new Color(0.2f, 0.2f, 0.2f, alpha * 0.5f);
                DrawTextureRect(icon, new Rect2(at, draw), false, mod);
                return;
            }
        }

        // Fallback: colored box (weapon color) + short name.
        float boxAlpha = owned ? alpha * 0.4f : alpha * 0.12f;
        DrawRect(cell, WeaponHud.ColorOf(w, boxAlpha));
        int fs = Mathf.Max(8, (int)(Mathf.Min(cell.Size.X, cell.Size.Y) * 0.35f));
        string name = ShortName(w.DisplayName, w.NetName);
        DrawTextCentered(new Vector2(cell.Position.X, cell.Position.Y + (cell.Size.Y - fs) * 0.5f),
            cell.Size.X, name, new Color(1f, 1f, 1f, owned ? alpha : alpha * 0.5f), fs);
    }

    /// <summary>Draw the weapon label per QC <c>hud_panel_weapons_label</c> (1 = number, 2 = bind, 3 = name),
    /// scaled by <c>_label_scale</c>, top-left of the cell.</summary>
    private void DrawLabel(Weapon w, Vector2 cellPos, Vector2 cellSize, int mode, float scale, float alpha)
    {
        int fs = Mathf.Max(8, (int)(Mathf.Min(cellSize.X, cellSize.Y) * scale));
        string text = mode switch
        {
            1 => w.Impulse.ToString(),                 // weapon number
            2 => BindLabel(w),                         // bind (falls back to the number)
            3 => (string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName).ToLowerInvariant(),
            _ => "",
        };
        if (string.IsNullOrEmpty(text)) return;
        DrawText(new Vector2(cellPos.X + 2f, cellPos.Y + 1f), text, new Color(1f, 1f, 1f, alpha), fs);
    }

    /// <summary>
    /// QC <c>getcommandkey(weapon_group_N)</c>: the key bound to this weapon's group, falling back to the
    /// number when no bind lookup is available in this port (no bind store on the HUD side yet).
    /// </summary>
    private static string BindLabel(Weapon w) => w.Impulse.ToString();

    /// <summary>
    /// Per-weapon ammo bar (QC <c>weapon_ammo</c>, clipped to <c>a / ammo_full</c>). We emulate the QC clip-area
    /// by drawing the bar art into a partial-height rect from the bottom (the QC bar grows bottom-up in the
    /// vertical strip). Ammo color from <c>_ammo_color</c>; alpha from <c>_ammo_alpha</c> × fg.
    /// </summary>
    private void DrawAmmoBar(Weapon w, Rect2 cell, float aspect, float fgAlpha)
    {
        if (Player is null) return;
        float a = Player.GetResource(w.AmmoType);
        if (a <= 0f) return;

        float full = AmmoFull(w.AmmoType);
        if (full <= 0f) full = 60f;
        float frac = Mathf.Clamp(a / full, 0f, 1f);
        if (frac <= 0f) return;

        Color ammoCol = ParseAmmoColor();
        float ammoAlpha = fgAlpha * Mathf.Clamp(CvarF("ammo_alpha", 1f), 0f, 1f);
        ammoCol.A = ammoAlpha;

        // QC (weapons.qc:613-630): the weapon_ammo art is drawn at the FULL cell, but the draw is CLIPPED to
        // `barsize.x * frac` width via drawsetcliparea — i.e. the icon is revealed left-to-right, NOT stretched
        // into a fraction-width rect (which distorts the art). Emulate the clip with a source-UV region blit:
        // dest = the frac-width left slice of the cell, src = the same frac-portion of the texture's width.
        var tex = TextureCache.GetFirst(
            $"gfx/hud/{HudSkin.SkinName}/weapon_ammo", "gfx/hud/default/weapon_ammo");
        if (tex is null)
        {
            DrawRect(new Rect2(cell.Position, new Vector2(cell.Size.X * frac, cell.Size.Y)),
                new Color(ammoCol.R, ammoCol.G, ammoCol.B, ammoAlpha * 0.5f));
            return;
        }
        Vector2 ts = tex.GetSize();
        var dst = new Rect2(cell.Position, new Vector2(cell.Size.X * frac, cell.Size.Y));
        var src = new Rect2(0f, 0f, ts.X * frac, ts.Y);
        DrawTextureRectRegion(tex, dst, src, ammoCol);
    }

    /// <summary>
    /// Draw the complaint bubble + message over a cell (QC the <c>complain_weapon</c> block), fading per
    /// <paramref name="when"/> (full-display) + <paramref name="fadetime"/>. Three colors from cvars.
    /// </summary>
    private void DrawComplain(Rect2 cell, float now, float when, float fadetime, float fgAlpha)
    {
        float a = fadetime > 0f
            ? (_complainTime + when > now ? 1f : (float)Mathf.Clamp((_complainTime + when + fadetime - now) / fadetime, 0f, 1f))
            : (_complainTime + when > now ? 1f : 0f);
        if (a <= 0f) return;

        float pad = CvarF("complainbubble_padding", 0f);
        var bubble = new Rect2(cell.Position.X + pad, cell.Position.Y + pad,
            Mathf.Max(1f, cell.Size.X - pad * 2f), Mathf.Max(1f, cell.Size.Y - pad * 2f));

        (string s, Color col) = _complainKind switch
        {
            ComplainKind.OutOfAmmo => ("Out of ammo", ParseColor("complainbubble_color_outofammo", new Color(0.8f, 0.11f, 0f))),
            ComplainKind.DontHave => ("Don't have", ParseColor("complainbubble_color_donthave", new Color(0.88f, 0.75f, 0f))),
            _ => ("Unavailable", ParseColor("complainbubble_color_unavailable", new Color(0f, 0.71f, 1f))),
        };

        float bubbleAlpha = fgAlpha * a;
        if (!DrawSkinPicMod("weapon_complainbubble", bubble, new Color(col.R, col.G, col.B, bubbleAlpha)))
            DrawRect(bubble, new Color(col.R, col.G, col.B, bubbleAlpha * 0.6f));

        int fs = Mathf.Max(8, (int)(Mathf.Min(bubble.Size.X, bubble.Size.Y) * 0.45f));
        DrawTextCentered(new Vector2(bubble.Position.X, bubble.Position.Y + (bubble.Size.Y - fs) * 0.5f),
            bubble.Size.X, s, new Color(1f, 1f, 1f, bubbleAlpha), fs);
    }

    // ---------------------------------------------------------------------------------------------
    //  Timeout fade (QC Weapons_Fade) — only the alpha effects (effect 1 fadebg/fgmin and effect 3 full-out)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Compute the (bgFactor, fgFactor) the timeout fade applies this frame (QC <c>Weapons_Fade</c>). We model
    /// the alpha effects (effect 1 = fade toward <c>_fadebgmin</c>/<c>_fadefgmin</c>; effect 3 = fade fully out)
    /// plus the time-in ramp; the off-screen slide (effect 2) is treated as a fade-out for the port. Returns
    /// (1,1) — no fade — when timeout is disabled or there has been recent activity.
    /// </summary>
    private (float bg, float fg) ComputeTimeoutFade()
    {
        float timeout = CvarF("timeout", 1f);
        if (timeout <= 0f) return (1f, 1f);

        int effect = (int)CvarF("timeout_effect", 1f);
        if (effect == 0) return (1f, 1f);

        float speedIn = Mathf.Max(0.0001f, CvarF("timeout_speed_in", 0.25f));
        float speedOut = Mathf.Max(0.0001f, CvarF("timeout_speed_out", 0.75f));
        float fadeBgMin = Mathf.Clamp(CvarF("timeout_fadebgmin", 0.4f), 0f, 1f);
        float fadeFgMin = Mathf.Clamp(CvarF("timeout_fadefgmin", 0.4f), 0f, 1f);

        float now = NowTime();
        float since = now - (float)_lastActivity;

        if (since >= timeout) // timed out → apply fade-out
        {
            float f = Mathf.Clamp((since - timeout) / speedOut, 0f, 1f);
            if (effect == 1)
                return (fadeBgMin * f + (1f - f), fadeFgMin * f + (1f - f));
            // effect 2 (slide) and 3 (fade) → fade fully out for the port
            float v = 1f - f;
            return (v, v);
        }

        // recently active → time-in ramp (fade back IN over speed_in)
        float fi = Mathf.Clamp(since / speedIn, 0f, 1f);
        if (effect == 1)
            return (fadeBgMin * (1f - fi) + fi, fadeFgMin * (1f - fi) + fi);
        return (fi, fi);
    }

    // ---------------------------------------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>QC ammo "full" reference per resource (<c>hud_panel_weapons_ammo_full_*</c>), luma defaults.</summary>
    private float AmmoFull(ResourceType t) => t switch
    {
        ResourceType.Shells => CvarF("ammo_full_shells", 60f),
        ResourceType.Bullets => CvarF("ammo_full_nails", 200f),
        ResourceType.Rockets => CvarF("ammo_full_rockets", 160f),
        ResourceType.Cells => CvarF("ammo_full_cells", 180f),
        ResourceType.Fuel => CvarF("ammo_full_fuel", 100f),
        _ => 60f,
    };

    private Color ParseAmmoColor() => ParseColor("ammo_color", new Color(0.58f, 1f, 0.04f));

    /// <summary>Parse a "r g b" panel cvar into a color, with a fallback when unset/malformed.</summary>
    private Color ParseColor(string suffix, Color fallback)
    {
        string s = CvarStr(suffix);
        return TryParseRgb(s, out Color c) ? c : fallback;
    }

    /// <summary>Draw a skin pic (skin→default) modulated by color; thin wrapper over <see cref="DrawSkinPicFirst"/>.</summary>
    private bool DrawSkinPicMod(string bareName, Rect2 local, Color modulate)
        => DrawSkinPicFirst(local, modulate,
            $"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");

    /// <summary>Accuracy tint (QC <c>Accuracy_GetColor</c>: red at 0% → yellow mid → green at 100%).</summary>
    private static Color AccuracyColor(float acc, float alpha)
    {
        acc = Mathf.Clamp(acc, 0f, 1f);
        Color c = acc >= 0.5f
            ? new Color(Mathf.Lerp(1f, 0.2f, (acc - 0.5f) * 2f), 1f, 0.2f)   // yellow -> green
            : new Color(1f, Mathf.Lerp(0.2f, 1f, acc * 2f), 0.2f);            // red -> yellow
        c.A = alpha;
        return c;
    }

    /// <summary>Pick a compact label for a fallback weapon cell (display name if short, else the NetName).</summary>
    private static string ShortName(string display, string netName)
    {
        string s = string.IsNullOrEmpty(display) ? netName : display;
        return s.Length <= 10 ? s : s.Substring(0, 10);
    }

    // ---------------------------------------------------------------------------------------------
    //  Behavior cvar defaults (QC hud_panel_weapons_* from the luma config) — invoked by HudConfig reflection
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Register this panel's behaviour-cvar defaults (QC <c>hud_panel_weapons_*</c> from the luma config).
    /// Invoked once at boot by <see cref="HudConfig.RegisterDefaults"/> via reflection. <c>Register</c> is
    /// idempotent so any exec'd cfg / user <c>seta</c> wins.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags Save = CvarFlags.Save;
        c.Register("hud_panel_weapons_accuracy", "0", Save);
        c.Register("hud_panel_weapons_label", "2", Save);
        c.Register("hud_panel_weapons_label_scale", "0.3", Save);
        c.Register("hud_panel_weapons_complainbubble", "1", Save);
        c.Register("hud_panel_weapons_complainbubble_padding", "0", Save);
        c.Register("hud_panel_weapons_complainbubble_time", "0", Save);
        c.Register("hud_panel_weapons_complainbubble_fadetime", "1", Save);
        c.Register("hud_panel_weapons_complainbubble_color_outofammo", "0.8 0.11 0", Save);
        c.Register("hud_panel_weapons_complainbubble_color_donthave", "0.88 0.75 0", Save);
        c.Register("hud_panel_weapons_complainbubble_color_unavailable", "0 0.71 1", Save);
        c.Register("hud_panel_weapons_ammo", "0", Save);
        c.Register("hud_panel_weapons_ammo_color", "0.58 1 0.04", Save);
        c.Register("hud_panel_weapons_ammo_alpha", "1", Save);
        c.Register("hud_panel_weapons_ammo_full_shells", "60", Save);
        c.Register("hud_panel_weapons_ammo_full_nails", "200", Save);
        c.Register("hud_panel_weapons_ammo_full_rockets", "160", Save);
        c.Register("hud_panel_weapons_ammo_full_cells", "180", Save);
        c.Register("hud_panel_weapons_ammo_full_fuel", "100", Save);
        c.Register("hud_panel_weapons_aspect", "1", Save);
        c.Register("hud_panel_weapons_timeout", "1", Save);
        c.Register("hud_panel_weapons_timeout_effect", "1", Save);
        c.Register("hud_panel_weapons_timeout_fadebgmin", "0.4", Save);
        c.Register("hud_panel_weapons_timeout_fadefgmin", "0.4", Save);
        c.Register("hud_panel_weapons_timeout_speed_in", "0.25", Save);
        c.Register("hud_panel_weapons_timeout_speed_out", "0.75", Save);
        c.Register("hud_panel_weapons_onlyowned", "1", Save);
        c.Register("hud_panel_weapons_noncurrent_alpha", "0.8", Save);
        c.Register("hud_panel_weapons_noncurrent_scale", "0.9", Save);
        c.Register("hud_panel_weapons_selection_radius", "0", Save);
        c.Register("hud_panel_weapons_selection_speed", "10", Save);
        c.Register("hud_panel_weapons_orderbyimpulse", "0", Save);
    }
}
