// Port of Base/darkplaces/prvm_cmds.c getmodel, getsurface, getmatrix, applytransform_forward,
// VM_getsurfacenumpoints, VM_getsurfacepoint, VM_getsurfacenormal, VM_getsurfacetexture,
// VM_getsurfacenumtriangles, VM_getsurfacetriangle (DP_QC_GETSURFACE, DP_QC_GETSURFACETRIANGLE), for
// the one kind of model the server program asks them of: the map and its submodels. Xonotic's
// warpzones find their plane this way (lib/warpzone/server.qc WarpZone_InitStep_UpdateTransform).
using System.Numerics;
using VortexArena.Common.Math;
using VortexArena.Formats.Bsp;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

public sealed partial class SvqcHost
{
    // getmodel + getsurface: face `surface` of the brush model the entity shows. False for an entity
    // without one, or a surface number past its last ("return 0 if no such surface").
    private bool TryGetSurface(int edict, int surface, out BspFace face)
    {
        face = default;
        if (!IsLive(edict) || World.Bsp is not { } bsp) return false;
        int modelIndex = QcVm.FloatToInt(Fl(edict, F.ModelIndex));
        if (!ModelIsBrush(modelIndex)) return false;
        string name = ModelName(modelIndex);
        int model = 0;
        if (name.Length > 0 && name[0] == '*' && !int.TryParse(name.AsSpan(1), out model)) return false;
        if ((uint)model >= (uint)bsp.Models.Length) return false;
        BspModel m = bsp.Models[model];
        if (surface < 0 || surface >= m.FaceCount || (uint)(m.FirstFace + surface) >= (uint)bsp.Faces.Length) return false;
        face = bsp.Faces[m.FirstFace + surface];
        // The vertex and index ranges come from the map file: a face whose ranges leave the lumps has
        // no points and no triangles.
        if (face.FirstVertex < 0 || face.VertexCount < 0 || (long)face.FirstVertex + face.VertexCount > bsp.Vertices.Length)
            face = face with { VertexCount = 0, IndexCount = 0 };
        if (face.FirstIndex < 0 || face.IndexCount < 0 || (long)face.FirstIndex + face.IndexCount > bsp.Triangles.Length)
            face = face with { IndexCount = 0 };
        // A patch's triangles exist only after tessellation, which is not done here: it has its
        // control points but no triangles. (DarkPlaces tessellates patches at load.)
        if (face.Type == BspFaceType.Patch) face = face with { IndexCount = 0 };
        return true;
    }

    private void SurfaceArgs(QcVm vm, int count, string name, out int edict, out int surface)
    {
        Parms(count, count, name);
        edict = vm.ArgEdict(0);
        surface = QcVm.FloatToInt(vm.ArgFloat(1));
    }

    // #434 float(entity e, float s) getsurfacenumpoints
    private void GetSurfaceNumPoints(QcVm vm)
    {
        SurfaceArgs(vm, 2, "VM_getsurfacenumpoints", out int e, out int s);
        vm.ReturnFloat(TryGetSurface(e, s, out BspFace face) ? face.VertexCount : 0);
    }

    // #435 vector(entity e, float s, float n) getsurfacepoint: in world space.
    private void GetSurfacePoint(QcVm vm)
    {
        SurfaceArgs(vm, 3, "VM_getsurfacepoint", out int e, out int s);
        vm.ReturnVector(default);
        int point = QcVm.FloatToInt(vm.ArgFloat(2));
        if (!TryGetSurface(e, s, out BspFace face) || point < 0 || point >= face.VertexCount) return;
        // note: this (incorrectly) assumes it is a simple polygon
        vm.ReturnVector(SvWorld.Q(EntityMatrixOf(e, false).Transform(World.Bsp!.Vertices[face.FirstVertex + point].Position)));
    }

    // #436 vector(entity e, float s) getsurfacenormal: of the surface's first three points.
    private void GetSurfaceNormal(QcVm vm)
    {
        SurfaceArgs(vm, 2, "VM_getsurfacenormal", out int e, out int s);
        vm.ReturnVector(default);
        if (!TryGetSurface(e, s, out BspFace face) || face.VertexCount < 3) return;
        // note: this only returns the first triangle, so it doesn't work very well for curved
        // surfaces or arbitrary meshes
        BspVertex[] v = World.Bsp!.Vertices;
        Vector3 a = v[face.FirstVertex].Position, b = v[face.FirstVertex + 1].Position, c = v[face.FirstVertex + 2].Position;
        // TriangleNormal(a, b, c) = (a - b) x (c - b)
        Vector3 normal = EntityMatrixOf(e, false).Rotate(Vector3.Cross(a - b, c - b));
        float length = normal.Length();
        vm.ReturnVector(length > 0 ? SvWorld.Q(normal / length) : default);
    }

    // #437 string(entity e, float s) getsurfacetexture: the null string past the last surface, which
    // is how a program counts them.
    private void GetSurfaceTexture(QcVm vm)
    {
        SurfaceArgs(vm, 2, "VM_getsurfacetexture", out int e, out int s);
        vm.ReturnInt(0);
        if (!TryGetSurface(e, s, out BspFace face)) return;
        BspTexture[] textures = World.Bsp!.Textures;
        vm.ReturnInt(vm.TempString((uint)face.TextureIndex < (uint)textures.Length ? textures[face.TextureIndex].ShaderName : ""));
    }

    // #628 float(entity e, float s) getsurfacenumtriangles
    private void GetSurfaceNumTriangles(QcVm vm)
    {
        SurfaceArgs(vm, 2, "VM_getsurfacenumtriangles", out int e, out int s);
        vm.ReturnFloat(TryGetSurface(e, s, out BspFace face) ? face.IndexCount / 3 : 0);
    }

    // #629 vector(entity e, float s, float n) getsurfacetriangle: three point numbers of the surface.
    private void GetSurfaceTriangle(QcVm vm)
    {
        SurfaceArgs(vm, 3, "VM_getsurfacetriangle", out int e, out int s);
        vm.ReturnVector(default);
        int triangle = QcVm.FloatToInt(vm.ArgFloat(2));
        if (!TryGetSurface(e, s, out BspFace face) || triangle < 0 || triangle >= face.IndexCount / 3) return;
        int[] indices = World.Bsp!.Triangles;
        int at = face.FirstIndex + triangle * 3;
        // The map's indices are already relative to the surface's first vertex, which is what the C
        // arrives at by subtracting num_firstvertex from its model-wide element numbers.
        vm.ReturnVector(new QcVector(indices[at], indices[at + 1], indices[at + 2]));
    }
}
