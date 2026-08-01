using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vx;

/// <summary>
/// Locating things, and running things. Everything here has a counterpart in <c>tools/lib/*.sh</c>, and the
/// two MUST agree: a resolver that disagrees with the shell scripts about which Godot or which Python to use
/// would be worse than the hardcoded paths this all replaced, because the disagreement would only surface as
/// two tools reporting different results on one machine. When you change an order here, change it there.
/// </summary>
internal static class Env
{
    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    internal static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// The repo root: walk up from the executable until a directory carrying BOTH <c>project.godot</c> and
    /// <c>tools/lib</c> is found. Two markers rather than one because a lone <c>project.godot</c> also
    /// appears in map/editor fixtures, and being wrong here silently points every check at the wrong tree.
    /// </summary>
    internal static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tools", "lib")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // Fall back to the CWD rather than throwing: doctor's whole job is reporting on a broken tree, so it
        // must still run in one, and every check that needs the root reports its own absence clearly.
        return Directory.GetCurrentDirectory();
    }

    /// <summary>First match for <paramref name="exe"/> on PATH, or null. The equivalent of `command -v`.</summary>
    internal static string? Which(string exe)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        // PATHEXT matters on Windows: `dotnet` on PATH is dotnet.exe, and probing the bare name finds nothing.
        string[] exts = IsWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                string candidate = Path.Combine(dir.Trim('"'), exe + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Run a command and capture it. Never throws for a non-zero exit — that is data, not an error.</summary>
    internal static (int Code, string Out, string Err) Run(string exe, params string[] args)
        => Run(exe, TimeSpan.FromSeconds(30), args);

    internal static (int Code, string Out, string Err) Run(string exe, TimeSpan timeout, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "could not start");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            // A bounded wait, because doctor must never be the thing that hangs. This is also the portable
            // `timeout` that tools/lib/run-timeout.sh has to hand-roll in sh — see that file's header for
            // why the shell side is harder than it looks.
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (-1, stdout, "timed out");
            }
            return (p.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Godot, resolved in the SAME order as <c>tools/lib/find-godot.sh</c>: <c>$GODOT</c> (verbatim, and a
    /// set-but-missing value deliberately does NOT fall through), then <c>.godot-bin/</c>, then PATH, then
    /// the platform's usual install location. Windows prefers the console build, whose stdout is capturable.
    /// </summary>
    internal static string? FindGodot()
    {
        string? env = Environment.GetEnvironmentVariable("GODOT");
        if (!string.IsNullOrEmpty(env))
            return File.Exists(env) ? env : null;

        string bin = Path.Combine(RepoRoot, ".godot-bin");
        foreach (string c in new[]
                 {
                     Path.Combine(bin, "godot_console.exe"), Path.Combine(bin, "godot.exe"),
                     Path.Combine(bin, "Godot.app", "Contents", "MacOS", "Godot"), Path.Combine(bin, "godot"),
                 })
            if (File.Exists(c)) return c;

        foreach (string n in new[] { "godot4", "godot", "Godot", "godot-mono" })
            if (Which(n) is { } hit) return hit;

        string[] platform = IsMacOS
            ? new[]
            {
                "/Applications/Godot_mono.app/Contents/MacOS/Godot",
                "/Applications/Godot.app/Contents/MacOS/Godot",
            }
            : IsWindows
                ? new[]
                {
                    @"C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe",
                    @"C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64.exe",
                }
                : new[] { "/usr/local/bin/godot", "/usr/bin/godot" };
        foreach (string c in platform)
            if (File.Exists(c)) return c;

        return null;
    }

    /// <summary>
    /// Python 3, resolved as <c>tools/lib/find-python.sh</c> does: <c>$PYTHON</c>, then <c>python3</c>, then
    /// <c>python</c> — checking the version rather than trusting the name, since neither spelling is portable
    /// (no <c>python</c> on macOS 12.3+, no <c>python3</c> under the python.org Windows install).
    /// </summary>
    internal static string? FindPython()
    {
        string? env = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrEmpty(env))
            return IsPython3(env) ? env : null;
        foreach (string n in new[] { "python3", "python" })
            if (Which(n) is { } hit && IsPython3(hit)) return hit;
        return null;
    }

    private static bool IsPython3(string exe)
        => Run(exe, "-c", "import sys; sys.exit(0 if sys.version_info[:2] >= (3, 8) else 1)").Code == 0;
}
