using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>What a paint stamp does to the weight already under it.</summary>
public enum VmapPaintMode
{
    /// <summary>Raise the weight towards full — the ordinary brush.</summary>
    Add,

    /// <summary>Lower it towards zero — the eraser.</summary>
    Subtract,

    /// <summary>Drive it to the stroke's strength outright, ignoring what was there.</summary>
    Set,

    /// <summary>Pull each texel towards the average of its neighbours, softening an edge.</summary>
    Smooth,
}

/// <summary>
/// The rasterizer behind the paint tool (backlog F3): stamp a soft-edged disc into one channel of a
/// <see cref="VmapBlendMap"/>.
///
/// Godot-free, so it can be tested at all — and so it can run on a peer replaying a stroke, which is what
/// makes replication a polyline of samples instead of a bitmap. That only works if two machines given the
/// same floats produce the same bytes, so the falloff is a smoothstep POLYNOMIAL: no <c>Pow</c>, no
/// <c>Exp</c>, nothing whose last bit is a library's business rather than the format's.
/// </summary>
public static class VmapBlendPaint
{
    /// <summary>
    /// Stamp one disc into <paramref name="channel"/> (0-3). Centre and radius are in the map's own 0-1
    /// space. Returns false when the stamp missed the map entirely.
    /// </summary>
    /// <param name="hardness">
    /// 0 fades from the centre out; 1 is a hard-edged disc. Everything between is where the plateau ends and
    /// the falloff begins, as a fraction of the radius.
    /// </param>
    public static bool Stamp(
        VmapBlendMap map, Vector2 centerUv, float radiusUv, float strength, float hardness,
        int channel, VmapPaintMode mode,
        out int rx, out int ry, out int rw, out int rh)
    {
        ArgumentNullException.ThrowIfNull(map);
        rx = ry = rw = rh = 0;
        if (!map.IsValid || channel is < 0 or > 3 || radiusUv <= 0f)
            return false;

        RegionOf(map.Width, map.Height, centerUv, radiusUv, out rx, out ry, out rw, out rh);
        if (rw <= 0 || rh <= 0)
            return false;

        strength = Math.Clamp(strength, 0f, 1f);
        hardness = Math.Clamp(hardness, 0f, 1f);

        // Radius in TEXELS, per axis: a map is not square, and a round brush on screen has to stay round on a
        // map whose two axes have different texel counts.
        float radiusX = radiusUv * map.Width;
        float radiusY = radiusUv * map.Height;
        if (radiusX <= 0f || radiusY <= 0f)
            return false;

        float centerX = centerUv.X * map.Width - 0.5f;
        float centerY = centerUv.Y * map.Height - 0.5f;

        byte[] texels = map.Texels;
        byte[]? source = mode == VmapPaintMode.Smooth ? map.Clone().Texels : null;

        for (int y = ry; y < ry + rh; y++)
        {
            for (int x = rx; x < rx + rw; x++)
            {
                float dx = (x - centerX) / radiusX;
                float dy = (y - centerY) / radiusY;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float falloff = Falloff(d, hardness);
                if (falloff <= 0f)
                    continue;

                int at = (y * map.Width + x) * 4 + channel;
                float current = texels[at] / 255f;
                float amount = strength * falloff;

                float next = mode switch
                {
                    VmapPaintMode.Add => current + amount,
                    VmapPaintMode.Subtract => current - amount,
                    VmapPaintMode.Set => current + (strength - current) * falloff,
                    _ => current + (Neighbourhood(source!, map.Width, map.Height, x, y, channel) - current)
                                   * amount,
                };

                texels[at] = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(next, 0f, 1f) * 255f), 0, 255);
            }
        }

        return true;
    }

    /// <summary>
    /// The texel rectangle a stamp WOULD touch, without touching it.
    ///
    /// Separate so an op can declare <c>TouchedBlendRegions</c> in its constructor, before it has a document —
    /// which is what the journal needs to snapshot the right bytes. It must be a SUPERSET of what
    /// <see cref="Stamp"/> writes, never a subset: a rectangle that is too small makes an undo restore only
    /// part of a stroke, and the leftover paint has no step that describes it.
    /// </summary>
    public static void RegionOf(
        int width, int height, Vector2 centerUv, float radiusUv, out int rx, out int ry, out int rw, out int rh)
    {
        rx = ry = rw = rh = 0;
        if (width <= 0 || height <= 0 || radiusUv <= 0f)
            return;

        // One texel of slack on each side, because the per-texel test samples texel CENTRES and a disc can
        // clip a texel whose centre is marginally outside it.
        float radiusX = radiusUv * width + 1f;
        float radiusY = radiusUv * height + 1f;
        float centerX = centerUv.X * width - 0.5f;
        float centerY = centerUv.Y * height - 0.5f;

        int x0 = (int)MathF.Floor(centerX - radiusX);
        int y0 = (int)MathF.Floor(centerY - radiusY);
        int x1 = (int)MathF.Ceiling(centerX + radiusX) + 1;
        int y1 = (int)MathF.Ceiling(centerY + radiusY) + 1;

        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);

        rx = x0;
        ry = y0;
        rw = Math.Max(0, x1 - x0);
        rh = Math.Max(0, y1 - y0);
    }

    /// <summary>The union of several regions, clamped to the map — what a whole stroke declares.</summary>
    public static VmapBlendRegion Union(int blendMapId, IReadOnlyList<VmapBlendRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        bool any = false;
        foreach (VmapBlendRegion r in regions)
        {
            if (r.Width <= 0 || r.Height <= 0)
                continue;
            x0 = Math.Min(x0, r.X);
            y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.X + r.Width);
            y1 = Math.Max(y1, r.Y + r.Height);
            any = true;
        }
        return any
            ? new VmapBlendRegion(blendMapId, x0, y0, x1 - x0, y1 - y0)
            : new VmapBlendRegion(blendMapId, 0, 0, 0, 0);
    }

    /// <summary>
    /// Smoothstep falloff: 1 inside the plateau, 0 outside the disc, and 3t²-2t³ between.
    ///
    /// Deliberately a polynomial. A peer replays a stroke rather than receiving its pixels, so the two
    /// machines must agree bit for bit — and <c>Pow</c>/<c>Exp</c> are the runtime's answers, not the
    /// format's.
    /// </summary>
    private static float Falloff(float d, float hardness)
    {
        if (d >= 1f)
            return 0f;
        if (d <= hardness)
            return 1f;

        float span = 1f - hardness;
        if (span <= 1e-6f)
            return 1f;

        float t = 1f - (d - hardness) / span;      // 1 at the plateau edge, 0 at the rim
        return t * t * (3f - 2f * t);
    }

    /// <summary>Average of a texel's four neighbours plus itself, for the smooth mode.</summary>
    private static float Neighbourhood(byte[] src, int width, int height, int x, int y, int channel)
    {
        int sum = 0, n = 0;
        void Take(int sx, int sy)
        {
            if (sx < 0 || sy < 0 || sx >= width || sy >= height)
                return;
            sum += src[(sy * width + sx) * 4 + channel];
            n++;
        }

        Take(x, y);
        Take(x - 1, y);
        Take(x + 1, y);
        Take(x, y - 1);
        Take(x, y + 1);
        return n == 0 ? 0f : sum / (float)n / 255f;
    }
}
