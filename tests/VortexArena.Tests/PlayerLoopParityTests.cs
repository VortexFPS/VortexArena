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

namespace XonoticGodot.Tests;

/// <summary>
/// Regression guards for the 2026-06-09 player-loop parity pass (planning/PLAYER_LOOP_RULES.md): the
/// can-only-die-once corpse bug (DEATH1), the damage-doesn't-pause-regen storage split (REGEN1), the
/// pickup-pauses-rot grace (REGEN2), and the player-count-scaled respawn timing + forced-respawn flag
/// (DEATH3/LOOP5). These are the bugs that made damage/health/spawning visibly wrong.
/// </summary>
public class PlayerLoopParityTests
{
    private static void Boot() => Api.Services = new EngineServices(new CollisionWorld());

    // ----- DEATH1: a respawn must clear the corpse/gib state so a player can die more than once -----

    [Fact]
    public void Respawn_ClearsCorpseAndGibState_SoPlayerCanDieAgain()
    {
        Boot();
        var p = new Player();
        p.Flags |= EntFlags.Client;

        // Simulate the post-death state DamageSystem.Killed/GibCorpse leave behind (and never reset).
        p.IsCorpse = true;
        p.Alpha = -1f;                 // fully gibbed marker
        p.DeadState = DeadFlag.Dead;
        p.BallisticsDensity = 5f;

        SpawnSystem.PutPlayerInServer(p, new SpawnPoint(new Vector3(0, 0, 50), Vector3.Zero));

        Assert.False(p.IsCorpse);                       // routes hits back through PlayerDamage (can die again)
        Assert.Equal(1f, p.Alpha, 2);                   // un-gibbed, visible
        Assert.Equal(DeadFlag.No, p.DeadState);
        Assert.Equal(0f, p.BallisticsDensity, 2);
        Assert.True(p.Health > 0f, "respawn should restore the start loadout health");
    }

    // ----- REGEN1: taking damage must pause regen (the read/write must hit the SAME field) -----

    [Fact]
    public void Regen_IsPaused_WhileDamagePauseTimerIsInFuture()
    {
        Boot();
        Api.Cvars.Set("g_balance_health_regen", "0.08");
        Api.Cvars.Set("g_balance_health_regenlinear", "0.5");
        Api.Cvars.Set("g_balance_health_regenstable", "100");
        Api.Cvars.Set("g_balance_health_rotstable", "100");

        var p = new Player { Health = 50f };
        p.Flags |= EntFlags.Client;
        var st = new ServerPlayerState();

        // The damage path writes Entity.PauseRegenFinished; the regen tick must read the SAME field. Pause active:
        p.PauseRegenFinished = 100f;   // far in the future (clock is at t=0)
        PlayerFrameLogic.Regen(p, st, 0.1f);
        Assert.Equal(50f, p.Health, 2); // no regen while the damage-pause is active

        // Pause elapsed -> regen resumes.
        p.PauseRegenFinished = -1f;
        PlayerFrameLogic.Regen(p, st, 0.1f);
        Assert.True(p.Health > 50f, $"expected regen toward stable, got {p.Health}");
    }

    // ----- REGEN2: a health/armor pickup must pause that resource's rot for the grace window -----

    [Fact]
    public void GiveResource_PausesRot_ForHealthAndArmor()
    {
        Boot(); // clock at t=0; g_balance_pause_*_rot fall back to 1s
        var e = new Entity { ClassName = "player", Health = 50f };
        e.Flags |= EntFlags.Client;

        e.GiveResource(ResourceType.Health, 10f);
        Assert.Equal(60f, e.Health, 2);
        Assert.Equal(1f, e.PauseRotHealthFinished, 2); // now(0) + g_balance_pause_health_rot(1)

        e.GiveResource(ResourceType.Armor, 5f);
        Assert.Equal(1f, e.PauseRotArmorFinished, 2);
    }

    // ----- DEATH3 / LOOP5: respawn timing scales + sets the forced-respawn flag -----

    [Fact]
    public void RespawnTiming_UsesDelayAndMax_AndForcedFlag()
    {
        Boot();
        Api.Cvars.Set("g_respawn_delay_small", "2");
        Api.Cvars.Set("g_respawn_delay_large", "2");
        Api.Cvars.Set("g_respawn_delay_max", "5");
        Api.Cvars.Set("g_forced_respawn", "0");

        var p = new Player();
        var roster = new List<Player> { p };

        RespawnTiming.Calculate(p, roster, teamplay: false);
        Assert.Equal(2f, p.RespawnTime, 2);                       // now(0) + small(2)
        Assert.Equal(5f, p.RespawnTimeMax, 2);                    // small(2) < max(5) -> now + max
        Assert.False((p.RespawnFlags & RespawnFlag.Force) != 0);  // not forced at stock defaults

        Api.Cvars.Set("g_forced_respawn", "1");
        RespawnTiming.Calculate(p, roster, teamplay: false);
        Assert.True((p.RespawnFlags & RespawnFlag.Force) != 0);   // g_forced_respawn -> RESPAWN_FORCE
    }

    [Fact]
    public void RespawnTiming_BotIsNotSpeciallyForced_RespawnsViaItsJumpButton()
    {
        Boot();
        Api.Cvars.Set("g_respawn_delay_small", "2");
        Api.Cvars.Set("g_respawn_delay_max", "5");
        Api.Cvars.Set("g_forced_respawn", "0");
        var bot = new Player { IsBot = true };
        RespawnTiming.Calculate(bot, new List<Player> { bot }, teamplay: false);
        // QC calculate_player_respawn_time (client.qc:1483-1484) arms RESPAWN_FORCE only on g_forced_respawn —
        // bots are NOT special-cased. In this port (T39) a bot DOES drive an input stream: BotBrain.ThinkProduce
        // presses JUMP while DEAD_DEAD (QC bot.qc:147) to advance the SAME button-gated DEAD_* machine a human
        // uses. Forcing a bot here would short-circuit DYING→RESPAWNING and skip DEAD_DEAD (see
        // BotLiveLoopTests.LiveLoop_DeadBot_PressesJumpWhenDeadDead_AndRespawns).
        Assert.False((bot.RespawnFlags & RespawnFlag.Force) != 0);
        // Same timing as a human at stock defaults: small(2) < max(5) -> respawn_time_max = now + max.
        Assert.Equal(2f, bot.RespawnTime, 2);
        Assert.Equal(5f, bot.RespawnTimeMax, 2);

        // ...and g_forced_respawn still forces everyone, bot included (QC: the flag is universal).
        Api.Cvars.Set("g_forced_respawn", "1");
        RespawnTiming.Calculate(bot, new List<Player> { bot }, teamplay: false);
        Assert.True((bot.RespawnFlags & RespawnFlag.Force) != 0);
    }
}
