using Godot;
using VortexArena.Common.Gameplay;   // Weapon (Reticle / ZoomOnSecondary)
using VortexArena.Common.Services;    // Api, ICvarService, CvarFlags
using VortexArena.Game.Hud;           // TextureCache (VFS art resolver)

namespace VortexArena.Game.Client;

/// <summary>
/// The zoom "scope" reticle — port of Base/.../qcsrc/client/hud/crosshair.qc <c>DrawReticle</c>. A full-screen
/// image composited over the view while zoomed: the weapon-specific scope (e.g. the Vortex's
/// <c>gfx/reticle_nex</c>) when zooming with a weapon that has one (QC <c>w_reticle</c> + <c>wr_zoom</c>), or the
/// generic <c>gfx/reticle_normal</c> when zooming with the dedicated <c>+zoom</c> button. The image fades with the
/// live zoom (QC <c>current_zoomfraction</c>): it pops in at 25% the instant you start zooming and ramps to full
/// as the zoom completes.
///
/// <para>Like <see cref="ViewEffects"/> this is a host-fed overlay (not self-driving like
/// <see cref="VignetteOverlay"/>): both play paths (<see cref="VortexArena.Game.GameDemo"/> via
/// <see cref="VortexArena.Game.PlayerController"/>, and <see cref="VortexArena.Game.Net.NetGame"/>) own one and
/// call <see cref="UpdateReticle"/> each frame after the shared <see cref="FirstPersonView"/> has stepped the zoom,
/// passing the active weapon + the live button/zoom state. It sits on its own <see cref="CanvasLayer"/> at layer 0
/// — above the 3D world and the <see cref="ViewEffects"/> tint (layer -1), but below every crosshair (the demo HUD
/// defaults to CanvasLayer layer 1; the net HUD is layer 5) so the crosshair always draws on top of the scope, as
/// in QC (HUD_Main draws the reticle, then the crosshair over it).</para>
///
/// <para><b>Cvars</b> (all <c>cl_reticle*</c>, archived — xonotic-client.cfg:42-48):
/// <list type="bullet">
///   <item><c>cl_reticle</c> — master on/off (1 = on).</item>
///   <item><c>cl_reticle_stretch</c> — stretch the image to the full screen (1) vs keep it square+centred (0,
///         the default, which keeps a circular scope round).</item>
///   <item><c>cl_reticle_normal_alpha</c> — opacity of the generic <c>+zoom</c> reticle.</item>
///   <item><c>cl_reticle_weapon</c> / <c>cl_reticle_weapon_alpha</c> — enable + opacity of the weapon scope.</item>
///   <item><c>cl_reticle_chase</c> — show the reticle while in a chase/third-person camera (0 = hide).</item>
/// </list></para>
/// </summary>
public partial class ReticleOverlay : CanvasLayer
{
    private TextureRect _rect = null!;

    public override void _Ready()
    {
        // Layer 0: above the 3D world + ViewEffects (-1), below every crosshair (demo HUD layer 1, net HUD layer
        // 5) so the crosshair sits on top of the scope — matching QC's reticle-then-crosshair draw order.
        Layer = 0;

        // Idempotent — never clobbers a value the cfg/menu store already set (also registered at boot by
        // ClientSettings so the menu/console see them pre-match).
        RegisterDefaults(Api.Cvars);

        _rect = new TextureRect
        {
            Name = "Reticle",
            // The host sets Position/Size explicitly each frame (square-centred or full-screen), so ignore the
            // texture's native size and scale it to fill that rect — the QC drawpic(reticle_pos, …, reticle_size).
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore, // purely cosmetic — never eat input
            Visible = false,
        };
        AddChild(_rect);
    }

    /// <summary>
    /// Port of <c>DrawReticle</c> (crosshair.qc:648-717): decide which reticle (if any) to show and at what alpha,
    /// then composite it. Called each frame by the host after the shared <see cref="FirstPersonView"/> stepped the
    /// zoom.
    /// </summary>
    /// <param name="activeWeapon">The local player's active weapon (its <see cref="Weapon.Reticle"/> /
    /// <see cref="Weapon.ZoomOnSecondary"/> stand in for QC's <c>w_reticle</c> + <c>wr_zoom</c>), or null.</param>
    /// <param name="buttonZoom">The dedicated <c>+zoom</c> bind is held (QC <c>button_zoom</c>).</param>
    /// <param name="attack2Held">ATTACK2 is held (QC <c>button_attack2</c>) — the Vortex's zoom trigger.</param>
    /// <param name="zoomFraction">The live <c>current_zoomfraction</c> from <see cref="FirstPersonView.ZoomFraction"/>.</param>
    /// <param name="dead">Local player is dead (QC <c>STAT(HEALTH) &lt;= 0</c>) — suppress the reticle.</param>
    /// <param name="spectating">Following another player (QC <c>spectatee_status</c>) — suppress.</param>
    /// <param name="chaseActive">A chase / third-person camera is active — suppress unless <c>cl_reticle_chase</c>.</param>
    public void UpdateReticle(Weapon? activeWeapon, bool buttonZoom, bool attack2Held,
                              float zoomFraction, bool dead, bool spectating, bool chaseActive,
                              bool zoomScriptCaught = false)
    {
        if (_rect is null)
            return;

        // QC crosshair.qc:650 — master gate.
        if (CvarF("cl_reticle", 1f) == 0f)
        {
            _rect.Visible = false;
            return;
        }

        // QC:655-666 — scan the active weapon: a weapon with wr_zoom contributes wep_zoomed + its w_reticle. The
        // port has one active weapon; "has wr_zoom" iff it declares a Reticle (the Vortex). QC vortex.qc:346
        // wr_zoom = button_zoom || zoomscript_caught || (!secondary && button_attack2) — so holding the dedicated
        // +zoom with the Vortex out still shows the NEX scope, not the generic one (faithful to base).
        string? reticleImage = null;
        bool wepZoomed = false;
        if (activeWeapon?.Reticle is { Length: > 0 } wr)
        {
            reticleImage = wr;
            wepZoomed = buttonZoom || (attack2Held && activeWeapon.ZoomOnSecondary);
        }

        // QC:671-684 — choose the reticle type. 0 = none, 1 = generic (+zoom), 2 = weapon scope.
        int reticleType;
        if (dead || spectating || (chaseActive && CvarF("cl_reticle_chase", 0f) == 0f))
            reticleType = 0; // no reticle while dead / spectating / chase-cam
        else if (wepZoomed && CvarF("cl_reticle_weapon", 1f) != 0f)
            reticleType = reticleImage is not null ? 2 : 0;
        else if (buttonZoom || zoomScriptCaught) // QC:681 — generic +zoom OR the low-fov zoomscript auto-zoom
            reticleType = 1;
        else
            reticleType = 0;

        if (reticleType == 0)
        {
            _rect.Visible = false;
            return;
        }

        // QC:704 — f = zoomscript_caught ? 1 : max(0.25, current_zoomfraction). With the low-fov zoomscript engaged
        // the reticle is full-alpha; otherwise it pops in at 25% the instant zoom begins and ramps to full as
        // current_zoomfraction → 1. On release the live button flag (above) drops reticle_type to 0 immediately, so
        // the scope snaps off while the fov eases out — exactly as in base.
        float f = zoomScriptCaught ? 1f : Mathf.Max(0.25f, zoomFraction);
        string image = reticleType == 2 ? reticleImage! : "gfx/reticle_normal";
        float alphaCvar = reticleType == 2
            ? CvarF("cl_reticle_weapon_alpha", 1f)
            : CvarF("cl_reticle_normal_alpha", 1f);
        float alpha = Mathf.Clamp(f * alphaCvar, 0f, 1f);
        if (alpha <= 0.001f)
        {
            _rect.Visible = false;
            return;
        }

        Texture2D? tex = TextureCache.Get(image);
        if (tex is null)
        {
            _rect.Visible = false;
            return;
        }

        ApplyGeometry();
        _rect.Texture = tex;
        _rect.Modulate = new Color(1f, 1f, 1f, alpha); // QC drawpic(... '1 1 1', f * alpha) — white tint, alpha = fade
        _rect.Visible = true;
    }

    /// <summary>
    /// Size + place the reticle rect (QC:688-702). <c>cl_reticle_stretch</c> 0 (default) draws a square of side
    /// <c>max(width, height)</c> centred on the screen (keeps a circular scope round; the sides run off-screen);
    /// 1 stretches the image to the exact screen rect (breaks the aspect).
    /// </summary>
    private void ApplyGeometry()
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        if (CvarF("cl_reticle_stretch", 0f) != 0f)
        {
            _rect.Position = Vector2.Zero;
            _rect.Size = vp;
        }
        else
        {
            float side = Mathf.Max(vp.X, vp.Y);
            _rect.Size = new Vector2(side, side);
            _rect.Position = new Vector2((vp.X - side) * 0.5f, (vp.Y - side) * 0.5f);
        }
    }

    /// <summary>
    /// Register the <c>cl_reticle*</c> defaults (archived, xonotic-client.cfg:42-48). Idempotent — keeps any
    /// value the user's config or the menu already set. Called both at boot (from <c>ClientSettings.ApplyAll</c>,
    /// into the shared menu/console store) and from <see cref="_Ready"/> (into the gameplay store) so the cvars
    /// exist everywhere they're read.
    /// </summary>
    public static void RegisterDefaults(ICvarService cvars)
    {
        if (cvars is null)
            return;

        const CvarFlags save = CvarFlags.Save;
        cvars.Register("cl_reticle", "1", save);
        cvars.Register("cl_reticle_stretch", "0", save);
        cvars.Register("cl_reticle_normal", "1", save);       // QC keeps this cvar but DrawReticle reads only _alpha
        cvars.Register("cl_reticle_normal_alpha", "1", save);
        cvars.Register("cl_reticle_weapon", "1", save);
        cvars.Register("cl_reticle_weapon_alpha", "1", save);
        cvars.Register("cl_reticle_chase", "0", save);
    }

    // Live cvar read (fall back only when genuinely unset; an explicit 0 is honoured — e.g. cl_reticle 0).
    private static float CvarF(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
