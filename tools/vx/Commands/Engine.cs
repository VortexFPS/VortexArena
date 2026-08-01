using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Vx.Commands;

/// <summary>
/// <c>vx engine</c> — the export templates a release is built from, pinned by
/// <c>tools/engine-patches/engine.lock.json</c>.
///
/// <para>A port of <c>tools/data/fetch-engine-template.py</c>, for the same reason
/// <see cref="Maps"/> was ported and with more urgency: it shares the urllib TLS failure, so on a machine
/// whose Python has no CA bundle <c>vx setup</c> got all the way through installing the engine and 700 MB
/// of maps and then died on this step. Fixing maps alone left the bootstrap broken one step later.</para>
///
/// <para>Note this fetches templates, NOT the editor. The editor is pinned separately in
/// <c>tools/godot.lock.json</c> and installed by <see cref="Setup"/> — one is what a developer runs, the
/// other is what gets embedded in a shipped build, and conflating them is how a release ends up built on
/// whatever engine the exporting machine happened to have.</para>
/// </summary>
internal static class Engine
{
    private const int Retries = 4;
    private const int Chunk = 1 << 20;

    internal static int Run(string[] args, bool json)
    {
        if (args.Contains("--editor"))
            return InstallEditorTemplates(args.Contains("--force"));

        bool verifyOnly = args.Contains("--verify-only");
        bool force = args.Contains("--force");
        var only = args.Where((a, i) => i > 0 && args[i - 1] == "--only").ToList();

        string root = Env.RepoRoot;
        string lockPath = Path.Combine(root, "tools", "engine-patches", "engine.lock.json");
        string dest = Path.Combine(root, "tools", "engine-templates");

        if (!File.Exists(lockPath))
        {
            Console.Error.WriteLine($"vx engine: {lockPath} not found");
            return 1;
        }

        JsonNode lockDoc = JsonNode.Parse(File.ReadAllText(lockPath))!;
        JsonObject platforms = lockDoc["template"]?["platforms"]?.AsObject()
                               ?? throw new InvalidOperationException("engine.lock.json has no template.platforms");

        var wanted = platforms
            .Where(kv => only.Count == 0 || only.Contains(kv.Key))
            .ToList();

        if (only.Count > 0)
        {
            var unknown = only.Where(o => !platforms.ContainsKey(o)).ToList();
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine($"vx engine: unknown platform(s): {string.Join(", ", unknown)}");
                Console.Error.WriteLine($"           pinned: {string.Join(", ", platforms.Select(p => p.Key))}");
                return 1;
            }
        }

        string tag = lockDoc["template"]?["tag"]?.GetValue<string>() ?? "?";
        Console.WriteLine($"engine templates pinned by {tag}: {string.Join(", ", wanted.Select(w => w.Key))}");

        var stale = new List<(string Name, string File, string Url, string Sha, long Size)>();
        foreach ((string name, JsonNode? e) in wanted)
        {
            string file = e!["filename"]!.GetValue<string>();
            string want = e["sha256"]!.GetValue<string>();
            string target = Path.Combine(dest, file);
            if (!force && File.Exists(target) && Sha256File(target).Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {name}: present and matches");
                continue;
            }
            stale.Add((name, file, e["url"]!.GetValue<string>(), want, e["bytes"]!.GetValue<long>()));
        }

        if (stale.Count == 0)
        {
            Console.WriteLine("everything is present and matches the lockfile");
            return 0;
        }

        if (verifyOnly)
        {
            Console.WriteLine($"\n{stale.Count} template(s) missing or mismatched:");
            foreach (var s in stale) Console.WriteLine($"  {s.Name,-10} {s.File}");
            Console.WriteLine("\nrun ./vx engine to fix");
            return 1;
        }

        Directory.CreateDirectory(dest);
        foreach ((string name, string file, string url, string want, long size) in stale)
        {
            Console.WriteLine($"  {name}: fetching {file} ({size / (double)(1 << 20):F0} MB)");
            string target = Path.Combine(dest, file);
            string partial = target + ".part";
            try
            {
                Download(url, partial, size);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vx engine: download failed for {file}: {ex.Message}");
                return 1;
            }

            string got = Sha256File(partial);
            if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                Console.Error.WriteLine($"vx engine: sha256 mismatch for {file}");
                Console.Error.WriteLine($"           expected {want}");
                Console.Error.WriteLine($"           got      {got}");
                Console.Error.WriteLine("           refusing to install — a template that is not the pinned one");
                Console.Error.WriteLine("           would silently ship a different engine than the lockfile claims");
                return 1;
            }
            File.Move(partial, target, overwrite: true);
        }

        Console.WriteLine($"\ndone — {stale.Count} template(s) under tools/engine-templates/");
        return 0;
    }

    /// <summary>
    /// Install Godot's OWN export templates (the Manage-Export-Templates set) for the pinned engine.
    ///
    /// <para>THE ONE PLACE vx WRITES OUTSIDE THE CLONE, because Godot resolves these from a fixed per-user
    /// directory it owns — unlike the editor binary, which .godot-bin/ can hold. Says where it is writing
    /// before it does. A .tpz is a zip whose members sit under <c>templates/</c>; they are flattened into
    /// the version directory, which is the layout the editor expects.</para>
    ///
    /// <para>Only presets with an empty <c>custom_template/release</c> need this — today just macos-client,
    /// a declared exception in engine.lock.json. The other three embed the pinned custom templates and are
    /// unaffected.</para>
    /// </summary>
    private static int InstallEditorTemplates(bool force)
    {
        string lockPath = Path.Combine(Env.RepoRoot, "tools", "godot.lock.json");
        JsonNode? e = JsonNode.Parse(File.ReadAllText(lockPath))!["editor_templates"];
        if (e is null) { Console.Error.WriteLine("vx engine --editor: godot.lock.json pins no editor_templates"); return 1; }

        string want = e["sha256"]!.GetValue<string>();
        long size = e["bytes"]!.GetValue<long>();
        string dirName = e["install_dir"]!.GetValue<string>();

        string root = Env.IsWindows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "export_templates")
            : Env.IsMacOS
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Godot", "export_templates")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "godot", "export_templates");
        string dest = Path.Combine(root, dirName);

        if (!force && Directory.Exists(dest) && Directory.GetFiles(dest).Length > 0)
        {
            Console.WriteLine($"editor templates already installed: {dest}");
            return 0;
        }

        Console.WriteLine($"vx engine --editor: installing Godot's export templates");
        Console.WriteLine($"   {size / (double)(1 << 20):F0} MB  ->  {dest}");
        Console.WriteLine("   (outside the clone: Godot resolves these from a fixed per-user directory)");

        Directory.CreateDirectory(root);
        string tpz = Path.Combine(root, e["filename"]!.GetValue<string>());
        if (!(File.Exists(tpz) && new FileInfo(tpz).Length == size))
        {
            try { Download(e["url"]!.GetValue<string>(), tpz, size); }
            catch (Exception ex) { Console.Error.WriteLine($"vx engine --editor: download failed: {ex.Message}"); return 1; }
        }

        string got = Sha256File(tpz);
        if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tpz);
            Console.Error.WriteLine($"vx engine --editor: sha256 mismatch\n   expected {want}\n   got      {got}");
            return 1;
        }
        Console.WriteLine("   sha256 verified");

        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        using (ZipArchive zip = ZipFile.OpenRead(tpz))
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.Length == 0 || entry.FullName.EndsWith('/')) continue;
                // Flatten: the archive nests everything under templates/, the editor wants them directly
                // in <version>/ and matches by file NAME.
                entry.ExtractToFile(Path.Combine(dest, Path.GetFileName(entry.FullName)), overwrite: true);
            }
        File.Delete(tpz);

        int n = Directory.GetFiles(dest).Length;
        Console.WriteLine($"   installed {n} template file(s)");
        return n > 0 ? 0 : 1;
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Same resume/retry shape as <see cref="Maps"/>; see that file for why each branch exists.</summary>
    private static void Download(string url, string partial, long size)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < Retries; attempt++)
        {
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (have == size) return;
            if (have > size) { File.Delete(partial); have = 0; }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using HttpResponseMessage resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                FileMode mode = (have > 0 && resp.StatusCode == HttpStatusCode.PartialContent)
                    ? FileMode.Append : FileMode.Create;
                using Stream net = resp.Content.ReadAsStream();
                using var fs = new FileStream(partial, mode, FileAccess.Write, FileShare.None, Chunk);
                net.CopyTo(fs, Chunk);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
                if (attempt < Retries - 1)
                {
                    int delay = 1 << attempt;
                    Console.WriteLine($"    retrying in {delay}s ({ex.Message})");
                    Thread.Sleep(delay * 1000);
                }
            }
        }
        throw last ?? new IOException("download failed with no recorded error");
    }
}
