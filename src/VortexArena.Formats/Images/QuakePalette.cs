using System;

namespace VortexArena.Formats.Images;

/// <summary>What a palette index means to DarkPlaces (<c>palette_featureflags</c>, <c>palette.c</c>).</summary>
[Flags]
public enum QuakePaletteFeatures
{
    None = 0,
    /// <summary>An ordinary colour.</summary>
    Standard = 1,
    /// <summary>Rows 8 to 13 (indices 128..223), whose ramps run bright to dark.</summary>
    Reversed = 2,
    /// <summary>Indices 96..111: recoloured by a player's pants colour.</summary>
    Pants = 4,
    /// <summary>Indices 16..31: recoloured by a player's shirt colour.</summary>
    Shirt = 8,
    /// <summary>A full-bright index (<see cref="QuakePalette.FullbrightStart"/>..254): drawn unlit from a glow texture.</summary>
    Glow = 16,
    /// <summary>Index 0.</summary>
    Zero = 32,
    /// <summary>Index 255: transparent where the texture is one that has holes.</summary>
    Transparent = 64,
}

/// <summary>One of DarkPlaces' 256-entry conversion tables (<c>palette_bgra_*</c>, <c>palette.c</c>).</summary>
public enum QuakePaletteTable
{
    /// <summary><c>palette_bgra_complete</c>: every index its colour, opaque.</summary>
    Complete,
    /// <summary><c>palette_bgra_transparent</c>: as <see cref="Complete"/>, index 255 is transparent black.</summary>
    Transparent,
    /// <summary><c>palette_bgra_nofullbrights</c>: full-bright indices become index 0's colour (black), opaque.</summary>
    NoFullbrights,
    /// <summary><c>palette_bgra_nofullbrights_transparent</c>: <see cref="NoFullbrights"/> with index 255 transparent black.</summary>
    NoFullbrightsTransparent,
    /// <summary><c>palette_bgra_onlyfullbrights</c>: full-bright indices keep their colour, all others are transparent black.</summary>
    OnlyFullbrights,
    /// <summary><c>palette_bgra_onlyfullbrights_transparent</c>: <see cref="OnlyFullbrights"/> with index 255 transparent black.</summary>
    OnlyFullbrightsTransparent,
    /// <summary><c>palette_bgra_nocolormap</c>: pants and shirt indices are transparent black.</summary>
    NoColormap,
    /// <summary><c>palette_bgra_nocolormapnofullbrights</c>: pants, shirt and full-bright indices are transparent black.</summary>
    NoColormapNoFullbrights,
    /// <summary><c>palette_bgra_pantsaswhite</c>: pants indices as the grey ramp (row 0), all others transparent black.</summary>
    PantsAsWhite,
    /// <summary><c>palette_bgra_shirtaswhite</c>: shirt indices as the grey ramp (row 0), all others transparent black.</summary>
    ShirtAsWhite,
    /// <summary><c>palette_bgra_alpha</c>: opaque white, index 255 white with alpha 0.</summary>
    Alpha,
    /// <summary><c>palette_bgra_font</c>: as <see cref="Complete"/>, index 0 is transparent black.</summary>
    Font,
}

/// <summary>
/// An 8-bit picture converted the way DarkPlaces uploads a Quake texture
/// (<c>R_SkinFrame_LoadInternalQuake</c> + <c>R_SkinFrame_GenerateTexturesFromQPixels</c>, <c>gl_rmain.c</c>).
/// Every array is straight RGBA8, <c>width * height * 4</c> bytes, display-space values exactly as stored
/// in the palette.
/// </summary>
/// <param name="Base">The lit texture. With a <paramref name="Glow"/>, full-bright texels are black here.</param>
/// <param name="Glow">The full-bright texels only (everything else zero); added unlit. Null when the picture
/// has no full-bright texel, or the palette has no full-bright range.</param>
/// <param name="HasAlpha">True when the picture was converted with index 255 transparent (a "fence" texture or
/// a holey model skin): <paramref name="Base"/>'s alpha channel then matters. False: every texel is opaque and
/// the alpha bytes of <paramref name="Glow"/> are not to be used (it is added, not blended).</param>
public sealed record QuakeTexture(byte[] Base, byte[]? Glow, bool HasAlpha);

/// <summary>
/// The colormapped form of a skin (a player's): what DarkPlaces builds when an entity has a colormap and the
/// skin has pants or shirt texels. The pixel is
/// <c>Base + Pants * pantsColour + Shirt * shirtColour</c>, lit, plus <c>Glow</c> unlit.
/// </summary>
/// <param name="Base">The skin with pants, shirt (and, with a <paramref name="Glow"/>, full-bright) texels zero.</param>
/// <param name="Pants">Pants texels as a grey ramp, everything else zero.</param>
/// <param name="Shirt">Shirt texels as a grey ramp, everything else zero.</param>
/// <param name="Glow">As <see cref="QuakeTexture.Glow"/>.</param>
public sealed record QuakeColormappedTexture(byte[] Base, byte[] Pants, byte[] Shirt, byte[]? Glow);

/// <summary>
/// The Quake palette and DarkPlaces' rules for turning 8-bit indexed pixels into textures (<c>palette.c</c>).
/// Engine-independent: used for <c>.mdl</c> skins, <c>.spr</c> frames and the wall textures embedded in a
/// Quake 1 level.
///
/// <para><b>Which palette.</b> DarkPlaces reads <c>gfx/palette.lmp</c> (768 bytes, 256 RGB triples) and falls
/// back on a built-in copy of Quake's (<see cref="DefaultRgb"/>).</para>
///
/// <para><b>Which indices are full-bright.</b> NOT a constant. DarkPlaces reads <c>gfx/colormap.lmp</c> and
/// takes <c>256 - colormap[16384]</c> as the first full-bright index; Quake's own file says 32, hence the
/// familiar 224..255. <b>Without that file there are no full-bright indices at all</b>
/// (<c>fullbright_start = 256</c>) and no glow texture is ever split out. Xonotic's data has no
/// <c>gfx/colormap.lmp</c>, so a Quake level played on a Xonotic install shows its "full-bright" colours lit
/// like any other unless a package supplies the file. <see cref="Default"/> is that case;
/// <see cref="Quake"/> is the built-in palette with Quake's 224; <see cref="Load"/> applies DarkPlaces' rule
/// to the files a session's filesystem has.</para>
///
/// <para><b>Index 255</b> is transparent only where the caller says the picture has holes (a wall texture whose
/// name starts with <c>{</c>, a model with the <c>MF_HOLEY</c> flag, a sprite); see the
/// <c>transparent255</c> arguments. It is never full-bright (its feature is "transparent"), though once a
/// picture has some other full-bright texel an opaque conversion does carry 255's colour into the glow, as
/// DarkPlaces' <c>palette_bgra_onlyfullbrights</c> does.</para>
/// </summary>
public sealed class QuakePalette
{
    /// <summary>The index DarkPlaces treats as transparent in pictures that have holes.</summary>
    public const int TransparentIndex = 255;

    /// <summary>The first full-bright index of Quake's own <c>gfx/colormap.lmp</c>.</summary>
    public const int QuakeFullbrightStart = 224;

    private const int PantsStart = 96, PantsEnd = 112;
    private const int ShirtStart = 16, ShirtEnd = 32;
    private const int ReversedStart = 128, ReversedEnd = 224;

    /// <summary>Byte offset in <c>gfx/colormap.lmp</c> of the count of full-bright indices (after 64 rows of 256).</summary>
    private const int ColormapFullbrightCountOffset = 16384;

    private static readonly byte[] DefaultBytes = System.Convert.FromBase64String(
        "AAAADw8PHx8fLy8vPz8/S0tLW1tba2tre3t7i4uLm5ubq6uru7u7y8vL29vb6+vrDwsHFw8LHxcLJxsPLyMTNysXPy8XSzcbUzsbW0MfY0sfa1Mfc1cfe18jg2cjj28jCwsPExMbGxsnJyczLy8/NzdLPz9XR0dnT09zW1t/Y2OLa2uXc3Oje3uvg4O7i4vLAAAABwcACwsAExMAGxsAIyMAKysHLy8HNzcHPz8HR0cHS0sLU1MLW1sLY2MLa2sPBwAADwAAFwAAHwAAJwAALwAANwAAPwAARwAATwAAVwAAXwAAZwAAbwAAdwAAfwAAExMAGxsAIyMALysANy8AQzcASzsHV0MHX0cHa0sLd1MPg1cTi1sTl18bo2Mfr2cjIxMHLxcLOx8PSyMTVysXYy8fczcjfzsrj0Mzn08zr2Mvv3cvz48r36sn78sf//MbCwcAGxMAKyMPNysTRzMbUzcjYz8rb0czf1M/i19Hm2tTp3tft4drw5N706OL47OXq4ujn3+Xk3OHi2d7f1tvd1Nja0tXXz9LVzdDSy83QycvNx8jKxcbIxMTFwsLDwcHu3Ofr2uPo1+Dl1d3i09rf0tfc0NTaztLXzM/Uys3RyMrOx8jLxcbIxMTFwsLDwcH28O7y7Onv6Obr5eLo4d7l3tvh29fe2NTa1dHX0s7Uz8zQzMnNysfJx8XGxMPDwsHb4N7Z3tvX3NnV2tfT2NXR1tPP1NHN0s/L0M3KzsvIzMnHysfFyMXDxsTCxMLBwsH//Mb798X28sTy7cPu6cPq5cLm4MHi3MHe2MHa1MAW0cASzcAOysAKx8AGw8ACwcAAAD/CwvvExPfGxvPIyO/KyuvLy+fLy+PLy9/Ly9vLy9fKytPIyM/GxsvExMfCwsPKwAAOwAASwcAXwcAbw8AfxcHkx8HoycLtzMPw0sbz2Mr238745dP56tf779399OLp3s7t5s3x8M35+NXf7//q+f/1///ZwAAiwAAswAA1wAA/wAA//OT//fH////n1tT");

    private readonly byte[] _rgb;                       // 768 bytes
    private readonly QuakePaletteFeatures[] _features = new QuakePaletteFeatures[256];
    private readonly byte[]?[] _tables = new byte[]?[Enum.GetValues<QuakePaletteTable>().Length];

    private QuakePalette(byte[] rgb, int fullbrightStart)
    {
        _rgb = rgb;
        FullbrightStart = Math.Clamp(fullbrightStart, 0, 256);

        // Palette_SetupSpecialPalettes: later assignments override earlier ones, in this order.
        for (int i = 0; i < 256; i++)
            _features[i] = QuakePaletteFeatures.Standard;
        for (int i = ReversedStart; i < ReversedEnd; i++)
            _features[i] = QuakePaletteFeatures.Reversed;
        for (int i = PantsStart; i < PantsEnd; i++)
            _features[i] = QuakePaletteFeatures.Pants;
        for (int i = ShirtStart; i < ShirtEnd; i++)
            _features[i] = QuakePaletteFeatures.Shirt;
        for (int i = FullbrightStart; i < 256; i++)
            _features[i] = QuakePaletteFeatures.Glow;
        _features[0] = QuakePaletteFeatures.Zero;
        _features[TransparentIndex] = QuakePaletteFeatures.Transparent;
    }

    /// <summary>DarkPlaces' built-in palette (<c>host_quakepal</c>): 256 RGB triples.</summary>
    public static ReadOnlySpan<byte> DefaultRgb => DefaultBytes;

    /// <summary>
    /// The built-in palette with NO full-bright indices: what DarkPlaces has when neither
    /// <c>gfx/palette.lmp</c> nor <c>gfx/colormap.lmp</c> exists (a plain Xonotic install).
    /// </summary>
    public static QuakePalette Default { get; } = new((byte[])DefaultBytes.Clone(), 256);

    /// <summary>The built-in palette with Quake's own full-bright range, 224..255 (Quake's <c>gfx/colormap.lmp</c>).</summary>
    public static QuakePalette Quake { get; } = new((byte[])DefaultBytes.Clone(), QuakeFullbrightStart);

    /// <summary>
    /// DarkPlaces' <c>Palette_Load</c>: the colours of <paramref name="paletteLmp"/> (<c>gfx/palette.lmp</c>)
    /// when it has at least 768 bytes, otherwise the built-in palette; the full-bright range from
    /// <paramref name="colormapLmp"/> (<c>gfx/colormap.lmp</c>) when it has at least 16385 bytes, otherwise
    /// none. Pass null for a file the filesystem does not have.
    /// </summary>
    public static QuakePalette Load(byte[]? paletteLmp, byte[]? colormapLmp = null)
    {
        byte[] rgb = new byte[768];
        if (paletteLmp is not null && paletteLmp.Length >= 768)
            Array.Copy(paletteLmp, rgb, 768);
        else
            Array.Copy(DefaultBytes, rgb, 768);

        int start = 256;
        if (colormapLmp is not null && colormapLmp.Length > ColormapFullbrightCountOffset)
            start = 256 - colormapLmp[ColormapFullbrightCountOffset];
        return new QuakePalette(rgb, start);
    }

    /// <summary>The same colours with a different first full-bright index (256 = none).</summary>
    public QuakePalette WithFullbrightStart(int fullbrightStart) =>
        fullbrightStart == FullbrightStart ? this : new QuakePalette(_rgb, fullbrightStart);

    /// <summary>The first full-bright index; 256 when there are none.</summary>
    public int FullbrightStart { get; }

    /// <summary>The 256 RGB triples.</summary>
    public ReadOnlySpan<byte> Rgb => _rgb;

    /// <summary>What <paramref name="index"/> means (exactly one flag).</summary>
    public QuakePaletteFeatures FeatureOf(byte index) => _features[index];

    /// <summary>The union of the features of every texel (DarkPlaces' <c>featuresmask</c>).</summary>
    public QuakePaletteFeatures FeaturesOf(ReadOnlySpan<byte> indices)
    {
        QuakePaletteFeatures mask = QuakePaletteFeatures.None;
        foreach (byte b in indices)
            mask |= _features[b];
        return mask;
    }

    /// <summary>
    /// One of DarkPlaces' conversion tables as 256 RGBA quadruples (1024 bytes; red first). Shared — do not
    /// write to it.
    /// </summary>
    public ReadOnlySpan<byte> Table(QuakePaletteTable table) => TableBytes(table);

    /// <summary>Convert indexed texels through one table: <c>indices.Length * 4</c> RGBA bytes.</summary>
    public byte[] ToRgba(ReadOnlySpan<byte> indices, QuakePaletteTable table = QuakePaletteTable.Complete)
    {
        byte[] t = TableBytes(table);
        byte[] rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            int s = indices[i] * 4, d = i * 4;
            rgba[d] = t[s];
            rgba[d + 1] = t[s + 1];
            rgba[d + 2] = t[s + 2];
            rgba[d + 3] = t[s + 3];
        }
        return rgba;
    }

    /// <summary>
    /// The uncolormapped upload of a Quake texture: a lit base and, when the picture has full-bright texels
    /// and <paramref name="splitFullbrights"/> (DarkPlaces' <c>r_fullbrights</c>, default 1) is set, a glow
    /// texture of those texels with the base black there.
    /// </summary>
    /// <param name="indices">The 8-bit texels.</param>
    /// <param name="transparent255">Index 255 is a hole (a <c>{</c> wall texture, a holey model skin).</param>
    /// <param name="splitFullbrights">False keeps full-bright colours in the base and returns no glow.</param>
    public QuakeTexture Convert(ReadOnlySpan<byte> indices, bool transparent255 = false, bool splitFullbrights = true)
    {
        bool glow = splitFullbrights && (FeaturesOf(indices) & QuakePaletteFeatures.Glow) != 0;
        byte[] baseRgba = ToRgba(indices, (glow, transparent255) switch
        {
            (true, true) => QuakePaletteTable.NoFullbrightsTransparent,
            (true, false) => QuakePaletteTable.NoFullbrights,
            (false, true) => QuakePaletteTable.Transparent,
            _ => QuakePaletteTable.Complete,
        });
        byte[]? glowRgba = !glow ? null : ToRgba(indices,
            transparent255 ? QuakePaletteTable.OnlyFullbrightsTransparent : QuakePaletteTable.OnlyFullbrights);
        return new QuakeTexture(baseRgba, glowRgba, transparent255);
    }

    /// <summary>
    /// The colormapped upload (base without pants and shirt, a pants mask, a shirt mask, a glow), or null when
    /// the picture has no pants or shirt texel — DarkPlaces then draws the plain <see cref="Convert"/> form
    /// whatever the entity's colormap says.
    /// </summary>
    public QuakeColormappedTexture? ConvertColormapped(ReadOnlySpan<byte> indices, bool splitFullbrights = true)
    {
        QuakePaletteFeatures mask = FeaturesOf(indices);
        if ((mask & (QuakePaletteFeatures.Pants | QuakePaletteFeatures.Shirt)) == 0)
            return null;
        bool glow = splitFullbrights && (mask & QuakePaletteFeatures.Glow) != 0;
        return new QuakeColormappedTexture(
            ToRgba(indices, glow ? QuakePaletteTable.NoColormapNoFullbrights : QuakePaletteTable.NoColormap),
            ToRgba(indices, QuakePaletteTable.PantsAsWhite),
            ToRgba(indices, QuakePaletteTable.ShirtAsWhite),
            glow ? ToRgba(indices, QuakePaletteTable.OnlyFullbrights) : null);
    }

    /// <summary>
    /// The colour a colormap number (0..15: the high nibble of a Quake colormap is the shirt, the low the
    /// pants) stands for when no <c>gfx/colormap_palette.lmp</c> overrides it: the palette row's bright entry
    /// (<c>palette_rgb_shirtcolormap</c> / <c>_pantscolormap</c>, which are equal by default).
    /// </summary>
    public (byte R, byte G, byte B) ColormapColor(int colour)
    {
        int i = colour & 15;
        int index = (i << 4) | (i >= 8 && i <= 13 ? 0x04 : 0x0C);
        return (_rgb[index * 3], _rgb[index * 3 + 1], _rgb[index * 3 + 2]);
    }

    private byte[] TableBytes(QuakePaletteTable table)
    {
        byte[]? t = _tables[(int)table];
        if (t is not null)
            return t;
        t = BuildTable(table);
        _tables[(int)table] = t;        // a benign race: two threads build equal tables
        return t;
    }

    private byte[] BuildTable(QuakePaletteTable table)
    {
        byte[] t = new byte[1024];

        void Copy(int to, int from)
        {
            t[to * 4] = _rgb[from * 3];
            t[to * 4 + 1] = _rgb[from * 3 + 1];
            t[to * 4 + 2] = _rgb[from * 3 + 2];
            t[to * 4 + 3] = 255;
        }

        void Zero(int i) => t[i * 4] = t[i * 4 + 1] = t[i * 4 + 2] = t[i * 4 + 3] = 0;

        void Complete()
        {
            for (int i = 0; i < 256; i++)
                Copy(i, i);
        }

        switch (table)
        {
            case QuakePaletteTable.Complete:
                Complete();
                break;
            case QuakePaletteTable.Transparent:
                Complete();
                Zero(TransparentIndex);
                break;
            case QuakePaletteTable.NoFullbrights:
            case QuakePaletteTable.NoFullbrightsTransparent:
                Complete();
                for (int i = FullbrightStart; i < 256; i++)
                    Copy(i, 0);
                if (table == QuakePaletteTable.NoFullbrightsTransparent)
                    Zero(TransparentIndex);
                break;
            case QuakePaletteTable.OnlyFullbrights:
            case QuakePaletteTable.OnlyFullbrightsTransparent:
                for (int i = FullbrightStart; i < 256; i++)
                    Copy(i, i);
                if (table == QuakePaletteTable.OnlyFullbrightsTransparent)
                    Zero(TransparentIndex);
                break;
            case QuakePaletteTable.NoColormap:
            case QuakePaletteTable.NoColormapNoFullbrights:
                Complete();
                for (int i = PantsStart; i < PantsEnd; i++)
                    Zero(i);
                for (int i = ShirtStart; i < ShirtEnd; i++)
                    Zero(i);
                if (table == QuakePaletteTable.NoColormapNoFullbrights)
                    for (int i = FullbrightStart; i < 256; i++)
                        Zero(i);
                break;
            case QuakePaletteTable.PantsAsWhite:
                // The row's ramp mapped onto the grey ramp (row 0); a reversed row would be flipped, but
                // neither the pants nor the shirt row lies in the reversed range.
                for (int i = PantsStart; i < PantsEnd; i++)
                    Copy(i, i >= ReversedStart && i < ReversedEnd ? 15 - (i - PantsStart) : i - PantsStart);
                break;
            case QuakePaletteTable.ShirtAsWhite:
                for (int i = ShirtStart; i < ShirtEnd; i++)
                    Copy(i, i >= ReversedStart && i < ReversedEnd ? 15 - (i - ShirtStart) : i - ShirtStart);
                break;
            case QuakePaletteTable.Alpha:
                Array.Fill(t, (byte)255);
                t[TransparentIndex * 4 + 3] = 0;
                break;
            case QuakePaletteTable.Font:
                Complete();
                Zero(0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(table));
        }
        return t;
    }
}
