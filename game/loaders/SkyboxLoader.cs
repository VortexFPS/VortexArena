using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Materials;

namespace VortexArena.Game.Loaders;

/// <summary>
/// Builds a Godot <see cref="Sky"/> from a Quake/Xonotic 6-face skybox, faithfully reproducing how the
/// reference DarkPlaces engine resolves and orients sky textures (<c>darkplaces/r_sky.c</c>).
///
/// <para><b>Name resolution.</b> DP picks the skybox from two sources (<c>darkplaces/cl_parse.c</c>
/// <c>CL_ParseEntityLump</c>): a Q3 sky shader's <c>skyParms &lt;farbox&gt;</c> is the per-map default
/// (<c>model_shared.c</c> sets <c>brush.skybox</c>), and a worldspawn <c>sky</c>/<c>skyname</c> key
/// overrides it. <see cref="ResolveSkyName"/> mirrors that precedence.</para>
///
/// <para><b>Face files.</b> For a base name DP probes three suffix conventions — <c>px/nx/py/ny/pz/nz</c>,
/// <c>posx/.../negz</c>, and <c>rt/lf/bk/ft/up/dn</c> — each under the path forms <c>NAME_suf</c>,
/// <c>NAMEsuf</c>, <c>env/NAMEsuf</c>, <c>gfx/env/NAMEsuf</c> (<c>r_sky.c</c> <c>R_LoadSkyBox</c>). The
/// <c>rt/lf/...</c> convention also carries per-face flips (transpose / mirror) that DP bakes into the
/// pixels at load via <c>Image_CopyMux</c>; <see cref="ApplyMux"/> applies the identical flips so every
/// convention lands in one canonical box orientation.</para>
///
/// <para><b>Rendering.</b> DP draws a cube at the view origin (<c>R_SkyBox</c>); we instead drive a Godot
/// sky shader that, for each view ray, selects the box face and samples that face's canonical-oriented
/// texture with the same per-vertex texcoords DP's box uses — so the on-screen result matches the
/// reference. The view ray is converted Godot(Y-up) → Quake(Z-up) exactly as <see cref="Coords"/> does.</para>
/// </summary>
public static class SkyboxLoader
{
    // DP box side order: 0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z (r_sky.c skyboxvertex3f). suffix[conv][side]
    // copied verbatim from r_sky.c:27-53 (the suffix[3][6] table).
    private static readonly string[][] Suffixes =
    {
        new[] { "px", "nx", "py", "ny", "pz", "nz" },
        new[] { "posx", "negx", "posy", "negy", "posz", "negz" },
        new[] { "rt", "lf", "bk", "ft", "up", "dn" },
    };

    // (flipX, flipY, flipDiagonal) per side, per convention — the third column of r_sky.c's suffix table.
    // Only the rt/lf/... convention flips; px/nx and posx/negx are stored already box-aligned.
    private static readonly (bool Fx, bool Fy, bool Diag)[][] Flips =
    {
        new[] { (false, false, false), (false, false, false), (false, false, false),
                (false, false, false), (false, false, false), (false, false, false) },
        new[] { (false, false, false), (false, false, false), (false, false, false),
                (false, false, false), (false, false, false), (false, false, false) },
        new[] { (false, false, true),  (true,  true,  true),  (false, true,  false),
                (true,  false, false), (false, false, true),  (false, false, true) },
    };

    private static readonly string[] Uniforms = { "face_px", "face_nx", "face_py", "face_ny", "face_pz", "face_nz" };

    /// <summary>
    /// Resolve the map's skybox and build a ready-to-use <see cref="Sky"/>, or null when the map declares no
    /// skybox or its faces cannot be loaded (callers then keep their fallback sky).
    /// </summary>
    public static Sky? TryBuild(BspData? bsp, AssetSystem? assets)
    {
        if (bsp is null || assets is null)
            return null;
        string name = ResolveSkyName(bsp, assets);
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Build(name, assets);
    }

    /// <summary>
    /// The skybox base name for this map: a worldspawn <c>sky</c>/<c>skyname</c> key if present (DP override),
    /// otherwise the first sky shader's <c>skyParms</c> far box (DP default). Empty when neither exists.
    /// </summary>
    public static string ResolveSkyName(BspData bsp, AssetSystem assets)
    {
        // worldspawn override (DP CL_ParseEntityLump: a "sky"/"skyname" key beats the shader default).
        string ws = MapLoader.BuildWorldspawn(bsp).Sky;
        if (!string.IsNullOrWhiteSpace(ws))
            return ws.Trim();

        // shader default: first surface whose shader is a sky shader carrying a skyParms far box.
        foreach (BspTexture t in bsp.Textures)
        {
            ShaderDef? def = assets.GetShader(t.ShaderName);
            string? fb = def?.SkyParms?.FarBox;
            if (def is not null && def.IsSky && !string.IsNullOrWhiteSpace(fb))
                return fb!.Trim();
        }
        return string.Empty;
    }

    private static Sky? Build(string name, AssetSystem assets)
    {
        Image[]? faces = LoadFaces(name, assets);
        if (faces is null)
        {
            GD.PrintErr($"[Skybox] '{name}': could not load all 6 faces; using fallback sky.");
            return null;
        }

        var mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        for (int i = 0; i < 6; i++)
        {
            faces[i].GenerateMipmaps();
            mat.SetShaderParameter(Uniforms[i], ImageTexture.CreateFromImage(faces[i]));
        }

        GD.Print($"[Skybox] loaded '{name}'.");
        return new Sky { SkyMaterial = mat, RadianceSize = Sky.RadianceSizeEnum.Size256 };
    }

    // Try each suffix convention in DP's order; use the first whose six faces all load. Each face is
    // reoriented to the canonical box layout the shader samples.
    private static Image[]? LoadFaces(string name, AssetSystem assets)
    {
        for (int conv = 0; conv < Suffixes.Length; conv++)
        {
            var faces = new Image[6];
            bool all = true;
            for (int side = 0; side < 6; side++)
            {
                Image? img = LoadFace(name, Suffixes[conv][side], assets);
                if (img is null) { all = false; break; }
                var (fx, fy, diag) = Flips[conv][side];
                faces[side] = ApplyMux(img, fx, fy, diag);
            }
            if (all)
                return faces;
        }
        return null;
    }

    // DP R_LoadSkyBox path forms: the "_" separator only on the first form (so "env/foo/foo" + "_" + "rt"
    // hits env/foo/foo_rt, and a name already ending in "_" hits via the second form). Extension search is
    // handled by VirtualFileSystem.ResolveImage inside LoadImage.
    private static Image? LoadFace(string name, string suffix, AssetSystem assets)
        => assets.LoadImage(name + "_" + suffix)
        ?? assets.LoadImage(name + suffix)
        ?? assets.LoadImage("env/" + name + suffix)
        ?? assets.LoadImage("gfx/env/" + name + suffix);

    /// <summary>
    /// Reorient a face exactly as DP's <c>Image_CopyMux</c> does (image.c): <paramref name="diag"/> transposes
    /// (swapping width/height), then <paramref name="fx"/>/<paramref name="fy"/> mirror. Operates on raw RGBA8.
    /// </summary>
    private static Image ApplyMux(Image src, bool fx, bool fy, bool diag)
    {
        if (src.GetFormat() != Image.Format.Rgba8)
            src.Convert(Image.Format.Rgba8);

        int w = src.GetWidth(), h = src.GetHeight();
        byte[] inb = src.GetData();

        if (!fx && !fy && !diag)
            return src; // identity (px/nx and posx/negx conventions)

        int ow = diag ? h : w;
        int oh = diag ? w : h;
        var outb = new byte[ow * oh * 4];

        for (int oy = 0; oy < oh; oy++)
        for (int ox = 0; ox < ow; ox++)
        {
            int ix, iy;
            if (!diag)
            {
                ix = fx ? w - 1 - ox : ox;
                iy = fy ? h - 1 - oy : oy;
            }
            else
            {
                // Image_CopyMux diagonal case: out[oy][ox] = in[fy(ox)][fx(oy)] (out dims are h x w).
                ix = fx ? w - 1 - oy : oy;
                iy = fy ? h - 1 - ox : ox;
            }
            int si = (iy * w + ix) * 4;
            int di = (oy * ow + ox) * 4;
            outb[di]     = inb[si];
            outb[di + 1] = inb[si + 1];
            outb[di + 2] = inb[si + 2];
            outb[di + 3] = inb[si + 3];
        }

        return Image.CreateFromData(ow, oh, false, Image.Format.Rgba8, outb);
    }

    // A Godot sky shader that reproduces DP's R_SkyBox: for each view ray it selects the cube face and
    // samples the (canonical-oriented) face texture with the same per-vertex texcoords DP's box uses.
    // EYEDIR is Godot world space (Y-up); q is the Quake-space ray (see game/Coords.cs ToQuake). The faces
    // are loaded sRGB (source_color) so they tonemap with the rest of the linear-space frame.
    private const string ShaderCode = @"
shader_type sky;

uniform sampler2D face_px : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D face_nx : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D face_py : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D face_ny : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D face_pz : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D face_nz : source_color, filter_linear_mipmap, repeat_disable;

void sky() {
    // Godot (Y-up) view direction -> Quake (Z-up) direction.
    vec3 q = vec3(EYEDIR.x, -EYEDIR.z, EYEDIR.y);
    vec3 a = abs(q);
    float m = max(a.x, max(a.y, a.z));
    vec3 n = q / m;            // the dominant axis component is +-1
    vec3 c;
    if (a.x >= a.y && a.x >= a.z) {
        if (n.x > 0.0) c = texture(face_px, vec2((1.0 - n.z) * 0.5, (1.0 - n.y) * 0.5)).rgb; // +X (rt)
        else           c = texture(face_nx, vec2((1.0 + n.z) * 0.5, (1.0 - n.y) * 0.5)).rgb; // -X (lf)
    } else if (a.y >= a.z) {
        if (n.y > 0.0) c = texture(face_py, vec2((1.0 + n.x) * 0.5, (1.0 + n.z) * 0.5)).rgb; // +Y (bk)
        else           c = texture(face_ny, vec2((1.0 + n.x) * 0.5, (1.0 - n.z) * 0.5)).rgb; // -Y (ft)
    } else {
        if (n.z > 0.0) c = texture(face_pz, vec2((1.0 + n.x) * 0.5, (1.0 - n.y) * 0.5)).rgb; // +Z (up)
        else           c = texture(face_nz, vec2((1.0 - n.x) * 0.5, (1.0 - n.y) * 0.5)).rgb; // -Z (dn)
    }
    COLOR = c;
}
";
}
