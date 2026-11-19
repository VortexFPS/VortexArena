using System.Numerics;
using System.Text;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Engine.Collision;
using VortexArena.Legacy.Protocol;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class WeaponStatsTests : IDisposable
{
    private const string FileCvar = "sv_weaponstats_file";
    private const string Filename = "reports/weaponstats.txt";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "va-weaponstats-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultEmptyAndLateEnableLeaveMapUncollected()
    {
        var world = NewWorld();
        try
        {
            Assert.Equal("", world.Services.Cvars.GetString(FileCvar));
            world.StartWeaponStats(_ => throw new InvalidOperationException("Disabled stats requested a path"));
            Assert.Null(Assert.IsType<DamageSystem>(Combat.System).WeaponStatsSink);

            world.Services.Cvars.Set(FileCvar, Filename);
            var attacker = AddPlayer(world, bot: false, "blaster");
            var victim = AddPlayer(world, bot: true, "shotgun");
            Assert.Equal(5f, Hit(attacker, victim, "blaster", 5f));
        }
        finally
        {
            world.Shutdown();
        }

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CollectsCreditedWeaponsBotFlagsZeroLossHitsAndActualLethalDamage()
    {
        var world = NewWorld();
        try
        {
            world.Services.Cvars.Set("rcon_password", "example-secret");
            StartStats(world);
            var human = AddPlayer(world, bot: false, "oknex");
            var bot = AddPlayer(world, bot: true, "machinegun");
            var otherHuman = AddPlayer(world, bot: false, "shotgun");

            // Neither damage before game_starttime nor damage in the pre-round interval contributes.
            world.GameStartTime = world.Time + 1f;
            Assert.Equal(5f, Hit(human, bot, "blaster", 5f));
            world.GameStartTime = world.Time;
            var rounds = world.EnableRounds(canStart: () => true, endDelay: 0f, countdown: 0f, roundTimeLimit: 0f);
            Assert.Equal(5f, Hit(human, bot, "blaster", 5f));
            rounds.Think();
            Assert.True(rounds.IsRoundStarted);

            SetResources(bot, health: 20f, armor: 10f);
            bot.Flags |= EntFlags.GodMode;
            Assert.Equal(0f, Hit(human, bot, "blaster", 9f));
            bot.Flags &= ~EntFlags.GodMode;
            Assert.Equal(7.25f, Hit(human, bot, "blaster", 7.25f), precision: 4);
            Hit(human, bot, "blaster", 500f);

            // Non-weapon damage is attributed to the credited client's held weapon, including
            // damage credited to the earlier pusher. Overkill weapons use the pinned tail IDs.
            var otherBot = AddPlayer(world, bot: true, "oknex");
            otherHuman.Pusher = otherBot;
            otherHuman.PushLTime = world.Time + 1f;
            Combat.Damage(otherHuman, null, null, 5f, DeathTypes.Fall, Vector3.Zero, Vector3.Zero);

            Hit(human, human, "blaster", 8f);
            Hit(null, human, "blaster", 8f);
            Assert.False(File.Exists(Path.Combine(_root, Filename)));
        }
        finally
        {
            world.Shutdown();
        }

        string report = File.ReadAllText(Path.Combine(_root, Filename));
        Assert.StartsWith("#begin statsfile\n#date ", report);
        Assert.Contains("\n#version ", report);
        Assert.Matches("\\n#config [0-9]+\\n#cvar_purechanges [0-9]+\\n#cvar_purechange ", report);
        Assert.DoesNotContain(FileCvar, report);
        Assert.DoesNotContain("example-secret", report);
        const string purityPrefix = "#cvar_purechange ";
        var purity = report.Split('\n').Where(line => line.StartsWith(purityPrefix, StringComparison.Ordinal))
            .Select(line => line[purityPrefix.Length..]);
        ushort checksum = Crc16.Block(Encoding.UTF8.GetBytes(string.Join('\n', purity) + "\n"));
        Assert.Contains($"\n#config {checksum}\n", report);

        Assert.Equal(new[]
        {
            "Stats test\tdm_boil\t1 0\t3 1\t1 3 30",
            "Stats test\tdm_boil\t28 1\t2 0\t0 1 5",
        }, report.Split('\n').Where(line => line.Contains('\t')));
        Assert.EndsWith("#end\n\n", report);
    }

    [Fact]
    public void ExcludesFriendlyDamageFrozenVictimsAndUnarmedVictims()
    {
        var world = NewWorld(gametype: "tdm");
        try
        {
            StartStats(world);
            var attacker = AddPlayer(world, bot: false, "blaster");
            var victim = AddPlayer(world, bot: false, "shotgun");
            attacker.Team = Teams.Red;
            victim.Team = Teams.Red;
            Hit(attacker, victim, "blaster", 4f);

            victim.Team = Teams.Blue;
            victim.FrozenStat = 1;
            Hit(attacker, victim, "blaster", 5f);

            victim.FrozenStat = 0;
            int heldWeapon = victim.ActiveWeaponId;
            victim.ActiveWeaponId = -1;
            Hit(attacker, victim, "blaster", 6f);

            victim.ActiveWeaponId = heldWeapon;
            Assert.Equal(7f, Hit(attacker, victim, "blaster", 7f));
        }
        finally
        {
            world.Shutdown();
        }

        string report = File.ReadAllText(Path.Combine(_root, Filename));
        Assert.Equal("Stats test\ttdm_boil\t1 0\t2 0\t0 1 7",
            Assert.Single(report.Split('\n'), line => line.Contains('\t')));
    }

    [Fact]
    public void IntermissionAndTeardownAppendOncePerMap()
    {
        const string previous = "prior contents\n";
        File.WriteAllText(Resolve(Filename), previous);

        var world = NewWorld();
        try
        {
            StartStats(world);
            world.EndMatch();
            world.Frame(0.1f);
            Assert.StartsWith(previous + "#begin statsfile\n", File.ReadAllText(Resolve(Filename)));
        }
        finally
        {
            world.Shutdown();
        }

        string finished = File.ReadAllText(Resolve(Filename));
        Assert.Equal(1, finished.Split("#begin statsfile\n").Length - 1);

        // A direct map change or quit before intermission takes the shutdown path.
        var next = NewWorld("dance");
        try
        {
            StartStats(next);
            var attacker = AddPlayer(next, bot: true, "shotgun");
            var victim = AddPlayer(next, bot: true, "blaster");
            Hit(attacker, victim, "shotgun", 4f);
        }
        finally
        {
            next.Shutdown();
        }

        next.Shutdown();
        string appended = File.ReadAllText(Resolve(Filename));
        Assert.StartsWith(finished, appended);
        Assert.Equal(2, appended.Split("#begin statsfile\n").Length - 1);
        Assert.Contains("Stats test\tdm_dance\t2 1\t1 1\t0 1 4\n", appended);
    }

    [Fact]
    public void IntermissionTriesDestinationsInOrderAndWritesLethalPairOnce()
    {
        Directory.CreateDirectory(Resolve("blocked"));
        const string fallback = "reports/with spaces.txt";
        File.WriteAllText(Resolve(fallback), "previous\n");
        var world = NewWorld();
        try
        {
            world.Services.Cvars.Set(FileCvar, $"blocked \"{fallback}\" unused.txt");
            world.StartWeaponStats(Resolve);
            var attacker = AddPlayer(world, bot: true, "oknex");
            var victim = AddPlayer(world, bot: false, "shotgun");
            Assert.Same(Weapons.ByName("oknex"), WeaponOrder.FromXonoticRegistryId(28));
            Assert.Same(Weapons.ByName("shotgun"), WeaponOrder.FromXonoticRegistryId(2));
            SetResources(victim, health: 12f, armor: 3f);
            Hit(attacker, victim, "oknex", 100f);
            world.EndMatch();
            world.Frame(0.1f);
            Assert.Contains("Stats test\tdm_boil\t28 1\t2 0\t1 1 15\n", File.ReadAllText(Resolve(fallback)));
        }
        finally
        {
            world.Shutdown();
        }

        string report = File.ReadAllText(Resolve(fallback));
        Assert.StartsWith("previous\n#begin statsfile\n", report);
        Assert.Equal(1, report.Split("#begin statsfile\n").Length - 1);
        Assert.False(File.Exists(Path.Combine(_root, "unused.txt")));
    }

    [Fact]
    public void StdoutFallbackClosesTheBatchWithoutTouchingLaterDestinations()
    {
        Directory.CreateDirectory(Resolve("blocked"));
        var world = NewWorld();
        var originalOut = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            world.Services.Cvars.Set(FileCvar, "https://example.invalid/stats blocked - unused.txt");
            world.StartWeaponStats(Resolve);
            world.EndMatch();
            world.Frame(0.1f);
        }
        finally
        {
            try { world.Shutdown(); }
            finally { Console.SetOut(originalOut); }
        }

        string report = stdout.ToString();
        Assert.Equal(1, report.Split("#begin statsfile\n").Length - 1);
        Assert.Contains("#end\n\n", report);
        Assert.False(File.Exists(Path.Combine(_root, "unused.txt")));
    }

    [Fact]
    public void PartialStdoutWriteIsTerminal()
    {
        var world = NewWorld();
        var originalOut = Console.Out;
        using var stdout = new PartialStdout();
        Console.SetOut(stdout);
        try
        {
            world.Services.Cvars.Set(FileCvar, $"- {Filename}");
            world.StartWeaponStats(Resolve);
            world.EndMatch();
            world.Frame(0.1f);
        }
        finally
        {
            try { world.Shutdown(); }
            finally { Console.SetOut(originalOut); }
        }

        Assert.Equal(1, stdout.Attempts);
        Assert.False(File.Exists(Path.Combine(_root, Filename)));
    }

    private sealed class PartialStdout : StringWriter
    {
        public int Attempts { get; private set; }

        public override void Write(string? value)
        {
            if (value?.StartsWith("#begin statsfile\n", StringComparison.Ordinal) == true)
            {
                Attempts++;
                base.Write(value[..16]);
                throw new IOException("simulated interruption after a partial write");
            }
            base.Write(value);
        }
    }

    [Fact]
    public void ExhaustedDestinationsAreTerminalEvenIfShutdownHasAUsablePath()
    {
        Directory.CreateDirectory(Resolve("blocked"));
        var world = NewWorld();
        try
        {
            world.Services.Cvars.Set(FileCvar, "blocked ../invalid");
            world.StartWeaponStats(Resolve);
            world.EndMatch();
            world.Frame(0.1f);
            world.Services.Cvars.Set(FileCvar, Filename);
        }
        finally
        {
            world.Shutdown();
        }

        Assert.False(File.Exists(Path.Combine(_root, Filename)));
        Assert.False(File.Exists(Path.Combine(_root, "invalid")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.invalid/stats")]
    [InlineData("../stats")]
    [InlineData("/stats")]
    [InlineData("C:\\stats")]
    [InlineData("\"\\absolute\"")]
    public void FlushReadsCurrentDestinationAndSkipsClearedOrNonlocalTargets(string destination)
    {
        var world = NewWorld();
        int pathRequests = 0;
        try
        {
            world.Services.Cvars.Set(FileCvar, Filename);
            world.StartWeaponStats(path =>
            {
                pathRequests++;
                return Resolve(path);
            });
            world.Services.Cvars.Set(FileCvar, destination);
        }
        finally
        {
            world.Shutdown();
        }

        Assert.Equal(0, pathRequests);
        Assert.False(Directory.Exists(_root));
    }

    private static GameWorld NewWorld(string map = "boil", string gametype = "dm")
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = map };
        world.Boot(gametype);
        world.Services.Cvars.Set("hostname", "Stats test");
        world.GameStartTime = world.Time;
        return world;
    }

    private void StartStats(GameWorld world)
    {
        world.Services.Cvars.Set(FileCvar, Filename);
        world.StartWeaponStats(Resolve);
    }

    private string Resolve(string path)
    {
        string fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static Player AddPlayer(GameWorld world, bool bot, string weapon)
    {
        var player = world.Clients.ClientConnect(isBot: bot).Player;
        player.IsObserver = false;
        player.TakeDamage = DamageMode.Aim;
        player.SpawnShieldExpire = 0f;
        player.ActiveWeaponId = Weapons.ByName(weapon)!.RegistryId;
        SetResources(player, health: 100f, armor: 0f);
        return player;
    }

    private static void SetResources(Player player, float health, float armor)
    {
        player.SetResourceExplicit(ResourceType.Health, health);
        player.SetResourceExplicit(ResourceType.Armor, armor);
    }

    private static float Hit(Player? attacker, Player target, string weapon, float damage)
        => Combat.Damage(target, attacker, attacker, damage, DeathTypes.FromWeapon(weapon), Vector3.Zero, Vector3.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
