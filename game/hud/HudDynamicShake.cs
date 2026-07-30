// Port of qcsrc/client/hud/hud.qc — the `hud_dynamic_shake` half of Hud_Dynamic_Frame (619-655) plus its
// Hud_Shake_Update helper (568-587). This is the damage-keyed whole-HUD nudge: when the local player loses
// enough health in a frame, the entire HUD root jitters along a fixed 9-keyframe path (a scaled
// `hud_dynamic_shake_x[]`/`_y[]` polyline), the strength of the jitter scaling with how much health was lost,
// and decaying out over the keyframe sequence. The strongest of overlapping hits wins (a bigger hit during an
// active shake retriggers from the start), and the effect is suppressed for the first frame after a HUD reset
// and during intermission.
//
// QC seams mirrored here:
//   * Hud_Shake_Update (568) ..................... ShakeUpdate() — keyframe interpolation + bound + scale
//   * Hud_Dynamic_Frame shake block (619-655) .... Update() — the health-loss → factor latch + decay + offset
//
// Mapping notes: QC `time` → the HUD clock supplied by the host (HudManager local clock); QC `STAT(HEALTH)`
// → the local player's RES_HEALTH (max(-1, health)); QC `vid_conwidth/conheight` → the viewport size; QC
// `hud_dynamic_shake_factor == -1` reset sentinel (set by HUD_Reset / hud_config load / main.qc:750) →
// RequestReset(). The pure keyframe math is kept as small static helpers (using System.Numerics.Vector2, no
// Godot types) so HudDynamicShakeTests can mirror them exactly — the test project can't reference this Godot
// assembly. The cvar reads supply Base's `autocvar_*` defaults as fallbacks (the cvar store returns "" for a
// menu checkbox the user never toggled), matching how HudPanel.GlobalF defaults unregistered globals.

using System;
using System.Numerics;
using VortexArena.Game.Menu; // MenuState.Cvars — the shared menu/console store (live console `set` reaches it)

namespace VortexArena.Game.Hud;

/// <summary>
/// The damage-keyed whole-HUD shake (QC <c>Hud_Dynamic_Frame</c> shake block + <c>Hud_Shake_Update</c>). Holds
/// the per-frame state QC kept in function statics/globals (<c>old_health</c>, <c>hud_dynamic_shake_factor</c>,
/// <c>hud_dynamic_shake_time</c>, <c>hud_dynamic_shake_realofs</c>) and, each frame, turns a health-loss into a
/// decaying pixel offset added to the HUD root. Driven each frame from <see cref="Hud._Process"/>.
/// </summary>
public sealed class HudDynamicShake
{
    // ---- cvar names + Base defaults (hud.qc:562-565) ----
    private const string CvarEnable    = "hud_dynamic_shake";             // autocvar_hud_dynamic_shake = 1
    private const string CvarDamageMax = "hud_dynamic_shake_damage_max";  // = 130
    private const string CvarDamageMin = "hud_dynamic_shake_damage_min";  // = 10
    private const string CvarScale     = "hud_dynamic_shake_scale";       // = 0.2

    private const float DefEnable    = 1f;
    private const float DefDamageMax = 130f;
    private const float DefDamageMin = 10f;
    private const float DefScale     = 0.2f;

    // QC hud_dynamic_shake_x[10] / hud_dynamic_shake_y[10] (hud.qc:566-567) — the fixed keyframe polyline.
    private static readonly float[] ShakeX = { 0f, 1f, -0.7f, 0.5f, -0.3f, 0.2f, -0.1f, 0.1f, 0.0f, 0f };
    private static readonly float[] ShakeY = { 0f, 0.4f, 0.8f, -0.2f, -0.6f, 0.0f, 0.3f, 0.1f, -0.1f, 0f };

    // ---- per-frame state (QC statics/globals) ----
    private float _factor;          // hud_dynamic_shake_factor (>0 = a shake is playing; -1 = reset sentinel)
    private float _shakeTime;       // hud_dynamic_shake_time (the `time` the active shake started)
    private float _oldHealth;       // the `static float old_health` in Hud_Dynamic_Frame
    private bool _seeded;           // QC seeds old_health implicitly from 0 on the first frame; we track it so
                                    // the very first health read doesn't register as a huge "loss" from 0.

    /// <summary>QC <c>hud_dynamic_shake_factor = -1</c> reset (HUD_Reset / hud_config.qc:1118 / main.qc:750):
    /// suppress the effect for the next frame and re-anchor <c>old_health</c> to the current health, so a
    /// respawn / level change / HUD-config toggle doesn't replay a stale shake from the health jump.</summary>
    public void RequestReset() => _factor = -1f;

    /// <summary>
    /// Advance one frame and return the HUD-root pixel offset to add (QC <c>hud_shift_current.x/y +=
    /// hud_dynamic_shake_realofs.x/y</c>). When <c>hud_dynamic_shake 0</c> (effect disabled) or no shake is
    /// active, returns <see cref="Vector2.Zero"/>.
    /// </summary>
    /// <param name="health">Local player health (QC <c>max(-1, STAT(HEALTH))</c>; the caller passes the raw
    /// resource value — this method applies the <c>max(-1, …)</c> floor). Pass the current value even when the
    /// player is dead/absent (a respawn jump is masked by <see cref="RequestReset"/>).</param>
    /// <param name="time">The HUD clock (QC <c>time</c>).</param>
    /// <param name="viewport">The viewport size in pixels (QC <c>vid_conwidth</c>/<c>vid_conheight</c>).</param>
    /// <param name="intermission">QC <c>intermission</c> — true suppresses new shakes (end-of-match screen).</param>
    public Vector2 Update(float health, float time, Vector2 viewport, bool intermission)
    {
        // hud.qc:619 — `if (autocvar_hud_dynamic_shake > 0)`. When off, QC leaves hud_shift_current untouched
        // (no offset) but does NOT reset the latch state; the next time it's re-enabled it resumes cleanly
        // because old_health is re-seeded below only on the active path. To match, bail with no offset.
        if (CvarOr(CvarEnable, DefEnable) <= 0f)
            return Vector2.Zero;

        float damageMin = CvarOr(CvarDamageMin, DefDamageMin);
        float damageMax = CvarOr(CvarDamageMax, DefDamageMax);
        float scale = CvarOr(CvarScale, DefScale);

        // QC `float health = max(-1, STAT(HEALTH));` (hud.qc:622).
        health = MathF.Max(-1f, health);

        // First-ever frame: seed old_health so we don't read a (old=0 - health) "gain" or a spurious loss.
        if (!_seeded)
        {
            _seeded = true;
            _oldHealth = health;
        }

        if (_factor == -1f) // hud.qc:623 — don't allow the effect for this frame (reset sentinel)
        {
            _factor = 0f;
            _oldHealth = health;
        }
        else
        {
            float newFactor = 0f;
            // hud.qc:631-633 — only trigger on a real health LOSS, with a valid (min<max) damage window, while
            // alive (old_health > 0) and not at the intermission screen.
            if (_oldHealth - health >= damageMin
                && damageMax > damageMin
                && _oldHealth > 0f && !intermission)
            {
                float m = MathF.Max(damageMin, 1f);                  // hud.qc:635
                newFactor = (_oldHealth - health - m) / (damageMax - m); // hud.qc:636
                if (newFactor >= 1f)                                 // hud.qc:637-638
                    newFactor = 1f;
                if (newFactor >= _factor)                            // hud.qc:639 — strongest hit wins / retrigger
                {
                    _factor = newFactor;
                    _shakeTime = time;                               // hud.qc:642
                }
            }
            _oldHealth = health;                                     // hud.qc:645

            // hud.qc:646-647 — once the keyframe sequence runs out, ShakeUpdate returns false and the shake ends.
            if (_factor != 0f && !ShakeUpdate(_factor, _shakeTime, time, scale, viewport, out _realofs))
                _factor = 0f;
        }

        // hud.qc:650-654 — apply the current offset only while a shake is active.
        if (_factor > 0f)
            return _realofs;
        return Vector2.Zero;
    }

    private Vector2 _realofs; // hud_dynamic_shake_realofs (cached between the compute above and the read below)

    /// <summary>
    /// QC <c>Hud_Shake_Update</c> (hud.qc:568-587): interpolate the keyframe polyline at the elapsed time, scale
    /// by <c>factor * hud_dynamic_shake_scale</c>, bound each axis to ±0.1, and multiply by the viewport size to
    /// get a pixel offset. Returns false (shake over) when the clock is before the start, or the sequence has run
    /// past keyframe 9. Pure — mirrored verbatim by the tests.
    /// </summary>
    public static bool ShakeUpdate(float factor, float shakeTime, float time, float scale, Vector2 viewport,
        out Vector2 realofs)
    {
        realofs = Vector2.Zero;

        if (time - shakeTime < 0f) // hud.qc:570
            return false;

        float animSpeed = 17f + 9f * factor;          // hud.qc:573
        float elapsed = (time - shakeTime) * animSpeed; // hud.qc:574
        int i = (int)MathF.Floor(elapsed);            // hud.qc:575
        if (i >= 9)                                   // hud.qc:576-577
            return false;

        float f = elapsed - i;                        // hud.qc:579
        float x = (1f - f) * ShakeX[i] + f * ShakeX[i + 1]; // hud.qc:580
        float y = (1f - f) * ShakeY[i] + f * ShakeY[i + 1]; // hud.qc:581
        // hud.qc:582 z = 0 (2D HUD — dropped). hud.qc:583 scale by factor * shake_scale.
        x *= factor * scale;
        y *= factor * scale;
        x = Bound(-0.1f, x, 0.1f) * viewport.X;       // hud.qc:584
        y = Bound(-0.1f, y, 0.1f) * viewport.Y;       // hud.qc:585
        realofs = new Vector2(x, y);
        return true;
    }

    /// <summary>QC <c>bound(lo, v, hi)</c> (clamp).</summary>
    public static float Bound(float lo, float v, float hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>Read a global cvar as float, falling back to <paramref name="def"/> when unset/blank (mirrors
    /// HudPanel.GlobalF; the menu checkbox/sliders leave the cvar "" until first touched, so the Base
    /// <c>autocvar_*</c> default must apply here).</summary>
    private static float CvarOr(string name, float def)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? def : MenuState.Cvars.GetFloat(name);
    }
}
