using System;
using System.Collections.Generic;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Draws lightning arcs and steady energy beams between two world points — the Godot successor to CSQC's
/// <c>te_csqc_lightningarc</c> / <c>cl_effects_lightningarc</c> + <c>Draw_CylindricLine</c>
/// (Base/.../qcsrc/common/effects/qc/lightningarc.qc). The server emits a <c>TE_CSQC_ARC</c> temp-entity
/// (from, to) for the electro combo, Golem lightning zaps and Tesla turret arcs; the client drew nothing for
/// these in this port (the gap this fills).
///
/// An <see cref="Arc"/> reproduces the QC jagged bolt: the segment from <c>from</c> to <c>to</c> is split into
/// up to 16 sub-segments (one per <c>cl_effects_lightningarc_segmentlength</c> = 64u), each drifting off the
/// straight line by a factor that fades from <c>drift_start</c> (0.45) near the source to <c>drift_end</c>
/// (0.1) at the target, so the bolt crackles near the emitter and snaps onto the target. <see cref="Beam"/>
/// draws the straight variant (a steady rail/电 beam). Both render as a self-illuminated additive cross-ribbon
/// (two perpendicular quad strips so it reads as a glowing line from any angle, the cheap stand-in for the
/// QC view-facing cylinder), fading out over a short lifetime before self-freeing.
///
/// Coordinates: <c>from</c>/<c>to</c> arrive in Quake space (the sim convention) and convert with
/// <see cref="Coords.ToGodot"/> at the boundary, like every other render node. The jitter is computed in Quake
/// space to match the QC algorithm exactly.
/// </summary>
public partial class BeamRenderer : Node3D
{
    // QC autocvar defaults (xonotic-client.cfg). Exposed so the host can override from the cvar store.
    [Export] public float SegmentLength { get; set; } = 64f;
    [Export] public float DriftStart { get; set; } = 0.45f;
    [Export] public float DriftEnd { get; set; } = 0.1f;

    /// <summary>Default electric tint (blue-white), the Xonotic lightning look.</summary>
    private static readonly Color DefaultArcColor = new(0.6f, 0.8f, 1.0f);

    // Deterministic-enough jitter source; a single shared instance avoids per-arc allocation. (Plain game
    // code — not a workflow script — so System.Random is fine here.)
    private static readonly Random Rng = new();

    private Texture2D? _beamTex; // shared glow gradient (built once)

    // =================================================================================================
    //  Public API
    // =================================================================================================

    /// <summary>
    /// Spawn a jagged lightning arc from <paramref name="fromQuake"/> to <paramref name="toQuake"/> (Quake
    /// space). The bolt lives <paramref name="lifetime"/> seconds, flickering and fading, then self-frees.
    /// Returns the created node (already in the tree), or null if the endpoints coincide.
    /// </summary>
    public Node3D? Arc(NVec3 fromQuake, NVec3 toQuake, Color? color = null, float width = 9f, float lifetime = 0.25f)
        => Spawn(BuildJaggedPath(fromQuake, toQuake), color ?? DefaultArcColor, width, lifetime, flicker: true);

    /// <summary>Spawn a straight steady beam (a rail/lightning trunk) between two Quake-space points.</summary>
    public Node3D? Beam(NVec3 fromQuake, NVec3 toQuake, Color? color = null, float width = 6f, float lifetime = 0.18f)
    {
        var path = new List<NVec3> { fromQuake, toQuake };
        return Spawn(path, color ?? DefaultArcColor, width, lifetime, flicker: false);
    }

    // =================================================================================================
    //  Jagged path (faithful port of cl_effects_lightningarc)
    // =================================================================================================

    private List<NVec3> BuildJaggedPath(NVec3 from, NVec3 to)
    {
        var pts = new List<NVec3> { from };
        float length = (to - from).Length();
        if (length < 1f)
            return pts;

        // Use at most 16 segments (QC beam-list cap).
        int steps = Math.Min(16, (int)MathF.Floor(length / MathF.Max(1f, SegmentLength)));
        if (steps < 1)
        {
            pts.Add(to);
            return pts;
        }

        float steplength = length / steps;
        NVec3 direction = NVec3.Normalize(to - from);
        NVec3 posL = from;

        if (length > SegmentLength)
        {
            for (int i = 1; i < steps; i++)
            {
                float t = (float)i / steps;
                float drift = DriftStart * (1f - t) + DriftEnd * t;
                NVec3 dirnew = SafeNormalize(direction * (1f - drift) + RandomVec() * drift);
                NVec3 pos = posL + dirnew * steplength;
                pts.Add(pos);
                posL = pos;
            }
        }
        pts.Add(to); // final segment snaps onto the target
        return pts;
    }

    private static NVec3 RandomVec()
    {
        // QC randomvec(): a vector with each component in [-1, 1].
        return new NVec3(
            (float)(Rng.NextDouble() * 2.0 - 1.0),
            (float)(Rng.NextDouble() * 2.0 - 1.0),
            (float)(Rng.NextDouble() * 2.0 - 1.0));
    }

    private static NVec3 SafeNormalize(NVec3 v)
        => v.LengthSquared() > 1e-8f ? NVec3.Normalize(v) : new NVec3(0f, 0f, 1f);

    // =================================================================================================
    //  Mesh build + lifetime
    // =================================================================================================

    private Node3D? Spawn(List<NVec3> pathQuake, Color color, float width, float lifetime, bool flicker)
    {
        if (pathQuake.Count < 2)
            return null;

        var mesh = CylindricLine.BuildCrossRibbonMesh(pathQuake, width);
        var mat = BeamMaterial(color);
        var mi = new MeshInstance3D
        {
            Name = "Beam",
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mi);

        AnimateAndFree(mi, mat, lifetime, flicker);
        return mi;
    }

    private StandardMaterial3D BeamMaterial(Color color)
    {
        _beamTex ??= BuildGlowTexture();
        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = color,
            AlbedoTexture = _beamTex,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            DisableReceiveShadows = true,
        };
    }

    /// <summary>A width gradient: opaque white core at the center (u=0.5), transparent at the edges.</summary>
    private static ImageTexture BuildGlowTexture()
    {
        const int w = 16, h = 4;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int x = 0; x < w; x++)
        {
            float u = x / (float)(w - 1);
            float edge = 1f - MathF.Abs(u - 0.5f) * 2f; // 0 at edges, 1 at center
            edge = edge * edge;                          // sharpen the core
            for (int y = 0; y < h; y++)
                img.SetPixel(x, y, new Color(1f, 1f, 1f, edge));
        }
        return ImageTexture.CreateFromImage(img);
    }

    private void AnimateAndFree(MeshInstance3D mi, StandardMaterial3D mat, float lifetime, bool flicker)
    {
        SceneTree? tree = IsInsideTree() ? GetTree() : mi.GetTree();
        if (tree is null)
        {
            mi.QueueFree();
            return;
        }

        // A short tween: bolts flicker (random alpha) then fade out over the lifetime.
        Tween tween = mi.CreateTween();
        if (flicker)
        {
            int flickers = 3;
            float step = lifetime / (flickers + 1);
            for (int i = 0; i < flickers; i++)
            {
                float a = 0.6f + (float)Rng.NextDouble() * 0.4f;
                tween.TweenProperty(mat, "albedo_color:a", a, step);
            }
            tween.TweenProperty(mat, "albedo_color:a", 0f, step);
        }
        else
        {
            tween.TweenProperty(mat, "albedo_color:a", 0f, lifetime);
        }
        tween.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mi)) mi.QueueFree(); }));
    }
}
