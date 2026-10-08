using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VortexArena.Modding;
using Wasmtime;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Modding;

/// <summary>
/// Loading a mod in two halves - compile (slow, any thread, cacheable) and bind-and-start (fast, the
/// host's thread) - and the on-disk cache of compiled modules.
///
/// The cache holds machine code that Wasmtime runs without checking, so most of these tests are about
/// what it refuses: an entry that was altered, an entry it did not write, an entry for other bytes or
/// other engine settings. Every refusal has to end in the same place - the module is compiled again
/// from the WebAssembly the client verified - and never in an error.
///
/// Every test no-ops where the Wasmtime native library is absent, like the other sandbox tests.
/// </summary>
public class ModCompileCacheTests
{
    private readonly ITestOutputHelper _output;
    public ModCompileCacheTests(ITestOutputHelper output) => _output = output;

    private sealed class CountingHost : IModHost
    {
        public int Rects, Texts;
        public readonly List<string> Logs = new();
        public void Log(ModLogLevel level, string message) => Logs.Add(message);
        public int EntityCount => 0;
        public double Time => 1.0;
        public int ReadState(ModStateKind kind, int index, Span<byte> destination) => -1;
        public bool TryGetCvar(string name, out string value) { value = ""; return false; }
        public int ResolveAsset(ModAssetKind kind, string path) => 0;
        public float MeasureText(int fontId, float size, string text) => 0;
        public bool SendToServer(ReadOnlySpan<byte> payload) => false;
        public void DrawRect(float x, float y, float w, float h, uint rgba) => Rects++;
        public void DrawPic(int id, float x, float y, float w, float h, uint rgba) { }
        public void DrawText(int font, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8) => Texts++;
        public void SetClip(float x, float y, float w, float h) { }
        public void ResetClip() { }
        public void PlaySound(int id, int channel, float volume, float pitch) { }
    }

    private static byte[] Wat(string body) => Module.ConvertText($"(module {body})");

    // Draws one rectangle a frame.
    private static readonly string RectMod = """
        (import "vortex_1" "commands" (func $commands (param i32 i32)))
        (memory (export "memory") 1)
        (data (i32.const 0) "\01\00\18\00")
        (func (export "mod_frame") (param f32) (call $commands (i32.const 0) (i32.const 24)))
        """;

    /// <summary>
    /// The same mod with a few thousand extra functions, so that compiling it takes tens of milliseconds
    /// rather than one: long enough that a test can look at the session WHILE the worker is compiling.
    /// </summary>
    private static byte[] SlowToCompileMod()
    {
        System.Text.StringBuilder text = new(RectMod);
        for (int i = 0; i < 6000; i++)
        {
            text.Append("(func (param i32 i32) (result i32) ");
            text.Append("(i32.add (i32.mul (local.get 0) (i32.const ").Append(i + 3).Append(")) ");
            text.Append("(i32.xor (i32.shl (local.get 1) (i32.const 3)) (i32.rem_u (local.get 0) (i32.const ").Append(i + 7).Append(")))))\n");
        }
        return Wat(text.ToString());
    }

    private static readonly ModLimits Small = ModLimits.Default with { FrameBudgetMs = 50, InitBudgetMs = 500, MaxMemoryBytes = 4 * ModAbi.PageSize };

    private static string? CSharpGuest()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("VA_CSHARP_GUEST_WASM");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        string template = Path.Combine(TestPaths.RepoRoot, "modding-sdk", "csharp", "templates", "hello-hud");
        if (!Directory.Exists(template)) return null;
        return Directory.EnumerateFiles(template, "hello-hud.wasm", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static string[] Entries(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.cwasm") : Array.Empty<string>();

    // ---- the two halves ----------------------------------------------------------------------------

    [Fact]
    public void AModuleCompiledOnAnotherThread_LoadsAndRunsOnThisOne()
    {
        if (!WasmModSandbox.IsAvailable) return;
        byte[] wasm = Wat(RectMod);

        ModCompiledModule compiled = Task.Run(() => WasmModSandbox.Compile("rect", wasm, Small)).GetAwaiter().GetResult();
        Assert.False(compiled.FromCache);

        CountingHost host = new();
        using WasmModSandbox sandbox = WasmModSandbox.Load(compiled, host);
        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0.016f));
        Assert.Equal(1, host.Rects);
    }

    [Fact]
    public void ACompiledModule_CanBeLoadedOnce()
    {
        if (!WasmModSandbox.IsAvailable) return;
        ModCompiledModule compiled = WasmModSandbox.Compile("rect", Wat(RectMod), Small);
        using WasmModSandbox first = WasmModSandbox.Load(compiled, new CountingHost());
        Assert.Throws<ModLoadException>(() => WasmModSandbox.Load(compiled, new CountingHost()));
        compiled.Dispose(); // nothing left to free; must not disturb the sandbox that took it
        Assert.True(first.Init());
        Assert.True(first.Frame(0.016f));
    }

    [Fact]
    public void WhatTheInterfaceForbids_IsStillRefused_WhenTheCodeComesFromTheCache()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled"));
        // Valid WebAssembly, so it compiles and is cached - but it imports something no client provides.
        byte[] wasm = Wat("""
            (import "env" "open_file" (func (param i32) (result i32)))
            (memory (export "memory") 1)
            (func (export "mod_frame") (param f32))
            """);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ModCompiledModule compiled = WasmModSandbox.Compile("nosy", wasm, Small, cache);
            Assert.Equal(attempt == 1, compiled.FromCache);
            ModLoadException refused = Assert.Throws<ModLoadException>(() => WasmModSandbox.Load(compiled, new CountingHost()));
            Assert.Contains("env", refused.Message);
        }
    }

    // ---- the cache ---------------------------------------------------------------------------------

    [Fact]
    public void TheSecondCompile_ComesFromTheCache_AndRunsTheSame()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled"));
        byte[] wasm = Wat(RectMod);

        using (ModCompiledModule first = WasmModSandbox.Compile("rect", wasm, Small, cache))
            Assert.False(first.FromCache);
        Assert.Single(Entries(cache.RootDirectory));
        Assert.Equal(1, cache.Count);

        ModCompiledModule second = WasmModSandbox.Compile("rect", wasm, Small, cache);
        Assert.True(second.FromCache);
        CountingHost host = new();
        using WasmModSandbox sandbox = WasmModSandbox.Load(second, host);
        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0.016f));
        Assert.Equal(1, host.Rects);
    }

    [Fact]
    public void AnEntryIsFiledUnderTheBytes_TheEngineSettings_AndNothingElse()
    {
        byte[] a = Wat(RectMod), b = Wat(RectMod + "(func)");
        string key = ModCompileCache.KeyFor(a, "settings-1");
        Assert.Equal(64, key.Length);
        Assert.Equal(key, ModCompileCache.KeyFor(a.ToArray(), "settings-1"));
        Assert.NotEqual(key, ModCompileCache.KeyFor(b, "settings-1"));
        Assert.NotEqual(key, ModCompileCache.KeyFor(a, "settings-2"));

        if (!WasmModSandbox.IsAvailable) return;
        // Two stack limits are two engine configurations: each gets an entry of its own.
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled"));
        WasmModSandbox.Compile("rect", a, Small, cache).Dispose();
        using ModCompiledModule other = WasmModSandbox.Compile("rect", a, Small with { MaxStackBytes = 256 * 1024 }, cache);
        Assert.False(other.FromCache);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void AnAlteredEntry_IsDeleted_AndTheModuleIsCompiledAgain()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled"));
        byte[] wasm = Wat(RectMod);
        WasmModSandbox.Compile("rect", wasm, Small, cache).Dispose();
        string entry = Entries(cache.RootDirectory).Single();

        // One bit, in the middle of the machine code.
        byte[] bytes = File.ReadAllBytes(entry);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(entry, bytes);

        ModCompiledModule again = WasmModSandbox.Compile("rect", wasm, Small, cache);
        Assert.False(again.FromCache);
        CountingHost host = new();
        using WasmModSandbox sandbox = WasmModSandbox.Load(again, host);
        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0.016f));
        Assert.Equal(1, host.Rects);
        // ...and what is stored now is the fresh, valid one.
        using ModCompiledModule third = WasmModSandbox.Compile("rect", wasm, Small, cache);
        Assert.True(third.FromCache);
    }

    [Theory]
    [InlineData("no checksum")]
    [InlineData("another installation's checksum")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public void AnEntryThisInstallationDidNotWrite_IsNeverHandedToTheRuntime(string what)
    {
        if (!WasmModSandbox.IsAvailable) return;
        using ModTempDir dir = new(), elsewhere = new();
        ModCompileCache cache = new(dir.Sub("compiled"));
        byte[] wasm = Wat(RectMod);
        WasmModSandbox.Compile("rect", wasm, Small, cache).Dispose();
        string entry = Entries(cache.RootDirectory).Single();
        string key = Path.GetFileNameWithoutExtension(entry);

        // What someone who can drop a file into the directory, but cannot read the key in it, could plant:
        // a perfectly good compiled module - of a DIFFERENT program - under this module's name.
        byte[] planted;
        ModCompileCache foreign = new(elsewhere.Sub("compiled"));
        byte[] otherWasm = Wat(RectMod.Replace("(i32.const 24)", "(i32.const 0)"));
        WasmModSandbox.Compile("other", otherWasm, Small, foreign).Dispose();
        byte[] foreignEntry = File.ReadAllBytes(Entries(foreign.RootDirectory).Single());
        switch (what)
        {
            case "no checksum": planted = foreignEntry[..^32]; break;
            case "another installation's checksum": planted = foreignEntry; break;
            case "truncated": planted = File.ReadAllBytes(entry)[..40]; break;
            default: planted = Array.Empty<byte>(); break;
        }
        File.WriteAllBytes(entry, planted);

        Assert.False(cache.TryRead(key, out byte[] read));
        Assert.Empty(read);
        Assert.False(File.Exists(entry));   // the planted file is gone, not left for another try

        // And the path the game takes: compiled again from the verified WebAssembly, drawing the rectangle
        // the real module draws (the planted program would have drawn nothing).
        ModCompiledModule compiled = WasmModSandbox.Compile("rect", wasm, Small, cache);
        Assert.False(compiled.FromCache);
        CountingHost host = new();
        using WasmModSandbox sandbox = WasmModSandbox.Load(compiled, host);
        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0.016f));
        Assert.Equal(1, host.Rects);
    }

    [Fact]
    public void WithoutItsKey_EveryOldEntryIsAMiss()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using ModTempDir dir = new();
        byte[] wasm = Wat(RectMod);
        WasmModSandbox.Compile("rect", wasm, Small, new ModCompileCache(dir.Sub("compiled"))).Dispose();

        File.Delete(Path.Combine(dir.Sub("compiled"), "install.key"));
        ModCompileCache reopened = new(dir.Sub("compiled"));   // a fresh object: the key is not in memory either
        using ModCompiledModule compiled = WasmModSandbox.Compile("rect", wasm, Small, reopened);
        Assert.False(compiled.FromCache);
        using ModCompiledModule next = WasmModSandbox.Compile("rect", wasm, Small, reopened);
        Assert.True(next.FromCache);                             // a new key was made and the entry rewritten
    }

    [Fact]
    public void NamesThatAreNotHashes_AreRefused_SoNothingCanPointOutsideTheDirectory()
    {
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled"));
        foreach (string name in new[] { "", "..", "../../evil", "C:\\evil", new string('a', 63), new string('g', 64), new string('A', 64) })
        {
            cache.Write(name, new byte[] { 1, 2, 3 });
            Assert.False(cache.TryRead(name, out _));
            cache.Remove(name);
        }
        Assert.Empty(Directory.Exists(dir.Path) ? Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories) : Array.Empty<string>());
    }

    [Fact]
    public void TheCacheKeepsOnlyItsNewestEntries()
    {
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled")) { MaxEntries = 3 };
        List<string> keys = new();
        for (int i = 0; i < 6; i++)
        {
            string key = ModCompileCache.KeyFor(new byte[] { (byte)i }, "x");
            keys.Add(key);
            cache.Write(key, new byte[] { 9, 9, (byte)i });
            File.SetLastWriteTimeUtc(Path.Combine(cache.RootDirectory, key + ".cwasm"), DateTime.UtcNow.AddMinutes(i - 10));
        }
        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryRead(keys[0], out _));
        Assert.True(cache.TryRead(keys[5], out byte[] newest));
        Assert.Equal(new byte[] { 9, 9, 5 }, newest);
    }

    [Fact]
    public void AnUnusableDirectory_CostsACompile_NotAnError()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using ModTempDir dir = new();
        // The "directory" is a file: nothing can be created under it.
        string blocked = dir.Sub("compiled");
        File.WriteAllText(blocked, "not a directory");
        ModCompileCache cache = new(blocked);
        for (int i = 0; i < 2; i++)
        {
            using ModCompiledModule compiled = WasmModSandbox.Compile("rect", Wat(RectMod), Small, cache);
            Assert.False(compiled.FromCache);
        }
        Assert.Equal(0, cache.Count);
    }

    // ---- the session: compiling off the caller's thread --------------------------------------------

    private sealed class AsyncRig : IDisposable
    {
        public readonly ModTempDir Dir = new();
        public readonly CountingHost Host = new();
        public readonly List<string> Log = new();
        public ModClientSession Session;
        public ModOfferPeer Peer;
        public readonly ModOffer Offer;
        public double Now;

        public AsyncRig(byte[] module)
        {
            Offer = ModOffer.FromMemory(ModOfferFixture.Description(), ("client.wasm", module));
            Session = NewSession();
            Peer = new ModOfferPeer(Offer, 0);
        }

        public ModClientSession NewSession() =>
            new(ModOfferFixture.Options() with { LimitCeiling = Small }, new ModCache(Dir.Sub("cache")), new ModConsentStore(), Host, Log.Add)
            {
                CompileOffThread = true,
                CompileCache = new ModCompileCache(Dir.Sub("compiled")),
            };

        /// <summary>One exchange in each direction and one Update, like one rendered frame of the game.</summary>
        public void Step()
        {
            Now += 0.05;
            while (Peer.TryDequeueOutbound(Now, out byte[] frame)) Session.HandleFrame(frame, Now);
            Session.Update(Now);
            if (Session.Offer.State == ModClientState.AwaitingConsent) Session.ResolveConsent(ModConsentDecision.AllowOnce);
            while (Session.TryDequeueOutbound(out byte[] frame)) Peer.HandleFrame(frame, Now);
        }

        public void StepUntil(Func<bool> done)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("the session did not get there");
                Step();
                Thread.Sleep(1);
            }
        }

        public void Dispose() { Session.Dispose(); Peer.Dispose(); Dir.Dispose(); }
    }

    [Fact]
    public void WithCompileOffThread_TheCallThatFinishesTheDownload_DoesNotCompile_AndTheModStartsLater()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using AsyncRig rig = new(SlowToCompileMod());

        rig.StepUntil(() => rig.Session.IsPreparing || rig.Session.Sandbox is not null);
        // The step that verified the last file only STARTED the compile.
        Assert.True(rig.Session.IsPreparing);
        Assert.Null(rig.Session.Sandbox);
        Assert.Equal(ModClientState.ReadyToLoad, rig.Session.Offer.State);
        Assert.NotEqual(ModPeerState.Ready, rig.Peer.State);
        Assert.False(rig.Session.Frame(0.016f, rig.Now));       // nothing to run yet, and no error

        rig.StepUntil(() => rig.Session.Sandbox is not null);
        Assert.False(rig.Session.IsPreparing);
        Assert.Equal(ModClientState.Active, rig.Session.Offer.State);
        rig.Step();
        Assert.Equal(ModPeerState.Ready, rig.Peer.State);       // the server is told only once it really runs
        Assert.True(rig.Session.Frame(0.016f, rig.Now));
        Assert.Equal(1, rig.Host.Rects);
        Assert.False(rig.Session.LastCompileFromCache);
        Assert.Contains(rig.Log, line => line.Contains("off the main thread"));
    }

    [Fact]
    public void TheNextConnection_GetsItsMachineCodeFromTheCache()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using AsyncRig rig = new(Wat(RectMod));
        rig.StepUntil(() => rig.Session.Sandbox is not null);
        Assert.False(rig.Session.LastCompileFromCache);

        // Disconnect, connect again: same download cache, same compile cache, fresh session and peer.
        rig.Session.Dispose();
        rig.Peer.Dispose();
        rig.Session = rig.NewSession();
        rig.Peer = new ModOfferPeer(rig.Offer, rig.Now);
        rig.StepUntil(() => rig.Session.Sandbox is not null);

        Assert.True(rig.Session.LastCompileFromCache);
        Assert.Equal(0, rig.Peer.BytesSent);                    // and nothing was downloaded twice either
        Assert.True(rig.Session.Frame(0.016f, rig.Now));
        Assert.Equal(1, rig.Host.Rects);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("level change")]
    [InlineData("mods switched off")]
    [InlineData("dispose")]
    public void AnOfferThatEndsWhileItsModuleIsCompiling_NeverStartsTheMod(string how)
    {
        if (!WasmModSandbox.IsAvailable) return;
        using AsyncRig rig = new(SlowToCompileMod());
        rig.StepUntil(() => rig.Session.IsPreparing);

        switch (how)
        {
            case "disconnect": rig.Session.Disconnected(); break;
            case "level change": rig.Session.LevelChanged(); break;
            case "mods switched off": rig.Session.SetAllowMods(false); break;
            default: rig.Session.Dispose(); break;
        }
        Assert.False(rig.Session.IsPreparing);

        // Give the abandoned compile time to finish, and keep the session ticking as the game would.
        for (int i = 0; i < 50; i++)
        {
            rig.Session.Update(rig.Now += 0.05);
            Assert.False(rig.Session.Frame(0.016f, rig.Now));
            Thread.Sleep(2);
        }
        Assert.Null(rig.Session.Sandbox);
        Assert.Equal(0, rig.Host.Rects);
        Assert.NotEqual(ModClientState.Active, rig.Session.Offer.State);
    }

    [Fact]
    public void AModuleThatWillNotCompile_IsDeclined_FromTheWorkerPathToo()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using AsyncRig rig = new(ModOfferFixture.Bytes(2000, seed: 7));   // not WebAssembly at all
        rig.StepUntil(() => rig.Session.Offer.State == ModClientState.Declined);
        Assert.Equal(ModDeclineReason.LoadFailed, rig.Session.Offer.DeclineReason);
        Assert.Null(rig.Session.Sandbox);
        rig.Step();
        Assert.Equal(ModPeerState.Declined, rig.Peer.State);
        Assert.Empty(Entries(rig.Dir.Sub("compiled")));         // nothing that failed to compile is cached
    }

    // ---- measurement: what the split and the cache buy, with the real C# guest ---------------------

    [Fact]
    public void Measure_TheRealCSharpGuest_CompileAgainstCache_AndWhatStaysOnTheCallersThread()
    {
        if (!WasmModSandbox.IsAvailable || CSharpGuest() is not { } path) return;
        byte[] wasm = File.ReadAllBytes(path);
        using ModTempDir dir = new();
        ModCompileCache cache = new(dir.Sub("compiled"));

        (double Compile, double Bind, double Init, bool Cached) Run(ModCompileCache? withCache)
        {
            ModCompiledModule compiled = WasmModSandbox.Compile("hello-hud", wasm, ModLimits.Default, withCache);
            bool cached = compiled.FromCache;
            double compileMs = compiled.Milliseconds;
            CountingHost host = new();
            long t = Stopwatch.GetTimestamp();
            using WasmModSandbox sandbox = WasmModSandbox.Load(compiled, host);
            double bindMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            t = Stopwatch.GetTimestamp();
            Assert.True(sandbox.Init());
            double initMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            Assert.True(sandbox.Frame(0.016f));
            Assert.Equal(1, host.Rects);
            return (compileMs, bindMs, initMs, cached);
        }

        Run(null);                                               // warm the runtime itself
        List<string> lines = new();
        for (int i = 0; i < 3; i++)
        {
            (double compile, double bind, double init, bool cached) = Run(null);
            Assert.False(cached);
            lines.Add($"no cache      : compile {compile,7:0.0} ms | bind+instantiate {bind,6:0.0} ms | start-up {init,6:0.0} ms");
        }
        (double firstCompile, _, _, bool firstCached) = Run(cache);
        Assert.False(firstCached);
        lines.Add($"cache, first  : compile {firstCompile,7:0.0} ms (includes writing {new FileInfo(Entries(cache.RootDirectory).Single()).Length / 1024} KiB)");
        double worstCached = 0;
        for (int i = 0; i < 3; i++)
        {
            (double compile, double bind, double init, bool cached) = Run(cache);
            Assert.True(cached);
            worstCached = Math.Max(worstCached, compile);
            lines.Add($"cache, repeat : compile {compile,7:0.0} ms | bind+instantiate {bind,6:0.0} ms | start-up {init,6:0.0} ms");
        }
        foreach (string line in lines) _output.WriteLine(line);
        string? report = Environment.GetEnvironmentVariable("VA_MOD_COMPILE_REPORT");
        if (!string.IsNullOrEmpty(report)) File.WriteAllLines(report, lines);

        // Not a benchmark assertion - machines differ - only that the cache is not slower than the compiler.
        Assert.True(worstCached < firstCompile * 2 + 50, $"reading back took {worstCached:0} ms against {firstCompile:0} ms to compile");
    }
}
