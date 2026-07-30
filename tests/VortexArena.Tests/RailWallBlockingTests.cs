using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// The rail beam must STOP at world geometry (Base <c>FireRailgunBullet</c>, server/weapons/tracing.qc:
/// the pierce loop only makes <c>SOLID_SLIDEBOX</c> entities non-solid, so any world surface terminates it).
/// Regression cover for "Vortex shots go through walls": a target standing behind a solid brush must take
/// zero damage, and the beam must not reach past the wall no matter what is standing in front of it.
/// </summary>
[Collection("GlobalState")]
public class RailWallBlockingTests
{
    private const float WallX = 200f;    // solid slab spanning X[200,232]
    private const float TargetX = 400f;  // victim well behind the wall
    private const int RailDeathType = 1; // any weapon id — only the damage bookkeeping reads it

    private sealed class Harness
    {
        public Entity Shooter = null!;
        public Entity Target = null!;
    }

    /// <summary>Floor + a full-height solid wall across the beam path, with a shooter and a target behind it.</summary>
    private static Harness Build(bool withWall = true)
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f),
            SuperContents.Solid));
        if (withWall)
            world.AddBrush(Brush.FromBox(new Vector3(WallX, -512f, 0f), new Vector3(WallX + 32f, 512f, 512f),
                SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services);

        var h = new Harness();

        h.Shooter = Api.Entities.Spawn();
        h.Shooter.ClassName = "player";
        h.Shooter.Flags = EntFlags.Client;
        h.Shooter.TakeDamage = DamageMode.Yes;
        h.Shooter.Solid = Solid.SlideBox;
        h.Shooter.Mins = new Vector3(-16f, -16f, -24f);
        h.Shooter.Maxs = new Vector3(16f, 16f, 45f);
        h.Shooter.ViewOfs = new Vector3(0f, 0f, 20f);
        h.Shooter.SetResource(ResourceType.Health, 100f);
        Api.Entities.SetOrigin(h.Shooter, new Vector3(0f, 0f, 24f));

        h.Target = Api.Entities.Spawn();
        h.Target.ClassName = "player";
        h.Target.Flags = EntFlags.Client;
        h.Target.TakeDamage = DamageMode.Yes;
        h.Target.Solid = Solid.SlideBox;
        h.Target.Mins = new Vector3(-16f, -16f, -24f);
        h.Target.Maxs = new Vector3(16f, 16f, 45f);
        h.Target.ViewOfs = new Vector3(0f, 0f, 20f);
        h.Target.SetResource(ResourceType.Health, 200f);
        Api.Entities.SetOrigin(h.Target, new Vector3(TargetX, 0f, 24f));

        return h;
    }

    private static float HealthOf(Entity e) => e.GetResource(ResourceType.Health);

    /// <summary>Eye-height beam from the shooter straight down +X toward (and past) the target.</summary>
    private static Entity? FireDownRange(Harness h, float damage = 80f)
    {
        Vector3 start = h.Shooter.Origin + h.Shooter.ViewOfs;
        Vector3 end = start + new Vector3(1f, 0f, 0f) * 4096f;
        return WeaponFiring.FireRailgunBullet(h.Shooter, start, end, damage, RailDeathType, force: 0f);
    }

    // ---- the bug ----------------------------------------------------------------------------------

    [Fact]
    public void Rail_Does_Not_Damage_A_Target_Behind_A_Wall()
    {
        Harness h = Build();
        float before = HealthOf(h.Target);

        Entity? hit = FireDownRange(h);

        Assert.Null(hit);
        Assert.Equal(before, HealthOf(h.Target));
    }

    /// <summary>
    /// The pierce loop makes each hit entity temporarily non-solid to reach the next one. A player standing in
    /// FRONT of the wall must not let the beam continue past the wall behind them — Base stops the loop at the
    /// first non-SLIDEBOX surface.
    /// </summary>
    [Fact]
    public void Rail_Stops_At_The_Wall_Even_After_Piercing_A_Player()
    {
        Harness h = Build();

        Entity blocker = Api.Entities.Spawn();
        blocker.ClassName = "player";
        blocker.Flags = EntFlags.Client;
        blocker.TakeDamage = DamageMode.Yes;
        blocker.Solid = Solid.SlideBox;
        blocker.Mins = new Vector3(-16f, -16f, -24f);
        blocker.Maxs = new Vector3(16f, 16f, 45f);
        blocker.ViewOfs = new Vector3(0f, 0f, 20f);
        blocker.SetResource(ResourceType.Health, 200f);
        Api.Entities.SetOrigin(blocker, new Vector3(100f, 0f, 24f)); // between shooter and wall

        float targetBefore = HealthOf(h.Target);
        float blockerBefore = HealthOf(blocker);

        Entity? hit = FireDownRange(h);

        Assert.Same(blocker, hit);                              // the blocker IS hit
        Assert.True(HealthOf(blocker) < blockerBefore, "the blocker should take the rail damage");
        Assert.Equal(targetBefore, HealthOf(h.Target));         // ...but nothing behind the wall
    }

    // ---- the control: without the wall the same shot must connect --------------------------------

    [Fact]
    public void Rail_Damages_A_Target_With_No_Wall_In_The_Way()
    {
        Harness h = Build(withWall: false);
        float before = HealthOf(h.Target);

        Entity? hit = FireDownRange(h);

        Assert.Same(h.Target, hit);
        Assert.True(HealthOf(h.Target) < before, "an unobstructed rail should damage the target");
    }

    // ---- the shot ORIGIN must never end up past the wall -----------------------------------------

    /// <summary>
    /// Base <c>W_SetupShot_Dir_ProjectileSize_Range</c> traces the muzzle offset out from the eye and then pulls
    /// the result back by <c>nudge</c> (tracing.qc: <c>tracebox(..., w_shotorg + forward * (md.x + nudge), ...);
    /// w_shotorg = trace_endpos - forward * nudge;</c>). Standing with the gun against a wall, the shot must
    /// therefore start just INSIDE the room, never on or past the surface — otherwise the beam is born in/behind
    /// the wall and hits whatever is on the far side. Shooter's eye is 12u from the wall; the muzzle offset is
    /// further than that, so the trace clamps.
    /// </summary>
    [Fact]
    public void ShotOrigin_Stays_On_The_Near_Side_When_Muzzle_Pushes_Into_A_Wall()
    {
        Harness h = Build();
        Api.Entities.SetOrigin(h.Shooter, new Vector3(WallX - 12f, 0f, 24f)); // eye 12u from the wall face

        ShotInfo shot = WeaponFiring.SetupShot(h.Shooter, new Vector3(1f, 0f, 0f), wep: null, maxDamage: 0f);

        Assert.True(shot.Origin.X < WallX,
            $"shot origin {shot.Origin.X:0.###} must stay in front of the wall at X={WallX}");
    }

    /// <summary>The end-to-end symptom: firing the Vortex with your muzzle in a wall must not hit through it.</summary>
    [Fact]
    public void Rail_From_A_Muzzle_Against_A_Wall_Does_Not_Hit_Through_It()
    {
        Harness h = Build();
        Api.Entities.SetOrigin(h.Shooter, new Vector3(WallX - 12f, 0f, 24f));
        float before = HealthOf(h.Target);

        ShotInfo shot = WeaponFiring.SetupShot(h.Shooter, new Vector3(1f, 0f, 0f), wep: null, maxDamage: 0f);
        Entity? hit = WeaponFiring.FireRailgunBullet(h.Shooter, shot.Origin,
            shot.Origin + shot.Dir * 4096f, 80f, RailDeathType, force: 0f);

        Assert.Null(hit);
        Assert.Equal(before, HealthOf(h.Target));
    }

    // ---- the beam VISUAL must stop where the damage stops ----------------------------------------

    /// <summary>
    /// The beam/impact endpoint (<see cref="WeaponFiring.HitscanImpactTrace"/>) has to stop at brush ENTITIES —
    /// <c>func_door</c>, <c>func_wall</c>, <c>func_plat</c>, breakables — not just the static world. It used to
    /// trace <c>MOVE_WORLDONLY</c>, which clips against the static world and nothing else, so the rail beam
    /// speared visibly through every door and its impact burst landed behind it while the damage correctly
    /// stopped at the door: "the shot went through the wall".
    /// </summary>
    [Fact]
    public void BeamImpact_Stops_At_A_Brush_Entity_Door()
    {
        Harness h = Build(withWall: false);   // no static wall — the only obstruction is the brush entity

        Entity door = Api.Entities.Spawn();
        door.ClassName = "func_door";
        door.Solid = Solid.Bsp;
        door.Mins = new Vector3(-16f, -256f, -256f);
        door.Maxs = new Vector3(16f, 256f, 256f);
        Api.Entities.SetOrigin(door, new Vector3(WallX, 0f, 24f));

        Vector3 start = h.Shooter.Origin + h.Shooter.ViewOfs;
        Vector3 end = start + new Vector3(1f, 0f, 0f) * 4096f;

        TraceResult imp = WeaponFiring.HitscanImpactTrace(h.Shooter, start, end).Trace;

        Assert.True(imp.EndPos.X <= WallX + 1f,
            $"beam/impact ended at X={imp.EndPos.X:0.###}, past the door face at X={WallX - 16f}..{WallX + 16f}");
    }

    /// <summary>...and the damage stops there too, so visual and damage agree (Base terminates on any
    /// non-SLIDEBOX solid).</summary>
    [Fact]
    public void Rail_Does_Not_Damage_Through_A_Brush_Entity_Door()
    {
        Harness h = Build(withWall: false);

        Entity door = Api.Entities.Spawn();
        door.ClassName = "func_door";
        door.Solid = Solid.Bsp;
        door.Mins = new Vector3(-16f, -256f, -256f);
        door.Maxs = new Vector3(16f, 256f, 256f);
        Api.Entities.SetOrigin(door, new Vector3(WallX, 0f, 24f));

        float before = HealthOf(h.Target);
        FireDownRange(h);

        Assert.Equal(before, HealthOf(h.Target));
    }

    /// <summary>Base pierces players: two stacked targets in the open both take the hit.</summary>
    [Fact]
    public void Rail_Pierces_Multiple_Players_When_Unobstructed()
    {
        Harness h = Build(withWall: false);

        Entity second = Api.Entities.Spawn();
        second.ClassName = "player";
        second.Flags = EntFlags.Client;
        second.TakeDamage = DamageMode.Yes;
        second.Solid = Solid.SlideBox;
        second.Mins = new Vector3(-16f, -16f, -24f);
        second.Maxs = new Vector3(16f, 16f, 45f);
        second.ViewOfs = new Vector3(0f, 0f, 20f);
        second.SetResource(ResourceType.Health, 200f);
        Api.Entities.SetOrigin(second, new Vector3(600f, 0f, 24f));

        float firstBefore = HealthOf(h.Target);
        float secondBefore = HealthOf(second);

        FireDownRange(h);

        Assert.True(HealthOf(h.Target) < firstBefore, "the near target should be hit");
        Assert.True(HealthOf(second) < secondBefore, "the far target should be pierced too");
    }
}
