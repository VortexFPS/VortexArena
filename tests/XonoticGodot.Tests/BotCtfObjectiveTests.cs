using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server.Bot;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the CTF bot role state machine (QC havocbot_role_ctf_* — sv_ctf.qc:1697-2217) and the
/// QC-scale item valuation (bot_pickupevalfunc — items.qc:885-979). Pins the parity fixes of 2026-07-11:
/// the six-role machine replacing the old single collapsed CTF role, and item values on the
/// BOT_PICKUP_RATING scale (thousands) instead of 0..1.
/// </summary>
[Collection("GlobalState")]
public class BotCtfObjectiveTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        return es;
    }

    private static Player NewPlayer(int team, Vector3 origin = default)
        => new Player { Team = team, Flags = EntFlags.Client, Origin = origin, Health = 100f, MaxHealth = 100f };

    private static (Ctf ctf, FlagState red, FlagState blue) NewCtf()
    {
        var ctf = new Ctf();
        var red = ctf.SpawnFlag(Teams.Red, new Vector3(1000, 0, 0));
        var blue = ctf.SpawnFlag(Teams.Blue, new Vector3(-1000, 0, 0));
        ctf.Activate();
        return (ctf, red, blue);
    }

    private static BotBrain NewBrain(Ctf ctf, Player bot, params Player[] others)
    {
        var brain = new BotBrain(bot) { GameType = ctf, GameTypeNetName = "ctf" };
        var all = new List<Player> { bot };
        all.AddRange(others);
        brain.PlayerProvider = () => all;
        return brain;
    }

    [Fact]
    public void SoloBot_ResetRole_PicksOffense_AndPushesEnemyBase()
    {
        Facade();
        var (ctf, _, blue) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(900, 0, 0));
        var brain = NewBrain(ctf, bot);
        var rater = new GoalRater();

        BotObjectiveRoles.RoleCtf(brain, rater);

        // QC reset_role: no teammates → offense; the offense rating routes at the enemy base.
        Assert.Equal(CtfBotRole.Offense, brain.CtfRole);
        Assert.True(rater.HasGoal);
        Assert.Equal(blue.HomeOrigin, rater.Best.Position);
    }

    [Fact]
    public void OneTeammate_InitialReset_PicksDefense()
    {
        Facade();
        var (ctf, _, _) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(900, 0, 0));
        var mate = NewPlayer(Teams.Red, new Vector3(800, 0, 0));
        var brain = NewBrain(ctf, bot, mate);
        var rater = new GoalRater();

        BotObjectiveRoles.RoleCtf(brain, rater);

        // QC reset_role "bots spawn all at once" defaults: exactly one teammate → defense.
        Assert.Equal(CtfBotRole.Defense, brain.CtfRole);
    }

    [Fact]
    public void CarryingEnemyFlag_BecomesCarrier_AndRoutesHome()
    {
        Facade();
        var (ctf, red, blue) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(-900, 0, 0));
        var brain = NewBrain(ctf, bot);
        var rater = new GoalRater();

        ctf.FlagTouch(blue.Entity!, bot); // steal the blue flag
        Assert.Same(blue, ctf.CarriedBy(bot));

        BotObjectiveRoles.RoleCtf(brain, rater);

        Assert.Equal(CtfBotRole.Carrier, brain.CtfRole);
        Assert.True(rater.HasGoal);
        Assert.Equal(red.HomeOrigin, rater.Best.Position); // run it home to capture
    }

    [Fact]
    public void OwnFlagStolen_BecomesRetriever_AndChasesTheThief()
    {
        Facade();
        var (ctf, red, _) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(900, 0, 0));
        var thief = NewPlayer(Teams.Blue, new Vector3(700, 0, 0));
        var brain = NewBrain(ctf, bot, thief);
        var rater = new GoalRater();

        ctf.FlagTouch(red.Entity!, thief); // blue steals our flag
        Assert.Same(red, ctf.CarriedBy(thief));

        BotObjectiveRoles.RoleCtf(brain, rater);

        // QC reset_role: our flag away → retriever; the retriever rating routes at the carrier.
        Assert.Equal(CtfBotRole.Retriever, brain.CtfRole);
        Assert.True(rater.HasGoal);
        Assert.Equal(thief.Origin, rater.Best.Position);
        Assert.Same(thief, rater.Best.Target);
    }

    [Fact]
    public void EnemyFlagTakenByTeammate_ResetPicksMiddle()
    {
        Facade();
        var (ctf, _, blue) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(900, 0, 0));
        var mate = NewPlayer(Teams.Red, new Vector3(-900, 0, 0));
        var brain = NewBrain(ctf, bot, mate);
        var rater = new GoalRater();

        ctf.FlagTouch(blue.Entity!, mate); // teammate steals the enemy flag
        Assert.Same(blue, ctf.CarriedBy(mate));

        BotObjectiveRoles.RoleCtf(brain, rater);

        // QC reset_role: enemy flag taken (and ours home) → go middle to intercept pursuers.
        Assert.Equal(CtfBotRole.Middle, brain.CtfRole);
    }

    [Fact]
    public void Offense_SwitchesToEscort_WhenCarrierIsClearOfOurBase()
    {
        Facade();
        var (ctf, _, blue) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(0, 500, 0));
        var mate = NewPlayer(Teams.Red, new Vector3(-200, 0, 0)); // carrier, >700 from our base at (1000,0,0)
        var brain = NewBrain(ctf, bot, mate);
        var rater = new GoalRater();

        ctf.FlagTouch(blue.Entity!, mate);
        brain.CtfRole = CtfBotRole.Offense; // already committed to offense

        BotObjectiveRoles.RoleCtf(brain, rater);

        // QC havocbot_role_ctf_offense: enemy flag taken and far from our base → escort the carrier.
        Assert.Equal(CtfBotRole.Escort, brain.CtfRole);
        Assert.Same(bot, brain.Bot); // sanity
        Assert.NotEqual(0f, brain.CtfRoleTimeout); // escort stamped its timeout
    }

    [Fact]
    public void Retriever_RevertsViaReset_WhenFlagIsHome()
    {
        Facade();
        var (ctf, _, _) = NewCtf();
        var bot = NewPlayer(Teams.Red, new Vector3(900, 0, 0));
        var brain = NewBrain(ctf, bot);
        var rater = new GoalRater();

        brain.CtfRole = CtfBotRole.Retriever;
        brain.CtfRoleTimeout = 999f;

        BotObjectiveRoles.RoleCtf(brain, rater);

        // Our flag is at base → the temporary retriever stint ends; solo bot re-balances to offense.
        Assert.Equal(CtfBotRole.Offense, brain.CtfRole);
    }

    [Fact]
    public void ItemValue_IsOnTheQcPickupRatingScale()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.Red, Vector3.Zero);
        bot.Health = 50f; // hurting → a 50 HP pickup is worth its full 5000 base (c = 1)
        var brain = new BotBrain(bot);
        brain.PlayerProvider = () => new List<Player> { bot };

        Entity item = es.EntityTable.Spawn();
        item.ClassName = "item_health_medium";
        item.Flags = EntFlags.Item;
        item.Solid = Solid.Trigger;
        item.Origin = new Vector3(100, 0, 0);
        item.SetResourceExplicit(ResourceType.Health, 50f);

        var rater = new GoalRater();
        rater.Start();
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);

        // QC healtharmor_pickupevalfunc: m_botvalue 5000 × min(2, itemhealth/health) = 5000 here; at
        // ratingscale 10000 (×0.0001) the goal rating lands in the thousands. The old 0..1 ItemValue put
        // this at ~2.5 — items lost to EVERYTHING and bots never detoured for pickups.
        Assert.True(rater.HasGoal);
        Assert.True(rater.Best.Rating > 1000f,
            $"needed-health item should rate in the thousands (QC scale), got {rater.Best.Rating}");
    }
}
