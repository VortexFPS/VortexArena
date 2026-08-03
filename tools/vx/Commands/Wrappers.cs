namespace Vx.Commands;

/// <summary>
/// The thin half of <c>vx</c>: commands that exist to be a door, not an implementation.
///
/// <para><b>These deliberately hold no logic.</b> <c>ci/ci.sh</c>, <c>tools/package.sh</c> and
/// <c>tools/perf-run.sh</c> are well reasoned, carry history in their comments, and stay independently
/// runnable — the plan is explicit that vx must not become a second place where build logic lives, in
/// parallel with them. So each of these resolves a tool, forwards its arguments, and returns its exit code
/// unmodified. Unrecognised flags are passed straight through rather than parsed, so a script gaining an
/// option does not require editing vx to reach it.</para>
///
/// <para>The exception is <see cref="Build"/>/<see cref="Test"/>, which wrap <c>dotnet</c> directly because
/// there is no script to delegate to — the commands are one line each and always have been.</para>
/// </summary>
internal static class Wrappers
{
    // ---- dotnet ---------------------------------------------------------------------------------------

    internal static int Build(string[] args)
    {
        // RELEASE IS THE DEFAULT (2026-08-03, Bryan) - symmetric with `vx run`: the default pipeline
        // operates on the configuration players get. `vx build debug` (or --config Debug) builds the Debug
        // assembly the editor project (`vx run debug`) loads. The build itself is INCREMENTAL by nature -
        // plain `dotnet build` runs MSBuild's up-to-date checks and compiles nothing when nothing changed;
        // `--clean` is the explicit full-rebuild escape (dotnet clean for that config, then build).
        string config = ValueOf(args, "--config") ?? (args.Contains("debug") || args.Contains("--debug") ? "Debug" : "Release");
        bool clean = args.Contains("--clean");

        // --no-render-thread / --render-thread do not affect the C# compile at all — they are a Godot
        // PROJECT SETTING, applied here because "build me a client that works on this machine" is when a
        // person reaches for it, and because doing it as part of the build is what makes it stick for the
        // `vx run` that follows. See RenderThread for the whole mechanism.
        if (RenderThreadFlag(args) is { } separate)
        {
            int rc = RenderThread.Apply(separate);
            if (rc != 0) return rc;
        }

        string? dotnet = Env.Which("dotnet");
        if (dotnet is null) { NoDotnet(); return 1; }
        string proj = Path.Combine(Env.RepoRoot, "VortexArena.csproj");
        if (clean)
        {
            int rc = Env.Exec(dotnet, ["clean", proj, "-c", config, "--nologo", "-v", "q"]);
            if (rc != 0) return rc;
        }
        Console.WriteLine($"-> {config} C# build{(clean ? " (after clean)" : "")}");
        return Env.Exec(dotnet, ["build", proj, "-c", config, "--nologo"]);
    }

    internal static int Test(string[] args)
    {
        string? dotnet = Env.Which("dotnet");
        if (dotnet is null) { NoDotnet(); return 1; }
        var argv = new List<string>
        {
            "test", Path.Combine(Env.RepoRoot, "tests", "VortexArena.Tests", "VortexArena.Tests.csproj"), "--nologo",
        };
        if (ValueOf(args, "--filter") is { } f) { argv.Add("--filter"); argv.Add(f); }
        argv.AddRange(PassThrough(args, "--filter"));
        return Env.Exec(dotnet, argv);
    }

    // ---- the engine -----------------------------------------------------------------------------------

    /// <summary>
    /// Launch the client. Extra args go to the game unchanged.
    ///
    /// <para><b>Which build you get, which used to be invisible.</b> Two very different things can be meant by
    /// "run the game", and they do not behave the same:</para>
    /// <list type="bullet">
    ///   <item><b>default</b> — the EXPORTED client from <c>dist/</c>, i.e. what a player runs and what every
    ///         perf number is measured against (docs/PERF-DEBUGGING.md: capture on the release export).</item>
    ///   <item><b><c>debug</c></b> — the Godot editor binary on the PROJECT (<c>--path &lt;root&gt;</c>), loading
    ///         the Debug C# assembly from <c>.godot/mono/temp/bin/Debug/</c>. <c>OS.IsDebugBuild()</c> is TRUE
    ///         here, and that is not cosmetic: the frame profiler defaults on, <c>showfps</c>/<c>showposition</c>
    ///         default on, and frame times are not release-representative. Fast to iterate — <c>./vx build
    ///         debug</c> and relaunch.</item>
    /// </list>
    ///
    /// <para>Both print which they picked before launching, because guessing wrong costs an afternoon of
    /// measuring the wrong build.</para>
    /// </summary>
    internal static int RunClient(string[] args)
    {
        // RELEASE IS THE DEFAULT (2026-08-03, Bryan): `vx run` launches the exported client — what a player
        // runs and what every perf number is measured against. The Debug project build is the OPT-IN
        // (`vx run debug`), because a debug run is the non-representative one: OS.IsDebugBuild() is true,
        // profiler + showfps default on, frame times are not comparable. `--release` is still accepted as a
        // no-op so older muscle memory and scripts keep working.
        bool debug = args.Contains("debug") || args.Contains("--debug");
        bool skipCheck = args.Contains("--no-build-check") || args.Contains("-n");
        string[] gameArgs = args
            .Where(a => a is not ("debug" or "--debug" or "--release" or "--no-build-check" or "-n"
                                  or "--no-render-thread" or "--render-thread"))
            .ToArray();

        // Accepted here as well as on `build` because this is where someone chasing a crash actually is.
        if (RenderThreadFlag(args) is { } separate)
        {
            int rc = RenderThread.Apply(separate);
            if (rc != 0) return rc;
        }
        else if (RenderThread.DisabledAtRoot())
        {
            // Unprompted, every launch, for the same reason both run paths announce which build they picked:
            // this changes frame times, it persists across sessions, and it is invisible in the game.
            Console.WriteLine("→ separate render thread OFF (override.cfg) — frame times are not comparable "
                            + "to a default build");
        }

        return debug ? RunProject(gameArgs, skipCheck) : RunRelease(gameArgs, skipCheck);
    }

    private static int RunProject(string[] gameArgs, bool skipCheck)
    {
        string? godot = Env.FindGodot();
        if (godot is null) { NoGodot(); return 1; }

        string dll = Path.Combine(Env.RepoRoot, ".godot", "mono", "temp", "bin", "Debug", "VortexArena.dll");
        if (!skipCheck && !EnsureFresh(dll, "the Debug assembly", "./vx build debug", () => Build(["debug"])))
            return 1;

        Console.WriteLine("→ project, Debug C# (editor engine; OS.IsDebugBuild() is true — profiler and "
                        + "showfps default ON, frame times are not release-representative)");
        var argv = new List<string> { "--path", Env.RepoRoot };
        argv.AddRange(gameArgs);
        return Env.Exec(godot, argv);
    }

    private static int RunRelease(string[] gameArgs, bool skipCheck)
    {
        (string preset, string outRel) = Presets.First(p => p.Preset == DefaultPreset());
        string artifact = Path.Combine(Env.RepoRoot, outRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(artifact) && !Directory.Exists(artifact))
        {
            Console.Error.WriteLine($"vx run: nothing exported at {outRel}");
            Console.Error.WriteLine($"        ./vx export --preset {preset}   (or `vx run debug` for the editor project)");
            return 1;
        }
        if (!skipCheck && !EnsureFresh(artifact, $"the {preset} export", $"./vx export --preset {preset}",
                                       () => Export(["--preset", preset])))
            return 1;

        // Clean here too, not only on export: a dist/ built before 2026-08-03 still carries the link, and
        // this is the command most likely to be the next thing run in such a tree.
        CleanStaleContentLink(artifact);

        // macOS exports a .app BUNDLE, not a bare executable — the thing to spawn is inside it.
        string launch = Directory.Exists(artifact)
            ? Path.Combine(artifact, "Contents", "MacOS", Path.GetFileNameWithoutExtension(artifact))
            : artifact;

        // CONTENT COMES FROM --data, not from a link beside the binary. The export excludes data/* from the
        // pck, and dist/<preset>/ holds only the binary — so point the game at the repo's tree with the flag
        // Main.cs already has. This replaced a symlink/junction (2026-08-03): same result, nothing left on
        // disk afterwards, and no path that tools/package.sh can later rsync --delete through.
        //
        // An explicit --data from the caller WINS: overriding someone who is deliberately pointing a build at
        // a different gamedir would defeat the only reason the flag exists.
        var argv = new List<string>();
        if (!gameArgs.Contains("--data"))
        {
            argv.Add("--data");
            argv.Add(Path.Combine(Env.RepoRoot, "data"));
        }
        argv.AddRange(gameArgs);

        Console.WriteLine($"→ {outRel}, release export (what a player runs)");
        // Launched from the install dir, exactly as a player would. That no longer decides where content is
        // found — --data does — but it still governs where relative paths in game args land.
        return Env.Exec(launch, argv, Path.GetDirectoryName(artifact));
    }

    /// <summary>
    /// Warn when the artifact about to be launched is older than the sources that produced it, and offer to
    /// rebuild it. Returns true to proceed with the launch, false only when a rebuild was asked for and failed
    /// (launching a half-built tree after that would just bury the compiler error).
    ///
    /// <para>Deliberately advisory. It is a modification-time comparison, not a dependency graph — it can be
    /// fooled by a touched-but-unchanged file, and it does not know about content, shaders or the cfg tree. So
    /// declining is a first-class answer, and a non-interactive caller (CI, a script, anything with stdin
    /// redirected) is warned and launched rather than being blocked on a prompt nobody will ever answer.</para>
    /// </summary>
    private static bool EnsureFresh(string artifact, string what, string howToBuild, Func<int> build)
    {
        if (NewestSource() is not { } newest)
            return true;                                   // unreadable tree — never block a launch on this

        DateTime built = File.Exists(artifact) ? File.GetLastWriteTimeUtc(artifact)
                       : Directory.Exists(artifact) ? Directory.GetLastWriteTimeUtc(artifact)
                       : DateTime.MinValue;
        if (built >= newest.When)
            return true;

        Console.Error.WriteLine(built == DateTime.MinValue
            ? $"vx run: {what} has not been built yet."
            : $"vx run: {what} is older than {newest.Path} — it may not include your latest changes.");

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"        Launching it anyway (no terminal to ask at). Build with: {howToBuild}");
            return true;
        }

        Console.Error.Write($"        Rebuild now ({howToBuild})? [Y/n] ");
        string? answer = Console.ReadLine()?.Trim();
        if (answer is { Length: > 0 } a && (a[0] is 'n' or 'N'))
            return true;                                   // deliberately launching the stale one

        int rc = build();
        if (rc != 0)
        {
            Console.Error.WriteLine($"vx run: build failed (exit {rc}) — not launching.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The most recently modified build INPUT, or null when the tree cannot be read.
    ///
    /// <para>Walks <c>game/</c> and <c>src/</c> for <c>*.cs</c> plus the project files. <c>obj/</c> and
    /// <c>bin/</c> are skipped because they are build OUTPUTS: they are always newer than the sources that
    /// produced them, so counting them would report every single launch as stale. Timestamps come off the
    /// directory enumeration rather than a stat per file, which keeps ~800 files well under the threshold
    /// where anyone would notice this running before a launch.</para>
    /// </summary>
    private static (string Path, DateTime When)? NewestSource()
    {
        try
        {
            string newestPath = "";
            DateTime newest = DateTime.MinValue;

            void Consider(FileInfo f)
            {
                if (f.LastWriteTimeUtc > newest) { newest = f.LastWriteTimeUtc; newestPath = f.FullName; }
            }

            char sep = Path.DirectorySeparatorChar;
            foreach (string dir in new[] { "game", "src" })
            {
                var root = new DirectoryInfo(Path.Combine(Env.RepoRoot, dir));
                if (!root.Exists) continue;
                foreach (FileInfo f in root.EnumerateFiles("*.cs", SearchOption.AllDirectories))
                    if (!f.FullName.Contains($"{sep}obj{sep}") && !f.FullName.Contains($"{sep}bin{sep}"))
                        Consider(f);
            }
            foreach (string proj in new[] { "VortexArena.csproj", "Directory.Build.props" })
            {
                var f = new FileInfo(Path.Combine(Env.RepoRoot, proj));
                if (f.Exists) Consider(f);
            }

            return newest == DateTime.MinValue ? null : (Path.GetRelativePath(Env.RepoRoot, newestPath), newest);
        }
        catch (Exception)
        {
            return null;   // a staleness heuristic must never be the reason the game won't start
        }
    }

    /// <summary>
    /// Headless dedicated server. The first bare argument is the map, matching
    /// <c>tools/run-dedicated.sh</c>'s shape; everything else is forwarded.
    /// </summary>
    internal static int Server(string[] args)
    {
        string? godot = Env.FindGodot();
        if (godot is null) { NoGodot(); return 1; }
        string? map = args.FirstOrDefault(a => !a.StartsWith('-'));
        var argv = new List<string> { "--headless", "--path", Env.RepoRoot };
        if (map is not null) { argv.Add("--dedicated"); argv.Add(map); }
        argv.AddRange(args.Where(a => a != map));
        return Env.Exec(godot, argv);
    }

    // ---- release ---------------------------------------------------------------------------------------

    /// <summary>Export presets and their output binary, mirroring export_presets.cfg and package.sh.</summary>
    internal static readonly (string Preset, string Out)[] Presets =
    [
        ("windows-client",  "dist/windows-client/VortexArena.exe"),
        ("linux-client",    "dist/linux-client/VortexArena.x86_64"),
        ("linux-dedicated", "dist/linux-dedicated/vortexarena-dedicated.x86_64"),
        ("macos-client",    "dist/macos-client/VortexArena.app"),
    ];

    internal static int Export(string[] args)
    {
        string? godot = Env.FindGodot();
        if (godot is null) { NoGodot(); return 1; }

        // The pinned templates are a HARD prerequisite, not a nicety: three of the four presets set
        // custom_template/release, and Godot aborts the export outright when that path is populated but
        // missing. Checking here turns that into one clear sentence instead of an engine-level abort.
        string templates = Path.Combine(Env.RepoRoot, "tools", "engine-templates");
        if (!Directory.Exists(templates) || Directory.GetFiles(templates).Length == 0)
        {
            Console.Error.WriteLine("vx export: the pinned export templates are not installed.");
            Console.Error.WriteLine("           ./vx engine        (fetches what engine.lock.json pins)");
            return 1;
        }

        var targets = args.Contains("--all")
            ? Presets.ToList()
            : ValueOf(args, "--preset") is { } p
                ? Presets.Where(x => x.Preset == p).ToList()
                : Presets.Where(x => x.Preset == DefaultPreset()).ToList();

        if (targets.Count == 0)
        {
            Console.Error.WriteLine($"vx export: unknown preset '{ValueOf(args, "--preset")}'");
            Console.Error.WriteLine($"           available: {string.Join(", ", Presets.Select(x => x.Preset))}");
            return 2;
        }

        foreach ((string preset, string outRel) in targets)
        {
            string outPath = Path.Combine(Env.RepoRoot, outRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            Console.WriteLine($"→ export {preset} → {outRel}");
            // ExecUntilDone, not Exec: headless --export-release routinely never exits after a SUCCESSFUL
            // export. See Env.ExecUntilDone for the evidence; without it this command hangs forever on
            // Windows, which is what run-release.sh existed to work around.
            // settleDir: the savepack marker fires BEFORE the .NET export copies its assembly set —
            // ExecUntilDone waits for the artifact dir to quiesce so the kill can't truncate that copy
            // (a build missing one runtime dll dies at StartListenServer; see ExecUntilDone's note).
            int rc = Env.ExecUntilDone(godot,
                ["--headless", "--path", Env.RepoRoot, "--export-release", preset, outPath],
                doneMarker: @"DONE.*savepack",
                settleDir: Path.GetDirectoryName(outPath));

            // Godot's headless export exits NON-ZERO on a fully successful export often enough that
            // run-release.sh stopped trusting the code entirely and gated on the artifact instead — a
            // judgement inherited here when that script was retired: the binary existing is the result,
            // the exit code is an opinion.
            if (!File.Exists(outPath) && !Directory.Exists(outPath))
            {
                Console.Error.WriteLine($"vx export: {preset} produced no artifact at {outRel} (exit {rc})");
                // The commonest cause, and one Godot words clearly but out of context: a preset with no
                // custom_template/release falls back to the EDITOR's stock export templates, which are a
                // separate ~1.2 GB .tpz that nothing here installs. macos-client is deliberately in that
                // state (engine.lock.json, unpinned_presets) — see ./vx doctor.
                if (!EditorTemplatesPresent())
                    Console.Error.WriteLine(
                        "           If Godot reported \"No export template found\": this preset uses the EDITOR's\n" +
                        "           stock templates. Install them once via the Godot editor\n" +
                        "           (Editor → Manage Export Templates → Download and Install).");
                return 1;
            }
            Console.WriteLine($"   ok: {outRel}");
            CleanStaleContentLink(outPath);

            // The export carries no content: export_presets.cfg excludes data/* from the pck. Said out loud
            // because the failure is silent — a bare double-click launches, mounts NOTHING and renders an
            // empty world, and perf-run.sh's notes record that exact shape eating an investigation.
            Console.WriteLine("   note: no content beside the binary — `./vx run` passes --data, "
                            + "`tools/package.sh` makes a real install");
        }

        // A fresh dist/ has no override.cfg, so without this an export silently re-enables the render thread
        // for `vx run` while `vx run debug` stays off — the switch would appear to work intermittently.
        RenderThread.Sync();
        return 0;
    }

    /// <summary>
    /// Repo-relative paths of every leftover <c>data/</c> LINK beside an export. For <see cref="Doctor"/>,
    /// which reports rather than removes — the removal lives in <see cref="CleanStaleContentLink"/>.
    /// </summary>
    internal static IEnumerable<string> StaleContentLinks()
    {
        foreach ((_, string outRel) in Presets)
        {
            string artifact = Path.Combine(Env.RepoRoot, outRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(artifact) && !Directory.Exists(artifact)) continue;
            string dest = ContentSibling(artifact);
            string? link = null;
            try { link = new DirectoryInfo(dest) is { Exists: true } d ? d.LinkTarget : null; }
            catch { /* unreadable is not a finding */ }
            if (link is not null)
                yield return Path.GetRelativePath(Env.RepoRoot, dest).Replace('\\', '/');
        }
    }

    /// <summary>Where an exported binary of this artifact would look for <c>data/</c>.</summary>
    private static string ContentSibling(string artifact)
        // macOS keeps it INSIDE the bundle, matching tools/package.sh and DataPaths' ../Resources probe.
        => Directory.Exists(artifact) && artifact.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(artifact, "Contents", "Resources", "data")
            : Path.Combine(Path.GetDirectoryName(artifact)!, "data");

    /// <summary>
    /// Remove a <c>data/</c> LINK left beside an export by the retired scripts — and nothing else, ever.
    ///
    /// <para><b>Why there is anything to clean.</b> Until 2026-08-03 the only way an exported build found
    /// content was a symlink/junction to the repo's <c>data/</c>, dropped next to the binary by
    /// <c>run-release.sh</c>, <c>tools/perf-run.*</c> and briefly by <c>vx export</c> itself. That is now
    /// obsolete — <c>vx run</c> passes <c>--data</c> (Main.cs) — but the links are still sitting in every
    /// <c>dist/</c> that predates the change, and they are not inert: <c>tools/package.sh</c> writes to this
    /// exact path, so an <c>rsync --delete</c> or an <c>rm -rf</c> aimed here resolves into the COMMITTED
    /// content tree. Clearing them is the point, not tidiness.</para>
    ///
    /// <para><b>LINKS ONLY.</b> A real directory here is a packaged install (<c>tools/package.sh</c> puts a
    /// genuine ~1.6 GB copy in exactly this place), and deleting somebody's build because it was in the way
    /// would be a far worse bug than the one being fixed. <c>LinkTarget</c> is the discriminator and
    /// <c>Directory.Delete</c> on a reparse point removes the link rather than recursing into its target —
    /// both verified against a real junction before this shipped.</para>
    /// </summary>
    private static void CleanStaleContentLink(string artifact)
    {
        string dest = ContentSibling(artifact);
        try
        {
            var di = new DirectoryInfo(dest);
            if (!di.Exists || di.LinkTarget is null)
                return;                                    // absent, or a real directory — leave it alone
            Directory.Delete(dest);                        // the reparse point, never what it points at
            Console.WriteLine($"   cleaned: stale data/ link at "
                            + $"{Path.GetRelativePath(Env.RepoRoot, dest).Replace('\\', '/')} (--data replaces it)");
        }
        catch (Exception ex)
        {
            // Never fatal: a link we could not remove is the state we were already in.
            Console.Error.WriteLine($"   note: could not remove the stale data/ link at {dest}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the EDITOR's stock export templates are installed. Distinct from tools/engine-templates/:
    /// those are the pinned custom templates three presets embed, while these are Godot's own set, needed
    /// by any preset with an empty custom_template/release — today just macos-client, deliberately.
    /// </summary>
    internal static bool EditorTemplatesPresent()
    {
        string? root = Env.IsWindows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "export_templates")
            : Env.IsMacOS
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Godot", "export_templates")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "godot", "export_templates");
        return Directory.Exists(root) && Directory.GetDirectories(root).Length > 0;
    }

    private static string DefaultPreset()
        => Env.IsWindows ? "windows-client" : Env.IsMacOS ? "macos-client" : "linux-client";

    internal static int Package(string[] args) => Env.Bash("tools/package.sh", args);

    // ---- the existing shell entry points ---------------------------------------------------------------

    internal static int Ci(string[] args) => Env.Bash("ci/ci.sh", args);

    /// <summary>
    /// Perf capture. This is the one command with a genuine platform split: tools/perf-run.ps1 and
    /// tools/perf-run.sh are parallel implementations that have already drifted, and the .ps1 is the one
    /// the dev box actually uses. Prefer it on Windows rather than pretending the .sh is authoritative
    /// there; retiring the pair is Phase 2 of the plan, not something to paper over here.
    /// </summary>
    /// <summary>Pre-merge perf gate. Same platform split as <see cref="Perf"/>.</summary>
    internal static int PerfSmoke(string[] args) => PsOrSh("perf-smoke", args);

    /// <summary>
    /// Motion/present wobble capture. The .ps1 is NOT interchangeable with the .sh here: it drives
    /// PresentMon, an ETW consumer with no macOS or Linux equivalent, so the shell twin captures the motion
    /// trace only. Dispatching by platform is what makes that difference invisible at the call site.
    /// </summary>
    internal static int Wobble(string[] args) => PsOrSh("wobble-capture", args);

    /// <summary>
    /// Run tools/&lt;name&gt;.ps1 on Windows and tools/&lt;name&gt;.sh elsewhere. The .ps1/.sh pairs are a
    /// maintenance tax the plan intends to retire, but while they exist a caller should not have to know
    /// which one they are on — and on Windows the .ps1 is the one with the fuller implementation.
    /// </summary>
    private static int PsOrSh(string name, string[] args)
    {
        if (Env.IsWindows)
        {
            string? pwsh = Env.Which("pwsh") ?? Env.Which("powershell");
            if (pwsh is not null)
            {
                var argv = new List<string> { "-File", Path.Combine(Env.RepoRoot, "tools", name + ".ps1") };
                argv.AddRange(args);
                return Env.Exec(pwsh, argv);
            }
        }
        return Env.Bash($"tools/{name}.sh", args);
    }

    internal static int Perf(string[] args) => PsOrSh("perf-run", args);

    // ---- helpers ---------------------------------------------------------------------------------------

    private static string? ValueOf(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// The requested render-thread state, or null when neither flag was given. Returning null rather than
    /// defaulting to true is the point: the switch is STICKY (it is a file on disk), so a plain `vx build`
    /// must leave it exactly as the last explicit instruction set it.
    /// </summary>
    private static bool? RenderThreadFlag(string[] args)
        => args.Contains("--no-render-thread") ? false
         : args.Contains("--render-thread") ? true
         : null;

    /// <summary>Everything except <paramref name="consumed"/> and its value, so extra flags reach the tool.</summary>
    private static IEnumerable<string> PassThrough(string[] args, string consumed)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == consumed) { i++; continue; }
            yield return args[i];
        }
    }

    private static void NoDotnet()
        => Console.Error.WriteLine("vx: the .NET SDK is not on PATH — see ./vx doctor");

    private static void NoGodot()
    {
        Console.Error.WriteLine("vx: Godot not found (tried $GODOT, .godot-bin/, PATH, the platform install dir).");
        Console.Error.WriteLine("    ./vx setup        installs the pinned engine into .godot-bin/");
        Console.Error.WriteLine("    ./vx doctor       shows what was probed");
    }
}
