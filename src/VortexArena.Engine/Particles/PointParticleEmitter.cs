using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Engine.Collision;

namespace VortexArena.Engine.Particles;

/// <summary>
/// The per-frame emission of a <c>func_pointparticles</c> / <c>func_sparks</c> map entity — Xonotic's
/// <c>Draw_PointParticles</c> (qcsrc/common/mapobjects/func/pointparticles.qc:166-233) without the engine
/// calls, so the test project can run it. One instance per map entity; the host calls <see cref="Step"/>
/// once a frame and plays the entity's effect at each returned point.
///
/// <para>What the QuakeC does, and this repeats: an emitter does NOT stream particles. Each frame it works
/// out a number of EMISSIONS <c>n = impulse · frametime</c> and runs
/// <c>for (i = random(); i &lt;= n; ++i)</c> — so with <c>impulse 4</c> at 250 frames a second most frames
/// emit nothing and about four frames a second emit once. Every emission is one whole
/// <c>pointparticles(effect, point, velocity, count)</c>: a complete burst of the effect (for stormkeep's
/// <c>sparks</c> with <c>count 6</c>, ninety sparks at one point).</para>
/// </summary>
public sealed class PointParticleEmitter
{
    /// <summary>QC <c>.absolute</c>: how <see cref="Impulse"/> is read.</summary>
    public enum Mode
    {
        /// <summary>The map gave a negative impulse: emissions per second per 64³ units of the volume. A
        /// point that falls outside the brush is simply not emitted (the density is per volume).</summary>
        Relative = 0,
        /// <summary>Emissions per second for the whole entity. A point outside the brush is retried.</summary>
        Absolute = 1,
        /// <summary><c>PARTICLES_IMPULSE</c>: <see cref="Impulse"/> emissions at the moment the entity is
        /// switched on, and none otherwise.</summary>
        OnlyAtToggle = 2,
    }

    /// <summary>World-space box the points are drawn from (<c>origin + mins</c> .. <c>origin + maxs</c>).</summary>
    public Vector3 BoxMin, BoxMax;

    /// <summary>The entity's brushes in model space, or null when it has no brush model (then every point
    /// of the box counts — QC <c>WarpZoneLib_BoxTouchesBrush</c> answers 1 for an entity with no model).</summary>
    public IReadOnlyList<Brush>? Brushes;

    /// <summary>World position of the brush model's origin (the entity's origin).</summary>
    public Vector3 BrushOrigin;

    /// <summary>Emissions per second, already converted from the map's value by <see cref="Configure"/>.</summary>
    public float Impulse;

    public Mode Absolute = Mode.Absolute;

    /// <summary>QC <c>.just_toggled</c>: the entity was switched on since the last emission.</summary>
    public bool JustToggled;

    /// <summary>
    /// Set <see cref="Impulse"/> and <see cref="Absolute"/> from the map's <c>impulse</c> key, as the client
    /// half of pointparticles.qc does on receiving it (:316-326): a negative value is a density and is
    /// multiplied by the box's volume over 64³; the <c>PARTICLES_IMPULSE</c> spawnflag overrides the mode.
    /// </summary>
    public void Configure(float mapImpulse, bool impulseSpawnflag)
    {
        Impulse = mapImpulse;
        Absolute = mapImpulse >= 0f ? Mode.Absolute : Mode.Relative;
        if (Absolute == Mode.Relative)
        {
            Vector3 v = BoxMax - BoxMin;
            Impulse *= -v.X * v.Y * v.Z / (64f * 64f * 64f);
        }
        if (impulseSpawnflag)
            Absolute = Mode.OnlyAtToggle;
    }

    /// <summary>
    /// One frame of Draw_PointParticles: appends this frame's emission points to <paramref name="points"/>
    /// (which is cleared first) and returns how many there are. <paramref name="frametime"/> is the client
    /// frame time in seconds (QC <c>drawframetime</c>); <paramref name="rng"/> stands for QC <c>random()</c>.
    /// </summary>
    public int Step(float frametime, Random rng, List<Vector3> points)
    {
        points.Clear();
        // n = doBGMScript(this), which is 1 for an entity with no bgmscript.
        float n = 1f;
        if (Absolute == Mode.OnlyAtToggle)
            n = JustToggled ? Impulse : 0f;
        else
        {
            n *= Impulse * frametime;
            if (JustToggled && n < 1f)
                n = 1f;
        }
        if (n == 0f)
            return 0;

        Vector3 size = BoxMax - BoxMin;
        float fail = 0f;
        for (float i = rng.NextSingle(); i <= n && fail <= 64f * n; ++i)
        {
            var p = new Vector3(
                BoxMin.X + rng.NextSingle() * size.X,
                BoxMin.Y + rng.NextSingle() * size.Y,
                BoxMin.Z + rng.NextSingle() * size.Z);
            if (Contains(p))
            {
                points.Add(p);
                JustToggled = false;
            }
            else if (Absolute != Mode.Relative)
            {
                ++fail;
                --i;
            }
        }
        return points.Count;
    }

    /// <summary>True when <paramref name="p"/> is inside the entity's brush model (any of its brushes), or
    /// the entity has none.</summary>
    public bool Contains(Vector3 p)
    {
        IReadOnlyList<Brush>? brushes = Brushes;
        if (brushes is null || brushes.Count == 0)
            return true;
        Vector3 local = p - BrushOrigin;
        for (int b = 0; b < brushes.Count; b++)
        {
            BrushPlane[] sides = brushes[b].Sides;
            bool inside = sides.Length > 0;
            for (int s = 0; s < sides.Length; s++)
            {
                if (Vector3.Dot(sides[s].Normal, local) - sides[s].Dist > 0f)
                {
                    inside = false;
                    break;
                }
            }
            if (inside)
                return true;
        }
        return false;
    }

    /// <summary>QC <c>randomvec()</c>: a uniform point of the unit ball (DarkPlaces VM_randomvec rejects
    /// samples of the cube until one lands inside).</summary>
    public static Vector3 RandomVec(Random rng)
    {
        Vector3 v;
        do
        {
            v = new Vector3(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f);
        }
        while (Vector3.Dot(v, v) >= 1f);
        return v;
    }
}
