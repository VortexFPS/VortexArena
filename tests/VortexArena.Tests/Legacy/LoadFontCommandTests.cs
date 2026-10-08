// gl_draw.c LoadFont_f as LegacyFontSlots.LoadCommand ports it: the loadfont console command the native text
// path (game/text/DpText.cs) and the legacy canvas both feed font-xolonium.cfg's lines through.
using VortexArena.Legacy.Presentation;
using Xunit;

namespace VortexArena.Tests.Legacy;

public class LoadFontCommandTests
{
    [Fact]
    public void A_font_xolonium_line_fills_the_named_slot_with_its_files_and_sizes()
    {
        LegacyFontSlots slots = new();
        int slot = slots.LoadCommand(new[] { "loadfont", "user2", "fonts/xolonium-bold.otf,fonts/unifont,gfx/vera-sans", "4", "6", "8", "11" });
        Assert.Equal(LegacyFontSlots.FontUser + 2, slot);
        Assert.Equal(new[] { "fonts/xolonium-bold.otf", "fonts/unifont", "gfx/vera-sans" }, slots[slot].Files);
        Assert.Equal(new[] { 4f, 6f, 8f, 11f }, slots[slot].Sizes);
    }

    [Fact]
    public void The_console_slot_keeps_unifont_first()
    {
        LegacyFontSlots slots = new();
        int slot = slots.LoadCommand(new[] { "loadfont", "console", "fonts/unifont,fonts/xolonium-regular.otf,gfx/vera-sans", "10" });
        Assert.Equal(1, slot);
        Assert.Equal("fonts/unifont", slots[slot].Files[0]);
    }

    [Fact]
    public void Scale_and_voffset_are_switches_not_sizes_and_duplicate_sizes_are_dropped()
    {
        LegacyFontSlots slots = new();
        int slot = slots.LoadCommand(new[] { "loadfont", "chat", "fonts/a.otf", "10", "scale", "1.5", "10", "voffset", "-0.25", "12" });
        Assert.Equal(new[] { 10f, 12f }, slots[slot].Sizes);
        Assert.Equal(1.5f, slots[slot].Scale);
        Assert.Equal(-0.25f, slots[slot].VerticalOffset);
    }

    [Fact]
    public void A_command_that_names_no_slot_does_nothing()
    {
        LegacyFontSlots slots = new();
        Assert.Equal(-1, slots.LoadCommand(new[] { "loadfont" }));
    }
}
