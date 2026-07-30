namespace XonoticGodot.Formats.Sidecars;

/// <summary>
/// One animation range from a <c>.framegroups</c> sidecar file. Vertex-morph models (MD3/MDL) and
/// skeletal models (IQM/DPM) store a flat stack of poses with no named clips; the sidecar carves that
/// flat range into named animation scenes. Each line of the file is exactly one <see cref="FrameGroup"/>.
///
/// Ground truth: <c>Mod_FrameGroupify_ParseGroups</c> / <c>Mod_FrameGroupify_ParseGroups_Store</c> in
/// Darkplaces <c>model_shared.c</c>. A model named <c>foo.md3</c> with a <c>foo.md3.framegroups</c>
/// sidecar replaces its engine-default scenes with the groups parsed here.
/// </summary>
public readonly record struct FrameGroup
{
    /// <summary>Index of the first pose/frame of the range (0-based, into the model's flat pose list).</summary>
    public int FirstFrame { get; init; }

    /// <summary>Number of consecutive poses in the range. Always &gt;= 1 once parsed.</summary>
    public int FrameCount { get; init; }

    /// <summary>Playback rate in frames per second. Defaults to 20 when omitted (DP default).</summary>
    public float Fps { get; init; }

    /// <summary>Whether the clip loops. Defaults to <c>true</c> when omitted (DP default).</summary>
    public bool Loop { get; init; }

    /// <summary>
    /// Optional clip name from a 5th token, if the file supplies one (Xonotic files usually leave it off
    /// and put a human comment after the numbers instead). Empty when not present. DP would synthesize
    /// <c>groupified_{index}_anim</c>; that synthesis is left to the host so it can index by line order.
    /// </summary>
    public string Name { get; init; }

    public FrameGroup(int firstFrame, int frameCount, float fps, bool loop, string name = "")
    {
        FirstFrame = firstFrame;
        FrameCount = frameCount;
        Fps = fps;
        Loop = loop;
        Name = name ?? string.Empty;
    }
}

/// <summary>
/// Parser for the line-oriented <c>.framegroups</c> sidecar format:
/// <code>firstframe framecount [fps] [loop] [name]   // optional comment</code>
/// e.g. <c>0 50 5 1</c> or <c>1115 121 10 1   // zombie idle</c>.
/// </summary>
public static class FrameGroups
{
    /// <summary>
    /// Parses the whole sidecar text into one <see cref="FrameGroup"/> per non-empty line, preserving order.
    ///
    /// Rules ported from <c>Mod_FrameGroupify_ParseGroups</c>:
    /// <list type="bullet">
    ///   <item>Tokens are whitespace-separated. The first two (firstframe, framecount) are REQUIRED;
    ///         a line missing the framecount is skipped (DP prints a warning and continues).</item>
    ///   <item><c>fps</c> defaults to 20, <c>loop</c> defaults to true; both optional.</item>
    ///   <item>A 5th token, if present, is the clip name. Any further tokens are ignored (DP "eats" them).</item>
    ///   <item><c>//</c> begins a comment that runs to end of line (DP's <c>COM_ParseToken_Simple</c> behavior);
    ///         this is how Xonotic annotates each clip.</item>
    ///   <item><c>/* */</c> block comments ARE stripped, and a line whose first token is not a number is
    ///         skipped: the DP-generated DPM weapon sidecars (h_rl/h_gl/h_electro/…) open with a
    ///         <c>/* Generated framegroups file … */</c> prose header. The old strict-line reading parsed those
    ///         prose lines as data — two junk groups (whose 5th words, "h_rl"/"simulate", became clip NAMES)
    ///         that shifted the real fire/fire2/idle/reload slots to indexes 2..5 and defeated the nameless
    ///         4-slot weapon contract: no DPM viewmodel ever animated (playtest r11).</item>
    ///   <item>Blank lines are ignored.</item>
    /// </list>
    /// Note: DP clamps <c>firstframe</c>/<c>framecount</c> against the owning model's pose count at store time.
    /// We have no model here, so we only enforce <c>framecount &gt;= 1</c> (matching the lower bound) and leave
    /// upper-bound clamping to the Godot builder, which knows the real pose count.
    /// </summary>
    public static List<FrameGroup> Parse(string text)
    {
        var result = new List<FrameGroup>();
        if (string.IsNullOrEmpty(text))
            return result;

        text = StripBlockComments(text);

        foreach (string rawLine in SplitLines(text))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            string[] tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // REQUIRED: firstframe + framecount. A lone first number is an incomplete line -> skip.
            if (tok.Length < 2)
                continue;

            // A data row starts with a NUMBER. Prose (a stray header line outside a /* */ block) is skipped
            // instead of atoi-ing to zeros and minting a phantom group.
            char c0 = tok[0][0];
            if (c0 != '-' && (c0 < '0' || c0 > '9'))
                continue;

            int firstFrame = ParseInt(tok[0]);
            int frameCount = ParseInt(tok[1]);
            // DP bound()s framecount to at least 1.
            if (frameCount < 1)
                frameCount = 1;
            if (firstFrame < 0)
                firstFrame = 0;

            // OPTIONAL: fps (default 20), loop (default true), name.
            float fps = tok.Length >= 3 ? ParseFloat(tok[2], 20f) : 20f;
            // DP does max(1, fps).
            if (fps < 1f)
                fps = 1f;

            bool loop = tok.Length < 4 || ParseInt(tok[3]) != 0;

            string name = tok.Length >= 5 ? tok[4] : string.Empty;

            result.Add(new FrameGroup(firstFrame, frameCount, fps, loop, name));
        }

        return result;
    }

    /// <summary>Removes a trailing <c>//</c> line comment.</summary>
    private static string StripComment(string line)
    {
        int idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx < 0 ? line : line.Substring(0, idx);
    }

    /// <summary>Removes <c>/* … */</c> block comments (the DP-generated DPM sidecars' prose header). An
    /// unterminated block runs to end of text, like C.</summary>
    private static string StripBlockComments(string text)
    {
        int open = text.IndexOf("/*", StringComparison.Ordinal);
        if (open < 0)
            return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int pos = 0;
        while (open >= 0)
        {
            sb.Append(text, pos, open - pos);
            int close = text.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
                return sb.ToString(); // unterminated → drop the rest
            pos = close + 2;
            open = text.IndexOf("/*", pos, StringComparison.Ordinal);
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        // Tolerate CRLF, LF and lone CR without allocating a regex.
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static int ParseInt(string s)
    {
        // Mirror C atoi(): parse a leading integer, treat garbage as 0 rather than throwing,
        // because the upstream format is permissive and host data must not hard-fail on a stray token.
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
    }

    private static float ParseFloat(string s, float fallback)
    {
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }
}
