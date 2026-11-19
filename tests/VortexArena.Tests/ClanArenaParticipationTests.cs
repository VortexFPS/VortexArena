using System;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Engine.Collision;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class ClanArenaParticipationTests
{
    [Fact]
    public void LiveRoundPreservesEliminatedAndQueuedPlayersUntilReset()
    {
        var world = NewWorld();
        var ca = Assert.IsType<ClanArena>(world.GameType);
        try
        {
            Player red = Connect(world, "red", Teams.Red);
            Player redMate = Connect(world, "redMate", Teams.Red);
            Player blue = Connect(world, "blue", Teams.Blue);
            Player watching = Connect(world, "watching", Teams.Blue);
            Player waiting = Connect(world, "waiting", Teams.Red);
            Player optOut = Connect(world, "optOut", Teams.Blue);
            Advance(world, 1.1f);
            Command(world, red, "join");
            Command(world, redMate, "join");
            Command(world, blue, "join");
            AdvanceUntil(world, () => world.Rounds!.IsRoundStarted);
            Assert.True(waiting.IsObserver);
            Assert.True(ca.SpawnBlocked);
            var roundsPlayed = GameScores.Field("ROUNDS_PL")!;
            Assert.Equal(1, GameScores.Get(red, roundsPlayed));
            Assert.Equal(0, GameScores.Get(watching, roundsPlayed));

            world.Services.Cvars.Set("g_maxplayers", "3");
            Command(world, waiting, "join");
            Assert.False(ca.IsJoiningMidRound(waiting));
            world.Services.Cvars.Set("g_maxplayers", "8");
            world.Clients.TeamsLocked = true;
            Command(world, waiting, "join");
            Assert.False(ca.IsJoiningMidRound(waiting));
            world.Clients.TeamsLocked = false;
            world.Clients.ForcedSpectate = p => p == waiting;
            Command(world, waiting, "join");
            Assert.False(ca.IsJoiningMidRound(waiting));
            world.Clients.ForcedSpectate = null;

            world.Clients.ObserverOrSpectatorThink(waiting, jumpHeld: true, attackHeld: false);
            Command(world, optOut, "join");
            Assert.True(ca.IsJoiningMidRound(waiting),
                $"state={waiting.FragsStatus}, locked={world.Clients.TeamsLocked}, cap={world.GetPlayerLimit()}, forced={world.Teamplay.GetForcedTeam(waiting)}");
            Assert.Equal(Player.FragsOutOfGame, waiting.FragsStatus);
            Assert.True(ca.IsJoiningMidRound(optOut));
            Command(world, optOut, "spectate");
            Assert.False(ca.IsJoiningMidRound(optOut));
            Assert.Equal(Player.FragsSpectator, optOut.FragsStatus);
            Player disconnected = Connect(world, "disconnected", Teams.Blue);
            Advance(world, 1.1f);
            Command(world, disconnected, "join");
            Assert.True(ca.IsJoiningMidRound(disconnected));
            world.Clients.ClientDisconnect(disconnected);
            Assert.False(ca.IsJoiningMidRound(disconnected));

            Combat.Damage(red, blue, blue, 10000f, DeathTypes.Kill, red.Origin, Vector3.Zero);
            Assert.True(red.IsDead);
            Command(world, red, "join");
            Advance(world, 1.9f);
            Assert.True(red.IsDead);
            Assert.False(red.IsObserver);
            Advance(world, 0.2f);
            Assert.True(red.IsObserver);
            Assert.Equal(Player.FragsOutOfGame, red.FragsStatus);
            Assert.Equal(1, ca.AliveCount(Teams.Red));
            Assert.Equal(1, ca.AliveCount(Teams.Blue));

            Command(world, red, "spectate blue");
            Assert.Null(red.Spectatee);
            Command(world, red, "spectate redMate");
            Assert.Same(redMate, red.Spectatee);
            Command(world, red, "spectate 0");
            Assert.Null(red.Spectatee);
            Assert.Equal(Player.FragsOutOfGame, red.FragsStatus);
            Command(world, watching, "spectate blue");
            Assert.Same(blue, watching.Spectatee);
            Assert.Equal(Player.FragsSpectator, watching.FragsStatus);

            Combat.Damage(blue, redMate, redMate, 10000f, DeathTypes.Kill, blue.Origin, Vector3.Zero);
            AdvanceUntil(world, () => world.Rounds!.AwaitingNextRound);
            Assert.Equal(1, ca.GetTeamRounds(Teams.Red));
            AdvanceUntil(world, () => !world.Rounds!.AwaitingNextRound);
            Assert.False(red.IsObserver);
            Assert.False(blue.IsObserver);
            Assert.False(waiting.IsObserver);
            Assert.False(ca.IsJoiningMidRound(waiting));
            Assert.True(watching.IsObserver);
            Assert.True(optOut.IsObserver);
            Assert.DoesNotContain(disconnected, world.Clients.Players);
        }
        finally { ca.Deactivate(); }
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(new CollisionWorld(),
        [
            new EntityDict("worldspawn"),
            new EntityDict("info_player_deathmatch", new Vector3(0, 0, 64)),
            new EntityDict("info_player_deathmatch", new Vector3(256, 0, 64)),
            new EntityDict("info_player_deathmatch", new Vector3(0, 256, 64)),
            new EntityDict("info_player_deathmatch", new Vector3(256, 256, 64))
        ]);
        world.Boot("ca", applyStartupOverrides: cvars =>
        {
            cvars.Set("g_ca_warmup", "1");
            cvars.Set("g_ca_round_enddelay", "3");
            cvars.Set("g_warmup", "0");
            cvars.Set("sv_spectate", "1");
        });
        return world;
    }

    private static Player Connect(GameWorld world, string name, int team)
    {
        Player p = world.Clients.ClientConnect(netName: name).Player;
        p.Team = team;
        return p;
    }

    private static void Command(GameWorld world, Player player, string line)
    {
        string output = world.Commands.Execute(line, isServerConsole: false, caller: player).Output;
        Assert.DoesNotContain("failed:", output);
        Assert.DoesNotContain("not allowed", output);
        Assert.DoesNotContain("Unknown command", output);
    }

    private static void Advance(GameWorld world, float seconds)
    {
        float end = world.Time + seconds;
        while (world.Time < end)
            world.Frame(1f / 64f);
    }

    private static void AdvanceUntil(GameWorld world, Func<bool> predicate)
    {
        for (int i = 0; i < 40 && !predicate(); i++)
            Advance(world, 0.5f);
        Assert.True(predicate());
    }
}
