using System;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for the turret aim/track pipeline (<see cref="TurretAI.AimPoint"/> /
/// <see cref="TurretAI.Track"/>, ports of <c>turret_aim_generic</c> / <c>turret_track</c> in
/// common/turrets/sv_turrets.qc:12-62). Pins the TFL_AIM_* lead pipeline (SIMPLE = current position;
/// LEAD = +vel*mintime; +SHOTTIMECOMPENSATE = +vel*(dist/shot_speed + mintime); ZPREDICT integrates
/// the gravity arc per sys_frametime step; SPLASH traces 32-up/64-down to the floor) and the stepmotor
/// slew-rate/per-axis clamps. The clock is driven by a real <see cref="SimulationLoop"/> so frametime
/// expectations come from the live <see cref="Api.Clock"/>, never hardcoded (avoids time flakiness).
/// </summary>
[Collection("GlobalState")]
public class TurretAimTrackTests
{
    private sealed class Harness
    {
        public SimulationLoop Sim = null!;
    }

    private static Harness Boot()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        var services = new EngineServices(world);
        GameInit.Boot(services);
        Prandom.Seed(0xC0FFEE);

        var sim = new SimulationLoop(services, world) { Gravity = 800f };
        // warm the published clock past zero (the QC-facing clock lags one tick)
        sim.Tick();
        sim.Tick();
        Assert.True(Api.Clock.Time > 0f);
        Assert.True(Api.Clock.FrameTime > 0f);
        return new Harness { Sim = sim };
    }

    private static Entity SpawnTurret(Vector3 origin)
    {
        Entity t = Api.Entities.Spawn();
        t.ClassName = "turret_test";
        t.Team = float.MaxValue;
        t.Origin = origin;
        Api.Entities.SetOrigin(t, origin);
        TurretAI.State(t).ShotOrg = origin;   // the muzzle the lead math measures from
        return t;
    }

    private static Entity SpawnTarget(Vector3 origin, Vector3 velocity, bool onGround = true)
    {
        Entity e = Api.Entities.Spawn();
        e.ClassName = "player";
        e.Flags |= EntFlags.Client;
        if (onGround) e.Flags |= EntFlags.OnGround;
        e.TakeDamage = DamageMode.Aim;
        e.Health = 100f;
        e.MoveType = MoveType.Walk;
        e.Velocity = velocity;
        e.Origin = origin;
        Api.Entities.SetOrigin(e, origin);
        return e;
    }

    private static TurretParams Aim(
        bool simple = false, bool lead = false, bool compensate = false, bool zPredict = false,
        bool splash = false, float shotSpeed = 0f, float aimSpeed = 36f,
        float aimMaxPitch = 20f, float aimMaxRot = 90f, int trackType = TurretAI.TrackStepMotor)
        => new(TurretAI.SelectPlayers, 0.1f, 5000f, shotDamage: 10f, refire: 1f,
            aimSpeed: aimSpeed, fireToleranceDist: 50f, lead: lead,
            shotSpeed: shotSpeed, aimMaxPitch: aimMaxPitch, aimMaxRot: aimMaxRot,
            shotTimeCompensate: compensate, zPredict: zPredict, aimSplash: splash, aimSimple: simple,
            trackType: trackType);

    // ---------------------------------------------------------------- AimPoint

    [Fact]
    public void AimSimple_ReturnsTheTargetCenterUnled()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(500, 0, 50), new Vector3(300, 0, 0));

        TurretParams p = Aim(simple: true);
        Assert.Equal(target.Origin, TurretAI.AimPoint(turret, target, in p));
    }

    [Fact]
    public void Lead_AddsVelocityTimesMinTime()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(500, 0, 50), new Vector3(300, 0, 0));
        TurretAI.State(turret).AttackFinished = 0f;   // cooled down: mintime = sys_frametime only

        float ft = Api.Clock.FrameTime;
        TurretParams p = Aim(lead: true);
        Vector3 aim = TurretAI.AimPoint(turret, target, in p);

        Vector3 expected = target.Origin + target.Velocity * ft;
        Assert.Equal(expected.X, aim.X, 3);
        Assert.Equal(expected.Y, aim.Y, 3);
    }

    [Fact]
    public void Lead_PendingRefire_ExtendsTheLeadWindow()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(500, 0, 50), new Vector3(300, 0, 0));

        float now = Api.Clock.Time;
        float ft = Api.Clock.FrameTime;
        TurretAI.State(turret).AttackFinished = now + 0.5f;   // refire still pending for 0.5s

        TurretParams p = Aim(lead: true);
        Vector3 aim = TurretAI.AimPoint(turret, target, in p);

        Vector3 expected = target.Origin + target.Velocity * (0.5f + ft);
        Assert.Equal(expected.X, aim.X, 2);
    }

    [Fact]
    public void ShotTimeCompensate_LeadsByTravelTimePlusMinTime()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(1000, 0, 50), new Vector3(0, 200, 0));
        TurretAI.State(turret).AttackFinished = 0f;

        const float shotSpeed = 2500f;
        float ft = Api.Clock.FrameTime;
        TurretParams p = Aim(lead: true, compensate: true, shotSpeed: shotSpeed);
        Vector3 aim = TurretAI.AimPoint(turret, target, in p);

        float impactTime = (target.Origin - turret.Origin).Length() / shotSpeed; // 1000/2500 = 0.4
        Vector3 expected = target.Origin + target.Velocity * (impactTime + ft);
        Assert.Equal(expected.Y, aim.Y, 2);
        Assert.Equal(expected.X, aim.X, 2);
    }

    [Fact]
    public void ZPredict_IntegratesTheGravityArcOfAnAirborneTarget()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        // airborne (NOT onground), gravity-affected movetype, falling at -100
        Entity target = SpawnTarget(new Vector3(1000, 0, 400), new Vector3(0, 0, -100), onGround: false);
        TurretAI.State(turret).AttackFinished = 0f;

        const float shotSpeed = 1000f;
        float ft = Api.Clock.FrameTime;
        TurretParams p = Aim(lead: true, compensate: true, zPredict: true, shotSpeed: shotSpeed);
        Vector3 aim = TurretAI.AimPoint(turret, target, in p);

        // replicate the QC integration exactly: z starts at the CURRENT z and falls per frame step
        float impactTime = (target.Origin - turret.Origin).Length() / shotSpeed;
        float expectedZ = target.Origin.Z;
        float vz = target.Velocity.Z;
        for (float t = 0f; t < impactTime; t += ft)
        {
            vz -= TurretAI.Gravity * ft;
            expectedZ += vz * ft;
        }
        Assert.Equal(expectedZ, aim.Z, 2);
        Assert.True(aim.Z < target.Origin.Z, "a falling target must be led DOWNWARD");
    }

    [Fact]
    public void AimSplash_DropsTheAimPointToTheFloor()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        // target hovering 30u above the floor (floor top is z=0); the 32-up/64-down trace hits the floor
        Entity target = SpawnTarget(new Vector3(400, 0, 30), Vector3.Zero);

        TurretParams p = Aim(splash: true);
        Vector3 aim = TurretAI.AimPoint(turret, target, in p);

        Assert.Equal(400f, aim.X, 1);
        Assert.Equal(0f, aim.Z, 0);   // pulled down to the floor plane
        Assert.True(aim.Z < target.Origin.Z);
    }

    // ---------------------------------------------------------------- Track (stepmotor)

    [Fact]
    public void Track_StepMotor_SlewsAtMostAimSpeedPerSecond()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(0, 1000, 50), Vector3.Zero);  // 90 deg of yaw away
        turret.Enemy = target;
        TurretState st = TurretAI.State(turret);
        st.AimPos = target.Origin;

        const float aimSpeed = 36f;
        float ft = Api.Clock.FrameTime;
        TurretParams p = Aim(aimSpeed: aimSpeed, aimMaxRot: 180f, trackType: TurretAI.TrackStepMotor);

        TurretAI.Track(turret, in p);

        // one tick moves the head exactly aimSpeed * frametime toward the 90-degree solution
        float yawMagnitude = MathF.Abs(st.HeadAngles.Y);
        Assert.Equal(aimSpeed * ft, yawMagnitude, 3);
        Assert.Equal(0f, st.HeadAngles.Z, 4);
    }

    [Fact]
    public void Track_StepMotor_ConvergesOnTheFiringSolution()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(0, 1000, 50), Vector3.Zero);
        turret.Enemy = target;
        TurretState st = TurretAI.State(turret);
        st.AimPos = target.Origin;

        TurretParams p = Aim(aimSpeed: 360f, aimMaxRot: 180f, trackType: TurretAI.TrackStepMotor);
        for (int i = 0; i < 200; i++)
            TurretAI.Track(turret, in p);

        // converged: the head's world yaw matches the bearing to the target (compare via QMath, not signs)
        Vector3 want = QMath.VecToAngles(QMath.Normalize(st.AimPos - st.ShotOrg));
        Vector3 head = TurretAI.HeadWorldAngles(turret);
        Assert.Equal(TurretMath.AngleMods(want.Y), TurretMath.AngleMods(head.Y), 1);
    }

    [Fact]
    public void Track_StepMotor_ClampsToAimMaxRotAndPitch()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        // target 90 degrees off in yaw and well above in pitch — beyond the 20/15 clamps
        Entity target = SpawnTarget(new Vector3(0, 800, 850), Vector3.Zero);
        turret.Enemy = target;
        TurretState st = TurretAI.State(turret);
        st.AimPos = target.Origin;

        const float maxPitch = 15f, maxRot = 20f;
        TurretParams p = Aim(aimSpeed: 720f, aimMaxPitch: maxPitch, aimMaxRot: maxRot,
            trackType: TurretAI.TrackStepMotor);
        for (int i = 0; i < 100; i++)
            TurretAI.Track(turret, in p);

        Assert.InRange(st.HeadAngles.Y, -maxRot, maxRot);
        Assert.InRange(st.HeadAngles.X, -maxPitch, maxPitch);
        Assert.Equal(maxRot, MathF.Abs(st.HeadAngles.Y), 1);   // pinned at the clamp, target is beyond it
    }

    [Fact]
    public void Track_FluidPrecise_AlsoRespectsThePerAxisClamps()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity target = SpawnTarget(new Vector3(0, 1000, 50), Vector3.Zero);
        turret.Enemy = target;
        TurretState st = TurretAI.State(turret);
        st.AimPos = target.Origin;

        const float maxRot = 25f;
        TurretParams p = Aim(aimSpeed: 720f, aimMaxRot: maxRot, trackType: TurretAI.TrackFluidPrecise);
        for (int i = 0; i < 100; i++)
            TurretAI.Track(turret, in p);

        Assert.InRange(st.HeadAngles.Y, -maxRot, maxRot);
    }
}
