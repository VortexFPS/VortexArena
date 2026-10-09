using System;
using System.Numerics;

namespace VortexArena.Engine.Particles;

/// <summary>
/// The quad DarkPlaces draws for a velocity-stretched spark (<c>PARTICLE_SPARK</c>,
/// cl_particles.c:2817-2825 with R_CalcBeam_Vertex3f, gl_rmain.c:6253-6281), in Quake space and free of any
/// renderer type so the test project can hold it against a transcription of the C.
/// </summary>
public static class ParticleGeometry
{
    /// <summary>
    /// The spark as a centred parallelogram: the quad's corners are <c>org ± along ± across</c>.
    /// <para><paramref name="along"/> is half the streak: <c>normalize(vel) · max(stretch · 0.04 · |vel|,
    /// size · 0.5)</c> — DarkPlaces uses the stretch factor as it is (zero or negative falls to the
    /// <c>size · 0.5</c> floor), and the length follows the CURRENT speed, so a spark shortens as air
    /// friction slows it.</para>
    /// <para><paramref name="across"/> is half the width: <c>size</c> along
    /// <c>normalize(along × (viewOrigin − org))</c> — perpendicular to the streak and to the line from the
    /// spark to the EYE, which is what keeps a spark at the edge of the screen as wide as one in the middle.
    /// (DarkPlaces takes that perpendicular at each END of the streak; one instanced quad can hold only one,
    /// taken here at the centre. The two differ by the angle the streak subtends at the eye.)</para>
    /// Returns false for a spark with no velocity or one aimed exactly at the eye (DarkPlaces draws a
    /// degenerate quad there too).
    /// </summary>
    public static bool SparkAxes(Vector3 org, Vector3 vel, float size, float stretch, Vector3 viewOrigin,
        out Vector3 along, out Vector3 across)
    {
        along = across = default;
        float len = vel.Length();
        if (!(len > 0f))
            return false;
        Vector3 up = vel / len;
        float lenfactor = stretch * 0.04f * len;
        if (lenfactor < size * 0.5f)
            lenfactor = size * 0.5f;
        Vector3 right = Vector3.Cross(up, viewOrigin - org);
        float rl = right.Length();
        if (!(rl > 1e-12f))
            return false;
        along = up * lenfactor;
        across = right * (size / rl);
        return true;
    }
}
