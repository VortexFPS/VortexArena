using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// [T41] Client-feedback drivers — the unit coverage for the four pieces of <c>view.qc</c>/<c>announcer.qc</c>
/// ported in T41:
/// <list type="bullet">
///   <item><b>Countdown number schedule</b> (<see cref="AnnouncerController.PickCountdownNumber"/> /
///         <see cref="AnnouncerController.CountdownRounded"/>) — 3/2/1/prepare picks the right NUM_* names.</item>
///   <item><b>Time-remaining hysteresis</b> (<see cref="AnnouncerController.Tick"/>) — the QC
///         <c>ANNOUNCER_CHECKMINUTE</c> latch fires the 5-/1-minute announcement once per crossing, re-arms on
///         the way back up, and respects <c>cl_announcer_maptime</c> + intermission.</item>
///   <item><b>DamageSystem stat increment</b> — <see cref="Entity.HitsoundDamageDealtTotal"/> accrues the
///         (health + armor) removed only for a hit on a DIFFERENT player, never on self-damage.</item>
///   <item><b>Objective-stat networking lifecycle</b> — the <see cref="EntityField.Feedback"/> delta round-trips
///         HitDamageDealtTotal + NadeTimer/CaptureProgress/ReviveProgress and stays off the wire when unchanged.</item>
/// </list>
/// </summary>
public class ClientFeedbackTests
{
    // ===========================================================================================
    //  Countdown number schedule (Announcer_PickNumber / Announcer_Countdown rounding)
    // ===========================================================================================

    [Theory]
    [InlineData(3, "NUM_GAMESTART_3")]
    [InlineData(2, "NUM_GAMESTART_2")]
    [InlineData(1, "NUM_GAMESTART_1")]
    public void Countdown_Picks_The_GameStart_Number_For_3_2_1(int second, string expected)
        => Assert.Equal(expected, AnnouncerController.PickCountdownNumber(
            AnnouncerController.CountdownKind.GameStart, second));

    [Fact]
    public void Countdown_Terminal_Zero_Is_Not_A_Number_Tick()
    {
        // 0 is the BEGIN tick (handled separately), not a NUM_* announcement.
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.GameStart, 0));
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.RoundStart, 0));
    }

    [Fact]
    public void Countdown_Out_Of_Range_Seconds_Have_No_Announcement()
    {
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.GameStart, -1));
        Assert.Null(AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.GameStart, 11));
    }

    [Fact]
    public void Countdown_RoundStart_Uses_The_RoundStart_Family()
        => Assert.Equal("NUM_ROUNDSTART_3",
            AnnouncerController.PickCountdownNumber(AnnouncerController.CountdownKind.RoundStart, 3));

    [Theory]
    [InlineData(3.0f, 3)]   // exact
    [InlineData(2.4f, 2)]   // floor(0.5 + 2.4) = floor(2.9) = 2
    [InlineData(2.6f, 3)]   // floor(0.5 + 2.6) = floor(3.1) = 3  (QC rounds to nearest)
    [InlineData(0.2f, 0)]   // floor(0.5 + 0.2) = 0 (terminal/BEGIN)
    public void Countdown_Rounded_Mirrors_QC_floor_half_plus(float secondsLeft, int rounded)
        => Assert.Equal(rounded, AnnouncerController.CountdownRounded(secondsLeft));

    // ===========================================================================================
    //  Time-remaining hysteresis (Announcer_Time / ANNOUNCER_CHECKMINUTE)
    // ===========================================================================================

    /// <summary>Build a controller whose context is driven by mutable locals + a list-recording announce sink.</summary>
    private static AnnouncerController MakeTimeAnnouncer(out List<int> fired,
        System.Func<float> timeLeftSeconds, System.Func<bool>? intermission = null, int mapTimeMode = 3)
    {
        var rec = new List<int>();
        fired = rec;
        // Drive Announcer_Time purely off a "time left" supplier: pin game_starttime at 0, time at 0, and feed
        // the remaining time through the (warmup-off) TIMELIMIT path. timeLeft = TIMELIMIT*60 + start - now, so
        // with start=now=0 the TimeLimitMinutes supplier IS timeLeft/60.
        return new AnnouncerController
        {
            Now = () => 0f,
            GameStartTime = () => 0f,
            WarmupStage = () => false,
            WarmupTimeLimitSeconds = () => 0f,
            TimeLimitMinutes = () => timeLeftSeconds() / 60f,
            Intermission = intermission ?? (() => false),
            AnnouncerMapTime = () => mapTimeMode,
            AnnounceRemainingMin = rec.Add,
        };
    }

    [Fact]
    public void TimeRemaining_Fires_Five_Minute_Once_As_It_Crosses_Below_300s()
    {
        float timeLeft = 0f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft);

        // First tick at 320s: above 300, no announcement, latch armed clear after the warmup-stage prime tick.
        timeLeft = 320f; ann.Tick(); // primes warmup_stage_prev (returns), then
        timeLeft = 320f; ann.Tick();
        Assert.Empty(fired);

        // Cross into the (299, 300) arming window -> fire "5" exactly once.
        timeLeft = 299.5f; ann.Tick();
        Assert.Equal(new[] { 5 }, fired);

        // Many more frames still inside / below the window -> no repeat (latch holds).
        timeLeft = 299.0f; ann.Tick();
        timeLeft = 250.0f; ann.Tick();
        Assert.Equal(new[] { 5 }, fired);
    }

    [Fact]
    public void TimeRemaining_Five_Minute_ReArms_When_Time_Climbs_Back_Above_300s()
    {
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft);
        ann.Tick(); ann.Tick(); // prime

        timeLeft = 299.5f; ann.Tick();          // fire 5
        timeLeft = 305f; ann.Tick();            // climb back above 300 -> latch clears (no announce on the climb)
        Assert.Equal(new[] { 5 }, fired);
        timeLeft = 299.5f; ann.Tick();          // cross again -> fires 5 a SECOND time
        Assert.Equal(new[] { 5, 5 }, fired);
    }

    [Fact]
    public void TimeRemaining_Fires_One_Minute_When_It_Crosses_Below_60s()
    {
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft);
        ann.Tick(); ann.Tick(); // prime

        timeLeft = 299.5f; ann.Tick();          // 5 fires
        timeLeft = 59.5f; ann.Tick();           // crosses below 60 -> 1 fires (5 already latched)
        Assert.Equal(new[] { 5, 1 }, fired);
    }

    [Fact]
    public void TimeRemaining_Mode2_Suppresses_The_One_Minute_Announcement()
    {
        // cl_announcer_maptime == 2 -> 5-minute only.
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft, mapTimeMode: 2);
        ann.Tick(); ann.Tick();

        timeLeft = 299.5f; ann.Tick(); // 5 fires
        timeLeft = 59.5f; ann.Tick();  // 1 would fire under mode 1/3, but mode 2 only does 5
        Assert.Equal(new[] { 5 }, fired);
    }

    [Fact]
    public void TimeRemaining_Mode1_Suppresses_The_Five_Minute_Announcement()
    {
        // cl_announcer_maptime == 1 -> 1-minute only.
        float timeLeft = 320f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft, mapTimeMode: 1);
        ann.Tick(); ann.Tick();

        timeLeft = 299.5f; ann.Tick(); // mode 1 does NOT announce 5
        timeLeft = 59.5f; ann.Tick();  // 1 fires
        Assert.Equal(new[] { 1 }, fired);
    }

    [Fact]
    public void TimeRemaining_Is_Silent_During_Intermission()
    {
        float timeLeft = 299.5f;
        var ann = MakeTimeAnnouncer(out List<int> fired, () => timeLeft, intermission: () => true);
        ann.Tick(); ann.Tick(); ann.Tick();
        Assert.Empty(fired);
    }

    // ===========================================================================================
    //  DamageSystem hit-confirmation stat increment (HitsoundDamageDealtTotal)
    // ===========================================================================================

    [Collection("GlobalState")]
    public sealed class DamageStat : System.IDisposable
    {
        private readonly IEngineServices _prevServices;
        private readonly IDamageSystem _prevDamage;
        private readonly DictCvars _cvars = new();
        private readonly MutableClock _clock = new();

        public DamageStat()
        {
            Sounds.RegisterAll();
            _prevServices = Api.Services;
            _prevDamage = Combat.System;
            Api.Services = new MinimalServices(_cvars, _clock);
            Combat.System = new DamageSystem();
            _clock.Time = 100f;
        }

        public void Dispose()
        {
            Combat.System = _prevDamage;
            Api.Services = _prevServices;
        }

        private static Player NewPlayer(float health = 100f, float armor = 0f, int team = 0)
        {
            var p = new Player { Flags = EntFlags.Client, TakeDamage = DamageMode.Yes, Team = team };
            p.Mins = new Vector3(-16, -16, -24);
            p.Maxs = new Vector3(16, 16, 45);
            p.SetResource(ResourceType.Health, health);
            p.SetResource(ResourceType.Armor, armor);
            p.MaxHealth = 100f;
            p.DamageForceScale = 1f;
            return p;
        }

        [Fact]
        public void Hit_On_Another_Player_Accrues_Health_Plus_Armor_Removed()
        {
            var attacker = NewPlayer(team: 5);
            var victim = NewPlayer(health: 100f, armor: 0f, team: 14); // different teams -> no friendly-fire nullify

            // 30 damage, no armor -> 30 health removed -> attacker stat += 30.
            float removed = Combat.Damage(victim, null, attacker, 30f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(30f, removed, 3);
            Assert.Equal(30f, attacker.HitsoundDamageDealtTotal, 3);

            // a second hit accumulates (the client diffs the running total).
            Combat.Damage(victim, null, attacker, 20f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(50f, attacker.HitsoundDamageDealtTotal, 3);
        }

        [Fact]
        public void Hit_Counts_Both_Health_And_Armor_Removed()
        {
            var attacker = NewPlayer(team: 5);
            // armor 100, blockpercent 0.7: a 40 hit saves 28 (armor) + takes 12 (health) -> stat += 40.
            var victim = NewPlayer(health: 100f, armor: 100f, team: 14);
            Combat.Damage(victim, null, attacker, 40f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(40f, attacker.HitsoundDamageDealtTotal, 2);
        }

        [Fact]
        public void Self_Damage_Does_Not_Accrue_The_Hit_Stat()
        {
            var p = NewPlayer(health: 100f, armor: 0f);
            // rocket-jump style self hit: g_balance_selfdamagepercent scales it, but the key assertion is the
            // hit-confirmation stat stays 0 (no beep for hitting yourself).
            Combat.Damage(p, null, p, 30f, DeathTypes.Generic, p.Origin, Vector3.Zero);
            Assert.Equal(0f, p.HitsoundDamageDealtTotal, 3);
        }

        [Fact]
        public void World_Damage_Does_Not_Accrue_Any_Player_Stat()
        {
            var victim = NewPlayer(health: 100f);
            // a world/environment hit (no attacker) removes health but credits no attacker stat.
            Combat.Damage(victim, null, null, 25f, DeathTypes.Generic, victim.Origin, Vector3.Zero);
            Assert.Equal(0f, victim.HitsoundDamageDealtTotal, 3);
        }
    }

    // ===========================================================================================
    //  Objective-stat networking lifecycle (EntityField.Feedback delta)
    // ===========================================================================================

    [Fact]
    public void Feedback_Stats_RoundTrip_Through_The_Delta_Codec()
    {
        var baseline = new NetEntityState { EntNum = 7, Kind = NetEntityKind.Player };
        var current = baseline;
        current.HitDamageDealtTotal = 137f;
        current.NadeTimer = 0.5f;
        current.CaptureProgress = 0.25f;
        current.ReviveProgress = 0.75f;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.Equal(EntityField.Feedback, mask); // only the feedback block on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(137f, got.HitDamageDealtTotal, 3);
        Assert.Equal(0.5f, got.NadeTimer, 3);
        Assert.Equal(0.25f, got.CaptureProgress, 3);
        Assert.Equal(0.75f, got.ReviveProgress, 3);
        Assert.Equal(NetEntityKind.Player, got.Kind); // carried from baseline
    }

    [Fact]
    public void ClientColors_RoundTrip_Through_The_Delta_Codec()
    {
        // [r15 #43] the packed 16*shirt+pants clientcolors slice (EntityField.Colors): a bot/player picking
        // FFA profile colors must reach the client render entity; a colorless player (0) keeps the bit clear.
        var baseline = new NetEntityState { EntNum = 3, Kind = NetEntityKind.Player };
        var current = baseline;
        current.Colors = 0x6B; // shirt 6, pants 11

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, current);
        Assert.Equal(EntityField.Colors, mask); // only the colors byte on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(0x6B, got.Colors);

        // unchanged colors stay off the wire
        var same = current;
        same.Origin = new Vector3(2, 0, 0);
        var w2 = new BitWriter();
        EntityField mask2 = EntityStateCodec.WriteDelta(w2, current, same);
        Assert.Equal(EntityField.Origin, mask2);
        Assert.False(mask2.HasFlag(EntityField.Colors));
    }

    [Fact]
    public void Feedback_Stats_Stay_Off_The_Wire_When_Unchanged()
    {
        // a player whose nade timer is mid-charge but identical between frames: the mask must NOT include
        // Feedback (idle objective stats cost nothing).
        var baseline = new NetEntityState { EntNum = 7, NadeTimer = 0.4f, HitDamageDealtTotal = 50f };
        var same = baseline;
        same.Origin = new Vector3(1, 0, 0); // only the origin moved

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, same);
        Assert.Equal(EntityField.Origin, mask);
        Assert.False(mask.HasFlag(EntityField.Feedback));
    }

    [Fact]
    public void NadeTimer_Clearing_To_Zero_Is_A_Tracked_Change()
    {
        // a thrown/expired nade clears the timer back to 0 — that's an objective-stat lifecycle edge the wire
        // must carry (the ring disappears on the client).
        var charged = new NetEntityState { EntNum = 7, NadeTimer = 0.9f };
        var cleared = charged;
        cleared.NadeTimer = 0f;

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, charged, cleared);
        Assert.Equal(EntityField.Feedback, mask);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, charged);
        Assert.Equal(0f, got.NadeTimer, 5);
    }

    // ===========================================================================================
    //  scaffolding
    // ===========================================================================================

    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) => _v.TryGetValue(name, out var s) && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { if (!_v.ContainsKey(name)) _v[name] = defaultValue; }
    }

    private sealed class MinimalServices : IEngineServices
    {
        public MinimalServices(ICvarService cvars, IGameClock clock) { Cvars = cvars; Clock = clock; }
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; }
        public ICvarService Cvars { get; }
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
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
        private sealed class NullSound : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
