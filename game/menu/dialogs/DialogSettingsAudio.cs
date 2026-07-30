using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Audio settings tab — a faithful C# port of <c>XonoticAudioSettingsTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings_audio.qc). Every control binds the same engine cvar the QC binds, with
/// the same dependencies (the volume rows grey out when master is muted) and the same "Apply immediately"
/// command button (<c>snd_restart</c>).
///
/// THIS IS THE REFERENCE PATTERN for porting a settings tab:
///   * subclass <see cref="SettingsTab"/>, override <see cref="Fill"/>;
///   * one <c>box.AddChild(Ui.Row("Label:", Widgets.Xxx("cvar", …)))</c> per QC <c>me.TD(... makeXonotic…)</c>;
///   * <see cref="Dependent.BindNot"/> / <see cref="Dependent.Bind"/> for QC <c>setDependentNOT/​setDependent</c>;
///   * <see cref="Widgets.CommandButton"/> for the apply button, referencing the same command string.
/// The QC stores volumes as decibels in the slider but the cvars (mastervolume, snd_channelNvolume, …) are
/// linear 0..1 (DP), so we use linear 0..1 sliders with a percent readout — same cvar, same effect.
/// </summary>
public partial class DialogSettingsAudio : SettingsTab
{
    // (label, cvar) for the per-channel volume sliders, in the QC order. All depend on master being unmuted.
    private static readonly (string Label, string Cvar)[] Volumes =
    {
        ("Master:",  "mastervolume"),
        ("Music:",   "bgmvolume"),
        ("Ambient:", "snd_staticvolume"),
        ("Info:",    "snd_channel0volume"),
        ("Items:",   "snd_channel3volume"),
        ("Pain:",    "snd_channel6volume"),
        ("Player:",  "snd_channel7volume"),
        ("Shots:",   "snd_channel4volume"),
        ("Voice:",   "snd_channel2volume"),
        ("Weapons:", "snd_channel1volume"),
    };

    protected override void Fill(VBoxContainer box)
    {
        box.AddChild(Ui.Header("Volume"));

        for (int i = 0; i < Volumes.Length; i++)
        {
            (string label, string cvar) = Volumes[i];
            CvarSlider slider = Widgets.Slider(cvar, 0f, 1f, 0.05f, format: Percent);
            HBoxContainer row = Ui.Row(label, slider);
            box.AddChild(row);
            // Every non-master volume greys out while master is muted (QC setDependentNOT(s,"mastervolume",0)).
            if (cvar != "mastervolume")
                Dependent.BindNot(row, "mastervolume", 0);
        }

        box.AddChild(Ui.Spacer());

        var apply = Widgets.CommandButton("Apply immediately", "snd_restart");
        box.AddChild(Widgets.CheckBox("menu_snd_attenuation_method", "New style sound attenuation"));
        box.AddChild(Widgets.CheckBox("snd_mutewhenidle", "Mute sounds when not active"));

        box.AddChild(Ui.Spacer());
        box.AddChild(Ui.Header("Output"));

        // Frequency — QC mixedslider snd_speed.
        var freq = Widgets.TextSlider("snd_speed", "Sound output frequency")
            .Add("8 kHz", 8000).Add("11.025 kHz", 11025).Add("16 kHz", 16000).Add("22.05 kHz", 22050)
            .Add("24 kHz", 24000).Add("32 kHz", 32000).Add("44.1 kHz", 44100).Add("48 kHz", 48000);
        box.AddChild(Ui.Row("Frequency:", freq));

        // Channels — QC mixedslider snd_channels.
        var chans = Widgets.TextSlider("snd_channels", "Number of channels for the sound output")
            .Add("Mono", 1).Add("Stereo", 2).Add("2.1", 3).Add("4", 4)
            .Add("5", 5).Add("5.1", 6).Add("6.1", 7).Add("7.1", 8);
        box.AddChild(Ui.Row("Channels:", chans));

        var swap = Widgets.CheckBox("snd_swapstereo", "Swap stereo output channels", "Swap left/right channels");
        box.AddChild(swap);
        Dependent.BindNot(swap, "snd_channels", 1); // QC setDependentNOT(e,"snd_channels",1)

        var headphone = Widgets.CheckBox("snd_spatialization_control", "Headphone-friendly mode",
            "Blend the left and right channels slightly to decrease stereo separation a bit");
        box.AddChild(headphone);
        Dependent.BindNot(headphone, "snd_channels", 1);

        box.AddChild(Ui.Spacer());

        // Hit indication sound + its pitch mode (QC checkbox + 3-way radio, dependent on the checkbox).
        box.AddChild(Widgets.CheckBox("cl_hitsound", "Hit indication sound",
            "Play a hit indicator sound when your shot hits an enemy"));
        var hitGroup = new ButtonGroup();
        var hitRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hitRow.AddThemeConstantOverride("separation", 8);
        hitRow.AddChild(Widgets.RadioButton("cl_hitsound", "1", "Fixed", hitGroup));
        hitRow.AddChild(Widgets.RadioButton("cl_hitsound", "2", "Decreasing", hitGroup, "Decrease pitch with more damage"));
        hitRow.AddChild(Widgets.RadioButton("cl_hitsound", "3", "Increasing", hitGroup, "Increase pitch with more damage"));
        box.AddChild(hitRow);
        Dependent.Bind(hitRow, "cl_hitsound", 1, 3); // QC setDependent(e,"cl_hitsound",1,3)

        box.AddChild(Widgets.CheckBox("con_chatsound", "Chat message sound"));

        // Menu sounds + focus sounds (QC: menu_sounds 1=click, 2=click+hover).
        box.AddChild(Widgets.CheckBox("menu_sounds", "Menu sounds", "Play sounds when clicking menu items"));
        var focus = Widgets.CheckBox("menu_sounds", "Focus sounds", "Play sounds when hovering over menu items too",
            on: "2", off: "1");
        box.AddChild(focus);
        Dependent.Bind(focus, "menu_sounds", 1, 2);

        box.AddChild(Ui.Spacer());
        box.AddChild(Ui.Header("Announcer"));

        var maptime = Widgets.TextSlider("cl_announcer_maptime")
            .Add("Disabled", 0).Add("1 min", 1).Add("5 min", 5).Add("Both", 3);
        box.AddChild(Ui.Row("Time announcer:", maptime));

        var taunt = Widgets.TextSlider("cl_autotaunt", "Automatically taunt enemies after fragging them")
            .Add("Never", 0).Add("Sometimes", 0.35f).Add("Often", 0.65f).Add("Always", 1);
        box.AddChild(Ui.Row("Automatic taunts:", taunt));

        box.AddChild(Ui.Spacer());
        box.AddChild(apply); // "Apply immediately" — snd_restart
    }

    /// <summary>Render a linear 0..1 volume as a percentage (the readout in place of the QC dB display).</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}
