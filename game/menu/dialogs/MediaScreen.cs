namespace VortexArena.Game.Menu;

/// <summary>
/// The Media dialog — <c>XonoticMediaDialog_fill</c> (qcsrc/menu/xonotic/dialog_media.qc) on its 18 x 6 grid: the
/// Guide, Demos, Screenshots and Music Player tab buttons a quarter of the row each, the tab body under them. Each
/// tab is a <see cref="QcTab"/> laid out as its <c>dialog_media_*.qc</c> fill function lays it out.
/// </summary>
public partial class MediaScreen : MenuScreen
{
    private QcTabController _tabs = null!;

    /// <summary>Select a tab by its title (e.g. "Demos"); no-op if not found. Used for dev/CI capture.</summary>
    public void SelectTab(string title) => _tabs.SelectByTitle(title);

    protected override void BuildUi()
    {
        Name = "MediaScreen";

        var me = new QcGrid(rows: 18, columns: 6);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        _tabs = new QcTabController();
        me.TR();
            me.TD(1, 6f / 4f, _tabs.MakeTabButton("Guide", new DialogMediaGuide()));
            me.TD(1, 6f / 4f, _tabs.MakeTabButton("Demos", new DialogMediaDemo()));
            me.TD(1, 6f / 4f, _tabs.MakeTabButton("Screenshots", new DialogMediaScreenshot()));
            me.TD(1, 6f / 4f, _tabs.MakeTabButton("Music Player", new DialogMediaMusicPlayer()));
        me.TR();
            me.TD(me.Rows - 1, me.Columns, _tabs);
    }
}
