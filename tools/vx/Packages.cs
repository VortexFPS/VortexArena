namespace Vx;

/// <summary>
/// The system package manager, and what to call the things vx needs on it.
///
/// <para><b>Why this exists.</b> "install python3" is not advice — it is a guess dressed as advice, and it
/// was wrong for the first contributor to hit it (2026-08-03, Calculate Linux: a Gentoo derivative, where
/// the answer is <c>emerge dev-lang/python</c> and no amount of squinting at <c>apt/dnf/pacman</c> gets you
/// there). Naming the real command for the machine in front of you is the whole value; a wrapper that hides
/// which command ran would give back exactly what it took.</para>
///
/// <para><b>Detection is a probe, not a lookup.</b> <c>/etc/os-release</c> tells you the distro's INTENT,
/// which is only a tiebreak: a Debian box with <c>nix</c> installed still has <c>apt</c>, and derivatives
/// routinely under-report (Calculate says <c>ID=calculate</c>, <c>ID_LIKE=gentoo</c>, and only the latter is
/// useful). So the executable on PATH decides, ordered by ID/ID_LIKE when more than one is present.</para>
///
/// <para><b>Names are not portable and are not guessed.</b> A dependency with no known package name for the
/// detected manager falls through to that manager's SEARCH command rather than to a plausible-looking
/// string — being told "here is how to look it up" is recoverable, being told to install a package that does
/// not exist is not.</para>
/// </summary>
internal sealed record PackageManager(
    string Id,
    string Exe,
    string[] Install,
    string[] Search,
    bool NeedsRoot)
{
    /// <summary>The command line that would install <paramref name="packages"/>, as a person would type it.</summary>
    internal string InstallCommand(IEnumerable<string> packages)
        => Render(Install.Concat(packages));

    internal string SearchCommand(string term) => Render(Search.Append(term));

    private string Render(IEnumerable<string> args)
        => (NeedsRoot ? "sudo " : "") + Path.GetFileName(Exe) + " " + string.Join(' ', args);
}

internal static class Packages
{
    /// <summary>
    /// Candidates in probe order. Package names live in <see cref="Names"/> keyed by the same
    /// <see cref="PackageManager.Id"/>, so adding a manager means touching exactly two places.
    ///
    /// <para>Install verbs are all NON-INTERACTIVE (<c>-y</c> and friends). That is not vx assuming consent:
    /// consent was taken before we got here, and a package manager that stops to ask a second question after
    /// the user already answered is a hang, not a safeguard. <c>emerge --ask=n</c> is the same idea in
    /// portage's spelling.</para>
    /// </summary>
    private static readonly (string Id, string[] Exe, string[] Install, string[] Search, bool Root)[] Known =
    [
        ("apt",    ["apt-get", "apt"],   ["install", "-y"],                  ["search"],       true),
        ("dnf",    ["dnf5", "dnf"],      ["install", "-y"],                  ["search"],       true),
        ("yum",    ["yum"],              ["install", "-y"],                  ["search"],       true),
        ("pacman", ["pacman"],           ["-S", "--needed", "--noconfirm"],  ["-Ss"],          true),
        ("zypper", ["zypper"],           ["--non-interactive", "install"],   ["search"],       true),
        ("apk",    ["apk"],              ["add"],                            ["search"],       true),
        ("emerge", ["emerge"],           ["--ask=n"],                        ["--search"],     true),
        ("xbps",   ["xbps-install"],     ["-Sy"],                            ["-Rs"],          true),
        ("eopkg",  ["eopkg"],            ["install", "-y"],                  ["search"],       true),
        ("brew",   ["brew"],             ["install"],                        ["search"],       false),
        ("port",   ["port"],             ["install"],                        ["search"],       true),
        ("winget", ["winget"],           ["install", "-e", "--id"],          ["search"],       false),
        ("choco",  ["choco"],            ["install", "-y"],                  ["search"],       false),
        ("scoop",  ["scoop"],            ["install"],                        ["search"],       false),
    ];

    /// <summary>
    /// Package names per manager for the dependencies vx can actually do something about. Deliberately NOT
    /// exhaustive — an unmapped pair produces a search suggestion, which is the honest answer.
    ///
    /// <para><c>xbps-query</c> rather than <c>xbps-install</c> does the searching on Void, so that one entry
    /// is a known wart: the search command below would need a different executable, and reporting a slightly
    /// wrong search line is a smaller failure than pretending Void is unsupported.</para>
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Names = new()
    {
        ["python3"] = new()
        {
            ["apt"] = "python3", ["dnf"] = "python3", ["yum"] = "python3", ["pacman"] = "python",
            ["zypper"] = "python3", ["apk"] = "python3", ["emerge"] = "dev-lang/python",
            ["xbps"] = "python3", ["eopkg"] = "python3", ["brew"] = "python3", ["port"] = "python312",
            ["winget"] = "Python.Python.3.12", ["choco"] = "python3", ["scoop"] = "python",
        },
        ["git"] = new()
        {
            ["apt"] = "git", ["dnf"] = "git", ["yum"] = "git", ["pacman"] = "git", ["zypper"] = "git",
            ["apk"] = "git", ["emerge"] = "dev-vcs/git", ["xbps"] = "git", ["eopkg"] = "git",
            ["brew"] = "git", ["port"] = "git", ["winget"] = "Git.Git", ["choco"] = "git", ["scoop"] = "git",
        },
        ["curl"] = new()
        {
            ["apt"] = "curl", ["dnf"] = "curl", ["yum"] = "curl", ["pacman"] = "curl", ["zypper"] = "curl",
            ["apk"] = "curl", ["emerge"] = "net-misc/curl", ["xbps"] = "curl", ["eopkg"] = "curl",
            ["brew"] = "curl", ["port"] = "curl", ["winget"] = "cURL.cURL", ["choco"] = "curl",
            ["scoop"] = "curl",
        },
        ["unzip"] = new()
        {
            ["apt"] = "unzip", ["dnf"] = "unzip", ["yum"] = "unzip", ["pacman"] = "unzip",
            ["zypper"] = "unzip", ["apk"] = "unzip", ["emerge"] = "app-arch/unzip", ["xbps"] = "unzip",
            ["eopkg"] = "unzip", ["brew"] = "unzip", ["port"] = "unzip", ["choco"] = "unzip",
        },
        // macOS only, and optional: it is what gives run-timeout.sh a real `gtimeout` instead of its shell
        // fallback. Named here so `vx doctor`'s suggestion is a command rather than a paragraph.
        ["coreutils"] = new() { ["brew"] = "coreutils", ["port"] = "coreutils" },
    };

    /// <summary>The detected manager, resolved once. Null when nothing recognised is on PATH.</summary>
    internal static PackageManager? Detected { get; } = Detect();

    private static PackageManager? Detect()
    {
        // /etc/os-release is a RANKING, not a filter: it decides which manager wins when a box has several,
        // and is ignored entirely when it names one that is not actually installed.
        string[] hints = OsReleaseHints();

        IEnumerable<(string Id, string[] Exe, string[] Install, string[] Search, bool Root)> ordered =
            Known.OrderBy(k =>
            {
                int i = Array.FindIndex(hints, h => Matches(h, k.Id));
                return i < 0 ? int.MaxValue : i;
            });

        foreach (var k in ordered)
            foreach (string exe in k.Exe)
                if (Env.Which(exe) is { } path)
                    return new PackageManager(k.Id, path, k.Install, k.Search, k.Root && !IsRoot());

        return null;
    }

    /// <summary>Distro ids from <c>/etc/os-release</c>: <c>ID</c> first, then each <c>ID_LIKE</c> word.</summary>
    private static string[] OsReleaseHints()
    {
        try
        {
            if (!File.Exists("/etc/os-release")) return [];
            var ids = new List<string>();
            string? like = null;
            foreach (string line in File.ReadAllLines("/etc/os-release"))
            {
                string[] kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                string value = kv[1].Trim().Trim('"');
                if (kv[0] == "ID") ids.Insert(0, value);
                else if (kv[0] == "ID_LIKE") like = value;
            }
            if (like is not null) ids.AddRange(like.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return ids.ToArray();
        }
        catch
        {
            return [];   // detection must never be the reason setup cannot run
        }
    }

    /// <summary>Distro id → manager id. Only the cases where the two names differ need to be listed.</summary>
    private static bool Matches(string distro, string manager) => manager switch
    {
        "apt" => distro is "debian" or "ubuntu" or "linuxmint" or "pop" or "raspbian",
        "dnf" => distro is "fedora" or "rhel" or "centos" or "almalinux" or "rocky",
        "pacman" => distro is "arch" or "manjaro" or "endeavouros" or "cachyos",
        "zypper" => distro is "opensuse" or "sles" or "opensuse-tumbleweed" or "opensuse-leap" or "suse",
        "apk" => distro is "alpine",
        "emerge" => distro is "gentoo" or "calculate",
        "xbps" => distro is "void",
        "eopkg" => distro is "solus",
        _ => false,
    };

    private static bool IsRoot()
    {
        // Already root (a container, a rootful CI image): prefixing sudo would then require sudo to EXIST,
        // which minimal images routinely omit — the command would fail for a reason unrelated to the package.
        if (OperatingSystem.IsWindows()) return false;
        try { return Environment.GetEnvironmentVariable("USER") == "root" || Env.Run("id", "-u").Out == "0"; }
        catch { return false; }
    }

    /// <summary>The package name for <paramref name="dep"/> on the detected manager, or null if unmapped.</summary>
    internal static string? NameFor(string dep)
        => Detected is not null && Names.TryGetValue(dep, out Dictionary<string, string>? byManager)
           && byManager.TryGetValue(Detected.Id, out string? name)
            ? name
            : null;

    /// <summary>
    /// One line telling the person how to get <paramref name="dep"/> on THIS machine: the real install
    /// command when the name is known, the manager's search command when it is not, and the upstream
    /// download page when no manager was found at all.
    /// </summary>
    internal static string Advice(string dep, string fallback)
    {
        if (Detected is null) return fallback;
        return NameFor(dep) is { } name
            ? Detected.InstallCommand([name])
            : $"{Detected.SearchCommand(dep)}    (no package name known for {Detected.Id} — search, then install)";
    }

    /// <summary>
    /// Install <paramref name="deps"/>, having already been told to. Returns the exit code of the package
    /// manager, unmodified and unwrapped: when this fails the person needs to see what really ran.
    /// </summary>
    internal static int Install(IReadOnlyCollection<string> deps)
    {
        if (Detected is null)
        {
            Console.Error.WriteLine("vx: no supported package manager on PATH — nothing to delegate to.");
            return 1;
        }

        string[] names = deps.Select(NameFor).OfType<string>().ToArray();
        string[] unmapped = deps.Where(d => NameFor(d) is null).ToArray();

        foreach (string d in unmapped)
        {
            Console.Error.WriteLine($"vx: no {Detected.Id} package name known for '{d}' — skipping it.");
            Console.Error.WriteLine($"    {Detected.SearchCommand(d)}");
        }
        if (names.Length == 0) return unmapped.Length > 0 ? 1 : 0;

        Console.WriteLine($"   {Detected.InstallCommand(names)}");

        var argv = new List<string>();
        string exe = Detected.Exe;
        if (Detected.NeedsRoot)
        {
            // sudo is resolved rather than assumed, so "sudo is not installed" reads as itself instead of as
            // a mystery 127 from the package manager.
            string? sudo = Env.Which("sudo");
            if (sudo is null)
            {
                Console.Error.WriteLine($"vx: this needs root and sudo is not on PATH. Run it yourself as root:");
                Console.Error.WriteLine($"    {Detected.InstallCommand(names)}");
                return 1;
            }
            argv.Add(Detected.Exe);
            exe = sudo;
        }
        argv.AddRange(Detected.Install);
        argv.AddRange(names);

        // Stdio is attached, which is what lets sudo prompt for a password and the manager show progress.
        int rc = Env.Exec(exe, argv);
        if (rc != 0)
            Console.Error.WriteLine($"vx: {Detected.Id} exited {rc} — the command above is the one to re-run by hand.");
        return rc == 0 && unmapped.Length > 0 ? 1 : rc;
    }
}
