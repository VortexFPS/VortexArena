using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Cheap projected shadows under models — the port of DarkPlaces' <c>r_shadows</c>, here called
/// <c>r_fakeshadows</c> (<b>F5</b>).
///
/// <para><b>Why the rename.</b> DP's cvar is <c>r_shadows</c>, which reads like the master shadow switch and
/// is not one: it controls a completely separate, much cheaper feature that draws a model's shadow onto the
/// world without any shadow map, and explicitly does <i>not</i> affect rtlights. Calling it
/// <c>r_fakeshadows</c> says what it is. The DP tunables keep their meanings under matching names
/// (<c>_darken</c> = <c>r_shadows_darken</c> 0.5, <c>_throwdistance</c> = <c>r_shadows_throwdistance</c> 500).</para>
///
/// <para><b>What it does.</b> For each model that should be grounded, trace from its origin along the throw
/// direction, and drop a soft dark ellipse on whatever it hits. DP's version projects the model's real
/// silhouette; this projects a blob. That is a deliberate downgrade: the silhouette version needs a render
/// pass per caster, and the entire point of this feature is to be the cheapest possible way to stop players
/// and items looking like they are hovering. On a fast arena shooter at 1/2 ms a frame, a blob that is in the
/// right place, the right size and the right darkness reads as a shadow.</para>
///
/// <para><b>Modes</b>, mirroring DP: <c>1</c> throws along the model's own light direction — here the baked
/// light direction from the map's light grid, which is exactly what DP means by "the model lighting" and
/// which <see cref="ModelLighting"/> already has. <c>2</c> throws straight down
/// (DP's <c>r_shadows_throwdirection</c> default <c>0 0 -1</c>), which is steadier in a map whose grid
/// direction swings between cells. <c>0</c> is off, and is DP's default too.</para>
///
/// <para><b>Cost.</b> One world trace and one transform per visible caster per frame, plus one alpha-blended
/// quad each. Casters are capped (<see cref="MaxShadows"/>) and ranked by distance, so a crowded map degrades
/// by dropping the furthest blobs rather than by getting slower.</para>
/// </summary>
public sealed partial class FakeShadowRenderer : Node3D
{
    /// <summary>Hard cap on simultaneous blobs. Beyond this the nearest N win.</summary>
    private const int MaxShadows = 24;

    /// <summary>Blob radius as a multiple of the caster's bounding radius.</summary>
    private const float RadiusScale = 1.15f;

    /// <summary>Lift off the surface, to stay out of z-fighting with the floor.</summary>
    private const float SurfaceOffset = 1.5f;

    private readonly List<MeshInstance3D> _pool = new();
    private QuadMesh? _quad;

    private sealed class Caster
    {
        public NVec3 Origin;
        public float Radius;
        public float Height;
        public float Dist;
        public float Rank;
    }

    /// <summary>
    /// How much this caster blob is worth drawing. Nearest-first is the obvious ordering and the wrong one:
    /// in first person the nearest caster is almost always YOU, and the next few are whatever you are
    /// standing among - so a nearest-first cap spends the entire budget on blobs directly under the camera,
    /// where the weapon and the HUD cover them, while every item you can actually SEE goes ungrounded.
    /// Ranking by "in front of me, and close" fixes that: behind-camera casters sink down the list and only
    /// get a blob when the budget is not otherwise spent.
    /// </summary>
    private static float RankOf(NVec3 origin, NVec3 eye, NVec3 fwd)
    {
        NVec3 to = origin - eye;
        float dist = to.Length();
        if (dist < 1f)
            return 1000f;                              // you: always worth one blob
        float facing = NVec3.Dot(to / dist, fwd);      // 1 = dead ahead, -1 = behind
        float infront = MathF.Max(0.05f, (facing + 1f) * 0.5f);
        return infront * 4096f / MathF.Max(64f, dist);
    }

    private readonly List<Caster> _casters = new();

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("fakeshadows");

        int mode = (int)Cvar("r_fakeshadows", 0f);
        if (mode <= 0 || Api.Services is null)
        {
            HideAll();
            return;
        }

        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null || !GodotObject.IsInstanceValid(cam))
        {
            HideAll();
            return;
        }
        NVec3 eye = Coords.ToQuake(cam.GlobalPosition);

        float throwDist = MathF.Max(1f, Cvar("r_fakeshadows_throwdistance", 500f));
        // -Z is Godot forward; Coords maps a DIRECTION the same way it maps a position.
        NVec3 fwd = Coords.ToQuake(-cam.GlobalTransform.Basis.Z);
        CollectCasters(eye, fwd, throwDist);
        float darken = Math.Clamp(Cvar("r_fakeshadows_darken", 0.5f), 0f, 1f);
        float sizeScale = MathF.Max(0.05f, Cvar("r_fakeshadows_size", 1f));

        int used = 0;
        foreach (Caster c in _casters)
        {
            if (used >= MaxShadows)
                break;

            // Trace from just inside the caster's feet so a model standing ON the floor still hits it.
            NVec3 dir = ThrowDirection(mode, c.Origin);
            NVec3 start = c.Origin + new NVec3(0f, 0f, MathF.Max(2f, c.Height * 0.25f));
            TraceResult tr = Api.Trace.Trace(start, NVec3.Zero, NVec3.Zero,
                start + dir * throwDist, MoveFilter.WorldOnly, null);
            if (tr.Fraction >= 1f)
                continue;   // nothing under it within throw distance — no shadow, which is correct over a pit

            // Fade with throw distance, the way a real penumbra washes out: a player mid-jump casts a
            // fainter, larger blob than one standing on the floor.
            float travelled = throwDist * tr.Fraction;
            float fade = 1f - Math.Clamp(travelled / throwDist, 0f, 1f);
            float alpha = darken * fade;
            if (alpha <= 0.01f)
                continue;

            MeshInstance3D quad = Acquire(used++);
            PlaceOnSurface(quad, tr.EndPos, tr.PlaneNormal,
                c.Radius * RadiusScale * sizeScale * (1f + (1f - fade) * 0.6f), alpha);
        }

        for (int i = used; i < _pool.Count; i++)
            _pool[i].Visible = false;
    }

    // =================================================================================================
    //  Casters
    // =================================================================================================

    /// <summary>
    /// Collect what should cast, nearest first. Players and pickups are the two classes that read as
    /// "hovering" without a ground shadow; brush entities and gibs are deliberately excluded - a gib blob is
    /// noise, and a moving platform blob would be wrong the moment it is not over floor.
    ///
    /// <para>Sourced from a radius query rather than a classname scan: it is spatially bounded (so a big map
    /// costs no more than a small one) and it naturally drops everything too far away to be worth a blob.</para>
    /// </summary>
    private void CollectCasters(NVec3 eye, NVec3 fwd, float throwDist)
    {
        _casters.Clear();
        float radius = MathF.Max(1024f, throwDist * 4f);
        Api.Entities.FindInRadius(eye, radius, _scratch);
        foreach (Entity e in _scratch)
        {
            if (e.IsFreed || !IsCaster(e.ClassName))
                continue;
            NVec3 size = e.Maxs - e.Mins;
            if (size.X <= 0f || size.Z <= 0f)
                continue;
            _casters.Add(new Caster
            {
                Origin = e.Origin + new NVec3(0f, 0f, e.Mins.Z),
                Radius = MathF.Max(4f, MathF.Max(size.X, size.Y) * 0.5f),
                Height = size.Z,
                Dist = NVec3.Distance(e.Origin, eye),
                Rank = RankOf(e.Origin, eye, fwd),
            });
        }
        _casters.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));
    }

    private readonly List<Entity> _scratch = new();

    /// <summary>Players and pickups ground; everything else does not (see CollectCasters).</summary>
    private static bool IsCaster(string cn) =>
        cn.StartsWith("player", StringComparison.OrdinalIgnoreCase)
        || cn.StartsWith("item_", StringComparison.OrdinalIgnoreCase)
        || cn.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The throw direction for a caster, in Quake axes. Mode 2 is straight down (DP
    /// <c>r_shadows_throwdirection</c> default <c>0 0 -1</c>). Mode 1 throws AWAY from the map baked light
    /// direction - DP "use the model lighting", and the light grid is exactly where this port keeps that. A
    /// cell with no baked direction (or no grid at all) falls back to down rather than to nothing, and a
    /// direction that would throw the shadow UPWARD is rejected for the same reason.
    /// </summary>
    private NVec3 ThrowDirection(int mode, NVec3 at)
    {
        NVec3 down = new(0f, 0f, -1f);
        if (mode >= 2 || Grid is null)
            return down;
        Grid.Sample(at, out _, out _, out NVec3 dir);
        if (dir.LengthSquared() < 0.25f)
            return down;
        NVec3 thrown = -NVec3.Normalize(dir);
        return thrown.Z > -0.15f ? down : thrown;
    }

    /// <summary>The map light grid, for mode 1 throw direction. Null = mode 1 behaves as mode 2.</summary>
    public VortexArena.Formats.Bsp.LightGridData? Grid { get; set; }

    // =================================================================================================
    //  Quad pool
    // =================================================================================================

    private MeshInstance3D Acquire(int index)
    {
        while (_pool.Count <= index)
        {
            var mi = new MeshInstance3D
            {
                Name = $"fakeshadow{_pool.Count}",
                Mesh = SharedQuad(),
                // Its own material instance so per-blob alpha can ride an instance uniform without unsharing
                // the mesh: opacity rides an instance uniform instead (see AlphaUniform).
                MaterialOverride = SharedMaterial(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(mi);
            _pool.Add(mi);
        }
        return _pool[index];
    }

    /// <summary>Lay the quad flat on the hit surface, sized and faded.</summary>
    private static void PlaceOnSurface(MeshInstance3D quad, NVec3 hit, NVec3 normal, float radius, float alpha)
    {
        Vector3 n = Coords.ToGodot(normal).Normalized();
        if (n.LengthSquared() < 0.5f)
            n = Vector3.Up;

        Vector3 pos = Coords.ToGodot(hit) + n * SurfaceOffset;
        // A quad's own normal is +Z, so build a basis whose Z is the surface normal.
        Vector3 up = MathF.Abs(n.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        Vector3 x = up.Cross(n).Normalized();
        Vector3 y = n.Cross(x).Normalized();
        quad.GlobalTransform = new Transform3D(new Basis(x, y, n).Scaled(new Vector3(radius, radius, 1f)), pos);

        quad.SetInstanceShaderParameter(AlphaUniform, alpha);
        quad.Visible = true;
    }

    private QuadMesh SharedQuad() => _quad ??= new QuadMesh { Size = new Vector2(2f, 2f) };

    /// <summary>Instance uniform: this blob opacity. An instance uniform rather than a per-quad material so
    /// every pool member shares ONE material - duplicating a material per member cost a 14 ms first-frame
    /// hitch (24 Resource.Duplicate calls plus 24 fresh material bindings) and bought nothing.</summary>
    private static readonly StringName AlphaUniform = "blob_alpha";

    private static Shader? _shader;
    private ShaderMaterial? _sharedMat;

    /// <summary>
    /// The blob material: a radial alpha falloff, alpha-blended, unshaded, depth-TEST on but depth-WRITE off
    /// so overlapping blobs do not punch holes in each other. A shader rather than a StandardMaterial3D
    /// purely so opacity can be an instance uniform.
    /// </summary>
    private ShaderMaterial SharedMaterial()
    {
        if (_sharedMat is not null)
            return _sharedMat;
        _shader ??= new Shader { Code = BlobShaderCode };
        var mat = new ShaderMaterial { Shader = _shader };
        mat.SetShaderParameter("blob_tex", BlobTexture());
        return _sharedMat = mat;
    }

    private const string BlobShaderCode = @"// VortexArena fake-shadow blob (r_fakeshadows). Generated in C#.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;
uniform sampler2D blob_tex : hint_default_white, filter_linear;
instance uniform float blob_alpha = 0.5;
void fragment() {
    ALBEDO = vec3(0.0);
    ALPHA = texture(blob_tex, UV).a * blob_alpha;
}
";

    /// <summary>
    /// A 64×64 radial gradient: opaque white at the centre falling to transparent at the rim, with a
    /// smoothstep so the edge does not band. Generated rather than shipped so there is no asset to lose.
    /// </summary>
    private static ImageTexture BlobTexture()
    {
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f;
                float dy = (y + 0.5f) / n * 2f - 1f;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float a = Math.Clamp(1f - d, 0f, 1f);
                a = a * a * (3f - 2f * a);   // smoothstep
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    private void HideAll()
    {
        foreach (MeshInstance3D mi in _pool)
            if (GodotObject.IsInstanceValid(mi))
                mi.Visible = false;
    }

    private static float Cvar(string name, float fallback)
    {
        string s = Menu.MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Menu.MenuState.Cvars.GetFloat(name);
    }
}
