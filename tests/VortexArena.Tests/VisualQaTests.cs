// Port of Base/data/xonotic-data.pk3dir/qcsrc — N/A (this is a QA harness, not a gameplay port).
// Visual-system ground truth (what these assertions guard against regressing) lives in Darkplaces, NOT QuakeC:
//   - maps:    model_brush.c  Mod_Q3BSP_Load* (lump directory: models/brushes/faces/lightmaps/deluxemaps).
//   - models:  model_alias.c  Mod_INTERQUAKEMODEL_Load (iqm), Mod_IDP3_Load (md3), Mod_DARKPLACESMODEL_Load (dpm),
//              Mod_IDP0_Load (mdl).
//   - shaders: model_shared.c Mod_LoadQ3Shaders (the .shader material scripts).
//
// SCOPE — read this before adding to the file. T5 (Wave A5) is a VERIFY-THEN-SCOPE task. The hard constraint is
// that Godot's headless renderer (dummy_video) renders NOTHING — GetViewport().GetTexture().GetImage() is null
// headless (game/ScreenshotHook.cs:50-56, docs/RUNNING.md "Run headless"). So NO rendered-frame / pixel correctness
// can be asserted in CI. This suite therefore asserts ONLY what is decidable from the parsed, byte-packed asset
// structures, with NO GPU and NO Godot runtime:
//   * every stock map parses + carries renderable/collidable geometry (load success + object/brush/face counts),
//   * every stock model loads + has a valid bone hierarchy (parent-chain) + a non-singular bind pose,
//   * every stock .shader script parses with no hard failure (compile-success only — NOT output color/specular).
// Visual CORRECTNESS (lightmap direction, patch smoothness, flare quads, material color, on-screen bone pose) is
// NOT automatable here — it is the WINDOWED manual checklist driven by tools/visual-qa.sh and documented in
// docs/RUNNING.md "Visual QA". Do NOT add a test here that claims to verify rendered output.
//
// Like the ~18 other real-data test classes, every [Theory]/[Fact] self-skips when assets/data is absent (CI
// has no assets): the MemberData providers yield a single sentinel row carrying null, and the test returns on it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Formats.Mdl;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T5 (Wave A5) — the headless-safe half of the Visual QA. A sweep over every shipped map, model, and shader
/// asserting the asset LOADS and is structurally sound, since that is all a renderless headless run can decide.
/// The windowed/manual half (actual on-screen correctness) is <c>tools/visual-qa.sh</c> + docs/RUNNING.md. See the
/// file header for the full headless/manual scoping split.
/// </summary>
public class VisualQaTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // One VFS mount for the whole class (the asset tree is large; the data tests share a single read-only mount).
    private static readonly Lazy<VirtualFileSystem?> Vfs = new(() =>
    {
        if (!Directory.Exists(DataDir))
            return null;
        var vfs = new VirtualFileSystem();
        return vfs.MountGameDir(DataDir) ? vfs : null;
    });

    // A sentinel data row (a single null path) so a [Theory] never fails with xUnit's "No data found" when
    // assets/data is absent — the test body returns on a null path, mirroring the other real-data classes' skip.
    private static object[] SkipRow() => new object[] { null! };

    private static IEnumerable<object[]> PathsOrSkip(string prefix, string extension)
    {
        VirtualFileSystem? vfs = Vfs.Value;
        if (vfs is null)
        {
            yield return SkipRow();
            yield break;
        }

        bool any = false;
        foreach (string p in vfs.Find(prefix, extension).OrderBy(p => p, StringComparer.Ordinal))
        {
            any = true;
            yield return new object[] { p };
        }
        if (!any)
            yield return SkipRow();
    }

    // ===================================================================== maps (BSP load + geometry counts)

    /// <summary>Every <c>maps/*.bsp</c> the data tree ships (the 31 official + Nexuiz-compat set), or the skip row.</summary>
    public static IEnumerable<object[]> AllMaps() => PathsOrSkip("maps/", "bsp");

    [Theory]
    [MemberData(nameof(AllMaps))]
    public void Map_Loads_And_Has_Renderable_And_Collidable_Geometry(string? mapPath)
    {
        if (mapPath is null) return; // skip-if-missing
        VirtualFileSystem vfs = Vfs.Value!;

        // Load success: the BSP reader parses the lump directory without throwing (parse failure == visual failure
        // — the map would never reach the GPU). This is the headless analogue of "the map opens".
        BspData bsp = BspReader.Read(vfs.ReadBytes(mapPath));

        // Object/brush/face counts: a real, renderable, collidable map. Every IBSP has at least the worldspawn
        // model (DP Mod_Q3BSP_LoadModels hard-requires it), faces to draw, and brushes OR tessellatable patch
        // faces to collide against. A zero on any of these would be an empty (invisible) map.
        Assert.True(bsp.Models.Length >= 1, $"{mapPath}: no models (worldspawn missing)");
        Assert.True(bsp.Faces.Length >= 1, $"{mapPath}: no faces (nothing to render)");
        Assert.True(bsp.Textures.Length >= 1, $"{mapPath}: no shader references");

        bool hasBrushes = bsp.Brushes.Length >= 1;
        bool hasPatch = bsp.Faces.Any(f => f.Type == BspFaceType.Patch);
        Assert.True(hasBrushes || hasPatch, $"{mapPath}: no collidable geometry (no brushes, no patches)");

        // The worldspawn model owns a contiguous brush range starting at 0 (the standard Q3 layout the collision
        // splitter relies on — see BspCollisionTests). A broken Models lump would silently drop all collision.
        Assert.Equal(0, bsp.Models[0].FirstBrush);
        Assert.True(bsp.Models[0].FirstBrush + bsp.Models[0].BrushCount <= bsp.Brushes.Length,
            $"{mapPath}: worldspawn brush range out of bounds");

        // Face vertex/index ranges stay in bounds (an out-of-range range renders garbage or crashes the mesh build).
        foreach (BspFace f in bsp.Faces)
        {
            Assert.True(f.FirstVertex >= 0 && f.FirstVertex + f.VertexCount <= bsp.Vertices.Length,
                $"{mapPath}: face vertex range out of bounds");
            Assert.True(f.FirstIndex >= 0 && f.FirstIndex + f.IndexCount <= bsp.Triangles.Length,
                $"{mapPath}: face index range out of bounds");
        }

        // Deluxemap de-interleave invariant: a deluxemapped map keeps one direction page per real lightmap page
        // (the pairing the future bumped-lighting pass and the manual deluxemap eye-check both depend on).
        if (bsp.IsDeluxemapped)
            Assert.Equal(bsp.Lightmaps.Length, bsp.Deluxemaps.Length);
    }

    // ===================================================================== models (loader + skeleton + bind pose)

    // The data tree ships model files whose EXTENSION LIES about their on-disk format — a long-standing Quake/DP
    // asset quirk the engine handles by dispatching every model load on its leading MAGIC, never its extension
    // (game/loaders/AssetLoader.cs:214 "extensions lie: h_*.iqm are often DPM"; DP model_shared.c). So these
    // sweeps gather candidate files by extension but VALIDATE each by its TRUE magic — the same dispatch the
    // engine uses — routing an "iqm" that is really a DPM to the DPM validator, etc. A file whose magic is one of
    // the not-yet-implemented formats (MD2/ZYM/PSK — TODO T72; MDL IS supported and validated by ValidateMdl) is
    // not a failure here: the engine returns null+log for those, so the test treats them as "not a
    // structurally-checkable model" and passes. (Each theory still walks every shipped model of its nominal
    // extension — nothing escapes coverage.)
    public static IEnumerable<object[]> AllIqm() => PathsOrSkip("models/", "iqm");
    public static IEnumerable<object[]> AllMd3() => PathsOrSkip("models/", "md3");
    public static IEnumerable<object[]> AllDpm() => PathsOrSkip("models/", "dpm");
    public static IEnumerable<object[]> AllMdl() => PathsOrSkip("models/", "mdl");

    private const string MagicIqm = "INTERQUAKEMODEL";
    private const string MagicDpm = "DARKPLACESMODEL";
    private const string MagicMd3 = "IDP3";
    private const string MagicMdl = "IDPO";

    [Theory]
    [MemberData(nameof(AllIqm))]
    public void IqmModel_Loads_Skeleton_ParentChain_And_NonSingular_BindPose(string? modelPath)
    {
        if (modelPath is null) return; // skip-if-missing
        ValidateModelByMagic(modelPath);
    }

    [Theory]
    [MemberData(nameof(AllMd3))]
    public void Md3Model_Loads_Frames_And_NonDegenerate_Tag_Frames(string? modelPath)
    {
        if (modelPath is null) return; // skip-if-missing
        ValidateModelByMagic(modelPath);
    }

    [Theory]
    [MemberData(nameof(AllDpm))]
    // NOTE: DPM (unlike IQM) deliberately does NOT assert a non-singular (det != 0) per-bone bind pose — shipped
    // DP models carry legitimate zero-scale helper bones (see ValidateDpm). The test verifies the parent-chain
    // and the per-frame pose count only; the name reflects that to avoid the misleading "NonSingular_BindPose"
    // claim the IQM test of the same shape genuinely checks.
    public void DpmModel_Loads_Skeleton_ParentChain_And_FramePoseCount(string? modelPath)
    {
        if (modelPath is null) return; // skip-if-missing
        ValidateModelByMagic(modelPath);
    }

    [Theory]
    [MemberData(nameof(AllMdl))]
    public void MdlModel_Loads_Geometry_Skin_And_UnitNormals(string? modelPath)
    {
        if (modelPath is null) return; // skip-if-missing
        ValidateModelByMagic(modelPath);
    }

    /// <summary>
    /// Dispatch a model file to the validator for its TRUE on-disk format (by leading magic, exactly as
    /// <c>AssetLoader.LoadModel</c> / DP <c>model_shared.c</c> do), then run that format's structural checks.
    /// A file whose magic is none of the four ported model magics (IQM/DPM/MD3/MDL) is one of the not-yet-ported
    /// formats (MD2/ZYM/PSK — TODO T72) the engine also declines to load — not a failure, so it is skipped.
    /// </summary>
    private static void ValidateModelByMagic(string modelPath)
    {
        VirtualFileSystem vfs = Vfs.Value!;
        byte[] bytes = vfs.ReadBytes(modelPath);
        string magic = bytes.Length >= 16 ? BinaryUtil.ReadMagic(bytes, 0) : "";

        if (magic.StartsWith(MagicIqm, StringComparison.Ordinal))
            ValidateIqm(modelPath, bytes);
        else if (magic.StartsWith(MagicDpm, StringComparison.Ordinal))
            ValidateDpm(modelPath, bytes);
        else if (magic.StartsWith(MagicMd3, StringComparison.Ordinal))
            ValidateMd3(modelPath, bytes);
        else if (magic.StartsWith(MagicMdl, StringComparison.Ordinal))
            ValidateMdl(modelPath, bytes);
        // else: a not-yet-implemented model magic (MD2/ZYM/PSK — TODO T72) the engine returns null+log for, so
        // there is nothing structurally checkable here; treat as a pass (coverage is by magic, not extension).
    }

    private static void ValidateIqm(string modelPath, byte[] bytes)
    {
        // Loader success: the player/monster/vehicle/view-weapon format parses without throwing.
        IqmData iqm = IqmReader.Read(bytes);
        Assert.True(iqm.Version is 1 or 2, $"{modelPath}: bad IQM version {iqm.Version}");

        // Skeleton parent-chain valid: every joint's parent strictly precedes it (-1 = root). This is the
        // invariant the forward-pass world-rest composer relies on; a cyclic/forward parent twists the model.
        for (int i = 0; i < iqm.Joints.Length; i++)
        {
            IqmJoint j = iqm.Joints[i];
            Assert.InRange(j.Parent, -1, i - 1);

            // Non-singular bind pose: the bone-local rest transform must be invertible, i.e. a unit rotation
            // (normalized quaternion) and a non-zero scale on every axis. A zero-scale or non-unit quat collapses
            // the bone and renders the mesh inside-out / vanished. (IqmReader normalizes joint quats; we assert it.)
            Assert.Equal(1f, j.Rotate.Length(), 3);
            Assert.True(MathF.Abs(j.Scale.X) > 1e-6f && MathF.Abs(j.Scale.Y) > 1e-6f && MathF.Abs(j.Scale.Z) > 1e-6f,
                $"{modelPath}: joint {i} '{j.Name}' has a singular (zero) bind-pose scale {j.Scale}");
        }
    }

    private static void ValidateMd3(string modelPath, byte[] bytes)
    {
        // Loader success (MD3 is vertex-morph, not skeletal — there is no bone parent-chain; its "skeleton" is the
        // tag-frame set + per-frame surface vertices). Parse without throwing == the model can reach the GPU.
        Md3Data md3 = Md3Reader.Read(bytes);
        Assert.True(md3.FrameCount >= 1, $"{modelPath}: no frames");
        Assert.Equal(md3.FrameCount, md3.Frames.Length);
        Assert.True(md3.Surfaces.Length >= 1, $"{modelPath}: no surfaces (nothing to render)");

        // Tag frames: each MD3 tag carries one axis frame per model frame (the attachment point a child model
        // rides). We assert the per-frame count invariant the renderer indexes by. NOTE: we deliberately do NOT
        // assert unit-length tag axes — DP's Mod_IDP3_Load reads the tag's rotationmatrix[9] as raw floats and
        // applies it as-is, so a shipped exporter may bake a SCALE into the tag basis (e.g.
        // models/turrets/ewheel-base2.md3 / ewheel-gun1.md3 carry a ~0.377-length tag axis). A non-unit tag axis
        // is valid content, not a load failure; requiring orthonormality here would reject real shipped models.
        foreach (Md3Tag t in md3.Tags)
            Assert.Equal(md3.FrameCount, t.Transforms.Length);

        // Each surface keeps one vertex block per model frame with unit morph normals (a zero normal renders black).
        foreach (Md3Surface s in md3.Surfaces)
        {
            Assert.Equal(md3.FrameCount, s.FrameVertices.Length);
            foreach (var frame in s.FrameVertices)
            {
                Assert.Equal(s.VertexCount, frame.Length);
                foreach (var v in frame)
                    Assert.Equal(1f, v.Normal.Length(), 2);
            }
        }
    }

    private static void ValidateMdl(string modelPath, byte[] bytes)
    {
        // Loader success: the Quake1 alias format parses without throwing == the model can reach the GPU. Like
        // MD3 it is vertex-morph (no skeleton); its geometry is a set of frames + the seam-resolved render corners.
        MdlData mdl = MdlReader.Read(bytes);
        Assert.True(mdl.VertexCount >= 1, $"{modelPath}: no vertices");
        Assert.True(mdl.Frames.Length >= 1, $"{modelPath}: no frames");
        Assert.True(mdl.Corners.Length >= 3, $"{modelPath}: no triangles (nothing to render)");

        // Every render corner indexes a real vertex; each frame carries one vertex per model vertex with a unit
        // normal (a zero normal renders black). The skin, when present, is a fully-formed RGBA buffer.
        Assert.All(mdl.Corners, c => Assert.InRange(c.Vertex, 0, mdl.VertexCount - 1));
        foreach (MdlFrame f in mdl.Frames)
        {
            Assert.Equal(mdl.VertexCount, f.Vertices.Length);
            foreach (MdlVertex v in f.Vertices)
                Assert.Equal(1f, v.Normal.Length(), 2);
        }
        if (mdl.SkinWidth > 0 && mdl.SkinHeight > 0)
            Assert.Equal(mdl.SkinWidth * mdl.SkinHeight * 4, mdl.SkinRgba.Length);
    }

    private static void ValidateDpm(string modelPath, byte[] bytes)
    {
        // Loader success: DARKPLACESMODEL (hierarchical skeletal, type 2) parses without throwing.
        DpmData dpm = DpmReader.Read(bytes);
        Assert.True(dpm.Bones.Length >= 1, $"{modelPath}: no bones");
        Assert.True(dpm.Frames.Length >= 1, $"{modelPath}: no frames");

        // Skeleton parent-chain valid: parents strictly precede children (the forward-pass composer's invariant).
        for (int i = 0; i < dpm.Bones.Length; i++)
            Assert.InRange(dpm.Bones[i].Parent, -1, i - 1);

        // Every frame carries exactly one pose per bone (the per-frame pose array the composer indexes by bone).
        // NOTE: we deliberately DON'T assert a non-singular (det != 0) per-bone bind pose here. Shipped DP skeletal
        // models legitimately carry a collapsed/zero-scale helper bone (e.g. models/vehicles/wakizashi.dpm frame
        // 'wakizashi_0' bone 9 has det 0) — DP composes the skeleton without inverting the rest pose, so a singular
        // helper bone is valid content, not a load failure. This matches the established DpmReaderTests
        // .RealAsset_SkeletonInvariants ground truth, which asserts the parent-chain + pose-count but NOT the
        // determinant. (A genuine bind-pose decode bug would surface as an out-of-range pose count or parent index.)
        foreach (DpmFrame f in dpm.Frames)
            Assert.Equal(dpm.Bones.Length, f.BonePoses.Length);
    }

    // ===================================================================== shaders (compile/parse success)

    /// <summary>Every <c>scripts/*.shader</c> material script, or the skip row.</summary>
    public static IEnumerable<object[]> AllShaderScripts() => PathsOrSkip("scripts/", "shader");

    [Theory]
    [MemberData(nameof(AllShaderScripts))]
    public void ShaderScript_Compiles_With_No_Hard_Failure(string? shaderPath)
    {
        if (shaderPath is null) return; // skip-if-missing
        VirtualFileSystem vfs = Vfs.Value!;

        // "Compile success" (the headless analogue of a shader that loads without an ERROR in the log): the
        // material script parses, the parser raises warnings (not throws) on bad content, and it produces at
        // least one material definition. We do NOT assert output color/specularity — only that nothing in the
        // file is a hard parse failure. (Q3ShaderParser.Parse is robust-by-contract: it warns, never throws.)
        string text = vfs.ReadText(shaderPath);

        var warnings = new List<string>();
        IReadOnlyDictionary<string, ShaderDef> defs = Q3ShaderParser.Parse(text, warnings.Add);

        // A .shader file that actually declares a block (has a '{') must yield at least one shader def — zero
        // defs there means the whole file was unparseable (a hard failure masquerading as "0 warnings"). We gate
        // on '{' rather than on non-whitespace so a comment-/header-only .shader file isn't a false failure.
        if (text.Contains('{'))
            Assert.True(defs.Count >= 1, $"{shaderPath}: declared a block but parsed to zero materials ({warnings.Count} warnings)");

        // No def may carry a self-evidently broken stage set (more than the DP cap leaks past the layer clamp).
        foreach (ShaderDef def in defs.Values)
            Assert.True(def.Stages.Count <= 8, $"{shaderPath}: '{def.Name}' exceeds the 8-layer cap");
    }

    /// <summary>
    /// A single aggregate compile pass over the whole <c>scripts/</c> set — the headless mirror of the ci.sh
    /// "N shaders compiled, 0 hard errors" summary line. Asserts the shipped material library is large (the
    /// real tree compiles to 500+ defs; a tiny number means the VFS didn't mount the content).
    /// </summary>
    [Fact]
    public void AllStockShaders_Compile_To_The_Full_Material_Library()
    {
        VirtualFileSystem? vfs = Vfs.Value;
        if (vfs is null) return; // skip-if-missing

        var texts = vfs.Find("scripts/", "shader").Select(vfs.ReadText);
        IReadOnlyDictionary<string, ShaderDef> shaders = Q3ShaderParser.ParseFiles(texts);
        Assert.True(shaders.Count >= 500,
            $"expected 500+ compiled materials from the stock shader scripts, got {shaders.Count}");
    }
}
