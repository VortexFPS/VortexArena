using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Materials;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// DarkPlaces' reflective and refractive water (<c>r_water 1</c>; gl_rmain.c R_Water_*), for the native game and
/// for legacy mode: the pass behind a Quake 3 shader's <c>dp_water</c> line.
///
/// <para><b>What DarkPlaces does.</b> For every visible plane of water it renders the scene twice at a reduced
/// size (<c>r_water_resolutionmultiplier</c>): mirrored in the plane (the reflection) and clipped to the far
/// side of it (the refraction). The surface then shows <c>mix(refraction * refractcolor, reflection *
/// reflectcolor, Fresnel)</c>, both looked up at a screen position shifted by the normal map, with the ordinary
/// material drawn over it at the shader's water alpha. With <c>r_water 0</c> - Xonotic's default below the
/// "high" preset; its menu calls the setting "Reflections" - none of that exists and the surface is the
/// ordinary blended material, which is what <see cref="Loaders.DpSurfaceShader"/> draws.</para>
///
/// <para><b>What this does.</b> The refraction is the frame already drawn behind the surface (the engine's
/// screen texture): no second render. The reflection is one render per plane from the eye mirrored in the
/// plane, into a texture of the view's size times <c>r_water_resolutionmultiplier</c>. This engine's cameras
/// have no oblique clip plane, so the reflection camera looks straight along the plane's normal with its near
/// plane ON the water plane (pulled back by <c>r_water_clippingplanebias</c>) and an off-centre frustum that
/// covers the visible part of the water; the surface's shader finds its texel by projecting its own world
/// position with that camera's matrix. Geometry under the water is therefore clipped exactly.</para>
///
/// <para><b>Cost is bounded:</b> nothing is rendered unless <c>r_water</c> is on AND a water plane faces the
/// eye inside the view; at most <see cref="MaxRendered"/> planes are rendered in a frame (the nearest; DarkPlaces
/// allows 16); the others, and every plane while the pass is off, are the plain material.</para>
///
/// <para><b>What differs from DarkPlaces:</b> the reflection's texels are spread over the visible water rather
/// than over the screen, so a distant shore is sharper and the water at one's feet softer than there; the
/// reflection's distortion is in that texture's units; the refraction can pick up something in front of the
/// surface near its silhouette (DarkPlaces clips it away); the local player's own body is not in a reflection
/// (the native client does not draw it at all); water on a brush model (a moving pool) keeps the plain look.</para>
/// </summary>
public sealed partial class WaterRenderer : Node
{
    /// <summary>How many planes are rendered in one frame at most.</summary>
    public const int MaxRendered = 2;

    /// <summary>Where the pass reads its cvars; the shared client store by default, a legacy session's own
    /// (Xonotic's client program forces r_water on levels with warpzones there).</summary>
    public Func<string, float, float>? CvarSource { get; set; }

    // ---- the materials of dp_water shaders (filled by DpSurfaceShader as levels load) -------------------------

    private sealed class WaterMaterial
    {
        public WeakReference<ShaderMaterial> Material = null!;
        public Shader Plain = null!, Water = null!;
        public DpWater Def = null!;
        /// <summary>A copy this pass made for one plane (each plane has its own reflection).</summary>
        public bool PlaneOwned;
    }

    private static readonly List<WaterMaterial> s_materials = new();

    /// <summary>Called for every material built from a <c>dp_water</c> shader (any thread).</summary>
    public static void Register(ShaderMaterial material, Shader plain, Shader water, DpWater def)
    {
        lock (s_materials)
            s_materials.Add(new WaterMaterial { Material = new WeakReference<ShaderMaterial>(material), Plain = plain, Water = water, Def = def });
    }

    private static WaterMaterial? Find(Material? material)
    {
        if (material is not ShaderMaterial shaded) return null;
        lock (s_materials)
        {
            for (int i = s_materials.Count - 1; i >= 0; i--)
            {
                if (!s_materials[i].Material.TryGetTarget(out ShaderMaterial? known)) { s_materials.RemoveAt(i); continue; }
                if (ReferenceEquals(known, shaded)) return s_materials[i];
            }
        }
        return null;
    }

    // ---- the planes of the level in view ----------------------------------------------------------------------

    private sealed class Plane
    {
        public Vector3 Normal;       // unit, towards the side the water is seen from
        public float Dist;           // dot(Normal, p) on the plane
        public Aabb Bounds;
        public Vector3 NormalSum;
        public readonly List<(ShaderMaterial Material, WaterMaterial Source)> Materials = new();
        public SubViewport? Viewport;
        public Camera3D? Camera;
        public bool Rendering;
        public float Distance;
    }

    private readonly List<Plane> _planes = new();
    private int _scannedGeneration = -1, _seenGeneration = -1;
    private bool _applied;
    private Vector2I _size;

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("water");

        // The pass needs a buffer of display values: the scene behind the surface is read as it is.
        bool on = DisplayFramebuffer.Active && Cvar("r_water", 0f) != 0f;
        if (!on && !_applied) return;

        // A level's cells arrive over several frames: the scan waits for a frame in which none was added.
        int generation = ShadowSettings.WorldCellGeneration;
        if (generation != _scannedGeneration && generation == _seenGeneration) Scan();
        _seenGeneration = generation;
        if (_planes.Count == 0) { _applied = on; return; }

        if (on != _applied)
        {
            _applied = on;
            foreach (Plane plane in _planes)
            {
                foreach ((ShaderMaterial material, WaterMaterial source) in plane.Materials)
                    material.Shader = on ? source.Water : source.Plain;
                if (!on) Stop(plane);
            }
        }
        if (!on) return;

        Viewport? mainViewport = GetViewport();
        Camera3D? eye = mainViewport?.GetCamera3D();
        if (mainViewport is null || eye is null || !GodotObject.IsInstanceValid(eye)) return;

        Vector2 view = mainViewport.GetVisibleRect().Size;
        (int width, int height) = DpWaterModel.TextureSize((int)view.X, (int)view.Y, Cvar("r_water_resolutionmultiplier", 0.5f));
        Vector2I size = new(width, height);
        float refract = Cvar("r_water_refractdistort", DpWaterModel.XonoticRefractDistort);
        float reflect = Cvar("r_water_reflectdistort", DpWaterModel.DefaultReflectDistort);
        float bias = MathF.Max(0f, Cvar("r_water_clippingplanebias", 1f));

        Vector3 origin = eye.GlobalPosition;
        Godot.Collections.Array<Godot.Plane> frustum = eye.GetFrustum();
        Vector3 inside = origin - eye.GlobalBasis.Z * (eye.Near + 1f);

        // Which planes face the eye inside the view, nearest first.
        int candidates = 0;
        foreach (Plane plane in _planes)
        {
            float above = plane.Normal.Dot(origin) - plane.Dist;
            plane.Distance = float.MaxValue;
            if (above > bias + 0.5f && InView(plane.Bounds, frustum, inside))
            {
                plane.Distance = DistanceTo(plane.Bounds, origin);
                candidates++;
            }
        }
        if (candidates > 1) _planes.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        int rendered = 0;
        foreach (Plane plane in _planes)
        {
            bool render = plane.Distance < float.MaxValue && rendered < MaxRendered && Aim(plane, eye, frustum, inside, origin, bias, size, mainViewport);
            if (render) rendered++;
            else Stop(plane);
            foreach ((ShaderMaterial material, WaterMaterial source) in plane.Materials)
            {
                material.SetShaderParameter("water_on", render ? 1f : 0f);
                if (!render) continue;
                (float refractScale, float reflectScale) = DpWaterModel.Distort(refract, reflect, source.Def.RefractFactor, source.Def.ReflectFactor);
                material.SetShaderParameter("water_distort", new Vector2(refractScale, reflectScale));
                material.SetShaderParameter("reflection_tex", plane.Viewport!.GetTexture());
                material.SetShaderParameter("reflection_vp", _matrix);
            }
        }
        _size = size;
    }

    private Projection _matrix;

    // Points the plane's camera: from the mirrored eye, along the normal, near plane on the water, the frustum
    // window around the part of the water that is in view. False when there is nothing to render.
    private bool Aim(Plane plane, Camera3D eye, Godot.Collections.Array<Godot.Plane> frustum, Vector3 inside, Vector3 origin, float bias, Vector2I size, Viewport mainViewport)
    {
        System.Numerics.Vector3 mirrored = DpWaterModel.MirrorPoint(new System.Numerics.Vector3(origin.X, origin.Y, origin.Z),
            new System.Numerics.Vector3(plane.Normal.X, plane.Normal.Y, plane.Normal.Z), plane.Dist);
        Vector3 from = new(mirrored.X, mirrored.Y, mirrored.Z);
        float height = plane.Dist - plane.Normal.Dot(from);            // the mirrored eye's distance to the plane
        float near = height - bias;                                    // R_SetupView: the clip plane, moved back by the bias
        if (near < 0.25f) return false;

        // An orthonormal frame with -Z along the normal (a camera looks down its -Z axis).
        Vector3 z = -plane.Normal;
        Vector3 x = MathF.Abs(plane.Normal.Y) > 0.9f ? Vector3.Right : Vector3.Up.Cross(z).Normalized();
        x = (x - z * x.Dot(z)).Normalized();
        Vector3 y = z.Cross(x);

        // The water's bounds, flattened onto the plane and cut by the view: what the reflection has to cover.
        Vector3 centre = plane.Bounds.GetCenter();
        centre -= plane.Normal * (plane.Normal.Dot(centre) - plane.Dist);
        Vector3 extent = plane.Bounds.Size * 0.5f;
        float halfX = MathF.Abs(x.X) * extent.X + MathF.Abs(x.Y) * extent.Y + MathF.Abs(x.Z) * extent.Z;
        float halfY = MathF.Abs(y.X) * extent.X + MathF.Abs(y.Y) * extent.Y + MathF.Abs(y.Z) * extent.Z;
        _polygon.Clear();
        _polygon.Add(centre - x * halfX - y * halfY);
        _polygon.Add(centre + x * halfX - y * halfY);
        _polygon.Add(centre + x * halfX + y * halfY);
        _polygon.Add(centre - x * halfX + y * halfY);
        foreach (Godot.Plane side in frustum)
        {
            Clip(_polygon, _scratch, side, side.DistanceTo(inside) <= 0f);
            (_polygon, _scratch) = (_scratch, _polygon);
            if (_polygon.Count < 3) return false;
        }
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector3 point in _polygon)
        {
            Vector3 d = point - from;
            float px = d.Dot(x), py = d.Dot(y);
            minX = MathF.Min(minX, px); maxX = MathF.Max(maxX, px);
            minY = MathF.Min(minY, py); maxY = MathF.Max(maxY, py);
        }
        // A margin for the distortion's reach, then the window at the near plane (similar triangles).
        float margin = 0.04f * MathF.Max(maxX - minX, maxY - minY) + 8f;
        minX -= margin; maxX += margin; minY -= margin; maxY += margin;
        float scale = near / height;
        float windowWidth = (maxX - minX) * scale, windowHeight = (maxY - minY) * scale;
        float aspect = size.X / (float)size.Y;
        if (windowWidth / windowHeight > aspect) windowHeight = windowWidth / aspect; else windowWidth = windowHeight * aspect;
        Vector2 offset = new((minX + maxX) * 0.5f * scale, (minY + maxY) * 0.5f * scale);

        if (plane.Viewport is null || !GodotObject.IsInstanceValid(plane.Viewport))
        {
            plane.Viewport = new SubViewport
            {
                Name = "WaterReflection",
                Size = size,
                World3D = mainViewport.World3D,
                Msaa3D = Viewport.Msaa.Disabled,
                UseOcclusionCulling = false,      // the camera sits under the water, inside the level's solid
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            };
            plane.Camera = new Camera3D { Name = "WaterCamera", Current = true };
            plane.Viewport.AddChild(plane.Camera);
            AddChild(plane.Viewport);
        }
        if (plane.Viewport.Size != size && size != _size) plane.Viewport.Size = size;   // a setting changed: rare
        Camera3D camera = plane.Camera!;
        // A water render draws neither water (its shader tests the bit) nor the first-person weapon.
        camera.CullMask = eye.CullMask & ~(DpSurfaceShaderGen.WaterRenderSkipBit | ViewModelRenderFx.RenderLayerBit);
        camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
        camera.SetFrustum(windowHeight, offset, near, MathF.Max(eye.Far, near + 16f));
        camera.GlobalTransform = new Transform3D(new Basis(x, y, z), from);
        _matrix = camera.GetCameraProjection() * new Projection(camera.GlobalTransform.AffineInverse());
        if (!plane.Rendering)
        {
            plane.Rendering = true;
            plane.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
        return true;
    }

    private List<Vector3> _polygon = new(12), _scratch = new(12);

    private static void Stop(Plane plane)
    {
        if (!plane.Rendering) return;
        plane.Rendering = false;
        if (plane.Viewport is not null && GodotObject.IsInstanceValid(plane.Viewport))
            plane.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    // Sutherland-Hodgman against one plane; "keepNegative" says which side of it is inside the view.
    private static void Clip(List<Vector3> polygon, List<Vector3> result, Godot.Plane side, bool keepNegative)
    {
        result.Clear();
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector3 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            float da = side.DistanceTo(a), db = side.DistanceTo(b);
            if (!keepNegative) { da = -da; db = -db; }
            if (da <= 0f) result.Add(a);
            if ((da < 0f && db > 0f) || (da > 0f && db < 0f)) result.Add(a + (b - a) * (da / (da - db)));
        }
    }

    private static bool InView(Aabb bounds, Godot.Collections.Array<Godot.Plane> frustum, Vector3 inside)
    {
        foreach (Godot.Plane side in frustum)
        {
            bool keepNegative = side.DistanceTo(inside) <= 0f;
            bool any = false;
            for (int i = 0; i < 8 && !any; i++)
            {
                float d = side.DistanceTo(bounds.GetEndpoint(i));
                any = keepNegative ? d <= 0f : d >= 0f;
            }
            if (!any) return false;
        }
        return true;
    }

    private static float DistanceTo(Aabb bounds, Vector3 point)
    {
        Vector3 nearest = point.Clamp(bounds.Position, bounds.End);
        return nearest.DistanceTo(point);
    }

    // ---- finding the water of a level: once per level, from the cell meshes the map loader built -------------

    private void Scan()
    {
        _scannedGeneration = ShadowSettings.WorldCellGeneration;
        foreach (Plane plane in _planes)
            if (plane.Viewport is not null && GodotObject.IsInstanceValid(plane.Viewport)) plane.Viewport.QueueFree();
        _planes.Clear();
        _applied = false;

        foreach (GeometryInstance3D cell in ShadowSettings.WorldCells())
        {
            if (cell is not MeshInstance3D { Mesh: ArrayMesh mesh }) continue;
            for (int surface = 0, count = mesh.GetSurfaceCount(); surface < count; surface++)
            {
                WaterMaterial? source = Find(mesh.SurfaceGetMaterial(surface));
                if (source is null) continue;
                Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
                Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                if (vertices.Length == 0 || normals.Length != vertices.Length) continue;
                // R_Water_AddWaterPlane: the normal is the sum of the vertex normals, the distance that of the
                // bounds' centre.
                Vector3 sum = Vector3.Zero;
                Aabb bounds = new(vertices[0], Vector3.Zero);
                for (int i = 0; i < vertices.Length; i++) { sum += normals[i]; bounds = bounds.Expand(vertices[i]); }
                if (sum.LengthSquared() < 1e-6f) continue;
                Vector3 normal = sum.Normalized();
                float dist = normal.Dot(bounds.GetCenter());
                Plane? found = null;
                foreach (Plane known in _planes)
                {
                    if (!DpWaterModel.SamePlane(new System.Numerics.Vector3(normal.X, normal.Y, normal.Z), dist,
                            new System.Numerics.Vector3(known.Normal.X, known.Normal.Y, known.Normal.Z), known.Dist)) continue;
                    found = known;
                    break;
                }
                if (found is null)
                {
                    if (_planes.Count >= DpWaterModel.MaxPlanes) continue;   // the surface keeps the plain material
                    found = new Plane { Normal = normal, Dist = dist, Bounds = bounds };
                    _planes.Add(found);
                }
                else found.Bounds = found.Bounds.Merge(bounds);
                // One material per plane: each plane has its own reflection. A surface an earlier scan already
                // gave a plane's copy keeps that copy.
                ShaderMaterial current = (ShaderMaterial)mesh.SurfaceGetMaterial(surface);
                if (source.PlaneOwned)
                {
                    bool listed = false;
                    foreach ((ShaderMaterial material, WaterMaterial _) in found.Materials) listed |= ReferenceEquals(material, current);
                    if (!listed) { current.Shader = source.Plain; found.Materials.Add((current, source)); }
                    continue;
                }
                ShaderMaterial? own = null;
                foreach ((ShaderMaterial material, WaterMaterial from) in found.Materials)
                    if (ReferenceEquals(from.Def, source.Def) && ReferenceEquals(from.Water, source.Water)) { own = material; break; }
                if (own is null)
                {
                    own = (ShaderMaterial)current.Duplicate();
                    own.Shader = source.Plain;
                    WaterMaterial copy = new() { Material = new WeakReference<ShaderMaterial>(own), Plain = source.Plain, Water = source.Water, Def = source.Def, PlaneOwned = true };
                    lock (s_materials) s_materials.Add(copy);
                    found.Materials.Add((own, copy));
                }
                mesh.SurfaceSetMaterial(surface, own);
            }
        }
    }

    private float Cvar(string name, float fallback)
    {
        if (CvarSource is { } source) return source(name, fallback);
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
