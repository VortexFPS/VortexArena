using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// One render surface of a model, in MODEL-LOCAL space — the unit the <c>getsurface*</c> builtins query.
/// A BSP inline model contributes one <see cref="ModelSurface"/> per face; a loaded md3/iqm contributes one
/// per mesh. Triangles are a flat index list (length a multiple of 3) into <see cref="Points"/>.
/// </summary>
public sealed class ModelSurface
{
    public required Vector3[] Points { get; init; }
    public Vector3[]? Normals { get; init; }          // per-vertex (SPA_R_AXIS)
    public Vector2[]? TexCoords { get; init; }         // per-vertex (SPA_TEXCOORDS0)
    public Vector2[]? LightmapCoords { get; init; }    // per-vertex (SPA_LIGHTMAP0_TEXCOORDS)
    public Vector4[]? Colors { get; init; }            // per-vertex RGBA 0..1 (SPA_LIGHTMAP_COLOR)
    public required int[] Triangles { get; init; }
    public string Texture { get; init; } = "";

    /// <summary>The surface's geometric plane normal (model-local), used by <c>getsurfacenormal</c>.</summary>
    public Vector3 PlaneNormal { get; init; }
}

/// <summary>
/// The <c>getsurface*</c> mesh-query builtins (DP <c>VM_getsurface*</c>). Resolves an entity's model to its
/// registered <see cref="ModelSurface"/> list (via <see cref="ModelService"/>) and returns per-surface
/// geometry in WORLD space — the entity's current origin+angles applied (Quake FLU entity matrix, scale 1).
///
/// Ground truth: DarkPlaces <c>prvm_cmds.c</c> (<c>VM_getsurfacenumpoints</c> … <c>VM_getsurfacetriangle</c>)
/// and <c>model_brush.c</c>/<c>model_alias.c</c> surface layout. Consumed by <c>lib/warpzone</c>'s
/// brush→plane auto-derivation (<see cref="XonoticGodot.Common.Gameplay.WarpzoneManager"/>) and surface/decal code.
/// </summary>
public sealed class SurfaceService : ISurfaceService
{
    private readonly ModelService _models;
    public SurfaceService(ModelService models) => _models = models;

    private IReadOnlyList<ModelSurface>? SurfacesOf(Entity e)
        => _models.TryGetModel(e.Model, out ModelService.ModelDef def) ? def.Surfaces : null;

    private static ModelSurface? Surface(IReadOnlyList<ModelSurface>? surfaces, int index)
        => surfaces is not null && index >= 0 && index < surfaces.Count ? surfaces[index] : null;

    // --- entity transform (Quake Matrix4x4_CreateFromQuakeEntity, scale 1): world = origin + FLU·local ---
    private static void Basis(Entity e, out Vector3 fwd, out Vector3 left, out Vector3 up)
    {
        QMath.AngleVectors(e.Angles, out fwd, out Vector3 right, out up);
        left = -right; // DP's entity matrix uses forward/left/up
    }
    private static Vector3 ToWorld(Entity e, Vector3 local)
    {
        Basis(e, out Vector3 f, out Vector3 l, out Vector3 u);
        return e.Origin + f * local.X + l * local.Y + u * local.Z;
    }
    private static Vector3 DirToWorld(Entity e, Vector3 local)
    {
        Basis(e, out Vector3 f, out Vector3 l, out Vector3 u);
        return f * local.X + l * local.Y + u * local.Z;
    }

    public int GetSurfaceNumPoints(Entity e, int surface)
        => Surface(SurfacesOf(e), surface)?.Points.Length ?? 0;

    public Vector3 GetSurfacePoint(Entity e, int surface, int point)
    {
        ModelSurface? s = Surface(SurfacesOf(e), surface);
        if (s is null || point < 0 || point >= s.Points.Length) return Vector3.Zero;
        return ToWorld(e, s.Points[point]);
    }

    public Vector3 GetSurfaceNormal(Entity e, int surface)
    {
        ModelSurface? s = Surface(SurfacesOf(e), surface);
        if (s is null) return Vector3.Zero;
        return QMath.Normalize(DirToWorld(e, s.PlaneNormal));
    }

    public string GetSurfaceTexture(Entity e, int surface)
        => Surface(SurfacesOf(e), surface)?.Texture ?? "";

    public int GetSurfaceNumTriangles(Entity e, int surface)
    {
        ModelSurface? s = Surface(SurfacesOf(e), surface);
        return s is null ? 0 : s.Triangles.Length / 3;
    }

    public Vector3 GetSurfaceTriangle(Entity e, int surface, int triangle)
    {
        ModelSurface? s = Surface(SurfacesOf(e), surface);
        if (s is null || triangle < 0 || triangle * 3 + 2 >= s.Triangles.Length) return Vector3.Zero;
        int b = triangle * 3;
        return new Vector3(s.Triangles[b], s.Triangles[b + 1], s.Triangles[b + 2]);
    }

    public int GetSurfaceNearPoint(Entity e, Vector3 point)
    {
        IReadOnlyList<ModelSurface>? surfaces = SurfacesOf(e);
        if (surfaces is null) return -1;
        int best = -1;
        float bestDistSq = float.MaxValue;
        for (int si = 0; si < surfaces.Count; si++)
        {
            ModelSurface s = surfaces[si];
            for (int p = 0; p < s.Points.Length; p++)
            {
                float d = (ToWorld(e, s.Points[p]) - point).LengthSquared();
                if (d < bestDistSq) { bestDistSq = d; best = si; }
            }
        }
        return best;
    }

    public Vector3 GetSurfaceClippedPoint(Entity e, int surface, Vector3 point)
    {
        ModelSurface? s = Surface(SurfacesOf(e), surface);
        if (s is null || s.Triangles.Length < 3) return point;
        Vector3 best = point;
        float bestDistSq = float.MaxValue;
        for (int t = 0; t + 2 < s.Triangles.Length; t += 3)
        {
            Vector3 a = ToWorld(e, s.Points[s.Triangles[t]]);
            Vector3 b = ToWorld(e, s.Points[s.Triangles[t + 1]]);
            Vector3 c = ToWorld(e, s.Points[s.Triangles[t + 2]]);
            Vector3 cp = ClosestOnTriangle(point, a, b, c);
            float d = (cp - point).LengthSquared();
            if (d < bestDistSq) { bestDistSq = d; best = cp; }
        }
        return best;
    }

    public Vector3 GetSurfacePointAttribute(Entity e, int surface, int point, int attribute)
    {
        ModelSurface? s = Surface(SurfacesOf(e), surface);
        if (s is null || point < 0 || point >= s.Points.Length) return Vector3.Zero;
        switch ((SurfaceAttribute)attribute)
        {
            case SurfaceAttribute.Position:
                return ToWorld(e, s.Points[point]);
            case SurfaceAttribute.Normal:
                return s.Normals is { } nn && point < nn.Length ? QMath.Normalize(DirToWorld(e, nn[point])) : GetSurfaceNormal(e, surface);
            case SurfaceAttribute.TexCoords:
                return s.TexCoords is { } tc && point < tc.Length ? new Vector3(tc[point].X, tc[point].Y, 0f) : Vector3.Zero;
            case SurfaceAttribute.LightmapTexCoords:
                return s.LightmapCoords is { } lc && point < lc.Length ? new Vector3(lc[point].X, lc[point].Y, 0f) : Vector3.Zero;
            case SurfaceAttribute.LightmapColor:
                return s.Colors is { } col && point < col.Length ? new Vector3(col[point].X, col[point].Y, col[point].Z) : Vector3.Zero;
            // S/T axes (tangent/bitangent) aren't stored for BSP; derive a stable tangent frame from the normal.
            case SurfaceAttribute.SAxis:
            case SurfaceAttribute.TAxis:
            {
                Vector3 n = GetSurfaceNormal(e, surface);
                Vector3 t = MathF.Abs(n.Z) < 0.99f ? Vector3.Cross(n, new Vector3(0, 0, 1)) : Vector3.Cross(n, new Vector3(1, 0, 0));
                t = QMath.Normalize(t);
                return (SurfaceAttribute)attribute == SurfaceAttribute.SAxis ? t : QMath.Normalize(Vector3.Cross(n, t));
            }
            default:
                return Vector3.Zero;
        }
    }

    /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection).</summary>
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
