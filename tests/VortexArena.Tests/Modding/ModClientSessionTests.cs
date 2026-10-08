using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VortexArena.Modding;
using Wasmtime;
using Xunit;

namespace VortexArena.Tests.Modding;

/// <summary>
/// The whole path a server-offered mod takes on the client, with a real sandbox at the end of it: a
/// server peer offers a real WebAssembly module, the session downloads, verifies, loads and runs it,
/// and stops it again for every reason it should. Modules are WebAssembly text, assembled by Wasmtime.
///
/// Every test no-ops where the Wasmtime native library is absent, like the other sandbox tests.
/// </summary>
public class ModClientSessionTests
{
    private sealed class RecordingHost : IModHost
    {
        public readonly List<string> Logs = new();
        public readonly List<string> Draws = new();
        public int DirectSends;

        public void Log(ModLogLevel level, string message) => Logs.Add(message);
        public int EntityCount => 0;
        public double Time => 1.0;
        public int ReadState(ModStateKind kind, int index, Span<byte> destination) => -1;
        public bool TryGetCvar(string name, out string value) { value = ""; return false; }
        public int ResolveAsset(ModAssetKind kind, string path) => kind == ModAssetKind.Sound ? 9 : 3;
        public float MeasureText(int fontId, float size, string text) => 0;
        // The session must never let a guest reach the real host's SendToServer: the offer flow owns that channel.
        public bool SendToServer(ReadOnlySpan<byte> payload) { DirectSends++; return true; }
        public void DrawRect(float x, float y, float w, float h, uint rgba) => Draws.Add("rect");
        public void DrawPic(int id, float x, float y, float w, float h, uint rgba) => Draws.Add("pic");
        public void DrawText(int font, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8) => Draws.Add("text");
        public void SetClip(float x, float y, float w, float h) => Draws.Add("clip");
        public void ResetClip() => Draws.Add("unclip");
        public void PlaySound(int id, int channel, float volume, float pitch) => Draws.Add($"sound {id}");
    }

    private static byte[] Wat(string body) => Module.ConvertText($"(module {body})");

    // A small mod. Each frame it draws one rectangle, plays one sound, sends "ping" to the server and
    // logs two characters: the result of the send ('0' or '1') and how many frames this INSTANCE has
    // run (a digit) - which is how the tests see that a level change started a fresh instance.
    // mod_event logs the payload the server sent.
    private const string ChattyMod = """
        (import "vortex_1" "commands" (func $commands (param i32 i32)))
        (import "vortex_1" "send_to_server" (func $send (param i32 i32) (result i32)))
        (import "vortex_1" "log" (func $log (param i32 i32 i32)))
        (import "vortex_1" "asset_id" (func $asset (param i32 i32 i32) (result i32)))
        (memory (export "memory") 1)
        (global $frames (mut i32) (i32.const 0))
        (data (i32.const 0) "\01\00\18\00")
        (data (i32.const 24) "\06\00\14\00")
        (data (i32.const 64) "ping")
        (data (i32.const 80) "boom")
        (func (export "mod_init")
          (i32.store (i32.const 28) (call $asset (i32.const 2) (i32.const 80) (i32.const 4)))
          (f32.store (i32.const 36) (f32.const 1))
          (f32.store (i32.const 40) (f32.const 1)))
        (func (export "mod_frame") (param f32)
          (global.set $frames (i32.add (global.get $frames) (i32.const 1)))
          (call $commands (i32.const 0) (i32.const 44))
          (i32.store8 (i32.const 200) (i32.add (i32.const 48) (call $send (i32.const 64) (i32.const 4))))
          (i32.store8 (i32.const 201) (i32.add (i32.const 48) (global.get $frames)))
          (call $log (i32.const 0) (i32.const 200) (i32.const 2)))
        (func (export "mod_alloc") (param i32) (result i32) (i32.const 4096))
        (func (export "mod_event") (param i32 i32 i32)
          (call $log (i32.const 1) (local.get 1) (local.get 2)))
        """;

    private const string Memory = "(memory (export \"memory\") 1)";

    private static readonly ModLimits FastCeiling = ModLimits.Default with { FrameBudgetMs = 20, InitBudgetMs = 500, MaxMemoryBytes = 4 * ModAbi.PageSize };

    /// <summary>A server peer and a client session joined by an in-memory connection.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly ModTempDir Dir = new();
        public readonly RecordingHost Host = new();
        public readonly ModClientSession Session;
        public readonly ModOfferPeer Peer;
        public readonly ModOffer Offer;
        public readonly List<string> Lifecycle = new();
        public double Now;

        public Rig(byte[] module, bool required = false, string[]? capabilities = null, ModClientOptions? options = null, ModConsentStore? consent = null)
        {
            Offer = ModOffer.FromMemory(ModOfferFixture.Description(required, capabilities ?? Array.Empty<string>()), ("client.wasm", module));
            Session = new ModClientSession(options ?? ModOfferFixture.Options() with { LimitCeiling = FastCeiling },
                new ModCache(Dir.Sub("cache")), consent ?? new ModConsentStore(), Host, Lifecycle.Add);
            Session.Loaded += plan => Lifecycle.Add("LOADED " + plan.Manifest.ModId);
            Session.Unloaded += plan => Lifecycle.Add("UNLOADED " + plan.Manifest.ModId);
            Peer = new ModOfferPeer(Offer, 0);
        }

        public void Pump()
        {
            for (int round = 0; round < 10_000; round++)
            {
                bool moved = false;
                while (Peer.TryDequeueOutbound(Now, out byte[] frame)) { Session.HandleFrame(frame, Now); moved = true; }
                Session.Update(Now);
                while (Session.TryDequeueOutbound(out byte[] frame)) { Peer.HandleFrame(frame, Now); moved = true; }
                Peer.Update(Now);
                if (moved) continue;
                if (Peer.State != ModPeerState.Sending) return;
                Now += 0.05;
            }
            throw new InvalidOperationException("the exchange did not settle");
        }

        /// <summary>Offer, say yes, download, load.</summary>
        public void Join(ModConsentDecision decision = ModConsentDecision.AllowOnce)
        {
            Pump();
            Assert.Equal(ModClientState.AwaitingConsent, Session.Offer.State);
            Session.ResolveConsent(decision);
            Pump();
        }

        public bool Frame()
        {
            Now += 0.016;
            Session.Update(Now);
            bool ran = Session.Frame(0.016f, Now);
            Pump();
            return ran;
        }

        public void Dispose() { Session.Dispose(); Peer.Dispose(); Dir.Dispose(); }
    }

    [Fact]
    public void AServerOfferedMod_Downloads_Loads_Draws_AndTalksToItsServerHalf()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(Wat(ChattyMod), capabilities: new[] { ModCapabilities.Net, ModCapabilities.Sound });

        rig.Pump();
        Assert.Null(rig.Session.Sandbox);                      // nothing runs before the player answers
        Assert.Empty(rig.Dir.Files("cache"));
        rig.Session.ResolveConsent(ModConsentDecision.AllowOnce);
        rig.Pump();

        Assert.Equal(ModClientState.Active, rig.Session.Offer.State);
        Assert.Equal(ModSandboxState.Running, rig.Session.Sandbox!.State);
        Assert.Equal(ModPeerState.Ready, rig.Peer.State);
        Assert.Contains("LOADED overkill", rig.Lifecycle);

        Assert.True(rig.Frame());
        Assert.Equal(new[] { "rect", "sound 9" }, rig.Host.Draws);
        Assert.Equal("11", rig.Host.Logs.Last());              // the send was accepted; first frame of this instance
        Assert.Equal(0, rig.Host.DirectSends);
        Assert.True(rig.Peer.TryDequeueMessage(out byte[] ping));
        Assert.Equal("ping", Encoding.UTF8.GetString(ping));

        // And the other way: the server half's message arrives in mod_event on the next frame.
        Assert.True(rig.Peer.TrySendToClient(7, Encoding.UTF8.GetBytes("hello mod")));
        rig.Pump();
        Assert.True(rig.Frame());
        Assert.Contains("hello mod", rig.Host.Logs);
        Assert.Equal("12", rig.Host.Logs.Last());
    }

    [Fact]
    public void WhatTheManifestDidNotDeclare_TheModCannotDo()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(Wat(ChattyMod));                   // no capabilities at all
        rig.Join();
        Assert.True(rig.Frame());

        Assert.Equal(new[] { "rect" }, rig.Host.Draws);        // the sound was dropped
        Assert.Equal("01", rig.Host.Logs.Last());              // send_to_server answered 0
        Assert.Equal(0, rig.Host.DirectSends);
        Assert.False(rig.Peer.TryDequeueMessage(out _));
        Assert.False(rig.Peer.TrySendToClient(7, new byte[1]));
        Assert.False(rig.Peer.ShouldDisconnect);               // an honest client never put a message on the wire
    }

    [Fact]
    public void AChattyMod_IsRateLimited_NotDisabled()
    {
        if (!WasmModSandbox.IsAvailable) return;
        ModClientOptions options = ModOfferFixture.Options() with { LimitCeiling = FastCeiling, ToServerPerSecond = 1, ToServerBurst = 3 };
        using Rig rig = new(Wat(ChattyMod), capabilities: new[] { ModCapabilities.Net }, options: options);
        rig.Join();

        int received = 0;
        for (int i = 0; i < 8; i++)
        {
            Assert.True(rig.Frame());
            while (rig.Peer.TryDequeueMessage(out _)) received++;
        }
        Assert.Equal(3, received);                             // the burst; eight frames is an eighth of a second
        Assert.Equal("08", rig.Host.Logs.Last());              // the guest was told 0 and kept running
        Assert.Equal(ModSandboxState.Running, rig.Session.Sandbox!.State);
        Assert.Equal(0, rig.Peer.DroppedMessages);             // the client held back; the server had nothing to drop
    }

    public static TheoryData<string, string, string> Faults() => new()
    {
        { "abort", $"{Memory} (func (export \"mod_frame\") (param f32) unreachable)", "unreachable" },
        { "endless loop", $"{Memory} (func (export \"mod_frame\") (param f32) (loop $l (br $l)))", "time budget" },
        { "out-of-bounds read", $"{Memory} (func (export \"mod_frame\") (param f32) (drop (i32.load (i32.const 99999999))))", "out-of-bounds" },
        { "bad pointer to an import", $"(import \"vortex_1\" \"commands\" (func $c (param i32 i32))) {Memory} (func (export \"mod_frame\") (param f32) (call $c (i32.const 65000) (i32.const 4096)))", "ABI violation" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void AModThatFaults_IsUnloaded_TheServerIsTold_AndThePlayerStays(string what, string module, string expectedReason)
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(Wat(module));
        rig.Join();
        Assert.Equal(ModPeerState.Ready, rig.Peer.State);

        Assert.False(rig.Frame(), what);
        Assert.Null(rig.Session.Sandbox);
        Assert.Equal(ModClientState.Declined, rig.Session.Offer.State);
        Assert.Equal(ModDeclineReason.Faulted, rig.Session.Offer.DeclineReason);
        Assert.Contains(expectedReason, rig.Session.Offer.DeclineText);
        Assert.Contains("UNLOADED overkill", rig.Lifecycle);

        Assert.Equal(ModPeerState.Declined, rig.Peer.State);
        Assert.Equal(ModDeclineReason.Faulted, rig.Peer.DeclineReason);
        Assert.True(rig.Peer.MayPlay);
        Assert.False(rig.Peer.ShouldDisconnect);

        // Further frames do nothing and throw nothing.
        Assert.False(rig.Frame());
        Assert.False(rig.Frame());
    }

    [Fact]
    public void ARequiredModThatFaults_CostsThePlayerTheServer_NotTheGame()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(Wat($"{Memory} (func (export \"mod_frame\") (param f32) unreachable)"), required: true);
        rig.Join();
        Assert.True(rig.Peer.MayPlay);
        Assert.False(rig.Frame());
        Assert.True(rig.Peer.ShouldDisconnect);
        Assert.Contains("requires the mod", rig.Peer.DisconnectReason);
        Assert.Contains("Faulted", rig.Peer.DisconnectReason);
    }

    public static TheoryData<string, byte[]> Unloadable()
    {
        TheoryData<string, byte[]> data = new() { { "not WebAssembly at all", Encoding.UTF8.GetBytes("MZ this is not a module, however well it hashes") } };
        if (!WasmModSandbox.IsAvailable) return data;
        data.Add("asks for a filesystem", Wat($"(import \"env\" \"fopen\" (func (param i32) (result i32))) {Memory} (func (export \"mod_frame\") (param f32))"));
        data.Add("no frame function", Wat(Memory));
        data.Add("hangs while starting", Wat($"{Memory} (func (export \"mod_init\") (loop $l (br $l))) (func (export \"mod_frame\") (param f32))"));
        data.Add("wants more memory than the client allows", Wat("(memory (export \"memory\") 64) (func (export \"mod_frame\") (param f32))"));
        return data;
    }

    [Theory]
    [MemberData(nameof(Unloadable))]
    public void AModuleThatCannotBeLoaded_IsDeclined_AndNeverRuns(string what, byte[] module)
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(module);
        rig.Join();

        Assert.True(rig.Session.Sandbox is null, what);
        Assert.Equal(ModClientState.Declined, rig.Session.Offer.State);
        Assert.Equal(ModDeclineReason.LoadFailed, rig.Session.Offer.DeclineReason);
        Assert.Equal(ModPeerState.Declined, rig.Peer.State);
        Assert.Equal(ModDeclineReason.LoadFailed, rig.Peer.DeclineReason);
        Assert.True(rig.Peer.MayPlay);
        Assert.DoesNotContain(rig.Lifecycle, l => l.StartsWith("LOADED", StringComparison.Ordinal));
        Assert.False(rig.Frame());
    }

    [Fact]
    public void ALevelChange_StopsTheMod_AndTheNextOfferStartsAFreshInstance()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(Wat(ChattyMod), capabilities: new[] { ModCapabilities.Net });
        rig.Join();
        rig.Frame(); rig.Frame(); rig.Frame();
        Assert.Equal("13", rig.Host.Logs.Last());
        WasmModSandbox first = rig.Session.Sandbox!;

        rig.Session.LevelChanged();
        Assert.Null(rig.Session.Sandbox);
        Assert.Equal(ModSandboxState.Disabled, first.State);
        Assert.Equal(1, rig.Lifecycle.Count(l => l == "UNLOADED overkill"));
        Assert.False(rig.Frame());                             // nothing runs between levels

        rig.Peer.Reoffer(rig.Now);
        long sentBefore = rig.Peer.BytesSent;
        rig.Pump();                                            // no prompt, no download: allowed for this connection, cached
        Assert.Equal(sentBefore, rig.Peer.BytesSent);
        Assert.NotSame(first, rig.Session.Sandbox);
        Assert.Equal(ModPeerState.Ready, rig.Peer.State);
        Assert.True(rig.Frame());
        Assert.Equal("11", rig.Host.Logs.Last());              // the frame counter started again: no state survived
        Assert.Equal(2, rig.Lifecycle.Count(l => l == "LOADED overkill"));
    }

    [Fact]
    public void Disconnecting_Or_SwitchingModsOff_Or_AWithdrawnOffer_UnloadsTheMod()
    {
        if (!WasmModSandbox.IsAvailable) return;

        using (Rig rig = new(Wat(ChattyMod)))
        {
            rig.Join();
            WasmModSandbox sandbox = rig.Session.Sandbox!;
            rig.Session.Disconnected();
            Assert.Null(rig.Session.Sandbox);
            Assert.Equal(ModSandboxState.Disabled, sandbox.State);
            Assert.Equal(ModClientState.Idle, rig.Session.Offer.State);
            Assert.False(rig.Session.TryDequeueOutbound(out _));   // nothing is sent to a server that is gone
            Assert.Contains("UNLOADED overkill", rig.Lifecycle);
        }

        using (Rig rig = new(Wat(ChattyMod)))
        {
            rig.Join();
            rig.Session.SetAllowMods(false);
            Assert.Null(rig.Session.Sandbox);
            rig.Pump();
            Assert.Equal(ModDeclineReason.ModsDisabled, rig.Peer.DeclineReason);
            Assert.False(rig.Frame());
        }

        using (Rig rig = new(Wat(ChattyMod)))
        {
            // The player's own "stop this": gone now, and not back on the next level either.
            rig.Join();
            rig.Session.StopForConnection();
            Assert.Null(rig.Session.Sandbox);
            rig.Pump();
            Assert.Equal(ModDeclineReason.Cancelled, rig.Peer.DeclineReason);
            rig.Peer.Reoffer(rig.Now += 10);
            rig.Pump();
            Assert.Null(rig.Session.Sandbox);
            Assert.Equal(ModDeclineReason.ConsentDenied, rig.Peer.DeclineReason);
        }

        using (Rig rig = new(Wat(ChattyMod)))
        {
            rig.Join();
            Assert.True(ModWire.TryParse(ModWire.Offer(1, rig.Offer.ManifestJson), out _));
            // The server withdraws the mod (sequence 1 is the peer's first offer).
            rig.Session.HandleFrame(ModWire.Abort(1, "mod removed"), rig.Now);
            Assert.Null(rig.Session.Sandbox);
            Assert.Equal(ModClientState.Idle, rig.Session.Offer.State);
        }

        using (Rig rig = new(Wat(ChattyMod)))
        {
            rig.Join();
            WasmModSandbox sandbox = rig.Session.Sandbox!;
            rig.Session.Dispose();
            Assert.Equal(ModSandboxState.Disabled, sandbox.State);
        }
    }

    [Fact]
    public void ACachedModuleThatWasTamperedWith_IsNotRun_AndIsRemoved()
    {
        if (!WasmModSandbox.IsAvailable) return;
        byte[] module = Wat(ChattyMod);
        ModConsentStore consent = new();
        string cacheFile;
        using (Rig first = new(module, consent: consent))
        {
            first.Join(ModConsentDecision.AllowOnThisServer);
            Assert.NotNull(first.Session.Sandbox);
            cacheFile = first.Session.LoadedPlan!.ModulePath!;
        }

        // Same size, different bytes, written straight into the cache behind the client's back.
        using Rig rig = new(module, consent: consent);
        string target = Path.Combine(rig.Dir.Sub("cache"), Path.GetFileName(Path.GetDirectoryName(cacheFile))!, Path.GetFileName(cacheFile));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] tampered = (byte[])module.Clone();
        tampered[^1] ^= 0x55;
        File.WriteAllBytes(target, tampered);

        rig.Pump();   // consent is remembered and the file "is cached", so the session goes straight to loading
        Assert.Null(rig.Session.Sandbox);
        Assert.Equal(ModDeclineReason.LoadFailed, rig.Session.Offer.DeclineReason);
        Assert.Contains("SHA-256", rig.Session.Offer.DeclineText);
        Assert.False(File.Exists(target));

        // With the bad copy gone, the next offer downloads the real one and it runs.
        rig.Peer.Reoffer(rig.Now += 10);
        rig.Pump();
        Assert.NotNull(rig.Session.Sandbox);
        Assert.Equal(module, File.ReadAllBytes(target));
    }

    [Fact]
    public void AnAssetsOnlyMod_LoadsWithoutASandbox()
    {
        ModOffer offer = ModOffer.FromMemory(ModOfferFixture.Description(), null, new[] { ("overkill.pk3", ModOfferFixture.Bytes(5000, 8)) });
        using ModTempDir dir = new();
        List<string> lifecycle = new();
        using ModClientSession session = new(ModOfferFixture.Options(), new ModCache(dir.Sub("cache")), new ModConsentStore(), new RecordingHost());
        session.Loaded += plan => lifecycle.Add($"LOADED {plan.Packs.Count} pack(s)");
        using ModOfferPeer peer = new(offer, 0);

        for (int round = 0; round < 200; round++)
        {
            while (peer.TryDequeueOutbound(round, out byte[] frame)) session.HandleFrame(frame, round);
            session.Update(round);
            if (session.Offer.State == ModClientState.AwaitingConsent) session.ResolveConsent(ModConsentDecision.AllowOnce);
            while (session.TryDequeueOutbound(out byte[] frame)) peer.HandleFrame(frame, round);
        }
        Assert.Equal(ModPeerState.Ready, peer.State);
        Assert.Null(session.Sandbox);
        Assert.Equal(new[] { "LOADED 1 pack(s)" }, lifecycle);
        Assert.False(session.Frame(0.016f, 300));
    }

    [Fact]
    public void TheRealCSharpGuest_TravelsTheWholePath_FromServerFileToDrawCommands()
    {
        // Needs the published hello-hud template (modding-sdk/README.md), like CSharpGuestTests.
        string template = Path.Combine(TestPaths.RepoRoot, "modding-sdk", "csharp", "templates", "hello-hud");
        string? guest = Environment.GetEnvironmentVariable("VA_CSHARP_GUEST_WASM") is { Length: > 0 } fromEnv && File.Exists(fromEnv) ? fromEnv
            : Directory.Exists(template) ? Directory.EnumerateFiles(template, "hello-hud.wasm", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (guest is null || !WasmModSandbox.IsAvailable) return;

        using ModTempDir dir = new();
        ModOffer offer = ModOffer.FromFiles(ModOfferFixture.Description() with { ModId = "hello-hud" }, guest);
        RecordingHost host = new();
        using ModClientSession session = new(ModOfferFixture.Options(), new ModCache(dir.Sub("cache")), new ModConsentStore(), host);
        using ModOfferPeer peer = new(offer, 0);

        // About 2 MB at the default 512 KiB a second: a little over four seconds of simulated time.
        double now = 0;
        for (; now < 60 && peer.State != ModPeerState.Ready && session.Offer.State != ModClientState.Declined; now += 0.05)
        {
            while (peer.TryDequeueOutbound(now, out byte[] frame)) session.HandleFrame(frame, now);
            session.Update(now);
            if (session.Offer.State == ModClientState.AwaitingConsent) session.ResolveConsent(ModConsentDecision.AllowOnce);
            while (session.TryDequeueOutbound(out byte[] frame)) peer.HandleFrame(frame, now);
        }

        Assert.Equal(ModPeerState.Ready, peer.State);
        long size = new FileInfo(guest).Length;
        ModPeerOptions pacing = new();
        // Paced, not instant: no faster than the upload limit allows once the burst is spent.
        Assert.True(now >= (size - pacing.UploadBurstBytes) / pacing.UploadBytesPerSecond - 0.1, $"{size} bytes arrived in {now:0.00} s");
        Assert.Equal(size, peer.BytesSent);
        Assert.Equal(ModSandboxState.Running, session.Sandbox!.State);
        for (int i = 0; i < 10; i++) Assert.True(session.Frame(0.016f, now += 0.016));
        Assert.Contains("rect", host.Draws);
        Assert.Contains("text", host.Draws);
        Assert.Equal(0, host.DirectSends);
    }

    [Fact]
    public void WithModsOff_NothingIsDownloadedOrRun_WhateverTheServerSends()
    {
        if (!WasmModSandbox.IsAvailable) return;
        using Rig rig = new(Wat(ChattyMod), capabilities: new[] { ModCapabilities.Net },
            options: ModOfferFixture.Options(allowMods: false) with { LimitCeiling = FastCeiling });
        rig.Pump();
        Assert.Equal(ModDeclineReason.ModsDisabled, rig.Session.Offer.DeclineReason);

        // A server that ignores the refusal and pushes the file and messages anyway.
        byte[] module = Wat(ChattyMod);
        rig.Session.HandleFrame(ModWire.Chunk(1, 0, 0, module), rig.Now);
        rig.Session.HandleFrame(ModWire.ToClient(1, 1, new byte[8]), rig.Now);
        rig.Session.Update(rig.Now);
        Assert.Null(rig.Session.Sandbox);
        Assert.False(rig.Frame());
        Assert.Empty(rig.Dir.Files("cache"));
        Assert.Empty(rig.Host.Draws);
        Assert.DoesNotContain(rig.Lifecycle, l => l.StartsWith("LOADED", StringComparison.Ordinal));
    }
}
