using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T40: the pre-match / round-start countdown announcer, driven server-side (the port has no CSQC
/// <c>Announcer_Countdown</c> timer). Asserts <see cref="WarmupController.OnCountdownTick"/> and
/// <see cref="RoundHandler.OnCountdownTick"/> fire once per whole second (count..1 then 0) — NOT once per
/// frame — and that the GameWorld-side mapping they feed broadcasts NUM_GAMESTART_* / NUM_ROUNDSTART_* +
/// COUNTDOWN_* + BEGIN via NotificationSystem. GlobalState collection (process-global Api.Services + Sink).
/// </summary>
[Collection("GlobalState")]
public class CountdownAnnouncerTests : IDisposable
{
    private readonly IEngineServices _prevServices;
    private readonly INotificationSink _prevSink;
    private readonly MutableClock _clock = new();
    private readonly NotificationSystem.RecordingSink _rec = new();

    public CountdownAnnouncerTests()
    {
        Notifications.RegisterAll();
        _prevServices = Api.Services;
        Api.Services = new ClockServices(_clock);
        _prevSink = NotificationSystem.Sink;
        NotificationSystem.Sink = _rec;
        _clock.Time = 0f;
    }

    public void Dispose()
    {
        NotificationSystem.Sink = _prevSink;
        Api.Services = _prevServices;
    }

    // ---- WarmupController game-start countdown -----------------------------------------------------

    [Fact]
    public void Warmup_GameStart_Countdown_Fires_Each_Second_10_Down_To_0()
    {
        var w = new WarmupController { Roster = () => System.Array.Empty<Player>() };
        var ticks = new List<int>();
        w.OnCountdownTick = ticks.Add;

        // QC world.qc:2221 -> Begin (no warmup) only arms the join window (game_starttime = now + g_start_delay,
        // 0 on a listen server). The live countdown is armed by ReadyRestart_force (vote.qc:460):
        // game_starttime = now + RESTART_COUNTDOWN (vote.qh:66 = 10s).
        Api.Cvars.Set("g_warmup", "0");
        w.Begin();
        w.ReadyRestart(forceWarmupEnd: true);
        Assert.Equal(WarmupController.RestartCountdown, w.GameStartTime, 3);

        // step the clock one second at a time; Think() drives the announcer.
        for (int sec = 0; sec <= 11; sec++)
        {
            _clock.Time = sec;
            w.Think();
        }

        // QC countdown_rounded: 10,9,..,1 then 0 (BEGIN). (At t=0 remaining=10 -> 10; ...; t=10 -> 0.)
        Assert.Equal(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 }, ticks);
    }

    [Fact]
    public void Warmup_Countdown_Does_Not_Spam_When_Think_Runs_Many_Times_Per_Second()
    {
        var w = new WarmupController { Roster = () => System.Array.Empty<Player>() };
        var ticks = new List<int>();
        w.OnCountdownTick = ticks.Add;
        Api.Cvars.Set("g_warmup", "0");
        w.Begin();
        w.ReadyRestart(forceWarmupEnd: true); // arms the RESTART_COUNTDOWN (10s) game-start countdown.

        // many frames inside the first second -> exactly one tick (for "10"), not one per frame.
        for (int frame = 0; frame < 10; frame++)
        {
            _clock.Time = frame * 0.05f; // 10 frames within the first 0.5s
            w.Think();
        }
        Assert.Equal(new[] { 10 }, ticks);
    }

    [Fact]
    public void Warmup_Countdown_Maps_To_GameStart_Announcers_And_Begin()
    {
        var w = new WarmupController { Roster = () => System.Array.Empty<Player>() };
        w.OnCountdownTick = n => GameStartCountdownBroadcast(n);
        Api.Cvars.Set("g_warmup", "0");
        w.Begin();
        w.ReadyRestart(forceWarmupEnd: true); // arms the RESTART_COUNTDOWN (10s) game-start countdown.
        for (int sec = 0; sec <= 11; sec++) { _clock.Time = sec; w.Think(); }

        // NUM_GAMESTART_5..1 fire (the registry enables n<=5) + COUNTDOWN_GAMESTART centers + BEGIN at 0.
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_NUM_GAMESTART_5");
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_NUM_GAMESTART_1");
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_COUNTDOWN_GAMESTART");
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_BEGIN");
    }

    // ---- RoundHandler round-start countdown --------------------------------------------------------

    [Fact]
    public void Round_Countdown_Fires_Each_Second_Down_To_0_Not_Per_Frame()
    {
        var r = new XonoticGodot.Server.RoundHandler { GameStartTime = 0f };
        var ticks = new List<int>();
        r.OnCountdownTick = ticks.Add;
        // a round that can always start, never ends (we only watch the countdown).
        r.Spawn(canRoundStart: () => true, canRoundEnd: () => false);

        // default countdown is 5: cnt starts at 6, decrements once a second. Run many frames across 6s.
        for (int frame = 0; frame <= 60; frame++)
        {
            _clock.Time = frame * 0.1f; // 0.1s steps -> 10 frames per second
            r.Think();
        }

        // visible seconds 5,4,3,2,1 then 0 (round begins) — exactly once each, despite 10 frames/second.
        Assert.Equal(new[] { 5, 4, 3, 2, 1, 0 }, ticks);
    }

    [Fact]
    public void Round_Countdown_Maps_To_RoundStart_Announcers_And_Begin()
    {
        var r = new XonoticGodot.Server.RoundHandler { GameStartTime = 0f };
        r.OnCountdownTick = n => RoundStartCountdownBroadcast(r.RoundsPlayed, n);
        r.Spawn(canRoundStart: () => true, canRoundEnd: () => false);
        for (int frame = 0; frame <= 60; frame++) { _clock.Time = frame * 0.1f; r.Think(); }

        // the registry enables NUM_ROUNDSTART_n only for n<=3.
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_NUM_ROUNDSTART_3");
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_NUM_ROUNDSTART_1");
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_COUNTDOWN_ROUNDSTART");
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "ANNCE_BEGIN");
        // NUM_ROUNDSTART_5 ships disabled, so it must NOT be in the feed even though tick(5) fired.
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName == "ANNCE_NUM_ROUNDSTART_5");
    }

    // ---- the GameWorld-side mapping under test (mirrors the seam the orchestrator wires) -----------
    // These mirror exactly the snippet reported in seamRequests.gameWorldWiring so the test exercises the
    // real broadcast shape. QC's Local_Notification skips a disabled notification (its nent_enabled cvar); the
    // port's NotificationSystem.Send does NOT, so the mapping gates the NUM announcer on the registry's Enabled
    // flag — which already encodes the SHIPPED defaults (NUM_GAMESTART enabled n<=5, NUM_ROUNDSTART n<=3).

    private static void AnnceIfEnabled(string bareName)
    {
        var n = Notifications.ByName(MsgType.Annce, bareName);
        if (n is { Enabled: true })
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Annce, bareName);
    }

    private static void GameStartCountdownBroadcast(int secondsLeft)
    {
        if (secondsLeft <= 0)
        {
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Multi, "BEGIN"); // ANNCE_BEGIN + center
            return;
        }
        if (secondsLeft > 5) // QC Announcer_Gamestart: PREPARE only when the countdown exceeds 5s.
            AnnceIfEnabled("PREPARE");
        AnnceIfEnabled("NUM_GAMESTART_" + secondsLeft);
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "COUNTDOWN_GAMESTART", secondsLeft);
    }

    private static void RoundStartCountdownBroadcast(int round, int secondsLeft)
    {
        if (secondsLeft <= 0)
        {
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Multi, "BEGIN");
            return;
        }
        AnnceIfEnabled("NUM_ROUNDSTART_" + secondsLeft);
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "COUNTDOWN_ROUNDSTART", round + 1, secondsLeft);
    }

    // ---- scaffolding -------------------------------------------------------------------------------

    private sealed class ClockServices : IEngineServices
    {
        public ClockServices(IGameClock clock) { Clock = clock; }
        public ITraceService Trace { get; } = new NullTrace();
        public IGameClock Clock { get; }
        public ICvarService Cvars { get; } = new DictCvars();
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();

        private sealed class NullTrace : ITraceService
        {
            public TraceResult Trace(System.Numerics.Vector3 start, System.Numerics.Vector3 mins, System.Numerics.Vector3 maxs, System.Numerics.Vector3 end, MoveFilter filter, Entity? ignore) => TraceResult.Miss(end);
            public int PointContents(System.Numerics.Vector3 point) => 0;
            public bool CheckPvs(System.Numerics.Vector3 viewpoint, System.Numerics.Vector3 target) => true;
        }
        private sealed class NullEntities : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, System.Numerics.Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, System.Numerics.Vector3 mins, System.Numerics.Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(System.Numerics.Vector3 origin, float radius) => System.Array.Empty<Entity>();
        }
        private sealed class NullSound : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out System.Numerics.Vector3 origin, out System.Numerics.Vector3 forward, out System.Numerics.Vector3 right, out System.Numerics.Vector3 up)
            { origin = forward = right = up = System.Numerics.Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }

    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) => _v.TryGetValue(name, out var s) && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { if (!_v.ContainsKey(name)) _v[name] = defaultValue; }
    }
}
