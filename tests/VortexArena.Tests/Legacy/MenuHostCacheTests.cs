using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Engine.Simulation;
using VortexArena.Legacy.Menu;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The server list (Menu/MenuHostCache.cs, port of netconn.c ServerList_*): filtering, ordering, what a
/// reply has to say to be believed, and the bounds on everything a server or a master sends.
/// </summary>
public class MenuHostCacheTests
{
    private sealed class Queries : IMenuServerQueries
    {
        public List<string> Masters { get; } = new();
        public List<(string Address, string Challenge)> Servers { get; } = new();
        public HashSet<string> Unsendable { get; } = new();

        public bool QueryMaster(string master, string gameName, int protocol)
        {
            if (Unsendable.Contains(master)) return false;
            Masters.Add($"{master} {gameName} {protocol}");
            return true;
        }

        public void QueryServer(string address, string challenge) => Servers.Add((address, challenge));
    }

    private static (MenuHostCache Cache, CvarService Cvars, Queries Queries) New()
    {
        CvarService cvars = new();
        Queries queries = new();
        double now = 100;
        MenuHostCache cache = new(cvars) { Queries = queries, Clock = () => now };
        return (cache, cvars, queries);
    }

    /// <summary>A believable status reply from <paramref name="address"/>, arriving <paramref name="ping"/> ms after its query.</summary>
    internal static bool Add(MenuHostCache cache, string address, string name, int ping, int clients = 0, int bots = 0, int maxClients = 16,
        string map = "stormkeep", string mod = "data", string qcStatus = "dm:git:P0:S16:F0:MXonotic", string players = "", int? gameVersion = null)
    {
        double sent = cache.Clock();
        string info = $"\\gamename\\Xonotic\\modname\\{mod}\\sv_maxclients\\{maxClients}\\clients\\{clients}\\bots\\{bots}\\mapname\\{map}" +
                      $"\\hostname\\{name}\\protocol\\3\\qcstatus\\{qcStatus}" + (gameVersion is { } v ? $"\\gameversion\\{v}" : "") + $"\\challenge\\{cache.BuildQuery(sent)}";
        return cache.ServerReply(address, info, players, sent + ping / 1000.0);
    }

    private static string[] Names(MenuHostCache cache) => cache.View.Select(e => e.Info.Name).ToArray();

    [Fact]
    public void AReply_FillsEveryField_AndThePingIsTheRoundTrip()
    {
        (MenuHostCache cache, _, _) = New();
        Assert.True(Add(cache, "192.0.2.10:26000", "^1Red^7 server", 37, clients: 5, bots: 2, maxClients: 12, map: "boil", mod: "data",
            qcStatus: "ctf:git:P3:S12:F3", players: "10 20 \"alice\"\n5 30 \"bob\"\n"));
        HostCacheEntry entry = Assert.Single(cache.View);
        HostCacheInfo info = entry.Info;
        Assert.Equal("192.0.2.10:26000", info.CName);
        Assert.Equal(37, info.Ping);
        Assert.Equal("^1Red^7 server", info.Name);
        Assert.Equal("Xonotic", info.Game);
        Assert.Equal("data", info.Mod);
        Assert.Equal("boil", info.Map);
        Assert.Equal("ctf:git:P3:S12:F3", info.QcStatus);
        Assert.Equal("10 20 \"alice\"\n5 30 \"bob\"\n", info.Players);
        Assert.Equal((12, 5, 2, 3, 7, 3), (info.MaxPlayers, info.NumPlayers, info.NumBots, info.NumHumans, info.FreeSlots, info.Protocol));
        Assert.Equal(1, cache.ServerReplyCount);
        Assert.StartsWith("^7   37^7 ^3  5^7/ 12 ^1Red^7 server", entry.Line1);

        // A second reply averages towards the new measurement: (37 + 21) * 0.5 + 0.5, truncated.
        Assert.True(Add(cache, "192.0.2.10:26000", "renamed", 21));
        Assert.Equal(29, cache.View[0].Info.Ping);
        Assert.Equal("renamed", cache.View[0].Info.Name);
        Assert.Equal(1, cache.CacheCount);
        Assert.Equal(1, cache.ServerReplyCount);
    }

    [Fact]
    public void AReply_IsRefused_WithoutTheChallengeThisClientMade()
    {
        (MenuHostCache cache, CvarService cvars, _) = New();
        const string info = "\\gamename\\Xonotic\\hostname\\x\\sv_maxclients\\8\\clients\\1";
        Assert.False(cache.ServerReply("192.0.2.1:26000", info, "", 100.02));                                        // no challenge at all
        Assert.False(cache.ServerReply("192.0.2.1:26000", info + "\\challenge\\AAAAAAAAAAAAAAAAAAAAAA186a0", "", 100.02));   // a made-up one
        Assert.False(cache.ServerReply("192.0.2.1:26000", info + "\\challenge\\" + cache.BuildQuery(100)[..22], "", 100.02)); // hash but no time
        // A stale but well-formed challenge from before the key changed (a new refresh makes a new key).
        string old = cache.BuildQuery(100);
        cache.Refresh(true, 100);
        Assert.False(cache.ServerReply("192.0.2.1:26000", info + "\\challenge\\" + old, "", 100.02));
        Assert.Equal(4, cache.RepliesRefused);
        Assert.Equal(0, cache.CacheCount);

        // Believable, but not an address, or a ping no server has: dropped without counting as forged.
        Assert.False(cache.ServerReply("example.org:26000", info + "\\challenge\\" + cache.BuildQuery(100), "", 100.02));
        Assert.False(cache.ServerReply("192.0.2.1:26000", info + "\\challenge\\" + cache.BuildQuery(100), "", 100));      // 0 ms
        Assert.False(cache.ServerReply("192.0.2.1:26000", info + "\\challenge\\" + cache.BuildQuery(100), "", 100.5));    // above net_slist_maxping
        cvars.Set("net_slist_maxping", "600");
        Assert.True(cache.ServerReply("192.0.2.1:26000", info + "\\challenge\\" + cache.BuildQuery(100), "", 100.5));
        Assert.Equal(500, cache.View[0].Info.Ping);
    }

    [Fact]
    public void WhatAServerSends_IsCutAndCleaned()
    {
        (MenuHostCache cache, _, _) = New();
        string longName = new('n', 500);
        Assert.True(Add(cache, "192.0.2.1:26000", longName + "\r\u0001;quit", 10, map: new string('m', 80), players: new string('p', 5000) + "\n\u0007x"));
        HostCacheInfo info = cache.View[0].Info;
        Assert.Equal(HostCacheInfo.NameSize - 1, info.Name.Length);
        Assert.Equal(HostCacheInfo.MapSize - 1, info.Map.Length);
        Assert.Equal(HostCacheInfo.PlayersSize - 1, info.Players.Length);
        Assert.DoesNotContain(info.Name, c => c < ' ');

        // Control characters never survive, wherever they are; a line feed stays only in the player list.
        Assert.True(Add(cache, "192.0.2.2:26000", "a\nb\u001bc\u007fd", 10, players: "1 2 \"x\"\n3 4 \"y\u0001\"\n"));
        HostCacheInfo second = cache.Cache[1].Info;
        Assert.Equal("abcd", second.Name);
        Assert.Equal("1 2 \"x\"\n3 4 \"y\"\n", second.Players);
        // The address is this client's spelling of where the datagram came from, never the packet's text.
        Assert.Equal("192.0.2.2:26000", second.CName);
        Assert.Equal("7", MenuHostCache.InfoValue("\\a\\1\\clients\\7\\b\\2", "clients", 16));
        Assert.Equal("", MenuHostCache.InfoValue("\\a\\1", "clients", 16));
        Assert.Equal("12345", MenuHostCache.InfoValue("\\k\\1234567890", "k", 6));
    }

    [Fact]
    public void TheCache_IsBounded()
    {
        (MenuHostCache cache, _, _) = New();
        for (int i = 0; i < MenuHostCache.MaxServers + 50; i++)
            Add(cache, $"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}:26000", "s" + i, 10 + i % 300);
        Assert.Equal(MenuHostCache.MaxServers, cache.CacheCount);
        Assert.Equal(MenuHostCache.MaxServers, cache.ViewCount);

        // A master's list is cut at the same bound, and anything that is not an address is skipped.
        (MenuHostCache second, CvarService cvars, _) = New();
        cvars.Set("sv_master1", "master.example:27950");
        second.Refresh(true, 100);
        List<string> listed = new() { "not an address", "192.0.2.5:0", "192.0.2.5:26000", "192.0.2.5:26000", "[::1]:26000" };
        for (int i = 0; i < 3000; i++) listed.Add($"10.1.{i >> 8}.{i & 255}:26000");
        second.MasterReply("master.example:27950", listed, endOfTransmission: true);
        Assert.Equal(MenuHostCache.MaxServers, second.CacheCount);
        Assert.Equal("192.0.2.5:26000", second.Cache[0].Info.CName);
        Assert.Equal(0, second.ViewCount);                    // nothing is shown before it has answered
    }

    [Fact]
    public void Sorting_ByEachKindOfField_IsStable()
    {
        (MenuHostCache cache, _, _) = New();
        Add(cache, "192.0.2.1:26000", "Charlie", 50, clients: 4, map: "b");
        Add(cache, "192.0.2.2:26000", "alpha", 20, clients: 9, map: "c");
        Add(cache, "192.0.2.3:26000", "Bravo", 50, clients: 1, map: "a");

        cache.SortField = HostCacheField.Ping;
        cache.SortFlags = 0;
        cache.RebuildView();
        Assert.Equal(new[] { "alpha", "Charlie", "Bravo" }, Names(cache));   // equal pings keep cache order

        cache.SortFlags = MenuHostCache.SortDescending;
        cache.RebuildView();
        Assert.Equal(new[] { "Charlie", "Bravo", "alpha" }, Names(cache));   // ... in either direction

        cache.SortField = HostCacheField.Name;                                // text: case-insensitive
        cache.SortFlags = 0;
        cache.RebuildView();
        Assert.Equal(new[] { "Charlie", "Bravo", "alpha" }, Names(cache));   // "for text if A < B": ascending is Z to A
        cache.SortFlags = MenuHostCache.SortDescending;
        cache.RebuildView();
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, Names(cache));

        cache.SortField = HostCacheField.NumPlayers;
        cache.RebuildView();
        Assert.Equal(new[] { "alpha", "Charlie", "Bravo" }, Names(cache));

        cache.SortField = (HostCacheField)99;                                 // "Bad serverlist_sortbyfield": cache order
        cache.RebuildView();
        Assert.Equal(new[] { "Charlie", "alpha", "Bravo" }, Names(cache));
    }

    [Fact]
    public void Favorites_And_Categories_SortFirst()
    {
        (MenuHostCache cache, CvarService cvars, _) = New();
        cvars.Set("net_slist_favorites", "192.0.2.3:26000 0123456789012345678901234567890123456789abcd not.an.ip");
        cache.Category = entry => entry.Info.Name.StartsWith("ctf", StringComparison.Ordinal) ? 1 : 2;
        Add(cache, "192.0.2.1:26000", "dm one", 10);
        Add(cache, "192.0.2.2:26000", "ctf two", 30);
        Add(cache, "192.0.2.3:26000", "dm three", 60);

        cache.SortField = HostCacheField.Ping;
        cache.SortFlags = MenuHostCache.SortFavorites;
        cache.RebuildView();
        Assert.Equal(new[] { "dm three", "dm one", "ctf two" }, Names(cache));
        Assert.True(cache.View[0].Info.IsFavorite);

        cache.SortFlags = MenuHostCache.SortCategories | MenuHostCache.SortFavorites;
        cache.RebuildView();
        Assert.Equal(new[] { "ctf two", "dm three", "dm one" }, Names(cache));
        Assert.Equal(1, cache.View[0].Info.Category);
        Assert.Null(cache.CallbackEntry);                     // "cleared on every way out"
    }

    [Fact]
    public void Masks_AndTogether_OrTogether_AndIgnoreColourCodes()
    {
        (MenuHostCache cache, _, _) = New();
        cache.SortField = HostCacheField.Ping;
        Add(cache, "192.0.2.1:26000", "^1Vot^2able^7 Insta", 40, clients: 0, map: "boil");
        Add(cache, "192.0.2.2:26000", "Plain DM", 80, clients: 6, map: "stormkeep");
        Add(cache, "192.0.2.3:26000", "Laggy CTF", 250, clients: 3, map: "implosion");

        HostCacheMask and = cache.Mask(0)!;
        and.Active = true;
        and.Info.Ping = 200;
        and.Tests[(int)HostCacheField.Ping] = HostCacheOp.LessEqual;
        cache.RebuildView();
        Assert.Equal(new[] { "^1Vot^2able^7 Insta", "Plain DM" }, Names(cache));

        HostCacheMask second = cache.Mask(1)!;                 // a second AND mask: both must pass
        second.Active = true;
        second.Info.NumHumans = 1;
        second.Tests[(int)HostCacheField.NumHumans] = HostCacheOp.GreaterEqual;
        cache.RebuildView();
        Assert.Equal(new[] { "Plain DM" }, Names(cache));

        cache.ResetMasks();
        HostCacheMask or1 = cache.Mask(512)!, or2 = cache.Mask(513)!;
        or1.Active = or2.Active = true;
        or1.Info.Name = "VOTABLE";                             // the mask is lower-cased; the name loses its colours
        or1.Tests[(int)HostCacheField.Name] = HostCacheOp.Contains;
        or2.Info.Map = "implo";
        or2.Tests[(int)HostCacheField.Map] = HostCacheOp.StartsWith;
        cache.RebuildView();
        Assert.Equal(new[] { "^1Vot^2able^7 Insta", "Laggy CTF" }, Names(cache));

        Assert.Null(cache.Mask(16));
        Assert.Null(cache.Mask(511));
        Assert.Null(cache.Mask(512 + 16));
        Assert.True(MenuHostCache.CompareInt(5, HostCacheOp.Contains, 5));       // string operators on a number: ">="
        Assert.False(MenuHostCache.CompareInt(4, HostCacheOp.StartsWith, 5));
        Assert.False(MenuHostCache.CompareString("anything", HostCacheOp.Contains, ""));   // an empty mask contains nothing ...
        Assert.True(MenuHostCache.CompareString("anything", HostCacheOp.NotContain, ""));  // ... and excludes nothing
        Assert.Equal("ab^c", MenuHostCache.Decolorize("^1a^xF00b^^c"));
    }

    [Fact]
    public void IncompatibleGameVersions_AreNotShown()
    {
        (MenuHostCache cache, CvarService cvars, _) = New();
        cvars.Set("gameversion", "806");
        cvars.Set("gameversion_min", "805");
        cvars.Set("gameversion_max", "899");
        cache.SortField = HostCacheField.Ping;
        Add(cache, "192.0.2.1:26000", "same", 10, gameVersion: 806);
        Add(cache, "192.0.2.2:26000", "older but in range", 20, gameVersion: 805);
        Add(cache, "192.0.2.3:26000", "too old", 30, gameVersion: 700);
        Add(cache, "192.0.2.4:26000", "says nothing", 40);
        Assert.Equal(new[] { "same", "older but in range" }, Names(cache));
        Assert.Equal(4, cache.CacheCount);
    }

    [Fact]
    public void ARefresh_AsksTheMasters_ThenEachServerThreeTimes_ThenTimesOutTheSilent()
    {
        (MenuHostCache cache, CvarService cvars, Queries queries) = New();
        double now = 100;
        cache.Clock = () => now;
        cvars.Set("sv_master1", "one.example:27950");
        cvars.Set("sv_master2", "two.example");
        cvars.Set("sv_masterextra1", "dead.example");
        cvars.Set("net_slist_favorites", "192.0.2.9:26995");
        queries.Unsendable.Add("dead.example");

        cache.Refresh(true);
        Assert.Equal(new[] { "one.example:27950 Xonotic 3", "two.example Xonotic 3" }, queries.Masters);
        Assert.Equal(2, cache.MasterQueryCount);
        Assert.Equal("192.0.2.9:26995", Assert.Single(cache.Cache).Info.CName);   // the favourite is asked without a master
        Assert.True(cache.Querying);

        cache.MasterReply("stranger.example", new[] { "192.0.2.66:26000" }, true);   // a master nobody asked
        Assert.Equal(1, cache.CacheCount);
        cache.MasterReply("one.example:27950", new[] { "192.0.2.1:26000", "192.0.2.2:26000" }, endOfTransmission: true);
        cache.Frame(now, 0.02);
        Assert.Empty(queries.Servers);                           // the second master has not finished its list
        cache.MasterUnreachable("two.example");                  // ... and turns out to have no address
        Assert.Equal(1, cache.MasterQueryCount);

        // net_slist_queriesperframe is 2 by default.
        cache.Frame(now += 0.02, 0.02);
        Assert.Equal(new[] { "192.0.2.9:26995", "192.0.2.1:26000" }, queries.Servers.Select(q => q.Address));
        cache.Frame(now += 0.02, 0.02);
        Assert.Equal(3, queries.Servers.Count);
        string challenge = queries.Servers[1].Challenge;
        Assert.True(cache.ServerReply("192.0.2.1:26000", "\\hostname\\answers\\sv_maxclients\\8\\clients\\1\\challenge\\" + challenge, "", now + 0.01));
        Assert.Equal(new[] { "answers" }, Names(cache));

        // Two more passes, a second apart (net_slist_interval), then the timeout pass ends the refresh.
        for (int i = 0; i < 400 && cache.Querying; i++) cache.Frame(now += 0.05, 0.05);
        Assert.False(cache.Querying);
        Assert.Equal(9, queries.Servers.Count);                  // three servers, three tries each
        Assert.Equal(new[] { "answers" }, Names(cache));

        // The next refresh keeps the cache; a server that stays silent this time leaves the view.
        cache.Refresh(false);
        cache.MasterReply("one.example:27950", Array.Empty<string>(), true);
        for (int i = 0; i < 400 && cache.Querying; i++) cache.Frame(now += 0.05, 0.05);
        Assert.Equal(3, cache.CacheCount);
        Assert.Equal(0, cache.ViewCount);
        Assert.Equal(0, cache.ServerReplyCount);
    }

    [Fact]
    public void TheListIsNotRebuilt_UnderTheCategoryCallback()
    {
        (MenuHostCache cache, _, _) = New();
        int rebuiltInside = -1;
        cache.Category = entry =>
        {
            cache.RebuildView();                                  // "must not rebuild or free the list under itself"
            cache.Refresh(true);
            rebuiltInside = cache.CacheCount;
            return 0;
        };
        Add(cache, "192.0.2.1:26000", "one", 10);
        Assert.Equal(1, rebuiltInside);
        Assert.Equal(1, cache.ViewCount);
    }

    [Theory]
    [InlineData("192.0.2.1", 26000, "192.0.2.1:26000")]
    [InlineData("192.0.2.1:27015", 26000, "192.0.2.1:27015")]
    [InlineData("192.0.2.256:1", 26000, null)]
    [InlineData("example.org", 26000, null)]
    [InlineData("192.0.2.1:99999", 26000, null)]
    [InlineData("192.0.2.1:", 26000, null)]
    [InlineData("1.2.3", 26000, null)]
    public void Addresses_AreLiteralIpv4Only(string text, int port, string? expected) =>
        Assert.Equal(expected, MenuHostCache.NormalizeAddress(text, port));
}
