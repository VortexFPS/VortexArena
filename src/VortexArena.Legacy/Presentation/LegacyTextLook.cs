// Port of Base/darkplaces/ft2.c Font_Postprocess_Update and Font_Postprocess (the outline and blur baked into
// every glyph of a font map), and gl_draw.c DrawQ_GetTextColor (r_textcontrast, r_textbrightness and the
// colour of the r_textshadow pass).
using System;

namespace VortexArena.Legacy.Presentation;

/// <summary>
/// What DarkPlaces does to a glyph between FreeType and the font map: "this is like mplayer subtitle
/// rendering". The glyph's coverage is spread by a disc of <c>r_font_postprocess_outline</c> pixels, that
/// outline is blurred by a Gaussian of <c>r_font_postprocess_blur</c> pixels, and the result is laid
/// UNDER the glyph in black. Xonotic sets both to 1 and <c>r_textshadow</c> to 0: its text has no drop
/// shadow, it has a soft dark halo about two pixels wide that is part of each glyph's picture and is
/// therefore tinted, faded and scaled with it.
///
/// The three shadow settings shift things: x and y move the outline disc, z moves the blur.
/// </summary>
public sealed class LegacyGlyphPostprocess
{
    /// <summary>POSTPROCESS_MAXRADIUS.</summary>
    public const int MaxRadius = 8;

    private readonly byte[] _gauss = new byte[2 * MaxRadius + 1];
    private readonly byte[,] _circle = new byte[2 * MaxRadius + 1, 2 * MaxRadius + 1];
    private readonly int _outlineL, _outlineR, _outlineT, _outlineB, _blurLT, _blurRB;
    private byte[] _buf = Array.Empty<byte>(), _buf2 = Array.Empty<byte>();

    public float Outline { get; }
    public float Blur { get; }
    public float ShadowX { get; }
    public float ShadowY { get; }
    public float ShadowZ { get; }

    /// <summary>How far the processed picture reaches beyond the glyph's own bitmap on each side, in pixels.</summary>
    public int PadLeft { get; }
    public int PadRight { get; }
    public int PadTop { get; }
    public int PadBottom { get; }

    /// <summary>True when the settings change nothing ("outline the font" is skipped).</summary>
    public bool Identity => !(Outline > 0 || Blur > 0 || ShadowX != 0 || ShadowY != 0 || ShadowZ != 0);

    public LegacyGlyphPostprocess(float outline, float blur, float shadowX = 0, float shadowY = 0, float shadowZ = 0)
    {
        // A cvar can hold anything; the C clamps the paddings to the radius and so bounds the work.
        Outline = Sane(outline);
        Blur = Sane(blur);
        ShadowX = Sane(shadowX);
        ShadowY = Sane(shadowY);
        ShadowZ = Sane(shadowZ);
        _outlineL = Pad(Outline - ShadowX);
        _outlineR = Pad(Outline + ShadowX);
        _outlineT = Pad(Outline - ShadowY);
        _outlineB = Pad(Outline + ShadowY);
        _blurLT = Pad(Blur - ShadowZ);
        _blurRB = Pad(Blur + ShadowZ);
        PadLeft = _blurLT + _outlineL;
        PadRight = _blurRB + _outlineR;
        PadTop = _blurLT + _outlineT;
        PadBottom = _blurRB + _outlineB;

        Span<float> gauss = stackalloc float[2 * MaxRadius + 1];
        float sum = 0;
        for (int x = -MaxRadius; x <= MaxRadius; x++)
            gauss[MaxRadius + x] = Blur > 0
                ? MathF.Exp(-((x + ShadowZ) * (x + ShadowZ)) / (Blur * Blur * 2))
                : MathF.Floor(x + ShadowZ + 0.5f) == 0 ? 1 : 0;
        for (int x = -_blurRB; x <= _blurLT; x++) sum += gauss[MaxRadius + x];
        for (int x = -MaxRadius; x <= MaxRadius; x++)
            _gauss[MaxRadius + x] = sum > 0 ? (byte)Math.Clamp(MathF.Floor(gauss[MaxRadius + x] / sum * 255 + 0.5f), 0, 255) : (byte)0;

        for (int y = -MaxRadius; y <= MaxRadius; y++)
            for (int x = -MaxRadius; x <= MaxRadius; x++)
            {
                float d = Outline + 1 - MathF.Sqrt((x + ShadowX) * (x + ShadowX) + (y + ShadowY) * (y + ShadowY));
                _circle[MaxRadius + y, MaxRadius + x] = d >= 1 ? (byte)255 : d <= 0 ? (byte)0 : (byte)MathF.Floor(d * 255 + 0.5f);
            }

        static float Sane(float value) => float.IsFinite(value) ? Math.Clamp(value, -MaxRadius, MaxRadius) : 0;
        static int Pad(float value) => (int)Math.Clamp(MathF.Ceiling(value), 0, MaxRadius);
    }

    /// <summary>
    /// A glyph's coverage (one byte a pixel, <paramref name="width"/> by <paramref name="height"/>) as the
    /// font map holds it: RGBA, <c>width + PadLeft + PadRight</c> by <c>height + PadTop + PadBottom</c>,
    /// the glyph's own pixels starting at (PadLeft, PadTop). The face is white; where the outline shows
    /// the colour falls to black in proportion ("c' = (a2 c2 + a1 (1 - a2) c1) / a'" with a black
    /// outline), so that multiplying by the text colour tints the face and leaves the halo dark.
    /// </summary>
    public byte[] Apply(ReadOnlySpan<byte> coverage, int width, int height, out int outWidth, out int outHeight)
    {
        if (width < 0 || height < 0 || width > 4096 || height > 4096 || coverage.Length < (long)width * height)
            throw new ArgumentException("the coverage is smaller than its size says", nameof(coverage));
        bool identity = Identity;
        int padL = identity ? 0 : PadLeft, padR = identity ? 0 : PadRight, padT = identity ? 0 : PadTop, padB = identity ? 0 : PadBottom;
        outWidth = width + padL + padR;
        outHeight = height + padT + padB;
        byte[] image = new byte[outWidth * outHeight * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = ((x + padL) + (y + padT) * outWidth) * 4;
                image[at] = image[at + 1] = image[at + 2] = 255;
                image[at + 3] = coverage[x + y * width];
            }
        if (identity || outWidth == 0 || outHeight == 0) return image;

        int pitch = outWidth, needed = outWidth * outHeight;
        if (_buf.Length < needed)
        {
            _buf = new byte[needed];
            _buf2 = new byte[needed];
        }
        Array.Clear(_buf, 0, needed);

        // "create outline buffer": each pixel takes the strongest coverage the disc reaches from it.
        for (int y = -padT; y < height + padB; y++)
            for (int x = -padL; x < width + padR; x++)
            {
                int x1 = Math.Max(-x, -_outlineR), y1 = Math.Max(-y, -_outlineB);
                int x2 = Math.Min(_outlineL, width - 1 - x), y2 = Math.Min(_outlineT, height - 1 - y);
                int highest = 0;
                for (int my = y1; my <= y2; my++)
                    for (int mx = x1; mx <= x2; mx++)
                    {
                        int cur = _circle[MaxRadius + my, MaxRadius + mx] * coverage[(x + mx) + (y + my) * width];
                        if (cur > highest) highest = cur;
                    }
                _buf[(x + padL) + pitch * (y + padT)] = (byte)((highest + 128) / 255);
            }

        // "blur the outline buffer": horizontally, then vertically.
        if (Blur > 0 || ShadowZ != 0)
        {
            for (int y = 0; y < outHeight; y++)
                for (int x = 0; x < outWidth; x++)
                {
                    int x1 = Math.Max(-x, -_blurRB), x2 = Math.Min(_blurLT, outWidth - 1 - x), blurred = 0;
                    for (int mx = x1; mx <= x2; mx++) blurred += _gauss[MaxRadius + mx] * _buf[(x + mx) + pitch * y];
                    _buf2[x + pitch * y] = (byte)(Math.Clamp(blurred, 0, 65025) / 255);
                }
            for (int y = 0; y < outHeight; y++)
                for (int x = 0; x < outWidth; x++)
                {
                    int y1 = Math.Max(-y, -_blurRB), y2 = Math.Min(_blurLT, outHeight - 1 - y), blurred = 0;
                    for (int my = y1; my <= y2; my++) blurred += _gauss[MaxRadius + my] * _buf2[x + pitch * (y + my)];
                    _buf[x + pitch * y] = (byte)(Math.Clamp(blurred, 0, 65025) / 255);
                }
        }

        // "paste the outline below the font"
        for (int i = 0; i < needed; i++)
        {
            int outlineAlpha = _buf[i];
            if (outlineAlpha == 0) continue;
            int oldAlpha = image[i * 4 + 3];
            int newAlpha = 255 - ((255 - outlineAlpha) * (255 - oldAlpha)) / 255;   // "this is >= oldalpha"
            byte face = (byte)(255 * oldAlpha / newAlpha);
            image[i * 4] = image[i * 4 + 1] = image[i * 4 + 2] = face;
            image[i * 4 + 3] = (byte)newAlpha;
        }
        return image;
    }
}

/// <summary>
/// The three cvars every DrawQ_String call reads. Xonotic: contrast 0.8, brightness 0.2, shadow 0 - so
/// white text is white, black text is a fifth grey, and nothing is drawn twice.
/// </summary>
/// <param name="Contrast">r_textcontrast: "additional contrast for text (0 = no contrast, 1 = normal)".</param>
/// <param name="Brightness">r_textbrightness: "additional brightness for text (0 = black, 1 = white)".</param>
/// <param name="Shadow">r_textshadow: the offset in real pixels of a second, black copy drawn first; 0 for none.</param>
public readonly record struct LegacyTextLook(float Contrast, float Brightness, float Shadow)
{
    /// <summary>DarkPlaces' own defaults: nothing changed.</summary>
    public static readonly LegacyTextLook Plain = new(1, 0, 0);

    /// <summary>
    /// DrawQ_GetTextColor: "color * C + B" on each of red, green and blue, where the colour is already
    /// the colour code's times the call's; alpha is left alone. Not clamped - the C is not, and the
    /// renderer saturates.
    /// </summary>
    public LegacyColor Color(LegacyColor color) =>
        new(color.R * Contrast + Brightness, color.G * Contrast + Brightness, color.B * Contrast + Brightness, color.A);

    /// <summary>
    /// The colour of the shadow pass: black, at the text's alpha times "(r + g + b) * 0.8" of the colour
    /// <see cref="Color"/> gives, bounded to 0..1 - dark text gets less shadow, black text almost none.
    /// </summary>
    public LegacyColor ShadowColor(LegacyColor color)
    {
        LegacyColor text = Color(color);
        return new LegacyColor(0, 0, 0, text.A * Math.Clamp((text.R + text.G + text.B) * 0.8f, 0, 1));
    }

    /// <summary>True when a shadow pass is drawn: "r_textshadow.value != 0 &amp;&amp; basealpha &gt; 0".</summary>
    public bool HasShadow(float baseAlpha) => Shadow != 0 && float.IsFinite(Shadow) && baseAlpha > 0;
}
