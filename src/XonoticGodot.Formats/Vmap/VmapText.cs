using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// The <c>.vmap</c> file: one UTF-8 text file per map, read and written here.
///
/// <para><b>Why one file.</b> A mapper's source is one <c>.map</c>; the compiled output is one <c>.bsp</c>.
/// This is both of those at once, so it is one <c>.vmap</c>. The earlier design was a container — a directory
/// while editing, a zip when shipping — which duplicated a job the game already does: a <c>.pk3dir</c> IS the
/// loose-files layout and a <c>.pk3</c> IS the zipped one, so a map ended up at
/// <c>stormkeep.pk3dir/maps/stormkeep.vmap/geometry.json</c>: two containers, one purpose. The pk3 is the
/// container now, and this is a file inside it, filling the slot the <c>.bsp</c> fills today.</para>
///
/// <para><b>Why text, and why not JSON.</b> Measured on the shipped maps, the JSON form was 9-10x larger than
/// the same data written compactly — 22 MB for stormkeep, 476 MB for catharsis — and the overhead was
/// SCAFFOLDING, not precision: every float sat on its own indented line, 454 bytes per face for about 70
/// bytes of digits. 476 MB is also past what GitHub will accept in a single file, so the format's own stated
/// goal (deterministic bytes "so .vmaps merge in git") was unreachable for the section that dominated it.
/// Compact text is 2.36 MB for stormkeep with the SAME round-trip floats, and it measured smaller than packed
/// binary — most values are short (<c>0</c>, <c>-1</c>, small ints) while binary spends four bytes on each
/// regardless. Binary would have cost readability and bought nothing.</para>
///
/// <para><b>The grammar.</b> Line-oriented and prefix-coded. Blank lines and <c>//</c> comments are ignored.
/// A record that carries sub-records is followed by them, exactly as a <c>.map</c> brush is followed by its
/// faces:</para>
/// <code>
///   // vmap 3                            magic and version; must be the first non-blank line
///   map "key" "value"                    manifest entry
///   mat &lt;index&gt; "material"               material table, referenced by index from faces and patches
///   grp &lt;id&gt; &lt;hidden&gt; "name"             group
///   b   &lt;id&gt; &lt;detail&gt; &lt;contents&gt; &lt;submodel&gt; &lt;tool&gt; &lt;group&gt;
///   f   &lt;nx ny nz d&gt; &lt;mat&gt; &lt;surf&gt; &lt;cont&gt; &lt;blend&gt; &lt;ux uy uz  vx vy vz  ou ov&gt;      face of the last b
///   l   &lt;mat&gt; &lt;mode&gt; &lt;chan&gt; &lt;ux uy uz  vx vy vz  ou ov&gt;                          layer above the last f
///   p   &lt;id&gt; &lt;mat&gt; &lt;w&gt; &lt;h&gt; &lt;surf&gt; &lt;cont&gt; &lt;group&gt;
///   c   &lt;x y z&gt; &lt;u v&gt;                    control point of the last p; w*h of them follow
///   e   &lt;id&gt; &lt;group&gt; "classname"
///   k   "key" "value"                    key of the last e
///   eb  &lt;id&gt;...                          brushes the last e owns
///   ep  &lt;id&gt;...                          patches the last e owns
///   x   &lt;id&gt; &lt;w&gt; &lt;h&gt; &lt;unitsPerTexel&gt; &lt;ux uy uz  vx vy vz  ou ov&gt;                  blend map
///   d   &lt;base64&gt;                         deflated texels of the last x, split across lines
/// </code>
///
/// <para><b>Determinism.</b> Invariant culture, round-trip floats, fixed field order, entity keys sorted
/// ordinal, and the material table in first-encounter order — appending rather than re-sorting, so adding a
/// material does not renumber every face that referenced the ones before it. Two saves of unchanged data are
/// byte-identical.</para>
/// </summary>
public static class VmapText
{
    /// <summary>Version this writer emits and the highest this reader accepts.</summary>
    public const int Version = 3;

    /// <summary>The first non-blank line of every <c>.vmap</c>, and how a file is recognised as one.</summary>
    public const string Magic = "// vmap";

    /// <summary>Base64 characters per <c>d</c> line, so a painted face diffs line-by-line rather than as one blob.</summary>
    private const int Base64LineLength = 120;

    /// <summary>Refuse a texel blob that claims more than this once inflated.</summary>
    private const int MaxBlendBytes = 64 * 1024 * 1024;

    /// <summary>True when these bytes look like a <c>.vmap</c> text file rather than a zip or a directory.</summary>
    public static bool LooksLikeVmapText(ReadOnlySpan<byte> bytes)
    {
        // Skip a UTF-8 BOM: not written here, but a mapper's editor may have added one.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        if (bytes.Length < Magic.Length)
            return false;
        for (int i = 0; i < Magic.Length; i++)
            if (bytes[i] != (byte)Magic[i])
                return false;
        return true;
    }

    // =============================================================================================
    //  Writing
    // =============================================================================================

    /// <summary>Serialize a document to the text form.</summary>
    public static string Write(VmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var sb = new StringBuilder(1 << 20);
        sb.Append(Magic).Append(' ').Append(Version).Append('\n');
        if (doc.Manifest.Name.Length > 0)
            sb.Append("// ").Append(doc.Manifest.Name).Append('\n');
        sb.Append('\n');

        WriteManifest(sb, doc);

        // The table is built before anything references it, in the order the document mentions each material.
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (VmapBrush b in doc.Brushes)
            foreach (VmapFace f in b.Faces)
                foreach (VmapFaceLayer layer in f.Layers)
                    Intern(materials, layer.Material);
        foreach (VmapPatch p in doc.Patches)
            Intern(materials, p.Material);

        if (materials.Count > 0)
        {
            sb.Append('\n');
            foreach (KeyValuePair<string, int> kv in materials.OrderBy(kv => kv.Value))
                sb.Append("mat ").Append(kv.Value).Append(' ').Append(Quote(kv.Key)).Append('\n');
        }

        if (doc.Groups.Count > 0)
        {
            sb.Append('\n');
            foreach (VmapGroup g in doc.Groups)
                sb.Append("grp ").Append(g.Id).Append(' ').Append(g.Hidden ? 1 : 0)
                    .Append(' ').Append(Quote(g.Name)).Append('\n');
        }

        WriteBrushes(sb, doc, materials);
        WritePatches(sb, doc, materials);
        WriteEntities(sb, doc);
        WriteBlendMaps(sb, doc);

        return sb.ToString();
    }

    private static void Intern(Dictionary<string, int> materials, string name)
    {
        if (!materials.ContainsKey(name))
            materials[name] = materials.Count;
    }

    private static void WriteManifest(StringBuilder sb, VmapDocument doc)
    {
        void Key(string key, string value)
        {
            if (value.Length > 0)
                sb.Append("map ").Append(Quote(key)).Append(' ').Append(Quote(value)).Append('\n');
        }

        Key("name", doc.Manifest.Name);
        Key("title", doc.Manifest.Title);
        Key("sourceKind", doc.Manifest.SourceKind);
        Key("sourcePath", doc.Manifest.SourcePath);
        Key("sourceHash", doc.Manifest.SourceHash);
    }

    private static void WriteBrushes(StringBuilder sb, VmapDocument doc, Dictionary<string, int> materials)
    {
        foreach (VmapBrush b in doc.Brushes)
        {
            sb.Append("\nb ").Append(b.Id).Append(' ').Append(b.IsDetail ? 1 : 0)
                .Append(' ').Append(b.ContentFlags).Append(' ').Append(b.SubmodelIndex)
                .Append(' ').Append(b.IsToolBrush ? 1 : 0).Append(' ').Append(b.GroupId).Append('\n');

            foreach (VmapFace f in b.Faces)
            {
                VmapFaceLayer baseLayer = f.Layers[0];
                sb.Append("f ");
                Vec3(sb, f.Plane.Normal);
                sb.Append(' ').Append(F(f.Plane.Dist))
                    .Append(' ').Append(materials[baseLayer.Material])
                    .Append(' ').Append(f.SurfaceFlags).Append(' ').Append(f.ContentFlags)
                    .Append(' ').Append(f.BlendMapId).Append(' ');
                Projection(sb, baseLayer.Projection);
                sb.Append('\n');

                for (int i = 1; i < f.Layers.Count; i++)
                {
                    VmapFaceLayer l = f.Layers[i];
                    sb.Append("l ").Append(materials[l.Material]).Append(' ').Append((int)l.Blend)
                        .Append(' ').Append(l.WeightChannel).Append(' ');
                    Projection(sb, l.Projection);
                    sb.Append('\n');
                }
            }
        }
    }

    private static void WritePatches(StringBuilder sb, VmapDocument doc, Dictionary<string, int> materials)
    {
        foreach (VmapPatch p in doc.Patches)
        {
            sb.Append("\np ").Append(p.Id).Append(' ').Append(materials[p.Material])
                .Append(' ').Append(p.Width).Append(' ').Append(p.Height)
                .Append(' ').Append(p.SurfaceFlags).Append(' ').Append(p.ContentFlags)
                .Append(' ').Append(p.GroupId).Append('\n');

            for (int i = 0; i < p.Controls.Count; i++)
            {
                sb.Append("c ");
                Vec3(sb, p.Controls[i]);
                Vector2 uv = i < p.ControlUvs.Count ? p.ControlUvs[i] : Vector2.Zero;
                sb.Append(' ').Append(F(uv.X)).Append(' ').Append(F(uv.Y)).Append('\n');
            }
        }
    }

    private static void WriteEntities(StringBuilder sb, VmapDocument doc)
    {
        foreach (VmapEntity e in doc.Entities)
        {
            sb.Append("\ne ").Append(e.Id).Append(' ').Append(e.GroupId)
                .Append(' ').Append(Quote(e.ClassName)).Append('\n');

            // Sorted, so unchanged data round-trips byte-identically whatever order the dictionary iterates.
            // classname is on the header and is not repeated here.
            foreach (string key in e.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (key.Equals("classname", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append("k ").Append(Quote(key)).Append(' ').Append(Quote(e.Fields[key])).Append('\n');
            }

            if (e.BrushIds.Count > 0)
                Ids(sb, "eb", e.BrushIds);
            if (e.PatchIds.Count > 0)
                Ids(sb, "ep", e.PatchIds);
        }
    }

    private static void WriteBlendMaps(StringBuilder sb, VmapDocument doc)
    {
        foreach (VmapBlendMap m in doc.BlendMaps)
        {
            if (!m.IsValid)
                continue;

            sb.Append("\nx ").Append(m.Id).Append(' ').Append(m.Width).Append(' ').Append(m.Height)
                .Append(' ').Append(F(m.UnitsPerTexel)).Append(' ');
            Projection(sb, m.Projection);
            sb.Append('\n');

            // Deflated first: a blend map is mostly zeroes until it is painted and mostly flat where it is, so
            // this runs 50-100x — which is what keeps a painted face around a kilobyte in the file rather than
            // the 85 KB raw base64 would cost.
            string encoded = Convert.ToBase64String(Deflate(m.Texels));
            for (int at = 0; at < encoded.Length; at += Base64LineLength)
                sb.Append("d ")
                    .Append(encoded, at, Math.Min(Base64LineLength, encoded.Length - at))
                    .Append('\n');
        }
    }

    private static void Ids(StringBuilder sb, string code, List<int> ids)
    {
        sb.Append(code);
        foreach (int id in ids)
            sb.Append(' ').Append(id);
        sb.Append('\n');
    }

    private static void Vec3(StringBuilder sb, Vector3 v)
        => sb.Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z));

    private static void Projection(StringBuilder sb, VmapTexProjection p)
    {
        Vec3(sb, p.AxisU);
        sb.Append(' ');
        Vec3(sb, p.AxisV);
        sb.Append(' ').Append(F(p.OffsetU)).Append(' ').Append(F(p.OffsetV));
    }

    /// <summary>Round-trip float formatting. Lossy would drift a plane every time a map was saved.</summary>
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quote a string as one token. Materials and spawn values are user text and hold spaces; without this a
    /// space would split into two tokens and shift every field after it.
    /// </summary>
    private static string Quote(string? s)
    {
        s ??= string.Empty;
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        sb.Append('"');
        return sb.ToString();
    }

    // =============================================================================================
    //  Reading
    // =============================================================================================

    /// <summary>
    /// Parse the text form. Throws <see cref="AssetParseException"/> with the LINE NUMBER on anything
    /// malformed — a map that will not load has to say where, or the mapper is diffing a two-megabyte file by
    /// eye.
    /// </summary>
    public static VmapDocument Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var doc = new VmapDocument();
        var materials = new List<string>();
        VmapBrush? brush = null;
        VmapFace? face = null;
        VmapPatch? patch = null;
        VmapEntity? entity = null;
        VmapBlendMap? blend = null;
        StringBuilder? blendData = null;
        bool sawHeader = false;
        int lineNumber = 0;

        void FlushBlend()
        {
            if (blend is null || blendData is null)
                return;
            byte[] texels = Inflate(Convert.FromBase64String(blendData.ToString()), blend.Width * blend.Height * 4);
            if (texels.Length != blend.Width * blend.Height * 4)
                throw Fail(lineNumber, $"blend map {blend.Id} texels do not match its {blend.Width}x{blend.Height}");
            blend.Texels = texels;
            doc.BlendMaps.Add(blend);
            blend = null;
            blendData = null;
        }

        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            if (line.Length > 0 && line[0] == '﻿')
                line = line[1..];

            if (line.Length == 0)
                continue;
            if (line.StartsWith("//"))
            {
                if (sawHeader)
                    continue;
                // The header comment IS the version gate, so it is read rather than skipped.
                string[] head = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (head.Length >= 3 && head[1] == "vmap")
                {
                    if (!int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                        throw Fail(lineNumber, $"unreadable format version '{head[2]}'");
                    if (v > Version)
                        throw Fail(lineNumber,
                            $"format version {v} is newer than this build supports ({Version})");
                    doc.FormatVersion = v;
                    sawHeader = true;
                }
                continue;
            }

            if (!sawHeader)
                throw Fail(lineNumber, $"not a .vmap: expected '{Magic} {Version}' first");

            string[] tok = Tokenize(raw, lineNumber);
            if (tok.Length == 0)
                continue;

            switch (tok[0])
            {
                case "map":
                    Need(tok, 3, lineNumber);
                    ApplyManifest(doc, tok[1], tok[2]);
                    break;

                case "mat":
                {
                    Need(tok, 3, lineNumber);
                    int index = Int(tok[1], lineNumber);
                    if (index != materials.Count)
                        throw Fail(lineNumber, $"material table is out of order: expected {materials.Count}, got {index}");
                    materials.Add(tok[2]);
                    break;
                }

                case "grp":
                    Need(tok, 4, lineNumber);
                    doc.Groups.Add(new VmapGroup
                    {
                        Id = Int(tok[1], lineNumber),
                        Hidden = Int(tok[2], lineNumber) != 0,
                        Name = tok[3],
                    });
                    break;

                case "b":
                    Need(tok, 7, lineNumber);
                    FlushBlend();
                    face = null;
                    patch = null;
                    entity = null;
                    brush = new VmapBrush
                    {
                        Id = Int(tok[1], lineNumber),
                        IsDetail = Int(tok[2], lineNumber) != 0,
                        ContentFlags = Int(tok[3], lineNumber),
                        SubmodelIndex = Int(tok[4], lineNumber),
                        IsToolBrush = Int(tok[5], lineNumber) != 0,
                        GroupId = Int(tok[6], lineNumber),
                    };
                    doc.Brushes.Add(brush);
                    break;

                case "f":
                {
                    Need(tok, 17, lineNumber);
                    if (brush is null)
                        throw Fail(lineNumber, "a face outside a brush");
                    face = new VmapFace
                    {
                        Plane = new VmapPlane(ReadVec3(tok, 1, lineNumber), Float(tok[4], lineNumber)),
                        Material = Material(materials, tok[5], lineNumber),
                        SurfaceFlags = Int(tok[6], lineNumber),
                        ContentFlags = Int(tok[7], lineNumber),
                        BlendMapId = Int(tok[8], lineNumber),
                        Projection = ReadProjection(tok, 9, lineNumber),
                    };
                    brush.Faces.Add(face);
                    break;
                }

                case "l":
                {
                    Need(tok, 12, lineNumber);
                    if (face is null)
                        throw Fail(lineNumber, "a layer outside a face");
                    face.Layers.Add(new VmapFaceLayer
                    {
                        Material = Material(materials, tok[1], lineNumber),
                        Blend = (VmapBlend)Int(tok[2], lineNumber),
                        WeightChannel = Int(tok[3], lineNumber),
                        Projection = ReadProjection(tok, 4, lineNumber),
                    });
                    break;
                }

                case "p":
                    Need(tok, 8, lineNumber);
                    FlushBlend();
                    brush = null;
                    face = null;
                    entity = null;
                    patch = new VmapPatch
                    {
                        Id = Int(tok[1], lineNumber),
                        Material = Material(materials, tok[2], lineNumber),
                        Width = Int(tok[3], lineNumber),
                        Height = Int(tok[4], lineNumber),
                        SurfaceFlags = Int(tok[5], lineNumber),
                        ContentFlags = Int(tok[6], lineNumber),
                        GroupId = Int(tok[7], lineNumber),
                    };
                    doc.Patches.Add(patch);
                    break;

                case "c":
                    Need(tok, 6, lineNumber);
                    if (patch is null)
                        throw Fail(lineNumber, "a control point outside a patch");
                    patch.Controls.Add(ReadVec3(tok, 1, lineNumber));
                    patch.ControlUvs.Add(new Vector2(Float(tok[4], lineNumber), Float(tok[5], lineNumber)));
                    break;

                case "e":
                    Need(tok, 4, lineNumber);
                    FlushBlend();
                    brush = null;
                    face = null;
                    patch = null;
                    entity = new VmapEntity
                    {
                        Id = Int(tok[1], lineNumber),
                        GroupId = Int(tok[2], lineNumber),
                        ClassName = tok[3],
                    };
                    // The hoisted property and the key have to stay in step — that is VmapEntity's contract,
                    // and every writer downstream reads the key.
                    if (entity.ClassName.Length > 0)
                        entity.Fields["classname"] = entity.ClassName;
                    doc.Entities.Add(entity);
                    break;

                case "k":
                    Need(tok, 3, lineNumber);
                    if (entity is null)
                        throw Fail(lineNumber, "an entity key outside an entity");
                    entity.Fields[tok[1]] = tok[2];
                    break;

                case "eb":
                    if (entity is null)
                        throw Fail(lineNumber, "owned brushes outside an entity");
                    for (int i = 1; i < tok.Length; i++)
                        entity.BrushIds.Add(Int(tok[i], lineNumber));
                    break;

                case "ep":
                    if (entity is null)
                        throw Fail(lineNumber, "owned patches outside an entity");
                    for (int i = 1; i < tok.Length; i++)
                        entity.PatchIds.Add(Int(tok[i], lineNumber));
                    break;

                case "x":
                {
                    Need(tok, 13, lineNumber);
                    FlushBlend();
                    brush = null;
                    face = null;
                    patch = null;
                    entity = null;

                    int w = Int(tok[2], lineNumber);
                    int h = Int(tok[3], lineNumber);
                    if (w <= 0 || h <= 0 || (long)w * h * 4 > MaxBlendBytes)
                        throw Fail(lineNumber, $"blend map size {w}x{h} is out of range");

                    blend = new VmapBlendMap
                    {
                        Id = Int(tok[1], lineNumber),
                        Width = w,
                        Height = h,
                        UnitsPerTexel = Float(tok[4], lineNumber),
                        Projection = ReadProjection(tok, 5, lineNumber),
                    };
                    blendData = new StringBuilder();
                    break;
                }

                case "d":
                    Need(tok, 2, lineNumber);
                    if (blendData is null)
                        throw Fail(lineNumber, "blend texels outside a blend map");
                    blendData.Append(tok[1]);
                    break;

                default:
                    throw Fail(lineNumber, $"unknown record '{tok[0]}'");
            }
        }

        FlushBlend();
        return doc;
    }

    private static void ApplyManifest(VmapDocument doc, string key, string value)
    {
        switch (key)
        {
            case "name": doc.Manifest.Name = value; break;
            case "title": doc.Manifest.Title = value; break;
            case "sourceKind": doc.Manifest.SourceKind = value; break;
            case "sourcePath": doc.Manifest.SourcePath = value; break;
            case "sourceHash": doc.Manifest.SourceHash = value; break;
            // An unknown manifest key is ignored rather than fatal: a newer build may write one, and losing a
            // field it does not understand is a far better outcome than refusing to open the map.
        }
    }

    private static string Material(List<string> table, string token, int line)
    {
        int index = Int(token, line);
        if (index < 0 || index >= table.Count)
            throw Fail(line, $"material {index} is not in the table ({table.Count} entries)");
        return table[index];
    }

    private static Vector3 ReadVec3(string[] tok, int at, int line)
        => new(Float(tok[at], line), Float(tok[at + 1], line), Float(tok[at + 2], line));

    private static VmapTexProjection ReadProjection(string[] tok, int at, int line)
        => new(ReadVec3(tok, at, line), ReadVec3(tok, at + 3, line),
            Float(tok[at + 6], line), Float(tok[at + 7], line));

    private static void Need(string[] tok, int count, int line)
    {
        if (tok.Length < count)
            throw Fail(line, $"'{tok[0]}' needs {count - 1} fields, got {tok.Length - 1}");
    }

    private static int Int(string s, int line)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : throw Fail(line, $"'{s}' is not a whole number");

    private static float Float(string s, int line)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v
            : throw Fail(line, $"'{s}' is not a number");

    private static AssetParseException Fail(int line, string what)
        => new($"vmap: line {line}: {what}");

    /// <summary>
    /// Split a line into tokens, honouring quoted strings. Whitespace-separated otherwise, so the numeric
    /// records — which are the overwhelming majority — cost nothing but a scan.
    /// </summary>
    private static string[] Tokenize(string line, int lineNumber)
    {
        var tokens = new List<string>(20);
        var sb = new StringBuilder(32);
        int i = 0;

        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= line.Length)
                break;

            if (line[i] == '"')
            {
                i++;
                sb.Clear();
                bool closed = false;
                while (i < line.Length)
                {
                    char c = line[i++];
                    if (c == '\\' && i < line.Length)
                    {
                        char esc = line[i++];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            '"' => '"',
                            '\\' => '\\',
                            _ => esc,
                        });
                        continue;
                    }
                    if (c == '"')
                    {
                        closed = true;
                        break;
                    }
                    sb.Append(c);
                }
                if (!closed)
                    throw Fail(lineNumber, "unterminated quoted string");
                tokens.Add(sb.ToString());
                continue;
            }

            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
                i++;
            tokens.Add(line[start..i]);
        }

        return tokens.ToArray();
    }

    // =============================================================================================
    //  Texel compression
    // =============================================================================================

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] packed, int expected)
    {
        if (expected <= 0 || expected > MaxBlendBytes)
            return Array.Empty<byte>();
        try
        {
            var result = new byte[expected];
            using var ms = new MemoryStream(packed, writable: false);
            using var gz = new DeflateStream(ms, CompressionMode.Decompress);
            int read = 0;
            while (read < expected)
            {
                int n = gz.Read(result, read, expected - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read == expected ? result : Array.Empty<byte>();
        }
        catch (InvalidDataException)
        {
            return Array.Empty<byte>();
        }
    }
}
