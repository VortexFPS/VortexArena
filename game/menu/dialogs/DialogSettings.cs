using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Settings dialog — C# successor to <c>XonoticSettingsDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings.qc): a two-row, full-width tab grid (Video/Effects/Audio over
/// Game/Input/User/Misc, exactly the QC column spans) over the frameless tab body. Each tab is a
/// <see cref="SettingsTab"/> subclass binding the same engine cvars its QC counterpart does (the shared
/// <see cref="MenuState.Cvars"/> store). The QC dialog has NO bottom button row — per-tab Apply buttons
/// (e.g. Video's "Apply immediately") live inside their tabs, and the dialog closes via the frame's
/// close-X / Esc. Settings persist on close (<see cref="MenuState.SaveUserConfig"/> in <c>_ExitTree</c>,
/// mirroring DP archiving cvars to config.cfg at shutdown).
/// </summary>
public partial class DialogSettings : MenuScreen
{
    private XonoticTabs _tabs = null!;

    /// <summary>Select a tab by its title (e.g. "Audio"); no-op if not found. Used for dev/CI capture.</summary>
    public void SelectTab(string title) => _tabs.SelectByTitle(title);

    protected override void BuildUi()
    {
        Name = "DialogSettings";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 18);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        if (!HostProvidesTitle) root.AddChild(MakeTitle("Settings"));

        // QC: row 1 = Video/Effects/Audio (2 columns each of 6), row 2 = Game/Input/User/Misc (1.5 each) —
        // both rows span the full dialog width.
        _tabs = new XonoticTabs();
        _tabs.AddRow();
        _tabs.AddTab("Video", new DialogSettingsVideo());
        _tabs.AddTab("Effects", new DialogSettingsEffects());
        _tabs.AddTab("Audio", new DialogSettingsAudio());
        _tabs.AddRow();
        _tabs.AddTab("Game", new DialogSettingsGame());
        _tabs.AddTab("Input", new DialogSettingsInput());
        _tabs.AddTab("User", new DialogSettingsUser());
        _tabs.AddTab("Misc", new DialogSettingsMisc());
        root.AddChild(_tabs);
    }

    public override void _ExitTree()
    {
        // DP archives changed (seta) cvars to config.cfg on shutdown; the dialog leaving the tree is the
        // closest equivalent for a pushed settings dialog (the nexposee panel persists via MainMenu instead).
        MenuState.SaveUserConfig();
    }
}
