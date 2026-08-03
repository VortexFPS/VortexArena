using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace VortexArena.Game.Client;

/// <summary>
/// The Darkplaces particle-texture atlas (<c>particles/particlefont.tga</c> + <c>particlefont.txt</c>) — the
/// source of every particle/decal SPRITE the effect system draws. DP references particle textures by an
/// integer index (the effectinfo <c>tex</c> / <c>staintex</c> ranges): smoke 0-7, bullet/scorch decals 8-15,
/// blood decals 16-23, blood particles 24-31, rain/square/etc. 32-47, fire/flame 48-55, debris/beam/bubble
/// 56-63, and the beam strips 200-205. Each index maps to a sub-rectangle of the 2048² atlas; the <c>.txt</c>
/// sidecar lists those rects in normalized UV (<c>index left top right bottom</c>, the DP particlefont format).
///
/// This loads the atlas once (via the host's VFS texture loader) and the UV table (via the VFS text loader),
/// then hands out a cached <see cref="ImageTexture"/> per index, cropped out of the atlas. The effect system
/// textures its billboard quads and projected decals with these instead of flat solid-color discs, which is
/// what makes an explosion read as a real fireball/smoke/spark burst and a scorch/blood mark read as the real
/// Xonotic decal. A failed load (no content mounted) leaves <see cref="Loaded"/> false and callers fall back
/// to the generated solid quads/discs, so the game still runs with no atlas.
/// </summary>
public sealed class ParticleFont
{
    // index -> pixel rect inside the atlas (x, y, w, h).
    private readonly Dictionary<int, Rect2I> _cells = new();
    private Image? _atlas;
    private ImageTexture? _atlasTex;
    private readonly Dictionary<int, ImageTexture> _cellTex = new();
    private readonly Dictionary<int, ImageTexture> _decalTex = new();
    private readonly Dictionary<int, ImageTexture?> _stripTex = new();

    /// <summary>True once both the atlas image and at least one UV cell parsed.</summary>
    public bool Loaded => _atlas is not null && _cells.Count > 0;

    /// <summary>
    /// The WHOLE source atlas as a single <see cref="ImageTexture"/> (the already-loaded
    /// <c>particlefont</c> image), built lazily on first access and cached. Paired with
    /// <see cref="TryGetCellUv"/>, a consumer can draw any number of cells by sampling this ONE texture at
    /// each cell's UV rect — exactly how DP keeps particlefont resident and samples cells by texcoord —
    /// instead of cropping every cell into a separate texture and re-packing them into a duplicate atlas
    /// (what <see cref="Cell"/> + a manual blit pass costs the faithful MultiMesh renderer and the modern
    /// GPU backend today). Null until the font is <see cref="Loaded"/>.
    /// </summary>
    public Texture2D? AtlasTexture
    {
        get
        {
            if (_atlasTex is null && _atlas is not null)
            {
                try { _atlasTex = ImageTexture.CreateFromImage(_atlas); }
                catch { _atlasTex = null; }
            }
            return _atlasTex;
        }
    }

    /// <summary>
    /// The NORMALIZED UV rect (x, y, w, h in 0..1) of atlas <paramref name="index"/> within
    /// <see cref="AtlasTexture"/>, derived from the cell's pixel rect and the atlas size. Returns false (with
    /// <paramref name="uv"/> = default) when the font isn't loaded or the index has no cell. Lets a consumer
    /// sample the shared atlas directly for cell <paramref name="index"/> instead of cropping it out via
    /// <see cref="Cell"/>.
    /// </summary>
    public bool TryGetCellUv(int index, out Rect2 uv)
    {
        uv = default;
        if (_atlas is null || !_cells.TryGetValue(index, out Rect2I rect) || rect.Size.X <= 0)
            return false;
        float w = _atlas.GetWidth(), h = _atlas.GetHeight();
        if (w <= 0f || h <= 0f)
            return false;
        uv = new Rect2(rect.Position.X / w, rect.Position.Y / h, rect.Size.X / w, rect.Size.Y / h);
        return true;
    }

    /// <summary>Every atlas index that parsed a cell — for consumers that enumerate the atlas (e.g. build a
    /// contiguous slot table over the shared <see cref="AtlasTexture"/>). Backed by the live cell table.</summary>
    public IReadOnlyCollection<int> CellIndices => _cells.Keys;

    /// <summary>Default VFS paths for the atlas image (extension-agnostic) and its UV table.</summary>
    public const string AtlasVPath = "particles/particlefont";
    public const string TableVPath = "particles/particlefont.txt";

    /// <summary>
    /// Build the atlas from the mounted content. <paramref name="texLoader"/> resolves the atlas image
    /// (e.g. <c>AssetSystem.LoadTexture</c>); <paramref name="textLoader"/> reads the UV table text
    /// (e.g. <c>VirtualFileSystem.ReadText</c>). Returns a loaded font, or a font with <see cref="Loaded"/>
    /// false if either source is missing/unreadable (never throws — the caller falls back to solid quads).
    /// </summary>
    public static ParticleFont Load(Func<string, Texture2D?>? texLoader, Func<string, string?>? textLoader)
    {
        var font = new ParticleFont();
        if (texLoader is null || textLoader is null)
            return font;

        // 1) the UV table — the same normalized rects DP loads in CL_Particles_Init.
        try
        {
            string? table = textLoader(TableVPath);
            if (!string.IsNullOrEmpty(table))
                font.ParseTable(table);
        }
        catch { /* leave _cells empty -> not Loaded */ }

        if (font._cells.Count == 0)
            return font;

        // 2) the atlas image. LoadTexture returns an ImageTexture; pull its Image for cropping.
        try
        {
            Texture2D? tex = texLoader(AtlasVPath);
            Image? img = tex?.GetImage();
            if (img is not null)
            {
                // Cropping needs an uncompressed format; decompress if the source was a compressed DDS.
                if (img.IsCompressed())
                    img.Decompress();
                font._atlas = img;
                // The UV table is normalized; resolve to pixels now that we know the atlas size.
                font.ResolveCellPixels(img.GetWidth(), img.GetHeight());
            }
        }
        catch { font._atlas = null; }

        return font;
    }

    // Normalized rects parsed from the .txt, kept until we know the atlas pixel size.
    private readonly Dictionary<int, (float L, float T, float R, float B)> _uv = new();

    private void ParseTable(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '/')
                continue;
            string[] tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 5)
                continue;
            if (!int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                continue;
            if (TryF(tok[1], out float l) && TryF(tok[2], out float t) &&
                TryF(tok[3], out float r) && TryF(tok[4], out float b))
            {
                _uv[idx] = (l, t, r, b);
                _cells[idx] = default; // placeholder; filled by ResolveCellPixels
            }
        }
    }

    private void ResolveCellPixels(int w, int h)
    {
        foreach (KeyValuePair<int, (float L, float T, float R, float B)> kv in _uv)
        {
            (float l, float t, float r, float b) = kv.Value;
            int x = Mathf.Clamp((int)MathF.Round(l * w), 0, w - 1);
            int y = Mathf.Clamp((int)MathF.Round(t * h), 0, h - 1);
            int cw = Mathf.Clamp((int)MathF.Round((r - l) * w), 1, w - x);
            int ch = Mathf.Clamp((int)MathF.Round((b - t) * h), 1, h - y);
            _cells[kv.Key] = new Rect2I(x, y, cw, ch);
        }
    }

    private static bool TryF(string s, out float v)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>
    /// The sprite texture for atlas <paramref name="index"/>, cropped from the atlas and cached. Returns null
    /// when the font isn't loaded or the index has no cell (caller falls back to a solid quad/disc).
    /// </summary>
    /// <summary>
    /// (zero-hitch 2026-08-03) Atlas UV rect of cell <paramref name="index"/>, normalized 0..1 — for renderers
    /// that draw MANY cells in ONE mesh sampling <see cref="AtlasTexture"/> directly (the merged decal-splat
    /// mesh) instead of holding a cropped per-cell texture each. False when the atlas/cell is missing.
    /// </summary>
    public bool CellUvRect(int index, out Rect2 uvRect)
    {
        uvRect = default;
        if (_atlas is null || !_cells.TryGetValue(index, out Rect2I rect) || rect.Size.X <= 0)
            return false;
        Vector2 atlasSize = new(_atlas.GetWidth(), _atlas.GetHeight());
        if (atlasSize.X <= 0 || atlasSize.Y <= 0)
            return false;
        uvRect = new Rect2(rect.Position.X / atlasSize.X, rect.Position.Y / atlasSize.Y,
                           rect.Size.X / atlasSize.X, rect.Size.Y / atlasSize.Y);
        return true;
    }

    public ImageTexture? Cell(int index)
    {
        if (_atlas is null || !_cells.TryGetValue(index, out Rect2I rect) || rect.Size.X <= 0)
            return null;
        if (_cellTex.TryGetValue(index, out ImageTexture? cached))
            return cached;

        ImageTexture? tex = null;
        try
        {
            Image region = _atlas.GetRegion(rect);
            tex = ImageTexture.CreateFromImage(region);
        }
        catch { tex = null; }

        _cellTex[index] = tex!; // cache even null to avoid re-cropping a bad cell
        return tex;
    }

    /// <summary>
    /// The DECAL form of atlas <paramref name="index"/>: the same sprite reworked for a Godot <see cref="Decal"/>
    /// node. DP's bullet/scorch/blood decal cells (8-23) are stored fully OPAQUE with the mark encoded in RGB
    /// luminance (for INVMOD blending) — projected straight onto a Decal they'd paint an opaque dark SQUARE.
    /// So we rebuild the cell as alpha = luminance (the mark becomes the coverage) over white RGB (so the
    /// Decal's modulate fully controls the scorch/blood tint). Cells that already carry real alpha keep it
    /// (combined with luminance), so this is safe for any index. Cached separately from the particle form.
    /// </summary>
    public ImageTexture? DecalCell(int index)
    {
        if (_atlas is null || !_cells.TryGetValue(index, out Rect2I rect) || rect.Size.X <= 0)
            return null;
        if (_decalTex.TryGetValue(index, out ImageTexture? cached))
            return cached;

        ImageTexture? tex = null;
        try
        {
            Image region = _atlas.GetRegion(rect);
            if (region.GetFormat() != Image.Format.Rgba8)
                region.Convert(Image.Format.Rgba8);
            int w = region.GetWidth(), h = region.GetHeight();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color p = region.GetPixel(x, y);
                    float lum = p.R * 0.299f + p.G * 0.587f + p.B * 0.114f;
                    // Coverage = mark luminance gated by any native alpha. White RGB lets modulate tint it.
                    region.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp(lum * p.A, 0f, 1f)));
                }
            tex = ImageTexture.CreateFromImage(region);
        }
        catch { tex = null; }

        _decalTex[index] = tex!;
        return tex;
    }

    /// <summary>
    /// A horizontal sprite strip packing every atlas cell in the half-open [<paramref name="tex0"/>,
    /// <paramref name="tex1"/>) range side-by-side. Used with <c>ParticlesAnimHFrames</c> and
    /// <c>ParticleProcessMaterial.AnimOffset</c> so each particle in a burst gets a randomly-selected
    /// cell (a different smoke wisp, fire blob, spark dot…). Cached by range. Returns null if the font
    /// is not loaded, the range is empty, or blit fails — callers fall back to <see cref="CellInRange"/>.
    /// </summary>
    public ImageTexture? CellStrip(int tex0, int tex1)
    {
        if (!Loaded || tex1 <= tex0) return null;
        if (tex1 - tex0 == 1) return Cell(tex0);
        int key = (tex0 << 16) | (tex1 & 0xFFFF);
        if (_stripTex.TryGetValue(key, out ImageTexture? cached)) return cached;
        ImageTexture? result = null;
        try
        {
            if (!_cells.TryGetValue(tex0, out Rect2I r0) || r0.Size.X <= 0)
            { _stripTex[key] = null; return null; }
            int cellW = r0.Size.X, cellH = r0.Size.Y;
            int count = tex1 - tex0;
            var strip = Image.CreateEmpty(cellW * count, cellH, false, Image.Format.Rgba8);
            for (int i = 0; i < count; i++)
            {
                if (!_cells.TryGetValue(tex0 + i, out Rect2I rect) || rect.Size.X <= 0) continue;
                Image cell = _atlas!.GetRegion(rect);
                if (cell.GetFormat() != Image.Format.Rgba8) cell.Convert(Image.Format.Rgba8);
                strip.BlitRect(cell,
                    new Rect2I(0, 0, Math.Min(cellW, cell.GetWidth()), Math.Min(cellH, cell.GetHeight())),
                    new Vector2I(i * cellW, 0));
            }
            result = ImageTexture.CreateFromImage(strip);
        }
        catch { result = null; }
        _stripTex[key] = result;
        return result;
    }

    /// <summary>
    /// A representative sprite for a DP <c>[tex0, tex1)</c> range (tex1 exclusive, as in DP's
    /// <c>tex0 + rand%(tex1-tex0)</c>). DP randomises per particle; we pick one cell per emitter — random
    /// within the range when it spans several cells (so repeated bursts vary), else the single cell.
    /// <paramref name="decal"/> selects the Decal-node form (alpha = luminance) instead of the particle form.
    /// </summary>
    public ImageTexture? CellInRange(int tex0, int tex1, bool decal = false)
    {
        if (!Loaded)
            return null;
        int lo = tex0, hi = tex1;
        if (hi <= lo)
            hi = lo + 1;
        int span = hi - lo;
        int idx = span <= 1 ? lo : lo + (int)(GD.Randi() % (uint)span);
        return decal ? (DecalCell(idx) ?? DecalCell(lo)) : (Cell(idx) ?? Cell(lo));
    }
}
