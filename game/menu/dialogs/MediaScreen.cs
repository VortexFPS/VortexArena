using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Media dialog — a <see cref="TabContainer"/> of the four media categories, C# successor to
/// <c>XonoticMediaDialog_fill</c> (qcsrc/menu/xonotic/dialog_media.qc): Guide, Demos, Screenshots,
/// Music Player (same tab order as the QC tab controller). Each page is its own
/// <see cref="Control"/> subclass mirroring the matching <c>dialog_media_*.qc</c> fill function.
///
/// FAITHFUL UI NOW: the surrounding controls, layout and command buttons are reproduced, but the
/// demo / screenshot / music / guide-registry data sources have no VortexArena backend yet, so the
/// list panes render empty with honest notes and their action buttons are inert (see each page).
/// </summary>
public partial class MediaScreen : MenuScreen
{
    private TabContainer _tabs = null!;

    /// <summary>Select a tab by its title (e.g. "Demos"); no-op if not found. Used for dev/CI capture.</summary>
    public void SelectTab(string title)
    {
        for (int i = 0; i < _tabs.GetTabCount(); i++)
            if (_tabs.GetTabTitle(i) == title) { _tabs.CurrentTab = i; return; }
    }

    protected override void BuildUi()
    {
        Name = "MediaScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Media"));

        // The tab host. NOT inside a ScrollContainer (it would collapse to 0 height) — it expands to fill.
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _tabs = tabs;
        root.AddChild(tabs);

        // QC tab order (dialog_media.qc): Guide, Demos, Screenshots, Music Player.
        AddTab(tabs, "Guide", new DialogMediaGuide());
        AddTab(tabs, "Demos", new DialogMediaDemo());
        AddTab(tabs, "Screenshots", new DialogMediaScreenshot());
        AddTab(tabs, "Music Player", new DialogMediaMusicPlayer());

        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }

    private static void AddTab(TabContainer tabs, string title, Control page)
    {
        // Wrap each page in a padded MarginContainer so the tab content (column headers, lists) gets
        // breathing room off the panel's inner border — otherwise the TabContainer draws the page flush
        // against the tab strip and the headers clip at the top. The wrapper carries the tab title so
        // SelectTab/GetTabTitle keep working. (Matches DialogSettings' inset tab content; ~16px.)
        var pad = new MarginContainer
        {
            Name = title,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 16);

        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        page.SizeFlagsVertical = SizeFlags.ExpandFill;
        pad.AddChild(page);
        tabs.AddChild(pad);
    }
}
