using Godot;
using XonoticGodot.Formats.Vmap;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// Draws the editor's overlay geometry (design doc §11.4): the hover highlight, the selection outline, the
/// ghost preview of an in-flight drag, the snap hint, and the wireframe the orthographic view is made of.
///
/// Everything is line geometry rebuilt into one <see cref="ImmediateMesh"/>. Lines rather than shaded
/// handles because an editor overlay has to be legible against arbitrary level art, and because a line mesh
/// costs one draw call for the whole overlay. The material is unshaded with depth testing OFF, so a selection
/// stays visible through the wall in front of it — the standard editor behaviour, and the reason you can grab
/// a face you know is there without first flying around to see it.
///
/// The world wireframe is expensive to build (every brush's windings) so it is cached and only rebuilt when
/// <see cref="EditorController.GeometryVersion"/> changes, i.e. after an actual edit rather than every frame.
/// </summary>
public sealed partial class EditorGizmos : Node3D
{
    private static readonly Color HoverColor = new(1f, 0.85f, 0.3f, 0.9f);
    private static readonly Color SelectionColor = new(0.35f, 0.9f, 1f, 0.95f);
    private static readonly Color GhostColor = new(0.4f, 1f, 0.55f, 0.85f);
    private static readonly Color SnapColor = new(1f, 0.4f, 0.85f, 1f);
    private static readonly Color WireColor = new(0.55f, 0.65f, 0.75f, 0.5f);

    /// <summary>Half-size of the cross drawn at a picked vertex, in world units.</summary>
    private const float VertexMarker = 4f;

    private EditorController? _controller;
    private MeshInstance3D _overlay = null!;
    private MeshInstance3D _wireframe = null!;
    private ImmediateMesh _overlayMesh = null!;
    private int _builtGeometryVersion = -1;
    private bool _wireframeVisible;

    /// <summary>Show the full world wireframe (the orthographic view's geometry, and a 3D debug overlay).</summary>
    public bool ShowWorldWireframe
    {
        get => _wireframeVisible;
        set
        {
            _wireframeVisible = value;
            if (_wireframe is not null)
                _wireframe.Visible = value;
        }
    }

    public void Attach(EditorController controller) => _controller = controller;

    public override void _Ready()
    {
        Name = "EditorGizmos";

        _overlayMesh = new ImmediateMesh();
        _overlay = new MeshInstance3D
        {
            Name = "Overlay",
            Mesh = _overlayMesh,
            MaterialOverride = LineMaterial(depthTest: false),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // The overlay spans wherever the selection is; a generous AABB keeps it from being culled when the
            // camera looks away from the mesh's own origin.
            CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
        };
        AddChild(_overlay);

        _wireframe = new MeshInstance3D
        {
            Name = "Wireframe",
            MaterialOverride = LineMaterial(depthTest: true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false,
            CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
        };
        AddChild(_wireframe);
    }

    public override void _Process(double delta)
    {
        using var _scope = Client.FrameProfiler.Scope("editor.gizmos");

        if (_controller is null || !_controller.Active || _controller.Document is null)
        {
            _overlay.Visible = false;
            _wireframe.Visible = false;
            return;
        }

        _overlay.Visible = true;
        _wireframe.Visible = _wireframeVisible;

        RebuildOverlay();

        if (_wireframeVisible && _builtGeometryVersion != _controller.GeometryVersion)
        {
            RebuildWorldWireframe();
            _builtGeometryVersion = _controller.GeometryVersion;
        }
    }

    // =============================================================================================
    //  Per-frame overlay
    // =============================================================================================

    private void RebuildOverlay()
    {
        EditorController c = _controller!;
        VmapDocument doc = c.Document!;
        _segments.Clear();

        bool anything = false;

        // --- selection ---
        if (c.Session is { } session)
        {
            foreach (VmapSelection sel in session.Selection)
                anything |= DrawSelection(doc, sel, SelectionColor, NVec3.Zero);
        }

        // --- hover (skipped mid-drag: the ghost already says what is moving) ---
        if (!c.IsDragging && c.Hover.Hit)
            anything |= DrawSelection(doc, c.Hover.Selection, HoverColor, NVec3.Zero);

        // --- drag ghost: the same outline, offset by the pending delta ---
        if (c.IsDragging && c.DragDelta != NVec3.Zero)
        {
            anything |= DrawSelection(doc, c.DragSelection, GhostColor, c.DragDelta);

            // --- snap hint: mark the feature the drag latched onto ---
            VmapPicking.SnapResult snap = c.DragSnap;
            if (snap.Snapped && snap.TargetPoints is { Count: > 0 })
            {
                if (snap.TargetPoints.Count >= 2)
                    Line(snap.TargetPoints[0], snap.TargetPoints[1], SnapColor);
                DrawCross(snap.Position, VertexMarker * 1.5f, SnapColor);
                anything = true;
            }
        }

        // Only open a surface when there is something to put in it: ImmediateMesh errors on SurfaceEnd with no
        // vertices, and an idle editor (nothing hovered, nothing selected) is the common case.
        _overlayMesh.ClearSurfaces();
        if (_segments.Count == 0)
            return;

        _overlayMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach ((Vector3 a, Vector3 b, Color color) in _segments)
        {
            _overlayMesh.SurfaceSetColor(color);
            _overlayMesh.SurfaceAddVertex(a);
            _overlayMesh.SurfaceSetColor(color);
            _overlayMesh.SurfaceAddVertex(b);
        }
        _overlayMesh.SurfaceEnd();
        _ = anything;
    }

    /// <summary>Line segments accumulated this frame, emitted in one surface at the end of the rebuild.</summary>
    private readonly List<(Vector3 A, Vector3 B, Color Color)> _segments = new();

    /// <summary>Outline one selection, optionally displaced (for the drag ghost). Returns true if it drew.</summary>
    private bool DrawSelection(VmapDocument doc, VmapSelection sel, Color color, NVec3 offset)
    {
        if (sel.IsEmpty || doc.FindBrush(sel.BrushId) is not { } brush)
            return false;

        switch (sel.Kind)
        {
            case VmapSelectionKind.Vertex:
                foreach (NVec3 v in sel.Vertices)
                    DrawCross(v + offset, VertexMarker, color);
                return sel.Vertices.Count > 0;

            case VmapSelectionKind.Edge:
                if (sel.Vertices.Count < 2)
                    return false;
                Line(sel.Vertices[0] + offset, sel.Vertices[1] + offset, color);
                DrawCross(sel.Vertices[0] + offset, VertexMarker, color);
                DrawCross(sel.Vertices[1] + offset, VertexMarker, color);
                return true;

            case VmapSelectionKind.Face:
            {
                if (sel.FaceIndex < 0 || sel.FaceIndex >= brush.Faces.Count)
                    return false;
                Vector3[] w = ToGodot(VmapWinding.BuildFaceWinding(brush, sel.FaceIndex), offset);
                DrawLoop(w, color);
                return w.Length >= 3;
            }

            case VmapSelectionKind.Brush:
            {
                NVec3[][] windings = VmapWinding.BuildBrushWindings(brush);
                bool drew = false;
                for (int i = 0; i < windings.Length; i++)
                {
                    Vector3[] w = ToGodot(windings[i], offset);
                    if (w.Length < 3)
                        continue;
                    DrawLoop(w, color);
                    drew = true;
                }
                return drew;
            }

            default:
                return false;
        }
    }

    // =============================================================================================
    //  World wireframe (the ortho view's geometry)
    // =============================================================================================

    /// <summary>
    /// Build a line mesh of every brush's face windings. This is TRUTH geometry, not the render mesh, which is
    /// what makes the orthographic view's lines exact rather than an artifact of tessellation and merging.
    /// </summary>
    private void RebuildWorldWireframe()
    {
        VmapDocument doc = _controller!.Document!;
        var verts = new List<Vector3>(4096);

        foreach (VmapBrush brush in doc.Brushes)
        {
            foreach (NVec3[] winding in VmapWinding.BuildBrushWindings(brush))
            {
                if (winding.Length < 3)
                    continue;
                for (int i = 0; i < winding.Length; i++)
                {
                    verts.Add(Coords.ToGodot(winding[i]));
                    verts.Add(Coords.ToGodot(winding[(i + 1) % winding.Length]));
                }
            }
        }

        if (verts.Count == 0)
        {
            _wireframe.Mesh = null;
            return;
        }

        var colors = new Color[verts.Count];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = WireColor;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        _wireframe.Mesh = mesh;
    }

    // =============================================================================================
    //  Primitives
    // =============================================================================================

    private void Line(NVec3 a, NVec3 b, Color color)
    {
        _overlayMesh.SurfaceSetColor(color);
        _overlayMesh.SurfaceAddVertex(Coords.ToGodot(a));
        _overlayMesh.SurfaceSetColor(color);
        _overlayMesh.SurfaceAddVertex(Coords.ToGodot(b));
    }

    private void Line(Vector3 a, Vector3 b, Color color) => _segments.Add((a, b, color));

    private void DrawLoop(Vector3[] points, Color color)
    {
        if (points.Length < 2)
            return;
        for (int i = 0; i < points.Length; i++)
            Line(points[i], points[(i + 1) % points.Length], color);
    }

    /// <summary>A three-axis cross marking a point — readable from any angle, unlike a flat square.</summary>
    private void DrawCross(NVec3 p, float size, Color color)
    {
        Vector3 g = Coords.ToGodot(p);
        Line(g - new Vector3(size, 0, 0), g + new Vector3(size, 0, 0), color);
        Line(g - new Vector3(0, size, 0), g + new Vector3(0, size, 0), color);
        Line(g - new Vector3(0, 0, size), g + new Vector3(0, 0, size), color);
    }

    private static Vector3[] ToGodot(NVec3[] winding, NVec3 offset)
    {
        var result = new Vector3[winding.Length];
        for (int i = 0; i < winding.Length; i++)
            result[i] = Coords.ToGodot(winding[i] + offset);
        return result;
    }

    /// <summary>
    /// Unshaded vertex-coloured line material. <paramref name="depthTest"/> off for the interaction overlay
    /// (so a selection reads through the geometry in front of it) and on for the world wireframe (where
    /// see-through lines would just be visual noise).
    /// </summary>
    private static StandardMaterial3D LineMaterial(bool depthTest) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = !depthTest,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableFog = true,
    };
}
