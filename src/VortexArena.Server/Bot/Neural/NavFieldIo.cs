using System;
using System.IO;
using System.Numerics;
using System.Text;
using VortexArena.Engine.Collision;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Binary read/write for a baked <see cref="NavField"/>, plus the geometry hash that decides whether a
/// cached field still matches the map it was baked from.
///
/// <para>The file sits next to the BSP, the same arrangement as <c>.waypoints.cache</c>. That precedent is
/// worth following exactly: parity finding D1 records what happened when the map packer classified
/// <c>.cache</c> as build residue and dropped it, and the fix was a one-line extension-list correction
/// rather than a new mechanism.</para>
///
/// <para><b>Staleness is a hash, not a timestamp.</b> A field baked against different geometry does not
/// fail loudly; it quietly tells the policy there is a floor where there is now a pit. The hash makes that
/// a load-time rejection instead of a gameplay mystery.</para>
/// </summary>
public static class NavFieldIo
{
    /// <summary>File magic: "VXNF" little-endian.</summary>
    private const uint Magic = 0x464E5856;

    /// <summary>Bump when the on-disk layout changes; an older file is rejected and re-baked.</summary>
    public const int Version = 1;

    /// <summary>The conventional filename for a map's field.</summary>
    public static string FileNameFor(string mapName) => $"maps/{mapName}.navfield";

    public static void Write(Stream stream, NavField field)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(field.MapName);
        w.Write(field.GeometryHash);
        w.Write(field.Origin.X);
        w.Write(field.Origin.Y);
        w.Write(field.Width);
        w.Write(field.Height);
        w.Write(NavField.CellSize);

        int cells = field.Width * field.Height;
        w.Write(field.SpanCount);

        // Counts first, then the spans in column order. The reader rebuilds the offsets rather than storing
        // them: they are a prefix sum of the counts, so writing both would let the two disagree.
        for (int y = 0; y < field.Height; y++)
            for (int x = 0; x < field.Width; x++)
                w.Write((byte)field.Column(x, y).Length);

        for (int y = 0; y < field.Height; y++)
        {
            for (int x = 0; x < field.Width; x++)
            {
                ReadOnlySpan<FloorSpan> col = field.Column(x, y);
                for (int i = 0; i < col.Length; i++)
                {
                    w.Write(col[i].FloorZ);
                    w.Write(col[i].CeilZ);
                    w.Write(col[i].SlopeDot);
                    w.Write(col[i].Content);
                    w.Write(col[i].JumpReachMask);
                }
            }
        }
        _ = cells;
    }

    /// <summary>
    /// Read a field. Returns null when the stream is not a field file, is a version this build does not
    /// understand, or is truncated. Null always means "bake it", never "crash".
    /// </summary>
    public static NavField? Read(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != Version) return null;

            string mapName = r.ReadString();
            ulong hash = r.ReadUInt64();
            float ox = r.ReadSingle(), oy = r.ReadSingle();
            int width = r.ReadInt32(), height = r.ReadInt32();
            int cellSize = r.ReadInt32();
            if (cellSize != NavField.CellSize) return null;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

            int spanCount = r.ReadInt32();
            if (spanCount < 0 || spanCount > width * height * NavField.MaxSpansPerColumn) return null;

            int cells = width * height;
            var counts = new byte[cells];
            for (int i = 0; i < cells; i++) counts[i] = r.ReadByte();

            var starts = new int[cells];
            int running = 0;
            for (int i = 0; i < cells; i++) { starts[i] = running; running += counts[i]; }
            if (running != spanCount) return null;

            var spans = new FloorSpan[spanCount];
            for (int i = 0; i < spanCount; i++)
            {
                spans[i] = new FloorSpan
                {
                    FloorZ = r.ReadInt16(),
                    CeilZ = r.ReadInt16(),
                    SlopeDot = r.ReadByte(),
                    Content = r.ReadByte(),
                    JumpReachMask = r.ReadByte(),
                };
            }

            return new NavField(mapName, hash, new Vector3(ox, oy, 0f), width, height, starts, counts, spans);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A cheap, order-stable fingerprint of the collision geometry: brush count, world bounds, and the plane
    /// set of every 37th brush. Full-geometry hashing would cost more than the bake it is protecting; the
    /// stride catches a recompile (which moves brushes and changes the count) without walking millions of
    /// planes.
    ///
    /// <para>37 is coprime with any power of two, so the stride does not land on the same structural
    /// position in every map's brush ordering.</para>
    /// </summary>
    public static ulong GeometryHash(CollisionWorld world)
    {
        // FNV-1a 64.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;

        void Mix(float f)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(f);
            for (int b = 0; b < 4; b++)
            {
                h ^= (byte)(bits >> (b * 8));
                h *= prime;
            }
        }

        var brushes = world.Brushes;
        h ^= (ulong)brushes.Count;
        h *= prime;
        Mix(world.WorldMins.X); Mix(world.WorldMins.Y); Mix(world.WorldMins.Z);
        Mix(world.WorldMaxs.X); Mix(world.WorldMaxs.Y); Mix(world.WorldMaxs.Z);

        for (int i = 0; i < brushes.Count; i += 37)
        {
            Brush b = brushes[i];
            Mix(b.Mins.X); Mix(b.Mins.Y); Mix(b.Mins.Z);
            Mix(b.Maxs.X); Mix(b.Maxs.Y); Mix(b.Maxs.Z);
            h ^= (ulong)b.Contents;
            h *= prime;
        }
        return h;
    }
}
