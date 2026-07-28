using System.Threading;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>What a light emits like. Mirrors q3map2's <c>EMIT_*</c>, because the maths differ per kind.</summary>
public enum BakedLightKind
{
    /// <summary>Spherical emitter: <c>photons / d^2</c>.</summary>
    Point,

    /// <summary>Cone emitter, same distance law inside the cone.</summary>
    Spot,

    /// <summary>Infinitely distant: no distance falloff at all, occlusion by one very long ray.</summary>
    Sun,
}

/// <summary>
/// One light as the baker sees it: no Godot node, just the physics — and specifically q3map2's physics.
///
/// <see cref="Photons"/> is the quantity q3map2 actually integrates: <c>intensity * pointScale</c> for an
/// entity light (pointScale is 7500), <c>value * areaScale</c> for a surface light, the raw intensity for a
/// sun. Carrying photons rather than a renderer's "energy" is what makes the RATIOS between a fixture, the
/// sun and the sky match the compiled map — those ratios are the map's lighting design, and no single global
/// scale can fix them once they are wrong.
/// </summary>
public readonly struct BakedLight
{
    public BakedLight(NVec3 position, Color color, float photons, float range, float radius = 0f,
        BakedLightKind kind = BakedLightKind.Point, NVec3 direction = default, float coneCos = -1f)
    {
        Position = position;
        Color = color;
        Photons = photons;
        Range = range;
        Radius = radius;
        Kind = kind;
        Direction = direction;
        ConeCos = coneCos;
    }

    /// <summary>Emitter kind — the distance law depends on it.</summary>
    public BakedLightKind Kind { get; }

    /// <summary>
    /// For a <see cref="BakedLightKind.Sun"/>, the direction TOWARD the light. For a spot, the direction it
    /// points.
    /// </summary>
    public NVec3 Direction { get; }

    /// <summary>Cosine of a spot's half-angle; -1 for everything else.</summary>
    public float ConeCos { get; }

    /// <summary>Quake space, matching the geometry the baker walks.</summary>
    public NVec3 Position { get; }

    public Color Color { get; }

    /// <summary>q3map2 photons — see the type remarks.</summary>
    public float Photons { get; }

    /// <summary>Distance past which this light is skipped, from q3map2's falloff tolerance.</summary>
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

    /// <summary>
    /// Hard cap on a light's broadphase radius in cells. At 512 units per cell this is a 4096-unit reach,
    /// and the cost of a light is cubic in this number — see the note in <see cref="Grid"/>.
    /// </summary>
    private const int MaxLightCellRadius = 8;

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

    /// <summary>
    /// Value that encodes to full scale in the vertex COLOR channel, set from the bake's own p99.
    ///
    /// It is measured rather than fixed because the bake's absolute magnitude is a property of the MAP: it
    /// follows q3map2's photon units, so a map with brighter lights or bigger emissive panels produces
    /// larger numbers. A hardcoded range silently clamps the top of the distribution, and a clamped bake
    /// looks exactly like a flat one — that failure has now happened twice, so the range is no longer a
    /// constant anyone can get wrong.
    /// </summary>
    public static float EncodeRange { get; private set; } = 48f;

    /// <summary>Set <see cref="EncodeRange"/> from a completed bake's value distribution.</summary>
    private static void MeasureEncodeRange(Color[] values)
    {
        if (values.Length == 0)
            return;
        var mags = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
            mags[i] = MathF.Max(values[i].R, MathF.Max(values[i].G, values[i].B));
        Array.Sort(mags);
        // p99, not the maximum: a handful of luxels sitting on top of an emitter would otherwise set the
        // range for the whole map and push everything else into the noise floor.
        float p99 = mags[(int)(0.99f * (mags.Length - 1))];
        EncodeRange = Math.Clamp(p99, 1f, 100000f);
    }

    // ---- dirtmapping (q3map2 -dirty) --------------------------------------------------------------

    /// <summary>
    /// Ambient occlusion baked per sample — q3map2's <c>-dirty</c>, and the single largest source of the
    /// depth a compiled Q3 map has and a plain light bake does not. Direct light alone leaves every unlit
    /// surface at exactly the same value regardless of how enclosed it is; dirt is what darkens the inside
    /// corner, the stair riser and the underside of the ledge, which is where the eye reads shape.
    ///
    /// Cheap by construction: the rays are SHORT (they terminate inside a cell or two of the DDA grid),
    /// unlike the light rays that cross the map.
    /// </summary>
    private const int DirtRays = 12;

    /// <summary>
    /// Minimum distance used in the inverse-square law, Quake units. Pure 1/d^2 is singular and a luxel can
    /// land arbitrarily close to a fixture's own face; clamping the DISTANCE rather than the result keeps
    /// the pool's shape exact everywhere its shape is actually visible.
    /// </summary>
    private const float NearClamp = 16f;

    /// <summary>
    /// How far a dirt ray looks for an occluder, Quake units. 64 because that is what stormkeep was compiled
    /// with (<c>-dirtdepth 64</c>, from Xonotic's own q3map2 line); a deeper probe over-occludes open floor.
    /// </summary>
    private const float DirtDepth = 64f;

    /// <summary>How much of the light dirt is allowed to remove, 0..1 (q3map2's dirtGain).</summary>
    private static float _dirtStrength = 0.9f;

    /// <summary>Set the dirt strength; 0 disables dirtmapping entirely.</summary>
    public static float DirtStrength
    {
        get => _dirtStrength;
        set => _dirtStrength = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Fraction of the hemisphere above <paramref name="normal"/> that is open, as a light multiplier.
    /// 1 = a surface in the open, ~0.1 = a tight inside corner.
    /// </summary>
    private static float DirtFactor(NVec3 position, NVec3 normal)
    {
        if (_shadows is not { } shadows || _dirtStrength <= 0f)
            return 1f;

        // Build a basis on the surface, then fire a fixed Fibonacci hemisphere through it. Fixed rather
        // than random: a bake must be reproducible, and neighbouring vertices sharing directions is what
        // keeps the result smooth instead of grainy.
        NVec3 side = NVec3.Normalize(MathF.Abs(normal.Z) < 0.9f
            ? NVec3.Cross(normal, new NVec3(0f, 0f, 1f))
            : NVec3.Cross(normal, new NVec3(1f, 0f, 0f)));
        NVec3 up = NVec3.Cross(normal, side);
        NVec3 from = position + normal * EditorShadowTrace.SurfaceBias;

        int open = 0;
        for (int i = 0; i < DirtRays; i++)
        {
            // Cosine-ish distribution: z rises with i, the azimuth advances by the golden angle.
            float z = (i + 0.5f) / DirtRays;
            float rxy = MathF.Sqrt(1f - z * z);
            float phi = i * 2.39996323f;
            NVec3 dir = NVec3.Normalize(
                side * (rxy * MathF.Cos(phi)) + up * (rxy * MathF.Sin(phi)) + normal * z);

            System.Threading.Interlocked.Increment(ref RaysTraced);
            if (!shadows.IsOccluded(from, from + dir * DirtDepth))
                open++;
        }

        float openness = (float)open / DirtRays;
        return 1f - _dirtStrength * (1f - openness);
    }

    // ---- the light index --------------------------------------------------------------------------

    private sealed class Grid
    {
        public Grid(IReadOnlyList<BakedLight> lights)
        {
            Lights = lights;
            var suns = new List<int>();
            for (int i = 0; i < lights.Count; i++)
            {
                BakedLight l = lights[i];

                // A SUN has no position and infinite reach, so it cannot go in a position index at all.
                // Putting one in anyway is not merely wasteful: Range is float.MaxValue, and
                // (int)ceil(3.4e38 / 512) overflows to int.MinValue, which silently files the sun under a
                // garbage key where no lookup will ever find it. Suns are evaluated for every sample.
                if (l.Kind == BakedLightKind.Sun)
                {
                    suns.Add(i);
                    continue;
                }

                // Bounded on purpose. A light's radius comes from its photon count, and a big enough emitter
                // can ask for thousands of units — which is (2r+1)^3 buckets, each an allocation, and then
                // every sample in that volume pays a shadow trace for it. Both the memory and the ray count
                // are cubic in a number derived from map data, so it gets a ceiling.
                int r = Math.Clamp((int)MathF.Ceiling(l.Range / LightCell), 0, MaxLightCellRadius);
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
            Suns = suns.ToArray();
        }

        /// <summary>Indices of the directional lights, which apply everywhere.</summary>
        public int[] Suns { get; }

        public IReadOnlyList<BakedLight> Lights { get; }
        public Dictionary<(int, int, int), List<int>> Buckets { get; } = new();

        public static (int, int, int) Cell(NVec3 p) => (
            (int)MathF.Floor(p.X / LightCell),
            (int)MathF.Floor(p.Y / LightCell),
            (int)MathF.Floor(p.Z / LightCell));
    }

    // ---- the retained bake ------------------------------------------------------------------------

    /// <summary>
    /// The last completed bake, as light samples in space rather than as mesh attributes. An edit rebuilds
    /// the world mesh from scratch, which throws the vertex colours away with it — so the lighting is kept
    /// HERE and resampled onto the new vertices.
    ///
    /// This is what lets an edit cost nothing in lighting. The alternative, recomputing a cheap unshadowed
    /// bake on every edit, is both slow and a visible downgrade: the world flashes to flatter lighting the
    /// instant you nudge a brush, which reads as the editor breaking the map.
    /// </summary>
    private static readonly Dictionary<(int, int, int), (NVec3 Sum, int Count)> _cache = new();

    private static bool _cacheMode;

    /// <summary>Cell size of the retained bake, in Quake units — the luxel spacing it was baked at.</summary>
    private const float CacheCell = 64f;

    /// <summary>True when a completed bake is available to resample.</summary>
    public static bool CacheReady => _cache.Count > 0;

    /// <summary>True when this build is resampling the retained bake rather than computing one.</summary>
    public static bool Resampling => _cacheMode;

    /// <summary>
    /// Arm resample-from-the-last-bake mode: <see cref="Sample"/> returns retained light instead of
    /// computing any. Used for every rebuild that is not an explicit rebake.
    /// </summary>
    public static Color Resample(NVec3 positionQuake) => SampleCached(positionQuake);

    public static void BeginCached()
    {
        // Deliberately does NOT release the indices: a background bake may be running and reading them.
        // Resample mode is a property of what the BUILDER does, not of what the worker is allowed to use.
        if (!BakeRunning)
        {
            _grid = null;
            _bounceGrid = null;
            _shadows = null;
        }
        _cacheMode = true;
    }

    // ---- the background bake ----------------------------------------------------------------------

    /// <summary>
    /// Vertices captured from a world build, waiting to be lit. Plain arrays of value types on purpose:
    /// the background bake must not touch a Godot object, and this is the whole interface between the two.
    /// </summary>
    public sealed class SampleSet
    {
        public SampleSet(int capacity)
        {
            Positions = new List<NVec3>(capacity);
            Normals = new List<NVec3>(capacity);
            Albedos = new List<Color>(capacity);
        }

        public List<NVec3> Positions { get; }
        public List<NVec3> Normals { get; }
        public List<Color> Albedos { get; }
        public int Count => Positions.Count;
    }

    /// <summary>
    /// When set, a world build CAPTURES its vertices instead of lighting them, and fills their colours by
    /// resampling the retained bake. The capture then feeds <see cref="RunBackground"/>.
    /// </summary>
    public static bool Deferred { get; set; }

    /// <summary>The vertices captured by the last deferred build.</summary>
    public static SampleSet? Captured { get; private set; }

    /// <summary>Begin a capture for a build of roughly <paramref name="capacity"/> vertices.</summary>
    public static void BeginCapture(int capacity) => Captured = new SampleSet(capacity);

    /// <summary>Record one vertex for the background bake.</summary>
    public static void Capture(NVec3 position, NVec3 normal, Color albedo)
    {
        SampleSet? set = Captured;
        if (set is null)
            return;
        lock (set)
        {
            set.Positions.Add(position);
            set.Normals.Add(normal);
            set.Albedos.Add(albedo);
        }
    }

    /// <summary>Samples lit so far by the running bake (for the progress readout).</summary>
    public static int Progress => Volatile.Read(ref _progress);

    /// <summary>Total samples the running bake will light.</summary>
    public static int ProgressTotal { get; private set; }

    /// <summary>True while a background bake is running.</summary>
    public static bool BakeRunning => Volatile.Read(ref _running) != 0;

    /// <summary>True once a background bake has finished and its result is waiting to be shown.</summary>
    public static bool BakeFinished => Volatile.Read(ref _finished) != 0;

    /// <summary>Acknowledge a finished bake (the caller is about to rebuild the world from it).</summary>
    public static void ClearFinished() => Volatile.Write(ref _finished, 0);

    private static int _progress;
    private static int _running;
    private static int _finished;
    private static int _cancel;

    /// <summary>
    /// Ask a running bake to stop and wait briefly for it to notice.
    ///
    /// Shutdown is the reason this exists: the worker holds the light and occluder indices, and a process
    /// that tears the scene down while minutes of ray tracing are still in flight is a process that looks
    /// hung to whoever is closing it.
    /// </summary>
    public static void Cancel()
    {
        if (!BakeRunning)
            return;
        Volatile.Write(ref _cancel, 1);
        for (int i = 0; i < 100 && BakeRunning; i++)
            Thread.Sleep(10);
    }

    /// <summary>
    /// Light the captured vertices off the main thread, then publish the result into the retained bake.
    ///
    /// Why off-thread at all: a faithful bake is not fast — q3map2 spends minutes on this map, and it is a
    /// batch tool that nobody is looking at. Ours runs inside a live game, so the same work done on the main
    /// thread is an unresponsive window that looks exactly like a hang, which is precisely what it was.
    /// The editor keeps rendering the previous lighting while this runs.
    /// </summary>
    public static async System.Threading.Tasks.Task RunBackground()
    {
        SampleSet? set = Captured;
        if (set is null || set.Count == 0 || _grid is null)
        {
            Volatile.Write(ref _finished, 1);
            return;
        }

        Volatile.Write(ref _running, 1);
        Volatile.Write(ref _progress, 0);
        Volatile.Write(ref _cancel, 0);
        ProgressTotal = set.Count;

        try
        {
            NVec3[] pos = set.Positions.ToArray();
            NVec3[] nrm = set.Normals.ToArray();
            Color[] alb = set.Albedos.ToArray();
            var result = new Color[pos.Length];
            var dirt = new float[pos.Length];

            await System.Threading.Tasks.Task.Run(() =>
            {
                // Direct + dirt, in chunks so the progress counter moves without an interlock per vertex.
                System.Threading.Tasks.Parallel.For(0, (pos.Length + ChunkSize - 1) / ChunkSize, chunk =>
                {
                    if (Volatile.Read(ref _cancel) != 0)
                        return;
                    int start = chunk * ChunkSize;
                    int end = Math.Min(start + ChunkSize, pos.Length);
                    for (int i = start; i < end; i++)
                    {
                        result[i] = SampleDirect(pos[i], nrm[i], alb[i], out float d);
                        dirt[i] = d;
                    }
                    Interlocked.Add(ref _progress, end - start);
                });

                if (Volatile.Read(ref _cancel) == 0 && _bounceWanted && BuildBounceLights() > 0)
                {
                    System.Threading.Tasks.Parallel.For(0, (pos.Length + ChunkSize - 1) / ChunkSize, chunk =>
                    {
                        if (Volatile.Read(ref _cancel) != 0)
                            return;
                        int start = chunk * ChunkSize;
                        int end = Math.Min(start + ChunkSize, pos.Length);
                        for (int i = start; i < end; i++)
                        {
                            Color bounce = SampleBounce(pos[i], nrm[i]);
                            Color c = result[i];
                            result[i] = new Color(
                                c.R + bounce.R * dirt[i],
                                c.G + bounce.G * dirt[i],
                                c.B + bounce.B * dirt[i]);
                        }
                    });
                }
            }).ConfigureAwait(false);

            // Publish only a COMPLETE bake: half of one written over the retained lighting would leave the
            // map lit in patches, which is worse than the lighting it replaced.
            if (Volatile.Read(ref _cancel) == 0)
            {
                MeasureEncodeRange(result);
                CacheReset();
                for (int i = 0; i < pos.Length; i++)
                    CacheStore(pos[i], result[i]);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            Volatile.Write(ref _finished, 1);
            Captured = null;
        }
    }

    /// <summary>Vertices per parallel work item — big enough to amortise the progress interlock.</summary>
    private const int ChunkSize = 2048;

    /// <summary>Drop the retained bake (a fresh one is about to replace it).</summary>
    public static void CacheReset()
    {
        _cache.Clear();
        _exact.Clear();
    }

    /// <summary>Retain one baked sample. Called for every vertex of a completed bake.</summary>
    public static void CacheStore(NVec3 positionQuake, Color color)
    {
        // EXACT, keyed to a quarter unit: a rebuild of unedited geometry regenerates the very same vertex
        // positions, so this hands back the bake bit for bit rather than a neighbourhood average.
        _exact[ExactKey(positionQuake)] = new NVec3(color.R, color.G, color.B);

        var key = (
            (int)MathF.Floor(positionQuake.X / CacheCell),
            (int)MathF.Floor(positionQuake.Y / CacheCell),
            (int)MathF.Floor(positionQuake.Z / CacheCell));
        _cache.TryGetValue(key, out (NVec3 Sum, int Count) acc);
        _cache[key] = (acc.Sum + new NVec3(color.R, color.G, color.B), acc.Count + 1);
    }

    private static (int, int, int) ExactKey(NVec3 p) => (
        (int)MathF.Round(p.X * 4f), (int)MathF.Round(p.Y * 4f), (int)MathF.Round(p.Z * 4f));

    private static readonly Dictionary<(int, int, int), NVec3> _exact = new();

    /// <summary>
    /// Retained light at a position: its own cell, else the mean of whatever neighbours have samples.
    /// Geometry that did not exist at bake time therefore inherits its surroundings' lighting rather than
    /// rendering black or white — approximate on purpose, and the stale indicator says so.
    /// </summary>
    private static Color SampleCached(NVec3 position)
    {
        if (_exact.TryGetValue(ExactKey(position), out NVec3 exact))
            return new Color(exact.X, exact.Y, exact.Z);

        int cx = (int)MathF.Floor(position.X / CacheCell);
        int cy = (int)MathF.Floor(position.Y / CacheCell);
        int cz = (int)MathF.Floor(position.Z / CacheCell);

        if (_cache.TryGetValue((cx, cy, cz), out (NVec3 Sum, int Count) hit) && hit.Count > 0)
            return Col(hit.Sum / hit.Count);

        NVec3 sum = NVec3.Zero;
        int n = 0;
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
            if (_cache.TryGetValue((cx + x, cy + y, cz + z), out (NVec3 Sum, int Count) near) && near.Count > 0)
            {
                sum += near.Sum / near.Count;
                n++;
            }

        return n > 0 ? Col(sum / n) : Colors.Black;

        static Color Col(NVec3 v) => new(v.X, v.Y, v.Z);
    }

    private static Grid? _grid;
    private static Grid? _bounceGrid;
    private static EditorShadowTrace? _shadows;
    private static bool _bounceWanted;
    private static int _bounces = 8;
    /// <summary>How far the sun-visibility ray travels before the sample counts as outdoors, Quake units.</summary>
    private const float SunRayLength = 16384f;

    /// <summary>Arm a bake.</summary>
    /// <param name="lights">The fixtures to bake.</param>
    /// <param name="shadows">Occluder index for traced shadows, or null for the unshadowed bake.</param>
    /// <param name="bounce">Accumulate and re-emit indirect light (the second pass).</param>
    public static void Begin(IReadOnlyList<BakedLight> lights, EditorShadowTrace? shadows = null, bool bounce = true,
        int bounces = 8)
    {
        _cacheMode = false;
        _grid = new Grid(lights);
        _shadows = shadows;
        // bounces <= 0 means NO bounce at all, exactly like q3map2's -bounce 0 — the earlier clamp to a
        // minimum of 1 silently turned "cl_editor_bake_bounces 0" into one bounce, which is why toggling it
        // appeared to do nothing.
        _bounceWanted = bounce && bounces > 0;
        _bounces = Math.Clamp(bounces, 1, 16);
        _bounceGrid = null;
        _bounceAccum.Clear();
    }

    /// <summary>Release every index.</summary>
    public static void End()
    {
        if (BakeRunning)
            return;     // the worker still owns these; the poll calls End() again once it finishes
        _grid = null;
        _bounceGrid = null;
        _shadows = null;
        _bounceAccum.Clear();
    }

    /// <summary>True when the builder should write baked vertex colours — computing them or resampling them.</summary>
    public static bool Active => _cacheMode || _grid is { Lights.Count: > 0 };

    /// <summary>True when the armed bake wants the bounce pass. Never in resample mode: it is already in there.</summary>
    public static bool BounceActive => !_cacheMode && _grid is { Lights.Count: > 0 } && _bounceWanted;

    // ---- pass 1: direct ---------------------------------------------------------------------------

    /// <summary>
    /// Direct light arriving at <paramref name="position"/> on a surface facing <paramref name="normal"/>,
    /// as a colour to multiply the surface albedo by. Also feeds the bounce accumulator, so the second pass
    /// knows what this region received.
    /// </summary>
    public static Color Sample(NVec3 position, NVec3 normal) =>
        Sample(position, normal, _greyAlbedo, out _);

    private static readonly Color _greyAlbedo = new(0.45f, 0.45f, 0.45f);

    /// <param name="position">Sample position, Quake space.</param>
    /// <param name="normal">Surface normal.</param>
    /// <param name="albedo">
    /// Average colour of the surface's texture: what the BOUNCE from this sample carries. Light reflecting
    /// off a rust floor is rust — feeding grey here is why bounce light used to read cold.
    /// </param>
    /// <param name="dirt">
    /// The sample's openness, for the caller to apply to the bounce pass as well — computed once here
    /// because the rays are not free and both passes want the same answer.
    /// </param>
    public static Color Sample(NVec3 position, NVec3 normal, Color albedo, out float dirt)
    {
        dirt = 1f;
        if (_cacheMode)
            return SampleCached(position);
        return SampleDirect(position, normal, albedo, out dirt);
    }

    /// <summary>
    /// Compute light at a sample, never resampling. The background worker uses this rather than
    /// <see cref="Sample"/> so that a main-thread rebuild flipping into resample mode mid-bake cannot
    /// silently turn the worker's own output into a copy of the bake it is replacing.
    /// </summary>
    /// <summary>
    /// Cheap preview light for a build that has no retained bake to resample: the direct term only, with
    /// whatever occluder index is currently armed (none, on the preview pass). Without this the very first
    /// build of a session renders BLACK for as long as the background bake takes, which reads as a broken
    /// editor rather than a working one.
    /// </summary>
    public static Color Preview(NVec3 position, NVec3 normal) =>
        SampleDirect(position, normal, _greyAlbedo, out _);

    private static Color SampleDirect(NVec3 position, NVec3 normal, Color albedo, out float dirt)
    {
        dirt = 1f;
        if (_grid is not { } grid)
            return Colors.Black;

        dirt = DirtFactor(position, normal);
        Color direct = GatherDirect(grid, position, normal);
        direct = new Color(direct.R * dirt, direct.G * dirt, direct.B * dirt);

        // The sun and the sky dome are ordinary baked lights now (q3map2 treats them as lights too), so
        // they are already in `direct` and therefore already feed the bounce. No special case needed.
        Color received = direct;

        if (_bounceWanted && (received.R > 0.001f || received.G > 0.001f || received.B > 0.001f))
            AccumulateBounce(position, normal, new Color(
                received.R * albedo.R, received.G * albedo.G, received.B * albedo.B));

        return direct;
    }

    private static Color GatherDirect(Grid grid, NVec3 position, NVec3 normal)
    {
        float r = 0f, g = 0f, b = 0f;

        grid.Buckets.TryGetValue(Grid.Cell(position), out List<int>? local);
        int localCount = local?.Count ?? 0;
        int total = localCount + grid.Suns.Length;

        for (int n = 0; n < total; n++)
        {
            int i = n < localCount ? local![n] : grid.Suns[n - localCount];
            BakedLight l = grid.Lights[i];

            NVec3 dir;
            float dist, attenuation;
            if (l.Kind == BakedLightKind.Sun)
            {
                // A sun is infinitely distant: no falloff, and the "position" is meaningless. q3map2's
                // EMIT_SUN contributes photons * N.L to anything that can see the sky along its direction.
                dir = l.Direction;
                dist = SunRayLength;
                attenuation = 1f;
            }
            else
            {
                NVec3 delta = l.Position - position;
                float dist2 = delta.LengthSquared();
                if (dist2 >= l.Range * l.Range)
                    continue;

                dist = MathF.Sqrt(dist2);
                if (dist < 1e-3f)
                    continue;
                dir = delta / dist;

                // q3map2, light.c: add = ( photons / ( dist * dist ) ) * angle. Pure inverse-square, with
                // no window and no saturation — the curve IS the look of a Q3 light pool. The near clamp
                // only keeps a luxel that lands on top of an emitter from going singular.
                float d = MathF.Max(dist, NearClamp);
                attenuation = 1f / (d * d);

                if (l.Kind == BakedLightKind.Spot)
                {
                    float cone = NVec3.Dot(-dir, l.Direction);
                    if (cone <= l.ConeCos)
                        continue;
                    // Soften the last few degrees so the cone edge is not a hard stamp.
                    attenuation *= Math.Clamp((cone - l.ConeCos) / MathF.Max(1e-4f, 1f - l.ConeCos) * 4f, 0f, 1f);
                }
            }

            float ndotl = NVec3.Dot(normal, dir);
            if (ndotl <= 0f)
                continue;   // the surface faces away; a bake has no reason to light its back

            // Occlusion LAST: every cheap rejection above spares a trace.
            float visibility = 1f;
            if (_shadows is { } shadows)
            {
                NVec3 from = position + normal * EditorShadowTrace.SurfaceBias;
                if (l.Kind == BakedLightKind.Sun)
                {
                    System.Threading.Interlocked.Increment(ref RaysTraced);
                    if (shadows.IsOccluded(from, from + dir * SunRayLength))
                        continue;
                }
                else if (l.Radius <= 0f)
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

            float k = l.Photons * attenuation * ndotl * visibility;

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
            // The per-sample TEXTURE albedo is already folded in at accumulation; the constant here is only
            // the emitter-strength calibration (raised from the grey-albedo era, since real Q3 textures
            // average darker than the 0.5 grey they replaced).
            col.Add(new NVec3(acc.R, acc.G, acc.B) * 0.22f);
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
                    float falloff = 1f / (1f + dist2 / (288f * 288f));
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
            float energy = Math.Clamp(sum, 0f, 16f);
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

            float falloff = 1f / (1f + dist * dist / (320f * 320f));
            float window = 1f - dist / l.Range;
            float k = l.Photons * ndotl * falloff * window * window;

            r += l.Color.R * k;
            g += l.Color.G * k;
            b += l.Color.B * k;
        }

        return new Color(r, g, b);
    }
}
