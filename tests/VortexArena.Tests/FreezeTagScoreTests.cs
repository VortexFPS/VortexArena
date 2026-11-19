using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Pins the Freeze Tag freeze-score matrix (QC <c>freezetag_Add_Score</c>, sv_freezetag.qc:162-181). The three
/// QC branches are:
///  - <c>attacker == targ</c>  → victim SCORE −1 (froze your own dumb self, counted as suicide already);
///  - <c>IS_PLAYER(attacker)</c> → teammate: attacker −1 + victim −1; enemy: attacker +1 + victim −1;
///  - else (NULL / non-player) → NOTHING ("got frozen by the gametype rules themselves").
/// The regression this guards: folding the NULL case into the self branch wrongly cost the victim 1 point on a
/// rules-driven freeze.
/// </summary>
[Collection("GlobalState")]
public class FreezeTagScoreTests
{
    public FreezeTagScoreTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameScores.RegisterAll(); // ScoreFrags routes through GameScores.Score; needs the registry
    }

    private static Player P(int team) => new Player { Team = team, Flags = EntFlags.Client };

    [Fact]
    public void LethalHit_PreservesFrozenStatusUntilThaw()
    {
        StatusEffectsCatalog.RegisterAll();
        var ft = new FreezeTag();
        Player victim = P(Teams.Red);
        victim.TakeDamage = DamageMode.Yes;
        victim.SetResource(ResourceType.Health, 100f);
        ft.Activate();
        try
        {
            new DamageSystem().Apply(new DamageInfo
            {
                Target = victim,
                Attacker = P(Teams.Blue),
                Amount = 120f,
                DeathType = DeathTypes.FromWeapon("blaster"),
            });

            StatusEffectDef frozenStatus = Assert.IsType<StatusEffectDef>(StatusEffectsCatalog.Frozen);
            Assert.True(ft.IsFrozen(victim));
            Assert.False(victim.IsDead);
            Assert.True(StatusEffectsCatalog.Has(victim, frozenStatus));
            Assert.Equal(0f, Assert.Single(victim.StatusEffects).ExpireTime);

            ft.Unfreeze(victim);
            Assert.False(StatusEffectsCatalog.Has(victim, frozenStatus));
        }
        finally
        {
            ft.Deactivate();
        }
    }

    [Theory]
    [InlineData("0", 26f)]
    [InlineData("1", 25f)]
    public void FrozenHazard_RelocatesWithoutChangingResources(string cvar, float expectedHeight)
    {
        Api.Cvars.Set("g_frozen_damage_trigger", cvar);
        var ft = new FreezeTag();
        Player victim = P(Teams.Red);
        victim.TakeDamage = DamageMode.Yes;
        victim.SetResource(ResourceType.Armor, 25f);
        victim.Mins = new Vector3(-16f, -16f, -24f);
        victim.Velocity = new Vector3(0f, 0f, -500f);
        victim.SpawnPointTarg = new Entity { Origin = new Vector3(300f, 50f, 25f), Angles = new Vector3(0f, 90f, 0f) };
        ft.Activate();
        try
        {
            ft.Freeze(victim, attacker: null);
            new DamageSystem().Apply(new DamageInfo { Target = victim, Amount = 1000f, DeathType = DeathTypes.Lava });

            Assert.True(ft.IsFrozen(victim));
            Assert.Equal(1f, victim.GetResource(ResourceType.Health));
            Assert.Equal(25f, victim.GetResource(ResourceType.Armor));
            Assert.Equal(new Vector3(300f, 50f, expectedHeight), victim.Origin);
            Assert.Equal(victim.Origin, victim.OldOrigin);
            Assert.Equal(Vector3.Zero, victim.Velocity);
            Assert.True(victim.FixAngle);
        }
        finally
        {
            ft.Deactivate();
        }
    }

    [Fact]
    public void NullAttacker_RulesFreeze_AwardsNoScore()
    {
        var ft = new FreezeTag();
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker: null); // QC else branch: gametype-rules freeze, no attacker

        // QC "// else nothing - got frozen by the gametype rules themselves": victim keeps its score.
        Assert.Equal(0, victim.ScoreFrags);
        Assert.True(ft.IsFrozen(victim)); // still frozen — only the SCORE side differs from a player freeze
    }

    [Fact]
    public void SelfFreeze_CostsVictimOnePoint()
    {
        var ft = new FreezeTag();
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker: victim); // QC attacker == targ

        Assert.Equal(-1, victim.ScoreFrags);
    }

    [Fact]
    public void EnemyFreeze_AttackerPlusOne_VictimMinusOne()
    {
        var ft = new FreezeTag();
        Player attacker = P(Teams.Blue);
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker); // QC IS_PLAYER && !SAME_TEAM

        Assert.Equal(+1, attacker.ScoreFrags);
        Assert.Equal(-1, victim.ScoreFrags);
    }

    [Fact]
    public void TeammateFreeze_BothMinusOne()
    {
        var ft = new FreezeTag();
        Player attacker = P(Teams.Red);
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker); // QC IS_PLAYER && SAME_TEAM

        Assert.Equal(-1, attacker.ScoreFrags);
        Assert.Equal(-1, victim.ScoreFrags);
    }

    // ---- ReviveTick accumulation + the networkable entity mirror (T41/T46 finding 11/16) ----

    [Fact]
    public void ReviveTick_NearbyTeammate_AccumulatesAndMirrorsToEntityField()
    {
        var ft = new FreezeTag();
        Player frozen = P(Teams.Red);
        Player reviver = P(Teams.Red); // same team, co-located → in reviving range (origin-distance fallback)

        ft.Freeze(frozen, attacker: null); // rules freeze (no score side effects)
        Assert.True(ft.IsFrozen(frozen));
        Assert.Equal(0f, frozen.ReviveProgress); // entity mirror starts at 0

        ft.SetRoster(new[] { frozen, reviver });
        ft.ReviveTick(0.5f); // 0.5s * default revive speed (0.4/s) = 0.2 progress

        // The per-player FrozenState accumulated, AND the snapshot-facing entity field mirrors it (the wire
        // ServerNet networks as NetEntityState.ReviveProgress → the client thaw ring).
        Assert.True(frozen.ReviveProgress > 0f);
        Assert.Equal(ft.Frozen[frozen].ReviveProgress, frozen.ReviveProgress, 5);

        // A non-frozen roster member reads 0 on the entity mirror.
        Assert.Equal(0f, reviver.ReviveProgress);
    }

    [Fact]
    public void ReviveTick_FullProgress_ThawsAndResetsEntityMirror()
    {
        var ft = new FreezeTag();
        Player frozen = P(Teams.Red);
        Player reviver = P(Teams.Red);

        ft.Freeze(frozen, attacker: null);
        ft.SetRoster(new[] { frozen, reviver });

        // Enough cumulative time to cross 1.0 (default speed 0.4/s → ~2.5s of revive). On reaching 1.0 the
        // player is thawed and the entity mirror falls back to 0 (Unfreeze reset the FrozenState).
        for (int i = 0; i < 10; i++)
            ft.ReviveTick(0.5f);

        Assert.False(ft.IsFrozen(frozen));
        Assert.Equal(0f, frozen.ReviveProgress);
    }
}
