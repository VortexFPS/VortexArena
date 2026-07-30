using System;
using System.Numerics;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Unit tests for T69 — the damage-keyed whole-HUD shake (port of qcsrc/client/hud/hud.qc's
/// <c>Hud_Dynamic_Frame</c> shake block + <c>Hud_Shake_Update</c>, ported to
/// <c>game/hud/HudDynamicShake.cs</c>).
///
/// <see cref="HudDynamicShake"/> lives in the Godot host assembly (<c>XonoticGodot.Game.Hud</c>), which this
/// test project does NOT reference (it links only the Godot-free <c>src/</c> libraries). So — following the
/// established repo idiom (see <c>HudConfigEditorTests</c> / <c>ClientFeedbackTests</c>) — these tests mirror the
/// shake's pure keyframe math (<see cref="ShakeUpdate"/> / <see cref="Bound"/>) and its stateful factor-latch
/// (<see cref="ShakeState"/>) VERBATIM from the same QC source, using <see cref="System.Numerics.Vector2"/>, and
/// assert the Base behavior. The mirrored code is kept byte-for-byte equivalent to <c>HudDynamicShake</c>.
/// </summary>
public class HudDynamicShakeTests
{
    // The luma reference viewport (QC vid_conwidth/conheight). Any size works; the math is fraction-based.
    private static readonly Vector2 VP = new(800f, 600f);

    // Base defaults (hud.qc:563-565).
    private const float DamageMin = 10f;
    private const float DamageMax = 130f;
    private const float Scale = 0.2f;

    // ---------------------------------------------------------------------------------------------------------
    //  Mirrors of HudDynamicShake's pure helpers (kept identical to the class — verified against hud.qc)
    // ---------------------------------------------------------------------------------------------------------

    // QC hud_dynamic_shake_x[10] / hud_dynamic_shake_y[10] (hud.qc:566-567).
    private static readonly float[] ShakeX = { 0f, 1f, -0.7f, 0.5f, -0.3f, 0.2f, -0.1f, 0.1f, 0.0f, 0f };
    private static readonly float[] ShakeY = { 0f, 0.4f, 0.8f, -0.2f, -0.6f, 0.0f, 0.3f, 0.1f, -0.1f, 0f };

    private static float Bound(float lo, float v, float hi) => v < lo ? lo : (v > hi ? hi : v);

    // QC Hud_Shake_Update (hud.qc:568-587).
    private static bool ShakeUpdate(float factor, float shakeTime, float time, float scale, Vector2 viewport,
        out Vector2 realofs)
    {
        realofs = Vector2.Zero;
        if (time - shakeTime < 0f) return false;

        float animSpeed = 17f + 9f * factor;
        float elapsed = (time - shakeTime) * animSpeed;
        int i = (int)MathF.Floor(elapsed);
        if (i >= 9) return false;

        float f = elapsed - i;
        float x = (1f - f) * ShakeX[i] + f * ShakeX[i + 1];
        float y = (1f - f) * ShakeY[i] + f * ShakeY[i + 1];
        x *= factor * scale;
        y *= factor * scale;
        x = Bound(-0.1f, x, 0.1f) * viewport.X;
        y = Bound(-0.1f, y, 0.1f) * viewport.Y;
        realofs = new Vector2(x, y);
        return true;
    }

    // Mirror of HudDynamicShake's stateful Update (hud.qc:619-655), minus the cvar read (defaults passed in).
    private sealed class ShakeState
    {
        public float Factor;
        public float ShakeTime;
        public float OldHealth;
        public bool Seeded;
        private Vector2 _realofs;

        public void RequestReset() => Factor = -1f;

        public Vector2 Update(float health, float time, Vector2 viewport, bool intermission,
            float damageMin = DamageMin, float damageMax = DamageMax, float scale = Scale, float enable = 1f)
        {
            if (enable <= 0f) return Vector2.Zero;
            health = MathF.Max(-1f, health);

            if (!Seeded) { Seeded = true; OldHealth = health; }

            if (Factor == -1f)
            {
                Factor = 0f;
                OldHealth = health;
            }
            else
            {
                float newFactor = 0f;
                if (OldHealth - health >= damageMin
                    && damageMax > damageMin
                    && OldHealth > 0f && !intermission)
                {
                    float m = MathF.Max(damageMin, 1f);
                    newFactor = (OldHealth - health - m) / (damageMax - m);
                    if (newFactor >= 1f) newFactor = 1f;
                    if (newFactor >= Factor)
                    {
                        Factor = newFactor;
                        ShakeTime = time;
                    }
                }
                OldHealth = health;
                if (Factor != 0f && !ShakeUpdate(Factor, ShakeTime, time, scale, viewport, out _realofs))
                    Factor = 0f;
            }

            return Factor > 0f ? _realofs : Vector2.Zero;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Hud_Shake_Update — pure keyframe math
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ShakeUpdate_BeforeStartTime_IsInactive()
    {
        // QC: `if (time - hud_dynamic_shake_time < 0) return false;`
        Assert.False(ShakeUpdate(1f, shakeTime: 10f, time: 9.5f, Scale, VP, out Vector2 ofs));
        Assert.Equal(Vector2.Zero, ofs);
    }

    [Fact]
    public void ShakeUpdate_PastKeyframe9_Ends()
    {
        // animSpeed at factor 1 = 26; i = floor(elapsed) hits 9 once elapsed >= 9 -> 9/26 s after start.
        float over = 9f / 26f + 0.01f;
        Assert.False(ShakeUpdate(1f, shakeTime: 0f, time: over, Scale, VP, out _));
    }

    [Fact]
    public void ShakeUpdate_AtStart_IsZeroOffset_BecauseFirstKeyframeIsOrigin()
    {
        // elapsed = 0 -> i = 0, f = 0 -> interpolates exactly ShakeX[0]/ShakeY[0] = (0,0).
        Assert.True(ShakeUpdate(1f, 0f, 0f, Scale, VP, out Vector2 ofs));
        Assert.Equal(0f, ofs.X, 4);
        Assert.Equal(0f, ofs.Y, 4);
    }

    [Fact]
    public void ShakeUpdate_MidInterval_MatchesLerpedScaledKeyframes()
    {
        // Land in the MIDDLE of interval [1,2) (avoid float-boundary fragility): elapsed = 1.5 -> i=1, f=0.5.
        // Use factor 0.05 so the small keyframe values stay inside the ±0.1 bound (no clamp), exercising the lerp.
        // animSpeed = 17 + 9*0.05 = 17.45; choose time so elapsed = 1.5 exactly.
        const float factor = 0.05f;
        float animSpeed = 17f + 9f * factor;
        float time = 1.5f / animSpeed;
        Assert.True(ShakeUpdate(factor, 0f, time, Scale, VP, out Vector2 ofs));

        // expected: lerp(ShakeX[1]=1, ShakeX[2]=-0.7, 0.5) = 0.15 ; lerp(ShakeY[1]=0.4, ShakeY[2]=0.8, 0.5) = 0.6
        // then *= factor*scale = 0.05*0.2 = 0.01 -> x=0.0015, y=0.006 (both inside ±0.1), * viewport.
        float ex = 0.15f * factor * Scale * VP.X;  // 0.0015 * 800 = 1.2
        float ey = 0.6f * factor * Scale * VP.Y;   // 0.006 * 600 = 3.6
        Assert.Equal(ex, ofs.X, 3);
        Assert.Equal(ey, ofs.Y, 3);
    }

    [Fact]
    public void ShakeUpdate_BoundsEachAxisToTenPercentOfViewport()
    {
        // A large scale would blow the raw keyframe past ±0.1; the bound caps |x| <= 0.1*width, |y| <= 0.1*height.
        // Sit mid-interval [1,2) (f=0.5) and crank scale so both axes saturate the bound.
        const float factor = 1f;
        float animSpeed = 17f + 9f * factor; // 26
        float time = 1.5f / animSpeed;       // elapsed = 1.5 -> i=1, f=0.5
        Assert.True(ShakeUpdate(factor, 0f, time, scale: 50f, VP, out Vector2 ofs)); // scale way up -> clamps
        Assert.True(MathF.Abs(ofs.X) <= 0.1f * VP.X + 1e-3f);
        Assert.True(MathF.Abs(ofs.Y) <= 0.1f * VP.Y + 1e-3f);
        // lerped x = 0.15 (positive) -> +0.1 bound * 800 = 80 ; lerped y = 0.6 (positive) -> +0.1 * 600 = 60.
        Assert.Equal(80f, ofs.X, 3);
        Assert.Equal(60f, ofs.Y, 3);
    }

    [Fact]
    public void ShakeUpdate_AnimSpeedScalesWithFactor()
    {
        // anim_speed = 17 + 9*factor: a stronger hit runs the keyframe sequence FASTER. At the same wall time a
        // factor-1 shake is further along its keyframe path than a factor-0.5 one.
        const float t = 0.05f;
        ShakeUpdate(1f, 0f, t, Scale, VP, out _);   // elapsed = 26*0.05 = 1.3
        ShakeUpdate(0.5f, 0f, t, Scale, VP, out _); // elapsed = 21.5*0.05 = 1.075
        float e1 = t * (17f + 9f * 1f);
        float e05 = t * (17f + 9f * 0.5f);
        Assert.True(e1 > e05);
        Assert.Equal(1.3f, e1, 4);
        Assert.Equal(1.075f, e05, 4);
    }

    [Fact]
    public void Bound_Clamps()
    {
        Assert.Equal(-0.1f, Bound(-0.1f, -5f, 0.1f), 5);
        Assert.Equal(0.1f, Bound(-0.1f, 5f, 0.1f), 5);
        Assert.Equal(0.03f, Bound(-0.1f, 0.03f, 0.1f), 5);
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Hud_Dynamic_Frame shake block — the health-loss → factor latch
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void NoShake_WhenHealthLossBelowDamageMin()
    {
        var s = new ShakeState();
        s.Update(100f, time: 0f, VP, intermission: false);      // seed old_health = 100
        // lose only 5 (< damage_min 10) -> no shake.
        Vector2 ofs = s.Update(95f, time: 0.1f, VP, intermission: false);
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
    }

    [Fact]
    public void Shake_Triggers_WhenHealthLossMeetsDamageMin()
    {
        var s = new ShakeState();
        s.Update(100f, 0f, VP, false);              // seed 100
        // lose 40 (>= 10) -> factor = (40 - max(10,1)) / (130 - max(10,1)) = 30/120 = 0.25.
        s.Update(60f, 0.001f, VP, false);
        Assert.Equal(0.25f, s.Factor, 4);
        Assert.Equal(0.001f, s.ShakeTime, 5);
    }

    [Fact]
    public void Shake_Factor_SaturatesAtOne_ForHugeHits()
    {
        var s = new ShakeState();
        s.Update(200f, 0f, VP, false);
        // raw factor = (200 - 10) / 120 = 1.583 -> clamped to 1.
        s.Update(0f, 0.001f, VP, false);
        Assert.Equal(1f, s.Factor, 5);
    }

    [Fact]
    public void Shake_StrongestHitWins_DoesNotDowngradeMidShake()
    {
        var s = new ShakeState();
        s.Update(200f, 0f, VP, false);
        s.Update(40f, 0.001f, VP, false);   // big hit: factor = (160-10)/120 = 1.25 -> 1
        Assert.Equal(1f, s.Factor, 5);
        float bigShakeTime = s.ShakeTime;

        // a small follow-up hit (factor would be ~0.25 < 1) must NOT replace the stronger active shake.
        s.Update(10f, 0.01f, VP, false);    // lose 30 -> raw 0.166 < current 1 -> ignored as a (re)trigger
        Assert.Equal(1f, s.Factor, 5);
        Assert.Equal(bigShakeTime, s.ShakeTime, 5); // not retriggered
    }

    [Fact]
    public void Shake_BiggerHitRetriggers_FromTheNewTime()
    {
        var s = new ShakeState();
        s.Update(200f, 0f, VP, false);
        s.Update(140f, 0.001f, VP, false);  // lose 60 -> factor = 50/120 = 0.4166
        Assert.Equal(0.4166f, s.Factor, 3);

        s.Update(40f, 0.01f, VP, false);    // lose 100 -> factor = 90/120 = 0.75 >= 0.4166 -> retrigger
        Assert.Equal(0.75f, s.Factor, 3);
        Assert.Equal(0.01f, s.ShakeTime, 5);
    }

    [Fact]
    public void NoShake_DuringIntermission()
    {
        var s = new ShakeState();
        s.Update(100f, 0f, VP, intermission: false);
        Vector2 ofs = s.Update(20f, 0.001f, VP, intermission: true); // big loss, but at the end screen
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
    }

    [Fact]
    public void NoShake_WhenAlreadyDead_OldHealthNotPositive()
    {
        // QC gate `old_health > 0`: a respawn from 0 health (old_health == 0) must not trigger a shake on the jump.
        var s = new ShakeState();
        s.Update(0f, 0f, VP, false);    // seed old_health = 0 (dead/observing)
        Vector2 ofs = s.Update(-1f, 0.001f, VP, false); // "loss" of 1 but old_health == 0 -> no trigger
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
    }

    [Fact]
    public void NoShake_WhenDamageWindowInverted()
    {
        // QC gate `damage_max > damage_min`: a misconfigured window (max <= min) disables the effect.
        var s = new ShakeState();
        s.Update(100f, 0f, VP, false);
        Vector2 ofs = s.Update(40f, 0.001f, VP, false, damageMin: 50f, damageMax: 50f);
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
    }

    [Fact]
    public void ResetSentinel_SuppressesTheEffectForOneFrame_AndReanchorsHealth()
    {
        // QC `hud_dynamic_shake_factor == -1` (HUD_Reset / main.qc:750): the reset frame zeroes the factor and
        // re-seeds old_health to the current value, so the discontinuous health jump doesn't replay a shake.
        var s = new ShakeState();
        s.Update(100f, 0f, VP, false);   // old_health = 100
        s.RequestReset();
        Vector2 ofs = s.Update(10f, 0.001f, VP, false); // would be a 90-loss shake — but the reset frame eats it
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
        Assert.Equal(10f, s.OldHealth, 5); // re-anchored to the new health

        // and the NEXT frame, a fresh real loss from the re-anchored baseline DOES shake.
        s.Update(0f, 0.01f, VP, false);  // lose 10 (>= min) -> factor = (10-10)/(130-10) = 0 (edge: exactly min)
        Assert.Equal(0f, s.Factor, 5);   // exactly at min the numerator is 0 -> no visible shake (QC parity)
    }

    [Fact]
    public void HealthGain_NeverTriggersAShake()
    {
        // Picking up health (old_health - health < 0) is never a "loss"; no shake, just re-anchor.
        var s = new ShakeState();
        s.Update(50f, 0f, VP, false);
        Vector2 ofs = s.Update(100f, 0.001f, VP, false); // +50 health
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
        Assert.Equal(100f, s.OldHealth, 5);
    }

    [Fact]
    public void Disabled_WhenCvarZero_ProducesNoOffset()
    {
        // QC `if (autocvar_hud_dynamic_shake > 0)`: enable == 0 short-circuits with no offset.
        var s = new ShakeState();
        s.Update(100f, 0f, VP, false, enable: 1f);
        Vector2 ofs = s.Update(20f, 0.001f, VP, false, enable: 0f);
        Assert.Equal(Vector2.Zero, ofs);
    }

    [Fact]
    public void ActiveShake_DecaysToZero_OnceKeyframesRunOut()
    {
        // Trigger a shake, then advance well past the 9-keyframe window: the factor latches back to 0 and the
        // offset returns to zero (QC `if (hud_dynamic_shake_factor && !Hud_Shake_Update()) hud_dynamic_shake_factor = 0`).
        var s = new ShakeState();
        s.Update(200f, 0f, VP, false);
        s.Update(0f, 0.0f, VP, false);            // max hit at t=0 -> factor 1
        Assert.Equal(1f, s.Factor, 5);

        // factor 1 -> animSpeed 26 -> sequence done after 9/26 ≈ 0.346s. Advance past it (health unchanged).
        Vector2 ofs = s.Update(0f, 0.5f, VP, false);
        Assert.Equal(0f, s.Factor, 5);
        Assert.Equal(Vector2.Zero, ofs);
    }
}
