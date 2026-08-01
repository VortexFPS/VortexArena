using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vx.Commands;

internal enum Status { Ok, Warn, Missing }

/// <param name="Required">
/// True when a fresh clone cannot be built or tested without it. Only these decide the exit code — Godot is
/// not required to build and test, so a machine without it is diagnosed, not failed.
/// </param>
internal sealed record Check(string Name, Status Status, string Detail, bool Required = false, string? Fix = null);

/// <summary>
/// <c>vx doctor</c> — say what is installed, what is missing, and what to do about it. CHANGES NOTHING, by
/// design: this is the command you want six months from now when a build breaks, and a diagnostic that also
/// mutates the machine is one you hesitate to run. <c>vx setup</c> is "doctor, plus act on what it found".
///
/// <para>It is also what the macOS/Windows/Linux CI jobs should run, because cross-platform rot is invisible
/// when the dev box is one OS and CI is another — which is precisely how four separate portability bugs
/// survived in this repo until 2026-08-01.</para>
/// </summary>
internal static class Doctor
{
    /// <summary>
    /// Bumped when the --json shape changes incompatibly. VortexLauncher consumes this ACROSS A REPO
    /// BOUNDARY, so a breaking change here is a breaking change, not an implementation detail.
    /// </summary>
    private const int JsonSchemaVersion = 1;

    internal static int Run(string[] args, bool json)
    {
        var checks = new List<Check>();
        checks.AddRange(Toolchain());
        checks.AddRange(Content());

        if (json) EmitJson(checks); else EmitText(checks);

        // Only REQUIRED failures are fatal. A warning must never fail the command, or every CI job that runs
        // doctor ends up with `|| true` appended and the whole thing stops being read.
        return checks.Any(c => c.Required && c.Status == Status.Missing) ? 1 : 0;
    }

    // ---------------------------------------------------------------------------------------------------

    private static IEnumerable<Check> Toolchain()
    {
        // --- .NET: the one genuinely unavoidable dependency ------------------------------------------
        string? dotnet = Env.Which("dotnet");
        if (dotnet is null)
        {
            yield return new Check(".NET SDK", Status.Missing, "not on PATH", Required: true,
                Fix: "https://dotnet.microsoft.com/download  (8.0 or newer; global.json pins 8.0.0 rollForward latestMajor)");
        }
        else
        {
            var sdks = Env.Run(dotnet, "--list-sdks");
            string newest = sdks.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split(' ')[0] ?? "unknown";
            bool ok = int.TryParse(newest.Split('.')[0], out int major) && major >= 8;
            yield return new Check(".NET SDK", ok ? Status.Ok : Status.Missing, $"{newest}  ({dotnet})",
                Required: true, Fix: ok ? null : "need 8.0 or newer — https://dotnet.microsoft.com/download");
        }

        // --- Python: still required until the fetchers migrate (plan, stage 2) -----------------------
        string? py = Env.FindPython();
        if (py is null)
        {
            string envSet = Environment.GetEnvironmentVariable("PYTHON") is { Length: > 0 } p
                ? $"$PYTHON is set to '{p}' but is not a working Python >= 3.8"
                : "not found (tried python3, python)";
            yield return new Check("Python 3", Status.Missing, envSet, Required: true,
                Fix: Env.IsMacOS ? "xcode-select --install"
                    : Env.IsWindows ? "https://www.python.org/downloads/  (tick 'Add python.exe to PATH')"
                    : "apt/dnf/pacman install python3");
        }
        else
        {
            string ver = Env.Run(py, "-c", "import sys;print('.'.join(map(str,sys.version_info[:3])))").Out;
            yield return new Check("Python 3", Status.Ok, $"{ver}  ({py})", Required: true);

            // python.org's macOS installer ships its own OpenSSL that ignores the system keychain, so
            // urllib's HTTPS fails until Install Certificates.command has been run once.
            //
            // Downgraded to a WARNING once `vx maps` and `vx engine` were ported to HttpClient (2026-08-01):
            // the bootstrap no longer depends on it. It still matters for `vx maps --rebuild`, which drives
            // the maps-src pipeline, and for anyone running the tools/*.py scripts directly — and the
            // symptom (CERTIFICATE_VERIFY_FAILED, four retries deep inside a fetcher) points nowhere near
            // the cause, which is why it is worth naming at all. Apple's /usr/bin/python3 is unaffected.
            var ssl = Env.Run(py, TimeSpan.FromSeconds(20),
                "-c", "import urllib.request as u; u.urlopen('https://api.github.com', timeout=10); print('ok')");
            if (ssl.Code != 0 && ssl.Err.Contains("CERTIFICATE_VERIFY_FAILED"))
                yield return new Check("Python TLS trust", Status.Warn,
                    "urllib cannot verify HTTPS — affects 'vx maps --rebuild' and the tools/*.py scripts run "
                    + "directly (vx maps / vx engine no longer use them)",
                    Fix: Env.IsMacOS
                        ? "run '/Applications/Python 3.x/Install Certificates.command', or: export PYTHON=/usr/bin/python3"
                        : "this interpreter has no usable CA bundle; try a distro python3");
            else if (ssl.Code != 0)
                yield return new Check("Python TLS trust", Status.Warn,
                    $"could not verify (offline?): {Truncate(ssl.Err, 60)}");
            else
                yield return new Check("Python TLS trust", Status.Ok, "HTTPS verified");
        }

        // --- git -------------------------------------------------------------------------------------
        string? git = Env.Which("git");
        yield return git is null
            ? new Check("git", Status.Missing, "not on PATH", Required: true, Fix: "install git")
            : new Check("git", Status.Ok, Env.Run(git, "--version").Out);

        // --- Godot: needed to RUN or EXPORT, not to build or test ------------------------------------
        string? godot = Env.FindGodot();
        if (godot is null)
        {
            string envSet = Environment.GetEnvironmentVariable("GODOT") is { Length: > 0 } g
                ? $"$GODOT is set to '{g}', which does not exist"
                : "not found (tried $GODOT, .godot-bin/, PATH, platform install dir)";
            yield return new Check("Godot 4.6.3 (mono)", Status.Warn, envSet,
                Fix: "needed only to run/export/smoke — './vx setup' will install to .godot-bin/");
        }
        else
        {
            var v = Env.Run(godot, TimeSpan.FromSeconds(20), "--version");
            bool mono = v.Out.Contains("mono", StringComparison.OrdinalIgnoreCase);
            yield return new Check("Godot 4.6.3 (mono)", mono ? Status.Ok : Status.Warn,
                $"{(v.Out.Length > 0 ? v.Out.Split('\n')[0] : "?")}  ({godot})",
                Fix: mono ? null : "this is NOT a .NET/mono build — it cannot run the game's C#");
        }

        // --- portable timeout: which implementation run-timeout.sh will pick --------------------------
        string? to = Env.Which("timeout") ?? Env.Which("gtimeout");
        yield return new Check("timeout", to is null ? Status.Warn : Status.Ok,
            to ?? "absent — tools/lib/run-timeout.sh uses its shell fallback",
            Fix: to is null && Env.IsMacOS ? "optional: brew install coreutils (provides gtimeout)" : null);

        // --- helpers the fetch/package paths shell out to ---------------------------------------------
        foreach (string t in new[] { "curl", "unzip" })
        {
            string? hit = Env.Which(t);
            yield return new Check(t, hit is null ? Status.Warn : Status.Ok, hit ?? "not on PATH");
        }
    }

    private static IEnumerable<Check> Content()
    {
        string root = Env.RepoRoot;

        yield return Directory.Exists(Path.Combine(root, "data"))
            ? new Check("data/ (core content)", Status.Ok, "present — committed, not a download")
            : new Check("data/ (core content)", Status.Missing, "absent — this checkout is broken", Required: true,
                Fix: "core content is COMMITTED; re-clone rather than fetch");

        string maps = Path.Combine(root, "data", "maps");
        int packs = Directory.Exists(maps) ? Directory.GetFiles(maps, "*.pk3").Length : 0;
        yield return packs > 0
            ? new Check("data/maps/ (compiled maps)", Status.Ok, $"{packs} pack(s)")
            : new Check("data/maps/ (compiled maps)", Status.Warn,
                "none — map-dependent tests self-skip and the host smoke cannot run",
                Fix: "$PYTHON tools/data/fetch-maps.py     (or --rebuild to compile from source)");

        // The repo-local engine install find-godot.sh probes before PATH.
        string bin = Path.Combine(root, ".godot-bin");
        yield return new Check(".godot-bin/", Directory.Exists(bin) ? Status.Ok : Status.Warn,
            Directory.Exists(bin) ? "present" : "absent (fine — Godot may be installed system-wide)");
    }

    // ---------------------------------------------------------------------------------------------------

    private static void EmitText(List<Check> checks)
    {
        Console.WriteLine();
        Console.WriteLine($"vx doctor — {Env.RepoRoot}");
        Console.WriteLine();
        int w = checks.Max(c => c.Name.Length);
        foreach (Check c in checks)
        {
            string mark = c.Status switch { Status.Ok => "  ok  ", Status.Warn => " warn ", _ => " MISS " };
            Console.WriteLine($"[{mark}] {c.Name.PadRight(w)}  {c.Detail}");
            if (c.Fix is not null)
                Console.WriteLine($"{new string(' ', w + 11)}→ {c.Fix}");
        }

        Console.WriteLine();
        int blocking = checks.Count(c => c.Required && c.Status == Status.Missing);
        int broken = checks.Count(c => !c.Required && c.Status == Status.Missing);
        int warn = checks.Count(c => c.Status == Status.Warn);

        // Three distinct statements, because collapsing them produces a summary that is not true — a broken
        // TLS trust store stops you FETCHING, it does not stop you building, and saying otherwise sends
        // someone off reinstalling a working .NET.
        if (blocking > 0)
            Console.WriteLine($"{blocking} required item(s) missing — the tree will not build or test until they are installed.");
        else
            Console.WriteLine("Ready to build and test.");

        if (broken > 0)
            Console.WriteLine($"{broken} item(s) present but not working — see the arrows above.");
        if (warn > 0)
            Console.WriteLine($"{warn} optional item(s) unmet; each is only needed for the task named beside it.");
        if (blocking == 0 && broken == 0 && warn == 0)
            Console.WriteLine("Everything checked is present.");
        Console.WriteLine();
    }

    private static void EmitJson(List<Check> checks)
    {
        var arr = new JsonArray();
        foreach (Check c in checks)
            arr.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["status"] = c.Status.ToString().ToLowerInvariant(),
                ["detail"] = c.Detail,
                ["required"] = c.Required,
                ["fix"] = c.Fix,
            });

        var doc = new JsonObject
        {
            ["schema"] = JsonSchemaVersion,
            ["command"] = "doctor",
            ["repoRoot"] = Env.RepoRoot,
            ["ok"] = !checks.Any(c => c.Required && c.Status == Status.Missing),
            ["checks"] = arr,
        };
        Console.WriteLine(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";
}
