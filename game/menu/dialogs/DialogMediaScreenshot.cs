using Godot;
using XonoticGodot.Common.Menu;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Screenshots tab — a faithful C# port of <c>XonoticScreenshotBrowserTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_screenshot.qc). The QC builds a filter box, an
/// <c>XonoticScreenshotList</c> (engine-enumerated screenshots), "Auto screenshot scoreboard"
/// (<c>cl_autoscreenshot</c> bit 1, sendCvars) + Refresh, and an "Open in the viewer" button that
/// pushes the screenshot viewer dialog. We also surface the viewer's slideshow/zoom transport
/// controls from <c>XonoticScreenshotViewerDialog_fill</c> (dialog_media_screenshot_viewer.qc):
/// zoom −/+/Reset, Previous/Next, Slide show.
///
/// The screenshot LIST is now a live file-scan backend (<see cref="ScreenshotSource"/>, the C# port of
/// screenshotlist.qc): it enumerates <c>screenshots/*.{jpg,tga,png}</c> via the asset VFS, strips the
/// prefix/extension, decolorizes and sorts the names (QC getScreenshots + buf_sort). With no VFS mounted it
/// renders empty with an honest note. The image viewer itself (the preview pane / slideshow) has no backend,
/// so the viewer transport buttons remain inert. The <c>cl_autoscreenshot</c> flag checkbox is live.
/// </summary>
public partial class DialogMediaScreenshot : Control
{
    private ScreenshotSource _source = null!;
    private ItemList _list = null!;
    private Label _note = null!;
    private LineEdit _filter = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _source = new ScreenshotSource(MenuDataBridge.Files);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        // Filter (QC makeXonoticInputBox + ScreenshotList_Filter_Change → re-scan with the filter glob).
        _filter = new LineEdit { PlaceholderText = "Filter screenshots…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _filter.TextChanged += _ => Reload();
        box.AddChild(Ui.Row("Filter:", _filter, 80f));

        var topButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topButtons.AddThemeConstantOverride("separation", 10);
        // QC: makeXonoticCheckBoxEx(2, 1, "cl_autoscreenshot", _("Auto screenshot scoreboard")) — checked
        // stores 2 (shoot every scoreboard), unchecked 1 (the shipped default: only when a demo ends).
        var autoshot = Widgets.ValueCheckBox("cl_autoscreenshot", 2f, 1f, "Auto screenshot scoreboard");
        autoshot.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topButtons.AddChild(autoshot);
        // QC: makeXonoticButton(_("Refresh"), …) onClick=ScreenshotList_Refresh_Click → getScreenshots + select 0.
        topButtons.AddChild(Ui.Button("Refresh", Reload));
        box.AddChild(topButtons);

        // The screenshot list (QC: slist spans the middle), now populated by the ScreenshotSource backend.
        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _list.ItemActivated += _ => OpenViewer(); // QC doubleClickListBoxItem → startScreenshot
        box.AddChild(_list);

        _note = Ui.Label("");
        _note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        box.AddChild(_note);

        // QC: makeXonoticButton(_("Open in the viewer"), …) onClick=StartScreenshot_Click (opens the viewer).
        box.AddChild(Ui.Button("Open in the viewer", OpenViewer));

        // Viewer transport controls (from XonoticScreenshotViewerDialog_fill): zoom −/+/Reset, Previous/Next,
        // Slide show. The viewer's image pane + slideshow are backend-driven, so these are inert.
        box.AddChild(Ui.Spacer(6f));
        box.AddChild(Ui.Header("Viewer"));
        var viewer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        viewer.AddThemeConstantOverride("separation", 8);
        viewer.AddChild(Ui.Button("-", () => MenuCommand.Run("menu_screenshotviewer_zoomout")));   // decreaseZoom_Click
        viewer.AddChild(Ui.Button("+", () => MenuCommand.Run("menu_screenshotviewer_zoomin")));    // increaseZoom_Click
        viewer.AddChild(Ui.Button("Reset", () => MenuCommand.Run("menu_screenshotviewer_zoomreset"))); // resetZoom_Click
        viewer.AddChild(Ui.Button("Previous", () => MenuCommand.Run("menu_screenshotviewer_prev"))); // prevScreenshot_Click
        viewer.AddChild(Ui.Button("Next", () => MenuCommand.Run("menu_screenshotviewer_next")));     // nextScreenshot_Click
        viewer.AddChild(Ui.Button("Slide show", () => MenuCommand.Run("menu_screenshotviewer_slideshow"))); // toggleSlideShow_Click
        box.AddChild(viewer);

        Reload();
    }

    /// <summary>QC getScreenshots: rescan + repopulate the list (honoring the filter box), then select the first row.</summary>
    private void Reload()
    {
        // ScreenshotList_Filter_Change wraps a plain query in "*query*"; an empty box means no filter.
        string text = _filter.Text;
        string? filter = string.IsNullOrEmpty(text)
            ? null
            : (text.Contains('*') || text.Contains('?') ? text : $"*{text}*");

        int n = _source.Reload(filter);
        _list.Clear();
        foreach (string name in _source.Names)
            _list.AddItem(name);
        if (_list.ItemCount > 0)
            _list.Select(0); // QC always selects the first element after a list update

        _note.Text = n > 0
            ? $"({n} screenshot{(n == 1 ? "" : "s")} found in screenshots/)"
            : "(no screenshots found — none captured yet, or the asset VFS isn't mounted)";
    }

    /// <summary>QC startScreenshot: pop up the viewer for the selected screenshot. Viewer backend pending → inert log.</summary>
    private void OpenViewer()
    {
        // The image viewer has no backend (no image pane / slideshow), so this stays a representative command.
        MenuCommand.Run("menu_showscreenshotviewer");
    }
}
