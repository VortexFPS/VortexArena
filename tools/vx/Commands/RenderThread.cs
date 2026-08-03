namespace Vx.Commands;

/// <summary>
/// The separate-render-thread switch — <c>./vx build --no-render-thread</c> and its <c>--render-thread</c>
/// inverse.
///
/// <para><b>Why this exists.</b> <c>project.godot</c> ships
/// <c>rendering/driver/threads/thread_model=2</c> (RENDER_SEPARATE_THREAD): the render pass is pipelined
/// onto its own thread, the frame becomes <c>max(proc, draw)</c> instead of <c>proc + draw</c>, and that
/// bought +13% fps on the dev box (see the long note beside the key). Upstream still labels the mode
/// experimental, and it does not fail uniformly — resize paths, particle systems and CommandQueueMT
/// contention with background loading (godot#112452, which is directly relevant to
/// BackgroundAssetStreamer/IdleWarmer) all have open reports. So a contributor hitting one of those needs a
/// way to fall back to Godot's default WITHOUT hand-editing a tracked file and then accidentally committing
/// it.</para>
///
/// <para><b>Why override.cfg and not project.godot.</b> Three properties, all of which were checked against
/// the pinned 4.6.3 source rather than assumed:</para>
/// <list type="number">
///   <item><b>It is read early enough.</b> <c>ProjectSettings::_setup</c> loads <c>override.cfg</c>
///         (project_settings.cpp:749-750 for a PCK-carrying binary, :773 for a project directory) and that
///         runs at main.cpp:2056 — well before main.cpp:2722 reads <c>thread_model</c>.</item>
///   <item><b>It cannot leak into an export.</b> main.cpp:2056 passes <c>p_ignore_override = editor</c>, and
///         <c>--export-release</c> sets <c>editor = true</c> at parse time (main.cpp:1637-1640) — i.e.
///         BEFORE that setup call. The exporter's <c>save_custom</c> therefore serialises ProjectSettings
///         that never saw this file. A clone with the override on still produces a stock export.</item>
///   <item><b>It leaves git clean.</b> The tracked <c>project.godot</c> is untouched, so the setting cannot
///         ride along in an unrelated commit — which is the failure mode of "just edit the file".</item>
/// </list>
///
/// <para><b>Why it is written in more than one place.</b> Godot resolves <c>override.cfg</c> relative to
/// whatever is running, and this repo has two of those: <c>vx run debug</c> is the engine on the project
/// directory (<c>res://</c> = the repo root), while <c>vx run</c> is an exported binary with an embedded PCK
/// (<c>exec_path.get_base_dir()</c> = <c>dist/&lt;preset&gt;/</c>). Writing one and not the other produces
/// the worst possible outcome — a switch that works for whichever half you did not test.</para>
///
/// <para>The file is edited as a MARKED BLOCK rather than owned outright, and an override.cfg carrying
/// anything vx did not write is refused rather than rewritten: this is a file a person may reasonably have
/// their own reasons to keep, and clobbering it to toggle one key would be a poor trade.</para>
/// </summary>
internal static class RenderThread
{
    private const string Key = "driver/threads/thread_model";
    private const string Begin = "; >>> vx: managed block — ./vx build --render-thread removes it";
    private const string End = "; <<< vx";

    /// <summary>What a single override.cfg location currently says.</summary>
    internal sealed record Site(string Path, string Label, bool Exists, bool Overridden);

    /// <summary>
    /// Every place Godot would look, for the ways this repo starts the game. Only locations whose artifact
    /// exists are returned: seeding <c>dist/</c> for a preset nobody has exported would leave a stray file
    /// that outlives the reason for it.
    /// </summary>
    internal static List<Site> Sites()
    {
        var sites = new List<Site> { Read(Env.RepoRoot, "project (vx run debug)") };

        foreach ((string preset, string outRel) in Wrappers.Presets)
        {
            string artifact = Path.Combine(Env.RepoRoot, outRel.Replace('/', Path.DirectorySeparatorChar));
            // macOS exports a .app BUNDLE and the executable lives inside it, so the directory Godot derives
            // from exec_path is Contents/MacOS — not the bundle root, which is what a glob over dist/ would
            // have picked.
            string dir = Directory.Exists(artifact) && artifact.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(artifact, "Contents", "MacOS")
                : Path.GetDirectoryName(artifact)!;
            if (File.Exists(artifact) || Directory.Exists(artifact))
                sites.Add(Read(dir, $"export ({preset})"));
        }
        return sites;
    }

    private static Site Read(string dir, string label)
    {
        string path = Path.Combine(dir, "override.cfg");
        if (!File.Exists(path)) return new Site(path, label, false, false);
        string text = File.ReadAllText(path);
        return new Site(path, label, true, text.Contains(Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when the separate render thread is currently switched OFF for the PROJECT — the root site, which
    /// is the one that exists on every clone and so is the honest answer to "what is this tree set to".
    /// </summary>
    internal static bool DisabledAtRoot() => Read(Env.RepoRoot, "").Overridden;

    /// <summary>
    /// Turn the separate render thread off (<paramref name="separate"/> false) or restore the project
    /// default. Idempotent, and safe to call when nothing changes — it prints only what it actually did.
    /// </summary>
    internal static int Apply(bool separate)
    {
        int failures = 0;
        var changed = new List<string>();

        foreach (Site site in Sites())
        {
            switch (Write(site, separate))
            {
                case Result.Changed: changed.Add(site.Label); break;
                case Result.Refused: failures++; break;
            }
        }

        if (failures > 0)
            return 1;

        Console.WriteLine(separate
            ? changed.Count > 0
                ? $"→ separate render thread RESTORED to the project default ({string.Join(", ", changed)})"
                : "→ separate render thread already at the project default (thread_model=2)"
            : changed.Count > 0
                ? $"→ separate render thread OFF ({string.Join(", ", changed)}) — Godot's default thread_model=1;\n"
                  + "  the whole render pass runs inline on the main thread, so frame times are NOT comparable\n"
                  + "  to a default build. `./vx build --render-thread` puts it back."
                : "→ separate render thread already off (thread_model=1)");
        return 0;
    }

    /// <summary>
    /// Re-assert the current project state across every site. Called after an export, so a freshly written
    /// <c>dist/</c> inherits the switch instead of silently coming back with the render thread on.
    /// </summary>
    internal static void Sync()
    {
        if (!DisabledAtRoot()) return;
        foreach (Site site in Sites())
            if (!site.Overridden)
                Write(site, separate: false);
    }

    private enum Result { Unchanged, Changed, Refused }

    private static Result Write(Site site, bool separate)
    {
        string[] lines = site.Exists ? File.ReadAllLines(site.Path) : [];
        int begin = Array.FindIndex(lines, l => l.StartsWith(Begin, StringComparison.Ordinal));
        int end = begin < 0 ? -1 : Array.FindIndex(lines, begin, l => l.StartsWith(End, StringComparison.Ordinal));

        // Someone else's file. Refusing beats merging: a stray `[rendering]` header appended below their
        // unsectioned keys would silently re-home them, and this is not vx's file to gamble with.
        if (site.Exists && begin < 0 && lines.Any(l => l.Trim() is { Length: > 0 } t && !t.StartsWith(';')))
        {
            Console.Error.WriteLine($"vx: {site.Path} exists and was not written by vx — leaving it alone.");
            Console.Error.WriteLine(separate
                ? $"    Remove the '{Key}' line yourself to restore the project default."
                : "    Add these two lines at the END of it (the section header applies to everything below it):");
            if (!separate)
            {
                Console.Error.WriteLine("        [rendering]");
                Console.Error.WriteLine($"        {Key}=1");
            }
            return Result.Refused;
        }

        var kept = new List<string>(lines);
        if (begin >= 0)
            kept.RemoveRange(begin, (end < 0 ? lines.Length : end + 1) - begin);

        if (!separate)
        {
            // APPENDED, always. `[rendering]` is section-scoped to everything that follows it, so a block
            // inserted anywhere but the end would capture whatever came after. (project.godot carries the
            // same warning: writing the key under the wrong section resolved it to `rendering/rendering/…`
            // and did nothing at all, which cost a wrong conclusion once already.)
            if (kept.Count > 0 && kept[^1].Trim().Length > 0) kept.Add("");
            kept.Add(Begin);
            kept.Add("; Godot's DEFAULT render threading (RENDER_THREAD_SAFE). project.godot ships 2");
            kept.Add("; (RENDER_SEPARATE_THREAD) for the frame-time win; this backs it out for a machine");
            kept.Add("; that hits one of the upstream experimental-mode bugs. Not read by --export-release.");
            kept.Add("[rendering]");
            kept.Add($"{Key}=1");
            kept.Add(End);
        }

        bool wantFile = kept.Any(l => l.Trim().Length > 0);
        bool same = site.Exists == wantFile && (!wantFile || lines.SequenceEqual(kept));
        if (same) return Result.Unchanged;

        if (!wantFile)
        {
            File.Delete(site.Path);
            return Result.Changed;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(site.Path)!);
        File.WriteAllLines(site.Path, kept);
        return Result.Changed;
    }
}
