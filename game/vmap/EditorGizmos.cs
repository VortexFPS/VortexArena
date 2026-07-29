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

        // --- point entities: EDIT-only boxes from their class descriptors (§11.9) ---
        //
        // Both entity-ish tools draw boxes; which entities they draw is the tool's partition (backlog T2).
        if (c.Tool is EditorTool.Entity or EditorTool.Light)
            anything |= DrawEntityBoxes(c);

        // --- lights: what they REACH, which is the thing a box cannot show (§11.9, backlog T2) ---
        if (c.Tool == EditorTool.Light)
            anything |= DrawLightGizmos(c);

        // --- §11.5 overlays: vertices, and the collision volumes that render nothing ---
        if (c.ShowVertices || c.ShowCollision)
            anything |= DrawOverlays(c, doc);

        // --- patch control lattice: the grab targets, drawn as the grid they form (§11.9) ---
        if (c.Tool == EditorTool.Patch && c.Mode == ToolMode.ControlPoints)
            anything |= DrawControlLattice(c);

        // --- clip preview: the clicked points and where the plane crosses the selection (§11.9) ---
        if (c.Tool == EditorTool.Clip)
            anything |= DrawClipPreview(c, doc);

        // --- paste ghost: the clipboard outlined where a click would put it (§11.9) ---
        if (c.Mode == ToolMode.Paste && !c.Clipboard.IsEmpty && c.TryGetPastePoint(out NVec3 pasteAt))
        {
            anything |= DrawClipboardGhost(c, pasteAt);
        }

        // --- manipulator handles: drawn from the SAME list the ray test picks against, so the arrow you can
        //     see and the thing you can grab are one description rather than two that drift apart ---
        if (c.HandleList.Count > 0)
        {
            DrawHandles(c.HandleList, c.HoverHandle);
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

    /// <summary>
    /// Draw the manipulator from the built handle list. The kinds are visually distinct at a glance — straight
    /// arrows translate, curved arcs rotate, boxed stubs scale — because the mode is otherwise invisible state
    /// and acting in the wrong one is an easy, annoying mistake.
    ///
    /// <paramref name="hovered"/> is highlighted, which is what tells the mapper the next click will grab THIS
    /// axis. Under the two-phase model (§11.9) that feedback is not decoration: a click that misses every
    /// handle re-selects instead of transforming, so knowing whether you are on one is the difference between
    /// the edit you meant and starting over.
    /// </summary>
    private void DrawHandles(IReadOnlyList<EditorHandle> handles, EditorHandle? hovered)
    {
        foreach (EditorHandle h in handles)
        {
            bool active = hovered is { } hv && Same(hv, h);
            Color color = active ? AxisActive : ColorFor(h.Axis);

            switch (h.Kind)
            {
                case HandleKind.MoveAxis:
                    DrawArrow(h.Origin, h.Axis, (h.Tip - h.Origin).Length(), color);
                    break;

                case HandleKind.MovePlane:
                    DrawPad(h.Origin, h.Axis, h.Axis2, h.Radius, active ? AxisActive : PadColor);
                    break;

                case HandleKind.RotateRing:
                    DrawRing(h.Origin, h.Axis, h.Radius, color);
                    break;

                case HandleKind.ScaleUniform:
                    DrawBox(h.Tip, h.Radius, active ? AxisActive : UniformColor);
                    break;

                default:
                    DrawScaleHandle(h.Origin, h.Axis * h.Sign, (h.Tip - h.Origin).Length(), color);
                    break;
            }
        }
    }

    /// <summary>Colour of the paste ghost — distinct from the drag ghost, because it is a different promise:
    /// a drag ghost previews a move, this previews something that does not exist yet.</summary>
    private static readonly Color PasteGhostColor = new(0.6f, 0.75f, 1f, 0.85f);

    /// <summary>
    /// Draw every point entity as its descriptor box, coloured the way the definition file says.
    ///
    /// EDIT ONLY, and only with the entity tool up. Two reasons, both from §11.9: PLAYTEST shows the SERVER's
    /// live entities, so drawing the document's would double every pickup; and a level is dense with entities,
    /// so boxing them while a mapper is pushing brushes around would wallpaper the view with volumes they are
    /// not working on.
    ///
    /// Boxes the controller's occlusion sweep says are behind geometry are skipped entirely (backlog T1). The
    /// test is on the CPU and hides the WHOLE box, because the overlay material has depth testing off for the
    /// selection's sake — letting the GPU clip these would leave half-boxes poking through floors, which reads
    /// as broken geometry rather than as hidden.
    /// </summary>
    private bool DrawEntityBoxes(EditorController c)
    {
        int selectedId = 0;
        bool drew = false;

        foreach (VmapPickIndex.EntityEntry ee in c.PickIndex.Entities)
        {
            EntityClassDef def = c.Defs?.GetOrPlaceholder(ee.Entity.ClassName)
                ?? new EntityClassDef { Name = ee.Entity.ClassName };

            bool isHover = c.Hover.Hit && c.Hover.Selection.Kind == VmapSelectionKind.Entity
                           && c.Hover.Selection.EntityId == ee.Entity.Id;
            bool isSelected = c.Session is { } s
                              && s.Selection.Exists(x => x.Kind == VmapSelectionKind.Entity
                                                         && x.EntityId == ee.Entity.Id);

            // Entities the current tool does not own (backlog T2: lights belong to the Light tool and nothing
            // else). A SELECTED one still draws — carrying a selection across a tool switch and watching it
            // vanish, handles and all, would read as the editor having lost it.
            if (!isSelected && !c.ShouldBoxEntity(ee.Entity))
                continue;

            // Hover and selection always draw. Hiding what is selected would leave the manipulator handles —
            // which are drawn depth-off — floating over nothing, and the mapper unable to see what they are
            // about to transform.
            if (!isHover && !isSelected && !c.IsEntityVisible(ee.Entity.Id))
                continue;

            // The class colour is the identity — it is how a mapper tells a spawn from a weapon at a glance —
            // so hover and selection BRIGHTEN it rather than replacing it.
            var color = new Color(def.Color.X, def.Color.Y, def.Color.Z, isSelected ? 1f : 0.72f);
            if (isSelected)
                color = SelectionColor;
            else if (isHover)
                color = HoverColor;

            DrawAabb(ee.Mins, ee.Maxs, color);
            if (isSelected)
                selectedId = ee.Entity.Id;
            drew = true;
        }

        // Draw the targetname->target link of whatever is selected, which is the relationship a mapper
        // otherwise has to reconstruct by reading keys (§11.8's entity inspector arrows, in their simplest form).
        if (selectedId != 0)
            drew |= DrawEntityLinks(c, selectedId);

        return drew;
    }

    private static readonly Color LightRangeColor = new(1f, 0.85f, 0.45f, 0.5f);
    private static readonly Color LightConeColor = new(1f, 0.7f, 0.25f, 0.85f);

    /// <summary>
    /// Draw what a light DOES rather than only where it sits: the sphere it reaches, and for an aimed light
    /// the cone it actually casts (backlog T2).
    ///
    /// The radius comes from <see cref="EditorLighting.RangeForIntensity"/>, the same expression the rig
    /// builds the omni with. A ring drawn from its own formula would be a picture of a light that is not in
    /// the level, and a mapper would tune reach against it and be wrong.
    ///
    /// Only for the SELECTED or HOVERED light. A ring is hundreds of units across and a map has a hundred
    /// lights; drawing them all would wallpaper the view exactly the way the entity-box comment above warns
    /// about, and none of them would be legible.
    /// </summary>
    private bool DrawLightGizmos(EditorController c)
    {
        if (c.Document is not { } doc)
            return false;

        bool drew = false;
        foreach (VmapPickIndex.EntityEntry ee in c.PickIndex.Entities)
        {
            VmapEntity e = ee.Entity;
            if (!c.ShouldBoxEntity(e))
                continue;

            bool isHover = c.Hover.Hit && c.Hover.Selection.Kind == VmapSelectionKind.Entity
                           && c.Hover.Selection.EntityId == e.Id;
            bool isSelected = c.Session is { } s
                              && s.Selection.Exists(x => x.Kind == VmapSelectionKind.Entity
                                                         && x.EntityId == e.Id);
            if (!isHover && !isSelected)
                continue;

            NVec3 at = e.Origin();
            float range = EditorLighting.RangeForIntensity(KeyFloat(e, "light", 300f), c.LightRangeScale);
            if (range > 1f)
            {
                DrawRing(at, new NVec3(1f, 0f, 0f), range, LightRangeColor);
                DrawRing(at, new NVec3(0f, 1f, 0f), range, LightRangeColor);
                DrawRing(at, new NVec3(0f, 0f, 1f), range, LightRangeColor);
                drew = true;
            }

            // An aimed light is a q3map2 spot, and its cone is the one thing about it that cannot be read off
            // the keys — it comes from the radius AND the distance to whatever it points at.
            if (!e.Fields.TryGetValue("target", out string? target) || string.IsNullOrWhiteSpace(target))
                continue;
            if (FindTargetOrigin(doc, target) is not { } aim || aim == at)
                continue;

            float radius = MathF.Max(1f, KeyFloat(e, "radius", EditorLighting.DefaultSpotRadius));
            NVec3 axis = NVec3.Normalize(aim - at);
            DrawRing(aim, axis, radius, LightConeColor);
            (NVec3 u, NVec3 v) = Perpendiculars(axis);
            Line(at, aim + u * radius, LightConeColor);
            Line(at, aim - u * radius, LightConeColor);
            Line(at, aim + v * radius, LightConeColor);
            Line(at, aim - v * radius, LightConeColor);
            drew = true;
        }

        return drew;
    }

    private static float KeyFloat(VmapEntity e, string key, float fallback)
        => e.Fields.TryGetValue(key, out string? text)
            && float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v)
            ? v
            : fallback;

    private static NVec3? FindTargetOrigin(VmapDocument doc, string target)
    {
        foreach (VmapEntity e in doc.Entities)
            if (e.Fields.TryGetValue("targetname", out string? name)
                && string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                return e.Origin();
        return null;
    }

    /// <summary>Arrows from the selected entity to everything it targets.</summary>
    private bool DrawEntityLinks(EditorController c, int fromId)
    {
        if (c.Document?.FindEntity(fromId) is not { } from)
            return false;
        if (!from.Fields.TryGetValue("target", out string? target) || target.Length == 0)
            return false;

        bool drew = false;
        foreach (VmapEntity to in c.Document.Entities)
        {
            if (!to.Fields.TryGetValue("targetname", out string? name) || name != target)
                continue;
            Line(from.Origin(), to.Origin(), LinkColor);
            DrawCross(to.Origin(), VertexMarker * 1.5f, LinkColor);
            drew = true;
        }
        return drew;
    }

    /// <summary>Wireframe box from world-space bounds.</summary>
    private void DrawAabb(NVec3 mins, NVec3 maxs, Color color)
    {
        Span<NVec3> corners = stackalloc NVec3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new NVec3(
                (i & 1) == 0 ? mins.X : maxs.X,
                (i & 2) == 0 ? mins.Y : maxs.Y,
                (i & 4) == 0 ? mins.Z : maxs.Z);

        // 0-1,2-3,4-5,6-7 along X; 0-2,1-3,4-6,5-7 along Y; 0-4,1-5,2-6,3-7 along Z.
        for (int i = 0; i < 8; i++)
        {
            if ((i & 1) == 0) Line(corners[i], corners[i | 1], color);
            if ((i & 2) == 0) Line(corners[i], corners[i | 2], color);
            if ((i & 4) == 0) Line(corners[i], corners[i | 4], color);
        }
    }

    private static readonly Color LinkColor = new(1f, 0.55f, 0.9f, 0.9f);

    private static readonly Color VertexOverlayColor = new(1f, 0.95f, 0.5f, 0.85f);
    private static readonly Color CollisionOverlayColor = new(1f, 0.35f, 0.45f, 0.7f);

    /// <summary>
    /// The §11.5 overlays.
    ///
    /// VERTICES marks every brush corner, which is what turns "these two walls look joined" into a checkable
    /// claim. COLLISION draws the volumes that render NOTHING — playerclip, trigger bounds, the caulk shell —
    /// and that is the one that earns its place: divergence between what you can see and what you can walk
    /// into is invisible by definition until something draws it.
    ///
    /// Both are RANGE-LIMITED around the viewer. A real map is thousands of brushes and marking every corner
    /// of all of them is unreadable as well as expensive.
    /// </summary>
    private bool DrawOverlays(EditorController c, VmapDocument doc)
    {
        NVec3 centre = c.OverlayCenter;
        float range = c.OverlayRange;
        float rangeSq = range * range;
        bool drew = false;

        foreach (VmapBrush b in doc.Brushes)
        {
            if (!VmapWinding.TryGetBounds(b, out NVec3 mins, out NVec3 maxs))
                continue;

            // Cheap reject on the box centre before deriving any windings.
            NVec3 mid = (mins + maxs) * 0.5f;
            if ((mid - centre).LengthSquared() > rangeSq)
                continue;

            bool invisible = b.IsToolBrush;
            if (c.ShowCollision && invisible)
            {
                foreach (NVec3[] w in VmapWinding.BuildBrushWindings(b))
                {
                    if (w.Length < 3)
                        continue;
                    DrawLoop(ToGodot(w, NVec3.Zero), CollisionOverlayColor);
                    drew = true;
                }
            }

            if (c.ShowVertices && !invisible)
            {
                foreach (NVec3 v in VmapWinding.BrushPoints(b))
                {
                    DrawCross(v, VertexMarker, VertexOverlayColor);
                    drew = true;
                }
            }
        }

        return drew;
    }

    private static readonly Color LatticeColor = new(0.55f, 0.75f, 0.95f, 0.8f);
    private static readonly Color ControlColor = new(1f, 0.9f, 0.4f, 1f);

    /// <summary>
    /// Draw the control grid of the selected patches: the lattice lines plus a marker at each control point.
    ///
    /// The lattice matters as much as the points. A patch’s control points sit OFF the surface — that is what
    /// makes it curve — so a bare scatter of markers gives no clue which point bends which part of it. The grid
    /// lines are what turn them back into a shape you can reason about.
    /// </summary>
    private bool DrawControlLattice(EditorController c)
    {
        if (c.Document is not { } doc)
            return false;

        bool drew = false;
        foreach (int patchId in c.SelectedPatchIds())
        {
            if (doc.FindPatch(patchId) is not { } p || !p.IsValid)
                continue;

            for (int row = 0; row < p.Height; row++)
                for (int col = 0; col < p.Width; col++)
                {
                    int i = row * p.Width + col;
                    if (col + 1 < p.Width)
                        Line(p.Controls[i], p.Controls[i + 1], LatticeColor);
                    if (row + 1 < p.Height)
                        Line(p.Controls[i], p.Controls[i + p.Width], LatticeColor);
                }
            drew = true;
        }

        // The grab targets themselves, sized exactly as the ray test sees them.
        foreach (EditorHandle h in c.ControlHandles)
            DrawBox(h.Tip, h.Radius, ControlColor);

        return drew;
    }

    private static readonly Color ClipPointColor = new(1f, 0.45f, 0.35f, 1f);
    private static readonly Color ClipPlaneColor = new(1f, 0.6f, 0.25f, 0.95f);

    /// <summary>
    /// Draw the pending cut: the clicked points, and the polygon where the plane actually crosses each selected
    /// brush.
    ///
    /// Showing the real cross-section rather than an abstract plane is the point. A clip is committed blind
    /// otherwise — the mapper has no way to tell whether the plane grazes a corner or slices cleanly until
    /// after they have applied it and are looking at the result.
    /// </summary>
    private bool DrawClipPreview(EditorController c, VmapDocument doc)
    {
        bool drew = false;

        foreach (NVec3 p in c.ClipPoints)
        {
            DrawCross(p, VertexMarker * 2f, ClipPointColor);
            drew = true;
        }

        if (!c.TryGetClipPlane(out VmapPlane plane) || c.Session is not { } session)
            return drew;

        foreach (int id in session.SelectedBrushIds())
        {
            if (doc.FindBrush(id) is not { } brush)
                continue;

            // The cross-section is the brush's own winding for the cut plane: start from the plane's base
            // polygon and chop it against every face, which is exactly how a brush face is derived.
            List<NVec3>? section = VmapWinding.BaseWindingForPlane(plane);
            if (section is null)
                continue;

            foreach (VmapFace f in brush.Faces)
            {
                section = VmapWinding.ChopWinding(section, f.Plane);
                if (section is null || section.Count < 3)
                    break;
            }
            if (section is null || section.Count < 3)
                continue;

            for (int i = 0; i < section.Count; i++)
                Line(section[i], section[(i + 1) % section.Count], ClipPlaneColor);
            drew = true;
        }

        return drew;
    }

    /// <summary>
    /// Outline the clipboard where a click would place it. Drawn from the CLIPBOARD's own geometry, offset by
    /// the pivot, rather than from anything in the document — the source brushes may have been deleted, or the
    /// clipboard may have come from a different map entirely.
    /// </summary>
    private bool DrawClipboardGhost(EditorController c, NVec3 at)
    {
        NVec3 offset = at - c.Clipboard.Pivot;
        bool drew = false;

        foreach (VmapBrush b in c.Clipboard.Brushes)
        {
            foreach (NVec3[] winding in VmapWinding.BuildBrushWindings(b))
            {
                if (winding.Length < 3)
                    continue;
                DrawLoop(ToGodot(winding, offset), PasteGhostColor);
                drew = true;
            }
        }

        foreach (VmapPatch p in c.Clipboard.Patches)
        {
            // The control lattice rather than a tessellation: it is cheap, it reads clearly as "a curve goes
            // here", and it does not need the pick index (which only knows about the live document).
            for (int row = 0; row < p.Height; row++)
                for (int col = 0; col < p.Width; col++)
                {
                    int i = row * p.Width + col;
                    if (col + 1 < p.Width)
                        Line(p.Controls[i] + offset, p.Controls[i + 1] + offset, PasteGhostColor);
                    if (row + 1 < p.Height)
                        Line(p.Controls[i] + offset, p.Controls[i + p.Width] + offset, PasteGhostColor);
                    drew = true;
                }
        }

        // Point entities have no geometry, so mark them with a box at their pasted origin.
        foreach (VmapEntity e in c.Clipboard.Entities)
        {
            if (e.IsBrushEntity)
                continue;
            DrawBox(e.Origin() + offset, 8f, PasteGhostColor);
            drew = true;
        }

        // A cross at the landing point, so the exact placement is legible even when the ghost is large.
        DrawCross(at, VertexMarker * 2f, PasteGhostColor);
        return drew || true;
    }

    /// <summary>Identity for highlighting: same kind, same axis, same side.</summary>
    private static bool Same(EditorHandle a, EditorHandle b)
        => a.Kind == b.Kind && a.Axis == b.Axis && a.Axis2 == b.Axis2 && a.Sign == b.Sign;

    private static Color ColorFor(NVec3 axis)
    {
        if (MathF.Abs(axis.X) > 0.5f && MathF.Abs(axis.Y) < 0.5f && MathF.Abs(axis.Z) < 0.5f) return AxisX;
        if (MathF.Abs(axis.Y) > 0.5f && MathF.Abs(axis.Z) < 0.5f) return AxisY;
        if (MathF.Abs(axis.Z) > 0.5f) return AxisZ;
        return UniformColor;
    }

    private static readonly Color PadColor = new(0.9f, 0.85f, 0.4f, 0.75f);
    private static readonly Color UniformColor = new(0.9f, 0.9f, 0.95f, 1f);

    /// <summary>A small square in the plane of two axes — the two-axis move pad.</summary>
    private void DrawPad(NVec3 centre, NVec3 u, NVec3 v, float half, Color color)
    {
        NVec3 a = centre + (u + v) * half;
        NVec3 b = centre + (u - v) * half;
        NVec3 c = centre - (u + v) * half;
        NVec3 d = centre - (u - v) * half;
        Line(a, b, color);
        Line(b, c, color);
        Line(c, d, color);
        Line(d, a, color);
    }

    /// <summary>
    /// A FULL circle about the axis. Full rather than the quarter-arc the indicator used, because the ring is
    /// now a click target and a mapper cannot be expected to find the one quadrant that happens to be live.
    /// </summary>
    private void DrawRing(NVec3 origin, NVec3 axis, float radius, Color color)
    {
        (NVec3 u, NVec3 v) = Perpendiculars(axis);
        const int segments = 40;
        NVec3 prev = origin + u * radius;
        for (int i = 1; i <= segments; i++)
        {
            float t = i / (float)segments * MathF.Tau;
            NVec3 p = origin + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
            Line(prev, p, color);
            prev = p;
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
