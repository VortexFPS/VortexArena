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
