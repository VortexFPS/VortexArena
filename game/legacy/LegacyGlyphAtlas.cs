// Port of Base/darkplaces/ft2.c Font_LoadMap as far as a glyph's picture and advance go, and of the glyph
// loop of gl_draw.c DrawQ_String_Scale: the font map DarkPlaces draws text from, rebuilt over Godot's
// FreeType rasteriser.
using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Legacy.Presentation;

namespace VortexArena.Game.Legacy;

/// <summary>
/// DarkPlaces text, glyph by glyph: each glyph rasterised by Godot at the size and hinting DarkPlaces asks
/// FreeType for, put through DarkPlaces' own post-processing (<see cref="LegacyGlyphPostprocess"/> - the
/// baked outline and blur that give Xonotic's text its dark halo) and kept in an atlas, then drawn at the
/// pen with the pen moved by the glyph's hinted advance.
///
/// Nothing here knows about the legacy client: it takes Godot fonts, a canvas item, a position, text, a
/// size and a colour, so the native HUD can draw with the same look. Colours are passed through
/// <see cref="LegacyTextLook.Color"/> by the caller, which also owns colour codes and the shadow pass.
///
/// Why not Font.DrawString with an outline: a shaper measures with unhinted advances and applies
/// fractional kerning (lines came out 1.5% narrower than DarkPlaces' and wrapped at other words), and
/// Godot's outline is a hard stroke where DarkPlaces' is a dilation by a soft disc followed by a blur.
/// </summary>
public sealed class LegacyGlyphAtlas
{
    private const int PageSize = 512, MaxPages = 24, MaxGlyphs = 16384;

    /// <summary>One glyph ready to draw. <see cref="Texture"/> is null for a glyph with no picture (a space).</summary>
    public readonly record struct Glyph(Texture2D? Texture, Rect2 Region, Vector2 Offset, float Advance);

    private sealed class Page
    {
        public readonly Image Image = Image.CreateEmpty(PageSize, PageSize, false, Image.Format.Rgba8);
        public ImageTexture? Texture;
        public int X = 1, Y = 1, RowHeight;
        public bool Dirty;
    }

    private readonly Dictionary<(ulong Face, int Size, int Rune), Glyph> _glyphs = new();
    private readonly List<Page> _pages = new();
    private LegacyGlyphPostprocess _postprocess = new(0, 0);
    private bool _dirty;

    /// <summary>How many glyphs have been rasterised since the atlas was last emptied, and into how many pages.</summary>
    public int GlyphCount => _glyphs.Count;
    public int PageCount => _pages.Count;

    /// <summary>
    /// r_font_postprocess_outline, _blur, _shadow_x, _shadow_y, _shadow_z. DarkPlaces reads them when a
    /// font is first loaded and keeps them until r_restart; here a change empties the atlas, so the next
    /// frame shows it.
    /// </summary>
    public void SetPostprocess(float outline, float blur, float shadowX = 0, float shadowY = 0, float shadowZ = 0)
    {
        LegacyGlyphPostprocess wanted = new(outline, blur, shadowX, shadowY, shadowZ);
        if (wanted.Outline == _postprocess.Outline && wanted.Blur == _postprocess.Blur && wanted.ShadowX == _postprocess.ShadowX
            && wanted.ShadowY == _postprocess.ShadowY && wanted.ShadowZ == _postprocess.ShadowZ) return;
        _postprocess = wanted;
        Clear();
    }

    /// <summary>Forgets every glyph (a font was reloaded, the hinting changed).</summary>
    public void Clear()
    {
        _glyphs.Clear();
        _pages.Clear();
        _dirty = false;
    }

    /// <summary>
    /// The font file of <paramref name="faces"/> that draws a character: the first that has it, the first
    /// of all if none does (loadfont's "face,fallback,..."; DarkPlaces looks through them per glyph).
    /// </summary>
    public static FontFile? FaceFor(IReadOnlyList<FontFile> faces, int rune)
    {
        if (faces.Count == 0) return null;
        foreach (FontFile face in faces)
            if (face.HasChar(rune)) return face;
        return faces[0];
    }

    /// <summary>
    /// A character of a font file at a size in pixels. The advance is "glyph->advance.x / 64" of the glyph
    /// as FreeType loaded it with the file's hinting - a whole number of pixels when hinted.
    /// </summary>
    public Glyph Get(FontFile face, int size, int rune)
    {
        (ulong, int, int) key = (face.GetInstanceId(), size, rune);
        if (_glyphs.TryGetValue(key, out Glyph known)) return known;
        Glyph glyph = Rasterise(face, Math.Clamp(size, 1, LegacyTextLayout.MaxPixelSize), rune);
        if (_glyphs.Count >= MaxGlyphs) Clear();
        _glyphs[key] = glyph;
        return glyph;
    }

    private Glyph Rasterise(FontFile face, int size, int rune)
    {
        Godot.Collections.Array<Rid> rids = face.GetRids();
        if (rids.Count == 0) return default;
        Rid rid = rids[0];
        TextServer server = TextServerManager.GetPrimaryInterface();
        Vector2I sized = new(size, 0);
        long index = server.FontGetGlyphIndex(rid, size, rune, 0);
        float advance = server.FontGetGlyphAdvance(rid, size, index).X;
        if (!float.IsFinite(advance) || advance < 0) advance = 0;
        if (index == 0) return new Glyph(null, default, default, advance);
        server.FontRenderGlyph(rid, sized, index);

        Rect2 uv = server.FontGetGlyphUVRect(rid, sized, index);
        Vector2 offset = server.FontGetGlyphOffset(rid, sized, index);
        int width = (int)MathF.Round(uv.Size.X), height = (int)MathF.Round(uv.Size.Y);
        long textureIndex = server.FontGetGlyphTextureIdx(rid, sized, index);
        if (width <= 0 || height <= 0 || width > 1024 || height > 1024 || textureIndex < 0) return new Glyph(null, default, default, advance);
        Image? source = server.FontGetTextureImage(rid, sized, textureIndex);
        if (source is null || source.IsEmpty()) return new Glyph(null, default, default, advance);
        int left = (int)MathF.Round(uv.Position.X), top = (int)MathF.Round(uv.Position.Y);
        if (left < 0 || top < 0 || left + width > source.GetWidth() || top + height > source.GetHeight()) return new Glyph(null, default, default, advance);

        // The glyph's coverage: Godot keeps a grey glyph as white with the coverage in alpha.
        byte[] coverage = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                coverage[x + y * width] = (byte)Math.Clamp((int)MathF.Round(source.GetPixel(left + x, top + y).A * 255f), 0, 255);

        byte[] pixels = _postprocess.Apply(coverage, width, height, out int outWidth, out int outHeight);
        int padLeft = _postprocess.Identity ? 0 : _postprocess.PadLeft, padTop = _postprocess.Identity ? 0 : _postprocess.PadTop;
        if (outWidth + 2 > PageSize || outHeight + 2 > PageSize) return new Glyph(null, default, default, advance);

        Page page = PageWithRoom(outWidth, outHeight);
        Image glyphImage = Image.CreateFromData(outWidth, outHeight, false, Image.Format.Rgba8, pixels);
        page.Image.BlitRect(glyphImage, new Rect2I(0, 0, outWidth, outHeight), new Vector2I(page.X, page.Y));
        Rect2 region = new(page.X, page.Y, outWidth, outHeight);
        page.X += outWidth + 1;
        page.RowHeight = Math.Max(page.RowHeight, outHeight);
        page.Dirty = true;
        _dirty = true;
        page.Texture ??= ImageTexture.CreateFromImage(page.Image);
        return new Glyph(page.Texture, region, offset - new Vector2(padLeft, padTop), advance);
    }

    private Page PageWithRoom(int width, int height)
    {
        Page? page = _pages.Count > 0 ? _pages[^1] : null;
        if (page is not null && page.X + width + 1 > PageSize)
        {
            page.X = 1;
            page.Y += page.RowHeight + 1;
            page.RowHeight = 0;
        }
        if (page is null || page.Y + height + 1 > PageSize)
        {
            // Out of pages: start over. Every glyph in use is rasterised again as it is next drawn.
            if (_pages.Count >= MaxPages) Clear();
            page = new Page();
            _pages.Add(page);
        }
        return page;
    }

    /// <summary>Uploads the pages that gained glyphs. Called by <see cref="DrawText"/>; cheap when nothing changed.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        foreach (Page page in _pages)
        {
            if (!page.Dirty || page.Texture is null) continue;
            page.Texture.Update(page.Image);
            page.Dirty = false;
        }
    }

    /// <summary>The width of text at a size in pixels: the sum of its glyphs' advances, as DrawQ_TextWidth walks it.</summary>
    public float Measure(IReadOnlyList<FontFile> faces, string text, int size)
    {
        float width = 0;
        foreach (Rune rune in text.EnumerateRunes())
            if (FaceFor(faces, rune.Value) is { } face) width += Get(face, size, rune.Value).Advance;
        return width;
    }

    /// <summary>
    /// Draws text a character at a time with its baseline at <paramref name="baseline"/>.Y and its pen
    /// starting at <paramref name="baseline"/>.X, in the canvas item's current transform - the caller
    /// scales that when the text is not drawn at the rasterised size. Returns the pen's x afterwards.
    /// </summary>
    public float DrawText(CanvasItem target, IReadOnlyList<FontFile> faces, Vector2 baseline, string text, int size, Color color)
    {
        float pen = baseline.X;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (FaceFor(faces, rune.Value) is not { } face) continue;
            Glyph glyph = Get(face, size, rune.Value);
            if (glyph.Texture is not null)
                target.DrawTextureRectRegion(glyph.Texture, new Rect2(new Vector2(pen, baseline.Y) + glyph.Offset, glyph.Region.Size), glyph.Region, color);
            pen += glyph.Advance;
        }
        Flush();
        return pen;
    }
}
