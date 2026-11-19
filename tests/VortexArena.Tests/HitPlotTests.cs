using System.Globalization;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class HitPlotTests
{
    [Fact]
    public void OrdinaryFire_WritesRewoundScreenCoordinates_OnlyForSelectedClient()
    {
        var directory = Directory.CreateTempSubdirectory();
        var world = new GameWorld(new CollisionWorld());
        var previousProvider = LagComp.Provider;
        try
        {
            world.Boot("dm");
            world.Services.Cvars.Set("g_antilag", "0");
            world.Services.Cvars.Set("g_hitplots_individuals", "192.0.2.1 192.0.2.110");
            world.StartHitPlots(name => Path.Combine(directory.FullName, name));
            var shooter = world.Clients.ClientConnect(netName: "^3Shooter", netAddress: "192.0.2.1").Player;
            var target = world.Clients.ClientConnect(netName: "Target", netAddress: "192.0.2.11").Player;
            Assert.Null(target.HitPlotShot);
            shooter.IsObserver = target.IsObserver = false;
            shooter.Angles = new Vector3(0, 90, 0);
            shooter.ViewOfs = new Vector3(0, 0, 10);
            Api.Entities.SetOrigin(shooter, Vector3.Zero);
            target.Solid = Solid.SlideBox;
            Api.Entities.SetSize(target, new Vector3(-10, -15, -20), new Vector3(20, 15, 40));
            var present = new Vector3(90, 100, 0);
            Api.Entities.SetOrigin(target, present);
            var history = new HistoricalTarget(target, new Vector3(2, 100, 0));
            LagComp.Provider = history;

            var blaster = Assert.IsType<Blaster>(Weapons.ByName("blaster"));
            var slot = new WeaponSlot(0);
            var state = shooter.WeaponState(slot);
            state.State = WeaponFireState.Ready;
            state.ButtonAttack = true;
            shooter.ActiveWeaponId = blaster.RegistryId;
            blaster.WrThink(shooter, slot, FireMode.Primary);
            Assert.Equal(1, state.BulletCounter);
            Assert.Equal(1, history.ForcedRewinds);
            Assert.Equal(present, target.Origin);

            world.Clients.ClientDisconnect(shooter);
            Assert.Null(shooter.HitPlotShot);
            string output = Assert.Single(directory.GetFiles("*.plot")).FullName;
            Assert.EndsWith($"-192.0.2.1-{shooter.PlayerId}.plot", output);
            string[] lines = File.ReadAllLines(output);
            Assert.Equal(2, lines.Length);
            Assert.Equal("#name ^3Shooter", lines[0]);
            string[] fields = lines[1].Split(' ');
            Assert.Equal("0.266667", fields[0]);
            Assert.Equal("0.500000", fields[1]);
            Assert.InRange(float.Parse(fields[2], CultureInfo.InvariantCulture), 84.90f, 85.10f);
            Assert.Equal("1", fields[3]); // Stock WEP_BLASTER.m_id, independent of the native registry.
        }
        finally
        {
            LagComp.Provider = previousProvider;
            world.Shutdown();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void NewMatch_UsesNewFileAndShutdownClosesRemainingClients()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            for (int match = 0; match < 2; match++)
            {
                var world = new GameWorld(new CollisionWorld());
                world.Boot("dm");
                world.Services.Cvars.Set("g_hitplots", "1");
                world.StartHitPlots(name => Path.Combine(directory.FullName, name));
                var player = world.Clients.ClientConnect(netName: "Local", netAddress: "local").Player;
                Assert.NotNull(player.HitPlotShot);
                world.Shutdown();
                Assert.Null(player.HitPlotShot);
            }

            Assert.Equal(2, directory.GetFiles("*.plot").Length);
            foreach (var file in directory.GetFiles("*.plot"))
                Assert.Equal("#name Local\n", File.ReadAllText(file.FullName));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class HistoricalTarget(Entity target, Vector3 historicalOrigin) : ILagCompensation
    {
        private Vector3 _present;
        private bool _forced;
        public int ForcedRewinds { get; private set; }

        public void Begin(Entity shooter) { }

        public void BeginForced(Entity shooter)
        {
            ForcedRewinds++;
            _forced = true;
            _present = target.Origin;
            Api.Entities.SetOrigin(target, historicalOrigin);
        }

        public void End()
        {
            if (!_forced)
                return;
            _forced = false;
            Api.Entities.SetOrigin(target, _present);
        }
    }
}
