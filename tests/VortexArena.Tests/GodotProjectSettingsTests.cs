using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Pins the load-bearing, non-default settings in <c>project.godot</c> and <c>export_presets.cfg</c>
/// (repo-restructure plan item 26 — G9).
///
/// Both files are owned by the Godot editor, and the editor REWRITES them wholesale on save: it emits
/// only what differs from its own defaults and it strips every comment. So the settings below have two
/// distinct ways to disappear, and neither one is visible in a diff review of gameplay code:
/// <list type="bullet">
///   <item>a value silently reverts to the Godot default, because the editor did not know we meant it;</item>
///   <item>the rationale comment block explaining WHY vanishes, so the next person re-derives it — or,
///     more likely, "tidies up" the setting because nothing says it matters.</item>
/// </list>
///
/// What makes this worth a test rather than a code comment is the failure signature. These are frame
/// TIMING settings. Losing them does not crash, log, or fail any other test — the game just moves
/// slow-then-fast by ~30% under load. Both root causes took a dedicated investigation to find
/// (<c>planning/wobble-independent-audit-2026-07-26.md</c>, <c>planning/perf-campaign-2026-07-06.md</c>),
/// and neither was noticed until someone played on a high-refresh panel and said it felt wrong.
///
/// So this fails the suite instead, at the cost of a speed bump for anyone deliberately retuning them:
/// the assert messages name the doc to read first.
/// </summary>
public class GodotProjectSettingsTests
{
    private readonly ITestOutputHelper _out;
    public GodotProjectSettingsTests(ITestOutputHelper o) => _out = o;

    private static string ProjectGodot => Path.Combine(TestPaths.RepoRoot, "project.godot");
    private static string ExportPresets => Path.Combine(TestPaths.RepoRoot, "export_presets.cfg");

    /// <summary>
    /// Every setting whose value diverges from Godot's own default, with the default it would revert
    /// to and the doc that argues for the divergence. Only divergent settings belong here: a setting
    /// that already equals the default cannot regress via a regenerate, so pinning it would add
    /// friction and catch nothing.
    /// </summary>
    public static TheoryData<string, string, string, string> LoadBearingTimingSettings() => new()
    {
        // name                                        ours     godot default  why
        { "application/run/delta_smooth",              "false", "true",
          "delta smoothing quantizes _Process deltas onto a grid derived from the DETECTED refresh " +
          "rate, which this platform misreads as 60Hz on high-refresh panels — the r16 rubberband" },
        { "physics/common/physics_ticks_per_second",   "10",    "60",
          "the game runs its own 72 Hz sim; Godot physics has zero consumers, so the 60 Hz phase cost " +
          "0.15-0.39 ms/frame for nothing and its catch-up steps amplified hitches (perf campaign R30)" },
        { "physics/common/physics_jitter_fix",         "0.0",   "0.5",
          "at physics_step 0.1s the jitter-fix clamp becomes a one-sided rectifier that REWRITES the " +
          "frame delta — 23-40% of frames landed on the 100/N ms grid (wobble audit 3f)" },
        // Equal to Godot's default (8), so a regenerate cannot silently change it. Pinned anyway
        // because the value is only safe BECAUSE physics_ticks_per_second is 10: main.cpp subtracts
        // (steps - max) * physics_step from the reported delta per dropped step, which at 0.1 s/step
        // drove _Process deltas NEGATIVE after a >=200 ms hitch. The two move together or not at all.
        { "physics/common/max_physics_steps_per_frame", "8",    "8",
          "bounded together with physics_ticks_per_second — see the block comment in project.godot" },
    };

    [Theory]
    [MemberData(nameof(LoadBearingTimingSettings))]
    public void Timing_Setting_Keeps_Its_Non_Default_Value(string setting, string expected, string godotDefault, string why)
    {
        var settings = ParseGodotIni(ProjectGodot);

        Assert.True(settings.ContainsKey(setting),
            $"project.godot no longer sets '{setting}', so Godot will use its default of "
            + $"'{godotDefault}'. This is almost certainly an editor save that regenerated the file: "
            + $"the editor writes only what differs from ITS defaults and does not know we meant this. "
            + $"Why it matters: {why}. Restore it by hand — do not re-save from the editor.");

        Assert.Equal(expected, settings[setting]);
        _out.WriteLine($"{setting} = {settings[setting]} (godot default {godotDefault})");
    }

    /// <summary>
    /// The comment block is the real regenerate detector: a value can survive a re-save if the editor
    /// happens to hold the same value in its inspector, but comments NEVER survive — Godot's serializer
    /// has no way to emit them. So zero comments means the file was regenerated, which is a strictly
    /// earlier and more reliable signal than any single value check above.
    /// </summary>
    [Fact]
    public void Rationale_Comments_Survive_In_project_godot()
    {
        string[] lines = File.ReadAllLines(ProjectGodot);
        int comments = lines.Count(l => l.TrimStart().StartsWith(';'));

        // ~35 today. The threshold is deliberately far below that: this is not a documentation-volume
        // quota, it is a "did the editor eat the file" tripwire, and a regenerate scores exactly 0.
        Assert.True(comments >= 20,
            $"project.godot has only {comments} comment lines. Godot strips ALL comments when it "
            + "rewrites this file, so a low count means it was regenerated from the editor and the "
            + "rationale for the timing settings is gone. Recover the block from git history "
            + "(`git log -p -- project.godot`) rather than re-deriving it.");

        // Named because a future reader who finds `physics_ticks_per_second=10` surprising needs to land
        // on the measurements, not on a summary of them.
        foreach (string cited in new[] { "wobble-independent-audit-2026-07-26.md", "perf-campaign-2026-07-06.md" })
        {
            Assert.Contains(cited, string.Join('\n', lines), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "planning", cited)),
                $"project.godot cites planning/{cited} as the rationale for its timing settings, but "
                + "that file is gone. Either restore it or update the citation — a dangling pointer is "
                + "worse than none, because the next person will assume the reasoning was never written down.");
        }
    }

    /// <summary>
    /// <c>custom_template/release</c> points the Windows release export at the PATCHED engine template
    /// (§7.2 — the borderless-refresh-rate and input fixes). Emptied, the export silently falls back to
    /// the stock template and ships a build missing engine-level fixes that no test covers, because they
    /// live below the C# boundary entirely.
    ///
    /// The value is a machine-local absolute path, so this asserts the key is still POPULATED rather
    /// than matching a literal; the path itself is checked at export time (docs/RELEASING.md).
    /// </summary>
    [Fact]
    public void Windows_Release_Export_Still_Points_At_A_Custom_Template()
    {
        string[] lines = File.ReadAllLines(ExportPresets);
        var templates = lines
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("custom_template/release=", StringComparison.Ordinal))
            .Select(l => l["custom_template/release=".Length..].Trim().Trim('"'))
            .ToList();

        Assert.True(templates.Count > 0,
            "export_presets.cfg has no custom_template/release key at all — the file was regenerated.");

        // Only the Windows client preset uses a patched template; the other three presets legitimately
        // leave it empty, so require at least one populated rather than all of them.
        Assert.True(templates.Any(t => t.Length > 0),
            $"every custom_template/release in export_presets.cfg is empty ({templates.Count} presets). "
            + "The Windows release export would fall back to the STOCK engine template, dropping the "
            + "patches in tools/engine-patches/ — including the borderless refresh-rate under-report "
            + "that caused the r16 rubberband. Re-point it per docs/RELEASING.md.");

        _out.WriteLine($"{templates.Count(t => t.Length > 0)}/{templates.Count} presets carry a custom template");
    }

    /// <summary>
    /// Parses Godot's ini-like config into fully-qualified setting names, joining the section header to
    /// the in-section key: <c>[physics]</c> + <c>common/physics_jitter_fix</c> becomes
    /// <c>physics/common/physics_jitter_fix</c>.
    ///
    /// Qualifying matters. Godot IGNORES a key filed under the wrong section, and an ignored key looks
    /// exactly like a correctly-set one to anything that greps for the bare name — so a bare-key check
    /// would pass on a file where the setting has no effect whatsoever.
    /// </summary>
    private static Dictionary<string, string> ParseGodotIni(string path)
    {
        Assert.True(File.Exists(path), $"{path} does not exist — expected a committed file at the repo root.");

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = string.Empty;

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line[..eq].Trim();
            settings[section.Length == 0 ? key : $"{section}/{key}"] = line[(eq + 1)..].Trim();
        }

        return settings;
    }
}
