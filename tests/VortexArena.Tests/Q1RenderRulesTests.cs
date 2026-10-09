using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VortexArena.Formats.Bsp;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The engine-free rules behind the drawing of a Quake 1 format map: texture names, the palette and its
/// full-bright split, the sky split, light style values, and the lightmap atlas with its texture coordinates.
/// Everything is built in code; the one test on real content skips when the packages are not on the machine.
/// </summary>
public class Q1RenderRulesTests
{
    // ---- texture names -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("wall1", Q1SurfaceKind.Wall, false)]
    [InlineData("{fence", Q1SurfaceKind.Fence, false)]
    [InlineData("*water1", Q1SurfaceKind.Liquid, true)]
    [InlineData("*slime0", Q1SurfaceKind.Liquid, true)]
    [InlineData("*lava1", Q1SurfaceKind.Liquid, false)]
    [InlineData("*teleport", Q1SurfaceKind.Liquid, false)]
    [InlineData("*rift1", Q1SurfaceKind.Liquid, false)]
    [InlineData("sky4", Q1SurfaceKind.Sky, false)]
    [InlineData("skyscraper", Q1SurfaceKind.Sky, false)]   // "sky" is a prefix test in DarkPlaces
    [InlineData("caulk", Q1SurfaceKind.NoDraw, false)]
    [InlineData("caulk2", Q1SurfaceKind.Wall, false)]
    [InlineData("+0button", Q1SurfaceKind.Wall, false)]
    public void A_texture_is_classified_by_its_name(string name, Q1SurfaceKind kind, bool waterAlpha)
    {
        Q1TextureClass c = Q1TextureRules.Classify(name);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(waterAlpha, c.WaterAlpha);
        Assert.Equal(kind == Q1SurfaceKind.Liquid, c.WaterScroll);
    }

    [Fact]
    public void Replacement_images_are_looked_for_under_the_map_first_and_a_star_is_a_hash()
    {
        Assert.Equal(new[] { "textures/e1m1/#water1", "textures/#water1" }, Q1TextureRules.ExternalCandidates("e1m1", "*water1"));
        Assert.Equal(new[] { "textures/e1m1/+0lamp", "textures/+0lamp" }, Q1TextureRules.ExternalCandidates("e1m1", "+0lamp"));
        Assert.Equal(new[] { "textures/wall" }, Q1TextureRules.ExternalCandidates("", "wall"));
        Assert.Equal("#04water1", Q1TextureRules.FileName("*04water1"));
        Assert.Equal(new[] { "_glow", "_luma" }, Q1TextureRules.GlowSuffixes);
        Assert.Equal("mc_q30th_end", Q1TextureRules.MapName("maps/mc_q30th_end.bsp"));
        Assert.Equal("sub/b_nail0", Q1TextureRules.MapName("maps/sub/b_nail0.bsp"));
        Assert.Equal("progs/b.v2", Q1TextureRules.MapName("progs/b.v2.bsp"));
    }

    [Fact]
    public void Animation_chains_are_built_as_DarkPlaces_links_them()
    {
        string[] names = { "wall", "+0lamp", "+1lamp", "+2lamp", "+alamp", "+0button", "+abutton", "+1broken", "+0solo", "+aonly", "+bonly", "+xbad" };
        Q1TextureAnimation?[] a = Q1TextureRules.BuildAnimations(names);
        Assert.Null(a[0]);
        // primary frames: chain 1,2,3; alternate 4
        Assert.Equal(new[] { 1, 2, 3 }, a[1]!.Primary);
        Assert.Equal(new[] { 4 }, a[1]!.Alternate);
        Assert.Same(a[1], a[2]);
        Assert.Same(a[1], a[3]);
        // the alternate frame has the two swapped
        Assert.Equal(new[] { 4 }, a[4]!.Primary);
        Assert.Equal(new[] { 1, 2, 3 }, a[4]!.Alternate);
        // a button: one frame each way
        Assert.Equal(new[] { 5 }, a[5]!.Primary);
        Assert.Equal(new[] { 6 }, a[5]!.Alternate);
        // "+1broken" has no frame 0: "Missing frame 0", not linked
        Assert.Null(a[7]);
        // one frame alone: the alternate is the primary
        Assert.Equal(new[] { 8 }, a[8]!.Primary);
        Assert.Equal(new[] { 8 }, a[8]!.Alternate);
        // only alternate frames: they serve as both
        Assert.Equal(new[] { 9, 10 }, a[9]!.Primary);
        Assert.Equal(new[] { 9, 10 }, a[9]!.Alternate);
        Assert.Same(a[9], a[10]);
        Assert.Null(a[11]);
    }

    [Fact]
    public void An_animated_texture_steps_five_times_a_second_and_a_nonzero_entity_frame_takes_the_alternate_chain()
    {
        var animation = new Q1TextureAnimation(new[] { 10, 11, 12 }, new[] { 20 });
        Assert.Equal(10, animation.FrameAt(0.0, false));
        Assert.Equal(10, animation.FrameAt(0.19, false));
        Assert.Equal(11, animation.FrameAt(0.2, false));
        Assert.Equal(12, animation.FrameAt(0.4, false));
        Assert.Equal(10, animation.FrameAt(0.6, false));
        Assert.Equal(20, animation.FrameAt(0.4, true));
        Assert.Equal(11, new Q1TextureAnimation(new[] { 10, 11 }, new[] { 10, 11 }).FrameAt(0.2, true));
    }

    // ---- palette -------------------------------------------------------------------------------------------

    [Fact]
    public void The_built_in_palette_is_Quakes_and_has_no_full_bright_range_without_a_colormap()
    {
        Q1Palette p = Q1Palette.BuiltIn;
        Assert.Equal(768, p.Rgb.Length);
        Assert.Equal((0, 0, 0), (p.Rgb[0], p.Rgb[1], p.Rgb[2]));
        Assert.Equal((15, 15, 15), (p.Rgb[3], p.Rgb[4], p.Rgb[5]));
        Assert.Equal((159, 91, 83), (p.Rgb[765], p.Rgb[766], p.Rgb[767]));   // index 255
        Assert.False(p.HasFullbrights);
        Assert.False(p.AnyFullbright(new byte[] { 0, 100, 254 }));
        Assert.Equal(224, Q1Palette.BuiltInWithQuakeFullbrights.FullbrightStart);
    }

    [Fact]
    public void Palette_and_colormap_files_replace_the_colours_and_name_the_full_bright_range()
    {
        byte[] file = new byte[768];
        for (int i = 0; i < 768; i++) file[i] = (byte)(i % 251);
        byte[] colormap = new byte[16385];
        colormap[16384] = 32;
        Q1Palette p = Q1Palette.Load(file, colormap);
        Assert.Equal(file, p.Rgb);
        Assert.Equal(224, p.FullbrightStart);
        // too short a file is ignored
        Q1Palette q = Q1Palette.Load(new byte[100], new byte[16384]);
        Assert.Equal(Q1Palette.BuiltIn.Rgb, q.Rgb);
        Assert.False(q.HasFullbrights);
    }

    [Fact]
    public void The_full_bright_split_moves_the_glowing_indices_to_their_own_layer()
    {
        Q1Palette p = Q1Palette.BuiltInWithQuakeFullbrights;
        byte[] pixels = { 1, 223, 224, 254, 255 };
        Assert.True(p.AnyFullbright(pixels));

        byte[] whole = p.ToRgba(pixels, Q1PaletteMode.Complete);
        byte[] lit = p.ToRgba(pixels, Q1PaletteMode.NoFullbrights);
        byte[] glow = p.ToRgba(pixels, Q1PaletteMode.OnlyFullbrights);
        for (int i = 0; i < pixels.Length; i++)
        {
            bool bright = pixels[i] >= 224;
            (byte r, byte g, byte b) colour = (p.Rgb[pixels[i] * 3], p.Rgb[pixels[i] * 3 + 1], p.Rgb[pixels[i] * 3 + 2]);
            Assert.Equal(colour, (whole[i * 4], whole[i * 4 + 1], whole[i * 4 + 2]));
            Assert.Equal(255, whole[i * 4 + 3]);
            // the base: a glowing pixel is colour 0 (black), opaque
            Assert.Equal(bright ? ((byte)0, (byte)0, (byte)0) : colour, (lit[i * 4], lit[i * 4 + 1], lit[i * 4 + 2]));
            Assert.Equal(255, lit[i * 4 + 3]);
            // the glow: everything else is zero
            Assert.Equal(bright ? colour : ((byte)0, (byte)0, (byte)0), (glow[i * 4], glow[i * 4 + 1], glow[i * 4 + 2]));
        }
        // a fence texture: index 255 is see-through in every layer
        Assert.Equal(0, p.ToRgba(pixels, Q1PaletteMode.Transparent)[4 * 4 + 3]);
        Assert.Equal(0, p.ToRgba(pixels, Q1PaletteMode.NoFullbrightsTransparent)[4 * 4 + 3]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, p.ToRgba(pixels, Q1PaletteMode.OnlyFullbrightsTransparent)[16..20]);
        Assert.Equal(255, p.ToRgba(pixels, Q1PaletteMode.Transparent)[3 * 4 + 3]);
    }

    [Fact]
    public void The_sky_texture_is_split_into_a_solid_right_half_and_a_keyed_left_half()
    {
        Q1Palette p = Q1Palette.BuiltIn;
        const int width = 8, height = 4;
        byte[] pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = x < 4 ? (byte)((x + y) % 2 == 0 ? 0 : 40) : (byte)(100 + x);
        (byte[] solid, byte[] alpha, int w, int h) = p.SplitSky(pixels, width, height);
        Assert.Equal((4, 4), (w, h));
        long r = 0, g = 0, b = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int right = pixels[y * width + x + 4];
                int o = (y * w + x) * 4;
                Assert.Equal((p.Rgb[right * 3], p.Rgb[right * 3 + 1], p.Rgb[right * 3 + 2], (byte)255), (solid[o], solid[o + 1], solid[o + 2], solid[o + 3]));
                r += p.Rgb[right * 3]; g += p.Rgb[right * 3 + 1]; b += p.Rgb[right * 3 + 2];
            }
        (byte, byte, byte) average = ((byte)(r / 16), (byte)(g / 16), (byte)(b / 16));
        // index 0 of the front layer: transparent, in the back layer's average colour
        Assert.Equal((average.Item1, average.Item2, average.Item3, (byte)0), (alpha[0], alpha[1], alpha[2], alpha[3]));
        // any other index: its colour, opaque
        Assert.Equal((p.Rgb[120], p.Rgb[121], p.Rgb[122], (byte)255), (alpha[4], alpha[5], alpha[6], alpha[7]));
        Assert.Equal(0, p.SplitSky(new byte[3], 8, 4).Width);
    }

    // ---- light styles --------------------------------------------------------------------------------------

    [Fact]
    public void A_light_style_letter_is_worth_22_a_step_and_m_is_264()
    {
        Assert.Equal(256, Q1LightStyles.Value(null, 3.0));
        Assert.Equal(256, Q1LightStyles.Value("", 3.0));
        Assert.Equal(264, Q1LightStyles.Value("m", 0));
        Assert.Equal(264, Q1LightStyles.Value("m", 123.456));
        Assert.Equal(0, Q1LightStyles.Value("a", 1));
        Assert.Equal(550, Q1LightStyles.Value("z", 1));
        // ten letters a second, not blended by default
        Assert.Equal(0, Q1LightStyles.Value("az", 0.05));
        Assert.Equal(550, Q1LightStyles.Value("az", 0.15));
        Assert.Equal(0, Q1LightStyles.Value("az", 0.25));
        // r_lerplightstyles 1: between the letter before and this one
        Assert.Equal(275, Q1LightStyles.Value("az", 0.15, lerp: true), 1.0);
        Assert.Equal(384, Q1LightStyles.Value("=1.5", 9));
        Assert.Equal(264, Q1LightStyles.Value("m", -0.5));
    }

    [Fact]
    public void Evaluate_gives_multipliers_and_keeps_the_no_layer_entry_at_zero()
    {
        string?[] styles = new string?[64];
        styles[0] = "m";
        styles[32] = "a";
        float[] scales = new float[Q1LightStyles.Count];
        Assert.True(Q1LightStyles.Evaluate(styles, 1.0, false, scales));
        Assert.Equal(1.03125f, scales[0]);
        Assert.Equal(1f, scales[1]);
        Assert.Equal(0f, scales[32]);
        Assert.Equal(1f, scales[200]);
        Assert.Equal(0f, scales[255]);
        Assert.False(Q1LightStyles.Evaluate(styles, 5.0, false, scales));
        styles[32] = "m";
        Assert.True(Q1LightStyles.Evaluate(styles, 5.0, false, scales));
        Assert.Equal(1.03125f, scales[32]);
    }

    [Fact]
    public void Layers_are_combined_as_R_BuildLightMap_combines_them()
    {
        // one layer under "m": 128 becomes 132 (3 % over full light)
        Assert.Equal(132, Q1LightStyles.Combine(new byte[] { 128 }, new[] { 264 }));
        // two layers, the second switched off
        Assert.Equal(103, Q1LightStyles.Combine(new byte[] { 100, 200 }, new[] { 264, 0 }));
        Assert.Equal(255, Q1LightStyles.Combine(new byte[] { 200, 200 }, new[] { 264, 264 }));
        // the shader's form of the same sum: samples / 255 times value / 256, less the half unit the shift loses
        float shader = (100 / 255f) * (264 / 256f) + (200 / 255f) * (132 / 256f);
        Assert.Equal(Q1LightStyles.Combine(new byte[] { 100, 200 }, new[] { 264, 132 }) / 255f, shader - 0.5f / 255f, 1.0 / 255);
    }

    // ---- lightmap atlas ------------------------------------------------------------------------------------

    private static Q1Face Face(int lightOffset, int extentS, int extentT, byte s0 = 0, byte s1 = 255, byte s2 = 255, byte s3 = 255,
        bool white = false, bool lightmapped = true, int minS = 0, int minT = 0) =>
        new(0, false, 0, 4, 0, 0, s0, s1, s2, s3, lightOffset, white, lightmapped, minS, minT, extentS, extentT, Vector3.UnitZ, Vector3.Zero, Vector3.One);

    private static Q1BspData Map(Q1Face[] faces, int lightBytes)
    {
        byte[] light = new byte[lightBytes];
        for (int i = 0; i < light.Length; i++) light[i] = (byte)(1 + i % 250);
        return new Q1BspData { Faces = faces, LightData = light };
    }

    [Fact]
    public void Blocks_do_not_overlap_and_hold_the_faces_samples_layer_by_layer()
    {
        var random = new Random(7);
        var faces = new List<Q1Face>();
        int offset = 0;
        for (int i = 0; i < 400; i++)
        {
            int w = random.Next(1, 19), h = random.Next(1, 19);
            int layers = i % 7 == 0 ? random.Next(2, 5) : 1;
            faces.Add(Face(offset, (w - 1) * 16, (h - 1) * 16, 0, layers > 1 ? (byte)32 : (byte)255, layers > 2 ? (byte)5 : (byte)255, layers > 3 ? (byte)6 : (byte)255,
                minS: random.Next(-20, 20) * 16, minT: random.Next(-20, 20) * 16));
            offset += w * h * 3 * layers;
        }
        Q1BspData bsp = Map(faces.ToArray(), offset);
        Q1LightmapAtlas atlas = Q1LightmapAtlas.Build(bsp, singlePageSize: 128, multiPageSize: 64);
        Assert.True(atlas.Pages.Length > 2);
        Assert.False(atlas.FullBright);

        var used = new Dictionary<int, bool[]>();
        for (int f = 0; f < bsp.Faces.Length; f++)
        {
            Q1Face face = bsp.Faces[f];
            Q1LightmapPlacement p = atlas.Faces[f];
            Q1LightmapPage page = atlas.Pages[p.Page];
            int w = face.LightWidth, h = face.LightHeight, layers = p.LayerCount;
            Assert.False(p.Constant);
            Assert.InRange(p.X, 0, page.Width - w);
            Assert.InRange(p.Y, 0, page.Height - h);
            Assert.True(layers <= page.Layers);
            Assert.Equal(layers == 1, page.Layers == 1);   // one-layer faces and the others live on different pages
            if (!used.TryGetValue(p.Page, out bool[]? map)) used[p.Page] = map = new bool[page.Width * page.Height];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int at = (p.Y + y) * page.Width + p.X + x;
                    Assert.False(map[at], $"face {f} overlaps another at page {p.Page} ({p.X + x}, {p.Y + y})");
                    map[at] = true;
                    for (int l = 0; l < layers; l++)
                        for (int c = 0; c < 3; c++)
                            Assert.Equal(bsp.LightData[face.LightOffset + ((l * h + y) * w + x) * 3 + c], page.Rgb[l][at * 3 + c]);
                }
            // The corners of the face's extent land on the centres of the block's corner samples.
            Vector2 low = atlas.TexCoord(bsp, f, face.TextureMinS, face.TextureMinT);
            Vector2 high = atlas.TexCoord(bsp, f, face.TextureMinS + face.ExtentS, face.TextureMinT + face.ExtentT);
            Assert.Equal((p.X + 0.5f) / page.Width, low.X, 5);
            Assert.Equal((p.Y + 0.5f) / page.Height, low.Y, 5);
            Assert.Equal((p.X + w - 0.5f) / page.Width, high.X, 5);
            Assert.Equal((p.Y + h - 0.5f) / page.Height, high.Y, 5);
        }
    }

    [Fact]
    public void Faces_without_samples_read_one_white_texel_or_are_black_and_sky_has_no_lightmap()
    {
        Q1Face[] faces =
        {
            Face(0, 16, 16),                                   // 2 x 2 samples
            Face(-1, 64, 64, white: true),                     // a liquid without light: white, on style 0
            Face(-1, 64, 64, s0: 3),                           // a wall without samples in a lit map: black
            Face(-1, 64, 64, lightmapped: false),              // sky
            Face(999999, 16, 16),                              // samples beyond the lump: black
        };
        Q1BspData bsp = Map(faces, 12);
        Q1LightmapAtlas atlas = Q1LightmapAtlas.Build(bsp);
        Q1LightmapPage page = atlas.Pages[0];

        Q1LightmapPlacement water = atlas.Faces[1];
        Assert.True(water.Constant);
        Assert.Equal((0, 255), (water.Style0, water.Style1));
        Vector2 a = atlas.TexCoord(bsp, 1, 0, 0), b = atlas.TexCoord(bsp, 1, 64, 64);
        Assert.Equal(a, b);
        int texel = ((int)(a.Y * page.Height) * page.Width + (int)(a.X * page.Width)) * 3;
        Assert.Equal(128, page.Rgb[0][texel]);
        // bilinear filtering around that coordinate stays inside the white block
        Assert.Equal(128, page.Rgb[0][texel - 3]);
        Assert.Equal(128, page.Rgb[0][texel - page.Width * 3]);

        Assert.Equal(0, atlas.Faces[2].LayerCount);            // every layer "none": the shader's sum is zero
        Assert.Equal(-1, atlas.Faces[3].Page);
        Assert.Equal(0, atlas.Faces[4].LayerCount);

        // the real block does not sit on the white texels
        Q1LightmapPlacement real = atlas.Faces[0];
        Assert.False(real.Constant);
        Assert.True(real.X >= 4 || real.Y >= 4);
    }

    [Fact]
    public void A_map_without_light_data_is_full_bright()
    {
        Q1BspData bsp = new() { Faces = new[] { Face(-1, 32, 32), Face(0, 32, 32, s0: 7) } };
        Q1LightmapAtlas atlas = Q1LightmapAtlas.Build(bsp);
        Assert.True(atlas.FullBright);
        Assert.All(atlas.Faces, p => Assert.True(p.Constant));
        Vector2 at = atlas.TexCoord(bsp, 1, 5, 5);
        Q1LightmapPage page = atlas.Pages[0];
        Assert.Equal(128, page.Rgb[0][((int)(at.Y * page.Height) * page.Width + (int)(at.X * page.Width)) * 3]);
    }

    // ---- real content ----------------------------------------------------------------------------------------

    [Fact]
    public void A_real_map_packs_every_face(/* skips without the package */)
    {
        string pack = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "_scratch", "q1", "pk3", "q1-mc30_01.pk3"));
        for (string? dir = AppContext.BaseDirectory; dir is not null && !File.Exists(pack); dir = Path.GetDirectoryName(dir))
            pack = Path.Combine(dir, "_scratch", "q1", "pk3", "q1-mc30_01.pk3");
        if (!File.Exists(pack)) return;   // the downloaded packages are not on this machine

        using var zip = System.IO.Compression.ZipFile.OpenRead(pack);
        byte[] Read(string name)
        {
            using Stream s = zip.GetEntry(name)!.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }
        Q1BspData bsp = Q1BspReader.Read(Read("maps/mc_q30th_caffeinefreak.bsp"), Read("maps/mc_q30th_caffeinefreak.lit"), Array.Empty<byte>());
        Q1LightmapAtlas atlas = Q1LightmapAtlas.Build(bsp);
        Assert.Equal(bsp.Faces.Length, atlas.Faces.Length);
        int placed = 0;
        for (int f = 0; f < bsp.Faces.Length; f++)
        {
            Q1LightmapPlacement p = atlas.Faces[f];
            if (p.Page < 0) continue;
            Q1LightmapPage page = atlas.Pages[p.Page];
            if (p.Constant) continue;
            placed++;
            Assert.True(p.X + bsp.Faces[f].LightWidth <= page.Width && p.Y + bsp.Faces[f].LightHeight <= page.Height);
        }
        Assert.True(placed > bsp.Faces.Length / 2);
        string[] names = new string[bsp.Textures.Length];
        for (int i = 0; i < names.Length; i++) names[i] = bsp.Textures[i].Name;
        Q1TextureAnimation?[] animations = Q1TextureRules.BuildAnimations(names);
        Assert.Equal(names.Length, animations.Length);
    }
}
