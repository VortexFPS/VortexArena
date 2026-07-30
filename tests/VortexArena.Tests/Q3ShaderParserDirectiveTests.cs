using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Formats.Materials;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — per-directive coverage for <see cref="Q3ShaderParser"/> (port of Darkplaces
/// <c>Mod_LoadQ3Shaders</c>, model_shared.c — previously only a bulk "500+ materials" count test).
/// Synthetic shader scripts pin: surfaceparm collection (incl. the rebirth-trans-lightmap-grates
/// regression — <c>surfaceparm trans</c> records ONLY the parm, it must not be conflated with
/// nolightmap), blendFunc shorthands + GL pair classification, rgbGen/alphaGen wave parsing, tcMod
/// scroll/stretch, skyParms with '-' slots, deformVertexes, the dp_ → dp keyword remap, the
/// first-declaration-wins duplicate rule, warning-not-throw robustness, and the Raw bag for q3map_*.
/// Pure text — always runs, no assets.
/// </summary>
public class Q3ShaderParserDirectiveTests
{
    private static ShaderDef One(string body, string name = "textures/test/x", List<string>? warnings = null)
    {
        var map = Q3ShaderParser.Parse(name + "\n{\n" + body + "\n}\n",
            warnings is null ? null : (System.Action<string>)warnings.Add);
        Assert.True(map.ContainsKey(name), "shader should have parsed");
        return map[name];
    }

    // ---------------------------------------------------------------- surfaceparms

    [Fact]
    public void SurfaceParm_Trans_RecordsOnlyTheParm()
    {
        // Regression pin (rebirth-trans-lightmap-grates): 'surfaceparm trans' must surface as the 'trans'
        // parm ONLY — DP keys lightmapping off the BSP face index, not off this parm, so the parser must
        // not invent 'nolightmap'.
        ShaderDef def = One("surfaceparm trans\nsurfaceparm nomarks");
        Assert.Contains("trans", def.SurfaceParms);
        Assert.Contains("nomarks", def.SurfaceParms);
        Assert.DoesNotContain("nolightmap", def.SurfaceParms);
    }

    [Fact]
    public void SurfaceParms_CaseFoldedAndQueryable()
    {
        ShaderDef def = One("surfaceparm NoDraw\nsurfaceparm NONSOLID");
        Assert.Contains("nodraw", def.SurfaceParms);
        Assert.Contains("nonsolid", def.SurfaceParms);
        Assert.True(def.IsNoDraw);
    }

    // ---------------------------------------------------------------- blendFunc

    [Fact]
    public void BlendFunc_Shorthands_ClassifyCorrectly()
    {
        Assert.Equal(BlendMode.Add, StageOf("map x.tga\nblendFunc add").BlendMode);
        Assert.Equal(BlendMode.Blend, StageOf("map x.tga\nblendFunc blend").BlendMode);
        Assert.Equal(BlendMode.Filter, StageOf("map x.tga\nblendFunc filter").BlendMode);
    }

    [Fact]
    public void BlendFunc_GlPairs_ClassifyCorrectly()
    {
        ShaderStage add = StageOf("map x.tga\nblendFunc GL_ONE GL_ONE");
        Assert.Equal(BlendFactor.One, add.BlendSrc);
        Assert.Equal(BlendFactor.One, add.BlendDst);
        Assert.Equal(BlendMode.Add, add.BlendMode);

        ShaderStage blend = StageOf("map x.tga\nblendFunc GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA");
        Assert.Equal(BlendMode.Blend, blend.BlendMode);

        ShaderStage opaque = StageOf("map x.tga\nblendFunc GL_ONE GL_ZERO");
        Assert.Equal(BlendMode.Opaque, opaque.BlendMode);

        ShaderStage filter2 = StageOf("map x.tga\nblendFunc GL_ZERO GL_SRC_COLOR");
        Assert.Equal(BlendMode.Filter, filter2.BlendMode);

        ShaderStage custom = StageOf("map x.tga\nblendFunc GL_DST_ALPHA GL_ONE");
        Assert.Equal(BlendMode.Custom, custom.BlendMode);
    }

    [Fact]
    public void NoBlendFunc_IsOpaque()
    {
        ShaderStage s = StageOf("map x.tga");
        Assert.False(s.HasBlendFunc);
        Assert.Equal(BlendMode.Opaque, s.BlendMode);
    }

    private static ShaderStage StageOf(string stageBody)
    {
        ShaderDef def = One("{\n" + stageBody + "\n}");
        return def.Stages.Single();
    }

    // ---------------------------------------------------------------- rgbGen / tcMod / tcGen

    [Fact]
    public void RgbGenWave_ParsesFuncAndParms()
    {
        ShaderStage s = StageOf("map x.tga\nrgbGen wave sin 0.5 0.2 0 1.5");
        Assert.NotNull(s.RgbGen);
        Assert.Equal(ColorGenType.Wave, s.RgbGen!.Type);
        Assert.Equal(WaveFunc.Sin, s.RgbGen.Wave!.Func);
        Assert.Equal(0.5f, s.RgbGen.Wave.Base);
        Assert.Equal(0.2f, s.RgbGen.Wave.Amplitude);
        Assert.Equal(0f, s.RgbGen.Wave.Phase);
        Assert.Equal(1.5f, s.RgbGen.Wave.Frequency);
    }

    [Fact]
    public void RgbGen_NamedModes_Parse()
    {
        Assert.Equal(ColorGenType.Identity, StageOf("map x.tga\nrgbGen identity").RgbGen!.Type);
        Assert.Equal(ColorGenType.Vertex, StageOf("map x.tga\nrgbGen vertex").RgbGen!.Type);
        Assert.Equal(ColorGenType.LightingDiffuse, StageOf("map x.tga\nrgbGen lightingDiffuse").RgbGen!.Type);
        ShaderStage c = StageOf("map x.tga\nrgbGen const 0.25 0.5 0.75");
        Assert.Equal(ColorGenType.Const, c.RgbGen!.Type);
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f }, c.RgbGen.Parms);
    }

    [Fact]
    public void AnimMap_MortarSightStage_ParsesFramesFpsAndWave()
    {
        // Pin the EXACT Base scripts/gl.shader grenadelauncher_sight stage (playtest r14 D): a 3-frame
        // animMap at 1 fps plus the rgbGen sawtooth blink. ShaderCompiler.NeedsAnimatedShader keys the
        // animated (cycling + pulsing) path off Frames.Length > 1 and the Wave — this is the data contract.
        ShaderStage s = StageOf(
            "animMap 1 textures/glsight01.tga textures/glsight02.tga textures/glsight03.tga\n" +
            "blendFunc GL_ONE GL_ONE\n" +
            "rgbGen wave sawtooth 0 1 0 10");
        Assert.NotNull(s.AnimMap);
        Assert.Equal(1f, s.AnimMap!.Fps);
        Assert.False(s.AnimMap.Clamp);
        Assert.Equal(
            new[] { "textures/glsight01.tga", "textures/glsight02.tga", "textures/glsight03.tga" },
            s.AnimMap.Frames);
        Assert.Equal(ColorGenType.Wave, s.RgbGen!.Type);
        Assert.Equal(WaveFunc.Sawtooth, s.RgbGen.Wave!.Func);
        Assert.Equal(0f, s.RgbGen.Wave.Base);
        Assert.Equal(1f, s.RgbGen.Wave.Amplitude);
        Assert.Equal(0f, s.RgbGen.Wave.Phase);
        Assert.Equal(10f, s.RgbGen.Wave.Frequency);
        Assert.Equal(BlendMode.Add, s.BlendMode);
    }

    [Fact]
    public void AnimClampMap_SetsClampFlag()
    {
        ShaderStage s = StageOf("animClampMap 4 a.tga b.tga");
        Assert.NotNull(s.AnimMap);
        Assert.True(s.AnimMap!.Clamp);
        Assert.Equal(4f, s.AnimMap.Fps);
        Assert.Equal(2, s.AnimMap.Frames.Length);
    }

    [Fact]
    public void TcModScroll_AndStretch_Parse()
    {
        ShaderStage s = StageOf("map x.tga\ntcMod scroll 0.1 -0.2\ntcMod stretch sin 0.8 0.1 0 0.5");
        Assert.Equal(2, s.TcMods.Count);

        TcMod scroll = s.TcMods[0];
        Assert.Equal(TcModType.Scroll, scroll.Type);
        Assert.Equal(0.1f, scroll.P(0));
        Assert.Equal(-0.2f, scroll.P(1));

        TcMod stretch = s.TcMods[1];
        Assert.Equal(TcModType.Stretch, stretch.Type);
        Assert.Equal(WaveFunc.Sin, stretch.Wave!.Func);
        Assert.Equal(0.8f, stretch.Wave.Base);
    }

    [Fact]
    public void TcGenEnvironment_AndLightmapStage_Parse()
    {
        ShaderStage env = StageOf("map x.tga\ntcGen environment");
        Assert.Equal(TcGenType.Environment, env.TcGen!.Type);

        ShaderDef def = One("{\nmap $lightmap\n}\n{\nmap x.tga\n}");
        Assert.True(def.UsesLightmap);
        Assert.True(def.Stages[0].IsLightmap);
    }

    // ---------------------------------------------------------------- skyParms / deformVertexes

    [Fact]
    public void SkyParms_DashMeansNone_AndForcesSkyParm()
    {
        ShaderDef def = One("skyParms env/distant_sunset/distant_sunset - -");
        Assert.True(def.IsSky);
        Assert.Contains("sky", def.SurfaceParms);
        Assert.Equal("env/distant_sunset/distant_sunset", def.SkyParms!.FarBox);
        Assert.Null(def.SkyParms.CloudHeight);
        Assert.Null(def.SkyParms.NearBox);
    }

    [Fact]
    public void DeformVertexes_Wave_ParsesDivAndWave()
    {
        ShaderDef def = One("deformVertexes wave 100 sin 0 3 0 0.1");
        DeformVertexes d = def.Deforms.Single();
        Assert.Equal(DeformType.Wave, d.Type);
        Assert.Equal(100f, d.Parms[0]);              // div
        Assert.Equal(WaveFunc.Sin, d.Wave!.Func);
        Assert.Equal(3f, d.Wave.Amplitude);
        Assert.Equal(0.1f, d.Wave.Frequency);
    }

    [Fact]
    public void DeformVertexes_Autosprite_Parses()
    {
        ShaderDef def = One("deformVertexes autosprite");
        Assert.Equal(DeformType.Autosprite, def.Deforms.Single().Type);
    }

    // ---------------------------------------------------------------- dp_ remap + extensions

    [Fact]
    public void DpUnderscorePrefix_RemapsToDpKeyword()
    {
        // DP rewrites a leading "dp_" to "dp": dp_reflect must hit the dpreflect handler.
        ShaderDef def = One("dp_reflect 0.5 1 0.9 0.8 0.7");
        Assert.NotNull(def.Dp.Reflect);
        Assert.Equal(0.5f, def.Dp.Reflect!.Factor);
        Assert.Equal(0.7f, def.Dp.Reflect.A);
    }

    [Fact]
    public void DpGlossTexture_AndDpNoShadow_Parse()
    {
        ShaderDef def = One("dpglosstexture textures/test/x_gloss\ndpnoshadow");
        Assert.Equal("textures/test/x_gloss", def.Dp.GlossTexture);
        Assert.True(def.Dp.NoShadow);
    }

    // ---------------------------------------------------------------- robustness contracts

    [Fact]
    public void UnknownGlobalDirective_LandsInRaw_NotAnError()
    {
        ShaderDef def = One("q3map_surfacelight 400\ntotally_unknown_keyword a b");
        Assert.Equal("400", def.Raw["q3map_surfacelight"]);
        Assert.Equal("a b", def.Raw["totally_unknown_keyword"]);
    }

    [Fact]
    public void UnknownTcMod_FiresWarning_DoesNotThrow()
    {
        var warnings = new List<string>();
        ShaderDef def = One("{\nmap x.tga\ntcMod wobble 1 2\n}", warnings: warnings);
        Assert.Empty(def.Stages.Single().TcMods);
        Assert.Contains(warnings, w => w.Contains("tcMod"));
    }

    [Fact]
    public void DuplicateShaderName_FirstDeclarationWins()
    {
        var map = Q3ShaderParser.Parse(
            "textures/a\n{\nsurfaceparm nodraw\n}\ntextures/a\n{\nsurfaceparm lava\n}\n");
        Assert.Single(map);
        Assert.True(map["textures/a"].IsNoDraw);
        Assert.DoesNotContain("lava", map["textures/a"].SurfaceParms);
    }

    [Fact]
    public void ParseFiles_FirstFileWinsAcrossFiles()
    {
        var map = Q3ShaderParser.ParseFiles(new[]
        {
            "textures/a\n{\nsurfaceparm nodraw\n}\n",
            "textures/a\n{\nsurfaceparm lava\n}\ntextures/b\n{\n}\n",
        });
        Assert.Equal(2, map.Count);
        Assert.True(map["textures/a"].IsNoDraw);
    }

    [Fact]
    public void NameLookup_IsCaseInsensitive()
    {
        var map = Q3ShaderParser.Parse("textures/Test/Wall\n{\n}\n");
        Assert.True(map.ContainsKey("TEXTURES/test/wall"));
    }

    [Fact]
    public void MalformedShader_IsSkipped_FollowingShaderStillParses()
    {
        var warnings = new List<string>();
        var map = Q3ShaderParser.Parse(
            "braceless_junk_line\ntextures/good\n{\nsurfaceparm nomarks\n}\n", warnings.Add);
        Assert.True(map.ContainsKey("textures/good"));
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void CommentsAndQuotedTokens_AreHandled()
    {
        ShaderDef def = One("// line comment\n/* block */ surfaceparm trans\n{\nmap \"textures/with space.tga\"\n}");
        Assert.Contains("trans", def.SurfaceParms);
        Assert.Equal("textures/with space.tga", def.Stages.Single().MapTexture);
    }

    [Fact]
    public void ExtraStagesBeyondMaxLayers_AreDropped()
    {
        // DP caps at 8 stages (Q3SHADER_MAXLAYERS); extras are parsed but dropped.
        string stages = string.Concat(Enumerable.Range(0, 10).Select(i => "{\nmap s" + i + ".tga\n}\n"));
        ShaderDef def = One(stages);
        Assert.Equal(8, def.Stages.Count);
        Assert.Equal("s0.tga", def.Stages[0].MapTexture);
        Assert.Equal("s7.tga", def.Stages[7].MapTexture);
    }
}
