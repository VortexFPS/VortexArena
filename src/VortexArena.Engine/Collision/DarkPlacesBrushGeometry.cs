// Port of Base/darkplaces/collision.c Collision_NewBrushFromPlanes (the points and edge directions it
// derives from a brush's planes) and polygon.c PolygonD_QuadForPlane / PolygonD_Divide.
using System.Numerics;

namespace VortexArena.Engine.Collision;

/// <summary>
/// The corner points and edge directions of a brush as DarkPlaces derives them: for each plane in
/// turn a huge quad on it is clipped by every other plane ("construct a collision brush (points,
/// planes, and renderable mesh) from a set of planes"), and the corners of what is left are collected
/// - a corner closer than a quarter of a unit to one already collected being that one.
///
/// <see cref="BspCollisionBuilder"/>'s own way (intersect every three planes, keep what lies inside)
/// finds the same brush, but not the same floats: where several corners fall within that quarter
/// unit of each other - the thin end of a sliver, of which model clip brushes are full - DarkPlaces
/// keeps the first one its plane order reaches and this builder kept another, and the sweep measures
/// a brush by its corners ("startplane[3] = furthestplanedist_float(startplane, other->points)"). On
/// xoylent that put the top of one such brush 0.06 units lower than a DarkPlaces server has it.
/// Used when <see cref="BspCollisionOptions.DarkPlacesBrushPoints"/> is set.
/// </summary>
internal static class DarkPlacesBrushGeometry
{
    private const int MaxPlanes = 256, MaxPoints = 256, MaxEdgeDirs = 256, MaxElements = 256, PolygonMaxPoints = 64;
    private const double PlaneDistEpsilon = 2.0f / 32.0f;   // COLLISION_PLANE_DIST_EPSILON
    private const float Snap2 = 2.0f / 32.0f;               // COLLISION_SNAP2, compared with a squared distance
    private const float EdgeDirDotEpsilon = 0.999f;         // COLLISION_EDGEDIR_DOT_EPSILON

    /// <summary>
    /// False where Collision_NewBrushFromPlanes returns NULL - the brush then does not exist for a
    /// DarkPlaces server: 256 planes or more, a face of more than 64 corners, more than 256 distinct
    /// corners or edge directions, faces that make more than 85 triangles between them, or fewer
    /// than four planes.
    /// </summary>
    public static bool Build(BrushPlane[] planes, out Vector3[] points, out Vector3[] edgeDirs)
    {
        points = edgeDirs = Array.Empty<Vector3>();
        int count = planes.Length;
        // check if there are too many planes and skip the brush
        if (count >= MaxPlanes) return false;
        // figure out how large a bounding box we need to properly compute this brush
        double maxdist = 0;
        for (int j = 0; j < count; j++) maxdist = Math.Max(maxdist, Math.Abs(planes[j].Dist));
        // now make it large enough to enclose the entire brush, and round it off to a reasonable multiple of 1024
        maxdist = Math.Floor(maxdist * (4.0 / 1024.0) + 2) * 1024.0;

        List<Vector3> pointsBuf = new(), edgeBuf = new();
        double[] a = new double[3 * PolygonMaxPoints], b = new double[3 * PolygonMaxPoints];
        int elements = 0;
        for (int j = 0; j < count; j++)
        {
            // create a large polygon from the plane
            double[] poly = a, other = b;
            QuadForPlane(poly, planes[j].Normal.X, planes[j].Normal.Y, planes[j].Normal.Z, planes[j].Dist, maxdist);
            int n = 4;
            // clip it by all other planes
            for (int k = 0; k < count && n >= 3 && n <= PolygonMaxPoints; k++)
            {
                if (k == j) continue;
                // we want to keep the inside of the brush plane so we flip the cutting plane
                n = Divide(n, poly, -planes[k].Normal.X, -planes[k].Normal.Y, -planes[k].Normal.Z, -planes[k].Dist, PlaneDistEpsilon, other);
                (poly, other) = (other, poly);
            }
            // if nothing is left, skip it
            if (n < 3) continue;
            // check if there are too many polygon vertices for buffer
            if (n > PolygonMaxPoints) return false;
            // check if there are too many triangle elements for buffer
            if (elements + (n - 2) * 3 > MaxElements) return false;
            elements += (n - 2) * 3;

            // add the unique points for this polygon
            for (int k = 0; k < n; k++)
            {
                // downgrade to float precision before comparing
                Vector3 v = new((float)poly[k * 3], (float)poly[k * 3 + 1], (float)poly[k * 3 + 2]);
                // check if there is already a matching point (no duplicates)
                int m;
                for (m = 0; m < pointsBuf.Count; m++)
                {
                    Vector3 d = v - pointsBuf[m];
                    if (d.X * d.X + d.Y * d.Y + d.Z * d.Z < Snap2) break;
                }
                if (m == pointsBuf.Count)
                {
                    // check if there are too many and skip the brush
                    if (pointsBuf.Count >= MaxPoints) return false;
                    pointsBuf.Add(v);
                }
            }

            // add the unique edgedirs for this polygon
            for (int k = 0, prev = n - 1; k < n; prev = k, k++)
            {
                // downgrade to float precision before comparing
                Vector3 dir = new((float)(poly[k * 3] - poly[prev * 3]), (float)(poly[k * 3 + 1] - poly[prev * 3 + 1]), (float)(poly[k * 3 + 2] - poly[prev * 3 + 2]));
                // VectorNormalize
                float ilength = dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z;
                if (ilength != 0) ilength = (float)(1.0 / Math.Sqrt(ilength));
                dir = new Vector3(dir.X * ilength, dir.Y * ilength, dir.Z * ilength);
                // check if there is already a matching edgedir (no duplicates)
                if (HasEdgeDir(edgeBuf, dir)) continue;
                // try again with negated edgedir
                dir = -dir;
                if (HasEdgeDir(edgeBuf, dir)) continue;
                // check if there are too many and skip the brush
                if (edgeBuf.Count >= MaxEdgeDirs) return false;
                // add the new one (the negated one, as the C does)
                edgeBuf.Add(dir);
            }
        }
        // if nothing is left, there's nothing to allocate
        if (count < 4) return false;
        points = pointsBuf.ToArray();
        edgeDirs = edgeBuf.ToArray();
        return true;
    }

    private static bool HasEdgeDir(List<Vector3> edges, Vector3 dir)
    {
        foreach (Vector3 e in edges)
            if (dir.X * e.X + dir.Y * e.Y + dir.Z * e.Z >= EdgeDirDotEpsilon) return true;
        return false;
    }

    // PolygonD_QuadForPlane
    private static void QuadForPlane(double[] o, double nx, double ny, double nz, double dist, double size)
    {
        double ux, uy, uz;
        if (Math.Abs(nz) > Math.Abs(nx) && Math.Abs(nz) > Math.Abs(ny)) { ux = 1; uy = 0; uz = 0; }
        else { ux = 0; uy = 0; uz = 1; }
        // d = -DotProduct(quadup, planenormal); VectorMA(quadup, d, planenormal, quadup); VectorNormalize(quadup)
        double d = -(ux * nx + uy * ny + uz * nz);
        ux += d * nx;
        uy += d * ny;
        uz += d * nz;
        d = 1.0 / Math.Sqrt(ux * ux + uy * uy + uz * uz);
        ux *= d;
        uy *= d;
        uz *= d;
        // CrossProduct(quadup, planenormal, quadright)
        double rx = uy * nz - uz * ny, ry = uz * nx - ux * nz, rz = ux * ny - uy * nx;
        // make the points
        o[0] = dist * nx - size * rx + size * ux;
        o[1] = dist * ny - size * ry + size * uy;
        o[2] = dist * nz - size * rz + size * uz;
        o[3] = dist * nx + size * rx + size * ux;
        o[4] = dist * ny + size * ry + size * uy;
        o[5] = dist * nz + size * rz + size * uz;
        o[6] = dist * nx + size * rx - size * ux;
        o[7] = dist * ny + size * ry - size * uy;
        o[8] = dist * nz + size * rz - size * uz;
        o[9] = dist * nx - size * rx - size * ux;
        o[10] = dist * ny - size * ry - size * uy;
        o[11] = dist * nz - size * rz - size * uz;
    }

    // PolygonD_Divide, the front half only: the part of the polygon on the front of the plane, and how
    // many points that takes (more than fit are counted and not stored, as in the C).
    private static int Divide(int inCount, double[] input, double nx, double ny, double nz, double dist, double epsilon, double[] front)
    {
        int frontCount = 0;
        if (inCount == 0) return 0;
        int max = front.Length / 3;
        double ndist = input[0] * nx + input[1] * ny + input[2] * nz - dist;
        for (int i = 0; i < inCount; i++)
        {
            int p = i * 3, n = ((i + 1) < inCount ? (i + 1) : 0) * 3;
            double pdist = ndist;
            ndist = input[n] * nx + input[n + 1] * ny + input[n + 2] * nz - dist;
            if (pdist >= -epsilon)
            {
                if (frontCount < max)
                {
                    front[frontCount * 3] = input[p];
                    front[frontCount * 3 + 1] = input[p + 1];
                    front[frontCount * 3 + 2] = input[p + 2];
                }
                frontCount++;
            }
            if ((pdist > epsilon && ndist < -epsilon) || (pdist < -epsilon && ndist > epsilon))
            {
                double frac = pdist / (pdist - ndist);
                if (frontCount < max)
                {
                    front[frontCount * 3] = input[p] + frac * (input[n] - input[p]);
                    front[frontCount * 3 + 1] = input[p + 1] + frac * (input[n + 1] - input[p + 1]);
                    front[frontCount * 3 + 2] = input[p + 2] + frac * (input[n + 2] - input[p + 2]);
                }
                frontCount++;
            }
        }
        return frontCount;
    }
}
