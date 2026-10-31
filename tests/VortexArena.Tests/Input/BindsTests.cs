using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests.Input;

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
    //  Two keys on one +command (playtest #87): DP KeyDown/KeyUp (cl_input.c:65-122) track WHICH keys hold a
    //  button, so it stays down until every one of them is released.
    // =====================================================================================================

    [Fact]
    public void Two_Keys_On_Jump_Releasing_One_Keeps_Jumping()
    {
        BindTable.Bind("Space", "+jump");
        BindTable.Bind("MOUSE5", "+jump");
        BindTable.HandleBind("Space", true, _ => { });
        BindTable.HandleBind("MOUSE5", true, _ => { });
        BindTable.HandleBind("Space", false, _ => { });
        Assert.True(BindTable.JumpHeld);      // MOUSE5 still holds it
        Assert.Equal(1f, BindTable.Up);
        BindTable.HandleBind("MOUSE5", false, _ => { });
        Assert.False(BindTable.JumpHeld);
    }

    [Theory]
    [InlineData("+forward")]
    [InlineData("+back")]
    [InlineData("+moveleft")]
    [InlineData("+moveright")]
    [InlineData("+crouch")]
    [InlineData("+fire")]
    [InlineData("+fire2")]
    [InlineData("+zoom")]
    [InlineData("+use")]
    [InlineData("+hook")]
    [InlineData("+showscores")]
    public void Every_Held_Button_Stays_Down_Until_Its_Last_Key_Is_Up(string command)
    {
        BindTable.Bind("A", command);
        BindTable.Bind("B", command);
        BindTable.HandleBind("A", true, _ => { });
        BindTable.HandleBind("B", true, _ => { });
        BindTable.HandleBind("B", false, _ => { });
        Assert.True(IsHeld(command));
        BindTable.HandleBind("A", false, _ => { });
        Assert.False(IsHeld(command));
    }

    private static bool IsHeld(string command) => command switch
    {
        "+forward" => BindTable.Forward > 0,
        "+back" => BindTable.Forward < 0,
        "+moveleft" => BindTable.Side < 0,
        "+moveright" => BindTable.Side > 0,
        "+crouch" => BindTable.CrouchHeld,
        "+fire" => BindTable.AttackHeld,
        "+fire2" => BindTable.Attack2Held,
        "+zoom" => BindTable.ZoomHeld,
        "+use" => BindTable.UseHeld,
        "+hook" => BindTable.HookHeld,
        "+showscores" => BindTable.ShowScores,
        _ => throw new ArgumentException(command),
    };

    [Fact]
    public void Three_Keys_All_Hold_The_Button()
    {
        // DP's down[2] refuses a third key ("Three keys down for a button!"); the port keeps any number.
        foreach (string k in new[] { "A", "B", "C" })
        {
            BindTable.Bind(k, "+jump");
            BindTable.HandleBind(k, true, _ => { });
        }
        BindTable.HandleBind("A", false, _ => { });
        BindTable.HandleBind("B", false, _ => { });
        Assert.True(BindTable.JumpHeld);
        BindTable.HandleBind("C", false, _ => { });
        Assert.False(BindTable.JumpHeld);
    }

    [Fact]
    public void Repeated_Press_Of_The_Same_Key_Needs_Only_One_Release()
    {
        BindTable.Bind("Space", "+jump");
        BindTable.HandleBind("Space", true, _ => { });
        BindTable.HandleBind("Space", true, _ => { }); // DP "repeating key"
        BindTable.HandleBind("Space", false, _ => { });
        Assert.False(BindTable.JumpHeld);
    }

    [Fact]
    public void Release_Of_A_Key_That_Never_Pressed_The_Button_Is_Ignored()
    {
        // DP "key up without corresponding down (menu pass through)": e.g. the press went to the menu.
        BindTable.Bind("Space", "+jump");
        BindTable.Bind("MOUSE5", "+jump");
        BindTable.HandleBind("Space", true, _ => { });
        BindTable.HandleBind("MOUSE5", false, _ => { });
        Assert.True(BindTable.JumpHeld);
    }

    [Fact]
    public void Typed_Minus_Command_Clears_Every_Key_Holding_The_Button()
    {
        BindTable.Bind("Space", "+jump");
        BindTable.Bind("MOUSE5", "+jump");
        BindTable.HandleBind("Space", true, _ => { });
        BindTable.HandleBind("MOUSE5", true, _ => { });
        Assert.True(BindTable.ButtonCommand("-jump", key: null)); // DP KeyUp with no key: "so clear all"
        Assert.False(BindTable.JumpHeld);
        // a later release of either key is a stray and leaves it up
        BindTable.HandleBind("Space", false, _ => { });
        Assert.False(BindTable.JumpHeld);
    }

    [Fact]
    public void Typed_Plus_Command_Holds_Like_One_More_Key()
    {
        BindTable.Bind("Space", "+jump");
        Assert.True(BindTable.ButtonCommand("+jump", key: null));
        BindTable.HandleBind("Space", true, _ => { });
        BindTable.HandleBind("Space", false, _ => { });
        Assert.True(BindTable.JumpHeld);                 // the typed press still holds it
        Assert.True(BindTable.ButtonCommand("-jump", key: null));
        Assert.False(BindTable.JumpHeld);
    }

    [Fact]
    public void ButtonCommand_Refuses_What_Is_Not_A_Held_Button()
    {
        Assert.False(BindTable.ButtonCommand("+hud_panel_radar_maximized", key: null));
        Assert.False(BindTable.ButtonCommand("kill", key: null));
        Assert.False(BindTable.ButtonCommand("", key: null));
    }

    [Fact]
    public void Key_Bound_To_Minus_Command_Releases_On_Press_Only()
    {
        // DP only appends the key to a '+' bind; a key bound to "-jump" runs it bare on press (clear all).
        BindTable.Bind("Space", "+jump");
        BindTable.Bind("J", "-jump");
        BindTable.HandleBind("Space", true, _ => { });
        BindTable.HandleBind("J", true, _ => { });
        Assert.False(BindTable.JumpHeld);
        BindTable.HandleBind("J", false, _ => { });
        Assert.False(BindTable.JumpHeld);
    }

    [Fact]
    public void ReleaseAttack_Lets_Go_Of_Every_Fire_Key()
    {
        BindTable.Bind("MOUSE1", "+fire");
        BindTable.Bind("Ctrl", "+fire");
        BindTable.HandleBind("MOUSE1", true, _ => { });
        BindTable.HandleBind("Ctrl", true, _ => { });
        BindTable.ReleaseAttack();
        Assert.False(BindTable.AttackHeld);
        BindTable.HandleBind("MOUSE1", false, _ => { }); // the physical release that follows is a stray
        Assert.False(BindTable.AttackHeld);
    }

    [Fact]
    public void Togglezoom_Off_Releases_A_Held_Plus_Zoom_Too()
    {
        // xonotic-client.cfg: togglezoom runs -zoom → -button4 with no key, which DP treats as "clear all".
        BindTable.Bind("MOUSE3", "togglezoom");
        BindTable.Bind("Z", "+zoom");
        BindTable.HandleBind("MOUSE3", true, _ => { }); // on
        BindTable.HandleBind("Z", true, _ => { });
        BindTable.HandleBind("MOUSE3", true, _ => { }); // off, even with Z held
        Assert.False(BindTable.ZoomHeld);
    }

    // =====================================================================================================
    //  Taps shorter than one move (playtest #61): DP's impulse-down bit (cl_input.c:91, :1801, :2180)
    // =====================================================================================================

    [Fact]
    public void Wheel_Bound_Jump_Press_And_Release_Before_The_Move_Still_Jumps()
    {
        var latch = new BindTable.TapLatch();
        BindTable.Bind("MWHEELDOWN", "+jump");
        BindTable.HandleBind("MWHEELDOWN", true, _ => { });   // the wheel's press and release arrive back to back
        BindTable.HandleBind("MWHEELDOWN", false, _ => { });
        Assert.False(BindTable.JumpHeld);                      // the level is already up again...
        Assert.True(latch.Sample(BindTable.MoveButton.Jump));  // ...but this move carries the press
        Assert.False(latch.Sample(BindTable.MoveButton.Jump)); // and only this one
    }

    [Fact]
    public void TapLatch_Reports_A_Held_Button_Every_Move()
    {
        var latch = new BindTable.TapLatch();
        BindTable.Bind("MOUSE5", "+hook");
        BindTable.HandleBind("MOUSE5", true, _ => { });
        Assert.True(latch.Sample(BindTable.MoveButton.Hook));
        Assert.True(latch.Sample(BindTable.MoveButton.Hook));
        BindTable.HandleBind("MOUSE5", false, _ => { });
        Assert.False(latch.Sample(BindTable.MoveButton.Hook));
    }

    [Fact]
    public void TapLatch_Readers_Are_Independent_And_Skip_Forgets_Taps()
    {
        var a = new BindTable.TapLatch();
        var b = new BindTable.TapLatch();
        BindTable.Bind("MOUSE1", "+fire");
        BindTable.HandleBind("MOUSE1", true, _ => { });
        BindTable.HandleBind("MOUSE1", false, _ => { });
        Assert.True(a.Sample(BindTable.MoveButton.Attack));
        Assert.True(b.Sample(BindTable.MoveButton.Attack)); // a's sample didn't consume b's tap
        BindTable.HandleBind("MOUSE1", true, _ => { });
        BindTable.HandleBind("MOUSE1", false, _ => { });
        a.Skip();                                            // the menu/console owned that press
        Assert.False(a.Sample(BindTable.MoveButton.Attack));
    }

    [Fact]
    public void Second_Key_Joining_A_Held_Button_Is_Not_A_New_Tap()
    {
        var latch = new BindTable.TapLatch();
        BindTable.Bind("Space", "+jump");
        BindTable.Bind("MOUSE5", "+jump");
        BindTable.HandleBind("Space", true, _ => { });
        Assert.True(latch.Sample(BindTable.MoveButton.Jump));
        BindTable.HandleBind("MOUSE5", true, _ => { });       // already down: DP sets no impulse either
        BindTable.HandleBind("Space", false, _ => { });
        BindTable.HandleBind("MOUSE5", false, _ => { });
        Assert.False(latch.Sample(BindTable.MoveButton.Jump));
    }

    [Fact]
    public void Wheel_Accumulator_Standard_Wheel_Is_One_Click_Per_Event()
    {
        var w = new WheelAccumulator();
        Assert.Equal(1, w.Clicks(up: true, 1f));
        Assert.Equal(1, w.Clicks(up: false, 1f));
        Assert.Equal(1, w.Clicks(up: false, 0f)); // a platform that reports no factor
    }

    [Fact]
    public void Wheel_Accumulator_Adds_Fractions_Into_Whole_Notches()
    {
        var w = new WheelAccumulator();
        Assert.Equal(0, w.Clicks(true, 0.25f));
        Assert.Equal(0, w.Clicks(true, 0.25f));
        Assert.Equal(0, w.Clicks(true, 0.25f));
        Assert.Equal(1, w.Clicks(true, 0.25f));
        int total = 0;
        for (int i = 0; i < 10; i++) total += w.Clicks(true, 0.1f);
        Assert.Equal(1, total);                    // float rounding does not lose the notch
        Assert.Equal(2, w.Clicks(true, 2f));       // a packed fast flick is two clicks
    }

    [Fact]
    public void Wheel_Accumulator_Drops_A_Partial_Notch_When_Direction_Flips()
    {
        var w = new WheelAccumulator();
        Assert.Equal(0, w.Clicks(true, 0.75f));
        Assert.Equal(0, w.Clicks(false, 0.5f));    // up's 0.75 is gone, not cancelled against
        Assert.Equal(1, w.Clicks(false, 0.5f));
    }

    [Fact]
    public void Console_Plus_And_Minus_Button_Commands_Drive_The_Table()
    {
        var cvars = new CvarService();
        var interp = new ConfigInterpreter(cvars, _ => null);
        _ = new ConsoleCommands(interp, cvars, _ => { });
        BindTable.Bind("Space", "+jump");
        BindTable.HandleBind("Space", true, _ => { });

        interp.ExecuteLine("+jump");             // typed: one more holder
        BindTable.HandleBind("Space", false, _ => { });
        Assert.True(BindTable.JumpHeld);
        interp.ExecuteLine("-jump");             // typed: clears everything
        Assert.False(BindTable.JumpHeld);

        interp.ExecuteLine("+fire 512");         // with a key, as DP's Key_Event sends it
        interp.ExecuteLine("+fire 513");
        interp.ExecuteLine("-fire 512");
        Assert.True(BindTable.AttackHeld);
        interp.ExecuteLine("-fire 513");
        Assert.False(BindTable.AttackHeld);
        Assert.False(cvars.Has("+fire"));        // never fell through to a bare cvar assignment
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
    private static readonly string Pk3Dir = TestPaths.CorePk3Dir;
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
