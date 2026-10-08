// legacy-server serve: the server on a UDP port in real time, with the dedicated console on stdin.
// DarkPlaces' counterpart is host.c Host_Main for a dedicated server: read the console, read the
// network, run the frame, send, sleep until the next tick is due.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using VortexArena.Legacy.Server;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private static int ServeImpl(Options o)
    {
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        StringBuilder printLine = new();
        using SvEnvironment? env = Environment(o, warnings, printLine, line => Log(Printable(line, 500)));
        if (env is null) return 1;
        if (!IPAddress.TryParse(o.Bind, out IPAddress? bind))
        {
            Log($"RESULT: FAILED (\"{o.Bind}\" is not an address)");
            return 1;
        }

        using SvServer server = new(env, new SvServerOptions { MaxClients = o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers, KeepRunningAfterFault = o.KeepRunning, RandomSeed = o.Seed, Print = text => Log(Printable(text.TrimEnd('\n'), 500)) });
        server.Event += text => Log("* " + Printable(text, 400));
        int commands = 0;
        server.ClientCommandReceived += (c, text) => { if (!text.StartsWith("sentcvar", StringComparison.Ordinal) && commands++ < 400) Log($"cmd< #{c.Edict} {Printable(text, 200)}"); };
        Stopwatch clock = Stopwatch.StartNew();
        if (!server.Start(o.Map, clock.Elapsed.TotalSeconds) || server.Host is null)
        {
            Log("RESULT: FAILED (the level could not be started)");
            return 1;
        }

        SvUdpTransport transport;
        try { transport = new SvUdpTransport(new IPEndPoint(bind, o.Port)); }
        catch (System.Net.Sockets.SocketException e)
        {
            Log($"RESULT: FAILED (cannot listen on {bind}:{o.Port}: {e.Message})");
            return 1;
        }
        using SvUdpTransport _ = transport;
        Log($"Server listening on address {transport.LocalEndPoint} (map {o.Map}, {(o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers)} slots); type console commands, \"quit\" to stop");

        // The console: lines typed on stdin, handed to the frame loop. A closed stdin just ends the reader.
        ConcurrentQueue<string> typed = new();
        Thread reader = new(() =>
        {
            try
            {
                while (Console.ReadLine() is { } line) typed.Enqueue(line);
            }
            catch (IOException) { }
        })
        { IsBackground = true, Name = "console" };
        reader.Start();

        List<(IPEndPoint To, byte[] Datagram)> outgoing = new();
        double nextStatus = 10, end = o.HasSeconds ? o.Seconds : double.PositiveInfinity;
        long frames = 0;
        while (!server.QuitRequested && clock.Elapsed.TotalSeconds < end)
        {
            double now = clock.Elapsed.TotalSeconds;
            while (typed.TryDequeue(out string? line))
            {
                // "prvm_edictset server N origin "x y z"" as DarkPlaces' console has it, for putting a
                // client where a comparison wants it; anything else is the server's console.
                string[] words = line.Split(new[] { ' ', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 7 && words[0] == "prvm_edictset" && words[1] == "server" && words[3] == "origin" && server.Host is { } level
                    && int.TryParse(words[2], out int edict) && level.IsLive(edict)
                    && float.TryParse(words[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(words[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    && float.TryParse(words[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    level.Vm.FieldVector(edict, level.F.Origin) = new VortexArena.QuakeC.QcVector(x, y, z);
                    level.Vm.FieldVector(edict, level.F.Velocity) = default;
                    level.LinkEdict(edict);
                    Log($"edict {edict} moved to '{x} {y} {z}'");
                }
                else server.Host?.AddCommandText(line + "\n");
            }
            outgoing.Clear();
            while (transport.TryReceive(out byte[] datagram, out IPEndPoint from)) server.Receive(datagram, from, now, outgoing);
            frames += server.Frame(now, outgoing);
            foreach ((IPEndPoint to, byte[] datagram) in outgoing) transport.Send(datagram, to);

            if (now >= nextStatus && server.Host is { } host)
            {
                nextStatus += 10;
                Log(string.Create(CultureInfo.InvariantCulture, $"[{now,7:0.0}] time {host.Time:0.0} frames {frames} clients {host.ActiveClients} faults {host.FaultCount} datagrams in {server.DatagramsReceived} out {server.DatagramsSent} ({server.BytesSent} bytes)"));
            }
            // A dedicated server sleeps until its next tick. Thread.Sleep(1) is a whole 15.6 ms timer
            // tick on Windows - as long as a server frame - so the wait is a yield loop instead: it
            // keeps one core busy, and keeps the 64 frames a second evenly spaced.
            double wake = now + 0.001;
            while (clock.Elapsed.TotalSeconds < wake) Thread.Yield();
        }

        if (server.Host is { } final)
        {
            Log($"level at exit: time {final.Time:0.0}, {final.Frames} frames, {final.Vm.NumEdicts} edicts, {final.FaultCount} faults");
            Report(final, warnings);
        }
        outgoing.Clear();
        server.Shutdown(outgoing);
        foreach ((IPEndPoint to, byte[] datagram) in outgoing) transport.Send(datagram, to);
        Log($"server stopped after {clock.Elapsed.TotalSeconds:0.0} s: {frames} frames, datagrams in {server.DatagramsReceived} out {server.DatagramsSent}, connectionless {server.ConnectionlessPackets}, ignored {server.DatagramsIgnored}, " +
            $"string commands {server.StringCommandsReceived}, moves {server.MovesReceived}, entity frames sent {server.EntityFramesSent}");
        return 0;
    }
}
