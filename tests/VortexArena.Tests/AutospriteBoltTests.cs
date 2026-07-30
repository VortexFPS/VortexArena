using System;
using System.Numerics;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Md3;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// The blaster/electro bolt "flattened" parity fix (playtest #38 residual —
/// planning/blaster-electro-bolt-autosprite-parity.md): Base's bolt models are zero-thickness quads whose
/// 3-D look comes from Q3 <c>deformVertexes autosprite</c>/<c>autosprite2</c>. These tests pin the two
/// Godot-free halves: <see cref="AutospriteQuads"/> (per-quad center/axis/s/t bake, DP's
/// <c>gl_rmain.c</c> Q3DEFORM_AUTOSPRITE/AUTOSPRITE2 math) against laser.mdl/elaser.mdl-shaped corner
/// data, and <see cref="AutospriteShaderGen"/> (the generated deform shader source — skip_vertex_transform
/// + CUSTOM0/1, additive but NOT unshaded, fullbright <c>_glow</c> EMISSION, <c>tcMod page</c> flipbook).
/// Pure math/text — always runs, no assets.
/// </summary>
public class AutospriteBoltTests
{
    private const float Eps = 1e-4f;

    // ---------------------------------------------------------------- bake: autosprite (core quads)

    // laser.mdl Plane01-shaped: one ~25.5×25.5 quad centered on the origin, all verts at Quake Z=0.
    private static readonly Vector3[] CoreQuad =
    {
        new(-12.75f,  12.75f, 0f),   // uv (0,0) — texture top-left
        new( 12.75f,  12.75f, 0f),   // uv (1,0)
        new( 12.75f, -12.75f, 0f),   // uv (1,1)
        new(-12.75f, -12.75f, 0f),   // uv (0,1)
    };

    private static readonly Vector2[] CoreUvs =
    {
        new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f),
    };

    [Fact]
    public void Autosprite_Bake_CenterAndOffsets_LaserCoreShape()
    {
        var s = new float[4];
        var t = new float[4];
        var quads = new AutospriteQuads.Quad[1];
        Assert.True(AutospriteQuads.Bake(CoreQuad, CoreUvs, axial: false, s, t, quads));

        // Center = mean of the corners = the model origin.
        Assert.Equal(0f, quads[0].Center.Length(), 3);

        // Every corner sits half the quad size out along each tangent — the screen-plane rebuild
        // (center + s*view_right + t*view_up) reproduces the full 25.5×25.5 sprite at any view angle.
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(12.75f, MathF.Abs(s[i]), 3);
            Assert.Equal(12.75f, MathF.Abs(t[i]), 3);
        }

        // s follows increasing U (here +X): the two right-column corners (uv.x=1) share one sign.
        Assert.True(s[1] > 0f && s[2] > 0f && s[0] < 0f && s[3] < 0f,
            $"s must follow the U axis: [{s[0]}, {s[1]}, {s[2]}, {s[3]}]");
        // t follows increasing V: the two bottom-row corners (uv.y=1) share one sign.
        Assert.True(t[2] > 0f && t[3] > 0f && t[0] < 0f && t[1] < 0f,
            $"t must follow the V axis: [{t[0]}, {t[1]}, {t[2]}, {t[3]}]");
    }

    // ---------------------------------------------------------------- bake: autosprite2 (streak quads)

    // laser.mdl Plane02-shaped: 37.6×17.4 streak, center trailing −10.8 on Quake X (behind the bolt),
    // long axis = the flight axis (+X), upright in the X/Z plane.
    private static readonly Vector3[] StreakQuad =
    {
        new(-29.6f, 0f, -8.7f),
        new(  8.0f, 0f, -8.7f),
        new(  8.0f, 0f,  8.7f),
        new(-29.6f, 0f,  8.7f),
    };

    [Fact]
    public void Autosprite2_Bake_AxisRunsBetweenShortEdgeMidpoints_LaserStreakShape()
    {
        var s = new float[4];
        var t = new float[4];
        var quads = new AutospriteQuads.Quad[1];
        Assert.True(AutospriteQuads.Bake(StreakQuad, ReadOnlySpan<Vector2>.Empty, axial: true, s, t, quads));

        // Pivot: the quad's own center, ~10.8 qu behind the bolt origin — NOT the node origin. (The old
        // full-billboard approximation swung the streak around the node origin; this is the fix.)
        Assert.Equal(-10.8f, quads[0].Center.X, 3);
        Assert.Equal(0f, quads[0].Center.Y, 3);
        Assert.Equal(0f, quads[0].Center.Z, 3);

        // Long axis = between the two short (17.4) edges' midpoints = the flight axis ±X.
        Assert.Equal(1f, MathF.Abs(quads[0].Axis.X), 4);
        Assert.Equal(0f, quads[0].Axis.Y, 4);
        Assert.Equal(0f, quads[0].Axis.Z, 4);

        // t = the preserved offset ALONG the axis (half of 37.6); s = the re-aimed width (half of 17.4).
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(18.8f, MathF.Abs(t[i]), 3);
            Assert.Equal(8.7f, MathF.Abs(s[i]), 3);
        }
        // The two head corners (x = 8.0) share the +axis sign; the two tail corners the −.
        Assert.Equal(t[1], t[2], 3);
        Assert.Equal(t[0], t[3], 3);
        Assert.True(t[1] * t[0] < 0f, "head and tail corners must sit on opposite ends of the axis");
    }

    [Fact]
    public void Autosprite2_Bake_SquareQuad_HeightBiasReadsItUpright()
    {
        // DP's 1/1024 length bias on height-changing edges: a perfectly SQUARE quad is ambiguous, and DP
        // resolves it "assuming they are meant to be upright" — the two level edges win the shortest-edge
        // search, so the roll axis comes out vertical (Quake Z).
        var square = new Vector3[]
        {
            new(-5f, 0f, -5f), new(5f, 0f, -5f), new(5f, 0f, 5f), new(-5f, 0f, 5f),
        };
        var s = new float[4];
        var t = new float[4];
        var quads = new AutospriteQuads.Quad[1];
        Assert.True(AutospriteQuads.Bake(square, ReadOnlySpan<Vector2>.Empty, axial: true, s, t, quads));

        Assert.True(MathF.Abs(quads[0].Axis.Z) > 1f - Eps,
            $"square quad must be read upright (axis ±Z), got {quads[0].Axis}");
    }

    [Fact]
    public void Bake_TwoQuadSurface_QuadsAreIndependent()
    {
        // A single autosprite surface can contain multiple sprites (DP walks vertices in groups of 4).
        var both = new Vector3[8];
        CoreQuad.CopyTo(both, 0);
        for (int i = 0; i < 4; i++)
            both[4 + i] = CoreQuad[i] + new Vector3(100f, 0f, 0f); // second quad, displaced
        var uvs = new Vector2[8];
        CoreUvs.CopyTo(uvs, 0);
        CoreUvs.CopyTo(uvs, 4);

        var s = new float[8];
        var t = new float[8];
        var quads = new AutospriteQuads.Quad[2];
        Assert.True(AutospriteQuads.Bake(both, uvs, axial: false, s, t, quads));

        Assert.Equal(0f, quads[0].Center.X, 3);
        Assert.Equal(100f, quads[1].Center.X, 3);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(s[i], s[4 + i], 4);   // identical local frames
            Assert.Equal(t[i], t[4 + i], 4);
        }
    }

    [Fact]
    public void Bake_RejectsNonQuadGeometry()
    {
        var tri = new Vector3[] { new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f) };
        Assert.False(AutospriteQuads.Bake(tri, ReadOnlySpan<Vector2>.Empty, axial: false,
            new float[3], new float[3], new AutospriteQuads.Quad[1]));
        Assert.False(AutospriteQuads.Bake(ReadOnlySpan<Vector3>.Empty, ReadOnlySpan<Vector2>.Empty,
            axial: false, Span<float>.Empty, Span<float>.Empty, Span<AutospriteQuads.Quad>.Empty));
    }

    // ---------------------------------------------------------------- shader generation

    private static (ShaderDef Def, ShaderStage Stage) ElectroLikeDef(bool withPage)
    {
        var def = new ShaderDef { Name = "electro_projectile_core" };
        def.Deforms.Add(new DeformVertexes { Type = DeformType.Autosprite, RawType = "autosprite" });
        var stage = new ShaderStage
        {
            MapTexture = "models/elaser.tga",
            BlendSrc = BlendFactor.One,
            BlendDst = BlendFactor.One,
            BlendMode = BlendMode.Add,
            HasBlendFunc = true,
            RgbGen = new ColorGen { Type = ColorGenType.LightingDiffuse },
        };
        if (withPage)
            stage.TcMods.Add(new TcMod { Type = TcModType.Page, RawType = "page", Parms = new[] { 4f, 1f, 0.1f } });
        def.Stages.Add(stage);
        return (def, stage);
    }

    [Fact]
    public void ShaderGen_Autosprite_ViewPlaneRebuild_NoAxisMath()
    {
        (ShaderDef def, ShaderStage stage) = ElectroLikeDef(withPage: false);
        string src = AutospriteShaderGen.Generate(def, stage, axial: false);

        Assert.Contains("skip_vertex_transform", src);   // VERTEX assigned in view space
        Assert.Contains("cull_disabled", src);
        Assert.Contains("CUSTOM0", src);                  // (center, s)
        Assert.Contains("vec3(CUSTOM0.w, CUSTOM1.w, 0.0)", src); // s→view-right, t→view-up
        Assert.DoesNotContain("cross(ax, fw)", src);      // no axial re-aim on the full billboard
    }

    [Fact]
    public void ShaderGen_Autosprite2_AxialRebuild_KeepsAxisComponent()
    {
        (ShaderDef def, ShaderStage stage) = ElectroLikeDef(withPage: false);
        string src = AutospriteShaderGen.Generate(def, stage, axial: true);

        Assert.Contains("skip_vertex_transform", src);
        Assert.Contains("CUSTOM1.xyz", src);              // the baked flight axis
        Assert.Contains("cross(ax, fw)", src);            // width re-aimed at the camera (DP newright)
        Assert.Contains("ax * (CUSTOM1.w", src);          // axial offset preserved per vertex
    }

    [Fact]
    public void ShaderGen_ShadingIsDpBoltModel_LitBasePlusFullbrightGlow_NotUnshaded()
    {
        // The corrected playtest-#38 model: `blendfunc add` + `rgbGen lightingDiffuse` means the BASE is
        // lit and only the _glow companion is fullbright (EMISSION) — NOT the unshaded look.
        (ShaderDef def, ShaderStage stage) = ElectroLikeDef(withPage: false);
        string src = AutospriteShaderGen.Generate(def, stage, axial: false);

        Assert.Contains("blend_add", src);
        Assert.DoesNotContain("unshaded", src);
        Assert.Contains("glow_tex", src);
        Assert.Contains("EMISSION", src);
        Assert.Contains("colormod", src);                 // per-entity DP colormod/glowmod ride fragment
        Assert.Contains("glowmod", src);                  //   instance uniforms (the safe, working kind)
        Assert.Contains("ALPHA = 1.0", src);              // GL_ONE GL_ONE: alpha must not scale the add
        // DP lightingDiffuse is diffuse-only: Godot's specular lobe lights the whole quad independent
        // of the black albedo — the intermittent "white box around the bolt" — and Godot fog blends
        // additive surfaces toward the fog COLOR (DP fades them toward black). Both must stay off.
        Assert.Contains("SPECULAR = 0.0", src);
        Assert.Contains("ROUGHNESS = 1.0", src);
        Assert.Contains("fog_disabled", src);
    }

    [Fact]
    public void ShaderGen_TcModPage_EmitsDpFlipbookMath()
    {
        // electro's `tcMod page 4 1 0.1` — 4-page horizontal flipbook at 10 fps (DP gl_rmain.c
        // Q3TCMOD_PAGE): idx = floor(fract(TIME / (delay*w*h)) * w*h), uv += (idx%w/w, (idx/w)/h).
        (ShaderDef def, ShaderStage stage) = ElectroLikeDef(withPage: true);
        string src = AutospriteShaderGen.Generate(def, stage, axial: false);

        Assert.Contains("tcMod page", src);
        Assert.Contains("fract(TIME / 0.4)", src);        // delay*w*h = 0.1*4*1
        Assert.Contains("floor(pf * 4.0)", src);          // w*h pages
        Assert.Contains("mod(idx, 4.0) / 4.0", src);      // horizontal page step
    }

    // ---------------------------------------------------------------- parser round-trip

    [Fact]
    public void Parser_TcModPage_RoundTrips()
    {
        var map = Q3ShaderParser.Parse(
            "electro_projectile_core\n{\n{\nmap models/elaser.tga\nblendfunc add\ntcmod page 4 1 0.1\n}\n}\n");
        ShaderDef def = Assert.Single(map.Values);
        ShaderStage stage = Assert.Single(def.Stages);
        TcMod page = Assert.Single(stage.TcMods);
        Assert.Equal(TcModType.Page, page.Type);
        Assert.Equal(4f, page.P(0), 4);
        Assert.Equal(1f, page.P(1), 4);
        Assert.Equal(0.1f, page.P(2), 4);
    }
}
