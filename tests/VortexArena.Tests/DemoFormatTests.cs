using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VortexArena.Net;
using VortexArena.Net.Demo;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests the demo record/replay spine (T62/T63, planning/specs/demo-replay-and-spectator.md §5/§12): the
/// <c>.xgd</c> container round-trip (record a synthetic entity-set sequence → write → read → the playback
/// reproduces the original sets exactly, across keyframe and delta boundaries), the seek contract (nearest
/// preceding keyframe + re-simulate forward yields the same state forward play reaches, entities flagged
/// Teleported across the discontinuity), the two-clock time controls (pause/speed clamp), event windowing
/// (forward emission only; a seek skips crossed events), the crash-truncation recovery (index rebuilt by
/// scanning), and the replay id remap that keeps recorded ids off the live viewers' id space.
/// </summary>
public class DemoFormatTests
{
    private const float TickRate = 72f;
    private const float TickDt = 1f / TickRate;

    private static NetEntityState Player(int id, Vector3 origin, int health = 100) => new()
    {
        EntNum = id, Kind = NetEntityKind.Player, ModelIndex = 3, Origin = origin,
        Velocity = new Vector3(100, 0, 0), Health = health, Model = "models/player/erebus.iqm",
    };

    private static DemoHeaderInfo Header() => new()
    {
        BuildParity = 0xC0FFEE,
        TickRate = TickRate,
        MapName = "catharsis",
        Gametype = "dm",
    };

    /// <summary>Record <paramref name="seconds"/> of a deterministic two-player scene at 72 Hz into a stream;
    /// entity 2 despawns halfway through and a third spawns. Returns the expected set at every recorded tick.</summary>
    private static List<Dictionary<int, NetEntityState>> RecordScene(Stream stream, float seconds,
        float keyframeInterval = 1f, bool finalize = true)
    {
        var expected = new List<Dictionary<int, NetEntityState>>();
        var rec = new DemoRecorder(stream, Header(), keyframeInterval, leaveOpen: true);
        int ticks = (int)(seconds * TickRate);
        for (int t = 0; t < ticks; t++)
        {
            float time = 10f + t * TickDt; // demos never start at time 0 in a real match
            var set = new Dictionary<int, NetEntityState>
            {
                [1] = Player(1, new Vector3(t * 4f, 0, 16)),
            };
            if (t < ticks / 2)
                set[2] = Player(2, new Vector3(0, t * 2f, 16), health: 100 - t % 50);
            else
                set[16384 + 7] = new NetEntityState { EntNum = 16384 + 7, Kind = NetEntityKind.Projectile, Origin = new Vector3(t, t, t) };
            rec.RecordTick(time, set);
            expected.Add(set);
        }
        if (finalize)
            rec.Stop();
        return expected;
    }

    private static void AssertSetsEqual(Dictionary<int, NetEntityState> want, IReadOnlyDictionary<int, NetEntityState> got, int remapOffset = 0)
    {
        Assert.Equal(want.Count, got.Count);
        foreach (KeyValuePair<int, NetEntityState> kv in want)
        {
            int id = kv.Key < DemoFormat.LiveEntityIdBase ? kv.Key + remapOffset : kv.Key;
            Assert.True(got.ContainsKey(id), $"entity {id} missing");
            NetEntityState g = got[id];
            // strip the flags playback may add (Teleported-on-seek) before the field comparison
            g.Flags &= ~NetEntityFlags.Teleported;
            g.EntNum = kv.Value.EntNum;
            g.Owner = kv.Value.Owner;
            Assert.Equal(EntityField.None, NetEntityState.Diff(kv.Value, g));
        }
    }

    private static Dictionary<int, NetEntityState> Sample(DemoPlayback pb)
    {
        var into = new Dictionary<int, NetEntityState>();
        pb.CopyEntities(into);
        return into;
    }

    [Fact]
    public void RoundTrip_Reproduces_Every_Tick_Exactly()
    {
        using var ms = new MemoryStream();
        List<Dictionary<int, NetEntityState>> expected = RecordScene(ms, seconds: 3f);

        ms.Position = 0;
        using var pb = new DemoPlayback(ms, leaveOpen: true);
        Assert.Equal("catharsis", pb.Header.MapName);
        Assert.Equal(0xC0FFEEu, pb.Header.BuildParity);

        // step the playhead one tick at a time; every reconstructed set must match the recorded one, including
        // across the keyframe boundaries (keyframeInterval 1 s → a keyframe every 72 ticks) and the despawn.
        AssertSetsEqual(expected[0], Sample(pb), DemoFormat.ReplayPlayerIdOffset);
        for (int t = 1; t < expected.Count; t++)
        {
            pb.Advance(TickDt);
            AssertSetsEqual(expected[t], Sample(pb), DemoFormat.ReplayPlayerIdOffset);
        }
    }

    [Fact]
    public void Seek_Matches_Forward_Play_And_Flags_Teleported()
    {
        using var ms = new MemoryStream();
        List<Dictionary<int, NetEntityState>> expected = RecordScene(ms, seconds: 4f);

        // forward play to an arbitrary mid-demo target…
        float target = 2.5f;
        ms.Position = 0;
        using (var forward = new DemoPlayback(ms, leaveOpen: true))
        {
            forward.Advance(target);
            // …and an instant seek to the same target on a fresh playback…
            ms.Position = 0;
            using var seeker = new DemoPlayback(ms, leaveOpen: true);
            seeker.Seek(target);

            Dictionary<int, NetEntityState> seeked = Sample(seeker);
            // seek determinism (spec §12): identical state to forward play at the same playhead
            Dictionary<int, NetEntityState> played = Sample(forward);
            Assert.Equal(played.Count, seeked.Count);
            foreach (KeyValuePair<int, NetEntityState> kv in played)
            {
                Assert.True(seeked.ContainsKey(kv.Key));
                NetEntityState s = seeked[kv.Key];
                // every entity in the first post-seek sample snaps (Teleported) instead of lerping the jump
                Assert.True((s.Flags & NetEntityFlags.Teleported) != 0, "post-seek entity not flagged Teleported");
                s.Flags &= ~NetEntityFlags.Teleported;
                NetEntityState p = kv.Value;
                p.Flags &= ~NetEntityFlags.Teleported;
                Assert.Equal(EntityField.None, NetEntityState.Diff(p, s));
            }

            // the flag is one-shot: the next sample interpolates normally again
            foreach (NetEntityState s in Sample(seeker).Values)
                Assert.True((s.Flags & NetEntityFlags.Teleported) == 0);
        }
        _ = expected;
    }

    [Fact]
    public void Pause_Freezes_The_Playhead_And_Speed_Scales_It()
    {
        using var ms = new MemoryStream();
        RecordScene(ms, seconds: 3f);
        ms.Position = 0;
        using var pb = new DemoPlayback(ms, leaveOpen: true);

        pb.Paused = true;
        pb.Advance(1f);
        Assert.Equal(0f, pb.PlayheadSeconds);

        pb.Paused = false;
        pb.Speed = 2f;
        pb.Advance(0.5f);
        Assert.Equal(1f, pb.PlayheadSeconds, 3);

        pb.Speed = 0.25f;
        pb.Advance(1f);
        Assert.Equal(1.25f, pb.PlayheadSeconds, 3);

        // clamp: the T63 speed range is 0.25×–4× (a 0/negative factor is pause's job, not speed's)
        pb.Speed = 0.01f;
        Assert.Equal(DemoPlayback.MinSpeed, pb.Speed);
        pb.Speed = 100f;
        Assert.Equal(DemoPlayback.MaxSpeed, pb.Speed);

        // the playhead clamps at the end and reports it
        pb.Speed = 4f;
        pb.Advance(100f);
        Assert.Equal(pb.DurationSeconds, pb.PlayheadSeconds, 3);
        Assert.True(pb.EndReached);
    }

    [Fact]
    public void Events_Emit_Forward_Only_And_Seek_Skips_Them()
    {
        using var ms = new MemoryStream();
        var rec = new DemoRecorder(ms, Header(), keyframeIntervalSeconds: 1f, leaveOpen: true);
        byte[] packetA = { 9, 1, 2, 3 };
        byte[] packetB = { 9, 4, 5, 6 };
        for (int t = 0; t < 144; t++) // 2 s
        {
            rec.RecordTick(10f + t * TickDt, new Dictionary<int, NetEntityState> { [1] = Player(1, new Vector3(t, 0, 0)) });
            if (t == 36) rec.RecordEventPacket(packetA, reliable: false);
            if (t == 100) rec.RecordEventPacket(packetB, reliable: true);
        }
        rec.Stop();

        ms.Position = 0;
        using var pb = new DemoPlayback(ms, leaveOpen: true);

        // crossing the first event's timestamp emits exactly it, on the channel it was recorded from
        pb.Advance(0.75f);
        Assert.Single(pb.PendingEvents);
        Assert.Equal(packetA, pb.PendingEvents[0].Data);
        Assert.False(pb.PendingEvents[0].Reliable);
        pb.ClearPendingEvents();

        // a seek across the second event does NOT re-fire it (events belong to skipped-over time)…
        pb.Seek(1.9f);
        Assert.Empty(pb.PendingEvents);

        // …and seeking back then playing forward emits it again when genuinely crossed
        pb.Seek(1.2f);
        pb.Advance(0.5f);
        Assert.Single(pb.PendingEvents);
        Assert.Equal(packetB, pb.PendingEvents[0].Data);
        Assert.True(pb.PendingEvents[0].Reliable);
    }

    [Fact]
    public void Truncated_File_Without_Index_Still_Plays_And_Seeks()
    {
        using var ms = new MemoryStream();
        RecordScene(ms, seconds: 2f, finalize: false); // crash: no Stop() → no index, header duration 0

        ms.Position = 0;
        using var pb = new DemoPlayback(ms, leaveOpen: true);
        Assert.True(pb.DurationSeconds > 1.9f, $"scan-derived duration wrong: {pb.DurationSeconds}");

        pb.Seek(1.5f);
        Dictionary<int, NetEntityState> set = Sample(pb);
        Assert.NotEmpty(set);
    }

    [Fact]
    public void Replay_Ids_Never_Collide_With_Live_Viewer_Ids()
    {
        // recorded player ids (small) land in [offset, entity-base); recorded world entities stay put
        Assert.Equal(DemoFormat.ReplayPlayerIdOffset + 1, DemoPlayback.RemapId(1));
        Assert.Equal(DemoFormat.ReplayPlayerIdOffset + 100, DemoPlayback.RemapId(100));
        Assert.True(DemoPlayback.RemapId(8191) < DemoFormat.LiveEntityIdBase + 8192);
        Assert.Equal(DemoFormat.LiveEntityIdBase + 7, DemoPlayback.RemapId(DemoFormat.LiveEntityIdBase + 7));
    }

    [Fact]
    public void Roster_CatchesUp_MidRecording_Joins_Deduplicated()
    {
        // The header roster snapshot at record start misses late joiners (sv_autodemo starts recording at
        // match start, before the bots connect) — AddRosterEntry accumulates them and Finish serializes the
        // FINAL roster in the trailer, which the reader prefers over the header roster.
        var header = Header();
        header.Roster.Add(new DemoRosterEntry(3, "Early", 5, "models/player/erebus.iqm", 5));

        using var ms = new MemoryStream();
        var rec = new DemoRecorder(ms, header, leaveOpen: true);
        rec.RecordTick(1f, new Dictionary<int, NetEntityState> { [3] = Player(3, Vector3.Zero) });
        rec.AddRosterEntry(new DemoRosterEntry(4, "LateBot", 14, "models/player/gak.iqm", 14));
        rec.AddRosterEntry(new DemoRosterEntry(3, "Early-again", 5, "models/player/erebus.iqm", 5)); // dupe id — ignored
        rec.AddRosterEntry(new DemoRosterEntry(5, "LaterBot", 14, "models/player/nyx.iqm", 14));
        rec.Stop();

        ms.Position = 0;
        using var pb = new DemoPlayback(ms, leaveOpen: true);
        Assert.Equal(3, pb.Header.Roster.Count);
        Assert.Equal("Early", pb.Header.Roster[0].Name);   // dedupe kept the original entry for id 3
        Assert.Equal(4, pb.Header.Roster[1].NetId);
        Assert.Equal("LateBot", pb.Header.Roster[1].Name);
        Assert.Equal("LaterBot", pb.Header.Roster[2].Name);
    }

    [Fact]
    public void Roster_Truncated_File_Falls_Back_To_Header_Roster()
    {
        // A crash before Finish loses the trailer (index + final roster) — the reader rebuilds the index by
        // scanning and the record-start header roster stands, exactly like the index fallback.
        var header = Header();
        header.Roster.Add(new DemoRosterEntry(3, "Early", 5, "models/player/erebus.iqm", 5));

        using var ms = new MemoryStream();
        var rec = new DemoRecorder(ms, header, leaveOpen: true);
        rec.RecordTick(1f, new Dictionary<int, NetEntityState> { [3] = Player(3, Vector3.Zero) });
        rec.RecordTick(1f + TickDt, new Dictionary<int, NetEntityState> { [3] = Player(3, new Vector3(4, 0, 0)) });
        rec.AddRosterEntry(new DemoRosterEntry(4, "LateBot", 14, "models/player/gak.iqm", 14));
        byte[] truncated = ms.ToArray(); // snapshot BEFORE Stop — no trailer written
        rec.Stop();

        using var cut = new MemoryStream(truncated);
        using var pb = new DemoPlayback(cut, leaveOpen: true);
        Assert.Single(pb.Header.Roster);                    // the late join is lost with the trailer
        Assert.Equal("Early", pb.Header.Roster[0].Name);
        Assert.True(pb.DurationSeconds > 0f);               // frames were still scanned (index rebuilt)
    }

    [Fact]
    public void Header_RoundTrips_Roster_And_Rejects_Garbage()
    {
        var header = Header();
        header.Roster.Add(new DemoRosterEntry(3, "Grunt", 5, "models/player/erebus.iqm", 5));
        header.Roster.Add(new DemoRosterEntry(4, "^1Red^7Bot", 14, "models/player/gak.iqm", 14));

        using var ms = new MemoryStream();
        var rec = new DemoRecorder(ms, header, leaveOpen: true);
        rec.RecordTick(1f, new Dictionary<int, NetEntityState> { [3] = Player(3, Vector3.Zero) });
        rec.Stop();

        ms.Position = 0;
        using var pb = new DemoPlayback(ms, leaveOpen: true);
        Assert.Equal(2, pb.Header.Roster.Count);
        Assert.Equal("Grunt", pb.Header.Roster[0].Name);
        Assert.Equal(4, pb.Header.Roster[1].NetId);
        Assert.Equal("^1Red^7Bot", pb.Header.Roster[1].Name);

        // a non-demo file is rejected with an honest error, never misread
        using var junk = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.Throws<InvalidDataException>(() => new DemoPlayback(junk, leaveOpen: true));
    }
}
