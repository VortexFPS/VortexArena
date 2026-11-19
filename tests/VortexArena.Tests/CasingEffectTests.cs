using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public class CasingEffectTests
{
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    public void EjectedCasingsKeepTheShooterForRecipientFiltering(int mode, bool shell, bool bullet)
    {
        var previousServices = Api.Services;
        var previousSink = EffectEmitter.Sink;
        var captured = new EffectEmitter.RecordingSink();
        try
        {
            var world = new GameWorld(new CollisionWorld()) { MapName = "stormkeep" };
            world.Boot("dm");
            world.Services.Cvars.Set("g_casings", mode.ToString());
            EffectEmitter.Sink = captured;
            var shooter = new Player { Flags = EntFlags.Client };
            var origin = new Vector3(80, 20, 32);

            WeaponFiring.EjectCasing(shooter, origin, WeaponFiring.CasingType.Shell);
            WeaponFiring.EjectCasing(shooter, origin, WeaponFiring.CasingType.Bullet);

            Assert.Equal((shell ? 1 : 0) + (bullet ? 1 : 0), captured.Count);
            Assert.Equal(shell, captured.Log.Any(effect => effect.Effect?.Name == "CASING_SHELL"));
            Assert.Equal(bullet, captured.Log.Any(effect => effect.Effect?.Name == "CASING_BULLET"));
            Assert.All(captured.Log, effect =>
            {
                Assert.Same(shooter, effect.CasingOwner);
                Assert.Null(effect.Except);

                var fromWorld = EffectNetProtocol.Encode(effect)!;
                var firstPerson = EffectNetProtocol.Encode(effect, firstPersonCasing: true)!;
                Assert.Equal(fromWorld.Length, firstPerson.Length);
                const int flagsOffset = 3 * sizeof(float);
                Assert.Equal(0, fromWorld[flagsOffset] & EffectNetProtocol.NetFirstPersonCasing);
                Assert.Equal(EffectNetProtocol.NetFirstPersonCasing,
                    firstPerson[flagsOffset] ^ fromWorld[flagsOffset]);
            });
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            Api.Services = previousServices;
        }
    }

    [Fact]
    public void CasingPreferencesAreReplicatedPerRecipient()
    {
        var previousServices = Api.Services;
        try
        {
            var world = new GameWorld(new CollisionWorld()) { MapName = "stormkeep" };
            world.Boot("dm");
            var shooter = new Player { Flags = EntFlags.Client };
            var viewer = new Player { Flags = EntFlags.Client };

            world.Commands.Execute("sentcvar cl_casings 0", isServerConsole: false, caller: viewer);
            world.Commands.Execute("sentcvar r_drawviewmodel 0", isServerConsole: false, caller: shooter);

            Assert.False(world.Commands.GetClientCvarBool(viewer, "cl_casings", fallback: true));
            Assert.True(world.Commands.GetClientCvarBool(shooter, "cl_casings", fallback: true));
            Assert.Equal(0f, world.Commands.GetClientCvarFloat(shooter, "r_drawviewmodel", fallback: 1f));
            Assert.Equal(1f, world.Commands.GetClientCvarFloat(viewer, "r_drawviewmodel", fallback: 1f));

            world.Commands.Execute("sentcvar cl_casings 1", isServerConsole: false, caller: viewer);
            Assert.True(world.Commands.GetClientCvarBool(viewer, "cl_casings", fallback: false));
        }
        finally
        {
            Api.Services = previousServices;
        }
    }
}
