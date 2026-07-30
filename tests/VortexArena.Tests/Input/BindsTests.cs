using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XonoticGodot.Common.Config;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests.Input;

/// <summary>
/// Tests for the keybind pipeline T15 wires up: the Godot-free runtime bind table
/// (<see cref="BindTable"/> — DP <c>bind</c>/<c>unbind</c> + the cl_input.c <c>+</c>/<c>-</c> button model) and
/// the ingestion of the canonical <c>binds-xonotic.cfg</c> through a <c>bind</c> sink registered on the
/// <see cref="ConfigInterpreter"/> (the linchpin: <c>exec binds-xonotic.cfg</c> → BindTable). The Godot input
/// glue (<c>Game.Console.BindInput</c> event encode + engine-key-name translation) needs Godot and is verified
/// in the windowed client; here we exercise the headless core + a faithful stand-in sink for the cfg ingestion.
///
/// <para><see cref="BindTable"/> is a process-global static, so every test resets it first (the suite already
/// disables xUnit parallelism in TestParallelization.cs, but the explicit reset keeps each test order-free).</para>
/// </summary>
public class BindsTests
{
    public BindsTests() => BindTable.Reset();

    // =====================================================================================================
    //  BindTable: the bind map (DP bind/unbind/unbindall) + the +/- button system (cl_input.c kbutton)
    // =====================================================================================================

    [Fact]
    public void Bind_Get_Unbind_Roundtrip()
    {
        BindTable.Bind("X", "+forward");
        Assert.Equal("+forward", BindTable.Get("X"));
        Assert.True(BindTable.Unbind("X"));
        Assert.Equal("", BindTable.Get("X"));
        Assert.False(BindTable.Unbind("X")); // already gone
    }

    [Fact]
    public void Bind_Is_CaseInsensitive_Matching_The_Encoder_Casing()
    {
        BindTable.Bind("w", "+forward");          // a typed/cfg lowercase name
        Assert.Equal("+forward", BindTable.Get("W")); // resolves the "W" a live event encodes to
    }

    [Fact]
    public void UnbindAll_Clears_The_Table_But_Not_Held_State()
    {
        BindTable.Bind("W", "+forward");
        BindTable.HandleBind("W", pressed: true, _ => { });
        BindTable.UnbindAll();
        Assert.Empty(BindTable.List());
        // UnbindAll leaves the held button alone (DP unbindall doesn't release); Reset() is the full clear.
        Assert.Equal(1f, BindTable.Forward);
    }

    [Fact]
    public void PlusForward_Sets_And_Clears_The_Forward_Axis()
    {
        BindTable.Bind("W", "+forward");
        BindTable.HandleBind("W", true, _ => { });
        Assert.Equal(1f, BindTable.Forward);
        BindTable.HandleBind("W", false, _ => { });
        Assert.Equal(0f, BindTable.Forward);
    }

    [Fact]
    public void Opposed_Strafe_Cancels_To_Zero()
    {
        BindTable.Bind("A", "+moveleft");
        BindTable.Bind("D", "+moveright");
        BindTable.HandleBind("A", true, _ => { });
        Assert.Equal(-1f, BindTable.Side);
        BindTable.HandleBind("D", true, _ => { });
        Assert.Equal(0f, BindTable.Side); // both held → net zero (the kbutton sum)
    }

    [Fact]
    public void Jump_And_Crouch_Drive_The_Up_Axis_And_Their_Buttons()
    {
        BindTable.Bind("SPACE", "+jump");
        BindTable.Bind("CTRL", "+crouch");
        BindTable.HandleBind("SPACE", true, _ => { });
        Assert.Equal(1f, BindTable.Up);
        Assert.True(BindTable.JumpHeld);
        BindTable.HandleBind("CTRL", true, _ => { });
        Assert.Equal(0f, BindTable.Up); // jump + crouch cancel on the up axis
        Assert.True(BindTable.CrouchHeld);
    }

    [Fact]
    public void Fire_Aliases_Map_To_Attack_Buttons()
    {
        // binds-xonotic.cfg uses +fire/+fire2 (not +attack/+attack2); BindTable.SetButton accepts both spellings.
        BindTable.Bind("MOUSE1", "+fire");
        BindTable.Bind("MOUSE2", "+fire2");
        BindTable.HandleBind("MOUSE1", true, _ => { });
        BindTable.HandleBind("MOUSE2", true, _ => { });
        Assert.True(BindTable.AttackHeld);
        Assert.True(BindTable.Attack2Held);
        BindTable.HandleBind("MOUSE1", false, _ => { });
        Assert.False(BindTable.AttackHeld);
    }

    [Fact]
    public void Zoom_And_Use_Held_Buttons()
    {
        BindTable.Bind("MOUSE3", "+zoom");
        BindTable.Bind("F", "+use");
        BindTable.HandleBind("MOUSE3", true, _ => { });
        BindTable.HandleBind("F", true, _ => { });
        Assert.True(BindTable.ZoomHeld);
        Assert.True(BindTable.UseHeld);
    }

    [Fact]
    public void Togglezoom_PressEdge_Toggles_ZoomHeld()
    {
        // The STOCK zoom bind is `bind MOUSE3 togglezoom` (binds-xonotic.cfg), a one-shot — not +zoom. It must flip
        // the held-zoom latch on each press edge (QC ${_togglezoom}zoom -> +button4) so the stock bind drives zoom.
        BindTable.Bind("MOUSE3", "togglezoom");
        Assert.False(BindTable.ZoomHeld);
        BindTable.HandleBind("MOUSE3", true, _ => { });   // press → zoom on
        Assert.True(BindTable.ZoomHeld);
        BindTable.HandleBind("MOUSE3", false, _ => { });  // release → unchanged (it's a toggle, not a hold)
        Assert.True(BindTable.ZoomHeld);
        BindTable.HandleBind("MOUSE3", true, _ => { });   // next press → zoom off
        Assert.False(BindTable.ZoomHeld);
    }

    [Fact]
    public void ShowScores_Held_Drives_The_Scoreboard_Flag()
    {
        BindTable.Bind("TAB", "+showscores");
        BindTable.HandleBind("TAB", true, _ => { });
        Assert.True(BindTable.ShowScores);
        BindTable.HandleBind("TAB", false, _ => { });
        Assert.False(BindTable.ShowScores);
    }

    [Fact]
    public void OneShot_Bind_Runs_On_Press_Only()
    {
        BindTable.Bind("K", "kill");
        int runs = 0;
        string? cmd = null;
        BindTable.HandleBind("K", true, c => { runs++; cmd = c; });
        BindTable.HandleBind("K", false, _ => runs++); // release must not re-run a one-shot
        Assert.Equal(1, runs);
        Assert.Equal("kill", cmd);
    }

    [Fact]
    public void WeaponSelect_Binds_Are_OneShots_Forwarded_Verbatim()
    {
        // weapnext / weapon_group_3 are NOT +commands → they run as one-shot commands (the consumer routes them
        // to the weapon-selection API / the server). The exact command string must reach the runner unchanged.
        BindTable.Bind("MWHEELUP", "weapnext");
        BindTable.Bind("3", "weapon_group_3");
        var ran = new List<string>();
        BindTable.HandleBind("MWHEELUP", true, ran.Add);
        BindTable.HandleBind("3", true, ran.Add);
        Assert.Equal(new[] { "weapnext", "weapon_group_3" }, ran);
    }

    [Fact]
    public void ReleaseAll_Clears_Every_Held_Button()
    {
        BindTable.Bind("W", "+forward");
        BindTable.Bind("TAB", "+showscores");
        BindTable.HandleBind("W", true, _ => { });
        BindTable.HandleBind("TAB", true, _ => { });
        BindTable.ReleaseAll();
        Assert.Equal(0f, BindTable.Forward);
        Assert.False(BindTable.ShowScores);
    }

    [Fact]
    public void Unbound_Key_Event_Is_Ignored()
    {
        bool ran = false;
        BindTable.HandleBind("Z", true, _ => ran = true); // nothing bound to Z
        Assert.False(ran);
        Assert.Equal(0f, BindTable.Forward);
    }

    [Fact]
    public void List_Is_Ordered_By_Key()
    {
        BindTable.Bind("W", "+forward");
        BindTable.Bind("A", "+moveleft");
        BindTable.Bind("S", "+back");
        List<string> keys = BindTable.List().Select(kv => kv.Key).ToList();
        Assert.Equal(new[] { "A", "S", "W" }, keys);
    }

    // =====================================================================================================
    //  binds-xonotic.cfg ingestion: register a `bind` sink on the interpreter, exec the cfg, assert the table
    //  (the linchpin path — MenuState.Boot does exactly this with BindInput's sink at boot).
    // =====================================================================================================

    /// <summary>
    /// A faithful stand-in for the headless half of the client's <c>bind</c> sink (BindInput.RegisterBindCommands):
    /// it stores each cfg <c>bind</c>/<c>unbind</c>/<c>unbindall</c> into <see cref="BindTable"/>, translating the
    /// engine key names this test needs into the canonical strings a live event encodes to (letters upper-cased,
    /// mouse/wheel/digit names pass through, the few named keys the movement binds use mapped explicitly). The
    /// real sink uses Godot's <c>OS.GetKeycodeString</c> for every named key; that spelling is an implementation
    /// detail not under test here — the behaviour under test is that the cfg's commands route into the button model.
    /// </summary>
    private static void RegisterTestBindSink(ConfigInterpreter interp)
    {
        interp.RegisterCommand("bind", argv =>
        {
            if (argv.Count < 2) return;
            string? key = TranslateKey(argv[1]);
            if (key is null) return;
            BindTable.Bind(key, argv.Count >= 3 ? string.Join(' ', argv.Skip(2)) : "");
        });
        interp.RegisterCommand("unbind", argv =>
        {
            if (argv.Count < 2) return;
            string? key = TranslateKey(argv[1]);
            if (key is not null) BindTable.Unbind(key);
        });
        interp.RegisterCommand("unbindall", _ => BindTable.UnbindAll());
    }

    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPACE"] = "Space", ["SHIFT"] = "Shift", ["CTRL"] = "Ctrl", ["TAB"] = "Tab",
        ["ENTER"] = "Enter", ["ESCAPE"] = "Escape", ["BACKSPACE"] = "Backspace",
    };

    private static string? TranslateKey(string n)
    {
        n = n.Trim();
        switch (n.ToUpperInvariant())
        {
            case "MOUSE1": case "MOUSE2": case "MOUSE3": case "MOUSE4": case "MOUSE5":
            case "MWHEELUP": case "MWHEELDOWN":
                return n.ToUpperInvariant();
        }
        if (n.Length == 1)
            return char.IsLetter(n[0]) ? char.ToUpperInvariant(n[0]).ToString() : n;
        if (NamedKeys.TryGetValue(n, out string? godot))
            return godot;
        return null; // arrows/keypad/JOY etc. — not needed for the gameplay-critical assertions below
    }

    // The real reference checkout (mirrors ConfigRealDataTests): CI-portable — no-op when absent.
    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";
    private static bool HaveData => File.Exists(Path.Combine(Pk3Dir, "binds-xonotic.cfg"));
    private static Func<string, string?> DiskReader => path =>
    {
        string full = Path.Combine(Pk3Dir, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    };

    [Fact]
    public void BindsXonoticCfg_Populates_The_Movement_Binds()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, DiskReader);
        RegisterTestBindSink(interp);
        interp.ExecuteFile("binds-xonotic.cfg");

        // The WASD movement block (binds-xonotic.cfg:2-11) landed with the right +commands.
        Assert.Equal("+forward", BindTable.Get("W"));
        Assert.Equal("+moveleft", BindTable.Get("A"));
        Assert.Equal("+back", BindTable.Get("S"));
        Assert.Equal("+moveright", BindTable.Get("D"));
        Assert.Equal("+jump", BindTable.Get("Space"));
        Assert.Equal("+crouch", BindTable.Get("Shift"));
    }

    [Fact]
    public void BindsXonoticCfg_Movement_Binds_Drive_The_Sampled_Axes()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, DiskReader);
        RegisterTestBindSink(interp);
        interp.ExecuteFile("binds-xonotic.cfg");

        // Pressing the cfg-bound keys produces the expected kbutton axes (the gameplay sampler reads these).
        BindTable.HandleBind("W", true, _ => { });
        BindTable.HandleBind("D", true, _ => { });
        BindTable.HandleBind("Space", true, _ => { });
        Assert.Equal(1f, BindTable.Forward);
        Assert.Equal(1f, BindTable.Side);
        Assert.True(BindTable.JumpHeld);
    }

    [Fact]
    public void BindsXonoticCfg_Populates_Fire_And_Weapon_Binds()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, DiskReader);
        RegisterTestBindSink(interp);
        interp.ExecuteFile("binds-xonotic.cfg");

        // Attack (MOUSE1/MOUSE2 +fire/+fire2) and weapon groups (digit keys → weapon_group_N), the cfg defaults.
        Assert.Equal("+fire", BindTable.Get("MOUSE1"));
        Assert.Equal("+fire2", BindTable.Get("MOUSE2"));
        Assert.Equal("weapon_group_1", BindTable.Get("1"));
        Assert.Equal("weapon_group_5", BindTable.Get("5"));
        Assert.Equal("weapnext", BindTable.Get("MWHEELUP"));
        Assert.Equal("weapprev", BindTable.Get("MWHEELDOWN"));

        // And +fire actually drives the attack button through the kbutton model.
        BindTable.HandleBind("MOUSE1", true, _ => { });
        Assert.True(BindTable.AttackHeld);
    }

    [Fact]
    public void BindSink_Wins_Over_Cvar_Fallback_So_Bind_Never_Becomes_A_Cvar()
    {
        // `bind` is on the interpreter's NonCvarCommands denylist AND the registered sink is consulted first, so
        // executing a bind line populates the table and never writes a junk `bind`/key cvar (the exact failure the
        // linchpin guards against: without the sink, binds were dropped/mis-handled).
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        RegisterTestBindSink(interp);
        interp.ExecuteLine("bind w \"+forward\"");
        Assert.Equal("+forward", BindTable.Get("W"));
        Assert.Equal("", cvars.GetString("bind"));
        Assert.Equal("", cvars.GetString("w"));
    }

    [Fact]
    public void Reset_All_Mirror_UnbindAll_Then_ReExec_Reloads_From_The_Cfg()
    {
        if (!HaveData) return;
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, DiskReader);
        RegisterTestBindSink(interp);
        interp.ExecuteFile("binds-xonotic.cfg");

        // A user rebind, then the menu "Reset all" (keybinder.qc KeyBinder_Bind_Reset_All): unbindall; exec cfg.
        BindTable.Bind("W", "kill");
        Assert.Equal("kill", BindTable.Get("W"));
        interp.ExecuteLine("unbindall");
        interp.ExecuteLine("exec binds-xonotic.cfg");
        Assert.Equal("+forward", BindTable.Get("W")); // back to the canonical default
    }
}
