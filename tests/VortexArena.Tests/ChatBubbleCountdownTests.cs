using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Playtest 2026-07-31 #67: the "typing" chat bubble stayed over a player's head during the pre-match countdown.
/// QC PHYS_INPUT_BUTTON_CHAT is DarkPlaces' per-usercmd <c>buttonchat</c>, refreshed from every command the
/// client sends; the port only refreshed <see cref="Entity.ButtonChat"/> inside PlayerPhysics.Move, which does not
/// run while the countdown (or intermission) holds the player. A player who readied up from the console or the
/// chat prompt — typing on the last tick before the hold — therefore kept the bubble for the whole countdown.
/// </summary>
[Collection("GlobalState")]
public sealed class ChatBubbleCountdownTests
{
    public ChatBubbleCountdownTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    private sealed class Input : IMovementInput
    {
        public Vector3 ViewAngles { get; set; }
        public Vector3 MoveValues { get; set; }
        public float FrameTime { get; set; } = SimulationLoop.TicRate;
        public bool ButtonJump => false;
        public bool ButtonCrouch => false;
        public bool ButtonUse => false;
        public bool ButtonAttack1 => false;
        public bool ButtonAttack2 => false;
        public bool Typing { get; set; }
    }

    private static CollisionWorld FlatFloor()
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        w.BuildGrid();
        return w;
    }

    private static (GameWorld world, Player p, Input input) LivePlayer()
    {
        var world = new GameWorld(FlatFloor(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(0f, 0f, 32f)),
        });
        world.Boot("dm");
        world.Services.Cvars.Set("sv_spectate", "0");
        Player p = world.Clients.ClientConnect(isBot: false, netName: "typist").Player;
        world.Clients.Join(p);
        var input = new Input();
        world.InputProvider = _ => input;
        for (int t = 0; t < 72 * 2; t++) world.Frame(SimulationLoop.TicRate);
        Assert.False(p.IsDead);
        return (world, p, input);
    }

    [Fact]
    public void BubbleClearsDuringTheCountdownWhenThePlayerStopsTyping()
    {
        (GameWorld world, Player p, Input input) = LivePlayer();

        // Typing while the match is live: the bubble comes up.
        input.Typing = true;
        world.Frame(SimulationLoop.TicRate);
        Assert.True(p.ButtonChat);
        Assert.NotNull(p.ChatBubble);

        // The countdown starts (players held: no PM_Main) and the prompt closes on the same tick.
        world.GameStartTime = world.Time + 5f;
        input.Typing = false;
        for (int t = 0; t < 10; t++) world.Frame(SimulationLoop.TicRate);

        Assert.True(world.Time < world.GameStartTime, "still inside the countdown");
        Assert.False(p.ButtonChat);
        Assert.True(p.ChatBubble is null || p.ChatBubble.IsFreed, "the typing bubble must clear while the countdown holds the player");
    }

    [Fact]
    public void TypingDuringTheCountdownStillShowsTheBubble()
    {
        // Base shows the bubble whenever BUTTON_CHAT is set — a player who really opens the chat during the
        // countdown gets it, same as in Xonotic.
        (GameWorld world, Player p, Input input) = LivePlayer();
        world.GameStartTime = world.Time + 5f;
        input.Typing = true;
        for (int t = 0; t < 5; t++) world.Frame(SimulationLoop.TicRate);
        Assert.True(p.ButtonChat);
        Assert.NotNull(p.ChatBubble);
    }
}
