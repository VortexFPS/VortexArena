using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using VortexArena.Modding;
using Wasmtime;
using Xunit;

namespace VortexArena.Tests.Modding;

/// <summary>
/// The sandbox's one promise: a guest can waste its own budget and nothing else. Each hostile module
/// here must end with the sandbox Disabled and a reason, the test process alive, and no exception
/// escaping to the caller. Modules are written as WebAssembly text so the suite needs no guest
/// toolchain; Wasmtime assembles them.
///
/// Every test no-ops where the Wasmtime native library is absent (a RID the package ships no binary
/// for), matching how the content-dependent tests behave without content.
/// </summary>
public class ModSandboxTests
{
    private sealed class RecordingHost : IModHost
    {
        public readonly List<string> Logs = new();
        public readonly List<string> Draws = new();
        public readonly List<byte[]> Sent = new();
        public readonly Dictionary<string, string> Cvars = new() { ["hud_fontsize"] = "11" };

        public void Log(ModLogLevel level, string message) => Logs.Add($"{level}:{message}");
        public int EntityCount => 3;
        public double Time => 12.5;

        public int ReadState(ModStateKind kind, int index, Span<byte> destination)
        {
            if (kind != ModStateKind.Screen) return -1;
            ModScreenState screen = new() { Width = 1920, Height = 1080, VirtualWidth = 800, VirtualHeight = 600 };
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref screen, 1));
            int n = Math.Min(bytes.Length, destination.Length);
            bytes[..n].CopyTo(destination);
            return n;
        }

        public bool TryGetCvar(string name, out string value) => Cvars.TryGetValue(name, out value!);
        public int ResolveAsset(ModAssetKind kind, string path) => path == "gfx/hud/panel" ? 7 : 0;
        public float MeasureText(int fontId, float size, string text) => text.Length * size;
        public bool SendToServer(ReadOnlySpan<byte> payload) { Sent.Add(payload.ToArray()); return true; }

        public void DrawRect(float x, float y, float w, float h, uint rgba) => Draws.Add($"rect {x} {y} {w} {h} {rgba:X8}");
        public void DrawPic(int id, float x, float y, float w, float h, uint rgba) => Draws.Add($"pic {id} {x} {y} {w} {h}");
        public void DrawText(int font, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8) => Draws.Add($"text {x} {y} {Encoding.UTF8.GetString(utf8)}");
        public void SetClip(float x, float y, float w, float h) => Draws.Add("clip");
        public void ResetClip() => Draws.Add("unclip");
        public void PlaySound(int id, int channel, float volume, float pitch) => Draws.Add($"sound {id}");
    }

    private static readonly ModLimits FastLimits = ModLimits.Default with { FrameBudgetMs = 20, InitBudgetMs = 200, MaxMemoryBytes = 4 * ModAbi.PageSize };

    private static byte[] Wat(string body) =>
        Module.ConvertText($"(module {body})");

    private static WasmModSandbox Load(string body, RecordingHost host, ModLimits? limits = null) =>
        WasmModSandbox.Load("test", Wat(body), host, limits ?? FastLimits);

    private const string Memory = "(memory (export \"memory\") 1)";
    private const string EmptyFrame = "(func (export \"mod_frame\") (param f32))";

    [Fact]
    public void RuntimeIsAvailable_OnEveryPlatformThePackageShipsABinaryFor()
    {
        // Every other test here returns early when the runtime is unavailable. Without this one, a
        // packaging mistake that loses the native library would turn the whole class green and empty.
        bool shipped = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64
            && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        if (shipped) Assert.True(WasmModSandbox.IsAvailable, WasmModSandbox.UnavailableReason);
        else Assert.NotNull(WasmModSandbox.UnavailableReason);
    }

    // ---- the well-behaved path ---------------------------------------------------------------------

    [Fact]
    public void WellBehavedGuest_DrawsThroughTheCommandBuffer()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();

        // One DrawRect record laid out by a data segment at address 64: opcode 1, size 24, then
        // x=10 y=20 w=30 h=40 as float32 and the colour.
        using WasmModSandbox sandbox = Load($$"""
            (import "vortex_1" "commands" (func $commands (param i32 i32)))
            (import "vortex_1" "log" (func $log (param i32 i32 i32)))
            {{Memory}}
            (data (i32.const 0) "ready")
            (data (i32.const 64) "\01\00\18\00\00\00\20\41\00\00\a0\41\00\00\f0\41\00\00\20\42\ff\80\40\20")
            (func (export "mod_init") (call $log (i32.const 0) (i32.const 0) (i32.const 5)))
            (func (export "mod_frame") (param f32) (call $commands (i32.const 64) (i32.const 24)))
            """, host);

        Assert.Equal(ModSandboxState.Loaded, sandbox.State);
        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0.016f));
        Assert.True(sandbox.Frame(0.016f));

        Assert.Equal(new[] { "Info:ready" }, host.Logs);
        Assert.Equal(new[] { "rect 10 20 30 40 204080FF", "rect 10 20 30 40 204080FF" }, host.Draws);
        Assert.Equal(ModSandboxState.Running, sandbox.State);
    }

    [Fact]
    public void Guest_ReadsStateCvarsAssetsAndTime()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();

        // mod_frame stores what each import returned at fixed addresses; mod_event sends 16 bytes of
        // that back to the "server", which is how the test reads guest memory without a debug import.
        using WasmModSandbox sandbox = Load($$"""
            (import "vortex_1" "state_read" (func $state (param i32 i32 i32 i32) (result i32)))
            (import "vortex_1" "cvar_get" (func $cvar (param i32 i32 i32 i32) (result i32)))
            (import "vortex_1" "asset_id" (func $asset (param i32 i32 i32) (result i32)))
            (import "vortex_1" "entity_count" (func $count (result i32)))
            (import "vortex_1" "time_now" (func $time (result f64)))
            (import "vortex_1" "send_to_server" (func $send (param i32 i32) (result i32)))
            {{Memory}}
            (data (i32.const 0) "hud_fontsize")
            (data (i32.const 16) "gfx/hud/panel")
            (data (i32.const 32) "rcon_password")
            (func (export "mod_frame") (param f32)
              (i32.store (i32.const 256) (call $state (i32.const 1) (i32.const 0) (i32.const 512) (i32.const 16)))
              (i32.store (i32.const 260) (call $cvar (i32.const 0) (i32.const 12) (i32.const 600) (i32.const 8)))
              (i32.store (i32.const 264) (call $asset (i32.const 1) (i32.const 16) (i32.const 13)))
              (i32.store (i32.const 268) (i32.add (call $count) (i32.trunc_f64_s (call $time))))
              (i32.store (i32.const 272) (call $cvar (i32.const 32) (i32.const 13) (i32.const 600) (i32.const 8)))
              (drop (call $send (i32.const 256) (i32.const 20)))
              (drop (call $send (i32.const 512) (i32.const 16))))
            """, host);

        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0f), sandbox.DisabledReason);

        ReadOnlySpan<int> results = MemoryMarshal.Cast<byte, int>(host.Sent[0]);
        Assert.Equal(16, results[0]);          // a full ModScreenState
        Assert.Equal(2, results[1]);           // "11"
        Assert.Equal(7, results[2]);           // the asset id
        Assert.Equal(3 + 12, results[3]);      // entity count + truncated time
        Assert.Equal(-1, results[4]);          // a cvar that is not on the allow-list
        ReadOnlySpan<float> screen = MemoryMarshal.Cast<byte, float>(host.Sent[1]);
        Assert.Equal(new float[] { 1920, 1080, 800, 600 }, screen.ToArray());
    }

    [Fact]
    public void Event_CopiesThePayloadIntoGuestAllocatedMemory()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        using WasmModSandbox sandbox = Load($$"""
            (import "vortex_1" "send_to_server" (func $send (param i32 i32) (result i32)))
            {{Memory}} {{EmptyFrame}}
            (func (export "mod_alloc") (param i32) (result i32) (i32.const 1024))
            (func (export "mod_event") (param $id i32) (param $ptr i32) (param $len i32)
              (drop (call $send (local.get $ptr) (local.get $len))))
            """, host);

        Assert.True(sandbox.Init());
        Assert.True(sandbox.Event(5, "hello"u8));
        Assert.Equal("hello", Encoding.UTF8.GetString(host.Sent[0]));
    }

    // ---- refused at load, before any guest code runs -----------------------------------------------

    [Theory]
    [InlineData("(import \"env\" \"system\" (func (param i32)))", "only 'vortex_1' is available")]
    [InlineData("(import \"vortex_1\" \"open_file\" (func (param i32 i32) (result i32)))", "does not provide")]
    [InlineData("(import \"vortex_1\" \"commands\" (func (param i32)))", "the ABI defines (i32,i32)")]
    [InlineData("(import \"vortex_1\" \"memory\" (memory 1))", "not a function")]
    public void Load_RefusesImportsOutsideTheAbi(string import, string expectedReason)
    {
        if (!WasmModSandbox.IsAvailable) return;
        string memory = import.Contains("(memory") ? "(export \"memory\" (memory 0))" : Memory;
        ModLoadException e = Assert.Throws<ModLoadException>(() => Load($"{import} {memory} {EmptyFrame}", new RecordingHost()));
        Assert.Contains(expectedReason, e.Message);
    }

    [Fact]
    public void Load_RefusesMissingOrMistypedExports()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        Assert.Contains("does not export its memory", Assert.Throws<ModLoadException>(() => Load($"(memory 1) {EmptyFrame}", host)).Message);
        Assert.Contains("does not export 'mod_frame'", Assert.Throws<ModLoadException>(() => Load(Memory, host)).Message);
        Assert.Contains("the ABI requires (f32)", Assert.Throws<ModLoadException>(() => Load($"{Memory} (func (export \"mod_frame\") (param i32))", host)).Message);
    }

    [Fact]
    public void Load_RefusesGarbageOversizedAndOverBudgetModules()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        Assert.Contains("not a valid WebAssembly module", Assert.Throws<ModLoadException>(() =>
            WasmModSandbox.Load("junk", new byte[] { 0, 0x61, 0x73, 0x6D, 9, 9, 9, 9, 1, 2, 3 }, host, FastLimits)).Message);
        Assert.Contains("the limit is", Assert.Throws<ModLoadException>(() =>
            WasmModSandbox.Load("big", new byte[4096], host, FastLimits with { MaxModuleBytes = 1024 })).Message);
        // Starts with 16 pages; the limit under test is 4.
        Assert.Contains("bytes of memory", Assert.Throws<ModLoadException>(() => Load($"(memory (export \"memory\") 16) {EmptyFrame}", host)).Message);
    }

    // ---- contained at run time ---------------------------------------------------------------------

    public static IEnumerable<object[]> HostileFrames() => new[]
    {
        new object[] { "infinite loop", "(func (export \"mod_frame\") (param f32) (loop $l (br $l)))", "time budget" },
        new object[] { "unbounded recursion", "(func $f (export \"mod_frame\") (param f32) (call $f (local.get 0)))", "stack overflow" },
        new object[] { "out-of-bounds load", "(func (export \"mod_frame\") (param f32) (drop (i32.load (i32.const 0x7ffffff0))))", "out-of-bounds" },
        new object[] { "deliberate abort", "(func (export \"mod_frame\") (param f32) (unreachable))", "unreachable" },
        new object[] { "division by zero", "(func (export \"mod_frame\") (param f32) (drop (i32.div_s (i32.const 1) (i32.const 0))))", "trap" },
        new object[] { "pointer past the end of memory",
            "(import \"vortex_1\" \"commands\" (func $c (param i32 i32))) (func (export \"mod_frame\") (param f32) (call $c (i32.const 65530) (i32.const 64)))",
            "outside guest memory" },
        new object[] { "negative length",
            "(import \"vortex_1\" \"log\" (func $l (param i32 i32 i32))) (func (export \"mod_frame\") (param f32) (call $l (i32.const 0) (i32.const 0) (i32.const -1)))",
            "negative pointer or length" },
        new object[] { "pointer that wraps when length is added",
            "(import \"vortex_1\" \"state_read\" (func $s (param i32 i32 i32 i32) (result i32))) (func (export \"mod_frame\") (param f32) (drop (call $s (i32.const 1) (i32.const 0) (i32.const 0x7fffffff) (i32.const 16))))",
            "outside guest memory" },
        new object[] { "oversized string",
            "(import \"vortex_1\" \"asset_id\" (func $a (param i32 i32 i32) (result i32))) (func (export \"mod_frame\") (param f32) (drop (call $a (i32.const 1) (i32.const 0) (i32.const 60000))))",
            "exceeds the" },
        new object[] { "malformed command buffer",
            "(import \"vortex_1\" \"commands\" (func $c (param i32 i32))) (data (i32.const 0) \"\\63\\00\\04\\00\") (func (export \"mod_frame\") (param f32) (call $c (i32.const 0) (i32.const 4)))",
            "malformed command buffer" },
    };

    [Theory]
    [MemberData(nameof(HostileFrames))]
    public void HostileGuest_IsDisabledAndTheHostSurvives(string what, string body, string expectedReason)
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        using WasmModSandbox sandbox = Load($"{body} {Memory}", host);

        Assert.True(sandbox.Init());
        Assert.False(sandbox.Frame(0.016f));

        Assert.Equal(ModSandboxState.Disabled, sandbox.State);
        Assert.True(sandbox.DisabledReason!.Contains(expectedReason, StringComparison.OrdinalIgnoreCase), $"{what}: reason was '{sandbox.DisabledReason}'");
        // Disabled is terminal and quiet: no further guest code runs, nothing throws.
        Assert.False(sandbox.Frame(0.016f));
        Assert.False(sandbox.Event(1, "x"u8));
        sandbox.Shutdown();
        Assert.Empty(host.Draws);
    }

    [Fact]
    public void MemoryBomb_FailsInsideTheGuestInsteadOfGrowing()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        // Asks for 1000 more pages against a 4-page limit and reports what memory.grow said.
        using WasmModSandbox sandbox = Load($$"""
            (import "vortex_1" "send_to_server" (func $send (param i32 i32) (result i32)))
            {{Memory}}
            (func (export "mod_frame") (param f32)
              (i32.store (i32.const 0) (memory.grow (i32.const 1000)))
              (drop (call $send (i32.const 0) (i32.const 4))))
            """, host);

        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0f));
        Assert.Equal(-1, BitConverter.ToInt32(host.Sent[0]));
        Assert.Equal(ModAbi.PageSize, sandbox.MemoryBytes);
    }

    [Fact]
    public void HangingStartFunction_IsRefusedAtLoad()
    {
        if (!WasmModSandbox.IsAvailable) return;
        ModLoadException e = Assert.Throws<ModLoadException>(() =>
            Load($"{Memory} {EmptyFrame} (func $spin (loop $l (br $l))) (start $spin)", new RecordingHost()));
        Assert.Contains("failed to instantiate", e.Message);
    }

    [Fact]
    public void LogFlood_IsCappedPerCall()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        using WasmModSandbox sandbox = Load($$"""
            (import "vortex_1" "log" (func $log (param i32 i32 i32)))
            {{Memory}} (data (i32.const 0) "spam")
            (func (export "mod_frame") (param f32) (local $i i32)
              (loop $l
                (call $log (i32.const 0) (i32.const 0) (i32.const 4))
                (br_if $l (i32.lt_u (local.tee $i (i32.add (local.get $i) (i32.const 1))) (i32.const 1000)))))
            """, host, FastLimits with { MaxLogLinesPerCall = 5 });

        Assert.True(sandbox.Init());
        Assert.True(sandbox.Frame(0f));
        Assert.Equal(5, host.Logs.Count);
    }

    // ---- WASI: answered, never granted -------------------------------------------------------------

    [Fact]
    public void WasiImports_AreStubbed_NoFilesystemNoEnvironmentNoExit()
    {
        if (!WasmModSandbox.IsAvailable) return;
        RecordingHost host = new();
        // What a language runtime does at start-up: look for pre-opened directories, read the
        // environment, try to open a file, then print. Results are reported back through send.
        using WasmModSandbox sandbox = Load($$"""
            (import "wasi_snapshot_preview1" "fd_prestat_get" (func $prestat (param i32 i32) (result i32)))
            (import "wasi_snapshot_preview1" "environ_sizes_get" (func $envsizes (param i32 i32) (result i32)))
            (import "wasi_snapshot_preview1" "path_open" (func $open (param i32 i32 i32 i32 i32 i64 i64 i32 i32) (result i32)))
            (import "wasi_snapshot_preview1" "fd_write" (func $write (param i32 i32 i32 i32) (result i32)))
            (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
            (import "vortex_1" "send_to_server" (func $send (param i32 i32) (result i32)))
            {{Memory}}
            (data (i32.const 100) "from wasi\n")
            (data (i32.const 200) "\64\00\00\00\0a\00\00\00")
            (func (export "mod_init")
              (i32.store (i32.const 0) (call $prestat (i32.const 3) (i32.const 300)))
              (i32.store (i32.const 320) (i32.const 77)) (i32.store (i32.const 324) (i32.const 77))
              (i32.store (i32.const 4) (call $envsizes (i32.const 320) (i32.const 324)))
              (i32.store (i32.const 8) (i32.load (i32.const 320)))
              (i32.store (i32.const 12) (call $open (i32.const 3) (i32.const 0) (i32.const 100) (i32.const 4) (i32.const 0) (i64.const 0) (i64.const 0) (i32.const 0) (i32.const 340)))
              (i32.store (i32.const 16) (call $write (i32.const 1) (i32.const 200) (i32.const 1) (i32.const 360)))
              (i32.store (i32.const 20) (call $write (i32.const 5) (i32.const 200) (i32.const 1) (i32.const 360)))
              (drop (call $send (i32.const 0) (i32.const 24))))
            (func (export "mod_frame") (param f32) (call $exit (i32.const 3)))
            """, host);

        Assert.True(sandbox.Init(), sandbox.DisabledReason);
        ReadOnlySpan<int> r = MemoryMarshal.Cast<byte, int>(host.Sent[0]);
        Assert.Equal(8, r[0]);   // fd_prestat_get: EBADF, there are no pre-opened directories
        Assert.Equal(0, r[1]);   // environ_sizes_get succeeds...
        Assert.Equal(0, r[2]);   // ...and reports zero variables
        Assert.Equal(52, r[3]);  // path_open: ENOSYS
        Assert.Equal(0, r[4]);   // stdout is the mod log
        Assert.Equal(8, r[5]);   // any other descriptor does not exist
        Assert.Equal(new[] { "Info:from wasi" }, host.Logs);

        Assert.False(sandbox.Frame(0f));
        Assert.Contains("proc_exit(3)", sandbox.DisabledReason);
    }
}

public class ModCommandDecoderTests
{
    private sealed class CountingSink : IModCommandSink
    {
        public int Calls;
        public string? LastText;
        public void DrawRect(float x, float y, float w, float h, uint rgba) => Calls++;
        public void DrawPic(int id, float x, float y, float w, float h, uint rgba) => Calls++;
        public void DrawText(int font, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8) { Calls++; LastText = Encoding.UTF8.GetString(utf8); }
        public void SetClip(float x, float y, float w, float h) => Calls++;
        public void ResetClip() => Calls++;
        public void PlaySound(int id, int channel, float volume, float pitch) => Calls++;
    }

    [Fact]
    public void Writer_RoundTripsEveryCommand()
    {
        ModCommandWriter writer = new();
        writer.SetClip(0, 0, 100, 100);
        writer.DrawRect(1, 2, 3, 4, 0xFFFFFFFF);
        writer.DrawPic(7, 1, 2, 3, 4, 0x80808080);
        writer.DrawText(0, 5, 6, 12, 0xFF0000FF, "héllo"u8); // 6 bytes: exercises the padding
        writer.ResetClip();
        writer.PlaySound(3, 1, 1f, 1f);

        CountingSink sink = new();
        Assert.Equal(6, ModCommandDecoder.Decode(writer.Written, sink));
        Assert.Equal(6, sink.Calls);
        Assert.Equal("héllo", sink.LastText);
        Assert.Equal(0, ModCommandDecoder.Decode(ReadOnlySpan<byte>.Empty, sink));
    }

    [Theory]
    [InlineData(new byte[] { 1, 0 })]                                  // truncated header
    [InlineData(new byte[] { 1, 0, 2, 0 })]                            // size smaller than the header
    [InlineData(new byte[] { 1, 0, 6, 0, 0, 0 })]                      // size not a multiple of 4
    [InlineData(new byte[] { 1, 0, 24, 0, 0, 0, 0, 0 })]               // size runs past the buffer
    [InlineData(new byte[] { 1, 0, 8, 0, 0, 0, 0, 0 })]                // DrawRect with the wrong payload size
    [InlineData(new byte[] { 0x63, 0, 4, 0 })]                         // unknown opcode
    [InlineData(new byte[] { 5, 0, 8, 0, 0, 0, 0, 0 })]                // ResetClip carrying a payload
    public void Decode_RejectsMalformedBuffers(byte[] buffer)
    {
        Assert.Equal(-1, ModCommandDecoder.Decode(buffer, new CountingSink()));
    }

    [Fact]
    public void Decode_RejectsNonFiniteCoordinatesAndLyingTextLengths()
    {
        ModCommandWriter writer = new();
        writer.DrawRect(float.NaN, 0, 1, 1, 0);
        Assert.Equal(-1, ModCommandDecoder.Decode(writer.Written, new CountingSink()));

        writer.Clear();
        writer.DrawText(0, 0, 0, 12, 0, "abcd"u8);
        byte[] lying = writer.Written.ToArray();
        lying[4 + 20] = 200; // claims 200 bytes of text in a record that holds 4
        Assert.Equal(-1, ModCommandDecoder.Decode(lying, new CountingSink()));
    }

    [Fact]
    public void Decode_NeverThrowsOrOverreadsOnRandomInput()
    {
        // Deterministic fuzz: a fixed seed, so a failure reproduces. Decode must return, whatever it is fed.
        Random random = new(20261007);
        CountingSink sink = new();
        byte[] buffer = new byte[512];
        for (int i = 0; i < 20000; i++)
        {
            int length = random.Next(buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            // Bias the stream towards plausible headers so the payload checks are reached.
            if (length >= 4) { buffer[0] = (byte)random.Next(8); buffer[1] = 0; buffer[2] = (byte)(random.Next(16) * 4); buffer[3] = 0; }
            int result = ModCommandDecoder.Decode(buffer.AsSpan(0, length), sink);
            Assert.True(result >= -1);
        }
    }
}
