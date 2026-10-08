// Port of Base/darkplaces/model_brush.c Mod_MakeCollisionBIH as model_alias.c calls it for an alias
// model ("Always make a BIH for the first frame": userendersurfaces, one BIH_RENDERTRIANGLE leaf per
// triangle of every surface, its box the triangle's bounds grown by one unit) and as Mod_OBJ_Load
// calls it; and of what a leaf carries into a trace (the surface's texture: its supercontents,
// surfaceflags and name).
using System.Numerics;

namespace VortexArena.Engine.Collision;

/// <summary>
/// The collision shape of a model that is not made of brushes - an MD3, IQM or OBJ decoration, a
/// player model under MOVE_HITMODEL: its triangles as DarkPlaces collides with them, and the
/// hierarchy that finds the ones near a move. DarkPlaces keeps no collision brushes for such a model;
/// it builds one from each candidate triangle at trace time (Collision_TraceBrushTriangleFloat, the
/// points snapped to 1/32 there and nowhere else), and so does <see cref="TraceService"/> - which is
/// why this is three numbers a triangle and not a <see cref="Brush"/> a triangle.
///
/// A mesh is a surface, not a volume: a box that straddles a triangle starts solid, one wholly
/// inside a closed crate does not; a line is stopped from the front of a triangle only; a point is
/// never inside (Mod_CollisionBIH_TracePoint_Mesh does nothing).
///
/// Immutable once made, so one instance may be traced from several threads.
/// </summary>
public sealed class CollisionMesh
{
    /// <summary>One surface of the model: what a triangle of it reports when hit. <paramref name="Contents"/>
    /// is the texture's supercontents in <see cref="SuperContents"/> bits; 0 is a surface nothing collides with.</summary>
    public readonly record struct Surface(int Contents, int SurfaceFlags, string? Texture);

    private readonly Vector3[] _vertices;
    private readonly int[] _elements;          // three vertex numbers a triangle
    private readonly int[] _triangleSurface;   // the surface of each triangle
    private readonly Surface[] _surfaces;

    public int TriangleCount => _triangleSurface.Length;
    public int VertexCount => _vertices.Length;
    public CollisionBih Bih { get; }

    /// <summary>
    /// A point at rest is tested against the entity's box instead of the mesh. DarkPlaces gives an
    /// animated alias model no TracePoint function, so Collision_ClipPointToGenericEntity falls back
    /// to Collision_ClipTrace_Point on the body box; a static one gets
    /// Mod_CollisionBIH_TracePoint_Mesh, which finds nothing.
    /// </summary>
    public bool PointUsesBodyBox { get; }

    /// <summary>The bounds of the vertices the triangles use (the model's own box at this pose).</summary>
    public Vector3 Mins { get; }
    public Vector3 Maxs { get; }

    private CollisionMesh(Vector3[] vertices, int[] elements, int[] triangleSurface, Surface[] surfaces, bool pointUsesBodyBox)
    {
        _vertices = vertices;
        _elements = elements;
        _triangleSurface = triangleSurface;
        _surfaces = surfaces;
        PointUsesBodyBox = pointUsesBodyBox;
        int n = triangleSurface.Length;
        Vector3[] mins = new Vector3[n], maxs = new Vector3[n];
        Vector3 lo = default, hi = default;
        for (int t = 0; t < n; t++)
        {
            Vector3 a = vertices[elements[t * 3]], b = vertices[elements[t * 3 + 1]], c = vertices[elements[t * 3 + 2]];
            Vector3 tmin = Vector3.Min(a, Vector3.Min(b, c)), tmax = Vector3.Max(a, Vector3.Max(b, c));
            mins[t] = tmin - Vector3.One;
            maxs[t] = tmax + Vector3.One;
            lo = t == 0 ? tmin : Vector3.Min(lo, tmin);
            hi = t == 0 ? tmax : Vector3.Max(hi, tmax);
        }
        Mins = lo;
        Maxs = hi;
        Bih = CollisionBih.Build(mins, maxs);
    }

    /// <summary>
    /// A mesh from a model's data, which is not trusted: a triangle that names a vertex the model
    /// does not have, or one with a coordinate that is not a finite number, or one of a surface
    /// that does not exist, is left out (DarkPlaces refuses such a file when it loads it). Null if
    /// nothing is left or there are more than <paramref name="maxTriangles"/> triangles - the
    /// caller then clips the entity as its box.
    /// </summary>
    /// <param name="elements">Three vertex numbers a triangle, surface after surface in the model's order.</param>
    /// <param name="triangleSurface">The index into <paramref name="surfaces"/> of each triangle.</param>
    public static CollisionMesh? Create(Vector3[] vertices, ReadOnlySpan<int> elements, ReadOnlySpan<int> triangleSurface,
        Surface[] surfaces, int maxTriangles, bool pointUsesBodyBox = false)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(surfaces);
        int count = Math.Min(elements.Length / 3, triangleSurface.Length);
        if (count <= 0 || count > maxTriangles) return null;
        List<int> keptElements = new(count * 3), keptSurfaces = new(count);
        for (int t = 0; t < count; t++)
        {
            int a = elements[t * 3], b = elements[t * 3 + 1], c = elements[t * 3 + 2], surface = triangleSurface[t];
            if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length || (uint)c >= (uint)vertices.Length) continue;
            if ((uint)surface >= (uint)surfaces.Length) continue;
            if (!IsFinite(vertices[a]) || !IsFinite(vertices[b]) || !IsFinite(vertices[c])) continue;
            keptElements.Add(a);
            keptElements.Add(b);
            keptElements.Add(c);
            keptSurfaces.Add(surface);
        }
        if (keptSurfaces.Count == 0) return null;
        return new CollisionMesh(vertices, keptElements.ToArray(), keptSurfaces.ToArray(), surfaces, pointUsesBodyBox);
    }

    /// <summary>Triangle <paramref name="index"/> (a leaf of <see cref="Bih"/>): its points as the model has them, and its surface.</summary>
    public Surface Triangle(int index, out Vector3 v0, out Vector3 v1, out Vector3 v2)
    {
        int e = index * 3;
        v0 = _vertices[_elements[e]];
        v1 = _vertices[_elements[e + 1]];
        v2 = _vertices[_elements[e + 2]];
        return _surfaces[_triangleSurface[index]];
    }

    /// <summary>A rough count of the bytes this mesh holds, for a cache's budget.</summary>
    public long ApproximateBytes => 12L * _vertices.Length + 16L * _triangleSurface.Length + 24L * 2 * _triangleSurface.Length + 52L * Bih.NodeCount + 4L * _triangleSurface.Length;

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
