using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for T45 — warpzone COMBAT TRAVERSAL (lib/warpzone/common.qc WarpZone_TraceBox/_TraceLine recursion +
/// WarpZone_FindRadius recursion). A hitscan/projectile trace and a radius-damage query cross a chain of seamless
/// portals: the trace continues out of the linked portal, the transform accumulates through the chain, the
/// 16-zone guard caps a pathological chain, and a far-side victim is found by the radius query tagged with the
/// transform back to the blast frame. The transform/teleport half is covered by <see cref="WarpzoneTests"/>.
///
/// Runs in the serialized collection because the radius tests install the ambient <see cref="Api.Services"/>.
/// </summary>
[Collection("GlobalState")]
public class WarpzoneTraceTests
{
    // A 2-zone pair: IN plane at origin facing +X (yaw 0); OUT plane 100 units away facing −X (yaw 180). A ray
    // approaching the IN surface from the +X side (moving −X, against inForward) crosses and emerges at the OUT
    // plane. These zones carry NO trigger entity (Api.Services null), so the recursion uses the plane-slab
    // fallback in WithinTriggerBounds — fine for the pure-geometry tests.
    private static WarpzoneManager TwoZonePair()
    {
        var mgr = new WarpzoneManager();
        mgr.Spawn(new Vector3(0, 0, 0), new Vector3(0, 0, 0), "wzA", "wzB",
            new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Spawn(new Vector3(100, 0, 0), new Vector3(0, 180, 0), "wzB", "wzA",
            new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Link();
        return mgr;
    }

    /// <summary>A plain trace service that never reports a hit (Fraction 1, EndPos == end). It lets the warpzone
    /// recursion drive entirely off the analytic plane-crossing detection.</summary>
    private sealed class MissTrace : ITraceService
    {
        public int Calls;
        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
        {
            Calls++;
            return TraceResult.Miss(end);
        }
        public int PointContents(Vector3 point) => 0;
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    // ---- transform chain composition -------------------------------------------------------------------

    [Fact]
    public void Chain_Identity_Is_NoOp()
    {
        var id = WarpzoneTransformChain.Identity;
        Assert.False(id.HasTransform);
        var p = new Vector3(12, -34, 56);
        Assert.True((id.TransformPoint(p) - p).Length() < 1e-4f);
        Assert.True((id.TransformDirection(p) - p).Length() < 1e-4f);
    }

    [Fact]
    public void Chain_Append_Matches_The_Zone_Transform_For_One_Zone()
    {
        var mgr = TwoZonePair();
        Warpzone a = mgr.Zones[0];
        var chain = WarpzoneTransformChain.Identity.Append(a.Transform);

        // Appending one zone must reproduce that zone's TransformOrigin exactly (the IN plane center maps to OUT).
        var pIn = new Vector3(0, 0, 0);
        Assert.True((chain.TransformPoint(pIn) - a.Transform.TransformOrigin(pIn)).Length() < 0.01f);
        var pArb = new Vector3(7, -11, 3);
        Assert.True((chain.TransformPoint(pArb) - a.Transform.TransformOrigin(pArb)).Length() < 0.01f);
        // and the direction part matches TransformVelocity.
        var d = new Vector3(-1, 2, -3);
        Assert.True((chain.TransformDirection(d) - a.Transform.TransformVelocity(d)).Length() < 0.01f);
    }

    // ---- WarpZone_TraceBox recursion -------------------------------------------------------------------

    [Fact]
    public void TraceLine_Crosses_One_Portal_And_Accumulates_The_Transform()
    {
        var mgr = TwoZonePair();
        var trace = new MissTrace();

        // Ray from (50,0,0) heading −X: crosses the IN plane at the origin (moving against inForward +X), emerges
        // at the OUT plane (100,0,0). The trace reports the far-side endpoint and a one-zone transform.
        WarpzoneTraceResult r = WarpzoneTrace.TraceWarpzone(trace, mgr,
            new Vector3(50, 0, 0), Vector3.Zero, Vector3.Zero, new Vector3(-50, 0, 0),
            MoveFilter.NoMonsters, null);

        Assert.Equal(1, r.ZonesCrossed);
        Assert.True(r.Transform.HasTransform);
        // The transform must map the crossing point (origin) to the OUT plane center (100,0,0).
        Assert.True((r.Transform.TransformPoint(new Vector3(0, 0, 0)) - new Vector3(100, 0, 0)).Length() < 0.5f);
    }

    [Fact]
    public void TraceLine_No_Portal_On_The_Segment_Is_A_Plain_Trace()
    {
        var mgr = TwoZonePair();
        var trace = new MissTrace();

        // Ray that runs parallel to the IN plane far off to the side — never crosses a portal.
        WarpzoneTraceResult r = WarpzoneTrace.TraceWarpzone(trace, mgr,
            new Vector3(0, 500, 0), Vector3.Zero, Vector3.Zero, new Vector3(0, 500, 100),
            MoveFilter.NoMonsters, null);

        Assert.Equal(0, r.ZonesCrossed);
        Assert.False(r.Transform.HasTransform);
        Assert.Equal(1, trace.Calls); // exactly one plain sweep
    }

    [Fact]
    public void TraceLine_No_Manager_Is_A_Single_Plain_Trace()
    {
        var trace = new MissTrace();
        WarpzoneTraceResult r = WarpzoneTrace.TraceWarpzone(trace, null,
            new Vector3(50, 0, 0), Vector3.Zero, Vector3.Zero, new Vector3(-50, 0, 0),
            MoveFilter.NoMonsters, null);
        Assert.Equal(0, r.ZonesCrossed);
        Assert.Equal(1, trace.Calls);
    }

    // ---- 16-zone guard ---------------------------------------------------------------------------------

    [Fact]
    public void TraceLine_Pathological_Self_Recrossing_Chain_Stops_At_The_Guard()
    {
        // A pair of zones whose OUT plane sits BEHIND the IN plane of the other, arranged so a long −X ray could
        // in principle re-cross endlessly if the guard were absent. Two stacked pairs along −X. The 16-zone cap
        // must terminate the recursion and return (no hang).
        var mgr = new WarpzoneManager();
        // zone pair that warps −X travel back to a point that is again in front of another zone, repeatedly.
        for (int k = 0; k < 8; k++)
        {
            float x = -k * 10f;
            mgr.Spawn(new Vector3(x, 0, 0), new Vector3(0, 0, 0), $"in{k}", $"out{k}",
                new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
            mgr.Spawn(new Vector3(x - 1f, 0, 0), new Vector3(0, 180, 0), $"out{k}", $"in{k}",
                new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        }
        mgr.Link();
        var trace = new MissTrace();

        // Long −X ray that keeps finding the next zone in front of it. Must terminate.
        WarpzoneTraceResult r = WarpzoneTrace.TraceWarpzone(trace, mgr,
            new Vector3(100, 0, 0), Vector3.Zero, Vector3.Zero, new Vector3(-1000, 0, 0),
            MoveFilter.NoMonsters, null);

        // The recursion is hard-capped at MaxZoneDepth; the call returns rather than hanging, and never exceeds it.
        Assert.True(r.ZonesCrossed <= WarpzoneTrace.MaxZoneDepth);
        Assert.True(trace.Calls <= WarpzoneTrace.MaxZoneDepth + 1);
    }

    // ---- WarpZone_FindRadius recursion -----------------------------------------------------------------

    [Fact]
    public void FindRadius_No_Warpzones_Is_The_Plain_FindRadius_With_Identity()
    {
        var near = new Entity { ClassName = "rocket", Origin = new Vector3(10, 0, 0) };
        var far = new Entity { ClassName = "rocket", Origin = new Vector3(500, 0, 0) };
        using var _ = new RadiusEnv(new[] { near, far });

        var hits = new List<WarpzoneRadiusHit>();
        WarpzoneRadiusQuery.FindRadiusWarpzone(null, Vector3.Zero, 100f, hits);

        Assert.Single(hits);
        Assert.Same(near, hits[0].Entity);
        Assert.False(hits[0].ToBlastFrame.HasTransform);
        Assert.True((hits[0].LocalBlastOrigin - Vector3.Zero).Length() < 1e-4f);
    }

    [Fact]
    public void FindRadius_Reaches_A_Victim_Only_Through_A_Portal()
    {
        var mgr = TwoZonePair();
        // The victim sits at (150,0,0): 100qu straight-line from the blast at (50,0,0) — OUTSIDE the 80qu radius,
        // so the straight-line search misses it. But the portal mouth (origin) is 50qu away (within 80), and the
        // blast warped through it lands at exactly (150,0,0) (outOrigin 100 + Rotate((50,0,0)) = 100 + 50 = 150),
        // with remaining radius 80 − 50 = 30 — so the PORTAL path finds the victim. This proves the recursion
        // reaches a victim ONLY reachable through the seam (QC WarpZone_FindRadius_Recurse).
        var victim = new Entity { ClassName = "player", Origin = new Vector3(150, 0, 0),
            Mins = new Vector3(-16, -16, -24), Maxs = new Vector3(16, 16, 45) };
        using var _ = new RadiusEnv(new[] { victim });

        var hits = new List<WarpzoneRadiusHit>();
        WarpzoneRadiusQuery.FindRadiusWarpzone(mgr, new Vector3(50, 0, 0), 80f, hits);

        Assert.Contains(hits, h => ReferenceEquals(h.Entity, victim));
        WarpzoneRadiusHit hv = hits.Find(h => ReferenceEquals(h.Entity, victim));
        // Reached through the portal → its hit carries the accumulated portal transform and the portal-shifted
        // blast origin (150,0,0), not the original (50,0,0).
        Assert.True(hv.ToBlastFrame.HasTransform);
        Assert.True((hv.LocalBlastOrigin - new Vector3(150, 0, 0)).Length() < 1f);
    }

    [Fact]
    public void FindRadius_Skips_Blacklisted_Classnames()
    {
        var wp = new Entity { ClassName = "waypoint", Origin = new Vector3(5, 0, 0) };
        var info = new Entity { ClassName = "info_player_deathmatch", Origin = new Vector3(6, 0, 0) };
        var real = new Entity { ClassName = "player", Origin = new Vector3(7, 0, 0) };
        using var _ = new RadiusEnv(new[] { wp, info, real });

        var hits = new List<WarpzoneRadiusHit>();
        WarpzoneRadiusQuery.FindRadiusWarpzone(null, Vector3.Zero, 100f, hits);

        Assert.Single(hits);
        Assert.Same(real, hits[0].Entity);
    }

    // ---- test scaffolding ------------------------------------------------------------------------------

    /// <summary>Installs <see cref="Api.Services"/> with an entity service whose findradius returns a fixed set
    /// (filtered by radius); restores on dispose. The trace/sound/etc. are benign nulls.</summary>
    private sealed class RadiusEnv : System.IDisposable
    {
        private readonly IEngineServices _prev;
        public RadiusEnv(IReadOnlyList<Entity> world)
        {
            _prev = Api.Services;
            Api.Services = new RadiusServices(world);
        }
        public void Dispose() => Api.Services = _prev;
    }

    private sealed class RadiusServices : IEngineServices
    {
        public ITraceService Trace { get; } = new MissTrace();
        public IGameClock Clock { get; } = new ZeroClock();
        public ICvarService Cvars { get; } = new EmptyCvars();
        public IEntityService Entities { get; }
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();
        public RadiusServices(IReadOnlyList<Entity> world) => Entities = new RadiusEntities(world);

        private sealed class ZeroClock : IGameClock { public float Time => 0f; public float FrameTime => 1f / 72f; }
        private sealed class EmptyCvars : ICvarService
        {
            public float GetFloat(string name) => 0f;
            public string GetString(string name) => "";
            public void Set(string name, string value) { }
            public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
        }
        private sealed class RadiusEntities : IEntityService
        {
            private readonly IReadOnlyList<Entity> _world;
            public RadiusEntities(IReadOnlyList<Entity> world) => _world = world;
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius)
            {
                foreach (Entity e in _world)
                    if (!e.IsFreed && (e.Origin - origin).Length() <= radius)
                        yield return e;
            }
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

    // ---- real data: a shot THROUGH stormkeep's actual warpzone (the live "attacks don't work" report) --------

    [Fact]
    public void Stormkeep_HitscanTrace_CrossesTheRealWarpzone()
    {
        const string dataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";
        if (!System.IO.Directory.Exists(dataDir)) return;
        using var vfs = new XonoticGodot.Formats.Vfs.VirtualFileSystem();
        if (!vfs.MountGameDir(dataDir)) return;
        string? path = System.Linq.Enumerable.FirstOrDefault(
            vfs.Find("maps/", "bsp"), p => p.Contains("stormkeep"));
        if (path is null) return;   // map not in this checkout — skip

        XonoticGodot.Formats.Bsp.BspData bsp = XonoticGodot.Formats.Bsp.BspReader.Read(vfs.ReadBytes(path));
        var trace = new XonoticGodot.Engine.Collision.TraceService(
            XonoticGodot.Engine.Collision.BspCollisionBuilder.Build(bsp).World);

        // The REAL zone pair (BSP ground truth, entities *5/*6): A's window plane at (1216,208,32) facing +Y
        // (the room north of it), B's at (-592,224,80) facing -X (the room west of it). POJO zones (no trigger
        // edict, Api.Services null) use the plane-slab bounds — the same 128x128 window either way.
        var mgr = new WarpzoneManager();
        mgr.Spawn(new Vector3(1216, 208, 32), new Vector3(0, 90, 0), "wzA", "wzB",
            new Vector3(-64, -64, -64), new Vector3(64, 64, 64));
        mgr.Spawn(new Vector3(-592, 224, 80), new Vector3(0, 180, 0), "wzB", "wzA",
            new Vector3(-64, -64, -64), new Vector3(64, 64, 64));
        mgr.Link();

        // A shot fired from the room straight into A's window must CROSS the zone and continue in B's frame —
        // the live report was hitscan stopping at the window like a wall.
        WarpzoneTraceResult r = WarpzoneTrace.TraceWarpzone(trace, mgr,
            new Vector3(1216, 400, 32), Vector3.Zero, Vector3.Zero, new Vector3(1216, -400, 32),
            MoveFilter.Normal, null);

        Assert.True(r.ZonesCrossed >= 1, $"the shot must cross the zone (crossed={r.ZonesCrossed}, end={r.Trace.EndPos})");
        Assert.True(r.Trace.EndPos.X < -580f, $"the shot must continue on B's side, ended at {r.Trace.EndPos}");
    }
}
