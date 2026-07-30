using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Game.Hud;           // TextureCache (VFS art resolver) for gfx/blood
using SC = VortexArena.Engine.Collision.SuperContents;

namespace VortexArena.Game.Client;

/// <summary>
/// The full-screen view post-effects overlay — the C# successor to the 2D <c>drawfill</c> screen tints in
/// QuakeC's <c>view.qc</c> (<c>HUD_Damage</c> and <c>HUD_Contents</c>,
/// Base/data/xonotic-data.pk3dir/qcsrc/client/view.qc). CSQC drew these as a single full-screen quad per
/// frame whose alpha it animated; here a <see cref="CanvasLayer"/> hosts one full-screen
/// <see cref="ColorRect"/> per effect and we animate <see cref="ColorRect.Color"/>'s alpha each frame.
///
/// Two effects are ported:
/// <list type="bullet">
///   <item><b>Damage red-flash</b> (<c>HUD_Damage</c>): an accumulator <c>myhealth_flash</c> that rises with
///         each chunk of damage taken (<c>dmg_take</c>) and decays over time; the visible alpha is the
///         accumulator minus a pain threshold. When dead (<c>myhealth &lt; 1</c>) it ramps up to a steady
///         death-fade. Colour is <c>hud_damage_color</c> (default red).</item>
///   <item><b>Liquid tint</b> (<c>HUD_Contents</c>): a low-passed <c>contentavgalpha</c> that fades in/out as
///         the eye enters/leaves a liquid, tinting the screen with the per-content colour
///         (water = blue, slime = green, lava = orange).</item>
/// </list>
///
/// Wiring (mirrors how the HUD reads live local-player state): the host adds one <see cref="ViewEffects"/>
/// to the scene next to the HUD, calls <see cref="ReportDamage"/> on the pain edge (a drop in the local
/// player's health), and each frame calls <see cref="UpdateEffects"/> with the player's current health and
/// the SUPERCONTENTS at the eye. The overlay then decays/animates itself.
///
/// What is NOT ported: the GLSL blur/sharpen post-process (<c>damage_blurpostprocess</c> /
/// <c>content_blurpostprocess</c> → <c>r_glsl_postprocess_uservec*</c>) and night-vision — those drive
/// engine shader cvars that have no Godot analogue here. The essential coloured screen reaction is faithful.
/// </summary>
public partial class ViewEffects : CanvasLayer
{
    // ColorRects stretched across the whole viewport; their alpha is what we animate (the RGB is the tint).
    private ColorRect _damage = null!;        // gentle-mode solid flash (cl_gentle / cl_gentle_damage) + texture fallback
    private TextureRect _bloodSplat = null!;  // QC drawpic("gfx/blood", ...) — the centred blood splatter
    private ColorRect _contents = null!;
    private ColorRect _frozen = null!;
    private ColorRect _darkBlind = null!; // Darkness nade blind: blinking near-black full-screen overlay (HUD_DarkBlinking)
    private ColorRect _orbFlash = null!;  // In-orb 2D additive colour flash (orb_draw2d)

    // QC view.qc:1304 cl_gentle_damage==2: a randomized colour latched while the flash is gone (myhealth_gentlergb).
    private Color _myHealthGentleRgb = new(1f, 0.7f, 1f);
    private readonly System.Random _gentleRng = new();

    // ---- HUD_Damage state (view.qc: myhealth / myhealth_flash / myhealth_prev) ----
    private float _myHealthFlash;   // the decaying damage accumulator
    private float _myHealthPrev;    // last frame's health (to detect respawn / death edges)
    private float _time;            // frametime-accumulated clock — QC's `time` in the pain-threshold sin-pulse

    // dmg_take accumulated between Update calls (QC reads the per-tick .dmg_take stat; we sum reported pain).
    private float _pendingDamage;

    // ---- HUD_Contents state (view.qc: contentavgalpha / liquidalpha_prev / liquidcolor_prev) ----
    private float _contentAvgAlpha;
    private float _liquidAlphaPrev;
    private Color _liquidColorPrev = new(0f, 0f, 0f);

    // -------------------------------------------------------------------------------------------------
    //  autocvar defaults (faithful to the cvar declarations these effects read in view.qc / _hud.cfg).
    //  Read live from the cvar service when set (the match loads the real Xonotic cfg tree); these are the
    //  fallbacks for standalone use, and for cvars GetFloat() returns 0 for when "0 means default" applies.
    // -------------------------------------------------------------------------------------------------
    // Defaults faithful to _hud_common.cfg:297-306 (the cvar declarations HUD_Damage reads).
    private const float DefDamageFactor       = 0.025f; // hud_damage_factor
    private const float DefDamageMaxAlpha      = 1.5f;   // hud_damage_maxalpha
    private const float DefDamageFadeRate      = 0.75f;  // hud_damage_fade_rate (per second)
    private const float DefDamagePainThreshold = 0.1f;   // hud_damage_pain_threshold
    private const float DefDamageGlobalAlpha   = 0.55f;  // hud_damage (master multiplier)
    // Near-death pain-threshold pulsing (_hud_common.cfg:307-310).
    private const float DefDamagePainThresholdLower       = 1.25f; // hud_damage_pain_threshold_lower
    private const float DefDamagePainThresholdLowerHealth = 50f;   // hud_damage_pain_threshold_lower_health
    private const float DefDamagePainPulsatingMin         = 0.6f;  // hud_damage_pain_threshold_pulsating_min
    private const float DefDamagePainPulsatingPeriod      = 0.8f;  // hud_damage_pain_threshold_pulsating_period
    // Gentle-mode (cl_gentle / cl_gentle_damage) — _hud.cfg:300-301, _all.cfg:878/881.
    private const float DefDamageGentleAlphaMultiplier    = 0.10f; // hud_damage_gentle_alpha_multiplier
    private static readonly Color DefDamageGentleColor    = new(1f, 0.7f, 1f); // hud_damage_gentle_color "1 0.7 1"

    // Nades — Darkness blind + in-orb colour flash (mutators/mutator/nades).
    private const float DefColorFlashAlpha     = 0.5f;   // hud_colorflash_alpha (orb_draw2d screen-flash strength)

    private const float DefContentsWaterAlpha  = 0.5f;   // hud_contents_water_alpha
    private const float DefContentsLavaAlpha   = 0.7f;   // hud_contents_lava_alpha
    private const float DefContentsSlimeAlpha  = 0.7f;   // hud_contents_slime_alpha
    private const float DefContentsFadeIn      = 0.02f;  // hud_contents_fadeintime  (fast in)
    private const float DefContentsFadeOut     = 0.25f;  // hud_contents_fadeouttime (slow out)

    private static readonly Color WaterColor = new(0.4f, 0.6f, 1.0f);  // hud_contents_water_color  "0.4 0.6 1.0"
    private static readonly Color LavaColor  = new(1.0f, 0.3f, 0.0f);  // hud_contents_lava_color   "1.0 0.3 0.0"
    private static readonly Color SlimeColor = new(0.0f, 0.8f, 0.0f);  // hud_contents_slime_color  "0.0 0.8 0.0"
    private static readonly Color DefDamageColor = new(1.0f, 0.0f, 0.0f); // hud_damage_color "1 0 0" (_hud_common.cfg:302)

    public override void _Ready()
    {
        // Sit above the 3D view but below the HUD if the HUD's CanvasLayer has a higher layer (HUD defaults to 0;
        // we use a negative layer so health/ammo/crosshair composite ON TOP of the tint, matching CSQC draw order
        // where HUD_Damage/HUD_Contents run before the HUD panels but the crosshair draws last).
        Layer = -1;

        _contents = MakeFullScreenRect("ContentsTint");
        _bloodSplat = MakeBloodSplat();
        _damage = MakeFullScreenRect("DamageFlash");
        _frozen = MakeFullScreenRect("FrozenOverlay");
        _darkBlind = MakeFullScreenRect("DarknessBlind");
        _orbFlash = MakeFullScreenRect("OrbColorFlash");
        // orb_draw2d paints the screen ADDITIVELY (drawfill DRAWFLAG_ADDITIVE) so the orb's colour brightens the
        // view rather than darkening it; a CanvasItemMaterial in Add blend mode reproduces that compositing.
        _orbFlash.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        AddChild(_contents);
        AddChild(_bloodSplat); // the gfx/blood splatter — the default (non-gentle) damage flash
        AddChild(_damage);     // gentle-mode solid flash, on top of the liquid tint
        AddChild(_frozen);     // the Freeze-Tag icy overlay composites on top of both
        AddChild(_darkBlind);  // Darkness-nade blind: blinking near-black, on top of the icy overlay
        AddChild(_orbFlash);   // in-orb additive colour flash, the topmost view-effect layer

        // hud_colorflash_alpha — the orb_draw2d screen-flash strength (Base default 0.5).
        if (Api.Services is not null)
            Api.Cvars.Register("hud_colorflash_alpha", "0.5");

        _myHealthPrev = 0f;
    }

    /// <summary>The centred <c>gfx/blood</c> splatter (QC HUD_Damage <c>drawpic(splash_pos, "gfx/blood", splash_size,
    /// …)</c>): a SQUARE sized to the larger screen axis (<c>splash_size = max(conwidth, conheight)</c>) and centred,
    /// so it always covers the viewport and overflows the shorter axis (it is NOT aspect-stretched in Base).</summary>
    private static TextureRect MakeBloodSplat() => new()
    {
        Name = "DamageBlood",
        Texture = TextureCache.Get("gfx/blood"),
        // Centre anchor; the square size + centred position are set each frame in UpdateDamage from the viewport.
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.Scale, // fill the square (the square itself stays 1:1)
        Modulate = new Color(1f, 1f, 1f, 0f),
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static ColorRect MakeFullScreenRect(string name) => new()
    {
        Name = name,
        // Full-rect anchors so it tracks any window resize (QC redrew against vid_conwidth each frame).
        AnchorRight = 1f,
        AnchorBottom = 1f,
        OffsetLeft = 0f,
        OffsetTop = 0f,
        OffsetRight = 0f,
        OffsetBottom = 0f,
        Color = new Color(0f, 0f, 0f, 0f),
        // Never eat input — the overlay is purely cosmetic (QC hud_cursormode off; HUD never grabs).
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>
    /// Report a chunk of damage just taken by the local player (QC <c>.dmg_take</c> on a stat update). The
    /// host calls this on the pain edge — typically a drop in the local player's <see cref="Entity.Health"/>.
    /// Accumulates until the next <see cref="UpdateEffects"/> folds it into the flash. Negative/zero is ignored.
    /// </summary>
    public void ReportDamage(float amount)
    {
        if (amount > 0f)
            _pendingDamage += amount;
    }

    /// <summary>
    /// Advance both screen effects one frame and repaint the overlays. Port of the per-frame bodies of
    /// <c>HUD_Damage</c> and <c>HUD_Contents</c> in view.qc.
    /// </summary>
    /// <param name="dt">Frame time in seconds (QC <c>frametime</c> / <c>drawframetime</c>).</param>
    /// <param name="health">The local player's current health (QC <c>STAT(HEALTH)</c>).</param>
    /// <param name="eyeContents">SUPERCONTENTS bitmask at the eye (QC <c>pointcontents(view_origin)</c>).</param>
    /// <param name="observing">
    /// True when the local player is an observer / not yet spawned / the match has ended — the C# analogue of QC's
    /// <c>spectatee_status == -1 || intermission</c> (view.qc:1281). The damage flash is force-cleared in this state
    /// so the pre-spawn / connecting window (health 0, which would otherwise read as "dead" and ramp the death fade)
    /// doesn't bleed a growing red tint onto the screen. A player who is genuinely dead DURING a match is NOT
    /// observing, so the death fade still shows for them.
    /// </param>
    public void UpdateEffects(float dt, float health, int eyeContents, bool observing)
    {
        if (dt <= 0f)
            dt = 0.0166667f; // assume 60fps before we know the real frametime (QC drawframetime default)

        UpdateDamage(dt, health, observing);
        UpdateContents(dt, eyeContents);
    }

    // -------------------------------------------------------------------------------------------------
    //  HUD_Damage (view.qc ~1237): myhealth_flash accumulate + decay; alpha = flash - pain_threshold.
    // -------------------------------------------------------------------------------------------------
    private void UpdateDamage(float dt, float health, bool observing)
    {
        // Tint colour through the cvar (QC stov(autocvar_hud_damage_color), _hud_common.cfg:302), like the
        // liquid-tint sibling colours, instead of a hardcoded red.
        Color damageColor = CvarColor("hud_damage_color", DefDamageColor);

        float global = Cvar("hud_damage", DefDamageGlobalAlpha);
        if (global <= 0f)
        {
            // hud_damage 0 disables the effect entirely (QC early-returns); clear and bail.
            _pendingDamage = 0f;
            _myHealthFlash = 0f;
            SetAlpha(_damage, damageColor, 0f);
            SetSplatAlpha(0f, damageColor);
            _myHealthPrev = health;
            return;
        }

        float fadeRate = Cvar("hud_damage_fade_rate", DefDamageFadeRate);
        float factor   = Cvar("hud_damage_factor", DefDamageFactor);
        float maxAlpha = Cvar("hud_damage_maxalpha", DefDamageMaxAlpha);
        if (maxAlpha <= 0f) maxAlpha = DefDamageMaxAlpha;
        float painThreshold = Cvar("hud_damage_pain_threshold", DefDamagePainThreshold);

        float myhealth = health;

        // fade out, then add the new damage (QC: myhealth_flash drifts down each frame, jumps up on dmg_take).
        _myHealthFlash = Mathf.Max(0f, _myHealthFlash - fadeRate * dt);
        _myHealthFlash = Mathf.Clamp(_myHealthFlash + _pendingDamage * factor, 0f, maxAlpha);
        _pendingDamage = 0f;

        // Near-death pulsating pain-threshold (QC view.qc:1256-1264): as health drops below
        // hud_damage_pain_threshold_lower_health (50), subtract a sin-pulse from pain_threshold scaled by how
        // close to 0 health we are. A NEGATIVE pain_threshold then makes the flash persist (a low-health throb).
        // _time advances by frametime — the faithful analogue of QC's `time` in sin(PI*time/period).
        _time += dt;
        float painThresholdLower = Cvar("hud_damage_pain_threshold_lower", DefDamagePainThresholdLower);
        float painThresholdLowerHealth = Cvar("hud_damage_pain_threshold_lower_health", DefDamagePainThresholdLowerHealth);
        if (painThresholdLower != 0f && myhealth < painThresholdLowerHealth)
        {
            float pulsatingMin = Cvar("hud_damage_pain_threshold_pulsating_min", DefDamagePainPulsatingMin);
            float pulsatingPeriod = Cvar("hud_damage_pain_threshold_pulsating_period", DefDamagePainPulsatingPeriod);
            painThreshold -= Mathf.Max(pulsatingMin, Mathf.Abs(Mathf.Sin(Mathf.Pi * _time / pulsatingPeriod)))
                * painThresholdLower * (1f - Mathf.Max(0f, myhealth) / painThresholdLowerHealth);
        }

        float flashTemp = Mathf.Clamp(_myHealthFlash - painThreshold, 0f, 1f);

        // Respawn / death edges (QC: myhealth_prev < 1 branch).
        if (_myHealthPrev < 1f)
        {
            if (myhealth >= 1f)
            {
                // just (re)spawned — clear the flash so a fresh spawn doesn't bleed red.
                _myHealthFlash = 0f;
                flashTemp = 0f;
            }
            else
            {
                // still dead — ramp the flash up toward a steady death fade.
                _myHealthFlash += fadeRate * dt;
            }
        }

        // Observing / not yet spawned / match ended (QC view.qc:1281 spectatee_status == -1 || intermission):
        // force the flash off. Without this the pre-spawn window (health 0 reads as "dead" above and ramps the
        // death fade) bleeds a growing red tint — the "view taking damage right after Create" glitch.
        if (observing)
        {
            _myHealthFlash = 0f;
            flashTemp = 0f;
        }

        _myHealthPrev = myhealth;

        // QC HUD_Damage draw split (view.qc:1293-1305):
        //   cl_gentle / cl_gentle_damage -> a SOLID coloured flash (no blood), alpha multiplied by
        //     hud_damage_gentle_alpha_multiplier; cl_gentle_damage==2 randomizes the colour while the flash is gone.
        //   else                          -> the gfx/blood splatter, tinted hud_damage_color.
        bool gentle = Cvar("cl_gentle", 0f) != 0f || Cvar("cl_gentle_damage", 0f) != 0f;
        if (gentle)
        {
            if (Cvar("cl_gentle_damage", 0f) == 2f)
            {
                // only re-randomize once the previous flash has fully faded (QC: myhealth_flash < pain_threshold)
                if (_myHealthFlash < painThreshold)
                    _myHealthGentleRgb = new Color(
                        (float)_gentleRng.NextDouble(), (float)_gentleRng.NextDouble(), (float)_gentleRng.NextDouble());
            }
            else
            {
                _myHealthGentleRgb = CvarColor("hud_damage_gentle_color", DefDamageGentleColor);
            }

            float gentleMul = Cvar("hud_damage_gentle_alpha_multiplier", DefDamageGentleAlphaMultiplier);
            SetAlpha(_damage, _myHealthGentleRgb, gentleMul * flashTemp * global);
            SetSplatAlpha(0f, damageColor); // hide the blood splatter in gentle mode
        }
        else
        {
            // gentle solid rect off first; SetSplatAlpha re-tints _damage only if gfx/blood is missing (fallback).
            SetAlpha(_damage, damageColor, 0f);
            SetSplatAlpha(flashTemp * global, damageColor);
        }
    }

    /// <summary>Size + centre the <c>gfx/blood</c> splatter to the viewport (QC <c>splash_size = max(conwidth,
    /// conheight)</c>, centred) and set its alpha/tint. Falls back to a solid red rect when the texture is missing
    /// (so the damage reaction is never silently lost on a stripped data tree).</summary>
    private void SetSplatAlpha(float alpha, Color tint)
    {
        alpha = Mathf.Clamp(alpha, 0f, 1f);
        bool visible = alpha > 0.0001f;
        // Lazy texture load: the VFS art resolver may not be wired yet at _Ready (mirrors ReticleOverlay's
        // per-frame TextureCache.Get). Retry the resolve until it succeeds, then it is cached.
        if (_bloodSplat.Texture is null && visible)
            _bloodSplat.Texture = TextureCache.Get("gfx/blood");
        if (_bloodSplat.Texture is null)
        {
            // No gfx/blood available — degrade to the solid coloured rect (uses _damage's slot via the gentle path
            // is taken in UpdateDamage; here just tint _damage so the flash still reads). Keep the splat hidden.
            _bloodSplat.Visible = false;
            SetAlpha(_damage, tint, alpha);
            return;
        }
        _bloodSplat.Visible = visible;
        if (!visible)
            return;
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float side = Mathf.Max(vp.X, vp.Y);
        _bloodSplat.Size = new Vector2(side, side);
        _bloodSplat.Position = new Vector2((vp.X - side) * 0.5f, (vp.Y - side) * 0.5f);
        _bloodSplat.Modulate = new Color(tint.R, tint.G, tint.B, alpha);
    }

    // -------------------------------------------------------------------------------------------------
    //  HUD_Contents (view.qc ~1167): low-pass contentavgalpha toward in/out of liquid; tint by content.
    // -------------------------------------------------------------------------------------------------
    private void UpdateContents(float dt, int contents)
    {
        float liquidAlpha;
        Color liquidColor;
        int inContent;

        // pointcontents() → the dominant liquid (QC switch on CONTENT_WATER/LAVA/SLIME).
        if ((contents & SC.Lava) != 0)
        {
            liquidAlpha = Cvar("hud_contents_lava_alpha", DefContentsLavaAlpha);
            liquidColor = LavaColor;
            inContent = 1;
        }
        else if ((contents & SC.Slime) != 0)
        {
            liquidAlpha = Cvar("hud_contents_slime_alpha", DefContentsSlimeAlpha);
            liquidColor = SlimeColor;
            inContent = 1;
        }
        else if ((contents & SC.Water) != 0)
        {
            liquidAlpha = Cvar("hud_contents_water_alpha", DefContentsWaterAlpha);
            liquidColor = WaterColor;
            inContent = 1;
        }
        else
        {
            liquidAlpha = 0f;
            liquidColor = new Color(0f, 0f, 0f);
            inContent = 0;
        }

        // Different fade speeds in vs out (instant-ish in, slow out) so leaving water lingers (QC comment).
        float fadeTime;
        if (inContent != 0)
        {
            fadeTime = Cvar("hud_contents_fadeintime", DefContentsFadeIn);
            // latch the colour/alpha so it persists while fading OUT (not reset until a new content entered).
            _liquidAlphaPrev = liquidAlpha;
            _liquidColorPrev = liquidColor;
        }
        else
        {
            fadeTime = Cvar("hud_contents_fadeouttime", DefContentsFadeOut);
        }

        float a = Mathf.Clamp(dt / Mathf.Max(0.0001f, fadeTime), 0f, 1f);
        _contentAvgAlpha = _contentAvgAlpha * (1f - a) + inContent * a;

        SetAlpha(_contents, _liquidColorPrev, _contentAvgAlpha * _liquidAlphaPrev);
    }

    /// <summary>
    /// The Freeze-Tag full-screen icy overlay — port of <c>MUTATOR_HOOKFUNCTION(cl_ft, HUD_Draw_overlay)</c>
    /// (common/gametypes/gametype/freezetag/cl_freezetag.qc): while the local player is frozen, fill the screen
    /// with an icy-blue tint (<c>'0.25 0.90 1'</c>) whose colour warms (toward white) and whose alpha fades out as
    /// the thaw ring (<c>STAT(REVIVE_PROGRESS)</c>) fills, so the player can see the world again as they thaw.
    /// </summary>
    /// <param name="frozen">True iff the local player currently has the Freeze-Tag freeze (QC STAT(FROZEN)).</param>
    /// <param name="reviveProgress">The local player's 0..1 thaw progress (QC STAT(REVIVE_PROGRESS)).</param>
    public void UpdateFrozenOverlay(bool frozen, float reviveProgress)
    {
        if (!frozen)
        {
            SetAlpha(_frozen, new Color(0f, 0f, 0f), 0f);
            return;
        }

        float p = Mathf.Clamp(reviveProgress, 0f, 1f);
        float colFade = Mathf.Max(0f, p * 2f - 1f);            // QC col_fade = max(0, REVIVE_PROGRESS*2 - 1)
        float alphaFade = 0.3f + 0.7f * (1f - Mathf.Max(0f, p * 4f - 3f)); // QC alpha_fade
        // QC base col '0.25 0.90 1'; as col_fade rises the tint warms (R up, G/B down toward white-ish).
        Color col = new(0.25f + colFade, 0.90f - colFade, 1f - colFade);
        SetAlpha(_frozen, col, alphaFade);
    }

    /// <summary>
    /// The Darkness-nade blind overlay — port of <c>HUD_DarkBlinking</c>
    /// (mutators/mutator/nades/nades.qc). While the local player is blinded by a Darkness nade
    /// (<c>STAT(NADE_DARKNESS_TIME) - time &gt; 0</c>) the whole screen is filled near-black with a BLINKING
    /// alpha, so the view pulses dark while the blind lasts. Cleared when the blind has expired.
    /// </summary>
    /// <param name="darknessRemaining">Seconds of blind left (QC <c>NADE_DARKNESS_TIME - time</c>); &lt;= 0 = none.</param>
    public void UpdateDarknessOverlay(float darknessRemaining)
    {
        if (darknessRemaining <= 0f)
        {
            SetAlpha(_darkBlind, new Color(0f, 0f, 0f), 0f);
            return;
        }

        // QC HUD_DarkBlinking: drawfill('0 0 0', bound(0.2, sin(time*const)*0.25 + 0.75, 0.9)) — a near-black
        // fill whose alpha throbs between 0.2 and 0.9. _time is the same frametime-accumulated clock the
        // pain-threshold pulse uses, so the blink keeps ticking with the rest of the view effects.
        float alpha = Mathf.Clamp(Mathf.Sin(_time * 10f) * 0.25f + 0.75f, 0.2f, 0.9f);
        SetAlpha(_darkBlind, new Color(0f, 0f, 0f), alpha);
    }

    /// <summary>
    /// The in-orb 2D colour flash — port of <c>orb_draw2d</c> (mutators/mutator/nades/nades.qc). When the eye
    /// is inside a nade orb's radius, the screen is painted ADDITIVELY with that orb's colour at
    /// <c>hud_colorflash_alpha * orb.Alpha</c>, so standing inside e.g. a heal/veil orb tints the view its colour.
    /// When the eye sits inside several overlapping orbs the STRONGEST one wins (the QC orbs draw in turn but the
    /// additive layer is dominated by the brightest contribution; we take the max so it reads stably).
    /// </summary>
    /// <param name="eyeOrigin">The local view origin in world space (QC <c>view_origin</c>), Quake units.</param>
    /// <param name="orbs">The live orbs: each (world Origin, containment Radius, flash Color, per-orb Alpha 0..1).</param>
    public void UpdateOrbColorFlash(
        System.Numerics.Vector3 eyeOrigin,
        System.Collections.Generic.IReadOnlyList<(System.Numerics.Vector3 Origin, float Radius, Color Color, float Alpha)> orbs)
    {
        float flashAlpha = Cvar("hud_colorflash_alpha", DefColorFlashAlpha);

        // Find the strongest orb whose radius contains the eye (QC: each orb tints if vlen(view_origin-org) < radius).
        bool any = false;
        Color best = new(0f, 0f, 0f);
        float bestAlpha = 0f;
        if (orbs is not null)
        {
            for (int i = 0; i < orbs.Count; i++)
            {
                var orb = orbs[i];
                if ((eyeOrigin - orb.Origin).Length() >= orb.Radius)
                    continue;
                float a = flashAlpha * orb.Alpha;
                if (a <= bestAlpha)
                    continue;
                bestAlpha = a;
                best = orb.Color;
                any = true;
            }
        }

        if (!any)
        {
            SetAlpha(_orbFlash, new Color(0f, 0f, 0f), 0f);
            return;
        }
        SetAlpha(_orbFlash, best, bestAlpha);
    }

    private static void SetAlpha(ColorRect rect, Color rgb, float alpha)
    {
        alpha = Mathf.Clamp(alpha, 0f, 1f);
        rect.Visible = alpha > 0.0001f;
        if (rect.Visible)
            rect.Color = new Color(rgb.R, rgb.G, rgb.B, alpha);
    }

    /// <summary>
    /// Read a float cvar live, falling back to <paramref name="fallback"/> only when the cvar is genuinely unset
    /// (its string value is empty). An explicit <c>0</c> is honored — so e.g. <c>hud_damage 0</c> disables the
    /// flash rather than being reinterpreted as "use the default".
    /// </summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// Read an "r g b" vector cvar as a <see cref="Color"/> (QC <c>stov(autocvar_…)</c>), falling back to
    /// <paramref name="fallback"/> when the cvar is unset or unparseable. Alpha is left at 1 (the effect's own
    /// alpha is applied separately in <see cref="SetAlpha"/>).
    /// </summary>
    private static Color CvarColor(string name, Color fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrWhiteSpace(s))
            return fallback;
        string[] parts = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)
            || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g)
            || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
            return fallback;
        return new Color(r, g, b);
    }
}
