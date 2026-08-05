using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The GPU light grid's addressing (F1-B). These are the assertions a screenshot cannot make: a grid packed one
/// slice out, with x/y transposed, or with the Quake→Godot axis swap inverted still renders as perfectly
/// plausible lighting — just the wrong lighting, subtly, everywhere. So the rule these pin down is:
///
/// <b>what the shader fetches at a world position must equal what <see cref="LightGridData.Sample"/> says is
/// there.</b>
///
/// <see cref="LightGridLayout.SampleNearest"/> is a CPU stand-in for the GPU sampler — same packing, same
/// matrix, same z clamp, nearest-texel instead of trilinear so exact grid points can be compared against raw
/// cell bytes without the blend muddying it.
/// </summary>
public class LightGridLayoutTests
{
    private static readonly string DataDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../data"));

    // ---- synthetic: a grid whose every cell is distinguishable, so a mis-index cannot pass -----------

    /// <summary>
    /// Build a grid with a KNOWN, position-dependent value in every cell. Ambient encodes the cell's (x,y,z)
    /// index directly, so any transposition or off-by-one slice shows up as a wrong number rather than as
    /// "some light".
    /// </summary>
    private static LightGridData BuildSynthetic(int nx, int ny, int nz, out Vector3 mins, out Vector3 maxs)
    {
        var cell = new Vector3(64f, 64f, 128f);
        // LightGridData.Build derives origin = size*ceil(mins/size) and count from maxs, so choose bounds that
        // land exactly on the grid: origin at (0,0,0) and count = n.
        mins = Vector3.Zero;
        maxs = new Vector3(cell.X * (nx - 1), cell.Y * (ny - 1), cell.Z * (nz - 1));

        var bytes = new byte[nx * ny * nz * 8];
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    int o = ((z * ny + y) * nx + x) * 8;
                    bytes[o + 0] = (byte)(x * 7 + 1);      // ambient R = f(x)
                    bytes[o + 1] = (byte)(y * 11 + 2);     // ambient G = f(y)
                    bytes[o + 2] = (byte)(z * 23 + 3);     // ambient B = f(z)
                    bytes[o + 3] = (byte)(x * 3 + 40);     // directed R
                    bytes[o + 4] = (byte)(y * 5 + 50);     // directed G
                    bytes[o + 5] = (byte)(z * 9 + 60);     // directed B
                    bytes[o + 6] = (byte)((x * 13 + z * 5) & 0xFF);  // pitch
                    bytes[o + 7] = (byte)((y * 17 + x * 3) & 0xFF);  // yaw
                }

        LightGridData? g = LightGridData.Build(mins, maxs, bytes);
        Assert.NotNull(g);
        Assert.Equal(nx, g!.Nx);
        Assert.Equal(ny, g.Ny);
        Assert.Equal(nz, g.Nz);
        return g;
    }

    [Fact]
    public void Layout_Dimensions_Match_Dp_Packing()
    {
        LightGridData g = BuildSynthetic(5, 7, 3, out _, out _);
        var layout = new LightGridLayout(g);

        Assert.Equal(5, layout.Width);
        Assert.Equal(7, layout.Height);
        Assert.Equal(3 + 2, layout.BlockSlices);          // one padding slice at each end
        Assert.Equal((3 + 2) * 3, layout.Depth);          // three stacked blocks
        Assert.Equal(5L * 7 * 15 * 4, layout.ByteCount);
    }

    /// <summary>
    /// Every grid point, fetched through the packed texture at that point's WORLD position, must return that
    /// cell's own bytes. This is the whole ballgame: it pins the matrix, the axis swap, the z bias, the block
    /// offsets and the slice indexing simultaneously.
    /// </summary>
    [Fact]
    public void Every_Grid_Point_Fetches_Its_Own_Cell()
    {
        const int nx = 5, ny = 7, nz = 3;
        LightGridData g = BuildSynthetic(nx, ny, nz, out _, out _);
        var layout = new LightGridLayout(g);

        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    // The world position of this grid point, in Quake space, then into Godot space the way
                    // the renderer holds it.
                    var quake = new Vector3(
                        g.Origin.X + x * g.CellSize.X,
                        g.Origin.Y + y * g.CellSize.Y,
                        g.Origin.Z + z * g.CellSize.Z);
                    Vector3 godot = LightGridLayout.QuakeToGodot(quake);

                    layout.SampleNearest(godot, out Vector3 amb, out Vector3 dir, out Vector3 qdir);

                    Assert.Equal(x * 7 + 1, (int)amb.X);
                    Assert.Equal(y * 11 + 2, (int)amb.Y);
                    Assert.Equal(z * 23 + 3, (int)amb.Z);
                    Assert.Equal(x * 3 + 40, (int)dir.X);
                    Assert.Equal(y * 5 + 50, (int)dir.Y);
                    Assert.Equal(z * 9 + 60, (int)dir.Z);

                    // The direction survives the encode/decode round trip to within a byte of precision
                    // (1/127 per component), and it is in QUAKE axes on both sides.
                    LightGridLayout.DecodeDirection(
                        (byte)((x * 13 + z * 5) & 0xFF), (byte)((y * 17 + x * 3) & 0xFF), out Vector3 expect);
                    Assert.True((qdir - expect).Length() < 0.02f,
                        $"cell ({x},{y},{z}) direction {qdir} != {expect}");
                }
    }

    /// <summary>
    /// A position ABOVE or BELOW the grid must clamp into the grid's own data, never into a neighbouring
    /// block. DP clamps only the top and leans on clamp-to-edge for the rest, which lets a below-grid position
    /// drag the +1/3 and +2/3 samples into the wrong block — the case this port fixes.
    /// </summary>
    [Fact]
    public void Outside_The_Grid_Clamps_Into_The_Right_Block()
    {
        const int nx = 4, ny = 4, nz = 3;
        LightGridData g = BuildSynthetic(nx, ny, nz, out _, out _);
        var layout = new LightGridLayout(g);

        // Far below and far above the grid in Quake Z (Godot Y).
        foreach (float quakeZ in new[] { g.Origin.Z - 10_000f, g.Origin.Z + 10_000f })
        {
            var quake = new Vector3(g.Origin.X, g.Origin.Y, quakeZ);
            layout.SampleNearest(LightGridLayout.QuakeToGodot(quake), out Vector3 amb, out Vector3 dir, out _);

            // Whatever it clamps to must be a REAL cell of column (0,0) — i.e. one of the z-layer values —
            // and never the black padding, and never a directed value showing up in the ambient slot.
            int[] ambB = Enumerable.Range(0, nz).Select(z => z * 23 + 3).ToArray();
            int[] dirB = Enumerable.Range(0, nz).Select(z => z * 9 + 60).ToArray();
            Assert.Contains((int)amb.Z, ambB);
            Assert.Contains((int)dir.Z, dirB);
            Assert.Equal(1, (int)amb.X);    // x=0 column
            Assert.Equal(2, (int)amb.Y);    // y=0 row
        }
    }

    // ---- real data: the packed fetch must agree with the CPU sampler on a shipped map ----------------

    /// <summary>
    /// On a real map, the packed-texture fetch and <see cref="LightGridData.Sample"/> must agree. Sample is
    /// trilinear and the fetch here is nearest, so they are compared AT grid points, where trilinear collapses
    /// to the nearest cell and the two must produce the same numbers.
    /// </summary>
    [Fact]
    public void Real_Map_Packed_Fetch_Agrees_With_Cpu_Sample()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountContentRoot(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault(p => p.Contains("stormkeep"));
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        LightGridData? grid = bsp.LightGrid;
        Assert.NotNull(grid);
        var layout = new LightGridLayout(grid!);

        int compared = 0, lit = 0;
        // Stride through the grid rather than visiting every cell — SampleNearest rebuilds a slice per fetch,
        // which is fine for a few hundred probes and far too slow for a few hundred thousand.
        for (int z = 0; z < grid!.Nz; z += Math.Max(1, grid.Nz / 4))
            for (int y = 0; y < grid.Ny; y += Math.Max(1, grid.Ny / 6))
                for (int x = 0; x < grid.Nx; x += Math.Max(1, grid.Nx / 6))
                {
                    var quake = new Vector3(
                        grid.Origin.X + x * grid.CellSize.X,
                        grid.Origin.Y + y * grid.CellSize.Y,
                        grid.Origin.Z + z * grid.CellSize.Z);

                    grid.Sample(quake, out Vector3 cpuAmb, out Vector3 cpuDir, out Vector3 cpuDirection);
                    layout.SampleNearest(LightGridLayout.QuakeToGodot(quake),
                        out Vector3 gpuAmb, out Vector3 gpuDir, out Vector3 gpuDirection);

                    Assert.True((cpuAmb - gpuAmb).Length() < 1.5f,
                        $"ambient mismatch at grid ({x},{y},{z}): cpu={cpuAmb} packed={gpuAmb}");
                    Assert.True((cpuDir - gpuDir).Length() < 1.5f,
                        $"directed mismatch at grid ({x},{y},{z}): cpu={cpuDir} packed={gpuDir}");

                    // Sample normalises its direction and returns zero for a black cell; the packed fetch
                    // returns the raw unit vector. Compare only where there is a direction to compare.
                    if (cpuDirection.LengthSquared() > 0.5f && gpuDirection.LengthSquared() > 0.25f)
                    {
                        Vector3 a = Vector3.Normalize(cpuDirection), b = Vector3.Normalize(gpuDirection);
                        Assert.True(Vector3.Dot(a, b) > 0.98f,
                            $"direction mismatch at grid ({x},{y},{z}): cpu={a} packed={b}");
                    }

                    compared++;
                    if (cpuAmb.X + cpuAmb.Y + cpuAmb.Z > 1f) lit++;
                }

        Assert.True(compared > 20, $"only {compared} probes — the stride collapsed");
        // If every probe were black the comparison above would be vacuous.
        Assert.True(lit > 0, "every probe was black — the grid or the indexing is wrong");
    }
}
