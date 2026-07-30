using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  Chunked SDF cache format (.psdf) — the contract between the generator (SdfGenerator.cs, written at
//  map load / by the --bake-sdf CLI) and the runtime service (game/client/particles/SdfCollisionService.cs,
//  which uploads each chunk into a GPUParticlesCollisionSDF3D node). Format spec: planning/
//  particles-dual-system.md §A.2. One file per map: user://sdfcache/<map>-<bspHash16>.psdf, or shipped at
//  maps/<map>.psdf inside a pk3.
//
//  This file pins the DATA TYPES and the reader/writer SIGNATURES so the runtime service and generator can
//  be built in parallel; PsdfFile method bodies are implemented alongside the generator.
// =====================================================================================================

/// <summary>One occupied chunk's signed-distance slab (chunk-local, world-unit distances).</summary>
public sealed class SdfChunk
{
    /// <summary>Integer chunk-cell coordinates within the grid (u16[3] cell in the file).</summary>
    public int Cx, Cy, Cz;

    /// <summary>Per-axis voxel resolution of this chunk (e.g. 128 → 128³ voxels).</summary>
    public int Res;

    /// <summary>Optional per-chunk flags (reserved; e.g. all-solid / all-empty fast paths).</summary>
    public byte Flags;

    /// <summary>Res³ signed distances in world units, chunk-local, X fastest then Y then Z. Negative =
    /// inside solid. Stored as R16F on disk; kept as float here for sampling/validation.</summary>
    public float[] Distances = System.Array.Empty<float>();

    /// <summary>World-space mins of this chunk's cell AABB (no skirt). The collider box is centered on
    /// cell + skirt; see the runtime service.</summary>
    public Vector3 CellMins;
}

/// <summary>A whole map's chunked SDF field: grid metadata + the occupied chunks.</summary>
public sealed class SdfField
{
    public float VoxelSize;
    public float ChunkSize;
    public float Skirt;
    public float Thickness;
    public int GeneratorVersion;

    /// <summary>sha256 of the BSP bytes as loaded via VFS (invalidates on any map edit / pk3 swap).</summary>
    public byte[] BspHash = System.Array.Empty<byte>();

    /// <summary>Hash of generation params (voxel, chunk, skirt, thickness, generatorVersion).</summary>
    public uint ParamsHash;

    /// <summary>World-space mins of voxel grid origin and per-axis chunk count.</summary>
    public Vector3 GridMins;
    public int GridDimsX, GridDimsY, GridDimsZ;

    public System.Collections.Generic.List<SdfChunk> Chunks = new();
}

/// <summary>Tunable generation parameters (sourced from the cl_particles_sdf_* cvars).</summary>
public sealed class SdfGenParams
{
    public float ChunkSize = 1024f;   // cl_particles_sdf_chunk
    public float VoxelSize = 8f;      // cl_particles_sdf_voxel → res = chunk/voxel
    public float Skirt = 128f;        // geometry gather skirt (qu)
    public float Band = 64f;          // distance clamp band (qu)
    public float Thickness = 0f;      // extra thickness dilation; default 0.5*voxel applied in generator

    public int Resolution => (int)System.MathF.Round(ChunkSize / VoxelSize);
}

/// <summary>Reads/writes the .psdf cache format (§A.2). Bodies implemented with the generator.</summary>
public static class PsdfFile
{
    /// <summary>FourCC 'XSDF' (little-endian u32).</summary>
    public const uint Magic = 0x46445358u;

    /// <summary>Format version; bump on any on-disk layout change.</summary>
    public const uint Version = 1u;

    /// <summary>Generator algorithm version; part of ParamsHash so a generator change invalidates caches.</summary>
    public const int GeneratorVersion = 1;

    /// <summary>
    /// Serialize a field to the .psdf binary format (§A.2): header (magic, version, bspHash[32], paramsHash,
    /// voxelSize, chunkSize, gridMins[3], gridDims[3], occupiedChunkCount) then per chunk: cell u16[3],
    /// flags u8, res u16, then a u32 compressedLen prefix and deflate(R16F res³ distances).
    /// </summary>
    public static void Write(Stream stream, SdfField field)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (field is null) throw new ArgumentNullException(nameof(field));

        // Leave the stream open for the caller; we don't dispose it.
        var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // ---- header ----
        w.Write(Magic);                 // u32 'XSDF'
        w.Write(Version);               // u32

        // 32-byte sha256 of the BSP bytes (zero-padded/truncated to exactly 32 — the field is identity, §A.2).
        byte[] hash = field.BspHash ?? Array.Empty<byte>();
        for (int i = 0; i < 32; i++)
            w.Write(i < hash.Length ? hash[i] : (byte)0);

        w.Write(field.ParamsHash);      // u32
        w.Write(field.VoxelSize);       // f32
        w.Write(field.ChunkSize);       // f32
        w.Write(field.GridMins.X);      // f32[3]
        w.Write(field.GridMins.Y);
        w.Write(field.GridMins.Z);
        w.Write((ushort)field.GridDimsX);   // u16[3]
        w.Write((ushort)field.GridDimsY);
        w.Write((ushort)field.GridDimsZ);
        w.Write((uint)field.Chunks.Count);  // u32 occupiedChunkCount

        // ---- chunks ----
        foreach (SdfChunk chunk in field.Chunks)
        {
            w.Write((ushort)chunk.Cx);  // u16[3] cell
            w.Write((ushort)chunk.Cy);
            w.Write((ushort)chunk.Cz);
            w.Write(chunk.Flags);       // u8
            w.Write((ushort)chunk.Res); // u16

            // Deflate the R16F voxel slab into a side buffer so we know its length, then write u32 len + bytes.
            byte[] compressed = DeflateHalfArray(chunk.Distances);
            w.Write((uint)compressed.Length); // u32 compressedLen
            w.Write(compressed);
        }

        w.Flush();
    }

    /// <summary>Parse a .psdf stream into an SdfField. Throws on bad magic/version.</summary>
    public static SdfField Read(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        uint magic = r.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Bad .psdf magic 0x{magic:X8} (expected 0x{Magic:X8}).");
        uint version = r.ReadUInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported .psdf version {version} (expected {Version}).");

        var field = new SdfField { GeneratorVersion = GeneratorVersion };

        var hash = new byte[32];
        int read = 0;
        while (read < 32)
        {
            int n = r.Read(hash, read, 32 - read);
            if (n == 0) throw new EndOfStreamException("Truncated .psdf header (bspHash).");
            read += n;
        }
        field.BspHash = hash;

        field.ParamsHash = r.ReadUInt32();
        field.VoxelSize = r.ReadSingle();
        field.ChunkSize = r.ReadSingle();
        float gx = r.ReadSingle(), gy = r.ReadSingle(), gz = r.ReadSingle();
        field.GridMins = new Vector3(gx, gy, gz);
        field.GridDimsX = r.ReadUInt16();
        field.GridDimsY = r.ReadUInt16();
        field.GridDimsZ = r.ReadUInt16();
        uint chunkCount = r.ReadUInt32();

        for (uint c = 0; c < chunkCount; c++)
        {
            var chunk = new SdfChunk
            {
                Cx = r.ReadUInt16(),
                Cy = r.ReadUInt16(),
                Cz = r.ReadUInt16(),
                Flags = r.ReadByte(),
                Res = r.ReadUInt16(),
            };
            int compressedLen = checked((int)r.ReadUInt32());
            byte[] compressed = r.ReadBytes(compressedLen);
            if (compressed.Length != compressedLen)
                throw new EndOfStreamException("Truncated .psdf chunk payload.");

            int voxelCount = chunk.Res * chunk.Res * chunk.Res;
            chunk.Distances = InflateHalfArray(compressed, voxelCount);

            // CellMins derived from the grid metadata (not stored — fully determined by cell + grid origin).
            chunk.CellMins = field.GridMins + new Vector3(
                chunk.Cx * field.ChunkSize,
                chunk.Cy * field.ChunkSize,
                chunk.Cz * field.ChunkSize);

            field.Chunks.Add(chunk);
        }

        return field;
    }

    /// <summary>Deflate a float array as R16F (System.Half) little-endian, return the compressed bytes.</summary>
    private static byte[] DeflateHalfArray(float[] values)
    {
        using var outMs = new MemoryStream();
        using (var deflate = new DeflateStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            Span<byte> two = stackalloc byte[2];
            foreach (float f in values)
            {
                Half h = (Half)f;
                ushort bits = BitConverter.HalfToUInt16Bits(h);
                two[0] = (byte)(bits & 0xFF);
                two[1] = (byte)(bits >> 8);
                deflate.Write(two);
            }
        }
        return outMs.ToArray();
    }

    /// <summary>Inflate <paramref name="voxelCount"/> R16F values (System.Half) from a deflate buffer to floats.</summary>
    private static float[] InflateHalfArray(byte[] compressed, int voxelCount)
    {
        var result = new float[voxelCount];
        byte[] raw = new byte[voxelCount * 2];

        using (var inMs = new MemoryStream(compressed, writable: false))
        using (var deflate = new DeflateStream(inMs, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = deflate.Read(raw, read, raw.Length - read);
                if (n == 0) throw new EndOfStreamException("Truncated deflate stream in .psdf chunk.");
                read += n;
            }
        }

        for (int i = 0; i < voxelCount; i++)
        {
            ushort bits = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
            result[i] = (float)BitConverter.UInt16BitsToHalf(bits);
        }
        return result;
    }

    /// <summary>sha256 of the raw BSP bytes (as loaded via VFS) — the cache identity (§A.2).</summary>
    public static byte[] ComputeBspHash(byte[] bspBytes)
    {
        if (bspBytes is null) throw new ArgumentNullException(nameof(bspBytes));
        return SHA256.HashData(bspBytes);
    }

    /// <summary>
    /// Stable FNV-1a (32-bit) hash of the generation params (voxel, chunk, skirt, thickness, generatorVersion)
    /// — invalidates the cache on a generator/format change. Hashes the raw IEEE bits of each float so the
    /// value is byte-stable across runs and platforms (§A.2).
    /// </summary>
    public static uint ComputeParamsHash(SdfGenParams p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));

        const uint FnvOffset = 2166136261u;
        const uint FnvPrime = 16777619u;
        uint h = FnvOffset;

        static uint Mix(uint h, uint prime, uint value)
        {
            // FNV-1a over the 4 bytes of value (little-endian).
            for (int i = 0; i < 4; i++)
            {
                h ^= value & 0xFF;
                h *= prime;
                value >>= 8;
            }
            return h;
        }

        h = Mix(h, FnvPrime, (uint)BitConverter.SingleToInt32Bits(p.VoxelSize));
        h = Mix(h, FnvPrime, (uint)BitConverter.SingleToInt32Bits(p.ChunkSize));
        h = Mix(h, FnvPrime, (uint)BitConverter.SingleToInt32Bits(p.Skirt));
        h = Mix(h, FnvPrime, (uint)BitConverter.SingleToInt32Bits(p.Thickness));
        h = Mix(h, FnvPrime, (uint)GeneratorVersion);
        return h;
    }

    /// <summary>Cache file name: <c>&lt;map&gt;-&lt;first 16 hex of bspHash&gt;.psdf</c>.</summary>
    public static string CacheFileName(string mapName, byte[] bspHash)
    {
        var sb = new System.Text.StringBuilder(mapName);
        sb.Append('-');
        for (int i = 0; i < 8 && i < bspHash.Length; i++)
            sb.Append(bspHash[i].ToString("x2"));
        sb.Append(".psdf");
        return sb.ToString();
    }
}
