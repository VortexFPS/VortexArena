using System.Collections.Generic;
using Godot;
using XonoticGodot.Game.Loaders;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The render-state side of Darkplaces' <c>RENDER_VIEWMODEL</c> for the first-person weapon — everything the
/// engine does to a viewmodel entity beyond placing it at the view origin:
///
/// <list type="bullet">
///   <item><b>Short depth range</b> (<c>MATERIALFLAG_SHORTDEPTHRANGE</c> = <c>GL_DepthRange(0, 0.0625)</c>,
///   gl_rmain.c:6214/8581): the gun's depth is compressed into the nearest 1/16 of the depth buffer with depth
///   testing still ON — self-occlusion inside the gun is preserved, but it always beats world geometry, so the
///   barrel never clips into a wall you are hugging. CSQC <c>RF_VIEWMODEL</c> maps here too (csprogs.c:398 sets
///   RENDER_VIEWMODEL|RENDER_NODEPTHTEST and gl_rmain.c:6768 turns BOTH into SHORTDEPTHRANGE — DP never
///   actually disables the depth test for model surfaces). Implemented as the <c>viewmodel_depth_range</c>
///   shader uniform (identity 1.0 default) in <see cref="PlayerSkinShader"/> / <see cref="Md3MorphShader"/>:
///   the gun renders through its OWN duplicates of the shared skin materials with 0.0625 set (per-material —
///   a per-INSTANCE uniform proved unreliable for this vertex-stage value), and the duplicates share the same
///   <see cref="Shader"/> objects, so no new pipeline variants are introduced. A plain
///   <see cref="StandardMaterial3D"/> cannot remap clip-space depth at all, so opaque textured gun surfaces
///   are converted to an equivalent skin-shader material (<see cref="ConvertToSkinMaterial"/>); surfaces that
///   cannot convert keep the legacy <c>NoDepthTest</c> approximation.</item>
///   <item><b>No visibility culling</b> (gl_rmain.c:4016-4023 <c>R_View_UpdateEntityVisible</c>: RENDER_VIEWMODEL
///   entities are marked visible with no leaf/trace test): the gun must draw even when its bounds sit inside
///   world geometry, so occlusion culling is bypassed per mesh (<c>IgnoreOcclusionCulling</c> — the software
///   occluder would cull the gun the moment the barrel pokes into a wall, exactly the scenario this fixes).</item>
///   <item><b>Main-view only</b> (gl_rmain.c:3999-4006: reflection / refraction / envmap scenes add
///   RENDER_VIEWMODEL to <c>renderimask</c>): the gun renders on its own <see cref="RenderLayerBit"/>, which
///   the warpzone portal cameras exclude — otherwise the depth-compressed gun would smear over every portal
///   view of your own position.</item>
///   <item><b>No shadow casting</b>: stock DP casts no model shadows for the viewmodel, and the depth remap
///   runs in every pass the vertex shader runs in — a shadow pass would bake the compressed depth into the
///   light's map — so viewmodel meshes must not cast.</item>
/// </list>
/// </summary>
internal static class ViewModelRenderFx
{
    /// <summary>
    /// The render layer the first-person weapon lives on (layer 19; <c>PortalRenderer</c>'s portal-surface
    /// bit is 1&lt;&lt;19 = layer 20). Inside the default <see cref="Camera3D.CullMask"/> (layers 1-20), so the
    /// main camera sees the gun with no setup; portal cameras mask it out — Base's renderimask exclusion of
    /// RENDER_VIEWMODEL entities from reflection/refraction scenes.
    /// </summary>
    public const uint RenderLayerBit = 1u << 18;

    /// <summary>
    /// DP's viewmodel depth-range max: <c>GL_DepthRange(0, 0.0625)</c> (gl_rmain.c:6214/8581) — the nearest
    /// 1/16 of the depth buffer. With the port's reversed-Z (Godot 4.3+) the slice mirrors to [0.9375, 1].
    /// </summary>
    public const float ShortDepthRange = 0.0625f;

    /// <summary>Meta key marking a material this class produced (a gun-owned duplicate/conversion), so the
    /// change-gated re-walks recognize an already-correct override without re-building it.</summary>
    private static readonly StringName DepthMaterialMeta = "vm_depth_material";

    // Gun-owned material caches keyed by the SOURCE material's instance id (never reused within a process):
    // weapon surface materials come from AssetSystem's shared cache, so every equip of e.g. the shotgun reuses
    // the SAME duplicate/conversion. The produced materials share the source's Shader object (duplicates) or
    // PlayerSkinShader (conversions), so no new pipelines are introduced; per-entity tint still rides the
    // UNCHANGED instance uniforms (colormod/glowmod/shirt/pants/grid_*). Bounded by the weapon-material count.
    private static readonly Dictionary<ulong, ShaderMaterial> DepthDuplicates = new();
    private static readonly Dictionary<ulong, ShaderMaterial> ConvertedSkin = new();

    /// <summary>
    /// Apply the viewmodel render state to every <see cref="MeshInstance3D"/> at or under <paramref name="root"/>:
    /// the dedicated render layer, shadow casting off, the DP no-visibility-culling bypass, and the
    /// short-depth-range material state on every surface (<see cref="EnsureOpaqueSurfaceFx"/>). Call after
    /// every model attach (equip, placeholder, muzzle-flash spawn); ViewModel's change-gated material walk
    /// re-asserts the surface state afterwards (idempotent via <see cref="DepthMaterialMeta"/>).
    /// </summary>
    public static void Apply(Node root)
    {
        if (root is MeshInstance3D mi)
        {
            mi.Layers = RenderLayerBit;
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            // DP bypasses ALL visibility culling for RENDER_VIEWMODEL entities (gl_rmain.c:4016-4023) — a
            // depth-compressed gun must draw even when its bounds sit inside world geometry. Frustum culling
            // still applies in Godot, which is fine (the gun hugs the camera).
            mi.IgnoreOcclusionCulling = true;

            // A MaterialOverride (the placeholder-bar case) bypasses the surface materials entirely, so the
            // surface pass never sees it. It cannot ride the depth uniform (BaseMaterial3D, untextured) —
            // give a per-instance duplicate the legacy NoDepthTest approximation.
            if (mi.MaterialOverride is BaseMaterial3D bo && !bo.NoDepthTest)
            {
                var dup = (BaseMaterial3D)bo.Duplicate();
                dup.NoDepthTest = true;
                mi.MaterialOverride = dup;
            }

            Mesh? mesh = mi.Mesh;
            int surfaces = mesh?.GetSurfaceCount() ?? 0;
            for (int i = 0; i < surfaces; i++)
                EnsureOpaqueSurfaceFx(mi, i);
        }
        foreach (Node child in root.GetChildren())
            Apply(child);
    }

    /// <summary>
    /// Ensure surface <paramref name="surface"/> of <paramref name="mi"/> renders with the faithful
    /// short-depth-range state for the OPAQUE (alpha = 1, the default) viewmodel:
    /// <list type="bullet">
    ///   <item>a shared skin/anim <see cref="ShaderMaterial"/> surface → a cached gun-owned DUPLICATE with
    ///   <c>viewmodel_depth_range = 0.0625</c> installed as the surface override (same Shader object — same
    ///   pipelines; foreign shaders without the uniform simply ignore the parameter);</item>
    ///   <item>a per-animator <see cref="Md3MorphShader"/> material (muzzle-flash / legacy morph weapons) →
    ///   the uniform is set DIRECTLY (the <c>ModelAnimator</c> keeps writing <c>morph_amount</c> to the same
    ///   material object, so it must not be swapped for a duplicate);</item>
    ///   <item>an opaque textured <see cref="BaseMaterial3D"/> → converted to the equivalent skin-shader
    ///   material (<see cref="ConvertToSkinMaterial"/>), which also puts it on the grid-light/instance-tint
    ///   path.</item>
    /// </list>
    /// Returns false when the caller must keep the legacy <c>NoDepthTest</c> BaseMaterial3D fallback
    /// (untextured / transparent / tinted source — including the additive muzzle-flash surfaces).
    /// </summary>
    public static bool EnsureOpaqueSurfaceFx(MeshInstance3D mi, int surface)
    {
        Mesh? mesh = mi.Mesh;
        if (mesh is null)
            return false;

        Material? current = mi.GetSurfaceOverrideMaterial(surface);
        if (current is not null && current.HasMeta(DepthMaterialMeta))
            return true;    // already carrying a gun-owned depth material from an earlier walk

        Material? source = mesh.SurfaceGetMaterial(surface);
        if (current is BaseMaterial3D && source is ShaderMaterial)
        {
            // A stale translucent dup (from a cl_viewmodel_alpha < 1 frame) sits over a shader-material
            // source — drop it so the pristine source takes the shader-material handling below.
            mi.SetSurfaceOverrideMaterial(surface, null);
            current = null;
        }
        Material? mat = current ?? source;
        switch (mat)
        {
            case ShaderMaterial sm when sm.Shader == Md3MorphShader.Shader:
                // Morph materials are built per ModelAnimator (BuildMorphMaterial) and the animator keeps
                // writing morph_amount to this exact object — mutate in place instead of duplicating.
                sm.SetShaderParameter(PlayerSkinShader.ViewmodelDepthRangeUniform, ShortDepthRange);
                sm.SetMeta(DepthMaterialMeta, true);
                return true;

            case ShaderMaterial sm:
            {
                // Shared cached material (PlayerSkinShader skin / a compiled shader stage): install a
                // gun-owned duplicate with the depth uniform. Same Shader object — no pipeline change; a
                // shader without the uniform ignores the parameter (that surface keeps normal depth, the
                // best available for foreign stages).
                ulong key = sm.GetInstanceId();
                if (!DepthDuplicates.TryGetValue(key, out ShaderMaterial? dup))
                {
                    dup = (ShaderMaterial)sm.Duplicate();
                    dup.ResourceName = sm.ResourceName + "/vm-depth";
                    dup.SetShaderParameter(PlayerSkinShader.ViewmodelDepthRangeUniform, ShortDepthRange);
                    dup.SetMeta(DepthMaterialMeta, true);
                    DepthDuplicates[key] = dup;
                }
                mi.SetSurfaceOverrideMaterial(surface, dup);
                return true;
            }

            case BaseMaterial3D bm:
            {
                // A previous alpha (<1) frame may have left a translucent BaseMaterial3D dup as `current`;
                // convert from the pristine MESH source so its alpha/tint mutations don't leak.
                ShaderMaterial? converted = ConvertToSkinMaterial((source as BaseMaterial3D) ?? bm);
                if (converted is null)
                    return false;
                mi.SetSurfaceOverrideMaterial(surface, converted);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Build (once, cached) the <see cref="PlayerSkinShader"/> equivalent of a plain opaque weapon
    /// <see cref="BaseMaterial3D"/>, mapping the WireCompanions texture wiring across — albedo,
    /// <c>_norm</c> (NormalTexture), <c>_gloss</c> (fed as RoughnessTexture, sampled .g and inverted — the
    /// same convention both shaders share), <c>_glow</c> (EmissionTexture) and <c>_reflect</c>
    /// (MetallicTexture → reflect mask) — plus the short-depth-range uniform. Mirrors
    /// <c>ModelAnimator.BuildMorphMaterial</c>'s "wrap the StandardMaterial3D look in the shader that can
    /// express what it cannot" pattern. Returns null for a material the shader can NOT reproduce (no albedo
    /// texture, non-white albedo tint, any transparency — e.g. the additive muzzle-flash surfaces) — the
    /// caller keeps the BaseMaterial3D + legacy NoDepthTest for those.
    /// </summary>
    internal static ShaderMaterial? ConvertToSkinMaterial(BaseMaterial3D source)
    {
        if (source.AlbedoTexture is null)
            return null;    // fallback/placeholder materials carry their look in AlbedoColor
        if (source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
            return null;    // muzzle-flash additive / alpha'd materials keep their BaseMaterial3D pipeline
        if (source.AlbedoColor != Colors.White)
            return null;    // the skin shader has no albedo-color slot (colormod is per-instance, tint-owned)

        ulong key = source.GetInstanceId();
        if (ConvertedSkin.TryGetValue(key, out ShaderMaterial? cached))
            return cached;

        var mat = new ShaderMaterial { Shader = PlayerSkinShader.Shader, ResourceName = source.ResourceName + "/vm-skin" };
        mat.SetShaderParameter(PlayerSkinShader.AlbedoUniform, source.AlbedoTexture);
        mat.SetShaderParameter(PlayerSkinShader.ViewmodelDepthRangeUniform, ShortDepthRange);
        if (source.NormalEnabled && source.NormalTexture is not null)
        {
            mat.SetShaderParameter("normal_tex", source.NormalTexture);
            mat.SetShaderParameter("has_normal", true);
        }
        if (source.RoughnessTexture is not null)
        {
            // WireCompanions feeds the _gloss companion through RoughnessTexture (grayscale, scalar 1.0);
            // the skin shader samples .g and inverts — the same gloss-is-inverse-roughness convention.
            mat.SetShaderParameter("gloss_tex", source.RoughnessTexture);
            mat.SetShaderParameter("has_gloss", true);
        }
        if (source.EmissionEnabled && source.EmissionTexture is not null)
        {
            mat.SetShaderParameter(PlayerSkinShader.GlowUniform, source.EmissionTexture);
            mat.SetShaderParameter("has_glow", true);
        }
        if (source.MetallicTexture is not null)
        {
            // WireCompanions' _reflect → metallic-channel approximation becomes the shader's real reflect
            // mask (the restrained no-cubemap sheen — see TryBuildSkinMaterial's dpreflectcube note).
            mat.SetShaderParameter(PlayerSkinShader.ReflectMaskUniform, source.MetallicTexture);
            mat.SetShaderParameter("has_reflect", true);
            mat.SetShaderParameter(PlayerSkinShader.ReflectStrengthUniform, 1.0f);
        }

        mat.SetMeta(DepthMaterialMeta, true);
        ConvertedSkin[key] = mat;
        return mat;
    }
}
