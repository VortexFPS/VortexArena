using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Ejected shell casings — the small brass/shell meshes a weapon throws on each shot. The C# successor to
/// CSQC's casings.qc (the <c>casings</c> net temp-entity + Casing_Draw): MDL_CASING_BULLET
/// (models/casing_bronze.iqm) for bullet weapons, MDL_CASING_SHELL (models/casing_shell.mdl) for the
/// shotgun. A casing is a bouncing rigid body (MOVETYPE_BOUNCE, gravity 1, an avelocity tumble) that lives
/// for cl_casings_*_time seconds.
///
/// (zero-hitch 2026-08-03) STRUCT-ARRAY SIM + MULTIMESH RENDER. The previous shape was one
/// <c>CasingBody : Node3D</c> per casing — at the 100 cap that is 100 scene nodes each with its own
/// <c>_PhysicsProcess</c> native callback, 100 render objects, 100 draw calls, and a spawn/QueueFree churn
/// burst on every firefight (the release captures' draw-count spikes + unscoped/post-process hitch class).
/// Now one struct pool advances fixed client tics in the render callback and presents the residual motion
/// through two fixed-capacity <see cref="MultiMesh"/> batches. Committed tics own collision and sounds.
///
/// Opaque and fading instances use separate batches so the authored opaque materials keep their depth behavior.
/// </summary>
public sealed partial class ShellCasings : Node3D
{
    /// <summary>Casing kind, matching the QC <c>casing.state</c> switch (1 = shotgun shell, else bullet).</summary>
    public enum CasingKind { Bullet = 0, Shell = 1 }

    /// <summary>Bullet-casing lifetime seconds (DP cl_casings_bronze_time default).</summary>
    [Export] public float BulletTime { get; set; } = 10f;

    /// <summary>Shell-casing lifetime seconds (DP cl_casings_shell_time default).</summary>
    [Export] public float ShellTime { get; set; } = 30f;

    /// <summary>Fallback live casing limit when no client cvar is available.</summary>
    [Export] public int MaxCasings { get; set; } = 100;

    internal ICvarService? Cvars { get; init; }

    /// <summary>
    /// Positional bounce-sound hook (host-set to <see cref="ClientWorld.OnSound"/>): plays the casing-impact
    /// samples (<c>brass1-3</c> / <c>casings1-3</c>) on touch, faithful to Base <c>Casing_Touch</c> (casings.qc).
    /// Signature: (sample, originQuake). When unset, casings bounce silently.
    /// </summary>
    public Action<string, NVec3>? SoundHook { get; set; }

    /// <summary>
    /// Result of a casing world-collision sweep (Base <c>Movetype_Physics_MatchTicrate</c> MOVETYPE_BOUNCE
    /// trace over the static map BSP). <see cref="Fraction"/> 1 = no hit; <see cref="Normal"/> is the impact
    /// plane normal (Quake space); <see cref="StartSolid"/> = the casing started inside a brush (gun poking
    /// into a wall — Base deletes those, mirroring <c>Casing_Draw</c>'s <c>trace_startsolid</c> cull).
    /// </summary>
    public readonly record struct CasingTrace(float Fraction, NVec3 EndPos, NVec3 Normal, bool StartSolid);

    /// <summary>Optional point sweep override in Quake space. Without a tracer, use the spawn's floor plane.</summary>
    public Func<NVec3, NVec3, CasingTrace>? TraceHook
    {
        get => _traceHook;
        set
        {
            _traceHook = value;
            _traceOverride = value is null ? null : (from, to, _) =>
            {
                CasingTrace hit = value(from, to);
                return new TraceResult
                {
                    Fraction = hit.Fraction, EndPos = hit.Fraction < 1f ? hit.EndPos : to,
                    PlaneNormal = hit.Normal, StartSolid = hit.StartSolid,
                };
            };
        }
    }

    private Func<NVec3, NVec3, CasingTrace>? _traceHook;
    private DebrisTrace? _traceOverride;
    internal DebrisTrace? WorldTrace { get; set; }

    /// <summary>
    /// Optional host-supplied model loader (e.g. <c>AssetLoader.LoadModel</c>): given a virtual model path
    /// returns a fresh Godot node, or null on miss. When unset, casings render as a tiny generated cylinder.
    /// </summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }

    private const float MinVisibleAlpha = 0.003f;
    private static readonly Regex FragmentTokens = new(
        @"//[^\r\n]*|/\*[\s\S]*?\*/|\bvoid\s+fragment\s*\(\s*\)\s*\{|\bALPHA\b|[{}]",
        RegexOptions.CultureInvariant);

    /// <summary>One simulated casing (pure data — no node, no allocation).</summary>
    private struct Casing
    {
        public CasingKind Kind;
        public DebrisMotion Motion;
        public float BounceFactor, Lifetime, FloorZ;
        public double Age, NextSoundAt;
    }

    private readonly RubblePool<Casing> _pool = new();

    // An opaque and a fading MultiMesh per kind. Each buffer can hold the shared pool's full live cap
    // without resizing. A casing occupies one slot in one batch, and VisibleInstanceCount limits draw work.
    private sealed class Batch
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mesh = null!;
        public Transform3D BaseXform;   // the source model's own node transform, folded into every instance
        public int Visible;
    }

    private readonly Dictionary<Material, Material> _fadeMaterials = new();
    private readonly Batch?[] _batches = new Batch?[4];   // opaque kinds, then fading kinds

    /// <summary>
    /// Eject a casing from <paramref name="origin"/> (Quake space) with initial <paramref name="velocity"/>
    /// (Quake space). The casing tumbles, falls under gravity, bounces off world faces (or the ground plane at
    /// <paramref name="floorZ"/>), and fades at end of life. Returns this node (legacy signature —
    /// casings no longer have per-spawn nodes).
    /// </summary>
    public Node3D Spawn(NVec3 origin, NVec3 velocity, CasingKind kind = CasingKind.Bullet, float floorZ = float.NegativeInfinity)
    {
        int limit = RubblePool<Casing>.ReadLimit(Cvars, "cl_casings_maxcount", MaxCasings);
        if (!_pool.TrySpawn(limit, out int slot))
            return this;
        EnsureBatches(kind);

        // QC adds a little velocity jitter on receipt: casing.velocity += 2 * prandomvec().
        ref Casing c = ref _pool[slot];
        c = new Casing
        {
            Kind = kind,
            Motion = new DebrisMotion(origin, velocity + RandomVec() * 2f,
                new NVec3(0f, 10f, 0f) + RandomVec() * 100f),
            BounceFactor = kind == CasingKind.Shell ? 0.25f : 0.5f,
            Lifetime = ReadLifetime(kind),
            FloorZ = floorZ,
        };
        return this;
    }

    private float ReadLifetime(CasingKind kind)
    {
        bool shell = kind == CasingKind.Shell;
        string name = shell ? "cl_casings_shell_time" : "cl_casings_bronze_time";
        return Cvars?.Has(name) == true ? Cvars.GetFloat(name) : shell ? ShellTime : BulletTime;
    }

    public override void _Process(double delta)
    {
        double dt = delta * ClientRenderTime.Scale;
        if (dt <= 0.0)
            return;

        if (_pool.IsEmpty)
        {
            for (int k = 0; k < _batches.Length; k++)
                if (_batches[k] is { } idle && idle.Visible != 0) { idle.Mesh.VisibleInstanceCount = 0; idle.Visible = 0; }
            return;
        }

        using var _scope = FrameProfiler.Scope("casings"); // house rule: per-frame node → scoped

        DebrisTiming timing = DebrisTiming.Read(Cvars, "cl_casings_ticrate", "cl_casings_sloppy", 0.03125f);
        DebrisTrace? trace = _traceOverride ?? WorldTrace;
        Span<int> counts = stackalloc int[4];
        for (int i = 0; i < _pool.Length; i++)
        {
            if (!_pool.IsActive(i))
                continue;

            ref Casing c = ref _pool[i];
            c.Age += dt;
            float alpha = Math.Clamp((float)(c.Lifetime - c.Age), 0f, 1f);
            if (!(alpha >= MinVisibleAlpha)) { _pool.Remove(i); continue; }

            int steps = c.Motion.Schedule(dt, timing.Ticrate, out double step, out double remainder);
            for (int j = 0; j < steps && !c.Motion.Resting; j++)
            {
                bool survived = c.Motion.Step((float)step, timing.Gravity, c.BounceFactor, c.FloorZ,
                    NVec3.Zero, trace, removeOnTouch: false, removeOnNoImpact: true, removeInSolid: true, out var impact);
                if (impact is { } touch)
                    BounceCasingSound(ref c, touch);
                if (!survived)
                {
                    _pool.Remove(i);
                    break;
                }
            }
            if (!_pool.IsActive(i))
                continue;

            bool fading = alpha < 1f;
            int batchIndex = (int)c.Kind + (fading ? 2 : 0);
            Batch b = _batches[batchIndex]!;
            Transform3D pose = c.Motion.Draw((float)remainder, timing.Gravity, timing.Sloppy,
                c.FloorZ, NVec3.Zero, trace, flattenOnGround: true, sink: 0f, out bool startSolid);
            if (startSolid) { _pool.Remove(i); continue; }
            int instance = counts[batchIndex]++;
            b.Mesh.SetInstanceTransform(instance, pose * b.BaseXform);
            if (fading)
                b.Mesh.SetInstanceColor(instance, new Color(1f, 1f, 1f, alpha));
        }

        for (int k = 0; k < _batches.Length; k++)
        {
            if (_batches[k] is not { } bb)
                continue;
            int want = counts[k];
            if (bb.Visible != want) { bb.Mesh.VisibleInstanceCount = want; bb.Visible = want; }
        }
    }

    // QC touch reads the client draw time, including during all catch-up tics in this draw.
    private void BounceCasingSound(ref Casing c, DebrisImpact impact)
    {
        bool ready = c.Age >= c.NextSoundAt;
        c.NextSoundAt = c.Age + 0.2;
        if (ready && impact.Speed > 50f)
            SoundHook?.Invoke(RandomImpactSound(c.Kind), impact.Origin);
    }

    private static string RandomImpactSound(CasingKind kind)
    {
        int n = GD.RandRange(1, 3);
        return kind == CasingKind.Shell ? $"weapons/casings{n}.wav" : $"weapons/brass{n}.wav";
    }

    // ------------------------------------------------------------------------------------------------
    //  Batches + meshes
    // ------------------------------------------------------------------------------------------------

    private void EnsureBatches(CasingKind kind)
    {
        int index = (int)kind;
        if (_batches[index] is not null)
            return;

        var (mesh, material, pose) = ResolveMesh(kind);
        Batch opaque = CreateBatch(kind, mesh, material, pose, fading: false, RubblePool<Casing>.Capacity);
        Batch fade = CreateBatch(kind, mesh, material, pose, fading: true, RubblePool<Casing>.Capacity);
        _batches[index] = opaque;
        _batches[index + 2] = fade;
        AddChild(opaque.Node);
        AddChild(fade.Node);
    }

    private Batch CreateBatch(CasingKind kind, Mesh mesh, Material? material, Transform3D pose,
        bool fading, int capacity)
    {
        if (fading)
        {
            if (material is not null)
                material = FadeMaterial(material);
            else
            {
                mesh = (Mesh)mesh.Duplicate();
                for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                    mesh.SurfaceSetMaterial(i, FadeMaterial(mesh.SurfaceGetMaterial(i)));
            }
        }
        var instances = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = fading,
            Mesh = mesh,
            InstanceCount = capacity,
            VisibleInstanceCount = 0,
        };
        string name = kind == CasingKind.Shell ? "shell_casings" : "bullet_casings";
        var node = new MultiMeshInstance3D
        {
            Name = fading ? name + "_fade" : name,
            Multimesh = instances,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            ExtraCullMargin = 16384f,
        };
        return new Batch { Node = node, Mesh = instances, BaseXform = pose };
    }

    private Material FadeMaterial(Material? material)
    {
        if (material is null)
            return new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                VertexColorUseAsAlbedo = true,
            };
        if (_fadeMaterials.TryGetValue(material, out Material? fade))
            return fade;
        fade = CreateFadeMaterial(material);
        _fadeMaterials.Add(material, fade);
        return fade;
    }

    private Material CreateFadeMaterial(Material material)
    {
        if (material is ShaderMaterial { Shader: { } shader } source && FadeShader(shader) is { } variant)
        {
            var copy = (ShaderMaterial)source.Duplicate();
            copy.Shader = variant;
            foreach (var uniform in shader.GetShaderUniformList())
            {
                StringName name = uniform.AsGodotDictionary()["name"].AsStringName();
                Variant value = source.GetShaderParameter(name);
                if (value.VariantType != Variant.Type.Nil)
                    copy.SetShaderParameter(name, value);
            }
            copy.NextPass = source.NextPass is { } nextPass ? FadeMaterial(nextPass) : null;
            return copy;
        }

        if (material is not BaseMaterial3D original)
            return material;

        var standard = (BaseMaterial3D)original.Duplicate();
        standard.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        standard.VertexColorUseAsAlbedo = true;
        standard.NextPass = material.NextPass is { } next ? FadeMaterial(next) : null;
        return standard;
    }

    private static Shader? FadeShader(Shader source)
    {
        string code = source.Code;
        int depth = 0;
        bool writesAlpha = false;
        foreach (Match token in FragmentTokens.Matches(code))
        {
            if (token.Value[0] == '/') continue;
            if (depth == 0)
            {
                if (token.Value.StartsWith("void", StringComparison.Ordinal))
                    depth = 1;
            }
            else if (token.Value == "{") depth++;
            else if (token.Value == "ALPHA") writesAlpha = true;
            else if (token.Value == "}" && --depth == 0)
            {
                string fade = writesAlpha ? "\nALPHA *= COLOR.a;\n" : "\nALPHA = COLOR.a;\n";
                return new Shader { Code = code.Insert(token.Index, fade) };
            }
        }
        return null;
    }

    /// <summary>The casing mesh + material + the source node's own transform (folded into every instance).
    /// Prefers the real model (IQM brass / MDL shell) via <see cref="SharedMeshCache"/>; generated cylinder
    /// fallback when the loader is unwired or the model can't be parsed.</summary>
    private (Mesh, Material?, Transform3D) ResolveMesh(CasingKind kind)
    {
        string vpath = kind == CasingKind.Shell ? "models/casing_shell.mdl" : "models/casing_bronze.iqm";
        if (ModelLoader is not null && SharedMeshCache.Instantiate(vpath, () => ModelLoader(vpath)) is { } mi)
        {
            Material? mat = mi.MaterialOverride;
            if (mat is null && mi.GetSurfaceOverrideMaterialCount() > 0)
                mat = mi.GetSurfaceOverrideMaterial(0);
            Mesh m = mi.Mesh;
            Transform3D xf = mi.Transform;
            mi.QueueFree();   // only needed its resolved (mesh, material, transform)
            return (m, mat, xf);
        }
        bool shell = kind == CasingKind.Shell;
        return (shell ? _shellMesh ??= BuildCasingMesh(true) : _bulletMesh ??= BuildCasingMesh(false),
                null,
                new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(90f)), Vector3.Zero));
    }

    private static CylinderMesh? _shellMesh;
    private static CylinderMesh? _bulletMesh;

    private static CylinderMesh BuildCasingMesh(bool shell)
    {
        Color brass = shell ? new Color(0.7f, 0.15f, 0.12f) : new Color(0.78f, 0.62f, 0.25f);
        return new CylinderMesh
        {
            TopRadius = shell ? 0.9f : 0.5f,
            BottomRadius = shell ? 0.9f : 0.5f,
            Height = shell ? 3.0f : 1.6f,
            RadialSegments = 6,
            Rings = 0,
            Material = new StandardMaterial3D
            {
                AlbedoColor = brass,
                Metallic = 0.8f,
                Roughness = 0.35f,
            },
        };
    }

    /// <summary>
    /// One opaque and one fading instance per casing kind for the offscreen GPU pipeline warm pass — as MULTIMESH
    /// instances, because instanced rendering is its own vertex format and therefore its own pipeline: warming
    /// a plain MeshInstance3D would leave the live MultiMesh's pipeline cold. Same meshes/materials the live
    /// batches resolve. The warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        var list = new List<Node3D>(4);
        foreach (CasingKind kind in new[] { CasingKind.Bullet, CasingKind.Shell })
        {
            var (mesh, material, pose) = ResolveMesh(kind);
            Batch opaque = CreateBatch(kind, mesh, material, pose, fading: false, 1);
            Batch fade = CreateBatch(kind, mesh, material, pose, fading: true, 1);
            opaque.Mesh.SetInstanceTransform(0, pose);
            opaque.Mesh.VisibleInstanceCount = 1;
            fade.Mesh.SetInstanceTransform(0, pose);
            fade.Mesh.SetInstanceColor(0, new Color(1f, 1f, 1f, 0.5f));
            fade.Mesh.VisibleInstanceCount = 1;
            list.Add(opaque.Node);
            list.Add(fade.Node);
        }
        return list;
    }

    private static NVec3 RandomVec()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));
}
