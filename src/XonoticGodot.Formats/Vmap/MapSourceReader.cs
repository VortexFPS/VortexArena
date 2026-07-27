using System.Globalization;
using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Parses a NetRadiant/Quake <c>.map</c> source file into the editable <see cref="VmapDocument"/> model
/// (design doc §11.1, phase E1). Unlike a <c>.bsp</c>, a <c>.map</c> is the AUTHORING truth: it carries the
/// brush plane sets, the real texture alignment, and the func_group/detail structure that compilation throws
/// away — so it is the better input for new maps and for the ornament-detection heuristics later on.
///
/// Supported syntax:
/// <list type="bullet">
///   <item><b>Classic Q3 texdef</b> — <c>( p0 ) ( p1 ) ( p2 ) shader xoff yoff rot xscale yscale [c s v]</c>,
///         the format Xonotic's maps use. Texture axes come from the idTech dominant-axis base table, then
///         rotate/scale/shift, exactly as q3map2 does it.</item>
///   <item><b>Valve 220 texdef</b> — <c>... shader [ ux uy uz uoff ] [ vx vy vz voff ] rot xscale yscale</c>,
///         where the axes are explicit.</item>
///   <item><b>patchDef2 / patchDef3</b> — bezier control grids.</item>
///   <item><b>brushDef</b> (Q3 brush primitives) — planes are read; the 2x3 texture matrix is converted.</item>
/// </list>
///
/// Both classic and Valve texdefs express offsets in TEXELS, so converting them into the canonical
/// repeats-based <see cref="VmapTexProjection"/> needs the texture's pixel size. Callers pass
/// <paramref name="textureSize"/> to resolve it; without one, 64x64 is assumed — the same fallback q3map2 and
/// Radiant use for an image they cannot load.
/// </summary>
public static class MapSourceReader
{
    /// <summary>Texture size assumed when no resolver is supplied or the image cannot be found.</summary>
    public const int DefaultTextureSize = 64;

    /// <summary>Q3 native content bit marking a detail brush (does not seal the world / take part in vis).</summary>
    private const int Q3ContentsDetail = 0x08000000;

    private const int Q3ContentsSolid = 1;

    /// <summary>
    /// Parse <paramref name="text"/> into a document.
    /// </summary>
    /// <param name="text">The full <c>.map</c> file contents.</param>
    /// <param name="mapName">Short map name recorded in the manifest.</param>
    /// <param name="sourcePath">Path recorded for provenance.</param>
    /// <param name="sourceHash">Content hash of the source bytes.</param>
    /// <param name="textureSize">Resolves a shader name to its (width, height) in pixels.</param>
    /// <param name="warnings">Optional sink for non-fatal problems (unknown syntax, degenerate brushes).</param>
    public static VmapDocument Read(
        string text,
        string mapName = "",
        string sourcePath = "",
        string sourceHash = "",
        Func<string, (int Width, int Height)>? textureSize = null,
        IList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var doc = new VmapDocument
        {
            Manifest = new VmapManifest
            {
                Name = mapName,
                Title = mapName,
                SourceKind = "map",
                SourcePath = sourcePath,
                SourceHash = sourceHash,
            },
        };

        var tok = new Tokenizer(text);
        var ctx = new ParseContext(doc, textureSize, warnings);

        while (tok.TryPeek(out string? token))
        {
            if (token != "{")
            {
                ctx.Warn($"expected '{{' at entity level, got '{token}' (line {tok.Line})");
                tok.Next();
                continue;
            }
            ParseEntity(tok, ctx);
        }

        return doc;
    }

    private sealed class ParseContext
    {
        public ParseContext(VmapDocument doc, Func<string, (int, int)>? textureSize, IList<string>? warnings)
        {
            Doc = doc;
            TextureSize = textureSize;
            Warnings = warnings;
        }

        public VmapDocument Doc { get; }
        public Func<string, (int Width, int Height)>? TextureSize { get; }
        public IList<string>? Warnings { get; }
        public int NextBrushId = 1;
        public int NextPatchId = 1;
        public int NextEntityId = 1;

        public void Warn(string message) => Warnings?.Add(message);

        public (float W, float H) SizeOf(string shader)
        {
            if (TextureSize is null)
                return (DefaultTextureSize, DefaultTextureSize);
            try
            {
                (int w, int h) = TextureSize(shader);
                return (w > 0 ? w : DefaultTextureSize, h > 0 ? h : DefaultTextureSize);
            }
            catch
            {
                return (DefaultTextureSize, DefaultTextureSize);
            }
        }
    }

    // =============================================================================================
    //  Entity / brush structure
    // =============================================================================================

    private static void ParseEntity(Tokenizer tok, ParseContext ctx)
    {
        tok.Expect("{");

        var ent = new VmapEntity { Id = ctx.NextEntityId++ };
        var ownedBrushes = new List<int>();
        var ownedPatches = new List<int>();

        while (tok.TryPeek(out string? token))
        {
            if (token == "}")
            {
                tok.Next();
                break;
            }

            if (token == "{")
            {
                ParseBrushOrPatch(tok, ctx, ownedBrushes, ownedPatches);
                continue;
            }

            // key/value pair — both are quoted strings in a well-formed .map.
            string key = tok.Next();
            if (!tok.TryPeek(out string? _))
                break;
            string value = tok.Next();
            ent.Fields[key] = value;
        }

        ent.Fields.TryGetValue("classname", out string? cls);
        ent.ClassName = cls ?? string.Empty;

        // func_group is a Radiant-only grouping node: q3map2 dissolves it, so its geometry is world geometry.
        // Keeping it as a brush entity would make the group solid-but-unrendered scenery instead of level.
        bool isGroup = string.Equals(ent.ClassName, "func_group", StringComparison.OrdinalIgnoreCase);

        if (!isGroup)
        {
            ent.BrushIds.AddRange(ownedBrushes);
            ent.PatchIds.AddRange(ownedPatches);
        }

        // worldspawn keeps no explicit brush list (unclaimed geometry belongs to it by definition), matching
        // how the collision and render builders decide ownership.
        if (string.Equals(ent.ClassName, "worldspawn", StringComparison.OrdinalIgnoreCase))
        {
            ent.BrushIds.Clear();
            ent.PatchIds.Clear();
        }

        if (!isGroup)
            ctx.Doc.Entities.Add(ent);
    }

    private static void ParseBrushOrPatch(Tokenizer tok, ParseContext ctx, List<int> brushIds, List<int> patchIds)
    {
        tok.Expect("{");

        if (tok.TryPeek(out string? kind))
        {
            switch (kind)
            {
                case "patchDef2":
                case "patchDef3":
                    tok.Next();
                    // ParsePatch consumes its own brace-balanced block, so this closes the outer brush block.
                    if (ParsePatch(tok, ctx, kind == "patchDef3") is { } patch)
                    {
                        ctx.Doc.Patches.Add(patch);
                        patchIds.Add(patch.Id);
                    }
                    SkipToCloseBrace(tok);
                    return;

                case "brushDef":
                    tok.Next();
                    if (ParseBrushDef(tok, ctx) is { } primitive)
                    {
                        ctx.Doc.Brushes.Add(primitive);
                        brushIds.Add(primitive.Id);
                    }
                    SkipToCloseBrace(tok);
                    return;
            }
        }

        if (ParseBrushFaces(tok, ctx) is { } brush)
        {
            ctx.Doc.Brushes.Add(brush);
            brushIds.Add(brush.Id);
        }
    }

    /// <summary>Parse the face list of a classic (or Valve 220) brush, consuming its closing brace.</summary>
    private static VmapBrush? ParseBrushFaces(Tokenizer tok, ParseContext ctx)
    {
        var brush = new VmapBrush { Id = ctx.NextBrushId++ };
        int contents = 0;

        while (tok.TryPeek(out string? token))
        {
            if (token == "}")
            {
                tok.Next();
                break;
            }

            if (!TryParseFace(tok, ctx, out VmapFace? face, out int faceContents))
            {
                // Unrecognized line: skip a token so a malformed face cannot spin the loop.
                tok.Next();
                continue;
            }

            brush.Faces.Add(face!);
            contents |= faceContents;
        }

        if (brush.Faces.Count < 4)
        {
            ctx.Warn($"brush {brush.Id} has {brush.Faces.Count} faces (needs 4) — dropped");
            return null;
        }

        brush.ContentFlags = contents != 0 ? contents : Q3ContentsSolid;
        brush.IsDetail = (contents & Q3ContentsDetail) != 0;
        return brush;
    }

    /// <summary>
    /// One face line: three plane points, a shader, then either a classic or a Valve 220 texdef.
    /// </summary>
    private static bool TryParseFace(Tokenizer tok, ParseContext ctx, out VmapFace? face, out int contents)
    {
        face = null;
        contents = 0;

        if (!TryReadPoint(tok, out Vector3 p0) ||
            !TryReadPoint(tok, out Vector3 p1) ||
            !TryReadPoint(tok, out Vector3 p2))
            return false;

        if (!VmapPlane.TryFromPoints(p0, p1, p2, out VmapPlane plane))
        {
            ctx.Warn($"degenerate face plane at line {tok.Line} — dropped");
            return false;
        }

        if (!tok.TryPeek(out string? _))
            return false;
        string shader = tok.Next();

        (float texW, float texH) = ctx.SizeOf(shader);

        VmapTexProjection projection;
        if (tok.TryPeek(out string? next) && next == "[")
        {
            // ---- Valve 220: explicit axes in texels ----
            if (!TryReadBracketed4(tok, out Vector4 u) || !TryReadBracketed4(tok, out Vector4 v))
                return false;
            _ = ReadFloat(tok);                    // rotation is already baked into the explicit axes
            float uScale = ReadFloat(tok, 1f);
            float vScale = ReadFloat(tok, 1f);
            if (uScale == 0f) uScale = 1f;
            if (vScale == 0f) vScale = 1f;

            Vector3 axisU = new Vector3(u.X, u.Y, u.Z) / (uScale * texW);
            Vector3 axisV = new Vector3(v.X, v.Y, v.Z) / (vScale * texH);
            projection = new VmapTexProjection(axisU, axisV, u.W / texW, v.W / texH);
        }
        else
        {
            // ---- Classic Q3: shift / rotate / scale over the dominant-axis base frame ----
            float shiftU = ReadFloat(tok);
            float shiftV = ReadFloat(tok);
            float rotate = ReadFloat(tok);
            float scaleU = ReadFloat(tok, 1f);
            float scaleV = ReadFloat(tok, 1f);

            // Optional trailing contents/surface/value triple (omitted by many maps).
            if (TryPeekNumber(tok, out _))
            {
                contents = (int)ReadFloat(tok);
                _ = ReadFloat(tok);   // surface flags: taken from the shader definition at load time
                _ = ReadFloat(tok);   // value
            }

            projection = ClassicProjection(plane.Normal, shiftU, shiftV, rotate, scaleU, scaleV, texW, texH);
        }

        face = new VmapFace
        {
            Plane = plane,
            Material = shader,
            Projection = projection,
            ContentFlags = contents,
        };
        return true;
    }

    /// <summary>
    /// The idTech classic texdef → world projection: pick the base axis pair for the face's dominant normal
    /// direction, rotate them in their own plane, divide by scale, and add the texel shift. Port of q3map2's
    /// <c>TextureAxisFromPlane</c> + <c>QuakeTextureVecs</c>.
    /// </summary>
    public static VmapTexProjection ClassicProjection(
        Vector3 normal, float shiftU, float shiftV, float rotate, float scaleU, float scaleV,
        float texW = DefaultTextureSize, float texH = DefaultTextureSize)
    {
        if (scaleU == 0f) scaleU = 1f;
        if (scaleV == 0f) scaleV = 1f;

        TextureAxisFromPlane(normal, out Vector3 xv, out Vector3 yv);

        // Rotate the axis pair about the face normal.
        double rad = rotate * Math.PI / 180.0;
        float sinv = (float)Math.Sin(rad), cosv = (float)Math.Cos(rad);

        // The classic implementation rotates within the two components the base axes actually use; picking the
        // first non-zero component of each axis reproduces it exactly (including the sign conventions).
        int sv = xv.X != 0f ? 0 : xv.Y != 0f ? 1 : 2;
        int tv = yv.X != 0f ? 0 : yv.Y != 0f ? 1 : 2;

        RotateAxis(ref xv, sv, tv, sinv, cosv);
        RotateAxis(ref yv, sv, tv, sinv, cosv);

        // Texels per unit -> repeats per unit.
        Vector3 axisU = xv / (scaleU * texW);
        Vector3 axisV = yv / (scaleV * texH);
        return new VmapTexProjection(axisU, axisV, shiftU / texW, shiftV / texH);
    }

    private static void RotateAxis(ref Vector3 v, int sv, int tv, float sinv, float cosv)
    {
        float s = Component(v, sv), t = Component(v, tv);
        float ns = cosv * s - sinv * t;
        float nt = sinv * s + cosv * t;
        SetComponent(ref v, sv, ns);
        SetComponent(ref v, tv, nt);
    }

    private static float Component(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    private static void SetComponent(ref Vector3 v, int i, float value)
    {
        if (i == 0) v.X = value;
        else if (i == 1) v.Y = value;
        else v.Z = value;
    }

    /// <summary>
    /// The idTech <c>baseaxis</c> table: for the face normal's dominant direction, the (U, V) axis pair the
    /// classic texdef is expressed in.
    /// </summary>
    public static void TextureAxisFromPlane(Vector3 normal, out Vector3 xv, out Vector3 yv)
    {
        ReadOnlySpan<float> baseAxis = stackalloc float[]
        {
             0, 0, 1,    1, 0, 0,    0,-1, 0,   // floor
             0, 0,-1,    1, 0, 0,    0,-1, 0,   // ceiling
             1, 0, 0,    0, 1, 0,    0, 0,-1,   // west wall
            -1, 0, 0,    0, 1, 0,    0, 0,-1,   // east wall
             0, 1, 0,    1, 0, 0,    0, 0,-1,   // south wall
             0,-1, 0,    1, 0, 0,    0, 0,-1,   // north wall
        };

        float best = 0f;
        int bestAxis = 0;
        for (int i = 0; i < 6; i++)
        {
            var candidate = new Vector3(baseAxis[i * 9 + 0], baseAxis[i * 9 + 1], baseAxis[i * 9 + 2]);
            float dot = Vector3.Dot(normal, candidate);
            if (dot > best)
            {
                best = dot;
                bestAxis = i;
            }
        }

        xv = new Vector3(baseAxis[bestAxis * 9 + 3], baseAxis[bestAxis * 9 + 4], baseAxis[bestAxis * 9 + 5]);
        yv = new Vector3(baseAxis[bestAxis * 9 + 6], baseAxis[bestAxis * 9 + 7], baseAxis[bestAxis * 9 + 8]);
    }

    /// <summary>
    /// Q3 "brush primitives" (<c>brushDef</c>): planes as usual, but the texture is a 2x3 matrix already
    /// expressed in normalized (repeat) space, which is exactly our canonical form once lifted onto the
    /// face's base axes.
    /// </summary>
    private static VmapBrush? ParseBrushDef(Tokenizer tok, ParseContext ctx)
    {
        tok.Expect("{");
        var brush = new VmapBrush { Id = ctx.NextBrushId++ };
        int contents = 0;

        while (tok.TryPeek(out string? token))
        {
            if (token == "}")
            {
                tok.Next();
                break;
            }

            if (!TryReadPoint(tok, out Vector3 p0) ||
                !TryReadPoint(tok, out Vector3 p1) ||
                !TryReadPoint(tok, out Vector3 p2))
            {
                tok.Next();
                continue;
            }

            if (!VmapPlane.TryFromPoints(p0, p1, p2, out VmapPlane plane))
                continue;

            // ( ( a b c ) ( d e f ) ) — rows of the texture matrix.
            tok.Expect("(");
            if (!TryReadTriple(tok, out Vector3 row0) || !TryReadTriple(tok, out Vector3 row1))
                continue;
            tok.Expect(")");

            string shader = tok.TryPeek(out string? _) ? tok.Next() : string.Empty;
            if (TryPeekNumber(tok, out _))
            {
                contents |= (int)ReadFloat(tok);
                _ = ReadFloat(tok);
                _ = ReadFloat(tok);
            }

            TextureAxisFromPlane(plane.Normal, out Vector3 xv, out Vector3 yv);
            Vector3 axisU = xv * row0.X + yv * row0.Y;
            Vector3 axisV = xv * row1.X + yv * row1.Y;

            brush.Faces.Add(new VmapFace
            {
                Plane = plane,
                Material = shader,
                Projection = new VmapTexProjection(axisU, axisV, row0.Z, row1.Z),
                ContentFlags = contents,
            });
        }

        if (brush.Faces.Count < 4)
        {
            ctx.Warn($"brushDef {brush.Id} has {brush.Faces.Count} faces (needs 4) — dropped");
            return null;
        }
        brush.ContentFlags = contents != 0 ? contents : Q3ContentsSolid;
        brush.IsDetail = (contents & Q3ContentsDetail) != 0;
        return brush;
    }

    /// <summary>
    /// <c>patchDef2</c>/<c>patchDef3</c>: a shader, a dimension header, then a bracketed grid of
    /// <c>( x y z u v )</c> control points.
    /// </summary>
    private static VmapPatch? ParsePatch(Tokenizer tok, ParseContext ctx, bool isPatchDef3)
    {
        tok.Expect("{");

        string shader = tok.TryPeek(out string? _) ? tok.Next() : string.Empty;

        // ( width height [subdivX subdivY] contents flags value )
        tok.Expect("(");
        int width = (int)ReadFloat(tok);
        int height = (int)ReadFloat(tok);
        if (isPatchDef3)
        {
            _ = ReadFloat(tok); // explicit subdivision X
            _ = ReadFloat(tok); // explicit subdivision Y
        }
        int contents = (int)ReadFloat(tok);
        _ = ReadFloat(tok);     // surface flags
        _ = ReadFloat(tok);     // value
        tok.Expect(")");

        var patch = new VmapPatch
        {
            Id = ctx.NextPatchId++,
            Material = shader,
            Width = width,
            Height = height,
            ContentFlags = contents != 0 ? contents : Q3ContentsSolid,
        };

        tok.Expect("(");
        for (int col = 0; col < width; col++)
        {
            tok.Expect("(");
            for (int row = 0; row < height; row++)
            {
                if (!TryReadPoint5(tok, out Vector3 pos, out Vector2 uv))
                {
                    ctx.Warn($"malformed patch control point at line {tok.Line} — patch dropped");
                    SkipToCloseBrace(tok);   // stay brace-balanced so the entity loop resynchronizes
                    return null;
                }
                patch.Controls.Add(pos);
                patch.ControlUvs.Add(uv);
            }
            tok.Expect(")");
        }
        tok.Expect(")");

        // Consume the patchDef block's own closing brace, leaving the stream balanced for the caller.
        SkipToCloseBrace(tok);

        // The file stores the grid column-major; the document model is row-major.
        Transpose(patch);

        if (!patch.IsValid)
        {
            ctx.Warn($"patch {patch.Id} has invalid dimensions {width}x{height} — dropped");
            return null;
        }
        return patch;
    }

    /// <summary>Convert a column-major control grid (the .map layout) into the row-major model layout.</summary>
    private static void Transpose(VmapPatch patch)
    {
        int w = patch.Width, h = patch.Height;
        if (patch.Controls.Count != w * h)
            return;

        var pos = new Vector3[w * h];
        var uvs = new Vector2[w * h];
        for (int col = 0; col < w; col++)
        for (int row = 0; row < h; row++)
        {
            int src = col * h + row;
            int dst = row * w + col;
            pos[dst] = patch.Controls[src];
            uvs[dst] = patch.ControlUvs[src];
        }

        patch.Controls.Clear();
        patch.Controls.AddRange(pos);
        patch.ControlUvs.Clear();
        patch.ControlUvs.AddRange(uvs);
    }

    // =============================================================================================
    //  Token helpers
    // =============================================================================================

    private static bool TryReadPoint(Tokenizer tok, out Vector3 p)
    {
        p = Vector3.Zero;
        if (!tok.TryPeek(out string? open) || open != "(")
            return false;
        tok.Next();
        if (!TryReadTriple(tok, out p))
            return false;
        if (tok.TryPeek(out string? close) && close == ")")
            tok.Next();
        return true;
    }

    private static bool TryReadTriple(Tokenizer tok, out Vector3 v)
    {
        v = Vector3.Zero;
        if (!tok.TryPeek(out string? open))
            return false;
        if (open == "(")
            tok.Next();
        float x = ReadFloat(tok), y = ReadFloat(tok), z = ReadFloat(tok);
        if (tok.TryPeek(out string? close) && close == ")")
            tok.Next();
        v = new Vector3(x, y, z);
        return true;
    }

    /// <summary>Read a <c>( x y z u v )</c> patch control point.</summary>
    private static bool TryReadPoint5(Tokenizer tok, out Vector3 pos, out Vector2 uv)
    {
        pos = Vector3.Zero;
        uv = Vector2.Zero;
        if (!tok.TryPeek(out string? open) || open != "(")
            return false;
        tok.Next();
        float x = ReadFloat(tok), y = ReadFloat(tok), z = ReadFloat(tok);
        float u = ReadFloat(tok), v = ReadFloat(tok);
        if (!tok.TryPeek(out string? close) || close != ")")
            return false;
        tok.Next();
        pos = new Vector3(x, y, z);
        uv = new Vector2(u, v);
        return true;
    }

    /// <summary>Read a Valve 220 <c>[ x y z offset ]</c> group.</summary>
    private static bool TryReadBracketed4(Tokenizer tok, out Vector4 v)
    {
        v = Vector4.Zero;
        if (!tok.TryPeek(out string? open) || open != "[")
            return false;
        tok.Next();
        float x = ReadFloat(tok), y = ReadFloat(tok), z = ReadFloat(tok), w = ReadFloat(tok);
        if (!tok.TryPeek(out string? close) || close != "]")
            return false;
        tok.Next();
        v = new Vector4(x, y, z, w);
        return true;
    }

    private static float ReadFloat(Tokenizer tok, float fallback = 0f)
    {
        if (!tok.TryPeek(out string? s))
            return fallback;
        tok.Next();
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
    }

    private static bool TryPeekNumber(Tokenizer tok, out float value)
    {
        value = 0f;
        return tok.TryPeek(out string? s)
               && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Consume tokens up to and including the matching close brace of the current nesting level.</summary>
    private static void SkipToCloseBrace(Tokenizer tok)
    {
        int depth = 1;
        while (depth > 0 && tok.TryPeek(out string? t))
        {
            tok.Next();
            if (t == "{") depth++;
            else if (t == "}") depth--;
        }
    }

    /// <summary>
    /// Lexer for the .map grammar: whitespace-separated bare tokens, quoted strings, the punctuation
    /// <c>{ } ( ) [ ]</c> as standalone tokens, and <c>//</c> line comments.
    /// </summary>
    private sealed class Tokenizer
    {
        private readonly string _text;
        private int _pos;
        private string? _peeked;

        public Tokenizer(string text) => _text = text;

        /// <summary>1-based line number of the read head, for diagnostics.</summary>
        public int Line { get; private set; } = 1;

        public bool TryPeek(out string? token)
        {
            _peeked ??= Scan();
            token = _peeked;
            return token is not null;
        }

        public string Next()
        {
            if (_peeked is not null)
            {
                string t = _peeked;
                _peeked = null;
                return t;
            }
            return Scan() ?? string.Empty;
        }

        public void Expect(string expected)
        {
            if (TryPeek(out string? t) && t == expected)
                Next();
            // A missing delimiter is not fatal: the caller's loops are all bounded by brace depth or counts,
            // so tolerating it lets a slightly malformed file still import rather than aborting the whole map.
        }

        private string? Scan()
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length)
                return null;

            char c = _text[_pos];

            if (c is '{' or '}' or '(' or ')' or '[' or ']')
            {
                _pos++;
                return c.ToString();
            }

            if (c == '"')
            {
                _pos++;
                int start = _pos;
                while (_pos < _text.Length && _text[_pos] != '"')
                {
                    if (_text[_pos] == '\n')
                        Line++;
                    _pos++;
                }
                string s = _text[start.._pos];
                if (_pos < _text.Length)
                    _pos++; // closing quote
                return s;
            }

            int begin = _pos;
            while (_pos < _text.Length && !char.IsWhiteSpace(_text[_pos])
                   && _text[_pos] is not ('{' or '}' or '(' or ')' or '[' or ']' or '"'))
                _pos++;
            return _pos > begin ? _text[begin.._pos] : null;
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c == '\n')
                {
                    Line++;
                    _pos++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n')
                        _pos++;
                }
                else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                {
                    _pos += 2;
                    while (_pos + 1 < _text.Length && !(_text[_pos] == '*' && _text[_pos + 1] == '/'))
                    {
                        if (_text[_pos] == '\n')
                            Line++;
                        _pos++;
                    }
                    _pos = Math.Min(_pos + 2, _text.Length);
                }
                else
                {
                    return;
                }
            }
        }
    }
}
