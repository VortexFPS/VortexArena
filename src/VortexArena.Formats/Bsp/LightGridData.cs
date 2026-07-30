using System;
using System.Numerics;

namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// The Q3 BSP <b>lightgrid</b> (lump 15): a uniform 3-D grid of baked light probes covering the world model,
/// one 8-byte cell per point — <c>ambient RGB, directed RGB, direction (longitude, latitude) bytes</c>. This is
/// how DarkPlaces lights every MODEL (players, items, viewmodels): it samples the grid at the entity origin
/// (<c>Mod_Q3BSP_LightPoint</c>, model_brush.c) so a gun in a dark red corridor is dim and red-tinted while the
/// same gun in a bright yard pops — the position-based model lighting the port lacked (playtest r12 "weapons
/// look dull": everything was lit by one global sun + a flat ambient).
///
/// Grid layout (ioquake3 <c>R_SetupEntityLightingGrid</c> / DP <c>Mod_Q3BSP_LoadLightGrid</c>):
/// cell size 64×64×128 (the q3map default; worldspawn "gridsize" can override, unused by stock Xonotic maps),
/// origin/counts derived from the world model's bounds:
/// <c>origin[i] = size[i]*ceil(mins[i]/size[i])</c>, <c>count[i] = (size[i]*floor(maxs[i]/size[i]) −
/// origin[i])/size[i] + 1</c>, cells ordered x-fastest then y then z.
/// </summary>
public sealed class LightGridData
{
    /// <summary>World-space position of grid point (0,0,0), Quake coords.</summary>
    public Vector3 Origin { get; }

    /// <summary>Grid cell spacing (q3map default 64,64,128).</summary>
    public Vector3 CellSize { get; }

    /// <summary>Grid point counts per axis.</summary>
    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }

    /// <summary>The raw cells, 8 bytes per point: ambient RGB, directed RGB, dir longitude, dir latitude.</summary>
    private readonly byte[] _cells;

    /// <summary>
    /// The map's average lit-cell intensity (mean of <c>ambient + 0.5·directed</c> luma over every NON-BLACK
    /// cell, 0..255 scale). Black cells are skipped — they're the grid points inside the void/solid, which
    /// dominate most maps and would drag the average toward zero. Callers normalize their samples by this so
    /// a modulate of 1.0 = "an averagely-lit spot on THIS map" regardless of how bright the map was compiled
    /// (playtest r13: a fixed /128 scale mostly darkened — typical Xonotic grids average well below 128).
    /// </summary>
    public float AverageIntensity { get; }

    private LightGridData(Vector3 origin, Vector3 cellSize, int nx, int ny, int nz, byte[] cells)
    {
        Origin = origin; CellSize = cellSize; Nx = nx; Ny = ny; Nz = nz; _cells = cells;

        double sum = 0; long lit = 0;
        for (int o = 0; o < cells.Length; o += 8)
        {
            // Luma of ambient + half the directed term (the same combination Sample consumers use).
            float v = (cells[o] + cells[o + 1] + cells[o + 2]) / 3f
                    + 0.5f * (cells[o + 3] + cells[o + 4] + cells[o + 5]) / 3f;
            if (v <= 1f)
                continue; // void/solid cell
            sum += v; lit++;
        }
        AverageIntensity = lit > 0 ? (float)(sum / lit) : 128f;
    }

    /// <summary>
    /// Derive the grid dims from the world model bounds and validate the lump length against them (DP warns
    /// and disables the grid on a mismatch — a mismatched grid samples garbage). Returns null when the lump
    /// is empty or inconsistent.
    /// </summary>
    public static LightGridData? Build(Vector3 worldMins, Vector3 worldMaxs, byte[] lump)
    {
        if (lump.Length < 8)
            return null;
        var size = new Vector3(64f, 64f, 128f);
        var origin = new Vector3(
            size.X * MathF.Ceiling(worldMins.X / size.X),
            size.Y * MathF.Ceiling(worldMins.Y / size.Y),
            size.Z * MathF.Ceiling(worldMins.Z / size.Z));
        int nx = (int)((size.X * MathF.Floor(worldMaxs.X / size.X) - origin.X) / size.X) + 1;
        int ny = (int)((size.Y * MathF.Floor(worldMaxs.Y / size.Y) - origin.Y) / size.Y) + 1;
        int nz = (int)((size.Z * MathF.Floor(worldMaxs.Z / size.Z) - origin.Z) / size.Z) + 1;
        if (nx <= 0 || ny <= 0 || nz <= 0)
            return null;
        long expected = (long)nx * ny * nz * 8;
        if (expected != lump.Length)
            return null; // dims don't match the data (unusual gridsize?) — disable rather than sample garbage
        return new LightGridData(origin, size, nx, ny, nz, lump);
    }

    /// <summary>
    /// Trilinearly sample the grid at a world position (Quake coords) — the DP <c>Mod_Q3BSP_LightPoint</c>
    /// blend of the 8 surrounding cells. Outputs are 0..255-scale RGB (the raw q3map values; callers apply
    /// their own modulate) plus the blended directed-light direction (normalized, Quake axes; zero when the
    /// directed term is black).
    /// </summary>
    public void Sample(Vector3 pos, out Vector3 ambient, out Vector3 directed, out Vector3 direction)
    {
        // Continuous grid coords, clamped so any world position (or an out-of-grid one) samples the edge.
        float gx = (pos.X - Origin.X) / CellSize.X;
        float gy = (pos.Y - Origin.Y) / CellSize.Y;
        float gz = (pos.Z - Origin.Z) / CellSize.Z;
        gx = Math.Clamp(gx, 0f, Nx - 1.0001f);
        gy = Math.Clamp(gy, 0f, Ny - 1.0001f);
        gz = Math.Clamp(gz, 0f, Nz - 1.0001f);
        int x0 = (int)gx, y0 = (int)gy, z0 = (int)gz;
        float fx = gx - x0, fy = gy - y0, fz = gz - z0;

        ambient = Vector3.Zero;
        directed = Vector3.Zero;
        direction = Vector3.Zero;

        for (int corner = 0; corner < 8; corner++)
        {
            int cx = x0 + (corner & 1);
            int cy = y0 + ((corner >> 1) & 1);
            int cz = z0 + ((corner >> 2) & 1);
            if (cx >= Nx || cy >= Ny || cz >= Nz)
                continue;
            float w = ((corner & 1) != 0 ? fx : 1f - fx)
                    * (((corner >> 1) & 1) != 0 ? fy : 1f - fy)
                    * (((corner >> 2) & 1) != 0 ? fz : 1f - fz);
            if (w <= 0f)
                continue;

            int o = ((cz * Ny + cy) * Nx + cx) * 8;
            ambient += w * new Vector3(_cells[o], _cells[o + 1], _cells[o + 2]);
            directed += w * new Vector3(_cells[o + 3], _cells[o + 4], _cells[o + 5]);

            // Direction bytes: longitude/latitude on a unit sphere (ioq3 R_SetupEntityLightingGrid:
            // lat = data[7]·2π/255, lng = data[6]·2π/255; dir = (cos lat·sin lng, sin lat·sin lng, cos lng)).
            float lng = _cells[o + 6] * (MathF.PI * 2f / 255f);
            float lat = _cells[o + 7] * (MathF.PI * 2f / 255f);
            direction += w * new Vector3(
                MathF.Cos(lat) * MathF.Sin(lng),
                MathF.Sin(lat) * MathF.Sin(lng),
                MathF.Cos(lng));
        }

        float len = direction.Length();
        direction = len > 1e-5f ? direction / len : Vector3.Zero;
    }
}
