using XonoticGodot.Formats.Vfs;

namespace XonoticGodot.Formats.Materials;

/// <summary>
/// Which image a texture BROWSER should show for a material (backlog T6).
///
/// Separated from the loader that decodes it because this is the part with a decision in it, and the loader
/// lives under <c>game/</c> where nothing can be unit-tested. Getting the precedence wrong does not throw or
/// log — it fills the grid with question marks, or worse, with the right number of pictures of the wrong
/// thing — so it is the half that has to be pinned.
///
/// The precedence is Radiant's, and 1118 of the 1291 shaders in Xonotic's own <c>xonotic-maps.pk3dir</c>
/// declare <c>qer_editorimage</c>, which is the shader author's own answer to "what does this look like in a
/// list". For an animated or multi-stage shader it is the ONLY honest answer: the first render stage of a
/// forcefield is a scrolling mask, not a picture of a forcefield.
/// </summary>
public static class ShaderPreview
{
    /// <summary>The <c>qer_*</c> directive Radiant reads. Kept verbatim in <see cref="ShaderDef.Raw"/>.</summary>
    public const string EditorImageKey = "qer_editorimage";

    /// <summary>
    /// The extension-stripped image base name to show for <paramref name="materialName"/>, or null when the
    /// name is empty.
    /// </summary>
    /// <param name="materialName">The material as a face or the browser names it.</param>
    /// <param name="def">
    /// Its parsed shader, or null when there is none — then the name IS the image, which is the same rule
    /// <c>AssetSystem.ResolveLightmapDiffuse</c> applies to a plain world brush.
    /// </param>
    public static string? ImageName(string materialName, ShaderDef? def)
    {
        if (string.IsNullOrEmpty(materialName))
            return null;
        if (def is null)
            return AssetPaths.StripImageExtension(materialName);

        if (def.Raw.TryGetValue(EditorImageKey, out string? qer) && !string.IsNullOrWhiteSpace(qer))
            return AssetPaths.StripImageExtension(qer.Trim());

        // No editor image: fall back to the diffuse, chosen exactly as the world's own resolver chooses it
        // (AssetSystem.ResolveLightmapDiffuse). Any divergence here would mean the browser showed one picture
        // and the wall showed another, which is worse than showing nothing.
        foreach (ShaderStage stage in def.Stages)
        {
            if (stage.Detail || stage.IsLightmap || stage.IsWhiteImage)
                continue;
            string image = !string.IsNullOrEmpty(stage.MapTexture) ? stage.MapTexture
                : stage.AnimMap is { Frames.Length: > 0 } anim ? anim.Frames[0] : string.Empty;
            if (string.IsNullOrEmpty(image) || image == "-" || image.StartsWith('$'))
                continue;
            return AssetPaths.StripImageExtension(image);
        }

        // A sky has no diffuse at all. Its farbox front face is the nearest thing to a picture of it, and it
        // is what a mapper recognises the sky by.
        if (def.SkyParms?.FarBox is { Length: > 0 } box && box != "-")
            return AssetPaths.StripImageExtension(box) + "_ft";

        // Global-only shader (surfaceparms and nothing else): best-effort the name, same as the world path.
        return AssetPaths.StripImageExtension(materialName);
    }
}
