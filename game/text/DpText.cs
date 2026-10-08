// Port of Base/darkplaces/gl_draw.c DrawQ_String_Scale / DrawQ_TextWidth (how a string is sized, snapped,
// coloured and walked glyph by glyph), gl_draw.c LoadFont_f + gl_draw_init's dp_fonts table (the font slots),
// and Base/data/font-xolonium.pk3dir/font-xolonium.cfg (which files each slot draws with).
// The Godot-free halves are VortexArena.Legacy.Presentation.LegacyTextLayout / LegacyFontSlots / LegacyTextLook /
// LegacyGlyphPostprocess; the glyph pictures come from LegacyGlyphAtlas. This file is the native client's
// front door to them - the same renderer the legacy client was verified with, without any legacy session.
using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Legacy;
using VortexArena.Legacy.Presentation;

namespace VortexArena.Game.Text;

/// <summary>
/// Text as Xonotic draws it on DarkPlaces, for every native canvas item (HUD panels, in-game overlays, the
/// console, the loading screen): per-glyph layout with FreeType's hinted advances, the outline and blur of
/// <c>r_font_postprocess_*</c> baked into each glyph, <c>r_textcontrast</c> / <c>r_textbrightness</c> on every
/// colour, <c>r_textshadow</c>'s second pass when a player turns it on, the font map nearest the asked size with
/// <c>r_font_size_snapping</c>, and the string's origin on a whole pixel.
///
/// Sizes are DarkPlaces' sizes: the HEIGHT OF THE CHARACTER CELL in pixels (what QuakeC passes to drawstring,
/// times the window's pixels per virtual unit), not an em size. A 14 pixel cell holds 12 pixel glyphs.
///
/// Everything is cached: the steady state of <see cref="Draw"/> and <see cref="Measure"/> is dictionary
/// lookups and draw calls, with no allocation.
/// </summary>
public static class DpText
{
    // dp_fonts slots (gl_draw_init): the eight the engine names, then user0..user7.
    /// <summary>FONT_CONSOLE: unifont first, then Xolonium.</summary>
    public const int Console = 1;
    /// <summary>FONT_NOTIFY.</summary>
    public const int Notify = 3;
    /// <summary>FONT_CHAT.</summary>
    public const int Chat = 4;
    /// <summary>FONT_CENTERPRINT.</summary>
    public const int Centerprint = 5;
    /// <summary>FONT_INFOBAR: the loading screen and the download/connect bar.</summary>
    public const int Infobar = 6;
    /// <summary>FONT_USER + 0: the menu's font (Xolonium regular).</summary>
    public const int Menu = LegacyFontSlots.FontUser;
    /// <summary>FONT_USER + 1: the HUD's font (QC <c>hud_font</c>).</summary>
    public const int Hud = LegacyFontSlots.FontUser + 1;
    /// <summary>FONT_USER + 2: the HUD's bold font (QC <c>hud_bigfont</c>, <c>draw_beginBoldFont</c>).</summary>
    public const int HudBold = LegacyFontSlots.FontUser + 2;
    /// <summary>FONT_USER + 3: the menu's bold font (window titles, bold labels).</summary>
    public const int MenuBold = LegacyFontSlots.FontUser + 3;

    private const string DefaultFontCfg = "font-xolonium.cfg";

    private sealed class SlotState
    {
        public FontFile[] Faces = Array.Empty<FontFile>();
        public LegacyFontMetrics? Metrics;
        /// <summary>The loadfont sizes in REAL pixels (virtual size times pixels per unit), unrounded.</summary>
        public float[] RealSizes = Array.Empty<float>();
        public float Scale = 1, VerticalOffset;
    }

    private readonly record struct CachedLayout(LegacyTextLayout Layout, float YShift);

    private static readonly LegacyFontSlots Slots = new();
    private static readonly SlotState[] States = new SlotState[LegacyFontSlots.BuiltIn];
    private static readonly Dictionary<string, FontFile> FontFiles = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LegacyFontMetrics?> FontMetrics = new(StringComparer.Ordinal);
    private static readonly Dictionary<long, LegacyGlyphAtlas.Glyph> Glyphs = new();
    private static readonly Dictionary<long, CachedLayout> Layouts = new();
    private static readonly LegacyGlyphAtlas GlyphAtlas = new();
    private static readonly List<string> Argv = new();

    private static VirtualFileSystem? _vfs;
    private static CvarService? _cvars;
    private static bool _dirty = true, _hooked;
    private static int _hinting = -1, _atlasGlyphs;
    private static float _snapping = 1;
    private static Vector2 _window;
    private static LegacyTextLook _look = LegacyTextLook.Plain;

    /// <summary>Bumped whenever the fonts, the window size or a text cvar changed what text looks like - what a
    /// consumer holding pre-built text (the menu's fonts) watches to know it has to rebuild.</summary>
    public static int Generation { get; private set; }

    /// <summary>Real pixels per virtual 2D unit: <c>vid.mode.height / vid_conheight</c> with the conheight
    /// Xonotic's menu derives from the window (slider_resolution.qc updateConwidths) - 1.2 at 1280x720.</summary>
    public static float PixelsPerUnit { get; private set; } = 1;

    /// <summary>r_textcontrast / r_textbrightness / r_textshadow as currently set.</summary>
    public static LegacyTextLook Look { get { Ensure(); return _look; } }

    /// <summary>
    /// Gives the text path the game data and the player's cvar store. Safe to call again (a rescan, a new
    /// match): fonts are re-read. Before the first call text is drawn with nothing - there are no fonts yet.
    /// </summary>
    public static void Init(VirtualFileSystem? vfs, CvarService cvars)
    {
        if (_cvars is not null && !ReferenceEquals(_cvars, cvars)) _cvars.Changed -= OnCvarChanged;
        if (!ReferenceEquals(_cvars, cvars)) cvars.Changed += OnCvarChanged;
        _vfs = vfs;
        _cvars = cvars;
        FontFiles.Clear();
        FontMetrics.Clear();
        _dirty = true;
    }

    private static void OnCvarChanged(string name)
    {
        // The cvars that decide how text looks, and the ones font-xolonium.cfg takes its sizes from.
        if (name.StartsWith("r_font", StringComparison.Ordinal) || name.StartsWith("r_text", StringComparison.Ordinal)
            || name is "con_textsize" or "con_notifysize" or "con_chatsize" or "hud_fontsize" or "menu_font_cfg" or "menu_vid_scale"
                or "scr_loadingscreen_barheight" or "scr_infobar_height")
            _dirty = true;
    }

    private static void OnWindowResized() => _dirty = true;

    private static void Ensure()
    {
        // The menu boots the game data and the cvar store; follow it (and a content rescan, which swaps the VFS).
        if (VortexArena.Game.Menu.MenuState.Booted && (_cvars is null || !ReferenceEquals(_vfs, VortexArena.Game.Menu.MenuState.Vfs)))
            Init(VortexArena.Game.Menu.MenuState.Vfs, VortexArena.Game.Menu.MenuState.Cvars);
        if (!_hooked && Godot.Engine.GetMainLoop() is SceneTree tree)
        {
            // The font maps depend on the window's height (Font_VirtualToRealSize); rebuild when it changes.
            tree.Root.SizeChanged += OnWindowResized;
            _hooked = true;
        }
        if (_dirty) Rebuild();
    }

    private static float Cvar(string name, float fallback) => _cvars is { } cvars && cvars.Has(name) ? cvars.GetFloat(name) : fallback;

    private static void Rebuild()
    {
        _dirty = false;
        Generation++;
        Vector2 window = Godot.Engine.GetMainLoop() is SceneTree tree ? tree.Root.Size : new Vector2(1280, 720);
        if (!(window.X >= 1) || !(window.Y >= 1)) window = new Vector2(1280, 720);
        _window = window;
        (int _, int conHeight) = LegacyConsoleSize.For(window.X, window.Y, 1, Cvar("menu_vid_scale", 0));
        PixelsPerUnit = window.Y / conHeight;

        _snapping = Cvar("r_font_size_snapping", 1);
        _look = new LegacyTextLook(Cvar("r_textcontrast", 1), Cvar("r_textbrightness", 0), Cvar("r_textshadow", 0));
        int hinting = Math.Clamp((int)Cvar("r_font_hinting", 3), 0, 3);
        if (hinting != _hinting)
        {
            _hinting = hinting;
            foreach (FontFile file in FontFiles.Values) DpFontFile.ApplyHinting(file, hinting);
            GlyphAtlas.Clear();
        }
        // "DarkPlaces reads them when a font is first loaded"; here a change empties the atlas.
        GlyphAtlas.SetPostprocess(Cvar("r_font_postprocess_outline", 0), Cvar("r_font_postprocess_blur", 0),
            Cvar("r_font_postprocess_shadow_x", 0), Cvar("r_font_postprocess_shadow_y", 0), Cvar("r_font_postprocess_shadow_z", 0));
        Glyphs.Clear();
        Layouts.Clear();
        _atlasGlyphs = GlyphAtlas.GlyphCount;

        LoadFontConfig();
        for (int slot = 0; slot < States.Length; slot++) States[slot] = ResolveSlot(slot);
    }

    /// <summary>
    /// quake.rc: "if_client exec font-xolonium.cfg" (the menu re-executes <c>$menu_font_cfg</c>). Only the
    /// loadfont lines matter here; <c>$name</c> is replaced by the cvar's value as the console does it.
    /// </summary>
    private static void LoadFontConfig()
    {
        string? text = null;
        string cfg = _cvars is { } cvars && cvars.Has("menu_font_cfg") ? cvars.GetString("menu_font_cfg") : "";
        if (string.IsNullOrWhiteSpace(cfg) || cfg.Contains("..") || cfg.Contains(':')) cfg = DefaultFontCfg;
        if (_vfs is { } vfs)
        {
            try
            {
                if (vfs.Exists(cfg)) text = vfs.ReadText(cfg);
                else if (vfs.Exists(DefaultFontCfg)) text = vfs.ReadText(DefaultFontCfg);
            }
            catch (System.IO.IOException) { }
        }
        // No game data (or no cfg in it): font-xolonium.cfg's own lines, so text still has its slots.
        text ??= """
            loadfont console fonts/unifont,fonts/xolonium-regular.otf,gfx/vera-sans $con_textsize
            loadfont notify fonts/xolonium-regular.otf,fonts/unifont,gfx/vera-sans $con_notifysize
            loadfont chat fonts/xolonium-regular.otf,fonts/unifont,gfx/vera-sans $con_chatsize
            loadfont centerprint fonts/xolonium-regular.otf,fonts/unifont,gfx/vera-sans 9
            loadfont infobar fonts/xolonium-regular.otf,fonts/unifont,gfx/vera-sans 8 12 $scr_loadingscreen_barheight $scr_infobar_height
            loadfont user0 fonts/xolonium-regular.otf,fonts/unifont,gfx/vera-sans 12
            loadfont user1 fonts/xolonium-regular.otf,fonts/unifont,gfx/vera-sans 4 6 8 10 12 14 16 20 24 28 32 $hud_fontsize
            loadfont user2 fonts/xolonium-bold.otf,fonts/unifont,gfx/vera-sans 4 6 8 10 12 14 16 20 24 28 32 $hud_fontsize
            loadfont user3 fonts/xolonium-bold.otf,fonts/unifont,gfx/vera-sans 12 16
            """;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (!line.StartsWith("loadfont ", StringComparison.Ordinal)) continue;
            Argv.Clear();
            foreach (string token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // "$name": the cvar's value; an unknown cvar leaves the token as it is, which is no size.
                if (token.Length > 1 && token[0] == '$' && _cvars is { } store && store.Has(token[1..]))
                {
                    string value = store.GetString(token[1..]);
                    if (value.Length != 0) Argv.Add(value);
                }
                else Argv.Add(token);
            }
            Slots.LoadCommand(Argv);
        }
    }

    private static SlotState ResolveSlot(int slot)
    {
        LegacyFontSlots.Slot record = Slots[slot];
        SlotState state = new() { Scale = record.Scale, VerticalOffset = record.VerticalOffset };
        List<FontFile> faces = new();
        foreach (string file in record.Files)
        {
            // "fonts/xolonium-regular.otf" as written, or "fonts/unifont" with no extension. The bitmap fonts
            // (gfx/vera-sans, gfx/conchars) are DarkPlaces' last resort for the old Quake character set and
            // are not read here.
            foreach (string candidate in new[] { file, file + ".otf", file + ".ttf" })
            {
                if (!candidate.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) && !candidate.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                if (LoadFontFile(candidate) is not { } loaded) continue;
                if (faces.Count == 0) state.Metrics = FontMetrics.TryGetValue(candidate, out LegacyFontMetrics? metrics) ? metrics : null;
                faces.Add(loaded);
                break;
            }
        }
        state.Faces = faces.ToArray();
        // Font_VirtualToRealSize: each loadfont size becomes a font map of that many REAL pixels.
        float[] sizes = record.Sizes;
        state.RealSizes = new float[sizes.Length];
        for (int i = 0; i < sizes.Length; i++) state.RealSizes[i] = sizes[i] * PixelsPerUnit;
        return state;
    }

    private static FontFile? LoadFontFile(string path)
    {
        if (FontFiles.TryGetValue(path, out FontFile? known)) return known;
        if (_vfs is not { } vfs) return null;
        byte[] data;
        try
        {
            if (!vfs.Exists(path)) return null;
            data = vfs.ReadBytes(path);
        }
        catch (System.IO.IOException) { return null; }
        FontFile? file = DpFontFile.Create(data, path);
        if (file is null) return null;
        DpFontFile.ApplyHinting(file, Math.Max(_hinting, 0));
        FontFiles[path] = file;
        // The file's own line metrics decide how large DarkPlaces draws it (Font_SearchSize).
        FontMetrics[path] = LegacyFontMetrics.TryRead(data, out LegacyFontMetrics read) ? read : null;
        return file;
    }

    /// <summary>The font files of a slot in fallback order (loadfont's "face,fallback,..."), for a consumer
    /// that rasterises a slot's glyphs itself (the menu's pre-built fonts).</summary>
    public static IReadOnlyList<FontFile> Faces(int slot)
    {
        Ensure();
        return State(slot).Faces;
    }

    /// <summary>The post-processing every glyph of a font map goes through, as currently configured.</summary>
    public static LegacyGlyphPostprocess Postprocess()
    {
        Ensure();
        return new LegacyGlyphPostprocess(Cvar("r_font_postprocess_outline", 0), Cvar("r_font_postprocess_blur", 0),
            Cvar("r_font_postprocess_shadow_x", 0), Cvar("r_font_postprocess_shadow_y", 0), Cvar("r_font_postprocess_shadow_z", 0));
    }

    private static SlotState State(int slot) => States[(uint)slot < (uint)States.Length ? slot : 0] ?? (States[0] ??= new SlotState());

    /// <summary>
    /// How a string of cell height <paramref name="size"/> REAL pixels is laid out in a slot: the font map
    /// DarkPlaces picks (Font_IndexForSize), whether the size snaps to it, the FreeType size of that map and
    /// the baseline. <paramref name="fontScaleX"/> / <paramref name="fontScaleY"/> are QC's drawfontscale.
    /// </summary>
    public static LegacyTextLayout Layout(int slot, float size, out float yShift, float fontScaleX = 1, float fontScaleY = 1)
    {
        Ensure();
        long key = ((long)(uint)slot << 32) | (uint)BitConverter.SingleToInt32Bits(size);
        bool plain = fontScaleX == 1 && fontScaleY == 1;
        if (plain && Layouts.TryGetValue(key, out CachedLayout cached))
        {
            yShift = cached.YShift;
            return cached.Layout;
        }
        SlotState state = State(slot);
        LegacyFontSizing sizing = new()
        {
            // Sizes are already in real pixels, so one "virtual unit" here is one pixel.
            Sizes = state.RealSizes,
            PixelsX = 1,
            PixelsY = 1,
            Snapping = _snapping,
            Metrics = state.Faces.Length > 0 ? state.Metrics : null,
            Scale = state.Scale,
            VerticalOffset = state.VerticalOffset,
        };
        LegacyTextLayout layout = LegacyTextLayout.For(size, size, fontScaleX, fontScaleY, sizing, out yShift);
        if (plain)
        {
            if (Layouts.Count > 4096) Layouts.Clear();
            Layouts[key] = new CachedLayout(layout, yShift);
        }
        return layout;
    }

    private static LegacyGlyphAtlas.Glyph GlyphFor(int slot, int pixelSize, int rune)
    {
        long key = ((long)(slot & 0xFF) << 40) | ((long)(pixelSize & 0x3FF) << 24) | (uint)(rune & 0x1FFFFF);
        if (Glyphs.TryGetValue(key, out LegacyGlyphAtlas.Glyph known)) return known;
        FontFile[] faces = State(slot).Faces;
        LegacyGlyphAtlas.Glyph glyph = default;
        if (LegacyGlyphAtlas.FaceFor(faces, rune) is { } face) glyph = GlyphAtlas.Get(face, pixelSize, rune);
        // The atlas starts over when it is full; every picture handed out before that is stale.
        if (GlyphAtlas.GlyphCount < _atlasGlyphs || Glyphs.Count > 32768) Glyphs.Clear();
        _atlasGlyphs = GlyphAtlas.GlyphCount;
        Glyphs[key] = glyph;
        return glyph;
    }

    /// <summary>
    /// DrawQ_TextWidth: the width in pixels of <paramref name="text"/> at cell height <paramref name="size"/>
    /// pixels - the sum of the glyphs' hinted advances in the font map <see cref="Draw"/> would use, so a
    /// string measured to fit does fit. Colour codes are not interpreted (the caller strips them).
    /// </summary>
    public static float Measure(int slot, string? text, float size, float fontScaleX = 1)
    {
        if (string.IsNullOrEmpty(text) || !(size > 0)) return 0;
        LegacyTextLayout layout = Layout(slot, size, out _, fontScaleX);
        if (layout.ScaleX == 0) return 0;
        float raster = 0;
        foreach (Rune rune in text.EnumerateRunes())
            if (rune.Value >= ' ') raster += GlyphFor(slot, layout.PixelSize, rune.Value).Advance;
        return layout.Width(raster);
    }

    /// <summary>The height of a line of text of cell height <paramref name="size"/>: the cell DarkPlaces
    /// actually draws after snapping to a font map.</summary>
    public static float CellHeight(int slot, float size)
    {
        if (!(size > 0)) return 0;
        LegacyTextLayout layout = Layout(slot, size, out _);
        return layout.Cell * MathF.Abs(layout.ScaleY);
    }

    /// <summary>
    /// DrawQ_String: draws <paramref name="text"/> with the top-left of its first character cell at
    /// <paramref name="position"/> (in <paramref name="target"/>'s own coordinates, which are taken to be
    /// pixels), cell height <paramref name="size"/> pixels. <paramref name="origin"/> is where the canvas
    /// item's own origin sits on screen, so the string can be snapped to a whole SCREEN pixel
    /// (snap_to_pixel_x/y) rather than a whole pixel of the item. Colour codes are not interpreted.
    /// Returns the width drawn, in pixels.
    /// </summary>
    public static float Draw(CanvasItem target, int slot, Vector2 position, string? text, float size, Color color,
        Vector2 origin = default, float fontScaleX = 1, float fontScaleY = 1)
    {
        if (string.IsNullOrEmpty(text) || !(size > 0)) return 0;
        LegacyTextLayout layout = Layout(slot, size, out float yShift, fontScaleX, fontScaleY);
        if (layout.ScaleX == 0 || layout.ScaleY == 0) return 0;

        // "startx = snap_to_pixel_x(startx, 0.4); starty = snap_to_pixel_y(starty, 0.4)"
        float x = LegacyTextLayout.SnapX(origin.X + position.X) - origin.X;
        float y = LegacyTextLayout.SnapY(origin.Y + position.Y + yShift, 0.4f) - origin.Y;
        float sx = layout.ScaleX, sy = layout.ScaleY;
        float baseline = y + layout.Baseline * sy;
        int pixelSize = layout.PixelSize;
        bool exact = sx == 1 && sy == 1;

        LegacyColor asked = new(color.R, color.G, color.B, color.A);
        // "for (shadow = r_textshadow.value != 0 && basealpha > 0; shadow >= 0; shadow--)": with r_textshadow
        // set, the whole string is drawn first in black, that many REAL pixels right and down. Xonotic leaves
        // it at 0; its text is set off by the outline baked into each glyph instead.
        for (int pass = _look.HasShadow(color.A) ? 1 : 0; pass >= 0; pass--)
        {
            LegacyColor c = pass == 1 ? _look.ShadowColor(asked) : _look.Color(asked);
            Color modulate = new(c.R, c.G, c.B, c.A);
            float drop = pass == 1 ? _look.Shadow : 0;
            float pen = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (rune.Value < ' ') continue;
                LegacyGlyphAtlas.Glyph glyph = GlyphFor(slot, pixelSize, rune.Value);
                if (glyph.Texture is not null)
                {
                    Rect2 rect = exact
                        ? new Rect2(x + pen + glyph.Offset.X + drop, baseline + glyph.Offset.Y + drop, glyph.Region.Size)
                        : new Rect2(x + (pen + glyph.Offset.X) * sx + drop, baseline + glyph.Offset.Y * sy + drop, glyph.Region.Size.X * sx, glyph.Region.Size.Y * sy);
                    target.DrawTextureRectRegion(glyph.Texture, rect, glyph.Region, modulate);
                }
                pen += glyph.Advance;
            }
            if (pass == 0)
            {
                GlyphAtlas.Flush();
                return pen * MathF.Abs(sx);
            }
        }
        return 0;
    }
}
