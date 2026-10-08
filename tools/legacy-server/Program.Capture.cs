// legacy-server capture: join any DP7 server - a real darkplaces-dedicated or this one - as the
// headless legacy client and write down the STRUCTURE of what it sends: the signon messages in
// order, svc_serverinfo's fields, the precache lists, what the signon buffer holds, the svc counts
// of every second in the game, the stats, and how many entities each stream carries. Two captures
// of the same scripted session can then be compared line by line (Base/darkplaces sv_main.c
// SV_SendServerinfo, sv_user.c SV_Spawn_f and sv_send.c SV_SendClientDatagram say what to expect).
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private sealed class CaptureSink : IDpClientHandler
    {
        public long EntityFrames, ChangedEntities;
        public int MaxEntityNumber, ActiveNow;
        /// <summary>The engine-networked entities active after the last frame.</summary>
        public readonly List<int> ActiveList = new();
        public readonly SortedSet<int> SeenEntities = new();
        public readonly SortedDictionary<int, int> PerEntity = new();
        public readonly SortedDictionary<string, int> Fields = new(StringComparer.Ordinal);
        public readonly SortedDictionary<int, string> Last = new();
        public int Sounds, StaticSounds, Statics, Baselines, PointParticles, TrailParticles, Effects, Particles;
        public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities)
        {
            EntityFrames++;
            ChangedEntities += frame.Changed.Count;
            foreach (int number in frame.Changed)
            {
                SeenEntities.Add(number);
                MaxEntityNumber = Math.Max(MaxEntityNumber, number);
                PerEntity[number] = PerEntity.GetValueOrDefault(number) + 1;
                // which fields of the state differ from the frame before: the decoder keeps both
                ref readonly EntityState a = ref entities.Previous(number);
                ref readonly EntityState b = ref entities.Current(number);
                void F(string name, bool differs) { if (differs) Fields[name] = Fields.GetValueOrDefault(name) + 1; }
                F("active", a.Active != b.Active); F("origin", a.Origin != b.Origin); F("angles", a.Angles != b.Angles); F("model", a.ModelIndex != b.ModelIndex);
                F("frame", a.Frame != b.Frame); F("skin", a.Skin != b.Skin); F("effects", a.Effects != b.Effects); F("flags", a.Flags != b.Flags);
                F("alpha", a.Alpha != b.Alpha); F("scale", a.Scale != b.Scale); F("colormap", a.Colormap != b.Colormap); F("tag", a.TagEntity != b.TagEntity || a.TagIndex != b.TagIndex);
                F("light", a.Light0 != b.Light0 || a.Light1 != b.Light1 || a.Light2 != b.Light2 || a.Light3 != b.Light3 || a.LightStyle != b.LightStyle || a.LightPFlags != b.LightPFlags);
                F("glow", a.GlowSize != b.GlowSize || a.GlowColor != b.GlowColor); F("colormod", a.ColorMod0 != b.ColorMod0 || a.ColorMod1 != b.ColorMod1 || a.ColorMod2 != b.ColorMod2);
                F("glowmod", a.GlowMod0 != b.GlowMod0 || a.GlowMod1 != b.GlowMod1 || a.GlowMod2 != b.GlowMod2); F("trail", a.TrailEffectNum != b.TrailEffectNum);
                F("nothing", a.Active == b.Active && a.Origin == b.Origin && a.Angles == b.Angles && a.ModelIndex == b.ModelIndex && a.Frame == b.Frame && a.Skin == b.Skin && a.Effects == b.Effects && a.Flags == b.Flags && a.Alpha == b.Alpha && a.Scale == b.Scale && a.Colormap == b.Colormap);
                if (b.IsActive) Last[number] = $"model {b.ModelIndex} flags {b.Flags} effects {b.Effects} tag {b.TagEntity}/{b.TagIndex} alpha {b.Alpha} origin {b.Origin}";
            }
            ActiveList.Clear();
            for (int i = 1; i < entities.Count; i++) if (entities.Current(i).IsActive) ActiveList.Add(i);
            ActiveNow = ActiveList.Count;
        }
        public void OnSound(in DpSound sound) => Sounds++;
        public void OnSpawnStaticSound(in DpStaticSound sound) => StaticSounds++;
        public void OnSpawnStatic(in EntityState state) => Statics++;
        public void OnSpawnBaseline(int entity, in EntityState baseline) => Baselines++;
        public void OnPointParticles(in DpPointParticles particles) => PointParticles++;
        public void OnTrailParticles(in DpTrailParticles trail) => TrailParticles++;
        public void OnEffect(in DpEffect effect) => Effects++;
        public void OnParticle(in DpParticle particle) => Particles++;
    }

    private static string SvcName(int id) => Enum.IsDefined(typeof(Svc), (byte)id) ? ((Svc)id).ToString() : "svc" + id.ToString(CultureInfo.InvariantCulture);

    private static int Capture(Options o, string host)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            Log($"RESULT: FAILED (\"{host}\" is not an address)");
            return 1;
        }
        using VirtualFileSystem vfs = new();
        if (!Directory.Exists(o.Data) || !vfs.MountGameDir(o.Data))
        {
            Log("RESULT: FAILED (nothing could be mounted from the data directory)");
            return 1;
        }
        CvarService cvars = new();
        ConfigInterpreter interpreter = new(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null);
        CsqcEngineCvars.Register(cvars);
        cvars.Register("pr_checkextension", "1");
        cvars.Register("utf8_enable", "1");
        cvars.Register("developer", "0");
        interpreter.ExecuteFile("default.cfg");
        string writeRoot = Path.Combine(Path.GetTempPath(), "legacy-capture-" + Guid.NewGuid().ToString("N"));
        LegacyQcHost services = new(cvars, vfs) { WriteRoot = writeRoot };
        HeadlessLegacyPresentation presentation = new(vfs);
        // The program comes from the game data when it is the one the server names: the download is
        // not what is being compared here, and skipping it keeps the two captures' clocks alike.
        LegacyClientOptions options = new() { AlwaysDownloadProgram = false, Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
        options.Client.Signon.Name = "capture";
        options.Client.Signon.Rate = 262144;
        options.Client.Signon.RateBurstSize = 1024;
        using LegacyClientSession session = new(services, interpreter, presentation, options);
        CaptureSink sink = new();
        session.EngineMessages = sink;
        DpClient client = session.Client;
        using DpUdpTransport transport = new(new IPEndPoint(address, o.Port));

        // What arrives, in order while signing on and as counts afterwards.
        Dictionary<string, int> window = new(StringComparer.Ordinal), total = new(StringComparer.Ordinal);
        List<string> signon = new();
        int stage = 0;
        bool inGame = false;
        client.Parser.CommandTrace = (id, _) =>
        {
            string name = SvcName(id);
            window[name] = window.GetValueOrDefault(name) + 1;
            if (inGame) total[name] = total.GetValueOrDefault(name) + 1;
            else if (signon.Count < 4000) signon.Add($"stage{stage} {name}");
        };

        Log($"capture of {address}:{o.Port} for {o.Seconds} s in the game");
        Stopwatch clock = Stopwatch.StartNew();
        session.Connect(clock.Elapsed.TotalSeconds);
        presentation.ModelData.Working = () =>
        {
            foreach (byte[] d in session.KeepAlive(clock.Elapsed.TotalSeconds)) transport.Send(d);
        };
        double inGameAt = -1, nextSecond = 0, nextFrame = 0;
        int second = 0;
        bool joined = false, wroteServerInfo = false, wroteStats = false;
        string? failure = null;
        while (true)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!inGame && now > 120) { failure = "not in the game after 120 s"; break; }
            if (inGame && now - inGameAt >= o.Seconds) break;
            session.BeginFrame(now);
            while (transport.TryReceive(out byte[] datagram)) session.Receive(datagram, now);
            // "--mode spectate": stay an observer, for a capture from a place the server is told to put it
            if (inGame && !joined && now - inGameAt >= 5 && o.Mode != "spectate")
            {
                joined = true;
                client.SendStringCommand("join");
            }
            foreach (byte[] d in session.Frame(now, default)) transport.Send(d);
            if (client.State is DpClientState.Rejected or DpClientState.TimedOut or DpClientState.Failed) { failure = $"{client.State}: {client.LastError}"; break; }

            if (client.Signon.Stage != stage)
            {
                stage = client.Signon.Stage;
                if (stage >= 1 && !wroteServerInfo)
                {
                    wroteServerInfo = true;
                    CsqcClientState s = session.State;
                    string[] models = s.ModelNames.Skip(1).TakeWhile(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToArray();
                    string[] sounds = s.SoundNames.Skip(1).TakeWhile(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToArray();
                    Log($"serverinfo: maxclients {s.MaxClients}, world \"{s.WorldModel}\", message \"{s.WorldMessage}\", {models.Length} models, {sounds.Length} sounds, view entity {s.ViewEntity}");
                    Log($"serverinfo: csqc_progname {client.Signon.CsqcProgName} size {client.Signon.CsqcProgSize} crc {client.Signon.CsqcProgCrc}, cl_serverextension_download {client.Signon.ServerExtensionDownload}");
                    for (int i = 0; i < models.Length; i++) Log($"model {i + 1} {models[i]}");
                    for (int i = 0; i < sounds.Length; i++) Log($"sound {i + 1} {sounds[i]}");
                }
                if (stage == DpProtocol.Signons)
                {
                    inGame = true;
                    inGameAt = now;
                    nextSecond = now + 1;
                    nextFrame = now;
                    // The signon, run-length encoded: "stage1 SpawnStaticSound x12".
                    string? last = null;
                    int run = 0;
                    foreach (string entry in signon.Append("end"))
                    {
                        if (entry == last) { run++; continue; }
                        if (last is not null) Log($"signon: {last}{(run > 1 ? " x" + run : "")}");
                        last = entry;
                        run = 1;
                    }
                    Log($"signon totals: static sounds {sink.StaticSounds}, static entities {sink.Statics}, baselines {sink.Baselines}; lightstyles and names are in the list above");
                    window.Clear();
                }
            }
            if (inGame && now >= nextFrame)
            {
                nextFrame += 1.0 / 60;
                if (nextFrame < now - 0.25) nextFrame = now;
                session.Draw(1.0 / 60);
            }
            if (inGame && now >= nextSecond)
            {
                nextSecond += 1;
                second++;
                Log($"second {second,2}: " + string.Join(" ", window.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")) +
                    $" | e5 active {sink.ActiveNow} csqc updates {session.Host?.EntityUpdates ?? 0} removes {session.Host?.EntityRemoves ?? 0}");
                window.Clear();
            }
            if (inGame && !wroteStats && now - inGameAt >= Math.Min(20, o.Seconds - 1))
            {
                wroteStats = true;
                int[] stats = session.State.Stats;
                Log("stats (index=int/float): " + string.Join(" ", Enumerable.Range(0, stats.Length).Where(i => stats[i] != 0)
                    .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i}={stats[i]}/{BitConverter.Int32BitsToSingle(stats[i]):G6}"))));
            }
            double wake = now + 0.002;
            while (clock.Elapsed.TotalSeconds < wake) Thread.Yield();
        }
        double played = inGameAt >= 0 ? clock.Elapsed.TotalSeconds - inGameAt : 0;
        if (played > 0)
        {
            Log("per second, over the whole time in the game: " + string.Join(" ", total.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}={p.Value / played:0.0}"))));
            Log(string.Create(CultureInfo.InvariantCulture, $"entities: svc_entities frames {sink.EntityFrames} ({sink.EntityFrames / played:0.0}/s), entity updates in them {sink.ChangedEntities} ({sink.ChangedEntities / played:0.0}/s), ") +
                $"distinct entity numbers {sink.SeenEntities.Count}, highest {sink.MaxEntityNumber}, active at the end {sink.ActiveNow}; csqc updates {session.Host?.EntityUpdates ?? 0}, removes {session.Host?.EntityRemoves ?? 0}, " +
                $"temp entities to the program {session.Host?.TempEntitiesConsumed ?? 0}; sounds {sink.Sounds}, point particles {sink.PointParticles}, trail particles {sink.TrailParticles}");
            // The two entity streams as they stand at the end: what the server considers visible from
            // where this client is. Two servers asked from the same place should name the same sets.
            Log($"visible at the end, engine entities ({sink.ActiveList.Count}): " + string.Join(" ", sink.ActiveList));
            if (session.Host is { } program)
            {
                List<int> shared = new();
                for (int n = 1; n < DpProtocol.MaxEdicts; n++)
                {
                    int edict = program.EdictForServerEntity(n);
                    if (edict > 0 && edict < program.Vm.NumEdicts && !program.Vm.IsFree(edict)) shared.Add(n);
                }
                Log($"visible at the end, csqc entities ({shared.Count}): " + string.Join(" ", shared));
            }
            Log(string.Create(CultureInfo.InvariantCulture, $"view at the end: origin stat-less; player entity {session.State.PlayerEntity}, view entity {session.State.ViewEntity}"));
            Log("e5 updates by entity: " + string.Join(" ", sink.PerEntity.Select(p => $"#{p.Key}={p.Value}")));
            Log("e5 fields that changed: " + string.Join(" ", sink.Fields.Select(p => $"{p.Key}={p.Value}")));
            foreach ((int number, string state) in sink.Last) Log($"e5 entity #{number}: {state}; model name {(session.State.ModelNames is { } names && state.Split(' ')[1] is { } m && int.TryParse(m, out int mi) && mi < names.Length ? names[mi] : "")}");
            Log($"client: messages parsed {client.MessagesParsed}, not decoded {session.MessagesNotDecoded}, program faults {session.Host?.FaultCount ?? 0}, desyncs {session.Host?.DesyncCount ?? 0}, " +
                $"reliable resends {client.Channel.PacketsResent}, unreliable gaps {client.Channel.DroppedDatagrams}");
        }
        if (client.State == DpClientState.Connected)
            foreach (byte[] d in session.Disconnect(clock.Elapsed.TotalSeconds)) transport.Send(d);
        try { if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true); } catch (IOException) { }
        Log(failure is null ? "RESULT: OK" : "RESULT: FAILED (" + failure + ")");
        return failure is null ? 0 : 1;
    }
}
