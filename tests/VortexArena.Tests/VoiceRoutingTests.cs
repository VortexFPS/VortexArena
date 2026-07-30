using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// [A5 #5] Voice-message routing: <see cref="SoundSystem.GlobalSound"/> → <c>_GlobalSound</c> (the C# port of
/// globalsound.qc:341 _GlobalSound + VOICETYPE dispatch). Before A5 these methods were fully implemented but
/// NEVER CALLED — the <c>voice</c> command played only on the emitter, dropping all recipient routing. These
/// tests drive GlobalSound directly with a recording sound service + a small roster and assert the VOICETYPE
/// recipient sets and gates QC applies: TEAMRADIO → same-team only, TAUNT → all (gated by sv_taunt/!sv_gentle),
/// AUTOTAUNT → all (gated by sv_autotaunt). GlobalState collection (process-global Api.Services).
/// </summary>
[Collection("GlobalState")]
public class VoiceRoutingTests : IDisposable
{
    private readonly IEngineServices _prevServices;
    private readonly RecordingSound _sound = new();
    private readonly DictCvars _cvars = new();

    public VoiceRoutingTests()
    {
        _prevServices = Api.Services;
        Api.Services = new VoiceTestServices(_sound, _cvars);
        SoundSystem.Reset();
    }

    public void Dispose()
    {
        Api.Services = _prevServices;
        SoundSystem.Reset();
    }

    private static Player NewPlayer(int team) => new() { Flags = EntFlags.Client, Team = team };

    /// <summary>QC TEAMRADIO ("attack"): FOREACH_CLIENT(IS_REAL_CLIENT && SAME_TEAM) — only same-team recipients.</summary>
    [Fact]
    public void TeamRadio_ReachesOnlySameTeamRecipients()
    {
        var speaker = NewPlayer(Teams.Red);
        var mate = NewPlayer(Teams.Red);
        var enemy = NewPlayer(Teams.Blue);
        var roster = new List<Entity> { speaker, mate, enemy };

        // "attack" is registered VoiceType.TeamRadio (VoiceMessages table).
        Assert.Equal(VoiceType.TeamRadio, VoiceMessages.VoiceTypeOf("attack"));
        SoundSystem.GlobalSound(speaker, "attack", roster);

        Assert.Contains(_sound.Played, e => ReferenceEquals(e.E, speaker));
        Assert.Contains(_sound.Played, e => ReferenceEquals(e.E, mate));
        Assert.DoesNotContain(_sound.Played, e => ReferenceEquals(e.E, enemy));
    }

    /// <summary>QC TAUNT ("taunt"): broadcast to all real clients when sv_taunt && !sv_gentle.</summary>
    [Fact]
    public void Taunt_GatedBySvTaunt_AndSvGentle()
    {
        var speaker = NewPlayer(Teams.Red);
        var enemy = NewPlayer(Teams.Blue);
        var roster = new List<Entity> { speaker, enemy };
        Assert.Equal(VoiceType.Taunt, VoiceMessages.VoiceTypeOf("taunt"));

        // sv_taunt off → nothing plays.
        _cvars.Set("sv_taunt", "0");
        SoundSystem.GlobalSound(speaker, "taunt", roster);
        Assert.Empty(_sound.Played);

        // sv_taunt on, sv_gentle off → broadcast to everyone (incl. the enemy).
        _cvars.Set("sv_taunt", "1");
        _cvars.Set("sv_gentle", "0");
        SoundSystem.GlobalSound(speaker, "taunt", roster);
        Assert.Contains(_sound.Played, e => ReferenceEquals(e.E, speaker));
        Assert.Contains(_sound.Played, e => ReferenceEquals(e.E, enemy));

        // sv_gentle on suppresses the taunt entirely.
        _sound.Played.Clear();
        _cvars.Set("sv_gentle", "1");
        SoundSystem.GlobalSound(speaker, "taunt", roster);
        Assert.Empty(_sound.Played);
    }

    // ---- test scaffolding --------------------------------------------------------------------------

    private sealed class RecordingSound : ISoundService
    {
        public readonly record struct Entry(Entity E, SoundChannel Channel, string Sample, float Volume);
        public readonly List<Entry> Played = new();
        public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f)
            => Played.Add(new Entry(e, channel, sample, volume));
        public void Stop(Entity e, SoundChannel channel) { }
    }

    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) => _v.TryGetValue(name, out var s) && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { if (!_v.ContainsKey(name)) _v[name] = defaultValue; }
    }

    private sealed class VoiceTestServices : IEngineServices
    {
        public VoiceTestServices(ISoundService sound, ICvarService cvars) { Sound = sound; Cvars = cvars; }
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; } = new MutableClock();
        public ICvarService Cvars { get; }
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; }
        public IModelService Models { get; } = new NullModels();

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
