using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The per-category <c>gl_texturecompression_*</c> gates (planning/texture-compression-and-caching-2026-07-31.md
/// §1, sourced from DarkPlaces <c>gl_textures.c:37-48</c>).
///
/// <para>Classification is the part of this feature that fails SILENTLY: a mis-bucketed texture does not error,
/// it just quietly obeys the wrong cvar — so a normal map landing in the <c>_color</c> bucket would get
/// block-compressed even with <c>gl_texturecompression_normal 0</c>, which is precisely the outcome routing
/// <c>_norm</c> to its own category exists to prevent.</para>
/// </summary>
public class TextureCompressionCategoryTests
{
    // ---- Companion-suffix channels ---------------------------------------------------------------------

    [Theory]
    [InlineData("textures/trak5x/base/base_pipe1a_norm.dds")]
    [InlineData("dds/textures/trak5x/base/base_pipe1a_norm.dds")]
    [InlineData("textures/exomorph/exo_floor_norm.tga")]
    [InlineData("models/player/erebus_norm")]                 // extensionless: the companion-probe form
    [InlineData("MODELS/PLAYER/EREBUS_NORM.TGA")]             // vpaths are compared case-insensitively
    public void NormalMaps_Route_To_The_Normal_Category(string vpath)
        => Assert.Equal(TexCategory.Normal, TextureCategories.Classify(vpath));

    [Theory]
    [InlineData("textures/x/foo_gloss.dds", TexCategory.Gloss)]
    [InlineData("textures/x/foo_glow.tga", TexCategory.Glow)]
    [InlineData("textures/x/foo_luma.tga", TexCategory.Glow)]   // Xonotic's spelling of the same channel
    [InlineData("textures/x/foo_reflect.tga", TexCategory.ReflectMask)]
    [InlineData("textures/x/foo.tga", TexCategory.Color)]
    public void CompanionSuffixes_Route_To_Their_Channel(string vpath, TexCategory expected)
        => Assert.Equal(expected, TextureCategories.Classify(vpath));

    /// <summary>
    /// The skin-shader masks have no DP compression category of their own — DP bakes <c>_pants</c>/<c>_shirt</c>
    /// as ordinary skinframe channels — so they must fall through to <c>_color</c> rather than accidentally
    /// matching a longer suffix test.
    /// </summary>
    [Theory]
    [InlineData("models/player/erebus_shirt.tga")]
    [InlineData("models/player/erebus_pants.tga")]
    public void SkinMasks_Fall_Through_To_Color(string vpath)
        => Assert.Equal(TexCategory.Color, TextureCategories.Classify(vpath));

    // ---- Path-keyed buckets ------------------------------------------------------------------------------

    [Theory]
    [InlineData("lm_0000.tga")]
    [InlineData("maps/stormkeep/lm_0003.tga")]
    public void LightmapPages_Route_To_Q3BspLightmaps(string vpath)
        => Assert.Equal(TexCategory.Q3BspLightmaps, TextureCategories.Classify(vpath));

    /// <summary>The <c>lm_</c> prefix outranks a channel suffix — a deluxe page can carry both.</summary>
    [Fact]
    public void LightmapPrefix_Beats_A_Channel_Suffix()
        => Assert.Equal(TexCategory.Q3BspLightmaps, TextureCategories.Classify("maps/x/lm_0001_norm.tga"));

    [Theory]
    [InlineData("env/distant_sunset/distant_sunset_up.tga")]
    [InlineData("gfx/env/exosystem/exosystem_rt.jpg")]
    [InlineData("textures/skies/env/foo_bk.tga")]
    public void SkyboxFaces_Route_To_Sky(string vpath)
        => Assert.Equal(TexCategory.Sky, TextureCategories.Classify(vpath));

    /// <summary>
    /// Sky is keyed on the <c>env/</c> path root, NOT on the six side suffixes: those are far too generic to
    /// key on alone, and a world texture merely ending in <c>_up</c> must stay a colormap.
    /// </summary>
    [Theory]
    [InlineData("textures/trak5x/panel_up.tga")]
    [InlineData("textures/exomorph/wall_dn.tga")]
    public void SideSuffixes_Alone_Are_Not_Sky(string vpath)
        => Assert.Equal(TexCategory.Color, TextureCategories.Classify(vpath));

    [Theory]
    [InlineData("gfx/hud/luma/health.tga", TexCategory.TwoD)]
    [InlineData("gfx/crosshair16.tga", TexCategory.TwoD)]
    [InlineData("sprites/chatbubble.spr_0.tga", TexCategory.Sprites)]
    [InlineData("models/misc/chatbubble.spr_0.tga", TexCategory.Sprites)]
    public void HudArt_And_Sprites_Route_To_Their_Buckets(string vpath, TexCategory expected)
        => Assert.Equal(expected, TextureCategories.Classify(vpath));

    /// <summary>Sprites outrank the gfx/ 2d bucket when a sprite sheet lives under gfx/.</summary>
    [Fact]
    public void SpritePath_Beats_The_Gfx_Bucket()
        => Assert.Equal(TexCategory.Sprites, TextureCategories.Classify("gfx/sprites/particlefont.tga"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyPath_Is_Color(string? vpath)
        => Assert.Equal(TexCategory.Color, TextureCategories.Classify(vpath!));

    // ---- DP's registered defaults ------------------------------------------------------------------------

    /// <summary>
    /// The mask must reproduce DarkPlaces' registered defaults exactly (<c>gl_textures.c:38-48</c>): color,
    /// gloss, glow, lightcubemaps, reflectmask and sprites ON; normal, 2d, both q3bsp maps and sky OFF.
    /// Xonotic's cfg overrides (<c>_sky</c> on, <c>_lightcubemaps</c> off, …) are NOT baked in here — they
    /// arrive by executing xonotic-client.cfg over these defaults, exactly as upstream does it.
    /// </summary>
    [Theory]
    [InlineData(TexCategory.Color, true)]
    [InlineData(TexCategory.Gloss, true)]
    [InlineData(TexCategory.Glow, true)]
    [InlineData(TexCategory.LightCubemaps, true)]
    [InlineData(TexCategory.ReflectMask, true)]
    [InlineData(TexCategory.Sprites, true)]
    [InlineData(TexCategory.Normal, false)]
    [InlineData(TexCategory.TwoD, false)]
    [InlineData(TexCategory.Q3BspLightmaps, false)]
    [InlineData(TexCategory.Q3BspDeluxemaps, false)]
    [InlineData(TexCategory.Sky, false)]
    public void DefaultMask_Matches_DarkPlaces(TexCategory category, bool expected)
        => Assert.Equal(expected, TextureCategories.Enabled(TextureCategories.DefaultMask, category));

    /// <summary>
    /// The headline of this change: with stock defaults a <c>_norm</c> texture is never block-compressed.
    /// Upstream ships <c>gl_texturecompression_normal 0</c> and so do we — and here it also keeps the port off
    /// a path where Godot's normal-map compression would drop the blue channel the shaders read as Z.
    /// </summary>
    [Fact]
    public void NormalMaps_Are_Not_Compressed_By_Default()
    {
        TexCategory c = TextureCategories.Classify("textures/x/wall_norm.tga");
        Assert.Equal(TexCategory.Normal, c);
        Assert.False(TextureCategories.Enabled(TextureCategories.DefaultMask, c));
    }

    [Fact]
    public void Enabled_Reads_The_Requested_Bit_Only()
    {
        int only = 1 << (int)TexCategory.Sky;
        Assert.True(TextureCategories.Enabled(only, TexCategory.Sky));
        Assert.False(TextureCategories.Enabled(only, TexCategory.Color));
        Assert.False(TextureCategories.Enabled(0, TexCategory.Sky));
    }
}
