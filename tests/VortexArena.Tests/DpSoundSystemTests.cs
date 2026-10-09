// DarkPlaces' sound rules (src/VortexArena.Engine/Audio), checked against values worked out from the C
// (Base/darkplaces/snd_main.c, snd_mix.c, snd_wav.c, snd_ogg.c, cd_shared.c) by a second, separate
// transcription: the Reference class below follows the C line by line with its own names and arithmetic,
// and the literal numbers were computed by hand from the formulas.
using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Engine.Audio;
using Xunit;

namespace VortexArena.Tests;

public class DpSoundSystemTests
{
    private const int Rate = 48000;

    /// <summary>An independent transcription of SND_Spatialize_WithSfx's stereo arithmetic.</summary>
    private static class Reference
    {
        public static (double L, double R) Spatialize(double mastervol, double[] listener, double[] fwd, double[] left, double[] up, double[] src,
            double attenuation, double radius, double exponent, double decibel, bool occluded)
        {
            double distfade = attenuation / radius;
            double[] v = { listener[0] - src[0], listener[1] - src[1], listener[2] - src[2] };
            double dist = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            double f = dist * distfade;
            f = (exponent == 0 ? 1.0 : Math.Pow(1.0 - Math.Min(1.0, f), exponent)) * (decibel == 0 ? 1.0 : Math.Pow(0.1, 0.1 * decibel * f));
            double intensity = mastervol * f;
            if (!(intensity > 0)) return (0, 0);
            if (occluded) intensity *= 0.5;
            // Matrix4x4_Transform by the inverse view, then the ear's yaw: the left ear reads +y, the right ear -y.
            double[] rel = { src[0] - listener[0], src[1] - listener[1], src[2] - listener[2] };
            double x = rel[0] * fwd[0] + rel[1] * fwd[1] + rel[2] * fwd[2];
            double y = rel[0] * left[0] + rel[1] * left[1] + rel[2] * left[2];
            double z = rel[0] * up[0] + rel[1] * up[1] + rel[2] * up[2];
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len != 0) y /= len;
            return (intensity * Math.Max(0, y * 0.5 + 0.5), intensity * Math.Max(0, -y * 0.5 + 0.5));
        }
    }

    private static DpSfx Tone(string name, int frames, int rate = Rate, int loopStart = -1, int channels = 1, short value = 16384)
    {
        short[] data = new short[frames * channels];
        Array.Fill(data, value);
        return new DpSfx(name, data, channels, rate, loopStart);
    }

    private static DpSoundSystem Make(DpSoundSettings? settings = null, IDpSoundWorld? world = null)
    {
        DpSoundSystem sound = new() { Settings = settings ?? DpSoundSettings.Xonotic(), Random = () => 0.5 };
        if (world is not null) sound.World = world;
        sound.Update(DpListener.Identity);
        return sound;
    }

    private sealed class World : IDpSoundWorld
    {
        public int View = 1, Clients = 8;
        public Dictionary<int, Vector3> Origins = new();
        public HashSet<int> Removed = new();
        public bool Blocked;
        public bool IsViewEntity(int entnum) => entnum == View;
        public int MaxClients => Clients;
        public DpEntityOrigin EntityOrigin(int entnum, ref Vector3 origin)
        {
            if (Removed.Contains(entnum)) return DpEntityOrigin.Removed;
            if (!Origins.TryGetValue(entnum, out Vector3 at)) return DpEntityOrigin.Keep;
            origin = at;
            return DpEntityOrigin.Moved;
        }
        public bool Occluded(Vector3 listener, Vector3 source, int mode) => Blocked;
    }

    // ---- volume and pan ----------------------------------------------------------------------------------

    [Theory]
    // Xonotic: mastervolume 0.7, volume 1, radius 2400, exponent 4. Straight ahead both ears get half.
    [InlineData(0f, 0f, 0.35f, 0.35f)]                       // at the listener: the zero vector stays zero, 0.7 * 0.5
    [InlineData(600f, 0f, 0.110742188f, 0.110742188f)]       // 0.7 * 0.75^4 * 0.5
    [InlineData(1200f, 0f, 0.021875f, 0.021875f)]            // 0.7 * 0.5^4 * 0.5
    [InlineData(1800f, 0f, 0.0013671875f, 0.0013671875f)]    // 0.7 * 0.25^4 * 0.5
    [InlineData(2400f, 0f, 0f, 0f)]
    [InlineData(2640f, 0f, 0f, 0f)]
    [InlineData(0f, 600f, 0.221484375f, 0f)]                 // hard left: 0.7 * 0.75^4
    [InlineData(0f, -600f, 0f, 0.221484375f)]
    [InlineData(-600f, 0f, 0.110742188f, 0.110742188f)]      // behind sounds like in front
    public void XonoticAttenuationAndPan(float forward, float left, float expectLeft, float expectRight)
    {
        DpSoundSystem sound = Make();
        int ch = sound.StartSound(0, 0, Tone("t", 4800), new Vector3(forward, left, 0), 1f, 1f);
        Assert.True(sound.TryGetChannelVolumes(ch, out float l, out float r));
        Assert.Equal(expectLeft, l, 6);
        Assert.Equal(expectRight, r, 6);
    }

    [Fact]
    public void MatchesTheReferenceTranscriptionOverAGrid()
    {
        DpSoundSettings s = DpSoundSettings.Xonotic();
        World world = new() { View = 99 };
        DpSoundSystem sound = Make(s, world);
        // A listener turned 30 degrees to the left and standing off the origin.
        double yaw = Math.PI / 6;
        DpListener listener = new()
        {
            Origin = new Vector3(100, -50, 20),
            Forward = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0),
            Left = new Vector3(-(float)Math.Sin(yaw), (float)Math.Cos(yaw), 0),
            Up = Vector3.UnitZ,
        };
        foreach (bool blocked in new[] { false, true })
            foreach ((float exponent, float decibel, float radius) in new[] { (4f, 0f, 2400f), (1f, 0f, 1200f), (0f, 10f, 1200f) })
            {
                s.AttenuationExponent = exponent; s.AttenuationDecibel = decibel; s.SoundRadius = radius;
                world.Blocked = blocked;
                foreach (float atten in new[] { 0.25f, 0.5f, 1f, 2f, 4f })
                    for (int i = 0; i < 40; i++)
                    {
                        Vector3 at = new(i * 97 % 1900 - 700, i * 53 % 1500 - 800, i * 31 % 400 - 200);
                        sound.StopAllSounds();
                        sound.Update(listener);
                        int ch = sound.StartSound(0, 0, Tone("t", 4800), at, 0.8f, atten);
                        Assert.True(sound.TryGetChannelVolumes(ch, out float l, out float r));
                        (double el, double er) = Reference.Spatialize(0.8 * 0.7,
                            new double[] { 100, -50, 20 }, new[] { Math.Cos(yaw), Math.Sin(yaw), 0 }, new[] { -Math.Sin(yaw), Math.Cos(yaw), 0 }, new double[] { 0, 0, 1 },
                            new double[] { at.X, at.Y, at.Z }, atten, radius, exponent, decibel, blocked);
                        Assert.Equal(el, l, 4);
                        Assert.Equal(er, r, 4);
                    }
            }
    }

    [Fact]
    public void QuakeDefaultIsAStraightLineOver1200Units()
    {
        DpSoundSystem sound = Make(new DpSoundSettings { Volume = 1, MasterVolume = 1, MaxChannelVolume = 0 });
        int ch = sound.StartSound(0, 0, Tone("t", 4800), new Vector3(0, 300, 0), 1f, 1f);
        sound.TryGetChannelVolumes(ch, out float l, out float r);
        Assert.Equal(0.75f, l, 6);
        Assert.Equal(0f, r, 6);
        // ATTEN_IDLE (2): half the radius.
        ch = sound.StartSound(0, 0, Tone("u", 4800), new Vector3(0, 300, 0), 1f, 2f);
        sound.TryGetChannelVolumes(ch, out l, out _);
        Assert.Equal(0.5f, l, 6);
    }

    [Fact]
    public void NoAttenuationAndTheViewEntityAreFullVolumeInBothEars()
    {
        World world = new() { View = 5 };
        DpSoundSystem sound = Make(null, world);
        int none = sound.StartSound(0, 0, Tone("a", 4800), new Vector3(0, 5000, 0), 0.5f, 0f);
        int view = sound.StartSound(5, 1, Tone("b", 4800), new Vector3(0, 5000, 0), 1f, 1f);
        sound.TryGetChannelVolumes(none, out float l, out float r);
        Assert.Equal(0.35f, l, 6); Assert.Equal(0.35f, r, 6);
        sound.TryGetChannelVolumes(view, out l, out r);
        Assert.Equal(0.7f, l, 6); Assert.Equal(0.7f, r, 6);
    }

    [Fact]
    public void VolumeCvarsByEntityClassAndChannel()
    {
        DpSoundSettings s = DpSoundSettings.Xonotic();
        s.MasterVolume = 1;
        s.CsqcChannelVolume[3] = 0.5f; s.WorldChannelVolume[3] = 0.25f; s.PlayerChannelVolume[3] = 0.125f; s.EntChannelVolume[3] = 0.75f;
        s.ChannelVolume[3] = 0.5f;
        s.ExtraChannelVolume = n => n == 8 ? 0.2f : 1f;
        Assert.Equal(0.25f, DpSoundSystem.MasterVolume(s, 1, false, 0, DpSoundSystem.MaxEdicts + 4, 3, 8, null), 6);
        Assert.Equal(0.125f, DpSoundSystem.MasterVolume(s, 1, false, 0, 0, 3, 8, null), 6);
        Assert.Equal(0.0625f, DpSoundSystem.MasterVolume(s, 1, false, 0, 8, 3, 8, null), 6);
        Assert.Equal(0.375f, DpSoundSystem.MasterVolume(s, 1, false, 0, 9, 3, 8, null), 6);
        // A channel outside 0..7 reads snd_channel<abs>volume only; auto channels are negative.
        Assert.Equal(0.2f, DpSoundSystem.MasterVolume(s, 1, false, 0, 9, 8, 8, null), 6);
        Assert.Equal(0.2f, DpSoundSystem.MasterVolume(s, 1, false, 0, 9, -8, 8, null), 6);
        Assert.Equal(1f, DpSoundSystem.MasterVolume(s, 1, false, 0, 9, -3, 8, null), 6);
        // A static sound takes snd_staticvolume in their place; a full-volume channel (music) none of them, nor "volume".
        s.StaticVolume = 0.4f; s.Volume = 0.5f;
        Assert.Equal(0.2f, DpSoundSystem.MasterVolume(s, 1, true, 0, 0, 3, 8, null), 6);
        Assert.Equal(1f, DpSoundSystem.MasterVolume(s, 1, false, DpSoundSystem.ChannelFlagFullVolume, -1, 0, 8, null), 6);
        // DarkPlaces' own default clamps one channel at snd_maxchannelvolume and applies ReplayGain.
        DpSoundSettings d = new() { Volume = 1, MasterVolume = 1, MaxChannelVolume = 0.5f };
        Assert.Equal(0.5f, DpSoundSystem.MasterVolume(d, 1, false, 0, 9, 0, 8, null), 6);
        DpSfx gained = Tone("g", 10); gained.VolumeMult = 0.5f; gained.VolumePeak = 0.9f;
        d.MaxChannelVolume = 10;
        Assert.Equal(0.5f, DpSoundSystem.MasterVolume(d, 1, false, 0, 9, 0, 8, gained), 6);
    }

    [Fact]
    public void AnOccludedSoundIsHalved()
    {
        World world = new() { View = 99, Blocked = true };
        DpSoundSystem sound = Make(null, world);
        int ch = sound.StartSound(0, 0, Tone("t", 4800), new Vector3(0, 600, 0), 1f, 1f);
        sound.TryGetChannelVolumes(ch, out float l, out _);
        Assert.Equal(0.221484375f * 0.5f, l, 6);
        sound.Settings.Occlusion = 0;
        sound.Update(DpListener.Identity);
        sound.TryGetChannelVolumes(ch, out l, out _);
        Assert.Equal(0.221484375f, l, 6);
    }

    [Fact]
    public void SpatializationControlNarrowsThePanByDistance()
    {
        DpSoundSettings s = DpSoundSettings.Xonotic();
        s.SpatializationControl = true;   // logarithmic between radius 100 (0.95) and 10000 (0.70)
        DpSoundSystem sound = Make(s);
        int ch = sound.StartSound(0, 0, Tone("t", 4800), new Vector3(0, 1000, 0), 1f, 0.001f);
        sound.TryGetChannelVolumes(ch, out float l, out float r);
        // f = 0.70 + 0.25 * (ln 1000 - ln 10000) / (ln 100 - ln 10000) = 0.825
        double intensity = 0.7 * Math.Pow(1 - 1000 * 0.001 / 2400, 4);
        Assert.Equal(intensity * (0.5 + 0.5 * 0.825), l, 5);
        Assert.Equal(intensity * (0.5 - 0.5 * 0.825), r, 5);
    }

    [Fact]
    public void ASoundFollowsItsEntityAndIsDisownedWhenTheEntityGoes()
    {
        World world = new() { View = 99 };
        DpSoundSystem sound = Make(null, world);
        int ch = sound.StartSound(40, 1, Tone("t", 480000), new Vector3(0, 600, 0), 1f, 1f);
        world.Origins[40] = new Vector3(0, -600, 0);
        sound.Update(DpListener.Identity);
        sound.TryGetChannelVolumes(ch, out float l, out float r);
        Assert.Equal(0f, l, 6); Assert.Equal(0.221484375f, r, 6);
        world.Removed.Add(40);
        world.Origins[40] = new Vector3(0, 600, 0);
        sound.Update(DpListener.Identity);
        world.Removed.Clear();
        sound.Update(DpListener.Identity);
        sound.TryGetChannelVolumes(ch, out l, out r);
        Assert.Equal(0f, l, 6); Assert.Equal(0.221484375f, r, 6);   // stays where it was: it belongs to nobody now
        Assert.Equal(-1f, sound.GetEntChannelPosition(40, 1));
    }

    // ---- channels ----------------------------------------------------------------------------------------

    [Fact]
    public void ASingleChannelReplacesAndAnAutoChannelStacks()
    {
        DpSoundSystem sound = Make();
        DpSfx a = Tone("a", 48000), b = Tone("b", 48000);
        int first = sound.StartSound(7, 1, a, default, 1, 1);
        int second = sound.StartSound(7, 1, b, default, 1, 1);
        Assert.Equal(first, second);
        Assert.True(sound.IsChannelPlaying(first, b, 7, 1));
        int auto1 = sound.StartSound(7, 0, a, default, 1, 1), auto2 = sound.StartSound(7, 0, a, default, 1, 1), auto3 = sound.StartSound(7, -1, a, default, 1, 1);
        Assert.Equal(4, new HashSet<int> { first, auto1, auto2, auto3 }.Count);
        // Another entity's channel 1 is its own.
        Assert.NotEqual(first, sound.StartSound(8, 1, a, default, 1, 1));
        sound.StopSound(7, 1);
        Assert.False(sound.IsChannelActive(first));
        Assert.True(sound.IsChannelActive(auto1));
    }

    [Fact]
    public void WhenFullTheSoundWithLeastLifeLeftIsStolenButNotALoopNorTheViewEntitys()
    {
        World world = new() { View = 1 };
        DpSoundSystem sound = Make(null, world);
        DpSfx longOne = Tone("long", 96000), shortOne = Tone("short", 4800), looped = Tone("loop", 2400, loopStart: 0), tiny = Tone("tiny", 480);
        int view = sound.StartSound(1, 0, tiny, default, 1, 1);          // least life of all, but the player's own
        int loop = sound.StartSound(50, 0, looped, default, 1, 1);
        int forced = sound.StartSound(51, 0, tiny, default, 1, 1, 0, DpSoundSystem.ChannelFlagForceLoop);
        int victim = sound.StartSound(52, 0, shortOne, default, 1, 1);
        for (int i = 4; i < DpSoundSystem.MaxDynamicChannels; i++) Assert.True(sound.StartSound(100 + i, 0, longOne, default, 1, 1) >= 0);
        int stolen = sound.StartSound(900, 0, longOne, default, 1, 1);
        Assert.Equal(victim, stolen);
        Assert.True(sound.IsChannelPlaying(view, tiny, 1, 0));
        Assert.True(sound.IsChannelPlaying(loop, looped, 50, 0));
        Assert.True(sound.IsChannelPlaying(forced, tiny, 51, 0));
        // The view entity's own new sound may take the view entity's old one.
        Assert.Equal(view, sound.StartSound(1, 0, longOne, default, 1, 1));
    }

    [Fact]
    public void NothingIsStartedWhenEveryChannelLoops()
    {
        DpSoundSystem sound = Make();
        DpSfx looped = Tone("loop", 2400, loopStart: 0);
        for (int i = 0; i < DpSoundSystem.MaxDynamicChannels; i++) sound.StartSound(100 + i, 0, looped, default, 1, 1);
        Assert.Equal(-1, sound.StartSound(900, 0, Tone("x", 100), default, 1, 1));
    }

    [Fact]
    public void AnIdenticalSoundStartedTheSameFrameIsOffset()
    {
        DpSoundSystem sound = Make();
        DpSfx a = Tone("a", 48000);
        sound.ServerTickSeconds = 1.0 / 60;   // Xonotic: time -0.1, tics 1: a delay of up to one tick
        sound.StartSound(7, 0, a, default, 1, 1);
        int second = sound.StartSound(8, 0, a, default, 1, 1);
        // lhrandom(0, -800) at the middle of its range: -400 frames, i.e. a delay of 400 / 48000 s.
        Assert.Equal(-400f / 48000f, sound.GetChannelPosition(second), 5);
        // Not connected (the menu): the whole 0.1 s.
        sound.ServerTickSeconds = 0;
        int third = sound.StartSound(9, 0, a, default, 1, 1);
        Assert.Equal(-2400f / 48000f, sound.GetChannelPosition(third), 4);
        // A different speed is a different sound; so is one that has begun to play.
        int other = sound.StartSound(10, 0, a, default, 1, 1, 0, 0, 1.5f);
        Assert.Equal(0f, sound.GetChannelPosition(other));
        // DarkPlaces' default skips INTO the sample instead.
        DpSoundSystem dp = Make(new DpSoundSettings());
        dp.StartSound(7, 0, a, default, 1, 1);
        Assert.Equal(2400f / 48000f, dp.GetChannelPosition(dp.StartSound(8, 0, a, default, 1, 1)), 4);
    }

    [Fact]
    public void TheEmptySampleChangesVolumeSpeedAndFlagsOfWhatPlays()
    {
        DpSoundSystem sound = Make();
        DpSfx a = Tone("a", 48000);
        int ch = sound.StartSound(7, 8, a, default, 1, 0);
        Assert.Equal(ch, sound.StartSound(7, 8, DpSoundSystem.ChangeVolume, default, 0.5f, 0, 0, DpSoundSystem.ChannelFlagForceLoop, 2f));
        sound.TryGetChannelVolumes(ch, out float l, out _);
        Assert.Equal(0.35f, l, 6);
        Assert.True(sound.IsChannelPlaying(ch, a, 7, 8));
        Assert.Equal(-1, sound.StartSound(7, 0, DpSoundSystem.ChangeVolume, default, 0.5f, 0));
        Assert.Equal(-1, sound.StartSound(99, 8, DpSoundSystem.ChangeVolume, default, 0.5f, 0));
        // Forced to loop by the flag: after more than its length it is still there.
        float[] buffer = new float[2 * 48000];
        sound.Mix(buffer, 48000, Rate);
        Assert.True(sound.IsChannelPlaying(ch, a, 7, 8));
    }

    [Fact]
    public void StaticSoundsDivideAttenuationBy64AndShareOneVoicePerSample()
    {
        DpSoundSettings s = DpSoundSettings.Xonotic();
        s.StaticVolume = 0.5f;
        DpSoundSystem sound = Make(s);
        DpSfx torch = Tone("torch", 4800);
        int a = sound.StaticSound(torch, new Vector3(0, 600, 0), 1f, 64f);
        int b = sound.StaticSound(torch, new Vector3(0, -600, 0), 1f, 64f);
        Assert.Equal(DpSoundSystem.MaxDynamicChannels, a);
        sound.Update(DpListener.Identity);
        sound.TryGetChannelVolumes(a, out float l, out float r);
        // Each is 0.5 * 0.7 * 0.75^4 on its own side; the second is folded into the first.
        Assert.Equal(0.110742188f, l, 6); Assert.Equal(0.110742188f, r, 6);
        sound.TryGetChannelVolumes(b, out l, out r);
        Assert.Equal(0f, l); Assert.Equal(0f, r);
        Assert.Equal(1, sound.MixedSounds);
        // A static sound loops though its file has no loop point.
        float[] buffer = new float[2 * 9600];
        sound.Mix(buffer, 9600, Rate);
        Assert.True(sound.IsChannelActive(a));
        sound.StopAllSounds();
        Assert.Equal(0, sound.StaticChannels);
    }

    // ---- the mixer ---------------------------------------------------------------------------------------

    private static DpSoundSystem Unity()
    {
        // Volume 1 everywhere, no limiter: what is mixed is the sample times the pan.
        return Make(new DpSoundSettings { Volume = 1, MasterVolume = 1, MaxChannelVolume = 0, SoftClip = 0 });
    }

    [Fact]
    public void ResamplingIsLinearInterpolationWithA16Dot16Step()
    {
        DpSoundSystem sound = Unity();
        short[] ramp = new short[100];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = (short)(i * 256);
        // 24 kHz into 48 kHz: every other output frame lies halfway between two samples.
        sound.StartSound(0, 0, new DpSfx("ramp", ramp, 1, 24000), default, 1, 0);
        float[] buffer = new float[2 * 16];
        sound.Mix(buffer, 16, Rate);
        for (int i = 0; i < 16; i++)
        {
            float expected = (int)(i * 0.5f * 256 / 32768f * 32768f) / 32768f;   // then truncated to 16 bit
            Assert.Equal(expected, buffer[i * 2], 6);
            Assert.Equal(expected, buffer[i * 2 + 1], 6);
        }
        // 44.1 kHz: the step is floor(0.91875 * 65536) = 60211 / 65536, not 0.91875 exactly.
        DpSoundSystem other = Unity();
        other.StartSound(0, 0, new DpSfx("ramp", ramp, 1, 44100), default, 1, 0);
        other.Mix(buffer, 16, Rate);
        long frac = 0; int index = 0;
        for (int i = 0; i < 16; i++)
        {
            float lerp1 = frac * (1.0f / 65536.0f);
            float sample = ramp[index] / 32768f * (1.0f - lerp1) + ramp[index + 1] / 32768f * lerp1;
            Assert.Equal((int)(sample * 32768.0f) / 32768f, buffer[i * 2], 6);
            frac += 60211; index += (int)(frac >> 16); frac &= 0xFFFF;
        }
    }

    [Fact]
    public void SpeedScalesTheStepAndTheSoundEndsWhenItsDataDoes()
    {
        DpSoundSystem sound = Unity();
        DpSfx a = Tone("a", 4800);
        int normal = sound.StartSound(1, 1, a, default, 1, 0);
        int fast = sound.StartSound(2, 1, a, default, 1, 0, 0, 0, 2f);
        float[] buffer = new float[2 * 2400];
        sound.Mix(buffer, 2400, Rate);
        Assert.Equal(0.05f, sound.GetChannelPosition(normal), 5);
        Assert.False(sound.IsChannelActive(fast));        // 4800 frames at double speed are gone in 2400
        Assert.Equal(1.0f, buffer[0], 4);                 // two channels of 0.5 at full volume in both ears
        sound.Mix(buffer, 2400, Rate);
        Assert.False(sound.IsChannelActive(normal));
        sound.Mix(buffer, 2400, Rate);
        Assert.Equal(0f, buffer[0]);
    }

    [Fact]
    public void ALoopPointLoopsBackToItAndWithoutOneTheSoundPlaysOnce()
    {
        DpSoundSystem sound = Unity();
        short[] data = new short[1000];
        for (int i = 0; i < data.Length; i++) data[i] = (short)(i * 16);
        int ch = sound.StartSound(1, 1, new DpSfx("loop", data, 1, Rate, 600), default, 1, 0);
        float[] buffer = new float[2 * 1500];
        sound.Mix(buffer, 1500, Rate);
        Assert.Equal(999 * 16 / 32768f, buffer[999 * 2], 6);
        Assert.Equal(600 * 16 / 32768f, buffer[1000 * 2], 6);      // straight back to the loop start, no gap
        Assert.Equal(999 * 16 / 32768f, buffer[1399 * 2], 6);
        Assert.Equal(600 * 16 / 32768f, buffer[1400 * 2], 6);
        Assert.True(sound.IsChannelActive(ch));
        Assert.Equal(700f / Rate, sound.GetChannelPosition(ch), 6);

        // The same data with no loop marker, not forced: once.
        int once = sound.StartSound(2, 1, new DpSfx("once", data, 1, Rate), default, 1, 0);
        sound.StopChannel(ch);
        sound.Mix(buffer, 1500, Rate);
        Assert.False(sound.IsChannelActive(once));
        Assert.Equal(0f, buffer[1000 * 2]);
    }

    [Fact]
    public void AStereoSampleKeepsItsSidesAndEachIsScaledByItsEar()
    {
        DpSoundSystem sound = Unity();
        short[] data = new short[200];
        for (int i = 0; i < 100; i++) { data[i * 2] = 8192; data[i * 2 + 1] = -16384; }
        sound.StartSound(0, 0, new DpSfx("st", data, 2, Rate), new Vector3(0, 300, 0), 1, 1);   // hard left: right ear 0
        float[] buffer = new float[2 * 10];
        sound.Mix(buffer, 10, Rate);
        Assert.Equal(0.25f * 0.75f, buffer[0], 4);
        Assert.Equal(0f, buffer[1]);
    }

    [Fact]
    public void TheLimiterDividesByThePeakAndRecoversOver400Milliseconds()
    {
        DpSoundSettings s = new() { Volume = 1, MasterVolume = 1, MaxChannelVolume = 0, SoftClip = 1 };
        DpSoundSystem sound = Make(s);
        DpSfx loud = Tone("loud", 4800, value: 32767);
        for (int i = 0; i < 4; i++) sound.StartSound(10 + i, 1, loud, default, 1, 0, 0, 0, 1f + i * 0.001f);
        float[] buffer = new float[2 * 4800];
        sound.Mix(buffer, 4800, Rate);
        // Four full-scale channels sum to about 4; the limiter brings that to 1 (less the 16 bit step).
        Assert.InRange(buffer[100], 0.9999f, 1f);
        // Then a quiet sound alone: still divided, by a peak that decays linearly to 1 in (4 - 1) * 0.4 s... per block
        // maxvol *= 1 - frames / (0.4 * rate), floored at 1.
        sound.StopAllSounds();
        sound.StartSound(20, 1, Tone("quiet", 96000, value: 3277), default, 1, 0);
        sound.Mix(buffer, 2048, Rate);
        float expected = 3277 / 32768f / (3.99988f * (1f - 2048f / (0.4f * Rate)));
        Assert.Equal(expected, buffer[0], 3);
        for (int i = 0; i < 40; i++) sound.Mix(buffer, 2048, Rate);
        Assert.Equal((int)(3277 / 32768f * 32768f) / 32768f, buffer[0], 5);
        // Without the limiter the same four channels clip at 16 bit full scale.
        DpSoundSystem hard = Unity();
        for (int i = 0; i < 4; i++) hard.StartSound(10 + i, 1, loud, default, 1, 0, 0, 0, 1f + i * 0.001f);
        hard.Mix(buffer, 4800, Rate);
        Assert.Equal(32767 / 32768f, buffer[100], 6);
    }

    [Fact]
    public void UnderWaterIsAOnePoleLowPassThatFadesInAtFourPerSecond()
    {
        DpSoundSystem sound = Unity();
        short[] square = new short[9600];
        for (int i = 0; i < square.Length; i++) square[i] = (short)(i / 4 % 2 == 0 ? 16384 : -16384);   // 6 kHz
        sound.StartSound(0, 1, new DpSfx("sq", square, 1, Rate, 0), default, 1, 0);
        float[] buffer = new float[2 * 2048];
        sound.Mix(buffer, 2048, Rate);
        Assert.Equal(0.5f, Math.Abs(buffer[2 * 1000]), 3);
        sound.Update(DpListener.Identity, 0.1, true);      // 0.4 of the way in after 0.1 s
        sound.Update(DpListener.Identity, 1.0, true);      // and fully in (snd_waterfx 1): alpha = 1/12
        sound.Mix(buffer, 2048, Rate);
        float peak = 0;
        for (int i = 1024; i < 2048; i++) peak = Math.Max(peak, Math.Abs(buffer[i * 2]));
        // A one-pole filter y += (x - y) / 12 on a 6 kHz square of amplitude 0.5 swings about +-0.083.
        Assert.InRange(peak, 0.06f, 0.11f);
        sound.Update(DpListener.Identity, 1.0, false);
        sound.Mix(buffer, 2048, Rate);
        Assert.Equal(0.5f, Math.Abs(buffer[2 * 1000]), 3);
    }

    [Fact]
    public void ADelayedSoundIsPaintedFromTheStartOfTheBlockAsDarkPlacesDoes()
    {
        DpSoundSystem sound = Unity();
        DpSfx a = Tone("a", 48000);
        sound.StartSound(7, 0, a, default, 1, 0);
        sound.StartSound(8, 0, a, default, 1, 0);   // delayed 2400 frames (not connected, Random 0.5)... with DP defaults: +2400
        DpSoundSystem x = Make(new DpSoundSettings { Volume = 1, MasterVolume = 1, MaxChannelVolume = 0, IdenticalSoundRandomizationTime = -0.01f });
        x.StartSound(7, 0, a, default, 1, 0);
        int late = x.StartSound(8, 0, a, default, 1, 0);
        Assert.Equal(-240f / Rate, x.GetChannelPosition(late), 6);
        float[] buffer = new float[2 * 1000];
        x.Mix(buffer, 1000, Rate);
        // The delay eats 241 frames; the remaining 759 are painted at frames 0..758 (the paint pointer was not moved).
        Assert.Equal(1.0f, buffer[0], 4);
        Assert.Equal(1.0f, buffer[758 * 2], 4);
        Assert.Equal(0.5f, buffer[759 * 2], 4);
        Assert.Equal(760f / Rate, x.GetChannelPosition(late), 6);
    }

    [Fact]
    public void AChannelWaitsForASampleStillBeingDecoded()
    {
        DpSoundSystem sound = Unity();
        DpSfx sfx = new("late");
        sfx.SetFormat(1, Rate, 4800, -1);
        int ch = sound.StartSound(0, 1, sfx, default, 1, 0);
        float[] buffer = new float[2 * 480];
        sound.Mix(buffer, 480, Rate);
        Assert.Equal(0f, sound.GetChannelPosition(ch));
        Array.Fill(sfx.Data, (short)16384);
        sfx.Publish(4800);
        sound.Mix(buffer, 480, Rate);
        Assert.Equal(0.5f, buffer[0], 4);
        Assert.Equal(0.01f, sound.GetChannelPosition(ch), 6);
        // A sample that failed to load cannot be started.
        DpSfx bad = new("bad"); bad.Fail();
        Assert.Equal(-1, sound.StartSound(0, 1, bad, default, 1, 0));
    }

    [Fact]
    public void PausingTheGameHoldsGameSoundsButNotLocalOnes()
    {
        DpSoundSystem sound = Unity();
        DpSfx a = Tone("a", 48000);
        int game = sound.StartSound(5, 1, a, default, 1, 0);
        int local = sound.LocalSound(Tone("b", 48000), 0, 1, 1);
        sound.PauseGameSounds(true);
        float[] buffer = new float[2 * 480];
        sound.Mix(buffer, 480, Rate);
        Assert.Equal(0f, sound.GetChannelPosition(game));
        Assert.Equal(0.01f, sound.GetChannelPosition(local), 6);
        sound.PauseGameSounds(false);
        sound.Mix(buffer, 480, Rate);
        Assert.Equal(0.01f, sound.GetChannelPosition(game), 6);
    }

    // ---- files -------------------------------------------------------------------------------------------

    private static byte[] Wav(int channels, int width, int rate, byte[] body, int cue = -1, int mark = -1)
    {
        List<byte> b = new();
        void S(string s) { foreach (char c in s) b.Add((byte)c); }
        void I(int v) { b.AddRange(BitConverter.GetBytes(v)); }
        void H(short v) { b.AddRange(BitConverter.GetBytes(v)); }
        S("RIFF"); I(0); S("WAVE");
        S("fmt "); I(16); H(1); H((short)channels); I(rate); I(rate * channels * width); H((short)(channels * width)); H((short)(width * 8));
        S("data"); I(body.Length); b.AddRange(body); if (body.Length % 2 == 1) b.Add(0);
        if (cue >= 0)
        {
            S("cue "); I(28); I(1); I(1); I(cue); S("data"); I(0); I(0); I(cue);
            if (mark >= 0) { S("LIST"); I(28); S("adtl"); S("ltxt"); I(20); I(1); I(mark); S("mark"); I(0); I(0); }
        }
        byte[] wav = b.ToArray();
        BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);
        return wav;
    }

    [Fact]
    public void WavLoopPointsComeFromTheCueChunkAndItsMarkLength()
    {
        byte[] body = new byte[2000];
        for (int i = 0; i < 1000; i++) BitConverter.GetBytes((short)(i - 500)).CopyTo(body, i * 2);
        DpSfx plain = DpSoundFiles.LoadWav("p", Wav(1, 2, 22050, body))!;
        Assert.Equal(1000, plain.TotalLength); Assert.Equal(1000, plain.LoopStart); Assert.False(plain.HasLoop);
        Assert.Equal(22050, plain.Rate); Assert.Equal(-500, plain.Data[0]);

        DpSfx cued = DpSoundFiles.LoadWav("c", Wav(1, 2, 22050, body, cue: 300))!;
        Assert.Equal(300, cued.LoopStart); Assert.Equal(1000, cued.TotalLength); Assert.True(cued.HasLoop);

        DpSfx marked = DpSoundFiles.LoadWav("m", Wav(1, 2, 22050, body, cue: 300, mark: 500))!;
        Assert.Equal(300, marked.LoopStart); Assert.Equal(800, marked.TotalLength);
        // A loop length past the data is not believed.
        Assert.Equal(1000, DpSoundFiles.LoadWav("m", Wav(1, 2, 22050, body, cue: 300, mark: 5000))!.TotalLength);

        // 8 bit unsigned becomes signed, read by the mixer as value / 128; stereo keeps its frames.
        DpSfx eight = DpSoundFiles.LoadWav("e", Wav(2, 1, 11025, new byte[] { 0, 255, 128, 192 }))!;
        Assert.Equal(2, eight.TotalLength); Assert.Equal(2, eight.Channels);
        Assert.Equal(new short[] { -32768, 32512, 0, 16384 }, eight.Data);

        Assert.Null(DpSoundFiles.LoadWav("x", new byte[] { 1, 2, 3 }));
        byte[] adpcm = Wav(1, 2, 22050, body); adpcm[20] = 2;
        Assert.Null(DpSoundFiles.LoadWav("x", adpcm));
    }

    [Fact]
    public void OggLoopAndReplayGainTags()
    {
        DpSoundFiles.OggInfo info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ref info);
        Assert.Equal(100000, info.Length); Assert.Equal(-1, info.LoopStart); Assert.Equal(0f, info.VolumePeak);

        info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["loop_start"] = "2000", ["LOOP_LENGTH"] = "50000" }, ref info);
        Assert.Equal(2000, info.LoopStart); Assert.Equal(52000, info.Length);

        info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LOOP_START"] = "2000", ["LOOP_END"] = "90000", ["LOOP_LENGTH"] = "5" }, ref info);
        Assert.Equal(90000, info.Length);

        info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LOOPSTART"] = "10", ["LOOPEND"] = "999999" }, ref info);
        Assert.Equal(10, info.LoopStart); Assert.Equal(100000, info.Length);

        info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LOOPPOINT"] = "777 samples" }, ref info);
        Assert.Equal(777, info.LoopStart);

        info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["REPLAYGAIN_TRACK_GAIN"] = "-6.0 dB", ["REPLAYGAIN_TRACK_PEAK"] = "0.8" }, ref info);
        Assert.Equal(0.8f, info.VolumePeak, 6);
        Assert.Equal(Math.Pow(10, -6.0 / 20), info.VolumeMult, 5);
        info = new() { TotalFrames = 100000 };
        DpSoundFiles.DecodeTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["REPLAYGAIN_TRACK_GAIN"] = "+6 dB", ["REPLAYGAIN_TRACK_PEAK"] = "0.8" }, ref info);
        Assert.Equal(1.25f, info.VolumeMult, 5);   // never past 1 / peak
    }

    [Fact]
    public void OggHeadersAreReadFromThePages()
    {
        // One stream: identification packet, comment packet (with a tag), then a last page whose granule is the length.
        byte[] id = new byte[30];
        id[0] = 1; "vorbis"u8.CopyTo(id.AsSpan(1)); id[11] = 2; BitConverter.GetBytes(44100).CopyTo(id, 12);
        List<byte> comment = new() { 3 };
        comment.AddRange("vorbis"u8.ToArray());
        comment.AddRange(BitConverter.GetBytes(4)); comment.AddRange("test"u8.ToArray());
        comment.AddRange(BitConverter.GetBytes(2));
        byte[] tag = "LOOP_START=1234"u8.ToArray();
        comment.AddRange(BitConverter.GetBytes(tag.Length)); comment.AddRange(tag);
        byte[] filler = System.Text.Encoding.ASCII.GetBytes("X=" + new string('y', 400));   // makes the packet span lacing values
        comment.AddRange(BitConverter.GetBytes(filler.Length)); comment.AddRange(filler);
        comment.Add(1);
        List<byte> file = new();
        void Page(byte[] body, long granule, uint serial)
        {
            file.AddRange("OggS"u8.ToArray()); file.Add(0); file.Add(0);
            file.AddRange(BitConverter.GetBytes(granule)); file.AddRange(BitConverter.GetBytes(serial));
            file.AddRange(BitConverter.GetBytes(0)); file.AddRange(BitConverter.GetBytes(0));
            List<byte> lacing = new();
            int left = body.Length;
            while (left >= 255) { lacing.Add(255); left -= 255; }
            lacing.Add((byte)left);
            file.Add((byte)lacing.Count); file.AddRange(lacing); file.AddRange(body);
        }
        Page(id, 0, 77);
        Page(comment.ToArray(), 0, 77);
        Page(new byte[10], 5000, 77);
        Page(new byte[10], 98765, 77);
        DpSoundFiles.OggInfo info = DpSoundFiles.ReadOggInfo(file.ToArray())!.Value;
        Assert.Equal(2, info.Channels); Assert.Equal(44100, info.Rate);
        Assert.Equal(98765, info.TotalFrames); Assert.Equal(98765, info.Length); Assert.Equal(1234, info.LoopStart);
        Assert.Null(DpSoundFiles.ReadOggInfo(new byte[100]));
    }

    [Fact]
    public void SampleAndTrackNamesAreLookedForWhereDarkPlacesLooks()
    {
        Assert.Equal(new[] { "sound/weapons/rocket_fire.wav", "sound/weapons/rocket_fire.ogg", "weapons/rocket_fire.wav", "weapons/rocket_fire.ogg" },
            DpSoundFiles.Candidates("weapons/rocket_fire.wav"));
        Assert.Equal(new[] { "sound/misc/a.ogg", "misc/a.ogg" }, DpSoundFiles.Candidates("misc/a.ogg"));
        Assert.Equal(new[] { "sound/x.wav", "sound/x.ogg" }, DpSoundFiles.Candidates("sound/x.wav"));

        string[] remap = { "rising-of-the-phoenix", "", "xoreo" };
        List<string> one = new(DpSoundFiles.TrackCandidates("1", remap));
        Assert.Equal("rising-of-the-phoenix", one[0]);
        Assert.Contains("sound/cdtracks/rising-of-the-phoenix.ogg", one);
        List<string> two = new(DpSoundFiles.TrackCandidates("2", remap));   // an empty remap entry: the number itself
        Assert.Equal("sound/cdtracks/track002.wav", two[0]);
        Assert.Equal("sound/cdtracks/track002.ogg", two[1]);
        Assert.Contains("sound/cdtracks/track02.ogg", two);
        Assert.Empty(DpSoundFiles.TrackCandidates("0", remap));
    }

    [Fact]
    public void MusicIsAFullVolumeLoopingChannelThatBgmvolumeScalesAndZeroPauses()
    {
        DpSoundSettings s = DpSoundSettings.Xonotic();
        s.Volume = 0.25f;   // must not touch the music
        DpSoundSystem sound = Make(s);
        DpSfx track = Tone("sound/cdtracks/tune.ogg", 4800, channels: 2);
        DpCdAudio cd = new(sound, path => path == "sound/cdtracks/tune.ogg", _ => track);
        cd.Command(new[] { "cd", "remap", "tune" });
        cd.Update(0.75f);
        cd.Command(new[] { "cd", "loop", "1" });
        Assert.Equal("sound/cdtracks/tune.ogg", cd.Track);
        Assert.True(cd.Playing);
        sound.Update(DpListener.Identity);
        float[] buffer = new float[2 * 9600];
        sound.Mix(buffer, 9600, Rate);
        Assert.Equal(0.5f * 0.75f * 0.7f, buffer[0], 3);    // bgmvolume * mastervolume, and it loops
        Assert.Equal(0.5f * 0.75f * 0.7f, buffer[9000 * 2], 3);
        float before = cd.Position;
        cd.Update(0f);                                       // muted: paused, not stopped
        sound.Mix(buffer, 9600, Rate);
        Assert.Equal(before, cd.Position);
        Assert.False(cd.Playing);
        cd.Update(0.5f);
        Assert.True(cd.Playing);
        sound.PauseGameSounds(true);                         // a paused game does not pause the music
        sound.Update(DpListener.Identity);
        sound.Mix(buffer, 480, Rate);
        Assert.Equal(0.5f * 0.5f * 0.7f, buffer[0], 3);
        // "cd play" does not loop: the track ends.
        cd.Command(new[] { "cd", "play", "tune" });
        sound.Mix(buffer, 9600, Rate);
        cd.Update(0.5f);
        Assert.Equal("", cd.Track);
        cd.Command(new[] { "cd", "loop", "nothing" });
        Assert.Equal("", cd.Track);
    }
}
