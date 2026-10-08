using System;
using Godot;

namespace VortexArena.Game.Client;

/// <summary>
/// The frame buffer of a legacy session holds DISPLAY values, as DarkPlaces' does.
///
/// <para>DarkPlaces with Xonotic's default <c>vid_sRGB 0</c> never converts a colour: texels are multiplied as
/// stored, and every blend - an additive water surface, a smoke particle, a decal, fog, a dynamic light - is
/// computed on the values the screen will show. This engine draws 3D in linear light and encodes at the end,
/// so the same blend on the same data gives another picture: adding in linear light is weaker than adding
/// display values wherever the background is not black (a pool of additive water over pale sand all but
/// vanished), and mixing is stronger.</para>
///
/// <para>The remedy does not touch the engine: while a legacy session runs, its shaders write display values
/// into the 3D buffer unconverted (the global shader parameter <see cref="Uniform"/> tells the shared ones),
/// so the buffer's blending IS DarkPlaces' blending, and the environment's colour-correction table
/// (<see cref="InverseOutputTable"/>) undoes the output transform's encoding once, at the end:
/// tonemap.glsl applies linear_to_srgb, then the table, and the table is srgb_to_linear. The native game
/// never sets any of this and is drawn as before.</para>
/// </summary>
public static class DisplayFramebuffer
{
    /// <summary>Global shader parameter: 1 while the 3D buffer holds display values, 0 for linear light.</summary>
    public static readonly StringName Uniform = "dp_framebuffer";

    /// <summary>True while a legacy session has the 3D buffer on display values. Read where a shader is
    /// generated or a colour is handed to the engine; never true in the native game.</summary>
    public static bool Active { get; private set; }

    public static void Set(bool on)
    {
        Active = on;
        WorldTint.EnsureRegistered();
        RenderingServer.GlobalShaderParameterSet(Uniform, on ? 1f : 0f);
    }

    private static ImageTexture? s_table;

    /// <summary>
    /// The colour-correction table that cancels the output transform's sRGB encoding: sampled at an encoded
    /// value it returns the decoded one. One row of floats; entry i holds the curve at the centre of its
    /// texel, which is where a linearly filtered lookup reads it.
    /// </summary>
    public static ImageTexture InverseOutputTable()
    {
        if (s_table is not null && GodotObject.IsInstanceValid(s_table)) return s_table;
        const int Width = 4096;
        float[] row = new float[Width * 3];
        for (int i = 0; i < Width; i++)
        {
            float value = VortexArena.Formats.Materials.DpColour.ToLinear((i + 0.5f) / Width);
            row[i * 3] = row[i * 3 + 1] = row[i * 3 + 2] = value;
        }
        byte[] bytes = new byte[row.Length * sizeof(float)];
        Buffer.BlockCopy(row, 0, bytes, 0, bytes.Length);
        s_table = ImageTexture.CreateFromImage(Image.CreateFromData(Width, 1, false, Image.Format.Rgbf, bytes));
        return s_table;
    }

    /// <summary>
    /// A colour for an engine property the engine decodes before use (a light's colour, the fog colour):
    /// while the buffer holds display values the colour is encoded first, so that what the engine arrives at
    /// is the display value itself. Unchanged otherwise.
    /// </summary>
    public static Color ForEngine(Color display)
    {
        if (!Active) return display;
        return new Color(Encode(display.R), Encode(display.G), Encode(display.B), display.A);
    }

    private static float Encode(float c) => VortexArena.Formats.Materials.DpColour.ToDisplay(MathF.Max(c, 0f));

    /// <summary>The GDShader helpers a shader of a legacy session ends with (dp_fb): DpColour.ShaderFunctions.</summary>
    public const string ShaderFunctions = VortexArena.Formats.Materials.DpColour.ShaderFunctions;
}
