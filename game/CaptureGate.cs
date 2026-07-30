namespace VortexArena.Game;

/// <summary>
/// A tiny process-wide latch the visual-capture path (<see cref="ScreenshotHook"/>) waits on, so a windowed
/// <c>--screenshot</c> lands on the finished scene instead of a loading screen or a pre-spawn frame.
///
/// <para>A scene that comes up ASYNCHRONOUSLY — a <see cref="Net.NetGame"/> listen server's connect→spawn
/// handshake (now the <c>--map</c> path too) — flips this true the moment the local player is spawned and the
/// camera is at the predicted eye. A SYNCHRONOUS scene (<see cref="ModelViewer"/>) flips it as soon as it's
/// built. A scene that never flips it (a menu dialog under <c>--menu-screen</c>) just leaves the screenshot
/// hook to fall back to its fixed frame budget — unchanged behaviour.</para>
/// </summary>
public static class CaptureGate
{
    /// <summary>True once the active scene reports it is fully on screen and safe to capture.</summary>
    public static bool Ready { get; private set; }

    /// <summary>Signal that the scene is ready to capture (idempotent).</summary>
    public static void MarkReady() => Ready = true;

    /// <summary>Clear the latch (e.g. on a changelevel, before the next map's spawn).</summary>
    public static void Reset() => Ready = false;
}
