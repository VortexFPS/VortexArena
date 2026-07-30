using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Vfs;            // VirtualFileSystem
using VortexArena.Game.Loaders;           // AssetLoader
using VortexArena.Game.Client;            // PlayerModel

namespace VortexArena.Game;

/// <summary>
/// A standalone, no-net <b>player-model viewer</b> for windowed visual QA — the boot target of the
/// <c>--model &lt;name&gt;</c> CLI flag (parallel to <c>--map</c>). It exists so the
/// <c>tools/visual-qa.sh</c> sweep can capture a real rendered frame of EACH stock player model
/// (<c>models/player/erebus.iqm</c>, <c>gak</c>, <c>ignis</c>, …) and a human (or an agent via the Read
/// tool) can eyeball the bind/idle pose, skin materials (no magenta missing-texture), and rig integrity.
///
/// <para><b>Why its own node and not the networked match.</b> The thing we want to see is a static,
/// well-lit model from several sides — it needs none of the netcode (server, client, prediction, async
/// handshake) a real <see cref="Net.NetGame"/> match carries, and that handshake would only race the
/// screenshot capture. So this is a thin scene: it reuses the SAME skeletal-IQM render path real players
/// render through (<see cref="AssetLoader.LoadSkeletalModel"/> → <see cref="PlayerModel"/>, posed idle),
/// adds neutral studio lighting, and frames an orthographic camera — nothing more.</para>
///
/// <para><b>Turntable contact sheet.</b> Rather than booting once per angle, the viewer places
/// <see cref="AngleCount"/> copies of the model in a single row, each yawed by an even slice of 360°, and
/// the existing <see cref="ScreenshotHook"/> (attached by <see cref="Main"/> when <c>--screenshot</c> is
/// passed) captures the whole row in ONE frame. An orthographic camera keeps every copy the same size, so
/// the result is a clean side-by-side strip of every facing.</para>
/// </summary>
public sealed partial class ModelViewer : Node3D
{
    /// <summary>
    /// The model to show. A bare hero name (<c>"erebus"</c>) resolves to <c>models/player/&lt;name&gt;.iqm</c>;
    /// a value containing a slash or <c>.iqm</c> is treated as an explicit VFS vpath. Set by the
    /// <c>--model</c> boot flag via <see cref="Shell"/>.
    /// </summary>
    [Export] public string ModelName { get; set; } = "erebus";

    /// <summary>
    /// An already-mounted asset VFS to read the model + skins from (the menu shell mounts the gamedir once at
    /// boot and hands it here). REQUIRED in practice — the <c>--model</c> path always boots through
    /// <see cref="Shell"/>, which supplies <c>MenuState.Vfs</c>. When null the viewer renders only the empty
    /// lit stage (nothing to load).
    /// </summary>
    public VirtualFileSystem? SharedVfs { get; set; }

    /// <summary>How many evenly-spaced turntable angles (0…360°) are laid out left→right in the contact sheet.</summary>
    private const int AngleCount = 6;

    /// <summary>
    /// Base yaw (degrees) applied to every copy on top of its turntable slice, chosen so the i=0 copy faces
    /// the camera (player IQMs are authored facing Quake +X → Godot +X; the camera looks down −Z, so a −90°
    /// yaw turns the model's front toward it). Purely cosmetic framing — adjust if a model reads back-to-front.
    /// </summary>
    private const float FrontYawDeg = -90f;

    private VirtualFileSystem? _vfs;
    private AssetLoader? _assets;

    public override void _Ready()
    {
        // Clean capture: remove the always-on frame-profiler graph (Main adds the FrameProfiler session-wide).
        // The cvar can't switch it off here — FrameProfiler.Mode() treats cl_frameprofiler==0 as "on" in a
        // DEBUG build unless the cvar is *modified* (value ≠ default), and its default IS "0", so no value both
        // reads 0 and counts as modified. So for a QA capture we just drop the node by its well-known name.
        GetTree().Root.FindChild("FrameProfiler", recursive: true, owned: false)?.QueueFree();

        AddLighting();

        _vfs = SharedVfs;
        if (_vfs is null)
        {
            GD.PrintErr("[ModelViewer] no shared VFS supplied — showing an empty stage.");
            return;
        }
        _assets = new AssetLoader(_vfs);

        string? vpath = ResolveModelVPath(ModelName);
        if (vpath is null)
        {
            GD.PrintErr($"[ModelViewer] model '{ModelName}' not found in the VFS (tried models/player/{ModelName}.iqm).");
            CaptureGate.MarkReady(); // nothing to wait for — let a --screenshot capture the empty stage + the error
            return;
        }

        BuildTurntable(vpath);

        // The turntable is built synchronously here; let a windowed --screenshot proceed (after its short settle).
        CaptureGate.MarkReady();
    }

    // -------------------------------------------------------------------------------------------------
    //  Turntable: N posed copies in a row + an orthographic camera framing the whole strip
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Build <see cref="AngleCount"/> idle-posed copies of the model, each yawed by an even slice of 360°,
    /// spread along the X axis, then drop an orthographic camera that frames the whole row. The model's own
    /// bounding box (measured from the first built copy) drives both the per-copy spacing and the camera size,
    /// so a tall/large model (megaerebus) frames as cleanly as a small one.
    /// </summary>
    private void BuildTurntable(string vpath)
    {
        var pivots = new List<Node3D>();
        Aabb modelAabb = default;
        bool haveAabb = false;

        for (int i = 0; i < AngleCount; i++)
        {
            Node3D? model = BuildModel(vpath);
            if (model is null)
                continue;

            float yawDeg = FrontYawDeg - i * (360f / AngleCount);
            var pivot = new Node3D { Name = $"Angle{i}" };
            pivot.RotateY(Mathf.DegToRad(yawDeg));
            pivot.AddChild(model);
            AddChild(pivot);
            pivots.Add(pivot);

            if (!haveAabb)
            {
                modelAabb = ComputeLocalAabb(model);
                haveAabb = true;
            }
        }

        if (pivots.Count == 0)
        {
            GD.PrintErr($"[ModelViewer] '{vpath}' produced no renderable copies.");
            return;
        }

        // Horizontal radius after a yaw about the up axis mixes the X and Z extents, so the footprint a
        // rotated copy can occupy is the larger of the two. Space copies by a little over that diameter.
        float xr = Mathf.Max(Mathf.Abs(modelAabb.Position.X), Mathf.Abs(modelAabb.End.X));
        float zr = Mathf.Max(Mathf.Abs(modelAabb.Position.Z), Mathf.Abs(modelAabb.End.Z));
        float horizRadius = Mathf.Max(Mathf.Max(xr, zr), 8f);
        float spacing = horizRadius * 2.6f;

        // Center the row on the origin: copy i sits at x = (i − (N−1)/2) · spacing.
        float mid = (pivots.Count - 1) * 0.5f;
        for (int i = 0; i < pivots.Count; i++)
            pivots[i].Position = new Vector3((i - mid) * spacing, 0f, 0f);

        float rowWidth = (pivots.Count - 1) * spacing + horizRadius * 2f;
        float height = Mathf.Max(modelAabb.Size.Y, 16f);
        float centerY = (modelAabb.Position.Y + modelAabb.End.Y) * 0.5f;

        AddTurntableCamera(rowWidth, height, centerY);

        GD.Print($"[ModelViewer] '{vpath}': {pivots.Count} angles, " +
                 $"model {modelAabb.Size.X:F0}×{modelAabb.Size.Y:F0}×{modelAabb.Size.Z:F0}u.");
    }

    /// <summary>
    /// Place an orthographic camera centered on the row, pulled back along +Z and looking at the strip. An
    /// ortho projection means every copy renders the same size regardless of its depth (no perspective
    /// foreshortening across the row), and the vertical <c>Size</c> is chosen so the row fits both ways given
    /// the current viewport aspect (set by <c>--resolution</c>).
    /// </summary>
    private void AddTurntableCamera(float rowWidth, float height, float centerY)
    {
        float aspect = ViewportAspect();
        // Ortho Size is the VERTICAL extent; horizontal coverage is Size·aspect. Fit whichever is tighter.
        float sizeForWidth = rowWidth / Mathf.Max(aspect, 0.1f);
        float orthoSize = Mathf.Max(sizeForWidth, height) * 1.15f; // 15% margin so nothing clips the edges

        float dist = Mathf.Max(rowWidth, height) * 2f + 256f;       // ortho: distance only sets the clip range
        var cam = new Camera3D
        {
            Name = "TurntableCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = orthoSize,
            Near = 1f,
            Far = dist * 2f + 1024f,
            Position = new Vector3(0f, centerY, dist),
            Current = true,
        };
        AddChild(cam);
        cam.LookAt(new Vector3(0f, centerY, 0f), Vector3.Up);
    }

    /// <summary>Current viewport aspect (width/height); falls back to 16:9 before the window has a size.</summary>
    private float ViewportAspect()
    {
        Vector2 sz = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280f, 720f);
        return sz.Y > 0f ? sz.X / sz.Y : 16f / 9f;
    }

    // -------------------------------------------------------------------------------------------------
    //  Model build + posing (the real skeletal-IQM path, settled into an idle stance)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Build one copy of the model through the live skeletal path and leave it at its BIND (rest) pose — the
    /// standard model-viewer reference, and what makes rig faults obvious: an un-twisted, fully-spread skeleton
    /// where a collapsed joint, a mis-weighted limb, or a magenta material reads at a glance. (We deliberately
    /// do NOT run the locomotion poser here — that synthesizes movement animation, which is for in-game players,
    /// not a static reference sheet.) <see cref="PlayerModel.Setup"/> stops the model's AnimationPlayer, so the
    /// skeleton stays at rest. Falls back to the format-agnostic static loader for a non-skeletal model.
    /// </summary>
    private Node3D? BuildModel(string vpath)
    {
        AssetLoader.SkeletalModelParts? parts = _assets!.LoadSkeletalModel(vpath, 0);
        if (parts is not null)
        {
            var pm = new PlayerModel { Name = "Model" };
            pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
            if (pm.Active)
                return pm; // bind/rest pose — the clean reference for visual QA
            pm.QueueFree(); // non-skeletal IQM — let the static loader handle it below
        }

        return _assets.LoadModel(vpath);
    }

    /// <summary>
    /// Compute a node's bounding box in its OWN local space by merging every descendant
    /// <see cref="VisualInstance3D"/>'s AABB (each transformed from its own local frame back into the root's).
    /// Rotation-independent, so it's safe to measure a copy that already sits under a yawed pivot. Returns a
    /// player-sized default if the node carries no visual instances yet.
    /// </summary>
    private static Aabb ComputeLocalAabb(Node3D root)
    {
        Transform3D rootInv = root.GlobalTransform.AffineInverse();
        bool have = false;
        Aabb total = default;

        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n is VisualInstance3D vi)
            {
                Aabb localToRoot = (rootInv * vi.GlobalTransform) * vi.GetAabb();
                total = have ? total.Merge(localToRoot) : localToRoot;
                have = true;
            }
            foreach (Node c in n.GetChildren())
                stack.Push(c);
        }

        return have ? total : new Aabb(new Vector3(-16f, 0f, -16f), new Vector3(32f, 72f, 32f));
    }

    // -------------------------------------------------------------------------------------------------
    //  Path resolution + lighting
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolve a configured model name/path to an existing VFS vpath. Probes (in order): an explicit path
    /// verbatim (and +<c>.iqm</c>), then the <c>models/player/&lt;name&gt;.iqm</c> hero-model convention.
    /// Returns the first candidate that exists, or null if none do.
    /// </summary>
    private string? ResolveModelVPath(string name)
    {
        foreach (string cand in ModelVPathCandidates(name))
            if (_vfs!.Exists(cand))
                return cand;
        return null;
    }

    private static IEnumerable<string> ModelVPathCandidates(string name)
    {
        string p = (name ?? "").Replace('\\', '/').Trim();
        if (p.Length == 0)
            p = "erebus";

        bool hasIqm = p.EndsWith(".iqm", StringComparison.OrdinalIgnoreCase);
        bool looksLikePath = p.Contains('/');

        if (looksLikePath)
        {
            yield return p;                              // explicit vpath, verbatim
            if (!hasIqm) yield return p + ".iqm";
        }

        // Bare hero name (the common case) → the models/player convention.
        string bare = looksLikePath ? p[(p.LastIndexOf('/') + 1)..] : p;
        if (bare.EndsWith(".iqm", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^4];
        yield return $"models/player/{bare}.iqm";
    }

    /// <summary>
    /// Neutral three-quarter studio lighting on a flat slate background: a keyed directional light + a dimmer
    /// fill from the opposite side, plus flat color ambient. The flat background (no skybox) and even fill are
    /// deliberate — they make a magenta missing-texture or a collapsed bone obvious at a glance, which is the
    /// whole point of the visual-QA capture.
    /// </summary>
    private void AddLighting()
    {
        AddChild(new DirectionalLight3D
        {
            Name = "Key",
            RotationDegrees = new Vector3(-50f, -35f, 0f),
            ShadowEnabled = true,
            LightEnergy = 1.1f,
        });
        AddChild(new DirectionalLight3D
        {
            Name = "Fill",
            RotationDegrees = new Vector3(-20f, 150f, 0f),
            LightEnergy = 0.4f,
        });

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.18f, 0.20f, 0.24f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
            AmbientLightEnergy = 0.7f,
            // Match the world/skin shaders' hand-tuned Linear round-trip (see GameDemo/NetGame lighting notes).
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Name = "ViewerEnvironment", Environment = env });
    }
}
