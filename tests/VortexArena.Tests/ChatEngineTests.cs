using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the T46 server chat engine — the port of <c>server/chat.qc</c> <c>Say()</c> + <c>formatmessage()</c>
/// and the ignore-list CRUD from <c>server/command/cmd.qc</c>. Covers the 4 g_chat_* allowed gates, per-say-type
/// flood throttling with persistent timestamps, recipient routing (public / team / spectator / private), mutual
/// ignore blocking keyed by PersistentId, muted = fake-accept, and the 1/0/-1 return code.
/// </summary>
[Collection("GlobalState")]
public class ChatEngineTests
{
    private static GameWorld NewWorld(string gametype = "dm")
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot(gametype);
        // Restore the shipped chat defaults (shared global cvar state across the test collection).
        foreach (string c in new[] { "g_chat_allowed", "g_chat_private_allowed", "g_chat_spectator_allowed", "g_chat_team_allowed" })
            Cvars.Set(c, "1");
        Cvars.Set("g_chat_nospectators", "0");
        Cvars.Set("g_chat_flood_notify_flooder", "1");
        Cvars.Set("g_chat_teamcolors", "0");
        Cvars.Set("g_chat_show_playerid", "0");
        Cvars.Set("g_chat_tellprivacy", "1");
        return world;
    }

    /// <summary>Connect a real (human) client and seat it as a live player on the given team with a stable id.</summary>
    private static Player AddPlayer(GameWorld world, string name, int team = Teams.None, string uid = "")
    {
        Player p = world.Clients.ClientConnect(isBot: false, netName: name).Player;
        p.IsObserver = false;
        p.FragsStatus = Player.FragsPlayer;
        p.DeadState = DeadFlag.No;
        p.Team = team;
        p.PersistentId = uid == "" ? name : uid; // give every test player a stable ignore key
        return p;
    }

    /// <summary>Connect a real client and leave it as an observer/spectator.</summary>
    private static Player AddSpectator(GameWorld world, string name, string uid = "")
    {
        Player p = world.Clients.ClientConnect(isBot: false, netName: name).Player;
        p.IsObserver = true;
        p.FragsStatus = Player.FragsSpectator;
        p.PersistentId = uid == "" ? name : uid;
        return p;
    }

    /// <summary>Wire the per-player delivery sink to a capture map and return it (recipient → lines received).</summary>
    private static Dictionary<Player, List<string>> CaptureDelivery(GameWorld world)
    {
        var inbox = new Dictionary<Player, List<string>>();
        world.Commands.ChatToPlayer = (p, text) =>
        {
            if (!inbox.TryGetValue(p, out var list)) inbox[p] = list = new List<string>();
            list.Add(text);
        };
        return inbox;
    }

    private static int Received(Dictionary<Player, List<string>> inbox, Player p)
        => inbox.TryGetValue(p, out var list) ? list.Count : 0;

    // ============================================================================== allowed gates

    [Fact]
    public void Gate_ChatDisabled_RejectsRealClient()
    {
        var world = NewWorld();
        Cvars.Set("g_chat_allowed", "0");
        Player a = AddPlayer(world, "a");
        int ret = world.Commands.Chat.Say(a, 0, null, "hello", false);
        Assert.Equal(0, ret); // 0 = rejected
    }

    [Fact]
    public void Gate_PrivateDisabled_RejectsTell()
    {
        var world = NewWorld();
        Cvars.Set("g_chat_private_allowed", "0");
        Player a = AddPlayer(world, "a");
        Player b = AddPlayer(world, "b");
        int ret = world.Commands.Chat.Say(a, 0, b, "psst", false);
        Assert.Equal(0, ret);
    }

    [Fact]
    public void Gate_SpectatorDisabled_RejectsObserverSay()
    {
        var world = NewWorld();
        Cvars.Set("g_chat_spectator_allowed", "0");
        Player s = AddSpectator(world, "spec");
        int ret = world.Commands.Chat.Say(s, 0, null, "hi", false);
        Assert.Equal(0, ret);
    }

    [Fact]
    public void Gate_TeamDisabled_RejectsTeamSay()
    {
        var world = NewWorld("tdm");
        Cvars.Set("g_chat_team_allowed", "0");
        Player a = AddPlayer(world, "a", Teams.Red);
        int ret = world.Commands.Chat.Say(a, 1, null, "go go go", false);
        Assert.Equal(0, ret);
    }

    [Fact]
    public void Gate_AllAllowed_PublicSayAccepted()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        int ret = world.Commands.Chat.Say(a, 0, null, "hello world", false);
        Assert.Equal(1, ret); // 1 = accepted
    }

    // ============================================================================== routing

    [Fact]
    public void Routing_PublicSay_ReachesEveryOtherRealClient()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        Player b = AddPlayer(world, "b");
        Player c = AddPlayer(world, "c");
        var inbox = CaptureDelivery(world);

        world.Commands.Chat.Say(a, 0, null, "hi all", false);

        Assert.True(Received(inbox, a) >= 1); // the sender sees their own message
        Assert.Equal(1, Received(inbox, b));
        Assert.Equal(1, Received(inbox, c));
    }

    [Fact]
    public void Routing_TeamSay_ReachesOnlyTeammates()
    {
        var world = NewWorld("tdm");
        Player red1 = AddPlayer(world, "red1", Teams.Red);
        Player red2 = AddPlayer(world, "red2", Teams.Red);
        Player blue1 = AddPlayer(world, "blue1", Teams.Blue);
        var inbox = CaptureDelivery(world);

        int ret = world.Commands.Chat.Say(red1, 1, null, "incoming", false);

        Assert.Equal(1, ret);
        Assert.Equal(1, Received(inbox, red2));  // teammate gets it
        Assert.Equal(0, Received(inbox, blue1)); // the enemy does not
        Assert.True(Received(inbox, red1) >= 1); // the sender echoes
    }

    [Fact]
    public void Routing_SpectatorSay_StaysAmongSpectators_WhenNoSpectators()
    {
        // QC chat.qc:241-245 — a spectator's PUBLIC say is downgraded to a spectator-only message (teamsay = -1)
        // only when CHAT_NOSPECTATORS() is on (g_chat_nospectators 1). With it off (the default), a spectator's
        // public say reaches everyone — so the downgrade requires the cvar.
        var world = NewWorld();
        Cvars.Set("g_chat_nospectators", "1");
        Player player = AddPlayer(world, "player");
        Player spec1 = AddSpectator(world, "spec1");
        Player spec2 = AddSpectator(world, "spec2");
        var inbox = CaptureDelivery(world);

        world.Commands.Chat.Say(spec1, 0, null, "anyone there", false);

        Assert.Equal(1, Received(inbox, spec2)); // other spectator gets it
        Assert.Equal(0, Received(inbox, player)); // live players do not
    }

    [Fact]
    public void Routing_SpectatorPublicSay_ReachesEveryone_WhenSpectatorsVisible()
    {
        // With g_chat_nospectators 0 (default), a spectator's public say is a normal broadcast to all real clients.
        var world = NewWorld();
        Player player = AddPlayer(world, "player");
        Player spec1 = AddSpectator(world, "spec1");
        var inbox = CaptureDelivery(world);

        world.Commands.Chat.Say(spec1, 0, null, "hello players", false);
        Assert.Equal(1, Received(inbox, player)); // a live player receives the spectator's public message
    }

    [Fact]
    public void Routing_PrivateTell_ReachesOnlyTarget()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        Player b = AddPlayer(world, "b");
        Player c = AddPlayer(world, "c");
        var inbox = CaptureDelivery(world);

        int ret = world.Commands.Chat.Say(a, 0, b, "secret", false);

        Assert.Equal(1, ret);
        Assert.Equal(1, Received(inbox, b)); // only the target
        Assert.Equal(0, Received(inbox, c)); // not a bystander
        Assert.True(Received(inbox, a) >= 1); // the sender sees the "you tell B" echo
    }

    // ============================================================================== ignore

    [Fact]
    public void Ignore_BlocksPublicSayFromIgnoredSender()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player b = AddPlayer(world, "b", uid: "uid_b");
        var inbox = CaptureDelivery(world);

        // b ignores a → a's public say must not reach b.
        Assert.Equal(1, Chat.IgnoreAddPlayer(b, a));
        Assert.True(Chat.IgnorePlayerInList(b, a));

        world.Commands.Chat.Say(a, 0, null, "you can't hear me", false);
        Assert.Equal(0, Received(inbox, b));
    }

    [Fact]
    public void Ignore_TellToIgnoringTarget_ReturnsMinusOne_AndDropped()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player b = AddPlayer(world, "b", uid: "uid_b");
        var inbox = CaptureDelivery(world);

        Chat.IgnoreAddPlayer(b, a); // b ignores a
        int ret = world.Commands.Chat.Say(a, 0, b, "psst", false);

        Assert.Equal(-1, ret);            // QC: source ignored by privatesay → return -1
        Assert.Equal(0, Received(inbox, b)); // the message never reaches b
    }

    [Fact]
    public void Unignore_RestoresDelivery()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player b = AddPlayer(world, "b", uid: "uid_b");
        var inbox = CaptureDelivery(world);

        Chat.IgnoreAddPlayer(b, a);
        Chat.IgnoreRemovePlayer(b, a);
        Assert.False(Chat.IgnorePlayerInList(b, a));

        world.Commands.Chat.Say(a, 0, null, "back again", false);
        Assert.Equal(1, Received(inbox, b));
    }

    [Fact]
    public void ClearIgnores_DropsAll()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player b = AddPlayer(world, "b", uid: "uid_b");
        Player c = AddPlayer(world, "c", uid: "uid_c");

        Chat.IgnoreAddPlayer(a, b);
        Chat.IgnoreAddPlayer(a, c);
        Assert.True(Chat.IgnorePlayerInList(a, b));
        Chat.IgnoreClearAll(a);
        Assert.False(Chat.IgnorePlayerInList(a, b));
        Assert.False(Chat.IgnorePlayerInList(a, c));
    }

    [Fact]
    public void Ignore_RejectsTargetWithoutPersistentId()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player anon = AddPlayer(world, "anon", uid: "uid_clear");
        anon.PersistentId = ""; // an unauthenticated player can't be permanently keyed
        Assert.Equal(0, Chat.IgnoreAddPlayer(a, anon)); // 0 = not added
        Assert.False(Chat.IgnorePlayerInList(a, anon));
    }

    [Fact]
    public void Ignore_FullList_ReturnsZero()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        for (int i = 0; i < Chat.IgnoreMaxPlayers; i++)
        {
            Player victim = AddPlayer(world, $"v{i}", uid: $"uid_v{i}");
            Assert.Equal(1, Chat.IgnoreAddPlayer(a, victim));
        }
        Player overflow = AddPlayer(world, "overflow", uid: "uid_overflow");
        Assert.Equal(0, Chat.IgnoreAddPlayer(a, overflow)); // list full
    }

    // ============================================================================== mute (fake-accept)

    [Fact]
    public void Muted_FakeAccept_SenderSeesButOthersDont()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        Player b = AddPlayer(world, "b");
        var inbox = CaptureDelivery(world);

        a.Muted = true;
        int ret = world.Commands.Chat.Say(a, 0, null, "shouldn't broadcast", false);

        Assert.Equal(-1, ret);               // QC: muted → fake-accept (-1)
        Assert.True(Received(inbox, a) >= 1); // the muted sender still sees their own line
        Assert.Equal(0, Received(inbox, b));  // no one else does
    }

    // ============================================================================== flood

    [Fact]
    public void Flood_ThirdRapidPublicSay_IsThrottled()
    {
        // Shipped broadcast flood: spl 3, burst 2, lmax 2. With burst-1 = 1 line of headroom, the first two
        // same-frame lines pass and the third is rejected (flood == 1 → ret 0 with notify_flooder on).
        var world = NewWorld();
        Player a = AddPlayer(world, "a");

        Assert.Equal(1, world.Commands.Chat.Say(a, 0, null, "one", true));
        Assert.Equal(1, world.Commands.Chat.Say(a, 0, null, "two", true));
        Assert.Equal(0, world.Commands.Chat.Say(a, 0, null, "three", true)); // throttled
    }

    [Fact]
    public void Flood_TimestampsPersistAcrossCalls()
    {
        // The flood stamp must persist on the Player between Say calls (a per-tick reset would let a flooder past).
        var world = NewWorld();
        Player a = AddPlayer(world, "a");

        world.Commands.Chat.Say(a, 0, null, "one", true);
        Assert.True(a.FloodControlChat > 0f, "the broadcast flood stamp should advance and persist");
    }

    [Fact]
    public void Flood_TeamAndBroadcastUseSeparateStamps()
    {
        // The three say-types use distinct flood fields (FloodControlChat / ChatTeam / ChatTell), so flooding the
        // public channel must not throttle the team channel.
        var world = NewWorld("tdm");
        Player a = AddPlayer(world, "a", Teams.Red);

        world.Commands.Chat.Say(a, 0, null, "p1", true);
        world.Commands.Chat.Say(a, 0, null, "p2", true);
        world.Commands.Chat.Say(a, 0, null, "p3", true); // public now throttled

        // a fresh team-say still goes through (separate stamp).
        Assert.Equal(1, world.Commands.Chat.Say(a, 1, null, "team line", true));
    }

    // ============================================================================== formatmessage

    [Fact]
    public void Format_PercentPercent_LiteralPercent()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        Assert.Equal("100%", world.Commands.Chat.FormatMessage(a, "100%%"));
    }

    [Fact]
    public void Format_Health_ExpandsToValue()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        a.Health = 87f;
        Assert.Equal("health: 87", world.Commands.Chat.FormatMessage(a, "health: %h"));
    }

    [Fact]
    public void Format_Armor_ExpandsToValue()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        a.GiveResource(ResourceType.Armor, 25f);
        Assert.Equal("armor: 25", world.Commands.Chat.FormatMessage(a, "armor: %a"));
    }

    [Fact]
    public void Format_Location_FallsBackToSomewhere()
    {
        // With no target_location volumes, %l resolves to "somewhere" (the QC NearestLocation default).
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        Assert.Equal("at somewhere", world.Commands.Chat.FormatMessage(a, "at %l"));
    }

    [Fact]
    public void Format_UnknownEscape_LeftVerbatim()
    {
        // An unknown %-escape with no mutator hook is emitted verbatim (QC default: replacement = substring(p,2)).
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        Assert.Equal("%q done", world.Commands.Chat.FormatMessage(a, "%q done"));
    }

    [Fact]
    public void Format_ReplacementBudget_StopsAtSeven()
    {
        // QC caps expansion at 7 replacements per call (n = 7). The 8th %% stays literal "%%".
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        string outp = world.Commands.Chat.FormatMessage(a, "%%%%%%%%%%%%%%%%"); // eight "%%" pairs (16 chars)
        Assert.Equal("%%%%%%%%%", outp); // 7 pairs collapse to "%" (7 chars), the 8th pair stays "%%" → 9 chars
    }

    // ============================================================================== command wiring

    [Fact]
    public void Command_SayTeam_Registered_AndCallerGated()
    {
        var world = NewWorld("tdm");
        Assert.True(world.Commands.Has("say_team"));
        CommandContext ctx = world.Commands.Execute("say_team hi", isServerConsole: true);
        Assert.Contains("client command", ctx.Output);
    }

    [Fact]
    public void Command_Tell_SelfTell_Rejected()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a");
        CommandContext ctx = world.Commands.Execute($"tell a hello", isServerConsole: false, caller: a);
        Assert.Contains("yourself", ctx.Output);
    }

    [Fact]
    public void Command_Ignore_Unignore_Roundtrip()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player b = AddPlayer(world, "b", uid: "uid_b");

        CommandContext add = world.Commands.Execute("ignore b", isServerConsole: false, caller: a);
        Assert.Contains("no longer receive", add.Output);
        Assert.True(Chat.IgnorePlayerInList(a, b));

        CommandContext already = world.Commands.Execute("ignore b", isServerConsole: false, caller: a);
        Assert.Contains("already ignored", already.Output);

        CommandContext rm = world.Commands.Execute("unignore b", isServerConsole: false, caller: a);
        Assert.Contains("again", rm.Output);
        Assert.False(Chat.IgnorePlayerInList(a, b));
    }

    [Fact]
    public void Command_ClearIgnores_Registered_AndClears()
    {
        var world = NewWorld();
        Player a = AddPlayer(world, "a", uid: "uid_a");
        Player b = AddPlayer(world, "b", uid: "uid_b");
        Chat.IgnoreAddPlayer(a, b);
        CommandContext ctx = world.Commands.Execute("clear_ignores", isServerConsole: false, caller: a);
        Assert.Contains("cleared", ctx.Output);
        Assert.False(Chat.IgnorePlayerInList(a, b));
    }
}
