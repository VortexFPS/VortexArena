using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VortexArena.Server.Bot.Neural;
using Xunit;

namespace VortexArena.Tests;

public sealed class PolicyCatalogTests
{
    [Fact]
    public void Scan_RanksPoliciesAndReadsLatestEvaluation()
    {
        string root = Path.Combine(Path.GetTempPath(), "va-policy-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteRun(root, "slow", 0.4, 8.0);
            WriteRun(root, "fast", 0.7, 5.0);
            var entries = PolicyCatalog.Scan(new[] { root });
            Assert.Equal(2, entries.Count);
            Assert.Equal("fast", entries[0].Run);
            Assert.Equal(0.7, entries[0].ArrivalRate);
            Assert.Equal(5.0, entries[0].MeanArrivalSeconds);
            Assert.Equal(3, entries[0].Stage);
            Assert.Equal(1234, entries[0].StageSteps);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteRun(string root, string name, double rate, double seconds)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "policy.vxpw"), new byte[] { 0x56, 0x58, 0x50, 0x57 });
        File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(new
        {
            run = name, phase = "training", stage = 3, stage_steps = 1234, update = 9,
            best_rate = rate - 0.1, last_rate = rate - 0.05,
        }));
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), JsonSerializer.Serialize(new
        {
            kind = "eval_completed", arrival_rate = rate, mean_arrival_seconds = seconds,
        }) + "\n");
    }
}
