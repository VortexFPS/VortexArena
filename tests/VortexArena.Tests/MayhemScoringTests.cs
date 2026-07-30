using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for T17 — Mayhem + Team Mayhem damage+frag scoring (QC MayhemCalculatePlayerScore, sv_mayhem.qc:145)
/// and the total_damage_dealt accrual hook (PlayerDamage_SplitHealthArmor, sv_mayhem.qc:281 /
/// sv_tmayhem.qc:147). Covers the three scoring methods against hand-computed QC values, the self-damage +
/// DEATH_FALL nullification (Damage_Calculate), the Team Mayhem SAME_TEAM accrual rule + zeroed mirror damage,
/// and the reset_map_players total_damage_dealt zeroing.
/// </summary>
[Collection("GlobalState")]
public class MayhemScoringTests
{
    private static Player NewPlayer(int team = Teams.None) =>
        new Player { Team = team, Flags = EntFlags.Client };

    public MayhemScoringTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameScores.AddPlayerScoreHook = null;   // isolate from any leaked hook
        GameScores.GameStopped = false;          // a prior test may have left scoring frozen
        GameScores.ResetTeams();                 // isolate team totals between tests
        // Isolate the global hook chains a mode's Activate() subscribes to (Combat.Death + the mutator chains),
        // so an earlier test's handlers can't fire here. The GlobalState collection serializes these tests.
        Combat.Death.Clear();
        MutatorHooks.DamageCalculate.Clear();
        GameHooks.PlayerDamageSplitHealthArmor.Clear();
        MutatorHooks.PlayerRegen.Clear();
        MutatorHooks.SetStartItems.Clear();
        MutatorHooks.SetWeaponArena.Clear();
        MutatorHooks.FilterItemDefinition.Clear();
        MutatorHooks.ForbidThrowCurrentWeapon.Clear();
    }

    // =====================================================================================
    //  MayhemCalculatePlayerScore — the three scoring methods (hand-computed QC values)
    // =====================================================================================

    [Fact]
    public void Method1_BlendsDamageAndFrags_DefaultWeights()
    {
        // defaults: upscaler=20, kill_weight=0.25, damage_weight=0.75, spawn HP+armor = 200+200 = 400.
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        Assert.Equal(20f, cfg.Upscaler);
        Assert.Equal(0.25f, cfg.KillWeight);
        Assert.Equal(0.75f, cfg.DamageWeight);
        Assert.Equal(400f, cfg.SpawnHealthArmor);

        // One full spawn-worth of damage (400) with no kills → 0.75 frags * upscaler 20 = 15 score.
        var damageOnly = NewPlayer();
        damageOnly.GtTotalDamageDealt = 400f;
        MayhemScoring.Calculate(damageOnly, cfg, teamGame: false);
        Assert.Equal(15, damageOnly.ScoreFrags);

        // One kill with no damage → 0.25 frags * upscaler 20 = 5 score.
        var killOnly = NewPlayer();
        GameScores.AddToPlayer(killOnly, GameScores.Kills, 1);
        MayhemScoring.Calculate(killOnly, cfg, teamGame: false);
        Assert.Equal(5, killOnly.ScoreFrags);

        // Combined: 400 damage (15) + 1 kill (5) = 20 score.
        var both = NewPlayer();
        both.GtTotalDamageDealt = 400f;
        GameScores.AddToPlayer(both, GameScores.Kills, 1);
        MayhemScoring.Calculate(both, cfg, teamGame: false);
        Assert.Equal(20, both.ScoreFrags);
    }

    [Fact]
    public void Method1_IsIdempotent_AddsOnlyTheDifference()
    {
        // QC recomputes the FULL target score and adds the missing delta, so calling it repeatedly with the
        // same inputs must NOT keep accumulating (the scoreboard would otherwise spiral).
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var p = NewPlayer();
        p.GtTotalDamageDealt = 400f;

        MayhemScoring.Calculate(p, cfg, teamGame: false);
        Assert.Equal(15, p.ScoreFrags);
        MayhemScoring.Calculate(p, cfg, teamGame: false); // same inputs again
        Assert.Equal(15, p.ScoreFrags);                   // unchanged (delta was 0)
    }

    [Fact]
    public void Method1_SubtractsForSuicides()
    {
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var p = NewPlayer();
        // 2 kills, 1 suicide, no damage: killCount = 2 - 0 - 1*1 = 1 → 0.25 frags * 20 = 5 score.
        GameScores.AddToPlayer(p, GameScores.Kills, 2);
        GameScores.AddToPlayer(p, GameScores.Suicides, 1);
        MayhemScoring.Calculate(p, cfg, teamGame: false);
        Assert.Equal(5, p.ScoreFrags);
    }

    [Fact]
    public void Method2_FragsOnly_WhenDamageWeightZero()
    {
        Api.Cvars.Set("g_mayhem_scoring_damage_weight", "0"); // method 2: frags only
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        Assert.Equal(0f, cfg.DamageWeight);

        var p = NewPlayer();
        GameScores.AddToPlayer(p, GameScores.Kills, 1);
        p.GtTotalDamageDealt = 400f; // ignored in method 2
        MayhemScoring.Calculate(p, cfg, teamGame: false);
        // playerKillScore = 1; upscaled = 1*20 = 20; scoreToAdd = 20.
        Assert.Equal(20, p.ScoreFrags);
    }

    [Fact]
    public void Method3_DamageOnly_WhenKillWeightZero()
    {
        Api.Cvars.Set("g_mayhem_scoring_kill_weight", "0"); // method 3: damage only
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        Assert.Equal(0f, cfg.KillWeight);

        var p = NewPlayer();
        p.GtTotalDamageDealt = 400f;
        GameScores.AddToPlayer(p, GameScores.Kills, 5); // ignored in method 3
        MayhemScoring.Calculate(p, cfg, teamGame: false);
        // playerDamageScore = (400/400*100) = 100; rounded = 100; upscaled = 100*20 = 2000; /100 = 20.
        Assert.Equal(20, p.ScoreFrags);
    }

    [Fact]
    public void NoScoring_WhenBothWeightsZero()
    {
        Api.Cvars.Set("g_mayhem_scoring_kill_weight", "0");
        Api.Cvars.Set("g_mayhem_scoring_damage_weight", "0");
        var cfg = MayhemScoring.GetConfig("g_mayhem");

        var p = NewPlayer();
        p.GtTotalDamageDealt = 400f;
        GameScores.AddToPlayer(p, GameScores.Kills, 5);
        MayhemScoring.Calculate(p, cfg, teamGame: false);
        Assert.Equal(0, p.ScoreFrags); // divide-by-zero guard: no score awarded
    }

    // =====================================================================================
    //  PlayerDamage_SplitHealthArmor — total_damage_dealt accrual (FFA)
    // =====================================================================================

    [Fact]
    public void SplitHealthArmor_AccruesEnemyDamage_AndRescores()
    {
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var attacker = NewPlayer();
        var target = NewPlayer();
        target.SetResource(ResourceType.Health, 1000f); // so damage_take isn't clamped

        // 50 damage all landing (take 50, save 0, frag_damage 50): total = 50 - max(0, 50-50-0) = 50.
        var args = new GameHooks.PlayerDamageArgs(attacker, target, damageTake: 50f, damageSave: 0f,
            force: Vector3.Zero, fragAttacker: attacker, fragDeathType: DeathTypes.FromWeapon("vortex"),
            fragDamage: 50f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: false);

        Assert.Equal(50f, attacker.GtTotalDamageDealt);
        // 50/400 * 0.75 * 20 = 1.875 → roundedDamageScore = rint(1.875*100*10)/10... recompute via the helper:
        // playerDamageScore = (50/400*100)*20*0.75 = 12.5*15 = 187.5; rounded = rint(1875)/10 = 187.5;
        // scoreToAdd = 187.5; delta = floor(187.5/100) = 1. SP_SCORE = 1.
        Assert.Equal(1, attacker.ScoreFrags);
    }

    [Fact]
    public void SplitHealthArmor_SubtractsSelfDamage()
    {
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var p = NewPlayer();
        p.SetResource(ResourceType.Health, 1000f);
        p.GtTotalDamageDealt = 100f; // pre-existing

        // self damage: attacker == target → subtract (g_mayhem_scoring_disable_selfdamage2score default 0).
        var args = new GameHooks.PlayerDamageArgs(p, p, damageTake: 30f, damageSave: 0f,
            force: Vector3.Zero, fragAttacker: p, fragDeathType: DeathTypes.FromWeapon("devastator"),
            fragDamage: 30f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: false);

        Assert.Equal(70f, p.GtTotalDamageDealt); // 100 - 30
    }

    [Fact]
    public void SplitHealthArmor_SubtractsEnvironmentalSuicide()
    {
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var victim = NewPlayer();
        victim.SetResource(ResourceType.Health, 1000f);
        victim.GtTotalDamageDealt = 200f;

        // world death (no attacker) by lava → subtract the total from the victim.
        var args = new GameHooks.PlayerDamageArgs(victim, victim, damageTake: 40f, damageSave: 0f,
            force: Vector3.Zero, fragAttacker: null, fragDeathType: DeathTypes.Lava, fragDamage: 40f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: false);

        Assert.Equal(160f, victim.GtTotalDamageDealt); // 200 - 40
    }

    [Fact]
    public void SplitHealthArmor_RespectsFullSpawnShield()
    {
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var attacker = NewPlayer();
        var target = NewPlayer();
        target.SetResource(ResourceType.Health, 1000f);
        target.SpawnShieldExpire = 999f; // shielded; g_spawnshield_blockdamage defaults to 1 (full)

        var args = new GameHooks.PlayerDamageArgs(attacker, target, damageTake: 50f, damageSave: 0f,
            force: Vector3.Zero, fragAttacker: attacker, fragDeathType: DeathTypes.FromWeapon("vortex"),
            fragDamage: 50f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: false);

        Assert.Equal(0f, attacker.GtTotalDamageDealt); // QC early-return: full spawn shield → no accrual
    }

    [Fact]
    public void SplitHealthArmor_NoAccrual_WhenDamageWeightZero()
    {
        Api.Cvars.Set("g_mayhem_scoring_damage_weight", "0");
        var cfg = MayhemScoring.GetConfig("g_mayhem");
        var attacker = NewPlayer();
        var target = NewPlayer();
        target.SetResource(ResourceType.Health, 1000f);

        var args = new GameHooks.PlayerDamageArgs(attacker, target, damageTake: 50f, damageSave: 0f,
            force: Vector3.Zero, fragAttacker: attacker, fragDeathType: DeathTypes.FromWeapon("vortex"),
            fragDamage: 50f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: false);

        Assert.Equal(0f, attacker.GtTotalDamageDealt); // QC: if (!damage_weight) return;
    }

    // =====================================================================================
    //  Damage_Calculate — self-damage + DEATH_FALL nullification (FFA, via the mode's hook)
    // =====================================================================================

    [Fact]
    public void DamageCalculate_NullifiesSelfDamage_WhenSelfDamageOff()
    {
        var m = new Mayhem();
        m.Activate(); // g_mayhem_selfdamage defaults to 0 → self-damage nullified
        try
        {
            var p = NewPlayer();
            p.SetResource(ResourceType.Health, 100f);
            var args = new MutatorHooks.DamageCalculateArgs(
                inflictor: null, attacker: p, target: p, deathType: DeathTypes.FromWeapon("devastator"),
                damage: 40f, mirrorDamage: 0f, force: Vector3.Zero, weaponEntity: null);
            MutatorHooks.DamageCalculate.Call(ref args);
            Assert.Equal(0f, args.Damage); // self-damage zeroed
        }
        finally { m.Deactivate(); }
    }

    [Fact]
    public void DamageCalculate_AlwaysNullifiesFallDamage()
    {
        var m = new Mayhem();
        m.Activate();
        try
        {
            var p = NewPlayer();
            p.SetResource(ResourceType.Health, 100f);
            // fall damage from "world" (no attacker) is always nullified for a live player.
            var args = new MutatorHooks.DamageCalculateArgs(
                inflictor: null, attacker: null, target: p, deathType: DeathTypes.Fall,
                damage: 60f, mirrorDamage: 0f, force: Vector3.Zero, weaponEntity: null);
            MutatorHooks.DamageCalculate.Call(ref args);
            Assert.Equal(0f, args.Damage);
        }
        finally { m.Deactivate(); }
    }

    [Fact]
    public void DamageCalculate_AllowsEnemyDamage()
    {
        var m = new Mayhem();
        m.Activate();
        try
        {
            var attacker = NewPlayer();
            var target = NewPlayer();
            target.SetResource(ResourceType.Health, 100f);
            var args = new MutatorHooks.DamageCalculateArgs(
                inflictor: null, attacker: attacker, target: target, deathType: DeathTypes.FromWeapon("vortex"),
                damage: 35f, mirrorDamage: 0f, force: Vector3.Zero, weaponEntity: null);
            MutatorHooks.DamageCalculate.Call(ref args);
            Assert.Equal(35f, args.Damage); // enemy damage is unmodified by the nullify rule
        }
        finally { m.Deactivate(); }
    }

    // =====================================================================================
    //  Team Mayhem — SAME_TEAM accrual rule + zeroed mirror damage
    // =====================================================================================

    [Fact]
    public void TeamMayhem_FriendlyFire_DoesNotCreditPositively()
    {
        var cfg = MayhemScoring.GetConfig("g_tmayhem");
        var attacker = NewPlayer(Teams.Red);
        var teammate = NewPlayer(Teams.Red);
        teammate.SetResource(ResourceType.Health, 1000f);
        attacker.GtTotalDamageDealt = 100f;

        // SAME_TEAM hit: must SUBTRACT, never add.
        var args = new GameHooks.PlayerDamageArgs(attacker, teammate, damageTake: 50f, damageSave: 0f,
            force: Vector3.Zero, fragAttacker: attacker, fragDeathType: DeathTypes.FromWeapon("vortex"),
            fragDamage: 50f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: true);

        Assert.Equal(50f, attacker.GtTotalDamageDealt); // 100 - 50 (friendly fire penalized)
    }

    [Fact]
    public void TeamMayhem_EnemyDamage_AccruesAndRoutesToTeam()
    {
        var cfg = MayhemScoring.GetConfig("g_tmayhem");
        var attacker = NewPlayer(Teams.Red);
        var enemy = NewPlayer(Teams.Blue);
        enemy.SetResource(ResourceType.Health, 200f);
        enemy.SetResource(ResourceType.Armor, 200f);

        // 400 damage FULLY absorbed by a 200hp+200armor enemy (no overkill) → accrue 400, recompute → +15
        // SP_SCORE, also added to the team's ST_SCORE. Per QC sv_mayhem.qc:292-295, total = frag_damage minus
        // overkill excess, where damage_take/save are bounded by the target's actual health/armor — so the
        // incoming split must fit within the target's pools to credit the full amount.
        var args = new GameHooks.PlayerDamageArgs(attacker, enemy, damageTake: 200f, damageSave: 200f,
            force: Vector3.Zero, fragAttacker: attacker, fragDeathType: DeathTypes.FromWeapon("vortex"),
            fragDamage: 400f);
        MayhemScoring.AccrueSplitHealthArmor(ref args, cfg, teamGame: true);

        Assert.Equal(400f, attacker.GtTotalDamageDealt);
        Assert.Equal(15, attacker.ScoreFrags);
        Assert.Equal(15, GameScores.TeamScore(Teams.Red, GameScores.TeamSlotScore)); // routed to the team total
    }

    [Fact]
    public void TeamMayhem_DamageCalculate_ZeroesMirrorDamage()
    {
        var tm = new TeamMayhem();
        tm.Activate();
        try
        {
            var attacker = NewPlayer(Teams.Red);
            var enemy = NewPlayer(Teams.Blue);
            enemy.SetResource(ResourceType.Health, 100f);
            var args = new MutatorHooks.DamageCalculateArgs(
                inflictor: null, attacker: attacker, target: enemy, deathType: DeathTypes.FromWeapon("vortex"),
                damage: 35f, mirrorDamage: 17f, force: Vector3.Zero, weaponEntity: null);
            MutatorHooks.DamageCalculate.Call(ref args);
            Assert.Equal(35f, args.Damage);       // enemy damage preserved
            Assert.Equal(0f, args.MirrorDamage);  // QC tmayhem: no mirror damaging
        }
        finally { tm.Deactivate(); }
    }

    // =====================================================================================
    //  reset_map_players — total_damage_dealt zeroing
    // =====================================================================================

    [Fact]
    public void ResetMapPlayers_ZeroesTotalDamageDealt_Mayhem()
    {
        var m = new Mayhem();
        var a = NewPlayer();
        var b = NewPlayer();
        a.GtTotalDamageDealt = 123f;
        b.GtTotalDamageDealt = 456f;

        m.ResetMapPlayers(new[] { a, b });

        Assert.Equal(0f, a.GtTotalDamageDealt);
        Assert.Equal(0f, b.GtTotalDamageDealt);
    }

    [Fact]
    public void ResetMapPlayers_ZeroesTotalDamageDealt_TeamMayhem()
    {
        var tm = new TeamMayhem();
        var a = NewPlayer(Teams.Red);
        a.GtTotalDamageDealt = 789f;

        tm.ResetMapPlayers(new[] { a });

        Assert.Equal(0f, a.GtTotalDamageDealt);
    }

    // =====================================================================================
    //  identity + limits (the ctor + point-limit fallbacks)
    // =====================================================================================

    [Fact]
    public void Mayhem_Identity_AndPointLimitDefault()
    {
        var m = new Mayhem();
        Assert.Equal("mayhem", m.NetName);
        Assert.Equal("Mayhem", m.DisplayName);
        Assert.False(m.TeamGame);
        Assert.Equal(1000f, m.PointLimit); // unset / -1 → gametype_init default 1000
    }

    [Fact]
    public void TeamMayhem_Identity_TeamCount_AndPointLimitDefault()
    {
        var tm = new TeamMayhem();
        Assert.Equal("tmayhem", tm.NetName);
        Assert.Equal("Team Mayhem", tm.DisplayName);
        Assert.True(tm.TeamGame);
        Assert.Equal(2, tm.TeamCount);       // default g_tmayhem_teams = 2
        Assert.Equal(1500f, tm.PointLimit);  // unset / -1 → gametype_init default 1500

        Api.Cvars.Set("g_tmayhem_teams", "3");
        Assert.Equal(3, tm.TeamCount);
        Api.Cvars.Set("g_tmayhem_teams_override", "4");
        Assert.Equal(4, tm.TeamCount);       // override >= 2 wins
    }

    [Fact]
    public void PointLimit_HonorsExplicitOverride()
    {
        var m = new Mayhem();
        Api.Cvars.Set("g_mayhem_point_limit", "500");
        Assert.Equal(500f, m.PointLimit);
        Api.Cvars.Set("g_mayhem_point_limit", "0"); // 0 = explicitly unlimited
        Assert.Equal(0f, m.PointLimit);
    }
}
