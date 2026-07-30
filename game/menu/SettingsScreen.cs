using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Settings screen: a <see cref="TabContainer"/> with Video / Audio / Input / Player tabs and an
/// Apply / Back bar. C# successor to <c>XonoticSettingsDialog</c> and its per-category tabs
/// (dialog_settings.qc + dialog_settings_video/audio/input/game_model.qc).
///
/// <para><strong>ORPHANED + keybinds QUARANTINED (T15).</strong> Nothing constructs this screen — the live
/// front-end uses the nexposee <c>DialogSettings*</c> dialogs (<see cref="DialogSettingsInput"/> is the live
/// input tab, which now edits the canonical <c>VortexArena.Engine.Console.BindTable</c> via <c>BindInput</c>). The
/// Input tab here still talks to the legacy <see cref="MenuSettings.Keybinds"/> store, which is no longer read
/// by any gameplay path; its capture is inert downstream. Kept for its video/audio/player tabs; do NOT revive
/// the keybind capture through <see cref="MenuSettings"/> — route it through <c>BindInput</c> like
/// <see cref="DialogSettingsInput"/> if this screen is ever brought back.</para>
/// </summary>
public partial class SettingsScreen : MenuScreen
{
    // --- Video ---
    private OptionButton _resolution = null!;
    private CheckBox _fullscreen = null!;
    private CheckBox _vsync = null!;

    // --- Audio ---
    private HSlider _masterVol = null!;
    private HSlider _musicVol = null!;
    private HSlider _effectsVol = null!;

    // --- Input ---
    private HSlider _sensitivity = null!;
    private Label _sensitivityValue = null!;
    private CheckBox _invertY = null!;

    // --- Player ---
    private LineEdit _playerName = null!;
    private OptionButton _playerModel = null!;
    private OptionButton _playerColor = null!;

    // The live settings model (loaded from disk on first access).
    private MenuSettings Settings => MenuSettings.Current;

    // Common 16:9 / 16:10 / 4:3 resolutions (stand-in for the engine's enumerated mode list).
    // Stored as explicit (w,h) so Apply can push exact pixel sizes to the window.
    private static readonly (int W, int H)[] Resolutions =
    {
        (1280, 720), (1366, 768), (1600, 900), (1920, 1080),
        (2560, 1440), (3840, 2160), (1280, 800), (1680, 1050), (1024, 768),
    };

    private static readonly string[] PlayerModels = { "Erebus", "Megaerebus", "Nyx", "Pyria", "Seraphina" };

    // Xonotic team/player color names (0..15 in the QC color picker).
    private static readonly string[] PlayerColors =
    {
        "Pink", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple",
        "White", "Light Grey", "Grey", "Dark Grey", "Brown", "Olive", "Teal", "Black",
    };

    protected override void BuildUi()
    {
        Name = "SettingsScreen";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Settings"));

        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(tabs);

        AddTab(tabs, "Video", BuildVideoTab());
        AddTab(tabs, "Audio", BuildAudioTab());
        AddTab(tabs, "Input", BuildInputTab());
        AddTab(tabs, "Player", BuildPlayerTab());

        root.AddChild(MakeButtonBar(
            MakeButton("Apply", OnApply),
            MakeButton("Back", GoBack)));

        // Pull the persisted values into every control now that they all exist.
        LoadIntoControls();
    }

    private static void AddTab(TabContainer tabs, string title, Control content)
    {
        content.Name = title;
        tabs.AddChild(content);
    }

    /// <summary>Wrap a tab body in scroll + margin so long tabs stay usable.</summary>
    private static VBoxContainer TabBody(out ScrollContainer scroll)
    {
        scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var inner = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            inner.AddThemeConstantOverride(side, 16);
        scroll.AddChild(inner);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 10);
        inner.AddChild(box);
        return box;
    }

    // -------------------------------------------------------------------------------------------------
    //  Video (dialog_settings_video.qc)
    // -------------------------------------------------------------------------------------------------

    private Control BuildVideoTab()
    {
        var box = TabBody(out var scroll);

        box.AddChild(MakeHeader("Display"));

        _resolution = new OptionButton();
        for (int i = 0; i < Resolutions.Length; i++)
            _resolution.AddItem($"{Resolutions[i].W} x {Resolutions[i].H}", i);
        box.AddChild(MakeRow("Resolution:", _resolution));

        _fullscreen = new CheckBox { Text = "Full screen" };
        box.AddChild(_fullscreen);

        _vsync = new CheckBox { Text = "Vertical synchronization" };
        box.AddChild(_vsync);

        return scroll;
    }

    // -------------------------------------------------------------------------------------------------
    //  Audio (dialog_settings_audio.qc)
    // -------------------------------------------------------------------------------------------------

    private Control BuildAudioTab()
    {
        var box = TabBody(out var scroll);

        box.AddChild(MakeHeader("Volume"));

        _masterVol = MakeVolumeSlider();
        box.AddChild(MakeRow("Master:", WithValueReadout(_masterVol)));

        _musicVol = MakeVolumeSlider();
        box.AddChild(MakeRow("Music:", WithValueReadout(_musicVol)));

        _effectsVol = MakeVolumeSlider();
        box.AddChild(MakeRow("Effects:", WithValueReadout(_effectsVol)));

        return scroll;
    }

    private static HSlider MakeVolumeSlider() => new()
    {
        MinValue = 0, MaxValue = 1, Step = 0.05,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ShrinkCenter,
    };

    /// <summary>Pack a slider with a live 0–100% readout into one row control.</summary>
    private static HBoxContainer WithValueReadout(HSlider slider)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var value = MakeLabel($"{Mathf.RoundToInt((float)slider.Value * 100)}%");
        value.CustomMinimumSize = new Vector2(44, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        slider.ValueChanged += v => value.Text = $"{Mathf.RoundToInt((float)v * 100)}%";
        row.AddChild(slider);
        row.AddChild(value);
        return row;
    }

    // -------------------------------------------------------------------------------------------------
    //  Input (dialog_settings_input.qc)
    // -------------------------------------------------------------------------------------------------

    private Control BuildInputTab()
    {
        var box = TabBody(out var scroll);

        box.AddChild(MakeHeader("Mouse"));

        var sensRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sensitivity = new HSlider
        {
            MinValue = 0.1, MaxValue = 9.9, Step = 0.1,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _sensitivityValue = MakeLabel("3.0");
        _sensitivityValue.CustomMinimumSize = new Vector2(40, 0);
        _sensitivityValue.HorizontalAlignment = HorizontalAlignment.Right;
        _sensitivity.ValueChanged += v => _sensitivityValue.Text = ((float)v).ToString("0.0");
        sensRow.AddChild(_sensitivity);
        sensRow.AddChild(_sensitivityValue);
        box.AddChild(MakeRow("Sensitivity:", sensRow));

        _invertY = new CheckBox { Text = "Invert aiming (Y axis)" };
        box.AddChild(_invertY);

        // Full keybind list with real capture — clicking a row arms it and the next key/mouse press binds.
        // (Legacy/orphaned: writes the quarantined MenuSettings.Keybinds store; inert downstream — see class doc.)
        box.AddChild(MakeHeader("Key Bindings"));
#pragma warning disable CS0618 // MenuSettings.Keybinds is intentionally quarantined (T15)
        foreach (var (id, label) in KeyBindings.Actions)
        {
            string current = Settings.Keybinds.TryGetValue(id, out string? b) ? b : "";
            var button = new KeyCaptureButton(id, current);
            button.BindCaptured += OnBindCaptured;
            box.AddChild(MakeRow(label + ":", button));
        }
#pragma warning restore CS0618

        // "Reset to defaults" for the binds (the QC keybinder has the same affordance).
        box.AddChild(MakeButtonBar(MakeButton("Reset binds to defaults", OnResetBinds)));

        return scroll;
    }

    /// <summary>A capture finished: store the new bind in the legacy (quarantined) model. Inert downstream —
    /// the live keybind path is BindTable via DialogSettingsInput (see class doc).</summary>
    private void OnBindCaptured(string actionId, string bind)
    {
#pragma warning disable CS0618 // MenuSettings.Keybinds is intentionally quarantined (T15)
        Settings.Keybinds[actionId] = bind;
#pragma warning restore CS0618
        GD.Print($"[Menu] Bound '{actionId}' -> {bind} (legacy SettingsScreen; inert — use the in-game input dialog)");
    }

    private void OnResetBinds()
    {
#pragma warning disable CS0618 // MenuSettings.Keybinds is intentionally quarantined (T15)
        foreach (var (id, key) in KeyBindings.Defaults)
            Settings.Keybinds[id] = key;
#pragma warning restore CS0618
        // Rebuild the Input tab so the button faces reflect the restored defaults.
        RefreshKeybindButtons();
    }

    /// <summary>Walk the tree and push the model's bind into each <see cref="KeyCaptureButton"/>.</summary>
    private void RefreshKeybindButtons()
    {
#pragma warning disable CS0618 // MenuSettings.Keybinds is intentionally quarantined (T15)
        foreach (KeyCaptureButton button in FindKeyCaptureButtons(this))
            if (Settings.Keybinds.TryGetValue(button.ActionId, out string? b))
                button.Bind = b;
#pragma warning restore CS0618
    }

    private static System.Collections.Generic.IEnumerable<KeyCaptureButton> FindKeyCaptureButtons(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is KeyCaptureButton kcb)
                yield return kcb;
            foreach (var nested in FindKeyCaptureButtons(child))
                yield return nested;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Player (dialog_multiplayer_profile.qc + dialog_settings_game_model.qc)
    // -------------------------------------------------------------------------------------------------

    private Control BuildPlayerTab()
    {
        var box = TabBody(out var scroll);

        box.AddChild(MakeHeader("Profile"));

        _playerName = new LineEdit { PlaceholderText = "your name" };
        box.AddChild(MakeRow("Name:", _playerName));

        _playerModel = new OptionButton();
        for (int i = 0; i < PlayerModels.Length; i++)
            _playerModel.AddItem(PlayerModels[i], i);
        box.AddChild(MakeRow("Model:", _playerModel));

        _playerColor = new OptionButton();
        for (int i = 0; i < PlayerColors.Length; i++)
            _playerColor.AddItem(PlayerColors[i], i);
        box.AddChild(MakeRow("Color:", _playerColor));

        return scroll;
    }

    // -------------------------------------------------------------------------------------------------
    //  Load (model -> controls) and Apply (controls -> model -> engine + disk)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Initialise every control from the persisted <see cref="MenuSettings"/>.</summary>
    private void LoadIntoControls()
    {
        // Video
        _resolution.Select(NearestResolutionIndex(Settings.Video.Width, Settings.Video.Height));
        _fullscreen.ButtonPressed = Settings.Video.Fullscreen;
        _vsync.ButtonPressed = Settings.Video.Vsync;

        // Audio (ValueChanged fires here, updating the % readouts).
        _masterVol.Value = Settings.Audio.Master;
        _musicVol.Value = Settings.Audio.Music;
        _effectsVol.Value = Settings.Audio.Effects;

        // Input
        _sensitivity.Value = Settings.Input.Sensitivity;
        _invertY.ButtonPressed = Settings.Input.InvertY;

        // Player
        _playerName.Text = Settings.Player.Name;
        _playerModel.Select(IndexOf(PlayerModels, Settings.Player.Model, 0));
        _playerColor.Select(Mathf.Clamp(Settings.Player.Color, 0, PlayerColors.Length - 1));
    }

    private void OnApply()
    {
        // Pull each control's value back into the model.
        var res = Resolutions[Mathf.Clamp(_resolution.Selected, 0, Resolutions.Length - 1)];
        Settings.Video.Width = res.W;
        Settings.Video.Height = res.H;
        Settings.Video.Fullscreen = _fullscreen.ButtonPressed;
        Settings.Video.Vsync = _vsync.ButtonPressed;

        Settings.Audio.Master = (float)_masterVol.Value;
        Settings.Audio.Music = (float)_musicVol.Value;
        Settings.Audio.Effects = (float)_effectsVol.Value;

        Settings.Input.Sensitivity = (float)_sensitivity.Value;
        Settings.Input.InvertY = _invertY.ButtonPressed;

        Settings.Player.Name = _playerName.Text;
        Settings.Player.Model = PlayerModels[Mathf.Clamp(_playerModel.Selected, 0, PlayerModels.Length - 1)];
        Settings.Player.Color = Mathf.Clamp(_playerColor.Selected, 0, PlayerColors.Length - 1);
        // (Keybinds are already written into Settings.Keybinds as the user captured them.)

        // Push to the live engine, then persist.
        Settings.Apply();
        Settings.Save();

        GD.Print($"[Menu] Settings applied + saved to {MenuSettings.ConfigPath} " +
                 $"({Settings.Video.Width}x{Settings.Video.Height}, fullscreen={Settings.Video.Fullscreen}, " +
                 $"player='{Settings.Player.Name}').");
    }

    // -------------------------------------------------------------------------------------------------
    //  Small helpers
    // -------------------------------------------------------------------------------------------------

    private static int NearestResolutionIndex(int w, int h)
    {
        for (int i = 0; i < Resolutions.Length; i++)
            if (Resolutions[i].W == w && Resolutions[i].H == h)
                return i;
        return 3; // default 1920x1080 if the stored size isn't in the list
    }

    private static int IndexOf(string[] arr, string value, int fallback)
    {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] == value)
                return i;
        return fallback;
    }
}
