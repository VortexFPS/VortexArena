using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Soft player collision (PORT EXTENSION <c>sv_player_softcollision</c>): the movement-hull pass-through
/// (<see cref="MoveFilter.NoPlayers"/> — players slip through each other, monsters/world still clip) and the
/// server's post-move <see cref="PlayerSeparation"/> pass (overlapping bodies slide apart horizontally until
/// they rest side by side). Touches the ambient <see cref="Api"/> globals → serialized collection.
/// </summary>
[Collection("GlobalState")]
public class PlayerSoftCollisionTests
{
    private static readonly Vector3 HullMins = new(-16f, -16f, -24f);
    private static readonly Vector3 HullMaxs = new(16f, 16f, 45f);

    private static Entity SpawnBody(EngineServices svc, Vector3 origin, EntFlags flags = EntFlags.Client)
    {
        Entity e = svc.EntityTable.Spawn();
        e.Origin = origin;
        e.Mins = HullMins;
        e.Maxs = HullMaxs;
        e.Solid = Solid.SlideBox;
        e.Flags |= flags;
        svc.EntityTable.LinkEdict(e);
        return e;
    }

    // =============================================================================================
    // MoveFilter.NoPlayers — the trace-level pass-through
    // =============================================================================================

    [Fact]
    public void NoPlayers_Filter_Skips_Players_But_Normal_Clips_Them()
    {
        var svc = new EngineServices(new CollisionWorld()); // empty world — only the entity sweep can clip
        Api.Services = svc;

        Entity mover = SpawnBody(svc, new Vector3(0, 0, 0));
        SpawnBody(svc, new Vector3(64, 0, 0)); // another player straight ahead

        // Stock filter: the other player's body blocks the hull move.
        TraceResult solid = svc.TraceImpl.Trace(mover.Origin, HullMins, HullMaxs,
            mover.Origin + new Vector3(128, 0, 0), MoveFilter.Normal, mover);
        Assert.True(solid.Fraction < 1f);

        // Soft-collision filter: the player never blocks — the move completes.
        TraceResult soft = svc.TraceImpl.Trace(mover.Origin, HullMins, HullMaxs,
            mover.Origin + new Vector3(128, 0, 0), MoveFilter.NoPlayers, mover);
        Assert.Equal(1f, soft.Fraction);
    }

    [Fact]
    public void NoPlayers_Filter_Still_Clips_Monsters()
    {
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;

        Entity mover = SpawnBody(svc, new Vector3(0, 0, 0));
        // A monster is SLIDEBOX + FL_MONSTER (no FL_CLIENT) — soft collision must NOT walk through it.
        SpawnBody(svc, new Vector3(64, 0, 0), flags: EntFlags.Monster);

        TraceResult tr = svc.TraceImpl.Trace(mover.Origin, HullMins, HullMaxs,
            mover.Origin + new Vector3(128, 0, 0), MoveFilter.NoPlayers, mover);
        Assert.True(tr.Fraction < 1f);
    }

    // =============================================================================================
    // PlayerSeparation — the post-move push-apart pass
    // =============================================================================================

    private const float Dt = 1f / 72f; // server tick

    private static void RunTicks(EngineServices svc, Entity[] players, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            PlayerSeparation.Run(players, Dt, svc.EntityTable.LinkEdict);
    }

    [Fact]
    public void Overlapping_Players_Slide_Apart_And_Settle_Adjacent()
    {
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;

        Entity a = SpawnBody(svc, new Vector3(0, 0, 0));
        Entity b = SpawnBody(svc, new Vector3(4, 0, 0)); // deep overlap along X

        RunTicks(svc, new[] { a, b }, 300); // plenty — settles in ~1s of sim time

        float sep = System.MathF.Abs(b.Origin.X - a.Origin.X);
        Assert.True(sep >= 32f - 0.01f, $"hulls still overlap: X separation {sep}");   // 32 = hull width
        Assert.True(sep <= 33f, $"overshot past contact: X separation {sep}");         // ease-out, no fling
        // Symmetric: both bodies moved, the pair midpoint stays put.
        Assert.Equal(4f, a.Origin.X + b.Origin.X, 2);
        // Horizontal-only: nobody gets boosted vertically, and the off-axis stays put.
        Assert.Equal(0f, a.Origin.Z); Assert.Equal(0f, b.Origin.Z);
        Assert.Equal(0f, a.Origin.Y, 3); Assert.Equal(0f, b.Origin.Y, 3);
    }

    [Fact]
    public void Fully_Colocated_Players_Resolve_Deterministically()
    {
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;

        Entity a = SpawnBody(svc, new Vector3(100, 100, 0));
        Entity b = SpawnBody(svc, new Vector3(100, 100, 0)); // telefrag-style: same spot

        RunTicks(svc, new[] { a, b }, 300);

        // Separated on SOME horizontal axis (the pair-keyed tiebreak direction), never vertically.
        float dx = System.MathF.Abs(b.Origin.X - a.Origin.X);
        float dy = System.MathF.Abs(b.Origin.Y - a.Origin.Y);
        Assert.True(dx >= 32f - 0.01f || dy >= 32f - 0.01f, $"still co-located: dx={dx} dy={dy}");
        Assert.Equal(0f, a.Origin.Z); Assert.Equal(0f, b.Origin.Z);
    }

    [Fact]
    public void Separation_Respects_The_Cvar_Off_Switch()
    {
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;
        Api.Cvars.Set("sv_player_softcollision", "0");

        Entity a = SpawnBody(svc, new Vector3(0, 0, 0));
        Entity b = SpawnBody(svc, new Vector3(4, 0, 0));

        RunTicks(svc, new[] { a, b }, 50);

        Assert.Equal(0f, a.Origin.X); // stock mode: bodies are solid, the pass never moves anyone
        Assert.Equal(4f, b.Origin.X);
    }

    [Fact]
    public void Dead_And_NonPlayer_Bodies_Are_Left_Alone()
    {
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;

        Entity a = SpawnBody(svc, new Vector3(0, 0, 0));
        Entity corpse = SpawnBody(svc, new Vector3(4, 0, 0));
        corpse.DeadState = DeadFlag.Dead;

        RunTicks(svc, new[] { a, corpse }, 50);

        Assert.Equal(0f, a.Origin.X);      // a dead body neither pushes nor gets pushed
        Assert.Equal(4f, corpse.Origin.X);
    }

    [Fact]
    public void Fast_Crossing_Is_Barely_Deflected()
    {
        // The user-facing promise: running THROUGH someone at speed is a slip, not a shove. One tick of
        // overlap displaces each body by at most pushspeed*dt/2 ≈ 1u — negligible against a 360 u/s sprint.
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;

        Entity a = SpawnBody(svc, new Vector3(0, 0, 0));
        Entity b = SpawnBody(svc, new Vector3(4, 0, 0));

        PlayerSeparation.Run(new[] { a, b }, Dt, svc.EntityTable.LinkEdict); // ONE tick of contact

        float moved = System.MathF.Abs(a.Origin.X);
        Assert.True(moved > 0f, "overlap should nudge");
        Assert.True(moved <= PlayerSeparation.DefaultPushSpeed * Dt, $"one tick moved {moved}u — too strong");
    }
}
