using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Pins the rule that HUD code reads the CLIENT cvar store, never the ambient one (2026-08-03).
///
/// <para><b>The two stores.</b> <c>MenuState.Cvars</c> is the client store: the settings dialogs bind to it,
/// <c>config.cfg</c> and the packaged cfg tree load into it, and <c>--cvar</c> plus a console <c>set</c> write
/// to it. <c>Api.Cvars</c> is the AMBIENT SIM store, and a match swaps that to the server world's services. A
/// client cvar read through <c>Api.Cvars</c> in a match therefore resolves against a store it was never put
/// in — it reads 0/"" and the caller falls through to its fallback.</para>
///
/// <para><b>Why this is a test and not a comment.</b> The failure is silent in the worst possible way: the
/// fallback is usually the same as the default, so everything looks correct until a player changes the
/// setting and nothing happens. It cost three separate bugs before anyone noticed —
/// <c>showfps</c>/<c>showposition</c> (masked completely by their debug-build default-on, so a developer saw
/// a counter and assumed their cvar produced it; a release build showed nothing at all),
/// <c>showping</c> (no debug default, so it had never worked in any build), and
/// <c>notification_item_centerprinttime</c> (fell through to the identical 1.5 constant). EditorPanel had
/// already hit it and written a warning comment; the comment did not stop the next three. Nothing else in the
/// suite fails when this regresses, which is exactly the case that earns a pin.</para>
///
/// <para>The fix in each case is the same: use <c>HudPanel.GlobalF</c>/<c>GlobalStr</c> (or
/// <c>ShowToggleMode</c> for a DP <c>show*</c> toggle), which read the client store.</para>
/// </summary>
public class HudClientCvarStoreTests
{
    private readonly ITestOutputHelper _out;
    public HudClientCvarStoreTests(ITestOutputHelper o) => _out = o;

    private static string HudDir => Path.Combine(TestPaths.RepoRoot, "game", "hud");

    [Fact]
    public void HudSourcesDoNotReadCvarsFromTheAmbientStore()
    {
        Assert.True(Directory.Exists(HudDir), $"HUD source directory not found: {HudDir}");

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(HudDir, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains("Api.Cvars", StringComparison.Ordinal))
                    continue;
                // Comments are how this rule is DOCUMENTED (HudPanel.ShowToggleMode and EditorPanel both name
                // Api.Cvars to explain what not to do), so only real code counts.
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("///", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {trimmed}");
            }
        }

        foreach (string o in offenders)
            _out.WriteLine(o);

        Assert.True(offenders.Count == 0,
            "HUD code must read client cvars through HudPanel.GlobalF/GlobalStr (or ShowToggleMode for a DP "
            + "show* toggle), which read MenuState.Cvars. Api.Cvars is the ambient sim store — a match swaps it "
            + "to the server world, where a client cvar is absent, so the read silently returns 0 and the "
            + "player's setting does nothing. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The three DP <c>show*</c> toggles specifically: each must go through <see cref="object"/>-free
    /// <c>ShowToggleMode</c> rather than hand-rolling the two-name lookup, so a future panel cannot
    /// reintroduce the split by copying an older one.
    /// </summary>
    [Theory]
    [InlineData("FpsPanel.cs", "showfps")]
    [InlineData("PingPanel.cs", "showping")]
    [InlineData("PositionPanel.cs", "showposition")]
    public void ShowTogglesGoThroughTheSharedReader(string fileName, string cvar)
    {
        string path = Path.Combine(HudDir, fileName);
        Assert.True(File.Exists(path), $"expected {fileName} in {HudDir}");
        string src = File.ReadAllText(path);

        Assert.True(src.Contains($"ShowToggleMode(\"{cvar}\"", StringComparison.Ordinal),
            $"{fileName} should resolve '{cvar}' via HudPanel.ShowToggleMode(\"{cvar}\", \"cl_{cvar}\") — "
            + "that helper is what guarantees the client store is the one consulted.");
    }
}
