using System.Numerics;

namespace XonoticGodot.Formats.Md3;

/// <summary>
/// Per-quad frame baking for Q3 <c>deformVertexes autosprite</c>/<c>autosprite2</c> surfaces — the pure
/// math half of the GPU deform (the generated vertex shader is <c>AutospriteShaderGen</c>).
///
/// <para>An autosprite surface is a run of independent 4-vertex quads (Base's bolt models —
/// <c>laser.mdl</c>/<c>elaser.mdl</c>/<c>hlac_bullet.md3</c> — are two such quads each). The deform
/// re-aims each quad at the camera every frame; DarkPlaces does it on the CPU
/// (<c>gl_rmain.c</c> <c>Q3DEFORM_AUTOSPRITE</c>/<c>Q3DEFORM_AUTOSPRITE2</c>). The port instead bakes each
/// quad's <b>view-independent</b> frame here — center, long axis, and every corner's 2-D offset (s, t) in
/// the quad's own tangent frame — into custom vertex attributes, and the vertex shader rebuilds the corner
/// on the view axes: a full screen-plane billboard pivoting at the quad center (<c>autosprite</c>), or an
/// axial billboard that keeps the streak stretched along its long axis and only rolls about it
/// (<c>autosprite2</c>).</para>
///
/// <para>All math runs in <b>Quake space</b> on the raw morph-frame vertices (this library is Godot-free);
/// the s/t scalars are rotation-invariant, so the caller only converts <see cref="Quad.Center"/> /
/// <see cref="Quad.Axis"/> at the Godot boundary. The autosprite2 shortest-edge search — including the
/// 1/1024 height tie-bias — is DP's algorithm verbatim.</para>
/// </summary>
public static class AutospriteQuads
{
    /// <summary>One baked quad: its center and (for autosprite2) the long/flight axis, Quake space.</summary>
    public readonly record struct Quad(Vector3 Center, Vector3 Axis);

    /// <summary>DP's 6 vertex-pair edges of a quad (4 sides + 2 diagonals), <c>gl_rmain.c quadedges</c>.</summary>
    private static readonly int[,] QuadEdges = { { 0, 1 }, { 0, 2 }, { 0, 3 }, { 1, 2 }, { 1, 3 }, { 2, 3 } };

    /// <summary>
    /// Bake the per-quad frames of an autosprite surface for one morph frame.
    /// </summary>
    /// <param name="positions">The surface's vertices for the displayed frame, Quake space, quads in
    /// consecutive groups of 4 (the MD3 layout DP assumes).</param>
    /// <param name="uvs">Per-vertex texcoords; orients the <c>autosprite</c> tangent frame so the sprite
    /// keeps its authored texture orientation on screen (DP's UV-derived svector/tvector). Ignored for
    /// <c>autosprite2</c> (its frame comes from the quad's geometry).</param>
    /// <param name="axial">False = <c>autosprite</c> (full billboard), true = <c>autosprite2</c> (axial).</param>
    /// <param name="s">Out: per-vertex offset along the quad's right/width axis (maps to view-right).</param>
    /// <param name="t">Out: per-vertex offset along the quad's up/long axis (maps to view-up / the axis).</param>
    /// <param name="quads">Out: one <see cref="Quad"/> per 4 vertices.</param>
    /// <returns>False when the surface is not quad-shaped (vertex count 0 or not a multiple of 4, or spans
    /// too small) — the caller must fall back to its non-deform path.</returns>
    public static bool Bake(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector2> uvs,
        bool axial,
        Span<float> s,
        Span<float> t,
        Span<Quad> quads)
    {
        int vcount = positions.Length;
        if (vcount <= 0 || (vcount & 3) != 0)
            return false;
        if (s.Length < vcount || t.Length < vcount || quads.Length < vcount / 4)
            return false;

        for (int q = 0; q < vcount; q += 4)
        {
            Vector3 p0 = positions[q + 0], p1 = positions[q + 1], p2 = positions[q + 2], p3 = positions[q + 3];
            Vector3 center = (p0 + p1 + p2 + p3) * 0.25f;

            Vector3 right, up, axis;
            if (axial)
            {
                (right, axis) = Autosprite2Frame(positions.Slice(q, 4), center);
                up = axis;
            }
            else
            {
                (right, up) = AutospriteFrame(positions.Slice(q, 4),
                    uvs.Length >= q + 4 ? uvs.Slice(q, 4) : default);
                axis = up; // unused by the autosprite shader; carried for symmetry/tests
            }

            quads[q / 4] = new Quad(center, axis);
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = positions[q + i] - center;
                s[q + i] = Vector3.Dot(v, right);
                t[q + i] = Vector3.Dot(v, up);
            }
        }
        return true;
    }

    /// <summary>
    /// The <c>autosprite</c> tangent frame: the quad's UV-aligned right (s/U) and up (t/V) directions, so
    /// the rebuilt billboard keeps its authored texture orientation (DP feeds its UV-derived
    /// svector/tvector into the view-axis rebuild). Falls back to an edge-based frame when the UVs are
    /// degenerate/absent.
    /// </summary>
    private static (Vector3 Right, Vector3 Up) AutospriteFrame(ReadOnlySpan<Vector3> p, ReadOnlySpan<Vector2> uv)
    {
        Vector3 e1 = p[1] - p[0];
        Vector3 e2 = p[2] - p[0];
        Vector3 normal = Vector3.Cross(e1, e2);

        if (uv.Length == 4)
        {
            // Standard UV-gradient tangents over the first triangle (Mod_BuildTextureVectorsFromNormals).
            Vector2 d1 = uv[1] - uv[0];
            Vector2 d2 = uv[2] - uv[0];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) > 1e-8f)
            {
                float inv = 1f / det;
                Vector3 sdir = (e1 * d2.Y - e2 * d1.Y) * inv;   // direction of increasing U
                Vector3 tdir = (e2 * d1.X - e1 * d2.X) * inv;   // direction of increasing V
                if (sdir.LengthSquared() > 1e-12f && tdir.LengthSquared() > 1e-12f)
                    return (Vector3.Normalize(sdir), Vector3.Normalize(tdir));
            }
        }

        // Degenerate UVs: an arbitrary-but-orthogonal in-plane frame (texture may spin, geometry is right).
        Vector3 right = e1.LengthSquared() > 1e-12f ? Vector3.Normalize(e1) : Vector3.UnitX;
        Vector3 upFb = Vector3.Cross(normal, right);
        Vector3 up = upFb.LengthSquared() > 1e-12f ? Vector3.Normalize(upFb) : Vector3.UnitY;
        return (right, up);
    }

    /// <summary>
    /// The <c>autosprite2</c> frame, DP's way (<c>gl_rmain.c</c> Q3DEFORM_AUTOSPRITE2): of the quad's 6
    /// vertex-pair edges, find the two shortest — biasing edges with differing heights (Quake Z) by
    /// +1/1024 so a perfectly square quad is read as upright — and run the long axis between their
    /// midpoints. Each corner keeps its offset along that axis; only the width component is re-aimed at
    /// the viewer per frame. The baked right is <c>cross(normal, axis)</c>, matching the shader's
    /// <c>cross(axis, toQuad)</c> when the original front face looks at the camera, so the width sign is
    /// stable and view-consistent.
    /// </summary>
    private static (Vector3 Right, Vector3 Axis) Autosprite2Frame(ReadOnlySpan<Vector3> p, Vector3 center)
    {
        // Two shortest edges, DP's exact insertion scan (including the seed-by-index behavior).
        float len0 = 0f, len1 = 0f;
        int a0 = 0, b0 = 0, a1 = 0, b1 = 0;
        for (int i = 0; i < 6; i++)
        {
            Vector3 v1 = p[QuadEdges[i, 0]];
            Vector3 v2 = p[QuadEdges[i, 1]];
            float l = Vector3.DistanceSquared(v1, v2);
            // DP: "this length bias tries to make sense of square polygons, assuming they are meant to
            // be upright" — edges that change height (Quake Z) are nudged longer so the two level edges win.
            if (v1.Z != v2.Z)
                l += 1.0f / 1024.0f;
            if (len0 > l || i == 0)
            {
                (len1, a1, b1) = (len0, a0, b0);
                (len0, a0, b0) = (l, QuadEdges[i, 0], QuadEdges[i, 1]);
            }
            else if (len1 > l || i == 1)
            {
                (len1, a1, b1) = (l, QuadEdges[i, 0], QuadEdges[i, 1]);
            }
        }

        Vector3 start = (p[a0] + p[b0]) * 0.5f;   // midpoint of the shortest edge
        Vector3 end = (p[a1] + p[b1]) * 0.5f;     // midpoint of the second-shortest
        Vector3 axisRaw = end - start;
        Vector3 axis = axisRaw.LengthSquared() > 1e-12f ? Vector3.Normalize(axisRaw) : Vector3.UnitX;

        // Width axis: cross(normal, axis) rather than DP's raw shortest-edge direction — same line, but
        // the sign is pinned to the quad's front face so it agrees with the shader's per-frame
        // rt = cross(axis, toQuad) (both reduce to the same vector when the front face looks at the camera).
        Vector3 normal = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
        Vector3 rightRaw = Vector3.Cross(normal, axis);
        Vector3 right;
        if (rightRaw.LengthSquared() > 1e-12f)
        {
            right = Vector3.Normalize(rightRaw);
        }
        else
        {
            Vector3 edge = p[a0] - p[b0];   // DP's fallback meaning: the shortest edge IS the width
            right = edge.LengthSquared() > 1e-12f ? Vector3.Normalize(edge) : Vector3.UnitY;
        }
        return (right, axis);
    }
}
