using System.Runtime.InteropServices;
using System.Text;
using Wasmtime;

namespace VortexArena.Modding;

public enum ModSandboxState
{
    /// <summary>Instantiated; <see cref="WasmModSandbox.Init"/> has not run yet.</summary>
    Loaded,
    Running,
    /// <summary>Trapped, overran a budget or broke the ABI. Every further call is a no-op.</summary>
    Disabled,
}

/// <summary>The module was refused before any of its code ran. The message says which rule it broke.</summary>
public sealed class ModLoadException : Exception
{
    public ModLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Thrown inside a host import when the guest passes something it has no right to. Becomes a trap.</summary>
internal sealed class ModViolationException : Exception
{
    public ModViolationException(string message) : base(message) { }
}

/// <summary>
/// One downloaded WebAssembly module, instantiated with no authority except <see cref="IModHost"/>.
///
/// The contract with the rest of the client is that a guest can waste its own budget and nothing
/// else. Whatever it does - loops forever, recurses without end, asks for a gigabyte, hands an import
/// a pointer outside its memory, emits a malformed command buffer - the call returns false, the
/// sandbox moves to <see cref="ModSandboxState.Disabled"/> with a reason, and the game carries on
/// without the mod. Nothing here throws at the caller once <see cref="Load"/> has succeeded.
///
/// Not thread-safe: use one instance from one thread. Create a fresh one per match rather than reusing
/// it, so no guest state survives into the next server's session.
/// </summary>
public sealed class WasmModSandbox : IDisposable
{
    /// <summary>
    /// Period of the watchdog that advances Wasmtime's epoch. Budgets are rounded up to whole ticks, and
    /// the OS timer may be coarser than this (about 15 ms on Windows unless something raised the timer
    /// resolution), so a budget is a ceiling on a stuck guest, not a scheduler.
    /// </summary>
    public const int EpochTickMs = 2;

    private static readonly Lazy<string?> s_unavailableReason = new(ProbeRuntime);

    /// <summary>
    /// False where the Wasmtime native library cannot be loaded - today that is any RID the NuGet package
    /// ships no binary for (linux-ppc64le). Callers treat it as "this client cannot run mods".
    /// </summary>
    public static bool IsAvailable => s_unavailableReason.Value is null;
    public static string? UnavailableReason => s_unavailableReason.Value;

    private readonly IModHost _host;
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Linker _linker;
    private readonly Store _store;
    private readonly Timer _watchdog;
    private readonly object _engineLock = new();
    private bool _disposed;
    private bool _inGuest;
    private int _logLinesThisCall;

    private Memory _memory = null!;
    private Action? _initialize, _init, _shutdown;
    private Action<float> _frame = null!;
    private Action<int, int, int>? _event;
    private Func<int, int>? _alloc;

    public string Name { get; }
    public ModLimits Limits { get; }
    public ModSandboxState State { get; private set; } = ModSandboxState.Loaded;
    public string? DisabledReason { get; private set; }

    /// <summary>Current size of the guest's linear memory, for the diagnostics overlay.</summary>
    public long MemoryBytes => State == ModSandboxState.Disabled || _disposed ? 0 : _memory.GetLength();

    private WasmModSandbox(string name, IModHost host, ModLimits limits, Engine engine, Module module)
    {
        Name = name;
        _host = host;
        Limits = limits;
        _engine = engine;
        _module = module;
        _linker = new Linker(engine);
        _store = new Store(engine);
        _watchdog = new Timer(_ => Tick(), null, EpochTickMs, EpochTickMs);
    }

    /// <summary>
    /// Validates, compiles and instantiates <paramref name="wasm"/>. No guest code runs here except the
    /// module's own start function, which executes under the init budget.
    /// </summary>
    /// <exception cref="ModLoadException">The module is oversized, malformed, or asks for something outside the ABI.</exception>
    public static WasmModSandbox Load(string name, ReadOnlySpan<byte> wasm, IModHost host, ModLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        limits ??= ModLimits.Default;
        if (!IsAvailable) throw new ModLoadException($"the WebAssembly runtime is unavailable on this platform: {UnavailableReason}");
        if (wasm.Length > limits.MaxModuleBytes)
            throw new ModLoadException($"module is {wasm.Length} bytes; the limit is {limits.MaxModuleBytes}");

        Engine engine = new(BuildConfig(limits));
        Module? module = null;
        WasmModSandbox? sandbox = null;
        try
        {
            try { module = Module.FromBytes(engine, name, wasm); }
            catch (WasmtimeException e) { throw new ModLoadException($"not a valid WebAssembly module: {FirstLine(e.Message)}", e); }

            sandbox = new WasmModSandbox(name, host, limits, engine, module);
            sandbox.CheckExports();
            sandbox.DefineImports();
            sandbox.Instantiate();
            return sandbox;
        }
        catch
        {
            if (sandbox is not null) sandbox.Dispose();
            else { module?.Dispose(); engine.Dispose(); }
            throw;
        }
    }

    /// <summary>Runs the guest's one-off start-up (<c>_initialize</c>, then <c>mod_init</c>).</summary>
    public bool Init()
    {
        if (State != ModSandboxState.Loaded) return false;
        if (_initialize is not null && !Call(Limits.InitBudgetMs, ModAbi.ExportInitialize, _initialize)) return false;
        if (_init is not null && !Call(Limits.InitBudgetMs, ModAbi.ExportInit, _init)) return false;
        State = ModSandboxState.Running;
        return true;
    }

    /// <summary>Runs one frame of the guest. Returns false, without throwing, when the mod is or becomes disabled.</summary>
    public bool Frame(float deltaSeconds)
    {
        if (State != ModSandboxState.Running) return false;
        Action<float> frame = _frame;
        return Call(Limits.FrameBudgetMs, ModAbi.ExportFrame, () => frame(deltaSeconds));
    }

    /// <summary>
    /// Delivers a message (a server-mod payload, a console command) to the guest. The payload is copied
    /// into a buffer the guest allocates through <c>mod_alloc</c> and then owns.
    /// </summary>
    public bool Event(int eventId, ReadOnlySpan<byte> payload)
    {
        if (State != ModSandboxState.Running || _event is null) return false;
        if (payload.Length > Limits.MaxCommandBytes) return false;

        int pointer = 0;
        if (payload.Length > 0)
        {
            if (_alloc is null) return false;
            Func<int, int> alloc = _alloc;
            int length = payload.Length;
            if (!Call(Limits.FrameBudgetMs, ModAbi.ExportAlloc, () => pointer = alloc(length))) return false;
            if (pointer <= 0 || (long)pointer + length > _memory.GetLength())
            {
                Disable($"{ModAbi.ExportAlloc} returned a buffer outside guest memory");
                return false;
            }
            payload.CopyTo(_memory.GetSpan(pointer, length));
        }

        Action<int, int, int> handler = _event;
        int ptr = pointer, len = payload.Length;
        return Call(Limits.FrameBudgetMs, ModAbi.ExportEvent, () => handler(eventId, ptr, len));
    }

    /// <summary>Best-effort <c>mod_shutdown</c>, under the init budget. Safe to call in any state.</summary>
    public void Shutdown()
    {
        if (State == ModSandboxState.Running && _shutdown is not null)
            Call(Limits.InitBudgetMs, ModAbi.ExportShutdown, _shutdown);
        if (State != ModSandboxState.Disabled) Disable("shut down");
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _watchdog.Dispose();
        _store.Dispose();
        _linker.Dispose();
        _module.Dispose();
        _engine.Dispose();
    }

    // ------------------------------------------------------------------------------------------------

    private static string? ProbeRuntime()
    {
        try
        {
            using Engine engine = new();
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            return $"{RuntimeInformation.RuntimeIdentifier}: {FirstLine(e.Message)}";
        }
    }

    private static Config BuildConfig(ModLimits limits)
    {
        Config config = new Config()
            .WithEpochInterruption(true)
            .WithMaximumStackSize(limits.MaxStackBytes);

        // Features no v1 guest needs. Each is one less piece of the engine a hostile module can reach.
        // Guarded individually because the C API omits entry points for features a given native build
        // was compiled without, and the binding reports that only when the method is called.
        Optional(() => config.WithWasmThreads(false));
        Optional(() => config.WithMemory64(false));
        Optional(() => config.WithMultiMemory(false));
        Optional(() => config.WithGc(false));
        Optional(() => config.WithComponentModel(false));

        // On macOS Wasmtime catches traps on a Mach-port handler thread by default, which fights the
        // .NET runtime's own handlers; plain signals coexist. The entry point exists only in the macOS
        // library, so it cannot be called unconditionally.
        if (OperatingSystem.IsMacOS()) Optional(() => config.WithMacosMachPorts(false));
        return config;
    }

    private static void Optional(Action configure)
    {
        try { configure(); }
        catch (EntryPointNotFoundException) { }
    }

    private void Tick()
    {
        lock (_engineLock)
        {
            if (!_disposed) _engine.IncrementEpoch();
        }
    }

    private void CheckExports()
    {
        bool hasMemory = false, hasFrame = false;
        foreach (Export export in _module.Exports)
        {
            switch (export)
            {
                case MemoryExport memory when export.Name == ModAbi.ExportMemory:
                    if (memory.Is64Bit) throw new ModLoadException("64-bit memories are not supported");
                    if (memory.Minimum * ModAbi.PageSize > Limits.MaxMemoryBytes)
                        throw new ModLoadException($"module starts with {memory.Minimum * ModAbi.PageSize} bytes of memory; the limit is {Limits.MaxMemoryBytes}");
                    hasMemory = true;
                    break;
                case FunctionExport function:
                    string? expected = export.Name switch
                    {
                        ModAbi.ExportInitialize or ModAbi.ExportInit or ModAbi.ExportShutdown => "()",
                        ModAbi.ExportFrame => "(f32)",
                        ModAbi.ExportEvent => "(i32,i32,i32)",
                        ModAbi.ExportAlloc => "(i32)->i32",
                        _ => null, // other exports are the guest's own business; the host never calls them
                    };
                    if (expected is null) break;
                    string actual = Signature(function.Parameters, function.Results);
                    if (actual != expected)
                        throw new ModLoadException($"export '{export.Name}' has signature {actual}; the ABI requires {expected}");
                    hasFrame |= export.Name == ModAbi.ExportFrame;
                    break;
            }
        }
        if (!hasMemory) throw new ModLoadException($"module does not export its memory as '{ModAbi.ExportMemory}'");
        if (!hasFrame) throw new ModLoadException($"module does not export '{ModAbi.ExportFrame}'");
    }

    private void DefineImports()
    {
        DefineVortexImports();
        foreach (Import import in _module.Imports)
        {
            if (import is not FunctionImport function)
                throw new ModLoadException($"module imports '{import.ModuleName}.{import.Name}', which is not a function; only host functions can be imported");

            string signature = Signature(function.Parameters, function.Results);
            switch (import.ModuleName)
            {
                case ModAbi.ImportModule:
                    if (!s_vortexSignatures.TryGetValue(import.Name, out string? expected))
                        throw new ModLoadException($"module imports '{ModAbi.ImportModule}.{import.Name}', which this client does not provide");
                    if (signature != expected)
                        throw new ModLoadException($"import '{ModAbi.ImportModule}.{import.Name}' is declared as {signature}; the ABI defines {expected}");
                    break;
                case ModAbi.WasiModule:
                    WasiStubs.Define(_linker, function, this);
                    break;
                default:
                    throw new ModLoadException($"module imports from '{import.ModuleName}'; only '{ModAbi.ImportModule}' is available");
            }
        }
    }

    private static readonly Dictionary<string, string> s_vortexSignatures = new(StringComparer.Ordinal)
    {
        ["log"] = "(i32,i32,i32)",
        ["commands"] = "(i32,i32)",
        ["state_read"] = "(i32,i32,i32,i32)->i32",
        ["entity_count"] = "()->i32",
        ["cvar_get"] = "(i32,i32,i32,i32)->i32",
        ["asset_id"] = "(i32,i32,i32)->i32",
        ["text_width"] = "(i32,f32,i32,i32)->f32",
        ["time_now"] = "()->f64",
        ["send_to_server"] = "(i32,i32)->i32",
    };

    private void DefineVortexImports()
    {
        const string m = ModAbi.ImportModule;

        _linker.DefineFunction(m, "log", (Caller caller, int level, int ptr, int len) =>
            LogFromGuest((ModLogLevel)Math.Clamp(level, 0, 2), GuestString(caller, ptr, len)));

        _linker.DefineFunction(m, "commands", (Caller caller, int ptr, int len) =>
        {
            ReadOnlySpan<byte> buffer = GuestBytes(caller, ptr, len, Limits.MaxCommandBytes);
            if (ModCommandDecoder.Decode(buffer, _host) < 0)
                throw new ModViolationException("malformed command buffer");
        });

        _linker.DefineFunction(m, "state_read", (Caller caller, int kind, int index, int ptr, int capacity) =>
            _host.ReadState((ModStateKind)kind, index, GuestBytes(caller, ptr, capacity, Limits.MaxCommandBytes)));

        _linker.DefineFunction(m, "entity_count", () => _host.EntityCount);

        // Returns the value's full UTF-8 length (so the guest can retry with a bigger buffer), or -1
        // when the cvar is not one a mod may read. Unknown and forbidden are deliberately the same answer.
        _linker.DefineFunction(m, "cvar_get", (Caller caller, int namePtr, int nameLen, int outPtr, int capacity) =>
        {
            string name = GuestString(caller, namePtr, nameLen);
            if (!_host.TryGetCvar(name, out string value)) return -1;
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            Span<byte> destination = GuestBytes(caller, outPtr, capacity, Limits.MaxStringBytes);
            utf8.AsSpan(0, Math.Min(utf8.Length, destination.Length)).CopyTo(destination);
            return utf8.Length;
        });

        _linker.DefineFunction(m, "asset_id", (Caller caller, int kind, int ptr, int len) =>
            _host.ResolveAsset((ModAssetKind)kind, GuestString(caller, ptr, len)));

        _linker.DefineFunction(m, "text_width", (Caller caller, int font, float size, int ptr, int len) =>
        {
            float width = _host.MeasureText(font, size, GuestString(caller, ptr, len));
            return float.IsFinite(width) ? width : 0f;
        });

        _linker.DefineFunction(m, "time_now", () => _host.Time);

        _linker.DefineFunction(m, "send_to_server", (Caller caller, int ptr, int len) =>
            _host.SendToServer(GuestBytes(caller, ptr, len, Limits.MaxCommandBytes)) ? 1 : 0);
    }

    private void Instantiate()
    {
        _store.SetLimits(Limits.MaxMemoryBytes, Limits.MaxTableElements, instances: 1, tables: 4, memories: 1);
        _store.SetEpochDeadline(Ticks(Limits.InitBudgetMs));

        Instance instance;
        try { instance = _linker.Instantiate(_store, _module); }
        catch (WasmtimeException e) { throw new ModLoadException($"module failed to instantiate: {FirstLine(e.Message)}", e); }

        _memory = instance.GetMemory(ModAbi.ExportMemory) ?? throw new ModLoadException("memory export disappeared at instantiation");
        _frame = instance.GetAction<float>(ModAbi.ExportFrame) ?? throw new ModLoadException($"'{ModAbi.ExportFrame}' is not callable");
        _initialize = instance.GetAction(ModAbi.ExportInitialize);
        _init = instance.GetAction(ModAbi.ExportInit);
        _shutdown = instance.GetAction(ModAbi.ExportShutdown);
        _event = instance.GetAction<int, int, int>(ModAbi.ExportEvent);
        _alloc = instance.GetFunction<int, int>(ModAbi.ExportAlloc);
    }

    private bool Call(int budgetMs, string what, Action invoke)
    {
        if (_disposed || State == ModSandboxState.Disabled) return false;
        if (_inGuest) throw new InvalidOperationException("the sandbox was re-entered from inside a host import");

        _logLinesThisCall = 0;
        _inGuest = true;
        try
        {
            _store.SetEpochDeadline(Ticks(budgetMs));
            invoke();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Traps arrive as TrapException; an exception thrown by one of our own imports comes back
            // wrapped by the binding. Either way the guest is finished: its memory may be half-written
            // and nothing about its state can be trusted after an aborted call.
            Disable($"{what}: {Describe(e)}");
            return false;
        }
        finally
        {
            _inGuest = false;
        }
    }

    private static ulong Ticks(int budgetMs) => (ulong)((budgetMs + EpochTickMs - 1) / EpochTickMs) + 1;

    private void Disable(string reason)
    {
        if (State == ModSandboxState.Disabled) return;
        State = ModSandboxState.Disabled;
        DisabledReason = reason;
    }

    private static string Describe(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
            if (inner is ModViolationException violation) return $"ABI violation ({violation.Message})";

        return e is TrapException trap
            ? trap.Type switch
            {
                TrapCode.Interrupt => "exceeded its time budget",
                TrapCode.StackOverflow => "stack overflow",
                TrapCode.MemoryOutOfBounds => "out-of-bounds memory access",
                TrapCode.Unreachable => "guest aborted (unreachable)",
                _ => $"trap ({trap.Type}): {FirstLine(trap.Message)}",
            }
            : FirstLine(e.Message);
    }

    // ---- guest memory access: every pointer a guest supplies goes through one of these two ----------

    /// <summary>
    /// The guest's bytes at [<paramref name="ptr"/>, ptr+len), or a violation. The memory is looked up
    /// on every call because it can grow, and so move, between any two host calls.
    /// </summary>
    internal Span<byte> GuestBytes(Caller caller, int ptr, int len, int maxLength)
    {
        Memory memory = caller.GetMemory(ModAbi.ExportMemory) ?? throw new ModViolationException("no memory export");
        if (ptr < 0 || len < 0) throw new ModViolationException("negative pointer or length");
        if (len > maxLength) throw new ModViolationException($"length {len} exceeds the {maxLength}-byte limit");
        if ((long)ptr + len > memory.GetLength()) throw new ModViolationException("pointer range is outside guest memory");
        return len == 0 ? Span<byte>.Empty : memory.GetSpan(ptr, len);
    }

    internal string GuestString(Caller caller, int ptr, int len) =>
        // Invalid UTF-8 decodes to U+FFFD rather than throwing: bad text is the guest's own problem.
        Encoding.UTF8.GetString(GuestBytes(caller, ptr, len, Limits.MaxStringBytes));

    internal void LogFromGuest(ModLogLevel level, string message)
    {
        if (_logLinesThisCall++ >= Limits.MaxLogLinesPerCall) return;
        _host.Log(level, message);
    }

    internal double HostTime => _host.Time;

    private static string Signature(IReadOnlyList<ValueKind> parameters, IReadOnlyList<ValueKind> results)
    {
        StringBuilder text = new("(");
        for (int i = 0; i < parameters.Count; i++) text.Append(i == 0 ? "" : ",").Append(Short(parameters[i]));
        text.Append(')');
        if (results.Count > 0)
        {
            text.Append("->");
            for (int i = 0; i < results.Count; i++) text.Append(i == 0 ? "" : ",").Append(Short(results[i]));
        }
        return text.ToString();

        static string Short(ValueKind kind) => kind switch
        {
            ValueKind.Int32 => "i32",
            ValueKind.Int64 => "i64",
            ValueKind.Float32 => "f32",
            ValueKind.Float64 => "f64",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }

    private static string FirstLine(string message)
    {
        int end = message.IndexOf('\n');
        return (end < 0 ? message : message[..end]).Trim();
    }
}
