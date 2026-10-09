using System;
using VortexArena.Formats.Md3;

namespace VortexArena.Formats.Mdl;

/// <summary>
/// The engine-independent parts of drawing a Quake <c>.mdl</c>: which skin picture a skin number shows at a
/// time, and the model in the shape the vertex-morph renderer takes.
/// </summary>
public static class MdlRender
{
    /// <summary>
    /// The skin picture (an index into the flattened pictures, <see cref="MdlScene.First"/> based) that skin
    /// number <paramref name="skin"/> shows at <paramref name="shaderTime"/> seconds on the entity's shader
    /// clock. DarkPlaces: "if (skinnum >= numskins) skinnum = 0" (cl_main.c), then for a skin group
    /// <c>firstframe + (unsigned int)(shadertime * framerate) % framecount</c> (gl_rmain.c
    /// R_GetCurrentTexture). -1 for a model without skins.
    /// </summary>
    public static int SkinPicture(ReadOnlySpan<MdlScene> scenes, int skin, double shaderTime)
    {
        if (scenes.Length == 0)
            return -1;
        if ((uint)skin >= (uint)scenes.Length)
            skin = 0;
        MdlScene scene = scenes[skin];
        if (scene.Count <= 1)
            return scene.Count == 1 ? scene.First : -1;
        double ticks = shaderTime * scene.FrameRate;
        uint whole = double.IsFinite(ticks) && ticks > 0 ? (uint)Math.Min(ticks, uint.MaxValue) : 0u;
        return scene.First + (int)(whole % (uint)scene.Count);
    }

    /// <summary>
    /// The model as one MD3-shaped surface named "default" (the mesh name a <c>.skin</c> file uses for a
    /// <c>.mdl</c>): DarkPlaces' compacted vertices - a seam vertex used by a back-facing triangle is a vertex
    /// of its own, with U + 0.5 - its element array, and every POSE's positions and normals. A frame number
    /// of the result is a pose (<see cref="MdlData.Frames"/>), not a <c>.frame</c> value
    /// (<see cref="MdlData.Scenes"/>).
    /// </summary>
    public static Md3Data ToMd3(MdlData mdl)
    {
        ArgumentNullException.ThrowIfNull(mdl);
        int vertices = mdl.MeshVertices.Length;
        var frames = new Md3Frame[mdl.Frames.Length];
        var frameVertices = new Md3Vertex[mdl.Frames.Length][];
        for (int f = 0; f < frames.Length; f++)
        {
            MdlVertex[] source = mdl.Frames[f].Vertices;
            var pose = new Md3Vertex[vertices];
            for (int v = 0; v < vertices; v++)
            {
                MdlVertex from = source[mdl.MeshVertices[v]];
                pose[v] = new Md3Vertex(from.Position, from.Normal);
            }
            frameVertices[f] = pose;
            frames[f] = new Md3Frame(mdl.Mins, mdl.Maxs, System.Numerics.Vector3.Zero, mdl.Radius, mdl.Frames[f].Name);
        }
        return new Md3Data
        {
            Name = mdl.Name,
            FrameCount = frames.Length,
            Frames = frames,
            Surfaces = new[]
            {
                new Md3Surface
                {
                    Name = "default",
                    VertexCount = vertices,
                    Triangles = mdl.MeshElements,
                    TexCoords = mdl.MeshTexCoords,
                    FrameVertices = frameVertices,
                },
            },
        };
    }
}
