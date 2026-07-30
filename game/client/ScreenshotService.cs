using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Common.Config;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Services;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Console; // BindInput (global-hotkey key resolution)
using VortexArena.Game.Menu;    // KeyCaptureButton.Capturing (yield during a keybind rebind)

namespace VortexArena.Game.Client;

/// <summary>
/// The client-side <c>screenshot</c> command — the C# successor to DarkPlaces' <c>SCR_ScreenShot_f</c>
/// (cl_screen.c), bound to <c>F12</c> by <c>binds-xonotic.cfg</c>. It grabs the next fully-rendered frame from
/// the root viewport and writes it to a <c>screenshots/</c> folder under the per-user write dir
/// (<see cref="UserPaths"/> — <c>~/XonData/screenshots/</c>, beside config.cfg; the discoverable analogue of DP's
/// writable gamedir, NOT Godot's hidden <c>user://</c>), with format / quality / gamma / naming all driven by the
/// stock <c>scr_screenshot_*</c> cvars — so a player's F12 (and a console-typed <c>screenshot</c>) behaves the way
/// it does in the reference engine.
///
/// <para><b>Why a registered command, not a server route.</b> In DP <c>screenshot</c> is a <c>CF_CLIENT</c>
/// command: it never forwards to the server. Registering it on the shared <see cref="ConfigInterpreter"/>
/// (<see cref="RegisterCommand"/>) makes the interpreter dispatch it locally — consulted before the
/// server-routing <see cref="ConfigInterpreter.UnknownCommandHandler"/>. Both entry points run it through that one
/// interpreter: a line typed into the console, and the global key hotkey below (which calls
/// <see cref="ConfigInterpreter.ExecuteLine"/>). The capture needs the Godot viewport, so this lives in a
/// <see cref="Node"/> (the host assembly), mirroring how <see cref="BindInput"/> registers <c>bind</c>.</para>
///
/// <para><b>Global hotkey.</b> The screenshot bind also fires from this node's <see cref="_Input"/> (the early
/// input stage, before <c>NetGame._UnhandledInput</c>'s gameplay binds), so it works in EVERY context — the menu,
/// an open console, the pause screen — not only in a live match, matching DP where screenshot is a global client
/// command. The handler resolves the pressed key against the live bind table (<see cref="BindInput.ResolvePress"/>)
/// and only acts on the <c>screenshot</c> bind, then consumes the event so an in-match press doesn't ALSO run
/// through the gameplay-bind path (no double capture). It steps aside while a keybind is being captured
/// (<see cref="KeyCaptureButton.Capturing"/>) so the menu can still rebind a key to F12.</para>
///
/// <para><b>Confirmation.</b> Beyond the DP-faithful console line (<c>Wrote …</c>, which the always-on
/// frame-profiler log can scroll away), a brief on-screen toast confirms the write regardless of console state.
/// The toast is hidden during the capture so it never appears in the shot itself.</para>
/// </summary>
public partial class ScreenshotService : Node
{
    private CvarService? _cvars;
    private ConfigInterpreter? _interp;

    // One namer per session: it caches the sequential counter so repeated F12s don't rescan from 0 (DP's static
    // shotnumber). Timestamp mode is stateless (static helper).
    private readonly ScreenshotNamer _namer = new();

    // On-screen confirmation toast (a self-contained CanvasLayer label); _toastToken makes the newest toast's
    // hide-timer win when several fire in quick succession.
    private Label? _toast;
    private int _toastToken;

    /// <summary>
    /// Register the stock DarkPlaces/Xonotic <c>scr_screenshot_*</c> cvar defaults (cl_screen.c + the two lines
    /// xonotic-client.cfg sets). Called from <see cref="ClientSettings.ApplyAll"/> at boot so they're visible to
    /// <c>cvarlist</c>/<c>search</c>/the menu and live before the first capture. Idempotent — keeps any value a cfg
    /// or the user's config.cfg already set (<see cref="CvarService.Register"/> only folds in the default + flags).
    /// </summary>
    public static void RegisterDefaults(CvarService c)
    {
        if (c is null)
            return;
        const CvarFlags save = CvarFlags.Save; // DP CF_ARCHIVE — persists to config.cfg

        // Format + quality. xonotic-client.cfg already assigns the first two (jpeg 1 / quality 0.9); the rest are
        // engine cvars DP registers in C, so this is their only source in the port.
        c.Register("scr_screenshot_jpeg", "1", save);
        c.Register("scr_screenshot_jpeg_quality", "0.9", save);
        c.Register("scr_screenshot_png", "0", save);
        c.Register("scr_screenshot_gammaboost", "1", save);   // 1.0 = save unmodified (the default)
        c.Register("scr_screenshot_timestamp", "1", save);    // DP default: timestamped names
        c.Register("scr_screenshot_alpha", "0");              // CF_CLIENT, NOT archived (a debug feature)
        c.Register("scr_screenshot_name_in_mapdir", "0", save); // registered for parity; subdir routing not yet wired

        // DP's engine default is "dp" (fs.c sets it from the gamedir); this port ships "xonotic" — the recognizable,
        // expected prefix for a Xonotic screenshot. `set scr_screenshot_name dp` restores the engine spelling.
        c.Register("scr_screenshot_name", "xonotic", save);
    }

    /// <summary>
    /// Register the <c>screenshot</c> command on the shared interpreter. Call once at boot (from <see cref="Shell"/>)
    /// after the node is in the tree. <paramref name="cvars"/> is the same shared store the cvars were registered on.
    /// </summary>
    public void RegisterCommand(ConfigInterpreter interp, CvarService cvars)
    {
        if (interp is null || cvars is null)
            return;
        _cvars = cvars;
        _interp = interp;
        interp.RegisterCommand("screenshot", OnScreenshotCommand);
    }

    public override void _Ready()
    {
        // Process even while the SceneTree is PAUSED (the in-game menu pauses it) so the global hotkey + the
        // toast's hide-timer keep working at the menu / pause screen.
        ProcessMode = ProcessModeEnum.Always;
        BuildToast();
    }

    /// <summary>
    /// The global screenshot hotkey. Runs at the <c>_Input</c> stage (before <c>NetGame._UnhandledInput</c>), so a
    /// key bound to <c>screenshot</c> (default F12) fires in every context — menu, open console, paused match —
    /// not just during live play. Only the screenshot bind is acted on (everything else falls through untouched);
    /// the event is then consumed so an in-match press isn't ALSO run by the gameplay-bind path (no double shot).
    /// Yields while a keybind is being captured so the menu can rebind a key to F12.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (_interp is null || KeyCaptureButton.Capturing)
            return;
        string? cmd = BindInput.ResolvePress(@event);
        if (cmd is null)
            return;
        if (cmd.Equals("screenshot", StringComparison.OrdinalIgnoreCase)
            || cmd.StartsWith("screenshot ", StringComparison.OrdinalIgnoreCase))
        {
            _interp.ExecuteLine(cmd);            // identical dispatch to a console-typed `screenshot`
            GetViewport().SetInputAsHandled();   // don't let NetGame's gameplay-bind path fire it again
        }
    }

    /// <summary><c>screenshot [filename]</c> — DP <c>SCR_ScreenShot_f</c>. An explicit filename must end in
    /// <c>.jpg</c>/<c>.tga</c>/<c>.png</c> (validated up-front so the error prints immediately, before any frame is
    /// grabbed, exactly like DP); otherwise the name + format come from the <c>scr_screenshot_*</c> cvars.</summary>
    private void OnScreenshotCommand(IReadOnlyList<string> argv)
    {
        string? explicitName = argv.Count >= 2 ? argv[1] : null;
        if (explicitName is not null && !ScreenshotNamer.TryFormatFromExtension(explicitName, out _))
        {
            Log.Info("screenshot: supplied filename must end in .jpg or .tga or .png");
            return;
        }
        CaptureNextFrame(explicitName);
    }

    /// <summary>
    /// Capture the next rendered frame and write it. DP reads the just-drawn backbuffer; we await
    /// <see cref="RenderingServer.SignalName.FramePostDraw"/> so the viewport texture is the freshly composited
    /// frame (the world + HUD + any open console/menu), not a stale/blank one — the same gate the dev
    /// <c>--screenshot</c> capture (<see cref="ScreenshotHook"/>) uses.
    /// </summary>
    private async void CaptureNextFrame(string? explicitName)
    {
        try
        {
            // Hide any lingering toast from a previous shot BEFORE the frame we're about to grab draws, so the
            // confirmation text is never baked into the screenshot itself.
            if (_toast is not null && GodotObject.IsInstanceValid(_toast))
                _toast.Visible = false;

            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!IsInsideTree() || _cvars is null)
                return;

            Image? img = GetViewport()?.GetTexture()?.GetImage();
            if (img is null)
            {
                Log.Warn("screenshot: viewport image unavailable (running headless?).");
                return;
            }

            CvarService c = _cvars;

            // Format: an explicit arg's extension wins; otherwise the jpeg/png cvars (DP precedence jpeg > png > tga).
            ScreenshotFormat fmt;
            if (explicitName is not null)
                ScreenshotNamer.TryFormatFromExtension(explicitName, out fmt);
            else
                fmt = ScreenshotNamer.ResolveFormat(
                    c.GetFloat("scr_screenshot_jpeg") != 0f,
                    c.GetFloat("scr_screenshot_png") != 0f);

            // Path: the explicit name verbatim, else DP's auto-naming (timestamp or sequential).
            string? relPath = explicitName ?? BuildAutoName(c, fmt);
            if (relPath is null)
                return; // counter overflow — BuildAutoName already logged the DP message

            string absPath = ResolveWritePath(relPath); // UserPaths.Resolve creates the screenshots/ dir as needed

            img = PostProcess(img, c);
            if (Save(img, absPath, fmt, c))
            {
                Log.Info($"Wrote {absPath}");                 // DP "Wrote <file>" (absolute, so it's findable)
                ShowToast($"Wrote {System.IO.Path.GetFileName(absPath)}");
            }
            else
            {
                Log.Warn($"screenshot: unable to write {absPath}");
                ShowToast("Screenshot failed — see console");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"screenshot failed: {ex.Message}");
            ShowToast("Screenshot failed — see console");
        }
    }

    // =====================================================================================================
    //  on-screen confirmation toast (visible regardless of console state / frame-profiler log spam)
    // =====================================================================================================

    /// <summary>Build the toast: a top-centered, outlined label on its own high CanvasLayer (above HUD/menu), so a
    /// screenshot confirmation is readable over any frame. Always-process so it shows while the menu pauses the tree.</summary>
    private void BuildToast()
    {
        var layer = new CanvasLayer { Name = "ScreenshotToast", Layer = 200, ProcessMode = ProcessModeEnum.Always };
        _toast = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0f,
            AnchorRight = 1f,
            OffsetTop = 28f,
        };
        // White text with a thick black outline so it reads over both bright and dark scenes.
        _toast.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        _toast.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        _toast.AddThemeConstantOverride("outline_size", 5);
        _toast.AddThemeFontSizeOverride("font_size", 18);
        layer.AddChild(_toast);
        AddChild(layer);
    }

    /// <summary>Flash <paramref name="msg"/> on screen for a couple of seconds. The newest toast wins (the token
    /// guard stops an earlier shot's timer from hiding a later one).</summary>
    private void ShowToast(string msg)
    {
        if (_toast is null || !GodotObject.IsInstanceValid(_toast))
            return;
        _toast.Text = msg;
        _toast.Visible = true;
        int token = ++_toastToken;
        // processAlways so the hide still fires while the menu has the tree paused.
        GetTree().CreateTimer(2.5, processAlways: true).Timeout += () =>
        {
            if (token == _toastToken && _toast is not null && GodotObject.IsInstanceValid(_toast))
                _toast.Visible = false;
        };
    }

    /// <summary>DP's two auto-naming modes (cl_screen.c), selected by <c>scr_screenshot_timestamp</c>. Returns a
    /// write-root-relative path (<c>screenshots/…</c>), or null on counter overflow (logged with the DP message).</summary>
    private string? BuildAutoName(CvarService c, ScreenshotFormat fmt)
    {
        string name = c.GetString("scr_screenshot_name");
        if (string.IsNullOrEmpty(name))
            name = "xonotic";

        // Probe existence against the real on-disk write root (user://screenshots/...).
        Func<string, bool> exists = rel => System.IO.File.Exists(ResolveWritePath(rel));

        if (c.GetFloat("scr_screenshot_timestamp") != 0f)
        {
            // DP runs scr_screenshot_name through strftime too; for the default (no % escapes) it's a literal prefix.
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string? rel = ScreenshotNamer.NextTimestamped(name + stamp, fmt, exists);
            if (rel is null)
                Log.Info("Couldn't create the image file - already 100 shots taken this second!");
            return rel;
        }

        string? seq = _namer.NextSequential(name, fmt, exists);
        if (seq is null)
            Log.Info("Couldn't create the image file - you already have 1000000 screenshots!");
        return seq;
    }

    /// <summary>Resolve a write-root-relative path (<c>screenshots/…</c>, or an explicit arg) to an absolute OS path
    /// under the per-user write dir via <see cref="UserPaths"/> (<c>~/XonData</c>), the single place this port keeps
    /// user-writable data (config.cfg, favorites, caches). Creates the parent directory as a side effect.</summary>
    private static string ResolveWritePath(string rel) => UserPaths.Resolve(rel);

    // =====================================================================================================
    //  post-processing (DP gammaboost + alpha) and encoding
    // =====================================================================================================

    /// <summary>Apply DP's optional gamma boost and the alpha-channel policy, returning an image ready to encode.</summary>
    private static Image PostProcess(Image img, CvarService c)
    {
        // Normalise to an 8-bit RGB(A) layout we can read/encode (a viewport image is usually Rgba8 already).
        Image.Format f = img.GetFormat();
        if (f != Image.Format.Rgb8 && f != Image.Format.Rgba8)
        {
            img.Convert(Image.Format.Rgba8);
            f = Image.Format.Rgba8;
        }

        // scr_screenshot_gammaboost: out = in ^ (1/boost). 1.0 (the default) writes the frame unmodified.
        float boost = c.GetFloat("scr_screenshot_gammaboost");
        if (boost > 0f && Mathf.Abs(boost - 1f) > 0.0001f)
            img = ApplyGamma(img, boost);

        // scr_screenshot_alpha 0 (default): drop the alpha channel (DP writes RGB unless alpha debugging is on).
        if (c.GetFloat("scr_screenshot_alpha") == 0f && img.GetFormat() == Image.Format.Rgba8)
            img.Convert(Image.Format.Rgb8);

        return img;
    }

    /// <summary>DP's screenshot gamma decode (cl_screen.c): a 256-entry LUT <c>out = pow(in/255, 1/boost)*255</c>
    /// applied to the colour channels (alpha untouched). Only runs when <c>scr_screenshot_gammaboost != 1</c>.</summary>
    private static Image ApplyGamma(Image img, float boost)
    {
        float inv = 1f / boost;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Mathf.Clamp(Mathf.Pow(i / 255f, inv) * 255f + 0.5f, 0f, 255f);

        byte[] data = img.GetData();
        int stride = img.GetFormat() == Image.Format.Rgba8 ? 4 : 3;
        for (int i = 0; i + 2 < data.Length; i += stride)
        {
            data[i] = lut[data[i]];
            data[i + 1] = lut[data[i + 1]];
            data[i + 2] = lut[data[i + 2]];
        }
        return Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, img.GetFormat(), data);
    }

    /// <summary>Encode + write the image in the chosen format. Godot writes JPEG/PNG natively; Targa goes through
    /// <see cref="SaveTga"/> (DP's default-fallback format, which Godot's <see cref="Image"/> can't write).</summary>
    private static bool Save(Image img, string absPath, ScreenshotFormat fmt, CvarService c)
    {
        switch (fmt)
        {
            case ScreenshotFormat.Jpeg:
                float q = c.GetFloat("scr_screenshot_jpeg_quality");
                if (q <= 0f)
                    q = 0.9f;
                return img.SaveJpg(absPath, Mathf.Clamp(q, 0.01f, 1f)) == Error.Ok;
            case ScreenshotFormat.Png:
                return img.SavePng(absPath) == Error.Ok;
            default:
                return SaveTga(img, absPath);
        }
    }

    /// <summary>Write a minimal uncompressed true-colour Targa (type 2), top-left origin, BGR(A) byte order — the
    /// format DP falls back to when both the jpeg and png cvars are off. Honors the alpha channel left on the image
    /// by <see cref="PostProcess"/> (24-bit when alpha was dropped, 32-bit when <c>scr_screenshot_alpha</c> is set).</summary>
    private static bool SaveTga(Image img, string absPath)
    {
        try
        {
            Image.Format f = img.GetFormat();
            if (f != Image.Format.Rgb8 && f != Image.Format.Rgba8)
            {
                img.Convert(Image.Format.Rgba8);
                f = Image.Format.Rgba8;
            }
            bool hasAlpha = f == Image.Format.Rgba8;
            int w = img.GetWidth(), h = img.GetHeight();
            byte[] src = img.GetData();
            int stride = hasAlpha ? 4 : 3;

            var buf = new byte[18 + w * h * stride];
            buf[2] = 2;                                  // image type: uncompressed true-colour
            buf[12] = (byte)(w & 0xFF); buf[13] = (byte)((w >> 8) & 0xFF);
            buf[14] = (byte)(h & 0xFF); buf[15] = (byte)((h >> 8) & 0xFF);
            buf[16] = (byte)(hasAlpha ? 32 : 24);        // bits per pixel
            buf[17] = (byte)(hasAlpha ? 0x28 : 0x20);    // descriptor: top-left origin (0x20) + 8 alpha bits (0x08)

            int o = 18;
            for (int i = 0; i < w * h; i++)
            {
                int s = i * stride;
                buf[o++] = src[s + 2];                   // B
                buf[o++] = src[s + 1];                   // G
                buf[o++] = src[s + 0];                   // R
                if (hasAlpha)
                    buf[o++] = src[s + 3];               // A
            }
            System.IO.File.WriteAllBytes(absPath, buf);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"screenshot: TGA write failed: {ex.Message}");
            return false;
        }
    }
}
