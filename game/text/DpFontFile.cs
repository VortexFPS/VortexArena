// Port of Base/darkplaces/ft2.c Font_LoadFile / Font_LoadMap as far as "how FreeType is asked for a glyph" goes:
// the load flags r_font_hinting selects, no sub-pixel pen, no shaping features.
using Godot;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Text;

/// <summary>
/// A font file set up the way DarkPlaces rasterises it. Shared by the native text path (<see cref="DpText"/>)
/// and the legacy client's canvas, so both ask Godot's FreeType for exactly the same glyphs.
/// </summary>
public static class DpFontFile
{
    /// <summary>
    /// A new <see cref="FontFile"/> over <paramref name="data"/> (TrueType or OpenType bytes), or null when the
    /// bytes are not a font. Every caller gets its own copy: these settings are DarkPlaces', and the same file
    /// may be in use elsewhere under Godot's defaults.
    /// </summary>
    public static FontFile? Create(byte[] data, string sourceName)
    {
        FontFile? file = FontLoader.FromBytes(data, sourceName);
        if (file is null) return null;
        // Glyphs sit on whole pixels of the font map (there is no sub-pixel pen in DrawQ_String), and a
        // character none of the slot's files has is not drawn from whatever the operating system offers:
        // without this the symbols of Xonotic's character map came out as the system's colour emoji.
        file.SubpixelPositioning = TextServer.SubpixelPositioning.Disabled;
        file.AllowSystemFallback = false;
        // DrawQ_String places one character at a time: no ligatures, no contextual forms. Its kerning is
        // FT_Get_Kerning's default mode, which scales a pair's value by ppem/25 below 25 pixels and then
        // rounds it to whole pixels - at the 10 to 14 pixels the menu and HUD are drawn at, nearly every
        // pair rounds to nothing. A shaper applies the full fractional value instead, which made every line
        // about 1.5% narrower than DarkPlaces' and wrapped paragraphs one word later. Off is the closer.
        TextServer server = TextServerManager.GetPrimaryInterface();
        Godot.Collections.Dictionary features = new();
        foreach (string tag in new[] { "kern", "liga", "clig", "calt", "dlig" }) features[server.NameToTag(tag)] = 0;
        file.OpentypeFeatureOverrides = features;
        return file;
    }

    /// <summary>
    /// r_font_hinting: "0 = no hinting, 1 = light autohinting, 2 = full autohinting, 3 = full hinting" - ft2.c
    /// Font_LoadMap's load flags FT_LOAD_NO_HINTING, FT_LOAD_FORCE_AUTOHINT | FT_LOAD_TARGET_LIGHT,
    /// FT_LOAD_FORCE_AUTOHINT | FT_LOAD_TARGET_NORMAL, FT_LOAD_TARGET_NORMAL.
    /// </summary>
    public static void ApplyHinting(FontFile file, int hinting)
    {
        file.Hinting = hinting switch { <= 0 => TextServer.Hinting.None, 1 => TextServer.Hinting.Light, _ => TextServer.Hinting.Normal };
        file.ForceAutohinter = hinting is 1 or 2;
    }
}
