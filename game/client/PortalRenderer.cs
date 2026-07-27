using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;  // WarpzoneTrace, WarpzoneManager, Warpzone, WarpzoneTransform
using XonoticGodot.Common.Services;  // Api
using NVec3 = System.Numerics.Vector3;
// Coords (Quake<->Godot axis swap) lives in the parent XonoticGodot.Game namespace — reachable implicitly.

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side warpzone PORTAL render — the C# stand-in for DarkPlaces' engine <c>r_water</c> portal pass
/// (Base renders <c>dpcamera</c> surfaces with a <c>setcamera_transform</c> callback + a true oblique clip at
/// the exit plane; Godot has neither, so we use a <see cref="SubViewport"/> per portal window).
///
/// <para><b>Technique: window-anchored PERSPECTIVE camera + projective UV sampling.</b> Per frame the portal
/// camera sits at the WARP-TRANSFORMED main-camera eye (correct parallax) with its orientation FIXED along the
/// exit plane's normal, and a symmetric perspective cone sized to cover the window:</para>
/// <list type="bullet">
///   <item>because the view axis IS the plane normal, <c>near = planeDist + 0.5</c> clips the plane-coincident
///   wall face (warpzone surfaces are painted on solid walls) and everything nearer — the oblique-equivalent
///   clip, without <c>Camera3D.SetFrustum</c>: Godot's FRUSTUM projection never rendered small-AABB instances
///   (items/players) here and its light culler rejected the matrices outright ("prepare_camera" spam);</item>
///   <item>the window is an off-center subrect of the image — the quad maps each fragment's in-plane position
///   through the exit projection (<c>wz_off/wz_dist/wz_tan</c> uniforms), no screen-UV coupling at all;</item>
///   <item>the render target stays SQUARE and only re-sizes on the (rare) adaptive-resolution bucket change:
///   frequent SubViewport resizes while its texture is bound in a live material corrupt the renderer
///   ("uninitialized RID" storm → BLIT_PASS segfault, observed live).</item>
/// </list>
///
/// <para>Windows come from <see cref="MapLoader.BuildPortalSurfaces"/> (only true <c>dpcamera</c> shaders; the
/// pocket decor — backdrop/rims — is layered off portal cameras, and all portal windows live on
/// <see cref="PortalSurfaceLayerBit"/> which portal cameras exclude: no portal-in-portal feedback, a distant
/// portal seen through a portal shows its authored backdrop, DP's no-recursion fallback). Each window matches a
/// linked <see cref="Warpzone"/> in <see cref="WarpzoneTrace.AmbientManager"/> by plane coincidence.</para>
///
/// <para><b>Listen-host only</b> (the zone transforms live in <see cref="WarpzoneTrace.AmbientManager"/>, which
/// a pure remote/dedicated-server client does not have — networking them is a follow-up). When the manager is
/// absent, or a surface matches no zone, the surface keeps the dark-mirror placeholder (no regression). Gated by
/// <c>cl_portal_render</c> (default 1). A window is only re-rendered while its surface is on screen
/// (<see cref="VisibleOnScreenNotifier3D"/>) AND the main camera is on the FRONT side of its plane.</para>
///
/// <para><b>Debug</b>: <c>sv_warpzone_trace 1</c> logs a 1Hz gate/pose line per portal; <c>wz_portal_dump 1</c>
/// additionally saves each portal texture + the main view to <c>screenshots/portal-debug/</c>;
/// <c>wz_portal_force 1</c> bypasses the visibility gates for scripted runs.</para>
/// </summary>
public partial class PortalRenderer : Node3D
{
    private const int MaxPortals = 6; // cap the per-frame extra scene renders

    // Cached shader-param names — hoisted out of the per-frame portal drive so the implicit string→StringName
    // conversion (godot#105750, analyzer XG0002) doesn't allocate a StringName every frame per portal.
    private static readonly StringName ParamPortalTex = "portal_tex";
    private static readonly StringName ParamWzOff = "wz_off";
    private static readonly StringName ParamWzDist = "wz_dist";
    private static readonly StringName ParamWzTan = "wz_tan";

    /// <summary>Render layer 20 — portal window quads live here (in ADDITION to layer 1) so portal cameras can
    /// exclude them (no portal-in-portal feedback). The pocket DECOR nodes join the same layer. The main
    /// camera's default cull mask includes layer 20, so everything stays visible to the player.</summary>
    private const uint PortalSurfaceLayerBit = 1u << 19;

    /// <summary>The ACTIVE portals' exit-side viewpoints (Quake) — one point 8qu in front of each rendering
    /// portal's exit window. The PVS cullers (<see cref="WorldPvsCuller"/> / ClientWorld's entity cull) UNION
    /// these with the main camera's cluster: they hide nodes via <c>Visible=false</c>, which applies to EVERY
    /// viewport sharing the World3D — without the union the exit room's cells are hidden for the portal camera
    /// (the main camera can't see them) and the portal renders black. Anything visible THROUGH the window is
    /// covered by the window-point cluster (every through-window sightline crosses the window plane).
    /// Rebuilt every frame by the live renderer; empty when no portal is rendering (menus, remote clients).</summary>
    public static readonly List<NVec3> ActiveExitViewsQuake = new();

    private sealed class Portal
    {
        public MeshInstance3D Surface = null!;
        public WarpzoneTransform Transform;
        public SubViewport Viewport = null!;
        public Camera3D Cam = null!;
        public ShaderMaterial Material = null!;
        public VisibleOnScreenNotifier3D Notifier = null!;
        public NVec3 InOriginQ, InForwardQ;   // the matched zone's IN plane (Quake) — the facing gate
        public NVec3 ExitCenterQ;             // the window center mapped onto the EXIT plane (Quake)
        public NVec3 OutFwdQ, OutRightQ, OutUpQ; // the exit plane basis (Quake) — the fixed camera orientation
        public float HalfR, HalfU;            // window half-extents along OutRight/OutUp (world units)
        public NVec3 ExitViewQuake;           // exit window center +8qu out — the PVS-union viewpoint
        public Basis ExitBasisG;              // precomputed Godot camera basis (fixed per portal)
        public float NextResizeOk;            // adaptive-resolution hysteresis clock (wall seconds)
    }

    private readonly List<Portal> _portals = new();
    private Camera3D? _mainCamera;
    private bool _built;
    private float _lastTrace = -1f;   // 1Hz gate-trace clock (sv_warpzone_trace)

    /// <summary>wz_portal_scan (debug, one-shot): list every MeshInstance3D in the tree whose world AABB
    /// intersects the first portal's window region — identifies WHICH node actually draws there.</summary>
    private bool _scanned;
    private void ScanWindowRegion(Portal p)
    {
        if (_scanned) return;
        _scanned = true;
        Vector3 c = Coords.ToGodot(p.InOriginQ);
        var probe = new Aabb(c - new Vector3(70, 70, 70), new Vector3(140, 140, 140));
        var stack = new Stack<Node>();
        stack.Push(GetTree().Root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            foreach (Node ch in n.GetChildren()) stack.Push(ch);
            if (n is MeshInstance3D mi && GodotObject.IsInstanceValid(mi))
            {
                Aabb world = mi.GlobalTransform * mi.GetAabb();
                if (!world.Intersects(probe)) continue;
                string mat = mi.MaterialOverride is not null ? mi.MaterialOverride.GetType().Name + "(override)"
                    : (mi.Mesh is not null && mi.Mesh.GetSurfaceCount() > 0 && mi.Mesh.SurfaceGetMaterial(0) is { } m0
                        ? m0.GetType().Name : "none");
            GD.Print($"[portal-scan] '{mi.GetPath()}' visible={mi.IsVisibleInTree()} layers={mi.Layers:X} "
                    + $"aabb={world.Position}+{world.Size} mat={mat}");
            }
        }
    }

    /// <summary>wz_portal_dump: save this portal's viewport image (+ the main view once per sweep) to
    /// screenshots/portal-debug/ — always-latest filenames, no accumulation — and log the portal camera's pose
    /// so the exit-camera's actual view can be inspected offline.</summary>
    private void DumpDebugImages(Portal p)
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("res://screenshots/portal-debug");
            System.IO.Directory.CreateDirectory(dir);
            int idx = _portals.IndexOf(p);
            p.Viewport.GetTexture().GetImage().SavePng($"{dir}/portal{idx}.png");
            if (idx == 0 && GetViewport() is { } mv)
                mv.GetTexture().GetImage().SavePng($"{dir}/main.png");
            long objs = RenderingServer.ViewportGetRenderInfo(p.Viewport.GetViewportRid(),
                RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame);
            long mainObjs = GetViewport() is { } mv2 ? RenderingServer.ViewportGetRenderInfo(mv2.GetViewportRid(),
                RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame) : -1;
            GD.Print($"[portal] dump portal{idx}: camG={p.Cam.GlobalPosition} near={p.Cam.Near:F2} "
                + $"size={p.Viewport.Size} halfR={p.HalfR:F0} halfU={p.HalfU:F0} exitC={p.ExitCenterQ} "
                + $"objs={objs} mainObjs={mainObjs}");

            // Entity probe: every MeshInstance3D under the "Render" node (ClientWorld — all runtime entities)
            // within 400qu of this portal's exit center — visibility flags + whether the portal camera's
            // frustum SHOULD contain it. Identifies why entities don't show through portals.
            if (GetParent() is Node ng && ng.GetNodeOrNull("Render") is Node3D render)
            {
                Vector3 exitG = Coords.ToGodot(p.ExitCenterQ);
                var st = new Stack<Node>();
                st.Push(render);
                while (st.Count > 0)
                {
                    Node n = st.Pop();
                    foreach (Node ch in n.GetChildren()) st.Push(ch);
                    if (n is not MeshInstance3D mi || !GodotObject.IsInstanceValid(mi)) continue;
                    if (mi.GlobalPosition.DistanceTo(exitG) > 400f) continue;
                    Vector3 local = p.Cam.GlobalBasis.Inverse() * (mi.GlobalPosition - p.Cam.GlobalPosition);
                    GD.Print($"[portal-ent] '{mi.GetPath()}' vis={mi.IsVisibleInTree()} layers={mi.Layers:X} "
                        + $"pos={mi.GlobalPosition} camLocal={local} near={p.Cam.Near:F1}");
                }
            }
        }
        catch (System.Exception e)
        {
            GD.Print($"[portal] dump failed: {e.Message}");
        }
    }

    /// <summary>The portal-window shader: unshaded, two-sided, PROJECTIVE sampling. The exit camera is a plain
    /// PERSPECTIVE camera looking along the exit normal (Godot's FRUSTUM projection wrongly culls small-AABB
    /// instances — items/players never rendered — and its light culler rejects sheared matrices outright), so
    /// the window is an off-center subrect of the image: each fragment maps its in-plane window position through
    /// the exit camera's projection. The horizontal axis mirrors through the seam (inRight → −outRight), folded
    /// into the x formula. <c>source_color</c> IS required: the SubViewport target stores tonemapped
    /// sRGB-ENCODED data — decode + the main viewport's re-encode is the identity round trip (sampling raw
    /// double-encodes and washes the view out too bright; user-verified both ways).</summary>
    private const string PortalShader =
        "shader_type spatial;\n" +
        "render_mode unshaded, cull_disabled, depth_draw_opaque;\n" +
        "uniform sampler2D portal_tex : source_color, filter_linear;\n" +
        "uniform vec3 wz_center;\n" +   // entry window center (Godot world)
        "uniform vec3 wz_right;\n" +    // entry plane right (unit, Godot world)
        "uniform vec3 wz_up;\n" +       // entry plane up (unit, Godot world)
        "uniform vec2 wz_off;\n" +      // exit-cam lateral offset of the window center (right, up)
        "uniform float wz_dist;\n" +    // exit-cam perpendicular distance to the exit plane
        "uniform vec2 wz_tan;\n" +      // tan(halfFovX), tan(halfFovY) of the exit camera
        "uniform float wz_uvtest = 0.0;\n" +   // debug: paint the computed UV as color (red=u, green=v)
        "varying vec3 wpos;\n" +
        "void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }\n" +
        "void fragment() {\n" +
        "    vec3 d = wpos - wz_center;\n" +
        "    float a = dot(d, wz_right);\n" +
        "    float b = dot(d, wz_up);\n" +
        "    vec2 ndc = vec2(wz_off.x - a, wz_off.y + b) / (wz_dist * wz_tan);\n" +
        "    vec2 uv = vec2(0.5 + 0.5 * ndc.x, 0.5 - 0.5 * ndc.y);\n" +
        "    if (wz_uvtest > 1.5) { ALBEDO = vec3(1.0, 0.0, 1.0); }\n" +           // 2 = solid magenta (which-quad probe)
        "    else if (wz_uvtest > 0.5) { ALBEDO = vec3(uv, 0.0); } else {\n" +      // 1 = paint UVs
        "    ALBEDO = texture(portal_tex, uv).rgb; }\n" +
        "}\n";

    /// <summary>Wire the renderer: the map root (whose "Portals" child holds the window meshes) and the live
    /// first-person camera. Builds the per-portal SubViewports once; safe to call when there are no portals.</summary>
    public void Setup(Node3D mapRoot, Camera3D mainCamera)
    {
        _mainCamera = mainCamera;
        if (_built || mapRoot is null || mainCamera is null)
            return;
        _built = true;

        if (Api.Services is null || !PortalRenderEnabled())
            return; // disabled → portal surfaces keep their placeholder material

        WarpzoneManager? zones = WarpzoneTrace.AmbientManager;
        if (zones is null)
            return; // pure remote/dedicated client: no zone transforms (listen-host-only feature)

        Node? portalsRoot = mapRoot.GetNodeOrNull("Portals");
        if (portalsRoot is null)
            return; // map has no warpzone surfaces

        Viewport? mainVp = GetViewport();

        foreach (Node child in portalsRoot.GetChildren())
        {
            if (child is not MeshInstance3D surface)
                continue;
            // Warpzone pocket DECOR (the authored backdrop + rims, see MapLoader.BuildPortalSurfaces): visible
            // to the player as authored, EXCLUDED from every portal camera (they sit inside the pocket volume
            // the frustum's near plane grazes).
            if (surface.HasMeta("wz_decor"))
            {
                surface.Layers |= PortalSurfaceLayerBit;
                continue;
            }
            if (!surface.HasMeta("wz_origin"))
                continue;
            if (_portals.Count >= MaxPortals)
            {
                GD.Print($"[PortalRenderer] portal cap {MaxPortals} reached — remaining surfaces keep the placeholder");
                break;
            }

            // Surface plane in QUAKE space (stored raw in the Vector3 metas by MapLoader).
            Vector3 og = (Vector3)surface.GetMeta("wz_origin");
            NVec3 surfOrigin = new(og.X, og.Y, og.Z);
            Vector3 ng = (Vector3)surface.GetMeta("wz_normal");
            NVec3 surfNormal = new(ng.X, ng.Y, ng.Z);

            if (!TryMatchZone(zones, surfOrigin, surfNormal, out WarpzoneTransform t))
                continue; // no linked zone for this surface — keep the placeholder

            // The window rectangle on the EXIT plane: warp the entry mesh's corners through the zone and
            // measure their spread along the exit plane's in-plane basis. (A rotation preserves extents, so
            // these also serve the entry-side shader mapping.)
            NVec3 exitCenter = t.TransformOrigin(surfOrigin);
            Aabb local = surface.GetAabb();
            float halfR = 1f, halfU = 1f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 cornerG = surface.GlobalTransform * local.GetEndpoint(i);
                NVec3 cQ = t.TransformOrigin(Coords.ToQuake(cornerG)) - exitCenter;
                halfR = Mathf.Max(halfR, Mathf.Abs(NVec3.Dot(cQ, t.OutRight)));
                halfU = Mathf.Max(halfU, Mathf.Abs(NVec3.Dot(cQ, t.OutUp)));
            }

            // Render-target size: fixed height, width from the WINDOW's aspect (the frustum near rect is the
            // window, so the image aspect must match the window, not the screen).
            // SQUARE render target (the perspective exit camera uses a symmetric cone; a square target never
            // needs aspect-driven resizes — see the per-frame block for why resizes must stay rare).
            var size = new Vector2I(384, 384);

            var vp = new SubViewport
            {
                Name = $"PortalView_{_portals.Count}",
                Size = size,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                // The portal camera hugs the exit plane from behind: occlusion culling would cull the whole
                // room whenever a config enables it (the camera is inside the wall volume). The frustum clip
                // does the real work.
                UseOcclusionCulling = false,
            };
            if (mainVp is not null)
            {
                vp.World3D = mainVp.World3D;     // share the live scene (map, players, effects, sun)
                vp.Msaa3D = mainVp.Msaa3D;
                vp.ScreenSpaceAA = mainVp.ScreenSpaceAA;
            }
            else
            {
                vp.OwnWorld3D = true;
            }
            AddChild(vp);

            // Fixed orientation: perpendicular to the exit plane, looking INTO the exit room. Basis columns =
            // (right, up, -forward) in Godot space.
            var basis = new Basis(
                Coords.ToGodot(t.OutRight), Coords.ToGodot(t.OutUp), -Coords.ToGodot(t.OutForward));
            var cam = new Camera3D
            {
                Name = "PortalCam",
                Current = true,
                // Exclude the portal surfaces themselves (no portal-in-portal feedback) AND the first-person
                // viewmodel layer: DP hides RENDER_VIEWMODEL entities in every reflection/refraction sub-view
                // (gl_rmain.c R_View_UpdateEntityVisible renderimask), and the gun's short-depth-range remap
                // would otherwise smear it across the whole portal image whenever a portal shows your position.
                CullMask = mainCamera.CullMask & ~(PortalSurfaceLayerBit | ViewModelRenderFx.RenderLayerBit),
            };
            vp.AddChild(cam);
            cam.GlobalBasis = basis;

            var shader = new Shader { Code = PortalShader };
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("portal_tex", vp.GetTexture());
            mat.SetShaderParameter("wz_center", Coords.ToGodot(surfOrigin));
            mat.SetShaderParameter("wz_right", Coords.ToGodot(t.InRight));
            mat.SetShaderParameter("wz_up", Coords.ToGodot(t.InUp));
            mat.SetShaderParameter("wz_half", new Vector2(halfR, halfU));
            surface.MaterialOverride = mat;
            surface.Layers |= PortalSurfaceLayerBit; // visible to the main camera, excluded from portal cameras

            // On-screen gate: only re-render the exit view while the window itself is visible to the main
            // camera. The window mesh is a FLAT quad — grow the notifier box so the culling test never sees a
            // degenerate AABB.
            var notifier = new VisibleOnScreenNotifier3D { Aabb = local.Grow(0.5f) };
            surface.AddChild(notifier);

            _portals.Add(new Portal
            {
                Surface = surface, Transform = t, Viewport = vp, Cam = cam, Material = mat, Notifier = notifier,
                InOriginQ = t.InOrigin, InForwardQ = t.InForward,
                ExitCenterQ = exitCenter, OutFwdQ = t.OutForward, OutRightQ = t.OutRight, OutUpQ = t.OutUp,
                HalfR = halfR, HalfU = halfU, ExitBasisG = basis,
                ExitViewQuake = t.OutOrigin + t.OutForward * 8f, // just inside the exit room's airspace
            });
        }

        if (_portals.Count > 0)
            GD.Print($"[PortalRenderer] {_portals.Count} live portal(s)");
    }

    /// <summary><c>cl_portal_render</c> — default ON (unset/empty enables it); explicit <c>0</c> falls back to the
    /// dark-mirror placeholder.</summary>
    private static bool PortalRenderEnabled()
    {
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString("cl_portal_render");
        return string.IsNullOrWhiteSpace(s) || Api.Cvars.GetFloat("cl_portal_render") != 0f;
    }

    /// <summary><c>cl_portal_resolution</c> — scale on the adaptive portal render resolution (default/unset 1.0;
    /// 0.5 = half detail for weak GPUs, 2 = supersample). Clamped to a sane range.</summary>
    private static float PortalResolutionScale()
    {
        if (Api.Services is null)
            return 1f;
        string s = Api.Cvars.GetString("cl_portal_resolution");
        if (string.IsNullOrWhiteSpace(s))
            return 1f;
        return Mathf.Clamp(Api.Cvars.GetFloat("cl_portal_resolution"), 0.25f, 2f);
    }

    public override void _ExitTree()
    {
        // Drop the published exit viewpoints with the renderer (a stale entry would feed the NEXT map's PVS
        // cullers clusters from the WRONG tree).
        ActiveExitViewsQuake.Clear();
    }

    public override void _Process(double delta)
    {
        // (P4 2026-07-03) Named profiler scope: the _Process side of the portal drive (gates, frustum math,
        // adaptive resize). The exit-view renders themselves land in rcpu/gpu — a hitch here with rcpu/gpu
        // spiking while portals are on screen still points at this pass. Registered in
        // FrameProfiler.TopLevelNodeScopes so it leaves proc:other.
        using var _ = FrameProfiler.Scope("portal.render");

        // Rebuilt every frame: only portals that actually render this frame contribute a PVS-union viewpoint.
        ActiveExitViewsQuake.Clear();
        if (_portals.Count == 0 || _mainCamera is null)
            return;

        Camera3D main = _mainCamera;
        // NetGame.UpdateCamera has already posed the camera this frame (parent _Process runs before this
        // child's), so there is no lag.
        NVec3 camPosQ = Coords.ToQuake(main.GlobalPosition);

        // 1Hz gate trace (sv_warpzone_trace); wz_portal_force bypasses the gates for scripted debug runs;
        // wz_portal_dump saves the portal + main images on each trace tick.
        bool trace = false;
        if (Api.Services is not null && Api.Cvars.GetFloat("sv_warpzone_trace") != 0f)
        {
            float nowS = Time.GetTicksMsec() * 0.001f;
            if (nowS - _lastTrace >= 1f) { _lastTrace = nowS; trace = true; }
        }
        bool force = Api.Services is not null && Api.Cvars.GetFloat("wz_portal_force") != 0f;
        bool dump = trace && Api.Services is not null && Api.Cvars.GetFloat("wz_portal_dump") != 0f;

        // wz_portal_lookat N (debug): park the MAIN camera head-on in front of the Nth portal's window every
        // frame (after NetGame posed it), so scripted dump runs capture the on-quad result deterministically.
        // N >= 11: park INSIDE portal (N-10)'s EXIT room on the same sightline the portal camera uses — the
        // apples-to-apples entity-visibility comparison (main.png direct vs portalN.png through the seam).
        float lookat = Api.Services is not null ? Api.Cvars.GetFloat("wz_portal_lookat") : 0f;
        if (_portals.Count > 0 && lookat != 0f)
        {
            bool exitSide = lookat >= 11f;
            Portal t0 = _portals[Mathf.Clamp((int)lookat - (exitSide ? 11 : 1), 0, _portals.Count - 1)];
            NVec3 eye = exitSide
                ? t0.ExitCenterQ + t0.OutFwdQ * 4f
                : t0.InOriginQ + t0.InForwardQ * 90f + new NVec3(0f, 0f, 8f);
            NVec3 fwd = exitSide ? t0.OutFwdQ : -t0.InForwardQ;
            NVec3 up = new(0f, 0f, 1f);
            NVec3 right = NVec3.Normalize(NVec3.Cross(fwd, up));
            main.GlobalBasis = new Basis(Coords.ToGodot(right), Coords.ToGodot(up), -Coords.ToGodot(fwd));
            main.GlobalPosition = Coords.ToGodot(eye);
            camPosQ = eye;
        }

        foreach (Portal p in _portals)
        {
            // Render gate: the window must be on screen AND the main camera on the FRONT side of the IN plane
            // (from behind you see the window's back face with whatever the texture last held; once the eye
            // actually crosses, WarpzoneFixView owns the whole screen). Disabled viewports keep their last
            // texture — a one-frame-stale image on re-entry, imperceptible.
            bool facing = NVec3.Dot(camPosQ - p.InOriginQ, p.InForwardQ) > 0f;
            bool onScreen = p.Notifier.IsOnScreen();
            bool visible = (facing && onScreen) || force;
            p.Viewport.RenderTargetUpdateMode = visible ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            if (trace)
            {
                GD.Print($"[portal] '{p.Surface.Name}' facing={facing} onScreen={onScreen} -> {(visible ? "RENDER" : "off")}"
                    + $" near={p.Cam.Near:F2} size={p.Viewport.Size} camQ={camPosQ} inO={p.InOriginQ} inF={p.InForwardQ}");
                // debug: paint the quad's computed UVs (1 → red=u, green=v; 2 → solid magenta) instead of the
                // texture. Read per frame — a Setup-time read can precede the cvar-store bridge sync.
                float uvtest = Api.Services is not null ? Api.Cvars.GetFloat("wz_portal_uvtest") : 0f;
                p.Material.SetShaderParameter("wz_uvtest", uvtest);
                if (uvtest != 0f)
                    GD.Print($"[portal] uvtest={uvtest} applied to '{p.Surface.Name}' mat={p.Surface.MaterialOverride == p.Material}");
            }
            if (dump && visible)
            {
                DumpDebugImages(p);
                ScanWindowRegion(_portals[0]);
            }
            if (!visible)
                continue;

            ActiveExitViewsQuake.Add(p.ExitViewQuake); // PVS-union viewpoint for the cullers (see the field doc)

            // Window-anchored frustum: eye at the warped main-camera position; orientation fixed perpendicular
            // to the exit plane; the frustum's near rectangle pinned to the exit window. near = perpendicular
            // distance from the eye to the exit plane (the eye is BEHIND it while the viewer is in front of the
            // entry — the facing gate guarantees that); offset = the window center's lateral offset from the
            // camera axis in camera-local X/Y (camera X == OutRight, Y == OutUp, world units carry 1:1).
            NVec3 pPos = p.Transform.TransformOrigin(camPosQ);
            // Perpendicular distance from the (behind-plane) eye to the exit plane. In the last couple of units
            // before the crossing the frustum's shear (lateral offset ÷ near) explodes and Godot's light culler
            // rejects the camera ("prepare_camera: Condition !res" spam, lights fall back unculled) — freeze the
            // viewport instead: the 1-2 frame-old image fills the screen at that range, and WarpzoneFixView owns
            // the view the moment the eye crosses.
            float planeDist = NVec3.Dot(p.ExitCenterQ - pPos, p.OutFwdQ);
            if (planeDist < 2f)
            {
                p.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                continue;
            }
            var offset = new Vector2(
                NVec3.Dot(p.ExitCenterQ - pPos, p.OutRightQ),
                NVec3.Dot(p.ExitCenterQ - pPos, p.OutUpQ));

            // PERSPECTIVE exit camera sized to cover the window's cone from the warped eye. Because the camera
            // looks ALONG the exit normal, the plane-coincident wall face (the warpzone surface is painted on a
            // SOLID wall) is exactly perpendicular to the view axis — near = planeDist + 0.5 clips it perfectly,
            // the same oblique-equivalent clip the frustum mode had, without FRUSTUM projection's broken
            // small-instance culling (items/players never rendered; the light culler rejected sheared matrices
            // with "prepare_camera" spam).
            // SYMMETRIC square cone that covers the window in BOTH axes: tan = the larger requirement. The
            // render target stays SQUARE so the per-frame aspect changes never resize it — repeatedly resizing
            // a SubViewport whose texture is bound in a live material corrupts the renderer ("uninitialized
            // RID" storm → BLIT_PASS segfault, observed live). Slight over-render on the narrow axis is the
            // price of a stable target.
            float tan = Mathf.Max(
                (Mathf.Abs(offset.Y) + p.HalfU) * 1.04f / planeDist,
                (Mathf.Abs(offset.X) + p.HalfR) * 1.04f / planeDist);
            // Clamp the half-angle (grazing views otherwise explode toward 180°); the window stays covered
            // because the facing/shear guard already freezes the viewport in the final approach.
            tan = Mathf.Min(tan, 10f);

            // ADAPTIVE RESOLUTION: ≈ the window's projected on-screen pixel size (sharp close, cheap far),
            // quantized to 128px buckets with a cooldown — resizes are RARE (bucket changes only).
            if (GetViewport() is { } mvp)
            {
                float screenH = mvp.GetVisibleRect().Size.Y;
                float projPx = screenH * (2f * p.HalfU / planeDist)
                    / (2f * Mathf.Tan(Mathf.DegToRad(main.Fov) * 0.5f));
                float resScale = PortalResolutionScale();
                int bucket = Mathf.Clamp(Mathf.CeilToInt(projPx * resScale / 128f), 1, 8) * 128;
                float nowR = Time.GetTicksMsec() * 0.001f;
                if (bucket != p.Viewport.Size.Y && nowR >= p.NextResizeOk)
                {
                    p.NextResizeOk = nowR + 0.3f;
                    p.Viewport.Size = new Vector2I(bucket, bucket);
                    // Re-bind the render-target texture after the reallocation so the material never samples
                    // a stale RID.
                    p.Material.SetShaderParameter(ParamPortalTex, p.Viewport.GetTexture());
                }
            }

            p.Cam.GlobalBasis = p.ExitBasisG;
            p.Cam.GlobalPosition = Coords.ToGodot(pPos);
            // Square viewport → aspect 1 → the horizontal fov equals the vertical (KeepAspect irrelevant).
            p.Cam.SetPerspective(Mathf.RadToDeg(2f * Mathf.Atan(tan)), planeDist + 0.5f, main.Far);

            // Feed the projective mapping (see PortalShader): the window subrect of the exit image.
            p.Material.SetShaderParameter(ParamWzOff, offset);
            p.Material.SetShaderParameter(ParamWzDist, planeDist);
            p.Material.SetShaderParameter(ParamWzTan, new Vector2(tan, tan));
        }
    }

    /// <summary>Match a portal surface to the linked warpzone whose IN plane it is: the zone's IN origin must lie
    /// (near) the surface plane with an aligned normal — nearest such zone within a distance bound. Returns that
    /// zone's IN→OUT transform — the warp a portal camera applies to the main view.</summary>
    private static bool TryMatchZone(WarpzoneManager zones, NVec3 surfOrigin, NVec3 surfNormal, out WarpzoneTransform best)
    {
        best = default;
        float bestDist = 96f * 96f; // within 96 units of the surface centroid
        bool found = false;
        NVec3 sn = surfNormal.LengthSquared() > 1e-9f ? NVec3.Normalize(surfNormal) : surfNormal;
        foreach (Warpzone wz in zones.Zones)
        {
            if (!wz.Linked || !wz.Transform.Valid)
                continue;
            NVec3 inFwd = wz.Transform.InForward;
            float align = System.MathF.Abs(NVec3.Dot(sn, inFwd.LengthSquared() > 1e-9f ? NVec3.Normalize(inFwd) : inFwd));
            if (align < 0.7f)
                continue; // not the same plane orientation
            // The zone's IN plane must be (near-)coplanar with the surface — an offset parallel face (the zone
            // box's backdrop) is decor, not the window.
            if (System.MathF.Abs(NVec3.Dot(wz.Transform.InOrigin - surfOrigin, sn)) > 8f)
                continue;
            float d = (wz.Transform.InOrigin - surfOrigin).LengthSquared();
            if (d < bestDist)
            {
                bestDist = d;
                best = wz.Transform;
                found = true;
            }
        }
        return found;
    }
}
