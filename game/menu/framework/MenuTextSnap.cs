// Port of Base/darkplaces/gl_draw.c snap_to_pixel_x / snap_to_pixel_y as DrawQ_String applies them
// ("startx = snap_to_pixel_x(startx, 0.4)"): text starts on a whole pixel of the WINDOW.
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The native menu is laid out 1080 units high and scaled to the window, so at 1280x720 a label sits at two
/// thirds of a pixel as often as not. Its glyphs are DarkPlaces font-map pictures made for whole pixels
/// (<see cref="VortexArena.Game.Text.DpBitmapFont"/>); drawn between pixels the texture filter greys every
/// face and smears every outline. DarkPlaces snaps a string's origin to a whole pixel of the window; here a
/// canvas shader does the same to the vertices of each text-drawing control, after the menu's scale. A glyph
/// picture is a whole number of pixels wide and its neighbours a whole number of pixels apart, so every glyph
/// of a line moves by the same fraction and the line stays intact.
/// </summary>
public static class MenuTextSnap
{
    private static ShaderMaterial? _material;

    /// <summary>The shared material: snap each vertex to the pixel grid, the next pixel once four tenths past one.</summary>
    public static ShaderMaterial Material => _material ??= new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type canvas_item;
                void vertex() {
                    mat4 to_window = CANVAS_MATRIX * MODEL_MATRIX;
                    vec4 window = to_window * vec4(VERTEX, 0.0, 1.0);
                    window.xy = floor(window.xy + vec2(0.6));
                    VERTEX = (inverse(to_window) * window).xy;
                }
                """,
        },
    };

    /// <summary>Gives a control that draws text the snapping material, unless it already has a material of its own.</summary>
    public static void Apply(Node node)
    {
        if (node is not (Label or BaseButton or LineEdit or RichTextLabel or ItemList or Tree or TabBar or TextEdit or MenuListBox or PickerGrid))
            return;
        var item = (CanvasItem)node;
        if (item.Material is null && !item.UseParentMaterial) item.Material = Material;
    }

    /// <summary>Applies to everything already under <paramref name="root"/>.</summary>
    public static void ApplyTree(Node root)
    {
        Apply(root);
        foreach (Node child in root.GetChildren()) ApplyTree(child);
    }
}
