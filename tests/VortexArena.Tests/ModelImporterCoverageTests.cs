// Port of darkplaces/model_shared.c (loader table 45-65) + qcsrc/common/models/all.inc (MODEL() registry)
// AssetLoader.LoadModel dispatches a model by its leading MAGIC (not its extension) and supports IQM
// ("INTERQUAKEMODEL"), DPM ("DARKPLACESMODEL"), MD3 ("IDP3") and — since 2026-07 — MDL ("IDPO", the Quake1
// alias format; MdlReader/MdlBuilder). MD2 ("IDP2"), ZYM ("ZYMOTICMODEL") and PSK ("ACTRHEAD") are NOT YET
// IMPLEMENTED (open work — TODO T72; see AssetLoader.BuildModelFactory). These tests pin that coverage: the
// casing shell + gib chunk are genuine "IDPO" MDLs that now resolve to a real model instead of a placeholder,
// while MD2/ZYM/PSK have no (or map-pack-only) shipped content today.

using System;
using System.IO;
using System.Linq;
using System.Text;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards which model importers <c>AssetLoader.BuildModelFactory</c> implements. AssetLoader itself is a
/// Godot type (uses GD/Node3D/AudioStream) and lives in the game project the test assembly does not
/// reference, so — exactly like <c>MuzzleOffsetTests</c> — these tests reproduce the loader's magic dispatch
/// over raw bytes rather than constructing an AssetLoader: the decision under test ("is this a model magic the
/// port implements?") is pure data. Deep MDL parsing is covered separately by <c>MdlReaderTests</c>.
///
/// <para>CI-portable: the real-asset cases silently no-op when the reference checkout is absent
/// (mirrors <c>AssetParserTests</c> / <c>MuzzleOffsetTests</c>).</para>
/// </summary>
public class ModelImporterCoverageTests
{
    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // The four model magics the port's AssetLoader.BuildModelFactory dispatches (extensions lie, so dispatch
    // is by leading magic — same tags AssetLoader uses as MagicIqm/MagicDpm/MagicMd3/MagicMdl).
    private const string MagicIqm = "INTERQUAKEMODEL"; // IQM
    private const string MagicDpm = "DARKPLACESMODEL"; // DPM
    private const string MagicMd3 = "IDP3";            // MD3
    private const string MagicMdl = "IDPO";            // Quake1 MDL — implemented since 2026-07
    // Not yet implemented (TODO T72; darkplaces model_shared.c:48-61): MD2 "IDP2", ZYM "ZYMOTICMODEL",
    // PSK "ACTRHEAD". Their (absent/map-only) content coverage is recorded by Md2_Psk_And_BaseZym_ShipNoContent.

    /// <summary>Read up to the first 16 bytes as an ASCII tag (trimmed at the first NUL) — the same magic
    /// extraction <c>AssetLoader.ReadMagic</c> performs before dispatch.</summary>
    private static string ReadMagic(byte[] data)
    {
        int n = Math.Min(16, data.Length);
        int end = 0;
        while (end < n && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, 0, end);
    }

    /// <summary>True iff the magic matches one of the four importers the port implements — i.e. iff
    /// <c>AssetLoader.BuildModelFactory</c> would build a node instead of falling through to null.</summary>
    private static bool IsSupportedModelMagic(string magic) =>
        magic.StartsWith(MagicIqm, StringComparison.Ordinal) ||
        magic.StartsWith(MagicDpm, StringComparison.Ordinal) ||
        magic.StartsWith(MagicMd3, StringComparison.Ordinal) ||
        magic.StartsWith(MagicMdl, StringComparison.Ordinal);

    // ── The two MDL files that reach AssetLoader.LoadModel — both now load a real model ───────────────

    /// <summary>
    /// <c>ShellCasings.BuildMesh</c> asks the loader for <c>models/casing_shell.mdl</c>; it is a Quake1 MDL
    /// ("IDPO") and now resolves to a real model (the generated brass cylinder is only the unwired-loader
    /// fallback). Pin the magic + that the port implements it.
    /// </summary>
    [Fact]
    public void CasingShellMdl_IsQuake1Mdl_AndNowImportable()
    {
        string path = Path.Combine(Pk3Dir, "models", "casing_shell.mdl");
        if (!File.Exists(path)) return; // no checkout → no-op

        string magic = ReadMagic(File.ReadAllBytes(path));
        Assert.StartsWith(MagicMdl, magic, StringComparison.Ordinal);          // it IS a Quake1 MDL ("IDPO")
        Assert.True(IsSupportedModelMagic(magic),                              // …and the port now imports it,
            $"casing_shell.mdl magic '{magic}' must be importable so the shotgun ejects a real shell model.");
    }

    /// <summary>
    /// <c>ModelGibs.BuildMesh</c> asks the loader for <c>models/gibs/chunk.mdl</c> (the .mdl extension guard was
    /// removed once MDL landed); its "IDPO" magic is now a supported importer. Pin that.
    /// </summary>
    [Fact]
    public void ChunkMdl_IsQuake1Mdl_AndNowImportable()
    {
        string path = Path.Combine(Pk3Dir, "models", "gibs", "chunk.mdl");
        if (!File.Exists(path)) return; // no checkout → no-op

        string magic = ReadMagic(File.ReadAllBytes(path));
        Assert.StartsWith(MagicMdl, magic, StringComparison.Ordinal);
        Assert.True(IsSupportedModelMagic(magic),
            $"gibs/chunk.mdl magic '{magic}' must be importable so fast chunks render the real chunk model.");
    }

    /// <summary>
    /// The sibling of the shell in the same registry entry (all.inc:137-138): the BULLET casing is an IQM and
    /// also carries a supported magic — so both casings now load real models. Proves the coverage check
    /// distinguishes "supported" from a broken loader.
    /// </summary>
    [Fact]
    public void CasingBronzeIqm_HasSupportedMagic_SoItLoadsCleanly()
    {
        string path = Path.Combine(Pk3Dir, "models", "casing_bronze.iqm");
        if (!File.Exists(path)) return; // no checkout → no-op

        string magic = ReadMagic(File.ReadAllBytes(path));
        Assert.StartsWith(MagicIqm, magic, StringComparison.Ordinal);
        Assert.True(IsSupportedModelMagic(magic), "casing_bronze.iqm is an IQM and must load.");
    }

    // ── Whole-tree sweep: every shipped .mdl now resolves to a supported importer ──────────────────────

    /// <summary>
    /// Sweep every <c>.mdl</c> the VFS mounts and assert dispatch follows the leading MAGIC, never the (lying)
    /// extension, AND that every one resolves to a supported importer. Of the 19 shipped <c>.mdl</c> files, 13
    /// are true Quake1 MDLs ("IDPO") — now imported by MdlReader — and the other 6 (ebomb/elaser/laser/
    /// plasmatrail/tracer/items/a_bullets) are actually MD3 ("IDP3") wearing a <c>.mdl</c> extension and load
    /// via the MD3 importer. Both populations must be non-empty (the sweep would be vacuous otherwise) and both
    /// must be supported. Run over the real VFS so it mirrors the actual mount the game uses.
    /// </summary>
    [Fact]
    public void EveryShippedMdl_ResolvesToASupportedImporter()
    {
        if (!Directory.Exists(DataDir)) return; // no checkout → no-op
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        string[] mdls = vfs.Find("models/", "mdl").ToArray();
        Assert.NotEmpty(mdls); // the data tree ships .mdl files (19 in stock Base)

        int trueMdl = 0, md3InMdlClothing = 0;
        foreach (string vpath in mdls)
        {
            string magic = ReadMagic(vfs.ReadBytes(vpath));
            Assert.True(IsSupportedModelMagic(magic),
                $"{vpath}: magic '{magic}' must resolve to a supported importer (dispatch is by magic).");
            if (magic.StartsWith(MagicMdl, StringComparison.Ordinal))
                trueMdl++;
            else
            {
                // Extension lies: the only non-MDL magic under a .mdl extension in stock Base is MD3.
                Assert.StartsWith(MagicMd3, magic, StringComparison.Ordinal);
                md3InMdlClothing++;
            }
        }
        Assert.True(trueMdl > 0, "stock Base ships genuine Quake1 MDL files (now imported)");
        Assert.True(md3InMdlClothing > 0,
            "stock Base ships MD3 files under a .mdl extension — proving dispatch is by magic, not extension");
    }

    /// <summary>
    /// Content coverage for the not-yet-implemented formats (TODO T72) — this is prioritisation context, not a
    /// justification for leaving them unported. MD2 and PSK ship nothing at all (anywhere the VFS mounts), so
    /// they are mod-compat only (lowest priority). ZYM ships nothing in the loose base data tree
    /// (xonotic-data.pk3dir/models) — its only occurrences are two map-pack addons (models/pomp/pomp.zym,
    /// models/train.zym) packaged inside the maps .pk3, which <c>MountGameDir</c> also mounts. So the base data
    /// tree has no ZYM, and every ZYM the VFS sees is a maps-pack model — the 2 real props a ZYM reader would make
    /// render (matches the recon inventory).
    /// </summary>
    [Fact]
    public void Md2_Psk_And_BaseZym_ShipNoContent()
    {
        if (!Directory.Exists(DataDir)) return; // no checkout → no-op
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        Assert.Empty(vfs.Find("", "md2")); // MD2 ("IDP2"): zero shipped content
        Assert.Empty(vfs.Find("", "psk")); // PSK ("ACTRHEAD"): zero shipped content

        // ZYM ("ZYMOTICMODEL"): none loose under xonotic-data.pk3dir — the base data tree ships no ZYM.
        string looseDataTree = Path.Combine(DataDir, "xonotic-data.pk3dir");
        if (Directory.Exists(looseDataTree))
            Assert.Empty(Directory.EnumerateFiles(looseDataTree, "*.zym", SearchOption.AllDirectories));

        // The only ZYMs the VFS resolves are the two map-pack addons inside the maps .pk3 — never base content.
        foreach (string zym in vfs.Find("", "zym"))
            Assert.True(
                zym.EndsWith("models/pomp/pomp.zym", StringComparison.OrdinalIgnoreCase) ||
                zym.EndsWith("models/train.zym", StringComparison.OrdinalIgnoreCase),
                $"unexpected ZYM '{zym}' — the only ZYMs in stock Base are the two maps-pack addons.");
    }
}
