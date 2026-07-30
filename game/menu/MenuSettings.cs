using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The menu's settings model + persistence + "apply to engine" layer. This is the C# stand-in for the
/// QC cvar layer that every <c>dialog_settings_*</c> tab read and wrote (the QC had no model object — the
/// engine cvars <em>were</em> the model, and "Apply" issued <c>vid_restart</c> / <c>snd_restart</c> /
/// <c>name</c> / <c>bind</c>). Here we keep an explicit object, serialise it to a Godot
/// <see cref="ConfigFile"/> at <see cref="ConfigPath"/>, and push the live values into Godot's
/// <see cref="DisplayServer"/> / <see cref="AudioServer"/> / <see cref="Input"/> on <see cref="Apply"/>.
///
/// One process-wide instance lives on <see cref="Current"/>; the settings screen mutates it, calls
/// <see cref="Apply"/> (engine effect) and <see cref="Save"/> (persistence), and other systems that need
/// a setting (player name, sensitivity, keybinds) can read it directly without going through the UI.
/// </summary>
public sealed class MenuSettings
{
    /// <summary>Where the config is persisted (under <see cref="UserPaths.BaseDir"/>, i.e. <c>~/XonData/settings.cfg</c>).</summary>
    public static string ConfigPath => UserPaths.Resolve("settings.cfg");

    // ----- the four setting groups (mirrors the QC settings tabs) -----
    public VideoSettings Video { get; } = new();
    public AudioSettings Audio { get; } = new();
    public InputSettings Input { get; } = new();
    public PlayerSettings Player { get; } = new();

    /// <summary>
    /// <strong>QUARANTINED (T15).</strong> A parallel keybind store that duplicated the engine bind table. The
    /// canonical, single source of truth is now <c>VortexArena.Engine.Console.BindTable</c>, seeded at boot from
    /// <c>binds-xonotic.cfg</c> via <c>BindInput.RegisterBindCommands</c> and persisted through the cvar/config
    /// dump (<c>MenuState.SaveUserConfig</c> → <c>BindInput.WriteBindings</c>). The live input-settings dialog
    /// (<see cref="DialogSettingsInput"/>) edits that table directly; this field is no longer read by any live
    /// path (only the orphaned <see cref="SettingsScreen"/> still touches it) and is kept solely so that legacy
    /// code compiles. Do NOT route new keybind work through here.
    /// </summary>
    [Obsolete("Keybinds live in VortexArena.Engine.Console.BindTable (seeded from binds-xonotic.cfg). Edit via BindInput, persist via MenuState.SaveUserConfig.")]
    public readonly Dictionary<string, string> Keybinds = new();

    // -------------------------------------------------------------------------------------------------
    //  Process-wide instance (lazily loaded from disk the first time anything asks for it).
    // -------------------------------------------------------------------------------------------------

    private static MenuSettings? _current;

    /// <summary>The shared settings instance, loaded from <see cref="ConfigPath"/> on first access.</summary>
    public static MenuSettings Current
    {
        get
        {
            if (_current is null)
            {
                _current = new MenuSettings();
                _current.Load();
            }
            return _current;
        }
    }

    private MenuSettings()
    {
        // Legacy: the quarantined Keybinds map is seeded from the thin defaults only so the orphaned
        // SettingsScreen still renders. The live keybind path is BindTable (seeded from binds-xonotic.cfg).
#pragma warning disable CS0618 // Type or member is obsolete (Keybinds is intentionally quarantined)
        foreach (var (action, key) in KeyBindings.Defaults)
            Keybinds[action] = key;
#pragma warning restore CS0618
    }

    // -------------------------------------------------------------------------------------------------
    //  Persistence  (ConfigFile <-> this object)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Load settings from <see cref="ConfigPath"/>, falling back to the in-memory defaults
    /// for anything missing or for a first run (no file yet).</summary>
    public void Load()
    {
        var cfg = new ConfigFile();
        Error err = cfg.Load(ConfigPath);
        if (err != Error.Ok)
            return; // first run / unreadable — keep constructed defaults

        // video
        Video.Width      = (int)cfg.GetValue("video", "width", Video.Width);
        Video.Height     = (int)cfg.GetValue("video", "height", Video.Height);
        Video.Fullscreen = (bool)cfg.GetValue("video", "fullscreen", Video.Fullscreen);
        Video.Vsync      = (bool)cfg.GetValue("video", "vsync", Video.Vsync);

        // audio
        Audio.Master  = (float)cfg.GetValue("audio", "master", Audio.Master);
        Audio.Music   = (float)cfg.GetValue("audio", "music", Audio.Music);
        Audio.Effects = (float)cfg.GetValue("audio", "effects", Audio.Effects);

        // input
        Input.Sensitivity = (float)cfg.GetValue("input", "sensitivity", Input.Sensitivity);
        Input.InvertY     = (bool)cfg.GetValue("input", "invert_y", Input.InvertY);

        // player
        Player.Name  = (string)cfg.GetValue("player", "name", Player.Name);
        Player.Model = (string)cfg.GetValue("player", "model", Player.Model);
        Player.Color = (int)cfg.GetValue("player", "color", Player.Color);

        // keybinds — QUARANTINED (T15): the canonical store is BindTable, persisted through user://config.cfg's
        // `bind` lines (MenuState.SaveUserConfig). We no longer read a [keybinds] section here, so a stale legacy
        // user://settings.cfg can't reintroduce a competing keybind store. (Any old section is simply ignored.)
    }

    /// <summary>Write the current settings to <see cref="ConfigPath"/>.</summary>
    public void Save()
    {
        var cfg = new ConfigFile();

        cfg.SetValue("video", "width", Video.Width);
        cfg.SetValue("video", "height", Video.Height);
        cfg.SetValue("video", "fullscreen", Video.Fullscreen);
        cfg.SetValue("video", "vsync", Video.Vsync);

        cfg.SetValue("audio", "master", Audio.Master);
        cfg.SetValue("audio", "music", Audio.Music);
        cfg.SetValue("audio", "effects", Audio.Effects);

        cfg.SetValue("input", "sensitivity", Input.Sensitivity);
        cfg.SetValue("input", "invert_y", Input.InvertY);

        cfg.SetValue("player", "name", Player.Name);
        cfg.SetValue("player", "model", Player.Model);
        cfg.SetValue("player", "color", Player.Color);

        // keybinds are NOT written here anymore (T15 quarantine): they live in BindTable and are persisted by
        // MenuState.SaveUserConfig as `bind` lines in user://config.cfg. Writing them here would re-create the
        // duplicate store this task removed.

        Error err = cfg.Save(ConfigPath);
        if (err != Error.Ok)
            GD.PushWarning($"[Menu] Failed to save settings to {ConfigPath}: {err}");
    }

    // -------------------------------------------------------------------------------------------------
    //  Apply  (push the model into the live engine — the QC "apply immediately" buttons)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Apply every group to the running engine (video mode, audio buses). Input keybinds are NOT
    /// applied here (T15 quarantine): the canonical bind table lives in <c>VortexArena.Engine.Console.BindTable</c>,
    /// driven by <c>BindInput</c> + the cvar/config dump — not Godot's <see cref="InputMap"/>.</summary>
    public void Apply()
    {
        ApplyVideo();
        ApplyAudio();
        // Player settings have no engine-side effect here; they're read by the client/net layer when a
        // match starts (name/model/color), so persisting them is the whole job.
    }

    /// <summary>Resolution + fullscreen + vsync into the <see cref="DisplayServer"/> (QC: vid_restart).</summary>
    public void ApplyVideo()
    {
        // Fullscreen toggles the window mode; in windowed mode we additionally honour the chosen size.
        DisplayServer.WindowSetMode(Video.Fullscreen
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed);

        if (!Video.Fullscreen && Video.Width > 0 && Video.Height > 0)
            DisplayServer.WindowSetSize(new Vector2I(Video.Width, Video.Height));

        DisplayServer.WindowSetVsyncMode(Video.Vsync
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);
    }

    /// <summary>Slider 0..1 volumes onto the audio buses (QC: mastervolume / bgmvolume / snd_*volume).</summary>
    public void ApplyAudio()
    {
        SetBusVolume("Master", Audio.Master);
        // "Music"/"SFX" only exist if the host project ships a bus layout with them; SetBusVolume is a
        // no-op when the bus is absent, so this is safe regardless of the project's audio configuration.
        SetBusVolume("Music", Audio.Music);
        SetBusVolume("SFX", Audio.Effects);
    }

    /// <summary>
    /// Convert a linear 0..1 volume to dB and set it on the named bus, if that bus exists. A volume of 0
    /// mutes the bus (rather than setting -inf dB which the engine clamps oddly).
    /// </summary>
    private static void SetBusVolume(string busName, float linear)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0)
            return; // bus not present in this project's layout

        bool mute = linear <= 0.0001f;
        AudioServer.SetBusMute(idx, mute);
        if (!mute)
            AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(Mathf.Clamp(linear, 0f, 1f)));
    }

    /// <summary>
    /// <strong>QUARANTINED (T15).</strong> Rebuilt Godot's <see cref="InputMap"/> from the legacy keybind table.
    /// No longer on any live path: gameplay input is driven by <c>VortexArena.Engine.Console.BindTable</c> (sampled
    /// directly in <c>NetGame.SampleInput</c> / <c>PlayerController</c>), not Godot input actions. Kept only for
    /// the orphaned <see cref="SettingsScreen"/>; not called by <see cref="Apply"/>.
    /// </summary>
    [Obsolete("Gameplay input reads BindTable directly, not Godot InputMap. This rebuild is dead — see DialogSettingsInput + BindInput.")]
    public void ApplyInput()
    {
#pragma warning disable CS0618 // Keybinds is intentionally quarantined
        foreach (var (action, key) in Keybinds)
        {
            string actionName = KeyBindings.InputActionName(action);

            if (!InputMap.HasAction(actionName))
                InputMap.AddAction(actionName);
            else
                InputMap.ActionEraseEvents(actionName);

            InputEvent? ev = KeyBindings.ToInputEvent(key);
            if (ev is not null)
                InputMap.ActionAddEvent(actionName, ev);
        }
#pragma warning restore CS0618
    }

    // sensitivity / invert-Y are read directly off Input by the player controller; nothing to push here.
}

// -----------------------------------------------------------------------------------------------------
//  The four setting groups. Plain data with sensible defaults (matched to the screen's defaults).
// -----------------------------------------------------------------------------------------------------

public sealed class VideoSettings
{
    public int Width = 1920;
    public int Height = 1080;
    public bool Fullscreen = true;
    public bool Vsync = true;
}

public sealed class AudioSettings
{
    public float Master = 1.0f;   // linear 0..1
    public float Music = 0.7f;
    public float Effects = 1.0f;
}

public sealed class InputSettings
{
    public float Sensitivity = 3.0f;
    public bool InvertY = false;
}

public sealed class PlayerSettings
{
    public string Name = "player";
    public string Model = "Erebus";
    public int Color = 2; // index into the menu's color list (2 = Orange)
}
