using System;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Playermodel lean — the port of the never-merged Base branch <c>mirceakitsune/lean_players</c>
/// (Aug 2011, math by divVerent; recovered from branch tip <c>c3fe0de24ef372ce17b7ecc28c8b6724cc5cc418</c>):
/// the player model tips against its acceleration ("lean back while accelerating forward, lean in front when
/// stopping") and away from the direction of incoming damage, server-side, visible to everyone.
///
/// <para><b>Structural divergence from the original</b>: the original composed the lean into
/// <c>self.angles</c> in PlayerPreThink — output fed back as next frame's input. On a live 2011 player the
/// engine re-derived <c>.angles</c> from <c>.v_angle</c> every frame so the feedback was masked, but on a DEAD
/// player nothing reset the angles and the per-frame multiply compounded — the "dead players spin all over the
/// place" bug the author worked around by skipping dead players. The port instead recomputes a STANDALONE
/// offset (<see cref="Entity.LeanAngles"/>) from scratch every tick and composes it at render time
/// (EntityNode), so no output is ever fed back and no accumulation is possible — also required here because
/// <c>Player.Angles</c> drives the weapon fire direction (W_SetupShot), which the original's in-place write
/// would have tilted.</para>
///
/// <para><b>Fixes over the original</b> (each pinned by PlayerLeanTests):</para>
/// <list type="number">
///   <item>Operator precedence: QC computed <c>accel = velocity - avg_vel * cvar</c> — i.e.
///         <c>velocity − (avg·0.05)</c> ≈ velocity — so the "acceleration" lean was pegged at its bound the
///         whole time the player MOVED, not while accelerating. Intended (and implemented here):
///         <c>(velocity − avg_vel) · cvar</c>, which is zero at constant velocity.</item>
///   <item>The off-switch: the accel-lean transform was computed OUTSIDE the
///         <c>if (autocvar_g_leanplayer_acceleration)</c> gate (only the velocity averaging was inside), so
///         <c>g_leanplayer_acceleration 0</c> still leaned (avg frozen at 0 → accel = velocity). Both terms
///         are now properly gated by their cvars; <c>g_leanplayer_damage</c> ships 0 = disabled.</item>
///   <item>Framerate independence: the QC velocity average (<c>avg = avg·(1−f) + vel·f</c>,
///         <c>f = frametime·fade</c>) and the damage decay (<c>force *= 0.9</c> per FRAME) both changed speed
///         with the tick rate (and the linear <c>f</c> exceeds 1 below ~5 fps, flipping the average's sign).
///         Both use exponential smoothing here: <c>frac = 1 − exp(−dt·fade)</c> and
///         <c>force *= exp(−dt·fade)</c>. <c>g_leanplayer_damage_fade</c> is therefore a RATE in 1/s now
///         (default 6.3 ≈ the original 0.9/tick at 60 Hz).</item>
///   <item>Direction-blind lean: the original's accel construction
///         <c>Multiply(Invert('0 yaw 0'), '|a| yaw 0')</c> is algebraically <c>R_z(−θ)·R_z(θ)·R_y(p) =
///         R_y(p)</c> — the acceleration's YAW cancels out, so the shipped code tipped the body straight
///         back by |accel| no matter which way the acceleration pointed (decelerating leaned BACK too).
///         This is the "player leans where it faces, not where the velocity is, cuz I can't figure the
///         correct maths" complaint from the branch's own commit trail, never actually resolved. The issue
///         text promises "lean opposite their acceleration direction": that needs the pitch CONJUGATED by
///         the accel-vs-body yaw — <c>R_z(Δ)·R_y(p)·R_z(−Δ)</c>, Δ = accelYaw − bodyYaw — which reduces to
///         the original's pitch-back for forward acceleration and correctly flips forward on deceleration /
///         banks sideways on strafe acceleration. The damage lean gets the same body-yaw conjugation so a
///         wounded stagger stays anchored to the WORLD-space shot direction while the victim turns,
///         instead of rotating along with the body.</item>
/// </list>
///
/// All math is Quake/makevectors space (down-positive pitch) — the port's entity angles never carry the DP
/// <c>.angles</c> pitch flip, so QC's <c>AnglesTransform_FromAngles/ToAngles</c> conversions collapse away and
/// QC <c>vectoangles + FromAngles</c> becomes <see cref="QMath.FixedVecToAngles"/>.
/// </summary>
public static class PlayerLean
{
    /// <summary>Below this many degrees a lean offset component snaps to zero — keeps the networked field
    /// (and its delta bit) quiet at rest instead of trickling sub-quantization noise forever.</summary>
    private const float AngleEpsilon = 0.01f;

    /// <summary>Below this stored-force magnitude the damage lean is considered decayed to rest
    /// (the max is bounded at ~0.5·√3, so this is well under 1% of full deflection).</summary>
    private const float ForceEpsilon = 0.005f;

    /// <summary>The six <c>g_leanplayer_*</c> cvars, read once per server tick (defaults = the original
    /// branch's <c>defaultXonotic.cfg</c> values, except Damage: the original shipped 0.015, the port ships
    /// 0 = damage lean disabled by default).</summary>
    public readonly struct Config
    {
        /// <summary>g_leanplayer_acceleration: lean scale per u/s of velocity-vs-average difference (0 = off).</summary>
        public readonly float Acceleration;
        /// <summary>g_leanplayer_acceleration_fade: velocity-averaging rate, 1/s (higher = snappier lean).</summary>
        public readonly float AccelerationFade;
        /// <summary>g_leanplayer_acceleration_max: per-component bound on the scaled acceleration (≈ max lean degrees).</summary>
        public readonly float AccelerationMax;
        /// <summary>g_leanplayer_damage: stored-force scale per unit of knockback force (0 = off, the default).</summary>
        public readonly float Damage;
        /// <summary>g_leanplayer_damage_fade: exponential decay rate of the damage lean, 1/s.</summary>
        public readonly float DamageFade;
        /// <summary>g_leanplayer_damage_max: per-component bound on the stored damage-lean force.</summary>
        public readonly float DamageMax;

        public Config(float acceleration, float accelerationFade, float accelerationMax,
                      float damage, float damageFade, float damageMax)
        {
            Acceleration = acceleration;
            AccelerationFade = accelerationFade;
            AccelerationMax = accelerationMax;
            Damage = damage;
            DamageFade = damageFade;
            DamageMax = damageMax;
        }

        /// <summary>Both effects off — Step returns identity and clears the working state.</summary>
        public bool Disabled => Acceleration == 0f && Damage == 0f;
    }

    /// <summary>One tick's outputs: the fresh lean offset plus the advanced working state.</summary>
    public readonly struct Result
    {
        public readonly Vector3 Offset;    // the composed lean transform (Zero = none) → Entity.LeanAngles
        public readonly Vector3 AvgVel;    // advanced velocity average → Entity.LeanAvgVel
        public readonly Vector3 DmgForce;  // decayed damage force → Entity.LeanDmgForce

        public Result(Vector3 offset, Vector3 avgVel, Vector3 dmgForce)
        {
            Offset = offset;
            AvgVel = avgVel;
            DmgForce = dmgForce;
        }
    }

    /// <summary>
    /// Advance one server tick: average the velocity, decay the damage force, and build the composed lean
    /// offset from scratch (QC PlayerPreThink lean block). Pure — the caller writes the results back onto
    /// the entity. <paramref name="bodyYaw"/> is the player's facing (Angles.Y): both lean terms are built
    /// in WORLD space and conjugated into the body frame with it, so the render-time compose
    /// (base ∘ offset) puts the tip axis where the acceleration/shot actually happened (fix #4/#5 — the
    /// original right-multiplied a face-relative transform, which is what made its accel yaw cancel out).
    /// A dead player gets identity and cleared state (QC: "Don't lean dead players, which causes them to
    /// spin all over the place"; with the stateless offset the spin CAN'T happen, but a corpse holding a
    /// frozen lean would still look wrong against the death animation).
    /// </summary>
    public static Result Step(Vector3 velocity, float bodyYaw, Vector3 avgVel, Vector3 dmgLoc, Vector3 dmgForce,
                              bool dead, in Config cfg, float dt)
    {
        if (dead || cfg.Disabled)
        {
            // Track the live velocity while leaning is suspended so a revive/late-enable doesn't read a huge
            // stale velocity difference as one giant fake acceleration spike.
            return new Result(Vector3.Zero, velocity, Vector3.Zero);
        }

        bool haveAccel = false, haveDamage = false;
        Vector3 accelT = Vector3.Zero, damageT = Vector3.Zero;

        // ---- acceleration leaning (gated by its cvar — fix #2) ----
        if (cfg.Acceleration != 0f)
        {
            // Exponential velocity average (framerate-independent form of QC's avg = avg·(1−f) + vel·f — fix #3).
            float frac = 1f - MathF.Exp(-dt * MathF.Max(0f, cfg.AccelerationFade));
            avgVel += (velocity - avgVel) * frac;

            // The velocity-vs-average difference IS the smoothed acceleration signal; scale THEN bound
            // (QC bounded the same way; the precedence fix #1 is the parenthesization here).
            Vector3 accel = (velocity - avgVel) * cfg.Acceleration;
            accel.X = QMath.Bound(-cfg.AccelerationMax, accel.X, cfg.AccelerationMax);
            accel.Y = QMath.Bound(-cfg.AccelerationMax, accel.Y, cfg.AccelerationMax);
            accel.Z = QMath.Bound(-cfg.AccelerationMax, accel.Z, cfg.AccelerationMax);

            float len = accel.Length();
            if (len > AngleEpsilon)
            {
                // "Lean opposite the acceleration direction" (fix #5): pitch UP by |accel| (negative
                // makevectors pitch = the original's FromAngles'd L0 — "lean back while accelerating
                // forward") about the horizontal axis perpendicular to the acceleration's WORLD yaw,
                // expressed in the body frame: R_z(Δ)·R_y(p)·R_z(−Δ), Δ = accelYaw − bodyYaw. For forward
                // acceleration (Δ = 0) this reduces exactly to the original's pitch-back; deceleration
                // (Δ = 180°) flips it forward; strafe acceleration (Δ = ±90°) becomes a sideways bank.
                float delta = Yaw(accel) - bodyYaw;
                var pitchT = new Vector3(-len, 0f, 0f);
                // A pure-yaw transform's inverse is its negation, so conjugate with the (0, ±Δ, 0) pair.
                accelT = QMath.AnglesTransformMultiply(new Vector3(0f, delta, 0f),
                    QMath.AnglesTransformMultiply(pitchT, new Vector3(0f, -delta, 0f)));
                haveAccel = true;
            }
        }
        else
        {
            avgVel = velocity; // keep the tracker current while the effect is off (see the dead branch)
        }

        // ---- damage leaning (ships disabled: g_leanplayer_damage 0) ----
        if (cfg.Damage != 0f && dmgForce != Vector3.Zero)
        {
            // QC: L0 = vectoangles(loc), L1 = vectoangles(loc + force), through FromAngles — the port's
            // FixedVecToAngles (COORDINATE_CONVENTIONS rule 3: the makevectors-consistent inverse). The lean
            // is the WORLD-space rotation that carries the pushed hit direction back onto the hit direction;
            // conjugate it into the body frame (fix #5) so the stagger stays anchored to the shot direction
            // while the victim turns instead of rotating along with the body.
            var l0 = QMath.FixedVecToAngles(dmgLoc);
            var l1 = QMath.FixedVecToAngles(dmgLoc + dmgForce);
            Vector3 worldT = QMath.AnglesTransformMultiply(QMath.AnglesTransformInvert(l1), l0);
            damageT = QMath.AnglesTransformMultiply(new Vector3(0f, -bodyYaw, 0f),
                QMath.AnglesTransformMultiply(worldT, new Vector3(0f, bodyYaw, 0f)));
            haveDamage = true;

            // Exponential per-second decay back to rest (framerate-independent — fix #3; the final original
            // commit removed its earlier damage-scaled fade formula as "didn't work as intended", leaving the
            // plain per-frame multiply this replaces).
            dmgForce *= MathF.Exp(-dt * MathF.Max(0f, cfg.DamageFade));
            if (dmgForce.Length() < ForceEpsilon)
                dmgForce = Vector3.Zero;
        }
        else if (cfg.Damage == 0f)
        {
            dmgForce = Vector3.Zero; // damage lean disabled → drop any leftover stored force at once
        }

        // ---- compose (QC: LA = ((base ∘ accel) ∘ damage) — the base is applied at render time) ----
        Vector3 offset;
        if (haveAccel && haveDamage) offset = QMath.AnglesTransformMultiply(accelT, damageT);
        else if (haveAccel) offset = accelT;
        else if (haveDamage) offset = damageT;
        else return new Result(Vector3.Zero, avgVel, dmgForce);

        offset = QMath.AnglesTransformNormalize(offset, minimizeRoll: false);
        // Snap sub-epsilon components so a fully-decayed lean is EXACTLY zero (render fast-path + net delta quiet).
        if (MathF.Abs(offset.X) < AngleEpsilon) offset.X = 0f;
        if (MathF.Abs(offset.Y) < AngleEpsilon) offset.Y = 0f;
        if (MathF.Abs(offset.Z) < AngleEpsilon) offset.Z = 0f;

        return new Result(offset, avgVel, dmgForce);
    }

    /// <summary>
    /// Register a hit against the damage lean (QC Damage() lean block): store the hit's lever arm and
    /// accumulate the scaled force ("keep existing force if any"), bounding each component to
    /// ±<paramref name="damageMax"/> — the original's fix for the model "get[ting] stuck leaning" past the
    /// limit. No-op while <c>g_leanplayer_damage</c> (<paramref name="damageScale"/>) is 0 (the default).
    /// </summary>
    public static void AccumulateDamage(Entity targ, Vector3 hitLoc, Vector3 force,
                                        float damageScale, float damageMax)
    {
        if (damageScale == 0f || force == Vector3.Zero)
            return;

        targ.LeanDmgLoc = hitLoc - targ.Origin;
        Vector3 f = targ.LeanDmgForce + force * damageScale;
        f.X = QMath.Bound(-damageMax, f.X, damageMax);
        f.Y = QMath.Bound(-damageMax, f.Y, damageMax);
        f.Z = QMath.Bound(-damageMax, f.Z, damageMax);
        targ.LeanDmgForce = f;
    }

    /// <summary>QC <c>vectoyaw(v)</c>: the horizontal yaw of a vector in degrees; 0 for a vector with no
    /// horizontal component (matching the builtin).</summary>
    private static float Yaw(Vector3 v)
        => (v.X == 0f && v.Y == 0f) ? 0f : MathF.Atan2(v.Y, v.X) * (180f / MathF.PI);
}
