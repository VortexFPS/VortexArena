using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T40: the combat-feedback SOUNDS emitted by the damage pipeline — the C# port of the armor/body-impact
/// (qcsrc/server/player.qc PlayerDamage ~327), pain-voice (~352-383) and death/drown-voice (~463-470) sound()
/// call sites. Runs the real <see cref="DamageSystem"/> with a recording <see cref="ISoundService"/> and asserts
/// the right sample plays on the right channel under the right HP/take/save conditions, including the
/// fatal-armor suppression, the pain debounce, and the sv_gentle gate. GlobalState collection (process-global
/// Api.Services + Combat.System).
/// </summary>
[Collection("GlobalState")]
public class CombatSoundTests : IDisposable
{
    private readonly IEngineServices _prevServices;
    private readonly IDamageSystem _prevDamage;
    private readonly RecordingSound _sound = new();
    private readonly DictCvars _cvars = new();
    private readonly MutableClock _clock = new();

    public CombatSoundTests()
    {
        Sounds.RegisterAll(); // ARMORIMPACT / BODYIMPACT* must resolve by name
        _prevServices = Api.Services;
        _prevDamage = Combat.System;
        Api.Services = new SoundTestServices(_sound, _cvars, _clock);
        Combat.System = new DamageSystem();
        _clock.Time = 100f; // a non-zero time so PainFinished (0) < time -> pain debounce open on the first hit
    }

    public void Dispose()
    {
        Combat.System = _prevDamage;
        Api.Services = _prevServices;
    }

    private static Player NewPlayer(float health = 100f, float armor = 0f)
    {
        var p = new Player { Flags = EntFlags.Client, TakeDamage = DamageMode.Yes };
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.SetResource(ResourceType.Health, health);
        p.SetResource(ResourceType.Armor, armor);
        p.MaxHealth = 100f;
        p.DamageForceScale = 1f;
        return p;
    }

    // Apply a special (non-weapon) hit so the global weapon damage factor doesn't perturb the amount, with no
    // armor unless the test gives some. deathType defaults to a generic special.
    private float Hit(Player p, float amount, string deathType = DeathTypes.Generic, Entity? attacker = null)
        => Combat.Damage(p, null, attacker, amount, deathType, p.Origin, Vector3.Zero);

    // ---- armor / body impact (player.qc ~327) ------------------------------------------------------

    [Fact]
    public void BodyImpact1_When_Take_Between_10_And_30_No_Armor()
    {
        var p = NewPlayer(health: 100f, armor: 0f);
        Hit(p, 20f); // take 20 (10<take<=30), save 0 -> BODYIMPACT1
        Assert.Contains(_sound.Played, s => s.Sample.Contains("bodyimpact1"));
        Assert.DoesNotContain(_sound.Played, s => s.Sample.Contains("bodyimpact2") || s.Sample.Contains("armorimpact"));
    }

    [Fact]
    public void BodyImpact2_When_Take_Over_30()
    {
        var p = NewPlayer(health: 100f, armor: 0f);
        Hit(p, 50f); // take 50 (>30) -> BODYIMPACT2
        Assert.Contains(_sound.Played, s => s.Sample.Contains("bodyimpact2"));
    }

    [Fact]
    public void ArmorImpact_When_Saved_Over_10_And_Survives()
    {
        // armor 100, blockpercent 0.7 -> a 40 hit saves 28 (>10), takes 12; survivor -> ARMORIMPACT.
        var p = NewPlayer(health: 100f, armor: 100f);
        Hit(p, 40f);
        Assert.Contains(_sound.Played, s => s.Sample.Contains("armorimpact"));
        Assert.DoesNotContain(_sound.Played, s => s.Sample.Contains("bodyimpact"));
    }

    [Fact]
    public void ArmorImpact_Suppressed_When_The_Hit_Is_Fatal()
    {
        // low health + big hit: even though save>10, (initial_health - take) <= 0 -> NO armor clink (QC guard).
        var p = NewPlayer(health: 10f, armor: 100f);
        Hit(p, 100f);
        Assert.DoesNotContain(_sound.Played, s => s.Sample.Contains("armorimpact"));
    }

    // ---- pain voice (player.qc ~352-383) -----------------------------------------------------------

    [Fact]
    public void Pain75_At_Mid_Health()
    {
        var p = NewPlayer(health: 80f, armor: 0f);
        Hit(p, 20f); // ends at 60 hp (50<hp<=75) -> pain75
        Assert.Contains(_sound.Played, s => s.Sample.EndsWith("/pain75"));
    }

    [Fact]
    public void Pain25_At_Low_Health()
    {
        var p = NewPlayer(health: 40f, armor: 0f);
        Hit(p, 20f); // ends at 20 hp (<=25) -> pain25
        Assert.Contains(_sound.Played, s => s.Sample.EndsWith("/pain25"));
    }

    [Fact]
    public void Fall_Damage_Plays_The_Fall_Grunt()
    {
        var p = NewPlayer(health: 100f, armor: 0f);
        Hit(p, 20f, DeathTypes.Fall); // DEATH_FALL -> the "fall" grunt regardless of HP bucket
        Assert.Contains(_sound.Played, s => s.Sample.EndsWith("/fall"));
    }

    [Fact]
    public void Pain_Debounce_Suppresses_A_Second_Hit_Within_Half_A_Second()
    {
        var p = NewPlayer(health: 100f, armor: 0f);
        Hit(p, 10.5f);                 // take ~10.5 (>10) -> first pain sound (sets PainFinished = time+0.5)
        int afterFirst = _sound.Played.Count;
        _clock.Time += 0.2f;           // < 0.5s later
        Hit(p, 10.5f);                 // QC: time <= pain_finished -> no new pain sound
        int painAfterSecond = CountVoice(_sound.Played, afterFirst);
        Assert.Equal(0, painAfterSecond);
    }

    // ---- death / drown voice (player.qc ~463-470) --------------------------------------------------

    [Fact]
    public void Death_Voice_On_A_Lethal_Hit()
    {
        var p = NewPlayer(health: 30f, armor: 0f);
        Hit(p, 100f); // lethal -> "death"
        Assert.Contains(_sound.Played, s => s.Sample.EndsWith("/death"));
    }

    [Fact]
    public void Drown_Voice_On_A_Lethal_Drown()
    {
        var p = NewPlayer(health: 30f, armor: 0f);
        Hit(p, 100f, DeathTypes.Drown); // lethal drown -> "drown"
        Assert.Contains(_sound.Played, s => s.Sample.EndsWith("/drown"));
        Assert.DoesNotContain(_sound.Played, s => s.Sample.EndsWith("/death"));
    }

    [Fact]
    public void SvGentle_Suppresses_Pain_And_Death_Voices()
    {
        _cvars.Set("sv_gentle", "1");
        var p = NewPlayer(health: 100f, armor: 0f);
        Hit(p, 20f);   // would be pain75 — suppressed
        Hit(p, 200f);  // lethal — death voice suppressed
        Assert.DoesNotContain(_sound.Played, s => s.Sample.Contains("/pain") || s.Sample.EndsWith("/death"));
        // the armor/body impact is NOT gated by sv_gentle (QC plays it regardless) — body impact still fired.
        Assert.Contains(_sound.Played, s => s.Sample.Contains("bodyimpact"));
    }

    // count voice-channel (CH_VOICE) plays recorded after index `from` (the pain/death/fall grunts).
    private static int CountVoice(IReadOnlyList<RecordingSound.Entry> log, int from)
    {
        int n = 0;
        for (int i = from; i < log.Count; i++)
            if (log[i].Channel == SoundChannel.Voice) n++;
        return n;
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

    private sealed class SoundTestServices : IEngineServices
    {
        public SoundTestServices(ISoundService sound, ICvarService cvars, IGameClock clock)
        { Sound = sound; Cvars = cvars; Clock = clock; }
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; }
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
