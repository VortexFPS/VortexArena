using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests.Perf;

/// <summary>
/// Does adaptive patch subdivision actually close the collision/render gap, and what does it cost
/// (backlog B3)?
///
/// The bug: collision tessellated every patch at a fixed 3 while the renderer used 8, so on a curve the
/// collision hull departed from the drawn surface and a dropped item settled at a visibly wrong height —
/// 6.49 units at worst under Stormkeep's mega health. The fix measures each patch's own curvature and spends
/// subdivisions only where the surface bends, so flat patches (most floors and grates, and exact at any
/// level) stay cheap.
///
/// <para>Reports, per map: the worst remaining deviation from the render tessellation, and the triangle count
/// against what fixed levels would produce — because "accurate" is only the answer if it is also affordable.
/// Run with <c>XG_BENCH=1</c>.</para>
/// </summary>
[Collection("GlobalState")]
public class PatchCollisionAccuracyBench
{
    private static readonly string DataDir = TestPaths.Data;

    private static bool Enabled => Environment.GetEnvironmentVariable("XG_BENCH") is { Length: > 0 };

    /// <summary>Must match BspCollisionBuilder.PatchCollisionTolerance.</summary>
    private const float Tolerance = 1.0f;

    /// <summary>Must match BspCollisionBuilder.PatchWallTolerance.</summary>
    private const float WallTolerance = 6.0f;

    private readonly ITestOutputHelper _out;
    public PatchCollisionAccuracyBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_AdaptivePatchSubdivision()
    {
        if (!Enabled) { _out.WriteLine("bench — set XG_BENCH=1 to run"); return; }
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing — skipped"); return; }
        void Line(string s) => _out.WriteLine(s);

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        Line($"Adaptive vs fixed patch subdivision (tolerance {Tolerance} unit).");
        Line($"{"map",-12} {"patches",8} {"curved",7} | {"worst@3",9} {"worst@adapt",12} "
             + $"| {"tris@3",8} {"tris@adapt",11} {"tris@8",9}");
        Line(new string('-', 92));

        foreach (string map in (Environment.GetEnvironmentVariable("XG_MAPS") ?? "stormkeep,fuse,catharsis")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string bspPath = $"maps/{map}.bsp";
            if (!vfs.Exists(bspPath)) { Line($"{map,-12} (missing)"); continue; }

            BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));

            int patches = 0, curved = 0, tri3 = 0, triA = 0, tri8 = 0;
            float worst3 = 0f, worstA = 0f, worstFloor = 0f;

            foreach (BspFace face in bsp.Faces)
            {
                if (face.Type != BspFaceType.Patch)
                    continue;
                patches++;

                float horizontality = BezierPatch.Horizontality(face, bsp.Vertices);
                float tol = float.Lerp(WallTolerance, Tolerance, horizontality);
                int adaptive = BezierPatch.SubdivisionsFor(face, bsp.Vertices, tol);
                if (adaptive > 1)
                    curved++;

                BezierPatch.Tessellation? t3 = BezierPatch.Tessellate(face, bsp.Vertices, 3);
                BezierPatch.Tessellation? ta = BezierPatch.Tessellate(face, bsp.Vertices, adaptive);
                BezierPatch.Tessellation? t8 = BezierPatch.Tessellate(face, bsp.Vertices, 8);
                if (t3 is null || ta is null || t8 is null)
                    continue;

                tri3 += t3.Indices.Count / 3;
                triA += ta.Indices.Count / 3;
                tri8 += t8.Indices.Count / 3;

                // The reference is the render tessellation; deviation is how far each of its vertices sits
                // from the nearest triangle of the coarser hull.
                worst3 = MathF.Max(worst3, MaxDeviation(t8, t3));
                float devA = MaxDeviation(t8, ta);
                worstA = MathF.Max(worstA, devA);
                if (horizontality >= 0.7f)          // the surfaces things actually rest on
                    worstFloor = MathF.Max(worstFloor, devA);
            }

            Line($"{map,-12} {patches,8} {curved,7} | {worst3,8:F2}u {worstA,11:F2}u "
                 + $"| {tri3,8} {triA,11} {tri8,9} | floors {worstFloor,5:F2}u");
        }

        Line("");
        Line("worst@3 is the bug as it shipped; worst@adapt is what a dropped item now rests within.");
        Line("tris@adapt against tris@8 is what measuring each patch saves over just raising the level.");
    }


    /// <summary>
    /// Largest distance from any vertex of <paramref name="reference"/> to the surface of
    /// <paramref name="coarse"/>, sampled point-to-triangle. This is exactly the error an item dropped onto
    /// the coarse hull inherits.
    /// </summary>
    private static float MaxDeviation(BezierPatch.Tessellation reference, BezierPatch.Tessellation coarse)
    {
        float worst = 0f;
        List<int> ci = coarse.Indices;

        foreach (BezierPatch.PatchVertex v in reference.Vertices)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 2 < ci.Count; i += 3)
            {
                float d = PointTriangleDistance(
                    v.Position,
                    coarse.Vertices[ci[i]].Position,
                    coarse.Vertices[ci[i + 1]].Position,
                    coarse.Vertices[ci[i + 2]].Position);
                if (d < best)
                    best = d;
                if (best <= 1e-4f)
                    break;
            }
            if (best < float.MaxValue && best > worst)
                worst = best;
        }
        return worst;
    }

    private static float PointTriangleDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        // Ericson, Real-Time Collision Detection: closest point on a triangle by barycentric regions.
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return (p - a).Length();

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return (p - b).Length();

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return (p - (a + ab * (d1 / (d1 - d3)))).Length();

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return (p - c).Length();

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return (p - (a + ac * (d2 / (d2 - d6)))).Length();

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).Length();

        float denom = 1f / (va + vb + vc);
        return (p - (a + ab * (vb * denom) + ac * (vc * denom))).Length();
    }
}
