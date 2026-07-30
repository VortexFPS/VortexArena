// The single reusable Draw_CylindricLine primitive (the CSQC qcsrc/client/view.qc Draw_CylindricLine successor).
//
// This is pure presentation — no networking, no entity-feed reads, no stats. It is the cheap view-facing-cylinder
// stand-in (two perpendicular textured quads forming a "+"-section cross-ribbon) shared identically by every
// beam/rope/line renderer in the port: LaserRenderer (Draw_Laser), HookRopeRenderer (Draw_GrapplingHook),
// PortoTrajectoryPreview (Porto_Draw) and BeamRenderer. Those four had four byte-for-byte copies of the same
// cross-ribbon technique; this consolidates it.
//
// Two flavours, lifted verbatim from the originals:
//   * The in-place-updatable <see cref="Segment"/> pool — a long-lived Node3D per drawn line, oriented +X along
//     the segment with a stable basis and scaled to (length, width) each frame. Matches LaserRenderer.UpdateBeam,
//     HookRopeRenderer.UpdateRope and PortoTrajectoryPreview.UpdateSegment exactly.
//   * The static multi-point <see cref="BuildCrossRibbonMesh"/> spawn-and-free ArrayMesh builder, lifted verbatim
//     from BeamRenderer.BuildCrossRibbon — one mesh for a whole multi-point path.
//
// All Quake-space endpoints are converted via Coords.ToGodot. A Quake unit ≈ a render unit at this scale, so the
// width passed in is in Quake units, the same convention every caller already used.

using System;
using System.Collections.Generic;
using Godot;
using NVec3 = System.Numerics.Vector3;
using GVec3 = Godot.Vector3;

namespace VortexArena.Game.Client;

/// <summary>The single reusable Draw_CylindricLine primitive: a <see cref="Node3D"/> owning a pool of
/// in-place-updatable cross-ribbon <see cref="Segment"/>s, plus the static multi-point <see cref="BuildCrossRibbonMesh"/>
/// path-mesh builder. Pure presentation (no networking) — the cheap view-facing-cylinder stand-in shared by every
/// beam/rope/line renderer (LaserRenderer/HookRopeRenderer/PortoTrajectoryPreview/BeamRenderer).</summary>
public sealed partial class CylindricLine : Node3D
{
    /// <summary>The unit quad every ribbon instances (centered at the origin, lies in the XY plane). One copy is
    /// shared across all segments and renderers — the same SharedQuad HookRopeRenderer/PortoTrajectoryPreview use.</summary>
    private static readonly QuadMesh SharedQuad = new() { Size = new Vector2(1f, 1f) };

    /// <summary>The free-list of parked segments owned by this node (created lazily under <c>AddChild</c>).</summary>
    private readonly List<Segment> _segments = new();
    private int _acquired;

    /// <summary>
    /// One long-lived cross-ribbon line: two perpendicular unit quads parked at local +0.5 X (so the
    /// <see cref="Root"/> sits at the segment START), crossed 90° about X, oriented +X along the line and scaled
    /// to (length, width) each frame. The byte-for-byte shared technique from LaserRenderer.UpdateBeam,
    /// HookRopeRenderer.UpdateRope and PortoTrajectoryPreview.UpdateSegment.
    /// </summary>
    public sealed class Segment
    {
        public Node3D Root = null!;              // positioned at the segment START, +X aimed at the end
        public MeshInstance3D RibbonA = null!;   // the two crossed quads
        public MeshInstance3D RibbonB = null!;
        public StandardMaterial3D Material = null!;

        /// <summary>Orient and scale the ribbon from Quake-space endpoints. Hides the root if the segment is
        /// shorter than one render unit (mirrors the <c>len &lt; 1</c> guard in all three originals).</summary>
        public void Update(NVec3 fromQuake, NVec3 toQuake, float widthQuake, Color rgba,
            BaseMaterial3D.BlendModeEnum blend = BaseMaterial3D.BlendModeEnum.Add)
        {
            GVec3 a = Coords.ToGodot(fromQuake);
            GVec3 b = Coords.ToGodot(toQuake);
            GVec3 seg = b - a;
            float len = seg.Length();
            if (len < 1f)
            {
                Root.Visible = false;
                return;
            }

            Root.Visible = true;
            Root.Position = a;
            // +X along the line (the stable-basis trick from LaserRenderer/ProjectileRenderer.OrientToVelocity).
            GVec3 x = seg / len;
            GVec3 upRef = Mathf.Abs(x.Dot(GVec3.Up)) > 0.99f ? GVec3.Forward : GVec3.Up;
            GVec3 z = x.Cross(upRef).Normalized();
            GVec3 y = z.Cross(x).Normalized();
            Root.Basis = new Basis(x, y, z);
            RibbonA.Scale = new GVec3(len, widthQuake, 1f);
            RibbonB.Scale = new GVec3(len, widthQuake, 1f);
            Material.BlendMode = blend;
            Material.AlbedoColor = rgba;
        }

        /// <summary>Apply (or clear) a scrolling beam texture, folding LaserRenderer.ApplyBeamTexture and its
        /// per-frame Uv1Scale/Uv1Offset scroll. The texture is tiled along the beam (U) proportional to
        /// length/width so texels stay ~square, then scrolled by <paramref name="scrollU"/>.</summary>
        public void SetTexture(Texture2D? tex, bool scrollTiled = false, float scrollU = 0f)
        {
            Material.AlbedoTexture = tex;
            if (tex is null)
                return;

            Material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
            Material.TextureRepeat = true;
            if (scrollTiled)
            {
                // Tile proportional to the current ribbon length/width (RibbonA.Scale = (len, width, 1)).
                float len = RibbonA.Scale.X;
                float width = RibbonA.Scale.Y;
                float tile = width > 0f ? MathF.Max(1f, len / width) : 1f;
                Material.Uv1Scale = new GVec3(tile, 1f, 1f);
                Material.Uv1Offset = new GVec3(-scrollU, 0f, 0f);
            }
            else
            {
                Material.Uv1Scale = new GVec3(1f, 1f, 1f);
                Material.Uv1Offset = GVec3.Zero;
            }
        }

        /// <summary>Park the segment (hide it without freeing) — for the free-list / per-frame "unused this frame".</summary>
        public void Hide()
        {
            if (GodotObject.IsInstanceValid(Root))
                Root.Visible = false;
        }

        /// <summary>Permanently release the segment's scene-tree node.</summary>
        public void Free()
        {
            if (GodotObject.IsInstanceValid(Root))
                Root.QueueFree();
        }
    }

    // =================================================================================================
    //  Segment pool
    // =================================================================================================

    /// <summary>Return a parked, ready-to-<see cref="Segment.Update"/> segment from the free-list, creating a new
    /// one (added as a child of this node) if the pool is exhausted. Mirrors PortoTrajectoryPreview.EnsureSegments
    /// + BuildSegment, collapsed into an acquire-on-demand call.</summary>
    public Segment AcquireSegment()
    {
        if (_acquired < _segments.Count)
            return _segments[_acquired++];

        Segment s = BuildSegment();
        _segments.Add(s);
        _acquired++;
        return s;
    }

    /// <summary>Reset the acquire cursor and park every segment — call once at the top of a per-frame rebuild that
    /// uses <see cref="AcquireSegment"/>, so unused segments are hidden and reused next frame.</summary>
    public void ReleaseAll()
    {
        _acquired = 0;
        foreach (Segment s in _segments)
            s.Hide();
    }

    private Segment BuildSegment()
    {
        var root = new Node3D { Name = $"cylindricline#{_segments.Count}" };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DisableReceiveShadows = true,
        };
        // Cross-ribbon: two unit quads parked at +0.5 X so the root sits at the line START, scaled per frame to
        // (length, width) — the same Draw_CylindricLine stand-in LaserRenderer/HookRopeRenderer/PortoPreview use.
        var ribbonA = new MeshInstance3D { Mesh = SharedQuad, MaterialOverride = mat, Position = new GVec3(0.5f, 0f, 0f) };
        var ribbonB = new MeshInstance3D { Mesh = SharedQuad, MaterialOverride = mat, Position = new GVec3(0.5f, 0f, 0f) };
        ribbonB.RotationDegrees = new GVec3(90f, 0f, 0f); // cross plane
        root.AddChild(ribbonA);
        root.AddChild(ribbonB);
        AddChild(root);
        return new Segment { Root = root, RibbonA = ribbonA, RibbonB = ribbonB, Material = mat };
    }

    // =================================================================================================
    //  Static multi-point path mesh (lifted verbatim from BeamRenderer.BuildCrossRibbon)
    // =================================================================================================

    /// <summary>
    /// Build a "+"-section ribbon ArrayMesh along a multi-point path: per segment, two camera-independent quads on
    /// perpendicular side axes, so the line glows from any viewpoint (the cheap stand-in for a view-facing cylinder).
    /// UVs run [0..1] across the width (the glow gradient) and along the length. The spawn-and-free variant — one
    /// mesh for the whole path, lifted verbatim from BeamRenderer.BuildCrossRibbon.
    /// </summary>
    public static ArrayMesh BuildCrossRibbonMesh(IReadOnlyList<NVec3> pathQuake, float widthQuake)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        float half = widthQuake * 0.5f;

        for (int i = 0; i < pathQuake.Count - 1; i++)
        {
            Vector3 a = Coords.ToGodot(pathQuake[i]);
            Vector3 b = Coords.ToGodot(pathQuake[i + 1]);
            Vector3 seg = b - a;
            if (seg.LengthSquared() < 1e-6f)
                continue;
            Vector3 dir = seg.Normalized();

            // Two side axes perpendicular to the segment (and to each other) → a cross cross-section.
            Vector3 side1 = dir.Cross(Vector3.Up);
            if (side1.LengthSquared() < 1e-4f) side1 = dir.Cross(Vector3.Right);
            side1 = side1.Normalized() * half;
            Vector3 side2 = dir.Cross(side1).Normalized() * half;

            AddQuad(verts, uvs, indices, a, b, side1);
            AddQuad(verts, uvs, indices, a, b, side2);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static void AddQuad(List<Vector3> verts, List<Vector2> uvs, List<int> indices,
        Vector3 a, Vector3 b, Vector3 side)
    {
        int baseIdx = verts.Count;
        verts.Add(a - side); uvs.Add(new Vector2(0f, 0f));
        verts.Add(a + side); uvs.Add(new Vector2(1f, 0f));
        verts.Add(b + side); uvs.Add(new Vector2(1f, 1f));
        verts.Add(b - side); uvs.Add(new Vector2(0f, 1f));
        // two triangles (double-sided handled by the material's cull-disabled)
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }
}
