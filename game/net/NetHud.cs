using Godot;
using VortexArena.Net;

namespace VortexArena.Game.Net;

/// <summary>
/// A minimal crosshair + health/armor readout for the networked client, drawn straight from the live
/// <see cref="ClientNet"/> snapshot stats (<see cref="ClientNet.Health"/> / <see cref="ClientNet.Armor"/>). It is
/// deliberately small and self-contained (a plain <see cref="Control"/> drawing in code) and — crucially — needs
/// NO local <c>Player</c> actor, since it reads the networked stats directly. That makes it the always-on
/// fallback that works even on a pure <c>--connect</c> client (where the full <c>Hud</c>'s player-bound panels —
/// health/ammo/weapon bar — have no actor to read).
///
/// <para>As of T34 the net path ALSO stands up the full <c>Hud</c> panel set (weapon bar / ammo / kill-feed /
/// centerprint / timer) alongside this — fed from the net stream + (on a listen server) the local server Player —
/// so this is no longer the only HUD on the net path; it layers on top as the crosshair + a guaranteed
/// health/armor readout.</para>
///
/// The crosshair mirrors the vector fallback of <see cref="VortexArena.Game.Hud.CrosshairPanel"/> (a center gap,
/// four ticks, a dot) and pulses a transient ring on fire (<see cref="PulseFire"/>), so firing reads on screen.
/// </summary>
public sealed partial class NetHud : Control
{
    /// <summary>The network source — the HUD reads the owner's replicated health/armor from it. Null draws only the crosshair.</summary>
    public ClientNet? Net { get; set; }

    /// <summary>
    /// When true, this lightweight HUD suppresses its OWN crosshair + health/armor readout, deferring to the
    /// full skinned HUD's <see cref="VortexArena.Game.Hud.CrosshairPanel"/> / <see cref="VortexArena.Game.Hud.HealthArmorPanel"/>.
    /// The net layer sets this true while a local server <c>Player</c> is present (a <c>--host</c> listen server),
    /// so the skinned panels render instead — avoiding a DOUBLE crosshair/health. On a pure <c>--connect</c> client
    /// (no local Player) it stays false so this remains the always-on fallback. The firing ring still pulses (it's
    /// a transient confirmation that pairs with the predicted shot), but is also gated so the ring doesn't double
    /// up with the skinned crosshair's own hit/fire feedback.
    /// </summary>
    public bool SuppressCrosshairAndHealth { get; set; }

    // ---- crosshair geometry (the vector fallback, in pixels — matches CrosshairPanel's defaults) ----
    private const float GapPixels = 5f;
    private const float TickLength = 8f;
    private const float Thickness = 2f;
    private const float DotRadius = 1.5f;
    private static readonly Color CrosshairColor = new(0.4f, 1f, 0.5f, 0.85f);

    // ---- transient firing ring (pulsed by PulseFire, decays each frame) ----
    private float _fireRing;
    private const float FireDecay = 0.25f;
    private const float RingRadius = 16f;

    private Label _stats = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; // never eat gameplay input

        // A simple bottom-left health/armor label (the textual stand-in for the HealthArmorPanel).
        _stats = new Label
        {
            Name = "Stats",
            Text = "",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _stats.AddThemeFontSizeOverride("font_size", 28);
        _stats.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.92f));
        _stats.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _stats.AddThemeConstantOverride("outline_size", 6);
        AddChild(_stats);
    }

    /// <summary>Pulse the firing ring (called on the rising edge of attack so a shot reads on the crosshair).</summary>
    public void PulseFire() => _fireRing = 1f;

    public override void _Process(double delta)
    {
        using var _nhScope = VortexArena.Game.Client.FrameProfiler.Scope("nethud"); // [profiling] NetHud per-frame
        if (_fireRing > 0f)
            _fireRing = Mathf.Max(0f, _fireRing - (float)delta / FireDecay);

        // Position the stats label in the lower-left and refresh its text from the net stats. Suppressed while
        // the skinned HealthArmorPanel owns the readout (a local Player is present) so the two don't stack.
        if (Net is { Accepted: true } && !SuppressCrosshairAndHealth)
        {
            _stats.Text = $"♥ {Mathf.Max(0, Net.Health)}    ▩ {Mathf.Max(0, Net.Armor)}";
            _stats.Visible = true;
        }
        else
        {
            _stats.Visible = false;
        }
        _stats.Position = new Vector2(32f, Size.Y - 56f);

        QueueRedraw();
    }

    public override void _Draw()
    {
        // When the skinned CrosshairPanel owns the reticle (a local Player is present), draw nothing here so
        // there is no double crosshair / double firing ring — the full HUD's crosshair handles both.
        if (SuppressCrosshairAndHealth)
            return;

        Vector2 center = Size * 0.5f;

        // Vector crosshair: four ticks leaving a center gap, plus a center dot.
        float g = GapPixels, t = TickLength, half = Thickness * 0.5f;
        DrawRect(new Rect2(center.X - g - t, center.Y - half, t, Thickness), CrosshairColor); // left
        DrawRect(new Rect2(center.X + g, center.Y - half, t, Thickness), CrosshairColor);      // right
        DrawRect(new Rect2(center.X - half, center.Y - g - t, Thickness, t), CrosshairColor);  // up
        DrawRect(new Rect2(center.X - half, center.Y + g, Thickness, t), CrosshairColor);      // down
        if (DotRadius > 0f)
            DrawCircle(center, DotRadius, CrosshairColor);

        // Firing ring: a shrinking ring pulsed on fire.
        if (_fireRing > 0f)
        {
            float r = RingRadius * (1f + (1f - _fireRing) * 0.6f);
            var c = new Color(CrosshairColor.R, CrosshairColor.G, CrosshairColor.B, _fireRing * CrosshairColor.A);
            DrawArc(center, r, 0f, Mathf.Tau, 40, c, 2f, antialiased: true);
        }
    }
}
