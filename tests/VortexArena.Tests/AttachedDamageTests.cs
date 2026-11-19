using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Net;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class AttachedDamageTests
{
    [Fact]
    public void SimulatedRocketFiresAndRecordsTheVictimHit()
    {
        var collision = new CollisionWorld();
        var services = new EngineServices(collision);
        var simulation = new SimulationLoop(services, collision);
        var recorder = new EffectEmitter.RecordingSink();
        var previous = (EffectEmitter.Sink, Api.Services, Combat.System);
        try
        {
            EffectEmitter.Sink = recorder;
            Api.Services = services;
            Combat.System = new DamageSystem();
            services.Cvars.Set("g_balance_devastator_damage", "45");
            services.Cvars.Set("g_balance_devastator_force", "0");
            GameRegistries.Reset();
            GameRegistries.Bootstrap();

            var weapon = Assert.IsType<Devastator>(Weapons.ByName("devastator"));
            var shooter = AddPlayer(services, Vector3.Zero);
            shooter.ViewOfs = new Vector3(0f, 0f, 30f);
            shooter.ActiveWeaponId = weapon.RegistryId;
            shooter.SetResourceExplicit(ResourceType.Rockets, 20f);
            shooter.WeaponState(new WeaponSlot(0)).State = WeaponFireState.Ready;
            shooter.WeaponState(new WeaponSlot(0)).RlRelease = true;
            var victim = AddPlayer(services, new Vector3(180f, 0f, 0f));

            simulation.Tick();
            simulation.Tick();
            WeaponFireDriver.Frame(shooter, new MovementInput { ButtonAttack1 = true });
            for (int i = 0; i < 35; i++)
            {
                WeaponFireDriver.Frame(shooter, null);
                simulation.Tick();
            }

            AttachedDamageHit hit = Assert.Single(recorder.AttachedDamage);
            Assert.Same(victim, hit.Target);
            Assert.Equal(weapon.RegistryId, hit.WeaponId);
            Assert.Equal((byte)(100f - victim.Health), hit.Damage);
            Assert.InRange(hit.HitOffset.Length(), 0f, 55f);
            Assert.InRange(victim.Health, 55f, 56f);
        }
        finally
        {
            (EffectEmitter.Sink, Api.Services, Combat.System) = previous;
        }
    }

    private static Entity AddPlayer(EngineServices services, Vector3 origin)
    {
        Entity e = services.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Health = e.MaxHealth = 100f;
        e.TakeDamage = DamageMode.Aim;
        e.Solid = Solid.SlideBox;
        e.Model = "models/player/erebus.iqm";
        services.Entities.SetSize(e, new Vector3(-16, -16, -24), new Vector3(16, 16, 45));
        services.Entities.SetOrigin(e, origin);
        return e;
    }

    [Fact]
    public void BleedUsesVictimSpeciesAndSkipsObjects()
    {
        var machinegun = new Machinegun();
        Assert.Equal("damage_machinegun", AttachedDamageHit.EffectName(machinegun, true, "human"));
        Assert.Equal("alien_damage_machinegun", AttachedDamageHit.EffectName(machinegun, true, "alien"));
        Assert.Equal("robot_damage_machinegun", AttachedDamageHit.EffectName(machinegun, true, "robot_rusty"));
        Assert.Null(AttachedDamageHit.EffectName(machinegun, false, ""));
        Assert.Equal("damage_devastator", AttachedDamageHit.EffectName(new OkRpc(), false, ""));
    }

    [Fact]
    public void ScheduleKeepsDeadBodiesUntilExpiryAndRetiresOnRespawn()
    {
        Assert.Equal(3f, AttachedDamageSchedule.Lifetime(1, 0.1f, 3f, 6f));
        Assert.Equal(4.5f, AttachedDamageSchedule.Lifetime(45, 0.1f, 3f, 6f));
        Assert.Equal(6f, AttachedDamageSchedule.Lifetime(200, 0.1f, 3f, 6f));
        var schedule = new AttachedDamageSchedule(0f, 4.5f, dead: false);
        Assert.False(schedule.IsFinished(1f, dead: true));
        Assert.False(schedule.IsFinished(2f, dead: true));
        Assert.True(schedule.IsFinished(3f, dead: false));

        schedule = new AttachedDamageSchedule(0f, 4.5f, dead: true);
        Assert.False(schedule.IsFinished(4.49f, dead: true));
        Assert.True(schedule.IsFinished(4.5f, dead: true));
    }

    [Fact]
    public void DistributedAndUndistributedTicksDoNotBackfillMissedFrames()
    {
        var schedule = new AttachedDamageSchedule(0f, 6f, dead: false);
        Assert.True(schedule.Tick(0f, 0.1f, 4, distribute: true));
        Assert.False(schedule.Tick(0.39f, 0.1f, 4, distribute: true));
        Assert.True(schedule.Tick(0.8f, 0.1f, 4, distribute: false));
        Assert.False(schedule.Tick(0.8f, 0.1f, 4, distribute: false));
        Assert.True(schedule.Tick(0.91f, 0.1f, 4, distribute: false));
    }

    [Fact]
    public void MatchRestartChangesTheLifeMarkerForAnAlivePlayer()
    {
        var world = new GameWorld(new CollisionWorld(),
        [
            new EntityDict("worldspawn"),
            new EntityDict("info_player_deathmatch", new Vector3(0, 0, 64)),
            new EntityDict("info_player_deathmatch", new Vector3(256, 0, 64)),
        ]);
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars => cvars.Set("g_warmup", "0"));
            Player player = world.Clients.ClientConnect(isBot: true, netName: "restart-probe").Player;
            Assert.False(player.IsObserver);
            Assert.False(player.IsDead);
            bool previousLife = player.SpawnParity;
            world.Frame(0.1f);
            world.Commands.Execute("restart", isServerConsole: true);
            Assert.False(player.IsDead);
            Assert.NotEqual(previousLife, player.SpawnParity);
        }
        finally
        {
            world.Shutdown();
        }
    }

    [Fact]
    public void SpawnParityUsesTheExistingFlagDeltaAndPersistsAfterTheTeleportPulse()
    {
        var previous = new NetEntityState { Flags = NetEntityFlags.OnGround };
        var spawn = previous;
        spawn.Flags |= NetEntityFlags.SpawnParity | NetEntityFlags.Teleported;
        var writer = new BitWriter();

        Assert.Equal(EntityField.Flags, EntityStateCodec.WriteDelta(writer, previous, spawn));
        var reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(spawn.Flags, EntityStateCodec.ReadDelta(ref reader, previous).Flags);

        var playing = spawn;
        playing.Flags &= ~NetEntityFlags.Teleported;
        writer.Reset();
        // A client missing the spawn packet still sees the life change in a later delta.
        Assert.Equal(EntityField.Flags, EntityStateCodec.WriteDelta(writer, previous, playing));
        reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(NetEntityFlags.OnGround | NetEntityFlags.SpawnParity,
            EntityStateCodec.ReadDelta(ref reader, previous).Flags);
    }

    [Fact]
    public void OptionalFramedRecordPreservesOtherEventsAndRejectsInvalidBodies()
    {
        var hit = new AttachedDamageWire(501, 8, new Vector3(-4.125f, 0.5f, 26f), 45, 13.5f, SpawnParity: true);
        var writer = new BitWriter();
        hit.Write(writer);
        writer.WriteUShort(3);
        writer.WriteUShort(2);
        writer.WriteUShort(1024);
        var reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(AttachedDamageWire.EffectRecordId, reader.ReadUShort());
        var body = reader.ReadBytes(reader.ReadUShort());
        Assert.Equal(hit, AttachedDamageWire.Read(body));
        Assert.Null(AttachedDamageWire.Read(body[..^1]));
        Assert.Equal(3, reader.ReadUShort());
        Assert.Equal(2, reader.ReadUShort());
        Assert.Equal(1024, reader.ReadUShort());

        writer.Reset();
        (hit with { HitOffset = new Vector3(float.NaN, 0f, 0f) }).Write(writer);
        Assert.Null(AttachedDamageWire.Read(writer.WrittenSpan[4..]));
    }
}
