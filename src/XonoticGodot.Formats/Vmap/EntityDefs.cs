using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// What a spawn key holds, from the <c>entities.ent</c> element name. Drives how the inspector edits it: a
/// <see cref="Boolean"/> gets a checkbox, a <see cref="Target"/> gets a picker of existing targetnames, a
/// <see cref="Color"/> gets a swatch. Unknown element names fall back to <see cref="String"/> rather than
/// being dropped, so a key the file gains later is still editable as text.
/// </summary>
public enum EntityKeyKind
{
    String,
    Real,
    Integer,
    Boolean,
    Color,
    Direction,
    Angles,
    Target,
    TargetName,
    Sound,
    Model,
    Texture,
    Array,
    Real3,
    Integer3,
}

/// <summary>One editable spawn key of an entity class.</summary>
public sealed class EntityKeyDef
{
    /// <summary>The key as written in the map file (<c>origin</c>, <c>targetname</c>, …).</summary>
    public string Key { get; init; } = "";

    /// <summary>Human-readable label; falls back to <see cref="Key"/> when the file gives none.</summary>
    public string Name { get; init; } = "";

    /// <summary>The file's own help text for this key.</summary>
    public string Help { get; init; } = "";

    /// <summary>How to edit it.</summary>
    public EntityKeyKind Kind { get; init; } = EntityKeyKind.String;
}

/// <summary>One spawnflag bit.</summary>
public sealed class EntityFlagDef
{
    public string Name { get; init; } = "";

    /// <summary>
    /// Bit INDEX, as the file's <c>bit=</c> attribute gives it — 0, 1, 2, … NOT 1, 2, 4.
    ///
    /// Worth spelling out, because the two readings agree for 0 and 1 and diverge silently after that.
    /// Xonotic's own file writes <c>&lt;flag key="LINEAR" bit="0"&gt;</c>, <c>NOANGLE bit="1"</c>,
    /// <c>NOGRIDLIGHT bit="4"</c>, and <c>func_door</c> reaches <c>bit="12"</c> — an index, not 4096.
    /// </summary>
    public int Bit { get; init; }

    /// <summary>What to OR into <c>spawnflags</c> to set this flag: <c>1 &lt;&lt; Bit</c>.</summary>
    public int Value => Bit is >= 0 and < 31 ? 1 << Bit : 0;

    public string Help { get; init; } = "";
}

/// <summary>
/// One entity class: everything the editor needs to place, draw, pick and edit it.
/// </summary>
public sealed class EntityClassDef
{
    /// <summary>The <c>classname</c> value.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// True for a BRUSH entity (<c>&lt;group&gt;</c> in the file): it has no origin of its own and is placed by
    /// assigning brushes to it, so the Create palette offers it only when geometry is selected.
    /// </summary>
    public bool IsBrushEntity { get; init; }

    /// <summary>Editor colour, 0..1 per channel.</summary>
    public Vector3 Color { get; init; } = new(1f, 1f, 1f);

    /// <summary>True when the file declared a bounding box for this class.</summary>
    public bool HasBox { get; init; }

    /// <summary>Box corners relative to the entity origin, in Quake units.</summary>
    public Vector3 Mins { get; init; }

    public Vector3 Maxs { get; init; }

    /// <summary>
    /// Editor preview model from the file's <c>modeldisabled=</c> line, or empty. Named for what it is in
    /// Radiant: a model shown INSTEAD of the box, never written back as a spawn key.
    /// </summary>
    public string Model { get; init; } = "";

    /// <summary>The class's prose description, with the file's KEYS/NOTES separators stripped.</summary>
    public string Description { get; init; } = "";

    /// <summary>Editable spawn keys.</summary>
    public IReadOnlyList<EntityKeyDef> Keys { get; init; } = Array.Empty<EntityKeyDef>();

    /// <summary>Spawnflag bits.</summary>
    public IReadOnlyList<EntityFlagDef> Flags { get; init; } = Array.Empty<EntityFlagDef>();

    /// <summary>Palette grouping, derived from the classname prefix.</summary>
    public string Category { get; init; } = "misc";

    /// <summary>
    /// Half-extents to draw when the class declared no box. Radiant's own fallback is a small cube, and the
    /// alternative — drawing nothing — makes a whole family of entities invisible and unpickable.
    /// </summary>
    public static readonly Vector3 DefaultMins = new(-8f, -8f, -8f);

    public static readonly Vector3 DefaultMaxs = new(8f, 8f, 8f);

    /// <summary>The box actually used for drawing and picking.</summary>
    public Vector3 DrawMins => HasBox ? Mins : DefaultMins;

    public Vector3 DrawMaxs => HasBox ? Maxs : DefaultMaxs;
}

/// <summary>
/// The entity class registry, parsed from Xonotic's own <c>scripts/entities.ent</c> (design doc §11.9).
///
/// Using the shipped file rather than a hand-authored table is the whole point: it carries 186 classes with
/// typed keys, per-key help text, editor colours, bounding boxes and preview models, it is what NetRadiant
/// drives its entity inspector from, and it is maintained alongside the game. A hand-written list would start
/// out worse and drift from the moment a new entity landed.
///
/// Godot-free and text-in, so the host reads the file through the VFS and hands the contents here.
/// </summary>
public sealed class EntityDefs
{
    /// <summary>Virtual path the game ships this at.</summary>
    public const string VirtualPath = "scripts/entities.ent";

    private readonly Dictionary<string, EntityClassDef> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EntityClassDef> _all = new();

    /// <summary>Every class, in file order.</summary>
    public IReadOnlyList<EntityClassDef> All => _all;

    /// <summary>How many classes were parsed.</summary>
    public int Count => _all.Count;

    /// <summary>Look up by classname; null when the file does not describe it.</summary>
    public EntityClassDef? Get(string className)
        => className is not null && _byName.TryGetValue(className, out EntityClassDef? d) ? d : null;

    /// <summary>
    /// The class for <paramref name="className"/>, or a synthesized placeholder.
    ///
    /// Never returns null on purpose. A map can legitimately contain a classname the definition file does not
    /// know — a mod entity, a typo, or something newer than the file — and the editor still has to draw it,
    /// pick it and let you fix its keys. Returning a plain white box is strictly better than making the entity
    /// invisible, which is how a mapper loses track of it entirely.
    /// </summary>
    public EntityClassDef GetOrPlaceholder(string className)
        => Get(className) ?? new EntityClassDef
        {
            Name = className ?? "",
            Category = CategoryFor(className ?? ""),
            Description = "Not described in entities.ent — shown as a plain box.",
        };

    /// <summary>Categories present, in a curated order with the common ones first.</summary>
    public IReadOnlyList<string> Categories()
    {
        var seen = new List<string>();
        foreach (string c in CategoryOrder)
            if (_all.Exists(d => d.Category == c))
                seen.Add(c);
        foreach (EntityClassDef d in _all)
            if (!seen.Contains(d.Category))
                seen.Add(d.Category);
        return seen;
    }

    /// <summary>Classes in one category, alphabetically.</summary>
    public IReadOnlyList<EntityClassDef> InCategory(string category)
    {
        var list = _all.FindAll(d => string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase));
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>
    /// Palette order. Weapons, items and spawns first because they are what a mapper places constantly; the
    /// compiler-only and scripting classes sink to the bottom.
    /// </summary>
    private static readonly string[] CategoryOrder =
    {
        "weapon", "item", "info", "func", "trigger", "target", "light", "misc",
    };

    /// <summary>
    /// Group by classname prefix. The file itself has no category field, and the prefixes are a real
    /// convention in Quake-lineage entity naming rather than a guess — <c>func_</c> moves, <c>trigger_</c>
    /// fires, <c>info_</c> marks a position.
    /// </summary>
    public static string CategoryFor(string className)
    {
        if (string.IsNullOrEmpty(className))
            return "misc";
        int underscore = className.IndexOf('_');
        string prefix = underscore > 0 ? className[..underscore] : className;

        return prefix.ToLowerInvariant() switch
        {
            "weapon" => "weapon",
            "item" => "item",
            "info" => "info",
            "func" => "func",
            "trigger" => "trigger",
            "target" => "target",
            "light" or "lightjunior" => "light",
            _ => className.StartsWith("light", StringComparison.OrdinalIgnoreCase) ? "light" : "misc",
        };
    }

    /// <summary>
    /// Parse the definition file. Malformed input yields an EMPTY registry rather than throwing: a missing or
    /// broken <c>entities.ent</c> must degrade the editor to placeholder boxes, not stop a session opening.
    /// </summary>
    public static EntityDefs Parse(string xml)
    {
        var defs = new EntityDefs();
        if (string.IsNullOrWhiteSpace(xml))
            return defs;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return defs;
        }

        if (doc.Root is null)
            return defs;

        foreach (XElement node in doc.Root.Elements())
        {
            bool isGroup = node.Name.LocalName.Equals("group", StringComparison.OrdinalIgnoreCase);
            bool isPoint = node.Name.LocalName.Equals("point", StringComparison.OrdinalIgnoreCase);
            if (!isGroup && !isPoint)
                continue;

            string name = (string?)node.Attribute("name") ?? "";
            if (name.Length == 0)
                continue;

            var keys = new List<EntityKeyDef>();
            var flags = new List<EntityFlagDef>();
            foreach (XElement child in node.Elements())
            {
                string tag = child.Name.LocalName;
                string key = (string?)child.Attribute("key") ?? "";
                if (key.Length == 0)
                    continue;

                if (tag.Equals("flag", StringComparison.OrdinalIgnoreCase))
                {
                    flags.Add(new EntityFlagDef
                    {
                        Name = (string?)child.Attribute("name") ?? key,
                        Bit = ParseInt((string?)child.Attribute("bit")),
                        Help = Collapse(child.Value),
                    });
                    continue;
                }

                keys.Add(new EntityKeyDef
                {
                    Key = key,
                    Name = (string?)child.Attribute("name") ?? key,
                    Help = Collapse(child.Value),
                    Kind = KindOf(tag),
                });
            }

            bool hasBox = TryParseVec6((string?)node.Attribute("box"), out Vector3 mins, out Vector3 maxs);

            defs.Add(new EntityClassDef
            {
                Name = name,
                IsBrushEntity = isGroup,
                Color = TryParseVec3((string?)node.Attribute("color"), out Vector3 c) ? c : new Vector3(1f, 1f, 1f),
                HasBox = hasBox,
                Mins = mins,
                Maxs = maxs,
                Model = ExtractModel(node),
                Description = DescriptionOf(node),
                Keys = keys,
                Flags = flags,
                Category = CategoryFor(name),
            });
        }

        return defs;
    }

    private void Add(EntityClassDef def)
    {
        // Later definitions win, matching how the file would be read top-to-bottom by Radiant.
        if (!_byName.ContainsKey(def.Name))
            _all.Add(def);
        _byName[def.Name] = def;
    }

    private static EntityKeyKind KindOf(string tag) => tag.ToLowerInvariant() switch
    {
        "real" => EntityKeyKind.Real,
        "integer" => EntityKeyKind.Integer,
        "boolean" => EntityKeyKind.Boolean,
        "color" => EntityKeyKind.Color,
        "direction" => EntityKeyKind.Direction,
        "angles" => EntityKeyKind.Angles,
        "target" => EntityKeyKind.Target,
        "targetname" => EntityKeyKind.TargetName,
        "sound" => EntityKeyKind.Sound,
        "model" => EntityKeyKind.Model,
        "texture" => EntityKeyKind.Texture,
        "array" => EntityKeyKind.Array,
        "real3" => EntityKeyKind.Real3,
        "integer3" => EntityKeyKind.Integer3,
        _ => EntityKeyKind.String,
    };

    /// <summary>
    /// The class's own prose: the element's direct text, minus the file's <c>-------- KEYS --------</c> style
    /// separators and the <c>modeldisabled=</c> line, which is editor metadata rather than documentation.
    /// </summary>
    private static string DescriptionOf(XElement node)
    {
        var text = new System.Text.StringBuilder();
        foreach (XNode n in node.Nodes())
        {
            if (n is not XText t)
                continue;
            foreach (string raw in t.Value.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith("--------", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("modeldisabled", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (text.Length > 0)
                    text.Append(' ');
                text.Append(line);
            }
        }
        return text.ToString();
    }

    /// <summary>Pull the <c>modeldisabled="path"</c> line out of the element's text.</summary>
    private static string ExtractModel(XElement node)
    {
        foreach (XNode n in node.Nodes())
        {
            if (n is not XText t)
                continue;
            int at = t.Value.IndexOf("modeldisabled", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;

            int open = t.Value.IndexOf('"', at);
            if (open < 0)
                continue;
            int close = t.Value.IndexOf('"', open + 1);
            if (close < 0)
                continue;
            return t.Value[(open + 1)..close].Trim();
        }
        return "";
    }

    private static string Collapse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int ParseInt(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static bool TryParseVec3(string? s, out Vector3 v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        string[] parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;
        if (!TryFloat(parts[0], out float x) || !TryFloat(parts[1], out float y) || !TryFloat(parts[2], out float z))
            return false;
        v = new Vector3(x, y, z);
        return true;
    }

    private static bool TryParseVec6(string? s, out Vector3 mins, out Vector3 maxs)
    {
        mins = default;
        maxs = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        string[] parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
            return false;

        Span<float> f = stackalloc float[6];
        for (int i = 0; i < 6; i++)
            if (!TryFloat(parts[i], out f[i]))
                return false;

        mins = new Vector3(f[0], f[1], f[2]);
        maxs = new Vector3(f[3], f[4], f[5]);
        return true;
    }

    /// <summary>
    /// Parse a float the file's way. Values like <c>.3</c> appear throughout the colour attributes, which
    /// <c>float.TryParse</c> handles, but the INVARIANT culture is mandatory: on a comma-decimal locale
    /// "0.77 0.88 1.0" would otherwise parse as garbage and every entity would come out the wrong colour.
    /// </summary>
    private static bool TryFloat(string s, out float v)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
