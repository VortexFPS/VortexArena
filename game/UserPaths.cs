using Godot;

namespace XonoticGodot.Game;

/// <summary>
/// Single source of truth for the per-user <em>writable</em> data directory — the user's config + keybinds
/// (<c>config.cfg</c>), menu settings (<c>settings.cfg</c>), server favorites (<c>favorites.cfg</c>), the
/// regenerable particle SDF cache (<c>sdfcache/</c>), and the profiler dumps. The counterpart to
/// <see cref="DataPaths"/>, which resolves the read-only content gamedir.
///
/// <para><b>Why this exists.</b> Godot's <c>user://</c> resolves to a hidden, platform-specific app-data
/// location (<c>%APPDATA%\Godot\app_userdata\XonoticGodot</c> on Windows, <c>~/.local/share/godot/…</c> on
/// Linux, <c>~/Library/Application Support/Godot/…</c> on macOS) that a player can't easily find. We instead
/// store everything under a single, discoverable home-directory subfolder: <c>~/XonData</c>. Set the
/// <c>VORTEX_USERDIR</c> environment variable to an absolute path to override it — used by tests/CI to keep
/// the real home directory clean.</para>
///
/// <para>Every call site that previously used a <c>user://…</c> path now resolves through <see cref="Resolve"/>,
/// which returns an absolute OS path. Godot's <see cref="FileAccess"/>/<see cref="ConfigFile"/>/
/// <see cref="DirAccess"/> and the .NET <c>System.IO</c> APIs all accept absolute paths, so the call sites need
/// only swap the path string.</para>
/// </summary>
public static class UserPaths
{
    /// <summary>The environment variable that overrides the base dir (absolute path). Empty/unset → <c>~/XonData</c>.</summary>
    public const string OverrideEnvVar = "VORTEX_USERDIR";

    /// <summary>The default subfolder name created under the OS home directory.</summary>
    public const string DefaultFolderName = "XonData";

    private static string? _baseDir;

    /// <summary>
    /// The absolute base directory all user data lives under — <c>~/XonData</c> by default, or the
    /// <c>VORTEX_USERDIR</c> override when set. Computed once and cached; the directory is created on first
    /// access so the very first save (config.cfg etc., which Godot's writers do NOT auto-create a parent for)
    /// always has somewhere to land.
    /// </summary>
    public static string BaseDir
    {
        get
        {
            if (_baseDir is not null)
                return _baseDir;

            string? overridden = System.Environment.GetEnvironmentVariable(OverrideEnvVar);
            string b = string.IsNullOrWhiteSpace(overridden)
                ? System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    DefaultFolderName)
                : overridden.Trim();

            _baseDir = Normalize(System.IO.Path.GetFullPath(b));
            EnsureDir(_baseDir);
            return _baseDir;
        }
    }

    /// <summary>
    /// Resolve a relative user-data path (e.g. <c>"config.cfg"</c>, <c>"sdfcache/foo.psdf"</c>) to an absolute
    /// OS path under <see cref="BaseDir"/>, creating the parent directory if it doesn't exist yet. Forward
    /// slashes in <paramref name="rel"/> are fine on every platform.
    /// </summary>
    public static string Resolve(string rel)
    {
        string full = Normalize(System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseDir, rel)));
        EnsureDir(System.IO.Path.GetDirectoryName(full));
        return full;
    }

    /// <summary>
    /// Use forward slashes on every platform — the form Godot's <see cref="ProjectSettings.GlobalizePath"/>
    /// emits and that both Godot's file APIs and .NET's <c>System.IO</c> accept on Windows (where
    /// <see cref="System.IO.Path.GetFullPath"/> would otherwise hand back backslashes).
    /// </summary>
    private static string Normalize(string p) => p.Replace('\\', '/');

    /// <summary>
    /// One-time, best-effort migration of the user's data from Godot's legacy <c>user://</c> location into
    /// <see cref="BaseDir"/>. Copies each known config file only when the destination doesn't already exist, so
    /// it's a no-op after the first run (and after the first in-app save). Idempotent and never throws — a
    /// failed copy just leaves the player at registered defaults, which is exactly the pre-migration behaviour.
    ///
    /// <para>Regenerable artifacts (the SDF cache, profiler dumps) are intentionally NOT migrated; they rebuild
    /// on demand in the new location. Call once at boot, before <c>config.cfg</c> is read.</para>
    /// </summary>
    public static void MigrateLegacyUserData()
    {
        string[] files = { "config.cfg", "settings.cfg", "favorites.cfg" };
        foreach (string file in files)
        {
            try
            {
                string dest = Resolve(file);
                if (System.IO.File.Exists(dest))
                    continue; // already migrated, or already saved in the new location
                string legacy = ProjectSettings.GlobalizePath("user://" + file);
                if (string.IsNullOrEmpty(legacy) || !System.IO.File.Exists(legacy))
                    continue; // nothing to migrate
                System.IO.File.Copy(legacy, dest);
                GD.Print($"[UserPaths] migrated {file}: {legacy} -> {dest}");
            }
            catch (System.Exception ex)
            {
                GD.PushWarning($"[UserPaths] could not migrate '{file}': {ex.Message}");
            }
        }
    }

    private static void EnsureDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        catch (System.Exception ex)
        {
            GD.PushWarning($"[UserPaths] could not create directory '{dir}': {ex.Message}");
        }
    }
}
