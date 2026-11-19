using System.Numerics;
using VortexArena.Common;
using VortexArena.Common.Config;
using VortexArena.Engine.Collision;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class LocalDiagnosticTests
{
    [Fact]
    public void WeaponDumpUsesDeclaredPropertiesAndRegistryOrder()
    {
        using var fixture = new Fixture();
        var cvars = fixture.World.Services.CvarsImpl;
        cvars.Set("g_balance_blaster_primary_damage", "123.456789");
        cvars.Set("g_balance_blaster_weaponreplace", "shotgun mortar");
        cvars.Set("g_balance_blaster_invented", "25");
        cvars.Set("g_balance_nexball_primary_speed", "10");
        cvars.Set("g_balance_turret_ewheel_damage", "10");

        var minelayer = VortexArena.Common.Gameplay.Weapons.ByName("minelayer")!;
        var originalFlags = minelayer.SpawnFlags;
        string output;
        string dump;
        try
        {
            minelayer.SpawnFlags &= ~VortexArena.Common.Gameplay.WeaponFlags.MutatorBlocked;
            fixture.Run("dumpweapons");
            Assert.Contains("// {{{ #5: Mine Layer\n", File.ReadAllText(fixture.PathFor("weapons_dump.cfg")));
            minelayer.SpawnFlags |= VortexArena.Common.Gameplay.WeaponFlags.MutatorBlocked;
            output = fixture.Run("dumpweapons");
            dump = File.ReadAllText(fixture.PathFor("weapons_dump.cfg"));
        }
        finally
        {
            minelayer.SpawnFlags = originalFlags;
        }
        Assert.StartsWith("// ************************************************************************************ //\n", dump);
        Assert.Contains("// ************************************************************************************ //\n\n// {{{ #1: Blaster\n", dump);
        Assert.Contains("set g_balance_blaster_primary_damage 123.457\n", dump);
        Assert.Contains("set g_balance_blaster_weaponreplace \"shotgun mortar\"\nset g_balance_blaster_weaponstart ", dump);
        Assert.Contains("// {{{ #5: Mine Layer (MUTATOR WEAPON)\n", dump);
        Assert.Contains("// {{{ #20: Ball Stealer (MUTATOR WEAPON)\n// }}}\n// {{{ #21: Overkill Heavy MachineGun", dump);
        Assert.DoesNotContain("invented", dump);
        Assert.DoesNotContain("g_balance_nexball_", dump);
        Assert.DoesNotContain("g_balance_turret_", dump);
        Assert.DoesNotContain("set g_balance_", output);
        Assert.Contains("#25: Ball Stealer: 0 settings...", output);
        Assert.Contains("Totals: 25 weapons, 851 settings", output);
        Assert.DoesNotContain("unexpected.cfg", fixture.Run("dumpweapons \"\" unexpected.cfg"));
        Assert.Contains("data/weapons_dump.cfg", output);

        string withEcho = fixture.Run("sv_cmd dumpweapons -");
        Assert.Contains("set g_balance_blaster_weaponreplace \"shotgun mortar\"", withEcho);
        string named = fixture.Run("sv_cmd dumpweapons \"reports/custom balance.cfg\"");
        Assert.Equal(File.ReadAllText(fixture.PathFor("weapons_dump.cfg")),
            File.ReadAllText(fixture.PathFor("reports/custom balance.cfg")));
        Assert.Contains("set g_balance_blaster_primary_damage 123.457", named);

        var reloaded = new VortexArena.Engine.Simulation.CvarService();
        new ConfigInterpreter(reloaded, _ => dump).ExecuteFile("weapons_dump.cfg");
        Assert.Equal("shotgun mortar", reloaded.GetString("g_balance_blaster_weaponreplace"));
    }

    [Fact]
    public void TurretDumpOverwritesAndSkipsGlobalOrUndeclaredProperties()
    {
        using var fixture = new Fixture();
        var cvars = fixture.World.Services.CvarsImpl;
        cvars.Set("g_turrets_unit_ewheel_health", "540");
        cvars.Set("g_turrets_unit_ewheel_ammo_max", "2000");
        cvars.Set("g_turrets_unit_walker_rocket_speed", "0.0000123456789");
        cvars.Set("g_turrets_unit_plasma_dual_health", "125");
        cvars.Set("g_turrets_unit_plasma_health", "250");
        cvars.Set("g_turrets_nofire", "1");
        cvars.Set("g_turrets_unit_ewheel_unregistered", "99");
        string path = fixture.PathFor("turrets_dump.cfg");
        File.WriteAllText(path, new string('z', 100000));

        string output = fixture.Run("dumpturrets -");
        string dump = File.ReadAllText(path);
        var exportedNames = dump.Split('\n').Where(line => line.StartsWith("alias ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]).ToArray();
        Assert.Equal(exportedNames.Length, exportedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.StartsWith("// {{{ #1: eWheel Turret\nset g_turrets_unit_ewheel_aim_firetolerance_dist ", dump);
        Assert.Contains("set g_turrets_unit_ewheel_ammo_max 2000\nset g_turrets_unit_ewheel_ammo_recharge ", dump);
        Assert.Contains("set g_turrets_unit_ewheel_health 540\n", dump);
        Assert.Contains("// {{{ #10: Dual Plasma Cannon\n", dump);
        Assert.Contains("set g_turrets_unit_plasma_dual_health 125\n", dump);
        Assert.Contains("set g_turrets_unit_plasma_health 250\n", dump);
        Assert.Contains("set g_turrets_unit_walker_rocket_speed 1.23457e-05\n", dump);
        Assert.DoesNotContain("zz", dump);
        Assert.DoesNotContain("unregistered", dump);
        Assert.DoesNotContain("g_turrets_nofire", dump);
        Assert.Contains("set g_turrets_unit_ewheel_health 540", output);
        Assert.DoesNotContain("set g_turrets_unit_", fixture.Run("dumpturrets"));
        Assert.Contains("Could not open file '../elsewhere.cfg'", fixture.Run("dumpturrets ../elsewhere.cfg"));
        Assert.Contains("Could not open file '/absolute.cfg'", fixture.Run("dumpturrets /absolute.cfg"));
        Assert.Contains("Could not open file 'reports/.hidden.cfg'", fixture.Run("dumpturrets reports/.hidden.cfg"));
    }

    [Fact]
    public void LocalCvarChangesUseStringDefaultsAndHidePrivateAndTemporaryValues()
    {
        using var fixture = new Fixture();
        var cvars = fixture.World.Services.CvarsImpl;
        cvars.Register("zz_sample", "original");
        cvars.Register("aa_sample", "1");
        cvars.Register("mm_unchanged", "same");
        cvars.Register("_diagnostic_temp", "0");
        cvars.Register("rcon_password", "");
        cvars.Set("zz_sample", "some words");
        cvars.Set("aa_sample", "1.0");
        cvars.Set("_diagnostic_temp", "1");
        cvars.Set("rcon_password", "sensitive");
        new ConfigInterpreter(cvars, _ => null).ExecuteLine("setp operator_secret sensitive");
        cvars.Set("operator_secret", "changed");
        string output = fixture.Run("cvar_localchanges");
        Assert.Contains("aa_sample \"1.0\" // \"1\"\n", output);
        Assert.Contains("zz_sample \"some words\" // \"original\"\n", output);
        Assert.True(output.IndexOf("aa_sample", StringComparison.Ordinal) < output.IndexOf("zz_sample", StringComparison.Ordinal));
        Assert.DoesNotContain("mm_unchanged", output);
        Assert.DoesNotContain("_diagnostic_temp", output);
        Assert.DoesNotContain("rcon_password", output);
        Assert.DoesNotContain("operator_secret", output);
        Assert.Contains(BuildInfo.Version, fixture.Run("version"));
        Assert.Contains("invalid buffer 512", fixture.Run("bufstr_get 512 1"));
    }

    [Fact]
    public void EntityFindRespectsClassOriginStrictRadiusAndConsoleSource()
    {
        using var fixture = new Fixture();
        var player = fixture.World.Clients.ClientConnect(netName: "Player").Player;
        var services = fixture.World.Services.EntityTable;
        var first = services.Spawn();
        first.ClassName = "local_probe";
        first.Origin = new Vector3(104, 204, 304);
        var second = services.Spawn();
        second.ClassName = "local_probe";
        second.Origin = first.Origin + new Vector3(2, 0, 0);
        var freed = services.Spawn();
        freed.ClassName = first.ClassName;
        freed.Origin = first.Origin;
        services.Remove(freed);

        Assert.Equal($"{first.Index} (local_probe)\n{second.Index} (local_probe)\nFound 2 entities\n", fixture.Run("findent local_probe"));
        Assert.Equal("", fixture.Run("findent LOCAL_PROBE"));
        Assert.Equal($"{first.Index} (local_probe)\nFound 1 entities\n", fixture.Run("findat \"104 204 304\""));
        Assert.Equal(fixture.Run("findat \"104 204 304\""), fixture.Run("sv_cmd findat \"104 204 304\" 2"));
        Assert.Contains("Found 2 entities", fixture.Run("findat \"'104 204 304'\" 2.01"));
        Assert.Contains($"{player.Index} ({player.ClassName})", fixture.Run($"findent {player.ClassName}"));
        Assert.All(new[] { "findent local_probe", "findat 0", "cvar_localchanges", "version", "bufstr_get 1 0",
            "dumpweapons", "dumpturrets", "sv_cmd dumpweapons" }, command =>
            Assert.Contains("Unknown command", fixture.World.Commands.Execute(command, isServerConsole: false, caller: player).Output));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root.FullName));
        Assert.All(new[] { "debugdraw_sv", "debugtrace", "find" }, name => Assert.False(fixture.World.Commands.Has(name)));
    }

    [Fact]
    public void CommandDumpUsesServerScopesAndTheLiveRegisteredNames()
    {
        using var fixture = new Fixture();
        var commands = fixture.World.Commands;
        commands.Register("zz_scope_probe", "registered test command", _ => true);
        fixture.World.Services.Cvars.Set("cvar_scope_probe", "do not export");
        var player = fixture.World.Clients.ClientConnect(netName: "Player").Player;
        string path = fixture.PathFor("sv_cmd_dump.txt");
        File.WriteAllText(path, new string('x', 50000));

        Assert.Contains("Completed dump of aliases", fixture.Run("sv_cmd dumpcommands ignored.txt"));
        string dump = File.ReadAllText(path);
        Assert.False(File.Exists(fixture.PathFor("ignored.txt")));
        string[] sections = dump.Split("\n\n", StringSplitOptions.None);
        Assert.Collection(sections,
            server => {
                Assert.StartsWith("dump of server console commands:\n", server);
                Assert.Contains("alias zz_scope_probe       \"qc_cmd_sv     zz_scope_probe       ${* ?}\" // registered test command", server);
                Assert.DoesNotContain("alias dumpcommands", server);
            },
            client => {
                Assert.StartsWith("dump of networked client only commands:\n", client);
                Assert.Contains("alias join                 \"qc_cmd_cmd    join", client);
                Assert.DoesNotContain("alias vote ", client);
            },
            common => {
                Assert.StartsWith("dump of common commands:\n", common);
                Assert.Contains("alias vote                 \"qc_cmd_svcmd  vote", common);
            },
            bans => {
                Assert.StartsWith("dump of ban commands:\n", bans);
                Assert.Contains("alias ban                  \"qc_cmd_sv     ban", bans);
                Assert.True(bans.IndexOf("alias unvoteban", StringComparison.Ordinal) <
                    bans.IndexOf("alias unplayban", StringComparison.Ordinal));
            },
            generic => {
                Assert.StartsWith("dump of generic commands:\n", generic);
                Assert.Contains("alias dumpcommands         \"qc_cmd_svmenu dumpcommands         ${* ?}\"", generic);
            });
        Assert.DoesNotContain("cvar_scope_probe", dump);
        Assert.DoesNotContain(new string('x', 100), dump);

        commands.Unregister("zz_scope_probe");
        Assert.Contains("Unknown command", commands.Execute("dumpcommands", isServerConsole: false, caller: player).Output);
        Assert.Contains("Unknown command", commands.Execute("sv_cmd dumpcommands", isServerConsole: false, caller: player).Output);
        Assert.Equal(dump, File.ReadAllText(path));
        Assert.Contains("Completed dump of aliases", fixture.Run("dumpcommands"));
        Assert.DoesNotContain("zz_scope_probe", File.ReadAllText(path));

        commands.OpenCommandExport = _ => throw new UnauthorizedAccessException();
        Assert.Contains("Could not dump to file!", fixture.Run("dumpcommands"));
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory();
        public GameWorld World { get; } = new(new CollisionWorld());

        public Fixture()
        {
            World.Boot("dm");
            World.Commands.OpenCommandExport = name =>
            {
                string path = PathFor(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return File.Create(path);
            };
        }

        public string PathFor(string name) => Path.Combine(Root.FullName, name);
        public string Run(string line) => World.Commands.Execute(line).Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        public void Dispose()
        {
            World.Shutdown();
            Root.Delete(recursive: true);
        }
    }
}
