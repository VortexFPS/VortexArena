using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// Stage two of the demo verification (planning/specs/legacy-compat.md section 9.3): the two demos
/// shipped with Xonotic are played through the protocol parser AND the client program each was
/// recorded with, headless. Stage one (DpDemoTests) stops at every message only QuakeC can measure;
/// here the VM measures them, so "fully decoded" counts messages whose QuakeC payloads were consumed
/// to the byte.
///
/// The report goes to the test output and to _scratch/csqc-demo-replay-&lt;demo&gt;.txt. The assertions
/// are the things that must hold whatever the remaining gaps: the program loads (or the refusal is
/// exact), CSQC_Init completes, nothing but a recorded program fault ever escapes, and the engine
/// protocol itself never mis-parses before the program first loses its place.
/// Needs the upstream reference checkout (../Base); returns early without it.
/// </summary>
public class CsqcDemoReplayTests
{
    private readonly ITestOutputHelper _output;
    public CsqcDemoReplayTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static string DemoPath(string name) => Path.Combine(TestPaths.BaseCorePk3Dir, "demos", name);

    /// <param name="headless">Receives the headless presentation when the replay is to run on one
    /// (real map, models and picture sizes) instead of the null presentation; called after the replay.</param>
    internal static CsqcDemoReplayResult Replay(string demoPath, bool callUpdateView, int maxMessages = int.MaxValue,
        Action<HeadlessLegacyPresentation>? headless = null)
    {
        string baseData = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(demoPath)!, "..", ".."));
        using VirtualFileSystem vfs = new();
        Assert.True(vfs.MountGameDir(baseData));

        CvarService cvars = new();
        ConfigInterpreter interpreter = new(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null);
        // What the engine itself registers and the program checks before anything else.
        // The engine's own cvars first, as DarkPlaces registers them before any configuration runs.
        CsqcEngineCvars.Register(cvars);
        cvars.Register("pr_checkextension", "1");
        cvars.Register("utf8_enable", "1");
        cvars.Register("developer", "0");
        interpreter.ExecuteFile("default.cfg");

        string writeRoot = Path.Combine(Path.GetTempPath(), "va-csqcreplay-" + Guid.NewGuid().ToString("N"));
        CsqcReplayLog log = new();
        LegacyQcHost services = new(cvars, vfs) { WriteRoot = writeRoot, PrintSink = log.Print, WarningSink = log.Warning };
        ILegacyPresentation presentation = headless is null
            ? new NullLegacyPresentation { FileExists = services.FileExists }
            : new HeadlessLegacyPresentation(vfs);
        try
        {
            using FileStream demo = File.OpenRead(demoPath);
            CsqcDemoReplayResult result = CsqcDemoReplay.Run(demo, Path.GetFileName(demoPath), services, interpreter, presentation,
                new CsqcDemoReplayOptions { CallUpdateView = callUpdateView, MaxMessages = maxMessages, Log = log, WriteRoot = writeRoot });
            if (presentation is HeadlessLegacyPresentation real) headless!(real);
            return result;
        }
        finally
        {
            if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("little-bot-orchestra.dem")]
    [InlineData("the-big-keybench.dem")]
    public void Real_Demo_Replays_Through_Its_Own_Client_Program(string name)
    {
        string path = DemoPath(name);
        if (!File.Exists(path)) return; // reference checkout not present on this machine

        // Two passes: decoding alone, then decoding plus one frame per server time stamp. A stop in
        // the first is a parse problem; one that appears only in the second came from a frame.
        CsqcDemoReplayResult parseOnly = Replay(path, callUpdateView: false);
        CsqcDemoReplayResult withFrames = Replay(path, callUpdateView: true);

        string report = parseOnly.Describe() + "\n" + withFrames.Describe();
        _output.WriteLine(report);
        try
        {
            string scratch = Path.Combine(RepoRoot(), "_scratch");
            Directory.CreateDirectory(scratch);
            File.WriteAllText(Path.Combine(scratch, $"csqc-demo-replay-{Path.GetFileNameWithoutExtension(name)}.txt"), report);
        }
        catch (IOException) { }

        foreach (CsqcDemoReplayResult r in new[] { parseOnly, withFrames })
        {
            // Nothing escapes the host: a hostile or merely unexpected program can fault, never throw.
            Assert.True(r.Unexpected is null, r.Unexpected?.ToString());
            Assert.Null(r.ReaderError);
            Assert.True(r.Messages > 100, "a real demo has many messages");

            // The recording carries its own program, byte for byte what the server announced.
            Assert.True(r.ProgramFound, "the demo embeds its csprogs.dat");
            Assert.Equal(r.ProgramSizeExpected, r.ProgramSize);
            Assert.Equal(r.ProgramCrcExpected, r.ProgramCrc);

            // It loads, or the loader says exactly why not (an opcode it does not implement, by number).
            if (!r.ProgramLoaded)
            {
                Assert.False(string.IsNullOrWhiteSpace(r.ProgramRefusal));
                continue;
            }
            Assert.True(r.InitRan && r.InitCompleted, "CSQC_Init completed: " + r.FaultSamples.FirstOrDefault()?.Message);

            // Before the program first loses its place, every stop would be the engine parser's own
            // doing: there must be none.
            Assert.True(r.ProtocolErrorsBeforeFirstQuakeCStop == 0, r.FirstStop?.ToString());
            Assert.True(r.EntityUpdates > 0, "the program decoded entity updates");
        }
    }

    /// <summary>
    /// The same replay with the headless presentation: the program's traces hit the demo's real map,
    /// its models have real boxes, bones and animation lengths, its pictures real sizes. The null
    /// replay above shows the program can decode its network data; this one shows it still can when
    /// its questions get true answers and its own code takes the branches real data leads to.
    /// A fault here that the null replay does not have is a real finding.
    /// </summary>
    [Theory]
    [InlineData("little-bot-orchestra.dem")]
    [InlineData("the-big-keybench.dem")]
    public void Real_Demo_Replays_With_The_Headless_Presentation(string name)
    {
        string path = DemoPath(name);
        if (!File.Exists(path)) return; // reference checkout not present on this machine

        string world = "";
        CsqcDemoReplayResult r = Replay(path, callUpdateView: true, headless: p =>
            world = $"world: {(p.Map.MapName is null ? "NOT LOADED (" + p.Map.LoadError + ")" : p.Map.MapName + ", " + p.Map.Collision!.Brushes.Count + " collision brushes")}; "
                + $"traces that hit the candidate bound: {p.Map.CandidateOverflows}; models parsed {p.ModelData.ModelsParsed}, cached {p.ModelData.CachedModels}; "
                + $"skeleton objects alive at the end {p.ModelData.LiveSkeletons}; pictures cached {p.Pictures.Count}");
        string report = "presentation: HeadlessLegacyPresentation" + Environment.NewLine + world + Environment.NewLine + r.Describe();
        _output.WriteLine(report);
        try
        {
            string scratch = Path.Combine(RepoRoot(), "_scratch");
            Directory.CreateDirectory(scratch);
            File.WriteAllText(Path.Combine(scratch, $"csqc-demo-replay-headless-{Path.GetFileNameWithoutExtension(name)}.txt"), report);
        }
        catch (IOException) { }

        Assert.True(r.Unexpected is null, r.Unexpected?.ToString());
        Assert.Null(r.ReaderError);
        Assert.True(r.ProgramLoaded, r.ProgramRefusal);
        Assert.True(r.InitRan && r.InitCompleted, "CSQC_Init completed: " + r.FaultSamples.FirstOrDefault()?.Message);
        Assert.DoesNotContain("NOT LOADED", world);
        // Every message decodes, as with the null presentation, and nothing faults.
        Assert.True(r.Stopped == 0, r.FirstStop?.ToString());
        Assert.Equal(r.Messages, r.FullyDecoded);
        Assert.True(r.Faults == 0, r.FaultSamples.FirstOrDefault()?.Message);
        Assert.Equal(0, r.Desyncs);
        Assert.True(r.Frames > 1000);
        // The questions were asked of real data. (The smaller demo's program is an older, simpler one.)
        Assert.True(r.PresentationCalls.GetValueOrDefault("Trace") > 0);
        Assert.True(r.PresentationCalls.GetValueOrDefault("TryGetBounds") > 0);
    }
}
