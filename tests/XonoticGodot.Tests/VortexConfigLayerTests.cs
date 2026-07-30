using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Proves the Vortex config layer (restructure D8, §11) actually takes effect.
///
/// These exist because the divergence they cover was already lost once, silently. The port shipped one
/// config difference — a physics preset — applied by hand-editing a copy of <c>physicsX.cfg</c> and
/// hand-editing <c>xonotic-server.cfg</c> to exec it. Re-pointing the content tree at a clean upstream
/// checkout reverted both. Nothing failed: the game ran stock physics while <c>ConfigLoader</c>'s own doc
/// comment and <c>planning/parity/cvar-diff-known.yaml</c> both went on describing it as running ours.
///
/// The layer makes that class of loss structurally harder — upstream does not own <c>vortex-*.cfg</c>, so
/// an upstream refresh cannot revert it. But "harder" is not "impossible", and the failure mode is still
/// a quiet one: a layer file that stops being exec'd, or a value that stops landing, looks exactly like
/// nothing. So assert the values, not the wiring.
/// </summary>
public class VortexConfigLayerTests
{
    private readonly ITestOutputHelper _out;
    public VortexConfigLayerTests(ITestOutputHelper o) => _out = o;

    private static readonly string Pk3Dir = TestPaths.CorePk3Dir;

    private static Func<string, string?> DiskReader => path =>
    {
        string full = Path.Combine(Pk3Dir, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    };

    /// <summary>
    /// The layer's files are committed to the repo, not fetched, so unlike the map-dependent tests these
    /// have no legitimate reason to skip. Assert rather than return.
    /// </summary>
    private static void RequireCoreContent()
        => Assert.True(File.Exists(Path.Combine(Pk3Dir, "xonotic-server.cfg")),
            $"core content is missing from {Pk3Dir}. Unlike compiled maps, core .cfg files are COMMITTED — "
            + "if they are absent the checkout is broken, and skipping would hide that.");

    [Fact]
    public void Layer_Recovers_The_Lost_Physics_Divergence()
    {
        RequireCoreContent();
        var cvars = new CvarService();
        var interp = ConfigLoader.LoadServerConfig(cvars, DiskReader);

        // The whole of the old physicsBryan.cfg: stock physicsX plus one port-added knob. Default is -1
        // (disabled), so a layer that silently stopped loading would leave this at -1 and simply play
        // like stock — the exact symptom nobody noticed last time.
        Assert.Equal(1f, cvars.GetFloat("sv_step_upspeed_max"));

        // PhysicsPreset.Resolve reads g_physics_<set>_* first and falls back to the global cvar, so the
        // per-set value is what makes the client-selectable "bryan" set carry its own value.
        Assert.Equal(1f, cvars.GetFloat("g_physics_bryan_step_upspeed_max"));

        _out.WriteLine($"chain: {interp.CvarsAssigned} cvars, {interp.FilesExecuted} files, "
                       + $"{interp.FilesMissing} missing");
    }

    /// <summary>
    /// The highest-value test here, because it guards a cost the layer approach genuinely has.
    ///
    /// A cvar assignment cannot append to a list, so <c>vortex-physics.cfg</c> restates
    /// <c>g_physics_clientselect_options</c> in full to add <c>warsow bryan</c>. That means if upstream
    /// ever adds a preset to its list, our restated string silently drops it — the new preset just never
    /// appears in the menu, with nothing to indicate why. So compare against upstream's own list rather
    /// than against a hardcoded expectation, and require ours to be a strict superset.
    /// </summary>
    [Fact]
    public void Restated_Physics_Preset_List_Keeps_Every_Upstream_Option()
    {
        RequireCoreContent();

        string[] upstream = PresetOptionsIn("physics.cfg");
        Assert.NotEmpty(upstream);

        var cvars = new CvarService();
        ConfigLoader.LoadServerConfig(cvars, DiskReader);
        string[] effective = cvars.GetString("g_physics_clientselect_options").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        string[] dropped = upstream.Except(effective).ToArray();
        Assert.True(dropped.Length == 0,
            $"vortex-physics.cfg restates g_physics_clientselect_options and has DROPPED upstream "
            + $"preset(s): {string.Join(", ", dropped)}. Upstream's physics.cfg now lists "
            + $"[{string.Join(" ", upstream)}]. A cvar cannot append to a list, so the restated string in "
            + "vortex-physics.cfg has to be re-synced whenever upstream's changes — otherwise the preset "
            + "exists in the shipped data but is unreachable from the menu.");

        // And the additions are actually present, or the restatement achieved nothing.
        Assert.Contains("bryan", effective);
        Assert.Contains("warsow", effective);
        _out.WriteLine($"upstream {upstream.Length} options, effective {effective.Length}: "
                       + $"+[{string.Join(" ", effective.Except(upstream))}]");
    }

    private static string[] PresetOptionsIn(string cfg)
    {
        string path = Path.Combine(Pk3Dir, cfg);
        Assert.True(File.Exists(path), $"{cfg} is missing from {Pk3Dir}");
        foreach (string line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (!t.StartsWith("set g_physics_clientselect_options", StringComparison.Ordinal))
                continue;
            int a = t.IndexOf('"'), b = t.LastIndexOf('"');
            if (a >= 0 && b > a)
                return t[(a + 1)..b].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// The video defaults moved out of C# (<c>MenuState</c> used to <c>_cvars.Set</c> them) into
    /// <c>vortex-client.cfg</c>. Nothing in the C# path sets them any more — deliberately, so there is one
    /// source of truth — which means if the layer stops loading, these silently revert to whatever
    /// <c>xonotic-client.cfg</c> ships and the "smoothest play" defaults are gone.
    /// </summary>
    [Fact]
    public void Layer_Supplies_The_Port_Video_Defaults()
    {
        RequireCoreContent();
        var cvars = new CvarService();
        // The client entry is what ships the values these override, so load it too rather than only the
        // server chain — this is the ordering the real boot uses.
        ConfigLoader.Load(cvars, DiskReader, "xonotic-client.cfg", ConfigLoader.VortexCommonEntry);

        Assert.Equal(2f, cvars.GetFloat("vid_fullscreen"));  // EXCLUSIVE fullscreen, not desktop-fullscreen
        Assert.Equal(0f, cvars.GetFloat("vid_vsync"));       // off; measured -0.5 ms/frame and better lows
    }

    /// <summary>
    /// Every file <c>vortex-common.cfg</c> execs must exist. A missing one is a no-op by design, which is
    /// what lets the layer ship incrementally — but it also means a typo in an <c>exec</c> line is
    /// invisible. Pinning <c>FilesMissing</c> to zero is what keeps that counter usable as a signal.
    /// </summary>
    [Fact]
    public void Every_Layer_File_The_Entry_Point_Execs_Exists()
    {
        RequireCoreContent();
        var interp = ConfigLoader.Load(new CvarService(), DiskReader, ConfigLoader.VortexCommonEntry);

        Assert.Equal(0, interp.FilesMissing);
        // The entry point plus the files it execs.
        Assert.True(interp.FilesExecuted >= 6,
            $"vortex-common.cfg executed only {interp.FilesExecuted} file(s) — it should pull in the layer.");

        // vortex-binds.cfg is exec'd from MenuState, not from vortex-common.cfg, so it is not counted
        // above; assert it exists so the two MenuState call sites have something to find.
        Assert.True(File.Exists(Path.Combine(Pk3Dir, "vortex-binds.cfg")),
            "vortex-binds.cfg is missing, so both MenuState bind call sites are no-ops.");
    }

    /// <summary>
    /// G15. The layer must use plain <c>set</c>, never <c>seta</c>. Per <c>ConfigLoader</c>'s archive-hook
    /// contract the shipped cfgs are the authority on which cvars are archiveable, and a <c>seta</c> here
    /// would widen the player's <c>config.cfg</c> beyond upstream's set — every player's saved config would
    /// start carrying cvars upstream never intended to persist.
    /// </summary>
    [Fact]
    public void Layer_Marks_Nothing_Archiveable()
    {
        RequireCoreContent();
        var archived = new List<string>();
        ConfigLoader.Load(new CvarService(), DiskReader, archived.Add, ConfigLoader.VortexCommonEntry);

        Assert.True(archived.Count == 0,
            $"the Vortex layer marked {archived.Count} cvar(s) archiveable via `seta`: "
            + $"{string.Join(", ", archived)}. Use plain `set` — these would be written into every "
            + "player's config.cfg, beyond the set upstream ships as archiveable. If a new archiveable "
            + "cvar is genuinely intended, say so here and allow it by name.");
    }

    /// <summary>
    /// The other half of the policy: the upstream chain stays untouched. This is what makes an upstream
    /// content refresh a file replacement rather than a merge, and it is the invariant §11.5 replaces the
    /// old <c>cvar-diff-known.yaml</c> entries with.
    /// </summary>
    [Fact]
    public void Upstream_Chain_Is_Not_Edited_To_Reach_The_Layer()
    {
        RequireCoreContent();

        string server = File.ReadAllText(Path.Combine(Pk3Dir, "xonotic-server.cfg"));
        Assert.Contains("exec physicsX.cfg", server);
        Assert.DoesNotContain("vortex-", server);

        // The file the old hand-edit mechanism created. Its absence is the point: the divergence now
        // lives in a file upstream does not own, and physicsX.cfg is exec'd exactly as shipped.
        Assert.False(File.Exists(Path.Combine(Pk3Dir, "physicsBryan.cfg")),
            "physicsBryan.cfg is back. The divergence belongs in vortex-physics.cfg — a hand-edited copy "
            + "of an upstream preset is what got lost last time.");

        // Nor should any upstream file reach into the layer; the C# boot chain is the only caller.
        foreach (string cfg in Directory.EnumerateFiles(Pk3Dir, "xonotic-*.cfg"))
            Assert.DoesNotContain("vortex-", File.ReadAllText(cfg));
    }
}
