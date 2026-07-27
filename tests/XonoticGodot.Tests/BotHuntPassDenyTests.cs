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
/// Tests for the 2026-07-11 bot behavior batch #3: monster targeting (QC g_bot_targets — bots were blind
/// to Invasion waves), the Invasion hunt role + player-attack veto (QC sv_invasion.qc:426), bot carrier
/// flag passing (port improvement), and Duel big-item denial (port improvement).
/// </summary>
[Collection("GlobalState")]
public class BotHuntPassDenyTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        return es;
    }

    private static Player NewPlayer(int team, Vector3 origin = default)
        => new Player
        {
            Team = team, Flags = EntFlags.Client, Origin = origin,
            Health = 100f, MaxHealth = 100f, TakeDamage = DamageMode.Aim,
        };

    private static Entity SpawnMonster(EngineServices es, Vector3 origin)
    {
        Entity m = es.EntityTable.Spawn();
        m.ClassName = "monster_zombie";
        m.Flags = EntFlags.Monster;
        m.Origin = origin;
        m.Health = 100f;
        m.TakeDamage = DamageMode.Aim;
        return m;
    }

    [Fact]
    public void ChooseEnemy_TargetsMonsters()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot } };
        Entity monster = SpawnMonster(es, new Vector3(400, 0, 0));

        // One think: the enemy scan must pick the monster up (QC g_bot_targets includes monsters —
        // the port's old players-only roster left bots blind to them).
        brain.ThinkProduce(bot, 0.05f);

        Assert.Same(monster, bot.Enemy);
    }

    [Fact]
    public void InvasionForbidHook_VetoesPlayers_AllowsMonsters()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var otherPlayer = NewPlayer(Teams.None, new Vector3(300, 0, 0));
        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot, otherPlayer } };
        Entity monster = SpawnMonster(es, new Vector3(600, 0, 0)); // farther than the player

        // The GameWorld Invasion arm's hook (QC inv Bot_ForbidAttack: only monsters are legal targets).
        BotBrain.ForbidAttackHook = (self, targ) => (targ.Flags & EntFlags.Monster) == 0;
        try
        {
            brain.ThinkProduce(bot, 0.05f);
            // The nearer PLAYER is vetoed; the monster is acquired instead.
            Assert.Same(monster, bot.Enemy);
        }
        finally
        {
            BotBrain.ForbidAttackHook = null;
        }
    }

    [Fact]
    public void RoleInvasion_RoutesTowardTheWave()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot } };
        Entity monster = SpawnMonster(es, new Vector3(2000, 0, 0));

        Assert.Equal("RoleInvasion", BotRoles.ChooseRole("inv").Method.Name);

        var rater = new GoalRater();
        BotObjectiveRoles.RoleInvasion(brain, rater);
        Assert.True(rater.HasGoal);
        Assert.Same(monster, rater.Best.Target);
    }

    [Fact]
    public void DyingCarrier_PassesToNearbyTeammate()
    {
        Facade();
        var ctf = new Ctf();
        ctf.SpawnFlag(Teams.Red, new Vector3(1000, 0, 0));
        var blue = ctf.SpawnFlag(Teams.Blue, new Vector3(-1000, 0, 0));
        ctf.Activate();

        var bot = NewPlayer(Teams.Red, new Vector3(-900, 0, 0));
        var mate = NewPlayer(Teams.Red, new Vector3(-700, 0, 0)); // inside g_ctf_pass_radius (500)
        var brain = new BotBrain(bot, skill: 6f) { GameType = ctf, GameTypeNetName = "ctf" };
        brain.PlayerProvider = () => new List<Player> { bot, mate };

        ctf.FlagTouch(blue.Entity!, bot);
        Assert.Same(blue, ctf.CarriedBy(bot));

        bot.Health = 25f; // about to die → hand the flag off
        var rater = new GoalRater();
        BotObjectiveRoles.RoleCtf(brain, rater);

        Assert.Null(ctf.CarriedBy(bot));                 // no longer carrying
        Assert.Equal(FlagStatus.Passing, blue.Status);   // the flag is in flight
        Assert.Same(mate, blue.PassTarget);              // toward the escort
        Assert.NotEqual(CtfBotRole.Carrier, brain.CtfRole); // role re-balanced off carrier
    }

    [Fact]
    public void HealthyCarrier_DoesNotPass()
    {
        Facade();
        var ctf = new Ctf();
        var red = ctf.SpawnFlag(Teams.Red, new Vector3(1000, 0, 0));
        var blue = ctf.SpawnFlag(Teams.Blue, new Vector3(-1000, 0, 0));
        ctf.Activate();

        var bot = NewPlayer(Teams.Red, new Vector3(-900, 0, 0));
        var mate = NewPlayer(Teams.Red, new Vector3(-700, 0, 0));
        var brain = new BotBrain(bot, skill: 6f) { GameType = ctf, GameTypeNetName = "ctf" };
        brain.PlayerProvider = () => new List<Player> { bot, mate };

        ctf.FlagTouch(blue.Entity!, bot);
        var rater = new GoalRater();
        BotObjectiveRoles.RoleCtf(brain, rater);

        Assert.Same(blue, ctf.CarriedBy(bot)); // healthy carrier keeps running it home
        Assert.Equal(CtfBotRole.Carrier, brain.CtfRole);
        Assert.True(rater.HasGoal);
        Assert.Equal(red.HomeOrigin, rater.Best.Position);
    }

    [Fact]
    public void HealthItem_IsRated_UpToTheOverhealLimit_NotTheSpawnHealth()
    {
        // QC healtharmor_pickupevalfunc compares against the ITEM's max_health, which is 200 for every stock
        // health item — NOT the player's 100 spawn health. A bot sitting at 100/100 still wants the mega
        // because it overheals to 200. Gating on Player.MaxHealth (100) made every health item rate 0 for a
        // healthy bot, and GoalRater.Rate drops a <= 0 rating entirely, so mega health was never a candidate.
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero); // spawn health 100, resource limit 200
        Entity mega = es.EntityTable.Spawn();
        mega.ClassName = "item_health_mega";
        mega.Flags = EntFlags.Item;
        mega.Solid = Solid.Trigger;
        mega.Origin = new Vector3(100, 0, 0);
        mega.SetResourceExplicit(ResourceType.Health, 100f);

        var brain = new BotBrain(bot, skill: 5f) { PlayerProvider = () => new List<Player> { bot } };
        var rater = new GoalRater();
        rater.Start();
        BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);

        Assert.True(rater.HasGoal, "a 100 HP bot must still rate the mega (it overheals to 200)");
        Assert.True(rater.Best.Rating > 1000f, $"expected the QC-scale health rating, got {rater.Best.Rating}");
    }

    [Fact]
    public void DuelBot_DeniesBigItems_WhenToppedUp()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        // Topped up at the OVERHEAL LIMIT (200), the only state with genuinely no use for a mega — that is
        // what makes the Duel denial floor observable rather than masked by the natural rating.
        bot.Health = 200f;
        Entity mega = es.EntityTable.Spawn();
        mega.ClassName = "item_health_mega";
        mega.Flags = EntFlags.Item;
        mega.Solid = Solid.Trigger;
        mega.Origin = new Vector3(100, 0, 0);
        mega.SetResourceExplicit(ResourceType.Health, 100f);

        float RateWith(float skill, GameType? gt)
        {
            var brain = new BotBrain(bot, skill: skill) { GameType = gt };
            brain.PlayerProvider = () => new List<Player> { bot };
            var rater = new GoalRater();
            rater.Start();
            BotRoles.GoalrateItems(brain, rater, bot.Origin, 10000f);
            return rater.HasGoal ? rater.Best.Rating : 0f;
        }

        // Outside Duel (or low skill in Duel): a topped-up bot has no use for the mega — rating ~0.
        Assert.Equal(0f, RateWith(8f, null));
        Assert.Equal(0f, RateWith(5f, new Duel()));
        // High-skill Duel bot: denial floor — sweep the mega anyway to starve the opponent.
        float denial = RateWith(8f, new Duel());
        Assert.True(denial > 1000f, $"duel denial floor should rate the mega, got {denial}");
    }
}
