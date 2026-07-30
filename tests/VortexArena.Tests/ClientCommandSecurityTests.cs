using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// [T47] Tests for the client-command security gate — the port of server/command/cmd.qc
/// <c>SV_ParseClientCommand</c> (the 3-gate pre-filter: UTF-8 round-trip, Ban_MaybeEnforceBanOnce, per-client
/// flood bucket) and the SERVER_COMMANDS vs CLIENT_COMMANDS privilege split (reg.qh). The 3 gates' pure pieces
/// live in <see cref="ClientCommandRegistry"/> (testable in this assembly); the privilege split is enforced in
/// <see cref="Commands.Execute"/> when a client (non-null caller) issues a command, and exercised directly here.
/// </summary>
[Collection("GlobalState")]
public class ClientCommandSecurityTests
{
    private static GameWorld NewWorld(string gametype = "dm")
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot(gametype);
        return world;
    }

    private static Player NewCaller(string name = "p")
        => new() { NetName = name, Flags = EntFlags.Client, PlayerId = 1 };

    // =========================================================================== allowlist (CLIENT_COMMANDS)

    /// <summary>
    /// QC reg.qh CLIENT_COMMANDS + the client-reachable common commands + cheat verbs: each must be allowed for a
    /// non-null caller (NOT rejected by the privilege gate as "Unknown command"). We assert the gate lets them
    /// through — the verb's own usage/effect output (or empty) is fine; what must NOT appear is the gate's
    /// rejection string, which is the same "Unknown command" an unregistered verb produces.
    /// </summary>
    [Theory]
    [InlineData("say hello")]
    [InlineData("say_team hi team")]
    [InlineData("tell #2 psst")]
    [InlineData("ignore #2")]
    [InlineData("unignore #2")]
    [InlineData("clear_ignores")]
    [InlineData("vote help")]          // COMMON_COMMAND(vote) — client-callable (call/yes/no/abstain/…)
    [InlineData("ready")]
    [InlineData("ready_up")]
    [InlineData("join")]
    [InlineData("spectate")]
    [InlineData("selectteam red")]
    [InlineData("kill")]
    [InlineData("autoswitch on")]
    [InlineData("physics list")]
    [InlineData("clientversion 806")]
    [InlineData("sentcvar cl_autoswitch 1")]
    [InlineData("suggestmap boil")]
    [InlineData("voice taunt")]
    [InlineData("minigame list")]
    [InlineData("impulse 10")]
    [InlineData("weapon_next")]
    [InlineData("reload")]
    [InlineData("records")]
    [InlineData("rankings")]
    [InlineData("lsmaps")]
    [InlineData("printmaplist")]
    [InlineData("ladder")]
    [InlineData("who")]
    [InlineData("time")]
    [InlineData("teamstatus")]
    public void ClientCallable_Verbs_AreNotRejectedByPrivilegeGate(string line)
    {
        var world = NewWorld();
        Player p = NewCaller();
        // give the caller a real edict slot so handlers that touch the entity (voice/kill/impulse) don't NRE.
        Api.Entities.SetOrigin(p, System.Numerics.Vector3.Zero);

        CommandContext ctx = world.Commands.Execute(line, isServerConsole: false, caller: p);

        // The privilege gate rejects with the literal "Unknown command \"<verb>\"". A client-callable verb must
        // reach its handler, so that exact rejection must NOT be present.
        string verb = line.Split(' ')[0];
        Assert.DoesNotContain($"Unknown command \"{verb}\"", ctx.Output);
    }

    /// <summary>Every name in the allowlist that the port actually registers must resolve to a real command (no
    /// stale entries pointing at a verb that doesn't exist).</summary>
    [Theory]
    [InlineData("say")]
    [InlineData("kill")]
    [InlineData("impulse")]
    [InlineData("voice")]
    [InlineData("minigame")]
    [InlineData("editmob")]
    public void AllowlistedVerbs_AreRegistered(string verb)
    {
        var world = NewWorld();
        Assert.True(ClientCommandRegistry.IsClientCallable(verb), $"{verb} should be client-callable");
        Assert.True(world.Commands.Has(verb), $"{verb} should be a registered command");
    }

    // =========================================================================== server-only block

    /// <summary>
    /// QC SERVER_COMMANDS + the generic cvar-reflection family: a client (non-console source carrying a caller)
    /// must NOT be able to invoke these — the privilege gate rejects them with "Unknown command" exactly as if the
    /// verb didn't exist. This is the core T47 fix: before the gate, a client could invoke any admin verb. We also
    /// assert each IS a real registered command (so the gate is what blocks it, not a missing registration) and is
    /// NOT in the client-callable allowlist. The console-runs-them direction is covered by
    /// <see cref="ClientSet_DoesNotMutateWorldCvar"/> on the side-effect-free <c>set</c> path.
    /// </summary>
    [Theory]
    [InlineData("set g_grappling_hook 1")]
    [InlineData("seta cl_anything 5")]
    [InlineData("cvar timelimit")]
    [InlineData("toggle g_warmup")]
    [InlineData("kick somebody")]
    [InlineData("ban 1.2.3.4")]
    [InlineData("kickban somebody")]
    [InlineData("unban 0")]
    [InlineData("banlist")]
    [InlineData("mute somebody")]
    [InlineData("playban somebody")]
    [InlineData("endmatch")]
    [InlineData("restart")]
    [InlineData("map stormkeep")]
    [InlineData("gotomap stormkeep")]
    [InlineData("nextmap stormkeep")]
    [InlineData("gametype ctf")]
    [InlineData("allready")]
    [InlineData("resetmatch")]
    [InlineData("shuffleteams")]
    [InlineData("moveplayer x spec")]
    [InlineData("allspec")]
    [InlineData("lockteams")]
    [InlineData("settemp g_grappling_hook 1")]
    [InlineData("settemp_restore")]
    [InlineData("bot_add")]
    [InlineData("bot_remove")]
    [InlineData("rpn /g_x 1 def")]
    [InlineData("maplist add stormkeep")]
    public void ServerOnly_Verbs_AreRejectedForClients(string line)
    {
        var world = NewWorld();
        Player p = NewCaller();
        string verb = line.Split(' ')[0];

        // it IS a real command (so the rejection is the privilege gate, not a missing registration) but is NOT
        // client-callable, so a client issuing it is rejected with the same surface as an unknown command.
        Assert.True(world.Commands.Has(verb), $"{verb} should be a registered (server-only) command");
        Assert.False(ClientCommandRegistry.IsClientCallable(verb), $"{verb} must not be client-callable");

        CommandContext asClient = world.Commands.Execute(line, isServerConsole: false, caller: p);
        Assert.Contains($"Unknown command \"{verb}\"", asClient.Output);
    }

    /// <summary>The cvar-reflection hole specifically: a client's `set` must NOT mutate a world cvar (the exact
    /// privilege-escalation T47 closes).</summary>
    [Fact]
    public void ClientSet_DoesNotMutateWorldCvar()
    {
        var world = NewWorld();
        Cvars.Set("g_grappling_hook", "0");
        world.Commands.Execute("set g_grappling_hook 1", isServerConsole: false, caller: NewCaller());
        Assert.Equal("0", Cvars.String("g_grappling_hook")); // unchanged — the client `set` was rejected

        // the console can still set it (proving the cvar + command path work; only the client source is blocked).
        world.Commands.Execute("set g_grappling_hook 1", isServerConsole: true, caller: null);
        Assert.Equal("1", Cvars.String("g_grappling_hook"));
        Cvars.Set("g_grappling_hook", "0"); // restore shared global state
    }

    // =========================================================================== GATE 1: UTF-8 validation

    [Theory]
    [InlineData("say hello world")]
    [InlineData("vote call restart")]
    [InlineData("name Pläyer")]       // valid 2-byte UTF-8 (U+00E4 ä)
    [InlineData("emote ☠ §")]    // valid multi-byte UTF-8 (U+2620 skull, U+00A7 §)
    [InlineData("")]                       // empty round-trips (QC: command == command2)
    public void Utf8_WellFormed_Passes(string line)
        => Assert.True(ClientCommandRegistry.IsValidUtf8Command(line));

    [Theory]
    [InlineData("bad�cmd")]           // U+FFFD: the wire decoder's malformed-byte marker → reject
    [InlineData("\uD800broken")]           // lone high surrogate (can't encode as UTF-8) → reject
    [InlineData("trail\uDC00")]            // lone low surrogate → reject
    public void Utf8_Malformed_Rejected(string line)
        => Assert.False(ClientCommandRegistry.IsValidUtf8Command(line));

    // =========================================================================== GATE 3: flood control

    /// <summary>
    /// QC cmd.qc:1241 budget: <c>if (mod_time &lt; store.cmd_floodtime)</c> rejects ONLY when the ceiling is
    /// STRICTLY behind the cursor. With count=8, time=1s, frameStart held fixed at 0 and a fresh client
    /// (cursor 0), <c>mod_time = 0 + 8*1 = 8</c> is constant and the cursor climbs 1,2,…; the cursor reaches
    /// <c>mod_time</c> (8) on the 9th accept (<c>8 &lt; 8</c> is FALSE → accepted), so the burst that passes is
    /// <c>count + 1 = 9</c> commands; the 10th (cursor now 9, <c>8 &lt; 9</c> true) is the first rejected. A
    /// <c>&lt;=</c> comparison would reject the 9th (equal) command and cap the burst at <c>count</c> = 8,
    /// diverging from Base — this test pins the strict-less-than boundary.
    /// </summary>
    [Fact]
    public void Flood_FreshClient_BurstsExactlyCount_ThenRejects()
    {
        float cursor = 0f;
        const float frameStart = 0f, count = 8f, time = 1f;
        for (int i = 0; i < 9; i++)
            Assert.True(ClientCommandRegistry.TryPassFlood(ref cursor, frameStart, count, time),
                $"command {i + 1} of the initial burst should pass (the 9th sits at mod_time == cursor and QC's '<' admits it)");
        Assert.False(ClientCommandRegistry.TryPassFlood(ref cursor, frameStart, count, time),
            "the 10th command in the same frame (cursor strictly past mod_time) should be flood-rejected");
    }

    /// <summary>Time advancing past the cursor reopens the budget: after the burst, stepping the sim clock forward
    /// by enough seconds lets a command through again (the bucket drains at 1 command/sec).</summary>
    [Fact]
    public void Flood_DrainsOverTime()
    {
        float cursor = 0f;
        const float count = 8f, time = 1f;
        // exhaust the burst at frameStart=0: QC's strict '<' admits count+1 = 9 (the 9th sits at mod_time==cursor).
        for (int i = 0; i < 9; i++) ClientCommandRegistry.TryPassFlood(ref cursor, 0f, count, time);
        Assert.False(ClientCommandRegistry.TryPassFlood(ref cursor, 0f, count, time));

        // cursor is now 9 (9 accepts × 1s). A command at frameStart=1 has mod_time = 1 + 8 = 9; 9 < 9 is FALSE → passes.
        Assert.True(ClientCommandRegistry.TryPassFlood(ref cursor, 1f, count, time),
            "after 1s the budget should admit one more command");
    }

    /// <summary>QC: antispam_time &lt; 0 ("-1" = no limit) disables the limiter entirely — every command passes
    /// and the cursor is untouched.</summary>
    [Fact]
    public void Flood_NegativeTime_DisablesLimiter()
    {
        float cursor = 12345f;
        for (int i = 0; i < 1000; i++)
            Assert.True(ClientCommandRegistry.TryPassFlood(ref cursor, 0f, 8f, -1f));
        Assert.Equal(12345f, cursor); // cursor never advanced
    }

    // =========================================================================== flood-exempt switch

    [Theory]
    [InlineData("begin")]
    [InlineData("download")]
    [InlineData("prespawn")]
    [InlineData("spawn")]
    [InlineData("pause")]
    [InlineData("sentcvar")]
    [InlineData("mv_getpicture")]
    [InlineData("wpeditor")]
    [InlineData("say")]
    [InlineData("say_team")]
    [InlineData("tell")]
    public void FloodExempt_EngineServerChatVerbs(string verb)
        => Assert.True(ClientCommandRegistry.IsCommandFloodExempt(verb, ""));

    [Theory]
    [InlineData("kill")]
    [InlineData("join")]
    [InlineData("vote")]
    [InlineData("autoswitch")]
    public void FloodNotExempt_OrdinaryClientVerbs(string verb)
        => Assert.False(ClientCommandRegistry.IsCommandFloodExempt(verb, ""));

    /// <summary>QC cmd.qc:1203-1215 minigame partial exemption: a common minigame subcommand (or an empty arg) is
    /// flood-controlled (NOT exempt); an individual-minigame move command is exempt.</summary>
    [Theory]
    [InlineData("create", false)]        // common subcommand → flood-controlled
    [InlineData("join", false)]
    [InlineData("list", false)]
    [InlineData("list-sessions", false)]
    [InlineData("end", false)]
    [InlineData("part", false)]
    [InlineData("invite", false)]
    [InlineData("", false)]              // empty arg → goto flood_control (NOT exempt)
    [InlineData("throw", true)]         // an individual-minigame move → exempt
    [InlineData("pong_aimore", true)]
    public void FloodExempt_MinigamePartial(string arg1, bool exempt)
        => Assert.Equal(exempt, ClientCommandRegistry.IsCommandFloodExempt("minigame", arg1));

    // =========================================================================== GATE 2: ban enforcement

    /// <summary>
    /// QC Ban_MaybeEnforceBan (ipban.qc): a banned client is told + dropped, and the call returns true. We insert
    /// an IP ban for the player's address, then assert MaybeEnforceBan reports banned and fires the drop pipeline.
    /// </summary>
    [Fact]
    public void Ban_EnforcedOnBannedClient_DropsAndReturnsTrue()
    {
        var world = NewWorld();
        var dropped = new List<Player>();
        world.Bans.DropClient = (p, _) => dropped.Add(p); // observe drops directly

        Player banned = NewCaller("banned");
        banned.NetAddress = "5.6.7.8";

        world.Bans.Insert("5.6.7.8", 600f, "test ban"); // ban the /32

        try
        {
            Assert.True(world.Bans.IsClientBanned(banned));
            Assert.True(world.Bans.MaybeEnforceBan(banned));
            Assert.Contains(banned, dropped);
        }
        finally
        {
            Cvars.Set("g_banned_list", ""); // restore shared global state (Insert persists into this cvar)
        }
    }

    /// <summary>An un-banned client is not enforced (returns false, not dropped) — the gate is a no-op for a
    /// normal client.</summary>
    [Fact]
    public void Ban_NotEnforcedOnCleanClient()
    {
        var world = NewWorld();
        var dropped = new List<Player>();
        world.Bans.DropClient = (p, _) => dropped.Add(p);

        Player clean = NewCaller("clean");
        clean.NetAddress = "9.9.9.9";

        Assert.False(world.Bans.MaybeEnforceBan(clean));
        Assert.Empty(dropped);
    }

    // =========================================================================== antispam cvar registration

    [Fact]
    public void AntispamCvars_RegisteredWithBaseDefaults()
    {
        var world = NewWorld();
        // commands.cfg:155-156 — time 1, count 8. Registration is idempotent, so a fresh boot has the defaults.
        Assert.Equal(1f, Cvars.FloatOr("sv_clientcommand_antispam_time", -999f));
        Assert.Equal(8f, Cvars.FloatOr("sv_clientcommand_antispam_count", -999f));
    }
}
