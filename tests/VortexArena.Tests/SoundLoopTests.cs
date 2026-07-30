using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Looping/stop sound semantics (DP SV_StartSound entity+channel model): the engine <see cref="SoundService"/>
/// raises Loop/Stop events, and the Godot-free <see cref="SoundWire"/> codec round-trips the wire record those
/// events serialize into (sample, origin, volume, attenuation, channel, source net-id, loop/stop flags). The
/// audible end (AudioStreamPlayer3D) can't be tested headless; this pins the headless-testable contract the
/// server encoder + client decoder both depend on.
/// </summary>
public class SoundLoopTests
{
    [Fact]
    public void Play_WithLoop_RaisesLoopEvent()
    {
        var svc = new SoundService();
        SoundEvent? got = null;
        svc.Broadcast += e => got = e;

        var ent = new Entity { Index = 3, Origin = new Vector3(10f, 20f, 30f) };
        svc.Play(ent, SoundChannel.Weapon, "weapons/arc_loop.wav", volume: 0.8f, attenuation: 1f, loop: true);

        Assert.True(got.HasValue);
        SoundEvent ev = got!.Value;
        Assert.True(ev.Loop);
        Assert.False(ev.Stop);
        Assert.Equal("weapons/arc_loop.wav", ev.Sample);
        Assert.Equal(SoundChannel.Weapon, ev.Channel);
        Assert.Same(ent, ev.Source);
        // SV_StartSound emits from the entity box center; mins/maxs are zero here so it equals the origin.
        Assert.Equal(new Vector3(10f, 20f, 30f), ev.Origin);
    }

    [Fact]
    public void Play_Default_IsOneShotNotLoop()
    {
        var svc = new SoundService();
        SoundEvent? got = null;
        svc.Broadcast += e => got = e;

        svc.Play(new Entity { Index = 1 }, SoundChannel.Auto, "weapons/laser_fire.wav");

        Assert.True(got.HasValue);
        Assert.False(got!.Value.Loop);
        Assert.False(got!.Value.Stop);
    }

    [Fact]
    public void Stop_RaisesStopEvent_OnEntityChannel()
    {
        var svc = new SoundService();
        SoundEvent? got = null;
        svc.Broadcast += e => got = e;

        var ent = new Entity { Index = 7 };
        svc.Stop(ent, SoundChannel.Weapon);

        Assert.True(got.HasValue);
        SoundEvent ev = got!.Value;
        Assert.True(ev.Stop);
        Assert.False(ev.Loop);
        Assert.Equal(SoundChannel.Weapon, ev.Channel);
        Assert.Same(ent, ev.Source);
        Assert.Equal("", ev.Sample); // a stop carries no sample
    }

    [Theory]
    [InlineData("weapons/arc_loop.wav", 1234, 1, true, false)]  // a looping start (Arc beam)
    [InlineData("", 4096, 4, false, true)]                      // a stop record (empty sample, stop flag)
    [InlineData("misc/null.wav", 0, 0, false, false)]           // a plain one-shot
    [InlineData("weapons/uzi_fire.wav", 42, -1, false, false)]  // negative (auto) channel round-trips via sbyte
    public void SoundWire_RoundTrips(string sample, int netId, int channel, bool loop, bool stop)
    {
        var rec = new SoundWire
        {
            Sample = sample,
            Origin = new Vector3(1.5f, -2.25f, 64f),
            Volume = 0.75f,
            Attenuation = 0.5f,
            Channel = channel,
            SourceNetId = netId,
            Loop = loop,
            Stop = stop,
        };

        var w = new BitWriter(64);
        rec.Write(w);
        var r = new BitReader(w.WrittenSpan);
        SoundWire back = SoundWire.Read(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(sample, back.Sample);
        Assert.Equal(rec.Origin, back.Origin);
        Assert.Equal(rec.Volume, back.Volume);
        Assert.Equal(rec.Attenuation, back.Attenuation);
        Assert.Equal(channel, back.Channel);
        Assert.Equal(netId, back.SourceNetId);
        Assert.Equal(loop, back.Loop);
        Assert.Equal(stop, back.Stop);
    }

    /// <summary>
    /// The Arc beam wiring (arc.qc): holding primary emits the looping <c>arc_loop</c> on (actor, CH_WEAPON);
    /// releasing emits a STOP on that same entity+channel — the firing→release edge (st.ArcBeam latch). This is
    /// the gameplay half of the looping-sound contract, the part bots don't exercise in a smoke run.
    /// </summary>
    [Fact]
    public void Arc_FireThenRelease_EmitsLoopThenStop()
    {
        // Minimal engine facade (a flat floor so the beam trace has a world) + a real SoundService to capture.
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        var services = new EngineServices(world);
        Api.Services = services;

        var events = new List<SoundEvent>();
        services.SoundImpl.Broadcast += events.Add;

        var arc = new Arc();
        arc.Configure(); // stock balance defaults (no cvars set → the hardcoded defaults)

        Entity actor = Api.Entities.Spawn();
        actor.ClassName = "player";
        actor.Flags |= EntFlags.Client;
        actor.Origin = new Vector3(0f, 0f, 100f); // up off the floor → the beam fires horizontally into the void (misses)
        actor.SetResource(ResourceType.Cells, 100f);

        var slot = new WeaponSlot(0);
        WeaponSlotState st = actor.WeaponState(slot);

        // --- fire (primary held) → a looping arc_loop on (actor, Weapon) ---
        st.ButtonAttack = true;
        arc.WrThink(actor, slot, FireMode.Primary);

        List<SoundEvent> loops = events.Where(e => e.Loop).ToList();
        Assert.Single(loops);
        Assert.Equal("weapons/arc_loop.wav", loops[0].Sample);
        Assert.Equal(SoundChannel.Weapon, loops[0].Channel);
        Assert.Same(actor, loops[0].Source);
        Assert.False(loops[0].Stop);

        // --- release (no buttons) → a stop on (actor, Weapon) (the firing→release edge) ---
        events.Clear();
        st.ButtonAttack = false;
        arc.WrThink(actor, slot, FireMode.Primary);

        List<SoundEvent> stops = events.Where(e => e.Stop).ToList();
        Assert.Single(stops);
        Assert.Equal(SoundChannel.Weapon, stops[0].Channel);
        Assert.Same(actor, stops[0].Source);
        Assert.False(stops[0].Loop);
    }
}
