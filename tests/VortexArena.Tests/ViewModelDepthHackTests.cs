// Port of Base/darkplaces gl_rmain.c:6214/8581 — the first-person viewmodel depth hack:
//   GL_DepthRange(0, (flags & RENDER_VIEWMODEL) ? 0.0625 : 1)   /  MATERIALFLAG_SHORTDEPTHRANGE
// CSQC RF_VIEWMODEL (csprogs.c:398) maps to RENDER_VIEWMODEL|RENDER_NODEPTHTEST, and gl_rmain.c:6768 turns
// BOTH into MATERIALFLAG_SHORTDEPTHRANGE — DP never disables the depth test for viewmodel MODEL surfaces; it
// compresses their depth into the nearest 1/16 of the depth buffer (self-occlusion preserved, world always
// loses). The port implements this as a clip-space remap in the shared model shaders, gated by the
// `viewmodel_depth_range` per-instance uniform (identity 1.0 default), applied by ViewModelRenderFx.
//
// The test assembly references only src/* (game/ is the Godot host assembly), so the shader sources are
// pinned by READING THE SOURCE FILES from the repo tree — located by walking up from the test bin dir, and
// self-skipping when the tree isn't found (mirrors the real-data classes' assets/data skip).

using System;
using System.IO;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the viewmodel short-depth-range parity mechanism so a shader/helper edit can't silently drop it:
/// (1) both model shaders carry the <c>viewmodel_depth_range</c> instance uniform + the <c>POSITION.z</c>
/// remap, (2) the applied fraction is DP's exact <c>0.0625</c>, (3) portal cameras exclude the viewmodel
/// render layer (DP renderimask), and (4) the reversed-Z remap math actually reproduces
/// <c>GL_DepthRange(0, 0.0625)</c> — identity at 1.0, the mirrored near-slice at 0.0625, order-preserving.
/// </summary>
public class ViewModelDepthHackTests
{
    /// <summary>DP's viewmodel depth-range max (gl_rmain.c:6214/8581). Must match ViewModelRenderFx.ShortDepthRange.</summary>
    private const float ShortDepthRange = 0.0625f;

    /// <summary>The clip-space remap both shaders apply: <c>POSITION.z = mix(z, w, 1 - frac)</c>.</summary>
    private const string RemapLine = "POSITION.z = mix(POSITION.z, POSITION.w, 1.0 - viewmodel_depth_range);";

    /// <summary>The gate uniform declaration (identity 1.0 default; the gun's material duplicates set 0.0625 —
    /// a per-MATERIAL uniform, deliberately NOT an instance uniform: per-instance delivery of this vertex-stage
    /// value proved unreliable, see ViewModelRenderFx).</summary>
    private const string UniformLine = "uniform float viewmodel_depth_range = 1.0;";

    // ---- repo-tree location (self-skip when absent) -------------------------------------------------

    private static string? RepoRoot()
    {
        // Walk up from the test bin directory to the tree that contains the game host sources. Works in the
        // main checkout, worktrees, and CI (game/ is part of the repo); returns null → tests self-skip.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "game", "loaders", "PlayerSkinShader.cs")))
                return dir.FullName;
        }
        return null;
    }

    private static string? SourceText(params string[] relative)
    {
        string? root = RepoRoot();
        if (root is null)
            return null;
        string path = Path.Combine(root, Path.Combine(relative));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ---- shader + helper pins ------------------------------------------------------------------------

    [Theory]
    [InlineData("PlayerSkinShader.cs")]
    [InlineData("Md3MorphShader.cs")]
    public void Model_Shaders_Carry_The_ShortDepthRange_Remap(string file)
    {
        string? src = SourceText("game", "loaders", file);
        if (src is null) return; // no repo tree next to the test bin — self-skip

        // The per-instance gate (identity default) and the clip-space remap must both be present — dropping
        // either silently reverts the gun to clipping into walls (the pre-parity behavior).
        Assert.Contains(UniformLine, src);
        Assert.Contains(RemapLine, src);
        Assert.Contains("POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);", src);
    }

    [Fact]
    public void RenderFx_Applies_DPs_Exact_Fraction_And_A_Dedicated_Layer()
    {
        string? src = SourceText("game", "client", "ViewModelRenderFx.cs");
        if (src is null) return;

        // GL_DepthRange(0, 0.0625) — the 1/16 near slice, verbatim from gl_rmain.c.
        Assert.Contains("ShortDepthRange = 0.0625f", src);
        // The dedicated render layer (19; the portal-surface bit is 1<<19 = layer 20) inside the default
        // camera cull mask, so the main view needs no setup while sub-views can exclude the gun.
        Assert.Contains("RenderLayerBit = 1u << 18", src);
    }

    [Fact]
    public void Portal_Cameras_Exclude_The_Viewmodel_Layer()
    {
        string? src = SourceText("game", "client", "PortalRenderer.cs");
        if (src is null) return;

        // DP hides RENDER_VIEWMODEL entities in reflection/refraction sub-views (R_View_UpdateEntityVisible
        // renderimask); the port's warpzone portal cameras must mask the gun's layer or the depth-compressed
        // model smears over the whole portal image.
        Assert.Contains("~(PortalSurfaceLayerBit | ViewModelRenderFx.RenderLayerBit)", src);
    }

    // ---- remap semantics (the math the shader line encodes) -------------------------------------------

    /// <summary>The shader remap, mirrored: clip z' for reversed-Z Godot (4.3+), returning NDC depth z'/w.</summary>
    private static float RemappedNdcDepth(float ndcDepth, float frac)
    {
        // POSITION.z = mix(z, w, 1 - frac) with z = ndcDepth * w; NDC depth is POSITION.z / POSITION.w.
        // (w cancels — use w = 1 without loss of generality, matching the shader for any perspective w > 0.)
        const float w = 1f;
        float z = ndcDepth * w;
        float mixed = z * frac + w * (1f - frac);
        return mixed / w;
    }

    [Fact]
    public void Remap_Is_Identity_At_Full_Range()
    {
        // Default 1.0 (every world/player instance): mix(z, w, 0) == z — world rendering is unchanged.
        foreach (float d in new[] { 0f, 0.25f, 0.5f, 0.9375f, 1f })
            Assert.Equal(d, RemappedNdcDepth(d, 1f), 6);
    }

    [Fact]
    public void Remap_Compresses_Into_The_Mirrored_Near_Slice()
    {
        // Godot 4.3+ is REVERSED-Z: NDC near = 1, far = 0. GL's glDepthRange(0, 0.0625) maps window depth
        // d → 0.0625·d (near stays nearest); with reversed depth r = 1 − d that is exactly
        // r' = 1 − 0.0625·(1 − r) = 0.9375 + 0.0625·r — the [0.9375, 1] slice the shader produces.
        Assert.Equal(0.9375f, RemappedNdcDepth(0f, ShortDepthRange), 6);   // far plane → slice floor
        Assert.Equal(1f, RemappedNdcDepth(1f, ShortDepthRange), 6);        // near plane → stays nearest
        Assert.Equal(1f - ShortDepthRange * (1f - 0.5f), RemappedNdcDepth(0.5f, ShortDepthRange), 6);

        // Everything world-rendered at full range below the slice floor loses the depth test against the gun:
        // with camera near = 1 (Quake units, NetGame) a wall must be within ~1.07u of the eye to reach
        // r > 0.9375 — inside the collision hull, unreachable. Same guarantee DP's 1/16 slice gives.
        Assert.True(RemappedNdcDepth(0f, ShortDepthRange) > 0.9f);
    }

    [Fact]
    public void Remap_Preserves_Order_Within_The_Gun()
    {
        // Depth testing stays ON (DP keeps it on for model surfaces — only sprites get true NODEPTHTEST,
        // r_sprites.c:407): the remap must be strictly increasing so the gun's own self-occlusion survives.
        float prev = -1f;
        for (float d = 0f; d <= 1.0001f; d += 0.125f)
        {
            float r = RemappedNdcDepth(d, ShortDepthRange);
            Assert.True(r > prev, $"remap must be strictly increasing (d={d})");
            prev = r;
        }
    }
}
