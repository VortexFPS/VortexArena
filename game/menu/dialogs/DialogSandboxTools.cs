using System.Globalization;
using Godot;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Sandbox-tools cheat dialog — a faithful C# port of <c>XonoticSandboxToolsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_sandboxtools.qc). It is the in-game editor (g_sandbox) for spawning and editing
/// map objects: a model field with Spawn/Remove/Copy/Paste, an attach-bone field with attach/detach actions,
/// then the visual (skin/opacity/color main+glow/frame) and physical (material/solidity/physics/scale/force)
/// property editors for the object you are facing, plus the claim/info/help buttons. Every cvar control binds
/// the same engine cvar the QC binds and every button issues the same console command string through
/// <see cref="Widgets.CommandButton"/>; the QC notes "* is the object you are facing".
///
/// FAITHFUL UI NOW: the cvar bindings are real (they write the shared <see cref="MenuState.Cvars"/> store the
/// game reads), and the action buttons drive the live SandboxMutator backend: each <c>sandbox …</c> string is
/// expanded by <see cref="MenuCommand"/> (the cfg alias <c>sandbox "cmd g_sandbox …"</c>) and routed to the
/// match (Commands.CmdSandbox → SandboxMutator.HandleCommand) when <c>g_sandbox</c> is enabled. (Object
/// duplicate COPY remains inert — the port has no svc_stufftext channel to set the clipboard cvar on a remote
/// client.) The two QC <c>makeXonoticColorpickerString</c> widgets have no toolkit factory, so they are built
/// as <see cref="DialogSandboxToolsColorButton"/> (a Godot <see cref="ColorPickerButton"/> bound to the same
/// "R G B" string cvar). The QC "OK" button just closed the dialog (<c>Dialog_Close</c>); here that is Back.
/// </summary>
public partial class DialogSandboxTools : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogSandboxTools"; // QC ATTRIB name "SandboxTools"

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Sandbox Tools"));

        // Dense form — wrap it in a ScrollContainer so it scrolls. NEVER a TabContainer here.
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // ---- Model row: input + Spawn / Remove * / Copy * / Paste --------------------------------------
        // QC: makeXonoticInputBox(1, "menu_sandbox_spawn_model") then four command buttons.
        box.AddChild(Ui.Row("Model:", Widgets.InputBox("menu_sandbox_spawn_model")));
        var modelBtns = MakeButtonRow(
            Widgets.CommandButton("Spawn",    "sandbox object_spawn \"$menu_sandbox_spawn_model\""),
            Widgets.CommandButton("Remove *", "sandbox object_remove"),
            Widgets.CommandButton("Copy *",   "sandbox object_duplicate copy cl_sandbox_clipboard"),
            Widgets.CommandButton("Paste",    "sandbox object_duplicate paste \"$cl_sandbox_clipboard\""));
        box.AddChild(modelBtns);

        // ---- Bone row: input + Set * as child / Attach to * / Detach from * ----------------------------
        // QC: makeXonoticInputBox(1, "menu_sandbox_attach_bone") then three attach command buttons.
        box.AddChild(Ui.Row("Bone:", Widgets.InputBox("menu_sandbox_attach_bone")));
        box.AddChild(MakeButtonRow(
            Widgets.CommandButton("Set * as child", "sandbox object_attach get"),
            Widgets.CommandButton("Attach to *",    "sandbox object_attach set \"$menu_sandbox_attach_bone\""),
            Widgets.CommandButton("Detach from *",  "sandbox object_attach remove")));

        box.AddChild(Ui.Spacer());

        // ================================================================================================
        //  Visual object properties for * (QC makeXonoticTextLabel header)
        // ================================================================================================
        box.AddChild(Ui.Header("Visual object properties for *:"));

        // Set skin: button + skin slider (QC "sandbox object_edit skin $menu_sandbox_edit_skin", 0..99 step 1).
        box.AddChild(MakeActionSliderRow(
            "Set skin:", "sandbox object_edit skin $menu_sandbox_edit_skin",
            "menu_sandbox_edit_skin", 0f, 99f, 1f));

        // Set opacity: button + alpha slider with "%" format (QC formatString "%", 0.1..1 step 0.05).
        box.AddChild(MakeActionSliderRow(
            "Set opacity:", "sandbox object_edit alpha $menu_sandbox_edit_alpha",
            "menu_sandbox_edit_alpha", 0.1f, 1f, 0.05f, Percent));

        // Set color main: button + color picker (QC makeXonoticColorpickerString on "menu_sandbox_edit_color_main").
        box.AddChild(MakeActionColorRow(
            "Set color main:", "sandbox object_edit color_main \"$menu_sandbox_edit_color_main\"",
            "menu_sandbox_edit_color_main"));

        // Set color glow: button + color picker (QC makeXonoticColorpickerString on "menu_sandbox_edit_color_glow").
        box.AddChild(MakeActionColorRow(
            "Set color glow:", "sandbox object_edit color_glow \"$menu_sandbox_edit_color_glow\"",
            "menu_sandbox_edit_color_glow"));

        // Set frame: button + frame slider (QC "sandbox object_edit frame $menu_sandbox_edit_frame", 0..99 step 1).
        box.AddChild(MakeActionSliderRow(
            "Set frame:", "sandbox object_edit frame $menu_sandbox_edit_frame",
            "menu_sandbox_edit_frame", 0f, 99f, 1f));

        box.AddChild(Ui.Spacer());

        // ================================================================================================
        //  Physical object properties for * (QC makeXonoticTextLabel header)
        // ================================================================================================
        box.AddChild(Ui.Header("Physical object properties for *:"));

        // Set material: button + material input box (QC makeXonoticInputBox(1, "menu_sandbox_edit_material")).
        var matRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        matRow.AddThemeConstantOverride("separation", 8);
        var matBtn = Widgets.CommandButton("Set material:", "sandbox object_edit material \"$menu_sandbox_edit_material\"");
        matBtn.CustomMinimumSize = new Vector2(160, 36);
        matBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        matRow.AddChild(matBtn);
        matRow.AddChild(Widgets.InputBox("menu_sandbox_edit_material"));
        box.AddChild(matRow);

        // Set solidity: button + radio (QC group 1: Non-solid=0 / Solid=1 on menu_sandbox_edit_solidity).
        var solidRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        solidRow.AddThemeConstantOverride("separation", 8);
        var solidBtn = Widgets.CommandButton("Set solidity:", "sandbox object_edit solidity $menu_sandbox_edit_solidity");
        solidBtn.CustomMinimumSize = new Vector2(160, 36);
        solidBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        solidRow.AddChild(solidBtn);
        var solidGroup = new ButtonGroup();
        solidRow.AddChild(Widgets.RadioButton("menu_sandbox_edit_solidity", "0", "Non-solid", solidGroup));
        solidRow.AddChild(Widgets.RadioButton("menu_sandbox_edit_solidity", "1", "Solid", solidGroup));
        box.AddChild(solidRow);

        // Set physics: button + radio (QC group 2: Static=0 / Movable=1 / Physical=2 on menu_sandbox_edit_physics).
        var physRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        physRow.AddThemeConstantOverride("separation", 8);
        var physBtn = Widgets.CommandButton("Set physics:", "sandbox object_edit physics $menu_sandbox_edit_physics");
        physBtn.CustomMinimumSize = new Vector2(160, 36);
        physBtn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        physRow.AddChild(physBtn);
        var physGroup = new ButtonGroup();
        physRow.AddChild(Widgets.RadioButton("menu_sandbox_edit_physics", "0", "Static", physGroup));
        physRow.AddChild(Widgets.RadioButton("menu_sandbox_edit_physics", "1", "Movable", physGroup));
        physRow.AddChild(Widgets.RadioButton("menu_sandbox_edit_physics", "2", "Physical", physGroup));
        box.AddChild(physRow);

        // Set scale: button + scale slider (QC "sandbox object_edit scale $menu_sandbox_edit_scale", 0.25..2 step 0.05).
        box.AddChild(MakeActionSliderRow(
            "Set scale:", "sandbox object_edit scale $menu_sandbox_edit_scale",
            "menu_sandbox_edit_scale", 0.25f, 2f, 0.05f));

        // Set force: button + force slider (QC "sandbox object_edit force $menu_sandbox_edit_force", 0..10 step 0.5).
        box.AddChild(MakeActionSliderRow(
            "Set force:", "sandbox object_edit force $menu_sandbox_edit_force",
            "menu_sandbox_edit_force", 0f, 10f, 0.5f));

        box.AddChild(Ui.Spacer());

        // ---- Claim * / object info / mesh info / attachment info / Show help ----------------------------
        // QC: command buttons issuing the various "sandbox object_info …; toggleconsole" / "sandbox help" cmds.
        box.AddChild(MakeButtonRow(
            Widgets.CommandButton("Claim *",           "sandbox object_claim"),
            Widgets.CommandButton("* object info",     "sandbox object_info object; toggleconsole"),
            Widgets.CommandButton("* mesh info",       "sandbox object_info mesh; toggleconsole"),
            Widgets.CommandButton("* attachment info", "sandbox object_info attachments; toggleconsole"),
            Widgets.CommandButton("Show help",         "sandbox help; toggleconsole")));

        // QC: makeXonoticTextLabel(0, _("* is the object you are facing")).
        box.AddChild(Ui.Label("* is the object you are facing"));

        // QC "OK" button (onClick = Dialog_Close). Here the universal Back closes the dialog.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    // -----------------------------------------------------------------------------------------------------
    //  Row builders (the QC packs a "Set X:" command button beside the control that edits its cvar).
    // -----------------------------------------------------------------------------------------------------

    /// <summary>A row of equal-width command buttons (one QC TR full of makeXonoticCommandButton cells).</summary>
    private static HBoxContainer MakeButtonRow(params Button[] buttons)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        foreach (Button b in buttons)
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(b);
        }
        return row;
    }

    /// <summary>A "Set X:" command button + a cvar slider on the same line (QC button cell + slider cell).</summary>
    private static HBoxContainer MakeActionSliderRow(string label, string command, string cvar,
        float min, float max, float step, System.Func<float, string>? format = null)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        var btn = Widgets.CommandButton(label, command);
        btn.CustomMinimumSize = new Vector2(160, 36);
        btn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        row.AddChild(btn);
        row.AddChild(Widgets.Slider(cvar, min, max, step, format: format));
        return row;
    }

    /// <summary>A "Set color X:" command button + a color picker on the same line (QC button + colorpicker).</summary>
    private static HBoxContainer MakeActionColorRow(string label, string command, string cvar)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        var btn = Widgets.CommandButton(label, command);
        btn.CustomMinimumSize = new Vector2(160, 36);
        btn.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        row.AddChild(btn);
        var picker = new DialogSandboxToolsColorButton(cvar) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(picker);
        return row;
    }

    /// <summary>Render a 0..1 alpha as a percentage (QC slider formatString "%").</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}

// ---------------------------------------------------------------------------------------------------------
//  Helper: a color picker bound to a "R G B" float-triple string cvar (QC makeXonoticColorpickerString).
// ---------------------------------------------------------------------------------------------------------

/// <summary>
/// The C# successor to <c>XonoticColorpickerString</c> (qcsrc/menu/xonotic/colorpicker_string.qc): a color
/// well bound to a string cvar that holds three space-separated 0..1 floats ("R G B"). Reading parses the
/// triple into a <see cref="Color"/>; picking a color writes the triple back (and marks it archived). Built as
/// a Godot <see cref="ColorPickerButton"/> because the widget toolkit has no color factory yet.
/// </summary>
public partial class DialogSandboxToolsColorButton : ColorPickerButton
{
    private readonly string _cvar;
    private bool _updating;

    public DialogSandboxToolsColorButton(string cvar)
    {
        _cvar = cvar;
        EditAlpha = false; // QC color picker stores RGB only.
        CustomMinimumSize = new Vector2(0, 32);
        ColorChanged += OnColorChanged;
    }

    public override void _EnterTree() { MenuState.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { MenuState.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        Color = ParseRgb(MenuState.Cvars.GetString(_cvar));
        _updating = false;
    }

    private void OnColorChanged(Color c)
    {
        if (_updating) return;
        // QC stores "R G B" with each channel as a trimmed 0..1 float.
        string v = string.Join(' ',
            c.R.ToString("0.##", CultureInfo.InvariantCulture),
            c.G.ToString("0.##", CultureInfo.InvariantCulture),
            c.B.ToString("0.##", CultureInfo.InvariantCulture));
        MenuState.Cvars.Set(_cvar, v);
        MenuState.Cvars.MarkArchived(_cvar);
    }

    /// <summary>Parse a "R G B" 0..1 float triple into a color; defaults to white on a malformed value.</summary>
    private static Color ParseRgb(string s)
    {
        string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        float r = 1f, g = 1f, b = 1f;
        if (parts.Length >= 1) float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
        if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g);
        if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
        return new Color(r, g, b);
    }
}
