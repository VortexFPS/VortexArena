// Port of Base/data/xonotic-data.pk3dir/qcsrc/menu/xonotic/slider_resolution.qc updateConwidths (the
// virtual 2D resolution Xonotic's menu program derives from the window size; with no menu program
// running, the host has to do it), and of the sizing in Base/darkplaces/gl_draw.c DrawQ_String_Scale
// ("dw = w * sw; dh = h * sh", "ftbase_y = dh * (4.5/6.0)") and the dp_fonts slot table of gl_draw_init
// and FindFont; and of ft2.c Font_LoadFont / Font_LoadSize / Font_SearchSize (which FreeType size a font
// map of a given size is rendered at), Font_VirtualToRealSize and Font_IndexForSize (which map a draw
// call uses, and when the call's size snaps to it), with the sizes LoadFont_f takes from the loadfont command.
using System.Buffers.Binary;

namespace VortexArena.Legacy.Presentation;

/// <summary>vid_conwidth / vid_conheight for a window.</summary>
public static class LegacyConsoleSize
{
    /// <summary>
    /// The 2D coordinate space for a window of <paramref name="width"/> by <paramref name="height"/>
    /// pixels: 800 wide and at least 600 high, widened instead when that would be lower than 600,
    /// never finer than the window itself. <paramref name="menuVidScale"/> is the menu_vid_scale cvar:
    /// negative values move towards one unit per pixel, positive ones towards a 640-wide space.
    /// </summary>
    public static (int Width, int Height) For(float width, float height, float pixelHeight = 1, float menuVidScale = 0)
    {
        if (!(width >= 1) || !(height >= 1)) return (800, 600);
        if (!(pixelHeight > 0)) pixelHeight = 1;
        // calculate the base resolution
        float cx = 800, cy = cx * height * pixelHeight / width;
        if (cy < 600)
        {
            cy = 600;
            cx = cy * width / (height * pixelHeight);
        }
        float f = MathF.Min(width / cx, height / cy);
        if (f < 1)
        {
            // ensures that c_x <= r_x and c_y <= r_y
            cx *= f;
            cy *= f;
        }
        float minFactor = MathF.Min(1, 640 / cx);
        float maxFactor = MathF.Max(1, MathF.Max(width / cx, height / cy));
        float sz = float.IsFinite(menuVidScale) ? Math.Clamp(menuVidScale, -1, 1) : 0;
        f = sz < 0 ? 1 - (maxFactor - 1) * sz : sz > 0 ? 1 + (minFactor - 1) * sz : 1;
        return (Math.Max(1, (int)MathF.Round(cx * f)), Math.Max(1, (int)MathF.Round(cy * f)));
    }
}

/// <summary>How one drawstring call is laid out, shared by the code that measures and the code that draws.</summary>
public readonly record struct LegacyTextLayout(int PixelSize, float ScaleX, float ScaleY, float Baseline)
{
    /// <summary>
    /// The character cell's side in rasterised pixels: the size of the font map the text is drawn from.
    /// A glyph of the old bitmap font (U+E000..U+E0FF) fills a cell this high and its own width times
    /// this wide. 0 means "the same as <see cref="PixelSize"/>" (the simple layout).
    /// </summary>
    public int CellSize { get; init; }

    /// <summary><see cref="CellSize"/>, or <see cref="PixelSize"/> when none was set.</summary>
    public int Cell => CellSize > 0 ? CellSize : PixelSize;

    /// <summary>The largest glyph size asked of a font; text taller than this is drawn scaled up.</summary>
    public const int MaxPixelSize = 256;

    /// <summary>
    /// A character cell of <paramref name="width"/> by <paramref name="height"/> virtual pixels, times
    /// the drawfontscale. The font is rasterised at <see cref="PixelSize"/> (the cell height, as
    /// DarkPlaces asks FreeType for it) and the result stretched by <see cref="ScaleX"/> /
    /// <see cref="ScaleY"/>, so a cell that is not square squeezes the glyphs as it does in the C.
    /// <see cref="Baseline"/> is where the baseline sits below the top of the cell, in rasterised
    /// pixels: three quarters of the way down.
    /// </summary>
    public static LegacyTextLayout For(float width, float height, float fontScaleX, float fontScaleY)
    {
        float dw = width * (fontScaleX != 0 ? fontScaleX : 1), dh = height * (fontScaleY != 0 ? fontScaleY : 1);
        if (!float.IsFinite(dw) || !float.IsFinite(dh) || dh == 0 || dw == 0) return new LegacyTextLayout(1, 0, 0, 0);
        int size = Math.Clamp((int)MathF.Round(MathF.Abs(dh)), 1, MaxPixelSize);
        return new LegacyTextLayout(size, dw / size, dh / size, size * (4.5f / 6.0f));
    }

    /// <summary>A width measured in rasterised pixels, as virtual pixels.</summary>
    public float Width(float rasterWidth) => rasterWidth * MathF.Abs(ScaleX);

    /// <summary>
    /// The same cell as DarkPlaces lays it out for an outline (FreeType) font, which is not "glyphs as
    /// tall as the cell":
    /// <list type="number">
    /// <item>A font is loaded as a few bitmaps ("maps"), one per size named on its loadfont line, each
    /// converted from virtual units to whole real pixels when it is loaded (Font_VirtualToRealSize).</item>
    /// <item>A map of size S is NOT rendered with an em of S pixels. Font_SearchSize looks for the largest
    /// FreeType size whose LINE height - ascender, descender and line gap - fits in S, so that a line of
    /// text fits its cell. For Xolonium, whose line is 1.2 em, a 14-pixel map holds 12-pixel glyphs.</item>
    /// <item>A draw call uses the map nearest its own size in real pixels (Font_IndexForSize), and if it
    /// is within r_font_size_snapping pixels of it, the call's cell BECOMES that map's size, so the
    /// glyphs land on whole pixels. Xonotic sets the cvar to 4: its menu font has one map, and menu text
    /// asked for at 10 to 18 pixels is all drawn at 14.</item>
    /// </list>
    /// The result is expressed as before: the font is rasterised at <see cref="PixelSize"/> (now the
    /// FreeType size of the chosen map) and stretched by <see cref="ScaleX"/> / <see cref="ScaleY"/>
    /// virtual units per rasterised pixel; the baseline is three quarters down the cell.
    /// </summary>
    /// <param name="yShift">What to add to the call's y: the font's "scale" setting keeps the text centred
    /// on its line, and "voffset" moves it ("center &amp; offset").</param>
    public static LegacyTextLayout For(float width, float height, float fontScaleX, float fontScaleY, in LegacyFontSizing font, out float yShift)
    {
        yShift = 0;
        float sw = fontScaleX != 0 ? fontScaleX : 1, sh = fontScaleY != 0 ? fontScaleY : 1;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width == 0 || height == 0) return new LegacyTextLayout(1, 0, 0, 0);
        float scale = font.Scale > 0 && float.IsFinite(font.Scale) ? font.Scale : 1;
        yShift = -((scale - 1) * height * 0.5f - font.VerticalOffset * height);
        float w = width * scale, h = height * scale;
        // A bitmap font (or a slot with no usable outline font): one glyph per cell, as tall as the cell.
        if (font.Metrics is not { } metrics || !(font.PixelsX > 0) || !(font.PixelsY > 0)) return For(w, h, sw, sh);

        // Font_IndexForSize.
        float fx = MathF.Abs(w) * font.PixelsX, fy = MathF.Abs(h) * font.PixelsY;
        int match = -1;
        float value = 1000000;
        IReadOnlyList<float>? sizes = font.Sizes;
        int count = sizes is { Count: > 0 } ? sizes.Count : 1;
        for (int m = 0; m < count; m++)
        {
            int size = MapSize(sizes is { Count: > 0 } ? sizes[m] : 0, font.PixelsY);
            if (size < 2) continue;
            float nval = 0.5f * (MathF.Abs(size - fx) + MathF.Abs(size - fy));
            // '"round up" to the bigger size if two equally-valued matches exist'
            if (match != -1 && !(nval < value) && !(nval == value && match < size)) continue;
            value = nval;
            match = size;
            if (value == 0) break;
        }
        if (match < 0) return For(w, h, sw, sh);
        if (value <= font.Snapping)
        {
            // "do NOT keep the aspect for perfect rendering"
            h = MathF.CopySign(match / font.PixelsY, h);
            w = MathF.CopySign(match / font.PixelsX, w);
        }
        float dw = w * sw, dh = h * sh;
        if (!float.IsFinite(dw) || !float.IsFinite(dh) || dw == 0 || dh == 0) return new LegacyTextLayout(1, 0, 0, 0);
        int raster = metrics.SearchSize(match);
        if (raster <= 0) raster = match;
        raster = Math.Clamp(raster, 1, MaxPixelSize);
        // "ftbase_y = dh * (4.5/6.0)", then "ftbase_y = snap_to_pixel_y(ftbase_y, 0.3)": the baseline is three
        // quarters down the cell and sits on a whole REAL pixel - the next one down once it is more than
        // three tenths past one. Expressed here in rasterised pixels of the map, which are real pixels
        // exactly when the call snapped to the map (a 14-pixel map: 10.5 becomes 11).
        float realHeight = MathF.Abs(dh) * font.PixelsY;
        float baseline = SnapY(realHeight * (4.5f / 6.0f)) * match / realHeight;
        return new LegacyTextLayout(raster, dw / match, dh / match, baseline) { CellSize = match };
    }

    /// <summary>
    /// gl_draw.c snap_to_pixel_x(x, 0.4), on a position already in real pixels: text starts on a whole
    /// pixel, the next one once it is four tenths or more past one. Without this a glyph drawn from the
    /// font map straddles two pixels and the texture filter greys its face and smears its outline.
    /// </summary>
    public static float SnapX(float pixels)
    {
        if (!float.IsFinite(pixels) || MathF.Abs(pixels) > 1e7f) return 0;
        int snap = (int)pixels;
        if (pixels - snap >= 0.4f) snap++;
        return snap;
    }

    /// <summary>snap_to_pixel_y(y, roundUpAt): as <see cref="SnapX"/>, but "more than", and 0.4 for where
    /// the text starts, 0.3 for its baseline.</summary>
    public static float SnapY(float pixels, float roundUpAt = 0.3f)
    {
        if (!float.IsFinite(pixels) || MathF.Abs(pixels) > 1e7f) return 0;
        int snap = (int)pixels;
        if (pixels - snap > roundUpAt) snap++;
        return snap;
    }

    /// <summary>
    /// The real size of the map loadfont makes for a virtual size: Font_VirtualToRealSize (rounded half
    /// up), then Font_LoadSize's "0 means 16".
    /// </summary>
    public static int MapSize(float virtualSize, float pixelsY)
    {
        if (!(virtualSize > 0.001f && virtualSize < 1000f)) return 16;
        float real = virtualSize * pixelsY;
        int size = (int)real;
        if (real - size >= 0.5f) size++;
        return size == 0 ? 16 : size;
    }
}

/// <summary>
/// The widths of a bitmap font's 256 glyphs (gl_draw.c LoadFont reading "name.width"): each a fraction of
/// the character cell, 1 where the file says nothing. DarkPlaces still draws U+E000..U+E0FF from this
/// font when the slot has an outline font - they are the old Quake character set, where Xonotic keeps its
/// coloured letters and icons - and advances by these widths.
/// </summary>
public sealed class LegacyBitmapFontWidths
{
    /// <summary>width_of[256].</summary>
    public float[] Widths { get; } = new float[256];
    /// <summary>The file's own "scale" property, which replaces the font's scale setting; null if it has none.</summary>
    public float? Scale { get; private set; }

    public LegacyBitmapFontWidths() => Array.Fill(Widths, 1f);

    /// <summary>
    /// Parses a .width file: numbers in glyph order, each with the current "extraspacing" added; the
    /// words "extraspacing" and "scale" take the next token as their value; any other word and its
    /// value are skipped. Null or empty text leaves every width at 1.
    /// </summary>
    public static LegacyBitmapFontWidths Parse(string? text)
    {
        LegacyBitmapFontWidths font = new();
        if (string.IsNullOrEmpty(text)) return font;
        string[] tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        float extraSpacing = 0;
        int ch = 0;
        for (int i = 0; i < tokens.Length && ch < 256; i++)
        {
            string token = tokens[i];
            if (token[0] is (>= '0' and <= '9') or '+' or '-' or '.')
            {
                font.Widths[ch++] = Number(token) + extraSpacing;
                continue;
            }
            if (i + 1 >= tokens.Length) break;
            string value = tokens[++i];
            if (token == "extraspacing") extraSpacing = Number(value);
            else if (token == "scale") font.Scale = Number(value);
        }
        return font;

        static float Number(string token) =>
            float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : 0;
    }

    /// <summary>
    /// How far the pen moves past glyph <paramref name="glyph"/> in a cell of <paramref name="cell"/>
    /// rasterised pixels: width_of_ft2, the width snapped to whole pixels of the map (Font_SnapTo).
    /// </summary>
    public float Advance(int glyph, int cell) => cell > 0 ? MathF.Floor(Widths[glyph & 0xFF] * cell + 0.5f) : 0;
}

/// <summary>
/// What a font file says about its line: the numbers FreeType's size metrics are made of. Read from the
/// file's own tables, so that the size DarkPlaces would pick for a font map can be computed without FreeType.
/// </summary>
/// <param name="UnitsPerEm">head.unitsPerEm.</param>
/// <param name="Height">FT_Face.height in font units: ascender - descender + line gap.</param>
public readonly record struct LegacyFontMetrics(int UnitsPerEm, int Height)
{
    /// <summary>
    /// Reads the metrics of a TrueType or OpenType font (the first face of a collection). FreeType takes
    /// the line from the OS/2 "typographic" values when the font says to (fsSelection bit 7,
    /// USE_TYPO_METRICS), otherwise from hhea, falling back to the typographic and then the Windows
    /// values when hhea's are zero (sfobjs.c sfnt_load_face).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> font, out LegacyFontMetrics metrics)
    {
        metrics = default;
        if (font.Length < 12) return false;
        int start = 0;
        if (font[..4].SequenceEqual("ttcf"u8))
        {
            if (font.Length < 16) return false;
            start = (int)BinaryPrimitives.ReadUInt32BigEndian(font[12..]);
            if (start < 0 || start > font.Length - 12) return false;
        }
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font[(start + 4)..]);
        ReadOnlySpan<byte> head = default, hhea = default, os2 = default;
        for (int i = 0; i < tables; i++)
        {
            int record = start + 12 + i * 16;
            if (record + 16 > font.Length) return false;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(font[(record + 8)..]), length = BinaryPrimitives.ReadUInt32BigEndian(font[(record + 12)..]);
            if (offset > (uint)font.Length || length > (uint)font.Length - offset) continue;
            ReadOnlySpan<byte> tag = font.Slice(record, 4), table = font.Slice((int)offset, (int)length);
            if (tag.SequenceEqual("head"u8)) head = table;
            else if (tag.SequenceEqual("hhea"u8)) hhea = table;
            else if (tag.SequenceEqual("OS/2"u8)) os2 = table;
        }
        if (head.Length < 20) return false;
        int unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head[18..]);
        if (unitsPerEm is < 16 or > 16384) return false;

        int ascender = 0, descender = 0, lineGap = 0;
        if (hhea.Length >= 10)
        {
            ascender = BinaryPrimitives.ReadInt16BigEndian(hhea[4..]);
            descender = BinaryPrimitives.ReadInt16BigEndian(hhea[6..]);
            lineGap = BinaryPrimitives.ReadInt16BigEndian(hhea[8..]);
        }
        bool hasTypo = os2.Length >= 74;
        bool useTypo = hasTypo && (BinaryPrimitives.ReadUInt16BigEndian(os2[62..]) & 128) != 0;
        int height;
        if (useTypo || (ascender == 0 && descender == 0 && hasTypo))
        {
            int typoAscender = BinaryPrimitives.ReadInt16BigEndian(os2[68..]), typoDescender = BinaryPrimitives.ReadInt16BigEndian(os2[70..]);
            int typoLineGap = BinaryPrimitives.ReadInt16BigEndian(os2[72..]);
            if (typoAscender != 0 || typoDescender != 0) height = typoAscender - typoDescender + typoLineGap;
            else if (os2.Length >= 78)
                // usWinAscent and usWinDescent, both positive.
                height = BinaryPrimitives.ReadUInt16BigEndian(os2[74..]) + BinaryPrimitives.ReadUInt16BigEndian(os2[76..]);
            else height = ascender - descender + lineGap;
        }
        else height = ascender - descender + lineGap;
        if (height <= 0 || height > 8 * unitsPerEm) return false;
        metrics = new LegacyFontMetrics(unitsPerEm, height);
        return true;
    }

    /// <summary>
    /// ft2.c Font_SearchSize: the largest whole FreeType size, counting down from <paramref name="size"/>,
    /// whose line height in whole pixels ("metrics.height >> 6") is at most <paramref name="size"/>.
    /// -1 if none down to 2 fits. FreeType rounds the scaled height to the nearest pixel.
    /// </summary>
    public int SearchSize(int size)
    {
        if (UnitsPerEm <= 0 || Height <= 0) return -1;
        for (int candidate = size; ; candidate--)
        {
            if (candidate < 1) return -1;
            // FT_PIX_ROUND(FT_MulFix(height, y_scale)) >> 6, with y_scale = candidate * 64 / unitsPerEm in 16.16.
            long scale = ((long)candidate * 64 * 65536 + UnitsPerEm / 2) / UnitsPerEm;
            long scaled = ((long)Height * scale + 0x8000) >> 16;
            if ((scaled + 32) >> 6 <= size) return candidate;
            if (candidate < 2) return -1;
        }
    }
}

/// <summary>What <see cref="LegacyTextLayout.For(float, float, float, float, in LegacyFontSizing, out float)"/>
/// needs to know about the font a call is drawn with and the screen it is drawn on.</summary>
public readonly struct LegacyFontSizing
{
    /// <summary>The sizes named on the font's loadfont line, in virtual units. None: one map, of 16 real pixels.</summary>
    public IReadOnlyList<float>? Sizes { get; init; }
    /// <summary>vid.mode.width / vid_conwidth and vid.mode.height / vid_conheight: real pixels per virtual unit.</summary>
    public float PixelsX { get; init; }
    public float PixelsY { get; init; }
    /// <summary>r_font_size_snapping: how many real pixels a call's size may be from a map's and still snap to it.</summary>
    public float Snapping { get; init; }
    /// <summary>The outline font's line metrics; null for a bitmap font, which is laid out one glyph per cell.</summary>
    public LegacyFontMetrics? Metrics { get; init; }
    /// <summary>The font's "scale" and "voffset" settings.</summary>
    public float Scale { get; init; }
    public float VerticalOffset { get; init; }
}

/// <summary>
/// dp_fonts: the engine's font slots. Eight are named by the engine, eight are "user0".."user7"
/// (Xonotic's HUD draws with user1 and user2), and loadfont appends the rest.
/// </summary>
public sealed class LegacyFontSlots
{
    /// <summary>MAX_FONTS: the slots that exist before any is added.</summary>
    public const int BuiltIn = 16;
    public const int MaxSlots = 256;
    /// <summary>MAX_FONT_SIZES.</summary>
    public const int MaxFontSizes = 16;
    public const int FontUser = 8;

    public static readonly string[] EngineNames = { "default", "console", "sbar", "notify", "chat", "centerprint", "infobar", "menu" };

    /// <summary>One slot: its title, and the font files loadfont named for it (the first is the face, the rest fallbacks).</summary>
    public sealed class Slot
    {
        public string Title = "";
        public string[] Files = Array.Empty<string>();
        public float Scale = 1, VerticalOffset;
        /// <summary>The sizes the font was loaded at, in virtual units (LoadFont_f's req_sizes). Empty: the default size.</summary>
        public float[] Sizes = Array.Empty<float>();
        /// <summary>Bumped whenever <see cref="Files"/> changes, so a renderer knows to re-resolve its font.</summary>
        public int Version;
    }

    private readonly List<Slot> _slots = new();

    public LegacyFontSlots()
    {
        foreach (string name in EngineNames) _slots.Add(new Slot { Title = name });
        for (int i = 0; i < BuiltIn - EngineNames.Length; i++) _slots.Add(new Slot { Title = "user" + i });
    }

    public int Count => _slots.Count;

    /// <summary>The slot's record, or the default font's for a number that names none (getdrawfont's fallback).</summary>
    public Slot this[int slot] => (uint)slot < (uint)_slots.Count ? _slots[slot] : _slots[0];

    /// <summary>#356 findfont: the slot titled <paramref name="name"/>, or -1.</summary>
    public int Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < _slots.Count; i++)
            if (string.Equals(_slots[i].Title, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// #357 loadfont and the loadfont console command: (re)define a slot. <paramref name="slot"/> -1
    /// finds the slot by title or allocates one. Returns the slot, or -1 if there is no room or the
    /// names are unusable.
    /// </summary>
    /// <param name="files">"face[:n][,fallback[:n]...]" as the command takes it.</param>
    /// <param name="sizes">The sizes to load the font at, in virtual units; duplicates and "crap sizes" are dropped as in the C.</param>
    public int Load(string name, string files, int slot, float scale, float verticalOffset, IReadOnlyList<float>? sizes = null)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || files is null || files.Length > 1024) return -1;
        if (slot < 0) slot = Find(name);
        if (slot < 0)
        {
            // FindFont(title, true): the first untitled slot, else a new one.
            slot = _slots.FindIndex(s => s.Title.Length == 0);
            if (slot < 0)
            {
                if (_slots.Count >= MaxSlots) return -1;
                _slots.Add(new Slot());
                slot = _slots.Count - 1;
            }
        }
        else if (slot >= _slots.Count)
        {
            if (slot >= MaxSlots) return -1;
            while (_slots.Count <= slot) _slots.Add(new Slot());
        }

        List<string> faces = new();
        foreach (string part in files.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // "file:face": the face index within a collection is not used here.
            int colon = part.IndexOf(':');
            string file = (colon >= 0 ? part[..colon] : part).Trim();
            if (file.Length is > 0 and <= 64 && faces.Count < 4) faces.Add(file);
        }
        Slot record = _slots[slot];
        record.Title = name;
        record.Files = faces.ToArray();
        record.Scale = float.IsFinite(scale) && scale > 0 ? scale : 1;
        record.VerticalOffset = float.IsFinite(verticalOffset) ? verticalOffset : 0;
        List<float> wanted = new();
        if (sizes is not null)
            foreach (float size in sizes)
                // "do not use crap sizes", "sz already in req_sizes, don't add it again", MAX_FONT_SIZES
                if (size > 0.001f && size < 1000.0f && !wanted.Contains(size) && wanted.Count < MaxFontSizes) wanted.Add(size);
        record.Sizes = wanted.ToArray();
        record.Version++;
        return slot;
    }

    /// <summary>
    /// gl_draw.c LoadFont_f, the loadfont console command: <c>loadfont slot face[,fallback...] [sizes...] [scale x]
    /// [voffset x]</c>. Xonotic's configuration names its fonts this way (font-xolonium.cfg), before any program
    /// runs. Returns the slot, or -1 when the command names none.
    /// </summary>
    public int LoadCommand(IReadOnlyList<string> argv)
    {
        if (argv is null || argv.Count < 2) return -1;
        string files = argv.Count >= 3 ? argv[2] : "gfx/conchars";
        float scale = 1, verticalOffset = 0;
        List<float> sizes = new();
        for (int i = 3; i < argv.Count; i++)
        {
            // "special switches", each followed by its value; every other argument is a size.
            if (argv[i] is "scale" or "voffset")
            {
                if (i + 1 < argv.Count)
                    float.TryParse(argv[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out argv[i] == "scale" ? ref scale : ref verticalOffset);
                i++;
                continue;
            }
            if (float.TryParse(argv[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size)) sizes.Add(size);
        }
        return Load(argv[1], files, -1, scale, verticalOffset, sizes);
    }
}
