// Tests for the r16 CTF-playtest fix batch (implosion, 2026-07-10):
//  1. Teamplay force-colors off-by-one — the port's Teams constants ARE color codes (QC's team-1), so
//     SetPlayerColors must NOT subtract 1 again (red painted GREEN, blue painted YELLOW), and the SetColor
//     round-trip must not re-ADD 1 (the two bugs cancelled for team identity, hiding the paint bug).
//  2. WireObjectiveSpawns classname gates — multi-gametype maps (implosion carries 12 dom_* entities) must
//     not turn foreign objectives into flags/points/goals: implosion's dom lump made CTF a silent ONE-FLAG
//     match with the carriable flag at the mapper's "Center" dom point.
//  3. CTF pickup per-audience fan-out (QC sv_ctf.qc:790-801) — every OTHER player gets an explicit line
//     naming WHICH flag was taken (teammates "Protect them!", the flag's team "Retrieve it!").
// (Fix 4 of the batch — the ±4096 coord wrap — is pinned in the codec tests, not here.)

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

[Collection("GlobalState")]
public sealed class CtfTeamplayR16FixTests : System.IDisposable
{
    public CtfTeamplayR16FixTests()
    {
        GameRegistries.Bootstrap();
        Api.Services = new EngineServices(new CollisionWorld());
    }

    public void Dispose()
    {
        GametypeObjectiveSpawns.Sink = null;
        NotificationSystem.Recorder.Clear();
        NotificationSystem.Sink = NotificationSystem.Recorder;
    }

    // ---- 1. teamplay forced colors -------------------------------------------------------------------

    [Theory]
    [InlineData(Teams.Red, 68)]     // 17*4  → shirt+pants both palette 4 (red)
    [InlineData(Teams.Blue, 221)]   // 17*13 → palette 13 (blue)
    [InlineData(Teams.Yellow, 204)] // 17*12
    [InlineData(Teams.Pink, 153)]   // 17*9
    public void SetTeam_ForcesTheTeamColorCode_NotTheGreenOffByOne(int team, int expectedColors)
    {
        var tp = new Teamplay(isTeamGame: true, teamCount: 4);
        var p = new Player { Flags = EntFlags.Client };

        Assert.True(tp.SetTeam(p, team));

        // QC SetPlayerColors(team_code) → clientcolors = 17*code (shirt AND pants forced to the team hue).
        // The old double `-1` gave red 17*3 = 51 → palette 3 = pure GREEN (the r16 all-green CTF bug).
        Assert.Equal(expectedColors, p.ClientColors);
        // And the SetColor round-trip re-derives the SAME team (no +1 — Team holds the color code itself).
        Assert.Equal(team, (int)p.Team);
    }

    [Fact]
    public void AssignBestTeam_AlsoForcesTheColorCode()
    {
        var tp = new Teamplay(isTeamGame: true, teamCount: 2);
        var joiner = new Player { Flags = EntFlags.Client };
        int team = tp.AssignBestTeam(joiner, new List<Player>());

        Assert.Contains(team, new[] { Teams.Red, Teams.Blue });
        Assert.Equal(17 * (team & 15), joiner.ClientColors);
        Assert.Equal(team, (int)joiner.Team);
    }

    // ---- 2. objective-spawn classname gates ----------------------------------------------------------

    private static EntityDict Dict(string cls, Vector3 origin = default, params (string k, string v)[] fields)
    {
        var d = new EntityDict { ClassName = cls, Origin = origin };
        foreach (var (k, v) in fields) d.Fields[k] = v;
        return d;
    }

    /// <summary>An implosion-shaped lump: two mirrored team flags + the dom_* entities of the map's
    /// Domination mode (no `team` keys — exactly what made them spawn as neutral flags).</summary>
    private static List<EntityDict> ImplosionLikeLump() => new()
    {
        Dict("item_flag_team1", new Vector3(-736, -864, 128)),
        Dict("dom_team", default, ("netname", "Red")),
        Dict("dom_team", default, ("netname", "Blue")),
        Dict("dom_controlpoint", new Vector3(440, -336, 296), ("message", " has captured a control point")),
        Dict("item_flag_team2", new Vector3(5152, -5024, 128)),
        Dict("dom_controlpoint", new Vector3(2208, -2944, 320), ("message", " has captured the Center control point")),
    };

    [Fact]
    public void CtfOnAMultiGametypeMap_SpawnsOnlyTheTwoRealFlags()
    {
        var world = new GameWorld(new CollisionWorld(), ImplosionLikeLump());
        world.Boot("ctf");
        var ctf = Assert.IsType<Ctf>(world.GameType);

        // The r16 bug: the 4 dom_* entities (no team key → Teams.None) each spawned a NEUTRAL flag,
        // flipping OneFlag on and putting the live carriable flag at the "Center" dom point.
        Assert.False(ctf.OneFlag);
        Assert.Null(ctf.NeutralFlag);
        Assert.Equal(2, ctf.Flags.Count);

        // Both real flags at their mirrored corners (SpawnFlag lifts origin by the QC dropToFloor +32).
        Assert.Equal(new Vector3(-736, -864, 160), ctf.FlagOf(Teams.Red)!.HomeOrigin);
        Assert.Equal(new Vector3(5152, -5024, 160), ctf.FlagOf(Teams.Blue)!.HomeOrigin);
    }

    [Fact]
    public void DominationOnAMultiGametypeMap_IgnoresTheFlags()
    {
        var world = new GameWorld(new CollisionWorld(), ImplosionLikeLump());
        world.Boot("dom");
        var dom = Assert.IsType<Domination>(world.GameType);

        // Only the 2 dom_controlpoints place points — the 2 flags and 2 dom_team holders must not.
        Assert.Equal(2, dom.Points.Count);
    }

    // ---- 3. pickup per-audience fan-out --------------------------------------------------------------

    [Fact]
    public void Pickup_FansOutPerAudience_NamingTheFlag()
    {
        var ctf = new Ctf();
        FlagState red = ctf.SpawnFlag(Teams.Red, new Vector3(-736, -864, 128));

        var carrier = new Player { Flags = EntFlags.Client, Team = Teams.Blue, NetName = "Thief" };
        var carrierMate = new Player { Flags = EntFlags.Client, Team = Teams.Blue, NetName = "Mate" };
        var victim1 = new Player { Flags = EntFlags.Client, Team = Teams.Red, NetName = "Vic1" };
        var victim2 = new Player { Flags = EntFlags.Client, Team = Teams.Red, NetName = "Vic2" };
        var roster = new List<Player> { carrier, carrierMate, victim1, victim2 };
        ctf.Tick(roster); // caches the roster the fan-out reads (the host does this every tick)

        NotificationSystem.Recorder.Clear();
        Assert.True(ctf.Pickup(carrier, red));

        var log = NotificationSystem.Recorder.Log;
        // Carrier's teammate: CHOICE naming the RED flag ("Protect them!"), targeted at exactly the mate.
        NotificationDispatch mate = Assert.Single(log, d =>
            d.Notification.RegistryName.Contains("CTF_PICKUP_TEAM_RED") && ReferenceEquals(d.Target, carrierMate));
        Assert.Equal(NotifBroadcast.One, mate.Broadcast);
        // The flag's own team: CHOICE_CTF_PICKUP_ENEMY ("your flag! Retrieve it!") to BOTH red players.
        Assert.Equal(2, log.Count(d =>
            d.Notification.RegistryName.EndsWith("CTF_PICKUP_ENEMY") &&
            (ReferenceEquals(d.Target, victim1) || ReferenceEquals(d.Target, victim2))));
        // The carrier gets the centerprint but NO team/enemy line.
        Assert.DoesNotContain(log, d =>
            ReferenceEquals(d.Target, carrier) &&
            (d.Notification.RegistryName.Contains("PICKUP_TEAM") || d.Notification.RegistryName.Contains("PICKUP_ENEMY")));
    }

    [Fact]
    public void Pickup_WithoutARoster_StillAnnouncesTheBasics()
    {
        // Bare unit-test path (no Tick roster): the fan-out politely skips; voice + feed + carrier line remain.
        var ctf = new Ctf();
        FlagState red = ctf.SpawnFlag(Teams.Red, default);
        var carrier = new Player { Flags = EntFlags.Client, Team = Teams.Blue, NetName = "Solo" };

        NotificationSystem.Recorder.Clear();
        Assert.True(ctf.Pickup(carrier, red));
        Assert.Contains(NotificationSystem.Recorder.Log, d => d.Notification.RegistryName.Contains("CTF_PICKUP_RED"));
    }
}
