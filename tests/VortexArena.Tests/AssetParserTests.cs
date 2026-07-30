using System.IO;
using System.Linq;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises the Godot-free asset PARSERS (pk3 VFS, IQM, Q3 shader) against the REAL Xonotic data tree.
/// CI-portable: silently no-ops where the reference checkout isn't present. The Godot mesh/material/skeleton
/// BUILDERS need a Godot runtime and are out of scope for unit tests.
/// </summary>
public class AssetParserTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    [Fact]
    public void Vfs_Mounts_And_Finds_Content()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountGameDir(DataDir));

        Assert.True(vfs.Find("scripts/", "shader").Count() >= 40, "expected the shipped .shader scripts");
        Assert.True(vfs.Find("models/", "iqm").Any(), "expected IQM models");
        Assert.True(vfs.Find("maps/", "bsp").Any(), "expected at least one .bsp");
        // extension-search resolves an image base name to a concrete file
        var anyTga = vfs.Find("textures/", "tga").FirstOrDefault() ?? vfs.Find("models/", "tga").FirstOrDefault();
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
        vfs.MountGameDir(DataDir);

        var texts = vfs.Find("scripts/", "shader").Select(vfs.ReadText);
        var shaders = Q3ShaderParser.ParseFiles(texts);
        Assert.True(shaders.Count >= 500, $"expected 500+ materials from the real shader scripts, got {shaders.Count}");
    }

    [Fact]
    public void Iqm_RealModel_Parses_WithJointsMeshes()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        vfs.MountGameDir(DataDir);

        string iqmPath = vfs.Find("models/", "iqm").First();
        var iqm = IqmReader.Read(vfs.ReadBytes(iqmPath));
        Assert.True(iqm.Meshes.Length >= 1, $"{iqmPath}: no meshes");
        Assert.True(iqm.Joints.Length >= 1, $"{iqmPath}: no joints (skeleton)");
    }
}
