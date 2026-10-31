using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Settings dialog — <c>XonoticSettingsDialog_fill</c> (qcsrc/menu/xonotic/dialog_settings.qc) on its 18 x 6
/// grid: Video/Effects/Audio (2 columns each) over Game/Input/User/Misc (1.5 each), the tab body from row 2.5. Each
/// tab is a <see cref="QcTab"/> binding the same engine cvars its QC counterpart does. The QC dialog has no button
/// row: the tabs carry their own "Apply immediately", and the dialog closes from the frame's close button or Esc.
/// Settings persist on close (<see cref="MenuState.SaveUserConfig"/> in <c>_ExitTree</c>, as DarkPlaces archives
/// cvars to config.cfg at shutdown).
/// </summary>
public partial class DialogSettings : MenuScreen
{
    private QcTabController _tabs = null!;

    /// <summary>Select a tab by its title (e.g. "Audio"), or a Game topic as "Game/HUD"; no-op if not found. Used for
    /// dev/CI capture.</summary>
    public void SelectTab(string title)
    {
        string[] parts = title.Split('/', 2);
        _tabs.SelectByTitle(parts[0]);
        if (parts.Length > 1 && _game is not null)
            _game.SelectTopic(parts[1]);
    }

    private DialogSettingsGame? _game;

    protected override void BuildUi()
    {
        Name = "DialogSettings";

        var dialog = new QcGrid(rows: 18, columns: 6);
        dialog.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dialog);

        _tabs = new QcTabController();
        dialog.TR();
        dialog.TD(1, 2, _tabs.MakeTabButton("Video", new DialogSettingsVideo()));
        dialog.TD(1, 2, _tabs.MakeTabButton("Effects", new DialogSettingsEffects()));
        dialog.TD(1, 2, _tabs.MakeTabButton("Audio", new DialogSettingsAudio()));
        dialog.TR();
        dialog.TD(1, 1.5f, _tabs.MakeTabButton("Game", _game = new DialogSettingsGame()));
        dialog.TD(1, 1.5f, _tabs.MakeTabButton("Input", new DialogSettingsInput()));
        dialog.TD(1, 1.5f, _tabs.MakeTabButton("User", new DialogSettingsUser()));
        dialog.TD(1, 1.5f, _tabs.MakeTabButton("Misc", new DialogSettingsMisc()));
        dialog.GotoRC(2.5f, 0);
        dialog.TD(dialog.Rows - 2.5f, dialog.Columns, _tabs);
    }

    public override void _ExitTree()
    {
        // DP archives changed (seta) cvars to config.cfg on shutdown; the dialog leaving the tree is the
        // closest equivalent for a pushed settings dialog (the nexposee panel persists via MainMenu instead).
        MenuState.SaveUserConfig();
    }
}
