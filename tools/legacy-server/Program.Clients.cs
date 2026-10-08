// legacy-server clients: several headless legacy clients on one server in one process, put through
// what players do to a server - arrive together, see each other, talk, vote, spectate and rejoin,
// change team, leave and come back, go silent, get kicked, find the server full, send garbage - with
// the server's and each client's account of every step written down, and a per-frame comparison of
// where a moving client believes it is with where the server has it. Each step says PASS or FAIL;
// the same assertions are in tests/VortexArena.Tests/Legacy/ServerClientsTests.cs.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    /// <summary>A server and its in-process clients, with what each side printed kept for inspection.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly SvEnvironment Env;
        public readonly SvServer Server;
        public SvLoopback? Loop;
        public readonly List<string> ServerPrints = new(), ServerEvents = new();
        public readonly List<List<string>> ClientPrints = new();
        public readonly Dictionary<string, int> ServerWarnings = new(StringComparer.Ordinal);
        public const double StepSeconds = 1.0 / 128;
        private readonly StringBuilder _line = new(), _engineLine = new();
        private readonly string _data;
        private readonly string _clientRoot = Path.Combine(Path.GetTempPath(), "legacy-clients-" + Guid.NewGuid().ToString("N"));
        public int Failures;
        public readonly Dictionary<SvLoopbackClient, string> Names = new();

        public Rig(Options o, int maxClients, params (string Name, string Value)[] cvars)
        {
            _data = o.Data;
            Env = new SvEnvironment(o.Data, null, print: text => Lines(text, _line, ServerPrints),
                warning: text => { text = text.TrimEnd(); ServerWarnings[text] = ServerWarnings.GetValueOrDefault(text) + 1; });
            Env.SetCvar("sv_public", "0");
            Env.SetCvar("g_warmup", "0");
            Env.SetCvar("g_start_delay", "0");
            foreach ((string name, string value) in cvars) Env.SetCvar(name, value);
            foreach ((string name, string value) in o.Sets) Env.SetCvar(name, value);
            Server = new SvServer(Env, new SvServerOptions { MaxClients = maxClients, RandomSeed = o.Seed ?? 1, KeepRunningAfterFault = o.KeepRunning, Print = text => Lines(text, _engineLine, ServerPrints) });
            Server.Event += ServerEvents.Add;
        }

        private static void Lines(string text, StringBuilder line, List<string> into)
        {
            foreach (char c in text)
            {
                if (c != '\n') { if (line.Length < 600) line.Append(c); continue; }
                if (into.Count < 20000) into.Add(line.ToString());
                line.Clear();
            }
        }

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
                Loop = new SvLoopback(Server, _data, _clientRoot, options, clientPrint: text => Lines(text, line, prints));
                c = Loop.Clients[0];
            }
            else c = Loop.AddClient(options, print: text => Lines(text, line, prints));
            Names[c] = name;
            Loop.Connect(c);
            return c;
        }

        public double Now => Loop?.Now ?? 0;

        /// <summary>Steps until the condition holds or the time is up. True if it held.</summary>
        public bool Until(Func<bool> condition, double seconds)
        {
            double end = Now + seconds;
            while (Now < end)
            {
                if (condition()) return true;
                Loop!.Step(StepSeconds);
            }
            return condition();
        }

        public void Run(double seconds) => Until(() => false, seconds);

        public bool Check(string what, bool ok, string detail = "")
        {
            if (!ok) Failures++;
            Log($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " - " + detail : "")}");
            return ok;
        }

        public int PrintsContaining(List<string> lines, string text, int from = 0)
        {
            int n = 0;
            for (int i = from; i < lines.Count; i++) if (lines[i].Contains(text, StringComparison.Ordinal)) n++;
            return n;
        }

        public void Dispose()
        {
            Loop?.Dispose();
            Server.Dispose();
            Env.Dispose();
            try { if (Directory.Exists(_clientRoot)) Directory.Delete(_clientRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static string ClientSummary(SvLoopbackClient c)
    {
        LegacyClientSession s = c.Session;
        return $"{s.Client.State}, signon {s.Client.Signon.Stage}, program faults {s.Host?.FaultCount ?? 0}, desyncs {s.Host?.DesyncCount ?? 0}, undecoded {s.MessagesNotDecoded}" +
            (s.FirstUndecoded is null ? "" : " (" + Printable(s.FirstUndecoded, 300) + ")") + (s.Client.LastError is null ? "" : $", last error \"{Printable(s.Client.LastError, 120)}\"");
    }

    private static bool ClientClean(SvLoopbackClient c) =>
        c.Session.MessagesNotDecoded == 0 && c.Session.ProgramError is null && (c.Session.Host?.FaultCount ?? 0) == 0 && (c.Session.Host?.DesyncCount ?? 0) == 0;

    private static int Clients(Options o)
    {
        int n = Math.Clamp(o.ClientCount > 0 ? o.ClientCount : 4, 2, 8);
        Log($"legacy-server clients: map {o.Map}, {n} in-process clients");
        int failures = 0;
        failures += ClientsTogether(o, n);
        failures += ClientsComeAndGo(o);
        failures += ClientsTeams(o);
        failures += ClientsMovement(o);
        Log(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures} checks failed)");
        return failures == 0 ? 0 : 1;
    }

    // ---- several clients in one game: visibility, chat, votes, spectating, status, kick -----------------

    private static int ClientsTogether(Options o, int n)
    {
        Log("");
        Log($"== {n} clients together ==");
        using Rig rig = new(o, 16, ("bot_number", "1"), ("sv_vote_call", "1"), ("g_maplist_votable", "0"));
        if (!rig.Server.Start(o.Map)) { Log("FAIL  the level could not be started"); return 1; }
        List<SvLoopbackClient> clients = new();
        for (int i = 0; i < n; i++) clients.Add(rig.AddClient("player" + (i + 1)));
        rig.Check($"all {n} clients reach the game", rig.Until(() => clients.All(c => c.InGame), 60), string.Join("; ", clients.Select(ClientSummary)));
        foreach (SvLoopbackClient c in clients) c.Session.Client.SendStringCommand("join");
        rig.Check("all become players (frags not -666)", rig.Until(() => clients.All(c => c.ServerSlot is { Begun: true, Frags: not -666 }), 10),
            string.Join(" ", clients.Select(c => c.ServerSlot?.Frags.ToString(CultureInfo.InvariantCulture) ?? "-")));

        rig.Run(1.0);
        // Spawn points are rooms apart, and a player in another room is rightly culled. Bring the
        // others to where the first one stands - each to a spot the server's own line trace says is
        // open from there - so that "sees the other players" has an answer that is not "culled".
        SvqcHost level = rig.Server.Host!;
        QcVector home = level.Vm.FieldVector(clients[0].ServerSlot!.Edict, level.F.Origin);
        (float X, float Y)[] around = { (72, 0), (-72, 0), (0, 72), (0, -72), (72, 72), (-72, -72), (72, -72), (-72, 72) };
        int placed = 0;
        for (int i = 1; i < n; i++)
        {
            foreach ((float dx, float dy) in around.Skip(placed))
            {
                placed++;
                QcVector spot = new(home.X + dx, home.Y + dy, home.Z + 8);
                if (level.World.Trace(home, default, default, spot, SvWorld.MoveWorldOnly, 0, SvWorld.ContentsOpaque).Fraction < 1) continue;
                int e = clients[i].ServerSlot!.Edict;
                level.Vm.FieldVector(e, level.F.Origin) = spot;
                level.Vm.FieldVector(e, level.F.Velocity) = default;
                level.LinkEdict(e);
                break;
            }
        }
        rig.Run(1.5);

        // Visibility: each client's CSQC entity stream carries the other players. A player the
        // server culls (not in the client's PVS) has no CSQC entity, which is correct and is counted
        // separately from one that is in the PVS and missing.
        SvqcHost host = rig.Server.Host!;
        int seen = 0, culled = 0, missing = 0;
        for (int i = 0; i < n; i++)
        {
            CsqcHost? program = clients[i].Session.Host;
            List<int> visible = new();
            rig.Server.VisibleEntities(clients[i].ServerSlot!, visible);
            StringBuilder row = new($"  player{i + 1} (entity {clients[i].ServerSlot?.Edict} at {V(host.Vm.FieldVector(clients[i].ServerSlot!.Edict, host.F.Origin))}, {visible.Count} entities sent, csqc edicts {program?.Vm.NumEdicts}) sees:");
            for (int j = 0; j < n; j++)
            {
                if (i == j || clients[j].ServerSlot is not { } other || program is null) continue;
                int edict = program.EdictForServerEntity(other.Edict);
                bool has = edict > 0 && edict < program.Vm.NumEdicts && !program.Vm.IsFree(edict);
                QcVector there = host.Vm.FieldVector(other.Edict, host.F.Origin);
                if (has)
                {
                    QcVector mine = program.Vm.FieldVector(edict, program.Fields.Origin);
                    double d = Math.Sqrt(Math.Pow(mine.X - there.X, 2) + Math.Pow(mine.Y - there.Y, 2) + Math.Pow(mine.Z - there.Z, 2));
                    row.Append(CultureInfo.InvariantCulture, $" player{j + 1}@{d:0.0}u");
                    seen++;
                }
                else if (!rig.Server.WouldSendEntity(clients[i].ServerSlot!, other.Edict))
                {
                    row.Append(CultureInfo.InvariantCulture, $" player{j + 1}(culled)");
                    culled++;
                }
                else
                {
                    row.Append(CultureInfo.InvariantCulture, $" player{j + 1}(MISSING)");
                    missing++;
                }
            }
            Log(row.ToString());
        }
        rig.Check("every client has a CSQC entity for every other player the server sends it", missing == 0 && seen > 0, $"{seen} seen, {culled} culled by visibility, {missing} missing");

        // Scoreboard: every client knows every other client's name (svc_updatename).
        bool names = clients.All(c => clients.All(other => other.ServerSlot is { } slot && c.Session.State.Scores[slot.Index].Name.Contains(rig.Names[other], StringComparison.Ordinal)));
        rig.Check("every client's scoreboard names every other client", names);

        // Chat.
        int[] marks = rig.ClientPrints.Select(p => p.Count).ToArray();
        clients[0].Session.Client.SendStringCommand("say hello from one");
        rig.Run(1);
        int heard = 0;
        for (int i = 0; i < n; i++) if (rig.PrintsContaining(rig.ClientPrints[i], "hello from one", marks[i]) > 0) heard++;
        rig.Check("say reaches every client", heard == n, $"{heard} of {n} heard it");

        // Spectate and rejoin.
        clients[1].Session.Client.SendStringCommand("spectate");
        rig.Check("cmd spectate makes player2 an observer", rig.Until(() => clients[1].ServerSlot is { Frags: -666 }, 5), $"frags {clients[1].ServerSlot?.Frags}");
        clients[1].Session.Client.SendStringCommand("join");
        rig.Check("cmd join makes player2 a player again", rig.Until(() => clients[1].ServerSlot is { Frags: not -666 }, 8), $"frags {clients[1].ServerSlot?.Frags}");

        // A vote: called by one, accepted by the rest.
        int serverMark = rig.ServerPrints.Count;
        clients[0].Session.Client.SendStringCommand("vote call extendmatchtime");
        rig.Run(1);
        for (int i = 1; i < n; i++) clients[i].Session.Client.SendStringCommand("vote yes");
        rig.Run(2);
        foreach (string line in rig.ClientPrints[0].Skip(marks[0]).Take(12)) Log("  player1 heard: " + Printable(line, 200));
        foreach (string line in rig.ServerPrints.Skip(serverMark).Take(12)) Log("  server printed: " + Printable(line, 200));
        List<string> voteLines = rig.ServerPrints.Skip(serverMark).Where(l => l.Contains("vote", StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
        foreach (string line in voteLines) Log("  server: " + Printable(line, 200));
        rig.Check("a called vote is accepted when the others vote yes", voteLines.Any(l => l.Contains("accepted", StringComparison.OrdinalIgnoreCase)), $"{voteLines.Count} vote lines");

        // status, from the server console and from a client.
        serverMark = rig.ServerPrints.Count;
        marks = rig.ClientPrints.Select(p => p.Count).ToArray();
        rig.Server.AddCommandText("status\n");
        clients[2 % n].Session.Client.SendStringCommand("status");
        rig.Run(0.5);
        int statusNames = clients.Count(c => rig.PrintsContaining(rig.ServerPrints, rig.Names[c], serverMark) > 0);
        rig.Check("status on the server console lists every client", statusNames == n, $"{statusNames} of {n} names");
        foreach (string line in rig.ServerPrints.Skip(serverMark).Take(n + 8)) Log("  status: " + Printable(line, 200));
        rig.Check("status asked by a client is answered to it", rig.PrintsContaining(rig.ClientPrints[2 % n], "players:", marks[2 % n]) > 0);

        // kick: by slot number, with a reason.
        SvLoopbackClient kicked = clients[n - 1];
        int kickedSlot = kicked.ServerSlot!.Edict;
        int disconnectsBefore = rig.PrintsContaining(rig.ServerPrints, "disconnected");
        rig.Server.AddCommandText($"kick # {kickedSlot} testing the kick\n");
        bool gone = rig.Until(() => kicked.Session.Client.State != DpClientState.Connected, 3);
        rig.Check($"kick # {kickedSlot} disconnects that client and frees its slot", gone && !rig.Server.Clients[kickedSlot - 1].Active,
            $"client {kicked.Session.Client.State} (\"{Printable(kicked.Session.Client.LastError ?? "", 100)}\"), slot active {rig.Server.Clients[kickedSlot - 1].Active}");
        rig.Check("the program's ClientDisconnect ran once for it", rig.PrintsContaining(rig.ServerPrints, "disconnected") == disconnectsBefore + 1);
        rig.Run(1);
        for (int i = 0; i < n - 1; i++) rig.Check($"player{i + 1} is still clean", clients[i].InGame && ClientClean(clients[i]), ClientSummary(clients[i]));
        rig.Check("the server program did not fault", rig.Server.TotalFaults == 0, $"{rig.Server.TotalFaults} faults" + (rig.Server.Host!.Faults.Count > 0 ? ": " + Printable(rig.Server.Host.Faults[0].Message, 600) : ""));
        return rig.Failures;
    }

    // ---- leaving, returning, timing out, a full server, garbage -----------------------------------------

    private static int ClientsComeAndGo(Options o)
    {
        Log("");
        Log("== clients coming and going (3 slots) ==");
        using Rig rig = new(o, 3, ("bot_number", "0"), ("net_messagetimeout", "4"), ("net_connecttimeout", "4"));
        if (!rig.Server.Start(o.Map)) { Log("FAIL  the level could not be started"); return 1; }
        SvLoopbackClient a = rig.AddClient("alice"), b = rig.AddClient("bob"), c = rig.AddClient("carol");
        rig.Check("three clients fill the three slots", rig.Until(() => a.InGame && b.InGame && c.InGame, 60), $"{ClientSummary(a)}; {ClientSummary(b)}; {ClientSummary(c)}");

        // A full server rejects the fourth.
        SvLoopbackClient d = rig.AddClient("dave");
        rig.Check("a fourth client is rejected: the server is full", rig.Until(() => d.Session.Client.State == DpClientState.Rejected, 15),
            $"{d.Session.Client.State}: \"{Printable(d.Session.Client.LastError ?? "", 100)}\"");

        // A clean disconnect frees the slot, ClientDisconnect runs once, and the slot is reusable.
        int disconnects = rig.PrintsContaining(rig.ServerPrints, "disconnected");
        int slotOfB = b.ServerSlot!.Index;
        rig.Loop!.Disconnect(b);
        rig.Check("disconnect frees the slot", rig.Until(() => !rig.Server.Clients[slotOfB].Active, 2));
        rig.Check("ClientDisconnect ran once", rig.PrintsContaining(rig.ServerPrints, "disconnected") == disconnects + 1, $"{rig.PrintsContaining(rig.ServerPrints, "disconnected") - disconnects} calls");
        rig.Run(6);   // past the server's connect-flood window for this host
        rig.Loop.Connect(d);
        rig.Check("the freed slot is taken by the client that was rejected", rig.Until(() => d.InGame, 60) && d.ServerSlot?.Index == slotOfB, ClientSummary(d));
        rig.Loop.Disconnect(d);
        rig.Until(() => !rig.Server.Clients[slotOfB].Active, 2);
        rig.Run(6);
        rig.Loop.Connect(b);
        rig.Check("the client that left reconnects and reaches the game again", rig.Until(() => b.InGame, 60), ClientSummary(b));

        // A client that stops sending is dropped after net_messagetimeout, once.
        disconnects = rig.PrintsContaining(rig.ServerPrints, "disconnected");
        int slotOfC = c.ServerSlot!.Index;
        c.Frozen = true;
        double silentAt = rig.Now;
        bool dropped = rig.Until(() => !rig.Server.Clients[slotOfC].Active, 12);
        rig.Check("a silent client is dropped by the timeout", dropped, string.Create(CultureInfo.InvariantCulture, $"after {rig.Now - silentAt:0.0} s (net_messagetimeout 4)"));
        rig.Run(1);
        rig.Check("ClientDisconnect ran once for it", rig.PrintsContaining(rig.ServerPrints, "disconnected") == disconnects + 1, $"{rig.PrintsContaining(rig.ServerPrints, "disconnected") - disconnects} calls");
        rig.Check("the timed-out slot is free", !rig.Server.Clients[slotOfC].Active && rig.ServerEvents.Any(e => e.Contains("Timed out", StringComparison.Ordinal)));

        // Garbage: bytes that are no datagram of the protocol, then well-formed channel packets
        // carrying nonsense. The first must be ignored; the second gets the sender dropped, as
        // DarkPlaces drops it ("Unknown message sent to the server") - and nobody else notices.
        Random random = new(7);
        long ignoredBefore = rig.Server.DatagramsIgnored;
        for (int i = 0; i < 400; i++)
        {
            byte[] junk = new byte[random.Next(4, 1500)];
            random.NextBytes(junk);
            if (i % 3 == 0) junk[0] = junk[1] = junk[2] = junk[3] = 0xFF;   // looks connectionless
            a.InjectToServer(junk);
            if (i % 8 == 0) rig.Loop.Step(Rig.StepSeconds);
        }
        rig.Run(0.5);
        rig.Check("raw garbage from a connected client's address does not disturb it or the server", a.InGame && rig.Server.TotalFaults == 0, ClientSummary(a));
        int slotOfA = a.ServerSlot!.Index;
        List<byte[]> framed = new();
        for (int i = 0; i < 40 && rig.Server.Clients[slotOfA].Active; i++)
        {
            byte[] nonsense = new byte[random.Next(1, 600)];
            random.NextBytes(nonsense);
            framed.Clear();
            a.Session.Client.Channel.Transmit(nonsense, rig.Now, framed);
            foreach (byte[] datagram in framed) a.InjectToServer(datagram);
            rig.Loop.Step(Rig.StepSeconds);
        }
        rig.Check("nonsense inside valid channel packets gets that client dropped", !rig.Server.Clients[slotOfA].Active, $"dropped for bad messages: {rig.Server.ClientsDroppedForBadMessages}");
        rig.Run(1);
        rig.Check("the other client plays on, clean", b.InGame && ClientClean(b), ClientSummary(b));
        rig.Check("the server program did not fault", rig.Server.TotalFaults == 0, $"{rig.Server.TotalFaults} faults" + (rig.Server.Host!.Faults.Count > 0 ? ": " + Printable(rig.Server.Host.Faults[0].Message, 600) : ""));
        return rig.Failures;
    }

    // ---- team change ------------------------------------------------------------------------------------

    private static int ClientsTeams(Options o)
    {
        Log("");
        Log("== team change (team deathmatch) ==");
        using Rig rig = new(o, 8, ("bot_number", "2"), ("g_tdm", "1"), ("g_dm", "0"), ("g_balance_teams", "0"), ("g_balance_teams_prevent_imbalance", "0"), ("g_changeteam_banned", "0"), ("sv_teamnagger", "0"));
        if (!rig.Server.Start(o.Map)) { Log("FAIL  the level could not be started"); return 1; }
        SvLoopbackClient a = rig.AddClient("alice"), b = rig.AddClient("bob");
        rig.Check("two clients reach the game", rig.Until(() => a.InGame && b.InGame, 60), $"{ClientSummary(a)}; {ClientSummary(b)}");
        SvqcHost host = rig.Server.Host!;
        float Team(SvLoopbackClient c) => c.ServerSlot is { } slot ? host.Vm.FieldFloat(slot.Edict, host.F.Team) : -1;
        // NUM_TEAM_1 (red) 5, NUM_TEAM_2 (blue) 14
        // joinAllowed: "time < jointime + MIN_SPEC_TIME" - a join in the first second after connecting is ignored.
        rig.Run(1.5);
        a.Session.Client.SendStringCommand("selectteam red");
        b.Session.Client.SendStringCommand("selectteam blue");
        bool onTeams = rig.Until(() => Team(a) == 5 && Team(b) == 14 && a.ServerSlot!.Frags != -666 && b.ServerSlot!.Frags != -666, 8);
        rig.Check("selectteam red / blue then join puts them on those teams", onTeams, $"teams {Team(a)} {Team(b)}");
        if (!onTeams) foreach (string line in rig.ClientPrints[0].TakeLast(10)) Log("  alice heard: " + Printable(line, 200));
        a.Session.Client.SendStringCommand("selectteam blue");
        int heardBefore = rig.ClientPrints[0].Count;
        bool moved = rig.Until(() => Team(a) == 14, 8);
        rig.Check("selectteam blue moves alice across", moved, $"team {Team(a)}");
        if (!moved) foreach (string line in rig.ClientPrints[0].Skip(heardBefore).Take(10)) Log("  alice heard: " + Printable(line, 200));
        // the scoreboard colours follow (svc_updatecolors: the low nibble is the team colour, 13 = blue)
        rig.Run(1);
        rig.Check("bob's client sees alice's new colours", (b.Session.State.Scores[a.ServerSlot!.Index].Colors & 15) == 13, $"colours {b.Session.State.Scores[a.ServerSlot.Index].Colors}");
        rig.Check("both clients clean, no server fault", ClientClean(a) && ClientClean(b) && rig.Server.TotalFaults == 0, $"{ClientSummary(a)}; {ClientSummary(b)}; {rig.Server.TotalFaults} faults");
        return rig.Failures;
    }

    // ---- movement prediction agreement ----------------------------------------------------------------

    /// <summary>Per input sequence number: where the client's prediction put the player after applying it, and where the server did.</summary>
    internal sealed class AgreementLog
    {
        private readonly Dictionary<uint, QcVector> _client = new();
        public readonly List<(double T, uint Sequence, double Error, QcVector Client, QcVector Server, string Phase)> Samples = new();

        public void ClientSample(uint sequence, QcVector origin) => _client[sequence] = origin;

        public void ServerSample(double t, uint sequence, QcVector origin, string phase)
        {
            if (sequence == 0 || !_client.Remove(sequence, out QcVector mine)) return;
            double d = Math.Sqrt(Math.Pow(mine.X - origin.X, 2) + Math.Pow(mine.Y - origin.Y, 2) + Math.Pow(mine.Z - origin.Z, 2));
            Samples.Add((t, sequence, d, mine, origin, phase));
            if (_client.Count > 4096) _client.Clear();
        }

        public double Percentile(double p)
        {
            if (Samples.Count == 0) return 0;
            double[] sorted = Samples.Select(s => s.Error).OrderBy(e => e).ToArray();
            return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        }
    }

    private static string V3(QcVector v) => string.Create(CultureInfo.InvariantCulture, $"'{v.X:0.000} {v.Y:0.000} {v.Z:0.000}'");

    private static int ClientsMovement(Options o)
    {
        Log("");
        Log("== movement prediction agreement, sampled every frame ==");
        using Rig rig = new(o, 8, ("bot_number", "0"));
        if (!rig.Server.Start(o.Map)) { Log("FAIL  the level could not be started"); return 1; }
        SvLoopbackClient c = rig.AddClient("runner");
        rig.Check("the client reaches the game", rig.Until(() => c.InGame, 60), ClientSummary(c));
        c.Session.Client.SendStringCommand("join");
        rig.Until(() => c.ServerSlot is { Frags: not -666 }, 8);
        rig.Run(2);

        // A scripted run: straight, a long turn while running, strafing, jumps while running and
        // turning, a stop. Sampled after every step: the client's predicted origin for the input
        // sequence it has just built, against the server's origin once it has executed that sequence.
        AgreementLog log = new();
        // The program moves its player in CSQC_UpdateView: draw after every step, or half the samples
        // would compare the server with a position one frame stale.
        rig.Loop!.DrawRate = 1000;
        double start = rig.Now;
        uint lastServerSequence = 0;
        const double Duration = 40;
        while (rig.Now - start < Duration)
        {
            double t = rig.Now - start;
            LegacyInput input = default;
            string phase;
            QcVector angles = c.Session.State.ViewAngles;
            if (t < 4) { phase = "straight"; input.ForwardMove = 400; }
            else if (t < 10) { phase = "turning"; input.ForwardMove = 400; angles.Y += (float)(Rig.StepSeconds * 90); }
            else if (t < 14) { phase = "strafing"; input.SideMove = (int)(t * 2) % 2 == 0 ? 400 : -400; input.ForwardMove = 200; }
            else if (t < 24) { phase = "jumping"; input.ForwardMove = 400; if (t % 1.2 < 0.15) input.Buttons |= 2; angles.Y -= (float)(Rig.StepSeconds * 45); }
            else if (t < 32) { phase = "bunnyhop"; input.ForwardMove = 400; input.Buttons |= 2; angles.Y += (float)(Rig.StepSeconds * 20); }
            else if (t < 36) { phase = "backwards"; input.ForwardMove = -400; }
            else phase = "standing";
            c.Session.State.ViewAngles = angles;
            c.Input = input;
            rig.Loop!.Step(Rig.StepSeconds);

            if (c.TryGetClientPlayerOrigin(out QcVector mine)) log.ClientSample(c.Session.State.MoveCommands[0].Sequence, mine);
            if (c.ServerSlot is { } slot && slot.MoveSequence != lastServerSequence && c.TryGetServerPlayerOrigin(out QcVector theirs))
            {
                lastServerSequence = slot.MoveSequence;
                log.ServerSample(t, slot.MoveSequence, theirs, phase);
            }
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"  {log.Samples.Count} matched samples over {Duration} s: median {log.Percentile(0.5):0.000}, p90 {log.Percentile(0.9):0.000}, p99 {log.Percentile(0.99):0.000}, p99.9 {log.Percentile(0.999):0.000}, max {(log.Samples.Count > 0 ? log.Samples.Max(s => s.Error) : 0):0.000} units"));
        foreach (IGrouping<string, (double T, uint Sequence, double Error, QcVector Client, QcVector Server, string Phase)> g in log.Samples.GroupBy(s => s.Phase))
        {
            double[] e = g.Select(s => s.Error).OrderBy(x => x).ToArray();
            Log(string.Create(CultureInfo.InvariantCulture, $"  {g.Key,-10} {e.Length,5} samples: median {e[e.Length / 2]:0.000}, p99 {e[Math.Min(e.Length - 1, (int)(e.Length * 0.99))]:0.000}, max {e[^1]:0.000}; over 1 unit: {e.Count(x => x > 1)}, over 4: {e.Count(x => x > 4)}, over 16: {e.Count(x => x > 16)}"));
        }
        foreach ((double t, uint sequence, double error, QcVector mine, QcVector theirs, string phase) in log.Samples.OrderByDescending(s => s.Error).Take(8))
            Log(string.Create(CultureInfo.InvariantCulture, $"  worst: t={t:0.000} seq {sequence} {phase}: {error:0.000} units; client {V(mine)} server {V(theirs)}"));
        // The frames around each of the three worst disagreements, to see what kind of event it was.
        foreach ((double t, uint _, double _, QcVector _, QcVector _, string _) in log.Samples.OrderByDescending(s => s.Error).Take(3))
        {
            Log(string.Create(CultureInfo.InvariantCulture, $"  around t={t:0.000}:"));
            foreach ((double st, uint seq, double error, QcVector mine, QcVector theirs, string phase) in log.Samples.Where(s => s.T > t - 0.04 && s.T < t + 0.06))
                Log(string.Create(CultureInfo.InvariantCulture, $"    t={st:0.000} seq {seq}: error {error,8:0.000}  client {V3(mine)}  server {V3(theirs)}"));
        }
        rig.Check("the client moved", log.Samples.Count > 1000, $"{log.Samples.Count} samples");
        // Two kinds of disagreement are not prediction errors. A respawn or teleport is the server's
        // alone for the one frame it takes to arrive (the server origin jumps by more than a frame
        // of any movement). And the client program eases its own player up and down steps
        // (cl_stairsmoothspeed, 200 units a second) while the server's steps at once - vertical only.
        int teleports = 0, horizontalOver = 0, verticalOver = 0;
        double worstHorizontal = 0, worstVertical = 0;
        QcVector previous = log.Samples.Count > 0 ? log.Samples[0].Server : default;
        foreach ((double _, uint _, double _, QcVector mine, QcVector theirs, string _) in log.Samples)
        {
            double jump = Math.Sqrt(Math.Pow(theirs.X - previous.X, 2) + Math.Pow(theirs.Y - previous.Y, 2) + Math.Pow(theirs.Z - previous.Z, 2));
            previous = theirs;
            if (jump > 200) { teleports++; continue; }
            double h = Math.Sqrt(Math.Pow(mine.X - theirs.X, 2) + Math.Pow(mine.Y - theirs.Y, 2)), v = Math.Abs(mine.Z - theirs.Z);
            worstHorizontal = Math.Max(worstHorizontal, h);
            worstVertical = Math.Max(worstVertical, v);
            if (h > 0.01) horizontalOver++;
            if (v > 0.01) verticalOver++;
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"  apart from {teleports} respawn/teleport frames: horizontal disagreement over 0.01 units in {horizontalOver} samples (worst {worstHorizontal:0.000}); vertical in {verticalOver} (worst {worstVertical:0.000}, the client program's stair smoothing)"));
        rig.Check("prediction agrees with the server horizontally (never off by a twentieth of a unit)", horizontalOver <= log.Samples.Count / 1000 && worstHorizontal < 0.05);
        rig.Check("vertical disagreement is stair smoothing only: rare and under a step's height", verticalOver < log.Samples.Count / 50 && worstVertical < 32);
        rig.Check("client clean, no server fault", ClientClean(c) && rig.Server.TotalFaults == 0, ClientSummary(c));
        return rig.Failures;
    }
}
