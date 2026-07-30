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
/// T22 — target_* utility + func_door_secret tests (qcsrc/common/mapobjects/target/{kill,speed,spawnpoint,
/// location,changelevel,levelwarp}.qc + func/door_secret.qc). Covers the velocity math, the spawn-point/
/// location bookkeeping, the changelevel/levelwarp host seams, and the secret-door open-on-use slide chain.
///
/// Runs in the GlobalState collection (mutates Api.Services, the MapMover index, MapObjectsState, and the
/// TargetUtilities host seams).
/// </summary>
[Collection("GlobalState")]
public sealed class TargetUtilitiesTests
{
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new();
        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private TestFacade _f = null!;

    private TestFacade Boot()
    {
        _f = new TestFacade();
        Api.Services = _f;
        MapMover.ClearIndex();
        MapObjectsState.Reset();
        // clear the host seams so a prior test's wiring doesn't leak.
        TargetUtilities.NextLevelHandler = null;
        TargetUtilities.ChangeLevelHandler = null;
        TargetUtilities.RealPlayerVoteCount = null;
        TargetUtilities.CampaignLevelWarpHandler = null;
        TargetUtilities.IsCampaign = null;
        GameScores.GameStopped = false;
        _f.GameClock.Time = 1f;
        _f.GameClock.FrameTime = 1f / 60f;
        return _f;
    }

    private static Entity Spawn(string className, System.Action<Entity>? pre = null)
    {
        Entity e = Api.Entities.Spawn();
        pre?.Invoke(e);
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered");
        return e;
    }

    // =====================================================================================
    //  target_kill
    // =====================================================================================

    [Fact]
    public void TargetKill_DamagesCreatureActorToDeath()
    {
        Boot();
        Combat.System = new DamageSystem();

        Entity tk = Spawn("target_kill");
        Entity victim = new Entity
        {
            ClassName = "player",
            Flags = EntFlags.Client,
            TakeDamage = DamageMode.Yes,
            Health = 100f,
            Mins = new Vector3(-16, -16, -24),
            Maxs = new Vector3(16, 16, 45),
        };

        tk.Use!(tk, victim);
        Assert.True(victim.GetResource(ResourceType.Health) < 1f, "target_kill should drive the victim's health below 1");
    }

    [Fact]
    public void TargetKill_InactiveDoesNothing()
    {
        Boot();
        Combat.System = new DamageSystem();

        Entity tk = Spawn("target_kill");
        tk.Active = MapMover.ActiveNot;
        Entity victim = new Entity { ClassName = "player", Flags = EntFlags.Client, TakeDamage = DamageMode.Yes, Health = 100f };

        tk.Use!(tk, victim);
        Assert.Equal(100f, victim.GetResource(ResourceType.Health), 3);
    }

    [Fact]
    public void TargetKill_NonDamageableActorIgnored()
    {
        Boot();
        Combat.System = new DamageSystem();

        Entity tk = Spawn("target_kill");
        Entity wall = new Entity { ClassName = "func_wall", TakeDamage = DamageMode.No, Health = 100f };

        tk.Use!(tk, wall);
        Assert.Equal(100f, wall.GetResource(ResourceType.Health), 3);
    }

    // =====================================================================================
    //  target_speed — the velocity solver (pure math)
    // =====================================================================================

    [Fact]
    public void TargetSpeed_SetsAxisVelocityToSpeed()
    {
        Boot();
        // POSITIVE_X only, speed 500: overwrite the X velocity to +500, preserve Y/Z (no flag).
        Entity ts = Spawn("target_speed", e =>
        {
            e.Speed = 500f;
            e.SpawnFlags = TargetUtilities.SpeedPositiveX;
        });

        Entity actor = new Entity { Velocity = new Vector3(10f, 20f, 30f) };
        ts.Use!(ts, actor);

        Assert.Equal(500f, actor.Velocity.X, 2);   // forced to +500 along X
        Assert.Equal(20f, actor.Velocity.Y, 2);    // unaffected axis preserved
        Assert.Equal(30f, actor.Velocity.Z, 2);
    }

    [Fact]
    public void TargetSpeed_NegativeAxisFlipsSign()
    {
        Boot();
        Entity ts = Spawn("target_speed", e => { e.Speed = 300f; e.SpawnFlags = TargetUtilities.SpeedNegativeZ; });
        Entity actor = new Entity { Velocity = new Vector3(1f, 2f, 50f) };

        ts.Use!(ts, actor);
        Assert.Equal(-300f, actor.Velocity.Z, 2);  // forced to -300 along Z
        Assert.Equal(1f, actor.Velocity.X, 2);
        Assert.Equal(2f, actor.Velocity.Y, 2);
    }

    [Fact]
    public void TargetSpeed_DefaultSpeedIs100()
    {
        Boot();
        Entity ts = Spawn("target_speed"); // no speed set
        Assert.Equal(100f, ts.Speed, 3);
    }

    [Fact]
    public void TargetSpeed_Percentage_ScalesCurrentSpeed()
    {
        Boot();
        // PERCENTAGE + POSITIVE_X, speed 50 (%): new |X-vel| = 50% of the current X speed.
        Entity ts = Spawn("target_speed", e =>
        {
            e.Speed = 50f;
            e.SpawnFlags = TargetUtilities.SpeedPercentage | TargetUtilities.SpeedPositiveX;
        });
        Entity actor = new Entity { Velocity = new Vector3(200f, 0f, 0f) };

        ts.Use!(ts, actor);
        Assert.Equal(100f, actor.Velocity.X, 1);   // 50% of 200
    }

    // =====================================================================================
    //  target_spawnpoint
    // =====================================================================================

    [Fact]
    public void TargetSpawnpoint_SetsActorForcedSpawn()
    {
        Boot();
        Entity sp = Spawn("target_spawnpoint");
        Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client };

        sp.Use!(sp, actor);
        Assert.Same(sp, actor.SpawnPointTarg);
    }

    // =====================================================================================
    //  target_location / info_location
    // =====================================================================================

    [Fact]
    public void TargetLocation_RegistersIntoLocationsListAsPassivePoint()
    {
        Boot();
        Entity loc = Spawn("target_location", e => e.NetName = "Courtyard");

        Assert.Contains(loc, MapObjectsState.Locations);
        Assert.Equal(Solid.Not, loc.Solid);
        Assert.Null(loc.Touch);
        Assert.Equal("Courtyard", loc.NetName);
    }

    [Fact]
    public void InfoLocation_CopiesNetnameToMessage()
    {
        Boot();
        Entity loc = Spawn("info_location", e => e.NetName = "Atrium");
        Assert.Equal("Atrium", loc.Message);
        Assert.Contains(loc, MapObjectsState.Locations);
    }

    // =====================================================================================
    //  target_changelevel — host seams + multiplayer fraction
    // =====================================================================================

    [Fact]
    public void TargetChangelevel_EmptyChmap_CallsNextLevel()
    {
        Boot();
        int nextCalls = 0;
        TargetUtilities.NextLevelHandler = _ => nextCalls++;

        Entity cl = Spawn("target_changelevel"); // empty chmap -> end match
        Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client };

        cl.Use!(cl, actor);
        Assert.Equal(1, nextCalls);
    }

    [Fact]
    public void TargetChangelevel_WithChmap_CallsChangeLevel()
    {
        Boot();
        string? switched = null;
        TargetUtilities.ChangeLevelHandler = m => switched = m;

        Entity cl = Spawn("target_changelevel", e => e.ChMap = "stormkeep");
        Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client };

        cl.Use!(cl, actor);
        Assert.Equal("stormkeep", switched);
    }

    [Fact]
    public void TargetChangelevel_GameStopped_DoesNothing()
    {
        Boot();
        GameScores.GameStopped = true;
        int nextCalls = 0;
        TargetUtilities.NextLevelHandler = _ => nextCalls++;

        Entity cl = Spawn("target_changelevel");
        cl.Use!(cl, new Entity { Flags = EntFlags.Client });
        Assert.Equal(0, nextCalls);
    }

    [Fact]
    public void TargetChangelevel_Multiplayer_WaitsForFractionOfPlayers()
    {
        Boot();
        int nextCalls = 0;
        TargetUtilities.NextLevelHandler = _ => nextCalls++;

        Entity cl = Spawn("target_changelevel", e => e.SpawnFlags = TargetUtilities.ChangeLevelMultiplayer);

        // 10 real players, only 3 voted (< ceil(10 * 0.7) = 7): no change yet.
        TargetUtilities.RealPlayerVoteCount = _ => (10, 3);
        cl.Use!(cl, new Entity { Flags = EntFlags.Client });
        Assert.Equal(0, nextCalls);

        // now 7 voted (== ceil(10 * 0.7)): change happens.
        TargetUtilities.RealPlayerVoteCount = _ => (10, 7);
        cl.Use!(cl, new Entity { Flags = EntFlags.Client });
        Assert.Equal(1, nextCalls);
    }

    [Fact]
    public void TargetChangelevel_Multiplayer_IgnoresNonPlayer()
    {
        Boot();
        int nextCalls = 0;
        TargetUtilities.NextLevelHandler = _ => nextCalls++;
        TargetUtilities.RealPlayerVoteCount = _ => (1, 1);

        Entity cl = Spawn("target_changelevel", e => e.SpawnFlags = TargetUtilities.ChangeLevelMultiplayer);
        cl.Use!(cl, new Entity { ClassName = "rocket" }); // not a client
        Assert.Equal(0, nextCalls);
    }

    // =====================================================================================
    //  target_levelwarp — campaign-only host seam
    // =====================================================================================

    [Fact]
    public void TargetLevelwarp_OnlyInCampaign()
    {
        Boot();
        int warpTo = int.MinValue;
        TargetUtilities.CampaignLevelWarpHandler = n => warpTo = n;

        // not a campaign -> no-op.
        TargetUtilities.IsCampaign = () => false;
        Entity lw = Spawn("target_levelwarp", e => e.Cnt = 3);
        lw.Use!(lw, new Entity());
        Assert.Equal(int.MinValue, warpTo);

        // a campaign + cnt 3 -> warp to level index 2 (cnt-1).
        TargetUtilities.IsCampaign = () => true;
        lw.Use!(lw, new Entity());
        Assert.Equal(2, warpTo);

        // cnt 0 -> next level (-1).
        lw.Cnt = 0;
        lw.Use!(lw, new Entity());
        Assert.Equal(-1, warpTo);
    }

    // =====================================================================================
    //  target_items — the give-token apply (value-first grammar)
    // =====================================================================================

    [Fact]
    public void TargetItems_GivesResourcesAndItemFlags()
    {
        Boot();
        // QC GiveItems grammar is VALUE-FIRST: "<value> <name>". Sticky value carries across names.
        Entity ti = Spawn("target_items", e =>
        {
            e.NetName = "100 health 50 armor 1 jetpack";
            e.Message = "Loadout!";
        });

        Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client, Health = 1f };
        ti.Use!(ti, actor);

        Assert.Equal(100f, actor.GetResource(ResourceType.Health), 2);
        Assert.Equal(50f, actor.GetResource(ResourceType.Armor), 2);
        Assert.True((actor.Items & (int)ItemFlag.Jetpack) != 0, "jetpack flag should be set");
    }

    [Fact]
    public void TargetItems_GrantsPowerupStatusEffect()
    {
        Boot();
        StatusEffectsCatalog.RegisterAll();

        // "1 strength" -> grant the strength powerup for the configured duration.
        Entity ti = Spawn("target_items", e => e.NetName = "1 strength");
        // ItemsSetup seeds StrengthFinished from the cvar (default 30 if unset).
        Assert.True(ti.StrengthFinished > 0f, "target_items should seed the strength duration");

        Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client, Health = 100f };
        ti.Use!(ti, actor);

        var def = StatusEffectsCatalog.ByName("strength");
        Assert.NotNull(def);
        bool hasStrength = false;
        foreach (var s in actor.StatusEffects)
            if (s.DefId == def!.RegistryId) { hasStrength = true; break; }
        Assert.True(hasStrength, "the actor should have the strength status effect after target_items");
    }

    [Fact]
    public void TargetItems_CenterprintsMessageToActivator()
    {
        Boot();
        // QC target_items_use: if (GiveItems(...)) centerprint(actor, this.message). Prove the .message text is
        // delivered to the activator on the live path — Centerprint() pushes it down the CenterRaw notification
        // channel (→ CenterPrintPanel.Add), the same path chat /tell uses. Capture it on a recording sink.
        Notifications.RegisterAll();
        var prevSink = NotificationSystem.Sink;
        var rec = new NotificationSystem.RecordingSink();
        NotificationSystem.Sink = rec;
        try
        {
            Entity ti = Spawn("target_items", e =>
            {
                e.NetName = "100 health";
                e.Message = "Loadout!";
            });
            Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client, Health = 1f };
            ti.Use!(ti, actor);

            // Exactly one CenterRaw centerprint, targeted at the activator only, carrying the .message text.
            NotificationDispatch? raw = null;
            foreach (var d in rec.Log)
                if (d.WireType == MsgType.CenterRaw) { raw = d; break; }
            Assert.NotNull(raw);
            Assert.Equal(NotifBroadcast.OneOnly, raw!.Value.Broadcast);
            Assert.Same(actor, raw.Value.Target);
            Assert.Equal("Loadout!", raw.Value.Text);
        }
        finally
        {
            NotificationSystem.Sink = prevSink;
        }
    }

    [Fact]
    public void TargetItems_IgnoresNonPlayer()
    {
        Boot();
        Entity ti = Spawn("target_items", e => e.NetName = "100 health");
        Entity rocket = new Entity { ClassName = "rocket", Health = 1f };
        ti.Use!(ti, rocket);
        Assert.Equal(1f, rocket.GetResource(ResourceType.Health), 2); // unchanged (not a player)
    }

    [Fact]
    public void TargetItems_SpawnFlags0_NetnameParsedVerbatim()
    {
        // spawnflags==0 (default): no prefix injected; the raw "N name" token list is used as-is.
        // NormalizeItemsNetname must return the original string unchanged.
        string result = TargetUtilities.NormalizeItemsNetname("100 health 50 armor", 0);
        Assert.Equal("100 health 50 armor", result);
    }

    [Fact]
    public void TargetItems_SpawnFlags1_InjectsMaxPrefix()
    {
        // spawnflags==1 (max): QC emits "max N name" for each token; the port reproduces this by injecting
        // "max" before each name token. GiveItems.Apply then uses GiveOp.Max (take max(current, given)).
        string result = TargetUtilities.NormalizeItemsNetname("100 health 50 armor", 1);
        // Expected: each name token ("health", "armor") gets "max" prepended.
        Assert.Contains("max", result);
        // The value tokens (100, 50) pass through unchanged; "max" appears before "health" and "armor".
        Assert.Contains("100", result);
        Assert.Contains("health", result);
        Assert.Contains("50", result);
        Assert.Contains("armor", result);
        // Verify GiveItems.Apply actually uses max/cap semantics: "max 100 health" means "cap at 100".
        // GiveOp.Max = MathF.Min(v0, val) (cap at val). Actor with health=150 gets capped to 100.
        Boot();
        Entity actor = new Entity { ClassName = "player", Flags = EntFlags.Client };
        actor.SetResourceExplicit(ResourceType.Health, 150f); // actor has more health than the cap
        Entity ti = Spawn("target_items", e =>
        {
            e.NetName = "100 health";
            e.SpawnFlags = 1; // max = cap
        });
        ti.Use!(ti, actor);
        // spawnflags=1 → normalized to "100 max health" → GiveOp.Max (cap) → min(150, 100) = 100.
        Assert.Equal(100f, actor.GetResource(ResourceType.Health), 2);
    }

    [Fact]
    public void TargetItems_SpawnFlags1_GiveString_StrippedAndUnprefixed()
    {
        // A "give " prefixed netname: QC strips the "give" token and uses the remainder verbatim,
        // bypassing the prefix injection even if spawnflags!=0.
        string result = TargetUtilities.NormalizeItemsNetname("give 100 health", 1);
        Assert.Equal("100 health", result);
    }

    [Fact]
    public void TargetItems_SpawnFlags2_InjectsMinPrefix()
    {
        // spawnflags==2 (min): "min N name" for each token; GiveOp.Min = take max(v0, val) in GiveItems.
        string result = TargetUtilities.NormalizeItemsNetname("50 armor", 2);
        Assert.Contains("min", result);
        Assert.Contains("50", result);
        Assert.Contains("armor", result);
    }

    // =====================================================================================
    //  target_speaker — ACTIVATOR (BIT3) MSG_ONE seam (PlayToClientHandler)
    // =====================================================================================

    [Fact]
    public void TargetSpeaker_Activator_WithSeam_PlaysOnlyToTriggeringClient()
    {
        Boot();
        // Wire the PlayToClientHandler seam (QC soundto MSG_ONE equivalent).
        Entity? seenClient = null;
        Entity? seenEmitter = null;
        string? seenSample = null;
        TargetSpeaker.PlayToClientHandler = (client, emitter, ch, sample, vol, atten) =>
        {
            seenClient = client;
            seenEmitter = emitter;
            seenSample = sample;
        };
        try
        {
            // A targeted (has targetname) ACTIVATOR speaker.
            Entity speaker = Spawn("target_speaker", e =>
            {
                e.TargetName = "myspeaker";
                e.Noise = "sound/misc/trigger.wav";
                e.SpawnFlags = 8; // SPEAKER_ACTIVATOR = BIT(3)
            });

            // Triggering real client: Flags.Client, not a bot.
            Entity client = new Entity
            {
                ClassName = "player",
                Flags = EntFlags.Client,
            };

            speaker.Use!(speaker, client);

            // Seam should have been called exactly with the triggering client, not broadcast.
            Assert.Same(client, seenClient);
            Assert.Same(speaker, seenEmitter);
            Assert.Equal("sound/misc/trigger.wav", seenSample);
        }
        finally
        {
            TargetSpeaker.PlayToClientHandler = null;
        }
    }

    [Fact]
    public void TargetSpeaker_Activator_BotActivator_ProducesNothing()
    {
        Boot();
        // A bot activator: IsBot=true; IsRealClient gate should filter it out.
        bool seamCalled = false;
        TargetSpeaker.PlayToClientHandler = (_, _, _, _, _, _) => { seamCalled = true; };
        try
        {
            Entity speaker = Spawn("target_speaker", e =>
            {
                e.TargetName = "myspeaker";
                e.Noise = "sound/misc/trigger.wav";
                e.SpawnFlags = 8; // SPEAKER_ACTIVATOR
            });

            var bot = new Player { ClassName = "player", Flags = EntFlags.Client };
            bot.IsBot = true;

            speaker.Use!(speaker, bot);
            Assert.False(seamCalled, "bot activator must not produce any sound (QC IS_REAL_CLIENT gate)");
        }
        finally
        {
            TargetSpeaker.PlayToClientHandler = null;
        }
    }

    // =====================================================================================
    //  func_door_secret — open-on-use slide chain
    // =====================================================================================

    [Fact]
    public void DoorSecret_UseStartsTheBackSlide()
    {
        Boot();
        // A secret door brush: needs a non-zero size so t_width/t_length derive. No targetname -> shootable.
        Entity door = Spawn("func_door_secret", e =>
        {
            e.Mins = new Vector3(-32, -32, -32);
            e.Maxs = new Vector3(32, 32, 32);
            e.Size = e.Maxs - e.Mins;
            e.Origin = Vector3.Zero;
            e.Angles = new Vector3(0f, 0f, 0f); // arrow points +X (yaw 0)
        });

        Assert.Equal("door_secret", door.ClassName);
        Assert.Equal(MapMover.ActiveActive, door.Active);
        Assert.Equal(50f, door.Speed, 3);
        Assert.True((door.SpawnFlags & TargetUtilities.DoorSecretYesShoot) != 0, "an untargeted secret door is shootable");
        Assert.Equal(Vector3.Zero, door.OldOrigin); // oldorigin captured at spawn

        // at rest (origin == oldorigin): use kicks off the first SUB_CalcMove (the back/down slide).
        door.Use!(door, new Entity { ClassName = "player", Flags = EntFlags.Client });

        // SUB_CalcMove set a destination + velocity toward dest1 (Pos1), and a follow-up think.
        Assert.NotEqual(Vector3.Zero, door.Pos1);          // dest1 computed
        Assert.NotEqual(Vector3.Zero, door.FinalDest);     // SUB_CalcMove target set
        Assert.NotEqual(Vector3.Zero, door.Velocity);      // moving toward dest1
        Assert.NotNull(door.Think);
    }

    [Fact]
    public void DoorSecret_UseWhileMovingIsIgnored()
    {
        Boot();
        Entity door = Spawn("func_door_secret", e =>
        {
            e.Mins = new Vector3(-32, -32, -32);
            e.Maxs = new Vector3(32, 32, 32);
            e.Size = e.Maxs - e.Mins;
        });

        // Pretend it's mid-slide: origin != oldorigin -> fd_secret_use must early-out (no new move).
        Api.Entities.SetOrigin(door, new Vector3(100f, 0f, 0f));
        door.OldOrigin = Vector3.Zero; // SetOrigin set OldOrigin too; force the mismatch QC checks
        door.Velocity = Vector3.Zero;
        door.Pos1 = Vector3.Zero;

        door.Use!(door, new Entity { Flags = EntFlags.Client });
        Assert.Equal(Vector3.Zero, door.Pos1);     // no new dest1 computed (it was already moving)
        Assert.Equal(Vector3.Zero, door.Velocity);
    }
}
