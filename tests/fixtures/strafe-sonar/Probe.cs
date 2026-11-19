using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Audio;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Audio;
using VortexArena.Game.Hud;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using Vector3 = System.Numerics.Vector3;

public partial class Probe : Node
{
    private const string Prefix = "hud_panel_strafehud";
    private static readonly MethodInfo Update = typeof(StrafeHudPanel).GetMethod(
        "UpdateSonar", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public override async void _Ready()
    {
        try
        {
            await Run();
            GD.Print("Strafe sonar probe passed");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr(ex);
            GetTree().Quit(1);
        }
    }

    private async Task Run()
    {
        HudConfig.RegisterDefaults(MenuState.Cvars);
        string[] defaults = ["0", "misc/talk", "0.5", "0.333333", "-0.222222", "1",
            "0.333333", "0.666666", "1", "0.9", "0.1", "1"];
        string[] cvars = ["", "_audio", "_start", "_interval_start", "_interval_range", "_interval_exponent",
            "_volume_start", "_volume_range", "_volume_exponent", "_pitch_start", "_pitch_range", "_pitch_exponent"];
        for (int i = 0; i < cvars.Length; i++)
            Check(MenuState.Cvars.GetString(Prefix + "_sonar" + cvars[i]) == defaults[i], "default " + cvars[i]);

        using var vfs = new VirtualFileSystem();
        Check(vfs.Mount(ProjectSettings.GlobalizePath("res://fixture")), "fixture mount");
        var assets = new AssetLoader(vfs);
        Func<string, AudioStream?> load = assets.LoadSoundFile;
        var panel = new StrafeHudPanel { SonarAssets = assets };
        Set("snd_darkplaces", "1");
        Set("mastervolume", "1");
        Set("volume", "1");
        Set("snd_softclip", "0");
        DpNative.Configure(MenuState.Cvars);
        Check(DpNative.Active, "native mixer active");
        DpSoundSystem sound = DpAudio.Instance.Sound;

        // Live draw uses the panel's own ratio with a deterministic airborne player and view angle.
        Set(Prefix, "1");
        Set("sv_maxairspeed", "320");
        Set("sv_airaccelerate", "1");
        Set("sys_ticrate", (1f / 60f).ToString("R", CultureInfo.InvariantCulture));
        const float speed = 640f;
        float best = MathF.Acos((320f - 320f / 60f) / speed) * 180f / MathF.PI;
        panel.Player = new Player { Velocity = new Vector3(speed, 0, 0), Angles = new Vector3(0, 45f - best, 0) };
        panel.Configure(new Rect2(10, 10, 240, 20));
        AddChild(panel);
        await DrawFrames(panel);
        Check(Channels(sound).Length == 0, "disabled by default during HUD draw");
        Set(Prefix + "_sonar", "1");
        Set(Prefix, "0");
        panel.Visible = panel.ResolveVisible(new HudShowContext(false, false, false));
        await DrawFrames(panel);
        Check(Channels(sound).Length == 0, "hidden HUD stays silent");
        Set(Prefix, "1");
        panel.Visible = panel.ResolveVisible(new HudShowContext(false, false, false));
        panel.SonarSimulationTime = 2;
        await DrawFrames(panel);
        var wav = DpNative.Bank(load)!.Get("sound/misc/talk.wav", false)!;
        int live = Channels(sound).Single();
        Check(sound.IsChannelPlaying(live, wav, DpSoundSystem.MaxEdicts, 0), "HUD draws into native CH_INFO with WAV precedence");
        MixAndCheck(sound, live, 0.699f, 1f);
        sound.StopChannel(live);
        Set(Prefix + "_sonar_interval_start", "0.001");
        Set(Prefix + "_sonar_interval_range", "0");
        await DrawFrames(panel);
        Check(Channels(sound).Length == 0, "paused simulation keeps the cooldown through rendered frames");
        panel.SonarSimulationTime = 2.002;
        await DrawFrames(panel);
        int resumed = Channels(sound).Single();
        sound.StopChannel(resumed);
        Set(Prefix + "_sonar_interval_start", "0.333333");
        Set(Prefix + "_sonar_interval_range", "-0.222222");
        panel.QueueFree();

        // Drive the same callback with explicit QC ratios and times for edge cases and ramps.
        var timed = new StrafeHudPanel { SonarAssets = assets };
        void Step(float ratio, double time) => Update.Invoke(timed, [ratio, time]);
        Step(-0.75f, 1);
        Step(0.499f, 1);
        Check(Channels(sound).Length == 0, "threshold uses the signed ratio");
        Step(0.5f, 1);
        int threshold = Channels(sound).Single();
        MixAndCheck(sound, threshold, 0.333333f * 0.7f, 0.9f);
        Step(0.5f, 1.2);
        Check(Channels(sound).SequenceEqual([threshold]), "cooldown at the threshold");

        Set(Prefix + "_sonar_interval_exponent", "2");
        Set(Prefix + "_sonar_volume_exponent", "2");
        Set(Prefix + "_sonar_pitch_exponent", "2");
        Step(0.75f, 1.27);
        Check(Channels(sound).SequenceEqual([threshold]), "quadratic interval still cooling down");
        Step(0.75f, 1.28);
        int midpoint = Channels(sound).Single(channel => channel != threshold);
        MixAndCheck(sound, midpoint, (0.333333f + 0.666666f * 0.25f) * 0.7f, 0.925f);
        foreach (int channel in Channels(sound))
            sound.StopChannel(channel);

        Set(Prefix + "_sonar_audio", "");
        Step(1, 2);
        Check(Channels(sound).Length == 0, "empty sample silences the cue");
        Set(Prefix + "_sonar_audio", "misc/missing");
        Step(1, 3);
        Check(Channels(sound).Length == 0, "missing sample stays silent");
        Set(Prefix + "_sonar_audio", "misc/alternate");
        Step(1, 3.1);
        Check(Channels(sound).Length == 0, "silent qualifying cues consume cooldown");
        Step(1, 3.12);
        var ogg = DpNative.Bank(load)!.Get("sound/misc/alternate.ogg", false)!;
        int alternate = Channels(sound).Single();
        Check(sound.IsChannelPlaying(alternate, ogg, DpSoundSystem.MaxEdicts, 0), "live sample change uses OGG fallback");
        sound.StopChannel(alternate);

        Set(Prefix + "_sonar_audio", "misc/talk");
        Set(Prefix + "_sonar_volume_start", "0");
        Set(Prefix + "_sonar_volume_range", "0");
        Step(1, 3.5);
        Check(Channels(sound).Length == 0, "zero volume stays silent");

        Set(Prefix + "_sonar_start", "2");
        Set(Prefix + "_sonar_volume_start", "3");
        Step(0.999f, 4);
        Check(Channels(sound).Length == 0, "threshold clamps to 1");
        Step(1, 4);
        int optimal = Channels(sound).Single();
        MixAndCheck(sound, optimal, 0.7f, 1f);
        sound.StopChannel(optimal);
        Set("snd_startnonloopingsounds", "0");
        DpNative.ReadSettings();
        Step(1, 4.5);
        Check(Channels(sound).Length == 0 && timed.GetChildCount() == 0, "mixer start gate cannot fall through to SFX");
        timed.Free();

        // The alternate backend retains the same resolved file and cue parameters.
        AudioServer.AddBus();
        AudioServer.SetBusName(AudioServer.BusCount - 1, "SFX");
        Set("snd_darkplaces", "0");
        DpNative.Configure(MenuState.Cvars);
        var fallback = new StrafeHudPanel { SonarAssets = assets };
        AddChild(fallback);
        Update.Invoke(fallback, [1f, 5d]);
        var cue = (AudioStreamPlayer)fallback.GetChild(0);
        Check(cue.Playing && cue.Stream is AudioStreamWav && cue.Bus == "SFX", "engine fallback plays the resolved WAV on SFX");
        Check(MathF.Abs(cue.PitchScale - 1f) < 0.001f && MathF.Abs(cue.VolumeDb - Mathf.LinearToDb(0.7f)) < 0.001f,
            "engine fallback applies the same pitch and volume");
        Set(Prefix + "_sonar_audio", "misc/broken");
        Update.Invoke(fallback, [1f, 5.12d]);
        Check(((AudioStreamPlayer)fallback.GetChild(1)).Stream is AudioStreamOggVorbis, "invalid WAV falls back to OGG on SFX");
        Check(Channels(sound).Length == 0, "engine fallback does not also start mixer channels");
        using var empty = new VirtualFileSystem();
        fallback.SonarAssets = new AssetLoader(empty);
        Update.Invoke(fallback, [1f, 6d]);
        Check(fallback.GetChildCount() == 2, "a replacement mount cannot replay the previous stream");
        fallback.SonarAssets = assets;
        Update.Invoke(fallback, [1f, 7d]);
        Check(fallback.GetChildCount() == 3, "restoring the mount resolves the same configured sound again");
        foreach (var player in fallback.GetChildren().OfType<AudioStreamPlayer>())
            player.Stop();
        foreach (var player in DpAudio.Instance.GetChildren().OfType<AudioStreamPlayer>())
            player.Stop();
        // Give the Dummy driver a real audio block to release its stopped playbacks.
        await Task.Delay(200);
        fallback.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task DrawFrames(StrafeHudPanel panel)
    {
        panel.QueueRedraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void MixAndCheck(DpSoundSystem sound, int channel, float volume, float pitch)
    {
        Check(sound.TryGetChannelVolumes(channel, out float left, out float right), "active cue");
        Check(MathF.Abs(left - volume) < 0.002f && left == right, $"unattenuated volume {left}, expected {volume}");
        const int frames = 480, rate = 48000;
        float before = sound.GetChannelPosition(channel);
        var pcm = new float[frames * 2];
        sound.Mix(pcm, frames, rate);
        float played = sound.GetChannelPosition(channel) - before;
        Check(MathF.Abs(played - frames * pitch / rate) < 0.00002f, $"mixed pitch {played * rate / frames}, expected {pitch}");
        Check(pcm.Any(sample => sample != 0) && pcm.Where((_, index) => index % 2 == 0)
            .SequenceEqual(pcm.Where((_, index) => index % 2 == 1)), "nonzero centered PCM");
    }

    private static int[] Channels(DpSoundSystem sound)
        => Enumerable.Range(0, DpSoundSystem.MaxDynamicChannels).Where(sound.IsChannelActive).ToArray();

    private static void Set(string name, string value) => MenuState.Cvars.Set(name, value);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
