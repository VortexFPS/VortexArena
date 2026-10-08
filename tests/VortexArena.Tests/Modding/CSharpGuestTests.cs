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
/// The sandbox against a real C# mod: the hello-hud template compiled to WebAssembly by NativeAOT-LLVM.
///
/// The module is a build output, not a committed file (a 2 MB binary per rebuild is not something to
/// keep in git history), so these tests run only where it has been built - set VA_CSHARP_GUEST_WASM to
/// the file, or publish the template in place (modding-sdk/README.md). Without it they return early,
/// like the other tests that need content the checkout does not carry.
/// </summary>
public class CSharpGuestTests
{
    private static string? GuestPath()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("VA_CSHARP_GUEST_WASM");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        string template = Path.Combine(TestPaths.RepoRoot, "modding-sdk", "csharp", "templates", "hello-hud");
        if (!Directory.Exists(template)) return null;
        return Directory.EnumerateFiles(template, "hello-hud.wasm", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private sealed class Host : IModHost
    {
        public readonly List<string> Logs = new(), Draws = new();
        public float SpeedX;

        public void Log(ModLogLevel level, string message) => Logs.Add(message);
        public int EntityCount => 0;
        public double Time => 1.0;

        public int ReadState(ModStateKind kind, int index, Span<byte> destination) => kind switch
        {
            ModStateKind.Screen => Copy(new ModScreenState { Width = 1280, Height = 720, VirtualWidth = 1067, VirtualHeight = 600 }, destination),
            ModStateKind.LocalPlayer => Copy(new ModLocalPlayerState { VelocityX = SpeedX, Health = 100 }, destination),
            _ => -1,
        };

        private static int Copy<T>(T record, Span<byte> destination) where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref record, 1));
            int count = Math.Min(bytes.Length, destination.Length);
            bytes[..count].CopyTo(destination);
            return count;
        }

        public bool TryGetCvar(string name, out string value) { value = ""; return false; }
        public int ResolveAsset(ModAssetKind kind, string path) => 0;
        public float MeasureText(int fontId, float size, string text) => text.Length * size;
        public bool SendToServer(ReadOnlySpan<byte> payload) => false;
        public void DrawRect(float x, float y, float w, float h, uint rgba) => Draws.Add($"rect {x} {y} {w} {h} {rgba:X8}");
        public void DrawPic(int id, float x, float y, float w, float h, uint rgba) => Draws.Add("pic");
        public void DrawText(int font, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8) => Draws.Add($"text {x} {y} {size} {Encoding.UTF8.GetString(utf8)}");
        public void SetClip(float x, float y, float w, float h) { }
        public void ResetClip() { }
        public void PlaySound(int id, int channel, float volume, float pitch) { }
    }

    [Fact]
    public void ACSharpMod_ImportsOnlyTheInterfaceAndTheWasiFunctionsTheSandboxAnswers()
    {
        if (GuestPath() is not { } path || !WasmModSandbox.IsAvailable) return;
        using Wasmtime.Engine engine = new();
        using Module module = Module.FromBytes(engine, "guest", File.ReadAllBytes(path));

        // Anything else would be refused at load. Pinned so that a compiler upgrade which starts
        // importing something new shows up here, as a named function, before a mod author hits it.
        HashSet<string> answered = new()
        {
            "environ_get", "environ_sizes_get", "args_get", "args_sizes_get", "clock_time_get", "clock_res_get", "fd_close",
            "fd_fdstat_get", "fd_prestat_get", "fd_prestat_dir_name", "fd_seek", "fd_write", "fd_read", "poll_oneoff",
            "proc_exit", "sched_yield", "random_get",
        };
        foreach (Import import in module.Imports)
        {
            Assert.Contains(import.ModuleName, new[] { ModAbi.ImportModule, ModAbi.WasiModule });
            if (import.ModuleName == ModAbi.WasiModule)
                Assert.True(answered.Contains(import.Name), $"the C# runtime imports WASI function '{import.Name}', which is not on the reviewed list");
        }
        Assert.Contains(module.Exports, e => e.Name == ModAbi.ExportInitialize); // the .NET runtime's start-up
    }

    [Fact]
    public void ACSharpMod_StartsDrawsAndStaysWithinItsBudgets()
    {
        if (GuestPath() is not { } path || !WasmModSandbox.IsAvailable) return;
        Host host = new();
        using WasmModSandbox sandbox = WasmModSandbox.Load("hello-hud", File.ReadAllBytes(path), host);

        Assert.True(sandbox.Init(), sandbox.DisabledReason);
        Assert.Equal(new[] { "hello-hud loaded" }, host.Logs);
        // The .NET runtime's own start-up must fit the default memory limit with room for the mod.
        Assert.InRange(sandbox.MemoryBytes, 1, ModLimits.Default.MaxMemoryBytes / 2);

        host.SpeedX = 417.4f;
        Assert.True(sandbox.Frame(0.016f), sandbox.DisabledReason);
        Assert.Equal(new[] { "rect 8 560 200 32 000000A0", "text 16 566 16 speed 417" }, host.Draws);

        // A few thousand frames: the garbage collector inside the guest gets its chances to run, and
        // none of them may cost the mod its frame budget. The template allocates nothing per frame, so
        // memory must not grow either.
        long before = sandbox.MemoryBytes;
        for (int i = 0; i < 5000; i++)
        {
            host.Draws.Clear();
            host.SpeedX = i;
            Assert.True(sandbox.Frame(0.016f), $"frame {i}: {sandbox.DisabledReason}");
        }
        Assert.Equal(before, sandbox.MemoryBytes);
        Assert.Equal(ModSandboxState.Running, sandbox.State);
    }
}
