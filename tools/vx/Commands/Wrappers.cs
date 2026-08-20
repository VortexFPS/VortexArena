using System.Text.Json.Nodes;

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
        // --yes answers the preflight's prompts in advance, for a script or an unattended machine. It is
        // NOT the default: some of what the preflight offers costs hours (compiling the engine) or a
        // gigabyte (the maps), and starting either without being asked would be worse than the error it
        // replaced.
        bool assumeYes = args.Contains("--yes") || args.Contains("-y");
        string[] gameArgs = args
            .Where(a => a is not ("debug" or "--debug" or "--release" or "--no-build-check" or "-n"
                                  or "--yes" or "-y"
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

        return debug ? RunProject(gameArgs, skipCheck, assumeYes) : RunRelease(gameArgs, skipCheck, assumeYes);
    }

    // ---- preflight ------------------------------------------------------------------------------------

    /// <summary>
    /// What both run paths need regardless of which build they launch: the game's content.
    ///
    /// <para>Neither is about the binary, and neither is caught by any staleness check - a clone with no
    /// maps launches perfectly and then cannot start a match, which reads as a bug in the game.</para>
    /// </summary>
    private static IEnumerable<Requirement> ContentRequirements()
    {
        yield return new Requirement(
            "data/",
            () => Directory.Exists(Path.Combine(Env.RepoRoot, "data")),
            "data/ is missing — the core content is COMMITTED, so this checkout is incomplete.",
            Command: null,   // nothing vx can fetch: it should already be here
            Fix: null,
            Fatal: true,
            Note: "Re-clone, or `git checkout -- data`. Nothing will render without it.");

        yield return new Requirement(
            "the map packs",
            () => !Setup.MapsIncomplete(),
            "the compiled map packs are missing or incomplete — the game will start but has no maps to play.",
            Command: "./vx maps",
            Fix: () => Maps.Run([], json: false),
            Fatal: false,
            Note: "~1 GB, pinned by data/maps.lock.json. The menu works without them.");
    }

    /// <summary>
    /// The engine, phrased so it is only required when something actually needs it.
    ///
    /// <para><paramref name="alreadySatisfied"/> is what keeps this honest: launching an export that is
    /// already built needs no engine at all, so demanding one there would be a fabricated requirement.
    /// It is a predicate rather than a bool because the state changes as earlier fixes run.</para>
    /// </summary>
    private static Requirement GodotRequirement(Func<bool> alreadySatisfied)
    {
        // Probed once here so the message can name what is actually wrong; the predicate re-probes, because
        // after a fix the answer legitimately changes. Skipped entirely when an export already exists, which
        // needs no engine at all.
        bool needed = !alreadySatisfied();
        string? found = needed ? Env.FindGodot() : null;
        string? defect = found is not null ? MonoDefect(found) : null;

        return new Requirement(
            "a .NET-capable Godot",
            () => alreadySatisfied() || (Env.FindGodot() is { } g && MonoDefect(g) is null),
            found is null
                ? "Godot is not installed here (tried $GODOT, .godot-bin/, PATH, the platform install dir)."
                : $"the Godot at {found} cannot run this game's C# — {defect}.",
            Command: Env.HostArchHasPrebuiltEngine
                ? "./vx setup"
                : "./vx build-engine --target editor --install",
            Fix: Env.HostArchHasPrebuiltEngine
                ? () => Setup.Run(["--yes"], json: false)
                : () => BuildEngine(["--target", "editor", "--install"]),
            Fatal: true,
            Note: Env.HostArchHasPrebuiltEngine
                ? null
                : $"no upstream Godot build exists for {Env.HostArch}, so it has to be compiled here — "
                  + "hours, and it needs a C++ toolchain, scons and the .NET SDK. Build it with "
                  + "module_mono_enabled=yes; a plain build cannot run the game at all.");
    }

    /// <summary>
    /// Why the Godot at <paramref name="path"/> cannot run this game's C#, or null when it can.
    ///
    /// <para><b>This is a launch blocker, not a warning, and it was one before it was checked.</b> A plain
    /// (non-.NET) Godot starts fine, opens the project fine, and then fails at the only thing that matters
    /// here. Reported 2026-08-20 from a ppc64le machine carrying a self-built engine in <c>.godot-bin/</c>:
    /// <c>vx doctor</c> said "this is NOT a .NET/mono build" and every other command carried on regardless,
    /// so the export died with a bare <c>exit 255</c> several steps from the cause.</para>
    ///
    /// <para>An unreadable version counts as a defect rather than "cannot tell". On that machine
    /// <c>--version</c> printed NOTHING, which is not a working engine under any reading — and treating
    /// silence as fine is what let the run get as far as it did. The message names what was observed, so a
    /// false positive diagnoses itself, and <c>$GODOT</c> overrides the choice of binary outright.</para>
    /// </summary>
    private static string? MonoDefect(string path)
    {
        var v = Env.Run(path, TimeSpan.FromSeconds(15), "--version");
        string version = v.Out.Length > 0 ? v.Out.Split('\n')[0].Trim() : "";

        if (version.Length == 0)
            return "`--version` printed nothing, so this is not a working Godot binary "
                 + (v.Err.Length > 0 ? $"({Truncate(v.Err)})" : "(no output at all)");

        if (!version.Contains("mono", StringComparison.OrdinalIgnoreCase))
            return $"it reports '{version}', which is not a .NET/mono build";

        return null;
    }

    private static string Truncate(string s)
        => s.Length <= 90 ? s.Replace('\n', ' ') : s[..90].Replace('\n', ' ') + "...";

    private static int RunProject(string[] gameArgs, bool skipCheck, bool assumeYes)
    {
        // The editor engine IS the runtime here, so it is unconditionally required - no artifact can
        // stand in for it the way an export does on the release path.
        var required = new List<Requirement> { GodotRequirement(() => false) };
        required.AddRange(ContentRequirements());
        if (!Preflight.Run(required, assumeYes, noFix: skipCheck)) return 1;

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

    private static int RunRelease(string[] gameArgs, bool skipCheck, bool assumeYes)
    {
        (string preset, string outRel) = AllPresets.First(p => p.Preset == DefaultPreset());
        string artifact = Path.Combine(Env.RepoRoot, outRel.Replace('/', Path.DirectorySeparatorChar));
        bool Exported() => File.Exists(artifact) || Directory.Exists(artifact);

        // Ordered: each fix is a prerequisite of the next. Both engine requirements short-circuit once an
        // export exists, because running one needs neither - this walks a fresh clone to a launch without
        // demanding anything a built tree does not need.
        bool localBuild = LocalBuildPresets.Any(p => p.Preset == preset);
        var required = new List<Requirement>
        {
            GodotRequirement(Exported),
            new("the export template",
                () => Exported() || TemplateForPresetPresent(preset),
                localBuild
                    ? $"the export template for {preset} has not been built — no binary is published for "
                      + $"{Env.HostArch}, so it is built here."
                    : "the pinned export templates are not installed, and the export cannot run without them.",
                Command: localBuild ? $"./vx build-engine --arch {Env.GodotArch} --install" : "./vx engine",
                Fix: localBuild
                    ? () => BuildEngine(["--arch", Env.GodotArch, "--install"])
                    : () => Engine.Run([], json: false),
                Fatal: true,
                Note: localBuild ? "hours: the editor is compiled first, to generate the C# glue." : null),
            new("the release export",
                Exported,
                $"nothing is exported at {outRel} — there is no built game to launch yet.",
                Command: $"./vx export --preset {preset}",
                Fix: () => Export(["--preset", preset]),
                Fatal: true,
                Note: "`./vx run debug` skips this entirely and runs the project in the editor engine."),
        };
        required.AddRange(ContentRequirements());
        if (!Preflight.Run(required, assumeYes, noFix: skipCheck)) return 1;
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
        var started = System.Diagnostics.Stopwatch.StartNew();
        int exit = Env.Exec(launch, argv, Path.GetDirectoryName(artifact));
        DiagnoseFailedLaunch(exit, started.Elapsed, preset);
        return exit;
    }

    /// <summary>
    /// A game that dies in the first few seconds did not "quit" — it failed to start, and the reason is
    /// usually in a category vx can name even without reading the output.
    ///
    /// <para>Bounded on purpose. Quitting normally can also return non-zero on some platforms, and a
    /// session someone actually played must never be described as a failure — so the window is short, and
    /// this offers suggestions rather than a diagnosis it cannot actually make.</para>
    /// </summary>
    private static void DiagnoseFailedLaunch(int exit, TimeSpan ran, string preset)
    {
        if (exit == 0 || ran > TimeSpan.FromSeconds(10)) return;

        Console.Error.WriteLine();
        Console.Error.WriteLine($"vx run: the game exited {exit} after {ran.TotalSeconds:F1}s — that is a "
                                + "failed launch rather than a session.");
        Console.Error.WriteLine("        Most likely, in order:");
        Console.Error.WriteLine($"          ./vx export --preset {preset}      re-export: a truncated export "
                                + "is missing a runtime dll and dies at startup");
        Console.Error.WriteLine("          ./vx run debug                      the editor engine prints the "
                                + "real error where the release build swallows it");
        Console.Error.WriteLine("          ./vx doctor                         driver, toolchain and content checks");
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

    /// <summary>
    /// The SHIPPING presets — the ones a release publishes — and their output binary, mirroring
    /// export_presets.cfg and package.sh. This is what <c>--all</c> means.
    /// </summary>
    internal static readonly (string Preset, string Out)[] Presets =
    [
        ("windows-client",  "dist/windows-client/VortexArena.exe"),
        ("linux-client",    "dist/linux-client/VortexArena.x86_64"),
        ("linux-dedicated", "dist/linux-dedicated/vortexarena-dedicated.x86_64"),
        ("macos-client",    "dist/macos-client/VortexArena.app"),
    ];

    /// <summary>
    /// Presets for platforms this project supports as BUILD-FROM-SOURCE only — no binary is ever
    /// published for them, so a person on one of these machines builds the engine, builds the game and
    /// exports it themselves. Declared in engine.lock.json under <c>local_build_presets</c>.
    ///
    /// <para>Deliberately OUT of <see cref="Presets"/>, which is what <c>--all</c> and the release
    /// workflow mean. They are reachable by name — <c>vx export --preset linux-client-ppc64le</c> — and
    /// <see cref="DefaultPreset"/> picks one automatically when that is the machine you are on, which is
    /// the case that matters: on POWER, `vx run` should just work rather than requiring the flag.</para>
    /// </summary>
    internal static readonly (string Preset, string Out)[] LocalBuildPresets =
    [
        ("linux-client-ppc64le",    "dist/linux-client-ppc64le/VortexArena.ppc64le"),
        ("linux-dedicated-ppc64le", "dist/linux-dedicated-ppc64le/vortexarena-dedicated.ppc64le"),
    ];

    /// <summary>Every preset that can be exported by name, shipping or source-only.</summary>
    internal static IEnumerable<(string Preset, string Out)> AllPresets => Presets.Concat(LocalBuildPresets);

    /// <summary>
    /// The repo-relative export template a preset needs, read from engine.lock.json — or null when the
    /// lockfile does not describe one (a declared gap, or a preset it has never heard of).
    /// </summary>
    private static string? TemplatePathForPreset(string preset)
    {
        try
        {
            string lockPath = Path.Combine(Env.RepoRoot, "tools", "engine-patches", "engine.lock.json");
            if (!File.Exists(lockPath)) return null;
            JsonNode doc = JsonNode.Parse(File.ReadAllText(lockPath))!;

            // Built on this machine (a source-only platform): the lockfile names the path directly, since
            // there is no published artifact to derive it from.
            if (doc["local_build_presets"]?[preset]?["template"]?.GetValue<string>() is { Length: > 0 } local)
                return local;

            // Pinned: the template lives under tools/engine-templates/ by the filename its platform pins.
            if (doc["template"]?["platforms"]?.AsObject() is { } platforms)
                foreach (KeyValuePair<string, JsonNode?> platform in platforms)
                {
                    if (platform.Value is not { } entry) continue;
                    if (entry["presets"]?.AsArray().Any(p => p!.GetValue<string>() == preset) == true
                        && entry["filename"]?.GetValue<string>() is { Length: > 0 } filename)
                        return $"tools/engine-templates/{filename}";
                }
        }
        catch { /* a malformed lockfile is verify-engine-template.py's problem, not a reason not to launch */ }
        return null;
    }

    /// <summary>
    /// True when the export template <paramref name="preset"/> actually needs is on disk.
    ///
    /// <para><b>Replaces a "is tools/engine-templates/ non-empty?" check that was wrong on any machine with
    /// more than one architecture in play.</b> Reported 2026-08-20: on ppc64le, <c>vx setup</c> had fetched the
    /// pinned x86_64 templates, so the directory was non-empty, so the check passed — and the export then
    /// failed with a bare exit 255 because the ppc64le template, which nobody publishes and which has to be
    /// built locally, was not there. A per-preset check cannot be fooled that way.</para>
    ///
    /// <para>Falls back to the old directory check only for a preset the lockfile does not describe, where
    /// there is no filename to look for and something is better than nothing.</para>
    /// </summary>
    private static bool TemplateForPresetPresent(string preset)
    {
        if (TemplatePathForPreset(preset) is not { } rel)
            return Setup.TemplatesPresent();
        return File.Exists(Path.Combine(Env.RepoRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal static int Export(string[] args)
    {
        string? godot = Env.FindGodot();
        if (godot is null) { NoGodot(); return 1; }

        // --all is the SHIPPING matrix, not "every preset that exists": the source-only presets have no
        // published template, so including them would make --all fail on every machine that is not the one
        // architecture they target. They are reachable by name and by DefaultPreset().
        var targets = args.Contains("--all")
            ? Presets.ToList()
            : ValueOf(args, "--preset") is { } p
                ? AllPresets.Where(x => x.Preset == p).ToList()
                : AllPresets.Where(x => x.Preset == DefaultPreset()).ToList();

        if (targets.Count == 0)
        {
            Console.Error.WriteLine($"vx export: unknown preset '{ValueOf(args, "--preset")}'");
            Console.Error.WriteLine($"           available: {string.Join(", ", Presets.Select(x => x.Preset))}");
            Console.Error.WriteLine($"           source-only: {string.Join(", ", LocalBuildPresets.Select(x => x.Preset))}");
            return 2;
        }

        // The template is a HARD prerequisite, not a nicety: a preset that sets custom_template/release makes
        // Godot abort the export outright when that path is populated but missing, with a message about an
        // ARCHITECTURE MISMATCH rather than a missing file. Checked per preset rather than "is the directory
        // non-empty" — see TemplateForPresetPresent for the machine that distinction was reported from.
        foreach ((string preset, string _) in targets)
        {
            if (TemplateForPresetPresent(preset)) continue;
            bool local = LocalBuildPresets.Any(p => p.Preset == preset);
            Console.Error.WriteLine($"vx export: {preset} has no export template on disk"
                                    + (TemplatePathForPreset(preset) is { } want ? $" ({want})" : "") + ".");
            Console.Error.WriteLine(local
                ? $"           Nothing publishes one for {Env.HostArch}, so it is built here:\n"
                  + $"           ./vx build-engine --arch {Env.GodotArch} --install     (hours)"
                : "           ./vx engine        (fetches what engine.lock.json pins)");
            return 1;
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
                if (LocalBuildPresets.Any(p => p.Preset == preset))
                {
                    // Reaching here means the template EXISTS (checked before the export) but nothing came
                    // out — so the usual suspect is the ENGINE that ran it, not the template. On a
                    // source-only platform both were built by hand, and a non-.NET Godot is the failure that
                    // looks exactly like this: it starts, opens the project, and exports nothing.
                    Console.Error.WriteLine(
                        "           This preset builds its own engine. Check that BOTH the editor and the\n" +
                        "           template were built with module_mono_enabled=yes — a plain Godot cannot\n" +
                        "           export a C# game — and that the template matches the preset:\n" +
                        $"           python tools/verify-engine-template.py --preset-config {preset}\n" +
                        "           ./vx doctor");
                }
                else if (!EditorTemplatesPresent())
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
        foreach ((_, string outRel) in AllPresets)
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

    /// <summary>
    /// The preset for THIS machine. Architecture matters as well as OS: on a Linux host that is not
    /// x86_64 the x86_64 preset is not merely a poor default, it is one that cannot produce a runnable
    /// binary. Falls back to the x86_64 Linux preset for any architecture with no preset of its own,
    /// which is where the export's own error message is the right teacher.
    /// </summary>
    private static string DefaultPreset()
    {
        if (Env.IsWindows) return "windows-client";
        if (Env.IsMacOS) return "macos-client";
        string arch = $"linux-client-{Env.HostArch}";
        return LocalBuildPresets.Any(p => p.Preset == arch) ? arch : "linux-client";
    }

    internal static int Package(string[] args) => Env.Bash("tools/package.sh", args);

    // ---- the existing shell entry points ---------------------------------------------------------------

    internal static int Ci(string[] args) => Env.Bash("ci/ci.sh", args);

    /// <summary>
    /// Build Godot itself from source. Delegates wholesale — every flag, every default and every safety
    /// check lives in tools/build-engine.sh, which is also runnable without vx (the machine that most
    /// needs it may not have got as far as a working `dotnet` yet).
    ///
    /// <para>Distinct from <see cref="Engine.Run"/>, and the two are easy to confuse: `vx engine`
    /// DOWNLOADS the export templates engine.lock.json pins, and `vx build-engine` COMPILES them. On
    /// x86_64 and arm64 the download is what you want. On an architecture upstream publishes nothing for
    /// — ppc64le — there is nothing to download and this is the only path.</para>
    /// </summary>
    internal static int BuildEngine(string[] args) => Env.Bash("tools/build-engine.sh", args);

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
