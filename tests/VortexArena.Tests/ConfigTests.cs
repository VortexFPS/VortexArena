using System.Collections.Generic;
using System.IO;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Unit tests for the Darkplaces-faithful <see cref="ConfigInterpreter"/> — the cfg/alias/exec grammar in
/// isolation (no real data, a standalone cvar store). Covers tokenizing, comments, <c>set</c>/<c>seta</c>,
/// bare assignment + the command denylist, <c>exec</c> recursion/cycles, alias define/invoke, and
/// <c>$cvar</c>/<c>$arg</c>/<c>$$</c> expansion.
/// </summary>
public class ConfigInterpreterTests
{
    private static ConfigInterpreter New(CvarService cvars, IDictionary<string, string>? files = null)
        => new(cvars, path => files != null && files.TryGetValue(path, out string? t) ? t : null);

    [Fact]
    public void Set_And_Seta_Assign_With_Optional_Description()
    {
        var cvars = new CvarService();
        var interp = New(cvars);
        interp.ExecuteScript("set sv_gravity 800 \"world gravity\"\nseta hostname \"My Server\"");

        Assert.Equal(800f, cvars.GetFloat("sv_gravity"));
        Assert.Equal("My Server", cvars.GetString("hostname")); // quoted value kept whole, description dropped
        Assert.Equal(2, interp.CvarsAssigned);
    }

    [Fact]
    public void LineComments_BlockComments_And_TrailingComments_Are_Stripped()
    {
        var cvars = new CvarService();
        New(cvars).ExecuteScript(
            "// a leading comment line\n" +
            "set a 1 // trailing comment\n" +
            "/* block\n comment */ set b 2\n" +
            "set c 3");

        Assert.Equal(1f, cvars.GetFloat("a"));
        Assert.Equal(2f, cvars.GetFloat("b"));
        Assert.Equal(3f, cvars.GetFloat("c"));
    }

    [Fact]
    public void Semicolons_Separate_Commands_But_Not_Inside_Quotes()
    {
        var cvars = new CvarService();
        New(cvars).ExecuteScript("set a 1; set b 2; set msg \"one; two; three\"");

        Assert.Equal(1f, cvars.GetFloat("a"));
        Assert.Equal(2f, cvars.GetFloat("b"));
        Assert.Equal("one; two; three", cvars.GetString("msg")); // the ; inside quotes is data, not a separator
    }

    [Fact]
    public void EscapedQuotes_In_Description_Do_Not_Break_Tokenizing()
    {
        var cvars = new CvarService();
        // The real configs are full of descriptions like: ... "\"1\" = qu/s, \"2\" = m/s"
        New(cvars).ExecuteScript("seta hud_speed_unit \"1\" \"speed unit; \\\"1\\\" = qu/s, \\\"2\\\" = m/s\"");
        Assert.Equal("1", cvars.GetString("hud_speed_unit"));
    }

    [Fact]
    public void BareAssignment_Sets_Cvars_But_Skips_Known_Commands()
    {
        var cvars = new CvarService();
        var interp = New(cvars);
        // physicsX.cfg sets sv_* with bare assignment; binds are commands, not cvars.
        interp.ExecuteScript("sv_gravity 800\nsv_maxspeed 360\nbind w +forward\nunbindall");

        Assert.Equal(800f, cvars.GetFloat("sv_gravity"));
        Assert.Equal(360f, cvars.GetFloat("sv_maxspeed"));
        Assert.Equal("", cvars.GetString("bind"));   // 'bind' must NOT become a cvar
        Assert.Equal("", cvars.GetString("w"));
    }

    [Fact]
    public void Exec_Includes_Recurse_And_Cycles_Are_Guarded()
    {
        var cvars = new CvarService();
        var files = new Dictionary<string, string>
        {
            ["root.cfg"] = "set fromroot 1\nexec child.cfg",
            ["child.cfg"] = "set fromchild 2\nexec root.cfg", // cycle back — must be guarded, not stack-overflow
        };
        var interp = New(cvars, files);
        interp.ExecuteFile("root.cfg");

        Assert.Equal(1f, cvars.GetFloat("fromroot"));
        Assert.Equal(2f, cvars.GetFloat("fromchild"));
        Assert.Equal(2, interp.FilesExecuted);              // root + child, each once
        Assert.Equal(0, interp.FilesMissing);
    }

    [Fact]
    public void Exec_Missing_File_Is_Recorded_Not_Fatal()
    {
        var cvars = new CvarService();
        var interp = New(cvars, new Dictionary<string, string>());
        Assert.False(interp.ExecuteFile("nope.cfg"));
        Assert.Equal(1, interp.FilesMissing);
    }

    [Fact]
    public void CvarReference_Expands_With_Braces_And_Bareword()
    {
        var cvars = new CvarService();
        var interp = New(cvars);
        interp.ExecuteScript("set base 5\nset a $base\nset b ${base}");

        Assert.Equal("5", cvars.GetString("a"));
        Assert.Equal("5", cvars.GetString("b"));
    }

    [Fact]
    public void Alias_Body_Is_Stored_Raw_And_DollarDollar_Defers()
    {
        var cvars = new CvarService();
        var interp = New(cvars);
        // $$* must NOT expand at definition; cvar refs are deferred until the alias runs.
        interp.ExecuteScript("set x 9\nalias show \"set captured $$*\"");
        Assert.Equal("set captured $*", interp.Aliases["show"]); // $$ -> $, body kept otherwise raw
    }

    [Fact]
    public void Alias_Invocation_Substitutes_Positional_And_Star_Args()
    {
        var cvars = new CvarService();
        var interp = New(cvars);
        // The range value is quoted so the two spliced words stay one cvar value (unquoted, `set` would take
        // only the first token as the value and the rest as the description — faithful DP behavior).
        interp.ExecuteScript("alias setboth \"set first $1; set rest \\\"${2-}\\\"\"");
        interp.ExecuteLine("setboth alpha beta gamma");

        Assert.Equal("alpha", cvars.GetString("first"));
        Assert.Equal("beta gamma", cvars.GetString("rest"));
    }

    [Fact]
    public void Passthrough_Alias_Runs_Its_Arguments_AsIs()
    {
        var cvars = new CvarService();
        var interp = New(cvars);
        // The if_client/if_dedicated mechanism: alias body "${* asis}" splices and runs the call's arguments.
        interp.DefineAlias("if_dedicated", "${* asis}");
        interp.ExecuteLine("if_dedicated set onlyonserver 1");
        Assert.Equal(1f, cvars.GetFloat("onlyonserver"));
    }

    [Fact]
    public void Later_Set_Overrides_Earlier_The_Way_Balance_Variants_Do()
    {
        var cvars = new CvarService();
        New(cvars).ExecuteScript("set g_balance_blaster_primary_damage 20\nset g_balance_blaster_primary_damage 25");
        Assert.Equal(25f, cvars.GetFloat("g_balance_blaster_primary_damage")); // overkill-style override wins
    }

    [Fact]
    public void EmptyQuoted_Value_Is_A_Real_Empty_Assignment()
    {
        var cvars = new CvarService();
        New(cvars).ExecuteScript("set g_random_start_weapons \"\"");
        Assert.Equal("", cvars.GetString("g_random_start_weapons"));
    }

    [Fact]
    public void Config_Overrides_PreRegistered_Defaults_The_Way_GameWorld_Boots()
    {
        // GameWorld.Boot does Cvars.RegisterDefaults() (idempotent Register) THEN loads the real cfg tree
        // (Set). A pre-registered baseline must lose to the config — e.g. a host/mod pre-registers a stale
        // sv_maxairspeed=30, but stock Xonotic (physicsX.cfg) is 360, so the load corrects it. (The shipped
        // Cvars.Defaults no longer pre-registers the movement tunables — they had drifted wrong — so their
        // single source of truth is MovementParameters.FromCvars; this test simulates the stale baseline.)
        var cvars = new CvarService();
        cvars.Register("sv_maxairspeed", "30", CvarFlags.Notify); // a stale hand-curated baseline (simulated)
        New(cvars).ExecuteScript("sv_maxairspeed 360");           // the real config (bare assignment)
        Assert.Equal(360f, cvars.GetFloat("sv_maxairspeed"));
    }
}

/// <summary>
/// Integration tests that load the <strong>real</strong> Xonotic <c>.cfg</c> tree from the reference checkout
/// and assert authentic balance/physics/gametype values land in the cvar store. CI-portable: silently no-op
/// when the checkout isn't present (mirrors <see cref="AssetParserTests"/>).
/// </summary>
public class ConfigRealDataTests
{
    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";

    /// <summary>A file-reader that resolves a config path relative to the pk3dir root (DP gamedir search).</summary>
    private static Func<string, string?> DiskReader => path =>
    {
        string full = Path.Combine(Pk3Dir, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    };

    private static bool HaveData => File.Exists(Path.Combine(Pk3Dir, "balance-xonotic.cfg"));

    [Fact]
    public void Balance_Config_Loads_Authentic_Weapon_And_Starting_Values()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        // balance-xonotic.cfg execs bal-wep-xonotic.cfg, so one entry pulls the whole default balance.
        var interp = ConfigLoader.Load(cvars, DiskReader, "balance-xonotic.cfg");

        // weapon balance (from bal-wep-xonotic.cfg, reached via the nested exec)
        Assert.Equal(20f, cvars.GetFloat("g_balance_blaster_primary_damage"));
        Assert.Equal(80f, cvars.GetFloat("g_balance_vortex_primary_damage"));
        Assert.Equal(55f, cvars.GetFloat("g_balance_mortar_primary_damage"));
        Assert.Equal(80f, cvars.GetFloat("g_balance_devastator_damage"));
        Assert.Equal(10f, cvars.GetFloat("g_balance_machinegun_sustained_damage"));

        // starting gear (from balance-xonotic.cfg itself)
        Assert.Equal(100f, cvars.GetFloat("g_balance_health_start"));
        Assert.Equal(15f, cvars.GetFloat("g_start_ammo_shells"));
        Assert.Equal("0", cvars.GetString("g_balance_armor_start")); // a genuine "0", distinct from unset

        Assert.True(interp.CvarsAssigned > 800, $"expected the full balance table, got {interp.CvarsAssigned}");
        Assert.True(interp.FilesExecuted >= 2, "balance-xonotic.cfg should have exec'd bal-wep-xonotic.cfg");
    }

    [Fact]
    public void PhysicsX_Config_Loads_Authentic_Movement_Cvars()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        // physicsX.cfg uses bare assignment exclusively (sv_gravity 800, ...).
        ConfigLoader.Load(cvars, DiskReader, "physicsX.cfg");

        Assert.Equal(800f, cvars.GetFloat("sv_gravity"));
        Assert.Equal(360f, cvars.GetFloat("sv_maxspeed"));
        Assert.Equal(260f, cvars.GetFloat("sv_jumpvelocity"));
        Assert.Equal(2f, cvars.GetFloat("sv_airaccelerate"));
        Assert.Equal("Xonotic", cvars.GetString("g_mod_physics"));
    }

    [Fact]
    public void Full_Server_Config_Chain_Executes_Every_Subsystem()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        var interp = ConfigLoader.LoadServerConfig(cvars, DiskReader);

        // one cvar from each deep config proves the whole exec chain ran
        Assert.Equal(1f, cvars.GetFloat("g_turrets"));                              // turrets.cfg
        Assert.Equal(1f, cvars.GetFloat("g_vehicles"));                            // vehicles.cfg
        Assert.Equal(60f, cvars.GetFloat("g_monster_zombie_attack_leap_damage")); // monsters.cfg
        Assert.Equal("dm", cvars.GetString("gamecfg"));                            // gametypes-server.cfg
        Assert.Equal(1f, cvars.GetFloat("sv_minigames"));                         // minigames.cfg
        Assert.Equal(800f, cvars.GetFloat("sv_gravity"));                          // physicsX.cfg (bare)
        Assert.Equal(20f, cvars.GetFloat("g_balance_blaster_primary_damage"));     // balance chain

        Assert.True(interp.CvarsAssigned > 3000, $"expected thousands of cvars, got {interp.CvarsAssigned}");
        Assert.True(interp.FilesExecuted >= 10, $"expected the full include chain, got {interp.FilesExecuted} files");
    }

    [Fact]
    public void Loaded_Movement_Cvars_Flow_Through_MovementParameters()
    {
        if (!HaveData) return;
        var facade = new EngineServices(new CollisionWorld());
        Api.Services = facade;

        var interp = ConfigLoader.Load(facade.Cvars, DiskReader, "physicsX.cfg");
        MovementParameters mp = MovementParameters.FromCvars();
        Assert.Equal(800f, mp.Gravity);
        Assert.Equal(360f, mp.MaxSpeed);
        Assert.Equal(260f, mp.JumpVelocity);

        // Prove FromCvars reads the live cvar (not just its matching default): override and re-read.
        interp.ExecuteLine("set sv_maxspeed 999");
        Assert.Equal(999f, MovementParameters.FromCvars().MaxSpeed);
    }
}
