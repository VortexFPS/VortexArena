using System;
using Godot;
using VortexArena.Formats.Mdl;

namespace VortexArena.Game.Loaders.Models;

/// <summary>
/// Turns a parsed <see cref="MdlData"/> (the Godot-free Quake1 "IDPO" importer output) into a Godot scene
/// node. Unlike <see cref="Md3Builder"/> the shipped MDLs that reach the loader are static single-frame props
/// (the shotgun shell casing, the gib chunk), so this builds a plain <see cref="MeshInstance3D"/> showing
/// frame 0 — no morph/animation node needed.
///
/// <para>The geometry (<see cref="ArrayMesh"/>) and the palette-decoded skin material are immutable and are
/// built once via <see cref="Prepare"/>; <see cref="Instantiate"/> then hands out lightweight
/// <see cref="MeshInstance3D"/>s that share those resources — the pattern <c>AssetLoader.BuildModelFactory</c>
/// uses so the per-casing/per-gib spawn is a cheap node alloc, not a re-decode. The skin is applied as a
/// <see cref="MeshInstance3D.MaterialOverride"/> (not a surface material) so the shared resource is never
/// mutated by a per-instance fade — the same "loaded models just pop" behaviour <c>ShellCasings</c> documents.</para>
///
/// Positions/normals convert Quake (Z-up) → Godot (Y-up) at the boundary via <see cref="Coords"/>.
/// </summary>
public static class MdlBuilder
{
    /// <summary>Shared, immutable render resources for one MDL model + frame: reuse across every instance.</summary>
    public sealed record Prepared(ArrayMesh Mesh, Material? SkinMaterial, string Name);

    /// <summary>Build the shared mesh + skin material for <paramref name="frame"/> (default 0). Do this once.</summary>
    public static Prepared Prepare(MdlData mdl, int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        if (mdl.Frames.Length == 0 || mdl.Corners.Length == 0)
            return new Prepared(new ArrayMesh(), null, "MdlModel");

        int f = Math.Clamp(frame, 0, mdl.Frames.Length - 1);
        MdlVertex[] verts = mdl.Frames[f].Vertices;

        int n = mdl.Corners.Length;
        var positions = new Vector3[n];
        var normals = new Vector3[n];
        var uvs = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            MdlCorner c = mdl.Corners[i];
            MdlVertex v = verts[c.Vertex];
            positions[i] = Coords.ToGodot(v.Position);
            Vector3 gn = Coords.ToGodot(v.Normal);
            normals[i] = gn.LengthSquared() > 1e-8f ? gn.Normalized() : Vector3.Up;
            uvs[i] = new Vector2(c.Uv.X, c.Uv.Y);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        string name = string.IsNullOrEmpty(mdl.Name) ? "MdlModel" : Sanitize(mdl.Name);
        return new Prepared(mesh, BuildSkinMaterial(mdl), name);
    }

    /// <summary>A fresh <see cref="MeshInstance3D"/> sharing <paramref name="prepared"/>'s mesh + material.</summary>
    public static Node3D Instantiate(Prepared prepared) => new MeshInstance3D
    {
        Name = prepared.Name,
        Mesh = prepared.Mesh,
        MaterialOverride = prepared.SkinMaterial,
    };

    /// <summary>Convenience one-shot (prepare + instantiate) for one-off callers / tests.</summary>
    public static Node3D Build(MdlData mdl, int frame = 0) => Instantiate(Prepare(mdl, frame));

    /// <summary>Wrap the decoded skin as a lit, textured material — null when the model ships no skin.</summary>
    private static Material? BuildSkinMaterial(MdlData mdl)
    {
        if (mdl.SkinWidth <= 0 || mdl.SkinHeight <= 0 ||
            mdl.SkinRgba.Length != mdl.SkinWidth * mdl.SkinHeight * 4)
            return null;

        var img = Image.CreateFromData(mdl.SkinWidth, mdl.SkinHeight, false, Image.Format.Rgba8, mdl.SkinRgba);
        var tex = ImageTexture.CreateFromImage(img);
        return new StandardMaterial3D
        {
            AlbedoTexture = tex,
            // The skin bakes the casing's shading/colour, so keep the surface mostly matte — a low metallic
            // avoids needing a reflection probe to not look black, while a little sheen reads as brass.
            Metallic = 0.1f,
            Roughness = 0.7f,
        };
    }

    /// <summary>Strip characters Godot disallows in node names (mirrors <see cref="Md3Morph"/>).</summary>
    private static string Sanitize(string raw)
    {
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (char ch in raw)
            buf[n++] = ch is ':' or '/' or '@' or '%' or '.' ? '_' : ch;
        return new string(buf[..n]);
    }
}
