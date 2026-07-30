using System;
using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Wave-5 parity: the deferred <c>kill</c> / team-change countdown (QC server/clientkill.qc
/// ClientKill_TeamChange + KillIndicator_Think) — specifically the two previously-DEAD presentation rows
/// sv-clientkill.announcer.number (spoken ANNCE_NUM_KILL_n) and sv-clientkill.centerprint.teamchange
/// (CENTER_TEAMCHANGE_* with ^COUNT). Drives <see cref="KillCountdown"/> directly with a fake clock + a
/// recording notification sink; the floating digit entity and the real GameWorld kill are out of scope here
/// (the kill itself is asserted via the injected <see cref="KillCountdown.PerformKill"/> sink).
/// </summary>
[Collection("GlobalState")]
public class KillCountdownTests : IDisposable
{
    private readonly INotificationSink _prevSink;
    private readonly NotificationSystem.RecordingSink _rec = new();
    private readonly Dictionary<string, bool> _annceEnabledRestore = new();

    public KillCountdownTests()
    {
        Notifications.RegisterAll();
        _prevSink = NotificationSystem.Sink;
        NotificationSystem.Sink = _rec;
    }

    public void Dispose()
    {
        NotificationSystem.Sink = _prevSink;
        // restore any announcer Enabled flags we toggled (process-global registry).
        foreach (var (name, prev) in _annceEnabledRestore)
        {
            var n = Notifications.ByName(MsgType.Annce, name);
            if (n is not null) n.Enabled = prev;
        }
    }

    private void EnableAnnce(string bareName)
    {
        var n = Notifications.ByName(MsgType.Annce, bareName);
        Assert.NotNull(n);
        _annceEnabledRestore.TryAdd(bareName, n!.Enabled);
        n.Enabled = true;
    }

    private (KillCountdown kc, ServerPlayerState st, Player p, float[] now, List<Player> killed) Make(float delay)
    {
        var st = new ServerPlayerState();
        var p = new Player();
        var now = new float[] { 0f };
        var killed = new List<Player>();
        var kc = new KillCountdown
        {
            Now = () => now[0],
            KillDelay = () => delay,
            RoundActiveNotStarted = () => false,
            GameStopped = () => false,
            StateOf = _ => st,
            PerformKill = killed.Add,
            IsRealClient = _ => true,
        };
        return (kc, st, p, now, killed);
    }

    [Fact]
    public void Suicide_Sends_TeamchangeSuicide_Centerprint_With_Count()
    {
        var (kc, _, p, _, _) = Make(2f);
        kc.Begin(p, 0);

        var center = _rec.Log.Single(d => d.Notification.RegistryName == "CENTER_TEAMCHANGE_SUICIDE");
        // cnt = ceil(2) = 2 handed to the ^COUNT arg.
        Assert.Equal(2f, center.FloatArgs.Single());
    }

    [Fact]
    public void Suicide_Speaks_KillCountdown_Numbers_When_Announcer_Enabled()
    {
        EnableAnnce("NUM_KILL_2");
        EnableAnnce("NUM_KILL_1");
        var (kc, _, p, now, _) = Make(2f);
        kc.Begin(p, 0);

        // think@0 -> "2", think@1 -> "1", think@2 -> kill (no number).
        now[0] = 0f; kc.Tick(p);
        now[0] = 1f; kc.Tick(p);

        var spoken = _rec.Log.Where(d => d.Notification.RegistryName.StartsWith("ANNCE_NUM_KILL_"))
            .Select(d => d.Notification.RegistryName).ToList();
        Assert.Equal(new[] { "ANNCE_NUM_KILL_2", "ANNCE_NUM_KILL_1" }, spoken);
    }

    [Fact]
    public void Disabled_Announcer_Stays_Silent_By_Default()
    {
        // NUM_KILL ships disabled (Base N___NEVER): the countdown must not speak unless the player enables it.
        var (kc, _, p, now, _) = Make(2f);
        kc.Begin(p, 0);
        now[0] = 0f; kc.Tick(p);
        now[0] = 1f; kc.Tick(p);
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName.StartsWith("ANNCE_NUM_KILL_"));
    }

    [Fact]
    public void Kill_Lands_After_Delay_Seconds_Not_Instantly()
    {
        var (kc, _, p, now, killed) = Make(2f);
        kc.Begin(p, 0);
        Assert.Empty(killed); // not instant

        now[0] = 0f; kc.Tick(p); Assert.Empty(killed);
        now[0] = 1f; kc.Tick(p); Assert.Empty(killed);
        now[0] = 2f; kc.Tick(p); Assert.Single(killed); // fires at +2s
        Assert.Same(p, killed.Single());
        Assert.False(kc_StateActive(kc, p));
    }

    [Fact]
    public void Zero_Delay_Kills_Immediately()
    {
        var (kc, st, p, _, killed) = Make(0f);
        bool armed = kc.Begin(p, 0);
        Assert.False(armed);
        Assert.Single(killed);
        Assert.False(st.KillCntdownActive);
    }

    [Fact]
    public void Repeated_Begin_Does_Not_Restart_The_Clock()
    {
        var (kc, st, p, now, killed) = Make(3f);
        kc.Begin(p, 0);            // cnt = 3
        now[0] = 0f; kc.Tick(p);   // -> cnt = 2
        Assert.Equal(2, st.KillCntdownCnt);
        kc.Begin(p, 0);            // a second `kill` must NOT reset cnt back to 3
        Assert.Equal(2, st.KillCntdownCnt);
    }

    [Fact]
    public void Teamchange_Targets_Map_To_Their_Center_Notifs()
    {
        var (kc, _, p, _, _) = Make(2f);
        kc.Begin(p, Teams.Red);
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_TEAMCHANGE_RED");

        _rec.Clear();
        var (kc2, _, p2, _, _) = Make(2f);
        kc2.Begin(p2, -2); // spectate
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "CENTER_TEAMCHANGE_SPECTATE");
    }

    private static bool kc_StateActive(KillCountdown kc, Player p) => kc.StateOf(p).KillCntdownActive;
}
