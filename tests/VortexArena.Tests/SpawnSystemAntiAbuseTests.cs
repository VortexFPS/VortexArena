using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Tests;

/// <summary>
/// Anti-abuse spawn-scoring guards (planning/spawn-system-analysis-2026-07-06.md, R0–R5). Each test drives the
/// public <see cref="SpawnSystem.SelectSpawnPoint"/> against a hand-built spawnpoint set with a controlled RNG
/// seed, exploiting the fact that <see cref="SpawnSystem"/>'s WeightedPick chooses the UNIQUE highest-priority
/// spot deterministically — so a scoring change that moves a spot to its own priority tier is observable as a
/// deterministic pick, and a change that only affects same-tier weighting is observable as a seed-swept frequency.
/// The collision world is empty, so a world-only trace is always "clear" (LOS visible) — which is exactly the
/// case the LOS penalty must fire on.
/// </summary>
[Collection("GlobalState")]
public sealed class SpawnSystemAntiAbuseTests : System.IDisposable
{
    public SpawnSystemAntiAbuseTests()
    {
        GameRegistries.Bootstrap();
        Api.Services = new EngineServices(new CollisionWorld());
        SpawnSystem.ResetTeamSpawns();
        GameScores.Teamplay = false;
    }

    public void Dispose()
    {
        // Reset the statics this class mutates so a following GlobalState test sees a clean slate. (Api.Services is
        // re-set by every test class's constructor, so it needs no teardown here.)
        SpawnSystem.ResetTeamSpawns();
        GameScores.Teamplay = false;
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static Entity Spot(NVec3 origin, float team = 0f)
    {
        Entity e = Api.Entities.Spawn();
        e.ClassName = "info_player_deathmatch";
        e.Team = team;
        Api.Entities.SetOrigin(e, origin);
        return e;
    }

    /// <summary>A solid player-sized hull linked into the collision grid (a "camper" body for R0a).</summary>
    private static Entity SolidBox(NVec3 origin)
    {
        Entity e = Api.Entities.Spawn();
        e.ClassName = "player";
        e.Solid = Solid.SlideBox;
        Api.Entities.SetSize(e, SpawnSystem.PlayerMins, SpawnSystem.PlayerMaxs);
        Api.Entities.SetOrigin(e, origin);
        return e;
    }

    private static Player LivePlayer(NVec3 origin, float team = 0f)
        => new() { Flags = EntFlags.Client, Origin = origin, Team = team, DeadState = DeadFlag.No };

    private static void Cvar(string name, string value) => Api.Cvars.Set(name, value);

    // ---- R0a: campers must not displace or delete spawnpoints ---------------------------------------------

    [Fact]
    public void R0a_CamperOnSpot_DoesNotRelocateOrDropIt()
    {
        Cvar("g_spawn_avoid_los", "0"); // isolate the in-solid gate from the LOS penalty
        Entity spot = Spot(new NVec3(0, 0, 0));
        SolidBox(new NVec3(0, 0, 0)); // an enemy body sitting exactly on the spot

        var p = new Player { Flags = EntFlags.Client };
        SpawnSystem.Reseed(1);
        SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, new List<Player>());

        Assert.NotNull(sp);                                   // still selectable (not scored -1 out of the pool)
        Assert.Same(spot, sp!.Value.Source);
        Assert.Equal(new NVec3(0, 0, 0), spot.Origin);        // NOT relocated ~70qu up by the player hull
    }

    // ---- R0b: an explicit g_spawn_furthest 0 is honored (uniform), not coerced to 0.5 ---------------------

    [Fact]
    public void R0b_SpawnFurthestZero_UsesUniformBranch_NotHalf()
    {
        Cvar("g_spawn_avoid_los", "0");
        Cvar("g_spawn_furthest", "0"); // 0 = always the uniform (near) pick; the bug read this back as 0.5

        // A near and a far spot, both good-distance (prio 10) w.r.t. one anchor, so they tie on priority and the
        // BRANCH decides: uniform → 50/50; the far (dist^5) branch → almost always the far spot. If "0" were
        // coerced to 0.5, ~half the picks would take the far branch and the near count would collapse toward 25%.
        Entity near = Spot(new NVec3(200, 0, 0));
        Entity far = Spot(new NVec3(5000, 0, 0));
        var live = new List<Player> { LivePlayer(new NVec3(0, 0, 0)) };

        int nearCount = 0;
        const int n = 200;
        for (int seed = 0; seed < n; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            if (ReferenceEquals(sp!.Value.Source, near)) nearCount++;
        }

        // Correct (always uniform) ⇒ ~50% near (~100). Coerced-to-0.5 ⇒ ~25% (~50). 70 cleanly separates them.
        Assert.True(nearCount > 70, $"expected the uniform branch (~half near), got {nearCount}/{n}");
    }

    // ---- R0c: the ACTIVE_ACTIVE gate that bots/match now target-check ------------------------------------

    [Fact]
    public void R0c_TargetCheck_RejectsInactiveSpot()
    {
        Cvar("g_spawn_avoid_los", "0");
        Entity active = Spot(new NVec3(0, 0, 0));
        Entity inactive = Spot(new NVec3(500, 0, 0));
        inactive.SpawnActive = XonoticGodot.Common.Gameplay.MapMover.ActiveNot; // deactivated (Onslaught/Assault)

        // With targetCheck (the bot/match path after R0c) the inactive spot is filtered out — only the active one.
        for (int seed = 0; seed < 20; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client },
                new List<Player>(), targetCheck: true);
            Assert.Same(active, sp!.Value.Source);
        }

        // Without target-checking, the inactive spot is eligible again (reachable across seeds).
        bool sawInactive = false;
        for (int seed = 0; seed < 40 && !sawInactive; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client },
                new List<Player>(), targetCheck: false);
            if (ReferenceEquals(sp!.Value.Source, inactive)) sawInactive = true;
        }
        Assert.True(sawInactive, "without target-checking the inactive spot should be selectable");
    }

    // ---- R1: LOS-aware demotion ---------------------------------------------------------------------------

    [Fact]
    public void R1_VisibleSpot_IsDemotedBelowAHiddenOne()
    {
        Cvar("g_spawn_avoid_los", "1");
        Cvar("g_spawn_avoid_los_distance", "1250");
        Cvar("g_spawn_furthest", "0"); // uniform branch: tiering is by priority alone

        // Enemy at origin. Spot A is 300qu away (good distance, but WITHIN LOS range and visible in the empty
        // world). Spot B is 2000qu away (beyond LOS range). With LOS on, A loses the good-distance tier and B is
        // the unique top tier ⇒ B is chosen every time.
        Entity a = Spot(new NVec3(300, 0, 0));
        Entity b = Spot(new NVec3(2000, 0, 0));
        var live = new List<Player> { LivePlayer(new NVec3(0, 0, 0)) };

        for (int seed = 0; seed < 25; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            Assert.Same(b, sp!.Value.Source);
        }

        // Control: with LOS off, A and B tie (both good distance) ⇒ A is reachable across seeds.
        Cvar("g_spawn_avoid_los", "0");
        bool sawA = false;
        for (int seed = 0; seed < 40 && !sawA; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            if (ReferenceEquals(sp!.Value.Source, a)) sawA = true;
        }
        Assert.True(sawA, "with LOS off the visible spot should tie and be reachable");
    }

    // ---- R2: death-point avoidance ------------------------------------------------------------------------

    [Fact]
    public void R2_SpotNearDeathOrigin_IsDemoted()
    {
        Cvar("g_spawn_avoid_los", "0");
        Cvar("g_spawn_avoid_death_radius", "300");
        Cvar("g_spawn_avoid_death_time", "8");
        Cvar("g_spawn_furthest", "0");

        Entity atDeath = Spot(new NVec3(500, 0, 0)); // right where the player died
        Entity elsewhere = Spot(new NVec3(2000, 0, 0));

        var p = new Player { Flags = EntFlags.Client, DeathOrigin = new NVec3(500, 0, 0), DeathTime = 0f };
        var empty = new List<Player>(); // no live players → both spots are good-distance (prio 10) before R2

        for (int seed = 0; seed < 25; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, empty);
            Assert.Same(elsewhere, sp!.Value.Source); // the death-point spot is demoted out of the top tier
        }

        // Control: radius 0 ⇒ no death penalty ⇒ the two tie and the death spot is reachable.
        Cvar("g_spawn_avoid_death_radius", "0");
        bool sawDeathSpot = false;
        for (int seed = 0; seed < 40 && !sawDeathSpot; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(p, empty);
            if (ReferenceEquals(sp!.Value.Source, atDeath)) sawDeathSpot = true;
        }
        Assert.True(sawDeathSpot, "with the radius at 0 the death-point spot should be reachable");
    }

    // ---- R3: bounded randomness in the far pick -----------------------------------------------------------

    [Fact]
    public void R3_TopFraction_NeverPicksBelowTheThreshold_ButSpreadsAcrossTheFarSet()
    {
        Cvar("g_spawn_avoid_los", "0");
        Cvar("g_spawn_furthest", "1");             // always the far branch
        Cvar("g_spawn_furthest_topfraction", "0.6");

        // Distances 1400 / 1900 / 2000 from one anchor. Far-set weights are dist^5: 1400 falls below 0.6·max
        // (2000^5), 1900 and 2000 clear it. So the near-of-far spot is never chosen, and the pick spreads across
        // the two furthest (not always the single max) — the anti-spawn-control property.
        Entity s1400 = Spot(new NVec3(1400, 0, 0));
        Entity s1900 = Spot(new NVec3(1900, 0, 0));
        Entity s2000 = Spot(new NVec3(2000, 0, 0));
        var live = new List<Player> { LivePlayer(new NVec3(0, 0, 0)) };

        int c1400 = 0, c1900 = 0, c2000 = 0;
        const int n = 120;
        for (int seed = 0; seed < n; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            Entity src = sp!.Value.Source!;
            if (ReferenceEquals(src, s1400)) c1400++;
            else if (ReferenceEquals(src, s1900)) c1900++;
            else if (ReferenceEquals(src, s2000)) c2000++;
        }

        Assert.Equal(0, c1400);                    // below 0.6·max ⇒ never chosen
        Assert.True(c1900 > 0, "the far set should include 1900qu");
        Assert.True(c2000 > 0, "the far set should include 2000qu");

        // Control: topfraction 0 ⇒ faithful dist^5 roulette ⇒ 1400 gets a (small) share and is reachable.
        Cvar("g_spawn_furthest_topfraction", "0");
        bool saw1400 = false;
        for (int seed = 0; seed < 300 && !saw1400; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            if (ReferenceEquals(sp!.Value.Source, s1400)) saw1400 = true;
        }
        Assert.True(saw1400, "with topfraction off the dist^5 roulette should reach 1400qu");
    }

    // ---- R4: enemies-only distance in teamplay ------------------------------------------------------------

    [Fact]
    public void R4_EnemiesOnlyDistance_StopsTeammatesRepellingSpawns()
    {
        GameScores.Teamplay = true;
        Cvar("g_spawn_avoid_los", "0");
        Cvar("g_spawn_furthest", "0");
        Cvar("g_spawn_distance_enemies_only", "1");

        // Spot A sits next to a live TEAMMATE; spot B sits next to a live ENEMY. With enemies-only distance the
        // teammate no longer repels A (A becomes good-distance / prio 10) while B stays near an enemy (prio 0) ⇒
        // A is the unique top tier and is chosen every time.
        Entity a = Spot(new NVec3(0, 0, 0));
        Entity b = Spot(new NVec3(2000, 0, 0));
        var forPlayer = new Player { Flags = EntFlags.Client, Team = Teams.Red };
        var live = new List<Player>
        {
            LivePlayer(new NVec3(50, 0, 0), Teams.Red),      // teammate near A
            LivePlayer(new NVec3(2050, 0, 0), Teams.Blue),   // enemy near B
        };

        for (int seed = 0; seed < 25; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(forPlayer, live);
            Assert.Same(a, sp!.Value.Source);
        }

        // Control: with enemies-only off, the teammate repels A too ⇒ both prio 0, tie ⇒ B reachable.
        Cvar("g_spawn_distance_enemies_only", "0");
        bool sawB = false;
        for (int seed = 0; seed < 40 && !sawB; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(forPlayer, live);
            if (ReferenceEquals(sp!.Value.Source, b)) sawB = true;
        }
        Assert.True(sawB, "with enemies-only off the teammate should repel spot A, making B reachable");
    }

    // ---- R5: occupied-spot re-pick ------------------------------------------------------------------------

    [Fact]
    public void R5_OccupiedSpot_IsNeverTheFinalChoice_WhenAnAlternativeExists()
    {
        Cvar("g_spawn_avoid_los", "0");
        Cvar("g_spawn_furthest", "0");
        Cvar("g_spawn_occupied_repick", "1");

        // A live player stands just off spot A — close enough that its hull overlaps A's placement box (occupied),
        // but ≥1qu away so A still ties B and C on weight (a player standing EXACTLY on a spot would drop it a
        // sub-tier on its own, masking the re-pick). All three are near the body (prio 0, a crowded pool the
        // uniform branch picks among); only A is actually overlapped. Re-pick swaps A for B/C whenever it is drawn.
        Entity a = Spot(new NVec3(0, 0, 0));
        Entity b = Spot(new NVec3(80, 0, 0));
        Entity c = Spot(new NVec3(0, 80, 0));
        var live = new List<Player> { LivePlayer(new NVec3(10, 0, 0)) }; // overlaps A's hull, 10qu from its origin

        int aCount = 0;
        const int n = 60;
        for (int seed = 0; seed < n; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            if (ReferenceEquals(sp!.Value.Source, a)) aCount++;
        }
        Assert.Equal(0, aCount); // the occupied spot is always re-picked away

        // Control: with re-pick off, the occupied spot is sometimes the final choice (Base's overlap behavior).
        Cvar("g_spawn_occupied_repick", "0");
        bool sawA = false;
        for (int seed = 0; seed < n && !sawA; seed++)
        {
            SpawnSystem.Reseed(seed);
            SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, live);
            if (ReferenceEquals(sp!.Value.Source, a)) sawA = true;
        }
        Assert.True(sawA, "with re-pick off the occupied spot should sometimes be chosen");
    }

    // ---- R0a complement: the in-solid gate must still catch BRUSH-ENTITY embedding (closed doors) ----------

    [Fact]
    public void R0a_BrushEntityOnSpot_IsStillRejected()
    {
        // The camper fix must not over-reach: a spot covered by a closed func_door/plat (Solid.Bsp) is genuinely
        // unusable and must be dropped. The gate's filter is NoMonsters — world + brush entities, no player
        // hulls — NOT WorldOnly, which would skip the door and spawn the player inside it.
        Cvar("g_spawn_avoid_los", "0");
        Cvar("g_spawnpoints_auto_move_out_of_solid", "0"); // isolate the reject path from the relocation nudge
        Entity covered = Spot(new NVec3(0, 0, 0));
        Entity clean = Spot(new NVec3(500, 0, 0));

        Entity door = Api.Entities.Spawn();               // a closed door engulfing the first spot's placement box
        door.ClassName = "func_door";
        door.Solid = Solid.Bsp;
        Api.Entities.SetSize(door, new NVec3(-64, -64, -16), new NVec3(64, 64, 160));
        Api.Entities.SetOrigin(door, new NVec3(0, 0, 0));

        SpawnSystem.Reseed(1);
        SpawnPoint? sp = SpawnSystem.SelectSpawnPoint(new Player { Flags = EntFlags.Client }, new List<Player>());

        Assert.NotNull(sp);
        Assert.Same(clean, sp!.Value.Source);             // the door-embedded spot is out of the pool
        Assert.NotSame(covered, sp!.Value.Source);
    }

    // ---- R1 preset: duel disables the LOS demotion for the match and restores it after -------------------

    [Fact]
    public void R1_DuelPreset_DisablesLosAvoid_AndRestoresOnDeactivate()
    {
        // The duel gametype preserves the 1v1 spawn-reading meta: Activate saves the host's value and forces
        // g_spawn_avoid_los 0; Deactivate restores the saved value (so duel -> DM doesn't leak the 0).
        Cvar("g_spawn_avoid_los", "1");
        var duel = new Duel();
        duel.Activate();
        try
        {
            Assert.Equal(0f, Api.Cvars.GetFloat("g_spawn_avoid_los"));
        }
        finally
        {
            duel.Deactivate();
        }
        Assert.Equal(1f, Api.Cvars.GetFloat("g_spawn_avoid_los"));
    }
}
