using System.IO;
using System.Linq;
using VortexArena.Formats.Iqm;
using VortexArena.Formats.Materials;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Exercises the Godot-free asset PARSERS (pk3 VFS, IQM, Q3 shader) against the REAL Xonotic data tree.
/// CI-portable: silently no-ops where the reference checkout isn't present. The Godot mesh/material/skeleton
/// BUILDERS need a Godot runtime and are out of scope for unit tests.
/// </summary>
public class AssetParserTests
{
    private static readonly string DataDir = TestPaths.Data;

    [Fact]
    public void Vfs_Mounts_And_Finds_Content()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        Assert.True(vfs.Find("scripts/", "shader").Count() >= 40, "expected the shipped .shader scripts");
        Assert.True(vfs.Find("models/", "iqm").Any(), "expected IQM models");
        // BSPs arrive with the fetched map packs (D7), so only assert when they are present.
        if (TestPaths.HasMaps)
            Assert.True(vfs.Find("maps/", "bsp").Any(), "expected at least one .bsp");
        // extension-search resolves an image base name to a concrete file
        // Probe png as well as tga: the content tree was re-encoded to PNG (restructure section 4.2),
        // so a tga-only probe would silently stop exercising ResolveImage entirely.
        var anyTga = vfs.Find("textures/", "tga").FirstOrDefault()
                     ?? vfs.Find("models/", "tga").FirstOrDefault()
                     ?? vfs.Find("textures/", "png").FirstOrDefault()
                     ?? vfs.Find("models/", "png").FirstOrDefault();
        Assert.NotNull(anyTga); // the tree always has art in one of those forms
        if (anyTga is not null)
        {
            string baseName = anyTga[..anyTga.LastIndexOf('.')];
            Assert.NotNull(vfs.ResolveImage(baseName));
        }
    }

    [Fact]
    public void Q3Shaders_Parse_IntoManyMaterials()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(DataDir);

        var texts = vfs.Find("scripts/", "shader").Select(vfs.ReadText);
        var shaders = Q3ShaderParser.ParseFiles(texts);
        // The map packs carry roughly half the shader scripts, so the floor depends on whether they
        // have been fetched. Scaled rather than dropped: a real assertion runs either way.
        int floor = TestPaths.HasMaps ? 500 : 200;
        Assert.True(shaders.Count >= floor,
            $"expected {floor}+ materials from the real shader scripts, got {shaders.Count} "
            + $"(maps present: {TestPaths.HasMaps})");
    }

    [Fact]
    public void Iqm_RealModel_Parses_WithJointsMeshes()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(DataDir);

        string iqmPath = vfs.Find("models/", "iqm").First();
        var iqm = IqmReader.Read(vfs.ReadBytes(iqmPath));
        Assert.True(iqm.Meshes.Length >= 1, $"{iqmPath}: no meshes");
        Assert.True(iqm.Joints.Length >= 1, $"{iqmPath}: no joints (skeleton)");
    }
}
