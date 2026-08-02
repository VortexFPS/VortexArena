using System;
using System.Collections.Generic;
using VortexArena.Formats.Bsp;

namespace VortexArena.Formats.Materials;

/// <summary>
/// Where a map's skybox comes from and which files back it — DarkPlaces' <c>R_LoadSkyBox</c> (<c>r_sky.c</c>)
/// and <c>CL_ParseEntityLump</c> (<c>cl_parse.c</c>) name resolution, as data.
///
/// <para><b>Why this is not simply inside the loader.</b> Two callers need the same answer: the loader that
/// builds the sky, and the <see cref="MapTextureAudit"/> that reports a map's missing art. If they enumerated
/// candidates separately the audit could confidently report a skybox missing that the loader finds, or stay
/// quiet about one it does not — a diagnostic that disagrees with the thing it diagnoses is worse than no
/// diagnostic. So the tables and the precedence live here once, in the Godot-free library where they can be
/// pinned by tests, and <c>Game.Loaders.SkyboxLoader</c> consumes them.</para>
/// </summary>
public static class SkyboxPaths
{
    /// <summary>Faces per box, in DP's side order: 0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z.</summary>
    public const int Sides = 6;

    /// <summary>
    /// The three suffix conventions DP probes, in order, indexed <c>[convention][side]</c> — copied verbatim
    /// from <c>r_sky.c</c>'s <c>suffix[3][6]</c> table. The first convention whose six faces ALL resolve wins.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> Suffixes = new[]
    {
        new[] { "px", "nx", "py", "ny", "pz", "nz" },
        new[] { "posx", "negx", "posy", "negy", "posz", "negz" },
        new[] { "rt", "lf", "bk", "ft", "up", "dn" },
    };

    /// <summary>
    /// Per-face reorientation flags paired with <see cref="Suffixes"/> by index (the third column of the same
    /// <c>r_sky.c</c> table): transpose, then mirror. Only the <c>rt/lf/…</c> convention flips — the other two
    /// are stored already box-aligned. Kept beside the suffixes on purpose: they are one table, and splitting
    /// them across assemblies is how the pairing would silently rot.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<(bool Fx, bool Fy, bool Diag)>> Flips = new[]
    {
        new[] { (false, false, false), (false, false, false), (false, false, false),
                (false, false, false), (false, false, false), (false, false, false) },
        new[] { (false, false, false), (false, false, false), (false, false, false),
                (false, false, false), (false, false, false), (false, false, false) },
        new[] { (false, false, true),  (true,  true,  true),  (false, true,  false),
                (true,  false, false), (false, false, true),  (false, false, true) },
    };

    /// <summary>The worldspawn keys that name a skybox, in the order the first non-empty one wins.</summary>
    private static readonly string[] SkyKeys = { "sky", "_skybox", "skyname", "skybox" };

    /// <summary>
    /// This map's skybox base name, by DP's precedence: a worldspawn <c>sky</c>/<c>skyname</c> key overrides,
    /// otherwise the first sky shader's <c>skyParms</c> far box supplies the default. Empty when the map
    /// declares no skybox at all (an indoor map, or one whose sky is a drawn shader dome rather than a box).
    /// </summary>
    public static string ResolveName(BspData bsp, Func<string, ShaderDef?> lookupShader)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(lookupShader);

        string ws = WorldspawnSky(bsp);
        if (!string.IsNullOrWhiteSpace(ws))
            return ws.Trim();

        foreach (BspTexture t in bsp.Textures)
        {
            ShaderDef? def = lookupShader(t.ShaderName);
            string? farBox = def?.SkyParms?.FarBox;
            if (def is { IsSky: true } && !string.IsNullOrWhiteSpace(farBox) && farBox != "-")
                return farBox!.Trim();
        }
        return string.Empty;
    }

    /// <summary>The worldspawn skybox key, or empty. Exposed so the worldspawn parse reads one key list.</summary>
    public static string WorldspawnSky(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        if (bsp.Entities.Count == 0)
            return string.Empty;

        IReadOnlyDictionary<string, string> ws = FindWorldspawn(bsp);
        foreach (string key in SkyKeys)
            if (ws.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return string.Empty;
    }

    /// <summary>Worldspawn by classname, falling back to the first entity (the Quake convention).</summary>
    private static IReadOnlyDictionary<string, string> FindWorldspawn(BspData bsp)
    {
        foreach (IReadOnlyDictionary<string, string> ent in bsp.Entities)
            if (ent.TryGetValue("classname", out string? cn) && cn == "worldspawn")
                return ent;
        return bsp.Entities[0];
    }

    /// <summary>
    /// The image base names DP probes for one face, in order (<c>R_LoadSkyBox</c>). The <c>_</c> separator is
    /// on the FIRST form only, so <c>env/foo/foo</c> + <c>rt</c> hits <c>env/foo/foo_rt</c> and a name already
    /// ending in <c>_</c> still hits via the second. Extension search happens downstream, in the VFS.
    /// </summary>
    public static IEnumerable<string> FaceCandidates(string name, string suffix)
    {
        yield return name + "_" + suffix;
        yield return name + suffix;
        yield return "env/" + name + suffix;
        yield return "gfx/env/" + name + suffix;
    }
}
