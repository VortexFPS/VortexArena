using System.Globalization;
using System.Numerics;
using System.Text;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// One gametype a map declares, with the per-mode overrides written on the same line
/// (<c>gametype rc timelimit=10 qualifying_timelimit=5</c>).
/// </summary>
public sealed class MapInfoGametype
{
    /// <summary>Short gametype code as the game knows it (<c>dm</c>, <c>ctf</c>, <c>duel</c>, …).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// <c>key=value</c> overrides applied when this map runs this mode. Ordered, because the file is a
    /// human-edited text file and reordering a mapper's line for no reason is a pointless diff.
    /// </summary>
    public List<KeyValuePair<string, string>> Settings { get; } = new();

    public override string ToString()
        => Settings.Count == 0
            ? Name
            : $"{Name} {string.Join(' ', Settings.Select(kv => $"{kv.Key}={kv.Value}"))}";
}

/// <summary>
/// A map's <c>.mapinfo</c> file — the metadata the map vote, the gametype picker and the server browser read
/// (design doc §11.9's MapInfo dialog).
///
/// The port had NO reader for this at all before E8: every mapinfo mention in the codebase was a comment
/// explaining what the QC would have done. This is the format itself, parsed and written.
///
/// Round-tripping is the design constraint. These are hand-edited files that live in the map's source tree, so
/// reading one and writing it back must not reformat a mapper's work, drop a directive this parser does not
/// model, or reorder their gametype list. Unrecognised lines are therefore PRESERVED verbatim and written back
/// in place — the alternative is an editor that silently deletes whatever it did not expect.
/// </summary>
public sealed class MapInfo
{
    /// <summary>Display title.</summary>
    public string Title { get; set; } = "";

    /// <summary>One-line description shown in the map vote.</summary>
    public string Description { get; set; } = "";

    /// <summary>Credited author(s), free text.</summary>
    public string Author { get; set; } = "";

    /// <summary>Music track number, or empty when the map declares none.</summary>
    public string CdTrack { get; set; } = "";

    /// <summary>
    /// Feature declarations (<c>has weapons</c> → "weapons"). Drives whether the map is offered for modes
    /// that need pickups.
    /// </summary>
    public List<string> Has { get; } = new();

    /// <summary>Supported gametypes, in file order.</summary>
    public List<MapInfoGametype> Gametypes { get; } = new();

    /// <summary>
    /// <c>settemp_for_type &lt;type&gt; &lt;cvar&gt; &lt;value&gt;</c> lines, kept as written. Modelled as raw
    /// text rather than parsed: they are arbitrary cvar assignments and the editor has no business
    /// second-guessing them.
    /// </summary>
    public List<string> SetTemp { get; } = new();

    /// <summary>
    /// True when the map declares explicit bounds. NOT a player-count range, which is what the directive name
    /// suggests and what a first reading of it assumes: <c>size</c> is six floats giving the bounds of the
    /// lightgrid brush, and the shipped files say what it is for in their own trailing comments — "if not set
    /// here the minimap won't be scaled properly".
    /// </summary>
    public bool HasBounds { get; set; }

    /// <summary>Lower corner of the declared map bounds, in Quake units.</summary>
    public Vector3 BoundsMin { get; set; }

    /// <summary>Upper corner of the declared map bounds.</summary>
    public Vector3 BoundsMax { get; set; }

    /// <summary>Trailing <c>//</c> comment on the size line, kept so a round trip preserves the explanation.</summary>
    public string BoundsComment { get; set; } = "";

    /// <summary>True when the map declares <c>hidden</c> — excluded from the map list.</summary>
    public bool Hidden { get; set; }

    /// <summary>True when the map declares <c>forbidden</c> — never selectable.</summary>
    public bool Forbidden { get; set; }

    /// <summary>True when the map declares <c>noautomaplist</c>.</summary>
    public bool NoAutoMapList { get; set; }

    /// <summary>
    /// Lines this parser does not model, kept verbatim so a round trip does not delete them. Comments are in
    /// here too, which is why they survive an edit.
    /// </summary>
    public List<string> Unrecognised { get; } = new();

    /// <summary>True when the map declares no gametype at all, which makes it unselectable.</summary>
    public bool IsPlayable => Gametypes.Count > 0;

    /// <summary>Does this map declare <paramref name="gametype"/>?</summary>
    public bool Supports(string gametype)
        => Gametypes.Exists(g => string.Equals(g.Name, gametype, StringComparison.OrdinalIgnoreCase));

    /// <summary>Add a gametype if it is not already declared. Returns true when it was added.</summary>
    public bool AddGametype(string gametype)
    {
        if (string.IsNullOrWhiteSpace(gametype) || Supports(gametype))
            return false;
        Gametypes.Add(new MapInfoGametype { Name = gametype.Trim() });
        return true;
    }

    /// <summary>Remove a gametype. Returns true when one was removed.</summary>
    public bool RemoveGametype(string gametype)
    {
        int at = Gametypes.FindIndex(g => string.Equals(g.Name, gametype, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return false;
        Gametypes.RemoveAt(at);
        return true;
    }

    // =====================================================================================
    //  Parse
    // =====================================================================================

    /// <summary>
    /// Parse a <c>.mapinfo</c>. Never throws: a malformed file yields whatever could be read, because a map
    /// with a broken metadata line should still be editable — that is precisely when you need the editor.
    /// </summary>
    public static MapInfo Parse(string text)
    {
        var info = new MapInfo();
        if (string.IsNullOrEmpty(text))
            return info;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            // Comments are kept: they are usually a mapper explaining a settemp, and deleting them on save
            // would be a hostile edit.
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                info.Unrecognised.Add(line);
                continue;
            }

            (string verb, string rest) = SplitFirst(line);
            switch (verb.ToLowerInvariant())
            {
                case "title":
                    info.Title = rest;
                    break;
                case "description":
                    info.Description = rest;
                    break;
                case "author":
                    info.Author = rest;
                    break;
                case "cdtrack":
                    info.CdTrack = rest;
                    break;

                case "has":
                    if (rest.Length > 0)
                        info.Has.Add(rest);
                    break;

                case "gametype":
                    info.Gametypes.Add(ParseGametype(rest));
                    break;

                case "settemp_for_type":
                    info.SetTemp.Add(rest);
                    break;

                case "size":
                {
                    // Six floats, optionally followed by a // comment explaining why the mapper set them.
                    string body = rest;
                    string comment = "";
                    int slash = body.IndexOf("//", StringComparison.Ordinal);
                    if (slash >= 0)
                    {
                        comment = body[slash..].Trim();
                        body = body[..slash];
                    }

                    string[] parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    Span<float> f = stackalloc float[6];
                    bool ok = parts.Length >= 6;
                    for (int i = 0; ok && i < 6; i++)
                        ok = float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]);

                    if (ok)
                    {
                        info.HasBounds = true;
                        info.BoundsMin = new Vector3(f[0], f[1], f[2]);
                        info.BoundsMax = new Vector3(f[3], f[4], f[5]);
                        info.BoundsComment = comment;
                    }
                    else
                    {
                        info.Unrecognised.Add(line);
                    }
                    break;
                }

                case "hidden":
                    info.Hidden = true;
                    break;
                case "forbidden":
                    info.Forbidden = true;
                    break;
                case "noautomaplist":
                    info.NoAutoMapList = true;
                    break;

                default:
                    info.Unrecognised.Add(line);
                    break;
            }
        }

        return info;
    }

    private static MapInfoGametype ParseGametype(string rest)
    {
        var g = new MapInfoGametype();
        string[] parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return g;

        g.Name = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0)
                continue;   // a bare token here is not a setting; the format has no meaning for it
            g.Settings.Add(new KeyValuePair<string, string>(parts[i][..eq], parts[i][(eq + 1)..]));
        }
        return g;
    }

    private static (string Verb, string Tail) SplitFirst(string line)
    {
        int space = line.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
    }

    // =====================================================================================
    //  Write
    // =====================================================================================

    /// <summary>
    /// Serialize back to <c>.mapinfo</c> text, in the order the shipped files use: identity, then features,
    /// then gametypes, then the settemp block. Directives the map did not declare are omitted rather than
    /// written empty, so the editor does not add noise a mapper then has to delete.
    /// </summary>
    public string Write()
    {
        var sb = new StringBuilder();

        void Line(string verb, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                sb.Append(verb).Append(' ').Append(value.Trim()).Append('\n');
        }

        Line("title", Title);
        Line("description", Description);
        Line("author", Author);
        Line("cdtrack", CdTrack);

        if (HasBounds)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"size {BoundsMin.X:0.###} {BoundsMin.Y:0.###} {BoundsMin.Z:0.###} "
                + $"{BoundsMax.X:0.###} {BoundsMax.Y:0.###} {BoundsMax.Z:0.###}"));
            if (BoundsComment.Length > 0)
                sb.Append(' ').Append(BoundsComment);
            sb.Append('\n');
        }

        if (Hidden)
            sb.Append("hidden\n");
        if (Forbidden)
            sb.Append("forbidden\n");
        if (NoAutoMapList)
            sb.Append("noautomaplist\n");

        foreach (string h in Has)
            Line("has", h);

        foreach (MapInfoGametype g in Gametypes)
            if (g.Name.Length > 0)
                sb.Append("gametype ").Append(g).Append('\n');

        foreach (string t in SetTemp)
            Line("settemp_for_type", t);

        // Anything this parser did not model goes back at the end rather than being dropped. Last, because its
        // original position is not recoverable — but present, which is the part that matters.
        foreach (string u in Unrecognised)
            sb.Append(u).Append('\n');

        return sb.ToString();
    }
}
