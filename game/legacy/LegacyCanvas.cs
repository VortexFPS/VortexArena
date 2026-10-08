// Port of Base/darkplaces/clvm_cmds.c VM_drawline, VM_drawcharacter, VM_drawstring,
// VM_drawcolorcodedstring, VM_stringwidth, VM_drawpic, VM_drawsubpic, VM_drawrotpic, VM_drawfill,
// VM_drawsetcliparea, VM_drawresetcliparea, VM_findfont, VM_loadfont, VM_precache_pic, VM_getimagesize,
// VM_freepic and VM_CL_ReadPicture's Draw_NewPic, as far as they reach the renderer; gl_draw.c
// Draw_CachePic_Flags (where a picture name is looked for), LoadFont_f (the loadfont console command)
// and DrawQ_TextWidth; ft2.c Font_LoadFont as far as "which file is the font" goes (the sizing rules
// are LegacyTextLayout's).
using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Loaders;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.QuakeC;

namespace VortexArena.Game.Legacy;

/// <summary>
/// The 2D half of a QuakeC program's output on Godot: its picture cache, its font slots, the text
/// measurer, and the list one frame's draw calls are recorded into for <see cref="LegacyDrawLayer"/> to
/// replay. A client program's HUD has one (owned by <see cref="GodotLegacyPresentation"/>) and the menu
/// program has another (owned by <see cref="LegacyMenu"/>): in DarkPlaces both draw through the same
/// DrawQ functions, and here both draw through this class.
///
/// The program is untrusted. Nothing here hands a name to Godot's resource loader: pictures and fonts
/// are read through the virtual filesystem given at construction - Xonotic's game data - after
/// <see cref="LegacyQcHost.IsSafePath"/>, and every cache is bounded.
/// </summary>
public sealed class LegacyCanvas : ILegacyDraw
{
    private const int MaxTextures = 2048;
    private const int MaxDefinedPictureBytes = 512 * 1024;
    private const int MaxDefinedPictureSide = 2048;
    private const int MaxMeasuredStrings = 8192;

    private static readonly string[] PictureExtensions = { ".tga", ".png", ".jpg", ".jpeg", ".pcx", ".dds" };

    private readonly VirtualFileSystem _vfs;
    private readonly AssetLoader _assets;
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _definedPictures = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Version, Font Font, LegacyFontMetrics? Metrics, bool Outline)> _slotFonts = new();
    private readonly Dictionary<int, (int Version, string Picture, LegacyBitmapFontWidths Widths)> _slotBitmaps = new();
    private readonly Dictionary<(string Text, int Font, int Size), float> _measured = new();
    private readonly Dictionary<int, FontFile[]> _slotFaces = new();
    private readonly Dictionary<(int Font, int Rune, int Size), (FontFile Face, float Advance)> _glyphs = new();
    private readonly StringBuilder _visible = new();

    /// <param name="files">The game data pictures and fonts are read from.</param>
    public LegacyCanvas(VirtualFileSystem files, AssetLoader assets)
    {
        _vfs = files ?? throw new ArgumentNullException(nameof(files));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        Pictures = new LegacyPictureCatalog(files);
    }

    /// <summary>The 2D picture cache, as far as existence and size go.</summary>
    public LegacyPictureCatalog Pictures { get; }
    /// <summary>dp_fonts: the font slots, filled by loadfont commands and the program's loadfont builtin.</summary>
    public LegacyFontSlots Fonts { get; } = new();
    /// <summary>This frame's recorded 2D drawing.</summary>
    public LegacyDrawList DrawList { get; } = new();
    /// <summary>vid_conwidth / vid_conheight: the virtual screen the recorded positions are in. The owner sets them each frame.</summary>
    public float ConWidth { get; set; } = 640;
    public float ConHeight { get; set; } = 480;
    /// <summary>vid.mode.width / height: the window in real pixels. With the virtual size it decides which of a
    /// font's sizes a text call is drawn with. The owner sets them each frame; 0 means "same as the virtual size".</summary>
    public float PixelWidth { get; set; }
    public float PixelHeight { get; set; }
    /// <summary>r_font_size_snapping (1 in DarkPlaces, 4 in Xonotic's configuration).</summary>
    public float FontSizeSnapping { get; set; } = 1;

    /// <summary>The font maps: every glyph as DarkPlaces draws it, outline and blur baked in.</summary>
    public LegacyGlyphAtlas Atlas { get; } = new();

    /// <summary>r_textcontrast, r_textbrightness and r_textshadow, applied to every text call.</summary>
    public LegacyTextLook TextLook { get; set; } = LegacyTextLook.Plain;

    /// <summary>
    /// Reads the cvars that decide how text looks - r_font_hinting, r_font_size_snapping, the five
    /// r_font_postprocess_* and the three r_text* - from wherever the owner keeps its cvars. Called each
    /// frame; nothing happens unless one changed.
    /// </summary>
    public void ReadTextCvars(Func<string, float?> cvar)
    {
        FontSizeSnapping = cvar("r_font_size_snapping") ?? 1;
        bool kerning = (cvar("r_font_kerning") ?? 1) != 0;
        if (kerning != FontKerning)
        {
            FontKerning = kerning;
            _measured.Clear();
        }
        if (cvar("r_font_hinting") is { } hinting) FontHinting = (int)hinting;
        int before = Atlas.GlyphCount;
        Atlas.SetPostprocess(cvar("r_font_postprocess_outline") ?? 0, cvar("r_font_postprocess_blur") ?? 0,
            cvar("r_font_postprocess_shadow_x") ?? 0, cvar("r_font_postprocess_shadow_y") ?? 0, cvar("r_font_postprocess_shadow_z") ?? 0);
        if (before != 0 && Atlas.GlyphCount == 0) _glyphs.Clear();
        TextLook = new LegacyTextLook(cvar("r_textcontrast") ?? 1, cvar("r_textbrightness") ?? 0, cvar("r_textshadow") ?? 0);
    }

    /// <summary>
    /// r_font_hinting: "0 = no hinting, 1 = light autohinting, 2 = full autohinting, 3 = full hinting".
    /// DarkPlaces' default is 3; Xonotic's configuration sets 2, which is the default here. It decides
    /// more than how crisp the glyphs are: a hinted glyph's advance is a whole number of pixels, and the
    /// menu wraps its text by those advances.
    /// </summary>
    public int FontHinting
    {
        get => _fontHinting;
        set
        {
            value = Math.Clamp(value, 0, 3);
            if (value == _fontHinting) return;
            _generation++;
            _fontHinting = value;
            foreach (FontFile file in _fontFiles.Values) ApplyHinting(file);
            _measured.Clear();
            _glyphs.Clear();
            Atlas.Clear();
        }
    }
    private int _fontHinting = 2;
    private readonly Dictionary<string, FontFile> _fontFiles = new(StringComparer.Ordinal);

    private void ApplyHinting(FontFile file)
    {
        VortexArena.Game.Text.DpFontFile.ApplyHinting(file, _fontHinting);
    }

    /// <summary>
    /// A font file of the game data as DarkPlaces rasterises it. The canvas keeps its own copy rather than
    /// the asset loader's: these settings are DarkPlaces', and the native interface draws with the same
    /// file under its own.
    /// </summary>
    private FontFile? LoadFontFile(string path)
    {
        if (_fontFiles.TryGetValue(path, out FontFile? known)) return known;
        FontFile? file = null;
        try { file = VortexArena.Game.Text.DpFontFile.Create(_vfs.ReadBytes(path), path); }
        catch (System.IO.IOException) { }
        if (file is null) return null;
        ApplyHinting(file);
        if (_fontFiles.Count < 64) _fontFiles[path] = file;
        return file;
    }

    // ---- pictures ------------------------------------------------------------------------------------

    public bool PictureExists(string name) => Pictures.Precache(name);

    public QcVector ImageSize(string name)
    {
        (int width, int height) = Pictures.Size(name);
        return new QcVector(width, height, 0);
    }

    private int _generation;

    /// <summary>
    /// Changes whenever something a recorded draw command only NAMES may have changed under it: a picture
    /// freed or sent anew by the server, a font slot loaded, the hinting, the glyph atlas emptied. The draw
    /// layer keeps what it drew for a stretch of commands for as long as the commands and this number stay
    /// the same.
    /// </summary>
    public int Generation => _generation + Atlas.Generation;

    public void FreePicture(string name)
    {
        _generation++;
        Pictures.Free(name);
        _textures.Remove(name);
        _definedPictures.Remove(name);
    }

    /// <summary>
    /// #501 ReadPicture, when the client has no such picture: the server sent a small JPEG in its
    /// place (a map's preview shot). It is decoded here - bounded in bytes and in size, because the
    /// server chose both - and exists only for this session.
    /// </summary>
    public void DefinePicture(string name, ReadOnlySpan<byte> jpeg)
    {
        if (!Pictures.Define(name, jpeg)) return;
        if (jpeg.Length is 0 or > MaxDefinedPictureBytes || _definedPictures.Count >= 64 || !LegacyQcHost.IsSafePath(name)) return;
        if (!LegacyPictureCatalog.TryReadImageSize(jpeg, out int width, out int height) || width > MaxDefinedPictureSide || height > MaxDefinedPictureSide) return;
        Image image = new();
        if (image.LoadJpgFromBuffer(jpeg.ToArray()) != Error.Ok) return;
        _generation++;
        _definedPictures[name] = ImageTexture.CreateFromImage(image);
        _textures.Remove(name);
    }

    /// <summary>
    /// The texture behind a picture name, or null. Draw_CachePic looks for the name as given with each
    /// image extension, so "gfx/hud/default/ammo_shells" and "gfx/hud/default/ammo_shells.tga" are the
    /// same picture. The name is the program's: it is refused unless it is a plain relative path, and
    /// what is loaded comes from the game data and nowhere else.
    /// </summary>
    public Texture2D? LoadPictureTexture(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_textures.TryGetValue(name, out Texture2D? cached)) return cached;
        Texture2D? texture = null;
        if (_definedPictures.TryGetValue(name, out Texture2D? defined)) texture = defined;
        else if (LegacyQcHost.IsSafePath(name) && name.Length <= 200)
        {
            string stem = name;
            foreach (string extension in PictureExtensions)
                if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[..^extension.Length];
                    break;
                }
            texture = _assets.LoadTexture(stem);
        }
        // Past the bound the oldest answers are forgotten wholesale; the asset pipeline still has the textures.
        if (_textures.Count >= MaxTextures) _textures.Clear();
        // A name that drew nothing a moment ago may draw something now (and the reverse).
        _generation++;
        _textures[name] = texture;
        return texture;
    }

    public void Picture(in LegacyPicture picture) => DrawList.Picture(picture);
    public void Fill(QcVector position, QcVector size, QcVector color, float alpha, int flags) => DrawList.Fill(position, size, color, alpha, flags);
    public void Line(float width, QcVector from, QcVector to, QcVector color, float alpha, int flags) => DrawList.Line(width, from, to, color, alpha, flags);
    public void SetClipArea(float x, float y, float width, float height) => DrawList.SetClip(x, y, width, height);
    public void ResetClipArea() => DrawList.ResetClip();

    // ---- text ----------------------------------------------------------------------------------------

    /// <summary>Records the text; the colour in effect at its end is computed now, because the six-argument drawcolorcodedstring returns it.</summary>
    public QcVector Text(in LegacyText text) => DrawList.Text(text);

    /// <summary>
    /// #327 stringwidth, from the same font at the same size the replay draws with, so a string the
    /// program measured to fit a panel does fit it. A program measures the same few hundred strings
    /// every frame; the answers are kept.
    /// </summary>
    public float StringWidth(string text, bool ignoreColorCodes, QcVector scale, int font, QcVector fontScale)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (text.Length > LegacyDrawList.MaxTextLength) text = text[..LegacyDrawList.MaxTextLength];
        // "if (!h) h = w": a cell with no height is square.
        LegacyTextLayout layout = Layout(scale.X, scale.Y != 0 ? scale.Y : scale.X, fontScale.X, fontScale.Y, font, out _);
        if (layout.ScaleX == 0) return 0;

        string visible = text;
        if (!ignoreColorCodes && text.Contains('^'))
        {
            _visible.Clear();
            LegacyTextColors.Walk(text, false, new LegacyColor(1, 1, 1, 1), null, _visible);
            visible = _visible.ToString();
        }
        if (visible.Length == 0) return 0;
        return layout.Width(RasterWidth(visible, font, layout));
    }

    /// <summary>
    /// How a text call of this cell size is laid out with the font in a slot: which of the font's sizes
    /// DarkPlaces would draw it with, and so how large the glyphs really are. Measuring and drawing both
    /// come through here, so a string the program measured to fit does fit.
    /// </summary>
    internal LegacyTextLayout Layout(float width, float height, float fontScaleX, float fontScaleY, int font, out float yShift)
    {
        LegacyFontSlots.Slot record = Fonts[font];
        FontForSlot(font);
        LegacyFontMetrics? metrics = _slotFonts.TryGetValue(font, out (int Version, Font Font, LegacyFontMetrics? Metrics, bool Outline) cached) ? cached.Metrics : null;
        // "scale" in the bitmap font's .width file replaces the loadfont setting.
        float scale = BitmapFont(font).Widths.Scale ?? record.Scale;
        LegacyFontSizing sizing = new()
        {
            Sizes = record.Sizes,
            PixelsX = PixelWidth > 0 && ConWidth > 0 ? PixelWidth / ConWidth : 1,
            PixelsY = PixelHeight > 0 && ConHeight > 0 ? PixelHeight / ConHeight : 1,
            Snapping = FontSizeSnapping,
            Metrics = metrics,
            Scale = scale,
            VerticalOffset = record.VerticalOffset,
        };
        return LegacyTextLayout.For(width, height, fontScaleX, fontScaleY, sizing, out yShift);
    }

    /// <summary>
    /// The width of a run of text in rasterised pixels: outline-font stretches measured by the font,
    /// old-font glyphs (U+E000..U+E0FF) by the bitmap font's width table. Drawing walks the text the
    /// same way (<see cref="Stretches"/>), so the two agree.
    /// </summary>
    internal float RasterWidth(string text, int font, in LegacyTextLayout layout)
    {
        float width = 0;
        int previous = 0;
        foreach ((string stretch, int glyph) in Stretches(text, font))
        {
            if (glyph >= 0)
            {
                width += BitmapFont(font).Widths.Advance(glyph, layout.Cell);
                previous = 0;   // "prevch = 0" after an old-font character
                continue;
            }
            width += KerningBetween(font, layout.PixelSize, previous, stretch) + OutlineWidth(stretch, font, layout.PixelSize);
            previous = LastRune(stretch);
        }
        return width;
    }

    /// <summary>r_font_kerning: "Use kerning if available". On in DarkPlaces and in Xonotic.</summary>
    public bool FontKerning { get; private set; } = true;

    /// <summary>
    /// ft2.c Font_GetKerningForMap between two characters of a slot, in pixels of the font map: looked up in
    /// the slot's main file whatever file draws them, whole pixels or nothing (<see cref="LegacyGlyphAtlas.Kerning"/>).
    /// </summary>
    internal float Kerning(int font, int pixelSize, int left, int right)
    {
        if (!FontKerning || left == 0 || right == 0) return 0;
        FontForSlot(font);
        return _slotFaces.TryGetValue(font, out FontFile[]? faces) && faces.Length > 0 ? Atlas.Kerning(faces[0], pixelSize, left, right) : 0;
    }

    /// <summary>The kerning between the character before a stretch of text and the stretch's first character:
    /// DrawQ_String keeps its previous character across colour codes, so a pair split by one still kerns.</summary>
    internal float KerningBetween(int font, int pixelSize, int previous, string stretch)
    {
        if (previous == 0 || stretch.Length == 0 || !FontKerning) return 0;
        return Rune.DecodeFromUtf16(stretch, out Rune first, out _) == System.Buffers.OperationStatus.Done ? Kerning(font, pixelSize, previous, first.Value) : 0;
    }

    /// <summary>The last character of a stretch (0 for an empty one): DrawQ_String's prevch after drawing it.</summary>
    internal static int LastRune(string stretch)
    {
        if (stretch.Length == 0) return 0;
        return Rune.DecodeLastFromUtf16(stretch, out Rune last, out _) == System.Buffers.OperationStatus.Done ? last.Value : 0;
    }

    /// <summary>
    /// Text as DrawQ_String walks it: stretches the outline font draws, and single glyphs of the old
    /// bitmap font - "E000..E0FF: emulate old-font characters (to still have smileys and such available)".
    /// Control characters draw nothing. A slot with no outline font at all draws its text with the
    /// interface font, old-font characters included, as plain ASCII where they have one.
    /// </summary>
    internal IEnumerable<(string Text, int Glyph)> Stretches(string text, int font)
    {
        FontForSlot(font);
        bool outline = _slotFonts.TryGetValue(font, out (int Version, Font Font, LegacyFontMetrics? Metrics, bool Outline) cached) && cached.Outline;
        if (!outline)
        {
            string plain = Drawable(text);
            if (plain.Length != 0) yield return (plain, -1);
            yield break;
        }
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool old = c is >= '\uE000' and <= '\uE0FF';
            if (!old && c >= ' ') continue;
            if (i > start) yield return (text[start..i], -1);
            if (old) yield return ("", c - 0xE000);
            start = i + 1;
        }
        if (start < text.Length) yield return (start == 0 ? text : text[start..], -1);
    }

    /// <summary>
    /// The bitmap font behind a slot (fnt->pic and its width table): the first of the slot's files that
    /// is a picture, else gfx/conchars - for Xonotic's slots that is gfx/vera-sans, listed last on every
    /// loadfont line for this purpose.
    /// </summary>
    internal (string Picture, LegacyBitmapFontWidths Widths) BitmapFont(int slot)
    {
        LegacyFontSlots.Slot record = Fonts[slot];
        if (_slotBitmaps.TryGetValue(slot, out (int Version, string Picture, LegacyBitmapFontWidths Widths) cached) && cached.Version == record.Version)
            return (cached.Picture, cached.Widths);
        string picture = "gfx/conchars";
        foreach (string file in record.Files)
        {
            if (!LegacyQcHost.IsSafePath(file) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Pictures.TryGetFileSize(file, out _, out _)) continue;
            picture = file;
            break;
        }
        string? widthText = null;
        try
        {
            string widthFile = picture + ".width";
            if (LegacyQcHost.IsSafePath(widthFile) && _vfs.Exists(widthFile)) widthText = _vfs.ReadText(widthFile);
        }
        catch (System.IO.IOException) { }
        LegacyBitmapFontWidths widths = LegacyBitmapFontWidths.Parse(widthText is { Length: <= 65536 } ? widthText : null);
        if (_slotBitmaps.Count > LegacyFontSlots.MaxSlots) _slotBitmaps.Clear();
        _slotBitmaps[slot] = (record.Version, picture, widths);
        return (picture, widths);
    }

    /// <summary>
    /// One character of an outline-font slot as DarkPlaces lays it out: the first of the slot's files that
    /// has it (the first file if none does), the text to hand to the rasteriser, and how far the pen moves -
    /// "advance = glyph->advance.x / 64", the HINTED advance of the glyph FreeType loaded for the font map,
    /// a whole number of pixels. A text shaper measures with the unhinted advance and rounds that, which
    /// came out about a tenth of a pixel per character narrower: enough to wrap the menu's paragraphs at
    /// different words than DarkPlaces does. Null for a slot with no outline font.
    /// </summary>
    internal (FontFile Face, float Advance)? Glyph(int font, int rune, int pixelSize)
    {
        (int, int, int) key = (font, rune, pixelSize);
        if (_glyphs.TryGetValue(key, out (FontFile Face, float Advance) known)) return known;
        FontForSlot(font);
        if (!_slotFaces.TryGetValue(font, out FontFile[]? faces) || LegacyGlyphAtlas.FaceFor(faces, rune) is not { } face) return null;
        known = (face, Atlas.Get(face, pixelSize, rune).Advance);
        if (_glyphs.Count >= MaxMeasuredStrings) _glyphs.Clear();
        _glyphs[key] = known;
        return known;
    }

    /// <summary>
    /// Draws a stretch of outline-font text a character at a time, as DrawQ_String does, each glyph's
    /// picture (outline and all) at the pen and the pen moved by the glyph's own advance. Returns the pen
    /// afterwards.
    /// </summary>
    internal float DrawOutline(CanvasItem target, string drawable, int font, int pixelSize, float pen, float baseline, Color color)
    {
        int previous = 0;
        foreach (Rune rune in drawable.EnumerateRunes())
        {
            if (Glyph(font, rune.Value, pixelSize) is not { } glyph)
            {
                // No outline font in this slot: the interface font, shaped as a whole.
                target.DrawString(FontForSlot(font), new Vector2(pen, baseline), drawable, HorizontalAlignment.Left, -1f, pixelSize, color);
                return pen + OutlineWidth(drawable, font, pixelSize);
            }
            // "if (prevch && Font_GetKerningForMap(...)) x += kx * dw;"
            pen += Kerning(font, pixelSize, previous, rune.Value);
            previous = rune.Value;
            LegacyGlyphAtlas.Glyph picture = Atlas.Get(glyph.Face, pixelSize, rune.Value);
            if (picture.Texture is not null)
                target.DrawTextureRectRegion(picture.Texture, new Rect2(new Vector2(pen, baseline) + picture.Offset, picture.Region.Size), picture.Region, color);
            pen += glyph.Advance;
        }
        Atlas.Flush();
        return pen;
    }

    /// <summary>The width of outline-font text in rasterised pixels at one glyph size, remembered.</summary>
    internal float OutlineWidth(string drawable, int font, int pixelSize)
    {
        (string, int, int) key = (drawable, font, pixelSize);
        if (!_measured.TryGetValue(key, out float raster))
        {
            raster = 0;
            bool outline = true;
            int previous = 0;
            foreach (Rune rune in drawable.EnumerateRunes())
            {
                if (Glyph(font, rune.Value, pixelSize) is not { } glyph) { outline = false; break; }
                raster += Kerning(font, pixelSize, previous, rune.Value) + glyph.Advance;
                previous = rune.Value;
            }
            if (!outline) raster = FontForSlot(font).GetStringSize(drawable, HorizontalAlignment.Left, -1f, pixelSize).X;
            if (_measured.Count >= MaxMeasuredStrings) _measured.Clear();
            _measured[key] = raster;
        }
        return raster;
    }

    /// <summary>
    /// Characters as a TrueType font can draw them. DarkPlaces maps U+E000..U+E0FF onto the 256 glyphs
    /// of the old bitmap font ("emulate old-font characters"); the upper half of that font repeats the
    /// ASCII set in another colour, so those become plain ASCII and the rest (the font's box-drawing
    /// and icon glyphs) are dropped.
    /// </summary>
    internal static string Drawable(string text)
    {
        int i = 0;
        for (; i < text.Length; i++)
            if (text[i] is (>= '' and <= '') or < ' ') break;
        if (i == text.Length) return text;
        StringBuilder result = new(text.Length);
        result.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= '' and <= '')
            {
                int glyph = (c - 0xE000) & 0x7F;
                if (glyph is > 32 and < 127) result.Append((char)glyph);
                else if (glyph == 32) result.Append(' ');
            }
            else if (c >= ' ') result.Append(c);
        }
        return result.ToString();
    }

    public int FindFont(string name) => Fonts.Find(name);

    public int LoadFont(string name, string files, string sizes, int slot, float scale, float verticalOffset)
    {
        // VM_loadfont: the sizes are one string of numbers separated by spaces.
        List<float> wanted = new();
        foreach (string token in (sizes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size)) wanted.Add(size);
        int loaded = Fonts.Load(name, files, slot, scale, verticalOffset, wanted);
        _generation++;
        _measured.Clear();
        return loaded;
    }

    /// <summary>
    /// The loadfont console command: <c>loadfont slot face[,fallback...] [sizes...] [scale x] [voffset x]</c>.
    /// Xonotic's configuration names its fonts this way (font-xolonium.cfg), before any program runs.
    /// </summary>
    public void LoadFontCommand(IReadOnlyList<string> argv)
    {
        if (Fonts.LoadCommand(argv) < 0) return;
        _generation++;
        _measured.Clear();
        _glyphs.Clear();
    }

    /// <summary>
    /// The Godot font for a slot: the first of the slot's files the game data holds as an outline
    /// font, with the rest as its fallbacks for the characters it lacks (loadfont's "face,fallback,..." -
    /// Xolonium has no CJK and few symbols, and server names are full of both, so Xonotic lists GNU
    /// Unifont behind it). Failing all of them, the interface font. DarkPlaces' bitmap fonts
    /// (gfx/conchars, gfx/vera-sans) are not read; a slot that names only those gets the interface font.
    /// </summary>
    public Font FontForSlot(int slot)
    {
        LegacyFontSlots.Slot record = Fonts[slot];
        if (_slotFonts.TryGetValue(slot, out (int Version, Font Font, LegacyFontMetrics? Metrics, bool Outline) cached) && cached.Version == record.Version) return cached.Font;
        Font? font = null;
        LegacyFontMetrics? metrics = null;
        Godot.Collections.Array<Font> fallbacks = new();
        foreach (string file in record.Files)
        {
            if (!LegacyQcHost.IsSafePath(file)) continue;
            // "fonts/xolonium-regular" with no extension, or "fonts/n019004l.pfb" with one.
            foreach (string candidate in new[] { file, file + ".otf", file + ".ttf" })
            {
                if (!candidate.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) && !candidate.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_vfs.Exists(candidate)) continue;
                FontFile? loaded = LoadFontFile(candidate);
                if (loaded is null) continue;
                if (font is not null)
                {
                    fallbacks.Add(loaded);
                    break;
                }
                font = loaded;
                // The main file's own line metrics decide how large DarkPlaces draws it (LegacyTextLayout).
                try
                {
                    if (LegacyFontMetrics.TryRead(_vfs.ReadBytes(candidate), out LegacyFontMetrics read)) metrics = read;
                }
                catch (System.IO.IOException) { }
                break;
            }
        }
        // The files are shared between slots that order them differently ("console" puts Unifont first), so
        // the fallback list goes on a variation of the font, not on the font file itself.
        bool outline = font is not null;
        List<FontFile> faces = new();
        if (font is FontFile main) faces.Add(main);
        foreach (Font fallback in fallbacks)
            if (fallback is FontFile file) faces.Add(file);
        _slotFaces[slot] = faces.ToArray();
        _glyphs.Clear();
        if (font is not null && fallbacks.Count > 0) font = new FontVariation { BaseFont = font, Fallbacks = fallbacks };
        font ??= _assets.Fonts.GetUiFont() ?? ThemeDB.FallbackFont;
        if (_slotFonts.Count > LegacyFontSlots.MaxSlots) { _slotFonts.Clear(); _slotFaces.Clear(); }
        _generation++;   // a slot's font was (re)resolved: text drawn with the old one is stale
        _slotFonts[slot] = (record.Version, font, metrics, outline);
        return font;
    }
}
