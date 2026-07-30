using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Tests;

/// <summary>
/// The analytic collision world + brush-vs-box trace used by <see cref="MovementParityTests"/>.
///
/// This is a LINE-FOR-LINE C# twin of the trace in <c>tools/movement-ref/movement_ref.c</c>
/// (<c>clip_box_to_brush</c> / <c>world_trace</c> / <c>world_pointcontents</c>). The golden-trace
/// generator and the parity test therefore share <i>identical</i> collision by construction, so any
/// trajectory divergence between the C reference and the ported <see cref="XonoticGodot.Common.Physics.PlayerPhysics"/>
/// is a pure physics-math difference — never a trace-implementation artefact. (The production BSP
/// <c>TraceService</c> is exercised separately by the collision/PVS tests; here we deliberately swap it
/// out to isolate the movement maths.)
/// </summary>
public sealed class AnalyticWorld
{
    public readonly record struct Plane(Vector3 Normal, float Dist);          // inside: dot(n,p) <= dist
    public sealed class Brush { public Plane[] Planes = System.Array.Empty<Plane>(); public int Contents; }

    // SUPERCONTENTS (bspfile.h), mirrored from the C reference.
    public const int ContSolid = 0x00000001;
    public const int ContWater = 0x00000010;
    public const int ContSlime = 0x00000020;
    public const int ContLava  = 0x00000040;
    public const int ContLiquidsMask = ContWater | ContSlime | ContLava;

    public const float TraceDistEpsilon = 1.0f / 32.0f;

    public readonly List<Brush> Brushes = new();

    public static AnalyticWorld FromPlanes(IReadOnlyList<(int contents, float[] planes)> brushes)
    {
        var w = new AnalyticWorld();
        foreach (var (contents, flat) in brushes)
        {
            int n = flat.Length / 4;
            var planes = new Plane[n];
            for (int i = 0; i < n; i++)
                planes[i] = new Plane(new Vector3(flat[i * 4 + 0], flat[i * 4 + 1], flat[i * 4 + 2]), flat[i * 4 + 3]);
            w.Brushes.Add(new Brush { Planes = planes, Contents = contents });
        }
        return w;
    }

    // ---- clip_box_to_brush (verbatim translation of the C) ----
    private void ClipBoxToBrush(ref TraceResult tr, Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, Brush b)
    {
        if (b.Planes.Length == 0) return;
        float enterfrac = -1.0f, leavefrac = 1.0f;
        Vector3 clipnormal = Vector3.Zero;
        bool startout = false, endout = false;
        for (int i = 0; i < b.Planes.Length; i++)
        {
            Vector3 n = b.Planes[i].Normal;
            float d = b.Planes[i].Dist;
            d -= (n.X > 0 ? n.X * mins.X : n.X * maxs.X);
            d -= (n.Y > 0 ? n.Y * mins.Y : n.Y * maxs.Y);
            d -= (n.Z > 0 ? n.Z * mins.Z : n.Z * maxs.Z);
            float d1 = Vector3.Dot(n, start) - d;
            float d2 = Vector3.Dot(n, end) - d;
            if (d1 > 0) startout = true;
            if (d2 > 0) endout = true;
            if (d1 > 0 && d2 >= 0) return;          // wholly outside -> no hit
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
            if (!endout) tr.AllSolid = true;
            if ((b.Contents & ContSolid) != 0) tr.DpHitContents |= b.Contents;
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
            }
        }
    }

    /// <summary>tracebox vs the world's SOLID brushes (mirrors <c>world_trace</c>).</summary>
    public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end)
    {
        var tr = new TraceResult { Fraction = 1.0f, EndPos = end, PlaneNormal = Vector3.Zero };
        foreach (Brush b in Brushes)
        {
            if ((b.Contents & ContSolid) == 0) continue;
            ClipBoxToBrush(ref tr, start, end, mins, maxs, b);
            if (tr.AllSolid) tr.Fraction = 0.0f;
        }
        if (tr.StartSolid) tr.Fraction = tr.AllSolid ? 0.0f : tr.Fraction;
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

/// <summary>An <see cref="ITraceService"/> over an <see cref="AnalyticWorld"/>.</summary>
public sealed class AnalyticTraceService : ITraceService
{
    private readonly AnalyticWorld _world;
    public AnalyticTraceService(AnalyticWorld world) => _world = world;

    public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
        => _world.Trace(start, mins, maxs, end);

    public int PointContents(Vector3 point) => _world.PointContents(point);
    public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
}

/// <summary>A settable clock for the parity sim (engine advances <c>time</c> by dt each frame).</summary>
public sealed class MutableClock : IGameClock
{
    public float Time { get; set; }
    public float FrameTime { get; set; }
}

/// <summary>
/// A minimal <see cref="IEngineServices"/> for the movement parity tests: the analytic trace + a settable
/// clock + an empty cvar store (so <c>MovementParameters.FromCvars</c> falls back to the stock Xonotic
/// defaults, matching the C reference). Entities/Sound/Models are unused by the player physics.
/// </summary>
public sealed class MovementTestServices : IEngineServices
{
    public ITraceService Trace { get; }
    public IGameClock Clock { get; }
    public ICvarService Cvars { get; } = new EmptyCvars();
    public IEntityService Entities { get; } = new MinimalEntities();
    public ISoundService Sound { get; } = new NullSound();
    public IModelService Models { get; } = new NullModels();

    public MovementTestServices(AnalyticWorld world, MutableClock clock)
    {
        Trace = new AnalyticTraceService(world);
        Clock = clock;
    }

    private sealed class EmptyCvars : ICvarService
    {
        public float GetFloat(string name) => 0f;       // unset -> physics uses the stock defaults
        public string GetString(string name) => "";
        public void Set(string name, string value) { }
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
    }
    // A minimal working entity service. The player physics never touches it, but Api.Services is a process
    // global other tests read after this one runs, so it must behave benignly (never throw) when leaked.
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
