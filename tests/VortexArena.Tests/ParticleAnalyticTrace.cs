using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Tests;

/// <summary>
/// The analytic collision world + swept-POINT trace used by <see cref="ParticleParityTests"/>.
///
/// This is a LINE-FOR-LINE C# twin of the trace in <c>tools/particles-ref/particles_ref.c</c>
/// (<c>clip_point_to_brush</c> / <c>world_trace</c> / <c>world_pointcontents</c>). Particle traces are POINT
/// traces (mins == maxs == 0), so — unlike the movement box trace — there is NO hull expansion; the plane
/// distances are used as-is. The golden generator and the parity test therefore share <i>identical</i>
/// collision by construction, so any trajectory divergence between the C reference and the ported
/// <see cref="XonoticGodot.Engine.Particles.ParticleSim"/> is a pure particle-math difference.
/// </summary>
public sealed class ParticleAnalyticWorld
{
    public readonly record struct Plane(Vector3 Normal, float Dist);          // inside: dot(n,p) <= dist
    public sealed class Brush { public Plane[] Planes = System.Array.Empty<Plane>(); public int Contents; public int SurfaceFlags; }

    // SUPERCONTENTS (Base/darkplaces/bspfile.h), mirrored from the C reference.
    public const int ContSolid = 0x00000001;
    public const int ContWater = 0x00000010;
    public const int ContSlime = 0x00000020;
    public const int ContLava  = 0x00000040;
    public const int ContNoDrop = unchecked((int)0x80000000);
    public const int ContLiquidsMask = ContWater | ContSlime | ContLava;

    public const float TraceDistEpsilon = 1.0f / 32.0f;

    public readonly List<Brush> Brushes = new();

    public static ParticleAnalyticWorld FromBrushes(IReadOnlyList<(int contents, int surfaceflags, float[] planes)> brushes)
    {
        var w = new ParticleAnalyticWorld();
        foreach (var (contents, sflags, flat) in brushes)
        {
            int n = flat.Length / 4;
            var planes = new Plane[n];
            for (int i = 0; i < n; i++)
                planes[i] = new Plane(new Vector3(flat[i * 4 + 0], flat[i * 4 + 1], flat[i * 4 + 2]), flat[i * 4 + 3]);
            w.Brushes.Add(new Brush { Planes = planes, Contents = contents, SurfaceFlags = sflags });
        }
        return w;
    }

    // ---- clip_point_to_brush (verbatim translation of the C; POINT trace -> no hull expansion) ----
    private static void ClipPointToBrush(ref TraceResult tr, ref int startContents, Vector3 start, Vector3 end, Brush b)
    {
        if (b.Planes.Length == 0) return;
        float enterfrac = -1.0f, leavefrac = 1.0f;
        Vector3 clipnormal = Vector3.Zero;
        bool startout = false, endout = false;
        for (int i = 0; i < b.Planes.Length; i++)
        {
            Vector3 n = b.Planes[i].Normal;
            float d = b.Planes[i].Dist;
            float d1 = Vector3.Dot(n, start) - d;
            float d2 = Vector3.Dot(n, end) - d;
            if (d1 > 0) startout = true;
            if (d2 > 0) endout = true;
            if (d1 > 0 && d2 >= 0) return;          // wholly outside this plane -> no hit
            if (d1 <= 0 && d2 <= 0) continue;        // wholly inside this halfspace
            if (d1 > d2)
            {
                float f = (d1 - TraceDistEpsilon) / (d1 - d2);
                if (f > enterfrac) { enterfrac = f; clipnormal = n; }
            }
            else
            {
                float f = (d1 + TraceDistEpsilon) / (d1 - d2);
                if (f < leavefrac) leavefrac = f;
            }
        }
        if (!startout)
        {
            tr.StartSolid = true;
            startContents |= b.Contents;
            if (!endout) tr.AllSolid = true;
            return;
        }
        if (enterfrac < leavefrac && enterfrac > -1.0f)
        {
            if (enterfrac < 0.0f) enterfrac = 0.0f;
            if (enterfrac < tr.Fraction)
            {
                tr.Fraction = enterfrac;
                tr.PlaneNormal = clipnormal;
                tr.DpHitContents = b.Contents;
                tr.DpHitQ3SurfaceFlags = b.SurfaceFlags;
            }
        }
    }

    /// <summary>traceline vs the brushes whose contents intersect <paramref name="hitmask"/>.</summary>
    public TraceResult Trace(Vector3 start, Vector3 end, int hitmask)
    {
        var tr = new TraceResult { Fraction = 1.0f, EndPos = end, PlaneNormal = Vector3.Zero };
        int startContents = 0;
        foreach (Brush b in Brushes)
        {
            if ((b.Contents & hitmask) == 0) continue;
            ClipPointToBrush(ref tr, ref startContents, start, end, b);
            if (tr.AllSolid) tr.Fraction = 0.0f;
        }
        if (tr.StartSolid) tr.Fraction = tr.AllSolid ? 0.0f : tr.Fraction;
        // The ParticleSim recovers "started in solid" from StartSolid + DpHitContents. On a pure start-solid
        // (no later surface hit) DpHitContents is still 0, so fold the start contents in — exactly DP, whose
        // kill test reads (trace.startsupercontents | trace.hitsupercontents).
        if (tr.StartSolid && tr.DpHitContents == 0) tr.DpHitContents = startContents;
        else if (tr.StartSolid) tr.DpHitContents |= startContents;
        tr.EndPos = start + (end - start) * tr.Fraction;
        return tr;
    }

    /// <summary>pointcontents (mirrors <c>world_pointcontents</c>).</summary>
    public int PointContents(Vector3 p)
    {
        int c = 0;
        foreach (Brush b in Brushes)
        {
            bool inside = true;
            foreach (Plane pl in b.Planes)
                if (Vector3.Dot(pl.Normal, p) - pl.Dist > 0) { inside = false; break; }
            if (inside) c |= b.Contents;
        }
        return c;
    }
}

/// <summary>An <see cref="ITraceService"/> over a <see cref="ParticleAnalyticWorld"/>.</summary>
public sealed class ParticleAnalyticTraceService : ITraceService
{
    private readonly ParticleAnalyticWorld _world;
    public ParticleAnalyticTraceService(ParticleAnalyticWorld world) => _world = world;

    // The particle bounce trace passes a POINT hull (mins == maxs == Vector3.Zero). DP's traceline takes the
    // SUPERCONTENTS hit mask explicitly (SOLID, plus LIQUIDS for rain/snow); MoveFilter cannot carry that, so
    // we trace SOLID here — none of the committed collision scenarios bounce a rain/snow particle, and a
    // SOLID-only trace exactly matches the C reference for every spark/blood scenario. ParticleSim still
    // applies DP's per-type kill/bounce logic on the returned hit.
    public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
        => _world.Trace(start, end, ParticleAnalyticWorld.ContSolid);

    public int PointContents(Vector3 point) => _world.PointContents(point);
    public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
}

/// <summary>
/// A minimal <see cref="IEngineServices"/> for the particle parity tests: the analytic point-trace + a
/// settable clock + a cvar service that returns the stock <see cref="XonoticGodot.Engine.Particles.ParticleCvars"/>
/// defaults (and a configurable cl_particles_collisions). Entities/Sound/Models are unused by the sim.
/// </summary>
public sealed class ParticleTestServices : IEngineServices
{
    public ITraceService Trace { get; }
    public IGameClock Clock { get; }
    public ICvarService Cvars { get; }
    public IEntityService Entities { get; } = new MinimalEntities();
    public ISoundService Sound { get; } = new NullSound();
    public IModelService Models { get; } = new NullModels();

    public ParticleTestServices(ParticleAnalyticWorld world, MutableClock clock, bool collisions)
    {
        Trace = new ParticleAnalyticTraceService(world);
        Clock = clock;
        Cvars = new ParticleDefaultsCvars(collisions);
    }

    /// <summary>Returns ParticleCvars.Defaults (+ overridable cl_particles_collisions), 0 for anything else.</summary>
    private sealed class ParticleDefaultsCvars : ICvarService
    {
        private readonly Dictionary<string, float> _values = new();
        public ParticleDefaultsCvars(bool collisions)
        {
            foreach (var (name, def, _) in XonoticGodot.Engine.Particles.ParticleCvars.Defaults)
                if (float.TryParse(def, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    _values[name] = v;
            _values[XonoticGodot.Engine.Particles.ParticleCvars.Collisions] = collisions ? 1f : 0f;
            _values["sv_gravity"] = 800f;
        }
        public float GetFloat(string name) => _values.TryGetValue(name, out float v) ? v : 0f;
        public string GetString(string name) => "";
        public void Set(string name, string value)
        {
            if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out float v))
                _values[name] = v;
        }
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
    }

    private sealed class MinimalEntities : IEntityService
    {
        public Entity Spawn() => new();
        public void Remove(Entity e) { }
        public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
        public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
        public void SetModel(Entity e, string model) { }
        public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
        public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => System.Array.Empty<Entity>();
    }
    private sealed class NullSound : ISoundService
    {
        public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
        public void Stop(Entity e, SoundChannel channel) { }
    }
    private sealed class NullModels : IModelService
    {
        public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
        { origin = forward = right = up = Vector3.Zero; return false; }
        public void SetAttachment(Entity e, Entity parent, string tagName) { }
    }
}
