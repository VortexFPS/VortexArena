using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// A real server (Xonotic's progs.dat from the reference checkout) and any number of headless legacy
/// clients in one process on a simulated clock, with what each side printed kept for the test to
/// read. Every user needs <c>../Base</c>; check <see cref="HaveData"/> first.
/// </summary>
internal sealed class ServerTestRig : IDisposable
{
    public const double StepSeconds = 1.0 / 128;

    public static bool HaveData => Directory.Exists(TestPaths.BaseCorePk3Dir);

    public readonly SvEnvironment Env;
    public readonly SvServer Server;
    public SvLoopback? Loop;
    public readonly List<string> ServerPrints = new(), ServerEvents = new();
    public readonly List<List<string>> ClientPrints = new();
    public readonly Dictionary<SvLoopbackClient, string> Names = new();
    private readonly StringBuilder _line = new(), _engineLine = new();
    private double _serverOnlyClock;
    private readonly List<(System.Net.IPEndPoint To, byte[] Datagram)> _out = new();

    /// <param name="cvars">Set after the default configuration has run. g_warmup 0, g_start_delay 0
    /// and sv_public 0 are set first: a test wants a match that starts at once on a private server.</param>
    public ServerTestRig(int maxClients, params (string Name, string Value)[] cvars)
    {
        Env = new SvEnvironment(TestPaths.BaseData, writeRoot: null, print: text => Lines(text, _line, ServerPrints));
        Env.SetCvar("sv_public", "0");
        Env.SetCvar("g_warmup", "0");
        Env.SetCvar("g_start_delay", "0");
        foreach ((string name, string value) in cvars) Env.SetCvar(name, value);
        Server = new SvServer(Env, new SvServerOptions { MaxClients = maxClients, RandomSeed = 1, Print = text => Lines(text, _engineLine, ServerPrints) });
        Server.Event += ServerEvents.Add;
    }

    private static void Lines(string text, StringBuilder line, List<string> into)
    {
        foreach (char c in text)
        {
            if (c != '\n') { if (line.Length < 600) line.Append(c); continue; }
            if (into.Count < 50000) into.Add(line.ToString());
            line.Clear();
        }
    }

    public SvqcHost Host => Server.Host!;
    public double Now => Loop?.Now ?? _serverOnlyClock;

    /// <summary>A new client, already connecting.</summary>
    public SvLoopbackClient AddClient(string name)
    {
        LegacyClientOptions options = new() { Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
        options.Client.Signon.Name = name;
        List<string> prints = new();
        StringBuilder line = new();
        ClientPrints.Add(prints);
        SvLoopbackClient c;
        if (Loop is null)
        {
            Loop = new SvLoopback(Server, TestPaths.BaseData, clientWriteRoot: null, options, clientPrint: text => Lines(text, line, prints));
            // Drawing is most of what a headless client costs; a test that is not about what is drawn
            // has each client draw a few times a second, enough to run its program's frame logic.
            Loop.DrawRate = 8;
            c = Loop.Clients[0];
        }
        else c = Loop.AddClient(options, print: text => Lines(text, line, prints));
        Names[c] = name;
        Loop.Connect(c);
        return c;
    }

    public void Step()
    {
        if (Loop is not null) Loop.Step(StepSeconds);
        else
        {
            // No client: the server alone, one tick of its own length at a time.
            _serverOnlyClock += 1.0 / 64;
            _out.Clear();
            Server.Frame(_serverOnlyClock, _out);
        }
    }

    /// <summary>Steps until the condition holds or the time is up. True if it held.</summary>
    public bool Until(Func<bool> condition, double seconds)
    {
        double end = Now + seconds;
        while (Now < end)
        {
            if (condition()) return true;
            Step();
        }
        return condition();
    }

    public void Run(double seconds) => Until(() => false, seconds);

    public int Count(List<string> lines, string text, int from = 0)
    {
        int n = 0;
        for (int i = from; i < lines.Count; i++) if (lines[i].Contains(text, StringComparison.Ordinal)) n++;
        return n;
    }

    public static string Summary(SvLoopbackClient c)
    {
        LegacyClientSession s = c.Session;
        return $"{s.Client.State}, signon {s.Client.Signon.Stage}, program faults {s.Host?.FaultCount ?? 0}, desyncs {s.Host?.DesyncCount ?? 0}, undecoded {s.MessagesNotDecoded}" +
            (s.FirstUndecoded is null ? "" : " (" + s.FirstUndecoded + ")") + (s.Client.LastError is null ? "" : $", last error \"{s.Client.LastError}\"")
            + (s.Host is { FaultCount: > 0 } h ? " first fault: " + h.Faults[0].Message : "");
    }

    /// <summary>The client decoded every message and its program has neither faulted nor lost its place in one.</summary>
    public static bool Clean(SvLoopbackClient c) =>
        c.Session.MessagesNotDecoded == 0 && c.Session.ProgramError is null && (c.Session.Host?.FaultCount ?? 0) == 0 && (c.Session.Host?.DesyncCount ?? 0) == 0;

    public string ServerFaults => Server.TotalFaults == 0 ? "0 faults" : $"{Server.TotalFaults} faults" + (Server.Host is { Faults.Count: > 0 } h ? ": " + h.Faults[0].EntryPoint + ": " + h.Faults[0].Message : "");

    public void Dispose()
    {
        Loop?.Dispose();
        Server.Dispose();
        Env.Dispose();
    }
}
