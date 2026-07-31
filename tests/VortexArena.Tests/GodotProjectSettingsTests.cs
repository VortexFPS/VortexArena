using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// EVERY preset is accounted for in <c>tools/engine-patches/engine.lock.json</c>: either it pins a
    /// template (and <c>custom_template/release</c> names exactly that file), or the lockfile declares it
    /// under <c>unpinned_presets</c> with a reason and the field is empty. Silently emptied, the export
    /// falls back to the stock template and ships a build missing engine-level fixes that no other test
    /// covers, because they live below the C# boundary entirely (G10 / ADR-0017).
    ///
    /// Widened 2026-07-31. This used to require only that ONE preset was populated, which was correct
    /// when windows-client was the only pinned preset but became a hole once the others were pinned: it
    /// would pass unchanged with those fields blanked back to stock.
    ///
    /// The declared-gap half is not a loophole, it is what keeps the check honest about macos-client,
    /// whose field is empty ON PURPOSE — Godot's macOS exporter unzips its custom template, and the
    /// published macOS artifact is a raw Mach-O, so pinning it aborts the export instead of using it.
    /// The point is that "unpinned" has to be written down in the lockfile with a reason before this test
    /// will accept it; a field blanked without that entry still fails.
    ///
    /// Scope split, deliberately. This test is a pure read of two committed files, so it runs on any
    /// checkout without a 300 MB template download. Whether the pinned file is actually PRESENT, hashes
    /// correctly, and is in a form Godot's exporter can open is
    /// <c>tools/verify-engine-template.py --preset-config</c>, at export time, where the file exists.
    /// </summary>
    [Fact]
    public void Every_Release_Export_Points_At_A_Pinned_Engine_Template()
    {
        var settings = ParseGodotIni(ExportPresets);
        using var lockfile = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot, "tools", "engine-patches", "engine.lock.json")));

        // preset name -> the filename the lockfile pins for it. Read from template.platforms[].presets
        // rather than by grepping the file for the filename: every published artifact appears in the
        // lockfile whether or not a preset consumes it, so a text search would happily accept a preset
        // pointed at a template that is pinned for a DIFFERENT platform, or at one deliberately consumed
        // by nothing (macos today).
        var pinned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty platform in lockfile.RootElement
                     .GetProperty("template").GetProperty("platforms").EnumerateObject())
        {
            string filename = platform.Value.GetProperty("filename").GetString()!;
            foreach (JsonElement preset in platform.Value.GetProperty("presets").EnumerateArray())
                pinned[preset.GetString()!] = filename;
        }

        var gaps = new HashSet<string>(StringComparer.Ordinal);
        if (lockfile.RootElement.TryGetProperty("unpinned_presets", out JsonElement declared))
            foreach (JsonProperty gap in declared.EnumerateObject().Where(p => !p.Name.StartsWith('$')))
            {
                Assert.True(gap.Value.TryGetProperty("reason", out JsonElement reason)
                            && !string.IsNullOrWhiteSpace(reason.GetString()),
                    $"engine.lock.json declares '{gap.Name}' under unpinned_presets with no 'reason'. An "
                    + "undocumented exemption is indistinguishable from the accident it exempts — either "
                    + "write down why the preset cannot be pinned, or pin it.");
                gaps.Add(gap.Name);
            }

        Assert.True(pinned.Count > 0,
            "engine.lock.json pins a template for no preset at all. Every release export would fall back "
            + "to the stock engine — the exact G10 failure this file exists to catch.");

        // Join `[preset.N] name=` to `[preset.N.options] custom_template/release=` on the index N. Godot
        // splits a preset across those two sections, so scanning for the bare key finds four values with
        // no way to say which preset owns each - and per-preset is the whole point here.
        var presets = settings.Keys
            .Where(k => k.EndsWith("/name", StringComparison.Ordinal) && k.StartsWith("preset.", StringComparison.Ordinal))
            .Select(k => k[..^"/name".Length])
            .Where(section => !section.EndsWith(".options", StringComparison.Ordinal))
            .ToList();

        Assert.True(presets.Count > 0,
            "export_presets.cfg has no [preset.N] sections at all — the file was regenerated or truncated.");

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string section in presets)
        {
            string name = settings[$"{section}/name"].Trim('"');
            string key = $"{section}.options/custom_template/release";
            seen.Add(name);

            Assert.True(settings.ContainsKey(key),
                $"preset '{name}' has no custom_template/release key — the editor rewrote "
                + "export_presets.cfg and dropped it. Restore it per tools/engine-patches/README.md.");

            string template = settings[key].Trim('"');

            // A preset in neither list is the one nobody would notice: verify-engine-template.py is
            // invoked per preset BY NAME from ci.sh and release.yml, so an unlisted preset is gated by no
            // step at all and every job stays green.
            Assert.True(pinned.ContainsKey(name) || gaps.Contains(name),
                $"preset '{name}' is accounted for nowhere in engine.lock.json — neither pinned under "
                + "template.platforms[…].presets nor declared under unpinned_presets. Pin it, or declare "
                + "it a gap with a reason. Do not leave it out of both: nothing else checks it.");

            if (gaps.Contains(name))
            {
                Assert.True(template.Length == 0,
                    $"preset '{name}' sets custom_template/release to '{template}' while engine.lock.json "
                    + "still lists it under unpinned_presets. Those contradict each other, so the field is "
                    + "live and gated by nothing. If the blocker is cleared, pin the preset and delete the "
                    + "unpinned_presets entry in the same change.");
                _out.WriteLine($"{name} -> (declared gap: exports from the stock template)");
                continue;
            }

            Assert.False(template.Length == 0,
                $"preset '{name}' has an EMPTY custom_template/release and is NOT declared as a gap. This "
                + "is the genuinely dangerous value: Godot does not fail on it, it falls back to the STOCK "
                + "export template and produces a complete, launchable binary carrying none of "
                + "tools/engine-patches/. Re-point it per docs/RELEASING.md, or — if it truly cannot be "
                + "pinned — add it to unpinned_presets with the reason.");

            Assert.StartsWith("tools/engine-templates/", template, StringComparison.Ordinal);

            // The filename is what tools/data/fetch-engine-template.py writes to disk, so a mismatch here
            // means a fetch followed by an export would NOT line up and the export would abort - with a
            // message about an architecture mismatch rather than a missing file, which is why this is
            // worth catching in a test rather than at the point of confusion.
            Assert.Equal($"tools/engine-templates/{pinned[name]}", template);

            _out.WriteLine($"{name} -> {template}");
        }

        // The other direction: the lockfile must not describe a preset that no longer exists. A stale
        // entry is what makes the check above pass for a build nobody produces any more.
        foreach (string described in pinned.Keys.Concat(gaps).Distinct())
            Assert.True(seen.Contains(described),
                $"engine.lock.json describes preset '{described}' but export_presets.cfg has no preset by "
                + "that name. The two have drifted — one is describing a build that does not exist.");
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
