using System;
using Godot;
using VortexArena.Formats.Bsp;

namespace VortexArena.Game.Loaders;

/// <summary>
/// The BSP light grid packed into a single 3-D texture, so every model surface can sample the map's baked
/// lighting <b>per fragment</b> — the port of DarkPlaces' <c>mod_q3bsp_lightgrid_texture</c> path
/// (<c>model_brush.c:6527-6604</c>), which is what DP does <b>by default</b> (the cvar's default is 1).
///
/// <para><b>Why this exists.</b> The light grid (BSP lump 15, see <see cref="LightGridData"/>) is a uniform 3-D
/// array of baked light probes over the map. DarkPlaces has two ways to consume it. The older one,
/// <c>Mod_Q3BSP_LightPoint</c>, samples it once at an entity's origin and shades the whole model with that one
/// value — which is what this port did, and only for the first-person viewmodel. The newer, default one uploads
/// the whole grid as a 3-D texture and samples it in the fragment shader, so a player straddling a light
/// boundary has dark legs and a bright torso instead of one averaged colour, and a model crossing a cell
/// boundary transitions smoothly instead of popping.</para>
///
/// <para><b>All of the layout and coordinate maths lives in <see cref="LightGridLayout"/></b> (in
/// <c>VortexArena.Formats</c>, no Godot types), because that is the part that is easy to get subtly wrong and
/// impossible to check on a screenshot — a light grid addressed one slice out still looks like plausible
/// lighting. This class is the Godot wrapper: it hands the layout's slices to an <see cref="ImageTexture3D"/>
/// and its columns to a <see cref="Projection"/>, and adds the size cap.</para>
/// </summary>
public sealed class LightGridTexture
{
    /// <summary>Refuse to build past this many texels (~64 MB at RGBA8). A grid this large means a map whose
    /// bounds are wildly larger than its playable area; the per-entity CPU sample still works there.</summary>
    private const long MaxTexels = 16L * 1024 * 1024;

    /// <summary>The packed 3-D texture: <c>[nx, ny, (nz+2)*3]</c>, RGBA8, no mipmaps.</summary>
    public ImageTexture3D Texture { get; }

    /// <summary>Godot world position → normalised light-grid texture coordinate (Quake axis swap folded in).</summary>
    public Projection WorldToTexture { get; }

    /// <summary>The layout this was built from — carries the dims and the sampler's z clamp.</summary>
    public LightGridLayout Layout { get; }

    public int Width => Layout.Width;
    public int Height => Layout.Height;
    public int Depth => Layout.Depth;

    /// <summary>Approximate VRAM footprint in bytes (RGBA8, no mips).</summary>
    public long Bytes => Layout.ByteCount;

    private LightGridTexture(ImageTexture3D texture, Projection worldToTexture, LightGridLayout layout)
    {
        Texture = texture;
        WorldToTexture = worldToTexture;
        Layout = layout;
    }

    /// <summary>
    /// Pack <paramref name="grid"/> into the DP-layout 3-D texture. Returns null when there is no grid, when
    /// the grid would exceed the texel cap, or when the driver refuses the upload — all of which leave the
    /// caller on the per-entity CPU path.
    /// </summary>
    public static LightGridTexture? Build(LightGridData? grid)
    {
        if (grid is null || grid.Nx <= 0 || grid.Ny <= 0 || grid.Nz <= 0)
            return null;

        var layout = new LightGridLayout(grid);
        long texels = (long)layout.Width * layout.Height * layout.Depth;
        if (texels > MaxTexels)
        {
            GD.PushWarning($"[LightGrid] grid {grid.Nx}x{grid.Ny}x{grid.Nz} would need {texels} texels " +
                           $"({texels * 4 / (1024 * 1024)} MB) — over the {MaxTexels * 4 / (1024 * 1024)} MB " +
                           "cap; falling back to per-entity sampling.");
            return null;
        }

        var slices = new Godot.Collections.Array<Image>();
        int sliceBytes = layout.SliceTexels * 4;
        for (int s = 0; s < layout.Depth; s++)
        {
            // A fresh buffer per slice: Image.CreateFromData keeps the array, so a reused scratch would
            // leave every slice showing the last one's contents.
            var buf = new byte[sliceBytes];
            layout.FillSlice(s, buf);
            slices.Add(Image.CreateFromData(layout.Width, layout.Height, false, Image.Format.Rgba8, buf));
        }

        var tex = new ImageTexture3D();
        Error err = tex.Create(Image.Format.Rgba8, layout.Width, layout.Height, layout.Depth,
                               useMipmaps: false, slices);
        if (err != Error.Ok)
        {
            GD.PushWarning($"[LightGrid] ImageTexture3D.Create failed ({err}); falling back to per-entity sampling.");
            return null;
        }

        layout.GetGodotWorldToTexture(
            out System.Numerics.Vector4 c0, out System.Numerics.Vector4 c1,
            out System.Numerics.Vector4 c2, out System.Numerics.Vector4 c3);
        var m = new Projection(V(c0), V(c1), V(c2), V(c3));
        return new LightGridTexture(tex, m, layout);
    }

    private static Vector4 V(System.Numerics.Vector4 v) => new(v.X, v.Y, v.Z, v.W);
}
