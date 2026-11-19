using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class OrderedAuditGameplayTests
{
    [Fact]
    public void ElectroEvictsOldestLaunchRegardlessOfLifetimeAndIgnoresUntrackedOrbs()
    {
        var previous = Api.Services;
        Api.Services = new EngineServices(new CollisionWorld());
        try
        {
            var electro = new Electro();
            electro.Configure();
            electro.Secondary.Refire = 0;
            electro.Secondary.Limit = 2;
            electro.Secondary.Ammo = 0;
            electro.CombocSafeAmmoCheck = false;
            var slot = new WeaponSlot(0);
            var owner = new Entity();
            var other = new Entity();
            owner.WeaponState(slot).ButtonAttack2 = true;
            other.WeaponState(slot).ButtonAttack2 = true;
            var untracked = Api.Entities.Spawn();
            untracked.ClassName = "electro_orb";
            untracked.Owner = owner;

            electro.WrThink(other, slot, FireMode.Secondary);
            Entity otherOrb = Assert.Single(Api.Entities.FindByClass("electro_orb"), e => e.Owner == other);
            electro.WrThink(owner, slot, FireMode.Secondary);
            Entity oldest = Assert.Single(Api.Entities.FindByClass("electro_orb"), e => e.Owner == owner && e != untracked);
            oldest.NextThink = 100f;
            oldest.Think = _ => throw new InvalidOperationException("Limit eviction invoked the scheduled think.");

            electro.WrThink(owner, slot, FireMode.Secondary);
            Entity newer = Assert.Single(Api.Entities.FindByClass("electro_orb"),
                e => e.Owner == owner && e != oldest && e != untracked);
            newer.NextThink = 1f;

            electro.WrThink(owner, slot, FireMode.Secondary);

            Assert.True(oldest.IsFreed);
            Assert.False(newer.IsFreed);
            Assert.False(otherOrb.IsFreed);
            Assert.False(untracked.IsFreed);
            Assert.Equal(2, Api.Entities.FindByClass("electro_orb").Count(e => e.Owner == owner && e != untracked));
        }
        finally
        {
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData("0", "electro_orb", false)]
    [InlineData("0", "electro_bolt", true)]
    [InlineData("2", "electro_orb", true)]
    public void ElectroOnlyExemptsComboDamageFromTheProjectileDamageGate(string mode, string inflictorClass, bool killed)
    {
        var previous = (Api.Services, Combat.System);
        Api.Services = new EngineServices(new CollisionWorld());
        Combat.System = new DamageSystem();
        try
        {
            Api.Cvars.Set("g_projectiles_damage", mode);
            var electro = new Electro();
            electro.Configure();
            electro.Secondary.Refire = 0;
            electro.Secondary.Ammo = 0;
            electro.CombocSafeAmmoCheck = false;
            var actor = new Entity();
            var slot = new WeaponSlot(0);
            actor.WeaponState(slot).ButtonAttack2 = true;
            electro.WrThink(actor, slot, FireMode.Secondary);
            Entity orb = Assert.Single(Api.Entities.FindByClass("electro_orb"));
            float health = orb.Health;
            var inflictor = new Entity { ClassName = inflictorClass, Owner = actor };

            Combat.Damage(orb, inflictor, actor, health + 1f, "electro", orb.Origin, Vector3.Zero);

            Assert.Equal(killed ? DamageMode.No : DamageMode.Yes, orb.TakeDamage);
            Assert.Equal(killed ? -1f : health, orb.Health);
        }
        finally
        {
            (Api.Services, Combat.System) = previous;
        }
    }

    [Theory]
    [InlineData("death")]
    [InlineData("class")]
    [InlineData("removed")]
    [InlineData("missing")]
    public void ElectroAttachmentLossUsesStockDropOrDeleteBranch(string loss)
    {
        var previous = Api.Services;
        var collision = new CollisionWorld();
        var services = new EngineServices(collision);
        var simulation = new SimulationLoop(services, collision);
        Api.Services = services;
        try
        {
            simulation.Tick();
            simulation.Tick();
            var electro = new Electro();
            electro.Configure();
            electro.Secondary.Refire = 0f;
            electro.Secondary.Limit = 1;
            electro.Secondary.Ammo = 0;
            electro.Secondary.Stick = true;
            electro.Secondary.StickLifetime = 6f;
            electro.Secondary.TouchExplode = false;
            electro.CombocSafeAmmoCheck = false;
            var slot = new WeaponSlot(0);
            var owner = new Entity();
            owner.WeaponState(slot).ButtonAttack2 = true;
            Entity carrier = Api.Entities.Spawn();
            carrier.ClassName = "target";
            electro.WrThink(owner, slot, FireMode.Secondary);
            Entity orb = Assert.Single(Api.Entities.FindByClass("electro_orb"));
            long order = orb.ElectroRubbleOrder;
            float expectedDeadline = Api.Clock.Time + electro.Secondary.StickLifetime;
            orb.Touch!(orb, carrier);
            Assert.Equal(MoveType.Follow, orb.MoveType);
            var lastOrigin = orb.Origin;

            switch (loss)
            {
                case "death": carrier.DeadState = DeadFlag.Dying; break;
                case "class": carrier.ClassName = "replacement"; break;
                case "removed": Api.Entities.Remove(carrier); break;
                case "missing": orb.Aiment = null; break;
            }

            simulation.Tick();

            if (loss is "missing" or "removed")
            {
                Assert.True(orb.IsFreed);
                return;
            }
            Assert.False(orb.IsFreed);
            Assert.Null(orb.Aiment);
            Assert.Equal(MoveType.Toss, orb.MoveType);
            Assert.Equal(Solid.Corpse, orb.Solid);
            Assert.Equal(1f, orb.Gravity);
            Assert.Equal(lastOrigin, orb.Origin);
            Assert.Equal(order, orb.ElectroRubbleOrder);
            Assert.Same(owner, orb.Owner);
            Assert.Equal(expectedDeadline, orb.NextThink);
        }
        finally
        {
            Api.Services = previous;
        }
    }

    [Fact]
    public void JoinNoticeWireRetainsLinesColorsAndStockWholeSecondDuration()
    {
        Assert.Null(JoinNotice.Create(15f, ""));
        Assert.Null(JoinNotice.Create(0.9f, "Gone"));
        var notice = Assert.IsType<JoinNotice>(JoinNotice.Create(15.9f, "^1Welcome|Match starts soon"));

        var writer = new BitWriter();
        notice.Write(writer);
        var reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(notice, JoinNotice.Read(ref reader));
        Assert.False(reader.BadRead);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(15, notice.Seconds);
        Assert.Equal(new[] { "^1Welcome", "Match starts soon" }, notice.Lines());

        reader = new BitReader(writer.WrittenSpan[..^1]);
        Assert.Null(JoinNotice.Read(ref reader));
        Assert.True(reader.BadRead);
    }

    [Fact]
    public void JoinNoticeUtf8LimitNeverCutsAScalarAndRejectsOversizedWireText()
    {
        string prefix = new('é', (JoinNotice.MaxUtf8Bytes - 2) / 2);
        string text = prefix + "🎮extra";
        var notice = Assert.IsType<JoinNotice>(JoinNotice.Create(10, text));

        Assert.Equal(prefix, notice.Text);
        var writer = new BitWriter();
        notice.Write(writer);
        var reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(notice, JoinNotice.Read(ref reader));
        Assert.False(reader.BadRead);

        writer.Reset();
        writer.WriteLong(10);
        writer.WriteString(text);
        reader = new BitReader(writer.WrittenSpan);
        Assert.Null(JoinNotice.Read(ref reader));
    }

    [Fact]
    public void ViewLocationWireCarriesBothAnchorsAndExplicitExit()
    {
        var region = new ViewLocationState(17, ViewLocationFlags.FreeAim | ViewLocationFlags.Track,
            true, new(5, 3, 10), new(25, -3, 20), new(15, 20, 0));
        var fixedCamera = region with { SideScroll = false, End = region.Start };
        var writer = new BitWriter();
        ViewLocationWire.Write(writer, region);
        ViewLocationWire.Write(writer, fixedCamera);
        ViewLocationWire.Write(writer, null);

        var reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(region, ViewLocationWire.Read(ref reader));
        Assert.Equal(fixedCamera, ViewLocationWire.Read(ref reader));
        Assert.Null(ViewLocationWire.Read(ref reader));
        Assert.Equal(0, reader.Remaining);
        Assert.False(reader.BadRead);
    }

    [Fact]
    public void ViewLocationTriggerBindsAnchorsAndClearsWhenPlayerLeaves()
    {
        var previous = Api.Services;
        var world = new CollisionWorld();
        var services = new EngineServices(world);
        var simulation = new SimulationLoop(services, world);
        Api.Services = services;
        try
        {
            Entity start = Api.Entities.Spawn();
            start.TargetName = "parity_start";
            ViewLocation.StartSetup(start);
            Entity end = Api.Entities.Spawn();
            end.TargetName = "parity_end";
            Api.Entities.SetOrigin(end, new Vector3(0, 100, 0));
            ViewLocation.EndSetup(end);

            Entity trigger = Api.Entities.Spawn();
            trigger.Target = start.TargetName;
            trigger.Target2 = end.TargetName;
            Api.Entities.SetSize(trigger, new Vector3(-32), new Vector3(32));
            ViewLocation.TriggerViewLocationSetup(trigger);
            ViewLocation.RunDeferredInit();

            Entity player = Api.Entities.Spawn();
            player.ClassName = "player";
            player.Flags = EntFlags.Client;
            Api.Entities.SetSize(player, new Vector3(-16), new Vector3(16));
            simulation.Tick();

            Assert.Equal(trigger, player.ViewLoc);
            var state = Assert.IsType<ViewLocationState>(player.ActiveViewLocation);
            Assert.Equal(end.Origin, state.End);
            Assert.True(state.SideScroll);

            Api.Entities.SetOrigin(player, new Vector3(100));
            simulation.Tick();
            Assert.Null(player.ActiveViewLocation);
        }
        finally
        {
            Api.Services = previous;
        }
    }

    [Theory]
    [InlineData(ViewLocationFlags.FreeMove, false, -45f, 8f, -3f, 90f)]
    [InlineData(ViewLocationFlags.FreeMove, true, -45f, 8f, 0f, 90f)]
    [InlineData(ViewLocationFlags.FreeAim, false, 30f, 8f, 0f, 30f)]
    public void ViewLocationRemapsInputsAndFacing(ViewLocationFlags flags, bool ladder, float yaw,
        float expectedForward, float expectedSide, float expectedYaw)
    {
        var region = new ViewLocationState(1, flags, true, Vector3.Zero, Vector3.UnitY, Vector3.Zero);
        var movement = new Vector3(3f, -8f, 2f);
        var angles = new Vector3(0f, yaw, 0f);
        region.RemapMovement(ref movement, ref angles, ladder);

        Assert.Equal(new Vector3(expectedForward, expectedSide, 2f), movement);
        Assert.Equal(expectedYaw, angles.Y);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 180)]
    public void ViewLocationMovementUsesTheFacingFromTheAvatarInput(int side, int yaw)
    {
        var previous = Api.Services;
        Api.Services = new EngineServices(new CollisionWorld());
        try
        {
            var player = new Entity
            {
                MoveType = MoveType.Walk,
                PredictedViewLocation = new ViewLocationState(1, ViewLocationFlags.None, true,
                    Vector3.Zero, new Vector3(100, 0, 0), Vector3.Zero),
            };
            var input = new MovementInput
            {
                FrameTime = 1f / 64f,
                MoveValues = new Vector3(0, 200 * side, 0),
                ViewAngles = new Vector3(0, yaw, 0),
                Predicted = true,
            };
            new PlayerPhysics().Move(player, input);

            Assert.Equal(200f, player.MovementForward);
            Assert.Equal(yaw, player.ViewAngles.Y);
            Assert.True(player.Velocity.X * -side > 0f);
        }
        finally
        {
            Api.Services = previous;
        }
    }

    [Fact]
    public void ViewLocationClampsCameraAndPreservesCloseSnapTransition()
    {
        var region = new ViewLocationState(1, ViewLocationFlags.CloseSnap, true,
            Vector3.Zero, new Vector3(0, 0, 8), new Vector3(0, 45, 0));
        float yaw = 0;
        var tracking = region.ResolveCamera(new Vector3(0, 4, 20), ref yaw);
        Assert.Equal(new Vector3(0, 0, 8), tracking.Origin);
        Assert.Equal(new Vector3(0, 90, 0), tracking.Angles);
        Assert.Equal(90f, yaw);

        var outsideRail = region with { Flags = ViewLocationFlags.None };
        Assert.Equal(45f, outsideRail.ResolveCamera(new Vector3(10, 10, 20), ref yaw).Angles.Y);
        var hardSnap = region with { Flags = ViewLocationFlags.HardSnap };
        Assert.Equal(90f, hardSnap.ResolveCamera(new Vector3(1, 1, 0), ref yaw).Angles.Y);
    }
}
