using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Health + armor readout — feature-complete port of
/// Base/.../qcsrc/client/hud/panel/healtharmor.qc (HUD panel #3). The QC version reads
/// <c>STAT(HEALTH)</c>/<c>STAT(ARMOR)</c>/<c>STAT(FUEL)</c>/<c>STAT(AIR_FINISHED)</c> and draws, per the
/// <c>hud_panel_healtharmor_*</c> cvars, EITHER a single combined bar (by "ideal max damage") OR split
/// health/armor progress bars + numbers tinted by <c>HUD_Get_Num_Color</c>, plus a fuel bar and an
/// underwater oxygen bar.
///
/// This faithfully reproduces the QC feature set:
/// <list type="bullet">
///   <item>combined vs split (<c>_combined</c>), with the same panel-aspect-driven side-by-side vs stacked
///         split, and <c>_flip</c>/<c>_baralign</c>/<c>_iconalign</c> handling.</item>
///   <item>skin progress bars in <c>hud_progressbar_health_color</c>/<c>_armor_color</c>/<c>_fuel_color</c>/
///         <c>_oxygen_color</c> at <c>hud_progressbar_alpha</c>.</item>
///   <item>low-health red pulse (<c>_progressbar_gfx_lowhealth</c> = 40 → <c>blink(0.85,0.15,9)</c>).</item>
///   <item>damage-flash ghost bar (<c>_progressbar_gfx_damage</c>): the pre-damage value bar fades out over a
///         second when you take a big hit.</item>
///   <item>smooth value lerp (<c>_progressbar_gfx_smooth</c>): the bar eases between large value jumps.</item>
///   <item>fuel bar + underwater oxygen bar (blinks once out of air).</item>
///   <item>skin health/armor icons (always the plain <c>health</c>/<c>armor</c> pic, exactly like Base — the old
///         <c>_big</c>/<c>_mega</c> over-max swap was the wrong item-variant art and was removed, playtest #52),
///         numbers tinted by value.</item>
/// </list>
///
/// Real data: pulled live from the local <see cref="Player"/> via the resource accessors
/// (<see cref="Resources.GetResource"/>). Limits come from <see cref="Resources.GetResourceLimit"/> but the
/// bar maxima follow the QC <c>_maxhealth</c>/<c>_maxarmor</c> cvars (200). The timing-sensitive effects
/// (damage flash, smooth lerp, low-health pulse, oxygen blink) need a clock and the air-finished stat which
/// are NOT on the entity, so the net/demo layer feeds <see cref="Now"/> and <see cref="AirFinished"/>; the
/// panel falls back to its own wall clock when <see cref="Now"/> is unset.
/// </summary>
public partial class HealthArmorPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>).</summary>
    public Player? Player { get; set; }

    /// <summary>Match/render clock used for the timed effects (QC <c>time</c>). If &lt; 0 the panel uses its
    /// own per-frame wall clock. The net/demo layer can slave it to the sim clock.</summary>
    public double Now { get; set; } = -1.0;

    /// <summary>QC <c>STAT(AIR_FINISHED)</c>: absolute time the player runs out of air (0 = breathing
    /// normally). Fed by the net/demo layer; drives the underwater oxygen bar. 0 → no oxygen bar.</summary>
    public float AirFinished { get; set; }

    /// <summary>QC <c>armorblockpercent</c> (the <c>g_balance_armor_blockpercent</c> stat) used by the
    /// combined-mode "ideal max damage" math. Defaults to the stock 0.7.</summary>
    public float ArmorBlockPercent { get; set; } = 0.7f;

    // Fallback progress-bar colors (used only if the global cvar is unset / unparseable).
    private static readonly Color DefaultHealthColor = new(0.83f, 0.12f, 0f);
    private static readonly Color DefaultArmorColor = new(0.28f, 0.8f, 0f);
    private static readonly Color DefaultFuelColor = new(0.77f, 0.67f, 0f);
    private static readonly Color DefaultOxygenColor = new(0.1f, 1f, 1f);

    // ---- per-frame smoothing / damage-flash state (QC statics in HUD_HealthArmor) ----
    private float _prevHealth = -1f, _prevArmor = -1f;     // value last frame (for damage/smooth deltas)
    private float _pPrevHealth, _pPrevArmor;               // smoothed (displayed) value last frame
    private float _oldPHealth, _oldPArmor;                 // smoothing anchor value
    private float _oldPHealthTime, _oldPArmorTime;         // smoothing anchor timestamp
    private float _healthBeforeDamage, _armorBeforeDamage; // ghost-bar value
    private float _healthDamageTime, _armorDamageTime;     // ghost-bar trigger time
    private bool _seeded;                                  // first-frame init guard (QC prev_p_health==-1)

    private double _localClock;

    /// <summary>Register the panel's behaviour-cvar defaults (luma). Auto-invoked by reflection from
    /// <c>HudConfig.RegisterDefaults</c>.</summary>
    public static void RegisterDefaults(XonoticGodot.Engine.Simulation.CvarService c)
    {
        const XonoticGodot.Common.Services.CvarFlags save = XonoticGodot.Common.Services.CvarFlags.Save;
        c.Register("hud_panel_healtharmor_combined", "0", save);
        c.Register("hud_panel_healtharmor_flip", "0", save);
        c.Register("hud_panel_healtharmor_iconalign", "3", save);
        c.Register("hud_panel_healtharmor_baralign", "3", save);
        c.Register("hud_panel_healtharmor_progressbar", "1", save);
        c.Register("hud_panel_healtharmor_progressbar_health", "progressbar", save);
        c.Register("hud_panel_healtharmor_progressbar_armor", "progressbar", save);
        c.Register("hud_panel_healtharmor_progressbar_gfx", "1", save);
        c.Register("hud_panel_healtharmor_progressbar_gfx_smooth", "2", save);
        c.Register("hud_panel_healtharmor_progressbar_gfx_damage", "5", save);
        c.Register("hud_panel_healtharmor_progressbar_gfx_lowhealth", "40", save);
        c.Register("hud_panel_healtharmor_text", "1", save);
        c.Register("hud_panel_healtharmor_maxhealth", "200", save);
        c.Register("hud_panel_healtharmor_maxarmor", "200", save);
        c.Register("hud_panel_healtharmor_hide_ondeath", "0", save);
        c.Register("hud_panel_healtharmor_fuelbar_startalpha", "0.3", save);
        c.Register("hud_panel_healtharmor_oxygenbar_startalpha", "0.2", save);
    }

    public override void _Process(double delta) => _localClock += delta;

    // Use the slaved sim clock when set (>= 0 and finite); otherwise the local wall clock. Guarding against a
    // non-finite Now matters because Time feeds Mathf.Sin in the blink/pulse effects — sin(Infinity) is NaN,
    // which would then taint every blinking color/alpha this frame.
    private float Time => Now >= 0.0 && double.IsFinite(Now) ? (float)Now : (float)_localClock;

    protected override void DrawPanel()
    {
        if (Player is null) return;

        // Sanitize the live resource reads: a NaN/Infinity would slip past the `health <= 0` self-blank guard
        // below (NaN compares false) and then propagate through every bar rect / color / alpha for the rest of
        // the frame — DrawPanel runs every frame, so a single bad stat would spam corrupt draw calls. Clamp to a
        // finite, non-negative range up front so the rest of the method only ever sees sane numbers.
        float health = Sanitize(Player.GetResource(ResourceType.Health));
        float armor = Sanitize(Player.GetResource(ResourceType.Armor));
        float fuel = Sanitize(Player.GetResource(ResourceType.Fuel));

        // QC: when dead, zero out and (optionally) hide. We always self-blank: with no health the panel has
        // nothing meaningful to show, and drawing an empty bar would be noise.
        if (health <= 0f)
        {
            // Reset the smoothing/flash state so respawn doesn't replay a stale ghost bar (QC prev_health=-1).
            _seeded = false;
            return;
        }

        float maxHealth = CvarF("maxhealth", 200f);
        float maxArmor = CvarF("maxarmor", 200f);
        if (maxHealth <= 0f) maxHealth = 200f;
        if (maxArmor <= 0f) maxArmor = 200f;

        bool progressbar = CvarBool("progressbar");
        bool combined = CvarBool("combined");
        bool text = CvarBool("text");
        int baralign = (int)CvarF("baralign", 3f);
        int iconalign = (int)CvarF("iconalign", 3f);

        // ---- compute the smoothed/ghost-bar display values (QC value-cache block) ----
        float pHealth = health, pArmor = armor;
        UpdateEffects(health, armor, combined, progressbar, ref pHealth, ref pArmor);

        // ---- alphas for fuel + oxygen (QC blocks) ----
        float airTime = ComputeAirTime(out float airAlpha);
        float fuelAlpha = ComputeFuelAlpha(fuel);

        DrawBackground();

        // QC: pos += '1 1 0' * panel_bg_padding; mySize -= '2 2 0' * panel_bg_padding.
        float pad = Cfg.Padding;
        var pos = new Vector2(pad, pad);
        var mySize = new Vector2(Size2.X - pad * 2f, Size2.Y - pad * 2f);
        if (mySize.X <= 0f || mySize.Y <= 0f) return;

        Color healthCol = GlobalColor("hud_progressbar_health_color", DefaultHealthColor);
        Color armorCol = GlobalColor("hud_progressbar_armor_color", DefaultArmorColor);
        Color fuelCol = GlobalColor("hud_progressbar_fuel_color", DefaultFuelColor);
        Color oxygenCol = GlobalColor("hud_progressbar_oxygen_color", DefaultOxygenColor);
        float pbAlpha = GlobalF("hud_progressbar_alpha", 0.6f) * LiveFgAlpha;

        if (combined)
            DrawCombined(pos, mySize, health, armor, fuel, airTime, maxHealth, maxArmor, baralign, iconalign,
                progressbar, text, healthCol, armorCol, fuelCol, oxygenCol, pbAlpha, fuelAlpha, airAlpha);
        else
            DrawSplit(pos, mySize, health, armor, pHealth, pArmor, fuel, airTime, maxHealth, maxArmor,
                baralign, iconalign, progressbar, text, healthCol, armorCol, fuelCol, oxygenCol, pbAlpha,
                fuelAlpha, airAlpha);
    }

    // =================================================================================================
    //  Effects state machine (QC value-cache block: smooth lerp + damage ghost-bar)
    // =================================================================================================

    private void UpdateEffects(float health, float armor, bool combined, bool progressbar,
        ref float pHealth, ref float pArmor)
    {
        float now = Time;

        if (!_seeded)
        {
            // QC prev_p_health == -1 branch: no effect, just snapshot.
            _healthBeforeDamage = 0f; _armorBeforeDamage = 0f;
            _healthDamageTime = 0f; _armorDamageTime = 0f;
            _prevHealth = health; _prevArmor = armor;
            _oldPHealth = health; _oldPArmor = armor;
            _pPrevHealth = health; _pPrevArmor = armor;
            _seeded = true;
        }

        pHealth = health; pArmor = armor;

        // Effects only apply in split + progressbar + gfx mode (QC guard).
        if (combined || !progressbar || !CvarBool("progressbar_gfx"))
        {
            _prevHealth = health; _prevArmor = armor;
            return;
        }

        float smooth = CvarF("progressbar_gfx_smooth", 0f);
        if (smooth > 0f)
        {
            pHealth = SmoothValue(health, smooth, now, ref _prevHealth, ref _pPrevHealth,
                ref _oldPHealth, ref _oldPHealthTime);
            pArmor = SmoothValue(armor, smooth, now, ref _prevArmor, ref _pPrevArmor,
                ref _oldPArmor, ref _oldPArmorTime);
        }

        float dmg = CvarF("progressbar_gfx_damage", 0f);
        if (dmg > 0f)
        {
            ApplyDamageGhost(health, dmg, now, _prevHealth, ref _healthBeforeDamage, ref _healthDamageTime);
            ApplyDamageGhost(armor, dmg, now, _prevArmor, ref _armorBeforeDamage, ref _armorDamageTime);
        }

        _prevHealth = health; _prevArmor = armor;
    }

    /// <summary>QC HEALTHARMOR_GFX_SMOOTH: ease the displayed value toward the real one after a big jump.</summary>
    private static float SmoothValue(float stat, float smooth, float now, ref float prev, ref float pPrev,
        ref float oldP, ref float oldPTime)
    {
        float p = stat;
        if (Mathf.Abs(prev - stat) >= smooth)
        {
            oldP = (now - oldPTime < 1f) ? pPrev : prev;
            oldPTime = now;
        }
        if (now - oldPTime < 1f)
        {
            p += (oldP - stat) * (1f - (now - oldPTime));
            pPrev = p;
        }
        return p;
    }

    /// <summary>QC HEALTHARMOR_GFX_DAMAGE: remember the pre-hit value for the fading ghost bar.</summary>
    private static void ApplyDamageGhost(float stat, float dmg, float now, float prev,
        ref float beforeDamage, ref float damageTime)
    {
        if (prev - stat >= dmg)
        {
            if (now - damageTime >= 1f)
                beforeDamage = prev;
            damageTime = now;
        }
    }

    // =================================================================================================
    //  Oxygen + fuel alpha (QC air_alpha / fuel_alpha blocks)
    // =================================================================================================

    /// <summary>Clamp a live stat read to a finite, non-negative value so NaN/Infinity can never enter the draw
    /// path (NaN slips past `&lt;= 0` guards and then corrupts every dependent rect/alpha for the whole frame).</summary>
    private static float Sanitize(float v) => float.IsFinite(v) && v > 0f ? v : 0f;

    private float ComputeAirTime(out float airAlpha)
    {
        float now = Time;
        // Sanitize the externally-fed air stat: a NaN would slip past `== 0f` (NaN compares false) and the
        // `now > airFinished` test, landing in the fade branch with a NaN alpha that propagates to the bar.
        float airFinished = float.IsFinite(AirFinished) ? AirFinished : 0f;
        if (airFinished <= 0f)
        {
            airAlpha = 0f;
            return 0f;
        }
        if (now > airFinished)
        {
            // Out of air: blink and peg the bar full (QC blink_synced(0.5,0.5,7,...)).
            airAlpha = BlinkSynced(0.5f, 0.5f, 7f, airFinished, -1, now);
            return 10f;
        }
        // Fading in over the back half of the 10s window.
        float airTime = Mathf.Clamp(airFinished - now, 0f, 10f);
        const float fadeTime = 10f / 2f;
        float startAlpha = CvarF("oxygenbar_startalpha", 0.2f);
        float f = (airFinished - now - fadeTime) / fadeTime;
        airAlpha = Mathf.Clamp(startAlpha + (1f - startAlpha) * (1f - f), 0f, 1f);
        return airTime;
    }

    private float ComputeFuelAlpha(float fuel)
    {
        if (fuel <= 0f) return 0f;
        float startAlpha = CvarF("fuelbar_startalpha", 0.3f);
        float f = (100f - fuel) / 50f;
        return Mathf.Clamp(startAlpha + (1f - startAlpha) * f, 0f, 1f);
    }

    // =================================================================================================
    //  Combined mode (QC autocvar_hud_panel_healtharmor_combined branch)
    // =================================================================================================

    private void DrawCombined(Vector2 pos, Vector2 mySize, float health, float armor, float fuel, float airTime,
        float maxHealth, float maxArmor, int baralign, int iconalign, bool progressbar, bool text,
        Color healthCol, Color armorCol, Color fuelCol, Color oxygenCol, float pbAlpha, float fuelAlpha,
        float airAlpha)
    {
        // QC healtharmor_maxdamage → "ideal max damage" hp (v.x) + the v.z flag. QC: v.z==1 (vz) ⇒ HEALTH is
        // the primary readout (health-colored bar + "health" number/icon, armor as the small sub-icon);
        // v.z==0 ⇒ ARMOR is primary. (The stock panel comment "// NOT fully armored" is misleading; the code
        // keys directly on v.z — see util.qc.)
        MaxDamage(health, armor, ArmorBlockPercent, out float hp, out bool vz);
        hp = Mathf.Floor(hp + 1f);
        float maxTotal = maxHealth + maxArmor;
        bool baralignFlag = baralign == 1 || baralign == 2;

        if (vz)
        {
            if (progressbar)
                DrawProgressBar(pos, mySize, hp / maxTotal, false, baralignFlag, healthCol, pbAlpha, HealthBarArt());
            if (text && armor > 0f)
            {
                // small armor sub-icon at the right edge (QC drawpic_aspect_skin at fg*armor/health).
                float ich = mySize.Y * 0.5f;
                var icoRect = new Rect2(pos.X + mySize.X - ich, pos.Y + (mySize.Y - ich) * 0.5f, ich, ich);
                DrawIcon("armor", icoRect, LiveFgAlpha * Mathf.Clamp(armor / Mathf.Max(health, 1f), 0f, 1f));
            }
        }
        else
        {
            if (progressbar)
                DrawProgressBar(pos, mySize, hp / maxTotal, false, baralignFlag, armorCol, pbAlpha, ArmorBarArt());
            if (text && health > 0f)
            {
                float ich = mySize.Y * 0.5f;
                var icoRect = new Rect2(pos.X + mySize.X - ich, pos.Y + (mySize.Y - ich) * 0.5f, ich, ich);
                DrawIcon("health", icoRect, LiveFgAlpha);
            }
        }

        // QC passes the raw iconalign int to DrawNumIcon; its check is `if (icon_right_align)` (truthy), so any
        // non-zero alignment right-aligns the icon in combined mode. biggercount = "health" when vz else "armor".
        if (text)
            DrawNumIcon(pos, mySize, hp, vz ? "health" : "armor", false, iconalign != 0,
                NumColorBlink(hp, maxTotal), maxTotal);

        // fuel sliver on top edge, oxygen sliver on the bottom edge (QC 0.2 sub-bars). NOTE: QC scales the
        // fuel/oxygen bar alpha by panel_fg_alpha * 0.8 only — NOT by hud_progressbar_alpha (unlike the
        // health/armor bars), so use LiveFgAlpha here, not pbAlpha.
        if (fuel > 0f)
            DrawProgressBar(pos, new Vector2(mySize.X, 0.2f * mySize.Y), fuel / 100f, false,
                baralign == 1 || baralign == 3, fuelCol, fuelAlpha * LiveFgAlpha * 0.8f);
        if (airTime > 0f)
            DrawProgressBar(pos + new Vector2(0f, 0.8f * mySize.Y), new Vector2(mySize.X, 0.2f * mySize.Y),
                airTime / 10f, false, baralign == 1 || baralign == 3, oxygenCol, airAlpha * LiveFgAlpha * 0.8f);
    }

    // =================================================================================================
    //  Split mode (QC else branch: two stacked or side-by-side cells)
    // =================================================================================================

    private void DrawSplit(Vector2 pos, Vector2 mySize, float health, float armor, float pHealth, float pArmor,
        float fuel, float airTime, float maxHealth, float maxArmor, int baralign, int iconalign,
        bool progressbar, bool text, Color healthCol, Color armorCol, Color fuelCol, Color oxygenCol,
        float pbAlpha, float fuelAlpha, float airAlpha)
    {
        float panelAr = mySize.X / Mathf.Max(mySize.Y, 1f);
        bool isVertical = panelAr < 1f;
        bool flip = CvarBool("flip");

        // Halve the cell along the long axis; the OTHER stat gets the offset (QC health_offset/armor_offset).
        var healthOffset = Vector2.Zero;
        var armorOffset = Vector2.Zero;
        if (panelAr >= 4f || (panelAr >= 1f / 4f && panelAr < 1f))
        {
            mySize.X *= 0.5f;
            if (flip) healthOffset.X = mySize.X; else armorOffset.X = mySize.X;
        }
        else
        {
            mySize.Y *= 0.5f;
            if (flip) healthOffset.Y = mySize.Y; else armorOffset.Y = mySize.Y;
        }

        // baralign / iconalign per QC (flip swaps the 2↔3 roles).
        bool healthBaralign, armorBaralign, fuelBaralign, airAlign, healthIconalign, armorIconalign;
        if (flip)
        {
            armorBaralign = baralign == 2 || baralign == 1;
            healthBaralign = baralign == 3 || baralign == 1;
            airAlign = fuelBaralign = healthBaralign;
            armorIconalign = iconalign == 2 || iconalign == 1;
            healthIconalign = iconalign == 3 || iconalign == 1;
        }
        else
        {
            healthBaralign = baralign == 2 || baralign == 1;
            armorBaralign = baralign == 3 || baralign == 1;
            airAlign = fuelBaralign = armorBaralign;
            healthIconalign = iconalign == 2 || iconalign == 1;
            armorIconalign = iconalign == 3 || iconalign == 1;
        }

        float now = Time;
        string healthArt = HealthBarArt();
        string armorArt = ArmorBarArt();

        // ---- health cell ----
        if (progressbar)
        {
            if (CvarBool("progressbar_gfx"))
            {
                if (CvarF("progressbar_gfx_damage", 0f) > 0f && now - _healthDamageTime < 1f)
                {
                    float a = 1f - (now - _healthDamageTime) * (now - _healthDamageTime);
                    DrawProgressBar(pos + healthOffset, mySize, _healthBeforeDamage / maxHealth, isVertical,
                        healthBaralign, healthCol, pbAlpha * a, healthArt);
                }
            }
            float painAlpha = 1f;
            if (CvarBool("progressbar_gfx") && health <= CvarF("progressbar_gfx_lowhealth", 40f))
                painAlpha = Blink(0.85f, 0.15f, 9f);
            DrawProgressBar(pos + healthOffset, mySize, pHealth / maxHealth, isVertical, healthBaralign,
                healthCol, pbAlpha * painAlpha, healthArt);
        }
        if (text)
            DrawNumIcon(pos + healthOffset, mySize, health, "health", isVertical, healthIconalign,
                NumColorBlink(health, maxHealth), maxHealth);

        // ---- armor cell ----
        if (progressbar)
        {
            if (CvarBool("progressbar_gfx") && CvarF("progressbar_gfx_damage", 0f) > 0f
                && now - _armorDamageTime < 1f)
            {
                float a = 1f - (now - _armorDamageTime) * (now - _armorDamageTime);
                DrawProgressBar(pos + armorOffset, mySize, _armorBeforeDamage / maxArmor, isVertical,
                    armorBaralign, armorCol, pbAlpha * a, armorArt);
            }
            if (pArmor != 0f)
                DrawProgressBar(pos + armorOffset, mySize, pArmor / maxArmor, isVertical, armorBaralign,
                    armorCol, pbAlpha, armorArt);
        }
        if ((!progressbar || pArmor != 0f) && text)
            DrawNumIcon(pos + armorOffset, mySize, armor, "armor", isVertical, armorIconalign,
                NumColorBlink(armor, maxArmor), maxArmor);

        // ---- fuel + oxygen sub-bars (QC tail block) ----
        if (fuel > 0f || airTime > 0f)
        {
            Vector2 cellSize = mySize;
            Vector2 subPos = pos;
            Vector2 subSize = mySize;
            if (isVertical) subSize.X *= 0.2f / 2f; else subSize.Y *= 0.2f;
            if (panelAr >= 4f) subSize.X *= 2f;
            else if (panelAr < 1f / 4f) subSize.Y *= 2f;

            // QC: fuel/oxygen bar alpha = panel_fg_alpha * 0.8 (no hud_progressbar_alpha) → use LiveFgAlpha.
            if (fuel > 0f)
                DrawProgressBar(subPos, subSize, fuel / 100f, isVertical, fuelBaralign, fuelCol,
                    fuelAlpha * LiveFgAlpha * 0.8f);
            if (airTime > 0f)
            {
                Vector2 airPos = subPos;
                if (panelAr > 1f && panelAr < 4f) airPos.Y += cellSize.Y;
                else if (panelAr > 1f / 4f && panelAr <= 1f) airPos.X += cellSize.X;
                if (isVertical) airPos.X += cellSize.X - subSize.X;
                else airPos.Y += cellSize.Y - subSize.Y;
                DrawProgressBar(airPos, subSize, airTime / 10f, isVertical, airAlign, oxygenCol,
                    airAlpha * LiveFgAlpha * 0.8f);
            }
        }
    }

    // =================================================================================================
    //  Draw primitives (QC HUD_Panel_DrawProgressBar / DrawNumIcon, on the base helpers)
    // =================================================================================================

    /// <summary>Port of QC <c>HUD_Panel_DrawProgressBar</c> — fills the bar from one edge per
    /// <paramref name="baralign"/>, supporting vertical fill. Uses the skin progress-bar art named
    /// <paramref name="art"/> (QC <c>hud_panel_healtharmor_progressbar_health</c>/<c>_armor</c>, default
    /// "progressbar"; the vertical variant appends "_vertical" exactly as QC does) tinted by
    /// <paramref name="color"/>, falling back to a plain rect when the art is missing.</summary>
    private void DrawProgressBar(Vector2 origin, Vector2 size, float lengthRatio, bool vertical, bool baralign,
        Color color, float alpha, string art = "progressbar")
    {
        if (string.IsNullOrWhiteSpace(art)) art = "progressbar";
        // Delegate to the shared faithful primitive (HudPanel.DrawProgressBar): the QC 3-slice cap render
        // (drawsubpic square/middle/cap → rounded ends) + the clamp/skin-resolve rules. The panel's bool
        // align maps to QC baralign 0 (left/top) vs 1 (right/bottom).
        DrawProgressBar(new Rect2(origin, size), art, lengthRatio, vertical, baralign ? 1 : 0,
            color, alpha);
    }

    /// <summary>The skin progress-bar art name for health / armor (QC string cvars
    /// <c>hud_panel_healtharmor_progressbar_health</c> / <c>_armor</c>, default "progressbar").</summary>
    private string HealthBarArt() { string s = CvarStr("progressbar_health"); return string.IsNullOrWhiteSpace(s) ? "progressbar" : s; }
    private string ArmorBarArt() { string s = CvarStr("progressbar_armor"); return string.IsNullOrWhiteSpace(s) ? "progressbar" : s; }

    /// <summary>Port of QC <c>DrawNumIcon</c> — an icon + the value number laid out in a 3:1 (h) or 1:2 (v)
    /// cell, with the icon on the left/top or right/bottom per <paramref name="iconRight"/>.</summary>
    private void DrawNumIcon(Vector2 myPos, Vector2 mySize, float value, string icon, bool vertical,
        bool iconRight, Color color, float max)
    {
        string txt = Mathf.RoundToInt(value).ToString();
        Vector2 newPos, newSize;

        if (vertical)
        {
            if (mySize.Y / Mathf.Max(mySize.X, 1f) > 2f)
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
            if (iconRight) { numPos = newPos; picPos = newPos + new Vector2(0f, newSize.X); }
            else { picPos = newPos; numPos = newPos + new Vector2(0f, newSize.X); }

            float half = newSize.Y * 0.5f;
            DrawIcon(icon, new Rect2(picPos, new Vector2(newSize.X, half)), LiveFgAlpha);
            // number cell, slightly smaller in y (QC reduces it to 0.7 and recenters by 0.15*half). The Y base
            // already carries the icon-width offset (numPos = newPos + eY*newSize.x), so add only the recenter.
            float numH = half * 0.7f;
            numPos.Y += (half - numH) * 0.5f;
            DrawTextCentered(numPos, newSize.X, txt, color, NumberSize(numH));
        }
        else
        {
            if (mySize.X / Mathf.Max(mySize.Y, 1f) > 3f)
            {
                newSize = new Vector2(3f * mySize.Y, mySize.Y);
                newPos = new Vector2(myPos.X + (mySize.X - newSize.X) * 0.5f, myPos.Y);
            }
            else
            {
                newSize = new Vector2(mySize.X, mySize.X / 3f);
                newPos = new Vector2(myPos.X, myPos.Y + (mySize.Y - newSize.Y) * 0.5f);
            }

            Vector2 picPos, numPos;
            if (iconRight) { numPos = newPos; picPos = newPos + new Vector2(2f * newSize.Y, 0f); }
            else { picPos = newPos; numPos = newPos + new Vector2(newSize.Y, 0f); }

            // icon = a square of side newSize.y; number cell = 2×newSize.y wide.
            DrawIcon(icon, new Rect2(picPos, new Vector2(newSize.Y, newSize.Y)), LiveFgAlpha);
            float numW = 2f * newSize.Y;
            int size = NumberSize(newSize.Y);
            float topY = numPos.Y + (newSize.Y - size) * 0.5f;
            DrawTextCentered(new Vector2(numPos.X, topY), numW, txt, color, size);
        }
    }

    /// <summary>Draw the health/armor skin icon — ALWAYS the plain <c>health</c>/<c>armor</c> pic, exactly like
    /// Base (<c>healtharmor.qc</c> passes the bare name to <c>DrawNumIcon</c>/<c>drawpic_aspect_skin</c>; the
    /// <c>_small/_medium/_big/_mega</c> files in the skin are the ITEM pickup icons, not panel art). The old
    /// port "enhancement" swapped to <c>_big</c>/<c>_mega</c> past 50%/100% of max — which meant the panel showed
    /// the wrong (item-variant) icon at any normal over-100 value (playtest #52). Falls back to a tinted box
    /// matching the bar color when the art is missing so the cell is never blank.</summary>
    private void DrawIcon(string baseName, Rect2 rect, float alpha)
    {
        var modulate = new Color(1f, 1f, 1f, Mathf.Clamp(alpha, 0f, 1f));
        if (DrawSkinPic(baseName, rect, modulate)) return;

        // Fallback glyph: a small filled square tinted like the resource.
        Color box = baseName == "armor" ? DefaultArmorColor : DefaultHealthColor;
        DrawRect(rect, new Color(box.R, box.G, box.B, modulate.A * 0.85f));
    }

    private int NumberSize(float cellH) => (int)Mathf.Clamp(cellH * 0.95f, 9f, 40f);

    // =================================================================================================
    //  Color helpers (QC HUD_Get_Num_Color — full 5-stop ramp + blink, replacing the base 2-stop NumColor)
    // =================================================================================================

    /// <summary>Faithful port of QC <c>HUD_Get_Num_Color(hp, max, blink=true)</c>: a 5-color ramp by percent
    /// with a low-health red pulse and an over-100% flash.</summary>
    private Color NumColorBlink(float hp, float max)
    {
        if (max <= 0f) return FgColor;
        var color100 = new Vector3(0f, 1f, 0f);       // green
        var color75 = new Vector3(0.4f, 0.9f, 0f);    // lightgreen
        var color50 = new Vector3(1f, 1f, 1f);        // white
        var color25 = new Vector3(1f, 1f, 0.2f);      // lightyellow
        var color10 = new Vector3(1f, 0f, 0f);        // red

        float pct = hp / max * 100f;
        Vector3 c;
        if (pct > 100f) c = color100;
        else if (pct > 75f) c = Between(color75, color100, pct, 75f, 100f);
        else if (pct > 50f) c = Between(color50, color75, pct, 50f, 75f);
        else if (pct > 25f) c = Between(color25, color50, pct, 25f, 50f);
        else if (pct > 10f) c = Between(color10, color25, pct, 10f, 25f);
        else c = color10;

        if (pct >= 100f)
        {
            float f = Mathf.Sin(2f * Mathf.Pi * Time);
            if (c.X == 0f) c.X = f;
            if (c.Y == 0f) c.Y = f;
            if (c.Z == 0f) c.Z = f;
        }
        else if (pct < 25f)
        {
            float f = (1f - pct / 25f) * Mathf.Sin(2f * Mathf.Pi * Time);
            c *= 1f - f;
        }

        return new Color(Mathf.Clamp(c.X, 0f, 1f), Mathf.Clamp(c.Y, 0f, 1f), Mathf.Clamp(c.Z, 0f, 1f),
            LiveFgAlpha);
    }

    private static Vector3 Between(Vector3 lo, Vector3 hi, float pct, float min, float max)
        => lo + (hi - lo) * ((pct - min) / (max - min));

    // ---- small QC math helpers (util.qc) ----

    /// <summary>QC <c>blink(base,range,freq) = base + range*sin(time*freq)</c>.</summary>
    private float Blink(float baseV, float range, float freq) => baseV + range * Mathf.Sin(Time * freq);

    /// <summary>QC <c>blink_synced</c>.</summary>
    private static float BlinkSynced(float baseV, float range, float freq, float startTime, int startPos, float now)
        => baseV + range * Mathf.Sin((now - startTime - (Mathf.Pi / 2f) * startPos) * freq);

    /// <summary>QC <c>healtharmor_maxdamage</c> (common/util.qc): the "ideal max damage" hp (<c>v.x</c>) plus
    /// the <c>v.z</c> flag. QC: when <c>armordamage &lt; healthdamage</c> → <c>v.x = armordamage; v.z = 1</c>;
    /// else <c>v.x = healthdamage; v.z = 0</c>. The healtharmor panel keys its primary-color/icon choice on
    /// <paramref name="vz"/> (<c>v.z == 1</c> → HEALTH is the primary readout, armor the sub-icon).</summary>
    private static void MaxDamage(float h, float a, float armorBlock, out float maxDamage, out bool vz)
    {
        armorBlock = Mathf.Clamp(armorBlock, 0f, 0.99f);
        float healthDamage = (h - 1f) / (1f - armorBlock); // damage absorbable via more health
        float armorDamage = a + (h - 1f);                  // damage absorbable via more armor
        if (armorDamage < healthDamage)
        {
            maxDamage = armorDamage;
            vz = true;
        }
        else
        {
            maxDamage = healthDamage;
            vz = false;
        }
        if (maxDamage < 0f) maxDamage = 0f;
    }

    private static Color GlobalColor(string name, Color fallback)
        => TryParseRgb(GlobalStr(name), out Color c) ? c : fallback;
}
