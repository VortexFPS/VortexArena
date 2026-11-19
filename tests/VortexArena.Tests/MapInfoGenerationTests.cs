using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Menu;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class MapInfoGenerationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vortex-mapinfo-").FullName;
    private readonly VirtualFileSystem _vfs = new();

    [Fact]
    public void CatalogUsesTheStockWeaponFeatureGateAndModeExemptions()
    {
        WriteMap("unarmed", NativeEntities(3, 300));
        WriteMap("armed", NativeEntities(3, 300));
        Write("maps/unarmed.mapinfo", "gametype dm\ngametype ca\n");
        Write("maps/armed.mapinfo", "gametype dm\ngametype ca\nhas weapons\n");
        var catalog = MountCatalog();
        string[] maps = { "unarmed", "armed" };
        var cvars = new CvarService();
        cvars.Set("g_pickup_items", "1");
        cvars.Set("g_melee_only", "1");
        Assert.Equal(new[] { "armed" }, catalog.ForGametype(maps, "dm", cvars));
        Assert.Equal(maps, catalog.ForGametype(maps, "ca", cvars));
        cvars.Set("g_instagib", "1");
        Assert.Equal(maps, catalog.ForGametype(maps, "dm", cvars));
    }

    [Fact]
    public void MenuMaplistUsesTheConfiguredGametypeUntilAnotherModeIsSelected()
    {
        var cvars = new CvarService();
        Assert.Equal("dm", MapInfoBackend.ConfiguredGametype(cvars));
        cvars.Set("gamecfg", "race");
        cvars.Set("g_race", "1");
        Assert.Equal("rc", MapInfoBackend.ConfiguredGametype(cvars));
        cvars.Set("g_freezetag", "1");
        Assert.Equal("freezetag", MapInfoBackend.ConfiguredGametype(cvars));
        cvars.Set("gamecfg", "unknown");
        cvars.Set("g_dm", "1");
        cvars.Set("g_ctf", "1");
        Assert.Equal("ctf", MapInfoBackend.ConfiguredGametype(cvars));
    }

    [Theory]
    [InlineData(7, 6000, false, false, false, false)]
    [InlineData(8, 3249, true, false, false, false)]
    [InlineData(8, 3250, false, false, false, false)]
    [InlineData(8, 3251, false, true, false, false)]
    [InlineData(8, 4096, false, true, false, false)]
    [InlineData(8, 4097, false, true, true, false)]
    [InlineData(11, 5121, false, true, true, false)]
    [InlineData(12, 5120, false, true, true, false)]
    [InlineData(12, 5121, false, true, true, true)]
    public void NativeMapThresholdsReachCatalogAndDetails(int spawns, int diameter, bool duel, bool team, bool tka, bool kh)
    {
        // The baked model is much larger and worldspawn's origin is far away. Neither defines the QC diameter.
        WriteMap("native", NativeEntities(spawns, diameter)
            + Entity("info_player_intermission") + Entity("weapon_blaster"));
        MapInfoCatalog catalog = MountCatalog();
        MapInfo info = catalog.Get("native");
        Assert.True(info.Generated);
        Assert.False(info.HasMapInfoFile);
        Assert.Equal((float)diameter, info.Diameter);
        Assert.Equal("Native", info.Title);
        Assert.Equal("Mapper", info.Author);
        Assert.Equal("An arena", info.Description);
        Assert.Equal("track", info.CdTrack);
        Assert.Equal(MapFeatures.Weapons, info.Features);

        var cvars = new CvarService();
        cvars.Set("g_duel_not_dm_maps", "1");
        cvars.Set("g_tka_on_ka_maps", "0");
        foreach (var (mode, expected) in new[]
        {
            ("dm", true), ("ka", true), ("lms", true), ("mayhem", true), ("tmayhem", true), ("surv", true),
            ("duel", duel), ("tdm", team), ("ca", team), ("freezetag", team), ("tka", tka), ("kh", kh), ("ctf", false),
        })
        {
            Assert.Equal(expected, info.SupportsInDetails(mode, MapForcedSupport.FromCvars(cvars)));
            Assert.Equal(expected, catalog.ForGametype(new[] { "native" }, mode, cvars, inMenu: true).Count == 1);
        }

        cvars.Set("g_duel_not_dm_maps", "0");
        Assert.True(info.SupportsInDetails("duel", MapForcedSupport.FromCvars(cvars)));
        Assert.Single(catalog.ForGametype(new[] { "native" }, "duel", cvars));
        Assert.Same(info, catalog.Get("NATIVE"));
    }

    [Theory]
    [InlineData("item_flag_team2", "ctf", true)]
    [InlineData("team_CTF_blueflag", "ctf", true)]
    [InlineData("target_assault_roundend", "as", true)]
    [InlineData("nexball_football", "nb", true)]
    [InlineData("ball_basketball", "nb", true)]
    [InlineData("trigger_race_checkpoint", "cts", true)]
    [InlineData("target_startTimer", "cts", false)]
    [InlineData("dom_controlpoint", "dom", false)]
    [InlineData("team_dom_point", "dom", false)]
    [InlineData("invasion_spawnpoint", "inv", false)]
    [InlineData("onslaught_generator", "ons", false)]
    public void NativeObjectivesControlCatalogEligibility(string classname, string mode, bool suppressesGeneric)
    {
        WriteMap("objective", NativeEntities(12, 8000) + Entity(classname));
        MapInfoCatalog catalog = MountCatalog();
        var cvars = new CvarService();
        Assert.Single(catalog.ForGametype(new[] { "objective" }, mode, cvars));
        Assert.Equal(!suppressesGeneric, catalog.Get("objective").Supports("kh"));
        Assert.Equal(!suppressesGeneric, catalog.Get("objective").Supports("dm"));
        Assert.False(catalog.Get("objective").Supports("rc"));
    }

    [Fact]
    public void RacePlacesRetainRaceAndDefragSuppressesGenericModes()
    {
        WriteMap("race", NativeEntities(12, 8000) + Entity("trigger_race_checkpoint", "\"race_place\" \"1\""));
        WriteMap("defrag", NativeEntities(12, 8000) + Entity("target_startTimer") + Entity("target_stopTimer"));
        WriteMap("stop_only", NativeEntities(12, 8000) + Entity("target_stopTimer"));
        MapInfoCatalog catalog = MountCatalog();
        var cvars = new CvarService();
        string[] maps = { "race", "defrag", "stop_only" };

        Assert.Equal(new[] { "race" }, catalog.ForGametype(maps, "rc", cvars));
        Assert.Equal(new[] { "defrag" }, catalog.ForGametype(maps, "cts", cvars));
        Assert.Empty(catalog.ForGametype(maps, "kh", cvars));
        Assert.Equal(new[] { "race", "defrag" }, catalog.ForGametype(maps, null, cvars));
    }

    [Fact]
    public void OriginDiameterUsesAllThreeAxesAndIncludesZero()
    {
        // With all spawns coincident, the single nonzero origin still measures from zero.
        WriteMap("diagonal", NativeEntities(12, 3000)
            + Entity("info_notnull", "\"origin\" \"3000 4000 1500\""));
        MapInfo info = MountCatalog().Get("diagonal");
        Assert.Equal(5220.153f, info.Diameter.GetValueOrDefault(), 2);
        Assert.True(info.Supports("kh"));
    }

    [Fact]
    public void EntityReplacementAndSidecarPrecedenceApplyToMountedMaps()
    {
        using (var pack = ZipFile.Open(Path.Combine(_root, "maps.pk3"), ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "override", "manual", "cached" })
            {
                using Stream entry = pack.CreateEntry($"maps/{name}.bsp").Open();
                entry.Write(Bsp(NativeEntities(12, 6000)));
            }
        }
        string ent = NativeEntities(5, 1000) + Entity("team_CTF_blueflag") + Entity("weapon_nex")
            + Entity("weapon_railgun") + Entity("turret_example") + Entity("vehicle_example")
            + Entity("monster_example") + Entity("target_music");
        foreach (string name in new[] { "override", "manual", "cached" })
            Write($"maps/{name}.ent", ent);
        Write("maps/manual.mapinfo", "title Authored\ngametype duel\nhas new_toys\n");
        Write("maps/autogenerated/manual.mapinfo", "title Cached\ngametype rc\n");
        Write("maps/autogenerated/cached.mapinfo", "title Cached\ngametype rc\n");

        MapInfoCatalog catalog = MountCatalog();
        Assert.Equal("Authored", catalog.Get("manual").Title);
        Assert.Equal(MapFeatures.Weapons, catalog.Get("manual").Features);
        Assert.Equal(new[] { "duel" }, catalog.Get("manual").SupportedGametypes);
        Assert.Equal("Cached", catalog.Get("cached").Title);
        Assert.Equal(new[] { "rc" }, catalog.Get("cached").SupportedGametypes);
        Assert.Equal(new[] { "ctf" }, catalog.Get("override").SupportedGametypes);
        Assert.Equal(MapFeatures.Turrets | MapFeatures.Vehicles | MapFeatures.Monsters, catalog.Get("override").Features);
        Assert.Empty(catalog.Get("override").CdTrack);
        Assert.Contains(BspMapSource.Read(_vfs, "maps/override.bsp").Entities,
            e => e.GetValueOrDefault("classname") == "team_CTF_blueflag");

        Write("maps/override.mapinfo", "gametype kh\n");
        Assert.True(catalog.Get("override").Supports("ctf"));
        _vfs.Rescan();
        Assert.True(new MapInfoCatalog(_vfs).Get("override").Supports("kh"));
    }

    [Fact]
    public void LegacyCtfFlagsRecognizedByTheCatalogSpawnInGame()
    {
        var originalServices = Api.Services;
        var originalSink = GametypeObjectiveSpawns.Sink;
        var world = new GameWorld(new CollisionWorld(), new[]
        {
            new EntityDict("team_CTF_redflag"),
            new EntityDict("team_CTF_blueflag"),
        });
        world.Boot("ctf");
        try
        {
            var ctf = Assert.IsType<Ctf>(world.GameType);
            Assert.Contains(ctf.FlagEntities, flag => flag.Team == Teams.Red);
            Assert.Contains(ctf.FlagEntities, flag => flag.Team == Teams.Blue);
        }
        finally
        {
            world.Shutdown();
            GametypeObjectiveSpawns.Sink = originalSink;
            Api.Services = originalServices;
        }
    }

    [Fact]
    public void RotationUsesGeneratedAndExplicitEligibilityForBothListSources()
    {
        WriteMap("large", NativeEntities(12, 6000));
        WriteMap("small", NativeEntities(12, 1000));
        WriteMap("ctf", NativeEntities(12, 6000) + Entity("item_flag_team2"));
        WriteMap("manual", NativeEntities(2, 1000));
        Write("maps/manual.mapinfo", "gametype kh\n");
        MapInfoCatalog catalog = MountCatalog();
        string[] maps = { "large", "small", "ctf", "manual" };
        var services = new EngineServices(new CollisionWorld());
        var originalServices = Api.Services;
        string? originalListFile = MapListStore.FilePath;
        try
        {
            Api.Services = services;
            MapListStore.FilePath = null;
            Cvars.RegisterDefaults();
            var rotation = new MapRotation
            {
                MapExists = name => _vfs.Exists($"maps/{name}.bsp"),
                MapSupportsGametype = name => catalog.Get(name).SupportsInCatalog("kh", MapForcedSupport.FromCvars(services.Cvars)),
                AllowedMaps = () => catalog.ForGametype(maps, "kh", services.Cvars),
            };

            Cvars.Set("g_maplist_shuffle", "0");
            Assert.Equal(2, rotation.Init("large"));
            Assert.Equal(new[] { "large", "manual" }, rotation.Buffer);

            Cvars.Set("g_maplist", "ctf manual small large");
            Assert.Equal(2, rotation.Init("manual"));
            Assert.Equal(new[] { "manual", "large" }, rotation.Buffer);
        }
        finally
        {
            Api.Services = originalServices;
            MapListStore.FilePath = originalListFile;
        }
    }

    [Theory]
    [InlineData(0, true, false, true)]
    [InlineData(1, false, true, false)]
    [InlineData(2, false, true, false)]
    [InlineData(3, false, true, false)]
    public void ArenaModesUseTheMapPackAndCombineDefi(int q3Compat, bool generatedDm, bool authoredCtf, bool kh)
    {
        using (var pack = ZipFile.Open(Path.Combine(_root, "z_maps.pk3"), ZipArchiveMode.Create))
        {
            using (Stream entry = pack.CreateEntry("maps/arena.bsp").Open())
                entry.Write(Bsp(NativeEntities(12, 6000)));
            using var script = new StreamWriter(pack.CreateEntry("scripts/collection.arena").Open());
            script.Write("""
                {
                    map "unrelated"
                    type "ons"
                    longname "Wrong Map"
                }
                {
                    map "ArEnA"
                    TYPE "ctf tourney"
                    longname "Pack Arena"
                    author "Arena Mapper"
                }
                """);
        }
        Write("scripts/arena.arena", "{\nmap \"another\"\nlongname \"Different Map\"\n}\n");
        Write("scripts/arena.defi", "{\nmap \"arena\"\nstyle \"run\"\nquote \"A course\"\n}\n");
        Assert.True(_vfs.MountGameDir(_root));
        Assert.True(MapInfoCatalog.HasQ3Companion(_vfs, "maps/arena.bsp"));
        var catalog = new MapInfoCatalog(_vfs, q3Compat);
        MapInfo info = catalog.Get("arena");
        Assert.Equal(authoredCtf, info.HasArenaMetadata);
        Assert.Equal(authoredCtf ? "Pack Arena" : "Native", info.Title);
        Assert.Equal(authoredCtf ? "Arena Mapper" : "Mapper", info.Author);
        Assert.Equal(authoredCtf, info.Supports("cts"));
        Assert.Equal(authoredCtf, info.Supports("ctf"));
        Assert.Equal(generatedDm, info.Supports("dm"));
        Assert.Equal(kh, info.Supports("kh"));
        Assert.False(info.Supports("ons"));
        Assert.Equal(q3Compat is 0 or 2, info.Diameter.HasValue);

        if (q3Compat == 1)
        {
            Write("scripts/arena.arena", "{\nmap \"arena\"\nlongname \"Named Arena\"\ntype \"ffa\"\n}\n");
            _vfs.Rescan();
            MapInfo named = new MapInfoCatalog(_vfs).Get("arena");
            Assert.Equal("Named Arena", named.Title);
            Assert.True(named.Supports("dm"));
            Assert.False(named.Supports("ctf"));
        }
    }

    [Fact]
    public void ArenaReadsBraceLinesBeforeStrippingQuotesOrTrailingComments()
    {
        foreach (string name in new[] { "braced", "commented", "course", "upper", "misleading" })
            WriteMap(name, NativeEntities(4, 1000));
        Write("scripts/braced.arena", "{\nmap \"braced\"\nlongname \"A { Hall\"\ntype ctf\n}\n");
        Write("scripts/commented.arena", "{\nmap \"commented\"\ntype ctf // }\n}\n");
        Write("scripts/course.arena", "{\nmap \"course\"\nlongname \"The Course\"\nquote \"Arena quote\"\n}\n");
        Write("scripts/course.defi", "{\nmap course // \"course\"\nstyle \"run\"\n}\n");
        Write("scripts/upper.arena", "{\nMAP \"upper\"\ntype \"ctf\"\n}\n");
        Write("scripts/misleading.arena", "{\nmap \"other\" // see \"misleading\"\ntype \"ctf\"\n}\n");
        MapInfoCatalog catalog = MountCatalog();
        Assert.False(catalog.Get("braced").HasArenaMetadata);
        Assert.False(catalog.Get("commented").Supports("ctf"));
        Assert.False(MapInfoCatalog.HasQ3Companion(_vfs, "maps/upper.bsp"));
        Assert.True(MapInfoCatalog.HasQ3Companion(_vfs, "maps/misleading.bsp"));
        Assert.False(catalog.Get("misleading").HasArenaMetadata);
        MapInfo course = catalog.Get("course");
        Assert.True(course.Supports("cts"));
        Assert.Equal("The Course", course.Title);
        Assert.Equal(string.Empty, course.Description);
    }

    [Fact]
    public void AuthoredMapInfoWinsAndArenaPrecedenceFollowsCompatibilityMode()
    {
        foreach (string name in new[] { "both", "cached", "invalid", "team" })
            WriteMap(name, NativeEntities(4, 1000));
        foreach (string name in new[] { "both", "cached" })
        {
            Write($"scripts/{name}.arena", $"{{\nmap \"{name}\"\ntype \"ctf\"\n}}\n");
            Write($"maps/autogenerated/{name}.mapinfo", "gametype as\n");
        }
        Write("maps/both.mapinfo", "gametype duel\n");
        Write("scripts/invalid.arena", "{\nmap \"another\"\n{\nmap \"invalid\"\ntype ctf\n}\n");
        Write("scripts/team.arena", "{\nmap \"team\"\ntype \"team ffa illegal\" // comment\n}\n");
        MapInfoCatalog direct = MountCatalog();
        Assert.Equal(new[] { "duel" }, direct.Get("both").SupportedGametypes);
        Assert.Equal(new[] { "ctf" }, direct.Get("cached").SupportedGametypes);
        Assert.Equal(new[] { "as" }, new MapInfoCatalog(_vfs, 2).Get("cached").SupportedGametypes);
        Assert.True(direct.Get("invalid").Generated);
        Assert.False(direct.Get("invalid").Supports("ctf"));
        Assert.Equal(new[] { "ca", "dm", "freezetag", "ka", "kh", "lms", "tdm" },
            direct.Get("team").SupportedGametypes.OrderBy(mode => mode));
    }

    [Theory]
    [InlineData("gametype dm timelimit=0", null, null, 0)]
    [InlineData("gametype dm timelimit=0", "timelimit", "22", 22)]
    [InlineData("gametype dm timelimit=14", "timelimit_override", "0", 0)]
    [InlineData("gametype dm timelimit=garbage fraglimit=NaN", null, null, 17)]
    [InlineData("gametype ctf timelimit=8", null, null, 17)]
    [InlineData(null, null, null, 17)]
    public void NativeBootUsesAuthoredLimitsAndRestoresTheSharedStore(
        string? sidecar, string? pinName, string? pin, float expected)
    {
        var shared = new CvarService();
        shared.Set("timelimit", pinName == "timelimit" ? pin! : "17");
        shared.Set("fraglimit", "30");
        var originalServices = Api.Services;
        var world = new GameWorld(new CollisionWorld(), sharedCvars: shared)
        {
            MapName = "_hudsetup",
            ConfigReader = path => path == "maps/_hudsetup.mapinfo" ? sidecar : null,
            MatchLimitOverrides = pinName is null ? null : new Dictionary<string, string> { [pinName] = pin! },
        };
        world.Boot("dm");
        try
        {
            Assert.Equal(expected, world.Services.Cvars.GetFloat("timelimit"));
            Assert.Equal(30f, world.Services.Cvars.GetFloat("fraglimit"));
        }
        finally
        {
            world.Shutdown();
            Api.Services = originalServices;
        }
        Assert.Equal(pinName == "timelimit" ? 22f : 17f, shared.GetFloat("timelimit"));
    }

    [Theory]
    [InlineData("0", null, 5, 10, 8)]
    [InlineData("2", "13", 5, 13, 16)]
    public void RaceUsesMapQualifyingClockThenRestoresTheSelectedMatchLimits(
        string teams, string? pinnedTime, float qualifying, float mainTime, int lapLimit)
    {
        var originalServices = Api.Services;
        var overrides = new Dictionary<string, string> { ["g_race_teams"] = teams };
        if (pinnedTime is not null)
            overrides["timelimit"] = pinnedTime;
        var world = new GameWorld(new CollisionWorld())
        {
            MapName = "leave_em_behind",
            ConfigReader = path => path == "maps/leave_em_behind.mapinfo"
                ? "gametype rc timelimit=10 qualifying_timelimit=5 laplimit=8 teamlaplimit=16"
                : null,
            MatchLimitOverrides = overrides,
        };
        world.Boot("rc");
        try
        {
            var race = Assert.IsType<Race>(world.GameType);
            Assert.Equal(qualifying, world.Services.Cvars.GetFloat("timelimit"));
            Assert.True(race.TransitionQualifyingToRace());
            Assert.Equal(mainTime, world.Services.Cvars.GetFloat("timelimit"));
            Assert.Equal(lapLimit, race.LapLimit);
        }
        finally
        {
            world.Shutdown();
            Api.Services = originalServices;
        }
    }

    [Fact]
    public void TdmUsesSparseAuthoredTeamColorsForRosterAndScoreSlots()
    {
        WriteMap("tdm_fixture", NativeEntities(8, 4000)
            + Entity("tdm_team", "\"cnt\" \"13.0\" \"netname\" \"Tide\" \"spawnflags\" \"2\"")
            + Entity("tdm_team", "\"cnt\" \"9\" \"netname\" \"Bloom\"")
            + Entity("tdm_team", "\"cnt\" \"4\" \"netname\" \"Filtered\" \"gametypefilter\" \"+ctf\"")
            + Entity("tdm_team", "\"cnt\" \"0\" \"netname\" \"Deleted\"")
            + Entity("tdm_team", "\"cnt\" \"3\" \"netname\" \"Invalid color\""));
        Write("maps/tdm_fixture.mapinfo", "gametype tdm teams=3");
        Assert.True(_vfs.MountGameDir(_root));
        var entities = BspMapSource.Read(_vfs, "maps/tdm_fixture.bsp").Entities.Select(fields =>
        {
            var entity = new EntityDict(fields["classname"]);
            foreach (var (key, value) in fields)
                entity.Fields.Add(key, value);
            return entity;
        }).ToArray();
        var originalServices = Api.Services;
        var world = new GameWorld(new CollisionWorld(), entities)
        {
            MapName = "tdm_fixture",
            ConfigReader = path => _vfs.Exists(path) ? _vfs.ReadText(path) : null,
            MatchLimitOverrides = new Dictionary<string, string> { ["g_tdm_teams_override"] = "4" },
        };
        world.Boot("tdm");
        try
        {
            Assert.Equal(new[] { Teams.Blue, Teams.Pink }, world.Teamplay.ActiveTeams);
            Assert.Equal(2, Assert.IsType<Tdm>(world.GameType).TeamCount);
            Assert.Equal(3f, world.Services.Cvars.GetFloat("g_tdm_teams"));
            Assert.Equal(4f, world.Services.Cvars.GetFloat("g_tdm_teams_override"));
            Assert.Equal(new[] { Teams.Pink, Teams.Blue },
                VortexArena.Common.Gameplay.Scoring.GameScores.TeamScores.Keys.Order());
            var teamEntities = world.Services.Entities.FindByClass("tdm_team").ToArray();
            Assert.Contains(teamEntities, team => team.NetName == "Tide" && team.Team == Teams.Blue && team.Cnt == 13
                && team.SpawnFlags == 2);
            Assert.Contains(teamEntities, team => team.NetName == "Bloom" && team.Team == Teams.Pink);
            Assert.DoesNotContain(teamEntities, team => team.NetName is "Filtered" or "Deleted");

            Player a = world.Clients.ClientConnect(isBot: true, netName: "first").Player;
            Player b = world.Clients.ClientConnect(isBot: true, netName: "second").Player;
            Assert.Equal(new[] { Teams.Pink, Teams.Blue }, new[] { (int)a.Team, (int)b.Team }.Order());
            Assert.Equal((int)a.Team * 17, a.ClientColors);
            Assert.Equal((int)b.Team * 17, b.ClientColors);
        }
        finally
        {
            world.Shutdown();
            Api.Services = originalServices;
        }

        var dm = new GameWorld(new CollisionWorld(), entities);
        dm.Boot("dm");
        try
        {
            Assert.Empty(dm.Services.Entities.FindByClass("tdm_team"));
        }
        finally
        {
            dm.Shutdown();
            Api.Services = originalServices;
        }
    }

    [Theory]
    [InlineData("gametype tdm teams=3", null, null, 3)]
    [InlineData("type tdm 50 20 3 0", null, null, 3)]
    [InlineData("gametype tdm teams=3", null, "4", 4)]
    [InlineData("gametype tdm teams=4", "3", "1", 3)]
    [InlineData("gametype tdm teams=bogus\ngametype ctf teams=4", null, null, 2)]
    public void TdmAppliesAuthoredCountAndOverrideWithMapLocalLifetime(
        string sidecar, string? pinnedTeams, string? pinnedOverride, int expected)
    {
        var shared = new CvarService();
        shared.Set("g_tdm_teams", pinnedTeams ?? "2");
        shared.Set("g_tdm_teams_override", pinnedOverride ?? "0");
        var overrides = new Dictionary<string, string>();
        if (pinnedTeams is not null) overrides["g_tdm_teams"] = pinnedTeams;
        if (pinnedOverride is not null) overrides["g_tdm_teams_override"] = pinnedOverride;
        var originalServices = Api.Services;
        var world = new GameWorld(new CollisionWorld(), sharedCvars: shared)
        {
            MapName = "tdm_count",
            ConfigReader = path => path == "maps/tdm_count.mapinfo" ? sidecar : null,
            MatchLimitOverrides = overrides,
        };
        world.Boot("tdm");
        try
        {
            Assert.Equal(Teams.Active(expected), world.Teamplay.ActiveTeams);
            Assert.Equal(Teams.Active(expected).Order(), world.Services.Entities.FindByClass("tdm_team")
                .Select(team => (int)team.Team).Order());
        }
        finally
        {
            world.Shutdown();
            Api.Services = originalServices;
        }
        Assert.Equal(pinnedTeams ?? "2", shared.GetString("g_tdm_teams"));

        // A second boot on the same store uses its own settings and never inherits the first map's team set.
        var next = new GameWorld(new CollisionWorld(), sharedCvars: shared) { MapName = "next_map" };
        next.Boot("tdm");
        try
        {
            int nextCount = pinnedOverride == "4" ? 4 : pinnedTeams == "3" ? 3 : 2;
            Assert.Equal(Teams.Active(nextCount), next.Teamplay.ActiveTeams);
        }
        finally
        {
            next.Shutdown();
            Api.Services = originalServices;
        }
    }

    [Theory]
    [InlineData("mayhem", false, 2000, 1000, 15)]
    [InlineData("mayhem", true, 2000, 1000, 15)]
    [InlineData("tmayhem", false, 3000, 1500, 20)]
    [InlineData("tmayhem", true, 3000, 1500, 20)]
    public void MayhemMenuPointSettingsReachTheScoreRuleAcrossMaps(
        string mode, bool sharedStore, int sliderMax, float defaultPoints, float defaultTime)
    {
        PointLimitSettings settings = Assert.IsType<PointLimitSettings>(PointLimitSettings.For(mode));
        Assert.Equal(200, PointLimitSettings.SliderMin);
        Assert.Equal(100, settings.SliderStep);
        Assert.Equal(sliderMax, settings.SliderMax);
        Assert.Equal($"g_{mode}_point_limit", settings.PointCvar);

        var menu = new CvarService();
        menu.Set("fraglimit", "30");
        menu.Set("timelimit", "7");
        menu.Set(settings.PointCvar, "400");
        if (settings.TeamsCvar is { } teamsOverride)
            menu.Set(teamsOverride, "4");

        CheckMatch("first", $"gametype {mode} timelimit=20 pointlimit=600 teams=3", 400, 20, 4);
        if (sharedStore)
        {
            Assert.Equal(30f, menu.GetFloat("fraglimit"));
            Assert.Equal(7f, menu.GetFloat("timelimit"));
        }
        menu.Set(settings.PointCvar, "-1");
        if (settings.TeamsCvar is { } teamSetting)
            menu.Set(teamSetting, "0");
        CheckMatch("second", $"gametype {mode} pointlimit=600 teams=3", 600, defaultTime, 3);
        CheckMatch("global", $"gametype {mode} pointlimit=600", 500, defaultTime, 2);
        CheckMatch("third", mode == "tmayhem" ? "type tmayhem 1500 20 4 0" : null, defaultPoints, defaultTime, 4);
        menu.Set(settings.PointCvar, "0");
        CheckMatch("unlimited", $"gametype {mode} pointlimit=600", 0, defaultTime, 2);

        void CheckMatch(string name, string? mapInfo, float points, float minutes, int teamCount)
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fraglimit_override"] = name switch { "first" => "5", "global" => "500", _ => "-1" },
            };
            settings.CopyHostOverrides(menu, overrides);
            var prior = Api.Services;
            var world = new GameWorld(new CollisionWorld(), sharedCvars: sharedStore ? menu : null)
            {
                MapName = name,
                ConfigReader = path => path == $"maps/{name}.mapinfo" ? mapInfo : null,
                MatchLimitOverrides = overrides,
            };
            world.Boot(mode);
            try
            {
                Assert.Equal(points, world.Services.Cvars.GetFloat("fraglimit"));
                Assert.Equal(minutes, world.Services.Cvars.GetFloat("timelimit"));
                if (mode == "tmayhem")
                    Assert.Equal(teamCount, world.Teamplay.ActiveTeams.Count);
                Assert.NotNull(world.GameType?.MenuSummary);

                Player player = world.Clients.ClientConnect(isBot: false, netName: "Scorer").Player;
                SetScore(points > 0 ? (int)points - 1 : 4000);
                world.Frame(0.021f);
                Assert.False(HasEnded());
                if (points > 0)
                {
                    SetScore((int)points);
                    world.Frame(0.021f);
                    Assert.True(HasEnded());
                }

                bool HasEnded() => world.GameType is Mayhem { MatchEnded: true } or TeamMayhem { MatchEnded: true };

                void SetScore(int score)
                {
                    if (mode == "tmayhem")
                        VortexArena.Common.Gameplay.Scoring.GameScores.SetTeamScore(Teams.Red, score);
                    else
                        player.ScoreFrags = score;
                }
            }
            finally
            {
                world.Shutdown();
                Api.Services = prior;
            }
        }
    }

    [Fact]
    public void KeyHuntMenuBindingsCarryTheSelectedSettingsToASeparateHost()
    {
        var settings = Assert.IsType<PointLimitSettings>(PointLimitSettings.For("kh"));
        Assert.Equal((200, 1500, 50), (PointLimitSettings.SliderMin, settings.SliderMax, settings.SliderStep));
        var menu = new CvarService();
        menu.Set("g_keyhunt_point_limit", "450");
        menu.Set("g_keyhunt_point_leadlimit", "-1");
        menu.Set("g_keyhunt_teams_override", "4");
        var overrides = new Dictionary<string, string>();
        settings.CopyHostOverrides(menu, overrides);
        var prior = Api.Services;
        var world = new GameWorld(new CollisionWorld()) { MatchLimitOverrides = overrides };
        try
        {
            world.Boot("kh");
            var kh = Assert.IsType<KeyHunt>(world.GameType);
            Assert.Equal(450, kh.PointLimit);
            Assert.Equal("Gather all the keys to win the round", kh.MenuSummary);
            Assert.Equal(4, world.Teamplay.ActiveTeams.Count);
        }
        finally
        {
            world.Shutdown();
            Api.Services = prior;
        }
    }

    [Theory]
    [InlineData("gametype kh teams=4 pointlimit=650 timelimit=18 leadlimit=75", "-1", "-1", "0", 650, 18, 75, 4)]
    [InlineData("type kh 700 17 4 85", "-1", "-1", "0", 700, 17, 85, 4)]
    [InlineData("gametype kh", "-1", "-1", "0", 1000, 20, 0, 3)]
    [InlineData("gametype kh teams=4 pointlimit=650", "400", "0", "2", 400, 20, 0, 2)]
    [InlineData("gametype kh pointlimit=650", "0", "0", "0", 0, 20, 0, 3)]
    public void KeyHuntBootAppliesMapDefaultsAndOperatorChoices(string sidecar, string pointOverride,
        string leadOverride, string teamsOverride, int score, int minutes, int lead, int teamCount)
    {
        var shared = new CvarService();
        shared.Set("g_keyhunt_teams", "0");
        shared.Set("fraglimit", "314");
        shared.Set("timelimit", "37");
        shared.Set("leadlimit", "8");
        shared.Set("g_keyhunt_teams_override", teamsOverride);
        shared.Set("g_keyhunt_point_limit", pointOverride);
        shared.Set("g_keyhunt_point_leadlimit", leadOverride);
        shared.Set("fraglimit_override", pointOverride == "-1" ? "-1" : "5");
        var prior = Api.Services;
        var world = new GameWorld(new CollisionWorld(), sharedCvars: shared)
        {
            MapName = "kh_settings",
            ConfigReader = path => path == "maps/kh_settings.mapinfo" ? sidecar : null,
        };
        try
        {
            world.Boot("kh");
            var kh = Assert.IsType<KeyHunt>(world.GameType);
            Assert.Equal(teamCount, world.Teamplay.ActiveTeams.Count);
            Assert.Equal(minutes, shared.GetFloat("timelimit"));
            Assert.Equal(score, shared.GetFloat("fraglimit"));
            Assert.Equal(lead, shared.GetFloat("leadlimit"));
            Assert.Equal(score, kh.PointLimit);
            Assert.Equal(lead, kh.LeadLimit);
            shared.Set("leadlimit", "0");
            VortexArena.Common.Gameplay.Scoring.GameScores.SetTeamScore(Teams.Red, score > 0 ? score - 1 : 2200);
            kh.UpdateLeaderAndCheckLimit();
            Assert.False(kh.MatchEnded);
            if (score > 0)
            {
                VortexArena.Common.Gameplay.Scoring.GameScores.SetTeamScore(Teams.Red, score);
                kh.UpdateLeaderAndCheckLimit();
                Assert.True(kh.MatchEnded);
            }
        }
        finally
        {
            world.Shutdown();
            Api.Services = prior;
        }
        Assert.Equal("0", shared.GetString("g_keyhunt_teams"));
        Assert.Equal("314", shared.GetString("fraglimit"));
        Assert.Equal("37", shared.GetString("timelimit"));
        Assert.Equal("8", shared.GetString("leadlimit"));
    }

    private MapInfoCatalog MountCatalog()
    {
        Assert.True(_vfs.MountGameDir(_root));
        return new MapInfoCatalog(_vfs);
    }

    private static string NativeEntities(int spawns, int diameter)
    {
        var text = new StringBuilder(Entity("worldspawn",
            "\"message\" \"^2Native by Mapper\" \"_description\" \"An arena\" \"music\" \"track.ogg\" \"origin\" \"99999 0 0\""));
        string[] spawnClasses = { "info_player_team1", "info_player_team2", "info_player_start", "info_player_deathmatch" };
        for (int i = 0; i < spawns; i++)
            text.Append(Entity(spawnClasses[i % spawnClasses.Length], $"\"origin\" \"{diameter} 0 0\""));
        return text.ToString();
    }

    private static string Entity(string classname, string fields = "") => $"{{ \"classname\" \"{classname}\" {fields} }}\n";

    private static byte[] Bsp(string entities)
    {
        const int headerSize = 144;
        byte[] lump = Encoding.ASCII.GetBytes(entities + "\0");
        byte[] data = new byte[headerSize + lump.Length + 40];
        "IBSP"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 46);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), lump.Length);
        lump.CopyTo(data.AsSpan(headerSize));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + 7 * 8), headerSize + lump.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12 + 7 * 8), 40);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(headerSize + lump.Length + 12), 25000f);
        return data;
    }

    private void WriteMap(string name, string entities)
    {
        Directory.CreateDirectory(Path.Combine(_root, "maps"));
        File.WriteAllBytes(Path.Combine(_root, "maps", name + ".bsp"), Bsp(entities));
    }

    private void Write(string path, string text)
    {
        string fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
    }

    public void Dispose()
    {
        _vfs.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
