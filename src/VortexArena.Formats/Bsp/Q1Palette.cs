// Port of Base/darkplaces/palette.c (Palette_Load, Palette_SetupSpecialPalettes) for the textures inside a
// Quake 1 format map, and of model_brush.c Mod_Q1BSP_LoadSplitSky.
namespace VortexArena.Formats.Bsp;

/// <summary>Which of DarkPlaces' derived palettes an 8-bit texture is expanded through.</summary>
public enum Q1PaletteMode
{
    /// <summary>palette_bgra_complete: every index its colour, opaque.</summary>
    Complete,
    /// <summary>palette_bgra_transparent: index 255 is transparent black (a "{" fence texture).</summary>
    Transparent,
    /// <summary>palette_bgra_nofullbrights: the full-bright indices are colour 0 (the base of a texture that has a glow layer).</summary>
    NoFullbrights,
    /// <summary>palette_bgra_nofullbrights_transparent.</summary>
    NoFullbrightsTransparent,
    /// <summary>palette_bgra_onlyfullbrights: everything but the full-bright indices is zero (the glow layer).</summary>
    OnlyFullbrights,
    /// <summary>palette_bgra_onlyfullbrights_transparent.</summary>
    OnlyFullbrightsTransparent,
}

/// <summary>
/// The Quake palette as DarkPlaces sets it up: 256 colours from <c>gfx/palette.lmp</c> (768 bytes) or the
/// built-in table, and the range of "full-bright" indices - colours that glow unlit.
///
/// <para>The full-bright range is NOT a constant. DarkPlaces reads it from the last byte of
/// <c>gfx/colormap.lmp</c> (<c>fullbright_start = 256 - colormap[16384]</c>; Quake's own file says 32, so 224
/// to 255) and, when the game data has no such file, there are no full-bright colours at all
/// (<c>fullbright_start = 256</c>). Xonotic ships no <c>gfx/colormap.lmp</c>: under Xonotic a Quake texture
/// is drawn with every index lit, and a Quake map's lamps and lava do not glow.</para>
/// </summary>
public sealed class Q1Palette
{
    public const int TransparentIndex = 255;

    /// <summary>768 bytes: red, green, blue of each index.</summary>
    public byte[] Rgb { get; }
    /// <summary>The first full-bright index; 256 when the palette has none.</summary>
    public int FullbrightStart { get; }
    public bool HasFullbrights => FullbrightStart < 256;

    private Q1Palette(byte[] rgb, int fullbrightStart)
    {
        Rgb = rgb;
        FullbrightStart = fullbrightStart;
    }

    /// <summary>The built-in table (<c>host_quakepal</c>) with no full-bright range: what DarkPlaces uses for a
    /// game without <c>gfx/palette.lmp</c> and <c>gfx/colormap.lmp</c>.</summary>
    public static Q1Palette BuiltIn => s_plain ??= new(BuiltInRgb(), 256);

    /// <summary>The built-in table with Quake's own full-bright range (224 to 255).</summary>
    public static Q1Palette BuiltInWithQuakeFullbrights => s_quake ??= new(BuiltInRgb(), 224);
    private static Q1Palette? s_plain, s_quake;

    /// <summary>
    /// Palette_Load: <paramref name="paletteLmp"/> replaces the built-in colours when it has at least 768
    /// bytes; <paramref name="colormapLmp"/> names the full-bright range when it has at least 16385.
    /// </summary>
    public static Q1Palette Load(ReadOnlySpan<byte> paletteLmp, ReadOnlySpan<byte> colormapLmp)
    {
        byte[] rgb = paletteLmp.Length >= 768 ? paletteLmp[..768].ToArray() : BuiltInRgb();
        int start = colormapLmp.Length >= 16385 ? 256 - colormapLmp[16384] : 256;
        return new Q1Palette(rgb, Math.Clamp(start, 0, 256));
    }

    public bool IsFullbright(int index) => index >= FullbrightStart;

    /// <summary>True if any pixel is a full-bright index: DarkPlaces then makes a glow layer (with
    /// <c>r_fullbrights 1</c>) and takes those pixels out of the base.</summary>
    public bool AnyFullbright(ReadOnlySpan<byte> pixels)
    {
        if (!HasFullbrights) return false;
        int start = FullbrightStart;
        foreach (byte p in pixels)
            if (p >= start) return true;
        return false;
    }

    /// <summary>The colour of an index under a mode: red, green, blue, alpha.</summary>
    public (byte R, byte G, byte B, byte A) Colour(int index, Q1PaletteMode mode)
    {
        bool transparent = mode is Q1PaletteMode.Transparent or Q1PaletteMode.NoFullbrightsTransparent or Q1PaletteMode.OnlyFullbrightsTransparent;
        if (transparent && index == TransparentIndex) return (0, 0, 0, 0);
        bool bright = index >= FullbrightStart;
        switch (mode)
        {
            case Q1PaletteMode.NoFullbrights or Q1PaletteMode.NoFullbrightsTransparent when bright:
                return (Rgb[0], Rgb[1], Rgb[2], 255);   // "palette_bgra_nofullbrights[i] = palette_bgra_complete[0]"
            case Q1PaletteMode.OnlyFullbrights or Q1PaletteMode.OnlyFullbrightsTransparent when !bright:
                return (0, 0, 0, 0);
        }
        return (Rgb[index * 3], Rgb[index * 3 + 1], Rgb[index * 3 + 2], 255);
    }

    /// <summary>Expands an 8-bit image to RGBA, four bytes a pixel.</summary>
    public byte[] ToRgba(ReadOnlySpan<byte> pixels, Q1PaletteMode mode)
    {
        Span<uint> table = stackalloc uint[256];
        for (int i = 0; i < 256; i++)
        {
            (byte r, byte g, byte b, byte a) = Colour(i, mode);
            table[i] = (uint)(r | g << 8 | b << 16 | a << 24);
        }
        byte[] rgba = new byte[pixels.Length * 4];
        for (int i = 0, o = 0; i < pixels.Length; i++, o += 4)
        {
            uint c = table[pixels[i]];
            rgba[o] = (byte)c;
            rgba[o + 1] = (byte)(c >> 8);
            rgba[o + 2] = (byte)(c >> 16);
            rgba[o + 3] = (byte)(c >> 24);
        }
        return rgba;
    }

    /// <summary>
    /// Mod_Q1BSP_LoadSplitSky for an 8-bit sky texture twice as wide as high: the right half is the solid back
    /// layer, the left half the front layer, in which index 0 is transparent and takes the back layer's average
    /// colour ("to avoid a fringe on the top level"). Both come back as RGBA, <c>width / 2</c> by <c>height</c>.
    /// </summary>
    public (byte[] Solid, byte[] Alpha, int Width, int Height) SplitSky(ReadOnlySpan<byte> pixels, int width, int height)
    {
        int w = width / 2, h = height;
        if (w <= 0 || h <= 0 || pixels.Length < width * height) return (Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        long r = 0, g = 0, b = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = pixels[y * width + x + w];
                r += Rgb[p * 3];
                g += Rgb[p * 3 + 1];
                b += Rgb[p * 3 + 2];
            }
        byte ar = (byte)(r / (w * h)), ag = (byte)(g / (w * h)), ab = (byte)(b / (w * h));
        byte[] solid = new byte[w * h * 4], alpha = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                int s = pixels[y * width + x + w];
                solid[o] = Rgb[s * 3];
                solid[o + 1] = Rgb[s * 3 + 1];
                solid[o + 2] = Rgb[s * 3 + 2];
                solid[o + 3] = 255;
                int p = pixels[y * width + x];
                if (p != 0)
                {
                    alpha[o] = Rgb[p * 3];
                    alpha[o + 1] = Rgb[p * 3 + 1];
                    alpha[o + 2] = Rgb[p * 3 + 2];
                    alpha[o + 3] = 255;
                }
                else
                {
                    alpha[o] = ar;
                    alpha[o + 1] = ag;
                    alpha[o + 2] = ab;
                    alpha[o + 3] = 0;
                }
            }
        return (solid, alpha, w, h);
    }

    private static byte[] BuiltInRgb() => (byte[])s_builtIn.Clone();

    // palette.c host_quakepal.
    private static readonly byte[] s_builtIn = Convert.FromBase64String(
        "AAAADw8PHx8fLy8vPz8/S0tLW1tba2tre3t7i4uLm5ubq6uru7u7y8vL29vb6+vrDwsHFw8LHxcLJxsPLyMTNysXPy8XSzcbUzsbW0MfY0sfa1Mfc1cfe18j" +
        "g2cjj28jCwsPExMbGxsnJyczLy8/NzdLPz9XR0dnT09zW1t/Y2OLa2uXc3Oje3uvg4O7i4vLAAAABwcACwsAExMAGxsAIyMAKysHLy8HNzcHPz8HR0cHS0sL" +
        "U1MLW1sLY2MLa2sPBwAADwAAFwAAHwAAJwAALwAANwAAPwAARwAATwAAVwAAXwAAZwAAbwAAdwAAfwAAExMAGxsAIyMALysANy8AQzcASzsHV0MHX0cHa0sL" +
        "d1MPg1cTi1sTl18bo2Mfr2cjIxMHLxcLOx8PSyMTVysXYy8fczcjfzsrj0Mzn08zr2Mvv3cvz48r36sn78sf//MbCwcAGxMAKyMPNysTRzMbUzcjYz8rb0cz" +
        "f1M/i19Hm2tTp3tft4drw5N706OL47OXq4ujn3+Xk3OHi2d7f1tvd1Nja0tXXz9LVzdDSy83QycvNx8jKxcbIxMTFwsLDwcHu3Ofr2uPo1+Dl1d3i09rf0tf" +
        "c0NTaztLXzM/Uys3RyMrOx8jLxcbIxMTFwsLDwcH28O7y7Onv6Obr5eLo4d7l3tvh29fe2NTa1dHX0s7Uz8zQzMnNysfJx8XGxMPDwsHb4N7Z3tvX3NnV2tf" +
        "T2NXR1tPP1NHN0s/L0M3KzsvIzMnHysfFyMXDxsTCxMLBwsH//Mb798X28sTy7cPu6cPq5cLm4MHi3MHe2MHa1MAW0cASzcAOysAKx8AGw8ACwcAAAD/Cwvv" +
        "ExPfGxvPIyO/KyuvLy+fLy+PLy9/Ly9vLy9fKytPIyM/GxsvExMfCwsPKwAAOwAASwcAXwcAbw8AfxcHkx8HoycLtzMPw0sbz2Mr238745dP56tf779399OL" +
        "p3s7t5s3x8M35+NXf7//q+f/1///ZwAAiwAAswAA1wAA/wAA//OT//fH////n1tT");
}
