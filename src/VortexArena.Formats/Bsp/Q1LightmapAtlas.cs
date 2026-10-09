// Port of the lightmap half of Base/darkplaces/model_brush.c Mod_Q1BSP_LoadFaces (a block of light samples
// per face, placed in a shared texture; the lightmap texture coordinates) with one difference in kind:
// DarkPlaces adds a face's style layers together on the CPU whenever a style's value changes
// (gl_rsurf.c R_BuildLightMap) and uploads the sum. Here the layers stay apart, one texture layer each,
// and the renderer's shader adds them with the current style values - the same sum, no upload.
using System.Numerics;

namespace VortexArena.Formats.Bsp;

/// <summary>One texture of the atlas: <see cref="Layers"/> images of <see cref="Width"/> x <see cref="Height"/>
/// samples, three bytes (R, G, B) a sample, a light sample of 128 being full light.</summary>
public sealed class Q1LightmapPage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Layers { get; init; }
    public byte[][] Rgb { get; init; } = Array.Empty<byte[]>();
}

/// <summary>
/// Where a face's light block is. <see cref="Page"/> is -1 for a face drawn without a lightmap (sky).
/// <see cref="Constant"/>: the face has no samples of its own and reads one texel (white for a liquid without
/// light, or for any face of an unlit map), so every vertex gets the same coordinate.
/// <see cref="Style0"/>..<see cref="Style3"/>: the style of each texture layer, 255 for none.
/// </summary>
public readonly record struct Q1LightmapPlacement(int Page, int X, int Y, bool Constant, byte Style0, byte Style1, byte Style2, byte Style3)
{
    public int LayerCount => Style0 == 255 ? 0 : Style1 == 255 ? 1 : Style2 == 255 ? 2 : Style3 == 255 ? 3 : 4;
}

/// <summary>
/// The light of a Quake 1 format map as textures: every face's block of samples packed into pages, the up to
/// four style layers of a face in the same place on up to four layers of its page. Faces with one layer (most
/// of a map) go to one-layer pages, the rest to pages with as many layers as their faces need.
/// </summary>
public sealed class Q1LightmapAtlas
{
    /// <summary>Luxels are 16 texture units apart.</summary>
    public const int LuxelSize = 16;
    private const int ConstantBlock = 4;

    public Q1LightmapPage[] Pages { get; private init; } = Array.Empty<Q1LightmapPage>();
    /// <summary>Parallel to <see cref="Q1BspData.Faces"/>.</summary>
    public Q1LightmapPlacement[] Faces { get; private init; } = Array.Empty<Q1LightmapPlacement>();
    /// <summary>The map has no light data: every face is drawn at full light whatever the styles say
    /// ("set to full bright if no light data"; the stored sample is 128).</summary>
    public bool FullBright { get; private init; }
    /// <summary>Samples in all pages and layers together.</summary>
    public long SampleCount { get; private init; }

    /// <summary>
    /// The lightmap texture coordinate of a point of face <paramref name="face"/> whose texture-space
    /// position is (<paramref name="s"/>, <paramref name="t"/>) texels: DarkPlaces'
    /// <c>((s + 8 - texturemins) / 16 + lightmaporigin) / lightmapsize</c>.
    /// </summary>
    public Vector2 TexCoord(Q1BspData bsp, int face, float s, float t)
    {
        ref readonly Q1LightmapPlacement p = ref Faces[face];
        if (p.Page < 0) return Vector2.Zero;
        Q1LightmapPage page = Pages[p.Page];
        if (p.Constant) return new Vector2((p.X + ConstantBlock * 0.5f) / page.Width, (p.Y + ConstantBlock * 0.5f) / page.Height);
        ref readonly Q1Face f = ref bsp.Faces[face];
        return new Vector2(
            ((s + 8 - f.TextureMinS) / LuxelSize + p.X) / page.Width,
            ((t + 8 - f.TextureMinT) / LuxelSize + p.Y) / page.Height);
    }

    /// <param name="singlePageSize">Width (and greatest height) of a one-layer page.</param>
    /// <param name="multiPageSize">The same for a page with several layers.</param>
    public static Q1LightmapAtlas Build(Q1BspData bsp, int singlePageSize = 2048, int multiPageSize = 1024)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        singlePageSize = Math.Clamp(singlePageSize, 64, 8192);
        multiPageSize = Math.Clamp(multiPageSize, 64, 8192);
        Q1Face[] faces = bsp.Faces;
        var placements = new Q1LightmapPlacement[faces.Length];
        bool unlit = bsp.LightData.Length == 0;
        var single = new List<int>();
        var multi = new List<int>();
        var constants = new List<int>();
        var layersOf = new byte[faces.Length];

        for (int i = 0; i < faces.Length; i++)
        {
            ref readonly Q1Face f = ref faces[i];
            if (!f.Lightmapped)
            {
                placements[i] = new Q1LightmapPlacement(-1, 0, 0, false, 255, 255, 255, 255);
                continue;
            }
            if (unlit)
            {
                // one white texel; the renderer ignores the styles of an unlit map
                placements[i] = new Q1LightmapPlacement(0, 0, 0, true, 0, 255, 255, 255);
                constants.Add(i);
                continue;
            }
            int layers = 0;
            while (layers < 4 && f.Style(layers) != 255) layers++;   // "maps < MAXLIGHTMAPS && styles[maps] != 255"
            int w = f.LightWidth, h = f.LightHeight;
            long bytes = (long)w * h * 3 * layers;
            bool hasSamples = f.LightOffset >= 0 && layers > 0 && w > 0 && h > 0 && f.LightOffset + bytes <= bsp.LightData.Length
                && w <= Math.Min(singlePageSize, multiPageSize) && h <= Math.Min(singlePageSize, multiPageSize);
            if (!hasSamples)
            {
                // "give non-lightmapped water a 1x white lightmap" on style 0; any other face without samples is black
                placements[i] = f.WhiteLight
                    ? new Q1LightmapPlacement(0, 0, 0, true, 0, 255, 255, 255)
                    : new Q1LightmapPlacement(0, 0, 0, true, 255, 255, 255, 255);
                constants.Add(i);
                continue;
            }
            layersOf[i] = (byte)layers;
            (layers == 1 ? single : multi).Add(i);
        }

        var pages = new List<Q1LightmapPage>();
        long samples = 0;

        // The one-layer pages. The first begins with the white texels the constant faces read.
        bool wantConstant = constants.Count > 0;
        if (single.Count > 0 || wantConstant || multi.Count == 0)
            samples += Pack(bsp, single, layersOf, singlePageSize, wantConstant, pages, placements);
        if (multi.Count > 0)
            samples += Pack(bsp, multi, layersOf, multiPageSize, false, pages, placements);

        return new Q1LightmapAtlas { Pages = pages.ToArray(), Faces = placements, FullBright = unlit, SampleCount = samples };
    }

    // Shelf packing: blocks sorted by height, laid in rows. Returns the samples stored.
    private static long Pack(Q1BspData bsp, List<int> blocks, byte[] layersOf, int pageSize, bool constantBlock, List<Q1LightmapPage> pages, Q1LightmapPlacement[] placements)
    {
        Q1Face[] faces = bsp.Faces;
        blocks.Sort((a, b) =>
        {
            int byHeight = faces[b].LightHeight.CompareTo(faces[a].LightHeight);
            if (byHeight != 0) return byHeight;
            int byWidth = faces[b].LightWidth.CompareTo(faces[a].LightWidth);
            return byWidth != 0 ? byWidth : a.CompareTo(b);
        });

        // First pass: positions. (page, x, y) per block; the pages' heights and layer counts.
        var where = new (int Page, int X, int Y)[blocks.Count];
        var heights = new List<int>();
        var layerCounts = new List<int>();
        int page = -1, x = 0, y = 0, rowHeight = 0;
        void NewPage()
        {
            page++;
            heights.Add(0);
            layerCounts.Add(1);
            x = y = rowHeight = 0;
        }
        NewPage();
        if (constantBlock)
        {
            x = ConstantBlock;
            rowHeight = ConstantBlock;
        }
        for (int n = 0; n < blocks.Count; n++)
        {
            ref readonly Q1Face f = ref faces[blocks[n]];
            int w = f.LightWidth, h = f.LightHeight;
            if (x + w > pageSize)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }
            if (y + h > pageSize)
            {
                heights[page] = Math.Max(heights[page], y);
                NewPage();
            }
            where[n] = (page, x, y);
            x += w;
            rowHeight = Math.Max(rowHeight, h);
            heights[page] = Math.Max(heights[page], y + rowHeight);
            layerCounts[page] = Math.Max(layerCounts[page], layersOf[blocks[n]]);
        }
        if (constantBlock) heights[0] = Math.Max(heights[0], ConstantBlock);

        // Second pass: the pages, and the samples copied in.
        int firstPage = pages.Count;
        long samples = 0;
        for (int p = 0; p <= page; p++)
        {
            int height = Math.Max(4, (heights[p] + 3) & ~3);
            var rgb = new byte[layerCounts[p]][];
            for (int l = 0; l < rgb.Length; l++) rgb[l] = new byte[pageSize * height * 3];
            pages.Add(new Q1LightmapPage { Width = pageSize, Height = height, Layers = rgb.Length, Rgb = rgb });
            samples += (long)pageSize * height * rgb.Length;
        }
        if (constantBlock)
        {
            byte[] first = pages[firstPage].Rgb[0];
            for (int row = 0; row < ConstantBlock; row++)
                first.AsSpan(row * pageSize * 3, ConstantBlock * 3).Fill(128);
            // the constant faces were given page 0 at (0, 0) by the caller; firstPage is 0 whenever there are any
        }
        byte[] light = bsp.LightData;
        for (int n = 0; n < blocks.Count; n++)
        {
            int faceIndex = blocks[n];
            ref readonly Q1Face f = ref faces[faceIndex];
            (int p, int bx, int by) = where[n];
            Q1LightmapPage target = pages[firstPage + p];
            int w = f.LightWidth, h = f.LightHeight, layers = layersOf[faceIndex];
            for (int l = 0; l < layers; l++)
            {
                byte[] destination = target.Rgb[l];
                int source = f.LightOffset + l * w * h * 3;
                for (int row = 0; row < h; row++)
                    Buffer.BlockCopy(light, source + row * w * 3, destination, ((by + row) * target.Width + bx) * 3, w * 3);
            }
            placements[faceIndex] = new Q1LightmapPlacement(firstPage + p, bx, by, false,
                f.Style0, layers > 1 ? f.Style1 : (byte)255, layers > 2 ? f.Style2 : (byte)255, layers > 3 ? f.Style3 : (byte)255);
        }
        return samples;
    }
}
