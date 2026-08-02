using System;
using System.Collections.Generic;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;

namespace VortexArena.Formats.Materials;

/// <summary>
/// Which of a map's textures will not render — the analysis half of the <c>r_missingtextures</c> console
/// command.
///
/// <para><b>Why this exists.</b> DarkPlaces has no command that answers "what is missing in this map". It
/// prints <c>could not load texture "…"</c> at load time (<c>Mod_LoadTextureFromQ3Shader</c>, warnmissing=true
/// from <c>Mod_Q3BSP_LoadTextures</c>) and you are expected to scroll the console back, which loses the count,
/// the face weighting, and everything already scrolled away. Worse, that message only fires for a texture with
/// <b>no shader at all</b>: when a <c>.shader</c> exists and one of its stages points at an image that isn't
/// there, DP loads the notexture placeholder silently (<c>R_SkinFrame_LoadExternal(…, complain: false,
/// fallbacknotexture: true)</c>) — the port's <see cref="ShaderCompiler"/> path is silent for the same reason.
/// That second class is the one mappers actually hit when a pk3 ships without its texture folder, and it is
/// why this audit walks stages rather than trusting a load-time log.</para>
///
/// <para><b>Static, not instrumented.</b> The scan resolves names the way the material build will, instead of
/// recording misses as they happen. That makes the answer complete the moment the BSP is parsed — a texture on
/// a surface nobody has looked at yet counts the same as one in the room you're standing in — and it lets the
/// command audit a map that is not loaded at all.</para>
///
/// <para>Godot-free by construction (delegates supply shader lookup and image resolution) so the precedence
/// can be pinned by tests; <c>game/</c> cannot be unit-tested. The Godot-side wiring lives in
/// <c>Game.Loaders.MissingTextureCommand</c>.</para>
/// </summary>
public static class MapTextureAudit
{
    /// <summary>What the audit concluded about one BSP texture entry.</summary>
    public enum Status
    {
        /// <summary>Every image the material build needs resolves.</summary>
        Ok,

        /// <summary>Never rendered (nodraw/sky/compiler-only), so a missing image is not a defect.</summary>
        NotDrawn,

        /// <summary>Nothing resolves: the surface will draw as the magenta checkerboard.</summary>
        Missing,

        /// <summary>The shader loads and some stages resolve, but at least one stage image does not.</summary>
        Partial,
    }

    /// <summary>One audited BSP texture entry.</summary>
    /// <param name="Name">The shader/texture name exactly as the BSP's texture lump stores it.</param>
    /// <param name="Status">The verdict.</param>
    /// <param name="HasShader">True when a <c>.shader</c> of this name was parsed (so the name is not the image).</param>
    /// <param name="FaceCount">Renderable faces (flat/mesh/patch) referencing this entry — the blast radius.</param>
    /// <param name="MissingImages">The image base names that did not resolve; empty unless Missing/Partial.</param>
    public readonly record struct Entry(
        string Name,
        Status Status,
        bool HasShader,
        int FaceCount,
        IReadOnlyList<string> MissingImages);

    /// <summary>
    /// The map's skybox. Separate from <see cref="Entry"/> because a skybox is not a surface texture: the sky
    /// SURFACES draw nothing (the box is drawn around the view instead), so a map whose every wall is present
    /// can still render a blank void overhead, and the entry list would have called it clean.
    /// </summary>
    /// <param name="Name">The resolved base name, or empty when the map declares no skybox.</param>
    /// <param name="Resolved">True when some suffix convention has all six faces (what the loader requires).</param>
    /// <param name="Convention">
    /// The suffix set the report is about: the one that got furthest. DP takes the first COMPLETE convention,
    /// so when none is complete the closest one is the author's intent and its gaps are the real defect —
    /// naming faces from a convention the map never used would send someone hunting for the wrong files.
    /// </param>
    /// <param name="MissingFaces">
    /// The <see cref="Convention"/> suffixes with no image, e.g. <c>["up", "dn"]</c>. Empty when resolved.
    /// </param>
    public readonly record struct SkyReport(
        string Name,
        bool Resolved,
        IReadOnlyList<string> Convention,
        IReadOnlyList<string> MissingFaces)
    {
        /// <summary>True when the map asks for a skybox at all (indoor maps do not).</summary>
        public bool Declared => Name.Length > 0;

        /// <summary>True when a declared skybox will not build — the sky renders as the fallback.</summary>
        public bool Broken => Declared && !Resolved;

        /// <summary>True when not one face resolved under any convention (usually a wholly absent env pack).</summary>
        public bool NothingFound => Broken && MissingFaces.Count >= SkyboxPaths.Sides;
    }

    /// <summary>The audit of one map.</summary>
    /// <param name="Entries">Every texture-lump entry, ordered worst-first then by descending face count.</param>
    /// <param name="Sky">The skybox verdict.</param>
    /// <param name="MissingCount">Entries with <see cref="Status.Missing"/>.</param>
    /// <param name="PartialCount">Entries with <see cref="Status.Partial"/>.</param>
    /// <param name="NotDrawnCount">Entries excluded as never-rendered.</param>
    /// <param name="FacesAffected">Faces wearing a Missing or Partial texture.</param>
    public sealed record Report(
        IReadOnlyList<Entry> Entries,
        SkyReport Sky,
        int MissingCount,
        int PartialCount,
        int NotDrawnCount,
        int FacesAffected)
    {
        /// <summary>Total texture-lump entries scanned.</summary>
        public int TextureCount => Entries.Count;

        /// <summary>True when nothing is missing or partially missing, skybox included.</summary>
        public bool Clean => MissingCount == 0 && PartialCount == 0 && !Sky.Broken;
    }

    // Q3SURFACEFLAG_* bits from the BSP texture lump. Re-declared here rather than referenced from
    // VortexArena.Engine.Collision so Formats keeps no edge into the engine — the same choice
    // Game.Loaders.SurfaceFlags documents, and these are canonical Q3/DP values that cannot drift.
    private const int SurfSky    = 0x0004;
    private const int SurfNoDraw = 0x0080;

    /// <summary>
    /// Audit every entry in <paramref name="bsp"/>'s texture lump.
    /// </summary>
    /// <param name="bsp">The parsed map.</param>
    /// <param name="lookupShader">Name → parsed shader, or null when there is none (<c>AssetSystem.GetShader</c>).</param>
    /// <param name="imageExists">
    /// Base name → does an image file resolve for it (<c>VirtualFileSystem.ResolveImage(n) is not null</c>).
    /// Resolution only: a file that exists but fails to decode is a different defect and already logs.
    /// </param>
    public static Report Scan(BspData bsp, Func<string, ShaderDef?> lookupShader, Func<string, bool> imageExists)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(lookupShader);
        ArgumentNullException.ThrowIfNull(imageExists);

        int[] faceCounts = CountFaces(bsp);
        var entries = new List<Entry>(bsp.Textures.Length);
        int missing = 0, partial = 0, notDrawn = 0, facesAffected = 0;

        for (int i = 0; i < bsp.Textures.Length; i++)
        {
            BspTexture tex = bsp.Textures[i];
            int faces = i < faceCounts.Length ? faceCounts[i] : 0;
            Entry entry = Audit(tex, faces, lookupShader, imageExists);
            entries.Add(entry);

            switch (entry.Status)
            {
                case Status.Missing:  missing++;  facesAffected += faces; break;
                case Status.Partial:  partial++;  facesAffected += faces; break;
                case Status.NotDrawn: notDrawn++; break;
            }
        }

        // Worst first, then by how much of the map wears it — the order a mapper wants to fix them in.
        entries.Sort(static (a, b) =>
        {
            int rank = Rank(a.Status).CompareTo(Rank(b.Status));
            if (rank != 0)
                return rank;
            int byFaces = b.FaceCount.CompareTo(a.FaceCount);
            return byFaces != 0 ? byFaces : string.CompareOrdinal(a.Name, b.Name);
        });

        return new Report(entries, AuditSky(bsp, lookupShader, imageExists),
            missing, partial, notDrawn, facesAffected);
    }

    /// <summary>
    /// Does the map's declared skybox have all six faces? Mirrors the loader exactly — same name precedence,
    /// same suffix conventions, same per-face path forms (all from <see cref="SkyboxPaths"/>) — so a verdict
    /// here is a statement about what the loader will actually do, not a parallel guess at it.
    /// </summary>
    private static SkyReport AuditSky(BspData bsp, Func<string, ShaderDef?> lookupShader,
        Func<string, bool> imageExists)
    {
        string name = SkyboxPaths.ResolveName(bsp, lookupShader);
        if (name.Length == 0)
            return new SkyReport(string.Empty, false, Array.Empty<string>(), Array.Empty<string>());

        IReadOnlyList<string>? bestConvention = null;
        List<string>? bestMissing = null;

        foreach (IReadOnlyList<string> convention in SkyboxPaths.Suffixes)
        {
            var missing = new List<string>();
            foreach (string suffix in convention)
            {
                bool found = false;
                foreach (string candidate in SkyboxPaths.FaceCandidates(name, suffix))
                {
                    if (!imageExists(candidate))
                        continue;
                    found = true;
                    break;
                }
                if (!found)
                    missing.Add(suffix);
            }

            if (missing.Count == 0)
                return new SkyReport(name, true, convention, Array.Empty<string>());

            // Fewest gaps wins the right to be reported; ties keep the earlier convention, which is DP's own
            // preference order.
            if (bestMissing is null || missing.Count < bestMissing.Count)
            {
                bestMissing = missing;
                bestConvention = convention;
            }
        }

        return new SkyReport(name, false, bestConvention!, bestMissing!);
    }

    private static int Rank(Status s) => s switch
    {
        Status.Missing => 0,
        Status.Partial => 1,
        Status.Ok => 2,
        _ => 3,
    };

    private static Entry Audit(BspTexture tex, int faceCount,
        Func<string, ShaderDef?> lookupShader, Func<string, bool> imageExists)
    {
        string name = tex.ShaderName ?? string.Empty;

        if (IsCompilerOnly(name) || (tex.SurfaceFlags & (SurfNoDraw | SurfSky)) != 0)
            return new Entry(name, Status.NotDrawn, false, faceCount, Array.Empty<string>());

        ShaderDef? def = lookupShader(name);

        // A shader can declare nodraw/sky with no BSP bit set — very common in Xonotic, and the reason
        // MapLoader.ShouldSkip unions both authorities rather than trusting the lump.
        if (def is not null && (def.SurfaceParms.Contains("nodraw") || def.SurfaceParms.Contains("sky")))
            return new Entry(name, Status.NotDrawn, true, faceCount, Array.Empty<string>());

        if (def is null)
        {
            // No shader: the name IS the image (DP's imageformats_textures search). This is exactly the case
            // DP shouts about at load time.
            bool ok = imageExists(AssetPaths.StripImageExtension(name));
            return ok
                ? new Entry(name, Status.Ok, false, faceCount, Array.Empty<string>())
                : new Entry(name, Status.Missing, false, faceCount,
                    new[] { AssetPaths.StripImageExtension(name) });
        }

        var missingImages = new List<string>();
        int required = 0;
        foreach (ShaderStage stage in def.Stages)
        {
            foreach (string image in StageImages(stage))
            {
                required++;
                if (!imageExists(image))
                    missingImages.Add(image);
            }
        }

        // A shader with no image stages at all is legitimate (a pure surfaceparm/fog/portal shader, or one
        // that is $lightmap-only). Nothing to be missing.
        if (required == 0)
            return new Entry(name, Status.Ok, true, faceCount, Array.Empty<string>());

        if (missingImages.Count == 0)
            return new Entry(name, Status.Ok, true, faceCount, Array.Empty<string>());

        Status status = missingImages.Count >= required ? Status.Missing : Status.Partial;
        return new Entry(name, status, true, faceCount, missingImages);
    }

    /// <summary>
    /// The image base names one stage needs. <c>$lightmap</c>/<c>$whiteimage</c> are engine-generated,
    /// <c>-</c> and empty mean "no map on this stage", and every <c>animMap</c> frame is a real file the
    /// compiler loads (ShaderCompiler loads each frame), so a hole anywhere in the sequence counts.
    /// Extensions are stripped exactly as the loader strips them, or a stage written <c>map foo.tga</c>
    /// would be probed as <c>foo.tga</c> and miss.
    /// </summary>
    private static IEnumerable<string> StageImages(ShaderStage stage)
    {
        if (stage.AnimMap is { Frames.Length: > 0 } anim)
        {
            foreach (string frame in anim.Frames)
                if (IsRealImage(frame))
                    yield return AssetPaths.StripImageExtension(frame);
            yield break;
        }

        if (IsRealImage(stage.MapTexture))
            yield return AssetPaths.StripImageExtension(stage.MapTexture);
    }

    private static bool IsRealImage(string? image)
        => !string.IsNullOrWhiteSpace(image) && image != "-" && !image.StartsWith('$');

    /// <summary>
    /// Names q3map2 consumes and never renders. The <c>textures/common/</c> prefix plus <c>noshader</c>/
    /// <c>NULL</c> is the same exclusion set Xonotic's own <c>misc/tools/bsptool-shaderfun.sh</c> applies when
    /// it walks a BSP's shader list. Checked ahead of the shader lookup so a install whose <c>common.shader</c>
    /// failed to mount reports the real problem once, instead of as a wall of caulk.
    /// </summary>
    private static bool IsCompilerOnly(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;
        if (name.Equals("noshader", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.StartsWith("textures/common/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renderable faces per texture index. Flares carry no geometry and are excluded, matching
    /// <c>MapLoader.BuildMap</c>'s own face filter — a face count is only useful if it means "surfaces you
    /// would see".
    /// </summary>
    private static int[] CountFaces(BspData bsp)
    {
        var counts = new int[bsp.Textures.Length];
        foreach (BspFace face in bsp.Faces)
        {
            if (face.Type == BspFaceType.Flare)
                continue;
            if ((uint)face.TextureIndex < (uint)counts.Length)
                counts[face.TextureIndex]++;
        }
        return counts;
    }
}
