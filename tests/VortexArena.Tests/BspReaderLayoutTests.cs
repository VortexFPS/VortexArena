using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first DIRECT coverage for <see cref="BspReader"/> (port of the Darkplaces IBSP loaders,
/// model_brush.c — previously only exercised indirectly via the collision/PVS suites). Synthetic
/// buffers pin the header contract (magic "IBSP", versions 46/47/48 only, the 17-entry lump directory
/// validated against the file size), the 72-byte texture stride with the "Funny lump size" rejection
/// (DP Host_Error when a lump length is not a multiple of its record size), the tolerant triangle
/// clamp, and the entities-lump text parse. The real-map half (skip-if-missing) checks structural
/// invariants across a shipped .bsp.
/// </summary>
public class BspReaderLayoutTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private const int LumpCount = 17;
    private const int HeaderSize = 8 + LumpCount * 8; // 144

    // ---------------------------------------------------------------- synthetic builder

    private sealed class LumpSpec
    {
        public int Index;
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>Builds an IBSP file: 144-byte header + the given lumps appended in order. Unlisted lumps are 0/0.</summary>
    private static byte[] BuildBsp(int version, params LumpSpec[] lumps)
    {
        int total = HeaderSize + lumps.Sum(l => l.Data.Length);
        var b = new byte[total];
        Encoding.ASCII.GetBytes("IBSP").CopyTo(b, 0);
        BitConverter.GetBytes(version).CopyTo(b, 4);

        int cursor = HeaderSize;
        foreach (LumpSpec l in lumps)
        {
            BitConverter.GetBytes(cursor).CopyTo(b, 8 + l.Index * 8);
            BitConverter.GetBytes(l.Data.Length).CopyTo(b, 8 + l.Index * 8 + 4);
            l.Data.CopyTo(b, cursor);
            cursor += l.Data.Length;
        }
        return b;
    }

    private static byte[] TextureRecord(string name, int surfaceFlags, int contents)
    {
        var rec = new byte[72]; // name[64] + surfaceflags + contents — the Q3 on-disk stride
        Encoding.ASCII.GetBytes(name).CopyTo(rec, 0);
        BitConverter.GetBytes(surfaceFlags).CopyTo(rec, 64);
        BitConverter.GetBytes(contents).CopyTo(rec, 68);
        return rec;
    }

    // ---------------------------------------------------------------- synthetic: layout facts

    [Fact]
    public void HeaderOnly_ParsesToEmptyMap()
    {
        BspData bsp = BspReader.Read(BuildBsp(46));

        Assert.Equal(46, bsp.Version);
        Assert.Empty(bsp.Textures);
        Assert.Empty(bsp.Vertices);
        Assert.Empty(bsp.Faces);
        Assert.Empty(bsp.Entities);
        Assert.Equal(string.Empty, bsp.EntitiesText);
        Assert.Equal(LumpCount, bsp.RawLumps.Length);
        Assert.False(bsp.IsDeluxemapped);
        // an absent vis lump reads as "unvised" (default BspVis, everything visible)
        Assert.Equal(0, bsp.Vis.ClusterCount);
    }

    [Fact]
    public void Versions46_47_48_AllAccepted()
    {
        Assert.Equal(46, BspReader.Read(BuildBsp(46)).Version);
        Assert.Equal(47, BspReader.Read(BuildBsp(47)).Version);
        Assert.Equal(48, BspReader.Read(BuildBsp(48)).Version);
    }

    [Fact]
    public void TextureLump_Stride72_NameFlagsContents()
    {
        var texData = TextureRecord("textures/test/wall", 0x80, 1)
            .Concat(TextureRecord("textures/test/lava", 0, 8)).ToArray();
        BspData bsp = BspReader.Read(BuildBsp(46, new LumpSpec { Index = (int)BspLump.Textures, Data = texData }));

        Assert.Equal(2, bsp.Textures.Length);
        Assert.Equal("textures/test/wall", bsp.Textures[0].ShaderName);
        Assert.Equal(0x80, bsp.Textures[0].SurfaceFlags);
        Assert.Equal(1, bsp.Textures[0].ContentFlags);
        Assert.Equal("textures/test/lava", bsp.Textures[1].ShaderName);
    }

    [Fact]
    public void EntitiesLump_ParsesText_AndStripsTrailingNul()
    {
        byte[] ents = Encoding.ASCII.GetBytes("{ \"classname\" \"worldspawn\" \"message\" \"hi\" }\0");
        BspData bsp = BspReader.Read(BuildBsp(46, new LumpSpec { Index = (int)BspLump.Entities, Data = ents }));

        Assert.Single(bsp.Entities);
        Assert.Equal("worldspawn", bsp.Entities[0]["classname"]);
        Assert.Equal("hi", bsp.Entities[0]["message"]);
        Assert.DoesNotContain('\0', bsp.EntitiesText);
    }

    [Fact]
    public void BrushSides_8BytesNormally_12BytesForVersion48()
    {
        // v46: planeindex + textureindex (8 bytes/record)
        var v46Side = new byte[8];
        BitConverter.GetBytes(3).CopyTo(v46Side, 0);
        BitConverter.GetBytes(5).CopyTo(v46Side, 4);
        BspData bsp46 = BspReader.Read(BuildBsp(46, new LumpSpec { Index = (int)BspLump.BrushSides, Data = v46Side }));
        Assert.Single(bsp46.BrushSides);
        Assert.Equal(3, bsp46.BrushSides[0].PlaneIndex);
        Assert.Equal(5, bsp46.BrushSides[0].TextureIndex);
        Assert.Equal(-1, bsp46.BrushSides[0].SurfaceFlags); // not stored pre-48

        // v48 (IG/ZeroRadiant): + per-side surfaceflags (12 bytes/record)
        var v48Side = new byte[12];
        BitConverter.GetBytes(3).CopyTo(v48Side, 0);
        BitConverter.GetBytes(5).CopyTo(v48Side, 4);
        BitConverter.GetBytes(9).CopyTo(v48Side, 8);
        BspData bsp48 = BspReader.Read(BuildBsp(48, new LumpSpec { Index = (int)BspLump.BrushSides, Data = v48Side }));
        Assert.Single(bsp48.BrushSides);
        Assert.Equal(9, bsp48.BrushSides[0].SurfaceFlags);
    }

    [Fact]
    public void Triangles_OutOfRangeIndicesClampToZero_NotAnError()
    {
        // DP clamps a bad element index to 0 rather than failing the load.
        var vert = new byte[44]; // one all-zero vertex (stride 44)
        var tris = new byte[12];
        BitConverter.GetBytes(0).CopyTo(tris, 0);
        BitConverter.GetBytes(7).CopyTo(tris, 4);   // out of range -> clamps to 0
        BitConverter.GetBytes(-2).CopyTo(tris, 8);  // negative -> clamps to 0
        BspData bsp = BspReader.Read(BuildBsp(46,
            new LumpSpec { Index = (int)BspLump.Vertices, Data = vert },
            new LumpSpec { Index = (int)BspLump.Triangles, Data = tris }));

        Assert.Equal(new[] { 0, 0, 0 }, bsp.Triangles);
    }

    // ---------------------------------------------------------------- synthetic: error paths

    [Fact]
    public void WrongMagic_Throws()
    {
        byte[] b = BuildBsp(46);
        b[0] = (byte)'X';
        Assert.Throws<AssetParseException>(() => BspReader.Read(b));
    }

    [Fact]
    public void UnsupportedVersion_Throws()
    {
        Assert.Throws<AssetParseException>(() => BspReader.Read(BuildBsp(45)));
        Assert.Throws<AssetParseException>(() => BspReader.Read(BuildBsp(49)));
    }

    [Fact]
    public void LumpRangeOutsideFile_Throws()
    {
        byte[] b = BuildBsp(46);
        // point lump 1 past the end of the buffer
        BitConverter.GetBytes(HeaderSize).CopyTo(b, 8 + 1 * 8);
        BitConverter.GetBytes(9999).CopyTo(b, 8 + 1 * 8 + 4);
        Assert.Throws<AssetParseException>(() => BspReader.Read(b));
    }

    [Fact]
    public void FunnyLumpSize_Throws()
    {
        // a 70-byte textures lump is not a multiple of the 72-byte record — DP Host_Errors ("funny lump size")
        Assert.Throws<AssetParseException>(() => BspReader.Read(
            BuildBsp(46, new LumpSpec { Index = (int)BspLump.Textures, Data = new byte[70] })));
    }

    [Fact]
    public void TooSmall_Throws()
        => Assert.Throws<AssetParseException>(() => BspReader.Read(new byte[HeaderSize - 1]));

    // ---------------------------------------------------------------- real map (skip-if-missing)

    private static readonly Lazy<BspData?> RealBsp = new(() =>
    {
        if (!Directory.Exists(DataDir)) return null;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return null;
        var maps = vfs.Find("maps/", "bsp").ToList();
        if (maps.Count == 0) return null;
        // prefer a real gameplay map (the _init/_hudsetup specials have no worldspawn entity)
        string path = maps.FirstOrDefault(m => m.Contains("stormkeep", StringComparison.OrdinalIgnoreCase))
                      ?? maps.FirstOrDefault(m => !m.Contains("_init", StringComparison.OrdinalIgnoreCase)
                                                  && !m.Contains("_hudsetup", StringComparison.OrdinalIgnoreCase))
                      ?? maps[0];
        return BspReader.Read(vfs.ReadBytes(path));
    });

    [Fact]
    public void RealMap_HeaderAndWorldspawn()
    {
        BspData? bsp = RealBsp.Value;
        if (bsp is null) return; // skip-if-missing

        Assert.True(bsp.Version is 46 or 47 or 48);
        Assert.True(bsp.Entities.Count >= 1);
        Assert.Contains(bsp.Entities, e => e.TryGetValue("classname", out string? c) && c == "worldspawn");
        Assert.True(bsp.Models.Length >= 1, "model 0 (the world) must exist");
    }

    [Fact]
    public void RealMap_CrossLumpIndexInvariants()
    {
        BspData? bsp = RealBsp.Value;
        if (bsp is null) return; // skip-if-missing

        foreach (BspFace f in bsp.Faces)
        {
            if (f.Type is BspFaceType.Flat or BspFaceType.Mesh or BspFaceType.Patch)
            {
                Assert.InRange(f.TextureIndex, 0, bsp.Textures.Length - 1);
                if (f.VertexCount > 0)
                    Assert.True(f.FirstVertex >= 0 && (long)f.FirstVertex + f.VertexCount <= bsp.Vertices.Length,
                        $"face vertex range {f.FirstVertex}+{f.VertexCount} exceeds {bsp.Vertices.Length}");
            }
            if (f.Type == BspFaceType.Patch)
            {
                Assert.True(f.PatchWidth >= 0 && f.PatchHeight >= 0);
            }
        }

        foreach (BspBrush brush in bsp.Brushes)
            Assert.True(brush.FirstSide >= 0 && brush.FirstSide + brush.SideCount <= bsp.BrushSides.Length);
        foreach (BspBrushSide side in bsp.BrushSides)
            Assert.InRange(side.PlaneIndex, 0, bsp.Planes.Length - 1);
        foreach (BspModel model in bsp.Models)
        {
            Assert.True(model.FirstFace >= 0 && model.FirstFace + model.FaceCount <= bsp.Faces.Length);
            Assert.True(model.FirstBrush >= 0 && model.FirstBrush + model.BrushCount <= bsp.Brushes.Length);
        }

        // texture names are NUL-trimmed, non-empty path strings
        Assert.All(bsp.Textures, t => Assert.False(string.IsNullOrEmpty(t.ShaderName)));

        // internal lightmap pages, when present, are whole 128x128x3 pages
        foreach (byte[] page in bsp.Lightmaps)
            Assert.Equal(BspData.LightmapSize * BspData.LightmapSize * 3, page.Length);
        if (bsp.IsDeluxemapped && bsp.Deluxemaps.Length > 0)
            Assert.Equal(bsp.Lightmaps.Length, bsp.Deluxemaps.Length);
    }
}
