using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Coverage for T38 (minigame activation): the server-side <see cref="MinigameSessionManager"/> lifecycle
/// (create/join/part/end, spectator overflow, the QC "only player → end" branch, the invite error-string
/// contract), the per-game Move contracts driven through the manager (TTT/C4 end-to-end win, Pong throw+tick),
/// and the <c>minigame</c> console command verb dispatch (create/join/list/part). These mirror
/// qcsrc/common/minigames/sv_minigames.qc and each game's server "cmd" event.
/// </summary>
[Collection("GlobalState")]
public class MinigameSessionTests
{
    public MinigameSessionTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
        Cvars.Register("sv_minigames", "1");          // QC minigames.cfg: ON by default
        Cvars.Register("sv_minigames_observer", "0"); // QC minigames.cfg: don't force observers by default
        Cvars.Set("sv_minigames", "1");
        Cvars.Set("sv_minigames_observer", "0");
        Minigames.Reset();
        Minigames.RegisterAll();
    }

    private static MinigameSessionManager NewManager()
        => new(Cvars.Bool, Cvars.Int);

    private static Player NewPlayer(string name = "p", int id = 1)
        => new() { NetName = name, Flags = EntFlags.Client, PlayerId = id };

    // =========================================================================================== create

    [Fact]
    public void Create_RegistersSession_AndSeatsCreatorOnTeam1()
    {
        var mg = NewManager();
        Player p = NewPlayer("alice", 1);

        MinigameSession? s = mg.Create(p, "ttt");

        Assert.NotNull(s);
        Assert.StartsWith("ttt_", s!.NetName);          // QC "<gameid>_<entnum>"
        Assert.Contains(s, mg.Sessions);
        Assert.Same(s, mg.ActiveSessionOf(p));
        MinigamePlayer? ptr = mg.PointerOf(p);
        Assert.NotNull(ptr);
        Assert.Equal(1, ptr!.Team);                     // first joiner is team 1
        Assert.Same(s, mg.ByName(s.NetName));
    }

    [Fact]
    public void Create_RejectedWhenSvMinigamesOff()
    {
        Cvars.Set("sv_minigames", "0");
        var mg = NewManager();

        MinigameSession? s = mg.Create(NewPlayer(), "ttt");

        Assert.Null(s);
        Assert.Empty(mg.Sessions);
    }

    [Fact]
    public void Create_RejectedForUnknownGame()
    {
        var mg = NewManager();
        Assert.Null(mg.Create(NewPlayer(), "nope"));
        Assert.Empty(mg.Sessions);
    }

    [Fact]
    public void Create_RejectedForBot()
    {
        var mg = NewManager();
        var bot = NewPlayer("[BOT] X", 2);
        bot.IsBot = true;
        Assert.Null(mg.Create(bot, "ttt"));            // QC IS_REAL_CLIENT gate
    }

    // =========================================================================================== join

    [Fact]
    public void Join_SecondPlayer_GetsTeam2_ThirdBecomesSpectator()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2), c = NewPlayer("c", 3);

        MinigameSession s = mg.Create(a, "ttt")!;
        Assert.Same(s, mg.JoinByName(b, s.NetName));
        Assert.Equal(2, mg.PointerOf(b)!.Team);

        // A third joiner on a 2-team game becomes a spectator (QC join returns *_SPECTATOR_TEAM).
        Assert.Same(s, mg.JoinByName(c, s.NetName));
        Assert.Equal(MinigameSession.SpectatorTeam, mg.PointerOf(c)!.Team);
    }

    [Fact]
    public void Join_UnknownSession_ReturnsNull()
    {
        var mg = NewManager();
        Assert.Null(mg.JoinByName(NewPlayer(), "ttt_999"));
    }

    [Fact]
    public void Join_SameSessionTwice_NoOp()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1);
        MinigameSession s = mg.Create(a, "ttt")!;
        // QC minigame_addplayer: identical session → return 0 (already in). The player stays seated once.
        Assert.Null(mg.JoinByName(a, s.NetName));
        Assert.Single(s.Players);
    }

    // =========================================================================================== part / end

    [Fact]
    public void Part_LastPlayer_EndsSession()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1);
        MinigameSession s = mg.Create(a, "ttt")!;

        mg.Part(a);

        // QC: the only player parting → end_minigame (no "part" event); the session is gone + the map cleared.
        Assert.DoesNotContain(s, mg.Sessions);
        Assert.Null(mg.ActiveSessionOf(a));
        Assert.Null(mg.ByName(s.NetName));
    }

    [Fact]
    public void Part_OneOfTwo_KeepsSession_AndClearsLeaver()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2);
        MinigameSession s = mg.Create(a, "ttt")!;
        mg.JoinByName(b, s.NetName);

        mg.Part(b);

        Assert.Contains(s, mg.Sessions);               // session survives (a is still in it)
        Assert.Null(mg.ActiveSessionOf(b));            // b cleared
        Assert.Same(s, mg.ActiveSessionOf(a));         // a unaffected
    }

    [Fact]
    public void EndAll_ClearsEveryLiveSession()
    {
        var mg = NewManager();
        mg.Create(NewPlayer("a", 1), "ttt");
        mg.Create(NewPlayer("b", 2), "c4");
        Assert.Equal(2, mg.Sessions.Count);

        mg.EndAll();

        Assert.Empty(mg.Sessions);
    }

    [Fact]
    public void Create_WhileInAnotherGame_PartsTheFirst()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1);
        MinigameSession first = mg.Create(a, "ttt")!;
        MinigameSession second = mg.Create(a, "c4")!;

        // QC minigame_addplayer rm's the player from the old session before seating in the new. The first was
        // a's only session → it ends; a is now in the second.
        Assert.Same(second, mg.ActiveSessionOf(a));
        Assert.DoesNotContain(first, mg.Sessions);
        Assert.Contains(second, mg.Sessions);
    }

    // =========================================================================================== invite

    [Fact]
    public void Invite_Contract()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2);

        // No active game → "Invalid minigame".
        Assert.Equal("Invalid minigame", mg.Invite(a, b, out _));

        MinigameSession s = mg.Create(a, "ttt")!;

        // Self-invite.
        Assert.Equal("You can't invite yourself", mg.Invite(a, a, out _));
        // Null/invalid target.
        Assert.Equal("Invalid player", mg.Invite(a, null, out _));
        // Success: empty string + the session netname out.
        Assert.Equal("", mg.Invite(a, b, out string netname));
        Assert.Equal(s.NetName, netname);

        // Target already in the same game → "<name> is already playing".
        mg.JoinByName(b, s.NetName);
        Assert.Equal("b is already playing", mg.Invite(a, b, out _));
    }

    // =========================================================================================== TTT end-to-end

    [Fact]
    public void TicTacToe_EndToEnd_Win()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2);
        MinigameSession s = mg.Create(a, "ttt")!;
        mg.JoinByName(b, s.NetName);

        // Team 1 takes the top row a3,b3,c3; team 2 plays elsewhere. Drive via the manager's move forward
        // (the "move <tile>" cmd form the client board click sends).
        Assert.True(mg.ForwardMove(a, "move a3")); // team1
        Assert.True(mg.ForwardMove(b, "move a1")); // team2
        Assert.True(mg.ForwardMove(a, "move b3")); // team1
        Assert.True(mg.ForwardMove(b, "move b1")); // team2
        Assert.True(mg.ForwardMove(a, "move c3")); // team1 completes the top row

        Assert.Equal(MinigameTurn.Win, s.TurnType);
        Assert.Equal(1, s.Winner);
    }

    [Fact]
    public void TicTacToe_RejectsOutOfTurnAndOccupied()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2);
        MinigameSession s = mg.Create(a, "ttt")!;
        mg.JoinByName(b, s.NetName);

        // It's team 1's turn — team 2 can't move.
        Assert.False(mg.ForwardMove(b, "move a1"));
        // Team 1 places a1, then can't re-place it.
        Assert.True(mg.ForwardMove(a, "move a1"));
        Assert.False(mg.ForwardMove(b, "move a1"));    // occupied
    }

    [Fact]
    public void TicTacToe_SinglePlayer_SeatsAi_AndAiResponds()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1);
        MinigameSession s = mg.Create(a, "ttt")!;

        Assert.True(mg.ForwardMove(a, "singleplayer"));
        // QC: the AI takes the other team and (since it's not its turn yet) waits; after the human's move it
        // responds, so the board has 2 pieces and the turn is back to the human.
        Assert.Equal(2, TicTacToe.AiTeam(s));
        int before = s.Board!.PieceCount;
        Assert.True(mg.ForwardMove(a, "move b2"));     // human places centre
        Assert.True(s.Board.PieceCount >= before + 2); // human + AI both placed
        Assert.Equal(1, s.CurrentTeam);                // turn back to the human
    }

    // =========================================================================================== C4 end-to-end

    [Fact]
    public void ConnectFour_EndToEnd_Win()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2);
        MinigameSession s = mg.Create(a, "c4")!;
        mg.JoinByName(b, s.NetName);

        // Team 1 drops four in column 'a' (vertical win); team 2 drops in 'b'. Column move (the cmd form).
        Assert.True(mg.ForwardMove(a, "move a")); // a1
        Assert.True(mg.ForwardMove(b, "move b"));
        Assert.True(mg.ForwardMove(a, "move a")); // a2
        Assert.True(mg.ForwardMove(b, "move b"));
        Assert.True(mg.ForwardMove(a, "move a")); // a3
        Assert.True(mg.ForwardMove(b, "move b"));
        Assert.True(mg.ForwardMove(a, "move a")); // a4 → four in a column

        Assert.Equal(MinigameTurn.Win, s.TurnType);
        Assert.Equal(1, s.Winner);
    }

    // =========================================================================================== Pong

    [Fact]
    public void Pong_ThrowThenTick_BallSpawnsAndEntersPlayAndMoves()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2);
        MinigameSession s = mg.Create(a, "pong")!;
        mg.JoinByName(b, s.NetName);

        PongState st = Pong.StateOf(s)!;
        Assert.NotNull(st.Paddles[0]); // team 1 paddle
        Assert.NotNull(st.Paddles[1]); // team 2 paddle

        // QC "throw": start the round (flags → PLAY) and spawn the configured ball(s).
        Assert.True(mg.ForwardMove(a, "throw"));
        Assert.Equal(MinigameTurn.Play, s.TurnType);
        Assert.True(st.Playing);
        Assert.NotEmpty(st.Balls);
        Assert.False(st.Balls[0].InPlay);              // waiting for BallWait before launch

        // After BallWait the ball is thrown into play (Tick drives it via the manager — the real-time driver).
        mg.Tick(st.BallWait + 0.05f);
        Assert.True(st.Balls[0].InPlay);
        Assert.NotEqual(System.Numerics.Vector2.Zero, st.Balls[0].Velocity); // launched in some direction

        // A further tick advances the ball from the centre (the manager pumps Pong.Tick each frame).
        var at = st.Balls[0].Origin;
        mg.Tick(0.05f);
        Assert.NotEqual(at, st.Balls[0].Origin);

        // The session is flagged dirty while playing (so the host re-sends the snapshot each tick).
        Assert.Contains(s, mg.Dirty);
    }

    [Fact]
    public void Pong_Throw_RejectedFromSpectator()
    {
        var mg = NewManager();
        Player a = NewPlayer("a", 1), b = NewPlayer("b", 2), c = NewPlayer("c", 3),
               d = NewPlayer("d", 4), e = NewPlayer("e", 5);
        MinigameSession s = mg.Create(a, "pong")!;
        mg.JoinByName(b, s.NetName);
        mg.JoinByName(c, s.NetName);
        mg.JoinByName(d, s.NetName);
        // Pong seats up to 4 paddles; the 5th joiner is a spectator (QC PONG_SPECTATOR_TEAM).
        mg.JoinByName(e, s.NetName);
        Assert.Equal(MinigameSession.SpectatorTeam, mg.PointerOf(e)!.Team);
        // A spectator's command is rejected (QC event_blocked = team == SPECTATOR_TEAM).
        Assert.False(mg.ForwardMove(e, "throw"));
        Assert.False(Pong.StateOf(s)!.Playing);
    }

    // =========================================================================================== command verb

    [Fact]
    public void Command_MinigameVerb_CreateJoinListPartEnd()
    {
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");                 // boots Minigames + the world's manager (GameWorld.Minigames)
        // Boot re-publishes Api.Services + registers cvar DEFAULTS, but sv_minigames is only in minigames.cfg
        // (no ConfigReader in this bare test), so set it ON explicitly after Boot.
        Cvars.Set("sv_minigames", "1");
        Player a = NewPlayer("alice", 1);

        // create
        CommandContext ctx = world.Commands.Execute("minigame create pong", isServerConsole: false, caller: a);
        Assert.Contains("Created minigame session:", ctx.Output);

        // list lists every registered game as "<netname> (<DisplayName>) "
        ctx = world.Commands.Execute("minigame list", isServerConsole: false, caller: a);
        foreach (Minigame m in Minigames.All)
            Assert.Contains($"{m.NetName} ({m.DisplayName}) ", ctx.Output);

        // list-sessions includes the session we created
        ctx = world.Commands.Execute("minigame list-sessions", isServerConsole: false, caller: a);
        Assert.Contains("pong_", ctx.Output);

        // part: leaves the session
        ctx = world.Commands.Execute("minigame part", isServerConsole: false, caller: a);
        Assert.Contains("Left minigame session", ctx.Output);

        // end with no active game now reports the "not playing" line
        ctx = world.Commands.Execute("minigame end", isServerConsole: false, caller: a);
        Assert.Contains("You aren't playing any minigame", ctx.Output);
    }

    [Fact]
    public void Command_Minigame_RejectedWhenOff_AndFromConsole()
    {
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");
        Cvars.Set("sv_minigames", "1");

        // server-console (no caller) is rejected — minigame is a client command.
        CommandContext ctx = world.Commands.Execute("minigame create pong", isServerConsole: true, caller: null);
        Assert.Contains("client command", ctx.Output);

        // disabled cvar → the "not enabled" gate (QC sprint "Minigames are not enabled!").
        Cvars.Set("sv_minigames", "0");
        ctx = world.Commands.Execute("minigame create pong", isServerConsole: false, caller: NewPlayer("bob", 2));
        Assert.Contains("Minigames are not enabled!", ctx.Output);
    }

    [Fact]
    public void Command_Minigame_JoinFailurePrints()
    {
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");
        Cvars.Set("sv_minigames", "1");
        Player a = NewPlayer("alice", 1);

        CommandContext ctx = world.Commands.Execute("minigame join ttt_999", isServerConsole: false, caller: a);
        Assert.Contains("Cannot join given minigame session!", ctx.Output);
    }
}
