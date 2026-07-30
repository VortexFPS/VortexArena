using System;

namespace XonoticGodot.Engine.Console;

/// <summary>The three on-disk image formats DarkPlaces' <c>screenshot</c> command writes (cl_screen.c).</summary>
public enum ScreenshotFormat
{
    /// <summary><c>scr_screenshot_jpeg</c> (the DP/Xonotic default) — <c>.jpg</c>.</summary>
    Jpeg,

    /// <summary><c>scr_screenshot_png</c> — <c>.png</c>.</summary>
    Png,

    /// <summary>Neither jpeg nor png set — uncompressed Targa <c>.tga</c> (DP's last-resort default).</summary>
    Tga,
}

/// <summary>
/// The Godot-free filename algorithm behind the client <c>screenshot</c> command — a faithful port of the naming
/// logic in DarkPlaces' <c>SCR_ScreenShot_f</c> (cl_screen.c). The Godot capture/encode glue lives in
/// <c>XonoticGodot.Game.Client.ScreenshotService</c>; the file-naming + format-selection is isolated here so it
/// is unit-testable without a viewport or a real filesystem (the existence probe is injected).
///
/// <para>DP has two auto-naming modes, selected by <c>scr_screenshot_timestamp</c>:
/// <list type="bullet">
///   <item><b>timestamp</b> (<see cref="NextTimestamped"/>): <c>screenshots/&lt;prefix&gt;&lt;YYYYMMDDHHMMSS&gt;-NN.&lt;ext&gt;</c>,
///         scanning the two-digit suffix <c>00</c>..<c>99</c> for the first free slot this second.</item>
///   <item><b>sequential</b> (<see cref="NextSequential"/>): <c>screenshots/&lt;prefix&gt;NNNNNN.&lt;ext&gt;</c>,
///         a six-digit counter that scans for the first unused number and resets when the prefix changes.</item>
/// </list>
/// In both modes a slot counts as taken if ANY of the three extensions exists at that name (so toggling the
/// format cvar never overwrites an earlier shot) — exactly DP's <c>FS_SysFileExists</c> triple-check.</para>
/// </summary>
public sealed class ScreenshotNamer
{
    // The DP statics in SCR_ScreenShot_f: the running sequential counter and the prefix it belongs to. Kept on the
    // instance so the Godot service holds one namer for the session (avoids rescanning from 0 on every shot).
    private int _shotNumber;
    private string _oldPrefix = "";

    /// <summary>The subdirectory (under the write root) screenshots land in — DP's <c>screenshots/</c>.</summary>
    public const string Dir = "screenshots";

    /// <summary>
    /// DP's format precedence (cl_screen.c:927-928): <c>scr_screenshot_jpeg</c> wins over <c>scr_screenshot_png</c>;
    /// with neither set the format is Targa.
    /// </summary>
    public static ScreenshotFormat ResolveFormat(bool jpeg, bool png)
        => jpeg ? ScreenshotFormat.Jpeg : png ? ScreenshotFormat.Png : ScreenshotFormat.Tga;

    /// <summary>The file extension (no dot) for a format: <c>jpg</c> / <c>png</c> / <c>tga</c>.</summary>
    public static string Extension(ScreenshotFormat fmt) => fmt switch
    {
        ScreenshotFormat.Jpeg => "jpg",
        ScreenshotFormat.Png => "png",
        _ => "tga",
    };

    /// <summary>
    /// DP's explicit-filename branch (cl_screen.c:931-955): a user-supplied <c>screenshot &lt;file&gt;</c> name must
    /// end in <c>.jpg</c>, <c>.tga</c>, or <c>.png</c>, which also picks the format. Returns false for any other
    /// extension (the caller prints "supplied filename must end in .jpg or .tga or .png" and aborts). Case-insensitive.
    /// </summary>
    public static bool TryFormatFromExtension(string filename, out ScreenshotFormat fmt)
    {
        fmt = ScreenshotFormat.Tga;
        if (string.IsNullOrEmpty(filename))
            return false;
        int dot = filename.LastIndexOf('.');
        if (dot < 0)
            return false;
        switch (filename[(dot + 1)..].ToLowerInvariant())
        {
            case "jpg": fmt = ScreenshotFormat.Jpeg; return true;
            case "tga": fmt = ScreenshotFormat.Tga; return true;
            case "png": fmt = ScreenshotFormat.Png; return true;
            default: return false;
        }
    }

    /// <summary>
    /// DP timestamp mode (cl_screen.c:957-981): build <c>screenshots/&lt;prefix&gt;-NN.&lt;ext&gt;</c>, scanning the
    /// two-digit suffix <c>00</c>..<c>99</c> for the first free slot (a slot is free only if none of the three
    /// extensions exist there). Returns null when all 100 are taken — i.e. 100 shots in the same second (DP prints
    /// "already 100 shots taken this second"). <paramref name="exists"/> probes a write-root-relative path.
    /// </summary>
    public static string? NextTimestamped(string prefix, ScreenshotFormat fmt, Func<string, bool> exists)
    {
        for (int n = 0; n < 100; n++)
        {
            string baseName = $"{Dir}/{prefix}-{n:00}";
            if (!TakenAnyExt(baseName, exists))
                return $"{baseName}.{Extension(fmt)}";
        }
        return null;
    }

    /// <summary>
    /// DP sequential mode (cl_screen.c:982-1015): build <c>screenshots/&lt;prefix&gt;NNNNNN.&lt;ext&gt;</c> with a
    /// six-digit counter. The counter is remembered between calls and resets to 0 whenever the prefix changes (DP's
    /// <c>old_prefix_name</c> guard), then advances to the first free number (none of the three extensions present).
    /// Returns null when 1,000,000 shots already exist (DP prints "you already have 1000000 screenshots").
    /// <paramref name="exists"/> probes a write-root-relative path.
    /// </summary>
    public string? NextSequential(string prefix, ScreenshotFormat fmt, Func<string, bool> exists)
    {
        if (!string.Equals(_oldPrefix, prefix, StringComparison.Ordinal))
        {
            _oldPrefix = prefix;
            _shotNumber = 0;
        }
        for (; _shotNumber < 1000000; _shotNumber++)
        {
            string baseName = $"{Dir}/{prefix}{_shotNumber:000000}";
            if (!TakenAnyExt(baseName, exists))
            {
                string file = $"{baseName}.{Extension(fmt)}";
                _shotNumber++; // DP advances past the slot it just claimed
                return file;
            }
        }
        return null;
    }

    /// <summary>A name slot is taken if a <c>.tga</c>, <c>.jpg</c>, OR <c>.png</c> already exists for it (DP's
    /// three <c>FS_SysFileExists</c> checks), so a format change never clobbers an earlier shot.</summary>
    private static bool TakenAnyExt(string baseName, Func<string, bool> exists)
        => exists($"{baseName}.tga") || exists($"{baseName}.jpg") || exists($"{baseName}.png");
}
