using System;
using System.Numerics;

namespace VortexArena.Formats.Bsp;

/// <summary>
/// The pure-math half of the GPU light grid: how <see cref="LightGridData"/>'s cells are packed into a 3-D
/// texture, and how a world position maps to a coordinate in it. This is DarkPlaces'
/// <c>mod_q3bsp_lightgrid_texture</c> layout (<c>model_brush.c:6527-6604</c>) with no Godot types in sight, so
/// the addressing — the part that is easy to get subtly wrong and impossible to eyeball on a screenshot — is
/// unit-testable. The Godot-side wrapper (<c>LightGridTexture</c>) does nothing but hand the slices to
/// <c>ImageTexture3D</c> and the columns to a <c>Projection</c>.
///
/// <para><b>Layout.</b> <c>[nx, ny, (nz + 2) × 3]</c>: three stacked blocks of <c>nz + 2</c> z-slices —
/// ambient RGB, directed RGB, and the bent-normal light direction as a signed unit vector stored
/// <c>×127 + 127</c>. Each block has one padding slice below its data and one above. The direction block's
/// padding is the neutral <c>(127,127,127)</c> (decodes to ~zero, i.e. "no direction"); the two colour blocks'
/// padding is black. That is what makes sampling outside the grid degrade to "unlit" rather than to whatever
/// the neighbouring block happens to hold.</para>
///
/// <para><b>Coordinates.</b> DP's transform is, per axis,
/// <c>tc[i] = (quake[i] / cellsize[i] - (imins[i] - bias)) / texturesize[i]</c> with bias 0.5 on x/y and 1.5 on
/// z (z carries the extra padding slice at the bottom of each block). <see cref="GetGodotWorldToTexture"/>
/// folds in the Quake→Godot axis swap <c>quake = (gx, -gz, gy)</c> so the shader can feed a Godot world
/// position straight in.</para>
/// </summary>
public readonly struct LightGridLayout
{
    /// <summary>The grid this layout packs.</summary>
    public LightGridData Grid { get; }

    /// <summary>Texture dimensions.</summary>
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }

    /// <summary>Slices per block (<c>Nz + 2</c>): the grid's z-range plus one padding slice at each end.</summary>
    public int BlockSlices { get; }

    /// <summary>Texels in one z-slice.</summary>
    public int SliceTexels => Width * Height;

    /// <summary>Total packed byte count (RGBA8).</summary>
    public long ByteCount => (long)Width * Height * Depth * 4;

    public LightGridLayout(LightGridData grid)
    {
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Width = grid.Nx;
        Height = grid.Ny;
        BlockSlices = grid.Nz + 2;
        Depth = BlockSlices * 3;
    }

    /// <summary>
    /// The z clamp the sampler must apply BEFORE adding the +1/3 and +2/3 block offsets: the centres of
    /// block 0's first and last DATA slices. DP only clamps the top (<c>min(z, 1/3)</c>) and relies on
    /// clamp-to-edge for the rest, which is right for the base sample but lets a position below the grid drag
    /// the offset samples into the wrong block.
    /// </summary>
    public float ZClampMin => 1.5f / Depth;

    /// <inheritdoc cref="ZClampMin"/>
    public float ZClampMax => (Grid.Nz + 0.5f) / Depth;

    /// <summary>
    /// The Godot-world → normalised-texture-coordinate transform, as the four columns of a column-major
    /// <c>mat4</c> applied as <c>M * vec4(godotPos, 1)</c>.
    /// </summary>
    public void GetGodotWorldToTexture(out Vector4 c0, out Vector4 c1, out Vector4 c2, out Vector4 c3)
    {
        float sx = 1f / Grid.CellSize.X, sy = 1f / Grid.CellSize.Y, sz = 1f / Grid.CellSize.Z;
        // LightGridData.Origin == imins * cellsize by construction, so this recovers DP's integer minimum.
        float iminsX = Grid.Origin.X * sx, iminsY = Grid.Origin.Y * sy, iminsZ = Grid.Origin.Z * sz;

        float ax = sx / Width, ay = sy / Height, az = sz / Depth;
        //   tc.x =  ax*gx          + bx     (quake x =  godot x)
        //   tc.y =       -ay*gz    + by     (quake y = -godot z)
        //   tc.z =        az*gy    + bz     (quake z =  godot y)
        c0 = new Vector4(ax, 0f, 0f, 0f);
        c1 = new Vector4(0f, 0f, az, 0f);
        c2 = new Vector4(0f, -ay, 0f, 0f);
        c3 = new Vector4(
            -(iminsX - 0.5f) / Width,
            -(iminsY - 0.5f) / Height,
            -(iminsZ - 1.5f) / Depth,
            1f);
    }

    /// <summary>Apply <see cref="GetGodotWorldToTexture"/> to a Godot-space position (what the shader does).</summary>
    public Vector3 GodotToTexture(Vector3 godotPos)
    {
        GetGodotWorldToTexture(out Vector4 c0, out Vector4 c1, out Vector4 c2, out Vector4 c3);
        Vector4 r = c0 * godotPos.X + c1 * godotPos.Y + c2 * godotPos.Z + c3;
        return new Vector3(r.X, r.Y, r.Z);
    }

    /// <summary>Quake position → Godot position, mirroring the game's <c>Coords.ToGodot</c>.</summary>
    public static Vector3 QuakeToGodot(Vector3 q) => new(q.X, q.Z, -q.Y);

    /// <summary>
    /// Fill one packed z-slice (RGBA8, <c>Width × Height</c> texels) into <paramref name="dest"/>.
    /// <paramref name="slice"/> runs 0..<see cref="Depth"/>-1 across all three blocks.
    /// </summary>
    public void FillSlice(int slice, Span<byte> dest)
    {
        if (dest.Length < SliceTexels * 4)
            throw new ArgumentException($"slice buffer needs {SliceTexels * 4} bytes", nameof(dest));

        int block = slice / BlockSlices;          // 0 ambient, 1 directed, 2 direction
        int z = slice % BlockSlices - 1;          // data slice 1 holds grid z=0
        bool padding = z < 0 || z >= Grid.Nz;

        if (block < 2)
        {
            int channel = block == 0 ? 0 : 3;     // cell bytes 0..2 ambient, 3..5 directed
            for (int i = 0; i < SliceTexels; i++)
            {
                if (padding)
                {
                    dest[i * 4] = dest[i * 4 + 1] = dest[i * 4 + 2] = 0;   // black: "no light here"
                }
                else
                {
                    int cell = (z * SliceTexels + i) * 8;
                    dest[i * 4 + 0] = Grid.CellByte(cell + channel + 0);
                    dest[i * 4 + 1] = Grid.CellByte(cell + channel + 1);
                    dest[i * 4 + 2] = Grid.CellByte(cell + channel + 2);
                }
                dest[i * 4 + 3] = 255;
            }
            return;
        }

        for (int i = 0; i < SliceTexels; i++)
        {
            if (padding)
            {
                // Neutral: decodes to ~(0,0,0), so a clamped sample loses the lobe instead of inventing one.
                dest[i * 4] = dest[i * 4 + 1] = dest[i * 4 + 2] = 127;
            }
            else
            {
                int cell = (z * SliceTexels + i) * 8;
                DecodeDirection(Grid.CellByte(cell + 6), Grid.CellByte(cell + 7), out Vector3 d);
                dest[i * 4 + 0] = Encode(d.X);
                dest[i * 4 + 1] = Encode(d.Y);
                dest[i * 4 + 2] = Encode(d.Z);
            }
            dest[i * 4 + 3] = 255;
        }
    }

    /// <summary>
    /// A cell's baked light direction, in QUAKE axes. Cell byte 6 is pitch, byte 7 is yaw
    /// (<c>q3dlightgrid_t</c>, DP <c>model_q3bsp.h:226-227</c>), each a 256th of a turn — DP indexes a
    /// 256-entry sine table with the raw byte (<c>mod_md3_sin[i] = sin(i·2π/256)</c>, <c>model_alias.c:201</c>),
    /// and <c>+64</c> fetches a cosine. Same assignment ioquake3's <c>R_SetupEntityLightingGrid</c> uses.
    /// </summary>
    public static void DecodeDirection(byte pitchByte, byte yawByte, out Vector3 quakeDir)
    {
        float pitch = pitchByte * (MathF.PI * 2f / 256f);
        float yaw = yawByte * (MathF.PI * 2f / 256f);
        float sp = MathF.Sin(pitch);
        quakeDir = new Vector3(MathF.Cos(yaw) * sp, MathF.Sin(yaw) * sp, MathF.Cos(pitch));
    }

    /// <summary>DP's <c>×127 + 127</c> signed-unit-vector encode.</summary>
    public static byte Encode(float v) => (byte)Math.Clamp((int)MathF.Round(v * 127f + 127f), 0, 255);

    /// <summary>Inverse of <see cref="Encode"/> (what the shader's <c>×2-1</c> does).</summary>
    public static float Decode(byte b) => b / 255f * 2f - 1f;

    // =================================================================================================
    //  Verification helpers — a CPU stand-in for the GPU sampler, so tests can assert that what the shader
    //  will fetch at a world position equals what LightGridData.Sample says is there.
    // =================================================================================================

    /// <summary>Nearest-texel fetch of the packed data at a normalised texture coordinate (clamp-to-edge).</summary>
    public (byte R, byte G, byte B) FetchNearest(Vector3 tc)
    {
        int x = Math.Clamp((int)MathF.Floor(tc.X * Width), 0, Width - 1);
        int y = Math.Clamp((int)MathF.Floor(tc.Y * Height), 0, Height - 1);
        int z = Math.Clamp((int)MathF.Floor(tc.Z * Depth), 0, Depth - 1);

        Span<byte> slice = new byte[SliceTexels * 4];
        FillSlice(z, slice);
        int o = (y * Width + x) * 4;
        return (slice[o], slice[o + 1], slice[o + 2]);
    }

    /// <summary>
    /// The three block samples the shader takes for a Godot world position: ambient, directed, and the
    /// direction decoded back to a QUAKE-axis vector. Nearest-texel (so a test can hit exact grid points and
    /// compare against raw cell bytes without trilinear blending muddying the comparison).
    /// </summary>
    public void SampleNearest(Vector3 godotPos, out Vector3 ambient, out Vector3 directed, out Vector3 quakeDir)
    {
        Vector3 tc = GodotToTexture(godotPos);
        float z = Math.Clamp(tc.Z, ZClampMin, ZClampMax);

        (byte ar, byte ag, byte ab) = FetchNearest(new Vector3(tc.X, tc.Y, z));
        (byte dr, byte dg, byte db) = FetchNearest(new Vector3(tc.X, tc.Y, z + 1f / 3f));
        (byte nr, byte ng, byte nb) = FetchNearest(new Vector3(tc.X, tc.Y, z + 2f / 3f));

        ambient = new Vector3(ar, ag, ab);
        directed = new Vector3(dr, dg, db);
        quakeDir = new Vector3(Decode(nr), Decode(ng), Decode(nb));
    }
}
