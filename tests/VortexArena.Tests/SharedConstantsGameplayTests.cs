using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class SharedConstantsGameplayTests
{
    private static GameWorld NewWorld() => new(new CollisionWorld(),
    [
        new EntityDict("worldspawn"),
        new EntityDict("info_player_deathmatch", new Vector3(0, 0, 64)),
        new EntityDict("info_player_deathmatch", new Vector3(256, 0, 64)),
    ]);

    [Theory]
    [InlineData(45f, 5f)]
    [InlineData(25f, 0.3f)]
    public void DeathTickRelinksTheCorpseHullAndRespawnRestoresTheStandingHull(float liveMax, float corpseMax)
    {
        var world = NewWorld();
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars =>
            {
                cvars.Set("g_warmup", "0");
                cvars.Set("g_spawnshieldtime", "0");
            });
            Player player = world.Clients.ClientConnect(isBot: true).Player;
            PinInPlace(player);
            Api.Entities.SetSize(player, new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, liveMax));
            Combat.Damage(player, null, null, player.Health, "shotgun", player.Origin, Vector3.Zero);

            Entity shooter = Api.Entities.Spawn();
            shooter.DpHitContentsMask = SuperContents.Solid | SuperContents.Body | SuperContents.Corpse;
            float aboveCorpse = (liveMax + corpseMax) * 0.5f;
            Vector3 highTrace = player.Origin + new Vector3(-32f, 0f, aboveCorpse);
            Assert.True(player.IsCorpse);
            Assert.Equal(liveMax, player.Maxs.Z);
            Assert.Same(player, TraceAcross(highTrace, shooter));

            world.Frame(SimulationLoop.TicRate);

            Assert.Equal(-24f, player.Mins.Z);
            Assert.Equal(corpseMax, player.Maxs.Z, 4);
            Assert.Equal(16f, player.Maxs.X);
            Assert.Equal(16f, player.Maxs.Y);
            Assert.Null(TraceAcross(highTrace, shooter));
            Assert.Same(player, TraceAcross(player.Origin + new Vector3(-32f, 0f, -12f), shooter));
            world.Frame(SimulationLoop.TicRate);
            Assert.Equal(corpseMax, player.Maxs.Z, 4);

            Assert.True(world.Clients.Spawn(player));
            Assert.Equal(45f, player.Maxs.Z);
            Assert.False(player.IsCorpse);
        }
        finally { world.Shutdown(); }
    }

    private static Entity? TraceAcross(Vector3 start, Entity shooter)
        => Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, start + new Vector3(64f, 0f, 0f), MoveFilter.Normal, shooter).Ent;

    private static void PinInPlace(Player player)
    {
        player.MoveType = MoveType.Noclip;
        player.Velocity = Vector3.Zero;
        Api.Entities.SetOrigin(player, new Vector3(0f, 0f, 64f));
    }

    [Theory]
    [InlineData(30, false, 20)]
    [InlineData(30, true, 19)]
    [InlineData(6, true, 6)]
    public void MapBallotReservesOneOfTwentyEntriesForAbstain(int requested, bool abstain, int maps)
    {
        var world = NewWorld();
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars =>
            {
                cvars.Set("g_maplist_votable", requested.ToString(System.Globalization.CultureInfo.InvariantCulture));
                cvars.Set("g_maplist_votable_abstain", abstain ? "1" : "0");
            });
            world.MapVote.Start(Enumerable.Range(1, 40).Select(i => "map" + i), 2, 30f);
            Assert.Equal(maps + (abstain ? 1 : 0), world.MapVote.Candidates.Count);
            Assert.Equal("map" + maps, world.MapVote.Candidates[maps - 1].MapName);
            Assert.Equal(abstain, world.MapVote.Candidates[^1].IsAbstain);
        }
        finally { world.Shutdown(); }
    }

    [Theory]
    [InlineData("alien", ModelSpecies.Alien)]
    [InlineData("robot_shiny", ModelSpecies.RobotShiny)]
    [InlineData("human", ModelSpecies.Human)]
    public void SpawnSidecarSpeciesFeedsWorldBloodAndCopiedBodyGibs(string sidecarSpecies, ModelSpecies species)
    {
        var world = NewWorld();
        var previousSink = EffectEmitter.Sink;
        try
        {
            world.ConfigReader = name => name == "models/player/erebus.iqm_0.txt" ? $"species {sidecarSpecies}\n" : null;
            world.Boot("dm", applyStartupOverrides: cvars =>
            {
                cvars.Set("g_warmup", "0");
                cvars.Set("g_spawnshieldtime", "0");
                cvars.Set("sv_gibhealth", "100");
                cvars.Set("sv_defaultplayerskin", "8"); // falls back to the shipped skin-0 sidecar
            });
            Player player = world.Clients.ClientConnect(isBot: true).Player;
            Assert.Equal(species, player.Species);

            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;
            Combat.Damage(player, null, null, 10f, "shotgun", player.Origin, Vector3.Zero);
            EffectRequest puff = Assert.Single(sink.Log, request => request.EffectName == ModelSpeciesEffects.GibHitName(species));
            Assert.Equal(10, puff.Count);
            Assert.NotNull(EffectNetProtocol.EncodeByName(puff));

            Entity body = Assert.IsType<Entity>(PlayerBody.Copy(player, Vector3.Zero));
            Assert.Equal(species, body.Species);
            player.Species = ModelSpecies.Human;
            body.SetResourceExplicit(ResourceType.Armor, 0f);
            Combat.Damage(body, null, null, body.Health + 200f, "shotgun", body.Origin, Vector3.Zero);
            Assert.Equal(new[] { 10, 200, 200, 101 }, sink.Log
                .Where(request => request.EffectName == ModelSpeciesEffects.GibHitName(species)).Select(request => request.Count));
            string expectedSplash = species == ModelSpecies.Human ? ModelSpeciesEffects.GibSplash : ModelSpeciesEffects.GibSplashName(species);
            EffectRequest splash = Assert.Single(sink.Log, request => request.EffectName == expectedSplash);
            Assert.True(ModelSpeciesEffects.TryGetGibSpecies(splash.EffectName, out ModelSpecies decoded));
            Assert.Equal(species, decoded);
            if (species != ModelSpecies.Human)
                Assert.NotNull(EffectNetProtocol.EncodeByName(splash));
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            world.Shutdown();
        }
    }

    [Theory]
    [InlineData(50f, 0f, 0)]
    [InlineData(51f, 0f, 1)]
    [InlineData(100f, 0f, 1)]
    [InlineData(101f, 0f, 2)]
    [InlineData(101f, 100f, 0)]
    public void LivePlayerGibHitsUseImpactForceAndChunksUseStrictHealthDamageThresholds(float damage, float armor, int chunks)
    {
        var world = NewWorld();
        var previousSink = EffectEmitter.Sink;
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars => cvars.Set("g_spawnshieldtime", "0"));
            var player = world.Clients.ClientConnect(isBot: true).Player;
            player.SetResourceExplicit(ResourceType.Health, 200f);
            player.SetResourceExplicit(ResourceType.Armor, armor);
            var hit = player.Origin + Vector3.UnitZ;
            var force = new Vector3(100f, 0f, 0f);
            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;

            Combat.Damage(player, null, null, damage, "shotgun", hit, force);

            var emitted = sink.Log.Where(r => ModelSpeciesEffects.TryGetGibEvent(r.EffectName, out _, out _)).ToArray();
            Assert.Equal(1 + chunks, emitted.Length);
            Assert.Equal(ModelSpeciesEffects.GibHitName(player.Species), emitted[0].EffectName);
            Assert.Equal((int)damage, emitted[0].Count);
            Assert.Equal(force, emitted[0].Velocity);
            for (int i = 0; i < chunks; i++)
            {
                Assert.Equal(ModelSpeciesEffects.GibChunkName(player.Species), emitted[i + 1].EffectName);
                Assert.Equal(1, emitted[i + 1].Count);
                Assert.Equal(force * (-0.1f * (i + 1)), emitted[i + 1].Velocity);
            }
            Assert.All(emitted, r => Assert.Equal(hit, r.Origin));
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            world.Shutdown();
        }
    }

    [Fact]
    public void CopiedDeadBodyUsesCorpseGibSequenceAndItsOwnSpecies()
    {
        var world = NewWorld();
        var previousSink = EffectEmitter.Sink;
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars =>
            {
                cvars.Set("g_spawnshieldtime", "0");
                cvars.Set("sv_gibhealth", "100");
            });
            var player = world.Clients.ClientConnect(isBot: true).Player;
            player.SetResourceExplicit(ResourceType.Health, 100f);
            player.SetResourceExplicit(ResourceType.Armor, 0f);
            player.Species = ModelSpecies.Alien;
            Combat.Damage(player, null, null, 100f, "shotgun", player.Origin, Vector3.Zero);
            Entity body = Assert.IsType<Entity>(PlayerBody.Copy(player, Vector3.UnitX));
            player.Species = ModelSpecies.Human;
            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;
            var hit = body.Origin + Vector3.UnitZ;
            var force = new Vector3(0f, 100f, 0f);

            Combat.Damage(body, null, null, 210f, "shotgun", hit, force);

            Assert.Equal(new[]
            {
                ModelSpeciesEffects.GibHitName(ModelSpecies.Alien), ModelSpeciesEffects.GibChunkName(ModelSpecies.Alien),
                ModelSpeciesEffects.GibChunkName(ModelSpecies.Alien), ModelSpeciesEffects.GibSplashName(ModelSpecies.Alien),
            }, sink.Log.Select(r => r.EffectName));
            Assert.Equal(new[] {200, 1, 1, 1}, sink.Log.Select(r => r.Count));
            Assert.Equal(force, sink.Log[0].Velocity);
            Assert.Equal(force * -0.1f, sink.Log[1].Velocity);
            Assert.Equal(force * -0.2f, sink.Log[2].Velocity);
            Assert.All(sink.Log.Take(3), r => Assert.Equal(hit, r.Origin));
            Assert.Equal(body.Origin + new Vector3(0f, 0f, 4f), sink.Last.Origin);
            Assert.Equal(body.Velocity, sink.Last.Velocity);
            Assert.Equal(DamageMode.No, body.TakeDamage);
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            world.Shutdown();
        }
    }

    [Fact]
    public void LiveSoundAttackSplitsHitEffectsBetweenTheEarsAndTossesFromTheFarEar()
    {
        var world = NewWorld();
        var previousSink = EffectEmitter.Sink;
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars => cvars.Set("g_spawnshieldtime", "0"));
            var player = world.Clients.ClientConnect(isBot: true).Player;
            player.SetResourceExplicit(ResourceType.Health, 200f);
            player.SetResourceExplicit(ResourceType.Armor, 0f);
            player.Angles = Vector3.Zero;
            var inflictor = new Entity { Origin = player.Origin - new Vector3(0f, 64f, 0f) };
            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;

            Combat.Damage(player, inflictor, null, 101f, DeathTypes.WithHitType("tuba", DeathTypes.Sound),
                player.Origin, Vector3.UnitX * 100f);

            var effects = sink.Log.Where(r => ModelSpeciesEffects.TryGetGibEvent(r.EffectName, out _, out _)).ToArray();
            Assert.Equal(new[] { 1, 200, 1, 1 }, effects.Select(r => r.Count));
            Vector3 center = player.Origin + Vector3.UnitZ * (0.125f * player.ViewOfs.Z + 0.875f * player.Maxs.Z);
            Assert.Equal(center + Vector3.UnitY * 10f, effects[0].Origin);
            Assert.Equal(center - Vector3.UnitY * 10f, effects[1].Origin);
            Assert.Equal(Vector3.UnitY * 100f, effects[0].Velocity);
            Assert.Equal(-effects[0].Velocity, effects[1].Velocity);
            Assert.Equal(effects[0].Origin, effects[2].Origin);
            Assert.Equal(effects[0].Origin, effects[3].Origin);
            Assert.Equal(new Vector3(0f, -10f, 0f), effects[2].Velocity);
            Assert.Equal(new Vector3(0f, -20f, 0f), effects[3].Velocity);
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            world.Shutdown();
        }
    }

    [Theory]
    [InlineData("0", "0", "100", false)]
    [InlineData("0", "1", "100", true)]
    [InlineData("1", "0", "100", true)]
    [InlineData("0", "0", "0", true)]
    [InlineData("0", "1", "0", true)]
    public void ServerSettingsCanRemoveAnOrdinaryCorpseOnTheDeathTick(string gentle, string ekg, string gibHealth, bool gibbed)
    {
        var world = NewWorld();
        var previousSink = EffectEmitter.Sink;
        try
        {
            world.Boot("dm", applyStartupOverrides: cvars =>
            {
                cvars.Set("g_warmup", "0");
                cvars.Set("g_spawnshieldtime", "0");
                cvars.Set("sv_gentle", gentle);
                cvars.Set("ekg", ekg);
                cvars.Set("sv_gibhealth", gibHealth);
            });
            var player = world.Clients.ClientConnect(isBot: true).Player;
            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;
            Combat.Damage(player, null, null, player.Health, "shotgun", player.Origin, Vector3.Zero);
            Assert.True(player.IsCorpse);
            Assert.Equal(gibbed, player.Alpha < 0);
            Assert.Equal(gibbed ? 1 : 0, sink.Log.Count(request =>
                ModelSpeciesEffects.TryGetGibSpecies(request.EffectName, out _)));
            Assert.Equal(gentle == "1" || ekg == "1" ? 3 : 2, sink.Log.Count(request =>
                ModelSpeciesEffects.TryGetGibEvent(request.EffectName, out GibEffectKind kind, out _) && kind == GibEffectKind.Hit));
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            world.Shutdown();
        }
    }

    [Theory]
    [InlineData("cts", false)]
    [InlineData("dm", true)]
    public void CorpseDisappearsInBothModesAndOnlyDeathmatchEmitsTheSplash(string mode, bool expectSplash)
    {
        var world = NewWorld();
        var previousSink = EffectEmitter.Sink;
        try
        {
            world.Boot(mode);
            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;
            var body = new Entity { ClassName = PlayerBody.ClassName, TakeDamage = DamageMode.Aim, Alpha = 1f };
            body.SetResourceExplicit(ResourceType.Health, 100);
            Combat.Damage(body, body, body, 100000, DeathTypes.Kill, body.Origin, Vector3.Zero);
            Assert.True(body.Alpha < 0f);
            Assert.Equal(expectSplash ? 1 : 0, sink.Log.Count(request =>
                ModelSpeciesEffects.TryGetGibSpecies(request.EffectName, out _)));
            Assert.Equal(expectSplash, sink.Log.Any(request =>
                ModelSpeciesEffects.TryGetGibEvent(request.EffectName, out GibEffectKind kind, out _) && kind != GibEffectKind.Splash));
        }
        finally
        {
            EffectEmitter.Sink = previousSink;
            world.Shutdown();
        }
    }

    [Fact]
    public void MapBootAimsAtTheClosedDoorBeforePlacingAndLinkingIt()
    {
        var camera = new EntityDict("info_autoscreenshot", new Vector3(0, 120, 64));
        camera.Fields["target"] = "probe_door";
        var door = new EntityDict("func_door", new Vector3(128, 120, 64));
        door.Fields["model"] = "*1";
        door.Fields["targetname"] = "probe_door";
        door.Fields["angle"] = "90";
        door.Fields["spawnflags"] = "1";

        var world = new GameWorld(new CollisionWorld(), [new EntityDict("worldspawn"), camera, door]);
        world.Services.ModelsImpl.Register("*1", new Vector3(-8, -32, -32), new Vector3(8, 32, 32), isBrushModel: true);
        try
        {
            world.Boot("dm");
            var cameraEdict = Assert.Single(Api.Entities.FindByClass(camera.ClassName));
            var doorEdict = Assert.Single(Api.Entities.FindByClass("door"));
            Assert.Equal(0f, cameraEdict.Angles.Y, 4);
            Assert.True(doorEdict.Origin.Y > door.Origin.Y);
            Assert.Equal(doorEdict.Origin, doorEdict.Pos1);
            Assert.Same(doorEdict, doorEdict.Owner);
        }
        finally { world.Shutdown(); }
    }
}
