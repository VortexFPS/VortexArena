using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Nades;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Ammunition readout — port of Base/.../qcsrc/client/hud/panel/ammo.qc (HUD panel #1). This is now a
/// faithful port of the QC <c>HUD_Ammo</c> / <c>DrawAmmoItem</c> pair:
/// <list type="bullet">
///   <item>the ammo pools are laid out in a grid (QC <c>HUD_GetRowCount</c> + the 1:3-aspect clamp), each
///         cell drawing the pool's skin <b>icon</b> (<c>ammo_shells</c>/<c>_bullets</c>/<c>_rockets</c>/
///         <c>_cells</c>/<c>_fuel</c>, QC <c>ammoType.m_icon</c>) beside its count;</item>
///   <item>the <b>current</b> weapon's pool gets the <c>ammo_current_bg</c> highlight backing and full
///         alpha; non-current pools are dimmed (<c>hud_panel_ammo_noncurrent_alpha</c>) and shrunk
///         (<c>hud_panel_ammo_noncurrent_scale</c>); empty non-current pools are "shadowed" (black icon,
///         half the non-current alpha);</item>
///   <item>icons can be aligned left or right of the number (<c>hud_panel_ammo_iconalign</c>); the count
///         text can be toggled (<c>hud_panel_ammo_text</c>) and tinted low-ammo red (count &lt; 10) or
///         infinite-green (<c>IT_UNLIMITED_AMMO</c> → "∞", QC U+221E);</item>
///   <item>an optional per-cell ammo <b>progressbar</b> (<c>hud_panel_ammo_progressbar</c>, fraction =
///         <c>ammo / hud_panel_ammo_maxammo</c>, art <c>hud_panel_ammo_progressbar_name</c>);</item>
///   <item><c>hud_panel_ammo_onlycurrent</c> shows just the active weapon's pool, drawn full-size.</item>
/// </list>
///
/// The active weapon comes from the inventory — <see cref="Inventory.CurrentWeapon"/> reads the player's
/// <see cref="Entity.ActiveWeaponId"/> (QC STAT(ACTIVEWEAPON)/<c>wepent.switchweapon</c>) — and the
/// *current* pool is that weapon's own ammo type via <see cref="WeaponHud.AmmoType"/> (QC
/// <c>wep.ammo_type</c>). Each amount is read live from the <see cref="Player"/> via
/// <see cref="Resources.GetResource"/>; infinite ammo from <see cref="PlayerPhysicsState.UnlimitedAmmo"/>
/// (QC <c>STAT(ITEMS) &amp; IT_UNLIMITED_AMMO</c>).
/// </summary>
public partial class AmmoPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>).</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// Optional override for the active weapon by NetName. Normally left null so the panel reads the
    /// player's equipped weapon from the inventory; the net/demo layer may set it to force a specific
    /// weapon's pool to highlight (QC switchweapon preview).
    /// </summary>
    public string? CurrentWeapon { get; set; }

    /// <summary>QC <c>hud_panel_ammo_onlycurrent</c>: show only the active weapon's pool, drawn large.
    /// Kept as a public property (net/demo layer may force it); when null the live cvar wins.</summary>
    public bool OnlyCurrent { get; set; }

    // ---- nades-mutator bonus-nade cell (QC DrawAmmoNades, cl_nades.qc:86) ----

    /// <summary>QC <c>STAT(NADE_BONUS)</c>: banked bonus-nade count (0..3), fed from the owning player's
    /// <see cref="NetEntityState.NadeBonus"/>. When &gt; 0 (or <see cref="NadeBonusScoreFrac"/> &gt; 0) the
    /// panel reserves one extra cell drawing the bonus-nade readout.</summary>
    public int NadeBonusCount { get; set; }

    /// <summary>QC <c>STAT(NADE_BONUS_TYPE)</c>: the <see cref="NadeRegistry"/> id (0..11) of the banked
    /// bonus nade; 0 means "random" and draws the Normal icon with a cycling rainbow tint.</summary>
    public int NadeBonusTypeId { get; set; }

    /// <summary>QC <c>STAT(NADE_BONUS_SCORE)</c>: 0..1 fraction of progress toward the next bonus nade,
    /// drawn as the cell's progressbar.</summary>
    public float NadeBonusScoreFrac { get; set; }

    // ---- luma behavior defaults (registered below; read live each frame so console/menu edits apply) ----
    private const float DefaultNoncurrentAlpha = 0.6f;
    private const float DefaultNoncurrentScale = 0.4f;
    private const float DefaultMaxAmmo = 40f;
    private const string DefaultProgressbarName = "progressbar";

    // QC ammoType.m_color (common/resources/all.inc) — the per-pool tint folded into the text + bar color.
    private static readonly Color ColShells = new(0.604f, 0.647f, 0.671f);
    private static readonly Color ColBullets = new(0.678f, 0.941f, 0.522f);
    private static readonly Color ColRockets = new(0.918f, 0.686f, 0.525f);
    private static readonly Color ColCells = new(0.545f, 0.882f, 0.969f);
    private static readonly Color ColFuel = new(0.984f, 0.878f, 0.506f);

    // Every ammo resource in QC default_order_resources order, each with its skin-icon (QC ammoType.m_icon),
    // m_color tint, and CSQC m_hidden flag (all.inc). FUEL is m_hidden in CSQC ("displayed in a separate
    // panel") so it is NOT drawn in the grid — but the onlycurrent path still draws the current weapon's pool
    // even when that is Fuel (QC doesn't check m_hidden there), so Fuel stays in the table for icon/color
    // lookup. The grid loop and the layout count use only the NON-hidden subset (QC AMMO_COUNT == 4).
    private static readonly (ResourceType Res, string Icon, Color Color, bool Hidden)[] Pools =
    {
        (ResourceType.Shells,  "ammo_shells",  ColShells,  false),
        (ResourceType.Bullets, "ammo_bullets", ColBullets, false),
        (ResourceType.Rockets, "ammo_rockets", ColRockets, false),
        (ResourceType.Cells,   "ammo_cells",   ColCells,   false),
        (ResourceType.Fuel,    "ammo_fuel",    ColFuel,    true),  // CSQC m_hidden — separate panel
    };

    /// <summary>Number of non-hidden ammo pools drawn in the grid (QC <c>AMMO_COUNT</c> == 4: Fuel is hidden).</summary>
    private static int VisiblePoolCount()
    {
        int n = 0;
        foreach (var p in Pools) if (!p.Hidden) n++;
        return n;
    }

    /// <summary>
    /// Resolve the active weapon's ammo type. Prefers the live inventory weapon
    /// (<see cref="Inventory.CurrentWeapon"/> → its <c>AmmoType</c>); if a <see cref="CurrentWeapon"/>
    /// NetName override is set, that weapon's ammo type wins. <see cref="ResourceType.None"/> when the
    /// active weapon uses no standard pool (e.g. Blaster).
    /// </summary>
    private ResourceType ActiveAmmoType()
    {
        if (Player is null) return ResourceType.None;

        // Override by NetName (net/demo-driven switch preview).
        if (!string.IsNullOrEmpty(CurrentWeapon))
        {
            Weapon? byName = Weapons.ByName(CurrentWeapon);
            if (byName is not null) return WeaponHud.AmmoType(byName);
        }

        Weapon? active = Inventory.CurrentWeapon(Player);
        return active is not null ? WeaponHud.AmmoType(active) : ResourceType.None;
    }

    // (R5) Change-gate: the drawn output is a pure function of the ammo pool counts, the active pool, the
    // infinite-ammo flag, onlycurrent, the alive (hide_ondeath) state, the scoreboard fade alpha, and the panel
    // size. Snapshot those (all cheap, no canvas work) and skip the per-frame DrawPanel re-record when unchanged.
    private int _lShells = -1, _lBullets = -1, _lRockets = -1, _lCells = -1, _lFuel = -1, _lCurrent = -2, _lAlpha = -1, _lW = -1, _lH = -1;
    private bool _lInfinite, _lOnlyCurrent, _lAlive;

    public override bool NeedsRedraw()
    {
        if (Player is null) return false;
        bool alive = Player.GetResource(ResourceType.Health) > 0f;
        if (!alive)
        {
            // hide_ondeath draws nothing; redraw once on the alive→dead edge to clear the last frame, then idle.
            if (_lAlive) { _lAlive = false; return true; }
            return false;
        }

        int shells = AmmoInt(ResourceType.Shells), bullets = AmmoInt(ResourceType.Bullets),
            rockets = AmmoInt(ResourceType.Rockets), cells = AmmoInt(ResourceType.Cells), fuel = AmmoInt(ResourceType.Fuel);
        int current = (int)ActiveAmmoType();
        bool infinite = Player.UnlimitedAmmo;
        bool onlyCurrent = OnlyCurrent || CvarBool("onlycurrent");
        int alpha = (int)(LiveFgAlpha * 255f);
        int w = (int)Size2.X, h = (int)Size2.Y;

        if (alive == _lAlive && shells == _lShells && bullets == _lBullets && rockets == _lRockets
            && cells == _lCells && fuel == _lFuel && current == _lCurrent && infinite == _lInfinite
            && onlyCurrent == _lOnlyCurrent && alpha == _lAlpha && w == _lW && h == _lH)
            return false;

        _lAlive = alive; _lShells = shells; _lBullets = bullets; _lRockets = rockets; _lCells = cells; _lFuel = fuel;
        _lCurrent = current; _lInfinite = infinite; _lOnlyCurrent = onlyCurrent; _lAlpha = alpha; _lW = w; _lH = h;
        return true;
    }

    private int AmmoInt(ResourceType res)
    {
        float raw = Player!.GetResource(res);
        return float.IsFinite(raw) ? Mathf.RoundToInt(raw) : 0;
    }

    protected override void DrawPanel()
    {
        if (Player is null) return;
        if (Player.GetResource(ResourceType.Health) <= 0f) return; // QC hide_ondeath

        DrawBackground();

        ResourceType current = ActiveAmmoType();
        bool infinite = Player.UnlimitedAmmo; // QC STAT(ITEMS) & IT_UNLIMITED_AMMO
        bool onlyCurrent = OnlyCurrent || CvarBool("onlycurrent");

        // QC HUD_Ammo: pos/mySize are inset by panel_bg_padding (the RESOLVED per-panel padding, Cfg.Padding —
        // already clamped >= 0 in HudPanel.Resolve), NOT the legacy compile-time const. Reading the const left
        // the grid mis-inset whenever the user/skin set a non-default hud_panel_bg_padding.
        float pad = Cfg.Padding;
        var pos = new Vector2(pad, pad);
        var size = new Vector2(Size2.X - pad * 2f, Size2.Y - pad * 2f);
        // Self-blank: a degenerate (too-small or zero) content rect would make the cell math produce NaN/garbage
        // (and divide-by-zero in the aspect clamp via cell.Y). Draw nothing rather than off-panel junk.
        if (size.X <= 0f || size.Y <= 0f) return;

        // QC HUD_Ammo: when the nades mutator is active and the player has banked bonus nades (or progress
        // toward one), a dedicated bonus-nade cell is added to the grid alongside the ammo pools
        // (cl_nades.qc DrawAmmoNades). Mirror that here: reserve ONE extra cell when nades are active.
        bool showNades = NadeBonusCount > 0 || NadeBonusScoreFrac > 0f;

        // QC: onlycurrent → 1 cell; else AMMO_COUNT (== 4: the non-hidden pools — Fuel is hidden here),
        // plus one bonus-nade cell when active.
        int count = onlyCurrent ? 1 : VisiblePoolCount();
        if (!onlyCurrent && showNades) count++;

        // QC HUD_GetRowCount(count, size, 3): pick the row count whose resulting cells stay closest to the
        // ideal 1:3 (width:height treated as 3:1) aspect, then derive columns.
        int rows = GetRowCount(count, size, 3f);
        if (rows < 1) rows = 1;
        int columns = Mathf.CeilToInt(count / (float)rows);
        if (columns < 1) columns = 1;

        var cell = new Vector2(size.X / columns, size.Y / rows);

        // QC: clamp each cell toward the 1:3 aspect, centering the leftover (offset).
        var offset = Vector2.Zero;
        if (cell.X / cell.Y > 3f)
        {
            float newSize = 3f * cell.Y;
            offset.X = cell.X - newSize;
            pos.X += offset.X * 0.5f;
            cell.X = newSize;
        }
        else
        {
            float newSize = (1f / 3f) * cell.X;
            offset.Y = cell.Y - newSize;
            pos.Y += offset.Y * 0.5f;
            cell.Y = newSize;
        }

        if (onlyCurrent)
        {
            DrawAmmoItem(pos, cell, current, true, infinite, current);
            return;
        }

        // QC: IL_EACH(default_order_resources, it.instanceOfAmmoResource && !it.m_hidden, …) — iterate the
        // non-hidden ammo pools in order, filling cells row-major (down a column, then across).
        int row = 0, column = 0;
        for (int i = 0; i < Pools.Length; i++)
        {
            (ResourceType res, _, _, bool hidden) = Pools[i];
            if (hidden) continue; // Fuel — shown in a separate panel (QC m_hidden)

            var cellPos = new Vector2(
                pos.X + column * (cell.X + offset.X),
                pos.Y + row * (cell.Y + offset.Y));
            DrawAmmoItem(cellPos, cell, res, res == current && current != ResourceType.None, infinite, current);

            row++;
            if (row >= rows) { row = 0; column++; }
        }

        // QC DrawAmmoNades: the banked bonus-nade cell takes the next free grid slot (after the ammo pools).
        if (showNades)
        {
            var nadePos = new Vector2(
                pos.X + column * (cell.X + offset.X),
                pos.Y + row * (cell.Y + offset.Y));
            DrawAmmoNades(nadePos, cell);
        }
    }

    /// <summary>
    /// Port of QC <c>DrawAmmoItem</c>: draw one ammo pool's icon + count (+ optional progressbar) in the
    /// given cell. Non-current pools shrink toward their cell center and dim; empty non-current pools are
    /// "shadowed" (black icon at half the non-current alpha). The current pool gets the
    /// <c>ammo_current_bg</c> backing. <paramref name="current"/> is only used to skip drawing a non-pool
    /// (RES_NONE) cell in the onlycurrent path.
    /// </summary>
    private void DrawAmmoItem(Vector2 pos, Vector2 size, ResourceType res, bool isCurrent, bool isInfinite, ResourceType current)
    {
        // QC: RES_NONE pools are not drawn at all (e.g. Blaster has no ammo_type in onlycurrent mode).
        if (res == ResourceType.None)
        {
            if (current == ResourceType.None)
                DrawNoPoolCell(pos, size); // port extension: show "∞" so the panel is never blank-on-Blaster
            return;
        }

        Color poolColor = PoolColor(res);
        // Guard a corrupt/uninitialized net read: a NaN/Infinity resource would round to int.MinValue (garbage
        // "-2147483648" text) and poison the progressbar fraction. Treat non-finite as 0 (empty pool).
        float rawAmmo = Player!.GetResource(res);
        int ammo = float.IsFinite(rawAmmo) ? Mathf.RoundToInt(rawAmmo) : 0;

        // QC: non-current pools shrink toward their cell center (noncurrent_scale, clamped 0.01..1).
        if (!isCurrent)
        {
            float scale = Mathf.Clamp(CvarF("noncurrent_scale", DefaultNoncurrentScale), 0.01f, 1f);
            pos += (size - size * scale) * 0.5f;
            size *= scale;
        }

        // QC: iconalign places the icon to the LEFT (0) or RIGHT (1) of the number; the icon is a square of
        // side = cell height (drawpic_aspect_skin '1 1 0' * mySize.y) and the number's box is
        // (2/3)*mySize.x wide × mySize.y tall (QC drawstring_aspect width = eX*(2/3)*mySize.x + eY*mySize.y).
        // Since the cell width was aspect-clamped to ~3*mySize.y, icon(1·y) + text(2·y) tile the full cell.
        bool iconRight = CvarBool("iconalign");
        Rect2 iconRect, textRect;
        float iconSide = size.Y;
        float textWidth = (2f / 3f) * size.X;
        if (iconRight)
        {
            iconRect = new Rect2(pos.X + 2f * size.Y, pos.Y, iconSide, iconSide);
            textRect = new Rect2(pos.X, pos.Y, textWidth, size.Y);
        }
        else
        {
            iconRect = new Rect2(pos.X, pos.Y, iconSide, iconSide);
            textRect = new Rect2(pos.X + size.Y, pos.Y, textWidth, size.Y);
        }

        // QC shadowed = empty, non-current, non-infinite → black icon at reduced alpha.
        bool isShadowed = ammo <= 0 && !isCurrent && !isInfinite;

        Color iconColor = isShadowed ? new Color(0f, 0f, 0f) : new Color(1f, 1f, 1f);

        // QC text base color: infinite → green; shadowed → black; low (<10) → red; else white. Then folded
        // 0.6*textColor + 0.4*m_color (per-pool tint).
        Color textBase;
        if (isInfinite)      textBase = new Color(0.2f, 0.95f, 0f);
        else if (isShadowed) textBase = new Color(0f, 0f, 0f);
        else if (ammo < 10)  textBase = new Color(0.8f, 0.04f, 0f);
        else                 textBase = new Color(1f, 1f, 1f);

        // QC alpha: current → fg; shadowed → fg * noncurrent_alpha * 0.5; else → fg * noncurrent_alpha.
        float noncurrentAlpha = Mathf.Clamp(CvarF("noncurrent_alpha", DefaultNoncurrentAlpha), 0f, 1f);
        float alpha;
        if (isCurrent)       alpha = LiveFgAlpha;
        else if (isShadowed) alpha = LiveFgAlpha * noncurrentAlpha * 0.5f;
        else                 alpha = LiveFgAlpha * noncurrentAlpha;
        if (alpha <= 0.001f) return;

        // --- current highlight backing (QC ammo_current_bg) ---
        if (isCurrent)
        {
            var bgRect = new Rect2(pos, size);
            if (!DrawSkinPic("ammo_current_bg", bgRect, new Color(1f, 1f, 1f, LiveFgAlpha)))
                DrawRect(bgRect, new Color(1f, 1f, 1f, LiveFgAlpha * 0.14f)); // primitive fallback
        }

        // --- optional progressbar (QC hud_panel_ammo_progressbar) ---
        if (ammo > 0 && CvarBool("progressbar"))
        {
            float maxAmmo = CvarF("maxammo", DefaultMaxAmmo);
            if (maxAmmo < 1f) maxAmmo = DefaultMaxAmmo;
            float frac = Mathf.Clamp(ammo / maxAmmo, 0f, 1f);
            // Clamp the user/skin xoffset to [0,1): a negative or >1 cvar would push the bar off the panel to the
            // left or make barSize.X negative (QC leaves it unbounded, but reference skins only use 0..small).
            float xoff = Mathf.Clamp(CvarF("progressbar_xoffset", 0f), 0f, 0.99f);
            var barPos = new Vector2(pos.X + xoff * size.X, pos.Y);
            var barSize = new Vector2(size.X - xoff * size.X, size.Y);
            // QC bar color = textColor*0.2 + m_color*0.8.
            Color barColor = Mix(textBase, 0.2f, poolColor, 0.8f);
            barColor.A = GlobalF("hud_progressbar_alpha", 0.5f) * alpha;
            DrawProgressBar(new Rect2(barPos, barSize), frac, barColor);
        }

        // --- count text (QC hud_panel_ammo_text), tinted 0.6*textBase + 0.4*m_color ---
        if (CvarF("text", 1f) != 0f)
        {
            string text = isInfinite ? "∞" : ammo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Color textColor = Mix(textBase, 0.6f, poolColor, 0.4f);
            textColor.A = alpha;
            // QC drawstring_aspect: draw as large as possible (height = sz.y = cell height) inside the box,
            // scaling DOWN only when the text is wider than the box (DRAWSTRING_ASPECT_SCALE), centered.
            int fontPx = Mathf.Max(8, Mathf.RoundToInt(textRect.Size.Y * 0.85f));
            float textW = MeasureText(text, fontPx);
            if (textW > textRect.Size.X && textW > 0.001f) // shrink to fit width (QC aspect downscale)
                fontPx = Mathf.Max(8, Mathf.RoundToInt(fontPx * textRect.Size.X / textW));
            float topY = textRect.Position.Y + (textRect.Size.Y - fontPx) * 0.5f;
            DrawTextCentered(new Vector2(textRect.Position.X, topY), textRect.Size.X, text, textColor, fontPx);
        }

        // --- pool icon (QC drawpic_aspect_skin ammoType.m_icon, square = cell height) ---
        Color iconMod = new(iconColor.R, iconColor.G, iconColor.B, alpha);
        string iconName = Pools[(int)PoolIndex(res)].Icon;
        if (!DrawSkinPic(iconName, iconRect, iconMod))
        {
            // Art missing — draw a small tinted box so the pool is never invisible.
            var c = poolColor; c.A = alpha * 0.6f;
            DrawRect(iconRect, c);
        }
    }

    /// <summary>
    /// Port-extension cell for a current weapon with no ammo pool (Blaster): show the infinity glyph centered
    /// so the onlycurrent panel is never blank. (QC simply draws nothing — RES_NONE early-returns — but the
    /// stock blaster carries IT_UNLIMITED-style "no ammo" semantics, so "∞" reads better here.)
    /// </summary>
    private void DrawNoPoolCell(Vector2 pos, Vector2 size)
    {
        int s = Mathf.Max(12, Mathf.RoundToInt(size.Y * 0.7f));
        float topY = pos.Y + (size.Y - s) * 0.5f;
        DrawTextCentered(new Vector2(pos.X, topY), size.X, "∞",
            new Color(0.2f, 0.95f, 0f, LiveFgAlpha), s);
    }

    /// <summary>
    /// Port of QC <c>DrawAmmoNades</c> (cl_nades.qc:86): the banked bonus-nade cell. Draws the per-type nade
    /// icon (<see cref="NadeDef"/> <c>m_icon</c> "nade_&lt;type&gt;"; the Normal icon cycled through a rainbow
    /// tint for the random/type-0 sentinel), the bonus-nade count text, and a progressbar of
    /// <see cref="NadeBonusScoreFrac"/> toward the next bonus nade — all laid out like
    /// <see cref="DrawAmmoItem"/> (icon = a square of side = cell height; text in the (2/3)·width box; icon
    /// align follows <c>hud_panel_ammo_iconalign</c>). The bar is tinted the nade's <c>m_color</c>.
    /// </summary>
    private void DrawAmmoNades(Vector2 pos, Vector2 size)
    {
        // QC: REGISTRY_GET(Nades, max(1, bonusType)) — type 0 (random) borrows the Normal nade's def/icon.
        int typeId = NadeBonusTypeId;
        NadeDef? def = NadeRegistry.ById(typeId <= 0 ? 1 : typeId) ?? NadeRegistry.Normal;

        // QC nadeColor: the typed nade's m_color, or a time-cycled rainbow for the random (type 0) sentinel.
        Color nadeColor;
        bool isRandom = typeId == 0;
        if (!isRandom && def is not null)
            nadeColor = new Color(def.Color.X, def.Color.Y, def.Color.Z);
        else
            nadeColor = RainbowHsv(NadeTime()); // QC hsv_to_rgb('time%2pi' 1 1)

        // QC icon: m_icon == "nade_<netname>" except the monster nade (netname "pokenade" → "nade_monster").
        // The random sentinel always uses the Normal icon "nade_normal".
        string nadeIcon = isRandom ? "nade_normal" : NadeIconName(def);

        // QC iiconalign/text-box layout — identical to DrawAmmoItem (icon = '1 1 0'*mySize.y square).
        bool iconRight = CvarBool("iconalign");
        float iconSide = size.Y;
        float textWidth = (2f / 3f) * size.X;
        Rect2 iconRect, textRect;
        if (iconRight)
        {
            iconRect = new Rect2(pos.X + 2f * size.Y, pos.Y, iconSide, iconSide);
            textRect = new Rect2(pos.X, pos.Y, textWidth, size.Y);
        }
        else
        {
            iconRect = new Rect2(pos.X, pos.Y, iconSide, iconSide);
            textRect = new Rect2(pos.X + size.Y, pos.Y, textWidth, size.Y);
        }

        // QC DrawNadeProgressBar: full-cell bar (xoffset inset), color = nadeColor, alpha = hud_progressbar_alpha*fg.
        float frac = Mathf.Clamp(NadeBonusScoreFrac, 0f, 1f);
        if (frac > 0f)
        {
            float xoff = Mathf.Clamp(CvarF("progressbar_xoffset", 0f), 0f, 0.99f);
            var barPos = new Vector2(pos.X + xoff * size.X, pos.Y);
            var barSize = new Vector2(size.X - xoff * size.X, size.Y);
            Color barColor = nadeColor;
            barColor.A = GlobalF("hud_progressbar_alpha", 0.5f) * LiveFgAlpha;
            DrawProgressBar(new Rect2(barPos, barSize), frac, barColor);
        }

        // QC count text: drawn plain white '1 1 1' at panel_fg_alpha (NOT the low-ammo / pool-tint scheme).
        if (CvarF("text", 1f) != 0f)
        {
            string text = NadeBonusCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var textColor = new Color(1f, 1f, 1f, LiveFgAlpha);
            // drawstring_aspect: height = cell height, shrink to fit width (same as DrawAmmoItem).
            int fontPx = Mathf.Max(8, Mathf.RoundToInt(textRect.Size.Y * 0.85f));
            float textW = MeasureText(text, fontPx);
            if (textW > textRect.Size.X && textW > 0.001f)
                fontPx = Mathf.Max(8, Mathf.RoundToInt(fontPx * textRect.Size.X / textW));
            float topY = textRect.Position.Y + (textRect.Size.Y - fontPx) * 0.5f;
            DrawTextCentered(new Vector2(textRect.Position.X, topY), textRect.Size.X, text, textColor, fontPx);
        }

        // QC icon: drawpic_aspect_skin(nadeIcon, '1 1 1' when typed else nadeColor) at panel_fg_alpha.
        Color iconTint = isRandom ? nadeColor : new Color(1f, 1f, 1f);
        var iconMod = new Color(iconTint.R, iconTint.G, iconTint.B, LiveFgAlpha);
        if (!DrawSkinPic(nadeIcon, iconRect, iconMod))
        {
            // Art missing — draw a small tinted box so the bonus-nade cell is never invisible.
            var c = nadeColor; c.A = LiveFgAlpha * 0.6f;
            DrawRect(iconRect, c);
        }
    }

    /// <summary>Skin-pic name for a nade type's icon (QC <c>m_icon</c>): "nade_&lt;netname&gt;", with the one
    /// QC override where the monster nade (netname "pokenade") uses "nade_monster".</summary>
    private static string NadeIconName(NadeDef? def)
    {
        if (def is null) return "nade_normal";
        return def.NetName == "pokenade" ? "nade_monster" : "nade_" + def.NetName;
    }

    /// <summary>Client game time for the random-nade rainbow cycle (QC <c>time</c>).</summary>
    private float NadeTime()
    {
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)(Time.GetTicksMsec() / 1000.0);
    }

    /// <summary>
    /// QC random-nade rainbow: <c>hsv_to_rgb((time % 2π, 1, 1))</c>. Ported faithfully via QC
    /// <c>hue_mi_ma_to_rgb(hue, 0, 1)</c> (color.qh) — note the hue is the raw <c>time % 2π</c> (0..~6.28)
    /// treated as a 0..6 sextant index, NOT a normalized 0..1 hue, so the cycle period is 2π seconds.
    /// </summary>
    private static Color RainbowHsv(float t)
    {
        float hue = t % (2f * Mathf.Pi);
        return HueMiMaToRgb(hue, 0f, 1f);
    }

    /// <summary>Port of QC <c>hue_mi_ma_to_rgb(hue, mi, ma)</c> (lib/color.qh): hue in 0..6 sextants.</summary>
    private static Color HueMiMaToRgb(float hue, float mi, float ma)
    {
        hue -= 6f * Mathf.Floor(hue / 6f);
        float r, g, b;
        if (hue <= 1f)      { r = ma;                       g = hue * (ma - mi) + mi;       b = mi; }
        else if (hue <= 2f) { r = (2f - hue) * (ma - mi) + mi; g = ma;                      b = mi; }
        else if (hue <= 3f) { r = mi;                       g = ma;                         b = (hue - 2f) * (ma - mi) + mi; }
        else if (hue <= 4f) { r = mi;                       g = (4f - hue) * (ma - mi) + mi; b = ma; }
        else if (hue <= 5f) { r = (hue - 4f) * (ma - mi) + mi; g = mi;                      b = ma; }
        else                { r = ma;                       g = mi;                         b = (6f - hue) * (ma - mi) + mi; }
        return new Color(r, g, b);
    }

    /// <summary>
    /// Draw the ammo progressbar (QC <c>HUD_Panel_DrawProgressBar</c>, horizontal, baralign 0). Renders the
    /// <c>hud_panel_ammo_progressbar_name</c> skin art (default "progressbar") clipped to the fill fraction;
    /// falls back to the base <see cref="HudPanel.DrawBar"/> primitive when the art is missing.
    /// </summary>
    private void DrawProgressBar(Rect2 area, float fraction, Color fill)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f || fill.A <= 0.001f) return;

        string barName = CvarStr("progressbar_name");
        if (string.IsNullOrWhiteSpace(barName)) barName = DefaultProgressbarName;

        // Delegate to the shared faithful primitive (QC 3-slice cap render + skin resolve); horizontal,
        // baralign 0 (left). Falls back to a flat fill inside the primitive when the art is missing.
        DrawProgressBar(area, barName, fraction, vertical: false, baralign: 0, fill, fill.A);
    }

    // ---- helpers ----

    /// <summary>Per-pool m_color (QC ammoType.m_color).</summary>
    private static Color PoolColor(ResourceType res) => res switch
    {
        ResourceType.Shells  => ColShells,
        ResourceType.Bullets => ColBullets,
        ResourceType.Rockets => ColRockets,
        ResourceType.Cells   => ColCells,
        ResourceType.Fuel    => ColFuel,
        _ => new Color(1f, 1f, 1f),
    };

    /// <summary>Index of a pool in <see cref="Pools"/> (matches the display order).</summary>
    private static long PoolIndex(ResourceType res)
    {
        for (int i = 0; i < Pools.Length; i++)
            if (Pools[i].Res == res) return i;
        return 0;
    }

    /// <summary>RGB blend <c>a*aw + b*bw</c> (alpha left at 1; caller overwrites). QC vector blends.</summary>
    private static Color Mix(Color a, float aw, Color b, float bw) => new(
        a.R * aw + b.R * bw,
        a.G * aw + b.G * bw,
        a.B * aw + b.B * bw,
        1f);

    /// <summary>
    /// Exact port of QC <c>HUD_GetRowCount(item_count, size, item_aspect)</c> (client/hud/hud.qc): pick the
    /// row count that best tiles <paramref name="itemCount"/> cells of the given target aspect into the panel.
    /// NOTE the QC <c>aspect</c> is <b>height/width</b> (size_y/size_x), so for the wide luma ammo strip this
    /// correctly returns 1 row / N columns (a naive sqrt(count*w/h/aspect) heuristic would wrongly stack rows).
    /// </summary>
    private static int GetRowCount(int itemCount, Vector2 size, float itemAspect)
    {
        if (itemCount <= 1) return 1;
        if (size.X <= 0f) return 1;

        float aspect = size.Y / size.X; // QC: aspect = size_y / size_x
        float rows = Mathf.Floor(
            (Mathf.Sqrt(4f * itemAspect * aspect * itemCount + aspect * aspect) + aspect + 0.5f) * 0.5f);
        return (int)Mathf.Clamp(rows, 1f, itemCount);
    }

    /// <summary>
    /// Register this panel's behaviour-cvar defaults (QC <c>hud_panel_ammo_*</c> from the luma config).
    /// Invoked once at boot by <c>HudConfig.RegisterDefaults</c> via reflection. <c>Register</c> is idempotent
    /// so any exec'd cfg / user <c>seta</c> wins.
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        c.Register("hud_panel_ammo_onlycurrent", "0", CvarFlags.Save);
        c.Register("hud_panel_ammo_noncurrent_alpha", "0.6", CvarFlags.Save);
        c.Register("hud_panel_ammo_noncurrent_scale", "0.4", CvarFlags.Save);
        c.Register("hud_panel_ammo_iconalign", "0", CvarFlags.Save);
        c.Register("hud_panel_ammo_progressbar", "0", CvarFlags.Save);
        c.Register("hud_panel_ammo_progressbar_name", "progressbar", CvarFlags.Save);
        c.Register("hud_panel_ammo_progressbar_xoffset", "0", CvarFlags.Save);
        c.Register("hud_panel_ammo_maxammo", "40", CvarFlags.Save);
        c.Register("hud_panel_ammo_text", "1", CvarFlags.Save);
    }
}
