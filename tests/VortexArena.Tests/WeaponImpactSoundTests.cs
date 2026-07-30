using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Locks the shared weapon impact/explosion sound helpers (<see cref="WeaponSplash.ImpactSound"/> /
/// <see cref="WeaponSplash.ImpactSoundAt"/>) that every weapon's detonation routes through. The bug they fix:
/// projectile <c>Explode</c> / hitscan impact code emitted only the VISUAL effect (EffectEmitter.Emit) and never
/// a sound, so explosions were silent. These assert the cue plays on CH_SHOTS (auto, so blasts stack) with the
/// requested sample, and that the point variant uses the emitter-less <see cref="ISoundService.PlayAt"/> path
/// (for hitscan trace endpoints, which have no entity). GlobalState: process-global Api.Services.
/// </summary>
[Collection("GlobalState")]
public class WeaponImpactSoundTests : IDisposable
{
    private readonly IEngineServices _prev;
    private readonly RecordingSound _sound = new();

    public WeaponImpactSoundTests()
    {
        _prev = Api.Services;
        Api.Services = new Harness(_sound);
    }

    public void Dispose() => Api.Services = _prev;

    [Fact]
    public void ImpactSound_Plays_The_Sample_On_The_Shots_Auto_Channel()
    {
        var proj = new Entity { Origin = new Vector3(10, 20, 30) };
        WeaponSplash.ImpactSound(proj, "weapons/rocket_impact.wav");

        Assert.Single(_sound.Played);
        Assert.Equal("weapons/rocket_impact.wav", _sound.Played[0].Sample);
        // CH_SHOTS is an AUTO channel so simultaneous blasts stack instead of cutting each other off.
        Assert.Equal(SoundChannel.ShotsAuto, _sound.Played[0].Channel);
        Assert.Equal(SoundLevels.VolBase, _sound.Played[0].Volume);
        Assert.False(_sound.Played[0].AtPoint); // an entity emitter, not the point path
    }

    [Fact]
    public void ImpactSoundAt_Uses_The_Pointed_PlayAt_Path()
    {
        var p = new Vector3(1, 2, 3);
        WeaponSplash.ImpactSoundAt(p, "weapons/neximpact.wav");

        Assert.Single(_sound.Played);
        Assert.Equal("weapons/neximpact.wav", _sound.Played[0].Sample);
        Assert.Equal(SoundChannel.ShotsAuto, _sound.Played[0].Channel);
        Assert.True(_sound.Played[0].AtPoint);      // hitscan endpoint: no emitter entity
        Assert.Equal(p, _sound.Played[0].Point);
    }

    [Fact]
    public void Empty_Sample_Is_A_No_Op()
    {
        WeaponSplash.ImpactSound(new Entity(), "");
        WeaponSplash.ImpactSoundAt(Vector3.Zero, "");
        Assert.Empty(_sound.Played);
    }

    // ---- scaffolding -------------------------------------------------------------------------------

    private sealed class RecordingSound : ISoundService
    {
        public readonly record struct Entry(SoundChannel Channel, string Sample, float Volume, bool AtPoint, Vector3 Point);
        public readonly List<Entry> Played = new();
        public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f)
            => Played.Add(new Entry(channel, sample, volume, false, e.Origin));
        public void PlayAt(Vector3 point, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f)
            => Played.Add(new Entry(channel, sample, volume, true, point));
        public void Stop(Entity e, SoundChannel channel) { }
    }

    private sealed class Harness : IEngineServices
    {
        public Harness(ISoundService sound) => Sound = sound;
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; } = new NullClock();
        public ICvarService Cvars { get; } = new NullCvars();
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; }
        public IModelService Models { get; } = new NullModels();

        private sealed class NullClock : IGameClock { public float Time => 0f; public float FrameTime => 0f; }
        private sealed class NullCvars : ICvarService
        {
            public float GetFloat(string name) => 0f;
            public string GetString(string name) => "";
            public void Set(string name, string value) { }
            public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
        }
        private sealed class NullTrace : ITraceService
        {
            public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore) => TraceResult.Miss(end);
            public int PointContents(Vector3 point) => 0;
            public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
        }
        private sealed class NullEntities : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => System.Array.Empty<Entity>();
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
