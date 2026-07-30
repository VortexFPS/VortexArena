using System;
using System.Collections.Generic;
using System.Linq; // IReadOnlyList<Warpzone>.Contains
using Godot;
using VortexArena.Common.Gameplay;  // WarpzoneTrace, WarpzoneManager, Warpzone
using VortexArena.Common.Math;      // QMath
using VortexArena.Common.Services;  // Api
using NVec3 = System.Numerics.Vector3;
// Coords (Quake<->Godot axis swap) lives in the parent VortexArena.Game namespace — reachable implicitly.

namespace VortexArena.Game.Client;

/// <summary>
/// The client-side Quake-style portal DISC cosmetic — the C# stand-in for Base's <c>MDL_PORTAL</c> entity
/// (<c>models/portal.md3</c>, server/portals.qc <c>Portal_Spawn</c>). For each live Porto-weapon portal
/// warpzone it draws the portal disc model at the IN plane with the role glow — <c>EF_RED</c> for the
/// in-portal, <c>EF_BLUE|EF_STARDUST</c> for the out-portal — and a 0.5s fade-out (the QC
/// <c>SUB_SetFade(time, 0.5)</c>) begun when the zone leaves the manager (expiry / owner death / reset).
///
/// <para>Skin selection mirrors the server: skin 0 = the IN portal, skin 1 = the OUT portal. Skin 2 (the
/// "waiting / broken" portal) is unreachable here — a warpzone portal is only drawn once it is
/// <see cref="Warpzone.Linked"/>, never in the unconnected waiting state.</para>
///
/// <para><b>Listen-host only</b> (the portal warpzones live in <see cref="WarpzoneTrace.AmbientManager"/>,
/// which a pure remote/dedicated-server client does not have — networking them is the pre-existing
/// <c>warpzones.client.render</c> follow-up). When the manager is absent there are simply no discs (no
/// regression). Shares the <c>cl_portal_render</c> toggle (default ON) with the see-through portal window
/// (<see cref="PortalRenderer"/>) so both halves of the cosmetic switch together.</para>
///
/// <para><b>Unverified in-engine.</b> Structurally identical to <see cref="PortalRenderer"/> /
/// <c>WarpzoneFixView</c> but the rendered disc (handedness through the Quake↔Godot conversion, the glow,
/// the fade) needs an in-game eyeball; set <c>cl_portal_render 0</c> to disable. The fade is a client-side
/// <c>SUB_SetFade</c> stand-in: Base fades the real portal model entity, which the warpzone realisation does
/// not host, so the fade is started here when the zone is no longer live in the manager.</para>
/// </summary>
public partial class PortalDiscRenderer : Node3D
{
    private const string PortalModel = "models/portal.md3";
    private const int SkinIn = 0, SkinOut = 1; /* Skin 2 (waiting/broken) unreachable: a warpzone portal is only drawn once Linked */
    private const float FadeSeconds = 0.5f; // SUB_SetFade(time, 0.5)

    private sealed class Disc
    {
        public Node3D Root = null!;
        public List<MeshInstance3D> Meshes = new();
        public List<StandardMaterial3D> Mats = new();
        public bool IsInPortal;
        public bool Fading;
        public float FadeStart;
    }

    private Camera3D? _mainCamera;
    private Func<string, int, Node3D?>? _modelFactory;
    private readonly Dictionary<Warpzone, Disc> _discs = new();

    /// <summary>Reused per-frame removal buffer so the removal scan never allocates.</summary>
    private readonly List<Warpzone> _stale = new();

    /// <summary>
    /// Wire the renderer: the live first-person camera (kept for parity with the sibling renderers / future
    /// LOD culling) and the host's model factory (path + skin → a textured render node, host-set to
    /// <c>AssetLoader.LoadModel</c>). A null factory or a null/missing-content return draws no disc.
    /// </summary>
    public void Setup(Camera3D mainCamera, Func<string, int, Node3D?> modelFactory)
    {
        _mainCamera = mainCamera;
        _modelFactory = modelFactory;
    }

    /// <summary><c>cl_portal_render</c> — read IDENTICALLY to <see cref="PortalRenderer"/> so the disc and the
    /// see-through window share one toggle: services-null → off; unset/blank → on; explicit <c>0</c> → off.</summary>
    private static bool DiscRenderEnabled()
    {
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString("cl_portal_render");
        return string.IsNullOrWhiteSpace(s) || Api.Cvars.GetFloat("cl_portal_render") != 0f;
    }

    public override void _Process(double delta)
    {
        if (_modelFactory is null || Api.Services is null || !DiscRenderEnabled())
            return;

        WarpzoneManager? zones = WarpzoneTrace.AmbientManager;
        if (zones is null)
            return; // pure remote/dedicated client: no zone transforms → no discs (no regression)

        // PASS 1 — spawn/keep: a disc for every live, linked Porto-portal warpzone not already drawn.
        foreach (Warpzone wz in zones.Zones)
        {
            if (wz.Owner is not null && wz.Linked && !_discs.ContainsKey(wz))
                SpawnDisc(wz);
        }

        // PASS 2 — detect removal: a drawn zone that is no longer live begins its SUB_SetFade.
        _stale.Clear();
        foreach (KeyValuePair<Warpzone, Disc> kv in _discs)
            if (!kv.Value.Fading && !IsLive(zones, kv.Key))
                _stale.Add(kv.Key);
        foreach (Warpzone wz in _stale)
        {
            Disc d = _discs[wz];
            d.Fading = true;
            d.FadeStart = Now();
        }

        // PASS 3 — advance fades: lerp alpha to 0 over FadeSeconds, then free. Snapshot the values so freeing
        // a disc can mutate the dictionary mid-loop.
        foreach (Disc d in _discs.Values.ToList())
        {
            if (!d.Fading)
                continue;
            float a = 1f - (Now() - d.FadeStart) / FadeSeconds;
            if (a <= 0f)
            {
                if (GodotObject.IsInstanceValid(d.Root))
                    d.Root.QueueFree();
                Warpzone? key = FindKey(d);
                if (key is not null)
                    _discs.Remove(key);
                continue;
            }
            foreach (StandardMaterial3D mat in d.Mats)
            {
                Color c = mat.AlbedoColor;
                c.A = a;
                mat.AlbedoColor = c;
            }
        }
    }

    /// <summary>The key of a disc value (the fade pass works off a values snapshot; recover the dictionary key
    /// to remove it). Cheap — at most a handful of live portals.</summary>
    private Warpzone? FindKey(Disc d)
    {
        foreach (KeyValuePair<Warpzone, Disc> kv in _discs)
            if (ReferenceEquals(kv.Value, d))
                return kv.Key;
        return null;
    }

    /// <summary>A warpzone is "live" (keep its disc) while it is still an owned, linked portal registered in the
    /// manager — a removed/expired zone has been dropped from <see cref="WarpzoneManager.Zones"/>.</summary>
    private static bool IsLive(WarpzoneManager z, Warpzone wz)
        => wz.Owner is not null && wz.Linked && z.Zones.Contains(wz);

    /// <summary>Build the disc model for one portal warpzone, oriented at its IN plane with the role glow.</summary>
    private void SpawnDisc(Warpzone wz)
    {
        int skin = wz.IsInPortal ? SkinIn : SkinOut;
        Node3D? model = _modelFactory!(PortalModel, skin);
        if (model is null)
            return; // graceful asset miss
        model.Name = $"portal_{(wz.IsInPortal ? "in" : "out")}";

        // ORIENT at the IN plane. forward = the plane normal; use the same basis convention PortalRenderer
        // uses (Basis(right, up, -forward), Quake→Godot per axis).
        QMath.AngleVectors(wz.InAngles, out NVec3 fwd, out NVec3 right, out NVec3 up);
        model.GlobalTransform = new Transform3D(
            new Basis(Coords.ToGodot(right), Coords.ToGodot(up), -Coords.ToGodot(fwd)),
            Coords.ToGodot(wz.InOrigin));

        // GLOW + single fade-owner material: override every mesh with an unshaded additive material tinted to
        // the role colour (EF_RED for the in-portal, EF_STARDUST|EF_BLUE for the out-portal). The override
        // multiplies the skin texture AND gives the fade ONE alpha owner per mesh.
        Color tint = wz.IsInPortal
            ? new Color(1f, 0.3f, 0.3f, 1f)   // EF_RED
            : new Color(0.3f, 0.5f, 1f, 1f);  // EF_STARDUST | EF_BLUE
        var meshes = new List<MeshInstance3D>();
        CollectMeshes(model, meshes);
        var mats = new List<StandardMaterial3D>();
        foreach (MeshInstance3D mi in meshes)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = tint,
            };
            mi.MaterialOverride = mat;
            mats.Add(mat);
        }

        AddChild(model);
        // DO NOT spin the disc — Base Portal_Spawn sets no avelocity.
        _discs[wz] = new Disc
        {
            Root = model,
            Meshes = meshes,
            Mats = mats,
            IsInPortal = wz.IsInPortal,
        };
    }

    /// <summary>Depth-first collect every <see cref="MeshInstance3D"/> at or under <paramref name="n"/>.</summary>
    private static void CollectMeshes(Node n, List<MeshInstance3D> dst)
    {
        if (n is MeshInstance3D mi)
            dst.Add(mi);
        foreach (Node child in n.GetChildren())
            CollectMeshes(child, dst);
    }

    /// <summary>Current client clock time (mirrors <c>ProjectileRenderer.Now</c>): the clock the fade is measured
    /// against. 0 in a headless/clockless harness.</summary>
    private static float Now()
        => Api.Services?.Clock?.Time ?? 0f;
}
