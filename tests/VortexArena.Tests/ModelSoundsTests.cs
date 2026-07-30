using System.Collections.Generic;
using System.IO;
using System.Linq;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for the <see cref="ModelSounds"/> sidecar parser (the Xonotic player/monster
/// <c>.sounds</c> voice-alias table; a data convention consumed by QuakeC, not a DP C function).
/// Pins the <c>id soundpath [variantcount]</c> line grammar, the <c>//TAG:</c> banner +
/// <c>//</c>-disabled lines being skipped, variant-count semantics (0/absent = single file =
/// <see cref="ModelSound.IsSingle"/>), duplicate-id last-wins in the flat map, and file-order
/// preservation in <see cref="ModelSounds.ParseEntries"/>. Includes a real shipped .sounds file when
/// assets are present.
/// </summary>
public class ModelSoundsTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private const string Sample =
        "//TAG: soldier\n" +
        "death sound/player/soldier/player/death 3\n" +
        "pain100 sound/player/soldier/player/pain100 2\n" +
        "jump sound/player/soldier/player/jump\n" +
        "//gasp sound/player/soldier/player/gasp 0\n" +
        "attack sound/player/soldier/player/attack 0\n";

    [Fact]
    public void Parse_BuildsIdToPathMap()
    {
        Dictionary<string, string> map = ModelSounds.Parse(Sample);

        Assert.Equal(4, map.Count);
        Assert.Equal("sound/player/soldier/player/death", map["death"]);
        Assert.Equal("sound/player/soldier/player/jump", map["jump"]);
        Assert.False(map.ContainsKey("gasp"));       // disabled (//) line skipped
        Assert.False(map.ContainsKey("//TAG:"));     // banner skipped
    }

    [Fact]
    public void ParseEntries_PreservesOrderAndVariantCounts()
    {
        List<ModelSound> entries = ModelSounds.ParseEntries(Sample);

        Assert.Equal(4, entries.Count);
        Assert.Equal(new[] { "death", "pain100", "jump", "attack" }, entries.Select(e => e.Id).ToArray());
        Assert.Equal(3, entries[0].VariantCount);
        Assert.Equal(2, entries[1].VariantCount);
        Assert.Equal(0, entries[2].VariantCount);    // no third token -> 0
        Assert.Equal(0, entries[3].VariantCount);    // explicit 0
    }

    [Fact]
    public void IsSingle_TrueOnlyForZeroVariants()
    {
        Assert.True(new ModelSound("x", "p", 0).IsSingle);
        Assert.True(new ModelSound("x", "p", -1).IsSingle);
        Assert.False(new ModelSound("x", "p", 3).IsSingle);
    }

    [Fact]
    public void DuplicateId_LastWinsInTheMap()
    {
        Dictionary<string, string> map = ModelSounds.Parse("death sound/a 1\ndeath sound/b 2\n");
        Assert.Equal("sound/b", map["death"]);
        // ...but ParseEntries keeps both, in order
        Assert.Equal(2, ModelSounds.ParseEntries("death sound/a 1\ndeath sound/b 2\n").Count);
    }

    [Fact]
    public void MalformedLines_AreSkippedSilently()
    {
        List<ModelSound> entries = ModelSounds.ParseEntries("loneid\n\n   \nok sound/ok 1\n");
        ModelSound s = Assert.Single(entries);
        Assert.Equal("ok", s.Id);
    }

    [Fact]
    public void NonNumericOrNegativeVariantToken_ParsesToZero()
    {
        Assert.Equal(0, ModelSounds.ParseEntries("a sound/a xyz").Single().VariantCount);
        Assert.Equal(0, ModelSounds.ParseEntries("a sound/a -5").Single().VariantCount);
    }

    [Fact]
    public void EmptyInput_ParsesToNothing()
    {
        Assert.Empty(ModelSounds.Parse(""));
        Assert.Empty(ModelSounds.ParseEntries("//TAG: nothing\n// all disabled\n"));
    }

    [Fact]
    public void CrLfLineEndings_AreTolerated()
    {
        Dictionary<string, string> map = ModelSounds.Parse("a sound/a 1\r\nb sound/b 2\r\n");
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void RealAsset_ShippedSoundsFile_ParsesToValidEntries()
    {
        if (!Directory.Exists(DataDir)) return; // skip-if-missing
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        string? path = vfs.Find("models/", "sounds").FirstOrDefault();
        if (path is null) return;

        List<ModelSound> entries = ModelSounds.ParseEntries(vfs.ReadText(path));
        Assert.True(entries.Count >= 1, $"{path}: expected at least one sound alias");
        foreach (ModelSound s in entries)
        {
            Assert.False(string.IsNullOrEmpty(s.Id));
            Assert.False(string.IsNullOrEmpty(s.Path));
            Assert.True(s.VariantCount >= 0);
        }
    }
}
