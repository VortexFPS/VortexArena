using System.Collections.Generic;
using System.IO;
using System.Linq;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="ShaderPreview.ImageName"/> — which image the editor's texture browser shows for a
/// material (backlog T6).
///
/// Getting this wrong is silent. It does not throw and it does not log: the grid fills with question marks,
/// or with the right number of pictures of the wrong thing, and the only way to tell is to look. So the
/// precedence lives in a Godot-free class purely so it can be pinned here, and the real-data case at the
/// bottom is the one that would actually catch a regression.
/// </summary>
public class ShaderPreviewTests
{
    private static readonly string DataDir = TestPaths.Data;

    private readonly ITestOutputHelper _out;
    public ShaderPreviewTests(ITestOutputHelper output) => _out = output;

    private static ShaderDef Shader(string text, string name)
    {
        IReadOnlyDictionary<string, ShaderDef> defs = Q3ShaderParser.Parse(text);
        Assert.True(defs.TryGetValue(name, out ShaderDef? def), $"'{name}' did not parse out of the sample");
        return def!;
    }

    /// <summary>
    /// The author's own answer beats anything derived. For an animated or multi-stage shader it is the only
    /// honest one — the first render stage of a forcefield is a scrolling mask, not a picture of a forcefield.
    /// </summary>
    [Fact]
    public void TheEditorImageWinsOverTheDiffuseStage()
    {
        ShaderDef def = Shader("""
            textures/x/wall
            {
                qer_editorimage textures/x/wall_editor.tga
                {
                    map textures/x/wall_diffuse
                }
            }
            """, "textures/x/wall");

        // Also proves the extension is stripped: qer_editorimage is written with one about as often as not.
        Assert.Equal("textures/x/wall_editor", ShaderPreview.ImageName("textures/x/wall", def));
    }

    /// <summary>
    /// The case naive name-loading gets wrong: a lightmapped shader's colour is its SECOND stage, and the
    /// shader name resolves to no file at all.
    /// </summary>
    [Fact]
    public void TheDiffuseIsTheFirstStageThatIsNotALightmap()
    {
        ShaderDef def = Shader("""
            textures/x/floor
            {
                { map $lightmap }
                { map textures/x/floor_d }
            }
            """, "textures/x/floor");

        Assert.Equal("textures/x/floor_d", ShaderPreview.ImageName("textures/x/floor", def));
    }

    [Fact]
    public void WhiteImageAndDetailStagesAreSkipped()
    {
        ShaderDef def = Shader("""
            textures/x/rock
            {
                { map $whiteimage }
                {
                    map textures/x/rock_detail
                    detail
                }
                { map textures/x/rock_d }
            }
            """, "textures/x/rock");

        Assert.Equal("textures/x/rock_d", ShaderPreview.ImageName("textures/x/rock", def));
    }

    [Fact]
    public void AnAnimatedStageShowsItsFirstFrame()
    {
        ShaderDef def = Shader("""
            textures/x/screen
            {
                {
                    animMap 5 textures/x/f1 textures/x/f2 textures/x/f3
                }
            }
            """, "textures/x/screen");

        Assert.Equal("textures/x/f1", ShaderPreview.ImageName("textures/x/screen", def));
    }

    /// <summary>A sky has no diffuse; the front face of its farbox is what a mapper recognises it by.</summary>
    [Fact]
    public void ASkyFallsBackToItsFarboxFrontFace()
    {
        ShaderDef def = Shader("""
            textures/x/sky
            {
                surfaceparm sky
                skyparms env/blue - -
            }
            """, "textures/x/sky");

        Assert.Equal("env/blue_ft", ShaderPreview.ImageName("textures/x/sky", def));
    }

    [Fact]
    public void AShaderWithNoStagesFallsBackToItsOwnName()
    {
        ShaderDef def = Shader("""
            textures/x/clip
            {
                surfaceparm nodraw
                surfaceparm nonsolid
            }
            """, "textures/x/clip");

        Assert.Equal("textures/x/clip", ShaderPreview.ImageName("textures/x/clip", def));
    }

    /// <summary>Most of a map's textures ship with no shader entry at all; there, the name IS the image.</summary>
    [Fact]
    public void WithNoShaderTheNameIsTheImage()
    {
        Assert.Equal("textures/x/plain", ShaderPreview.ImageName("textures/x/plain", null));
        Assert.Equal("textures/x/plain", ShaderPreview.ImageName("textures/x/plain.tga", null));
        Assert.Equal("textures/x/plain", ShaderPreview.ImageName("textures/x/plain.dds", null));
    }

    /// <summary>A dot that is not an image extension is part of the name — <c>env/sky_1.5</c> is a real one.</summary>
    [Fact]
    public void ANonImageExtensionIsNotStripped()
        => Assert.Equal("env/sky_1.5", ShaderPreview.ImageName("env/sky_1.5", null));

    [Fact]
    public void AnEmptyNameHasNoImage()
        => Assert.Null(ShaderPreview.ImageName("", null));

    /// <summary>
    /// The assertion that would actually catch a regression: across the game's own ~1300 texture shaders, the
    /// name this resolves must be a file that EXISTS. A synthetic test tells you the precedence is what you
    /// wrote down; only this tells you the grid is full of pictures rather than question marks.
    /// </summary>
    [Fact]
    public void RealShadersMostlyResolveToAnImageThatExists()
    {
        if (!Directory.Exists(DataDir))
        {
            _out.WriteLine($"content dir '{DataDir}' missing — skipped");
            return;
        }

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));

        IReadOnlyDictionary<string, ShaderDef> shaders =
            Q3ShaderParser.ParseFiles(vfs.Find("scripts/", "shader").Select(vfs.ReadText));

        int considered = 0, resolved = 0;
        var missing = new List<string>();
        foreach (KeyValuePair<string, ShaderDef> kv in shaders)
        {
            if (!kv.Key.StartsWith("textures/", System.StringComparison.OrdinalIgnoreCase))
                continue;
            considered++;

            string? image = ShaderPreview.ImageName(kv.Key, kv.Value);
            Assert.False(string.IsNullOrEmpty(image), $"{kv.Key} resolved to no image name at all");

            if (vfs.ResolveImage(image!) is not null)
                resolved++;
            else if (missing.Count < 12)
                missing.Add($"{kv.Key} -> {image}");
        }

        // Most texture shaders live in the map packs, which are fetched rather than committed (D7).
        int minConsidered = TestPaths.HasMaps ? 200 : 0;
        Assert.True(considered > minConsidered,
            $"only {considered} texture shaders parsed (maps present: {TestPaths.HasMaps}) "
            + "— is the data dir right?");
        if (considered == 0) return;
        double rate = resolved * 100.0 / considered;
        _out.WriteLine($"{resolved}/{considered} texture shaders resolve to a real image ({rate:0.#}%)");
        foreach (string m in missing)
            _out.WriteLine($"  unresolved: {m}");

        // The rate only means something over a representative sample. Without the fetched map packs the
        // core tree yields a handful of texture shaders, and one of those pointing at map art reads as
        // 0% — a statement about what is installed, not about the precedence chain.
        if (!TestPaths.HasMaps || considered < 50)
        {
            _out.WriteLine($"resolution-rate assertion skipped: {considered} texture shaders, "
                           + $"maps present: {TestPaths.HasMaps}. {TestPaths.NoMapsReason}");
            return;
        }

        // Not 100%: some shaders point at art the free data set does not ship, and some tool shaders name
        // nothing at all. Well under 85% would mean the precedence chain, not the content.
        Assert.True(rate >= 85.0, $"only {rate:0.#}% of texture shaders resolve to a real image");
    }
}
