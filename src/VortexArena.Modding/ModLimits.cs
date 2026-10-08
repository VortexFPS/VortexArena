namespace VortexArena.Modding;

/// <summary>
/// The resource budget one guest runs under. A server's manifest may ask for less than the client's
/// ceiling but never more: <see cref="ClampTo"/> is applied to whatever the manifest carries.
/// </summary>
public sealed record ModLimits
{
    /// <summary>Largest linear memory the guest may grow to. A <c>memory.grow</c> past it fails inside the guest.</summary>
    /// <remarks>
    /// 128 MiB because of C#: the .NET runtime inside a NativeAOT-LLVM guest grows its memory to about
    /// 50 MiB while starting up, before the mod has allocated anything (measured with the hello-hud
    /// template). A Rust guest of the same mod needs a fraction of a megabyte.
    /// </remarks>
    public long MaxMemoryBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Largest module accepted for compilation, before any of it is parsed.</summary>
    public long MaxModuleBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Wall-clock budget for one per-frame call. Enforced by Wasmtime's epoch interruption, which the
    /// guest cannot evade, at the resolution of <see cref="WasmModSandbox.EpochTickMs"/>.
    /// </summary>
    public int FrameBudgetMs { get; init; } = 8;

    /// <summary>Budget for the one-off calls (<c>_initialize</c>, <c>mod_init</c>, <c>mod_shutdown</c>). A C# guest
    /// starts a garbage-collected runtime in here, so it is far larger than the frame budget.</summary>
    public int InitBudgetMs { get; init; } = 2000;

    /// <summary>Guest call-stack ceiling. Runaway recursion traps here rather than reaching the host's stack.</summary>
    public int MaxStackBytes { get; init; } = 1024 * 1024;

    /// <summary>Largest command buffer accepted from one <c>commands</c> call.</summary>
    public int MaxCommandBytes { get; init; } = 1024 * 1024;

    /// <summary>Largest single string the host will read out of guest memory (log line, cvar name, asset path).</summary>
    public int MaxStringBytes { get; init; } = 4096;

    /// <summary>Log lines accepted per call into the guest; the rest are dropped, so a guest cannot flood the console.</summary>
    public int MaxLogLinesPerCall { get; init; } = 32;

    public uint MaxTableElements { get; init; } = 100_000;

    public static ModLimits Default { get; } = new();

    /// <summary>Returns these limits with every field lowered to <paramref name="ceiling"/> where it exceeds it.</summary>
    public ModLimits ClampTo(ModLimits ceiling) => this with
    {
        MaxMemoryBytes = Math.Clamp(MaxMemoryBytes, ModAbi.PageSize, ceiling.MaxMemoryBytes),
        MaxModuleBytes = Math.Clamp(MaxModuleBytes, 1, ceiling.MaxModuleBytes),
        FrameBudgetMs = Math.Clamp(FrameBudgetMs, 1, ceiling.FrameBudgetMs),
        InitBudgetMs = Math.Clamp(InitBudgetMs, 1, ceiling.InitBudgetMs),
        MaxStackBytes = Math.Clamp(MaxStackBytes, 64 * 1024, ceiling.MaxStackBytes),
        MaxCommandBytes = Math.Clamp(MaxCommandBytes, 0, ceiling.MaxCommandBytes),
        MaxStringBytes = Math.Clamp(MaxStringBytes, 0, ceiling.MaxStringBytes),
        MaxLogLinesPerCall = Math.Clamp(MaxLogLinesPerCall, 0, ceiling.MaxLogLinesPerCall),
        MaxTableElements = Math.Min(MaxTableElements, ceiling.MaxTableElements),
    };
}
