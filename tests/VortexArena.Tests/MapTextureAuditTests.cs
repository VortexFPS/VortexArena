using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Materials;
using VortexArena.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="MapTextureAudit"/> — the analysis behind <c>r_missingtextures</c>.
///
/// The verdicts have to be pinned because both ways of being wrong are silent in the game. A false positive
/// sends a mapper hunting for a file that was never supposed to exist (caulk, <c>$lightmap</c>, a nodraw
/// clip brush); a false negative is the whole reason the command exists, since the engine it is modelled on
/// says nothing at all when a shader's stage image is missing.
/// </summary>
public class MapTextureAuditTests
{
    private readonly ITestOutputHelper _out;
    public MapTextureAuditTests(ITestOutputHelper output) => _out = output;

    // ---- fixtures -------------------------------------------------------------------------------

    private static BspData Map(params BspTexture[] textures)
        => new() { Textures = textures, Faces = Array.Empty<BspFace>() };

    /// <summary>A map whose worldspawn carries the given keys (e.g. <c>"sky", "env/foo/foo"</c>).</summary>
    private static BspData MapWithWorldspawn(BspTexture[] textures, params string[] keyValuePairs)
    {
        var ws = new Dictionary<string, string>(StringComparer.Ordinal) { ["classname"] = "worldspawn" };
        for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
            ws[keyValuePairs[i]] = keyValuePairs[i + 1];
        return new BspData
        {
            Textures = textures,
            Faces = Array.Empty<BspFace>(),
            Entities = new IReadOnlyDictionary<string, string>[] { ws },
        };
    }

    /// <summary>
    /// The six face paths of one suffix convention, each written in ONE of the four DP path forms
    /// (<paramref name="form"/>: 0 = <c>NAME_suf</c>, 1 = <c>NAMEsuf</c>, 2 = <c>env/…</c>, 3 = <c>gfx/env/…</c>).
    /// Picking a single form is the point — feeding every form to the resolver would prove nothing about which
    /// ones the audit actually probes.
    /// </summary>
    private static string[] SkyFaces(string name, int convention, int form = 0)
        => SkyboxPaths.Suffixes[convention]
            .Select(suffix => SkyboxPaths.FaceCandidates(name, suffix).ElementAt(form))
            .ToArray();

    private static BspTexture Tex(string name, int surfaceFlags = 0) => new(name, surfaceFlags, 0);

    private static BspFace Face(int textureIndex, BspFaceType type = BspFaceType.Flat)
        => new(textureIndex, 0, type, 0, 3, 0, 3, -1, 0, 0);

    /// <summary>A shader table from source text, in the form the audit consumes it (name → def or null).</summary>
    private static Func<string, ShaderDef?> Shaders(string text)
    {
        IReadOnlyDictionary<string, ShaderDef> defs = Q3ShaderParser.Parse(text);
        return name => defs.TryGetValue(name, out ShaderDef? def) ? def : null;
    }

    private static Func<string, ShaderDef?> NoShaders() => _ => null;

    /// <summary>An image resolver where exactly the listed base names exist.</summary>
    private static Func<string, bool> Images(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static MapTextureAudit.Entry Find(MapTextureAudit.Report r, string name)
        => r.Entries.Single(e => e.Name == name);

    // ---- the shaderless case (the one DarkPlaces already shouts about) --------------------------

    /// <summary>With no shader the name IS the image, so its absence is the whole defect.</summary>
    [Fact]
    public void AShaderlessTextureWithNoImageIsMissing()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/wall")), NoShaders(), Images());

        Assert.Equal(MapTextureAudit.Status.Missing, Find(r, "textures/x/wall").Status);
        Assert.Equal(1, r.MissingCount);
        Assert.False(r.Clean);
    }

    [Fact]
    public void AShaderlessTextureWhoseImageResolvesIsOk()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/wall")), NoShaders(), Images("textures/x/wall"));

        Assert.Equal(MapTextureAudit.Status.Ok, Find(r, "textures/x/wall").Status);
        Assert.True(r.Clean);
    }

    // ---- the case the reference engine is silent about ------------------------------------------

    /// <summary>
    /// The headline: a shader that parses fine while one of its stages points at a file nobody shipped.
    /// DarkPlaces loads the notexture placeholder for that stage without a word
    /// (<c>R_SkinFrame_LoadExternal(…, complain: false)</c>), which is exactly how a pk3 missing its texture
    /// folder gets all the way to a player. The audit has to name the FILE, not just the shader.
    /// </summary>
    [Fact]
    public void AShaderWhoseStageImageIsMissingIsReportedWithTheFileName()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/glass")),
            Shaders("""
                textures/x/glass
                {
                    { map $lightmap }
                    { map textures/x/glass_d }
                    { map textures/effects/glass_env }
                }
                """),
            Images("textures/x/glass_d"));

        MapTextureAudit.Entry e = Find(r, "textures/x/glass");
        Assert.Equal(MapTextureAudit.Status.Partial, e.Status);
        Assert.True(e.HasShader);
        Assert.Equal(new[] { "textures/effects/glass_env" }, e.MissingImages);
        Assert.Equal(1, r.PartialCount);
        Assert.Equal(0, r.MissingCount);
    }

    /// <summary>When nothing at all resolves the surface draws as the checkerboard — that is Missing, not Partial.</summary>
    [Fact]
    public void AShaderWithNoResolvableStageIsMissingNotPartial()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/glass")),
            Shaders("""
                textures/x/glass
                {
                    { map $lightmap }
                    { map textures/x/glass_d }
                }
                """),
            Images());

        Assert.Equal(MapTextureAudit.Status.Missing, Find(r, "textures/x/glass").Status);
    }

    /// <summary>
    /// <c>$lightmap</c> and <c>$whiteimage</c> are generated by the engine and have no file behind them. A
    /// lightmap-only shader is complete as written; reporting it would flag a large share of every stock map.
    /// </summary>
    [Fact]
    public void EngineGeneratedStagesAreNotFilesAndCannotBeMissing()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/lit")),
            Shaders("""
                textures/x/lit
                {
                    { map $lightmap }
                    { map $whiteimage }
                }
                """),
            Images());

        Assert.Equal(MapTextureAudit.Status.Ok, Find(r, "textures/x/lit").Status);
        Assert.True(r.Clean);
    }

    /// <summary>
    /// Stage maps are written with an extension about as often as not. The loader strips it before probing
    /// (<c>Image_StripImageExtension</c>); if the audit did not, every such stage would read as missing.
    /// </summary>
    [Fact]
    public void AStageMapWrittenWithAnExtensionProbesTheStrippedName()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/wall")),
            Shaders("""
                textures/x/wall
                {
                    { map textures/x/wall_d.tga }
                }
                """),
            Images("textures/x/wall_d"));

        Assert.Equal(MapTextureAudit.Status.Ok, Find(r, "textures/x/wall").Status);
    }

    /// <summary>Every animMap frame is a real file the compiler loads, so a hole anywhere in the sequence counts.</summary>
    [Fact]
    public void AMissingAnimMapFrameCounts()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/fan")),
            Shaders("""
                textures/x/fan
                {
                    { animMap 4 textures/x/fan1 textures/x/fan2 textures/x/fan3 }
                }
                """),
            Images("textures/x/fan1", "textures/x/fan3"));

        MapTextureAudit.Entry e = Find(r, "textures/x/fan");
        Assert.Equal(MapTextureAudit.Status.Partial, e.Status);
        Assert.Equal(new[] { "textures/x/fan2" }, e.MissingImages);
    }

    /// <summary>A pure surfaceparm shader (clip volumes, fog brushes) has no images to be missing.</summary>
    [Fact]
    public void AShaderWithNoImageStagesIsOk()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/hint")),
            Shaders("""
                textures/x/hint
                {
                    surfaceparm nonsolid
                    surfaceparm trans
                }
                """),
            Images());

        Assert.Equal(MapTextureAudit.Status.Ok, Find(r, "textures/x/hint").Status);
    }

    // ---- exclusions: things that are supposed to have no image ----------------------------------

    /// <summary>The BSP lump's own NODRAW/SKY bits. A caulk face has no image by design.</summary>
    [Theory]
    [InlineData(0x0080)] // Q3SURFACEFLAG_NODRAW
    [InlineData(0x0004)] // Q3SURFACEFLAG_SKY
    public void SurfacesTheLumpMarksAsNeverDrawnAreExcluded(int surfaceFlags)
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/invisible", surfaceFlags)), NoShaders(), Images());

        Assert.Equal(MapTextureAudit.Status.NotDrawn, Find(r, "textures/x/invisible").Status);
        Assert.Equal(0, r.MissingCount);
        Assert.Equal(1, r.NotDrawnCount);
        Assert.True(r.Clean);
    }

    /// <summary>
    /// Xonotic declares nodraw/sky in the <c>.shader</c> far more often than it sets the BSP bit, which is why
    /// <c>MapLoader.ShouldSkip</c> unions both authorities. Trusting the lump alone would flag every one.
    /// </summary>
    [Theory]
    [InlineData("nodraw")]
    [InlineData("sky")]
    public void SurfacesTheShaderMarksAsNeverDrawnAreExcluded(string parm)
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/hidden")),
            Shaders($$"""
                textures/x/hidden
                {
                    surfaceparm {{parm}}
                    { map textures/x/nothere }
                }
                """),
            Images());

        Assert.Equal(MapTextureAudit.Status.NotDrawn, Find(r, "textures/x/hidden").Status);
        Assert.True(r.Clean);
    }

    /// <summary>
    /// q3map2's own vocabulary — the exclusion set Xonotic's <c>bsptool-shaderfun.sh</c> uses. Checked before
    /// the shader lookup on purpose: an install whose common.shader failed to mount should report that once,
    /// not as a wall of caulk.
    /// </summary>
    [Theory]
    [InlineData("textures/common/caulk")]
    [InlineData("textures/common/clip")]
    [InlineData("noshader")]
    [InlineData("NULL")]
    public void CompilerOnlyNamesAreExcludedEvenWithNoShaderTable(string name)
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(Map(Tex(name)), NoShaders(), Images());

        Assert.Equal(MapTextureAudit.Status.NotDrawn, Find(r, name).Status);
        Assert.True(r.Clean);
    }

    // ---- face weighting and ordering ------------------------------------------------------------

    /// <summary>
    /// The face count is what turns a list into a priority order: one missing texture on 800 faces is the
    /// map, and one on two faces is a trim detail. Flares carry no geometry and must not inflate it.
    /// </summary>
    [Fact]
    public void FacesAreCountedPerTextureAndFlaresAreExcluded()
    {
        var bsp = new BspData
        {
            Textures = new[] { Tex("textures/x/floor"), Tex("textures/x/trim") },
            Faces = new[]
            {
                Face(0), Face(0), Face(0, BspFaceType.Patch),
                Face(0, BspFaceType.Flare),   // no geometry — not a surface anyone sees
                Face(1),
            },
        };

        MapTextureAudit.Report r = MapTextureAudit.Scan(bsp, NoShaders(), Images());

        Assert.Equal(3, Find(r, "textures/x/floor").FaceCount);
        Assert.Equal(1, Find(r, "textures/x/trim").FaceCount);
        Assert.Equal(4, r.FacesAffected);
    }

    /// <summary>Worst first, then by how much of the map wears it — the order you would fix them in.</summary>
    [Fact]
    public void EntriesAreOrderedWorstFirstThenByFaceCount()
    {
        var bsp = new BspData
        {
            Textures = new[]
            {
                Tex("textures/x/fine"),        // 0 — resolves
                Tex("textures/x/small_hole"),  // 1 — missing, 1 face
                Tex("textures/x/partial"),     // 2 — partial, 50 faces
                Tex("textures/x/big_hole"),    // 3 — missing, 10 faces
            },
            Faces = Enumerable.Range(0, 1).Select(_ => Face(1))
                .Concat(Enumerable.Range(0, 50).Select(_ => Face(2)))
                .Concat(Enumerable.Range(0, 10).Select(_ => Face(3)))
                .ToArray(),
        };

        MapTextureAudit.Report r = MapTextureAudit.Scan(
            bsp,
            Shaders("""
                textures/x/partial
                {
                    { map textures/x/partial_d }
                    { map textures/x/partial_env }
                }
                """),
            Images("textures/x/fine", "textures/x/partial_d"));

        Assert.Equal(
            new[] { "textures/x/big_hole", "textures/x/small_hole", "textures/x/partial", "textures/x/fine" },
            r.Entries.Select(e => e.Name).ToArray());

        // Partial counts toward the blast radius too — those 50 faces render with a stage missing.
        Assert.Equal(61, r.FacesAffected);
        Assert.Equal(2, r.MissingCount);
        Assert.Equal(1, r.PartialCount);
    }

    // ---- skybox ---------------------------------------------------------------------------------

    /// <summary>
    /// A skybox is invisible to the surface listing: the sky FACES draw nothing (the box is drawn around the
    /// view instead), so a map whose every wall resolves can still render a blank void overhead. That is the
    /// gap this check exists to close, and it has to make the whole report dirty.
    /// </summary>
    [Fact]
    public void ABrokenSkyboxIsReportedAndMakesTheMapNotClean()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            MapWithWorldspawn(new[] { Tex("textures/x/wall") }, "sky", "env/distant/distant"),
            NoShaders(),
            Images("textures/x/wall"));

        Assert.True(r.Sky.Declared);
        Assert.True(r.Sky.Broken);
        Assert.True(r.Sky.NothingFound);
        Assert.Equal("env/distant/distant", r.Sky.Name);
        Assert.False(r.Clean);           // every surface texture is present — only the sky is gone
        Assert.Equal(0, r.MissingCount);
    }

    /// <summary>All six faces of one convention present is exactly what the loader requires.</summary>
    [Fact]
    public void ASkyboxWithAllSixFacesResolves()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            MapWithWorldspawn(Array.Empty<BspTexture>(), "sky", "env/distant/distant"),
            NoShaders(),
            Images(SkyFaces("env/distant/distant", convention: 0)));

        Assert.True(r.Sky.Resolved);
        Assert.False(r.Sky.Broken);
        Assert.Empty(r.Sky.MissingFaces);
        Assert.True(r.Clean);
    }

    /// <summary>
    /// DP takes the first COMPLETE convention, not the first with any hit. A map with a couple of stray
    /// <c>px/nx</c> files alongside a full <c>rt/lf</c> set loads fine, and reporting it as broken would be a
    /// false alarm on content that works.
    /// </summary>
    [Fact]
    public void ACompleteLaterConventionWinsOverAPartialEarlierOne()
    {
        string[] strays = SkyFaces("env/x/x", convention: 0).Take(2).ToArray();
        string[] complete = SkyFaces("env/x/x", convention: 2);

        MapTextureAudit.Report r = MapTextureAudit.Scan(
            MapWithWorldspawn(Array.Empty<BspTexture>(), "sky", "env/x/x"),
            NoShaders(),
            Images(strays.Concat(complete).ToArray()));

        Assert.True(r.Sky.Resolved);
        Assert.Equal(SkyboxPaths.Suffixes[2], r.Sky.Convention);
    }

    /// <summary>
    /// When nothing is complete, the closest convention is the author's intent — so its gaps are the files to
    /// go and find. Naming faces from a convention the map never used would send someone hunting for the
    /// wrong thing entirely.
    /// </summary>
    [Fact]
    public void AnIncompleteSkyboxNamesTheMissingFacesOfTheClosestConvention()
    {
        // Five of the six rt/lf faces, and nothing at all in the other two conventions.
        string[] almost = SkyFaces("env/x/x", convention: 2).Where(p => !p.EndsWith("dn")).ToArray();

        MapTextureAudit.Report r = MapTextureAudit.Scan(
            MapWithWorldspawn(Array.Empty<BspTexture>(), "sky", "env/x/x"),
            NoShaders(),
            Images(almost));

        Assert.True(r.Sky.Broken);
        Assert.False(r.Sky.NothingFound);
        Assert.Equal(SkyboxPaths.Suffixes[2], r.Sky.Convention);
        Assert.Equal(new[] { "dn" }, r.Sky.MissingFaces);
    }

    /// <summary>
    /// DP probes four path forms per face. A skybox shipped only under <c>gfx/env/</c> — the last form — is
    /// perfectly loadable, so the audit has to try them all or it invents missing skies.
    /// </summary>
    [Theory]
    [InlineData(0)] // NAME_suf
    [InlineData(1)] // NAMEsuf
    [InlineData(2)] // env/NAMEsuf
    [InlineData(3)] // gfx/env/NAMEsuf
    public void EveryDpPathFormCountsAsFound(int form)
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            MapWithWorldspawn(Array.Empty<BspTexture>(), "sky", "bigsky"),
            NoShaders(),
            Images(SkyFaces("bigsky", convention: 1, form)));

        Assert.True(r.Sky.Resolved);
    }

    /// <summary>The worldspawn key overrides the shader default (DP <c>CL_ParseEntityLump</c>).</summary>
    [Fact]
    public void TheWorldspawnSkyKeyBeatsTheShaderSkyParms()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            MapWithWorldspawn(new[] { Tex("textures/x/sky") }, "sky", "env/override/override"),
            Shaders("""
                textures/x/sky
                {
                    surfaceparm sky
                    skyParms env/shaderdefault/shaderdefault - -
                }
                """),
            Images());

        Assert.Equal("env/override/override", r.Sky.Name);
    }

    /// <summary>With no worldspawn key, the first sky shader's <c>skyParms</c> far box is the map's default.</summary>
    [Fact]
    public void TheShaderSkyParmsSuppliesTheDefaultName()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/sky")),
            Shaders("""
                textures/x/sky
                {
                    surfaceparm sky
                    skyParms env/shaderdefault/shaderdefault - -
                }
                """),
            Images());

        Assert.Equal("env/shaderdefault/shaderdefault", r.Sky.Name);
        Assert.True(r.Sky.Broken);
    }

    /// <summary>
    /// An indoor map, or one whose sky is a drawn shader dome rather than a box, declares no skybox — and
    /// must not be reported as missing one.
    /// </summary>
    [Fact]
    public void AMapThatDeclaresNoSkyboxIsNotBroken()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/wall")), NoShaders(), Images("textures/x/wall"));

        Assert.False(r.Sky.Declared);
        Assert.False(r.Sky.Broken);
        Assert.True(r.Clean);
    }

    /// <summary>
    /// <c>skyParms -</c> means "no far box" (the shader supplies only a cloud layer or near box). It is not a
    /// skybox named <c>-</c>, and treating it as one would report a missing <c>-_rt.tga</c> on real content.
    /// </summary>
    [Fact]
    public void ADashFarBoxIsNotASkyboxName()
    {
        MapTextureAudit.Report r = MapTextureAudit.Scan(
            Map(Tex("textures/x/sky")),
            Shaders("""
                textures/x/sky
                {
                    surfaceparm sky
                    skyParms - 128 -
                }
                """),
            Images());

        Assert.False(r.Sky.Declared);
        Assert.True(r.Clean);
    }

    // ---- real data ------------------------------------------------------------------------------

    /// <summary>
    /// Run the audit over every installed map against the real shader table and the real search path.
    ///
    /// <para>This is the case the synthetic tests cannot reach: it is the one that would catch the audit
    /// mis-classifying a whole convention (a stage form nothing above uses, a name shape only shipped content
    /// has) and reporting hundreds of textures that are perfectly fine. The stock packs are known-good content
    /// — the fetcher pins them — so a wall of "missing" here means the analysis is wrong, not the maps.</para>
    ///
    /// <para>It doubles as a guard on the asset pipeline itself: if a repack or a VFS precedence change ever
    /// stops resolving a class of texture, this goes red with the names.</para>
    /// </summary>
    [Fact]
    public void StockMapsAuditClean()
    {
        if (!Directory.Exists(TestPaths.Data))
        {
            _out.WriteLine($"content dir '{TestPaths.Data}' missing — skipped");
            return;
        }

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(TestPaths.Data));

        string[] maps = vfs.Find("maps/", "bsp").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (maps.Length == 0)
        {
            _out.WriteLine($"no compiled maps — skipped. {TestPaths.NoMapsReason}");
            return;
        }

        IReadOnlyDictionary<string, ShaderDef> shaders =
            Q3ShaderParser.ParseFiles(vfs.Find("scripts/", "shader").Select(vfs.ReadText));
        Func<string, ShaderDef?> lookup = n => shaders.TryGetValue(n, out ShaderDef? d) ? d : null;
        Func<string, bool> exists = n => vfs.ResolveImage(n) is not null;

        int scanned = 0, dirtyMaps = 0, totalMissing = 0, totalPartial = 0, skiesDeclared = 0;
        var worst = new List<string>();

        foreach (string vpath in maps)
        {
            BspData bsp;
            try { bsp = BspReader.Read(vfs.ReadBytes(vpath)); }
            catch (Exception ex) { _out.WriteLine($"  {vpath}: unreadable ({ex.Message})"); continue; }

            MapTextureAudit.Report r = MapTextureAudit.Scan(bsp, lookup, exists);
            scanned++;
            if (r.Sky.Declared)
                skiesDeclared++;
            if (r.Clean)
                continue;

            dirtyMaps++;
            totalMissing += r.MissingCount;
            totalPartial += r.PartialCount;
            _out.WriteLine($"{vpath}: {r.MissingCount} missing, {r.PartialCount} partial " +
                           $"({r.FacesAffected} faces) of {r.TextureCount} textures" +
                           (r.Sky.Broken ? $"; skybox '{r.Sky.Name}' missing {r.Sky.MissingFaces.Count} face(s)" : ""));
            if (r.Sky.Broken && worst.Count < 20)
                worst.Add($"{vpath}: skybox '{r.Sky.Name}' -> missing {string.Join(",", r.Sky.MissingFaces)}");
            foreach (MapTextureAudit.Entry e in r.Entries.Take(6))
            {
                if (e.Status is not (MapTextureAudit.Status.Missing or MapTextureAudit.Status.Partial))
                    break;
                string detail = e.MissingImages.Count > 0 && e.HasShader
                    ? $" -> {string.Join(", ", e.MissingImages)}"
                    : string.Empty;
                _out.WriteLine($"    {e.Status} {e.FaceCount,5} faces  {e.Name}{detail}");
                if (worst.Count < 20)
                    worst.Add($"{vpath}: {e.Name}{detail}");
            }
        }

        _out.WriteLine($"{scanned - dirtyMaps}/{scanned} maps audit clean " +
                       $"({totalMissing} missing, {totalPartial} partial across the set; " +
                       $"{skiesDeclared} declare a skybox)");

        // The skybox half needs its own floor. Stock Xonotic maps are overwhelmingly outdoor, so if the name
        // resolution or the suffix/path tables were wrong this count would collapse toward zero while the
        // clean-map assertion below stayed perfectly green — a check that silently stopped checking.
        Assert.True(skiesDeclared >= scanned / 2,
            $"only {skiesDeclared} of {scanned} maps declare a skybox — SkyboxPaths name resolution looks broken");

        // A handful of stock maps genuinely ship a broken reference, so this is a ceiling rather than zero —
        // but the ceiling is low enough that a systematic mis-classification cannot hide under it. Scaled to
        // the set actually installed, since a partial fetch is a supported checkout.
        int ceiling = Math.Max(2, scanned / 4);
        Assert.True(dirtyMaps <= ceiling,
            $"{dirtyMaps} of {scanned} stock maps report missing textures (ceiling {ceiling}) — "
            + "the audit is likely mis-classifying a convention rather than the content being broken:\n  "
            + string.Join("\n  ", worst));
    }
}
