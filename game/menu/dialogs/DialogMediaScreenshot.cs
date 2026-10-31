using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Screenshots tab — <c>XonoticScreenshotBrowserTab_fill</c> (qcsrc/menu/xonotic/dialog_media_screenshot.qc) on
/// its 17 x 6.5 grid: the filter box and "Auto screenshot scoreboard" (<c>cl_autoscreenshot</c> 2/1) + Refresh half a
/// row down, the screenshot list (<see cref="QcNameList"/>, screenshotlist.qc: <c>screenshots/*.jpg|tga|png</c>, one
/// subdirectory level too) from row 2, "Open in the viewer" under it.
///
/// "Open in the viewer" (or a double-click)
/// opens the screenshot viewer (<see cref="DialogScreenshotViewer"/>) on it.
/// </summary>
public partial class DialogMediaScreenshot : QcTab
{
    private QcNameList _list = null!;
    private string? _filter;

    public DialogMediaScreenshot() : base(rows: 17f, columns: 6.5f, intendedWidth: 0.96f) { }

    protected override void Fill()
    {
        _list = new QcNameList(skinColor: false);
        _list.ItemActivated += _ => OpenViewer();

        GotoRC(0.5f, 0); SetFirstColumn(CurrentColumn);
            TD(1, 0.6f, Qc.Label("Filter:", 1f));
            var filter = TD(1, 2.4f, QcLook.InputBox(new LineEdit()));
                filter.TextChanged += t => { _filter = DialogMediaDemo.Glob(t); GetScreenshots(); };

        GotoRC(0.5f, 3.1f);
            TD(1, 1.9f, Qc.CheckEx(2, 1, "cl_autoscreenshot", "Auto screenshot scoreboard"));
            TD(1, 1, Qc.Button("Refresh", GetScreenshots));

        GotoRC(2, 0);
            TD(Rows - 3, Columns, _list);

        GotoRC(Rows - 1, 0);
            TD(1, Columns, Qc.Button("Open in the viewer", OpenViewer));

        GetScreenshots();
    }

    /// <summary>XonoticScreenshotList_getScreenshots.</summary>
    private void GetScreenshots()
        => _list.SetNames(DialogMediaDemo.Scan("screenshots/", new[] { ".jpg", ".tga", ".png" }, _filter, "COLOR_SCREENSHOTLIST_SUBDIR"));

    /// <summary>XonoticScreenshotList_startScreenshot: the viewer for the selected screenshot.</summary>
    private void OpenViewer()
    {
        if (_list.SelectedItem >= 0 && _list.SelectedItem < _list.Names.Count)
            Menu?.Push(new DialogScreenshotViewer(_list));
    }
}
