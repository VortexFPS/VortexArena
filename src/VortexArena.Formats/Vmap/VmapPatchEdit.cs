using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>The Modify dialog's operations on a patch's control grid.</summary>
public enum PatchOperation
{
    /// <summary>Reverse the column order, flipping which side the surface faces.</summary>
    Invert,

    /// <summary>Swap rows and columns.</summary>
    Transpose,

    /// <summary>Re-space the interior control points evenly along each row.</summary>
    RedisperseRows,

    /// <summary>Re-space the interior control points evenly down each column.</summary>
    RedisperseColumns,

    /// <summary>Add two rows, subdividing the grid vertically.</summary>
    InsertRows,

    /// <summary>Add two columns, subdividing the grid horizontally.</summary>
    InsertColumns,

    /// <summary>Drop two rows.</summary>
    RemoveRows,

    /// <summary>Drop two columns.</summary>
    RemoveColumns,
}

/// <summary>
/// Control-grid transforms for the patch Modify dialog (design doc §11.9) — Radiant's Matrix and
/// Rows/Columns menus.
///
/// Pure functions over the grid, kept out of the op so both halves are testable without a document: the op
/// writes the result, these decide what it should be.
///
/// Every operation has to leave the grid ODD in both dimensions and at least 3x3, because that is what a
/// biquadratic patch IS — an even count would leave a trailing control point belonging to no patch, which the
/// format cannot express and the tessellator would read past.
/// </summary>
public static class VmapPatchEdit
{
    /// <summary>Human-readable name for the dialog.</summary>
    public static string Label(PatchOperation op) => op switch
    {
        PatchOperation.Invert => "Invert (flip facing)",
        PatchOperation.Transpose => "Transpose (swap rows/columns)",
        PatchOperation.RedisperseRows => "Redisperse rows",
        PatchOperation.RedisperseColumns => "Redisperse columns",
        PatchOperation.InsertRows => "Insert rows",
        PatchOperation.InsertColumns => "Insert columns",
        PatchOperation.RemoveRows => "Remove rows",
        PatchOperation.RemoveColumns => "Remove columns",
        _ => op.ToString(),
    };

    /// <summary>What the operation is for.</summary>
    public static string Describe(PatchOperation op) => op switch
    {
        PatchOperation.Invert =>
            "Reverses the column order, so the surface faces the other way. The fix when a patch is invisible "
            + "from the side you are standing on.",
        PatchOperation.Transpose =>
            "Swaps rows and columns. Changes which way the grid runs without moving any point in space.",
        PatchOperation.RedisperseRows =>
            "Spaces each row's interior points evenly between its ends — straightens a curve that was dragged "
            + "out of shape.",
        PatchOperation.RedisperseColumns => "The same, down each column.",
        PatchOperation.InsertRows => "Adds two rows, giving more control points to sculpt with.",
        PatchOperation.InsertColumns => "Adds two columns.",
        PatchOperation.RemoveRows => "Drops two rows. Refused at the 3-row minimum.",
        PatchOperation.RemoveColumns => "Drops two columns. Refused at the 3-column minimum.",
        _ => "",
    };

    /// <summary>
    /// Apply an operation, returning a NEW patch (same id and material) or null when it cannot be done —
    /// which is the honest answer for removing a row from a 3-row grid.
    /// </summary>
    public static VmapPatch? Apply(VmapPatch patch, PatchOperation op)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.IsValid)
            return null;

        return op switch
        {
            PatchOperation.Invert => Invert(patch),
            PatchOperation.Transpose => Transpose(patch),
            PatchOperation.RedisperseRows => Redisperse(patch, rows: true),
            PatchOperation.RedisperseColumns => Redisperse(patch, rows: false),
            PatchOperation.InsertRows => Insert(patch, rows: true),
            PatchOperation.InsertColumns => Insert(patch, rows: false),
            PatchOperation.RemoveRows => Remove(patch, rows: true),
            PatchOperation.RemoveColumns => Remove(patch, rows: false),
            _ => null,
        };
    }

    /// <summary>
    /// Reverse each row, flipping the surface's facing.
    ///
    /// The UVs travel with their control points rather than being reversed separately: the texture is attached
    /// to the surface, and a flip that moved the geometry but not the mapping would mirror the artwork.
    /// </summary>
    private static VmapPatch Invert(VmapPatch p)
    {
        VmapPatch copy = Blank(p, p.Width, p.Height);
        for (int row = 0; row < p.Height; row++)
            for (int col = 0; col < p.Width; col++)
            {
                int src = row * p.Width + (p.Width - 1 - col);
                copy.Controls.Add(p.Controls[src]);
                copy.ControlUvs.Add(p.ControlUvs[src]);
            }
        return copy;
    }

    private static VmapPatch Transpose(VmapPatch p)
    {
        VmapPatch copy = Blank(p, p.Height, p.Width);
        for (int row = 0; row < p.Width; row++)
            for (int col = 0; col < p.Height; col++)
            {
                int src = col * p.Width + row;
                copy.Controls.Add(p.Controls[src]);
                copy.ControlUvs.Add(p.ControlUvs[src]);
            }
        return copy;
    }

    /// <summary>Space the interior points of each row (or column) evenly between its two ends.</summary>
    private static VmapPatch Redisperse(VmapPatch p, bool rows)
    {
        VmapPatch copy = Clone(p);

        int outer = rows ? p.Height : p.Width;
        int inner = rows ? p.Width : p.Height;
        if (inner < 3)
            return copy;

        for (int o = 0; o < outer; o++)
        {
            int firstIdx = Index(p, rows, o, 0);
            int lastIdx = Index(p, rows, o, inner - 1);
            Vector3 a = p.Controls[firstIdx];
            Vector3 b = p.Controls[lastIdx];

            for (int i = 1; i < inner - 1; i++)
            {
                float t = i / (float)(inner - 1);
                copy.Controls[Index(p, rows, o, i)] = a + (b - a) * t;
            }
        }
        return copy;
    }

    /// <summary>
    /// Add two rows (or columns) by subdividing: every existing span gains a midpoint.
    ///
    /// Subdividing rather than appending is what keeps the SHAPE. Adding two rows on the end would extend the
    /// patch into space it did not occupy; splitting each span leaves the surface exactly where it was, with
    /// more points to sculpt it by — which is what the mapper asked for.
    /// </summary>
    private static VmapPatch Insert(VmapPatch p, bool rows)
    {
        int oldInner = rows ? p.Height : p.Width;
        int newInner = oldInner + 2;

        int width = rows ? p.Width : newInner;
        int height = rows ? newInner : p.Height;
        VmapPatch copy = Blank(p, width, height);

        int outer = rows ? p.Width : p.Height;
        var controls = new Vector3[width * height];
        var uvs = new Vector2[width * height];

        for (int o = 0; o < outer; o++)
        {
            // Resample the old line of points at the new, denser parameter positions.
            for (int i = 0; i < newInner; i++)
            {
                float t = i / (float)(newInner - 1) * (oldInner - 1);
                int lo = (int)MathF.Floor(t);
                int hi = Math.Min(lo + 1, oldInner - 1);
                float f = t - lo;

                Vector3 a = p.Controls[Index(p, !rows, o, lo)];
                Vector3 b = p.Controls[Index(p, !rows, o, hi)];
                Vector2 ua = p.ControlUvs[Index(p, !rows, o, lo)];
                Vector2 ub = p.ControlUvs[Index(p, !rows, o, hi)];

                int dst = rows ? i * width + o : o * width + i;
                controls[dst] = a + (b - a) * f;
                uvs[dst] = ua + (ub - ua) * f;
            }
        }

        copy.Controls.AddRange(controls);
        copy.ControlUvs.AddRange(uvs);
        return copy;
    }

    /// <summary>Drop two rows (or columns), resampling the survivors so the shape holds. Null at the minimum.</summary>
    private static VmapPatch? Remove(VmapPatch p, bool rows)
    {
        int oldInner = rows ? p.Height : p.Width;
        if (oldInner <= 3)
            return null;   // already at the minimum a biquadratic patch can be

        int newInner = oldInner - 2;
        int width = rows ? p.Width : newInner;
        int height = rows ? newInner : p.Height;
        VmapPatch copy = Blank(p, width, height);

        int outer = rows ? p.Width : p.Height;
        var controls = new Vector3[width * height];
        var uvs = new Vector2[width * height];

        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < newInner; i++)
            {
                float t = i / (float)(newInner - 1) * (oldInner - 1);
                int lo = (int)MathF.Floor(t);
                int hi = Math.Min(lo + 1, oldInner - 1);
                float f = t - lo;

                Vector3 a = p.Controls[Index(p, !rows, o, lo)];
                Vector3 b = p.Controls[Index(p, !rows, o, hi)];
                Vector2 ua = p.ControlUvs[Index(p, !rows, o, lo)];
                Vector2 ub = p.ControlUvs[Index(p, !rows, o, hi)];

                int dst = rows ? i * width + o : o * width + i;
                controls[dst] = a + (b - a) * f;
                uvs[dst] = ua + (ub - ua) * f;
            }
        }

        copy.Controls.AddRange(controls);
        copy.ControlUvs.AddRange(uvs);
        return copy;
    }

    /// <summary>
    /// Index into the control grid. <paramref name="alongRow"/> true walks a ROW (outer = row index, inner =
    /// column); false walks a COLUMN.
    /// </summary>
    private static int Index(VmapPatch p, bool alongRow, int outer, int inner)
        => alongRow ? outer * p.Width + inner : inner * p.Width + outer;

    private static VmapPatch Blank(VmapPatch from, int width, int height) => new()
    {
        Id = from.Id,
        Material = from.Material,
        Width = width,
        Height = height,
        SurfaceFlags = from.SurfaceFlags,
        ContentFlags = from.ContentFlags,
    };

    private static VmapPatch Clone(VmapPatch p)
    {
        VmapPatch copy = Blank(p, p.Width, p.Height);
        copy.Controls.AddRange(p.Controls);
        copy.ControlUvs.AddRange(p.ControlUvs);
        return copy;
    }
}
