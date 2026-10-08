// Port of Base/darkplaces/gl_draw.c string_colors[], DrawQ_GetTextColor, RGBstring_to_colorindex and the
// colour-code walk of DrawQ_String_Scale (which DrawQ_TextWidth_UntilWidth_TrackColors_Scale repeats);
// and of the calls VM_drawpic / VM_drawsubpic / VM_drawrotpic / VM_drawfill / VM_drawline /
// VM_drawstring / VM_drawsetcliparea make (clvm_cmds.c), as records instead of immediate GL.
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Presentation;

/// <summary>An RGBA colour as DrawQ_Color holds it (not clamped: additive draws use values above 1).</summary>
public readonly record struct LegacyColor(float R, float G, float B, float A)
{
    public QcVector Rgb => new(R, G, B);
}

/// <summary>A stretch of text in one colour, as it will be drawn.</summary>
public readonly record struct LegacyTextRun(string Text, LegacyColor Color);

/// <summary>
/// DarkPlaces' in-string colour codes: <c>^0</c>..<c>^9</c> pick from a fixed table, <c>^xRGB</c> is a
/// 12-bit colour, <c>^^</c> is one caret. The code colour is multiplied by the colour the caller
/// passed, so "white text" tinted red by the caller stays red.
/// </summary>
public static class LegacyTextColors
{
    /// <summary>STRING_COLOR_DEFAULT: white.</summary>
    public const int Default = 7;

    /// <summary>string_colors[]. Blue is "lighter blue, readable unlike the above"; 8 is half transparent, 9 half bright.</summary>
    public static readonly LegacyColor[] Table =
    {
        new(0, 0, 0, 1), new(1, 0, 0, 1), new(0, 1, 0, 1), new(1, 1, 0, 1), new(0.05f, 0.15f, 1, 1),
        new(0, 1, 1, 1), new(1, 0, 1, 1), new(1, 1, 1, 1), new(1, 1, 1, 0.5f), new(0.5f, 0.5f, 0.5f, 1),
    };

    /// <summary>DrawQ_GetTextColor with r_textcontrast 1 and r_textbrightness 0: table or RGB colour times the base.</summary>
    public static LegacyColor Resolve(int colorIndex, LegacyColor baseColor)
    {
        LegacyColor c = (colorIndex & 0x10000) != 0
            ? new LegacyColor(((colorIndex >> 12) & 0xF) / 15f, ((colorIndex >> 8) & 0xF) / 15f, ((colorIndex >> 4) & 0xF) / 15f, (colorIndex & 0xF) / 15f)
            : Table[(uint)colorIndex < (uint)Table.Length ? colorIndex : Default];
        return new LegacyColor(c.R * baseColor.R, c.G * baseColor.G, c.B * baseColor.B, c.A * baseColor.A);
    }

    /// <summary>RGBstring_to_colorindex: three hex digits to 0x1RGBF, or 0 if they are not hex digits.</summary>
    public static int RgbIndex(string text, int start)
    {
        if (start < 0 || start + 3 > text.Length) return 0;
        int index = 1;
        for (int i = 0; i < 3; i++)
        {
            int digit = HexDigit(text[start + i]);
            if (digit < 0) return 0;
            index = (index << 4) | digit;
        }
        return (index << 4) | 0xF;
    }

    private static int HexDigit(char c) =>
        c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>
    /// Walks <paramref name="text"/> as DrawQ_String does and returns the colour in effect at its end
    /// (what the six-argument drawcolorcodedstring returns). With <paramref name="runs"/>, also
    /// splits it into the stretches to draw, colour codes removed; with <paramref name="visible"/>,
    /// collects just the characters that are drawn (for measuring).
    /// </summary>
    public static LegacyColor Walk(string text, bool ignoreColorCodes, LegacyColor baseColor, List<LegacyTextRun>? runs = null, System.Text.StringBuilder? visible = null)
    {
        LegacyColor color = Resolve(Default, baseColor);
        if (string.IsNullOrEmpty(text)) return color;
        if (ignoreColorCodes)
        {
            runs?.Add(new LegacyTextRun(text, color));
            visible?.Append(text);
            return color;
        }

        System.Text.StringBuilder? run = runs is null ? null : new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '^' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next is >= '0' and <= '9')
                {
                    Flush();
                    color = Resolve(next - '0', baseColor);
                    i++;
                    continue;
                }
                if (next == 'x' && RgbIndex(text, i + 2) is var rgb and not 0)
                {
                    Flush();
                    color = Resolve(rgb, baseColor);
                    i += 4;
                    continue;
                }
                if (next == '^') i++; // "^^" draws one caret
            }
            run?.Append(ch);
            visible?.Append(ch);
        }
        Flush();
        return color;

        void Flush()
        {
            if (run is null || run.Length == 0) return;
            runs!.Add(new LegacyTextRun(run.ToString(), color));
            run.Clear();
        }
    }
}

public enum LegacyDrawKind : byte { Fill, Picture, Text, Line, SetClip, ResetClip, Polygon }

/// <summary>One recorded 2D call, in virtual-screen coordinates (vid_conwidth by vid_conheight).</summary>
public struct LegacyDrawCommand
{
    public LegacyDrawKind Kind;
    /// <summary>DRAWFLAG_*: 0 normal, 1 additive, 2 modulate, 3 2x modulate, 4 screen.</summary>
    public int Flags;
    /// <summary>Position and size; for a line, the two end points.</summary>
    public float X, Y, Width, Height;
    public LegacyColor Color;
    /// <summary>A picture's name, a polygon's texture, or the text.</summary>
    public string? Text;
    /// <summary>drawsubpic: the source rectangle as fractions of the picture.</summary>
    public float SourceX, SourceY, SourceWidth, SourceHeight;
    /// <summary>drawrotpic: the pivot within the picture and the angle in degrees; a line's width in <see cref="Angle"/>.</summary>
    public float PivotX, PivotY, Angle;
    public bool Rotated;
    /// <summary>Text: the font slot, the drawfontscale, and whether ^-codes are literal.</summary>
    public int Font;
    public float FontScaleX, FontScaleY;
    public bool IgnoreColorCodes;
    /// <summary>Polygon: its vertices in <see cref="LegacyDrawList.PolygonVertices"/>.</summary>
    public int VertexStart, VertexCount;
}

/// <summary>A 2D polygon vertex: position in virtual-screen coordinates, texture coordinate, colour.</summary>
public readonly record struct LegacyDrawVertex(float X, float Y, float U, float V, LegacyColor Color);

/// <summary>
/// The frame's 2D drawing, recorded as the program issues it and replayed by whoever owns a canvas.
/// The program is a stranger's code: the list is bounded, so a loop that draws forever costs that
/// frame its later draws (<see cref="Dropped"/> counts them) and nothing else.
/// </summary>
public sealed class LegacyDrawList
{
    public const int MaxCommands = 16384;
    public const int MaxPolygonVertices = 65536;
    /// <summary>Characters of text kept per frame, across all strings.</summary>
    public const int MaxTextCharacters = 262144;
    public const int MaxTextLength = 4096;

    private readonly List<LegacyDrawCommand> _commands = new(1024);
    private readonly List<LegacyDrawVertex> _vertices = new();
    private int _textCharacters;

    public IReadOnlyList<LegacyDrawCommand> Commands => _commands;
    public IReadOnlyList<LegacyDrawVertex> PolygonVertices => _vertices;
    public int Count => _commands.Count;
    /// <summary>Commands refused since <see cref="Clear"/> because a bound was reached.</summary>
    public int Dropped { get; private set; }
    /// <summary>Commands refused over the list's lifetime.</summary>
    public long TotalDropped { get; private set; }

    public void Clear()
    {
        _commands.Clear();
        _vertices.Clear();
        _textCharacters = 0;
        Dropped = 0;
    }

    private static float Finite(float value) => float.IsFinite(value) ? Math.Clamp(value, -1e6f, 1e6f) : 0;

    private static LegacyColor Finite(QcVector rgb, float alpha) => new(Finite(rgb.X), Finite(rgb.Y), Finite(rgb.Z), Finite(alpha));

    private bool Add(in LegacyDrawCommand command)
    {
        if (_commands.Count >= MaxCommands)
        {
            Dropped++;
            TotalDropped++;
            return false;
        }
        _commands.Add(command);
        return true;
    }

    public bool Fill(QcVector position, QcVector size, QcVector color, float alpha, int flags) => Add(new LegacyDrawCommand
    {
        Kind = LegacyDrawKind.Fill, Flags = flags, X = Finite(position.X), Y = Finite(position.Y), Width = Finite(size.X), Height = Finite(size.Y),
        Color = Finite(color, alpha),
    });

    public bool Line(float width, QcVector from, QcVector to, QcVector color, float alpha, int flags) => Add(new LegacyDrawCommand
    {
        Kind = LegacyDrawKind.Line, Flags = flags, X = Finite(from.X), Y = Finite(from.Y), Width = Finite(to.X), Height = Finite(to.Y),
        Angle = Math.Clamp(Finite(width), 0, 4096), Color = Finite(color, alpha),
    });

    public bool Picture(in LegacyPicture picture)
    {
        if (string.IsNullOrEmpty(picture.Name) || picture.Name.Length > 260) return false;
        return Add(new LegacyDrawCommand
        {
            Kind = LegacyDrawKind.Picture, Flags = picture.Flags, Text = picture.Name,
            X = Finite(picture.Position.X), Y = Finite(picture.Position.Y), Width = Finite(picture.Size.X), Height = Finite(picture.Size.Y),
            Color = Finite(picture.Color, picture.Alpha),
            SourceX = Finite(picture.SourcePosition.X), SourceY = Finite(picture.SourcePosition.Y),
            SourceWidth = Finite(picture.SourceSize.X), SourceHeight = Finite(picture.SourceSize.Y),
            PivotX = Finite(picture.RotationOrigin.X), PivotY = Finite(picture.RotationOrigin.Y), Angle = Finite(picture.Angle),
            Rotated = picture.Angle != 0 || picture.RotationOrigin.X != 0 || picture.RotationOrigin.Y != 0,
        });
    }

    /// <summary>Records the text and returns the colour in effect at its end (DrawQ_Color after DrawQ_String).</summary>
    public QcVector Text(in LegacyText text)
    {
        LegacyColor baseColor = Finite(text.Color, text.Alpha);
        string s = text.Text ?? "";
        if (s.Length > MaxTextLength) s = s[..MaxTextLength];
        LegacyColor last = LegacyTextColors.Walk(s, text.IgnoreColorCodes, baseColor);
        if (s.Length == 0) return last.Rgb;
        if (_textCharacters + s.Length > MaxTextCharacters)
        {
            Dropped++;
            TotalDropped++;
            return last.Rgb;
        }
        if (Add(new LegacyDrawCommand
            {
                Kind = LegacyDrawKind.Text, Flags = text.Flags, Text = s, X = Finite(text.Position.X), Y = Finite(text.Position.Y),
                Width = Finite(text.Scale.X), Height = Finite(text.Scale.Y), Color = baseColor, Font = text.Font,
                FontScaleX = Finite(text.FontScale.X), FontScaleY = Finite(text.FontScale.Y), IgnoreColorCodes = text.IgnoreColorCodes,
            }))
            _textCharacters += s.Length;
        return last.Rgb;
    }

    public bool SetClip(float x, float y, float width, float height) => Add(new LegacyDrawCommand
    {
        Kind = LegacyDrawKind.SetClip, X = Finite(x), Y = Finite(y), Width = Math.Max(0, Finite(width)), Height = Math.Max(0, Finite(height)),
    });

    public bool ResetClip() => Add(new LegacyDrawCommand { Kind = LegacyDrawKind.ResetClip });

    /// <summary>A 2D R_BeginPolygon .. R_EndPolygon: a fan of at least three vertices.</summary>
    public bool Polygon(string texture, int flags, ReadOnlySpan<LegacyPolygonVertex> vertices)
    {
        if (vertices.Length < 3) return false;
        if (_vertices.Count + vertices.Length > MaxPolygonVertices || _commands.Count >= MaxCommands)
        {
            Dropped++;
            TotalDropped++;
            return false;
        }
        int start = _vertices.Count;
        foreach (ref readonly LegacyPolygonVertex v in vertices)
            _vertices.Add(new LegacyDrawVertex(Finite(v.Position.X), Finite(v.Position.Y), Finite(v.TexCoord.X), Finite(v.TexCoord.Y), Finite(v.Color, v.Alpha)));
        return Add(new LegacyDrawCommand
        {
            Kind = LegacyDrawKind.Polygon, Flags = flags, Text = texture is { Length: > 0 and <= 260 } ? texture : null,
            VertexStart = start, VertexCount = vertices.Length,
        });
    }
}
