using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Confirm-quit dialog. C# port of <c>XonoticQuitDialog</c> (qcsrc/menu/xonotic/dialog_quit.qc):
/// a centered "Are you sure you want to quit?" prompt with Yes / No.
///
///  * Yes  → <see cref="SceneTree.Quit"/> (the QC runs <c>quit</c>).
///  * No   → pops back to whatever opened the dialog (the QC's <c>Dialog_Close</c>).
///
/// Presented full-rect with a dimmed backdrop so it reads as a modal over the main menu, rather than a
/// floating window — Godot makes the modal-over-host pattern from item/modalcontroller.qc trivial.
/// </summary>
public partial class QuitDialog : MenuScreen, ISelfFramedDialog
{
    /// <summary>
    /// When hosted as a nexposee panel the frame + backdrop come from the surrounding panel, so this dialog
    /// drops its own dim ColorRect + PanelContainer and just lays out the prompt + Yes/No filling the panel.
    /// </summary>
    public bool Embedded { get; set; }

    protected override void BuildUi()
    {
        Name = "QuitDialog";

        VBoxContainer column;
        if (Embedded)
        {
            // Centered content directly — the nexposee slot's bordered panel is our frame.
            var margin = new MarginContainer();
            margin.SetAnchorsPreset(LayoutPreset.FullRect);
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(side, 24);
            AddChild(margin);
            var center = new CenterContainer();
            margin.AddChild(center);
            column = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
            column.AddThemeConstantOverride("separation", 18);
            center.AddChild(column);
        }
        else
        {
            // Dimmed backdrop covering the menu beneath.
            var dim = new ColorRect
            {
                Color = new Color(0, 0, 0, 0.55f),
                MouseFilter = MouseFilterEnum.Stop,
            };
            dim.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(dim);

            // Centered confirm panel.
            var center = new CenterContainer();
            center.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(center);

            var panel = new PanelContainer();
            center.AddChild(panel);

            var margin = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(side, 24);
            panel.AddChild(margin);

            column = new VBoxContainer { CustomMinimumSize = new Vector2(340, 0) };
            column.AddThemeConstantOverride("separation", 18);
            margin.AddChild(column);
        }

        var prompt = MakeLabel("Are you sure you want to quit?");
        prompt.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(prompt);

        var buttons = MakeButtonBar(
            MakeButton("Yes", OnYes),
            MakeButton("No", OnNo));
        column.AddChild(buttons);
    }

    private void OnYes() => GetTree().Quit();

    private void OnNo() => GoBack();
}
