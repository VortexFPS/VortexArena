namespace XonoticGodot.Formats.Sidecars;

/// <summary>
/// A parsed <c>.skin</c> sidecar (e.g. <c>player.iqm_0.skin</c>, <c>player.iqm_1.skin</c>). Alias/skeletal
/// models (MDL/MD2/MD3/IQM/DPM) carry only one baked texture set; a <c>.skin</c> file overrides per-mesh
/// materials so the same geometry can have team/variant skins (suffix <c>_0</c>, <c>_1</c>, ... selects the
/// variant). It can also assign names to the model's tags (Quake3 syntax).
///
/// Ground truth: <c>Mod_LoadSkinFiles</c> (Darkplaces <c>model_shared.c</c>) and
/// <c>Mod_BuildAliasSkinsFromSkinFiles</c> (<c>model_alias.c</c>). Spec: <c>DP_GFX_SKINFILES</c> in
/// <c>dpextensions.qc</c>.
///
/// Two line syntaxes are accepted (mixable in one file):
/// <list type="bullet">
///   <item>DP <c>replace</c> command: <c>replace "meshname" "shadername"</c>.</item>
///   <item>Quake3 CSV: <c>meshname,shadername</c> for a mesh remap, or <c>tag_name,</c> to name the Nth tag.</item>
/// </list>
/// </summary>
public sealed class SkinFile
{
    /// <summary>
    /// Mesh/surface name -&gt; replacement shader (material) path. Built from both <c>replace</c> lines and
    /// Quake3 <c>mesh,shader</c> lines. Last write wins for a duplicated mesh name. The shader value is kept
    /// verbatim (image extension NOT stripped); DP strips the extension when it resolves the actual texture,
    /// so the Godot builder should do the same. The special value <c>common/nodraw</c> (or
    /// <c>textures/common/nodraw</c>) means "render this mesh invisible".
    /// </summary>
    public Dictionary<string, string> MeshToTexture { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tag aliases from Quake3 <c>tag_name,[alias]</c> lines, in file order. The key is the <c>tag_*</c> token
    /// (word[0]); the value is the optional alias after the comma (word[2]), which is usually empty in practice
    /// (the meaningful name is the <c>tag_*</c> token itself, applied to the Nth model tag by line order).
    /// DP itself ignores these, but they are preserved here so a host can name attachment sockets.
    /// </summary>
    public Dictionary<string, string> TagAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tag names in the exact order they appeared, so a host can map them positionally onto the model's tags
    /// (line N names tag N), exactly as the Quake3 <c>.skin</c> convention specifies.
    /// </summary>
    public List<string> TagOrder { get; } = new();

    /// <summary>
    /// Lines that could not be classified (not a <c>replace</c>, mesh, or tag spec). Kept for diagnostics
    /// instead of throwing, mirroring DP which prints a warning and continues. Empty on a clean file.
    /// </summary>
    public List<string> Unrecognized { get; } = new();

    /// <summary>
    /// True if a mesh's replacement is one of DP's "make invisible" sentinels. The Godot builder should skip
    /// rendering meshes whose resolved replacement satisfies this.
    /// </summary>
    public static bool IsNoDraw(string replacement) =>
        string.Equals(replacement, "common/nodraw", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(replacement, "textures/common/nodraw", StringComparison.OrdinalIgnoreCase) ||
        // The bare form: the invisible-hand weapon rigs' skeleton plane carries the raw mesh material name
        // "nodraw" (h_shotgun/h_uzi/… 'Plane'). DP hides it by name; unhidden it rendered as an untextured
        // black quad that swung into view on the landing dip (playtest r11's "black triangle").
        string.Equals(replacement, "nodraw", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses one <c>.skin</c> file's text.
    ///
    /// Tokenization matches DP's <c>COM_ParseToken_QuakeC</c> in the ways that matter here: the comma is its
    /// own token, whitespace separates tokens, and <c>//</c> / <c>/* */</c> are comments. So
    /// <c>mesh,tex</c> tokenizes to three words <c>["mesh", ",", "tex"]</c>, which is why a mesh line is
    /// recognized by <c>word[1] == ","</c>.
    ///
    /// Line classification (in DP's precedence order):
    /// <list type="number">
    ///   <item><c>word[0] == "replace"</c> and exactly 3 words -&gt; mesh remap word[1] -&gt; word[2].</item>
    ///   <item><c>word[0]</c> starts with <c>tag_</c> and &gt;=2 words -&gt; tag name/alias.</item>
    ///   <item><c>word[1] == ","</c> and &gt;=2 words -&gt; mesh remap word[0] -&gt; word[2] (word[2] may be empty).</item>
    ///   <item>otherwise -&gt; recorded in <see cref="Unrecognized"/>.</item>
    /// </list>
    /// A blank line, or a "mesh," line with no shader after the comma, is effectively a skip for the material
    /// map (we still record the empty mapping so the host can decide; an empty replacement means "leave default").
    /// Lines with more than 10 words are dropped (DP's word[10] overflow guard).
    /// </summary>
    public static SkinFile Parse(string text)
    {
        var skin = new SkinFile();
        if (string.IsNullOrEmpty(text))
            return skin;

        foreach (string rawLine in SplitLines(text))
        {
            string line = StripComments(rawLine).Trim();
            if (line.Length == 0)
                continue;

            // Tokenize with the comma promoted to its own token, like COM_ParseToken_QuakeC.
            List<string> words = Tokenize(line);
            if (words.Count == 0)
                continue;

            // DP's overflow guard: lines with more than 10 statements are skipped.
            if (words.Count > 10)
            {
                skin.Unrecognized.Add(rawLine.Trim());
                continue;
            }

            // 1) explicit "replace meshname shadername"
            if (words[0] == "replace")
            {
                if (words.Count == 3)
                    skin.MeshToTexture[words[1]] = words[2];
                else
                    skin.Unrecognized.Add(rawLine.Trim());
                continue;
            }

            // 2) Quake3 tag naming: "tag_xxx," (>=2 words). Alias is word[2] if present (often absent/empty).
            if (words.Count >= 2 && words[0].StartsWith("tag_", StringComparison.Ordinal))
            {
                string alias = words.Count >= 3 ? words[2] : string.Empty;
                skin.TagAliases[words[0]] = alias;
                skin.TagOrder.Add(words[0]);
                continue;
            }

            // 3) Quake3 mesh remap: "meshname , shadername" -> word[1] is the comma.
            if (words.Count >= 2 && words[1] == ",")
            {
                string mesh = words[0];
                string shader = words.Count >= 3 ? words[2] : string.Empty;
                skin.MeshToTexture[mesh] = shader;
                continue;
            }

            skin.Unrecognized.Add(rawLine.Trim());
        }

        return skin;
    }

    /// <summary>
    /// Splits a line into tokens, treating <c>,</c> as a standalone token and everything else as
    /// whitespace-delimited words. Quoted strings ("...") are supported because DP's QuakeC tokenizer honors
    /// them (the <c>replace "a" "b"</c> form uses quotes); the surrounding quotes are stripped.
    /// </summary>
    private static List<string> Tokenize(string line)
    {
        var words = new List<string>();
        int i = 0;
        int n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == ',')
            {
                words.Add(",");
                i++;
                continue;
            }
            if (c == '"')
            {
                // Quoted token: read until the closing quote (or end of line).
                int start = ++i;
                while (i < n && line[i] != '"')
                    i++;
                words.Add(line.Substring(start, i - start));
                if (i < n) i++; // skip closing quote
                continue;
            }
            // Bareword: read until whitespace or a comma (the comma is a separate token).
            int ws = i;
            while (i < n && !char.IsWhiteSpace(line[i]) && line[i] != ',')
                i++;
            words.Add(line.Substring(ws, i - ws));
        }
        return words;
    }

    /// <summary>Removes <c>//</c> line comments and inline <c>/* ... */</c> block comments on a single line.</summary>
    private static string StripComments(string line)
    {
        // Block comments first (QuakeC supports /* */); only single-line spans are handled, which is all the
        // .skin format uses in practice.
        int open;
        while ((open = line.IndexOf("/*", StringComparison.Ordinal)) >= 0)
        {
            int close = line.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                line = line.Substring(0, open);
                break;
            }
            line = line.Remove(open, close + 2 - open);
        }
        int slash = line.IndexOf("//", StringComparison.Ordinal);
        if (slash >= 0)
            line = line.Substring(0, slash);
        return line;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
