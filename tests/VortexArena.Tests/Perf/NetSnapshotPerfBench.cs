using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using XonoticGodot.Net;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// T33 perf bench (BotPerfBench pattern — measurement, not a CI assertion) for the snapshot
/// delta-compression hot path: <see cref="ServerSnapshotHistory.EncodeSnapshot"/> +
/// <see cref="ClientSnapshotHistory.DecodeSnapshot"/> round-trips at a realistic load
/// (16 clients × 256 entities × 72 Hz × 30 s, 64 entities moving per tick). This is the per-tick
/// work `game/net/ServerNet.cs` does in its snapshot send loop (the writer + the per-client ring
/// dicts are reused there exactly as here — SnapshotDelta.cs:100-106 / ServerNet.cs:770).
///
/// Needs NO assets — runs everywhere (incl. CI), but is informational: it prints + records a
/// baseline rather than asserting timings (machine-dependent).
///
/// Run: dotnet test tests/XonoticGodot.Tests --filter NetSnapshotPerfBench -l "console;verbosity=detailed"
///
/// Measured baseline (2026-06-09, dev machine, Debug build):
///   full (no baseline):   5496 B wire for 256 entities
///   steady-state delta:   encode 0.115 ms/client-tick + decode 0.061 ms/client-tick
///                         (= 2.8 ms/server-tick for all 16 clients — encode is dominated by
///                         NetEntityState.Diff over EVERY entity per client per tick + the baseline
///                         dict copy, SnapshotDelta.cs:85-106)
///   wire:                 1059 B/snapshot avg (64 movers; ~1.2 MB/s/server at 72 Hz);
///                         1386 B/snapshot with an 8-tick ack lag
///   alloc:                272 B/client-tick (the boxed IReadOnlyDictionary enumerators in
///                         EncodeSnapshot's three foreaches — the ring dicts themselves are reused)
/// </summary>
[Collection("GlobalState")]
public class NetSnapshotPerfBench
{
    private const int Clients = 16;
    private const int Entities = 256;
    private const int Movers = 64;      // entities whose origin/velocity/frame change every tick
    private const int Ticks = 72 * 30;  // 30 sim-seconds at the 72 Hz server rate

    private readonly ITestOutputHelper _out;
    public NetSnapshotPerfBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Benchmark_SnapshotEncodeDecodeRoundTrip()
    {
        var sb = new StringBuilder();
        void Line(string s) { _out.WriteLine(s); sb.AppendLine(s); }

        // --- the world's networked entity set (players + projectiles + items), like ServerNet._entityScratch ---
        var world = new Dictionary<int, NetEntityState>(Entities);
        for (int i = 1; i <= Entities; i++)
        {
            world[i] = new NetEntityState
            {
                EntNum = i,
                Kind = i <= 16 ? NetEntityKind.Player : i <= Movers ? NetEntityKind.Projectile : NetEntityKind.Item,
                ModelIndex = i % 32,
                Frame = 0,
                Origin = new Vector3(i * 64f, (i % 7) * 128f, 64f),
                Angles = new Vector3(0, i * 13f % 360f, 0),
                Velocity = Vector3.Zero,
                Model = i <= 16 ? "models/player/erebus.iqm" : "",
                Weapon = -1,
            };
        }

        // --- per-client server + client histories (what ServerNet/ClientNet hold per connection) ---
        var servers = new ServerSnapshotHistory[Clients];
        var clients = new ClientSnapshotHistory[Clients];
        var writers = new BitWriter[Clients];
        for (int c = 0; c < Clients; c++)
        {
            servers[c] = new ServerSnapshotHistory();
            clients[c] = new ClientSnapshotHistory();
            writers[c] = new BitWriter(16 * 1024);
        }

        // --- probe 0: the full (no-baseline) snapshot — the worst case sent to a fresh/desynced client ---
        {
            var w = new BitWriter(64 * 1024);
            new ServerSnapshotHistory().EncodeSnapshot(w, world, snapshotSeq: 1);
            Line($"=== snapshot bench: {Clients} clients x {Entities} entities ({Movers} movers/tick) x {Ticks} ticks ===");
            Line($"[full] no-baseline snapshot wire size: {w.Length} B for {Entities} entities");
        }

        // --- warmup: fill every ring + JIT the encode/decode path (also establishes the ack chain) ---
        ushort seq = 0;
        for (int t = 0; t < ServerSnapshotHistory.Capacity + 8; t++)
        {
            seq++;
            if (seq == 0) seq = 1;
            Mutate(world, t);
            RoundTrip(world, servers, clients, writers, seq, out _);
        }

        // --- measured run: steady-state deltas under an ideal network (every snapshot acked next tick) ---
        long encodeTicks = 0, decodeTicks = 0, wireBytes = 0;
        int decodeFailures = 0;
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var swTotal = Stopwatch.StartNew();
        for (int t = 0; t < Ticks; t++)
        {
            seq++;
            if (seq == 0) seq = 1; // 0 is the "no baseline" sentinel — never use it as a real sequence
            Mutate(world, t);

            for (int c = 0; c < Clients; c++)
            {
                BitWriter w = writers[c];
                w.Reset();

                long t0 = Stopwatch.GetTimestamp();
                servers[c].EncodeSnapshot(w, world, seq, excludeEntNum: c + 1);
                long t1 = Stopwatch.GetTimestamp();

                var r = new BitReader(w.WrittenSpan);
                IReadOnlyDictionary<int, NetEntityState>? decoded = clients[c].DecodeSnapshot(ref r);
                long t2 = Stopwatch.GetTimestamp();

                encodeTicks += t1 - t0;
                decodeTicks += t2 - t1;
                wireBytes += w.Length;
                if (decoded is null) decodeFailures++;
                else servers[c].Ack(clients[c].LastDecodedSeq); // the client's ack closing the delta loop
            }
        }
        swTotal.Stop();
        long alloc1 = GC.GetAllocatedBytesForCurrentThread();

        double clientTicks = (double)Ticks * Clients;
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        Line($"[delta] {Ticks} ticks x {Clients} clients: {swTotal.Elapsed.TotalMilliseconds:F0} ms total " +
             $"({swTotal.Elapsed.TotalMilliseconds / Ticks:F3} ms/server-tick for all {Clients} clients)");
        Line($"  encode: {encodeTicks * tickToMs / clientTicks:F4} ms/client-tick   " +
             $"decode: {decodeTicks * tickToMs / clientTicks:F4} ms/client-tick");
        Line($"  wire:   {wireBytes / clientTicks:F0} B/snapshot avg (steady-state delta, {Movers} movers)   " +
             $"= {wireBytes / (double)Ticks / 1024.0 * 72.0:F1} KB/s/server at 72 Hz");
        Line($"  alloc:  {(alloc1 - alloc0) / clientTicks:F0} B/client-tick   " +
             $"({(alloc1 - alloc0) / 1024.0 / 1024.0:F1} MB over the run)");
        Line($"  decode failures: {decodeFailures} (expected 0)");
        Assert.Equal(0, decodeFailures); // correctness backstop — the round-trip itself must hold

        // --- probe: a lossy network (acks delayed 8 ticks) — deltas span deeper baselines, more bytes ---
        {
            var srv = new ServerSnapshotHistory();
            var cli = new ClientSnapshotHistory();
            var w = new BitWriter(64 * 1024);
            var ackQueue = new Queue<ushort>();
            long lossyBytes = 0;
            int lossyTicks = 72 * 5;
            for (int t = 0; t < lossyTicks; t++)
            {
                seq++;
                if (seq == 0) seq = 1;
                Mutate(world, t);
                w.Reset();
                srv.EncodeSnapshot(w, world, seq);
                lossyBytes += w.Length;
                var r = new BitReader(w.WrittenSpan);
                cli.DecodeSnapshot(ref r);
                ackQueue.Enqueue(cli.LastDecodedSeq);
                if (ackQueue.Count > 8) srv.Ack(ackQueue.Dequeue()); // 8-tick RTT
            }
            Line($"[delta, 8-tick ack lag] {lossyBytes / (double)lossyTicks:F0} B/snapshot avg");
        }

        Line("(numbers are informational — record significant regressions in the baseline comment atop this file)");
        _out.WriteLine(sb.ToString().Length > 0 ? "(report complete)" : "");
    }

    /// <summary>Advance the world one tick: the first <see cref="Movers"/> entities move, the rest idle.</summary>
    private static void Mutate(Dictionary<int, NetEntityState> world, int tick)
    {
        for (int i = 1; i <= Movers; i++)
        {
            NetEntityState s = world[i];
            // multiples of 1/8 so the 13.3 fixed-point coord quantization round-trips losslessly
            s.Origin += new Vector3(2.125f, (i % 3) - 1f, 0f);
            s.Velocity = new Vector3(153f, ((i + tick) % 5) * 16f, 0f);
            if ((tick & 3) == 0) s.Frame = (s.Frame + 1) & 0xFFFF;
            if (((tick + i) & 15) == 0) s.Angles = new Vector3(0, (s.Angles.Y + 45f) % 360f, 0);
            world[i] = s;
        }
    }

    private static void RoundTrip(Dictionary<int, NetEntityState> world, ServerSnapshotHistory[] servers,
        ClientSnapshotHistory[] clients, BitWriter[] writers, ushort seq, out long bytes)
    {
        bytes = 0;
        for (int c = 0; c < servers.Length; c++)
        {
            BitWriter w = writers[c];
            w.Reset();
            servers[c].EncodeSnapshot(w, world, seq, excludeEntNum: c + 1);
            bytes += w.Length;
            var r = new BitReader(w.WrittenSpan);
            if (clients[c].DecodeSnapshot(ref r) is not null)
                servers[c].Ack(clients[c].LastDecodedSeq);
        }
    }
}
