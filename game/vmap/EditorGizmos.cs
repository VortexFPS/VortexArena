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
    private float _builtWireAlpha = -1f;
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

        float wireAlphaNow = _controller.Ortho?.WireAlpha ?? 1f;
        if (_wireframeVisible
            && (_builtGeometryVersion != _controller.GeometryVersion
                || MathF.Abs(_builtWireAlpha - wireAlphaNow) > 0.001f))
        {
            RebuildWorldWireframe();
            _builtGeometryVersion = _controller.GeometryVersion;
            _builtWireAlpha = wireAlphaNow;
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
        _fills.Clear();

        bool anything = false;

        // --- selection ---
        if (c.Session is { } session)
        {
            foreach (VmapSelection sel in session.Selection)
                anything |= DrawSelection(doc, sel, SelectionColor, NVec3.Zero, fillAlpha: 0.16f);
        }

        // --- hover (skipped mid-drag: the ghost already says what is moving) ---
        if (!c.IsDragging && c.Hover.Hit)
            anything |= DrawSelection(doc, c.Hover.Selection, HoverColor, NVec3.Zero, fillAlpha: 0.10f);

        // --- drag ghost: the same outline, offset by the pending delta ---
        if (c.IsDragging && c.DragDelta != NVec3.Zero)
        {
            anything |= DrawSelection(doc, c.DragSelection, GhostColor, c.DragDelta, fillAlpha: 0.24f);

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

        // --- manipulator handles: axis arrows / rotation arcs / scale boxes at the selection ---
        if (c.TryGetManipulatorOrigin(out NVec3 manipOrigin))
        {
            DrawManipulator(manipOrigin, c.Manipulator, c.DragAxis);
            anything = true;
        }

        // Only open a surface when there is something to put in it: ImmediateMesh errors on SurfaceEnd with no
        // vertices, and an idle editor (nothing hovered, nothing selected) is the common case.
        _overlayMesh.ClearSurfaces();
        _ = anything;

        // Fills first, outlines second: the translucent face wash reads as "this is the thing", and drawing the
        // crisp outline over it keeps the exact boundary legible against busy level art.
        if (_fills.Count > 0)
        {
            _overlayMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            foreach ((Vector3 fa, Vector3 fb, Vector3 fc, Color color) in _fills)
            {
                _overlayMesh.SurfaceSetColor(color);
                _overlayMesh.SurfaceAddVertex(fa);
                _overlayMesh.SurfaceSetColor(color);
                _overlayMesh.SurfaceAddVertex(fb);
                _overlayMesh.SurfaceSetColor(color);
                _overlayMesh.SurfaceAddVertex(fc);
            }
            _overlayMesh.SurfaceEnd();
        }

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
    }

    // Axis colours follow the universal convention (X red, Y green, Z blue) so the handles are readable
    // without a legend.
    private static readonly Color AxisX = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Color AxisY = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Color AxisZ = new(0.45f, 0.6f, 1f, 1f);
    private static readonly Color AxisActive = new(1f, 0.95f, 0.4f, 1f);

    /// <summary>Handle length in world units. Fixed rather than screen-constant for now — see the note below.</summary>
    private const float HandleLength = 48f;

    /// <summary>
    /// Draw the manipulator at <paramref name="origin"/>. The three modes are visually distinct at a glance —
    /// straight arrows translate, curved arcs rotate, boxed stubs scale — because the mode is otherwise
    /// invisible state and acting in the wrong one is an easy, annoying mistake.
    ///
    /// <paramref name="activeAxis"/> (the constrained axis of a live drag) is highlighted, so the handle shows
    /// which way the current edit is actually moving.
    ///
    /// NOTE: the handles are currently an INDICATOR — they are not yet click targets. Ray-vs-handle picking
    /// (so grabbing an arrow beats grabbing the face behind it) is the remaining piece; dragging still goes
    /// through face/vertex picking.
    /// </summary>
    private void DrawManipulator(NVec3 origin, ManipulatorMode mode, NVec3 activeAxis)
    {
        Span<NVec3> axes = stackalloc NVec3[3];
        axes[0] = new NVec3(1f, 0f, 0f);
        axes[1] = new NVec3(0f, 1f, 0f);
        axes[2] = new NVec3(0f, 0f, 1f);
        ReadOnlySpan<Color> colors = stackalloc Color[3] { AxisX, AxisY, AxisZ };

        for (int i = 0; i < 3; i++)
        {
            NVec3 axis = axes[i];
            // Highlight whichever axis the live drag is constrained to.
            bool active = activeAxis != NVec3.Zero && MathF.Abs(NVec3.Dot(activeAxis, axis)) > 0.9f;
            Color color = active ? AxisActive : colors[i];

            switch (mode)
            {
                case ManipulatorMode.Translate:
                    DrawArrow(origin, axis, HandleLength, color);
                    break;
                case ManipulatorMode.Rotate:
                    DrawArc(origin, axis, HandleLength, color);
                    break;
                default:
                    DrawScaleHandle(origin, axis, HandleLength, color);
                    break;
            }
        }
    }

    /// <summary>A shaft with a four-line arrowhead — the translate handle.</summary>
    private void DrawArrow(NVec3 origin, NVec3 axis, float length, Color color)
    {
        NVec3 tip = origin + axis * length;
        Line(origin, tip, color);

        // Arrowhead: four barbs angled back from the tip, in the two axes perpendicular to the shaft.
        (NVec3 u, NVec3 v) = Perpendiculars(axis);
        float head = length * 0.22f;
        NVec3 baseP = tip - axis * head;
        Line(tip, baseP + u * (head * 0.5f), color);
        Line(tip, baseP - u * (head * 0.5f), color);
        Line(tip, baseP + v * (head * 0.5f), color);
        Line(tip, baseP - v * (head * 0.5f), color);
    }

    /// <summary>A quarter-circle arc about the axis — the rotate handle.</summary>
    private void DrawArc(NVec3 origin, NVec3 axis, float radius, Color color)
    {
        (NVec3 u, NVec3 v) = Perpendiculars(axis);
        const int segments = 16;
        NVec3 prev = origin + u * radius;
        for (int i = 1; i <= segments; i++)
        {
            // A quarter turn is enough to read as "rotation about this axis" without three full rings
            // overlapping into an unreadable ball.
            float t = i / (float)segments * (MathF.PI * 0.5f);
            NVec3 p = origin + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
            Line(prev, p, color);
            prev = p;
        }

        // A small barb at the end so the arc reads as an arrow rather than a plain curve.
        NVec3 endDir = NVec3.Normalize(prev - origin);
        NVec3 tangent = NVec3.Cross(axis, endDir);
        Line(prev, prev - tangent * (radius * 0.16f) + endDir * (radius * 0.10f), color);
        Line(prev, prev - tangent * (radius * 0.16f) - endDir * (radius * 0.10f), color);
    }

    /// <summary>A stub ending in a small cube — the scale handle.</summary>
    private void DrawScaleHandle(NVec3 origin, NVec3 axis, float length, Color color)
    {
        NVec3 tip = origin + axis * length;
        Line(origin, tip, color);
        DrawBox(tip, length * 0.09f, color);
    }

    /// <summary>Wireframe cube centred on a point.</summary>
    private void DrawBox(NVec3 center, float half, Color color)
    {
        Vector3 c = Coords.ToGodot(center);
        for (int i = 0; i < 4; i++)
        {
            // Two opposite faces plus the four connecting edges.
            float sx = (i is 0 or 3) ? half : -half;
            float sy = (i is 0 or 1) ? half : -half;
            float nx = (i is 1 or 0) ? half : -half;
            float ny = (i is 2 or 1) ? half : -half;

            Line(c + new Vector3(sx, sy, half), c + new Vector3(nx, ny, half), color);
            Line(c + new Vector3(sx, sy, -half), c + new Vector3(nx, ny, -half), color);
            Line(c + new Vector3(sx, sy, half), c + new Vector3(sx, sy, -half), color);
        }
    }

    /// <summary>Two unit vectors perpendicular to <paramref name="axis"/> and to each other.</summary>
    private static (NVec3 U, NVec3 V) Perpendiculars(NVec3 axis)
    {
        NVec3 seed = MathF.Abs(axis.Z) < 0.9f ? new NVec3(0f, 0f, 1f) : new NVec3(1f, 0f, 0f);
        NVec3 u = NVec3.Normalize(NVec3.Cross(seed, axis));
        return (u, NVec3.Cross(axis, u));
    }

    /// <summary>Line segments accumulated this frame, emitted in one surface at the end of the rebuild.</summary>
    private readonly List<(Vector3 A, Vector3 B, Color Color)> _segments = new();

    /// <summary>Translucent fill triangles accumulated this frame.</summary>
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Color Color)> _fills = new();

    /// <summary>Fan-fill a convex polygon with a translucent wash.</summary>
    private void FillPolygon(Vector3[] points, Color color)
    {
        for (int i = 1; i + 1 < points.Length; i++)
            _fills.Add((points[0], points[i], points[i + 1], color));
    }

    /// <summary>Outline one selection, optionally displaced (for the drag ghost). Returns true if it drew.</summary>
    private bool DrawSelection(VmapDocument doc, VmapSelection sel, Color color, NVec3 offset, float fillAlpha = 0f)
    {
        EditorController c0 = _controller!;
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
                FillPolygon(w, new Color(color.R, color.G, color.B, fillAlpha));
                return w.Length >= 3;
            }

            case VmapSelectionKind.Patch:
            {
                // Outline the tessellation. A patch has no winding to trace, so the visible boundary IS its
                // triangles — drawn as a light mesh so a curved surface reads as selected without hiding it.
                foreach (VmapPickIndex.Entry _ in Array.Empty<VmapPickIndex.Entry>()) { }
                bool drewPatch = false;
                foreach (VmapPickIndex.PatchEntry pe in c0.PickIndex.Patches)
                {
                    if (pe.Patch.Id != sel.PatchId)
                        continue;
                    for (int i = 0; i + 2 < pe.Triangles.Length; i += 3)
                    {
                        Vector3 a0 = Coords.ToGodot(pe.Triangles[i] + offset);
                        Vector3 b0 = Coords.ToGodot(pe.Triangles[i + 1] + offset);
                        Vector3 c1 = Coords.ToGodot(pe.Triangles[i + 2] + offset);
                        Line(a0, b0, color);
                        Line(b0, c1, color);
                        Line(c1, a0, color);
                        if (fillAlpha > 0f)
                            _fills.Add((a0, b0, c1, new Color(color.R, color.G, color.B, fillAlpha * 0.5f)));
                    }
                    drewPatch = true;
                    break;
                }
                return drewPatch;
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
                    FillPolygon(w, new Color(color.R, color.G, color.B, fillAlpha * 0.6f));
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
        var verts = new List<Vector3>(4096);

        // Reuse the picking cache's windings: they are already built for this geometry version, and deriving
        // them a second time here is what used to make opening the ortho view stall on a large map.
        _controller!.PickIndex.EnsureBuilt(_controller.Document!, _controller.GeometryVersion);

        foreach (VmapPickIndex.Entry entry in _controller.PickIndex.Entries)
        {
            for (int fi = 0; fi < entry.Windings.Length; fi++)
            {
                // Only outline surfaces that actually render. A wireframe of every brush side — including the
                // caulk and noshader planes buried inside walls — is an unreadable thicket, and it is exactly
                // the geometry the mapper said gets in the way.
                if (fi < entry.Brush.Faces.Count
                    && ((entry.Brush.Faces[fi].SurfaceFlags & VmapGeometryBuilder.SurfaceNoDraw) != 0
                        || VmapBrush.IsToolMaterial(entry.Brush.Faces[fi].Material)))
                    continue;

                NVec3[] winding = entry.Windings[fi];
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

        // Edge lines can obscure the art underneath, so their opacity is a user-cycled setting.
        float wireAlpha = _controller?.Ortho?.WireAlpha ?? 1f;
        var tinted = new Color(WireColor.R, WireColor.G, WireColor.B, WireColor.A * wireAlpha);
        var colors = new Color[verts.Count];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = tinted;

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

    /// <summary>
    /// Quake-space line. Buffers like its Godot-space twin — writing to the ImmediateMesh directly here is
    /// what produced thousands of "Not creating any surface" errors a frame once the manipulator handles
    /// started using this overload: the buffering rework converted only the other one.
    /// </summary>
    private void Line(NVec3 a, NVec3 b, Color color)
        => _segments.Add((Coords.ToGodot(a), Coords.ToGodot(b), color));

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
