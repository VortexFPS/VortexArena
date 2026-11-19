using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Particles;
using Xunit;

namespace VortexArena.Tests;

public sealed class MapBgmScriptTests
{
    [Fact]
    public void MapKeysAreTypedAndOnlyClientModelNamesTreatPrefixAsAngular()
    {
        var entity = new Entity { ClassName = "func_clientwall" };
        var fields = new Dictionary<string, string>
        {
            ["bgmscript"] = "<a",
            ["bgmscriptattack"] = "0.25",
            ["bgmscriptdecay"] = "0.125",
            ["bgmscriptsustain"] = "-1",
            ["bgmscriptrelease"] = "0.5",
        };
        MapObjectFieldsExtra.Apply(entity, fields);
        Assert.Equal(new MapBgmSettings("a", true, .25f, .125f, 0f, .5f), entity.BgmScript);

        entity = new Entity { ClassName = "func_pointparticles" };
        fields.Remove("bgmscriptsustain");
        MapObjectFieldsExtra.Apply(entity, fields);
        Assert.Equal(new MapBgmSettings("<a", false, .25f, .125f, 1f, .5f), entity.BgmScript);

        entity = new Entity { ClassName = "func_wall" };
        MapObjectFieldsExtra.Apply(entity, fields);
        Assert.Null(entity.BgmScript);
    }

    [Fact]
    public void EnvelopeFollowsNoteVelocityAndAttackDecaySustainRelease()
    {
        var playback = Bind("a 1 0.8\na 3 0", new("a", false, .5f, .5f, .5f, 1f));

        Assert.Equal(new MapBgmScript.Sample(-1f, false), playback.Advance(-1f, 0f));
        Assert.Equal(new MapBgmScript.Sample(0f, false), playback.Advance(1f, 0f));
        var attack = playback.Advance(1.25f, .25f);
        Assert.True(attack.NoteOn);
        Assert.Equal(.4f, attack.Amplitude, 4);
        Assert.Equal(.6f, playback.Advance(1.75f, .5f).Amplitude, 4);
        Assert.Equal(.4f, playback.Advance(2.5f, .75f).Amplitude, 4);
        var release = playback.Advance(3.5f, 1f);
        Assert.False(release.NoteOn);
        Assert.Equal(.2f, release.Amplitude, 4);
        Assert.Equal(0f, playback.Advance(4.1f, .6f).Amplitude);
    }

    [Fact]
    public void RetriggerAndLoopPreserveAmplitudeAndReplayTheFirstLane()
    {
        var script = MapBgmScript.Parse("a 1 1\na 1.5 0\na 1.75 1\nb 2 1\na 3 0");
        var playback = Assert.IsType<MapBgmScript.Playback>(script.Bind(new("a", false, 1f, 0f, 1f, 1f)));
        Assert.Null(script.Bind(new("undefined", false, 0f, 0f, 1f, 0f)));

        Assert.Equal(.5f, playback.Advance(1.5f, 0f).Amplitude, 4);
        Assert.Equal(.375f, playback.Advance(1.625f, .125f).Amplitude, 4);
        Assert.Equal(.375f, playback.Advance(1.875f, .25f).Amplitude, 4);
        Assert.Equal(1f, playback.Advance(3.5f, 0f).Amplitude, 4);
        Assert.Equal(1f, playback.Advance(.25f, .125f).Amplitude, 4);

        // Loop starts its release from the new clock position, then restarts the first note.
        Assert.True(playback.Advance(1.1f, 0f).NoteOn);
        Assert.Equal(.35f, playback.Advance(1.1f, 0f).Amplitude, 4);
    }

    [Fact]
    public void LastCrossedEventControlsTheBurstAndMissingMusicUsesQcFallback()
    {
        var playback = Bind("a 0.1 1\na 0.2 0\na 0.3 1", new("a", false, 0f, 0f, .25f, 0f));
        var emitter = new PointParticleEmitter();
        var points = new List<Vector3>();
        var random = new Random(1);
        emitter.Configure(8f, impulseSpawnflag: true);

        var sample = playback.Advance(.25f, .25f);
        emitter.JustToggled = sample.NoteOn;
        Assert.Equal(0, emitter.Step(.25f, random, points, sample.Amplitude));
        sample = playback.Advance(.35f, .10f);
        emitter.JustToggled = sample.NoteOn;
        Assert.Equal(8, emitter.Step(.1f, random, points, sample.Amplitude));
        Assert.Equal(2, emitter.Step(.25f, random, points, playback.Advance(-1f, 0f).Amplitude));

        emitter.Configure(8f, impulseSpawnflag: false);
        Assert.Equal(0, emitter.Step(.25f, random, points, -1f));
        Assert.Equal(1, emitter.Step(.25f, random, points, .5f));
        emitter.JustToggled = true;
        Assert.Equal(1, emitter.Step(.001f, random, points, 0f));
    }

    [Theory]
    [InlineData(.5f, .8f, .6f)]
    [InlineData(.5f, -.8f, .6f)]
    [InlineData(0f, -.8f, 1f)]
    [InlineData(-1f, .8f, 1f)]
    public void ModelOpacityUnderstandsBothLipSignsAndRestoresOpaqueWhenMuted(float amplitude, float lip, float expected) =>
        Assert.Equal(expected, new MapBgmScript.Sample(amplitude, false).ModelAlpha(lip), 4);

    private static MapBgmScript.Playback Bind(string text, MapBgmSettings settings) =>
        Assert.IsType<MapBgmScript.Playback>(MapBgmScript.Parse(text).Bind(settings));
}
