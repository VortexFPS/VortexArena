using System;
using System.Numerics;
using System.Reflection;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for the turret lifecycle (ports of <c>turret_use</c> / <c>turret_damage</c> /
/// <c>turret_die</c> / <c>turret_respawn</c>, common/turrets/sv_turrets.qc:166-290, plus the
/// spawnfunc path <c>turret_initialize</c>). NOTE: the turret subsystem has no live engine caller —
/// these tests are its first consumer, and several pins record PORT-vs-cfg drift (see the FLAGGED
/// notes) rather than fixing production code, per the T31 charter.
/// </summary>
[Collection("GlobalState")]
public class TurretLifecycleTests
{
    private static EngineServices Boot()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        var services = new EngineServices(world);
        GameInit.Boot(services);
        Prandom.Seed(0xC0FFEE);
        return services;
    }

    private static Entity SpawnEwheel(Vector3 origin)
    {
        Entity e = Api.Entities.Spawn();
        e.Origin = origin;
        Api.Entities.SetOrigin(e, origin);
        TurretSpawnFuncs.EWheel(e);
        Assert.False(e.IsFreed, "ewheel spawnfunc should accept the edict (g_turrets defaults on)");
        return e;
    }

    private static Entity Attacker(Vector3 origin, float team = 0f)
    {
        Entity a = Api.Entities.Spawn();
        a.ClassName = "player";
        a.Flags |= EntFlags.Client;
        a.Team = team;
        a.Health = 100f;
        a.TakeDamage = DamageMode.Aim;
        a.Origin = origin;
        Api.Entities.SetOrigin(a, origin);
        return a;
    }

    // ---------------------------------------------------------------- spawn defaults

    [Fact]
    public void EwheelSpawn_PinsHealthAndCoreSetup()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));

        // turrets.cfg:12 — g_turrets_unit_ewheel_health 200 (canary value, matches the shipped cfg)
        Assert.Equal(200f, e.Health);
        Assert.Equal(200f, e.MaxHealth);
        Assert.Equal("turret_ewheel", e.ClassName);
        Assert.Equal("ewheel", e.NetName);
        Assert.Equal(DamageMode.Aim, e.TakeDamage);          // QC DAMAGE_AIM
        Assert.Equal(Solid.SlideBox, e.Solid);               // ewheel is mobile (SOLID_SLIDEBOX)
        Assert.Equal(MoveType.Step, e.MoveType);
        Assert.Equal(DeadFlag.No, e.DeadState);
        Assert.NotEqual(0f, e.Team);                          // QC default nonzero team for SAME_TEAM gating
        Assert.NotNull(e.Think);                              // turret_link armed the per-frame think
        Assert.NotNull(e.Use);                                // turret_use wired

        TurretState st = TurretAI.State(e);
        Assert.True(st.Active);
        Assert.Equal(4000f, st.Ammo);                         // turrets.cfg:42 ammo_max 4000
        Assert.Equal(4000f, st.AmmoMax);
        Assert.Equal(50f, st.AmmoRecharge);                   // turrets.cfg:43 ammo_recharge 50
        Assert.True(st.Movable);                              // TUR_FLAG_MOVE
        // turrets.cfg:13 g_turrets_unit_ewheel_respawntime 30 (Wave-2 fixed the prior 60f drift to match the cfg).
        Assert.Equal(30f, st.RespawnTime);
    }

    [Fact]
    public void Spawnfunc_RespectsTheGTurretsMasterSwitch()
    {
        Boot();
        Api.Cvars.Set("g_turrets", "0");
        Entity e = Api.Entities.Spawn();
        Api.Entities.SetOrigin(e, new Vector3(0, 0, 8));
        TurretSpawnFuncs.EWheel(e);
        Assert.True(e.IsFreed);   // QC: if (!autocvar_g_turrets) delete(this)

        Api.Cvars.Set("g_turrets", "1");
        Entity e2 = Api.Entities.Spawn();
        Api.Entities.SetOrigin(e2, new Vector3(64, 0, 8));
        TurretSpawnFuncs.EWheel(e2);
        Assert.False(e2.IsFreed);
    }

    // ---------------------------------------------------------------- turret_use

    [Fact]
    public void Use_AdoptsActivatorTeam_TeamlessActivatorDeactivates()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));

        Entity activator = Attacker(new Vector3(100, 0, 26), team: 5f);
        TurretAI.Use(e, activator);
        Assert.Equal(5f, e.Team);
        Assert.True(TurretAI.State(e).Active);

        // QC turret_use: a teamless activator parks the turret inactive + teamless
        Entity neutral = Attacker(new Vector3(100, 0, 26), team: 0f);
        TurretAI.Use(e, neutral);
        Assert.Equal(0f, e.Team);
        Assert.False(TurretAI.State(e).Active);
    }

    // ---------------------------------------------------------------- turret_damage gates

    [Fact]
    public void Damage_DeadTurret_TakesNothing()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        e.DeadState = DeadFlag.Dead;
        Assert.Equal(0f, TurretAI.Damage(e, Attacker(new Vector3(500, 0, 26), 9f), 50f, Vector3.Zero));
    }

    [Fact]
    public void Damage_InactiveTurret_TakesNothing()
    {
        // QC turret_damage: "Inactive turrets take no damage. (hm..)"
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        TurretAI.State(e).Active = false;
        Assert.Equal(0f, TurretAI.Damage(e, Attacker(new Vector3(500, 0, 26), 9f), 50f, Vector3.Zero));
    }

    [Fact]
    public void Damage_SameTeam_GatedByFriendlyFire()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        e.Team = 5f;
        Entity friend = Attacker(new Vector3(500, 0, 26), team: 5f);

        // g_friendlyfire unset/0 -> a teammate's hit is rejected outright (QC: else return)
        Assert.Equal(0f, TurretAI.Damage(e, friend, 50f, Vector3.Zero));

        // g_friendlyfire 0.5 -> damage *= 0.5
        Api.Cvars.Set("g_friendlyfire", "0.5");
        Assert.Equal(25f, TurretAI.Damage(e, friend, 50f, Vector3.Zero), 3);
        Api.Cvars.Set("g_friendlyfire", "0");
    }

    [Fact]
    public void Damage_EnemyHit_FullDamage_ShovesMovable_DoesNotRetaliate()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        e.Team = 5f;
        Entity foe = Attacker(new Vector3(500, 0, 26), team: 14f);
        Vector3 force = new(120f, 0f, 40f);

        Assert.Null(e.Enemy);
        float taken = TurretAI.Damage(e, foe, 50f, force);

        Assert.Equal(50f, taken);
        Assert.Equal(force, e.Velocity);   // TUR_FLAG_MOVE: vforce shoves the mobile chassis
        // Base turret_damage (sv_turrets.qc:207-251) never adopts the attacker as an enemy: TFL_DMG_RETALIATE is
        // set but read by no Base code, so the turret does NOT turn to face whoever shot it.
        Assert.Null(e.Enemy);
    }

    // ---------------------------------------------------------------- turret_die / turret_respawn

    [Fact]
    public void Die_Unsolidifies_StopsDamage_AndSchedulesRespawn()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        float now = Api.Clock.Time;

        TurretAI.Die(e);

        Assert.Equal(Solid.Not, e.Solid);                  // QC SOLID_NOT
        Assert.Equal(DamageMode.No, e.TakeDamage);         // QC DAMAGE_NO
        Assert.Equal(0f, e.Health);                        // QC SetResourceExplicit(RES_HEALTH, 0)
        Assert.Null(e.Enemy);
        Assert.False(TurretAI.State(e).Active);
        // not TSL_NO_RESPAWN: the QC two-step (turret_die -> turret_hide -> turret_respawn). Die first schedules
        // turret_hide at +0.2 (sv_turrets.qc:200), and turret_hide schedules turret_respawn at respawntime - 0.2
        // (sv_turrets.qc:162) so the total dead interval is exactly respawntime.
        Assert.Equal(DeadFlag.Respawning, e.DeadState);
        Assert.NotNull(e.Think);
        float dieDelay = e.NextThink - now;
        Assert.Equal(0.2f, dieDelay, 2);

        // Run the hide step (the second think). The clock has not advanced in this unit test, so Hide schedules
        // the respawn at respawntime - 0.2 from "now"; combined with the 0.2 Die already spent, the total dead
        // interval is exactly respawntime (sv_turrets.qc turret_die:200 + turret_hide:162).
        TurretAI.Hide(e);
        float hideDelay = e.NextThink - now;
        Assert.Equal(TurretAI.State(e).RespawnTime - 0.2f, hideDelay, 2);
        Assert.Equal(TurretAI.State(e).RespawnTime, dieDelay + hideDelay, 2);
    }

    [Fact]
    public void Respawn_RestoresFullCombatState()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        TurretState st = TurretAI.State(e);
        st.HeadAngles = new Vector3(10f, 45f, 0f);
        st.Ammo = 100f;
        TurretAI.Die(e);

        TurretAI.Respawn(e);

        Assert.Equal(DeadFlag.No, e.DeadState);            // QC DEAD_NO
        // QC turret_respawn sets SOLID_BBOX then calls tur.tr_setup (sv_turrets.qc:273,291); the ewheel's
        // tr_setup (turret/ewheel.qc:204) re-applies SOLID_SLIDEBOX, so a MOBILE turret ends up SlideBox — the
        // TurretAI.Respawn -> EWheelTurret.OnRespawn chain mirrors that exactly.
        Assert.Equal(Solid.SlideBox, e.Solid);
        Assert.Equal(MoveType.Step, e.MoveType);           // ewheel OnRespawn re-applies MOVETYPE_STEP
        Assert.Equal(DamageMode.Aim, e.TakeDamage);        // QC DAMAGE_AIM
        Assert.Equal(e.MaxHealth, e.Health);               // health restored to max
        Assert.Null(e.Enemy);
        Assert.Equal(Vector3.Zero, e.AVelocity);
        Assert.Equal(st.IdleAim, st.HeadAngles);           // head reset to the idle pose
        Assert.Equal(st.AmmoMax, st.Ammo);                 // ammo refilled
        Assert.Equal(0f, st.AttackFinished);
        Assert.True(st.Active);                            // team is nonzero -> reactivates
    }

    [Fact]
    public void LethalDamage_ThroughTheSharedDeathHook_RunsDie()
    {
        Boot();
        // The hook is a once-only static subscription to Combat.Death; another test class may have
        // Clear()ed the chain after it was installed, so reset the guard via reflection and re-install
        // (OnAnyDeath is idempotent: a second invocation sees DeadState Respawning and skips).
        FieldInfo? hooked = typeof(TurretAI).GetField("_deathHooked", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(hooked);
        hooked!.SetValue(null, false);
        TurretAI.EnsureDeathHook();

        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        Entity foe = Attacker(new Vector3(900, 0, 26), team: 14f);
        e.Team = 5f;

        Combat.Damage(e, foe, foe, 10_000f, DeathTypes.FromWeapon("vortex"), e.Origin, Vector3.Zero);

        // the generic DamageSystem killed it -> Combat.Death -> TurretAI.Die -> respawn scheduled
        Assert.Equal(DeadFlag.Respawning, e.DeadState);
        Assert.Equal(Solid.Not, e.Solid);
        Assert.Equal(DamageMode.No, e.TakeDamage);
        Assert.Equal(0f, e.Health);
    }

    // ---------------------------------------------------------------- ewheel locomotion (Wave-7)

    private static Entity SpawnCheckpoint(string targetName, string target, Vector3 origin)
    {
        Entity cp = Api.Entities.Spawn();
        cp.Origin = origin;
        Api.Entities.SetOrigin(cp, origin);
        cp.TargetName = targetName;
        cp.Target = target;
        TurretSpawnFuncs.Checkpoint(cp);   // QC spawnfunc(turret_checkpoint): floor-snap + index by targetname
        return cp;
    }

    [Fact]
    public void EwheelMoveEnemy_SelectsTheCorrectDriveFrame()
    {
        Boot();
        Entity e = SpawnEwheel(new Vector3(0, 0, 8));
        e.Team = 5f;

        // Enemy well beyond the optimal range (900), dead ahead so the body yaw error is ~0 -> fast forward.
        Entity foe = Attacker(new Vector3(2000, 0, 26), team: 14f);
        e.Angles = QMath.VecToAngles(QMath.Normalize(foe.Origin - e.Origin));
        e.Enemy = foe;

        e.Think!(e);   // RunCombat + Drive

        // ewheel_move_enemy: dist > optimal AND aligned -> ewheel_anim_fwd_fast (2), stamped onto Entity.Frame.
        Assert.Equal(2f, e.Frame);

        // Pull the enemy inside the kite threshold (optimal*0.5 = 450) -> ewheel_anim_bck_slow (3).
        Api.Entities.SetOrigin(foe, new Vector3(300, 0, 26));
        e.Enemy = foe;
        e.Think!(e);
        Assert.Equal(3f, e.Frame);
    }

    [Fact]
    public void EwheelIdleWithPath_FollowsTheCheckpointChainAndDrives()
    {
        Boot();
        // A two-node looped chain: cp1 -> cp2 -> cp1. Both well above the floor brush (z=0).
        SpawnCheckpoint("cp1", "cp2", new Vector3(500, 0, 40));
        SpawnCheckpoint("cp2", "cp1", new Vector3(-500, 0, 40));

        // The ewheel targets the entry waypoint cp1 (set BEFORE spawn so ewheel_findtarget resolves it).
        Entity e = Api.Entities.Spawn();
        e.Origin = new Vector3(0, 0, 8);
        Api.Entities.SetOrigin(e, new Vector3(0, 0, 8));
        e.Target = "cp1";
        TurretSpawnFuncs.EWheel(e);
        e.Team = 5f;

        TurretState st = TurretAI.State(e);
        Assert.NotNull(st.PathCurrent);                         // ewheel_findtarget wired the entry checkpoint
        Assert.Equal("cp1", st.PathCurrent!.TargetName);

        // Idle (no enemy): ewheel_move_path should roll it toward cp1 (speed_fast), building velocity.
        e.Enemy = null;
        e.Think!(e);
        Assert.NotEqual(Vector3.Zero, e.Velocity);

        // Teleport onto cp1: the next think advances the chain to cp2 (turret_closetotarget within 64u).
        Api.Entities.SetOrigin(e, st.PathCurrent!.Origin);
        e.Think!(e);
        Assert.Equal("cp2", TurretAI.State(e).PathCurrent!.TargetName);
    }
}
