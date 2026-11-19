using VortexArena.Common.Menu;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class MapTempSettingsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeMapTemporariesFollowTheActiveModeAndRestoreAcrossRotation(bool shared)
    {
        var menu = new CvarService();
        Seed(menu);
        var reads = new List<string>();
        var files = new Dictionary<string, string>
        {
            ["maps/first.mapinfo"] = """
                settemp_for_type all timelimit 8
                gametype dm timelimit=7 pointlimit=20
                settemp_for_type all fraglimit 33
                settemp_for_type all g_pickup_shells 3
                settemp_for_type ffa "g_pickup_shells" "4" "description"
                settemp_for_type ctf g_pickup_cells 123
                settemp_for_type all g_pickup_shells 5; echo denied
                settemp_for_type all g_pickup_private 93
                settemp_for_type all g_pickup_readonly 92
                settemp_for_type all g_pickup_denied 91
                settemp_for_type all g_pickup_unknown 90
                settemp_for_type all g_mapinfo_settemp_acl +*
                settemp_for_type all rcon_password rejected
                settemp_for_type all _test_privileged rejected
                settemp_for_type all g_ban_fixture rejected
                settemp_for_type all sv_weaponstats_file rejected
                settemp_for_type all #include "maps/first-settings.cfg"
                settemp_for_type all #include "../outside.cfg"
                settemp_for_type all g_start_ammo_shells 88
                settemp_for_type all g_balance_fixture_weaponreplace ""
                clientsettemp_for_type all g_pickup_cells 98
                """,
            ["maps/first-settings.cfg"] = """
                // Included settings use assignment syntax only.
                seta g_pickup_nails "17" "description"
                set g_pickup_cells 18 // comment
                #include "maps/nested.cfg"
                exec maps/not-executed.cfg
                """,
            ["maps/second.mapinfo"] = "gametype dm\nsettemp_for_type ctf g_pickup_shells 9",
        };
        string? Read(string path)
        {
            reads.Add(path);
            return files.GetValueOrDefault(path);
        }

        // The normal shared parser remains inert during listing and never opens the include.
        var catalogInfo = new MapInfoBackend(Read, _ => false).Get("first");
        Assert.True(catalogInfo.Supports("dm"));
        Assert.DoesNotContain("maps/first-settings.cfg", reads);
        Assert.Equal("6", menu.GetString("g_pickup_shells"));

        var originalServices = Api.Services;
        string? originalListFile = MapListStore.FilePath;
        MapListStore.FilePath = null;
        var first = Start("first");
        string nextMap;
        string originalNails;
        try
        {
            CvarService cvars = first.Services.CvarsImpl;
            Assert.Equal("4", cvars.GetString("g_pickup_shells"));
            Assert.Equal("17", cvars.GetString("g_pickup_nails"));
            originalNails = cvars.GetPersistentString("g_pickup_nails");
            Assert.NotEqual("17", originalNails);
            Assert.Equal("18", cvars.GetString("g_pickup_cells"));
            Assert.Equal("", cvars.GetString("g_balance_fixture_weaponreplace"));
            Assert.Equal("7", cvars.GetString("timelimit"));
            Assert.Equal("42", cvars.GetString("fraglimit"));
            Assert.Equal("2", cvars.GetString("g_start_ammo_shells"));
            Assert.Equal("6", cvars.GetPersistentString("g_pickup_shells"));
            Assert.False(cvars.WasSetByUser("g_pickup_nails"));
            Assert.False(cvars.IsArchived("g_pickup_nails"));
            foreach (string denied in new[] { "g_pickup_private", "g_pickup_readonly", "g_pickup_denied",
                         "rcon_password", "_test_privileged", "g_ban_fixture", "sv_weaponstats_file" })
                Assert.Equal("original", cvars.GetString(denied));
            Assert.False(cvars.Has("g_pickup_unknown"));
            Assert.Equal("-g_pickup_denied +* -g_pickup_cells", cvars.GetString("g_mapinfo_settemp_acl"));
            Assert.DoesNotContain("maps/nested.cfg", reads);
            Assert.DoesNotContain("../outside.cfg", reads);

            first.Rotation.AllowedMaps = () => new[] { "second" };
            first.Rotation.Init("first");
            nextMap = first.Rotation.GetNextMap();
            Assert.Equal("second", nextMap);
        }
        finally
        {
            first.Shutdown();
            MapListStore.FilePath = originalListFile;
            Api.Services = originalServices;
        }
        Assert.Equal("6", first.Services.Cvars.GetString("g_pickup_shells"));
        Assert.Equal(originalNails, first.Services.Cvars.GetString("g_pickup_nails"));
        Assert.Equal("electro", first.Services.Cvars.GetString("g_balance_fixture_weaponreplace"));

        var second = Start(nextMap);
        try
        {
            Assert.Equal("6", second.Services.Cvars.GetString("g_pickup_shells"));
            Assert.Equal(originalNails, second.Services.Cvars.GetString("g_pickup_nails"));
            Assert.False(second.HasMapTempSetting("g_pickup_shells"));
        }
        finally
        {
            second.Shutdown();
            Api.Services = originalServices;
        }

        GameWorld Start(string map)
        {
            var world = new GameWorld(new CollisionWorld(), sharedCvars: shared ? menu : null)
            {
                MapName = map,
                ConfigReader = Read,
                MatchLimitOverrides = new Dictionary<string, string>
                {
                    ["fraglimit_override"] = "42",
                    ["g_start_ammo_shells"] = "2",
                },
            };
            world.Boot("dm", applyStartupOverrides: shared ? null : Seed);
            return world;
        }
    }

    [Fact]
    public void MountedClientVisualDefaultsFollowModeAclAndExplicitPreferences()
    {
        var reads = new List<string>();
        var files = new Dictionary<string, string>
        {
            ["maps/first.mapinfo"] = """
                clientsettemp_for_type all r_sky 0
                clientsettemp_for_type ffa r_bloom 0
                clientsettemp_for_type ctf r_sky 1
                clientsettemp_for_type unknown r_sky 1
                clientsettemp_for_type dm #include "maps/client.cfg"
                clientsettemp_for_type dm #include "../outside.cfg"
                clientsettemp_for_type dm #include "maps/missing.cfg"
                clientsettemp_for_type dm r_sky 1; echo invalid
                settemp_for_type all r_sky 1
                """,
            ["maps/client.cfg"] = """
                seta r_drawfog "0"
                set r_bloom 1
                #include "maps/nested.cfg"
                exec maps/not-executed.cfg
                set r_drawfog NaN
                set cl_name denied
                """,
        };
        string? Read(string path)
        {
            reads.Add(path);
            return files.GetValueOrDefault(path);
        }

        var catalog = new MapInfoBackend(Read, _ => false);
        MapInfo first = catalog.Get("first");
        Assert.Equal(new[] { "maps/first.mapinfo" }, reads);
        var dm = MapClientVisualSettings.Resolve(first, "dm", "+*", Read);
        Assert.Equal(new MapClientVisualSettings(true, false, false), dm);
        Assert.DoesNotContain("../outside.cfg", reads);
        Assert.DoesNotContain("maps/nested.cfg", reads);
        Assert.Contains("maps/missing.cfg", reads);
        Assert.Equal(new MapClientVisualSettings(null, true, null),
            MapClientVisualSettings.Resolve(first, "ctf", "+*", Read));
        Assert.Equal(new MapClientVisualSettings(null, false, false),
            MapClientVisualSettings.Resolve(first, "dm", "-r_bloom +*", Read));

        var cvars = new CvarService();
        cvars.Register("r_sky", "1", CvarFlags.Save);
        cvars.LockDefaults();
        Assert.False(EffectiveSky(dm));
        Assert.Equal("1", cvars.GetPersistentString("r_sky"));
        var next = MapClientVisualSettings.Resolve(catalog.Get("second"), "dm", "+*", Read);
        Assert.True(EffectiveSky(next));
        cvars.Set("r_sky", "1");
        Assert.True(EffectiveSky(dm));
        cvars.Set("r_sky", "0");
        Assert.False(EffectiveSky(next));
        Assert.Equal("0", cvars.GetPersistentString("r_sky"));

        bool EffectiveSky(MapClientVisualSettings settings) => MapClientVisualSettings.EffectiveValue(
            settings.Sky, cvars.GetFloat("r_sky") != 0f, cvars.WasSetByUser("r_sky"));
    }

    [Fact]
    public void ServerSuggestionsUseTheActiveModeAndOperatorAcl()
    {
        var cvars = new CvarService();
        cvars.Set("g_mapinfo_settemp_acl", "-r_drawfog +*");
        var world = new GameWorld(new CollisionWorld(), new[] { new EntityDict("worldspawn") }, sharedCvars: cvars)
        {
            MapName = "fixture",
            ConfigReader = path => path switch
            {
                "maps/fixture.mapinfo" => """
                    clientsettemp_for_type all r_sky 0
                    clientsettemp_for_type ffa #include "maps/dm-visuals.cfg"
                    clientsettemp_for_type ctf r_sky 1
                    clientsettemp_for_type all r_drawfog 0
                    clientsettemp_for_type all cl_accept_server_map_visuals 1
                    """,
                "maps/dm-visuals.cfg" => "set r_bloom 1",
                _ => null,
            },
        };
        var originalServices = Api.Services;
        try
        {
            world.Boot("dm");
            Assert.Equal(new MapClientVisualSettings(true, false, null), world.MapVisualSuggestions);
            Assert.False(cvars.Has(MapClientVisualSettings.ServerSuggestionsCvar));
            Assert.False(cvars.Has("r_bloom"));
        }
        finally
        {
            world.Shutdown();
            Api.Services = originalServices;
        }
    }

    [Fact]
    public void ServerSuggestionsAreOptionalPerVisualAndRespectLocalAcl()
    {
        var mounted = new MapClientVisualSettings(true, false, true);
        var server = new MapClientVisualSettings(false, true, null);
        Assert.Equal(mounted, mounted.WithServerSuggestions(server, accept: false));
        Assert.Equal(new MapClientVisualSettings(false, true, true), mounted.WithServerSuggestions(server, accept: true));
        Assert.Equal(mounted, mounted.WithServerSuggestions(default, accept: true));
        Assert.Equal(mounted with { Sky = true }, mounted.WithServerSuggestions(server.Filter("-r_bloom +*"), accept: true));
    }

    [Theory]
    [InlineData(null, "0.1 0.2 0.3 0.4", "0.1 0.2 0.3 0.4")]
    [InlineData("", "0.1 0.2 0.3 0.4", "0.1 0.2 0.3 0.4")]
    [InlineData("fog none", "0.1 0.2 0.3 0.4", "none")]
    [InlineData("fog 0.2 0.6 0.5 0.4 0.5 32 256 0 150", "0.1", "0.2 0.6 0.5 0.4 0.5 32 256 0 150")]
    [InlineData("fog 0.2\nfog NaN\nfog 0.9; echo ignored", "0.1", "0.2")]
    [InlineData("fog missing", "0.1", "0.1")]
    [InlineData("fog 0.2 0 0 0 1 500 10", "0.1; echo ignored", "none")]
    public void NativeWorldResolvesEffectiveMapFogWithoutMutatingPreferences(string? mapInfo, string worldspawnFog, string expected)
    {
        var cvars = new CvarService();
        cvars.Set("sv_fog", "operator");
        cvars.Set("r_drawfog", "0");
        var worldspawn = new EntityDict("worldspawn");
        worldspawn.Fields["fog"] = worldspawnFog;
        var world = new GameWorld(new CollisionWorld(), new[] { worldspawn }, sharedCvars: cvars)
        {
            MapName = "maps/fixture.bsp",
            ConfigReader = path => path == "maps/fixture.mapinfo" ? mapInfo : null,
        };
        var originalServices = Api.Services;
        try
        {
            world.Boot("dm");
            Assert.Equal(expected, world.MapFog);
            Assert.True(MapFogSettings.TryParse(world.MapFog, out _));
            Assert.Equal("operator", cvars.GetString("sv_fog"));
            Assert.Equal("0", cvars.GetString("r_drawfog"));
        }
        finally
        {
            world.Shutdown();
            Api.Services = originalServices;
        }
    }

    private static void Seed(CvarService cvars)
    {
        cvars.Register("g_pickup_shells", "2", CvarFlags.Save);
        cvars.Register("g_pickup_nails", "2");
        cvars.Register("g_pickup_cells", "2");
        cvars.Register("g_start_ammo_shells", "2");
        cvars.Register("g_balance_fixture_weaponreplace", "electro");
        cvars.Register("g_pickup_private", "original", CvarFlags.Private);
        cvars.Register("g_pickup_readonly", "original", CvarFlags.ReadOnly);
        foreach (string name in new[] { "g_pickup_denied", "rcon_password", "_test_privileged", "g_ban_fixture", "sv_weaponstats_file" })
            cvars.Set(name, "original");
        cvars.Set("g_mapinfo_settemp_acl", "-g_pickup_denied +* -g_pickup_cells");
        cvars.LockDefaults();
        cvars.Set("g_pickup_shells", "6");
    }
}
