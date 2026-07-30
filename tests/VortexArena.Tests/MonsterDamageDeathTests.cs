using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T43 — the monster damage → pain/death/reset seam (port of <c>Monster_Damage</c> / <c>Monster_Dead</c> /
/// <c>Monster_Dead_Damage</c> / <c>Monster_Reset</c> + the <c>monsters_total</c>/<c>monsters_killed</c> globals,
/// common/monsters/sv_monsters.qc).
///
/// These pin the wiring the brief required: a monster victim now runs the MONSTER pain/death path
/// (<see cref="MonsterAI.MonsterEventDamage"/>, installed on <see cref="Entity.GtEventDamage"/> in
/// <see cref="MonsterAI.Setup"/>) instead of being treated as a player. They cover the armor-block quirk
/// (ArmorValue/100, near-zero by design), the live -100 vs corpse -50 gib thresholds, the self-knockback
/// double-push (generic calcpush + the raw force*damageforcescale, both FAITHFUL per recon §8), the natural-
/// spawn-only counters + MonsterDies bonus, and the round-restart reset.
/// </summary>
[Collection("GlobalState")]
public class MonsterDamageDeathTests
{
    // ------------------------------------------------------------------------------------------------
    //  harness
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Boot the engine facade + the gameplay registries (so <c>Monsters.ByName</c> + the status-effect catalog
    /// the spawn shield uses are live) on a big flat floor, route <see cref="Combat"/> through the real
    /// <see cref="DamageSystem"/>, and zero the global monster counters for test isolation.
    /// </summary>
    private static EngineServices Boot()
    {
        var world = new CollisionWorld();
        // Big floor slab (Quake Z up): top at Z=0 so a monster hull rests on it; gives traces something to hit.
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services);                 // Api.Services = services; registries (monsters/status effects) + DamageSystem
        MonsterAI.ResetCounters();               // isolate the process-global monsters_total/killed
        return services;
    }

    private static Monster Zombie()
    {
        Monster? z = Monsters.ByName("zombie");
        Assert.NotNull(z); // registry booted
        return z!;
    }

    /// <summary>
    /// Spawn a NATURAL (map-placed, first-life) monster: no MONSTERFLAG_SPAWNED/RESPAWNED, driven through the
    /// real <c>Monster_Spawn</c> port (<see cref="MonsterAI.SpawnFromMap"/>) so it carries the installed
    /// GtEventDamage shim + Reset delegate and counts toward monsters_total. The brief spawn shield (applied in
    /// Setup) is cleared so the monster is immediately damageable in the test (QC would tick past it).
    /// </summary>
    private static Entity SpawnNaturalZombie(Vector3 origin)
    {
        Entity e = Api.Entities.Spawn();
        e.Origin = origin;
        Api.Entities.SetOrigin(e, origin);
        bool ok = MonsterAI.SpawnFromMap(Zombie(), e, checkAppear: false);
        Assert.True(ok);
        ClearSpawnShield(e);
        return e;
    }

    /// <summary>Clear the post-spawn invulnerability so the monster takes damage immediately (test convenience).</summary>
    private static void ClearSpawnShield(Entity e)
    {
        if (StatusEffectsCatalog.Has(e, MonsterFramework.SpawnShield))
            StatusEffectsCatalog.Remove(e, MonsterFramework.SpawnShield);
    }

    private static Player NewPlayer() => new() { Flags = EntFlags.Client, Health = 1000f, TakeDamage = DamageMode.Aim };

    // ------------------------------------------------------------------------------------------------
    //  1. routing — a monster victim runs the MONSTER path, not PlayerDamage
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_LiveMonster_RunsMonsterPath_NotPlayerCorpseSemantics()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
        float startHp = m.Health; // 200 * skillmod (skill 1 default) — whatever it is, a 50-dmg hit subtracts ~50
        Assert.True(startHp > 60f);

        // F14: QC Monster_Spawn_Setup:1327-1328 seeds RES_ARMOR = bound(0.2, 0.5*MONSTER_SKILLMOD, 0.9) when unset,
        // so a fresh zombie is NOT armor-0. The monster armor BLOCK is ArmorValue/100 (a small fraction by design —
        // see MonsterEventDamage), so a 50-dmg hit takes 50*(1 - ArmorValue/100), NOT a flat 50.
        // skill 1 (default) -> SKILLMOD 0.59 -> bound(0.2, 0.295, 0.9) = 0.295 -> block 0.00295 -> take ≈ 49.8525.
        Assert.Equal(0.295f, m.ArmorValue, 3);
        float armorBlock = m.ArmorValue / 100f;
        float expectedTake = 50f * (1f - armorBlock); // bound(0, damage - bound(0, damage*block, 100), damage)

        var atk = NewPlayer();
        Combat.Damage(m, atk, atk, 50f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);

        Assert.Equal(startHp - expectedTake, m.Health, 3);
        // It is NOT treated as a player: no player-corpse marker was set, it's still alive and solid as a monster.
        Assert.False(m.IsCorpse);
        Assert.Equal(DeadFlag.No, m.DeadState);
        Assert.Equal(Solid.BBox, m.Solid);
    }

    [Fact]
    public void Damage_SpawnShielded_LiveMonster_IgnoresNonKillDamage()
    {
        Boot();
        Entity e = Api.Entities.Spawn();
        e.Origin = new Vector3(0, 0, 26);
        Api.Entities.SetOrigin(e, e.Origin);
        MonsterAI.SpawnFromMap(Zombie(), e, checkAppear: false);
        // Do NOT clear the spawn shield: a non-kill hit must be fully soaked (QC Monster_Damage:1087 early return).
        Assert.True(StatusEffectsCatalog.Has(e, MonsterFramework.SpawnShield));
        float hp = e.Health;

        var atk = NewPlayer();
        Combat.Damage(e, atk, atk, 75f, DeathTypes.FromWeapon("blaster"), e.Origin, Vector3.Zero);

        Assert.Equal(hp, e.Health, 3); // shield ate it all
    }

    // ------------------------------------------------------------------------------------------------
    //  2. self-knockback — the FAITHFUL double push (generic calcpush + raw force*damageforcescale)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_LiveMonster_AppliesDoublePushKnockback()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
        // Pin the per-monster knockback scale deterministically (the zombie zeroes it during the spawn anim;
        // a real game restores it in Think). Seed BOTH the side-table value and the edict field the generic
        // apply-push reads, so this hit reproduces the steady-state double push without ticking the brain.
        var st = MonsterAI.StateOf(m)!;
        st.DamageForceScale = 0.55f;
        m.DamageForceScale = 0.55f;
        m.Velocity = Vector3.Zero;

        var atk = NewPlayer();
        Vector3 force = new(0, 0, 100); // straight up; a stationary target's calcpush multiplier is exactly 1
        Combat.Damage(m, atk, atk, 20f, DeathTypes.FromWeapon("blaster"), m.Origin, force);

        // QC damage.qc:674 generic apply-push adds calcpush(force*0.55)=force*0.55 (mult 1 for a still target),
        // then Monster_Damage:1112 adds force*0.55 AGAIN -> total force*1.1. Both are faithful (recon §8).
        Assert.Equal(110f, m.Velocity.Z, 1);
    }

    // ------------------------------------------------------------------------------------------------
    //  3. death -> corpse (health to 0..-100): becomes a corpse, fires Death once, counts the kill
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_PastZero_BecomesCorpse_FiresDeathOnce_CountsKill()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
        Assert.Equal(0, MonsterAI.MonstersKilled);

        int deathFires = 0;
        bool Counter(ref DeathEvent ev) { if (ReferenceEquals(ev.Victim, m)) deathFires++; return false; }
        Combat.Death.Add(Counter);
        try
        {
            float t = MonsterAI.Now;
            var atk = NewPlayer();
            // One big-but-not-gibbing hit: health -> well below 0 but above -100 so it corpses (not gibs).
            Combat.Damage(m, atk, atk, m.Health + 40f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);

            Assert.Equal(DeadFlag.Dead, m.DeadState);
            Assert.Equal(Solid.Corpse, m.Solid);
            Assert.Equal(MoveType.Toss, m.MoveType);
            Assert.Equal(DamageMode.Aim, m.TakeDamage);
            Assert.True(m.Health > -100f, $"should corpse, not gib: hp={m.Health}");
            Assert.Equal(t + 5f, MonsterAI.StateOf(m)!.Lifetime, 3); // corpse lifetime = time + 5
            Assert.Equal(1, MonsterAI.MonstersKilled);               // natural kill counted
            Assert.Equal(1, deathFires);                             // Combat.Death fired exactly once
        }
        finally { Combat.Death.Remove(Counter); }
    }

    // ------------------------------------------------------------------------------------------------
    //  4. live gib threshold is the LITERAL -100 (NOT sv_gibhealth): an overkill hit removes the monster
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_BelowMinus100_Gibs_AndSchedulesRemoval()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));

        var atk = NewPlayer();
        // Drive health to ~ -150 (below the -100 live gib threshold) in one hit.
        Combat.Damage(m, atk, atk, m.Health + 150f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);

        Assert.True(m.Health <= -100f, $"hp should be below the gib threshold: {m.Health}");
        Assert.Equal(DeadFlag.Dead, m.DeadState);
        // gibbed -> a short-fuse removal think was armed (QC SUB_Remove at time+0.1).
        Assert.NotNull(m.Think);
        Assert.True(m.NextThink >= MonsterAI.Now, "gibbed corpse should have its removal think armed");
    }

    [Fact]
    public void Damage_DeathKill_AlwaysGibs_EvenWithHealthAboveMinus100()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));

        var atk = NewPlayer();
        // DEATH_KILL is the mobkill command: it forces a gib (gibbed = health<=-100 || isKill) and suppresses loot.
        Combat.Damage(m, atk, atk, m.Health + 1f, DeathTypes.Kill, m.Origin, Vector3.Zero);

        Assert.Equal(DeadFlag.Dead, m.DeadState);
        Assert.False(MonsterAI.StateOf(m)!.CanDrop); // killed by the kill command -> no loot
        Assert.NotNull(m.Think);                      // gib removal armed even though health > -100
    }

    // ------------------------------------------------------------------------------------------------
    //  5. corpse damage -> Monster_Dead_Damage path (-50 gib), no second Death event
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_OnCorpse_RoutesToDeadDamage_GibsAtMinus50_NoSecondDeath()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));

        // Kill it into a corpse (above -100 so it doesn't immediately gib): health lands around -10.
        var atk = NewPlayer();
        Combat.Damage(m, atk, atk, m.Health + 10f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);
        Assert.Equal(DeadFlag.Dead, m.DeadState);
        Assert.True(m.Health > -50f);
        float corpseHp = m.Health;

        int deathFires = 0;
        bool Counter(ref DeathEvent ev) { if (ReferenceEquals(ev.Victim, m)) deathFires++; return false; }
        Combat.Death.Add(Counter);
        try
        {
            // A small corpse hit subtracts raw (the corpse armor is irrelevant): health drops, NO new Death event.
            Combat.Damage(m, atk, atk, 20f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);
            Assert.Equal(corpseHp - 20f, m.Health, 3);
            Assert.Equal(0, deathFires); // corpse damage never re-fires the obituary

            // A bigger corpse hit pushes past -50 -> gib (removal armed, shim disarmed).
            Combat.Damage(m, atk, atk, 100f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);
            Assert.True(m.Health <= -50f);
            Assert.Null(m.GtEventDamage); // QC Monster_Dead_Damage: event_damage = func_null after gibbing
            Assert.Equal(0, deathFires);
        }
        finally { Combat.Death.Remove(Counter); }
    }

    // ------------------------------------------------------------------------------------------------
    //  6. MonsterDies bonus — only a NATURAL monster killed by a player awards the nade bonus
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Kill_NaturalMonster_ByPlayer_AwardsNadeBonus()
    {
        Boot();
        Api.Cvars.Set("g_nades", "1");
        Api.Cvars.Set("g_nades_bonus", "1");

        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
        var atk = NewPlayer();
        Assert.Equal(0f, atk.NadeBonusScore);

        Combat.Damage(m, atk, atk, m.Health + 5f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);

        // OnMonsterDies -> GiveBonus(minor) accrues toward the next bonus nade (one kill won't bank a whole one,
        // but the accrual proves the MonsterDies hook fired for a natural monster killed by a player).
        Assert.True(atk.NadeBonusScore > 0f, $"natural-monster kill should accrue a nade bonus, got {atk.NadeBonusScore}");
    }

    [Fact]
    public void Kill_SpawnedMonster_ByPlayer_AwardsNoNadeBonus()
    {
        Boot();
        Api.Cvars.Set("g_nades", "1");
        Api.Cvars.Set("g_nades_bonus", "1");

        // A command/wave-spawned monster (MONSTERFLAG_SPAWNED) — QC awards nothing for these.
        Entity e = Api.Entities.Spawn();
        Entity? m = MonsterAI.SpawnMonster(e, "zombie", null, spawnedBy: null, follow: null,
            origin: new Vector3(0, 0, 26), respawn: false, removeIfInvalid: true, moveFlags: 0);
        Assert.NotNull(m);
        ClearSpawnShield(m!);

        var atk = NewPlayer();
        Combat.Damage(m!, atk, atk, m!.Health + 5f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);

        Assert.Equal(0f, atk.NadeBonusScore); // spawned monster -> no MonsterDies bonus
    }

    // ------------------------------------------------------------------------------------------------
    //  7. counters — monsters_total / monsters_killed track NATURAL first-life spawns only
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Counters_NaturalSpawnsAndKills_TrackTotalsAndReset()
    {
        Boot();
        Assert.Equal(0, MonsterAI.MonstersTotal);
        Assert.Equal(0, MonsterAI.MonstersKilled);

        Entity a = SpawnNaturalZombie(new Vector3(0, 0, 26));
        Entity b = SpawnNaturalZombie(new Vector3(64, 0, 26));
        Entity c = SpawnNaturalZombie(new Vector3(128, 0, 26));
        Assert.Equal(3, MonsterAI.MonstersTotal);

        var atk = NewPlayer();
        Combat.Damage(a, atk, atk, a.Health + 5f, DeathTypes.FromWeapon("blaster"), a.Origin, Vector3.Zero);
        Combat.Damage(b, atk, atk, b.Health + 5f, DeathTypes.FromWeapon("blaster"), b.Origin, Vector3.Zero);
        Assert.Equal(2, MonsterAI.MonstersKilled);

        MonsterAI.ResetCounters();
        Assert.Equal(0, MonsterAI.MonstersTotal);
        Assert.Equal(0, MonsterAI.MonstersKilled);

        _ = c; // spawned to prove the total counted 3; not killed
    }

    [Fact]
    public void Counters_SpawnedMonster_NotCountedInTotalOrKilled()
    {
        Boot();
        Assert.Equal(0, MonsterAI.MonstersTotal);

        // SPAWNED (mobspawn/wave) monster — excluded from BOTH the total and the killed count.
        Entity e = Api.Entities.Spawn();
        Entity? m = MonsterAI.SpawnMonster(e, "zombie", null, spawnedBy: null, follow: null,
            origin: new Vector3(0, 0, 26), respawn: false, removeIfInvalid: true, moveFlags: 0);
        Assert.NotNull(m);
        Assert.Equal(0, MonsterAI.MonstersTotal); // a spawned monster doesn't bump monsters_total

        ClearSpawnShield(m!);
        var atk = NewPlayer();
        Combat.Damage(m!, atk, atk, m!.Health + 5f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);
        Assert.Equal(0, MonsterAI.MonstersKilled); // ...nor monsters_killed
    }

    // ------------------------------------------------------------------------------------------------
    //  8. Monster_Reset — natural monster restored to spawn; spawned monster removed
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Reset_NaturalMonster_RestoresSpawnPointAndHealth()
    {
        Boot();
        Vector3 spawn = new(0, 0, 26);
        Entity m = SpawnNaturalZombie(spawn);
        float maxHp = MonsterAI.StateOf(m)!.MaxHealth;

        // Perturb it: move, hurt, acquire an enemy/goal.
        Api.Entities.SetOrigin(m, new Vector3(300, 50, 26));
        m.Angles = new Vector3(0, 90, 0);
        m.Health = maxHp * 0.25f;
        m.Velocity = new Vector3(100, 0, 0);
        var foe = NewPlayer();
        m.Enemy = foe;
        m.GoalEntity = foe;

        Assert.NotNull(m.Reset); // the reset delegate was installed at spawn (QC this.reset = Monster_Reset)
        m.Reset!(m);

        Assert.Equal(spawn, m.Origin);
        Assert.Equal(maxHp, m.Health, 3);
        Assert.Equal(Vector3.Zero, m.Velocity);
        Assert.Null(m.Enemy);
        Assert.Null(m.GoalEntity);
    }

    [Fact]
    public void Reset_SpawnedMonster_IsRemoved()
    {
        Boot();
        Entity e = Api.Entities.Spawn();
        Entity? m = MonsterAI.SpawnMonster(e, "zombie", null, spawnedBy: null, follow: null,
            origin: new Vector3(0, 0, 26), respawn: false, removeIfInvalid: true, moveFlags: 0);
        Assert.NotNull(m);
        Assert.NotNull(m!.Reset);

        m.Reset!(m);

        // QC Monster_Reset: a MONSTERFLAG_SPAWNED monster is Monster_Remove'd (forgotten + freed).
        Assert.Null(MonsterAI.StateOf(m)); // state forgotten on removal
    }

    [Fact]
    public void Reset_AllMonsters_SweepRestoresNaturalAndRemovesSpawned()
    {
        Boot();
        Entity nat = SpawnNaturalZombie(new Vector3(0, 0, 26));
        Api.Entities.SetOrigin(nat, new Vector3(500, 0, 26)); // move it away from spawn

        Entity se = Api.Entities.Spawn();
        Entity? spawned = MonsterAI.SpawnMonster(se, "zombie", null, spawnedBy: null, follow: null,
            origin: new Vector3(64, 0, 26), respawn: false, removeIfInvalid: true, moveFlags: 0);
        Assert.NotNull(spawned);

        MonsterAI.ResetAll();

        Assert.Equal(new Vector3(0, 0, 26), nat.Origin);  // natural restored to spawn
        Assert.Null(MonsterAI.StateOf(spawned!));         // spawned removed
    }

    // ------------------------------------------------------------------------------------------------
    //  9. defaults — the two cfg-default regressions (RespawnTime 20, spawnshield fallback 2)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Defaults_RespawnTimeField_IsTwenty()
    {
        // monsters.cfg ships g_monsters_respawn_delay 20; the MonsterState default must match (was 10).
        Assert.Equal(20f, new MonsterAI.MonsterState().RespawnTime, 3);
    }

    [Fact]
    public void Defaults_SpawnShieldFallback_IsTwoSeconds()
    {
        Boot();
        // With g_monsters_spawnshieldtime UNSET, Setup falls back to 2 (was 1): a freshly-spawned monster carries
        // a spawn shield. (Proven indirectly: a non-kill hit on a just-spawned monster is fully soaked — see
        // Damage_SpawnShielded_LiveMonster_IgnoresNonKillDamage. Here we assert the shield is actually applied.)
        Entity e = Api.Entities.Spawn();
        e.Origin = new Vector3(0, 0, 26);
        Api.Entities.SetOrigin(e, e.Origin);
        MonsterAI.SpawnFromMap(Zombie(), e, checkAppear: false);
        Assert.True(StatusEffectsCatalog.Has(e, MonsterFramework.SpawnShield));
    }

    // ------------------------------------------------------------------------------------------------
    //  10. F14 — default armor is SEEDED at spawn (QC Monster_Spawn_Setup:1327-1328)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Setup_SeedsDefaultArmor_BoundBySkillMod()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));

        // QC: if(!RES_ARMOR) SetResourceExplicit(RES_ARMOR, bound(0.2, 0.5*MONSTER_SKILLMOD(this), 0.9)).
        // skill 1 (g_monsters_skill default) -> SKILLMOD = 0.5 + 1*0.09 = 0.59 -> bound(0.2, 0.295, 0.9) = 0.295.
        var st = MonsterAI.StateOf(m)!;
        float expected = XonoticGodot.Common.Math.QMath.Bound(0.2f, 0.5f * MonsterAI.SkillMod(st), 0.9f);
        Assert.Equal(0.295f, expected, 3);          // pin the concrete value for skill 1
        Assert.Equal(expected, m.ArmorValue, 3);    // ...and that Setup actually seeded it (was 0 -> full damage)
        Assert.True(m.ArmorValue > 0f, "a freshly-spawned monster must carry default armor, not 0");
    }

    // ------------------------------------------------------------------------------------------------
    //  11. F15 — INVINCIBLE soaks ordinary damage but NOT the NEEDKILL trap deathtypes (lava/slime/void/swamp)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Invincible_SoaksOrdinaryDamage_ButLavaStillHurts()
    {
        Boot();
        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
        m.SpawnFlags |= MonsterAI.MonsterFlag_Invincible; // QC MONSTERFLAG_INVINCIBLE
        float hp = m.Health;

        var atk = NewPlayer();

        // Ordinary (weapon) damage is fully soaked by INVINCIBLE (QC Monster_Damage:1085 early return).
        Combat.Damage(m, atk, atk, 75f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);
        Assert.Equal(hp, m.Health, 3);

        // LAVA is ITEM_DAMAGE_NEEDKILL -> bypasses INVINCIBLE -> the monster still takes it (armor block applies).
        Combat.Damage(m, atk, atk, 30f, DeathTypes.Lava, m.Origin, Vector3.Zero);
        float lavaTake = 30f * (1f - m.ArmorValue / 100f);
        Assert.Equal(hp - lavaTake, m.Health, 3);
        Assert.True(m.Health < hp, "lava must hurt an INVINCIBLE monster");
    }

    [Fact]
    public void Invincible_NeedKill_DeathtypesAllHurt()
    {
        Boot();
        // Each of the four ITEM_DAMAGE_NEEDKILL deathtypes (void/slime/lava/swamp) must pierce INVINCIBLE.
        foreach (string dt in new[] { DeathTypes.Void, DeathTypes.Slime, DeathTypes.Lava, DeathTypes.Swamp })
        {
            Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
            m.SpawnFlags |= MonsterAI.MonsterFlag_Invincible;
            float hp = m.Health;

            var atk = NewPlayer();
            Combat.Damage(m, atk, atk, 20f, dt, m.Origin, Vector3.Zero);
            Assert.True(m.Health < hp, $"NEEDKILL deathtype '{dt}' should pierce INVINCIBLE");
        }
    }

    // ------------------------------------------------------------------------------------------------
    //  12. F17 — a PLAYER who kills a monster scores g_monsters_score_kill (QC Monster_Dead:1049-1051)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Kill_NaturalMonster_ByPlayer_AwardsMonsterScoreKill()
    {
        Boot();
        XonoticGodot.Common.Gameplay.Scoring.GameScores.GameStopped = false; // a prior test may have frozen scoring
        Api.Cvars.Set("g_monsters_score_kill", "5");

        Entity m = SpawnNaturalZombie(new Vector3(0, 0, 26));
        var atk = NewPlayer();
        Assert.Equal(0, XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(atk, XonoticGodot.Common.Gameplay.Scoring.GameScores.Score));

        Combat.Damage(m, atk, atk, m.Health + 5f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);

        // QC GameRules_scoring_add(attacker, SCORE, +autocvar_g_monsters_score_kill) = PlayerScore_Add(.., SP_SCORE, 5).
        Assert.Equal(5, XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(atk, XonoticGodot.Common.Gameplay.Scoring.GameScores.Score));
    }

    [Fact]
    public void Kill_SpawnedMonster_ByPlayer_AwardsNoScore_UnlessScoreSpawnedSet()
    {
        Boot();
        XonoticGodot.Common.Gameplay.Scoring.GameScores.GameStopped = false;
        Api.Cvars.Set("g_monsters_score_kill", "5");

        // A command/wave-spawned monster (MONSTERFLAG_SPAWNED): QC gate is (g_monsters_score_spawned || natural),
        // so with g_monsters_score_spawned UNSET a spawned-monster kill awards nothing.
        Entity e = Api.Entities.Spawn();
        Entity? m = MonsterAI.SpawnMonster(e, "zombie", null, spawnedBy: null, follow: null,
            origin: new Vector3(0, 0, 26), respawn: false, removeIfInvalid: true, moveFlags: 0);
        Assert.NotNull(m);
        ClearSpawnShield(m!);

        var atk = NewPlayer();
        Combat.Damage(m!, atk, atk, m!.Health + 5f, DeathTypes.FromWeapon("blaster"), m.Origin, Vector3.Zero);
        Assert.Equal(0, XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(atk, XonoticGodot.Common.Gameplay.Scoring.GameScores.Score));

        // With g_monsters_score_spawned set, the SAME spawned-monster kill DOES award the score.
        Api.Cvars.Set("g_monsters_score_spawned", "1");
        Entity e2 = Api.Entities.Spawn();
        Entity? m2 = MonsterAI.SpawnMonster(e2, "zombie", null, spawnedBy: null, follow: null,
            origin: new Vector3(64, 0, 26), respawn: false, removeIfInvalid: true, moveFlags: 0);
        Assert.NotNull(m2);
        ClearSpawnShield(m2!);
        Combat.Damage(m2!, atk, atk, m2!.Health + 5f, DeathTypes.FromWeapon("blaster"), m2.Origin, Vector3.Zero);
        Assert.Equal(5, XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(atk, XonoticGodot.Common.Gameplay.Scoring.GameScores.Score));
    }
}
