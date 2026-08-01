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
        string config = ValueOf(args, "--config") ?? "Debug";
        string? dotnet = Env.Which("dotnet");
        if (dotnet is null) { NoDotnet(); return 1; }
        return Env.Exec(dotnet, ["build", Path.Combine(Env.RepoRoot, "VortexArena.csproj"), "-c", config, "--nologo"]);
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

    /// <summary>Launch the client on the PROJECT (not an export). Extra args go to the game unchanged.</summary>
    internal static int RunClient(string[] args)
    {
        string? godot = Env.FindGodot();
        if (godot is null) { NoGodot(); return 1; }
        var argv = new List<string> { "--path", Env.RepoRoot };
        argv.AddRange(args);
        return Env.Exec(godot, argv);
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
    private static readonly (string Preset, string Out)[] Presets =
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
            int rc = Env.Exec(godot, ["--headless", "--path", Env.RepoRoot, "--export-release", preset, outPath]);

            // Godot's headless export exits NON-ZERO on a fully successful export often enough that
            // run-release.sh stopped trusting the code entirely and gates on the artifact instead. Same
            // judgement here: the binary existing is the result, the exit code is an opinion.
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
        }
        return 0;
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
    internal static int Perf(string[] args)
    {
        if (Env.IsWindows)
        {
            string? pwsh = Env.Which("pwsh") ?? Env.Which("powershell");
            if (pwsh is not null)
            {
                var argv = new List<string> { "-File", Path.Combine(Env.RepoRoot, "tools", "perf-run.ps1") };
                argv.AddRange(args);
                return Env.Exec(pwsh, argv);
            }
        }
        return Env.Bash("tools/perf-run.sh", args);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static string? ValueOf(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

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
