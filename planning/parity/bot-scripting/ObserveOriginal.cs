using System.Diagnostics;
using System.Net;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;

// Join as a DP7 spectator and decode the actual positional sound messages.
using var vfs = new VirtualFileSystem();
if (!vfs.MountGameDir(args[1]))
    throw new IOException($"Could not mount {args[1]}");
var cvars = new CvarService();
CsqcEngineCvars.Register(cvars);
var scripts = new ConfigInterpreter(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null);
scripts.ExecuteFile("default.cfg");
var services = new LegacyQcHost(cvars, vfs) { WriteRoot = args[2] };
var presentation = new HeadlessLegacyPresentation(vfs);
var options = new LegacyClientOptions { AlwaysDownloadProgram = false };
options.Client.Signon.Name = "CueObserver";
using var session = new LegacyClientSession(services, scripts, presentation, options);
session.EngineMessages = new Observer(session);
using var transport = new DpUdpTransport(new IPEndPoint(IPAddress.Loopback, int.Parse(args[0])));
var clock = Stopwatch.StartNew();
presentation.ModelData.Working = () =>
{
    foreach (byte[] packet in session.KeepAlive(clock.Elapsed.TotalSeconds))
        transport.Send(packet);
};
session.Connect(0);
bool ready = false;
while (clock.Elapsed.TotalSeconds < 40)
{
    double now = clock.Elapsed.TotalSeconds;
    session.BeginFrame(now);
    while (transport.TryReceive(out byte[] datagram))
        session.Receive(datagram, now);
    foreach (byte[] packet in session.Frame(now, default))
        transport.Send(packet);
    if (session.Client.State is DpClientState.Rejected or DpClientState.Failed or DpClientState.TimedOut)
        throw new IOException(session.Client.LastError);
    if (!ready && session.Client.Signon.Stage == 4)
    {
        ready = true;
        Console.WriteLine("OBSERVER_READY");
    }
    Thread.Sleep(10);
}

sealed class Observer(LegacyClientSession session) : IDpClientHandler
{
    public void OnSound(in DpSound sound)
    {
        string? sample = session.State.SoundNames[sound.SoundIndex];
        if (sample?.StartsWith("botprobe/", StringComparison.Ordinal) == true)
            Console.WriteLine(FormattableString.Invariant(
                $"CUE {sample} server_time={session.Client.ServerTime:F3} entity={sound.Entity} channel={sound.Channel} volume={sound.Volume}/255 attenuation={sound.Attenuation}"));
    }
}
