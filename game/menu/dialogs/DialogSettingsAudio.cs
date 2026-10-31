using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Audio settings tab — <c>XonoticAudioSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_audio.qc), TD for
/// TD on its 15.5 x 6.2 grid. The volumes are the QC's decibel sliders (<see cref="QcDecibelsSlider"/>: -40..0 dB in
/// 0.4 dB steps over the linear 0..1 cvars), each greyed while the master volume is off.
///
/// <para>"Apply immediately" runs <c>snd_restart</c> (the QC also runs its <c>snd_attenuation_method_N</c> alias,
/// which this client's sound system has no counterpart for), live only while an output setting it applies has
/// changed (<see cref="ClientSettings.AudioApplyCvars"/>).</para>
/// </summary>
public partial class DialogSettingsAudio : QcTab
{
    public DialogSettingsAudio() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    private static QcDecibelsSlider Volume(string cvar) => QcLook.Slider(new QcDecibelsSlider(-40, 0, 0.4f, cvar));

    protected override void Fill()
    {
        var apply = Qc.Command("Apply immediately", "snd_restart");
        PendingApply.Bind(apply, ClientSettings.AudioApplyCvars, string.Empty);

        TR();
            TD(1, 1, Qc.Label("Master:"));
            TD(1, 2, Volume("mastervolume"));
        // (label, cvar, makeMulti) per channel row, in the QC order; each greyed while master is off.
        foreach ((string label, string cvar, string? multi) in new (string, string, string?)[]
                 {
                     ("Music:", "bgmvolume", "snd_channel8volume"),
                     ("Ambient:", "snd_staticvolume", "snd_channel9volume"),
                     ("Info:", "snd_channel0volume", null),
                     ("Items:", "snd_channel3volume", null),
                     ("Pain:", "snd_channel6volume", null),
                     ("Player:", "snd_channel7volume", null),
                     ("Shots:", "snd_channel4volume", null),
                     ("Voice:", "snd_channel2volume", null),
                     ("Weapons:", "snd_channel1volume", "snd_channel5volume"), // @!#%'n Tuba
                 })
        {
            TR();
                TDempty(0.2f);
                QcDecibelsSlider s = Volume(cvar).DepNot("mastervolume", 0);
                if (multi is not null) s.Multi(cvar, multi);
                TD(1, 0.8f, Qc.Label(label)).DepNot("mastervolume", 0);
                TD(1, 2, s);
        }
        TR();
        TR();
            TD(1, 3, Qc.Check(0, "menu_snd_attenuation_method", "New style sound attenuation"));
        TR();
            TD(1, 3, Qc.Check(0, "snd_mutewhenidle", "Mute sounds when not active"));

        GotoRC(0, 3.2f); SetFirstColumn(CurrentColumn);
            TD(1, 1, Qc.Label("Frequency:"));
            TD(1, 2, Qc.Mixed("snd_speed", "Sound output frequency")
                .Add("8 kHz", 8000).Add("11.025 kHz", 11025).Add("16 kHz", 16000).Add("22.05 kHz", 22050)
                .Add("24 kHz", 24000).Add("32 kHz", 32000).Add("44.1 kHz", 44100).Add("48 kHz", 48000).Done());
        TR();
            TD(1, 1, Qc.Label("Channels:"));
            TD(1, 2, Qc.Mixed("snd_channels", "Number of channels for the sound output")
                .Add("Mono", 1).Add("Stereo", 2).Add("2.1", 3).Add("4", 4).Add("5", 5).Add("5.1", 6).Add("6.1", 7).Add("7.1", 8)
                .Done());
        TR();
        TR();
            TD(1, 3, Qc.Check(0, "snd_swapstereo", "Swap stereo output channels", "Swap left/right channels"))
                .DepNot("snd_channels", 1);
        TR();
            TD(1, 3, Qc.Check(0, "snd_spatialization_control", "Headphone-friendly mode",
                "Blend the left and right channels slightly to decrease stereo separation a bit")).DepNot("snd_channels", 1);
        TR();
        TR();
            TD(1, 3, Qc.Check(0, "cl_hitsound", "Hit indication sound", "Play a hit indicator sound when your shot hits an enemy"));
        TR();
            TDempty(0.2f);
            var hitsound = new QcRadioGroup();
            TD(1, 2.8f / 3, Qc.Radio(hitsound, "cl_hitsound", "1", "Fixed")).Dep("cl_hitsound", 1, 3);
            TD(1, 2.8f / 3, Qc.Radio(hitsound, "cl_hitsound", "2", "Decreasing", "Decrease pitch with more damage")).Dep("cl_hitsound", 1, 3);
            TD(1, 2.8f / 3, Qc.Radio(hitsound, "cl_hitsound", "3", "Increasing", "Increase pitch with more damage")).Dep("cl_hitsound", 1, 3);
        TR();
            TD(1, 3, Qc.Check(0, "con_chatsound", "Chat message sound"));
        TR();
            TD(1, 1.2f, Qc.Check(0, "menu_sounds", "Menu sounds", "Play sounds when clicking menu items"));
            // linkedCheckBox: both edit menu_sounds, and each re-reads it when the other changes it.
            TD(1, 1.2f, Qc.CheckEx(2, 1, "menu_sounds", "Focus sounds", "Play sounds when hovering over menu items too"))
                .Dep("menu_sounds", 1, 2);
        TR();
        TR();
            TD(1, 1, Qc.Label("Time announcer:"));
            TD(1, 2, Qc.Mixed("cl_announcer_maptime", "", "m").Add("Disabled", 0).AddRange(1, 5, 4).Add("Both", 3).Done());
        TR();
            TD(1, 1, Qc.Label("Automatic taunts:"));
            TD(1, 2, Qc.Mixed("cl_autotaunt", "Automatically taunt enemies after fragging them")
                .Add("Never", 0).Add("Sometimes", 0.35f).Add("Often", 0.65f).Add("Always", 1).Done());

        GotoRC(Rows - 1, 0);
            TD(1, Columns, apply);
    }
}
