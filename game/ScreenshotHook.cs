using Godot;

namespace VortexArena.Game;

/// <summary>
/// Dev/CI visual-capture helper. When the host is launched with <c>--screenshot &lt;path&gt;</c>,
/// <see cref="Main"/> attaches this node: it lets the scene settle for <see cref="WarmupFrames"/> idle frames
/// (assets stream in, lighting/shadows resolve), waits for the GPU to finish drawing, writes the root viewport
/// to a PNG, and quits the tree.
///
/// Run WINDOWED — a <c>--headless</c> run uses the dummy renderer and the capture comes out blank. With this an
/// agent (or CI) can produce a real frame of the running game and actually inspect it. See docs/RUNNING.md
/// "Visual capture".
/// </summary>
public partial class ScreenshotHook : Node
{
    /// <summary>Destination PNG. A <c>res://</c>/<c>user://</c> path is globalized; otherwise it's an OS path.</summary>
    public string OutPath { get; init; } = "";

    /// <summary>Idle frames to spin before capturing, so streaming assets + shadows settle.</summary>
    public int WarmupFrames { get; init; } = 90;

    /// <summary>
    /// Deterministic-capture gate: while true, the hook keeps waiting (after its warmup) instead of
    /// capturing. A demo driver that wants the frame at an exact moment (GameDemo's <c>--fx-still</c>:
    /// one effect burst captured at a precise age) sets this at boot and clears it at the moment to
    /// shoot — the capture then lands within a frame, independent of boot/load timing.
    /// </summary>
    public static volatile bool Hold;

    public override async void _Ready()
    {
        if (string.IsNullOrWhiteSpace(OutPath))
        {
            GD.PrintErr("[Screenshot] no output path; skipping.");
            return;
        }

        // Wait until the active scene reports it's ready to capture (a NetGame listen server's connect→spawn
        // handshake — the --map path — flips CaptureGate; the ModelViewer flips it as soon as it's built), then
        // settle a few more frames for asset streaming + shadow/SSAO convergence. Capped at WarmupFrames so a
        // scene that never signals (e.g. a --menu-screen dialog) still captures at the old fixed budget, and a
        // headless run never blocks forever.
        const int settleAfterReady = 45;
        int readyAt = -1;
        for (int i = 0; i < WarmupFrames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!CaptureGate.Ready)
                continue;
            if (readyAt < 0)
                readyAt = i;
            if (i - readyAt >= settleAfterReady)
                break;
        }

        // A running editor bake would otherwise be captured mid-flight — the frame would show whatever
        // lighting existed BEFORE it, which silently invalidates every A/B comparison made from screenshots.
        // The APPLY counts too: the finished bake streams onto the world a few milliseconds a frame, so a
        // capture between "bake done" and "apply done" shows a world lit PARTLY by the old bake and partly
        // by the new one — the worst possible reference image, and one that looks plausible.
        // Re-checked AFTER settling, because these stages hand off across frames: a finished bake is not
        // applied until the host's next tick starts the recolour, so for one frame "not baking" and "not
        // applying" are both true while the work is very much unfinished. A single pass through the wait
        // photographed a world 31% recoloured — half old lighting, half new, and plausible enough to publish.
        for (int guard = 0; guard < 4; guard++)
        {
            while (VortexArena.Game.Vmap.EditorLightBake.BakeRunning
                   || VortexArena.Game.Vmap.EditorLightBake.BakeFinished
                   || VortexArena.Game.Vmap.VmapMapBuilder.RecolorRemaining > 0)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            // Settle, then look again: if the handoff started something, the loop above catches it.
            for (int i = 0; i < 8; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            if (!VortexArena.Game.Vmap.EditorLightBake.BakeRunning
                && !VortexArena.Game.Vmap.EditorLightBake.BakeFinished
                && VortexArena.Game.Vmap.VmapMapBuilder.RecolorRemaining == 0)
                break;
        }

        // Deterministic-capture gate (--fx-still): wait for the demo driver to release at the exact moment.
        while (Hold)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // Capture only AFTER the GPU has drawn the frame, or the viewport image is stale/blank.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        Image? img = GetViewport()?.GetTexture()?.GetImage();
        if (img is null)
        {
            GD.PrintErr("[Screenshot] viewport image unavailable (running headless?).");
            GetTree().Quit(1);
            return;
        }

        // Resolve res://|user:// to an absolute path and ensure the directory exists (SavePng won't create it).
        string path = OutPath;
        if (path.StartsWith("res://") || path.StartsWith("user://"))
            path = ProjectSettings.GlobalizePath(path);
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        Error err = img.SavePng(path);
        if (err == Error.Ok)
            GD.Print($"[Screenshot] wrote {img.GetWidth()}x{img.GetHeight()} -> {path}");
        else
            GD.PrintErr($"[Screenshot] SavePng('{path}') failed: {err}");

        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
