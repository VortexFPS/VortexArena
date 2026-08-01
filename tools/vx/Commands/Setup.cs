using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Vx.Commands;

/// <summary>
/// <c>vx setup</c> — bring a clone to a runnable state. "<see cref="Doctor"/>, plus act on what it found."
///
/// <para><b>Install policy, which is the part worth getting right.</b> Installing software is the most
/// invasive thing a setup script does and where these tools usually earn their reputation. So: nothing
/// happens without an explicit yes (<c>--yes</c>, or an interactive confirmation — never a default); no
/// <c>sudo</c> is ever run, only printed for the person to run themselves; and everything vx installs goes
/// into <c>.godot-bin/</c> INSIDE the clone rather than system-wide, so uninstalling is <c>rm -rf</c> and
/// two clones can pin two engine versions. Every action prints the command it is really running.</para>
///
/// <para>Package managers are a suggestion, not a mechanism: missing system dependencies are reported with
/// the exact command for the platform and never wrapped, because when one fails the person needs to see the
/// real command rather than vx's abstraction of it.</para>
/// </summary>
internal static class Setup
{
    private sealed record Profile(string Name, string Blurb, bool Godot, bool Maps, bool Templates, bool MayPrompt);

    private static readonly Profile[] Profiles =
    [
        new("play",     "run the game: engine + maps",                    true,  true,  true,  true),
        new("dev",      "develop: engine + maps + export templates",      true,  true,  true,  true),
        new("server",   "dedicated server: maps only, no editor",         false, true,  true,  true),
        new("ci",       "non-interactive; installs nothing it is not told to", true,  true,  true,  false),
        new("launcher", "invoked by VortexLauncher; content only",        false, true,  true,  false),
    ];

    internal static int Run(string[] args, bool json)
    {
        bool yes = args.Contains("--yes") || args.Contains("-y");
        bool dryRun = args.Contains("--dry-run");
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
        if (Env.FindPython() is null)
            manual.Add(Env.IsMacOS ? "xcode-select --install    (provides python3)"
                : Env.IsWindows ? "install Python 3 — https://www.python.org/downloads/ (tick 'Add to PATH')"
                : "sudo apt install python3    (or dnf/pacman equivalent)");

        if (profile.Godot && Env.FindGodot() is null)
            plan.Add(($"install Godot {GodotVersion()} (mono) into .godot-bin/", InstallGodot));

        if (profile.Maps && MapsIncomplete())
            plan.Add(("fetch the map packs pinned by data/maps.lock.json", () => Maps.Run([], json: false)));

        if (profile.Templates && !TemplatesPresent())
            plan.Add(("fetch the export templates pinned by engine.lock.json", () => Engine.Run([], json: false)));

        if (manual.Count > 0)
        {
            Console.WriteLine("These are yours to install — vx will not run a package manager or sudo for you:");
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

        foreach (string f in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(f);
            string? dest = Env.IsWindows
                ? name.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase) ? "godot_console.exe"
                  : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "godot.exe" : null
                : name.Contains("linux", StringComparison.OrdinalIgnoreCase) ? "godot" : null;
            if (dest is null) continue;
            string target = Path.Combine(binDir, dest);
            File.Move(f, target, overwrite: true);
            if (!Env.IsWindows) MakeExecutable(target);
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
