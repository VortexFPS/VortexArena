// Port of Base/darkplaces/ft2.c Font_LoadMap (a font map: every glyph of a font at one size, rasterised once,
// post-processed by Font_Postprocess and kept as a picture with its hinted advance), expressed as a Godot
// FontFile so that stock Controls (Label, Button, LineEdit, RichTextLabel, ItemList...) draw DarkPlaces' glyphs.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Legacy.Presentation;

namespace VortexArena.Game.Text;

/// <summary>
/// DarkPlaces font maps as Godot fonts. A stock Godot control shapes and rasterises its own text, which is why
/// the native menu and console could not look like Xonotic's: no baked outline, unhinted advances, kerning.
/// This builds, for one font slot at one DarkPlaces size, a <see cref="FontFile"/> with NO font data - only
/// pre-rendered glyph pictures (the same pictures <see cref="DpText"/> draws: FreeType at DarkPlaces' size and
/// hinting, then the outline and blur of <c>r_font_postprocess_*</c>) and DarkPlaces' advances. Godot then lays
/// text out one glyph after another with exactly those advances, as DrawQ_String does, and every control that
/// is given the font - through a theme or an override - draws Xonotic's text without knowing it.
///
/// The font is a fixed-size one (<see cref="FontFile.FixedSize"/>) that does not scale: whatever size a control
/// asks for, it gets the font map, which is what r_font_size_snapping does to every size near a map's.
///
/// Characters outside the baked set fall back to the slot's own font files (plain glyphs, no outline).
/// </summary>
public static class DpBitmapFont
{
    private const int PageSize = 1024;

    private sealed class Entry
    {
        public FontFile Font = null!;
        public int Slot;
        public float VirtualSize;
        public int Key;
        /// <summary>True: the control lives in the menu's 1080-high design space (one unit = window height /
        /// 1080 pixels). False: its units are pixels.</summary>
        public bool MenuSpace;
        public int Generation = -1;
    }

    private static readonly List<Entry> Entries = new();
    private static bool _hooked, _refreshQueued;

    /// <summary>The menu's design height (MenuRoot.DesignHeight): every menu screen is laid out 1080 units high
    /// and scaled to the window.</summary>
    public const float MenuDesignHeight = 1080f;

    /// <summary>The code points baked into every font map: what Xolonium covers of Latin, Greek and Cyrillic,
    /// punctuation, arrows and the symbols menus use. Anything else is drawn by the fallback fonts.</summary>
    private static readonly (int From, int To)[] Ranges =
    {
        (0x20, 0x7E), (0xA0, 0x17F), (0x18F, 0x192), (0x1A0, 0x1B0), (0x218, 0x21B), (0x2C6, 0x2DD),
        (0x384, 0x3CE), (0x400, 0x45F), (0x490, 0x491), (0x2010, 0x2027), (0x2030, 0x203A), (0x2044, 0x2044),
        (0x20AC, 0x20AC), (0x2116, 0x2122), (0x2190, 0x2199), (0x21B5, 0x21B5), (0x2212, 0x2212), (0x221E, 0x221E),
        (0x25A0, 0x25CF), (0x2713, 0x2717),
    };

    /// <summary>
    /// A font for a control in the MENU's design space: slot <paramref name="slot"/> at
    /// <paramref name="virtualSize"/> DarkPlaces virtual units (SKINFONTSIZE_NORMAL is 12, _TITLE 16), to be
    /// asked for at font size <paramref name="key"/>. Rebuilt in place when the window or a text cvar changes.
    /// </summary>
    public static FontFile Menu(int slot, float virtualSize, int key) => Get(slot, virtualSize, key, true);

    /// <summary>A font for a control whose units are pixels (the console): slot at a DarkPlaces virtual size,
    /// asked for at font size <paramref name="key"/>.</summary>
    public static FontFile Screen(int slot, float virtualSize, int key) => Get(slot, virtualSize, key, false);

    private static FontFile Get(int slot, float virtualSize, int key, bool menuSpace)
    {
        foreach (Entry known in Entries)
            if (known.Slot == slot && known.VirtualSize == virtualSize && known.Key == key && known.MenuSpace == menuSpace)
            {
                if (known.Generation != DpText.Generation) Bake(known);
                return known.Font;
            }
        Entry entry = new()
        {
            Slot = slot, VirtualSize = virtualSize, Key = Math.Max(1, key), MenuSpace = menuSpace,
            Font = new FontFile
            {
                ResourceName = $"dp-font-map slot {slot} size {virtualSize}",
                FixedSize = Math.Max(1, key),
                FixedSizeScaleMode = TextServer.FixedSizeScaleMode.Disable,
                SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
                AllowSystemFallback = false,
                GenerateMipmaps = false,
            },
        };
        Entries.Add(entry);
        Bake(entry);
        if (!_hooked && Godot.Engine.GetMainLoop() is SceneTree tree)
        {
            // Font maps are made for the window's height; DpText marks itself stale on a resize or a cvar
            // change, and the fonts follow a moment later (not on every event of a window being dragged).
            tree.Root.SizeChanged += QueueRefresh;
            _hooked = true;
        }
        return entry.Font;
    }

    /// <summary>Re-bakes every font whose font maps are out of date. Cheap when nothing changed.</summary>
    public static void Refresh()
    {
        _refreshQueued = false;
        _ = DpText.Look;   // brings DpText up to date (a pending resize or cvar change bumps Generation)
        foreach (Entry entry in Entries)
            if (entry.Generation != DpText.Generation) Bake(entry);
    }

    /// <summary>Asks for a <see cref="Refresh"/> shortly: after a window resize, or by whoever changed a text cvar.</summary>
    public static void QueueRefresh()
    {
        if (_refreshQueued || Godot.Engine.GetMainLoop() is not SceneTree tree) return;
        _refreshQueued = true;
        tree.CreateTimer(0.2, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += Refresh;
    }

    private static void Bake(Entry entry)
    {
        _ = DpText.Look;
        entry.Generation = DpText.Generation;
        FontFile font = entry.Font;
        font.ClearCache();
        font.FixedSize = entry.Key;

        Vector2 window = Godot.Engine.GetMainLoop() is SceneTree tree ? tree.Root.Size : new Vector2(1280, 720);
        // Pixels per unit of the control's own space.
        float unit = entry.MenuSpace && window.Y >= 1 ? window.Y / MenuDesignHeight : 1f;
        float cell = entry.VirtualSize * DpText.PixelsPerUnit;
        LegacyTextLayout layout = DpText.Layout(entry.Slot, cell, out _);
        IReadOnlyList<FontFile> faces = DpText.Faces(entry.Slot);
        if (faces.Count == 0 || layout.ScaleX == 0 || layout.ScaleY == 0) return;
        // Real pixels per rasterised pixel (1 when the size snapped to a font map), then to the control's units.
        float sx = MathF.Abs(layout.ScaleX) / unit, sy = MathF.Abs(layout.ScaleY) / unit;
        int raster = layout.PixelSize;

        Vector2I sizeKey = new(entry.Key, 0);
        // "ftbase_y = dh * (4.5/6.0)": the baseline is three quarters down the cell; the rest is below it.
        font.SetCacheAscent(0, entry.Key, layout.Baseline * sy);
        font.SetCacheDescent(0, entry.Key, (layout.Cell - layout.Baseline) * sy);
        font.SetCacheUnderlinePosition(0, entry.Key, 2f * sy);
        font.SetCacheUnderlineThickness(0, entry.Key, MathF.Max(1f, raster / 14f) * sy);
        font.SetCacheScale(0, entry.Key, 1f);

        LegacyGlyphPostprocess postprocess = DpText.Postprocess();
        int padLeft = postprocess.Identity ? 0 : postprocess.PadLeft, padTop = postprocess.Identity ? 0 : postprocess.PadTop;
        TextServer server = TextServerManager.GetPrimaryInterface();
        Vector2I rasterKey = new(raster, 0);

        // Pass 1: have FreeType render every glyph of the set (each from the first face that has it).
        List<(int Rune, Rid Face, long Index)> wanted = new();
        Dictionary<Rid, bool> rids = new();
        foreach ((int from, int to) in Ranges)
            for (int rune = from; rune <= to; rune++)
            {
                FontFile? face = null;
                foreach (FontFile candidate in faces)
                    if (candidate.HasChar(rune)) { face = candidate; break; }
                if (face is null) continue;
                Godot.Collections.Array<Rid> faceRids = face.GetRids();
                if (faceRids.Count == 0) continue;
                Rid rid = faceRids[0];
                long index = server.FontGetGlyphIndex(rid, raster, rune, 0);
                if (index == 0) continue;
                server.FontRenderGlyph(rid, rasterKey, index);
                wanted.Add((rune, rid, index));
                rids[rid] = true;
            }

        // Pass 2: read each of FreeType's texture pages once, then cut the glyphs out of them.
        Dictionary<(Rid, long), Image?> sources = new();
        List<Image> pages = new();
        Image page = Image.CreateEmpty(PageSize, PageSize, false, Image.Format.Rgba8);
        int penX = 1, penY = 1, rowHeight = 0;
        foreach ((int rune, Rid rid, long index) in wanted)
        {
            float advance = server.FontGetGlyphAdvance(rid, raster, index).X;
            if (!float.IsFinite(advance) || advance < 0) advance = 0;
            font.SetGlyphAdvance(0, entry.Key, rune, new Vector2(advance * sx, 0));

            Rect2 uv = server.FontGetGlyphUVRect(rid, rasterKey, index);
            Vector2 offset = server.FontGetGlyphOffset(rid, rasterKey, index);
            int width = (int)MathF.Round(uv.Size.X), height = (int)MathF.Round(uv.Size.Y);
            long textureIndex = server.FontGetGlyphTextureIdx(rid, rasterKey, index);
            if (width <= 0 || height <= 0 || width > 512 || height > 512 || textureIndex < 0) continue;   // a space
            if (!sources.TryGetValue((rid, textureIndex), out Image? source))
            {
                source = server.FontGetTextureImage(rid, rasterKey, textureIndex);
                sources[(rid, textureIndex)] = source;
            }
            if (source is null || source.IsEmpty()) continue;
            int left = (int)MathF.Round(uv.Position.X), top = (int)MathF.Round(uv.Position.Y);
            if (left < 0 || top < 0 || left + width > source.GetWidth() || top + height > source.GetHeight()) continue;

            byte[] coverage = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    coverage[x + y * width] = (byte)Math.Clamp((int)MathF.Round(source.GetPixel(left + x, top + y).A * 255f), 0, 255);
            byte[] pixels = postprocess.Apply(coverage, width, height, out int outWidth, out int outHeight);
            if (outWidth + 2 > PageSize || outHeight + 2 > PageSize) continue;

            if (penX + outWidth + 1 > PageSize)
            {
                penX = 1;
                penY += rowHeight + 1;
                rowHeight = 0;
            }
            if (penY + outHeight + 1 > PageSize)
            {
                pages.Add(page);
                page = Image.CreateEmpty(PageSize, PageSize, false, Image.Format.Rgba8);
                penX = penY = 1;
                rowHeight = 0;
            }
            page.BlitRect(Image.CreateFromData(outWidth, outHeight, false, Image.Format.Rgba8, pixels),
                new Rect2I(0, 0, outWidth, outHeight), new Vector2I(penX, penY));
            font.SetGlyphTextureIdx(0, sizeKey, rune, pages.Count);
            font.SetGlyphUVRect(0, sizeKey, rune, new Rect2(penX, penY, outWidth, outHeight));
            font.SetGlyphSize(0, sizeKey, rune, new Vector2(outWidth * sx, outHeight * sy));
            font.SetGlyphOffset(0, sizeKey, rune, new Vector2((offset.X - padLeft) * sx, (offset.Y - padTop) * sy));
            penX += outWidth + 1;
            rowHeight = Math.Max(rowHeight, outHeight);
        }
        pages.Add(page);
        for (int i = 0; i < pages.Count; i++) font.SetTextureImage(0, sizeKey, i, pages[i]);

        // ft2.c Font_LoadSize "load the default kerning vector": the pairs of the first 256 characters, looked up
        // in the slot's main file and snapped to whole pixels of the map (most are nothing at small sizes). Godot
        // then moves its pen by them as DrawQ_String does. A bitmap font's glyph index is the character itself.
        font.ClearKerningMap(0, entry.Key);
        if (DpText.KerningEnabled && faces.Count > 0)
        {
            FontFile main = faces[0];
            List<int> kerned = new();
            foreach ((int rune, Rid _, long _) in wanted)
                if (rune < 256 && main.HasChar(rune)) kerned.Add(rune);
            foreach (int left in kerned)
                foreach (int right in kerned)
                {
                    float kerning = DpText.KerningOfMap(main, raster, left, right);
                    if (kerning != 0) font.SetKerning(0, entry.Key, new Vector2I(left, right), new Vector2(kerning * sx, 0));
                }
        }

        // Anything not baked: the slot's own files, plain. (Unifont's sixty thousand glyphs are not pre-rendered.)
        Godot.Collections.Array<Font> fallbacks = new();
        foreach (FontFile face in faces) fallbacks.Add(face);
        font.Fallbacks = fallbacks;
    }
}
