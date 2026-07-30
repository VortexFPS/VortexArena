using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Demos tab — a faithful C# port of <c>XonoticDemoBrowserTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_demo.qc). The QC builds a filter box + an
/// <c>XonoticDemoList</c> (engine-enumerated *.dem files) with "Auto record demos"
/// (<c>cl_autodemo</c>), Refresh, Timedemo and Play buttons.
///
/// FAITHFUL UI NOW: VortexArena has no demo playback / demo-file enumeration backend, so the demo
/// list is rendered as an empty <see cref="ItemList"/> with an honest note; the filter, Refresh,
/// Timedemo and Play actions are present but inert (routed through <see cref="MenuCommand"/> with
/// the representative DP console commands <c>playdemo</c>/<c>timedemo</c>, logged as having no
/// backend). Only the <c>cl_autodemo</c> checkbox is live (it binds the same cvar the QC binds).
/// </summary>
public partial class DialogMediaDemo : Control
{
    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        // Top row: filter (QC makeXonoticInputBox + DemoList_Filter_Change — no cvar; client-side filter, inert)
        // and "Auto record demos" + Refresh.
        var filter = new LineEdit { PlaceholderText = "Filter demos…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(Ui.Row("Filter:", filter, 80f));

        var topButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topButtons.AddThemeConstantOverride("separation", 10);
        // QC: makeXonoticCheckBox(0, "cl_autodemo", _("Auto record demos")) — live, binds the same cvar.
        var autodemo = Widgets.CheckBox("cl_autodemo", "Auto record demos");
        autodemo.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topButtons.AddChild(autodemo);
        // QC: makeXonoticButton(_("Refresh"), …) onClick=DemoList_Refresh_Click — rescans the demo dir (inert here).
        topButtons.AddChild(Ui.Button("Refresh", () => MenuCommand.Run("menu_cmd sync")));
        box.AddChild(topButtons);

        // The demo list (QC: demolist = makeXonoticDemoList(); spans the middle). Backend absent → empty + note.
        var list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 240),
        };
        box.AddChild(list);
        box.AddChild(Ui.Label("(demo list — demo playback / *.dem enumeration backend pending)"));

        // Bottom row: Timedemo (left half) + Play (right half), matching the QC two-column split.
        // QC handlers run demolist.timeDemo / demolist.startDemo; representative DP commands are timedemo/playdemo.
        var bottom = Ui.ButtonBar(
            Ui.Button("Timedemo", () => MenuCommand.Run("timedemo")),
            Ui.Button("Play", () => MenuCommand.Run("playdemo")));
        box.AddChild(bottom);
    }
}
