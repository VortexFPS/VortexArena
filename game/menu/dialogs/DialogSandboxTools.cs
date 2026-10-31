using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Sandbox Tools" — <c>XonoticSandboxToolsDialog_fill</c> (qcsrc/menu/xonotic/dialog_sandboxtools.qc) on its
/// 16 x 4 grid, 0.8 of the menu wide: the model box with Spawn / Remove * / Copy * / Paste, the bone box with the
/// three attach buttons, the visual properties (skin and opacity sliders, the two colour pickers two rows high,
/// frame), the physical properties (material box, solidity and physics radios, scale and force sliders), the
/// claim / info / help buttons (the info ones tinted '1 1 0.5', help '1 0.5 0.5'), the "* is the object you are
/// facing" note, and "OK" on the last row.
///
/// The cvar bindings are real, and each <c>sandbox …</c> command is expanded by <see cref="MenuCommand"/> (the cfg
/// alias <c>sandbox "cmd g_sandbox …"</c>) and routed to the match's SandboxMutator when <c>g_sandbox</c> is on.
/// (Object duplicate COPY stays inert: the port has no svc_stufftext channel to set the clipboard cvar on a
/// remote client.)
/// </summary>
public partial class DialogSandboxTools : MenuScreen, IQcDialog
{
    // dialog_sandboxtools.qh (name "SandboxTools")
    public string QcTitle => Localization.Tr("Sandbox Tools");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 16;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_SANDBOXTOOLS", 1f, 1f, 1f);

    protected override void BuildUi()
    {
        Name = "DialogSandboxTools"; // QC ATTRIB name "SandboxTools"

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        var solidity = new QcRadioGroup();
        var physics = new QcRadioGroup();

        me.TR();
            me.TD(1, 0.3f, Qc.Label("Model:"));
            me.TD(1, 1.3f, InputBox("menu_sandbox_spawn_model"));
            me.TDempty(0.1f);
            me.TD(1, 2.2f / 4, Qc.Command("Spawn", "sandbox object_spawn \"$menu_sandbox_spawn_model\""));
            me.TD(1, 2.2f / 4, Qc.Command("Remove *", "sandbox object_remove"));
            me.TDempty(0.1f);
            me.TD(1, 2.2f / 4, Qc.Command("Copy *", "sandbox object_duplicate copy cl_sandbox_clipboard"));
            me.TD(1, 2.2f / 4, Qc.Command("Paste", "sandbox object_duplicate paste \"$cl_sandbox_clipboard\""));
        me.TR();
            me.TD(1, 0.3f, Qc.Label("Bone:"));
            me.TD(1, 1.3f, InputBox("menu_sandbox_attach_bone"));
            me.TDempty(0.1f);
            me.TD(1, 2.1f / 3, Qc.Command("Set * as child", "sandbox object_attach get"));
            me.TDempty(0.1f);
            me.TD(1, 2.1f / 3, Qc.Command("Attach to *", "sandbox object_attach set \"$menu_sandbox_attach_bone\""));
            me.TDempty(0.1f);
            me.TD(1, 2.1f / 3, Qc.Command("Detach from *", "sandbox object_attach remove"));
        me.TR();
        me.TR();
            me.TD(1, 4, Qc.Label("Visual object properties for *:"));
        me.TR();
            me.TD(1, 0.75f, Qc.Command("Set skin:", "sandbox object_edit skin $menu_sandbox_edit_skin"));
            me.TD(1, 1.25f, Qc.Slider(0, 99, 1, "menu_sandbox_edit_skin"));
            me.TD(1, 0.75f, Qc.Command("Set opacity:", "sandbox object_edit alpha $menu_sandbox_edit_alpha"));
            me.TD(1, 1.25f, Qc.Slider(0.1f, 1, 0.05f, "menu_sandbox_edit_alpha", format: "%"));
        me.TR();
            me.TD(1, 0.75f, Qc.Command("Set color main:", "sandbox object_edit color_main \"$menu_sandbox_edit_color_main\""));
            me.TD(2, 1.25f, ColorPicker("menu_sandbox_edit_color_main"));
            me.TD(1, 0.75f, Qc.Command("Set color glow:", "sandbox object_edit color_glow \"$menu_sandbox_edit_color_glow\""));
            me.TD(2, 1.25f, ColorPicker("menu_sandbox_edit_color_glow"));
        me.TR();
        me.TR();
            me.TD(1, 0.75f, Qc.Command("Set frame:", "sandbox object_edit frame $menu_sandbox_edit_frame"));
            me.TD(1, 1.25f, Qc.Slider(0, 99, 1, "menu_sandbox_edit_frame"));
        me.TR();
            me.TD(1, 4, Qc.Label("Physical object properties for *:"));
        me.TR();
            me.TD(1, 0.6f, Qc.Command("Set material:", "sandbox object_edit material \"$menu_sandbox_edit_material\""));
            me.TD(1, 1.4f, InputBox("menu_sandbox_edit_material"));
        me.TR();
            me.TD(1, 0.6f, Qc.Command("Set solidity:", "sandbox object_edit solidity $menu_sandbox_edit_solidity"));
            me.TD(1, 1.4f / 2, Qc.Radio(solidity, "menu_sandbox_edit_solidity", "0", "Non-solid"));
            me.TD(1, 1.4f / 2, Qc.Radio(solidity, "menu_sandbox_edit_solidity", "1", "Solid"));
            me.TD(1, 0.6f, Qc.Command("Set physics:", "sandbox object_edit physics $menu_sandbox_edit_physics"));
            me.TD(1, 1.4f / 3, Qc.Radio(physics, "menu_sandbox_edit_physics", "0", "Static"));
            me.TD(1, 1.4f / 3, Qc.Radio(physics, "menu_sandbox_edit_physics", "1", "Movable"));
            me.TD(1, 1.4f / 3, Qc.Radio(physics, "menu_sandbox_edit_physics", "2", "Physical"));
        me.TR();
            me.TD(1, 0.6f, Qc.Command("Set scale:", "sandbox object_edit scale $menu_sandbox_edit_scale"));
            me.TD(1, 1.4f, Qc.Slider(0.25f, 2, 0.05f, "menu_sandbox_edit_scale"));
            me.TD(1, 0.6f, Qc.Command("Set force:", "sandbox object_edit force $menu_sandbox_edit_force"));
            me.TD(1, 1.4f, Qc.Slider(0, 10, 0.5f, "menu_sandbox_edit_force"));
        me.TR();
        me.TR();
            me.TD(1, 0.5f, Qc.Command("Claim *", "sandbox object_claim"));
            me.TDempty(0.25f);
            me.TD(1, 0.8f, TintedCommand("* object info", new Color(1, 1, 0.5f), "sandbox object_info object; toggleconsole"));
            me.TD(1, 0.75f, TintedCommand("* mesh info", new Color(1, 1, 0.5f), "sandbox object_info mesh; toggleconsole"));
            me.TD(1, 0.8f, TintedCommand("* attachment info", new Color(1, 1, 0.5f), "sandbox object_info attachments; toggleconsole"));
            me.TDempty(0.25f);
            me.TD(1, 0.65f, TintedCommand("Show help", new Color(1, 0.5f, 0.5f), "sandbox help; toggleconsole"));
        me.TR();
            me.TDempty(0.1f);
            me.TD(1, 3.9f, Qc.Label("* is the object you are facing"));

        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns, Qc.Button("OK", GoBack));
    }

    /// <summary>makeXonoticInputBox(1, cvar): colour codes shown as colours, with the clear button.</summary>
    private static LineEdit InputBox(string cvar)
        => QcLook.InputBox(Widgets.InputBox(cvar), clearButton: true, editColorCodes: true);

    /// <summary>makeXonoticColorpickerString(cvar, cvar): the HSL picture writing "r g b" into the cvar.</summary>
    private static HslColorPicker ColorPicker(string cvar)
    {
        HslColorPicker picker = HslColorPicker.ForStringCvar(cvar);
        picker.CustomMinimumSize = Vector2.Zero; // a QC item is exactly its cell
        return picker;
    }

    /// <summary>makeXonoticCommandButton(text, color, command, 0) with a colour: the gray art, tinted.</summary>
    private static CommandButton TintedCommand(string text, Color tint, string command)
        => QcLook.Button(Widgets.CommandButton(text, command), tint);
}
