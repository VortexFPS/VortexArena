using System;
using VortexArena.Engine.Simulation;

namespace VortexArena.Engine.Console;

/// <summary>
/// Loads the packaged DarkPlaces engine cvar help table into a <see cref="CvarService"/> — the missing third
/// source of DP's <c>cvar_t.description</c>.
///
/// <para>Xonotic's own cvars declare their help string in the shipped cfg tree, as the third argument of
/// <c>set name value "description"</c>; <see cref="VortexArena.Common.Config.ConfigInterpreter.CvarDescriptionHook"/>
/// carries those straight into the store as the tree loads. The ~1400 cvars DP declares in ENGINE C code
/// (<c>cl_maxfps</c>, <c>con_textsize</c>, <c>vid_vsync</c>, the <c>r_*</c> renderer set…) have no such line —
/// the cfgs assign them bare (<c>cl_maxfps 256</c>) — so their descriptions exist only in the DarkPlaces
/// sources. <c>tools/extract-engine-cvar-help.py</c> lifts them into
/// <c>data/core.pk3dir/engine-cvar-help.txt</c>, and this reads that file at boot.</para>
///
/// <para>Metadata only: it never creates or assigns a cvar. <see cref="CvarService.SetDescription"/> is
/// first-writer-wins, so loading this AFTER the cfg tree leaves any description the tree supplied untouched and
/// only fills the gaps. Descriptions for engine features the port never implemented simply sit unused —
/// every reader (<c>search</c>, <c>cvarlist</c>, Tab completion) walks the live cvar list and looks the
/// description up, never the reverse.</para>
/// </summary>
public static class CvarHelpTable
{
    /// <summary>The packaged table's path inside the mounted content tree.</summary>
    public const string FileName = "engine-cvar-help.txt";

    /// <summary>
    /// Parse <paramref name="text"/> (<c>name&lt;TAB&gt;description</c> per line, <c>#</c> comments and blanks
    /// ignored) into <paramref name="cvars"/>. Returns how many descriptions were accepted. Tolerant by design:
    /// a malformed line is skipped, not an error — a stale or truncated help table must never stop the game
    /// booting.
    /// </summary>
    public static int Load(CvarService cvars, string? text)
    {
        if (cvars is null || string.IsNullOrEmpty(text))
            return 0;

        int n = 0;
        foreach (ReadOnlySpan<char> raw in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            ReadOnlySpan<char> name = line[..tab].Trim();
            ReadOnlySpan<char> desc = line[(tab + 1)..].Trim();
            if (name.Length == 0 || desc.Length == 0)
                continue;
            cvars.SetDescription(name.ToString(), desc.ToString());
            n++;
        }
        return n;
    }

    /// <summary>
    /// <see cref="Load(CvarService, string?)"/> resolved through a content reader (the client passes its VFS
    /// lookup). A missing file is a silent no-op — a bare/CI run with no content tree mounted keeps working,
    /// it just has no engine help strings to search.
    /// </summary>
    public static int Load(CvarService cvars, Func<string, string?> readFile)
        => Load(cvars, readFile is null ? null : readFile(FileName));
}
