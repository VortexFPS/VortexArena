using VortexArena.Legacy.Presentation;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// ft2.c Font_LoadSize / Font_GetKerningForMap: a pair's kerning is FT_Get_Kerning's value (26.6 fixed point at
/// the font map's FreeType size) snapped to whole pixels of the map - "Font_SnapTo((kern / 64.0) / size,
/// 1 / size)", which is floor(x + 0.5) pixels. The values below are what DarkPlaces' own FreeType 2.14.3
/// returns for Xolonium Regular (measured through its libfreetype-6.dll): "AY" is -64 at a FreeType size of 10
/// (the 12-pixel font map), the first size at which any pair survives; "AY" is -128 at 17; "(j" is +64 at 20.
/// </summary>
public class FontKerningTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-64f, -1f)]      // AY, LY, YA at FreeType size 10
    [InlineData(-128f, -2f)]     // AY at FreeType size 17
    [InlineData(64f, 1f)]        // (j at FreeType size 20
    [InlineData(-256f, -4f)]     // ,? at FreeType size 40
    [InlineData(-31f, 0f)]       // an unfitted value below half a pixel is nothing
    [InlineData(-32f, 0f)]       // floor(-0.5 + 0.5) = 0: half a pixel to the left rounds to none
    [InlineData(-33f, -1f)]
    [InlineData(31f, 0f)]
    [InlineData(32f, 1f)]        // floor(0.5 + 0.5) = 1: half a pixel to the right rounds up
    public void Kerning_is_snapped_to_whole_pixels_of_the_font_map(float kern26Dot6, float pixels)
    {
        Assert.Equal(pixels, LegacyTextLayout.SnapKerning(kern26Dot6));
    }
}
