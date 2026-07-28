using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// The <c>/kill</c> suicide must leave the OWNER-BLOCK health the client reads at or below zero, so the client
/// knows it is dead.
///
/// The bug ("after `kill` I can still see my weapon model, but not when someone else kills me"): QC
/// <c>ClientKill_Now</c> suicides with <c>Damage(this, this, this, 100000, DEATH_KILL, …)</c>. PlayerDamage
/// clamps the killing <c>take</c> to the remaining health, then hands the whole overkill <c>excess</c> to
/// <c>PlayerCorpseDamage</c>, which subtracts it from the corpse UNCLAMPED — so the dead player's health lands
/// around -65000. Base networks health as a full 32-bit stat and does not care; the port writes it with
/// <c>BitWriter.WriteShort</c>, whose <c>(short)</c> cast silently truncated -65000 to a POSITIVE value. The
/// client's <c>EquipNetworkedWeapon</c> hides the first-person weapon on <c>Health &lt;= 0</c>, so a suicide left
/// the gun on screen for the whole death. A normal frag ends only slightly negative and round-trips fine, which
/// is why only the suicide showed it.
/// </summary>
[Collection("GlobalState")]
public class SuicideHealthWireTests
{
    private readonly ITestOutputHelper _out;
    public SuicideHealthWireTests(ITestOutputHelper output)
    {
        _out = output;
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    private static CollisionWorld FlatFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    /// <summary>The owner block's health field as ServerNet writes it: a signed 16-bit value, CLAMPED (not
    /// truncated) from the authoritative float. Mirrors <c>ServerNet.WriteOwnerState</c>.</summary>
    private static int WireHealth(float health)
        => System.Math.Clamp((int)health, short.MinValue, short.MaxValue);

    /// <summary>The pre-fix encoding: <c>WriteShort</c>'s bare <c>(short)</c> cast.</summary>
    private static int WireHealthTruncated(float health) => (short)(int)health;

    [Fact]
    public void SuicideLeavesNetworkedHealthAtOrBelowZero()
    {
        var world = new GameWorld(FlatFloor(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(0f, 0f, 32f)),
        });
        world.Boot("dm");
        world.Services.Cvars.Set("sv_spectate", "0");

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "victim");
        Player p = info.Player;
        world.Clients.Join(p);
        for (int t = 0; t < 72 * 2; t++) world.Frame(SimulationLoop.TicRate);

        p.SpawnShieldExpire = 0f;
        Assert.False(p.IsDead);

        // QC ClientKill_Now: Damage(this, this, this, 100000, DEATH_KILL, …).
        Combat.Damage(p, p, p, 100000f, DeathTypes.Kill, p.Origin, Vector3.Zero);

        float health = p.GetResource(ResourceType.Health);
        _out.WriteLine($"post-suicide health={health} wire={WireHealth(health)} (pre-fix truncated={WireHealthTruncated(health)})");

        Assert.True(p.IsDead, "the suicide must kill the player");
        // The overkill really does drive the corpse deeply negative — this is what overflowed the 16-bit field.
        Assert.True(health < short.MinValue, $"expected a large negative corpse health, got {health}");
        // What the client actually reads must still say "dead".
        Assert.True(WireHealth(health) <= 0, $"networked health must be <= 0, got {WireHealth(health)}");
    }

    /// <summary>Pins the encoding itself: a value past the 16-bit range must clamp (keeping its sign) rather
    /// than wrap. Without this, -65000 arrives as +536 and the client believes it is alive.</summary>
    [Theory]
    [InlineData(-64900f)]
    [InlineData(-99980f)]
    [InlineData(-40000f)]
    public void DeeplyNegativeHealthClampsInsteadOfWrappingPositive(float health)
    {
        Assert.True(WireHealthTruncated(health) > 0, "precondition: this value is one the old cast wrapped positive");
        Assert.Equal(short.MinValue, WireHealth(health));
        Assert.True(WireHealth(health) <= 0);
    }

    /// <summary>A normal frag stays inside the 16-bit range, so the fix must not perturb it.</summary>
    [Fact]
    public void OrdinaryDeathHealthIsUnchangedByTheClamp()
    {
        foreach (float h in new[] { 100f, 1f, 0f, -20f, -300f })
            Assert.Equal(WireHealthTruncated(h), WireHealth(h));
    }
}
