using System;
using Godot;
using VortexArena.Formats.Md3;
using VortexArena.Formats.Sidecars;
using VortexArena.Game.Loaders;

namespace VortexArena.Game;

/// <summary>
/// Turns a parsed <see cref="Md3Data"/> (the Godot-free MD3 importer output) into Godot scene nodes, and is
/// the project's MD3-specific helper (static snapshot mesh + tag markers) that the client view-model /
/// animator build on.
///
///  * <see cref="BuildModel"/> — a static snapshot of one animation frame as an <see cref="ArrayMesh"/>
///    (one surface per MD3 surface). MD3 is vertex-morph; this bakes a single frame. Full morph-target
///    or baked-Animation playback is a later pass (asset-pipeline.md "MD3 → morph targets").
///  * <see cref="BuildTags"/> — a <see cref="Marker3D"/> per attachment tag, posed at the tag's frame
///    transform, so weapons/effects can be parented to these sockets (gettaginfo/setattachment).
///  * <see cref="LoadModel"/> — the path-based entry: it routes through the unified
///    <see cref="AssetLoader"/> so a model is dispatched by magic (IQM/DPM/MD3) and skinned/animated by the
///    new asset pipeline. Existing callers that already hold a parsed <see cref="Md3Data"/> keep using
///    <see cref="BuildModel"/>/<see cref="BuildTags"/> unchanged.
///
/// All positions/axes convert from Quake (Z-up) to Godot (Y-up) at the boundary via <see cref="Coords"/>.
/// Materials (the surface shader names) are left to the material compiler — surfaces are untextured.
/// </summary>
public static class ModelLoader
{
    /// <summary>
    /// Load a model by virtual path through the unified asset pipeline (the magic-dispatching
    /// <see cref="AssetLoader.LoadModel"/>): IQM/DPM/MD3 are detected by their file magic and built with
    /// their sidecar framegroups/skin applied. This is the preferred entry for new code; it supersedes the
    /// MD3-only <see cref="BuildModel"/> path for anything loading from a file. Returns null on miss.
    /// </summary>
    public static Node3D? LoadModel(AssetLoader loader, string vpath, int skinIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return loader.LoadModel(vpath, skinIndex);
    }

    /// <summary>
    /// Build a <see cref="MeshInstance3D"/> holding an <see cref="ArrayMesh"/> snapshot of
    /// <paramref name="frame"/>. Each MD3 surface becomes one ArrayMesh surface (Vertex/Normal/TexUV/Index).
    /// </summary>
    public static MeshInstance3D BuildModel(Md3Data md3, int frame = 0, AssetSystem? assets = null, SkinFile? skin = null)
    {
        var mesh = new ArrayMesh();
        int frameCount = md3.FrameCount;

        int surfaceIndex = 0;
        foreach (Md3Surface surface in md3.Surfaces)
        {
            int vcount = surface.VertexCount;
            if (vcount <= 0 || surface.Triangles.Length == 0)
                continue;
            if (surface.FrameVertices.Length == 0)
                continue;

            // Resolve the surface material when an AssetSystem is supplied: the skin remap (by mesh name) wins,
            // else the surface's own first shader name; a nodraw remap hides the surface. Without an AssetSystem
            // the mesh is built untextured (legacy behavior). Mirrors Md3Builder/Md3Morph.ResolveSurfaces.
            Material? material = null;
            if (assets is not null)
            {
                string shader = surface.Shaders.Length > 0 ? surface.Shaders[0] : surface.Name;
                bool visible = true;
                if (skin is not null && skin.MeshToTexture.TryGetValue(surface.Name, out string? remap))
                {
                    if (SkinFile.IsNoDraw(remap))
                        visible = false;
                    else if (!string.IsNullOrEmpty(remap))
                        shader = remap;
                }
                if (!visible)
                    continue;
                material = assets.ResolveMaterial(shader);
            }

            // Clamp the requested frame into this surface's available frames.
            int f = frame;
            if (f < 0) f = 0;
            if (f >= surface.FrameVertices.Length) f = surface.FrameVertices.Length - 1;
            Md3Vertex[] frameVerts = surface.FrameVertices[f];
            if (frameVerts.Length < vcount)
                continue;

            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            for (int v = 0; v < vcount; v++)
            {
                positions[v] = Coords.ToGodot(frameVerts[v].Position);
                normals[v] = Coords.ToGodot(frameVerts[v].Normal);
                uvs[v] = v < surface.TexCoords.Length
                    ? new Vector2(surface.TexCoords[v].X, surface.TexCoords[v].Y)
                    : Vector2.Zero;
            }

            // MD3 triangles index into this surface's own vertices.
            var indices = new int[surface.Triangles.Length];
            for (int t = 0; t < surface.Triangles.Length; t++)
            {
                int idx = surface.Triangles[t];
                indices[t] = (idx >= 0 && idx < vcount) ? idx : 0;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (material is not null)
                mesh.SurfaceSetMaterial(surfaceIndex, material);
            surfaceIndex++;
        }

        _ = frameCount; // referenced for clarity; frame clamping is per-surface above
        return new MeshInstance3D { Name = string.IsNullOrEmpty(md3.Name) ? "Md3Model" : md3.Name, Mesh = mesh };
    }

    /// <summary>
    /// Create one <see cref="Marker3D"/> per tag, posed at the tag's transform for <paramref name="frame"/>
    /// (origin + 3x3 axis converted into Godot space). Returns a parent <see cref="Node3D"/> holding them;
    /// attach weapons/effects by reparenting onto the matching marker.
    /// </summary>
    public static Node3D BuildTags(Md3Data md3, int frame)
    {
        var root = new Node3D { Name = "Tags" };

        foreach (Md3Tag tag in md3.Tags)
        {
            if (tag.Transforms.Length == 0)
                continue;
            int f = frame;
            if (f < 0) f = 0;
            if (f >= tag.Transforms.Length) f = tag.Transforms.Length - 1;
            Md3TagTransform xf = tag.Transforms[f];

            // Convert the Quake basis (3 axis vectors) and origin into a Godot transform. Each Quake
            // basis vector maps through ToGodot; assembling them as the columns gives the Godot rotation.
            var basis = new Basis(
                Coords.ToGodot(xf.AxisX),
                Coords.ToGodot(xf.AxisY),
                Coords.ToGodot(xf.AxisZ));

            var marker = new Marker3D
            {
                Name = string.IsNullOrEmpty(tag.Name) ? "tag" : tag.Name,
                Transform = new Transform3D(basis, Coords.ToGodot(xf.Origin)),
            };
            root.AddChild(marker);
        }

        return root;
    }
}
