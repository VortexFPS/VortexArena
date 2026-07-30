using System.Collections.Generic;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.12: the spectate-enemies restriction (an in-game eliminated player may follow only teammates)
/// and the ClanArena no-friendly-fire damage filter (a live player takes no team/self/fall damage).
/// </summary>
[Collection("GlobalState")]
public class SpectatorRulesTests
{
    private static Player NewPlayer(int team) => new Player { Team = team, Flags = EntFlags.Client };

    [Fact]
    public void TeammatesOnly_BlocksEnemyButAllowsTeammate()
    {
        var spec = NewPlayer(Teams.Red);
        var ally = NewPlayer(Teams.Red);
        var enemy = NewPlayer(Teams.Blue);

        // in-game (eliminated, still on a team), teammates-only mode, team game
        Assert.True(SpectatorRules.CanSpectate(spec, ally, spectatorInGame: true, SpectatorRules.SpectateTeammatesOnly, teamGame: true));
        Assert.False(SpectatorRules.CanSpectate(spec, enemy, spectatorInGame: true, SpectatorRules.SpectateTeammatesOnly, teamGame: true));
    }

    [Fact]
    public void Observer_OrAnyoneMode_CanSpectateEnemies()
    {
        var spec = NewPlayer(Teams.Red);
        var enemy = NewPlayer(Teams.Blue);

        // a pure observer (not in-game) may watch anyone even in teammates-only mode
        Assert.True(SpectatorRules.CanSpectate(spec, enemy, spectatorInGame: false, SpectatorRules.SpectateTeammatesOnly, teamGame: true));
        // mode 1 (anyone) lets even an in-game player watch enemies
        Assert.True(SpectatorRules.CanSpectate(spec, enemy, spectatorInGame: true, SpectatorRules.SpectateAnyone, teamGame: true));
    }

    [Fact]
    public void Cycle_SkipsEnemiesWhenRestricted()
    {
        var spec = NewPlayer(Teams.Red);
        var ally1 = NewPlayer(Teams.Red);
        var enemy = NewPlayer(Teams.Blue);
        var ally2 = NewPlayer(Teams.Red);
        var players = new List<Player> { ally1, enemy, ally2 };

        // starting at ally1, the next allowed teammate (skipping the enemy) is ally2
        Player? next = SpectatorRules.CycleSpectatee(spec, players, ally1, spectatorInGame: true,
            SpectatorRules.SpectateTeammatesOnly, teamGame: true, forward: true);
        Assert.Same(ally2, next);
    }

    [Fact]
    public void ClanArena_NoFriendlyFireOrSelfDamage()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameInit.InstallGameplaySystems();
        var ca = new ClanArena();
        ca.Activate();

        var a = NewPlayer(Teams.Red); a.Health = 100; a.MaxHealth = 100; a.TakeDamage = DamageMode.Aim;
        var mate = NewPlayer(Teams.Red); mate.Health = 100; mate.MaxHealth = 100; mate.TakeDamage = DamageMode.Aim;
        var foe = NewPlayer(Teams.Blue); foe.Health = 100; foe.MaxHealth = 100; foe.TakeDamage = DamageMode.Aim;

        // team damage → 0
        Combat.Damage(mate, a, a, 50f, "blaster", mate.Origin, System.Numerics.Vector3.Zero);
        Assert.Equal(100f, mate.Health);
        // self damage → 0
        Combat.Damage(a, a, a, 50f, "blaster", a.Origin, System.Numerics.Vector3.Zero);
        Assert.Equal(100f, a.Health);
        // enemy damage → applies
        Combat.Damage(foe, a, a, 40f, "blaster", foe.Origin, System.Numerics.Vector3.Zero);
        Assert.True(foe.Health < 100f);

        ca.Deactivate();
    }
}
