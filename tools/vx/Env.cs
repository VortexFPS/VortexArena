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
    /// The host CPU architecture, in the spelling this tree uses for ANYTHING A HUMAN OR THE LAUNCHER
    /// SEES — zip suffixes, manifest platform keys, dist/ directories, the godot.lock.json platform key.
    ///
    /// <para><b>There are two spellings and they are not interchangeable.</b> Godot's build system names
    /// 64-bit PowerPC <c>ppc64</c> (its SConstruct aliases <c>ppc64le</c> to that, and the engine is
    /// little-endian only), so the scons flag, the engine binary's filename and a preset's
    /// <c>binary_format/architecture</c> must all say <c>ppc64</c>. Everything else — .NET's runtime
    /// identifier, <c>uname -m</c>, every distro's package name — says <c>ppc64le</c>, which is the
    /// better public name because it states the endianness that <c>ppc64</c> only implies. This property
    /// returns the PUBLIC spelling; <see cref="GodotArch"/> converts to the engine's.</para>
    ///
    /// <para>Unknown architectures return the lowercased runtime name rather than throwing: a machine vx
    /// has never seen should get a report that names it, not an exception.</para>
    /// </summary>
    internal static string HostArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86_32",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm32",
        Architecture.Ppc64le => "ppc64le",
        Architecture.LoongArch64 => "loongarch64",
        // RiscV64 is deliberately absent: the enum member does not exist on net8.0, which is the TFM
        // this tool is pinned to, and the fallback already yields "riscv64" for it.
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>The engine's spelling of <see cref="HostArch"/> — see that property for why they differ.</summary>
    internal static string GodotArch => HostArch == "ppc64le" ? "ppc64" : HostArch;

    /// <summary>
    /// True when this tree publishes a prebuilt engine and release artifacts for the host architecture.
    /// Everything else is a from-source machine: <c>tools/build-engine.sh</c> is the only way to get an
    /// engine onto it, because upstream Godot publishes no binary for it.
    /// </summary>
    internal static bool HostArchHasPrebuiltEngine => HostArch is "x86_64" or "arm64";

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

    // ---- sizes and space --------------------------------------------------------------------------------

    /// <summary>
    /// Bytes as a person reads them. Decimal units (MB = 10^6), deliberately: every size vx quotes comes from
    /// a lockfile that recorded it to compare against a download, and download sizes are quoted in decimal by
    /// every browser, CDN and package manager a reader will have seen. Quoting 666 MB where curl said 699 MB
    /// would look like a different file.
    /// </summary>
    internal static string HumanBytes(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes < 1_000) return $"{bytes} B";
        if (bytes < 1_000_000) return $"{bytes / 1e3:F0} kB";
        if (bytes < 1_000_000_000) return $"{bytes / 1e6:F0} MB";
        return $"{bytes / 1e9:F1} GB";
    }

    /// <summary>
    /// Free bytes on the volume holding <paramref name="path"/>, or null when it cannot be determined.
    ///
    /// <para>Walks UP to the nearest existing directory first: the thing being asked about is usually where a
    /// download is ABOUT to go, so it does not exist yet, and asking the OS about a non-existent path answers
    /// nothing useful. Null rather than an exception or a zero — callers treat "unknown" as "do not
    /// block", because a space check must never be the reason a working machine refuses to fetch.</para>
    /// </summary>
    internal static long? FreeSpace(string path)
    {
        try
        {
            string? probe = Path.GetFullPath(path);
            while (probe is not null && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);
            if (probe is null) return null;
            return new DriveInfo(Path.GetPathRoot(probe)!).AvailableFreeSpace;
        }
        catch { return null; }
    }

    /// <summary>
    /// One line describing what a fetch will cost and whether it fits: the size, the space left, and a plain
    /// warning when it is close. Returns null when free space is unknown, so callers can print just the size.
    ///
    /// <para><paramref name="headroom"/> is the multiple of <paramref name="needed"/> that must be free before
    /// this stays quiet. It defaults above 1.0 because "exactly enough" is not enough: an archive that is
    /// unpacked needs the archive AND its contents on disk at once, and a filesystem at 100% fails in ways
    /// that have nothing to do with the download.</para>
    /// </summary>
    internal static string? SpaceNote(long needed, string destination, double headroom = 1.3)
    {
        if (FreeSpace(destination) is not { } free) return null;
        string sizes = $"{HumanBytes(needed)} needed, {HumanBytes(free)} free";
        if (free < needed)
            return $"{sizes} — NOT ENOUGH SPACE; this will fail partway";
        if (free < (long)(needed * headroom))
            return $"{sizes} — that is tight; unpacking needs room for the archive and its contents";
        return sizes;
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
    /// Run a command with the caller's stdio attached (no capture) and return its exit code. This is what
    /// the delegating commands use: the wrapped script's own output IS the output, so vx never reformats or
    /// swallows it.
    /// </summary>
    internal static int Exec(string exe, IEnumerable<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDir ?? RepoRoot };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process? p = Process.Start(psi);
        if (p is null) { Console.Error.WriteLine($"vx: could not start {exe}"); return 127; }
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>
    /// Run a command, mirroring its output live, and STOP WAITING once <paramref name="doneMarker"/> appears
    /// — killing it if it has not exited <paramref name="graceSeconds"/> later.
    ///
    /// <para><b>This exists for exactly one reason: Godot's headless <c>--export-release</c> does not
    /// reliably exit.</b> It prints <c>[ DONE ] savepack</c>, flushes the binary, and then sits there forever
    /// on a lingering render/.NET thread. Verified still true on 4.6.3 (2026-08-03): a windows-client export
    /// wrote its .exe at 17:33:57 and was still running, doing nothing, at 17:36:19 — a plain
    /// <see cref="Exec"/> hangs until the caller gives up. It ALSO exits non-zero on fully successful
    /// exports, which is why <c>vx export</c> gates on the artifact rather than the code.</para>
    ///
    /// <para>Ported from <c>run-release.sh</c>, which carried this workaround for months while
    /// <c>vx export</c> — the command anyone would actually reach for — did not have it. Match the marker
    /// LOOSELY (<c>DONE.*savepack</c>): Godot colorises it, so there are ANSI escapes between the <c>]</c>
    /// and the word, and a too-strict pattern degrades silently into "hangs until the cap".</para>
    /// </summary>
    internal static int ExecUntilDone(string exe, IEnumerable<string> args, string doneMarker,
                                      int graceSeconds = 2, int capSeconds = 600, string? settleDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process? p = Process.Start(psi);
        if (p is null) { Console.Error.WriteLine($"vx: could not start {exe}"); return 127; }

        var done = new ManualResetEventSlim(false);
        var marker = new System.Text.RegularExpressions.Regex(doneMarker,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        void OnLine(string? line)
        {
            if (line is null) return;
            Console.WriteLine(line);                       // the tool's own output IS the output
            if (marker.IsMatch(line)) done.Set();
        }

        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Whichever happens first: it exits on its own (fine), or it announces success and stops mattering.
        int cap = capSeconds * 1000;
        int waited = 0;
        while (!p.HasExited && waited < cap)
        {
            if (done.Wait(250)) break;
            waited += 250;
        }

        if (!p.HasExited)
        {
            if (done.IsSet)
            {
                // Let it finish flushing before the kill. A FIXED grace is not enough for an export:
                // Godot's .NET export copies the whole assembly set (185+ dlls) into
                // data_<app>_<platform>/ AFTER the savepack marker, and killing 2 s in truncated that
                // copy nondeterministically — a release build missing System.Security.Cryptography.dll
                // (StartListenServer died at MasterAnnounce) with a leftover VortexArena.tmp beside the
                // exe. So when the caller names the artifact dir, wait until it QUIESCES: no *.tmp
                // remains and nothing has been (re)written for graceSeconds, capped at 120 s.
                if (settleDir is not null && Directory.Exists(settleDir))
                {
                    var settle = System.Diagnostics.Stopwatch.StartNew();
                    long last = DirNewestWriteTicks(settleDir);
                    int stableMs = 0;
                    while (settle.Elapsed.TotalSeconds < 120 && !p.HasExited)
                    {
                        Thread.Sleep(500);
                        long now = DirNewestWriteTicks(settleDir);
                        bool tmpLeft = Directory.EnumerateFiles(settleDir, "*.tmp", SearchOption.AllDirectories).Any();
                        if (!tmpLeft && now == last)
                        {
                            stableMs += 500;
                            if (stableMs >= graceSeconds * 1000) break;
                        }
                        else
                        {
                            stableMs = 0;
                            last = now;
                        }
                    }
                }
                else
                {
                    Thread.Sleep(graceSeconds * 1000);
                }
            }
            try { p.Kill(entireProcessTree: true); } catch { /* raced us to the exit */ }
        }
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
    }

    /// <summary>Newest LastWriteTimeUtc (ticks) across every file under <paramref name="dir"/> — the
    /// change detector for <see cref="ExecUntilDone"/>'s settle wait. 0 for an empty/unreadable tree.</summary>
    private static long DirNewestWriteTicks(string dir)
    {
        long newest = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                long t = File.GetLastWriteTimeUtc(f).Ticks;
                if (t > newest) newest = t;
            }
        }
        catch { /* mid-copy races are exactly what the next poll is for */ }
        return newest;
    }

    /// <summary>
    /// Run one of the repo's shell scripts. On Windows that needs a bash — Git Bash, which this tree
    /// already requires (ci/ci.sh is bash-only, and the .run/ Rider configs drive ./vx through it). Finding
    /// it explicitly rather than assuming <c>bash</c> is on PATH keeps the failure legible: "install Git for
    /// Windows" is actionable where "the system cannot find the file specified" is not.
    /// </summary>
    internal static int Bash(string scriptRelPath, IEnumerable<string> args)
    {
        string script = Path.Combine(RepoRoot, scriptRelPath);
        if (!File.Exists(script))
        {
            Console.Error.WriteLine($"vx: missing {scriptRelPath}");
            return 1;
        }

        string? bash = Which("bash");
        if (bash is null && IsWindows)
            foreach (string c in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe" })
                if (File.Exists(c)) { bash = c; break; }

        if (bash is null)
        {
            Console.Error.WriteLine($"vx: {scriptRelPath} needs bash.");
            Console.Error.WriteLine(IsWindows
                ? "    Install Git for Windows (it provides Git Bash): https://git-scm.com/download/win"
                : "    No bash on PATH — install it via your package manager.");
            return 1;
        }

        var argv = new List<string> { script };
        argv.AddRange(args);
        return Exec(bash, argv);
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
