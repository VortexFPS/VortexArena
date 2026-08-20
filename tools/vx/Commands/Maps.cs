using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vx.Commands;

/// <summary>
/// <c>vx maps</c> — install the compiled map packs pinned by <c>data/maps.lock.json</c>.
///
/// <para><b>A PORT of tools/data/fetch-maps.py, not a reimplementation.</b> The behaviours below were
/// arrived at against real transfers and are preserved deliberately: resume via <c>Range</c>, retry each
/// mirror with exponential backoff, treat a <c>200</c> answer to a ranged request as "the server ignored
/// Range, start over", discard a file longer than the pinned size, hash before installing, and install by
/// renaming so there is no window in which a half-written pack is mountable. The Python version stays in
/// the tree and stays the reference; this is not a rewrite of the reasoning, only of the language.</para>
///
/// <para><b>Why it moved.</b> python.org's macOS installer ships its own OpenSSL and ignores the system
/// keychain, so <c>urllib</c> cannot verify HTTPS until <c>Install Certificates.command</c> has been run —
/// meaning a fresh clone on a Mac could fetch neither maps nor engine templates, with the symptom appearing
/// four retries deep as <c>CERTIFICATE_VERIFY_FAILED</c>. .NET's <see cref="HttpClient"/> uses the platform
/// trust store, so the failure class disappears rather than being worked around. This is stage 2 of the
/// migration in planning/bootstrap-and-task-runner-2026-08-01.md, brought forward for that reason.</para>
///
/// <para><c>--rebuild</c> is NOT ported: it drives a q3map2 compile and a <c>publish.py</c> in the
/// <c>maps-src</c> submodule — a Python build pipeline in another repository. It delegates to
/// <c>tools/data/fetch-maps.py</c>, which is the right layer for it.</para>
/// </summary>
internal static class Maps
{
    private const int Retries = 4;
    private const int Chunk = 1 << 20;
    private const string UserAgent = "VortexArena-vx-maps/1";
    private const int JsonSchemaVersion = 1;

    internal static int Run(string[] args, bool json)
    {
        bool verifyOnly = args.Contains("--verify-only");
        bool force = args.Contains("--force");
        bool rebuild = args.Contains("--rebuild");
        var only = args.Where((a, i) => i > 0 && args[i - 1] == "--only").ToList();

        string root = Env.RepoRoot;
        string lockPath = Path.Combine(root, "data", "maps.lock.json");
        string dest = Path.Combine(root, "data", "maps");

        if (rebuild)
            return Rebuild(args, root);

        if (!File.Exists(lockPath))
        {
            Console.Error.WriteLine($"vx maps: {lockPath} not found — nothing to fetch");
            return 1;
        }

        JsonNode lockDoc = JsonNode.Parse(File.ReadAllText(lockPath))
                           ?? throw new InvalidOperationException("maps.lock.json is empty");
        int schema = lockDoc["schema"]?.GetValue<int>() ?? -1;
        if (schema != 1)
        {
            Console.Error.WriteLine($"vx maps: unsupported lockfile schema {schema}");
            return 1;
        }

        var packs = new SortedDictionary<string, Pack>(StringComparer.Ordinal);
        foreach (var kv in lockDoc["packs"]!.AsObject())
        {
            JsonNode e = kv.Value!;
            packs[kv.Key] = new Pack(
                kv.Key,
                e["size"]!.GetValue<long>(),
                e["sha256"]!.GetValue<string>(),
                e["urls"]!.AsArray().Select(u => u!.GetValue<string>()).ToArray());
        }

        if (!json)
            Console.WriteLine($"{packs.Count} packs pinned by maps.lock.json " +
                              $"(release {lockDoc["release"]} of {lockDoc["source"]})");

        if (only.Count > 0)
        {
            // A typo must not read as success. Silently fetching nothing would leave the caller — ci.sh's
            // host smoke, say — believing it had the map, failing later with something far less obvious.
            var unknown = only.Where(m => !packs.ContainsKey(m)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine($"vx maps: unknown map(s): {string.Join(", ", unknown)}");
                Console.Error.WriteLine($"         pinned: {string.Join(", ", packs.Keys)}");
                return 1;
            }
            foreach (string k in packs.Keys.ToList())
                if (!only.Contains(k)) packs.Remove(k);
            if (!json) Console.WriteLine($"--only: restricted to {string.Join(", ", packs.Keys)}");
        }

        // Skipped under --only: this sweeps the whole maps dir, and a targeted fetch has no business
        // removing artefacts belonging to maps it was not asked about.
        if (!verifyOnly && only.Count == 0)
        {
            int removed = CleanLegacyLayout(dest);
            if (removed > 0 && !json)
                Console.WriteLine($"removed {removed} extracted .pk3dir left by the previous fetch scheme");
        }

        var stale = new List<(Pack Pack, string? Current)>();
        foreach (Pack p in packs.Values)
        {
            string target = Path.Combine(dest, p.Name + ".pk3");
            string? current = force ? null : InstalledDigest(target, p.Size);
            if (!string.Equals(current, p.Sha256, StringComparison.OrdinalIgnoreCase))
                stale.Add((p, current));
        }

        if (stale.Count == 0)
        {
            if (json) EmitJson("maps", true, packs.Count, 0, Array.Empty<string>());
            else Console.WriteLine("everything is present and matches the lockfile");
            return 0;
        }

        if (verifyOnly)
        {
            if (json) EmitJson("maps", false, packs.Count, stale.Count, stale.Select(s => s.Pack.Name).ToArray());
            else
            {
                Console.WriteLine($"\n{stale.Count} pack(s) missing or mismatched:");
                foreach ((Pack p, string? cur) in stale)
                    Console.WriteLine($"  {p.Name,-24} {(cur is null ? "missing" : "has " + cur[..12])}, want {p.Sha256[..12]}");
                Console.WriteLine("\nrun ./vx maps to fix");
            }
            return 1;
        }

        Directory.CreateDirectory(dest);
        long totalBytes = stale.Sum(s => s.Pack.Size);
        if (!json)
        {
            // A .pk3 is installed exactly as it arrives — it stays a zip and the VFS mounts it — so the
            // download size IS the disk cost. Nothing else vx fetches has that property, hence saying so.
            Console.WriteLine($"fetching {stale.Count} pack(s), {Env.HumanBytes(totalBytes)} "
                              + "(installed as-is, so that is also the disk cost)");
            if (Env.SpaceNote(totalBytes, dest, headroom: 1.05) is { } note)
                Console.WriteLine($"  {note}");
            Console.WriteLine();
        }

        var installed = new List<string>();
        for (int i = 0; i < stale.Count; i++)
        {
            Pack p = stale[i].Pack;
            string target = Path.Combine(dest, p.Name + ".pk3");
            string partial = target + ".part";
            if (!json)
                Console.WriteLine($"  [{i + 1}/{stale.Count}] {p.Name} ({Env.HumanBytes(p.Size)})");

            try
            {
                Download(p, partial, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vx maps: could not download {p.Name}.pk3 after {Retries} attempts: {ex.Message}");
                return 1;
            }

            string digest = Sha256File(partial);
            if (!string.Equals(digest, p.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                Console.Error.WriteLine($"vx maps: {p.Name}: sha256 mismatch");
                Console.Error.WriteLine($"         expected {p.Sha256}");
                Console.Error.WriteLine($"         got      {digest}");
                Console.Error.WriteLine("         refusing to install — the lockfile and the asset disagree");
                return 1;
            }

            // Verified, so the rename IS the install. No window in which a bad pack is mountable.
            File.Move(partial, target, overwrite: true);
            installed.Add(p.Name);
        }

        if (json) EmitJson("maps", true, packs.Count, 0, installed.ToArray());
        else Console.WriteLine($"\ndone — {installed.Count} pack(s) installed under data/maps/");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------

    private sealed record Pack(string Name, long Size, string Sha256, string[] Urls);

    /// <summary>Hash the installed pack, skipping the read when the size already rules it out.</summary>
    private static string? InstalledDigest(string path, long expectSize)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != expectSize) return null;
        }
        catch (IOException) { return null; }
        return Sha256File(path);
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No automatic redirect limit fiddling and no custom handler: the DEFAULT handler is the point —
        // it uses the platform certificate store, which is the whole reason this moved off urllib.
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    /// <summary>Download to <paramref name="partial"/>, resuming, trying each URL with backoff.</summary>
    private static void Download(Pack pack, string partial, bool quiet)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < Retries; attempt++)
        {
            foreach (string url in pack.Urls)
            {
                long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (have == pack.Size) return;
                if (have > pack.Size)
                {
                    File.Delete(partial);   // longer than pinned means it is not our file
                    have = 0;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

                    using HttpResponseMessage resp =
                        Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();

                    // A server that ignores Range answers 200 and restarts the body, so appending would
                    // corrupt the file. Truncate in that case; append only on a real 206.
                    FileMode mode = (have > 0 && resp.StatusCode == HttpStatusCode.PartialContent)
                        ? FileMode.Append
                        : FileMode.Create;

                    using (Stream net = resp.Content.ReadAsStream())
                    using (var outFs = new FileStream(partial, mode, FileAccess.Write, FileShare.None, Chunk))
                        net.CopyTo(outFs, Chunk);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    last = ex;
                }
            }
            if (attempt < Retries - 1)
            {
                int delay = 1 << attempt;
                if (!quiet) Console.WriteLine($"    retrying in {delay}s ({last?.Message})");
                Thread.Sleep(delay * 1000);
            }
        }
        throw last ?? new IOException("download failed with no recorded error");
    }

    /// <summary>
    /// Remove <c>&lt;map&gt;.pk3dir</c> directories left by the earlier extract-on-fetch scheme. They must
    /// go rather than linger: MountGameDir mounts BOTH .pk3 and .pk3dir from the same directory, so an old
    /// extracted copy beside a new pack would mount the same map twice, and the .pk3dir would win on name
    /// order — a stale map quietly shadowing the pinned one.
    /// </summary>
    private static int CleanLegacyLayout(string dest)
    {
        if (!Directory.Exists(dest)) return 0;
        int removed = 0;
        foreach (string stale in Directory.GetDirectories(dest, "*.pk3dir").OrderBy(x => x, StringComparer.Ordinal))
        {
            Directory.Delete(stale, recursive: true);
            removed++;
        }
        string staging = Path.Combine(dest, ".staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        return removed;
    }

    /// <summary>
    /// <c>--rebuild</c> drives a q3map2 compile and publish.py inside the maps-src submodule — a Python
    /// build pipeline belonging to another repository. Porting that would move a lot of orchestration for
    /// no benefit, so it is delegated. The interpreter is resolved the same way every other caller does it.
    /// </summary>
    private static int Rebuild(string[] args, string root)
    {
        string? py = Env.FindPython();
        if (py is null)
        {
            Console.Error.WriteLine("vx maps --rebuild: needs Python (it drives the maps-src build pipeline).");
            Console.Error.WriteLine("                   Run './vx doctor' for how to install it.");
            return 1;
        }
        string script = Path.Combine(root, "tools", "data", "fetch-maps.py");
        var psi = new ProcessStartInfo(py) { WorkingDirectory = root, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        foreach (string a in args) psi.ArgumentList.Add(a);
        Console.WriteLine($"vx maps: delegating --rebuild to {Path.GetFileName(script)}");
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void EmitJson(string command, bool ok, int pinned, int staleCount, string[] affected)
    {
        var doc = new JsonObject
        {
            ["schema"] = JsonSchemaVersion,
            ["command"] = command,
            ["ok"] = ok,
            ["pinned"] = pinned,
            ["stale"] = staleCount,
            ["affected"] = new JsonArray(affected.Select(a => (JsonNode)a!).ToArray()),
        };
        Console.WriteLine(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
