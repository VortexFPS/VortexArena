using Godot;
using XonoticGodot.Formats.Vmap;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>One light as the baker sees it: no Godot node, just the physics.</summary>
public readonly struct BakedLight
{
    public BakedLight(NVec3 position, Color color, float energy, float range)
    {
        Position = position;
        Color = color;
        Energy = energy;
        Range = range;
    }

    /// <summary>Quake space, matching the geometry the baker walks.</summary>
    public NVec3 Position { get; }

    public Color Color { get; }
    public float Energy { get; }
    public float Range { get; }
}

/// <summary>
/// Computes a per-vertex lightmap for the edited world — the "compute it once and save the result" step.
///
/// Why this exists at all, in measurements rather than principle. Real-time lights were made to reach far
/// enough to fill a room, and hundreds of overlapping volumes then cost per-PIXEL work that scales with
/// resolution — the reason the editor ran fine in a small window and badly in a large one. Pulled back to
/// stay cheap, the same lights stopped reaching, contributing about 16% of visible brightness with the sun
/// off. There is no setting of a real-time light that is both far-reaching and free.
///
/// A bake has neither constraint: every fixture contributes with true inverse-square falloff to every
/// surface, and the runtime cost is one extra vertex attribute. This is a vertex lightmap rather than a chart
/// atlas because vertices need no UV2, no packing and no atlas residency — the geometry is subdivided here so
/// the gradients land roughly where a lightmap's luxels would.
///
/// Shadowing is deliberately NOT traced. q3map2 spends minutes on visibility; this runs on every geometry
/// edit and must stay interactive, so occlusion is left to the one real-time light that still casts (the
/// sun). The result is soft, shadowless fixture light — which is what an unshadowed q3map2 <c>-fast</c>
/// bake looks like too.
/// </summary>
public static class EditorLightBake
{
    /// <summary>Target spacing between baked samples, in Quake units — the "luxel size" of this vertex bake.</summary>
    /// (72, not the 56 first tried: 56 subdivided stormkeep into enough vertices to cost 1.78 s of world
    /// rebuild, which a mapper pays on every edit. 72 keeps the gradients and roughly halves the work.)
    public static float SampleSpacing = 96f;

    /// <summary>Grid cell for the light broadphase; a vertex only tests lights in neighbouring cells.</summary>
    private const float LightCell = 512f;

    private readonly struct Grid
    {
        public Grid(IReadOnlyList<BakedLight> lights)
        {
            Lights = lights;
            Buckets = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < lights.Count; i++)
            {
                BakedLight l = lights[i];
                int r = (int)MathF.Ceiling(l.Range / LightCell);
                var c = Cell(l.Position);
                for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                {
                    var key = (c.Item1 + x, c.Item2 + y, c.Item3 + z);
                    if (!Buckets.TryGetValue(key, out List<int>? b))
                        Buckets[key] = b = new List<int>();
                    b.Add(i);
                }
            }
        }

        public IReadOnlyList<BakedLight> Lights { get; }
        public Dictionary<(int, int, int), List<int>> Buckets { get; }

        public static (int, int, int) Cell(NVec3 p) => (
            (int)MathF.Floor(p.X / LightCell),
            (int)MathF.Floor(p.Y / LightCell),
            (int)MathF.Floor(p.Z / LightCell));
    }

    private static Grid? _grid;

    /// <summary>Index the lights once per world build.</summary>
    public static void Begin(IReadOnlyList<BakedLight> lights) => _grid = new Grid(lights);

    /// <summary>Release the index.</summary>
    public static void End() => _grid = null;

    /// <summary>True when a bake is armed (i.e. <see cref="Begin"/> was called with lights).</summary>
    public static bool Active => _grid is { Lights.Count: > 0 };

    /// <summary>
    /// Light arriving at <paramref name="position"/> on a surface facing <paramref name="normal"/>, as a
    /// colour to multiply the surface albedo by. Lambert with inverse-square falloff windowed to the light's
    /// range, which is q3map2's own point model.
    /// </summary>
    public static Color Sample(NVec3 position, NVec3 normal)
    {
        if (_grid is not { } grid)
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
                continue;   // the surface faces away; a bake has no reason to light its back

            // Inverse-square, windowed smoothly to zero at the range so a light's edge has no seam. The
            // reference distance keeps the numbers in the same family as the real-time path's energies.
            float falloff = 1f / (1f + dist * dist / (128f * 128f));
            float window = 1f - dist / l.Range;
            float k = l.Energy * ndotl * falloff * window * window;

            r += l.Color.R * k;
            g += l.Color.G * k;
            b += l.Color.B * k;
        }

        return new Color(r, g, b);
    }
}
