using Godot;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>One light as the baker sees it: no Godot node, just the physics.</summary>
public readonly struct BakedLight
{
    public BakedLight(NVec3 position, Color color, float energy, float range, float radius = 0f)
    {
        Position = position;
        Color = color;
        Energy = energy;
        Range = range;
        Radius = radius;
    }

    /// <summary>Quake space, matching the geometry the baker walks.</summary>
    public NVec3 Position { get; }

    public Color Color { get; }
    public float Energy { get; }
    public float Range { get; }

    /// <summary>
    /// Physical size of the emitter, in Quake units. Zero is a true point (one hard shadow ray); a surface
    /// light's cluster carries the size of the panel area it stands in for, and its shadow is resolved with
    /// several rays across that extent — the difference between a razor-edged wrong shadow and a penumbra.
    /// </summary>
    public float Radius { get; }
}

/// <summary>
/// Computes a per-vertex lightmap for the edited world — the "compute it once and save the result" step.
///
/// Why this exists at all, in measurements rather than principle: a real-time light cannot be both
/// far-reaching and cheap. Made to reach, hundreds of overlapping volumes cost per-PIXEL work that scales
/// with resolution; pulled back to stay cheap, the same lights stop reaching. A bake has neither constraint —
/// every fixture contributes with true falloff to every surface, and the runtime cost is one vertex attribute.
///
/// Structure of a bake, in the order it runs:
/// <list type="number">
///   <item><b>Direct</b> — Lambert x inverse-square from every light, with occlusion traced against the brush
///     set (<see cref="EditorShadowTrace"/>). Area sources get several jittered rays for a penumbra; points
///     get one hard ray, which is what a point's shadow is.</item>
///   <item><b>Bounce</b> — the direct pass accumulates what each region RECEIVES; those sums become virtual
///     emitters (q3map2's <c>-bounce</c>, radiosity's gather/shoot) and a second pass adds their glow,
///     unshadowed — indirect light is low-frequency, and tracing it would double the cost for detail nobody
///     can see. This is what keeps shadowed areas readable instead of pitch black.</item>
/// </list>
/// </summary>
public static class EditorLightBake
{
    /// <summary>Target spacing between baked samples, in Quake units — the "luxel size" of this vertex bake.</summary>
    public static float SampleSpacing = 96f;

    /// <summary>Grid cell for the light broadphase; a vertex only tests lights in neighbouring cells.</summary>
    private const float LightCell = 512f;

    /// <summary>Bounce gather cell: one virtual emitter per this much space. Coarse on purpose — bounce is fill.</summary>
    private const float BounceCell = 256f;

    /// <summary>
    /// Fraction of received light a surface re-emits. Q3 textures are mostly dark masonry; q3map2 derives
    /// per-texture reflectivity, and 0.5 sits in the range it lands on for this content.
    /// </summary>
    private const float BounceAlbedo = 0.5f;

    /// <summary>Reach of one bounce emitter, in Quake units.</summary>
    private const float BounceRange = 768f;

    /// <summary>Shadow rays traced during the last bake (diagnostics).</summary>
    public static long RaysTraced;

    // ---- the light index --------------------------------------------------------------------------

    private sealed class Grid
    {
        public Grid(IReadOnlyList<BakedLight> lights)
        {
            Lights = lights;
            for (int i = 0; i < lights.Count; i++)
            {
                BakedLight l = lights[i];
                int r = (int)MathF.Ceiling(l.Range / LightCell);
                (int gx, int gy, int gz) = Cell(l.Position);
                for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                {
                    var key = (gx + x, gy + y, gz + z);
                    if (!Buckets.TryGetValue(key, out List<int>? b))
                        Buckets[key] = b = new List<int>();
                    b.Add(i);
                }
            }
        }

        public IReadOnlyList<BakedLight> Lights { get; }
        public Dictionary<(int, int, int), List<int>> Buckets { get; } = new();

        public static (int, int, int) Cell(NVec3 p) => (
            (int)MathF.Floor(p.X / LightCell),
            (int)MathF.Floor(p.Y / LightCell),
            (int)MathF.Floor(p.Z / LightCell));
    }

    private static Grid? _grid;
    private static Grid? _bounceGrid;
    private static EditorShadowTrace? _shadows;
    private static bool _bounceWanted;
    private static int _bounces = 8;
    private static NVec3? _sunDir;          // direction TOWARD the sun, Quake space
    private static Color _sunColor;
    private static float _sunEnergy;

    /// <summary>
    /// Scale of the sun's contribution INTO THE BOUNCE. The sun's direct term stays real-time (its crisp
    /// dynamic shadows are the one thing worth per-frame cost); what was missing is everything that light
    /// does after it lands — on stormkeep, sun bouncing off the lit floor is a large share of the compiled
    /// look. q3map's sun intensities dwarf its fixture values, hence the multiplier.
    /// </summary>
    private const float SunBounceScale = 5f;

    /// <summary>How far the sun-visibility ray travels before the sample counts as outdoors, Quake units.</summary>
    private const float SunRayLength = 16384f;

    /// <summary>Arm a bake.</summary>
    /// <param name="lights">The fixtures to bake.</param>
    /// <param name="shadows">Occluder index for traced shadows, or null for the unshadowed bake.</param>
    /// <param name="bounce">Accumulate and re-emit indirect light (the second pass).</param>
    public static void Begin(IReadOnlyList<BakedLight> lights, EditorShadowTrace? shadows = null, bool bounce = true,
        int bounces = 8, NVec3? sunDirToSun = null, Color sunColor = default, float sunEnergy = 0f)
    {
        _grid = new Grid(lights);
        _shadows = shadows;
        _bounceWanted = bounce;
        _bounces = Math.Clamp(bounces, 1, 16);
        _sunDir = sunDirToSun is { } d && d.LengthSquared() > 1e-6f ? NVec3.Normalize(d) : null;
        _sunColor = sunColor;
        _sunEnergy = sunEnergy;
        _bounceGrid = null;
        _bounceAccum.Clear();
    }

    /// <summary>Release every index.</summary>
    public static void End()
    {
        _grid = null;
        _bounceGrid = null;
        _shadows = null;
        _bounceAccum.Clear();
    }

    /// <summary>True when a bake is armed.</summary>
    public static bool Active => _grid is { Lights.Count: > 0 };

    /// <summary>True when the armed bake wants the bounce pass.</summary>
    public static bool BounceActive => Active && _bounceWanted;

    // ---- pass 1: direct ---------------------------------------------------------------------------

    /// <summary>
    /// Direct light arriving at <paramref name="position"/> on a surface facing <paramref name="normal"/>,
    /// as a colour to multiply the surface albedo by. Also feeds the bounce accumulator, so the second pass
    /// knows what this region received.
    /// </summary>
    public static Color Sample(NVec3 position, NVec3 normal)
    {
        if (_grid is not { } grid)
            return Colors.Black;

        Color direct = GatherDirect(grid, position, normal);

        // The SUN's landing feeds the bounce and only the bounce: its direct term is the real-time light
        // (crisp dynamic shadows), so baking it too would double it — but the light it throws around a room
        // after landing was simply missing, and that bounce is a large share of the compiled look.
        Color received = direct;
        if (_bounceWanted && _sunDir is { } sunDir && _shadows is { } sunShadows)
        {
            float sunDot = NVec3.Dot(normal, sunDir);
            if (sunDot > 0f)
            {
                System.Threading.Interlocked.Increment(ref RaysTraced);
                NVec3 from = position + normal * EditorShadowTrace.SurfaceBias;
                if (!sunShadows.IsOccluded(from, from + sunDir * SunRayLength))
                {
                    float k = _sunEnergy * sunDot * SunBounceScale;
                    received = new Color(
                        received.R + _sunColor.R * k,
                        received.G + _sunColor.G * k,
                        received.B + _sunColor.B * k);
                }
            }
        }

        if (_bounceWanted && (received.R > 0.001f || received.G > 0.001f || received.B > 0.001f))
            AccumulateBounce(position, normal, received);

        return direct;
    }

    private static Color GatherDirect(Grid grid, NVec3 position, NVec3 normal)
    {
        if (!grid.Buckets.TryGetValue(Grid.Cell(position), out List<int>? candidates))
            return Colors.Black;

        float r = 0f, g = 0f, b = 0f;
        foreach (int i in candidates)
        {
            BakedLight l = grid.Lights[i];
            NVec3 delta = l.Position - position;
            float dist2 = delta.LengthSquared();
            if (dist2 >= l.Range * l.Range)
                continue;

            float dist = MathF.Sqrt(dist2);
            if (dist < 1e-3f)
                continue;

            NVec3 dir = delta / dist;
            float ndotl = NVec3.Dot(normal, dir);
            if (ndotl <= 0f)
                continue;   // the surface faces away; a bake has no reason to light its back

            // Occlusion LAST: every cheap rejection above spares a trace.
            float visibility = 1f;
            if (_shadows is { } shadows)
            {
                NVec3 from = position + normal * EditorShadowTrace.SurfaceBias;
                if (l.Radius <= 0f)
                {
                    // A true point: one ray, one answer. Hard shadows are what a point light casts.
                    System.Threading.Interlocked.Increment(ref RaysTraced);
                    if (shadows.IsOccluded(from, l.Position))
                        continue;
                }
                else
                {
                    // An AREA source: several rays across its extent, and visibility is the fraction that get
                    // through. One hard ray from an area light is not a simplification, it is wrong — it
                    // stamps a razor edge where reality has a penumbra, and on a vertex bake those edges land
                    // in arbitrary places (the reported "shadows feel inaccurate").
                    NVec3 side = NVec3.Normalize(MathF.Abs(dir.Z) < 0.9f
                        ? NVec3.Cross(dir, new NVec3(0f, 0f, 1f))
                        : NVec3.Cross(dir, new NVec3(1f, 0f, 0f)));
                    NVec3 up = NVec3.Cross(dir, side);
                    float spread = l.Radius;

                    int unoccluded = 0;
                    Span<NVec3> targets = stackalloc NVec3[4]
                    {
                        l.Position + side * spread,
                        l.Position - side * spread,
                        l.Position + up * spread,
                        l.Position - up * spread,
                    };
                    foreach (NVec3 t in targets)
                    {
                        System.Threading.Interlocked.Increment(ref RaysTraced);
                        if (!shadows.IsOccluded(from, t))
                            unoccluded++;
                    }
                    if (unoccluded == 0)
                        continue;
                    visibility = unoccluded / 4f;
                }
            }

            // Inverse-square, windowed smoothly to zero at the range so a light's edge has no seam.
            float falloff = 1f / (1f + dist * dist / (128f * 128f));
            float window = 1f - dist / l.Range;
            float k = l.Energy * ndotl * falloff * window * window * visibility;

            r += l.Color.R * k;
            g += l.Color.G * k;
            b += l.Color.B * k;
        }

        return new Color(r, g, b);
    }

    // ---- pass 2: bounce ---------------------------------------------------------------------------

    private struct BounceAccum
    {
        public float R, G, B;
        public NVec3 PosW;
        public NVec3 NormW;
        public float W;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), BounceAccum>
        _bounceAccum = new();

    /// <summary>
    /// Record light RECEIVED at a sample, so the region can re-emit it. Samples are roughly uniform (the
    /// tessellation spaces them at luxel distance), so a plain sum weights by lit area — what radiosity wants.
    /// </summary>
    private static void AccumulateBounce(NVec3 position, NVec3 normal, Color direct)
    {
        var key = ((int)MathF.Floor(position.X / BounceCell),
                   (int)MathF.Floor(position.Y / BounceCell),
                   (int)MathF.Floor(position.Z / BounceCell));
        float luma = direct.R + direct.G + direct.B;

        _bounceAccum.AddOrUpdate(key,
            _ => new BounceAccum
            {
                R = direct.R, G = direct.G, B = direct.B,
                PosW = position * luma, NormW = normal * luma, W = luma,
            },
            (_, acc) =>
            {
                acc.R += direct.R; acc.G += direct.G; acc.B += direct.B;
                acc.PosW += position * luma;
                acc.NormW += normal * luma;
                acc.W += luma;
                return acc;
            });
    }

    /// <summary>
    /// Turn the accumulated received light into virtual emitters — radiosity's shoot step, run once between
    /// the two passes. Emitters sit just off their region's average surface, tinted by what arrived there,
    /// scaled by <see cref="BounceAlbedo"/>.
    /// </summary>
    public static int BuildBounceLights()
    {
        if (!_bounceWanted || _bounceAccum.IsEmpty)
        {
            _bounceGrid = null;
            return 0;
        }

        var pos = new List<NVec3>(_bounceAccum.Count);
        var nrm = new List<NVec3>(_bounceAccum.Count);
        var col = new List<NVec3>(_bounceAccum.Count);   // rgb energy, unnormalised
        foreach (BounceAccum acc in _bounceAccum.Values)
        {
            if (acc.W <= 1e-3f)
                continue;
            NVec3 p2 = acc.PosW / acc.W;
            NVec3 n2 = acc.NormW.LengthSquared() > 1e-6f ? NVec3.Normalize(acc.NormW) : new NVec3(0f, 0f, 1f);
            pos.Add(p2 + n2 * 24f);
            nrm.Add(n2);
            col.Add(new NVec3(acc.R, acc.G, acc.B) * (BounceAlbedo * 0.06f));
        }

        // Bounces 2..N as EMITTER-TO-EMITTER radiosity. Iterating at the patch level is what makes "8
        // bounces like the map's own compile" affordable: each pass is a few hundred squared cheap pairs,
        // instead of re-gathering over every baked vertex. Energy decays by the albedo each pass, so the
        // series converges the same way q3map2's does.
        int passes = _bounces - 1;
        var add = new NVec3[pos.Count];
        for (int pass = 0; pass < passes; pass++)
        {
            Array.Clear(add);
            for (int i = 0; i < pos.Count; i++)
            {
                for (int j = 0; j < pos.Count; j++)
                {
                    if (i == j)
                        continue;
                    NVec3 delta = pos[i] - pos[j];
                    float dist2 = delta.LengthSquared();
                    if (dist2 >= BounceRange * BounceRange || dist2 < 1f)
                        continue;
                    float dist = MathF.Sqrt(dist2);
                    NVec3 dir = delta / dist;
                    float give = NVec3.Dot(nrm[j], dir);        // emitter j radiates forward
                    float take = -NVec3.Dot(nrm[i], dir);       // receiver i faces it
                    if (give <= 0f || take <= 0f)
                        continue;
                    float falloff = 1f / (1f + dist2 / (192f * 192f));
                    float window = 1f - dist / BounceRange;
                    add[i] += col[j] * (give * take * falloff * window * window * BounceAlbedo);
                }
            }
            for (int i = 0; i < pos.Count; i++)
                col[i] += add[i];
        }

        var emitters = new List<BakedLight>(pos.Count);
        for (int i = 0; i < pos.Count; i++)
        {
            float sum = col[i].X + col[i].Y + col[i].Z;
            if (sum < 0.02f)
                continue;
            var tint = new Color(col[i].X / sum, col[i].Y / sum, col[i].Z / sum);
            float energy = Math.Clamp(sum, 0f, 5f);
            emitters.Add(new BakedLight(pos[i], tint, energy, BounceRange));
        }

        _bounceGrid = emitters.Count > 0 ? new Grid(emitters) : null;
        return emitters.Count;
    }

    /// <summary>
    /// Indirect light at a sample: the gather over the virtual emitters. UNSHADOWED by design — bounce is
    /// low-frequency fill, and tracing it would double the bake for detail nobody can see. The facing test
    /// still applies, so bounce does not leak onto surfaces pointing away from the region that emits it.
    /// </summary>
    public static Color SampleBounce(NVec3 position, NVec3 normal)
    {
        if (_bounceGrid is not { } grid)
            return Colors.Black;

        if (!grid.Buckets.TryGetValue(Grid.Cell(position), out List<int>? candidates))
            return Colors.Black;

        float r = 0f, g = 0f, b = 0f;
        foreach (int i in candidates)
        {
            BakedLight l = grid.Lights[i];
            NVec3 delta = l.Position - position;
            float dist2 = delta.LengthSquared();
            if (dist2 >= l.Range * l.Range)
                continue;

            float dist = MathF.Sqrt(dist2);
            if (dist < 1e-3f)
                continue;

            float ndotl = NVec3.Dot(normal, delta / dist);
            if (ndotl <= 0f)
                continue;

            float falloff = 1f / (1f + dist * dist / (192f * 192f));
            float window = 1f - dist / l.Range;
            float k = l.Energy * ndotl * falloff * window * window;

            r += l.Color.R * k;
            g += l.Color.G * k;
            b += l.Color.B * k;
        }

        return new Color(r, g, b);
    }
}
