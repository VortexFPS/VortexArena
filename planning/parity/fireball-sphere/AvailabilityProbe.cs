using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Game.Net;
using VortexArena.Net;
using VortexArena.Server;
using Vector3 = System.Numerics.Vector3;

internal static class FireballAvailabilityProbe
{
    private static readonly FireballBlastEffect First = new(new Vector3(5250.25f, 16f, -32f), 96f);
    private static readonly FireballBlastEffect Last = new(new Vector3(11f, 12f, 13f), 180f);

    public static int Run(int port)
    {
        try
        {
            CheckWindow(port);
            CheckNewMap(port);
            GD.Print("FIREBALL_AVAILABILITY PASS");
            return 0;
        }
        catch (Exception error)
        {
            GD.PrintErr(error);
            return 1;
        }
    }

    private static GameWorld BootWorld(string map)
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = map };
        world.Boot("dm", applyStartupOverrides: cvars =>
        {
            cvars.Set("sv_public", "-2");
            cvars.Set("bot_number", "0");
            cvars.Set("g_warmup", "0");
            cvars.Set("slowmo", "1");
            cvars.Set("g_playerstats_gamereport_uri", "");
            cvars.Set("g_playerstats_playerbasic_uri", "");
            cvars.Set("g_playerstats_playerdetail_uri", "");
        });
        return world;
    }

    private static void CheckWindow(int port)
    {
        GameWorld world = BootWorld("fireball-window");
        try
        {
            using var server = ServerNet.Start(world, port) ?? throw new InvalidOperationException("server bind failed");
            using var existing = new Peer(port, "existing");
            using var during = new Peer(port, "during");
            using var boundary = new Peer(port, "boundary");
            using var after = new Peer(port, "after");
            Peer[] peers = { existing, during, boundary, after };
            Wait(server, peers, () => Array.TrueForAll(peers, peer => peer.Transport.IsConnected));
            foreach (Peer peer in peers) peer.RequestChallenge();
            Wait(server, peers, () => Array.TrueForAll(peers, peer => peer.ChallengeReceived));
            existing.Authenticate();
            Wait(server, peers, () => existing.Accepted);
            Check(world.Time == 0f, "admission pump must leave simulation at time zero");

            float bornAt = EmitOnNextTick(world, server, First, 0.025f);
            Wait(server, peers, () => existing.Blasts.Count == 1);
            Check(existing.Blasts[0] == First, "existing peer received wrong payload");
            Drain(server, peers);
            Check(during.Blasts.Count + boundary.Blasts.Count + after.Blasts.Count == 0,
                "an ENet-connected peer received the blast before authentication");
            GD.Print("existing at spawn: 1; all three challenged, unauthenticated peers: 0");

            Tick(world, server, 0.025f);
            Tick(world, server, 0.025f);
            Tick(world, server, 0.023f);
            Check(Math.Abs(world.Time - bornAt - 0.098f) < 1e-6f, "inside trial clock is not 98 ms");
            Thread.Sleep(125); // This clock must ignore wall time before the delayed authentication.
            during.Authenticate();
            Wait(server, peers, () => during.Accepted && during.Blasts.Count == 1);
            Check(during.Blasts[0] == First, "inside peer received wrong payload");
            during.Authenticate(); // The already accepted connection must not replay a second copy.
            Drain(server, peers);
            Check(during.Blasts.Count == 1, "duplicate authentication repeated the event");
            GD.Print("accepted at 98 ms of simulation after a 125 ms wall pause: 1; repeated auth: still 1");

            Tick(world, server, 0.002f);
            Check(world.Time == bornAt + 0.1f, $"boundary trial clock is not exact: {world.Time:R}");
            boundary.Authenticate();
            Wait(server, peers, () => boundary.Accepted);
            Drain(server, peers);
            Check(boundary.Blasts.Count == 0, "effect was still available at the exact expiry");
            GD.Print("accepted at exactly 100 ms of simulation: 0");

            Tick(world, server, 0.025f);
            after.Authenticate();
            Wait(server, peers, () => after.Accepted);
            Drain(server, peers);
            Check(after.Blasts.Count == 0, "after-expiry peer received the old blast");
            Check(existing.Blasts.Count == 1 && during.Blasts.Count == 1, "later flush repeated a delivery");
            GD.Print("accepted at 125 ms of simulation: 0; later flush repeated no deliveries");

            CheckCatchUpFlush(world, server, peers);
            EmitOnNextTick(world, server, Last, 0.025f);
            Wait(server, peers, () => Array.TrueForAll(peers, peer => peer.Blasts.Contains(Last)));
            GD.Print("new blast still live at map teardown: one copy on each of four accepted peers");
        }
        finally
        {
            world.Shutdown();
        }
    }

    private static void CheckCatchUpFlush(GameWorld world, ServerNet server, Peer[] peers)
    {
        var early = new FireballBlastEffect(Vector3.UnitX, 40f);
        var late = new FireballBlastEffect(Vector3.UnitY, 60f);
        int ticks = 0;
        void EmitOnTick()
        {
            ticks++;
            if (ticks == 1) EffectEmitter.Sink.Emit(early);
            if (ticks == 6) EffectEmitter.Sink.Emit(late);
        }

        // Six binary-exact 1/32 s physics ticks before a single network flush.
        world.Services.Cvars.Set("sv_overload_timedrop", "0");
        world.Services.Cvars.Set("sys_ticrate", "0.03125");
        world.Simulation.StartFrame += EmitOnTick;
        try { server.Tick(6f / 32f); }
        finally { world.Simulation.StartFrame -= EmitOnTick; }
        Check(ticks == 6, $"expected six ticks before flushing, got {ticks}");
        Wait(server, peers, () => Array.TrueForAll(peers, peer => peer.Blasts.Contains(late)));
        Drain(server, peers);
        foreach (Peer peer in peers)
        {
            Check(!peer.Blasts.Contains(early), "first-flush delivery included an expired effect");
            Check(peer.Blasts.FindAll(blast => blast == late).Count == 1, "live catch-up effect count");
        }
        GD.Print("six physics ticks before first flush: expired blast 0; fresh blast 1 on each accepted peer");
    }

    private static void CheckNewMap(int port)
    {
        GameWorld world = BootWorld("fireball-next-map");
        try
        {
            using var server = ServerNet.Start(world, port) ?? throw new InvalidOperationException("next map bind failed");
            using var peer = new Peer(port, "new-map");
            Peer[] peers = { peer };
            Wait(server, peers, () => peer.Transport.IsConnected);
            peer.RequestChallenge();
            Wait(server, peers, () => peer.ChallengeReceived);
            peer.Authenticate();
            Wait(server, peers, () => peer.Accepted);
            Tick(world, server, 0.025f);
            Drain(server, peers);
            Check(peer.Blasts.Count == 0, "blast crossed map teardown");
            GD.Print("accepted on replacement map on the same UDP port: 0 previous-map blasts");
        }
        finally
        {
            world.Shutdown();
        }
    }

    private static float EmitOnNextTick(GameWorld world, ServerNet server, FireballBlastEffect blast, float delta)
    {
        float bornAt = float.NaN;
        void Emit()
        {
            bornAt = Api.Clock.Time;
            EffectEmitter.Sink.Emit(blast);
        }

        world.Simulation.StartFrame += Emit;
        try { Tick(world, server, delta); }
        finally { world.Simulation.StartFrame -= Emit; }
        Check(float.IsFinite(bornAt), "the effect sink was not driven by a simulation tick");
        return bornAt;
    }

    private static void Tick(GameWorld world, ServerNet server, float delta)
    {
        world.Services.Cvars.Set("sys_ticrate", delta.ToString("R", CultureInfo.InvariantCulture));
        server.Tick(delta);
    }

    private static void Wait(ServerNet server, Peer[] peers, Func<bool> done)
    {
        for (int i = 0; i < 1500; i++)
        {
            Pump(server, peers);
            if (done()) return;
        }
        throw new TimeoutException("ENet state was not reached");
    }

    private static void Drain(ServerNet server, Peer[] peers)
    {
        for (int i = 0; i < 60; i++)
            Pump(server, peers);
    }

    private static void Pump(ServerNet server, Peer[] peers)
    {
        server.Tick(0f);
        foreach (Peer peer in peers) peer.Transport.Poll();
        Thread.Sleep(1);
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException(reason);
    }

    private sealed class Peer : IDisposable
    {
        private readonly string _name;
        private readonly PlayerIdentity _identity = PlayerIdentity.Generate();
        private byte[]? _challenge;
        public readonly NetTransport.Client Transport;
        public readonly List<FireballBlastEffect> Blasts = new();
        public bool Accepted { get; private set; }
        public bool ChallengeReceived => _challenge is not null;

        public Peer(int port, string name)
        {
            _name = name;
            Transport = NetTransport.Client.Connect("127.0.0.1", port)
                ?? throw new InvalidOperationException("peer connect failed");
            Transport.PacketReceived += Receive;
        }

        private void Receive(int from, int channel, byte[] packet)
        {
            var reader = new BitReader(packet);
            switch ((NetControl)reader.ReadByte())
            {
                case NetControl.HandshakeChallenge:
                    _challenge = reader.ReadBytes(reader.ReadUShort()).ToArray();
                    break;
                case NetControl.HandshakeAccept:
                    Accepted = true;
                    break;
                case NetControl.FireballBlast:
                    Check(Accepted, $"{_name} received a blast before its accepted packet");
                    Check(channel == NetProtocol.ReliableChannel, "fireball replay left the reliable channel");
                    Blasts.Add(FireballBlastWire.Read(ref reader) ?? throw new InvalidOperationException("invalid blast packet"));
                    break;
            }
        }

        public void RequestChallenge()
        {
            var writer = new BitWriter();
            writer.WriteByte((byte)NetControl.HandshakeRequest);
            writer.WriteULong(NetProtocol.BuildParity());
            writer.WriteString(_name);
            writer.WriteUShort(_identity.PublicKey.Length);
            writer.WriteBytes(_identity.PublicKey);
            Send(writer);
        }

        public void Authenticate()
        {
            byte[] signature = _identity.Sign(_challenge ?? throw new InvalidOperationException("missing challenge"));
            var writer = new BitWriter();
            writer.WriteByte((byte)NetControl.HandshakeAuth);
            writer.WriteUShort(signature.Length);
            writer.WriteBytes(signature);
            Send(writer);
        }

        private void Send(BitWriter writer)
        {
            Transport.SendToServer(writer.WrittenSpan, reliable: true);
            Transport.Flush();
        }

        public void Dispose()
        {
            Transport.Dispose();
            _identity.Dispose();
        }
    }
}
