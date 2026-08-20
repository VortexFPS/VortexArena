using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vx.Commands;

internal enum Status { Ok, Warn, Missing }

/// <param name="Required">
/// True when a fresh clone cannot be built or tested without it. Only these decide the exit code — Godot is
/// not required to build and test, so a machine without it is diagnosed, not failed.
/// </param>
/// <summary>
/// A runnable answer to a failed check: the command as a person would type it, and the same thing as
/// something vx can call.
///
/// <para>It lives beside the check rather than in a table somewhere else, because the two would drift — a
/// check whose remedy is written elsewhere is one nobody updates when the remedy changes.</para>
/// </summary>
internal sealed record Fixer(string Command, Func<int> Run);

/// <param name="Required">
/// True when a fresh clone cannot be built or tested without it. Only these decide the exit code — Godot is
/// not required to build and test, so a machine without it is diagnosed, not failed.
/// </param>
/// <param name="Fix">The human-readable sentence, for the cases with no runnable answer (and as the note
/// beside one that has).</param>
/// <param name="Repair">
/// How to fix it, runnably, or null when vx cannot. <b>Doctor never calls this</b> — it changes nothing, by
/// design — but <c>vx run</c> offers it as a [Y/n], so someone who just wants to play is walked to a working
/// build instead of reading a diagnosis and retyping it.
/// </param>
/// <param name="Recheck">
/// A CHEAP re-probe of this one item, to confirm a repair worked. Cheap is the point: re-running every
/// doctor check would mean another HTTPS request (the TLS probe) and half a dozen subprocesses, far too much
/// to pay after each fix. Null means "cannot re-check" and the repair's exit code is trusted.
/// </param>
internal sealed record Check(string Name, Status Status, string Detail, bool Required = false, string? Fix = null,
                             Fixer? Repair = null, Func<bool>? Recheck = null);

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

    /// <summary>
    /// Every check, in report order. Exposed so <c>vx run</c> can offer the repairs without keeping a second
    /// list of what can go wrong — see <see cref="Check.Repair"/>.
    /// </summary>
    /// <param name="includeNetworkProbes">
    /// False skips the checks that go out to the network. <c>vx run</c> passes false: the only such probe is
    /// Python's TLS trust, which is a warning about <c>vx maps --rebuild</c> and the tools/*.py scripts —
    /// nothing a launch depends on — and paying for it here would put an HTTPS request in front of
    /// starting a game. Measured 0.34 s online; OFFLINE it is a ten-second stall before a warning the launch
    /// cannot act on anyway, which is the case that decided this.
    /// </param>
    internal static List<Check> Findings(bool includeNetworkProbes = true)
        => Toolchain(includeNetworkProbes).Concat(Content()).ToList();

    /// <summary>
    /// The remedy every missing system dependency shares: let setup drive the detected package manager. One
    /// command covers them all, so five missing tools cost one prompt rather than five.
    ///
    /// <para><c>--yes</c> is safe here ONLY because the caller already asked, naming the command — it answers
    /// setup's own confirmation rather than skipping consent. It runs the system package manager, sudo
    /// included, which is why the requirement offering it says so out loud.</para>
    /// </summary>
    private static int InstallSystemDeps() => Setup.Run(["--install-deps", "--yes"], json: false);

    private static readonly Fixer SystemDeps = new("./vx setup --install-deps", InstallSystemDeps);

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

    private static IEnumerable<Check> Toolchain(bool includeNetworkProbes = true)
    {
        // --- the host itself -------------------------------------------------------------------------
        // Reported first because it changes what every line under it MEANS. On x86_64 and arm64 the
        // engine is a download; on anything else it is a compile, and a reader who does not know which
        // machine they are on will read "Godot: not found → run ./vx setup" as advice that works.
        yield return Env.HostArchHasPrebuiltEngine
            ? new Check("host", Status.Ok, $"{Env.HostArch} — upstream publishes an engine for this architecture")
            : new Check("host", Status.Warn,
                $"{Env.HostArch} — no upstream Godot build exists for this architecture",
                Fix: "the engine has to be compiled here: './vx build-engine --target editor --install' "
                     + "(hours). './vx build' and './vx test' need no engine and work now.");

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

            // The RUNTIME is a separate question from the SDK, and conflating them cost a contributor two
            // rounds of "install .NET 8" that changed nothing (2026-08-03, Gentoo/Calculate: SDK 8 and 10
            // both merged, but each lives under its own /opt prefix and the selected 10.0 host only ever
            // looks inside its own root). Everything here targets net8.0; a host with no 8.x refuses to
            // start it unless something rolls forward, which the shims now do — so this is reported as
            // WORKING-BUT-WORTH-KNOWING rather than as a fault, and named so the next person recognises
            // the error text when they meet it outside vx.
            string[] runtimes = Env.Run(dotnet, "--list-runtimes").Out
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal))
                .Select(l => l.Split(' ')[1])
                .ToArray();
            if (runtimes.Length == 0)
                yield return new Check(".NET runtime", Status.Warn, "none reported by --list-runtimes",
                    Fix: "the SDK normally carries one; a partial install is the usual cause");
            else
            {
                bool native = runtimes.Any(r => r.StartsWith("8.", StringComparison.Ordinal));
                yield return new Check(".NET runtime", Status.Ok,
                    $"{string.Join(", ", runtimes)}"
                    + (native ? "  (8.x present — no roll-forward needed)"
                              : "  (no 8.x — rolling net8.0 forward via DOTNET_ROLL_FORWARD=LatestMajor)"));
            }
        }

        // --- Python: still required until the fetchers migrate (plan, stage 2) -----------------------
        string? py = Env.FindPython();
        if (py is null)
        {
            string envSet = Environment.GetEnvironmentVariable("PYTHON") is { Length: > 0 } p
                ? $"$PYTHON is set to '{p}' but is not a working Python >= 3.8"
                : "not found (tried python3, python)";
            // The suggestion is resolved against the DETECTED package manager rather than named as a
            // three-distro guess, which is what it used to be. See Packages for why that mattered.
            yield return new Check("Python 3", Status.Missing, envSet, Required: true,
                Fix: Packages.Advice("python3", Env.IsMacOS ? "xcode-select --install"
                    : Env.IsWindows ? "https://www.python.org/downloads/  (tick 'Add python.exe to PATH')"
                    : "install python3 with your distro's package manager"),
                // Windows has no repair: winget can install Python, but the new PATH does not reach an
                // already-running shell, so the fix would appear to have failed. Say it, do not do it.
                Repair: Env.IsWindows ? null : SystemDeps,
                Recheck: () => Env.FindPython() is not null);
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
            var ssl = includeNetworkProbes
                ? Env.Run(py, TimeSpan.FromSeconds(20),
                    "-c", "import urllib.request as u; u.urlopen('https://api.github.com', timeout=10); print('ok')")
                : (Code: 0, Out: "", Err: "");   // not asked for: reported as Ok rather than invented as a fault
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
            else if (includeNetworkProbes)
                yield return new Check("Python TLS trust", Status.Ok, "HTTPS verified");
        }

        // --- git -------------------------------------------------------------------------------------
        string? git = Env.Which("git");
        yield return git is null
            ? new Check("git", Status.Missing, "not on PATH", Required: true,
                Fix: Packages.Advice("git", "install git — https://git-scm.com/downloads"),
                Repair: Env.IsWindows ? null : SystemDeps,
                Recheck: () => Env.Which("git") is not null)
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
            // One implementation of "can this engine run our C#", shared with the preflight, so the
            // two cannot disagree about a machine. It covers more than the mono suffix: an engine
            // whose GodotSharp/ assemblies are missing reports a perfectly good version and still
            // cannot load a single C# file (ppc64le, 2026-08-20).
            string? defect = Wrappers.MonoDefect(godot, v.Out, v.Err);
            yield return new Check("Godot 4.6.3 (mono)", defect is null ? Status.Ok : Status.Warn,
                $"{(v.Out.Length > 0 ? v.Out.Split('\n')[0] : "?")}  ({godot})",
                Fix: defect);
        }

        // --- portable timeout: which implementation run-timeout.sh will pick --------------------------
        string? to = Env.Which("timeout") ?? Env.Which("gtimeout");
        yield return new Check("timeout", to is null ? Status.Warn : Status.Ok,
            to ?? "absent — tools/lib/run-timeout.sh uses its shell fallback",
            Fix: to is null && Env.IsMacOS
                ? $"optional: {Packages.Advice("coreutils", "brew install coreutils")}  (provides gtimeout)"
                : null,
            Repair: to is null && !Env.IsWindows ? SystemDeps : null,
            Recheck: () => (Env.Which("timeout") ?? Env.Which("gtimeout")) is not null);

        // --- helpers the fetch/package paths shell out to ---------------------------------------------
        foreach (string t in new[] { "curl", "unzip" })
        {
            string? hit = Env.Which(t);
            string tool = t;   // captured per iteration: the Recheck closure outlives this loop body
            yield return new Check(tool, hit is null ? Status.Warn : Status.Ok, hit ?? "not on PATH",
                Fix: hit is null ? Packages.Advice(tool, $"install {tool}") : null,
                Repair: hit is null && !Env.IsWindows ? SystemDeps : null,
                Recheck: () => Env.Which(tool) is not null);
        }

        // Named explicitly so `--install-deps` is a discoverable option rather than one buried in --help,
        // and so a machine where detection FAILED says so here instead of silently offering worse advice.
        yield return Packages.Detected is null
            ? new Check("package manager", Status.Warn, "none recognised on PATH — suggestions fall back to download links")
            : new Check("package manager", Status.Ok, $"{Packages.Detected.Id}  ({Packages.Detected.Exe})");
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
                Fix: "$PYTHON tools/data/fetch-maps.py     (or --rebuild to compile from source)",
                Repair: new Fixer("./vx maps", () => Maps.Run([], json: false)),
                Recheck: () => Directory.Exists(maps) && Directory.GetFiles(maps, "*.pk3").Length > 0);

        // Packs present but no longer pinned. `vx maps` verifies the other direction — every pinned pack is
        // installed and matches — so a pack DROPPED from the lockfile, or renamed, or left by an older fetch
        // scheme, is invisible to every existing check while the VFS goes on mounting it.
        //
        // Safe to call an extra pack a finding because this directory is not a drop box: player-supplied
        // packs belong in ~/XonData/data/maps (UserPaths), which is mounted OVER this tree rather than into
        // it. REPORTED, NOT REMOVED — doctor changes nothing, and "delete the map file I do not recognise"
        // is not a decision to take on someone's behalf.
        // Exports that exist but contain no game. A crashed export leaves a complete-looking binary with
        // no embedded pck (ppc64le, 2026-08-20) — launching one dead-ends at Godot's "Is the .pck file
        // missing?" dialog, which names neither the export nor the crash. `vx run` refuses its own preset's
        // broken artifact; this is the only place the OTHER presets' artifacts get looked at.
        foreach ((string bPreset, string bOut) in Wrappers.BrokenExports())
            yield return new Check($"dist/ ({bPreset})", Status.Warn,
                $"{bOut} exists but has NO game data — the export that wrote it crashed partway",
                Fix: $"./vx export --preset {bPreset}     (and read its output: the crash is the real story)");

        string[] orphans = OrphanPacks(root).ToArray();
        if (orphans.Length > 0)
            yield return new Check("data/maps/ (unpinned)", Status.Warn,
                $"{orphans.Length} pack(s) not in maps.lock.json: {string.Join(", ", orphans.Take(5))}"
                + (orphans.Length > 5 ? $", +{orphans.Length - 5} more" : ""),
                Fix: "left by an older fetcher or a dropped pin — the VFS still mounts them; delete if unwanted");

        // Godot's OWN export templates, needed by any preset with an empty custom_template/release
        // (today: macos-client, a declared exception in engine.lock.json). Separate from the pinned custom
        // templates below.
        //
        // The advice here used to be "nothing in vx installs them, use the editor's GUI". That stopped being
        // true when `vx engine --editor` landed (it fetches what godot.lock.json pins under
        // editor_templates), and a stale hint sending someone into a GUI for what one command does is worse
        // than no hint at all.
        yield return Wrappers.EditorTemplatesPresent()
            ? new Check("Godot editor templates", Status.Ok, "installed")
            : new Check("Godot editor templates", Status.Warn,
                "not installed - presets without a pinned custom template cannot export (macos-client)",
                Fix: "./vx engine --editor     (~1.2 GB; or the Godot editor's Manage Export Templates)",
                Repair: new Fixer("./vx engine --editor", () => Engine.Run(["--editor"], json: false)),
                Recheck: Wrappers.EditorTemplatesPresent);

        // The repo-local engine install find-godot.sh probes before PATH.
        string bin = Path.Combine(root, ".godot-bin");
        yield return new Check(".godot-bin/", Directory.Exists(bin) ? Status.Ok : Status.Warn,
            Directory.Exists(bin) ? "present" : "absent (fine — Godot may be installed system-wide)");

        // Stale content links (retired 2026-08-03 in favour of --data). Reported rather than removed, because
        // doctor changes nothing — but reported at all because these are not inert: tools/package.sh writes to
        // this exact path, so an rsync --delete or rm -rf aimed there resolves into the committed content tree.
        string[] staleLinks = Wrappers.StaleContentLinks().ToArray();
        if (staleLinks.Length > 0)
            yield return new Check("dist/ content links", Status.Warn,
                $"{staleLinks.Length} leftover data/ link(s): {string.Join(", ", staleLinks)}",
                Fix: "obsolete since `vx run` began passing --data; `./vx export` removes them, or delete the "
                   + "LINK by hand (never its target)");

        // Reported ALWAYS, not only when overridden. An override.cfg is untracked, persists across sessions
        // and is invisible from inside the game, so the state this tree is in has to be sayable out loud —
        // otherwise it becomes the thing that quietly explains a frame-time result nobody can reproduce.
        List<RenderThread.Site> sites = RenderThread.Sites();
        RenderThread.Site[] off = sites.Where(s => s.Overridden).ToArray();
        yield return off.Length == 0
            ? new Check("render thread", Status.Ok, "separate (project.godot thread_model=2)")
            : new Check("render thread", Status.Warn,
                $"DISABLED for {off.Length} of {sites.Count} target(s): {string.Join(", ", off.Select(s => s.Label))}",
                Fix: "frame times are not comparable to a default build — './vx build --render-thread' restores it");
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

    /// <summary>
    /// Installed <c>data/maps/*.pk3</c> that <c>data/maps.lock.json</c> does not pin. Empty when the lockfile
    /// is unreadable: a check that cannot read its own reference must report nothing rather than accuse every
    /// pack of being an orphan.
    /// </summary>
    private static IEnumerable<string> OrphanPacks(string root)
    {
        string dir = Path.Combine(root, "data", "maps");
        if (!Directory.Exists(dir)) yield break;

        HashSet<string>? pinned = null;
        try
        {
            string lockPath = Path.Combine(root, "data", "maps.lock.json");
            if (File.Exists(lockPath))
                pinned = JsonNode.Parse(File.ReadAllText(lockPath))?["packs"]?.AsObject()
                    .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { /* fall through */ }
        if (pinned is null) yield break;

        foreach (string f in Directory.GetFiles(dir, "*.pk3").OrderBy(x => x, StringComparer.Ordinal))
            if (!pinned.Contains(Path.GetFileNameWithoutExtension(f)))
                yield return Path.GetFileName(f);
    }
}
