using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wasmtime;

namespace VortexArena.Modding;

/// <summary>
/// Answers the WASI (WebAssembly System Interface) imports a guest's own language runtime insists on,
/// without granting anything. This is NOT a WASI implementation and <c>Linker.DefineWasi()</c> is never
/// called: that would hand the guest real file descriptors, environment variables and a wall clock.
///
/// A C# guest needs this because the .NET runtime compiled into it by NativeAOT-LLVM imports a few WASI
/// functions to start up and to print an unhandled exception. What each gets:
///   fd_write to stdout/stderr -> the mod log (rate-limited like any other log line)
///   clock_time_get            -> game time, the same clock the ABI's own time_now gives
///   random_get                -> random bytes (a guest could synthesise its own; this leaks nothing)
///   args/environ              -> empty
///   proc_exit                 -> a trap, which disables the mod
///   everything else           -> ENOSYS or EBADF, i.e. "this system has no such thing"
/// </summary>
internal static class WasiStubs
{
    private const int Success = 0;
    private const int BadFileDescriptor = 8;
    private const int NotSupported = 52;
    private const int MaxIoVectors = 16;

    public static void Define(Linker linker, FunctionImport import, WasmModSandbox sandbox)
    {
        string name = import.Name;
        IReadOnlyList<ValueKind> parameters = import.Parameters, results = import.Results;
        bool returnsErrno = results.Count == 1 && results[0] == ValueKind.Int32;

        Function.UntypedCallbackDelegate callback = name switch
        {
            "fd_write" when Is(parameters, 4) && returnsErrno => (Caller caller, ReadOnlySpan<ValueBox> a, Span<ValueBox> r) =>
                r[0] = FdWrite(sandbox, caller, a[0].AsInt32(), a[1].AsInt32(), a[2].AsInt32(), a[3].AsInt32()),

            "clock_time_get" when parameters.Count == 3 && parameters[2] == ValueKind.Int32 && returnsErrno => (Caller caller, ReadOnlySpan<ValueBox> a, Span<ValueBox> r) =>
            {
                long nanoseconds = (long)(sandbox.HostTime * 1e9);
                BinaryPrimitives.WriteInt64LittleEndian(sandbox.GuestBytes(caller, a[2].AsInt32(), 8, 8), nanoseconds);
                r[0] = Success;
            },

            "random_get" when Is(parameters, 2) && returnsErrno => (Caller caller, ReadOnlySpan<ValueBox> a, Span<ValueBox> r) =>
            {
                RandomNumberGenerator.Fill(sandbox.GuestBytes(caller, a[0].AsInt32(), a[1].AsInt32(), ModAbi.PageSize));
                r[0] = Success;
            },

            "environ_sizes_get" or "args_sizes_get" when Is(parameters, 2) && returnsErrno => (Caller caller, ReadOnlySpan<ValueBox> a, Span<ValueBox> r) =>
            {
                sandbox.GuestBytes(caller, a[0].AsInt32(), 4, 4).Clear();
                sandbox.GuestBytes(caller, a[1].AsInt32(), 4, 4).Clear();
                r[0] = Success;
            },

            "environ_get" or "args_get" or "sched_yield" when returnsErrno => (Caller _, ReadOnlySpan<ValueBox> _, Span<ValueBox> r) =>
                r[0] = Success,

            "proc_exit" => (Caller _, ReadOnlySpan<ValueBox> a, Span<ValueBox> _) =>
                throw new ModViolationException($"guest called proc_exit({(a.Length > 0 ? a[0].AsInt32() : 0)})"),

            // Descriptor queries: there are no descriptors, including no pre-opened directories, which
            // is how a WASI libc discovers that it has no filesystem.
            _ when name.StartsWith("fd_", StringComparison.Ordinal) && returnsErrno => (Caller _, ReadOnlySpan<ValueBox> _, Span<ValueBox> r) =>
                r[0] = BadFileDescriptor,

            _ => (Caller _, ReadOnlySpan<ValueBox> _, Span<ValueBox> r) =>
            {
                for (int i = 0; i < r.Length; i++)
                    r[i] = results[i] switch
                    {
                        ValueKind.Int32 => returnsErrno ? NotSupported : 0,
                        ValueKind.Int64 => 0L,
                        ValueKind.Float32 => 0f,
                        ValueKind.Float64 => 0d,
                        _ => throw new ModViolationException($"WASI import '{name}' returns an unsupported type"),
                    };
            },
        };

        linker.DefineFunction(ModAbi.WasiModule, name, callback, parameters, results);
    }

    private static bool Is(IReadOnlyList<ValueKind> kinds, int count)
    {
        if (kinds.Count != count) return false;
        foreach (ValueKind kind in kinds)
            if (kind != ValueKind.Int32) return false;
        return true;
    }

    private static int FdWrite(WasmModSandbox sandbox, Caller caller, int fd, int iovs, int iovCount, int writtenPtr)
    {
        if (fd is not (1 or 2)) return BadFileDescriptor;
        if (iovCount < 0 || iovCount > MaxIoVectors) throw new ModViolationException("fd_write: too many I/O vectors");

        int maxString = sandbox.Limits.MaxStringBytes;
        StringBuilder text = new();
        int total = 0;
        for (int i = 0; i < iovCount; i++)
        {
            ReadOnlySpan<byte> vector = sandbox.GuestBytes(caller, checked(iovs + i * 8), 8, 8);
            int pointer = BinaryPrimitives.ReadInt32LittleEndian(vector);
            int length = BinaryPrimitives.ReadInt32LittleEndian(vector[4..]);
            ReadOnlySpan<byte> bytes = sandbox.GuestBytes(caller, pointer, length, maxString);
            total += length;
            if (text.Length < maxString) text.Append(Encoding.UTF8.GetString(bytes));
        }

        string message = text.ToString().TrimEnd('\r', '\n');
        if (message.Length > 0) sandbox.LogFromGuest(fd == 2 ? ModLogLevel.Warning : ModLogLevel.Info, message);
        BinaryPrimitives.WriteInt32LittleEndian(sandbox.GuestBytes(caller, writtenPtr, 4, 4), total);
        return Success;
    }
}
