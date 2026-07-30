namespace XonoticGodot.Formats.Sidecars;

/// <summary>
/// One entry of a model <c>.sounds</c> sidecar (e.g. <c>soldier.iqm_0.sounds</c>): a named voice/effect
/// line for a player or monster model. Lines look like:
/// <code>death sound/player/soldier/player/death 3</code>
/// where the trailing integer is the number of numbered variant files: <c>0</c> means a single file at the
/// exact path (no number suffix), and <c>N &gt; 0</c> means the engine picks randomly among
/// <c>{path}1</c>..<c>{path}N</c> (extension added by the audio loader). The leading <c>//TAG: name</c> line
/// and any <c>//</c>-commented lines (disabled aliases) are skipped.
/// </summary>
public readonly record struct ModelSound(string Id, string Path, int VariantCount)
{
    /// <summary>True when there is no numbered variant (single file at <see cref="Path"/>).</summary>
    public bool IsSingle => VariantCount <= 0;
}

/// <summary>
/// Parser for the player/monster voice-line alias table stored next to a model as <c>.sounds</c>.
/// This is an Xonotic data convention consumed by QuakeC (not the DP engine binary), so the rules here
/// follow the file format rather than a single C function: one alias per line,
/// <c>id soundpath [variantcount]</c>, with <c>//</c> comments and a <c>//TAG:</c> banner.
/// </summary>
public static class ModelSounds
{
    /// <summary>
    /// Parses the sidecar into a simple <c>id -&gt; soundpath</c> map (the shape the task asks for).
    /// Duplicate ids: last one wins. Commented-out lines (<c>//...</c>) and the <c>//TAG:</c> banner are
    /// ignored. The variant count, if present, is dropped here; use <see cref="ParseEntries"/> to keep it.
    /// </summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ModelSound s in ParseEntries(text))
            map[s.Id] = s.Path;
        return map;
    }

    /// <summary>
    /// Richer parse that preserves the variant count per entry, in file order. Use this when the audio
    /// builder needs to expand <c>{path}1..{path}N</c> for random voice variants.
    /// </summary>
    public static List<ModelSound> ParseEntries(string text)
    {
        var result = new List<ModelSound>();
        if (string.IsNullOrEmpty(text))
            return result;

        foreach (string rawLine in SplitLines(text))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            // A line starting with "//" is a comment / disabled alias / the //TAG: banner -> skip.
            // (Unlike .framegroups, comments here are whole-line; trailing "//" mid-line is not used.)
            if (line.StartsWith("//", StringComparison.Ordinal))
                continue;

            string[] tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Need at least an id and a path.
            if (tok.Length < 2)
                continue;

            string id = tok[0];
            string path = tok[1];
            int variants = 0;
            if (tok.Length >= 3)
                int.TryParse(tok[2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out variants);
            if (variants < 0)
                variants = 0;

            result.Add(new ModelSound(id, path, variants));
        }

        return result;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
