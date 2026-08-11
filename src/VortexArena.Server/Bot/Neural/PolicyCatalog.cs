using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Discovers game-loadable policies and attaches the trainer's run/evaluation metadata to them.
/// The trainer deliberately writes plain JSON beside every <c>.vxpw</c>, so the game can browse runs
/// without importing Python, torch, or a second database.
/// </summary>
public static class PolicyCatalog
{
    public sealed record Entry(
        string Path, string Run, string Artifact, int Stage, long StageSteps, int Update,
        double? ArrivalRate, double? MeanArrivalSeconds, string Phase, DateTime ModifiedUtc)
    {
        public string DisplayName => $"{Run} / {Artifact}";
    }

    /// <summary>Find likely run roots from a checkout, exported executable, or an explicit override.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? configured = Environment.GetEnvironmentVariable("VORTEX_POLICY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) roots.Add(Path.GetFullPath(configured));
        AddAncestors(roots, Directory.GetCurrentDirectory());
        AddAncestors(roots, AppContext.BaseDirectory);
        return roots.Where(Directory.Exists).ToArray();
    }

    private static void AddAncestors(HashSet<string> roots, string start)
    {
        DirectoryInfo? at;
        try { at = new DirectoryInfo(Path.GetFullPath(start)); }
        catch { return; }
        for (int i = 0; at is not null && i < 8; i++, at = at.Parent)
        {
            string runs = Path.Combine(at.FullName, "runs");
            if (Directory.Exists(runs)) roots.Add(runs);
        }
    }

    public static IReadOnlyList<Entry> Scan(IEnumerable<string>? roots = null)
    {
        var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots ?? DefaultRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.vxpw", SearchOption.AllDirectories); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (string file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0) continue;
                    string dir = info.DirectoryName ?? root;
                    RunMeta meta = ReadMeta(dir);
                    string artifact = Path.GetFileNameWithoutExtension(file);
                    double? rate = artifact.Contains("best", StringComparison.OrdinalIgnoreCase)
                        ? meta.BestRate : meta.LastRate ?? meta.BestRate;
                    double? time = artifact.Contains("best", StringComparison.OrdinalIgnoreCase)
                        ? meta.BestTime : meta.LastTime ?? meta.BestTime;
                    string full = info.FullName;
                    found[full] = new Entry(full, meta.Run ?? new DirectoryInfo(dir).Name, artifact,
                        meta.Stage, meta.StageSteps, meta.Update, rate, time, meta.Phase ?? "", info.LastWriteTimeUtc);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }

        return found.Values
            .OrderByDescending(e => e.ArrivalRate ?? -1d)
            .ThenBy(e => e.MeanArrivalSeconds ?? double.MaxValue)
            .ThenByDescending(e => e.ModifiedUtc)
            .ToArray();
    }

    private sealed class RunMeta
    {
        public string? Run, Phase;
        public int Stage, Update;
        public long StageSteps;
        public double? BestRate, BestTime, LastRate, LastTime;
    }

    private static RunMeta ReadMeta(string dir)
    {
        var m = new RunMeta();
        string state = Path.Combine(dir, "state.json");
        if (File.Exists(state))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(state));
            JsonElement r = doc.RootElement;
            m.Run = String(r, "run");
            m.Phase = String(r, "phase");
            m.Stage = Int(r, "stage");
            m.Update = Int(r, "update");
            m.StageSteps = Long(r, "stage_steps");
            m.BestRate = Double(r, "best_rate");
            m.BestTime = Double(r, "best_time");
            m.LastRate = Double(r, "last_rate");
        }

        // The state file stores the current/best rates, while the append-only events file is the source of
        // truth for the latest measured completion time. Read only eval-completed rows and keep the last.
        string events = Path.Combine(dir, "events.jsonl");
        if (File.Exists(events))
        {
            foreach (string line in File.ReadLines(events))
            {
                if (!line.Contains("\"eval_completed\"", StringComparison.Ordinal)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement r = doc.RootElement;
                    m.LastRate = Double(r, "arrival_rate") ?? m.LastRate;
                    m.LastTime = Double(r, "mean_arrival_seconds") ?? m.LastTime;
                }
                catch (JsonException) { }
            }
        }
        return m;
    }

    private static string? String(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;
    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.TryGetInt64(out long n) ? n : 0;
    private static double? Double(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : null;
}
