using System;
using System.Runtime.InteropServices;

namespace VortexArena.Game.Client;

/// <summary>
/// (engine-perf 2026-06-16) A tiny, self-guarding bridge to the RenderDoc in-application API
/// (renderdoc_app.h). It lets the GAME programmatically trigger a RenderDoc frame capture from inside its own
/// render loop — the only reliable way to capture a STOCHASTIC, render-thread shader/pipeline-compile stall:
/// FrameProfiler calls <see cref="TriggerCapture"/> the instant a SURFACE pipeline-compile is detected, so
/// RenderDoc captures the exact frame regardless of window focus / present state (the GUI's "capture frame"
/// button can't, because a backgrounded window stops presenting).
///
/// <para>SAFE BY CONSTRUCTION: it never <c>LoadLibrary</c>s anything. It only resolves <c>renderdoc.dll</c> via
/// <see cref="GetModuleHandleA"/> — which returns null unless the process was launched UNDER RenderDoc (the
/// capture layer injects the dll). In every normal run <see cref="Available"/> is false and every method is a
/// no-op, so this is free to ship.</para>
/// </summary>
internal static class RenderDocCapture
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiFn(int version, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetPathFn(IntPtr pathUtf8);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = false)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = false)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    // The RENDERDOC_API_1_x_x struct is a flat array of function pointers in a stable order (new versions only
    // APPEND). Indices used here are valid since RenderDoc 1.0: SetCaptureFilePathTemplate=11, TriggerCapture=15.
    private const int IdxSetPathTemplate = 11;
    private const int IdxTriggerCapture = 15;
    private const int ApiVersion_1_1_2 = 10102;   // lowest version that exposes both; offsets are version-stable

    private static IntPtr _api;
    private static bool _init, _available;

    /// <summary>True only when running under RenderDoc (the dll is injected). Lazily initialised once.</summary>
    public static bool Available
    {
        get
        {
            if (_init) return _available;
            _init = true;
            try
            {
                IntPtr rdoc = GetModuleHandleA("renderdoc.dll");
                if (rdoc == IntPtr.Zero) return false;
                IntPtr getApiPtr = GetProcAddress(rdoc, "RENDERDOC_GetAPI");
                if (getApiPtr == IntPtr.Zero) return false;
                var getApi = Marshal.GetDelegateForFunctionPointer<GetApiFn>(getApiPtr);
                if (getApi(ApiVersion_1_1_2, out _api) != 1 || _api == IntPtr.Zero) return false;
                _available = true;

                // Save captures next to the repo so they're trivial to find (RenderDoc appends _frameNNNN.rdc).
                try
                {
                    IntPtr setPathPtr = Marshal.ReadIntPtr(_api, IdxSetPathTemplate * IntPtr.Size);
                    var setPath = Marshal.GetDelegateForFunctionPointer<SetPathFn>(setPathPtr);
                    IntPtr utf8 = Marshal.StringToHGlobalAnsi(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xonotic_rdoc", "surfacecompile"));
                    setPath(utf8);
                    Marshal.FreeHGlobal(utf8);
                }
                catch { /* path template is best-effort; default location still works */ }
            }
            catch { _available = false; }
            return _available;
        }
    }

    /// <summary>Queue a RenderDoc capture of the NEXT presented frame. No-op when not running under RenderDoc.</summary>
    public static void TriggerCapture()
    {
        if (!Available) return;
        try
        {
            IntPtr fnPtr = Marshal.ReadIntPtr(_api, IdxTriggerCapture * IntPtr.Size);
            if (fnPtr == IntPtr.Zero) return;
            Marshal.GetDelegateForFunctionPointer<VoidFn>(fnPtr)();
        }
        catch { /* never let an instrumentation hook escape into the render loop */ }
    }
}
