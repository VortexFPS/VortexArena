using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the DP-faithful <c>SV_NudgeOutOfSolid</c> recovery wired into <see cref="PlayerPhysics"/>: a player
/// that begins a tick embedded in solid (a tight landing on patch-collision geometry, a telefrag, a
/// spawn-in-solid) is nudged to the nearest free spot instead of staying stuck while the client predicts on
/// (which reads as the "server stopped caring → reconcile snap" landing glitch). Also exercises the
/// developer-gated diagnostic log + the <c>sv_gameplayfix_nudgeoutofsolid</c> gate.
/// </summary>
public class NudgeOutOfSolidTests
{
    private static Entity SetupEmbeddedPlayer()
    {
        var world = new CollisionWorld();
        // A floor slab, top at Z=0 (Quake Z up).
        world.AddBrush(Brush.FromBox(new Vector3(-512f, -512f, -64f), new Vector3(512f, 512f, 0f), SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services);                 // Api.Services = services; movement + spawnfuncs registered

        Entity player = Api.Entities.Spawn();
        player.ClassName = "player";
        player.MoveType = MoveType.Walk;
        player.Solid = Solid.SlideBox;
        player.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        player.Mins = new Vector3(-16f, -16f, -24f);
        player.Maxs = new Vector3(16f, 16f, 45f);
        player.Gravity = 1f;
        player.Origin = new Vector3(0f, 0f, 20f);  // hull bottom at Z=-4 → 4u inside the floor [-64,0]
        Api.Entities.SetOrigin(player, player.Origin);
        return player;
    }

    private static bool Stuck(Entity p)
        => Api.Trace.Trace(p.Origin, p.Mins, p.Maxs, p.Origin, MoveFilter.NoMonsters, p).StartSolid;

    private static MovementInput ZeroInput => new() { FrameTime = 1f / 60f, ViewAngles = Vector3.Zero, MoveValues = Vector3.Zero };

    [Fact]
    public void Embedded_Player_Is_Nudged_Free()
    {
        Entity player = SetupEmbeddedPlayer();
        Assert.True(Stuck(player), "test setup: the player must start embedded in the floor");

        Movement.Move(player, ZeroInput);

        Assert.False(Stuck(player), "NudgeOutOfSolid should have freed the player from the floor");
        // Freed UP onto the floor, not flung away or dropped through it.
        Assert.True(player.Origin.Z is > -4f and < 64f, $"expected to rest near the floor, got z={player.Origin.Z}");
    }

    [Fact]
    public void Nudge_Logs_When_Enabled_And_Honors_The_Cvar()
    {
        var lines = new List<string>();
        Action<LogLevel, string> savedSink = Log.Sink;
        Func<int> savedDev = Log.DeveloperLevel;
        Log.Sink = (_, line) => lines.Add(line);
        Log.DeveloperLevel = () => 1;            // enable LOG_TRACE
        try
        {
            // Enabled (default): the nudge fires and logs.
            Movement.Move(SetupEmbeddedPlayer(), ZeroInput);
            Assert.Contains(lines, l => l.Contains("[nudge]"));

            // Disabled via cvar: the nudge must not run (no log).
            lines.Clear();
            Entity p2 = SetupEmbeddedPlayer();
            Api.Cvars.Set("sv_gameplayfix_nudgeoutofsolid", "0");
            Movement.Move(p2, ZeroInput);
            Api.Cvars.Set("sv_gameplayfix_nudgeoutofsolid", "1"); // restore the shared store
            Assert.DoesNotContain(lines, l => l.Contains("[nudge]"));
        }
        finally
        {
            Log.Sink = savedSink;
            Log.DeveloperLevel = savedDev;
        }
    }
}
