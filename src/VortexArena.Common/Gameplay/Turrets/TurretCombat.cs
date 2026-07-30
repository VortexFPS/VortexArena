using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Turret-side hitscan + beam helpers that add the pieces the shared <see cref="WeaponFiring"/> defers
/// (deterministic spread cone + knockback force), which the turret weapons need to match QC
/// <c>fireBullet</c> / <c>FireImoBeam</c>. Kept here in the turret folder (WeaponFiring.cs is off-limits)
/// and Godot-free. Damage routes through <see cref="Combat.Damage"/> so armor/knockback/kill all flow
/// through the real pipeline.
///
/// Deferred to match the WeaponFiring gaps (not turret-specific): solid penetration / multi-hit, distance
/// falloff, antilag/warpzones, accuracy logging, tracer effects.
/// </summary>
public static class TurretCombat
{
    /// <summary>
    /// Faithful turret <c>fireBullet</c> (server/weapons/tracing.qc): apply the deterministic spread cone to
    /// <paramref name="dir"/>, trace from <paramref name="start"/>, and damage the first hit with
    /// <paramref name="damage"/> + an outward <paramref name="force"/> knockback along the shot direction.
    /// Returns the entity hit (or null).
    ///
    /// <para><b>Tracer</b>: pass <paramref name="tracerEffect"/> (the QC <c>tracer_effect</c> arg of
    /// <c>fireBullet</c>, e.g. "BULLET" = EFFECT_BULLET) to sweep the bullet trail from the muzzle to the
    /// impact. Faithful to the player <see cref="WeaponFiring.FireBullet"/> path: the trail is drawn only when
    /// the segment is longer than QC's 16u open-air threshold so a point-blank shot doesn't draw a degenerate
    /// zero-length tracer. The turret path emits this just like the player path — in Base machinegun_weapon.qc
    /// the <c>fireBullet(... EFFECT_BULLET)</c> tracer is OUTSIDE the <c>if (isPlayer)</c> gate, so it is
    /// turret-visible. null (the default) keeps the old no-tracer behaviour.</para>
    /// </summary>
    public static Entity? FireBullet(Entity turret, Vector3 start, Vector3 dir, float spread,
        float damage, float force, string deathType, string? tracerEffect = null)
        => FireBullet(turret, start, dir, spread, damage, force, deathType, out _, tracerEffect);

    /// <summary>
    /// <see cref="FireBullet(Entity, Vector3, Vector3, float, float, float, string, string)"/> with the trace
    /// impact point exposed (QC <c>trace_endpos</c>) — the instagib plasma turret needs it to draw the team-
    /// coloured hit beam (plasma.qc tr_attack <c>EFFECT_VAPORIZER_BEAM</c>). <paramref name="endPos"/> is the
    /// muzzle when the shot can't be traced (no live services).
    /// </summary>
    public static Entity? FireBullet(Entity turret, Vector3 start, Vector3 dir, float spread,
        float damage, float force, string deathType, out Vector3 endPos, string? tracerEffect = null)
    {
        endPos = start;
        if (Api.Services is null) return null;

        Vector3 shotDir = ApplySpread(dir, spread);
        Vector3 end = start + shotDir * TurretAI.CurrentMaxShotDistance;
        TraceResult tr = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, turret);
        endPos = tr.EndPos;

        // EFFECT_BULLET tracer trail (QC fireBullet_trace_callback): sweep the trail from the muzzle to the
        // impact, but only when the segment is long enough (>16u) so a point-blank shot doesn't draw a
        // degenerate zero-length tracer — matching WeaponFiring.FireBullet's open-air gate.
        if (!string.IsNullOrEmpty(tracerEffect))
        {
            Effect? tracer = Effects.ByName(tracerEffect);
            if (tracer is not null && (tr.EndPos - start).Length() > 16f)
                EffectEmitter.EmitTrail(tracer, start, tr.EndPos);
        }

        Entity? hit = tr.Ent;
        if (hit is not null && hit.TakeDamage != DamageMode.No)
            Combat.Damage(hit, turret, turret, damage, deathType, tr.EndPos, shotDir * force);
        return hit;
    }

    /// <summary>
    /// W_CalculateSpread core (common/weapons/calculations.qc) — the deterministic spread cone (ADR-0010,
    /// <see cref="Prandom"/>). A zero spread returns the normalized direction unchanged.
    /// </summary>
    public static Vector3 ApplySpread(Vector3 dir, float spread)
    {
        Vector3 fwd = QMath.Normalize(dir);
        if (spread <= 0f) return fwd;
        QMath.AngleVectors(QMath.VecToAngles(fwd), out Vector3 f, out Vector3 r, out Vector3 u);
        return Prandom.Spread(f, r, u, spread);
    }
}
