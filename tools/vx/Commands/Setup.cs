using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Vx.Commands;

/// <summary>
/// <c>vx setup</c> — bring a clone to a runnable state. "<see cref="Doctor"/>, plus act on what it found."
///
/// <para><b>Install policy, which is the part worth getting right.</b> Installing software is the most
/// invasive thing a setup script does and where these tools usually earn their reputation. So: nothing
/// happens without an explicit yes (<c>--yes</c>, or an interactive confirmation — never a default), and
/// everything vx installs on its own authority goes into <c>.godot-bin/</c> INSIDE the clone rather than
/// system-wide, so uninstalling is <c>rm -rf</c> and two clones can pin two engine versions. Every action
/// prints the command it is really running.</para>
///
/// <para><b>System packages are opt-in twice</b> (<c>--install-deps</c>, 2026-08-03). The rule used to be
/// that vx never ran a package manager at all — it printed <c>"apt/dnf/pacman install python3"</c> and left
/// the rest to you. That held up badly the first time someone outside the usual three distros tried it: on a
/// Gentoo derivative the right answer is <c>emerge dev-lang/python</c>, which that string does not contain
/// and does not hint at. So the line moved, but only by one step:</para>
/// <list type="bullet">
///   <item><b>By default, still nothing.</b> The exact command for the DETECTED manager is printed and the
///         machine is untouched — strictly better advice than before, same blast radius.</item>
///   <item><b><c>--install-deps</c> adds them to the plan</b>, where they are listed with the literal
///         command line and then gated on the same confirmation as everything else. Two deliberate acts:
///         asking for the flag, and confirming the plan.</item>
///   <item><b>sudo is run, not printed</b> — but only along that path, only with stdio attached so the
///         password prompt is the real one, and only after the command has been shown. It is never wrapped:
///         when a package manager fails, the person needs to see what actually ran.</item>
/// </list>
/// <para>The <c>ci</c> and <c>launcher</c> profiles refuse system installs outright. Both run unattended, a
/// sudo prompt there is a hang rather than a question, and neither should be reshaping a host it does not
/// own. Package-name and manager detection live in <see cref="Packages"/>.</para>
/// </summary>
internal static class Setup
{
    /// <param name="MaySystemInstall">
    /// Whether <c>--install-deps</c> is honoured at all. False for the two unattended profiles: reshaping a
    /// host you were merely invoked on is not the same permission as setting up the clone you were pointed at.
    /// </param>
    private sealed record Profile(string Name, string Blurb, bool Godot, bool Maps, bool Templates,
                                  bool MayPrompt, bool MaySystemInstall);

    private static readonly Profile[] Profiles =
    [
        new("play",     "run the game: engine + maps",                    true,  true,  true,  true,  true),
        new("dev",      "develop: engine + maps + export templates",      true,  true,  true,  true,  true),
        new("server",   "dedicated server: maps only, no editor",         false, true,  true,  true,  true),
        new("ci",       "non-interactive; installs nothing it is not told to", true,  true,  true,  false, false),
        // Godot went true 2026-08-06. This profile was "content only" because the launcher resolved its
        // own engine and refused when it found none — which meant an operator's first build failed with
        // instructions to go and install by hand, while the tool that installs the RIGHT one by
        // construction sat in the clone it had just cloned. VortexLauncher now calls this profile when no
        // editor is present, so the profile has to be able to supply one. MayPrompt/MaySystemInstall stay
        // false: it still runs unattended and still has no business reshaping a host it was invoked on.
        new("launcher", "invoked by VortexLauncher; engine + content",    true,  true,  true,  false, false),
    ];

    /// <summary>System dependencies vx knows how to name, in the order a fresh clone needs them.</summary>
    private static readonly (string Dep, Func<bool> Missing, string Fallback)[] SystemDeps =
    [
        ("python3", () => Env.FindPython() is null,
            "install Python 3 — https://www.python.org/downloads/ (tick 'Add to PATH')"),
        ("git", () => Env.Which("git") is null, "install git — https://git-scm.com/downloads"),
        ("curl", () => Env.Which("curl") is null, "install curl"),
        ("unzip", () => Env.Which("unzip") is null, "install unzip"),
    ];

    internal static int Run(string[] args, bool json)
    {
        bool yes = args.Contains("--yes") || args.Contains("-y");
        bool dryRun = args.Contains("--dry-run");
        bool installDeps = args.Contains("--install-deps");
        string? profileName = ValueOf(args, "--profile");

        // Interactive only when there is a human AND no profile was named. A profile is the non-interactive
        // contract that CI and the launcher use, so passing one must never open a prompt.
        bool interactive = profileName is null && !yes && !Console.IsInputRedirected && !Console.IsOutputRedirected;

        Profile profile;
        if (profileName is not null)
        {
            Profile? p = Profiles.FirstOrDefault(x => x.Name == profileName);
            if (p is null)
            {
                Console.Error.WriteLine($"vx setup: unknown profile '{profileName}'");
                Console.Error.WriteLine($"          available: {string.Join(", ", Profiles.Select(x => x.Name))}");
                return 2;
            }
            profile = p;
        }
        else if (interactive)
        {
            profile = AskProfile();
        }
        else
        {
            profile = Profiles.First(x => x.Name == "dev");
            Console.WriteLine("vx setup: no --profile and not a terminal — assuming 'dev'.");
        }

        Console.WriteLine();
        Console.WriteLine($"vx setup — profile '{profile.Name}' ({profile.Blurb})");
        Console.WriteLine();

        // ---- plan ------------------------------------------------------------------------------------
        var plan = new List<(string What, Func<int> Act)>();
        var manual = new List<string>();

        if (Env.Which("dotnet") is null)
            manual.Add("install the .NET SDK 8.0+ — https://dotnet.microsoft.com/download");

        string[] missingDeps = SystemDeps.Where(d => d.Missing()).Select(d => d.Dep).ToArray();
        if (missingDeps.Length > 0)
        {
            bool canInstall = installDeps && profile.MaySystemInstall && Packages.Detected is not null;
            if (canInstall)
            {
                // Named in the plan line with the LITERAL command, because "install 2 packages" is not
                // something anyone can meaningfully consent to and this is the one step that touches the
                // machine rather than the clone.
                string[] known = missingDeps.Where(d => Packages.NameFor(d) is not null).ToArray();
                if (known.Length > 0)
                    plan.Add(($"{Packages.Detected!.InstallCommand(known.Select(Packages.NameFor)!)}",
                              () => Packages.Install(known)));
                foreach (string d in missingDeps.Except(known))
                    manual.Add(Packages.Advice(d, Fallback(d)));
            }
            else
            {
                foreach (string d in missingDeps)
                    manual.Add(Packages.Advice(d, Fallback(d)));

                if (installDeps && !profile.MaySystemInstall)
                    manual.Add($"(--install-deps is ignored for the '{profile.Name}' profile — it runs unattended)");
                else if (installDeps && Packages.Detected is null)
                    manual.Add("(--install-deps found no supported package manager on PATH)");
                else if (Packages.Detected is not null)
                    manual.Add($"...or re-run with --install-deps to have vx run {Packages.Detected.Id} for you");
            }
        }

        if (profile.Godot && Env.FindGodot() is null)
            plan.Add(($"install Godot {GodotVersion()} (mono) into .godot-bin/", InstallGodot));

        if (profile.Maps && MapsIncomplete())
            plan.Add(("fetch the map packs pinned by data/maps.lock.json", () => Maps.Run([], json: false)));

        if (profile.Templates && !TemplatesPresent())
            plan.Add(("fetch the export templates pinned by engine.lock.json", () => Engine.Run([], json: false)));

        if (manual.Count > 0)
        {
            Console.WriteLine(Packages.Detected is null
                ? "These are yours to install — no package manager vx recognises is on PATH:"
                : $"These are yours to install ({Packages.Detected.Id} detected):");
            foreach (string m in manual) Console.WriteLine($"  • {m}");
            Console.WriteLine();
        }

        if (plan.Count == 0)
        {
            Console.WriteLine(manual.Count > 0
                ? "Nothing else for vx to do until the above are installed."
                : "Nothing to do — this clone is already set up. (`./vx doctor` for detail.)");
            return manual.Count > 0 ? 1 : 0;
        }

        Console.WriteLine("Plan:");
        for (int i = 0; i < plan.Count; i++) Console.WriteLine($"  {i + 1}. {plan[i].What}");
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("--dry-run: nothing was changed.");
            return 0;
        }

        if (!yes)
        {
            if (!profile.MayPrompt || !interactive)
            {
                // Refusing is the right answer, not proceeding. A setup that installs without consent
                // because it could not find a terminal is exactly the behaviour this policy exists to stop.
                Console.Error.WriteLine("vx setup: no confirmation possible here (not a terminal). Re-run with --yes to proceed,");
                Console.Error.WriteLine("          or --dry-run to see the plan without doing anything.");
                return 1;
            }
            Console.Write($"Proceed with {plan.Count} action(s)? [y/N] ");
            string? answer = Console.ReadLine();
            if (answer is null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Nothing was changed.");
                return 0;
            }
            Console.WriteLine();
        }

        foreach ((string what, Func<int> act) in plan)
        {
            Console.WriteLine($"→ {what}");
            int rc = act();
            if (rc != 0)
            {
                Console.Error.WriteLine($"vx setup: step failed ({rc}) — stopping. Fix it and re-run; setup is resumable.");
                return rc;
            }
            Console.WriteLine();
        }

        Console.WriteLine("Setup complete. `./vx doctor` to confirm.");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------

    private static Profile AskProfile()
    {
        Console.WriteLine();
        Console.WriteLine("What are you setting this clone up for?");
        for (int i = 0; i < Profiles.Length; i++)
            Console.WriteLine($"  {i + 1}. {Profiles[i].Name,-9} {Profiles[i].Blurb}");
        Console.Write($"Choice [1-{Profiles.Length}, default 2=dev]: ");
        string? line = Console.ReadLine();
        return int.TryParse(line?.Trim(), out int n) && n >= 1 && n <= Profiles.Length
            ? Profiles[n - 1]
            : Profiles.First(x => x.Name == "dev");
    }

    private static string? ValueOf(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// What to say about <paramref name="dep"/> when no package manager can name it. Apple's python3 is the
    /// one case where the answer is not a package at all but a Command Line Tools install.
    /// </summary>
    private static string Fallback(string dep)
        => dep == "python3" && Env.IsMacOS
            ? "xcode-select --install    (provides python3)"
            : SystemDeps.First(d => d.Dep == dep).Fallback;

    private static JsonNode GodotLock()
    {
        string p = Path.Combine(Env.RepoRoot, "tools", "godot.lock.json");
        if (!File.Exists(p)) throw new FileNotFoundException($"missing {p}");
        return JsonNode.Parse(File.ReadAllText(p))!;
    }

    private static string GodotVersion()
    {
        try { return GodotLock()["engine"]!["version"]!.GetValue<string>(); }
        catch { return "4.6.3"; }
    }

    private static bool MapsIncomplete()
    {
        string lockPath = Path.Combine(Env.RepoRoot, "data", "maps.lock.json");
        string dir = Path.Combine(Env.RepoRoot, "data", "maps");
        if (!File.Exists(lockPath)) return false;
        try
        {
            int pinned = JsonNode.Parse(File.ReadAllText(lockPath))!["packs"]!.AsObject().Count;
            int have = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.pk3").Length : 0;
            return have < pinned;
        }
        catch { return false; }
    }

    private static bool TemplatesPresent()
        => Directory.Exists(Path.Combine(Env.RepoRoot, "tools", "engine-templates"))
           && Directory.GetFiles(Path.Combine(Env.RepoRoot, "tools", "engine-templates")).Length > 0;

    /// <summary>
    /// Download, verify and unpack the pinned editor into <c>.godot-bin/</c>, normalising the layout so
    /// <c>tools/lib/find-godot.sh</c>'s probe paths hold regardless of how upstream names the archive.
    /// </summary>
    private static int InstallGodot()
    {
        JsonNode lockDoc = GodotLock();
        string key = Env.IsMacOS ? "macos" : Env.IsWindows ? "windows" : "linux";
        JsonNode? plat = lockDoc["platforms"]?[key];
        if (plat is null)
        {
            Console.Error.WriteLine($"vx setup: tools/godot.lock.json pins no '{key}' build.");
            return 1;
        }

        string url = plat["url"]!.GetValue<string>();
        string want = plat["sha256"]!.GetValue<string>();
        long size = plat["bytes"]!.GetValue<long>();
        string binDir = Path.Combine(Env.RepoRoot, ".godot-bin");
        Directory.CreateDirectory(binDir);
        string zip = Path.Combine(binDir, plat["filename"]!.GetValue<string>());

        Console.WriteLine($"   {url}");
        Console.WriteLine($"   {size / (double)(1 << 20):F0} MB");
        if (!(File.Exists(zip) && new FileInfo(zip).Length == size))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using HttpResponseMessage resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url),
                HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using Stream net = resp.Content.ReadAsStream();
            using var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            net.CopyTo(fs, 1 << 20);
        }

        string got;
        using (var fs = File.OpenRead(zip)) got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            Console.Error.WriteLine($"vx setup: sha256 mismatch for the Godot archive");
            Console.Error.WriteLine($"          expected {want}");
            Console.Error.WriteLine($"          got      {got}");
            Console.Error.WriteLine("          refusing to install — the lockfile and the download disagree");
            return 1;
        }
        Console.WriteLine("   sha256 verified");

        string staging = Path.Combine(binDir, ".unpack");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        Normalise(staging, binDir);
        Directory.Delete(staging, true);
        File.Delete(zip);

        string? found = Env.FindGodot();
        if (found is null)
        {
            Console.Error.WriteLine("vx setup: unpacked, but no engine at the expected path in .godot-bin/.");
            return 1;
        }
        Console.WriteLine($"   installed: {found}");
        if (Env.IsMacOS)
            Console.WriteLine("   note: macOS quarantines downloaded bundles — the first launch may need Gatekeeper approval.");
        return 0;
    }

    /// <summary>
    /// Move the unpacked payload to the fixed names find-godot.sh probes. Upstream archive layouts carry the
    /// version in every path, so without this the probe would have to change on every engine bump — and the
    /// shell and C# resolvers would both have to be edited in step, which is exactly the drift risk that
    /// having one resolution order exists to remove.
    /// </summary>
    private static void Normalise(string staging, string binDir)
    {
        if (Env.IsMacOS)
        {
            string? app = Directory.GetDirectories(staging, "*.app", SearchOption.AllDirectories).FirstOrDefault()
                          ?? Directory.GetDirectories(staging).FirstOrDefault();
            if (app is null) return;
            string dest = Path.Combine(binDir, "Godot.app");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(app, dest);
            // ZipFile does not preserve the Unix exec bit, so the extracted binary is not runnable.
            string exe = Path.Combine(dest, "Contents", "MacOS", "Godot");
            if (File.Exists(exe)) MakeExecutable(exe);
            return;
        }

        // The payload is inside a single top-level directory for the mono archives and loose at the root
        // for the plain ones. Take whichever this is.
        string[] topDirs = Directory.GetDirectories(staging);
        string root = topDirs.Length == 1 && Directory.GetFiles(staging).Length == 0 ? topDirs[0] : staging;

        // MOVE THE DIRECTORIES TOO, not only the executable.
        //
        // This loop used to walk files with SearchOption.AllDirectories and keep just the one whose name
        // matched, discarding everything else with the staging tree. That is correct for the PLAIN builds,
        // which are a lone binary, and silently wrong for the MONO ones: those ship a GodotSharp/ directory
        // of managed assemblies that has to sit beside the executable. Without it the editor still starts,
        // exits 0, and prints NOTHING — not even for --version — so every caller sees a binary that is
        // present, runnable and unidentifiable, and the failure surfaces much later as "engine unusable".
        // Measured on Ubuntu 24.04 against the pinned Godot_v4.6.3-stable_mono_linux_x86_64.zip.
        foreach (string dir in Directory.GetDirectories(root))
        {
            string dest = Path.Combine(binDir, Path.GetFileName(dir));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(dir, dest);
        }

        foreach (string f in Directory.GetFiles(root))
        {
            string name = Path.GetFileName(f);
            // Unmatched files keep their own name rather than being dropped: the archives carry
            // LICENSE/README beside the binary, and there is no reason to be selective now that the
            // directory beside them is being kept.
            string dest = Env.IsWindows
                ? name.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase) ? "godot_console.exe"
                  : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "godot.exe" : name
                : name.Contains("linux", StringComparison.OrdinalIgnoreCase) ? "godot" : name;

            string target = Path.Combine(binDir, dest);
            File.Move(f, target, overwrite: true);
            // ZipFile does not preserve the Unix exec bit, so only the engine itself needs it back.
            if (!Env.IsWindows && dest == "godot") MakeExecutable(target);
        }
    }

    /// <summary>
    /// ZipFile does not carry the Unix exec bit across, so an extracted engine is not runnable until this
    /// runs — the symptom being "permission denied" from a binary that looks perfectly present.
    /// </summary>
    private static void MakeExecutable(string path)
    {
        // OperatingSystem.IsWindows() rather than Env.IsWindows: identical at runtime, but the platform-
        // compatibility analyzer can only reason about the former, and silencing CA1416 with a pragma would
        // hide the next genuinely unguarded call.
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

}
