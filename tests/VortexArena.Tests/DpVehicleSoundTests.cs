// The native game's vehicle sounds as calls on DarkPlaces' mixer (src/VortexArena.Engine/Audio/DpVehicleSounds.cs),
// checked against what Xonotic's QuakeC gives them (qcsrc/common/vehicles: VOL_VEHICLEENGINE 1, ATTEN_NORM 0.5,
// VOL_BASE 0.7, VOL_BASEVOICE 1, ATTEN_NONE, CH_TRIGGER_SINGLE 3, CH_PAIN_SINGLE 6, CH_SHOTS -4) and against
// DarkPlaces' own arithmetic for the volumes that result (snd_main.c SND_Spatialize_WithSfx), worked out here
// from the formula: intensity = volume * mastervolume * (1 - distance * attenuation / radius)^exponent, each ear
// 0.5 + 0.5 * the source's direction towards it.
using System;
using System.Numerics;
using VortexArena.Engine.Audio;
using Xunit;

namespace VortexArena.Tests;

public class DpVehicleSoundTests
{
    private const int Rate = 48000;

    private static DpSfx Tone(string name, int frames)
    {
        short[] data = new short[frames];
        Array.Fill(data, (short)16384);
        return new DpSfx(name, data, 1, Rate);
    }

    private static DpSoundSystem Make(out DpSoundSettings settings)
    {
        settings = DpSoundSettings.Xonotic();
        DpSoundSystem sound = new() { Settings = settings, Random = () => 0.5 };
        sound.Update(DpListener.Identity);
        return sound;
    }

    // Xonotic's fall-off (snd_soundradius 2400, snd_attenuation_exponent 4) for a QuakeC attenuation.
    private static double Falloff(double distance, double attenuation) => Math.Pow(1.0 - Math.Min(1.0, distance * attenuation / 2400.0), 4.0);

    [Fact]
    public void TheConstantsAreXonotics()
    {
        Assert.Equal(1f, DpVehicleSounds.VolVehicleEngine);
        Assert.Equal(0.5f, DpVehicleSounds.AttenNorm);
        Assert.Equal(0f, DpVehicleSounds.AttenNone);
        Assert.Equal(0.7f, DpVehicleSounds.VolBase);
        Assert.Equal(1f, DpVehicleSounds.VolBaseVoice);
        Assert.Equal(3, DpVehicleSounds.ChTriggerSingle);
        Assert.Equal(6, DpVehicleSounds.ChPainSingle);
        Assert.Equal(-4, DpVehicleSounds.ChShots);
    }

    [Theory]
    [InlineData(0f, false, 0.75f, 0f, 0f)]
    [InlineData(0.5f, false, 0.45f, 0.4f, 0f)]
    [InlineData(1f, true, 0.15f, 0.8f, 0.9f)]
    [InlineData(7f, false, 0.15f, 0.8f, 0f)]
    [InlineData(-1f, true, 0.75f, 0f, 0.9f)]
    public void TheEngineMixFollowsSpeedAndBoost(float speed, bool boosting, float idle, float move, float boost)
    {
        (float i, float m, float b) = DpVehicleSounds.EngineMix(speed, boosting);
        Assert.Equal(idle, i, 5);
        Assert.Equal(move, m, 5);
        Assert.Equal(boost, b, 5);
    }

    [Fact]
    public void AnEngineLoopIsAttenuatedAndPannedByDarkPlacesRulesAndFollowsTheVehicle()
    {
        DpSoundSystem sound = Make(out DpSoundSettings s);
        DpSfx sfx = Tone("vehicles/racer_move.wav", 4800);
        float volume = DpVehicleSounds.EngineMix(0.5f, false).Move;   // 0.4
        int channel = DpVehicleSounds.StartEngineLoop(sound, sfx, new Vector3(600, 0, 0), volume);
        Assert.True(channel >= 0);

        // 600 units straight ahead: half in each ear.
        double intensity = volume * s.Volume * s.MasterVolume * Falloff(600, 0.5);
        Assert.True(sound.TryGetChannelVolumes(channel, out float left, out float right));
        Assert.Equal(intensity * 0.5, left, 6);
        Assert.Equal(intensity * 0.5, right, 6);

        // The vehicle drives to the listener's left: all of it in the left ear, none in the right.
        sound.SetChannelOrigin(channel, new Vector3(0, 600, 0));
        sound.Update(DpListener.Identity);
        Assert.True(sound.TryGetChannelVolumes(channel, out left, out right));
        Assert.Equal(intensity, left, 6);
        Assert.Equal(0.0, right, 6);

        // The cross-fade changes the volume of what plays, without restarting it.
        sound.SetChannelVolume(channel, DpVehicleSounds.EngineMix(1f, false).Move);
        sound.Update(DpListener.Identity);
        Assert.True(sound.TryGetChannelVolumes(channel, out left, out _));
        Assert.Equal(0.8 * s.Volume * s.MasterVolume * Falloff(600, 0.5), left, 6);

        // It loops (a tenth of a second of sample, more than half a second mixed), and at the sample's own speed: no pitch change.
        float[] output = new float[26400 * 2];
        sound.Mix(output, 26400, Rate);
        Assert.True(sound.IsChannelPlaying(channel, sfx, -1, 0));
        Assert.Equal(0.05, sound.GetChannelPosition(channel), 3);   // 26400 frames = five and a half times round the 4800

        // Beyond the radius for ATTEN_NORM (2400 / 0.5 = 4800 units) it is silent but still there.
        sound.SetChannelOrigin(channel, new Vector3(5000, 0, 0));
        sound.Update(DpListener.Identity);
        Assert.True(sound.TryGetChannelVolumes(channel, out left, out right));
        Assert.Equal(0f, left);
        Assert.Equal(0f, right);
    }

    [Fact]
    public void TheBlowUpIsAStackingSoundAtVolBaseAndAttenNorm()
    {
        DpSoundSystem sound = Make(out DpSoundSettings s);
        DpSfx sfx = Tone("weapons/rocket_impact.wav", 48000);
        // 1200 units to the right: listener's left is +Y.
        int first = DpVehicleSounds.StartDeath(sound, sfx, new Vector3(0, -1200, 0));
        int second = DpVehicleSounds.StartDeath(sound, sfx, new Vector3(0, -1200, 0));
        Assert.True(first >= 0 && second >= 0);
        Assert.NotEqual(first, second);   // CH_SHOTS is an automatic channel: two blow-ups do not cut each other off
        Assert.True(sound.TryGetChannelVolumes(first, out float left, out float right));
        Assert.Equal(0.0, left, 6);
        Assert.Equal(0.7 * s.Volume * s.MasterVolume * Falloff(1200, 0.5), right, 6);
        // It plays once.
        float[] output = new float[50000 * 2];
        sound.Mix(output, 50000, Rate);
        Assert.False(sound.IsChannelActive(first));
    }

    [Fact]
    public void AnAlarmIsFullVolumeInBothEarsOnItsOwnSingleChannelOfTheWorld()
    {
        DpSoundSystem sound = Make(out DpSoundSettings s);
        DpSfx health = Tone("vehicles/alarm.wav", 96000), shield = Tone("vehicles/alarm_shield.wav", 48000);
        int h = DpVehicleSounds.StartAlarm(sound, health, shield: false);
        int sh = DpVehicleSounds.StartAlarm(sound, shield, shield: true);
        Assert.True(h >= 0 && sh >= 0);
        Assert.NotEqual(h, sh);
        Assert.True(sound.IsChannelPlaying(h, health, DpSoundSystem.MaxEdicts, DpVehicleSounds.ChPainSingle));
        Assert.True(sound.IsChannelPlaying(sh, shield, DpSoundSystem.MaxEdicts, DpVehicleSounds.ChTriggerSingle));

        // ATTEN_NONE: the same in both ears wherever the listener is and whichever way it faces.
        sound.Update(new DpListener { Origin = new Vector3(9000, -4000, 300), Forward = -Vector3.UnitY, Left = Vector3.UnitX, Up = Vector3.UnitZ });
        double expected = 1.0 * s.CsqcChannelVolume[DpVehicleSounds.ChPainSingle] * s.ChannelVolume[DpVehicleSounds.ChPainSingle] * s.Volume * s.MasterVolume;
        Assert.True(sound.TryGetChannelVolumes(h, out float left, out float right));
        Assert.Equal(expected, left, 6);
        Assert.Equal(expected, right, 6);

        // The two-second repeat replaces what still plays (the sample is two seconds; one has been mixed).
        float[] output = new float[48000 * 2];
        sound.Mix(output, 48000, Rate);
        Assert.Equal(1.0, sound.GetChannelPosition(h), 3);
        int again = DpVehicleSounds.StartAlarm(sound, health, shield: false);
        Assert.Equal(h, again);
        Assert.Equal(0.0, sound.GetChannelPosition(again), 3);

        // vehicle_alarm(NULL, CH_PAIN_SINGLE, SND_Null): the health alarm ends, the shield alarm is not touched.
        sh = DpVehicleSounds.StartAlarm(sound, shield, shield: true);
        DpVehicleSounds.StopAlarm(sound, shield: false);
        Assert.False(sound.IsChannelActive(h));
        Assert.True(sound.IsChannelActive(sh));
        DpVehicleSounds.StopAlarm(sound, shield: true);
        Assert.False(sound.IsChannelActive(sh));
    }

    [Fact]
    public void ANameWithoutASoundExtensionIsGivenTheDefaultOne()
    {
        Assert.Equal("vehicles/alarm.wav", DpSoundFiles.WithDefaultExtension("vehicles/alarm", ".wav"));
        Assert.Equal("misc/hit.wav", DpSoundFiles.WithDefaultExtension("misc/hit.wav", ".wav"));
        Assert.Equal("misc/Hit.OGG", DpSoundFiles.WithDefaultExtension("misc/Hit.OGG", ".wav"));
        Assert.Equal("a/b.flac", DpSoundFiles.WithDefaultExtension("a/b.flac", ".wav"));
        Assert.Equal("announcer/default/1.5seconds.wav", DpSoundFiles.WithDefaultExtension("announcer/default/1.5seconds", ".wav"));
        // A legacy session passes the program's names through as DarkPlaces would look them up.
        Assert.Equal("vehicles/alarm", DpSoundFiles.WithDefaultExtension("vehicles/alarm", null));
        Assert.Equal("", DpSoundFiles.WithDefaultExtension("", ".wav"));
    }

    [Fact]
    public void StartingASoundStampsTheTimeTheOutputLatencyIsMeasuredFrom()
    {
        DpSoundSystem sound = Make(out _);
        Assert.Equal(0, sound.LastStartTicks);
        long before = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(sound.StartSound(5, 1, Tone("a", 4800), default, 1, 1) >= 0);
        long stamped = sound.LastStartTicks;
        Assert.InRange(stamped, before, System.Diagnostics.Stopwatch.GetTimestamp());
        // A change of volume (the empty sample) starts nothing and stamps nothing.
        sound.StartSound(5, 1, DpSoundSystem.ChangeVolume, default, 0.5f, 1);
        Assert.Equal(stamped, sound.LastStartTicks);
    }
}
