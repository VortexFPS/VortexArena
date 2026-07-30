using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §5 (server infrastructure): bans, cheats, anticheat, the vote system, the event log, the
/// player-stats report, campaign, demo control, the map rotation/vote flow, and the timeout system — plus a
/// GameWorld integration smoke that exercises the end-of-match map rotation.
/// </summary>
[Collection("GlobalState")]
public class ServerInfraTests
{
    public ServerInfraTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    private static Player NewPlayer(string name = "p", string ip = "1.2.3.4", string id = "")
        => new() { NetName = name, NetAddress = ip, PersistentId = id, Flags = EntFlags.Client, PlayerId = 1 };

    // =========================================================================== step-up limiter cvar registration

    [Fact]
    public void StepUpSpeedCvars_Registered_Discoverable_AndDefaultIsNoOp()
    {
        // The ctor's Cvars.RegisterDefaults() stamped them into the ambient store — exactly what MenuState.Boot
        // does to the shared console store (so cvarlist/autocomplete, which read CvarService.Names, list them) and
        // what GameWorld.Boot does to the server store (so the listen-server bridge's Has(name) gate forwards a
        // console `set` to the physics tick). Presence in the store == listed by cvarlist.
        Assert.Equal("1", Api.Cvars.GetString("sv_step_upspeed_scale"));
        Assert.Equal("-1", Api.Cvars.GetString("sv_step_upspeed_max"));

        // Registering did NOT shadow the FromCvars fallback with a wrong value: the registered defaults are the
        // no-op identity, byte-equal to MovementParameters.Defaults (scale 1, max -1 = disabled).
        MovementParameters mp = MovementParameters.FromCvars();
        Assert.Equal(1f, mp.StepUpSpeedScale);
        Assert.Equal(-1f, mp.StepUpSpeedMax);

        // A console-style `set` is read live, and a real configured 0 survives the EXISTS-gated read.
        Api.Cvars.Set("sv_step_upspeed_scale", "0");
        Api.Cvars.Set("sv_step_upspeed_max", "80");
        MovementParameters mp2 = MovementParameters.FromCvars();
        Assert.Equal(0f, mp2.StepUpSpeedScale);
        Assert.Equal(80f, mp2.StepUpSpeedMax);
    }

    [Fact]
    public void BackfillModified_CarriesUserOverridesAcrossAMapBoot_WithoutClobberingDefaults()
    {
        // Simulate the two-store split: a SHARED (console/menu) store where the user changed some cvars, and a
        // freshly-booted SERVER store reloaded from the cfg tree (so it holds the cfg defaults). Backfill must carry
        // the user's CHANGED cvars (the "sv_step_* lost on new map" bug) but leave the server's untouched cfg/map
        // values — and the ruleset/map ones the user never touched — alone.
        var shared = new CvarService();
        var server = new CvarService();

        // both stores "load the cfg tree" — same registered defaults (server does NOT know the client-only cvar).
        shared.Register("sv_step_upspeed_max", "-1");
        shared.Register("sv_maxspeed", "360");
        shared.Register("sv_gravity", "800");
        shared.Register("cl_local_only", "0");   // a CLIENT cvar — present in shared, absent from the server store
        server.Register("sv_step_upspeed_max", "-1");
        server.Register("sv_maxspeed", "360");
        server.Register("sv_gravity", "800");

        // the user changes things in the console (shared store only)
        shared.Set("sv_step_upspeed_max", "5");   // CHANGED → must carry
        shared.Set("sv_gravity", "200");          // CHANGED but BOOT-AUTHORED (map worldspawn) → must NOT carry
        shared.Set("cl_local_only", "1");         // CHANGED but server doesn't Has it → must NOT carry
        // sv_maxspeed left at its default 360 → not modified → must NOT carry (don't clobber a ruleset value)

        // the new map's server store happens to run a ruleset value the user never touched
        server.Set("sv_maxspeed", "320");         // a ruleset/map override the backfill must preserve

        var exclude = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal) { "sv_gravity" };
        CvarService.BackfillModified(shared, server, exclude);

        Assert.Equal(5f, server.GetFloat("sv_step_upspeed_max"));   // user override carried across the boot
        Assert.Equal(320f, server.GetFloat("sv_maxspeed"));         // untouched-by-user ruleset value preserved
        Assert.Equal(800f, server.GetFloat("sv_gravity"));          // boot-authored exclusion respected
        Assert.False(server.Has("cl_local_only"));                  // client-only cvar never leaked into the server
    }

    // ============================================================== config-save rule (DP Cvar_WriteVariables)

    [Fact]
    public void ArchivedNamesToPersist_WritesOnlyUserChangesOffTheLockedDefault()
    {
        // Mirrors MenuState.Boot's order: load the stock tree (via `set`, as the interpreter does), LockDefaults,
        // then apply the user's menu/console changes (MarkArchived == DP's seta/CVAR_SAVE bit). The save then
        // writes only what the user actually moved off the shipped default — DP's config.cfg rule — not a dump.
        var cvars = new CvarService();
        cvars.Set("crosshair", "3");      // shipped default
        cvars.Set("sensitivity", "6");    // shipped default
        cvars.Set("fov", "90");           // shipped default
        cvars.LockDefaults();

        cvars.Set("crosshair", "12");
        cvars.MarkArchived("crosshair");  // changed off default + archived → MUST persist
        cvars.Set("sensitivity", "6");    // re-set to the SAME locked default
        cvars.MarkArchived("sensitivity");// archived but == default → MUST be omitted (lean diff, not full dump)
        // fov never touched → not archived → omitted

        var persist = new HashSet<string>(cvars.ArchivedNamesToPersist, System.StringComparer.Ordinal);
        Assert.Contains("crosshair", persist);
        Assert.DoesNotContain("sensitivity", persist);
        Assert.DoesNotContain("fov", persist);
    }

    [Fact]
    public void ArchivedNamesToPersist_RegisteredCvarAtDefault_NotWritten_EvenIfRegisteredAfterLock()
    {
        // THE config.cfg-bloat fix. A port-extension cl_* / hud_* cvar registers via ClientSettings.ApplyAll —
        // AFTER MenuState.Boot's LockDefaults — and at its default value. It must NOT be written, just like DP
        // doesn't dump CF_ALLOCATED cvars once they're cleared by a Register (CF_ALLOCATED is cleared, the rule
        // falls back to value!=defstring). This is the exact sequence that bloated config.cfg with ~195 setas.
        var cvars = new CvarService();
        cvars.Set("crosshair", "3");      // a stock cfg-tree cvar present at lock time
        cvars.LockDefaults();             // cl_vignette is NOT in the store yet (registers later)

        // a previous bloated config.cfg is loaded first: the cvar is created by `seta` at its default value...
        cvars.Set("cl_vignette", "1");
        cvars.MarkArchived("cl_vignette");
        // ...then the overlay registers its real default (== the loaded value). Register clears Allocated + adopts
        // the authoritative default, so the cvar is now "unchanged from default" and drops out of the save.
        cvars.Register("cl_vignette", "1", CvarFlags.Save);

        Assert.False(cvars.IsModified("cl_vignette"));
        Assert.DoesNotContain("cl_vignette",
            new HashSet<string>(cvars.ArchivedNamesToPersist, System.StringComparer.Ordinal));
    }

    [Fact]
    public void ArchivedNamesToPersist_RegisteredCvarChangedFromDefault_IsWritten()
    {
        // The flip side: the user actually changed a registered cvar off its default. Register promotes the real
        // default (1), so value (2) != default (1) → it IS written — even though it was loaded/created before the
        // overlay's Register ran (the inferred default "2" must not mask the real default "1").
        var cvars = new CvarService();
        cvars.LockDefaults();
        cvars.Set("cl_vignette", "2");                       // loaded from config at a NON-default value
        cvars.MarkArchived("cl_vignette");
        cvars.Register("cl_vignette", "1", CvarFlags.Save);  // overlay declares the real default 1

        Assert.True(cvars.IsModified("cl_vignette"));
        Assert.Contains("cl_vignette",
            new HashSet<string>(cvars.ArchivedNamesToPersist, System.StringComparer.Ordinal));
    }

    [Fact]
    public void ArchivedNamesToPersist_UserCreatedCvarNeverRegistered_AlwaysWritten()
    {
        // DP's CF_ALLOCATED & !CF_DEFAULTSET escape: a cvar the user created in the console (`seta my_pref 1`) that
        // no code ever Registers has no authoritative default we can compare against, so it's always saved — losing
        // it would be wrong. It stays Allocated (never promoted) and unlocked (created after LockDefaults).
        var cvars = new CvarService();
        cvars.LockDefaults();
        cvars.Set("my_pref", "1");
        cvars.MarkArchived("my_pref");

        Assert.Contains("my_pref",
            new HashSet<string>(cvars.ArchivedNamesToPersist, System.StringComparer.Ordinal));
    }

    [Fact]
    public void ConfigLoad_SetaVsSet_DecidesTheArchiveBit_LikeDpCfgProvenance()
    {
        // DP: `seta` in the shipped cfg tree sets CVAR_SAVE on the cvar (Cvar_SetA_f → Cvar_Get flags|=);
        // plain `set` does not — the TREE, not the C# registration tables, is the authority on what may
        // persist to config.cfg. MenuState.Boot wires ConfigLoader's archive hook to MarkArchived so that
        // provenance survives the port.
        var cvars = new CvarService();
        const string cfg = "seta crosshair_enabled 1\nset sv_spectate 1\n";
        XonoticGodot.Common.Config.ConfigLoader.Load(cvars, p => p == "boot.cfg" ? cfg : null,
            name => cvars.MarkArchived(name), "boot.cfg");
        cvars.LockDefaults();

        Assert.True(cvars.IsArchived("crosshair_enabled"));   // seta → archiveable
        Assert.False(cvars.IsArchived("sv_spectate"));        // plain set → never archived (Base parity)

        // Change BOTH off their locked defaults: only the seta-declared one is eligible for config.cfg.
        cvars.Set("crosshair_enabled", "0");
        cvars.Set("sv_spectate", "0");
        var persist = new HashSet<string>(cvars.ArchivedNamesToPersist, System.StringComparer.Ordinal);
        Assert.Contains("crosshair_enabled", persist);
        Assert.DoesNotContain("sv_spectate", persist);
    }

    [Fact]
    public void ServerDefaults_OpsCvars_AreNotArchived_MatchingBasePlainSet()
    {
        // Base declares these with plain `set` (xonotic-server.cfg:14/102/124/194/233) or registers them
        // C-side without CVAR_SAVE (`skill` — a bare `skill 8` line over DP sv_main.c's flags-0 cvar), so
        // none may persist to config.cfg. They used to carry CvarFlags.Save here — which is how every
        // --host/--bots/perf run's writes leaked into the user's real config as fake preferences
        // (bot_number 6 / skill 0 / sv_spectate 0 / g_maplist_mostrecent stormkeep / …).
        foreach (string name in new[]
                 { "bot_number", "bot_join_empty", "skill", "minplayers", "sv_spectate", "g_maplist_mostrecent" })
        {
            CvarDef def = System.Array.Find(Cvars.Defaults, d => d.Name == name);
            Assert.Equal(name, def.Name); // present in the defaults table at all
            Assert.Equal(CvarFlags.None, def.Flags & CvarFlags.Save);
        }

        // Positive control: g_maplist IS `seta` in Base (xonotic-common.cfg:95) and stays archived.
        CvarDef maplist = System.Array.Find(Cvars.Defaults, d => d.Name == "g_maplist");
        Assert.NotEqual(CvarFlags.None, maplist.Flags & CvarFlags.Save);
    }

    // =========================================================================================== bans

    [Fact]
    public void Bans_GetClientIp_IPv4_DerivesMasks()
    {
        var p = NewPlayer(ip: "12.34.56.78:27015");
        ClientBanIp ip = Bans.GetClientIp(p);
        Assert.True(ip.Ok);
        Assert.Equal("12", ip.Mask8);
        Assert.Equal("12.34", ip.Mask16);
        Assert.Equal("12.34.56", ip.Mask24);
        Assert.Equal("12.34.56.78", ip.Mask32);
    }

    [Fact]
    public void Bans_GetClientIp_RejectsLocalAndBot()
    {
        Assert.False(Bans.GetClientIp(NewPlayer(ip: "bot")).Ok);
        Assert.False(Bans.GetClientIp(NewPlayer(ip: "local")).Ok);
        Assert.False(Bans.GetClientIp(NewPlayer(ip: "")).Ok);
    }

    [Fact]
    public void Bans_InsertAndCheck_ByIp()
    {
        var bans = new Bans();
        Assert.True(bans.Insert("12.34.56.78", 100f, "test"));
        Assert.True(bans.IsClientBanned(NewPlayer(ip: "12.34.56.78:5000")));
        Assert.False(bans.IsClientBanned(NewPlayer(ip: "99.99.99.99")));
    }

    [Fact]
    public void Bans_IdMode_IpBanOnlyCatchesAnonymous()
    {
        Cvars.Set("g_banned_list_idmode", "1");
        var bans = new Bans();
        bans.Insert("12.34.56", 100f, "subnet ban"); // a /24 IP ban
        // anonymous (no crypto id) on that subnet → banned
        Assert.True(bans.IsClientBanned(NewPlayer(ip: "12.34.56.78", id: "")));
        // authenticated (has a crypto id) → NOT caught by the IP ban under idmode
        Assert.False(bans.IsClientBanned(NewPlayer(ip: "12.34.56.78", id: "ABC123")));
    }

    [Fact]
    public void Bans_CryptoIdBan_AlwaysWins()
    {
        var bans = new Bans();
        bans.Insert("DEADBEEF", 100f, "id ban");
        Assert.True(bans.IsClientBanned(NewPlayer(ip: "9.9.9.9", id: "DEADBEEF")));
    }

    [Fact]
    public void Bans_Delete_RemovesBan()
    {
        var bans = new Bans();
        bans.Insert("5.5.5.5", 100f, "x");
        Assert.True(bans.IsClientBanned(NewPlayer(ip: "5.5.5.5")));
        Assert.True(bans.Delete(0));
        Assert.False(bans.IsClientBanned(NewPlayer(ip: "5.5.5.5")));
    }

    [Fact]
    public void Bans_SaveLoad_RoundTripsThroughCvar()
    {
        var bans = new Bans();
        bans.Insert("7.7.7.7", 500f, "x");
        Assert.StartsWith("1 ", Cvars.String("g_banned_list"));
        var reloaded = new Bans();
        reloaded.Load();
        Assert.True(reloaded.IsClientBanned(NewPlayer(ip: "7.7.7.7")));
    }

    [Fact]
    public void Bans_PrefixList_MuteMatchesByIpAndId()
    {
        var p = NewPlayer(ip: "1.2.3.4", id: "ABCDEF");
        Bans.AddToList(p, "g_chatban_list");
        Assert.True(Bans.PlayerInList(p, "g_chatban_list"));
        Assert.True(Bans.PlayerInList(NewPlayer(ip: "1.2.3.4"), "g_chatban_list"));
        Assert.False(Bans.PlayerInList(NewPlayer(ip: "9.9.9.9"), "g_chatban_list"));
        Bans.RemoveFromList(p, "g_chatban_list");
        Assert.False(Bans.PlayerInList(p, "g_chatban_list"));
    }

    // =========================================================================================== cheats

    [Fact]
    public void Cheats_GatedBySvCheats()
    {
        var c = new Cheats();
        Cvars.Set("sv_cheats", "0");
        c.Init();
        var p = NewPlayer();
        Assert.False(c.Allowed(p));
        Assert.Equal(EntFlags.Client, p.Flags & ~EntFlags.GodMode); // god not set

        Cvars.Set("sv_cheats", "1");
        c.Init();
        Assert.True(c.Allowed(p));
        Assert.True(c.Command(p, new[] { "god" }));
        Assert.True((p.Flags & EntFlags.GodMode) != 0);
        Assert.Equal(1, c.CheatCountTotal);
    }

    [Fact]
    public void Cheats_NoclipAndFlyToggle()
    {
        var c = new Cheats();
        Cvars.Set("sv_cheats", "1");
        c.Init();
        var p = NewPlayer();
        c.Command(p, new[] { "noclip" });
        Assert.Equal(MoveType.Noclip, p.MoveType);
        c.Command(p, new[] { "noclip" });
        Assert.Equal(MoveType.Walk, p.MoveType);
    }

    [Fact]
    public void Cheats_GiveAll_GrantsWeaponsAndResources()
    {
        GameRegistries.Bootstrap(); // ensure the weapon registry is populated
        var c = new Cheats();
        Cvars.Set("sv_cheats", "1");
        c.Init();
        var p = NewPlayer();
        Assert.True(c.GiveAll(p));
        Assert.True(p.OwnedWeapons.Count > 0);
        Assert.True(p.GetResource(ResourceType.Health) > 0f);
    }

    // ====================================================================================== anticheat

    [Fact]
    public void AntiCheat_Mean_PowerMean()
    {
        var m = new Mean(1);          // arithmetic mean
        m.Accumulate(2, 1);
        m.Accumulate(4, 1);
        Assert.Equal(3.0, m.Evaluate(), 6);

        var empty = new Mean(5);
        Assert.Equal(0.0, empty.Evaluate(), 6); // no samples → 0
    }

    [Fact]
    public void AntiCheat_MovementOddity_BotlikeReversalScoresHigh()
    {
        // a near-180° reversal is "odd"; an identical direction is not.
        double rev = AntiCheat.MovementOddity(new Vector3(1, 0, 0), new Vector3(-1, 0, 0));
        double same = AntiCheat.MovementOddity(new Vector3(1, 0, 0), new Vector3(1, 0, 0));
        Assert.True(rev > 0);
        Assert.Equal(0.0, same, 6);
    }

    [Fact]
    public void AntiCheat_Physics_AccumulatesWithoutThrowing()
    {
        var ac = new AntiCheat();
        var p = NewPlayer();
        ac.Init(p, 0f);
        var input = new MovementInput { ViewAngles = new Vector3(0, 90, 0), MoveValues = new Vector3(400, 0, 0), FrameTime = 0.014f };
        for (int i = 0; i < 5; i++)
            ac.Physics(p, input, i * 0.014f, 0.014f, 0.014f, 1f);
        // the speedhack baseline gets seeded after the first frame.
        Assert.NotEqual(0f, ac.Of(p).SpeedhackOffset);
    }

    [Fact]
    public void AntiCheat_Display_Verdicts()
    {
        Assert.EndsWith(":N", AntiCheat.Display(0.0, 200, 120, 0.2f, 0.5f)); // below mi, enough time → clean
        Assert.EndsWith(":Y", AntiCheat.Display(0.9, 200, 120, 0.2f, 0.5f)); // above ma → flagged
        Assert.EndsWith(":-", AntiCheat.Display(0.9, 10, 120, 0.2f, 0.5f));  // not enough time → inconclusive
    }

    // ============================================================================================ vote

    [Fact]
    public void Vote_CheckNasty_RejectsInjection()
    {
        Assert.False(VoteController.CheckNasty("restart; quit"));
        Assert.False(VoteController.CheckNasty("map $foo"));
        Assert.True(VoteController.CheckNasty("gotomap dance"));
    }

    [Fact]
    public void Vote_CheckInList_WholeWordAndMapNormalization()
    {
        string list = "restart gotomap endmatch";
        Assert.True(VoteController.CheckInList("gotomap", list));
        Assert.True(VoteController.CheckInList("map", list));   // map → gotomap
        Assert.True(VoteController.CheckInList("chmap", list)); // chmap → gotomap
        Assert.False(VoteController.CheckInList("quit", list));
    }

    [Fact]
    public void Vote_CallAndPass_SinglePlayerAutoMajority()
    {
        var p = NewPlayer();
        var roster = new List<Player> { p };
        var vc = new VoteController { Roster = () => roster };
        string? ran = null;
        vc.VotePassed = cmd => ran = cmd;

        var ctx = new CommandContext(new[] { "vote", "call", "restart" }, isServerConsole: false, caller: p);
        vc.Execute(ctx);
        // one voter, auto-yes → majority(0.5) needs 1 → passes immediately, running "defer 1 restart".
        Assert.Equal("defer 1 restart", ran);
        Assert.False(vc.Active);
    }

    [Fact]
    public void Vote_Reject_WhenMajorityNo()
    {
        var a = NewPlayer("a"); var b = NewPlayer("b"); var c = NewPlayer("c");
        var roster = new List<Player> { a, b, c };
        var vc = new VoteController { Roster = () => roster };
        bool ran = false; vc.VotePassed = _ => ran = true;

        vc.Execute(new CommandContext(new[] { "vote", "call", "endmatch" }, false, a)); // a auto-yes
        vc.Execute(new CommandContext(new[] { "vote", "no" }, false, b));
        vc.Execute(new CommandContext(new[] { "vote", "no" }, false, c));
        // 2 no of 3 → yes can't reach the needed 2 → rejected.
        Assert.False(vc.Active);
        Assert.False(ran);
    }

    [Fact]
    public void Vote_MasterLogin_GrantsAndDoRuns()
    {
        Cvars.Set("sv_vote_master", "1");
        Cvars.Set("sv_vote_master_password", "secret");
        var p = NewPlayer();
        var vc = new VoteController { Roster = () => new List<Player> { p } };
        string? ran = null; vc.VotePassed = cmd => ran = cmd;

        vc.Execute(new CommandContext(new[] { "vote", "master", "login", "secret" }, false, p));
        Assert.True(vc.IsMaster(p));
        vc.Execute(new CommandContext(new[] { "vote", "master", "do", "restart" }, false, p));
        Assert.Equal("defer 1 restart", ran);
    }

    // ========================================================================================= gamelog

    [Fact]
    public void GameLog_Echo_CapturesAndFormatsJoin()
    {
        var log = new GameLog();
        var p = NewPlayer("Frag", "1.2.3.4");
        log.Join(p);
        Assert.Contains(log.Recent, l => l.StartsWith(":join:1:") && l.EndsWith(":Frag"));
    }

    [Fact]
    public void GameLog_ConsoleSink_GatedByCvar()
    {
        var captured = new List<string>();
        var log = new GameLog { ConsoleSink = captured.Add };
        Cvars.Set("sv_eventlog_console", "0");
        log.Echo(":test:1");
        Assert.Empty(captured);
        Cvars.Set("sv_eventlog_console", "1");
        log.Echo(":test:2");
        Assert.Single(captured);
    }

    // ===================================================================================== playerstats

    [Fact]
    public void PlayerStats_DisabledWithoutUri()
    {
        var ps = new PlayerStats();
        Cvars.Set("g_playerstats_gamereport_uri", "");
        ps.Init();
        Assert.False(ps.Enabled);
        Assert.False(ps.DelayMapVote);
    }

    [Fact]
    public void PlayerStats_AccumulatesAndBuildsReport()
    {
        var ps = new PlayerStats();
        Cvars.Set("g_playerstats_gamereport_uri", "http://example/submit");
        ps.Init();
        Assert.True(ps.Enabled);

        var p = NewPlayer("Frag", id: "UID1");
        ps.AddPlayer(p);
        ps.EventPlayer(p, PlayerStats.Wins, 1);
        ps.EventPlayer(p, "kills-1", 5);

        Assert.Single(ps.Players);
        Assert.Equal("UID1", ps.Players[0]);

        string report = ps.BuildReport(120f);
        Assert.StartsWith("V 9\n", report);
        Assert.Contains("P UID1", report);
        Assert.Contains("e wins 1", report);
    }

    [Fact]
    public void PlayerStats_GameReport_WarmupDiscards()
    {
        var ps = new PlayerStats { IsWarmup = () => true };
        Cvars.Set("g_playerstats_gamereport_uri", "http://example/submit");
        ps.Init();
        var p = NewPlayer();
        ps.AddPlayer(p);
        string report = ps.GameReport(true, new[] { p }, 60f, false);
        Assert.Equal("", report);          // warmup → discarded
        Assert.False(ps.DelayMapVote);     // never blocks the map vote
    }

    // ========================================================================================= campaign

    [Fact]
    public void Campaign_ParseCsvLine_HandlesQuotesAndEmpties()
    {
        var f = Campaign.ParseCsvLine("\"dm\",\"boil\",\"5\",\"2\",\"10\",,,\"Deathmatch\",\"desc\"");
        Assert.Equal("dm", f[0]);
        Assert.Equal("boil", f[1]);
        Assert.Equal("5", f[2]);
        Assert.Equal("", f[5]); // empty timelimit field
        Assert.Equal("", f[6]); // empty mutators field
    }

    [Fact]
    public void Campaign_Load_SkipsCommentsAndIndexesDataRows()
    {
        string file =
            "//campaign:Test Campaign\n" +
            "//\"game\",\"mapname\",...\n" +
            "\"dm\",\"boil\",\"5\",\"2\",\"10\",,,\"DM\",\"d\"\n" +
            "\"ctf\",\"dance\",\"9\",\"3\",\"3\",,,\"CTF\",\"d\"\n";
        var c = new Campaign { FileReader = _ => file };
        int n = c.Load(0, 2);
        Assert.Equal(2, n);
        Assert.Equal("Test Campaign", c.Title);
        Assert.Equal("dm", c.Entries[0].Gametype);
        Assert.Equal("boil", c.Entries[0].MapName);
        Assert.Equal(5f, c.Entries[0].Bots);

        // offset 1 → the second data row is entry[0]
        var c2 = new Campaign { FileReader = _ => file };
        c2.Load(1, 1);
        Assert.Equal("dance", c2.Entries[0].MapName);
    }

    [Fact]
    public void Campaign_PreInit_AppliesLevelCvars()
    {
        // columns: game, mapname, bots, skill, fraglimit, timelimit, mutators, desc, longdesc
        // here: dm/boil, 7 bots, skill 3, fraglimit 20, timelimit 15.
        string file = "//campaign:T\n\"dm\",\"boil\",\"7\",\"3\",\"20\",\"15\",,\"DM\",\"d\"\n";
        Cvars.Set("_campaign_index", "0");
        Cvars.Set("_campaign_name", "");
        Cvars.Set("g_campaign_skill", "1");
        Cvars.Set("sv_cheats", "0");
        var c = new Campaign { FileReader = _ => file };
        Assert.True(c.PreInit());
        Assert.Equal("dm", c.CurrentGametype);
        Assert.Equal("boil", c.CurrentMap);
        Assert.Equal(7f, Cvars.Float("bot_number"));
        Assert.Equal(4f, Cvars.Float("skill")); // g_campaign_skill(1) + level skill(3)
        c.PostInit();
        Assert.Equal(20f, Cvars.Float("fraglimit"));
        Assert.Equal(15f, Cvars.Float("timelimit"));
    }

    [Fact]
    public void Campaign_WinLose_RequiresSoleWinner()
    {
        string file = "//campaign:T\n\"dm\",\"boil\",\"5\",\"2\",\"10\",,,\"DM\",\"d\"\n";
        var c = new Campaign { FileReader = _ => file, CfgReader = () => "", CfgWriter = _ => { } };
        c.PreInit();
        var human = NewPlayer();
        // sole winner → won
        Assert.Equal(1, c.PreIntermission(new[] { human }, _ => true, false, 0, 1f));
        // not a winner → lost
        Assert.Equal(0, c.PreIntermission(new[] { human }, _ => false, false, 0, 1f));
    }

    [Fact]
    public void Campaign_ProgressSave_FiresOnProgressSavedHook()
    {
        // Winning the frontier level advances + persists g_campaign<id>_index; the OnProgressSaved hook is how an
        // in-process listen server mirrors that back to the shared menu cvar store so the campaign list unlocks.
        string file = "//campaign:T\n\"dm\",\"boil\",\"5\",\"2\",\"10\",,,\"DM\",\"d\"\n";
        Cvars.Set("_campaign_index", "0");
        Cvars.Set("_campaign_name", "test");
        Cvars.Set("sv_cheats", "0");
        var saved = new Dictionary<string, float>();
        var c = new Campaign
        {
            FileReader = _ => file, CfgReader = () => "", CfgWriter = _ => { },
            OnProgressSaved = (n, v) => saved[n] = v,
        };
        c.PreInit();
        Assert.Equal(1, c.PreIntermission(new[] { NewPlayer() }, _ => true, false, 0, 1f)); // sole winner @ frontier
        Assert.Equal(1f, saved["g_campaigntest_index"]); // frontier advanced 0 -> 1
        Assert.Equal(1f, saved["g_campaigntest_won"]);   // single-entry campaign also marks it won

        // A replay of an already-completed level (Level 0 while the frontier is 2) must NOT regress progress.
        saved.Clear();
        Cvars.Set("g_campaigntest_index", "2");
        c.PreIntermission(new[] { NewPlayer() }, _ => true, false, 0, 1f);
        Assert.Empty(saved); // Level(0) != frontier(2) → no save
    }

    // ============================================================================================ demo

    [Fact]
    public void Demo_ShouldRecordClient_Modes()
    {
        var p = NewPlayer();
        Assert.False(DemoControl.ShouldRecordClient(p, 0));
        Assert.True(DemoControl.ShouldRecordClient(p, 1));  // in-game player
        Assert.True(DemoControl.ShouldRecordClient(p, 2));  // all clients
        Assert.False(DemoControl.ShouldRecordClient(new Player { IsBot = true }, 2)); // bots never
    }

    [Fact]
    public void Demo_MatchStartStop()
    {
        Cvars.Set("sv_autodemo", "1");
        string? started = null; bool stopped = false;
        var demo = new DemoControl { StartRecording = n => started = n, StopRecording = () => stopped = true };
        demo.OnMatchStart("boil", "dm", System.Array.Empty<Player>());
        Assert.True(demo.Recording);
        Assert.NotNull(started);
        demo.OnMatchEnd();
        Assert.False(demo.Recording);
        Assert.True(stopped);
    }

    // ===================================================================================== map rotation

    [Fact]
    public void MapRotation_GetNextMap_Iterates()
    {
        Cvars.Set("g_maplist", "boil dance stormkeep");
        Cvars.Set("g_maplist_shuffle", "0");
        Cvars.Set("g_maplist_selectrandom", "0");
        Cvars.Set("g_maplist_mostrecent", "");
        var r = new MapRotation();
        r.Init("boil");
        string next = r.GetNextMap();
        Assert.Equal("dance", next);
    }

    [Fact]
    public void MapRotation_RecentMapsExcluded()
    {
        Cvars.Set("g_maplist", "boil dance");
        Cvars.Set("g_maplist_shuffle", "0");
        Cvars.Set("g_maplist_mostrecent_count", "2");
        var r = new MapRotation();
        r.MarkAsRecent("dance");
        Assert.True(MapRotation.IsRecent("dance"));
        r.Init("boil");
        // dance is recent → iterate skips it → repeats boil (pass-2 fallback) since it's the only non-recent
        string next = r.GetNextMap();
        Assert.NotEqual("dance", next);
    }

    [Fact]
    public void MapRotation_BuildBallot()
    {
        Cvars.Set("g_maplist", "boil dance stormkeep solarium");
        Cvars.Set("g_maplist_shuffle", "0");
        Cvars.Set("g_maplist_mostrecent", "");
        var r = new MapRotation();
        r.Init("boil");
        var ballot = r.BuildBallot(3);
        Assert.True(ballot.Count >= 2);
        Assert.Equal(ballot.Count, new HashSet<string>(ballot).Count); // distinct
    }

    // ========================================================================================== timeout

    [Fact]
    public void Timeout_LeadThenPauseThenResume()
    {
        Cvars.Set("sv_timeout", "1");
        Cvars.Set("sv_timeout_leadtime", "4");
        Cvars.Set("sv_timeout_length", "120");
        Cvars.Set("sv_timeout_number", "2");
        Cvars.Set("timelimit", "0"); // no "too late" guard

        float now = 0f;
        var t = new TimeoutController { Clock = () => now };
        var p = NewPlayer();
        t.ResetAllowance(p);
        Assert.True(t.CallTimeout(p, out _));
        Assert.Equal(TimeoutController.LeadTime, t.Status);
        Assert.Equal(1, t.AllowedOf(p)); // one used

        now = 5f; t.Think();             // past leadtime (4s) → paused
        Assert.True(t.IsPaused);

        Assert.True(t.CallTimein(p, out _)); // shorten to resumetime (3s)
        now = 10f; t.Think();                // resumetime elapsed → resumed
        Assert.False(t.Active);
    }

    // ====================================================================== GameWorld end-of-match flow

    [Fact]
    public void GameWorld_EndOfMatch_RotatesToNextMap()
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        string? changedTo = null;
        world.Boot("dm");
        // Boot publishes the world's own cvar store, so set the rotation cvars AFTER Boot.
        Cvars.Set("g_maplist", "boil dance");
        Cvars.Set("g_maplist_shuffle", "0");
        Cvars.Set("g_maplist_votable", "0"); // no vote → silent rotation
        Cvars.Set("g_maplist_mostrecent", "");
        Cvars.Set("sv_mapchange_delay", "0");
        world.Commands.ChangeLevelHandler = m => changedTo = m;
        world.Commands.AddBotHandler = (_, _) => true;

        // connect two bots so there is a roster, then force the match to end.
        world.Clients.ClientConnect(isBot: true, netName: "bot1");
        world.Clients.ClientConnect(isBot: true, netName: "bot2");
        world.EndMatch();

        // Advance enough frames for intermission to elapse + the map flow to apply. With no real client pressing
        // fire, QC IntermissionThink waits the full input-grace window (intermission_exittime + 10, intermission.qc:501)
        // before MapVote_Start; sv_mapchange_delay 0 makes exittime = now, so we need to clear ~10s of sim here.
        for (int i = 0; i < 150 && changedTo is null; i++)
            world.Frame(0.1f);

        Assert.Equal("dance", changedTo); // rotated from boil → dance
        Assert.Equal("dance", world.SelectedNextMap);
        Assert.True(MapRotation.IsRecent("dance"));
    }

    // ====================================================================== console chat / bot host sinks

    [Fact]
    public void CmdSay_WithChatHandler_BroadcastsWithoutLocalEcho()
    {
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");
        Player? sawCaller = null;
        string? sawMsg = null;
        world.Commands.ChatHandler = (caller, msg, _) => { sawCaller = caller; sawMsg = msg; };

        CommandContext ctx = world.Commands.Execute("say hello there", isServerConsole: false, caller: NewPlayer("alice"));

        Assert.Equal("alice", sawCaller?.NetName);  // the broadcast pipeline received it...
        Assert.Equal("hello there", sawMsg);
        Assert.Equal("", ctx.Output.Trim());        // ...and it was NOT also echoed locally (no double on a listen server)
    }

    [Fact]
    public void CmdSay_WithoutChatHandler_EchoesLocally()
    {
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");
        world.Commands.ChatHandler = null;          // no broadcast pipeline wired

        CommandContext ctx = world.Commands.Execute("say hi", isServerConsole: false, caller: NewPlayer("bob"));

        Assert.Contains("hi", ctx.Output);          // falls back to a local echo so `say` isn't silent
    }

    [Fact]
    public void BotConnect_AddsToRoster_AndDisconnectRemoves()
    {
        // The world-level mechanics NetGame's AddBot/RemoveBot host sinks drive: connect adds a (bot) client to
        // the roster (the per-tick snapshot loop then networks it), disconnect drops it.
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");
        Assert.Equal(0, world.Clients.BotCount);

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: true, netName: "[BOT] Sasha");
        Assert.Equal(1, world.Clients.BotCount);
        Assert.True(info.Player.IsBot);

        Assert.True(world.Clients.ClientDisconnect(info.Player));
        Assert.Equal(0, world.Clients.BotCount);
    }

    [Fact]
    public void BalanceCvarChange_RederivesWeaponBalance_OnNextTick()
    {
        // QC autocvars are live; the port caches each weapon's balance block in a struct (Weapon.Configure), so a
        // runtime `set g_balance_*` would otherwise never reach the live match. GameWorld watches its cvar store
        // and re-derives on the next tick (OnStartFrame), coalesced — this is the fix for "I changed the blaster
        // radius in the console and it had no effect".
        var world = new GameWorld(new CollisionWorld());
        world.Boot("dm");

        var blaster = (Blaster)Weapons.ByName("blaster")!;
        float original = blaster.Primary.Radius;

        // A `set` lands on the server's own cvar store (on a listen server the console→server bridge mirrors the
        // shared store into here). The store updates immediately, but the cached struct is stale until a tick.
        world.Commands.Execute("set g_balance_blaster_primary_radius 999", isServerConsole: true);
        Assert.Equal(original, blaster.Primary.Radius);   // coalesced — not applied mid-frame

        world.Frame(0.1f);                                // OnStartFrame flushes the dirty balance → ConfigureAll
        Assert.Equal(999f, blaster.Primary.Radius);

        // restore the GLOBAL weapon registry so later tests see stock balance (a fresh Boot would also reset it).
        world.Commands.Execute("set g_balance_blaster_primary_radius 60", isServerConsole: true);
        world.Frame(0.1f);
    }

    [Fact]
    public void Map_ImmediatelyRoutesToChangeLevelHandler()
    {
        // DP `map`: an immediate changelevel — CmdMap invokes the host's ChangeLevelHandler right away (NetGame
        // wires it to reboot the listen server on that map).
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("dm");
        string? changedTo = null;
        world.Commands.ChangeLevelHandler = m => changedTo = m;

        world.Commands.Execute("map dance", isServerConsole: true);

        Assert.Equal("dance", changedTo);
    }

    [Fact]
    public void GotoMap_QueuesThenRoutesToChangeLevelHandlerAtMatchEnd()
    {
        // DP `gotomap`: queue the map + end the match; after intermission the end-of-match flow routes the QUEUED
        // map (it wins over the rotation/vote) to ChangeLevelHandler — what NetGame reboots the server on.
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("dm");
        Cvars.Set("g_maplist_votable", "0"); // no end-of-match vote → direct apply
        Cvars.Set("sv_mapchange_delay", "0");
        string? changedTo = null;
        world.Commands.ChangeLevelHandler = m => changedTo = m;
        world.Clients.ClientConnect(isBot: true, netName: "bot1"); // a roster so the match flow runs

        world.Commands.Execute("gotomap dance", isServerConsole: true);
        // QC IntermissionThink holds for the input-grace window (intermission_exittime + 10, intermission.qc:501)
        // when no real client presses fire; sv_mapchange_delay 0 → exittime = now, so clear ~10s of sim here.
        for (int i = 0; i < 150 && changedTo is null; i++)
            world.Frame(0.1f);

        Assert.Equal("dance", changedTo);            // gotomap's queued map won and reached the changelevel pipeline
        Assert.Equal("dance", world.SelectedNextMap); // and is recorded as the chosen next map
    }

    [Fact]
    public void Restart_FromIntermission_ResetsTheServerCleanly()
    {
        // DP/QC `restart` (RestartMatch → ReadyRestart): leave intermission, clear scores, re-arm the pre-live
        // start countdown, and re-spawn every player + reset map objects — WITHOUT reloading the level (that is
        // what `map` does). This pins the full server-side reset the listen-server host triggers from its console
        // and a passed `vote call restart`, so a regression in any of those steps fails here rather than in a
        // playtest. (The listen-server CLIENT then resets its own prediction off the respawn-teleport snap; the
        // `map` full reload is covered by Map_*RoutesToChangeLevelHandler above.)
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("dm");
        world.Commands.AddBotHandler = (_, _) => true;

        ClientManager.ClientInfo a = world.Clients.ClientConnect(isBot: true, netName: "bot1");
        world.Clients.ClientConnect(isBot: true, netName: "bot2");

        // Dirty the match: register + score a player, give them speed, then drive the match to intermission.
        world.Scores.Row(a.Player);                 // ensure the row exists so Score_ClearAll touches it
        a.Player.ScoreFrags = 7;
        a.Player.Velocity = new Vector3(320f, 0f, 0f);
        world.EndMatch();
        Assert.True(world.Intermission.Running, "precondition: EndMatch latches intermission (the state restart undoes)");

        // The exact path the host's in-game console runs (isServerConsole: true → bypasses the client gate).
        CommandContext ctx = world.Commands.Execute("restart", isServerConsole: true);
        Assert.Contains("restart", ctx.Output);

        Assert.False(world.Intermission.Running, "restart must leave intermission");
        Assert.False(world.Warmup.WarmupStage, "restart forces warmup to end (forceWarmupEnd)");
        Assert.True(world.Warmup.CountdownRunning, "restart must arm the pre-live start countdown");
        Assert.Equal(0, a.Player.ScoreFrags);                  // QC Score_ClearAll on a real restart
        Assert.False(a.Player.IsDead);                         // every player re-spawned alive (PutClientInServer)
        Assert.Equal(Vector3.Zero, a.Player.Velocity);         // reset_map zeroes velocity for the fresh start
    }

    [Fact]
    public void Map_SameMap_ReloadsTheCurrentLevel()
    {
        // DP `map <currentmap>` is the faithful way to RELOAD the level (full changelevel: server reboots on the
        // same map, the client tears down + reconnects). It must route to ChangeLevelHandler exactly like a
        // change to a different map — i.e. reloading the level is not special-cased away.
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("dm");
        string? changedTo = null;
        world.Commands.ChangeLevelHandler = m => changedTo = m;

        world.Commands.Execute("map boil", isServerConsole: true);

        Assert.Equal("boil", changedTo); // a reload of the current level reaches the same reboot pipeline
    }
}
